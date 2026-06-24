using System.Collections.Generic;
using System.Threading.Tasks;
using MojoCad.Core.Changes;
using MojoCad.Core.Drawing;
using MojoCad.Core.Geometry;

namespace MojoCad.Core.Ports
{
    /// <summary>
    /// The single seam between mojoCAD and AutoCAD. <b>The only code that touches the AutoCAD
    /// <c>Database</c> lives behind this port.</b> The agent and UI depend on this abstraction, never
    /// on Autodesk types, which keeps those layers unit-testable away from AutoCAD.
    ///
    /// Read operations may be called from a background thread; the implementation is responsible for
    /// marshalling onto AutoCAD's main thread and taking any document lock it needs.
    /// </summary>
    public interface IAcadBridge
    {
        /// <summary>Whether a drawing document is currently active.</summary>
        bool HasActiveDocument { get; }

        /// <summary>Snapshot the active drawing for context injection (backs <c>get_drawing_summary</c>).</summary>
        Task<DrawingSummary> GetDrawingSummaryAsync();

        /// <summary>Query entities with optional filters (backs <c>query_entities</c>).</summary>
        Task<IReadOnlyList<EntityInfo>> QueryEntitiesAsync(EntityQuery query);

        /// <summary>Full, type-specific properties of the given handles (backs <c>get_entity_properties</c>).</summary>
        Task<IReadOnlyList<EntityDetail>> GetEntityPropertiesAsync(IReadOnlyList<string> handles);

        /// <summary>CAD-computed measurement (backs <c>measure</c>); never trust the model to compute geometry.</summary>
        Task<MeasureResult> MeasureAsync(string mode, IReadOnlyList<Pt>? points, IReadOnlyList<string>? handles);

        /// <summary>Resolve which of the supplied handles still exist (used to validate edit-tool arguments at stage time).</summary>
        Task<IReadOnlyList<string>> FilterExistingHandlesAsync(IReadOnlyList<string> handles);

        /// <summary>Whether a layer with this (case-insensitive) name exists.</summary>
        Task<bool> LayerExistsAsync(string name);

        /// <summary>Whether a block definition with this name exists.</summary>
        Task<bool> BlockExistsAsync(string name);

        /// <summary>The user's current implied selection as handles (for the "current selection" context chip).</summary>
        Task<IReadOnlyList<string>> GetCurrentSelectionAsync();

        /// <summary>Zoom/pan the active viewport to frame the given handles (used by "show me" affordances).</summary>
        Task ZoomToHandlesAsync(IReadOnlyList<string> handles);
    }
}
