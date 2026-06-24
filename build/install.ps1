<#
.SYNOPSIS
  Build mojoCAD and install it as an AutoCAD Autoloader bundle so AutoCAD loads
  it automatically on startup (no NETLOAD needed, ever).

.DESCRIPTION
  This is the closest thing to a "one-click" install for a source-distributed
  AutoCAD .NET plugin. It:
    1. Builds MojoCad.Plugin (Release) for the target framework(s).
    2. Assembles a self-contained bundle (plugin DLL + all dependencies, minus
       AutoCAD's own assemblies) with an Autoloader PackageContents.xml.
    3. Copies it to %APPDATA%\Autodesk\ApplicationPlugins\mojoCAD.bundle\.
  Restart AutoCAD and type MOJO. That's it.

.PARAMETER Frameworks
  Which target framework(s) to build. Default: auto-detected from the AutoCAD
  release(s) installed on this machine (R25+ -> net8.0-windows, R24.x -> net48).
  Override with e.g. -Frameworks net8.0-windows,net48

.PARAMETER Uninstall
  Remove the installed bundle and exit.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build\install.ps1

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build\install.ps1 -Frameworks net48

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build\install.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string[]] $Frameworks,
    [switch]   $Uninstall
)

$ErrorActionPreference = 'Stop'
$RepoRoot   = Split-Path -Parent $PSScriptRoot
$PluginProj = Join-Path $RepoRoot 'src\MojoCad.Plugin\MojoCad.Plugin.csproj'
$BundleName = 'mojoCAD.bundle'
$BundleDir  = Join-Path $env:APPDATA "Autodesk\ApplicationPlugins\$BundleName"

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }
function Write-Warn2($msg) { Write-Host "    $msg" -ForegroundColor Yellow }

# ---- Uninstall ----------------------------------------------------------------
if ($Uninstall) {
    Write-Step "Uninstalling mojoCAD"
    if (Test-Path $BundleDir) {
        Remove-Item -Recurse -Force $BundleDir
        Write-Ok "Removed $BundleDir"
    } else {
        Write-Warn2 "Nothing to remove ($BundleDir does not exist)."
    }
    Write-Host "`nDone. Restart AutoCAD to unload mojoCAD." -ForegroundColor Cyan
    return
}

# ---- Prerequisite: dotnet -----------------------------------------------------
Write-Step "Checking for the .NET SDK"
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-Host @"
The .NET SDK was not found on PATH.

mojoCAD is distributed as source, so it must be compiled once. Install the SDK,
then re-run this script:

  - Easiest:  winget install Microsoft.DotNet.SDK.8
  - Or download: https://dotnet.microsoft.com/download/dotnet/8.0

(For AutoCAD 2021-2024 / net48 builds you also need the .NET Framework 4.8
 Developer Pack and MSBuild, e.g. Visual Studio Build Tools.)
"@ -ForegroundColor Yellow
    throw "dotnet SDK not found."
}
Write-Ok ("dotnet " + (& dotnet --version))

# ---- Decide which frameworks to build ----------------------------------------
function Get-InstalledAutocadFrameworks {
    $tfms = @()
    try {
        $keys = Get-ChildItem 'HKLM:\SOFTWARE\Autodesk\AutoCAD' -ErrorAction SilentlyContinue
        foreach ($k in $keys) {
            $name = Split-Path $k.Name -Leaf   # e.g. R25.0, R24.3
            if ($name -match '^R(\d+)\.(\d+)$') {
                $major = [int]$Matches[1]
                if ($major -ge 25) { $tfms += 'net8.0-windows' }
                elseif ($major -eq 24) { $tfms += 'net48' }
            }
        }
    } catch { }
    return ($tfms | Select-Object -Unique)
}

if (-not $Frameworks -or $Frameworks.Count -eq 0) {
    $detected = Get-InstalledAutocadFrameworks
    if ($detected.Count -gt 0) {
        $Frameworks = $detected
        Write-Step ("Detected AutoCAD -> building: " + ($Frameworks -join ', '))
    } else {
        $Frameworks = @('net8.0-windows')
        Write-Step "No installed AutoCAD detected; defaulting to net8.0-windows (AutoCAD 2025+)."
        Write-Warn2 "If you run AutoCAD 2021-2024, re-run with: -Frameworks net48"
    }
}

# ---- Build --------------------------------------------------------------------
$built = @()
foreach ($tfm in $Frameworks) {
    Write-Step "Building MojoCad.Plugin ($tfm, Release)"
    & dotnet build $PluginProj -c Release -f $tfm --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Warn2 "Build failed for $tfm - skipping it."
        continue
    }
    $outDir = Join-Path $RepoRoot "src\MojoCad.Plugin\bin\Release\$tfm"
    if (Test-Path (Join-Path $outDir 'MojoCad.Plugin.dll')) {
        $built += [pscustomobject]@{ Tfm = $tfm; OutDir = $outDir }
        Write-Ok "Built $tfm"
    } else {
        Write-Warn2 "No MojoCad.Plugin.dll produced for $tfm - skipping."
    }
}

