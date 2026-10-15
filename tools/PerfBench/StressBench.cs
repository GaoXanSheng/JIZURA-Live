using System.Diagnostics;
using System.Globalization;
using JizuraLive.Engine;
using JizuraLive.Logging;
using JizuraLive.Pv;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Effects;
using JizuraLive.Render.Gpu;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace PerfBench;

/// <summary>
/// 压力负载：把产品渲染管线（PvPipeline：图层栈 + 歌词 + 字幕 + 效果链）挂在探针里，
/// 并按节拍持续制造"动态事件"——效果组合轮换、图层增删、字幕断句切换、歌词 seek。
///
/// 为什么需要它：稳态长跑（固定负载跑 10 分钟）只能量出每帧均摊的慢泄漏，
/// 而真实泄漏大多藏在**事件路径**（换效果、建/删图层、源重连、字幕重排这些每次都新建
/// D2D/DWrite/GPU 资源的地方）——稳态下这些路径一次都不跑，占用自然"看起来平稳"。
/// 压力模式让每条事件路径每几秒就被踩一次，慢泄漏在几分钟内就涨得肉眼可见。
///
/// 事件节拍（都由帧时间驱动，无额外线程）：
///   · 每 5s   轮换效果组合（从 EffectPresets 逐个套 + 随机增删单效果节点）
///   · 每 8s   增删一个图层（RequestAdd/RequestRemove 对，跑 ApplyLayerOps 的建/删 provider 路径）
///   · 每 3s   seek 歌词（SetLyrics 换一批 + Prepare 换时刻，踩歌词排版重建）
///   · 每帧    字幕源换内容节拍（SubtitleProvider 的重排路径）
///
/// 注意：图层增删走 PvPipeline 的跨线程命令队列（RequestAdd/RequestRemove），
/// 帧首由 Prepare 消费——与产品工作台同一套接线，探针不另开旁路。
/// </summary>
internal sealed class StressBench : IDisposable
{
    /// <summary>事件节拍（秒）。</summary>
    private const double FxRotaSec = 5.0;
    private const double LayerChurnSec = 8.0;
    private const double SeekSec = 3.0;

    private readonly PresentHost _host;
    private readonly string[] _presetIds;
    private readonly Random _rng;

    private PvPipeline? _pipe;
    private D2DHost? _d2d;
    private readonly EffectGraph _effects = new();
    private readonly PvScene _scene;
    private readonly TestSubtitleSource _subtitleSource = new();

    private int _fxPhase;
    private int _layerSeq;
    private string? _churnLayerId;
    private readonly List<string> _lrcPool = new();
    private int _lrcIdx;
    private double _nextFxAt = FxRotaSec;
    private double _nextLayerAt = LayerChurnSec;
    private double _nextSeekAt = SeekSec;
    private double _time;
    private bool _disposed;

    public StressBench(PresentHost host, int seed)
    {
        _host = host;
        _presetIds = EffectPresets.All.Select(p => p.Id).ToArray();
        _rng = new Random(seed);
        _scene = PvScene.CreateDefault();
        // 池子里放几段假歌词（时间轴递进、行数不同），seek 轮换踩排版重建
        for (var i = 0; i < 6; i++)
        {
            var sb = new System.Text.StringBuilder();
            for (var line = 0; line < 8; line++)
            {
                var start = line * 4 + i * 40;
                sb.Append(CultureInfo.InvariantCulture, $"[{start / 60:00}:{start % 60:00.00}]压力歌词第{i + 1}段第{line + 1}行\n");
            }
            _lrcPool.Add(sb.ToString());
        }
    }

    public bool Ready => _pipe is { Ready: true };

    private void OnReady(PresentHost host)
    {
        var gpu = host.Gpu;
        if (gpu is null) return;

        _d2d = D2DHost.Create(gpu);
        BindTarget(host);              // 先把目标位图绑上，否则 Composite 直接被挡掉 → 黑屏且测不到合成路径
        host.Resized += BindTarget;    // 窗口尺寸变化要重绑（照 Program.Bench 的接线）
        _pipe = new PvPipeline(gpu, _scene, _effects, _subtitleSource);
        _pipe.SetLyrics(LrcParser.Parse(_lrcPool[0]));
        Console.WriteLine("[Stress] 产品管线就绪：图层栈 + 歌词 + 字幕 + 效果链（事件节拍 5s/8s/3s）");
    }

    /// <summary>每帧驱动：先发事件命令（任意线程安全），再走产品 Prepare/Composite 全链。</summary>
    public void Frame(double dt)
    {
        if (_pipe is not { Ready: true }) return;
        _time += dt;

        // —— 事件节拍（帧时间驱动，单线程渲染回调内，无锁） ——
        if (_time >= _nextFxAt) { RotateEffects(); _nextFxAt += FxRotaSec; }
        if (_time >= _nextLayerAt) { ChurnLayer(); _nextLayerAt += LayerChurnSec; }
        if (_time >= _nextSeekAt) { SeekLyrics(); _nextSeekAt += SeekSec; }
        _subtitleSource.Tick(_time); // 每 2s 换一批字幕行，逼重排/绘字路径持续执行

        // —— 产品全链（与工作台同一条路：Prepare 帧首消费图层命令 → 合成） ——
        _pipe.Prepare(_time % 240.0); // 歌词轴 4 分钟循环，seek 有意义
        if (_target is not null) _pipe.Composite(_target, _tw, _th); // BindTarget 在 Ready/Resized 后才有值
    }

