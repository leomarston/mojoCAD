using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MojoCad.Acad.Interop;
using MojoCad.Core.Changes;
using MojoCad.Core.Geometry;

namespace MojoCad.Acad
{
    /// <summary>
    /// Translates additive <see cref="ProposedOp"/>s into detached AutoCAD <see cref="Entity"/> objects
    /// (used identically for transient preview and for commit) and computes the transforms for edit ops.
    /// Detached entities are NOT owned by the database until appended; the caller disposes any entity it
    /// builds but does not append.
    /// </summary>
    internal static class EntityFactory
    {
        /// <summary>
        /// Build the detached entities an additive op creates (polyline, circle, arc, text, block, ...).
        /// Returns an empty list for non-additive ops. <paramref name="db"/>/<paramref name="tr"/> are used
        /// to resolve layers and existing references; pass the active document's database.
        /// </summary>
        public static List<Entity> BuildCreated(ProposedOp op, Database db, Transaction tr)
        {
            var entities = new List<Entity>();
            switch (op)
            {
                case CreatePolylineOp p:
                    entities.Add(BuildPolyline(p.Points, p.Closed, p.Bulges, p.GlobalWidth));
                    break;

                case CreateCircleOp c:
                    entities.Add(new Circle(GeometryConvert.ToPoint3d(c.Center), Vector3d.ZAxis, c.Radius));
                    break;

                case CreateArcOp a:
                    entities.Add(new Arc(GeometryConvert.ToPoint3d(a.Center), a.Radius,
                        GeometryConvert.DegToRad(a.StartAngleDeg), GeometryConvert.DegToRad(a.EndAngleDeg)));
                    break;

                case CreateRectangleOp r:
                    entities.Add(BuildRectangle(r));
                    break;

                case CreateEllipseOp e:
                    entities.Add(new Ellipse(GeometryConvert.ToPoint3d(e.Center), Vector3d.ZAxis,
                        GeometryConvert.ToVector3d(e.MajorAxis), e.Ratio, 0, 2 * Math.PI));
                    break;

                case CreateTextOp t:
                    entities.Add(BuildText(t, db, tr));
                    break;

                case InsertBlockOp b:
                    entities.Add(BuildBlockRef(b, db, tr));
                    break;

                case CreateHatchOp h:
                    // Hatch must be appended to a BlockTableRecord before loops are added, so it is built at
                    // commit time (see ChangeApplier). For preview we show the boundary outline instead.
                    if (h.BoundaryPoints != null && h.BoundaryPoints.Count >= 3)
                        entities.Add(BuildPolyline(h.BoundaryPoints, true, null, null));
                    break;

                case CreateDimensionOp d:
                    var dim = BuildDimension(d, db, tr);
                    if (dim != null) entities.Add(dim);
                    break;
            }

            foreach (var ent in entities)
                ApplyProperties(ent, op, db, tr);

            return entities;
        }

        // ----- builders --------------------------------------------------------------------------

        public static Polyline BuildPolyline(IReadOnlyList<Pt> pts, bool closed, IReadOnlyList<double>? bulges, double? width)
        {
            var pl = new Polyline();
            for (int i = 0; i < pts.Count; i++)
            {
                double bulge = (bulges != null && i < bulges.Count) ? bulges[i] : 0.0;
                pl.AddVertexAt(i, GeometryConvert.ToPoint2d(pts[i]), bulge, 0, 0);
            }
            pl.Closed = closed;
            if (width.HasValue && width.Value > 0)
                pl.ConstantWidth = width.Value;
            return pl;
        }

        private static Polyline BuildRectangle(CreateRectangleOp r)
        {
            double x1 = r.Corner1.X, y1 = r.Corner1.Y, x2 = r.Corner2.X, y2 = r.Corner2.Y;
            var pts = new List<Pt>
            {
                new Pt(x1, y1), new Pt(x2, y1), new Pt(x2, y2), new Pt(x1, y2)
            };
            var pl = BuildPolyline(pts, true, null, null);
            if (Math.Abs(r.RotationDeg) > 1e-9)
            {
                var m = Matrix3d.Rotation(GeometryConvert.DegToRad(r.RotationDeg), Vector3d.ZAxis, GeometryConvert.ToPoint3d(r.Corner1));
                pl.TransformBy(m);
            }
            return pl;
        }

