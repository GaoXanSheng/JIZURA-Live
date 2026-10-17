using JizuraLive.Logging;
using JizuraLive.Render.Gpu;
using SpoutDXWrapper;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using DCommonAlphaMode = Vortice.DCommon.AlphaMode;
using DCommonPixelFormat = Vortice.DCommon.PixelFormat;

namespace JizuraLive.Spout;

public sealed class SpoutOut : IDisposable
{
    private readonly D3DDevice _gpu;
    private SpoutSender? _sender;
    private ID3D11Texture2D? _texture;
    private IDXGISurface? _surface;
    private ID2D1Bitmap1? _bitmap;

    private int _width;
    private int _height;
    private string _senderName;
    private bool _initialized;

    public string SenderName => _senderName;
    public int Width => _width;
    public int Height => _height;
    public bool IsActive => _initialized && _sender is not null;

    public long SentCount { get; private set; }
    public long FailedCount { get; private set; }
    public double LastSendMs { get; private set; }
    public string? LastError { get; private set; }

    public ID2D1Bitmap1? Bitmap => _bitmap;

    public SpoutOut(D3DDevice gpu, string senderName = "JIZURA-live-PV", int width = 1920, int height = 1080)
    {
        _gpu = gpu;
        _senderName = string.IsNullOrWhiteSpace(senderName) ? "JIZURA-live-PV" : senderName.Trim();
        _width = Math.Max(16, width);
        _height = Math.Max(16, height);

        Initialize();
    }

    private void Initialize()
    {
        try
        {
            var sender = new SpoutSender();
            sender.SetSenderName(_senderName);
            if (!sender.OpenDirectX(_gpu.Device.NativePointer))
            {
                sender.Dispose();
                Log.Error($"[SpoutOut] SpoutSender.OpenDirectX 失败（发送端名 '{_senderName}'）");
                return;
            }

            var desc = new Texture2DDescription
            {
                Width = (uint)_width,
                Height = (uint)_height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.Shared,
            };

            _texture = _gpu.Device.CreateTexture2D(desc);
            _surface = _texture.QueryInterface<IDXGISurface>();
            _sender = sender;
            _initialized = true;

            Log.Info($"[SpoutOut] 广播就绪：'{_senderName}' ({_width}×{_height})");
        }
        catch (Exception ex)
        {
            Log.Error("[SpoutOut] 初始化异常：" + ex.Message, ex);
            Dispose();
        }
    }

    public ID2D1Bitmap1 EnsureD2DBitmap(ID2D1DeviceContext ctx)
    {
        if (_bitmap is not null) return _bitmap;
        if (_surface is null) throw new InvalidOperationException("Spout 表面未初始化");

        _bitmap = ctx.CreateBitmapFromDxgiSurface(_surface, new BitmapProperties1(
            new DCommonPixelFormat(Format.B8G8R8A8_UNorm, DCommonAlphaMode.Premultiplied),
            96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw));
        return _bitmap;
    }

    public bool PresentFrame()
    {
        if (!_initialized || _sender is null || _texture is null) return false;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            _gpu.Context.Flush();
            _sender.SendTexture(_texture.NativePointer);
            SentCount++;
            LastSendMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0
                         / System.Diagnostics.Stopwatch.Frequency;
            return true;
        }
        catch (Exception ex)
        {
            FailedCount++;
            LastError = ex.Message;
            Log.Warn("[SpoutOut] 发送帧异常：" + ex.Message);
            return false;
        }
    }

    public void Resize(int width, int height, string? newSenderName = null)
    {
        width = Math.Max(16, width);
        height = Math.Max(16, height);
        var name = string.IsNullOrWhiteSpace(newSenderName) ? _senderName : newSenderName.Trim();

        if (_width == width && _height == height && _senderName == name) return;

        Dispose();
        _width = width;
        _height = height;
        _senderName = name;
        Initialize();
    }

    public void Dispose()
    {
        _initialized = false;
        _bitmap?.Dispose();
        _bitmap = null;
        _surface?.Dispose();
        _surface = null;
        _texture?.Dispose();
        _texture = null;

        if (_sender is not null)
        {
            try { _sender.Dispose(); } catch { }
            _sender = null;
        }
    }
}
