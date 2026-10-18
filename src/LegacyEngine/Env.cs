using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace JizuraLive.LegacyEngine;

public sealed class Env
{
    [ThreadStatic] private static SKPaint? _paint;
    [ThreadStatic] private static SKPath? _path;

        internal static SKPaint SharedPaint()
    {
        var p = _paint ??= new SKPaint();
        p.StrokeJoin = SKStrokeJoin.Miter;
        p.StrokeCap = SKStrokeCap.Butt;
        p.StrokeMiter = 4;
        p.StrokeWidth = 0;
        return p;
    }

        internal static SKPath SharedPath()
    {
        var p = _path ??= new SKPath();
        p.Reset();
        return p;
    }
    public SKCanvas Ctx { get; set; } = null!;

        public int W { get; set; }
    public int H { get; set; }

        public Scheme? Sc { get; set; }

    public StyleDef St { get; set; } = null!;
    public FxSettings Fx { get; set; } = new();
    public int Fps { get; set; } = 24;
    public Cut? Cut { get; set; }
    public Plan Plan { get; set; } = null!;

        public double Lt { get; set; }

        public double T { get; set; }

        public double Ltb { get; set; }

        public double Step { get; set; }

        public double Energy { get; set; }

        public double Beat { get; set; }

        public bool BeatHas { get; set; }

        public double BeatSince { get; set; }

        public double BeatLen { get; set; }

        public int BeatIndex { get; set; }

        public Zone? Zone { get; set; }

        public string? Pass { get; set; }

        public string? PassColor { get; set; }

        public double Scale { get; set; } = 1;

        public double PIn { get; set; }

        public double POut { get; set; }

        public JObject P => Cut?.Params ?? EmptyParams;

    private static readonly JObject EmptyParams = new();

        public BBox? Body { get; set; }

        public JObject? It { get; set; }

        public double Ap { get; set; }

        public List<JAnim.CharFn> CharFns { get; } = new();

        public List<JAnim.PieceFn> PieceFns { get; } = new();

        public JAnim.CharFn? CharFn { get; set; }

        public JAnim.PieceFn? PieceFn { get; set; }

        public Action<Env, JObject>? PreFn { get; set; }

        public Action<Env, JObject, BBox?>? PostFn { get; set; }

        public Func<Env, JObject, SKPath>? ClipFn { get; set; }

        public string? Layer { get; set; }

        public bool HideText { get; set; }

        public bool AllowFilter { get; set; } = true;

        public List<JText.GlyphLogEntry>? GlyphLog { get; set; }

        public SKBlendMode Blend { get; set; } = SKBlendMode.SrcOver;

        public static Env Make(SKCanvas ctx, Plan plan, Cut? cut, Scheme? sc, double lt, double step,
        double energy, double beat, Zone? zone, string pass, string? passColor, double scale,
        double t = 0, double ltb = 0)
    {
        var w = zone?.W ?? plan.W;
        var h = zone?.H ?? plan.H;
        var env = new Env
        {
            Ctx = ctx,
            Plan = plan,
            Cut = cut,
            Sc = sc,
            St = plan.Style,
            Fx = plan.Fx,
            Fps = plan.Fps,
            W = w,
            H = h,
            Lt = lt,
            T = t,
            Ltb = ltb,
            Step = step,
            Energy = energy,
            Beat = beat,
            Zone = zone,
            Pass = pass,
            PassColor = passColor,
            Scale = scale
        };
        if (cut != null)
        {
            env.PIn = J.Clamp(lt / Math.Max(0.01, cut.InDur));
            env.POut = cut.OutDur > 0 ? J.Clamp((lt - (cut.Dur - cut.OutDur)) / cut.OutDur) : 0;
        }
        else
        {
            env.PIn = 1;
            env.POut = 0;
        }
        return env;
    }

        public bool GhostPass => Pass != "main";

        public string? ColOf(string? c, bool ghostable) => GhostPass ? (ghostable ? PassColor : null) : c;

        public static SKColor SkColor(string hex, double alpha = 1)
    {
        var (r, g, b) = J.Hex(hex);
        return new SKColor((byte)r, (byte)g, (byte)b, (byte)Math.Round(J.Clamp(alpha) * 255));
    }

