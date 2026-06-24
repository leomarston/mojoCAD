using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using MojoCad.Core.Agent;

namespace MojoCad.Ui.ViewModels
{
    /// <summary>A message the engineer typed. Plain text - no markdown, no streaming.</summary>
    public sealed partial class UserMessageViewModel : MessageViewModel
    {
        public UserMessageViewModel(string text) => Text = text;

        [ObservableProperty]
        private string _text = string.Empty;

        /// <summary>Context chips the user attached to this turn (e.g. "12 selected"), shown under the bubble.</summary>
        public ObservableCollection<string> Attachments { get; } = new();
    }

    /// <summary>
    /// An assistant prose bubble. While the turn streams we append deltas to <see cref="StreamingText"/>;
    /// when the step finalises we set <see cref="Markdown"/> (the canonical text to render). We keep both
    /// so a partially-streamed bubble still shows something if the connection drops mid-token.
    /// </summary>
    public sealed partial class AssistantMessageViewModel : MessageViewModel
    {
        [ObservableProperty]
        private string _streamingText = string.Empty;

        [ObservableProperty]
        private string _markdown = string.Empty;

        /// <summary>True until <see cref="OnAssistantText"/> finalises this bubble; drives the caret/cursor.</summary>
        [ObservableProperty]
        private bool _isStreaming = true;

        /// <summary>The text the view should render: the final markdown once present, else the live stream.</summary>
        public string DisplayText => string.IsNullOrEmpty(Markdown) ? StreamingText : Markdown;

        partial void OnStreamingTextChanged(string value) => OnPropertyChanged(nameof(DisplayText));
        partial void OnMarkdownChanged(string value) => OnPropertyChanged(nameof(DisplayText));

        public void AppendDelta(string delta)
        {
            StreamingText += delta;
        }

        public void Finalize(string markdown)
        {
            Markdown = markdown;
            IsStreaming = false;
        }
    }

    /// <summary>
    /// A collapsible "the agent did X" row backed by a tool call. We key rows by <see cref="CallId"/> so
    /// the Started -&gt; Succeeded/Failed updates land on the same row instead of stacking duplicates.
    /// </summary>
    public sealed partial class ToolActivityViewModel : MessageViewModel
    {
        public ToolActivityViewModel(string callId, string toolName)
        {
            CallId = callId;
            ToolName = toolName;
        }

        public string CallId { get; }

        [ObservableProperty]
        private string _toolName = string.Empty;

        [ObservableProperty]
        private string _display = string.Empty;

        [ObservableProperty]
        private string? _detail;

        [ObservableProperty]
        private ToolActivityState _state = ToolActivityState.Started;

        /// <summary>The activity rows are collapsed (detail hidden) by default to keep the transcript calm.</summary>
        [ObservableProperty]
        private bool _isExpanded;

        public bool IsRunning => State == ToolActivityState.Started;
        public bool IsFailed => State == ToolActivityState.Failed;
        public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

        // A tiny glyph stands in for an icon font we don't want to depend on.
        public string StatusGlyph => State switch
        {
            ToolActivityState.Started => "◌",   // dotted circle = in progress
            ToolActivityState.Succeeded => "✓", // check
            ToolActivityState.Failed => "✗",    // cross
            _ => "◌"
        };

        public void Update(ToolActivityInfo info)
        {
            ToolName = info.ToolName;
            if (!string.IsNullOrWhiteSpace(info.Display)) Display = info.Display;
            Detail = info.Detail;
            State = info.State;
        }

