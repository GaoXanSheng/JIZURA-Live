using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using JizuraLive.Pv;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Effects;
using JizuraLive.Render.Gpu;
using JizuraLive.Spout;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using DCommonAlphaMode = Vortice.DCommon.AlphaMode;
using DCommonPixelFormat = Vortice.DCommon.PixelFormat;

namespace SpoutProbe;

/// <summary>
/// LEAK 模式（<c>--leak</c>）：对**真实运行中的外部 Spout 源**做分档二分，定位
/// 「静态画面下工作集仍持续增长」到底在哪一层。
///
/// 为什么必须有这一段：用户实测应用开着、画面基本静止时内存还在涨（修复前 +1.95 MB/分钟、
/// 修复后仍 +1.01 MB/分钟 ≈ 230 字节/帧 @75fps）。这个量级像「每帧新建一个小的 COM/native 对象
/// 且不释放」，不像整张大纹理。而此前的所有长跑都跑在「收不到真实源」的状态上，刚好把
/// 真实源这条路径（native 直收 + D2D 位图包装）整段排除在测量之外——所以必须对这层单独造探针。
///
/// 三档（每档独立建/拆自己的资源，档间留 3 秒 GC 沉降；每档 40 秒，每 10 秒一行 [sample]）：
///   A：只跑 native 收帧——<see cref="SpoutIn"/> + 每帧 <c>Pump(true, 名字)</c>，自有纹理，不碰 D2D。
///   B：A + 每帧把 <c>FrontTexture</c> 包成 D2D 位图并画到一个小目标位图上（走生产类
///      <see cref="SpoutSourceProvider"/>，位图按 (代次, 槽位) 缓存——与生产侧同一套生命周期）。
///   C：完整 <see cref="PvPipeline"/>（默认场景 + 默认后期链 + 该 Spout 源作为角色层）+ Prepare/Composite。
///
/// 另有一段 [W] 取证：<c>new ID3D11Texture2D(IntPtr)</c> 是否 AddRef——即 SpoutIn.Pump 里
/// 「每帧 using 一个 COM 包装」到底配不平。不配平是**正确性**问题（错减发送端对象的引用计数），
/// 不是往上涨的内存；所以必须用引用计数实验把它与真正的泄漏分开，不能凭记忆断言。
///
/// 斜率口径与 tools/PerfBench/MemCurve.cs 同源（最小二乘，MB/分钟；NaN = 不可判定不给 0），
/// 但本文件自带一份实现——SpoutProbe 只引用主工程，不引用 PerfBench 工程。
/// </summary>
internal sealed class LeakBisectProbe
{
    /// <summary>每档时长（秒）。</summary>
    private const double TierSeconds = 40.0;

    /// <summary>[sample] 行间隔（秒）。</summary>
    private const double SampleSeconds = 10.0;

    /// <summary>
    /// 每档采样窗口前的预热（秒）：帧照跑但不采样。首次建 D2D 位图、D2D 为被包面做的一次性快照
    /// 这类一次性开销不能混进稳态斜率（口径与 PerfBench 的预热一致）。
    /// </summary>
    private const double WarmupSeconds = 10.0;

    /// <summary>档与档之间的 GC 沉降（秒）：上一档的托管垃圾与 finalizer 队列不清干净，下一档起点会被顶高。</summary>
    private const double SettleSeconds = 3.0;

    /// <summary>目标帧率：与用户实测的 ~75fps 同量级，好让「MB/分钟」与实测数字可比。</summary>
    private const double TargetFps = 75.0;

    /// <summary>B/C 档的绘制目标尺寸：只要覆盖「把源位图贴到目标」这一步，不需要生产分辨率。</summary>
    private const int TargetW = 640;
    private const int TargetH = 360;

    private readonly D3DDevice _gpu;
    private readonly D2DHost _d2d;
    private readonly string? _onlySender;
    private bool _vramWarned;

    public LeakBisectProbe(D3DDevice gpu, D2DHost d2d, string? onlySender)
    {
        _gpu = gpu;
        _d2d = d2d;
        _onlySender = string.IsNullOrWhiteSpace(onlySender) ? null : onlySender.Trim();
    }

    // ===================================================================== Run

