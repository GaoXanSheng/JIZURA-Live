using JizuraLive.Pv;

namespace JizuraLive.Render.Text;

public sealed class GlyphPieceXf
{
    public double Dx, Dy;
    public double Rot;
    public double S = 1;
    public double St = 1;
    public double Sdir;
    public double A = 1;

    public static readonly GlyphPieceXf Pid = new();

    public static GlyphPieceXf Pt(double dx = 0, double dy = 0, double rot = 0, double s = 1,
        double st = 1, double sdir = 0, double a = 1) =>
        new() { Dx = dx, Dy = dy, Rot = rot, S = s, St = st, Sdir = sdir, A = a };
}

public sealed class GlyphCharXf
{
    public bool Hide;
    public double Dx, Dy;
    public double Rot;
    public double? S, A, Sx, Sy, Skew;
}

public delegate GlyphCharXf? GlyphCharFn(int i, int n);

public delegate GlyphPieceXf? GlyphPieceFn(int ci, int pj, int pc, double ox, double oy);

public readonly record struct GlyphMotionEnv(
    double W,
    double H,
    double Size,
    int Seed,
    double T,
    double Lt,
    double Dur,
    int Step,
    IReadOnlyDictionary<string, double>? Params);

public enum MotionPhase
{
    Enter,
    Hold,
    Exit,
}

public readonly record struct MotionParam(string Key, double Def, double Lo, double Hi);

public delegate GlyphCharXf? GlyphPresetFn(int i, int n, double p, GlyphMotionEnv env);

public static class Gx
{
    public const double Deg = Math.PI / 180.0;
    public const double Tau = Math.PI * 2.0;

    public static double Clamp(double x) => x < 0 ? 0 : x > 1 ? 1 : x;

    public static double Clamp(double x, double lo, double hi) => x < lo ? lo : x > hi ? hi : x;

    public static double Lerp(double a, double b, double t) => a + (b - a) * t;

    public static double P(in GlyphMotionEnv env, string key, double def) =>
        env.Params is not null && env.Params.TryGetValue(key, out var v) && double.IsFinite(v) ? v : def;

    public static double R(int a, int b = 0, int c = 0, int d = 0, int e = 0) => CameraHash.R(a, b, c, d, e);

    public static double Rs(int a, int b = 0, int c = 0, int d = 0, int e = 0) => CameraHash.Rs(a, b, c, d, e);

    public static double NoiseLike(int seed, double x)
    {
        var i = Math.Floor(x);
        var f = x - i;
        var u = f * f * (3 - 2 * f);
        return Lerp(Rs(seed, (int)i), Rs(seed, (int)i + 1), u);
    }

    public static double Smooth(double a, double b, double x)
    {
        var t = Clamp(b == a ? 0 : (x - a) / (b - a));
        return t * t * (3 - 2 * t);
    }

    public static double OrdLr(int i, int n) => n > 1 ? i / (double)(n - 1) : 0;

    public static double Stg(double p, double k, double spread) => Clamp((p - spread * k) / (1 - spread));

    public static double Win(double p, double start, double len) => Clamp((p - start) / (len <= 0 ? 1e-6 : len));

    public static int Dir(int seed) => (seed & 1) == 0 ? 1 : -1;

    public static double InQuad(double x)
    {
        x = Clamp(x);
        return x * x;
    }

    public static double InQuint(double x)
    {
        x = Clamp(x);
        return x * x * x * x * x;
    }

    public static double Frac(double x) => x - Math.Floor(x);

    public static double InCubic(double x)
    {
        x = Clamp(x);
        return x * x * x;
    }

    public static double OutCubic(double x) { x = Clamp(x); return 1 - Math.Pow(1 - x, 3); }

    public static double InOutCubic(double x)
    {
        x = Clamp(x);
        return x < 0.5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2;
    }

    public static double OutQuint(double x) { x = Clamp(x); return 1 - Math.Pow(1 - x, 5); }

    public static double OutExpo(double x) { x = Clamp(x); return x >= 1 ? 1 : 1 - Math.Pow(2, -10 * x); }

