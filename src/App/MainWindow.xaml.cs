using System.Windows;
using System.Windows.Input;
using CefSharp;

namespace JizuraLive;

public partial class MainWindow : Window
{
    private readonly bool _clean;

    private readonly SpoutPipeline _lyricsPipeline = new();
    private readonly Settings _settings;
    private readonly string _url;

    private bool _lastSyncPlaying;
    private bool _lastSyncPlayingValid;

    public MainWindow(Settings settings, string url)
    {
        InitializeComponent();

        _settings = settings;
        _url = url;
        _clean = settings.Clean;

        // 页面"保存"按钮走浏览器下载（a[download]）：
        // 不设 DownloadHandler 时 CEF 会静默取消下载，按钮表现为无效
        Browser.Address = url;
        Browser.DownloadHandler = AppDownloadHandler.Instance;
        Browser.FrameLoadEnd += OnBrowserFrameLoadEnd;
        Browser.JavascriptMessageReceived += OnJavascriptMessageReceived;
        Browser.LoadError += (_, e) =>
            Log.Error($"Browser LoadError: {e.ErrorCode}, {e.ErrorText}, URL: {e.FailedUrl}");

        Loaded += (_, _) =>
        {
            if (_clean)
                ApplyClean();

            if (_settings.SpoutEnabled)
                StartSpout();
        };

        Closed += (_, _) =>
        {
            SpoutPipeline.Instance.Stop();
            _lyricsPipeline.Stop();
        };
    }

    /// <summary>
    ///     Spout 渲染进程加载的纯 canvas 页（/#/render 路由，100% 画布）。
    ///     编辑页与渲染页同源，经 BroadcastChannel 直通工程/时间/字幕，互不打扰。
    /// </summary>
    private string RenderUrl => _url + "render";

    /// <summary>歌词独立通道的渲染页（透明底、只画歌词文字）。</summary>
    private string LyricsRenderUrl => RenderUrl + "?layer=lyric";

    /// <summary>歌词独立通道的 Spout 发送端名。</summary>
    private string LyricsSenderName => _settings.SpoutName + "-LYRICS";

    // ====================================================================
    // Spout
    // ====================================================================

    public void StartSpout()
    {
        var pipeline = SpoutPipeline.Instance;

        if (pipeline.IsRunning)
        {
            if (!string.Equals(pipeline.SenderName, _settings.SpoutName, StringComparison.Ordinal))
                RestartSpout();
            return;
        }

        pipeline.SyncPoke = ForceSyncResend;
        ApplySyncRate();
        pipeline.Start(
            RenderUrl,
            _settings.SpoutName,
            _settings.RenderFps,
            _settings.Width,
            _settings.Height,
            _settings.SpoutTransparent,
            _settings.SpoutLyricsEnabled);

        ApplySpoutLyrics();
    }

    public void StopSpout()
    {
        SpoutPipeline.Instance.Stop();
        _lyricsPipeline.Stop();
    }

    // --------------------------------------------------------------------
    // 独立 Spout 字幕通道：按设置开关启停，透明底、只画歌词，
    // 输出到 <SpoutID>-LYRICS。时间锚点与主通道同源（sync_play 同时喂两路）。
    // --------------------------------------------------------------------

    public void ApplySpoutLyrics()
    {
        if (_settings.SpoutLyricsEnabled)
        {
            if (_lyricsPipeline.IsRunning)
                RestartLyrics();
            else
                StartLyrics();
        }
        else
        {
            _lyricsPipeline.Stop();
        }
    }

    private void StartLyrics()
    {
        _lyricsPipeline.SyncPoke = ForceSyncResend;
        _lyricsPipeline.Start(
            LyricsRenderUrl,
            LyricsSenderName,
            _settings.RenderFps,
            _settings.Width,
            _settings.Height,
            true);
    }

    private void RestartLyrics()
    {
        _lyricsPipeline.Stop();
        StartLyrics();
    }

    public void RestartSpout()
    {
        var pipeline = SpoutPipeline.Instance;
        pipeline.Stop();
        pipeline.SyncPoke = ForceSyncResend;
        ApplySyncRate();
        pipeline.Start(
            RenderUrl,
            _settings.SpoutName,
            _settings.RenderFps,
            _settings.Width,
            _settings.Height,
            _settings.SpoutTransparent,
            _settings.SpoutLyricsEnabled);

        if (_settings.SpoutLyricsEnabled)
            RestartLyrics();
        else
            _lyricsPipeline.Stop();
    }

    // --------------------------------------------------------------------
    // 采样钟热更新：设置里改输出帧率后调用，
    // 主页面采样间隔立即跟随（__jlSyncMs 由注入脚本读取）
    // --------------------------------------------------------------------

    public void ApplySyncRate()
    {
        if (!Browser.IsBrowserInitialized)
            return;

        var ms = Math.Clamp(1000 / Math.Max(1, _settings.RenderFps), 8, 200);
        try
        {
            Browser.ExecuteScriptAsync($"window.__jlSyncMs = {ms};");
        }
        catch (Exception ex)
        {
            Log.Warn("ApplySyncRate 失败：" + ex.Message);
        }
    }

