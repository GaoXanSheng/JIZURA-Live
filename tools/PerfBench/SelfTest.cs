namespace PerfBench;

/// <summary>
/// 纯函数自检：**不建设备、不开窗口、不写文件**。这是本线唯一不看设备就能跑的硬闸门
/// （仓库既有先例：tools/SpoutProbe 用退出码当结论）。
///
/// 钉住的东西（都是后续重构容易悄悄漂移的口径）：
///   1. 最近秩百分位的定义：n=1 / n=2（偶数）/ 奇数 / 全等值 / 乱序 / p100 与越界钳位；
///   2. 固定样本集 1..100ms 的 p50=50、p95=95、p99=99（手算值，容差 1e-9）；
///   3. [1,2] 的 p50=1、p99=2；[5,5,5,5] 三者全为 5；
///   4. B/帧 的除零保护；
///   5. Format() 输出必须含 p95、GC 行、工作集行与固定一行 [WARP] 标记；
///   6. CSV 表头与数据行数一致；
///   7. 长跑的内存曲线：最小二乘斜率（正/负/持平/带噪声）与按 |斜率| 的分档结论，
///      以及「算不出趋势给 NaN 而不是 0」的退化路径。
///
/// 退出码：0 = 全部通过；3 = 有断言失败（打印首条不一致的期望/实际）。
/// </summary>
internal static class SelfTest
{
    private const double Tol = 1e-9;

    private static readonly List<string> Failures = new();
    private static int _passed;

    public static int Run()
    {
        ValidatePercentile();
        ValidateAllocPerFrame();
        ValidateFormat();
        ValidateCsv();
        ValidateMemCurve();

        Console.WriteLine($"[SelfTest] 通过 {_passed} 项，失败 {Failures.Count} 项");
        if (Failures.Count == 0)
        {
            Console.WriteLine("[SelfTest] 结论：口径一致（纯数学，无 GPU、无窗口、无真机依赖）");
            return 0;
        }

        Console.WriteLine("[SelfTest][FAIL] 首条不一致：" + Failures[0]);
        return 3;
    }

    private static void ValidatePercentile()
    {
        // n=1：任何分位都取那唯一一个样本
        var one = Stats(7.5);
        Check("n=1 p50=7.5", one.Percentile(0.50), 7.5);
        Check("n=1 p95=7.5", one.Percentile(0.95), 7.5);
        Check("n=1 p99=7.5", one.Percentile(0.99), 7.5);
        Check("n=1 p100=7.5", one.Percentile(1.00), 7.5);

        // 偶数样本：最近秩不是插值——[1,2] 的 p50 是 1，不是 1.5
        var two = Stats(1, 2);
        Check("[1,2] p50=1（取较小样本，非插值）", two.Percentile(0.50), 1);
        Check("[1,2] p95=2", two.Percentile(0.95), 2);
        Check("[1,2] p99=2", two.Percentile(0.99), 2);

        // 奇数样本：[10,20,30,40,50] → p50 = ceil(0.5*5)=3 → 30
        var odd = Stats(10, 20, 30, 40, 50);
        Check("[10..50] p50=30", odd.Percentile(0.50), 30);
        Check("[10..50] p95=50", odd.Percentile(0.95), 50);

        // 偶数样本：[10,20,30,40] → p50 = ceil(0.5*4)=2 → 20
        var even = Stats(10, 20, 30, 40);
        Check("[10,20,30,40] p50=20", even.Percentile(0.50), 20);
        Check("[10,20,30,40] p95=40", even.Percentile(0.95), 40);

        // 全等值：任何分位都一样
        var same = Stats(5, 5, 5, 5);
        Check("[5,5,5,5] p50=p95=p99=5",
            same.Percentile(0.50) + same.Percentile(0.95) + same.Percentile(0.99), 15);
        Check("[5,5,5,5] max=5", same.Max, 5);

        // 乱序：结果与输入顺序无关
        var shuffled = Stats(40, 10, 30, 20);
        Check("乱序 [40,10,30,20] p50=20", shuffled.Percentile(0.50), 20);
        Check("乱序 [40,10,30,20] p99=40", shuffled.Percentile(0.99), 40);

        // 固定样本集 1..100：手算值 p50=50、p95=95、p99=99（n/100 的采样窗口恰好命中整数秩）
        var ramp = Stats(Enumerable.Range(1, 100).Select(i => (double)i).ToArray());
        Check("1..100 p50=50", ramp.Percentile(0.50), 50);
        Check("1..100 p95=95", ramp.Percentile(0.95), 95);
        Check("1..100 p99=99", ramp.Percentile(0.99), 99);
        Check("1..100 p100=100（max 边界）", ramp.Percentile(1.00), 100);
        Check("1..100 q=0 → 最小样本 1", ramp.Percentile(0.0), 1);
        Check("1..100 q=1.5 → 钳到 max 100", ramp.Percentile(1.5), 100);
        Check("1..100 q=-0.5 → 钳到 min 1", ramp.Percentile(-0.5), 1);
        Check("1..100 max=100", ramp.Max, 100);
        Check("1..100 样本数=100", ramp.Count, 100);

        // 无样本：分位无定义 → 返回 0，由调用方另判「采样不足」
        Check("空集 p95=0（退化值，不等于一次测量）", new FrameStats().Percentile(0.95), 0);
        Check("空集 max=0", new FrameStats().Max, 0);
    }

