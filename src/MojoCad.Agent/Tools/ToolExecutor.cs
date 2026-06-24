using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MojoCad.Core.Agent;
using MojoCad.Core.Changes;
using MojoCad.Core.Drawing;
using MojoCad.Core.Geometry;
using MojoCad.Core.Ports;

namespace MojoCad.Agent.Tools
{
    /// <summary>The outcome of executing one tool call: the JSON result to feed back, plus any control signal.</summary>
    public sealed class ToolOutcome
    {
        public string ResultJson { get; set; } = "{}";
        public string Display { get; set; } = string.Empty;
        public bool Failed { get; set; }
        public bool IsRead { get; set; }

        // Control signals interpreted by the agent loop:
        public PlanInfo? Plan { get; set; }
        public QuestionInfo? Question { get; set; }
        public bool EmitChangeset { get; set; }
        public string? ChangeSummary { get; set; }
    }

    /// <summary>
    /// Executes a single tool call. Read tools go to <see cref="IAcadBridge"/>; write tools stage a
    /// <see cref="ProposedOp"/> into the turn's <see cref="ChangeSetBuilder"/> (never touching the
    /// drawing); control tools raise signals. All argument errors are returned as the uniform envelope
    /// so the model can self-correct.
    /// </summary>
    public sealed class ToolExecutor
    {
        private readonly IAcadBridge _bridge;
        private readonly ChangeSetBuilder _builder;
        private readonly DomainTools _domain;

        public ToolExecutor(IAcadBridge bridge, ChangeSetBuilder builder)
        {
            _bridge = bridge;
            _builder = builder;
            _domain = new DomainTools(bridge, builder);
        }

        public async Task<ToolOutcome> ExecuteAsync(string toolName, string argumentsJson, CancellationToken ct)
        {
            try
            {
                var args = new ToolArgs(argumentsJson);
                switch (toolName)
                {
                    // ---- read ----
                    case ToolNames.GetDrawingSummary: return await GetDrawingSummary();
                    case ToolNames.QueryEntities: return await QueryEntities(args);
                    case ToolNames.GetEntityProperties: return await GetEntityProperties(args);
                    case ToolNames.Measure: return await Measure(args);

                    // ---- write: primitives ----
                    case ToolNames.CreatePolyline: return CreatePolyline(args);
                    case ToolNames.CreateCircle: return CreateCircle(args);
                    case ToolNames.CreateArc: return CreateArc(args);
                    case ToolNames.CreateRectangle: return CreateRectangle(args);
                    case ToolNames.CreateEllipse: return CreateEllipse(args);

                    // ---- write: annotation ----
                    case ToolNames.CreateText: return CreateText(args);
                    case ToolNames.CreateDimension: return CreateDimension(args);
                    case ToolNames.CreateHatch: return await CreateHatch(args);

                    // ---- write: blocks & layers ----
                    case ToolNames.InsertBlock: return await InsertBlock(args);
                    case ToolNames.CreateLayer: return CreateLayer(args);
                    case ToolNames.SetCurrentLayer: return SetCurrentLayer(args);

                    // ---- write: editing ----
                    case ToolNames.MoveEntities: return await Move(args);
                    case ToolNames.CopyEntities: return await Copy(args);
                    case ToolNames.RotateEntities: return await Rotate(args);
                    case ToolNames.ScaleEntities: return await Scale(args);
                    case ToolNames.MirrorEntities: return await Mirror(args);
                    case ToolNames.OffsetEntities: return await Offset(args);
                    case ToolNames.ArrayEntities: return await ArrayTool(args);
                    case ToolNames.EraseEntities: return await Erase(args);

                    // ---- write: domain ----
                    case ToolNames.DrawWall: return await _domain.DrawWallAsync(args);
                    case ToolNames.PlaceOpening: return await _domain.PlaceOpeningAsync(args);
                    case ToolNames.RouteMep: return await _domain.RouteMepAsync(args);
                    case ToolNames.PlaceSprinklers: return await _domain.PlaceSprinklersAsync(args);
                    case ToolNames.AddRoomTag: return await _domain.AddRoomTagAsync(args);

                    // ---- control ----
                    case ToolNames.PresentPlan: return PresentPlan(args);
                    case ToolNames.AskClarification: return AskClarification(args);
                    case ToolNames.EmitChangeset: return EmitChangeset(args);

                    default:
                        return Fail("UNKNOWN_TOOL", $"Unknown tool '{toolName}'.", toolName,
                            "Use only the documented tools.", display: $"Unknown tool {toolName}");
                }
            }
            catch (ToolArgException ex)
            {
                return Fail(ex.Code, ex.Message, ex.Offending, ex.Hint, display: $"{toolName} failed: {ex.Code}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Fail("TOOL_ERROR", ex.Message, null, "An unexpected error occurred executing the tool.",
                    display: $"{toolName} error");
            }
        }

