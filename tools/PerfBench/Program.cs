using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using JizuraLive.Logging;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Gpu;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using DCommonAlphaMode = Vortice.DCommon.AlphaMode;
using DCommonPixelFormat = Vortice.DCommon.PixelFormat;

namespace PerfBench;

/// <summary>
/// M7 基准探针（**短跑 + `--minutes` 长跑**）：把「出帧间隔 p50/p95/p99 + GC + 工作集 + 显存采样」的
/// 测量管线立起来，并给后续改动一个可复核的前后对比基线。长跑在此之上加两样东西：每 30s 一行内存采样，
/// 以及报告末尾的工作集/显存曲线斜率与结论（口径与算法见 MemCurve.cs）。宿主形状照 tools/RenderProbe：
/// Application + Window + PresentHost（子窗口 + D3D11 flip 交换链），帧回调里只画 `Clear` + 一行 `DrawText`
/// 的最小负载。
///
/// 用法（路线图 §10 的命令面）：
///   dotnet build tools/PerfBench/PerfBench.csproj -nologo
///   dotnet run --project tools/PerfBench/PerfBench.csproj -- --seconds 8 --warmup 2
///   dotnet run --project tools/PerfBench/PerfBench.csproj -- --minutes 10
///   dotnet run --project tools/PerfBench/PerfBench.csproj -- --self-test
///   dotnet run --project tools/PerfBench/PerfBench.csproj -- --seconds 5 --warmup 1 --nosync --csv %TEMP%\perfbench.csv
///
/// 参数：
///   --seconds N   总跑时秒数（默认 8，短跑）
///   --warmup N    预热秒数（默认 2）：窗口内的样本一律丢弃，不计入统计
///   --minutes N   长跑：预热固定 10s + N 分钟统计窗口，每 30s 打一行 "[sample] …" 内存采样。
///                 给出即由它定总时长（--seconds/--warmup 让位），免得「总时长」同时有两个说法
///   --nosync      关垂直同步（Present(0,0)，测循环上限；与 vsync 开时的数字不可比）
///   --csv &lt;path&gt; 逐帧毫秒落盘便于复核（默认不写文件）
///   --self-test   只跑纯函数自检后退出（不建设备、不开窗口、不写文件）
///
/// 数据目录纪律（路线图 §10）：运行前把 JIZURA_PV_DATA_DIR 指向专用临时目录；本探针自身也会抢先
/// 隔离到 %TEMP%\jz-perfbench-&lt;时间戳&gt;（照 tools/SpoutProbe 先例），绝不读写用户真实配置/autosave。
///
/// **口径（写死，别在别处另立一套）**：
///   ① 出帧间隔 ≠ GPU 耗时：它含 vsync 等待、DWM 合成与调度抖动。
///   ② CPU 提交耗时是命令入队量（Prepare+Frame 回调），路线图 §9 明令不得当 GPU 耗时，
///      也不得与异步流水线数据相加。
///   ③ 本探针的短跑数字只用来立起测量管线与做前后对比，**不是产品性能验收**。
///
/// 退出码：0 = 跑完并出报告；2 = GPU 初始化失败（打印 host.Error，照 RenderProbe 返回 2）；
///         3 = --self-test 失败；4 = 采样不足（--seconds 内的总出帧、或统计窗口内的帧数 &lt; 32）。
/// </summary>
internal static class Program
{
    /// <summary>
    /// 采样不足门槛：帧数少于这个数，任何分位数都不足以成立。
    /// （路线图 §9 的验收要 120s 以上采样，量级远在此之上——这个 32 只拦住「一眼假」的短跑。）
    /// </summary>
    private const int MinFrames = 32;

