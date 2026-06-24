# Agent tools

The agent affects the drawing only through a fixed tool surface, defined in `MojoCad.Agent/Tools/ToolRegistry.cs` (the OpenAI-compatible function definitions sent to the model) and named in `ToolNames.cs`. mojoCAD favours a compact set of powerful, composable primitives over hundreds of narrow tools; a handful of domain tools cover semantics models routinely get wrong.

Tools fall into three classes:

- **Read** — query the live drawing through `IAcadBridge`. Never stage anything.
- **Write** — stage a `ProposedOp` into the turn's change set. **Never touch the drawing**; reviewed and applied later.
- **Control** — emit no geometry; they raise signals interpreted by the agent loop (plan, question, finalise).

## Conventions (every tool)

These conventions are baked into both the tool schemas and the system prompt:

- **Coordinates** are raw **drawing units**: `[x, y]` or `[x, y, z]` (`z` defaults to 0).
- **Angles** are in **degrees, counter-clockwise from +X**.
- **Existing entities are referenced by AutoCAD handle** — the persistent hex string from `query_entities` / `get_drawing_summary` — never by index. Geometry staged earlier *this turn* may be referenced by its **provisional handle** (`@op-N`) returned by the staging tool.
- **Units & scale come from the drawing**, never assumed. `get_drawing_summary` reports the unit, the unit scale (drawing units per metre) and precision; the model converts human dimensions itself.
- **Measurements come from CAD.** Use the `measure` tool for lengths/areas/angles; the model must not compute geometry itself for output.
- **Every write stages and is reviewed** before anything is applied. There is no auto-apply.
- **BYLAYER rule.** Colour, linetype and lineweight live on **layers**. Every write tool accepts an optional `layer` and a discouraged per-entity `props` override (`color`, `true_color`, `linetype`, `lineweight`); the standards lint flags any `props` override as a notice. The agent is told to create/pick the right layer instead.

### Shared write-tool fields

Every write tool augments its own parameters with:

| Field | Type | Meaning |
|---|---|---|
| `layer` | string | Target layer (defaults to the current layer). Prefer a standard name. |
| `props` | object | Discouraged per-entity overrides (`color` 1–255, `true_color` 0xRRGGBB, `linetype`, `lineweight` in 1/100 mm). Prefer BYLAYER. |
| `note` | string | One-line plain-language description shown on the review row. |
| `severity` | enum | `info` \| `notice` \| `warning` \| `blocking` review severity (default `info`). |

## Read tools

| Tool | Purpose | Key params |
|---|---|---|
| `get_drawing_summary` | Read the active drawing's context: units, unit scale (drawing units per metre), precision, extents, current layer, all layers, block definitions, text/dim styles, entity count, current selection. **Call at the start of a task and after every applied change** — never act from memory. | *(none)* |
| `query_entities` | Find entities with optional AND-combined filters. Returns `[{id (handle), type, layer, summary}]`. | `layer`, `types[]` (DXF names), `window` `[xmin,ymin,xmax,ymax]`, `near_point`+`radius`, `block_name`, `limit` (default 200) |
| `get_entity_properties` | Full, type-specific geometry/properties for handles (vertices, centre, radius, text, rotation, bounding box, overridden props). | `ids[]` (handles) |
| `measure` | Have AutoCAD compute a measurement. Always use this for lengths/areas/angles. | `mode` (`distance` \| `area` \| `angle` \| `total_length`), `points[]`, `ids[]` |

## Write tools — primitives

| Tool | Purpose | Key params |
|---|---|---|
| `create_polyline` | The workhorse for walls, boundaries, any connected path. A 2-point polyline is a line. | `points[]` (≥2), `closed`, `bulges[]`, `global_width` |
| `create_circle` | Create a circle. | `center`, `radius` (>0) |
| `create_arc` | Arc by centre, radius and start/end angles (deg CCW from +X). | `center`, `radius` (>0), `start_angle`, `end_angle` |
| `create_rectangle` | Rectangle (closed polyline) between two opposite corners. | `corner1`, `corner2`, `rotation`, `fillet_radius` |
| `create_ellipse` | Ellipse from centre, major-axis vector, minor/major ratio. | `center`, `major_axis` `[dx,dy]`, `ratio` (0,1] |

## Write tools — annotation

| Tool | Purpose | Key params |
|---|---|---|
| `create_text` | Single-line text or MTEXT. Size for the drawing's annotation scale. | `contents`, `position`, `height` (>0), `rotation`, `style`, `justify` (`left`/`center`/`right`/`middle`), `mtext`, `width` |
| `create_dimension` | A dimension. AutoCAD computes the measured value — pass geometry, not a value. Use `text_override` only for notes like `TYP.`. | `kind` (`linear`/`aligned`/`angular`/`radial`/`diameter`), `p1`, `p2`, `line_location`, `ids` (radial/diameter), `dim_style`, `text_override` |
| `create_hatch` | Fill a region. Prefer `boundary_ids` (closed entities) over explicit points. | `pattern` (default SOLID), `boundary_ids[]`, `boundary_points[]` (≥3), `scale`, `angle` |

## Write tools — blocks & layers

| Tool | Purpose | Key params |
|---|---|---|
| `insert_block` | Insert a block reference. If the block name is unknown the call fails and the model is pointed at the available blocks. | `block_name`, `position`, `scale`, `rotation`, `attributes` (tag→value) |
| `create_layer` | Create a layer (idempotent — succeeds if it already exists). Set colour/linetype/lineweight here so geometry stays BYLAYER. | `name`, `color` (1–255), `linetype`, `lineweight` (1/100 mm), `description` |
| `set_current_layer` | Set the current layer for subsequent geometry that doesn't specify its own. | `name` |

