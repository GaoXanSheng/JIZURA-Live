using JizuraLive.Engine;
using JizuraLive.Pv;
using Newtonsoft.Json;
using Xunit;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 粒子编排（M6b）的回归闸：三层粒子层的存在与种类、星光的落点爆发、
/// 不透明度曲线、相对角色层的层序、重复生成的幂等与同种子确定性。
/// 合成 BeatMap 与 <see cref="AutoDirectorTests"/> 同构：前奏 0–4、主歌 4–12、副歌 12–20、
/// 桥段 20–24、副歌 24–30、尾奏 30–32；每拍 0.5s、每 4 拍一个下拍。
/// </summary>
public class ParticleDirectorTests
{
    private const double Beat = 0.5;

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

    /// <summary>带角色层的工程：层序断言必须有角色层可参照。</summary>
    private static DirectorProject Project()
    {
        var p = new DirectorProject { LrcText = Lyrics };
        p.Scene.Layers = new List<PvLayerConfig>
        {
            new() { Id = "bg", Kind = PvLayerKind.Background },
            new() { Id = PvScene.CharacterLayerId, Kind = PvLayerKind.VisualAsset, SpoutSenderName = null },
            new() { Id = "lyrics_pv", Kind = PvLayerKind.LyricPv },
        };
        return p;
    }

    private static AutoReport Generate(DirectorProject p, string style = "dynamic", double intensity = 1.0, int seed = 1)
        => AutoDirector.Generate(p, Song(), new AutoOptions { Style = style, Intensity = intensity, Seed = seed });

    private static List<PvLayerConfig> ParticleLayers(DirectorProject p) =>
        p.Scene.Layers
            .Where(l => l.Id.StartsWith(AutoDirector.AutoLayerPrefix, StringComparison.Ordinal) && l.Particles is not null)
            .ToList();

    private static PvLayerConfig Layer(DirectorProject p, string kind) =>
        ParticleLayers(p).Single(l => l.Particles!.Kind == kind);

    private static ParamAddress Addr(string text)
    {
        Assert.True(ParamAddress.TryParse(text, out var a), text);
        return a;
    }

    private static double Opacity(DirectorProject p, string layerId, double t) =>
        DirectorEvaluator.FromSaves(p.Curves).Resolve(Addr($"layer:{layerId}.opacity"), t, 1.0);

    [Fact]
    public void GeneratesExactlyThreeParticleLayersWithCorrectKinds()
    {
        var p = Project();
        Generate(p);

        var layers = ParticleLayers(p);
        Assert.Equal(3, layers.Count);
        Assert.All(layers, l => Assert.Equal(PvLayerKind.ForegroundFx, l.Kind));
        Assert.All(layers, l => Assert.Equal(AutoDirector.AutoLayerPrefix + l.Particles!.Kind, l.Id));
        Assert.Equal(["bokeh", "sakura", "sparkle"],
            layers.Select(l => l.Particles!.Kind).OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.All(layers, l => Assert.True(l.Particles!.Seed >= 1, $"{l.Id} 的种子没有取到"));
    }

    [Fact]
    public void SparkleBursts_ContainEveryHighSectionDrop()
    {
        var p = Project();
        Generate(p);

        var bursts = Layer(p, "sparkle").Particles!.Bursts;
        Assert.Contains(12.0, bursts);  // 副歌 12–20 落点
        Assert.Contains(24.0, bursts);  // 副歌 24–30 落点
        Assert.Contains(18.0, bursts);  // dynamic：段内每第 4 个下拍（12/14/16/18 → 第 4 个）
    }

    [Fact]
    public void OpacityCurves_StayWithinUnitRange()
    {
        var p = Project();
        Generate(p);

        var saves = p.Curves.Where(c => c.Address.StartsWith("layer:autodir_", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, saves.Count);
        foreach (var save in saves)
            Assert.All(save.Curve.Keys, k => Assert.InRange(k.Value, 0.0, 1.0));

        var eval = DirectorEvaluator.FromSaves(p.Curves);
        foreach (var id in new[] { "autodir_sakura", "autodir_sparkle", "autodir_bokeh" })
            for (double t = 0; t <= 32; t += 0.25)
                Assert.InRange(eval.Resolve(Addr($"layer:{id}.opacity"), t, 1.0), 0.0, 1.0);
    }

    [Fact]
    public void SakuraOpacity_IsBrighterInChorusThanVerse()
    {
        var p = Project();
        Generate(p);

        double verse = Opacity(p, "autodir_sakura", 8.0);    // 主歌 4–12 中点
        double chorus = Opacity(p, "autodir_sakura", 16.0);  // 副歌 12–20 中点

        Assert.True(chorus > verse, $"副歌樱花不透明度 {chorus} 没有高于主歌 {verse}");
        Assert.Equal(0.5, verse, precision: 6);
        Assert.Equal(0.85, chorus, precision: 6);
    }

    [Fact]
    public void LayerOrder_BokehBehindCharacterAndPetalsInFront()
    {
        var p = Project();
        Generate(p);

        var ids = p.Scene.Layers.Select(l => l.Id).ToList();
        int chr = ids.IndexOf(PvScene.CharacterLayerId);
        int bokeh = ids.IndexOf("autodir_bokeh");
        int sakura = ids.IndexOf("autodir_sakura");
        int sparkle = ids.IndexOf("autodir_sparkle");
        Assert.True(chr >= 0 && bokeh >= 0 && sakura >= 0 && sparkle >= 0);

        // 图层栈 index 0 在最底（PvStageCompositor 先画在下）：
        // 光斑在角色层之下（背景侧），樱花/星光在角色层之上（前景侧）
        Assert.True(bokeh < chr, $"光斑应在角色层之下（背景侧）：bokeh={bokeh}, character={chr}");
        Assert.True(sakura > chr, $"樱花应在角色层之上（前景侧）：sakura={sakura}, character={chr}");
        Assert.True(sparkle > chr, $"星光应在角色层之上（前景侧）：sparkle={sparkle}, character={chr}");
    }

    [Fact]
    public void Regeneration_StaysThreeLayersAndKeepsUserLayers()
    {
        var p = Project();
        p.Scene.Layers.Add(new PvLayerConfig { Id = "user_fx", Kind = PvLayerKind.ForegroundFx });

        Generate(p);
        Generate(p, "epic", seed: 5);

        Assert.Equal(3, ParticleLayers(p).Count);
        Assert.Contains(p.Scene.Layers, l => l.Id == "user_fx");
    }

    [Fact]
    public void Soft_HasLowerDensityAndOnlyDropBursts()
    {
        var soft = Project();
        var dynamic = Project();
        Generate(soft, "soft");
        Generate(dynamic, "dynamic");

        foreach (var kind in new[] { "sakura", "sparkle", "bokeh" })
            Assert.True(Layer(soft, kind).Particles!.Density < Layer(dynamic, kind).Particles!.Density,
                $"soft 的 {kind} 密度没有更低");

        Assert.Equal([12.0, 24.0], Layer(soft, "sparkle").Particles!.Bursts.ToArray());
    }

    [Fact]
    public void SameSeed_SerializesIdenticalLayerStack()
    {
        var a = Project();
        var b = Project();
        Generate(a, "epic", seed: 9);
        Generate(b, "epic", seed: 9);

        Assert.Equal(JsonConvert.SerializeObject(a.Scene.Layers), JsonConvert.SerializeObject(b.Scene.Layers));
    }
}
