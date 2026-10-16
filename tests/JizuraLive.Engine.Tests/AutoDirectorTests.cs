using System.IO;
using JizuraLive.Engine;
using JizuraLive.Pv;
using JizuraLive.Render.Effects;
using Newtonsoft.Json;
using Xunit;

namespace JizuraLive.Engine.Tests;

public class AutoDirectorTests
{
    private const double Beat = 0.5;

    /// <summary>
    /// 120 BPM、32 秒的一首"歌"：前奏 0–4、主歌 4–12、副歌 12–20、桥段 20–24、副歌 24–30、尾奏 30–32。
    /// 每拍 0.5s、每 4 拍一个下拍（小节 2s），主歌里有 4 句歌词。
    /// </summary>
    private static BeatMap Song()
    {
        var beats = Enumerable.Range(0, 64).Select(i => i * Beat).ToList();
        return new BeatMap
        {
            Bpm = 120,
            Beats = beats,
            Downbeats = beats.Where((_, i) => i % 4 == 0).ToList(),
            Sections =
            [
                new Section { Start = 0, End = 4, Kind = "intro", Energy = 0.1 },
                new Section { Start = 4, End = 12, Kind = "verse", Label = "A", Energy = 0.4 },
                new Section { Start = 12, End = 20, Kind = "chorus", Label = "B", Energy = 0.9 },
                new Section { Start = 20, End = 24, Kind = "bridge", Label = "C", Energy = 0.3 },
                new Section { Start = 24, End = 30, Kind = "chorus", Label = "B", Energy = 1.0 },
                new Section { Start = 30, End = 32, Kind = "outro", Energy = 0.2 },
            ],
        };
    }

    private const string Lyrics = "[00:04.50]第一句\n[00:06.20]第二句\n[00:08.40]第三句\n[00:10.30]第四句\n[00:12.00]副歌";

    private static DirectorProject Generate(string style = "dynamic", double intensity = 1.0, int seed = 1,
        BeatMap? map = null, string? lrc = Lyrics)
    {
        var project = new DirectorProject { LrcText = lrc };
        AutoDirector.Generate(project, map ?? Song(), new AutoOptions { Style = style, Intensity = intensity, Seed = seed });
        return project;
    }

    private static ParamAddress Addr(string text)
    {
        Assert.True(ParamAddress.TryParse(text, out var a), text);
        return a;
    }

    private static double BaseScale(DirectorEvaluator e, double t) =>
        e.TryEvaluate(Addr("camera.scale"), t, out var v) ? v : 1.0;

    private static double Scale(DirectorEvaluator e, double t) => e.Resolve(Addr("camera.scale"), t, 1.0);

    private static EffectNode Node(DirectorProject p, string defId) =>
        p.Effects.Nodes.Single(n => n.DefId == defId && n.InstanceId.StartsWith(AutoDirector.AutoPrefix));

    [Fact]
    public void VersePush_SurvivesDownbeatPunch()
    {
        var eval = DirectorEvaluator.FromSaves(Generate().Curves);

        // 主歌段内基础镜头在缓推（旧版的下拍冲击会在每个下拍两侧插 1.0，把这段推近打平）
        Assert.True(BaseScale(eval, 11.5) > BaseScale(eval, 4.0) + 0.02);

        // 11.5s 离上一个下拍（10s）已过冲击衰减期：画面值就是基础值，冲击没有残留
        Assert.Equal(BaseScale(eval, 11.5), Scale(eval, 11.5), precision: 6);
    }

    [Fact]
    public void ChorusDownbeat_PunchMultipliesOnTopOfBase()
    {
        var eval = DirectorEvaluator.FromSaves(Generate().Curves);

        Assert.True(Scale(eval, 14.0) > BaseScale(eval, 14.0) * 1.03);
        Assert.Contains(eval.LayersOf(Addr("camera.scale")), l => l.Name == AutoDirector.BeatLayer && l.Blend == CurveBlend.Multiply);
    }

    [Fact]
    public void ChorusEntry_CutsCloserThanVerseStart()
    {
        var eval = DirectorEvaluator.FromSaves(Generate().Curves);
        Assert.True(BaseScale(eval, 12.0) > BaseScale(eval, 4.0) + 0.05);
        Assert.True(BaseScale(eval, 19.5) > BaseScale(eval, 12.0));
    }

