using System.Linq;
using MojoCad.Core.Changes;
using MojoCad.Core.Geometry;
using Xunit;

namespace MojoCad.Core.Tests
{
    /// <summary>
    /// The change-set stats and AcceptAll policy are the trust boundary: the UI shows THESE numbers,
    /// never any model-supplied count, and AcceptAll must never sweep up blocking ops or erases. We
    /// pin that behaviour so a regression there can't silently apply something dangerous.
    /// </summary>
    public sealed class ChangeSetTests
    {
        private static CreateCircleOp Circle(string id, string? layer = null, double r = 5.0) =>
            new CreateCircleOp { OpId = id, Layer = layer, Center = new Pt(0, 0), Radius = r };

        private static MoveOp Move(string id, string? layer = null) =>
            new MoveOp { OpId = id, Layer = layer, From = new Pt(0, 0), To = new Pt(1, 1) };

        private static EraseOp Erase(string id, string? layer = null) =>
            new EraseOp { OpId = id, Layer = layer };

        private static CreateLayerOp Layer(string id, string name) =>
            new CreateLayerOp { OpId = id, Name = name, Layer = name };

        [Fact]
        public void Stats_CountsByCategory()
        {
            var cs = new ChangeSet();
            cs.Ops.Add(Circle("op-1"));                 // Additive
            cs.Ops.Add(Circle("op-2"));                 // Additive
            cs.Ops.Add(Move("op-3"));                   // Modify
            cs.Ops.Add(Erase("op-4"));                  // Erase
            cs.Ops.Add(Layer("op-5", "A-WALL"));        // Organizational

            var stats = cs.ComputeStats();

            Assert.Equal(5, stats.Total);
            Assert.Equal(2, stats.Additive);
            Assert.Equal(1, stats.Modify);
            Assert.Equal(1, stats.Erase);
            Assert.Equal(1, stats.Organizational);
            Assert.Equal(0, stats.Blocking);
        }

        [Fact]
        public void Stats_CountsBlockingBySeverity_IndependentOfCategory()
        {
            var cs = new ChangeSet();
            cs.Ops.Add(new CreateCircleOp { OpId = "op-1", Center = new Pt(0, 0), Radius = 1, Severity = Severity.Blocking });
            cs.Ops.Add(Move("op-2"));

            var stats = cs.ComputeStats();

            Assert.Equal(1, stats.Blocking);
            // Blocking is a severity, not a category, so the additive count is unaffected.
            Assert.Equal(1, stats.Additive);
        }

        [Fact]
        public void Stats_LayersTouched_DedupesCaseInsensitively_AndSorts()
        {
            var cs = new ChangeSet();
            cs.Ops.Add(Circle("op-1", "A-WALL"));
            cs.Ops.Add(Circle("op-2", "a-wall"));   // same layer, different case -> deduped
            cs.Ops.Add(Move("op-3", "M-DUCT"));
            cs.Ops.Add(Circle("op-4", "   "));      // whitespace -> ignored
            cs.Ops.Add(Circle("op-5", null));       // null -> ignored

            var stats = cs.ComputeStats();

            Assert.Equal(new[] { "A-WALL", "M-DUCT" }, stats.LayersTouched.ToArray());
        }

        [Fact]
        public void AcceptAll_SkipsBlockingAndErase_ByDefault()
        {
            var cs = new ChangeSet();
            var additive = Circle("op-1");
            var blocking = new CreateCircleOp { OpId = "op-2", Center = new Pt(0, 0), Radius = 1, Severity = Severity.Blocking };
            var erase = Erase("op-3");
            cs.Ops.Add(additive);
            cs.Ops.Add(blocking);
            cs.Ops.Add(erase);

            cs.AcceptAll();

            Assert.Equal(ChangeState.Accepted, additive.State);
            Assert.Equal(ChangeState.Proposed, blocking.State); // blocking is never auto-accepted
            Assert.Equal(ChangeState.Proposed, erase.State);    // erase requires explicit opt-in
            Assert.Single(cs.AcceptedOps);
        }

        [Fact]
        public void AcceptAll_IncludeErase_AcceptsErasesButStillSkipsBlocking()
        {
            var cs = new ChangeSet();
            var erase = Erase("op-1");
            var blockingErase = new EraseOp { OpId = "op-2", Severity = Severity.Blocking };
            cs.Ops.Add(erase);
            cs.Ops.Add(blockingErase);

            cs.AcceptAll(includeErase: true);

            Assert.Equal(ChangeState.Accepted, erase.State);
            Assert.Equal(ChangeState.Proposed, blockingErase.State); // blocking trumps includeErase
        }

        [Fact]
        public void RejectAll_RejectsEveryOp_IncludingBlocking()
        {
            var cs = new ChangeSet();
            cs.Ops.Add(Circle("op-1"));
            cs.Ops.Add(new CreateCircleOp { OpId = "op-2", Center = new Pt(0, 0), Radius = 1, Severity = Severity.Blocking });

            cs.RejectAll();

            Assert.All(cs.Ops, o => Assert.Equal(ChangeState.Rejected, o.State));
        }
    }
}
