using System.Collections.Generic;
using MojoCad.Core.Geometry;

namespace MojoCad.Core.Drawing
{
    /// <summary>Parameters for the <c>query_entities</c> read tool. All filters are optional and AND-combined.</summary>
    public sealed class EntityQuery
    {
        /// <summary>Restrict to a layer name.</summary>
        public string? Layer { get; set; }

        /// <summary>Restrict to DXF type names (e.g. "LWPOLYLINE", "CIRCLE").</summary>
        public List<string>? Types { get; set; }

        /// <summary>Window filter [xmin, ymin, xmax, ymax].</summary>
        public Bounds? Window { get; set; }

        /// <summary>Proximity filter centre.</summary>
        public Pt? NearPoint { get; set; }

        /// <summary>Proximity filter radius (requires <see cref="NearPoint"/>).</summary>
        public double? Radius { get; set; }

        /// <summary>Restrict to block references of this block name.</summary>
        public string? BlockName { get; set; }

        /// <summary>Hard cap on returned rows (default 200) to bound token cost.</summary>
        public int Limit { get; set; } = 200;
    }
}
