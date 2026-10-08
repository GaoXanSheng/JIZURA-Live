using System.IO;

namespace JizuraLive.App;

// 不放 exe 旁边：Program Files 普通权限写不进，publish/升级覆盖还会清掉，
// 设置和登录态每发一版丢一次
public static class AppDirs
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JizuraLive");

    public static string ConfigDir => Path.Combine(Root, "config");
    public static string LogsDir => Path.Combine(Root, "logs");

    /// <summary>用户字体库：网络拉取的字体缓存 + 上传的字体本体（引擎 JFonts.UserFontDir 指这里）。
    /// 不放 exe 旁的原因同设置：安装目录普通权限写不进，升级覆盖还会清掉。</summary>
    public static string FontsDir => Path.Combine(Root, "fonts");
}