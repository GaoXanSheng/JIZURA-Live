using System.IO;
using CefSharp;
using CefSharp.Wpf;

namespace JizuraLive;

public static class CefRuntime
{
    private static bool _initialized;
    public static string CachePath { get; private set; } = "";
    public static string LogFile { get; private set; } = "";

    public static bool Initialize(Settings? settings = null)
    {
        if (_initialized || Cef.IsInitialized == true)
        {
            _initialized = true;
            return true;
        }

        CachePath = Path.Combine(AppDirs.Root, "cef-cache");
        LogFile = Path.Combine(CachePath, "cef_debug.log");
        var cefSettings = new CefSettings
        {
            RootCachePath = CachePath,
            CachePath = CachePath,
            LogSeverity = LogSeverity.Verbose,
            LogFile = LogFile,
            WindowlessRenderingEnabled = true,
            MultiThreadedMessageLoop = true,
            ResourcesDirPath = AppContext.BaseDirectory,
            BackgroundColor = Cef.ColorSetARGB(0, 0, 0, 0)
        };

        // 镜像（离屏）浏览器没有用户手势，AudioContext 默认被自动播放策略拦停，
        // 播放时钟会走不动；主窗口本身有真实交互，放开无副作用
        cefSettings.CefCommandLineArgs["autoplay-policy"] = "no-user-gesture-required";
        cefSettings.CefCommandLineArgs.Remove("disable-gpu-compositing");
        string extraArgs = Environment.GetEnvironmentVariable("JIZURA_CEF_ARGS") ?? "";
        foreach (string tok in extraArgs.Split(
                     new[] { ' ', ',' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = tok.IndexOf('=');

            if (eq > 0)
                cefSettings.CefCommandLineArgs[tok[..eq]] = tok[(eq + 1)..];
            else
                cefSettings.CefCommandLineArgs[tok] = "1";
        }

        int? cdpPort = null;
        string envPort = Environment.GetEnvironmentVariable("JIZURA_CDP_PORT") ?? "";
        if (int.TryParse(envPort, out int envParsed) && envParsed > 0)
        {
            cdpPort = envParsed;
            Log.Info($"CDP 端口来自环境变量：JIZURA_CDP_PORT={envParsed}");
        }
        else if (settings != null &&
                 settings.CdpEnabled &&
                 settings.CdpPort > 0)
        {
            cdpPort = settings.CdpPort;
        }

        if (cdpPort.HasValue)
        {
            cefSettings.RemoteDebuggingPort = cdpPort.Value;
            cefSettings.CefCommandLineArgs["remote-allow-origins"] = "*";
            Log.Info(
                $"CDP 远程调试端口已开启：" +
                $"http://127.0.0.1:{cdpPort.Value}/json");
        }
        else
        {
            Log.Info("CDP 远程调试端口未启用");
        }

        foreach (var kv in cefSettings.CefCommandLineArgs)
            Log.Info($"CEF 命令行参数：{kv.Key}={kv.Value}");
        bool success = false;
        try
        {
            success = Cef.Initialize(cefSettings, performDependencyCheck: false);
        }
        catch (Exception ex)
        {
            Log.Error("Cef.Initialize 抛出异常", ex);
        }

        Log.Info($"Cef.Initialize 返回: {success}, Cef.IsInitialized: {Cef.IsInitialized}");
        _initialized = Cef.IsInitialized == true;
        return _initialized;
    }

    public static void Shutdown()
    {
        if (Cef.IsInitialized == true) Cef.Shutdown();
    }
}