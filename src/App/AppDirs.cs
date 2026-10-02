using System.IO;

namespace JizuraLive;

// 不放 exe 旁边：Program Files 普通权限写不进，publish/升级覆盖还会清掉，
// 设置和登录态每发一版丢一次
public static class AppDirs
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JizuraLive");

    public static string ConfigDir => Path.Combine(Root, "config");
    public static string LogsDir => Path.Combine(Root, "logs");
}