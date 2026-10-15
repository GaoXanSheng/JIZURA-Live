using System.Diagnostics;
using JizuraLive.Logging;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Gpu;
using JizuraLive.Spout;
using SpoutDXWrapper;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using DCommonAlphaMode = Vortice.DCommon.AlphaMode;
using DCommonPixelFormat = Vortice.DCommon.PixelFormat;

namespace SpoutProbe;

/// <summary>
/// M3 路线图 §5 第 1 条「发送端原生返回、同步、纹理借用期、可终止性」的实测探针。
/// 全部用**生产类** <see cref="SpoutOut"/> + <see cref="SpoutIn"/> 在同一个进程里
/// sender → receiver 往返取证，不重写一份「类似」实现（那样只能证明探针自己）。
///
///   S1  无人接收时发送是否阻塞——建 320×180 发送端，不建接收端，连发 N 次 PresentFrame，
///       逐次计时（p50/p99/max）。基准线，S5 的反压对照。
///   S1b 原生回执区分度——直接建 native <see cref="SpoutSender"/>，分别在「无接收端」与
///       「有接收端且已连上」两种状态下看 SendTexture 的 bool 是否不同。
///       PresentFrame 目前吞掉这个返回值，本节回答的是「把它透出来有没有用」。
///   S2  像素/尺寸往返——D2D 清纯红发出，接收端校验尺寸与 RGBA；再换纯蓝，
///       确认接收端看到的是**变化**而不是陈旧帧，并记录帧级延迟。
///   S3  可终止性——① 接收端在跑时 Dispose 发送端；② 发送端在跑时 Dispose 接收端；
///       分别计时，并确认之后双方都还能正常工作。
///   S4  同名重建变尺寸——Dispose 发送端后同名重建为 640×480，既有接收端能否看到新尺寸与新画面；
///       另建一个新接收端做「同名重连」对照。
///   S5  慢消费——接收端连上后故意不取帧，发送端连发 N 帧，与 S1（无接收端）和
///       「接收端同步消费」两点对照，看发送是否被反压；再确认接收端恢复取帧时看到的是**最新帧**而不是积压队列。
/// </summary>
internal sealed class SenderProbe
{
    /// <summary>发送端/接收端尺寸：与 S5/S1 对照一致，故意取小于生产的尺寸以免显存效应混淆。</summary>
    public const int TestW = 320;
    public const int TestH = 180;

    /// <summary>同名重建用的第二尺寸（S4）。</summary>
    public const int RebuildW = 640;
    public const int RebuildH = 480;

    private static readonly Color4 ColRed = new(1f, 0f, 0f, 1f);
    private static readonly Color4 ColGreen = new(0f, 1f, 0f, 1f);
    private static readonly Color4 ColBlue = new(0f, 0f, 1f, 1f);

    private readonly D3DDevice _gpu;
    private readonly D2DHost _d2d;
    private readonly string _name;

    public SenderProbe(D3DDevice gpu, D2DHost d2d, string name)
    {
        _gpu = gpu;
        _d2d = d2d;
        _name = name;
    }

    public SenderResults Run(int frames, int slowFrames, int warmup)
    {
        var r = new SenderResults { SenderName = _name, Frames = frames, SlowFrames = slowFrames };
        r.S1 = NoReceiverSend(frames, warmup);
        r.S1b = NativeReceipt(frames, warmup);
        r.S2 = RoundTrip();
        r.S3 = DisposeSafety();
        r.S4 = RebuildResize();
        r.S5 = SlowConsumer(slowFrames, warmup);
        return r;
    }

    // ================================================================= S1

    /// <summary>
    /// S1：不建任何接收端，连发 <paramref name="frames"/> 帧，逐次计时。
    /// 判定标准与 Q1 一致（<see cref="Probe.BlockMs"/>）：若像接收端那样立即返回，
    /// 发送端就不会在无人观看时把渲染线程关在门外。
    /// </summary>
    public SendStats NoReceiverSend(int frames, int warmup)
    {
        var st = new SendStats("S1 无接收端");
        using var s = new SpoutOut(_gpu, _name, TestW, TestH);
        st.SenderActive = s.IsActive;
        st.Size = $"{s.Width}×{s.Height}";
        if (!s.IsActive) return st;

        Fill(s, ColRed);
        st.WarmupMs = Warmup(s, warmup);

        for (var i = 0; i < frames; i++)
        {
            var ms = TimedPresent(s);
            st.Add(ms);
            if (i < 3 || i % 250 == 0) Console.WriteLine($"[S1] PresentFrame#{i} {ms:F4}ms（无接收端）");
        }

        return st;
    }

