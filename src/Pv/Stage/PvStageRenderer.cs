using JizuraLive.Engine;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Effects;
using JizuraLive.Render.Gpu;
using JizuraLive.Render.Text;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using DCommonAlphaMode = Vortice.DCommon.AlphaMode;
using DCommonPixelFormat = Vortice.DCommon.PixelFormat;

namespace JizuraLive.Pv;

/// <summary>
/// PV 舞台渲染器：把「歌词排版层 + 图层栈 + 效果图」串成一帧。
/// 全链路纯 GPU（D3D11 + D2D1.1 + HLSL Passes），零线程抢锁，零 CPU 拷贝。
/// </summary>
public sealed class PvStageRenderer : IDisposable
{
    private readonly D3DDevice _gpu;
    private readonly D2DHost _d2d;
    private readonly GpuEffectRunner _fx;
    private readonly Dictionary<IntPtr, ID2D1Bitmap1> _fxBitmaps = new();
    private OffscreenTarget? _stageTarget;
    private ID3D11ShaderResourceView? _stageSrv;
    private int _stageW, _stageH;

    public TypographyLayerProvider? Lyric { get; }
    public PvStage Stage { get; }
    public EffectGraph Effects { get; }

    /// <summary>最近一帧各段耗时（毫秒）。</summary>
    public (double Engine, double Fx, double Composite) LastTiming { get; private set; }

    /// <summary>效果链段数（状态条显示用）。</summary>
    public int EffectCount => Effects.Nodes.Count(n => n.Enabled);

    /// <summary>D2D 设备上下文：调用方用它把交换链后缓冲包成绘制目标。</summary>
    public ID2D1DeviceContext D2DContext => _d2d.Ctx;

    /// <summary>管线所属的 D3D 设备（图层提供者建纹理要用同一个）。</summary>
    public D3DDevice Device => _gpu;

    public PvStageRenderer(D3DDevice gpu, D2DHost d2d, PvScene scene, EffectGraph effects,
        int designWidth = 1920, int designHeight = 1080)
    {
        _gpu = gpu;
        _d2d = d2d;
        _fx = new GpuEffectRunner(gpu);
        Effects = effects;
        Stage = new PvStage(scene);

        Lyric = new TypographyLayerProvider("lyrics_pv", gpu, d2d, designWidth, designHeight);
        Stage.Register(Lyric);
    }

    public void SetLyrics(IReadOnlyList<KrcLine>? lines) => Lyric?.SetLyrics(lines);

    public void Prepare(double time)
    {
        if (Lyric is null) return;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Lyric.Render(time);
        LastTiming = (Elapsed(t0), LastTiming.Fx, LastTiming.Composite);
    }

    /// <summary>
    /// 把舞台合成写进一块外部共享纹理（供 SpoutOut 等广播通道使用）。
    /// </summary>
    public SharedTarget OpenSharedTarget(IntPtr shareHandle, int width, int height)
    {
        var texture = _gpu.Device.OpenSharedResource<ID3D11Texture2D>(shareHandle);
        try
        {
            var surface = texture.QueryInterface<IDXGISurface>();
            var bitmap = _d2d.Ctx.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
                new DCommonPixelFormat(Format.B8G8R8A8_UNorm, DCommonAlphaMode.Premultiplied),
                96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw));
            return new SharedTarget(texture, surface, bitmap, width, height);
        }
        catch
        {
            texture.Dispose();
            throw;
        }
    }

    /// <summary>外部共享纹理上的合成目标。</summary>
    public sealed class SharedTarget : IDisposable
    {
        public ID3D11Texture2D Texture { get; }
        public ID2D1Bitmap1 Bitmap { get; }
        public int Width { get; }
        public int Height { get; }

        private readonly IDXGISurface _surface;

        internal SharedTarget(ID3D11Texture2D texture, IDXGISurface surface, ID2D1Bitmap1 bitmap,
            int width, int height)
        {
            Texture = texture;
            _surface = surface;
            Bitmap = bitmap;
            Width = width;
            Height = height;
        }

        public void Dispose()
        {
            Bitmap.Dispose();
            _surface.Dispose();
            Texture.Dispose();
        }
    }

    /// <summary>合成并画到目标。</summary>
    public void Composite(ID2D1Bitmap1 target, int width, int height)
    {
        EnsureStageTarget(width, height);
        var d = _d2d.Draw;

        // 1) 图层栈合成到可采样面
        d.SetTarget(_stageTarget!.Bitmap, width, height);
        d.Begin();
        d.Clear(new Color4(0f, 0f, 0f, 1f));
        Stage.RenderFrame(d, width, height);
        d.End();
        var t1 = System.Diagnostics.Stopwatch.GetTimestamp();

        // 2) 效果图（D3D11 全屏 pass 链）
        GpuTexturePool.Entry? fxOut = null;
        var passes = Effects.Nodes.Any(n => n.Enabled)
            ? Effects.BuildRenderGraph(_fx.Shaders, (float)(Stage.Driver?.Time ?? 0), width, height)
            : null;
        if (passes is { Count: > 0 })
            fxOut = _fx.RunGraph(passes, _stageSrv!, width, height);

        var t2 = System.Diagnostics.Stopwatch.GetTimestamp();

        // 3) 画到目标
        d.SetTarget(target, width, height);
        d.Begin();
        d.Clear(new Color4(0f, 0f, 0f, 1f));
        d.DrawBitmap(fxOut is null ? _stageTarget!.Bitmap : BitmapFor(fxOut),
            new Rect(0, 0, width, height));
        d.End();

        if (fxOut is not null) _fx.Pool.Return(fxOut);
        LastTiming = (LastTiming.Engine, Elapsed(t1), Elapsed(t2));
    }

    private void EnsureStageTarget(int width, int height)
    {
        if (_stageTarget is not null && _stageW == width && _stageH == height) return;
        _stageSrv?.Dispose();
        _stageTarget?.Dispose();
        _stageTarget = OffscreenTarget.Create(_gpu, _d2d.Ctx, width, height, sampleable: true);
        _stageSrv = _gpu.Device.CreateShaderResourceView(_stageTarget.Texture);
        _stageW = width;
        _stageH = height;
    }

    private ID2D1Bitmap1 BitmapFor(GpuTexturePool.Entry entry)
    {
        var key = entry.Texture.NativePointer;
        if (_fxBitmaps.TryGetValue(key, out var bmp)) return bmp;

        using var surface = entry.Texture.QueryInterface<IDXGISurface>();
        bmp = _d2d.Ctx.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
            new DCommonPixelFormat(Format.B8G8R8A8_UNorm, DCommonAlphaMode.Premultiplied),
            96f, 96f, BitmapOptions.None));
        _fxBitmaps[key] = bmp;
        return bmp;
    }

    private static double Elapsed(long from) =>
        (System.Diagnostics.Stopwatch.GetTimestamp() - from) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    public void Dispose()
    {
        foreach (var b in _fxBitmaps.Values) b.Dispose();
        _fxBitmaps.Clear();
        _stageSrv?.Dispose();
        _stageTarget?.Dispose();
        Lyric?.Dispose();
        Stage.Dispose();
        _fx.Dispose();
    }
}