    public int Run()
    {
        Console.WriteLine("======================================================================");
        Console.WriteLine("[leak] 静态画面上仍增长的内存 · 分档二分探针（A 收帧 / B 收帧+D2D 包装 / C 完整管线）");
        Console.WriteLine($"[leak] 每档 {TierSeconds:F0}s｜每 {SampleSeconds:F0}s 一行 [sample]（形如 PerfBench）｜"
                          + $"档间 GC 沉降 {SettleSeconds:F0}s｜目标 {TargetFps:F0}fps");
        Console.WriteLine("======================================================================");

        var name = ResolveSource();
        if (name is null)
        {
            Console.WriteLine("[leak] 结论：当前没有可用的 Spout 源 → 三档全 N/A（先启动 VTS/Shoot 等真实发送端再重跑）");
            return 8;
        }

        Console.WriteLine($"[leak] 源：'{name}'｜硬件：{_gpu.AdapterName}｜软件光栅={_gpu.IsSoftware}");

        // 先做引用计数取证：SpoutIn.Pump 每帧 wrap 一个 COM 包装——配不配平会直接影响本探针读数的解释
        WrapRefCountForensics(name);

        var a = RunTier("A", "native 收帧（SpoutIn.Pump，自有纹理，不碰 D2D）", () => BuildA(name));
        var b0 = RunTier("B0", "A + 每帧取图（生产类 SpoutSourceProvider，位图包好但不画）", () => BuildB0(name));
        var b = RunTier("B", "B0 + 每帧把缓存位图画到小目标（复现生产侧包装与绘制）", () => BuildB(name));
        var c = RunTier("C", "完整 PvPipeline（默认场景 + 默认后期链 + 该 Spout 源作为角色层）", () => BuildC(name));

        PrintSummary(a, b0, b, c);
        return 0;
    }

    /// <summary>枚举活跃发送端，取 --name 指定的那个（没指定就取第一个）。没有可用源返回 null。</summary>
    private string? ResolveSource()
    {
        var senders = SpoutNames.Enumerate(refresh: true);
        Console.WriteLine($"[leak] 活跃发送端 {senders.Count} 个："
                          + $"{(senders.Count == 0 ? "（无）" : string.Join("、", senders))}");
        if (senders.Count == 0) return null;

        if (_onlySender is null) return senders[0];
        foreach (var s in senders)
        {
            if (s.Equals(_onlySender, StringComparison.OrdinalIgnoreCase)) return s;
        }

        Console.WriteLine($"[leak] 指定的 --name '{_onlySender}' 不在活跃列表里，仍按它试跑（可能全档 N/A）");
        return _onlySender;
    }

    // ============================================================ 档运行骨架

