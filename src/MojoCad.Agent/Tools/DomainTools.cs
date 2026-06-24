using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MojoCad.Core.Changes;
using MojoCad.Core.Geometry;
using MojoCad.Core.Ports;

namespace MojoCad.Agent.Tools
{
    /// <summary>
    /// Higher-level domain tools (walls, openings, MEP runs, sprinkler grids, room tags). Each decomposes
    /// into editable primitive <see cref="ProposedOp"/>s on standard layers - the engineer reviews the
    /// result. These are deliberately pragmatic v1 constructions; they favour correct, reviewable output
    /// over CAD-perfect cleanup, and never invent life-safety parameters (the model must ask).
    /// </summary>
    public sealed class DomainTools
    {
        private readonly IAcadBridge _bridge;
        private readonly ChangeSetBuilder _builder;

        public DomainTools(IAcadBridge bridge, ChangeSetBuilder builder)
        {
            _bridge = bridge;
            _builder = builder;
        }

        // ----- draw_wall -------------------------------------------------------------------------

        public Task<ToolOutcome> DrawWallAsync(ToolArgs args)
        {
            var path = args.GetPoints("path", 2);
            double width = args.GetDouble("width");
            if (width <= 0) throw new ToolArgException("BAD_GEOMETRY", "Wall width must be positive.", "width");
            string justify = args.Has("justify") ? args.GetEnum("justify", new[] { "center", "left", "right" }, "center") : "center";
            string layer = args.GetStringOrNull("layer") ?? "A-WALL";

            var outline = DomainGeometry.WallOutline(path, width, justify);
            var op = new CreatePolylineOp { Points = outline, Closed = true, Layer = layer };
            op.Meta["role"] = "wall";
            op.Meta["centerline"] = JsonSerializer.Serialize(path.Select(p => new[] { p.X, p.Y, p.Z }));
            op.Meta["width"] = width.ToString(CultureInfo.InvariantCulture);
            op.Meta["justify"] = justify;
            ApplyNote(op, args, $"Wall, width {width:0.###}, on {layer}");

            var staged = _builder.Add(op);
            return Task.FromResult(new ToolOutcome
            {
                Display = $"Staged wall (width {width:0.###}) on {layer}",
                ResultJson = ToolResult.Staged(staged.OpId, staged.ProvisionalHandle, "Staged wall.",
                    new Dictionary<string, object?> { ["wall_id"] = staged.ProvisionalHandle })
            });
        }

        // ----- place_opening ---------------------------------------------------------------------