    [STAThread]
    private static int Main(string[] args)
    {
        var seconds = 8.0;
        var warmup = 2.0;
        var minutes = 0.0;
        var vsync = true;
        string? csvPath = null;
        var selfTest = false;
        var stress = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--seconds" && i + 1 < args.Length) double.TryParse(args[i + 1], out seconds);
            if (args[i] == "--warmup" && i + 1 < args.Length) double.TryParse(args[i + 1], out warmup);
            if (args[i] == "--minutes" && i + 1 < args.Length) double.TryParse(args[i + 1], out minutes);
            if (args[i] == "--nosync") vsync = false;
            if (args[i] == "--csv" && i + 1 < args.Length) csvPath = args[i + 1];
            if (args[i] == "--self-test") selfTest = true;
            if (args[i] == "--stress") stress = true;
        }

        // 长跑：预热固定 10s，总时长 = 预热 + N 分钟统计窗口。
        // 由 --minutes 一家定总时长：否则 --seconds 与它同时出现时「跑多久」会有两个互相矛盾的说法
        var longRun = minutes > 0;
        if (longRun)
        {
            warmup = 10.0;
            seconds = warmup + minutes * 60.0;
        }

        // 压力模式必须长跑：事件节拍 5s/8s/3s 在 8 秒短跑里一个完整周期都踩不满，曲线没有意义。
        // 默认 2 分钟就够（加压后泄漏斜率在几十秒内就显形，不需要跑满 10 分钟）
        if (stress && !longRun)
        {
            minutes = 2;
            longRun = true;
            warmup = 10.0;
            seconds = warmup + minutes * 60.0;
            Console.WriteLine("[Bench] --stress 未带 --minutes：默认按 2 分钟压力跑执行（预热 10s 不计）");
        }

        // 自检在最前面：不碰数据目录、不写文件、不建设备（否则「纯数学闸门」就不纯了）
        if (selfTest) return SelfTest.Run();

