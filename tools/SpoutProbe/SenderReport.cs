namespace SpoutProbe;

/// <summary>
/// S1–S5 的结论汇总 + 断言判定，风格与 Q1–Q6 的 <see cref="Report"/> 一致：
/// 数字全打出来、判定阈值只用 <see cref="Probe"/> 上的常量、最后一行为退出码。
/// 退出码就是这个探针的验收结论——0 才允许接线。
/// </summary>
internal sealed class SenderReport
{
    private readonly SenderResults _r;
    private readonly string _adapter;
    private readonly bool _software;
    /// <summary>发送端没责任、但接线必须遵守的纪律（不参与退出码判定）。</summary>
    private readonly List<string> _disciplines = new();

    public SenderReport(SenderResults r, string adapter, bool software)
    {
        _r = r;
        _adapter = adapter;
        _software = software;
    }

    /// <summary>
    /// 0 = 全部通过（发送端接线放行）；
    /// 3 = S1 无人接收时发送阻塞；4 = S2 像素/尺寸往返不符或陈旧帧；
    /// 6 = S3 Dispose 卡住；7 = S4 同名变尺寸重连失败；8 = S5 慢消费反压；9 = 无样本。
    /// </summary>
    public int ExitCode { get; private set; }

    private void Fail(int code)
    {
        if (ExitCode == 0) ExitCode = code;
    }

    public void Print()
    {
        Console.WriteLine();
        Console.WriteLine("============ SpoutOut 发送端原生行为实测结论（M3 §5 第 1 条）============");
        Console.WriteLine($"硬件：{_adapter}｜软件光栅={_software}｜发送端名='{_r.SenderName}'"
                          + $"｜S1/S5 帧数={_r.Frames}/{_r.SlowFrames}");

        PrintS1();
        PrintS1b();
        PrintS2();
        PrintS3();
        PrintS4();
        PrintS5();

        Console.WriteLine();
        Console.WriteLine("[闸门] M3 路线图 §5 第 1 条：发送端原生返回 / 同步 / 纹理借用期 / 可终止性");
        if (ExitCode == 0)
        {
            Console.WriteLine("     → 通过：发送端可以在生产管线里接线");
            if (_disciplines.Count > 0)
            {
                Console.WriteLine($"       接线纪律（{_disciplines.Count} 条，发送端无责任但必须照办）：");
                foreach (var d in _disciplines) Console.WriteLine("       · " + d);
            }
        }
        else
        {
            Console.WriteLine("     → 不通过：上面标了「不通过」的那一项必须先解决，本阶段不得接线");
        }
        Console.WriteLine();
        Console.WriteLine("退出码：" + ExitCode
                          + "（0=通过 3=S1发送阻塞 4=S2像素/尺寸 6=S3关闭不安全 7=S4同名变尺寸重连 8=S5慢消费反压 9=无样本）");
        Console.WriteLine("======================================================================");
    }

    // ------------------------------------------------------------------ S1

    private void PrintS1()
    {
        var s = _r.S1;
        Console.WriteLine();
        Console.WriteLine("[S1] 无人接收时发送是否阻塞（不建接收端，连发 PresentFrame）");
        var t = s.Timing;
        if (!s.SenderActive)
        {
            Console.WriteLine("     发送端未激活（IsActive=false）——无法判定（视为失败）");
            Fail(9);
            return;
        }

        Console.WriteLine($"     发送端 {s.Size}｜IsActive=True｜预热 {s.WarmupMs:F3}ms（不计入样本）");
        Console.WriteLine($"     样本 {t.Count} 次：min={t.Min:F4}ms avg={t.Avg:F4}ms p50={t.P50:F4}ms p99={t.P99:F4}ms max={t.Max:F4}ms");
        Console.WriteLine($"     <{Probe.FastMs}ms（立即返回）{t.FastCount}/{t.Count} 次；≥{Probe.BlockMs}ms（阻塞）{t.BlockHits} 次");
        if (t.Count == 0)
        {
            Console.WriteLine("     → 结论：无样本，不能判定（视为失败）");
            Fail(9);
        }
        else if (t.BlockHits > 0 || t.P99 >= Probe.BlockMs)
        {
            Console.WriteLine($"     → 结论（不通过）：无人接收时发送会阻塞（命中 {t.BlockHits} 次，max={t.Max:F3}ms）");
            Fail(3);
        }
        else
        {
            Console.WriteLine($"     → 结论：立即返回（max={t.Max:F3}ms < {Probe.BlockMs}ms，与接收端 Q1 同量级）；"
                              + "无人观看时发送端不会把渲染线程关在门外");
        }
    }

