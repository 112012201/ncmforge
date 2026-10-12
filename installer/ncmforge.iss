; Inno Setup script for NcmForge — builds the Windows .exe installer.
;
; Product decision: an .exe installer rather than an .msi. Inno Setup was chosen over NSIS because
; the script is declarative, the uninstaller and Add/Remove Programs entry come for free, and a
; per-user install needs no elevation.
;
; The zip archives remain published alongside this installer; this is an additional way to install,
; not a replacement.
;
; Build (StageDir MUST be absolute — see the guard below):
;   ISCC.exe installer\ncmforge.iss /DAppVersion=1.2.4153-rc2 ^
;            /DStageDir=C:\work\dist\win-x64 /DAppArch=x64 /Odist
;
; Everything comes in through /D defines so one file serves all three architectures from a single
; CI matrix:
;   AppVersion  the release version, e.g. 1.2.4153-rc2
;   StageDir    absolute path to the folder holding NcmForge.exe and cli\ncmforge.exe
;   AppArch     x64 | x86 | arm64

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef StageDir
  #define StageDir "..\dist\win-x64"
#endif
#ifndef AppArch
  #define AppArch "x64"
#endif

; Fail loudly rather than silently emitting an installer with no program in it. A relative StageDir
; resolves against this file's folder, so getting it wrong is caught here instead of at install time.
#if !FileExists(AddBackslash(StageDir) + "NcmForge.exe")
  #error StageDir must be an absolute path to a published build containing NcmForge.exe
#endif

; One AppId per architecture. A shared id would make the x64 installer remove an existing x86
; install (and vice versa) instead of installing beside it, which is wrong for a program that ships
; three architectures.
#ifndef AppId
  #define AppId "NcmForge_" + AppArch
#endif

; Inno reports the architecture as x64 / x86 / arm64; these map it onto its own directive vocabulary.
#if AppArch == "x64"
  #define ArchAllowed "x64compatible"
  #define ArchMode "x64compatible"
#elif AppArch == "arm64"
  #define ArchAllowed "arm64"
  #define ArchMode "arm64"
#else
  #define ArchAllowed "x86compatible"
  #define ArchMode "x86compatible"
#endif

[Setup]
AppId={#AppId}
AppName=NcmForge
AppVersion={#AppVersion}
AppVerName=NcmForge {#AppVersion}
AppPublisher=NcmForge
AppPublisherURL=https://github.com/112012201/ncmforge
AppSupportURL=https://github.com/112012201/ncmforge/issues
AppUpdatesURL=https://github.com/112012201/ncmforge/releases
DefaultDirName={autopf}\NcmForge
DefaultGroupName=NcmForge
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
InfoBeforeFile=..\DISCLAIMER.md

; Per-user by default, so no UAC prompt; the user can still pick a machine-wide install in the
; dialog. {autopf} resolves to Program Files or to the per-user programs folder accordingly.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

ArchitecturesAllowed={#ArchAllowed}
ArchitecturesInstallIn64BitMode={#ArchMode}

OutputDir=..\dist
OutputBaseFilename=NcmForge-{#AppVersion}-win-{#AppArch}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=NcmForge {#AppVersion} ({#AppArch})
UninstallDisplayIcon={app}\NcmForge.exe
SetupLogging=yes

[Languages]
; Simplified Chinese is an official Inno translation but is NOT bundled with every Inno release, so
; it is vendored beside this script. See THIRD-PARTY-NOTICES.md.
Name: "chinese"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "addtopath"; Description: "将命令行版 ncmforge.exe 加入 PATH"; GroupDescription: "环境变量："; Flags: unchecked

[Files]
; The GUI and its self-contained runtime.
Source: "{#StageDir}\NcmForge.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageDir}\*.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageDir}\*.json"; DestDir: "{app}"; Flags: ignoreversion
; The command-line frontend lives in its own folder so it cannot overwrite the GUI — publishing both
; into one directory is how an earlier release shipped a console program as the desktop app.
Source: "{#StageDir}\cli\*"; DestDir: "{app}\cli"; Flags: ignoreversion recursesubdirs
; Documentation and licence, so the install is self-describing.
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\DISCLAIMER.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\NcmForge"; Filename: "{app}\NcmForge.exe"; WorkingDir: "{app}"
Name: "{group}\NcmForge 命令行"; Filename: "{app}\cli\ncmforge.exe"; WorkingDir: "{app}"
Name: "{group}\{cm:UninstallProgram,NcmForge}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\NcmForge"; Filename: "{app}\NcmForge.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\NcmForge.exe"; Description: "{cm:LaunchProgram,NcmForge}"; Flags: nowait postinstall skipifsilent

[Registry]
; Only written when the user asks for it; the installer never edits PATH unasked. Inno has no line
; continuation, so this stays on one line.
Root: HKCU; Subkey: "Environment"; ValueType: string; ValueName: "Path"; ValueData: "{olddata};{app}\cli"; Tasks: addtopath; Flags: uninsdeletevalue

[UninstallDelete]
; Settings live in %APPDATA% and are deliberately NOT removed: they belong to the user, not to the
; installer, and a reinstall should find them.
Type: filesandordirs; Name: "{app}\.ncmforge-tmp"
