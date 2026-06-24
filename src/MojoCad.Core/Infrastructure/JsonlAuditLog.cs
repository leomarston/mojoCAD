using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MojoCad.Core.Ports;

namespace MojoCad.Core.Infrastructure
{
    /// <summary>
    /// Append-only audit trail, one JSON-lines file per drawing under %APPDATA%\mojoCAD\audit. Records
    /// only what was applied (summary, op descriptions, model, timestamp) - never the API key and never
    /// the model's raw reasoning. This is the accountability record for life-safety work.
    /// </summary>
    public sealed class JsonlAuditLog : IAuditLog
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private static readonly object Gate = new object();

        public Task RecordAsync(AuditEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));

            MojoPaths.EnsureCreated();
            string file = FileFor(entry.DrawingKey);
            string line = JsonSerializer.Serialize(entry, Options);

            lock (Gate)
            {
                File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditEntry>> ReadAsync(string drawingKey)
        {
            string file = FileFor(drawingKey);
            if (!File.Exists(file))
                return Task.FromResult<IReadOnlyList<AuditEntry>>(Array.Empty<AuditEntry>());

            var entries = new List<AuditEntry>();
            foreach (var line in File.ReadAllLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var e = JsonSerializer.Deserialize<AuditEntry>(line, Options);
                    if (e != null) entries.Add(e);
                }
                catch
                {
                    // Skip a corrupt line rather than failing the whole read.
                }
            }
            return Task.FromResult<IReadOnlyList<AuditEntry>>(entries);
        }

        private static string FileFor(string drawingKey)
        {
            string safe = MakeFileSafe(string.IsNullOrWhiteSpace(drawingKey) ? "unsaved" : drawingKey);
            return Path.Combine(MojoPaths.AuditDir, safe + ".jsonl");
        }

        private static string MakeFileSafe(string s)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                sb.Append(invalid.Contains(c) ? '_' : c);
            return sb.ToString();
        }
    }
}
