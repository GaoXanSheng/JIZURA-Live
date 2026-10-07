using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using JizuraLive.Engine;
using JizuraLive.Logging;
using JizuraLive.Spout;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace JizuraLive.App;

public partial class MainWindow : Window
{
    private readonly Settings _settings;
    private readonly PreviewEngine _preview = new();
    private readonly SpoutOut _spout = new();

    /// <summary>编辑器单一事实源（面板经 App.Main.State 访问）。</summary>
    public EditorState State { get; }
    public PreviewEngine Preview => _preview;
    public SpoutOut Spout => _spout;

    // 引擎渲染挪到专用后台线程（乒乓双面）：M2b/c 烟雾实测「koma=12 真渲染 ~24 次/s 每次 ~40ms」
    // ——Render 优先级 DispatcherTimer 把它整个压在 UI 线程 ≈96% 饱和，点击事件永远排不上队
    //（表象：全部按钮不可用 + 全局卡顿）。后台线程渲染，UI 线程只做 1-2ms blit
    private Thread? _renderThread;
    private volatile bool _renderRunning;
    // 持久渲染面 P：引擎跨帧依赖同一块面（整帧缓存 + 增量绘制），每帧必须画同一块——
    // 交替画两块面会把跨帧状态劈成两半（实测：画面持续闪烁）。P 绝不 Dispose（缓存规矩），
    // 换尺寸的旧面进 graveyard。显示侧用「P → 显示双缓冲 memcpy」隔离：UI 只读显示缓冲，
    // 锁只护 1-3ms 的 memcpy/翻转 vs UI blit，不护 40ms 的引擎渲染
    private SKBitmap? _renderBmp;
    private SKSurface? _renderSurface;
    private int _renderW = -1, _renderH = -1;
    private readonly SKBitmap?[] _displayBuf = new SKBitmap[2];
    private int _displayW = -1, _displayH = -1;
    private volatile int _displayFront;
    private readonly object _displayLock = new();
    private readonly List<SKSurface> _stageGraveyard = new();
    // 舞台尺寸缓存（UI SizeChanged 写，渲染线程读——每帧 Dispatcher.Invoke 会白烧调度器）
    private volatile int _stageWpx, _stageHpx;
    // 脏检查：暂停且 t/plan 没变就跳过重绘（暂停态省 ~96% CPU）
    private double _lastRT = -1;
    private long _lastRV = -1;
    private Plan? _lastRPlan;
    // UI 线程健康探针：Dispatcher tick 调度抖动 >33ms 即按钮可感知卡顿，5s 汇总进日志
    private readonly DispatcherTimer _uiProbe;
    private long _probeDue, _probeWindowEnd;
    private double _probeJitterMax;

    public MainWindow(Settings settings)
    {
        InitializeComponent();

        _settings = settings;
        App.MainInstance = this;
        State = new EditorState(_preview);
        State.StateChanged += () => Dispatcher.Invoke(UpdateStatusText);

        // 最大化 ↔ 窗口化按钮的图标/提示跟随窗口状态
        StateChanged += (_, _) => UpdateMaxButton();

        var fps = Math.Clamp(settings.RenderFps, 12, 60);
        _renderRunning = true;
        _renderThread = new Thread(() => RenderPump(1000.0 / fps))
        {
            IsBackground = true,
            Name = "JizuraPreviewRender",
            Priority = ThreadPriority.BelowNormal
        };
        _renderThread.Start();

        // 舞台尺寸缓存：渲染线程每帧读，避免 Dispatcher.Invoke 白烧调度器
        Stage.SizeChanged += (_, e2) =>
        {
            _stageWpx = (int)e2.NewSize.Width;
            _stageHpx = (int)e2.NewSize.Height;
        };

        // UI 线程健康探针：Background 优先级 tick 的调度抖动直接量化"卡顿"，5s 汇总进日志
        _probeDue = _probeWindowEnd = Stopwatch.GetTimestamp();
        _uiProbe = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _uiProbe.Tick += (_, _) =>
        {
            var now = Stopwatch.GetTimestamp();
            var late = (now - _probeDue) * 1000.0 / Stopwatch.Frequency;
            if (late > _probeJitterMax) _probeJitterMax = late;
            _probeDue = now + Stopwatch.Frequency / 10;
            if (now >= _probeWindowEnd)
            {
                if (_probeJitterMax > 33)
                    Log.Warn($"[UI探针] 5s 最大调度抖动 {_probeJitterMax:F0}ms（>33ms 即按钮可感知卡顿）");
                _probeJitterMax = 0;
                _probeWindowEnd = now + 5 * Stopwatch.Frequency;
            }
        };
        _uiProbe.Start();

        UpdateStatusText();
        _preview.Play(); // 预览默认自动播放（暂停态泵只出脏帧，起播才能看到真帧率）

        Loaded += (_, _) =>
        {
            // Spout 启动放 Loaded：管线建 D3D 设备要几百毫秒，别挡窗口先画出来
            if (_settings.SpoutEnabled)
                StartSpout();
        };
    }

