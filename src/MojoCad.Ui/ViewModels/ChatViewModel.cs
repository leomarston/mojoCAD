using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MojoCad.Core.Agent;
using MojoCad.Core.Changes;
using MojoCad.Core.Composition;
using MojoCad.Core.Ports;
using MojoCad.Core.Settings;

namespace MojoCad.Ui.ViewModels
{
    /// <summary>
    /// The brain of the chat palette. It owns the transcript, drives one turn at a time through
    /// <see cref="IAgentService"/>, and implements <see cref="IAgentObserver"/> to fold the streamed
    /// callbacks into view-models. Because observer callbacks arrive on a BACKGROUND thread, every one of
    /// them marshals onto the UI dispatcher before touching <see cref="Messages"/> or any bound property.
    /// It also implements <see cref="IReviewHost"/> so review cards can run the apply/preview/undo flow
    /// without holding a service reference of their own.
    /// </summary>
    public sealed partial class ChatViewModel : ObservableObject, IAgentObserver, IReviewHost
    {
        private readonly MojoServices _services;
        private readonly Dispatcher _dispatcher;
        private readonly Action _openSettings;

        private CancellationTokenSource? _turnCts;

        // The assistant bubble currently being streamed into (created lazily on the first text delta).
        private AssistantMessageViewModel? _streamingBubble;

        // The last user prompt, kept so the Retry affordance can resend the exact same turn.
        private string? _lastUserPrompt;

        // The most recent change set the agent staged - the live preview belongs to it.
        private ChangeSet? _activeChangeSet;

        public ChatViewModel(MojoServices services, Dispatcher dispatcher, Action openSettings)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _openSettings = openSettings ?? (() => { });

            _settings = _services.Settings.Load();
            CurrentModelId = _settings.Models.Model;
            UseSelectionContext = false;

            ShowOnboardingIfNeeded();
        }

        // ----- bound state ----------------------------------------------------------------------

        public ObservableCollection<MessageViewModel> Messages { get; } = new();

        private MojoSettings _settings;

        [ObservableProperty]
        private string _inputText = string.Empty;

        [ObservableProperty]
        private bool _isBusy;

        [ObservableProperty]
        private string _statusText = "Ready";

        [ObservableProperty]
        private string _currentModelId = string.Empty;

        /// <summary>Footer usage line, e.g. "1,204 in / 318 out · $0.0042".</summary>
        [ObservableProperty]
        private string _usageText = string.Empty;

        [ObservableProperty]
        private ConnectionState _connectionState = ConnectionState.Idle;

        /// <summary>Whether the user has toggled the "current selection" context chip on for the next turn.</summary>
        [ObservableProperty]
        private bool _useSelectionContext;

        /// <summary>A transient error line shown above the input (cleared on the next send).</summary>
        [ObservableProperty]
        private string? _inlineError;

        partial void OnIsBusyChanged(bool value)
        {
            SendCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
            NewChatCommand.NotifyCanExecuteChanged();
        }

        // ----- commands -------------------------------------------------------------------------

        private bool CanSend() => !IsBusy && !string.IsNullOrWhiteSpace(InputText);

        [RelayCommand(CanExecute = nameof(CanSend))]
        private void Send()
        {
            var text = InputText.Trim();
            if (string.IsNullOrEmpty(text)) return;
            InputText = string.Empty;
            StartTurn(text, isFollowUp: false);
        }

        private bool CanStop() => IsBusy;

        [RelayCommand(CanExecute = nameof(CanStop))]
        private void Stop()
        {
            _turnCts?.Cancel();
            StatusText = "Stopping…";
        }

        private bool CanNewChat() => !IsBusy;

        [RelayCommand(CanExecute = nameof(CanNewChat))]
        private async Task NewChat()
        {
            _services.Agent.ResetConversation();
            Messages.Clear();
            _streamingBubble = null;
            _activeChangeSet = null;
            _lastUserPrompt = null;
            UsageText = string.Empty;
            InlineError = null;
            StatusText = "Ready";
            ConnectionState = ConnectionState.Idle;
            try { await _services.Previewer.ClearAsync(); } catch { }
            ShowOnboardingIfNeeded();
        }