    /// <summary>轮换效果组合：整批换成下一个预设，再随机增删一两个单节点（踩效果链重建与参数打包）。</summary>
    private void RotateEffects()
    {
        var preset = EffectPresets.Create(_presetIds[_fxPhase++ % _presetIds.Length]);
        if (preset is null) return;

        _effects.Nodes.Clear();
        foreach (var n in preset.Nodes) _effects.Nodes.Add(n);

        // 顺手加一个随机单效果再删掉：踩 Add/Remove 的实例 Id 分配与节点表变更
        var extra = EffectCatalog.All[_rng.Next(EffectCatalog.All.Count)].Id;
        _effects.Add(extra);
        _effects.Remove(extra);

        Console.WriteLine($"[Stress] 效果轮换 #{_fxPhase - 1}: {_presetIds[(_fxPhase - 1) % _presetIds.Length]} + {extra}(add/remove)");
    }

    /// <summary>图层增删对：先 RequestAdd 一个 Spout 源名（连不上没关系，provider 生命周期照样走全），
    /// 下一个节拍把上一次的删掉。ApplyLayerOps 的建/删 provider 路径就是泄漏最可能藏的地方之一。</summary>
    private void ChurnLayer()
    {
        if (_churnLayerId is not null)
        {
            _pipe?.RequestRemove(_churnLayerId);
            _churnLayerId = null;
            Console.WriteLine("[Stress] 图层删除");
        }
        else
        {
            var id = $"src_{Interlocked.Increment(ref _layerSeq)}";
            _pipe?.RequestAdd($"StressSource-{id}");
            _churnLayerId = id;
            Console.WriteLine("[Stress] 图层新增（无真实发送端，provider 建立后空转）");
        }
    }

    /// <summary>seek 歌词：换下一批歌词 + 推进时刻（踩 SetLyrics 排版重建与时间轴二分）。</summary>
    private void SeekLyrics()
    {
        _lrcIdx = (_lrcIdx + 1) % _lrcPool.Count;
        _pipe?.SetLyrics(LrcParser.Parse(_lrcPool[_lrcIdx]));
        Console.WriteLine($"[Stress] 歌词轮换 #{_lrcIdx}");
    }

    // —— 目标位图绑定（照 Program.Bench 的 BindTarget 形状） ——
    private ID2D1Bitmap1? _target;
    private int _tw = 1280, _th = 720;

    public void BindTarget(PresentHost host)
    {
        _target?.Dispose();
        _target = null;
        if (_d2d is null || host.Target is null) return;
        using var back = host.Target.GetBackBuffer();
        using var surface = back.QueryInterface<Vortice.DXGI.IDXGISurface>();
        _tw = host.Target.Width;
        _th = host.Target.Height;
        _target = _d2d.Ctx.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // 渲染线程契约：这些资源都在渲染线程建，必须在本线程放（Shutdown 事件里调）
        _target?.Dispose();
        _target = null;
        _pipe?.Dispose();
        _pipe = null;
        _d2d?.Dispose();
        _d2d = null;
    }
}

/// <summary>
/// 测试字幕源：字幕行按节拍轮换，逼 SubtitleProvider 每几秒走一次重排/绘字路径
/// （泄漏审计的头号嫌疑就在"每帧新建 DWrite 布局不释放"这类地方）。
/// 接口形状照 IPvSubtitleSource 的真实定义（Lines/Config/Version）。
/// </summary>
internal sealed class TestSubtitleSource : IPvSubtitleSource
{
    private long _version = 1;
    private IReadOnlyList<KrcLine>? _lines;

    public IReadOnlyList<KrcLine>? Lines => _lines;

    public SubtitleConfig? Config { get; } = new SubtitleConfig();

    public long Version => _version;

    /// <summary>换一批字幕行（渲染线程的帧节拍里调）。行数/文本长度刻意不同，逼真实重排而不是复用。</summary>
    public void Tick(double time)
    {
        var batch = (int)(time / 2.0);
        var count = 1 + batch % 3; // 1~3 行轮换
        var lines = new List<KrcLine>();
        for (var i = 0; i < count; i++)
        {
            lines.Add(new KrcLine(
                StartMs: batch * 2000L + i * 500L,
                DurMs: 1800L,
                Text: $"压力字幕#{batch}.{i}：长度刻意不同_{batch * 7 + i % 13}字"));
        }
        _lines = lines;
        _version++;
    }
}