        public Task<ToolOutcome> PlaceOpeningAsync(ToolArgs args)
        {
            string wallId = args.GetString("wall_id");
            string kind = args.GetEnum("kind", new[] { "door", "window", "opening" });
            double offset = args.GetDouble("offset");
            double width = args.GetDouble("width");
            if (width <= 0) throw new ToolArgException("BAD_GEOMETRY", "Opening width must be positive.", "width");
            string swing = args.Has("swing") ? args.GetEnum("swing", new[] { "left", "right" }, "left") : "left";

            // Resolve the wall geometry. v1 supports walls staged earlier THIS turn (provisional handle),
            // where the centerline and width are known exactly.
            var wallOp = _builder.Current.Ops.FirstOrDefault(o => o.ResultHandles.Contains(wallId) && o.Meta.ContainsKey("role") && o.Meta["role"] == "wall");
            if (wallOp == null)
            {
                return Task.FromResult(Fail("WALL_NOT_RESOLVABLE",
                    $"Could not resolve wall '{wallId}' to a centerline.",
                    wallId,
                    "Place openings in a wall you drew with draw_wall in this same turn, or build the opening from primitives (two jamb lines + a swing arc)."));
            }

            var centerline = ParsePts(wallOp.Meta["centerline"]);
            double wallWidth = double.Parse(wallOp.Meta["width"], CultureInfo.InvariantCulture);

            if (!PointAndTangentAt(centerline, offset, out Pt center, out Vec tangent))
                return Task.FromResult(Fail("OFFSET_OUT_OF_RANGE",
                    $"Offset {offset} is beyond the wall length.", "offset", "Use an offset within the wall run length."));

            var along = Normalize(tangent);
            var perp = new Vec(-along.Y, along.X);
            double halfOpen = width / 2.0;
            double halfWall = wallWidth / 2.0;

            Pt startC = Translate(center, along, -halfOpen);
            Pt endC = Translate(center, along, +halfOpen);

            string doorLayer = kind == "door" ? "A-DOOR" : kind == "window" ? "A-GLAZ" : "A-WALL";
            var staged = new List<StagedRef>();

            // Jambs: short lines across the wall thickness at each end of the opening.
            staged.Add(_builder.Add(JambLine(startC, perp, halfWall, doorLayer)));
            staged.Add(_builder.Add(JambLine(endC, perp, halfWall, doorLayer)));

            if (kind == "door")
            {
                // Leaf + 90-degree swing arc from the hinge jamb.
                Pt hinge = swing == "left" ? startC : endC;
                Pt leafEnd = Translate(hinge, perp, width); // leaf swung open, length ~ opening width
                staged.Add(_builder.Add(new CreatePolylineOp { Points = new List<Pt> { hinge, leafEnd }, Layer = doorLayer }));
                double a0 = Math.Atan2(perp.Y, perp.X) * Angles.RadToDeg;
                double dirSign = swing == "left" ? -1 : 1;
                staged.Add(_builder.Add(new CreateArcOp
                {
                    Center = hinge,
                    Radius = width,
                    StartAngleDeg = Angles.Normalize(a0),
                    EndAngleDeg = Angles.Normalize(a0 + dirSign * 90),
                    Layer = doorLayer
                }));
            }
            else if (kind == "window")
            {
                // Glazing line along the centre of the opening.
                staged.Add(_builder.Add(new CreatePolylineOp { Points = new List<Pt> { startC, endC }, Layer = doorLayer }));
            }

            return Task.FromResult(new ToolOutcome
            {
                Display = $"Staged {kind} ({width:0.###}) in wall at offset {offset:0.###}",
                ResultJson = ToolResult.Ok(new Dictionary<string, object?>
                {
                    ["staged"] = true,
                    ["op_ids"] = staged.Select(s => s.OpId).ToList(),
                    ["message"] = $"Staged {kind} with {staged.Count} primitives."
                })
            });
        }

        // ----- route_mep -------------------------------------------------------------------------

        public Task<ToolOutcome> RouteMepAsync(ToolArgs args)
        {
            string system = args.GetEnum("system", new[] { "pipe", "duct", "conduit", "sprinkler_main", "sprinkler_branch" });
            var path = args.GetPoints("path", 2);
            string size = args.GetString("size");
            string representation = args.Has("representation") ? args.GetEnum("representation", new[] { "single_line", "double_line" }, "single_line") : "single_line";
            string? tag = args.GetStringOrNull("tag");
            string layer = args.GetStringOrNull("layer") ?? DefaultMepLayer(system);

            var staged = new List<StagedRef>();
            if (representation == "double_line" && TryParseLength(size, out double w) && w > 0)
            {
                staged.Add(_builder.Add(new CreatePolylineOp { Points = DomainGeometry.OffsetPath(path, w / 2.0), Layer = layer }));
                staged.Add(_builder.Add(new CreatePolylineOp { Points = DomainGeometry.OffsetPath(path, -w / 2.0), Layer = layer }));
            }
            else
            {
                staged.Add(_builder.Add(new CreatePolylineOp { Points = new List<Pt>(path), Layer = layer }));
            }

            if (!string.IsNullOrWhiteSpace(tag))
            {
                Pt mid = path[path.Count / 2];
                staged.Add(_builder.Add(new CreateTextOp { Contents = tag!, Position = mid, Height = EstimateTextHeight(path), Layer = layer }));
            }

            return Task.FromResult(new ToolOutcome
            {
                Display = $"Staged {system} run ({size}) on {layer}",
                ResultJson = ToolResult.Ok(new Dictionary<string, object?>
                {
                    ["staged"] = true,
                    ["op_ids"] = staged.Select(s => s.OpId).ToList(),
                    ["system"] = system,
                    ["size"] = size,
                    ["message"] = $"Staged {system} {representation} run."
                })
            });
        }

        // ----- place_sprinklers ------------------------------------------------------------------

