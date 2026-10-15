using JizuraLive.Spout;

namespace SpoutProbe;

/// <summary>
/// 实测结论汇总 + 断言判定。判定阈值集中在 <see cref="Probe"/> 上（FastMs/BlockMs/DisposeBlockMs），
/// 这里只做判定与打印，不重新定义阈值。
/// </summary>
internal sealed class Report
{
    private readonly NoSenderStats _q1;
    private readonly PumpFrameStats _q3;
    private readonly CloseStats _q2;
    private readonly SenderGoneStats _q4;
    private readonly FirstPumpStats _q5;
    private readonly NativeStats _q6;
    private readonly string _adapter;
    private readonly bool _software;
    private readonly int _maxPumps;
    private readonly bool _selfSend;

    public Report(NoSenderStats q1, PumpFrameStats q3, CloseStats q2, SenderGoneStats q4,
        FirstPumpStats q5, NativeStats q6, string adapter, bool software, int maxPumps, bool selfSend)
    {
        _q1 = q1;
        _q3 = q3;
        _q2 = q2;
        _q4 = q4;
        _q5 = q5;
        _q6 = q6;
        _adapter = adapter;
        _software = software;
        _maxPumps = maxPumps;
        _selfSend = selfSend;
    }

    /// <summary>0 = 全通过；3 = Q1 阻塞；4 = Q2 关闭不安全。</summary>
    public int ExitCode { get; private set; }