        // 数据目录隔离：必须先于任何 AppDirs 触碰——AppDirs.Root 是静态只读，一旦被读就定死
        var isoDir = Path.Combine(Path.GetTempPath(), "jz-perfbench-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(isoDir);
        Environment.SetEnvironmentVariable("JIZURA_PV_DATA_DIR", isoDir);
        Log.Init("perfbench.log");
        Console.WriteLine($"[Bench] 数据目录（隔离）：{isoDir}");
        if (longRun)
        {
            Console.WriteLine($"[Bench] 模式：长跑（预热 {warmup:F0}s + 统计 {minutes:F2} 分钟，总 {seconds:F0}s）");
            // 采样行的口径一次说清：下游按 [sample] 取值，所以这行说明用 [Bench] 开头，别混进采样行里
            Console.WriteLine("[Bench] 内存采样行：每 30s 一行，形如 "
                              + "\"[sample] <整分钟>min ws=<工作集 MB>MB vram=<显存 Local CurrentUsage MB>MB "
                              + "gc=<Gen0>/<Gen1>/<Gen2> frames=<本段帧数>\"；"
                              + "整分钟 = 距统计起点向下取整；gc 为统计窗口内累计；"
                              + "vram=-1 表示不可得（WARP/无适配器/接口不可得），不记 0");
        }

        if (warmup >= seconds)
            Console.WriteLine($"[Bench][WARN] 预热 {warmup}s 不小于总跑时 {seconds}s：统计窗口为空，必然以退出码 4 收场");

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new Window
        {
            Title = stress ? "JIZURA PerfBench（压力剖面 · 产品全链）" : "JIZURA PerfBench（M7 基准 · 短跑）",
            Width = 1280,
            Height = 720,
            Background = System.Windows.Media.Brushes.Black,
        };

        var host = new PresentHost
        {
            InitialWidth = 1280,
            InitialHeight = 720,
            VSync = vsync,
        };

        if (stress)
        {
            // 压力模式：挂产品管线全链 + 事件节拍。内存采样/斜率结论复用 Bench 的同一套机制——
            // 泄漏对比的前提是"测法不变、只换负载"，两套采样器就没法前后比了
            var stressBench = new StressBench(host, seed: 20261015);
            var memProbe = new StressMemProbe(host, stressBench, warmup);
            window.Content = host;
            window.Show();
            var codeS = app.Run(window);
            if (host.Error is not null)
            {
                Console.WriteLine($"[Bench][FAIL] GPU 路初始化失败：{host.Error}");
                return 2;
            }
            Console.WriteLine(memProbe.Conclusion());
            Console.WriteLine("[Bench] 结束：退出码 0（压力剖面跑完；曲线斜率即泄漏判据，对照稳态长跑读）");
            return codeS == 0 ? 0 : codeS;
        }

        var bench = new Bench(host, seconds, warmup, vsync);
        window.Content = host;

        if (seconds > 0)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                window.Close();
            };
            timer.Start();
        }

        window.Show();
        var code = app.Run(window);
        bench.Dispose(); // Shutdown 已在渲染线程放过就是空操作

        if (host.Error is not null)
        {
            Console.WriteLine($"[Bench][FAIL] GPU 路初始化失败：{host.Error}");
            return 2;
        }

        if (!bench.ShutdownCompleted)
            Console.WriteLine("[Bench][WARN] 渲染线程未走收尾（Shutdown 未触发）：内存/显存读数可能不完整");

        // app.Run 返回时 DestroyWindowCore → StopRender 已 Join 渲染线程，所以这里读采样结果是
        // 「线程已退出」之后的读，不需要再加锁
        var snap = bench.Snapshot();
        Console.WriteLine(snap.Format());
        WriteCsv(csvPath, bench, snap);

        // 采样不足：两种表现都判——统计窗口内的样本太少（分位数无从成立），
        // 或 --seconds 内总出帧本来就少（多半是预热吃掉了整个窗口/根本没出帧）
        if (snap.Frames < MinFrames || snap.TotalFrames < MinFrames)
        {
            Console.WriteLine($"[Bench][FAIL] 采样不足：统计窗口内 {snap.Frames} 帧、总出帧 {snap.TotalFrames} 帧，"
                              + $"门槛 {MinFrames} 帧（--seconds {seconds} --warmup {warmup}）→ 不出任何性能结论");
            return 4;
        }

        Console.WriteLine("[Bench] 结束：退出码 0（跑完并出报告；数字仅用于测量管线与前后对比，非性能验收）");
        return code == 0 ? 0 : code;
    }

    /// <summary>逐帧 CSV 落盘（可选）。写失败只告警：CSV 是辅助复核手段，不影响退出码语义。</summary>
    private static void WriteCsv(string? path, Bench bench, FrameSnapshot snap)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            File.WriteAllText(path, bench.Csv());
            var rows = bench.SampledFrames;
            Console.WriteLine($"[Bench] CSV：{path}（表头 1 + 数据 {rows} 行，"
                              + $"与统计帧数一致={(rows == snap.Frames ? "是" : "否")}）");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Bench][WARN] CSV 写入失败：{ex.Message}");
        }
    }
}

/// <summary>
/// 最小负载基准：PresentHost（子窗口 + D3D11 flip 交换链）里每帧 `Clear` + 一行 `DrawText`。
/// 刻意**不**挂 PvPipeline/PvStageWindow/PvStageRenderer（M4 正在改这些文件，也免得探针长成第二套
/// 产品路径），也不跑效果链——本线只立测量管线，不对产品路径下性能结论。
///
/// 线程模型：PresentHost 的事件全在渲染线程上触发（PresentHost.cs 的契约），所以这里是单写者；
/// 报告在渲染线程退出（StopRender 的 Join）之后才读，不加锁也不会撕裂。
/// </summary>
internal sealed class Bench : IDisposable
{
    /// <summary>内存曲线的采样间隔（秒）：长跑口径就是「每 30s 一行」。</summary>
    private const double MemSampleIntervalSec = 30.0;

    private readonly PresentHost _host;
    private readonly double _totalSeconds;
    private readonly double _warmupSeconds;
    private readonly FrameStats _intervals = new();
    private readonly FrameStats _cpu = new();
    private readonly Stopwatch _sw = new();

    /// <summary>内存曲线的采样点（渲染线程写，报告在渲染线程退出后读，与其余采样同契约）。</summary>
    private readonly List<MemSample> _memSamples = new();