        public async Task<ToolOutcome> PlaceSprinklersAsync(ToolArgs args)
        {
            var area = args.GetPoints("area", 3);
            double spacing = args.GetDouble("spacing");
            if (spacing <= 0) throw new ToolArgException("BAD_GEOMETRY", "Spacing must be positive.", "spacing");
            double wallOffset = args.GetDoubleOrNull("max_wall_offset") ?? spacing / 2.0;
            string headBlock = args.GetStringOrNull("head_block") ?? "FP-SPKL-HEAD";
            bool stagger = args.GetBool("stagger");
            string layer = args.GetStringOrNull("layer") ?? "FP-SPKL";

            var heads = DomainGeometry.SprinklerGrid(area, spacing, wallOffset, stagger);
            bool blockExists = await _bridge.BlockExistsAsync(headBlock).ConfigureAwait(false);

            var staged = new List<StagedRef>();
            foreach (var h in heads)
            {
                if (blockExists)
                    staged.Add(_builder.Add(new InsertBlockOp { BlockName = headBlock, Position = h, Layer = layer }));
                else
                    // Placeholder symbol so the layout is reviewable even without the head block loaded.
                    staged.Add(_builder.Add(new CreateCircleOp { Center = h, Radius = Math.Max(spacing * 0.03, 1e-3), Layer = layer }));
            }

            string note = blockExists ? "" : $" (block '{headBlock}' not found - placed placeholder circles)";
            return new ToolOutcome
            {
                Display = $"Staged {heads.Count} sprinkler heads at {spacing:0.###} spacing{note}",
                ResultJson = ToolResult.Ok(new Dictionary<string, object?>
                {
                    ["staged"] = true,
                    ["head_count"] = heads.Count,
                    ["achieved_spacing"] = spacing,
                    ["block_found"] = blockExists,
                    ["message"] = $"Placed {heads.Count} heads on a {spacing:0.###} grid (max wall offset {wallOffset:0.###}). Verify against the applicable code (e.g. NFPA 13).{note}"
                })
            };
        }

        // ----- add_room_tag ----------------------------------------------------------------------

        public async Task<ToolOutcome> AddRoomTagAsync(ToolArgs args)
        {
            string name = args.GetString("name");
            string? number = args.GetStringOrNull("number");
            bool showArea = args.GetBool("show_area", true);
            string layer = args.GetStringOrNull("layer") ?? "A-AREA-IDEN";
            string? boundaryId = args.GetStringOrNull("boundary_id");
            Pt? point = args.GetPointOrNull("point");

            Pt position;
            double? area = null;

            if (boundaryId != null)
            {
                var existing = await _bridge.FilterExistingHandlesAsync(new[] { boundaryId }).ConfigureAwait(false);
                if (existing.Count == 1)
                {
                    var details = await _bridge.GetEntityPropertiesAsync(new[] { boundaryId }).ConfigureAwait(false);
                    position = details.Count > 0 ? details[0].Bounds.Center : (point ?? new Pt(0, 0));
                    if (showArea)
                    {
                        var m = await _bridge.MeasureAsync("area", null, new[] { boundaryId }).ConfigureAwait(false);
                        area = m.Value;
                    }
                }
                else
                {
                    // Maybe a provisional boundary staged this turn - compute from its points if it's a polyline.
                    var op = _builder.Current.Ops.FirstOrDefault(o => o.ResultHandles.Contains(boundaryId)) as CreatePolylineOp;
                    if (op == null)
                        return Fail("BOUNDARY_NOT_FOUND", $"Boundary '{boundaryId}' not found.", boundaryId,
                            "Pass a 'point' inside the room, or a valid closed boundary handle.");
                    position = DomainGeometry.BoundsOf(op.Points).Item1; // top-left-ish; good enough for a label
                    var (mn, mx) = DomainGeometry.BoundsOf(op.Points);
                    position = new Pt((mn.X + mx.X) / 2, (mn.Y + mx.Y) / 2);
                    if (showArea) area = DomainGeometry.PolygonArea(op.Points);
                }
            }
            else if (point != null)
            {
                position = point.Value;
            }
            else
            {
                return Fail("MISSING_ARG", "Provide a 'boundary_id' or a 'point' for the room tag.", null,
                    "Give either a closed boundary handle or a point inside the room.");
            }

            string contents = string.IsNullOrWhiteSpace(number) ? name : $"{name}\\P{number}";
            if (area.HasValue && showArea) contents += $"\\P{area.Value:0.##}";

            double height = 2.5; // drawing units; the model should size for the annotation scale via create_text otherwise
            var textOp = new CreateTextOp { Contents = contents, Position = position, Height = height, IsMText = true, Layer = layer };
            var staged = _builder.Add(textOp);

            return new ToolOutcome
            {
                Display = $"Staged room tag '{name}'" + (area.HasValue ? $" ({area.Value:0.##})" : ""),
                ResultJson = ToolResult.Ok(new Dictionary<string, object?>
                {
                    ["staged"] = true,
                    ["op_id"] = staged.OpId,
                    ["computed_area"] = area,
                    ["message"] = $"Staged room tag for '{name}'."
                })
            };
        }

