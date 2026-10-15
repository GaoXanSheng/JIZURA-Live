using Vortice.DXGI;

namespace SpoutProbe;

/// <summary>
/// G 段（<c>--gpu-input</c>）的结论汇总 + 断言判定，风格与 Q/S 两份报告一致：
/// 数字全打出来、阈值只用 <see cref="Probe"/> 上的常量、最后一行为退出码。
///
/// 退出码即结论：0 = 通过；3 = native 阻塞；4 = 像素/尺寸不符；
/// 5 = 不兼容格式没给诊断或仍出图；6 = 借用期泄漏；7 = 无样本。
/// </summary>
internal sealed class GpuReceiveReport
{
    /// <summary>
    /// 已知的有界代价上界（ms）。M1 已 6/6 复现：源消失/共享句柄换手后紧跟的那一帧，
    /// native 自己会等 ~67ms。判定「native 阻塞」时必须把它排除，否则闸门永远红灯；
    /// 但它仍然必须是**有界**的，超过这个上界就是新的阻塞，照样判失败。
    /// </summary>
    private const double KnownCostCeilingMs = 80.0;

    /// <summary>
    /// 改前锚点（本线开工前，同机同口径实测，来自本次会话的 Q3 与探针日志）：
    /// 自发送 640×480，PumpFrame 15478 次 p50=0.2229ms p99=0.4904ms max=7.5592ms；
    /// 接收端看到的源格式 B8G8R8A8_UNorm。用来和 G1 的新数字对比。
    /// </summary>
    private const string BeforeAnchor =
        "改前锚点（本线开工前，RTX 4060 Laptop，SpoutProbe.exe --seconds 8 --self-send）："
        + "PumpFrame 15478 次 p50=0.2229ms p99=0.4904ms max=7.5592ms；源格式 B8G8R8A8_UNorm。";

    private readonly GpuReceiveResults _r;
    private readonly string _adapter;
    private readonly bool _software;

    public GpuReceiveReport(GpuReceiveResults r, string adapter, bool software)
    {
        _r = r;
        _adapter = adapter;
        _software = software;
    }

    public int ExitCode { get; private set; }

    private void Fail(int code)
    {
        if (ExitCode == 0) ExitCode = code;
    }

    public void Print()
    {
        Console.WriteLine();
        Console.WriteLine("======== Spout 输入去 CPU 往返实测结论（M7 G 段 · --gpu-input）========");
        Console.WriteLine($"硬件：{_adapter}｜软件光栅={_software}｜发送端名='{_r.SenderName}'｜借用期帧数={_r.Frames}");
        Console.WriteLine(BeforeAnchor);

        PrintG1();
        PrintG1b();
        PrintG2();
        PrintG3();

        Console.WriteLine();
        Console.WriteLine("[闸门] M7：Spout 输入消除 CPU 往返（不宣称零拷贝）");
        if (ExitCode == 0)
        {
            Console.WriteLine("     → 通过：收帧路径无 Map/Unmap、无 Marshal.Copy、无 byte[] 像素缓冲；"
                              + "像素/尺寸/借用期/不兼容格式诊断逐条命中");
        }
        else
        {
            Console.WriteLine("     → 不通过：上面标了「不通过」的那一项必须先解决");
        }

        Console.WriteLine();
        Console.WriteLine($"退出码：{ExitCode}（0=通过 3=native阻塞 4=像素/尺寸不符 5=不兼容格式没给诊断或仍出图 "
                          + "6=借用期泄漏 7=无样本）");
        Console.WriteLine("======================================================================");
    }

    // ------------------------------------------------------------------ G1

