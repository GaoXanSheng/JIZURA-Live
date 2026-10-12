using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using JizuraLive.App.Cloud;
using JizuraLive.App.I18n;
using JizuraLive.Engine;
using JizuraLive.Logging;
using JizuraLive.Server;
using JizuraLive.Spout;
using SkiaSharp;

namespace JizuraLive.App;

public partial class MainWindow : Window
{
    private readonly Settings _settings;
    private readonly PreviewEngine _preview = new();
    private readonly SpoutOut _spout = new();
    private readonly CloudPlayService _cloud = new();
    private readonly CloudPlayerSession _player;

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

    /// <summary>云试听播放会话（队列 + 独立播放链；面板与底部常驻条都操作它）。</summary>
    public CloudPlayService Cloud => _cloud;

    /// <summary>播放器模式会话（PV 跟着云歌单渲染；进模式快照工程、退出还原）。</summary>
    public CloudPlayerSession Player => _player;

    /// <summary>当前是不是播放器模式（两套布局的开关）。</summary>
    public bool PlayerMode { get; private set; }

    // 引擎渲染挪到专用后台线程（乒乓双面）：M2b/c 烟雾实测「koma=12 真渲染 ~24 次/s 每次 ~40ms」
    // ——Render 优先级 DispatcherTimer 把它整个压在 UI 线程 ≈96% 饱和，点击事件永远排不上队
    //（表象：全部按钮不可用 + 全局卡顿）。后台线程渲染，UI 线程只做 1-2ms blit
    private Thread? _renderThread;
    private volatile bool _renderRunning;
    // 目标帧率：double 不能标 volatile，存 int FPS 由泵线程每拍换算毫秒
    private volatile int _targetFps = 60;
    // 持久渲染面 P：引擎跨帧依赖同一块面（整帧缓存 + 增量绘制），每帧必须画同一块——
    // 交替画两块面会把跨帧状态劈成两半（实测：画面持续闪烁）。P 绝不 Dispose（缓存规矩），
    // 换尺寸的旧面进 graveyard
    private SKBitmap? _renderBmp;
    private SKSurface? _renderSurface;
    private int _renderW = -1, _renderH = -1;
    // 显示双缓冲：泵线程写 back 再翻面（锁只护 1-3ms 的 memcpy/缩放），UI 线程按脏标记把正面
    // 拷进舞台位图。两侧都不碰对方的缓冲，没有「UI 等泵」或「泵等 UI」的长临界区
    private readonly SKBitmap?[] _displayBuf = new SKBitmap[2];
    private readonly SKSurface?[] _displaySurf = new SKSurface?[2]; // 镜像模式的缩放目标（与显示缓冲同底）
    private int _displayW = -1, _displayH = -1;
    private volatile int _displayFront;
    private long _displaySeq;      // 泵侧：每翻一次面 +1
    private long _presentedSeq;    // UI 侧：已贴到舞台位图的序号
    private int _presentPending;   // 失效合并：已排队一次 UI 拷贝就不再排（UI 忙时自然丢中间帧）
    private readonly object _displayLock = new();
    // 舞台呈现：WriteableBitmap（CPU 回退分支）或 D3D11ImageSource（纯 GPU 直显分支）
    private WriteableBitmap? _stageBmp;
    private int _stageBmpW = -1, _stageBmpH = -1;
    private D3D11ImageSource? _d3dStage;
    // GPU 直显的共享纹理打不开（D3D9Ex 不认该格式/设备失效）：置位后预览泵改走 CPU 渲染，
    // 否则 Stage.Source 停在没绑后缓冲的 D3DImage 上就是一块黑屏
    private volatile bool _gpuPresentFailed;
    // GPU 直显分支的字幕叠加层：显存直显没有 SKCanvas 可画，字幕另画进这张透明底位图
    // （与 Stage 同格同 Stretch=Uniform，缩放一致即像素对齐）
    private WriteableBitmap? _stageOverlayBmp;
    private int _stageOverlayW = -1, _stageOverlayH = -1;
    // 窗口不可见（最小化/隐藏）时不出帧：预览泵整个空转，镜像读回也一并停掉（见 RenderPump）
    private volatile bool _stageShown = true;
    // 镜像模式：已贴出的镜像帧序号与时间戳（判断「有没有新帧」；超时无新帧回退 CPU 渲染）
    private long _mirrorSeq;
    private long _mirrorAt;
    private long _mirrorWarnAt;
    private long _fallbackAt; // 兜底 CPU 渲染上次执行时刻（限速用，见镜像分支注释）
    // 播放态 0→1 时把镜像计时/兜底起点重新起算：暂停期间 Spout 泵只保活不产新帧，计时若跨过
    // 暂停就会把「刚恢复播放」误判成断供（实测一条 422715ms：暂停 7 分钟后首拍即落 CPU 兜底）
    private bool _wasPlaying;
    private long _playStartedAt;
    private long _cpuFallbackCount; // 本 5s 窗口 CPU 兜底次数（进 [预览泵] 日志）
    // CPU 直渲路径的慢帧降质（对位前端 draw() 的 S.slow：帧间隔 >30ms 进、<14ms 持续 1.2s 出；
    // 慢态下出帧走 fast/noGhost，掉质量保帧率）。镜像模式不渲染引擎，这套滞回不参与。
    private bool _cpuSlow;
    private long _cpuSlowAt;
    private long _cpuRenderPrevTs;
    private readonly SKPaint _mirrorPaint = new() { IsAntialias = false };
    // 舞台尺寸缓存（UI SizeChanged 写，渲染线程读——每帧 Dispatcher.Invoke 会白烧调度器）
    private volatile int _stageWpx, _stageHpx;
    // 留白指引框的显示几何（当前帧的纹理尺寸 + 画幅区位置，设备像素；UI 线程写读）
    private int _guideTexW, _guideTexH, _guidePlanX, _guidePlanY, _guidePlanW, _guidePlanH;
    // 脏检查：暂停且 t/plan 没变就跳过重绘（暂停态省 ~96% CPU）
    private double _lastRT = -1;
    private long _lastRV = -1;
    private long _lastRSub = -1;
    private Plan? _lastRPlan;
    private int _lastRW = -1;   // 上次出帧的预览渲染尺寸（尺寸变化也要重出：暂停时切布局/改窗口大小）
    private int _lastRH = -1;

