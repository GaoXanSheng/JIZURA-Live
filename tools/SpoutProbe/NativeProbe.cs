using System.Diagnostics;
using JizuraLive.Logging;
using JizuraLive.Render.Gpu;
using JizuraLive.Spout;
using SpoutDXWrapper;
using Vortice.Direct2D1;

namespace SpoutProbe;

/// <summary>
/// Q6：把 67ms 定位到 **native <see cref="SpoutReceiver.ReceiveTexturePtr"/> 内部**。
/// Q4/Q5 只能证明「PumpFrame 第一帧 67ms」——中间还夹着托管侧的 Close/Open/EnsureTargets。
/// 这里绕过 <see cref="SpoutIn"/> 与 <see cref="SpoutSourceProvider"/>，直接调 native，
/// 逐次计时：若 native 自己就是 67ms，则 Q4/Q5 的结论可以钉死；若 native 是微秒级，
/// 那 67ms 出在托管侧，要改的是托管代码而不是怀疑 native。
/// </summary>
internal sealed class NativeProbe
{
    private readonly D3DDevice _gpu;
    private readonly string _sender;

    public NativeProbe(D3DDevice gpu, string sender)
    {
        _gpu = gpu;
        _sender = sender;
    }

    /// <summary>
    /// 流程：建 receiver → 自发送 → 收到至少一帧 → 释放 sender →
    /// 直接循环调 <see cref="SpoutReceiver.ReceiveTexturePtr"/>，逐次计时。
    /// </summary>
    public NativeStats Run(int roundsAfterGone)
    {
        var stats = new NativeStats();
        var rx = new SpoutReceiver();
        rx.SetReceiverName(_sender);
        if (!rx.OpenDirectX(_gpu.Device.NativePointer))
        {
            stats.OpenFailed = true;
            rx.Dispose();
            return stats;
        }

        var send = new SpoutOut(_gpu, _sender, 640, 480);

        // 连上为止
        var t0 = Stopwatch.StartNew();
        var liveMs = -1.0;
        while (liveMs < 0 && t0.Elapsed.TotalSeconds < 2)
        {
            send.PresentFrame();
            liveMs = Time(rx);
        }

        if (liveMs < 0)
        {
            Console.WriteLine("[Q6] 发送端没连上，无法继续");
            send.Dispose();
            rx.Dispose();
            return stats;
        }

        stats.Connected = true;
        // 有源时 native 单次耗时（有帧）
        stats.LiveFrameMs = Time(rx);

        // 杀掉发送端
        send.Dispose();
        Console.WriteLine($"[Q6] 发送端已释放；直接循环调 native ReceiveTexturePtr {roundsAfterGone} 次……");

        for (var i = 0; i < roundsAfterGone; i++)
        {
            var raw = Time(rx);
            var ms = Math.Abs(raw);
            stats.AddAfterGoneMs(ms, i, raw >= 0);
            if (i < 5 || i % 20 == 0)
                Console.WriteLine($"[Q6] native#{i} {ms:F3}ms ptr={(raw >= 0 ? "非零" : "零")}");
        }

        rx.Dispose();
        return stats;
    }

    /// <summary>
    /// 单次 native 调用的耗时（毫秒）。返回 -1 表示指针为零（无帧）——调用方据此判连接态。
    /// </summary>
    private static double Time(SpoutReceiver rx)
    {
        var t0 = Stopwatch.GetTimestamp();
        var ptr = rx.ReceiveTexturePtr();
        var ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        return ptr == IntPtr.Zero ? -ms : ms;
    }
}

/// <summary>Q6 统计。</summary>
internal sealed class NativeStats
{
    private readonly List<double> _afterMs = new();

    public bool OpenFailed { get; set; }
    public bool Connected { get; set; }
    public double LiveFrameMs { get; set; }

    public void AddAfterGoneMs(double ms, int index, bool gotFrame)
    {
        _afterMs.Add(ms);
        if (gotFrame) FrameCount++;
        if (index == 0) FirstMs = ms;
        if (ms > Max) Max = ms;
        if (ms < Min || Min < 0) Min = ms;
        if (ms >= Probe.BlockMs) BlockHits++;
    }

    public int Count => _afterMs.Count;
    public int FrameCount { get; private set; }
    public double FirstMs { get; private set; }
    public double Min { get; private set; } = -1;
    public double Max { get; private set; }
    public int BlockHits { get; private set; }
    public double Avg => _afterMs.Count == 0 ? 0 : _afterMs.Average();
}
