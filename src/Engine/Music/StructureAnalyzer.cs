namespace JizuraLive.Engine.Music;

/// <summary>
/// 结构分析：拍级 chroma+能量 → 自相似矩阵 → 边界打分（块内一致 − 块间相似）→ 段聚类与命名。
/// 纯函数：只读 <see cref="MusicFeatures"/>，不碰音频、播放位置与渲染状态。
///
/// 真实混音上 chroma 被鼓组与共有的调性抹平（实测同一首歌任意两拍余弦相似度均值 0.945），
/// 段间差别只剩几个百分点，所以这里对拍级特征做白化（对数压缩 + 去全曲均值）并把能量单独成项，
/// 阈值也按当首曲子自身的分布自适应，不写死"相似度 > 0.75"这类常数。
/// </summary>
public static class StructureAnalyzer
{
    private const int Half = 8;                 // 边界核半宽 → 16 拍
    private const int MinCells = 8;             // 段长下限（拍）
    private const int MaxCells = 64;            // 段长上限（拍）：过长就在间隔里按打分强制补刀，免得整曲只剩两段
    private const int AlignTol = 2;             // 段间对齐允许 ±2 拍抖动
    private const int SnapCells = 2;            // 边界吸附到下拍的最大格距
    private const double LogBeta = 20.0;        // chroma 对数压缩系数：压掉主和弦对背景音级的碾压
    private const double EnergyTolerance = 2.0; // 能量 z 差的尺度：差 2 个标准差相似度折到 e^-1
    private const double CoherenceMin = 0.30;   // 块内一致度下限：两侧各自一致才算"这里是段边界"，噪声场判不掉的那种就靠它挡住
    private const double ScoreZ = 0.8;          // 打分阈值：内部均值 + 0.8 个标准差
    private const double NoiseZ = 3.5;          // 打分阈值下限：无结构噪声的 3.5σ
    private const double ClusterZ = 0.6;        // 聚类阈值：段间相似度均值 + 0.6 个标准差
    private const double FallbackCellSeconds = 0.5;

    /// <summary>A−B 的噪声折算系数：块内/块间两个加权均值的权重平方和各自按权重和归一后相加开方。</summary>
    private static readonly double NoiseFactor = NoiseScaleFactor();

    public static List<Section> Analyze(MusicFeatures f, IReadOnlyList<double> beats, IReadOnlyList<double> downbeats)
    {
        if (f?.Chroma == null || f.Chroma.Length == 0 || !(f.FrameStep > 0))
            return Single(f?.Duration ?? 0);

        double duration = f.Duration > 0 ? f.Duration : f.Chroma.Length * f.FrameStep;
        if (!(duration > 0)) return Single(0);

        var grid = BuildGrid(beats, duration);
        int n = grid.Count - 1;
        if (n <= 0) return Single(duration);

        var chroma = CellChroma(f, grid);
        var energy = CellEnergy(f, grid);
        var sim = Similarity(chroma, energy);
        var (score, coherence) = BoundaryScore(sim);

        var bounds = PickBoundaries(score, coherence, grid, downbeats, sim);
        var segs = BuildSegments(bounds, grid, energy);
        Cluster(segs, sim);
        return Name(segs);
    }

    private static List<Section> Single(double duration)
        => new() { new Section { Start = 0, End = duration > 0 ? duration : 0, Kind = "intro", Label = "A", Energy = 0 } };

    /// <summary>边界核权重（中心在块接口上，两侧各 Half 拍）。</summary>
    private static double Weight(int offset)
    {
        var sigma = Half / 2.0;
        return Math.Exp(-(offset * offset) / (2.0 * sigma * sigma));
    }

    private static double NoiseScaleFactor()
    {
        double withinSq = 0, withinW = 0, crossSq = 0, crossW = 0;
        for (int p = -Half; p < Half; p++)
            for (int q = -Half; q < Half; q++)
            {
                if (p == q) continue;
                var w = Weight(p) * Weight(q);
                if ((p < 0) == (q < 0)) { withinSq += w * w; withinW += w; }
                else { crossSq += w * w; crossW += w; }
            }
        return Math.Sqrt(withinSq / (withinW * withinW) + crossSq / (crossW * crossW));
    }

