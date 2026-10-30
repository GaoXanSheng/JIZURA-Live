using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using JizuraLive.App.I18n;
using JizuraLive.Logging;

namespace JizuraLive.App;

public partial class App : Application
{
    [DllImport("shell32.dll", SetLastError = true)]
    private static extern void SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string AppID);

    private const string MutexName = "JizuraLivePV_SingleInstance";
    private Mutex? _mutex;
    private Settings? _settings;

    private bool _startupComplete;

    public Settings? Settings { get; private set; }

    public static PvStage.PvStageWindow? MainInstance { get; set; }

    public static Settings? SettingsOf() => (Current as App)?.Settings;

    private static bool AnotherRealInstanceRunning()
    {
        if (Environment.GetEnvironmentVariable("JIZURA_PV_ALLOW_MULTI") == "1") return false;
        int self = Environment.ProcessId;
        try
        {
            return Process.GetProcessesByName("JizuraLivePV").Any(p => p.Id != self && !p.HasExited);
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

        try
        {
            SetCurrentProcessExplicitAppUserModelID("JizuraLivePV.App");
        }
        catch { }

        Log.Init();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        JizuraLive.Server.HttpClientFactorySetup.Initialize();
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

            var settings = Settings.Load();
            _settings = settings;
            Settings = settings;

            L.Initialize(settings.UiLang);
            AppCursors.Apply();

            var pvWindow = new PvStage.PvStageWindow();
            MainInstance = pvWindow;
            MainWindow = pvWindow;
            pvWindow.Show();

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
            Log.Info("应用正常退出");
            try { Settings.FlushPending(); } catch (Exception ex) { Log.Warn("退出前保存设置失败：" + ex.Message); }
            try { _mutex?.ReleaseMutex(); } catch { }
            _mutex?.Dispose();
        };
    }
}