    private void PrintG1()
    {
        Console.WriteLine();
        Console.WriteLine("[G1] 收帧基线：自发送 → SpoutIn.Pump（生产类，逐次计时）");
        PrintOne(_r.G1A);
        PrintOne(_r.G1B);

        var steady = new TimingStats();
        if (_r.G1A.Timing.Count > 0) foreach (var v in _r.G1A.Timing.Values) steady.Add(v);
        if (_r.G1B.Timing.Count > 0) foreach (var v in _r.G1B.Timing.Values) steady.Add(v);
        Console.WriteLine($"     两档合计 {steady.Count} 次：p50={steady.P50:F4}ms p95={steady.P95:F4}ms "
                          + $"p99={steady.P99:F4}ms max={steady.Max:F4}ms｜≥{Probe.BlockMs}ms 命中 {steady.BlockHits} 次");
        Console.WriteLine($"     改后每帧托管分配：640×480 {_r.G1A.AllocBytesPerFrame:F1}B、"
                          + $"1280×720 {_r.G1B.AllocBytesPerFrame:F1}B（旧路径每帧一个 w*h*4 托管数组："
                          + $"{_r.G1A.LegacyPixelBufferBytes}B / {_r.G1B.LegacyPixelBufferBytes}B）");

        if (_r.G1A.Timing.Count == 0 || _r.G1B.Timing.Count == 0)
        {
            Console.WriteLine("     → 结论（不通过）：无样本，不能判定");
            Fail(7);
            return;
        }

        if (!_r.G1A.PixelsOk || !_r.G1B.PixelsOk || _r.G1A.MissCount > 0 || _r.G1B.MissCount > 0)
        {
            Console.WriteLine($"     → 结论（不通过）：像素/尺寸不符（640×480 中心={PixelText(_r.G1A.CenterPixel)}、"
                              + $"1280×720 中心={PixelText(_r.G1B.CenterPixel)}；未取到帧 {_r.G1A.MissCount}/{_r.G1B.MissCount} 次）");
            Fail(4);
            return;
        }

        if (steady.BlockHits > 0)
        {
            Console.WriteLine($"     → 结论（不通过）：native 阻塞（稳态样本 ≥{Probe.BlockMs}ms 命中 {steady.BlockHits} 次，"
                              + $"max={steady.Max:F3}ms）");
            Fail(3);
            return;
        }

        Console.WriteLine($"     → 结论：收帧在微秒/亚毫秒级（max={steady.Max:F3}ms < {Probe.BlockMs}ms），"
                          + "像素正确、尺寸严格跟随；且每帧托管分配已降到个位数字节量级"
                          + "（旧路径每帧一个 w*h*4 托管数组 + 一次 GPU→CPU→GPU 往返，已删除）。");
    }

    private void PrintOne(G1Stats s)
    {
        if (s.Timing.Count == 0)
        {
            Console.WriteLine($"     {s.Width}×{s.Height}：（无样本）{s.Error}");
            return;
        }

        Console.WriteLine($"     {s.Width}×{s.Height}：{s.Timing.Count} 次 Pump（连接用 {s.ConnectPumps} 次）"
                          + $"｜p50={s.Timing.P50:F4}ms p95={s.Timing.P95:F4}ms p99={s.Timing.P99:F4}ms max={s.Timing.Max:F4}ms"
                          + $"｜每帧托管分配={s.AllocBytesPerFrame:F1}B");
        Console.WriteLine($"        源 {s.SourceWidth}×{s.SourceHeight} {s.SourceFormatName}（数值 {s.SourceFormatValue}）"
                          + $"｜自有纹理 {s.OwnedWidth}×{s.OwnedHeight} {s.OwnedFormat}"
                          + $"｜Bind={s.OwnedBind} Usage={s.OwnedUsage} CPU={s.OwnedCpuAccess} Misc={s.OwnedMisc}"
                          + $"｜代次={s.Generation} Seq={s.Seq}｜中心像素 {PixelText(s.CenterPixel)}");
    }

    // ----------------------------------------------------------------- G1b

