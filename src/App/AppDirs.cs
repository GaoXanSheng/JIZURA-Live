using System.IO;

namespace JizuraLive.App;

public static class AppDirs
{
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

    public static string AudioCacheDir => Path.Combine(CacheDir, "audio");

}
