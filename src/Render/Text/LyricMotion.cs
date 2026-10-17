using System.Numerics;
using JizuraLive.Engine;
using JizuraLive.Pv;
using Vortice.Mathematics;

namespace JizuraLive.Render.Text;

public sealed record LyricMotion(string Enter, string Hold, string Exit, double EnterDur, double ExitDur, int Seed,
    double HomeScale)
{
    public readonly record struct Frame(float Alpha, Matrix3x2 Matrix, Rect? Clip, float Cx, float Cy);

    public static LyricMotion For(LyricClip clip, int motionSeed)
    {
        var style = clip.Style?.Trim().ToLowerInvariant();
        uint seed = CameraHash.H(motionSeed, StableHash(clip.Id), 17);
        var rng = new CameraRng(seed);

        var enter = style switch
        {
            "typewriter" => "type",
            "pop" => rng.WPick([("pop", 3.0), ("drop", 1.1), ("spin", 1.1), ("zoom", 1.0)]),
            "karaoke" => rng.WPick([("fade", 2.0), ("slide", 1.4), ("pop", 1.0), ("wipe", 0.9)]),
            _ => rng.WPick([("fade", 2.0), ("slide", 1.5), ("pop", 1.1), ("drop", 0.8),
                ("zoom", 0.8), ("wipe", 0.9), ("spin", 0.6)]),
        };

        var hold = style switch
        {
            "pop" => rng.WPick([("breathe", 2.0), ("drift", 1.0), ("still", 1.0), ("jitter", 0.5)]),
            _ => rng.WPick([("still", 2.2), ("breathe", 1.2), ("drift", 1.2), ("jitter", 0.7)]),
        };

        var exit = rng.WPick([("fade", 2.4), ("shrink", 1.1), ("fall", 0.9), ("drift", 0.9), ("wipe", 0.8)]);

        double dur = Math.Max(0.1, (clip.EndMs - clip.StartMs) / 1000.0);
        double enterDur = Math.Min(dur * 0.45, 0.45 + rng.Range(0, 0.35));
        double exitDur = Math.Min(dur * 0.35, 0.35 + rng.Range(0, 0.25));
        if (enterDur + exitDur > dur * 0.8)
        {
            double k = dur * 0.8 / (enterDur + exitDur);
            enterDur *= k;
            exitDur *= k;
        }

        return new LyricMotion(enter, hold, exit, enterDur, exitDur, unchecked((int)seed),
            1 + rng.Range(-0.06, 0.08));
    }

    public Frame Evaluate(double time, LyricClip clip, float w, float h, float scale)
    {
        float cx = w / 2f;
        float cy = h * 0.75f;
        double t = time;
        double dur = Math.Max(0.01, (clip.EndMs - clip.StartMs) / 1000.0);
        double lt = Math.Clamp(t - clip.StartMs / 1000.0, 0, dur);
        double remain = Math.Clamp(clip.EndMs / 1000.0 - t, 0, dur);
        double ep = EnterDur > 0 ? Math.Clamp(lt / EnterDur, 0, 1) : 1;
        double xp = ExitDur > 0 ? Math.Clamp(remain / ExitDur, 0, 1) : 1;

        double dx = 0, dy = 0, sx = 1, sy = 1, rot = 0, alpha = 1;
        Rect? clipRect = null;

        switch (Enter)
        {
            case "fade":
                alpha = ep;
                dy = (1 - OutCubic(ep)) * 16 * scale;
                break;
            case "slide":
                alpha = ep;
                dx = (1 - OutCubic(ep)) * w * 0.26 * Dir();
                break;
            case "pop":
                alpha = Math.Min(1, ep * 2.5);
                double s = 0.55 + 0.45 * OutBack(ep, 2.2);
                sx = sy = s;
                rot = (1 - ep) * 7 * Dir();
                break;
            case "drop":
                alpha = Math.Min(1, ep * 2);
                dy = -(1 - Bounce(ep)) * h * 0.26;
                sy = 1 + (1 - ep) * 0.55;
                sx = 1 - (1 - ep) * 0.18;
                break;
            case "zoom":
                alpha = ep;
                sx = sy = 1.42 - 0.42 * OutCubic(ep);
                break;
            case "spin":
                alpha = Math.Min(1, ep * 2);
                rot = (1 - OutCubic(ep)) * 16 * Dir();
                sx = sy = 0.8 + 0.2 * OutCubic(ep);
                break;
            case "wipe":
                alpha = 1;
                clipRect = WipeRect(ep, cx, cy, w, h);
                break;
        }

        switch (Hold)
        {
            case "breathe":
                sx *= 1 + 0.012 * Math.Sin(lt * 1.7);
                sy *= 1 + 0.012 * Math.Sin(lt * 1.7);
                break;
            case "drift":
                dx += Math.Sin(lt * 0.9) * w * 0.006;
                dy += Math.Cos(lt * 0.7) * h * 0.004;
                break;
            case "jitter":
                int step = (int)Math.Floor(t * 30.0);
                dx += CameraHash.Rs(Seed, step, 1) * 2.2;
                dy += CameraHash.Rs(Seed, step, 2) * 1.6;
                rot += CameraHash.Rs(Seed, step, 3) * 0.3;
                break;
        }

        switch (Exit)
        {
            case "fade":
                alpha *= xp;
                dy -= (1 - xp) * 10 * scale;
                break;
            case "shrink":
                alpha *= xp;
                sx *= 0.72 + 0.28 * xp;
                sy *= 0.72 + 0.28 * xp;
                break;
            case "fall":
                alpha *= xp;
                dy += (1 - xp) * h * 0.2;
                rot += (1 - xp) * 8 * Dir();
                break;
            case "drift":
                alpha *= xp;
                dx += (1 - xp) * w * 0.16 * Dir();
                break;
            case "wipe":
                alpha *= xp;
                clipRect = WipeRect(xp, cx, cy, w, h);
                break;
        }

        var m = Matrix3x2.CreateTranslation(-cx, -cy);
        if (Math.Abs(rot) > 0.01)
            m = Matrix3x2.Multiply(m, Matrix3x2.CreateRotation((float)(rot * Math.PI / 180.0)));
        m = Matrix3x2.Multiply(m, Matrix3x2.CreateScale((float)(sx * HomeScale), (float)(sy * HomeScale)));
        m = Matrix3x2.Multiply(m, Matrix3x2.CreateTranslation((float)(cx + dx), (float)(cy + dy)));

        return new Frame((float)Math.Clamp(alpha, 0, 1), m, clipRect, cx, cy);
    }

    private Rect WipeRect(double p, float cx, float cy, float w, float h)
    {
        double e = InOutCubic(Math.Clamp(p, 0, 1));
        double width = w * e;
        return new Rect((float)(Dir() > 0 ? 0 : w - width), cy - h,
            (float)Math.Max(0, width), 2 * h);
    }

    private int Dir() => (Seed & 1) == 0 ? 1 : -1;

    private static double OutCubic(double q) => 1 - Math.Pow(1 - q, 3);

    private static double InOutCubic(double q) =>
        q < 0.5 ? 4 * q * q * q : 1 - Math.Pow(-2 * q + 2, 3) / 2;

    private static double OutBack(double q, double s)
    {
        double u = q - 1;
        return 1 + (s + 1) * u * u * u + s * u * u;
    }

    private static double Bounce(double q)
    {
        const double n = 7.5625, d = 2.75;
        if (q < 1 / d) return n * q * q;
        if (q < 2 / d) { q -= 1.5 / d; return n * q * q + 0.75; }
        if (q < 2.5 / d) { q -= 2.25 / d; return n * q * q + 0.9375; }
        q -= 2.625 / d;
        return n * q * q + 0.984375;
    }

    private static int StableHash(string text)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (var c in text)
            {
                h ^= c;
                h *= 16777619;
            }
            return (int)h;
        }
    }
}
