using System;
using System.Collections.Generic;
using MojoCad.Core.Geometry;

namespace MojoCad.Agent.Tools
{
    /// <summary>
    /// Pure-2D helpers backing the domain tools (walls, sprinkler grids, room areas). These produce
    /// editable primitive geometry the engineer reviews; they intentionally favour clarity over CAD-
    /// perfect cleanup (the human is in the loop). All maths is AutoCAD-free.
    /// </summary>
    internal static class DomainGeometry
    {
        /// <summary>
        /// Build a closed wall outline from a centre/face path and width. Returns the outline vertices
        /// (left face forward, right face back). <paramref name="justify"/> shifts the path off centre.
        /// </summary>
        public static List<Pt> WallOutline(IReadOnlyList<Pt> path, double width, string justify)
        {
            double leftDist, rightDist;
            switch (justify)
            {
                case "left": leftDist = 0; rightDist = width; break;
                case "right": leftDist = width; rightDist = 0; break;
                default: leftDist = width / 2.0; rightDist = width / 2.0; break; // center
            }

            var left = OffsetPath(path, leftDist);
            var right = OffsetPath(path, -rightDist);

            var outline = new List<Pt>(left.Count + right.Count);
            outline.AddRange(left);
            for (int i = right.Count - 1; i >= 0; i--)
                outline.Add(right[i]);
            return outline;
        }

        /// <summary>
        /// Offset a polyline path to one side by <paramref name="dist"/> (positive = left of travel),
        /// mitering interior corners. Endpoints use the adjacent segment normal.
        /// </summary>
        public static List<Pt> OffsetPath(IReadOnlyList<Pt> path, double dist)
        {
            int n = path.Count;
            var result = new List<Pt>(n);
            if (n == 0) return result;
            if (n == 1) { result.Add(path[0]); return result; }

            for (int i = 0; i < n; i++)
            {
                Vec? inDir = i > 0 ? Dir(path[i - 1], path[i]) : (Vec?)null;
                Vec? outDir = i < n - 1 ? Dir(path[i], path[i + 1]) : (Vec?)null;

                if (inDir == null) // first vertex
                {
                    var nrm = LeftNormal(outDir!.Value);
                    result.Add(Offset(path[i], nrm, dist));
                }
                else if (outDir == null) // last vertex
                {
                    var nrm = LeftNormal(inDir.Value);
                    result.Add(Offset(path[i], nrm, dist));
                }
                else
                {
                    var n1 = LeftNormal(inDir.Value);
                    var n2 = LeftNormal(outDir.Value);
                    var miter = Normalize(new Vec(n1.X + n2.X, n1.Y + n2.Y));
                    double cos = miter.X * n1.X + miter.Y * n1.Y;
                    double scale = Math.Abs(cos) < 1e-6 ? dist : dist / cos;
                    result.Add(Offset(path[i], miter, scale));
                }
            }
            return result;
        }

        /// <summary>Generate sprinkler-head positions on a grid clipped to a polygon area.</summary>
        public static List<Pt> SprinklerGrid(IReadOnlyList<Pt> area, double spacing, double wallOffset, bool stagger)
        {
            var heads = new List<Pt>();
            if (area.Count < 3 || spacing <= 0) return heads;

            var (min, max) = BoundsOf(area);
            double startX = min.X + wallOffset;
            double startY = min.Y + wallOffset;

            int row = 0;
            for (double y = startY; y <= max.Y - wallOffset + 1e-9; y += spacing, row++)
            {
                double xShift = (stagger && (row % 2 == 1)) ? spacing / 2.0 : 0.0;
                for (double x = startX + xShift; x <= max.X - wallOffset + 1e-9; x += spacing)
                {
                    var p = new Pt(x, y);
                    if (PointInPolygon(p, area))
                        heads.Add(p);
                }
            }
            return heads;
        }

        /// <summary>Standard even-odd ray-casting point-in-polygon test (XY only).</summary>
        public static bool PointInPolygon(Pt p, IReadOnlyList<Pt> poly)
        {
            bool inside = false;
            int n = poly.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double xi = poly[i].X, yi = poly[i].Y;
                double xj = poly[j].X, yj = poly[j].Y;
                bool intersect = ((yi > p.Y) != (yj > p.Y)) &&
                                 (p.X < (xj - xi) * (p.Y - yi) / ((yj - yi) == 0 ? 1e-12 : (yj - yi)) + xi);
                if (intersect) inside = !inside;
            }
            return inside;
        }

        /// <summary>Shoelace area of a closed polygon (absolute value), XY only.</summary>
        public static double PolygonArea(IReadOnlyList<Pt> poly)
        {
            double sum = 0;
            int n = poly.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
                sum += (poly[j].X + poly[i].X) * (poly[j].Y - poly[i].Y);
            return Math.Abs(sum) / 2.0;
        }

        public static (Pt min, Pt max) BoundsOf(IReadOnlyList<Pt> pts)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in pts)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
            return (new Pt(minX, minY), new Pt(maxX, maxY));
        }

        // ----- vector helpers -----
        private static Vec Dir(Pt a, Pt b) => Normalize(new Vec(b.X - a.X, b.Y - a.Y));
        private static Vec LeftNormal(Vec d) => new Vec(-d.Y, d.X);
        private static Pt Offset(Pt p, Vec n, double d) => new Pt(p.X + n.X * d, p.Y + n.Y * d, p.Z);

        private static Vec Normalize(Vec v)
        {
            double len = Math.Sqrt(v.X * v.X + v.Y * v.Y);
            return len < 1e-12 ? new Vec(0, 0) : new Vec(v.X / len, v.Y / len);
        }
    }
}
