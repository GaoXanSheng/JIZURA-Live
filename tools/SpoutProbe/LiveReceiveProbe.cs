using JizuraLive.Render.Draw;
using JizuraLive.Render.Gpu;
using JizuraLive.Spout;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SpoutProbe;

/// <summary>
/// LIVE 模式：对**真实运行中的外部 Spout 发送端**跑生产接收链，逐档给出「为什么没画面」。
///
/// 为什么必须有这一段：G1 是自发送（本进程 SpoutOut 发、SpoutIn 收），G1b 是合成非法格式，
/// 两者都在"我们完全掌控"的前提下验证——而用户报告的故障恰恰是**真实外部发送端收不到画面**
/// （VTS/Shoost 这类第三方发送端）。它们的格式、尺寸、共享句柄语义都可能与自发送不同，
/// 尤其 M7 给源格式加了白名单（非 BGRA8 不拷不出图），真实源一旦不是 BGRA8 就是"什么都没有"。
///
/// 逐档诊断（每档独立建 SpoutIn，避免上档状态污染）：
///   L1 枚举当前活跃发送端（SpoutNames，直读 Spout2 共享内存注册表）
///   L2 对每个发送端：生产 SpoutIn.Pump 60 次，统计 ptr 非零率 / Connected / 源尺寸 /
///      源格式 / FormatDiagnostic（白名单挡下的可读原因）/ FrontTexture 是否为空
///   L3 FrontTexture 非空时：CopyResource 进 staging → Map → 采样像素，判断画面是黑是亮
///      （"收得到句柄但画面全黑"与"根本收不到句柄"是两种完全不同的故障）
///
/// 退出码：0 = 至少一个发送端能收到非黑画面；6 = 能收到帧但画面全黑（疑似格式/alpha 问题）；
///         7 = 一个发送端都收不到帧（连接/命名/白名单问题）；8 = 没有活跃发送端可测。
/// </summary>
internal sealed class LiveReceiveProbe
{
    private const int PumpsPerSender = 60;

    private readonly D3DDevice _gpu;
    private readonly D2DHost _d2d;
    private readonly string? _onlySender;

    public LiveReceiveProbe(D3DDevice gpu, D2DHost d2d, string? onlySender)
    {
        _gpu = gpu;
        _d2d = d2d;
        _onlySender = string.IsNullOrWhiteSpace(onlySender) ? null : onlySender.Trim();
    }

    public int Run()
    {
        Console.WriteLine("======================================================================");
        Console.WriteLine("[LIVE] 真实外部 Spout 发送端 → 生产接收链 逐档诊断");
        Console.WriteLine("======================================================================");

        // —— L1 枚举 ——
        var senders = SpoutNames.Enumerate(refresh: true);
        Console.WriteLine($"[L1] 活跃发送端 {senders.Count} 个：{(senders.Count == 0 ? "（无）" : string.Join("、", senders))}");
        if (senders.Count == 0)
        {
            Console.WriteLine("[L1] → 结论：当前没有任何 Spout 发送端在跑。这不是产品故障——");
            Console.WriteLine("     先启动 VTS/Shoost/游戏（确认它们确实在发 Spout），再重跑本模式。");
            return 8;
        }

        var targets = _onlySender is null ? senders : senders.Where(s => s.Equals(_onlySender, StringComparison.OrdinalIgnoreCase)).ToList();
        if (targets.Count == 0)
        {
            Console.WriteLine($"[L1] → 指定的 --name '{_onlySender}' 不在活跃列表里（上面那些才是）。");
            return 8;
        }

        var anyFrames = false;
        var anyNonBlack = false;
        var diagnosticSaysFormat = false;

        foreach (var name in targets)
        {
            Console.WriteLine("----------------------------------------------------------------------");
            Console.WriteLine($"[L2] 源 '{name}'：生产 SpoutIn 连续 Pump {PumpsPerSender} 次");
            var (frames, nonBlack, detail) = ProbeSender(name);
            Console.WriteLine(detail);
            anyFrames |= frames > 0;
            anyNonBlack |= nonBlack;
            if (detail.Contains("FormatDiagnostic")) diagnosticSaysFormat = true;
        }

        Console.WriteLine("======================================================================");
        if (anyNonBlack)
        {
            Console.WriteLine("[LIVE] 结论：至少一个源收到了非黑画面 → 生产接收链对真实源可用。");
            Console.WriteLine("       若工作台里仍看不到画面，问题在图层接线/合成侧，不在收帧侧（把本段输出给我）。");
            return 0;
        }

        if (anyFrames)
        {
            Console.WriteLine("[LIVE] 结论：收得到帧但画面全黑 → 疑似格式/alpha 或源本身在发黑屏。");
            return 6;
        }

        Console.WriteLine("[LIVE] 结论：一个源都收不到帧 → 命名不匹配、白名单挡格式、或 native 直收失败。");
        Console.WriteLine("       逐条看上面的 FormatDiagnostic / ptr 非零率 / 收帧结果。");

        // 收不到帧且诊断指向格式白名单时：取证「跟随源格式」这条路能不能走通
        if (diagnosticSaysFormat)
        {
            FormatFollowForensics(targets);
        }

        return 7;
    }