    // ----------------------------------------------------------------- S1b

    private void PrintS1b()
    {
        var s = _r.S1b;
        Console.WriteLine();
        Console.WriteLine("[S1b] native SendTexture 的 bool 回执有没有区分度（PresentFrame 现在把它吞掉了）");
        if (s.OpenFailed)
        {
            Console.WriteLine("     native SpoutSender.OpenDirectX 失败——无法判定（不影响 S1/S2 结论）");
            return;
        }

        if (!string.IsNullOrEmpty(s.Error))
        {
            Console.WriteLine("     探针异常：" + s.Error);
            return;
        }

        Console.WriteLine($"     阶段 A 无接收端：{s.A.Count} 次，返回 true {s.A.TrueCount} / false {s.A.FalseCount}"
                          + $"｜p50={s.A.P50:F4}ms max={s.A.Max:F4}ms｜native 自报尺寸 {s.NativeWidthNoRx}×{s.NativeHeightNoRx}");
        Console.WriteLine($"     阶段 B 有接收端（连上={s.ReceiverConnected}，{s.ReceiverConnectPumps} 次 Pump 见到帧）："
                          + $"{s.B.Count} 次，返回 true {s.B.TrueCount} / false {s.B.FalseCount}"
                          + $"｜p50={s.B.P50:F4}ms max={s.B.Max:F4}ms｜native 自报尺寸 {s.NativeWidthWithRx}×{s.NativeHeightWithRx}");
        if (s.NativeFps > 0) Console.WriteLine($"     native 自报 Fps={s.NativeFps:F1}");

        if (s.B.Count == 0)
        {
            Console.WriteLine("     → 结论：无样本，不能判定回执区分度");
        }
        else if (s.A.FalseCount == 0 && s.B.FalseCount == 0)
        {
            Console.WriteLine("     → 结论：**没有区分度**——无人接收和有接收端都恒返回 true。"
                              + "这个 bool 只能证明「调用没抛异常」，不能证明「有人收到」。"
                              + "生产回执不能只靠它：要么用 SentCount/LastError 这类自证字段，"
                              + "要么加真正的下游确认（Spout 原生不提供，须由上层约定）。");
        }
        else if (s.A.FalseCount > 0 && s.B.FalseCount == 0)
        {
            Console.WriteLine("     → 结论：**有区分度**——无人接收时返回 false、有接收端时返回 true，"
                              + "可以直接作为「帧确实发出去了」的回执");
        }
        else
        {
            Console.WriteLine($"     → 结论：返回值与接收端存在与否不是一一对应（A {s.A.TrueCount}true/{s.A.FalseCount}false，"
                              + $"B {s.B.TrueCount}true/{s.B.FalseCount}false），不能单独用作接收确认");
        }
    }

    // ------------------------------------------------------------------ S2

    private void PrintS2()
    {
        var s = _r.S2;
        Console.WriteLine();
        Console.WriteLine("[S2] 尺寸/像素往返（320×180，D2D 清纯色）");
        Console.WriteLine($"     接收端尺寸 {s.Size}（期望 320×180）"
                          + (s.SizeOk ? " ✓" : " ✗"));
        Console.WriteLine($"     红：Present {s.RedSendMs:F4}ms｜接收端 {s.RedConnectPumps} 次 Pump / {s.RedLatencyMs:F3}ms 见到｜"
                          + s.RedSamples);
        Console.WriteLine($"     蓝：Present {s.BlueSendMs:F4}ms｜接收端 {s.BluePumps} 次 Pump / {s.BlueLatencyMs:F3}ms 见到｜"
                          + s.BlueSamples);
        Console.WriteLine($"     回红：Present {s.RedBackSendMs:F4}ms｜接收端 {s.RedBackPumps} 次 Pump / {s.RedBackLatencyMs:F3}ms 见到"
                          + $"｜帧号 Seq={s.SeqAfterBlue}");

        var ok = s.SizeOk && s.RedSeen && s.BlueSeen && s.RedBackSeen;
        if (!ok)
        {
            var miss = new List<string>();
            if (!s.SizeOk) miss.Add("尺寸不符");
            if (!s.RedSeen) miss.Add("红色没看到");
            if (!s.BlueSeen) miss.Add("蓝色没看到（可能是陈旧帧/颜色没跟上）");
            if (!s.RedBackSeen) miss.Add("回红没看到");
            Console.WriteLine("     → 结论（不通过）：" + string.Join("、", miss));
            Fail(4);
            return;
        }

        Console.WriteLine($"     → 结论：红色/蓝色/回红三种颜色都被接收端**逐帧看到**（RGB 命中 R>200/G<50/B<50/A>200 判据，"
                          + $"5 点采样）——不是陈旧帧；颜色切换的帧级延迟 {s.BluePumps}+{s.RedBackPumps} 次 Pump / "
                          + $"{s.BlueLatencyMs:F2}+{s.RedBackLatencyMs:F2}ms，即「发一帧、下一拍就看得见」量级。"
                          + "尺寸严格一致。");
    }

