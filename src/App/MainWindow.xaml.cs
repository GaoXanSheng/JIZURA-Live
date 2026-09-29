using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using CefSharp;

namespace JizuraLive;

public partial class MainWindow : Window
{
    private readonly Settings _settings;
    private readonly string _url;

    private bool _clean;

    private bool _lastSyncPlaying;

    private bool _lastSyncPlayingValid;

    // --------------------------------------------------------------------
    // 时间流仪表：采样间隔健康度（每 60 条汇总一条日志）
    // --------------------------------------------------------------------

    private int _syncCount;
    private long _syncLastTicks;
    private double _syncGapSum;
    private double _syncGapMax;

    public MainWindow(
        Settings settings,
        string url)
    {
        InitializeComponent();

        _settings = settings;
        _url = url;

        _clean =
            settings.Clean;

        // ------------------------------------------------------------
        // 主浏览器
        // ------------------------------------------------------------

        Browser.Address = url;

        // 页面"保存"按钮走浏览器下载（a[download]）：
        // 不设 DownloadHandler 时 CEF 会静默取消下载，按钮表现为无效
        Browser.DownloadHandler = AppDownloadHandler.Instance;

        Browser.FrameLoadEnd +=
            OnBrowserFrameLoadEnd;

        Browser.JavascriptMessageReceived +=
            OnJavascriptMessageReceived;

        Browser.IsBrowserInitializedChanged +=
            (_, _) =>
            {
                Log.Info(
                    $"Browser IsBrowserInitialized: " +
                    $"{Browser.IsBrowserInitialized}");
            };

        Browser.LoadError +=
            (_, e) =>
            {
                Log.Error(
                    $"Browser LoadError: " +
                    $"{e.ErrorCode}, " +
                    $"{e.ErrorText}, " +
                    $"URL: {e.FailedUrl}");
            };

        SourceInitialized +=
            (_, _) =>
            {
                Log.Info(
                    "MainWindow SourceInitialized");
            };

        Loaded +=
            (_, _) =>
            {
                Log.Info(
                    "MainWindow Loaded");

                if (_clean)
                {
                    ApplyClean();
                }

                if (_settings.SpoutEnabled)
                {
                    StartSpout();
                }
            };

        Closing +=
            (_, e) =>
            {
                Log.Info(
                    $"MainWindow Closing, " +
                    $"Cancel={e.Cancel}");
            };

        Closed +=
            (_, _) =>
            {
                Log.Info(
                    "MainWindow Closed");

                SpoutPipeline
                    .Instance
                    .Stop();
            };
    }

    // ====================================================================
    // Spout
    // ====================================================================

    public void StartSpout()
    {
        var pipeline =
            SpoutPipeline.Instance;

        if (pipeline.IsRunning)
        {
            if (
                !string.Equals(
                    pipeline.SenderName,
                    _settings.SpoutName,
                    StringComparison.Ordinal)
            )
            {
                RestartSpout();
            }

            return;
        }

        pipeline.SyncPoke =
            ForceSyncResend;

        ApplySyncRate();

        pipeline.Start(
            _url,
            _settings.SpoutName,
            _settings.RenderFps,
            _settings.Width,
            _settings.Height,
            _settings.SpoutTransparent);
    }

    public void StopSpout()
    {
        SpoutPipeline
            .Instance
            .Stop();
    }

    public void RestartSpout()
    {
        var pipeline =
            SpoutPipeline.Instance;
        pipeline.Stop();
        pipeline.SyncPoke =
            ForceSyncResend;
        ApplySyncRate();
        pipeline.Start(
            _url,
            _settings.SpoutName,
            _settings.RenderFps,
            _settings.Width,
            _settings.Height,
            _settings.SpoutTransparent);
    }

    // --------------------------------------------------------------------
    // 采样钟热更新：设置里改输出帧率后调用，
    // 主页面采样间隔立即跟随（__jlSyncMs 由注入脚本读取）
    // --------------------------------------------------------------------

    public void ApplySyncRate()
    {
        if (!Browser.IsBrowserInitialized)
            return;

        int ms =
            Math.Clamp(
                1000 / Math.Max(1, _settings.RenderFps),
                8,
                200);

        try
        {
            Browser.ExecuteScriptAsync(
                $"window.__jlSyncMs = {ms};");
        }
        catch (Exception ex)
        {
            Log.Warn(
                "ApplySyncRate 失败：" +
                ex.Message);
        }
    }

    // ====================================================================
    // 主浏览器加载完成
    // ====================================================================

    private void OnBrowserFrameLoadEnd(
        object? sender,
        FrameLoadEndEventArgs e)
    {
        Log.Info(
            $"OnBrowserFrameLoadEnd: " +
            $"IsMain={e.Frame.IsMain}, " +
            $"Url={e.Url}");

        if (!e.Frame.IsMain)
            return;

        if (_clean)
        {
            Browser.ExecuteScriptAsync(
                "window.JizuraLive && " +
                "JizuraLive.setClean(true);");
        }

        // 采样节奏跟随设置里的"输出帧率"：60fps → 16ms，30fps → 33ms。
        // 间隔存在 window.__jlSyncMs 上，运行时改帧率时
        // ApplySyncRate() 热更新，无需刷新页面
        int syncMs =
            Math.Clamp(
                1000 / Math.Max(1, _settings.RenderFps),
                8,
                200);

        string syncScript = """
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

                const p =
                    !!S.playing;

                const t =
                    typeof S.t === 'number'
                        ? S.t
                        : 0;

                const dirty =
                    window.__jlSyncDirty === true;

                if (dirty) {
                    window.__jlSyncDirty = false;
                }

                // 阈值=半个采样间隔：推进超过半帧即上报，
                // 帧内平滑由镜像端墙钟外推负责
                const eps = (window.__jlSyncMs || 33) / 2000;

                if (
                    dirty ||
                    p !== lastPlaying ||
                    Math.abs(t - lastT) > eps
                ) {

                    lastPlaying = p;
                    lastT = t;

                    if (
                        window.CefSharp &&
                        CefSharp.PostMessage
                    ) {

                        CefSharp.PostMessage({
                            type: 'sync_play',
                            playing: p,
                            t: t
                        });
                    }
                }

            }
            catch(err) {

                console.error(
                    '[MainWindow] sync error:',
                    err
                );
            }

        })();

    }
    catch(err) {

        console.error(
            '[MainWindow] sync sender hook error:',
            err
        );
    }
})();
""".Replace("__SYNC_MS__", syncMs.ToString());

        Browser.ExecuteScriptAsync(
            syncScript);
    }

