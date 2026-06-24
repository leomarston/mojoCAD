<#
.SYNOPSIS
  Build mojoCAD and assemble a DISTRIBUTABLE artifact (a drop-in bundle folder + a zip)
  under .\dist. This does NOT install anything - it just packages.

.DESCRIPTION
  Used by CI (and runnable locally) to produce the files end users download:
    dist\mojoCAD.bundle\        - drop this folder into %APPDATA%\Autodesk\ApplicationPlugins\
    dist\mojoCAD-bundle.zip     - the same, zipped for download
  A one-click .exe installer is built separately from this bundle (see build\installer\mojoCAD.iss).

  This is independent of build\install.ps1, which builds AND installs locally for developers.
  Both paths are supported and kept in sync intentionally.

.PARAMETER Frameworks
  Target framework(s) to build. Default: both net8.0-windows (AutoCAD 2025+) and net48 (2021-2024).
  A framework that fails to build is skipped (with a warning) rather than failing the whole package.

.PARAMETER OutDir
  Output directory. Default: .\dist
#>
[CmdletBinding()]
param(
    [string[]] $Frameworks = @('net8.0-windows', 'net48'),
    [string]   $OutDir
)

$ErrorActionPreference = 'Stop'
$RepoRoot   = Split-Path -Parent $PSScriptRoot
$PluginProj = Join-Path $RepoRoot 'src\MojoCad.Plugin\MojoCad.Plugin.csproj'
if (-not $OutDir) { $OutDir = Join-Path $RepoRoot 'dist' }
$BundleDir  = Join-Path $OutDir 'mojoCAD.bundle'

function Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "    $m" -ForegroundColor Green }
function Warn2($m){ Write-Host "    $m" -ForegroundColor Yellow }

Step "Cleaning $OutDir"
if (Test-Path $OutDir) { Remove-Item -Recurse -Force $OutDir }
New-Item -ItemType Directory -Force -Path (Join-Path $BundleDir 'Contents') | Out-Null

# ---- Build each requested framework ------------------------------------------
$built = @()
foreach ($tfm in $Frameworks) {
    Step "Building MojoCad.Plugin ($tfm, Release)"
    & dotnet build $PluginProj -c Release -f $tfm --nologo
    $outBin = Join-Path $RepoRoot "src\MojoCad.Plugin\bin\Release\$tfm"
    if ($LASTEXITCODE -eq 0 -and (Test-Path (Join-Path $outBin 'MojoCad.Plugin.dll'))) {
        $dest = Join-Path $BundleDir "Contents\$tfm"
        New-Item -ItemType Directory -Force -Path $dest | Out-Null
        Copy-Item (Join-Path $outBin '*') -Destination $dest -Recurse -Force
        $built += $tfm
        Ok "Packaged Contents\$tfm"
    } else {
        Warn2 "Skipped $tfm (build failed or no output)."
    }
}

if ($built.Count -eq 0) { throw "Nothing built - cannot package. See the build output above." }

# ---- Help file referenced by PackageContents.xml -----------------------------
@"
mojoCAD - Cursor for AutoCAD
Type MOJO (or MOJOCHAT) at the command line to open the chat palette.
Paste your OpenRouter API key in Settings to begin.
"@ | Set-Content -Path (Join-Path $BundleDir 'Contents\README.txt') -Encoding UTF8

# ---- PackageContents.xml pruned to the frameworks we built -------------------
Step "Writing PackageContents.xml ($([string]::Join(', ', $built)))"
$blocks = @()
if ($built -contains 'net8.0-windows') {
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
if ($built -contains 'net48') {
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

# ---- Zip it for download -----------------------------------------------------
$zip = Join-Path $OutDir 'mojoCAD-bundle.zip'
Step "Zipping -> $zip"
Compress-Archive -Path $BundleDir -DestinationPath $zip -Force
Ok "Wrote $zip"

Write-Host ""
Step "Done. Distributable artifacts:"
Write-Host "    $BundleDir"  -ForegroundColor White
Write-Host "    $zip"        -ForegroundColor White
