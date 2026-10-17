#define AppName "JizuraLivePV"
#define AppId "{{E7B5A3C1-4D6F-4E82-9B7A-1C2D3E4F5A6B}"
#include "bin\build.version.iss"
#define AppExeName "JizuraLivePV.exe"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
DefaultGroupName={#AppName}
DefaultDirName={autopf}\{#AppName}
DisableDirPage=no
LanguageDetectionMethod=none
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
OutputDir=Output
OutputBaseFilename={#AppName}_{#AppVersion}_Setup
Compression=lzma
SolidCompression=yes
UninstallDisplayIcon={app}\{#AppExeName}
SetupIconFile=assets\app.ico


CloseApplications=yes
CloseApplicationsFilter={#AppExeName}

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面图标"; GroupDescription: "附加任务:"; Flags: unchecked

[Files]
Source: "bin\publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{commondesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\JizuraLivePV"

[Code]
#include "build.cache-clean.iss"

function IsFirstInstall(): Boolean;
begin
  Result := (not RegKeyExists(HKLM,
    'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{#AppId}_is1'))
    and (not DirExists(ExpandConstant('{localappdata}\JizuraLivePV')));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssInstall) and (not IsFirstInstall()) then
    CleanCachesUnder(ExpandConstant('{localappdata}\JizuraLivePV'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
    Exec(ExpandConstant('{cmd}'),
      '/C taskkill /F /T /IM {#AppExeName} >nul 2>&1',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;
