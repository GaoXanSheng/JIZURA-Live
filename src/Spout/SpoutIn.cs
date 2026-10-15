using JizuraLive.Logging;
using SpoutDXWrapper;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace JizuraLive.Spout;

/// <summary>
/// 外部 Spout 源的接收端（纯 Vortice D3D11 实现，零旧依赖）。
/// 把外部发送端（如 VTS 角色画面）的一帧收进**本设备上的自有纹理**，交给渲染管线直接用。
///
/// 【M7 零拷贝线 · 收帧路径 · 2026-10-15】收帧不再经过 CPU：
///   旧路径 = <c>CopyResource(staging)</c> → <c>Map</c> → 按 <c>w*4</c> 逐行 <c>Marshal.Copy</c>
///   到托管 byte[]，下游 <see cref="JizuraLive.Pv.SpoutSourceProvider"/> 再用
///   <c>CopyFromMemory</c> 传回 GPU。两头都在同一张显卡上，中间却走了两趟 PCIe 往返，
///   每帧还多分配一次 w*h*4 的托管大数组（640×480 约 1.2MB/帧，1280×720 约 3.7MB/帧）。
///   新路径 = 一次 **native 直收**（<c>ReceiveToExistingTexture(自有纹理)</c>，Plan A）进自有纹理，
///   下游用 <c>CreateBitmapFromDxgiSurface</c> 直接把这张纹理当 D2D 位图，全程无 Map/Unmap、
///   无 Marshal.Copy、无 byte[] 像素缓冲。
///
///   【裁决点 · 为什么是 Plan A】tools/SpoutProbe 的 G2 段实测（RTX 4060 Laptop）：
///     · 有源时 <c>ReceiveToExistingTexture(自有纹理)</c> 返回 true，且把 dest 从洋红写成了源色
///       （纯红 R255G0B0A255）——直收成立；无源时返回 false、dest 不变。
///     · 源指针 <c>ReceiveTexturePtr()</c> 连续调用恒定，源变尺寸/同名重连后都换新指针；
///       其 Description 为 640×480 B8G8R8A8_UNorm / Bind=SR|RT / Usage=Default / Misc=None，
///       并且把它 CopyResource 进我们自建的 BGRA8 纹理后回读正确——即**同设备**（D3D11 只允许同设备拷贝）。
///     · 陷阱：dest 尺寸与源不一致时该调用返回 true 却**不写**（G2 状态 C，静默 no-op），
///       所以目标纹理必须由我们按当前源 Description 建好。
///   备选 Plan B（<c>ReceiveTexturePtr()</c> + <c>_ctx.CopyResource(自有纹理, src)</c>，DOC §9 允许）
///   在同一次取证里也被验证可行（同设备拷贝回读命中）。两者实测成本（G1，各 2000 次样本）：
///   Plan B p50=0.0232/0.0293ms（640×480 / 1280×720）、Plan A p50=0.0507/0.0524ms——
///   Plan A 每次多约 25µs（native 内部还要自己查一次共享句柄），60fps 下占帧预算 0.15%，
///   所以按 G2 的裁决优先用 Plan A（native 自己管共享句柄的生命周期）；若某台机器/驱动上
///   Plan A 被证伪，切回 Plan B 只需换掉这一处调用。
///
///   **措辞纪律：这不是「零拷贝」**——native 内部仍搬了一次像素（G2 的同设备拷贝回读即其证据）。
///   本线交付的是「消除 CPU 往返」（DOC/PV-DIRECTOR-SYSTEM.md §9 只允许走到这一步；
///   共享视图直采需另一轮验证）。
///
///   不兼容格式（白名单见 <see cref="SpoutSourceFormat"/>）：**不拷、不出图**，只留一条可读诊断，
///   见 <see cref="FormatDiagnostic"/>——旧实现会把 10/16 位源按每像素 4 字节切成静默错色
///   （G1b 段用本地重放留下了改前数字：R10G10B10A2 源清成纯红，按 w*4 读出来是 R0G3B255A192）。
///
/// 【M1 第 6 步实测结论 · 2026-10-15 · RTX 4060 Laptop，见 DOC/PV-ENGINE-ARCHITECTURE.md 验证证据节】
/// 无人发送时 <see cref="SpoutReceiver.ReceiveTexturePtr"/> **立即返回**：20000 次连续 Pump
/// p50=0.0007ms、p99=0.001ms，&lt;1ms 占 19999/20000（唯一离群 18.9ms 是进程首帧 JIT/GC）。
/// 空转吞吐约 5×10^5 次/秒——不会把渲染线程关在门外。
/// 唯一例外：**发送端刚消失后的那一帧**，native 自己会等 ~67ms（6/6 轮确定性复现，
/// 直接调 native 也复现，托管侧无嫌疑）。所以停机握手仍必须先把 _stopPumping 置上再停线程，
/// 不能让渲染线程恰好撞上这一帧。数字与复现方式见 tools/SpoutProbe（Q4/Q5/Q6）。
/// </summary>
public sealed class SpoutIn : IDisposable
{
    private const int WaitLogMs = 10_000;
    private const int RetryBackoffMs = 2_000;
    private const int MaxFails = 5;

