// Spout 直出管线（pure-csharp 分支）：Skia 光栅 → D3D11 纹理 → SpoutDX。
//
// 两条光栅路线（同一套泵/通道/发送语义，接收端无感）：
//   GPU（默认，GpuRasterHost 可用时）：ANGLE(GL on D3D11) → Skia GRContext 直接画进
//        该表面背后的 D3D11 共享纹理 → SpoutDX.SendTexture(同一块纹理) —— 全程 GPU，
//        没有一个像素经过 CPU 内存。1080p 实测 ~7ms/帧（CPU 光栅 ~46-64ms/帧）。
//   CPU（回退：缺 ANGLE 二进制 / 初始化失败 / JIZURA_GPU=0）：SkiaSharp CPU 光栅（BGRA 8888）
//        → PeekPixels → 逐行拷进 D3D11 Dynamic 纹理 → SendTexture。
//
// 双通道：
//   主通道   <SpoutName>        settings.Width×Height @ RenderFps，SpoutTransparent 时透出底
//   字幕通道 <SpoutName>-LYRICS 同分辨率 @ min(LyricsFps, RenderFps)，恒透明底只画字幕层
//            （SubtitleLayer：KRC 逐字主行 + 音译/翻译行，与导出烤字幕同一个渲染器；
//             无 KRC 字幕数据时输出全透明帧）
//
// 泵语义（对位旧版 EBF 泵）：固定帧率泵线程；播放态 = 目标帧率；暂停态降到 10Hz 且只在
// 「画面会变」（seek/重规划/换字幕 → 版本号变）时重绘——暂停帧是静态的，10Hz 只是让编辑/seek
// 后 ≤100ms 反映到接收端，不空转烧 CPU/GPU。
//
// 线程纪律：GPU 路线下泵线程是 EGL 上下文的唯一持有者（PumpLoop 进 Enter / 出 Exit），
// 引擎静态缓存经 PreviewEngine.EngineRenderGate 与 WPF 预览串行。

using System.Diagnostics;
using JizuraLive.App;
using JizuraLive.App.Cloud;
using JizuraLive.Engine;
using JizuraLive.Logging;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SkiaSharp;
using SpoutDXWrapper;
using Buffer = System.Buffer;
using Device = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;
using Resource = SharpDX.DXGI.Resource;

namespace JizuraLive.Spout;

/// <summary>Spout 数据源：给出当前 plan / 播放时间 / 暂停脏标记（PreviewEngine 实现）。</summary>
public interface ISpoutSource
{
    Plan Plan { get; }

    /// <summary>当前工程时间（秒）。</summary>
    double CurrentTime { get; }

    /// <summary>是否播放中。</summary>
    bool IsPlaying { get; }

    /// <summary>内容版本号：暂停态下若不变，说明画面静止，泵跳过重绘。</summary>
    long Version { get; }

    /// <summary>字幕层时间轴（KRC 逐字）；null = 无 KRC 数据，字幕通道退化为从 plan 行合成
    /// （连注音内容都没有时才输出全透明帧）。</summary>
    IReadOnlyList<KrcLine>? Subtitles { get; }

    /// <summary>字幕层配置（与导出烤字幕同一份）。</summary>
    SubtitleConfig SubtitleConfig { get; }

    /// <summary>字幕数据版本号：装载/清空字幕时变，字幕通道据此重建字幕层。</summary>
    long SubtitleVersion { get; }
}

/// <summary>SpoutOut 启动参数（从 Settings 组装）。</summary>
public sealed class SpoutOutOptions
{
    /// <summary>主通道 Spout 发送端名。</summary>
    public string SenderName { get; set; } = "JIZURA-live";

    /// <summary>主通道透明底（跳过背景填充，只出歌词+特效透明图层给 OBS 合成）。</summary>
    public bool MainTransparent { get; set; }

    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;

    /// <summary>主通道帧率（泵节拍基准）。</summary>
    public int Fps { get; set; } = 60;

    /// <summary>独立字幕通道开关。</summary>
    public bool LyricsEnabled { get; set; } = true;

    /// <summary>字幕通道帧率（调用方先钳 min(LyricsFps, RenderFps)——字幕画得比主通道快没意义）。</summary>
    public int LyricsFps { get; set; } = 10;
}

/// <summary>
/// Spout 直出：一条管线同时服务主通道与（可选）字幕通道。
/// 线程模型：泵线程独占渲染（自有 Renderer 实例，实例状态不跨线程）；
/// 引擎静态缓存经 PreviewEngine.EngineRenderGate 与 WPF 预览串行；
/// Plan 引用换新原子生效（每帧取一次引用，旧 plan 在在途帧内仍有效）。
/// </summary>
public sealed class SpoutOut : IDisposable
{
    /// <summary>暂停态泵拍（旧管线的 PausedPumpMs）：10Hz 只为 seek/重规划 ≤100ms 出画。</summary>
    private const int PausedPumpMs = 100;

    private const int MinIntervalMs = 4; // 泵拍下限（≤250fps），防 0 间隔自旋

    private Device? _device;

    /// <summary>_device 是 CPU 回退路径自建的（GPU 模式下它是宿主单例的设备，不归本实例释放）。</summary>
    private bool _ownsDevice;

    private GpuRasterHost? _gpu;
    private Channel? _main;
    private Channel? _lyrics;
    private Thread? _pump;
    private volatile bool _running;
    private ISpoutSource? _source;

    /// <summary>预览镜像（MainWindow 建、Spout 泵写；null = 不需要）。见 <see cref="JizuraLive.App.PreviewMirror"/>。</summary>
    public PreviewMirror? Preview { get; set; }

