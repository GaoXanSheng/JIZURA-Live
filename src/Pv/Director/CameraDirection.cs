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

    public bool UserEdited { get; set; }

    public CameraTrack Clone()
    {
        var copy = new CameraTrack
        {
            BeatLen = BeatLen,
            Width = Width,
            Height = Height,
            UserEdited = UserEdited,
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
        new("pullOut", 1.00, false),
        new("panL", 0.90, false),
        new("panR", 0.90, false),
        new("tiltUp", 0.80, false),
        new("dutch", 0.80, false),
        new("handheld", 1.00, false),
        new("beatPunch", 0.90, false),
        new("whipIn", 0.70, true),
        new("crashZoom", 0.60, true),
        new("roll", 0.80, false),
        new("driftDiag", 1.00, false),
        new("shakeHard", 0.60, true),
        new("dollyIn", 1.00, false),
        new("stepZoom", 0.80, false),
        new("whipBlur", 0.80, false),
        new("hrNervous", 0.90, false),
        new("hrDutchSnap", 0.70, true),
        new("knReadPan", 0.80, false),
        new("knTiltKick", 0.80, false),
        new("knCardFlip", 0.70, true),
        new("knShearKick", 0.80, false),
        new("knJumpCut", 0.70, false),
        new("knRushIn", 0.70, true),
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
        "pullOut" => P(("a", rng.Range(0.06, 0.09))),
        "panL" or "panR" => P(("a", rng.Range(0.018, 0.028))),
        "tiltUp" => P(("a", rng.Range(0.02, 0.03))),
        "dutch" => P(("dir", rng.Pick(new[] { 1.0, -1.0 })), ("a", rng.Range(2.5, 4.5))),
        "handheld" => P(("f", rng.Range(0.8, 1.3))),
        "beatPunch" => P(("a", rng.Range(0.03, 0.045))),
        "whipIn" => P(
            ("dir", rng.Pick(new[] { -1.0, 1.0, -1.0, 1.0, -2.0, 2.0 })), ("d", rng.Range(0.16, 0.24))),
        "crashZoom" => P(("at", rng.Range(0.6, 0.75)), ("a", rng.Range(0.08, 0.11))),
        "roll" => P(("dir", rng.Pick(new[] { 1.0, -1.0 })), ("a", rng.Range(2.5, 4.0))),
        "driftDiag" => P(
            ("dx", rng.Pick(new[] { 1.0, -1.0 })), ("dy", rng.Pick(new[] { 1.0, -1.0 }))),
        "shakeHard" => P(("a", rng.Range(0.85, 1.15))),
        "dollyIn" => P(("a", rng.Range(0.08, 0.11))),
        "stepZoom" => P(("n", rng.Pick(new[] { 2.0, 3.0 })), ("a", rng.Range(0.035, 0.045))),
        "whipBlur" => P(("a", rng.Range(0.85, 1.15))),
        "hrNervous" => P(("f", rng.Range(0.9, 1.3)), ("jerk", rng.Range(0.6, 1.0))),
        "hrDutchSnap" => P(
            ("at", rng.Range(0.45, 0.65)), ("a", rng.Range(3.0, 4.8) * rng.Pick(new[] { 1.0, -1.0 }))),
        "knReadPan" => P(("a", rng.Range(0.026, 0.036))),
        "knTiltKick" => P(("dir", rng.Pick(new[] { 1.0, -1.0 })), ("a", rng.Range(2.4, 3.4))),
        "knCardFlip" => P(("vert", rng.Pick(new[] { 0.0, 0.0, 0.0, 1.0 })), ("dir", rng.Pick(new[] { 1.0, -1.0 }))),
        "knShearKick" => P(("a", rng.Range(5.0, 8.0))),
        "knJumpCut" => P(("s0", Math.Floor(rng.Range(0, 100)))),
        "knRushIn" => P(("z", rng.Range(0.66, 0.76)), ("r", rng.Range(-4.0, 4.0))),
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
            "pullOut" => PullOut(e, p, K, Cu),
            "panL" => PanL(e, p, K, Cu),
            "panR" => PanR(e, p, K, Cu),
            "tiltUp" => TiltUp(e, p, K, Cu),
            "dutch" => Dutch(e, p, K, Cu),
            "handheld" => Handheld(e, p, K),
            "beatPunch" => BeatPunch(e, p, K),
            "whipIn" => WhipIn(e, p, K),
            "crashZoom" => CrashZoom(e, p, K),
            "roll" => Roll(e, p, K, Cu),
            "driftDiag" => DriftDiag(e, p, K, Cu),
            "shakeHard" => ShakeHard(e, p, K),
            "dollyIn" => DollyIn(e, p, K, Cu),
            "stepZoom" => StepZoom(e, p, K),
            "whipBlur" => WhipBlur(e, p, K, Cu),
            "hrNervous" => HrNervous(e, p, K),
            "hrDutchSnap" => HrDutchSnap(e, p, K),
            "knReadPan" => KnReadPan(e, p, K),
            "knTiltKick" => KnTiltKick(e, p, K),
            "knCardFlip" => KnCardFlip(e, p, K),
            "knShearKick" => KnShearKick(e, p, K),
            "knJumpCut" => KnJumpCut(e, p, K),
            "knRushIn" => KnRushIn(e, p, K),
            _ => Push(e, K, Cu),
        };

        state = ApplyFraming(state, e, p);
        return ApplyPanZoom(state, e, p, K, Cu);
    }

    // 分镜机位：景别/构图/角度是镜头本身，不参与动效强度 k 的缩放。
    private static PvCamState ApplyFraming(PvCamState s, in Env e, IReadOnlyDictionary<string, double> p)
    {
        if (!p.ContainsKey("shotScale")) return s;

        double scale = Math.Clamp(D(p, "shotScale", 1), 0.5, 2.0);
        double x = D(p, "shotX", 0) * e.W;
        double y = D(p, "shotY", 0) * e.H;
        double rot = D(p, "shotRot", 0);

        return s with
        {
            X = s.X * scale + x,
            Y = s.Y * scale + y,
            Scale = s.Scale * scale,
            Rotation = s.Rotation + rot,
            ScaleX = s.ScaleX is { } sx ? 1 + (sx - 1) * scale : null,
            ScaleY = s.ScaleY is { } sy ? 1 + (sy - 1) * scale : null,
        };
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

    private static PvCamState PullOut(in Env e, IReadOnlyDictionary<string, double> p, double K, double Cu) =>
        new(0, 0, 1 + D(p, "a", 0.07) * K * (1 - OutCubic(Cu)), 0, 0, null, null, 0, 0);

    private static PvCamState PanL(in Env e, IReadOnlyDictionary<string, double> p, double K, double Cu)
    {
        double x = (PanP(Cu) - 0.5) * 2 * e.W * D(p, "a", 0.022) * K;
        return new(x, 0, 1.02, 0, 0, null, null, 0, 0);
    }

    private static PvCamState PanR(in Env e, IReadOnlyDictionary<string, double> p, double K, double Cu)
    {
        double x = -(PanP(Cu) - 0.5) * 2 * e.W * D(p, "a", 0.022) * K;
        return new(x, 0, 1.02, 0, 0, null, null, 0, 0);
    }

    private static PvCamState TiltUp(in Env e, IReadOnlyDictionary<string, double> p, double K, double Cu)
    {
        double y = (PanP(Cu) - 0.5) * 2 * e.H * D(p, "a", 0.025) * K;
        return new(0, y, 1.02, 0, 0, null, null, 0, 0);
    }

    private static PvCamState Dutch(in Env e, IReadOnlyDictionary<string, double> p, double K, double Cu)
    {
        double q = Clamp(e.Lt / Math.Max(0.3, e.Dur * 0.8));
        double rot = D(p, "dir", 1) * Math.Min(5, D(p, "a", 3.5) * K) * InOutSine(q);
        return new(0, 0, 1 + 0.03 * K * Cu, rot, 0, null, null, 0, 0);
    }

    private static PvCamState Handheld(in Env e, IReadOnlyDictionary<string, double> p, double K)
    {
        double f = D(p, "f", 1);
        double t = e.Lt;
        int sd = e.Seed;
        return new PvCamState(
            (CameraHash.Noise1(t * f * 1.1, sd) * 0.7 + CameraHash.Noise1(t * f * 2.9, sd + 1) * 0.3) * e.W * 0.007 * K,
            (CameraHash.Noise1(t * f * 0.9, sd + 2) * 0.7 + CameraHash.Noise1(t * f * 3.3, sd + 3) * 0.3) * e.H * 0.009 * K,
            1.012,
            CameraHash.Noise1(t * f * 0.8, sd + 4) * 0.7 * K,
            0, null, null, 0, 0);
    }

    private static PvCamState BeatPunch(in Env e, IReadOnlyDictionary<string, double> p, double K)
    {
        double since = BeatSince(e, 0.5);
        double hit = Math.Exp(-since * 9);
        return new PvCamState(
            0, -e.H * 0.004 * K * hit,
            1 + D(p, "a", 0.035) * K * hit,
            0, 0, null, null, 0, 0);
    }

    private static PvCamState WhipIn(in Env e, IReadOnlyDictionary<string, double> p, double K)
    {
        double r = 1 - OutExpo(Clamp(e.Lt / 0.3));
        if (r <= 0.001) return PvCamState.Identity;

        double dir = D(p, "dir", 1);
        bool hor = Math.Abs(dir) < 1.5;
        double sg = dir < 0 ? -1 : 1;
        double d = D(p, "d", 0.2) * K * r;
        return new PvCamState(
            hor ? sg * e.W * d : 0,
            hor ? 0 : sg * e.H * d * 0.7,
            1 + 0.04 * r,
            0,
            hor ? sg * 9 * r * K : 0,
            null, null,
            22 * K * r, 0);
    }

    private static PvCamState CrashZoom(in Env e, IReadOnlyDictionary<string, double> p, double K)
    {
        double dur = e.Dur;
        double tc = dur < 0.8 ? dur * 0.5 : Math.Max(dur * D(p, "at", 0.65), dur - 0.6);
        double dt = e.Lt - tc;
        if (dt < 0)
            return new PvCamState(0, 0, 1 - 0.008 * K * Clamp((e.Lt - tc + 0.25) / 0.25),
                0, 0, null, null, 0, 0);

        double q = OutExpo(Clamp(dt / 0.1));
        double sh = Math.Exp(-dt * 6) * q;
        return new PvCamState(
            CameraHash.Rs(e.Seed, e.Step, 1) * e.W * 0.004 * sh * K,
            CameraHash.Rs(e.Seed, e.Step, 2) * e.H * 0.004 * sh * K,
            1 + Math.Min(0.12, D(p, "a", 0.1) * K) * q,
            0, 0, null, null,
            12 * K * Math.Max(0, 1 - Math.Abs(dt - 0.05) / 0.08),
            0);
    }

    private static PvCamState Roll(in Env e, IReadOnlyDictionary<string, double> p, double K, double Cu) =>
        new(0, 0, 1.025, D(p, "dir", 1) * (Cu - 0.5) * D(p, "a", 3) * K, 0, null, null, 0, 0);

    private static PvCamState DriftDiag(in Env e, IReadOnlyDictionary<string, double> p, double K, double Cu)
    {
        double u = InOutSine(Cu) * 0.5 + Cu * 0.5;
        return new PvCamState(
            (u - 0.5) * e.W * 0.035 * K * D(p, "dx", 1),
            (u - 0.5) * e.H * 0.03 * K * D(p, "dy", 1),
            1.02 + 0.025 * K * u,
            0, 0, null, null, 0, 0);
    }

    private static PvCamState ShakeHard(in Env e, IReadOnlyDictionary<string, double> p, double K)
    {
        double amp = Math.Exp(-e.Lt * 4.5) * K * D(p, "a", 1);
        if (amp < 0.01) return PvCamState.Identity;

        return new PvCamState(
            CameraHash.Rs(e.Seed, e.Step, 1) * e.W * 0.022 * amp,
            CameraHash.Rs(e.Seed, e.Step, 2) * e.H * 0.02 * amp,
            1 + 0.03 * amp,
            CameraHash.Rs(e.Seed, e.Step, 3) * 1.6 * amp,
            0, null, null,
            2.5 * amp, 0);
    }

    private static PvCamState DollyIn(in Env e, IReadOnlyDictionary<string, double> p, double K, double Cu)
    {
        double q = InCubic(Cu);
        return new PvCamState(
            0, -e.H * 0.008 * K * q,
            1 + Math.Min(0.14, D(p, "a", 0.1) * K) * q,
            0, 0, null, null, 0, 0);
    }

    private static PvCamState StepZoom(in Env e, IReadOnlyDictionary<string, double> p, double K)
    {
        int n = Math.Max(1, (int)Math.Round(D(p, "n", 2)));
        double a = D(p, "a", 0.04) * K;
        double s = 1;
        double blur = 0;
        foreach (var ti in StepTimes(e, n))
        {
            double d = e.Lt - ti;
            if (d < 0) continue;
            s += a * OutExpo(Clamp(d / 0.07));
            blur += 5 * K * (1 - Clamp(d / 0.06));
        }

        return new PvCamState(0, 0, Math.Min(1.15, s), 0, 0, null, null, blur, 0);
    }

    private static PvCamState WhipBlur(in Env e, IReadOnlyDictionary<string, double> p, double K, double Cu)
    {
        double amt = Cover(Cu, 0.4);
        double d = e.W * 0.045 * D(p, "a", 1) * amt;
        return new PvCamState(0, 0, 1, 0, 0, null, null, d * 0.5 * Math.Min(1, K), 0);
    }

    private static PvCamState HrNervous(in Env e, IReadOnlyDictionary<string, double> p, double K)
    {
        double f = D(p, "f", 1);
        double jerk = D(p, "jerk", 0.8);
        double t = e.Lt;
        int sd = e.Seed;
        double x = (CameraHash.Noise1(t * f * 1.7, sd) * 0.6 + CameraHash.Noise1(t * f * 5.3, sd + 1) * 0.4) * e.W * 0.009 * K;
        double y = (CameraHash.Noise1(t * f * 1.4, sd + 2) * 0.6 + CameraHash.Noise1(t * f * 4.7, sd + 3) * 0.4) * e.H * 0.011 * K
                   + Math.Sin(t * 2.2) * e.H * 0.004 * K;
        double rot = CameraHash.Noise1(t * f * 1.1, sd + 4) * 1.2 * K;

        const double per = 1.6;
        int cyc = (int)Math.Floor(t / per);
        double since = t - cyc * per - CameraHash.R(sd, cyc, 5) * per * 0.6;
        if (since > 0 && CameraHash.R(sd, cyc, 6) < 0.65 * jerk)
        {
            double d = Math.Exp(-since * 9) * K;
            x += CameraHash.Rs(sd, cyc, 7) * e.W * 0.025 * d;
            y += CameraHash.Rs(sd, cyc, 8) * e.H * 0.03 * d;
            rot += CameraHash.Rs(sd, cyc, 9) * 3 * d;
        }

        return new PvCamState(x, y, 1.03, rot, 0, null, null, 0, 0);
    }

    private static PvCamState HrDutchSnap(in Env e, IReadOnlyDictionary<string, double> p, double K)
    {
        double u = Clamp(e.Lt / Math.Max(0.3, e.Dur));
        double q = Clamp((e.Lt - e.Dur * D(p, "at", 0.55)) / 0.1);
        double b = OutBack(q, 2.5);
        return new PvCamState(
            0, -e.H * 0.006 * K * b,
            1 + 0.045 * K * InOutSine(u) + 0.02 * K * b,
            D(p, "a", 4) * Math.Min(1, K) * b,
            0, null, null, 0, 0);
    }

    private static PvCamState KnReadPan(in Env e, IReadOnlyDictionary<string, double> p, double K)
    {
        double[] ts = WordTimes(e);
        int n = ts.Length;
        double amp = e.W * D(p, "a", 0.03) * K;
        double lt = e.Lt;

        if (n < 2)
            return new PvCamState(
                amp * (1 - 2 * InOutSine(Clamp(lt / Math.Max(0.4, e.Dur)))) * 0.5,
                0, 1.03, 0, 0, null, null, 0, 0);

        double Pos(int i) => amp * (1 - 2 * i / (double)(n - 1));
        int idx = Math.Max(0, CurIdx(ts, lt));
        double ease = idx > 0 ? OutBack(Clamp((lt - ts[idx]) / 0.2), 1.8) : 1;
        double x = idx > 0 ? Lerp(Pos(idx - 1), Pos(idx), ease) : Pos(0);
        double tb = ts[n - 1] + 0.55;
        x *= 1 - InOutCubic(Clamp((lt - tb) / 0.5));
        double whip = idx > 0 ? Bell(Clamp((lt - ts[idx]) / 0.14)) : 0;
        double sgn = amp > 0 ? 1 : amp < 0 ? -1 : 0;
        return new PvCamState(x, 0, 1.035, 0, -sgn * 2.5 * whip * K, null, null, 3 * whip * K, 0);
    }

    private static PvCamState KnTiltKick(in Env e, IReadOnlyDictionary<string, double> p, double K)
    {
        double[] ts = WordTimes(e);
        int n = ts.Length;
        double k = Math.Min(1, K);
        double lt = e.Lt;
        double dir = D(p, "dir", 1);
        double a = D(p, "a", 3) * k * (dir != 0 ? dir : 1);
        double Ang(int i) => i >= n - 1 ? 0 : (i % 2 != 0 ? -a : a);
        int idx = Math.Max(0, CurIdx(ts, lt));
        double d = lt - ts[idx];
        double from = idx > 0 ? Ang(idx - 1) : 0;
        double to = Ang(idx);
        double sp = Math.Exp(-d * 7) * Math.Cos(d * 17);
        double r = to + (from - to) * sp;
        return new PvCamState(0, 0, 1.03 + 0.012 * Math.Abs(r) / 3,
            Math.Clamp(r, -5, 5), 0, null, null, 0, 0);
    }

    private static PvCamState KnCardFlip(in Env e, IReadOnlyDictionary<string, double> p, double K)
    {
        double[] ts = WordTimes(e);
        double lt = e.Lt;
        double k = Math.Min(1, K);
        double dir = D(p, "dir", 1);
        double q = Clamp(lt / 0.42);
        double th = 90 * (1 - OutBack(q, 1.7));
        double f = Math.Max(0.04, Math.Abs(Math.Cos(th * Deg)));
        for (int i = 1; i < ts.Length; i++)
        {
            double dd = lt - ts[i];
            if (dd > 0 && dd < 0.24) f *= 1 - 0.1 * k * Bell(dd / 0.24);
        }

        double sk = 7 * Math.Sin(th * Deg) * dir * k;
        if (D(p, "vert", 0) > 0.5)
            return new PvCamState(0, -e.H * 0.02 * Math.Sin(th * Deg), 1.01 * f, 0, 0, null, null, 0, 0);

        return new PvCamState(
            e.W * 0.02 * Math.Sin(th * Deg) * dir, 0, 1.01 * f, 0, sk * 0.4, null, null, 0, 0);
    }

    private static PvCamState KnShearKick(in Env e, IReadOnlyDictionary<string, double> p, double K)
    {
        double[] ts = WordTimes(e);
        double lt = e.Lt;
        double k = Math.Min(1.1, K);
        double since;
        int idx;
        if (e.HasBeat && e.BeatLen > 0.2 && lt > ts[^1] + 0.3)
        {
            since = BeatSince(e, 0.65);
            idx = Math.Max(0, BeatIndex(e));
        }
        else
        {
            idx = Math.Max(0, CurIdx(ts, lt));
            since = lt - ts[idx];
        }

        double w = Math.Exp(-since * 8) * Math.Cos(since * 22);
        double sg = idx % 2 != 0 ? 1 : -1;
        return new PvCamState(
            e.W * 0.006 * k * w * sg, 0, 1.02, 0,
            D(p, "a", 6.5) * k * w * sg, null, null, 0, 0);
    }

    private static PvCamState KnJumpCut(in Env e, IReadOnlyDictionary<string, double> p, double K)
    {
        double[] ts = WordTimes(e);
        int n = ts.Length;
        double lt = e.Lt;
        double k = Math.Min(1, K);
        int idx = Math.Max(0, CurIdx(ts, lt));
        if (lt > ts[n - 1] + 0.5 || n < 2) return new PvCamState(0, 0, 1.02, 0, 0, null, null, 0, 0);

        int sd = e.Seed + (int)D(p, "s0", 0);
        double[] tab = { 1.0, 1.12, 1.05, 1.14, 1.08, 1.13 };
        double s = tab[((idx + sd % 3) % 6 + 6) % 6];
        double sg = idx % 2 != 0 ? 1 : -1;
        return new PvCamState(
            sg * (0.5 + 0.5 * CameraHash.R(sd, idx, 1)) * e.W * 0.04 * k * (idx != 0 ? 1 : 0),
            CameraHash.Rs(sd, idx, 2) * e.H * 0.035 * k,
            1 + (s - 1) * k,
            0, 0, null, null, 0, 0);
    }

    private static PvCamState KnRushIn(in Env e, IReadOnlyDictionary<string, double> p, double K)
    {
        double k = Math.Min(1, K);
        double q = Clamp(e.Lt / 0.34);
        if (q >= 1)
            return new PvCamState(0, 0,
                1 + 0.01 * Clamp((e.Lt - 0.34) / Math.Max(0.3, e.Dur)), 0, 0, null, null, 0, 0);

        double ease = OutBack(q, 1.9);
        return new PvCamState(
            0, 0,
            Lerp(1 - (1 - D(p, "z", 0.7)) * k, 1, ease),
            D(p, "r", 0) * (1 - OutCubic(q)) * k,
            0, null, null,
            9 * k * (1 - OutCubic(q)), 0);
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

    private static double InCubic(double q) => q * q * q;

    private static double OutExpo(double q) => q >= 1 ? 1 : 1 - Math.Pow(2, -10 * q);

    private static double PanP(double u) => u * 0.6 + InOutSine(u) * 0.4;

    private static double Bell(double q) => Math.Sin(Math.PI * Clamp(q));

    private static double Cover(double q, double b)
    {
        double u = Clamp(q);
        return u < b ? InCubic(u / b) : 1 - OutCubic((u - b) / Math.Max(0.01, 1 - b));
    }

    private static double[] WordTimes(in Env e, int nMax = 6)
    {
        double dur = e.Dur;
        int n = Math.Clamp((int)Math.Floor(dur / 0.38) + 1, 2, nMax);
        double last = Math.Max(0, Math.Min(dur * 0.5, (n - 1) * 0.38));
        var t = new double[n];
        for (int i = 0; i < n; i++) t[i] = last * i / (n - 1);

        if (!e.HasBeat || e.Track is null) return t;

        var pick = new List<double>(n - 1);
        double prev = 0;
        foreach (var b in e.Track.Beats)
        {
            double r = b - e.ShotStart;
            if (r <= 0.12) continue;
            if (r > dur * 0.72) break;
            if (r - prev >= 0.2)
            {
                pick.Add(r);
                prev = r;
            }

            if (pick.Count >= n - 1) break;
        }

        if (pick.Count >= n - 1 && pick[n - 2] <= Math.Max(last * 1.35, dur * 0.55))
            for (int i = 1; i < n; i++) t[i] = pick[i - 1];

        return t;
    }

    private static int CurIdx(double[] ts, double t)
    {
        int k = -1;
        for (int i = 0; i < ts.Length; i++)
            if (t >= ts[i]) k = i;
        return k;
    }

    private static int BeatIndex(in Env e) =>
        e.Track is null ? -1 : UpperBound(e.Track.Beats, e.Time) - 1;

    private static List<double> StepTimes(in Env e, int n)
    {
        var v = new List<double>(n);
        if (e.Track is not null)
        {
            foreach (var b in e.Track.Beats)
            {
                double r = b - e.ShotStart;
                if (r <= 0.2 || r >= e.Dur - 0.15) continue;
                if (v.Count == 0 || r - v[^1] >= 0.3) v.Add(r);
                if (v.Count >= n) break;
            }
        }

        if (v.Count == 0)
            for (int i = 0; i < n; i++) v.Add(e.Dur * (i + 1) / (n + 1));

        return v;
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

    public static double Rr(double lo, double hi, int a, int b = 0, int c = 0, int d = 0, int e = 0) =>
        lo + (hi - lo) * R(a, b, c, d, e);

    public static double Noise1(double x, int seed = 0)
    {
        var i = Math.Floor(x);
        var f = x - i;
        var u = f * f * (3 - 2 * f);
        return Lerp(Rs(seed, (int)i), Rs(seed, (int)i + 1), u);
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
