using JizuraLive.Engine.Music;

namespace JizuraLive.Engine.Tests;

/// <summary>MusicFeatureExtractor 的纯数值测试：合成 PCM 进、逐帧特征出。</summary>
public class MusicFeaturesTests
{
    private const int Rate = 44100;

    [Fact]
    public void FrameStepIsHopOverSampleRate()
    {
        var f = MusicFeatureExtractor.FromMono(new float[Rate], Rate);
        Assert.Equal(512.0 / Rate, f.FrameStep, 12);
        Assert.Equal(1.0, f.Duration, 9);
    }

    [Fact]
    public void SilenceHasZeroEnergyAndZeroChroma()
    {
        var f = MusicFeatureExtractor.FromMono(new float[Rate], Rate);
        Assert.True(f.Energy.Length > 0);
        Assert.All(f.Energy, e => Assert.Equal(0f, e));
        Assert.All(f.LowEnergy, e => Assert.Equal(0f, e));
        Assert.All(f.Onset, o => Assert.Equal(0f, o));
        foreach (var row in f.Chroma)
            Assert.All(row, c => Assert.Equal(0f, c));
    }

    [Fact]
    public void SineA440PeaksAtChromaIndex9()
    {
        var pcm = new float[Rate * 2];
        for (var i = 0; i < pcm.Length; i++)
            pcm[i] = (float)(0.8 * Math.Sin(2 * Math.PI * 440.0 * i / Rate));

        var f = MusicFeatureExtractor.FromMono(pcm, Rate);
        var row = f.Chroma[f.Chroma.Length / 2];

        var best = 0;
        for (var c = 1; c < 12; c++)
            if (row[c] > row[best]) best = c;
        Assert.Equal(9, best);   // 440Hz = A，pc=(12·log2(f/440)+69) mod 12 = 9

        double norm = 0;
        foreach (var v in row) norm += v * v;
        Assert.Equal(1.0, Math.Sqrt(norm), 4);   // 每帧 L2 归一化
    }

    [Fact]
    public void InterleavedStereoMatchesMonoAverage()
    {
        var mono = new float[Rate];
        for (var i = 0; i < mono.Length; i++)
            mono[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 300.0 * i / Rate));

        var interleaved = new float[Rate * 2];
        for (var i = 0; i < mono.Length; i++)
        {
            interleaved[i * 2] = mono[i];
            interleaved[i * 2 + 1] = mono[i];
        }

        var a = MusicFeatureExtractor.FromMono(mono, Rate);
        var b = MusicFeatureExtractor.FromInterleaved(interleaved, 2, Rate);
        Assert.Equal(a.Energy.Length, b.Energy.Length);
        for (var j = 0; j < a.Energy.Length; j++)
        {
            Assert.Equal(a.Energy[j], b.Energy[j], 6);
            Assert.Equal(a.LowEnergy[j], b.LowEnergy[j], 4);
        }
    }
}
