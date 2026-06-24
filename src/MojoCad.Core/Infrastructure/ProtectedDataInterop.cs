using System.Security.Cryptography;

namespace MojoCad.Core.Infrastructure
{
    /// <summary>
    /// Thin isolation wrapper over Windows DPAPI (<see cref="ProtectedData"/>). Keeping the only call
    /// site here means the platform-specific dependency is in one place and easy to stub in tests.
    /// </summary>
    internal static class ProtectedDataInterop
    {
        public static byte[] Protect(byte[] plaintext, byte[] entropy) =>
            ProtectedData.Protect(plaintext, entropy, DataProtectionScope.CurrentUser);

        public static byte[] Unprotect(byte[] ciphertext, byte[] entropy) =>
            ProtectedData.Unprotect(ciphertext, entropy, DataProtectionScope.CurrentUser);
    }
}
