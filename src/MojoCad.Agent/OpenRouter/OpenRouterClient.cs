using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MojoCad.Agent.Models;
using MojoCad.Agent.Streaming;
using MojoCad.Core.Agent;

namespace MojoCad.Agent.OpenRouter
{
    /// <summary>Result of one streamed chat step.</summary>
    public sealed class ChatStreamResult
    {
        public ChatMessage AssistantMessage { get; set; } = new ChatMessage { Role = "assistant" };
        public string? FinishReason { get; set; }
        public UsageInfo? Usage { get; set; }
    }

    /// <summary>Low-level OpenRouter chat surface used by the agent loop.</summary>
    public interface IOpenRouterClient
    {
        Task<ChatStreamResult> StreamChatAsync(ChatRequest request, string apiKey, Action<string>? onContentDelta, CancellationToken cancellationToken);
    }

    /// <summary>Typed transport error mapped from OpenRouter's HTTP status / error code.</summary>
    public sealed class OpenRouterException : Exception
    {
        public int? StatusCode { get; }
        public AgentErrorKind Kind { get; }
        public TimeSpan? RetryAfter { get; }

        public OpenRouterException(AgentErrorKind kind, string message, int? statusCode = null, TimeSpan? retryAfter = null)
            : base(message)
        {
            Kind = kind;
            StatusCode = statusCode;
            RetryAfter = retryAfter;
        }
    }

    /// <summary>
    /// OpenRouter client. Implements both the low-level chat surface (<see cref="IOpenRouterClient"/>)
    /// and the account surface the Settings UI needs (<see cref="IOpenRouterAccount"/>). One shared
    /// <see cref="HttpClient"/>; streaming relies on the cancellation token rather than a request timeout.
    /// </summary>
    public sealed class OpenRouterClient : IOpenRouterClient, IOpenRouterAccount, IDisposable
    {
        private const string BaseUrl = "https://openrouter.ai/api/v1";
        private const string Referer = "https://mojocad.app";
        private const string Title = "mojoCAD";

        private readonly HttpClient _http;

        public OpenRouterClient(HttpClient? http = null)
        {
            _http = http ?? new HttpClient();
            // Streaming responses can run for minutes; control lifetime with the cancellation token.
            _http.Timeout = Timeout.InfiniteTimeSpan;
        }

        // ----- Chat (streaming) ----------------------------------------------------------------

        public async Task<ChatStreamResult> StreamChatAsync(
            ChatRequest request, string apiKey, Action<string>? onContentDelta, CancellationToken cancellationToken)
        {
            request.Stream = true;
            string json = JsonSerializer.Serialize(request, WireJson.Options);

            using var httpReq = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/chat/completions");
            ApplyHeaders(httpReq, apiKey);
            httpReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            httpReq.Content = new StringContent(json, Encoding.UTF8, "application/json");

            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new OpenRouterException(AgentErrorKind.Network, "Could not reach OpenRouter: " + ex.Message);
            }

            using (resp)
            {
                if (!resp.IsSuccessStatusCode)
                    throw await MapErrorAsync(resp).ConfigureAwait(false);

                var accumulator = new StreamingChatAccumulator { OnContentDelta = onContentDelta };

#if NET8_0_OR_GREATER
                using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#else
                using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
#endif
                await foreach (var payload in SseParser.ReadDataPayloadsAsync(stream, cancellationToken).ConfigureAwait(false))
                {
                    ChatChunk? chunk;
                    try
                    {
                        chunk = JsonSerializer.Deserialize<ChatChunk>(payload, WireJson.Options);
                    }
                    catch (JsonException)
                    {
                        continue; // ignore an unparseable keep-alive / partial line
                    }
                    if (chunk == null) continue;

                    if (!accumulator.Accept(chunk))
                    {
                        var err = accumulator.Error;
                        throw new OpenRouterException(
                            MapCodeToKind(err?.Code),
                            err?.Message ?? "The provider returned an error mid-stream.",
                            err?.Code);
                    }
                }

                return new ChatStreamResult
                {
                    AssistantMessage = accumulator.BuildAssistantMessage(),
                    FinishReason = accumulator.FinishReason,
                    Usage = accumulator.Usage
                };
            }
        }

        // ----- Account: validate key ------------------------------------------------------------