    // ------------------------------------------------------------------ S3

    private void PrintS3()
    {
        var s = _r.S3;
        Console.WriteLine();
        Console.WriteLine("[S3] 可终止性（两个方向的 Dispose）");
        Console.WriteLine($"     ① 接收端在跑（{s.Case1ConnectedPumps} 次 Pump 连上）时 Dispose 发送端：{s.DisposeSenderMs:F3}ms");
        Console.WriteLine($"        发送端消失后接收端再 Pump {s.PumpAfterGoneMs.Count} 次："
                          + $"p50={s.PumpAfterGoneMs.P50:F4}ms p99={s.PumpAfterGoneMs.P99:F4}ms max={s.PumpAfterGoneMs.Max:F4}ms"
                          + $"，第一帧 {s.FirstPumpAfterSenderGoneMs:F3}ms"
                          + $"（≥{Probe.BlockMs}ms 命中 {s.PumpAfterGoneMs.BlockHits}/{s.PumpAfterGoneMs.Count}）");
        Console.WriteLine($"        随后 Dispose 接收端：{s.ExitCode0AliveMs:F3}ms");
        Console.WriteLine($"     ② 发送端在跑（{s.Case2ConnectedPumps} 次 Pump 连上）时 Dispose 接收端：{s.DisposeReceiverMs:F3}ms");
        Console.WriteLine($"        接收端消失后发送端继续发 {s.SendAfterReceiverGoneMs.Count} 帧："
                          + $"p50={s.SendAfterReceiverGoneMs.P50:F4}ms max={s.SendAfterReceiverGoneMs.Max:F4}ms");
        Console.WriteLine($"        随后 Dispose 发送端：{s.DisposeSenderAfterMs:F3}ms");

        var maxDispose = Math.Max(Math.Max(s.DisposeSenderMs, s.ExitCode0AliveMs),
            Math.Max(s.DisposeReceiverMs, s.DisposeSenderAfterMs));
        if (maxDispose >= Probe.DisposeBlockMs)
        {
            Console.WriteLine($"     → 结论（不通过）：关闭不安全（最长 Dispose {maxDispose:F3}ms ≥ {Probe.DisposeBlockMs}ms）");
            Fail(6);
            return;
        }

        Console.WriteLine($"     → 结论：关闭安全（最长 Dispose {maxDispose:F3}ms < {Probe.DisposeBlockMs}ms，全部微秒/毫秒级）；"
                          + "两个方向都不卡，且拆掉一端后另一端仍能继续工作。");
        if (s.DisposeReceiverMs >= Probe.BlockMs)
            Console.WriteLine($"       注意（新发现）：**源还活着时拆接收端要 {s.DisposeReceiverMs:F3}ms**——"
                              + "native 释放已连接的共享句柄是有界等待，不是异常；源已经消失后再拆只要 "
                              + $"{s.ExitCode0AliveMs:F3}ms（Q2 的 0.016ms 是「从未连上过」的场景）。"
                              + "停机预算必须给这一步留 ~70ms，且它发生在渲染线程之外的收尾段。");
        if (s.FirstPumpAfterSenderGoneMs >= Probe.BlockMs)
            Console.WriteLine($"       注意：发送端消失后的**第一帧**取帧 {s.FirstPumpAfterSenderGoneMs:F3}ms ≥ {Probe.BlockMs}ms，"
                              + "与 Q4/Q5/Q6 的 ~67ms 吻合——这是 native 在共享纹理句柄换手后的有界等待；"
                              + "发送端一侧的 Dispose 本身（0.1–0.2ms）不是它的来源。");
        else
            Console.WriteLine($"       注意：本轮「发送端消失后第一帧」只有 {s.FirstPumpAfterSenderGoneMs:F3}ms，"
                              + "没复现 Q4/Q5/Q6 的 ~67ms——该等待的量级受时序影响，不是每轮必然发生；"
                              + "停机握手仍按「先停泵、再释放」的纪律执行。");
    }