        public BBox? DrawItem(JObject it)
    {
        if (ReferenceEquals(It, it)) return DrawItemCore(it);
        var (c, p) = (CharFn, PieceFn);
        CharFn = null;
        PieceFn = null;
        try
        {
            return DrawItemCore(it);
        }
        finally
        {
            CharFn = c;
            PieceFn = p;
        }
    }

    private BBox? DrawItemCore(JObject it) => JText.DrawItem(this, it, new JText.TextDrawOpts
    {
        HideText = HideText,
        GlyphLog = GlyphLog,
        AllowFilter = AllowFilter,
    });

        public void Rect(double x, double y, double w, double h, string? c, double a = 1, bool ghostable = true)
    {
        var col = ColOf(c, ghostable);
        if (col == null || a <= 0) return;
        using var p = new SKPaint { IsAntialias = true, Color = SkColor(col, a), BlendMode = Blend };
        Ctx.DrawRect(new SKRect((float)x, (float)y, (float)(x + w), (float)(y + h)), p);
    }

        public void Line(double[][] pts, string? c, double lw = 1, double a = 1, bool ghostable = true)
    {
        var col = ColOf(c, ghostable);
        if (col == null || a <= 0 || pts.Length < 2) return;
        var path = SharedPath();
        path.MoveTo((float)pts[0][0], (float)pts[0][1]);
        for (var i = 1; i < pts.Length; i++) path.LineTo((float)pts[i][0], (float)pts[i][1]);
        var p = SharedPaint();
        p.IsAntialias = true;
        p.IsStroke = true;
        p.StrokeWidth = (float)lw;
        p.StrokeJoin = SKStrokeJoin.Miter;
        p.StrokeCap = SKStrokeCap.Butt;
        p.Shader = null;
        p.ImageFilter = null;
        p.Color = SkColor(col, a);
        p.BlendMode = Blend;
        Ctx.DrawPath(path, p);
    }

        public void PolyPartial(double[][] pts, double e, string? c, double lw = 1, double a = 1, bool ghostable = true)
    {
        if (e <= 0) return;
        var L = 0.0;
        var seg = new double[pts.Length - 1];
        for (var i = 1; i < pts.Length; i++)
        {
            var d = Math.Sqrt((pts[i][0] - pts[i - 1][0]) * (pts[i][0] - pts[i - 1][0]) + (pts[i][1] - pts[i - 1][1]) * (pts[i][1] - pts[i - 1][1]));
            seg[i - 1] = d;
            L += d;
        }
        var rem = L * J.Clamp(e);
        var @out = new List<double[]> { pts[0] };
        for (var i = 1; i < pts.Length && rem > 0; i++)
        {
            var d = seg[i - 1];
            if (rem >= d)
            {
                @out.Add(pts[i]);
                rem -= d;
            }
            else
            {
                var k = rem / d;
                @out.Add(new[] { J.Lerp(pts[i - 1][0], pts[i][0], k), J.Lerp(pts[i - 1][1], pts[i][1], k) });
                rem = 0;
            }
        }
        Line(@out.ToArray(), c, lw, a, ghostable);
    }

        public void Circle(double cx, double cy, double r, string? fill, string? stroke, double lw = 1, double a = 1, bool ghostable = true)
    {
        if (r <= 0 || a <= 0) return;
        var f = fill != null ? ColOf(fill, ghostable) : null;
        var s = stroke != null ? ColOf(stroke, ghostable) : null;
        if (f == null && s == null) return;
        if (f != null)
        {
            var p = SharedPaint();
            p.IsAntialias = true;
            p.IsStroke = false;
            p.Shader = null;
            p.Color = SkColor(f, a);
            p.BlendMode = Blend;
            Ctx.DrawCircle((float)cx, (float)cy, (float)r, p);
        }
        if (s != null)
        {
            var p = SharedPaint();
            p.IsAntialias = true;
            p.IsStroke = true;
            p.StrokeWidth = (float)lw;
            p.Shader = null;
            p.Color = SkColor(s, a);
            p.BlendMode = Blend;
            Ctx.DrawCircle((float)cx, (float)cy, (float)r, p);
        }
    }