    /// <summary>拍网格：0、各拍时刻、Duration。拍不足 2 个时退化为每 0.5 秒一格。</summary>
    private static List<double> BuildGrid(IReadOnlyList<double> beats, double duration)
    {
        var mid = new List<double>();
        if (beats != null)
            foreach (var b in beats)
                if (b > 1e-6 && b < duration - 1e-6) mid.Add(b);
        mid.Sort();

        if (mid.Count < 2)
        {
            mid.Clear();
            for (double t = FallbackCellSeconds; t < duration - 1e-6; t += FallbackCellSeconds) mid.Add(t);
        }

        var cuts = new List<double> { 0.0 };
        foreach (var t in mid)
            if (t - cuts[^1] > 1e-6) cuts.Add(t);
        if (duration - cuts[^1] > 1e-6) cuts.Add(duration);
        else cuts[^1] = duration;
        return cuts;
    }

    /// <summary>每拍区间的 chroma 均值再 L2 归一化；区间内无帧或全静音时给零向量。</summary>
    private static float[][] CellChroma(MusicFeatures f, List<double> grid)
    {
        int n = grid.Count - 1;
        int frames = f.Chroma.Length;
        var cells = new float[n][];
        for (int c = 0; c < n; c++)
        {
            int i0 = FrameAt(grid[c], f.FrameStep, frames);
            int i1 = FrameAt(grid[c + 1], f.FrameStep, frames);
            if (i1 <= i0) i1 = Math.Min(i0 + 1, frames);


            var acc = new double[12];
            for (int i = i0; i < i1; i++)
            {
                var ch = f.Chroma[i];
                if (ch == null) continue;
                int len = Math.Min(12, ch.Length);
                for (int k = 0; k < len; k++) acc[k] += ch[k];
            }

            double norm = 0;
            for (int k = 0; k < 12; k++) norm += acc[k] * acc[k];
            norm = Math.Sqrt(norm);

            var v = new float[12];
            if (norm > 1e-9)
                for (int k = 0; k < 12; k++) v[k] = (float)(acc[k] / norm);
            cells[c] = v;
        }
        return cells;
    }

    private static double[] CellEnergy(MusicFeatures f, List<double> grid)
    {
        int n = grid.Count - 1;
        var cells = new double[n];
        var levels = f.Energy;
        if (levels == null || levels.Length == 0) return cells;
        int frames = levels.Length;

        for (int c = 0; c < n; c++)
        {
            int i0 = FrameAt(grid[c], f.FrameStep, frames);
            int i1 = FrameAt(grid[c + 1], f.FrameStep, frames);
            if (i1 <= i0) i1 = Math.Min(i0 + 1, frames);

            double sum = 0;
            for (int i = i0; i < i1; i++) sum += levels[i];
            cells[c] = i1 > i0 ? sum / (i1 - i0) : (c > 0 ? cells[c - 1] : 0);
        }
        return cells;
    }

    private static int FrameAt(double t, double step, int frames)
        => Math.Clamp((int)Math.Round(t / step), 0, frames);

    /// <summary>
    /// 白化后的拍级相似度：chroma 取对数再减去全曲均值（共有的调性、鼓组造成的弥散分量被剔掉，
    /// 留下各段自己的音级差别），L2 归一化后取余弦；能量另走一条 z-score 通道并用乘积折减——
    /// 同和弦不同能量的段落（主歌/副歌）不能靠 chroma 就并成一段。相同内容 = 1，无关联 ≈ 0。
    /// </summary>
    private static float[][] Similarity(float[][] chroma, double[] energy)
    {
        int n = chroma.Length;

        var mean = new double[12];
        for (int i = 0; i < n; i++)
            for (int k = 0; k < 12; k++) mean[k] += Math.Log(1.0 + LogBeta * chroma[i][k]);
        for (int k = 0; k < 12; k++) mean[k] /= n;

        var v = new double[n][];
        for (int i = 0; i < n; i++)
        {
            v[i] = new double[12];
            double s = 0;
            for (int k = 0; k < 12; k++)
            {
                v[i][k] = Math.Log(1.0 + LogBeta * chroma[i][k]) - mean[k];
                s += v[i][k] * v[i][k];
            }
            var norm = Math.Sqrt(s);
            if (norm > 1e-9)
                for (int k = 0; k < 12; k++) v[i][k] /= norm;
        }

        var z = ZScore(energy);

        var sim = new float[n][];
        for (int i = 0; i < n; i++)
        {
            sim[i] = new float[n];
            for (int j = 0; j < n; j++)
            {
                double dot = 0;
                for (int k = 0; k < 12; k++) dot += v[i][k] * v[j][k];
                var cos = Math.Clamp(dot, 0.0, 1.0);
                var eSim = Math.Exp(-Math.Abs(z[i] - z[j]) / EnergyTolerance);
                sim[i][j] = (float)(cos * eSim);
            }
        }
        return sim;
    }

