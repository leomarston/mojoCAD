# Building mojoCAD

mojoCAD is a single multi-targeted codebase that builds for both AutoCAD runtimes:

| AutoCAD release | Runtime | Target framework |
|---|---|---|
| 2025+ | .NET 8 | `net8.0-windows` (UI/Acad/Plugin), `net8.0` (Core/Agent) |
| 2021–2024 | .NET Framework 4.8 | `net48` |

The managed ObjectARX API surface mojoCAD uses is identical across both, so the same source compiles for either. Pick the target framework that matches the AutoCAD you will load into.

## Prerequisites

- **Windows, x64.** The plugin assemblies must be x64 (AutoCAD is a 64-bit host). `MojoCad.Acad` and `MojoCad.Plugin` set `<Platforms>x64</Platforms>` and `<PlatformTarget>x64</PlatformTarget>`.
- **.NET SDK** capable of building `net8.0-windows` and (for the Framework target) `net48`. On the .NET CLI you also need the .NET Framework reference assemblies / a Windows machine with the targeting pack installed to build `net48`. Visual Studio 2022 with the .NET desktop workload is the simplest setup.
- **WPF.** The UI and Plugin projects set `<UseWPF>true</UseWPF>`, which requires the `-windows` TFM on .NET 8.
- **AutoCAD** of the matching release, installed locally, for NETLOAD and debugging.
- **An OpenRouter account and API key** to actually run the agent (not needed to compile).

NuGet restores the rest: `AutoCAD.NET` (compile-only — see below), `CommunityToolkit.Mvvm` (UI), and on `net48` the `System.Text.Json` / `Microsoft.Bcl.AsyncInterfaces` / `System.Security.Cryptography.ProtectedData` packages that ship in-box on .NET 8.

## AutoCAD.NET package versions per release

The AutoCAD managed assemblies are referenced via the `AutoCAD.NET` NuGet package, with the version tracking the AutoCAD release, selected per target framework:

```xml
<!-- MojoCad.Acad.csproj / MojoCad.Plugin.csproj -->
<ItemGroup Condition="'$(TargetFramework)' == 'net8.0-windows'">
  <PackageReference Include="AutoCAD.NET" Version="25.0.0">
    <ExcludeAssets>runtime</ExcludeAssets>
  </PackageReference>
</ItemGroup>

<ItemGroup Condition="'$(TargetFramework)' == 'net48'">
  <PackageReference Include="AutoCAD.NET" Version="24.3.0">
    <ExcludeAssets>runtime</ExcludeAssets>
  </PackageReference>
</ItemGroup>
```

| Target framework | `AutoCAD.NET` version | AutoCAD release |
|---|---|---|
| `net8.0-windows` | `25.x` | AutoCAD 2025+ |
| `net48` | `24.3.x` | AutoCAD 2024 (also loads in 2021–2023) |

### Retargeting to a different AutoCAD release

- **Change the package version** to match your AutoCAD release in `MojoCad.Acad.csproj` and `MojoCad.Plugin.csproj` (both reference the AutoCAD assemblies). Keep `<ExcludeAssets>runtime</ExcludeAssets>`.
- **Or reference your local SDK directly.** Replace the `PackageReference` with `HintPath`-based `<Reference>`s pointing at `acmgd.dll`, `acdbmgd.dll`, `accoremgd.dll` and `AdWindows.dll` from your AutoCAD install or ObjectARX SDK, and set `<Private>false</Private>` on each (the moral equivalent of `ExcludeAssets=runtime`).
- Build against the **lowest** AutoCAD version you intend to support; later versions load assemblies built against earlier API revisions, not the other way round.

## The `ExcludeAssets=runtime` rule — don't copy AutoCAD DLLs

AutoCAD loads managed plugins into its own process and provides its own copies of `acmgd`, `acdbmgd`, `accoremgd` and `AdWindows`. Shipping our own copies in the plugin output folder causes load conflicts and version mismatches.

mojoCAD prevents that two ways:

- **`<ExcludeAssets>runtime</ExcludeAssets>`** on the `AutoCAD.NET` package reference: we compile against the AutoCAD assemblies but never copy their runtime assets to the output. (With raw `<Reference>`s the equivalent is `<Private>false</Private>`.)
- **`<CopyLocalLockFileAssemblies>false</CopyLocalLockFileAssemblies>`** in `Directory.Build.props`, applied solution-wide, so transitive package assemblies are not dragged into the output folder either.

