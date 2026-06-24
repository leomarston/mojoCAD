using System.Linq;
using MojoCad.Agent.OpenRouter;
using Xunit;

namespace MojoCad.Agent.Tests
{
    /// <summary>
    /// The /key and /models response parsers are internal statics (exposed via InternalsVisibleTo) so we
    /// can verify them directly without an HTTP server. The two things that must be right: the tool-
    /// capability filter (only models whose supported_parameters contains "tools" are offered, because a
    /// non-tool model would break the whole agent) and the pricing conversion (OpenRouter quotes per-token
    /// USD as a string; we surface per-million-token doubles for the picker).
    /// </summary>
    public sealed class OpenRouterParseTests
    {
        // ----- ParseKeyInfo ----------------------------------------------------------------------

        [Fact]
        public void ParseKeyInfo_ReadsNestedDataObject()
        {
            string body = @"{ ""data"": {
                ""label"": ""my-key"",
                ""usage"": 1.25,
                ""limit"": 10.0,
                ""limit_remaining"": 8.75,
                ""is_free_tier"": true
            } }";

            var info = OpenRouterClient.ParseKeyInfo(body);

            Assert.True(info.IsValid);
            Assert.Equal("my-key", info.Label);
            Assert.Equal(1.25, info.Usage);
            Assert.Equal(10.0, info.Limit);
            Assert.Equal(8.75, info.LimitRemaining);
            Assert.True(info.IsFreeTier);
        }

        [Fact]
        public void ParseKeyInfo_NullLimit_BecomesNullNotZero()
        {
            // A null usage limit (unlimited key) must come back as null, not coerced to 0.
            string body = @"{ ""data"": { ""label"": ""k"", ""usage"": 0.5, ""limit"": null, ""is_free_tier"": false } }";

            var info = OpenRouterClient.ParseKeyInfo(body);

            Assert.Equal(0.5, info.Usage);
            Assert.Null(info.Limit);
            Assert.Null(info.LimitRemaining);
            Assert.False(info.IsFreeTier);
        }

        // ----- ParseModels -----------------------------------------------------------------------

        [Fact]
        public void ParseModels_FiltersToToolCapableModelsOnly()
        {
            string body = @"{ ""data"": [
                {
                    ""id"": ""anthropic/claude-opus-4.8"",
                    ""name"": ""Claude Opus 4.8"",
                    ""context_length"": 200000,
                    ""supported_parameters"": [""tools"", ""temperature""],
                    ""pricing"": { ""prompt"": ""0.000015"", ""completion"": ""0.000075"" }
                },
                {
                    ""id"": ""some/no-tools-model"",
                    ""name"": ""No Tools"",
                    ""supported_parameters"": [""temperature""],
                    ""pricing"": { ""prompt"": ""0.000001"", ""completion"": ""0.000002"" }
                }
            ] }";

            var models = OpenRouterClient.ParseModels(body);

            var model = Assert.Single(models);
            Assert.Equal("anthropic/claude-opus-4.8", model.Id);
            Assert.Equal("Claude Opus 4.8", model.Name);
            Assert.True(model.SupportsTools);
            Assert.Equal(200000, model.ContextLength);
        }

        [Fact]
        public void ParseModels_ConvertsPerTokenPricing_ToPerMillionTokens()
        {
            string body = @"{ ""data"": [
                {
                    ""id"": ""x/y"",
                    ""name"": ""XY"",
                    ""supported_parameters"": [""tools""],
                    ""pricing"": { ""prompt"": ""0.000015"", ""completion"": ""0.000075"" }
                }
            ] }";

            var model = Assert.Single(OpenRouterClient.ParseModels(body));

            // 0.000015 $/tok * 1e6 = 15 $/MTok ; 0.000075 -> 75 $/MTok.
            Assert.NotNull(model.PromptPricePerMTok);
            Assert.NotNull(model.CompletionPricePerMTok);
            Assert.Equal(15.0, model.PromptPricePerMTok!.Value, 6);
            Assert.Equal(75.0, model.CompletionPricePerMTok!.Value, 6);
        }

        [Fact]
        public void ParseModels_MissingPricing_LeavesPriceNull()
        {
            string body = @"{ ""data"": [
                { ""id"": ""x/y"", ""name"": ""XY"", ""supported_parameters"": [""tools""] }
            ] }";

            var model = Assert.Single(OpenRouterClient.ParseModels(body));

            Assert.Null(model.PromptPricePerMTok);
            Assert.Null(model.CompletionPricePerMTok);
        }

        [Fact]
        public void ParseModels_NoDataArray_ReturnsEmpty()
        {
            Assert.Empty(OpenRouterClient.ParseModels(@"{ ""error"": ""nope"" }"));
        }

        [Fact]
        public void ParseModels_SkipsModelsWithoutSupportedParameters()
        {
            // A model that doesn't advertise supported_parameters at all is treated as not tool-capable.
            string body = @"{ ""data"": [
                { ""id"": ""x/y"", ""name"": ""XY"" }
            ] }";

            Assert.Empty(OpenRouterClient.ParseModels(body));
        }
    }
}
