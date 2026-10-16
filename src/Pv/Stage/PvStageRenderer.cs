using JizuraLive.Engine;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Effects;
using JizuraLive.Render.Gpu;
using JizuraLive.Render.Text;
using JizuraLive.Spout;
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
    private OffscreenTarget? _stageTarget;
    private ID3D11ShaderResourceView? _stageSrv;

    /// <summary>广播发送端（可空）。所有权在管线：这里只记引用，绝不 Dispose 它。</summary>
    private SpoutOut? _broadcast;

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

    /// <summary>
    /// 效果图快照：构造期是管线给的初始快照，此后每帧由 <see cref="Prepare"/> 换成管线当帧那一份。
    /// 渲染器不长期持有文档图——文档图归 UI 线程，渲染线程每帧只看一份冻结的克隆
    /// （曲线值会写进它，所以那个对象上不能有第二个写者）。volatile 是因为别的线程（状态条）读它。
    /// </summary>
    private volatile EffectGraph _effects;

    /// <summary>
    /// 本帧 Prepare 传进来的那一份：帧内构建效果链只认它。
    /// 为什么不直接读 <see cref="_effects"/>：帧中途 UI 线程可能又发布了一份，那样曲线求值算的是上一份、
    /// 建的链却是新一份，同一帧的常量会对不上。
    /// </summary>
    private EffectGraph _frameEffects;

    public EffectGraph Effects => _effects;

    /// <summary>
    /// 全局后期**之后**的叠加层（清晰字幕）。所有权在调用方：这里只记引用，绝不 Dispose 它。
    /// 它不参与 <see cref="Stage"/> 的 RenderFrame，因此不吃视差与场景模糊，也不进图层栈
    /// （契约 PV-DIRECTOR-SYSTEM.md:399「字幕不经过视差/场景模糊」）。
    /// </summary>
    public IPvLayerProvider? Overlay { get; set; }

    /// <summary>最近一帧各段耗时（毫秒）。</summary>
    public (double Engine, double Fx, double Composite) LastTiming { get; private set; }

    /// <summary>效果链段数（状态条显示用）。</summary>
    public int EffectCount => Effects.Nodes.Count(n => n.Enabled);

    /// <summary>效果链的纹理池（诊断/验收用：池的计数快照是显存复用的观测面）。</summary>
    public GpuTexturePool FxPool => _fx.Pool;

    /// <summary>
    /// 本代际创建的 fx 位图数（诊断用）。位图挂在池 entry 上、随 entry 复用，
    /// 所以它由「池的复用集大小」封顶，**不随帧数增长**——它涨到帧数量级就说明
    /// 最终输出的纹理每帧都是新的（池在稳态下没有真正复用）。
    /// </summary>
    public int FxBitmapsCreated { get; private set; }

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
        _effects = effects;
        _frameEffects = effects;
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

    /// <summary>
    /// 接入 / 摘除广播发送端。仅渲染线程调用（与 Composite 同一线程，无同步）。
    /// 只存引用：发送端 native 资源的建立与释放都归管线。
    /// </summary>
    public void SetBroadcast(SpoutOut? outlet) => _broadcast = outlet;

    /// <summary>
    /// 本帧准备：记下本帧时刻与效果图快照（都是帧内只读的一份），再驱动歌词排版。
    /// <paramref name="effects"/> 由管线在帧首取到的发布快照传入——渲染器不自己回头读发布槽，
    /// 否则同一帧的曲线求值与效果链可能落在两份不同的图上。
    /// </summary>
    public void Prepare(double time, EffectGraph effects)
    {
        _effects = effects;
        _frameEffects = effects;
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
        var passes = _frameEffects.Nodes.Any(n => n.Enabled)
            ? _frameEffects.BuildRenderGraph(_fx.Shaders, (float)frame.Time, _designW, _designH)
            : null;
        if (passes is { Count: > 0 })
            fxOut = _fx.RunGraph(passes, _stageSrv!, _designW, _designH);

        var t2 = System.Diagnostics.Stopwatch.GetTimestamp();

        // 3) 等比贴到目标，多出来的地方留黑
        d.SetTarget(target, width, height);
        d.Begin();
        d.Clear(new Color4(0f, 0f, 0f, 1f));
        var previewRect = Letterbox(_designW, _designH, width, height);
        d.DrawBitmap(fxOut is null ? _stageTarget.Bitmap : BitmapFor(fxOut), previewRect);
        DrawOverlay(d, frame, previewRect);
        d.End();

        // 4) 广播：把同一张完成帧（有效果取效果输出，无效果取舞台面）再贴一次到发送端纹理后发出。
        //    不重求值、不重跑 Composite——只是同一块显存的第二次贴图。
        //    广播尺寸恒为设计尺寸（发送端由管线按 DesignWidth/DesignHeight 建）：预览窗口怎么缩，
        //    接收端拿到的一直是设计分辨率，这是闸门要求。
        //    慢发送策略（绝对截止点、跳过时隙）留给 M7：同步渲染循环里发送不积压，量级已实测。
        if (_broadcast is { IsActive: true, Bitmap: { } bbmp } b)
        {
            var src = fxOut is null ? _stageTarget.Bitmap : BitmapFor(fxOut);
            var broadcastRect = new Rect(0, 0, b.Width, b.Height);
            d.SetTarget(bbmp, b.Width, b.Height);
            d.Begin();
            d.Clear(new Color4(0f, 0f, 0f, 1f));
            d.DrawBitmap(src, broadcastRect);
            DrawOverlay(d, frame, broadcastRect);
            d.End();
            b.PresentFrame();
        }

        if (!_freezeLast && fxOut is not null) _fx.Pool.Return(fxOut);
        LastTiming = (LastTiming.Engine, Elapsed(t1), Elapsed(t2));
    }

    /// <summary>
    /// 把叠加层贴进与底图**同一块**矩形：位图按该矩形缩放（叠加层按设计尺寸排版），
    /// 纯色层直接铺满该矩形。画在效果输出之后，所以清晰字幕不会被后期改色/糊掉，
    /// 且预览与广播走同一段代码——两边永远同步。
    /// </summary>
    private void DrawOverlay(Immediate2D d, in PvFrameContext frame, Rect rect)
    {
        if (Overlay is null) return;

        var image = Overlay.Pump(frame);
        if (image.Bitmap is { } bmp) d.DrawBitmap(bmp, rect);
        else if (image.Solid is { } solid)
            d.FillRect(rect, new Paint2D { Brush = new SolidBrush2D(solid) });
    }

    /// <summary>
    /// 冻结效果链：停机的第二步，让后续 Composite 保留末帧（见 <see cref="_freezeLast"/>）。
    /// 可在任意线程调用；渲染线程在下一帧 Composite 末尾读到它。
    /// </summary>
    public void FreezeFrames() => _freezeLast = true;

    /// <summary>
    /// 清掉挂在池 entry 上的 D2D 位图（按代际清理用）：设备/上下文重建或画布换代时，
    /// 旧位图绑的是已作废的 D2D 上下文，留着只会持续占资源且再也不会被正确命中。
    /// 下一次 <see cref="BitmapFor"/> 会按当前上下文重建。
    ///
    /// 位图挂在 entry 上、随 entry 淘汰/释放自动 Dispose（见 <see cref="GpuTexturePool.Entry.Attach"/>），
    /// 所以这里只负责「换代」这一件事，运行期不需要逐帧维护。
    /// 必须在渲染线程上调用（与 <see cref="BitmapFor"/> 同一线程）。
    /// </summary>
    public void ClearFxBitmapCache()
    {
        foreach (var entry in _fx.Pool.LiveEntries) entry.DisposeAttachment();
        FxBitmapsCreated = 0;
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

    /// <summary>
    /// 把效果链的输出面包成 D2D 位图（预览与广播共用同一张）。
    ///
    /// 缓存挂在 <see cref="GpuTexturePool.Entry"/> 上，不按纹理指针另建字典：
    /// 位图持有底层 DXGI 面的引用，只要它活着，被淘汰的纹理就释放不掉；而按指针索引还会在
    /// 纹理释放、指针被 COM 分配器复用后把旧位图当成新纹理的命中。绑到 entry 身份上，
    /// 位图随 entry 淘汰/释放一起 Dispose，缓存集也由池的复用集封顶。
    /// </summary>
    private ID2D1Bitmap1 BitmapFor(GpuTexturePool.Entry entry)
    {
        if (entry.TryGetAttachment(out ID2D1Bitmap1? cached)) return cached;

        using var surface = entry.Texture.QueryInterface<IDXGISurface>();
        var bmp = _d2d.Ctx.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
            new DCommonPixelFormat(Format.B8G8R8A8_UNorm, DCommonAlphaMode.Premultiplied),
            96f, 96f, BitmapOptions.None));
        entry.Attach(bmp);
        FxBitmapsCreated++;
        return bmp;
    }

    private static double Elapsed(long from) =>
        (System.Diagnostics.Stopwatch.GetTimestamp() - from) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    public void Dispose()
    {
        // 只摘引用：广播发送端归管线所有，由管线在自己的 Dispose 里释放
        _broadcast = null;
        Overlay = null;
        ClearFxBitmapCache();
        _stageSrv?.Dispose();
        _stageTarget?.Dispose();
        // Lyric 已注册在 Stage 的提供者表里，释放权归 Stage，这里不再单独 Dispose 它
        Stage.Dispose();
        _fx.Dispose();
    }
}
