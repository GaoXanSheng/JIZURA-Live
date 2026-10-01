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

    public void Dispose()
    {
        Stop();
    }

    public bool Start(string url, string senderName, int fps, int initialW, int initialH, bool transparent = false,
        bool suppressSubtitle = false)
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
                WindowlessFrameRate = safeFps
            };

            // 镜像行为（泵/锚点应用/isolate/分辨率保持/工程跟随）已内置于页面本身
            //（RenderPage.vue 的 ?mirror=1 模式），C# 不再注入任何脚本——
            // 只负责推 __jlApplySync 锚点、收 plan-size 消息
            var mirrorUrl = AppendMirrorParams(url, safeFps, transparent, suppressSubtitle);

            // ------------------------------------------------------------
            // 重要：
            // RenderHandler 必须在 CreateBrowser() 之前准备好。
            // ------------------------------------------------------------
            _renderHandler = new MirrorRenderHandler(this);

            _browser = new D3DChromiumWebBrowser(
                mirrorUrl,
                browserSettings);

            // RenderHandler 必须先设置
            _browser.RenderHandler = _renderHandler;
            _browser.AudioHandler = new CAudioHandler();
            // JS / 页面事件
            _browser.FrameLoadEnd += OnFrameLoadEnd;
            _browser.JavascriptMessageReceived += OnMirrorMessage;

            _browser.LoadError += OnMirrorLoadError;

            // 镜像只出画面：与主窗口共享存储后会真正加载歌曲，
            // 同步播放时不静音会出现双重音频
            _browser.BrowserInitialized += OnMirrorBrowserInitialized;

            // 尺寸先设置好
            _browser.Size = new Size(safeW, safeH);

            // ------------------------------------------------------------
            // 最后才真正 CreateBrowser()
            // ------------------------------------------------------------
            _browser.CreateD3DBrowser();

            _ = WaitInitialLoadAsync(_browser);

            IsRunning = true;

            Log.Info(
                $"Spout 管线已启动：" +
                $"url={url}, " +
                $"通道={senderName}, " +
                $"fps={safeFps}, " +
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
        // 页面脚本需要一点时间完成初始化
        Log.Info(
            $"[SpoutMirror] 页面加载完成：" +
            $"{e.Url}（镜像模式由页面原生处理）。");

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

            // ------------------------------------------------------------
            // 注意：
            // sync_play 是主窗口浏览器发过来的，
            // 主窗口自己的 MainWindow 会处理。
            //
            // SpoutPipeline 这里只负责镜像端产生的消息。
            // 所以这里不处理 sync_play。
            // ------------------------------------------------------------
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

    // --------------------------------------------------------------------
    // 镜像参数走 URL 传给页面（RenderPage.vue ?mirror=1 原生处理），
    // 不再注入脚本
    // --------------------------------------------------------------------

    private static string AppendMirrorParams(
        string url,
        int fps,
        bool transparent,
        bool suppressSubtitle)
    {
        var sep = url.Contains('?') ? "&" : "?";

        return
            $"{url}{sep}mirror=1" +
            $"&fps={fps.ToString(CultureInfo.InvariantCulture)}" +
            $"&transparent={(transparent ? 1 : 0)}" +
            $"&nosub={(suppressSubtitle ? 1 : 0)}";
    }

    // --------------------------------------------------------------------
    // 主窗口 -> 镜像窗口
    // --------------------------------------------------------------------

    public void ApplyPlaybackSync(
        bool playing,
        double time)
    {
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

            // 时间流每秒约 30 次，不逐条记日志（会拖慢 UI 线程）

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
}