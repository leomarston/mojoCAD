using System.Collections.Generic;
using System.Text.Json;
using MojoCad.Agent.Models;

namespace MojoCad.Agent.Tools
{
    /// <summary>
    /// The complete tool surface offered to the model, as OpenAI-compatible function definitions with
    /// JSON-Schema parameters. The full list is sent on every request in the loop (the router
    /// re-validates each call). A compact set of powerful, composable primitives is preferred over
    /// hundreds of narrow tools; the handful of domain tools cover semantics models routinely get wrong.
    ///
    /// Conventions shared by every WRITE tool (also stated in the system prompt):
    ///  - Coordinates are raw drawing units: [x, y] or [x, y, z] (z defaults 0).
    ///  - Angles are degrees, counter-clockwise from +X.
    ///  - Existing entities are referenced by AutoCAD handle (persistent hex string), never by index.
    ///  - Every write stages into the pending change set and is reviewed before anything is applied.
    ///  - Colour/linetype/lineweight live on layers (BYLAYER); per-entity "props" overrides are discouraged.
    /// </summary>
    public static class ToolRegistry
    {
        public static List<ToolDef> BuildAll()
        {
            var tools = new List<ToolDef>();

            // --- READ ---------------------------------------------------------------------------
            tools.Add(Tool(ToolNames.GetDrawingSummary,
                "Read the active drawing's context: units, unit scale (drawing-units per metre), precision, extents, current layer, all layers, block definitions, text/dim styles, entity count and the user's current selection. Call this at the start of a task and after every applied change - never act from memory of a previous state.",
                Obj(props: new Dictionary<string, object>(), required: new string[0])));

            tools.Add(Tool(ToolNames.QueryEntities,
                "Find entities with optional AND-combined filters. Returns [{id (handle), type, layer, summary}]. Use this to locate the geometry you will modify.",
                Obj(new Dictionary<string, object>
                {
                    ["layer"] = Str("Restrict to this layer name."),
                    ["types"] = Arr(Str("DXF type name, e.g. LWPOLYLINE, CIRCLE, MTEXT, INSERT."), "Restrict to these DXF type names."),
                    ["window"] = NumArray("Window filter [xmin, ymin, xmax, ymax] in drawing units.", 4, 4),
                    ["near_point"] = Point("Centre of a proximity filter."),
                    ["radius"] = Num("Proximity radius (requires near_point)."),
                    ["block_name"] = Str("Restrict to block references of this block name."),
                    ["limit"] = Int("Maximum rows to return (default 200).")
                }, new string[0])));

            tools.Add(Tool(ToolNames.GetEntityProperties,
                "Get full, type-specific geometry and properties for the given handles (vertices, centre, radius, text contents, rotation, bounding box, overridden props).",
                Obj(new Dictionary<string, object> { ["ids"] = Handles("Entity handles to inspect.") }, new[] { "ids" })));

            tools.Add(Tool(ToolNames.Measure,
                "Have AutoCAD compute a measurement. Always use this for lengths/areas/angles - never compute geometry yourself.",
                Obj(new Dictionary<string, object>
                {
                    ["mode"] = Enum(new[] { "distance", "area", "angle", "total_length" }, "What to measure."),
                    ["points"] = Arr(Point("A point."), "Explicit points to measure between (for distance/angle/area)."),
                    ["ids"] = Handles("Entity handles to measure (e.g. total_length of selected polylines, area of a closed boundary).")
                }, new[] { "mode" })));

            // --- WRITE: primitives --------------------------------------------------------------
            tools.Add(WriteTool(ToolNames.CreatePolyline,
                "Create a polyline - the workhorse for walls, boundaries and any connected path. A 2-point polyline is a line.",
                new Dictionary<string, object>
                {
                    ["points"] = NumArrayOfPoints("Ordered vertices; at least two."),
                    ["closed"] = Bool("Close the polyline back to the first vertex (default false)."),
                    ["bulges"] = Arr(Num("Bulge factor for the segment starting at this vertex."), "Optional per-vertex bulge factors for arc segments; length must equal points."),
                    ["global_width"] = Num("Optional constant width for the whole polyline.")
                }, new[] { "points" }));

            tools.Add(WriteTool(ToolNames.CreateCircle,
                "Create a circle.",
                new Dictionary<string, object> { ["center"] = Point("Centre point."), ["radius"] = Num("Radius (> 0).") },
                new[] { "center", "radius" }));

            tools.Add(WriteTool(ToolNames.CreateArc,
                "Create an arc by centre, radius and start/end angles (degrees CCW from +X).",
                new Dictionary<string, object>
                {
                    ["center"] = Point("Centre point."),
                    ["radius"] = Num("Radius (> 0)."),
                    ["start_angle"] = Num("Start angle in degrees CCW from +X."),
                    ["end_angle"] = Num("End angle in degrees CCW from +X.")
                }, new[] { "center", "radius", "start_angle", "end_angle" }));

            tools.Add(WriteTool(ToolNames.CreateRectangle,
                "Create a rectangle (closed polyline) between two opposite corners.",
                new Dictionary<string, object>
                {
                    ["corner1"] = Point("First corner."),
                    ["corner2"] = Point("Opposite corner."),
                    ["rotation"] = Num("Rotation in degrees about corner1 (default 0)."),
                    ["fillet_radius"] = Num("Optional corner fillet radius.")
                }, new[] { "corner1", "corner2" }));

            tools.Add(WriteTool(ToolNames.CreateEllipse,
                "Create an ellipse from a centre, a major-axis vector and a minor/major ratio.",
                new Dictionary<string, object>
                {
                    ["center"] = Point("Centre point."),
                    ["major_axis"] = NumArray("Major-axis vector [dx, dy] from the centre to a major-axis endpoint.", 2, 3),
                    ["ratio"] = Num("Minor-to-major ratio in (0, 1].")
                }, new[] { "center", "major_axis", "ratio" }));

            // --- WRITE: annotation --------------------------------------------------------------
            tools.Add(WriteTool(ToolNames.CreateText,
                "Create single-line text or multiline text (MTEXT). Size text for the drawing's annotation scale.",
                new Dictionary<string, object>
                {
                    ["contents"] = Str("The text string (MTEXT formatting codes allowed when mtext=true)."),
                    ["position"] = Point("Insertion point."),
                    ["height"] = Num("Text height in drawing units (> 0)."),
                    ["rotation"] = Num("Rotation in degrees (default 0)."),
                    ["style"] = Str("Text style name (optional)."),
                    ["justify"] = Enum(new[] { "left", "center", "right", "middle" }, "Justification (default left)."),
                    ["mtext"] = Bool("Create MTEXT instead of single-line text (default false)."),
                    ["width"] = Num("MTEXT wrap width (only for mtext).")
                }, new[] { "contents", "position", "height" }));

            tools.Add(WriteTool(ToolNames.CreateDimension,
                "Create a dimension. AutoCAD computes the measured value - do not pass a value; pass geometry. Use text_override only for notes like 'TYP.'.",
                new Dictionary<string, object>
                {
                    ["kind"] = Enum(new[] { "linear", "aligned", "angular", "radial", "diameter" }, "Dimension kind."),
                    ["p1"] = Point("First definition point (extension line origin)."),
                    ["p2"] = Point("Second definition point."),
                    ["line_location"] = Point("A point the dimension line passes through (offset)."),
                    ["ids"] = Handles("For radial/diameter: the arc/circle handle to dimension."),
                    ["dim_style"] = Str("Dimension style name (optional)."),
                    ["text_override"] = Str("Optional text note; leave empty to show the measured value.")
                }, new[] { "kind" }));

            tools.Add(WriteTool(ToolNames.CreateHatch,
                "Fill a region with a hatch pattern. Prefer boundary_ids (closed entities) over explicit points.",
                new Dictionary<string, object>
                {
                    ["pattern"] = Str("Pattern name (default SOLID)."),
                    ["boundary_ids"] = Handles("Handles of closed entities forming the boundary (preferred)."),
                    ["boundary_points"] = NumArrayOfPoints("Explicit boundary polygon points (fallback)."),
                    ["scale"] = Num("Pattern scale (default 1)."),
                    ["angle"] = Num("Pattern angle in degrees (default 0).")
                }, new string[0]));

            // --- WRITE: blocks & layers ---------------------------------------------------------
            tools.Add(WriteTool(ToolNames.InsertBlock,
                "Insert a block reference. If the block name is unknown the call fails and lists available blocks.",
                new Dictionary<string, object>
                {
                    ["block_name"] = Str("Name of an existing block definition."),
                    ["position"] = Point("Insertion point."),
                    ["scale"] = Num("Uniform scale (default 1)."),
                    ["rotation"] = Num("Rotation in degrees (default 0)."),
                    ["attributes"] = StrMap("Optional attribute tag -> value pairs.")
                }, new[] { "block_name", "position" }));

            tools.Add(WriteTool(ToolNames.CreateLayer,
                "Create a layer (idempotent - succeeds if it already exists). Set colour/linetype/lineweight here so geometry can stay BYLAYER.",
                new Dictionary<string, object>
                {
                    ["name"] = Str("Layer name (follow the project layer standard, e.g. A-WALL)."),
                    ["color"] = Int("AutoCAD Color Index 1-255 (optional)."),
                    ["linetype"] = Str("Linetype name (optional; must be loaded or a standard name)."),
                    ["lineweight"] = Int("Lineweight in hundredths of a millimetre, e.g. 25 = 0.25mm (optional)."),
                    ["description"] = Str("Layer description (optional).")
                }, new[] { "name" }));

            tools.Add(WriteTool(ToolNames.SetCurrentLayer,
                "Set the current layer for subsequent geometry that does not specify its own layer.",
                new Dictionary<string, object> { ["name"] = Str("Existing layer name.") }, new[] { "name" }));

            // --- WRITE: editing -----------------------------------------------------------------
            tools.Add(WriteTool(ToolNames.MoveEntities,
                "Move entities by a displacement (from -> to) or an explicit delta.",
                new Dictionary<string, object>
                {
                    ["ids"] = Handles("Entities to move."),
                    ["from"] = Point("Base point."),
                    ["to"] = Point("Destination point."),
                    ["delta"] = NumArray("Explicit displacement [dx, dy] (alternative to from/to).", 2, 3)
                }, new[] { "ids" }));

            tools.Add(WriteTool(ToolNames.CopyEntities,
                "Copy entities by a displacement, optionally multiple times.",
                new Dictionary<string, object>
                {
                    ["ids"] = Handles("Entities to copy."),
                    ["from"] = Point("Base point."),
                    ["to"] = Point("Destination point."),
                    ["delta"] = NumArray("Explicit displacement [dx, dy].", 2, 3),
                    ["count"] = Int("Number of copies (default 1).")
                }, new[] { "ids" }));

            tools.Add(WriteTool(ToolNames.RotateEntities,
                "Rotate entities about a base point.",
                new Dictionary<string, object>
                {
                    ["ids"] = Handles("Entities to rotate."),
                    ["base"] = Point("Rotation base point."),
                    ["angle"] = Num("Rotation in degrees CCW.")
                }, new[] { "ids", "base", "angle" }));

            tools.Add(WriteTool(ToolNames.ScaleEntities,
                "Scale entities about a base point.",
                new Dictionary<string, object>
                {
                    ["ids"] = Handles("Entities to scale."),
                    ["base"] = Point("Scale base point."),
                    ["factor"] = Num("Scale factor (> 0).")
                }, new[] { "ids", "base", "factor" }));

            tools.Add(WriteTool(ToolNames.MirrorEntities,
                "Mirror entities across an axis line.",
                new Dictionary<string, object>
                {
                    ["ids"] = Handles("Entities to mirror."),
                    ["axis_p1"] = Point("First point of the mirror axis."),
                    ["axis_p2"] = Point("Second point of the mirror axis."),
                    ["keep_original"] = Bool("Keep the source entities (default true).")
                }, new[] { "ids", "axis_p1", "axis_p2" }));

            tools.Add(WriteTool(ToolNames.OffsetEntities,
                "Offset curves by a distance. Give a point on the side to offset toward, or a direction vector.",
                new Dictionary<string, object>
                {
                    ["ids"] = Handles("Curves to offset."),
                    ["distance"] = Num("Offset distance (> 0)."),
                    ["side"] = Point("A point on the side to offset toward (preferred)."),
                    ["direction"] = NumArray("Direction vector [dx, dy] (alternative to side).", 2, 3)
                }, new[] { "ids", "distance" }));

            tools.Add(WriteTool(ToolNames.ArrayEntities,
                "Create a rectangular or polar array of entities.",
                new Dictionary<string, object>
                {
                    ["ids"] = Handles("Entities to array."),
                    ["kind"] = Enum(new[] { "rectangular", "polar" }, "Array kind."),
                    ["rows"] = Int("Rectangular: number of rows."),
                    ["cols"] = Int("Rectangular: number of columns."),
                    ["row_spacing"] = Num("Rectangular: spacing between rows."),
                    ["col_spacing"] = Num("Rectangular: spacing between columns."),
                    ["center"] = Point("Polar: centre of the array."),
                    ["count"] = Int("Polar: number of items."),
                    ["angle_fill"] = Num("Polar: total fill angle in degrees (default 360).")
                }, new[] { "ids", "kind" }));

            tools.Add(WriteTool(ToolNames.EraseEntities,
                "Delete entities. Deletions are shown in red and are NOT auto-accepted - the engineer must opt in.",
                new Dictionary<string, object> { ["ids"] = Handles("Entities to erase.") }, new[] { "ids" }));

            // --- WRITE: domain ------------------------------------------------------------------
            tools.Add(WriteTool(ToolNames.DrawWall,
                "Draw a wall as a parallel double-line of a given width along a centre/face path, on the wall layer (default A-WALL). Returns the wall's handle(s) so openings can be placed in it.",
                new Dictionary<string, object>
                {
                    ["path"] = NumArrayOfPoints("Ordered points defining the wall run (at least two)."),
                    ["width"] = Num("Wall thickness in drawing units."),
                    ["justify"] = Enum(new[] { "center", "left", "right" }, "How the path relates to the wall faces (default center)."),
                    ["cleanup"] = Bool("Clean up intersections/corners (default true)."),
                    ["height"] = Num("Optional wall height (stored as data; 2D plan still drawn).")
                }, new[] { "path", "width" }));

            tools.Add(WriteTool(ToolNames.PlaceOpening,
                "Place a door, window or plain opening in an existing wall.",
                new Dictionary<string, object>
                {
                    ["wall_id"] = Str("Handle of the wall to cut."),
                    ["kind"] = Enum(new[] { "door", "window", "opening" }, "Opening kind."),
                    ["offset"] = Num("Distance along the wall from its start to the opening centre."),
                    ["width"] = Num("Opening width."),
                    ["swing"] = Enum(new[] { "left", "right" }, "Door swing side (doors only; default left)."),
                    ["block"] = Str("Optional block to use for the door/window symbol."),
                    ["sill_height"] = Num("Window sill height (windows only).")
                }, new[] { "wall_id", "kind", "offset", "width" }));

            tools.Add(WriteTool(ToolNames.RouteMep,
                "Route an MEP system (pipe, duct, conduit, sprinkler main/branch) along a path, with optional fittings and a tag.",
                new Dictionary<string, object>
                {
                    ["system"] = Enum(new[] { "pipe", "duct", "conduit", "sprinkler_main", "sprinkler_branch" }, "System type."),
                    ["path"] = NumArrayOfPoints("Route points (at least two)."),
                    ["size"] = Str("Nominal size, e.g. '100mm', '4\"', '300x200'."),
                    ["representation"] = Enum(new[] { "single_line", "double_line" }, "Drawing representation (default single_line)."),
                    ["fittings"] = Bool("Add elbows/tees at vertices (default true)."),
                    ["tag"] = Str("Optional text tag/label for the run.")
                }, new[] { "system", "path", "size" }));

            tools.Add(WriteTool(ToolNames.PlaceSprinklers,
                "Place sprinkler heads on a grid within an area and report the achieved spacing for code verification. NEVER guess spacing for life-safety - confirm it with the engineer first.",
                new Dictionary<string, object>
                {
                    ["area"] = NumArrayOfPoints("Polygon points bounding the coverage area."),
                    ["spacing"] = Num("Target head-to-head spacing in drawing units."),
                    ["max_wall_offset"] = Num("Maximum distance from a wall to the nearest head."),
                    ["head_block"] = Str("Block to use for a head (default FP-SPKL-HEAD)."),
                    ["stagger"] = Bool("Stagger alternate rows (default false)."),
                    ["connect_to"] = Str("Optional handle of a branch line to connect heads to.")
                }, new[] { "area", "spacing" }));

            tools.Add(WriteTool(ToolNames.AddRoomTag,
                "Add a room/space label with optional computed area. Returns the computed area.",
                new Dictionary<string, object>
                {
                    ["name"] = Str("Room name."),
                    ["boundary_id"] = Str("Handle of a closed boundary to compute area from (preferred)."),
                    ["point"] = Point("A point inside the room (alternative to boundary_id)."),
                    ["number"] = Str("Optional room number."),
                    ["show_area"] = Bool("Include the computed area in the tag (default true).")
                }, new[] { "name" }));

            // --- CONTROL ------------------------------------------------------------------------
            tools.Add(Tool(ToolNames.PresentPlan,
                "Present a short numbered plan BEFORE making changes, so the engineer can read it and stop you if needed. For Fire & Life-Safety work the plan must be approved before you proceed.",
                Obj(new Dictionary<string, object>
                {
                    ["title"] = Str("Short plan title."),
                    ["steps"] = Arr(Str("One plan step."), "Ordered, concrete steps you intend to take.")
                }, new[] { "steps" })));

            tools.Add(Tool(ToolNames.AskClarification,
                "Ask the engineer a question when the request is ambiguous. MANDATORY for any unspecified life-safety parameter (fire rating, egress width, sprinkler spacing). Ends the turn until they answer.",
                Obj(new Dictionary<string, object>
                {
                    ["question"] = Str("The question to ask."),
                    ["options"] = Arr(Str("A suggested answer."), "Optional suggested answers to offer as buttons.")
                }, new[] { "question" })));

            tools.Add(Tool(ToolNames.EmitChangeset,
                "Finalise everything you have staged this turn into one reviewable change set and present it to the engineer for accept/reject. This is your ONLY path to the drawing - nothing is applied until the engineer accepts. Provide a clear plain-language summary.",
                Obj(new Dictionary<string, object>
                {
                    ["summary"] = Str("Plain-language summary of what this change set does and why.")
                }, new[] { "summary" })));

            return tools;
        }

