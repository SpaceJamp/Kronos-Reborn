; Inno Setup script for Kronos Reborn.
;
; The version arrives as /DAppVersion from package\package.cmd, which reads it from config.cmd. That
; indirection is the point: a literal here is a second place for the installer's name to drift from
; the project's.
#ifndef AppVersion
  ; Fallback for compiling this script by hand. Flagged because it is exactly the kind of literal
  ; that goes stale; a test checks it against Directory.Build.props and config.cmd.
  #define AppVersion "0.1.0"
#endif

#define AppName "Kronos Reborn"
#define AppId "{{6C3D9E24-1B7A-4E58-9F0C-2D5A83B71E40}}"
#define AppPublisher "Kronos Reborn"
#define AppExeName "KronosReborn.exe"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}.0
VersionInfoProductVersion={#AppVersion}

; Per-user or per-machine, chosen by the person running the installer.
;
; PrivilegesRequired=lowest means the installer never demands elevation to begin with.
; PrivilegesRequiredOverridesAllowed=dialog then offers the choice, and Inno handles everything that
; follows from it: elevation only when the machine-wide option is picked, %LOCALAPPDATA%\Programs
; against Program Files, HKCU against HKLM, and the matching uninstall entry.
;
; An earlier version of this script built its own radio page in [Code] and called SetInstallScope.
; That is Inno reimplementing a dialog Inno already has, in Pascal Script, and every step of it was
; fought through: a Values array that would not type check, an indexer that takes a string rather
; than a position. The built-in dialog is less code and behaves correctly.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; A build directory is never a real install. Without this, running the setup out of Output\ would
; offer to install over itself.
DirExistsWarning=no
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExeName}
OutputDir=Output
OutputBaseFilename=KronosReborn-{#AppVersion}-installer
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

; No SetupIconFile yet. There is no artwork, and a placeholder icon tends to outlive its usefulness.

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; One recursive entry rather than a generated file list, so a clean clone builds without an
; intermediate artefact having to be committed.
;
; The path resolves to src\KronosReborn\bin\publish\portable because PublishDir is interpreted
; relative to the project, not to this directory. build.cmd uses the same location for the portable
; zip, and config.cmd names it once.
Source: "..\src\KronosReborn\bin\publish\portable\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

; No [Code] section yet.
;
; Two things will want one, and neither is here yet because there is no behaviour to attach them to:
;
;   - Removing the data directory on uninstall. It is per-user data even for a machine-wide install,
;     so deleting it during a machine-wide uninstall would throw away one user's games and settings
;     while everyone else kept theirs. It should only ever be removed for a per-user install, which
;     means asking the user first.
;   - Refusing to install while the application is running, by detecting a running process and
;     offering to close it.
