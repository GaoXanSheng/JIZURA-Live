using System.Globalization;

namespace PerfBench;

/// <summary>
/// 内存曲线的一次采样（**纯数据**）：既是 stdout 上那一行 "[sample] …" 的原始读数，也是回归的点来源。
/// 显存读数沿用本工程的约定：-1 = 不可得（WARP/无适配器/接口不可得），**不记 0**。
/// </summary>
/// <param name="Minute">距统计起点的分钟数（精确值：回归的 x 轴用它，打印时向下取整成整分钟）。</param>
/// <param name="FramesInInterval">本段帧数（距上次采样之间的出帧数）。</param>
/// <param name="WorkingSetBytes">进程工作集（字节）。</param>
/// <param name="VramUsageBytes">适配器 Local 段 CurrentUsage（字节；-1 = 不可得）。</param>
/// <param name="Gc0">统计窗口内累计 Gen0 回收次数。</param>
/// <param name="Gc1">统计窗口内累计 Gen1 回收次数。</param>
/// <param name="Gc2">统计窗口内累计 Gen2 回收次数。</param>
public sealed record MemSample(
    double Minute,
    int FramesInInterval,
    long WorkingSetBytes,
    long VramUsageBytes,
    int Gc0,
    int Gc1,
    int Gc2)
{
    /// <summary>
    /// stdout 采样行：固定 key=value 单行，便于 grep 与下游脚本取值。
    /// 分钟向下取整而不是打 0.5 这类小数：30s 采样下「第几分钟」这列要一眼能读，
    /// 精确时刻只留给内部回归（见 <see cref="Minute"/>）。
    /// </summary>
    public string Line() =>
        $"[sample] {(int)Minute}min ws={Mb(WorkingSetBytes)}MB"
        + $" vram={(VramUsageBytes < 0 ? "-1" : Mb(VramUsageBytes))}MB"
        + $" gc={Gc0}/{Gc1}/{Gc2} frames={FramesInInterval}";

    /// <summary>MB 读数（F1，invariant）：采样行要机器可读，所以不带单位后缀、也不套「不可得」字样。</summary>
    private static string Mb(long bytes) => (bytes / 1048576.0).ToString("F1", CultureInfo.InvariantCulture);
}

/// <summary>回归点：x = 距统计起点的分钟数，y = 读数（MB）。</summary>
public readonly record struct MemPoint(double Minute, double Mb);

/// <summary>
/// 曲线斜率与分档结论：**纯函数**（自检不看设备就能钉住），报告里的「工作集曲线 / 显存曲线」都出自这里。
///
/// 为什么用最小二乘而不是「首尾差 ÷ 时长」：一次 GC 或一次显存回收会把单个采样顶起或压低，
/// 首尾两点就把这点抖动当成整段趋势；最小二乘让每个 30s 采样都参与，只由趋势决定斜率。
/// 口径：斜率单位 MB/分钟，正 = 增长；算不出趋势时给 NaN，**不给 0**（0 会被读成「测出来是平的」）。
/// </summary>
public static class MemCurve
{
    /// <summary>平稳上界（MB/分钟）：|斜率| 小于它算平稳。</summary>
    public const double StableBelow = 1.0;

    /// <summary>疑似泄漏下界（MB/分钟）：|斜率| 大于它算疑似泄漏。报告里印的阈值文字与这两个常量同源。</summary>
    public const double LeakAbove = 10.0;

    /// <summary>
    /// 最小二乘斜率（MB/分钟）。少于 2 点、或所有点挤在同一分钟（分母为 0）时返回 <see cref="double.NaN"/>：
    /// 这两种情况都算不出趋势，给 0 就是拿一个退化值去冒充「没变化」。
    /// </summary>
    public static double SlopeMbPerMinute(IReadOnlyList<MemPoint> points)
    {
        if (points.Count < 2) return double.NaN;

        double sumX = 0, sumY = 0;
        foreach (var p in points)
        {
            sumX += p.Minute;
            sumY += p.Mb;
        }

        var meanX = sumX / points.Count;
        var meanY = sumY / points.Count;
        double sxy = 0, sxx = 0;
        foreach (var p in points)
        {
            var dx = p.Minute - meanX;
            sxy += dx * (p.Mb - meanY);
            sxx += dx * dx;
        }

        return sxx > 0 ? sxy / sxx : double.NaN;
    }

    /// <summary>
    /// 按 |斜率| 分档给结论（阈值见 <see cref="StableBelow"/> / <see cref="LeakAbove"/>）。
    /// 增/降分开措辞：曲线明显在缩时还写「缓增 / 泄漏」，报告就自己打自己。
    /// </summary>
    public static string Verdict(double slopeMbPerMinute)
    {
        if (double.IsNaN(slopeMbPerMinute)) return "不可判定（采样点不足或读数不可得）";

        var m = Math.Abs(slopeMbPerMinute);
        if (m < StableBelow) return "平稳（|斜率| < 1 MB/分钟）";
        if (m <= LeakAbove)
            return slopeMbPerMinute > 0 ? "缓增需观察（1-10 MB/分钟）" : "缓降需观察（1-10 MB/分钟）";
        return slopeMbPerMinute > 0 ? "疑似泄漏（> 10 MB/分钟）" : "疑似异常下降（> 10 MB/分钟）";
    }

    /// <summary>斜率取值文本：NaN 印「不可得」，不印 0（0 会被读成一次真实测量）。</summary>
    public static string FormatSlope(double slopeMbPerMinute) =>
        double.IsNaN(slopeMbPerMinute)
            ? "不可得（采样点不足 2 点或读数不可得）"
            : slopeMbPerMinute.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + " MB/分钟";

    /// <summary>工作集曲线的回归点（MB）。</summary>
    public static List<MemPoint> WorkingSetPoints(IReadOnlyList<MemSample> samples) => Points(samples, vram: false);

    /// <summary>显存曲线的回归点（MB）：显存记 -1 的采样一律跳过——-1 不是一次测量。</summary>
    public static List<MemPoint> VramPoints(IReadOnlyList<MemSample> samples) => Points(samples, vram: true);

    /// <summary>一条曲线的结论。点数可能是 0（读数全不可得），那时斜率为 NaN、结论为不可判定。</summary>
    public static MemCurveResult Summarize(string name, IReadOnlyList<MemPoint> points) =>
        new(name, SlopeMbPerMinute(points), points.Count);

    private static List<MemPoint> Points(IReadOnlyList<MemSample> samples, bool vram)
    {
        var list = new List<MemPoint>(samples.Count);
        foreach (var s in samples)
        {
            var bytes = vram ? s.VramUsageBytes : s.WorkingSetBytes;
            if (bytes < 0) continue;
            list.Add(new MemPoint(s.Minute, bytes / 1048576.0));
        }

        return list;
    }
}

/// <summary>一条曲线在报告里的结论：斜率 + 参与回归的点数（点数太少时读者有权怀疑这个斜率）。</summary>
public sealed record MemCurveResult(string Name, double SlopeMbPerMinute, int Points)
{
    /// <summary>报告行：曲线名 → 斜率 → 分档结论。</summary>
    public string Describe() =>
        $"{Name}：斜率={MemCurve.FormatSlope(SlopeMbPerMinute)}（最小二乘，{Points} 点）"
        + $"｜结论：{MemCurve.Verdict(SlopeMbPerMinute)}";

    /// <summary>单行形态（两条曲线合成一行收尾时用）。</summary>
    public string Summary() =>
        $"{Name} {MemCurve.FormatSlope(SlopeMbPerMinute)} → {MemCurve.Verdict(SlopeMbPerMinute)}";
}
