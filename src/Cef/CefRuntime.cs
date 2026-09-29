using System.IO;
using System.Threading;
using CefSharp;
using CefSharp.Wpf;

namespace JizuraLive;

public static class CefRuntime
{
    public static string CachePath { get; private set; } = "";
    public static string LogFile { get; private set; } = "";
    private static bool _initialized;
    public static bool Initialize()
    {
        if (_initialized || Cef.IsInitialized == true)
        {
            _initialized = true;
            return true;
        }
        CachePath = Path.Combine(AppContext.BaseDirectory, "cef-cache");
        LogFile = Path.Combine(CachePath, "cef_debug.log");
        var cefSettings = new CefSharp.Wpf.CefSettings
        {
            RootCachePath = CachePath,
            CachePath = CachePath,
            LogSeverity = LogSeverity.Verbose,
            LogFile = LogFile,
            WindowlessRenderingEnabled = true,
            ResourcesDirPath = AppContext.BaseDirectory,
        };

        // CDP 远程调试端口（仅绑定 127.0.0.1），可用环境变量 JIZURA_CDP_PORT 覆盖，设为 0 关闭
        string cdpPort = Environment.GetEnvironmentVariable("JIZURA_CDP_PORT") ?? "9222";
        if (int.TryParse(cdpPort, out int port) && port > 0)
        {
            cefSettings.RemoteDebuggingPort = port;
            Log.Info($"CDP 远程调试端口已开启：http://127.0.0.1:{port}/json");
        }

        // 镜像（离屏）浏览器没有用户手势，AudioContext 默认被自动播放策略拦停，
        // 播放时钟会走不动；主窗口本身有真实交互，放开无副作用
        cefSettings.CefCommandLineArgs["autoplay-policy"] = "no-user-gesture-required";
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