    /// <summary>一档的资源句柄：Tick 是每帧要跑的那一段；Dispose 在档末拆资源并打印该档的量化备注。</summary>
    private sealed class TierHandle
    {
        public required Action Tick { get; init; }
        public required Action Dispose { get; init; }
        public string Note { get; init; } = "";
    }
    private TierResult RunTier(string tag, string title, Func<TierHandle?> build)
    {
        var r = new TierResult { Tag = tag, Title = title };
        Console.WriteLine("----------------------------------------------------------------------");
        Console.WriteLine($"[leak] 档 {tag}：{title}");
        Console.WriteLine($"[leak] 档 {tag} 前 GC 沉降 {SettleSeconds:F0}s（不把上一档的垃圾算进本档起点）");
        Settle();

        TierHandle? handle;
        try
        {
            handle = build();
        }
        catch (Exception ex)
        {
            r.NaReason = ex.Message;
            Console.WriteLine($"[leak] 档 {tag} 建立失败 → N/A：{ex.Message}");
            return r;
        }

        if (handle is null)
        {
            r.NaReason = "档不可用（上方已给原因）";
            return r;
        }

        r.Available = true;
        r.Note = handle.Note;

        var intervalTicks = (long)(1000.0 / TargetFps * Stopwatch.Frequency / 1000.0);

        // 托管堆基线：与工作集判别配对——工作集涨而托管堆平 = 增长在原生/COM 侧；
        // 两者同涨 = 有托管对象被长期持有（GC 收不掉的那种，也是泄漏）。
        r.HeapBefore = GC.GetTotalMemory(forceFullCollection: true);

        // 预热：帧照跑但不采样。首次建 D2D 位图、D2D 为被包面做的一次性快照都发生在这里，
        // 把它们算进斜率会把「一次性分配」读成「泄漏」（B 档第一版就踩了这个坑：头 10s +10MB）。
        Console.WriteLine($"[leak] 档 {tag} 预热 {WarmupSeconds:F0}s（帧照跑，不采样）");
        var warmSw = Stopwatch.StartNew();
        var warmFrames = 0;
        while (warmSw.Elapsed.TotalSeconds < WarmupSeconds)
        {
            var ws = Stopwatch.GetTimestamp();
            handle.Tick();
            warmFrames++;
            Pace(ws, intervalTicks);
        }

        r.WarmupFrames = warmFrames;

        var gc0Base = GC.CollectionCount(0);
        var gc1Base = GC.CollectionCount(1);
        var gc2Base = GC.CollectionCount(2);
        var allocBase = GC.GetTotalAllocatedBytes(precise: false);

        // t=0 锚点：只靠 10/20/30/40s 四个点在 40s 窗口里做回归，噪声偏大；加一个起点锚能把截距钉住
        r.Samples.Add(MakeSample(0.0, 0, gc0Base, gc1Base, gc2Base));
        var framesAtSample = 0;

        var sw = Stopwatch.StartNew();
        var nextSampleMs = SampleSeconds * 1000.0;

        while (true)
        {
            var frameStart = Stopwatch.GetTimestamp();
            handle.Tick();
            r.Frames++;

            var elapsedMs = sw.Elapsed.TotalMilliseconds;
            if (elapsedMs >= nextSampleMs)
            {
                nextSampleMs += SampleSeconds * 1000.0;
                r.Samples.Add(MakeSample(elapsedMs / 60000.0, r.Frames - framesAtSample, gc0Base, gc1Base, gc2Base));
                framesAtSample = r.Frames;
            }

            if (elapsedMs >= TierSeconds * 1000.0) break;
            Pace(frameStart, intervalTicks);
        }

        // 档末补一次采样：主循环可能在最后一行 [sample] 之后才到 40s，缺了它最后 ~10s 的趋势不参与回归
        if (r.Frames > framesAtSample)
            r.Samples.Add(MakeSample(sw.Elapsed.TotalMinutes, r.Frames - framesAtSample, gc0Base, gc1Base, gc2Base));

        r.Fps = r.Frames / Math.Max(1e-6, sw.Elapsed.TotalSeconds);
        r.SlopeMbPerMinute = SlopeMinutes(r.Samples);
        r.BytesPerFrame = SlopeFrames(r.Samples);
        r.ManagedBytesPerFrame = (GC.GetTotalAllocatedBytes(precise: false) - allocBase) / (double)Math.Max(1, r.Frames);
        r.VramStart = r.Samples[0].Vram;
        r.VramEnd = r.Samples[^1].Vram;

        // 关键判别：强制一次 GC 再看工作集。托管垃圾（每帧 new 的 float[]/图对象）会掉回去；
        // 原生/COM 泄漏不会。没有这一步，「每帧分配」与「每帧泄漏」分不开。
        r.WsBeforeGc = Process.GetCurrentProcess().WorkingSet64;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Thread.Sleep(300);
        r.WsAfterGc = Process.GetCurrentProcess().WorkingSet64;
        r.HeapAfterGc = GC.GetTotalMemory(forceFullCollection: true);
        r.WsStart = r.Samples[0].WorkingSet;

        handle.Dispose();

        Console.WriteLine($"[leak] 档 {tag} 结果：{r.Frames} 帧（预热 {warmFrames} 帧）｜实测 {r.Fps:F1}fps｜"
                          + $"斜率 {SlopeText(r.SlopeMbPerMinute)}｜{r.BytesPerFrame:F1} 字节/帧｜"
                          + $"托管分配 {r.ManagedBytesPerFrame:F1} 字节/帧｜{Verdict(r.SlopeMbPerMinute)}");
        Console.WriteLine($"[leak] 档 {tag} 判别：起点 ws={Mb(r.WsStart)}MB｜档末 ws={Mb(r.WsBeforeGc)}MB｜"
                          + $"强制 GC 后 ws={Mb(r.WsAfterGc)}MB｜GC 后相对起点 {Mb(r.WsAfterGc - r.WsStart)}MB"
                          + $"｜托管堆 {Mb(r.HeapBefore)}→{Mb(r.HeapAfterGc)}MB（+{Mb(r.HeapAfterGc - r.HeapBefore)}MB）");
        return r;
    }

    private static string Mb(long bytes) => (bytes / 1048576.0).ToString("F1", CultureInfo.InvariantCulture);

    /// <summary>把一帧节流到目标帧率：先 Sleep(1) 再用自旋收尾，避免 Windows 默认 15.6ms 定时器粒度把帧率压到 ~64。</summary>
    private static void Pace(long frameStartTicks, long intervalTicks)
    {
        while (true)
        {
            var remain = intervalTicks - (Stopwatch.GetTimestamp() - frameStartTicks);
            if (remain <= 0) return;
            var remainMs = remain * 1000.0 / Stopwatch.Frequency;
            if (remainMs > 2.0) Thread.Sleep(1);
            else Thread.SpinWait(400);
        }
    }

