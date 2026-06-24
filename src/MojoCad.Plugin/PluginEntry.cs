using System;
using Autodesk.AutoCAD.Runtime;
using MojoCad.Acad;
using MojoCad.Acad.Interop;
using MojoCad.Agent;
using MojoCad.Agent.OpenRouter;
using MojoCad.Core.Composition;
using MojoCad.Core.Infrastructure;

[assembly: ExtensionApplication(typeof(MojoCad.Plugin.PluginEntry))]
[assembly: CommandClass(typeof(MojoCad.Plugin.Commands))]

namespace MojoCad.Plugin
{
    /// <summary>
    /// The plugin lifecycle and composition root. <see cref="IExtensionApplication.Initialize"/> runs on
    /// AutoCAD's main thread when the assembly loads; it captures the main-thread context and builds the
    /// single <see cref="MojoServices"/> graph that the UI and commands use.
    /// </summary>
    public sealed class PluginEntry : IExtensionApplication
    {
        private static MojoServices? _services;
        private static OpenRouterClient? _openRouter;

        /// <summary>The shared services graph, built lazily on first use.</summary>
        public static MojoServices Services => _services ??= BuildServices();

        public void Initialize()
        {
            // Capture AutoCAD's main-thread SynchronizationContext so background DB reads can marshal back.
            AcadContext.Initialize();
            MojoPaths.EnsureCreated();

            var ed = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager?.MdiActiveDocument?.Editor;
            ed?.WriteMessage("\nmojoCAD loaded. Type MOJO to open the chat palette.\n");
        }

        public void Terminate()
        {
            try { PaletteHost.Shutdown(); } catch { }
            try { _openRouter?.Dispose(); } catch { }
        }

        private static MojoServices BuildServices()
        {
            // Adapters - the only place concrete types are wired together.
            var secureStore = new DpapiSecureStore();
            var settingsStore = new SettingsStore();
            var bridge = new DrawingReader();
            var previewer = new TransientPreview();
            var applier = new ChangeApplier();

            _openRouter = new OpenRouterClient();                 // implements IOpenRouterClient + IOpenRouterAccount
            var agent = new AgentService(_openRouter, bridge, secureStore);

            var auditLog = new JsonlAuditLog();
            var lint = new StandardsLint();
            var conversations = new JsonFileConversationStore();

            return new MojoServices(
                agent, bridge, previewer, applier,
                _openRouter, secureStore, auditLog, lint, conversations, settingsStore);
        }
    }
}
