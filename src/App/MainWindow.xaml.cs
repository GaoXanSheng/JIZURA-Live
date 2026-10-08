using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using JizuraLive.App.Cloud;
using JizuraLive.App.I18n;
using JizuraLive.Engine;
using JizuraLive.Logging;
using JizuraLive.Spout;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace JizuraLive.App;

public partial class MainWindow : Window
{
    private readonly Settings _settings;
    private readonly PreviewEngine _preview = new();
    private readonly SpoutOut _spout = new();

    /// <summary>预览镜像：Spout 泵渲好的 GPU 帧经 D3D11 staging 读回供预览直接贴显示
    /// （预览不再 CPU 重渲同一内容——两者共用引擎闸会让双方都掉出帧预算，见 PreviewMirror 注释）。</summary>
    private readonly PreviewMirror _mirror = new();

    /// <summary>编辑器单一事实源（面板经 App.Main.State 访问）。</summary>
    public EditorState State { get; }

    /// <summary>SMTC 跟随外部播放器（M6；云音乐面板与 TopBar 的「跟随」开关都驱动它）。</summary>
    public FollowController Follow { get; }

    /// <summary>导出进行中：预览渲染泵让路（编码线程优先吃引擎闸）。</summary>
    public volatile bool ExportBusy;
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
    private readonly SKSurface?[] _displaySurf = new SKSurface?[2]; // 镜像模式的缩放目标（与显示缓冲同底）
    private int _displayW = -1, _displayH = -1;
    private volatile int _displayFront;
    private readonly object _displayLock = new();
    // 镜像模式：已贴出的镜像帧序号与时间戳（判断「有没有新帧」；超时无新帧回退 CPU 渲染）
    private long _mirrorSeq;
    private long _mirrorAt;
    private long _mirrorWarnAt;
    private long _fallbackAt; // 兜底 CPU 渲染上次执行时刻（限速用，见镜像分支注释）
    private readonly SKPaint _mirrorPaint = new() { IsAntialias = false };
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
        Follow = new FollowController(Dispatcher, State);
        // 预览音量/消音：上次退出时的档位（对位旧前端 jizura.previewVolume 的记忆恢复）
        _preview.SetVolume(settings.PreviewVolume, settings.PreviewMuted);

        // 最大化 ↔ 窗口化按钮的图标/提示跟随窗口状态
        StateChanged += (_, _) => UpdateMaxButton();