    public static double InExpo(double x) { x = Clamp(x); return x <= 0 ? 0 : Math.Pow(2, 10 * x - 10); }

    public static double InOutExpo(double x)
    {
        if (x <= 0 || x >= 1) return x;
        return x < 0.5 ? Math.Pow(2, 20 * x - 10) / 2 : (2 - Math.Pow(2, -20 * x + 10)) / 2;
    }

    public static double OutBack(double x, double s)
    {
        x = Clamp(x);
        var c = s + 1;
        return 1 + c * Math.Pow(x - 1, 3) + s * Math.Pow(x - 1, 2);
    }

    public static double Bounce(double x)
    {
        const double n = 7.5625, d = 2.75;
        if (x < 1 / d) return n * x * x;
        if (x < 2 / d) { x -= 1.5 / d; return n * x * x + 0.75; }
        if (x < 2.5 / d) { x -= 2.25 / d; return n * x * x + 0.9375; }
        x -= 2.625 / d;
        return n * x * x + 0.984375;
    }
}

public static class GlyphAnim
{
    public static GlyphCharFn? CombineChar(IReadOnlyList<GlyphCharFn> fns)
    {
        if (fns.Count == 0) return null;
        if (fns.Count == 1) return fns[0];
        return (i, n) =>
        {
            GlyphCharXf? o = null;
            foreach (var f in fns)
            {
                var r = f(i, n);
                if (r is null) continue;
                if (r.Hide) return r;
                o ??= new GlyphCharXf();
                o.Dx += r.Dx;
                o.Dy += r.Dy;
                o.Rot += r.Rot;
                if (r.S is not null) o.S = (o.S ?? 1) * r.S.Value;
                if (r.A is not null) o.A = (o.A ?? 1) * r.A.Value;
                if (r.Sx is not null && r.Sx.Value != 0) o.Sx = (o.Sx ?? 1) * r.Sx.Value;
                if (r.Sy is not null && r.Sy.Value != 0) o.Sy = (o.Sy ?? 1) * r.Sy.Value;
                if (r.Skew is not null && r.Skew.Value != 0) o.Skew = (o.Skew ?? 0) + r.Skew.Value;
            }
            return o;
        };
    }

    public static GlyphPieceFn? CombinePiece(IReadOnlyList<GlyphPieceFn> fns)
    {
        if (fns.Count == 0) return null;
        if (fns.Count == 1) return fns[0];
        return (ci, pj, pc, ox, oy) =>
        {
            GlyphPieceXf? o = null;
            foreach (var f in fns)
            {
                var r = f(ci, pj, pc, ox, oy);
                if (r is null) return null;
                if (ReferenceEquals(r, GlyphPieceXf.Pid)) continue;
                o ??= new GlyphPieceXf();
                o.Dx += r.Dx;
                o.Dy += r.Dy;
                o.Rot += r.Rot;
                o.S *= r.S;
                o.A *= r.A;
                if (r.St > o.St)
                {
                    o.St = r.St;
                    o.Sdir = r.Sdir;
                }
            }
            return o ?? GlyphPieceXf.Pid;
        };
    }

    public static GlyphTransform ToGlyphTransform(GlyphCharXf? xf, float homeScale = 1f)
    {
        if (xf is null) return GlyphTransform.Identity;
        if (xf.Hide)
            return new GlyphTransform { ScaleX = 1f, ScaleY = 1f, AlphaScale = 0f };
        return new GlyphTransform
        {
            ScaleX = (float)((xf.Sx ?? xf.S ?? 1) * homeScale),
            ScaleY = (float)((xf.Sy ?? xf.S ?? 1) * homeScale),
            RotationRad = (float)xf.Rot,
            OffsetX = (float)xf.Dx,
            OffsetY = (float)xf.Dy,
            AlphaScale = (float)Gx.Clamp(xf.A ?? 1),
            SkewRad = (float)(xf.Skew ?? 0),
        };
    }
}
