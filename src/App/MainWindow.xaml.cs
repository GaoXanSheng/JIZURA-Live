using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using CefSharp;
using JizuraLive.Cef;
using JizuraLive.Logging;
using JizuraLive.Server;
using JizuraLive.Spout;

namespace JizuraLive.App;

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

        // 保存按钮走 a[download] 下载，不设 DownloadHandler 会被 CEF 静默取消，按钮表现为无效
        Browser.Address = url;
        Browser.DownloadHandler = AppDownloadHandler.Instance;
        Browser.MenuHandler = AppContextMenuHandler.Instance;
        Browser.FrameLoadEnd += OnBrowserFrameLoadEnd;
        Browser.JavascriptMessageReceived += OnJavascriptMessageReceived;
        Browser.LoadError += (_, e) =>
            Log.Error($"Browser LoadError: {e.ErrorCode}, {e.ErrorText}, URL: {e.FailedUrl}");

        // /mcp 端点经此桥把 JSON-RPC 派发进页面执行
        McpBridge.EvaluateAsync = EvaluateMcpAsync;

        // 镜像宿主拆分模式：宿主回调接回本窗口（等价同进程模式里管线直接持有的引用）
        if (MirrorHostBridge.Instance is { Enabled: true } bridge)
            WireMirrorHostBridge(bridge);

        Loaded += (_, _) =>
        {
            if (_clean)
                ApplyClean();

            if (_settings.SpoutEnabled)
                StartSpout();
        };

        // 最大化 ↔ 窗口化按钮的图标/提示跟随窗口状态
        StateChanged += (_, _) => UpdateMaxButton();

        Closed += (_, _) =>
        {
            McpBridge.EvaluateAsync = null;
            SpoutPipeline.Instance.Stop();
            _lyricsPipeline.Stop();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ((HwndSource)PresentationSource.FromVisual(this))?.AddHook(WndProc);
        UpdateMaxButton();
    }

    // WindowStyle=None 的窗口最大化默认盖住任务栏、边缘还悬出屏幕一截：
    // 挂 WM_GETMINMAXINFO 把最大化尺寸/位置钉到最近显示器的 work area（任务栏以内）解决
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO)
            return IntPtr.Zero;

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        var hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (hMonitor == IntPtr.Zero || !GetMonitorInfo(hMonitor, ref mi))
            return IntPtr.Zero;

        mmi.ptMaxPosition.x = mi.rcWork.left - mi.rcMonitor.left;
        mmi.ptMaxPosition.y = mi.rcWork.top - mi.rcMonitor.top;
        mmi.ptMaxSize.x = mi.rcWork.right - mi.rcWork.left;
        mmi.ptMaxSize.y = mi.rcWork.bottom - mi.rcWork.top;
        Marshal.StructureToPtr(mmi, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    // 拆分模式：Spout 管线在独立宿主进程里，本窗口只发命令不建管线
    // 拆分模式：管线在宿主进程里，本进程 SpoutPipeline.IsRunning 恒为 false，
    // RestartRender 等外部调用方据此判断该走 IPC 而不是看本进程管线状态
    public bool Split => MirrorHostBridge.Instance is { Enabled: true };

    private void WireMirrorHostBridge(MirrorHostBridge bridge)
    {
        bridge.PokeSync = ForceSyncResend;
        // 镜像页(重)加载后由页面 __jlHostResend 补发全量 state；宿主 ready 常早于编辑器就绪，带重试
        bridge.NeedEditorState = () => _ = PokeEditorStateWithRetryAsync();
    }

    // 拆分开关切换：按目标模式重建管线，无需重启应用（镜像输出会黑 1~2 秒）
    public void ApplySplitMode(bool enabled)
    {
        if (enabled)
        {
            if (Split)
                return;

            Log.Info("切换到镜像宿主拆分模式");
            SpoutPipeline.Instance.Stop();
            _lyricsPipeline.Stop();

            var bridge = new MirrorHostBridge(_settings, _url);
            WireMirrorHostBridge(bridge);
            bridge.Start();
            if (_settings.SpoutEnabled)
                bridge.SendCmd("start");
        }
        else
        {
            if (!Split)
                return;

            Log.Info("切回同进程镜像模式");
            MirrorHostBridge.Instance!.Dispose(); // 优雅关闭宿主，Instance 置 null，Split 变 false
            if (_settings.SpoutEnabled)
                StartSpout();
        }
    }
    private string RenderUrl => _url + "render";

    // 独立歌词通道的渲染页，透明底只画歌词
    private string LyricsRenderUrl => RenderUrl + "?layer=lyric";

    // 独立歌词通道的 Spout 发送端名
    private string LyricsSenderName => _settings.SpoutName + "-LYRICS";

    public void StartSpout()
    {
        if (Split)
        {
            MirrorHostBridge.Instance!.SendCmd("start");
            return;
        }

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
            _settings.SpoutLyricsEnabled,
            _settings);

        ApplySpoutLyrics();
    }

    public void StopSpout()
    {
        if (Split)
        {
            MirrorHostBridge.Instance!.SendCmd("stop");
            return;
        }

        SpoutPipeline.Instance.Stop();
        _lyricsPipeline.Stop();
    }

    // 独立歌词通道：按设置开关启停，时间锚点与主通道同源（sync_play 同时喂两路）
    public void ApplySpoutLyrics()
    {
        if (Split)
        {
            MirrorHostBridge.Instance!.SendCmd("apply-lyrics");
            return;
        }

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
        // 字幕通道帧率可配置（默认 30），生效值钳到主通道帧率——
        // 字幕层画得比主通道快没有意义；同宿主共享 gpu-process 时过高还会拖垮主通道
        _lyricsPipeline.Start(
            LyricsRenderUrl,
            LyricsSenderName,
            Math.Min(_settings.LyricsFps, _settings.RenderFps),
            _settings.Width,
            _settings.Height,
            true,
            false,
            _settings);
    }

    private void RestartLyrics()
    {
        _lyricsPipeline.Stop();
        StartLyrics();
    }

    public void RestartSpout()
    {
        if (Split)
        {
            MirrorHostBridge.Instance!.SendCmd("restart");
            return;
        }

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
            _settings.SpoutLyricsEnabled,
            _settings);

        if (_settings.SpoutLyricsEnabled)
            RestartLyrics();
        else
            _lyricsPipeline.Stop();
    }

    // 采样钟热更新：设置里改输出帧率后调，主页面采样间隔立即跟随（__jlSyncMs 由注入脚本读取）
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

    private void OnBrowserFrameLoadEnd(object? sender, FrameLoadEndEventArgs e)
    {
        if (!e.Frame.IsMain)
            return;

        if (_clean)
            Browser.ExecuteScriptAsync(
                "window.JizuraLive && JizuraLive.setClean(true);");
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

        // 编辑页(重)加载后全局丢失，等页面就绪戳一次 __jlHostResend 补发最新 state
        if (Split)
        {
            _ = Task.Delay(1500).ContinueWith(_ =>
                Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        Browser.ExecuteScriptAsync("window.__jlHostResend && window.__jlHostResend();");
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("编辑页 state 补发戳失败：" + ex.Message);
                    }
                }));
        }
    }

    private void OnJavascriptMessageReceived(object? sender, JavascriptMessageReceivedEventArgs e)
    {
        try
        {
            if (e.Message is not IDictionary<string, object> dict ||
                !dict.TryGetValue("type", out var typeObj) ||
                typeObj == null)
                return;

        // sync_play 是高频时间流（约 30 条/秒），分流处理不逐条记日志
        if (string.Equals(typeObj.ToString(), "sync_play", StringComparison.OrdinalIgnoreCase))
        {
            HandleSyncPlay(dict);
            return;
        }

        // 镜像宿主中继：编辑页 bindEditorBroadcast 镜像给宿主的 BC 消息（state/project/sub）。
        // 标记键不能下划线开头——CefSharp 的 V8→CLR 转换会丢 __ 开头的键
        if (dict.ContainsKey("jlHostRelay"))
        {
            Log.Info($"[中继] 收到编辑页消息：{typeObj}");
            MirrorHostBridge.Instance?.SendMsg(dict);
            return;
        }

            // MCP 导出指定的免确认落盘目录，dir=null 表示导出结束、恢复另存为
            if (string.Equals(typeObj.ToString(), "set_autosave_dir", StringComparison.OrdinalIgnoreCase))
            {
                var dir = dict.TryGetValue("dir", out var dirObj) ? dirObj?.ToString() : null;
                AppDownloadHandler.AutoSaveDir = string.IsNullOrWhiteSpace(dir) ? null : dir;
                Log.Info("免确认导出目录：" + (AppDownloadHandler.AutoSaveDir ?? "（已清除，恢复另存为）"));
            }
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

            // 锚点经管道喂宿主里的两路管线
            if (Split)
            {
                MirrorHostBridge.Instance!.SendPlay(playing, time);
                return;
            }

            SpoutPipeline.Instance.ApplyPlaybackSync(playing, time);
            _lyricsPipeline.ApplyPlaybackSync(playing, time);
        }
        catch (Exception ex)
        {
            Log.Warn("[MainWindow] sync_play 解析失败：" + ex.Message);
        }
    }

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

    // 宿主 ready/镜像页加载常早于编辑器浏览器初始化完成，state 补发戳带重试直到页面可执行脚本
    private async Task PokeEditorStateWithRetryAsync()
    {
        for (var i = 0; i < 30; i++)
        {
            try
            {
                if (Browser.IsBrowserInitialized && Browser.CanExecuteJavascriptInMainFrame)
                {
                    Browser.ExecuteScriptAsync("window.__jlHostResend && window.__jlHostResend();");
                    return;
                }
            }
            catch
            {
                // 浏览器尚在初始化，等下一轮
            }

            await Task.Delay(500);
        }

        Log.Warn("编辑页 15s 未就绪，宿主 state 补发放弃（等下一次镜像页加载再触发）");
    }

    // 页面里执行脚本并取回 JSON.stringify 的结果（null = 通知）。
    // McpModule 在 HTTP 线程调，先切 UI 线程再交给 CEF
    private Task<string?> EvaluateMcpAsync(string script)
    {
        return Dispatcher.InvokeAsync(() => EvaluateMcpOnUiAsync(script)).Task.Unwrap();
    }

    private async Task<string?> EvaluateMcpOnUiAsync(string script)
    {
        if (!Browser.IsBrowserInitialized || !Browser.CanExecuteJavascriptInMainFrame)
            throw new InvalidOperationException("页面尚未加载完成");

        var response = await Browser.EvaluateScriptAsync(script, TimeSpan.FromSeconds(60));
        if (!response.Success)
            throw new InvalidOperationException(response.Message);

        return response.Result as string;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 双击标题栏在最大化/窗口化之间切换（标准窗口行为）
        if (e.ClickCount == 2)
        {
            BtnMax_Click(sender, e);
            return;
        }

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

    private void BtnMax_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    // 最大化态显示「窗口化」图标（还原），普通态显示「最大化」
    private void UpdateMaxButton()
    {
        var max = WindowState == WindowState.Maximized;
        BtnMax.Content = max ? "\uE923" : "\uE739";
        BtnMax.ToolTip = max ? "窗口化" : "最大化";
    }

    private const int WM_GETMINMAXINFO = 0x0024;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

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