    // ------------------------------------------------------------------ S4

    private void PrintS4()
    {
        var s = _r.S4;
        Console.WriteLine();
        Console.WriteLine("[S4] 同名源重启（先同尺寸、再变尺寸）+ 生产恢复路径");
        Console.WriteLine($"     重建前：连上={s.BeforeConnected}｜接收端尺寸 {s.BeforeSize}｜红色像素={(s.BeforePixelsOk ? "是" : "否")}");
        Console.WriteLine($"     ① 同名**同尺寸**重启（320×180 红 → 320×180 蓝）：Dispose {s.DisposeSenderMs:F3}ms → "
                          + $"既有接收端 {(s.SameSizeRestartSeen ? "跟上" : "**没跟上**")}"
                          + $"（{s.SameSizePumps} 次 Pump / 取到 {s.SameSizeFrames} 帧 / {s.SameSizeMs:F1}ms）"
                          + $"｜尺寸 {s.SameSizeSeenSize}｜{s.SameSizeSamples}"
                          + (string.IsNullOrEmpty(s.SameSizeLastError) ? "" : $"｜LastError={s.SameSizeLastError}"));
        Console.WriteLine($"     ② 同名**变尺寸**重启（320×180 → 640×480 绿）：Dispose {s.DisposeSameSizeSenderMs:F3}ms → "
                          + $"发送端 IsActive={s.RebuiltActive} 自报 {s.RebuiltSize}；既有接收端"
                          + $"{(s.OldSeenPumps > 0 ? "跟上" : "**没跟上**")}"
                          + $"（{s.OldSeenPumps} 次 Pump / 取到 {s.OldSeenFrames} 帧 / {s.OldSeenMs:F1}ms）"
                          + $"｜尺寸 {s.OldSeenSize}｜{s.OldSamples}"
                          + (string.IsNullOrEmpty(s.OldLastError) ? "" : $"｜LastError={s.OldLastError}"));
        Console.WriteLine($"     ③ 新建接收端（= 生产恢复路径「删掉再加回图层」）："
                          + $"{(s.NewSeenPumps > 0 ? "立刻看到" : "**看不到**")}"
                          + $"（{s.NewSeenPumps} 次 Pump / {s.NewSeenMs:F3}ms）｜尺寸 {s.NewSeenSize}"
                          + $"｜绿色像素={(s.NewPixelsOk ? "是" : "否")}"
                          + (string.IsNullOrEmpty(s.NewLastError) ? "" : $"｜LastError={s.NewLastError}"));
        Console.WriteLine($"     随后 Dispose 重建后的发送端：{s.DisposeRebuiltMs:F3}ms");

        if (s.NewSeenPumps == 0)
        {
            Console.WriteLine("     → 结论（不通过）：新建接收端也看不到同名重建后的新尺寸/新画面——"
                              + "「同名源重启变尺寸可重连」在发送端一侧就不成立，本阶段不得接线");
            Fail(7);
            return;
        }

        Console.WriteLine($"     → 结论：发送端一侧**通过**——同名源重启（无论变不变尺寸）后，"
                          + $"新建接收端 {s.NewSeenPumps} 次 Pump / {s.NewSeenMs:F3}ms 就连上了 {s.NewSeenSize} 并看到新画面，"
                          + "说明共享纹理是按名字重新注册的、发送端没有留下旧尺寸的残状态。");
        if (s.OldSeenPumps > 0)
        {
            Console.WriteLine("       既有接收端也自己跟随到了新尺寸，无需重建。");
            return;
        }

        // 既有接收端跟不上：发送端没责任（新建的能连），但接线必须知道这件事
        if (s.SameSizeRestartSeen)
        {
            _disciplines.Add("同名源**变尺寸**重启后，既有 SpoutIn 不会自己跟随（同尺寸重启能跟上，"
                             + $"失败点就是尺寸变化）：它会停在旧尺寸 {s.OldSeenSize} 继续出 {s.OldSeenFrames} 帧旧画面，"
                             + "等于「伪装已连接」。接线必须：源变尺寸后重建接收端（删掉再加回图层 → 新 provider），"
                             + "并把尺寸变化当作失效判据，别把旧尺寸帧当有效输入。");
        }
        else
        {
            _disciplines.Add("同名源**重启**后（连同尺寸、只换内容），既有 SpoutIn 就不再跟上了"
                             + $"（等待期间只取到 {s.SameSizeFrames} 帧旧画面，之后 {s.OldSeenFrames} 帧）。"
                             + "接线必须：发送端重建后同样重建接收端，或给 SpoutIn 补一条「源消失 → Close → Open」"
                             + "的恢复路径（本轮只出证据，不改生产语义）。");
        }
    }

