using JizuraLive.Vts;

namespace JizuraLive.Pv;

public static partial class AutoDirector
{
    /// <summary>演员轨：逐段歌词样式区间 + VTS 角色 cue。</summary>
    private static void BuildPerformance(DirectorProject project, Song song, string style, double k, AutoReport report)
    {
        BuildLyricStyleSpans(project, song, style);
        BuildVtsCues(project, song, style, k);
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

        // 切点 = 段落边界；段落没盖到的地方按默认样式补齐，区间必须连续覆盖 [0, Duration]
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

    private static void BuildVtsCues(DirectorProject project, Song song, string style, double k)
    {
        double amp = (0.5 + 0.5 * k) * (style == "soft" ? 0.6 : 1.0);
        double beat = song.BeatLen, bar = song.BarLen;
        var cues = project.VtsCues.Cues;

        void Add(double t, VtsCueKind kind, string target, double value, double duration)
        {
            if (!double.IsFinite(t) || t < 0 || t > song.Duration) return;
            cues.Add(new VtsCue { Time = t, Kind = kind, Target = target, Value = value, Duration = duration });
        }

        // 落点 = 前一段不是高潮的高潮段入口
        var drops = new List<double>();
        for (int i = 0; i < song.Sections.Count; i++)
        {
            var sec = song.Sections[i];
            if (!song.IsHigh(sec)) continue;
            if (i > 0 && song.IsHigh(song.Sections[i - 1])) continue;
            drops.Add(sec.Start);
        }

        // 点头：高潮段每拍、其余段落每个下拍；前奏 / 尾奏不动
        foreach (var sec in song.Sections)
        {
            if (sec.Kind is "intro" or "outro") continue;
            double value = -(3 + 5 * sec.Energy) * amp;
            var marks = song.IsHigh(sec) ? song.BeatsIn(sec.Start, sec.End) : song.DownbeatsIn(sec.Start, sec.End);
            foreach (var t in marks) Add(t, VtsCueKind.Pulse, "FaceAngleY", value, 0.8 * beat);
        }

        // 摇摆：高潮段与桥段每小节一次，左右交替
        int sway = 0;
        foreach (var sec in song.Sections)
        {
            if (!song.IsHigh(sec) && sec.Kind != "bridge") continue;
            foreach (var d in song.DownbeatsIn(sec.Start, sec.End))
                Add(d, VtsCueKind.Pulse, "FaceAngleZ", (sway++ % 2 == 0 ? 1 : -1) * 4 * amp, bar);
        }

        foreach (var d in drops)
        {
            Add(d, VtsCueKind.Pulse, "MouthSmile", 0.5, 2 * bar);
            Add(d, VtsCueKind.Hotkey, "role:hit", 0, 0);
        }

        foreach (var sec in song.Sections)
        {
            string? role = song.IsHigh(sec) ? VtsRoles.Chorus : sec.Kind == "bridge" ? VtsRoles.Bridge : null;
            if (role is null) continue;
            Add(sec.Start, VtsCueKind.Expression, "role:" + role, 1, 0);
            Add(sec.End, VtsCueKind.Expression, "role:" + role, 0, 0);
        }

        foreach (var sec in song.Sections)
            if (sec.Kind == "outro")
                Add(sec.Start + bar, VtsCueKind.Hotkey, "role:" + VtsRoles.Outro, 0, 0);

        // OrderBy 是稳定排序：同一时刻的 cue 保持生成顺序，同种子逐字节相同
        var ordered = cues.OrderBy(c => c.Time).ToList();
        cues.Clear();
        cues.AddRange(ordered);
    }
}