    private readonly ID3D11Device _device;
    private SpoutReceiver? _rx;
    private string _rxName = "";

    /// <summary>
    /// 两张自有帧纹理（ping-pong）。为什么要两张：下游拿到的是**借用**句柄，
    /// 而收帧线程与合成线程可能不是同一拍——单张纹理会出现「下游正在画、上游正在写」的撕裂。
    /// 双缓冲保证本轮借出去的那张，在下一轮 Pump 里不会被写入。
    /// 恒为 BGRA8/Default/RT|SR（非 BGRA8 源在 <see cref="Pump"/> 里已被白名单挡掉）。
    /// </summary>
    private readonly ID3D11Texture2D?[] _bufs = new ID3D11Texture2D?[2];

    /// <summary>最新一帧所在槽位（-1 = 当前没有可借出的帧）。受 <see cref="_pub"/> 保护。</summary>
    private int _front = -1;

    /// <summary>下一个要写入的槽位（-1 的反面：front 与 back 永远不同）。</summary>
    private int _back;

    private long _generation;

    /// <summary>已建成纹理的源参数（仅当 (w,h,fmt) 变化时才重建纹理）。</summary>
    private int _srcW = -1, _srcH = -1, _srcFmt = -1;

    /// <summary>最近一次**观测到**的源格式（含被白名单挡掉的那些；-1 = 还没看到过任何帧）。</summary>
    private int _seenFmt = -1;

    private readonly object _pub = new();
    private long _seq;

    private long _lastWaitLog = -WaitLogMs;
    private long _retryAfterTs;
    private int _fails;

    /// <summary>
    /// 连续失败 <see cref="MaxFails"/> 次后置位，此后 Pump 恒返回 false——**本实例内永不清除**。
    /// 这是有意的：失败来自 OpenDirectX/收帧/拷贝，属设备或源状态异常，重试无益。
    /// 恢复路径不在本对象内：删掉再添加图层会新建 <see cref="JizuraLive.Pv.SpoutSourceProvider"/>
    /// （连带新 SpoutIn），见 PvPipeline.ApplyLayerOps 的 pendingAdds/pendingRemoves。
    /// 不要在 Pump 里自作聪明清它。
    /// </summary>
    private bool _gaveUp;

    public string LastError { get; private set; } = "";

    public SpoutIn(ID3D11Device device) => _device = device;

    public long Seq => Interlocked.Read(ref _seq);
    public bool Connected { get; private set; }
    public int SourceWidth => _srcW < 0 ? 0 : _srcW;
    public int SourceHeight => _srcH < 0 ? 0 : _srcH;

    /// <summary>最近一次观测到的源格式（DXGI_FORMAT 数值，-1 = 还没看到过任何帧）。</summary>
    public int SourceFormat => _seenFmt;

    /// <summary>
    /// 源格式不在白名单时的可读诊断（<see cref="SpoutSourceFormat.DescribeUnsupported"/>）；
    /// 源格式正常时为 null。非 null 期间 <see cref="FrontTexture"/> 恒为 null（不出图）。
    /// </summary>
    public string? FormatDiagnostic { get; private set; }

    /// <summary>
    /// 纹理代次：每次重建纹理缓冲或 <see cref="Close"/> 都 +1。
    /// 下游用它判断「手里缓存的 surface 位图是不是已经指着被释放的纹理」。
    /// </summary>
    public long Generation => Interlocked.Read(ref _generation);

