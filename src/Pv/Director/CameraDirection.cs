using Newtonsoft.Json;
using System.ComponentModel;

namespace JizuraLive.Pv;

public sealed class CameraShot
{
    public string Preset { get; set; } = "push";

    public double Start { get; set; }
    public double End { get; set; }

    public int Seed { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
    [DefaultValue("auto")]
    public string Source { get; set; } = "auto";

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
    [DefaultValue(false)]
    public bool Locked { get; set; }

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public Dictionary<string, double> Params { get; set; } = new(StringComparer.Ordinal);

    [JsonIgnore]
    public double Duration => End - Start;
}

public sealed class CameraTrack
{
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<CameraShot> Shots { get; set; } = new();

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<double> Beats { get; set; } = new();

    public double BeatLen { get; set; } = 0.5;

    public double Width { get; set; } = 1280;
    public double Height { get; set; } = 720;

    public CameraTrack Clone()
    {
        var copy = new CameraTrack
        {
            BeatLen = BeatLen,
            Width = Width,
            Height = Height,
            Beats = new List<double>(Beats),
            Shots = new List<CameraShot>(Shots.Count),
        };

        foreach (var shot in Shots)
        {
            copy.Shots.Add(new CameraShot
            {
                Preset = shot.Preset,
                Start = shot.Start,
                End = shot.End,
                Seed = shot.Seed,
                Source = shot.Source,
                Locked = shot.Locked,
                Params = new Dictionary<string, double>(shot.Params ?? new Dictionary<string, double>(), StringComparer.Ordinal),
            });
        }

        return copy;
    }
}

public sealed record CameraPresetInfo(string Key, double Weight, bool Strong);

public static class CameraDirection
{
    private const double Tau = Math.PI * 2;
    private const double Deg = Math.PI / 180.0;
    private const double FrameGrid = 30.0;

    public static readonly IReadOnlyList<CameraPresetInfo> Presets =
    [
        new("push", 1.45, false),
        new("orbitDrift", 0.85, false),
        new("pendulumSway", 0.75, false),
        new("focusIn", 0.90, false),
        new("rackFocus", 0.60, false),
        new("floatNoise", 0.85, false),
        new("tiltDown", 0.80, false),
        new("vertigo", 0.60, false),
        new("spiralIn", 0.65, false),
        new("snapPan", 0.70, false),
        new("jelly", 0.65, false),
        new("earthquake", 0.55, true),
        new("barrelRoll", 0.50, true),
    ];

    public static uint SeedFor(int seed, int shotIndex, int lineIndex) =>
        CameraHash.H((int)CameraHash.H(seed, lineIndex >= 0 ? lineIndex : 0x3f3f3f), shotIndex, 17);

    public static Dictionary<string, double> PlanParams(string preset, CameraRng rng) => preset switch
    {
        "orbitDrift" => P(
            ("dir", rng.Pick(new[] { 1.0, -1.0 })), ("a0", rng.Range(0, Tau)), ("sp", rng.Range(1.1, 1.6))),
        "barrelRoll" => P(
            ("dir", rng.Pick(new[] { 1.0, -1.0 })), ("a", rng.Range(70, 110)), ("d", rng.Range(0.42, 0.55))),
        "pendulumSway" => P(
            ("a", rng.Range(1.8, 2.6)), ("per", rng.Range(2, 3)), ("side", rng.Pick(new[] { 1.0, -1.0 }))),
        "focusIn" => P(("d", rng.Range(0.5, 0.8)), ("b", rng.Range(10, 15))),
        "rackFocus" => P(("b", rng.Range(5, 8)), ("at", rng.Range(0.6, 0.7))),
        "earthquake" => P(("per", rng.Range(0.55, 0.8)), ("a", rng.Range(0.85, 1.1))),
        "floatNoise" => P(("f", rng.Range(0.8, 1.2))),
        "vertigo" => P(("dir", rng.Pick(new[] { 1.0, -1.0 })), ("a", rng.Range(0.05, 0.07))),
        "tiltDown" => P(("a", rng.Range(0.024, 0.032))),
        "spiralIn" => P(
            ("dir", rng.Pick(new[] { 1.0, -1.0 })), ("a0", rng.Range(0, Tau)), ("d", rng.Range(0.9, 1.3))),
        "snapPan" => P(
            ("dir", rng.Pick(new[] { 1.0, -1.0 })), ("a", rng.Range(0.024, 0.032)), ("at", rng.Range(0.45, 0.6))),
        "jelly" => P(("a", rng.Range(0.045, 0.065)), ("f", rng.Range(18, 24))),
        _ => new Dictionary<string, double>(StringComparer.Ordinal),
    };

