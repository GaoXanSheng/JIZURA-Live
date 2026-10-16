using JizuraLive.Engine;
using JizuraLive.Engine.Music;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// <see cref="SongAnalyzer"/> 在合成音轨上的端到端结果（BPM / 下拍 / 段落连续）：
/// 特征、拍点、结构三条链一起验，输入只有 PCM。
/// </summary>
public class BeatAnalysisTests
{
    private const int Rate = 44100;

    [Fact]
    public void SilenceYieldsNoBeatsAndOneSectionCoveringWholeTrack()
    {
        const double duration = 12.0;
        var map = SongAnalyzer.Analyze(new float[(int)(duration * Rate)], 1, Rate);

        Assert.Equal(0.0, map.Bpm);
        Assert.Empty(map.Beats);
        Assert.Empty(map.Downbeats);
        Assert.Empty(map.Peaks);
        Assert.Single(map.Sections);
        Assert.Equal(0.0, map.Sections[0].Start);
        Assert.Equal(duration, map.Sections[^1].End, 6);
    }

    [Theory]
    [InlineData(120.0)]
    [InlineData(100.0)]
    [InlineData(150.0)]
    public void ClickTrackDetectsBpmBeatsAndDownbeats(double bpm)
    {
        const double duration = 16.0;
        var pcm = RenderTrack(bpm, duration, [(0.0, duration, 261.63, 0.1)]);

        var map = SongAnalyzer.Analyze(pcm, 1, Rate);

        var bpmError = Math.Abs(map.Bpm - bpm) / bpm;
        Assert.True(bpmError < 0.03, $"BPM 误差 {bpmError:P2} 超过 3%（期望 {bpm}，实得 {map.Bpm}）");

        var beatPeriod = 60.0 / bpm;
        Assert.True(map.Beats.Count > 0);
        for (var i = 0; i < map.Beats.Count - 1; i++)
        {
            var gap = map.Beats[i + 1] - map.Beats[i];
            Assert.True(map.Beats[i] < map.Beats[i + 1], "拍点必须严格递增");
            // 拍点落在帧网格上（hop 512），相邻间隔与拍周期的偏差不超过一帧
            Assert.True(Math.Abs(gap - beatPeriod) < 0.02, $"拍间隔 {gap:F4}s 偏离拍周期 {beatPeriod:F4}s");
        }

        double expectedBeats = duration / beatPeriod;
        Assert.True(Math.Abs(map.Beats.Count - expectedBeats) / expectedBeats < 0.1,
            $"拍数 {map.Beats.Count} 与时长×BPM/60={expectedBeats:F1} 相差超过 10%");

        // 下拍：4 拍一小节，间隔应约等于 4 个拍周期，且都落在拍点上
        Assert.NotEmpty(map.Downbeats);
        Assert.All(map.Downbeats, d => Assert.Contains(d, map.Beats));
        for (var i = 0; i < map.Downbeats.Count - 1; i++)
        {
            var gap = map.Downbeats[i + 1] - map.Downbeats[i];
            Assert.True(Math.Abs(gap - 4 * beatPeriod) < 0.15 * 4 * beatPeriod,
                $"下拍间隔 {gap:F3}s 偏离 4 拍（{4 * beatPeriod:F3}s）");
        }
    }

    [Fact]
    public void SectionsAreContiguousAndCoverTheWholeTrack()
    {
        const double duration = 36.0;
        var pcm = RenderTrack(120.0, duration,
        [
            (0.0, 12.0, 261.63, 0.10),
            (12.0, 24.0, 369.99, 0.30),
            (24.0, duration, 261.63, 0.10),
        ]);

        var map = SongAnalyzer.Analyze(pcm, 1, Rate);

        Assert.True(map.Sections.Count >= 3, $"段落数 {map.Sections.Count} 少于 3");
        Assert.Equal(0.0, map.Sections[0].Start);
        Assert.Equal(duration, map.Sections[^1].End, 6);
        for (var i = 0; i < map.Sections.Count - 1; i++)
            Assert.Equal(map.Sections[i].End, map.Sections[i + 1].Start, 6);

        foreach (var section in map.Sections)
        {
            Assert.InRange(section.Energy, 0.0, 1.0);
            Assert.InRange(section.Start, 0.0, duration);
            Assert.InRange(section.End, section.Start, duration);
            Assert.False(string.IsNullOrWhiteSpace(section.Label));
        }

        // 段平均能量按全曲归一：最响的一段必到 1.0
        Assert.Equal(1.0, map.Sections.Max(s => s.Energy), 6);
    }

    [Fact]
    public void EmptyTrackAnalyzesSafely()
    {
        var map = SongAnalyzer.Analyze([], 1, Rate);

        Assert.Equal(0.0, map.Bpm);
        Assert.Empty(map.Beats);
        Assert.Empty(map.Peaks);
        Assert.Single(map.Sections);
        Assert.Equal(0.0, map.Sections[0].End);
    }

    /// <summary>
    /// 合成音轨：段落块决定持续和弦与音量（给 chroma / 段落能量），每拍叠一个 kick、每 4 拍加重（给 onset / 下拍）。
    /// </summary>
    private static float[] RenderTrack(double bpm, double seconds, IReadOnlyList<(double From, double To, double RootHz, double Gain)> blocks)
    {
        var frames = (int)Math.Round(seconds * Rate);
        var pcm = new float[frames];

        for (var n = 0; n < frames; n++)
        {
            var t = (double)n / Rate;
            double v = 0;
            foreach (var (from, to, root, gain) in blocks)
            {
                if (t < from || t >= to) continue;
                v += gain * (Math.Sin(2 * Math.PI * root * t)
                             + 0.6 * Math.Sin(2 * Math.PI * root * 1.5 * t)
                             + 0.4 * Math.Sin(2 * Math.PI * root * 2.0 * t));
            }
            pcm[n] = (float)v;
        }

        var rng = new Random(7);
        var beat = 60.0 / bpm;
        for (var k = 0; ; k++)
        {
            var start = k * beat;
            if (start >= seconds) break;

            var amp = k % 4 == 0 ? 0.9 : 0.5;
            var offset = (int)Math.Round(start * Rate);
            var len = (int)Math.Round(0.12 * Rate);
            for (var i = 0; i < len && offset + i < frames; i++)
            {
                var tt = (double)i / Rate;
                var body = Math.Exp(-tt / 0.025) * Math.Sin(2 * Math.PI * 55 * tt);
                var click = (rng.NextDouble() * 2 - 1) * Math.Exp(-tt / 0.006) * 0.6;
                pcm[offset + i] += (float)(amp * (body + click));
            }
        }

        return pcm;
    }
}
