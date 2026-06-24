using System.Collections.Generic;
using System.Text.Json;

namespace MojoCad.Agent.Tools
{
    /// <summary>
    /// Builds the JSON string returned to the model as a tool result. Success payloads are tool-specific;
    /// failures use the uniform envelope { ok:false, error:{ code, message, offending, hint } } whose
    /// <c>hint</c> is the model's self-correction lever.
    /// </summary>
    public static class ToolResult
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public static string Ok(object payload)
        {
            var wrapped = new Dictionary<string, object?> { ["ok"] = true };
            if (payload is IDictionary<string, object?> dict)
            {
                foreach (var kv in dict) wrapped[kv.Key] = kv.Value;
            }
            else
            {
                wrapped["result"] = payload;
            }
            return JsonSerializer.Serialize(wrapped, Options);
        }

        public static string Staged(string opId, string? provisionalHandle, string message, object? extra = null)
        {
            var payload = new Dictionary<string, object?>
            {
                ["staged"] = true,
                ["op_id"] = opId,
                ["message"] = message
            };
            if (provisionalHandle != null) payload["handle"] = provisionalHandle;
            if (extra is IDictionary<string, object?> ex)
                foreach (var kv in ex) payload[kv.Key] = kv.Value;
            return Ok(payload);
        }

        public static string Error(string code, string message, string? offending = null, string? hint = null)
        {
            var env = new Dictionary<string, object?>
            {
                ["ok"] = false,
                ["error"] = new Dictionary<string, object?>
                {
                    ["code"] = code,
                    ["message"] = message,
                    ["offending"] = offending,
                    ["hint"] = hint
                }
            };
            return JsonSerializer.Serialize(env, Options);
        }
    }
}