    private static Dictionary<string, double> P(params (string Key, double Value)[] items)
    {
        var d = new Dictionary<string, double>(items.Length, StringComparer.Ordinal);
        foreach (var (key, value) in items) d[key] = value;
        return d;
    }

    public static bool Evaluate(CameraTrack? track, double time, out PvCamState camera)
    {
        camera = PvCamState.Identity;
        if (track is null || track.Shots.Count == 0 || !double.IsFinite(time)) return false;

        var shot = FindShot(track.Shots, time);
        if (shot is null) return false;

        var env = new Env(track, shot, time);
        camera = Clamp(EvaluatePreset(shot.Preset, in env, shot.Params), env);
        return true;
    }

    private static CameraShot? FindShot(List<CameraShot> shots, double time)
    {
        int lo = 0, hi = shots.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (shots[mid].Start <= time)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (found < 0) return null;
        var shot = shots[found];
        if (time >= shot.Start && (time < shot.End || found == shots.Count - 1)) return shot;
        return null;
    }

    private static PvCamState EvaluatePreset(string preset, in Env e, IReadOnlyDictionary<string, double> p)
    {
        double K = e.Gain;
        double Cu = Clamp(e.Lt / Math.Max(0.3, e.Dur));

        var state = preset switch
        {
            "orbitDrift" => OrbitDrift(e, p, K),
            "barrelRoll" => BarrelRoll(e, p, K),
            "pendulumSway" => PendulumSway(e, p, K),
            "focusIn" => FocusIn(e, p, K, Cu),
            "rackFocus" => RackFocus(e, p, K),
            "earthquake" => Earthquake(e, p, K),
            "floatNoise" => FloatNoise(e, p, K),
            "vertigo" => Vertigo(e, p, K, Cu),
            "tiltDown" => TiltDown(e, p, K, Cu),
            "spiralIn" => SpiralIn(e, p, K, Cu),
            "snapPan" => SnapPan(e, p, K, Cu),
            "jelly" => Jelly(e, p, K),
            _ => Push(e, K, Cu),
        };

        return ApplyPanZoom(state, e, p, K, Cu);
    }

    private static PvCamState ApplyPanZoom(PvCamState s, in Env e, IReadOnlyDictionary<string, double> p,
        double K, double Cu)
    {
        if (K <= 0 || !p.ContainsKey("panX0")) return s;

        double q = InOutSine(Cu);
        double px = ClampRange(Lerp(D(p, "panX0", 0), D(p, "panX1", 0), q), -0.08, 0.08);
        double py = ClampRange(Lerp(D(p, "panY0", 0), D(p, "panY1", 0), q), -0.08, 0.08);
        double z = ClampRange(Lerp(D(p, "zoom0", 1), D(p, "zoom1", 1), q), 0.94, 1.08);

        return s with
        {
            X = s.X + e.W * px * K,
            Y = s.Y + e.H * py * K,
            Scale = s.Scale * (1 + (z - 1) * K),
        };
    }

    private static PvCamState Push(in Env e, double K, double Cu) =>
        new(0, 0, 1 + 0.03 * K * Cu, 0, 0, null, null, 0, 0);

    private static PvCamState OrbitDrift(in Env e, IReadOnlyDictionary<string, double> p, double K)
    {
        double d = D(p, "dir", 1);
        double th = D(p, "a0", 0) + d * e.Lt * D(p, "sp", 1.3);
        double r = OutCubic(Clamp(e.Lt / 0.7));
        return new PvCamState(
            Math.Cos(th) * e.W * 0.016 * K * r,
            Math.Sin(th) * e.H * 0.020 * K * r,
            1 + 0.02 * K,
            Math.Sin(th) * 0.9 * K * r * d,
            0, null, null, 0, 0);
    }

    private static PvCamState BarrelRoll(in Env e, IReadOnlyDictionary<string, double> p, double K)
    {
        double q = Clamp(e.Lt / D(p, "d", 0.5));
        if (q >= 1) return PvCamState.Identity;

        double r = 1 - OutBack(q, 1.3);
        return new PvCamState(
            0, 0,
            1 - 0.12 * K * Math.Sin(Math.PI * Math.Min(1, q * 1.25)),
            D(p, "dir", 1) * D(p, "a", 90) * K * r,
            0, null, null,
            9 * K * Clamp(1 - q * 2.2), 0);
    }