    private static double[] ZScore(double[] values)
    {
        int n = values.Length;
        var z = new double[n];
        double mean = 0;
        for (int i = 0; i < n; i++) mean += values[i];
        mean /= n;

        double variance = 0;
        for (int i = 0; i < n; i++)
        {
            var d = values[i] - mean;
            variance += d * d;
        }
        var std = Math.Sqrt(variance / n);
        if (std <= 1e-12) return z;

        for (int i = 0; i < n; i++) z[i] = (values[i] - mean) / std;
        return z;
    }

    /// <summary>
    /// 每个格子的边界打分：两侧各自内部的加权相似 A（块内一致度）减跨两侧的加权相似 B，
    /// 以及 A 本身。A 高说明两侧各自是"同一种东西"，B 低说明两侧不是同一种东西——两条都满足才是段边界。
    /// 只看核完整的内部下标：c=0 与 c=n 处核只有单象限、A/B 都只取到一半邻域，值不可比。
    /// </summary>
    private static (double[] Score, double[] Coherence) BoundaryScore(float[][] sim)
    {
        int n = sim.Length;
        var score = new double[n + 1];
        var coherence = new double[n + 1];

        for (int c = Half; c <= n - Half; c++)
        {
            double within = 0, withinW = 0;
            for (int p = -Half; p < Half; p++)
                for (int q = -Half; q < Half; q++)
                {
                    if ((p < 0) != (q < 0) || p == q) continue;
                    var w = Weight(p) * Weight(q);
                    within += w * sim[c + p][c + q];
                    withinW += w;
                }

            double cross = 0, crossW = 0;
            for (int p = -Half; p < 0; p++)
                for (int q = 0; q < Half; q++)
                {
                    var w = Weight(p) * Weight(q);
                    cross += w * sim[c + p][c + q];
                    crossW += w;
                }

            coherence[c] = withinW > 0 ? within / withinW : 0;
            score[c] = coherence[c] - (crossW > 0 ? cross / crossW : 0);
        }
        return (score, coherence);
    }

