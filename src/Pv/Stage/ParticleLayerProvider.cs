using JizuraLive.Render.Draw;
using JizuraLive.Render.Gpu;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace JizuraLive.Pv;

public sealed class ParticleLayerProvider : IPvLayerProvider, IDisposable
{
    private const float PetalPx = 26f;
    private const float StarPx = 22f;
    private const float BokehPx = 60f;

    private readonly D3DDevice _gpu;
    private readonly D2DHost _host;
    private readonly PvParticleConfig _cfg;
    private readonly ParticleState[] _buffer = new ParticleState[ParticleField.MaxParticles];
    private readonly Paint2D _paint = new();
    private readonly Path2D _petal;
    private readonly Path2D _star;

    private OffscreenTarget? _target;
    private Color4 _brushColor;
    private string? _brushHex;
    private bool _brushReady;
    private double _renderedTime = double.NaN;
    private int _w, _h;
    private long _version = 1;

    public string Id { get; }
    public long Version => _version;
    public int Width => _w;
    public int Height => _h;
    public int DesignWidth { get; }
    public int DesignHeight { get; }

    public ParticleLayerProvider(string id, D3DDevice gpu, D2DHost host, PvParticleConfig cfg,
        int designWidth, int designHeight)
    {
        Id = id;
        _gpu = gpu;
        _host = host;
        _cfg = cfg;
        DesignWidth = designWidth;
        DesignHeight = designHeight;
        _petal = BuildPetal(host.Factory);
        _star = BuildStar(host.Factory);
    }

    public PvLayerImage Pump(in PvFrameContext ctx)
    {
        if (ctx.Time != _renderedTime) Render(ctx.Time);
        return _target is null ? PvLayerImage.Empty : new PvLayerImage(_target.Bitmap, null, _w, _h);
    }

    public void Render(double time)
    {
        var w = Math.Max(16, DesignWidth);
        var h = Math.Max(16, DesignHeight);
        if (_target is null || w != _w || h != _h)
        {
            _target?.Dispose();
            _target = OffscreenTarget.Create(_gpu, _host.Ctx, w, h, sampleable: true);
            _w = w;
            _h = h;
            _version++;
        }

        var count = ParticleField.Compute(_cfg, time, w, h, _buffer);
        var ambient = Math.Min(ParticleField.AmbientCount(_cfg), count);

        if (!_brushReady || !ReferenceEquals(_cfg.Color, _brushHex))
        {
            _brushReady = true;
            _brushHex = _cfg.Color;
            var color = PvScene.ParseHex(_cfg.Color);
            if (!SameColor(color, _brushColor))
            {
                _brushColor = color;
                _paint.Brush = new SolidBrush2D(color);
            }
        }

        var kind = _cfg.Kind;
        var sparkle = string.Equals(kind, "sparkle", StringComparison.Ordinal);
        var bokeh = string.Equals(kind, "bokeh", StringComparison.Ordinal);

        var d = _host.Draw;
        d.SetTarget(_target.Bitmap, w, h);
        d.Begin();
        d.Clear(new Color4(0f, 0f, 0f, 0f));

        for (var i = 0; i < count; i++)
        {
            var s = _buffer[i];
            if (s.Alpha <= 0.002f) continue;

            _paint.Alpha = Math.Clamp(s.Alpha, 0f, 1f);
            var px = s.Scale * BaseSize(i < ambient, sparkle, bokeh);

            d.Save();
            d.Scale(px, px);
            d.Rotate(s.Rotation);
            d.Translate(s.X, s.Y);

            if (i < ambient)
            {
                if (sparkle) DrawStar(d);
                else if (bokeh) DrawBokeh(d);
                else d.FillPath(_petal, _paint);
            }
            else
            {
                DrawStar(d);
            }

            d.Restore();
        }

        d.End();
        _renderedTime = time;
    }

    public ReadbackImage? ReadbackTarget() => _target?.Readback();

    private void DrawStar(Immediate2D d)
    {
        var alpha = _paint.Alpha;
        _paint.Alpha = alpha * 0.35f;
        d.FillCircle(0f, 0f, 0.32f, _paint);
        _paint.Alpha = alpha;
        d.FillPath(_star, _paint);
    }

    private void DrawBokeh(Immediate2D d)
    {
        var alpha = _paint.Alpha;
        _paint.Alpha = alpha * 0.35f;
        d.FillCircle(0f, 0f, 0.25f, _paint);
        _paint.Alpha = alpha * 0.24f;
        d.FillCircle(0f, 0f, 0.45f, _paint);
        _paint.Alpha = alpha * 0.16f;
        d.FillCircle(0f, 0f, 0.70f, _paint);
        _paint.Alpha = alpha * 0.10f;
        d.FillCircle(0f, 0f, 1.00f, _paint);
        _paint.Alpha = alpha;
    }

    private static float BaseSize(bool ambient, bool sparkle, bool bokeh) =>
        ambient ? sparkle ? StarPx : bokeh ? BokehPx : PetalPx : StarPx;

    private static Path2D BuildPetal(ID2D1Factory factory)
    {
        var p = new Path2D(factory);
        p.MoveTo(0f, -1f);
        p.CubicTo(0.62f, -0.74f, 0.78f, 0.22f, 0.20f, 0.88f);
        p.CubicTo(0.10f, 0.70f, -0.10f, 0.70f, -0.20f, 0.88f);
        p.CubicTo(-0.78f, 0.22f, -0.62f, -0.74f, 0f, -1f);
        return p.Close();
    }

    private static Path2D BuildStar(ID2D1Factory factory)
    {
        var p = new Path2D(factory);
        p.MoveTo(0f, -1f);
        p.QuadTo(0.16f, -0.16f, 1f, 0f);
        p.QuadTo(0.16f, 0.16f, 0f, 1f);
        p.QuadTo(-0.16f, 0.16f, -1f, 0f);
        p.QuadTo(-0.16f, -0.16f, 0f, -1f);
        return p.Close();
    }

    private static bool SameColor(Color4 a, Color4 b) =>
        a.R == b.R && a.G == b.G && a.B == b.B && a.A == b.A;

    public void Dispose()
    {
        _target?.Dispose();
        _petal.Dispose();
        _star.Dispose();
    }
}
