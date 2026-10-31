using JizuraLive.Logging;
using JizuraLive.Render.Gpu;
using JizuraLive.Spout;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using DCommonAlphaMode = Vortice.DCommon.AlphaMode;
using DCommonPixelFormat = Vortice.DCommon.PixelFormat;

namespace JizuraLive.Pv;

public sealed class SpoutSourceProvider : IPvLayerProvider, IDisposable
{
    private readonly SpoutIn _in;
    private readonly ID2D1DeviceContext _ctx;
    private readonly D3DDevice _gpu;
    private readonly string _sender;
    private readonly bool _premultiplied;
    private readonly ShaderLibrary _shaders;
    private readonly FullScreenPass _pass;
    private readonly ID3D11PixelShader _premultiplyShader;

    private readonly ID2D1Bitmap?[] _bitmaps = new ID2D1Bitmap?[2];
    private long _bitmapGeneration = -1;

    private ID3D11Texture2D? _convertedTexture;
    private ID3D11RenderTargetView? _convertedRtv;
    private ID2D1Bitmap? _convertedBitmap;
    private int _convertedW;
    private int _convertedH;

    private readonly ID3D11ShaderResourceView?[] _sourceSrv = new ID3D11ShaderResourceView?[2];
    private readonly IntPtr[] _sourcePtr = new IntPtr[2];
    private int _sourceSlot;

    private bool _alphaWarned;

    private bool _alphaUnsupported;

    private bool _slowLogged;

    private long _version = 1;

    public string Id { get; }
    public long Version => Interlocked.Read(ref _version);

    public bool Connected => _in.Connected;

    public double MaxReceiveMs { get; private set; }

    public SpoutSourceProvider(string id, D3DDevice gpu, ID2D1DeviceContext ctx,
        string sender, bool premultiplied = true)
    {
        Id = id;
        _ctx = ctx;
        _gpu = gpu;
        _sender = sender;
        _premultiplied = premultiplied;
        _in = new SpoutIn(gpu.Device);

        _shaders = new ShaderLibrary(gpu.Device);
        _pass = new FullScreenPass(gpu.Device, gpu.Context, _shaders);
        _premultiplyShader = _shaders.GetPixel("spout_premultiply", """
            float4 PSMain(VSOut i) : SV_Target
            {
                float4 c = Tex.Sample(Smp, i.uv);
                return float4(c.rgb * c.a, c.a);
            }
            """);
    }