    [Fact]
    public void TransientEffects_OnlyAppearInsideWindows()
    {
        var p = Generate();

        var flash = Node(p, "flash");
        Assert.Equal(1.0, flash.WindowWeight(12.0), precision: 6);  // 进副歌那一刻满闪
        Assert.Equal(0.0, flash.WindowWeight(8.0), precision: 6);   // 主歌中间没有

        foreach (var defId in new[] { "glitch", "chromatic", "shake", "zoomPunch", "scanline" })
            Assert.NotEmpty(Node(p, defId).Windows);

        // 常驻的只有画面质感
        foreach (var defId in new[] { "colorGrade", "bloom", "vignette" })
            Assert.Empty(Node(p, defId).Windows);

        // 桥段专属的扫描线不会出现在副歌里
        var scan = Node(p, "scanline");
        Assert.True(scan.WindowWeight(22.0) > 0);
        Assert.Equal(0.0, scan.WindowWeight(14.0), precision: 6);
    }

    [Fact]
    public void Riser_BuildsUpDuringLastBarBeforeChorus()
    {
        var zoom = Node(Generate(), "zoomPunch");
        Assert.Equal(0.0, zoom.WindowWeight(9.9), precision: 6);
        Assert.True(zoom.WindowWeight(11.0) > 0.3);
        Assert.True(zoom.WindowWeight(11.9) > zoom.WindowWeight(11.0));
        Assert.Equal(0.0, zoom.WindowWeight(12.1), precision: 6);  // 落点瞬间弹回
    }

    [Fact]
    public void Pipeline_AppliesWindowsAndFades()
    {
        var p = Generate();
        var eval = DirectorEvaluator.FromSaves(p.Curves);
        var graph = p.Effects.Clone();

        var flash = graph.Nodes.Single(n => n.DefId == "flash");
        var amount = EffectCatalog.Get("flash")!.Params.First(x => x.Key == "amount");
        var full = flash.Value(amount);

        PvPipeline.ApplyEffectCurves(graph, eval, 8.0);
        Assert.False(flash.FrameActive);

        PvPipeline.ApplyEffectCurves(graph, eval, 12.0);
        Assert.True(flash.FrameActive);
        Assert.Equal(full, flash.Value(amount), precision: 4);

        PvPipeline.ApplyEffectCurves(graph, eval, 12.3);
        Assert.True(flash.Value(amount) < full * 0.7);   // 闪白在衰减

        // 回到满闪时刻：从文档原值重新算，不受上一帧写回值影响
        PvPipeline.ApplyEffectCurves(graph, eval, 12.0);
        Assert.Equal(full, flash.Value(amount), precision: 4);
    }

    [Fact]
    public void CameraDrift_MovesOnLyricLines()
    {
        var eval = DirectorEvaluator.FromSaves(Generate().Curves);
        Assert.True(eval.Curves.TryGetValue(Addr("camera.offsetX"), out var offX));
        foreach (var t in new[] { 4.5, 6.2, 8.4, 10.3 })
            Assert.Contains(offX!.Keys, k => Math.Abs(k.Time - t) < 1e-6);

        // 相邻两句不落在同一点
        Assert.NotEqual(offX!.Evaluate(4.5), offX.Evaluate(6.2));
        // 副歌开头回中
        Assert.Equal(0.0, offX.Evaluate(12.0), precision: 6);
    }

    [Fact]
    public void WithoutLyrics_DriftFallsBackToBars()
    {
        var eval = DirectorEvaluator.FromSaves(Generate(lrc: null).Curves);
        Assert.True(eval.Curves.TryGetValue(Addr("camera.offsetX"), out var offX));
        Assert.True(offX!.Count > 4);
    }

    [Fact]
    public void RepeatedGeneration_IsIdempotentAndDeterministic()
    {
        var project = new DirectorProject { LrcText = Lyrics };
        var r1 = AutoDirector.Generate(project, Song(), new AutoOptions { Seed = 7 });
        var json1 = JsonConvert.SerializeObject(project.Curves) + EffectGraphJson.Serialize(project.Effects);

        var r2 = AutoDirector.Generate(project, Song(), new AutoOptions { Seed = 7 });
        var json2 = JsonConvert.SerializeObject(project.Curves) + EffectGraphJson.Serialize(project.Effects);

        Assert.Equal(json1, json2);
        Assert.Equal(r1.CurveKeys, r2.CurveKeys);
        Assert.Equal(r1.EffectWindows, r2.EffectWindows);
    }

