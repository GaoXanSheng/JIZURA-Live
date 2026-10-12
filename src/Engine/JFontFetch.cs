using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using JizuraLive.App;
using JizuraLive.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkiaSharp;

// EngineRenderGate（渲染串行闸，与预览/导出共享引擎静态缓存的规矩）

namespace JizuraLive.Engine;

/// <summary>
/// 字体按需拉取（原前端 scripts/fetch-fonts.mjs「网络拉取 + 本地缓存」的 C# 对应物）。
///
/// 与浏览器的差别：Skia 读不了 woff2/woff，所以不能照抄 CDN 的切片 CSS，改从 Google Fonts
/// 的仓库镜像拉**完整 TTF**（含 CJK 全字库，一个家族一个文件，2–9MB）：
///   jsDelivr（cdn.jsdelivr.net / fastly.jsdelivr.net）→ raw.githubusercontent.com
/// 拉到即写进用户字体库（%LOCALAPPDATA%\JizuraLive\fonts），随后重扫目录、清字形缓存，
/// 画面热更新——用户不需要重启，也不用手动投字体。
///
/// 触发点：EnsureFonts（plan 用到的脸）/ StyleTab 选字体。整族都拉不到（离线、被墙、字体名
/// 对不上仓库路径）就维持系统气质栈兜底，功能不受影响；拉过的家族有本地缓存 + 持久账本
/// （.fetchlog.json，成功即不再联网，失败按 10 分钟→1 小时→24 小时递增退避，跨重启有效）。
/// 一次只跑一个家族（后台、低优先级），重复请求合并。
/// </summary>
public static class FontFetch
{
    /// <summary>总开关：默认关——只有 App 启动时按设置打开（Engine 层被测试/探针直接构造时
    /// 不该偷偷联网下字体；关掉就是纯本地：字体目录 + 系统栈）。</summary>
    public static bool Enabled { get; set; }

    /// <summary>拉到新字体后的回调（App 侧接重规划/重绘；引擎不反向依赖 UI）。</summary>
    public static Action<string>? OnFetched;

