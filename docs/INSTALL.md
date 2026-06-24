# Installing mojoCAD into AutoCAD

mojoCAD is a compiled AutoCAD .NET plugin. Because it ships as source, it must be
built **once**; after that it installs as an AutoCAD **Autoloader bundle** that
loads automatically every time you start AutoCAD — you never run `NETLOAD`.

## The one-step way (Windows)

1. Make sure the **.NET 8 SDK** is installed (`winget install Microsoft.DotNet.SDK.8`,
   or <https://dotnet.microsoft.com/download/dotnet/8.0>).
   *For AutoCAD 2021–2024 you also need the .NET Framework 4.8 Developer Pack +
   MSBuild (e.g. Visual Studio Build Tools).*
2. Double-click **`build\Install-mojoCAD.bat`**.
   *(or, from a terminal: `powershell -ExecutionPolicy Bypass -File build\install.ps1`)*
3. **Restart AutoCAD.**
4. Type **`MOJO`** at the command line. The chat palette opens.
5. Click the **gear → paste your OpenRouter API key → Test connection → Save**.
6. Start chatting. **Nothing is drawn until you accept a proposed change.**

That's it. The script auto-detects your AutoCAD release and builds the matching
runtime (.NET 8 for 2025+, .NET Framework 4.8 for 2021–2024).

### What the installer actually does

- Builds `MojoCad.Plugin` in **Release**.
- Bundles the plugin DLL **plus all its dependencies** (the AutoCAD assemblies are
  intentionally excluded — they come from the running AutoCAD).
- Writes an Autoloader `PackageContents.xml` and copies the whole bundle to:
  ```
  %APPDATA%\Autodesk\ApplicationPlugins\mojoCAD.bundle\
  ```
  AutoCAD scans `ApplicationPlugins` on startup and auto-loads the matching component.

### Options

```powershell
# Build for a specific runtime (otherwise auto-detected):
build\install.ps1 -Frameworks net48
build\install.ps1 -Frameworks net8.0-windows,net48

# Remove it:
build\install.ps1 -Uninstall          # or double-click build\Uninstall-mojoCAD.bat
```

## The manual way (if you prefer to drive the build yourself)

```powershell
dotnet build src\MojoCad.Plugin\MojoCad.Plugin.csproj -c Release -f net8.0-windows
```
Then either:

- **Autoload (recommended):** create `%APPDATA%\Autodesk\ApplicationPlugins\mojoCAD.bundle\`,
  copy `build\PackageContents.xml` into it, and copy the build output into
  `Contents\net8.0-windows\`. Restart AutoCAD.
- **Per-session load:** in AutoCAD run `NETLOAD` and pick
  `src\MojoCad.Plugin\bin\Release\net8.0-windows\MojoCad.Plugin.dll`. (Use the
  **Startup Suite** in `APPLOAD` to make it load every session.)

## Requirements

| | |
|---|---|
| OS | Windows 64-bit |
| AutoCAD | 2025+ (.NET 8) **or** 2021–2024 (.NET Framework 4.8) |
| Build | .NET 8 SDK (+ .NET Framework 4.8 Dev Pack for net48 builds) |
| Account | An [OpenRouter](https://openrouter.ai) API key with credits |

## Troubleshooting

- **`MOJO` is "unknown command":** the bundle didn't load. Confirm
  `%APPDATA%\Autodesk\ApplicationPlugins\mojoCAD.bundle\PackageContents.xml` exists and
  that the `Contents\<framework>\MojoCad.Plugin.dll` matches your AutoCAD release.
  Check AutoCAD's command line / `ProgramFiles\...\acad.log` for autoloader warnings.
- **"dotnet not found":** install the .NET 8 SDK (see step 1) and re-run.
- **A dependency fails to load:** make sure you used the installer (it flattens all
  dependencies into the bundle). A bare `NETLOAD` of the DLL needs its sibling DLLs
  in the same folder — the installer guarantees that.
- **The key isn't saved:** it is stored DPAPI-encrypted under
  `%APPDATA%\mojoCAD\` for the current Windows user; it never enters the drawing or repo.