        // ===== READ ===============================================================================

        private async Task<ToolOutcome> GetDrawingSummary()
        {
            var summary = await _bridge.GetDrawingSummaryAsync().ConfigureAwait(false);
            return Read(JsonSerializer.Serialize(summary, ToolJson.ReadResultOptions),
                $"Read drawing summary ({summary.EntityTotal} entities, {summary.Layers.Count} layers)");
        }

        private async Task<ToolOutcome> QueryEntities(ToolArgs args)
        {
            var q = new EntityQuery
            {
                Layer = args.GetStringOrNull("layer"),
                Types = args.GetStringListOrNull("types"),
                BlockName = args.GetStringOrNull("block_name"),
                NearPoint = args.GetPointOrNull("near_point"),
                Radius = args.GetDoubleOrNull("radius"),
                Limit = args.Has("limit") ? args.GetInt("limit", 200) : 200
            };
            var window = args.GetDoubleArrayOrNull("window");
            if (window != null && window.Count >= 4)
                q.Window = new Bounds(new Pt(window[0], window[1]), new Pt(window[2], window[3]));

            var rows = await _bridge.QueryEntitiesAsync(q).ConfigureAwait(false);
            var payload = new Dictionary<string, object?> { ["count"] = rows.Count, ["entities"] = rows };
            return Read(JsonSerializer.Serialize(payload, ToolJson.ReadResultOptions), $"Queried entities ({rows.Count} found)");
        }

        private async Task<ToolOutcome> GetEntityProperties(ToolArgs args)
        {
            var ids = args.GetStringList("ids");
            var details = await _bridge.GetEntityPropertiesAsync(ids).ConfigureAwait(false);
            var payload = new Dictionary<string, object?> { ["entities"] = details };
            return Read(JsonSerializer.Serialize(payload, ToolJson.ReadResultOptions), $"Inspected {ids.Count} entit{(ids.Count == 1 ? "y" : "ies")}");
        }

        private async Task<ToolOutcome> Measure(ToolArgs args)
        {
            string mode = args.GetEnum("mode", new[] { "distance", "area", "angle", "total_length" });
            var points = args.GetPointsOrNull("points");
            var ids = args.GetStringListOrNull("ids");
            var result = await _bridge.MeasureAsync(mode, points, ids).ConfigureAwait(false);
            return Read(JsonSerializer.Serialize(result, ToolJson.ReadResultOptions), $"Measured {mode} = {result.Value:0.###} {result.Units}");
        }

        // ===== WRITE: primitives ==================================================================

        private ToolOutcome CreatePolyline(ToolArgs args)
        {
            var op = new CreatePolylineOp
            {
                Points = args.GetPoints("points", 2),
                Closed = args.GetBool("closed"),
                Bulges = args.GetDoubleArrayOrNull("bulges"),
                GlobalWidth = args.GetDoubleOrNull("global_width")
            };
            return Stage(args, op, $"polyline ({op.Points.Count} pts{(op.Closed ? ", closed" : "")})");
        }

        private ToolOutcome CreateCircle(ToolArgs args)
        {
            double r = args.GetDouble("radius");
            if (r <= 0) throw new ToolArgException("BAD_GEOMETRY", "Circle radius must be positive.", "radius", "Use radius > 0.");
            var op = new CreateCircleOp { Center = args.GetPoint("center"), Radius = r };
            return Stage(args, op, $"circle r={r:0.###}");
        }

        private ToolOutcome CreateArc(ToolArgs args)
        {
            double r = args.GetDouble("radius");
            if (r <= 0) throw new ToolArgException("BAD_GEOMETRY", "Arc radius must be positive.", "radius", "Use radius > 0.");
            var op = new CreateArcOp
            {
                Center = args.GetPoint("center"),
                Radius = r,
                StartAngleDeg = args.GetDouble("start_angle"),
                EndAngleDeg = args.GetDouble("end_angle")
            };
            return Stage(args, op, $"arc r={r:0.###}");
        }

        private ToolOutcome CreateRectangle(ToolArgs args)
        {
            var op = new CreateRectangleOp
            {
                Corner1 = args.GetPoint("corner1"),
                Corner2 = args.GetPoint("corner2"),
                RotationDeg = args.GetDoubleOrNull("rotation") ?? 0,
                FilletRadius = args.GetDoubleOrNull("fillet_radius")
            };
            return Stage(args, op, "rectangle");
        }

