namespace MojoCad.Agent.Tools
{
    /// <summary>Canonical tool names. One place so the registry, executor and tests never drift.</summary>
    public static class ToolNames
    {
        // Read / query (never stage)
        public const string GetDrawingSummary = "get_drawing_summary";
        public const string QueryEntities = "query_entities";
        public const string GetEntityProperties = "get_entity_properties";
        public const string Measure = "measure";

        // Write - primitives
        public const string CreatePolyline = "create_polyline";
        public const string CreateCircle = "create_circle";
        public const string CreateArc = "create_arc";
        public const string CreateRectangle = "create_rectangle";
        public const string CreateEllipse = "create_ellipse";

        // Write - annotation
        public const string CreateText = "create_text";
        public const string CreateDimension = "create_dimension";
        public const string CreateHatch = "create_hatch";

        // Write - blocks & layers
        public const string InsertBlock = "insert_block";
        public const string CreateLayer = "create_layer";
        public const string SetCurrentLayer = "set_current_layer";

        // Write - editing
        public const string MoveEntities = "move_entities";
        public const string CopyEntities = "copy_entities";
        public const string RotateEntities = "rotate_entities";
        public const string ScaleEntities = "scale_entities";
        public const string MirrorEntities = "mirror_entities";
        public const string OffsetEntities = "offset_entities";
        public const string ArrayEntities = "array_entities";
        public const string EraseEntities = "erase_entities";

        // Write - domain
        public const string DrawWall = "draw_wall";
        public const string PlaceOpening = "place_opening";
        public const string RouteMep = "route_mep";
        public const string PlaceSprinklers = "place_sprinklers";
        public const string AddRoomTag = "add_room_tag";

        // Power tool - full AutoCAD command surface (opt-in; off by default)
        public const string RunCommand = "run_command";

        // Control (no geometry)
        public const string PresentPlan = "present_plan";
        public const string AskClarification = "ask_clarification";
        public const string EmitChangeset = "emit_changeset";
    }
}
