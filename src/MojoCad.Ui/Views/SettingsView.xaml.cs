using System;
using System.Windows.Controls;
using MojoCad.Core.Composition;
using MojoCad.Ui.ViewModels;

namespace MojoCad.Ui.Views
{
    /// <summary>
    /// The settings panel. Code-behind only constructs/refreshes the view-model and clears the masked
    /// PasswordBox on each open (the secret never round-trips into a control we don't have to keep it in).
    /// All behaviour lives on <see cref="SettingsViewModel"/>.
    /// </summary>
    public partial class SettingsView : UserControl
    {
        private MojoServices? _services;
        private Action? _onClosed;

        public SettingsView()
        {
            InitializeComponent();
        }

        /// <summary>Wire the panel once; <paramref name="onClosed"/> hides the overlay.</summary>
        internal void Initialize(MojoServices services, Action onClosed)
        {
            _services = services;
            _onClosed = onClosed;
            Reload();
        }

        /// <summary>Rebuild the VM from disk so every open reflects the latest persisted settings.</summary>
        internal void Reload()
        {
            if (_services == null || _onClosed == null) return;
            // Clearing the box ensures a previously-typed (unsaved) key isn't left visible across opens.
            KeyPasswordBox.Clear();
            DataContext = new SettingsViewModel(_services, _onClosed);
        }
    }
}
