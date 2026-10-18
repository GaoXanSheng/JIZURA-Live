using System.Numerics;
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
    private readonly PvStageCompositor _compositor;
    private readonly List<GpuTexturePool.Entry> _returns = new();
    private OffscreenTarget? _stageTarget;
    private ID3D11ShaderResourceView? _stageSrv;
    private OffscreenTarget? _transparentStageTarget;
    private ID3D11ShaderResourceView? _transparentStageSrv;

    private readonly Dictionary<string, LayerTarget> _layerTargets = new(StringComparer.Ordinal);

    private SpoutOut? _broadcast;

    public bool SpoutTransparent { get; set; }

    public bool LyricsAvoid { get; set; }

    public double LyricsAvoidPadding { get; set; } = 0.03;

    public double LyricsAvoidMinScale { get; set; } = 0.5;

    private OffscreenTarget? _probeTarget;
    private int _avoidFrame;
    private Rect _charBounds;
    private Rect _lyricsBounds;
    private bool _charBoundsValid;
    private bool _lyricsBoundsValid;
    private int _frameIndex;
    private int _avoidStateFrame = -1;
    private PvLayerState _avoidState;

    private readonly int _designW, _designH;

    private readonly TransitionRunner _transitionRunner;

    private readonly TransitionParams _transParams = new();

    private DesignSegment? _transSeg;

    private bool _transPrimed;

    private bool _freezeLast;

    private double _frameTime;

    public PvStage Stage { get; }

    public TransitionTrack Transitions { get; } = new();

    public void SetDesignSegments(IEnumerable<DesignSegment>? segments)
    {
        Transitions.LoadFrom(segments);
    }

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
        _compositor = new PvStageCompositor(d2d.Draw);
        _effects = effects;
        _frameEffects = effects;
        Stage = new PvStage(scene);
        _designW = Math.Max(16, designWidth);
        _designH = Math.Max(16, designHeight);
        _transitionRunner = new TransitionRunner(d2d.Draw, _fx.Pool, _designW, _designH);
    }

    public int DesignWidth => _designW;
    public int DesignHeight => _designH;

    public void SetBroadcast(SpoutOut? outlet) => _broadcast = outlet;

    public void Prepare(double time, EffectGraph effects)
    {
        _effects = effects;
        _frameEffects = effects;
        _frameTime = time;
        LastTiming = (0, LastTiming.Fx, LastTiming.Composite);
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

    public void Composite(ID2D1Bitmap1 target, int width, int height)
    {
        EnsureStageTarget();
        _frameIndex++;
        var d = _d2d.Draw;
        var frame = new PvFrameContext(_frameTime, Stage.Beat, Stage.Energy, _designW, _designH);

        Stage.PumpImages(frame);
        var images = Stage.LastImages;
        UpdateAvoidBounds(images);
        var compositor = _compositor;
        var returns = _returns;
        returns.Clear();
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

            var state = LayerState(layer);

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
        returns.Clear();
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
        DrawStage(d, fxOut is null ? _stageTarget.Bitmap : BitmapFor(fxOut), previewRect);
        DrawOverlay(d, frame, previewRect);
        d.End();

        if (_broadcast is { IsActive: true, Bitmap: { } bbmp } b)
        {
            var broadcastRect = new Rect(0, 0, b.Width, b.Height);
            if (SpoutTransparent)
            {
                EnsureTransparentStageTarget();
                var transparent = _transparentStageTarget!;

                d.SetTarget(transparent.Bitmap, _designW, _designH);
                d.Begin();
                d.Clear(new Color4(0f, 0f, 0f, 0f));
                CompositeTransparent(images);
                d.End();

                GpuTexturePool.Entry? transparentOut = null;
                if (passes is { Count: > 0 })
                    transparentOut = _fx.RunGraph(passes, _transparentStageSrv!, _designW, _designH);

                d.SetTarget(bbmp, b.Width, b.Height);
                d.Begin();
                d.Clear(new Color4(0f, 0f, 0f, 0f));
                DrawStage(d, transparentOut is null ? transparent.Bitmap : BitmapFor(transparentOut), broadcastRect);
                DrawOverlay(d, frame, broadcastRect);
                d.End();

                if (transparentOut is not null) _fx.Pool.Return(transparentOut);
            }
            else
            {
                var src = fxOut is null ? _stageTarget.Bitmap : BitmapFor(fxOut);
                d.SetTarget(bbmp, b.Width, b.Height);
                d.Begin();
                d.Clear(new Color4(0f, 0f, 0f, 1f));
                DrawStage(d, src, broadcastRect);
                DrawOverlay(d, frame, broadcastRect);
                d.End();
            }

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

    private void DrawStage(Immediate2D d, ID2D1Bitmap1 bitmap, Rect rect)
    {
        d.DrawBitmap(bitmap, rect);
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

    private void EnsureTransparentStageTarget()
    {
        if (_transparentStageTarget is not null) return;
        _transparentStageTarget = OffscreenTarget.Create(_gpu, _d2d.Ctx, _designW, _designH, sampleable: true);
        _transparentStageSrv = _gpu.Device.CreateShaderResourceView(_transparentStageTarget.Texture);
    }

    private void CompositeTransparent(IReadOnlyDictionary<string, PvLayerImage> images)
    {
        var scene = Stage.Scene;
        var layers = scene.Layers;
        for (var i = 0; i < layers.Count; i++)
        {
            var layer = layers[i];
            if (!layer.Enabled) continue;
            if (!images.TryGetValue(layer.Id, out var image) || !image.HasContent) continue;

            var state = LayerState(layer);
            _compositor.CompositeLayer(scene, layer, state, image, Stage.Camera, _designW, _designH);
        }
    }

    private PvLayerState LayerState(PvLayerConfig layer)
    {
        var state = Stage.Scene.Frame.TryGetLayer(layer.Id, out var frameState)
            ? frameState
            : PvLayerState.From(layer);

        if (!LyricsAvoid
            || layer.Id != LegacyPvProvider.FrontId
            || !_lyricsBoundsValid
            || !_charBoundsValid)
        {
            if (layer.Id == LegacyPvProvider.FrontId) _avoidStateFrame = -1;
            return state;
        }

        if (_avoidStateFrame == _frameIndex) return _avoidState;

        var target = ApplyLyricsAvoidance(state, _lyricsBounds, _charBounds);
        if (_avoidStateFrame == _frameIndex - 1)
        {
            target = new PvLayerState(
                Lerp(_avoidState.OffsetX, target.OffsetX, 0.45),
                Lerp(_avoidState.OffsetY, target.OffsetY, 0.45),
                Lerp(_avoidState.Scale, target.Scale, 0.45),
                target.Opacity,
                target.Rotation);
        }

        _avoidState = target;
        _avoidStateFrame = _frameIndex;
        return target;
    }

    private void UpdateAvoidBounds(IReadOnlyDictionary<string, PvLayerImage> images)
    {
        if (!LyricsAvoid) return;
        if (++_avoidFrame % 3 != 0) return;

        _charBoundsValid = TryGetLayerBounds(PvScene.CharacterLayerId, images, out _charBounds);
        _lyricsBoundsValid = TryGetLayerBounds(LegacyPvProvider.FrontId, images, out _lyricsBounds);
    }

    private bool TryGetLayerBounds(string layerId, IReadOnlyDictionary<string, PvLayerImage> images, out Rect bounds)
    {
        bounds = default;

        var scene = Stage.Scene;
        var layer = scene.Layers.FirstOrDefault(l => l.Id == layerId);
        if (layer is null || !layer.Enabled) return false;
        if (!images.TryGetValue(layerId, out var image) || image.Bitmap is null) return false;
        if (!TryProbeAlpha(image, layerId == PvScene.CharacterLayerId, out var norm)) return false;

        var state = scene.Frame.TryGetLayer(layerId, out var frameState)
            ? frameState
            : PvLayerState.From(layer);
        var tf = PvParallax.Compute(_designW, _designH, layer, state, Stage.Camera,
            globalParallax: scene.GlobalParallaxEnabled,
            antiBleed: scene.AntiBleedGuardEnabled);

        var dest = PvStageCompositor.FitRect(image, layer.Cover, _designW, _designH);
        var x0 = (float)(dest.X + norm.X * dest.Width);
        var y0 = (float)(dest.Y + norm.Y * dest.Height);
        var x1 = (float)(dest.X + (norm.X + norm.Width) * dest.Width);
        var y1 = (float)(dest.Y + (norm.Y + norm.Height) * dest.Height);

        var p1 = Vector2.Transform(new Vector2(x0, y0), tf.Matrix);
        var p2 = Vector2.Transform(new Vector2(x1, y0), tf.Matrix);
        var p3 = Vector2.Transform(new Vector2(x1, y1), tf.Matrix);
        var p4 = Vector2.Transform(new Vector2(x0, y1), tf.Matrix);

        var minX = Math.Min(Math.Min(p1.X, p2.X), Math.Min(p3.X, p4.X));
        var minY = Math.Min(Math.Min(p1.Y, p2.Y), Math.Min(p3.Y, p4.Y));
        var maxX = Math.Max(Math.Max(p1.X, p2.X), Math.Max(p3.X, p4.X));
        var maxY = Math.Max(Math.Max(p1.Y, p2.Y), Math.Max(p3.Y, p4.Y));

        bounds = new Rect(minX, minY, maxX - minX, maxY - minY);
        return bounds.Width > 1 && bounds.Height > 1;
    }

    private bool TryProbeAlpha(PvLayerImage image, bool allowColorKey, out Rect norm)
    {
        norm = default;
        EnsureProbeTarget();
        var probe = _probeTarget!;
        var d = _d2d.Draw;

        d.SetTarget(probe.Bitmap, probe.Width, probe.Height);
        d.Begin();
        try
        {
            d.Clear(new Color4(0f, 0f, 0f, 0f));
            d.DrawBitmap(image.Bitmap!, new Rect(0, 0, probe.Width, probe.Height), 1f);
        }
        finally
        {
            d.End();
        }

        using var read = probe.Readback();
        var pixels = read.BgraPixels;
        int minX = probe.Width, minY = probe.Height, maxX = -1, maxY = -1;
        for (var y = 0; y < probe.Height; y++)
        {
            var row = y * probe.Width * 4;
            for (var x = 0; x < probe.Width; x++)
            {
                if (pixels[row + x * 4 + 3] <= 8) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        var alphaFull = maxX - minX + 1 >= probe.Width * 0.9f
                        && maxY - minY + 1 >= probe.Height * 0.9f;
        if (allowColorKey && alphaFull && TryColorKeyBounds(pixels, probe.Width, probe.Height, out norm))
            return true;

        if (maxX < minX || maxY < minY) return false;

        norm = new Rect(
            minX / (float)probe.Width,
            minY / (float)probe.Height,
            (maxX - minX + 1) / (float)probe.Width,
            (maxY - minY + 1) / (float)probe.Height);
        return true;
    }

    private static bool TryColorKeyBounds(byte[] pixels, int w, int h, out Rect norm)
    {
        norm = default;

        // 用边框出现最多的量化颜色当底色键；VTS 不透明输出时 alpha 全满，只能靠色差找角色。
        var hist = new Dictionary<int, int>();
        void Sample(int x, int y)
        {
            var i = (y * w + x) * 4;
            var key = ((pixels[i + 2] >> 3) << 10) | ((pixels[i + 1] >> 3) << 5) | (pixels[i] >> 3);
            hist[key] = hist.TryGetValue(key, out var count) ? count + 1 : 1;
        }

        for (var x = 0; x < w; x++)
        {
            Sample(x, 0);
            Sample(x, h - 1);
        }

        for (var y = 1; y < h - 1; y++)
        {
            Sample(0, y);
            Sample(w - 1, y);
        }

        if (hist.Count == 0) return false;

        var best = hist.MaxBy(p => p.Value).Key;
        int br = ((best >> 10) & 31) << 3;
        int bg = ((best >> 5) & 31) << 3;
        int bb = (best & 31) << 3;

        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (var y = 0; y < h; y++)
        {
            var row = y * w * 4;
            for (var x = 0; x < w; x++)
            {
                var i = row + x * 4;
                var dist = Math.Abs(pixels[i + 2] - br) + Math.Abs(pixels[i + 1] - bg) + Math.Abs(pixels[i] - bb);
                if (dist <= 48) continue;

                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        if (maxX < minX || maxY < minY) return false;
        if (maxX - minX + 1 >= w && maxY - minY + 1 >= h) return false;

        norm = new Rect(
            minX / (float)w,
            minY / (float)h,
            (maxX - minX + 1) / (float)w,
            (maxY - minY + 1) / (float)h);
        return true;
    }

    private void EnsureProbeTarget()
    {
        _probeTarget ??= OffscreenTarget.Create(_gpu, _d2d.Ctx, 96, 54, sampleable: true);
    }

    private PvLayerState ApplyLyricsAvoidance(PvLayerState state, Rect text, Rect character)
    {
        var pad = (float)(Math.Clamp(LyricsAvoidPadding, 0, 0.2) * Math.Min(_designW, _designH));
        var cb = Inflate(character, pad);
        if (cb.Width >= _designW * 0.92f && cb.Height >= _designH * 0.92f) return state;
        if (text.Width >= _designW * 0.9f && text.Height >= _designH * 0.9f) return state;
        if (!Intersects(text, cb)) return state;

        var left = Math.Clamp(cb.X, 0, _designW);
        var right = Math.Clamp(cb.Right, 0, _designW);
        var top = Math.Clamp(cb.Y, 0, _designH);
        var bottom = Math.Clamp(cb.Bottom, 0, _designH);

        var candidates = new[]
        {
            new Rect(0, 0, left, _designH),
            new Rect(right, 0, _designW - right, _designH),
            new Rect(0, 0, _designW, top),
            new Rect(0, bottom, _designW, _designH - bottom),
            new Rect(0, 0, left, top),
            new Rect(right, 0, _designW - right, top),
            new Rect(0, bottom, left, _designH - bottom),
            new Rect(right, bottom, _designW - right, _designH - bottom),
        };

        var best = state;
        var bestFit = 0.0;
        foreach (var free in candidates)
        {
            if (free.Width < 40 || free.Height < 40) continue;

            var fit = Math.Min(
                free.Width / Math.Max(1.0, text.Width),
                free.Height / Math.Max(1.0, text.Height));
            if (fit <= bestFit) continue;

            bestFit = fit;
            best = FitInto(state, text, free, fit);
        }

        return bestFit > 0 ? best : state;
    }

    private PvLayerState FitInto(PvLayerState state, Rect text, Rect free, double fit)
    {
        var minScale = Math.Clamp(LyricsAvoidMinScale, 0.1, 1.0);
        var scale = Math.Clamp(fit, minScale, 1.0);

        var baseScale = Math.Max(0.01, state.Scale);
        var newScale = baseScale * scale;
        var cx = _designW / 2.0;
        var cy = _designH / 2.0;

        var textCx = text.X + text.Width / 2.0;
        var textCy = text.Y + text.Height / 2.0;
        var targetCx = free.X + free.Width / 2.0;
        var targetCy = free.Y + free.Height / 2.0;

        var p0x = cx + (textCx - cx - state.OffsetX * _designW) / baseScale;
        var p0y = cy + (textCy - cy - state.OffsetY * _designH) / baseScale;

        var offX = (targetCx - cx - newScale * (p0x - cx)) / _designW;
        var offY = (targetCy - cy - newScale * (p0y - cy)) / _designH;

        return state with { Scale = newScale, OffsetX = offX, OffsetY = offY };
    }

    private static Rect Inflate(Rect r, float pad) =>
        new(r.X - pad, r.Y - pad, r.Width + pad * 2, r.Height + pad * 2);

    private static bool Intersects(Rect a, Rect b) =>
        a.X < b.Right && a.Right > b.X && a.Y < b.Bottom && a.Bottom > b.Y;

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

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
        _transitionRunner.Dispose();
        ClearFxBitmapCache();
        foreach (var target in _layerTargets.Values) target.Dispose();
        _layerTargets.Clear();
        _stageSrv?.Dispose();
        _stageTarget?.Dispose();
        _transparentStageSrv?.Dispose();
        _transparentStageTarget?.Dispose();
        _probeTarget?.Dispose();
        Stage.Dispose();
        _fx.Dispose();
    }
}
