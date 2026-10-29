namespace JizuraLive.Pv;

public static partial class AutoDirector
{
        private static void BuildAutoCamera(DirectorProject project, LegacyPvPlanInfo plan, AutoOptions options,
        AutoReport report, List<CameraShot> preserved)
    {
        var track = new CameraTrack
        {
            Width = plan.Width,
            Height = plan.Height,
            Fps = project.Scene is { Fps: > 0 } scene ? scene.Fps : 60,
            BeatLen = plan.BeatLen > 0.05 ? plan.BeatLen : 0.5,
            Beats = new List<double>(plan.Beats),
        };

        double duration = plan.Duration > 0.01
            ? plan.Duration
            : plan.Cuts.Count > 0 ? plan.Cuts[^1].End : 0;
        if (duration <= 0.1)
        {
            if (preserved.Count > 0) track.Shots.AddRange(preserved);
            project.CameraTrack = track;
            return;
        }

        var starts = new List<double> { 0 };
        double last = 0;
        double minShot = Math.Max(2.2, plan.BeatLen * 5);
        foreach (var cut in plan.Cuts)
        {
            if (cut.Start - last < minShot) continue;
            if (cut.Start >= duration - 0.2) break;
            starts.Add(cut.Start);
            last = cut.Start;
        }

        if (starts.Count == 1) starts.Add(duration);
        else if (starts[^1] < duration - 0.4) starts.Add(duration);
        else starts[^1] = duration;

        var rng = new CameraRng(unchecked((uint)(options.Seed * 7919 + 17)));
        int recentSize = -1;
        double amp = (0.55 + 0.9 * options.Intensity) * (options.Style switch
        {
            "soft" => 0.75,
            "epic" => 1.25,
            _ => 1.0,
        });

        for (int i = 0; i < starts.Count - 1; i++)
        {
            double start = starts[i], end = starts[i + 1];
            if (end - start < 0.4) continue;
            if (IsDeleted(project, AutoCamKey(start))) continue;

            var cut = plan.Cuts.FirstOrDefault(c => c.Start >= start - 0.02 && c.Start < end);
            bool emph = cut?.Emph ?? false;

            int size = PickSize(rng, emph, ref recentSize, options.Style);
            double scale = size switch
            {
                0 => rng.Range(0.98, 1.06),
                1 => rng.Range(1.06, 1.18),
                2 => rng.Range(1.18, 1.34),
                _ => rng.Range(1.34, 1.58),
            };
            scale = Math.Clamp(1 + (scale - 1) * amp, 0.9, 1.7);

            double rot = rng.Next() < (emph ? 0.7 : 0.35)
                ? (rng.Next() < 0.5 ? -1 : 1) * rng.Range(2.0, emph ? 9.0 : 6.0)
                : 0;
            rot = Math.Clamp(rot * amp, -12, 12);

            double shiftX = rng.Next() < (emph ? 0.65 : 0.35)
                ? (rng.Next() < 0.5 ? -1 : 1) * rng.Range(0.03, emph ? 0.16 : 0.10)
                : 0;
            double shiftY = rng.Next() < 0.35
                ? (rng.Next() < 0.5 ? -1 : 1) * rng.Range(0.02, 0.07)
                : 0;
            shiftX = Math.Clamp(shiftX * amp, -0.2, 0.2);
            shiftY = Math.Clamp(shiftY * amp, -0.2, 0.2);

            string preset = PickPreset(rng, size);
            var pars = CameraDirection.PlanParams(preset, rng);
            pars["shotScale"] = scale;
            pars["shotX"] = shiftX;
            pars["shotY"] = shiftY;
            pars["shotRot"] = rot;
            pars["gain"] = Math.Clamp(1 + (rng.Range(0.7, emph ? 1.2 : 1.0) - 1) * amp, 0.3, 1.6);

            track.Shots.Add(new CameraShot
            {
                Preset = preset,
                Start = start,
                End = end,
                Seed = unchecked((int)(rng.Next() * int.MaxValue)),
                Params = pars,
                Source = "auto",
            });
        }

        foreach (var shot in preserved) track.Shots.Add(shot);
        track.Shots.Sort((a, b) => a.Start.CompareTo(b.Start));
        project.CameraTrack = track;
        report.CameraShots = track.Shots.Count;
    }

    private static int PickSize(CameraRng rng, bool emph, ref int recent, string style)
    {
        var weights = style switch
        {
            "soft" => new[] { 0.42, 0.40, 0.15, 0.03 },
            "epic" => new[] { 0.08, 0.27, 0.42, 0.23 },
            _ => new[] { 0.28, 0.44, 0.24, 0.04 },
        };

        if (emph)
        {
            weights[0] *= 0.4;
            weights[1] *= 0.8;
            weights[2] *= 1.3;
            weights[3] *= 2.0;
        }

        int size = WeightedIndex(weights, rng);
        if (size == recent && rng.Next() < 0.7)
            size = (size + 1 + (int)(rng.Next() * 3)) % 4;
        recent = size;
        return size;
    }

    private static int WeightedIndex(double[] weights, CameraRng rng)
    {
        double total = 0;
        foreach (var w in weights) total += Math.Max(0, w);
        if (total <= 0) return 1;

        double x = rng.Next() * total;
        for (int i = 0; i < weights.Length; i++)
        {
            x -= Math.Max(0, weights[i]);
            if (x < 0) return i;
        }

        return weights.Length - 1;
    }

    private static string PickPreset(CameraRng rng, int size)
    {
        string[] pool = size switch
        {
            0 => ["pullOut", "panL", "panR", "tiltDown", "tiltUp", "orbitDrift", "driftDiag", "dollyIn", "floatNoise", "handheld"],
            1 => ["orbitDrift", "focusIn", "rackFocus", "floatNoise", "pendulumSway", "vertigo", "handheld", "knReadPan", "knTiltKick", "push"],
            2 => ["push", "spiralIn", "snapPan", "jelly", "whipIn", "whipBlur", "stepZoom", "roll", "barrelRoll", "dutch", "knShearKick", "hrDutchSnap"],
            _ => ["crashZoom", "earthquake", "shakeHard", "whipIn", "barrelRoll", "knRushIn", "knCardFlip", "knJumpCut", "hrNervous", "dollyIn"],
        };

        return rng.Pick(pool);
    }
}
