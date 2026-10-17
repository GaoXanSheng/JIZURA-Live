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

public sealed class PvStageRenderer : IDisposable
{
    private readonly D3DDevice _gpu;
    private readonly D2DHost _d2d;
    private readonly GpuEffectRunner _fx;
    private OffscreenTarget? _stageTarget;
    private ID3D11ShaderResourceView? _stageSrv;

    private readonly Dictionary<string, LayerTarget> _layerTargets = new(StringComparer.Ordinal);

    private SpoutOut? _broadcast;

    private readonly int _designW, _designH;

    private readonly TransitionRunner _transitionRunner;

    private readonly TransitionParams _transParams = new();

    private DesignSegment? _transSeg;

    private bool _transPrimed;

    private bool _freezeLast;

    private double _frameTime;

    public TypographyLayerProvider? Lyric { get; }
    public PvStage Stage { get; }

    public TransitionTrack Transitions { get; } = new();

    public void SetDesignSegments(IEnumerable<DesignSegment>? segments) => Transitions.LoadFrom(segments);

    private volatile EffectGraph _effects;

    private EffectGraph _frameEffects;

    public EffectGraph Effects => _effects;

    public IPvLayerProvider? Overlay { get; set; }

    public (double Engine, double Fx, double Composite) LastTiming { get; private set; }

    public int EffectCount => Effects.Nodes.Count(n => n.Enabled);

    public GpuTexturePool FxPool => _fx.Pool;

    public int FxBitmapsCreated { get; private set; }

    public ID2D1DeviceContext D2DContext => _d2d.Ctx;

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
        _designW = Math.Max(16, designWidth);
        _designH = Math.Max(16, designHeight);
        _transitionRunner = new TransitionRunner(d2d.Draw, _fx.Pool, _designW, _designH);
        LyricMotion.Transitions = Transitions;