    private static void ValidateAllocPerFrame()
    {
        Check("B/帧：1000B/4 帧 = 250", FrameStats.AllocBytesPerFrame(1000, 4), 250);
        Check("B/帧：帧数 0 时给 0（不除零）", FrameStats.AllocBytesPerFrame(123456, 0), 0);
        Check("B/帧：0B/N 帧 = 0", FrameStats.AllocBytesPerFrame(0, 7), 0);
    }

    private static void ValidateFormat()
    {
        var hw = SampleSnapshot(software: false).Format();
        var warp = SampleSnapshot(software: true).Format();

        Check("Format 含 p95", hw.Contains("p95=", StringComparison.Ordinal));
        Check("Format 含 GC 计数行", hw.Contains("GC 计数：", StringComparison.Ordinal));
        Check("Format 含工作集行", hw.Contains("工作集 ：", StringComparison.Ordinal));
        Check("Format 含帧数行", hw.Contains("帧数   ：", StringComparison.Ordinal));
        Check("Format 含硬件行（适配器名）", hw.Contains("硬件   ：基准测试适配器", StringComparison.Ordinal));
        Check("Format 含 VSync 状态", hw.Contains("VSync=开", StringComparison.Ordinal));
        Check("Format 含显存行", hw.Contains("显存   ：", StringComparison.Ordinal));

        // [WARP] 标记是固定一行：硬件时报「否」，软件光栅时报出「不参加硬件性能通过判定」
        Check("Format 含固定一行 [WARP] 标记", hw.Contains("[WARP] ", StringComparison.Ordinal));
        Check("硬件光栅：[WARP] 行显示「否」", hw.Contains("[WARP] 否", StringComparison.Ordinal));
        Check("软件光栅：[WARP] 行声明不参加硬件判定",
            warp.Contains("[WARP] 软件光栅", StringComparison.Ordinal)
            && warp.Contains("本结果不参加硬件性能通过判定", StringComparison.Ordinal));
        Check("软件光栅：[WARP] 行不再显示「否」", !warp.Contains("[WARP] 否", StringComparison.Ordinal));

        // 帧数为 0 的退化快照也必须能出一份可读（且不炸）的报告
        var empty = SampleSnapshot(software: false, frames: 0, allocPerFrame: 0,
            vramUsage: -1, vramBudget: -1).Format();
        Check("空样本报告含帧数 0", empty.Contains("帧数   ：0 帧", StringComparison.Ordinal));
        Check("不可得显存记 -1（不冒充 0 字节）", empty.Contains("不可得（-1）", StringComparison.Ordinal));

        // 长跑：有采样点时报告才多出曲线行；短跑（无 30s 采样）的形状与加长跑之前一致
        var curved = SampleSnapshot(software: false,
            workingSetCurve: new MemCurveResult("工作集曲线", 2.5, 20),
            vramCurve: new MemCurveResult("显存曲线", -0.5, 20)).Format();
        Check("Format 含工作集曲线行", curved.Contains("工作集曲线：", StringComparison.Ordinal));
        Check("Format 含显存曲线行", curved.Contains("显存曲线：", StringComparison.Ordinal));
        Check("Format 含曲线斜率结论行（下游只取最后一条含「斜率」的行）",
            curved.Contains("[Bench] 曲线斜率结论：", StringComparison.Ordinal));
        Check("短跑报告不含曲线行（形状不变）", !hw.Contains("曲线", StringComparison.Ordinal));
    }

