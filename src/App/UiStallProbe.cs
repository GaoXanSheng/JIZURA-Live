using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using JizuraLive.Logging;

namespace JizuraLive.App;

public static class UiStallProbe
{
    private const int StallMs = 600;
    private const int SampleMs = 1000;

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private static readonly ConcurrentDictionary<int, string> Activities = new();
    private static int _uiManagedId;
    private static int _uiWin32Id;

    public static void Mark(string activity) => Activities[Environment.CurrentManagedThreadId] = activity;
    public static void MarkIdle() => Activities.TryRemove(Environment.CurrentManagedThreadId, out _);

    private static long _heartbeat;
    private static long _stallStart;
    private static int _gc0, _gc1, _gc2;
    private static int _stalling;
    private static Process? _proc;
    private static TimeSpan _uiCpu;

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
                Interlocked.Exchange(ref _stalling, 0);
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
        catch {                    }
        return -1;
    }

    private static string UiActivity()
    {
        var ui = Volatile.Read(ref _uiManagedId);
        return ui == 0 ? "idle" : Activities.GetValueOrDefault(ui, "idle");
    }

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
