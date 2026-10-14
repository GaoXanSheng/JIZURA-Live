// Spout 输入端：把外部 Spout 源（VTS 角色画面 / 游戏 / 桌面捕获等）收进本进程，读回 CPU 位图后作为
// 引擎的「实时层」按槽位合成进我们的出图面——从此源与歌词在**我们这边**合成，输出一路流给 Shoost/OBS。
//
// 线程纪律：整个类只在 Spout 泵线程上使用（与 SpoutOut 的 Channel 同一条纪律：D3D11 设备与
// ImmediateContext 都不跨线程并发）。接收端建在同一块设备上（SpoutDX 在设备上打开发送端共享纹理），
// 因此不需要第二块设备，也不会与 ANGLE 的渲染线程抢上下文。
//
// 每帧路径（1080p 实测量级）：ReceiveTexturePtr（句柄交换，零拷贝）→ CopyResource 到 staging
// （~0.3ms + GPU 同步 ~1ms）→ Map 逐行 memcpy 进 SKBitmap（~1.5ms）→ 翻面发布。
// 这条 CPU 往返是刻意的取舍：接收端与预览两条光栅路线（GPU/CPU）共用同一份位图，且 Skia 侧
// 怎么画都行（模糊/发光/色相都能直接上）。将来要省掉这趟往返，可在这层换成
// EGL_ANGLE_d3d_texture_client_buffer 把源纹理直接包成 GL 纹理给 Skia（零拷贝），接口不用动。
//
// 格式：源纹理的 DXGI 格式决定 SKBitmap 的 SKColorType（BGRA/RGBA/10-10-10-2/F16 都认），
// 不认识就只记一次日志并跳过——绝不猜格式（猜错的表现是画面正常但颜色错位或全黑）。
// alpha：Spout 惯例是**直通（非预乘）**，故默认 Unpremul；源确实预乘时由设置里勾选改判。

using System.Runtime.InteropServices;
using JizuraLive.Logging;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SkiaSharp;
using SpoutDXWrapper;
using Buffer = System.Buffer;
using Device = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;

namespace JizuraLive.Spout;

/// <summary>
/// 外部 Spout 源的接收端 + 最新帧发布。
/// 使用方：Spout 泵线程 <see cref="Pump"/>（收帧 + 画进输出面）、预览线程 <see cref="TryCopyLatest"/>
/// （镜像不可用时预览自己渲染，需要同一份位图）。
/// </summary>
public sealed class SpoutIn : IDisposable
{
    /// <summary>找不到源时的等待日志间隔（VTS 没开/名字写错时别刷屏）。</summary>
    private const int WaitLogMs = 10_000;

    /// <summary>接收端报错后的重连退避（避免逐帧重建接收端）。</summary>
    private const int RetryBackoffMs = 2_000;

    /// <summary>连续失败多少次后彻底放弃（设备移除等持续性故障；改设置/重启管线才会再试）。</summary>
    private const int MaxFails = 5;

    private readonly Device _device;

    private SpoutReceiver? _rx;
    private string _rxName = "";     // 已建接收端对应的源名（"" = 未建）

    private Texture2D? _stg;         // staging 读回（尺寸/格式随源）
    private int _srcW = -1, _srcH = -1, _srcFmt = -1;
    private int _srcBpp = 4;         // 源每像素字节数（F16 是 8，逐行拷贝按它算）

    private readonly object _pub = new();
    private SKBitmap? _front;        // 已发布（泵线程画它、预览线程拷它）
    private SKBitmap? _back;         // 泵线程写它
    private long _seq;               // 发布序号（帧内容更新即 ++）

    private long _lastWaitLog = -WaitLogMs;
    private long _retryAfterTs;
    private int _fails;
    private bool _gaveUp;

    /// <summary>最近一次失败原因（状态栏/日志用）。</summary>
    public string LastError { get; private set; } = "";

    public SpoutIn(Device device) => _device = device;

    /// <summary>已发布的帧序号：暂停态泵据此判断「实时层还在动」，必须继续出帧。</summary>
    public long Seq => Interlocked.Read(ref _seq);

    /// <summary>当前是否连着源（最近一帧拿到了纹理）。</summary>
    public bool Connected { get; private set; }

    /// <summary>源的画面尺寸（未连接时为 0）。</summary>
    public int SourceWidth => _srcW < 0 ? 0 : _srcW;
    public int SourceHeight => _srcH < 0 ? 0 : _srcH;