    // ================================================================ S1b

    /// <summary>
    /// S1b：native <see cref="SpoutSender.SendTexture"/> 的 bool 回执有没有区分度。
    /// 两个阶段用同一个 native sender：A 无任何接收端；B 有 <see cref="SpoutIn"/> 且已看到帧。
    /// 若两阶段全 true，则这个 bool 只能证明「调用没抛异常」，不能证明「有人收到」。
    /// 用独立名字（<c>_name + "-raw"</c>）以免和生产 SpoutOut 的同名 sender 抢注册表。
    /// </summary>
    public NativeReceiptStats NativeReceipt(int frames, int warmup)
    {
        var st = new NativeReceiptStats();
        var rawName = _name + "-raw";
        var raw = new SpoutSender();
        ID3D11Texture2D? tex = null;
        IDXGISurface? surf = null;
        ID2D1Bitmap1? bmp = null;
        SpoutIn? rx = null;
        try
        {
            raw.SetSenderName(rawName);
            if (!raw.OpenDirectX(_gpu.Device.NativePointer))
            {
                st.OpenFailed = true;
                return st;
            }

            st.OpenOk = true;
            var desc = new Texture2DDescription
            {
                Width = TestW,
                Height = TestH,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.Shared,
            };
            tex = _gpu.Device.CreateTexture2D(desc);
            surf = tex.QueryInterface<IDXGISurface>();
            bmp = _d2d.Ctx.CreateBitmapFromDxgiSurface(surf, new BitmapProperties1(
                new DCommonPixelFormat(Format.B8G8R8A8_UNorm, DCommonAlphaMode.Premultiplied),
                96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw));
            _d2d.Draw.SetTarget(bmp, TestW, TestH);
            ClearTo(ColRed);

            // 阶段 A：无接收端
            for (var i = 0; i < warmup; i++) raw.SendTexture(tex.NativePointer);
            for (var i = 0; i < frames; i++)
            {
                var t0 = Stopwatch.GetTimestamp();
                var ok = raw.SendTexture(tex.NativePointer);
                st.A.Add(Timing(t0), ok);
            }

            st.NativeWidthNoRx = raw.GetWidth();
            st.NativeHeightNoRx = raw.GetHeight();

            // 阶段 B：有接收端，连上为止
            rx = new SpoutIn(_gpu.Device);
            var connect = PumpUntil(rx, rawName, r => r.FrontPixels is not null, 3000);
            st.ReceiverConnected = connect.Hit;
            st.ReceiverConnectPumps = connect.Pumps;
            for (var i = 0; i < warmup; i++) raw.SendTexture(tex.NativePointer);
            for (var i = 0; i < frames; i++)
            {
                var t0 = Stopwatch.GetTimestamp();
                var ok = raw.SendTexture(tex.NativePointer);
                st.B.Add(Timing(t0), ok);
            }

            st.NativeWidthWithRx = raw.GetWidth();
            st.NativeHeightWithRx = raw.GetHeight();
            st.NativeFps = raw.GetFps();
        }
        catch (Exception ex)
        {
            st.Error = ex.Message;
            Log.Warn("[S1b] native 回执探针异常：" + ex.Message);
        }
        finally
        {
            rx?.Dispose();
            DetachTarget();
            bmp?.Dispose();
            surf?.Dispose();
            tex?.Dispose();
            raw.Dispose();
        }

        return st;
    }

    // ================================================================= S2

