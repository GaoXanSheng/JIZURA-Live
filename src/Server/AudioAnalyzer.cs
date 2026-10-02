using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace JizuraLive.Server;

/// <summary>
///     音频节拍/能量分析（与前端引擎 10_audio.js 的 J.analyzeAudio 同一套算法）。
///     前端只负责 decode 与传输，数值计算全部在这里跑——
///     同样的循环 C# 比浏览器快一个量级，且完全不影响 UI 线程。
/// </summary>
internal static class AudioAnalyzer
{
    public const int AnalyzeRate = 11025;

    public static Result Analyze(float[] mono, int sampleRate, double duration)
    {
        var len = mono.Length;

        // 能量 / 高通能量通量（50Hz hop）
        var rate = 50;
        var hop = Math.Max(1, (int)Math.Round((double)sampleRate / rate));
        var n = len / hop;
        rate = (int)Math.Round((double)sampleRate / hop);
        var energy = new float[n];

        int minLag = (int)Math.Round(rate * 60.0 / 180), maxLag = (int)Math.Round(rate * 60.0 / 70);

        var flux = ArrayPool<float>.Shared.Rent(n);
        var onset = ArrayPool<double>.Shared.Rent(n);
        var scores = ArrayPool<double>.Shared.Rent(maxLag + 2);
        var sorted = ArrayPool<float>.Shared.Rent(n);

        try
        {
            Array.Clear(onset, 0, n);
            Array.Clear(scores, 0, maxLag + 2);

            float prevHP = 0, prevX = 0;
            for (var f = 0; f < n; f++)
            {
                double e = 0, eh = 0;
                int start = f * hop, end = Math.Min(len, (f + 1) * hop);
                for (var i = start; i < end; i++)
                {
                    var x = mono[i];
                    e += x * (double)x;
                    var hp = 0.92f * (prevHP + x - prevX);
                    prevHP = hp;
                    prevX = x;
                    eh += hp * (double)hp;
                }

                energy[f] = (float)Math.Sqrt(e / hop);
                flux[f] = (float)Math.Sqrt(eh / hop);
            }

            // onset 强度：高通能量对数对局部均值的正增量
            for (var f = 1; f < n; f++)
            {
                var cur = Math.Log(1e-4 + flux[f]);
                double m = 0;
                var k = 0;
                for (var j = Math.Max(0, f - 4); j < f; j++)
                {
                    m += Math.Log(1e-4 + flux[j]);
                    k++;
                }

                onset[f] = Math.Max(0, cur - m / Math.Max(1, k));
            }

            // 自相关估 BPM（70..180），高斯先验偏置 125
            double best = 0;
            var bestLag = (int)Math.Round(rate * 0.5);
            for (var lag = minLag; lag <= maxLag; lag++)
            {
                double s = 0;
                for (var f = lag; f < n; f++) s += onset[f] * onset[f - lag];
                var bpm = 60.0 * rate / lag;
                var w = Math.Exp(-0.5 * Math.Pow(Math.Log2(bpm / 125.0) / 0.7, 2));
                s *= w;
                scores[lag] = s;
                if (s > best)
                {
                    best = s;
                    bestLag = lag;
                }
            }

            double lagF = bestLag;
            if (bestLag >= 1 && bestLag + 1 < maxLag + 2 &&
                (scores[bestLag - 1] != 0 || scores[bestLag + 1] != 0))
            {
                double a = scores[bestLag - 1], b = scores[bestLag], c = scores[bestLag + 1];
                var d = a - 2 * b + c;
                if (d != 0) lagF = bestLag + 0.5 * (a - c) / d;
            }

            var period = lagF / rate;

            // 相位
            var bestPh = 0;
            double bestPS = -1;
            for (double ph = 0; ph < lagF; ph += 0.5)
            {
                double s = 0;
                for (var t = ph; t < n; t += lagF)
                {
                    var idx = (int)Math.Round(t);
                    if (idx < n) s += onset[idx];
                }

                if (s > bestPS)
                {
                    bestPS = s;
                    bestPh = (int)ph;
                }
            }

            var beats = new List<double>((int)(duration * 60.0 / Math.Max(0.2, 60.0 / period)) + 8);
            for (var t = bestPh / (double)rate; t < duration; t += period)
                beats.Add(Math.Round(t, 4));

            // 归一化能量（95 分位）
            Array.Copy(energy, sorted, n);
            Array.Sort(sorted, 0, n);
            var p95 = n > 0 ? sorted[Math.Min(n - 1, (int)(n * 0.95))] : 1f;
            if (p95 <= 0) p95 = 1f;
            for (var f = 0; f < n; f++) energy[f] = Math.Min(1f, energy[f] / p95);

            // 音高（F0）：70..1000 Hz，与能量同帧率。静音/无周期帧为 0
            var pitch = EstimatePitch(mono, sampleRate, energy, hop, n, len);

            // 时间轴波形峰
            const int bins = 1600;
            var peaks = new float[bins];
            var per = Math.Max(1, len / bins);
            for (var b = 0; b < bins; b++)
            {
                float m = 0;
                int start = b * per, end = Math.Min(len, (b + 1) * per);
                for (var i = start; i < end; i += 4)
                {
                    var v = Math.Abs(mono[i]);
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
                Pitch = pitch,
                PitchRate = rate
            };
        }
        finally
        {
            ArrayPool<float>.Shared.Return(flux, true);
            ArrayPool<double>.Shared.Return(onset, true);
            ArrayPool<double>.Shared.Return(scores, true);
            ArrayPool<float>.Shared.Return(sorted, true);
        }
    }

    /// <summary>
    ///     解析请求体：[int32 sampleRate][int32 sampleCount][float32×count 单声道]。
    ///     float 段用 MemoryMarshal.Cast 一次性重解释（x64 本就小端），免去逐样本循环。
    /// </summary>
    public static bool TryParseRequest(byte[] body, out float[] mono, out int sampleRate)
    {
        mono = Array.Empty<float>();
        sampleRate = 0;
        if (body.Length < 12) return false;
        sampleRate = BinaryPrimitives.ReadInt32LittleEndian(body);
        var count = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4));
        if (sampleRate <= 0 || count <= 0 || body.Length < 8 + count * 4) return false;
        mono = MemoryMarshal.Cast<byte, float>(body.AsSpan(8, count * 4)).ToArray();
        return true;
    }

    /// <summary>
    ///     音高（F0）检测：窗口归一化互相关（NCCF），70..1000 Hz。
    ///     窗口取 2*hop（50Hz 帧率下 40ms），保证下限频率约两个完整周期；
    ///     能量门限（归一化后 &lt;0.04）与相关性门限（&lt;0.45）之外的帧输出 0（视为无声）；
    ///     短周期峰达最高峰 88% 即优先取短周期（倍频抑制），帧间 3 值中值滤波去毛刺。
    /// </summary>
    private static float[] EstimatePitch(float[] mono, int sampleRate, float[] energy, int hop, int n, int len)
    {
        var pitch = new float[n];
        var win = 2 * hop;
        var minLag = Math.Max(2, (int)Math.Round(sampleRate / 1000.0));
        var maxLag = Math.Min(win - 4, (int)Math.Round(sampleRate / 70.0));
        if (maxLag <= minLag + 2) return pitch;

        var wbuf = ArrayPool<float>.Shared.Rent(win);
        var cc = ArrayPool<double>.Shared.Rent(maxLag + 2);
        try
        {
            for (var f = 0; f < n; f++)
            {
                if (energy[f] < 0.04f) continue; // 静音帧：pitch 保持 0
                var start = f * hop;
                if (start + win > len) start = len - win;
                if (start < 0) break;

                double mean = 0;
                for (var i = 0; i < win; i++) mean += mono[start + i];
                mean /= win;
                double e0 = 0;
                for (var i = 0; i < win; i++)
                {
                    var v = mono[start + i] - (float)mean;
                    wbuf[i] = v;
                    e0 += v * (double)v;
                }

                if (e0 < 1e-9) continue;

                double bestC = 0;
                var bestLag = -1;
                for (var lag = minLag; lag <= maxLag; lag++)
                {
                    double dot = 0, e1 = 0;
                    for (var i = lag; i < win; i++)
                    {
                        dot += wbuf[i] * (double)wbuf[i - lag];
                        e1 += wbuf[i - lag] * (double)wbuf[i - lag];
                    }

                    var c = e1 > 1e-12 ? dot / Math.Sqrt(e0 * e1) : 0;
                    cc[lag] = c;
                    if (c > bestC)
                    {
                        bestC = c;
                        bestLag = lag;
                    }
                }

                if (bestLag < 0 || bestC < 0.45) continue;

                // 倍频抑制：短周期（高八度）峰接近最高峰时优先取短周期
                var chosen = bestLag;
                var thresh = bestC * 0.88;
                for (var lag = minLag + 1; lag < bestLag; lag++)
                    if (cc[lag] >= thresh && cc[lag] >= cc[lag - 1] && cc[lag] >= cc[lag + 1])
                    {
                        chosen = lag;
                        break;
                    }

                // 抛物线插值细化周期
                double lagF = chosen;
                if (chosen > minLag && chosen < maxLag)
                {
                    double a = cc[chosen - 1], b = cc[chosen], c2 = cc[chosen + 1];
                    var d = a - 2 * b + c2;
                    if (d != 0) lagF = chosen + 0.5 * (a - c2) / d;
                }

                pitch[f] = (float)(sampleRate / lagF);
            }

            // 3 帧中值滤波：只对有声帧生效（零帧=无声，不外推假音高）
            var smoothed = new float[n];
            for (var f = 0; f < n; f++)
            {
                if (pitch[f] <= 0) continue;
                var a = f > 0 ? pitch[f - 1] : pitch[f];
                var b = pitch[f];
                var c3 = f < n - 1 ? pitch[f + 1] : pitch[f];
                if (a > 0 && c3 > 0)
                {
                    if (a > b) (a, b) = (b, a);
                    if (b > c3) (b, c3) = (c3, b);
                    if (a > b) (a, b) = (b, a);
                    smoothed[f] = b;
                }
                else
                {
                    smoothed[f] = b;
                }
            }

            return smoothed;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(wbuf, true);
            ArrayPool<double>.Shared.Return(cc, true);
        }
    }

    /// <summary>
    ///     原始音频文件（mp3/flac/m4a/wav…，MediaFoundation 支持的格式）→ 单声道 11025Hz float[]。
    ///     前端不再自己 decode+重采样+拷 40MB PCM，整文件直接丢过来。
    /// </summary>
    public static float[] DecodeMonoAnalyzeRate(string filePath, out double duration)
    {
        using var mf = new MediaFoundationReader(filePath);
        using var resampler = new MediaFoundationResampler(mf, new WaveFormat(AnalyzeRate, 16, 1));
        var sp = resampler.ToSampleProvider();
        var chunks = new List<float[]>();
        var total = 0;
        while (true)
        {
            var buf = new float[AnalyzeRate * 10]; // 每次读 10 秒
            var read = sp.Read(buf);
            if (read <= 0) break;
            Array.Resize(ref buf, read);
            chunks.Add(buf);
            total += read;
            if (total > AnalyzeRate * 60 * 30) break; // 30 分钟上限
        }

        var all = new float[total];
        var off = 0;
        foreach (var c in chunks)
        {
            Array.Copy(c, 0, all, off, c.Length);
            off += c.Length;
        }

        duration = total / (double)AnalyzeRate;
        return all;
    }

    public sealed class Result
    {
        public double Bpm { get; init; }
        public double[] Beats { get; init; } = Array.Empty<double>();
        public float[] Energy { get; init; } = Array.Empty<float>();
        public int EnergyRate { get; init; }
        public float[] Peaks { get; init; } = Array.Empty<float>();
        public float[] Pitch { get; init; } = Array.Empty<float>();
        public int PitchRate { get; init; }
    }
}