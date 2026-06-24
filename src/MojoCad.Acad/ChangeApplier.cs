using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using MojoCad.Acad.Interop;
using MojoCad.Core.Changes;
using MojoCad.Core.Ports;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MojoCad.Acad
{
    /// <summary>
    /// Commits the accepted subset of a change set to the drawing. The entire apply runs in command
    /// context (via <c>ExecuteInCommandContextAsync</c>), under an explicit document lock, inside ONE
    /// transaction - so it commits as a single Ctrl+Z step and is strictly all-or-nothing: if any op
    /// throws, the transaction is abandoned and nothing is committed.
    /// </summary>
    public sealed class ChangeApplier : IChangeApplier
    {
        public Task<ApplyResult> ApplyAsync(ChangeSet changeSet, string undoLabel)
        {
            var accepted = changeSet.AcceptedOps.ToList();
            var tcs = new TaskCompletionSource<ApplyResult>();

            var dm = AcApp.DocumentManager;
            if (dm?.MdiActiveDocument == null)
                return Task.FromResult(ApplyResult.Failed("No active AutoCAD document."));

            // Marshal onto the main thread in command context so locking/transactions are valid.
            dm.ExecuteInCommandContextAsync(_ =>
            {
                var result = new ApplyResult { UndoLabel = undoLabel };
                var doc = dm.MdiActiveDocument;

                // Raw-command ops can't run inside our transaction, so they take a different path.
                var commandOps = accepted.OfType<RunCommandOp>().ToList();
                var dbOps = accepted.Where(o => !(o is RunCommandOp)).ToList();

                try
                {
                    using (doc.LockDocument())
                    {
                        if (commandOps.Count == 0)
                        {
                            // Common case: everything is one transaction => one Ctrl+Z step.
                            CommitDbOps(doc, dbOps, result);
                        }
                        else
                        {
                            // Mixed/command case: bracket in an UNDO group so it still undoes as one step.
                            ApplyMixed(doc, dbOps, commandOps, result);
                        }
                        result.Success = true;
                    }
                }
                catch (Exception ex)
                {
                    // Full rollback. Nothing stays committed, so every accepted op is marked Failed.
                    result.Success = false;
                    result.Error = ex.Message;
                    result.AppliedOpIds.Clear();
                    result.FailedOpIds.Clear();
                    foreach (var op in accepted)
                    {
                        op.State = ChangeState.Failed;
                        op.FailureReason = ex.Message;
                        result.FailedOpIds.Add(op.OpId);
                    }
                }

                tcs.TrySetResult(result);
                return Task.CompletedTask;
            }, null);

            return tcs.Task;
        }

        public Task UndoLastAsync()
        {
            return AcadContext.InvokeAsync(() =>
            {
                var doc = AcApp.DocumentManager?.MdiActiveDocument;
                doc?.SendStringToExecute("_.U ", true, false, false);
            });
        }

        // ----- apply strategies ------------------------------------------------------------------

        /// <summary>Apply all (non-command) ops in a single transaction - the whole commit is one undo step.</summary>
        private static void CommitDbOps(Document doc, List<ProposedOp> dbOps, ApplyResult result)
        {
            using var tr = doc.Database.TransactionManager.StartTransaction();
            var db = doc.Database;
            var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
            var opResults = new Dictionary<string, List<ObjectId>>(StringComparer.OrdinalIgnoreCase);

            foreach (var op in dbOps)
            {
                var created = ApplyOne(op, db, tr, ms, opResults);
                op.ResultHandles.Clear();
                foreach (var id in created) op.ResultHandles.Add(HandleUtil.ToHandle(id));
                opResults[op.OpId] = created;
                opResults["@" + op.OpId] = created;
                op.State = ChangeState.Applied;
                result.AppliedOpIds.Add(op.OpId);
            }
            tr.Commit();
        }

        /// <summary>
        /// EXPERIMENTAL command path: structured ops commit first (their own transaction), then each raw
        /// AutoCAD command runs via the Editor, all wrapped in a single UNDO group so the accept is still
        /// one Ctrl+Z step. On any failure the group is undone and the exception propagates to the caller's
        /// rollback handler. Requires real-AutoCAD validation before relying on it for production work.
        /// </summary>
        private static void ApplyMixed(Document doc, List<ProposedOp> dbOps, List<RunCommandOp> commandOps, ApplyResult result)
        {
            var ed = doc.Editor;
            ed.Command("._UNDO", "_Begin");
            bool ok = false;
            try
            {
                if (dbOps.Count > 0) CommitDbOps(doc, dbOps, result);

                foreach (var cmd in commandOps)
                {
                    RunOneCommand(doc, ed, cmd);
                    cmd.State = ChangeState.Applied;
                    result.AppliedOpIds.Add(cmd.OpId);
                }
                ok = true;
            }
            finally
            {
                ed.Command("._UNDO", "_End");
                if (!ok)
                {
                    // Roll the whole group back so a partial command run leaves nothing behind.
                    try { ed.Command("._U"); } catch { /* best effort */ }
                }
            }
        }

        private static void RunOneCommand(Document doc, Editor ed, RunCommandOp cmd)
        {
            // Pre-select the target entities so the command can consume them (PICKFIRST) or via "P"/"L".
            if (cmd.TargetHandles.Count > 0)
            {
                var ids = cmd.TargetHandles
                    .Select(h => HandleUtil.Resolve(doc.Database, h))
                    .Where(id => !id.IsNull)
                    .ToArray();
                if (ids.Length > 0) ed.SetImpliedSelection(ids);
            }

            var tokens = new List<object>(cmd.Inputs.Count + 1) { cmd.CommandName };
            foreach (var input in cmd.Inputs) tokens.Add(input);
            ed.Command(tokens.ToArray());
        }

        // ----- per-op apply ----------------------------------------------------------------------

        private static List<ObjectId> ApplyOne(ProposedOp op, Database db, Transaction tr, BlockTableRecord ms,
            Dictionary<string, List<ObjectId>> opResults)
        {
            switch (op)
            {
                case CreateLayerOp layer:
                    return new List<ObjectId> { EnsureLayer(layer, db, tr) };

                case SetCurrentLayerOp setLayer:
                    SetCurrentLayer(setLayer.Name, db, tr);
                    return new List<ObjectId>();

                case CreateHatchOp hatch:
                    return AppendHatch(hatch, db, tr, ms, opResults);

                case EraseOp erase:
                    foreach (var id in ResolveTargets(erase, db, opResults))
                        if (tr.GetObject(id, OpenMode.ForWrite) is Entity e) e.Erase();
                    return new List<ObjectId>();

                case PropertyChangeOp prop:
                    return ApplyPropertyChange(prop, db, tr, opResults);

                case MoveOp _:
                case RotateOp _:
                case ScaleOp _:
                    return TransformInPlace(op, db, tr, opResults);

                case MirrorOp mirror:
                    return mirror.KeepOriginal
                        ? AppendTransformedClones(op, db, tr, ms, opResults)
                        : TransformInPlace(op, db, tr, opResults);

                case CopyOp _:
                case ArrayOp _:
                    return AppendTransformedClones(op, db, tr, ms, opResults);

                case OffsetOp offset:
                    return AppendOffsets(offset, db, tr, ms, opResults);

                default:
                    // Additive create primitives (polyline/circle/arc/rect/ellipse/text/block/dimension).
                    return AppendCreated(op, db, tr, ms);
            }
        }

        private static List<ObjectId> AppendCreated(ProposedOp op, Database db, Transaction tr, BlockTableRecord ms)
        {
            var ids = new List<ObjectId>();
            foreach (var ent in EntityFactory.BuildCreated(op, db, tr))
            {
                ObjectId id = ms.AppendEntity(ent);
                tr.AddNewlyCreatedDBObject(ent, true);
                ids.Add(id);
            }
            return ids;
        }

        private static List<ObjectId> AppendTransformedClones(ProposedOp op, Database db, Transaction tr,
            BlockTableRecord ms, Dictionary<string, List<ObjectId>> opResults)
        {
            var ids = new List<ObjectId>();
            var transforms = op is MirrorOp m && m.KeepOriginal
                ? new List<Matrix3d> { EntityFactory.GetSimpleTransform(m) ?? Matrix3d.Identity }
                : EntityFactory.GetCopyTransforms(op);

            foreach (var srcId in ResolveTargets(op, db, opResults))
            {
                if (!(tr.GetObject(srcId, OpenMode.ForRead) is Entity src)) continue;
                foreach (var t in transforms)
                {
                    var clone = (Entity)src.Clone();
                    clone.TransformBy(t);
                    ObjectId id = ms.AppendEntity(clone);
                    tr.AddNewlyCreatedDBObject(clone, true);
                    ids.Add(id);
                }
            }
            return ids;
        }

        private static List<ObjectId> TransformInPlace(ProposedOp op, Database db, Transaction tr,
            Dictionary<string, List<ObjectId>> opResults)
        {
            var matrix = EntityFactory.GetSimpleTransform(op) ?? Matrix3d.Identity;
            var ids = ResolveTargets(op, db, opResults);
            foreach (var id in ids)
                if (tr.GetObject(id, OpenMode.ForWrite) is Entity ent)
                    ent.TransformBy(matrix);
            return ids; // these entities were modified, not created
        }

        private static List<ObjectId> AppendOffsets(OffsetOp off, Database db, Transaction tr,
            BlockTableRecord ms, Dictionary<string, List<ObjectId>> opResults)
        {
            var ids = new List<ObjectId>();
            foreach (var srcId in ResolveTargets(off, db, opResults))
            {
                if (!(tr.GetObject(srcId, OpenMode.ForRead) is Curve curve)) continue;
                double dist = ChooseOffsetSign(curve, off);
                try
                {
                    foreach (DBObject o in curve.GetOffsetCurves(dist))
                    {
                        if (o is Entity e)
                        {
                            e.SetPropertiesFrom(curve);
                            ObjectId id = ms.AppendEntity(e);
                            tr.AddNewlyCreatedDBObject(e, true);
                            ids.Add(id);
                        }
                    }
                }
                catch { /* a curve that cannot be offset is skipped */ }
            }
            return ids;
        }

        private static double ChooseOffsetSign(Curve curve, OffsetOp off)
        {
            // Positive offsets one way, negative the other. Pick the side nearer the 'side' point if given.
            if (off.Side.HasValue)
            {
                try
                {
                    var sidePt = GeometryConvert.ToPoint3d(off.Side.Value);
                    var near = curve.GetClosestPointTo(sidePt, false);
                    var plusObjs = curve.GetOffsetCurves(off.Distance);
                    foreach (DBObject o in plusObjs)
                        if (o is Curve pc)
                        {
                            bool plusIsCloser = pc.GetClosestPointTo(sidePt, false).DistanceTo(sidePt)
                                                <= near.DistanceTo(sidePt);
                            pc.Dispose();
                            return plusIsCloser ? off.Distance : -off.Distance;
                        }
                }
                catch { }
            }
            return off.Distance;
        }

        private static List<ObjectId> ApplyPropertyChange(PropertyChangeOp prop, Database db, Transaction tr,
            Dictionary<string, List<ObjectId>> opResults)
        {
            var ids = ResolveTargets(prop, db, opResults);
            foreach (var id in ids)
            {
                if (!(tr.GetObject(id, OpenMode.ForWrite) is Entity ent)) continue;
                if (!string.IsNullOrWhiteSpace(prop.NewLayer) && EntityFactory.LayerExists(prop.NewLayer!, db, tr))
                    ent.Layer = prop.NewLayer!;
                if (prop.NewProps != null)
                {
                    var carrier = new ProxyOp { Props = prop.NewProps };
                    EntityFactory.ApplyProperties(ent, carrier, db, tr);
                }
            }
            return ids;
        }

        // ----- helpers ---------------------------------------------------------------------------

        private static ObjectId EnsureLayer(CreateLayerOp op, Database db, Transaction tr)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(op.Name))
                return lt[op.Name];

            lt.UpgradeOpen();
            var ltr = new LayerTableRecord { Name = op.Name };
            if (op.ColorIndex.HasValue)
                ltr.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, (short)op.ColorIndex.Value);
            if (!string.IsNullOrWhiteSpace(op.Description))
                ltr.Description = op.Description;
            ObjectId id = lt.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);
            return id;
        }

        private static void SetCurrentLayer(string name, Database db, Transaction tr)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(name)) db.Clayer = lt[name];
        }

        private static List<ObjectId> AppendHatch(CreateHatchOp op, Database db, Transaction tr,
            BlockTableRecord ms, Dictionary<string, List<ObjectId>> opResults)
        {
            var hatch = new Hatch();
            EntityFactory.ApplyProperties(hatch, op, db, tr);
            hatch.SetHatchPattern(HatchPatternType.PreDefined, string.IsNullOrEmpty(op.Pattern) ? "SOLID" : op.Pattern);
            hatch.PatternScale = op.Scale <= 0 ? 1.0 : op.Scale;
            hatch.PatternAngle = GeometryConvert.DegToRad(op.AngleDeg);

            ObjectId hatchId = ms.AppendEntity(hatch);
            tr.AddNewlyCreatedDBObject(hatch, true);
            hatch.Associative = false;

            if (op.BoundaryIds != null && op.BoundaryIds.Count > 0)
            {
                var loopIds = new ObjectIdCollection();
                foreach (var h in op.BoundaryIds)
                {
                    ObjectId id = ResolveOne(h, db, opResults);
                    if (!id.IsNull) loopIds.Add(id);
                }
                if (loopIds.Count > 0)
                    hatch.AppendLoop(HatchLoopTypes.Outermost, loopIds);
            }
            else if (op.BoundaryPoints != null && op.BoundaryPoints.Count >= 3)
            {
                // Build an explicit polyline loop from the points.
                var pl = EntityFactory.BuildPolyline(op.BoundaryPoints, true, null, null);
                ObjectId plId = ms.AppendEntity(pl);
                tr.AddNewlyCreatedDBObject(pl, true);
                hatch.AppendLoop(HatchLoopTypes.Outermost, new ObjectIdCollection { plId });
            }
            hatch.EvaluateHatch(true);
            return new List<ObjectId> { hatchId };
        }

        private static List<ObjectId> ResolveTargets(ProposedOp op, Database db, Dictionary<string, List<ObjectId>> opResults)
        {
            var ids = new List<ObjectId>();
            foreach (var h in op.TargetHandles)
            {
                ObjectId id = ResolveOne(h, db, opResults);
                if (!id.IsNull) ids.Add(id);
            }
            return ids;
        }

        private static ObjectId ResolveOne(string handle, Database db, Dictionary<string, List<ObjectId>> opResults)
        {
            if (opResults.TryGetValue(handle, out var staged) && staged.Count > 0)
                return staged[0]; // provisional reference to geometry created earlier this apply
            return HandleUtil.Resolve(db, handle);
        }

        /// <summary>A lightweight ProposedOp carrier so we can reuse EntityFactory.ApplyProperties for prop changes.</summary>
        private sealed class ProxyOp : ProposedOp
        {
            public override OpKind Kind => OpKind.PropertyChange;
            public override OpCategory Category => OpCategory.Modify;
        }
    }
}