    /// <summary>
    /// S2：尺寸/像素往返。320×180 发送端 D2D 清纯红 → 接收端必须看到 320×180 且
    /// R&gt;200/G&lt;50/B&lt;50/A&gt;200；改纯蓝后必须看到**蓝色**（不是红/黑陈旧帧），
    /// 记录 PresentFrame 返回到看到新颜色的帧数与时延。
    /// </summary>
    public RoundTripStats RoundTrip()
    {
        var st = new RoundTripStats();
        using var s = new SpoutOut(_gpu, _name, TestW, TestH);
        if (!s.IsActive) return st;

        using var rx = new SpoutIn(_gpu.Device);
        rx.Pump(true, _name); // 先开 native receiver，后续帧数才好解释

        Fill(s, ColRed);
        var t0 = Stopwatch.GetTimestamp();
        s.PresentFrame();
        st.RedSendMs = Timing(t0);
        var redPumps = PumpUntil(rx, _name, r => Samples(r).Any(IsRed), 3000);
        st.RedConnectPumps = redPumps.Pumps;
        st.RedLatencyMs = redPumps.Ms;
        st.RedSeen = redPumps.Hit;
        st.Size = $"{rx.SourceWidth}×{rx.SourceHeight}";
        st.SizeOk = rx.SourceWidth == TestW && rx.SourceHeight == TestH;
        st.RedSamples = SampleText(rx);

        // 换色：这是「不是陈旧帧」的硬证据
        Fill(s, ColBlue);
        var t1 = Stopwatch.GetTimestamp();
        s.PresentFrame();
        st.BlueSendMs = Timing(t1);
        var bluePumps = PumpUntil(rx, _name, r => Samples(r).Any(IsBlue), 3000);
        st.BluePumps = bluePumps.Pumps;
        st.BlueLatencyMs = bluePumps.Ms;
        st.BlueSeen = bluePumps.Hit;
        st.BlueSamples = SampleText(rx);
        st.SeqAfterBlue = rx.Seq;

        // 再回红一次：证明不是「一直看到最后发的那个」的假象（双向可变）
        Fill(s, ColRed);
        var t2 = Stopwatch.GetTimestamp();
        s.PresentFrame();
        st.RedBackSendMs = Timing(t2);
        var backPumps = PumpUntil(rx, _name, r => Samples(r).Any(IsRed), 3000);
        st.RedBackPumps = backPumps.Pumps;
        st.RedBackLatencyMs = backPumps.Ms;
        st.RedBackSeen = backPumps.Hit;

        return st;
    }

    // ================================================================= S3

    /// <summary>
    /// S3 可终止性：
    ///   ① 接收端在跑（已连上）时 Dispose 发送端——Dispose 是否秒回，之后接收端还能继续 Pump；
    ///   ② 发送端在跑时 Dispose 接收端——Dispose 是否秒回，之后发送端还能继续发。
    /// 两段的耗时都进报告，报告按 <see cref="Probe.DisposeBlockMs"/> 判定。
    /// </summary>
    public DisposeStats DisposeSafety()
    {
        var st = new DisposeStats();

        // ① 先收后杀发
        {
            var rx = new SpoutIn(_gpu.Device);
            var s = new SpoutOut(_gpu, _name, TestW, TestH);
            Fill(s, ColRed);
            var connect = 0;
            for (var i = 0; i < 200 && connect == 0; i++)
            {
                s.PresentFrame();
                if (rx.Pump(true, _name)) connect = i + 1;
            }

            st.Case1ConnectedPumps = connect;
            DetachTarget();
            st.DisposeSenderMs = TimedDispose(s);
            Console.WriteLine($"[S3①] 接收端在跑时 Dispose 发送端：{st.DisposeSenderMs:F3}ms");

            // 发送端没了，接收端还得能继续 Pump（逐次计时，第一帧是已知的 native 代价）
            var after = new TimingStats();
            for (var i = 0; i < 30; i++)
            {
                var t = Stopwatch.GetTimestamp();
                rx.Pump(true, _name);
                var ms = Timing(t);
                after.Add(ms);
                if (i == 0) st.FirstPumpAfterSenderGoneMs = ms;
            }

            st.PumpAfterGoneMs = after;
            st.ExitCode0AliveMs = TimedDispose(rx);
            Console.WriteLine($"[S3①] 发送端消失后 Pump 30 次：max={after.Max:F3}ms（第一帧 {st.FirstPumpAfterSenderGoneMs:F3}ms）"
                              + $"；随后 Dispose 接收端 {st.ExitCode0AliveMs:F3}ms");
        }

        // ② 先杀收再发
        {
            var s = new SpoutOut(_gpu, _name, TestW, TestH);
            var rx = new SpoutIn(_gpu.Device);
            Fill(s, ColRed);
            var connect = 0;
            for (var i = 0; i < 200 && connect == 0; i++)
            {
                s.PresentFrame();
                if (rx.Pump(true, _name)) connect = i + 1;
            }

            st.Case2ConnectedPumps = connect;
            st.DisposeReceiverMs = TimedDispose(rx);
            Console.WriteLine($"[S3②] 发送端在跑时 Dispose 接收端：{st.DisposeReceiverMs:F3}ms");

            var after = new TimingStats();
            for (var i = 0; i < 60; i++) after.Add(TimedPresent(s));
            st.SendAfterReceiverGoneMs = after;
            Console.WriteLine($"[S3②] 接收端消失后继续发 60 帧：p50={after.P50:F4}ms max={after.Max:F4}ms");

            DetachTarget();
            st.DisposeSenderAfterMs = TimedDispose(s);
            Console.WriteLine($"[S3②] 随后 Dispose 发送端：{st.DisposeSenderAfterMs:F3}ms");
        }

        return st;
    }