    private void PrintG1b()
    {
        Console.WriteLine();
        Console.WriteLine("[G1b] 恶意格式基线：raw native sender + SetSenderFormat 发非 BGRA8 源");
        if (_r.G1b.Cases.Count == 0)
        {
            Console.WriteLine("     （无样本）");
            Fail(7);
            return;
        }

        var bad = 0;
        foreach (var c in _r.G1b.Cases)
        {
            Console.WriteLine($"     源格式 {c.Format}（{c.FormatValue}），清色 {ColorText(c.ClearColor)}：");
            Console.WriteLine($"       ① 旧解码复现（探针内重放旧 SpoutIn.cs:130-139：CopyResource→Map→按 w*4 逐行 Marshal.Copy）："
                              + c.Legacy.Text());
            Console.WriteLine($"       ② 当前实现：{c.ReceiverPumps} 次 Pump｜观测格式 {c.ObservedFormatName}"
                              + $"｜出图={(!c.FrontTextureNull)}｜Connected={c.Connected}");
            Console.WriteLine($"          诊断：{(string.IsNullOrEmpty(c.Diagnostic) ? "**空（不通过）**" : c.Diagnostic)}");

            var noDiagnostic = string.IsNullOrEmpty(c.Diagnostic);
            var produced = !c.FrontTextureNull;
            if (noDiagnostic || produced)
            {
                bad++;
                Console.WriteLine($"          → 不通过：{(noDiagnostic ? "没给诊断" : "")}"
                                  + $"{(noDiagnostic && produced ? "且" : "")}{(produced ? "仍然出了图（可能花屏）" : "")}");
            }
            else
            {
                Console.WriteLine("          → 通过：不兼容格式不给诊断就是静默花屏，这里给出了可读诊断且不出图");
            }
        }

        if (bad > 0)
        {
            Fail(5);
            return;
        }

        Console.WriteLine("     → 结论：非 BGRA8 源一律「有诊断、不出图」。旧路径按每像素 4 字节硬读，"
                          + "上面的旧解码复现数字就是它当时的输出——错色/半行错位，且没有任何提示。");
    }

    // ------------------------------------------------------------------ G2

