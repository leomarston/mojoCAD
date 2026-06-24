using MojoCad.Core.Agent;
using MojoCad.Core.Infrastructure;
using MojoCad.Core.Ports;

namespace MojoCad.Core.Composition
{
    /// <summary>
    /// The composition container passed from the plugin's entry point to the UI. It bundles the
    /// implementations of every port so the UI can be built without referencing the concrete adapter
    /// projects. The plugin (composition root) is the only place that wires concrete types together.
    /// </summary>
    public sealed class MojoServices
    {
        public IAgentService Agent { get; }
        public IAcadBridge Bridge { get; }
        public IChangePreviewer Previewer { get; }
        public IChangeApplier Applier { get; }
        public IOpenRouterAccount Account { get; }
        public ISecureStore SecureStore { get; }
        public IAuditLog AuditLog { get; }
        public IStandardsLint Lint { get; }
        public IConversationStore Conversations { get; }
        public SettingsStore Settings { get; }

        public MojoServices(
            IAgentService agent,
            IAcadBridge bridge,
            IChangePreviewer previewer,
            IChangeApplier applier,
            IOpenRouterAccount account,
            ISecureStore secureStore,
            IAuditLog auditLog,
            IStandardsLint lint,
            IConversationStore conversations,
            SettingsStore settings)
        {
            Agent = agent;
            Bridge = bridge;
            Previewer = previewer;
            Applier = applier;
            Account = account;
            SecureStore = secureStore;
            AuditLog = auditLog;
            Lint = lint;
            Conversations = conversations;
            Settings = settings;
        }
    }
}
