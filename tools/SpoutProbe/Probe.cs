using System.Diagnostics;
using JizuraLive.Logging;
using JizuraLive.Pv;
using JizuraLive.Render.Gpu;
using JizuraLive.Spout;
using Vortice.Direct2D1;

namespace SpoutProbe;

/// <summary>
/// M1 路线图第 6 步的三个实测问题，全部以数字说话：
///   Q1 无人发送时 native <c>ReceiveTexturePtr</c> 是否立即返回；
///   Q2 关闭安全性（Dispose 是否秒回、进程是否干净退出）；
///   Q3 工作台真实路径 <see cref="SpoutSourceProvider.PumpFrame"/> 的耗时量级。
/// </summary>
internal sealed class Probe
{
    /// <summary>「立即返回」阈值：1ms。一次 Pump 里只有一个 native 调用（ReceiveTexturePtr）。</summary>
    public const double FastMs = 1.0;

    /// <summary>「阻塞」阈值：50ms——超过就把渲染线程关在门外一帧以上。</summary>
    public const double BlockMs = 50.0;

    /// <summary>Dispose 阈值：1s。native 收尾若卡住，停机握手就凉了。</summary>
    public const double DisposeBlockMs = 1000.0;

    private readonly D3DDevice _gpu;
    private readonly ID2D1DeviceContext _ctx;
    private readonly string _sender;

    public Probe(D3DDevice gpu, ID2D1DeviceContext ctx, string sender)
    {
        _gpu = gpu;
        _ctx = ctx;
        _sender = sender;
    }

    /// <summary>
    /// Q1：不开任何 Spout sender，直接建 <see cref="SpoutIn"/> 连续 Pump，逐次计时。
    /// 跑满 <paramref name="seconds"/> 秒（托管侧等待日志节拍是 10s 一条，必须覆盖到才看得见），
    /// 同时用 <see cref="PanelAppender.Line"/> 抓「等待 Spout 源」日志的时间戳。
    /// </summary>
    public NoSenderStats NoSenderReturns(double seconds, int maxPumps)
    {
        // 「无人发送」的硬证据是共享内存注册表为空；不空则前提不成立，数据只作参照
        var active = SpoutNames.Enumerate(refresh: true);
        Console.WriteLine($"[Q1] 当前活跃 Spout 发送端：{(active.Count == 0 ? "（无）" : string.Join(", ", active))}");
        if (active.Count > 0)
            Console.WriteLine("[Q1][WARN] 检测到活跃发送端——「无人发送」前提不成立，本节数据仅供参照");

        var stats = new NoSenderStats();
        var waitLogs = new List<long>();
        void OnLine(string line)
        {
            if (line.Contains("等待 Spout 源")) waitLogs.Add(Stopwatch.GetTimestamp());
        }

        PanelAppender.Line += OnLine;
        using var rx = new SpoutIn(_gpu.Device);
        try
        {
            var sw = Stopwatch.StartNew();
            var i = 0;
            while (sw.Elapsed.TotalSeconds < seconds && i < maxPumps)
            {
                var t0 = Stopwatch.GetTimestamp();
                var ok = rx.Pump(true, _sender);
                var ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                stats.Add(ms, ok);
                if (i < 5 || i % 2000 == 0)
                    Console.WriteLine($"[Q1] Pump#{i} {(ok ? "有帧" : "无帧")} {ms:F3}ms");
                i++;
            }

            stats.PumpsPerSec = sw.Elapsed.TotalSeconds > 0 ? stats.Count / sw.Elapsed.TotalSeconds : 0;
        }
        finally
        {
            PanelAppender.Line -= OnLine;
        }

        stats.WaitLogCount = waitLogs.Count;
        stats.WaitLogSpanMs = waitLogs.Count >= 2
            ? (waitLogs[^1] - waitLogs[0]) * 1000.0 / Stopwatch.Frequency
            : 0;
        return stats;
    }