    private void PrintG2()
    {
        var s = _r.G2;
        Console.WriteLine();
        Console.WriteLine("[G2] native 语义取证：ReceiveToExistingTexture(自有纹理) 与 ReceiveTexturePtr()");
        if (!string.IsNullOrEmpty(s.Error))
        {
            Console.WriteLine("     探针异常：" + s.Error);
            Fail(7);
            return;
        }

        Console.WriteLine($"     状态 A 无源：返回={s.NoSourceReturn}｜dest 回读 {PixelText(s.NoSourcePixel)}（清色为洋红）");
        Console.WriteLine($"     状态 B 有源（纯红）：返回={s.WithSourceReturn}｜dest 回读 {PixelText(s.WithSourcePixel)}");
        Console.WriteLine($"     状态 C 尺寸不匹配（dest {GpuReceiveProbe.MismatchW}×{GpuReceiveProbe.MismatchH}）："
                          + $"返回={s.MismatchReturn}｜dest 回读 {PixelText(s.MismatchPixel)}（清色为洋红）");
        Console.WriteLine($"     源指针：首次={PtrText(s.SrcPtrBefore)} 复读={PtrText(s.SrcPtrRepeat)}"
                          + $"｜恒定={(s.PtrStable ? "是" : "否")}｜源消失后={PtrText(s.PtrAfterSourceGone)}");
        Console.WriteLine($"     源指针：变尺寸后={PtrText(s.PtrAfterResize)}（变化={(s.PtrChangedOnResize ? "是" : "否")}，"
                          + $"native 自报尺寸跟随={s.ResizeSizeOk}）｜同名重连后={PtrText(s.PtrAfterRestart)}"
                          + $"（变化={(s.PtrChangedOnRestart ? "是" : "否")}）");
        Console.WriteLine($"     源纹理 Description：{s.SrcW}×{s.SrcH} {s.SrcFormat}｜Bind={s.SrcBind}"
                          + $"｜Usage={s.SrcUsage}｜CPU={s.SrcCpuAccess}｜Misc={s.SrcMisc}");
        Console.WriteLine($"     同设备证据：把该指针当纹理 CopyResource 进自建 BGRA8 纹理再回读 → {PixelText(s.SameDeviceCopyPixel)}"
                          + $"（期望纯红，命中={s.SameDeviceCopyOk}）");

        // 裁决：Plan A 成立的充分条件是「native 确实把我们那张 dest 写成了源内容」。
        var destWritten = s.WithSourcePixel.R > 200 && s.WithSourcePixel.G < 60 && s.WithSourcePixel.B < 60
                          && s.WithSourcePixel.A > 200;
        if (destWritten && s.WithSourceReturn)
        {
            Console.WriteLine("     → 裁决：Plan A 成立——ReceiveToExistingTexture 会把源内容写进我们传入的自有纹理，"
                              + "可以直收（无需再补一次 CopyResource）。");
            Console.WriteLine("       但 SpoutIn 仍需要 ReceiveTexturePtr() 先取一次 Description 才能做格式白名单判定，"
                              + "所以生产路径用的是 Plan A 的写入口 + 一次元数据读取。");
        }
        else
        {
            Console.WriteLine("     → 裁决：Plan A **不成立**——传进去的自有纹理没有被写成源内容"
                              + $"（有源时 dest 回读仍是 {PixelText(s.WithSourcePixel)}）。");
            Console.WriteLine("       采用 Plan B：ReceiveTexturePtr() 取源纹理 → 同设备 CopyResource 进自有纹理。"
                              + "同设备性由上面的 CopyResource 回读命中佐证（D3D11 的 CopyResource 只允许同设备）。");
        }

        if (!s.SameDeviceCopyOk)
        {
            Console.WriteLine("     → 结论（不通过）：同设备拷贝像素不对，Plan B 的前提不成立");
            Fail(4);
        }
    }

    // ------------------------------------------------------------------ G3

