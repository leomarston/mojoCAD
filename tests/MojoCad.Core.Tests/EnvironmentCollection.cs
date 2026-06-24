using Xunit;

namespace MojoCad.Core.Tests
{
    /// <summary>
    /// SettingsStoreTests and DpapiSecureStoreTests both redirect the process-wide %APPDATA% (and the
    /// XDG/HOME equivalents) to a temp dir. xUnit parallelises across test classes by default, so without
    /// pinning them into one collection two of these classes could clobber each other's env vars mid-run.
    /// Sharing a non-parallel collection serialises them while leaving the pure tests free to run parallel.
    /// </summary>
    [CollectionDefinition("AppData environment", DisableParallelization = true)]
    public sealed class AppDataEnvironmentCollection
    {
    }
}
