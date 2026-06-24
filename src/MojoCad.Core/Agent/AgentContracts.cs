using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MojoCad.Core.Changes;
using MojoCad.Core.Settings;

namespace MojoCad.Core.Agent
{
    /// <summary>
    /// The high-level agent the UI talks to. One call runs a single user turn: the agent reasons,
    /// reads the drawing through tools, optionally stages a change set, and streams progress to the
    /// observer. It never applies anything - it only produces a reviewable <see cref="ChangeSet"/>.
    /// </summary>
    public interface IAgentService
    {
        /// <summary>
        /// Run one turn. Completes when the assistant finishes, emits a change set, or asks a
        /// clarifying question. Cancellation (the Stop button) aborts the in-flight request cleanly.
        /// </summary>
        Task RunTurnAsync(string userMessage, AgentTurnContext context, IAgentObserver observer, CancellationToken cancellationToken);

        /// <summary>Begin a fresh conversation (the "New chat" affordance).</summary>
        void ResetConversation();

        /// <summary>Serialize / restore the transcript for persistence.</summary>
        string ExportTranscript();
        void ImportTranscript(string serialized);
    }

    /// <summary>Per-turn inputs that vary independently of the long-lived agent configuration.</summary>
    public sealed class AgentTurnContext
    {
        public ModelConfig Models { get; set; } = new ModelConfig();
        public StandardsProfile Standards { get; set; } = new StandardsProfile();

        /// <summary>Handles the user attached as context chips (current selection, a picked window, etc.).</summary>
        public List<string> AttachedHandles { get; set; } = new List<string>();

        /// <summary>Enforce the plan-before-act approval gate for this turn.</summary>
        public bool RequirePlanApproval { get; set; }

        /// <summary>Offer the experimental <c>run_command</c> power tool this turn (user opt-in).</summary>
        public bool EnableCommandExecution { get; set; }
    }

    /// <summary>
    /// Streamed callbacks for a turn. Implemented by the chat view-model; every method is invoked on a
    /// background thread, so the UI implementation marshals to the dispatcher.
    /// </summary>
    public interface IAgentObserver
    {
        /// <summary>Coarse status for the footer/typing indicator, e.g. "Reading drawing", "Thinking".</summary>
        void OnStatus(string status);

        /// <summary>A streamed fragment of assistant prose.</summary>
        void OnAssistantTextDelta(string delta);

        /// <summary>The assistant prose for this step is complete (final markdown to render).</summary>
        void OnAssistantText(string markdown);

        /// <summary>A numbered plan emitted before acting (rendered as a PlanCard; may gate on approval).</summary>
        void OnPlan(PlanInfo plan);

        /// <summary>A tool started/finished (rendered as a collapsible activity row).</summary>
        void OnToolActivity(ToolActivityInfo activity);

        /// <summary>The agent asked a clarifying question (rendered as a QuestionCard; ends the turn).</summary>
        void OnQuestion(QuestionInfo question);

        /// <summary>A reviewable change set is ready. The UI shows the preview + review card.</summary>
        void OnChangeSetReady(ChangeSet changeSet);

        /// <summary>A recoverable or terminal error occurred.</summary>
        void OnError(AgentError error);

        /// <summary>The turn finished; carries usage/cost for the footer.</summary>
        void OnTurnComplete(TurnUsage usage);
    }

    public sealed class PlanInfo
    {
        public string Title { get; set; } = "Plan";
        public List<string> Steps { get; set; } = new List<string>();

        /// <summary>True when the user must approve before the agent may proceed (Fire &amp; Life-Safety).</summary>
        public bool RequiresApproval { get; set; }
    }

    public enum ToolActivityState { Started, Succeeded, Failed }

    public sealed class ToolActivityInfo
    {
        public string ToolName { get; set; } = string.Empty;
        public string CallId { get; set; } = string.Empty;
        public ToolActivityState State { get; set; }

        /// <summary>One-line human description, e.g. "Read drawing summary" or "Staged 4 walls on A-WALL".</summary>
        public string Display { get; set; } = string.Empty;

        /// <summary>For failures, the error code/hint surfaced to the user (and fed back to the model).</summary>
        public string? Detail { get; set; }
    }

    public sealed class QuestionInfo
    {
        public string Question { get; set; } = string.Empty;
        public List<string> Options { get; set; } = new List<string>();
    }

    public enum AgentErrorKind { Network, Auth, InsufficientCredits, RateLimited, Moderation, Provider, BadRequest, Cancelled, Unknown }

    public sealed class AgentError
    {
        public AgentErrorKind Kind { get; set; } = AgentErrorKind.Unknown;
        public string Message { get; set; } = string.Empty;

        /// <summary>Suggested next action for the UI (e.g. "Open Settings → API key", "Top up credits").</summary>
        public string? Remedy { get; set; }

        /// <summary>True when the agent can be retried as-is (transient network/provider issue).</summary>
        public bool Retryable { get; set; }
    }

    public sealed class TurnUsage
    {
        public string Model { get; set; } = string.Empty;
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }

        /// <summary>Cost in USD as reported by OpenRouter, when available.</summary>
        public double? CostUsd { get; set; }
    }
}