        [RelayCommand]
        private void OpenSettings() => _openSettings();

        [RelayCommand]
        private void ToggleSelectionContext() => UseSelectionContext = !UseSelectionContext;

        // ----- turn orchestration ---------------------------------------------------------------

        /// <summary>
        /// Append the user bubble, build the per-turn context from current settings, and kick the agent off
        /// on a background task. <paramref name="isFollowUp"/> turns (plan approval, question answers) don't
        /// re-add a bubble for an empty prompt path but otherwise behave identically.
        /// </summary>
        private void StartTurn(string text, bool isFollowUp)
        {
            if (IsBusy) return;
            InlineError = null;

            // Re-read settings each turn so changes made in the Settings panel take effect immediately.
            _settings = _services.Settings.Load();
            CurrentModelId = _settings.Models.Model;

            var userMsg = new UserMessageViewModel(text);
            if (UseSelectionContext)
                userMsg.Attachments.Add("Current selection");
            Messages.Add(userMsg);
            _lastUserPrompt = text;

            _streamingBubble = null;
            IsBusy = true;
            ConnectionState = ConnectionState.Working;
            StatusText = "Thinking…";

            _turnCts = new CancellationTokenSource();
            var ct = _turnCts.Token;

            // Run the (potentially long) turn off the UI thread; observer callbacks marshal back.
            _ = Task.Run(async () =>
            {
                try
                {
                    var ctx = await BuildContextAsync(ct).ConfigureAwait(false);
                    await _services.Agent.RunTurnAsync(text, ctx, this, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Post(() =>
                    {
                        StatusText = "Stopped";
                        IsBusy = false;
                        ConnectionState = ConnectionState.Idle;
                    });
                }
                catch (Exception ex)
                {
                    // A throw that escaped the agent's own error path - surface it rather than hang the UI.
                    Post(() =>
                    {
                        Messages.Add(new ErrorMessageViewModel(
                            new AgentError { Kind = AgentErrorKind.Unknown, Message = ex.Message, Retryable = true },
                            RetryLastCommand, OpenSettingsCommand));
                        StatusText = "Error";
                        IsBusy = false;
                        ConnectionState = ConnectionState.Error;
                    });
                }
            });
        }

        /// <summary>Build the per-turn context. Selection handles are fetched only when the chip is toggled on.</summary>
        private async Task<AgentTurnContext> BuildContextAsync(CancellationToken ct)
        {
            var ctx = new AgentTurnContext
            {
                Models = _settings.Models,
                Standards = _settings.Standards,
                RequirePlanApproval = _settings.RequirePlanApproval
                                      || _settings.Standards.Discipline == DisciplinePreset.FireAndLifeSafety,
                EnableCommandExecution = _settings.EnableCommandExecution
            };

            if (UseSelectionContext)
            {
                try
                {
                    var handles = await _services.Bridge.GetCurrentSelectionAsync().ConfigureAwait(false);
                    ctx.AttachedHandles = handles.ToList();
                }
                catch
                {
                    // A failed selection read shouldn't abort the turn; just send without the chip.
                }
            }

            return ctx;
        }

        /// <summary>Send a follow-up turn (plan approval / question answer) as if the user typed it.</summary>
        private void SendFollowUp(string text)
        {
            if (IsBusy) return;
            StartTurn(text, isFollowUp: true);
        }

        [RelayCommand]
        private void RetryLast()
        {
            if (IsBusy || string.IsNullOrWhiteSpace(_lastUserPrompt)) return;
            StartTurn(_lastUserPrompt!, isFollowUp: true);
        }

        // ----- IAgentObserver (ALL marshalled to the dispatcher) --------------------------------

        public void OnStatus(string status) => Post(() => StatusText = status);

        public void OnAssistantTextDelta(string delta) => Post(() =>
        {
            // Create the assistant bubble lazily on the first token so empty steps don't leave blanks.
            _streamingBubble ??= AddAssistantBubble();
            _streamingBubble.AppendDelta(delta);
        });

        public void OnAssistantText(string markdown) => Post(() =>
        {
            var bubble = _streamingBubble ?? AddAssistantBubble();
            bubble.Finalize(markdown);
            _streamingBubble = null; // the next prose starts a fresh bubble
        });

        public void OnPlan(PlanInfo plan) => Post(() =>
        {
            // Approving sends a follow-up turn so the agent may proceed past the gate.
            var approve = new RelayCommand<PlanCardViewModel?>(card =>
            {
                if (card is null || card.IsApproved) return;
                card.IsApproved = true;
                SendFollowUp("Approved, please proceed.");
            });
            Messages.Add(new PlanCardViewModel(plan, approve));
            // A new plan supersedes any half-streamed bubble.
            _streamingBubble = null;
        });

        public void OnToolActivity(ToolActivityInfo activity) => Post(() =>
        {
            // Coalesce on CallId so Started -> Succeeded/Failed updates the same row.
            var existing = Messages.OfType<ToolActivityViewModel>()
                                   .FirstOrDefault(t => t.CallId == activity.CallId);
            if (existing != null)
            {
                existing.Update(activity);
            }
            else
            {
                var row = new ToolActivityViewModel(activity.CallId, activity.ToolName);
                row.Update(activity);
                Messages.Add(row);
            }
        });

        public void OnQuestion(QuestionInfo question) => Post(() =>
        {
            // Choosing an option (or typing a custom answer) sends it as the next user turn.
            var card = new QuestionCardViewModel(question, answer => SendFollowUp(answer));
            Messages.Add(card);
            _streamingBubble = null;
        });

        public void OnChangeSetReady(ChangeSet changeSet) => PostAsync(() => HandleChangeSetReady(changeSet));

        public void OnError(AgentError error) => Post(() =>
        {
            Messages.Add(new ErrorMessageViewModel(error, RetryLastCommand, OpenSettingsCommand));
            StatusText = error.Kind switch
            {
                AgentErrorKind.Auth => "Authentication failed",
                AgentErrorKind.InsufficientCredits => "Out of credits",
                AgentErrorKind.RateLimited => "Rate limited",
                AgentErrorKind.Network => "Network error",
                AgentErrorKind.Cancelled => "Stopped",
                _ => "Error"
            };
            ConnectionState = error.Kind == AgentErrorKind.Cancelled ? ConnectionState.Idle : ConnectionState.Error;
            _streamingBubble = null;
            // An error (including a Stop/cancel) ends the turn - clear the busy state so the composer
            // returns to Send and isn't stuck showing a dead Stop button. (RunTurnAsync swallows the
            // cancellation and routes it here, so OnTurnComplete won't run to reset this.)
            IsBusy = false;
        });

        public void OnTurnComplete(TurnUsage usage) => Post(() =>
        {
            UsageText = FormatUsage(usage);
            if (!string.IsNullOrWhiteSpace(usage.Model))
                CurrentModelId = usage.Model;
            IsBusy = false;
            if (ConnectionState == ConnectionState.Working)
                ConnectionState = ConnectionState.Connected;
            if (StatusText is "Thinking…" or "Stopping…")
                StatusText = "Ready";
            _streamingBubble = null;
        });

        // ----- change-set handling --------------------------------------------------------------

        /// <summary>
        /// Bring a freshly-staged change set on screen: render the side-effect-free preview, lint it,
        /// attach findings to rows, and add the review card. The header uses the change set's own computed
        /// stats - we never trust a model-supplied count.
        /// </summary>
        private async Task HandleChangeSetReady(ChangeSet cs)
        {
            _activeChangeSet = cs;

            // Show the transient preview first so the drawing reflects what the card describes.
            try { await _services.Previewer.ShowAsync(cs); }
            catch (Exception ex) { InlineError = $"Preview unavailable: {ex.Message}"; }

            var card = new ReviewCardViewModel(cs, this);

            // Lint against the active standards and pin findings to their rows (a Blocking finding will
            // already have been reflected as a Blocking op severity by the agent, but we surface the text).
            try
            {
                var findings = _services.Lint.Inspect(cs, _settings.Standards);
                foreach (var f in findings)
                {
                    var row = card.ChangeSet.Ops.FirstOrDefault(o => o.OpId == f.OpId);
                    row?.AttachLint(f.Message, f.Hint);
                }
            }
            catch
            {
                // Lint is advisory; never let it block showing the review card.
            }

            Messages.Add(card);

            // Scope the initial preview to the rows that defaulted to ticked.
            try { await _services.Previewer.UpdateVisibilityAsync(card.ChangeSet.AcceptedOpIds); }
            catch { }
        }

        // ----- IReviewHost (review cards call back through here; we're already on the UI thread) -

        public Task<ApplyResult> ApplyAsync(ChangeSet changeSet, string undoLabel)
            => _services.Applier.ApplyAsync(changeSet, undoLabel);

        public async Task RecordAuditAsync(ChangeSet changeSet, ApplyResult result)
        {
            var stats = changeSet.ComputeStats();
            var entry = new AuditEntry
            {
                DrawingKey = DrawingKeySentinel,
                ChangeSetId = changeSet.Id,
                TimestampUtc = DateTime.UtcNow.ToString("o"),
                Model = CurrentModelId,
                Summary = changeSet.Summary,
                AppliedOpCount = result.AppliedOpIds.Count,
                UndoLabel = result.UndoLabel,
                AppliedOpDescriptions = changeSet.Ops
                    .Where(o => result.AppliedOpIds.Contains(o.OpId))
                    .Select(o => string.IsNullOrWhiteSpace(o.PlainLanguage) ? o.Kind.ToString() : o.PlainLanguage)
                    .ToList()
            };
            try { await _services.AuditLog.RecordAsync(entry); } catch { /* audit is best-effort */ }
        }

        public Task ClearPreviewAsync()
        {
            if (ReferenceEquals(_activeChangeSet, null)) return Task.CompletedTask;
            return _services.Previewer.ClearAsync();
        }

        public Task UpdatePreviewVisibilityAsync(IReadOnlyList<string> visibleOpIds)
            => _services.Previewer.UpdateVisibilityAsync(visibleOpIds);

        public Task FlashAsync(string opId, bool zoom) => _services.Previewer.FlashAsync(opId, zoom);

        public Task UndoLastAsync() => _services.Applier.UndoLastAsync();

        public void ReportError(string message) => InlineError = message;

        // ----- helpers --------------------------------------------------------------------------

        private AssistantMessageViewModel AddAssistantBubble()
        {
            var bubble = new AssistantMessageViewModel();
            Messages.Add(bubble);
            return bubble;
        }

        private void ShowOnboardingIfNeeded()
        {
            // First-run: no key stored yet. Point the user at Settings with an inline card.
            if (!_services.SecureStore.HasApiKey)
            {
                ConnectionState = ConnectionState.Idle;
                StatusText = "Add your OpenRouter key to begin";
                Messages.Add(new OnboardingCardViewModel(OpenSettingsCommand));
            }
            else
            {
                ConnectionState = ConnectionState.Connected;
            }
        }

        // The audit log is "one file per drawing", but the canonical per-drawing key (resolved from the
        // active document path) is the audit adapter's concern - it sits behind IAuditLog and has the
        // AutoCAD document in hand. The UI only needs to hand over a non-empty, stable sentinel; the
        // adapter substitutes the real key. Keeping it here avoids an extra bridge round-trip and a
        // dependency on DrawingSummary's internals from the UI layer.
        private const string DrawingKeySentinel = "active-drawing";

        private static string FormatUsage(TurnUsage usage)
        {
            string tokens = $"{usage.PromptTokens:N0} in / {usage.CompletionTokens:N0} out";
            if (usage.CostUsd is double cost)
                return $"{tokens} · ${cost:0.0000}";
            return tokens;
        }

        /// <summary>
        /// Marshal an action onto the UI dispatcher. Observer callbacks arrive on a BACKGROUND thread, so
        /// every mutation of <see cref="Messages"/> or a bound property goes through here first.
        /// </summary>
        private void Post(Action action)
        {
            if (_dispatcher.CheckAccess())
                action();
            else
                _dispatcher.BeginInvoke(action);
        }

        /// <summary>Marshal an async action onto the UI dispatcher and run it fire-and-forget.</summary>
        private void PostAsync(Func<Task> asyncAction)
        {
            if (_dispatcher.CheckAccess())
                _ = asyncAction();
            else
                _dispatcher.BeginInvoke(new Action(() => _ = asyncAction()));
        }
    }
}
