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

/// <summary>
/// Spout 源图层：把一路外部 Spout 输入（VTS 角色画面、游戏画面）作为图层交给 PV 舞台。
/// 纯 Vortice 实现，零旧依赖。
///
/// 【M7 零拷贝线】取图不再经过 CPU：<see cref="SpoutIn"/> 交出的是一张**本设备自有纹理**，
/// 这里用 <c>CreateBitmapFromDxgiSurface</c> 把它直接包成 D2D 位图（与 PvStageRenderer.BitmapFor
/// 同一手法）。旧实现的 <c>CopyFromMemory</c> 上传（每帧一次 w*h*4 的托管数组 → GPU）已删除。
/// 位图按 (纹理代次, 槽位) 缓存：<see cref="SpoutIn.Generation"/> 变化说明底层纹理被重建过，
/// 旧位图指着已释放的纹理，必须先 Dispose 再重建。
/// </summary>
public sealed class SpoutSourceProvider : IPvLayerProvider, IDisposable
{
    private readonly SpoutIn _in;
    private readonly ID2D1DeviceContext _ctx;
    private readonly string _sender;
    private readonly bool _premultiplied;

    /// <summary>两张帧纹理各配一张 surface 位图（槽位与 <see cref="SpoutIn.FrontIndex"/> 对齐）。</summary>
    private readonly ID2D1Bitmap?[] _bitmaps = new ID2D1Bitmap?[2];
    private long _bitmapGeneration = -1;

    /// <summary>alpha 模式被 surface 位图拒绝时只记一条诊断（每帧重试但别每帧刷屏）。</summary>
    private bool _alphaWarned;

    /// <summary>本代次下该 alpha 模式已被证明建不出位图——别再每帧往 D2D 里扔异常（异常很贵）。</summary>
    private bool _alphaUnsupported;

    private long _version = 1;

    public string Id { get; }
    public long Version => _version;

    public string Sender => _sender;
    public bool Connected => _in.Connected;
    public int SourceWidth => _in.SourceWidth;
    public int SourceHeight => _in.SourceHeight;
    public string LastError => _in.LastError;

    /// <summary>源格式不在白名单时的可读诊断（null = 正常）。工作台状态条据此提示用户改发送端格式。</summary>
    public string? FormatDiagnostic => _in.FormatDiagnostic;

    /// <summary>取帧耗时（毫秒）。停机握手要用实测判断原生接收会不会把渲染线程关在门外。</summary>
    /// <remarks>
    /// 【M1 第 6 步实测 · 2026-10-15 · RTX 4060 Laptop】无人发送时本值约 0.001ms（空转）；
    /// 有源时（640×480 自发送）avg 0.30ms、p50 0.25ms；上限是「发送端刚消失后那一帧」的
    /// ~67ms（native 内部，非托管侧）。停机握手看到 MaxReceiveMs 落在 60–70ms 区间即属正常已知代价。
    /// </remarks>
    public double LastReceiveMs { get; private set; }
    public double MaxReceiveMs { get; private set; }

    /// <summary>
    /// <paramref name="premultiplied"/> 默认 true：D2D 的 surface 位图**只接受 Premultiplied**
    /// （声明 Straight 必抛 WINCODEC_ERR_UNSUPPORTEDPIXELFORMAT，实测见
    /// SpoutGpuInputTests.OwnedTexture_WrapsIntoD2DBitmap_AndRenders），而 VTS/Spout 发送端本来就发预乘像素。
    /// 默认 false 会让默认接线（PvPipeline 不传这个参数时）每帧抛异常、图层永不出图——那是黑屏，不是报错。
    /// </summary>
    public SpoutSourceProvider(string id, D3DDevice gpu, ID2D1DeviceContext ctx,
        string sender, bool premultiplied = true)
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
            // alpha 模式只归属本 provider 的 D2D 位图（_premultiplied），与收帧无关——
            // 所以收帧这一步没有 premultiplied 参数。
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

    /// <summary>
    /// 把自有帧纹理包成 D2D 位图。缓存键是 (代次, 槽位)：代次变了说明纹理被重建（尺寸/源变了），
    /// 旧位图指着已释放的纹理，必须先全部 Dispose；槽位对应 ping-pong 的两张纹理。
    /// </summary>
    private ID2D1Bitmap? EnsureBitmap(ID3D11Texture2D tex, long generation, int frontIndex)
    {
        if (frontIndex is < 0 or >= 2) return null;

        if (_bitmapGeneration != generation)
        {
            DisposeBitmaps();
            _bitmapGeneration = generation;
            _alphaUnsupported = false; // 换了纹理代次，重新试一次（失败多半与格式无关，但没必要记着）
        }

        if (_alphaUnsupported) return null;
        if (_bitmaps[frontIndex] is { } cached) return cached;

        try
        {
            using var surface = tex.QueryInterface<IDXGISurface>();
            var alphaMode = _premultiplied ? DCommonAlphaMode.Premultiplied : DCommonAlphaMode.Straight;
            var format = new DCommonPixelFormat(Format.B8G8R8A8_UNorm, alphaMode);
            var bmp = _ctx.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(format, 96f, 96f, BitmapOptions.None));
            _bitmaps[frontIndex] = bmp;
            return bmp;
        }
        catch (Exception ex)
        {
            // 建位图失败（实测就是 alpha 模式不被 surface 位图接受，见 SpoutGpuInputTests
            // 的 OwnedTexture_WrapsIntoD2DBitmap_AndRenders）：不出图，并留下可读原因。
            // 声明 Straight 却被拒时必须显式诊断——静默回退成 Premultiplied 会让画面悄悄变色。
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
        // 先位图后 in：位图指着 in 的纹理，反过来的话 in 会释放纹理而位图还在。
        DisposeBitmaps();
        _in.Dispose();
    }
}
