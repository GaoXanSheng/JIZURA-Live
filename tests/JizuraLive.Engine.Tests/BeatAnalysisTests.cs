using JizuraLive.Engine;
using Xunit;

namespace JizuraLive.Engine.Tests;

public class BeatAnalysisTests
{
    private const double Step = 512.0 / 48000.0; // ~0.010667s

    [Fact]
    public void SilenceReturnsEmptyBeatsAndSectionsCoversFullDuration()
    {
        const double duration = 12.0;
        int frames = (int)Math.Ceiling(duration / Step);
        var analysis = new AudioAnalysis(new float[frames], new float[frames], new float[frames], new float[frames], Step);

        var map = BeatAnalysis.Analyze(analysis, duration);

        Assert.Equal(0, map.Bpm);
        Assert.Empty(map.Beats);
        Assert.Empty(map.Peaks);
        Assert.NotEmpty(map.Sections);
        Assert.Equal(0.0, map.Sections[0].Start);
        Assert.Equal(duration, map.Sections[^1].End);
        for (int i = 0; i < map.Sections.Count - 1; i++)
            Assert.Equal(map.Sections[i].End, map.Sections[i + 1].Start);
    }

    [Fact]
    public void PureNoiseReturnsEmptyBeatsAndSectionsCoversFullDuration()
    {
        const double duration = 15.0;
        int frames = (int)Math.Ceiling(duration / Step);
        var rng = new Random(42);
        var rms = new float[frames];
        for (int i = 0; i < frames; i++)
            rms[i] = (float)(0.35 + rng.NextDouble() * 0.1);

        var analysis = new AudioAnalysis(rms, new float[frames], new float[frames], new float[frames], Step);
        var map = BeatAnalysis.Analyze(analysis, duration);

        Assert.Equal(0, map.Bpm);
        Assert.Empty(map.Beats);
        Assert.NotEmpty(map.Sections);
        Assert.Equal(0.0, map.Sections[0].Start);
        Assert.Equal(duration, map.Sections[^1].End);
    }

    [Theory]
    [InlineData(120.0, 0.15)]
    [InlineData(100.0, 0.05)]
    [InlineData(144.0, 0.20)]
    public void SyntheticPulseTrainDetectsExactBpmAndPhaseAlignment(double expectedBpm, double phase)
    {
        const double duration = 12.0;
        int frames = (int)Math.Ceiling(duration / Step);
        var rms = new float[frames];
        var bass = new float[frames];

        double beatPeriod = 60.0 / expectedBpm;
        for (double t = phase; t < duration; t += beatPeriod)
        {
            int f = (int)Math.Round(t / Step);
            if (f < frames)
            {
                rms[f] = 0.95f;
                bass[f] = 0.8f;
            }
        }

        var analysis = new AudioAnalysis(rms, bass, new float[frames], new float[frames], Step);
        var map = BeatAnalysis.Analyze(analysis, duration);

        double bpmError = Math.Abs(map.Bpm - expectedBpm) / expectedBpm;
        Assert.True(bpmError < 0.01, $"BPM error {bpmError:P2} exceeds 1% (expected {expectedBpm}, got {map.Bpm})");

        Assert.NotEmpty(map.Beats);
        for (int i = 0; i < map.Beats.Count - 1; i++)
            Assert.True(map.Beats[i] < map.Beats[i + 1], "Beats must be strictly ascending");

        foreach (var beat in map.Beats)
        {
            Assert.InRange(beat, 0.0, duration);
            double k = Math.Round((beat - phase) / beatPeriod);
            double expectedBeatTime = phase + k * beatPeriod;
            if (expectedBeatTime >= 0 && expectedBeatTime < duration)
                Assert.True(Math.Abs(beat - expectedBeatTime) < 0.03, $"Beat {beat} misaligned from expected {expectedBeatTime}");
        }
    }