    // ====================================================================
    // 主浏览器加载完成
    // ====================================================================

    private void OnBrowserFrameLoadEnd(object? sender, FrameLoadEndEventArgs e)
    {
        if (!e.Frame.IsMain)
            return;

        if (_clean)
            Browser.ExecuteScriptAsync(
                "window.JizuraLive && JizuraLive.setClean(true);");

        // 采样节奏跟随设置里的"输出帧率"：60fps → 16ms，30fps → 33ms。
        // 间隔存在 window.__jlSyncMs 上，运行时改帧率时
        // ApplySyncRate() 热更新，无需刷新页面
        var syncMs = Math.Clamp(1000 / Math.Max(1, _settings.RenderFps), 8, 200);

        var syncScript = """
                         (function() {
                             try {
                                 if (window.__jl_sync_sender_hooked)
                                     return;

                                 window.__jl_sync_sender_hooked = true;
                                 window.__jlSyncMs = __SYNC_MS__;

                                 window.__jlForceSync = function() {
                                     window.__jlSyncDirty = true;
                                 };

                                 let lastPlaying = false;
                                 let lastT = 0;

                                 (function loop() {
                                     setTimeout(loop, window.__jlSyncMs || 33);
                                     try {
                                         if (!window.J || !J.ui)
                                             return;

                                         const S = J.ui;
                                         const p = !!S.playing;
                                         const t = typeof S.t === 'number' ? S.t : 0;
                                         const dirty = window.__jlSyncDirty === true;

                                         if (dirty) {
                                             window.__jlSyncDirty = false;
                                         }

                                         // 阈值=半个采样间隔：推进超过半帧即上报，
                                         // 帧内平滑由镜像端墙钟外推负责
                                         const eps = (window.__jlSyncMs || 33) / 2000;

                                         if (dirty || p !== lastPlaying || Math.abs(t - lastT) > eps) {
                                             lastPlaying = p;
                                             lastT = t;

                                             if (window.CefSharp && CefSharp.PostMessage) {
                                                 CefSharp.PostMessage({
                                                     type: 'sync_play',
                                                     playing: p,
                                                     t: t
                                                 });
                                             }
                                         }
                                     }
                                     catch(err) {
                                         console.error('[MainWindow] sync error:', err);
                                     }
                                 })();
                             }
                             catch(err) {
                                 console.error('[MainWindow] sync sender hook error:', err);
                             }
                         })();
                         """.Replace("__SYNC_MS__", syncMs.ToString());

        Browser.ExecuteScriptAsync(syncScript);
    }

    // ====================================================================
    // 主浏览器 JS -> C#
    // ====================================================================

    private void OnJavascriptMessageReceived(object? sender, JavascriptMessageReceivedEventArgs e)
    {
        try
        {
            // sync_play 是高频时间流（约 30 条/秒），先分流处理，不逐条记日志
            if (e.Message is IDictionary<string, object> dict &&
                dict.TryGetValue("type", out var typeObj) &&
                string.Equals(typeObj?.ToString(), "sync_play", StringComparison.OrdinalIgnoreCase))
                HandleSyncPlay(dict);
        }
        catch (Exception ex)
        {
            Log.Warn("[MainWindow] JS 消息处理失败：" + ex.Message);
        }
    }

    private void HandleSyncPlay(IDictionary<string, object> dict)
    {
        try
        {
            var playing = dict.TryGetValue("playing", out var playingObj) && Convert.ToBoolean(playingObj);
            var time = dict.TryGetValue("t", out var timeObj) ? Convert.ToDouble(timeObj) : 0;

            // 只在播放态切换时记日志，否则 UI 线程上的日志 I/O 会造成可感知的卡顿
            if (playing != _lastSyncPlaying || !_lastSyncPlayingValid)
            {
                _lastSyncPlaying = playing;
                _lastSyncPlayingValid = true;
                Log.Info($"[MainWindow] sync_play 状态切换：playing={playing}, t={time:F3}");
            }

            SpoutPipeline.Instance.ApplyPlaybackSync(playing, time);
            _lyricsPipeline.ApplyPlaybackSync(playing, time);
        }
        catch (Exception ex)
        {
            Log.Warn("[MainWindow] sync_play 解析失败：" + ex.Message);
        }
    }

    // ====================================================================
    // 强制同步
    // ====================================================================

    public void ForceSyncResend()
    {
        try
        {
            Browser.ExecuteScriptAsync(
                "window.__jlForceSync && window.__jlForceSync();");
        }
        catch (Exception ex)
        {
            Log.Warn("ForceSyncResend 失败：" + ex.Message);
        }
    }

    // ====================================================================
    // 窗口控制
    // ====================================================================

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            try
            {
                DragMove();
            }
            catch (Exception ex)
            {
                Log.Debug("DragMove failed: " + ex);
            }
    }

    private void BtnMin_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ApplyClean()
    {
        if (!Browser.IsBrowserInitialized)
            return;

        Browser.ExecuteScriptAsync(
            $"window.JizuraLive && JizuraLive.setClean({_clean.ToString().ToLowerInvariant()});");
    }
}