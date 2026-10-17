using JizuraLive.Engine;

namespace JizuraLive.Pv;

public static partial class AutoDirector
{
    private static void BuildCamera(DirectorProject project, Song song, Look look, double k, int seed, AutoReport report)
    {
        project.CameraTrack = BuildCameraTrack(project, song, look, k, seed);
        report.CameraShots = project.CameraTrack.Shots.Count;

        var punch = new FloatCurve();
        var kick = new FloatCurve();
        var pulse = new FloatCurve();
        Key(punch, 0, 1, CurveInterp.Linear);
        Key(pulse, 0, 1, CurveInterp.Linear);
        Key(kick, 0, 0, CurveInterp.Linear);
        double decay = Math.Min(song.BeatLen * 0.9, 0.42);

        foreach (var sec in song.Sections)
        {
            bool high = song.IsHigh(sec);
            double amp = high ? look.ChorusPunch : sec.Kind == "verse" ? look.VersePunch : 0;
            if (amp <= 0) continue;
            amp *= k;

            int bar = 0;
            foreach (var d in song.DownbeatsIn(sec.Start, sec.End))
            {
                bool accent = high || bar % 2 == 0;
                bar++;
                if (!accent) continue;
                Spike(punch, d, 1.0, 1.0 + amp, decay);
                if (high && look.Kick > 0)
                    Spike(kick, d, 0.0, (bar % 2 == 0 ? -1 : 1) * look.Kick * k, decay);
            }

            if (high && look.BeatPulse > 0)
            {
                var downs = song.DownbeatsIn(sec.Start, sec.End).ToHashSet();
                foreach (var b in song.BeatsIn(sec.Start, sec.End))
                    if (!downs.Contains(b))
                        Spike(pulse, b, 1.0, 1.0 + look.BeatPulse * k, Math.Min(song.BeatLen * 0.6, 0.25));
            }
        }

        int camKeys = 0;
        camKeys += Save(project, "camera.scale", BeatLayer, CurveBlend.Multiply, punch, minKeys: 2);
        camKeys += Save(project, "camera.rotation", BeatLayer, CurveBlend.Add, kick, minKeys: 2);
        camKeys += Save(project, "camera.scale", PulseLayer, CurveBlend.Multiply, pulse, minKeys: 2);

        var snap = new FloatCurve();
        Key(snap, 0, 1, CurveInterp.Linear);
        double snapDecay = Math.Min(song.BeatLen * 0.55, 0.3);
        foreach (var sec in song.Sections)
        {
            if (sec.Kind is "intro" or "outro" or "bridge") continue;
            if (song.IsHigh(sec))
            {
                int i = 0;
                foreach (var b in song.BeatsIn(sec.Start, sec.End))
                    if (i++ % 2 == 1) Spike(snap, b, 1.0, 1.0 + look.Snap * k, snapDecay);
            }
            else
            {
                foreach (var d in song.DownbeatsIn(sec.Start, sec.End))
                    Spike(snap, d, 1.0, 1.0 + look.Snap * 0.6 * k, snapDecay);
            }
        }
        camKeys += Save(project, "camera.scale", SnapLayer, CurveBlend.Multiply, snap, minKeys: 2);

        var whip = new FloatCurve();
        Key(whip, 0, 0, CurveInterp.Linear);
        BuildWhips(song, look, k, whip);
        camKeys += Save(project, "camera.offsetX", WhipLayer, CurveBlend.Add, whip, minKeys: 2);

        var blur = new FloatCurve();
        var brighten = new FloatCurve();
        BuildDefocus(song, look, k, blur, brighten);
        camKeys += Save(project, "camera.blur", FocusLayer, CurveBlend.Add, blur, minKeys: 2);
        camKeys += Save(project, "camera.brighten", BloomLayer, CurveBlend.Add, brighten, minKeys: 2);

        report.CurveKeys += camKeys;
    }

    public const string FocusLayer = "auto.focus";
    public const string BloomLayer = "auto.bloom";

