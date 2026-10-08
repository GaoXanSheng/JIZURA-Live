using JizuraLive.Engine;
using NAudio.Wave;
using Xunit;

namespace JizuraLive.Engine.Tests;

public class AudioSpeedTests
{
    private sealed class SineProvider : ISampleProvider
    {
        private readonly int _totalFrames;
        private int _produced;

        public SineProvider(int sampleRate, int channels, int totalFrames)
        {
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            _totalFrames = totalFrames;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(Span<float> buffer)
        {
            var ch = WaveFormat.Channels;
            var wantFrames = buffer.Length / ch;
            var remain = _totalFrames - _produced;
            var frames = Math.Min(wantFrames, Math.Max(0, remain));
            for (var i = 0; i < frames * ch; i++)
                buffer[i] = 0.5f;
            _produced += frames;
            return frames * ch;
        }
    }

    [Fact]
    public void SpeedSampleProvider_Speed1_OutputsSameDuration()
    {
        const int sr = 48000;
        const int inFrames = 48000; // 1.0 秒
        var src = new SineProvider(sr, 2, inFrames);
        var speed = new SpeedSampleProvider(src, 1.0);

        var buf = new float[4096];
        var totalRead = 0;
        int got;
        while ((got = speed.Read(buf)) > 0)
        {
            totalRead += got;
        }

        var outFrames = totalRead / 2;
        Assert.InRange(outFrames, inFrames - 2, inFrames + 2);
    }

    [Fact]
    public void SpeedSampleProvider_Speed2_HalvesDuration()
    {
        const int sr = 48000;
        const int inFrames = 48000; // 1.0 秒
        var src = new SineProvider(sr, 2, inFrames);
        var speed = new SpeedSampleProvider(src, 2.0); // 2倍速 -> 0.5秒 = 24000帧

        var buf = new float[4096];
        var totalRead = 0;
        int got;
        while ((got = speed.Read(buf)) > 0)
        {
            totalRead += got;
        }

        var outFrames = totalRead / 2;
        Assert.InRange(outFrames, 23990, 24010);
    }

    [Fact]
    public void SpeedSampleProvider_SpeedHalf_DoublesDuration()
    {
        const int sr = 48000;
        const int inFrames = 24000; // 0.5 秒
        var src = new SineProvider(sr, 2, inFrames);
        var speed = new SpeedSampleProvider(src, 0.5); // 0.5倍速 -> 1.0秒 = 48000帧

        var buf = new float[4096];
        var totalRead = 0;
        int got;
        while ((got = speed.Read(buf)) > 0)
        {
            totalRead += got;
        }

        var outFrames = totalRead / 2;
        Assert.InRange(outFrames, 47990, 48010);
    }
}
