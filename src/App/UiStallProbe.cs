using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using JizuraLive.Logging;

namespace JizuraLive.App;

public static class UiStallProbe
{

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private static readonly ConcurrentDictionary<int, string> Activities = new();

    public static void Mark(string activity) => Activities[Environment.CurrentManagedThreadId] = activity;
    public static void MarkIdle() => Activities.TryRemove(Environment.CurrentManagedThreadId, out _);

    private static int _gc0, _gc1, _gc2;

}
