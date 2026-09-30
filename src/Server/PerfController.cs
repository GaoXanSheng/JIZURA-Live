using System.Diagnostics;
using System.Net.Http;
using System.Text;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using Newtonsoft.Json;

namespace JizuraLive.Server;

/// <summary>
/// 渲染调试面板的系统指标端点：
///   GET /api/perf/sys —— 应用相关进程（主进程 + CefSharp.BrowserSubprocess）的 CPU/GPU/内存占用，
///                        内部 500ms 缓存采样；GPU 走 GpuCounter（pdh.dll 系统计数器）。
///   GET /api/perf/cdp-targets?port= —— 服务端转发 CEF CDP 的 /json/list，页面直连被 CORS 拦截时的兜底。
/// </summary>
internal sealed class PerfController : WebApiController
{
    private const int SubprocessSampleMax = 32;

    private static readonly object Gate = new();
    private static readonly GpuCounter Gpu = new();
    private static readonly Dictionary<int, (TimeSpan Cpu, long AtMs)> CpuPrev = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private static string? _cacheJson;
    private static long _cacheAt;

    [Route(HttpVerbs.Get, "/api/perf/sys")]
    public async Task Sys()
    {
        // Sample 是同步快照（≤几毫秒）；SendStringAsync 必须 await——
        // 前端 1s 轮询在页面刷新时会中途断开连接，未 await 的写入
        // 异常会以 UnobservedTaskException 的形式打到 finalizer 线程
        var json = Sample();
        await HttpContext.SendStringAsync(json, "application/json; charset=utf-8", Encoding.UTF8);
    }

    [Route(HttpVerbs.Get, "/api/perf/cdp-targets")]
    public async Task CdpTargets()
    {
        int port = 9222;
        if (int.TryParse(HttpContext.Request.QueryString["port"], out int p) && p is >= 1 and <= 65535)
            port = p;
        try
        {
            var body = await Http.GetStringAsync($"http://127.0.0.1:{port}/json/list");
            await HttpContext.SendStringAsync(body, "application/json; charset=utf-8", Encoding.UTF8);
        }
        catch
        {
            Response.StatusCode = 502;
            await HttpContext.SendStringAsync("{\"ok\":false,\"error\":\"CDP 端口不可达（设置里开启调试端口并重启应用）\"}",
                "application/json; charset=utf-8", Encoding.UTF8);
        }
    }

    private static string Sample()
    {
        lock (Gate)
        {
            long now = Environment.TickCount64;
            if (_cacheJson != null && now - _cacheAt < 500)
                return _cacheJson;

            var main = Process.GetCurrentProcess();
            var rows = new List<ProcRow>();
            var pids = new List<int> { main.Id };
            CollectProcess(main, "main", now, rows, pids);

            Process[]? subs = null;
            try
            {
                subs = Process.GetProcessesByName("CefSharp.BrowserSubprocess");
                for (int i = 0; i < subs.Length && i < SubprocessSampleMax; i++)
                    CollectProcess(subs[i], "cef", now, rows, pids);
            }
            catch
            {
                /* 子进程枚举失败不影响主进程数据 */
            }
            finally
            {
                if (subs != null)
                    foreach (var sp in subs)
                        if (sp.Id != main.Id)
                            sp.Dispose();
            }

            // GPU：对全部目标 pid 一次采样，按 pid 合并（无实例/样本不足的 pid = -1 未知）
            var gpu = Gpu.Sample(pids);
            double totalCpu = 0, totalGpu = 0, totalMem = 0;
            bool gpuKnown = false;
            foreach (var r in rows)
            {
                if (r.Cpu >= 0) totalCpu += r.Cpu;
                if (gpu.TryGetValue(r.Pid, out var g))
                {
                    r.Gpu = Math.Round(g, 1);
                    totalGpu += g;
                    gpuKnown = true;
                }

                if (r.MemMB >= 0) totalMem += r.MemMB;
            }

            PruneCpuPrev(pids);

            _cacheJson = JsonConvert.SerializeObject(new
            {
                ok = true,
                ts = now,
                total = new
                {
                    cpu = Math.Round(totalCpu, 1),
                    gpu = gpuKnown ? Math.Round(totalGpu, 1) : -1,
                    memMB = (long)totalMem,
                },
                procs = rows,
            });
            _cacheAt = now;
            return _cacheJson;
        }
    }

    private static void CollectProcess(Process p, string type, long now, List<ProcRow> rows, List<int> pids)
    {
        var row = new ProcRow { Pid = p.Id, Name = p.ProcessName, Type = type };
        try
        {
            var cpuTime = p.TotalProcessorTime;
            if (CpuPrev.TryGetValue(p.Id, out var prev) && now > prev.AtMs)
                row.Cpu = (cpuTime - prev.Cpu).TotalMilliseconds / (now - prev.AtMs) / Environment.ProcessorCount *
                          100.0;
            CpuPrev[p.Id] = (cpuTime, now);
        }
        catch
        {
            /* 进程可能在枚举与读取之间退出，CPU 保持 -1 未知 */
        }

        try
        {
            row.MemMB = (long)(p.WorkingSet64 / 1048576.0);
        }
        catch
        {
        }

        rows.Add(row);
        pids.Add(p.Id);
    }

    private static void PruneCpuPrev(List<int> alivePids)
    {
        if (CpuPrev.Count <= alivePids.Count + 8)
            return;
        var alive = new HashSet<int>(alivePids);
        var dead = new List<int>();
        foreach (var pid in CpuPrev.Keys)
            if (!alive.Contains(pid))
                dead.Add(pid);
        foreach (var pid in dead)
            CpuPrev.Remove(pid);
    }

    private sealed class ProcRow
    {
        public double Cpu;
        public double Gpu = -1;
        public long MemMB;
        public string Name = "";
        public int Pid;
        public string Type = "";
    }
}