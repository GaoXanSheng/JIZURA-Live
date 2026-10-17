namespace JizuraLive.Pv;

public static partial class AutoDirector
{
    private static void BuildPerformance(DirectorProject project, Song song, string style)
    {
        BuildLyricStyleSpans(project, song, style);
    }

    private static void BuildLyricStyleSpans(DirectorProject project, Song song, string style)
    {
        double duration = song.Duration;
        if (!(duration > 0.01)) return;

        string StyleAt(double t)
        {
            var sec = song.SectionAt(t);
            if (sec is null) return "Karaoke";
            if (sec.Kind is "intro" or "outro") return "Typewriter";
            if (song.IsHigh(sec)) return style == "soft" ? "Karaoke" : "Pop";
            return "Karaoke";
        }

        var cuts = new List<double> { 0, duration };
        foreach (var sec in song.Sections)
        {
            if (sec.Start > 0 && sec.Start < duration) cuts.Add(sec.Start);
            if (sec.End > 0 && sec.End < duration) cuts.Add(sec.End);
        }
        cuts.Sort();

        for (int i = 0; i < cuts.Count - 1; i++)
        {
            double a = cuts[i], b = cuts[i + 1];
            if (b - a <= 1e-9) continue;
            string s = StyleAt((a + b) * 0.5);
            if (project.LyricStyles.Count > 0 && project.LyricStyles[^1].Style == s
                && Math.Abs(project.LyricStyles[^1].End - a) < 1e-9)
                project.LyricStyles[^1].End = b;
            else
                project.LyricStyles.Add(new LyricStyleSpan { Start = a, End = b, Style = s });
        }
    }
}