    [Fact]
    public void StepSignalDetectsSectionBoundary()
    {
        const double duration = 20.0;
        int frames = (int)Math.Ceiling(duration / Step);
        var rms = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            double t = i * Step;
            rms[i] = t < 10.0 ? 0.2f : 0.85f;
        }

        var analysis = new AudioAnalysis(rms, new float[frames], new float[frames], new float[frames], Step);
        var map = BeatAnalysis.Analyze(analysis, duration);

        Assert.True(map.Sections.Count >= 2, "Expected at least 2 sections across step");
        Assert.Equal(0.0, map.Sections[0].Start);
        Assert.Equal(duration, map.Sections[^1].End);
        for (int i = 0; i < map.Sections.Count - 1; i++)
            Assert.Equal(map.Sections[i].End, map.Sections[i + 1].Start);

        bool foundStepBoundary = map.Sections.Any(s => Math.Abs(s.Start - 10.0) < 0.5 || Math.Abs(s.End - 10.0) < 0.5);
        Assert.True(foundStepBoundary, "Expected section boundary near 10.0s");

        var chorusSection = map.Sections.FirstOrDefault(s => s.Kind == "chorus");
        Assert.NotNull(chorusSection);
        Assert.True(chorusSection.Start >= 9.5);

        Assert.NotEmpty(map.Peaks);
        Assert.All(map.Peaks, p => Assert.True(p >= 9.5));
    }

    [Fact]
    public void DurationMismatchClampsToShorter()
    {
        const int frames = 600;
        double seriesDuration = frames * Step;
        var rms = new float[frames];
        Array.Fill(rms, 0.3f);
        var analysis = new AudioAnalysis(rms, new float[frames], new float[frames], new float[frames], Step);

        var mapA = BeatAnalysis.Analyze(analysis, 12.0);
        Assert.Equal(seriesDuration, mapA.Sections[^1].End, 4);

        var mapB = BeatAnalysis.Analyze(analysis, 4.0);
        Assert.Equal(4.0, mapB.Sections[^1].End, 4);
    }

    [Fact]
    public void BoundarySnapsToNearestBeat()
    {
        const double duration = 16.0;
        int frames = (int)Math.Ceiling(duration / Step);
        var rms = new float[frames];
        var bass = new float[frames];

        const double beatPeriod = 0.5;
        for (double t = 0.0; t < duration; t += beatPeriod)
        {
            int f = (int)Math.Round(t / Step);
            if (f < frames)
            {
                rms[f] = t < 8.1 ? 0.35f : 0.95f;
                bass[f] = 0.8f;
            }
        }
        for (int i = 0; i < frames; i++)
        {
            double t = i * Step;
            if (rms[i] < 0.1f) rms[i] = t < 8.1 ? 0.15f : 0.75f;
        }

        var analysis = new AudioAnalysis(rms, bass, new float[frames], new float[frames], Step);
        var map = BeatAnalysis.Analyze(analysis, duration);

        Assert.NotEmpty(map.Beats);
        Assert.True(map.Sections.Count >= 2);

        double cut = map.Sections[0].End;
        bool snappedToBeat = map.Beats.Any(b => Math.Abs(b - cut) < 1e-4);
        Assert.True(snappedToBeat, $"Section boundary {cut} should snap to a beat in Beats");
    }

    [Fact]
    public void EmptyOrInvalidInputHandledSafely()
    {
        var mapNull = BeatAnalysis.Analyze(null!, 10.0);
        Assert.Equal(0, mapNull.Bpm);
        Assert.Single(mapNull.Sections);
        Assert.Equal(10.0, mapNull.Sections[0].End);

        var mapZeroDur = BeatAnalysis.Analyze(new AudioAnalysis([], [], [], [], Step), 0.0);
        Assert.Equal(0, mapZeroDur.Bpm);
        Assert.Empty(mapZeroDur.Sections);

        var mapNegative = BeatAnalysis.Analyze(new AudioAnalysis([], [], [], [], Step), -5.0);
        Assert.Equal(0, mapNegative.Bpm);
        Assert.Empty(mapNegative.Sections);
    }
}
