using System.IO;
using JizuraLive.Logging;
using JizuraLive.Render.Gpu;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using DCommonAlphaMode = Vortice.DCommon.AlphaMode;
using DCommonPixelFormat = Vortice.DCommon.PixelFormat;

namespace JizuraLive.Pv;

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

    private double _uploadedPts = double.NaN;

    private long _stampTicks = -1;
    private bool _stampTried;

    private long _version = 1;

    public string Id { get; }
    public long Version => _version;

    public string? LastError { get; private set; }

    public double Duration => _decoder?.Duration ?? 0;

    public bool Ready => _decoder is not null;

    public FileVideoProvider(string id, D3DDevice gpu, ID2D1DeviceContext ctx, string path)
    {
        Id = id;
        _gpu = gpu;
        _ctx = ctx;
        _path = path;
    }

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
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };

        _texture = _gpu.Device.CreateTexture2D(desc);
        _width = width;
        _height = height;
        _textureGeneration++;
    }

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
        DisposeBitmap();
        DisposeTarget();
        _decoder?.Dispose();
        _decoder = null;
        _frame = null;
    }
}