    // 仓库根：Google Fonts 官方仓库（OFL 协议，自托管合规）；双镜像，大陆网络也常通
    private static readonly string[] Hosts =
    {
        "https://cdn.jsdelivr.net/gh/google/fonts@main",
        "https://fastly.jsdelivr.net/gh/google/fonts@main",
        "https://raw.githubusercontent.com/google/fonts/main"
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    // 正在拉的家族键（合并同一家族的重复请求）；「拉过/刚失败」的退避由持久账本负责（见 Ledger）
    private static readonly Dictionary<string, DateTime> Tried = new();
    private static readonly object Gate = new();
    private static Task? _worker;

    /// <summary>字体名 → 仓库目录名（google/fonts 的 ofl/ 下按 family 小写去空格命名）。</summary>
    private static string DirOf(string family) => Regex.Replace(family.ToLowerInvariant(), "[^a-z0-9]", "");

    /// <summary>字重 → 仓库静态文件名里的风格名（缺的风格会自动下探到最近的重/常规）。</summary>
    private static string StyleOf(int weight) => weight switch
    {
        <= 150 => "Thin",
        <= 250 => "ExtraLight",
        <= 350 => "Light",
        <= 450 => "Regular",
        <= 550 => "Medium",
        <= 650 => "SemiBold",
        <= 750 => "Bold",
        <= 850 => "ExtraBold",
        _ => "Black"
    };

    /// <summary>候选文件名：可变字体（[wght]）优先，其次按风格名 / 常规退回。</summary>
    private static IEnumerable<string> FileCandidates(string family, int weight)
    {
        var flat = Regex.Replace(family, "[^A-Za-z0-9]", "");
        var style = StyleOf(weight);
        yield return flat + "[wght].ttf";
        yield return flat + "-" + style + ".ttf";
        yield return "static/" + flat + "-" + style + ".ttf";
        if (style != "Regular") yield return flat + "-Regular.ttf";
    }

    /// <summary>
    /// 请求按需补字：把 face 里「本地（目录/用户/系统）都没有、且没在拉/刚失败过」的家族排进队列。
    /// 传 family 与目标字重（同一家族不同字重各拉一份，文件名带字重区分）。
    /// </summary>
    public static void Request(IEnumerable<FontFace> faces)
    {
        if (!Enabled) return;
        var want = new List<(string Family, int Weight)>();
        foreach (var f in faces)
        {
            var fam = f.Family.Trim().Trim('"');
            if (fam.Length == 0 || fam is "sans-serif" or "serif" or "monospace" or "cursive" or "fantasy") continue;
            if (f.User || UserFontFamily(fam)) continue;          // 用户字体：本体在
            if (JFonts.InFontDirs(fam) || JFonts.IsFamilyInstalled(fam)) continue; // 目录里有 / 本机装了：不用拉
            want.Add((fam, f.Weight));
        }
        if (want.Count == 0) return;

        var queued = false;
        lock (Gate)
        {
            foreach (var (fam, weight) in want)
            {
                var key = fam + "|" + weight;
                if (SkipByLedger(key)) continue; // 拉过（成/败）→ 账本说了算
                if (Tried.TryGetValue(key, out var at))
                {
                    if (at == DateTime.MinValue) continue;                  // 正在拉：合并重复请求
                }
                Tried[key] = DateTime.MinValue; // 占位（正在拉）；成/败后由工人写实时间
                _queue.Enqueue((fam, weight));
                queued = true;
            }
        }
        if (queued) Pump();
    }

    /// <summary>用户上传字体是否已覆盖这个 family（UserFaces 用合成名，这里判定 UF_ 前缀即可）。</summary>
    private static bool UserFontFamily(string fam) => fam.StartsWith("UF_", StringComparison.Ordinal);

    private static readonly Queue<(string Family, int Weight)> _queue = new();

    /// <summary>起一个后台工人（单飞）：一次一个家族，串行下载，成功即热更新。</summary>
    private static void Pump()
    {
        lock (Gate)
        {
            if (_worker is { IsCompleted: false }) return;
            _worker = Task.Run(Worker);
        }
    }

    private static async Task Worker()
    {
        while (true)
        {
            (string Family, int Weight) job;
            lock (Gate)
            {
                if (_queue.Count == 0) return;
                job = _queue.Dequeue();
            }
            string? file = null;
            try
            {
                file = await FetchFamily(job.Family, job.Weight).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Debug($"[字体拉取] {job.Family} 失败：{ex.Message}");
            }
            lock (Gate)
            {
                Tried[job.Family + "|" + job.Weight] = DateTime.UtcNow;
                LedgerPut(job.Family + "|" + job.Weight, file != null, file ?? "");
            }
            if (file == null) continue;

            try
            {
                JFonts.ScanFontDirs(force: true); // 新文件进索引；内部自带渲染闸（重建 Dispose 旧脸必须与 Frame 串行）
                lock (EngineRenderGate.Lock) // 字形/SKFont 是共享静态缓存：清缓存必须与渲染/导出串行
                {
                    JFonts.InvalidateResolveCache();
                }
                OnFetched?.Invoke(job.Family);
            }
            catch (Exception ex)
            {
                Log.Warn("[字体拉取] 装入失败：" + ex.Message);
            }
        }
    }

    /// <summary>拉一个家族的一份字重：候选文件名 × 镜像逐个试，先落地再验（族名对得上、字形数像完整字库）。
    /// 成功返回落盘路径，全试完没拉到返回 null。</summary>
    private static async Task<string?> FetchFamily(string family, int weight)
    {
        var dir = DirOf(family);
        Directory.CreateDirectory(JFonts.UserFontDir);
        foreach (var file in FileCandidates(family, weight))
        {
            foreach (var host in Hosts)
            {
                var url = $"{host}/ofl/{dir}/{Uri.EscapeDataString(file)}";
                byte[]? bytes = null;
                try
                {
                    using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) continue;
                    bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Debug($"[字体拉取] {url} 拿不到：{ex.Message}");
                    continue;
                }
                if (bytes == null || bytes.Length < 4096) continue;

                // 落盘 → 用 Skia 验一次（族名对得上、字形数像完整字库，不是 CDN 的拉丁子集）
                var name = Regex.Replace(family, "[^A-Za-z0-9]+", "_") + "-" + weight + ".ttf";
                var path = Path.Combine(JFonts.UserFontDir, name);
                try
                {
                    var tmp = path + ".tmp";
                    await File.WriteAllBytesAsync(tmp, bytes).ConfigureAwait(false);
                    var tf = SKTypeface.FromFile(tmp);
                    var good = tf != null && Equalish(tf.FamilyName, family) && tf.GlyphCount > 200;
                    var cjk = tf != null && tf.ContainsGlyph('漢');
                    tf?.Dispose();
                    if (!good)
                    {
                        File.Delete(tmp);
                        continue;
                    }
                    File.Move(tmp, path, overwrite: true);
                    Log.Info($"[字体拉取] {family} {weight} ← {url}（{bytes.Length / 1048576.0:F1}MB，CJK={(cjk ? "有" : "无")}）");
                    return path;
                }
                catch (Exception ex)
                {
                    Log.Debug($"[字体拉取] 落地失败 {name}：{ex.Message}");
                }
            }
        }
        Log.Info($"[字体拉取] {family} {weight} 没拉到（用系统字体栈兜底）");
        return null;
    }