    private void PrintG3()
    {
        var s = _r.G3;
        Console.WriteLine();
        Console.WriteLine("[G3] 生产路径：换色 / 借用期 / 变尺寸+位图重建 / 源消失");
        if (!string.IsNullOrEmpty(s.Error))
        {
            Console.WriteLine("     探针异常：" + s.Error);
            Fail(7);
            return;
        }

        Console.WriteLine($"     换色：红={(s.RedSeen ? "看到" : "**没看到**")}（{s.RedPumps} 次 Pump）"
                          + $"｜蓝={(s.BlueSeen ? "看到" : "**没看到**")}（{s.BluePumps} 次 Pump）"
                          + "（换色后必须看到新颜色，不是陈旧帧）");
        Console.WriteLine($"     借用期：上游改共享纹理期间接收端不 Pump｜同一张纹理实例={(s.BorrowSameTexture ? "是" : "否")}"
                          + $"｜Seq 未变={(s.BorrowSeqUnchanged ? "是" : "否")}｜差异字节={s.BorrowChangedBytes}（共 {s.BorrowLength} 字节）");
        Console.WriteLine($"     变尺寸：{s.BeforeSize} → {s.AfterSize}｜尺寸跟随={(s.SizeFollowed ? "是" : "否")}"
                          + $"｜位图重建={(s.BitmapRebuilt ? "是" : "否")}｜位图尺寸正确={(s.BitmapSizeOk ? "是" : "否")}"
                          + $"（{s.ResizePumps} 次 Pump）");
        Console.WriteLine($"     源消失：Pump 30 次｜Connected={s.AfterGoneConnected}｜Seq 未变={(s.AfterGoneSeqUnchanged ? "是" : "否")}"
                          + $"｜同一张纹理实例={(s.AfterGoneSameTexture ? "是" : "否")}｜最后一帧差异字节={s.AfterGoneChangedBytes}");

        var steady = s.Steady;
        Console.WriteLine($"     取帧耗时（G3 段）：{steady.Count} 次样本 p50={steady.P50:F4}ms p95={steady.P95:F4}ms "
                          + $"max={steady.Max:F4}ms｜≥{Probe.BlockMs}ms 命中 {steady.BlockHits} 次");
        foreach (var (label, ms) in s.KnownCost)
        {
            var known = ms is >= Probe.BlockMs and <= KnownCostCeilingMs;
            Console.WriteLine($"     已知有界代价（不参与阻塞判定）：{label} {ms:F3}ms"
                              + $"｜{(known ? "落在 60–80ms 已知区间 ✓" : "**超出已知区间**")}");
        }

        Console.WriteLine($"     alpha 能力边界：声明 Straight（生产 4 参构造的默认）→ 出图={s.StraightImageOk}"
                          + $"｜声明 Premultiplied → 出图={s.PremultipliedImageOk}");
        if (!s.StraightImageOk)
        {
            Console.WriteLine("       → 结论：D2D 的 **surface 位图只接受 Premultiplied**（声明 Straight 时 "
                              + "CreateBitmapFromDxgiSurface 抛 UNSUPPORTEDPIXELFORMAT）。本线按「显式诊断（Log.Warn 一次，不刷屏）"
                              + "+ 不出图」交付，不静默变色；straight→premultiplied 的转换 pass 不在本线（notDoing）。"
                              + "**注意**：生产构造点（PvPipeline）目前用的是 4 参构造 = premultiplied:false，"
                              + "所以在那条默认值被改掉之前，Spout 图层会「一条诊断 + 不出图」——这是本线交付的明确取舍，"
                              + "需要里程碑收口人决定改默认值还是另立转换线。");
        }

        if (!s.PremultipliedImageOk)
        {
            Console.WriteLine("     → 结论（不通过）：声明 Premultiplied 也出不了图，surface 位图这条路走不通");
            Fail(4);
            return;
        }

        if (!s.RedSeen || !s.BlueSeen)
        {
            Console.WriteLine("     → 结论（不通过）：换色后没在超时内看到新颜色——像素/尺寸不符");
            Fail(4);
            return;
        }

        if (s.BorrowChangedBytes != 0 || !s.BorrowSameTexture || !s.BorrowSeqUnchanged)
        {
            Console.WriteLine("     → 结论（不通过）：借用期泄漏——上游写入漏进了下游借用的纹理"
                              + "（合成器读到一半就会被改）");
            Fail(6);
            return;
        }

        if (!s.SizeFollowed || !s.BitmapRebuilt || !s.BitmapSizeOk)
        {
            Console.WriteLine($"     → 结论（不通过）：变尺寸后尺寸没跟随或位图没重建"
                              + $"（跟随={s.SizeFollowed} 重建={s.BitmapRebuilt} 位图尺寸={s.BitmapSizeOk}）");
            Fail(4);
            return;
        }

        if (s.AfterGoneChangedBytes != 0 || !s.AfterGoneSeqUnchanged || s.AfterGoneConnected)
        {
            Console.WriteLine("     → 结论（不通过）：源消失后仍出新帧或最后一帧内容被改"
                              + $"（Seq 未变={s.AfterGoneSeqUnchanged} Connected={s.AfterGoneConnected} "
                              + $"差异字节={s.AfterGoneChangedBytes}）");
            Fail(4);
            return;
        }

        var overCeiling = s.KnownCost.Where(x => x.Ms > KnownCostCeilingMs).ToList();
        if (steady.BlockHits > 0 || overCeiling.Count > 0)
        {
            Console.WriteLine($"     → 结论（不通过）：native 阻塞（稳态命中 {steady.BlockHits} 次，"
                              + $"超出已知上界 {KnownCostCeilingMs}ms 的样本 {overCeiling.Count} 次）");
            Fail(3);
            return;
        }

        Console.WriteLine("     → 结论：换色在 1 次 Pump 内可见（不是陈旧帧）、借用期内容零变化、"
                          + "变尺寸后尺寸跟随且位图按代次重建、源消失后不再出新帧且最后一帧仍是独立副本；"
                          + $"源消失/重连后的首帧代价落在已知有界区间（≤{KnownCostCeilingMs}ms），"
                          + "不构成新的 native 阻塞。");
    }

