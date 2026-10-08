using System.Diagnostics;
using System.Windows;
using JizuraLive.App.Mcp;
using JizuraLive.Engine;
using JizuraLive.Logging;
using JizuraLive.Server;

namespace JizuraLive.App;

public partial class App : Application
{
    private const string MutexName = "JizuraLive_SingleInstance";
    private Mutex? _mutex;
    private McpHttpServer? _server;
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
        ProfLog.Install(); // JIZURA_PROF=leaf 时开启叶子级分配归因（默认关，零开销）
        HookGlobalExceptions();
        HttpClientFactorySetup.Initialize();
        // 引擎装配（02_/04_/05_/06_/07_）由 PreviewEngine 构造时完成；Omakase 在其后接线
        // Reg.RandomOk（追加分/和風/套装的随机放行）与氛围标签——Planner 的随机提取依赖它
        Omakase.Install();

        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
            if (!createdNew && AnotherRealInstanceRunning())
            {
                Log.Info("检测到已运行实例，激活其后退出。");
                Shutdown(-1);
                return;
            }

            var settings = Settings.Load();
            _settings = settings;
            Settings = settings;

            var main = new MainWindow(settings);
            MainWindow = main;

            // MCP：17 工具直调 EditorState（无页面桥）；窗口先建好（State/Plan 就绪）再起端点
            McpTools.Current = new McpTools(main.State, main.Preview);
            _server = McpHttpServer.Start(McpTools.Current);
            if (_server == null)
            {
                Log.Error("MCP 服务启动失败：端口 8642~8651 都被占用。");
                Shutdown(-2);
                return;
            }

            Log.Info($"MCP 就绪：http://127.0.0.1:{_server.Port}/mcp（/api 已随 EmbedIO 移除）");

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
