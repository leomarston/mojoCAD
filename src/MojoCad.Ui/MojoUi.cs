using System;
using System.Windows;
using MojoCad.Core.Composition;
using MojoCad.Ui.Views;

namespace MojoCad.Ui
{
    /// <summary>
    /// The one public entry point the AutoCAD plugin calls to build the dockable chat palette. Keeping the
    /// surface to a single static factory means the plugin (the composition root) never has to know about
    /// any of the UI's internals - it hands us a fully-wired <see cref="MojoServices"/> and gets back a
    /// ready-to-dock <see cref="FrameworkElement"/>.
    /// </summary>
    public static class MojoUi
    {
        /// <summary>
        /// Build the chat palette content.
        /// </summary>
        /// <param name="services">The composition container with every port the UI needs.</param>
        /// <returns>The chat view, ready to host inside AutoCAD's PaletteSet.</returns>
        /// <remarks>
        /// The returned control is created on (and expected to live on) AutoCAD's UI thread - the palette
        /// is already on that thread. The view captures its own dispatcher and marshals all background
        /// agent-observer callbacks onto it, so the caller doesn't have to think about threading.
        /// </remarks>
        public static FrameworkElement CreateChatView(MojoServices services)
        {
            if (services is null) throw new ArgumentNullException(nameof(services));

            var view = new ChatView();

            // Merge the Dark theme into THIS control's resources rather than the app's. AutoCAD owns the
            // Application object (and may not have one at all in some hosts), so scoping the dictionary to
            // our control keeps the plugin self-contained and avoids clobbering the host's resources.
            var theme = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/MojoCad.Ui;component/Themes/Dark.xaml", UriKind.Absolute)
            };
            view.Resources.MergedDictionaries.Add(theme);

            // Wire the view-model and settings panel now that the theme (and thus DynamicResource lookups)
            // are available on the control.
            view.Initialize(services);

            return view;
        }
    }
}
