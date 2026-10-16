using JizuraLive.Engine.Music;

namespace JizuraLive.Engine.Tests;

/// <summary>BeatTracker 的合成音轨测试：点击音轨验 BPM/拍点，重拍音轨验下拍，Onset 拣峰验位置。</summary>
public class BeatTrackerTests
{
    private const int Rate = 44100;

    [Theory]
    [InlineData(120.0, 1.0)]
    [InlineData(90.0, 1.5)]
    [InlineData(150.0, 1.5)]
    public void ClickTrackBpmAndBeatsAreRecovered(double bpm, double tolerance)
    {
        const double seconds = 30.0;
        var result = BeatTracker.Track(MusicFeatureExtractor.FromMono(ClickTrack(bpm, seconds), Rate));

        Assert.InRange(result.Bpm, bpm - tolerance, bpm + tolerance);
        Assert.NotEmpty(result.Beats);

        var interval = 60.0 / bpm;
        int total = 0, hit = 0;
        for (var t = interval; t < seconds - 1e-9; t += interval)
        {
            total++;
            if (Nearest(result.Beats, t) <= 0.040) hit++;
        }
        Assert.True(hit >= 0.9 * total, $"{bpm}BPM: {hit}/{total} 拍点在 ±40ms 内");
    }

    [Fact]
    public void AccentedFirstBeatOfBarGivesDownbeats()
    {
        const double bpm = 120.0;
        const double seconds = 24.0;
        var result = BeatTracker.Track(MusicFeatureExtractor.FromMono(AccentedTrack(bpm, seconds), Rate));

        Assert.InRange(result.Bpm, bpm - 1.5, bpm + 1.5);
        Assert.NotEmpty(result.Downbeats);

        var interval = 60.0 / bpm;
        int total = 0, hit = 0, index = 0;
        for (var t = interval; t < seconds - 1e-9; t += interval, index++)
        {
            if (index % 4 != 0) continue;   // 重拍 = 小节首拍
            total++;
            if (Nearest(result.Downbeats, t) <= 0.040) hit++;
        }
        Assert.True(total >= 4, "合成音轨应至少 4 个小节");
        Assert.True(hit >= 0.8 * total, $"{hit}/{total} 下拍与小节首拍 ±40ms 匹配");
    }

    [Fact]
    public void OnsetsAppearNearClicks()
    {
        const double bpm = 120.0;
        const double seconds = 12.0;
        var result = BeatTracker.Track(MusicFeatureExtractor.FromMono(ClickTrack(bpm, seconds), Rate));

        Assert.NotEmpty(result.Onsets);
        Assert.Equal(result.Onsets.Count, result.OnsetStrengths.Count);
        Assert.All(result.OnsetStrengths, s => Assert.InRange(s, 0f, 1f));

        var interval = 60.0 / bpm;
        int total = 0, hit = 0;
        for (var t = interval; t < seconds - 1e-9; t += interval)
        {
            total++;
            if (Nearest(result.Onsets, t) <= 0.060) hit++;
        }
        Assert.True(hit >= 0.9 * total, $"{hit}/{total} onset 落在点击附近");
    }

    private static double Nearest(List<double> times, double t)
    {
        var best = double.MaxValue;
        foreach (var v in times) best = Math.Min(best, Math.Abs(v - t));
        return best;
    }

    /// <summary>每隔一拍一个 10ms 白噪声猝发；拍点从 interval 起（给动态规划留出前导帧）。</summary>
    private static float[] ClickTrack(double bpm, double seconds)
        => Synthesize(bpm, seconds, accentEvery: 0, bassHz: 0);

    /// <summary>4/4：小节首拍噪声振幅 2 倍并叠 80ms 60Hz 低音，用于下拍相位判定。</summary>
    private static float[] AccentedTrack(double bpm, double seconds)
        => Synthesize(bpm, seconds, accentEvery: 4, bassHz: 60.0);

    private static float[] Synthesize(double bpm, double seconds, int accentEvery, double bassHz)
    {
        var pcm = new float[(int)(Rate * seconds)];
        var rng = new Random(20261016);
        var interval = 60.0 / bpm;
        var clickSamples = (int)(Rate * 0.010);
        var clickGain = 0.8;
        var bassSamples = (int)(Rate * 0.080);

        var index = 0;
        for (var t = interval; t < seconds; t += interval, index++)
        {
            var accent = accentEvery > 0 && index % accentEvery == 0;
            var start = (int)(t * Rate);
            var gain = accent ? 2.0 * clickGain : clickGain;

            for (var i = 0; i < clickSamples; i++)
            {
                var p = start + i;
                if (p >= pcm.Length) break;
                pcm[p] += (float)(gain * (rng.NextDouble() * 2 - 1));
            }

            if (accent && bassHz > 0)
            {
                for (var i = 0; i < bassSamples; i++)
                {
                    var p = start + i;
                    if (p >= pcm.Length) break;
                    pcm[p] += (float)(0.9 * Math.Sin(2 * Math.PI * bassHz * i / Rate));
                }
            }
        }
        return pcm;
    }
}