        public void Arc(double cx, double cy, double r, double a0, double a1, string? c, double lw = 1, double a = 1, bool ghostable = true)
    {
        var col = ColOf(c, ghostable);
        if (col == null || a <= 0 || r <= 0) return;
        var path = SharedPath();
        path.AddArc(new SKRect((float)(cx - r), (float)(cy - r), (float)(cx + r), (float)(cy + r)), (float)a0, (float)(a1 - a0));
        var p = SharedPaint();
        p.IsAntialias = true;
        p.IsStroke = true;
        p.StrokeWidth = (float)lw;
        p.Shader = null;
        p.Color = SkColor(col, a);
        p.BlendMode = Blend;
        Ctx.DrawPath(path, p);
    }

        public void RRect(double x, double y, double w, double h, double r, object? fill, double a = 1, bool ghostable = true,
        string? stroke = null, double lw = 1)
    {
        if (a <= 0 || w <= 0 || h <= 0) return;
        object? f;
        if (fill == null) f = null;
        else if (GhostPass) f = ghostable ? PassColor : null;           else f = fill;
        var s = stroke != null ? ColOf(stroke, ghostable) : null;
        if (f == null && s == null) return;
        r = Math.Min(r, Math.Min(w / 2, h / 2));
        using var rr = new SKRoundRect(new SKRect((float)x, (float)y, (float)(x + w), (float)(y + h)), (float)r);
        if (f is string fs)
        {
            var p = SharedPaint();
            p.IsAntialias = true;
            p.IsStroke = false;
            p.Shader = null;
            p.Color = SkColor(fs, a);
            p.BlendMode = Blend;
            Ctx.DrawRoundRect(rr, p);
        }
        else if (f is SKShader shader)
        {
            var p = SharedPaint();
            p.IsAntialias = true;
            p.IsStroke = false;
            p.Color = SkColor("#FFFFFF", a);
            p.Shader = shader;
            p.BlendMode = Blend;
            Ctx.DrawRoundRect(rr, p);
            p.Shader = null;
        }
        if (s != null)
        {
            var p = SharedPaint();
            p.IsAntialias = true;
            p.IsStroke = true;
            p.StrokeWidth = (float)lw;
            p.Shader = null;
            p.Color = SkColor(s, a);
            p.BlendMode = Blend;
            Ctx.DrawRoundRect(rr, p);
        }
    }

        public void Poly(double[][] pts, string? c, double a = 1, bool ghostable = true)
    {
        var col = ColOf(c, ghostable);
        if (col == null || a <= 0) return;
        var path = SharedPath();
        for (var i = 0; i < pts.Length; i++)
        {
            if (i == 0) path.MoveTo((float)pts[i][0], (float)pts[i][1]);
            else path.LineTo((float)pts[i][0], (float)pts[i][1]);
        }
        path.Close();
        var p = SharedPaint();
        p.IsAntialias = true;
        p.IsStroke = false;
        p.Shader = null;
        p.Color = SkColor(col, a);
        p.BlendMode = Blend;
        Ctx.DrawPath(path, p);
    }

        public void Blob(double[][] pts, string? c, double a = 1, bool ghostable = true)
    {
        var col = ColOf(c, ghostable);
        if (col == null || a <= 0) return;
        var n = pts.Length;
        double[] Mid(int i) => new[] { (pts[i % n][0] + pts[(i + 1) % n][0]) / 2, (pts[i % n][1] + pts[(i + 1) % n][1]) / 2 };
        var path = SharedPath();
        var m0 = Mid(0);
        path.MoveTo((float)m0[0], (float)m0[1]);
        for (var i = 1; i <= n; i++)
        {
            var pt = pts[i % n];
            var m = Mid(i);
            path.QuadTo((float)pt[0], (float)pt[1], (float)m[0], (float)m[1]);
        }
        path.Close();
        var paint = SharedPaint();
        paint.IsAntialias = true;
        paint.IsStroke = false;
        paint.Shader = null;
        paint.Color = SkColor(col, a);
        paint.BlendMode = Blend;
        Ctx.DrawPath(path, paint);
    }
}
