using System.IO;
using CefSharp;
using CefSharp.Wpf;
using JizuraLive.App;
using JizuraLive.Logging;

namespace JizuraLive.Cef;

public static class CefRuntime
{
    private static bool _initialized;
    public static string CachePath { get; private set; } = "";
    public static string LogFile { get; private set; } = "";

    // cacheDirName: CEF 锁定 cache 目录，同机多 CEF 实例（如镜像独立宿主进程）必须各用一份；
    // cdpPortOverride: 宿主进程的 CDP 端口与主进程错开，环境变量 JIZURA_CDP_PORT 仍最优先；
    // cdpOff: 显式关闭 CDP——设置里端口填 0 时用，免得顺路落进 settings.CdpEnabled 分支
    // 与主进程端口撞车
    public static bool Initialize(Settings? settings = null, string cacheDirName = "cef-cache", int? cdpPortOverride = null, bool cdpOff = false)
    {
        if (_initialized || CefSharp.Cef.IsInitialized == true)
        {
            _initialized = true;
            return true;
        }

        CachePath = Path.Combine(AppDirs.Root, cacheDirName);
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
            BackgroundColor = CefSharp.Cef.ColorSetARGB(0, 0, 0, 0)
        };
        cefSettings.CefCommandLineArgs["autoplay-policy"] = "no-user-gesture-required";
        cefSettings.CefCommandLineArgs.Remove("disable-gpu-compositing");
        cefSettings.CefCommandLineArgs["disable-backgrounding-occluded-windows"] = "1";
        cefSettings.CefCommandLineArgs["disable-renderer-backgrounding"] = "1";
        cefSettings.CefCommandLineArgs["disable-background-timer-throttling"] = "1";
        cefSettings.CefCommandLineArgs["disable-features"] = "CalculateNativeWinOcclusion";
        var extraArgs = Environment.GetEnvironmentVariable("JIZURA_CEF_ARGS") ?? "";
        foreach (var tok in extraArgs.Split(
                     new[] { ' ' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = tok.IndexOf('=');

            if (eq > 0)
                cefSettings.CefCommandLineArgs[tok[..eq]] = tok[(eq + 1)..];
            else
                cefSettings.CefCommandLineArgs[tok] = "1";
        }

        int? cdpPort = null;
        var envPort = Environment.GetEnvironmentVariable("JIZURA_CDP_PORT") ?? "";
        if (cdpOff)
        {
            Log.Info("CDP 远程调试端口已按设置关闭");
        }
        else if (int.TryParse(envPort, out var envParsed) && envParsed > 0)
        {
            cdpPort = envParsed;
            Log.Info($"CDP 端口来自环境变量：JIZURA_CDP_PORT={envParsed}");
        }
        else if (cdpPortOverride.HasValue && cdpPortOverride.Value > 0)
        {
            cdpPort = cdpPortOverride;
            Log.Info($"CDP 端口来自角色覆盖：{cdpPort.Value}");
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
        var success = false;
        try
        {
            success = CefSharp.Cef.Initialize(cefSettings, false);
        }
        catch (Exception ex)
        {
            Log.Error("Cef.Initialize 抛出异常", ex);
        }

        Log.Info($"Cef.Initialize 返回: {success}, Cef.IsInitialized: {CefSharp.Cef.IsInitialized}");
        _initialized = CefSharp.Cef.IsInitialized == true;
        return _initialized;
    }

    public static void Shutdown()
    {
        if (CefSharp.Cef.IsInitialized == true) CefSharp.Cef.Shutdown();
    }
}