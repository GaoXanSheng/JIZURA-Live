using JizuraLive.Engine;
using JizuraLive.Pv;
using JizuraLive.Vts;
using Newtonsoft.Json;
using Xunit;

namespace JizuraLive.Engine.Tests;

public class PerformanceDirectorTests
{
    private const double Beat = 0.5;
    private const double Bar = 2.0;

    /// <summary>与 AutoDirectorTests 同一首合成曲：前奏 0–4、主歌 4–12、副歌 12–20、桥段 20–24、副歌 24–30、尾奏 30–32。</summary>
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

    private const double Duration = 32.0;

    private static DirectorProject Generate(string style = "dynamic", double intensity = 1.0, int seed = 1)
    {
        var project = new DirectorProject { LrcText = Lyrics };
        AutoDirector.Generate(project, Song(), new AutoOptions { Style = style, Intensity = intensity, Seed = seed });
        return project;
    }

    private static string StyleAt(DirectorProject p, double t) =>
        p.LyricStyles.Single(s => t >= s.Start && t < s.End).Style;

    private static List<VtsCue> Pulses(DirectorProject p, string target) =>
        p.VtsCues.Cues.Where(c => c.Kind == VtsCueKind.Pulse && c.Target == target).ToList();

    [Fact]
    public void LyricStyleSpans_CoverWholeSongContiguously()
    {
        var spans = Generate().LyricStyles;

        Assert.NotEmpty(spans);
        Assert.Equal(0.0, spans[0].Start, precision: 6);
        Assert.Equal(Duration, spans[^1].End, precision: 6);
        foreach (var s in spans)
        {
            Assert.False(string.IsNullOrEmpty(s.Style));
            Assert.True(s.End > s.Start);
        }
        for (int i = 1; i < spans.Count; i++)
        {
            Assert.Equal(spans[i - 1].End, spans[i].Start, precision: 6);
            Assert.NotEqual(spans[i - 1].Style, spans[i].Style);  // 相邻同样式必须已合并
        }
    }

    [Fact]
    public void LyricStyleSpans_ChorusPop_IntroTypewriter()
    {
        var p = Generate();
        Assert.Equal("Typewriter", StyleAt(p, 2.0));   // intro
        Assert.Equal("Karaoke", StyleAt(p, 8.0));      // verse
        Assert.Equal("Pop", StyleAt(p, 14.0));         // chorus
        Assert.Equal("Karaoke", StyleAt(p, 22.0));     // bridge
        Assert.Equal("Pop", StyleAt(p, 27.0));         // chorus
        Assert.Equal("Typewriter", StyleAt(p, 31.0));  // outro
    }

    [Fact]
    public void LyricStyleSpans_SoftUsesNoPop()
    {
        var soft = Generate("soft");
        Assert.DoesNotContain(soft.LyricStyles, s => s.Style == "Pop");
        Assert.Equal("Karaoke", StyleAt(soft, 14.0));
        Assert.Equal("Typewriter", StyleAt(soft, 2.0));
    }

    [Fact]
    public void Cues_AscendingAndWithinDuration()
    {
        var cues = Generate().VtsCues.Cues;

        Assert.NotEmpty(cues);
        for (int i = 1; i < cues.Count; i++)
            Assert.True(cues[i].Time >= cues[i - 1].Time, $"cue {i} 未升序");
        foreach (var c in cues)
        {
            Assert.InRange(c.Time, 0.0, Duration);
            Assert.NotEqual(VtsCueKind.Move, c.Kind);  // 演员轨不生成 Move
        }
    }

    [Fact]
    public void FaceAngleY_CountIsNonHighDownbeatsPlusHighBeats()
    {
        var map = Song();
        var downs = map.Downbeats;
        var beats = map.Beats;

        int expected = 0;
        foreach (var s in map.Sections)
        {
            bool high = s.Kind == "chorus";
            if (high)
                expected += beats.Count(b => b >= s.Start && b < s.End);
            else if (s.Kind is not ("intro" or "outro"))
                expected += downs.Count(d => d >= s.Start && d < s.End);
        }

        Assert.Equal(34, expected);  // 主歌 4 + 桥段 2 + 副歌 16 + 副歌 12
        Assert.Equal(expected, Pulses(Generate(), "FaceAngleY").Count);
    }