        // ----- helpers ---------------------------------------------------------------------------

        private CreatePolylineOp JambLine(Pt center, Vec perp, double halfWall, string layer) => new CreatePolylineOp
        {
            Points = new List<Pt> { Translate(center, perp, -halfWall), Translate(center, perp, halfWall) },
            Layer = layer
        };

        private static void ApplyNote(ProposedOp op, ToolArgs args, string fallback)
        {
            string? note = args.GetStringOrNull("note");
            op.PlainLanguage = string.IsNullOrWhiteSpace(note) ? fallback : note!;
            if (args.Has("severity"))
                op.Severity = ToolCommon.SeverityFrom(args.GetEnum("severity", new[] { "info", "notice", "warning", "blocking" }, "info"));
        }

        private static string DefaultMepLayer(string system) => system switch
        {
            "duct" => "M-DUCT",
            "pipe" => "P-PIPE",
            "conduit" => "E-COND",
            "sprinkler_main" => "FP-SPKL",
            "sprinkler_branch" => "FP-SPKL",
            _ => "0"
        };

        private static bool TryParseLength(string size, out double value)
        {
            // Accepts "100mm", "100", "4\"" (inch), "300x200" (uses first number). Returns drawing-unit-ish value.
            value = 0;
            if (string.IsNullOrWhiteSpace(size)) return false;
            var num = new string(size.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
            return double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0;
        }

        private static double EstimateTextHeight(IReadOnlyList<Pt> path)
        {
            double len = 0;
            for (int i = 1; i < path.Count; i++) len += path[i].DistanceTo(path[i - 1]);
            return Math.Max(len * 0.02, 1e-3);
        }

        private static bool PointAndTangentAt(IReadOnlyList<Pt> path, double dist, out Pt point, out Vec tangent)
        {
            point = path[0];
            tangent = new Vec(1, 0);
            if (dist < 0) return false;
            double acc = 0;
            for (int i = 1; i < path.Count; i++)
            {
                double seg = path[i].DistanceTo(path[i - 1]);
                if (acc + seg >= dist)
                {
                    double t = seg < 1e-12 ? 0 : (dist - acc) / seg;
                    point = new Pt(
                        path[i - 1].X + (path[i].X - path[i - 1].X) * t,
                        path[i - 1].Y + (path[i].Y - path[i - 1].Y) * t,
                        path[i - 1].Z + (path[i].Z - path[i - 1].Z) * t);
                    tangent = new Vec(path[i].X - path[i - 1].X, path[i].Y - path[i - 1].Y);
                    return true;
                }
                acc += seg;
            }
            return false;
        }

        private static List<Pt> ParsePts(string json)
        {
            var list = new List<Pt>();
            using var doc = JsonDocument.Parse(json);
            foreach (var arr in doc.RootElement.EnumerateArray())
            {
                var n = arr.EnumerateArray().Select(e => e.GetDouble()).ToArray();
                if (n.Length >= 2) list.Add(new Pt(n[0], n[1], n.Length > 2 ? n[2] : 0));
            }
            return list;
        }

        private static Vec Normalize(Vec v)
        {
            double len = Math.Sqrt(v.X * v.X + v.Y * v.Y);
            return len < 1e-12 ? new Vec(1, 0) : new Vec(v.X / len, v.Y / len);
        }

        private static Pt Translate(Pt p, Vec dir, double d) => new Pt(p.X + dir.X * d, p.Y + dir.Y * d, p.Z);

        private static ToolOutcome Fail(string code, string message, string? offending, string? hint) =>
            new ToolOutcome { Failed = true, Display = $"failed: {code}", ResultJson = ToolResult.Error(code, message, offending, hint) };
    }
}
