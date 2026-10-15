using System.Globalization;
using System.Text;

namespace PerfBench;

/// <summary>
/// 毫秒样本累加器 + **最近秩（nearest-rank）**百分位。
///
/// 口径（本探针说的 p50/p95/p99 就是这个，后续重构不允许漂移）：
///   样本升序 x[0..n-1] 后，pQ 取 x[rank-1]，rank = ceil(Q/100 * n)，rank 再钳到 [1, n]。
///   取值**必然落在真实样本上**，不做任何插值。
///   * n=1：任何分位都取那唯一一个样本（rank = ceil(q*1) = 1）。
///   * 偶数样本：n=2 的 p50 → rank = ceil(0.5*2) = 1 → 取**较小**那个，即 [1,2] 的 p50 = 1
///     （最近秩不是中位数插值，绝不是 1.5）；n=4 的 p50 → rank = 2，即 [10,20,30,40] 的 p50 = 20。
///   * 全等值：rank 落在哪里都一样，任何分位都等于该值。
///   * 乱序样本：本类内部排序，结果与输入顺序无关。
///   * p100 / q=1：rank = n → 取最大值；q &lt; 0 或 q &gt; 1 一律钳到 [0,1]。
///   * n=0（无样本）：分位无定义，本类返回 0——调用方必须另判「采样不足」，
///     不能让这个 0 冒充一次真实测量（退出码 4 就是干这个的）。
///
/// 线程模型：单写者（渲染线程逐帧 Sample），报告在渲染线程退出后才读，不加锁。
/// </summary>
public sealed class FrameStats
{
    private readonly List<double> _samples = new();
    private List<double>? _sorted;

    /// <summary>已累计的样本数。</summary>
    public int Count => _samples.Count;

    /// <summary>只读的原始样本（按采样顺序），逐帧落盘 CSV 时按序配对用。</summary>
    public IReadOnlyList<double> Samples => _samples;

    /// <summary>累计一个毫秒样本（出帧间隔或 CPU 提交耗时，由调用方决定口径）。</summary>
    public void Sample(double ms)
    {
        _samples.Add(ms);
        _sorted = null; // 排序结果只在报告时要一次；每次 Sample 都排是白烧 CPU
    }

    /// <summary>最近秩百分位，<paramref name="q"/> 取 0..1（0.95 = p95）。无样本时返回 0。</summary>
    public double Percentile(double q)
    {
        if (_samples.Count == 0) return 0;
        q = Math.Clamp(q, 0.0, 1.0);

        // 减 1e-9：0.95*100 这类乘积可能落在整数上方 1 ulp，直接 ceil 会把名次抬高一档、
        // 让 p95 变成第 96 个样本。这个守卫只在乘积「本应正好是整数」时生效。
        var rank = (int)Math.Ceiling(q * _samples.Count - 1e-9);
        var sorted = Sorted();
        return sorted[Math.Clamp(rank, 1, sorted.Count) - 1];
    }

    /// <summary>样本最大值（无样本时 0）。</summary>
    public double Max => _samples.Count == 0 ? 0 : Sorted()[^1];

    private List<double> Sorted()
    {
        if (_sorted is not null) return _sorted;
        var copy = new List<double>(_samples);
        copy.Sort();
        return _sorted = copy;
    }

    /// <summary>
    /// 分配量 ÷ 帧数（B/帧）。帧数为 0 时给 0：既不能除零，也不能把「无样本」伪装成一个分配数字。
    /// </summary>
    public static double AllocBytesPerFrame(long allocBytes, int frames) =>
        frames > 0 ? (double)allocBytes / frames : 0;

    /// <summary>逐帧 CSV 的表头（自检钉住它与数据行数一致）。</summary>
    public const string CsvHeader = "frame,interval_ms,cpu_submit_ms";