    // 字幕叠加层缓存（只在渲染泵线程访问）：字幕版本或 plan 变了就重建——注音行来自 plan 的
    // cut 注音（SubtitleLayer.NotesFromPlan），plan 换对象（重规划）也要跟着换
    private SubtitleLayer? _subLayer;
    private long _subLayerVer = -1;
    private Plan? _subLayerPlan;
    // UI 线程健康探针：Dispatcher tick 调度抖动 >33ms 即按钮可感知卡顿，5s 汇总进日志
    private readonly DispatcherTimer _uiProbe;
    private long _probeDue, _probeWindowEnd;
    private double _probeJitterMax;
    // 预览泵实际出帧数（5s 窗口汇总进日志）：设置页「渲染帧率」到底有没有生效，看这条对数
    private int _pumpErrors;
    private long _pumpFrames;
    private long _pumpFramesPrev;

    public MainWindow(Settings settings)
    {
        InitializeComponent();

        _settings = settings;
        App.MainInstance = this;
        // 字幕层样式：上次退出时的档位（对位旧前端 localStorage 的 jizura.kgsub 记忆恢复；
        // 灌的是同一份共享实例——预览叠加/导出烤入/Spout 字幕通道都读它）
        _preview.SubtitleCfg.CopyFrom(settings.Subtitle);
        State = new EditorState(_preview);
        State.StateChanged += () => Dispatcher.Invoke(UpdateStatusText);
        Follow = new FollowController(Dispatcher, State);
        // 预览音量/消音：上次退出时的档位（对位旧前端 jizura.previewVolume 的记忆恢复）
        _preview.SetVolume(settings.PreviewVolume, settings.PreviewMuted);

        // 最大化 ↔ 窗口化按钮的图标/提示跟随窗口状态；窗口状态变化也刷新可见性缓存
        StateChanged += (_, _) =>
        {
            UpdateMaxButton();
            _stageShown = Stage.IsVisible;
        };

        _targetFps = Math.Clamp(settings.RenderFps, 12, 240);
        timeBeginPeriod(1); // 泵节奏要 1ms 粒度（默认 15.6ms 会让「等下一拍」超睡 → 掉到 ~1/2 帧率）
        _renderRunning = true;
        _renderThread = new Thread(RenderPump)
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
            UpdateCenterGuide(); // 舞台缩放 → 留白指引框跟着重定位
        };
        // 可见性缓存（同理由：渲染线程每帧读，不碰 Dispatcher）：最小化/隐藏时预览泵整个停帧
        Stage.IsVisibleChanged += (_, _) => _stageShown = Stage.IsVisible;

        // UI 线程健康探针：Background 优先级 tick 的调度抖动直接量化"卡顿"，5s 汇总进日志
        _probeDue = _probeWindowEnd = Stopwatch.GetTimestamp();
        _uiProbe = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _uiProbe.Tick += (_, _) =>
        {
            UiStallProbe.Heartbeat(); // 卡顿取证探针的心跳（缺口超阈值时看门狗逐秒报「卡在哪」）
            var now = Stopwatch.GetTimestamp();
            var late = (now - _probeDue) * 1000.0 / Stopwatch.Frequency;
            if (late > _probeJitterMax) _probeJitterMax = late;
            _probeDue = now + Stopwatch.Frequency / 10;
            if (now >= _probeWindowEnd)
            {
                if (_probeJitterMax > 33)
                {
                    // 秒级卡顿附 GC 证据：Gen2 计数与上一次报告的差值非零 = 卡顿主因是阻塞式 GC，
                    // 而不是 UI 线程上的代码（文件 IO/D3D/引擎同步段）。对比相邻两行即可定位。
                    var gc = _probeJitterMax > 2000
                        ? $"｜GC Gen0={GC.CollectionCount(0)} Gen1={GC.CollectionCount(1)} Gen2={GC.CollectionCount(2)}"
                        : "";
                    Log.Warn($"[UI探针] 5s 最大调度抖动 {_probeJitterMax:F0}ms（>33ms 即按钮可感知卡顿）{gc}");
                }
                _probeJitterMax = 0;
                var frames = Interlocked.Read(ref _pumpFrames);
                var got = frames - _pumpFramesPrev;
                _pumpFramesPrev = frames;
                if (got > 0 && _preview.IsPlaying)
                {
                    // 上面的 got 只是泵拍数（InvalidateStage 次数），不是引擎渲染数：镜像模式下预览
                    // 直接消费 Spout 交付的帧，引擎侧调用应为 0。要判断「渲染是否跟得上」看后半段的
                    // 真渲染次数/耗时（CPU 直渲模式下才有意义）。
                    var es = _preview.Renderer.TakeWindowStats();
                    Log.Info($"[预览泵] 近 5s {got} 拍（{got / 5.0:F0}fps，目标 {_targetFps}fps，"
                             + $"{(_settings.SpoutEnabled ? "Spout 镜像" : "CPU 直渲")}）"
                             + $"｜引擎调用 {es.Calls} 次（真渲染 {es.Real}、缓存 {es.Cached}，"
                             + $"真渲染平均 {(es.Real > 0 ? es.RealSumMs / es.Real : 0):F1}ms、峰值 {es.MaxMs:F1}ms）"
                             + (_cpuFallbackCount > 0 ? $"；本窗 CPU 兜底 {_cpuFallbackCount} 次" : ""));
                    _cpuFallbackCount = 0;
                }
                _probeWindowEnd = now + 5 * Stopwatch.Frequency;
            }
        };
        _player = new CloudPlayerSession(State, _cloud);
        _player.Changed += RefreshPlayerOverlay;
        _cloud.Changed += RefreshPlayerOverlay;