        private static Entity BuildText(CreateTextOp t, Database db, Transaction tr)
        {
            if (t.IsMText)
            {
                var mt = new MText
                {
                    Contents = t.Contents,
                    Location = GeometryConvert.ToPoint3d(t.Position),
                    TextHeight = t.Height,
                    Rotation = GeometryConvert.DegToRad(t.RotationDeg)
                };
                if (t.Width.HasValue) mt.Width = t.Width.Value;
                SetTextStyle(mt, t.Style, db, tr);
                return mt;
            }

            var dbt = new DBText
            {
                TextString = t.Contents,
                Position = GeometryConvert.ToPoint3d(t.Position),
                Height = t.Height,
                Rotation = GeometryConvert.DegToRad(t.RotationDeg)
            };
            switch (t.Justify)
            {
                case TextJustify.Center: dbt.Justify = AttachmentPoint.MiddleCenter; break;
                case TextJustify.Right: dbt.Justify = AttachmentPoint.BaseRight; break;
                case TextJustify.Middle: dbt.Justify = AttachmentPoint.MiddleCenter; break;
                default: dbt.Justify = AttachmentPoint.BaseLeft; break;
            }
            if (dbt.Justify != AttachmentPoint.BaseLeft)
                dbt.AlignmentPoint = GeometryConvert.ToPoint3d(t.Position);
            SetTextStyle(dbt, t.Style, db, tr);
            return dbt;
        }

        private static BlockReference BuildBlockRef(InsertBlockOp b, Database db, Transaction tr)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            if (!bt.Has(b.BlockName))
                throw new InvalidOperationException($"Block '{b.BlockName}' not found.");
            ObjectId defId = bt[b.BlockName];
            var br = new BlockReference(GeometryConvert.ToPoint3d(b.Position), defId)
            {
                Rotation = GeometryConvert.DegToRad(b.RotationDeg),
                ScaleFactors = new Scale3d(b.Scale == 0 ? 1 : b.Scale)
            };
            return br;
        }

        private static Entity? BuildDimension(CreateDimensionOp d, Database db, Transaction tr)
        {
            Point3d p1 = d.P1.HasValue ? GeometryConvert.ToPoint3d(d.P1.Value) : Point3d.Origin;
            Point3d p2 = d.P2.HasValue ? GeometryConvert.ToPoint3d(d.P2.Value) : Point3d.Origin;
            Point3d line = d.LineLocation.HasValue ? GeometryConvert.ToPoint3d(d.LineLocation.Value) : p2;
            string? txt = string.IsNullOrEmpty(d.TextOverride) ? null : d.TextOverride;

            switch (d.DimKind)
            {
                case DimensionKind.Linear:
                    return new RotatedDimension(0, p1, p2, line, txt, db.Dimstyle);
                case DimensionKind.Aligned:
                    return new AlignedDimension(p1, p2, line, txt, db.Dimstyle);
                case DimensionKind.Radial:
                    return new RadialDimension(p1, p2, 0, txt, db.Dimstyle);
                case DimensionKind.Diameter:
                    return new DiametricDimension(p1, p2, 0, txt, db.Dimstyle);
                case DimensionKind.Angular:
                    // Angular needs three points; without them we skip rather than guess.
                    return null;
                default:
                    return null;
            }
        }

        // ----- transforms for edit ops -----------------------------------------------------------

        /// <summary>The single transform for move/rotate/scale/mirror. Null for ops needing per-copy handling.</summary>
        public static Matrix3d? GetSimpleTransform(ProposedOp op)
        {
            switch (op)
            {
                case MoveOp m:
                    return Matrix3d.Displacement(GeometryConvert.ToPoint3d(m.From).GetVectorTo(GeometryConvert.ToPoint3d(m.To)));
                case RotateOp r:
                    return Matrix3d.Rotation(GeometryConvert.DegToRad(r.AngleDeg), Vector3d.ZAxis, GeometryConvert.ToPoint3d(r.Base));
                case ScaleOp s:
                    return Matrix3d.Scaling(s.Factor, GeometryConvert.ToPoint3d(s.Base));
                case MirrorOp mi:
                    return Matrix3d.Mirroring(new Line3d(GeometryConvert.ToPoint3d(mi.AxisP1), GeometryConvert.ToPoint3d(mi.AxisP2)));
                default:
                    return null;
            }
        }