        private ToolOutcome CreateEllipse(ToolArgs args)
        {
            var axis = args.GetVectorOrNull("major_axis") ?? throw new ToolArgException("MISSING_ARG", "major_axis is required.", "major_axis");
            double ratio = args.GetDouble("ratio");
            if (ratio <= 0 || ratio > 1) throw new ToolArgException("BAD_GEOMETRY", "Ellipse ratio must be in (0, 1].", "ratio");
            var op = new CreateEllipseOp { Center = args.GetPoint("center"), MajorAxis = axis, Ratio = ratio };
            return Stage(args, op, "ellipse");
        }

        // ===== WRITE: annotation ==================================================================

        private ToolOutcome CreateText(ToolArgs args)
        {
            double h = args.GetDouble("height");
            if (h <= 0) throw new ToolArgException("BAD_GEOMETRY", "Text height must be positive.", "height");
            var op = new CreateTextOp
            {
                Contents = args.GetString("contents"),
                Position = args.GetPoint("position"),
                Height = h,
                RotationDeg = args.GetDoubleOrNull("rotation") ?? 0,
                Style = args.GetStringOrNull("style"),
                Justify = ParseJustify(args.Has("justify") ? args.GetEnum("justify", new[] { "left", "center", "right", "middle" }, "left") : "left"),
                IsMText = args.GetBool("mtext"),
                Width = args.GetDoubleOrNull("width")
            };
            return Stage(args, op, $"text \"{Truncate(op.Contents, 24)}\"");
        }

        private ToolOutcome CreateDimension(ToolArgs args)
        {
            var op = new CreateDimensionOp
            {
                DimKind = ParseDimKind(args.GetEnum("kind", new[] { "linear", "aligned", "angular", "radial", "diameter" })),
                P1 = args.GetPointOrNull("p1"),
                P2 = args.GetPointOrNull("p2"),
                LineLocation = args.GetPointOrNull("line_location"),
                DimStyle = args.GetStringOrNull("dim_style"),
                TextOverride = args.GetStringOrNull("text_override")
            };
            var ids = args.GetStringListOrNull("ids");
            if (ids != null) op.TargetHandles.AddRange(ids);
            return Stage(args, op, $"{op.DimKind.ToString().ToLowerInvariant()} dimension");
        }

        private async Task<ToolOutcome> CreateHatch(ToolArgs args)
        {
            var op = new CreateHatchOp
            {
                Pattern = args.GetStringOrNull("pattern") ?? "SOLID",
                BoundaryIds = args.GetStringListOrNull("boundary_ids"),
                BoundaryPoints = args.GetPointsOrNull("boundary_points"),
                Scale = args.GetDoubleOrNull("scale") ?? 1.0,
                AngleDeg = args.GetDoubleOrNull("angle") ?? 0.0
            };
            if (op.BoundaryIds != null && op.BoundaryIds.Count > 0)
            {
                await ToolCommon.ResolveHandlesAsync(op.BoundaryIds, _builder, _bridge).ConfigureAwait(false);
                op.TargetHandles.AddRange(op.BoundaryIds);
            }
            else if (op.BoundaryPoints == null || op.BoundaryPoints.Count < 3)
            {
                throw new ToolArgException("NO_BOUNDARY", "A hatch needs boundary_ids or at least 3 boundary_points.", null,
                    "Provide closed boundary entities (preferred) or a boundary polygon.");
            }
            return Stage(args, op, $"hatch ({op.Pattern})");
        }

        // ===== WRITE: blocks & layers =============================================================

        private async Task<ToolOutcome> InsertBlock(ToolArgs args)
        {
            string name = args.GetString("block_name");
            if (!await _bridge.BlockExistsAsync(name).ConfigureAwait(false))
                return Fail("BLOCK_NOT_FOUND", $"No block definition named '{name}'.", name,
                    "Check the block_defs in get_drawing_summary, or create the geometry from primitives.",
                    display: $"insert_block failed: '{name}' not found");

            var op = new InsertBlockOp
            {
                BlockName = name,
                Position = args.GetPoint("position"),
                Scale = args.GetDoubleOrNull("scale") ?? 1.0,
                RotationDeg = args.GetDoubleOrNull("rotation") ?? 0.0,
                Attributes = args.GetStringMapOrNull("attributes")
            };
            return Stage(args, op, $"insert '{name}'");
        }

