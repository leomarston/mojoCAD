using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.GraphicsInterface;
using Autodesk.AutoCAD.Geometry;
using MojoCad.Acad.Interop;
using MojoCad.Core.Changes;
using MojoCad.Core.Ports;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MojoCad.Acad
{
    /// <summary>
    /// Renders a change set as a side-effect-free preview using AutoCAD transient graphics and entity
    /// highlighting - NO database writes. Additive geometry shows green, modified geometry shows an amber
    /// ghost of its new state, doomed (erased) geometry is highlighted red. Reject is free: clearing the
    /// transients and un-highlighting leaves the drawing exactly as it was.
    ///
    /// All methods must run on AutoCAD's main thread (the UI dispatches them there); transient graphics
    /// and highlight are screen-only and need no document lock.
    /// </summary>
    public sealed class TransientPreview : IChangePreviewer
    {
        // ACI colours for the canonical legend.
        private const int Green = 3;   // new
        private const int Amber = 40;  // modified (orange)
        private const int Red = 1;     // deleted
        private const int Gray = 8;    // organisational

        private readonly TransientManager _tm = TransientManager.CurrentTransientManager;
        private readonly Dictionary<string, List<Drawable>> _byOp = new Dictionary<string, List<Drawable>>();
        private readonly List<ObjectId> _highlighted = new List<ObjectId>();

        public Task ShowAsync(ChangeSet changeSet)
        {
            return AcadContext.InvokeAsync(() =>
            {
                ClearCore();
                var doc = AcApp.DocumentManager?.MdiActiveDocument;
                if (doc == null) return;
                var db = doc.Database;

                using var tr = db.TransactionManager.StartTransaction();
                foreach (var op in changeSet.Ops)
                {
                    var drawables = BuildPreview(op, db, tr);
                    if (drawables.Count > 0)
                    {
                        _byOp[op.OpId] = drawables;
                        foreach (var d in drawables)
                            _tm.AddTransient(d, TransientDrawingMode.DirectShortTerm, 128, new IntegerCollection());
                    }
                }
                tr.Commit();
                doc.Editor.UpdateScreen();
            });
        }

        public Task UpdateVisibilityAsync(IReadOnlyCollection<string> visibleOpIds)
        {
            return AcadContext.InvokeAsync(() =>
            {
                var visible = new HashSet<string>(visibleOpIds);
                foreach (var kv in _byOp)
                {
                    bool show = visible.Contains(kv.Key);
                    foreach (var d in kv.Value)
                    {
                        // Toggle by removing/re-adding the transient.
                        _tm.EraseTransient(d, new IntegerCollection());
                        if (show) _tm.AddTransient(d, TransientDrawingMode.DirectShortTerm, 128, new IntegerCollection());
                    }
                }
                AcApp.DocumentManager?.MdiActiveDocument?.Editor.UpdateScreen();
            });
        }

        public Task FlashAsync(string opId, bool zoom)
        {
            return AcadContext.InvokeAsync(() =>
            {
                var doc = AcApp.DocumentManager?.MdiActiveDocument;
                if (doc == null) return;
                if (_byOp.TryGetValue(opId, out var drawables))
                {
                    foreach (var d in drawables) _tm.UpdateTransient(d, new IntegerCollection());
                    if (zoom)
                    {
                        var ext = ExtentsOf(drawables);
                        if (ext != null) ZoomHelper.ZoomToExtents(doc.Editor, ext.Value);
                    }
                }
                doc.Editor.UpdateScreen();
            });
        }

        public Task ClearAsync() => AcadContext.InvokeAsync(ClearCore);

        // ----- internals -------------------------------------------------------------------------

        private void ClearCore()
        {
            foreach (var list in _byOp.Values)
                foreach (var d in list)
                {
                    try { _tm.EraseTransient(d, new IntegerCollection()); } catch { }
                    if (d is IDisposable disp) { try { disp.Dispose(); } catch { } }
                }
            _byOp.Clear();

            if (_highlighted.Count > 0)
            {
                var doc = AcApp.DocumentManager?.MdiActiveDocument;
                if (doc != null)
                {
                    using var tr = doc.Database.TransactionManager.StartTransaction();
                    foreach (var id in _highlighted)
                    {
                        if (id.IsNull) continue;
                        try { if (tr.GetObject(id, OpenMode.ForRead) is Entity e) e.Unhighlight(); } catch { }
                    }
                    tr.Commit();
                }
                _highlighted.Clear();
            }
        }

        /// <summary>Build the transient drawables that preview one op, and highlight any affected originals.</summary>
        private List<Drawable> BuildPreview(ProposedOp op, Database db, Transaction tr)
        {
            var result = new List<Drawable>();
            try
            {
                switch (op.Category)
                {
                    case OpCategory.Additive:
                        foreach (var ent in BuildAdditive(op, db, tr))
                        {
                            Tint(ent, Green);
                            result.Add(ent);
                        }
                        break;

                    case OpCategory.Modify:
                        Highlight(op.TargetHandles, db, tr);
                        foreach (var ghost in BuildModifiedGhosts(op, db, tr))
                        {
                            Tint(ghost, Amber);
                            result.Add(ghost);
                        }
                        break;

                    case OpCategory.Erase:
                        Highlight(op.TargetHandles, db, tr);
                        foreach (var ghost in CloneTargets(op.TargetHandles, db, tr))
                        {
                            Tint(ghost, Red);
                            result.Add(ghost);
                        }
                        break;

                    case OpCategory.Organizational:
                        // No geometry to preview (layer create / set current layer).
                        break;
                }
            }
            catch
            {
                // A preview that can't be built is non-fatal - the row still shows; commit is what matters.
            }
            return result;
        }

        private static List<Entity> BuildAdditive(ProposedOp op, Database db, Transaction tr)
        {
            // Create primitives directly; copy/array/mirror-keep/offset clone+transform their targets.
            switch (op)
            {
                case CopyOp _:
                case ArrayOp _:
                case MirrorOp _:
                    return CloneAndTransform(op, db, tr);
                case OffsetOp off:
                    return OffsetPreview(off, db, tr);
                default:
                    return EntityFactory.BuildCreated(op, db, tr);
            }
        }

        private static List<Entity> BuildModifiedGhosts(ProposedOp op, Database db, Transaction tr)
        {
            var m = EntityFactory.GetSimpleTransform(op);
            var ghosts = CloneTargets(op.TargetHandles, db, tr);
            if (m.HasValue)
                foreach (var g in ghosts) g.TransformBy(m.Value);
            return ghosts;
        }

        private static List<Entity> CloneAndTransform(ProposedOp op, Database db, Transaction tr)
        {
            var output = new List<Entity>();
            var transforms = EntityFactory.GetCopyTransforms(op);
            if (op is MirrorOp mi)
            {
                var mt = EntityFactory.GetSimpleTransform(mi);
                if (mt.HasValue) transforms = new List<Matrix3d> { mt.Value };
            }
            foreach (var src in CloneTargets(op.TargetHandles, db, tr))
            {
                foreach (var t in transforms)
                {
                    var clone = (Entity)src.Clone();
                    clone.TransformBy(t);
                    output.Add(clone);
                }
                src.Dispose(); // the un-transformed clone is not used directly
            }
            return output;
        }

        private static List<Entity> OffsetPreview(OffsetOp off, Database db, Transaction tr)
        {
            var output = new List<Entity>();
            foreach (var h in off.TargetHandles)
            {
                ObjectId id = HandleUtil.Resolve(db, h);
                if (id.IsNull) continue;
                if (tr.GetObject(id, OpenMode.ForRead) is Curve curve)
                {
                    try
                    {
                        foreach (DBObject o in curve.GetOffsetCurves(off.Distance))
                            if (o is Entity e) output.Add(e);
                    }
                    catch { }
                }
            }
            return output;
        }

        private static List<Entity> CloneTargets(IReadOnlyList<string> handles, Database db, Transaction tr)
        {
            var clones = new List<Entity>();
            foreach (var h in handles)
            {
                ObjectId id = HandleUtil.Resolve(db, h);
                if (id.IsNull) continue; // provisional / staged-this-turn handles have no DB entity to clone
                if (tr.GetObject(id, OpenMode.ForRead) is Entity ent)
                {
                    try { clones.Add((Entity)ent.Clone()); } catch { }
                }
            }
            return clones;
        }

        private void Highlight(IReadOnlyList<string> handles, Database db, Transaction tr)
        {
            foreach (var h in handles)
            {
                ObjectId id = HandleUtil.Resolve(db, h);
                if (id.IsNull) continue;
                try
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is Entity ent)
                    {
                        ent.Highlight();
                        _highlighted.Add(id);
                    }
                }
                catch { }
            }
        }

        private static void Tint(Entity ent, int aci)
        {
            try { ent.ColorIndex = aci; } catch { }
        }

        private static Extents3d? ExtentsOf(List<Drawable> drawables)
        {
            Extents3d? ext = null;
            foreach (var d in drawables)
                if (d is Entity e)
                {
                    try
                    {
                        var ee = e.GeometricExtents;
                        if (ext == null) ext = ee; else { var t = ext.Value; t.AddExtents(ee); ext = t; }
                    }
                    catch { }
                }
            return ext;
        }
    }
}