    private static void ValidateMemCurve()
    {
        // 斜率值：正 / 负 / 持平（手算：三点等距时最小二乘 = (y2-y0)/2）
        Check("斜率：+10 MB/分钟", MemCurve.SlopeMbPerMinute(Points((0, 100), (1, 110), (2, 120))), 10);
        Check("斜率：-10 MB/分钟", MemCurve.SlopeMbPerMinute(Points((0, 120), (1, 110), (2, 100))), -10);
        Check("斜率：持平 = 0", MemCurve.SlopeMbPerMinute(Points((0, 100), (1, 100), (2, 100))), 0);
        // 中间点被顶起来（一次 GC/显存回收的抖动）时仍取趋势，而不是把抖动当整段
        Check("斜率：带噪声仍为 +5 MB/分钟", MemCurve.SlopeMbPerMinute(Points((0, 100), (1, 120), (2, 110))), 5);

        // 分档阈值：|斜率| < 1 平稳；1-10 缓增需观察；> 10 疑似泄漏（1 与 10 归中间档）
        Check("结论：0.99 → 平稳", MemCurve.Verdict(0.99), "平稳（|斜率| < 1 MB/分钟）");
        Check("结论：1 → 缓增需观察", MemCurve.Verdict(1.0), "缓增需观察（1-10 MB/分钟）");
        Check("结论：10 → 缓增需观察", MemCurve.Verdict(10.0), "缓增需观察（1-10 MB/分钟）");
        Check("结论：10.1 → 疑似泄漏", MemCurve.Verdict(10.1), "疑似泄漏（> 10 MB/分钟）");
        Check("结论：-3 → 缓降（不写成缓增）", MemCurve.Verdict(-3.0), "缓降需观察（1-10 MB/分钟）");
        Check("结论：NaN → 不可判定", MemCurve.Verdict(double.NaN), "不可判定（采样点不足或读数不可得）");

        // 退化路径：算不出趋势给 NaN，不给 0（0 会被读成「测出来是平的」）
        Check("斜率：单点 → NaN", double.IsNaN(MemCurve.SlopeMbPerMinute(Points((0, 100)))));
        Check("斜率：空集 → NaN", double.IsNaN(MemCurve.SlopeMbPerMinute(new List<MemPoint>())));
        Check("斜率：所有点同一分钟 → NaN（分母为 0）",
            double.IsNaN(MemCurve.SlopeMbPerMinute(Points((2, 100), (2, 130)))));
        Check("斜率文本：NaN 印「不可得」不印 0",
            MemCurve.FormatSlope(double.NaN), "不可得（采样点不足 2 点或读数不可得）");
        Check("斜率文本：+2.5 带正号（增长方向一眼可见）", MemCurve.FormatSlope(2.5), "+2.50 MB/分钟");

        // 采样行形状：下游按这个形状取值，改了就是破坏接口
        var line = new MemSample(Minute: 1.5, FramesInInterval: 1798, WorkingSetBytes: 537L * 1024 * 1024,
            VramUsageBytes: 268L * 1024 * 1024, Gc0: 12, Gc1: 1, Gc2: 0).Line();
        Check("采样行形状固定（[sample] Nmin ws=…MB vram=…MB gc=…/…/… frames=…）",
            line, "[sample] 1min ws=537.0MB vram=268.0MB gc=12/1/0 frames=1798");

        // 显存不可得的采样不参与显存曲线回归（-1 不是一次测量），工作集那条不受影响
        var samples = new List<MemSample>
        {
            new(0.5, 10, 100L * 1024 * 1024, -1, 0, 0, 0),
            new(1.5, 10, 200L * 1024 * 1024, -1, 0, 0, 0),
        };
        Check("显存曲线：全是 -1 → 0 个点（不拿 -1 当读数）", MemCurve.VramPoints(samples).Count, 0);
        Check("显存曲线：0 点 → 斜率 NaN", double.IsNaN(
            MemCurve.Summarize("显存曲线", MemCurve.VramPoints(samples)).SlopeMbPerMinute));
        Check("工作集曲线：不受显存不可得影响（2 点）", MemCurve.WorkingSetPoints(samples).Count, 2);
        Check("工作集曲线：0.5→1.5 分钟 +100MB = +100 MB/分钟",
            MemCurve.Summarize("工作集曲线", MemCurve.WorkingSetPoints(samples)).SlopeMbPerMinute, 100);
    }