    /// <summary>
    /// Q3：走工作台真实路径 <see cref="SpoutSourceProvider.PumpFrame"/>，逐次记录自测耗时，
    /// 并与 provider 自带的 <see cref="SpoutSourceProvider.MaxReceiveMs"/> 对账。
    /// <paramref name="sendFrames"/> 为 true 时同步驱动 <see cref="SpoutOut"/> 发帧，测「有源」量级。
    /// </summary>
    public PumpFrameStats PumpFrameStats(double seconds, bool sendFrames, SpoutOut? outSend)
    {
        var stats = new PumpFrameStats();
        using var provider = new SpoutSourceProvider("probe_layer", _gpu, _ctx, _sender);

        var sw = Stopwatch.StartNew();
        var i = 0;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            // 有源模式：发送端先出帧，接收端才可能拿到（Spout 共享纹理要发送端先注册）
            if (sendFrames && outSend is not null && i % 2 == 0) outSend.PresentFrame();

            var t0 = Stopwatch.GetTimestamp();
            provider.PumpFrame();
            stats.AddMs((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);

            if (i < 5 || i % 100 == 0)
                Console.WriteLine($"[Q3] PumpFrame#{i} 自测 {stats.Last:F3}ms｜provider Last={provider.LastReceiveMs:F3} Max={provider.MaxReceiveMs:F3}｜连接={provider.Connected}");

            i++;
        }

        stats.ProviderMaxMs = provider.MaxReceiveMs;
        stats.ProviderLastMs = provider.LastReceiveMs;
        stats.Connected = provider.Connected;
        stats.SendFrames = sendFrames;
        return stats;
    }

