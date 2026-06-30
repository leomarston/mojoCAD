using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MojoCad.Core.Composition;
using MojoCad.Ui.ViewModels;

namespace MojoCad.Ui.Views
{
    /// <summary>
    /// The chat palette's root control. Code-behind is kept to the irreducible view concerns: keyboard
    /// send (Enter vs Shift+Enter), auto-scroll on new messages, expanding a tool row, routing a question
    /// option click into the VM, and showing/hiding the in-panel Settings overlay. Everything else lives
    /// in the view-models.
    /// </summary>
    public partial class ChatView : UserControl
    {
        private ChatViewModel? _vm;

        // "Stick to bottom" state. True while the transcript should glue to the newest content; flipped off
        // the moment the user scrolls up to read history, and back on when they return to the bottom.
        private bool _autoScroll = true;
        private const double ScrollSlack = 24; // px tolerance so a near-bottom view still counts as "pinned"

        public ChatView()
        {
            InitializeComponent();
        }

        /// <summary>Called once by <see cref="MojoCad.Ui.MojoUi.CreateChatView"/> after construction.</summary>
        internal void Initialize(MojoServices services)
        {
            // The chat view runs on AutoCAD's UI thread; capture its dispatcher for marshalling callbacks.
            _vm = new ChatViewModel(services, Dispatcher, OpenSettings);
            DataContext = _vm;

            // Build the Settings view lazily-but-eagerly here so the overlay is ready on first open.
            SettingsHost.Initialize(services, CloseSettings);
        }

        /// <summary>
        /// Canonical chat "stick to bottom". <see cref="ScrollChangedEventArgs.ExtentHeightChange"/> is non-zero
        /// when the CONTENT grew - a new bubble, a tool row, OR an assistant message streaming tokens into an
        /// existing bubble (which is only a property change, never a collection change, so a collection handler
        /// would miss it). When content grows and we're pinned, glue to the bottom. When it's zero the scroll
        /// was user-initiated, so we recompute the pin from where they landed: scrolling up unpins, returning
        /// to the bottom re-pins. Re-entrancy is safe - our ScrollToVerticalOffset raises a follow-up event with
        /// ExtentHeightChange == 0 that simply re-affirms the pin.
        /// </summary>
        private void TranscriptScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.ExtentHeightChange > 0)
            {
                if (_autoScroll)
                    TranscriptScroll.ScrollToVerticalOffset(TranscriptScroll.ScrollableHeight);
            }
            else if (e.VerticalChange != 0 || e.ViewportHeightChange != 0)
            {
                _autoScroll = TranscriptScroll.VerticalOffset
                              >= TranscriptScroll.ScrollableHeight - ScrollSlack;
            }
        }

        private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Enter sends; Shift+Enter inserts a newline (standard chat ergonomics).
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                if (_vm?.SendCommand.CanExecute(null) == true)
                    _vm.SendCommand.Execute(null);
            }
        }

        private void ToolActivity_ToggleClick(object sender, RoutedEventArgs e)
        {
            // The info button toggles the detail panel without needing a command on each row.
            if (sender is FrameworkElement fe && fe.DataContext is ToolActivityViewModel row)
                row.IsExpanded = !row.IsExpanded;
        }

        private void OpenSettings()
        {
            SettingsHost.Reload();
            SettingsOverlay.Visibility = Visibility.Visible;
        }

        private void CloseSettings()
        {
            SettingsOverlay.Visibility = Visibility.Collapsed;
            // Re-evaluate onboarding/connection state cheaply by letting the next turn re-read settings.
        }
    }
}