        /// <summary>The list of displacement/rotation transforms a copy/array produces (one entity each).</summary>
        public static List<Matrix3d> GetCopyTransforms(ProposedOp op)
        {
            var list = new List<Matrix3d>();
            switch (op)
            {
                case CopyOp c:
                {
                    var step = GeometryConvert.ToPoint3d(c.From).GetVectorTo(GeometryConvert.ToPoint3d(c.To));
                    for (int i = 1; i <= Math.Max(1, c.Count); i++)
                        list.Add(Matrix3d.Displacement(step * i));
                    break;
                }
                case ArrayOp a when a.ArrayKind == ArrayKind.Rectangular:
                {
                    for (int row = 0; row < Math.Max(1, a.Rows); row++)
                        for (int col = 0; col < Math.Max(1, a.Cols); col++)
                        {
                            if (row == 0 && col == 0) continue; // original stays
                            list.Add(Matrix3d.Displacement(new Vector3d(col * a.ColSpacing, row * a.RowSpacing, 0)));
                        }
                    break;
                }
                case ArrayOp a when a.ArrayKind == ArrayKind.Polar && a.Center.HasValue:
                {
                    int n = Math.Max(1, a.Count);
                    double fill = GeometryConvert.DegToRad(a.AngleFillDeg);
                    var center = GeometryConvert.ToPoint3d(a.Center.Value);
                    for (int i = 1; i < n; i++)
                    {
                        double ang = fill * i / (Math.Abs(a.AngleFillDeg - 360.0) < 1e-6 ? n : (n - 1));
                        list.Add(Matrix3d.Rotation(ang, Vector3d.ZAxis, center));
                    }
                    break;
                }
            }
            return list;
        }

        // ----- properties ------------------------------------------------------------------------

        /// <summary>Apply the op's layer and (discouraged) property overrides to a built entity, BYLAYER otherwise.</summary>
        public static void ApplyProperties(Entity ent, ProposedOp op, Database db, Transaction tr)
        {
            if (!string.IsNullOrWhiteSpace(op.Layer) && LayerExists(op.Layer!, db, tr))
                ent.Layer = op.Layer!;

            if (op.Props != null)
            {
                if (op.Props.TrueColor.HasValue)
                {
                    int rgb = op.Props.TrueColor.Value;
                    ent.Color = Color.FromRgb((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
                }
                else if (op.Props.ColorIndex.HasValue)
                {
                    ent.ColorIndex = op.Props.ColorIndex.Value;
                }
                if (op.Props.LineweightHundredthsMm.HasValue)
                    ent.LineWeight = MapLineWeight(op.Props.LineweightHundredthsMm.Value);
                if (!string.IsNullOrWhiteSpace(op.Props.Linetype) && LinetypeExists(op.Props.Linetype!, db, tr))
                    ent.Linetype = op.Props.Linetype!;
            }
        }

        public static bool LayerExists(string name, Database db, Transaction tr)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            return lt.Has(name);
        }

        private static bool LinetypeExists(string name, Database db, Transaction tr)
        {
            var ltt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
            return ltt.Has(name);
        }

        private static void SetTextStyle(Entity ent, string? styleName, Database db, Transaction tr)
        {
            if (string.IsNullOrWhiteSpace(styleName)) return;
            var tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            if (!tst.Has(styleName)) return;
            ObjectId id = tst[styleName];
            if (ent is DBText dbt) dbt.TextStyleId = id;
            else if (ent is MText mt) mt.TextStyleId = id;
        }

        private static LineWeight MapLineWeight(int hundredthsMm)
        {
            // Snap to the nearest standard AutoCAD lineweight enum value.
            int[] standard = { 0, 5, 9, 13, 15, 18, 20, 25, 30, 35, 40, 50, 53, 60, 70, 80, 90, 100, 106, 120, 140, 158, 200, 211 };
            int best = standard[0];
            int bestDiff = int.MaxValue;
            foreach (int s in standard)
            {
                int diff = Math.Abs(s - hundredthsMm);
                if (diff < bestDiff) { bestDiff = diff; best = s; }
            }
            return (LineWeight)best;
        }
    }
}
