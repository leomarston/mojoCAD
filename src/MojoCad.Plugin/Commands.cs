using Autodesk.AutoCAD.Runtime;

namespace MojoCad.Plugin
{
    /// <summary>AutoCAD command entry points. MOJO / MOJOCHAT toggle the dockable chat palette.</summary>
    public sealed class Commands
    {
        [CommandMethod("MOJO")]
        public void Mojo() => PaletteHost.Toggle(PluginEntry.Services);

        [CommandMethod("MOJOCHAT")]
        public void MojoChat() => PaletteHost.Show(PluginEntry.Services);
    }
}
