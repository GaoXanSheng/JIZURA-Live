using System.Numerics;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Gpu;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace JizuraLive.Pv;

public static class DecorCatalog
{
    public const string Petals = "petals";
    public const string Momiji = "momiji";
    public const string Confetti = "confetti";
    public const string Dandelion = "dandelion";
    public const string Hanabi = "hanabi";
    public const string Fireflies = "fireflies";
    public const string Snow = "snow";
    public const string Bokeh = "bokeh";
    public const string RainStreaks = "rainStreaks";
    public const string StarField = "starField";
    public const string Twinkle = "twinkle";
    public const string RisingParticles = "risingParticles";
    public const string LightLeak = "lightLeak";
    public const string Smoke = "smoke";

    public static readonly string[] All =
    [
        Petals, Momiji, Confetti, Dandelion, Hanabi, Fireflies, Snow, Bokeh,
        RainStreaks, StarField, Twinkle, RisingParticles, LightLeak, Smoke,
    ];

    public static string Label(string kind) => kind switch
    {
        Petals => "花瓣",
        Momiji => "红叶",
        Confetti => "紙吹雪",
        Dandelion => "绵毛",
        Hanabi => "烟火",
        Fireflies => "萤火",
        Snow => "雪",
        Bokeh => "光斑",
        RainStreaks => "雨丝",
        StarField => "星空",
        Twinkle => "星芒",
        RisingParticles => "上升粒子",
        LightLeak => "光漏",
        Smoke => "烟",
        _ => kind,
    };

    public static string DefaultColor(string kind) => kind switch
    {
        Petals => "#ffc0d8",
        Momiji => "#ff8a4a",
        Confetti => "#ffd24a",
        Dandelion => "#fff6e0",
        Hanabi => "#ffb35c",
        Fireflies => "#c8ff8a",
        Snow => "#ffffff",
        Bokeh => "#bfe3ff",
        RainStreaks => "#a8c8ff",
        StarField => "#ffffff",
        Twinkle => "#fff4c2",
        RisingParticles => "#8ad4ff",
        LightLeak => "#ff9a5c",
        Smoke => "#c8c8d8",
        _ => "#ffffff",
    };

    public static string DefaultAccent(string kind) => kind switch
    {
        Petals => "#ffffff",
        Momiji => "#ffd0a0",
        Confetti => "#8ad4ff",
        Dandelion => "#ffe9a8",
        Hanabi => "#8ad4ff",
        Fireflies => "#e8ffb0",
        Snow => "#d8e8ff",
        Bokeh => "#ffd9f2",
        RainStreaks => "#ffffff",
        StarField => "#8ad4ff",
        Twinkle => "#8ad4ff",
        RisingParticles => "#ffffff",
        LightLeak => "#8ad4ff",
        Smoke => "#ffffff",
        _ => "#ffffff",
    };

