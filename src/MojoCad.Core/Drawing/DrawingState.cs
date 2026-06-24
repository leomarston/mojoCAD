using System.Collections.Generic;
using MojoCad.Core.Geometry;

namespace MojoCad.Core.Drawing
{
    /// <summary>
    /// A snapshot of the active drawing's context, produced by the Acad adapter and injected into the
    /// agent each turn (backing the <c>get_drawing_summary</c> read tool). AutoCAD-free so it can be
    /// serialised straight to JSON for the model and saved to the audit log.
    /// </summary>
    public sealed class DrawingSummary
    {
        public string? DocumentName { get; set; }

        /// <summary>Human label for the drawing units, e.g. "Millimeters", "Inches".</summary>
        public string Units { get; set; } = "Unitless";

        /// <summary>Drawing units per metre (e.g. 1000 for mm, 39.3701 for inches). Lets the agent convert human dimensions.</summary>
        public double UnitScalePerMeter { get; set; } = 1.0;

        /// <summary>Linear display precision (number of decimals).</summary>
        public int Precision { get; set; } = 4;

        public Bounds Extents { get; set; }

        public string CurrentLayer { get; set; } = "0";

        public List<LayerInfo> Layers { get; set; } = new List<LayerInfo>();

        public List<string> BlockDefinitions { get; set; } = new List<string>();

        public List<string> TextStyles { get; set; } = new List<string>();

        public List<string> DimStyles { get; set; } = new List<string>();

        public int EntityTotal { get; set; }

        /// <summary>The user's current implied selection, if any (handles + a short summary each).</summary>
        public List<EntityInfo> Selection { get; set; } = new List<EntityInfo>();
    }

    public sealed class LayerInfo
    {
        public string Name { get; set; } = string.Empty;
        public int ColorIndex { get; set; } = 7;
        public bool IsOn { get; set; } = true;
        public bool IsFrozen { get; set; }
        public bool IsLocked { get; set; }
        public string? Linetype { get; set; }
        public string? Description { get; set; }
    }

    /// <summary>A lightweight description of one entity for query results.</summary>
    public sealed class EntityInfo
    {
        /// <summary>The AutoCAD handle (persistent hex string) - the stable identity used across turns.</summary>
        public string Handle { get; set; } = string.Empty;

        /// <summary>DXF-style type name, e.g. "LWPOLYLINE", "CIRCLE", "MTEXT", "INSERT".</summary>
        public string Type { get; set; } = string.Empty;

        public string Layer { get; set; } = "0";

        /// <summary>One-line summary, e.g. "polyline, 4 verts, closed, len 12.40".</summary>
        public string Summary { get; set; } = string.Empty;

        public Bounds Bounds { get; set; }

        /// <summary>For block references, the block name.</summary>
        public string? BlockName { get; set; }
    }

    /// <summary>Full, type-specific properties of an entity (backing <c>get_entity_properties</c>).</summary>
    public sealed class EntityDetail
    {
        public string Handle { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Layer { get; set; } = "0";
        public Bounds Bounds { get; set; }

        /// <summary>
        /// Type-specific geometry and properties as a property bag (e.g. center, radius, vertices,
        /// text contents, rotation). Serialised verbatim to the model.
        /// </summary>
        public Dictionary<string, object?> Properties { get; set; } = new Dictionary<string, object?>();
    }

    /// <summary>Result of a CAD-computed measurement (backing the <c>measure</c> tool).</summary>
    public sealed class MeasureResult
    {
        public string Mode { get; set; } = string.Empty;
        public double Value { get; set; }
        public string Units { get; set; } = string.Empty;
    }
}