    private D3DDevice? _gpu;
    private D2DHost? _d2d;
    private ID2D1Bitmap1? _target;
    private int _tw = 1280;
    private int _th = 720;
    private bool _vsync;
    private string _adapter = "(未建)";
    private string _featureLevel = "(未建)";
    private bool _software;

    private long _prepareTs;
    private long _lastFrameTs;
    private int _totalFrames;
    private bool _warmed;
    private double _sampleStartSec;
    private double _nextMemSampleSec;
    private int _framesAtLastMemSample;
    private bool _vramWarned;
    private int _gc0Base, _gc1Base, _gc2Base;
    private long _allocBase;
    private int _gc0, _gc1, _gc2;
    private double _allocBytes;
    private double _sampledSeconds;
    private long _workingSet;
    private long _privateBytes;
    private long _vramUsage = -1;
    private long _vramBudget = -1;
    private bool _shutdownRan;

    public Bench(PresentHost host, double totalSeconds, double warmupSeconds, bool vsync)
    {
        _host = host;
        _totalSeconds = totalSeconds;
        _warmupSeconds = Math.Max(0, warmupSeconds);
        _vsync = vsync;
        host.Ready += OnReady;
        host.Prepare += OnPrepare;
        host.Frame += OnFrame;
        host.Resized += OnResized;
        host.Shutdown += OnShutdown;
    }

    /// <summary>渲染循环已退出、资源已在本线程收尾（报告完整性用）。</summary>
    public bool ShutdownCompleted => _shutdownRan;

    /// <summary>统计窗口内的帧数。</summary>
    public int SampledFrames => _intervals.Count;

    /// <summary>逐帧 CSV（表头 + 出帧间隔 + CPU 提交耗时）。</summary>
    public string Csv() => FrameStats.ToCsv(_intervals, _cpu);

    private void OnReady(PresentHost host)
    {
        _gpu = host.Gpu;
        if (_gpu is null) return;
        _adapter = _gpu.AdapterName;
        _featureLevel = _gpu.FeatureLevel.ToString();
        _software = _gpu.IsSoftware;
        _vsync = host.Target?.VSync ?? _vsync;

        // 这里只建 D2DHost：Immediate2D 就是它建好的那一个（D2DHost.cs:35），
        // 在同一个设备上下文上再建一个绘制面没有意义，还会多一份画刷缓存
        _d2d = D2DHost.Create(_gpu);
        BindTarget();
        Baseline();
        _sw.Restart();
        Console.WriteLine($"[Bench] 就绪：{_adapter}｜feature level {_featureLevel}｜"
                          + $"软件光栅={(_software ? "是" : "否")}｜VSync={(_vsync ? "开" : "关")}｜目标 {_tw}x{_th}");
    }

    private void OnResized(PresentHost host) => BindTarget();

    private void OnPrepare(PresentHost host) => _prepareTs = Stopwatch.GetTimestamp();

    private void OnFrame(PresentHost host, double dt)
    {
        var frameStart = Stopwatch.GetTimestamp();
        var t = _sw.Elapsed.TotalSeconds;

        if (_d2d is not null && _target is not null)
        {
            var d = _d2d.Draw;
            d.SetTarget(_target, _tw, _th);
            d.Begin();
            d.Clear(new Color4(0.04f, 0.04f, 0.06f, 1f));
            d.DrawText($"PerfBench 短跑 · {host.Frames} 帧", _tw / 2f, _th / 2f, TextAlign2D.Center,
                d.Fonts.Get("Consolas", 24f),
                new Paint2D { Brush = new SolidBrush2D(new Color4(1f, 1f, 1f, 1f)) });
            d.End();
        }

        var frameEnd = Stopwatch.GetTimestamp();
        _totalFrames++;

        if (!_warmed && t >= _warmupSeconds)
        {
            // 预热窗口的资源创建/着色器编译/字体装载不是稳态：基线在这里才取
            _warmed = true;
            _sampleStartSec = t;
            Baseline();
        }

        if (!_warmed) return;

        // 出帧间隔：相邻两次 Frame 回调的时间差（首帧没有「上一帧」→ 不采样）。
        // 含 vsync 等待与调度抖动——所以它是「出帧节奏」，**不是** GPU 耗时。
        if (_lastFrameTs != 0)
            _intervals.Sample((frameStart - _lastFrameTs) * 1000.0 / Stopwatch.Frequency);
        _lastFrameTs = frameStart;

        // CPU 提交耗时：Prepare 起（含）到 Frame 回调画完（含）= 一帧的命令入队量
        if (_prepareTs != 0)
            _cpu.Sample((frameEnd - _prepareTs) * 1000.0 / Stopwatch.Frequency);

        // 内存曲线：每 30s 一行 stdout 采样。取数留在渲染线程上做——显存查询必须在设备存活期内
        // （PresentHost 只在渲染线程放设备），短跑没到 30s 就一行都不会打，行为与从前一致
        var sinceSample = t - _sampleStartSec;
        if (sinceSample >= _nextMemSampleSec) TakeMemSample(sinceSample);
    }