    private static PvCamState PendulumSway(in Env e, IReadOnlyDictionary<string, double> p, double K)
    {
        double damp = 0.75 + 0.25 * Math.Exp(-e.Lt * 0.6);
        double L = e.W * 0.62;
        double phi = D(p, "a", 2.2) * K * D(p, "side", 1) * Math.Cos(e.Lt / D(p, "per", 2.4) * Tau) * damp * Deg;
        return new PvCamState(
            -L * Math.Sin(phi), -L * (1 - Math.Cos(phi)),
            1 + 0.02 * K, phi / Deg, 0, null, null, 0, 0);
    }

    private static PvCamState FocusIn(in Env e, IReadOnlyDictionary<string, double> p, double K, double Cu)
    {
        double q = OutCubic(Clamp(e.Lt / D(p, "d", 0.65)));
        return new PvCamState(
            0, 0,
            1 + K * (0.03 * (1 - q) + 0.012 * Cu),
            0, 0, null, null,
            (1 - q) * D(p, "b", 12) * Math.Min(1, K), 0);
    }

    private static PvCamState RackFocus(in Env e, IReadOnlyDictionary<string, double> p, double K)
    {
        double dur = e.Dur;
        double st = Math.Max(dur * D(p, "at", 0.65), dur - 0.9);
        double q = InOutSine(Clamp((e.Lt - st) / Math.Max(0.2, dur - st)));
        return new PvCamState(
            0, e.H * 0.004 * q * K,
            1 + K * (0.01 - 0.02 * q),
            0, 0, null, null,
            q * D(p, "b", 6.5) * Math.Min(1, K), 0);
    }

    private static PvCamState Earthquake(in Env e, IReadOnlyDictionary<string, double> p, double K)
    {
        double hit = Math.Exp(-BeatSince(e, D(p, "per", 0.65)) * 7);
        double amp = K * (0.14 + hit) * D(p, "a", 1);
        int step = e.Step;
        return new PvCamState(
            CameraHash.Rs(e.Seed, step, 11) * e.W * 0.005 * amp,
            CameraHash.Rs(e.Seed, step, 12) * e.H * 0.014 * amp,
            1 + K * (0.02 + 0.012 * hit),
            CameraHash.Rs(e.Seed, step, 13) * 0.45 * amp,
            0, null, null,
            1.5 * hit * K, 0);
    }

    private static PvCamState FloatNoise(in Env e, IReadOnlyDictionary<string, double> p, double K)
    {
        double t = e.Lt * D(p, "f", 1);
        int sd = unchecked(e.Seed + 7);
        return new PvCamState(
            CameraHash.Noise1(t * 0.6, sd) * e.W * 0.020 * K,
            (CameraHash.Noise1(t * 0.5 + 5, sd + 1) * 0.7 + 0.3 * Math.Sin(t * 1.3)) * e.H * 0.024 * K,
            1 + K * (0.025 + 0.012 * Math.Sin(t * 0.8)),
            CameraHash.Noise1(t * 0.3 + 9, sd + 2) * 1.3 * K,
            0, null, null, 0, 0);
    }

    private static PvCamState Vertigo(in Env e, IReadOnlyDictionary<string, double> p, double K, double Cu)
    {
        double z = InOutSine(Cu);
        double w = Math.Sin(e.Lt * 2.3);
        double d = D(p, "dir", 1);
        return new PvCamState(
            0, 0,
            1 + D(p, "a", 0.06) * K * z,
            0.8 * K * z * Math.Sin(e.Lt * 1.1 + 1) * d,
            2.2 * K * z * Math.Sin(e.Lt * 1.7) * d,
            1 + 0.03 * K * z * w,
            1 - 0.026 * K * z * w,
            0, 0);
    }

    private static PvCamState TiltDown(in Env e, IReadOnlyDictionary<string, double> p, double K, double Cu)
    {
        double q = OutCubic(Cu);
        return new PvCamState(
            0, e.H * D(p, "a", 0.028) * K * (1.2 - 1.6 * q),
            1 + K * (0.035 - 0.02 * q),
            0, 0, null, null, 0, 0);
    }

    private static PvCamState SpiralIn(in Env e, IReadOnlyDictionary<string, double> p, double K, double Cu)
    {
        double d = D(p, "dir", 1);
        double q = OutCubic(Clamp(e.Lt / D(p, "d", 1.1)));
        double r = 1 - q;
        double th = D(p, "a0", 0) + d * q * Tau * 0.8;
        return new PvCamState(
            Math.Cos(th) * e.W * 0.03 * K * r,
            Math.Sin(th) * e.H * 0.035 * K * r,
            1 + K * (-0.08 * r + 0.015 * Cu),
            -d * 7 * K * r,
            0, null, null, 0, 0);
    }

