using System.IO;
using JizuraLive.App.PvStage;
using NAudio.Dsp;
using NAudio.Wave;

namespace JizuraLive.Engine;

public readonly record struct AudioAnalysisConfig(
    int WindowSize,
    int Hop,
    float BassLowHz,
    float BassHighHz,
    float MidHighHz,
    float HighCapHz,
    float DbFloor,
    int Version)
{
    public static readonly AudioAnalysisConfig Default = new(2048, 512, 20f, 250f, 4000f, 16000f, 1e-5f, 1);

    public AudioAnalysisKey Key(string contentHash, int sampleRate, int channels)
        => new(contentHash, Version, sampleRate, channels);
}

public readonly record struct AudioAnalysisKey(string ContentHash, int AnalysisVersion, int SampleRate, int Channels);

public readonly record struct AudioBands(float Rms, float Bass, float Mid, float High, bool Valid);

public class AudioAnalysis
{
    public float[] Rms { get; init; } = Array.Empty<float>();
    public float[] Bass { get; init; } = Array.Empty<float>();
    public float[] Mid { get; init; } = Array.Empty<float>();
    public float[] High { get; init; } = Array.Empty<float>();
    public double StepSeconds { get; init; }
    public int SampleRate { get; init; } = 48000;
    public int Count => Rms?.Length ?? 0;

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

