#define AppName "JizuraLive"
#define AppId "{{C5A3E1D2-9B47-4F86-A2C1-7E0B3D94F6A8}"
#define AppVersion "1.0.0"
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

; --- 优化部分开始 ---
; 自动关闭正在运行的应用程序 (Inno Setup 的内置机制，尝试温和关闭)
CloseApplications=yes
; 【重要优化】只让安装程序检查并尝试关闭我们的主程序，而不是扫描全系统所有的exe/dll/sys
CloseApplicationsFilter={#AppExeName}
; --- 优化部分结束 ---

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面图标"; GroupDescription: "附加任务:"; Flags: unchecked

[Files]
Source: "bin\publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{commondesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Code]
// 安装程序初始化时触发
function InitializeSetup(): Boolean;
begin
  Result := True;
end;

// 安装步骤改变时触发：在文件复制前删除旧的日志目录，避免旧日志残留
procedure CurStepChanged(CurStep: TSetupStep);
var
  LogsDir: String;
begin
  if CurStep = ssInstall then
  begin
    LogsDir := ExpandConstant('{app}\logs');
    if DirExists(LogsDir) then
    begin
      Log('正在删除日志目录: ' + LogsDir);
      if DelTree(LogsDir, True, True, True) then
        Log('日志目录删除成功。')
      else
        Log('日志目录删除失败。');
    end
    else
      Log('日志目录不存在，跳过删除: ' + LogsDir);
  end;
end;

// 卸载步骤改变时触发：在卸载开始前清理运行时生成的日志目录
procedure DoCleanupResources();
var
  LogsDir: String;
begin
  LogsDir := ExpandConstant('{app}\logs');
  if DirExists(LogsDir) then
  begin
    Log('正在删除日志目录: ' + LogsDir);
    if DelTree(LogsDir, True, True, True) then
      Log('日志目录删除成功。')
    else
      Log('日志目录删除失败。');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // 在卸载实际开始前执行清理
  if CurUninstallStep = usUninstall then
  begin
    DoCleanupResources();
  end;
end;
