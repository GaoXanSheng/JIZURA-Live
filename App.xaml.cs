using System.Diagnostics;
using System.Windows;
using JizuraLive.Server;

namespace JizuraLive;

public partial class App : Application
{
    private const string MutexName = "JizuraLive_SingleInstance";
    private Mutex? _mutex;
    private Mutex? _hostMutex;
    private EmbeddedServer? _server;
    private Settings? _settings;

    private bool _startupComplete;
    private bool _isMirrorHost;
    private string _url = "";

    /// <summary>开发模式时为 Vite dev server 地址（如 http://localhost:5174/），否则 null。</summary>
    public static string? DevUrl { get; private set; }

    /// <summary>本机设置（AppController 等服务端组件读取）。</summary>
    public Settings? Settings { get; private set; }

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

        // 角色分支最先于一切单实例逻辑：--mirror-host 是同 exe 的第二进程形态，
        // 若先走主实例互斥/进程名检查，会被正在运行的主实例误杀
        if (e.Args.Contains("--mirror-host", StringComparer.OrdinalIgnoreCase))
        {
            _isMirrorHost = true;
            RunAsMirrorHost(e.Args);
            return;
        }

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

            // 主互斥空闲 + 宿主互斥被占 ⇒ 孤儿宿主：催它自灭，免得占着 Spout 发送器还挡启动
            MirrorHostBridge.CleanupStaleHost();

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
            Settings = settings;

            if (!CefRuntime.Initialize(settings))
            {
                Log.Fatal("CEF 初始化失败。");
                Shutdown(-3);
                return;
            }

            Log.Info("CEF 就绪");

            // 开发模式：设置环境变量 JIZURA_DEV=1（或 JIZURA_DEV=http://localhost:5174/ 指定地址），
            // CEF 主窗口与 Spout 渲染 browser 都改载 Vite dev server（frontend 下 npm run dev），
            // 页面热更新即时生效；本机 8642 服务照常启动，由 Vite 的 /api 代理回来到这里。
            string dev = Environment.GetEnvironmentVariable("JIZURA_DEV") ?? "";
            if (dev.Equals("1", StringComparison.OrdinalIgnoreCase))
                dev = "http://localhost:5174/";
            DevUrl = dev.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? dev : null;
            if (DevUrl != null)
                Log.Info($"开发模式：页面加载 {DevUrl}（API 经 Vite 代理回本机服务）");

            _url = DevUrl ?? $"http://127.0.0.1:{_server.Port}/";

            // 镜像宿主拆分：主程序不再本地建 Spout 管线，宿主子进程持有独立 GPU 进程，
            // 编辑器与镜像的 GPU 饱和互相隔离；JIZURA_MIRROR_HOST=0 或 settings 回退同进程
            if (MirrorHostBridge.IsEnabled(settings))
            {
                var bridge = new MirrorHostBridge(settings, _url);
                bridge.Start();
            }
            else
            {
                Log.Info("镜像宿主拆分已关闭，Spout 管线走同进程模式");
            }

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

    // ---------------- 镜像宿主进程形态 ----------------

    private void RunAsMirrorHost(string[] args)
    {
        // 独立日志：双进程并发写同一文件会互相穿插
        Log.Init("jizura-mirror.log");
        HookGlobalExceptions();

        string? url = null, pipeName = MirrorHostBridge.PipeName, token = null;
        int parentPid = 0;
        foreach (var a in args)
        {
            if (a.StartsWith("--url=", StringComparison.OrdinalIgnoreCase))
                url = a["--url=".Length..].Trim('"');
            else if (a.StartsWith("--pipe=", StringComparison.OrdinalIgnoreCase))
                pipeName = a["--pipe=".Length..];
            else if (a.StartsWith("--parent-pid=", StringComparison.OrdinalIgnoreCase))
                int.TryParse(a["--parent-pid=".Length..], out parentPid);
            else if (a.StartsWith("--token=", StringComparison.OrdinalIgnoreCase))
                token = a["--token=".Length..];
        }

        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(token))
        {
            Log.Fatal("镜像宿主缺少 --url/--token 参数，退出。");
            Shutdown(-4);
            return;
        }

        try
        {
            // 单宿主互斥：已有一个宿主时催它退（它有父进程监视，通常秒退），最多等 8s
            try
            {
                using (var ev = EventWaitHandle.OpenExisting(MirrorHostBridge.ShutdownEventName))
                    ev.Set();
            }
            catch
            {
            }

            var deadline = Environment.TickCount64 + 8000;
            while (true)
            {
                _hostMutex = new Mutex(initiallyOwned: true, MirrorHostBridge.HostMutexName, out var createdNew);
                if (createdNew)
                    break;

                _hostMutex.Dispose();
                if (Environment.TickCount64 > deadline)
                {
                    Log.Fatal("另一个镜像宿主 8s 未退出，放弃。");
                    Shutdown(-4);
                    return;
                }

                Thread.Sleep(200);
            }

            var settings = Settings.Load();
            _settings = settings;
            Settings = settings;

            // 独立 CEF cache 目录（CEF 锁目录，不能与主实例共享）；
            // 调试端口从设置走（MirrorCdpEnabled+MirrorCdpPort），桥接器注入的 env JIZURA_CDP_PORT 仍最优先
            var mirrorCdpOn = settings.MirrorCdpEnabled;
            var mirrorCdp = Math.Clamp(settings.MirrorCdpPort, 0, 65535);
            var cdpUsable = mirrorCdpOn && mirrorCdp > 0;
            if (!CefRuntime.Initialize(settings, "cef-cache-mirror", cdpUsable ? mirrorCdp : null,
                    cdpOff: !cdpUsable))
            {
                Log.Fatal("镜像宿主 CEF 初始化失败。");
                Shutdown(-3);
                return;
            }

            Log.Info($"镜像宿主 CEF 就绪（独立 GPU 进程，cdp={(cdpUsable ? mirrorCdp.ToString() : "关闭")}）");

            // 无窗进程在任务管理器里只有 exe 名，给个藏在屏幕外的标题窗口当身份牌；
            // 设为 MainWindow：它若被关了宿主就退出，语义正好
            var badge = new Window
            {
                Title = "字面 Live 渲染进程",
                Width = 0,
                Height = 0,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000
            };
            badge.Show();
            MainWindow = badge;

            var host = new MirrorHostApp(Dispatcher, token);
            host.Run(url!, pipeName, parentPid);
            _startupComplete = true;
        }
        catch (Exception ex)
        {
            Log.Fatal("镜像宿主启动严重异常：" + ex);
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

        if (_isMirrorHost)
        {
            // 宿主：停双管线释放 Spout 发送器，再关 CEF
            MirrorHostApp.Instance?.ShutdownAll();
            CefRuntime.Shutdown();
            _hostMutex?.Dispose();
            Log.Shutdown();
            base.OnExit(e);
            return;
        }

        // 主：先优雅收宿主（shutdown 命令 → 3s 宽限 → Kill），再清自己的东西
        MirrorHostBridge.Instance?.Dispose();
        SpoutPipeline.Instance.Stop();
        CefRuntime.Shutdown();
        _server?.Dispose();
        _mutex?.Dispose();
        Log.Shutdown();
        base.OnExit(e);
    }
}
