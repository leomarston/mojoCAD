using MojoCad.Agent.Tools;
using Xunit;

namespace MojoCad.Agent.Tests
{
    /// <summary>
    /// ToolArgs is the typed reader over a model's raw JSON tool arguments. Its whole point is to fail
    /// LOUD with a specific, self-correctable error code rather than silently coercing bad input - so we
    /// verify both the happy-path readers and that the right code (MISSING_ARG / BAD_ENUM / etc.) comes
    /// back for each failure mode. A wrong code here would rob the model of its self-correction lever.
    /// </summary>
    public sealed class ToolArgsTests
    {
        [Fact]
        public void GetPoint_ReadsXY_AndDefaultsZ()
        {
            var args = new ToolArgs(@"{ ""center"": [3, 4] }");
            var p = args.GetPoint("center");

            Assert.Equal(3, p.X);
            Assert.Equal(4, p.Y);
            Assert.Equal(0, p.Z);
        }

        [Fact]
        public void GetPoint_ReadsXYZ()
        {
            var args = new ToolArgs(@"{ ""center"": [1, 2, 9] }");
            var p = args.GetPoint("center");

            Assert.Equal(9, p.Z);
        }

        [Fact]
        public void GetPoints_ReadsList_AndEnforcesMinCount()
        {
            var args = new ToolArgs(@"{ ""points"": [[0,0],[10,0],[10,10]] }");
            var pts = args.GetPoints("points", 2);

            Assert.Equal(3, pts.Count);
            Assert.Equal(10, pts[1].X);
        }

        [Fact]
        public void GetPoints_FewerThanMin_ThrowsNotEnoughPoints()
        {
            var args = new ToolArgs(@"{ ""points"": [[0,0]] }");

            var ex = Assert.Throws<ToolArgException>(() => args.GetPoints("points", 2));
            Assert.Equal("NOT_ENOUGH_POINTS", ex.Code);
        }

        [Fact]
        public void GetEnum_AcceptsAllowedValue_CaseInsensitively_AndCanonicalises()
        {
            var args = new ToolArgs(@"{ ""kind"": ""LINEAR"" }");
            string val = args.GetEnum("kind", new[] { "linear", "aligned" });

            // Returns the canonical allowed spelling, not the model's casing.
            Assert.Equal("linear", val);
        }

        [Fact]
        public void GetEnum_BadValue_ThrowsBadEnum_WithOffending()
        {
            var args = new ToolArgs(@"{ ""kind"": ""banana"" }");

            var ex = Assert.Throws<ToolArgException>(() => args.GetEnum("kind", new[] { "linear", "aligned" }));
            Assert.Equal("BAD_ENUM", ex.Code);
            Assert.Equal("banana", ex.Offending);
        }

        [Fact]
        public void GetEnum_Missing_WithFallback_ReturnsFallback()
        {
            var args = new ToolArgs("{}");
            Assert.Equal("left", args.GetEnum("justify", new[] { "left", "right" }, "left"));
        }

        [Fact]
        public void GetEnum_Missing_NoFallback_ThrowsMissingArg()
        {
            var args = new ToolArgs("{}");

            var ex = Assert.Throws<ToolArgException>(() => args.GetEnum("kind", new[] { "linear" }));
            Assert.Equal("MISSING_ARG", ex.Code);
        }

        [Fact]
        public void GetStringList_ReadsArray_AndEnforcesMinCount()
        {
            var args = new ToolArgs(@"{ ""ids"": [""A1"", ""B2""] }");
            var ids = args.GetStringList("ids");

            Assert.Equal(new[] { "A1", "B2" }, ids.ToArray());
        }

        [Fact]
        public void GetStringList_Empty_ThrowsEmptyList()
        {
            var args = new ToolArgs(@"{ ""ids"": [] }");

            var ex = Assert.Throws<ToolArgException>(() => args.GetStringList("ids"));
            Assert.Equal("EMPTY_LIST", ex.Code);
        }

        [Fact]
        public void GetString_MissingRequired_ThrowsMissingArg_WithOffendingName()
        {
            var args = new ToolArgs("{}");

            var ex = Assert.Throws<ToolArgException>(() => args.GetString("name"));
            Assert.Equal("MISSING_ARG", ex.Code);
            Assert.Equal("name", ex.Offending);
        }

        [Fact]
        public void GetDouble_WrongType_ThrowsBadArg()
        {
            var args = new ToolArgs(@"{ ""radius"": ""five"" }");

            var ex = Assert.Throws<ToolArgException>(() => args.GetDouble("radius"));
            Assert.Equal("BAD_ARG", ex.Code);
        }

        [Fact]
        public void GetPoint_NonNumericCoordinate_ThrowsBadArg()
        {
            var args = new ToolArgs(@"{ ""center"": [""x"", 4] }");

            var ex = Assert.Throws<ToolArgException>(() => args.GetPoint("center"));
            Assert.Equal("BAD_ARG", ex.Code);
        }

        [Fact]
        public void Constructor_InvalidJson_ThrowsInvalidJson()
        {
            var ex = Assert.Throws<ToolArgException>(() => new ToolArgs("{ not json"));
            Assert.Equal("INVALID_JSON", ex.Code);
        }

        [Fact]
        public void GetProps_ReadsOverrides_AndReturnsNullWhenEmpty()
        {
            var withProps = new ToolArgs(@"{ ""props"": { ""color"": 3, ""linetype"": ""HIDDEN"" } }");
            var p = withProps.GetPropsOrNull();
            Assert.NotNull(p);
            Assert.Equal(3, p!.ColorIndex);
            Assert.Equal("HIDDEN", p.Linetype);

            // An empty props object carries no overrides -> null.
            var emptyProps = new ToolArgs(@"{ ""props"": {} }");
            Assert.Null(emptyProps.GetPropsOrNull());
        }
    }
}