    /// <summary>
    /// 逐帧 CSV：表头 + 每个出帧间隔一行，CPU 提交耗时按序号配对（缺样本留空列）。
    /// 行分隔固定用 '\n'，便于自检按行数比对，也不受宿主环境换行风格影响。
    /// </summary>
    public static string ToCsv(FrameStats intervals, FrameStats cpu)
    {
        var sb = new StringBuilder(CsvHeader).Append('\n');
        for (var i = 0; i < intervals.Count; i++)
        {
            sb.Append(i).Append(',')
              .Append(intervals.Samples[i].ToString("F3", CultureInfo.InvariantCulture)).Append(',');
            if (i < cpu.Count) sb.Append(cpu.Samples[i].ToString("F3", CultureInfo.InvariantCulture));
            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>字节的原文（-1 = 不可得，见 <see cref="FrameSnapshot"/> 的约定）。</summary>
    public static string Bytes(long b) => b < 0 ? "不可得（-1）" : b.ToString(CultureInfo.InvariantCulture) + " B";

    /// <summary>字节的 MB 读数（-1 时保留「不可得」字样，不折算成 0）。</summary>
    public static string Mb(long b) =>
        b < 0 ? "不可得（-1）" : (b / 1048576.0).ToString("F1", CultureInfo.InvariantCulture) + " MB";
}

/// <summary>
/// 跑法与环境（硬件行用）。**纯数据**，可在没有设备时构造——自检据此校验 Format 的字段齐全性。
/// </summary>
public sealed record BenchEnv(
    string AdapterName,
    string FeatureLevel,
    bool Software,
    int Width,
    int Height,
    bool VSync,
    double TotalSeconds,
    double WarmupSeconds,
    double SampledSeconds);

/// <summary>
/// 一次短跑的采样快照。**纯数据**：自检直接构造它来校验 <see cref="Format"/>，不需要设备。
///
/// 显存约定：<see cref="VramUsageBytes"/> / <see cref="VramBudgetBytes"/> 为 -1 表示**不可得**
/// （WARP、无适配器、接口不可得）。**不记 0**：0 会被读成「用了 0 字节」这种真实测量，
/// 而 WARP 的 Local 段预算根本没有意义。
///
/// 曲线约定：<see cref="WorkingSetCurve"/> / <see cref="VramCurve"/> 只有长跑有 30s 采样时才非 null，
/// 短跑的这两行不出现（报告形状与加长跑之前一致）。
/// </summary>
public sealed record FrameSnapshot(
    BenchEnv Env,
    int Frames,
    int TotalFrames,
    double IntervalP50,
    double IntervalP95,
    double IntervalP99,
    double IntervalMax,
    double CpuP50,
    double CpuP95,
    double CpuP99,
    double CpuMax,
    int Gc0,
    int Gc1,
    int Gc2,
    double AllocBytesPerFrame,
    long WorkingSetBytes,
    long PrivateBytes,
    long VramUsageBytes,
    long VramBudgetBytes,
    MemCurveResult? WorkingSetCurve = null,
    MemCurveResult? VramCurve = null)
{
    /// <summary>
    /// 可读报告块：口径声明 → 硬件行 → 固定一行 [WARP] 标记 → 各项读数（长跑再加曲线行）。
    /// 口径与 [WARP] 行是**结论的一部分**，不是装饰：任何消费这份数字的人先看到的就是它们。
    /// </summary>
    public string Format()
    {
        Func<double, string> ms = v => v.ToString("F3", CultureInfo.InvariantCulture) + " ms";
        var warp = Env.Software
            ? "[WARP] 软件光栅（WARP）：本结果不参加硬件性能通过判定，退出码不受其数值影响。"
            : "[WARP] 否（硬件光栅）：数值可作前后对比，但仍不是路线图 §9 的硬件验收结论。";

        var lines = new List<string>
        {
            "==== PerfBench 短跑基准（tools/PerfBench）====",
            "[口径] 出帧间隔 ≠ GPU 耗时：其中含 vsync 等待、DWM 合成与调度抖动。",
            "[口径] CPU 提交耗时只是命令入队量（Prepare+Frame 回调），不得当 GPU 耗时，"
                + "也不得与异步流水线数据相加（路线图 §9）。",
            "[口径] 短跑数字只用来立起测量管线与做前后对比，不是产品性能验收。",
            $"硬件   ：{Env.AdapterName}｜feature level {Env.FeatureLevel}｜"
                + $"软件光栅={(Env.Software ? "是" : "否")}｜窗口 {Env.Width}x{Env.Height}｜"
                + $"VSync={(Env.VSync ? "开" : "关")}｜跑秒 {Env.SampledSeconds:F2}s"
                + $"（总 {Env.TotalSeconds:F2}s，预热 {Env.WarmupSeconds:F2}s 不计入统计）",
            warp,
            $"帧数   ：{Frames} 帧（统计窗口内；另计总出帧 {TotalFrames} 帧）",
            $"出帧间隔：p50={ms(IntervalP50)}｜p95={ms(IntervalP95)}｜p99={ms(IntervalP99)}｜max={ms(IntervalMax)}",
            $"CPU 提交：p50={ms(CpuP50)}｜p95={ms(CpuP95)}｜p99={ms(CpuP99)}｜max={ms(CpuMax)}"
                + "（命令入队量，非 GPU 耗时）",
            $"GC 计数：Gen0=+{Gc0}｜Gen1=+{Gc1}｜Gen2=+{Gc2}（统计窗口内差值）｜"
                + $"分配={AllocBytesPerFrame.ToString("F1", CultureInfo.InvariantCulture)} B/帧"
                + "（GetTotalAllocatedBytes(precise:false) 差值 ÷ 帧数）",
            $"工作集 ：{FrameStats.Mb(WorkingSetBytes)}（{FrameStats.Bytes(WorkingSetBytes)}）｜"
                + $"私有={FrameStats.Mb(PrivateBytes)}（{FrameStats.Bytes(PrivateBytes)}）",
            $"显存   ：Local 段 CurrentUsage={FrameStats.Mb(VramUsageBytes)}（{FrameStats.Bytes(VramUsageBytes)}）｜"
                + $"Budget={FrameStats.Mb(VramBudgetBytes)}（{FrameStats.Bytes(VramBudgetBytes)}）"
                + "｜-1 = 不可得（WARP/无适配器/接口不可得），不记 0",
        };

        // 曲线行只属于长跑（有 30s 采样才有斜率可算）
        if (WorkingSetCurve is not null) lines.Add(WorkingSetCurve.Describe());
        if (VramCurve is not null) lines.Add(VramCurve.Describe());
        // 再补一行把两条曲线并起来：下游只看最后一条含「斜率」的行，合起来才不至于把工作集那条挤掉
        if (WorkingSetCurve is not null && VramCurve is not null)
            lines.Add("[Bench] 曲线斜率结论：" + WorkingSetCurve.Summary() + "；" + VramCurve.Summary());

        return string.Join(Environment.NewLine, lines);
    }
}
