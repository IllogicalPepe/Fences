; FenceDesk — Inno Setup 6 installer
; Build with:  powershell -File ..\Build-Installer.ps1
; Or:          ISCC.exe FenceDesk.iss   (after publishing to ..\dist\publish)

#define MyAppName      "FenceDesk"
#define MyAppVersion   "2.1.3"
#define MyAppPublisher "FenceDesk"
#define MyAppExeName   "FenceDesk.exe"
#define MyAppId        "{{8F3C2A91-6B4E-4D7A-9C1F-E5A8B0D4F627}"

#ifndef SourceDir
  #define SourceDir "..\dist\publish"
#endif

#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; Per-user install — no admin elevation required
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=FenceDesk-Setup-{#MyAppVersion}
SetupIconFile=..\Assets\FenceDesk.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
WizardSizePercent=100
; Close running FenceDesk so files can be replaced
CloseApplications=yes
CloseApplicationsFilter=FenceDesk.exe
RestartApplications=no
; Keep layout/settings under %LOCALAPPDATA%\FenceDesk on uninstall
UsePreviousAppDir=yes
DirExistsWarning=no
AllowNoIcons=yes
MinVersion=10.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: checkedonce
Name: "autostart"; Description: "Start FenceDesk when Windows starts"; GroupDescription: "Startup:"

[Files]
; Self-contained publish output (no .NET runtime install needed)
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Comment: "Desktop fence organizer"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Comment: "Desktop fence organizer"; Tasks: desktopicon

[Registry]
; Optional Run-at-logon (removed on uninstall when task was selected)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "FenceDesk"; ValueData: """{app}\{#MyAppExeName}"""; \
    Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; \
    Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Remove leftover files we may have written into the app folder
Type: filesandordirs; Name: "{app}"

[Code]
function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  { Best-effort: stop a running instance before file copy }
  Exec('taskkill.exe', '/IM FenceDesk.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := True;
end;

function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/IM FenceDesk.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := True;
end;
