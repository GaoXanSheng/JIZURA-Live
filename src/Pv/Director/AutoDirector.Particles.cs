using JizuraLive.Engine;

namespace JizuraLive.Pv;

public static partial class AutoDirector
{
    /// <summary>粒子层编排：樱花、星光（含爆发）、光斑。</summary>
    private static void BuildParticles(DirectorProject project, Song song, string style, double k, Random rng, AutoReport report)
    {
        bool soft = style == "soft";
        double densityScale = soft ? 0.6 : 1.0;

        var sakura = PvPipeline.NewParticleLayer(AutoLayerPrefix + "sakura", "sakura");
        var sparkle = PvPipeline.NewParticleLayer(AutoLayerPrefix + "sparkle", "sparkle");
        var bokeh = PvPipeline.NewParticleLayer(AutoLayerPrefix + "bokeh", "bokeh");

        sakura.Particles!.Density = 90 * (0.6 + 0.4 * k) * densityScale;
        sparkle.Particles!.Density = 45 * densityScale;
        bokeh.Particles!.Density = 22 * densityScale;

        foreach (var layer in new[] { sakura, sparkle, bokeh })
            layer.Particles!.Seed = rng.Next(1, 1 << 20);

        // 星光爆发：每个高潮段的落点；dynamic/epic 再加段内每第 4 个下拍，soft 只有落点
        var bursts = new List<double>();
        foreach (var sec in song.Sections)
        {
            if (!song.IsHigh(sec)) continue;
            bursts.Add(sec.Start);
            if (soft) continue;

            var downs = song.DownbeatsIn(sec.Start, sec.End).ToList();
            for (int i = 3; i < downs.Count; i += 4) bursts.Add(downs[i]);
        }
        bursts.Sort();
        for (int i = bursts.Count - 1; i > 0; i--)
            if (bursts[i] - bursts[i - 1] < 1e-6) bursts.RemoveAt(i);
        sparkle.Particles!.Bursts = bursts;

        double halfBar = song.BarLen * 0.5;
        int keys = 0;
        keys += Save(project, $"layer:{sakura.Id}.opacity", null, CurveBlend.Add,
            SectionOpacity(song, halfBar, sec => SakuraOpacity(song, sec)));
        keys += Save(project, $"layer:{sparkle.Id}.opacity", null, CurveBlend.Add, ConstantOpacity(0.9));
        keys += Save(project, $"layer:{bokeh.Id}.opacity", null, CurveBlend.Add,
            SectionOpacity(song, halfBar, sec => sec.Kind is "intro" or "bridge" or "outro" ? 0.7 : 0.2));
        report.CurveKeys += keys;

        // 图层栈 index 0 在最底（PvStageCompositor.Composite 按顺序画，先画在下）：
        // 光斑落在角色层之下（背景侧），樱花与星光落在角色层之上（前景侧）。
        var layers = new List<PvLayerConfig>(project.Scene.Layers);
        int chr = layers.FindIndex(l => l.Id == PvScene.CharacterLayerId);
        if (chr < 0) chr = layers.FindIndex(l => l.Kind == PvLayerKind.VisualAsset);
        if (chr >= 0)
        {
            layers.Insert(chr, bokeh);
            layers.Insert(chr + 2, sakura);
            layers.Insert(chr + 3, sparkle);
        }
        else
        {
            layers.Insert(0, bokeh);
            layers.Add(sakura);
            layers.Add(sparkle);
        }
        project.Scene.Layers = layers;

        report.Notes.Add($"粒子：樱花 {sakura.Particles.Density:F0} · 星光 {sparkle.Particles.Density:F0}（爆发 {bursts.Count}）"
                         + $" · 光斑 {bokeh.Particles.Density:F0}");
    }

    /// <summary>高潮段更亮，其余按段落种类取基准；intro/outro 收着，bridge 最淡。</summary>
    private static double SakuraOpacity(Song song, Section sec) =>
        song.IsHigh(sec) ? 0.85
        : sec.Kind switch
        {
            "verse" => 0.5,
            "intro" or "outro" => 0.35,
            "bridge" => 0.3,
            _ => 0.5,
        };

    /// <summary>整曲常量不透明度。</summary>
    private static FloatCurve ConstantOpacity(double v)
    {
        var c = new FloatCurve();
        Key(c, 0, v, CurveInterp.Linear);
        return c;
    }

    /// <summary>逐段不透明度：段边界起半小节 SmoothStep 过渡到本段值，段内保持。</summary>
    private static FloatCurve SectionOpacity(Song song, double halfBar, Func<Section, double> value)
    {
        var c = new FloatCurve();
        double prev = 0;
        bool first = true;
        foreach (var sec in song.Sections)
        {
            if (sec.End - sec.Start <= 0.05) continue;
            double v = value(sec);
            if (first) prev = v;

            Key(c, sec.Start, prev, CurveInterp.SmoothStep);
            Key(c, Math.Min(sec.Start + halfBar, sec.End), v, CurveInterp.Linear);

            prev = v;
            first = false;
        }
        return c;
    }
}
