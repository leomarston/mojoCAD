using System;
using System.Collections.Generic;
using System.Linq;
using MojoCad.Core.Changes;
using MojoCad.Core.Ports;
using MojoCad.Core.Settings;

namespace MojoCad.Core.Infrastructure
{
    /// <summary>
    /// Pre-apply linting against the active standards. Findings are surfaced on the review card before
    /// the engineer accepts. <see cref="Severity.Blocking"/> findings prevent acceptance of that op.
    /// This is intentionally conservative - it nudges toward standards, it does not silently "fix".
    /// </summary>
    public sealed class StandardsLint : IStandardsLint
    {
        public IReadOnlyList<LintFinding> Inspect(ChangeSet changeSet, StandardsProfile standards)
        {
            var findings = new List<LintFinding>();
            if (changeSet == null) return findings;

            foreach (var op in changeSet.Ops)
            {
                // 1) BYLAYER discipline: per-entity colour/linetype overrides are discouraged.
                if (op.Props != null && (op.Props.ColorIndex.HasValue || op.Props.TrueColor.HasValue ||
                                         op.Props.Linetype != null || op.Props.LineweightHundredthsMm.HasValue))
                {
                    findings.Add(new LintFinding
                    {
                        OpId = op.OpId,
                        Severity = Severity.Notice,
                        Message = "Per-entity property override set instead of relying on the layer (BYLAYER).",
                        Hint = "Prefer creating/using a layer that carries the colour, linetype and lineweight."
                    });
                }

                // 2) Layer-naming standard hint for created geometry on ad-hoc layers.
                if (op.Category == OpCategory.Additive && !string.IsNullOrWhiteSpace(op.Layer) &&
                    standards.LayerStandard == LayerStandard.AiaNcs && !LooksLikeNcsLayer(op.Layer!))
                {
                    findings.Add(new LintFinding
                    {
                        OpId = op.OpId,
                        Severity = Severity.Info,
                        Message = $"Layer '{op.Layer}' does not look like an AIA/NCS name (e.g. A-WALL, M-DUCT, FP-SPKL).",
                        Hint = "Confirm this matches the project's CAD standard."
                    });
                }

                // 3) Geometry sanity - obvious life-safety footguns.
                AppendGeometrySanity(op, findings);
            }

            return findings;
        }

        private static void AppendGeometrySanity(ProposedOp op, List<LintFinding> findings)
        {
            switch (op)
            {
                case CreateCircleOp c when c.Radius <= 0:
                    findings.Add(Blocking(op, "Circle radius must be positive."));
                    break;
                case CreateArcOp a when a.Radius <= 0:
                    findings.Add(Blocking(op, "Arc radius must be positive."));
                    break;
                case CreatePolylineOp p when p.Points.Count < 2:
                    findings.Add(Blocking(op, "Polyline needs at least two vertices."));
                    break;
                case ScaleOp s when s.Factor <= 0:
                    findings.Add(Blocking(op, "Scale factor must be positive."));
                    break;
                case CreateTextOp t when t.Height <= 0:
                    findings.Add(Blocking(op, "Text height must be positive."));
                    break;
            }
        }

        private static LintFinding Blocking(ProposedOp op, string message) => new LintFinding
        {
            OpId = op.OpId,
            Severity = Severity.Blocking,
            Message = message,
            Hint = "This operation cannot be applied until corrected."
        };

        private static bool LooksLikeNcsLayer(string name)
        {
            // NCS layer names start with a one/two-char discipline code then a hyphen, e.g. A-, M-, FP-, S-, E-, P-.
            int dash = name.IndexOf('-');
            if (dash <= 0 || dash > 2) return false;
            string disc = name.Substring(0, dash).ToUpperInvariant();
            return disc.All(char.IsLetter);
        }
    }
}