        var fps = Math.Clamp(settings.RenderFps, 12, 60);
        timeBeginPeriod(1); // 泵节奏要 1ms 粒度（默认 15.6ms 会让「等下一拍」超睡 → 掉到 ~1/2 帧率）
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
                if (plan == null || !_renderRunning || ExportBusy) continue;
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
                // 注意：_stageWpx/H 是 int，除法必须转 double——整数除法会让 756/1920=0，
                // fit=0 → 渲染面退化成 1×1 像素（预览「什么都不渲染」的元凶）
                var fit = _stageWpx > 1 && _stageHpx > 1
                    ? Math.Min(Math.Min(_stageWpx / (double)plan.W, _stageHpx / (double)plan.H), 1.0)
                    : 1.0;
                // 预览长边封顶 1100px：预览是给人看的，1080p 全尺寸光栅的成本/分配
                // 是 GC 压力与卡顿的大头（Spout 输出通道不受此限制）
                fit = Math.Min(fit, 1100.0 / Math.Max(plan.W, plan.H));
                var rw = Math.Max(1, (int)Math.Round(plan.W * fit));
                var rh = Math.Max(1, (int)Math.Round(plan.H * fit));
                // 预览镜像：Spout 主通道走 GPU 时，画面直接消费它交付的帧——预览不再 CPU 重渲。
                // （两者共用 EngineRenderGate 会串行超出帧预算：预览 ~15-25ms + Spout ~4.7ms > 16.7ms，
                // 输出与预览同时掉到 22-45fps、UI 被挤到秒级抖动并灌 GC；镜像路径不消费引擎闸。）
                var mirrorLive = _spout.IsRunning && _spout.MirrorActive;
                _mirror.Request(mirrorLive ? rw : 0, mirrorLive ? rh : 0);
                if (mirrorLive)
                {
                    var seq = _mirror.Seq;
                    if (seq != _mirrorSeq)
                    {
                        _mirrorSeq = seq;
                        _mirrorAt = Stopwatch.GetTimestamp();
                        BlitMirrorToDisplay(rw, rh);
                        Dispatcher.BeginInvoke(() => Stage.InvalidateVisual(), DispatcherPriority.Render);
                        continue;
                    }

                    // 首帧还没到（Spout 刚启动/重建）：CPU 路径继续画，不算停滞
                    if (_mirrorSeq != 0)
                    {
                        // 暂停态静止画面本就该停更；播放中长时间无新帧 = Spout 泵停滞（设备失效等）
                        // → 落到下面的 CPU 渲染兜底（并按 5s 节流留痕，绝不逐帧刷日志）。
                        // 判定窗 1200ms：镜像帧正常 ≤30ms 到；gen2 GC（堆 200MB+/LOH 60MB）的
                        // stop-the-world 停顿实测 0.8-4.3s 会让泵整体静默，窗口太短会把它误判成设备失效。
                        var starveMs = (Stopwatch.GetTimestamp() - _mirrorAt) * 1000.0 / Stopwatch.Frequency;
                        // 重规划在途：新 plan 1–2 秒就到，此时**不做** CPU 兜底——兜底首帧是冷缓存
                        // （实测 0.7–1.8 秒/帧且持引擎闸），会把重规划与 Spout 泵一起拖住形成回环。
                        // 保持旧镜像帧，等新 plan 落地后泵自然续帧。
                        if (starveMs >= 1200 && _preview.ReplanPending) continue;
                        if (_preview.IsPlaying && starveMs >= 1200)
                        {
                            // 兜底限速：CPU 渲染（15-25ms、~350KB/帧）与 Spout 泵抢同一把 EngineRenderGate，
                            // 逐帧兜底会形成「越卡越渲、越渲越卡」的回环（实测泵 2.6s 不产帧 + UI 抖动 2560ms）。
                            // 限到 ~8fps：画面继续动，但不与泵争闸/灌 GC。泵真死时 IsRunning=false，本分支不会进。
                            if ((Stopwatch.GetTimestamp() - _fallbackAt) * 1000.0 / Stopwatch.Frequency < 120) continue;
                            _fallbackAt = Stopwatch.GetTimestamp();
                            if ((Stopwatch.GetTimestamp() - _mirrorWarnAt) * 1000.0 / Stopwatch.Frequency > 5000)
                            {
                                _mirrorWarnAt = Stopwatch.GetTimestamp();
                                Log.Warn($"[预览镜像] {starveMs:F0}ms 无新帧，本帧回退 CPU 渲染兜底（限速 8fps；Spout 泵停滞或 GC 停顿）");
                            }
                        }
                        else
                        {
                            continue;
                        }
                    }
                }
                // 持久渲染面 P：尺寸不符才换（换前清空整帧缓存，安全释放旧面与位图）
                if (_renderSurface == null || _renderW != rw || _renderH != rh)
                {
                    lock (EngineRenderGate.Lock)
                        _preview.Renderer.ClearFrameCache();
                    _renderSurface?.Dispose();
                    _renderBmp?.Dispose();
                    _renderBmp = new SKBitmap(rw, rh, SKColorType.Bgra8888, SKAlphaType.Premul);
                    _renderSurface = SKSurface.Create(_renderBmp.Info, _renderBmp.GetPixels(), _renderBmp.RowBytes);
                    _renderW = rw;
                    _renderH = rh;
                }
                var surf = _renderSurface;
                var tCpu = Stopwatch.GetTimestamp();
                lock (EngineRenderGate.Lock)
                    _preview.Renderer.Frame(surf.Canvas, plan, t, new RenderOpts { Scale = fit, Stage = true });
                var cpuMs = (Stopwatch.GetTimestamp() - tCpu) * 1000.0 / Stopwatch.Frequency;
                // P → 显示双缓冲 memcpy（~1-3ms），翻面后 UI 读新正面（与 memcpy 目标不同面）
                lock (_displayLock)
                {
                    EnsureDisplayLocked(rw, rh);
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
                // 慢帧分段（性能专项）：兜底 CPU 渲染是主因候选——首帧冷启动（新 plan 的排版/字体/
                // shader 一次成型）实测可达秒级，且它持引擎闸会连带拖住 Spout 泵。
                if (cpuMs > 200)
                    Log.Info($"[perf] 预览兜底慢帧：CPU 渲染 {cpuMs:F0}ms（无新帧 {(Stopwatch.GetTimestamp() - _mirrorAt) * 1000.0 / Stopwatch.Frequency:F0}ms）");
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

    // ---------------- 镜像/显示缓冲帮手 ----------------

    /// <summary>显示双缓冲与缩放面按尺寸就绪（调用方须已持 _displayLock；尺寸不符才重建）。</summary>
    private void EnsureDisplayLocked(int rw, int rh)
    {
        if (_displayBuf[0] != null && _displayW == rw && _displayH == rh) return;
        _displayBuf[0]?.Dispose(); // 显示缓冲不喂引擎，可安全换新
        _displayBuf[1]?.Dispose();
        foreach (var s in _displaySurf) s?.Dispose();
        _displayBuf[0] = new SKBitmap(rw, rh, SKColorType.Bgra8888, SKAlphaType.Premul);
        _displayBuf[1] = new SKBitmap(rw, rh, SKColorType.Bgra8888, SKAlphaType.Premul);
        _displaySurf[0] = null;
        _displaySurf[1] = null;
        _displayW = rw;
        _displayH = rh;
        _displayFront = 0;
    }

    /// <summary>把镜像帧（Spout 主通道的计划区）缩放贴进显示背缓冲并翻面。
    /// 缩放走后缓冲自己的面（持久复用），成本 ~4ms 且不占引擎闸——这正是镜像要省掉的那次「CPU 重渲」。</summary>
    private void BlitMirrorToDisplay(int rw, int rh)
    {
        lock (_displayLock)
        {
            EnsureDisplayLocked(rw, rh);
            var back = 1 - _displayFront;
            var buf = _displayBuf[back]!;
            var sx = _displaySurf[back] ??= SKSurface.Create(buf.Info, buf.GetPixels(), buf.RowBytes)
                ?? throw new InvalidOperationException("镜像缩放面创建失败");
            _mirror.WithLatest(src =>
            {
                var px = src.PeekPixels();
                if (px == null) return;
                using var img = SKImage.FromPixels(px.Info, px.GetPixels(), px.RowBytes);
                var c = sx.Canvas;
                c.SetMatrix(SKMatrix.Identity);
                c.Clear(new SKColor(15, 15, 18)); // 透明底工程（SpoutTransparent）在预览里以深灰承托
                c.DrawImage(img, new SKRect(0, 0, src.Width, src.Height), new SKRect(0, 0, rw, rh),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), _mirrorPaint);
                c.Flush();
            });
            _displayFront = back;
        }
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

        _spout.Preview = _mirror; // 预览镜像：Spout 泵写、预览泵读（见 PreviewMirror）
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
        _spout.Preview = _mirror;
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

    /// <summary>打开右栏「特效」页的音频特效区（Transport 的「特效」按钮 → RightPanel.OpenAudioFx）。</summary>
    public void OpenAudioFx() => Right.OpenAudioFx();

    // ---------------- 键盘 ----------------

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        // 空格 = 播放/暂停；歌词编辑框里打空格要照常输入
        if (e.Key == Key.Space && Keyboard.FocusedElement is not TextBoxBase)
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

    // 最大化态显示「窗口化」图标（还原成两个叠方块），普通态显示单方块
    private void UpdateMaxButton()
    {
        var max = WindowState == WindowState.Maximized;
        IcMax.Data = Geometry.Parse(max
            ? "M 0 2.6 H 7.4 V 10 H 0 Z M 2.6 2.6 V 0 H 10 V 7.4"
            : "M 0 0 H 10 V 10 H 0 Z");
        BtnMax.ToolTip = max ? L.T("Main.Restore.Tip") : L.T("Main.Max.Tip");
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

    // 定时器精度：Windows 默认调度粒度 15.6ms，泵的「Thread.Sleep(1) 等下一拍」会超睡一整拍
    // （实测：Spout 泵 60fps 目标掉到 32-43fps，随睡眠落点抽奖）。渲染泵活着期间把粒度提到 1ms。
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        Follow.Stop(); // 跟随循环与引擎时钟归位（FollowEnd 冻结时间轴）
        // 先停后台渲染泵再释放：渲染线程还持着面/锁
        _renderRunning = false;
        try { _renderThread?.Join(800); } catch { /* 线程已退出 */ }
        _uiProbe.Stop();
        // 停泵释放：发送端、D3D 纹理/设备；渲染器先于 Skia 面释放（缓存快照引用着面）
        _spout.Preview = null;
        _spout.Dispose();
        _preview.Dispose();
        _renderSurface?.Dispose();
        _renderBmp?.Dispose();
        _displayBuf[0]?.Dispose();
        _displayBuf[1]?.Dispose();
        foreach (var s in _displaySurf) s?.Dispose();
        timeEndPeriod(1);
        base.OnClosed(e);
    }
}