        Lyric = new TypographyLayerProvider("lyrics_pv", gpu, d2d, _designW, _designH);
        Stage.Register(Lyric);
    }

    public int DesignWidth => _designW;
    public int DesignHeight => _designH;

    public void SetLyrics(IReadOnlyList<KrcLine>? lines) => Lyric?.SetLyrics(lines);

    public void SetBroadcast(SpoutOut? outlet) => _broadcast = outlet;

    public void Prepare(double time, EffectGraph effects)
    {
        _effects = effects;
        _frameEffects = effects;
        _frameTime = time;
        if (Lyric is null) return;
        Lyric.MotionSeed = Stage.Scene.LyricMotionSeed;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Lyric.Render(time);
        LastTiming = (Elapsed(t0), LastTiming.Fx, LastTiming.Composite);
    }

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

    private static readonly PvLayerConfig LyricsFallbackLayer = new()
    {
        Id = "lyrics_pv",
        Name = "动效歌词",
        Kind = PvLayerKind.LyricPv,
    };

    private PvLayerConfig? FindLayer(string id)
    {
        var layers = Stage.Scene.Layers;
        for (var i = 0; i < layers.Count; i++)
            if (string.Equals(layers[i].Id, id, StringComparison.Ordinal)) return layers[i];
        return null;
    }

    public void Composite(ID2D1Bitmap1 target, int width, int height)
    {
        EnsureStageTarget();
        var d = _d2d.Draw;
        var frame = new PvFrameContext(_frameTime, Stage.Beat, Stage.Energy, _designW, _designH);

        Stage.PumpImages(frame);
        var images = Stage.LastImages;
        var compositor = new PvStageCompositor(d);
        var returns = new List<GpuTexturePool.Entry>();
        var full = new Rect(0, 0, _designW, _designH);

        var transSeg = Transitions.At(_frameTime);
        var transTech = transSeg is null ? null : TransitionCatalog.Get(transSeg.Technique);
        if (transTech is not null && (!_transPrimed || !ReferenceEquals(_transSeg, transSeg)))
        {
            _transitionRunner.Acquire();
            BlitInto(_transitionRunner.SurfaceA, _stageTarget!.Bitmap);
            _transPrimed = true;
            _transSeg = transSeg;
        }

        d.SetTarget(_stageTarget!.Bitmap, _designW, _designH);
        d.Begin();
        d.Clear(new Color4(0f, 0f, 0f, 1f));

        var frameLayers = Stage.Scene.Layers;
        for (var li = 0; li < frameLayers.Count; li++)
        {
            var layer = frameLayers[li];
            if (!layer.Enabled) continue;
            if (!images.TryGetValue(layer.Id, out var image) || !image.HasContent) continue;

            var state = Stage.Scene.Frame.TryGetLayer(layer.Id, out var frameState)
                ? frameState
                : PvLayerState.From(layer);

            if (!_frameEffects.HasEffectsFor(layer.Id))
            {
                compositor.CompositeLayer(Stage.Scene, layer, state, image, Stage.Camera, _designW, _designH);
                continue;
            }

            d.End();
            DrawLayerWithEffects(d, compositor, layer, state, image, frame, returns, full);
        }

        compositor.DrawBrighten(Stage.Camera, _designW, _designH);

        foreach (var id in PvStageCompositor.FixedZones)
        {
            if (!images.TryGetValue(id, out var zone) || !zone.HasContent) continue;

            if (string.Equals(id, "lyrics_pv", StringComparison.Ordinal) && FindLayer(id) is null)
            {
                var fallback = LyricsFallbackLayer;
                compositor.CompositeLayer(Stage.Scene, fallback, PvLayerState.From(fallback), zone,
                    Stage.Camera, _designW, _designH);
                continue;
            }

            if (!_frameEffects.HasEffectsFor(id))
            {
                compositor.CompositeFixedZone(id, images, _designW, _designH);
                continue;
            }

            d.End();
            DrawZoneWithEffects(d, compositor, id, images, frame, returns, full);
        }

        d.End();
        if (transTech is not null && transSeg is not null)
        {
            BlitInto(_transitionRunner.SurfaceB, _stageTarget.Bitmap);
            ApplyTransitionParams(transSeg);
            _transitionRunner.Composite(_stageTarget.Bitmap, full, transTech,
                new TransitionWindow(transSeg.Start, transSeg.End).Progress(_frameTime), _transParams);
        }
        else if (_transPrimed)
        {
            _transitionRunner.Release();
            _transPrimed = false;
            _transSeg = null;
        }

        foreach (var entry in returns) _fx.Pool.Return(entry);
        var t1 = System.Diagnostics.Stopwatch.GetTimestamp();

        GpuTexturePool.Entry? fxOut = null;
        var passes = _frameEffects.HasEffectsFor("")
            ? _frameEffects.BuildRenderGraph(_fx.Shaders, (float)frame.Time, _designW, _designH)
            : null;
        if (passes is { Count: > 0 })
            fxOut = _fx.RunGraph(passes, _stageSrv!, _designW, _designH);

        var t2 = System.Diagnostics.Stopwatch.GetTimestamp();

        d.SetTarget(target, width, height);
        d.Begin();
        d.Clear(new Color4(0f, 0f, 0f, 1f));
        var previewRect = Letterbox(_designW, _designH, width, height);
        d.DrawBitmap(fxOut is null ? _stageTarget.Bitmap : BitmapFor(fxOut), previewRect);
        DrawOverlay(d, frame, previewRect);
        d.End();

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

    private void DrawLayerWithEffects(Immediate2D d, PvStageCompositor compositor, PvLayerConfig layer,
        in PvLayerState state, PvLayerImage image, in PvFrameContext frame,
        List<GpuTexturePool.Entry> returns, Rect full)
    {
        var target = LayerTargetFor(layer.Id);

        d.SetTarget(target.Surface.Bitmap, _designW, _designH);
        d.Begin();
        d.Clear(new Color4(0f, 0f, 0f, 0f));
        compositor.CompositeLayer(Stage.Scene, layer, state, image, Stage.Camera, _designW, _designH);
        d.End();

        var graph = _frameEffects.BuildRenderGraph(_fx.Shaders, (float)frame.Time, _designW, _designH, layer.Id);
        var output = graph.Count > 0 ? _fx.RunGraph(graph, target.Srv, _designW, _designH) : null;

        d.SetTarget(_stageTarget!.Bitmap, _designW, _designH);
        d.Begin();
        if (output is not null)
        {
            d.DrawBitmap(BitmapFor(output), full);
            returns.Add(output);
        }
        else
        {
            d.DrawBitmap(target.Surface.Bitmap, full);
        }
    }

    private void DrawZoneWithEffects(Immediate2D d, PvStageCompositor compositor, string id,
        IReadOnlyDictionary<string, PvLayerImage> images, in PvFrameContext frame,
        List<GpuTexturePool.Entry> returns, Rect full)
    {
        var target = LayerTargetFor(id);

        d.SetTarget(target.Surface.Bitmap, _designW, _designH);
        d.Begin();
        d.Clear(new Color4(0f, 0f, 0f, 0f));
        compositor.CompositeFixedZone(id, images, _designW, _designH);
        d.End();

        var graph = _frameEffects.BuildRenderGraph(_fx.Shaders, (float)frame.Time, _designW, _designH, id);
        var output = graph.Count > 0 ? _fx.RunGraph(graph, target.Srv, _designW, _designH) : null;

        d.SetTarget(_stageTarget!.Bitmap, _designW, _designH);
        d.Begin();
        if (output is not null)
        {
            d.DrawBitmap(BitmapFor(output), full);
            returns.Add(output);
        }
        else
        {
            d.DrawBitmap(target.Surface.Bitmap, full);
        }
    }

    private LayerTarget LayerTargetFor(string id)
    {
        if (_layerTargets.TryGetValue(id, out var existing)) return existing;

        var surface = OffscreenTarget.Create(_gpu, _d2d.Ctx, _designW, _designH, sampleable: true);
        var target = new LayerTarget(surface, _gpu.Device.CreateShaderResourceView(surface.Texture));
        _layerTargets[id] = target;
        return target;
    }

    private sealed class LayerTarget : IDisposable
    {
        public OffscreenTarget Surface { get; }
        public ID3D11ShaderResourceView Srv { get; }

        public LayerTarget(OffscreenTarget surface, ID3D11ShaderResourceView srv)
        {
            Surface = surface;
            Srv = srv;
        }

        public void Dispose()
        {
            Srv.Dispose();
            Surface.Dispose();
        }
    }

    private void DrawOverlay(Immediate2D d, in PvFrameContext frame, Rect rect)
    {
        if (Overlay is null) return;

        var image = Overlay.Pump(frame);
        if (image.Bitmap is { } bmp) d.DrawBitmap(bmp, rect);
        else if (image.Solid is { } solid)
            d.FillRect(rect, new Paint2D { Brush = new SolidBrush2D(solid) });
    }

    public void FreezeFrames() => _freezeLast = true;

    public void ClearFxBitmapCache()
    {
        foreach (var entry in _fx.Pool.LiveEntries) entry.DisposeAttachment();
        FxBitmapsCreated = 0;
    }

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
        if (entry.TryGetAttachment(out ID2D1Bitmap1? cached)) return cached;

        using var surface = entry.Texture.QueryInterface<IDXGISurface>();
        var bmp = _d2d.Ctx.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
            new DCommonPixelFormat(Format.B8G8R8A8_UNorm, DCommonAlphaMode.Premultiplied),
            96f, 96f, BitmapOptions.None));
        entry.Attach(bmp);
        FxBitmapsCreated++;
        return bmp;
    }

    private void BlitInto(ID2D1Bitmap1? dst, ID2D1Bitmap1 src)
    {
        if (dst is null) return;
        var d = _d2d.Draw;
        d.SetTarget(dst, _designW, _designH);
        d.Begin();
        try
        {
            d.Clear(new Color4(0f, 0f, 0f, 0f));
            d.DrawBitmap(src, new Rect(0, 0, _designW, _designH));
        }
        finally
        {
            d.End();
        }
    }

    private void ApplyTransitionParams(DesignSegment seg)
    {
        var values = _transParams.Values;
        values.Clear();
        foreach (var (key, value) in seg.Params) values[key] = value;

        _transParams.Seed = seg.Seed != 0
            ? seg.Seed
            : DeterministicSeed(seg.Start);

        var colors = _transParams.Colors;
        colors.Clear();
        if (seg.Colors is not null)
        {
            foreach (var (key, value) in seg.Colors) colors[key] = value;
            return;
        }

        var scene = Stage.Scene;
        var style = LyricStyle.FromAnyName(scene.LyricStyleName) ?? LyricStyle.Classic;
        colors["accent"] = style.HighlightColor;
        colors["accent2"] = style.Color;
        colors["bg"] = scene.AmbientTint;
        colors["bgPrev"] = DarkenedHex(scene.AmbientTint, 0.45);
        colors["bgCur"] = DarkenedHex(scene.AmbientTint, 0.3);
    }

    private static int DeterministicSeed(double start) =>
        unchecked((int)(Math.Round(start * 1000.0) * 2654435761) ^ 0x5f3759df);

    private static string DarkenedHex(string hex, double factor)
    {
        var c = PvScene.ParseHex(hex);
        return $"#{Math.Clamp((int)(c.R * 255 * factor), 0, 255):x2}"
               + $"{Math.Clamp((int)(c.G * 255 * factor), 0, 255):x2}"
               + $"{Math.Clamp((int)(c.B * 255 * factor), 0, 255):x2}";
    }

    private static double Elapsed(long from) =>
        (System.Diagnostics.Stopwatch.GetTimestamp() - from) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    public void Dispose()
    {
        _broadcast = null;
        Overlay = null;
        if (ReferenceEquals(LyricMotion.Transitions, Transitions)) LyricMotion.Transitions = null;
        _transitionRunner.Dispose();
        ClearFxBitmapCache();
        foreach (var target in _layerTargets.Values) target.Dispose();
        _layerTargets.Clear();
        _stageSrv?.Dispose();
        _stageTarget?.Dispose();
        Stage.Dispose();
        _fx.Dispose();
    }
}
