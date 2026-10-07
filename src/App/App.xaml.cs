using System.Diagnostics;
using System.Windows;
using JizuraLive.Logging;
using JizuraLive.Server;

namespace JizuraLive.App;

public partial class App : Application
{
    private const string MutexName = "JizuraLive_SingleInstance";
    private Mutex? _mutex;
    private EmbeddedServer? _server;
    private Settings? _settings;

    private bool _startupComplete;

    /// <summary>本机设置（AppController 等服务端组件读取）。</summary>
    public Settings? Settings { get; private set; }

    /// <summary>面板访问主窗口服务（State/Preview/Spout）的入口。</summary>
    public static MainWindow? MainInstance { get; set; }

    /// <summary>面板取全局设置的便捷入口（启动完成后非空）。</summary>
    public static Settings? SettingsOf() => (Current as App)?.Settings;

    private static bool AnotherRealInstanceRunning()
    {
        int self = Environment.ProcessId;
        try
        {
            return Process.GetProcessesByName("JizuraLive").Any(p => p.Id != self && !p.HasExited);
        }
        catch (Exception ex)
        {
            Log.Debug("AnotherRealInstanceRunning check failed: " + ex);
            return false;
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Log.Init();
        HookGlobalExceptions();
        HttpClientFactorySetup.Initialize();

        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
            if (!createdNew && AnotherRealInstanceRunning())
            {
                Log.Info("检测到已运行实例，激活其后退出。");
                Shutdown(-1);
                return;
            }

            _server = EmbeddedServer.Start();
            if (_server == null)
            {
                Log.Error("本地服务启动失败：端口 8642~8651 都被占用。");
                Shutdown(-2);
                return;
            }

            Log.Info($"内嵌服务就绪：http://127.0.0.1:{_server.Port}/（/api 保留；静态站随 CEF 移除）");

            var settings = Settings.Load();
            _settings = settings;
            Settings = settings;

            // 纯 C# 重构期间 MCP 暂不可用（M6 里程碑接回编辑器状态）：
            // 给 MCP 客户端一个明确错误，而不是悬空委托返回"窗口未创建"
            McpBridge.EvaluateAsync = _ =>
                throw new InvalidOperationException("纯 C# 重构进行中：MCP 编辑器桥将在 M6 接回");

            if (e.Args.Contains("--mirror-host", StringComparer.OrdinalIgnoreCase))
                Log.Warn("--mirror-host 已随 CEF 移除，忽略该参数，按主实例启动。");

            var main = new MainWindow(settings);
            MainWindow = main;
            main.Show();
            _startupComplete = true;
        }
        catch (Exception ex)
        {
            Log.Fatal("OnStartup 发生严重异常：" + ex);
            throw;
        }
    }

    private void HookGlobalExceptions()
    {
        DispatcherUnhandledException += (s, e) =>
        {
            Log.Error("UI 未处理异常", e.Exception);
            if (!_startupComplete)
            {
                Shutdown(-9);
            }

            e.Handled = true;
        };

        AppDomain.CurrentDomain.ProcessExit += (s, e) =>
        {
            // 兜底：非 OnExit 路径退出时保住日志落盘
            Log.Shutdown();
        };

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            Log.Fatal("未处理异常（非 UI 线程，进程将终止）：" + e.ExceptionObject);

        TaskScheduler.UnobservedTaskException += (s, e) =>
            Log.Warn("未观察的任务异常：" + e.Exception);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("================ JIZURA Live 退出 ================");
        _server?.Dispose();
        _mutex?.Dispose();
        Log.Shutdown();
        base.OnExit(e);
    }
}
