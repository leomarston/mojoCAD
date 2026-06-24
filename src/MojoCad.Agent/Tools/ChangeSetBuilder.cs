using System.Collections.Generic;
using MojoCad.Core.Changes;

namespace MojoCad.Agent.Tools
{
    /// <summary>
    /// Accumulates the <see cref="ProposedOp"/>s staged during one turn into a <see cref="ChangeSet"/>.
    /// Assigns stable op ids and a provisional handle for each created op, so the model can reference
    /// geometry it just created (e.g. place an opening in a wall) before anything is applied. The Acad
    /// applier resolves provisional handles ("@op-3") to real AutoCAD handles at commit time.
    /// </summary>
    public sealed class ChangeSetBuilder
    {
        private readonly ChangeSet _changeSet;
        private int _counter;

        public ChangeSetBuilder(string changeSetId)
        {
            _changeSet = new ChangeSet { Id = changeSetId };
        }

        public int Count => _changeSet.Ops.Count;

        public bool HasOps => _changeSet.Ops.Count > 0;

        /// <summary>Provisional handles assigned this turn (so edit tools can validate references to staged geometry).</summary>
        public HashSet<string> ProvisionalHandles { get; } = new HashSet<string>();

        /// <summary>Append an op. Returns the assigned op id and (for creates) a provisional handle.</summary>
        public StagedRef Add(ProposedOp op)
        {
            _counter++;
            op.OpId = "op-" + _counter;
            string provisional = "@" + op.OpId;

            // Creates and copies yield new geometry the model may reference later this turn.
            bool producesGeometry =
                op.Category == OpCategory.Additive || op is CopyOp || op is ArrayOp ||
                (op is MirrorOp m && m.KeepOriginal);

            if (producesGeometry)
            {
                op.ResultHandles.Add(provisional);
                ProvisionalHandles.Add(provisional);
            }

            _changeSet.Ops.Add(op);
            return new StagedRef(op.OpId, producesGeometry ? provisional : null);
        }

        public ChangeSet Finalize(string summary)
        {
            _changeSet.Summary = summary;
            return _changeSet;
        }

        public ChangeSet Current => _changeSet;
    }

    public readonly struct StagedRef
    {
        public string OpId { get; }
        public string? ProvisionalHandle { get; }

        public StagedRef(string opId, string? provisionalHandle)
        {
            OpId = opId;
            ProvisionalHandle = provisionalHandle;
        }
    }
}
