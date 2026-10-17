namespace JizuraLive.Pv;

public static partial class AutoDirector
{
    private static readonly HashSet<string> AnimeFxDefIds =
        ["speedLines", "mangaPanels", "wipe", "duotone", "letterbox"];

    private static void BuildAnimeFx(DirectorProject project, Song song, Look look, string style, double k, Random rng, AutoReport report)
    {
        double fx = (0.5 + 0.5 * k) * (style == "soft" ? 0.6 : 1.0);
        double beat = song.BeatLen, bar = song.BarLen;

        var drops = new List<double>();
        for (int i = 0; i < song.Sections.Count; i++)
            if (song.IsHigh(song.Sections[i]) && (i == 0 || !song.IsHigh(song.Sections[i - 1])))
                drops.Add(song.Sections[i].Start);

        var cool = AddEffect(project, "duotone", "cool", n =>
        {
            Set(n, "amount", (float)(0.20 * fx));
            SetColor(n, "shadow", "#1d2b64");
            SetColor(n, "highlight", "#f8cdda");
        });
        foreach (var sec in song.Sections)
            if (!song.IsHigh(sec)) Window(cool, sec.Start, sec.End, beat / 2, beat / 2);

        var vivid = AddEffect(project, "duotone", "vivid", n =>
        {
            Set(n, "amount", (float)(0.26 * fx));
            SetColor(n, "shadow", "#3a0ca3");
            SetColor(n, "highlight", "#ff9ecf");
        });
        foreach (var sec in song.Sections)
            if (song.IsHigh(sec)) Window(vivid, sec.Start, sec.End, beat / 2, beat / 2);

        var cinema = AddEffect(project, "letterbox", "cinema", n => Set(n, "amount", 1f));
        foreach (var sec in song.Sections)
            if (sec.Kind is "intro" or "outro") Window(cinema, sec.Start, sec.End, bar, bar);

        if (style != "soft")
        {
            var burst = AddEffect(project, "speedLines", "drop", n => Set(n, "amount", 0.85f));
            foreach (var d in drops) Window(burst, d, d + bar * 1.25, fadeOut: bar * 0.8);

            var dash = AddEffect(project, "speedLines", "accent", n => Set(n, "amount", 0.4f));
            foreach (var sec in song.Sections)
            {
                if (!song.IsHigh(sec)) continue;
                int i = 0;
                foreach (var d in song.DownbeatsIn(sec.Start, sec.End))
                    if (i++ % 4 == 3) Window(dash, d, d + beat / 2, fadeOut: beat / 2);
            }

            var panels = AddEffect(project, "mangaPanels", "drop", n =>
            {
                Set(n, "amount", 1f);
                Set(n, "count", 3f);
                Set(n, "zoom", 1.25f);
                Set(n, "focusY", 0.45f);
            });
            foreach (var d in style == "epic" ? drops : drops.Take(1))
                Window(panels, d + bar, d + bar * 3, fadeIn: 0.15, fadeOut: 0.3);

            var iris = AddEffect(project, "wipe", "iris", n => Set(n, "iris", 1f));
            var line = AddEffect(project, "wipe", "line", n =>
            {
                Set(n, "angle", 30f);
                SetColor(n, "color", "#ffffff");
                SetColor(n, "accent", style == "epic" ? "#7cc4ff" : "#ff5fa2");
            });

            var irisCurve = new FloatCurve();
            var lineCurve = new FloatCurve();
            double half = bar * 0.3;
            for (int i = 1; i < song.Sections.Count; i++)
            {
                double t = song.Sections[i].Start;
                if (t <= 0 || drops.Contains(t)) continue;

                bool fromIntro = song.Sections[i - 1].Kind == "intro";
                Window(fromIntro ? iris : line, t - half, t + half);

                var curve = fromIntro ? irisCurve : lineCurve;
                Key(curve, t - half, 0, CurveInterp.SmoothStep);
                Key(curve, t, 0.5, CurveInterp.SmoothStep);
                Key(curve, t + half, 1, CurveInterp.Step);
            }

            report.CurveKeys += Save(project, $"effect:{iris.InstanceId}.progress", null, CurveBlend.Add, irisCurve);
            report.CurveKeys += Save(project, $"effect:{line.InstanceId}.progress", null, CurveBlend.Add, lineCurve);
        }

        project.Effects.Nodes.RemoveAll(n =>
            n.InstanceId.StartsWith(AutoPrefix, StringComparison.Ordinal)
            && AnimeFxDefIds.Contains(n.DefId) && n.Windows.Count == 0);

        foreach (var n in project.Effects.Nodes)
        {
            if (!n.InstanceId.StartsWith(AutoPrefix, StringComparison.Ordinal) || !AnimeFxDefIds.Contains(n.DefId)) continue;
            n.Windows.Sort((a, b) => a.Start.CompareTo(b.Start));
            report.EffectInstances++;
            report.EffectWindows += n.Windows.Count;
        }
    }
}