    private static void ValidateCsv()
    {
        var intervals = Stats(16.1, 16.2, 16.3, 16.4, 16.5);
        var cpu = Stats(1.1, 0.9, 1.0, 1.2, 0.8);
        var text = FrameStats.ToCsv(intervals, cpu);
        var lines = text.Split('\n');

        // 表头 + 每帧一行，最后一行是结尾换行 → 行数 = 1 + 帧数 + 1
        Check("CSV 首行 = 表头", lines.Length > 0 && lines[0] == FrameStats.CsvHeader, FrameStats.CsvHeader,
            lines.Length > 0 ? lines[0] : "(空)");
        Check("CSV 数据行数 = 帧数", lines.Length - 2, intervals.Count);
        Check("CSV 首条数据行 = 0,16.100,1.100", lines.Length > 1 && lines[1] == "0,16.100,1.100",
            "0,16.100,1.100", lines.Length > 1 ? lines[1] : "(空)");
        Check("CSV 末条数据行 = 4,16.500,0.800", lines.Length > 5 && lines[5] == "4,16.500,0.800",
            "4,16.500,0.800", lines.Length > 5 ? lines[5] : "(空)");
        Check("CSV 以单条结尾换行收尾", lines[^1].Length == 0);

        // 帧数为 0 时只剩表头
        var headOnly = FrameStats.ToCsv(new FrameStats(), new FrameStats()).Split('\n');
        Check("CSV 无样本时仅表头 1 行", headOnly.Length - 1, 1);
    }

    /// <summary>字面值构造一份快照——自检不碰 GC/Process/设备，只校验纯渲染逻辑。</summary>
    private static FrameSnapshot SampleSnapshot(bool software, int frames = 240, double allocPerFrame = 12.5,
        long vramUsage = 256L * 1024 * 1024, long vramBudget = 1024L * 1024 * 1024,
        MemCurveResult? workingSetCurve = null, MemCurveResult? vramCurve = null) =>
        new(
            new BenchEnv(
                AdapterName: "基准测试适配器",
                FeatureLevel: "Level_11_0",
                Software: software,
                Width: 1280,
                Height: 720,
                VSync: true,
                TotalSeconds: 8,
                WarmupSeconds: 2,
                SampledSeconds: 6),
            Frames: frames,
            TotalFrames: frames + 120,
            IntervalP50: 16.6,
            IntervalP95: 17.1,
            IntervalP99: 18.4,
            IntervalMax: 33.3,
            CpuP50: 1.0,
            CpuP95: 1.4,
            CpuP99: 2.0,
            CpuMax: 5.0,
            Gc0: 3,
            Gc1: 0,
            Gc2: 0,
            AllocBytesPerFrame: allocPerFrame,
            WorkingSetBytes: 512L * 1024 * 1024,
            PrivateBytes: 400L * 1024 * 1024,
            VramUsageBytes: vramUsage,
            VramBudgetBytes: vramBudget,
            WorkingSetCurve: workingSetCurve,
            VramCurve: vramCurve);

    /// <summary>把 (分钟, MB) 字面值拼成回归点。</summary>
    private static MemPoint[] Points(params (double Minute, double Mb)[] points) =>
        points.Select(p => new MemPoint(p.Minute, p.Mb)).ToArray();

    private static FrameStats Stats(params double[] values)
    {
        var s = new FrameStats();
        foreach (var v in values) s.Sample(v);
        return s;
    }

    private static void Check(string name, double actual, double expected)
    {
        var ok = Math.Abs(actual - expected) <= Tol;
        Report(name, ok, expected.ToString("R"), actual.ToString("R"));
    }

    private static void Check(string name, bool ok, string expected = "true", string actual = "false") =>
        Report(name, ok, expected, actual);

    private static void Check(string name, string actual, string expected) => Report(name, actual == expected, expected, actual);

    private static void Report(string name, bool ok, string expected, string actual)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine($"[SelfTest] PASS {name}");
            return;
        }

        Failures.Add($"{name}｜期望={expected}｜实际={actual}");
        Console.WriteLine($"[SelfTest] FAIL {name}");
    }
}