        partial void OnStateChanged(ToolActivityState value)
        {
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsFailed));
            OnPropertyChanged(nameof(StatusGlyph));
        }

        partial void OnDetailChanged(string? value) => OnPropertyChanged(nameof(HasDetail));
    }

    /// <summary>
    /// A numbered plan rendered before the agent acts. When <see cref="RequiresApproval"/> is set, the
    /// card shows an Approve button; clicking it sends a follow-up turn so the agent may proceed.
    /// </summary>
    public sealed partial class PlanCardViewModel : MessageViewModel
    {
        public PlanCardViewModel(PlanInfo plan, ICommand approveCommand)
        {
            Title = string.IsNullOrWhiteSpace(plan.Title) ? "Plan" : plan.Title;
            RequiresApproval = plan.RequiresApproval;
            ApproveCommand = approveCommand;
            int i = 1;
            foreach (var step in plan.Steps)
                Steps.Add(new PlanStep(i++, step));
        }

        public string Title { get; }
        public bool RequiresApproval { get; }
        public ObservableCollection<PlanStep> Steps { get; } = new();
        public ICommand ApproveCommand { get; }

        /// <summary>Flipped once the user approves (or the next turn begins) so the button can't be re-clicked.</summary>
        [ObservableProperty]
        private bool _isApproved;

        public sealed record PlanStep(int Number, string Text);
    }

    /// <summary>
    /// A clarifying question. Each option is a button that, when clicked, sends the chosen answer as the
    /// next user turn. Once answered the card locks so the transcript stays an honest record.
    /// </summary>
    public sealed partial class QuestionCardViewModel : MessageViewModel
    {
        public QuestionCardViewModel(QuestionInfo question, System.Action<string> answer)
        {
            Question = question.Question;
            foreach (var opt in question.Options)
                Options.Add(opt);
            _answer = answer;
            ChooseCommand = new CommunityToolkit.Mvvm.Input.RelayCommand<string>(opt =>
            {
                if (!string.IsNullOrWhiteSpace(opt)) Choose(opt!);
            });
        }

        private readonly System.Action<string> _answer;

        public string Question { get; }
        public ObservableCollection<string> Options { get; } = new();
        public bool HasOptions => Options.Count > 0;

        /// <summary>Bound by each option button; forwards the chosen option to <see cref="Choose"/>.</summary>
        public ICommand ChooseCommand { get; }

        [ObservableProperty]
        private bool _isAnswered;

        [ObservableProperty]
        private string? _chosenAnswer;

        public void Choose(string option)
        {
            if (IsAnswered) return;
            ChosenAnswer = option;
            IsAnswered = true;
            _answer(option);
        }
    }

    /// <summary>
    /// A terminal/recoverable error line. Carries the remedy hint from the agent and a Retry button when
    /// the failure is transient (the agent told us it is <see cref="AgentError.Retryable"/>).
    /// </summary>
    public sealed partial class ErrorMessageViewModel : MessageViewModel
    {
        public ErrorMessageViewModel(AgentError error, ICommand? retryCommand, ICommand? openSettingsCommand)
        {
            Message = error.Message;
            Remedy = error.Remedy;
            Kind = error.Kind;
            IsRetryable = error.Retryable;
            RetryCommand = retryCommand;
            OpenSettingsCommand = openSettingsCommand;
            // Auth / credit problems are best fixed in Settings, so offer that affordance for them.
            ShowOpenSettings = error.Kind is AgentErrorKind.Auth or AgentErrorKind.InsufficientCredits;
        }

        public string Message { get; }
        public string? Remedy { get; }
        public AgentErrorKind Kind { get; }
        public bool IsRetryable { get; }
        public bool ShowOpenSettings { get; }
        public bool HasRemedy => !string.IsNullOrWhiteSpace(Remedy);
        public ICommand? RetryCommand { get; }
        public ICommand? OpenSettingsCommand { get; }
    }

    /// <summary>
    /// First-run onboarding card shown inline at the top of the transcript when no API key is stored. Its
    /// only job is to point the user at Settings so they can paste their OpenRouter key.
    /// </summary>
    public sealed partial class OnboardingCardViewModel : MessageViewModel
    {
        public OnboardingCardViewModel(ICommand openSettingsCommand) => OpenSettingsCommand = openSettingsCommand;

        public ICommand OpenSettingsCommand { get; }
    }
}
