using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MojoCad.Agent.Tools;
using MojoCad.Core.Changes;
using Xunit;

namespace MojoCad.Agent.Tests
{
    /// <summary>
    /// The ToolExecutor is where a model's tool call becomes a staged <see cref="ProposedOp"/> (or a
    /// self-correctable error envelope). It must never touch the drawing - it only appends to the turn's
    /// ChangeSetBuilder - and it must enforce the geometry/handle guards. We drive it against the in-memory
    /// FakeAcadBridge so the whole surface is testable off AutoCAD.
    /// </summary>
    public sealed class ToolExecutorTests
    {
        private readonly FakeAcadBridge _bridge = new FakeAcadBridge();
        private readonly ChangeSetBuilder _builder = new ChangeSetBuilder("cs-test");
        private readonly ToolExecutor _executor;

        public ToolExecutorTests()
        {
            _executor = new ToolExecutor(_bridge, _builder);
        }

        private Task<ToolOutcome> Run(string tool, string args) =>
            _executor.ExecuteAsync(tool, args, CancellationToken.None);

        private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

        [Fact]
        public async Task CreateCircle_StagesOp_AndReturnsStagedTrue()
        {
            var outcome = await Run(ToolNames.CreateCircle, @"{ ""center"": [0,0], ""radius"": 5, ""layer"": ""A-WALL"" }");

            Assert.False(outcome.Failed);
            var root = Json(outcome.ResultJson);
            Assert.True(root.GetProperty("ok").GetBoolean());
            Assert.True(root.GetProperty("staged").GetBoolean());
            Assert.Equal("op-1", root.GetProperty("op_id").GetString());

            // The op was actually appended to the builder as a CreateCircleOp on the requested layer.
            Assert.Equal(1, _builder.Count);
            var op = Assert.IsType<CreateCircleOp>(_builder.Current.Ops.Single());
            Assert.Equal(5, op.Radius);
            Assert.Equal("A-WALL", op.Layer);
            // A create yields a provisional handle the model can reference later this turn.
            Assert.Contains("@op-1", _builder.ProvisionalHandles);
        }

        [Fact]
        public async Task CreateCircle_NonPositiveRadius_ReturnsErrorEnvelope_StagesNothing()
        {
            var outcome = await Run(ToolNames.CreateCircle, @"{ ""center"": [0,0], ""radius"": 0 }");

            Assert.True(outcome.Failed);
            var error = Json(outcome.ResultJson).GetProperty("error");
            Assert.Equal("BAD_GEOMETRY", error.GetProperty("code").GetString());
            Assert.Equal("radius", error.GetProperty("offending").GetString());
            Assert.False(Json(outcome.ResultJson).GetProperty("ok").GetBoolean());

            Assert.Equal(0, _builder.Count); // nothing staged on failure
        }

        [Fact]
        public async Task CreateCircle_MissingRadius_ReturnsMissingArgEnvelope()
        {
            var outcome = await Run(ToolNames.CreateCircle, @"{ ""center"": [0,0] }");

            Assert.True(outcome.Failed);
            Assert.Equal("MISSING_ARG", Json(outcome.ResultJson).GetProperty("error").GetProperty("code").GetString());
        }

        [Fact]
        public async Task EmitChangeset_WithNothingStaged_Fails()
        {
            var outcome = await Run(ToolNames.EmitChangeset, @"{ ""summary"": ""nothing here"" }");

            Assert.True(outcome.Failed);
            Assert.False(outcome.EmitChangeset);
            Assert.Equal("NOTHING_STAGED", Json(outcome.ResultJson).GetProperty("error").GetProperty("code").GetString());
        }

        [Fact]
        public async Task EmitChangeset_AfterStaging_SetsEmitFlagAndSummary()
        {
            await Run(ToolNames.CreateCircle, @"{ ""center"": [0,0], ""radius"": 2 }");

            var outcome = await Run(ToolNames.EmitChangeset, @"{ ""summary"": ""Add a 2-unit circle"" }");

            Assert.False(outcome.Failed);
            Assert.True(outcome.EmitChangeset);
            Assert.Equal("Add a 2-unit circle", outcome.ChangeSummary);
            var root = Json(outcome.ResultJson);
            Assert.True(root.GetProperty("emitted").GetBoolean());
            Assert.Equal(1, root.GetProperty("op_count").GetInt32());
        }

