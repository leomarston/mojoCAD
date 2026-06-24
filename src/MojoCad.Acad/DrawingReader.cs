using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using MojoCad.Acad.Interop;
using MojoCad.Core.Drawing;
using MojoCad.Core.Geometry;
using MojoCad.Core.Ports;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MojoCad.Acad
{
    /// <summary>
    /// Implements <see cref="IAcadBridge"/> - the read side of the AutoCAD seam. Every method marshals to
    /// AutoCAD's main thread (via <see cref="AcadContext"/>) and reads inside a transaction. The agent
    /// calls these to ground itself in the live drawing.
    /// </summary>
    public sealed class DrawingReader : IAcadBridge
    {
        public bool HasActiveDocument => AcApp.DocumentManager?.MdiActiveDocument != null;

        public Task<DrawingSummary> GetDrawingSummaryAsync() => AcadContext.InvokeAsync(BuildSummary);

        private static DrawingSummary BuildSummary()
        {
            var doc = AcApp.DocumentManager?.MdiActiveDocument;
            var summary = new DrawingSummary();
            if (doc == null) return summary;
            var db = doc.Database;

            var unit = UnitsCatalog.Resolve((int)db.Insunits);
            summary.DocumentName = System.IO.Path.GetFileName(doc.Name);
            summary.Units = unit.Label;
            summary.UnitScalePerMeter = unit.PerMeter;
            summary.Precision = db.Luprec;
            summary.Extents = new Bounds(GeometryConvert.ToPt(db.Extmin), GeometryConvert.ToPt(db.Extmax));

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId id in lt)
                {
                    var ltr = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    summary.Layers.Add(new LayerInfo
                    {
                        Name = ltr.Name,
                        ColorIndex = ltr.Color.IsByAci ? ltr.Color.ColorIndex : 256,
                        IsOn = !ltr.IsOff,
                        IsFrozen = ltr.IsFrozen,
                        IsLocked = ltr.IsLocked,
                        Linetype = SafeLinetypeName(tr, ltr.LinetypeObjectId),
                        Description = string.IsNullOrEmpty(ltr.Description) ? null : ltr.Description
                    });
                }

                var clayer = (LayerTableRecord)tr.GetObject(db.Clayer, OpenMode.ForRead);
                summary.CurrentLayer = clayer.Name;

                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId id in bt)
                {
                    var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (!btr.IsLayout && !btr.IsAnonymous && !btr.IsFromExternalReference)
                        summary.BlockDefinitions.Add(btr.Name);
                }

                var tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
                foreach (ObjectId id in tst)
                    summary.TextStyles.Add(((TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name);

                var dst = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
                foreach (ObjectId id in dst)
                    summary.DimStyles.Add(((DimStyleTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name);

                var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                int count = 0;
                foreach (ObjectId _ in ms) count++;
                summary.EntityTotal = count;

                tr.Commit();
            }

            summary.Selection = ReadSelection(doc, db);
            return summary;
        }

        public Task<IReadOnlyList<EntityInfo>> QueryEntitiesAsync(EntityQuery query) =>
            AcadContext.InvokeAsync<IReadOnlyList<EntityInfo>>(() => RunQuery(query));

        private static IReadOnlyList<EntityInfo> RunQuery(EntityQuery query)
        {
            var results = new List<EntityInfo>();
            var doc = AcApp.DocumentManager?.MdiActiveDocument;
            if (doc == null) return results;
            var db = doc.Database;

            var typeSet = query.Types != null && query.Types.Count > 0
                ? new HashSet<string>(query.Types, StringComparer.OrdinalIgnoreCase) : null;

            using var tr = db.TransactionManager.StartTransaction();
            var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
            foreach (ObjectId id in ms)
            {
                if (results.Count >= query.Limit) break;
                var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null) continue;

                string dxf = ent.GetType().Name.ToUpperInvariant();
                string dxfName = ToDxfName(ent);
                if (typeSet != null && !typeSet.Contains(dxfName) && !typeSet.Contains(dxf)) continue;
                if (query.Layer != null && !string.Equals(ent.Layer, query.Layer, StringComparison.OrdinalIgnoreCase)) continue;

                string? blockName = ent is BlockReference br ? BlockRefName(tr, br) : null;
                if (query.BlockName != null && !string.Equals(blockName, query.BlockName, StringComparison.OrdinalIgnoreCase)) continue;

                Bounds bounds;
                try { bounds = GeometryConvert.ToBounds(ent.GeometricExtents); }
                catch { continue; } // some entities have no extents

                if (query.Window.HasValue && !Overlaps(bounds, query.Window.Value)) continue;
                if (query.NearPoint.HasValue && query.Radius.HasValue &&
                    bounds.Center.DistanceTo(query.NearPoint.Value) > query.Radius.Value) continue;

                results.Add(new EntityInfo
                {
                    Handle = HandleUtil.ToHandle(id),
                    Type = dxfName,
                    Layer = ent.Layer,
                    BlockName = blockName,
                    Bounds = bounds,
                    Summary = Summarize(ent, tr)
                });
            }
            tr.Commit();
            return results;
        }

        public Task<IReadOnlyList<EntityDetail>> GetEntityPropertiesAsync(IReadOnlyList<string> handles) =>
            AcadContext.InvokeAsync<IReadOnlyList<EntityDetail>>(() => ReadDetails(handles));

        private static IReadOnlyList<EntityDetail> ReadDetails(IReadOnlyList<string> handles)
        {
            var details = new List<EntityDetail>();
            var doc = AcApp.DocumentManager?.MdiActiveDocument;
            if (doc == null) return details;
            var db = doc.Database;

            using var tr = db.TransactionManager.StartTransaction();
            foreach (var h in handles)
            {
                ObjectId id = HandleUtil.Resolve(db, h);
                if (id.IsNull) continue;
                if (!(tr.GetObject(id, OpenMode.ForRead) is Entity ent)) continue;

                var detail = new EntityDetail
                {
                    Handle = h,
                    Type = ToDxfName(ent),
                    Layer = ent.Layer
                };
                try { detail.Bounds = GeometryConvert.ToBounds(ent.GeometricExtents); } catch { }
                PopulateProperties(ent, tr, detail.Properties);
                details.Add(detail);
            }
            tr.Commit();
            return details;
        }

        public Task<MeasureResult> MeasureAsync(string mode, IReadOnlyList<Pt>? points, IReadOnlyList<string>? handles) =>
            AcadContext.InvokeAsync(() => RunMeasure(mode, points, handles));

        private static MeasureResult RunMeasure(string mode, IReadOnlyList<Pt>? points, IReadOnlyList<string>? handles)
        {
            var doc = AcApp.DocumentManager?.MdiActiveDocument;
            var result = new MeasureResult { Mode = mode };
            if (doc == null) return result;
            var db = doc.Database;
            result.Units = UnitsCatalog.Resolve((int)db.Insunits).Label;

            switch (mode)
            {
                case "distance":
                    if (points != null && points.Count >= 2)
                        result.Value = points[0].DistanceTo(points[1]);
                    break;
                case "angle":
                    if (points != null && points.Count >= 3)
                        result.Value = AngleBetween(points[0], points[1], points[2]);
                    break;
                case "area":
                case "total_length":
                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        double acc = 0;
                        if (handles != null)
                        {
                            foreach (var h in handles)
                            {
                                ObjectId id = HandleUtil.Resolve(db, h);
                                if (id.IsNull) continue;
                                var ent = tr.GetObject(id, OpenMode.ForRead);
                                if (mode == "area" && ent is Curve cv) { try { acc += cv.Area; } catch { } }
                                else if (mode == "total_length" && ent is Curve c2)
                                {
                                    try { acc += c2.GetDistanceAtParameter(c2.EndParam) - c2.GetDistanceAtParameter(c2.StartParam); } catch { }
                                }
                                else if (mode == "area" && ent is Hatch ht) { try { acc += ht.Area; } catch { } }
                            }
                        }
                        else if (mode == "area" && points != null && points.Count >= 3)
                        {
                            acc = ShoelaceArea(points);
                        }
                        result.Value = acc;
                        tr.Commit();
                    }
                    break;
            }
            return result;
        }

        public Task<IReadOnlyList<string>> FilterExistingHandlesAsync(IReadOnlyList<string> handles) =>
            AcadContext.InvokeAsync<IReadOnlyList<string>>(() =>
            {
                var doc = AcApp.DocumentManager?.MdiActiveDocument;
                if (doc == null) return Array.Empty<string>();
                var db = doc.Database;
                return handles.Where(h => !HandleUtil.Resolve(db, h).IsNull).ToList();
            });

        public Task<bool> LayerExistsAsync(string name) => AcadContext.InvokeAsync(() =>
        {
            var doc = AcApp.DocumentManager?.MdiActiveDocument;
            if (doc == null) return false;
            using var tr = doc.Database.TransactionManager.StartTransaction();
            var lt = (LayerTable)tr.GetObject(doc.Database.LayerTableId, OpenMode.ForRead);
            bool has = lt.Has(name);
            tr.Commit();
            return has;
        });

        public Task<bool> BlockExistsAsync(string name) => AcadContext.InvokeAsync(() =>
        {
            var doc = AcApp.DocumentManager?.MdiActiveDocument;
            if (doc == null) return false;
            using var tr = doc.Database.TransactionManager.StartTransaction();
            var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
            bool has = bt.Has(name);
            tr.Commit();
            return has;
        });

        public Task<IReadOnlyList<string>> GetCurrentSelectionAsync() =>
            AcadContext.InvokeAsync<IReadOnlyList<string>>(() =>
            {
                var doc = AcApp.DocumentManager?.MdiActiveDocument;
                if (doc == null) return Array.Empty<string>();
                return ReadSelection(doc, doc.Database).Select(e => e.Handle).ToList();
            });

        public Task ZoomToHandlesAsync(IReadOnlyList<string> handles) => AcadContext.InvokeAsync(() =>
        {
            var doc = AcApp.DocumentManager?.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;
            Extents3d? ext = null;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (var h in handles)
                {
                    ObjectId id = HandleUtil.Resolve(db, h);
                    if (id.IsNull) continue;
                    if (tr.GetObject(id, OpenMode.ForRead) is Entity ent)
                    {
                        try
                        {
                            var e = ent.GeometricExtents;
                            if (ext == null) ext = e; else { var t = ext.Value; t.AddExtents(e); ext = t; }
                        }
                        catch { }
                    }
                }
                tr.Commit();
            }
            if (ext != null) ZoomHelper.ZoomToExtents(doc.Editor, ext.Value);
        });

        // ----- helpers ---------------------------------------------------------------------------

        private static List<EntityInfo> ReadSelection(Autodesk.AutoCAD.ApplicationServices.Document doc, Database db)
        {
            var list = new List<EntityInfo>();
            PromptSelectionResult psr = doc.Editor.SelectImplied();
            if (psr.Status != PromptStatus.OK || psr.Value == null) return list;

            using var tr = db.TransactionManager.StartTransaction();
            foreach (SelectedObject so in psr.Value)
            {
                if (so == null) continue;
                if (!(tr.GetObject(so.ObjectId, OpenMode.ForRead) is Entity ent)) continue;
                Bounds b = default;
                try { b = GeometryConvert.ToBounds(ent.GeometricExtents); } catch { }
                list.Add(new EntityInfo
                {
                    Handle = HandleUtil.ToHandle(so.ObjectId),
                    Type = ToDxfName(ent),
                    Layer = ent.Layer,
                    Bounds = b,
                    Summary = Summarize(ent, tr)
                });
            }
            tr.Commit();
            return list;
        }

        private static string ToDxfName(Entity ent)
        {
            // DXF class name is the most model-friendly type label.
            try { return ent.GetRXClass().DxfName.ToUpperInvariant(); }
            catch { return ent.GetType().Name.ToUpperInvariant(); }
        }

        private static string? BlockRefName(Transaction tr, BlockReference br)
        {
            try
            {
                var btr = (BlockTableRecord)tr.GetObject(br.DynamicBlockTableRecord, OpenMode.ForRead);
                return btr.Name;
            }
            catch { return null; }
        }

        private static string Summarize(Entity ent, Transaction tr)
        {
            switch (ent)
            {
                case Polyline pl:
                    return $"polyline, {pl.NumberOfVertices} verts{(pl.Closed ? ", closed" : "")}, len {pl.Length:0.##}";
                case Line ln:
                    return $"line, len {ln.Length:0.##}";
                case Circle c:
                    return $"circle, r {c.Radius:0.##}";
                case Arc a:
                    return $"arc, r {a.Radius:0.##}";
                case DBText t:
                    return $"text \"{Trunc(t.TextString)}\"";
                case MText mt:
                    return $"mtext \"{Trunc(mt.Text)}\"";
                case BlockReference br:
                    return $"block '{BlockRefName(tr, br)}'";
                case Hatch h:
                    return $"hatch '{h.PatternName}', area {SafeArea(h):0.##}";
                case Dimension d:
                    return $"dimension '{d.DimensionText}'";
                default:
                    return ent.GetType().Name;
            }
        }

        private static void PopulateProperties(Entity ent, Transaction tr, Dictionary<string, object?> p)
        {
            switch (ent)
            {
                case Polyline pl:
                    var verts = new List<double[]>();
                    for (int i = 0; i < pl.NumberOfVertices; i++)
                    {
                        var pt = pl.GetPoint3dAt(i);
                        verts.Add(new[] { Math.Round(pt.X, 6), Math.Round(pt.Y, 6) });
                    }
                    p["vertices"] = verts;
                    p["closed"] = pl.Closed;
                    p["length"] = pl.Length;
                    break;
                case Line ln:
                    p["start"] = new[] { ln.StartPoint.X, ln.StartPoint.Y };
                    p["end"] = new[] { ln.EndPoint.X, ln.EndPoint.Y };
                    p["length"] = ln.Length;
                    break;
                case Circle c:
                    p["center"] = new[] { c.Center.X, c.Center.Y };
                    p["radius"] = c.Radius;
                    break;
                case Arc a:
                    p["center"] = new[] { a.Center.X, a.Center.Y };
                    p["radius"] = a.Radius;
                    p["start_angle_deg"] = a.StartAngle * Angles.RadToDeg;
                    p["end_angle_deg"] = a.EndAngle * Angles.RadToDeg;
                    break;
                case DBText t:
                    p["contents"] = t.TextString;
                    p["height"] = t.Height;
                    p["position"] = new[] { t.Position.X, t.Position.Y };
                    p["rotation_deg"] = t.Rotation * Angles.RadToDeg;
                    break;
                case MText mt:
                    p["contents"] = mt.Contents;
                    p["height"] = mt.TextHeight;
                    p["location"] = new[] { mt.Location.X, mt.Location.Y };
                    break;
                case BlockReference br:
                    p["block_name"] = BlockRefName(tr, br);
                    p["position"] = new[] { br.Position.X, br.Position.Y };
                    p["rotation_deg"] = br.Rotation * Angles.RadToDeg;
                    p["scale"] = br.ScaleFactors.X;
                    break;
            }
        }

        private static bool Overlaps(Bounds a, Bounds w) =>
            a.Min.X <= w.Max.X && a.Max.X >= w.Min.X && a.Min.Y <= w.Max.Y && a.Max.Y >= w.Min.Y;

        private static double AngleBetween(Pt a, Pt vertex, Pt b)
        {
            double a1 = Math.Atan2(a.Y - vertex.Y, a.X - vertex.X);
            double a2 = Math.Atan2(b.Y - vertex.Y, b.X - vertex.X);
            double deg = Math.Abs((a2 - a1) * Angles.RadToDeg);
            return deg > 180 ? 360 - deg : deg;
        }

        private static double ShoelaceArea(IReadOnlyList<Pt> pts)
        {
            double sum = 0;
            int n = pts.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
                sum += (pts[j].X + pts[i].X) * (pts[j].Y - pts[i].Y);
            return Math.Abs(sum) / 2.0;
        }

        private static double SafeArea(Hatch h) { try { return h.Area; } catch { return 0; } }

        private static string? SafeLinetypeName(Transaction tr, ObjectId ltId)
        {
            try { return ((LinetypeTableRecord)tr.GetObject(ltId, OpenMode.ForRead)).Name; }
            catch { return null; }
        }

        private static string Trunc(string s) => s == null ? "" : (s.Length <= 30 ? s : s.Substring(0, 30) + "…");
    }
}