## Write tools — editing existing entities

All reference targets by handle (`ids[]`).

| Tool | Purpose | Key params |
|---|---|---|
| `move_entities` | Move by displacement or explicit delta. | `ids[]`, `from`+`to` or `delta` |
| `copy_entities` | Copy by displacement, optionally multiple times. | `ids[]`, `from`+`to` or `delta`, `count` |
| `rotate_entities` | Rotate about a base point. | `ids[]`, `base`, `angle` (deg CCW) |
| `scale_entities` | Scale about a base point. | `ids[]`, `base`, `factor` (>0) |
| `mirror_entities` | Mirror across an axis line. | `ids[]`, `axis_p1`, `axis_p2`, `keep_original` (default true) |
| `offset_entities` | Offset curves by a distance toward a side point or a direction. | `ids[]`, `distance` (>0), `side` or `direction` |
| `array_entities` | Rectangular or polar array. | `ids[]`, `kind` (`rectangular`/`polar`), `rows`/`cols`/`row_spacing`/`col_spacing`, `center`/`count`/`angle_fill` |
| `erase_entities` | Delete entities. **Shown in red, NOT auto-accepted** — the engineer must opt in. Staged at `warning` severity by default. | `ids[]` |

## Write tools — domain

Higher-level constructions that decompose into editable primitive ops on standard layers; the engineer reviews the result. These never invent life-safety parameters.

| Tool | Purpose | Key params |
|---|---|---|
| `draw_wall` | Wall as a parallel double-line of a given width along a path, on the wall layer (default `A-WALL`). Returns a `wall_id` (provisional handle) so openings can be placed in it. | `path[]` (≥2), `width`, `justify` (`center`/`left`/`right`), `cleanup`, `height` |
| `place_opening` | Door, window or plain opening in an existing wall. v1 resolves walls drawn with `draw_wall` **this same turn** (known centerline/width). | `wall_id`, `kind` (`door`/`window`/`opening`), `offset` (along the wall), `width`, `swing` (`left`/`right`), `block`, `sill_height` |
| `route_mep` | Route an MEP system along a path, with optional fittings and a tag. Default layer per system (`M-DUCT`, `P-PIPE`, `E-COND`, `FP-SPKL`). | `system` (`pipe`/`duct`/`conduit`/`sprinkler_main`/`sprinkler_branch`), `path[]`, `size`, `representation` (`single_line`/`double_line`), `fittings`, `tag` |
| `place_sprinklers` | Place heads on a grid within an area and report the achieved spacing for code verification. Falls back to placeholder circles if the head block isn't loaded. **Never guess spacing for life-safety — confirm it first.** | `area[]`, `spacing`, `max_wall_offset`, `head_block` (default `FP-SPKL-HEAD`), `stagger`, `connect_to` |
| `add_room_tag` | Room/space label with optional computed area (CAD-measured from a boundary). Returns the computed area. | `name`, `boundary_id` or `point`, `number`, `show_area` |

## Control tools (no geometry)

| Tool | Purpose | Key params | Effect on the loop |
|---|---|---|---|
| `present_plan` | Present a short numbered plan BEFORE making changes. For Fire & Life-Safety, must be approved first. | `title`, `steps[]` | Raises `OnPlan`. If approval is required (FLS discipline or plan-approval setting), the turn **stops** for approval. |
| `ask_clarification` | Ask the engineer a question when the request is ambiguous. **MANDATORY for any unspecified life-safety parameter** (fire rating, egress width, sprinkler spacing). | `question`, `options[]` | Raises `OnQuestion` and **ends the turn** until answered. |
| `emit_changeset` | Finalise everything staged this turn into one reviewable change set. **The only path to the drawing** — nothing applies until the engineer accepts. | `summary` | Raises `OnChangeSetReady` and **stops** the turn. Fails with `NOTHING_STAGED` if no ops were staged. |

## The uniform error envelope

Every tool failure (and any argument error) is returned to the model as one JSON envelope, so the model can self-correct without breaking the loop. Success payloads are tool-specific; staged writes use a `{ staged, op_id, handle, message }` shape.

```json
{
  "ok": false,
  "error": {
    "code": "BAD_GEOMETRY",
    "message": "Circle radius must be positive.",
    "offending": "radius",
    "hint": "Use radius > 0."
  }
}
```

- `code` — a stable machine code. Examples emitted by the tool layer: `BAD_GEOMETRY`, `MISSING_ARG`, `NO_BOUNDARY`, `BLOCK_NOT_FOUND`, `WALL_NOT_RESOLVABLE`, `OFFSET_OUT_OF_RANGE`, `BOUNDARY_NOT_FOUND`, `NOTHING_STAGED`, `UNKNOWN_TOOL`, `TOOL_ERROR`.
- `message` — human-readable explanation.
- `offending` — the parameter or value at fault (when known).
- `hint` — the **self-correction lever**: a concrete suggestion for the next attempt (e.g. "Place openings in a wall you drew with `draw_wall` in this same turn"). The agent loop feeds the whole envelope back as the tool result and continues.

## Why these conventions matter

- **Handles, not indices** keep references stable as the drawing changes between turns.
- **CAD-computed measurements** (and the `create_dimension` rule of passing geometry, not values) stop the model from fabricating numbers.
- **BYLAYER + a layer standard** keeps output consistent with office/project CAD standards and reviewable at a glance.
- **The uniform envelope** turns failures into productive retries instead of dead ends, and surfaces the failure reason to the engineer on the activity row.