        // ----- schema builders ------------------------------------------------------------------

        private static ToolDef Tool(string name, string description, JsonElement parameters) =>
            new ToolDef { Type = "function", Function = new FunctionDef { Name = name, Description = description, Parameters = parameters } };

        /// <summary>A WRITE tool: augments the caller's params with the common layer/props/note/severity fields.</summary>
        private static ToolDef WriteTool(string name, string description, Dictionary<string, object> props, string[] required)
        {
            props["layer"] = Str("Target layer (defaults to the current layer). Prefer a standard layer name.");
            props["props"] = PropsOverride();
            props["note"] = Str("One-line plain-language description of this operation for the review card.");
            props["severity"] = Enum(new[] { "info", "notice", "warning", "blocking" }, "Review severity (default info).");
            return Tool(name, description, Obj(props, required));
        }

        private static JsonElement Obj(Dictionary<string, object> props, string[] required)
        {
            var schema = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = props,
                ["additionalProperties"] = false
            };
            if (required.Length > 0) schema["required"] = required;
            return ToElement(schema);
        }

        private static object Str(string desc) => new Dictionary<string, object> { ["type"] = "string", ["description"] = desc };
        private static object Num(string desc) => new Dictionary<string, object> { ["type"] = "number", ["description"] = desc };
        private static object Int(string desc) => new Dictionary<string, object> { ["type"] = "integer", ["description"] = desc };
        private static object Bool(string desc) => new Dictionary<string, object> { ["type"] = "boolean", ["description"] = desc };

