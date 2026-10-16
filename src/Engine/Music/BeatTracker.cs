using System;
using System.Collections.Generic;

namespace JizuraLive.Engine.Music;

public sealed class BeatResult
{
    public double Bpm;
    public List<double> Beats = new();
    public List<double> Downbeats = new();
    public List<double> Onsets = new();
    public List<float> OnsetStrengths = new();
}

public static class BeatTracker
{
    private const double MinBpm = 60.0;
    private const double MaxBpm = 200.0;
    private const double PriorCenterBpm = 120.0;
    private const double DpAlpha = 100.0;
    private const int Harmonics = 4;
    private const double OnsetMinGapSeconds = 0.1;
    private const int BeatsPerBar = 4;

    public static BeatResult Track(MusicFeatures f)
    {
        var result = new BeatResult();
        if (f is null || f.Onset is null || f.Onset.Length < 4 || f.FrameStep <= 0) return result;

        var onset = f.Onset;
        var dt = f.FrameStep;

        var bpm = EstimateBpm(onset, dt);
        result.Bpm = bpm;

        if (bpm > 0)
        {
            var tau = (60.0 / bpm) / dt;
            var beatFrames = TrackBeats(onset, tau);
            TrimWeakEnds(onset, beatFrames);

            foreach (var bf in beatFrames) result.Beats.Add(bf * dt);
            result.Downbeats.AddRange(DetectDownbeats(f, beatFrames));
        }

        PickOnsets(onset, dt, result);
        return result;
    }

    /// <summary>
    /// Onset 自相关 × 以 120BPM 为心、对数域 1 个八度标准差的高斯先验，峰上抛物线插值。
    /// 相关值取前 4 个谐波滞后之和：非整数周期（如 150BPM→34.45 帧）下单个滞后会被量化到
    /// 相邻整数帧上、令 2× 周期反超，谐波求和能把基周期重新顶上来。
    /// </summary>
    private static double EstimateBpm(float[] onset, double dt)
    {
        var n = onset.Length;
        var minLag = (60.0 / MaxBpm) / dt;
        var maxLag = (60.0 / MinBpm) / dt;
        if (minLag < 1) minLag = 1;
        if (maxLag > n - 2) maxLag = n - 2;
        if (maxLag <= minLag) return 0;

        double mean = 0;
        for (var i = 0; i < n; i++) mean += onset[i];
        mean /= n;

        var d = new double[n];
        for (var i = 0; i < n; i++) d[i] = onset[i] - mean;

        // 分数滞后 + 线性插值：整数滞后在非整数周期上会错拿 2× 周期当峰
        const double step = 0.1;
        var count = (int)Math.Floor((maxLag - minLag) / step) + 1;
        var score = new double[count];
        for (var i = 0; i < count; i++)
        {
            var lag = minLag + i * step;
            double salience = 0;
            for (var m = 1; m <= Harmonics && m * lag <= n - 2; m++)
                salience += Autocorrelation(d, m * lag);
            score[i] = salience * Prior(60.0 / (lag * dt));
        }

        var best = 0;
        for (var i = 1; i < count; i++)
            if (score[i] > score[best]) best = i;
        if (score[best] <= 0) return 0;

        var y0 = score[Math.Max(best - 1, 0)];
        var y1 = score[best];
        var y2 = score[Math.Min(best + 1, count - 1)];
        var denom = y0 - 2.0 * y1 + y2;
        var delta = Math.Abs(denom) > 1e-15 ? 0.5 * (y0 - y2) / denom : 0.0;
        delta = Math.Clamp(delta, -0.5, 0.5);

        return 60.0 / ((minLag + (best + delta) * step) * dt);
    }

    private static double Autocorrelation(double[] d, double lag)
    {
        var n = d.Length;
        var last = (int)Math.Floor(n - 1 - lag);
        if (last < 0) return 0;

        double acc = 0;
        for (var t = 0; t <= last; t++)
        {
            var x = t + lag;
            var i0 = (int)x;
            var frac = x - i0;
            var v = i0 + 1 < n ? d[i0] * (1.0 - frac) + d[i0 + 1] * frac : d[i0];
            acc += d[t] * v;
        }
        return acc / (last + 1);
    }

    private static double Prior(double bpm) => Math.Exp(-0.5 * Math.Pow(Math.Log2(bpm / PriorCenterBpm), 2));

