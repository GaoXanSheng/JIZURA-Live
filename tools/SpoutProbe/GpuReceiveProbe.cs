using System.Diagnostics;
using System.Runtime.InteropServices;
using JizuraLive.Logging;
using JizuraLive.Pv;
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
using D3DMapFlags = Vortice.Direct3D11.MapFlags;

namespace SpoutProbe;

/// <summary>
/// M7「Spout 输入去 CPU 往返」的实测探针（<c>--gpu-input</c>）。四段，全部自发送、不依赖外部 App：
///
///   G1  收帧基线——生产类 <see cref="SpoutOut"/> 自发送 + <see cref="SpoutIn"/> 收，
///       640×480 与 1280×720 两档，逐次计时（p50/p95/p99）+ 每帧托管分配 + 源/自有纹理的
///       尺寸与格式。改前锚点（旧 CPU 回读路径）由本次运行的历史数据与本仓 DOC 给出。
///   G1b 恶意格式基线——raw native <see cref="SpoutSender"/> + <c>SetSenderFormat</c> 发
///       R10G10B10A2_UNorm / R16G16B16A16_Float。既复现旧解码（staging + Map + 按 w*4 逐行
///       Marshal.Copy）的错色数字，也验当前实现是否给诊断且不出图。
///   G2  native 语义取证——只取证不改产品：<c>ReceiveToExistingTexture(自有纹理)</c> 在
///       无源/有源/尺寸不匹配三种状态下的返回值与 dest 内容；<c>ReceiveTexturePtr()</c> 的指针
///       恒定性、变尺寸/同名重连后是否变化、以及该指针 Description 的格式/绑定/用途。
///   G3  生产路径——<see cref="SpoutSourceProvider"/>：换色是否在 N 次 Pump 内可见（不是陈旧帧）、
///       变尺寸后尺寸跟随且位图重建、源消失后不再出新帧、借用期内容不变。
///
/// 退出码即结论（与 Q/S 模式同一约定）：
///   0 = 通过；3 = native 阻塞；4 = 像素/尺寸不符；5 = 不兼容格式没给诊断或仍出图；
///   6 = 借用期泄漏；7 = 无样本。
/// </summary>
internal sealed class GpuReceiveProbe
{
    public const int W1 = 640, H1 = 480;
    public const int W2 = 1280, H2 = 720;
    private const int FmtW = 320, FmtH = 180;
    public const int MismatchW = 100, MismatchH = 100;
    private const int ConnectTimeoutMs = 3000;

    private static readonly Color4 ColRed = new(1f, 0f, 0f, 1f);
    private static readonly Color4 ColGreen = new(0f, 1f, 0f, 1f);
    private static readonly Color4 ColBlue = new(0f, 0f, 1f, 1f);
    private static readonly Color4 ColMagenta = new(1f, 0f, 1f, 1f);

    private readonly D3DDevice _gpu;
    private readonly D2DHost _d2d;
    private readonly string _name;

    public GpuReceiveProbe(D3DDevice gpu, D2DHost d2d, string name)
    {
        _gpu = gpu;
        _d2d = d2d;
        _name = name;
    }

    public GpuReceiveResults Run(int frames, int borrowFrames)
    {
        var r = new GpuReceiveResults { SenderName = _name, Frames = borrowFrames };
        r.G1A = ReceiveBaseline(W1, H1, frames);
        r.G1B = ReceiveBaseline(W2, H2, frames);
        r.G1b = MaliciousFormats();
        r.G2 = NativeSemantics();
        r.G3 = ProductionPath(borrowFrames);
        return r;
    }

    // ================================================================= G1

    /// <summary>
    /// G1：自发送 → <see cref="SpoutIn"/> 逐次收帧。连接阶段（含首帧 ~67ms 有界代价）不计入样本。
    /// 每帧的托管分配 = <c>GC.GetTotalAllocatedBytes(false)</c> 差 ÷ 帧数——旧路径每帧要 new 一个
    /// w*h*4 的托管数组（640×480≈1.2MB、1280×720≈3.7MB），新路径应当接近 0。
    /// </summary>
    private G1Stats ReceiveBaseline(int w, int h, int frames)
    {
        var st = new G1Stats { Width = w, Height = h, Frames = frames };
        using var send = new SpoutOut(_gpu, _name, w, h);
        if (!send.IsActive)
        {
            st.Error = "发送端未激活（SpoutOut.IsActive=false）";
            return st;
        }

        Fill(send, ColRed);
        using var rx = new SpoutIn(_gpu.Device);

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalMilliseconds < ConnectTimeoutMs && !rx.Connected)
        {
            send.PresentFrame();
            rx.Pump(true, _name);
            st.ConnectPumps++;
        }

