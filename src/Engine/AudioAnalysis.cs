using System.IO;
using JizuraLive.App.PvStage;
using NAudio.Dsp;
using NAudio.Wave;

namespace JizuraLive.Engine;

public class AudioAnalysis
{
    public float[] Rms { get; init; } = Array.Empty<float>();
    public float[] Bass { get; init; } = Array.Empty<float>();
    public float[] Mid { get; init; } = Array.Empty<float>();
    public float[] High { get; init; } = Array.Empty<float>();
    public double StepSeconds { get; init; }
    public int SampleRate { get; init; } = 48000;

    public AudioAnalysis() { }

    public AudioAnalysis(float[] rms, float[] bass, float[] mid, float[] high, double stepSeconds, int sampleRate = 48000)
    {
        Rms = rms;
        Bass = bass;
        Mid = mid;
        High = high;
        StepSeconds = stepSeconds;
        SampleRate = sampleRate;
    }
}

public sealed class AudioBandSeries : AudioAnalysis
{
    private readonly int _hop;

    internal AudioBandSeries(float[] rms, float[] bass, float[] mid, float[] high, int hop, int sampleRate, int configVersion, bool isComplete)
        : base(rms, bass, mid, high, (double)hop / sampleRate, sampleRate)
    {
        _hop = hop;
        ConfigVersion = configVersion;
        IsComplete = isComplete;
    }

    public int ConfigVersion { get; }

    public bool IsComplete { get; }

}