    // ---------------- 引擎预览舞台 ----------------

    // UI 线程只做 blit：读显示双缓冲的正面，1-2ms，不碰引擎锁
    private void Stage_PaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        var w = e.Info.Width;
        var h = e.Info.Height;
        canvas.Clear(new SKColor(15, 15, 18));
        lock (_displayLock)
        {
            var buf = _displayBuf[_displayFront];
            if (buf == null) return;
            var px = buf.PeekPixels();
            if (px == null) return;
            using var borrow = SKImage.FromPixels(px.Info, px.GetPixels(), px.RowBytes);
            var fit = Math.Min(w / (double)px.Info.Width, h / (double)px.Info.Height);
            canvas.DrawImage(borrow, (float)((w - px.Info.Width * fit) / 2), (float)((h - px.Info.Height * fit) / 2));
        }
    }

    // ---------------- 后台渲染泵（乒乓双面） ----------------
    // SkiaSharp 3.119+ 规矩不变：交给 Frame 的面绝不 Dispose（整帧缓存持有跨帧快照），
    // 换尺寸的旧面进 graveyard；blit 用 PeekPixels+FromPixels 借像素，不用 Snapshot()

    private void RenderPump(double frameMs)
    {
        var next = Stopwatch.GetTimestamp();
        while (_renderRunning)
        {
            var wakeIn = (next - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency;
            if (wakeIn > 1) Thread.Sleep((int)Math.Min(wakeIn, 50));
            next += (long)(frameMs / 1000.0 * Stopwatch.Frequency);
            if (next < Stopwatch.GetTimestamp()) next = Stopwatch.GetTimestamp(); // 掉帧不追帧
            try
            {
                var plan = _preview.Plan;
                if (plan == null || !_renderRunning) continue;
                _preview.Tick();
                var t = _preview.CurrentTime;
                // 脏检查：暂停且 t/plan 没变就跳过（暂停态省 ~96% CPU）
                var ver = _preview.Version;
                if (!_preview.IsPlaying && t == _lastRT && ver == _lastRV && ReferenceEquals(plan, _lastRPlan))
                    continue;
                _lastRT = t;
                _lastRV = ver;
                _lastRPlan = plan;
                // 预览渲染尺寸：contain 适配舞台（尺寸缓存由 UI SizeChanged 维护），钳 1.0 不超采
                var fit = _stageWpx > 1 && _stageHpx > 1
                    ? Math.Min(Math.Min(_stageWpx / plan.W, _stageHpx / plan.H), 1.0)
                    : 1.0;
                var rw = Math.Max(1, (int)Math.Round(plan.W * fit));
                var rh = Math.Max(1, (int)Math.Round(plan.H * fit));
                // 持久渲染面 P：尺寸不符才换（旧面进 graveyard 绝不 Dispose——整帧缓存持有快照）
                if (_renderSurface == null || _renderW != rw || _renderH != rh)
                {
                    if (_renderSurface != null) _stageGraveyard.Add(_renderSurface);
                    _renderBmp = new SKBitmap(rw, rh, SKColorType.Bgra8888, SKAlphaType.Premul);
                    _renderSurface = SKSurface.Create(_renderBmp.Info, _renderBmp.GetPixels(), _renderBmp.RowBytes);
                    _renderW = rw;
                    _renderH = rh;
                }
                var surf = _renderSurface;
                lock (EngineRenderGate.Lock)
                    _preview.Renderer.Frame(surf.Canvas, plan, t, new RenderOpts { Scale = fit, Stage = true });
                // P → 显示双缓冲 memcpy（~1-3ms），翻面后 UI 读新正面（与 memcpy 目标不同面）
                lock (_displayLock)
                {
                    if (_displayBuf[0] == null || _displayW != rw || _displayH != rh)
                    {
                        _displayBuf[0]?.Dispose(); // 显示缓冲不喂引擎，可安全换新
                        _displayBuf[1]?.Dispose();
                        _displayBuf[0] = new SKBitmap(rw, rh, SKColorType.Bgra8888, SKAlphaType.Premul);
                        _displayBuf[1] = new SKBitmap(rw, rh, SKColorType.Bgra8888, SKAlphaType.Premul);
                        _displayW = rw;
                        _displayH = rh;
                        _displayFront = 0;
                    }
                    var back = 1 - _displayFront;
                    var dst = _displayBuf[back]!;
                    unsafe
                    {
                        Buffer.MemoryCopy((void*)_renderBmp!.GetPixels(), (void*)dst.GetPixels(),
                            (long)dst.RowBytes * rh, (long)_renderBmp.RowBytes * rh);
                    }
                    _displayFront = back;
                }
                Dispatcher.BeginInvoke(() => Stage.InvalidateVisual(), DispatcherPriority.Render);
            }
            catch (Exception ex)
            {
                Log.Debug("预览渲染泵异常：" + ex.Message);
            }
        }
    }

    private void UpdateStatusText()
    {
        StatusChanged?.Invoke();
    }

    /// <summary>状态行变化（TopBar 订阅刷新）。</summary>
    public event Action? StatusChanged;

    // ---------------- Spout 直出（SpoutOut 管线） ----------------

    private SpoutOutOptions SpoutOpts() => new()
    {
        SenderName = _settings.SpoutName,
        MainTransparent = _settings.SpoutTransparent,
        Width = _settings.Width,
        Height = _settings.Height,
        Fps = _settings.RenderFps,
        LyricsEnabled = _settings.SpoutLyricsEnabled,
        // 字幕层画得比主通道快没有意义（旧管线同钳法）
        LyricsFps = Math.Min(_settings.LyricsFps, _settings.RenderFps),
    };

    public void StartSpout()
    {
        if (_spout.IsRunning)
        {
            // 名称变了只有重建发送端才生效
            if (!string.Equals(_spout.SenderName, _settings.SpoutName, StringComparison.Ordinal))
                RestartSpout();
            return;
        }

        _spout.Start(_preview, SpoutOpts());
        UpdateStatusText();
    }

    public void StopSpout()
    {
        _spout.Stop();
        UpdateStatusText();
    }

    /// <summary>按当前 settings 重建管线（分辨率/帧率/名称/透明底改动由此生效）。</summary>
    public void RestartSpout()
    {
        if (!_spout.IsRunning)
        {
            if (_settings.SpoutEnabled) StartSpout();
            return;
        }

        Log.Info($"[MainWindow] 重启 Spout 管线：{_settings.SpoutName} {_settings.Width}×{_settings.Height}@{_settings.RenderFps}"
                 + ($", 字幕通道{(_settings.SpoutLyricsEnabled ? "开" : "关")}"));
        _spout.Restart(_preview, SpoutOpts());
        UpdateStatusText();
    }

    /// <summary>顶栏 Spout 开关（即时启停并持久化）。</summary>
    public void SetSpoutEnabled(bool on)
    {
        if (on == _settings.SpoutEnabled && (on == _spout.IsRunning || !IsLoaded)) return;
        _settings.SpoutEnabled = on;
        _settings.Save();
        if (on) StartSpout();
        else StopSpout();
    }

    /// <summary>设置页「重启渲染引擎」（/api/app/render/restart）的落点。</summary>
    public void RestartRender()
    {
        if (_settings.SpoutEnabled) RestartSpout();
        else StopSpout();
    }

    // ---------------- 键盘 ----------------

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        // 空格 = 播放/暂停；歌词编辑框里打空格要照常输入
        if (e.Key == Key.Space && Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase)
        {
            _preview.TogglePlay();
            e.Handled = true;
        }
    }

    // ---------------- 窗口 chrome ----------------

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

    protected override void OnClosed(EventArgs e)
    {
        // 先停后台渲染泵再释放：渲染线程还持着面/锁
        _renderRunning = false;
        try { _renderThread?.Join(800); } catch { /* 线程已退出 */ }
        _uiProbe.Stop();
        // 停泵释放：发送端、D3D 纹理/设备；渲染器先于 Skia 面释放（缓存快照引用着面）
        _spout.Dispose();
        _preview.Dispose();
        foreach (var s in _stageGraveyard) s.Dispose();
        _renderSurface?.Dispose();
        _renderBmp?.Dispose();
        _displayBuf[0]?.Dispose();
        _displayBuf[1]?.Dispose();
        base.OnClosed(e);
    }
}