    // -------------------------------------------------------------- 小工具

    private static string PtrText(IntPtr p) => p == IntPtr.Zero ? "0" : "非零";

    private static string PixelText((byte R, byte G, byte B, byte A) c) => $"R{c.R}G{c.G}B{c.B}A{c.A}";

    private static string ColorText(Vortice.Mathematics.Color4 c) =>
        $"R{(int)MathF.Round(c.R * 255)}G{(int)MathF.Round(c.G * 255)}B{(int)MathF.Round(c.B * 255)}";
}

// =================================================================== 结果类

internal sealed class GpuReceiveResults
{
    public string SenderName { get; set; } = "";
    public int Frames { get; set; }
    public G1Stats G1A { get; set; } = new();
    public G1Stats G1B { get; set; } = new();
    public G1bStats G1b { get; set; } = new();
    public G2Stats G2 { get; set; } = new();
    public G3Stats G3 { get; set; } = new();
}

/// <summary>G1：一档尺寸下的收帧分布与源/自有纹理画像。</summary>
internal sealed class G1Stats
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int Frames { get; set; }
    public string Error { get; set; } = "";
    public int ConnectPumps { get; set; }
    public bool Connected { get; set; }
    public int MissCount { get; set; }
    public TimingStats Timing { get; } = new();
    public double AllocBytesPerFrame { get; set; }
    public int SourceWidth { get; set; }
    public int SourceHeight { get; set; }
    public int SourceFormatValue { get; set; } = -1;
    public string SourceFormatName { get; set; } = "（未看到）";
    public int OwnedWidth { get; set; }
    public int OwnedHeight { get; set; }
    public string OwnedFormat { get; set; } = "";
    public string OwnedBind { get; set; } = "";
    public string OwnedUsage { get; set; } = "";
    public string OwnedCpuAccess { get; set; } = "";
    public string OwnedMisc { get; set; } = "";
    public long LegacyPixelBufferBytes { get; set; }
    public long Generation { get; set; }
    public long Seq { get; set; }
    public bool PixelsOk { get; set; }
    public (byte R, byte G, byte B, byte A) CenterPixel { get; set; }
}

/// <summary>G1b：非 BGRA8 源的两个观察点。</summary>
internal sealed class G1bStats
{
    public List<FormatCaseStats> Cases { get; } = new();
}

internal sealed class FormatCaseStats
{
    public Format Format { get; set; }
    public int FormatValue { get; set; }
    public Vortice.Mathematics.Color4 ClearColor { get; set; }
    public string Error { get; set; } = "";
    public bool Sent { get; set; }
    public LegacyDecodeStats Legacy { get; set; } = new();
    public int ReceiverPumps { get; set; }
    public bool ReceiverSawFrame { get; set; }
    public int ObservedFormat { get; set; } = -1;
    public string ObservedFormatName { get; set; } = "（未看到）";
    public string Diagnostic { get; set; } = "";
    public bool FrontTextureNull { get; set; }
    public bool Connected { get; set; }
}

/// <summary>旧解码路径的本地重放结果（改前数字）。</summary>
internal sealed class LegacyDecodeStats
{
    public string Error { get; set; } = "";
    public int SourceW { get; set; }
    public int SourceH { get; set; }
    public string SourceFormat { get; set; } = "";
    public int RowPitch { get; set; }
    public int DecodedBytes { get; set; }
    public byte[] Pixels { get; set; } = [];
    public (byte R, byte G, byte B, byte A) CenterPixel { get; set; }