        private ToolOutcome CreateLayer(ToolArgs args)
        {
            var op = new CreateLayerOp
            {
                Name = args.GetString("name"),
                ColorIndex = args.Has("color") ? args.GetInt("color") : (int?)null,
                Linetype = args.GetStringOrNull("linetype"),
                LineweightHundredthsMm = args.Has("lineweight") ? args.GetInt("lineweight") : (int?)null,
                Description = args.GetStringOrNull("description")
            };
            return Stage(args, op, $"layer '{op.Name}'");
        }

        private ToolOutcome SetCurrentLayer(ToolArgs args)
        {
            var op = new SetCurrentLayerOp { Name = args.GetString("name") };
            return Stage(args, op, $"set current layer '{op.Name}'");
        }

        // ===== WRITE: editing =====================================================================

        private async Task<ToolOutcome> Move(ToolArgs args)
        {
            var ids = await ResolveIds(args);
            var (from, to) = FromToOrDelta(args);
            var op = new MoveOp { From = from, To = to };
            op.TargetHandles.AddRange(ids);
            return Stage(args, op, $"move {ids.Count} entit{(ids.Count == 1 ? "y" : "ies")}");
        }

        private async Task<ToolOutcome> Copy(ToolArgs args)
        {
            var ids = await ResolveIds(args);
            var (from, to) = FromToOrDelta(args);
            var op = new CopyOp { From = from, To = to, Count = args.Has("count") ? Math.Max(1, args.GetInt("count")) : 1 };
            op.TargetHandles.AddRange(ids);
            return Stage(args, op, $"copy {ids.Count} entit{(ids.Count == 1 ? "y" : "ies")} x{op.Count}");
        }

        private async Task<ToolOutcome> Rotate(ToolArgs args)
        {
            var ids = await ResolveIds(args);
            var op = new RotateOp { Base = args.GetPoint("base"), AngleDeg = args.GetDouble("angle") };
            op.TargetHandles.AddRange(ids);
            return Stage(args, op, $"rotate {ids.Count} by {op.AngleDeg:0.#}°");
        }

        private async Task<ToolOutcome> Scale(ToolArgs args)
        {
            var ids = await ResolveIds(args);
            double f = args.GetDouble("factor");
            if (f <= 0) throw new ToolArgException("BAD_GEOMETRY", "Scale factor must be positive.", "factor");
            var op = new ScaleOp { Base = args.GetPoint("base"), Factor = f };
            op.TargetHandles.AddRange(ids);
            return Stage(args, op, $"scale {ids.Count} by {f:0.###}");
        }

        private async Task<ToolOutcome> Mirror(ToolArgs args)
        {
            var ids = await ResolveIds(args);
            var op = new MirrorOp
            {
                AxisP1 = args.GetPoint("axis_p1"),
                AxisP2 = args.GetPoint("axis_p2"),
                KeepOriginal = args.GetBool("keep_original", true)
            };
            op.TargetHandles.AddRange(ids);
            return Stage(args, op, $"mirror {ids.Count} entit{(ids.Count == 1 ? "y" : "ies")}");
        }

        private async Task<ToolOutcome> Offset(ToolArgs args)
        {
            var ids = await ResolveIds(args);
            double d = args.GetDouble("distance");
            if (d <= 0) throw new ToolArgException("BAD_GEOMETRY", "Offset distance must be positive.", "distance");
            var op = new OffsetOp { Distance = d, Side = args.GetPointOrNull("side"), Direction = args.GetVectorOrNull("direction") };
            if (op.Side == null && op.Direction == null)
                throw new ToolArgException("MISSING_ARG", "Offset needs a 'side' point or a 'direction' vector.", null,
                    "Pass 'side' (a point on the offset side) or 'direction'.");
            op.TargetHandles.AddRange(ids);
            return Stage(args, op, $"offset {ids.Count} by {d:0.###}");
        }

        private async Task<ToolOutcome> ArrayTool(ToolArgs args)
        {
            var ids = await ResolveIds(args);
            var op = new ArrayOp
            {
                ArrayKind = args.GetEnum("kind", new[] { "rectangular", "polar" }) == "polar" ? ArrayKind.Polar : ArrayKind.Rectangular,
                Rows = args.Has("rows") ? args.GetInt("rows") : 1,
                Cols = args.Has("cols") ? args.GetInt("cols") : 1,
                RowSpacing = args.GetDoubleOrNull("row_spacing") ?? 0,
                ColSpacing = args.GetDoubleOrNull("col_spacing") ?? 0,
                Center = args.GetPointOrNull("center"),
                Count = args.Has("count") ? args.GetInt("count") : 0,
                AngleFillDeg = args.GetDoubleOrNull("angle_fill") ?? 360.0
            };
            op.TargetHandles.AddRange(ids);
            return Stage(args, op, $"{op.ArrayKind.ToString().ToLowerInvariant()} array of {ids.Count}");
        }

