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
    private readonly string _sender;
    private readonly bool _premultiplied;

    private readonly ID2D1Bitmap?[] _bitmaps = new ID2D1Bitmap?[2];
    private long _bitmapGeneration = -1;

    private bool _alphaWarned;

    private bool _alphaUnsupported;

    private long _version = 1;

    public string Id { get; }
    public long Version => _version;

    public string Sender => _sender;
    public bool Connected => _in.Connected;
    public int SourceWidth => _in.SourceWidth;
    public int SourceHeight => _in.SourceHeight;
    public string LastError => _in.LastError;

    public string? FormatDiagnostic => _in.FormatDiagnostic;

    public double LastReceiveMs { get; private set; }
    public double MaxReceiveMs { get; private set; }

    public SpoutSourceProvider(string id, D3DDevice gpu, ID2D1DeviceContext ctx,
        string sender, bool premultiplied = true)
    {
        Id = id;
        _ctx = ctx;
        _sender = sender;
        _premultiplied = premultiplied;
        _in = new SpoutIn(gpu.Device);
    }

    public void PumpFrame()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if (_in.Pump(true, _sender)) _version++;
        }
        catch (Exception ex)
        {
            Log.Warn("[PV/Spout] 收帧失败：" + ex.Message);
        }
        finally
        {
            var ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0
                     / System.Diagnostics.Stopwatch.Frequency;
            LastReceiveMs = ms;
            if (ms > MaxReceiveMs) MaxReceiveMs = ms;
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

        if (_alphaUnsupported) return null;
        if (_bitmaps[frontIndex] is { } cached) return cached;

        try
        {
            using var surface = tex.QueryInterface<IDXGISurface>();
            var texFormat = tex.Description.Format;
            var alphaMode = _premultiplied ? DCommonAlphaMode.Premultiplied : DCommonAlphaMode.Straight;
            var format = new DCommonPixelFormat(texFormat, alphaMode);
            var bmp = _ctx.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(format, 96f, 96f, BitmapOptions.None));
            _bitmaps[frontIndex] = bmp;
            return bmp;
        }
        catch (Exception ex)
        {
            _alphaUnsupported = true;
            if (!_alphaWarned)
            {
                _alphaWarned = true;
                Log.Warn($"[PV/Spout] 把源纹理包成 D2D 位图失败（alphaMode={(_premultiplied ? "Premultiplied" : "Straight")}）："
                         + ex.Message + "；本图层不出图（每次纹理代次变化会再试一次），不静默改用别的 alpha 模式。");
            }

            return null;
        }
    }

    private void DisposeBitmaps()
    {
        for (var i = 0; i < _bitmaps.Length; i++)
        {
            _bitmaps[i]?.Dispose();
            _bitmaps[i] = null;
        }
    }

    public void Dispose()
    {
        DisposeBitmaps();
        _in.Dispose();
    }
}