    /// <summary>一次内存曲线采样：读工作集/显存/GC，记进曲线并把这一行打到 stdout（渲染线程内调用）。</summary>
    private void TakeMemSample(double sinceSampleSec)
    {
        // 下一拍按「本次之后 30s」排，而不是「上一拍之后 30s」：一次卡顿不该让随后几帧连着补采样
        // （补出来的读数属于同一瞬间，会把这 30s 的斜率带歪）
        _nextMemSampleSec = sinceSampleSec + MemSampleIntervalSec;

        var (vramUsage, _) = QueryVram();
        var sample = new MemSample(
            Minute: sinceSampleSec / 60.0,
            FramesInInterval: _totalFrames - _framesAtLastMemSample,
            WorkingSetBytes: Process.GetCurrentProcess().WorkingSet64,
            VramUsageBytes: vramUsage,
            Gc0: GC.CollectionCount(0) - _gc0Base,
            Gc1: GC.CollectionCount(1) - _gc1Base,
            Gc2: GC.CollectionCount(2) - _gc2Base);
        _framesAtLastMemSample = _totalFrames;
        _memSamples.Add(sample);
        Console.WriteLine(sample.Line());
    }

    private void OnShutdown(PresentHost host)
    {
        // 采样收尾必须在这里做：PresentHost 保证 Shutdown 先于 Target/Gpu 释放（PresentHost.cs:180-195），
        // 所以此刻查适配器显存还安全；跑完再查就是碰已释放的设备
        (_vramUsage, _vramBudget) = QueryVram();

        var allocDelta = GC.GetTotalAllocatedBytes(precise: false) - _allocBase;
        _gc0 = GC.CollectionCount(0) - _gc0Base;
        _gc1 = GC.CollectionCount(1) - _gc1Base;
        _gc2 = GC.CollectionCount(2) - _gc2Base;
        _allocBytes = FrameStats.AllocBytesPerFrame(allocDelta, _intervals.Count);

        var p = Process.GetCurrentProcess();
        _workingSet = p.WorkingSet64;
        _privateBytes = p.PrivateMemorySize64;
        _sampledSeconds = _warmed ? Math.Max(0, _sw.Elapsed.TotalSeconds - _sampleStartSec) : 0;
        _shutdownRan = true;
        _gpu = null; // 设备随后由 PresentHost 释放，留着引用就是给后来人埋 use-after-free

        // D2D 资源必须在本线程释放：换线程释放就是 use-after-free 的源头
        _target?.Dispose();
        _target = null;
        _d2d?.Dispose();
        _d2d = null;
    }

    /// <summary>取 GC/分配基线（启动与预热结束各取一次），并复位内存曲线的采样节拍。</summary>
    private void Baseline()
    {
        _gc0Base = GC.CollectionCount(0);
        _gc1Base = GC.CollectionCount(1);
        _gc2Base = GC.CollectionCount(2);
        _allocBase = GC.GetTotalAllocatedBytes(precise: false);
        // 曲线与 GC 基线同源（都从预热结束那一刻起算）；OnReady 那次调用只是把状态归零
        _nextMemSampleSec = MemSampleIntervalSec;
        _framesAtLastMemSample = _totalFrames;
    }