    private static CameraTrack BuildCameraTrack(DirectorProject project, Song song, Look look, double k, int seed)
    {
        var track = new CameraTrack
        {
            Width = project.Scene.Width,
            Height = project.Scene.Height,
            BeatLen = song.BeatLen > 0.05 ? song.BeatLen : 0.5,
            Beats = song.Beats.Where(double.IsFinite).Where(b => b >= 0).Distinct().OrderBy(b => b).ToList(),
        };

        if (!(song.Duration > 0.01)) return track;

        var cuts = CameraCutTimes(song, look);
        var recent = new List<string>(3);

        for (int i = 0; i < cuts.Count - 1; i++)
        {
            double start = cuts[i], end = cuts[i + 1];
            if (end - start < 0.05) continue;

            var sec = song.SectionAt(start + 1e-6);
            bool high = sec is not null && song.IsHigh(sec);
            int lineIndex = song.Lines.FindIndex(l => Math.Abs(l.Start - start) < 1e-6);
            bool lineStart = lineIndex >= 0;

            uint shotSeed = CameraDirection.SeedFor(seed, i, lineIndex);
            var rng = new CameraRng(shotSeed);
            string preset = ChooseCameraPreset(song, sec, lineStart, high, look, recent, rng);
            var param = CameraDirection.PlanParams(preset, rng);
            AddRandomPanZoom(param, rng);

            double gain = k * look.CamGain;
            if (sec is not null) gain *= 0.72 + 0.28 * Math.Clamp(sec.Energy, 0, 1);
            if (!lineStart) gain *= 0.8;
            param["gain"] = gain;

            track.Shots.Add(new CameraShot
            {
                Preset = preset,
                Start = start,
                End = end,
                Seed = unchecked((int)shotSeed),
                Params = param,
            });

            recent.Add(preset);
            if (recent.Count > 3) recent.RemoveAt(0);
        }

        return track;
    }

    private static void AddRandomPanZoom(Dictionary<string, double> p, CameraRng rng)
    {
        double x0 = rng.Range(-0.035, 0.035);
        double y0 = rng.Range(-0.030, 0.030);
        p["panX0"] = x0;
        p["panY0"] = y0;
        p["panX1"] = Math.Clamp(x0 + rng.Range(-0.055, 0.055), -0.08, 0.08);
        p["panY1"] = Math.Clamp(y0 + rng.Range(-0.045, 0.045), -0.08, 0.08);
        p["zoom0"] = rng.Range(0.97, 1.04);
        p["zoom1"] = rng.Range(0.97, 1.06);
    }

    private static List<double> CameraCutTimes(Song song, Look look)
    {
        var set = new SortedSet<double>();
        void Add(double t)
        {
            if (double.IsFinite(t) && t >= 0 && t <= song.Duration) set.Add(Math.Round(t, 6));
        }

        Add(0);
        Add(song.Duration);
        foreach (var sec in song.Sections)
        {
            Add(sec.Start);
            Add(sec.End);
        }
        foreach (var line in song.Lines)
        {
            Add(line.Start);
            if (line.End > line.Start + 0.05) Add(line.End);
        }

        var sectionStarts = song.Sections.Select(s => Math.Round(s.Start, 6)).ToHashSet();
        var raw = set.ToList();
        var filtered = new List<double>(raw.Count) { raw[0] };
        for (int i = 1; i < raw.Count; i++)
        {
            double t = raw[i];
            bool forced = sectionStarts.Contains(t) || Math.Abs(t - song.Duration) < 1e-9;
            if (!forced && t - filtered[^1] < 0.3) continue;
            filtered.Add(t);
        }

        var downbeats = song.Downbeats.Where(double.IsFinite).OrderBy(d => d).ToList();
        var beats = song.Beats.Where(double.IsFinite).OrderBy(d => d).ToList();
        double maxLen = Math.Max(1.0, look.CamCutBars * song.BarLen);
        var final = new List<double>(filtered.Count * 2);

        for (int i = 0; i < filtered.Count - 1; i++)
        {
            double a = filtered[i], b = filtered[i + 1];
            final.Add(a);
            double span = b - a;
            if (span <= maxLen * 1.15) continue;

            int parts = Math.Max(2, (int)Math.Ceiling(span / maxLen));
            for (int p = 1; p < parts; p++)
            {
                double target = a + span * p / parts;
                double cut = Nearest(beats.Count > 0 ? beats : downbeats, target, out bool found);
                if (!found) cut = target;
                if (cut > final[^1] + 0.35 && cut < b - 0.25) final.Add(cut);
            }
        }

        final.Add(filtered[^1]);
        final.Sort();
        return final;
    }

    private static double Nearest(List<double> times, double target, out bool found)
    {
        found = false;
        if (times.Count == 0) return target;

        int lo = 0, hi = times.Count - 1, index = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (times[mid] <= target) { index = mid; lo = mid + 1; }
            else hi = mid - 1;
        }

        double best = target;
        double bestDist = double.MaxValue;
        if (index >= 0)
        {
            best = times[index];
            bestDist = Math.Abs(best - target);
        }

        if (index + 1 < times.Count)
        {
            double d = Math.Abs(times[index + 1] - target);
            if (d < bestDist) best = times[index + 1];
        }

        found = Math.Abs(best - target) <= 0.6;
        return best;
    }

    private static string ChooseCameraPreset(Song song, Section? sec, bool lineStart, bool high,
        Look look, List<string> recent, CameraRng rng)
    {
        var candidates = new List<(string V, double W)>(CameraDirection.Presets.Count);

        foreach (var preset in CameraDirection.Presets)
        {
            double w = preset.Weight * StyleAffinity(look.Style, preset.Key);

            if (sec is null)
            {
                w *= 0.5;
            }
            else if (sec.Kind is "intro" or "outro")
            {
                w *= preset.Strong ? 0.12 : 1.15;
            }
            else if (sec.Kind == "bridge")
            {
                w *= preset.Key is "floatNoise" or "focusIn" or "rackFocus" or "vertigo" or "orbitDrift" ? 1.7
                    : preset.Strong ? 0.35 : 1.0;
            }
            else if (high)
            {
                w *= preset.Strong ? 1.45 * (0.65 + 0.55 * Math.Clamp(sec.Energy, 0, 1)) : 1.05;
            }
            else
            {
                w *= preset.Strong ? 0.3 : 1.2;
            }

            if (!lineStart) w *= 0.75;

            int last = recent.LastIndexOf(preset.Key);
            if (last == recent.Count - 1) w *= 0.22;
            else if (last == recent.Count - 2) w *= 0.5;
            else if (last >= 0) w *= 0.75;

            candidates.Add((preset.Key, Math.Max(0.001, w)));
        }

        return rng.WPick(candidates);
    }

    private static double StyleAffinity(string style, string preset) => style switch
    {
        "soft" => preset switch
        {
            "push" => 2.2,
            "focusIn" => 1.8,
            "rackFocus" => 1.6,
            "floatNoise" => 1.6,
            "pendulumSway" => 1.3,
            "tiltDown" => 1.3,
            "orbitDrift" => 1.2,
            "vertigo" => 0.4,
            "spiralIn" => 0.25,
            "snapPan" => 0.2,
            "jelly" => 0.2,
            "earthquake" => 0.1,
            "barrelRoll" => 0.08,
            _ => 1,
        },
        "epic" => preset switch
        {
            "earthquake" => 1.35,
            "vertigo" => 1.5,
            "tiltDown" => 1.5,
            "orbitDrift" => 1.2,
            "spiralIn" => 1.4,
            "snapPan" => 1.2,
            "barrelRoll" => 1.2,
            "pendulumSway" => 1.0,
            "push" => 0.9,
            "floatNoise" => 0.8,
            "focusIn" => 0.7,
            "rackFocus" => 0.5,
            "jelly" => 0.7,
            _ => 1,
        },
        _ => preset switch
        {
            "spiralIn" => 1.3,
            "snapPan" => 1.35,
            "jelly" => 1.25,
            "orbitDrift" => 1.15,
            "pendulumSway" => 1.1,
            "earthquake" => 1.0,
            "vertigo" => 1.0,
            "tiltDown" => 1.0,
            "floatNoise" => 1.0,
            "focusIn" => 1.0,
            "push" => 1.15,
            "rackFocus" => 0.8,
            "barrelRoll" => 0.65,
            _ => 1,
        },
    };
}
