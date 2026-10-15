using System.IO;
using JizuraLive.Logging;
using JizuraLive.Render.Gpu;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using DCommonAlphaMode = Vortice.DCommon.AlphaMode;
using DCommonPixelFormat = Vortice.DCommon.PixelFormat;

namespace JizuraLive.Pv;

/// <summary>
/// 视频文件图层：把一段视频当图层交给 PV 舞台，按帧时钟循环播放。
/// 解码（<see cref="MfVideoDecoder"/>，CPU 侧 RGB32）→ 自有 BGRA8 纹理（UpdateSubresource）→ D2D 位图（按纹理代次缓存）。
/// 台阶与图片层一致：只交出"位图 + 原尺寸"，Cover/Contain 的缩放裁切由 <see cref="PvStageCompositor"/> 统一算。
/// 不在本线内：音频轨、硬件解码、多视频层并发解码优化。
/// </summary>
public sealed class FileVideoProvider : IPvLayerProvider, IDisposable
{
    private readonly D3DDevice _gpu;
    private readonly ID2D1DeviceContext _ctx;
    private readonly string _path;

    private MfVideoDecoder? _decoder;
    private byte[]? _frame;
    private ID3D11Texture2D? _texture;
    private ID2D1Bitmap? _bitmap;
    private long _textureGeneration = 1;
    private long _bitmapGeneration = -1;
    private int _width;
    private int _height;

    /// <summary>已上传到纹理的那一帧的时刻（NaN = 还没有）。用来跳过"同一帧再传一次"。</summary>
    private double _uploadedPts = double.NaN;

    /// <summary>上一次核对的文件戳（最后写入时刻的 Ticks；&lt; 0 = 当时文件不存在）。</summary>
    private long _stampTicks = -1;
    private bool _stampTried;

    private long _version = 1;

    public string Id { get; }
    public long Version => _version;

    /// <summary>最近一次失败/解码失败的可读原因（null = 正常）。</summary>
    public string? LastError { get; private set; }

    public double Duration => _decoder?.Duration ?? 0;

    /// <summary>解码器已就绪（帧尺寸与时长可读）时为 true。</summary>
    public bool Ready => _decoder is not null;

    public FileVideoProvider(string id, D3DDevice gpu, ID2D1DeviceContext ctx, string path)
    {
        Id = id;
        _gpu = gpu;
        _ctx = ctx;
        _path = path;
    }

    /// <summary>
    /// 把任意时刻折进 [0, duration)：循环取模 + 越界钳制（负时刻、时长非法、NaN/∞ 都有定值）。
    /// </summary>
    public static double LoopTime(double time, double duration)
    {
        if (!(duration > 0) || !double.IsFinite(time)) return 0;
        var t = time % duration;
        return t < 0 ? t + duration : t;
    }

    public PvLayerImage Pump(in PvFrameContext ctx)
    {
        EnsureOpen();
        var decoder = _decoder;
        if (decoder is null) return Cached();

        if (decoder.CopyFrameAt(LoopTime(ctx.Time, decoder.Duration), _frame!))
        {
            // 解码器判定的"同一帧"（源帧率低于取帧节奏）不再上传：1080p 一帧 8MB，重复上传是白烧显存带宽
            if (decoder.LastFramePts != _uploadedPts)
            {
                Upload(decoder);
                _uploadedPts = decoder.LastFramePts;
                _version++;
            }
        }
        else if (decoder.LastError is { } why && !string.Equals(why, LastError, StringComparison.Ordinal))
        {
            LastError = why;
            Log.Warn($"[PV/视频] {_path}：{why}");
        }

        return Cached();
    }

    private PvLayerImage Cached() =>
        _bitmap is null ? PvLayerImage.Empty : new PvLayerImage(_bitmap, null, _width, _height);

    /// <summary>
    /// 打开解码器。只在还没打开时核对文件戳：已在播的片子再换掉属于换图层，
    /// 帧中途换解码器会把播放位置一起换掉（与图片层的热重载不是一回事）。
    /// </summary>
    private void EnsureOpen()
    {
        if (_decoder is not null) return;

        var ticks = Stamp();
        if (_stampTried && ticks == _stampTicks) return;
        _stampTried = true;
        _stampTicks = ticks;

        var decoder = MfVideoDecoder.TryOpen(_path, out var error);
        if (decoder is null)
        {
            if (!string.Equals(error, LastError, StringComparison.Ordinal))
                Log.Warn($"[PV/视频] 打不开 {_path}：{error}（文件就位或改动后会再试）");
            LastError = error;
            return;
        }

        _decoder = decoder;
        _frame = new byte[decoder.FrameBytes];
        LastError = null;
        Log.Info($"[PV/视频] 已载入 {_path}（{decoder.Width}×{decoder.Height}，"
                 + $"{decoder.Duration:F2}s，约 {decoder.FrameCount} 帧）");
    }

    /// <summary>文件的最后写入时刻（Ticks）；不存在或读不到返回 -1（照 FileImageProvider 的同一手法）。</summary>
    private long Stamp()
    {
        try
        {
            var ticks = File.GetLastWriteTimeUtc(_path).Ticks;
            return ticks <= 0 ? -1L : ticks;
        }
        catch (Exception)
        {
            return -1L;
        }
    }

    private void Upload(MfVideoDecoder decoder)
    {
        EnsureTarget(decoder.Width, decoder.Height);
        _gpu.Context.UpdateSubresource(_frame!, _texture, 0,
            (uint)(_width * 4), (uint)(_width * 4 * _height), null);
        EnsureBitmap();
    }

    private void EnsureTarget(int width, int height)
    {
        if (_texture is not null && _width == width && _height == height) return;

        DisposeTarget();
        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            // SR 是 D2D 位图（CreateBitmapFromDxgiSurface）要的；与 SpoutIn 的自有帧纹理同一组合
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };

        _texture = _gpu.Device.CreateTexture2D(desc);
        _width = width;
        _height = height;
        _textureGeneration++;
    }

    /// <summary>
    /// 把自有纹理包成 D2D 位图。缓存键是纹理代次（照 <see cref="SpoutSourceProvider"/> 的代次手法）：
    /// 代次变了说明纹理被重建过，旧位图指着已释放的纹理，必须先 Dispose 再重建。
    /// alpha 恒按预乘：解码时已把 A 钉成 255（帧不透明），位图也就与纹理所见一致。
    /// </summary>
    private void EnsureBitmap()
    {
        if (_bitmap is not null && _bitmapGeneration == _textureGeneration) return;

        DisposeBitmap();
        try
        {
            using var surface = _texture!.QueryInterface<IDXGISurface>();
            _bitmap = _ctx.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
                new DCommonPixelFormat(Format.B8G8R8A8_UNorm, DCommonAlphaMode.Premultiplied),
                96f, 96f, BitmapOptions.None));
            _bitmapGeneration = _textureGeneration;
        }
        catch (Exception ex)
        {
            LastError = "包成 D2D 位图失败：" + ex.Message;
            Log.Warn($"[PV/视频] {_path}：{LastError}");
        }
    }

    private void DisposeBitmap()
    {
        _bitmap?.Dispose();
        _bitmap = null;
    }

    private void DisposeTarget()
    {
        _texture?.Dispose();
        _texture = null;
    }

    public void Dispose()
    {
        // 顺序：位图 → 纹理 → 解码器（位图指着纹理，纹理是解码输出的落脚处）
        DisposeBitmap();
        DisposeTarget();
        _decoder?.Dispose();
        _decoder = null;
        _frame = null;
    }
}
