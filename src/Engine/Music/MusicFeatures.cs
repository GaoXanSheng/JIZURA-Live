using NAudio.Dsp;

namespace JizuraLive.Engine.Music;

public sealed class MusicFeatures
{
    public double FrameStep;
    public float[] Onset = [];
    public float[] Energy = [];
    public float[] LowEnergy = [];
    public float[][] Chroma = [];
    public float[] Mono = [];
    public int SampleRate;
    public double Duration;
}

public static class MusicFeatureExtractor
{
    private const int WindowSize = 2048;
    private const int Hop = 512;
    private const int MelBands = 40;
    private const float LowEnergyHz = 150f;
    private const float ChromaLowHz = 50f;
    private const float ChromaHighHz = 5000f;
    private const int OnsetMeanRadius = 8;

    public static MusicFeatures FromMono(ReadOnlySpan<float> mono, int sampleRate)
        => Extract(mono, sampleRate);

    public static MusicFeatures FromInterleaved(ReadOnlySpan<float> interleaved, int channels, int sampleRate)
    {
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        var frames = interleaved.Length / channels;
        var mono = new float[frames];
        for (var n = 0; n < frames; n++)
        {
            double sum = 0;
            for (var c = 0; c < channels; c++) sum += interleaved[n * channels + c];
            mono[n] = (float)(sum / channels);
        }
        return Extract(mono, sampleRate);
    }

    private static MusicFeatures Extract(ReadOnlySpan<float> mono, int sampleRate)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

        var frameStep = (double)Hop / sampleRate;
        var duration = (double)mono.Length / sampleRate;
        var features = new MusicFeatures { FrameStep = frameStep, Duration = duration, SampleRate = sampleRate };
        if (mono.Length == 0) return features;

        var frameCount = (mono.Length + Hop - 1) / Hop;

        var hann = new float[WindowSize];
        double windowEnergy = 0;
        for (var i = 0; i < WindowSize; i++)
        {
            hann[i] = (float)FastFourierTransform.HannWindow(i, WindowSize);
            windowEnergy += (double)hann[i] * hann[i];
        }

        var fft = new Complex[WindowSize];
        var m = Log2Exact(WindowSize);
        var melWeights = BuildMelFilterbank(sampleRate);
        var halfBins = WindowSize / 2;
        var lowMaxBin = (int)Math.Floor(LowEnergyHz * WindowSize / sampleRate);

        var onset = new float[frameCount];
        var energy = new float[frameCount];
        var lowEnergy = new float[frameCount];
        var chroma = new float[frameCount][];
        var flux = new float[frameCount];
        var prevLogMel = new float[MelBands];
        var logMel = new float[MelBands];
        var chromaAcc = new float[12];
        var power = new double[halfBins + 1];

        for (var j = 0; j < frameCount; j++)
        {
            var center = (long)j * Hop;
            double windowSum = 0;
            for (var i = 0; i < WindowSize; i++)
            {
                var idx = center - WindowSize / 2 + i;
                var x = idx >= 0 && idx < mono.Length ? mono[(int)idx] : 0f;
                windowSum += (double)x * x;
                var y = x * hann[i];
                fft[i].X = y;
                fft[i].Y = 0f;
            }
            energy[j] = (float)Math.Sqrt(windowSum / WindowSize);

            FastFourierTransform.FFT(true, m, fft);

            double low = 0;
            Array.Clear(chromaAcc);
            for (var k = 0; k <= halfBins; k++)
            {
                var re = (double)fft[k].X;
                var im = (double)fft[k].Y;
                var mag2 = re * re + im * im;
                power[k] = mag2 / windowEnergy;
                if (k <= lowMaxBin) low += mag2;

                var freq = k * (double)sampleRate / WindowSize;
                if (freq < ChromaLowHz || freq > ChromaHighHz || k == 0) continue;
                var pc = (int)Math.Round(12.0 * Math.Log2(freq / 440.0) + 69.0) % 12;
                if (pc < 0) pc += 12;
                chromaAcc[pc] += (float)Math.Sqrt(mag2);
            }
            lowEnergy[j] = (float)low;

            var chromaSum = 0f;
            for (var c = 0; c < 12; c++) chromaSum += chromaAcc[c] * chromaAcc[c];
            if (chromaSum > 1e-20f)
            {
                var inv = (float)(1.0 / Math.Sqrt(chromaSum));
                var row = new float[12];
                for (var c = 0; c < 12; c++) row[c] = chromaAcc[c] * inv;
                chroma[j] = row;
            }
            else
            {
                chroma[j] = new float[12];
            }

            var fluxSum = 0.0;
            for (var b = 0; b < MelBands; b++)
            {
                var row = melWeights[b];
                double melEnergy = 0;
                for (var k = 0; k <= halfBins; k++) melEnergy += row[k] * power[k];
                logMel[b] = (float)Math.Log(1.0 + 10000.0 * melEnergy);
                if (j == 0) continue;
                var d = logMel[b] - prevLogMel[b];
                if (d > 0) fluxSum += d;
            }
            flux[j] = (float)fluxSum;
            Array.Copy(logMel, prevLogMel, MelBands);
        }

        for (var j = 0; j < frameCount; j++)
        {
            var lo = Math.Max(0, j - OnsetMeanRadius);
            var hi = Math.Min(frameCount - 1, j + OnsetMeanRadius);
            double mean = 0;
            for (var k = lo; k <= hi; k++) mean += flux[k];
            mean /= hi - lo + 1;
            onset[j] = (float)Math.Max(0.0, flux[j] - mean);
        }

        features.Onset = onset;
        features.Energy = energy;
        features.LowEnergy = lowEnergy;
        features.Chroma = chroma;
        features.Mono = mono.ToArray();
        return features;
    }

    private static double[][] BuildMelFilterbank(int sampleRate)
    {
        var bins = WindowSize / 2 + 1;
        double melMin = ToMel(0.0), melMax = ToMel(sampleRate / 2.0);
        var edges = new double[MelBands + 2];
        for (var i = 0; i < edges.Length; i++)
            edges[i] = FromMel(melMin + (melMax - melMin) * i / (MelBands + 1));

        var weights = new double[MelBands][];
        for (var b = 0; b < MelBands; b++)
        {
            var row = new double[bins];
            double lo = edges[b], mid = edges[b + 1], hi = edges[b + 2];
            for (var k = 0; k < bins; k++)
            {
                var f = k * (double)sampleRate / WindowSize;
                if (f >= lo && f <= mid && mid > lo) row[k] = (f - lo) / (mid - lo);
                else if (f > mid && f <= hi && hi > mid) row[k] = (hi - f) / (hi - mid);
            }
            weights[b] = row;
        }
        return weights;
    }

    private static double ToMel(double hz) => 1127.01048 * Math.Log(1.0 + hz / 700.0);

    private static double FromMel(double mel) => 700.0 * (Math.Exp(mel / 1127.01048) - 1.0);

    private static int Log2Exact(int powerOfTwo)
    {
        var m = 0;
        while ((1 << m) < powerOfTwo) m++;
        return m;
    }
}
