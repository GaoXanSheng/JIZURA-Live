// 性能专项临时控制台（不入库的剖析基建，任务结束后保留在 tools/prof/）：
//   dotnet run --project tools/prof/PerfCli -- bench [N] [startSec]   帧计时基线（koma 步进采样，逐帧 ms）
//   dotnet run --project tools/prof/PerfCli -- pngs <dir> [N] [start] 固定帧序列落 PNG（像素一致性基准）
//   dotnet run --project tools/prof/PerfCli -- diff <dirA> <dirB>     逐像素 diff（±3 内算抖动）
// 大工程：125 行 LRC、4:41（281s）、默认风格 + 全表现包，与用户反馈的卡顿工程同量级。

using System.Diagnostics;
using System.IO;
using JizuraLive.Engine;
using SkiaSharp;

var root = FindRoot();
EngineBoot.InstallPacks();
SysFonts();

var project = BigProject();
var sw = Stopwatch.StartNew();
var plan = Planner.Plan(project, null);
Console.WriteLine($"[plan] cuts={plan.Cuts.Count} dur={plan.Duration:F1}s events={plan.Events.Count} ({sw.ElapsedMilliseconds}ms) W={plan.W} H={plan.H} fps={plan.Fps} koma={project.Fx.Koma}");

var cmd = args.Length > 0 ? args[0] : "bench";
// bench N start | pngs DIR N start | diff A B（pngs 按位置取 N/start，其余命令取 args[1]/args[2] 的数字）
var n = 30;
var start = 30.0;
if (cmd == "pngs")
{
    if (args.Length > 2 && int.TryParse(args[2], out var n1)) n = n1;
    if (args.Length > 3 && double.TryParse(args[3], System.Globalization.CultureInfo.InvariantCulture, out var s1)) start = s1;
}
else
{
    if (args.Length > 1 && int.TryParse(args[1], out var n2)) n = n2;
    if (args.Length > 2 && double.TryParse(args[2], System.Globalization.CultureInfo.InvariantCulture, out var s2)) start = s2;
}