    /// <summary>
    /// Q4（路线图隐含的停机握手关键场景）：发送端**用着用着消失**（VTS 被用户关掉）
    /// 时，native 取帧会不会卡住。流程：自发送 2 秒 → 当场释放 SpoutOut（模拟 VTS 退出）
    /// → 继续 Pump 3 秒，逐次计时。
    /// 这是停机握手最关心的一拍：如果这里阻塞，渲染线程就跟着一起挂住。
    /// </summary>
    public SenderGoneStats SenderDiesMidStream()
    {
        var stats = new SenderGoneStats();
        using var provider = new SpoutSourceProvider("probe_gone", _gpu, _ctx, _sender);
        using var send = new SpoutOut(_gpu, _sender, 640, 480);

        // 阶段 1：有源，跑到连上为止（最多 2 秒）
        var t0 = Stopwatch.StartNew();
        while (!provider.Connected && t0.Elapsed.TotalSeconds < 2)
        {
            send.PresentFrame();
            provider.PumpFrame();
        }

        stats.ConnectedBefore = provider.Connected;
        Console.WriteLine($"[Q4] 阶段1（有源）：Connected={stats.ConnectedBefore}，{t0.ElapsedMilliseconds}ms 后连上");

        // 阶段 2：杀掉发送端，继续收——逐次计时
        var disposeMs = TimedDispose(send);
        stats.SenderDisposeMs = disposeMs;
        Console.WriteLine($"[Q4] 阶段2：发送端已释放（Dispose {disposeMs:F3}ms），继续 Pump……");

        var after = Stopwatch.StartNew();
        var i = 0;
        // 按 60fps 节奏收帧（真实工作台就是渲染线程一帧一次），避免空转过快把 GC 抖动
        // 放大成「阻塞」假象；同时样本量也足够看分布。
        var frameBudget = 1000.0 / 60;
        double firstMs = 0;
        while (after.Elapsed.TotalSeconds < 3)
        {
            var t1 = Stopwatch.GetTimestamp();
            provider.PumpFrame();
            var ms = (Stopwatch.GetTimestamp() - t1) * 1000.0 / Stopwatch.Frequency;
            stats.AddAfterMs(ms);
            if (i == 0) firstMs = ms;
            if (i < 5 || i % 60 == 0)
                Console.WriteLine($"[Q4] 源消失后 PumpFrame#{i} {ms:F3}ms｜连接={provider.Connected}｜MaxReceiveMs={provider.MaxReceiveMs:F3}");
            i++;

            var spent = (Stopwatch.GetTimestamp() - t1) * 1000.0 / Stopwatch.Frequency;
            var sleep = (int)(frameBudget - spent);
            if (sleep > 0) Thread.Sleep(sleep);
        }

        stats.ConnectedAfter = provider.Connected;
        stats.ProviderMaxMs = provider.MaxReceiveMs;
        stats.GcPauses = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2);
        stats.FirstPumpAfterGoneMs = firstMs;
        return stats;
    }

    /// <summary>
    /// Q5：量化 Q4 发现的「sender Dispose 后第一帧 ~67ms」是不是确定性行为。
    /// 循环 N 轮：建发送端 → 连上 → 释放发送端 → 只测随后第一帧 Pump 的耗时。
    /// 如果每轮都是几十毫秒，这就是 native 在共享纹理句柄失效后的固定代价，必须写进停机握手的账。
    /// </summary>
    public FirstPumpStats FirstPumpAfterSenderGone(int rounds)
    {
        var stats = new FirstPumpStats();
        for (var r = 0; r < rounds; r++)
        {
            using var provider = new SpoutSourceProvider($"probe_fp{r}", _gpu, _ctx, _sender);
            var send = new SpoutOut(_gpu, _sender, 640, 480);

            // 连上为止（最多 1 秒）
            var t0 = Stopwatch.StartNew();
            while (!provider.Connected && t0.Elapsed.TotalSeconds < 1)
            {
                send.PresentFrame();
                provider.PumpFrame();
            }

            if (!provider.Connected) { stats.ConnectFailures++; send.Dispose(); continue; }
            stats.Connects++;

            TimedDispose(send);

            // 第一帧：只测这一次
            var t1 = Stopwatch.GetTimestamp();
            provider.PumpFrame();
            stats.AddFirstMs((Stopwatch.GetTimestamp() - t1) * 1000.0 / Stopwatch.Frequency);
        }

        return stats;
    }

    /// <summary>
    /// Q2 关闭安全性：三个场景分别计时 Dispose——
    ///   A 从未收到过帧（无人发送，最坏情况）；
    ///   B provider 路径（内部有 staging 纹理 + D2D 位图）；
    ///   C 反复 Open/Close 切换（enabled 翻转 10 轮），确认不卡死不泄漏。
    /// </summary>
    public CloseStats CloseSafety()
    {
        var stats = new CloseStats();

        // 场景 A：无人发送、连开带收再关
        var rx = new SpoutIn(_gpu.Device);
        for (var i = 0; i < 50; i++) rx.Pump(true, _sender);
        stats.DisposeNoFrameMs = TimedDispose(rx);

        // 场景 B：provider 路径
        var provider = new SpoutSourceProvider("probe_close", _gpu, _ctx, _sender);
        var t0 = Stopwatch.GetTimestamp();
        for (var i = 0; i < 50; i++) provider.PumpFrame();
        stats.ProviderPumpMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        stats.DisposeProviderMs = TimedDispose(provider);

        // 场景 C：Open/Close 反复切换
        using (var toggler = new SpoutIn(_gpu.Device))
        {
            for (var round = 0; round < 10; round++)
            {
                toggler.Pump(true, _sender);
                toggler.Pump(false, _sender); // enabled=false → Close()
                stats.Toggles++;
            }
        }

        return stats;
    }

    private static double TimedDispose(IDisposable d)
    {
        var t0 = Stopwatch.GetTimestamp();
        d.Dispose();
        return (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
    }
}

/// <summary>Q1 统计：无人发送时 Pump 的逐次耗时与等待日志节拍。</summary>
internal sealed class NoSenderStats
{
    private readonly List<double> _ms = new();
    private readonly List<double> _ok = new();

    public int Count => _ms.Count;
    public double Last { get; private set; }
    public int FastCount { get; private set; }
    public int BlockHits { get; private set; }
    public int WaitLogCount { get; set; }
    public double WaitLogSpanMs { get; set; }
    public double PumpsPerSec { get; set; }
    public int FrameCount => _ok.Count;

    public void Add(double ms, bool ok)
    {
        _ms.Add(ms);
        if (ok) _ok.Add(ms);
        Last = ms;
        if (ms > Max) Max = ms;
        if (Min < 0 || ms < Min) Min = ms;
        if (ms < Probe.FastMs) FastCount++;
        if (ms >= Probe.BlockMs) BlockHits++;
    }