    // ================================================================= S4

    /// <summary>
    /// S4：同名源重启。分两步，把「重启」和「变尺寸」拆开——否则分不清失败是因为重启还是因为尺寸变了。
    ///   ① 同名**同尺寸**重启（320×180 红 → 320×180 蓝）：已连上的接收端跟不跟？
    ///   ② 同名**变尺寸**重启（320×180 → 640×480 绿）：同一个已连上的接收端跟不跟？
    ///   ③ 新建接收端（= 生产上的「删掉再加回图层」，见 SpoutIn 类注释的恢复路径）能否立刻看到新尺寸。
    /// 结论直接决定 M3 闸门「同名源重启变尺寸可重连」在发送端一侧能不能放行、以及接收端要做什么。
    /// </summary>
    public RebuildStats RebuildResize()
    {
        var st = new RebuildStats();
        using var rxOld = new SpoutIn(_gpu.Device);
        var sA = new SpoutOut(_gpu, _name, TestW, TestH);
        if (!sA.IsActive)
        {
            sA.Dispose();
            return st;
        }

        Fill(sA, ColRed);
        var connect = 0;
        for (var i = 0; i < 200 && connect == 0; i++)
        {
            sA.PresentFrame();
            if (rxOld.Pump(true, _name)) connect = i + 1;
        }

        st.BeforeConnected = connect > 0;
        st.BeforeSize = $"{rxOld.SourceWidth}×{rxOld.SourceHeight}";
        st.BeforePixelsOk = Samples(rxOld).Any(IsRed);

        // ① 同名同尺寸重启：只换颜色
        DetachTarget();
        st.DisposeSenderMs = TimedDispose(sA);
        var sA2 = new SpoutOut(_gpu, _name, TestW, TestH);
        Fill(sA2, ColBlue);
        var same = PumpUntil(rxOld, _name, r => Samples(r).Any(IsBlue), 3000, () => sA2.PresentFrame());
        st.SameSizeRestartSeen = same.Hit;
        st.SameSizePumps = same.Pumps;
        st.SameSizeFrames = same.Frames;
        st.SameSizeMs = same.Ms;
        st.SameSizeSeenSize = $"{rxOld.SourceWidth}×{rxOld.SourceHeight}";
        st.SameSizeSamples = SampleText(rxOld);
        st.SameSizeLastError = rxOld.LastError;

        // ② 同名变尺寸重启
        DetachTarget();
        st.DisposeSameSizeSenderMs = TimedDispose(sA2);
        var sB = new SpoutOut(_gpu, _name, RebuildW, RebuildH);
        st.RebuiltActive = sB.IsActive;
        st.RebuiltSize = $"{sB.Width}×{sB.Height}";
        Fill(sB, ColGreen);
        var resized = PumpUntil(rxOld, _name,
            r => r.SourceWidth == RebuildW && r.SourceHeight == RebuildH && Samples(r).Any(IsGreen),
            3000, () => sB.PresentFrame());
        st.OldSeenPumps = resized.Pumps;
        st.OldSeenFrames = resized.Frames;
        st.OldSeenMs = resized.Ms;
        st.OldSeenSize = $"{rxOld.SourceWidth}×{rxOld.SourceHeight}";
        st.OldPixelsOk = Samples(rxOld).Any(IsGreen);
        st.OldSamples = SampleText(rxOld);
        st.OldLastError = rxOld.LastError;

        // ③ 新建接收端（生产上的恢复路径：删掉再加回图层 → 新 provider → 新 SpoutIn）
        using (var rxNew = new SpoutIn(_gpu.Device))
        {
            var fresh = PumpUntil(rxNew, _name,
                r => r.SourceWidth == RebuildW && r.SourceHeight == RebuildH && Samples(r).Any(IsGreen),
                3000, () => sB.PresentFrame());
            st.NewSeenPumps = fresh.Pumps;
            st.NewSeenFrames = fresh.Frames;
            st.NewSeenMs = fresh.Ms;
            st.NewSeenSize = $"{rxNew.SourceWidth}×{rxNew.SourceHeight}";
            st.NewPixelsOk = Samples(rxNew).Any(IsGreen);
            st.NewLastError = rxNew.LastError;
        }

        DetachTarget();
        st.DisposeRebuiltMs = TimedDispose(sB);
        return st;
    }

