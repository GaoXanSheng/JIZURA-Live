using JizuraLive.Engine;
using JizuraLive.Engine.Music;
using Xunit;

namespace JizuraLive.Engine.Tests;

public class StructureAnalyzerTests
{
    private const double BeatSeconds = 0.5;              // 120 BPM
    private const int FramesPerBeat = 4;
    private const double FrameStep = BeatSeconds / FramesPerBeat;
    private const double Duration = 40.0;
    private const int ChordSwapBeats = 2;                // 和弦每 2 拍轮换

    private static readonly int[] C = { 0, 4, 7 };
    private static readonly int[] Am = { 9, 0, 4 };
    private static readonly int[] F = { 5, 9, 0 };
    private static readonly int[] G = { 7, 11, 2 };
    private static readonly int[] Dm = { 2, 5, 9 };

    // 结构真值：段 → (拍数, 轮换和弦, 帧能量)。边界落在第 0/8/24/40/56/72/80 拍。
    private static readonly (int Beats, int[][] Chords, double Energy)[] Truth =
    {
        (8,  new[] { Dm }, 0.08),
        (16, new[] { C, Am }, 0.50),
        (16, new[] { F, G }, 0.90),
        (16, new[] { C, Am }, 0.50),
        (16, new[] { F, G }, 0.90),
        (8,  new[] { Dm }, 0.08),
    };

    private static readonly int[] TrueBoundaryBeats = { 0, 8, 24, 40, 56, 72, 80 };

    [Fact]
    public void DetectsRepeatingStructureWithLabelsAndKinds()
    {
        var (features, beats, downbeats) = Build(Truth);
        var sections = StructureAnalyzer.Analyze(features, beats, downbeats);

        Assert.InRange(sections.Count, 5, 7);

        Assert.Equal(0.0, sections[0].Start, 6);
        Assert.Equal(Duration, sections[^1].End, 6);
        for (int i = 0; i + 1 < sections.Count; i++)
            Assert.Equal(sections[i].End, sections[i + 1].Start, 6);

        var detected = new List<double> { sections[0].Start };
        foreach (var s in sections) detected.Add(s.End);
        foreach (var d in detected)
            Assert.True(TrueBoundaryBeats.Any(t => Math.Abs(t - d / BeatSeconds) <= 2),
                $"检出边界 {d:F3}s 不在任何真值边界 ±2 拍内");
        foreach (var t in TrueBoundaryBeats)
            Assert.True(detected.Any(d => Math.Abs(t - d / BeatSeconds) <= 2),
                $"真值边界第 {t} 拍附近没有检出边界");

        var b1 = At(sections, 16.0);
        var b2 = At(sections, 32.0);
        Assert.Equal(b1.Label, b2.Label);
        Assert.Equal("chorus", b1.Kind);
        Assert.Equal("chorus", b2.Kind);

        var a1 = At(sections, 8.0);
        var a2 = At(sections, 24.0);
        Assert.Equal(a1.Label, a2.Label);
        Assert.Equal("verse", a1.Kind);
        Assert.Equal("verse", a2.Kind);

        Assert.Equal("intro", sections[0].Kind);
        Assert.Equal("outro", sections[^1].Kind);

        Assert.All(sections, s => Assert.InRange(s.Energy, 0.0, 1.0));
        Assert.Equal(1.0, sections.Max(s => s.Energy), 6);
    }

    [Fact]
    public void UniformSongYieldsAtMostTwoSections()
    {
        var (features, beats, downbeats) = Build(new[]
        {
            (24, new[] { C }, 0.5),
            (24, new[] { C }, 0.5),
            (24, new[] { C }, 0.5),
        });

        var sections = StructureAnalyzer.Analyze(features, beats, downbeats);
        Assert.InRange(sections.Count, 1, 2);
        Assert.Equal(0.0, sections[0].Start, 6);
        Assert.Equal(72 * BeatSeconds, sections[^1].End, 6);

        // 无拍时退化为每 0.5 秒一格，同样不该碎成多段
        var degraded = StructureAnalyzer.Analyze(features, Array.Empty<double>(), Array.Empty<double>());
        Assert.InRange(degraded.Count, 1, 2);
        Assert.Equal(72 * BeatSeconds, degraded[^1].End, 6);
    }

    [Fact]
    public void BeatAtDurationKeepsSectionEnergyFinite()
    {
        var (features, beats, downbeats) = Build(new[]
        {
            (16, new[] { C, Am }, 0.3),
            (16, new[] { F, G }, 0.9),
        });
        beats.Add(features.Duration - features.FrameStep * 0.4);

        var sections = StructureAnalyzer.Analyze(features, beats, downbeats);

        Assert.All(sections, s => Assert.InRange(s.Energy, 0.0, 1.0));
    }

    [Fact]
    public void EmptyFeaturesYieldSingleIntroSection()
    {
        var empty = new MusicFeatures
        {
            FrameStep = FrameStep,
            Duration = 0,
            Chroma = Array.Empty<float[]>(),
            Energy = Array.Empty<float>()
        };

        var sections = StructureAnalyzer.Analyze(empty, Array.Empty<double>(), Array.Empty<double>());

        Assert.Single(sections);
        Assert.Equal("intro", sections[0].Kind);
        Assert.Equal(0.0, sections[0].Start);
        Assert.Equal(0.0, sections[0].End);
    }

    private static Section At(List<Section> sections, double t)
        => sections.First(s => t >= s.Start && t < s.End);

    private static (MusicFeatures Features, List<double> Beats, List<double> Downbeats) Build(
        (int Beats, int[][] Chords, double Energy)[] truth)
    {
        int totalBeats = truth.Sum(t => t.Beats);
        int frames = (int)Math.Round(totalBeats * BeatSeconds / FrameStep);
        var chroma = new float[frames][];
        var energy = new float[frames];
        var rng = new Random(20240517);

        int beat = 0;
        foreach (var (count, chords, level) in truth)
        {
            for (int b = 0; b < count; b++)
            {
                var chord = chords[(b / ChordSwapBeats) % chords.Length];
                for (int k = 0; k < FramesPerBeat; k++)
                {
                    int f = beat * FramesPerBeat + k;
                    var v = new double[12];
                    foreach (var pc in chord) v[pc] += 1.0;
                    for (int i = 0; i < 12; i++) v[i] += (rng.NextDouble() - 0.5) * 0.06;

                    double norm = Math.Sqrt(v.Sum(x => x * x));
                    var row = new float[12];
                    for (int i = 0; i < 12; i++) row[i] = (float)(v[i] / norm);

                    chroma[f] = row;
                    energy[f] = (float)(level + (rng.NextDouble() - 0.5) * 0.01);
                }
                beat++;
            }
        }

        var beats = new List<double>();
        for (int i = 0; i < totalBeats; i++) beats.Add(i * BeatSeconds);
        var downbeats = new List<double>();
        for (int i = 0; i < totalBeats; i += 4) downbeats.Add(i * BeatSeconds);

        var features = new MusicFeatures
        {
            FrameStep = FrameStep,
            Duration = totalBeats * BeatSeconds,
            Chroma = chroma,
            Energy = energy
        };
        return (features, beats, downbeats);
    }
}