switch (cmd)
{
    case "bench":
    {
        // koma=12 → 步长 2/24s；按步进采样保证每帧都是真渲染（整帧缓存不命中）
        var step = Planner.StepDur(plan.Fx, plan.Fps);
        var times = Enumerable.Range(0, n).Select(i => start + i * step).ToArray();
        // 预热：JIT + 字体 + glyph 缓存（与导出工况一致：热路径）
        using (var warm = new Renderer())
        using (var s = SKSurface.Create(new SKImageInfo(plan.W, plan.H)))
        {
            foreach (var t in times.Take(3)) warm.Frame(s.Canvas, plan, t);
        }
        // 阶段剖析接线（--stages 时输出各 Renderer 阶段平均 ms）
        var wantStages = args.Contains("--stages");
        var stageSum = new Dictionary<string, double>();
        var stageN = new Dictionary<string, int>();
        if (wantStages)
            Renderer.StageHook = (name, ms) =>
            {
                stageSum[name] = stageSum.GetValueOrDefault(name) + ms;
                stageN[name] = stageN.GetValueOrDefault(name) + 1;
            };
        var samples = new double[n];
        long alloc0 = 0, alloc1 = 0;
        var gc0 = (GOC0: GC.CollectionCount(0), G1: GC.CollectionCount(1), G2: GC.CollectionCount(2));
        using var renderer = new Renderer();
        using var surface = SKSurface.Create(new SKImageInfo(plan.W, plan.H));
        for (var i = 0; i < n; i++)
        {
            if (i == 1) { alloc0 = GC.GetTotalAllocatedBytes(); gc0 = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)); }
            surface.Canvas.Clear(SKColors.Black);
            var f = Stopwatch.StartNew();
            renderer.Frame(surface.Canvas, plan, times[i]);
            surface.Canvas.Flush();
            f.Stop();
            samples[i] = f.Elapsed.TotalMilliseconds;
            if (!wantStages)
                Console.WriteLine($"[frame {i:D3}] t={times[i],8:F3}  {samples[i],8:F2} ms");
        }
        Renderer.StageHook = null;
        alloc1 = GC.GetTotalAllocatedBytes();
        var gc1 = (GOC0: GC.CollectionCount(0), G1: GC.CollectionCount(1), G2: GC.CollectionCount(2));
        Array.Sort(samples);
        double avg = samples.Average(), p50 = samples[n / 2], p95 = samples[(int)(n * 0.95)];
        Console.WriteLine($"[bench] n={n} avg={avg:F2}ms p50={p50:F2}ms p95={p95:F2}ms max={samples[^1]:F2}ms min={samples[0]:F2}ms  → {1000 / avg:F1} fps");
        Console.WriteLine($"[gc] alloc={((alloc1 - alloc0) / Math.Max(1, n - 1) / 1024.0 / 1024.0):F2} MB/帧  gen0={gc1.GOC0 - gc0.GOC0} gen1={gc1.G1 - gc0.G1} gen2={gc1.G2 - gc0.G2}（n-1 帧合计）");
        if (wantStages)
        {
            Console.WriteLine("[stages] 平均 ms/帧:");
            foreach (var kv in stageSum.OrderByDescending(kv => kv.Value))
                Console.WriteLine($"  {kv.Key,-12} {kv.Value / Math.Max(1, stageN[kv.Key]),7:F2} ms  (calls={stageN[kv.Key]})");
        }
        break;
    }
    case "pngs":
    {
        var dir = args.Length > 1 ? args[1] : Path.Combine(root, "obj", "perf-pngs");
        Directory.CreateDirectory(dir);
        var step = Planner.StepDur(plan.Fx, plan.Fps);
        var times = Enumerable.Range(0, n).Select(i => start + i * step).ToArray();
        using var renderer = new Renderer();
        for (var i = 0; i < n; i++)
        {
            using var surface = SKSurface.Create(new SKImageInfo(plan.W, plan.H));
            surface.Canvas.Clear(SKColors.Black);
            renderer.Frame(surface.Canvas, plan, times[i]);
            surface.Canvas.Flush();
            using var img = surface.Snapshot();
            var path = Path.Combine(dir, $"f{i:D3}_t{times[i].ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}.png");
            using var png = img.Encode(SKEncodedImageFormat.Png, 100);
            using var fs = File.Create(path);
            png.SaveTo(fs);
            Console.WriteLine($"[png] {path}");
        }
        break;
    }
    case "diff":
    {
        var a = args[1];
        var b = args[2];
        var fa = Directory.GetFiles(a, "*.png").OrderBy(p => p).ToArray();
        var fb = Directory.GetFiles(b, "*.png").OrderBy(p => p).ToArray();
        if (fa.Length != fb.Length) { Console.WriteLine($"[diff] 帧数不同 {fa.Length} vs {fb.Length}"); return 2; }
        long worst = 0; string worstName = "";
        long totalDiff = 0, totalOver = 0, totalPx = 0;
        for (var i = 0; i < fa.Length; i++)
        {
            if (!Path.GetFileName(fa[i]).Equals(Path.GetFileName(fb[i]), StringComparison.OrdinalIgnoreCase))
            { Console.WriteLine($"[diff] 文件名不配对: {fa[i]} vs {fb[i]}"); return 2; }
            using var ba = SKBitmap.FromImage(SKImage.FromEncodedData(fa[i]))!;
            using var bb = SKBitmap.FromImage(SKImage.FromEncodedData(fb[i]))!;
            if (ba.Width != bb.Width || ba.Height != bb.Height) { Console.WriteLine($"[diff] 尺寸不同 {fa[i]}"); return 2; }
            var pa = ba.Bytes;
            var pb = bb.Bytes;
            long over = 0, diffPx = 0;
            for (var p = 0; p + 3 < pa.Length; p += 4)
            {
                int dr = Math.Abs(pa[p] - pb[p]), dg = Math.Abs(pa[p + 1] - pb[p + 1]), db = Math.Abs(pa[p + 2] - pb[p + 2]);
                var d = Math.Max(dr, Math.Max(dg, db));
                if (d > 0) diffPx++;
                if (d > 3) over++;
            }
            var px = (long)ba.Width * ba.Height;
            totalPx += px; totalDiff += diffPx; totalOver += over;
            var pct = over * 100.0 / px;
            if (over > worst) { worst = over; worstName = Path.GetFileName(fa[i]); }
            Console.WriteLine($"[diff] {Path.GetFileName(fa[i])}: >3diff={over}/{px} ({pct:F4}%)  anydiff={diffPx * 100.0 / px:F2}%");
        }
        Console.WriteLine($"[diff] TOTAL: >3diff = {totalOver * 100.0 / totalPx:F4}%（阈值 ≤0.5%），worst={worstName} {worst * 100.0 / (totalPx / fa.Length):F4}%");
        return totalOver * 100.0 / totalPx <= 0.5 ? 0 : 2;
    }
    default:
        Console.WriteLine("unknown cmd");
        return 1;
}
return 0;

