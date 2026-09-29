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
            // ------------------------------------------------------------
            // 必须为 true：镜像（离屏）浏览器依赖 OSR（离屏渲染）。
            // CEF 文档明确要求创建 windowless 浏览器时全局开启此开关；
            // 关掉后离屏浏览器会退化为软件帧（OnPaint），GPU 共享纹理
            //（OnAcceleratedPaint）永远不会触发。
            // 主窗口是 HwndHost（有窗口渲染），不受此全局开关影响。
            // ------------------------------------------------------------
            WindowlessRenderingEnabled = true,
            MultiThreadedMessageLoop = true,
            ResourcesDirPath = AppContext.BaseDirectory,
            BackgroundColor = Cef.ColorSetARGB(0, 0, 0, 0)
        };

        // 镜像（离屏）浏览器没有用户手势，AudioContext 默认被自动播放策略拦停，
        // 播放时钟会走不动；主窗口本身有真实交互，放开无副作用
        cefSettings.CefCommandLineArgs["autoplay-policy"] = "no-user-gesture-required";

        // ------------------------------------------------------------
        // 关键修复：CefSharp 默认注入 disable-gpu-compositing（历史
        // 遗留的稳定性开关），它强制 CEF 走软件合成，后果：
        //   1. GPU 共享纹理（OnAcceleratedPaint）永远不会触发，
        //      镜像只能走 CPU OnPaint（每帧全幅 8MB 内存拷贝）；
        //   2. 页面画布退化为 CPU 光栅（1080p 单帧 ~16ms，仅 ~17fps）。
        // 移除后启用 GPU 合成，两条链路都恢复 GPU。
        // ------------------------------------------------------------
        cefSettings.CefCommandLineArgs.Remove("disable-gpu-compositing");

        // ------------------------------------------------------------
        // GPU 诊断/实验参数：环境变量 JIZURA_CEF_ARGS 注入，
        // 空格或逗号分隔，"k=v" 或 "k"。
        // 例：JIZURA_CEF_ARGS="ignore-gpu-blocklist use-angle=d3d11"
        // ------------------------------------------------------------
        string extraArgs =
            Environment.GetEnvironmentVariable("JIZURA_CEF_ARGS") ?? "";

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

        // CDP 远程调试端口（仅绑定 127.0.0.1），可用环境变量 JIZURA_CDP_PORT 覆盖，设为 0 关闭
        string cdpPort = Environment.GetEnvironmentVariable("JIZURA_CDP_PORT") ?? "9222";
        if (int.TryParse(cdpPort, out int port) && port > 0)
        {
            cefSettings.RemoteDebuggingPort = port;
            Log.Info($"CDP 远程调试端口已开启：http://127.0.0.1:{port}/json");
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