/// <summary>
/// 压力模式的内存采样器：挂在 PresentHost 的事件上，复用 Bench 的采样口径
/// （工作集/显存/GC 同款读数）与 MemCurve 的斜率结论，但采样间隔缩到 10s——
/// 压力模式默认只跑 2 分钟，30s 间隔凑不出足够点数算斜率。
/// </summary>
internal sealed class StressMemProbe : IDisposable
{
    /// <summary>压力模式采样间隔：10s（2 分钟能拿到 ~12 个点，足够算斜率）。</summary>
    private const double SampleIntervalSec = 10.0;

    private readonly PresentHost _host;
    private readonly StressBench _bench;
    private readonly double _warmup;
    private readonly Stopwatch _sw = new();
    private readonly List<MemSample> _samples = new();

    private int _gc0Base, _gc1Base, _gc2Base;
    private long _framesAtSample;
    private double _nextSampleAt;
    private double _warmedAt = -1;
    private double _totalSeconds;

    public StressMemProbe(PresentHost host, StressBench bench, double warmup)
    {
        _host = host;
        _bench = bench;
        _warmup = warmup;
        host.Ready += OnReady;
        host.Frame += OnFrame;
        host.Shutdown += OnShutdown;
    }

    /// <summary>压力事件与产品全链都在 Ready 之后才有宿主可挂。</summary>
    private void OnReady(PresentHost host)
    {
        host.Frame += StressFrame; // Frame 是多播：先跑采样（dt 语义不变），StressFrame 驱动负载
        _sw.Restart();
        Baseline();
        Console.WriteLine($"[Stress] 采样器就绪：每 {SampleIntervalSec:F0}s 一行，预热 {_warmup:F0}s 不计斜率");
    }

    private void StressFrame(PresentHost host, double dt)
    {
        var t = _sw.Elapsed.TotalSeconds;
        _totalSeconds = t;
        if (_warmedAt < 0 && t >= _warmup)
        {
            _warmedAt = t;
            Baseline(); // 预热结束重取 GC 基线：窗口创建/着色器编译的抖动不算进泄漏曲线
        }

        _bench.Frame(dt);

        if (_warmedAt >= 0)
        {
            var since = t - _warmedAt;
            if (since >= _nextSampleAt) TakeSample(since);
        }
    }

    private void TakeSample(double sinceWarmup)
    {
        _nextSampleAt = sinceWarmup + SampleIntervalSec;
        long vram = -1;
        try
        {
            var gpu = _host.Gpu;
            if (gpu?.Adapter is not null)
                using (var a3 = gpu.Adapter.QueryInterfaceOrNull<Vortice.DXGI.IDXGIAdapter3>())
                    if (a3 is not null)
                        vram = (long)a3.QueryVideoMemoryInfo(0, Vortice.DXGI.MemorySegmentGroup.Local).CurrentUsage;
        }
        catch { /* 显存不可得记 -1，不记 0（与 Bench 同口径） */ }

        var sample = new MemSample(
            Minute: sinceWarmup / 60.0,
            FramesInInterval: (int)(_host.Frames - _framesAtSample),
            WorkingSetBytes: Process.GetCurrentProcess().WorkingSet64,
            VramUsageBytes: vram,
            Gc0: (int)(GC.CollectionCount(0) - _gc0Base),
            Gc1: (int)(GC.CollectionCount(1) - _gc1Base),
            Gc2: (int)(GC.CollectionCount(2) - _gc2Base));
        _framesAtSample = _host.Frames;
        _samples.Add(sample);
        Console.WriteLine(sample.Line());
    }

    private void Baseline()
    {
        _gc0Base = GC.CollectionCount(0);
        _gc1Base = GC.CollectionCount(1);
        _gc2Base = GC.CollectionCount(2);
        _nextSampleAt = SampleIntervalSec;
        _framesAtSample = _host.Frames;
    }

    /// <summary>收尾：打印斜率结论（Shutdown 在渲染线程，最后一次采样在此补）。</summary>
    public string Conclusion()
    {
        if (_warmedAt >= 0 && _totalSeconds - _warmedAt > SampleIntervalSec / 2.0)
            TakeSample(_totalSeconds - _warmedAt); // 补收尾采样，别让最后一段斜率漏算

        if (_samples.Count < 2)
            return "[Stress] 结论：采样不足（<2 条），无法给斜率——延长 --minutes 再跑";

        var ws = MemCurve.Summarize("压力工作集曲线", MemCurve.WorkingSetPoints(_samples));
        var vram = MemCurve.Summarize("压力显存曲线", MemCurve.VramPoints(_samples));
        return $"[Stress] {_samples.Count} 条采样｜{ws}\n[Stress] {vram}";
    }

    public void Dispose()
    {
        // 渲染线程已退出（StopRender Join 之后 app.Run 才返回），这里清引用即可
        _host.Ready -= OnReady;
        _host.Frame -= StressFrame;
        _host.Frame -= OnFrame;
        _host.Shutdown -= OnShutdown;
    }

    private void OnFrame(PresentHost host, double dt) { /* 占位：多播顺序不依赖它，留空便于以后拆 */ }

    private void OnShutdown(PresentHost host) { /* 斜率在 Conclusion 统一算；这里无需动作 */ }
}
