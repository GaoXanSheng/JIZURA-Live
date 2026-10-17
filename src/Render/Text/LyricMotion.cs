using System.Numerics;
using JizuraLive.Engine;
using JizuraLive.Pv;
using Vortice.Mathematics;

namespace JizuraLive.Render.Text;

public sealed record LyricMotion(string Enter, string Hold, string Exit, double EnterDur, double ExitDur, int Seed,
    double HomeScale)
{
    public Dictionary<string, double>? EnterParams { get; init; }

    public Dictionary<string, double>? HoldParams { get; init; }

    public Dictionary<string, double>? ExitParams { get; init; }

    public readonly record struct Frame(float Alpha, Matrix3x2 Matrix, Rect? Clip, float Cx, float Cy);

    public static LyricMotion For(LyricClip clip, int motionSeed)
    {
        var style = clip.Style?.Trim().ToLowerInvariant();
        uint seed = CameraHash.H(motionSeed, StableHash(clip.Id), 17);
        var rng = new CameraRng(seed);

        var enter = style switch
        {
            "typewriter" => "type",
            "pop" => rng.WPick([("pop", 3.0), ("drop", 1.1), ("spin", 1.1), ("zoom", 1.0), ("bounceBig", 0.6), ("squashDrop", 0.6)]),
            "karaoke" => rng.WPick([("fade", 2.0), ("slide", 1.4), ("pop", 1.0), ("wipe", 0.9), ("trackIn", 0.6)]),
            _ => rng.WPick([("fade", 2.0), ("slide", 1.5), ("pop", 1.1), ("drop", 0.8),
                ("zoom", 0.8), ("wipe", 0.9), ("spin", 0.6), ("trackIn", 0.5), ("riseMask", 0.5), ("windBlown", 0.4)]),
        };

        var hold = style switch
        {
            "pop" => rng.WPick([("breathe", 2.0), ("drift", 1.0), ("still", 1.0), ("jitter", 0.5), ("pulse", 0.5)]),
            _ => rng.WPick([("still", 2.2), ("breathe", 1.2), ("drift", 1.2), ("jitter", 0.7),
                ("float", 0.5), ("sway", 0.4), ("wave", 0.4)]),
        };

        var exit = rng.WPick([("fade", 2.4), ("shrink", 1.1), ("fall", 0.9), ("drift", 0.9), ("wipe", 0.8),
            ("scatter", 0.5), ("dissolve", 0.5), ("riseOut", 0.5)]);

        enter = Resolve(MotionPhase.Enter, enter);
        hold = Resolve(MotionPhase.Hold, hold);
        exit = Resolve(MotionPhase.Exit, exit);

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

    public Frame Evaluate(double time, LyricClip clip, float w, float h, float scale) =>
        Evaluate(time, clip, w, h, scale, 0.5f, 0.75f);

    public Frame Evaluate(double time, LyricClip clip, float w, float h, float scale, float homeX, float homeY)
    {
        float cx = w * homeX;
        float cy = h * homeY;
        double t = time;
        double dur = Math.Max(0.01, (clip.EndMs - clip.StartMs) / 1000.0);
        double lt = Math.Clamp(t - clip.StartMs / 1000.0, 0, dur);
        double remain = Math.Clamp(clip.EndMs / 1000.0 - t, 0, dur);
        double ep = EnterDur > 0 ? Math.Clamp(lt / EnterDur, 0, 1) : 1;
        double xp = ExitDur > 0 ? Math.Clamp(remain / ExitDur, 0, 1) : 1;

        var env = BuildEnv(w, h, scale, t, lt, dur);
        var enterXf = Run(MotionPhase.Enter, Enter, EnterParams, 0, 1, ep, env);
        var holdXf = Run(MotionPhase.Hold, Hold, HoldParams, 0, 1, 1, env);
        var exitXf = Run(MotionPhase.Exit, Exit, ExitParams, 0, 1, xp, env);

        double dx = 0, dy = 0, sx = 1, sy = 1, rot = 0, alpha = 1;
        MergeXf(enterXf, ref dx, ref dy, ref sx, ref sy, ref rot, ref alpha);
        MergeXf(holdXf, ref dx, ref dy, ref sx, ref sy, ref rot, ref alpha);
        MergeXf(exitXf, ref dx, ref dy, ref sx, ref sy, ref rot, ref alpha);

        Rect? clipRect = null;
        if (ep < 1 && string.Equals(Enter, "wipe", StringComparison.OrdinalIgnoreCase))
            clipRect = WipeRect(ep, cx, cy, w, h);
        if (xp < 1 && string.Equals(Exit, "wipe", StringComparison.OrdinalIgnoreCase))
            clipRect = WipeRect(xp, cx, cy, w, h);

        var m = Matrix3x2.CreateTranslation(-cx, -cy);
        if (Math.Abs(rot) > 0.01)
            m = Matrix3x2.Multiply(m, Matrix3x2.CreateRotation((float)(rot * Math.PI / 180.0)));
        m = Matrix3x2.Multiply(m, Matrix3x2.CreateScale((float)(sx * HomeScale), (float)(sy * HomeScale)));
        m = Matrix3x2.Multiply(m, Matrix3x2.CreateTranslation((float)(cx + dx), (float)(cy + dy)));

        return new Frame((float)Math.Clamp(alpha, 0, 1), m, clipRect, cx, cy);
    }

    public GlyphCharXf? GlyphAt(double time, LyricClip clip, int i, int n, float w, float h, float scale)
    {
        double dur = Math.Max(0.01, (clip.EndMs - clip.StartMs) / 1000.0);
        double lt = Math.Clamp(time - clip.StartMs / 1000.0, 0, dur);
        double remain = Math.Clamp(clip.EndMs / 1000.0 - time, 0, dur);
        double ep = EnterDur > 0 ? Math.Clamp(lt / EnterDur, 0, 1) : 1;
        double xp = ExitDur > 0 ? Math.Clamp(remain / ExitDur, 0, 1) : 1;

        var env = BuildEnv(w, h, scale, time, lt, dur);
        var fns = new List<GlyphCharFn>(3);
        var enterDef = MotionPresets.Get(MotionPhase.Enter, Enter);
        var holdDef = MotionPresets.Get(MotionPhase.Hold, Hold);
        var exitDef = MotionPresets.Get(MotionPhase.Exit, Exit);
        if (enterDef is not null) fns.Add((ci, cn) => enterDef.Draw(ci, cn, ep, env));
        if (holdDef is not null) fns.Add((ci, cn) => holdDef.Draw(ci, cn, 1, env));
        if (exitDef is not null) fns.Add((ci, cn) => exitDef.Draw(ci, cn, xp, env));
        var combined = GlyphAnim.CombineChar(fns);
        return combined?.Invoke(i, n);
    }

    private GlyphMotionEnv BuildEnv(float w, float h, float scale, double t, double lt, double dur) => new(
        w, h, h * 0.055f * scale, Seed, t, lt, dur,
        (int)Math.Floor(t * 30.0), null);

    private static GlyphCharXf? Run(MotionPhase phase, string key, Dictionary<string, double>? ps,
        int i, int n, double p, GlyphMotionEnv env)
    {
        var def = MotionPresets.Get(phase, key);
        if (def is null) return null;
        if (ps is null) return def.Draw(i, n, p, env);
        var merged = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var schema in def.Params) merged[schema.Key] = schema.Def;
        foreach (var (k, v) in ps) merged[k] = v;
        var withParams = env with { Params = merged };
        return def.Draw(i, n, p, withParams);
    }

    private static void MergeXf(GlyphCharXf? xf, ref double dx, ref double dy, ref double sx, ref double sy,
        ref double rot, ref double alpha)
    {
        if (xf is null || xf.Hide) return;
        dx += xf.Dx;
        dy += xf.Dy;
        rot += xf.Rot;
        sx *= xf.Sx ?? xf.S ?? 1;
        sy *= xf.Sy ?? xf.S ?? 1;
        alpha *= xf.A ?? 1;
    }

    private static string Resolve(MotionPhase phase, string key) =>
        MotionPresets.TryGet(phase, key, out _) ? key : "cut";

    private Rect WipeRect(double p, float cx, float cy, float w, float h)
    {
        double e = InOutCubic(Math.Clamp(p, 0, 1));
        double width = w * e;
        return new Rect((float)(Dir() > 0 ? 0 : w - width), cy - h,
            (float)Math.Max(0, width), 2 * h);
    }

    private int Dir() => (Seed & 1) == 0 ? 1 : -1;

    private static double InOutCubic(double q) =>
        q < 0.5 ? 4 * q * q * q : 1 - Math.Pow(-2 * q + 2, 3) / 2;

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