        _uiProbe.Start();
        UiStallProbe.Start();

        UpdateStatusText();
        // 默认打开为暂停状态（暂停态泵出第一帧后空转，不抢占 CPU/GPU；用户主动点播放或按空格才开跑）

        Loaded += (_, _) =>
        {
            // Spout 启动放 Loaded：管线建 D3D 设备要几百毫秒，别挡窗口先画出来
            if (_settings.SpoutEnabled)
                StartSpout();
            // 上次播放器模式异常退出：把用户工程从会话快照里还原回来（autosave 里是云歌工程）
            if (_player.RecoverLeftoverIfAny()) RefreshPlayerOverlay();
            // 后台自愈清理临时文件、重复音频与超期日志
            StorageCleaner.AutoCleanupAsync(State.AudioPath);
        };
    }

    // ---------------- 引擎预览舞台 ----------------

    /// <summary>
    /// UI 线程：把显示缓冲的正面拷进舞台位图（同尺寸 1:1 memcpy，约 0.3-0.5ms），
    /// 缩放/letterbox 交给 WPF 的 GPU 合成器（Image 的 Stretch=Uniform）。
    /// 由渲染泵按「失效合并」规则排队调用——UI 忙的时候中间帧自然丢掉，只贴最新一帧，
    /// 不会像原来的 DispatcherPriority.Render 那样每帧刷一次失效。
    /// </summary>
    private unsafe void PresentStage()
    {
        Interlocked.Exchange(ref _presentPending, 0);
        var frameW = 0;
        var frameH = 0;
        lock (_displayLock)
        {
            var seq = _displaySeq;
            if (seq == _presentedSeq) return;
            var buf = _displayBuf[_displayFront];
            if (buf == null) return;
            var w = buf.Width;
            var h = buf.Height;
            var bmp = EnsureStageBitmap(w, h);
            if (bmp == null) return;
            var src = (byte*)buf.GetPixels();
            if (src == null) return;
            var srcStride = buf.RowBytes;
            bmp.Lock();
            try
            {
                var dst = (byte*)bmp.BackBuffer;
                var dstStride = bmp.BackBufferStride;
                if (dstStride == srcStride)
                {
                    Buffer.MemoryCopy(src, dst, (long)dstStride * h, (long)srcStride * h);
                }
                else
                {
                    for (var y = 0; y < h; y++)
                        Buffer.MemoryCopy(src + (long)y * srcStride, dst + (long)y * dstStride, dstStride, srcStride);
                }

                bmp.AddDirtyRect(new Int32Rect(0, 0, w, h));
            }
            finally
            {
                bmp.Unlock();
            }

            _presentedSeq = seq;
            frameW = w;
            frameH = h;
        }
        // CPU 帧的字幕已烤进舞台位图：清掉 GPU 直显分支留下的覆盖层（模式切换/镜像兜底时），
        // 否则会与位图里的字幕重影
        if (StageOverlay.Source != null) StageOverlay.Source = null;
        // 这一帧整幅就是画幅（无出边 letterbox）：留白指引框按它算
        SetGuideGeometry(frameW, frameH, 0, 0, frameW, frameH);
        // AddDirtyRect 已通知 WPF D3D 合成管道刷新硬件纹理，无需再调用 InvalidateVisual 触发整树重复测量
    }

    // ---------------- 留白指引框（编辑器预览专用，不入 Spout 与导出） ----------------

    /// <summary>当前预览帧的显示几何：纹理尺寸 + 画幅区（plan 区域）在纹理里的位置（设备像素）。
    /// CPU/镜像路径整幅就是画幅（0,0,w,h）；GPU 直显时纹理是全幅、画幅是居中一块。</summary>
    private void SetGuideGeometry(int texW, int texH, int planX, int planY, int planW, int planH)
    {
        _guideTexW = texW;
        _guideTexH = texH;
        _guidePlanX = planX;
        _guidePlanY = planY;
        _guidePlanW = planW;
        _guidePlanH = planH;
        UpdateCenterGuide();
    }

    /// <summary>
    /// 把留白指引框摆到画幅里留空的那块（只在 UI 线程调用）。
    /// 位置按 Stage 的 Stretch=Uniform 同一套 contain 计算反推：先把纹理贴进舞台格，再在纹理里定位画幅区，
    /// 最后把构图坐标的留空区缩放到画幅区上。与帧内容无关，所以 CPU/GPU 两条呈现路径都成立。
    /// </summary>
    private void UpdateCenterGuide()
    {
        var plan = _preview.Plan;
        var free = plan is { CenterFree: true } ? Planner.FreeZone(plan) : null;
        // Stage 自己的 ActualWidth/Height 是「按 Uniform 缩放后的内容尺寸」，不是布局格尺寸
        // （实测 9:16 源在 696x483 的格里报 696x392）——居中留白必须按父容器那一格算
        var host = Stage.Parent as FrameworkElement;
        var cellW = host?.ActualWidth ?? Stage.ActualWidth;
        var cellH = host?.ActualHeight ?? Stage.ActualHeight;
        if (free == null || _guideTexW < 2 || _guideTexH < 2 || cellW < 2 || cellH < 2)
        {
            CenterGuide.Visibility = Visibility.Collapsed;
            return;
        }

        var fit = Math.Min(cellW / _guideTexW, cellH / _guideTexH);
        var originX = (cellW - _guideTexW * fit) / 2 + _guidePlanX * fit;
        var originY = (cellH - _guideTexH * fit) / 2 + _guidePlanY * fit;
        var s = _guidePlanW * fit / plan!.W; // 画幅区像素 → 构图坐标
        var (gx, gy, gw, gh) = free.Value;

        CenterGuide.Width = Math.Max(1, gw * s);
        CenterGuide.Height = Math.Max(1, gh * s);
        CenterGuide.Margin = new Thickness(originX + gx * s, originY + gy * s, 0, 0);
        CenterGuide.Visibility = Visibility.Visible;
    }

    /// <summary>取与帧同尺寸的舞台位图（只在 UI 线程调用；尺寸不符才换新）。
    /// Pbgra32 与 Skia 的 Bgra8888+Premul 同语义同布局（预乘 BGRA，行距 = w×4），可整行直拷。</summary>
    private WriteableBitmap? EnsureStageBitmap(int w, int h)
    {
        if (_stageBmp != null && _stageBmpW == w && _stageBmpH == h && ReferenceEquals(Stage.Source, _stageBmp)) return _stageBmp;
        _stageBmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);
        _stageBmpW = w;
        _stageBmpH = h;
        Stage.Source = _stageBmp;
        return _stageBmp;
    }

    /// <summary>GPU 纯显存模式：D3D11 共享句柄直接映射进 WPF D3DImage 硬件表面。
    /// 句柄来自 Spout 泵的**发布纹理**（已拷完的静止帧），不是引擎正在重画的出图面。
    /// 显存直显没有 SKCanvas 可画，字幕另开一层透明底覆盖层（DrawGpuOverlay）与帧同拍提交。
    /// w/h 是共享纹理尺寸；plan* 是画幅区域（出边时是居中一块），字幕排版以 plan 为坐标系。</summary>
    private void PresentGpuFrame(IntPtr shareHandle, int w, int h, int planX, int planY, int planW, int planH)
    {
        if (Interlocked.Exchange(ref _presentPending, 1) != 0) return;
        // 字幕层与时刻在泵线程取好再交给 UI：到 UI 线程时 CurrentTime 可能已往前走，那样字幕
        // 会比画面快半拍（OverlayLayer 的约定也是只在渲染泵线程调用）
        var layer = OverlayLayer();
        var t = _preview.CurrentTime;
        Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _presentPending, 0);
            if (EnsureGpuStage(w, h, shareHandle))
            {
                DrawGpuOverlay(layer, t, w, h, planX, planY, planW, planH);
                SetGuideGeometry(w, h, planX, planY, planW, planH);
            }
            else
            {
                _gpuPresentFailed = true; // 绑定失败：让预览泵退回 CPU 渲染，别停在黑屏
                if (StageOverlay.Source != null) StageOverlay.Source = null; // 也不留上一帧的残影字幕
            }
            _d3dStage?.Invalidate();
        }, DispatcherPriority.Render);
    }

    private bool EnsureGpuStage(int w, int h, IntPtr shareHandle)
    {
        if (_d3dStage == null)
        {
            _d3dStage = new D3D11ImageSource();
            Stage.Source = _d3dStage;
        }
        else if (!ReferenceEquals(Stage.Source, _d3dStage))
        {
            Stage.Source = _d3dStage;
        }

        return _d3dStage.BindSharedHandle(shareHandle, w, h);
    }

    /// <summary>
    /// GPU 直显分支的字幕叠加层：主画面是显存直显（无 SKCanvas 可画），字幕画进一张与 Stage
    /// 同尺寸、同 Stretch=Uniform 的透明底位图，两层缩放一致即像素对齐。
    /// 字幕排版以 plan 区域为坐标系（与 CPU 叠加/导出烤入同源派生），出边时整体平移到 plan 左上角。
    /// </summary>
    private void DrawGpuOverlay(SubtitleLayer? layer, double t, int w, int h,
        int planX, int planY, int planW, int planH)
    {
        if (layer == null)
        {
            if (StageOverlay.Source != null) StageOverlay.Source = null;
            return;
        }

        if (_stageOverlayBmp == null || _stageOverlayW != w || _stageOverlayH != h)
        {
            _stageOverlayBmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);
            _stageOverlayW = w;
            _stageOverlayH = h;
            StageOverlay.Source = _stageOverlayBmp;
        }

        _stageOverlayBmp.Lock();
        try
        {
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surf = SKSurface.Create(info, _stageOverlayBmp.BackBuffer, _stageOverlayBmp.BackBufferStride);
            if (surf == null)
            {
                Log.Warn($"[预览叠加] SKSurface.Create 失败（{w}x{h}，stride={_stageOverlayBmp.BackBufferStride}）：本帧不出字幕");
                return;
            }
            var c = surf.Canvas;
            c.Clear(SKColors.Transparent);
            c.Save();
            c.Translate(planX, planY);
            layer.Render(c, t, planW, planH);
            c.Restore();
            c.Flush();
            _stageOverlayBmp.AddDirtyRect(new Int32Rect(0, 0, w, h));
        }
        catch (Exception ex)
        {
            // 字幕出错不能拖垮预览（同 CPU 分支）。但必须可见：这条路径此前静默失败过
            Log.Warn("GPU 字幕叠加失败：" + ex.Message);
        }
        finally
        {
            _stageOverlayBmp.Unlock();
        }
    }

    /// <summary>渲染泵：本帧已翻面，请 UI 线程来取（合并失效，绝不逐帧排满 Dispatcher）。</summary>
    private void InvalidateStage()
    {
        if (Interlocked.Exchange(ref _presentPending, 1) != 0) return;
        Dispatcher.BeginInvoke(PresentStage, DispatcherPriority.Render);
    }

    /// <summary>泵线程：把 back 缓冲翻成正面并请求 UI 贴图（调用方须已持 _displayLock）。</summary>
    private void FlipDisplayLocked()
    {
        _displayFront = 1 - _displayFront;
        _displaySeq++;
    }

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

    // ---------------- 后台渲染泵（乒乓双面） ----------------
    // SkiaSharp 3.119+ 规矩不变：交给 Frame 的面绝不 Dispose（整帧缓存持有跨帧快照），
    // 换尺寸的旧面进 graveyard；blit 用 PeekPixels+FromPixels 借像素，不用 Snapshot()

    private void RenderPump()
    {
        var next = Stopwatch.GetTimestamp();
        while (_renderRunning)
        {
            var frameMs = 1000.0 / _targetFps;
            var wakeIn = (next - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency;
            if (wakeIn > 1) Thread.Sleep((int)Math.Min(wakeIn, 50));
            next += (long)(frameMs / 1000.0 * Stopwatch.Frequency);
            if (next < Stopwatch.GetTimestamp()) next = Stopwatch.GetTimestamp(); // 掉帧不追帧
            try
            {
                var plan = _preview.Plan;
                if (plan == null || !_renderRunning || ExportBusy) continue;
                _preview.Tick();
                // 播放态 0→1：镜像「无新帧」计时与兜底起点必须从这里重新起算。暂停期间 Spout 泵
                // 只保活、不产新帧（镜像 Seq 不动），计时跨过暂停就会把「刚恢复播放」误判成断供：
                // 日志实测一条 422715ms（暂停 7 分钟→播放），首拍即落 4fps CPU 兜底并持引擎闸，
                // 反把 Spout 泵堵住（下面注释里那个秒级冻结回环的入口）。
                var playingNow = _preview.IsPlaying;
                if (playingNow && !_wasPlaying)
                {
                    _mirrorAt = Stopwatch.GetTimestamp();
                    _fallbackAt = 0;
                    _playStartedAt = Stopwatch.GetTimestamp();
                    _cpuRenderPrevTs = 0; // 同理由：帧间隔基准不能跨暂停（否则首拍即判慢）
                }
                if (!playingNow)
                {
                    // 暂停帧一律满质量（前端同：!playing → slow=false），并清帧间隔基准：
                    // 留着的话恢复播放的首拍会把「暂停时长」当成帧间隔，立刻误进慢态。
                    _cpuSlow = false;
                    _cpuRenderPrevTs = 0;
                }
                _wasPlaying = playingNow;
                // 窗口不可见（最小化/隐藏）：帧没人看，整条出帧路径停掉——镜像读回（1080p
                // MapSubresource，是个 GPU 同步点）与引擎闸都不该再被占用。Tick 已经跑过，
                // 播放/循环照常推进。必须放在下面的脏检查记账之前：记账被消耗掉的话，
                // 恢复窗口时那一帧会被判成「没变化」而不再重出，预览停在最小化前的旧画面上。
                if (!_stageShown)
                {
                    _mirror.Request(0, 0);
                    continue;
                }
                var t = _preview.CurrentTime;
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

                // 脏检查：暂停且 t/plan/字幕/**预览尺寸**都没变就跳过（暂停态省 ~96% CPU）。
                // 字幕版本覆盖「装载/清空字幕」与「设置页改字幕样式」；
                // 尺寸覆盖「暂停时切编辑器/播放器布局、改窗口大小」——不看尺寸的话那一帧会停在旧尺寸上，
                // 表现就是「切回播放器模式画面定死、不渲染」（镜像帧按旧尺寸贴完就再没人重贴）。
                var ver = _preview.Version;
                var subVer = _preview.SubtitleVersion;
                var subDirty = subVer != _lastRSub;
                var sizeDirty = rw != _lastRW || rh != _lastRH;
                if (!_preview.IsPlaying && t == _lastRT && ver == _lastRV && !subDirty && !sizeDirty
                    && ReferenceEquals(plan, _lastRPlan))
                    continue;
                _lastRT = t;
                _lastRV = ver;
                _lastRSub = subVer;
                _lastRPlan = plan;
                _lastRW = rw;
                _lastRH = rh;
                Interlocked.Increment(ref _pumpFrames);
                // 预览镜像：Spout 主通道走 GPU 时，画面直接消费它交付的帧——预览不再 CPU 重渲。
                // （两者共用 EngineRenderGate 会串行超出帧预算：预览 ~15-25ms + Spout ~4.7ms > 16.7ms，
                // 输出与预览同时掉到 22-45fps、UI 被挤到秒级抖动并灌 GC；镜像路径不消费引擎闸。）
                var mirrorLive = _spout.IsRunning && _spout.MirrorActive && !_gpuPresentFailed;
                _mirror.Request(mirrorLive ? rw : 0, mirrorLive ? rh : 0);
                if (mirrorLive)
                {
                    // 与 Spout 发布同拍：贴之前等一小拍新帧（上限半帧、4~10ms）。不等的话两套节拍器
                    // 错拍时同一帧贴两次、下一帧被跳过——预览看着一顿一顿，而 Spout 输出本身满帧
                    //（OBS 里反而顺）。等到就贴；等不到（暂停/泵停滞）走下面的既有分支。
                    if (_mirror.Seq == _mirrorSeq)
                        _mirror.WaitNewFrame((int)Math.Clamp(frameMs / 2, 4, 10));
                    var seq = _mirror.Seq;
                    if (seq != _mirrorSeq)
                    {
                        _mirrorSeq = seq;
                        _mirrorAt = Stopwatch.GetTimestamp();
                        if (_mirror.TryGetGpuFrame(out var handle, out var gw, out var gh,
                                out var px, out var py, out var pw, out var ph, out _))
                        {
                            PresentGpuFrame(handle, gw, gh, px, py, pw, ph);
                            continue;
                        }
                        BlitMirrorToDisplay(rw, rh);
                        continue;
                    }

                    // 只有字幕变了（装载/改样式）而镜像没有新帧：重贴上一帧 + 重画字幕。主通道不因
                    // 字幕版本重绘（镜像只有新帧才走上面的分支），暂停态靠这里把字幕改动落到预览
                    if (subDirty && _mirrorSeq != 0)
                    {
                        _mirrorAt = Stopwatch.GetTimestamp();
                        if (_mirror.TryGetGpuFrame(out var handle, out var gw, out var gh,
                                out var px, out var py, out var pw, out var ph, out _))
                        {
                            PresentGpuFrame(handle, gw, gh, px, py, pw, ph);
                            continue;
                        }
                        BlitMirrorToDisplay(rw, rh);
                        continue;
                    }

                    // 首帧还没到（Spout 刚启动/重建）：CPU 路径继续画，不算停滞
                    if (_mirrorSeq != 0)
                    {
                        // 暂停态静止画面本就该停更；播放中长时间无新帧 = Spout 泵停滞（设备失效等）
                        // → 落到下面的 CPU 渲染兜底（并按 5s 节流留痕，绝不逐帧刷日志）。
                        // 判定窗 4000ms：镜像帧正常 ≤30ms 到，但兜底本身是「自伤」——它持引擎闸整帧，
                        // 正好把 Spout 泵堵住，堵住又让镜像继续断供，形成秒级冻结回环（实测 1200ms 窗
                        // 时反复触发：泵 2.6s 不产帧 + UI 抖动 2560ms）。窗拉长 + 限速 4fps 后只在
                        // 泵真死（设备失效/线程卡死）时兜底，正常 GC 停顿（<2s）不再触发。
                        var starveMs = (Stopwatch.GetTimestamp() - _mirrorAt) * 1000.0 / Stopwatch.Frequency;
                        // 重规划在途：新 plan 1–2 秒就到，此时**不做** CPU 兜底——兜底首帧是冷缓存
                        // （实测 0.7–1.8 秒/帧且持引擎闸），会把重规划与 Spout 泵一起拖住形成回环。
                        // 保持旧镜像帧，等新 plan 落地后泵自然续帧。
                        if (starveMs >= 4000 && _preview.ReplanPending) continue;
                        // 恢复播放后的头 4s 不给兜底：泵刚起步还在追拍，此刻兜底只会自伤（见上）
                        var playMs = (Stopwatch.GetTimestamp() - _playStartedAt) * 1000.0 / Stopwatch.Frequency;
                        if (_preview.IsPlaying && starveMs >= 4000 && playMs >= 4000)
                        {
                            if ((Stopwatch.GetTimestamp() - _fallbackAt) * 1000.0 / Stopwatch.Frequency < 250) continue;
                            _fallbackAt = Stopwatch.GetTimestamp();
                            _cpuFallbackCount++;
                            if ((Stopwatch.GetTimestamp() - _mirrorWarnAt) * 1000.0 / Stopwatch.Frequency > 5000)
                            {
                                _mirrorWarnAt = Stopwatch.GetTimestamp();
                                Log.Warn($"[预览镜像] {starveMs:F0}ms 无新帧，本帧回退 CPU 渲染兜底（限速 4fps；Spout 泵停滞或 GC 停顿）");
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
                // 慢帧滞回（前端 S.slow 对位）：间隔 >30ms 记慢、<14ms 持续 1.2s 才退出。
                // 只在播放中推进——暂停/单帧重绘不参与（暂停帧必须满质量）。
                var cpuNow = Stopwatch.GetTimestamp();
                if (_preview.IsPlaying && _cpuRenderPrevTs != 0)
                {
                    var gapMs = (cpuNow - _cpuRenderPrevTs) * 1000.0 / Stopwatch.Frequency;
                    if (gapMs > 30)
                    {
                        if (!_cpuSlow) _cpuSlowAt = cpuNow;
                        _cpuSlow = true;
                    }
                    else if (gapMs < 14 && _cpuSlow && (cpuNow - _cpuSlowAt) * 1000.0 / Stopwatch.Frequency > 1200)
                    {
                        _cpuSlow = false;
                    }
                }
                _cpuRenderPrevTs = cpuNow;
                var tCpu = Stopwatch.GetTimestamp();
                lock (EngineRenderGate.Lock)
                {
                    _preview.Renderer.Frame(surf.Canvas, plan, t,
                        new RenderOpts { Scale = fit, Stage = true, Fast = _cpuSlow, NoGhost = _cpuSlow });
                    // 字幕叠加：与导出烤入同语义（只画注音/翻译行，主歌词由引擎画），按设备像素落笔
                    PaintSubtitleOverlay(surf.Canvas, OverlayLayer(), t, rw, rh);
                }
                var cpuMs = (Stopwatch.GetTimestamp() - tCpu) * 1000.0 / Stopwatch.Frequency;
                // P → 显示背缓冲（~1-3ms memcpy）→ 翻面 → 请 UI 线程贴图。UI 侧只做一次同尺寸
                // memcpy，缩放/合成由 WPF 的 GPU 合成器做（原来 SKElement 要在 UI 线程做一次
                // 全幅软件缩放光栅，且每帧刷一次 DispatcherPriority.Render 失效）
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

                    FlipDisplayLocked();
                }

                InvalidateStage();
                // 慢帧分段（性能专项）：兜底 CPU 渲染是主因候选——首帧冷启动（新 plan 的排版/字体/
                // shader 一次成型）实测可达秒级，且它持引擎闸会连带拖住 Spout 泵。
                if (cpuMs > 200)
                    Log.Info($"[perf] 预览兜底慢帧：CPU 渲染 {cpuMs:F0}ms（无新帧 {(Stopwatch.GetTimestamp() - _mirrorAt) * 1000.0 / Stopwatch.Frequency:F0}ms）");
            }
            catch (Exception ex)
            {
                // 首次用 Warn：这里的异常会让整帧（含字幕叠加）静默消失，Debug 级等于看不见
                if (Interlocked.Increment(ref _pumpErrors) <= 3) Log.Warn("预览渲染泵异常：" + ex);
                else Log.Debug("预览渲染泵异常：" + ex.Message);
            }
        }
    }

    private void UpdateStatusText()
    {
        StatusChanged?.Invoke();
    }

    // ---------------- 镜像/显示缓冲帮手 ----------------

    /// <summary>把镜像帧（Spout 主通道的计划区）缩放贴进显示背缓冲并翻面。
    /// 缩放走后缓冲自己的面（持久复用），成本 ~4ms 且不占引擎闸——这正是镜像要省掉的那次「CPU 重渲」。
    /// 细粒度临界区：仅在分配与翻面时短暂持 _displayLock，耗时的 CPU 图像缩放、字幕叠加与引导线绘制在锁外进行，
    /// 彻底消除 UI 线程在 PresentStage 取帧时的秒级/毫秒级锁竞争。</summary>
    private void BlitMirrorToDisplay(int rw, int rh)
    {
        SKSurface sx;
        lock (_displayLock)
        {
            EnsureDisplayLocked(rw, rh);
            var back = 1 - _displayFront;
            var buf = _displayBuf[back]!;
            sx = _displaySurf[back] ??= SKSurface.Create(buf.Info, buf.GetPixels(), buf.RowBytes)
                ?? throw new InvalidOperationException("镜像缩放面创建失败");
        }

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
        // 字幕叠加：镜像帧来自 Spout 主通道（不含字幕，字幕走独立通道），预览侧叠注音/翻译行
        PaintSubtitleOverlay(sx.Canvas, OverlayLayer(), _preview.CurrentTime, rw, rh);

        lock (_displayLock)
        {
            FlipDisplayLocked();
        }

        InvalidateStage();
    }

    /// <summary>
    /// 当前字幕叠加层（null = 不叠：内容=off 或完全无注音内容）。只在渲染泵线程调用。
    /// 兼容性保障：优先使用酷狗 KRC 逐字数据；若未载入 KRC（如网易云 LRC、手动粘贴或重启恢复工程），
    /// 则自动从 plan.Lines 提炼基础行（对位 Vue 无论何种歌词源均从 plan.lines 获取 note 进行渲染的契约）。
    /// </summary>
    private SubtitleLayer? OverlayLayer()
    {
        if (_preview.SubtitleCfg.Content == SubContent.Off)
        {
            _subLayer = null;
            return null;
        }

        var plan = _preview.Plan;
        if (plan == null)
        {
            return null;
        }

        var lines = _preview.Subtitles;
        IReadOnlyList<string>? notes;
        if (lines is { Count: > 0 })
        {
            notes = SubtitleLayer.NotesFromPlan(lines, plan);
        }
        else
        {
            // 没有 KRC 逐字数据（网易云 LRC / 手动粘贴 / 重启恢复工程）：从 plan 行合成。
            // 与 Spout 字幕通道、导出烤入共用 SubtitleLayer.FromPlan，三处行为一致。
            var synth = SubtitleLayer.FromPlan(plan);
            if (synth == null)
            {
                _subLayer = null;
                return null;
            }

            lines = synth.Value.Lines;
            notes = synth.Value.Notes;
        }

        var ver = _preview.SubtitleVersion;
        if (_subLayer == null || _subLayerVer != ver || !ReferenceEquals(_subLayerPlan, plan))
        {
            _subLayer = new SubtitleLayer(lines, _preview.SubtitleCfg, notes, mainLine: true);
            _subLayerVer = ver;
            _subLayerPlan = plan;
        }

        return _subLayer;
    }

    /// <summary>把字幕层叠到已渲染的帧上。包含主歌词行与注音/翻译行（配置为 Off 时由 OverlayLayer 返回 null 略过）；
    /// 尺寸用设备像素（w/h），排版常数与导出烤入同源派生，保证预览与导出所见一致。</summary>
    private static void PaintSubtitleOverlay(SKCanvas canvas, SubtitleLayer? layer, double t, int w, int h)
    {
        if (layer == null) return;
        try
        {
            canvas.SetMatrix(SKMatrix.Identity);
            layer.Render(canvas, t, w, h);
        }
        catch (Exception ex)
        {
            Log.Debug("预览字幕叠加失败：" + ex.Message); // 字幕出错不能拖垮渲染（同 Vue 的兜底）
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
        _settings.SaveDebounced();
        if (on) StartSpout();
        else StopSpout();
    }

    /// <summary>设置页「重启渲染引擎」（/api/app/render/restart）的落点。</summary>
    public void RestartRender()
    {
        _targetFps = Math.Clamp(_settings.RenderFps, 12, 240);
        if (_settings.SpoutEnabled) RestartSpout();
        else StopSpout();
    }

    // ---------------- 键盘 ----------------

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        // 空格 = 播放/暂停；歌词编辑框里打空格要照常输入。
        // 谁在响就切谁：云试听在响（或工程侧没在响而云试听有曲）→ 切云试听，否则工程预览。
        if (e.Key == Key.Space && Keyboard.FocusedElement is not TextBoxBase)
        {
            if (_cloud.HasTrack && (_cloud.IsPlaying || !_preview.IsPlaying)) _cloud.Toggle();
            else _preview.TogglePlay();
            e.Handled = true;
        }
    }

    // ---------------- 编辑器 / 播放器 两套布局 ----------------

    private static readonly System.Windows.Media.Brush StageBg =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0F, 0x0F, 0x12));

    private void BtnMode_Click(object sender, RoutedEventArgs e)
    {
        var toPlayer = ReferenceEquals(sender, BtnModePlayer);
        // 从播放器切回编辑器时：若在播放器中已选歌播放过，应保留当前播放的云歌到编辑器（不以旧快照覆盖）
        var restore = !toPlayer && !_player.HasActiveSong;
        SetPlayerMode(toPlayer, restore);
    }

    /// <summary>
    /// 切模式：**瞬切**——连续改列宽/边距会让 Stage 每帧 SizeChanged，CPU 渲染路径会反复冲刷帧缓存。
    /// 播放器模式把顶栏/左右栏/时间轴/切分/工程播放条全让位，只留标题栏 + PV + 云队列条。
    /// </summary>
    public void SetPlayerMode(bool on, bool restoreSnapshot = true)
    {
        if (PlayerMode == on) return;
        PlayerMode = on;
        if (on)
        {
            Views.TapSession.Stop(); // 打点会话会抢窗口级空格
            Keyboard.ClearFocus(); // 隐藏的输入框别继续持有焦点（空格判定看 FocusedElement）
            _player.Enter();
        }

        BarTop.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        PaneLeft.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        PaneRight.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        // 播放器模式：左栏换成歌单侧栏（窄一点），编辑器面板让位
        PanePlaylistSide.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ColLeft.Width = on ? new GridLength(300) : new GridLength(340);
        ColLeft.MinWidth = on ? 260 : 280;
        ColRight.Width = on ? new GridLength(0) : new GridLength(370);
        ColRight.MinWidth = on ? 0 : 300;
        // 播放器模式：画面是内嵌的圆角卡片（四周留出底色），控制块紧贴画面下方 —— 对位酷狗歌曲页
        PaneCenter.Margin = on ? new Thickness(16, 18, 28, 18) : new Thickness(16, 12, 16, 12);
        StageFrame.BorderThickness = new Thickness(1);
        StageFrame.CornerRadius = on ? new CornerRadius(14) : new CornerRadius(10);
        StageFrame.Background = on ? System.Windows.Media.Brushes.Black : StageBg;
        BarTransport.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        BarNowPlaying.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        BarTimeline.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        BarCutInfo.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        // 播放器模式下控制键在画面下方那张卡里，底部常驻条让位（避免两套控制重复）
        BarCloud.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        CenterGuide.Visibility = Visibility.Collapsed; // 播放器模式不留白指引
        BtnModeEditor.Tag = on ? null : "on";
        BtnModePlayer.Tag = on ? "on" : null;

        if (!on) _player.Exit(restoreSnapshot);
        RefreshPlayerOverlay();
        Log.Info("[Main] 切换到" + (on ? "播放器" : "编辑器") + "模式");
    }

    /// <summary>播放器模式的空态提示：没在放歌时才显示（歌名/控制都在画面下方那张卡里）。</summary>
    private void RefreshPlayerOverlay()
    {
        if (!PlayerMode) return;
        TblPvHint.Visibility = _cloud.HasTrack ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------------- 窗口 chrome ----------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ((HwndSource)PresentationSource.FromVisual(this))?.AddHook(WndProc);
        UpdateMaxButton();
        ApplyNativeWindowIcon();
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

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtr")]
    private static extern IntPtr SetClassLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private const uint WM_SETICON = 0x0080;
    private const int GCLP_HICON = -14;
    private const int GCLP_HICONSM = -34;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    // 定时器精度：Windows 默认调度粒度 15.6ms，泵的「Thread.Sleep(1) 等下一拍」会超睡一整拍
    // （实测：Spout 泵 60fps 目标掉到 32-43fps，随睡眠落点抽奖）。渲染泵活着期间把粒度提到 1ms。
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

    private Icon? _hIcon16, _hIcon32;

    /// <summary>
    /// 为 HWND 显式绑定原生 16x16 与 32x32 图标并挂上 GCLP_HICON：
    /// 解决无边框自定义标题栏（WindowStyle=None）在 Windows 任务栏显示空白应用窗口图标的问题。
    /// </summary>
    private void ApplyNativeWindowIcon()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var uri = new Uri("pack://application:,,,/assets/app.ico");
            var streamInfo = Application.GetResourceStream(uri);
            if (streamInfo == null) return;
            using var baseIco = new Icon(streamInfo.Stream);
            _hIcon16 = new Icon(baseIco, 16, 16);
            _hIcon32 = new Icon(baseIco, 32, 32);
            SendMessage(hwnd, WM_SETICON, (IntPtr)0, _hIcon16.Handle);
            SendMessage(hwnd, WM_SETICON, (IntPtr)1, _hIcon32.Handle);
            if (Environment.Is64BitProcess)
            {
                SetClassLongPtr64(hwnd, GCLP_HICONSM, _hIcon16.Handle);
                SetClassLongPtr64(hwnd, GCLP_HICON, _hIcon32.Handle);
            }
        }
        catch (Exception ex)
        {
            Log.Debug("[Icon] 原生窗口图标设置失败：" + ex.Message);
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _cloud.Dispose(); // 云试听：先停链再拆（进程要退了，避免设备句柄留到 GC）
        Follow.Stop(); // 跟随循环与引擎时钟归位（FollowEnd 冻结时间轴）
        _hIcon16?.Dispose();
        _hIcon32?.Dispose();
        // 先停后台渲染泵再释放：渲染线程还持着面/锁。冷缓存帧实测 0.7–1.8s/帧且持引擎闸，
        // 固定 800ms 会超时后照常 Dispose 在途帧用的资源。给足 3s；仍不退出就记日志后照常释放
        // （窗口已关，进程随即退出，残留句柄无害；卡着硬 Dispose 才有原生崩溃风险）。
        _renderRunning = false;
        if (_renderThread != null && _renderThread.IsAlive)
        {
            var waited = 0;
            while (_renderThread.IsAlive && waited < 3000)
            {
                try { _renderThread.Join(100); } catch { /* 线程已退出 */ }
                waited += 100;
            }
            if (_renderThread.IsAlive) Log.Warn("[Main] 渲染泵 3s 未退出，带活线程释放（进程即将退出）");
        }
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
        _d3dStage?.Dispose();
        timeEndPeriod(1);
        base.OnClosed(e);
    }
}
