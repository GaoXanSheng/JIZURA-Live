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
    private Format _textureFormat = Format.B8G8R8A8_UNorm;
    private VideoProcessorConverter? _converter;
    private VideoDevice? _videoDevice;
    private ID3D11Texture2D[]? _sharedTextures;
    private bool _converterFailed;
    private bool _gpuLogged;

    private double _uploadedPts = double.NaN;

    private long _stampTicks = -1;
    private bool _stampTried;
    private long _retryAfterTicks;

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

        if (decoder.TryReadFrameAt(LoopTime(ctx.Time, decoder.Duration), _frame,
                out var gpuTexture, out var pts, out var newFrame))
        {
            try
            {
                if (newFrame)
                {
                    if (gpuTexture is not null) UploadGpu(gpuTexture);
                    else UploadCpu(decoder);
                    _uploadedPts = pts;
                    _version++;
                }
            }
            finally
            {
                gpuTexture?.Dispose();
            }
        }
        else if (decoder.LastError is { } why)
        {
            if (!string.Equals(why, LastError, StringComparison.Ordinal))
            {
                LastError = why;
                Log.Warn($"[PV/视频] {_path}：{why}");
            }

            ResetDecoder();
        }

        return Cached();
    }

    private PvLayerImage Cached() =>
        _bitmap is null ? PvLayerImage.Empty : new PvLayerImage(_bitmap, null, _width, _height);

    private void ResetDecoder()
    {
        if (_sharedTextures is not null)
        {
            foreach (var texture in _sharedTextures) texture.Dispose();
            _sharedTextures = null;
        }

        _converter?.Dispose();
        _converter = null;
        _converterFailed = false;
        _decoder?.Dispose();
        _decoder = null;
        _videoDevice?.Dispose();
        _videoDevice = null;
        _frame = null;
        _uploadedPts = double.NaN;
        _stampTried = false;
        _retryAfterTicks = Environment.TickCount64 + 1000;
    }

    private void EnsureOpen()
    {
        if (_decoder is not null) return;
        if (Environment.TickCount64 < _retryAfterTicks) return;

        var ticks = Stamp();
        if (_stampTried && ticks == _stampTicks) return;
        _stampTried = true;
        _stampTicks = ticks;

        _videoDevice ??= VideoDevice.Create(_gpu.Adapter);
        var decoder = MfVideoDecoder.TryOpen(_path, _videoDevice?.Device, out var error);
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
                 + $"{decoder.Duration:F2}s，约 {decoder.FrameCount} 帧）"
                 + (MfVideoDecoder.LastOpenNote.Length > 0 ? $" {MfVideoDecoder.LastOpenNote}" : ""));
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

    private void UploadCpu(MfVideoDecoder decoder)
    {
        EnsureTarget(decoder.Width, decoder.Height, Format.B8G8R8A8_UNorm);
        if (_texture is null) return;
        _gpu.Context.UpdateSubresource(_frame!, _texture, 0,
            (uint)(_width * 4), (uint)(_width * 4 * _height), null);
        EnsureBitmap();
    }

    private void UploadGpu(ID3D11Texture2D source)
    {
        var desc = source.Description;
        if (_converter is null) EnsureConverter(source);
        if (_converter is not null)
        {
            var converted = _converter.Convert(source);
            if (converted is null || _sharedTextures is null) return;

            EnsureTarget(_converter.OutputWidth, _converter.OutputHeight, Format.B8G8R8A8_UNorm);
            if (_texture is null) return;
            _gpu.Context.CopyResource(_texture, _sharedTextures[_converter.WrittenIndex]);
            EnsureBitmap();
            LogGpuOnce(Format.B8G8R8A8_UNorm, _converter.OutputWidth, _converter.OutputHeight);
            return;
        }

        if (_videoDevice is not null) return;
        if (IsDirectFormat(desc.Format))
        {
            EnsureTarget((int)desc.Width, (int)desc.Height, desc.Format);
            if (_texture is null) return;
            _gpu.Context.CopyResource(_texture, source);
            EnsureBitmap();
            LogGpuOnce(desc.Format, (int)desc.Width, (int)desc.Height);
        }
    }

    private void LogGpuOnce(Format format, int width, int height)
    {
        if (_gpuLogged) return;
        _gpuLogged = true;
        Log.Info($"[PV/视频] {_path}：GPU 直通 {width}×{height} {format}");
    }

    private void EnsureConverter(ID3D11Texture2D source)
    {
        if (_converter is not null || _converterFailed) return;

        if (_videoDevice is null)
        {
            _converterFailed = true;
            return;
        }

        var desc = source.Description;
        var decoder = _decoder;
        var outputWidth = decoder?.Width ?? (int)desc.Width;
        var outputHeight = decoder?.Height ?? (int)desc.Height;
        const int maxEdge = 1920;
        var edge = Math.Max(outputWidth, outputHeight);
        if (edge > maxEdge)
        {
            var scale = maxEdge / (double)edge;
            outputWidth = Math.Max(2, ((int)(outputWidth * scale)) & ~1);
            outputHeight = Math.Max(2, ((int)(outputHeight * scale)) & ~1);
        }

        _converter = VideoProcessorConverter.TryCreate(_videoDevice.Device, _videoDevice.Context,
            (int)desc.Width, (int)desc.Height, outputWidth, outputHeight, out var error);
        if (_converter is not null)
        {
            _sharedTextures = new ID3D11Texture2D[_converter.SharedHandles.Length];
            for (var i = 0; i < _sharedTextures.Length; i++)
                _sharedTextures[i] = _gpu.Device.OpenSharedResource<ID3D11Texture2D>(_converter.SharedHandles[i]);
            Log.Info($"[PV/视频] {_path}：视频处理器 {desc.Width}×{desc.Height} → {outputWidth}×{outputHeight}");
            return;
        }

        _converterFailed = true;
        LastError = "创建视频处理器失败：" + error;
        Log.Warn($"[PV/视频] {_path}：{LastError}");
    }

    private void EnsureTarget(int width, int height, Format format)
    {
        if (_texture is not null && _width == width && _height == height && _textureFormat == format) return;

        DisposeTarget();
        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };

        _texture = _gpu.Device.CreateTexture2D(desc);
        _width = width;
        _height = height;
        _textureFormat = format;
        _textureGeneration++;
    }

    private static bool IsDirectFormat(Format format) =>
        format is Format.B8G8R8A8_UNorm or Format.R8G8B8A8_UNorm;

    private static DCommonPixelFormat ToD2DFormat(Format format) => format switch
    {
        Format.R8G8B8A8_UNorm => new DCommonPixelFormat(Format.R8G8B8A8_UNorm, DCommonAlphaMode.Ignore),
        _ => new DCommonPixelFormat(Format.B8G8R8A8_UNorm, DCommonAlphaMode.Ignore),
    };

    private void EnsureBitmap()
    {
        if (_bitmap is not null && _bitmapGeneration == _textureGeneration) return;

        DisposeBitmap();
        try
        {
            using var surface = _texture!.QueryInterface<IDXGISurface>();
            _bitmap = _ctx.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
                ToD2DFormat(_textureFormat), 96f, 96f, BitmapOptions.None));
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
        if (_sharedTextures is not null)
        {
            foreach (var texture in _sharedTextures) texture.Dispose();
            _sharedTextures = null;
        }

        _converter?.Dispose();
        _converter = null;
        _decoder?.Dispose();
        _decoder = null;
        _frame = null;
        _videoDevice?.Dispose();
        _videoDevice = null;
    }
}