    /// <summary>
    /// 峰值拣选：块内一致度 A ≥ 0.30、打分 ≥ max(内部均值 + 0.8σ, 3.5×噪声尺度) 的局部极大，
    /// 互相至少隔 8 拍（按打分贪心），段长超过 64 拍的间隔再按打分强制补一刀。
    /// 噪声尺度由远距离相似度对（|i−j| ≥ 2×核宽）的标准差按核权重折算：无结构的曲子里那就是全部信息。
    /// </summary>
    private static List<int> PickBoundaries(double[] score, double[] coherence, List<double> grid,
        IReadOnlyList<double> downbeats, float[][] sim)
    {
        int n = grid.Count - 1;
        var bounds = new List<int> { 0, n };

        int from = MinCells, to = n - MinCells;
        if (to < from) return bounds;

        double sum = 0;
        for (int i = from; i <= to; i++) sum += score[i];
        var mean = sum / (to - from + 1);

        double variance = 0;
        for (int i = from; i <= to; i++)
        {
            var d = score[i] - mean;
            variance += d * d;
        }
        var std = Math.Sqrt(variance / (to - from + 1));
        var nullNoise = FarPairStd(sim) * NoiseFactor;
        var thr = Math.Max(mean + ScoreZ * std, NoiseZ * nullNoise);

        var cand = new List<(int Idx, double Val)>();
        for (int i = from; i <= to; i++)
        {
            if (score[i] < thr || coherence[i] < CoherenceMin) continue;
            bool peak = true;
            for (int d = 1; d <= 2 && peak; d++)
                if (score[i] <= score[i - d] || score[i] <= score[i + d]) peak = false;
            if (peak) cand.Add((i, score[i]));
        }

        cand.Sort((a, b) => b.Val.CompareTo(a.Val));
        var picked = new List<int>();
        foreach (var (idx, _) in cand)
        {
            bool ok = true;
            foreach (var p in picked)
                if (Math.Abs(p - idx) < MinCells) { ok = false; break; }
            if (ok) picked.Add(idx);
        }

        var cuts = new List<int> { 0 };
        picked.Sort();
        foreach (var idx in picked)
            if (idx - cuts[^1] >= MinCells && n - idx >= MinCells) cuts.Add(idx);
        cuts.Add(n);

        for (int i = 0; i + 1 < cuts.Count;)
        {
            if (cuts[i + 1] - cuts[i] <= MaxCells) { i++; continue; }

            int best = -1;
            var bestVal = double.NegativeInfinity;
            for (int c = cuts[i] + MinCells; c <= cuts[i + 1] - MinCells; c++)
                if (score[c] > bestVal) { bestVal = score[c]; best = c; }

            if (best < 0) { i++; continue; }
            cuts.Insert(i + 1, best);
        }

        if (downbeats is { Count: > 0 })
        {
            for (int i = 1; i < cuts.Count - 1; i++)
            {
                int best = cuts[i];
                int bestDelta = int.MaxValue;
                foreach (var d in downbeats)
                {
                    int gi = NearestGridIndex(grid, d);
                    if (Math.Abs(grid[gi] - d) > 1e-3) continue;   // 下拍不在拍网格上（异常输入）不吸附
                    int delta = Math.Abs(gi - cuts[i]);
                    if (delta < bestDelta) { bestDelta = delta; best = gi; }
                }
                if (bestDelta <= SnapCells) cuts[i] = best;
            }
        }

        var result = new List<int> { 0 };
        cuts.Sort();
        foreach (var idx in cuts)
            if (idx - result[^1] >= MinCells && n - idx >= MinCells) result.Add(idx);
        if (result[^1] != n) result.Add(n);
        return result;
    }

    /// <summary>远距离相似度对（|i−j| ≥ 2×核宽）的标准差：无结构时相似度的噪声尺度。</summary>
    private static double FarPairStd(float[][] sim)
    {
        int n = sim.Length;
        double sum = 0, sum2 = 0;
        long count = 0;
        for (int i = 0; i < n; i++)
            for (int j = i + 2 * Half; j < n; j++)
            {
                var v = (double)sim[i][j];
                sum += v;
                sum2 += v * v;
                count++;
            }
        if (count == 0) return 0;

        var mean = sum / count;
        return Math.Sqrt(Math.Max(0.0, sum2 / count - mean * mean));
    }