    /// <summary>
    /// 显存采样：IDXGIAdapter3.QueryVideoMemoryInfo(0, Local) 的 CurrentUsage/Budget。
    /// 适配器为 null（WARP 回退）/接口不可得时记 **-1**——**不记 0**：0 会被读成「用了 0 字节」
    /// 这种真实测量，而 WARP 的 Local 段预算根本没有意义。
    /// 只允许在设备的存活期内调用：OnShutdown，或渲染线程的帧回调（长跑的 30s 采样）。
    /// </summary>
    private (long Usage, long Budget) QueryVram()
    {
        try
        {
            if (_gpu?.Adapter is null) return (-1, -1);
            using var adapter3 = _gpu.Adapter.QueryInterfaceOrNull<IDXGIAdapter3>();
            if (adapter3 is null) return (-1, -1);
            var info = adapter3.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local);
            return ((long)info.CurrentUsage, (long)info.Budget);
        }
        catch (Exception ex)
        {
            // 长跑每 30s 都会撞上同一条失败：只报一次，读数照旧记 -1（免得 20 行同样的告警埋掉采样行）
            if (!_vramWarned)
            {
                _vramWarned = true;
                Console.WriteLine($"[Bench][WARN] 显存采样不可得（记 -1）：{ex.Message}");
            }

            return (-1, -1);
        }
    }

    private void BindTarget()
    {
        _target?.Dispose();
        _target = null;
        if (_d2d is null || _host.Target is null) return;

        using var back = _host.Target.GetBackBuffer();
        using var surface = back.QueryInterface<IDXGISurface>();
        _tw = _host.Target.Width;
        _th = _host.Target.Height;
        _target = _d2d.Ctx.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
            new DCommonPixelFormat(Format.B8G8R8A8_UNorm, DCommonAlphaMode.Premultiplied),
            96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw));
    }

    /// <summary>把本次采样的读数拼成一份快照（纯数据，格式化与字段校验都在 FrameStats 那边）。</summary>
    public FrameSnapshot Snapshot()
    {
        // 渲染线程没走收尾（理论上只有线程卡死）时，内存读数现场补一次；显存不补——
        // 那要碰已被 PresentHost 释放的设备
        if (!_shutdownRan)
        {
            var p = Process.GetCurrentProcess();
            _workingSet = p.WorkingSet64;
            _privateBytes = p.PrivateMemorySize64;
        }

        return new FrameSnapshot(
            new BenchEnv(_adapter, _featureLevel, _software, _tw, _th, _vsync,
                _totalSeconds, _warmupSeconds, _sampledSeconds),
            _intervals.Count,
            _totalFrames,
            _intervals.Percentile(0.50), _intervals.Percentile(0.95), _intervals.Percentile(0.99), _intervals.Max,
            _cpu.Percentile(0.50), _cpu.Percentile(0.95), _cpu.Percentile(0.99), _cpu.Max,
            _gc0, _gc1, _gc2, _allocBytes,
            _workingSet, _privateBytes, _vramUsage, _vramBudget,
            // 曲线结论只在有 30s 采样点时给：短跑报告的形状与从前一致（不多出两行「不可得」）
            _memSamples.Count == 0
                ? null
                : MemCurve.Summarize("工作集曲线", MemCurve.WorkingSetPoints(_memSamples)),
            _memSamples.Count == 0
                ? null
                : MemCurve.Summarize("显存曲线", MemCurve.VramPoints(_memSamples)));
    }

    /// <summary>
    /// 兜底释放（Shutdown 已在渲染线程放过就是空操作）。跨线程释放是 use-after-free 的源头，
    /// 所以只在 Shutdown 跑过之后才动这些资源；渲染线程没退出时宁可让资源随进程结束。
    /// </summary>
    public void Dispose()
    {
        if (!_shutdownRan) return;
        _target?.Dispose();
        _target = null;
        _d2d?.Dispose();
        _d2d = null;
    }
}
