using System;
using System.Collections.Specialized;
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

            // Auto-scroll the transcript to the newest message as the conversation grows.
            if (MessageList.Items is INotifyCollectionChanged incc)
                incc.CollectionChanged += OnMessagesChanged;

            // Build the Settings view lazily-but-eagerly here so the overlay is ready on first open.
            SettingsHost.Initialize(services, CloseSettings);
        }

        private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // Cursor-style "stick to bottom": only auto-scroll when a message is appended AND the user
            // was already pinned to (or near) the bottom. If they've scrolled up to read earlier history,
            // leave their position alone so a new bubble doesn't yank the viewport.
            if (e.Action != NotifyCollectionChangedAction.Add) return;

            const double slack = 24; // px tolerance - a slightly-scrolled view still counts as "at bottom"
            bool atBottom = TranscriptScroll.ScrollableHeight <= 0
                            || TranscriptScroll.VerticalOffset >= TranscriptScroll.ScrollableHeight - slack;
            if (!atBottom) return;

            // Defer to render so the new item has measured before we scroll.
            Dispatcher.BeginInvoke(new Action(() => TranscriptScroll.ScrollToEnd()),
                System.Windows.Threading.DispatcherPriority.Background);
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