    public void PumpFrame()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if (_in.Pump(true, _sender)) Interlocked.Increment(ref _version);
        }
        catch (Exception ex)
        {
            Log.Warn("[PV/Spout] 收帧失败：" + ex.Message);
        }
        finally
        {
            var ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0
                     / System.Diagnostics.Stopwatch.Frequency;
            if (ms > MaxReceiveMs) MaxReceiveMs = ms;
            if (ms > 100 && !_slowLogged)
            {
                _slowLogged = true;
                Log.Info($"[PV/Spout] 收帧单次耗时 {ms:F1}ms（源 {_sender}）");
            }
        }
    }

    public PvLayerImage Pump(in PvFrameContext ctx)
    {
        var tex = _in.FrontTexture;
        if (tex is null) return PvLayerImage.Empty;

        var w = _in.SourceWidth;
        var h = _in.SourceHeight;
        if (w <= 0 || h <= 0) return PvLayerImage.Empty;

        var bmp = EnsureBitmap(tex, _in.Generation, _in.FrontIndex);
        if (bmp is null) return PvLayerImage.Empty;

        return new PvLayerImage(bmp, null, w, h);
    }

    private ID2D1Bitmap? EnsureBitmap(ID3D11Texture2D tex, long generation, int frontIndex)
    {
        if (frontIndex is < 0 or >= 2) return null;

        if (_bitmapGeneration != generation)
        {
            DisposeBitmaps();
            _bitmapGeneration = generation;
            _alphaUnsupported = false;
        }

        if (!_premultiplied) return _alphaUnsupported ? null : PremultipliedBitmap(tex);

        if (_alphaUnsupported) return null;
        if (_bitmaps[frontIndex] is { } cached) return cached;

        try
        {
            using var surface = tex.QueryInterface<IDXGISurface>();
            var format = new DCommonPixelFormat(tex.Description.Format, DCommonAlphaMode.Premultiplied);
            var bmp = _ctx.CreateBitmapFromDxgiSurface(surface,
                new BitmapProperties1(format, 96f, 96f, BitmapOptions.None));
            _bitmaps[frontIndex] = bmp;
            return bmp;
        }
        catch (Exception ex)
        {
            _alphaUnsupported = true;
            if (!_alphaWarned)
            {
                _alphaWarned = true;
                Log.Warn("[PV/Spout] 把源纹理包成 D2D 位图失败：" + ex.Message
                         + "；本图层不出图（每次纹理代次变化会再试一次）。");
            }

            return null;
        }
    }

    private ID2D1Bitmap? PremultipliedBitmap(ID3D11Texture2D tex)
    {
        try
        {
            int w = (int)tex.Description.Width;
            int h = (int)tex.Description.Height;
            if (w <= 0 || h <= 0) return null;

            EnsureConverted(w, h);
            var srv = SourceSrv(tex);
            if (srv is null || _convertedRtv is null) return null;

            _pass.Run(srv, null, _convertedRtv, w, h, _premultiplyShader, ReadOnlySpan<float>.Empty);
            return _convertedBitmap;
        }
        catch (Exception ex)
        {
            _alphaUnsupported = true;
            if (!_alphaWarned)
            {
                _alphaWarned = true;
                Log.Warn("[PV/Spout] 直通 alpha 预乘失败：" + ex.Message + "；本图层不出图。");
            }

            return null;
        }
    }

    private ID3D11ShaderResourceView? SourceSrv(ID3D11Texture2D tex)
    {
        var ptr = tex.NativePointer;
        for (int i = 0; i < _sourceSrv.Length; i++)
            if (_sourcePtr[i] == ptr && _sourceSrv[i] is not null) return _sourceSrv[i];

        var srv = _gpu.Device.CreateShaderResourceView(tex);
        int slot = _sourceSlot ^ 1;
        _sourceSlot = slot;
        _sourceSrv[slot]?.Dispose();
        _sourceSrv[slot] = srv;
        _sourcePtr[slot] = ptr;
        return srv;
    }

    private void EnsureConverted(int w, int h)
    {
        if (_convertedTexture is not null && _convertedW == w && _convertedH == h) return;

        DisposeConverted();
        var desc = new Texture2DDescription
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
        };

        _convertedTexture = _gpu.Device.CreateTexture2D(desc);
        _convertedRtv = _gpu.Device.CreateRenderTargetView(_convertedTexture);
        using var surface = _convertedTexture.QueryInterface<IDXGISurface>();
        _convertedBitmap = _ctx.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
            new DCommonPixelFormat(Format.B8G8R8A8_UNorm, DCommonAlphaMode.Premultiplied),
            96f, 96f, BitmapOptions.None));
        _convertedW = w;
        _convertedH = h;
    }

    private void DisposeConverted()
    {
        _convertedBitmap?.Dispose();
        _convertedBitmap = null;
        _convertedRtv?.Dispose();
        _convertedRtv = null;
        _convertedTexture?.Dispose();
        _convertedTexture = null;
        _convertedW = 0;
        _convertedH = 0;
    }

    private void DisposeBitmaps()
    {
        for (var i = 0; i < _bitmaps.Length; i++)
        {
            _bitmaps[i]?.Dispose();
            _bitmaps[i] = null;
        }

        for (var i = 0; i < _sourceSrv.Length; i++)
        {
            _sourceSrv[i]?.Dispose();
            _sourceSrv[i] = null;
            _sourcePtr[i] = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        DisposeBitmaps();
        DisposeConverted();
        _pass.Dispose();
        _shaders.Dispose();
        _in.Dispose();
    }
}
