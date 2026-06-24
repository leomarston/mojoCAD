using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MojoCad.Agent.Models
{
    /// <summary>
    /// OpenRouter / OpenAI-compatible chat-completions wire model. Only the fields mojoCAD uses are
    /// modelled. Serialised with <see cref="WireJson.Options"/> (camelCase off; explicit names; nulls ignored).
    /// </summary>
    public sealed class ChatRequest
    {
        /// <summary>Primary model id (e.g. "anthropic/claude-opus-4.8").</summary>
        [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;

        /// <summary>Ordered fallback list for OpenRouter auto-failover. Sent in addition to <see cref="Model"/>.</summary>
        [JsonPropertyName("models")] public List<string>? Models { get; set; }

        [JsonPropertyName("messages")] public List<ChatMessage> Messages { get; set; } = new List<ChatMessage>();

        [JsonPropertyName("tools")] public List<ToolDef>? Tools { get; set; }

        [JsonPropertyName("tool_choice")] public string? ToolChoice { get; set; }

        /// <summary>We want ordered, deterministic CAD mutations, so this is false.</summary>
        [JsonPropertyName("parallel_tool_calls")] public bool ParallelToolCalls { get; set; }

        [JsonPropertyName("temperature")] public double Temperature { get; set; } = 0.2;

        [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; } = 4096;

        [JsonPropertyName("stream")] public bool Stream { get; set; } = true;

        [JsonPropertyName("provider")] public ProviderPrefs? Provider { get; set; }

        [JsonPropertyName("usage")] public UsageRequest? Usage { get; set; }

        [JsonPropertyName("reasoning")] public ReasoningPrefs? Reasoning { get; set; }
    }

    public sealed class ProviderPrefs
    {
        /// <summary>Guarantees the routed provider actually supports the parameters we send (incl. tools).</summary>
        [JsonPropertyName("require_parameters")] public bool RequireParameters { get; set; } = true;

        /// <summary>"deny" stops providers from logging/training on the prompt - protects proprietary CAD data.</summary>
        [JsonPropertyName("data_collection")] public string? DataCollection { get; set; } = "deny";
    }

    public sealed class UsageRequest
    {
        [JsonPropertyName("include")] public bool Include { get; set; } = true;
    }

    public sealed class ReasoningPrefs
    {
        [JsonPropertyName("effort")] public string? Effort { get; set; }
    }

    public sealed class ChatMessage
    {
        [JsonPropertyName("role")] public string Role { get; set; } = "user";

        /// <summary>Plain text content. Null is valid for an assistant message that only contains tool calls.</summary>
        [JsonPropertyName("content")] public string? Content { get; set; }

        /// <summary>For assistant messages that requested tools.</summary>
        [JsonPropertyName("tool_calls")] public List<ToolCall>? ToolCalls { get; set; }

        /// <summary>For role="tool" messages: which call this result answers.</summary>
        [JsonPropertyName("tool_call_id")] public string? ToolCallId { get; set; }

        /// <summary>Optional name (e.g. tool name on a tool message).</summary>
        [JsonPropertyName("name")] public string? Name { get; set; }

        public static ChatMessage System(string content) => new ChatMessage { Role = "system", Content = content };
        public static ChatMessage User(string content) => new ChatMessage { Role = "user", Content = content };
        public static ChatMessage Tool(string toolCallId, string content) =>
            new ChatMessage { Role = "tool", ToolCallId = toolCallId, Content = content };
    }

    public sealed class ToolCall
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("type")] public string Type { get; set; } = "function";
        [JsonPropertyName("function")] public FunctionCall Function { get; set; } = new FunctionCall();

        /// <summary>Index used while assembling streamed tool-call fragments (not serialised on requests we send).</summary>
        [JsonIgnore] public int StreamIndex { get; set; }
    }

    public sealed class FunctionCall
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

        /// <summary>JSON-encoded argument object (a STRING on the wire) - deserialize before use.</summary>
        [JsonPropertyName("arguments")] public string Arguments { get; set; } = string.Empty;
    }

    public sealed class ToolDef
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "function";
        [JsonPropertyName("function")] public FunctionDef Function { get; set; } = new FunctionDef();
    }

    public sealed class FunctionDef
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;

        /// <summary>JSON Schema object describing the parameters.</summary>
        [JsonPropertyName("parameters")] public JsonElement Parameters { get; set; }
    }

    // ----- Streaming response (chat.completion.chunk) -------------------------------------------

    public sealed class ChatChunk
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("choices")] public List<ChunkChoice>? Choices { get; set; }
        [JsonPropertyName("usage")] public UsageInfo? Usage { get; set; }
        [JsonPropertyName("error")] public ErrorPayload? Error { get; set; }
    }

    public sealed class ChunkChoice
    {
        [JsonPropertyName("delta")] public Delta? Delta { get; set; }
        [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
    }

    public sealed class Delta
    {
        [JsonPropertyName("content")] public string? Content { get; set; }
        [JsonPropertyName("tool_calls")] public List<DeltaToolCall>? ToolCalls { get; set; }
    }

    public sealed class DeltaToolCall
    {
        [JsonPropertyName("index")] public int Index { get; set; }
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("function")] public DeltaFunction? Function { get; set; }
    }

    public sealed class DeltaFunction
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("arguments")] public string? Arguments { get; set; }
    }

    public sealed class UsageInfo
    {
        [JsonPropertyName("prompt_tokens")] public int PromptTokens { get; set; }
        [JsonPropertyName("completion_tokens")] public int CompletionTokens { get; set; }
        [JsonPropertyName("total_tokens")] public int TotalTokens { get; set; }

        /// <summary>OpenRouter cost in USD, when "usage.include" was requested.</summary>
        [JsonPropertyName("cost")] public double? Cost { get; set; }
    }

    public sealed class ErrorPayload
    {
        [JsonPropertyName("code")] public int? Code { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("metadata")] public JsonElement Metadata { get; set; }
    }

    public sealed class ErrorEnvelope
    {
        [JsonPropertyName("error")] public ErrorPayload? Error { get; set; }
    }

    public static class WireJson
    {
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true
        };
    }
}
