using System.Linq;
using MojoCad.Core.Changes;
using MojoCad.Core.Geometry;
using MojoCad.Core.Infrastructure;
using MojoCad.Core.Ports;
using MojoCad.Core.Settings;
using Xunit;

namespace MojoCad.Core.Tests
{
    /// <summary>
    /// The lint is the last automated guard before an engineer accepts. Its blocking findings prevent a
    /// life-safety footgun (zero-radius circle, negative scale, degenerate polyline) from ever being
    /// applied, and its notices/info nudge toward BYLAYER + the layer standard. We verify both the
    /// blocking cases and the advisory cases against the real implementation.
    /// </summary>
    public sealed class StandardsLintTests
    {
        private readonly StandardsLint _lint = new StandardsLint();
        private readonly StandardsProfile _ncs = new StandardsProfile { LayerStandard = LayerStandard.AiaNcs };

        private static ChangeSet Wrap(params ProposedOp[] ops)
        {
            var cs = new ChangeSet();
            cs.Ops.AddRange(ops);
            return cs;
        }

        [Fact]
        public void Notice_WhenPropertyOverrideUsedInsteadOfByLayer()
        {
            var op = new CreateCircleOp
            {
                OpId = "op-1",
                Center = new Pt(0, 0),
                Radius = 5,
                Layer = "A-WALL",
                Props = new PropertyOverrides { ColorIndex = 1 }
            };

            var findings = _lint.Inspect(Wrap(op), _ncs);

            var notice = Assert.Single(findings.Where(f => f.Severity == Severity.Notice));
            Assert.Equal("op-1", notice.OpId);
            Assert.Contains("BYLAYER", notice.Message);
        }

        [Fact]
        public void Info_WhenAdditiveLayerDoesNotLookLikeNcs()
        {
            var op = new CreateCircleOp { OpId = "op-1", Center = new Pt(0, 0), Radius = 5, Layer = "MyRandomLayer" };

            var findings = _lint.Inspect(Wrap(op), _ncs);

            var info = Assert.Single(findings.Where(f => f.Severity == Severity.Info));
            Assert.Contains("NCS", info.Message);
        }

        [Fact]
        public void NoNcsHint_WhenLayerLooksLikeNcs()
        {
            var op = new CreateCircleOp { OpId = "op-1", Center = new Pt(0, 0), Radius = 5, Layer = "FP-SPKL" };

            var findings = _lint.Inspect(Wrap(op), _ncs);

            Assert.DoesNotContain(findings, f => f.Severity == Severity.Info);
        }

        [Fact]
        public void Blocking_WhenCircleRadiusIsZeroOrNegative()
        {
            var zero = new CreateCircleOp { OpId = "op-1", Center = new Pt(0, 0), Radius = 0 };
            var negative = new CreateCircleOp { OpId = "op-2", Center = new Pt(0, 0), Radius = -3 };

            var findings = _lint.Inspect(Wrap(zero, negative), _ncs);

            var blocking = findings.Where(f => f.Severity == Severity.Blocking).ToList();
            Assert.Equal(2, blocking.Count);
            Assert.Contains(blocking, b => b.OpId == "op-1");
            Assert.Contains(blocking, b => b.OpId == "op-2");
        }

        [Fact]
        public void Blocking_WhenArcRadiusNonPositive()
        {
            var op = new CreateArcOp { OpId = "op-1", Center = new Pt(0, 0), Radius = 0, StartAngleDeg = 0, EndAngleDeg = 90 };

            var findings = _lint.Inspect(Wrap(op), _ncs);

            Assert.Contains(findings, f => f.Severity == Severity.Blocking && f.OpId == "op-1");
        }

        [Fact]
        public void Blocking_WhenScaleFactorNonPositive()
        {
            var op = new ScaleOp { OpId = "op-1", Base = new Pt(0, 0), Factor = -1 };

            var findings = _lint.Inspect(Wrap(op), _ncs);

            Assert.Contains(findings, f => f.Severity == Severity.Blocking && f.OpId == "op-1");
        }

        [Fact]
        public void Blocking_WhenPolylineHasFewerThanTwoVertices()
        {
            var op = new CreatePolylineOp { OpId = "op-1", Points = { new Pt(0, 0) } };

            var findings = _lint.Inspect(Wrap(op), _ncs);

            Assert.Contains(findings, f => f.Severity == Severity.Blocking && f.OpId == "op-1");
        }

        [Fact]
        public void Blocking_WhenTextHeightNonPositive()
        {
            var op = new CreateTextOp { OpId = "op-1", Contents = "NOTE", Position = new Pt(0, 0), Height = 0 };

            var findings = _lint.Inspect(Wrap(op), _ncs);

            Assert.Contains(findings, f => f.Severity == Severity.Blocking && f.OpId == "op-1");
        }

        [Fact]
        public void ValidGeometry_ProducesNoBlockingFindings()
        {
            var op = new CreateCircleOp { OpId = "op-1", Center = new Pt(0, 0), Radius = 5, Layer = "A-WALL" };

            var findings = _lint.Inspect(Wrap(op), _ncs);

            Assert.DoesNotContain(findings, f => f.Severity == Severity.Blocking);
        }

        [Fact]
        public void NullChangeSet_ReturnsEmpty_NeverThrows()
        {
            var findings = _lint.Inspect(null!, _ncs);
            Assert.Empty(findings);
        }
    }
}