    // ================================================================= S5

    /// <summary>
    /// S5 慢消费 + 纹理借用期：
    ///   对照 A 无接收端（S1 的基准线）；B 有接收端但不消费；C 接收端同步消费。
    /// 若 B 相对 A 没有明显变慢，说明发送端不因下游不消费而阻塞/积压（Spout 不排队，只留最新一帧）。
    ///
    /// B 段里顺带验**借用期**：接收端已经取到红色帧后，发送端把共享纹理改成蓝色并连发 200 帧，
    /// 期间接收端一次都不 Pump——此时再读 <see cref="SpoutIn.FrontPixels"/>（借用句柄）必须还是
    /// 原来的那口红色数据（同一数组、逐字节不变）。若变了，说明上游写直接漏进了下游的借用缓冲
    /// （零拷贝别名），合成器读到一半就会被改，那是必须挡在接线前的问题。
    /// </summary>
    public SlowConsumerStats SlowConsumer(int frames, int warmup)
    {
        var st = new SlowConsumerStats();
        using var s = new SpoutOut(_gpu, _name, TestW, TestH);
        if (!s.IsActive) return st;

        var rx = new SpoutIn(_gpu.Device);
        try
        {
            Fill(s, ColRed);
            var connect = 0;
            for (var i = 0; i < 200 && connect == 0; i++)
            {
                s.PresentFrame();
                if (rx.Pump(true, _name)) connect = i + 1;
            }

            st.Connected = connect > 0;
            st.ConnectPumps = connect;
            if (!st.Connected) return st;

            // 借用期基线：此刻接收端手里这口帧
            var borrow = rx.FrontPixels;
            var snapshot = borrow is null ? Array.Empty<byte>() : (byte[])borrow.Clone();
            st.BorrowSeqBefore = rx.Seq;
            st.BorrowLength = snapshot.Length;

            // 换色：共享纹理内容变了，但接收端停手不取帧
            Fill(s, ColBlue);
            var stallStart = Stopwatch.GetTimestamp();
            for (var i = 0; i < warmup; i++) TimedPresent(s);
            for (var i = 0; i < frames; i++)
            {
                st.StalledSend.Add(TimedPresent(s));
                if (i < 3 || i % 50 == 0)
                    Console.WriteLine($"[S5-B] 不消费时 PresentFrame#{i} {st.StalledSend.Last:F4}ms");
            }

            // 借用期检查：不 Pump，直接再读那口借用数据
            var after = rx.FrontPixels;
            st.BorrowSameArray = ReferenceEquals(borrow, after);
            st.BorrowSeqAfter = rx.Seq;
            st.BorrowSeqUnchanged = st.BorrowSeqBefore == st.BorrowSeqAfter;
            st.BorrowChangedBytes = after is null || after.Length != snapshot.Length
                ? -1
                : CountDiff(snapshot, after);
            st.StallMs = Timing(stallStart);

            // 阶段 C：接收端同步消费
            for (var i = 0; i < warmup; i++) TimedPresent(s);
            for (var i = 0; i < frames; i++)
            {
                st.ConsumedSend.Add(TimedPresent(s));
                var t = Stopwatch.GetTimestamp();
                rx.Pump(true, _name);
                st.PumpMs.Add(Timing(t));
            }

            // 恢复取帧：最后发绿，看接收端拿到的是最新帧还是积压
            Fill(s, ColGreen);
            for (var i = 0; i < 15; i++) s.PresentFrame();
            var pumps = PumpUntil(rx, _name, r => Samples(r).Any(IsGreen), 2000);
            st.LatestPumps = pumps.Pumps;
            st.LatestMs = pumps.Ms;
            st.LatestIsGreen = pumps.Hit;
            st.LatestSamples = SampleText(rx);
        }
        finally
        {
            rx.Dispose();
        }

        return st;
    }