    /// <summary>已发布帧的像素描述（预览侧据此分配自己的拷贝缓冲）；无帧返回 null。</summary>
    public SKImageInfo? LatestInfo
    {
        get
        {
            lock (_pub) return _front?.Info;
        }
    }

    /// <summary>
    /// 泵线程：当前该合成的帧位图（**借用**——只保证在下一次 <see cref="Pump"/> 之前有效，
    /// 调用方必须在本拍内画完，不得留存引用）。无帧返回 null。
    /// </summary>
    public SKBitmap? FrontForRender
    {
        get
        {
            lock (_pub) return _front;
        }
    }

    /// <summary>
    /// 泵线程：按需（重）连接 + 收一帧 + 读回发布。返回 true = 本次拿到了新帧。
    /// <paramref name="senderName"/> 变了会就地重建接收端（VTS 改输出名后不用重启管线）。
    /// </summary>
    public bool Pump(bool enabled, string senderName, bool premultiplied)
    {
        if (_gaveUp) return false;
        if (!enabled)
        {
            if (_rx != null) Close(); // 已建接收端才需要收摊；本来就是关的，每拍这次判断零开销
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
            // 没连上不是错误：源可能还没开（对方程序未启动/未开 Spout 输出/名字不符）。节流留痕。
            // 上一帧留着不清——与 OBS 的 Spout 源一致：源断了画面停在最后一帧，重新连上即续上
            //（PV 面板的状态行会显示「等待源」，用户看得出不是活的）。
            Connected = false;
            if (now - _lastWaitLog >= WaitLogMs)
            {
                _lastWaitLog = now;
                Log.Info($"[SpoutIn] 等待 Spout 源 '{name}'……（对方程序需已开启 Spout 输出，且名字完全一致）");
            }
            return false;
        }

        try
        {
            // QueryInterface 而非直接包指针：返回物归我们所有（Dispose 只释放我们自己这份引用），
            // 不会把包装层持有的那块纹理误释放掉
            using var src = ComObject.QueryInterfaceOrNull<Texture2D>(ptr);
            if (src == null)
            {
                Fail("源纹理 QueryInterface", null);
                return false;
            }

            var d = src.Description;
            if (d.Width <= 0 || d.Height <= 0) return false;
            if (!EnsureTargets((int)d.Width, (int)d.Height, (int)d.Format, premultiplied))
            {
                Connected = false;
                return false;
            }

            var ctx = _device.ImmediateContext;
            ctx.CopyResource(src, _stg!);
            var mapped = ctx.MapSubresource(_stg!, 0, MapMode.Read, MapFlags.None);
            try
            {
                var dst = _back!;
                // 逐行拷贝：源 RowPitch 是 D3D 的对齐行距，位图行距是紧排的，不能整块 memcpy
                var rowBytes = Math.Min((long)_srcW * _srcBpp, Math.Min(mapped.RowPitch, dst.RowBytes));
                unsafe
                {
                    var srcBase = (byte*)mapped.DataPointer;
                    var dstBase = (byte*)dst.GetPixels();
                    for (var row = 0; row < _srcH; row++)
                        Buffer.MemoryCopy(
                            srcBase + (long)row * mapped.RowPitch,
                            dstBase + (long)row * dst.RowBytes,
                            dst.RowBytes, rowBytes);
                }
            }
            finally
            {
                ctx.UnmapSubresource(_stg!, 0);
            }

            lock (_pub)
            {
                (_front, _back) = (_back, _front);
                _seq++;
            }

            Connected = true;
            _fails = 0;
            if (_lastWaitLog != 0) _lastWaitLog = 0; // 连上后下一次掉线要立刻留痕
            return true;
        }
        catch (Exception ex)
        {
            Fail("收帧/读回", ex);
            return false;
        }
    }

