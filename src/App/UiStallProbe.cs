using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using JizuraLive.Logging;

namespace JizuraLive.App;

/// <summary>
/// UI 线程卡顿取证探针（配合 MainWindow 的 UI探针——那边报「卡了多久」，这里报「卡在哪」）：
/// 心跳 = UI 线程的探针 tick（Background 优先级）；看门狗线程每秒查缺口，超阈值逐秒取证一行：
///   · 活动    = UI 线程经 <see cref="Mark"/> 登记的当前操作（文件写 / MCP 工具 / 同步重规划 /
///               D3D 设备创建与共享纹理绑定……）。按调用线程分槽：后台写盘的 Mark 不会污染
///               UI 线程的报告（实测教训：FlushAutosave 的 Timer 线程写 autosave 时把 UI 卡顿的
///               活动名覆盖成了 file:autosave.json）。
///   · GC增量  = 免堆栈的 GC 归因。基线随心跳持续刷新——卡顿第一秒内的 GC 也算得进
///               （基线只在发现卡顿时取的话，第一秒的收集会被漏掉，增量永远 0）。
///   · UI CPU  = UI 线程内核 CPU 时间增量。烧了 CPU = 代码在忙（排版/JIT/解析）；
///               ≈0 = UI 线程在等内核（D3D 驱动/锁/换页/被抢占）。「忙 vs 等」一锤定音。
///   · 线程池  = 后台任务风暴把池抽干的迹象（空闲数掉到 0）。
/// 心跳恢复由 <see cref="Heartbeat"/> 报总时长。日志键：[UI卡顿]。
/// </summary>
public static class UiStallProbe
{
    private const int StallMs = 600;   // 心跳缺口超此即开始取证（感知阈值 33ms 之上取个低噪声值）
    private const int SampleMs = 1000; // 卡顿中每秒补一行（长卡顿的演进过程全程留痕）

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    // 每线程一个活动槽：Mark/MarkIdle 写调用者自己的槽，看门狗只读 UI 线程的槽
    private static readonly ConcurrentDictionary<int, string> Activities = new();
    private static int _uiManagedId;  // UI 线程托管 ID（首次 Heartbeat 登记）
    private static int _uiWin32Id;    // UI 线程内核 ID（ProcessThread.Id 对得上）

    public static void Mark(string activity) => Activities[Environment.CurrentManagedThreadId] = activity;
    public static void MarkIdle() => Activities.TryRemove(Environment.CurrentManagedThreadId, out _);

    private static long _heartbeat;
    private static long _stallStart;
    private static int _gc0, _gc1, _gc2;   // GC 基线：随心跳刷新（防「第一秒盲区」）
    private static int _stalling;          // 1 = 本次卡顿已开始取证
    private static Process? _proc;
    private static TimeSpan _uiCpu;        // 上次采样的 UI 线程累计 CPU（内核时间）

    public static void Start()
    {
        Volatile.Write(ref _heartbeat, Stopwatch.GetTimestamp());
        var t = new Thread(Watchdog)
        {
            IsBackground = true,
            Name = "UiStallProbe",
        };
        t.Start();
    }

    private static void Watchdog()
    {
        while (true)
        {
            Thread.Sleep(SampleMs);
            var now = Stopwatch.GetTimestamp();
            var gapMs = (now - Volatile.Read(ref _heartbeat)) * 1000.0 / Stopwatch.Frequency;
            if (gapMs < StallMs)
            {
                Interlocked.Exchange(ref _stalling, 0); // 心跳恢复的「结束」由 Heartbeat 报（UI 线程知道精确时刻）
                continue;
            }
            if (Interlocked.Exchange(ref _stalling, 1) == 0)
            {
                _stallStart = now;
                _gc0 = GC.CollectionCount(0);
                _gc1 = GC.CollectionCount(1);
                _gc2 = GC.CollectionCount(2);
                Log.Warn($"[UI卡顿] 开始：UI 线程已 {gapMs:F0}ms 无心跳｜活动={UiActivity()}｜GC增量 {_GcDelta()}");
                continue;
            }
            ThreadPool.GetAvailableThreads(out var w, out var io);
            ThreadPool.GetMaxThreads(out var wm, out var iom);
            Log.Warn($"[UI卡顿] 进行中 {(now - _stallStart) * 1000.0 / Stopwatch.Frequency:F0}ms｜活动={UiActivity()}"
                     + $"｜GC增量 {_GcDelta()}"
                     + $"｜UI CPU {_UiCpuDelta():F0}ms"
                     + $"｜线程池空闲 {w}/{wm}（IO {io}/{iom}）");
        }
    }

    private static string _GcDelta() =>
        $"Gen0={GC.CollectionCount(0) - _gc0} Gen1={GC.CollectionCount(1) - _gc1} Gen2={GC.CollectionCount(2) - _gc2}";

    /// <summary>UI 线程本次采样间隔内烧掉的内核 CPU（毫秒）。-1 = 查不到（线程已退/快照失败）。</summary>
    private static double _UiCpuDelta()
    {
        var win32 = Volatile.Read(ref _uiWin32Id);
        if (win32 == 0) return -1;
        try
        {
            _proc ??= Process.GetCurrentProcess();
            _proc.Refresh();
            foreach (ProcessThread pt in _proc.Threads)
            {
                if (pt.Id != win32) continue;
                var total = pt.TotalProcessorTime;
                var delta = (total - _uiCpu).TotalMilliseconds;
                _uiCpu = total;
                return delta;
            }
        }
        catch { /* 进程快照失败，下一秒再试 */ }
        return -1;
    }

    private static string UiActivity()
    {
        var ui = Volatile.Read(ref _uiManagedId);
        return ui == 0 ? "idle" : Activities.GetValueOrDefault(ui, "idle");
    }

    /// <summary>UI 线程心跳（探针 tick 首行调用）。首次调用登记 UI 线程双 ID；GC 基线随心跳刷新；
    /// 卡顿结束后在此报总时长。</summary>
    public static void Heartbeat()
    {
        Interlocked.CompareExchange(ref _uiManagedId, Environment.CurrentManagedThreadId, 0);
        Interlocked.CompareExchange(ref _uiWin32Id, (int)GetCurrentThreadId(), 0);
        _gc0 = GC.CollectionCount(0);
        _gc1 = GC.CollectionCount(1);
        _gc2 = GC.CollectionCount(2);
        Volatile.Write(ref _heartbeat, Stopwatch.GetTimestamp());
        if (Interlocked.Exchange(ref _stalling, 0) == 1)
            Log.Info($"[UI卡顿] 结束：共 {(Stopwatch.GetTimestamp() - _stallStart) * 1000.0 / Stopwatch.Frequency:F0}ms（活动={UiActivity()}）");
    }
}