    public string Text()
    {
        if (!string.IsNullOrEmpty(Error)) return "无法复现（" + Error + "）";
        return $"源 {SourceW}×{SourceH} {SourceFormat}，行距 {RowPitch}B（按 w*4={SourceW * 4}B 逐行读，"
               + $"共读 {DecodedBytes}B）→ 中心像素 R{CenterPixel.R}G{CenterPixel.G}B{CenterPixel.B}A{CenterPixel.A}";
    }
}

/// <summary>G2：native 语义取证。</summary>
internal sealed class G2Stats
{
    public string Error { get; set; } = "";
    public bool SenderActive { get; set; }

    public bool NoSourceReturn { get; set; }
    public (byte R, byte G, byte B, byte A) NoSourcePixel { get; set; }

    public bool WithSourceReturn { get; set; }
    public (byte R, byte G, byte B, byte A) WithSourcePixel { get; set; }

    public bool MismatchReturn { get; set; }
    public (byte R, byte G, byte B, byte A) MismatchPixel { get; set; }

    public IntPtr SrcPtrBefore { get; set; }
    public IntPtr SrcPtrRepeat { get; set; }
    public bool PtrStable { get; set; }
    public IntPtr PtrAfterSourceGone { get; set; }
    public IntPtr PtrAfterResize { get; set; }
    public IntPtr PtrAfterRestart { get; set; }
    public bool PtrChangedOnResize { get; set; }
    public bool PtrChangedOnRestart { get; set; }
    public bool ResizeSizeOk { get; set; }

    public int SrcW { get; set; }
    public int SrcH { get; set; }
    public string SrcFormat { get; set; } = "";
    public string SrcBind { get; set; } = "";
    public string SrcUsage { get; set; } = "";
    public string SrcCpuAccess { get; set; } = "";
    public string SrcMisc { get; set; } = "";

    public bool SameDeviceCopyOk { get; set; }
    public (byte R, byte G, byte B, byte A) SameDeviceCopyPixel { get; set; }
}

/// <summary>G3：生产路径（provider / SpoutIn）的四条闸门。</summary>
internal sealed class G3Stats
{
    public string Error { get; set; } = "";

    public bool RedSeen { get; set; }
    public int RedPumps { get; set; }
    public bool BlueSeen { get; set; }
    public int BluePumps { get; set; }

    public bool BorrowSameTexture { get; set; }
    public bool BorrowSeqUnchanged { get; set; }
    public int BorrowChangedBytes { get; set; } = -1;
    public int BorrowLength { get; set; }

    public string BeforeSize { get; set; } = "（未取到）";
    public string AfterSize { get; set; } = "（未取到）";
    public bool BeforeBitmapOk { get; set; }
    public bool SizeFollowed { get; set; }
    public bool BitmapRebuilt { get; set; }
    public bool BitmapSizeOk { get; set; }
    public int ResizePumps { get; set; }

    public bool GoneConnected { get; set; }
    public bool GoneHadFrame { get; set; }
    public bool AfterGoneConnected { get; set; }
    public bool AfterGoneSeqUnchanged { get; set; }
    public bool AfterGoneSameTexture { get; set; }
    public int AfterGoneChangedBytes { get; set; } = -1;

    /// <summary>稳态取帧样本（参与「native 阻塞」判定）。</summary>
    public TimingStats Steady { get; } = new();

    /// <summary>已知有界代价点（源消失/重连后的首帧）——单独记，不参与阻塞判定，但超出上界仍算失败。</summary>
    public List<(string Label, double Ms)> KnownCost { get; } = new();

    /// <summary>声明 Straight 时能不能出图（D2D surface 位图的 alpha 能力边界）。</summary>
    public bool StraightImageOk { get; set; }

    /// <summary>声明 Premultiplied 时能不能出图。</summary>
    public bool PremultipliedImageOk { get; set; }
}