    /// <summary>逐字节比较，返回不同的字节数（只看一致与否，不改任何一侧）。</summary>
    private static int CountDiff(byte[] a, byte[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        var diff = Math.Abs(a.Length - b.Length);
        for (var i = 0; i < n; i++)
        {
            if (a[i] != b[i]) diff++;
        }

        return diff;
    }

    // ============================================================ 基础工具

    private void Fill(SpoutOut s, Color4 color)
    {
        var bmp = s.EnsureD2DBitmap(_d2d.Ctx);
        _d2d.Draw.SetTarget(bmp, s.Width, s.Height);
        ClearTo(color);
    }

    /// <summary>
    /// 在当前 D2D 目标上清成一色并**把 D2D 批次推到设备**（<c>Ctx.Flush</c>）。
    /// 不推的话「发送端已提交该颜色」这个前提就打了折——测试会分不清
    /// 「接收端没跟上」和「发送端还没画完」。
    /// </summary>
    private void ClearTo(Color4 color)
    {
        _d2d.Draw.Begin();
        _d2d.Draw.Clear(color);
        // ID2D1RenderTarget.Flush 只能在 BeginDraw/EndDraw 之间调（外面调会 D2DERR_WRONG_STATE）
        _d2d.Ctx.Flush(out _, out _);
        _d2d.Draw.End();
    }

    /// <summary>把 D2D 目标摘掉——sender.Dispose() 会释放位图，别让 ctx 还指着它。</summary>
    private void DetachTarget()
    {
        try { _d2d.Draw.SetTarget(null, 1, 1); }
        catch (Exception ex) { Log.Debug("[SenderProbe] 摘除 D2D 目标失败：" + ex.Message); }
    }

    private static double TimedPresent(SpoutOut s)
    {
        var t0 = Stopwatch.GetTimestamp();
        // 回执字段由另一条线并行补（PresentFrame 返回 void 或 bool 都编译得过）——
        // 本探针只测耗时，不依赖返回值。
        s.PresentFrame();
        return Timing(t0);
    }

    private static double Timing(long t0) =>
        (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

    private static double Warmup(SpoutOut s, int times)
    {
        var max = 0.0;
        for (var i = 0; i < times; i++)
        {
            var ms = TimedPresent(s);
            if (ms > max) max = ms;
        }

        return max;
    }

    /// <summary>
    /// 反复 Pump 直到 <paramref name="check"/> 成立。返回 (Pump 次数, 取到帧的次数, 是否命中, 耗时 ms)。
    /// <c>Pumps=0</c> 表示超时没等到；<c>Frames</c> 用来区分「一直没帧」和「有帧但内容/尺寸不对」——
    /// 后者是「接收端还抱着旧共享纹理」的证据，不是「没连上」。
    /// <paramref name="pumpTick"/> 可让调用方在每次 Pump 之间补一次发送（接收端刚开、还没内容时用）。
    /// </summary>
    private (int Pumps, int Frames, bool Hit, double Ms) PumpUntil(SpoutIn rx, string name,
        Func<SpoutIn, bool> check, int timeoutMs, Action? pumpTick = null)
    {
        var sw = Stopwatch.StartNew();
        var pumps = 0;
        var frames = 0;
        while (sw.Elapsed.TotalMilliseconds < timeoutMs)
        {
            pumpTick?.Invoke();
            if (rx.Pump(true, name)) frames++;
            pumps++;
            if (check(rx)) return (pumps, frames, true, sw.Elapsed.TotalMilliseconds);
        }

        return (pumps, frames, false, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>取 5 个采样点的颜色（中心 + 四角内缩）——只看一个点容易被 letterbox/边角骗。</summary>
    private static List<(byte R, byte G, byte B, byte A)> Samples(SpoutIn rx)
    {
        var px = rx.FrontPixels;
        var w = rx.SourceWidth;
        var h = rx.SourceHeight;
        if (px is null || w <= 0 || h <= 0 || px.Length < w * h * 4) return new List<(byte, byte, byte, byte)>();

        return new List<(byte, byte, byte, byte)>
        {
            Pixel(px, w, w / 2, h / 2),
            Pixel(px, w, w / 4, h / 4),
            Pixel(px, w, w - 1 - w / 4, h - 1 - h / 4),
            Pixel(px, w, 1, 1),
            Pixel(px, w, w - 2, h - 2),
        };
    }

    /// <summary>BGRA 排布取单点（<see cref="SpoutIn.FrontPixels"/> 是 BGRA）。</summary>
    private static (byte R, byte G, byte B, byte A) Pixel(byte[] px, int w, int x, int y)
    {
        var o = (y * w + x) * 4;
        return (px[o + 2], px[o + 1], px[o], px[o + 3]);
    }

    private static bool IsRed((byte R, byte G, byte B, byte A) c) =>
        c.R > 200 && c.G < 50 && c.B < 50 && c.A > 200;

    private static bool IsBlue((byte R, byte G, byte B, byte A) c) =>
        c.B > 200 && c.R < 50 && c.G < 50 && c.A > 200;

    private static bool IsGreen((byte R, byte G, byte B, byte A) c) =>
        c.G > 200 && c.R < 50 && c.B < 50 && c.A > 200;

    private static string SampleText(SpoutIn rx)
    {
        var s = Samples(rx);
        if (s.Count == 0) return "（无帧）";
        return string.Join(" ", s.Select(c => $"R{c.R}G{c.G}B{c.B}A{c.A}"));
    }

    private static double TimedDispose(IDisposable d)
    {
        var t0 = Stopwatch.GetTimestamp();
        d.Dispose();
        return Timing(t0);
    }
}

// =================================================================== 统计类

/// <summary>逐次耗时分布（与 Q1/Q3 同口径：<see cref="Probe.FastMs"/> / <see cref="Probe.BlockMs"/>）。</summary>
internal sealed class TimingStats
{
    private readonly List<double> _ms = new();

    public int Count => _ms.Count;
    public double Last => _ms.Count == 0 ? 0 : _ms[^1];
    public double Min { get; private set; } = -1;
    public double Max { get; private set; }
    public int FastCount { get; private set; }
    public int BlockHits { get; private set; }
    public double Avg => _ms.Count == 0 ? 0 : _ms.Average();
    public double P50 => P(0.50);
    public double P99 => P(0.99);

    public void Add(double ms)
    {
        _ms.Add(ms);
        if (ms > Max) Max = ms;
        if (Min < 0 || ms < Min) Min = ms;
        if (ms < Probe.FastMs) FastCount++;
        if (ms >= Probe.BlockMs) BlockHits++;
    }

    private double P(double q)
    {
        if (_ms.Count == 0) return 0;
        var sorted = new List<double>(_ms);
        sorted.Sort();
        return sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * q))];
    }
}

/// <summary>（耗时, 原生返回值）成对的分布——S1b 用来看 bool 的区分度。</summary>
internal sealed class ReceiptStats
{
    private readonly List<(double Ms, bool Ok)> _items = new();

