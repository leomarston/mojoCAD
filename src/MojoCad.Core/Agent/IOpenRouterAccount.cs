using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MojoCad.Core.Agent
{
    /// <summary>
    /// The account-level OpenRouter surface the Settings UI needs: validate a pasted key and list the
    /// available models. Kept separate from the chat client so the settings screen depends only on Core.
    /// </summary>
    public interface IOpenRouterAccount
    {
        /// <summary>
        /// Validate a key against GET /api/v1/key. Returns credit/limit info on success; throws an
        /// <see cref="OpenRouterAccountException"/> with a typed reason on failure (401 invalid, etc.).
        /// </summary>
        Task<KeyInfo> ValidateKeyAsync(string apiKey, CancellationToken cancellationToken);

        /// <summary>List models from GET /api/v1/models, filtered to those that support tool calling.</summary>
        Task<IReadOnlyList<ModelInfo>> ListToolCapableModelsAsync(string apiKey, CancellationToken cancellationToken);
    }

    public sealed class KeyInfo
    {
        public bool IsValid { get; set; }
        public string? Label { get; set; }
        public double? Usage { get; set; }
        public double? Limit { get; set; }
        public double? LimitRemaining { get; set; }
        public bool IsFreeTier { get; set; }
    }

    public sealed class ModelInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int? ContextLength { get; set; }
        public double? PromptPricePerMTok { get; set; }
        public double? CompletionPricePerMTok { get; set; }
        public bool SupportsTools { get; set; }
    }

    public enum OpenRouterAccountError { InvalidKey, Network, RateLimited, Unknown }

    public sealed class OpenRouterAccountException : System.Exception
    {
        public OpenRouterAccountError Reason { get; }

        public OpenRouterAccountException(OpenRouterAccountError reason, string message)
            : base(message)
        {
            Reason = reason;
        }
    }
}