    [Fact]
    public void Regeneration_KeepsUserEffectsAndTheirCurves()
    {
        var project = new DirectorProject { LrcText = Lyrics };
        project.Effects.Add("halftone");
        var userId = project.Effects.Nodes[0].InstanceId;
        var curve = new FloatCurve();
        curve.TryAdd(0, 0.5);
        project.Curves.Add(new CurveSave { Address = $"effect:{userId}.strength", Curve = curve });

        AutoDirector.Generate(project, Song(), new AutoOptions());
        AutoDirector.Generate(project, Song(), new AutoOptions { Style = "epic" });

        Assert.Contains(project.Effects.Nodes, n => n.InstanceId == userId);
        Assert.Contains(project.Curves, c => c.Address == $"effect:{userId}.strength");
        Assert.DoesNotContain(project.Effects.Nodes, n => n.DefId == "glitch");  // 换风格后上一版的点缀清干净
    }

    [Fact]
    public void SaveAndLoad_PreservesLayersAndWindows()
    {
        var project = Generate("epic");
        var path = Path.Combine(Path.GetTempPath(), $"jzpv_autodir_{Guid.NewGuid():N}.jzpv");
        try
        {
            Assert.Null(ProjectStore.Save(project, path));
            var loaded = ProjectStore.Load(path);
            Assert.Null(loaded.Fault);
            var back = loaded.Project!;

            Assert.Equal(project.Curves.Count, back.Curves.Count);
            Assert.Contains(back.Curves, c => c.Layer == AutoDirector.BeatLayer && c.Blend == CurveBlend.Multiply);
            Assert.Equal(
                project.Effects.Nodes.Sum(n => n.Windows.Count),
                back.Effects.Nodes.Sum(n => n.Windows.Count));

            var before = DirectorEvaluator.FromSaves(project.Curves);
            var after = DirectorEvaluator.FromSaves(back.Curves);
            foreach (var t in new[] { 3.0, 12.0, 14.0, 21.0, 25.5 })
                Assert.Equal(Scale(before, t), Scale(after, t), precision: 9);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData("soft")]
    [InlineData("dynamic")]
    [InlineData("epic")]
    public void EveryStyle_HasLookAndDistinctAccents(string style)
    {
        var p = Generate(style);
        var ids = p.Effects.Nodes.Select(n => n.DefId).ToHashSet();
        Assert.Contains("colorGrade", ids);
        Assert.Contains("bloom", ids);
        Assert.Contains("flash", ids);

        switch (style)
        {
            case "soft":
                Assert.DoesNotContain("glitch", ids);
                Assert.DoesNotContain("shake", ids);
                Assert.Contains("lightRays", ids);
                Assert.Contains("liquid", ids);
                break;
            case "epic":
                Assert.Contains("lightRays", ids);
                Assert.Contains("glassShatter", ids);
                Assert.DoesNotContain("glitch", ids);
                break;
            default:
                Assert.Contains("glitch", ids);
                Assert.Contains("chromatic", ids);
                break;
        }
    }

    [Fact]
    public void ZeroIntensity_LeavesCameraStill()
    {
        var eval = DirectorEvaluator.FromSaves(Generate(intensity: 0).Curves);
        for (double t = 0; t < 32; t += 0.25)
            Assert.Equal(1.0, Scale(eval, t), precision: 9);
    }

    [Fact]
    public void WithoutChorusLabel_HighEnergySectionIsTreatedAsChorus()
    {
        var map = Song();
        foreach (var s in map.Sections.Where(s => s.Kind == "chorus")) s.Kind = "verse";
        var p = Generate("epic", map: map);

        Assert.Contains(p.Effects.Nodes, n => n.DefId == "lightRays");
        Assert.Equal("Neon", p.Scene.LyricStyleName);
    }

    [Fact]
    public void EmptyAnalysis_DoesNotThrow()
    {
        var project = new DirectorProject();
        var report = AutoDirector.Generate(project, new BeatMap(), new AutoOptions());
        Assert.Contains(project.Effects.Nodes, n => n.DefId == "colorGrade");
        Assert.DoesNotContain(project.Effects.Nodes, n => n.DefId == "glitch");
        Assert.True(report.EffectInstances > 0);
    }

    [Fact]
    public void VerseOnly_UsesMinimalLyricStyle()
    {
        var map = new BeatMap
        {
            Bpm = 120,
            Beats = Enumerable.Range(0, 16).Select(i => i * Beat).ToList(),
            Sections = [new Section { Start = 0, End = 8, Kind = "verse", Energy = 0.5 }],
        };
        var p = Generate(map: map);
        Assert.Equal("Minimal", p.Scene.LyricStyleName);
    }
}
