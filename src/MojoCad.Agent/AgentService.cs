using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MojoCad.Agent.Models;
using MojoCad.Agent.OpenRouter;
using MojoCad.Agent.Tools;
using MojoCad.Core.Agent;
using MojoCad.Core.Changes;
using MojoCad.Core.Ports;
using MojoCad.Core.Settings;

namespace MojoCad.Agent
{
    /// <summary>
    /// The agentic loop. For each user turn it grounds itself in the live drawing, streams the model,
    /// executes tool calls in order (parallel_tool_calls is off), stages a reviewable change set, and
    /// streams progress to the observer. It never applies anything to the drawing - it only produces a
    /// <see cref="ChangeSet"/> for the UI to preview and the engineer to accept.
    /// </summary>
    public sealed class AgentService : IAgentService
    {
        private const int MaxToolIterations = 16;
        private const int MaxTransientRetries = 3;

        private readonly IOpenRouterClient _client;
        private readonly IAcadBridge _bridge;
        private readonly ISecureStore _secureStore;

        // Two cached tool surfaces: with and without the opt-in run_command power tool.
        private readonly List<ToolDef> _toolsBase;
        private readonly List<ToolDef> _toolsWithCommand;

        // Conversation history (user/assistant/tool messages). The system prompt is rebuilt fresh each turn.
        private readonly List<ChatMessage> _history = new List<ChatMessage>();

        public AgentService(IOpenRouterClient client, IAcadBridge bridge, ISecureStore secureStore)
        {
            _client = client;
            _bridge = bridge;
            _secureStore = secureStore;
            _toolsBase = ToolRegistry.BuildAll(includeCommandTool: false);
            _toolsWithCommand = ToolRegistry.BuildAll(includeCommandTool: true);
        }

        public void ResetConversation() => _history.Clear();

        public string ExportTranscript() => JsonSerializer.Serialize(_history, WireJson.Options);

        public void ImportTranscript(string serialized)
        {
            _history.Clear();
            try
            {
                var msgs = JsonSerializer.Deserialize<List<ChatMessage>>(serialized, WireJson.Options);
                if (msgs != null) _history.AddRange(msgs);
            }
            catch { /* ignore a corrupt transcript */ }
        }

        public async Task RunTurnAsync(string userMessage, AgentTurnContext context, IAgentObserver observer, CancellationToken cancellationToken)
        {
            string? apiKey = _secureStore.LoadApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                observer.OnError(new AgentError
                {
                    Kind = AgentErrorKind.Auth,
                    Message = "No OpenRouter API key is configured.",
                    Remedy = "Open Settings and paste your OpenRouter API key.",
                    Retryable = false
                });
                return;
            }

            _history.Add(ChatMessage.User(userMessage));

            var totalUsage = new TurnUsage { Model = context.Models.Model };
            var builder = new ChangeSetBuilder(Guid.NewGuid().ToString("N"));
            var executor = new ToolExecutor(_bridge, builder);

            try
            {
                observer.OnStatus("Reading drawing");
                var drawing = _bridge.HasActiveDocument
                    ? await _bridge.GetDrawingSummaryAsync().ConfigureAwait(false)
                    : new Core.Drawing.DrawingSummary();

                string systemPrompt = SystemPromptBuilder.Build(context.Standards, drawing, context.AttachedHandles,
                    context.RequirePlanApproval || context.Standards.Discipline == DisciplinePreset.FireAndLifeSafety,
                    context.EnableCommandExecution);

                for (int iteration = 0; iteration < MaxToolIterations; iteration++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    observer.OnStatus(iteration == 0 ? "Thinking" : "Continuing");

                    var request = BuildRequest(systemPrompt, context);
                    ChatStreamResult result = await CallModelWithRetryAsync(request, apiKey!, observer, cancellationToken).ConfigureAwait(false);

                    Accumulate(totalUsage, result.Usage);

                    var assistant = result.AssistantMessage;
                    _history.Add(assistant);

                    if (!string.IsNullOrWhiteSpace(assistant.Content))
                        observer.OnAssistantText(assistant.Content!);

                    var toolCalls = assistant.ToolCalls;
                    if (toolCalls == null || toolCalls.Count == 0)
                        break; // assistant finished prose-only; turn complete.

                    bool stopTurn = false;
                    foreach (var call in toolCalls)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        observer.OnToolActivity(new ToolActivityInfo
                        {
                            ToolName = call.Function.Name,
                            CallId = call.Id,
                            State = ToolActivityState.Started,
                            Display = HumanizeStart(call.Function.Name)
                        });

                        ToolOutcome outcome = await executor.ExecuteAsync(call.Function.Name, call.Function.Arguments, cancellationToken).ConfigureAwait(false);

                        observer.OnToolActivity(new ToolActivityInfo
                        {
                            ToolName = call.Function.Name,
                            CallId = call.Id,
                            State = outcome.Failed ? ToolActivityState.Failed : ToolActivityState.Succeeded,
                            Display = outcome.Display,
                            Detail = outcome.Failed ? outcome.ResultJson : null
                        });

                        _history.Add(ChatMessage.Tool(call.Id, outcome.ResultJson));

                        // Control signals.
                        if (outcome.Plan != null)
                        {
                            bool requireApproval = context.RequirePlanApproval || context.Standards.Discipline == DisciplinePreset.FireAndLifeSafety;
                            outcome.Plan.RequiresApproval = requireApproval;
                            observer.OnPlan(outcome.Plan);
                            if (requireApproval) stopTurn = true; // wait for the engineer to approve.
                        }
                        if (outcome.Question != null)
                        {
                            observer.OnQuestion(outcome.Question);
                            stopTurn = true;
                        }
                        if (outcome.EmitChangeset)
                        {
                            var changeSet = builder.Finalize(outcome.ChangeSummary ?? "Proposed changes");
                            changeSet.OriginMessageId = call.Id;
                            observer.OnChangeSetReady(changeSet);
                            stopTurn = true;
                        }
                    }

                    if (stopTurn) break;
                }
            }
            catch (OperationCanceledException)
            {
                observer.OnError(new AgentError { Kind = AgentErrorKind.Cancelled, Message = "Stopped.", Retryable = true });
                return;
            }
            catch (OpenRouterException ex)
            {
                observer.OnError(MapError(ex));
                return;
            }
            catch (Exception ex)
            {
                observer.OnError(new AgentError { Kind = AgentErrorKind.Unknown, Message = ex.Message, Retryable = false });
                return;
            }

