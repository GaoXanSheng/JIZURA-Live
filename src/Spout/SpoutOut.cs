// Spout 直出管线（pure-csharp 分支）：SkiaSharp CPU 光栅 → SpoutDX。
//
// 路径（主通道）：SkiaSharp CPU 光栅（BGRA 8888 SKSurface）→ Renderer.Frame 画 plan 当前帧
//       → PeekPixels 拿到像素 → 逐行拷进 SharpDX D3D11 Texture2D（Dynamic，B8G8R8A8_UNorm）
//       → SpoutDX SpoutSender.SendTexture（内部 GPU 拷进 Spout 共享纹理）。
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

    /// <summary>字幕层时间轴（KRC 逐字）；null = 无字幕数据，字幕通道输出全透明帧。</summary>
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
    private Channel? _main;
    private Channel? _lyrics;
    private Thread? _pump;
    private volatile bool _running;
    private ISpoutSource? _source;

    public bool IsRunning => _running;
    public string? SenderName => _main?.Name;
    public string? LyricsSenderName => _lyrics?.Name;
    public bool LyricsRunning => _lyrics != null;

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

        try
        {
            _device ??= new Device(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        }
        catch (Exception ex)
        {
            Log.Error("[SpoutOut] D3D11 设备创建失败，Spout 输出不可用：" + ex.Message, ex);
            return false;
        }

        try
        {
            _main = new Channel(opt.SenderName, opt.Width, opt.Height, opt.Fps, opt.MainTransparent,
                subtitle: false, _device);
            _lyrics = opt.LyricsEnabled
                ? new Channel(opt.SenderName + "-LYRICS", opt.Width, opt.Height,
                    Math.Max(1, opt.LyricsFps), transparent: true, subtitle: true, _device)
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
            $"[SpoutOut] 管线已启动：主通道={opt.SenderName} {opt.Width}x{opt.Height}@{opt.Fps}"
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
            pump.Join(500);

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
        _device?.Dispose();
        _device = null;
    }

    // ---------------- 泵 ----------------

    private void PumpLoop()
    {
        // 主通道定节拍；字幕通道按自己的 fps 用计时戳滤帧（两条通道共享一个泵线程）
        var main = _main!;
        var lastMain = Stopwatch.GetTimestamp();
        var lastLyrics = Stopwatch.GetTimestamp();
        var lastStat = Stopwatch.GetTimestamp();

        while (_running)
        {
            var src = _source;
            var now = Stopwatch.GetTimestamp();

            // 每 5s 报一次帧数（掉出帧率时日志一眼可见；对齐旧 FrameIntervalStats 的窗口习惯）
            if (ElapsedMs(lastStat, now) >= 5_000)
            {
                var lyrStat = _lyrics;
                Log.Info(
                    $"[SpoutOut] 近 5s：{main.Name} {main.Frames} 帧"
                    + (lyrStat != null ? $"；{lyrStat.Name} {lyrStat.Frames} 帧" : ""));
                lastStat = now;
                main.Frames = 0;
                if (lyrStat != null) lyrStat.Frames = 0;
            }

            try
            {
                if (src != null && src.Plan.Cuts.Count > 0)
                {
                    if (src.IsPlaying)
                    {
                        if (ElapsedMs(lastMain, now) >= main.IntervalMs)
                        {
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
                        // 暂停：画面内容只在 seek/重规划（Version 变）时改变；
                        // 两条通道各自记 Version，重规划后各补一帧。字幕通道还要看字幕数据
                        // 版本（换歌装载字幕时暂停态也得重出一帧）
                        if (Dirty(main, src))
                        {
                            RenderChannel(main, src);
                            lastMain = Stopwatch.GetTimestamp();
                        }

                        var lyr = _lyrics;
                        if (lyr != null && Dirty(lyr, src))
                        {
                            RenderChannel(lyr, src);
                            lastLyrics = Stopwatch.GetTimestamp();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogRenderError(ex);
            }

            // 高精度等待下一拍：粗睡留 2ms 余量，末段自旋补齐（对齐旧 EBF 泵）
            var next = Stopwatch.GetTimestamp()
                       + (long)(PausedPumpMs / 1000.0 * Stopwatch.Frequency);
            if (src is { IsPlaying: true })
                next = Stopwatch.GetTimestamp()
                       + (long)(Math.Max(MinIntervalMs, main.IntervalMs) / 1000.0 * Stopwatch.Frequency);
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

    // ---------------- 单通道渲染：Skia CPU 光栅 → D3D11 → SpoutDX ----------------

    private void RenderChannel(Channel ch, ISpoutSource src)
    {
        var px = ch.Subtitle ? PaintSubtitle(ch, src) : PaintEngine(ch, src);
        Upload(ch, px);
        ch.LastVersion = src.Version;
        ch.Frames++;
    }

    /// <summary>字幕通道：透明底全幅只画字幕层（SubtitleLayer 与导出烤字幕同一个渲染器）。</summary>
    private SKPixmap PaintSubtitle(Channel ch, ISpoutSource src)
    {
        EnsureSurface(ref ch.Out, ch.W, ch.H);
        var oc = ch.Out!.Canvas;
        oc.SetMatrix(SKMatrix.Identity);
        oc.Clear(SKColors.Transparent);

        // 换歌/装载/清空字幕（版本号变）时重建字幕层：注音行从当前 plan 的 cut 注音回填
        if (src.SubtitleVersion != ch.SubLayerVersion)
        {
            ch.SubLayerVersion = src.SubtitleVersion;
            var lines = src.Subtitles;
            ch.SubLayer = lines is { Count: > 0 }
                ? new SubtitleLayer(lines, src.SubtitleConfig, SubtitleLayer.NotesFromPlan(lines, src.Plan))
                : null;
            if (ch.SubLayer == null)
                Log.Info("[SpoutOut] 字幕通道：当前没有 KRC 字幕数据，输出透明帧"
                         + "（字幕层来自酷狗 KRC 逐字；网易云 LRC 不产生）");
            else
                Log.Info($"[SpoutOut] 字幕通道：字幕层就绪，{lines!.Count} 行"
                         + (src.SubtitleConfig.Content == SubContent.Off ? "（内容=off，不出画面）" : ""));
        }

        // 排版按输出分辨率全幅派生（字号 = H 百分比、底部 5.5% 起排），与导出烤字幕所见一致
        ch.SubLayer?.Render(oc, src.CurrentTime, ch.W, ch.H);
        return ch.Out.PeekPixels() ?? throw new InvalidOperationException("字幕面 PeekPixels 返回空");
    }

    private SKPixmap PaintEngine(Channel ch, ISpoutSource src)
    {
        var plan = src.Plan;
        var t = src.CurrentTime;

        // contain 缩放：settings 分辨率与 plan 画幅比不一致时出边（主流 16:9 配 16:9 无痕）。
        // 边带底色：主通道黑（或透明底时透出），暂无配置项
        var scale = Math.Min(ch.W / (double)plan.W, ch.H / (double)plan.H);
        var rw = Math.Max(1, (int)Math.Round(plan.W * scale));
        var rh = Math.Max(1, (int)Math.Round(plan.H * scale));
        var exactFit = rw == ch.W && rh == ch.H;

        // SkiaSharp 3.119 坑（headless 复现：obj/repro，AV 于 Frame 的 _fcCv.Width）：
        // 引擎整帧缓存把 SnapshotOf(ctx) 存起来跨帧用——交给 Frame 的面必须保留（绝不 Dispose），
        // blit 也不用 Snapshot()，改 PeekPixels + FromPixels 借用像素。场景面按尺寸保留，
        // plan 画幅变化时换新面、旧面孤儿化（退出时随渲染器一起释放）
        if (ch.Scene == null || ch.SceneW != rw || ch.SceneH != rh)
        {
            if (ch.Scene != null) ch.Graveyard.Add(ch.Scene);
            ch.Scene = SKSurface.Create(new SKImageInfo(rw, rh, SKColorType.Bgra8888, SKAlphaType.Premul));
            ch.SceneW = rw;
            ch.SceneH = rh;
        }

        lock (EngineRenderGate.Lock)
            ch.Renderer!.Frame(ch.Scene.Canvas, plan, t, new RenderOpts
            {
                Scale = scale,
                Transparent = ch.Transparent,
                Stage = true,
            });

        if (exactFit)
            return ch.Scene.PeekPixels() ?? throw new InvalidOperationException("场景面 PeekPixels 返回空");

        EnsureSurface(ref ch.Out, ch.W, ch.H);
        var oc = ch.Out!.Canvas;
        oc.SetMatrix(SKMatrix.Identity);
        oc.Clear(ch.Transparent ? SKColors.Transparent : SKColors.Black);
        var spx = ch.Scene.PeekPixels() ?? throw new InvalidOperationException("场景面 PeekPixels 返回空");
        using var borrow = SKImage.FromPixels(spx.Info, spx.GetPixels(), spx.RowBytes);
        oc.DrawImage(borrow, (ch.W - rw) / 2f, (ch.H - rh) / 2f);
        return ch.Out.PeekPixels() ?? throw new InvalidOperationException("出图面 PeekPixels 返回空");
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

        public readonly SpoutSender Sender;
        public readonly Renderer? Renderer; // 字幕通道不需要引擎渲染器
        public SKSurface? Scene;  // plan 画幅渲染面（保留，尺寸/画幅变化才换新；仅引擎通道）
        public int SceneW = -1;
        public int SceneH = -1;
        public SKSurface? Out;   // 输出分辨率面（PeekPixels 源；从不交给 Frame，也从不被 Snapshot）
        public readonly List<SKSurface> Graveyard = new(); // 换下的旧场景面（Dispose 时随渲染器一起释放）
        public SubtitleLayer? SubLayer; // 字幕层缓存（按字幕数据版本重建）
        public long SubLayerVersion = -1;
        public Texture2D? Texture;
        public int TexW;
        public int TexH;
        public long LastVersion = -1; // 泵的暂停态脏判断
        public long Frames;

        public Channel(string name, int w, int h, int fps, bool transparent, bool subtitle, Device device)
        {
            Name = name;
            W = Math.Max(64, w);
            H = Math.Max(64, h);
            IntervalMs = Math.Max(MinIntervalMs, 1000 / Math.Max(1, fps));
            Transparent = transparent;
            Subtitle = subtitle;
            Device = device;
            if (!subtitle) Renderer = new Renderer();

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
            foreach (var s in Graveyard) s.Dispose();
            Out?.Dispose();
            Texture?.Dispose();
        }
    }
}