        public async Task<KeyInfo> ValidateKeyAsync(string apiKey, CancellationToken cancellationToken)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/key");
            ApplyHeaders(req, apiKey);

            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                throw new OpenRouterAccountException(OpenRouterAccountError.Network, "Could not reach OpenRouter: " + ex.Message);
            }

            using (resp)
            {
                if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                    throw new OpenRouterAccountException(OpenRouterAccountError.InvalidKey, "The API key was rejected (401/403).");
                if ((int)resp.StatusCode == 429)
                    throw new OpenRouterAccountException(OpenRouterAccountError.RateLimited, "Rate limited while validating the key.");
                if (!resp.IsSuccessStatusCode)
                    throw new OpenRouterAccountException(OpenRouterAccountError.Unknown, "Unexpected status " + (int)resp.StatusCode + " from /key.");

                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return ParseKeyInfo(body);
            }
        }

        internal static KeyInfo ParseKeyInfo(string body)
        {
            using var doc = JsonDocument.Parse(body);
            // Shape: { "data": { "label", "usage", "limit", "limit_remaining", "is_free_tier", ... } }
            JsonElement data = doc.RootElement.TryGetProperty("data", out var d) ? d : doc.RootElement;

            double? GetD(string name) =>
                data.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : (double?)null;

            return new KeyInfo
            {
                IsValid = true,
                Label = data.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null,
                Usage = GetD("usage"),
                Limit = GetD("limit"),
                LimitRemaining = GetD("limit_remaining"),
                IsFreeTier = data.TryGetProperty("is_free_tier", out var f) && f.ValueKind == JsonValueKind.True
            };
        }

        // ----- Account: list models -------------------------------------------------------------

        public async Task<IReadOnlyList<ModelInfo>> ListToolCapableModelsAsync(string apiKey, CancellationToken cancellationToken)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/models");
            ApplyHeaders(req, apiKey); // /models is public, but sending the key is harmless and consistent.

            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                throw new OpenRouterAccountException(OpenRouterAccountError.Network, "Could not reach OpenRouter: " + ex.Message);
            }

            using (resp)
            {
                if (!resp.IsSuccessStatusCode)
                    throw new OpenRouterAccountException(OpenRouterAccountError.Unknown, "Unexpected status " + (int)resp.StatusCode + " from /models.");
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return ParseModels(body);
            }
        }

        internal static IReadOnlyList<ModelInfo> ParseModels(string body)
        {
            var result = new List<ModelInfo>();
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var m in data.EnumerateArray())
            {
                bool supportsTools = false;
                if (m.TryGetProperty("supported_parameters", out var sp) && sp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in sp.EnumerateArray())
                        if (p.ValueKind == JsonValueKind.String && p.GetString() == "tools") { supportsTools = true; break; }
                }
                if (!supportsTools) continue;

                var info = new ModelInfo
                {
                    Id = m.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    Name = m.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "",
                    SupportsTools = true,
                    ContextLength = m.TryGetProperty("context_length", out var cl) && cl.ValueKind == JsonValueKind.Number ? cl.GetInt32() : (int?)null
                };

                if (m.TryGetProperty("pricing", out var pr) && pr.ValueKind == JsonValueKind.Object)
                {
                    info.PromptPricePerMTok = ParsePrice(pr, "prompt");
                    info.CompletionPricePerMTok = ParsePrice(pr, "completion");
                }
                if (!string.IsNullOrEmpty(info.Id)) result.Add(info);
            }
            return result;
        }

        private static double? ParsePrice(JsonElement pricing, string field)
        {
            // OpenRouter quotes per-token USD as a string; convert to per-million-tokens.
            if (pricing.TryGetProperty(field, out var e) && e.ValueKind == JsonValueKind.String &&
                double.TryParse(e.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var perTok))
                return perTok * 1_000_000.0;
            return null;
        }

        // ----- shared ---------------------------------------------------------------------------

        private static void ApplyHeaders(HttpRequestMessage req, string apiKey)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            req.Headers.TryAddWithoutValidation("HTTP-Referer", Referer);
            req.Headers.TryAddWithoutValidation("X-Title", Title);
        }

        private static async Task<OpenRouterException> MapErrorAsync(HttpResponseMessage resp)
        {
            int code = (int)resp.StatusCode;
            string? message = null;
            try
            {
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(body))
                {
                    var env = JsonSerializer.Deserialize<ErrorEnvelope>(body, WireJson.Options);
                    message = env?.Error?.Message;
                }
            }
            catch { /* fall through to a generic message */ }

            TimeSpan? retryAfter = resp.Headers.RetryAfter?.Delta;
            return new OpenRouterException(MapCodeToKind(code), message ?? DefaultMessage(code), code, retryAfter);
        }

        private static AgentErrorKind MapCodeToKind(int? code) => code switch
        {
            401 => AgentErrorKind.Auth,
            402 => AgentErrorKind.InsufficientCredits,
            403 => AgentErrorKind.Moderation,
            408 => AgentErrorKind.Network,
            429 => AgentErrorKind.RateLimited,
            400 => AgentErrorKind.BadRequest,
            502 => AgentErrorKind.Provider,
            503 => AgentErrorKind.Provider,
            _ => AgentErrorKind.Unknown
        };

        private static string DefaultMessage(int code) => code switch
        {
            401 => "Invalid or missing API key.",
            402 => "Insufficient OpenRouter credits.",
            403 => "The request was blocked by content moderation.",
            408 => "The request timed out.",
            429 => "Rate limited. Please retry shortly.",
            502 => "The upstream model provider is unavailable.",
            503 => "The model provider is temporarily unavailable.",
            _ => "OpenRouter returned status " + code + "."
        };

        public void Dispose() => _http.Dispose();
    }
}