    /// <summary>Ellis 动态规划：score[t]=onset[t]+max(score[p]−α·ln((t−p)/τ)²)，回溯出拍点帧下标。</summary>
    private static List<int> TrackBeats(float[] onset, double tau)
    {
        var n = onset.Length;
        var score = new double[n];
        var back = new int[n];
        var pLoSpan = (int)Math.Round(2.0 * tau);
        var pHiSpan = (int)Math.Round(tau / 2.0);

        for (var t = 0; t < n; t++)
        {
            var bestVal = double.NegativeInfinity;
            var bestP = -1;
            var pLo = Math.Max(0, t - pLoSpan);
            var pHi = Math.Min(t - 1, t - pHiSpan);
            for (var p = pLo; p <= pHi; p++)
            {
                var ratio = (t - p) / tau;
                var penalty = DpAlpha * Math.Pow(Math.Log(ratio), 2);
                var v = score[p] - penalty;
                if (v > bestVal)
                {
                    bestVal = v;
                    bestP = p;
                }
            }
            score[t] = onset[t] + (bestP >= 0 ? bestVal : 0.0);
            back[t] = bestP;
        }

        var last = 0;
        for (var t = 1; t < n; t++)
            if (score[t] > score[last]) last = t;

        var frames = new List<int>();
        for (var cur = last; cur >= 0; cur = back[cur]) frames.Add(cur);
        frames.Reverse();
        return frames;
    }

    private static void TrimWeakEnds(float[] onset, List<int> frames)
    {
        while (frames.Count > 0 && onset[frames[0]] <= 0f) frames.RemoveAt(0);
        while (frames.Count > 0 && onset[frames[^1]] <= 0f) frames.RemoveAt(frames.Count - 1);
    }

    /// <summary>4 拍一小节：取（拍点上归一化 Onset + 归一化 LowEnergy）之和最大的相位。</summary>
    private static List<double> DetectDownbeats(MusicFeatures f, List<int> beatFrames)
    {
        var result = new List<double>();
        if (beatFrames.Count < BeatsPerBar) return result;

        var onset = f.Onset;
        var low = f.LowEnergy;
        double maxOnset = 0, maxLow = 0;
        for (var i = 0; i < onset.Length; i++) if (onset[i] > maxOnset) maxOnset = onset[i];
        for (var i = 0; i < low.Length; i++) if (low[i] > maxLow) maxLow = low[i];
        var invOnset = maxOnset > 0 ? 1.0 / maxOnset : 0.0;
        var invLow = maxLow > 0 ? 1.0 / maxLow : 0.0;

        var phaseScore = new double[BeatsPerBar];
        for (var i = 0; i < beatFrames.Count; i++)
        {
            var fr = beatFrames[i];
            var s = onset[fr] * invOnset + (fr < low.Length ? low[fr] : 0f) * invLow;
            phaseScore[i % BeatsPerBar] += s;
        }

        var bestPhase = 0;
        for (var p = 1; p < BeatsPerBar; p++)
            if (phaseScore[p] > phaseScore[bestPhase]) bestPhase = p;

        for (var i = bestPhase; i < beatFrames.Count; i += BeatsPerBar)
            result.Add(beatFrames[i] * f.FrameStep);
        return result;
    }

    /// <summary>Onset 包络自适应阈值（局部中位数+δ）拣峰，峰间隔 ≥100ms，强度归一到 0..1。</summary>
    private static void PickOnsets(float[] onset, double dt, BeatResult result)
    {
        var n = onset.Length;
        float maxEnv = 0;
        for (var i = 0; i < n; i++) if (onset[i] > maxEnv) maxEnv = onset[i];
        if (maxEnv <= 0) return;

        var radius = Math.Max(1, (int)Math.Round(OnsetMinGapSeconds / dt));
        const double delta = 0.1;
        var candidates = new List<(int Frame, double Strength)>();

        for (var t = 0; t < n; t++)
        {
            var v = onset[t] / maxEnv;
            if (v < delta) continue;

            var lo = Math.Max(0, t - radius);
            var hi = Math.Min(n - 1, t + radius);
            var median = Median(onset, lo, hi);
            if (v <= median / maxEnv + delta) continue;

            var leftOk = t == 0 || onset[t] >= onset[t - 1];
            var rightOk = t == n - 1 || onset[t] > onset[t + 1];
            if (leftOk && rightOk) candidates.Add((t, v));
        }

        var accepted = new List<(int Frame, double Strength)>();
        candidates.Sort((a, b) => b.Strength.CompareTo(a.Strength));
        foreach (var c in candidates)
        {
            var ok = true;
            foreach (var a in accepted)
            {
                if (Math.Abs(a.Frame - c.Frame) * dt < OnsetMinGapSeconds)
                {
                    ok = false;
                    break;
                }
            }
            if (ok) accepted.Add(c);
        }

        accepted.Sort((a, b) => a.Frame.CompareTo(b.Frame));
        foreach (var a in accepted)
        {
            result.Onsets.Add(a.Frame * dt);
            result.OnsetStrengths.Add((float)Math.Clamp(a.Strength, 0.0, 1.0));
        }
    }

    private static double Median(float[] values, int lo, int hi)
    {
        var count = hi - lo + 1;
        var buf = new float[count];
        Array.Copy(values, lo, buf, 0, count);
        Array.Sort(buf);
        return buf[count / 2];
    }
}