        [Fact]
        public async Task MoveEntities_WithNonExistentHandle_YieldsStaleHandle()
        {
            // The fake reports no existing handles, so the referenced handle is stale.
            var outcome = await Run(ToolNames.MoveEntities,
                @"{ ""ids"": [""DEADBEEF""], ""from"": [0,0], ""to"": [10,0] }");

            Assert.True(outcome.Failed);
            var error = Json(outcome.ResultJson).GetProperty("error");
            Assert.Equal("STALE_HANDLE", error.GetProperty("code").GetString());
            Assert.Contains("DEADBEEF", error.GetProperty("offending").GetString());
            Assert.Equal(0, _builder.Count);
        }

        [Fact]
        public async Task MoveEntities_WithExistingHandle_Stages()
        {
            _bridge.ExistingHandles.Add("2A");

            var outcome = await Run(ToolNames.MoveEntities,
                @"{ ""ids"": [""2A""], ""from"": [0,0], ""to"": [5,5] }");

            Assert.False(outcome.Failed);
            var op = Assert.IsType<MoveOp>(_builder.Current.Ops.Single());
            Assert.Contains("2A", op.TargetHandles);
            Assert.Equal(5, op.To.X);
        }

        [Fact]
        public async Task MoveEntities_CanReferenceProvisionalHandleStagedThisTurn()
        {
            // Stage a circle (gets provisional handle @op-1), then move it before anything is applied.
            await Run(ToolNames.CreateCircle, @"{ ""center"": [0,0], ""radius"": 1 }");

            var outcome = await Run(ToolNames.MoveEntities,
                @"{ ""ids"": [""@op-1""], ""delta"": [10, 0] }");

            Assert.False(outcome.Failed);
            Assert.Equal(2, _builder.Count);
        }

        [Fact]
        public async Task InsertBlock_UnknownBlock_FailsWithBlockNotFound()
        {
            var outcome = await Run(ToolNames.InsertBlock,
                @"{ ""block_name"": ""DOOR-A"", ""position"": [0,0] }");

            Assert.True(outcome.Failed);
            Assert.Equal("BLOCK_NOT_FOUND", Json(outcome.ResultJson).GetProperty("error").GetProperty("code").GetString());
        }

        [Fact]
        public async Task InsertBlock_KnownBlock_Stages()
        {
            _bridge.ExistingBlocks.Add("DOOR-A");

            var outcome = await Run(ToolNames.InsertBlock,
                @"{ ""block_name"": ""DOOR-A"", ""position"": [3,4], ""rotation"": 90 }");

            Assert.False(outcome.Failed);
            var op = Assert.IsType<InsertBlockOp>(_builder.Current.Ops.Single());
            Assert.Equal("DOOR-A", op.BlockName);
            Assert.Equal(90, op.RotationDeg);
        }

        [Fact]
        public async Task EraseEntities_DefaultsToWarningSeverity_SoItIsntSweptByAcceptAll()
        {
            _bridge.ExistingHandles.Add("FF");

            var outcome = await Run(ToolNames.EraseEntities, @"{ ""ids"": [""FF""] }");

            Assert.False(outcome.Failed);
            var op = Assert.IsType<EraseOp>(_builder.Current.Ops.Single());
            Assert.Equal(Severity.Warning, op.Severity);
        }

        [Fact]
        public async Task UnknownTool_ReturnsUnknownToolEnvelope()
        {
            var outcome = await Run("not_a_tool", "{}");

            Assert.True(outcome.Failed);
            Assert.Equal("UNKNOWN_TOOL", Json(outcome.ResultJson).GetProperty("error").GetProperty("code").GetString());
        }

        [Fact]
        public async Task PresentPlan_RaisesPlanSignal()
        {
            var outcome = await Run(ToolNames.PresentPlan,
                @"{ ""title"": ""Egress"", ""steps"": [""Measure corridor"", ""Place doors""] }");

            Assert.False(outcome.Failed);
            Assert.NotNull(outcome.Plan);
            Assert.Equal("Egress", outcome.Plan!.Title);
            Assert.Equal(2, outcome.Plan.Steps.Count);
        }

        [Fact]
        public async Task AskClarification_RaisesQuestionSignal()
        {
            var outcome = await Run(ToolNames.AskClarification,
                @"{ ""question"": ""What sprinkler spacing?"", ""options"": [""3m"", ""3.7m""] }");

            Assert.False(outcome.Failed);
            Assert.NotNull(outcome.Question);
            Assert.Equal("What sprinkler spacing?", outcome.Question!.Question);
            Assert.Equal(2, outcome.Question.Options.Count);
        }

        [Fact]
        public async Task GetDrawingSummary_IsReadOnly_StagesNothing()
        {
            var outcome = await Run(ToolNames.GetDrawingSummary, "{}");

            Assert.True(outcome.IsRead);
            Assert.False(outcome.Failed);
            Assert.Equal(0, _builder.Count);
        }
    }
}
