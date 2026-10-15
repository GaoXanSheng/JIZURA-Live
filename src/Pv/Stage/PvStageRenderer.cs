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

    /// <summary>设计画布尺寸：本渲染器一生不变，窗口尺寸只影响预览怎么贴上去。</summary>
    private readonly int _designW, _designH;

    /// <summary>
    /// 停机后冻结末帧：Composite 不再把最后一张 fx 输出还回纹理池。
    /// 池是会复用的——末帧一还回去，下一帧（或别的租用者）就会覆写它正在被贴的那块显存，
    /// 预览面随之出现撕裂/闪黑。停机时最后一帧必须保持原样直到管线释放。
    /// </summary>
    private bool _freezeLast;

    /// <summary>
    /// 本帧时刻：由 <see cref="Prepare"/> 写入，Composite 只读这一份。
    /// 帧内绝不再回头读时钟——否则歌词贴图与着色器相位会拿到两个不同的时刻。
    /// </summary>
    private double _frameTime;

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
        // 设计尺寸来自可手改的场景文件，非法值兜到最小可用画布（16 是本仓库各处离屏面的下限）
        _designW = Math.Max(16, designWidth);
        _designH = Math.Max(16, designHeight);

        Lyric = new TypographyLayerProvider("lyrics_pv", gpu, d2d, _designW, _designH);
        Stage.Register(Lyric);
    }

    /// <summary>设计画布尺寸：图层取景、效果半径、镜头位移都以它为单位。</summary>
    public int DesignWidth => _designW;
    public int DesignHeight => _designH;

    public void SetLyrics(IReadOnlyList<KrcLine>? lines) => Lyric?.SetLyrics(lines);

    public void Prepare(double time)
    {
        _frameTime = time;
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

    /// <summary>
    /// 合成一帧：先在固定设计画布上图层栈、跑效果图，最后等比贴到目标。
    /// 目标尺寸只决定这块 letterbox 矩形，不参与任何内容坐标计算。
    /// </summary>
    public void Composite(ID2D1Bitmap1 target, int width, int height)
    {
        EnsureStageTarget();
        var d = _d2d.Draw;
        var frame = new PvFrameContext(_frameTime, Stage.Beat, Stage.Energy, _designW, _designH);

        // 1) 图层栈合成到可采样面（永远是设计尺寸：窗口缩放不重上面、不重分配）
        d.SetTarget(_stageTarget!.Bitmap, _designW, _designH);
        d.Begin();
        d.Clear(new Color4(0f, 0f, 0f, 1f));
        Stage.RenderFrame(d, frame);
        d.End();
        var t1 = System.Diagnostics.Stopwatch.GetTimestamp();

        // 2) 效果图（D3D11 全屏 pass 链）：像素半径按设计尺寸标定，与预览分辨率无关
        GpuTexturePool.Entry? fxOut = null;
        var passes = Effects.Nodes.Any(n => n.Enabled)
            ? Effects.BuildRenderGraph(_fx.Shaders, (float)frame.Time, _designW, _designH)
            : null;
        if (passes is { Count: > 0 })
            fxOut = _fx.RunGraph(passes, _stageSrv!, _designW, _designH);

        var t2 = System.Diagnostics.Stopwatch.GetTimestamp();

        // 3) 等比贴到目标，多出来的地方留黑
        d.SetTarget(target, width, height);
        d.Begin();
        d.Clear(new Color4(0f, 0f, 0f, 1f));
        d.DrawBitmap(fxOut is null ? _stageTarget.Bitmap : BitmapFor(fxOut),
            Letterbox(_designW, _designH, width, height));
        d.End();

        if (!_freezeLast && fxOut is not null) _fx.Pool.Return(fxOut);
        LastTiming = (LastTiming.Engine, Elapsed(t1), Elapsed(t2));
    }

    /// <summary>
    /// 冻结效果链：停机的第二步，让后续 Composite 保留末帧（见 <see cref="_freezeLast"/>）。
    /// 可在任意线程调用；渲染线程在下一帧 Composite 末尾读到它。
    /// </summary>
    public void FreezeFrames() => _freezeLast = true;

    /// <summary>
    /// 清掉 D2D 位图缓存（临时面句柄→位图）。按代际清理用：设备/上下文重建或画布换代时，
    /// 旧位图绑的是已作废的 DXGI 面，留着只会持续占 D2D 资源且再也不会被命中。
    /// 必须在渲染线程上调用（与 <see cref="BitmapFor"/> 同一线程）。
    /// </summary>
    public void ClearFxBitmapCache()
    {
        foreach (var b in _fxBitmaps.Values) b.Dispose();
        _fxBitmaps.Clear();
    }

    /// <summary>把设计画布等比放进目标矩形，居中，余量留黑。</summary>
    public static Rect Letterbox(int designW, int designH, int targetW, int targetH)
    {
        var s = Math.Min(targetW / (float)designW, targetH / (float)designH);
        var w = designW * s;
        var h = designH * s;
        return new Rect((targetW - w) / 2f, (targetH - h) / 2f, w, h);
    }

    private void EnsureStageTarget()
    {
        if (_stageTarget is not null) return;
        _stageTarget = OffscreenTarget.Create(_gpu, _d2d.Ctx, _designW, _designH, sampleable: true);
        _stageSrv = _gpu.Device.CreateShaderResourceView(_stageTarget.Texture);
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
        ClearFxBitmapCache();
        _stageSrv?.Dispose();
        _stageTarget?.Dispose();
        // Lyric 已注册在 Stage 的提供者表里，释放权归 Stage，这里不再单独 Dispose 它
        Stage.Dispose();
        _fx.Dispose();
    }
}