            observer.OnTurnComplete(totalUsage);
        }

        // ----- model call with backoff -----------------------------------------------------------

        private async Task<ChatStreamResult> CallModelWithRetryAsync(ChatRequest request, string apiKey, IAgentObserver observer, CancellationToken ct)
        {
            int attempt = 0;
            while (true)
            {
                try
                {
                    return await _client.StreamChatAsync(request, apiKey, observer.OnAssistantTextDelta, ct).ConfigureAwait(false);
                }
                catch (OpenRouterException ex) when (IsRetryable(ex.Kind) && attempt < MaxTransientRetries)
                {
                    attempt++;
                    TimeSpan delay = ex.RetryAfter ?? TimeSpan.FromSeconds(Math.Pow(2, attempt)); // 2s, 4s, 8s
                    observer.OnStatus($"Provider busy - retrying in {delay.TotalSeconds:0}s ({attempt}/{MaxTransientRetries})");
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
            }
        }

        private static bool IsRetryable(AgentErrorKind kind) =>
            kind == AgentErrorKind.RateLimited || kind == AgentErrorKind.Provider || kind == AgentErrorKind.Network;

        // ----- request building ------------------------------------------------------------------

        private ChatRequest BuildRequest(string systemPrompt, AgentTurnContext context)
        {
            var messages = new List<ChatMessage>(_history.Count + 1) { ChatMessage.System(systemPrompt) };
            messages.AddRange(_history);

            var fallbacks = context.Models.FallbackModels?
                .Where(m => !string.Equals(m, context.Models.Model, StringComparison.OrdinalIgnoreCase))
                .ToList();

            return new ChatRequest
            {
                Model = context.Models.Model,
                // OpenRouter fallback semantics: `model` is primary, `models` is the ordered fallback
                // list (excluding the primary). If the primary errors/refuses, the router tries these.
                Models = fallbacks != null && fallbacks.Count > 0 ? fallbacks : null,
                Messages = messages,
                Tools = context.EnableCommandExecution ? _toolsWithCommand : _toolsBase,
                ToolChoice = "auto",
                ParallelToolCalls = false,
                Temperature = context.Models.Temperature,
                MaxTokens = context.Models.MaxTokens,
                Stream = true,
                Provider = new ProviderPrefs
                {
                    RequireParameters = true,
                    DataCollection = context.Models.DenyDataCollection ? "deny" : null
                },
                Usage = new UsageRequest { Include = true },
                Reasoning = string.IsNullOrWhiteSpace(context.Models.ReasoningEffort)
                    ? null
                    : new ReasoningPrefs { Effort = context.Models.ReasoningEffort }
            };
        }

        private static void Accumulate(TurnUsage total, UsageInfo? usage)
        {
            if (usage == null) return;
            total.PromptTokens += usage.PromptTokens;
            total.CompletionTokens += usage.CompletionTokens;
            if (usage.Cost.HasValue) total.CostUsd = (total.CostUsd ?? 0) + usage.Cost.Value;
        }

        private static AgentError MapError(OpenRouterException ex)
        {
            string? remedy = ex.Kind switch
            {
                AgentErrorKind.Auth => "Open Settings and re-check your OpenRouter API key.",
                AgentErrorKind.InsufficientCredits => "Top up your OpenRouter credits, then retry.",
                AgentErrorKind.Moderation => "Rephrase the request.",
                _ => null
            };
            return new AgentError
            {
                Kind = ex.Kind,
                Message = ex.Message,
                Remedy = remedy,
                Retryable = IsRetryable(ex.Kind)
            };
        }

        private static string HumanizeStart(string toolName) => toolName switch
        {
            ToolNames.GetDrawingSummary => "Reading the drawing",
            ToolNames.QueryEntities => "Querying entities",
            ToolNames.GetEntityProperties => "Inspecting entities",
            ToolNames.Measure => "Measuring",
            ToolNames.EmitChangeset => "Preparing the change set",
            ToolNames.PresentPlan => "Drafting a plan",
            ToolNames.AskClarification => "Asking a question",
            _ => "Working: " + toolName
        };
    }
}