    // 镜像读回用的 staging 纹理（按尺寸/格式缓存复用；CopyResource 要求格式与共享纹理一致）
    private Texture2D? _mirrorStaging;
    private int _mirrorStgW = -1, _mirrorStgH = -1, _mirrorStgFmt = -1;

    // 预览发布纹理：交给 WPF D3DImage 显存直显的那块面。引擎画的 pbuffer 每帧都被 Clear+重画，
    // 把它的句柄直接给预览会让合成器按自己的 vsync 采到「清空后/画一半」的中间态（实测播放时
    // 持续闪烁；暂停态不重画所以不闪）。改成每帧把画完的 pbuffer 整幅拷进这块专用共享纹理再发布
    // ——它除一次 ~30µs 的 GPU 拷贝外全程静止，合成器采到的永远是完整帧。
    // shortcut: 只有一块发布纹理，合成器理论上仍可能落在那 ~30µs 的拷贝里（最坏是两帧之间的
    // 细缝，不是整屏清空）。若这点细缝真被看出来，改成 2~3 块轮转发布 + D3D11ImageSource
    // 缓存多个句柄按帧 SetBackBuffer。
    private Texture2D? _pubTex;
    private IntPtr _pubHandle = IntPtr.Zero;
    private int _pubW = -1, _pubH = -1, _pubFmt = -1;
    private bool _pubUnavailable; // 共享发布纹理建不出来：只试一次，之后一直走 staging 读回

    public bool IsRunning => _running;
    public string? SenderName => _main?.Name;
    public string? LyricsSenderName => _lyrics?.Name;
    public bool LyricsRunning => _lyrics != null;

    /// <summary>主通道正走 GPU 光栅（预览镜像可用；CPU 回退时预览必须走自己的 CPU 路径）。</summary>
    public bool MirrorActive => _running && _gpu != null && _main is { Gpu: true };

    /// <summary>按当前设置重建（分辨率/帧率/名称/透明改动经 /api/app/render/restart 走这里生效）。</summary>
    public bool Restart(ISpoutSource source, SpoutOutOptions opt)
    {
        Stop();
        return Start(source, opt);
    }

