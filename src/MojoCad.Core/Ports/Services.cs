using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MojoCad.Core.Ports
{
    /// <summary>
    /// Secure, at-rest storage for the OpenRouter API key. The Windows implementation uses DPAPI
    /// (<c>ProtectedData</c>, CurrentUser scope) so the key is encrypted to the logged-in user and
    /// never written in clear text to disk, logs, the drawing, or the repo.
    /// </summary>
    public interface ISecureStore
    {
        void SaveApiKey(string apiKey);

        /// <summary>The decrypted key, or null if none stored.</summary>
        string? LoadApiKey();

        bool HasApiKey { get; }

        void DeleteApiKey();
    }

    /// <summary>
    /// Append-only audit trail of applied changes, one file per drawing. Records what was applied,
    /// when, by which model, and the plain-language summary - but never the API key or raw chain-of-thought.
    /// </summary>
    public interface IAuditLog
    {
        Task RecordAsync(AuditEntry entry);
        Task<IReadOnlyList<AuditEntry>> ReadAsync(string drawingKey);
    }

    public sealed class AuditEntry
    {
        public string DrawingKey { get; set; } = string.Empty;
        public string ChangeSetId { get; set; } = string.Empty;
        public string TimestampUtc { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public int AppliedOpCount { get; set; }
        public List<string> AppliedOpDescriptions { get; set; } = new List<string>();
        public string UndoLabel { get; set; } = string.Empty;
    }

    /// <summary>
    /// Pre-apply linting of a change set against the active standards (layer naming, BYLAYER discipline,
    /// life-safety sanity checks). Findings can be informational or <c>Blocking</c>.
    /// </summary>
    public interface IStandardsLint
    {
        IReadOnlyList<LintFinding> Inspect(MojoCad.Core.Changes.ChangeSet changeSet, MojoCad.Core.Settings.StandardsProfile standards);
    }

    public sealed class LintFinding
    {
        public string OpId { get; set; } = string.Empty;
        public MojoCad.Core.Changes.Severity Severity { get; set; }
        public string Message { get; set; } = string.Empty;
        public string? Hint { get; set; }
    }

    /// <summary>Persistence for chat transcripts so a conversation survives a palette close / AutoCAD restart.</summary>
    public interface IConversationStore
    {
        Task SaveAsync(string conversationId, string serializedTranscript);
        Task<string?> LoadAsync(string conversationId);
        Task<IReadOnlyList<string>> ListAsync();
        Task DeleteAsync(string conversationId);
    }
}
