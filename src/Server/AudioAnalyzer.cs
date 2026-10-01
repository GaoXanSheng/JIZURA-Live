using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace JizuraLive.Server;

/// <summary>
/// 音频节拍/能量分析（与前端引擎 10_audio.js 的 J.analyzeAudio 同一套算法）。
/// 前端只负责 decode 与传输，数值计算全部在这里跑——
/// 同样的循环 C# 比浏览器快一个量级，且完全不影响 UI 线程。
/// </summary>
internal static class AudioAnalyzer
{
    public static Result Analyze(float[] mono, int sampleRate, double duration)
    {
        int len = mono.Length;

        // 能量 / 高通能量通量（50Hz hop）
        int rate = 50;
        int hop = Math.Max(1, (int)Math.Round((double)sampleRate / rate));
        int n = len / hop;
        rate = (int)Math.Round((double)sampleRate / hop);
        var energy = new float[n];
        var flux = new float[n];
        float prevHP = 0, prevX = 0;
        for (int f = 0; f < n; f++)
        {
            double e = 0, eh = 0;
            int start = f * hop, end = Math.Min(len, (f + 1) * hop);
            for (int i = start; i < end; i++)
            {
                float x = mono[i];
                e += x * (double)x;
                float hp = 0.92f * (prevHP + x - prevX);
                prevHP = hp;
                prevX = x;
                eh += hp * (double)hp;
            }

            energy[f] = (float)Math.Sqrt(e / hop);
            flux[f] = (float)Math.Sqrt(eh / hop);
        }

        // onset 强度：高通能量对数对局部均值的正增量
        var onset = new double[n];
        for (int f = 1; f < n; f++)
        {
            double cur = Math.Log(1e-4 + flux[f]);
            double m = 0;
            int k = 0;
            for (int j = Math.Max(0, f - 4); j < f; j++)
            {
                m += Math.Log(1e-4 + flux[j]);
                k++;
            }

            onset[f] = Math.Max(0, cur - m / Math.Max(1, k));
        }

        // 自相关估 BPM（70..180），高斯先验偏置 125
        int minLag = (int)Math.Round(rate * 60.0 / 180), maxLag = (int)Math.Round(rate * 60.0 / 70);
        double best = 0;
        int bestLag = (int)Math.Round(rate * 0.5);
        var scores = new double[maxLag + 2];
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double s = 0;
            for (int f = lag; f < n; f++) s += onset[f] * onset[f - lag];
            double bpm = 60.0 * rate / lag;
            double w = Math.Exp(-0.5 * Math.Pow(Math.Log2(bpm / 125.0) / 0.7, 2));
            s *= w;
            scores[lag] = s;
            if (s > best)
            {
                best = s;
                bestLag = lag;
            }
        }

        double lagF = bestLag;
        if (bestLag >= 1 && bestLag + 1 < scores.Length &&
            (scores[bestLag - 1] != 0 || scores[bestLag + 1] != 0))
        {
            double a = scores[bestLag - 1], b = scores[bestLag], c = scores[bestLag + 1];
            double d = a - 2 * b + c;
            if (d != 0) lagF = bestLag + 0.5 * (a - c) / d;
        }

        double period = lagF / rate;

        // 相位
        int bestPh = 0;
        double bestPS = -1;
        for (double ph = 0; ph < lagF; ph += 0.5)
        {
            double s = 0;
            for (double t = ph; t < n; t += lagF)
            {
                int idx = (int)Math.Round(t);
                if (idx < n) s += onset[idx];
            }

            if (s > bestPS)
            {
                bestPS = s;
                bestPh = (int)ph;
            }
        }

        var beats = new List<double>((int)(duration * 60.0 / Math.Max(0.2, 60.0 / period)) + 8);
        for (double t = bestPh / (double)rate; t < duration; t += period)
            beats.Add(Math.Round(t, 4));

        // 归一化能量（95 分位）
        var sorted = (float[])energy.Clone();
        Array.Sort(sorted);
        float p95 = sorted.Length > 0 ? sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.95))] : 1f;
        if (p95 <= 0) p95 = 1f;
        for (int f = 0; f < n; f++) energy[f] = Math.Min(1f, energy[f] / p95);

        // 时间轴波形峰
        const int bins = 1600;
        var peaks = new float[bins];
        int per = Math.Max(1, len / bins);
        for (int b = 0; b < bins; b++)
        {
            float m = 0;
            int start = b * per, end = Math.Min(len, (b + 1) * per);
            for (int i = start; i < end; i += 4)
            {
                float v = Math.Abs(mono[i]);
                if (v > m) m = v;
            }

            peaks[b] = m;
        }

        return new Result
        {
            Bpm = Math.Round(60.0 / period * 10) / 10,
            Beats = beats.ToArray(),
            Energy = energy,
            EnergyRate = rate,
            Peaks = peaks,
        };
    }

    /// <summary>解析请求体：[int32 sampleRate][int32 sampleCount][float32×count 单声道]。
    /// float 段用 MemoryMarshal.Cast 一次性重解释（x64 本就小端），免去逐样本循环。</summary>
    public static bool TryParseRequest(byte[] body, out float[] mono, out int sampleRate)
    {
        mono = Array.Empty<float>();
        sampleRate = 0;
        if (body.Length < 12) return false;
        sampleRate = BinaryPrimitives.ReadInt32LittleEndian(body);
        int count = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4));
        if (sampleRate <= 0 || count <= 0 || body.Length < 8 + count * 4) return false;
        mono = MemoryMarshal.Cast<byte, float>(body.AsSpan(8, count * 4)).ToArray();
        return true;
    }

    public sealed class Result
    {
        public double Bpm { get; init; }
        public double[] Beats { get; init; } = Array.Empty<double>();
        public float[] Energy { get; init; } = Array.Empty<float>();
        public int EnergyRate { get; init; }
        public float[] Peaks { get; init; } = Array.Empty<float>();
    }
}