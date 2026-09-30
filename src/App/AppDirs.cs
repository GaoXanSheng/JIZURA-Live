using System.IO;

namespace JizuraLive;

// --------------------------------------------------------------------
// 运行时可写数据（config/logs/CEF 缓存）统一放 %LOCALAPPDATA%\JizuraLive。
// 不放安装目录的原因：Program Files 普通权限不可写；dotnet publish 与
// 升级覆盖都会清掉 exe 旁的文件（设置/登录态每发布一次就丢一次）。
// --------------------------------------------------------------------
public static class AppDirs
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JizuraLive");

    public static string ConfigDir => Path.Combine(Root, "config");
    public static string LogsDir => Path.Combine(Root, "logs");
}