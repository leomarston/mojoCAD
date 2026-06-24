using System;
using System.IO;
using System.Runtime.InteropServices;
using MojoCad.Core.Infrastructure;
using Xunit;

namespace MojoCad.Core.Tests
{
    /// <summary>
    /// DPAPI is a Windows-only facility (ProtectedData / CurrentUser scope), so these tests only run on
    /// Windows; on Linux/macOS they short-circuit (the encryption back-end simply isn't present off
    /// Windows, which is fine because we only ship the plugin to Windows AutoCAD). On Windows we verify a
    /// full save -> load -> delete round-trip into a redirected %APPDATA% so the developer's real key
    /// store is never touched.
    /// </summary>
    [Collection("AppData environment")]
    public sealed class DpapiSecureStoreTests : IDisposable
    {
        private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        private readonly string _tempRoot;
        private readonly string? _origAppData;

        public DpapiSecureStoreTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "mojocad-dpapi-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
            _origAppData = Environment.GetEnvironmentVariable("APPDATA");
            Environment.SetEnvironmentVariable("APPDATA", _tempRoot);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("APPDATA", _origAppData);
            try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort */ }
        }

        [Fact]
        public void SaveLoadDelete_RoundTrips_OnWindows()
        {
            if (!IsWindows) return; // DPAPI unavailable off Windows; nothing to assert.

            var store = new DpapiSecureStore();
            Assert.False(store.HasApiKey);
            Assert.Null(store.LoadApiKey());

            const string key = "sk-or-v1-deadbeefcafef00d";
            store.SaveApiKey(key);

            Assert.True(store.HasApiKey);
            Assert.Equal(key, store.LoadApiKey());
            // The blob on disk must be ciphertext, never the plaintext key.
            byte[] onDisk = File.ReadAllBytes(MojoPaths.ApiKeyFile);
            Assert.DoesNotContain("sk-or-v1", System.Text.Encoding.UTF8.GetString(onDisk));

            store.DeleteApiKey();
            Assert.False(store.HasApiKey);
            Assert.Null(store.LoadApiKey());
        }

        [Fact]
        public void SaveApiKey_TrimsWhitespace_OnWindows()
        {
            if (!IsWindows) return;

            var store = new DpapiSecureStore();
            store.SaveApiKey("  sk-or-v1-trimme  ");

            Assert.Equal("sk-or-v1-trimme", store.LoadApiKey());
        }

        [Fact]
        public void SaveApiKey_RejectsEmpty_OnWindows()
        {
            if (!IsWindows) return;

            var store = new DpapiSecureStore();
            Assert.Throws<ArgumentException>(() => store.SaveApiKey("   "));
        }
    }
}
