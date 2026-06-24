using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MojoCad.Agent.Models;

namespace MojoCad.Agent.Streaming
{
    /// <summary>
    /// Reads an OpenRouter Server-Sent-Events stream and yields the JSON payload of each <c>data:</c>
    /// line. Skips SSE comment/keep-alive lines (those starting with ':', e.g. ": OPENROUTER PROCESSING")
    /// and terminates on the literal <c>data: [DONE]</c> sentinel.
    /// </summary>
    public static class SseParser
    {
        public static async IAsyncEnumerable<string> ReadDataPayloadsAsync(
            Stream stream,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var reader = new StreamReader(stream);
            while (!reader.EndOfStream)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null) break;

                // Blank lines separate SSE events; comment lines start with ':'.
                if (line.Length == 0 || line[0] == ':') continue;

                const string prefix = "data:";
                if (!line.StartsWith(prefix, System.StringComparison.Ordinal)) continue;

                string payload = line.Substring(prefix.Length).TrimStart();
                if (payload.Length == 0) continue;
                if (payload == "[DONE]") yield break;

                yield return payload;
            }
        }
    }

    /// <summary>
    /// Accumulates streamed <see cref="ChatChunk"/>s into a final assistant message. Text increments
    /// arrive in <c>delta.content</c>; tool calls arrive as fragments keyed by <c>index</c> (the first
    /// fragment per index carries id/name, later fragments carry argument string pieces, which we
    /// concatenate). Pure and synchronous so it can be unit-tested without any network.
    /// </summary>
    public sealed class StreamingChatAccumulator
    {
        private readonly System.Text.StringBuilder _content = new System.Text.StringBuilder();
        private readonly SortedDictionary<int, ToolCallBuilder> _toolCalls = new SortedDictionary<int, ToolCallBuilder>();

        public string? FinishReason { get; private set; }
        public UsageInfo? Usage { get; private set; }
        public ErrorPayload? Error { get; private set; }

        /// <summary>Raised for each non-empty text increment, so the UI can stream tokens live.</summary>
        public System.Action<string>? OnContentDelta { get; set; }

        /// <summary>Feed one parsed chunk. Returns false if the stream signalled an error (caller should stop).</summary>
        public bool Accept(ChatChunk chunk)
        {
            if (chunk.Error != null)
            {
                Error = chunk.Error;
                return false;
            }

            if (chunk.Usage != null)
                Usage = chunk.Usage;

            if (chunk.Choices == null) return true;

            foreach (var choice in chunk.Choices)
            {
                if (choice.FinishReason != null)
                    FinishReason = choice.FinishReason;

                var delta = choice.Delta;
                if (delta == null) continue;

                if (!string.IsNullOrEmpty(delta.Content))
                {
                    _content.Append(delta.Content);
                    OnContentDelta?.Invoke(delta.Content!);
                }

                if (delta.ToolCalls != null)
                {
                    foreach (var tc in delta.ToolCalls)
                    {
                        if (!_toolCalls.TryGetValue(tc.Index, out var builder))
                        {
                            builder = new ToolCallBuilder { Index = tc.Index };
                            _toolCalls[tc.Index] = builder;
                        }
                        if (!string.IsNullOrEmpty(tc.Id)) builder.Id = tc.Id!;
                        if (!string.IsNullOrEmpty(tc.Type)) builder.Type = tc.Type!;
                        if (tc.Function != null)
                        {
                            if (!string.IsNullOrEmpty(tc.Function.Name)) builder.Name = tc.Function.Name!;
                            if (tc.Function.Arguments != null) builder.Arguments.Append(tc.Function.Arguments);
                        }
                    }
                }
            }

            return true;
        }

        public string Content => _content.ToString();

        public IReadOnlyList<ToolCall> BuildToolCalls()
        {
            var list = new List<ToolCall>();
            foreach (var kv in _toolCalls)
            {
                var b = kv.Value;
                list.Add(new ToolCall
                {
                    Id = b.Id,
                    Type = string.IsNullOrEmpty(b.Type) ? "function" : b.Type,
                    StreamIndex = b.Index,
                    Function = new FunctionCall { Name = b.Name, Arguments = b.Arguments.ToString() }
                });
            }
            return list;
        }

        /// <summary>Build the assistant message to append to the conversation (content + any tool calls).</summary>
        public ChatMessage BuildAssistantMessage()
        {
            var calls = BuildToolCalls();
            return new ChatMessage
            {
                Role = "assistant",
                Content = _content.Length > 0 ? _content.ToString() : null,
                ToolCalls = calls.Count > 0 ? new List<ToolCall>(calls) : null
            };
        }

        private sealed class ToolCallBuilder
        {
            public int Index;
            public string Id = string.Empty;
            public string Type = "function";
            public string Name = string.Empty;
            public System.Text.StringBuilder Arguments = new System.Text.StringBuilder();
        }
    }
}
