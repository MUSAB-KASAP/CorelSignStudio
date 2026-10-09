; Corel AI Operatörü — Inno Setup 6 script.
;
; Build it through build\publish.ps1, which passes the version and the folders:
;   ISCC.exe /DAppVersion=1.0.0 /DSourceDir=...\artifacts\release\CorelAI-Operator /DOutputDir=...\artifacts\release\installer installer.iss
;
; The installer contains the publish folder only. CorelDRAW 2026 is a prerequisite and is not bundled.
; Settings, the API key, logs and outputs live in the user's profile; they are never packaged, and they
; are left in place on uninstall.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\release\CorelAI-Operator"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\release\installer"
#endif

#define AppName "Corel AI Operatörü"
#define AppExe "CorelSignStudio.App.exe"

[Setup]
AppId={{6C0B3F2E-7C1A-4E6B-9A55-2F5D1C0E9A41}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Corel Sign Studio
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
; Per-user by default: with PrivilegesRequired=lowest, {autopf} is the user's own programs folder inside
; the user profile, so no administrator rights are needed. Choosing "all users" in the dialog elevates
; and uses Program Files instead.
DefaultDirName={autopf}\CorelAI-Operator
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=CorelAI-Operator-{#AppVersion}-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
