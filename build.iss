#define AppName "JizuraLive"
#define AppId "{{C5A3E1D2-9B47-4F86-A2C1-7E0B3D94F6A8}"
#define AppVersion "1.3.0"
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
; Excludes 挡掉开发机上跑 bin\publish 时产生的运行时数据（含设置与登录 cookie）
Source: "bin\publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion; Excludes: "config\*;logs\*;cef-cache\*"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{commondesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
end;