    /// <summary>档间沉降：把托管垃圾与 finalizer 队列推干净，再等几秒让工作集落回稳态。</summary>
    private static void Settle()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Thread.Sleep((int)(SettleSeconds * 1000));
    }

    // ================================================================ 档 A/B/C

    /// <summary>A 档：只 native 收帧。<see cref="SpoutIn.Pump"/> 每次成功都会用自有纹理直收一帧。</summary>
    private TierHandle? BuildA(string name)
    {
        var rx = new SpoutIn(_gpu.Device);
        var received = 0;

        // 先连上：连不上就没有「每帧收帧」这条路可测，记 N/A 而不是记 0
        for (var i = 0; i < 300 && rx.FrontTexture is null; i++)
        {
            rx.Pump(true, name);
            Thread.Sleep(10);
        }

        if (rx.FrontTexture is null)
        {
            rx.Dispose();
            Console.WriteLine($"      A 档 N/A：源 '{name}' 3s 内没有交出任何帧");
            return null;
        }

        var note = $"源 {rx.SourceWidth}×{rx.SourceHeight}｜格式值 {rx.SourceFormat}";
        return new TierHandle
        {
            Note = note,
            Tick = () => { if (rx.Pump(true, name)) received++; },
            Dispose = () =>
            {
                rx.Dispose();
                Console.WriteLine($"      A 档收帧成功 {received} 次");
            },
        };
    }

    /// <summary>
    /// B0 档：A + 每帧取图（生产类 <see cref="SpoutSourceProvider"/> 把自有纹理包成 D2D 位图），但**不绘制**。
    /// 把「D2D 包装 / QueryInterface」与「D2D 绘制」两步拆开：B 涨而 B0 不涨，泄漏就在绘制那一步。
    /// </summary>
    private TierHandle? BuildB0(string name)
    {
        var provider = new SpoutSourceProvider("leak_b0", _gpu, _d2d.Ctx, name, premultiplied: true);
        var ctx = new PvFrameContext(0, 0, 0, TargetW, TargetH);

        for (var i = 0; i < 300 && provider.Pump(ctx).Bitmap is null; i++)
        {
            provider.PumpFrame();
            Thread.Sleep(10);
        }

        if (provider.Pump(ctx).Bitmap is null)
        {
            provider.Dispose();
            Console.WriteLine($"      B0 档 N/A：源 '{name}' 3s 内没有可包装的帧");
            return null;
        }

        var got = 0;
        return new TierHandle
        {
            Note = $"源 {provider.SourceWidth}×{provider.SourceHeight}｜只取图不绘制",
            Tick = () =>
            {
                provider.PumpFrame();
                if (provider.Pump(ctx).Bitmap is not null) got++;
            },
            Dispose = () =>
            {
                provider.Dispose();
                Console.WriteLine($"      B0 档取到图 {got} 帧");
            },
        };
    }

    /// <summary>
    /// B 档：收帧 + D2D 包装与绘制。直接用生产类 <see cref="SpoutSourceProvider"/>，
    /// 这样位图的缓存键 (代次, 槽位) 与生产侧完全同源——不另造一套包装，免得测的不是生产路径。
    /// </summary>
    private TierHandle? BuildB(string name)
    {
        var provider = new SpoutSourceProvider("leak_b", _gpu, _d2d.Ctx, name, premultiplied: true);
        var ctx = new PvFrameContext(0, 0, 0, TargetW, TargetH);

        for (var i = 0; i < 300 && provider.Pump(ctx).Bitmap is null; i++)
        {
            provider.PumpFrame();
            Thread.Sleep(10);
        }

        if (provider.Pump(ctx).Bitmap is null)
        {
            provider.Dispose();
            Console.WriteLine($"      B 档 N/A：源 '{name}' 3s 内没有可包装的帧");
            return null;
        }

        var props = new BitmapProperties1(
            new DCommonPixelFormat(Format.B8G8R8A8_UNorm, DCommonAlphaMode.Premultiplied),
            96f, 96f, BitmapOptions.Target);
        var target = _d2d.Ctx.CreateBitmap(new SizeI(TargetW, TargetH), props);
        var draw = _d2d.Draw;
        var rect = new Rect(0, 0, TargetW, TargetH);
        var drawn = 0;

        return new TierHandle
        {
            Note = $"源 {provider.SourceWidth}×{provider.SourceHeight}｜目标 {TargetW}×{TargetH}",
            Tick = () =>
            {
                provider.PumpFrame();
                var image = provider.Pump(ctx);
                if (image.Bitmap is null) return;

                draw.SetTarget(target, TargetW, TargetH);
                draw.Begin();
                draw.Clear(new Color4(0f, 0f, 0f, 1f));
                draw.DrawBitmap(image.Bitmap, rect);
                draw.End();
                drawn++;
            },
            Dispose = () =>
            {
                draw.SetTarget(null, 1, 1); // 摘掉目标：别让 Immediate2D 还抓着一张要释放的位图
                target.Dispose();
                provider.Dispose();
                Console.WriteLine($"      B 档画出 {drawn} 帧");
            },
        };
    }

    /// <summary>
    /// C 档：完整生产稳态。默认场景的角色层本来就指向 VTubeStudioSpout，这里把它的源名对齐到实际枚举到的那个，
    /// 于是「默认场景 + 默认后期链 + 该 Spout 源作为图层」一次成立；字幕/歌词不接（传 null），
    /// 若本档斜率与 B 档相当，字幕/歌词就**不是**本泄漏源。
    /// </summary>
    private TierHandle? BuildC(string name)
    {
        var scene = PvScene.CreateDefault();
        var character = scene.Layers.FirstOrDefault(l => l.Id == PvScene.CharacterLayerId);
        if (character is null)
        {
            Console.WriteLine("      C 档 N/A：默认场景没有角色层");
            return null;
        }

        character.SpoutSenderName = name;
        var effects = EffectGraph.CreateDefaultPost();

        PvPipeline pipe;
        try
        {
            pipe = new PvPipeline(_gpu, scene, effects, subtitleSource: null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"      C 档 N/A：管线建立失败（{ex.Message}）");
            return null;
        }

        if (!pipe.Ready)
        {
            Console.WriteLine($"      C 档 N/A：管线未就绪（{pipe.Error}）");
            pipe.Dispose();
            return null;
        }

        // 目标位图必须由管线自己的 D2D 上下文建（管线内部另建了一个 D2DHost）：跨上下文的位图不能当绘制目标
        var props = new BitmapProperties1(
            new DCommonPixelFormat(Format.B8G8R8A8_UNorm, DCommonAlphaMode.Premultiplied),
            96f, 96f, BitmapOptions.Target);
        var target = pipe.D2DContext.CreateBitmap(new SizeI(TargetW, TargetH), props);

        // 等 Spout 图层连上源：连不上本档就退化成「空管线跑效果链」，测的不是目标路径
        var wait = Stopwatch.StartNew();
        var live = false;
        while (wait.Elapsed.TotalSeconds < 5 && !live)
        {
            pipe.Prepare(wait.Elapsed.TotalSeconds);
            pipe.Composite(target, TargetW, TargetH);
            live = pipe.SpoutState.Live > 0;
            Thread.Sleep(10);
        }

        if (!live)
        {
            target.Dispose();
            pipe.Dispose();
            Console.WriteLine($"      C 档 N/A：5s 内 Spout 图层没有连上源 '{name}'");
            return null;
        }

        var time = 0.0;
        var frames = 0;
        return new TierHandle
        {
            Note = $"画布 {scene.Width}×{scene.Height}｜效果 {effects.Nodes.Count} 个｜目标 {TargetW}×{TargetH}",
            Tick = () =>
            {
                time += 1.0 / TargetFps;
                pipe.Prepare(time);
                pipe.Composite(target, TargetW, TargetH);
                frames++;
            },
            Dispose = () =>
            {
                // 先读回执再拆：Dispose 会清空 provider 表，之后再问就只剩 (0,0)
                var state = pipe.SpoutState;
                var stats = pipe.Renderer.FxPool.GetStats();
                var fxBitmaps = pipe.Renderer.FxBitmapsCreated;
                target.Dispose();
                pipe.Dispose();
                Console.WriteLine($"      C 档合成 {frames} 帧｜Spout 图层 {state.Live}/{state.Total} 在连");
                Console.WriteLine($"      C 档效果池：空闲 {stats.FreeCount}｜租出 {stats.LeasedCount}｜"
                                  + $"已分配 {stats.AllocatedBytes / 1048576.0:F1}MB｜淘汰 {stats.Evictions}｜"
                                  + $"重复归还 {stats.DoubleReturns}｜fx 位图创建数 {fxBitmaps}");
            },
        };
    }

    // ======================================================== 引用计数取证 [W]

    /// <summary>[UnmanagedFunctionPointer] 的 IUnknown AddRef/Release 签名（x64 上 StdCall 即默认调用约定）。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint RefFn(IntPtr self);

    /// <summary>
    /// 读一个 COM 对象的当前引用计数：AddRef 的返回值是**加后**的计数，再 Release 还原，所以净读数 = 返回值 - 1。
    /// 这是「不改变对象状态就能读到引用计数」的唯一无副作用手法（IUnknown vtable：0=QueryInterface 1=AddRef 2=Release）。
    /// </summary>
    private static int ReadRefCount(IntPtr p)
    {
        if (p == IntPtr.Zero) return -1;
        var vtbl = Marshal.ReadIntPtr(p);
        var addRef = Marshal.GetDelegateForFunctionPointer<RefFn>(Marshal.ReadIntPtr(vtbl, 1 * IntPtr.Size));
        var release = Marshal.GetDelegateForFunctionPointer<RefFn>(Marshal.ReadIntPtr(vtbl, 2 * IntPtr.Size));
        var after = addRef(p);
        release(p);
        return (int)after - 1;
    }

    /// <summary>只加不减的 AddRef（预置保底引用用；配对释放留给进程退出）。</summary>
    private static void AddRefRaw(IntPtr p)
    {
        var vtbl = Marshal.ReadIntPtr(p);
        var addRef = Marshal.GetDelegateForFunctionPointer<RefFn>(Marshal.ReadIntPtr(vtbl, 1 * IntPtr.Size));
        addRef(p);
    }

    /// <summary>
    /// 取证 <c>new ID3D11Texture2D(IntPtr)</c> 是否 AddRef。两条独立证据：
    ///   ① 自有纹理上跑 1000 次 wrap+Dispose，看引用计数净变化（事先 AddRef 一万次兜底：
    ///      无论净变化朝哪个方向，都不会把对象打到 0 而崩溃）；
    ///   ② 真实源纹理上只 wrap+Dispose **一次**（这正是生产每帧的动作），看净变化。
    /// </summary>
    private void WrapRefCountForensics(string name)
    {
        Console.WriteLine("----------------------------------------------------------------------");
        Console.WriteLine("[W] 取证：new ID3D11Texture2D(IntPtr) 是否 AddRef（SpoutIn.Pump 每帧 wrap 的包装配不配平）");

        try
        {
            using var tex = _gpu.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = 64,
                Height = 64,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget,
                CPUAccessFlags = CpuAccessFlags.None,
            });

            var p = tex.NativePointer;
            const int guard = 10_000;
            const int loop = 1_000;

            var before = ReadRefCount(p);
            for (var i = 0; i < guard; i++) AddRefRaw(p);
            var guarded = ReadRefCount(p);

            for (var i = 0; i < loop; i++)
            {
                using var w = new ID3D11Texture2D(p);
                _ = w.Description; // 真正用一下，免得 JIT/优化把包装优化掉
            }

            var afterLoop = ReadRefCount(p);
            var perWrap = (afterLoop - guarded) / (double)loop;
            Console.WriteLine($"      ① 自有 64×64 纹理：初始 {before}｜预 AddRef {guard} 后 {guarded}｜"
                              + $"wrap+Dispose {loop} 次后 {afterLoop}｜每次净变化 {perWrap:+0.000;-0.000;0.000}");
            Console.WriteLine(perWrap == 0
                ? "         → 结论：构造/析构**配平**（要么构造 AddRef 且 Dispose 释放，要么谁都不动）"
                : $"         → 结论：**不配平**——每次 wrap+Dispose 净改变引用计数 {perWrap:+0;-0}，"
                  + "生产每帧这么做就是在持续错改发送端对象的引用计数");
        }
        catch (Exception ex)
        {
            Console.WriteLine("      ① 自有纹理取证异常：" + ex.Message);
        }

        // ② 真实源指针上只测一次：与生产一模一样的一个「包装 → 读 Description → 释放」回合
        try
        {
            var raw = new SpoutDXWrapper.SpoutReceiver();
            try
            {
                raw.SetReceiverName(name);
                if (!raw.OpenDirectX(_gpu.Device.NativePointer))
                {
                    Console.WriteLine("      ② 真实源：接收端打不开 → 跳过");
                    return;
                }

                var p = IntPtr.Zero;
                for (var i = 0; i < 300 && p == IntPtr.Zero; i++)
                {
                    p = raw.ReceiveTexturePtr();
                    Thread.Sleep(10);
                }

                if (p == IntPtr.Zero)
                {
                    Console.WriteLine("      ② 真实源：3s 内没拿到源指针 → 跳过");
                    return;
                }

                var b0 = ReadRefCount(p);
                using (var w = new ID3D11Texture2D(p))
                {
                    _ = w.Description;
                }

                var b1 = ReadRefCount(p);
                Console.WriteLine($"      ② 真实源纹理：一个 wrap+Dispose 回合前引用计数 {b0}｜后 {b1}｜"
                                  + $"净变化 {b1 - b0:+#;-#;0}"
                                  + (b1 == b0 ? "（配平）" : "（**不配平**）"));
            }
            finally
            {
                raw.Dispose();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("      ② 真实源取证异常：" + ex.Message);
        }
    }

    // ============================================================== 采样与回归

    /// <summary>一次工作集采样（纯数据；[sample] 行的原始读数，也是回归的点来源）。</summary>
    private readonly record struct TierSample(
        double Minute,
        int Frames,
        long WorkingSet,
        long Vram,
        int Gc0,
        int Gc1,
        int Gc2)
    {
        /// <summary>与 PerfBench 同款的 stdout 采样行（vram=-1 = 不可得，不记 0）。</summary>
        public string Line() =>
            $"[sample] {(int)Minute}min ws={Mb(WorkingSet)}MB"
            + $" vram={(Vram < 0 ? "-1" : Mb(Vram))}MB gc={Gc0}/{Gc1}/{Gc2} frames={Frames}";

        private static string Mb(long bytes) => (bytes / 1048576.0).ToString("F1", CultureInfo.InvariantCulture);
    }

    private TierSample MakeSample(double minute, int framesInInterval, int gc0Base, int gc1Base, int gc2Base)
    {
        var sample = new TierSample(
            minute,
            framesInInterval,
            Process.GetCurrentProcess().WorkingSet64,
            QueryVram(),
            GC.CollectionCount(0) - gc0Base,
            GC.CollectionCount(1) - gc1Base,
            GC.CollectionCount(2) - gc2Base);
        Console.WriteLine(sample.Line());
        return sample;
    }

    /// <summary>显存采样：IDXGIAdapter3.QueryVideoMemoryInfo(0, Local) 的 CurrentUsage；不可得记 -1。</summary>
    private long QueryVram()
    {
        try
        {
            if (_gpu.Adapter is null) return -1;
            using var adapter3 = _gpu.Adapter.QueryInterfaceOrNull<IDXGIAdapter3>();
            if (adapter3 is null) return -1;
            return (long)adapter3.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local).CurrentUsage;
        }
        catch (Exception ex)
        {
            if (!_vramWarned)
            {
                _vramWarned = true;
                Console.WriteLine($"[leak][WARN] 显存采样不可得（记 -1）：{ex.Message}");
            }

            return -1;
        }
    }

    /// <summary>最小二乘斜率（MB/分钟），口径与 PerfBench/MemCurve.SlopeMbPerMinute 一致；点数不足给 NaN。</summary>
    private static double SlopeMinutes(IReadOnlyList<TierSample> pts)
    {
        if (pts.Count < 2) return double.NaN;
        double sumX = 0, sumY = 0;
        foreach (var p in pts)
        {
            sumX += p.Minute;
            sumY += p.WorkingSet / 1048576.0;
        }

        var meanX = sumX / pts.Count;
        var meanY = sumY / pts.Count;
        double sxy = 0, sxx = 0;
        foreach (var p in pts)
        {
            var dx = p.Minute - meanX;
            sxy += dx * (p.WorkingSet / 1048576.0 - meanY);
            sxx += dx * dx;
        }

        return sxx > 0 ? sxy / sxx : double.NaN;
    }

    /// <summary>以帧序号为 x 的最小二乘斜率（字节/帧）：泄漏若按帧发生，MB/分钟会随帧率漂，这个数不随。</summary>
    private static double SlopeFrames(IReadOnlyList<TierSample> pts)
    {
        if (pts.Count < 2) return double.NaN;
        double sumX = 0, sumY = 0;
        foreach (var p in pts)
        {
            sumX += p.Frames;
            sumY += p.WorkingSet;
        }

        var meanX = sumX / pts.Count;
        var meanY = sumY / pts.Count;
        double sxy = 0, sxx = 0;
        foreach (var p in pts)
        {
            var dx = p.Frames - meanX;
            sxy += dx * (p.WorkingSet - meanY);
            sxx += dx * dx;
        }

        return sxx > 0 ? sxy / sxx : double.NaN;
    }

    // ================================================================== 报告

    /// <summary>一档的结论汇总。</summary>
    private sealed class TierResult
    {
        public string Tag { get; init; } = "";
        public string Title { get; init; } = "";
        public string NaReason { get; set; } = "";
        public string Note { get; set; } = "";
        public bool Available { get; set; }
        public List<TierSample> Samples { get; } = new();
        public double SlopeMbPerMinute { get; set; } = double.NaN;
        public double BytesPerFrame { get; set; } = double.NaN;
        public double ManagedBytesPerFrame { get; set; } = double.NaN;
        public double Fps { get; set; }
        public int Frames { get; set; }
        public int WarmupFrames { get; set; }
        public long HeapBefore { get; set; }
        public long HeapAfterGc { get; set; }
        public long WsStart { get; set; }
        public long WsBeforeGc { get; set; }
        public long WsAfterGc { get; set; }
        public long VramStart { get; set; } = -1;
        public long VramEnd { get; set; } = -1;
    }

    private static string SlopeText(double slope) =>
        double.IsNaN(slope) ? "不可得" : slope.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + " MB/分钟";

    /// <summary>分档结论：阈值与 MemCurve 同源（|斜率| &lt; 1 平稳；&gt; 10 疑似泄漏）。</summary>
    private static string Verdict(double slope)
    {
        if (double.IsNaN(slope)) return "不可判定（采样点不足）";
        var m = Math.Abs(slope);
        if (m < 1.0) return "平稳";
        if (m <= 10.0) return slope > 0 ? "缓增需观察" : "缓降需观察";
        return slope > 0 ? "疑似泄漏" : "疑似异常下降";
    }

    private void PrintSummary(TierResult a, TierResult b0, TierResult b, TierResult c)
    {
        Console.WriteLine("======================================================================");
        Console.WriteLine("[leak] 分档斜率汇总（工作集，最小二乘，正 = 增长；每档预热 " + WarmupSeconds.ToString("F0") + "s 不计）");
        Console.WriteLine("[leak] 档  可用  斜率 MB/分钟   字节/帧    实测 fps   帧数   托管分配/帧   GC 后相对起点   说明");
        foreach (var r in new[] { a, b0, b, c }) Console.WriteLine(FormatLine(r));

        Console.WriteLine("----------------------------------------------------------------------");
        Console.WriteLine("[leak] 读法：斜率明显不为 0 且强制 GC 后**不塌**的那一档就是原生泄漏所在层——");
        Console.WriteLine("[leak]   A 非 0 = 收帧层（native 直收）；A 平而 B0 非 0 = D2D 包装层；");
        Console.WriteLine("[leak]   B0 平而 B 非 0 = D2D 绘制层；B 平而 C 非 0 = 管线/效果/合成层。");
        Console.WriteLine("[leak] 「GC 后相对起点」≈ 起点 = 增长是托管垃圾（会被回收，不是泄漏）；");
        Console.WriteLine("[leak]   它明显为正且斜率非 0 = 增长驻留在原生/COM 侧（真泄漏）。");
        Console.WriteLine("[leak] 若 B 与 C 相当，则字幕/歌词层（本档未接）不可能是本泄漏源。");
        Console.WriteLine("======================================================================");
    }

    private static string FormatLine(TierResult r)
    {
        // GC 后相对起点：把「托管垃圾」与「原生泄漏」分开的关键列（NaN 用 —— 表示不可用）
        var gcAfter = r.Available ? Mb(r.WsAfterGc - r.WsStart) : "——";
        if (!r.Available)
            return $"[leak] {r.Tag,-3}  N/A    ——            ——         ——        ——     ——            ——              {r.NaReason}";

        var vram = r.VramStart >= 0 && r.VramEnd >= 0
            ? $"｜显存 {(r.VramStart / 1048576.0):F0}→{(r.VramEnd / 1048576.0):F0}MB"
            : "";
        return $"[leak] {r.Tag,-3}  ✓      {r.SlopeMbPerMinute,12:F2}  {r.BytesPerFrame,9:F1}  "
               + $"{r.Fps,8:F1}  {r.Frames,6}  {r.ManagedBytesPerFrame,11:F1}  {gcAfter,12}MB  {r.Note}{vram}";
    }
}