        private static object Enum(string[] values, string desc) =>
            new Dictionary<string, object> { ["type"] = "string", ["enum"] = values, ["description"] = desc };

        private static object Arr(object items, string desc) =>
            new Dictionary<string, object> { ["type"] = "array", ["items"] = items, ["description"] = desc };

        private static object Point(string desc) => new Dictionary<string, object>
        {
            ["type"] = "array",
            ["items"] = new Dictionary<string, object> { ["type"] = "number" },
            ["minItems"] = 2,
            ["maxItems"] = 3,
            ["description"] = desc + " As [x, y] or [x, y, z] in drawing units."
        };

        private static object NumArray(string desc, int min, int max) => new Dictionary<string, object>
        {
            ["type"] = "array",
            ["items"] = new Dictionary<string, object> { ["type"] = "number" },
            ["minItems"] = min,
            ["maxItems"] = max,
            ["description"] = desc
        };

        private static object NumArrayOfPoints(string desc) => new Dictionary<string, object>
        {
            ["type"] = "array",
            ["items"] = new Dictionary<string, object>
            {
                ["type"] = "array",
                ["items"] = new Dictionary<string, object> { ["type"] = "number" },
                ["minItems"] = 2,
                ["maxItems"] = 3
            },
            ["description"] = desc + " Each point is [x, y] or [x, y, z]."
        };

        private static object Handles(string desc) => new Dictionary<string, object>
        {
            ["type"] = "array",
            ["items"] = new Dictionary<string, object> { ["type"] = "string" },
            ["description"] = desc + " Values are AutoCAD handles (hex strings) from query_entities/get_drawing_summary."
        };

        private static object StrMap(string desc) => new Dictionary<string, object>
        {
            ["type"] = "object",
            ["additionalProperties"] = new Dictionary<string, object> { ["type"] = "string" },
            ["description"] = desc
        };

        private static object PropsOverride() => new Dictionary<string, object>
        {
            ["type"] = "object",
            ["description"] = "Discouraged per-entity property overrides. Prefer BYLAYER.",
            ["additionalProperties"] = false,
            ["properties"] = new Dictionary<string, object>
            {
                ["color"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "AutoCAD Color Index 1-255." },
                ["true_color"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "True colour 0xRRGGBB." },
                ["linetype"] = new Dictionary<string, object> { ["type"] = "string" },
                ["lineweight"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Lineweight in 1/100 mm." }
            }
        };

        private static JsonElement ToElement(object schema)
        {
            string json = JsonSerializer.Serialize(schema);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
    }
}