    /// <summary>
    /// 最新一帧的纹理（**借用**语义：到下一次 <see cref="Pump"/> 之前有效）。
    /// 双缓冲保证它在本轮内不会被下一帧覆盖；但下一次 Pump 可能重建/交换缓冲，
    /// 因此**不许跨 Pump 持有**。没有帧、或源格式不兼容时为 null。
    /// </summary>
    public ID3D11Texture2D? FrontTexture
    {
        get
        {
            lock (_pub) return _front < 0 ? null : _bufs[_front];
        }
    }

    /// <summary><see cref="FrontTexture"/> 所在槽位（0/1；-1 = 当前没有帧）。下游按它缓存 surface 位图。</summary>
    public int FrontIndex
    {
        get
        {
            lock (_pub) return _front;
        }
    }

    public bool Pump(bool enabled, string senderName)
    {
        if (_gaveUp) return false;
        if (!enabled)
        {
            if (_rx != null) Close();
            return false;
        }

        var now = Environment.TickCount64;
        if (now < _retryAfterTs) return false;

        var name = Normalize(senderName);
        if (_rx == null || !string.Equals(_rxName, name, StringComparison.Ordinal))
        {
            Close();
            if (!Open(name)) return false;
        }

        // native 无超时/取消参数，但实测无人发送时它立即返回（p50 0.0007ms，见类注释）。
        // 唯一的例外是「发送端刚消失后的那一帧」：native 会等 ~67ms（确定性，6/6 轮复现）。
        // 停机握手因此必须先让上层 StopPumping()，别让渲染线程撞上那一帧。
        IntPtr ptr;
        try
        {
            ptr = _rx!.ReceiveTexturePtr();
        }
        catch (Exception ex)
        {
            Fail("ReceiveTexturePtr", ex);
            return false;
        }

        if (ptr == IntPtr.Zero)
        {
            Connected = false;
            if (now - _lastWaitLog >= WaitLogMs)
            {
                _lastWaitLog = now;
                Log.Info($"[SpoutIn] 等待 Spout 源 '{name}'……");
            }
            return false;
        }

        try
        {
            // 只包一层壳读 Description，不持有所有权（native 的共享纹理归 sender）。
            using var src = new ID3D11Texture2D(ptr);
            var d = src.Description;
            if (d.Width <= 0 || d.Height <= 0) return false;

            int w = (int)d.Width;
            int h = (int)d.Height;
            int fmt = (int)d.Format;

            if (!SpoutSourceFormat.IsSupported(d.Format))
            {
                // 不兼容源：不拷、不出图，只留诊断。日志按「观测到的格式变化」去重，避免每帧刷屏。
                var changed = _seenFmt != fmt;
                _seenFmt = fmt;
                FormatDiagnostic = SpoutSourceFormat.DescribeUnsupported(d.Format);
                Connected = false;
                ClearFront();
                if (changed) Log.Warn("[SpoutIn] " + FormatDiagnostic);
                return false;
            }

            _seenFmt = fmt;
            FormatDiagnostic = null;

            if (!EnsureTargets(w, h, fmt))
            {
                Connected = false;
                return false;
            }

            var dst = _bufs[_back]!;

            // 【Plan A · G2 取证成立，2026-10-15 · RTX 4060 Laptop】native 的
            // ReceiveToExistingTexture(自有纹理) 会把源画面**直收**进我们传进去的那张纹理：
            // G2 把 dest 先清成洋红，有源时该调用返回 true 且 dest 回读变成源色（纯红）；
            // 无源时返回 false 且 dest 保持洋红。所以这里不再自己补一次 CopyResource。
            // 措辞纪律：这仍**不是**「零拷贝」——native 内部照样搬了一次像素（证据见 G2 的
            // 同设备 CopyResource 回读），本线交付的是「消除 CPU 往返」。
            // 已知陷阱（G2 状态 C 实测）：dest 尺寸与源不一致时 native 返回 true 却**不写**，
            // 是静默 no-op。所以尺寸必须由我们保证——dst 就是上面按当前源 Description 建的。
            if (!_rx!.ReceiveToExistingTexture(dst.NativePointer))
            {
                // 没写进去（源刚消失 / 句柄换手 / 目标尺寸不符）：本拍不出帧，不当作失败。
                Connected = false;
                return false;
            }

            lock (_pub)
            {
                _front = _back;
                _back ^= 1;
                _seq++;
            }

            Connected = true;
            _fails = 0;
            if (_lastWaitLog != 0) _lastWaitLog = 0;
            return true;
        }
        catch (Exception ex)
        {
            Fail("收帧/直收", ex);
            return false;
        }
    }

