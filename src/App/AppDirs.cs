using System.IO;

namespace JizuraLive.App;

// 不放 exe 旁边：Program Files 普通权限写不进，publish/升级覆盖还会清掉，
// 设置和登录态每发一版丢一次
public static class AppDirs
{
    /// <summary>数据根目录。JIZURA_PV_DATA_DIR / JIZURA_DATA_DIR 可重定向——测试进程必须指向临时目录：
    /// 测试曾经直接读写用户的真实 autosave（工程被覆盖成测试标题，userFonts 被灌成百万条）。</summary>
    public static string Root { get; } =
        Environment.GetEnvironmentVariable("JIZURA_PV_DATA_DIR") is { Length: > 0 } pvDir
            ? pvDir
            : Environment.GetEnvironmentVariable("JIZURA_DATA_DIR") is { Length: > 0 } dir
                ? dir
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JizuraLivePV");

    public static string ConfigDir => Path.Combine(Root, "config");
    public static string LogsDir => Path.Combine(Root, "logs");
    public static string CacheDir => Path.Combine(Root, "cache");
    public static string CloudDir => Path.Combine(Root, "cloud");

    /// <summary>音频缓存库：包含自动保存工程的音频副本以及解包的音频文件。</summary>
    public static string AudioCacheDir => Path.Combine(CacheDir, "audio");

    /// <summary>用户字体库：网络拉取的字体缓存 + 上传的字体本体（引擎 JFonts.UserFontDir 指这里）。
    /// 不放 exe 旁的原因同设置：安装目录普通权限写不进，升级覆盖还会清掉。</summary>
    public static string FontsDir => Path.Combine(Root, "fonts");
}