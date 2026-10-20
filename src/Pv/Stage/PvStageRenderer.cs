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

public sealed record PvVtsControl(bool Active, double X, double Y, double Size, double Rotation);

public sealed record PvSelectionInfo(
    Vector2 A, Vector2 B, Vector2 C, Vector2 D, Vector2 Center,
    Matrix3x2 Matrix, int DesignWidth, int DesignHeight, double FitWidth, double CaptureWidth)
{
    public Vector2 TopMiddle => (A + B) * 0.5f;

    public Vector2 RotationHandle
    {
        get
        {
            var up = A - D;
            var length = up.Length();
            if (length <= 1e-4f) return A;

            var offset = up / length * 26f;
            var outward = TopMiddle + offset;
            if (outward.X < 0 || outward.X > DesignWidth || outward.Y < 0 || outward.Y > DesignHeight)
                return TopMiddle - offset;
            return outward;
        }
    }
}

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

    private volatile string? _selectedLayerId;

    public string? SelectedLayerId
    {
        get => _selectedLayerId;
        set
        {
            _selectedLayerId = value;
            if (value is null) Volatile.Write(ref _selection, null);
        }
    }

    private PvSelectionInfo? _selection;

    public PvSelectionInfo? Selection => Volatile.Read(ref _selection);

    private PvVtsControl _vtsControl = new(false, 0, 0, 0, 0);

    public PvVtsControl VtsControl
    {
        get => Volatile.Read(ref _vtsControl);
        set => Volatile.Write(ref _vtsControl, value);
    }

    public (double Engine, double Fx, double Composite) LastTiming { get; private set; }

    public int EffectCount => Effects.Nodes.Count(n => n.Enabled);

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
        var d = _d2d.Draw;
        var frame = new PvFrameContext(_frameTime, Stage.Beat, Stage.Energy, _designW, _designH);

        Stage.PumpImages(frame);
        var images = Stage.LastImages;
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

        PvSelectionInfo? selection = null;
        var frameLayers = Stage.Scene.Layers;
        for (var li = 0; li < frameLayers.Count; li++)
        {
            var layer = frameLayers[li];
            if (!layer.Enabled) continue;
            if (!images.TryGetValue(layer.Id, out var image) || !image.HasContent) continue;

            var state = LayerState(layer);
            if (string.Equals(layer.Id, _selectedLayerId, StringComparison.Ordinal))
                selection = BuildSelection(layer, state, image);

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
        _selection = selection;
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
        if (selection is not null) DrawSelection(d, selection, previewRect);
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

    private PvSelectionInfo BuildSelection(PvLayerConfig layer, in PvLayerState state, PvLayerImage image)
    {
        var tf = PvParallax.Compute(_designW, _designH, layer, state, Stage.Camera,
            globalParallax: Stage.Scene.GlobalParallaxEnabled,
            antiBleed: Stage.Scene.AntiBleedGuardEnabled);
        var r = PvStageCompositor.FitRect(image, layer.Cover, _designW, _designH);
        var vts = VtsControl;
        if (vts.Active)
        {
            double designPerCapture = r.Width / Math.Max(1, image.Width);
            var center = new Vector2(
                (float)(r.X + r.Width / 2 + vts.X * designPerCapture),
                (float)(r.Y + r.Height / 2 + vts.Y * designPerCapture));
            double vtsScale = Math.Clamp(1 + vts.Size / 100.0, 0.1, 4.0);
            double radians = vts.Rotation * Math.PI / 180.0;
            float halfWidth = (float)(r.Width * 0.19 * vtsScale);
            float halfHeight = (float)(r.Height * 0.33 * vtsScale);
            var cos = (float)Math.Cos(radians);
            var sin = (float)Math.Sin(radians);

            Vector2 Corner(float sx, float sy)
            {
                float x = sx * halfWidth;
                float y = sy * halfHeight;
                var p = center + new Vector2(x * cos - y * sin, x * sin + y * cos);
                return Vector2.Transform(p, tf.Matrix);
            }

            return new PvSelectionInfo(
                Corner(-1, -1),
                Corner(1, -1),
                Corner(1, 1),
                Corner(-1, 1),
                Vector2.Transform(center, tf.Matrix),
                tf.Matrix, _designW, _designH,
                Math.Max(1, r.Width), Math.Max(1, image.Width));
        }

        Vector2 Map(float x, float y) => Vector2.Transform(new Vector2(x, y), tf.Matrix);

        return new PvSelectionInfo(
            Map(r.X, r.Y),
            Map(r.X + r.Width, r.Y),
            Map(r.X + r.Width, r.Y + r.Height),
            Map(r.X, r.Y + r.Height),
            Map(r.X + r.Width / 2, r.Y + r.Height / 2),
            tf.Matrix, _designW, _designH, Math.Max(1, r.Width), Math.Max(1, image.Width));
    }

    private void DrawSelection(Immediate2D d, PvSelectionInfo sel, Rect rect)
    {
        float sx = (float)(rect.Width / sel.DesignWidth);
        float sy = (float)(rect.Height / sel.DesignHeight);

        Vector2 Map(Vector2 p) => new((float)rect.X + p.X * sx, (float)rect.Y + p.Y * sy);

        var a = Map(sel.A);
        var b = Map(sel.B);
        var c = Map(sel.C);
        var e = Map(sel.D);

        var line = new Paint2D
        {
            Brush = new SolidBrush2D(new Color4(0.36f, 0.55f, 1f, 1f)),
            StrokeWidth = 1.5f,
        };
        d.DrawLine(a.X, a.Y, b.X, b.Y, line);
        d.DrawLine(b.X, b.Y, c.X, c.Y, line);
        d.DrawLine(c.X, c.Y, e.X, e.Y, line);
        d.DrawLine(e.X, e.Y, a.X, a.Y, line);

        var topMiddle = Map(sel.TopMiddle);
        var rotation = Map(sel.RotationHandle);
        d.DrawLine(topMiddle.X, topMiddle.Y, rotation.X, rotation.Y, line);

        var fill = new Paint2D { Brush = new SolidBrush2D(new Color4(1f, 1f, 1f, 1f)) };
        foreach (var p in new[] { a, b, c, e })
        {
            var handle = new Rect(p.X - 4f, p.Y - 4f, 8f, 8f);
            d.FillRect(handle, fill);
            d.StrokeRect(handle, line);
        }

        d.FillCircle(rotation.X, rotation.Y, 4.5f, fill);
        d.StrokeCircle(rotation.X, rotation.Y, 4.5f, line);
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

    private PvLayerState LayerState(PvLayerConfig layer) =>
        Stage.Scene.Frame.TryGetLayer(layer.Id, out var frameState)
            ? frameState
            : PvLayerState.From(layer);

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
        Stage.Dispose();
        _fx.Dispose();
    }
}