    /// <summary>没有可借出的帧了（不兼容格式 / 显式清空）。<b>不</b>动 <see cref="Seq"/>——它不是「新帧」。</summary>
    private void ClearFront()
    {
        lock (_pub) _front = -1;
    }

    private static string Normalize(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "VTubeStudioSpout" : name.Trim();

    private bool Open(string name)
    {
        try
        {
            var rx = new SpoutReceiver();
            rx.SetReceiverName(name);
            if (!rx.OpenDirectX(_device.NativePointer))
            {
                rx.Dispose();
                Fail($"SpoutReceiver.OpenDirectX 失败（源 '{name}'）", null);
                return false;
            }

            _rx = rx;
            _rxName = name;
            _lastWaitLog = 0;
            Log.Info($"[SpoutIn] 接收端就绪，等待源 '{name}'");
            return true;
        }
        catch (Exception ex)
        {
            Fail($"SpoutReceiver 创建失败（源 '{name}'）", ex);
            return false;
        }
    }

    private void Close()
    {
        Connected = false;
        var rx = _rx;
        _rx = null;
        _rxName = "";
        if (rx != null)
        {
            try { rx.Dispose(); }
            catch (Exception ex) { Log.Debug("[SpoutIn] Receiver.Dispose 失败：" + ex.Message); }
        }

        DisposeBuffers();
        Interlocked.Increment(ref _generation);
        lock (_pub) _seq++;
    }

    private void DisposeBuffers()
    {
        lock (_pub) { _front = -1; _back = 0; }
        for (var i = 0; i < _bufs.Length; i++)
        {
            _bufs[i]?.Dispose();
            _bufs[i] = null;
        }

        _srcW = _srcH = -1;
        _srcFmt = -1;
    }

    private void Fail(string what, Exception? ex)
    {
        _fails++;
        LastError = ex == null ? what : $"{what}：{ex.Message}";
        _retryAfterTs = Environment.TickCount64 + RetryBackoffMs;
        Close();
        if (_fails >= MaxFails)
        {
            _gaveUp = true;
            var msg = $"[SpoutIn] {what} 连续失败 {_fails} 次，停止重试";
            if (ex != null) Log.Error(msg + "：" + ex.Message, ex);
            else Log.Error(msg);
            return;
        }

        Log.Warn($"[SpoutIn] {what}（第 {_fails}/{MaxFails} 次，{RetryBackoffMs}ms 后重试）"
                 + (ex != null ? "：" + ex.Message : ""));
    }

    /// <summary>
    /// 建/重建两张自有帧纹理。只在 (w,h,源格式) 变化时重建——每帧重建纹理是显存抖动，
    /// 而源尺寸在 VTS 改分辨率时确实会变，所以这里必须能重建。
    /// <paramref name="dxgiFormat"/> 只用于判断「是否换了源格式」，纹理本身恒为 BGRA8
    /// （调用方已过 <see cref="SpoutSourceFormat.IsSupported"/> 白名单）。
    /// </summary>
    private bool EnsureTargets(int w, int h, int dxgiFormat)
    {
        if (_bufs[0] != null && _srcW == w && _srcH == h && _srcFmt == dxgiFormat)
            return true;

        DisposeBuffers();

        var desc = new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            // RT|SR：SR 是 D2D 位图（CreateBitmapFromDxgiSurface）与后续效果链要的；
            // MiscFlags 不给 Shared——这张纹理只在本进程本设备用，不需要跨进程共享句柄。
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };

        _bufs[0] = _device.CreateTexture2D(desc);
        _bufs[1] = _device.CreateTexture2D(desc);

        _srcW = w;
        _srcH = h;
        _srcFmt = dxgiFormat;
        Interlocked.Increment(ref _generation);

        Log.Info($"[SpoutIn] 源尺寸变更：{w}×{h} (Format {(Format)dxgiFormat})");
        return true;
    }

    public void Dispose()
    {
        Close();
    }
}
