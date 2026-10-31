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

public sealed record PvVtsControl(bool Active, double X, double Y, double Size, double Rotation,
    double HullMinX = 0, double HullMaxX = 0, double HullMinY = 0, double HullMaxY = 0, bool HasHull = false);

public sealed record PvSelectionInfo(
    Vector2 A, Vector2 B, Vector2 C, Vector2 D, Vector2 Center,
    Matrix3x2 Matrix, int DesignWidth, int DesignHeight, double FitWidth, double FitHeight)
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

public readonly record struct PvHitQuad(
    string Id, Vector2 A, Vector2 B, Vector2 C, Vector2 D, Vector2 Center,
    Matrix3x2 Matrix, float FitWidth, float FitHeight)
{
    public byte[]? Alpha { get; init; }

    public int AlphaWidth { get; init; }

    public int AlphaHeight { get; init; }

    public float FitX { get; init; }

    public float FitY { get; init; }

    public float FitW { get; init; }

    public float FitH { get; init; }

    public bool Contains(Vector2 p)
    {
        float c1 = Cross(A, B, p);
        float c2 = Cross(B, C, p);
        float c3 = Cross(C, D, p);
        float c4 = Cross(D, A, p);
        return (c1 >= 0 && c2 >= 0 && c3 >= 0 && c4 >= 0)
               || (c1 <= 0 && c2 <= 0 && c3 <= 0 && c4 <= 0);
    }

    public bool ContainsOpaque(Vector2 p)
    {
        if (!Contains(p)) return false;
        if (Alpha is not { Length: > 0 } alpha || AlphaWidth <= 0 || AlphaHeight <= 0) return true;
        if (!Matrix3x2.Invert(Matrix, out var inverse)) return true;

        var local = Vector2.Transform(p, inverse);
        float u = (local.X - FitX) / Math.Max(0.001f, FitW);
        float v = (local.Y - FitY) / Math.Max(0.001f, FitH);
        if (u < 0 || v < 0 || u > 1 || v > 1) return false;

        int ax = Math.Clamp((int)(u * (AlphaWidth - 1)), 0, AlphaWidth - 1);
        int ay = Math.Clamp((int)(v * (AlphaHeight - 1)), 0, AlphaHeight - 1);
        return alpha[ay * AlphaWidth + ax] > 8;
    }

    private static float Cross(Vector2 a, Vector2 b, Vector2 p) =>
        (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
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

    public float AudioBass { get; set; }

    public float AudioMid { get; set; }

    public float AudioHigh { get; set; }

    public float AudioRms { get; set; }

    public TransitionTrack Transitions { get; } = new();

    public void SetDesignSegments(IEnumerable<DesignSegment>? segments)
    {
        Transitions.LoadFrom(segments);
    }

    private volatile EffectGraph _effects;

    private EffectGraph _frameEffects;

    public EffectGraph Effects => _effects;

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

    private volatile PvHitQuad[] _hits = Array.Empty<PvHitQuad>();

    private readonly List<PvHitQuad> _hitScratch = new();

    public PvSelectionInfo? Selection => Volatile.Read(ref _selection);

    public PvHitQuad? Pick(Vector2 designPoint)
    {
        var hits = _hits;
        for (int i = hits.Length - 1; i >= 0; i--)
            if (hits[i].ContainsOpaque(designPoint)) return hits[i];
        return null;
    }

    private double _hullHalfX;
    private double _hullHalfY;
    private double _hullCenterX;
    private double _hullCenterY;
    private bool _hullCenterValid;

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
        d.Clear(BackdropClear);

        PvSelectionInfo? selection = null;
        _hitScratch.Clear();
        var frameLayers = Stage.Scene.Layers;
        for (var li = 0; li < frameLayers.Count; li++)
        {
            var layer = frameLayers[li];
            if (!layer.Enabled) continue;
            if (!images.TryGetValue(layer.Id, out var image) || !image.HasContent) continue;

            var state = LayerState(layer);
            if (!layer.Locked && layer.Kind != PvLayerKind.Subtitle)
            {
                if (!LegacyPvProvider.IsLayerId(layer.Id))
                    _hitScratch.Add(BuildHit(layer, state, image));
                if (string.Equals(layer.Id, _selectedLayerId, StringComparison.Ordinal))
                    selection = BuildSelection(layer, state, image);
            }

            if (layer.Topmost == true) continue;

            if (!_frameEffects.HasEffectsFor(layer.Id))
            {
                compositor.CompositeLayer(Stage.Scene, layer, state, image, Stage.Camera, _designW, _designH);
                continue;
            }

            d.End();
            DrawLayerWithEffects(d, compositor, layer, state, image, frame, returns, full);
        }

        _hits = [.. _hitScratch];

        if (!SpoutTransparent) compositor.DrawBrighten(Stage.Camera, _designW, _designH);

        d.End();
        _selection = selection;

        if (_frameEffects.HasEffectsFor("", EffectStage.Composite))
        {
            var compositePasses = _frameEffects.BuildRenderGraph(_fx.Shaders, (float)frame.Time,
                _designW, _designH, "", AudioBass, AudioMid, AudioHigh, AudioRms, EffectStage.Composite);
            if (compositePasses.Count > 0)
            {
                var composite = _fx.RunGraph(compositePasses, _stageSrv!, _designW, _designH);
                d.SetTarget(_stageTarget.Bitmap, _designW, _designH);
                d.Begin();
                d.Clear(BackdropClear);
                d.DrawBitmap(BitmapFor(composite), full);
                d.End();
                returns.Add(composite);
            }
        }

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
            ? _frameEffects.BuildRenderGraph(_fx.Shaders, (float)frame.Time, _designW, _designH,
                "", AudioBass, AudioMid, AudioHigh, AudioRms)
            : null;
        if (passes is { Count: > 0 })
            fxOut = _fx.RunGraph(passes, _stageSrv!, _designW, _designH);

        var stageSource = fxOut is null ? _stageTarget!.Bitmap : BitmapFor(fxOut);

        var hasTopmost = false;
        for (var li = 0; li < frameLayers.Count; li++)
        {
            var layer = frameLayers[li];
            if (layer.Topmost != true || !layer.Enabled) continue;
            if (!images.TryGetValue(layer.Id, out var probe) || !probe.HasContent) continue;
            hasTopmost = true;
            break;
        }

        if (hasTopmost)
        {
            if (fxOut is not null)
            {
                d.SetTarget(_stageTarget!.Bitmap, _designW, _designH);
                d.Begin();
                d.Clear(BackdropClear);
                d.DrawBitmap(stageSource, full);
                d.End();
            }

            for (var li = 0; li < frameLayers.Count; li++)
            {
                var layer = frameLayers[li];
                if (layer.Topmost != true || !layer.Enabled) continue;
                if (!images.TryGetValue(layer.Id, out var image) || !image.HasContent) continue;

                var state = LayerState(layer);
                d.End();

                if (!_frameEffects.HasEffectsFor(layer.Id))
                {
                    d.SetTarget(_stageTarget!.Bitmap, _designW, _designH);
                    d.Begin();
                    compositor.CompositeLayer(Stage.Scene, layer, state, image, Stage.Camera, _designW, _designH);
                    d.End();
                    continue;
                }

                DrawLayerWithEffects(d, compositor, layer, state, image, frame, returns, full, _stageTarget!.Bitmap);
            }

            d.End();
            foreach (var entry in returns) _fx.Pool.Return(entry);
            returns.Clear();
            stageSource = _stageTarget!.Bitmap;
        }

        var t2 = System.Diagnostics.Stopwatch.GetTimestamp();

        d.SetTarget(target, width, height);
        d.Begin();
        d.Clear(new Color4(0f, 0f, 0f, 1f));
        var previewRect = Letterbox(_designW, _designH, width, height);
        DrawStage(d, stageSource, previewRect);
        if (selection is not null) DrawSelection(d, selection, previewRect);
        d.End();

        if (_broadcast is { IsActive: true, Bitmap: { } bbmp } b)
        {
            var broadcastRect = new Rect(0, 0, b.Width, b.Height);
            var src = stageSource;
            d.SetTarget(bbmp, b.Width, b.Height);
            d.Begin();
            d.Clear(SpoutTransparent ? new Color4(0f, 0f, 0f, 0f) : new Color4(0f, 0f, 0f, 1f));
            DrawStage(d, src, broadcastRect);
            d.End();

            b.PresentFrame();
        }

        if (!_freezeLast && fxOut is not null) _fx.Pool.Return(fxOut);
        LastTiming = (LastTiming.Engine, Elapsed(t1), Elapsed(t2));
    }

    private void DrawLayerWithEffects(Immediate2D d, PvStageCompositor compositor, PvLayerConfig layer,
        in PvLayerState state, PvLayerImage image, in PvFrameContext frame,
        List<GpuTexturePool.Entry> returns, Rect full, ID2D1Bitmap1? dest = null)
    {
        var target = LayerTargetFor(layer.Id);

        d.SetTarget(target.Surface.Bitmap, _designW, _designH);
        d.Begin();
        d.Clear(new Color4(0f, 0f, 0f, 0f));
        compositor.CompositeLayer(Stage.Scene, layer, state, image, Stage.Camera, _designW, _designH);
        d.End();

        var graph = _frameEffects.BuildRenderGraph(_fx.Shaders, (float)frame.Time, _designW, _designH,
            layer.Id, AudioBass, AudioMid, AudioHigh, AudioRms);
        var output = graph.Count > 0 ? _fx.RunGraph(graph, target.Srv, _designW, _designH) : null;

        d.SetTarget(dest ?? _stageTarget!.Bitmap, _designW, _designH);
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

    private PvHitQuad BuildHit(PvLayerConfig layer, in PvLayerState state, PvLayerImage image)
    {
        var tf = PvParallax.Compute(_designW, _designH, layer, state, Stage.Camera,
            globalParallax: Stage.Scene.GlobalParallaxEnabled,
            antiBleed: Stage.Scene.AntiBleedGuardEnabled,
            parallaxOverride: PvParallax.OverrideFor(Stage.Scene, layer));
        var r = PvStageCompositor.FitRect(image, layer.Cover, _designW, _designH);

        if (Stage.Scene.IsCharacterLayer(layer)
            && VtsControl is { HasHull: true } vts
            && vts.HullMaxX > vts.HullMinX
            && vts.HullMaxY > vts.HullMinY)
        {
            double halfWidth = r.Width / 2.0;
            double halfHeight = r.Height / 2.0;
            double centerX = (vts.HullMinX + vts.HullMaxX) / 2;
            double centerY = (vts.HullMinY + vts.HullMaxY) / 2;
            double halfX = (vts.HullMaxX - vts.HullMinX) / 2 * 1.03;
            double halfY = (vts.HullMaxY - vts.HullMinY) / 2 * 1.03;

            var center = new Vector2(
                (float)(r.X + r.Width / 2 + centerX * halfWidth),
                (float)(r.Y + r.Height / 2 - centerY * halfHeight));
            float boxHalfX = (float)(halfX * halfWidth);
            float boxHalfY = (float)(halfY * halfHeight);

            Vector2 Corner(float sx, float sy) => Vector2.Transform(
                new Vector2(center.X + sx * boxHalfX, center.Y + sy * boxHalfY), tf.Matrix);

            return new PvHitQuad(layer.Id, Corner(-1, -1), Corner(1, -1), Corner(1, 1), Corner(-1, 1),
                Vector2.Transform(center, tf.Matrix), tf.Matrix,
                Math.Max(1, r.Width), Math.Max(1, r.Height));
        }

        Vector2 Map(float x, float y) => Vector2.Transform(new Vector2(x, y), tf.Matrix);

        return new PvHitQuad(layer.Id,
            Map(r.X, r.Y),
            Map(r.X + r.Width, r.Y),
            Map(r.X + r.Width, r.Y + r.Height),
            Map(r.X, r.Y + r.Height),
            Map(r.X + r.Width / 2, r.Y + r.Height / 2),
            tf.Matrix, Math.Max(1, r.Width), Math.Max(1, r.Height))
        {
            Alpha = image.Alpha,
            AlphaWidth = image.AlphaWidth,
            AlphaHeight = image.AlphaHeight,
            FitX = (float)r.X,
            FitY = (float)r.Y,
            FitW = (float)r.Width,
            FitH = (float)r.Height,
        };
    }

    private PvSelectionInfo BuildSelection(PvLayerConfig layer, in PvLayerState state, PvLayerImage image)
    {
        var tf = PvParallax.Compute(_designW, _designH, layer, state, Stage.Camera,
            globalParallax: Stage.Scene.GlobalParallaxEnabled,
            antiBleed: Stage.Scene.AntiBleedGuardEnabled,
            parallaxOverride: PvParallax.OverrideFor(Stage.Scene, layer));
        var r = PvStageCompositor.FitRect(image, layer.Cover, _designW, _designH);
        var vts = VtsControl;
        if (vts.Active)
        {
            double halfWidth = r.Width / 2.0;
            double halfHeight = r.Height / 2.0;
            bool hasHull = vts.HasHull
                           && vts.HullMaxX > vts.HullMinX
                           && vts.HullMaxY > vts.HullMinY;

            double targetCenterX, targetCenterY, targetHalfX, targetHalfY;
            if (hasHull)
            {
                targetCenterX = (vts.HullMinX + vts.HullMaxX) / 2;
                targetCenterY = (vts.HullMinY + vts.HullMaxY) / 2;
                targetHalfX = (vts.HullMaxX - vts.HullMinX) / 2 * 1.03;
                targetHalfY = (vts.HullMaxY - vts.HullMinY) / 2 * 1.03;
            }
            else
            {
                double vtsScale = Math.Clamp(1 + vts.Size / 100.0, 0.1, 4.0);
                targetCenterX = vts.X;
                targetCenterY = vts.Y;
                targetHalfX = 0.19 * vtsScale;
                targetHalfY = 0.33 * vtsScale;
            }

            if (!_hullCenterValid)
            {
                _hullCenterX = targetCenterX;
                _hullCenterY = targetCenterY;
                _hullHalfX = targetHalfX;
                _hullHalfY = targetHalfY;
                _hullCenterValid = true;
            }
            else
            {
                _hullCenterX += (targetCenterX - _hullCenterX) * 0.35;
                _hullCenterY += (targetCenterY - _hullCenterY) * 0.35;
                _hullHalfX += (targetHalfX - _hullHalfX) * 0.35;
                _hullHalfY += (targetHalfY - _hullHalfY) * 0.35;
            }

            var center = new Vector2(
                (float)(r.X + r.Width / 2 + _hullCenterX * halfWidth),
                (float)(r.Y + r.Height / 2 - _hullCenterY * halfHeight));

            float boxHalfX = (float)(_hullHalfX * halfWidth);
            float boxHalfY = (float)(_hullHalfY * halfHeight);

            Vector2 Corner(float sx, float sy) => Vector2.Transform(
                new Vector2(center.X + sx * boxHalfX, center.Y + sy * boxHalfY), tf.Matrix);

            return new PvSelectionInfo(
                Corner(-1, -1),
                Corner(1, -1),
                Corner(1, 1),
                Corner(-1, 1),
                Vector2.Transform(center, tf.Matrix),
                tf.Matrix, _designW, _designH, Math.Max(1, r.Width), Math.Max(1, r.Height));
        }

        _hullHalfX = 0;
        _hullHalfY = 0;
        _hullCenterValid = false;

        Vector2 Map(float x, float y) => Vector2.Transform(new Vector2(x, y), tf.Matrix);

        return new PvSelectionInfo(
            Map(r.X, r.Y),
            Map(r.X + r.Width, r.Y),
            Map(r.X + r.Width, r.Y + r.Height),
            Map(r.X, r.Y + r.Height),
            Map(r.X + r.Width / 2, r.Y + r.Height / 2),
            tf.Matrix, _designW, _designH, Math.Max(1, r.Width), Math.Max(1, r.Height));
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

        d.FillCircle(rotation.X, rotation.Y, 6.5f, fill);
        d.StrokeCircle(rotation.X, rotation.Y, 6.5f, line);
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

    private Color4 BackdropClear => SpoutTransparent
        ? new Color4(0f, 0f, 0f, 0f)
        : new Color4(0f, 0f, 0f, 1f);

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
            : SeedFor(seg.Start);

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

    public static int SeedFor(double start) =>
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
