using System;
using System.Windows;
using Autodesk.AutoCAD.Windows;
using MojoCad.Core.Composition;
using MojoCad.Ui;

namespace MojoCad.Plugin
{
    /// <summary>
    /// Owns the dockable <see cref="PaletteSet"/> that hosts the WPF chat view. The view is created once
    /// (it carries the conversation state) and reused. The palette runs on AutoCAD's main/UI thread, so
    /// the WPF content and the agent's dispatcher callbacks share that thread.
    /// </summary>
    internal static class PaletteHost
    {
        // Stable id so AutoCAD remembers the palette's dock state between sessions.
        private static readonly Guid PaletteId = new Guid("7E2C0B40-6C2E-4C0A-9C3E-9B8F5A1D2E10");

        private static PaletteSet? _paletteSet;
        private static FrameworkElement? _view;

        public static void Show(MojoServices services)
        {
            EnsureCreated(services);
            _paletteSet!.Visible = true;
        }

        public static void Toggle(MojoServices services)
        {
            EnsureCreated(services);
            _paletteSet!.Visible = !_paletteSet.Visible;
        }

        public static void Shutdown()
        {
            if (_paletteSet != null)
            {
                _paletteSet.Visible = false;
                _paletteSet = null;
            }
            _view = null;
        }

        private static void EnsureCreated(MojoServices services)
        {
            if (_paletteSet != null) return;

            _paletteSet = new PaletteSet("mojoCAD", PaletteId)
            {
                // Cursor-style side panel: pin (auto-hide), close, snap-to-edge, the gripper menu,
                // and use the panel name as the title bar so it reads as one clean column.
                Style = PaletteSetStyles.ShowAutoHideButton
                        | PaletteSetStyles.ShowCloseButton
                        | PaletteSetStyles.Snappable
                        | PaletteSetStyles.ShowPropertiesMenu
                        | PaletteSetStyles.UsePaletteNameAsTitleForSingle,
                // Allow docking either side and floating; comfortable floor for a chat column.
                DockEnabled = DockSides.Left | DockSides.Right,
                MinimumSize = new System.Drawing.Size(360, 480)
                // No KeepFocus: it can disrupt command-line interop and isn't needed for typing.
            };

            // A sensible default floating size (docking uses the width); set before it's shown.
            _paletteSet.Size = new System.Drawing.Size(420, 820);

            _view = MojoUi.CreateChatView(services);
            _paletteSet.AddVisual("Chat", _view);

            // Default to docked on the RIGHT, like Cursor's panel. The user can drag it to the
            // left or float it afterwards; AutoCAD remembers their choice per the palette GUID.
            try { _paletteSet.Dock = DockSides.Right; } catch { /* dock state is best-effort */ }
        }
    }
}
