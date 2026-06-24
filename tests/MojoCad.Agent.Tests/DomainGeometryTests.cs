using System.Collections.Generic;
using MojoCad.Agent.Tools;
using MojoCad.Core.Geometry;
using Xunit;

namespace MojoCad.Agent.Tests
{
    /// <summary>
    /// DomainGeometry backs the domain tools (room areas, sprinkler grids). It's pure 2D maths, so it's
    /// fully deterministic and worth pinning: a wrong point-in-polygon or shoelace area would silently
    /// mis-place life-safety geometry. (DomainGeometry is internal; the test assembly sees it via the
    /// InternalsVisibleTo on MojoCad.Agent.)
    /// </summary>
    public sealed class DomainGeometryTests
    {
        private static readonly List<Pt> UnitSquare = new List<Pt>
        {
            new Pt(0, 0), new Pt(1, 0), new Pt(1, 1), new Pt(0, 1)
        };

        [Fact]
        public void PointInPolygon_TrueForInteriorPoint()
        {
            Assert.True(DomainGeometry.PointInPolygon(new Pt(0.5, 0.5), UnitSquare));
        }

        [Fact]
        public void PointInPolygon_FalseForExteriorPoint()
        {
            Assert.False(DomainGeometry.PointInPolygon(new Pt(2, 2), UnitSquare));
            Assert.False(DomainGeometry.PointInPolygon(new Pt(-0.5, 0.5), UnitSquare));
        }

        [Fact]
        public void PolygonArea_UnitSquare_IsOne()
        {
            Assert.Equal(1.0, DomainGeometry.PolygonArea(UnitSquare), 9);
        }

        [Fact]
        public void PolygonArea_IsOrientationIndependent()
        {
            // Same square wound clockwise - shoelace returns the absolute area either way.
            var cw = new List<Pt> { new Pt(0, 0), new Pt(0, 1), new Pt(1, 1), new Pt(1, 0) };
            Assert.Equal(1.0, DomainGeometry.PolygonArea(cw), 9);
        }

        [Fact]
        public void PolygonArea_NonUnitRectangle()
        {
            var rect = new List<Pt> { new Pt(0, 0), new Pt(4, 0), new Pt(4, 3), new Pt(0, 3) };
            Assert.Equal(12.0, DomainGeometry.PolygonArea(rect), 9);
        }

        [Fact]
        public void SprinklerGrid_FillsRectangle_WithExpectedCount()
        {
            // A 12x12 area, 2-unit spacing, 1-unit wall offset: heads at x,y in {1,3,5,7,9,11} -> 6x6 = 36.
            // The 1-unit offset keeps every grid point strictly interior, so the count is deterministic
            // (even-odd point-in-polygon is only ambiguous exactly on the boundary, which we avoid here).
            var area = new List<Pt> { new Pt(0, 0), new Pt(12, 0), new Pt(12, 12), new Pt(0, 12) };

            var heads = DomainGeometry.SprinklerGrid(area, spacing: 2.0, wallOffset: 1.0, stagger: false);

            Assert.Equal(36, heads.Count);
            // Every head must fall strictly inside the coverage polygon.
            Assert.All(heads, h => Assert.True(DomainGeometry.PointInPolygon(h, area)));
        }

        [Fact]
        public void SprinklerGrid_RespectsWallOffset()
        {
            // 10x10 area, 2-unit spacing, 1-unit wall offset: start at 1, heads at {1,3,5,7,9} -> 5x5 = 25.
            var area = new List<Pt> { new Pt(0, 0), new Pt(10, 0), new Pt(10, 10), new Pt(0, 10) };

            var heads = DomainGeometry.SprinklerGrid(area, spacing: 2.0, wallOffset: 1.0, stagger: false);

            Assert.Equal(25, heads.Count);
        }

        [Fact]
        public void SprinklerGrid_DegenerateInput_ReturnsEmpty()
        {
            var line = new List<Pt> { new Pt(0, 0), new Pt(10, 0) }; // < 3 points
            Assert.Empty(DomainGeometry.SprinklerGrid(line, 2.0, 0.0, false));

            var area = new List<Pt> { new Pt(0, 0), new Pt(10, 0), new Pt(10, 10), new Pt(0, 10) };
            Assert.Empty(DomainGeometry.SprinklerGrid(area, spacing: 0, wallOffset: 0, stagger: false));
        }

        [Fact]
        public void BoundsOf_ReturnsMinAndMaxCorners()
        {
            var pts = new List<Pt> { new Pt(2, -3), new Pt(-1, 4), new Pt(5, 1) };

            var (min, max) = DomainGeometry.BoundsOf(pts);

            Assert.Equal(-1, min.X);
            Assert.Equal(-3, min.Y);
            Assert.Equal(5, max.X);
            Assert.Equal(4, max.Y);
        }
    }
}