    public bool Start(ISpoutSource source, SpoutOutOptions opt)
    {
        if (_running) return true;
        _source = source ?? throw new ArgumentNullException(nameof(source));

        // 两条光栅路线：GPU（ANGLE 渲染进共享纹理，Spout 同设备直发，全程无 CPU 拷贝）优先；
        // 宿主不可用（缺 ANGLE 二进制/初始化失败/JIZURA_GPU=0）则回退 CPU 面 → Map/Unmap 上传。
        // 两条路都产出 D3D11 纹理交给同一个 SpoutSender，接收端无感。
        _gpu = GpuRasterHost.TryGet(out var gpuErr);
        if (_gpu == null)
            Log.Info("[SpoutOut] GPU 光栅不可用，本次走 CPU 光栅：" + gpuErr);

        try
        {
            if (_gpu != null)
            {
                _device = _gpu.D3DDevice;
                _ownsDevice = false;
            }
            else if (_device == null)
            {
                _device = new Device(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
                _ownsDevice = true;
            }
        }
        catch (Exception ex)
        {
            Log.Error("[SpoutOut] D3D11 设备创建失败，Spout 输出不可用：" + ex.Message, ex);
            return false;
        }

        try
        {
            _main = new Channel(opt.SenderName, opt.Width, opt.Height, opt.Fps, opt.MainTransparent,
                subtitle: false, _device, _gpu != null);
            _lyrics = opt.LyricsEnabled
                ? new Channel(opt.SenderName + "-LYRICS", opt.Width, opt.Height,
                    Math.Max(1, opt.LyricsFps), transparent: true, subtitle: true, _device, _gpu != null)
                : null;
        }
        catch (Exception ex)
        {
            Log.Error("[SpoutOut] Spout 发送端初始化失败：" + ex.Message, ex);
            Cleanup();
            return false;
        }

        _running = true;
        _pump = new Thread(PumpLoop)
        {
            IsBackground = true,
            Name = "SpoutOutPump",
        };
        _pump.Start();

        Log.Info(
            $"[SpoutOut] 管线已启动（{(_gpu != null ? "GPU 光栅" : "CPU 光栅")}）：主通道={opt.SenderName} {opt.Width}x{opt.Height}@{opt.Fps}"
            + (opt.MainTransparent ? "（透明底）" : "")
            + (_lyrics != null
                ? $"；字幕通道={opt.SenderName}-LYRICS@{opt.LyricsFps}（透明底，KRC 字幕层）"
                : "；字幕通道=关"));
        return true;
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;

        var pump = _pump;
        _pump = null;
        if (pump != null && pump.IsAlive && pump != Thread.CurrentThread)
        {
            var waited = 0; // 单帧冷渲染实测可达 0.7–1.8s：分步等待给足退出窗口
            while (pump.IsAlive && waited < 2000)
            {
                pump.Join(100);
                waited += 100;
            }
            if (pump.IsAlive)
            {
                // 泵还卡着就 Cleanup = 销毁它正在用的原生资源（最坏原生崩溃）。宁可漏掉这组
                // Channel 等进程退出回收，也不在活线程上 Dispose。
                Log.Warn("[SpoutOut] 泵线程 2s 未退出：跳过 Cleanup，Channel 留待进程退出回收");
                return;
            }
        }

        Cleanup();
        Log.Info("[SpoutOut] 管线已停止");
    }

    private void Cleanup()
    {
        foreach (var ch in new[] { _main, _lyrics })
            ch?.Dispose();
        _main = null;
        _lyrics = null;
    }

    public void Dispose()
    {
        Stop();
        // 宿主设备是进程单例（预览/导出将来复用），不能在这里释放；只有 CPU 回退路径自建的设备归本实例
        _mirrorStaging?.Dispose();
        _mirrorStaging = null;
        _mirrorStgW = _mirrorStgH = -1;
        _mirrorStgFmt = -1;
        _pubTex?.Dispose();
        _pubTex = null;
        _pubHandle = IntPtr.Zero;
        _pubW = _pubH = _pubFmt = -1;
        if (_ownsDevice)
        {
            _device?.Dispose();
            _device = null;
            _ownsDevice = false;
        }
    }

    // ---------------- 泵 ----------------

    private void PumpLoop()
    {
        // 主通道定节拍；字幕通道按自己的 fps 用计时戳滤帧（两条通道共享一个泵线程）
        var main = _main!;

        // GPU 路径：EGL 上下文必须在渲染线程上 current。泵线程进入时绑定、退出时解绑
        // （宿主是进程单例，本体留给下一次启动复用）。绑定失败就地把两条通道降回 CPU 面。
        var gpu = _gpu;
        var gpuEntered = false;
        if (gpu != null)
        {
            try
            {
                gpu.Enter();
                gpuEntered = true;
            }
            catch (Exception ex)
            {
                Log.Error("[SpoutOut] GPU 上下文绑定失败，本管线转 CPU 光栅：" + ex.Message, ex);
                foreach (var ch in new[] { _main, _lyrics })
                    if (ch != null) ch.Gpu = false;
                gpu = null;
            }
        }

        try
        {
            PumpLoopCore(main);
        }
        finally
        {
            if (gpuEntered) gpu!.Exit();
        }
    }

    /// <summary>泵主体：固定节拍 + 双通道渲染。抽成独立方法只为让 GPU 上下文的 Enter/Exit 成对。</summary>
    private void PumpLoopCore(Channel main)
    {
        var lastMain = Stopwatch.GetTimestamp();
        var lastLyrics = Stopwatch.GetTimestamp();
        var lastStat = Stopwatch.GetTimestamp();

        while (_running)
        {
            var src = _source;
            var now = Stopwatch.GetTimestamp();

            // 每 5s 报一次帧数 + 耗时归因（掉出帧率时一眼定位：渲染 / 等 GPU 落盘 / 镜像读回 / 等引擎闸）
            if (ElapsedMs(lastStat, now) >= 5_000)
            {
                var lyrStat = _lyrics;
                Log.Info(
                    $"[SpoutOut] 近 5s：{main.Name} {main.Frames} 帧{KeepTxt(main)}"
                    + (lyrStat != null ? $"；{lyrStat.Name} {lyrStat.Frames} 帧{KeepTxt(lyrStat)}" : "")
                    + $"，耗时 渲染{main.SumRender / Math.Max(1, main.Frames):F1}"
                    + $"+同步{main.SumFlush / Math.Max(1, main.Frames):F1}"
                    + $"+镜像{main.SumMirror / Math.Max(1, main.Frames):F1}"
                    + $"+等闸{main.SumGate / Math.Max(1, main.Frames):F1}ms"
                    + RenderStatsTxt(main) + (lyrStat != null ? RenderStatsTxt(lyrStat) : ""));
                lastStat = now;
                main.Frames = 0;
                main.KeepAlives = 0;
                main.SumRender = main.SumFlush = main.SumMirror = main.SumGate = 0;
                if (lyrStat != null)
                {
                    lyrStat.Frames = 0;
                    lyrStat.KeepAlives = 0;
                    lyrStat.SumRender = lyrStat.SumFlush = lyrStat.SumMirror = lyrStat.SumGate = 0;
                }
            }

            try
            {
                if (src != null && src.Plan.Cuts.Count > 0)
                {
                    if (src.IsPlaying)
                    {
                        if (ElapsedMs(lastMain, now) >= main.IntervalMs)
                        {
                            // 慢帧滞回按「上一拍到这一拍」的间隔推进（前端 dt 同义：用户实际看到的帧间隔）
                            var pubNow = Stopwatch.GetTimestamp();
                            if (main.LastPubTs != 0) main.TickSlow(Ms(main.LastPubTs, pubNow), pubNow);
                            main.LastPubTs = pubNow;
                            RenderChannel(main, src);
                            lastMain = now;
                        }

                        var lyr = _lyrics;
                        if (lyr != null && ElapsedMs(lastLyrics, now) >= lyr.IntervalMs)
                        {
                            RenderChannel(lyr, src);
                            lastLyrics = now;
                        }
                    }
                    else
                    {
                        // 暂停：画面内容只在 seek/重规划（Version 变）时改变，不逐帧重渲；
                        // 两条通道各自记 Version，重规划后各补一帧。字幕通道还要看字幕数据
                        // 版本（换歌装载字幕时暂停态也得重出一帧）。
                        // 但 Spout 流不能断：接收端在发送端静默一段时间后会把这一路判为掉线——
                        // 表现是主画面还留着上一帧、独立的字幕叠加层却空了。所以没脏也要把上一帧
                        // 纹理再投一次（纯句柄发布，不重画），让接收端看到流还活着。
                        // 暂停帧一律满质量（前端同：!playing → slow=false），并清掉帧间隔基准，
                        // 否则恢复播放的首拍会把「暂停时长」当成帧间隔，立刻误进慢态。
                        main.Slow = false;
                        main.LastPubTs = 0;
                        if (Dirty(main, src))
                        {
                            RenderChannel(main, src);
                            lastMain = Stopwatch.GetTimestamp();
                        }
                        else
                        {
                            KeepAlive(main);
                        }

                        var lyr = _lyrics;
                        if (lyr != null)
                        {
                            if (Dirty(lyr, src))
                            {
                                RenderChannel(lyr, src);
                                lastLyrics = Stopwatch.GetTimestamp();
                            }
                            else
                            {
                                KeepAlive(lyr);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogRenderError(ex);
            }

            // 高精度等待下一拍：粗睡留 2ms 余量，末段自旋补齐（对齐旧 EBF 泵）。
            // 播放态目标时刻锚在「上一拍」上：渲染耗时已经吃掉一部分间隔，不能从当前时刻再加一整个
            // 间隔（那会让实际周期 = 渲染耗时 + 间隔，1080p GPU 光栅下掉到 ~47fps）。
            // 已落后（渲染超时/长暂停后恢复）则不再等，立刻进入下一拍，避免追赶风暴。
            long next;
            if (src is { IsPlaying: true })
            {
                next = lastMain
                       + (long)(Math.Max(MinIntervalMs, main.IntervalMs) / 1000.0 * Stopwatch.Frequency);
                var tickNow = Stopwatch.GetTimestamp();
                if (next < tickNow) next = tickNow;
            }
            else
            {
                next = Stopwatch.GetTimestamp()
                       + (long)(PausedPumpMs / 1000.0 * Stopwatch.Frequency);
            }

            while (_running)
            {
                var remainMs = (next - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency;
                if (remainMs <= 0) break;
                if (remainMs > 2) Thread.Sleep(1);
                else Thread.SpinWait(64);
            }
        }
    }

    private static double ElapsedMs(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;

    /// <summary>暂停态脏判断：引擎通道看内容版本；字幕通道还要看字幕数据版本（换歌/装载字幕）。</summary>
    private static bool Dirty(Channel ch, ISpoutSource src) =>
        src.Version != ch.LastVersion || (ch.Subtitle && src.SubtitleVersion != ch.SubLayerVersion);

    /// <summary>暂停态保活：不重渲，把该通道上一帧的纹理再投一次。Spout 接收端（OBS/Shoost 等）在发送端
    /// 静默一段时间后会把这一路判为掉线——独立字幕层这种叠加源会直接空掉，而主画面还留着上一帧。
    /// 内容没变，重发只是让接收端看到流还在；代价是一次 SendTexture 句柄发布，不产生渲染开销。
    /// </summary>
    private void KeepAlive(Channel ch)
    {
        var tex = ch.LastTexture;
        if (tex == IntPtr.Zero) return;
        try
        {
            ch.Sender!.SendTexture(tex);
            ch.KeepAlives++;
        }
        catch (Exception ex)
        {
            LogRenderError(ex);
        }
    }

    /// <summary>统计行里的暂停态保活后缀（没保活就不加，保持原格式）。</summary>
    private static string KeepTxt(Channel ch) => ch.KeepAlives > 0 ? $"（保活{ch.KeepAlives}）" : "";

    /// <summary>渲染侧窗口统计文本。出帧数只是 Frame 调用次数：命中整帧缓存那一帧只是一次贴图，
    /// 真正说明「引擎画了多少、每帧多久」的是这里的真渲染次数与真渲染耗时，与上面的帧数分开看。</summary>
    private static string RenderStatsTxt(Channel ch)
    {
        if (ch.Renderer == null) return $"｜{ch.Name} 字幕层无引擎渲染";
        var s = ch.Renderer.TakeWindowStats();
        return $"｜{ch.Name} 引擎调用 {s.Calls} 次（真渲染 {s.Real}、缓存 {s.Cached}，"
             + $"真渲染平均 {(s.Real > 0 ? s.RealSumMs / s.Real : 0):F1}ms、峰值 {s.MaxMs:F1}ms）";
    }

    // 渲染失败按帧发生（设备移除等持续性故障），首条立即记，之后每 30 秒汇总一条（对齐旧节流）
    private long _lastRenderErrorTick = -31_000;
    private int _renderErrorsSinceLastLog;

    private void LogRenderError(Exception ex)
    {
        _renderErrorsSinceLastLog++;
        var now = Environment.TickCount64;
        if (now - _lastRenderErrorTick < 30_000) return;
        var suppressed = _renderErrorsSinceLastLog - 1;
        _renderErrorsSinceLastLog = 0;
        _lastRenderErrorTick = now;
        Log.Error("[SpoutOut] 帧渲染失败" + (suppressed > 0 ? $"（节流：此前 {suppressed} 条同类错误未逐条记录）" : ""), ex);
    }

    // ---------------- 单通道渲染：GPU 光栅直发 / CPU 光栅上传（两条路都出 D3D11 纹理） ----------------

    /// <summary>
    /// 渲染一帧并按通道的光栅路线发出去。GPU 路连续失败（设备移除/上下文失效等持续性故障）
    /// 时本通道降回 CPU 面，泵不中断。
    /// </summary>
    private void RenderChannel(Channel ch, ISpoutSource src)
    {
        try
        {
            RenderChannelCore(ch, src);
            ch.GpuErrors = 0;
        }
        catch
        {
            if (ch.Gpu && ++ch.GpuErrors >= 3)
            {
                ch.Gpu = false;
                Log.Error($"[SpoutOut] {ch.Name}：GPU 渲染连续失败，本通道降回 CPU 光栅"
                          + "（GPU 上下文/设备可能已失效）");
            }

            throw;
        }
    }

    private void RenderChannelCore(Channel ch, ISpoutSource src)
    {
        var t0 = Stopwatch.GetTimestamp();
        var t = ch.Subtitle ? PaintSubtitle(ch, src) : PaintEngine(ch, src);
        var tRender = Stopwatch.GetTimestamp();
        if (t.Gpu != null)
        {
            // 共享纹理已由 Skia 画好：刷命令 + 等 GPU 落盘（落盘后 Spout 的设备才读得到），
            // 然后同设备直发——这一帧没有一个像素经过 CPU。
            _gpu!.FlushAndSync();
            var tSync = Stopwatch.GetTimestamp();
            ch.Sender!.SendTexture(t.Gpu.TexturePointer);
            ch.LastTexture = t.Gpu.TexturePointer; // 暂停态保活复用（面是池化的，指针跨帧稳定）
            // 预览镜像：主通道 GPU 帧读回一块给预览用（消费方是 WPF 预览泵；无请求零开销）
            if (!ch.Subtitle && ReferenceEquals(ch, _main))
                MirrorFrame(t);
            var tMirror = Stopwatch.GetTimestamp();
            ch.SumRender += Ms(t0, tRender);
            ch.SumFlush += Ms(tRender, tSync);
            ch.SumMirror += Ms(tSync, tMirror);
        }
        else
        {
            Upload(ch, t.Surface.PeekPixels() ?? throw new InvalidOperationException("出图面 PeekPixels 返回空"));
        }

        ch.LastVersion = src.Version;
        ch.Frames++;
    }

    private static double Ms(long a, long b) => (b - a) * 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// 出图面 → 预览镜像，两条路：
    /// GPU 直显（首选）：把画完的 pbuffer 整幅拷进发布纹理，只发布这块静止的共享纹理给
    /// WPF D3DImage（见 EnsurePublishTexture 与字段注释）。
    /// staging 读回（兜底：发布纹理建不出来/拷贝失败）：计划区经 D3D11 staging 读回 CPU 位图
    /// （1080p 实测 ~1.7ms；走 ANGLE 的 glReadPixels 同尺寸要 ~13.6ms）。必须在 FlushAndSync
    /// 之后调用（GPU 落盘后才读得到）；staging 格式取共享纹理的 DXGI 格式——不一致时
    /// CopyResource 会静默不拷。
    /// </summary>
    private void MirrorFrame(Target t)
    {
        var m = Preview;
        if (m == null || !m.Wanted) return;

        // GPU 直显：发布的是**发布纹理**的句柄，不是 pbuffer 自己的——pbuffer 下一帧就被
        // Clear+重画，直接发布会让 WPF 合成器采到中间态（见字段注释）。
        // 尺寸用纹理尺寸（Gpu.Width/Height），不是 plan 区域尺寸——共享资源就是全幅纹理，
        // 预览侧按 plan 尺寸打开会被 D3D11 判 E_INVALIDARG；plan 区域单独给，供字幕排版。
        if (t.Gpu != null && EnsurePublishTexture(t.Gpu.Width, t.Gpu.Height, t.Gpu.DxgiFormat))
        {
            try
            {
                _device!.ImmediateContext.CopyResource(t.Gpu.Texture, _pubTex!);
                m.PublishGpu(_pubHandle, t.Gpu.Width, t.Gpu.Height, t.PlanX, t.PlanY, t.PlanW, t.PlanH);
                return;
            }
            catch (Exception ex)
            {
                // 拷贝失败不该把 Spout 主通道拖降级（预览只是消费方）：记一次（节流）后走 staging 读回
                LogRenderError(ex);
            }
        }

        var back = m.BeginWrite(t.PlanW, t.PlanH);
        if (back == null) return;
        try
        {
            var src = t.Gpu!.Texture;
            EnsureMirrorStaging(t.PlanW, t.PlanH, t.Gpu.DxgiFormat);
            var ctx = _device!.ImmediateContext;
            if (t.PlanX == 0 && t.PlanY == 0 && t.PlanW == t.Gpu.Width && t.PlanH == t.Gpu.Height)
            {
                ctx.CopyResource(src, _mirrorStaging!);
            }
            else
            {
                ctx.CopySubresourceRegion(src, 0,
                    new ResourceRegion(t.PlanX, t.PlanY, 0, t.PlanX + t.PlanW, t.PlanY + t.PlanH, 1),
                    _mirrorStaging!, 0);
            }

            var mapped = ctx.MapSubresource(_mirrorStaging!, 0, MapMode.Read, MapFlags.None);
            try
            {
                var dst = back.GetPixels();
                unsafe
                {
                    for (var row = 0; row < t.PlanH; row++)
                        Buffer.MemoryCopy(
                            (void*)(mapped.DataPointer + (long)row * mapped.RowPitch),
                            (void*)(dst + (long)row * back.RowBytes),
                            back.RowBytes, (long)t.PlanW * 4);
                }
            }
            finally
            {
                ctx.UnmapSubresource(_mirrorStaging!, 0);
            }

            m.EndWrite();
        }
        catch (Exception ex)
        {
            // 镜像失败不该拖垮输出：记一次（节流）后继续，预览侧无新帧会自动回退 CPU 渲染
            LogRenderError(ex);
        }
    }

    private void EnsureMirrorStaging(int w, int h, int dxgiFormat)
    {
        if (_mirrorStaging != null && _mirrorStgW == w && _mirrorStgH == h && _mirrorStgFmt == dxgiFormat) return;
        _mirrorStaging?.Dispose();
        _mirrorStaging = new Texture2D(_device!, new Texture2DDescription
        {
            Width = w,
            Height = h,
            MipLevels = 1,
            ArraySize = 1,
            Format = (Format)dxgiFormat,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CpuAccessFlags = CpuAccessFlags.Read,
            OptionFlags = ResourceOptionFlags.None,
        });
        _mirrorStgW = w;
        _mirrorStgH = h;
        _mirrorStgFmt = dxgiFormat;
    }

    /// <summary>
    /// 预览发布纹理：与出图面同尺寸同格式的 D3D11 共享纹理，WPF D3DImage 经 D3D9Ex 零拷贝打开它。
    /// 必须带 ResourceOptionFlags.Shared 才有 D3D9Ex 认的共享句柄；格式随出图面（CopyResource 要求同规格）。
    /// 建不出来（驱动不支持共享等）只试一次，之后返回 false 让调用方走 staging 读回。
    /// </summary>
    private bool EnsurePublishTexture(int w, int h, int dxgiFormat)
    {
        if (_pubUnavailable) return false;
        if (_pubTex != null && _pubW == w && _pubH == h && _pubFmt == dxgiFormat) return true;
        try
        {
            _pubTex?.Dispose();
            _pubTex = new Texture2D(_device!, new Texture2DDescription
            {
                Width = w,
                Height = h,
                MipLevels = 1,
                ArraySize = 1,
                Format = (Format)dxgiFormat,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.Shared,
            });
            using var res = _pubTex.QueryInterface<Resource>();
            _pubHandle = res.SharedHandle;
            _pubW = w;
            _pubH = h;
            _pubFmt = dxgiFormat;
            if (_pubHandle != IntPtr.Zero) return true;
            throw new InvalidOperationException("共享纹理没有 D3D9Ex 可用的共享句柄");
        }
        catch (Exception ex)
        {
            _pubTex?.Dispose();
            _pubTex = null;
            _pubHandle = IntPtr.Zero;
            _pubW = _pubH = _pubFmt = -1;
            _pubUnavailable = true;
            Log.Warn("[SpoutOut] 预览发布纹理创建失败，预览改走 staging 读回：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 一块已激活的绘制目标。Gpu 非空 = 画的就是要发给 Spout 的那块共享纹理（零拷贝）；
    /// 为空 = CPU 面（回退路径，画完 PeekPixels 上传）。
    /// PlanX/Y/W/H = 计划区在出图面里的位置与尺寸（letterbox 时小于出图面；预览镜像按它读回）。
    /// </summary>
    private readonly struct Target
    {
        public readonly SKSurface Surface;
        public readonly SKCanvas Canvas;
        public readonly GpuSurface? Gpu;
        public readonly int PlanX, PlanY, PlanW, PlanH;

        public Target(SKSurface surface, GpuSurface? gpu, int planX, int planY, int planW, int planH)
        {
            Surface = surface;
            Canvas = surface.Canvas;
            Gpu = gpu;
            PlanX = planX;
            PlanY = planY;
            PlanW = planW;
            PlanH = planH;
        }
    }

    /// <summary>取输出面：GPU 模式从宿主面池按尺寸取并激活（该面的纹理就是发送对象）；CPU 模式按尺寸复用。</summary>
    private Target AcquireOut(Channel ch)
    {
        if (ch.Gpu)
        {
            var g = _gpu!.Rent(ch.Name, ch.W, ch.H);
            _gpu.Activate(g);
            return new Target(g.Skia, g, 0, 0, ch.W, ch.H);
        }

        EnsureSurface(ref ch.Out, ch.W, ch.H);
        return new Target(ch.Out!, null, 0, 0, ch.W, ch.H);
    }

    /// <summary>
    /// 引擎场景面（contain 后的画幅）。使用 CPU 托管面并按尺寸复用（绝不逐帧 Dispose，整帧缓存持有其快照）。
    /// Blit 阶段使用 PeekPixels + FromPixels 借像素上传到 GPU 出图面，杜绝 Snapshot() 导致的
    /// _fcCv 悬空引用（0xC0000005）与 GPU 纹理进 GC 终结器（0xC000001D）。
    /// </summary>
    private SKSurface AcquireScene(Channel ch, int w, int h)
    {
        if (ch.Scene == null || ch.SceneW != w || ch.SceneH != h)
        {
            ch.Renderer?.ClearFrameCache();
            ch.Scene?.Dispose();
            ch.Scene = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
            ch.SceneW = w;
            ch.SceneH = h;
        }

        return ch.Scene;
    }

    /// <summary>字幕通道：透明底全幅只画字幕层（SubtitleLayer 与导出烤字幕同一个渲染器）。</summary>
    private Target PaintSubtitle(Channel ch, ISpoutSource src)
    {
        var tgt = AcquireOut(ch);
        var oc = tgt.Canvas;
        oc.SetMatrix(SKMatrix.Identity);
        oc.Clear(SKColors.Transparent);

        // 换歌/装载/清空字幕、改字幕样式（都走字幕版本号）、重规划（注音来自 plan：KRC 走 cut 注音
        // 回填，无 KRC 时整行合成）时重建字幕层；plan 变化不记日志（编辑时每次重规划都记会刷屏）
        var lines = src.Subtitles;
        var versionChanged = src.SubtitleVersion != ch.SubLayerVersion;
        var planChanged = !ReferenceEquals(src.Plan, ch.SubLayerPlan);
        if (versionChanged || planChanged)
        {
            ch.SubLayerVersion = src.SubtitleVersion;
            ch.SubLayerPlan = src.Plan;

            var fromKrc = lines is { Count: > 0 };
            IReadOnlyList<string>? notes;
            if (fromKrc)
            {
                notes = SubtitleLayer.NotesFromPlan(lines!, src.Plan);
            }
            else
            {
                // 没有 KRC 逐字（网易云 LRC / 手动粘贴 / 重启恢复工程——KRC 不随工程落盘）：
                // 从 plan 行合成，与预览叠加、导出烤入共用同一份，避免「预览有字幕、Spout 通道空」
                var synth = SubtitleLayer.FromPlan(src.Plan);
                lines = synth?.Lines;
                notes = synth?.Notes;
            }

            ch.SubLayer = lines is { Count: > 0 }
                ? new SubtitleLayer(lines, src.SubtitleConfig, notes)
                : null;
            if (versionChanged)
            {
                if (ch.SubLayer == null)
                    Log.Info("[SpoutOut] 字幕通道：当前没有可用字幕数据，输出透明帧"
                             + "（既无酷狗 KRC 逐字，工程歌词行也没有注音内容）");
                else
                    Log.Info($"[SpoutOut] 字幕通道：字幕层就绪，{lines!.Count} 行"
                             + $"（{(fromKrc ? "KRC 逐字" : "工程行合成")}）"
                             + (src.SubtitleConfig.Content == SubContent.Off ? "（内容=off，不出画面）" : ""));
            }
        }

        // 排版按输出分辨率全幅派生（字号 = H 百分比、底部 5.5% 起排），与导出烤字幕所见一致
        ch.SubLayer?.Render(oc, src.CurrentTime, ch.W, ch.H);
        return tgt;
    }

    private Target PaintEngine(Channel ch, ISpoutSource src)
    {
        var plan = src.Plan;
        var t = src.CurrentTime;

        // contain 缩放：settings 分辨率与 plan 画幅比不一致时出边（主流 16:9 配 16:9 无痕）。
        var scale = Math.Min(ch.W / (double)plan.W, ch.H / (double)plan.H);
        var rw = Math.Max(1, (int)Math.Round(plan.W * scale));
        var rh = Math.Max(1, (int)Math.Round(plan.H * scale));

        // 画幅比一致（绝大多数情形）：引擎直接画进出图面。GPU 模式下这就是要发的共享纹理，
        // 全程零拷贝——没有场景面、没有 blit、没有 CPU 上传。
        if (rw == ch.W && rh == ch.H)
        {
            var only = AcquireOut(ch);
            var g0 = Stopwatch.GetTimestamp();
            lock (EngineRenderGate.Lock)
            {
                ch.SumGate += Ms(g0, Stopwatch.GetTimestamp()); // 只记等闸（进锁耗时），不含帧内
                ch.Renderer!.Frame(only.Canvas, plan, t, new RenderOpts
                {
                    Scale = scale,
                    Transparent = ch.Transparent,
                    Stage = true,
                    Fast = ch.Slow,     // 慢帧降质（见 Channel.Slow）
                    NoGhost = ch.Slow,
                });
            }
            return only;
        }

        // 出边：先画场景面（plan 画幅），再居中 blit 到出图面（边带黑/透明）
        var scene = AcquireScene(ch, rw, rh);
        var g1 = Stopwatch.GetTimestamp();
        lock (EngineRenderGate.Lock)
        {
            ch.SumGate += Ms(g1, Stopwatch.GetTimestamp());
            ch.Renderer!.Frame(scene.Canvas, plan, t, new RenderOpts
            {
                Scale = scale,
                Transparent = ch.Transparent,
                Stage = true,
                Fast = ch.Slow,
                NoGhost = ch.Slow,
            });
        }

        var tgt = AcquireOut(ch);
        var oc = tgt.Canvas;
        oc.SetMatrix(SKMatrix.Identity);
        oc.Clear(ch.Transparent ? SKColors.Transparent : SKColors.Black);
        // 引擎整帧缓存把 SnapshotOf(ctx) 存起来跨帧用——交给 Frame 的面必须保留（绝不 Dispose），
        // blit 改 PeekPixels + FromPixels 借用像素，禁止使用 scene.Snapshot()（避免破坏整帧缓存 _fcCv）。
        var spx = scene.PeekPixels() ?? throw new InvalidOperationException("场景面 PeekPixels 返回空");
        using var borrow = SKImage.FromPixels(spx.Info, spx.GetPixels(), spx.RowBytes);
        oc.DrawImage(borrow, (ch.W - rw) / 2f, (ch.H - rh) / 2f);

        return new Target(tgt.Surface, tgt.Gpu, (ch.W - rw) / 2, (ch.H - rh) / 2, rw, rh);
    }

    /// <summary>像素 → D3D11 纹理 → Spout 发送（两条通道共用）。</summary>
    private void Upload(Channel ch, SKPixmap px)
    {
        EnsureTexture(ch, px.Width, px.Height);
        var ctx = _device!.ImmediateContext;
        var mapped = ctx.MapSubresource(ch.Texture!, 0, MapMode.WriteDiscard, MapFlags.None);
        try
        {
            var srcStride = px.Info.RowBytes;
            var dstStride = mapped.RowPitch;
            var srcBase = px.GetPixels();
            if (dstStride == srcStride)
            {
                unsafe
                {
                    Buffer.MemoryCopy((void*)srcBase, (void*)mapped.DataPointer,
                        (long)dstStride * px.Height, (long)srcStride * px.Height);
                }
            }
            else
            {
                for (var row = 0; row < px.Height; row++)
                {
                    unsafe
                    {
                        Buffer.MemoryCopy((void*)(srcBase + (long)row * srcStride),
                            (void*)(mapped.DataPointer + (long)row * dstStride), dstStride, srcStride);
                    }
                }
            }
        }
        finally
        {
            ctx.UnmapSubresource(ch.Texture!, 0);
        }

        ch.Sender!.SendTexture(ch.Texture!.NativePointer);
        ch.LastTexture = ch.Texture!.NativePointer; // 暂停态保活复用
    }

    private static void EnsureSurface(ref SKSurface? s, int w, int h)
    {
        w = Math.Max(1, w);
        h = Math.Max(1, h);
        if (s != null && s.Canvas.DeviceClipBounds.Width == w && s.Canvas.DeviceClipBounds.Height == h) return;
        s?.Dispose();
        // BGRA 8888 + Premul：与 D3D11 B8G8R8A8_UNorm 字节序一致（原样透传给接收端）
        s = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
    }

    private static void EnsureTexture(Channel ch, int w, int h)
    {
        if (ch.Texture != null && ch.TexW == w && ch.TexH == h) return;
        ch.Texture?.Dispose();
        ch.Texture = new Texture2D(ch.Device, new Texture2DDescription
        {
            Width = w,
            Height = h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.Write,
            OptionFlags = ResourceOptionFlags.None,
        });
        ch.TexW = w;
        ch.TexH = h;
    }

    // ---------------- 通道状态 ----------------

    private sealed class Channel : IDisposable
    {
        public readonly string Name;
        public readonly int W;
        public readonly int H;
        public readonly int IntervalMs;
        public readonly bool Transparent;

        /// <summary>字幕通道：画 SubtitleLayer（透明底字幕层），不跑引擎渲染。</summary>
        public readonly bool Subtitle;
        public readonly Device Device;

        /// <summary>光栅路线：true = GPU 面（宿主共享纹理直发）；运行期出错可降回 CPU 面。</summary>
        public bool Gpu;

        /// <summary>GPU 路连续失败计数：满 3 次本通道降回 CPU 面。</summary>
        public int GpuErrors;

        public readonly SpoutSender Sender;
        public readonly Renderer? Renderer; // 字幕通道不需要引擎渲染器
        public SKSurface? Scene;  // plan 画幅渲染面（保留，尺寸/画幅变化才换新；仅引擎通道）
        public int SceneW = -1;
        public int SceneH = -1;
        public SKSurface? Out;   // CPU 输出面（PeekPixels 源；从不交给 Frame，也从不被 Snapshot）
        public SubtitleLayer? SubLayer; // 字幕层缓存（按字幕版本 + plan 重建）
        public long SubLayerVersion = -1;
        public Plan? SubLayerPlan; // 注音行来自 plan 的 cut 注音：重规划（plan 换对象）也要重建
        public Texture2D? Texture;
        public int TexW;
        public int TexH;
        public long LastVersion = -1; // 泵的暂停态脏判断
        public long Frames;
        public long KeepAlives;       // 暂停态保活投递次数（不重渲，只重发上一帧纹理）

        // 慢帧降质（对位前端 draw() 的 S.slow：帧间隔 >30ms 记慢、<14ms 持续 1.2s 才退出）。
        // 慢态下出帧走 fast/noGhost —— 前端是「掉质量保帧率」，我们是「掉帧保质量」，补上这一半。
        public bool Slow;
        public long SlowAt;
        public long LastPubTs; // 上次出帧时刻（算帧间隔；暂停时清零，恢复播放的首拍不作数）

        /// <summary>按帧间隔推进慢帧滞回（前端 draw() 的 dt>30 / dt<14 规则）。</summary>
        public void TickSlow(double dtMs, long nowTs)
        {
            if (dtMs > 30)
            {
                if (!Slow) SlowAt = nowTs;
                Slow = true;
            }
            else if (dtMs < 14 && Slow && ElapsedMs(SlowAt, nowTs) > 1200)
            {
                Slow = false;
            }
        }

        /// <summary>上一帧投给 Spout 的纹理指针（暂停态保活直接重发它，不重渲）。</summary>
        public IntPtr LastTexture;

        // 5s 窗口耗时归因（渲染 / 等 GPU 落盘 / 镜像读回 / 等引擎闸）
        public double SumRender, SumFlush, SumMirror, SumGate;

        public Channel(string name, int w, int h, int fps, bool transparent, bool subtitle, Device device, bool gpu)
        {
            Name = name;
            W = Math.Max(64, w);
            H = Math.Max(64, h);
            IntervalMs = Math.Max(MinIntervalMs, 1000 / Math.Max(1, fps));
            Transparent = transparent;
            Subtitle = subtitle;
            Device = device;
            Gpu = gpu;
            if (!subtitle)
            {
                Renderer = new Renderer();
                // 引擎内部中间面（scratch/trans/morph 等）默认走 CPU 面（稳健防 GC 终结器与 GPU 上下文冲突）。
                // 若显式指定 JIZURA_GPU_SCRATCH=1 则实验性启用 GPU 中间面。
                if (gpu && Environment.GetEnvironmentVariable("JIZURA_GPU_SCRATCH") is "1" or "on" or "true")
                {
                    var host = GpuRasterHost.TryGet(out _);
                    if (host != null) Renderer.SurfaceFactory = host.CreateGpuSurface;
                }
            }

            Sender = new SpoutSender();
            Sender.SetSenderName(name);
            if (!Sender.OpenDirectX(device.NativePointer))
                throw new InvalidOperationException($"SpoutSender.OpenDirectX 失败（通道 {name}）");
        }

        public void Dispose()
        {
            try
            {
                Sender.Release();
            }
            catch (Exception ex)
            {
                Log.Debug($"[SpoutOut] {Name} Sender.Release 失败：{ex.Message}");
            }

            try
            {
                Sender.Dispose();
            }
            catch (Exception ex)
            {
                Log.Debug($"[SpoutOut] {Name} Sender.Dispose 失败：{ex.Message}");
            }

            // 渲染器先释放（其整帧缓存快照引用着场景面），再释放 Skia 面
            Renderer?.Dispose();
            Scene?.Dispose();
            Out?.Dispose();
            Texture?.Dispose();
        }
    }
}
