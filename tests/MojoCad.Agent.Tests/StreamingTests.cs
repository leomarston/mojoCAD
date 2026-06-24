using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MojoCad.Agent.Models;
using MojoCad.Agent.Streaming;
using Xunit;

namespace MojoCad.Agent.Tests
{
    /// <summary>
    /// The streaming layer is the part of the OpenRouter client we can exercise off the network: the SSE
    /// line parser (comment skipping + [DONE] termination) and the accumulator that reassembles a
    /// streamed assistant message - text deltas concatenated and tool-call fragments stitched by index.
    /// These behaviours decide whether tool calls fire correctly, so they're worth pinning precisely.
    /// </summary>
    public sealed class StreamingTests
    {
        // ----- SseParser -------------------------------------------------------------------------

        private static async Task<List<string>> ReadAll(string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            using var ms = new MemoryStream(bytes);
            var payloads = new List<string>();
            await foreach (var p in SseParser.ReadDataPayloadsAsync(ms, CancellationToken.None))
                payloads.Add(p);
            return payloads;
        }

        [Fact]
        public async Task SseParser_YieldsDataPayloads_SkipsCommentsAndBlankLines()
        {
            // ':' comment lines (keep-alives) and blank event separators must be ignored.
            string body =
                ": OPENROUTER PROCESSING\n" +
                "\n" +
                "data: {\"a\":1}\n" +
                "\n" +
                ": keep-alive\n" +
                "data: {\"b\":2}\n";

            var payloads = await ReadAll(body);

            Assert.Equal(new[] { "{\"a\":1}", "{\"b\":2}" }, payloads.ToArray());
        }

        [Fact]
        public async Task SseParser_TerminatesOnDoneSentinel()
        {
            string body =
                "data: {\"a\":1}\n" +
                "data: [DONE]\n" +
                "data: {\"never\":true}\n"; // must not be yielded - parser stops at [DONE]

            var payloads = await ReadAll(body);

            Assert.Single(payloads);
            Assert.Equal("{\"a\":1}", payloads[0]);
        }

        [Fact]
        public async Task SseParser_TrimsLeadingSpaceAfterPrefix_AndIgnoresEmptyData()
        {
            string body =
                "data:{\"tight\":1}\n" +   // no space after colon
                "data:   \n" +             // whitespace-only payload -> ignored
                "data: {\"spaced\":2}\n";

            var payloads = await ReadAll(body);

            Assert.Equal(new[] { "{\"tight\":1}", "{\"spaced\":2}" }, payloads.ToArray());
        }

        // ----- StreamingChatAccumulator ----------------------------------------------------------

        private static ChatChunk TextChunk(string content) => new ChatChunk
        {
            Choices = new List<ChunkChoice> { new ChunkChoice { Delta = new Delta { Content = content } } }
        };

        [Fact]
        public void Accumulator_ConcatenatesTextDeltas_AndRaisesPerDeltaCallback()
        {
            var acc = new StreamingChatAccumulator();
            var seen = new List<string>();
            acc.OnContentDelta = seen.Add;

            Assert.True(acc.Accept(TextChunk("Hello")));
            Assert.True(acc.Accept(TextChunk(", ")));
            Assert.True(acc.Accept(TextChunk("world")));

            Assert.Equal("Hello, world", acc.Content);
            Assert.Equal(new[] { "Hello", ", ", "world" }, seen.ToArray());
        }

        [Fact]
        public void Accumulator_AssemblesToolCalls_ById_Name_And_ConcatenatedArguments()
        {
            var acc = new StreamingChatAccumulator();

            // First fragment carries id + name; later fragments carry argument string pieces.
            acc.Accept(new ChatChunk
            {
                Choices = new List<ChunkChoice>
                {
                    new ChunkChoice { Delta = new Delta { ToolCalls = new List<DeltaToolCall>
                    {
                        new DeltaToolCall { Index = 0, Id = "call_1", Type = "function",
                            Function = new DeltaFunction { Name = "create_circle", Arguments = "{\"radius\":" } }
                    } } }
                }
            });
            acc.Accept(new ChatChunk
            {
                Choices = new List<ChunkChoice>
                {
                    new ChunkChoice { Delta = new Delta { ToolCalls = new List<DeltaToolCall>
                    {
                        new DeltaToolCall { Index = 0, Function = new DeltaFunction { Arguments = "5}" } }
                    } } }
                }
            });

            var calls = acc.BuildToolCalls();

            var call = Assert.Single(calls);
            Assert.Equal("call_1", call.Id);
            Assert.Equal("function", call.Type);
            Assert.Equal(0, call.StreamIndex);
            Assert.Equal("create_circle", call.Function.Name);
            Assert.Equal("{\"radius\":5}", call.Function.Arguments);
        }