        private async Task<ToolOutcome> Erase(ToolArgs args)
        {
            var ids = await ResolveIds(args);
            var op = new EraseOp();
            op.TargetHandles.AddRange(ids);
            // Deletions default to a Warning so they stand out and aren't swept up by "accept all".
            if (op.Severity == Severity.Info) op.Severity = Severity.Warning;
            return Stage(args, op, $"erase {ids.Count} entit{(ids.Count == 1 ? "y" : "ies")}");
        }

        // ===== CONTROL ============================================================================

        private ToolOutcome PresentPlan(ToolArgs args)
        {
            var plan = new PlanInfo
            {
                Title = args.GetStringOrNull("title") ?? "Plan",
                Steps = args.GetStringList("steps", 1)
            };
            return new ToolOutcome
            {
                Plan = plan,
                Display = $"Presented a {plan.Steps.Count}-step plan",
                ResultJson = ToolResult.Ok(new Dictionary<string, object?> { ["acknowledged"] = true, ["steps"] = plan.Steps.Count })
            };
        }

        private ToolOutcome AskClarification(ToolArgs args)
        {
            var q = new QuestionInfo { Question = args.GetString("question"), Options = args.GetStringListOrNull("options") ?? new List<string>() };
            return new ToolOutcome
            {
                Question = q,
                Display = "Asked a clarifying question",
                ResultJson = ToolResult.Ok(new Dictionary<string, object?> { ["asked"] = true })
            };
        }

        private ToolOutcome EmitChangeset(ToolArgs args)
        {
            string summary = args.GetString("summary");
            if (!_builder.HasOps)
                return Fail("NOTHING_STAGED", "No operations have been staged this turn.", null,
                    "Stage geometry with the write tools before calling emit_changeset.", display: "emit_changeset: nothing staged");
            return new ToolOutcome
            {
                EmitChangeset = true,
                ChangeSummary = summary,
                Display = $"Emitted change set ({_builder.Count} ops) for review",
                ResultJson = ToolResult.Ok(new Dictionary<string, object?> { ["emitted"] = true, ["op_count"] = _builder.Count })
            };
        }

        // ===== helpers ============================================================================

        private ToolOutcome Stage(ToolArgs args, ProposedOp op, string display)
        {
            ToolCommon.ApplyCommon(op, args);
            var staged = _builder.Add(op);
            string layerText = string.IsNullOrEmpty(op.Layer) ? "current layer" : op.Layer!;
            return new ToolOutcome
            {
                Display = $"Staged {display} on {layerText}",
                ResultJson = ToolResult.Staged(staged.OpId, staged.ProvisionalHandle, $"Staged {display}.")
            };
        }

        private async Task<List<string>> ResolveIds(ToolArgs args)
        {
            var ids = args.GetStringList("ids");
            return await ToolCommon.ResolveHandlesAsync(ids, _builder, _bridge).ConfigureAwait(false);
        }

        private static (Pt from, Pt to) FromToOrDelta(ToolArgs args)
        {
            var delta = args.GetVectorOrNull("delta");
            if (delta != null)
                return (new Pt(0, 0, 0), new Pt(delta.Value.X, delta.Value.Y, delta.Value.Z));
            var from = args.GetPointOrNull("from") ?? throw new ToolArgException("MISSING_ARG", "Provide 'from'+'to' or 'delta'.", "from");
            var to = args.GetPointOrNull("to") ?? throw new ToolArgException("MISSING_ARG", "Provide 'from'+'to' or 'delta'.", "to");
            return (from, to);
        }

        private static TextJustify ParseJustify(string s) => s switch
        {
            "center" => TextJustify.Center,
            "right" => TextJustify.Right,
            "middle" => TextJustify.Middle,
            _ => TextJustify.Left
        };

        private static DimensionKind ParseDimKind(string s) => s switch
        {
            "aligned" => DimensionKind.Aligned,
            "angular" => DimensionKind.Angular,
            "radial" => DimensionKind.Radial,
            "diameter" => DimensionKind.Diameter,
            _ => DimensionKind.Linear
        };

        private static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";

        private static ToolOutcome Read(string json, string display) =>
            new ToolOutcome { ResultJson = json, Display = display, IsRead = true };

        private static ToolOutcome Fail(string code, string message, string? offending, string? hint, string display) =>
            new ToolOutcome { Failed = true, Display = display, ResultJson = ToolResult.Error(code, message, offending, hint) };
    }
}
