using JizuraLive.Engine;
using JizuraLive.Pv;
using JizuraLive.Render.Effects;
using Newtonsoft.Json;
using Xunit;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 二次元后期编排（BuildAnimeFx）的回归闸：五种效果在正确的段落出现、区间自洽、
/// 色块转场靠 progress 曲线自己守住两端恒等、soft 风格不掺集中线 / 分格 / 转场，同种子逐字节可复现。
/// </summary>
public class AnimeFxDirectorTests
{
    private const double Beat = 0.5;
    private const double Bar = 2.0;

    /// <summary>第一个落点：第一个副歌段的起点（16s 主歌之后）。</summary>
    private const double Drop = 48.0;

    /// <summary>全曲时长：最后一拍 160.0 + 一拍（Song.Build 的 end 口径）。</summary>
    private const double Total = 160.5;

    /// <summary>120 BPM 4/4、80 小节的合成曲：intro 8 小节 / verse 16 / chorus 16 / verse 16 / chorus 16 / outro 8。</summary>
    private static BeatMap Song()
    {
        var beats = Enumerable.Range(0, 321).Select(i => i * Beat).ToList();
        return new BeatMap
        {
            Bpm = 120,
            Beats = beats,
            Downbeats = beats.Where((_, i) => i % 4 == 0).ToList(),
            Sections =
            [
                new Section { Start = 0, End = 16, Kind = "intro", Energy = 0.1 },
                new Section { Start = 16, End = 48, Kind = "verse", Label = "A", Energy = 0.4 },
                new Section { Start = 48, End = 80, Kind = "chorus", Label = "B", Energy = 0.9 },
                new Section { Start = 80, End = 112, Kind = "verse", Label = "A", Energy = 0.4 },
                new Section { Start = 112, End = 144, Kind = "chorus", Label = "B", Energy = 1.0 },
                new Section { Start = 144, End = 160, Kind = "outro", Energy = 0.2 },
            ],
        };
    }

    private static DirectorProject Generate(string style = "dynamic", int seed = 1)
    {
        var project = new DirectorProject();
        AutoDirector.Generate(project, Song(), new AutoOptions { Style = style, Intensity = 1.0, Seed = seed });
        return project;
    }

    private static EffectNode Node(DirectorProject p, string defId, string role) =>
        p.Effects.Nodes.Single(n => n.DefId == defId && n.InstanceId.EndsWith("_" + role, StringComparison.Ordinal));

    private static ParamAddress Addr(string text)
    {
        Assert.True(ParamAddress.TryParse(text, out var a), text);
        return a;
    }

    [Fact]
    public void AnimeFxNodes_AreAllPresent()
    {
        var ids = Generate().Effects.Nodes.Select(n => n.DefId).ToHashSet();
        foreach (var id in new[] { "speedLines", "mangaPanels", "wipe", "duotone", "letterbox" })
            Assert.Contains(id, ids);
    }

