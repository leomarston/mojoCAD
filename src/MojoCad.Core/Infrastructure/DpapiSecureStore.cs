using System;
using System.IO;
using System.Text;
using MojoCad.Core.Ports;

namespace MojoCad.Core.Infrastructure
{
    /// <summary>
    /// DPAPI-backed implementation of <see cref="ISecureStore"/>. The OpenRouter API key is encrypted
    /// with <c>ProtectedData</c> (CurrentUser scope) and written to %APPDATA%\mojoCAD\apikey.bin. It is
    /// therefore decryptable only by the same Windows user on the same machine, and never appears in
    /// clear text on disk, in logs, in the drawing, or in the repository.
    /// </summary>
    public sealed class DpapiSecureStore : ISecureStore
    {
        // An application-specific entropy value adds a second factor to the DPAPI blob.
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("mojoCAD::openrouter::v1");

        public bool HasApiKey => File.Exists(MojoPaths.ApiKeyFile);

        public void SaveApiKey(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentException("API key must not be empty.", nameof(apiKey));

            MojoPaths.EnsureCreated();
            byte[] plaintext = Encoding.UTF8.GetBytes(apiKey.Trim());
            byte[] encrypted = ProtectedDataInterop.Protect(plaintext, Entropy);
            Array.Clear(plaintext, 0, plaintext.Length);

            // Write atomically: temp file then move, so a crash mid-write never corrupts the key.
            string tmp = MojoPaths.ApiKeyFile + ".tmp";
            File.WriteAllBytes(tmp, encrypted);
            if (File.Exists(MojoPaths.ApiKeyFile))
                File.Delete(MojoPaths.ApiKeyFile);
            File.Move(tmp, MojoPaths.ApiKeyFile);
        }

        public string? LoadApiKey()
        {
            if (!File.Exists(MojoPaths.ApiKeyFile))
                return null;

            try
            {
                byte[] encrypted = File.ReadAllBytes(MojoPaths.ApiKeyFile);
                byte[] plaintext = ProtectedDataInterop.Unprotect(encrypted, Entropy);
                string key = Encoding.UTF8.GetString(plaintext);
                Array.Clear(plaintext, 0, plaintext.Length);
                return key;
            }
            catch
            {
                // A blob that won't decrypt (e.g. copied from another machine/user) is treated as "no key".
                return null;
            }
        }

        public void DeleteApiKey()
        {
            if (File.Exists(MojoPaths.ApiKeyFile))
                File.Delete(MojoPaths.ApiKeyFile);
        }
    }
}