    private static PvCamState SnapPan(in Env e, IReadOnlyDictionary<string, double> p, double K, double Cu)
    {
        double d = D(p, "dir", 1);
        double A = e.W * D(p, "a", 0.028) * K * d;
        double drift = e.W * 0.005 * K * d * (Cu - 0.5);
        if (e.Dur < 1.1)
            return new PvCamState(A * 0.5 * (1 - 2 * Cu), 0, 1 + 0.02 * K, 0, 0, null, null, 0, 0);

        double dt = e.Lt - SnapTime(e, D(p, "at", 0.5));
        double q = InOutCubic(Clamp(dt / 0.16));
        double bell = dt > 0 && dt < 0.16 ? Math.Sin(Math.PI * dt / 0.16) : 0;
        return new PvCamState(
            A * (1 - 2 * q) - drift, 0,
            1 + K * (0.02 + 0.015 * bell),
            0,
            -d * 5 * K * bell,
            null, null,
            14 * K * bell, 0);
    }

    private static PvCamState Jelly(in Env e, IReadOnlyDictionary<string, double> p, double K)
    {
        double t = e.Lt;
        double f = D(p, "f", 21);
        double w = Math.Exp(-t * 5) * Math.Cos(t * f);
        if (e.HasBeat && e.BeatLen > 0.2 && t > 0.6)
            w += 0.45 * Math.Exp(-BeatSince(e, 0.6) * 7) * Math.Cos(BeatSince(e, 0.6) * f);

        double A = D(p, "a", 0.055) * K;
        return new PvCamState(
            0, e.H * 0.008 * K * w,
            1 + 0.01 * K,
            0, 0,
            1 + A * w,
            1 - A * w * 0.9,
            0, 0);
    }

    private static double BeatSince(in Env e, double period)
    {
        if (!e.HasBeat || e.BeatLen <= 0.15) return Wrap(e.Lt, period);
        if (e.Track is null || e.Track.Beats.Count == 0) return Wrap(e.Lt, period);

        int i = UpperBound(e.Track.Beats, e.Time) - 1;
        return i >= 0 ? Math.Max(0, e.Time - e.Track.Beats[i]) : e.Lt;
    }

    private static double SnapTime(in Env e, double at)
    {
        double v = e.Dur * at;
        if (e.Track is null) return v;

        foreach (var b in e.Track.Beats)
        {
            double r = b - e.ShotStart;
            if (r >= e.Dur * 0.38 && r <= e.Dur * 0.72)
                return r;
        }

        return v;
    }

    private static PvCamState Clamp(PvCamState c, in Env e)
    {
        double x = Finite(c.X, 0);
        double y = Finite(c.Y, 0);
        double scale = Finite(c.Scale, 1);
        double rot = Finite(c.Rotation, 0);
        double skew = Finite(c.SkewX, 0);
        double sx = Finite(c.ScaleX ?? 1, 1);
        double sy = Finite(c.ScaleY ?? 1, 1);

        return new PvCamState(
            Math.Clamp(x, -e.W, e.W),
            Math.Clamp(y, -e.H, e.H),
            Math.Clamp(scale, 0.5, 2.0),
            Math.Clamp(rot, -180, 180),
            Math.Clamp(skew, -15, 15),
            Math.Clamp(sx, 0.5, 2.0),
            Math.Clamp(sy, 0.5, 2.0),
            Math.Clamp(Finite(c.BlurSigma, 0), 0, 40),
            Math.Clamp(Finite(c.Brighten, 0), 0, 1));
    }

    private static double Finite(double v, double fallback) => double.IsFinite(v) ? v : fallback;

    private static double D(IReadOnlyDictionary<string, double> p, string key, double fallback) =>
        p.TryGetValue(key, out var v) && double.IsFinite(v) ? v : fallback;

    private static double Clamp(double v) => Math.Clamp(v, 0, 1);

    private static double ClampRange(double v, double lo, double hi) => Math.Clamp(v, lo, hi);

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static double Wrap(double v, double m) => m > 1e-9 ? ((v % m) + m) % m : 0;

    private static double OutCubic(double q) => 1 - Math.Pow(1 - q, 3);

    private static double InOutCubic(double q) =>
        q < 0.5 ? 4 * q * q * q : 1 - Math.Pow(-2 * q + 2, 3) / 2;

