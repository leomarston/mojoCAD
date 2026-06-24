using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MojoCad.Core.Drawing;
using MojoCad.Core.Geometry;
using MojoCad.Core.Ports;

namespace MojoCad.Agent.Tests
{
    /// <summary>
    /// An in-memory <see cref="IAcadBridge"/> that lets us exercise the ToolExecutor entirely off AutoCAD.
    /// It records nothing about the drawing except the few facts the executor consults at stage time:
    /// which handles exist (for STALE_HANDLE validation) and which blocks/layers exist. Everything else
    /// returns a benign empty result - the executor's write path never reads geometry, it only stages.
    /// </summary>
    internal sealed class FakeAcadBridge : IAcadBridge
    {
        public HashSet<string> ExistingHandles { get; } = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ExistingBlocks { get; } = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ExistingLayers { get; } = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        public bool HasActiveDocument => true;

        public Task<DrawingSummary> GetDrawingSummaryAsync() => Task.FromResult(new DrawingSummary());

        public Task<IReadOnlyList<EntityInfo>> QueryEntitiesAsync(EntityQuery query) =>
            Task.FromResult<IReadOnlyList<EntityInfo>>(new List<EntityInfo>());

        public Task<IReadOnlyList<EntityDetail>> GetEntityPropertiesAsync(IReadOnlyList<string> handles) =>
            Task.FromResult<IReadOnlyList<EntityDetail>>(new List<EntityDetail>());

        public Task<MeasureResult> MeasureAsync(string mode, IReadOnlyList<Pt>? points, IReadOnlyList<string>? handles) =>
            Task.FromResult(new MeasureResult { Mode = mode, Value = 0, Units = "mm" });

        public Task<IReadOnlyList<string>> FilterExistingHandlesAsync(IReadOnlyList<string> handles) =>
            Task.FromResult<IReadOnlyList<string>>(handles.Where(ExistingHandles.Contains).ToList());

        public Task<bool> LayerExistsAsync(string name) => Task.FromResult(ExistingLayers.Contains(name));

        public Task<bool> BlockExistsAsync(string name) => Task.FromResult(ExistingBlocks.Contains(name));

        public Task<IReadOnlyList<string>> GetCurrentSelectionAsync() =>
            Task.FromResult<IReadOnlyList<string>>(new List<string>());

        public Task ZoomToHandlesAsync(IReadOnlyList<string> handles) => Task.CompletedTask;
    }
}
