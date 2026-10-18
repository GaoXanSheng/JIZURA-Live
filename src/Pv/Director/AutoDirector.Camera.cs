namespace JizuraLive.Pv;

public static partial class AutoDirector
{
    /// <summary>
    /// 按原版 Plan 的切点自动生成多机位：景别（远/中/近/特）、左右构图、俯仰/荷兰角，
    /// 每个机位内部再叠加 push/pan/dolly 等运动。角色不再单独位移，整幅画面一起跟机位走。
    /// </summary>
    private static void BuildAutoCamera(DirectorProject project, LegacyPvPlanInfo plan, AutoOptions options,
        AutoReport report, List<CameraShot> preserved)
    {
        var track = new CameraTrack
        {
            Width = plan.Width,
            Height = plan.Height,
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

        for (int i = 0; i < starts.Count - 1; i++)
        {
            double start = starts[i], end = starts[i + 1];
            if (end - start < 0.4) continue;

            var cut = plan.Cuts.FirstOrDefault(c => c.Start >= start - 0.02 && c.Start < end);
            bool emph = cut?.Emph ?? false;

            int size = PickSize(rng, emph, ref recentSize);
            double scale = size switch
            {
                0 => rng.Range(0.96, 1.00),
                1 => rng.Range(1.00, 1.06),
                2 => rng.Range(1.08, 1.16),
                _ => rng.Range(1.16, 1.24),
            };

            double rot = rng.Next() < (emph ? 0.55 : 0.25)
                ? (rng.Next() < 0.5 ? -1 : 1) * rng.Range(0.8, emph ? 3.0 : 2.0)
                : 0;

            string preset = PickPreset(rng, size);
            var pars = CameraDirection.PlanParams(preset, rng);
            pars["shotScale"] = scale;
            // 机位只做景别/旋转，不平移：画布、PV、角色原位都不被推走。
            pars["shotX"] = 0;
            pars["shotY"] = 0;
            pars["shotRot"] = rot;
            pars["gain"] = 0;

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

    private static int PickSize(CameraRng rng, bool emph, ref int recent)
    {
        var weights = new double[4];
        if (emph)
        {
            weights[0] = 0.10;
            weights[1] = 0.30;
            weights[2] = 0.40;
            weights[3] = 0.20;
        }
        else
        {
            weights[0] = 0.28;
            weights[1] = 0.44;
            weights[2] = 0.24;
            weights[3] = 0.04;
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
            0 => ["push", "floatNoise", "handheld", "roll", "pendulumSway"],
            1 => ["push", "floatNoise", "handheld", "stepZoom", "roll", "pendulumSway"],
            2 => ["push", "stepZoom", "whipBlur", "shakeHard", "knRushIn", "roll"],
            _ => ["stepZoom", "whipBlur", "shakeHard", "knRushIn", "push", "roll"],
        };

        return rng.Pick(pool);
    }
}