    /// <summary>
    /// L4 格式取证：白名单挡下了源格式时，验证真正的修法（自有纹理**跟随源格式** + D2D 直接包）
    /// 是否成立。两个问题：
    ///   a) 自有纹理用源格式（如 R8G8B8A8）时，native ReceiveToExistingTexture 能写入且像素非黑？
    ///   b) 这张源格式纹理能被 D2D 包成位图（Premultiplied）？
    /// 都为真 → 修法是「跟随源格式」，不需要额外通道交换 pass。
    /// </summary>
    private void FormatFollowForensics(IReadOnlyList<string> targets)
    {
        Console.WriteLine("----------------------------------------------------------------------");
        Console.WriteLine("[L4] 格式取证：自有纹理跟随源格式（白名单挡下格式时的修法验证）");

        foreach (var name in targets.Take(1))
        {
            // 先拿源的真实格式与尺寸：用原生指针读 Description（与 SpoutIn 同手法）
            SpoutDXWrapper.SpoutReceiver? rx = null;
            try
            {
                rx = new SpoutDXWrapper.SpoutReceiver();
                rx.SetReceiverName(name);
                if (!rx.OpenDirectX(_gpu.Device.NativePointer))
                {
                    Console.WriteLine("      取证失败：接收端打不开");
                    return;
                }

                var ptr = rx.ReceiveTexturePtr();
                if (ptr == IntPtr.Zero)
                {
                    Console.WriteLine("      取证失败：拿不到源指针（源没在发帧？）");
                    return;
                }

                int w, h;
                Format srcFmt;
                using (var src = new ID3D11Texture2D(ptr))
                {
                    var d = src.Description;
                    w = (int)d.Width; h = (int)d.Height; srcFmt = d.Format;
                }

                Console.WriteLine($"      源：{w}×{h} {srcFmt}");

                // a) 自有纹理用源格式
                var desc = new Texture2DDescription
                {
                    Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                    Format = srcFmt,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.None,
                };
                using var owned = _gpu.Device.CreateTexture2D(desc);

                var wrote = rx.ReceiveToExistingTexture(owned.NativePointer);
                var (pixOk, pixDetail) = SamplePixels(owned);
                Console.WriteLine($"      a) 自有纹理用 {srcFmt}：直收返回={wrote}｜{pixDetail}");

                // b) D2D 包成位图（Premultiplied）
                try
                {
                    using var surface = owned.QueryInterface<IDXGISurface>();
                    using var bmp = _d2d.Ctx.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
                        new Vortice.DCommon.PixelFormat(srcFmt, Vortice.DCommon.AlphaMode.Premultiplied),
                        96f, 96f, BitmapOptions.None));
                    Console.WriteLine($"      b) D2D 包装 {srcFmt} 位图：**成功**（跟随源格式可行，不需通道交换 pass）");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"      b) D2D 包装 {srcFmt} 位图：**失败** → {ex.Message}");
                    Console.WriteLine("         → 跟随源格式不可行，需在收帧后加一次通道交换（swizzle）");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("      取证异常：" + ex.Message);
            }
            finally
            {
                rx?.Dispose();
            }
        }
    }

    private (int Frames, bool NonBlack, string Detail) ProbeSender(string name)
    {
        using var rx = new SpoutIn(_gpu.Device);
        var ptrNonZero = 0;
        var frames = 0;
        var connected = 0;
        var diagnostic = "";
        var lastResult = false;
        int w = 0, h = 0, fmt = -1;

        for (var i = 0; i < PumpsPerSender; i++)
        {
            lastResult = rx.Pump(true, name);
            if (lastResult) frames++;
            if (rx.Connected) connected++;
            if (rx.SourceWidth > 0) { w = rx.SourceWidth; h = rx.SourceHeight; }
            if (rx.SourceFormat >= 0) fmt = rx.SourceFormat;
            if (rx.FormatDiagnostic is { } dg) diagnostic = dg;
            // FrontTexture 非空说明 native 真的把一帧写进了自有纹理
            if (rx.FrontTexture is not null) ptrNonZero++;
            Thread.Sleep(8); // ~120Hz 节拍，给真实发送端留出发帧时间（60 次 ≈ 0.5s）
        }

        var lines = new List<string>
        {
            $"      Pump 成功 {frames}/{PumpsPerSender}｜Connected {connected}/{PumpsPerSender}｜FrontTexture 非空 {ptrNonZero}/{PumpsPerSender}",
            $"      源尺寸 {(w > 0 ? $"{w}×{h}" : "（未观测到）")}｜源格式 {(fmt >= 0 ? ((Format)fmt).ToString() : "（未观测到）")}",
        };
        if (!string.IsNullOrEmpty(diagnostic)) lines.Add($"      **FormatDiagnostic**：{diagnostic}");

        var nonBlack = false;
        if (rx.FrontTexture is { } tex)
        {
            var (ok, sample) = SamplePixels(tex);
            lines.Add(ok ? $"      像素采样：{sample}" : $"      像素采样失败：{sample}");
            nonBlack = ok && !sample.Contains("均值=0");
        }
        else
        {
            lines.Add("      FrontTexture 为空 → 本源当前**没有可画的帧**（下游必然黑屏）");
        }

        return (frames, nonBlack, string.Join("\n", lines));
    }

    /// <summary>把自有纹理读回 CPU 采样像素（诊断用，允许慢）：均值全 0 = 黑屏。</summary>
    private (bool Ok, string Detail) SamplePixels(ID3D11Texture2D tex)
    {
        try
        {
            var d = tex.Description;
            var stagingDesc = d with
            {
                BindFlags = BindFlags.None,
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
            };
            using var staging = _gpu.Device.CreateTexture2D(stagingDesc);
            _gpu.Context.CopyResource(staging, tex);
            var map = _gpu.Context.Map(staging, 0, MapMode.Read);
            try
            {
                var row = new byte[d.Width * 4];
                // 采样 5 行 x 5 列（不必读全图，诊断只要"是不是全黑"）
                long sum = 0;
                var n = 0;
                for (var ry = 0; ry < 5; ry++)
                {
                    var y = (int)(d.Height * (ry + 0.5) / 5);
                    var ptr = map.DataPointer + (nint)(y * map.RowPitch);
                    System.Runtime.InteropServices.Marshal.Copy(ptr, row, 0, (int)(d.Width * 4));
                    for (var rx2 = 0; rx2 < 5; rx2++)
                    {
                        var x = (int)(d.Width * (rx2 + 0.5) / 5);
                        var b = row[x * 4]; var g = row[x * 4 + 1]; var r = row[x * 4 + 2];
                        sum += r + g + b; n += 3;
                    }
                }
                var avg = n > 0 ? sum / (double)n : 0;
                return (true, $"均值={(int)avg}{(avg < 1 ? "（全黑）" : "")}｜样本 {n / 3} 点｜{d.Width}×{d.Height} {d.Format}");
            }
            finally { _gpu.Context.Unmap(staging, 0); }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