    // ====================================================================
    // 主浏览器 JS -> C#
    // ====================================================================

    private void OnJavascriptMessageReceived(
        object? sender,
        JavascriptMessageReceivedEventArgs e)
    {
        try
        {
            // ------------------------------------------------------------
            // sync_play —— 高频时间流（约 30 次/秒），
            // 先分流处理，不逐条记日志
            // ------------------------------------------------------------

            if (
                e.Message
                is IDictionary<string, object> dict
            )
            {
                if (
                    dict.TryGetValue(
                        "type",
                        out var typeObj)
                )
                {
                    string? type =
                        typeObj?.ToString();

                    if (
                        string.Equals(
                            type,
                            "sync_play",
                            StringComparison.OrdinalIgnoreCase)
                    )
                    {
                        HandleSyncPlay(dict);
                        return;
                    }
                }
            }
            
            if (
                e.Message as string ==
                "jl-settings"
            )
            {
                Dispatcher.BeginInvoke(
                    ToggleSettings);

                return;
            }
        }
        catch (Exception ex)
        {
            Log.Warn(
                "[MainWindow] JS 消息处理失败：" +
                ex.Message);
        }
    }

    private void HandleSyncPlay(
        IDictionary<string, object> dict)
    {
        try
        {
            bool playing = false;
            double time = 0;

            if (
                dict.TryGetValue(
                    "playing",
                    out var playingObj)
            )
            {
                playing =
                    Convert.ToBoolean(
                        playingObj);
            }

            if (
                dict.TryGetValue(
                    "t",
                    out var timeObj)
            )
            {
                time =
                    Convert.ToDouble(
                        timeObj);
            }

            // 时间流每秒约 30 条，只在播放态切换时记日志，
            // 否则 UI 线程上的日志 I/O 会造成可感知的卡顿
            if (playing != _lastSyncPlaying ||
                !_lastSyncPlayingValid)
            {
                _lastSyncPlaying = playing;
                _lastSyncPlayingValid = true;

                Log.Info(
                    $"[MainWindow] sync_play 状态切换：" +
                    $"playing={playing}, " +
                    $"t={time:F3}");
            }

            // 时间流仪表：间隔抖动大 = 主页面采样被饿（主线程忙）
            long ts =
                Stopwatch.GetTimestamp();

            if (_syncLastTicks != 0)
            {
                double gapMs =
                    (ts - _syncLastTicks) *
                    1000.0 / Stopwatch.Frequency;

                _syncGapSum += gapMs;

                if (gapMs > _syncGapMax)
                    _syncGapMax = gapMs;
            }

            _syncLastTicks = ts;

            if (++_syncCount >= 60)
            {
                double expected =
                    1000.0 / Math.Max(1, _settings.RenderFps);

                Log.Info(
                    $"[Sync] 时间流：间隔 " +
                    $"avg={_syncGapSum / Math.Max(1, _syncCount - 1):F1}ms, " +
                    $"max={_syncGapMax:F1}ms " +
                    $"（期望≈{expected:F0}ms；max 频繁过大说明主页面采样被饿）");

                _syncCount = 0;
                _syncLastTicks = 0;
                _syncGapSum = 0;
                _syncGapMax = 0;
            }

            SpoutPipeline
                .Instance
                .ApplyPlaybackSync(
                    playing,
                    time);
        }
        catch (Exception ex)
        {
            Log.Warn(
                "[MainWindow] sync_play 解析失败：" +
                ex.Message);
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
                "window.__jlForceSync && " +
                "window.__jlForceSync();");
        }
        catch (Exception ex)
        {
            Log.Warn(
                "[MainWindow] ForceSyncResend 失败：" +
                ex.Message);
        }
    }

    // ====================================================================
    // 窗口控制
    // ====================================================================

    private void TitleBar_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (
            e.ButtonState ==
            MouseButtonState.Pressed
        )
        {
            try
            {
                DragMove();
            }
            catch
            {
            }
        }
    }

    private void BtnMin_Click(
        object sender,
        RoutedEventArgs e)
    {
        WindowState =
            WindowState.Minimized;
    }

    private void BtnClose_Click(
        object sender,
        RoutedEventArgs e)
    {
        Close();
    }

    private void BtnGear_Click(
        object sender,
        RoutedEventArgs e)
    {
        ToggleSettings();
    }

    private void ToggleSettings()
    {
        Log.Info("ToggleSettings 触发");

        SettingsWindow.Toggle(
            _settings,
            this,
            BtnGear);
    }

    private void ApplyClean()
    {
        if (!Browser.IsBrowserInitialized)
            return;

        Browser.ExecuteScriptAsync(
            $"window.JizuraLive && " +
            $"JizuraLive.setClean(" +
            $"{(_clean ? "true" : "false")}" +
            $");");

        _settings.Clean =
            _clean;
    }
}