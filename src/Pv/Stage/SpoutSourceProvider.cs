using JizuraLive.Logging;
using JizuraLive.Render.Gpu;
using JizuraLive.Spout;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using DCommonAlphaMode = Vortice.DCommon.AlphaMode;
using DCommonPixelFormat = Vortice.DCommon.PixelFormat;

namespace JizuraLive.Pv;

/// <summary>
/// Spout 源图层：把一路外部 Spout 输入（VTS 角色画面、游戏画面）作为图层交给 PV 舞台。
/// 纯 Vortice 实现，零旧依赖。
/// </summary>
public sealed class SpoutSourceProvider : IPvLayerProvider, IDisposable
{
    private readonly SpoutIn _in;
    private readonly ID2D1DeviceContext _ctx;
    private readonly string _sender;
    private readonly bool _premultiplied;

    private ID2D1Bitmap? _bitmap;
    private int _cachedW, _cachedH;
    private long _version = 1;

    public string Id { get; }
    public long Version => _version;

    public string Sender => _sender;
    public bool Connected => _in.Connected;
    public int SourceWidth => _in.SourceWidth;
    public int SourceHeight => _in.SourceHeight;
    public string LastError => _in.LastError;

    /// <summary>取帧耗时（毫秒）。停机握手要用实测判断原生接收会不会把渲染线程关在门外。</summary>
    /// <remarks>
    /// 【M1 第 6 步实测 · 2026-10-15 · RTX 4060 Laptop】无人发送时本值约 0.001ms（空转）；
    /// 有源时（640×480 自发送）avg 0.30ms、p50 0.25ms；上限是「发送端刚消失后那一帧」的
    /// ~67ms（native 内部，非托管侧）。停机握手看到 MaxReceiveMs 落在 60–70ms 区间即属正常已知代价。
    /// </remarks>
    public double LastReceiveMs { get; private set; }
    public double MaxReceiveMs { get; private set; }

    public SpoutSourceProvider(string id, D3DDevice gpu, ID2D1DeviceContext ctx,
        string sender, bool premultiplied = false)
    {
        Id = id;
        _ctx = ctx;
        _sender = sender;
        _premultiplied = premultiplied;
        _in = new SpoutIn(gpu.Device);
    }

    /// <summary>收一帧（在渲染线程上调用）。</summary>
    public void PumpFrame()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if (_in.Pump(true, _sender, _premultiplied)) _version++;
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
        var pixels = _in.FrontPixels;
        var w = _in.SourceWidth;
        var h = _in.SourceHeight;
        if (pixels is null || w <= 0 || h <= 0) return PvLayerImage.Empty;

        var bmp = EnsureBitmap(w, h);
        if (bmp is null) return PvLayerImage.Empty;

        try
        {
            unsafe
            {
                fixed (byte* p = pixels)
                {
                    bmp.CopyFromMemory((IntPtr)p, (uint)(w * 4));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("[PV/Spout] 上传像素失败：" + ex.Message);
            return PvLayerImage.Empty;
        }

        return new PvLayerImage(bmp, null, w, h);
    }

    private ID2D1Bitmap? EnsureBitmap(int w, int h)
    {
        if (_bitmap is not null && _cachedW == w && _cachedH == h)
            return _bitmap;

        _bitmap?.Dispose();

        try
        {
            var alphaMode = _premultiplied ? DCommonAlphaMode.Premultiplied : DCommonAlphaMode.Straight;
            var format = new DCommonPixelFormat(Format.B8G8R8A8_UNorm, alphaMode);
            _bitmap = _ctx.CreateBitmap(new SizeI(w, h), new BitmapProperties1(format, 96f, 96f));
            _cachedW = w;
            _cachedH = h;
            return _bitmap;
        }
        catch (Exception ex)
        {
            Log.Warn("[PV/Spout] 建位图失败：" + ex.Message);
            return null;
        }
    }

    public void Dispose()
    {
        _bitmap?.Dispose();
        _in.Dispose();
    }
}