    public void Print()
    {
        Console.WriteLine();
        Console.WriteLine("================ SpoutIn 可终止性实测结论 ================");
        Console.WriteLine($"硬件：{_adapter}｜软件光栅={_software}｜Q1 Pump 上限={_maxPumps}｜自发送={_selfSend}");

        // ---------- Q1 ----------
        Console.WriteLine();
        Console.WriteLine("[Q1] 无人发送时 native ReceiveTexturePtr 是否立即返回");
        if (_q1.Count > 0)
        {
            Console.WriteLine($"     Pump {_q1.Count} 次｜有帧 {_q1.FrameCount} 次");
            Console.WriteLine($"     耗时 min={_q1.Min:F4}ms max={_q1.Max:F4}ms avg={_q1.Avg:F4}ms "
                              + $"p50={_q1.P50:F4}ms p99={_q1.P99:F4}ms");
            Console.WriteLine($"     空转吞吐：{_q1.PumpsPerSec:F0} 次/秒");
            Console.WriteLine($"     <1ms（立即返回）{_q1.FastCount}/{_q1.Count} 次；≥50ms（阻塞）{_q1.BlockHits} 次");
        }
        else
        {
            Console.WriteLine("     （未采集到样本）");
        }

        Console.WriteLine($"     「等待 Spout 源」日志 {_q1.WaitLogCount} 条"
                          + (_q1.WaitLogSpanMs > 0 ? $"（跨度 {_q1.WaitLogSpanMs:F0}ms）" : ""));

        if (_q1.BlockHits > 0)
        {
            Console.WriteLine($"     → 结论：native 会阻塞（≥{Probe.BlockMs}ms 命中 {_q1.BlockHits} 次，max={_q1.Max:F3}ms）");
            ExitCode = 3;
        }
        else if (_q1.Count == 0)
        {
            Console.WriteLine("     → 结论：无样本，不能判定（视为失败）");
            ExitCode = 3;
        }
        else
        {
            Console.WriteLine($"     → 结论：立即返回（max={_q1.Max:F3}ms < {Probe.BlockMs}ms，"
                              + $"{_q1.FastCount}/{_q1.Count} 次 <1ms），无人发送时不会把渲染线程关在门外");
        }

        // ---------- Q3 ----------
        Console.WriteLine();
        Console.WriteLine("[Q3] SpoutSourceProvider.PumpFrame 耗时量级（工作台真实路径）");
        if (_q3.Count > 0)
        {
            Console.WriteLine($"     PumpFrame {_q3.Count} 次｜连接={_q3.Connected}");
            Console.WriteLine($"     自测 min={_q3.Min:F4}ms max={_q3.Max:F4}ms avg={_q3.Avg:F4}ms "
                              + $"p50={_q3.P50:F4}ms p99={_q3.P99:F4}ms");
            Console.WriteLine($"     provider 埋点：LastReceiveMs={_q3.ProviderLastMs:F4}ms MaxReceiveMs={_q3.ProviderMaxMs:F4}ms");
            Console.WriteLine($"     → MaxReceiveMs 实际量级：{_q3.ProviderMaxMs:F4}ms（停机握手据此判断 native 收尾）");
        }
        else
        {
            Console.WriteLine("     （未采集到样本）");
        }

        // ---------- Q4 ----------
        Console.WriteLine();
        Console.WriteLine("[Q4] 发送端用着用着消失（VTS 被关掉）后取帧是否仍立即返回");
        Console.WriteLine($"     消失前 Connected={_q4.ConnectedBefore}；发送端 Dispose {_q4.SenderDisposeMs:F3}ms");
        Console.WriteLine($"     消失后 PumpFrame {_q4.Count} 次：min={_q4.Min:F4}ms max={_q4.Max:F4}ms avg={_q4.Avg:F4}ms");
        Console.WriteLine($"     <1ms {_q4.FastCount}/{_q4.Count} 次；≥50ms（阻塞）{_q4.BlockHits} 次");
        Console.WriteLine($"     消失后 Connected={_q4.ConnectedAfter}｜MaxReceiveMs={_q4.ProviderMaxMs:F3}ms｜GC 回收 {_q4.GcPauses} 次");
        if (_q4.BlockHits > 0)
        {
            Console.WriteLine($"     → 结论：源消失后出现 ≥{Probe.BlockMs}ms 命中 {_q4.BlockHits} 次（max={_q4.Max:F3}ms）。"
                              + $"总样本 {_q4.Count} 次、<1ms 占 {_q4.FastCount} 次——"
                              + (_q4.BlockHits <= 2
                                  ? "极低频离群（每百万次 1 次量级），更像 GC/调度抖动而非 native 等待；"
                                  : "命中频繁，是 native 阻塞证据；")
                              + "停机握手仍应按 _stopPumping 先行的纪律执行。");
            if (ExitCode == 0) ExitCode = 3;
        }
        else if (_q4.Count == 0)
        {
            Console.WriteLine("     → 结论：无样本（发送端没连上过），不能判定");
        }
        else
        {
            Console.WriteLine($"     → 结论：源消失后仍立即返回（max={_q4.Max:F3}ms < {Probe.BlockMs}ms），"
                              + "取帧本身没有 native 等待；收帧的字节搬运成本已在 M7 由同设备 GPU 拷贝取代"
                              + "（GPU→CPU→GPU 往返已删除，见 --gpu-input 的 G 段）");
        }

        // ---------- Q5 ----------
        Console.WriteLine();
        Console.WriteLine("[Q5] sender 释放后「第一帧」代价是否确定性（Q4 离群点定位）");
        Console.WriteLine($"     共 {_q5.Count} 轮（连上 {_q5.Connects} 次，连接失败 {_q5.ConnectFailures} 次）");
        if (_q5.Count > 0)
        {
            Console.WriteLine($"     第一帧耗时 min={_q5.Min:F3}ms p50={_q5.P50:F3}ms max={_q5.Max:F3}ms avg={_q5.Avg:F3}ms");
            Console.WriteLine($"     ≥{Probe.BlockMs}ms 命中 {_q5.BlockHits}/{_q5.Count} 轮");
            if (_q5.BlockHits == _q5.Count)
                Console.WriteLine($"     → 结论：确定性代价——每轮第一帧都 ≥{Probe.BlockMs}ms（p50={_q5.P50:F3}ms）。"
                                  + "源消失后**紧跟的那一帧**会被 native 关住几十毫秒，停机握手必须先把 _stopPumping 置上。");
            else if (_q5.BlockHits > 0)
                Console.WriteLine($"     → 结论：多数但不是每轮命中（{_q5.BlockHits}/{_q5.Count}），p50={_q5.P50:F3}ms——"
                                  + "与共享纹理句柄失效的时序相关，仍应视为「源消失后第一帧有几十毫秒风险」。");
            else
                Console.WriteLine("     → 结论：第一帧无固定代价（Q4 的离群点是 GC/调度抖动，不是 native 等待）");
        }
        else
        {
            Console.WriteLine("     （无样本）");
        }

        // ---------- Q6 ----------
        Console.WriteLine();
        Console.WriteLine("[Q6] 67ms 定位：直接调 native ReceiveTexturePtr（绕过 SpoutIn/PumpFrame）");
        if (_q6.OpenFailed)
        {
            Console.WriteLine("     native receiver 打开失败——无法定位（视为失败）");
            if (ExitCode == 0) ExitCode = 3;
        }
        else if (!_q6.Connected)
        {
            Console.WriteLine("     发送端没连上，无样本");
        }
        else
        {
            Console.WriteLine($"     有源时单次 native：{_q6.LiveFrameMs:F3}ms");
            Console.WriteLine($"     源消失后 {_q6.Count} 次：min={_q6.Min:F3}ms avg={_q6.Avg:F3}ms max={_q6.Max:F3}ms，第一帧={_q6.FirstMs:F3}ms");
            Console.WriteLine($"     ≥{Probe.BlockMs}ms 命中 {_q6.BlockHits}/{_q6.Count} 次");
            if (_q6.BlockHits > 0)
            {
                Console.WriteLine($"     → 结论：67ms 出在 **native ReceiveTexturePtr 内部**（第一帧 {_q6.FirstMs:F3}ms）。"
                                  + "托管侧无嫌疑——native 在共享纹理句柄失效后自己会等几十毫秒。");
                if (ExitCode == 0) ExitCode = 3;
            }
            else
            {
                Console.WriteLine($"     → 结论：67ms 不在 native（native max={_q6.Max:F3}ms），出在托管侧"
                                  + "（Close→Open→EnsureTargets 重建 staging/字节数组）。");
            }
        }

        // ---------- Q2 ----------
        Console.WriteLine();
        Console.WriteLine("[Q2] 关闭安全性（Dispose 计时）");
        Console.WriteLine($"     A 无帧后 Dispose：{_q2.DisposeNoFrameMs:F3}ms");
        Console.WriteLine($"     B provider Pump×50 共 {_q2.ProviderPumpMs:F3}ms → Dispose {_q2.DisposeProviderMs:F3}ms");
        Console.WriteLine($"     C Open/Close 切换 {_q2.Toggles} 轮完成（未卡死）");
        var disposeMax = Math.Max(_q2.DisposeNoFrameMs, _q2.DisposeProviderMs);
        if (disposeMax >= Probe.DisposeBlockMs)
        {
            Console.WriteLine($"     → 结论：关闭不安全（最长 Dispose {disposeMax:F3}ms ≥ {Probe.DisposeBlockMs}ms）");
            ExitCode = ExitCode == 3 ? 3 : 4;
        }
        else
        {
            Console.WriteLine($"     → 结论：关闭安全（最长 Dispose {disposeMax:F3}ms < {Probe.DisposeBlockMs}ms）");
        }

        Console.WriteLine();
        Console.WriteLine($"退出码：{ExitCode}（0=通过 3=取帧阻塞 4=Q2 关闭不安全）");
        Console.WriteLine("=======================================================");
    }
}
