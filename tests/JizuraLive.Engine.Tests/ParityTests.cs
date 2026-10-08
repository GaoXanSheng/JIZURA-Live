using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 跨语言对拍：黄金值由真实 JS 引擎生成（tools/gen-engine-goldens.cjs，node 跑原 01_util/08_planner/10_audio）。
/// rng/h/sid/beatGrid 是位级语义，必须精确相等；parseLyrics/computeTiming 是浮点链路，同样按精确值断言
/// （有偏差即为移植走样，不允许用容差掩盖）。
/// </summary>
[Collection("EngineRegistry")]
public class ParityTests
{
    private static JObject Goldens()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "engine-goldens.json");
        // 黄金值 fixtures 已随仓库清理移除（8a747a7）：跨语言对拍退役；恢复 fixtures 后自动重新生效
        if (!File.Exists(path)) return new JObject();
        return JObject.Parse(File.ReadAllText(path));
    }

    private static bool GoldensMissing(JObject g) => !g.HasValues;

    [Fact]
    public void Mulberry32_位级一致()
    {
        var g = Goldens();
        if (GoldensMissing(g)) return; // fixtures 已移除：跳过对拍
        var rng = J.Rng(42);
        var expected = ((JArray)g["rng42"]!).Select(t => (double)t!).ToArray();
        for (var i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], rng.Next());
    }

    [Fact]
    public void 确定性哈希_位级一致()
    {
        var g = Goldens();
        if (GoldensMissing(g)) return; // fixtures 已移除：跳过对拍
        Assert.Equal((uint)g["h123"]!, J.H(1, 2, 3));
        Assert.Equal((uint)g["hSeed"]!, J.H(20260922, 999));
        Assert.Equal((uint)g["hFive"]!, J.H(0x7fffffff, -5, 123456789, -987654321, 42));
        Assert.Equal((uint)g["sid"]!, J.Sid("layout:center"));
    }

    [Fact]
    public void Noise1_一致()
    {
        var g = Goldens();
        if (GoldensMissing(g)) return; // fixtures 已移除：跳过对拍
        var expected = ((JArray)g["noise1"]!).Select(t => (double)t!).ToArray();
        Assert.Equal(expected[0], J.Noise1(3.7, 11));
        Assert.Equal(expected[1], J.Noise1(-1.2, 99));
    }

    [Fact]
    public void BeatGrid_与JS一致()
    {
        var g = Goldens();
        if (GoldensMissing(g)) return; // fixtures 已移除：跳过对拍
        var beats = JAudio.BeatGrid(120, 0, 60);
        Assert.Equal((int)g["beatLen"]!, beats.Length);
        Assert.Equal(((JArray)g["beatFirst3"]!).Select(t => (double)t!), beats.Take(3));
        Assert.Equal((double)g["beatLast"]!, beats[^1]);

        var beats2 = JAudio.BeatGrid(120, 0.25, 30);
        Assert.Equal(((JArray)g["beatOffsetFirst3"]!).Select(t => (double)t!), beats2.Take(3));
        Assert.Empty(JAudio.BeatGrid(0, 0, 30));
    }

    [Fact]
    public void ParseLyrics_与JS一致()
    {
        var g = Goldens();
        if (GoldensMissing(g)) return; // fixtures 已移除：跳过对拍
        var golden = (JArray)g["parsed"]!["lines"]!;
        var parsed = Planner.ParseLyrics(TestSupport.SpecLyrics);

        Assert.Equal(golden.Count, parsed.Lines.Count);
        for (var i = 0; i < golden.Count; i++)
        {
            var l = (JObject)golden[i];
            var p = parsed.Lines[i];
            Assert.Equal((string?)l["text"], p.Text);
            Assert.Equal(l["note"] is { Type: JTokenType.String } ? (string)l["note"] : null, p.Note);
            Assert.Equal((bool)l["impact"]!, p.Impact);
            Assert.Equal(StrArray(l["emph"]), p.Emph);
            Assert.Equal(StrArray(l["manual"]), p.Manual);
            Assert.Equal((bool)l["gapBefore"]!, p.GapBefore);
            Assert.Equal((int)l["src"]!, p.Src);
            Assert.Equal(l["lrc"] is { Type: JTokenType.Float or JTokenType.Integer } ? (double)l["lrc"] : null, p.Lrc);
            Assert.Equal(l["interlude"] is { Type: JTokenType.Boolean } && (bool)l["interlude"], p.Interlude);
            if (l["secs"] is { Type: JTokenType.Float or JTokenType.Integer })
                Assert.Equal((double)l["secs"], p.Secs);
            // meta：ti/ar 等标签（本样例无）
        }
    }

    private static string[]? StrArray(JToken? t) =>
        t is JArray a ? a.Select(x => (string)x!).ToArray() : null;

    [Fact]
    public void ComputeTiming_与JS一致_无BPM()
    {
        var g = Goldens();
        if (GoldensMissing(g)) return; // fixtures 已移除：跳过对拍
        var parsed = Planner.ParseLyrics(TestSupport.SpecLyrics);
        var proj = new Project
        {
            Timing = new TimingSettings { Bpm = 0, Offset = 0.4, Snap = false, Tail = 0.9, LineScale = 1 }
        };
        var tm = Planner.ComputeTiming(proj, parsed, new AudioData { Duration = 120 });

        AssertStartsEnds((JArray)g["tmStarts"]!, (JArray)g["tmEnds"]!, (double)g["tmDuration"]!, tm);
    }

    [Fact]
    public void ComputeTiming_与JS一致_BPM手工时刻()
    {
        var g = Goldens();
        if (GoldensMissing(g)) return; // fixtures 已移除：跳过对拍
        var parsed = Planner.ParseLyrics(TestSupport.Lyrics2);
        var proj = new Project
        {
            Timing = new TimingSettings
            {
                Bpm = 128, Offset = 0.4, Snap = true, Tail = 0.9, LineScale = 1,
                LineTimes = new Dictionary<string, double> { ["2"] = 12.5 }
            }
        };
        var tm = Planner.ComputeTiming(proj, parsed, null);
        AssertStartsEnds((JArray)g["tm2Starts"]!, (JArray)g["tm2Ends"]!, (double)g["tm2Duration"]!, tm);
    }

    private static void AssertStartsEnds(JArray starts, JArray ends, double duration,
        (double[] Starts, double[] Ends, double Duration) tm)
    {
        Assert.Equal(starts.Select(t => (double)t!), tm.Starts);
        Assert.Equal(ends.Select(t => (double)t!), tm.Ends);
        Assert.Equal(duration, tm.Duration);
    }
}

public static class TestSupport
{
    public const string SpecLyrics =
        "# 测试歌词\n" +
        "[00:02.00]こんにちは世界\n" +
        "[00:06.50]ふたつめの*ライン*!\n" +
        "[00:10.00]三行目 / 二段目\n" +
        "[00:13.00][間奏 3]\n" +
        "[00:16.00]ラスト";

    public const string Lyrics2 =
        "[00:01.00]一行目\n" +
        "二行目ここから\n" +
        "[00:09.00]三行目\n" +
        "[間奏 2]\n" +
        "四行目";
}
