using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MojoCad.Core.Ports;

namespace MojoCad.Core.Infrastructure
{
    /// <summary>File-backed <see cref="IConversationStore"/> - one JSON transcript per conversation id.</summary>
    public sealed class JsonFileConversationStore : IConversationStore
    {
        public Task SaveAsync(string conversationId, string serializedTranscript)
        {
            MojoPaths.EnsureCreated();
            File.WriteAllText(PathFor(conversationId), serializedTranscript);
            return Task.CompletedTask;
        }

        public Task<string?> LoadAsync(string conversationId)
        {
            string p = PathFor(conversationId);
            return Task.FromResult(File.Exists(p) ? File.ReadAllText(p) : null);
        }

        public Task<IReadOnlyList<string>> ListAsync()
        {
            if (!Directory.Exists(MojoPaths.ConversationsDir))
                return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

            var ids = Directory.GetFiles(MojoPaths.ConversationsDir, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(s => s != null)
                .Select(s => s!)
                .ToList();
            return Task.FromResult<IReadOnlyList<string>>(ids);
        }

        public Task DeleteAsync(string conversationId)
        {
            string p = PathFor(conversationId);
            if (File.Exists(p)) File.Delete(p);
            return Task.CompletedTask;
        }

        private static string PathFor(string conversationId)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                conversationId = conversationId.Replace(c, '_');
            return Path.Combine(MojoPaths.ConversationsDir, conversationId + ".json");
        }
    }
}