    [Fact]
    public void ChorusExpression_OnAndOffArePaired()
    {
        var cues = Generate().VtsCues.Cues;
        var chorus = cues.Where(c => c.Kind == VtsCueKind.Expression && c.Target == "role:chorus").ToList();

        Assert.Equal(2, chorus.Count(c => c.Value == 1));
        Assert.Equal(chorus.Count(c => c.Value == 1), chorus.Count(c => c.Value == 0));
        foreach (var on in chorus.Where(c => c.Value == 1))
            Assert.Contains(chorus, off => off.Value == 0 && off.Time > on.Time);
    }

    [Fact]
    public void BridgeExpression_OnAndOffArePaired()
    {
        var cues = Generate().VtsCues.Cues;
        var bridge = cues.Where(c => c.Kind == VtsCueKind.Expression && c.Target == "role:bridge").ToList();

        Assert.Equal(2, bridge.Count);
        Assert.Contains(bridge, c => c.Value == 1 && Math.Abs(c.Time - 20.0) < 1e-6);
        Assert.Contains(bridge, c => c.Value == 0 && Math.Abs(c.Time - 24.0) < 1e-6);
    }

    [Fact]
    public void EachDrop_HasExactlyOneHitHotkey()
    {
        var cues = Generate().VtsCues.Cues;
        var hits = cues.Where(c => c.Kind == VtsCueKind.Hotkey && c.Target == "role:hit").ToList();

        Assert.Equal(2, hits.Count);  // 12s 与 24s 两个落点
        Assert.Equal(hits.Count, hits.Select(h => h.Time).Distinct().Count());
        Assert.Contains(hits, h => Math.Abs(h.Time - 12.0) < 1e-6);
        Assert.Contains(hits, h => Math.Abs(h.Time - 24.0) < 1e-6);

        var smiles = Pulses(Generate(), "MouthSmile");
        Assert.Equal(hits.Count, smiles.Count);
        Assert.All(smiles, s => Assert.Equal(0.5, s.Value, precision: 6));
        Assert.All(smiles, s => Assert.Equal(2 * Bar, s.Duration, precision: 6));
    }

    [Fact]
    public void OutroHotkey_OneBarAfterOutroStart()
    {
        var cues = Generate().VtsCues.Cues;
        var outro = cues.Where(c => c.Kind == VtsCueKind.Hotkey && c.Target == "role:outro").ToList();

        Assert.Single(outro);
        Assert.Equal(30.0 + Bar, outro[0].Time, precision: 6);
    }

    [Fact]
    public void Sway_PulsesOnChorusAndBridgeBars()
    {
        var sway = Pulses(Generate(), "FaceAngleZ");
        Assert.Equal(9, sway.Count);  // 副歌 4 + 3、桥段 2
        Assert.All(sway, c => Assert.Equal(Bar, c.Duration, precision: 6));
        // 相邻两次方向相反
        for (int i = 1; i < sway.Count; i++)
            Assert.Equal(Math.Sign(sway[i - 1].Value), -Math.Sign(sway[i].Value));
    }

    [Fact]
    public void SoftStyle_HasSmallerPulseAmplitude()
    {
        double Peak(string style) => Pulses(Generate(style), "FaceAngleY").Max(c => Math.Abs(c.Value));

        Assert.True(Peak("soft") < Peak("dynamic"));
    }

    [Fact]
    public void SameSeed_ProducesIdenticalCueSerialization()
    {
        var a = JsonConvert.SerializeObject(Generate(seed: 7).VtsCues.Cues);
        var b = JsonConvert.SerializeObject(Generate(seed: 7).VtsCues.Cues);
        Assert.Equal(a, b);
    }
}