    public bool Sample(double timeSeconds, out AudioBands bands)
    {
        int n = Rms.Length;
        if (n == 0)
        {
            bands = default;
            return false;
        }

        double f = timeSeconds * SampleRate / _hop;
        if (!double.IsFinite(f))
        {
            bands = default;
            return false;
        }

        if (f <= 0) f = 0;
        else if (f >= n - 1) f = n - 1;

        int j = (int)Math.Round(f);
        double frac = f - j;
        int k = frac > 0 ? Math.Min(j + 1, n - 1) : Math.Max(j - 1, 0);

        float t = (float)Math.Abs(frac);
        bands = new AudioBands(
            Lerp(Rms[j], Rms[k], t),
            Lerp(Bass[j], Bass[k], t),
            Lerp(Mid[j], Mid[k], t),
            Lerp(High[j], High[k], t),
            true);
        return true;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

public static class AudioAnalyzer
{
    public static AudioBandSeries Analyze(ReadOnlySpan<float> interleaved, int channels, int sampleRate, AudioAnalysisConfig config = default)
    {
        var analyzer = new SlidingWindowAnalyzer(channels, sampleRate, config);
        analyzer.Feed(interleaved);
        return analyzer.Finish();
    }
}

internal sealed class SlidingWindowAnalyzer
{
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly AudioAnalysisConfig _config;
    private readonly int _hop;
    private readonly int _w;
    private readonly int _half;
    private readonly int _m;
    private readonly float _highCap;
    private readonly float[] _hann;
    private readonly double _windowEnergy;
    private readonly NAudio.Dsp.Complex[] _fft;
    private readonly float[] _ring;
    private readonly float[] _carry;
    private readonly List<float> _rms = new();
    private readonly List<float> _bass = new();
    private readonly List<float> _mid = new();
    private readonly List<float> _high = new();

    private int _carryCount;
    private long _framesSeen;
    private int _nextWindow;

    internal SlidingWindowAnalyzer(int channels, int sampleRate, AudioAnalysisConfig config)
    {
        if (config.WindowSize <= 0 || config.Hop <= 0) config = AudioAnalysisConfig.Default;
        if (!IsPowerOfTwo(config.WindowSize))
            throw new ArgumentException("WindowSize 必须是 2 的幂：NAudio FFT 只接受 2^m 个点", nameof(config));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

        _channels = channels;
        _sampleRate = sampleRate;
        _config = config;
        _hop = config.Hop;
        _w = config.WindowSize;
        _half = _w / 2;
        _m = Log2Exact(_w);
        _highCap = Math.Min(config.HighCapHz, sampleRate * 0.5f);
        _hann = new float[_w];
        _fft = new NAudio.Dsp.Complex[_w];
        _ring = new float[_w * channels];
        _carry = new float[channels];

        double windowEnergy = 0;
        for (int i = 0; i < _w; i++)
        {
            double v = FastFourierTransform.HannWindow(i, _w);
            _hann[i] = (float)v;
            windowEnergy += v * v;
        }

        _windowEnergy = windowEnergy;
    }

    internal void Feed(ReadOnlySpan<float> interleaved)
    {
        var i = 0;

        if (_carryCount > 0)
        {
            while (_carryCount < _channels && i < interleaved.Length) _carry[_carryCount++] = interleaved[i++];
            if (_carryCount < _channels) return;

            WriteFrame(_carry);
            _carryCount = 0;
        }

        while (interleaved.Length - i >= _channels)
        {
            WriteFrame(interleaved.Slice(i, _channels));
            i += _channels;
        }

        _carryCount = interleaved.Length - i;
        for (var k = 0; k < _carryCount; k++) _carry[k] = interleaved[i + k];
    }

    internal AudioBandSeries Finish()
    {
        if (_framesSeen > 0)
        {
            long last = _framesSeen <= _half ? 0 : (_framesSeen - _half + _hop - 1) / _hop;
            while (_nextWindow <= last) EmitWindow(_nextWindow++);
        }

        return new AudioBandSeries(_rms.ToArray(), _bass.ToArray(), _mid.ToArray(), _high.ToArray(),
            _hop, _sampleRate, _config.Version, true);
    }

    private void WriteFrame(ReadOnlySpan<float> frame)
    {
        var at = (int)(_framesSeen % _w) * _channels;
        for (var c = 0; c < _channels; c++) _ring[at + c] = frame[c];
        _framesSeen++;

        while ((long)_nextWindow * _hop + _half <= _framesSeen) EmitWindow(_nextWindow++);
    }

    private void EmitWindow(int j)
    {
        var start = (long)j * _hop - _half;
        double rmsPower = 0, bassPower = 0, midPower = 0, highPower = 0;

        for (var c = 0; c < _channels; c++)
        {
            double timePower = 0;
            for (var i = 0; i < _w; i++)
            {
                var frame = start + i;
                float x = frame >= 0 && frame < _framesSeen ? _ring[(int)(frame % _w) * _channels + c] : 0f;
                float y = x * _hann[i];
                timePower += (double)y * y;
                _fft[i].X = y;
                _fft[i].Y = 0f;
            }

            rmsPower += timePower / _windowEnergy;

            FastFourierTransform.FFT(false, _m, _fft);

            for (var k = 1; k < _w / 2; k++)
            {
                float freq = (float)(k * (double)_sampleRate / _w);
                if (freq < _config.BassLowHz || freq >= _highCap) continue;
                double re = _fft[k].X, im = _fft[k].Y;
                double p = 2.0 * (re * re + im * im) / (_w * _windowEnergy);
                if (freq < _config.BassHighHz) bassPower += p;
                else if (freq < _config.MidHighHz) midPower += p;
                else highPower += p;
            }
        }

        double inv = 1.0 / _channels;
        _rms.Add(ToLevel(Math.Sqrt(rmsPower * inv)));
        _bass.Add(ToLevel(Math.Sqrt(bassPower * inv)));
        _mid.Add(ToLevel(Math.Sqrt(midPower * inv)));
        _high.Add(ToLevel(Math.Sqrt(highPower * inv)));
    }

    private float ToLevel(double power)
    {
        double x = double.IsFinite(power) && power > _config.DbFloor ? power : _config.DbFloor;
        return (float)Math.Clamp((20.0 * Math.Log10(x) + 60.0) / 60.0, 0.0, 1.0);
    }

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

    private static int Log2Exact(int powerOfTwo)
    {
        int m = 0;
        while ((1 << m) < powerOfTwo) m++;
        return m;
    }
}

public static class AudioFileAnalyzer
{
    private const int ChunkFloats = 65536;

    public static AudioBandSeries Analyze(string path, CancellationToken ct = default)
    {
        using var reader = AudioDecoding.Open(path);
        var format = reader.WaveFormat;
        if (format.Channels <= 0 || format.SampleRate <= 0)
            throw new InvalidDataException(
                $"音频格式不可分析：{format.SampleRate}Hz/{format.Channels}ch（{path}）");

        var analyzer = new SlidingWindowAnalyzer(format.Channels, format.SampleRate, AudioAnalysisConfig.Default);
        var samples = (ISampleProvider)reader;
        var chunk = new float[ChunkFloats];

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var read = samples.Read(chunk.AsSpan());
            if (read <= 0) break;
            analyzer.Feed(chunk.AsSpan(0, read));
        }

        return analyzer.Finish();
    }
}