    // ---------------- 拉取账本（fonts\.fetchlog.json）----------------
    // 记「这个族这个字重拉过没有」：拉成功过就不再请求（族名归一化跟目录索引对不上时也不会反复重下），
    // 失败按次数递增退避并跨重启保留——否则仓库里根本没有的族，每次启动、每 10 分钟都要把镜像再敲一遍。

    private sealed record LedgerEntry(bool Ok, long Ts, int Fails, string File);

    private static Dictionary<string, LedgerEntry>? _log;

    private static string LedgerPath => Path.Combine(JFonts.UserFontDir, ".fetchlog.json");

    private static Dictionary<string, LedgerEntry> Ledger()
    {
        lock (Gate)
        {
            if (_log != null) return _log;
            var map = new Dictionary<string, LedgerEntry>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(LedgerPath))
                    foreach (var p in JObject.Parse(File.ReadAllText(LedgerPath)).Properties())
                        map[p.Name] = new LedgerEntry(p.Value.Value<bool?>("ok") ?? false,
                            p.Value.Value<long?>("ts") ?? 0, p.Value.Value<int?>("fails") ?? 0,
                            p.Value.Value<string>("file") ?? "");
            }
            catch (Exception ex)
            {
                Log.Debug("[字体拉取] 账本读取失败（忽略）：" + ex.Message);
            }
            return _log = map;
        }
    }

    /// <summary>账本判定：拉成功过的跳过（拉到的文件被删了则重新拉）；失败过的按递增退避跳过。</summary>
    private static bool SkipByLedger(string key)
    {
        var e = Ledger().GetValueOrDefault(key);
        if (e == null) return false;
        if (e.Ok) return e.File.Length == 0 || File.Exists(e.File);
        var backoff = e.Fails switch
        {
            <= 1 => TimeSpan.FromMinutes(10),
            2 => TimeSpan.FromHours(1),
            _ => TimeSpan.FromHours(24),
        };
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - e.Ts < backoff.TotalMilliseconds;
    }

    private static void LedgerPut(string key, bool ok, string file)
    {
        lock (Gate)
        {
            var map = Ledger();
            var fails = ok ? 0 : (map.GetValueOrDefault(key)?.Fails ?? 0) + 1;
            map[key] = new LedgerEntry(ok, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), fails, file);
            try
            {
                var j = new JObject();
                foreach (var (k, v) in map)
                    j[k] = new JObject { ["ok"] = v.Ok, ["ts"] = v.Ts, ["fails"] = v.Fails, ["file"] = v.File };
                Directory.CreateDirectory(JFonts.UserFontDir);
                AtomicFile.Write(LedgerPath, j.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                Log.Debug("[字体拉取] 账本落盘失败：" + ex.Message);
            }
        }
    }

    /// <summary>族名相等判定（忽略大小写/空格/连字符；仓库文件名与 family 写法常有出入）。</summary>
    private static bool Equalish(string a, string b) =>
        Regex.Replace(a ?? "", "[^A-Za-z0-9]", "").Equals(
            Regex.Replace(b ?? "", "[^A-Za-z0-9]", ""), StringComparison.OrdinalIgnoreCase);
}
