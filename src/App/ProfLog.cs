using System.Text;
using JizuraLive.Engine;
using JizuraLive.Logging;

namespace JizuraLive.App;

/// <summary>
/// 可选性能探针（性能专项）：设环境变量 JIZURA_PROF=leaf 启动时开启，每 10s 往日志打一次
/// 引擎「叶子级」分配/耗时 Top 榜（站点 = layout/enter/hold/exit/decor/bg/fx 分支…）。
/// 用途：主程序实测 22MB/s 分配率 + gen2 停顿秒级冻结时，定位是哪些站点在灌 GC。
/// 默认关：LeafHook 为 null 时引擎侧的 Scope 全程空转，零开销零分配。
/// 覆盖范围 = 引擎里已插 Scope 的站点（Renderer.DrawCut 各层 / JLayouts 主轴 / JText 分层绘制）。
/// 注意：只统计调用线程（渲染线程），线程池/UI 线程的分配不在此列。
/// </summary>
internal static class ProfLog
{
    private static readonly object Lock = new();
    private static readonly Dictionary<string, (double Ms, long Alloc, long N)> Agg = new();
    private static Timer? _timer;

    public static void Install()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("JIZURA_PROF"), "leaf", StringComparison.OrdinalIgnoreCase))
            return;

        Prof.LeafHook = (site, ms, alloc, n) =>
        {
            lock (Lock)
            {
                var cur = Agg.TryGetValue(site, out var v) ? v : default;
                Agg[site] = (cur.Ms + ms, cur.Alloc + alloc, cur.N + n);
            }
        };
        _timer = new Timer(_ => Dump(), null, 10_000, 10_000);
        Log.Info("[ProfLog] 叶子级归因已开启（JIZURA_PROF=leaf）：每 10s 输出分配 Top10 / 耗时 Top5");
    }

    private static void Dump()
    {
        List<KeyValuePair<string, (double Ms, long Alloc, long N)>> snap;
        lock (Lock)
        {
            snap = Agg.OrderByDescending(kv => kv.Value.Alloc).Take(10).ToList();
            Agg.Clear();
        }
        if (snap.Count == 0) return;

        var sb = new StringBuilder("[ProfLog] 10s 分配 Top10（KB/s @ 站点；括号内为耗时 ms 与调用次数）");
        foreach (var (site, v) in snap)
            sb.Append($"\n  {v.Alloc / 1024.0,9:F0} KB/s  {site}  ({v.Ms:F1}ms ×{v.N})");
        Log.Info(sb.ToString());
    }
}
