using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using JizuraLive.Server;

namespace JizuraLive;

public partial class App : Application
{
    private const string MutexName = "JizuraLive_SingleInstance";
    private Mutex? _mutex;
    private EmbeddedServer? _server;
    private Settings? _settings;
    private string _url = "";

    private static bool AnotherRealInstanceRunning()
    {
        int self = Environment.ProcessId;
        try
        {
            return Process.GetProcessesByName("JizuraLive").Any(p => p.Id != self && !p.HasExited);
        }
        catch
        {
            return false;
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Init();
        HookGlobalExceptions();

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
            Log.Info($"内嵌服务就绪：http://127.0.0.1:{_server.Port}/");

            // 设置必须在 CEF 初始化前加载：CDP 调试端口等参数
            // 属于 CefSettings，只能在 Cef.Initialize 之前配置
            var settings = Settings.Load();
            _settings = settings;

            if (!CefRuntime.Initialize(settings))
            {
                Log.Fatal("CEF 初始化失败。");
                Shutdown(-3);
                return;
            }
            Log.Info("CEF 就绪");

            _url = $"http://127.0.0.1:{_server.Port}/";
            var main = new MainWindow(settings, _url);
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

    private bool _startupComplete;
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
        SpoutPipeline.Instance.Stop();
        CefRuntime.Shutdown();
        _server?.Dispose();
        _mutex?.Dispose();
        Log.Shutdown();
        base.OnExit(e);
    }
}