        st.Connected = rx.Connected;
        if (!st.Connected)
        {
            st.Error = $"连接超时（{st.ConnectPumps} 次 Pump 没收到帧）";
            return st;
        }

        var alloc0 = GC.GetTotalAllocatedBytes(false);
        for (var i = 0; i < frames; i++)
        {
            send.PresentFrame();
            var t0 = Stopwatch.GetTimestamp();
            var ok = rx.Pump(true, _name);
            st.Timing.Add(Timing(t0));
            if (!ok) st.MissCount++;
        }

        st.AllocBytesPerFrame = (GC.GetTotalAllocatedBytes(false) - alloc0) / (double)frames;
        st.SourceWidth = rx.SourceWidth;
        st.SourceHeight = rx.SourceHeight;
        st.SourceFormatValue = rx.SourceFormat;
        st.SourceFormatName = ((Format)rx.SourceFormat).ToString();
        st.Generation = rx.Generation;
        st.Seq = rx.Seq;

        var tex = rx.FrontTexture;
        if (tex is not null)
        {
            var d = tex.Description;
            st.OwnedWidth = (int)d.Width;
            st.OwnedHeight = (int)d.Height;
            st.OwnedFormat = d.Format.ToString();
            st.OwnedBind = d.BindFlags.ToString();
            st.OwnedUsage = d.Usage.ToString();
            st.OwnedCpuAccess = d.CPUAccessFlags.ToString();
            st.OwnedMisc = d.MiscFlags.ToString();
            // 旧实现这里有一口 w*h*4 的托管像素缓冲；数字留着做改前/改后对比
            st.LegacyPixelBufferBytes = (long)st.OwnedWidth * st.OwnedHeight * 4;
            st.CenterPixel = CenterPixel(tex, st.OwnedWidth, st.OwnedHeight);
        }

