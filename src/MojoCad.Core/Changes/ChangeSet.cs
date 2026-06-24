using System;
using System.Collections.Generic;
using System.Linq;

namespace MojoCad.Core.Changes
{
    /// <summary>
    /// The unit of agreement between the AI and the engineer. Every AI turn that wants to alter the
    /// drawing finalises exactly one <see cref="ChangeSet"/> (via the <c>emit_changeset</c> control
    /// tool). The engineer reviews it, ticks the operations they accept, and the accepted subset is
    /// applied <i>atomically</i> as a single named undo group. Nothing here is ever applied without
    /// explicit human action.
    /// </summary>
    public sealed class ChangeSet
    {
        /// <summary>Stable id (e.g. a GUID string) used by the UI, audit log and provenance stamp.</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Plain-language summary authored by the agent (rendered as the review card header).</summary>
        public string Summary { get; set; } = string.Empty;

        /// <summary>The conversation turn / message this change set belongs to.</summary>
        public string? OriginMessageId { get; set; }

        /// <summary>Ordered operations. Order matters - later ops may depend on earlier ones.</summary>
        public List<ProposedOp> Ops { get; } = new List<ProposedOp>();

        /// <summary>Aggregate state. Derived from the ops' states.</summary>
        public ChangeState State { get; set; } = ChangeState.Proposed;

        public IEnumerable<ProposedOp> AcceptedOps => Ops.Where(o => o.State == ChangeState.Accepted);

        /// <summary>
        /// Counts computed from the ops - the UI shows <i>these</i>, never any model-supplied number,
        /// so the AI cannot misrepresent what it is about to do.
        /// </summary>
        public ChangeSetStats ComputeStats() => ChangeSetStats.From(Ops);

        /// <summary>Mark every non-blocking op as accepted. Deletions are left for explicit opt-in.</summary>
        public void AcceptAll(bool includeErase = false)
        {
            foreach (var op in Ops)
            {
                if (op.Severity == Severity.Blocking) continue;
                if (op.Category == OpCategory.Erase && !includeErase) continue;
                op.State = ChangeState.Accepted;
            }
        }

        public void RejectAll()
        {
            foreach (var op in Ops)
                op.State = ChangeState.Rejected;
        }
    }

    /// <summary>Computed, trustworthy counts for a change set. Display these; never trust the model's.</summary>
    public sealed class ChangeSetStats
    {
        public int Total { get; private set; }
        public int Additive { get; private set; }
        public int Modify { get; private set; }
        public int Erase { get; private set; }
        public int Organizational { get; private set; }
        public int Blocking { get; private set; }

        /// <summary>Distinct layers touched by the change set.</summary>
        public IReadOnlyList<string> LayersTouched { get; private set; } = Array.Empty<string>();

        public static ChangeSetStats From(IReadOnlyCollection<ProposedOp> ops)
        {
            var stats = new ChangeSetStats
            {
                Total = ops.Count,
                Additive = ops.Count(o => o.Category == OpCategory.Additive),
                Modify = ops.Count(o => o.Category == OpCategory.Modify),
                Erase = ops.Count(o => o.Category == OpCategory.Erase),
                Organizational = ops.Count(o => o.Category == OpCategory.Organizational),
                Blocking = ops.Count(o => o.Severity == Severity.Blocking),
                LayersTouched = ops
                    .Select(o => o.Layer)
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Select(l => l!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };
            return stats;
        }
    }

    /// <summary>Outcome of applying the accepted subset of a change set to the drawing.</summary>
    public sealed class ApplyResult
    {
        public bool Success { get; set; }

        /// <summary>The undo-group label that was used (e.g. "mojoCAD: Fire corridor L2").</summary>
        public string UndoLabel { get; set; } = string.Empty;

        /// <summary>Ops that were committed.</summary>
        public List<string> AppliedOpIds { get; } = new List<string>();

        /// <summary>Ops that failed (apply is all-or-nothing, so on failure this drives the rollback toast).</summary>
        public List<string> FailedOpIds { get; } = new List<string>();

        /// <summary>Human-readable error if the apply aborted.</summary>
        public string? Error { get; set; }

        public static ApplyResult Failed(string error) => new ApplyResult { Success = false, Error = error };
    }
}