if ($built.Count -eq 0) {
    throw "Nothing built successfully. See the build output above."
}

# ---- Assemble the bundle ------------------------------------------------------
Write-Step "Assembling the bundle"
if (Test-Path $BundleDir) { Remove-Item -Recurse -Force $BundleDir }
New-Item -ItemType Directory -Force -Path (Join-Path $BundleDir 'Contents') | Out-Null

foreach ($b in $built) {
    $dest = Join-Path $BundleDir "Contents\$($b.Tfm)"
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    # Copy the flattened runtime closure (DLLs + config). AutoCAD's own assemblies
    # were excluded at build time (ExcludeAssets=runtime), so they won't be here.
    Copy-Item (Join-Path $b.OutDir '*') -Destination $dest -Recurse -Force
    Write-Ok "Staged Contents\$($b.Tfm)"
}

# A tiny help file referenced by PackageContents.xml.
@"
mojoCAD - Cursor for AutoCAD
Type MOJO (or MOJOCHAT) at the command line to open the chat palette.
Paste your OpenRouter API key in Settings to begin.
"@ | Set-Content -Path (Join-Path $BundleDir 'Contents\README.txt') -Encoding UTF8

# ---- Write a PackageContents.xml pruned to the frameworks we actually built ---
Write-Step "Writing PackageContents.xml"
$builtTfms = $built.Tfm
$blocks = @()
if ($builtTfms -contains 'net8.0-windows') {
    $blocks += @'
  <Components Description="mojoCAD for AutoCAD 2025 and later (.NET 8)">
    <RuntimeRequirements OS="Win64" Platform="AutoCAD*" SeriesMin="R25.0" />
    <ComponentEntry AppName="mojoCAD" Version="0.1.0" ModuleName="./Contents/net8.0-windows/MojoCad.Plugin.dll" AppDescription="mojoCAD AI design copilot" LoadOnAutoCADStartup="True">
      <Commands GroupName="MOJO">
        <Command Global="MOJO" Local="MOJO" />
        <Command Global="MOJOCHAT" Local="MOJOCHAT" />
      </Commands>
    </ComponentEntry>
  </Components>
'@
}
if ($builtTfms -contains 'net48') {
    $blocks += @'
  <Components Description="mojoCAD for AutoCAD 2021-2024 (.NET Framework 4.8)">
    <RuntimeRequirements OS="Win64" Platform="AutoCAD*" SeriesMin="R24.0" SeriesMax="R24.3" />
    <ComponentEntry AppName="mojoCAD" Version="0.1.0" ModuleName="./Contents/net48/MojoCad.Plugin.dll" AppDescription="mojoCAD AI design copilot" LoadOnAutoCADStartup="True">
      <Commands GroupName="MOJO">
        <Command Global="MOJO" Local="MOJO" />
        <Command Global="MOJOCHAT" Local="MOJOCHAT" />
      </Commands>
    </ComponentEntry>
  </Components>
'@
}

$packageContents = @"
<?xml version="1.0" encoding="utf-8"?>
<ApplicationPackage SchemaVersion="1.0" AppVersion="0.1.0" ProductCode="{8F2A6C10-7D4B-4E2A-9C31-0A1B2C3D4E5F}" Author="mojoCAD" Name="mojoCAD" Description="mojoCAD - Cursor for AutoCAD." AutodeskProduct="AutoCAD" ProductType="Application" HelpFile="./Contents/README.txt">
  <CompanyDetails Name="mojoCAD" Url="https://mojocad.app" />
$([string]::Join("`r`n", $blocks))
</ApplicationPackage>
"@
$packageContents | Set-Content -Path (Join-Path $BundleDir 'PackageContents.xml') -Encoding UTF8
Write-Ok "Wrote PackageContents.xml ($([string]::Join(', ', $builtTfms)))"

# ---- Done ---------------------------------------------------------------------
Write-Host ""
Write-Step "Installed to:"
Write-Host "    $BundleDir" -ForegroundColor White

if (Get-Process -Name acad -ErrorAction SilentlyContinue) {
    Write-Warn2 "AutoCAD is running - RESTART it to load mojoCAD."
}

Write-Host @"

Next:
  1. (Re)start AutoCAD.
  2. Type  MOJO  at the command line to open the chat palette.
  3. Click the gear, paste your OpenRouter API key, Test connection, Save.
  4. Start chatting. Nothing is drawn until you accept a change set.

To remove later:  build\install.ps1 -Uninstall
"@ -ForegroundColor Cyan