        st.PixelsOk = Near(st.CenterPixel, ColRed);
        Console.WriteLine($"[G1] {w}×{h}：{st.Timing.Count} 次 Pump｜p50={st.Timing.P50:F4}ms p95={st.Timing.P95:F4}ms "
                          + $"p99={st.Timing.P99:F4}ms max={st.Timing.Max:F4}ms｜每帧托管分配={st.AllocBytesPerFrame:F1}B"
                          + $"｜源 {st.SourceWidth}×{st.SourceHeight} {st.SourceFormatName}｜自有纹理 {st.OwnedFormat} "
                          + $"{st.OwnedBind}/{st.OwnedUsage}/CPU={st.OwnedCpuAccess}/Misc={st.OwnedMisc}｜中心像素 {PixelText(st.CenterPixel)}");
        return st;
    }

    // ================================================================ G1b

    private G1bStats MaliciousFormats()
    {
        var st = new G1bStats();
        st.Cases.Add(FormatCase(Format.R10G10B10A2_UNorm, ColRed));
        // 16 位浮点用四通道都非零的颜色：这样「按 4 字节读」出来的错色一眼可辨，
        // 不会因为低半字恰好是 0 而看起来像「读到了透明黑」。
        st.Cases.Add(FormatCase(Format.R16G16B16A16_Float, new Color4(0.5f, 0.25f, 0.75f, 1f)));
        return st;
    }

    /// <summary>
    /// 用 raw native sender 发一路「格式不对」的源。两个观察点：
    ///   ① 旧解码复现——把旧 SpoutIn.cs:130-139 的三步（CopyResource 到同格式 staging → Map →
    ///      按 w*4 逐行 Marshal.Copy）在探针内原样重放，看它读出来的像素是什么样（错色/半行错位）。
    ///      这是「改前」数字：生产代码已经删掉那条路径，只有本地重放才能留下对比。
    ///   ② 当前实现——必须给非空诊断、且不出图（FrontTexture 为 null）。
    /// </summary>
    private FormatCaseStats FormatCase(Format fmt, Color4 clear)
    {
        var st = new FormatCaseStats { Format = fmt, FormatValue = (int)fmt, ClearColor = clear };
        var name = $"{_name}-fmt{(int)fmt}";
        var raw = new SpoutSender();
        ID3D11Texture2D? tex = null;
        try
        {
            raw.SetSenderName(name);
            raw.SetSenderFormat((int)fmt);
            if (!raw.OpenDirectX(_gpu.Device.NativePointer))
            {
                st.Error = "native SpoutSender.OpenDirectX 失败";
                return st;
            }

            var desc = new Texture2DDescription
            {
                Width = FmtW,
                Height = FmtH,
                MipLevels = 1,
                ArraySize = 1,
                Format = fmt,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.Shared,
            };
            tex = _gpu.Device.CreateTexture2D(desc);
            using (var rtv = _gpu.Device.CreateRenderTargetView(tex))
            {
                _gpu.Context.ClearRenderTargetView(rtv, clear);
            }

            _gpu.Context.Flush();
            for (var i = 0; i < 10; i++) raw.SendTexture(tex.NativePointer);
            st.Sent = true;

            st.Legacy = LegacyDecode(name);

            using var rx = new SpoutIn(_gpu.Device);
            var sw = Stopwatch.StartNew();
            var sawFrame = false;
            while (sw.Elapsed.TotalMilliseconds < 2000 && st.ReceiverPumps < 600)
            {
                raw.SendTexture(tex.NativePointer);
                sawFrame |= rx.Pump(true, name);
                st.ReceiverPumps++;
            }

            st.ReceiverSawFrame = sawFrame;
            st.ObservedFormat = rx.SourceFormat;
            st.ObservedFormatName = rx.SourceFormat >= 0 ? ((Format)rx.SourceFormat).ToString() : "（未看到）";
            st.Diagnostic = rx.FormatDiagnostic ?? "";
            st.FrontTextureNull = rx.FrontTexture is null;
            st.Connected = rx.Connected;

            Console.WriteLine($"[G1b] 源格式 {fmt}（{(int)fmt}）：旧解码复现 {st.Legacy.Text()}｜当前实现 "
                              + $"诊断={(string.IsNullOrEmpty(st.Diagnostic) ? "**空**" : "有")}｜出图={(!st.FrontTextureNull)}"
                              + $"｜Connected={st.Connected}");
            if (!string.IsNullOrEmpty(st.Diagnostic)) Console.WriteLine($"      诊断：{st.Diagnostic}");
        }
        catch (Exception ex)
        {
            st.Error = ex.Message;
            Log.Warn("[G1b] 格式探针异常：" + ex.Message);
        }
        finally
        {
            tex?.Dispose();
            raw.Dispose();
        }

        return st;
    }

    /// <summary>旧解码路径的本地重放（逐行对应旧 SpoutIn.cs:130-139，只用于留「改前」数字）。</summary>
    private LegacyDecodeStats LegacyDecode(string senderName)
    {
        var st = new LegacyDecodeStats();
        var rx = new SpoutReceiver();
        try
        {
            rx.SetReceiverName(senderName);
            if (!rx.OpenDirectX(_gpu.Device.NativePointer))
            {
                st.Error = "native receiver 打开失败";
                return st;
            }

            var ptr = rx.ReceiveTexturePtr();
            if (ptr == IntPtr.Zero)
            {
                st.Error = "native 没给出源纹理（ptr=0）——该格式下旧路径也读不到东西";
                return st;
            }

            using var src = new ID3D11Texture2D(ptr);
            var d = src.Description;
            st.SourceW = (int)d.Width;
            st.SourceH = (int)d.Height;
            st.SourceFormat = d.Format.ToString();

            using var stg = _gpu.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = d.Width,
                Height = d.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = d.Format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
            });

            _gpu.Context.CopyResource(stg, src);
            var mapped = _gpu.Context.Map(stg, 0, MapMode.Read, D3DMapFlags.None);
            try
            {
                st.RowPitch = (int)mapped.RowPitch;
                var rowBytes = st.SourceW * 4;
                st.DecodedBytes = rowBytes * st.SourceH;
                st.Pixels = new byte[st.DecodedBytes];
                for (var row = 0; row < st.SourceH; row++)
                {
                    Marshal.Copy(IntPtr.Add(mapped.DataPointer, (int)(row * mapped.RowPitch)),
                        st.Pixels, row * rowBytes, rowBytes);
                }
            }
            finally
            {
                _gpu.Context.Unmap(stg, 0);
            }

            st.CenterPixel = PixelAt(st.Pixels, st.SourceW, st.SourceH, st.SourceW / 2, st.SourceH / 2);
        }
        catch (Exception ex)
        {
            st.Error = ex.Message;
        }
        finally
        {
            rx.Dispose();
        }

        return st;
    }

    // ================================================================= G2

    /// <summary>
    /// G2：native 语义取证（只取证，不改产品）。三种状态各记「返回值 + dest 内容」——
    /// dest 每次都先清成洋红，这样「native 到底有没有写进我们这张纹理」一眼可判：
    /// 回读仍是洋红 = 它没写我们这张（直收不成立）；回读是源色 = 它写了（直收成立）。
    /// </summary>
    private G2Stats NativeSemantics()
    {
        var st = new G2Stats();
        using var dest = CreateOwned(W1, H1);
        using var destMismatch = CreateOwned(MismatchW, MismatchH);
        var rx = new SpoutReceiver();
        try
        {
            rx.SetReceiverName(_name);
            if (!rx.OpenDirectX(_gpu.Device.NativePointer))
            {
                st.Error = "native receiver 打开失败";
                return st;
            }

            // 状态 A：无源（sender 还没建）
            ClearOwned(dest, W1, H1, ColMagenta);
            st.NoSourceReturn = rx.ReceiveToExistingTexture(dest.NativePointer);
            st.NoSourcePixel = CenterPixel(dest, W1, H1);

            // 状态 B：有源（同尺寸）
            using var send = new SpoutOut(_gpu, _name, W1, H1);
            st.SenderActive = send.IsActive;
            if (!st.SenderActive) return st;

            Fill(send, ColRed);
            for (var i = 0; i < 30; i++) send.PresentFrame();
            for (var i = 0; i < 200 && rx.ReceiveTexturePtr() == IntPtr.Zero; i++) send.PresentFrame();

            st.SrcPtrBefore = rx.ReceiveTexturePtr();
            st.SrcPtrRepeat = rx.ReceiveTexturePtr();
            st.PtrStable = st.SrcPtrBefore != IntPtr.Zero && st.SrcPtrBefore == st.SrcPtrRepeat;
            DescribePtr(st.SrcPtrBefore, st);

            ClearOwned(dest, W1, H1, ColMagenta);
            st.WithSourceReturn = rx.ReceiveToExistingTexture(dest.NativePointer);
            st.WithSourcePixel = CenterPixel(dest, W1, H1);
            st.SameDeviceCopyOk = SameDeviceCopy(st.SrcPtrBefore, ColRed, out var copyPixel);
            st.SameDeviceCopyPixel = copyPixel;

            // 状态 C：尺寸故意不匹配（dest 比源小）
            ClearOwned(destMismatch, MismatchW, MismatchH, ColMagenta);
            st.MismatchReturn = rx.ReceiveToExistingTexture(destMismatch.NativePointer);
            st.MismatchPixel = CenterPixel(destMismatch, MismatchW, MismatchH);

            // 指针在「源变尺寸」与「同名重连」后是否变化
            send.Dispose();
            st.PtrAfterSourceGone = rx.ReceiveTexturePtr();
            using (var send2 = new SpoutOut(_gpu, _name, W2, H2))
            {
                Fill(send2, ColGreen);
                for (var i = 0; i < 30; i++) send2.PresentFrame();
                for (var i = 0; i < 200 && rx.ReceiveTexturePtr() == IntPtr.Zero; i++) send2.PresentFrame();
                st.PtrAfterResize = rx.ReceiveTexturePtr();
                st.ResizeSizeOk = rx.GetWidth() == W2 && rx.GetHeight() == H2;
            }

            using (var send3 = new SpoutOut(_gpu, _name, W2, H2))
            {
                Fill(send3, ColBlue);
                for (var i = 0; i < 30; i++) send3.PresentFrame();
                for (var i = 0; i < 200 && rx.ReceiveTexturePtr() == IntPtr.Zero; i++) send3.PresentFrame();
                st.PtrAfterRestart = rx.ReceiveTexturePtr();
            }

            st.PtrChangedOnResize = st.PtrAfterResize != IntPtr.Zero && st.PtrAfterResize != st.SrcPtrBefore;
            st.PtrChangedOnRestart = st.PtrAfterRestart != IntPtr.Zero && st.PtrAfterRestart != st.PtrAfterResize;

            Console.WriteLine($"[G2] 无源：返回={st.NoSourceReturn} dest={PixelText(st.NoSourcePixel)}"
                              + $"｜有源：返回={st.WithSourceReturn} dest={PixelText(st.WithSourcePixel)}（源为纯红）"
                              + $"｜尺寸不匹配：返回={st.MismatchReturn} dest={PixelText(st.MismatchPixel)}（dest 为纯洋红）");
            Console.WriteLine($"[G2] 源指针：恒定={(st.PtrStable ? "是" : "否")}｜变尺寸后变化={(st.PtrChangedOnResize ? "是" : "否")}"
                              + $"｜同名重连后变化={(st.PtrChangedOnRestart ? "是" : "否")}"
                              + $"｜同设备拷贝像素正确={(st.SameDeviceCopyOk ? "是" : "否")}");
            Console.WriteLine($"[G2] 源纹理 Description：{st.SrcW}×{st.SrcH} {st.SrcFormat}"
                              + $"｜Bind={st.SrcBind}｜Usage={st.SrcUsage}｜CPU={st.SrcCpuAccess}｜Misc={st.SrcMisc}");
        }
        catch (Exception ex)
        {
            st.Error = ex.Message;
            Log.Warn("[G2] native 语义探针异常：" + ex.Message);
        }
        finally
        {
            rx.Dispose();
        }

        return st;
    }

    private void DescribePtr(IntPtr ptr, G2Stats st)
    {
        if (ptr == IntPtr.Zero) return;
        using var t = new ID3D11Texture2D(ptr);
        var d = t.Description;
        st.SrcW = (int)d.Width;
        st.SrcH = (int)d.Height;
        st.SrcFormat = d.Format.ToString();
        st.SrcBind = d.BindFlags.ToString();
        st.SrcUsage = d.Usage.ToString();
        st.SrcCpuAccess = d.CPUAccessFlags.ToString();
        st.SrcMisc = d.MiscFlags.ToString();
    }

    /// <summary>同设备证据：把源指针当纹理，CopyResource 进我们自建的 BGRA8 纹理再回读。</summary>
    private bool SameDeviceCopy(IntPtr srcPtr, Color4 expect, out (byte R, byte G, byte B, byte A) pixel)
    {
        pixel = default;
        if (srcPtr == IntPtr.Zero) return false;
        try
        {
            using var src = new ID3D11Texture2D(srcPtr);
            var d = src.Description;
            using var dst = CreateOwned((int)d.Width, (int)d.Height);
            _gpu.Context.CopyResource(dst, src);
            pixel = CenterPixel(dst, (int)d.Width, (int)d.Height);
            return Near(pixel, expect);
        }
        catch (Exception ex)
        {
            Log.Warn("[G2] 同设备拷贝取证失败：" + ex.Message);
            return false;
        }
    }

    // ================================================================= G3

    /// <summary>
    /// G3：生产路径逐条命中——换色（不是陈旧帧）、借用期、变尺寸+位图重建、源消失后不再出新帧。
    /// 不兼容格式一条在 G1b 里取证（同一套判据，报告合并判定）。
    /// </summary>
    private G3Stats ProductionPath(int frames)
    {
        var st = new G3Stats();

        var send = new SpoutOut(_gpu, _name, W1, H1);
        if (!send.IsActive)
        {
            st.Error = "发送端未激活（SpoutOut.IsActive=false）";
            send.Dispose();
            return st;
        }

        // ---- A 换色 + B 借用期：用 SpoutIn 取像素真相（provider 不暴露纹理） ----
        using (var rx = new SpoutIn(_gpu.Device))
        {
            Fill(send, ColRed);
            st.RedPumps = PumpUntilPixel(send, rx, ColRed, ConnectTimeoutMs, out var redSeen, st);
            st.RedSeen = redSeen;

            Fill(send, ColBlue);
            st.BluePumps = PumpUntilPixel(send, rx, ColBlue, ConnectTimeoutMs, out var blueSeen, st);
            st.BlueSeen = blueSeen;

            // 借用期：接收端停手，上游连改共享纹理
            var borrow = rx.FrontTexture;
            var borrowSeq = rx.Seq;
            var borrowPixels = borrow is null ? [] : ReadAll(borrow, rx.SourceWidth, rx.SourceHeight);
            st.BorrowLength = borrowPixels.Length;

            Fill(send, ColGreen);
            for (var i = 0; i < frames; i++) send.PresentFrame();

            var after = rx.FrontTexture;
            st.BorrowSameTexture = borrow is not null && ReferenceEquals(borrow, after);
            st.BorrowSeqUnchanged = borrowSeq == rx.Seq;
            var afterPixels = after is null ? [] : ReadAll(after, rx.SourceWidth, rx.SourceHeight);
            st.BorrowChangedBytes = afterPixels.Length != borrowPixels.Length
                ? -1
                : CountDiff(borrowPixels, afterPixels);
            Console.WriteLine($"[G3] 借用期：停手期间上游发了 {frames} 帧并改成绿｜同一张纹理实例={(st.BorrowSameTexture ? "是" : "否")}"
                              + $"｜Seq 未变={(st.BorrowSeqUnchanged ? "是" : "否")}｜差异字节={st.BorrowChangedBytes}（共 {st.BorrowLength} 字节）");
        }

        // ---- C 变尺寸 + 位图重建：走 provider（生产路径） ----
        // premultiplied: true 是**故意的**：G3 的 alpha 能力边界实测表明 D2D 的 surface 位图
        // 只接受 Premultiplied（声明 Straight 会 D2DERR/UNSUPPORTEDPIXELFORMAT）。声明 Straight
        // 的那条路单独在下面的 E 段取证（要求「一条诊断 + 不出图」，不静默变色）。
        SpoutOut? send2 = null;
        var ctx = new PvFrameContext(0, 0, 0, W1, H1);
        using (var provider = new SpoutSourceProvider("probe_gpu", _gpu, _d2d.Ctx, _name, premultiplied: true))
        {
            var img1 = PumpProvider(provider, ctx, send, ConnectTimeoutMs);
            st.BeforeSize = $"{provider.SourceWidth}×{provider.SourceHeight}";
            st.BeforeBitmapOk = img1.Bitmap is not null;

            send.Dispose();
            send2 = new SpoutOut(_gpu, _name, W2, H2);
            Fill(send2, ColGreen);
            st.ResizePumps = PumpProviderUntilSize(provider, ctx, send2, W2, H2, ConnectTimeoutMs, out var img2, st);
            st.AfterSize = $"{provider.SourceWidth}×{provider.SourceHeight}";
            st.SizeFollowed = provider.SourceWidth == W2 && provider.SourceHeight == H2;
            st.BitmapRebuilt = img2.Bitmap is not null && !ReferenceEquals(img1.Bitmap, img2.Bitmap);
            st.BitmapSizeOk = img2.Width == W2 && img2.Height == H2;
        }

        // ---- D 源消失：不再出新帧，最后一帧内容不变（独立副本，不是悬垂共享纹理） ----
        if (send2 is not null)
        {
            using var rx2 = new SpoutIn(_gpu.Device);
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalMilliseconds < ConnectTimeoutMs && !rx2.Connected)
            {
                send2.PresentFrame();
                rx2.Pump(true, _name);
            }

            st.GoneConnected = rx2.Connected;
            var texBefore = rx2.FrontTexture;
            var seqBefore = rx2.Seq;
            var pixelsBefore = texBefore is null ? [] : ReadAll(texBefore, rx2.SourceWidth, rx2.SourceHeight);
            st.GoneHadFrame = texBefore is not null;

            send2.Dispose();
            double firstGoneMs = 0;
            for (var i = 0; i < 30; i++)
            {
                var t0 = Stopwatch.GetTimestamp();
                rx2.Pump(true, _name);
                var ms = Timing(t0);
                // 源消失后的第一帧就是 M1 记录的那笔已知有界代价（~67ms）：单独记，
                // 不参与「native 阻塞」判定，但超出上界仍算失败。
                if (i == 0) firstGoneMs = ms;
                else st.Steady.Add(ms);
            }

            st.KnownCost.Add(("源消失后首帧", firstGoneMs));

            st.AfterGoneConnected = rx2.Connected;
            st.AfterGoneSeqUnchanged = seqBefore == rx2.Seq;
            st.AfterGoneSameTexture = texBefore is not null && ReferenceEquals(texBefore, rx2.FrontTexture);
            var texAfter = rx2.FrontTexture;
            var pixelsAfter = texAfter is null ? [] : ReadAll(texAfter, rx2.SourceWidth, rx2.SourceHeight);
            st.AfterGoneChangedBytes = pixelsAfter.Length != pixelsBefore.Length
                ? -1
                : CountDiff(pixelsBefore, pixelsAfter);

            Console.WriteLine($"[G3] 源消失后：Pump 30 次｜首帧 {firstGoneMs:F3}ms｜Connected={st.AfterGoneConnected}"
                              + $"｜Seq 未变={(st.AfterGoneSeqUnchanged ? "是" : "否")}"
                              + $"｜同一张纹理实例={(st.AfterGoneSameTexture ? "是" : "否")}｜最后一帧差异字节={st.AfterGoneChangedBytes}");
        }

        send2?.Dispose();

        // ---- E alpha 能力边界：D2D surface 位图对 Straight / Premultiplied 的取舍 ----
        var alphaName = _name + "-alpha";
        using (var sendAlpha = new SpoutOut(_gpu, alphaName, W1, H1))
        {
            Fill(sendAlpha, ColRed);
            using (var pStraight = new SpoutSourceProvider("probe_alpha_s", _gpu, _d2d.Ctx, alphaName, premultiplied: false))
            {
                st.StraightImageOk = PumpProvider(pStraight, ctx, sendAlpha, ConnectTimeoutMs).Bitmap is not null;
            }

            using (var pPremul = new SpoutSourceProvider("probe_alpha_p", _gpu, _d2d.Ctx, alphaName, premultiplied: true))
            {
                st.PremultipliedImageOk = PumpProvider(pPremul, ctx, sendAlpha, ConnectTimeoutMs).Bitmap is not null;
            }
        }

        Console.WriteLine($"[G3] alpha 能力边界（surface 位图）：声明 Straight（生产 4 参构造的默认）→ 出图={st.StraightImageOk}"
                          + $"｜声明 Premultiplied → 出图={st.PremultipliedImageOk}");

        Console.WriteLine($"[G3] 变尺寸：{st.BeforeSize} → {st.AfterSize}｜尺寸跟随={(st.SizeFollowed ? "是" : "否")}"
                          + $"｜位图重建={(st.BitmapRebuilt ? "是" : "否")}｜位图尺寸正确={(st.BitmapSizeOk ? "是" : "否")}"
                          + $"（{st.ResizePumps} 次 Pump）");
        Console.WriteLine($"[G3] 换色：红={st.RedSeen}（{st.RedPumps} 次 Pump）｜蓝={st.BlueSeen}（{st.BluePumps} 次 Pump）");
        return st;
    }

    private int PumpUntilPixel(SpoutOut send, SpoutIn rx, Color4 want, int timeoutMs, out bool hit, G3Stats st)
    {
        hit = false;
        var sw = Stopwatch.StartNew();
        var pumps = 0;
        while (sw.Elapsed.TotalMilliseconds < timeoutMs)
        {
            send.PresentFrame();
            var t0 = Stopwatch.GetTimestamp();
            rx.Pump(true, _name);
            var ms = Timing(t0);
            // 连接阶段（首帧）不计入样本：与 G1 同口径——那一拍混着首次建纹理/JIT，
            // 不是稳态收帧成本，也不是「源消失后的有界代价」。
            if (pumps > 0) st.Steady.Add(ms);
            pumps++;

            var tex = rx.FrontTexture;
            if (tex is not null && Near(CenterPixel(tex, rx.SourceWidth, rx.SourceHeight), want))
            {
                hit = true;
                break;
            }
        }

        return pumps;
    }

    private PvLayerImage PumpProvider(SpoutSourceProvider p, in PvFrameContext ctx, SpoutOut send, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        var img = PvLayerImage.Empty;
        while (sw.Elapsed.TotalMilliseconds < timeoutMs)
        {
            send.PresentFrame();
            p.PumpFrame();
            img = p.Pump(ctx);
            if (img.Bitmap is not null) break;
        }

        return img;
    }

    private int PumpProviderUntilSize(SpoutSourceProvider p, in PvFrameContext ctx, SpoutOut send,
        int wantW, int wantH, int timeoutMs, out PvLayerImage img, G3Stats st)
    {
        var sw = Stopwatch.StartNew();
        img = PvLayerImage.Empty;
        var pumps = 0;
        while (sw.Elapsed.TotalMilliseconds < timeoutMs)
        {
            send.PresentFrame();
            var t0 = Stopwatch.GetTimestamp();
            p.PumpFrame();
            var ms = Timing(t0);
            if (pumps == 0) st.KnownCost.Add(("变尺寸重连后首帧", ms));
            else st.Steady.Add(ms);
            pumps++;
            img = p.Pump(ctx);
            if (p.SourceWidth == wantW && p.SourceHeight == wantH && img.Width == wantW && img.Bitmap is not null)
                break;
        }

        return pumps;
    }

    // ============================================================ 基础工具

    private ID3D11Texture2D CreateOwned(int w, int h) =>
        _gpu.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });

    /// <summary>把一张自有纹理清成纯色（借 D2D 目标走一遍，D3D11 的 ClearRenderTargetView 也行，
    /// 这里统一走 D2D 以便和合成器看到的 alpha 语义一致）。</summary>
    private void ClearOwned(ID3D11Texture2D tex, int w, int h, Color4 color)
    {
        using var surf = tex.QueryInterface<IDXGISurface>();
        using var bmp = _d2d.Ctx.CreateBitmapFromDxgiSurface(surf, new BitmapProperties1(
            new DCommonPixelFormat(Format.B8G8R8A8_UNorm, DCommonAlphaMode.Premultiplied),
            96f, 96f, BitmapOptions.Target));
        _d2d.Draw.SetTarget(bmp, w, h);
        _d2d.Draw.Begin();
        _d2d.Draw.Clear(color);
        _d2d.Ctx.Flush(out _, out _);
        _d2d.Draw.End();
        _d2d.Draw.SetTarget(null, 1, 1); // 摘掉：native 要往同一张纹理写，别让 D2D 还占着
    }

    /// <summary>把发送端的 D2D 目标清成一色并推给设备（不推的话「已提交该颜色」这个前提就打折）。</summary>
    private void Fill(SpoutOut s, Color4 color)
    {
        var bmp = s.EnsureD2DBitmap(_d2d.Ctx);
        _d2d.Draw.SetTarget(bmp, s.Width, s.Height);
        _d2d.Draw.Begin();
        _d2d.Draw.Clear(color);
        _d2d.Ctx.Flush(out _, out _);
        _d2d.Draw.End();
    }

    private (byte R, byte G, byte B, byte A) CenterPixel(ID3D11Texture2D tex, int w, int h) =>
        PixelAt(ReadAll(tex, w, h), w, h, w / 2, h / 2);

    private byte[] ReadAll(ID3D11Texture2D tex, int w, int h)
    {
        if (w <= 0 || h <= 0) return Array.Empty<byte>();
        using var img = GpuReadback.ToImage(_gpu, tex, w, h);
        return img.BgraPixels;
    }

    private static (byte R, byte G, byte B, byte A) PixelAt(byte[] px, int w, int h, int x, int y)
    {
        if (px.Length < w * h * 4 || x < 0 || y < 0 || x >= w || y >= h) return default;
        var o = (y * w + x) * 4;
        return (px[o + 2], px[o + 1], px[o], px[o + 3]);
    }

    private static string PixelText((byte R, byte G, byte B, byte A) c) => $"R{c.R}G{c.G}B{c.B}A{c.A}";

    private static bool Near((byte R, byte G, byte B, byte A) c, Color4 want, int tol = 24)
    {
        static int E(float v) => (int)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f);
        return Math.Abs(c.R - E(want.R)) <= tol && Math.Abs(c.G - E(want.G)) <= tol
               && Math.Abs(c.B - E(want.B)) <= tol && Math.Abs(c.A - E(want.A)) <= tol;
    }

    private static int CountDiff(byte[] a, byte[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        var diff = Math.Abs(a.Length - b.Length);
        for (var i = 0; i < n; i++)
            if (a[i] != b[i]) diff++;
        return diff;
    }

    private static double Timing(long t0) =>
        (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
}