    public int Count => _items.Count;
    public int TrueCount => _items.Count(x => x.Ok);
    public int FalseCount => _items.Count(x => !x.Ok);
    public double Min { get; private set; } = -1;
    public double Max { get; private set; }
    public double Avg => _items.Count == 0 ? 0 : _items.Average(x => x.Ms);
    public double P50 => P(0.50);
    public double P99 => P(0.99);

    public void Add(double ms, bool ok)
    {
        _items.Add((ms, ok));
        if (ms > Max) Max = ms;
        if (Min < 0 || ms < Min) Min = ms;
    }

    private double P(double q)
    {
        if (_items.Count == 0) return 0;
        var sorted = _items.Select(x => x.Ms).OrderBy(x => x).ToList();
        return sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * q))];
    }
}

/// <summary>全部 S 段结果的聚合（报告只读它）。</summary>
internal sealed class SenderResults
{
    public string SenderName { get; set; } = "";
    public int Frames { get; set; }
    public int SlowFrames { get; set; }
    public SendStats S1 { get; set; } = new("S1");
    public NativeReceiptStats S1b { get; set; } = new();
    public RoundTripStats S2 { get; set; } = new();
    public DisposeStats S3 { get; set; } = new();
    public RebuildStats S4 { get; set; } = new();
    public SlowConsumerStats S5 { get; set; } = new();
}

/// <summary>S1：无接收端发送的耗时分布。</summary>
internal sealed class SendStats
{
    public SendStats(string label) => Label = label;

    public string Label { get; }
    public string Size { get; set; } = "";
    public bool SenderActive { get; set; }
    public double WarmupMs { get; set; }
    public TimingStats Timing { get; } = new();

    public void Add(double ms) => Timing.Add(ms);
}

/// <summary>S1b：native SendTexture 回执的区分度。</summary>
internal sealed class NativeReceiptStats
{
    public bool OpenFailed { get; set; }
    public bool OpenOk { get; set; }
    public string Error { get; set; } = "";
    public bool ReceiverConnected { get; set; }
    public int ReceiverConnectPumps { get; set; }
    public ReceiptStats A { get; } = new();
    public ReceiptStats B { get; } = new();
    public uint NativeWidthNoRx { get; set; }
    public uint NativeHeightNoRx { get; set; }
    public uint NativeWidthWithRx { get; set; }
    public uint NativeHeightWithRx { get; set; }
    public double NativeFps { get; set; }
}

