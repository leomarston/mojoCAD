using System;
using System.IO;

namespace MojoCad.Core.Infrastructure
{
    /// <summary>
    /// Canonical on-disk locations for mojoCAD data, all under %APPDATA%\mojoCAD. Centralised so the
    /// config, logs, audit trail and conversation store never drift apart.
    /// </summary>
    public static class MojoPaths
    {
        public static string Root
        {
            get
            {
                // Honour the %APPDATA% environment variable first - that is exactly what this path means,
                // it lets an admin/user redirect it, and it makes the store unit-testable. Fall back to the
                // known Roaming folder when the variable is unset (which is the normal Windows default anyway).
                var appData = Environment.GetEnvironmentVariable("APPDATA");
                if (string.IsNullOrWhiteSpace(appData))
                    appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(appData, "mojoCAD");
            }
        }

        public static string ConfigFile => Path.Combine(Root, "config.json");

        /// <summary>DPAPI-encrypted API key blob. Never plain text, never in the repo or the DWG.</summary>
        public static string ApiKeyFile => Path.Combine(Root, "apikey.bin");

        public static string LogsDir => Path.Combine(Root, "logs");

        public static string AuditDir => Path.Combine(Root, "audit");

        public static string ConversationsDir => Path.Combine(Root, "conversations");

        /// <summary>Create the full directory tree if missing. Safe to call repeatedly.</summary>
        public static void EnsureCreated()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(LogsDir);
            Directory.CreateDirectory(AuditDir);
            Directory.CreateDirectory(ConversationsDir);
        }
    }
}