    public static bool IsKnown(string kind)
    {
        foreach (var k in All)
            if (string.Equals(k, kind, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static DecorConfig CreateConfig(string kind) => new()
    {
        Kinds = { kind },
        Color = DefaultColor(kind),
        AccentColor = DefaultAccent(kind),
    };
}

public sealed class DecorLayerProvider : IPvLayerProvider, IDisposable
{
    private const double FadeInSec = 0.45;
    private const double FadeOutSec = 0.7;
    private const double BurstLife = 1.2;
    private const int BurstParticles = 40;
    private const int MaxParticles = 640;

    private readonly D3DDevice _gpu;
    private readonly D2DHost _host;
    private readonly DecorConfig _cfg;
    private readonly Paint2D _paint = new();
    private Brush2D _brushMain = new SolidBrush2D(new Color4(1f, 1f, 1f));
    private Brush2D _brushAlt = new SolidBrush2D(new Color4(1f, 1f, 1f));
    private Color4 _colorMain = new(1f, 1f, 1f);
    private Color4 _colorAlt = new(1f, 1f, 1f);
    private readonly Path2D _petal;
    private readonly Path2D _maple;
    private readonly Path2D _star;

    private readonly string[] _kindKeys;

    private string? _seenColor;
    private string? _seenAccent;

    private OffscreenTarget? _target;
    private double _renderedTime = double.NaN;
    private int _w, _h;
    private long _version = 1;

    public string Id { get; }
    public long Version => _version;
    public int DesignWidth { get; }
    public int DesignHeight { get; }

    public DecorLayerProvider(string id, D3DDevice gpu, D2DHost host, DecorConfig cfg,
        int designWidth, int designHeight)
    {
        Id = id;
        _gpu = gpu;
        _host = host;
        _cfg = cfg;
        DesignWidth = designWidth;
        DesignHeight = designHeight;

        var list = new List<string>();
        foreach (var kind in cfg.Kinds)
        {
            if (string.IsNullOrWhiteSpace(kind) || !DecorCatalog.IsKnown(kind)) continue;
            list.Add(kind);
        }
        if (list.Count == 0) list.Add(DecorCatalog.Petals);

        _kindKeys = list.ToArray();
        ReloadColors(force: true);

        _petal = BuildPetal(host.Factory);
        _maple = BuildMaple(host.Factory);
        _star = BuildGlint(host.Factory);
    }

    public void ReloadColors(bool force = false)
    {
        if (!force
            && ReferenceEquals(_cfg.Color, _seenColor)
            && ReferenceEquals(_cfg.AccentColor, _seenAccent))
            return;

        _seenColor = _cfg.Color;
        _seenAccent = _cfg.AccentColor;

        var main = PvScene.ParseHex(_cfg.Color);
        var accent = PvScene.ParseHex(_cfg.AccentColor);

        _colorMain = main;
        _colorAlt = accent;
        _brushMain = new SolidBrush2D(main);
        _brushAlt = new SolidBrush2D(accent);

        _version++;
    }

    public PvLayerImage Pump(in PvFrameContext ctx)
    {
        if (ctx.Time != _renderedTime) Render(ctx.Time);
        return _target is null ? PvLayerImage.Empty : new PvLayerImage(_target.Bitmap, null, _w, _h);
    }

    public void Render(double time)
    {
        if (!double.IsFinite(time)) time = 0;

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

        ReloadColors();

        var d = _host.Draw;
        d.SetTarget(_target.Bitmap, w, h);
        d.Begin();
        d.Clear(new Color4(0f, 0f, 0f, 0f));

        var life = LifeGate(time);
        if (life > 0.003f)
        {
            var u = Math.Min(w, h) / 1080.0;
            var speed = Finite(_cfg.Speed, 1.0, 0.2, 3.0);
            var size = Finite(_cfg.Size, 1.0, 0.3, 3.0);
            var seed = _cfg.Seed;
            var count = AmbientCount();

            for (var k = 0; k < _kindKeys.Length; k++)
            {
                var share = k == _kindKeys.Length - 1
                    ? count - (count / _kindKeys.Length) * (_kindKeys.Length - 1)
                    : count / _kindKeys.Length;
                if (share <= 0) continue;
                DrawKind(d, _kindKeys[k], share, time, w, h, u, speed, size, seed, life);
            }

            DrawBursts(d, time, w, h, size, seed, life);
        }

        d.End();
        _renderedTime = time;
    }

    private float LifeGate(double time)
    {
        var first = double.MaxValue;
        var last = double.MinValue;
        var bursting = false;

        foreach (var b in _cfg.Bursts)
        {
            if (!double.IsFinite(b)) continue;
            if (b < first) first = b;
            if (b > last) last = b;
            var age = time - b;
            if (age >= 0 && age < BurstLife) bursting = true;
        }

        var hasBursts = first < double.MaxValue;
        if (hasBursts)
        {
            if (!bursting || time < first || time > last + BurstLife) return 0f;

            var fade = Math.Clamp((time - first) / FadeInSec, 0, 1)
                       * Math.Clamp((last + BurstLife - time) / FadeOutSec, 0, 1);
            return (float)Math.Clamp(fade, 0, 1);
        }

        return 1f;
    }

    private int AmbientCount()
    {
        var density = _cfg.Density;
        if (!double.IsFinite(density)) return 0;
        var per = (int)Math.Round(Math.Clamp(density, 0, 400)) * _kindKeys.Length;
        return (int)Math.Clamp(per, 0, MaxParticles);
    }

    private void DrawBursts(Immediate2D d, double time, int w, int h, double size,
        int seed, float life)
    {
        var bursts = _cfg.Bursts;
        for (var b = 0; b < bursts.Count; b++)
        {
            var age = time - bursts[b];
            if (!(age >= 0) || age >= BurstLife) continue;

            var progress = age / BurstLife;
            var ease = 1 - Math.Pow(1 - progress, 3);
            var reach = 0.45 * Math.Sqrt((double)w * w + (double)h * h) * ease;

            for (var k = 0; k < BurstParticles; k++)
            {
                var key = b * BurstParticles + k;
                var angle = (k / (double)BurstParticles + Unit(seed, key, 11) * 0.12) * Math.Tau;
                var dist = reach * (0.45 + 0.55 * Unit(seed, key, 12));
                var x = (float)(w * 0.5 + Math.Cos(angle) * dist);
                var y = (float)(h * 0.5 + Math.Sin(angle) * dist);
                var fade = (float)Math.Pow(1 - progress, 1.5);
                var px = (float)(size * (0.7 + 0.6 * Unit(seed, key, 14)) * (1 - 0.35 * progress));

                var rot = (float)(Unit(seed, key, 13) * Math.Tau + age * 120.0 * Math.PI / 180.0);
                var avoid = CenterAvoid(x, y, w, h);
                if (avoid <= 0.02f) continue;

                _paint.Alpha = Math.Clamp(fade * life * avoid, 0f, 1f);
                PushTransform(d, x, y, px, rot);
                d.FillPath(_star, _paint);
                d.Restore();
            }
        }
    }

    private void DrawKind(Immediate2D d, string kind, int count, double time,
        int w, int h, double u, double speed, double size, int seed, float life)
    {
        switch (kind)
        {
            case DecorCatalog.Petals: DrawPetals(d, count, time, w, h, u, speed, size, seed, life, momiji: false); break;
            case DecorCatalog.Momiji: DrawPetals(d, count, time, w, h, u, speed, size, seed, life, momiji: true); break;
            case DecorCatalog.Confetti: DrawConfetti(d, count, time, w, h, u, speed, size, seed, life); break;
            case DecorCatalog.Dandelion: DrawDandelion(d, count, time, w, h, u, speed, size, seed, life); break;
            case DecorCatalog.Hanabi: DrawHanabi(d, time, w, h, u, speed, size, seed, life); break;
            case DecorCatalog.Fireflies: DrawFireflies(d, count, time, w, h, u, speed, size, seed, life); break;
            case DecorCatalog.Snow: DrawSnow(d, count, time, w, h, u, speed, size, seed, life); break;
            case DecorCatalog.Bokeh: DrawBokeh(d, count, time, w, h, u, speed, size, seed, life); break;
            case DecorCatalog.RainStreaks: DrawRainStreaks(d, count, time, w, h, u, speed, size, seed, life); break;
            case DecorCatalog.StarField: DrawStarField(d, count, time, w, h, u, speed, size, seed, life); break;
            case DecorCatalog.Twinkle: DrawTwinkle(d, count, time, w, h, u, speed, size, seed, life); break;
            case DecorCatalog.RisingParticles: DrawRising(d, count, time, w, h, u, speed, size, seed, life); break;
            case DecorCatalog.LightLeak: DrawLightLeak(d, time, w, h, speed, size, seed, life); break;
            case DecorCatalog.Smoke: DrawSmoke(d, time, w, h, u, speed, seed, life); break;
        }
    }

    private void DrawPetals(Immediate2D d, int count, double time, int w, int h, double u,
        double speed, double size, int seed, float life, bool momiji)
    {
        var n = Math.Min(momiji ? 30 : 34, count);
        var dir = Unit(seed, 0, 20) < 0.5 ? 1.0 : -1.0;
        for (var i = 0; i < n; i++)
        {
            var q = OutCubic(Clamp01((time - Unit(seed, i, 1) * 0.4) / 0.35));
            if (q <= 0) continue;

            var vy = (40 + Unit(seed, i, 2) * 60) * u * speed;
            var vx = dir * (30 + Unit(seed, i, 3) * 50) * u * speed;
            var x = Wrap(Unit(seed, i, 4) * w + vx * time
                + Math.Sin(time * (0.8 + Unit(seed, i, 5)) + Unit(seed, i, 6) * 6) * 30 * u, -60 * u, w + 120 * u);
            var y = Wrap(Unit(seed, i, 7) * h + vy * time, -60 * u, h + 120 * u);

            var avoid = CenterAvoid((float)x, (float)y, w, h);
            if (avoid <= 0.5f) continue;

            var l = (momiji ? 40 + Unit(seed, i, 8) * 26 : 24 + Unit(seed, i, 8) * 20) * u * q * size;
            var rot = Unit(seed, i, 9) * Math.Tau + time * (Unit(seed, i, 10) - 0.5) * 3;
            var flip = Math.Max(0.2, Math.Abs(Math.Cos(time * (1.2 + Unit(seed, i, 11) * 2) + Unit(seed, i, 12) * 6)));
            var useMain = Unit(seed, i, 13) < 0.7;

            _paint.Alpha = Math.Clamp(0.9f * life * avoid, 0f, 1f);
            _paint.Brush = useMain ? _brushMain : _brushAlt;
            PushTransform(d, (float)x, (float)y, (float)(l / 26.0), (float)rot);
            d.Save();
            d.Scale((float)flip, 1f);
            d.FillPath(momiji ? _maple : _petal, _paint);
            d.Restore();
            d.Restore();
        }
    }

    private void DrawConfetti(Immediate2D d, int count, double time, int w, int h, double u,
        double speed, double size, int seed, float life)
    {
        var n = Math.Min(56, count);
        for (var i = 0; i < n; i++)
        {
            var q = OutBack(Clamp01((time - Unit(seed, i, 1) * 0.35) / 0.3), 1.8);
            if (q <= 0) continue;

            var vy = (70 + Unit(seed, i, 2) * 110) * u * speed;
            var span = h + 80 * u;
            var y = Wrap(Unit(seed, i, 3) * h + vy * time, -40 * u, span);
            var x = Unit(seed, i, 4) * w
                + Math.Sin(time * (1 + Unit(seed, i, 5) * 1.8) + Unit(seed, i, 6) * 6) * (10 + Unit(seed, i, 7) * 30) * u;

            var rectW = (float)((11 + Unit(seed, i, 8) * 11) * u * q * size);
            if (rectW <= 0.5f) continue;

            var rectH = rectW * (0.4f + Unit(seed, i, 9) * 0.3f);
            var avoid = CenterAvoid((float)x, (float)y, w, h);
            if (avoid <= 0.02f) continue;

            var rot = (float)(Unit(seed, i, 10) * Math.Tau + time * (Unit(seed, i, 11) - 0.5f) * 8);
            var flip = MathF.Max(0.15f, MathF.Abs(MathF.Cos((float)time * (2f + Unit(seed, i, 12) * 4f) + Unit(seed, i, 13) * 6f)));

            _paint.Alpha = Math.Clamp(0.95f * life * avoid, 0f, 1f);
            _paint.Brush = i % 2 == 0 ? _brushMain : _brushAlt;
            PushTransform(d, (float)x, (float)y, 1f, rot);
            d.FillRect(new Rect(-rectW * 0.5f * flip, -rectH * 0.5f, rectW * flip, rectH), _paint);
            d.Restore();
        }
    }

    private void DrawDandelion(Immediate2D d, int count, double time, int w, int h, double u,
        double speed, double size, int seed, float life)
    {
        var n = Math.Min(24, count);
        var dir = Unit(seed, 0, 20) < 0.5 ? 1.0 : -1.0;
        var lw = (float)Math.Max(1, 0.9 * u);
        var headR = (float)(1.3 * u * size);

        for (var i = 0; i < n; i++)
        {
            var q = OutCubic(Clamp01((time - Unit(seed, i, 1) * 0.35) / 0.4));
            if (q <= 0) continue;

            var v = (40 + Unit(seed, i, 2) * 50) * u * speed;
            var span = w + 120 * u;
            var x = Wrap(Unit(seed, i, 3) * w + dir * v * (time + Unit(seed, i, 4) * 8), -60 * u, span);
            var y = Wrap(h * (0.12 + 0.76 * Unit(seed, i, 5)) - (18 + Unit(seed, i, 6) * 20) * u * time * speed
                + Math.Sin(time * (1 + Unit(seed, i, 7)) + Unit(seed, i, 8) * 6) * 18 * u, -40 * u, h + 80 * u);

            var avoid = CenterAvoid((float)x, (float)y, w, h);
            if (avoid < 0.6f) continue;

            var rot = (float)((dir * 12 + Math.Sin(time * 1.2 + i) * 18) * Math.PI / 180.0);
            var len = (float)(17 * u * size * q);
            var alpha = Math.Clamp(0.7f * life * avoid, 0f, 1f);

            _paint.Alpha = alpha;
            _paint.IsStroke = true;
            _paint.StrokeWidth = lw;
            _paint.StrokeCap = StrokeCap2D.Round;
            _paint.Brush = _brushAlt;

            PushTransform(d, (float)x, (float)y, 1f, rot);
            for (var k = 0; k < 7; k++)
            {
                var a = (-90 + (k - 3) * 26) * MathF.PI / 180f;
                d.DrawLine(0f, 0f, MathF.Cos(a) * len, MathF.Sin(a) * len, _paint);
            }
            d.Restore();

            _paint.IsStroke = false;
            _paint.Alpha = Math.Clamp(0.85f * life * avoid, 0f, 1f);
            _paint.Brush = _brushMain;
            PushTransform(d, (float)x, (float)y, (float)(q * size), rot);
            d.FillCircle(0f, 0f, headR, _paint);
            d.Restore();
        }
    }

    private void DrawHanabi(Immediate2D d, double time, int w, int h, double u,
        double speed, double size, int seed, float life)
    {
        const double cycle = 2.2;
        const double rise = 0.3;

        var count = Math.Max(1, Math.Min(4, 2 + (int)Unit(seed, 0, 21) % 2));
        var cx = w * 0.5f;
        var cy = h * 0.42f;

        for (var k = 0; k < count; k++)
        {
            var all = time * speed - 0.02 - k * 0.6;
            if (all < 0) continue;

            var cyc = (int)Math.Floor(all / cycle);
            var tau = all - cyc * cycle;
            var r0 = Math.Clamp(Math.Min(w, h) * 0.28, 44 * u, 190 * u) * (0.75 + 0.3 * Unit(seed, k, cyc, 3));
            var x = cx + (Unit(seed, k, cyc, 1) - 0.5) * w * 0.5;
            var y = cy + (Unit(seed, k, cyc, 2) - 0.5) * h * 0.22;
            var useMain = (k + cyc) % 2 == 0;

            if (tau < rise)
            {
                var q = OutCubic(tau / rise);
                var yy = (float)(y + r0 * 1.1 * (1 - q));
                _paint.Alpha = Math.Clamp(0.7f * life, 0f, 1f);
                _paint.IsStroke = true;
                _paint.StrokeWidth = (float)(1.4 * u);
                _paint.StrokeCap = StrokeCap2D.Round;
                _paint.Brush = useMain ? _brushMain : _brushAlt;
                d.DrawLine((float)x, yy, (float)x, yy + (float)(34 * u * (1 - q * 0.7)), _paint);
                _paint.IsStroke = false;
                d.FillCircle((float)x, yy, (float)(2.2 * u), _paint);
                continue;
            }

            var tb = tau - rise;
            var lifeSpan = 1.4;
            if (tb > lifeSpan) continue;

            var fade = (float)(1 - Clamp01((tb - lifeSpan * 0.4) / (lifeSpan * 0.6)));
            var rays = 28;
            var trail = 0.2;
            var lw = (float)Math.Max(1, 1.4 * u);

            for (var i = 0; i < rays; i++)
            {
                var th = (i + Unit(seed, k, cyc, i, 3) * 0.35) / (double)rays * Math.Tau;
                var sp = 0.86 + 0.18 * Unit(seed, k, cyc, i, 3);
                var px = x + Math.Cos(th) * r0 * sp * OutCubic(Clamp01(tb / 0.85));
                var py = y + Math.Sin(th) * r0 * sp * OutCubic(Clamp01(tb / 0.85)) + r0 * 0.28 * tb * tb;
                var qx = x + Math.Cos(th) * r0 * sp * OutCubic(Clamp01(Math.Max(0, tb - trail) / 0.85));
                var qy = y + Math.Sin(th) * r0 * sp * OutCubic(Clamp01(Math.Max(0, tb - trail) / 0.85))
                    + r0 * 0.28 * Math.Max(0, tb - trail) * Math.Max(0, tb - trail);

                var avoid = CenterAvoid((float)px, (float)py, w, h);
                if (avoid <= 0.3f) continue;

                _paint.Alpha = Math.Clamp(0.8f * fade * life * avoid, 0f, 1f);
                _paint.IsStroke = true;
                _paint.StrokeWidth = lw;
                _paint.StrokeCap = StrokeCap2D.Round;
                _paint.Brush = useMain ? _brushMain : _brushAlt;
                d.DrawLine((float)qx, (float)qy, (float)px, (float)py, _paint);

                _paint.IsStroke = false;
                d.FillCircle((float)px, (float)py, (float)(2 * u * size), _paint);
            }
        }
    }

    private void DrawFireflies(Immediate2D d, int count, double time, int w, int h, double u,
        double speed, double size, int seed, float life)
    {
        var n = Math.Min(20, count);
        var cx = w * 0.5;
        var cy = h * 0.5;
        var m = Math.Min(w, h) * 0.05;
        var t = (float)time * (float)speed;

        for (var i = 0; i < n; i++)
        {
            var f = Clamp01((t - Unit(seed, i, 1) * 0.4) / 0.4);
            if (f <= 0) continue;

            var th = Unit(seed, i, 2) * Math.Tau + Noise1(t * 0.18 + i * 3.1, seed) * 1.6;
            var rad = 0.55 + 0.45 * Unit(seed, i, 3) + 0.12 * Noise1(t * 0.3 + i * 5.3, seed + 1);
            var px = (float)Clamp(cx + Math.Cos(th) * (w * 0.5 - m) * rad, m * 0.5, w - m * 0.5);
            var py = (float)Clamp(cy + Math.Sin(th) * (h * 0.5 - m) * rad, m * 0.5, h - m * 0.5);

            var avoid = CenterAvoid(px, py, w, h);
            if (avoid <= 0.02f) continue;

            var blink = (float)(Math.Pow(Math.Max(0, Math.Sin(t * (1.6f + Unit(seed, i, 4) * 1.4f) + Unit(seed, i, 5) * 6f)), 2) * 0.85 + 0.15);
            var a = (float)(f * avoid * blink);

            var useMain = i % 2 == 0;
            _paint.IsStroke = false;
            _paint.Brush = useMain ? _brushMain : _brushAlt;

            _paint.Alpha = Math.Clamp(0.1f * a * life, 0f, 1f);
            d.FillCircle(px, py, (float)(20 * u) * (float)size, _paint);
            _paint.Alpha = Math.Clamp(0.26f * a * life, 0f, 1f);
            d.FillCircle(px, py, (float)(8.5 * u) * (float)size, _paint);
            _paint.Alpha = Math.Clamp((0.35f + 0.65f * a) * life * MathF.Min(1f, a * 3), 0f, 1f);
            d.FillCircle(px, py, (float)(3 * u) * (float)size, _paint);
        }
    }

    private void DrawSnow(Immediate2D d, int count, double time, int w, int h, double u,
        double speed, double size, int seed, float life)
    {
        var n = Math.Min(90, count);
        var t = (float)time * (float)speed;
        var radii = new[] { 2.0f, 3.4f, 5.6f };
        var alphas = new[] { 0.35f, 0.55f, 0.75f };

        for (var i = 0; i < n; i++)
        {
            var z = Unit(seed, i, 1);
            var lay = z < 0.5 ? 0 : z < 0.85 ? 1 : 2;
            var rad = radii[lay] * (float)u * (0.8f + Unit(seed, i, 2) * 0.4f) * (float)size;
            var vy = (22 + lay * 22 + Unit(seed, i, 3) * 14) * u * speed;
            var y = Wrap(Unit(seed, i, 4) * h + vy * t, -10 * u, h + 20 * u);
            var x = Wrap(Unit(seed, i, 5) * w
                + Math.Sin(t * (0.6 + Unit(seed, i, 6)) + Unit(seed, i, 7) * 6) * (8 + lay * 10) * u
                + t * 8 * u, -10 * u, w + 20 * u);

            var useMain = lay == 2;

            if (lay != 0)
            {
                _paint.Brush = useMain ? _brushMain : _brushAlt;
                _paint.Alpha = Math.Clamp(0.08f * life, 0f, 1f);
                d.FillCircle((float)x, (float)y, (float)(rad * 2.2), _paint);
            }

            var avoid = CenterAvoid((float)x, (float)y, w, h);
            _paint.Alpha = Math.Clamp(alphas[lay] * life * avoid, 0f, 1f);
            d.FillCircle((float)x, (float)y, (float)rad, _paint);
        }
    }

    private void DrawBokeh(Immediate2D d, int count, double time, int w, int h, double u,
        double speed, double size, int seed, float life)
    {
        var n = Math.Min(18, count);
        var mm = Math.Min(w, h);
        var t = (float)time * (float)speed;

        for (var i = 0; i < n; i++)
        {
            var f = OutCubic(Clamp01((t - Unit(seed, i, 1) * 0.4) / 0.6));
            if (f <= 0) continue;

            var r0 = mm * (0.025 + Unit(seed, i, 2) * Unit(seed, i, 2) * 0.075);
            var rr = r0 * (1.5 - 0.5 * f) * size;
            if (rr <= 0.5) continue;

            var x = Unit(seed, i, 3) * w + Math.Sin(t * 0.5 + Unit(seed, i, 4) * 6) * 14 * u;
            var y = Wrap(Unit(seed, i, 5) * h - t * (6 + Unit(seed, i, 6) * 12) * u, -r0, h + r0 * 2);

            var useMain = i % 2 == 0;
            var avoid = CenterAvoid((float)x, (float)y, w, h);
            var a = (float)((0.06f + Unit(seed, i, 7) * 0.1f) * f * life * avoid);

            _paint.Brush = useMain ? _brushMain : _brushAlt;
            _paint.IsStroke = false;
            _paint.Alpha = Math.Clamp(a, 0f, 1f);
            d.FillCircle((float)x, (float)y, (float)rr, _paint);
            _paint.Alpha = Math.Clamp(a * 1.3f, 0f, 1f);
            _paint.IsStroke = true;
            _paint.StrokeWidth = (float)Math.Max(1, 1.6 * u);
            _paint.Brush = useMain ? _brushMain : _brushAlt;
            d.DrawCircle((float)x, (float)y, (float)(rr * 0.97), _paint);
            _paint.IsStroke = false;
        }
    }

    private void DrawRainStreaks(Immediate2D d, int count, double time, int w, int h, double u,
        double speed, double size, int seed, float life)
    {
        var n = Math.Min(110, count);
        var sl = 8.0 * Math.PI / 180.0;
        var tn = Math.Tan(sl);
        var t = (float)time * (float)speed;

        for (var i = 0; i < n; i++)
        {
            var z = Unit(seed, i, 1);
            var v = (1200 + z * 1300) * u * speed;
            var len = (36 + z * 80) * u * size;
            var span = h + len + 80 * u;
            var y = Wrap(Unit(seed, i, 2) * span + v * t, -len - 40 * u, span);
            var x = Unit(seed, i, 3) * (w + h * Math.Abs(tn)) + y * tn;

            var avoid = CenterAvoid((float)x, (float)y, w, h);
            if (avoid <= 0.05f) continue;

            var useMain = z >= 0.4;
            var alpha = (z < 0.4 ? 0.18f : z < 0.8 ? 0.3f : 0.46f) * life * avoid;

            _paint.Alpha = Math.Clamp(alpha, 0f, 1f);
            _paint.IsStroke = true;
            _paint.StrokeWidth = (float)Math.Max(1, (z < 0.4 ? 0.9 : z < 0.8 ? 1.2 : 1.6) * u);
            _paint.StrokeCap = StrokeCap2D.Flat;
            _paint.Brush = useMain ? _brushMain : _brushAlt;
            d.DrawLine((float)x, (float)y, (float)(x + len * tn), (float)(y + len), _paint);
            _paint.IsStroke = false;
        }
    }

    private void DrawStarField(Immediate2D d, int count, double time, int w, int h, double u,
        double speed, double size, int seed, float life)
    {
        var n = Math.Min(160, count);
        var t = (float)time * (float)speed;
        var radii = new[] { 1.2f, 1.9f, 2.8f };
        var alphas = new[] { 0.45f, 0.65f, 0.9f };

        for (var i = 0; i < n; i++)
        {
            var f = Clamp01((t - Unit(seed, i, 1) * 0.5) / 0.3);
            if (f <= 0) continue;

            var x = Unit(seed, i, 2) * w;
            var y = Unit(seed, i, 3) * h;
            var tw = 0.5f + 0.5f * MathF.Sin(t * (1.5f + Unit(seed, i, 4) * 3f) + Unit(seed, i, 5) * 6f);
            var lvl = Unit(seed, i, 6) < 0.6 ? 0 : Unit(seed, i, 6) < 0.9 ? 1 : 2;
            var rad = radii[lvl] * (float)u * (0.6f + 0.4f * tw) * f * (float)size;
            if (rad <= 0.3f) continue;

            var useMain = lvl == 2;
            var avoid = CenterAvoid((float)x, (float)y, w, h);

            _paint.Brush = useMain ? _brushMain : _brushAlt;
            _paint.Alpha = Math.Clamp(alphas[lvl] * life * avoid, 0f, 1f);
            d.FillCircle((float)x, (float)y, (float)rad, _paint);

            if (lvl == 2 && Unit(seed, i, 7) < 0.5)
            {
                var l = (float)((7 + 7 * tw) * u);
                _paint.Alpha = Math.Clamp(0.5f * tw * life * avoid, 0f, 1f);
                _paint.IsStroke = true;
                _paint.StrokeWidth = (float)Math.Max(1, 0.8 * u);
                _paint.Brush = useMain ? _brushMain : _brushAlt;
                d.DrawLine((float)x - l, (float)y, (float)x + l, (float)y, _paint);
                d.DrawLine((float)x, (float)y - l, (float)x, (float)y + l, _paint);
                _paint.IsStroke = false;
            }
        }
    }

    private void DrawTwinkle(Immediate2D d, int count, double time, int w, int h, double u,
        double speed, double size, int seed, float life)
    {
        var k2 = Math.Min(7, Math.Max(3, count));
        var cx = w * 0.5f;
        var cy = h * 0.5f;
        var pad = 16f * (float)u + MathF.Min(w, h) * 0.06f;
        var x0 = cx - w * 0.5f + pad;
        var x1 = cx + w * 0.5f - pad;
        var y0 = cy - h * 0.5f + pad;
        var y1 = cy + h * 0.5f - pad;
        var per = 2 * ((x1 - x0) + (y1 - y0));

        for (var i = 0; i < k2; i++)
        {
            var t = (i + Unit(seed, i, 1) * 0.6f) / k2 * per;
            float x, y;
            if (t < x1 - x0) { x = x0 + t; y = y0; }
            else if ((t -= x1 - x0) < y1 - y0) { x = x1; y = y0 + t; }
            else if ((t -= y1 - y0) < x1 - x0) { x = x1 - t; y = y1; }
            else { t -= x1 - x0; x = x0; y = y1 - t; }

            x += Unit(seed, i, 2) * 14f * (float)u;
            y += Unit(seed, i, 3) * 14f * (float)u;
            x = Clamp(x, (float)(16 * u), w - (float)(16 * u));
            y = Clamp(y, (float)(16 * u), h - (float)(16 * u));

            var rr = (i == 0 ? 30f : 13f + Unit(seed, i, 4) * 12f) * (float)u * (float)size;
            var cyc = 1.3f + Unit(seed, i, 5) * 0.8f;
            var ph = (((float)time * (float)speed - Unit(seed, i, 6) * 0.3f) / cyc + Unit(seed, i, 7)) % 1f;
            var intro = (float)OutBack(Clamp01(((float)time * (float)speed - Unit(seed, i, 6) * 0.3f) / 0.3f), 2);
            var s = Math.Min(intro, time > 0.6f ? 0.25f + 0.75f * MathF.Pow(MathF.Sin(MathF.PI * ph), 2) : intro);
            if (s <= 0.02) continue;

            var useMain = i % 3 != 0;
            var rot = Unit(seed, i, 8) * MathF.Tau;

            _paint.Alpha = Math.Clamp((float)s * life, 0f, 1f);
            _paint.Brush = useMain ? _brushMain : _brushAlt;
            PushTransform(d, x, y, (float)(rr * s / 30.0), rot);
            d.FillPath(_star, _paint);
            d.Restore();

            var l = (float)(rr * 1.9 * s);
            _paint.Alpha = Math.Clamp(0.5f * (float)s * life, 0f, 1f);
            _paint.IsStroke = true;
            _paint.StrokeWidth = (float)Math.Max(1, 0.9 * u);
            _paint.Brush = useMain ? _brushMain : _brushAlt;
            d.DrawLine(x - l, y, x + l, y, _paint);
            d.DrawLine(x, y - l, x, y + l, _paint);
            _paint.IsStroke = false;
        }
    }

    private void DrawRising(Immediate2D d, int count, double time, int w, int h, double u,
        double speed, double size, int seed, float life)
    {
        var n = Math.Min(54, count);
        var t = (float)time * (float)speed;
        var lw = (float)Math.Max(1, u);

        for (var i = 0; i < n; i++)
        {
            var vy = (40 + Unit(seed, i, 1) * 90) * u * speed;
            var span = h + 120 * u;
            var y = h + 60 * u - Wrap(Unit(seed, i, 2) * span + vy * t, 0, span);
            var prog = Clamp01(1 - y / h);
            var fade = Math.Pow(Math.Sin(Math.PI * Clamp01(prog)), 0.7);
            if (fade <= 0.05) continue;

            var f = 0.7 + Unit(seed, i, 4);
            var sw = 14 * u;
            var ph = Unit(seed, i, 5) * 6;
            var x = Unit(seed, i, 3) * w + Math.Sin(t * f + ph) * sw;
            var s = (1.6 + Unit(seed, i, 6) * 2.6) * u * size;
            var b = fade > 0.66 ? 2 : fade > 0.33 ? 1 : 0;
            var alphas = new[] { 0.25f, 0.5f, 0.85f };

            var avoid = CenterAvoid((float)x, (float)y, w, h);
            var useMain = b == 2;

            _paint.Alpha = Math.Clamp(alphas[b] * life * avoid, 0f, 1f);
            _paint.Brush = useMain ? _brushMain : _brushAlt;
            d.FillCircle((float)x, (float)y, (float)s, _paint);

            var tl = vy * 0.28;
            _paint.Alpha = Math.Clamp(new[] { 0.08f, 0.16f, 0.26f }[b] * life * avoid, 0f, 1f);
            _paint.IsStroke = true;
            _paint.StrokeWidth = lw;
            _paint.StrokeCap = StrokeCap2D.Round;
            _paint.Brush = useMain ? _brushMain : _brushAlt;
            d.DrawLine((float)x, (float)(y + s * 1.5), (float)(x - Math.Cos(t * f + ph) * f * sw * tl / vy),
                (float)(y + s * 1.5 + tl), _paint);
            _paint.IsStroke = false;
        }
    }

    private void DrawLightLeak(Immediate2D d, double time, int w, int h,
        double speed, double size, int seed, float life)
    {
        var t = (float)time * (float)speed;
        var mm = Math.Max(w, h);
        var breathe = 0.85 + 0.15 * MathF.Sin(t * 1.4f + Unit(seed, 0, 6) * 6f);
        var a = (float)(0.5 * life * breathe);

        for (var k = 0; k < 2; k++)
        {
            var useMain = k == 0;
            var col = useMain ? _colorMain : _colorAlt;
            var cx = (float)(k == 0 ? w : 0) + (float)((k == 0 ? 1 : -1) * mm * 0.05);
            var cy = (float)(h * 0.9 + h * 0.05 + t * 18 * (k == 0 ? 1 : -1));
            var rr = (float)(mm * (k == 0 ? 0.42 + Unit(seed, 0, 1) * 0.18 : 0.22 + Unit(seed, 0, 2) * 0.1) * size);

            var grad = new RadialGradient2D(
                new Vector2(cx, cy), rr, rr,
                [
                    new GradientStop2D(0f, WithAlpha(col, a)),
                    new GradientStop2D(0.35f, WithAlpha(col, a * 0.5f)),
                    new GradientStop2D(1f, WithAlpha(col, 0f)),
                ]);
            _paint.Brush = grad;
            _paint.Alpha = 1f;
            d.FillRect(new Rect(cx - rr, cy - rr, rr * 2, rr * 2), _paint);
        }
    }

    private void DrawSmoke(Immediate2D d, double time, int w, int h, double u,
        double speed, int seed, float life)
    {
        var t = (float)time * (float)speed;
        var hs = h * 0.72 * OutCubic(Clamp01(t / 1.0));
        var nw = 2;

        for (var wi = 0; wi < nw; wi++)
        {
            var xb = w * (0.15 + 0.7 * Unit(seed, wi, 1));
            var yb = h + 10 * u;
            var wseed = seed + wi * 31;

            for (var s = 0; s < 4; s++)
            {
                var sp = s - 1.5;
                var prevX = xb;
                var prevY = (float)yb;
                for (var dd = 8.0 * u; dd <= hs; dd += 8 * u)
                {
                    var amp = 6 * u + dd * 0.17;
                    var x = xb + sp * dd * 0.035
                        + Noise1(dd / (130 * u) - t * 0.8 + s * 0.45, wseed) * amp
                        + Math.Sin(dd / (75 * u) - t * 1.5 + s * 0.8) * amp * 0.4;
                    var y = yb - dd;

                    var useMain = s % 2 == 0;
                    var alpha = (float)((0.6 - s * 0.13) * (1 - s * 0.18) * life * 0.5);
                    _paint.Alpha = Math.Clamp(alpha, 0f, 1f);
                    _paint.IsStroke = true;
                    _paint.StrokeWidth = (float)Math.Max(1, (1.7 - s * 0.25) * u);
                    _paint.StrokeCap = StrokeCap2D.Round;
                    _paint.Brush = useMain ? _brushMain : _brushAlt;
                    d.DrawLine((float)prevX, prevY, (float)x, (float)y, _paint);

                    prevX = x;
                    prevY = (float)y;
                }
            }
        }

        _paint.IsStroke = false;
    }

    private float CenterAvoid(float x, float y, int w, int h)
    {
        var cx = w * 0.5f;
        var cy = h * 0.5f;
        var halfW = w * 0.5f * (float)SubjectKeepOut;
        var halfH = h * 0.5f * (float)SubjectKeepOut;
        var soft = MathF.Max(w, h) * 0.06f;

        var dx = MathF.Max(MathF.Max(cx - halfW - x, 0f), x - (cx + halfW));
        var dy = MathF.Max(MathF.Max(cy - halfH - y, 0f), y - (cy + halfH));
        var dist = MathF.Sqrt(dx * dx + dy * dy);
        return Math.Clamp(dist / soft, 0f, 1f);
    }

    private const double SubjectKeepOut = 0.62;

    private void PushTransform(Immediate2D d, float x, float y, float scale, float rot)
    {
        d.Save();
        if (MathF.Abs(rot) > 1e-5f)
        {
            d.Translate(x, y);
            d.RotateRadians(rot);
            d.Translate(-x, -y);
        }

        if (MathF.Abs(scale - 1f) > 1e-5f)
        {
            d.Translate(x, y);
            d.Scale(scale, scale);
            d.Translate(-x, -y);
        }
    }

    private static float Unit(int seed, int index, uint salt) => (Hash(seed, index, salt) >> 8) * (1f / 16777216f);

    private static float Unit(int seed, int a, int b, uint salt) =>
        (Hash(seed, a * 73856093 ^ b, salt) >> 8) * (1f / 16777216f);

    private static float Unit(int seed, int a, int b, int c, uint salt) =>
        (Hash(seed, (a * 73856093 ^ b * 19349663) ^ c, salt) >> 8) * (1f / 16777216f);

    private static uint Hash(int seed, int index, uint salt)
    {
        var h = (ulong)(uint)seed * 0x9E3779B97F4A7C15UL
                ^ (ulong)(uint)index * 0xC2B2AE3D27D4EB4FUL
                ^ salt;
        h ^= h >> 30;
        h *= 0xBF58476D1CE4E5B9UL;
        h ^= h >> 27;
        h *= 0x94D049BB133111EBUL;
        return (uint)(h ^ (h >> 31));
    }

    private static double Noise1(double x, int seed)
    {
        var i = Math.Floor(x);
        var f = x - i;
        var u = f * f * (3 - 2 * f);
        var a = Unit(seed, (int)i, 1u) * 2 - 1;
        var b = Unit(seed, (int)i + 1, 1u) * 2 - 1;
        return a + (b - a) * u;
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

    private static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;

    private static double Wrap(double v, double lo, double span) => lo + (((v - lo) % span) + span) % span;

    private static double OutCubic(double x)
    {
        x = Clamp01(x);
        return 1 - Math.Pow(1 - x, 3);
    }

    private static double OutBack(double x, double s)
    {
        x = Clamp01(x);
        var c = s + 1;
        return 1 + c * Math.Pow(x - 1, 3) + s * Math.Pow(x - 1, 2);
    }

    private static double Finite(double value, double fallback, double min, double max) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    private static Color4 WithAlpha(Color4 c, float a) => new(c.R, c.G, c.B, c.A * a);

    private static Path2D BuildPetal(ID2D1Factory factory)
    {
        var p = new Path2D(factory);
        p.MoveTo(0f, -1f);
        p.CubicTo(0.62f, -0.74f, 0.78f, 0.22f, 0.20f, 0.88f);
        p.CubicTo(0.10f, 0.70f, -0.10f, 0.70f, -0.20f, 0.88f);
        p.CubicTo(-0.78f, 0.22f, -0.62f, -0.74f, 0f, -1f);
        return p.Close();
    }

    private static Path2D BuildMaple(ID2D1Factory factory)
    {
        double[][] lobes =
        [
            [-125, 0.36], [-82, 0.66], [-42, 0.9], [0, 1.0], [42, 0.9], [82, 0.66], [125, 0.36],
        ];
        var p = new Path2D(factory);
        var first = true;
        for (var i = 0; i < lobes.Length; i++)
        {
            var a = lobes[i][0];
            var l = lobes[i][1];
            var steps = new[] { (a - 17, l * 0.6), (a - 11, l * 0.64), (a - 6, l * 0.84), (a, l), (a + 6, l * 0.84), (a + 11, l * 0.64), (a + 17, l * 0.6) };
            foreach (var (ang, rad) in steps)
            {
                var x = (float)(Math.Sin(ang * Math.PI / 180.0) * rad);
                var y = (float)(-Math.Cos(ang * Math.PI / 180.0) * rad);
                if (first) { p.MoveTo(x, y); first = false; }
                else p.LineTo(x, y);
            }

            var nx = i < lobes.Length - 1 ? lobes[i + 1][0] : 180;
            var midRad = i < lobes.Length - 1 ? 0.3 : 0.1;
            var midAng = (a + nx) / 2;
            var mx = (float)(Math.Sin(midAng * Math.PI / 180.0) * midRad);
            var my = (float)(-Math.Cos(midAng * Math.PI / 180.0) * midRad);
            if (first) { p.MoveTo(mx, my); first = false; }
            else p.LineTo(mx, my);
        }

        return p.Close();
    }

    private static Path2D BuildGlint(ID2D1Factory factory)
    {
        var p = new Path2D(factory);
        for (var i = 0; i < 8; i++)
        {
            var a = i * MathF.PI / 4f;
            var q = i % 2 == 0 ? 1f : 0.2f;
            var x = MathF.Cos(a) * q;
            var y = MathF.Sin(a) * q;
            if (i == 0) p.MoveTo(x, y);
            else p.LineTo(x, y);
        }

        return p.Close();
    }

    public void Dispose()
    {
        _target?.Dispose();
        _petal.Dispose();
        _maple.Dispose();
        _star.Dispose();
    }
}