    [Fact]
    public void EveryWindow_IsOrderedAndInsideTheSong()
    {
        var nodes = Generate().Effects.Nodes
            .Where(n => n.InstanceId.StartsWith(AutoDirector.AutoPrefix, StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(nodes);

        foreach (var n in nodes)
        foreach (var w in n.Windows)
        {
            Assert.True(w.Start < w.End, $"{n.InstanceId} 的区间 {w.Start}..{w.End} 不是正长度");
            Assert.True(w.Start >= 0 && w.End <= Total, $"{n.InstanceId} 的区间 {w.Start}..{w.End} 超出 [0,{Total}]");
        }
    }

    [Fact]
    public void Duotone_SwitchesBetweenCoolVerseAndVividChorus()
    {
        var p = Generate();

        Assert.Equal(1.0, Node(p, "duotone", "cool").WindowWeight(32.0), precision: 6);   // 主歌中点
        Assert.Equal(1.0, Node(p, "duotone", "vivid").WindowWeight(64.0), precision: 6);  // 副歌中点

        // 两套色不越界
        Assert.Equal(0.0, Node(p, "duotone", "vivid").WindowWeight(32.0), precision: 6);
        Assert.Equal(0.0, Node(p, "duotone", "cool").WindowWeight(64.0), precision: 6);
    }

    [Fact]
    public void Letterbox_OnlyFramesIntroAndOutro()
    {
        var bars = Node(Generate(), "letterbox", "cinema");
        Assert.Equal(1.0, bars.WindowWeight(8.0), precision: 6);   // 前奏中点
        Assert.Equal(0.0, bars.WindowWeight(64.0), precision: 6);  // 副歌中点
    }

    [Fact]
    public void SpeedLines_BurstAtTheDrop()
    {
        var p = Generate();
        var lines = p.Effects.Nodes.Where(n => n.DefId == "speedLines").ToList();
        Assert.Contains(lines, n => n.WindowWeight(Drop + 0.1) > 0);
        // 落点之外（落点前一小节）不拉线
        Assert.All(lines, n => Assert.Equal(0.0, n.WindowWeight(Drop - Bar), precision: 6));
    }

    [Fact]
    public void MangaPanels_ArriveOneBarAfterTheDrop()
    {
        var panels = Node(Generate(), "mangaPanels", "drop");
        Assert.True(panels.WindowWeight(Drop + 2 * Bar) > 0);
        Assert.Equal(0.0, panels.WindowWeight(Drop), precision: 6);
    }

    [Fact]
    public void WipeProgress_IsHalfAtEachNonDropBoundaryAndIdentityAtBothEnds()
    {
        var p = Generate();
        var eval = DirectorEvaluator.FromSaves(p.Curves);
        var wipes = p.Effects.Nodes.Where(n => n.DefId == "wipe").ToList();
        Assert.Equal(2, wipes.Count);   // wipe_iris + wipe_line

        double half = Bar * 0.3;
        foreach (var t in new[] { 16.0, 80.0, 144.0 })
        {
            var owner = wipes.Single(n => n.WindowWeight(t) > 0);
            var addr = new ParamAddress(ParamAddressKind.Effect, owner.InstanceId, "progress");
            Assert.True(eval.TryEvaluate(addr, t, out var mid), $"{owner.InstanceId} 在 {t}s 没有 progress 曲线");
            Assert.Equal(0.5, mid, precision: 6);

            // 两端严格恒等：起点 0、终点 1（wipe 不在出场混合表里，恒等只由曲线保证）
            Assert.True(eval.TryEvaluate(addr, t - half, out var start));
            Assert.True(eval.TryEvaluate(addr, t + half, out var end));
            Assert.Equal(0.0, start, precision: 9);
            Assert.Equal(1.0, end, precision: 9);
        }

        // 前奏之后那一次走光圈实例，其余走线性；两个落点（48 / 112）上没有转场
        Assert.Equal("fx_autodir_wipe_iris", wipes.Single(n => n.WindowWeight(16.0) > 0).InstanceId);
        Assert.All(wipes, n => Assert.Equal(0.0, n.WindowWeight(Drop), precision: 6));
        Assert.All(wipes, n => Assert.Equal(0.0, n.WindowWeight(112.0), precision: 6));

        var iris = Node(p, "wipe", "iris");
        var line = Node(p, "wipe", "line");
        Assert.Equal(1f, iris.Value(EffectCatalog.Get("wipe")!.Params.First(x => x.Key == "iris")));
        Assert.True(line.Value(EffectCatalog.Get("wipe")!.Params.First(x => x.Key == "angle")) == 30f);
        Assert.Equal("#ff5fa2", line.ColorValue(EffectCatalog.Get("wipe")!.Params.First(x => x.Key == "accent")));
    }

    [Fact]
    public void EveryParamValue_StaysInsideTheCatalogRange()
    {
        var p = Generate();
        foreach (var n in p.Effects.Nodes.Where(n => n.InstanceId.StartsWith(AutoDirector.AutoPrefix, StringComparison.Ordinal)))
        {
            var def = EffectCatalog.Get(n.DefId);
            Assert.NotNull(def);
            foreach (var param in def!.Params)
            {
                if (param.Kind == EffectParamKind.Color) continue;
                Assert.InRange(n.Value(param), param.Min, param.Max);
            }
        }
    }

    [Fact]
    public void SoftStyle_SkipsTheAnimeAccents()
    {
        var p = Generate("soft");
        var ids = p.Effects.Nodes.Select(n => n.DefId).ToHashSet();
        Assert.DoesNotContain("speedLines", ids);
        Assert.DoesNotContain("mangaPanels", ids);
        Assert.DoesNotContain("wipe", ids);

        // 色调与黑边是画面质感，soft 也留
        Assert.Contains("duotone", ids);
        Assert.Contains("letterbox", ids);
        Assert.DoesNotContain(p.Curves, c => c.Address.Contains("wipe"));
    }

    [Fact]
    public void SameSeed_ProducesByteIdenticalEffectsAndCurves()
    {
        var project = new DirectorProject();
        AutoDirector.Generate(project, Song(), new AutoOptions { Style = "dynamic", Intensity = 1.0, Seed = 1 });
        var fx = EffectGraphJson.Serialize(project.Effects);
        var curves = JsonConvert.SerializeObject(project.Curves);

        AutoDirector.Generate(project, Song(), new AutoOptions { Style = "dynamic", Intensity = 1.0, Seed = 1 });

        Assert.Equal(fx, EffectGraphJson.Serialize(project.Effects));
        Assert.Equal(curves, JsonConvert.SerializeObject(project.Curves));
    }
}