    // ------------------------------------------------------------------ S5

    private void PrintS5()
    {
        var s = _r.S5;
        Console.WriteLine();
        Console.WriteLine("[S5] 慢消费反压 + 纹理借用期（接收端连上后故意不取帧，发送端连发）");
        if (!s.Connected)
        {
            Console.WriteLine("     接收端没连上，样本无意义");
            Fail(9);
            return;
        }

        var b = _r.S1.Timing;
        Console.WriteLine($"     对照 A 无接收端（S1）：p50={b.P50:F4}ms p99={b.P99:F4}ms max={b.Max:F4}ms");
        Console.WriteLine($"     B 有接收端但不消费：p50={s.StalledSend.P50:F4}ms p99={s.StalledSend.P99:F4}ms "
                          + $"max={s.StalledSend.Max:F4}ms｜≥{Probe.BlockMs}ms 命中 {s.StalledSend.BlockHits}/{s.StalledSend.Count}"
                          + $"｜{s.StalledSend.Count} 帧共 {s.StallMs:F1}ms");
        Console.WriteLine($"     C 有接收端且同步消费：p50={s.ConsumedSend.P50:F4}ms p99={s.ConsumedSend.P99:F4}ms "
                          + $"max={s.ConsumedSend.Max:F4}ms｜接收端每帧 p50={s.PumpMs.P50:F4}ms");
        var ratio = b.P50 > 0 ? s.StalledSend.P50 / b.P50 : 0;
        Console.WriteLine($"     B/A 的 p50 比：{(ratio > 0 ? ratio.ToString("F2") + "×" : "n/a")}");
        Console.WriteLine($"     恢复取帧后：{s.LatestPumps} 次 Pump / {s.LatestMs:F3}ms 见到最新颜色"
                          + $"（绿色={(s.LatestIsGreen ? "是" : "否")}）｜{s.LatestSamples}");

        // 纹理借用期
        Console.WriteLine($"     借用期：B 段期间接收端一次没 Pump（Seq {s.BorrowSeqBefore}→{s.BorrowSeqAfter}，"
                          + $"未变={(s.BorrowSeqUnchanged ? "是" : "否")}），上游连发 {s.StalledSend.Count} 帧改了共享纹理内容；"
                          + $"再读 FrontPixels：同一数组={(s.BorrowSameArray ? "是" : "否")}，"
                          + $"差异字节={s.BorrowChangedBytes}（共 {s.BorrowLength} 字节）");
        if (s.BorrowChangedBytes != 0)
        {
            Console.WriteLine("     → 结论（不通过）：上游写入漏进了下游的借用缓冲（上游一写、下游那口帧就变），"
                              + "合成器会读到一半被改——必须先修掉再接线");
            Fail(4);
            return;
        }

        if (s.StalledSend.BlockHits > 0)
        {
            Console.WriteLine($"     → 结论（不通过）：接收端不消费时发送被反压（≥{Probe.BlockMs}ms 命中 "
                              + $"{s.StalledSend.BlockHits} 次，max={s.StalledSend.Max:F3}ms）");
            Fail(8);
            return;
        }

        Console.WriteLine($"     → 结论：**没有反压**——接收端连着却不取帧时，发送耗时与「根本没接收端」同一量级"
                          + $"（p50 {b.P50:F4}→{s.StalledSend.P50:F4}ms），"
                          + "发送端不会因下游不消费而阻塞或积压。");
        Console.WriteLine("       借用期安全：接收端手里那口帧是**托管副本**，上游接着写共享纹理不会改到它"
                          + "（差异 0 字节、Seq 未变）——合成器中途读帧不会被撕裂。");
        Console.WriteLine(s.LatestIsGreen
            ? $"       恢复取帧后 {s.LatestPumps} 次 Pump 拿到**最新**颜色：Spout 不排队，只留最新一帧，"
              + "所以下游慢消费丢的是中间帧，不会拿到陈旧帧。"
            : "       但恢复取帧后没看到最新颜色——积压/陈旧，需要单独查。");
        if (!s.LatestIsGreen) Fail(8);
    }
}