    private static double InOutSine(double q) => -(Math.Cos(Math.PI * q) - 1) / 2;

    private static double OutBack(double q, double s)
    {
        double u = q - 1;
        return 1 + (s + 1) * u * u * u + s * u * u;
    }

    private static int UpperBound(List<double> list, double value)
    {
        int lo = 0, hi = list.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (list[mid] <= value) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    private readonly record struct Env(
        CameraTrack? Track,
        double ShotStart,
        double Time,
        double Lt,
        double Dur,
        double W,
        double H,
        int Seed,
        double Gain,
        bool HasBeat,
        double BeatLen,
        int Step)
    {
        public Env(CameraTrack track, CameraShot shot, double time)
            : this(
                track,
                shot.Start,
                time,
                Math.Max(0, time - shot.Start),
                Math.Max(0.001, shot.End - shot.Start),
                track.Width > 0 ? track.Width : 1280,
                track.Height > 0 ? track.Height : 720,
                shot.Seed,
                ParamGain(shot),
                track.Beats.Count > 1 && track.BeatLen > 0.15,
                track.BeatLen > 0 ? track.BeatLen : 0.5,
                (int)Math.Floor(time * FrameGrid))
        {
        }

        private static double ParamGain(CameraShot shot) =>
            shot.Params.TryGetValue("gain", out var g) && double.IsFinite(g) ? Math.Clamp(g, 0, 2) : 1;
    }
}

public sealed class CameraRng(uint seed)
{
    private uint _s = seed;

    public double Next()
    {
        unchecked
        {
            _s += 0x6d2b79f5;
            uint t = _s;
            t = (uint)Imul((int)(t ^ (t >> 15)), (int)(t | 1));
            t ^= (uint)(Imul((int)(t ^ (t >> 7)), (int)(t | 61)) + (int)t);
            return (t ^ (t >> 14)) / 4294967296.0;
        }
    }

    private static int Imul(int a, int b) => unchecked(a * b);

    public double Range(double lo, double hi) => lo + (hi - lo) * Next();

    public T Pick<T>(IReadOnlyList<T> arr) => arr[(int)Math.Floor(Next() * arr.Count) % arr.Count];

    public T WPick<T>(IReadOnlyList<(T V, double W)> list)
    {
        double total = 0;
        foreach (var item in list) total += item.W;

        double x = Next() * total;
        foreach (var item in list)
            if ((x -= item.W) < 0)
                return item.V;

        return list[^1].V;
    }
}

public static class CameraHash
{
    public static uint H(int a, int b = 0, int c = 0, int d = 0, int e = 0)
    {
        unchecked
        {
            var h = (int)(0x9e3779b9 ^ a);
            h = Imul(h ^ (int)((uint)h >> 16), unchecked((int)0x85ebca6b));
            h += Imul((int)((uint)b + 0x632be5ab), unchecked((int)0xc2b2ae35));
            h = Imul(h ^ (int)((uint)h >> 13), unchecked((int)0xc2b2ae35));
            h += Imul((int)((uint)c + 0x5bd1e995), unchecked((int)0x27d4eb2f));
            h = Imul(h ^ (int)((uint)h >> 15), unchecked((int)0x165667b1));
            h += Imul((int)((uint)d + 0x1b873593), unchecked((int)0x85ebca6b));
            h = Imul(h ^ (int)((uint)h >> 16), unchecked((int)0x27d4eb2f));
            h += Imul((int)((uint)e + 0x68e31da4), unchecked((int)0x9e3779b1));
            h ^= (int)((uint)h >> 15);
            h = Imul(h, unchecked((int)0x2c1b3c6d));
            h ^= (int)((uint)h >> 12);
            h = Imul(h, unchecked((int)0x297a2d39));
            h ^= (int)((uint)h >> 15);
            return (uint)h;
        }
    }

    private static int Imul(int a, int b) => unchecked(a * b);

    public static double R(int a, int b = 0, int c = 0, int d = 0, int e = 0) =>
        H(a, b, c, d, e) / 4294967296.0;

    public static double Rs(int a, int b = 0, int c = 0, int d = 0, int e = 0) => R(a, b, c, d, e) * 2 - 1;

    public static double Noise1(double x, int seed = 0)
    {
        var i = Math.Floor(x);
        var f = x - i;
        var u = f * f * (3 - 2 * f);
        return Lerp(Rs(seed, (int)i), Rs(seed, (int)i + 1), u);
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
