namespace JizuraLive.Pv;

public static partial class AutoDirector
{
    /// <summary>本文件编排的效果（清理"一个区间都没拿到"的节点时按它辨认）。</summary>
    private static readonly HashSet<string> AnimeFxDefIds =
        ["speedLines", "mangaPanels", "wipe", "duotone", "letterbox"];

    /// <summary>二次元后期编排：集中线、色块转场、漫画分格、双色调、电影黑边。</summary>
    private static void BuildAnimeFx(DirectorProject project, Song song, Look look, string style, double k, Random rng, AutoReport report)
    {
        double fx = (0.5 + 0.5 * k) * (style == "soft" ? 0.6 : 1.0);
        double beat = song.BeatLen, bar = song.BarLen;

        // 落点：高潮段的起点（前一段不是高潮），与 BuildEffects 同一口径
        var drops = new List<double>();
        for (int i = 0; i < song.Sections.Count; i++)
            if (song.IsHigh(song.Sections[i]) && (i == 0 || !song.IsHigh(song.Sections[i - 1])))
                drops.Add(song.Sections[i].Start);

        // 双色调：主歌冷调、高潮高饱和。两套色各一个实例，区间按段落起止，半拍淡入淡出
        var cool = AddEffect(project, "duotone", "cool", n =>
        {
            Set(n, "amount", (float)(0.28 * fx));
            SetColor(n, "shadow", "#1d2b64");
            SetColor(n, "highlight", "#f8cdda");
        });
        foreach (var sec in song.Sections)
            if (!song.IsHigh(sec)) Window(cool, sec.Start, sec.End, beat / 2, beat / 2);

        var vivid = AddEffect(project, "duotone", "vivid", n =>
        {
            Set(n, "amount", (float)(0.35 * fx));
            SetColor(n, "shadow", "#3a0ca3");
            SetColor(n, "highlight", "#ff9ecf");
        });
        foreach (var sec in song.Sections)
            if (song.IsHigh(sec)) Window(vivid, sec.Start, sec.End, beat / 2, beat / 2);

        // 电影黑边：前奏与尾奏压成宽银幕，一小节淡入淡出
        var cinema = AddEffect(project, "letterbox", "cinema", n => Set(n, "amount", 1f));
        foreach (var sec in song.Sections)
            if (sec.Kind is "intro" or "outro") Window(cinema, sec.Start, sec.End, bar, bar);

        if (style != "soft")
        {
            // 集中线：落点处两小节满线，另在高潮段内每第 4 个下拍补一道轻的
            var burst = AddEffect(project, "speedLines", "drop", n => Set(n, "amount", 0.85f));
            foreach (var d in drops) Window(burst, d, d + bar * 2, fadeOut: bar);

            var dash = AddEffect(project, "speedLines", "accent", n => Set(n, "amount", 0.4f));
            foreach (var sec in song.Sections)
            {
                if (!song.IsHigh(sec)) continue;
                int i = 0;
                foreach (var d in song.DownbeatsIn(sec.Start, sec.End))
                    if (i++ % 4 == 3) Window(dash, d, d + beat / 2, fadeOut: beat / 2);
            }

            // 漫画分格：dynamic 只切第一个落点（次次都切就成了模板），epic 每个落点都切
            var panels = AddEffect(project, "mangaPanels", "drop", n =>
            {
                Set(n, "amount", 1f);
                Set(n, "count", 3f);
                Set(n, "focusY", 0.32f);
            });
            foreach (var d in style == "epic" ? drops : drops.Take(1))
                Window(panels, d + bar, d + bar * 3, fadeIn: 0.15, fadeOut: 0.3);

            // 色块转场：非落点的段落边界扫一次。wipe 不在出场混合表里——区间两端回到恒等
            // 只能靠 progress 曲线自己做到（起点 0、终点 1）。前奏出来的那一次用光圈。
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
                // Step 保持 1 到下一次边界——那里正是 ramp 的起点 0，中途取值无所谓（节点在区间外不渲染）
                Key(curve, t + half, 1, CurveInterp.Step);
            }

            report.CurveKeys += Save(project, $"effect:{iris.InstanceId}.progress", null, CurveBlend.Add, irisCurve);
            report.CurveKeys += Save(project, $"effect:{line.InstanceId}.progress", null, CurveBlend.Add, lineCurve);
        }

        // 一个区间都没拿到的节点留着就是全程常驻，正是要避免的（只认本文件生成的那几个实例）
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