        [Fact]
        public void Accumulator_KeepsParallelToolCalls_SeparateByIndex_InOrder()
        {
            var acc = new StreamingChatAccumulator();

            // Two interleaved tool calls keyed by index 0 and 1.
            acc.Accept(new ChatChunk
            {
                Choices = new List<ChunkChoice>
                {
                    new ChunkChoice { Delta = new Delta { ToolCalls = new List<DeltaToolCall>
                    {
                        new DeltaToolCall { Index = 1, Id = "b", Function = new DeltaFunction { Name = "two", Arguments = "{}" } },
                        new DeltaToolCall { Index = 0, Id = "a", Function = new DeltaFunction { Name = "one", Arguments = "{}" } }
                    } } }
                }
            });

            var calls = acc.BuildToolCalls();

            // SortedDictionary keys the builders by index, so the output is index-ordered regardless of arrival order.
            Assert.Equal(2, calls.Count);
            Assert.Equal("one", calls[0].Function.Name);
            Assert.Equal("two", calls[1].Function.Name);
        }

        [Fact]
        public void Accumulator_CapturesFinishReasonAndUsage()
        {
            var acc = new StreamingChatAccumulator();

            acc.Accept(new ChatChunk
            {
                Choices = new List<ChunkChoice> { new ChunkChoice { FinishReason = "tool_calls" } },
                Usage = new UsageInfo { PromptTokens = 12, CompletionTokens = 34, TotalTokens = 46, Cost = 0.0009 }
            });

            Assert.Equal("tool_calls", acc.FinishReason);
            Assert.NotNull(acc.Usage);
            Assert.Equal(12, acc.Usage!.PromptTokens);
            Assert.Equal(34, acc.Usage.CompletionTokens);
            Assert.Equal(0.0009, acc.Usage.Cost);
        }

        [Fact]
        public void Accumulator_ErrorChunk_ReturnsFalse_AndCapturesError()
        {
            var acc = new StreamingChatAccumulator();

            bool ok = acc.Accept(new ChatChunk
            {
                Error = new ErrorPayload { Code = 402, Message = "Insufficient credits" }
            });

            Assert.False(ok); // caller must stop consuming the stream
            Assert.NotNull(acc.Error);
            Assert.Equal(402, acc.Error!.Code);
            Assert.Equal("Insufficient credits", acc.Error.Message);
        }

        [Fact]
        public void Accumulator_BuildAssistantMessage_IncludesContentAndToolCalls()
        {
            var acc = new StreamingChatAccumulator();
            acc.Accept(TextChunk("Drawing it now."));
            acc.Accept(new ChatChunk
            {
                Choices = new List<ChunkChoice>
                {
                    new ChunkChoice { Delta = new Delta { ToolCalls = new List<DeltaToolCall>
                    {
                        new DeltaToolCall { Index = 0, Id = "call_x", Function = new DeltaFunction { Name = "emit_changeset", Arguments = "{}" } }
                    } } }
                }
            });

            var msg = acc.BuildAssistantMessage();

            Assert.Equal("assistant", msg.Role);
            Assert.Equal("Drawing it now.", msg.Content);
            Assert.NotNull(msg.ToolCalls);
            Assert.Single(msg.ToolCalls!);
            Assert.Equal("emit_changeset", msg.ToolCalls![0].Function.Name);
        }

        [Fact]
        public void Accumulator_BuildAssistantMessage_NullContent_WhenOnlyToolCalls()
        {
            var acc = new StreamingChatAccumulator();
            acc.Accept(new ChatChunk
            {
                Choices = new List<ChunkChoice>
                {
                    new ChunkChoice { Delta = new Delta { ToolCalls = new List<DeltaToolCall>
                    {
                        new DeltaToolCall { Index = 0, Id = "c", Function = new DeltaFunction { Name = "get_drawing_summary", Arguments = "{}" } }
                    } } }
                }
            });

            var msg = acc.BuildAssistantMessage();

            // An assistant turn that only requested tools carries null content (valid on the wire).
            Assert.Null(msg.Content);
            Assert.NotNull(msg.ToolCalls);
        }
    }
}
