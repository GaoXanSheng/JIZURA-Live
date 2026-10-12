#define AppName "JizuraLive"
#define AppId "{{C5A3E1D2-9B47-4F86-A2C1-7E0B3D94F6A8}"
#include "bin\build.version.iss"
#define AppExeName "JizuraLive.exe"

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


; 自动关闭正在运行的应用程序 (Inno Setup 的内置机制，尝试温和关闭)
CloseApplications=yes
; 让安装程序检查并尝试关闭我们的主程序，而不是扫描全系统所有的exe/dll/sys
CloseApplicationsFilter={#AppExeName}

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面图标"; GroupDescription: "附加任务:"; Flags: unchecked

[Files]
; 运行时数据（config/logs/云缓存）已统一放 %LOCALAPPDATA%\JizuraLive，
; publish 产物里不会再混入，无需 Excludes
Source: "bin\publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{commondesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[UninstallDelete]
; 用户数据目录（设置与登录态 config、日志 logs）随卸载一并删除
Type: filesandordirs; Name: "{localappdata}\JizuraLive"

[Code]
#include "build.cache-clean.iss"

// 首次安装判定：注册表里没有我们的卸载项，且用户数据目录还不存在
function IsFirstInstall(): Boolean;
begin
  Result := (not RegKeyExists(HKLM,
    'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{#AppId}_is1'))
    and (not DirExists(ExpandConstant('{localappdata}\JizuraLive')));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  // 升级覆盖时先清掉旧版缓存（首次安装时目录还不存在，无需处理）：
  // 日志 / 云音乐下载与歌词缓存 / 解包音频，保留跟随对齐偏移与自动恢复用的音频备份
  if (CurStep = ssInstall) and (not IsFirstInstall()) then
    CleanCachesUnder(ExpandConstant('{localappdata}\JizuraLive'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  // 卸载开始前结束仍在运行的程序，避免 logs 被占用导致用户数据删不干净
  if CurUninstallStep = usUninstall then
    Exec(ExpandConstant('{cmd}'),
      '/C taskkill /F /T /IM {#AppExeName} >nul 2>&1',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;