    public double Min { get; private set; } = -1;
    public double Max { get; private set; }
    public double P50 => P(0.50);
    public double P99 => P(0.99);

    private double P(double q)
    {
        if (_ms.Count == 0) return 0;
        var sorted = new List<double>(_ms);
        sorted.Sort();
        return sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * q))];
    }

    public double Avg => _ms.Count == 0 ? 0 : _ms.Average();
}

/// <summary>Q3 统计：PumpFrame 耗时与 provider 埋点对账。</summary>
internal sealed class PumpFrameStats
{
    private readonly List<double> _ms = new();

    public double ProviderMaxMs { get; set; }
    public double ProviderLastMs { get; set; }
    public bool Connected { get; set; }
    public bool SendFrames { get; set; }

    public void AddMs(double ms)
    {
        _ms.Add(ms);
        if (ms > Max) Max = ms;
        if (Min < 0 || ms < Min) Min = ms;
    }

    public int Count => _ms.Count;
    public double Last => _ms.Count == 0 ? 0 : _ms[^1];
    public double Min { get; private set; } = -1;
    public double Max { get; private set; }
    public double P50 => Q(0.50);
    public double P99 => Q(0.99);
    public double Avg => _ms.Count == 0 ? 0 : _ms.Average();

    private double Q(double q)
    {
        if (_ms.Count == 0) return 0;
        var sorted = new List<double>(_ms);
        sorted.Sort();
        return sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * q))];
    }
}

/// <summary>Q2 统计：各场景 Dispose 耗时。</summary>
internal sealed class CloseStats
{
    public double DisposeNoFrameMs { get; set; }
    public double DisposeProviderMs { get; set; }
    public double ProviderPumpMs { get; set; }
    public int Toggles { get; set; }
}

/// <summary>Q4 统计：发送端用着用着消失后，取帧是否还立即返回。</summary>
internal sealed class SenderGoneStats
{
    private readonly List<double> _afterMs = new();

    public bool ConnectedBefore { get; set; }
    public bool ConnectedAfter { get; set; }
    public double SenderDisposeMs { get; set; }
    public double ProviderMaxMs { get; set; }
    /// <summary>阶段 2 期间的 GC 代数回收总次数（判「离群点是 native 阻塞还是 GC 暂停」用）。</summary>
    public int GcPauses { get; set; }

    public void AddAfterMs(double ms)
    {
        _afterMs.Add(ms);
        if (ms > Max) Max = ms;
        if (Min < 0 || ms < Min) Min = ms;
        if (ms < Probe.FastMs) FastCount++;
        if (ms >= Probe.BlockMs) BlockHits++;
    }

    public int Count => _afterMs.Count;
    public double Min { get; private set; } = -1;
    public double Max { get; private set; }
    public int FastCount { get; private set; }
    public int BlockHits { get; private set; }
    public double Avg => _afterMs.Count == 0 ? 0 : _afterMs.Average();

    /// <summary>源消失后**第一帧**的耗时——Q4 里离群点每次都落在这一帧上。</summary>
    public double FirstPumpAfterGoneMs { get; set; }
}

/// <summary>Q5 统计：sender 释放后第一帧的确定性代价。</summary>
internal sealed class FirstPumpStats
{
    private readonly List<double> _firstMs = new();

    public int Connects { get; set; }
    public int ConnectFailures { get; set; }

    public void AddFirstMs(double ms)
    {
        _firstMs.Add(ms);
        if (ms > Max) Max = ms;
        if (Min < 0 || ms < Min) Min = ms;
        if (ms >= Probe.BlockMs) BlockHits++;
    }

    public int Count => _firstMs.Count;
    public double Min { get; private set; } = -1;
    public double Max { get; private set; }
    public int BlockHits { get; private set; }
    public double Avg => _firstMs.Count == 0 ? 0 : _firstMs.Average();
    public double P50 => _firstMs.Count == 0 ? 0 : _firstMs.OrderBy(x => x).ElementAt(_firstMs.Count / 2);
}