static string FindRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d != null && !File.Exists(Path.Combine(d.FullName, "JizuraLive.csproj"))) d = d.Parent!;
    return d!.FullName;
}

/// <summary>冒烟/对拍同款字体兜底：全部字体键 → 同一系统 CJK 字体。</summary>
static void SysFonts()
{
    var fam = new[] { "Yu Gothic", "Meiryo", "MS Gothic", "Microsoft YaHei UI", "SimSun", "Arial" }
        .FirstOrDefault(f =>
        {
            try
            {
                using var t = SKTypeface.FromFamilyName(f);
                return t != null && string.Equals(t.FamilyName, f, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }) ?? "Arial";
    foreach (var kv in JFonts.Table.ToArray())
        JFonts.AddUserFont(kv.Key, kv.Value.Label, fam, kv.Value.Weight, kv.Value.Kind);
}

/// <summary>大工程：125 行 LRC、4:41（281s）。文本池轮转拼行，覆盖假名/汉字/拉丁/标点。</summary>
static Project BigProject()
{
    string[] pool =
    {
        "夜明けの色を覚えてる", "ほどけた声が遠くで鳴った", "ねえ、まだ間に合うかな", "*透明*なままじゃ終われない!",
        "グラスの中の melody が", " static な街に溶けてく", "きみの名前を三回唱えた", " Level 4 の心臓で踊れ",
        "雨粒は glass を割って", "Tell me why, tell me why!", "信号待ちの空に float", "さよならの練習 was 失敗",
        "深呼吸の数だけ距離が", " PLAN B なんてないけど", "过去不见了、未来还没来", "真夜中の三分球 like that",
        "窓を開けたら west side", " circuit of the lonely night", "ため息がカーテンを揺らす", "hello goodbye, one more time!",
    };
    var sb = new System.Text.StringBuilder();
    var startT = 8.0;
    const int lines = 125;
    const double span = 271.0; // 8s → 279s ≈ 4:39，尾后留白到 4:41
    for (var i = 0; i < lines; i++)
    {
        var t = startT + span * i / lines;
        var mm = (int)(t / 60);
        var sec = t - mm * 60;
        sb.Append($"[{mm:D2}:{sec.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}]{pool[i % pool.Length]}\n");
    }
    return new Project
    {
        Lyrics = sb.ToString(),
        Title = "PERF",
        Artist = "bench",
        Style = "noir",
        Extra = true,
        Wa = true,
        Typo = true,
        Kinetic = true,
        Seed = 20260922,
        Enabled = Reg.DefaultEnabled(),
    };
}