/// <summary>S2：尺寸/像素往返与换色延迟。</summary>
internal sealed class RoundTripStats
{
    public bool SizeOk { get; set; }
    public string Size { get; set; } = "（未取到）";
    public bool RedSeen { get; set; }
    public int RedConnectPumps { get; set; }
    public double RedSendMs { get; set; }
    public double RedLatencyMs { get; set; }
    public string RedSamples { get; set; } = "";
    public bool BlueSeen { get; set; }
    public int BluePumps { get; set; }
    public double BlueSendMs { get; set; }
    public double BlueLatencyMs { get; set; }
    public string BlueSamples { get; set; } = "";
    public long SeqAfterBlue { get; set; }
    public bool RedBackSeen { get; set; }
    public int RedBackPumps { get; set; }
    public double RedBackSendMs { get; set; }
    public double RedBackLatencyMs { get; set; }
}

/// <summary>S3：两个方向的 Dispose 耗时。</summary>
internal sealed class DisposeStats
{
    public int Case1ConnectedPumps { get; set; }
    public double DisposeSenderMs { get; set; }
    public double FirstPumpAfterSenderGoneMs { get; set; }
    public TimingStats PumpAfterGoneMs { get; set; } = new();
    public double ExitCode0AliveMs { get; set; }
    public int Case2ConnectedPumps { get; set; }
    public double DisposeReceiverMs { get; set; }
    public TimingStats SendAfterReceiverGoneMs { get; set; } = new();
    public double DisposeSenderAfterMs { get; set; }
}

/// <summary>S4：同名源重启（同尺寸 / 变尺寸）与恢复路径。</summary>
internal sealed class RebuildStats
{
    public bool BeforeConnected { get; set; }
    public string BeforeSize { get; set; } = "（未取到）";
    public bool BeforePixelsOk { get; set; }
    public double DisposeSenderMs { get; set; }

    // ① 同名同尺寸重启（只换颜色）
    public bool SameSizeRestartSeen { get; set; }
    public int SameSizePumps { get; set; }
    public int SameSizeFrames { get; set; }
    public double SameSizeMs { get; set; }
    public string SameSizeSeenSize { get; set; } = "（未取到）";
    public string SameSizeSamples { get; set; } = "";
    public string SameSizeLastError { get; set; } = "";
    public double DisposeSameSizeSenderMs { get; set; }

    // ② 同名变尺寸重启
    public bool RebuiltActive { get; set; }
    public string RebuiltSize { get; set; } = "";
    public int OldSeenPumps { get; set; }
    /// <summary>既有接收端在等待期间**取到帧**的次数——有帧但尺寸/内容不对 = 抱着旧共享纹理。</summary>
    public int OldSeenFrames { get; set; }
    public double OldSeenMs { get; set; }
    public string OldSeenSize { get; set; } = "（未取到）";
    public bool OldPixelsOk { get; set; }
    public string OldSamples { get; set; } = "";
    public string OldLastError { get; set; } = "";

    // ③ 新建接收端（生产恢复路径）
    public int NewSeenPumps { get; set; }
    public int NewSeenFrames { get; set; }
    public double NewSeenMs { get; set; }
    public string NewSeenSize { get; set; } = "（未取到）";
    public bool NewPixelsOk { get; set; }
    public string NewLastError { get; set; } = "";
    public double DisposeRebuiltMs { get; set; }
}

/// <summary>S5：慢消费反压 + 纹理借用期。</summary>
internal sealed class SlowConsumerStats
{
    public bool Connected { get; set; }
    public int ConnectPumps { get; set; }
    /// <summary>B 段：接收端连上但不取帧时的发送耗时。</summary>
    public TimingStats StalledSend { get; } = new();
    /// <summary>C 段：接收端同步消费时的发送耗时。</summary>
    public TimingStats ConsumedSend { get; } = new();
    /// <summary>C 段：每帧接收耗时。</summary>
    public TimingStats PumpMs { get; } = new();
    public int LatestPumps { get; set; }
    public double LatestMs { get; set; }
    public bool LatestIsGreen { get; set; }
    public string LatestSamples { get; set; } = "";

    // ---- 纹理借用期 ----
    public int BorrowLength { get; set; }
    public long BorrowSeqBefore { get; set; }
    public long BorrowSeqAfter { get; set; }
    public bool BorrowSeqUnchanged { get; set; }
    /// <summary>借用句柄是不是同一个数组实例（true = 期间没被换过缓冲）。</summary>
    public bool BorrowSameArray { get; set; }
    /// <summary>B 段期间借用数据的差异字节数（0 = 上游写入没漏进下游缓冲）。-1 = 长度都不对了。</summary>
    public int BorrowChangedBytes { get; set; } = -1;
    public double StallMs { get; set; }
}