    private static int NearestGridIndex(List<double> grid, double t)
    {
        int lo = 0, hi = grid.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (grid[mid] < t) lo = mid + 1; else hi = mid;
        }
        if (lo > 0 && Math.Abs(grid[lo - 1] - t) <= Math.Abs(grid[lo] - t)) return lo - 1;
        return lo;
    }

    private static List<Seg> BuildSegments(List<int> bounds, List<double> grid, double[] energy)
    {
        var segs = new List<Seg>();
        for (int i = 0; i + 1 < bounds.Count; i++)
        {
            int c0 = bounds[i], c1 = bounds[i + 1];
            double sum = 0;
            for (int c = c0; c < c1; c++) sum += energy[c];
            segs.Add(new Seg
            {
                C0 = c0,
                C1 = c1,
                Start = grid[c0],
                End = grid[c1],
                Energy = c1 > c0 ? sum / (c1 - c0) : 0
            });
        }
        return segs;
    }

    /// <summary>贪心聚类：与已有标签的首段比较，对角线条带均值超阈值即归入该标签，否则开新标签。</summary>
    private static void Cluster(List<Seg> segs, float[][] sim)
    {
        var thr = ClusterThreshold(segs, sim);
        var reps = new List<int>();
        for (int i = 0; i < segs.Count; i++)
        {
            int match = -1;
            for (int r = 0; r < reps.Count; r++)
                if (BandSimilarity(segs[i], segs[reps[r]], sim) >= thr) { match = r; break; }
            if (match < 0) { match = reps.Count; reps.Add(i); }
            segs[i].Label = ((char)('A' + match % 26)).ToString();
        }
    }

    /// <summary>聚类阈值按当首曲子的段间相似度分布自适应（均值 + 0.6σ）：白化后相似度整体会偏低，写死的常数两头不讨好。</summary>
    private static double ClusterThreshold(List<Seg> segs, float[][] sim)
    {
        if (segs.Count < 2) return double.MaxValue;

        double sum = 0, sum2 = 0;
        long count = 0;
        for (int i = 0; i < segs.Count; i++)
            for (int j = i + 1; j < segs.Count; j++)
            {
                var v = BandSimilarity(segs[i], segs[j], sim);
                sum += v;
                sum2 += v * v;
                count++;
            }

        var mean = sum / count;
        var std = Math.Sqrt(Math.Max(0.0, sum2 / count - mean * mean));
        return Math.Clamp(mean + ClusterZ * std, 0.15, 0.95);
    }

    /// <summary>对齐长度后逐拍比较，取 ±AlignTol 内最优的对角线条带均值。</summary>
    private static double BandSimilarity(Seg a, Seg b, float[][] sim)
    {
        int la = a.C1 - a.C0, lb = b.C1 - b.C0;
        double best = 0;
        for (int d = -AlignTol; d <= AlignTol; d++)
        {
            double sum = 0;
            int cnt = 0;
            for (int i = 0; i < la; i++)
            {
                int j = i + d;
                if (j < 0 || j >= lb) continue;
                sum += sim[a.C0 + i][b.C0 + j];
                cnt++;
            }
            if (cnt > 0)
            {
                double mean = sum / cnt;
                if (mean > best) best = mean;
            }
        }
        return best;
    }

    private static List<Section> Name(List<Seg> segs)
    {
        int m = segs.Count;
        var norm = new double[m];
        double maxE = 0;
        for (int i = 0; i < m; i++) if (segs[i].Energy > maxE) maxE = segs[i].Energy;
        for (int i = 0; i < m; i++) norm[i] = maxE > 1e-9 ? segs[i].Energy / maxE : 0;

        var count = new Dictionary<string, int>();
        var energySum = new Dictionary<string, double>();
        for (int i = 0; i < m; i++)
        {
            var lab = segs[i].Label;
            count[lab] = count.TryGetValue(lab, out var c) ? c + 1 : 1;
            energySum[lab] = (energySum.TryGetValue(lab, out var e) ? e : 0) + norm[i];
        }

        string? chorus = null;
        double best = double.MinValue;
        foreach (var kv in count)
            if (kv.Value >= 2)
            {
                double mean = energySum[kv.Key] / kv.Value;
                if (mean > best) { best = mean; chorus = kv.Key; }
            }

        var sorted = (double[])norm.Clone();
        Array.Sort(sorted);
        double median = sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : 0.5 * (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]);

        var result = new List<Section>(m);
        for (int i = 0; i < m; i++)
        {
            var s = segs[i];
            string kind;
            if (m == 1) kind = "intro";
            else if (s.Label == chorus) kind = "chorus";
            else if (i == 0 && norm[i] < median) kind = "intro";
            else if (i == m - 1 && norm[i] < median) kind = "outro";
            else if (count[s.Label] == 1 && i != 0 && i != m - 1 && i * 2 >= m) kind = "bridge";
            else kind = "verse";

            result.Add(new Section
            {
                Start = Math.Round(s.Start, 6),
                End = Math.Round(s.End, 6),
                Kind = kind,
                Label = s.Label,
                Energy = Math.Round(norm[i], 6)
            });
        }
        return result;
    }

    private sealed class Seg
    {
        public int C0;
        public int C1;
        public double Start;
        public double End;
        public double Energy;
        public string Label = "A";
    }
}
