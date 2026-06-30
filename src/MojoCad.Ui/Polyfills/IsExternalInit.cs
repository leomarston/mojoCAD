// On .NET Framework 4.8 the compiler-required type System.Runtime.CompilerServices.IsExternalInit
// (used by `init`-only setters and `record` types, e.g. SettingsViewModel.ModelOption) does not
// exist. Provide it for net48 only - on net8 the runtime already defines it, so guard with NETFRAMEWORK
// to avoid a duplicate definition.
#if NETFRAMEWORK
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif
