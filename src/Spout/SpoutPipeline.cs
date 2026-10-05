using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Text.Json;
using CefSharp;

namespace JizuraLive;

public sealed class SpoutPipeline : IDisposable
{
    public D3DChromiumWebBrowser? _browser;
    private MirrorRenderHandler? _renderHandler;
    public static SpoutPipeline Instance { get; } = new();

    public bool IsRunning { get; private set; }

    public string? SenderName { get; private set; }

    public Action? SyncPoke { get; set; }

    private bool _externalBeginFrame;

    // EBF 泵线程与在途标记：上一拍还没被 CEF UI 线程消费就跳过本拍，
    // CEF 落后时不堆积 BeginFrame（猝发帧比背压队列难看）
    private Thread? _beginFramePump;
    private volatile bool _pumpRunning;
    private int _beginFrameInFlight;

    // 泵节拍：播放态 = 目标帧率；暂停态降到 10Hz——暂停时页面无 damage，
    // BeginFrame 不出帧，10Hz 只是让编辑/seek 后 ≤100ms 内反映到镜像，
    // 同时掐断合成器空转（实测暂停态页面 rAF 心跳会持续驱出 ~23fps 空帧烧 GPU）
    private const int PausedPumpMs = 100;
    private int _targetFps = 60;
    private volatile int _pumpIntervalMs = 17;

    private long _lastBfErrorTick = -61_000;
    private int _bfErrorsSinceLastLog;

    // 页面主 frame 加载完成（含重载）；宿主模式由 MirrorHostApp 订阅：重放缓存再要新 state
    public Action? PageLoaded;

    // 在镜像页主 frame 执行脚本（宿主模式注入 __jlApplyMsg 用）
    public void EvaluateScript(string script)
    {
        var browser = _browser;

        if (browser == null)
            return;

        try
        {
            browser.GetMainFrame().ExecuteJavaScriptAsync(script);
        }
        catch (Exception ex)
        {
            Log.Warn("[SpoutMirror] EvaluateScript 失败：" + ex.Message);
        }
    }

    public void Dispose()
    {
        Stop();
    }

    // ExternalBeginFrame 开关：设置项 MirrorExternalBeginFrame，
    // 环境变量 JIZURA_MIRROR_EBF=1/0 优先（仿 JIZURA_MIRROR_HOST=0 的一票否决模式）
    public static bool ResolveExternalBeginFrame(Settings? settings)
    {
        var env = Environment.GetEnvironmentVariable("JIZURA_MIRROR_EBF");

        if (string.Equals(env, "1", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(env, "0", StringComparison.OrdinalIgnoreCase)) return false;

        return settings?.MirrorExternalBeginFrame ?? false;
    }

    // desynchronized 画布开关：默认关（URL 带 dsync=0，合成器路径）。
    // EBF 开启后 present 节奏归宿主管、不再被消费端反压，可开 dsync 换低延迟；
    // JIZURA_MIRROR_DSYNC=1/0 环境变量优先
    public static bool ResolveDsync(Settings? settings)
    {
        var env = Environment.GetEnvironmentVariable("JIZURA_MIRROR_DSYNC");

        if (string.Equals(env, "1", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(env, "0", StringComparison.OrdinalIgnoreCase)) return false;

        return settings?.MirrorDsync ?? false;
    }

    public bool Start(string url, string senderName, int fps, int initialW, int initialH, bool transparent = false,
        bool suppressSubtitle = false, Settings? settings = null)
    {
        if (IsRunning)
            return true;

        try
        {
            SenderName = senderName;

            var safeFps = Math.Clamp(fps, 1, 240);
            var safeW = Math.Max(64, initialW);
            var safeH = Math.Max(64, initialH);

            var browserSettings = new BrowserSettings
            {
                // EBF 开启时此项被绕过（帧率归宿主泵），保留作回退路径
                WindowlessFrameRate = safeFps
            };

            _externalBeginFrame = ResolveExternalBeginFrame(settings);
            var dsync = ResolveDsync(settings);

            // 镜像行为（泵/锚点应用/isolate/分辨率保持/工程跟随）已内置在页面本身
            // （RenderPage.vue 的 ?mirror=1 模式），C# 不再注入任何脚本，
            // 只负责推 __jlApplySync 锚点、收 plan-size 消息
            var mirrorUrl = AppendMirrorParams(url, safeFps, transparent, suppressSubtitle, safeW, safeH,
                dsync, _externalBeginFrame);

            // RenderHandler 必须在 CreateBrowser() 之前准备好
            _renderHandler = new MirrorRenderHandler(this);

            _browser = new D3DChromiumWebBrowser(mirrorUrl, browserSettings);

            _browser.RenderHandler = _renderHandler;
            _browser.AudioHandler = new CAudioHandler();
            _browser.FrameLoadEnd += OnFrameLoadEnd;
            _browser.JavascriptMessageReceived += OnMirrorMessage;

            _browser.LoadError += OnMirrorLoadError;

            // 镜像只出画面：与主窗口共享存储后会真正加载歌曲，
            // 同步播放时不静音会出现双重音频
            _browser.BrowserInitialized += OnMirrorBrowserInitialized;

            _browser.Size = new Size(safeW, safeH);

            _browser.CreateD3DBrowser(_externalBeginFrame);

            if (_externalBeginFrame)
                StartBeginFramePump(safeFps);

            _ = WaitInitialLoadAsync(_browser);

            IsRunning = true;

            Log.Info(
                $"Spout 管线已启动：" +
                $"url={url}, " +
                $"通道={senderName}, " +
                $"fps={safeFps}, " +
                $"externalBeginFrame={_externalBeginFrame}, " +
                $"dsync={dsync}, " +
                $"初始视口={safeW}x{safeH}");

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(
                "[SpoutMirror] Spout 管线启动失败",
                ex);

            CleanupLocked();

            return false;
        }
    }

    private static async Task WaitInitialLoadAsync(
        D3DChromiumWebBrowser browser)
    {
        try
        {
            await browser.WaitForInitialLoadAsync();
        }
        catch (Exception ex)
        {
            Log.Error(
                "[SpoutMirror] 页面初始加载失败",
                ex);
        }
    }

    private void OnMirrorBrowserInitialized(
        object? sender,
        EventArgs e)
    {
        try
        {
            _browser?.GetBrowser()?.GetHost()?.SetAudioMuted(true);

            Log.Info("[SpoutMirror] 镜像浏览器已静音");
        }
        catch (Exception ex)
        {
            Log.Warn(
                "[SpoutMirror] 镜像静音失败：" +
                ex.Message);
        }
    }

    private void OnMirrorLoadError(
        object? sender,
        LoadErrorEventArgs e)
    {
        Log.Error(
            $"[SpoutMirror] 页面加载失败：" +
            $"{e.ErrorCode}, " +
            $"{e.ErrorText}, " +
            $"{e.FailedUrl}");
    }

    private void OnFrameLoadEnd(
        object? sender,
        FrameLoadEndEventArgs e)
    {
        if (!e.Frame.IsMain)
            return;

        // 镜像行为由页面原生处理（RenderPage.vue ?mirror=1），无脚本可注入。
        // 页面脚本初始化要一点时间，延迟后再推一次同步锚点
        Log.Info(
            $"[SpoutMirror] 页面加载完成：" +
            $"{e.Url}（镜像模式由页面原生处理）。");

        try
        {
            PageLoaded?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn("[SpoutMirror] PageLoaded 回调失败：" + ex.Message);
        }

        var poke = SyncPoke;

        if (poke != null)
            _ = Task.Delay(1000).ContinueWith(_ =>
            {
                try
                {
                    poke();
                }
                catch (Exception ex)
                {
                    Log.Warn(
                        "[SpoutMirror] SyncPoke 调用失败：" +
                        ex.Message);
                }
            });
    }

    private void OnMirrorMessage(
        object? sender,
        JavascriptMessageReceivedEventArgs e)
    {
        try
        {
            if (e.Message is not IDictionary<string, object> dict)
                return;

            if (!dict.TryGetValue("type", out var typeObj))
                return;

            var type = typeObj?.ToString();

            if (string.Equals(
                    type,
                    "plan-size",
                    StringComparison.OrdinalIgnoreCase))
                HandlePlanSize(dict);

            // sync_play 由主窗口浏览器发出、MainWindow 自己处理，
            // 这里只管镜像端产生的消息
        }
        catch (Exception ex)
        {
            Log.Warn(
                "[SpoutMirror] JavascriptMessageReceived 处理失败：" +
                ex.Message);
        }
    }

    private void HandlePlanSize(
        IDictionary<string, object> dict)
    {
        try
        {
            if (!dict.TryGetValue("w", out var wObj))
                return;

            if (!dict.TryGetValue("h", out var hObj))
                return;

            var w = Convert.ToInt32(wObj);
            var h = Convert.ToInt32(hObj);

            if (w <= 0 || h <= 0)
                return;

            var browser = _browser;

            if (browser == null)
                return;

            var oldSize = browser.Size;

            if (oldSize.Width == w &&
                oldSize.Height == h)
                return;

            Log.Info(
                $"[SpoutMirror] 工程分辨率变更：" +
                $"{oldSize.Width}x{oldSize.Height} -> {w}x{h}");

            browser.Size = new Size(w, h);
        }
        catch (Exception ex)
        {
            Log.Warn(
                "[SpoutMirror] plan-size 消息处理失败：" +
                ex.Message);
        }
    }

    // 镜像参数走 URL 传给页面（RenderPage.vue ?mirror=1 原生处理），不注入脚本。
    // outw/outh = 输出分辨率（设置里的渲染窗口尺寸）：页面把场景按它缩放绘制，
    // 且不再回发 plan-size 覆盖浏览器尺寸——设置的分辨率是输出的唯一事实源。
    // dsync：desynchronized 画布开关，默认关（dsync=0）——desynchronized 的交换链
    // 只有两三块缓冲，合成器调度下 present 要等 C# 取纹理→Spout 释放，消费稍慢就
    // 阻塞 GPU 主线程 100-270ms（循环 wrap 双镜像同时全帧重绘时必现，BeginFrame
    // 断供就是卡顿来源），宁要多一帧（16.7ms）合成器延迟。EBF 开启后 present 节奏
    // 归宿主泵管、不再被反压，可经 Settings.MirrorDsync / JIZURA_MIRROR_DSYNC 打开换低延迟
    private static string AppendMirrorParams(
        string url,
        int fps,
        bool transparent,
        bool suppressSubtitle,
        int outW,
        int outH,
        bool dsync,
        bool ebf)
    {
        var sep = url.Contains('?') ? "&" : "?";

        return
            $"{url}{sep}mirror=1" +
            $"&fps={fps.ToString(CultureInfo.InvariantCulture)}" +
            $"&transparent={(transparent ? 1 : 0)}" +
            $"&nosub={(suppressSubtitle ? 1 : 0)}" +
            $"&outw={outW.ToString(CultureInfo.InvariantCulture)}" +
            $"&outh={outH.ToString(CultureInfo.InvariantCulture)}" +
            $"&dsync={(dsync ? 1 : 0)}" +
            $"&ebf={(ebf ? 1 : 0)}";
    }

    public void ApplyPlaybackSync(
        bool playing,
        double time)
    {
        // EBF 泵节拍跟随播放态：播放 = 目标帧率，暂停 = 10Hz（见 PausedPumpMs 注释）
        if (_externalBeginFrame)
            _pumpIntervalMs = playing ? 1000 / Math.Max(1, _targetFps) : PausedPumpMs;

        var browser = _browser;

        if (browser == null)
            return;

        try
        {
            var payload = new
            {
                playing,
                t = time
            };

            var json =
                JsonSerializer.Serialize(payload);

            var script =
                $"window.__jlApplySync && " +
                $"window.__jlApplySync({json});";

            // 时间流约 30 条/秒，不逐条记日志（会拖慢 UI 线程）
            browser
                .GetMainFrame()
                .ExecuteJavaScriptAsync(script);
        }
        catch (Exception ex)
        {
            Log.Warn(
                "[SpoutMirror] ApplyPlaybackSync 失败：" +
                ex.Message);
        }
    }

    private void CleanupLocked()
    {
        IsRunning = false;

        StopBeginFramePump();

        if (_browser != null)
        {
            _browser.FrameLoadEnd -= OnFrameLoadEnd;
            _browser.JavascriptMessageReceived -= OnMirrorMessage;
            _browser.LoadError -= OnMirrorLoadError;
            _browser.BrowserInitialized -= OnMirrorBrowserInitialized;

            try
            {
                _browser.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn(
                    "[SpoutMirror] Browser Dispose 失败：" +
                    ex.Message);
            }

            _browser = null;
        }

        if (_renderHandler != null)
        {
            try
            {
                _renderHandler.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn(
                    "[SpoutMirror] RenderHandler Dispose 失败：" +
                    ex.Message);
            }

            _renderHandler = null;
        }
    }

    public void Stop()
    {
        if (!IsRunning)
            return;

        var oldName = SenderName;

        CleanupLocked();

        Log.Info(
            $"Spout 管线已停止：通道={oldName}");
    }

    // ---------------- External BeginFrame 泵 ----------------

    // 独立线程按 1/fps 节拍驱动 BeginFrame。不用 DispatcherTimer（受系统计时器
    // ~15.6ms 分辨率拖累）也不用链式驱动（页面暂停无帧输出时会断供）；空闲 tick
    // （页面无 damage）不出帧，成本可忽略。Start 在 WPF UI 线程被调（同进程模式
    // 在主窗口、宿主模式在宿主 Dispatcher），但泵线程独立于两者
    private void StartBeginFramePump(int fps)
    {
        StopBeginFramePump();

        _targetFps = Math.Max(1, fps);
        _pumpIntervalMs = 1000 / _targetFps;
        _pumpRunning = true;
        _beginFramePump = new Thread(PumpLoop)
        {
            IsBackground = true,
            Name = "MirrorBeginFramePump"
        };
        _beginFramePump.Start();
    }

    private void PumpLoop()
    {
        while (_pumpRunning)
        {
            SendBeginFrame();

            // 高精度等待下一拍（节拍每次循环重读：播放/暂停切换立即生效），
            // 粗睡留 2ms 余量，末段自旋补齐
            var nextTickTicks = (long)(_pumpIntervalMs / 1000.0 * Stopwatch.Frequency);
            var next = Stopwatch.GetTimestamp() + nextTickTicks;
            while (_pumpRunning)
            {
                var remainMs = (next - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency;
                if (remainMs <= 0)
                    break;
                if (remainMs > 2)
                    Thread.Sleep(1);
                else
                    Thread.SpinWait(64);
            }
        }
    }

    private void SendBeginFrame()
    {
        var browser = _browser;

        if (browser == null)
            return;

        // 上一拍还没被 CEF UI 线程消费就跳过：CEF 落后时不堆积 BeginFrame
        if (Interlocked.CompareExchange(ref _beginFrameInFlight, 1, 0) != 0)
            return;

        try
        {
            // IBrowserHost 方法要求 CEF UI 线程；MultiThreadedMessageLoop 下
            // 泵线程必须经 UIThreadTaskFactory 投递，不能直接调
            Cef.UIThreadTaskFactory.StartNew(() =>
            {
                try
                {
                    browser.GetBrowser()?.GetHost()?.SendExternalBeginFrame();
                }
                catch (Exception ex)
                {
                    LogBeginFrameError(ex);
                }
                finally
                {
                    Interlocked.Exchange(ref _beginFrameInFlight, 0);
                }
            });
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _beginFrameInFlight, 0);
            LogBeginFrameError(ex);
        }
    }

    private void StopBeginFramePump()
    {
        _pumpRunning = false;

        var pump = _beginFramePump;
        _beginFramePump = null;

        if (pump != null && pump.IsAlive && pump != Thread.CurrentThread)
            pump.Join(500);
    }

    private void LogBeginFrameError(Exception ex)
    {
        _bfErrorsSinceLastLog++;

        var now = Environment.TickCount64;
        if (now - _lastBfErrorTick < 30_000)
            return;

        var suppressed = _bfErrorsSinceLastLog - 1;
        _bfErrorsSinceLastLog = 0;
        _lastBfErrorTick = now;
        Log.Warn(
            "[SpoutMirror] SendExternalBeginFrame 失败" +
            (suppressed > 0 ? $"（节流：此前 {suppressed} 条同类错误未逐条记录）" : "") +
            "：" + ex.Message);
    }
}