    /// <summary>
    /// 预览线程：把最新整帧拷进调用方的缓冲（自渲染路径用；镜像路径不需要）。
    /// 拷完即与接收端解耦——之后源怎么翻面都不会动到这份像素。<paramref name="seq"/> 是所拷帧的序号。
    /// </summary>
    public bool TryCopyLatest(ref SKBitmap? dst, out long seq)
    {
        lock (_pub)
        {
            seq = _seq;
            if (_front == null) return false;
            if (dst == null || dst.Width != _front.Width || dst.Height != _front.Height
                || dst.ColorType != _front.ColorType || dst.AlphaType != _front.AlphaType)
            {
                dst?.Dispose();
                dst = new SKBitmap(_front.Info);
            }

            unsafe
            {
                var src = _front;
                var rows = Math.Min(src.Height, dst.Height);
                for (var row = 0; row < rows; row++)
                    Buffer.MemoryCopy(
                        (void*)(src.GetPixels() + (long)row * src.RowBytes),
                        (void*)(dst.GetPixels() + (long)row * dst.RowBytes),
                        dst.RowBytes, Math.Min(src.RowBytes, dst.RowBytes));
            }

            return true;
        }
    }

    public void Dispose()
    {
        Close();
        lock (_pub)
        {
            _front?.Dispose();
            _back?.Dispose();
            _front = _back = null;
        }
    }

    // ---------------- 内部 ----------------

    private static string Normalize(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "VTubeStudio" : name.Trim();

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
            _lastWaitLog = 0; // 新源：第一次没收到就留痕
            Log.Info($"[SpoutIn] 接收端就绪，等待源 '{name}'（对方程序的 Spout 输出名）");
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

        _stg?.Dispose();
        _stg = null;
        _srcW = _srcH = -1;
        _srcFmt = -1;
        lock (_pub)
        {
            _front?.Dispose();
            _back?.Dispose();
            _front = _back = null;
            _seq++; // 断开也是一次内容变化：消费方（预览脏判断）要能看到
        }
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
            var msg = $"[SpoutIn] {what} 连续失败 {_fails} 次，本次运行不再尝试接收外部 Spout 源（改设置或重启渲染引擎可再试）";
            if (ex != null) Log.Error(msg + "：" + ex.Message, ex);
            else Log.Error(msg);
            return;
        }

        Log.Warn($"[SpoutIn] {what}（第 {_fails}/{MaxFails} 次，{RetryBackoffMs}ms 后重试）"
                 + (ex != null ? "：" + ex.Message : ""));
    }

    /// <summary>按源的尺寸/格式准备 staging 与两块位图；格式不认识返回 false（只记一次）。</summary>
    private bool EnsureTargets(int w, int h, int dxgiFormat, bool premultiplied)
    {
        var alpha = premultiplied ? SKAlphaType.Premul : SKAlphaType.Unpremul;
        var color = dxgiFormat switch
        {
            (int)Format.B8G8R8A8_UNorm => SKColorType.Bgra8888,
            (int)Format.R8G8B8A8_UNorm => SKColorType.Rgba8888,
            (int)Format.R10G10B10A2_UNorm => SKColorType.Rgba1010102,
            (int)Format.R16G16B16A16_Float => SKColorType.RgbaF16,
            _ => SKColorType.Unknown,
        };
        if (color == SKColorType.Unknown)
        {
            if (_srcFmt != dxgiFormat)
            {
                _srcFmt = dxgiFormat;
                LastError = $"源格式不支持（DXGI {(Format)dxgiFormat}）";
                Log.Warn($"[SpoutIn] 源纹理格式 {(Format)dxgiFormat} 不认识，跳过合成"
                         + "（支持 B8G8R8A8 / R8G8B8A8 / R10G10B10A2 / R16G16B16A16_Float）");
            }

            return false;
        }

        if (_stg != null && _srcW == w && _srcH == h && _srcFmt == dxgiFormat
            && _front != null && _front.ColorType == color && _front.AlphaType == alpha)
            return true;

        _stg?.Dispose();
        _stg = new Texture2D(_device, new Texture2DDescription
        {
            Width = w,
            Height = h,
            MipLevels = 1,
            ArraySize = 1,
            Format = (Format)dxgiFormat,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CpuAccessFlags = CpuAccessFlags.Read,
            OptionFlags = ResourceOptionFlags.None,
        });

        _srcW = w;
        _srcH = h;
        _srcFmt = dxgiFormat;
        _srcBpp = dxgiFormat == (int)Format.R16G16B16A16_Float ? 8 : 4;

        lock (_pub)
        {
            _front?.Dispose();
            _back?.Dispose();
            var info = new SKImageInfo(w, h, color, alpha);
            _front = new SKBitmap(info);
            _back = new SKBitmap(info);
        }

        Log.Info($"[SpoutIn] 源画面 {w}×{h}（{(Format)dxgiFormat}，{(premultiplied ? "预乘" : "直通")} alpha）");
        return true;
    }
}
