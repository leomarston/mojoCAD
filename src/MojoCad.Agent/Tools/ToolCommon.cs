using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MojoCad.Core.Changes;
using MojoCad.Core.Ports;

namespace MojoCad.Agent.Tools
{
    /// <summary>Shared helpers for parsing the common WRITE-tool fields and validating handle references.</summary>
    internal static class ToolCommon
    {
        /// <summary>Apply the common layer/props/note/severity fields onto a freshly-built op.</summary>
        public static void ApplyCommon(ProposedOp op, ToolArgs args)
        {
            op.Layer = args.GetStringOrNull("layer");
            op.Props = ParseProps(args);
            string? note = args.GetStringOrNull("note");
            if (!string.IsNullOrWhiteSpace(note)) op.PlainLanguage = note!;
            if (args.Has("severity"))
                op.Severity = SeverityFrom(args.GetEnum("severity", new[] { "info", "notice", "warning", "blocking" }, "info"));
        }

        public static Severity SeverityFrom(string s) => s switch
        {
            "notice" => Severity.Notice,
            "warning" => Severity.Warning,
            "blocking" => Severity.Blocking,
            _ => Severity.Info
        };

        public static PropertyOverrides? ParseProps(ToolArgs args) => args.GetPropsOrNull();

        /// <summary>
        /// Validate that every referenced handle either exists in the drawing or is a provisional handle
        /// staged earlier this turn. Throws a STALE_HANDLE <see cref="ToolArgException"/> otherwise.
        /// </summary>
        public static async Task<List<string>> ResolveHandlesAsync(
            List<string> handles, ChangeSetBuilder builder, IAcadBridge bridge)
        {
            var provisional = handles.Where(h => builder.ProvisionalHandles.Contains(h)).ToList();
            var realCandidates = handles.Where(h => !builder.ProvisionalHandles.Contains(h)).ToList();

            if (realCandidates.Count > 0)
            {
                var existing = (await bridge.FilterExistingHandlesAsync(realCandidates).ConfigureAwait(false))
                    .ToHashSet(System.StringComparer.OrdinalIgnoreCase);
                var missing = realCandidates.Where(h => !existing.Contains(h)).ToList();
                if (missing.Count > 0)
                    throw new ToolArgException("STALE_HANDLE",
                        "These handles do not exist in the drawing: " + string.Join(", ", missing) + ".",
                        string.Join(",", missing),
                        "Call query_entities to get current handles, or reference geometry you staged this turn.");
            }
            return handles;
        }
    }
}
