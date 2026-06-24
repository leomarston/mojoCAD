; Inno Setup script -> builds a one-click "mojoCAD-Setup.exe".
; It installs the prebuilt bundle into the per-user AutoCAD Autoloader folder, so AutoCAD
; loads mojoCAD automatically on startup. No admin rights required (per-user install).
;
; The bundle is produced by build\package.ps1 (default: dist\mojoCAD.bundle). Override with:
;   iscc build\installer\mojoCAD.iss /DSourceBundle="C:\path\to\dist\mojoCAD.bundle" /DOutDir="C:\path\to\dist"
;
; This installer is an ADDITION to the from-source path (build\install.ps1), which is preserved.

#ifndef SourceBundle
  #define SourceBundle "..\..\dist\mojoCAD.bundle"
#endif
#ifndef OutDir
  #define OutDir "..\..\dist"
#endif
#define MyAppVersion "0.1.0"

[Setup]
AppId={{8F2A6C10-7D4B-4E2A-9C31-0A1B2C3D4E5F}
AppName=mojoCAD
AppVersion={#MyAppVersion}
AppPublisher=mojoCAD
AppPublisherURL=https://mojocad.app
DefaultDirName={userappdata}\Autodesk\ApplicationPlugins\mojoCAD.bundle
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#OutDir}
OutputBaseFilename=mojoCAD-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=mojoCAD for AutoCAD
; AutoCAD reads the bundle from the install folder; nothing else needs to be registered.

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; Copy the entire prebuilt bundle (PackageContents.xml + Contents\<framework>\...) into place.
Source: "{#SourceBundle}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Messages]
WelcomeLabel2=This will install mojoCAD into AutoCAD for the current user.%n%nAfter installation, restart AutoCAD and type MOJO to open the chat palette. Paste your OpenRouter API key in Settings to begin.

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
  // Friendly heads-up if no AutoCAD release is detected (non-blocking).
  if not RegKeyExists(HKLM, 'SOFTWARE\Autodesk\AutoCAD') then
    MsgBox('No AutoCAD installation was detected on this machine. mojoCAD will be installed anyway and will load the next time a supported AutoCAD (2021-2025) starts.', mbInformation, MB_OK);
end;