The net effect: your build output contains the mojoCAD assemblies (and genuinely needed third-party dependencies), but **not** AutoCAD's own DLLs — those come from the running AutoCAD process.

## Building

From the repository root:

```
# Build everything for both target frameworks
dotnet build mojoCAD.sln -c Release

# Build just the .NET 8 (AutoCAD 2025+) flavour of the plugin
dotnet build src/MojoCad.Plugin/MojoCad.Plugin.csproj -c Release -f net8.0-windows

# Build the .NET Framework 4.8 (AutoCAD 2021–2024) flavour
dotnet build src/MojoCad.Plugin/MojoCad.Plugin.csproj -c Release -f net48
```

Do not modify `mojoCAD.sln` — the projects are already registered. Shared build settings (lang version, nullable, the `MojoNet8`/`MojoNet48` TFM aliases, the no-copy rules) live in `Directory.Build.props`.

The artifact you load is `MojoCad.Plugin.dll` (plus its sibling mojoCAD assemblies) from the matching `bin/<Config>/<TFM>/` output directory.

## NETLOAD into AutoCAD

1. Start the AutoCAD release that matches the target framework you built.
2. Run the `NETLOAD` command.
3. Select `MojoCad.Plugin.dll` from the build output (e.g. `src/MojoCad.Plugin/bin/Release/net8.0-windows/MojoCad.Plugin.dll`). The sibling assemblies (`MojoCad.Core`, `MojoCad.Agent`, `MojoCad.Acad`, `MojoCad.Ui`) must be in the same folder so they resolve.
4. On load you'll see `mojoCAD loaded. Type MOJO to open the chat palette.` in the command line.
5. Run `MOJO` (toggle) or `MOJOCHAT` (show) to open the dockable palette.

> Tip: AutoCAD may keep a NETLOADed assembly locked for the session. For iterative development, launch a fresh AutoCAD per build, or use a loader that supports app domains / demand-loading registry entries.

## Debug session (acad.exe as the debug target)

To step through the plugin under the debugger:

1. Set the startup/debug project to `MojoCad.Plugin`.
2. Configure it to **launch an external program**: point the debug target at your AutoCAD executable, e.g.
   `C:\Program Files\Autodesk\AutoCAD 2025\acad.exe`.
   In Visual Studio this is *Project → Properties → Debug → Launch: Executable → Path to `acad.exe`*. (The same effect can be had with a `launchSettings.json` profile whose `commandName` is `Executable` and `executablePath` is `acad.exe`.)
3. Build the framework that matches that AutoCAD (`net8.0-windows` for 2025+, `net48` for 2021–2024), and ensure it's an **x64** build.
4. Start debugging. AutoCAD launches; in AutoCAD run `NETLOAD` and pick the freshly built `MojoCad.Plugin.dll` from your `bin` output (or wire up demand-loading so it auto-loads). Breakpoints in the mojoCAD assemblies will now bind.
5. Run `MOJO` and exercise the palette; breakpoints in the agent loop, tool executor, previewer and applier hit on the appropriate threads (note that DB reads/commits run on AutoCAD's main thread — see the threading model in [`ARCHITECTURE.md`](ARCHITECTURE.md)).

## The net48 vs net8 target switch — checklist

When you change which AutoCAD release you target, make sure these line up:

- **Target framework**: `net8.0-windows` (AutoCAD 2025+) or `net48` (AutoCAD 2021–2024) for the Acad/Ui/Plugin projects; `net8.0`/`net48` for Core/Agent.
- **`AutoCAD.NET` version**: `25.x` for `net8.0-windows`, `24.3.x` for `net48` (or your local SDK references).
- **Platform**: x64 (already enforced on Acad/Plugin).
- **Packaged BCL deps on net48 only**: `System.Text.Json`, `Microsoft.Bcl.AsyncInterfaces`, and `System.Security.Cryptography.ProtectedData` (DPAPI). On .NET 8 these are in-box.
- **Load into the matching AutoCAD**: a `net8.0-windows` build will not load into a .NET Framework AutoCAD (2021–2024), and vice-versa.
