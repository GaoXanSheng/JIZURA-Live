// 对抗验证 C# 侧：同 case 输入 → Planner.Plan + Renderer 出帧 → 与 JS 黄金值 diff。
// 用法：dotnet run --project tools/ParityCli -- <case名> [--noframes] [--png]
//   默认：plan JSON 精确 diff（null 剥离后深度比较）+ 帧灰度结构比对（下采样 96×54）
//   --png 额外把 C# 帧落盘 obj/parity/<case>.cs.t<t>.png
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System.IO;
using SkiaSharp;

var caseName = args.Length > 0 ? args[0] : "smoke";
var noFrames = args.Contains("--noframes");
var savePng = args.Contains("--png");
var jsJsMode = args.Contains("--jsjs"); // JS 自身两次 run 对照：测指标地板（grain 随机性）

var root = FindRoot();
var casePath = Path.Combine(root, "tools", "parity", "cases", caseName + ".json");
var jsPath = Path.Combine(root, "obj", "parity", caseName + (args.Contains("--sys") ? ".sys" : "") + (args.Contains("--w400") ? ".w4" : "") + ".js.json");
var outDir = Path.Combine(root, "obj", "parity");
var caseJson = JObject.Parse(File.ReadAllText(casePath));
var js = JObject.Parse(File.ReadAllText(jsPath));

if (jsJsMode)
{
    var js2 = JObject.Parse(File.ReadAllText(Path.Combine(root, "obj", "parity", caseName + ".sys2.js.json")));
    var fa = (JArray)js["frames"]!;
    var fb = (JArray)js2["frames"]!;
    for (var i = 0; i < Math.Min(fa.Count, fb.Count); i++)
    {
        using var ba = new SKBitmap(1, 1);
        var (s, h) = PngVsPng(DecodeDataUrl((string)fa[i]!["png"]!), DecodeDataUrl((string)fb[i]!["png"]!));
        Console.WriteLine($"[js-vs-js] t={(double)fa[i]!["t"]!:F2}: 灰度平均差 {s:F2}/255，热点 {h:P1}");
    }
    return 0;
}

static (double, double) PngVsPng(byte[] pa, byte[] pb)
{
    using var ia = SKImage.FromEncodedData(pa);
    using var bma = SKBitmap.FromImage(ia)!;
    using var ib = SKImage.FromEncodedData(pb);
    using var bmb = SKBitmap.FromImage(ib)!;
    return Host.FrameDistance(bma, bmb);
}

JizuraLive.Engine.EngineBoot.Install();
// 与 PreviewEngine 同款系统字体兜底：把全部字体键映射到同一系统字体，
// 与 JS harness 的 ?famsys=1 形成同一字体表面（字体差异剥离，只对拍引擎）
{
    var fam = new[] { "Yu Gothic", "Meiryo", "MS Gothic", "Microsoft YaHei UI", "SimSun", "Arial" }
        .FirstOrDefault(f =>
        {
            using var t = SkiaSharp.SKTypeface.FromFamilyName(f);
            return t != null && string.Equals(t.FamilyName, f, StringComparison.OrdinalIgnoreCase);
        }) ?? "Arial";
    foreach (var kv in JizuraLive.Engine.JFonts.Table.ToArray())
        JizuraLive.Engine.JFonts.AddUserFont(kv.Key, kv.Value.Label, fam, args.Contains("--w400") ? 400 : kv.Value.Weight, kv.Value.Kind);
}

// ---- 同一输入 ----
var project = JizuraLive.Engine.Project.NewDefault();
ApplyProject(project, (JObject)caseJson["project"]!);
JizuraLive.Engine.AudioData? audio = null;
if (caseJson["audio"] is JObject ao)
    audio = ao.ToObject<JizuraLive.Engine.AudioData>();

var sw = System.Diagnostics.Stopwatch.StartNew();
var plan = JizuraLive.Engine.Planner.Plan(project, audio);
Console.WriteLine($"[cs] plan ok: cuts={plan.Cuts.Count} lines={plan.Lines.Count} events={plan.Events.Count} dur={plan.Duration:F3} ({sw.ElapsedMilliseconds}ms)");

// ---- C# plan → camelCase JToken（与 JS JSON 形态对齐），双侧剥 null ----
var settings = new JsonSerializerSettings
{
    ContractResolver = new CamelCasePropertyNamesContractResolver(),
    NullValueHandling = NullValueHandling.Ignore,
    ReferenceLoopHandling = ReferenceLoopHandling.Ignore
};
var csPlan = (JObject)StripNulls(JObject.FromObject(plan, JsonSerializer.Create(settings)));
var jsPlan = (JObject)StripNulls(Host.NormalizeJs((JObject)js["plan"]!));
// JS plan 根对象的 W/H 是大写（对象字面量简写），其余键全小写；camelCase resolver 会把 C# 的 W/H 变 w/h
if (csPlan["w"] != null && csPlan["W"] == null) { csPlan["W"] = csPlan["w"]; csPlan.Remove("w"); }
if (csPlan["h"] != null && csPlan["H"] == null) { csPlan["H"] = csPlan["h"]; csPlan.Remove("h"); }
// JS 对 false 值的这类可选键整键省略（条件对象字面量），双侧剥 false 形态对齐
DropFalse(csPlan); DropFalse(jsPlan); Host.DropTrivial(csPlan, jsPlan);
csPlan.Remove("style"); jsPlan.Remove("style");       // 风格对象：JS 侧含函数/字体数据，键级 diff 留待专项
csPlan.Remove("appVersion"); jsPlan.Remove("appVersion");

static void DropFalse(JToken t)
{
    if (t is JObject o)
    {
        foreach (var k in o.Properties().Select(p => p.Name).ToList())
            if (o[k] is JValue v && v.Value is bool bv && !bv && k is "interlude" or "kime" or "weightGrow" or "recap" or "emph" or "companion")
                o.Remove(k);
            else DropFalse(o[k]!);
    }
    else if (t is JArray a)
        foreach (var v in a) DropFalse(v);
}

var diffs = new List<string>();
Diff(csPlan, jsPlan, "plan", diffs);
Console.WriteLine($"[diff] plan: {diffs.Count} 处差异");
if (js["lay"] is JArray layJs)
{
    var it = new JObject { ["text"] = "夜明けの色を覚えてる", ["font"] = "gothic_bold", ["size"] = 120, ["track"] = 0.05 };
    var layCs = JizuraLive.Engine.JText.LayoutText(it);
    Console.WriteLine($"[lay] w: js={js["layW"]} cs={layCs.W:F2}  h: js={js["layH"]} cs={layCs.H:F2}  cells: js={layJs.Count} cs={layCs.Cells.Count}");
    for (var i = 0; i < Math.Min(layJs.Count, layCs.Cells.Count); i++)
    {
        var jc = (JArray)layJs[i]!;
        var cc = layCs.Cells[i];
        var dx = (double)jc[0]! - cc.X;
        if (Math.Abs(dx) > 0.05 || Math.Abs((double)jc[1]! - cc.Y) > 0.05 || Math.Abs((double)jc[2]! - cc.W) > 0.05)
            Console.WriteLine($"  [lay {i}] x js={jc[0]} cs={cc.X:F2} (Δ{dx:F2})  y js={jc[1]} cs={cc.Y:F2}  w js={jc[2]} cs={cc.W:F2}  h js={jc[3]} cs={cc.H:F2}");
    }
    Console.WriteLine("[lay] 首单元: " + string.Join(" ", layCs.Cells.Take(3).Select(c => $"({c.X:F2},{c.Y:F2},{c.W:F2},{c.H:F2})")));
}
if (js["probe"] is JObject pr)
{
    foreach (var kv in pr)
    {
        double? jsw = kv.Value!.Type == JTokenType.Float || kv.Value.Type == JTokenType.Integer ? (double)kv.Value : null;
        double? csw = null;
        try { csw = JizuraLive.Engine.JText.Measure(new JObject { ["text"] = "夜あA", ["font"] = kv.Key, ["size"] = 100 }).W; }
        catch (Exception ex) { Console.WriteLine($"[probe] {kv.Key}: cs ERR {ex.Message}"); }
        Console.WriteLine($"[probe] {kv.Key}: js={jsw?.ToString("F2") ?? (string)kv.Value!} cs={csw?.ToString("F2")}");
    }
}
foreach (var d in diffs.Take(40)) Console.WriteLine("  " + d);
if (diffs.Count > 40) Console.WriteLine($"  …（还有 {diffs.Count - 40} 处）");

// ---- 帧渲染与结构比对 ----
if (!noFrames)
{
    var renderer = new JizuraLive.Engine.Renderer();
    var jsFrames = (JArray)js["frames"]!;
    foreach (var f in jsFrames)
    {
        var t = (double)f["t"]!;
        var jsPng = DecodeDataUrl((string)f["png"]!);
        File.WriteAllBytes(Path.Combine(outDir, $"{caseName}.js.t{t}.png"), jsPng);
        var bmp = new SKBitmap(plan.W, plan.H, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(bmp.Info, bmp.GetPixels(), bmp.RowBytes);
        Host.Remember(surface, plan.W, plan.H);
        renderer.Frame(surface.Canvas, plan, t, new JizuraLive.Engine.RenderOpts { Scale = 1 });

        if (savePng)
            using (var img = SKImage.FromPixels(bmp.Info, bmp.GetPixels(), bmp.RowBytes))
            using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
                using (var fs2 = File.Create(Path.Combine(outDir, $"{caseName}.cs.t{t}.png")))
                    data.SaveTo(fs2);

        var (score, hot) = FrameDistancePng(jsPng, bmp);
        Console.WriteLine($"[frame] t={t:F2}: 灰度平均差 {score:F2}/255，热点占比 {hot:P1}（{DiffDescribe(score)}）");
        if (savePng)
            Host.WriteDiffMapPub(jsPng, bmp, Path.Combine(outDir, $"{caseName}.diff.t{t}.png"));
    }
}

Console.WriteLine(diffs.Count == 0 ? "VERDICT: PLAN EXACT MATCH" : $"VERDICT: {diffs.Count} PLAN DIFFS");
return diffs.Count == 0 ? 0 : 2;

// ================= helpers =================

static string FindRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d != null && !File.Exists(Path.Combine(d.FullName, "JizuraLive.csproj"))) d = d.Parent!;
    return d!.FullName;
}


static void ApplyProject(JizuraLive.Engine.Project p, JObject o)
{
    if (o["lyrics"] != null) p.Lyrics = (string)o["lyrics"];
    if (o["title"] != null) p.Title = (string)o["title"];
    if (o["artist"] != null) p.Artist = (string)o["artist"];
    if (o["seed"] != null) p.Seed = (uint)o["seed"];
    if (o["style"] != null) p.Style = (string)o["style"];
    if (o["aspect"] != null) p.Aspect = (string)o["aspect"];
    if (o["res"] != null) p.Res = (int)o["res"];
    if (o["fps"] != null) p.Fps = (int)o["fps"];
    foreach (var k in new[] { "unify", "typeset", "extra", "wa", "horror", "typo", "kinetic", "centerFree" })
        if (o[k] != null) typeof(JizuraLive.Engine.Project).GetProperty(char.ToUpper(k[0]) + k[1..])!
            .SetValue(p, (bool)o[k]);
    if (o["lang"] != null) p.Lang = (string)o["lang"];
    if (o["keyBg"] != null) p.KeyBg = (string)o["keyBg"];
    if (o["centerDir"] != null) p.CenterDir = (string)o["centerDir"];
    if (o["centerZone"] != null) p.CenterZone = (string)o["centerZone"];
    if (o["centerGap"] != null) p.CenterGap = (double)o["centerGap"];
    if (o["mood"] != null) p.Mood = (string?)o["mood"];
    if (o["timing"] is JObject tm)
    {
        if (tm["bpm"] != null) p.Timing.Bpm = (double)tm["bpm"];
        if (tm["offset"] != null) p.Timing.Offset = (double)tm["offset"];
        if (tm["snap"] != null) p.Timing.Snap = (bool)tm["snap"];
        if (tm["tail"] != null) p.Timing.Tail = (double)tm["tail"];
        if (tm["lineScale"] != null) p.Timing.LineScale = (double)tm["lineScale"];
        if (tm["lineTimes"] is JObject lt)
            foreach (var kv in lt) p.Timing.LineTimes[kv.Key] = (double)kv.Value!;
    }
    if (o["fx"] is JObject fx)
    {
        if (fx["motion"] != null) p.Fx.Motion = (double)fx["motion"];
        if (fx["glitch"] != null) p.Fx.Glitch = (double)fx["glitch"];
        if (fx["chroma"] != null) p.Fx.Chroma = (double)fx["chroma"];
        if (fx["decor"] != null) p.Fx.Decor = (double)fx["decor"];
        if (fx["density"] != null) p.Fx.Density = (double)fx["density"];
        if (fx["texture"] != null) p.Fx.Texture = (double)fx["texture"];
        if (fx["flash"] != null) p.Fx.Flash = (bool)fx["flash"];
        if (fx["onTwos"] != null) p.Fx.OnTwos = (bool)fx["onTwos"];
        if (fx["koma"] != null) p.Fx.Koma = (int)fx["koma"];
        if (fx["hud"] != null) p.Fx.Hud = (string)fx["hud"];
        if (fx["bgSwitch"] != null) p.Fx.BgSwitch = (double)fx["bgSwitch"];
    }
}

// 深度剥 null：JS 侧 undefined 字段缺失 / 显式 null，C# 侧 NullValueHandling.Ignore 后仍可能留 null
static JToken StripNulls(JToken t)
{
    if (t is JObject o)
    {
        var r = new JObject();
        foreach (var kv in o)
            if (kv.Value != null && kv.Value.Type != JTokenType.Null)
                r[kv.Key] = StripNulls(kv.Value);
        return r;
    }
    if (t is JArray a)
    {
        var r = new JArray();
        foreach (var v in a) r.Add(StripNulls(v));
        return r;
    }
    return t;
}

static void Diff(JToken? a, JToken? b, string path, List<string> outDiffs)
{
    if (a == null && b == null) return;
    if (a == null || b == null)
    {
        outDiffs.Add($"{path}: 单侧缺失（cs={(a == null ? "无" : Summarize(a))} js={(b == null ? "无" : Summarize(b))}）");
        return;
    }
    if (a is JObject oa && b is JObject ob)
    {
        foreach (var k in oa.Properties().Select(p => p.Name).Union(ob.Properties().Select(p => p.Name)))
            Diff(oa[k], ob[k], path + "." + k, outDiffs);
        return;
    }
    if (a is JArray aa && b is JArray ab)
    {
        if (aa.Count != ab.Count)
        {
            outDiffs.Add($"{path}: 数组长度 cs={aa.Count} js={ab.Count}");
            return;
        }
        for (var i = 0; i < aa.Count; i++) Diff(aa[i], ab[i], $"{path}[{i}]", outDiffs);
        return;
    }
    if (a is JValue va && b is JValue vb)
    {
        if (va.Type == JTokenType.Float || vb.Type == JTokenType.Float ||
            va.Type == JTokenType.Integer && vb.Type == JTokenType.Integer)
        {
            var da = Convert.ToDouble(va.Value, System.Globalization.CultureInfo.InvariantCulture);
            var db = Convert.ToDouble(vb.Value, System.Globalization.CultureInfo.InvariantCulture);
            if (double.IsNaN(da) && double.IsNaN(db)) return;
            if (da != db && Math.Abs(da - db) > 1e-6)
                outDiffs.Add($"{path}: cs={da:R} js={db:R}（Δ={da - db:G3}）");
            return;
        }
        if (!JToken.DeepEquals(va, vb))
            outDiffs.Add($"{path}: cs={va.Value} js={vb.Value}");
        return;
    }
    if (!JToken.DeepEquals(a, b))
        outDiffs.Add($"{path}: 类型/值不同 cs={Summarize(a)} js={Summarize(b)}");
}

static string Summarize(JToken t)
{
    var s = t.ToString(Formatting.None);
    return s.Length > 80 ? s[..80] + "…" : s;
}

static byte[] DecodeDataUrl(string url)
{
    var b64 = url[(url.IndexOf(',') + 1)..];
    return Convert.FromBase64String(b64);
}

// 帧结构比对：两级降采样（1920→384→96，把亚像素 AA 差洗掉），灰度平均差 + 超阈值热点占比
static (double Score, double Hot) FrameDistancePng(byte[] jsPng, SKBitmap csBmp)
{
    using var jsImg = SKImage.FromEncodedData(jsPng);
    using var jsBmp = SKBitmap.FromImage(jsImg)!;
    return Host.FrameDistance(jsBmp, csBmp);
}



static string DiffDescribe(double score) => score switch
{
    < 3 => "结构一致",
    < 8 => "轻微偏差（字体/纹理类可接受）",
    < 18 => "明显偏差，需排查",
    _ => "结构不同，必须排查"
};

// 引擎宿主须知：交给 Frame 的面必须按尺寸保留（整帧缓存持有快照），进程内常驻
static class Host
{
    static readonly Dictionary<(int, int), SKSurface> KeepAlive = new();
    
// 差异热力图：1/5 分辨率灰度差 → 红=差异大，落盘供目检定位
static void WriteDiffMap(byte[] jsPng, SKBitmap csBmp, string path)
{
    using var jsImg = SKImage.FromEncodedData(jsPng);
    using var jsBmp = SKBitmap.FromImage(jsImg)!;
    const int W = 96, H = 54;
    var ga = DownGray(jsBmp, W, H);
    var gb = DownGray(csBmp, W, H);
    using var bmp = new SKBitmap(W, H);
    for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var d = Math.Min(255, (int)(Math.Abs(ga[y * W + x] - gb[y * W + x]) * 3));
            bmp.SetPixel(x, y, new SKColor((byte)d, (byte)(d / 4), (byte)(d / 4)));
        }
    using var img = SKImage.FromBitmap(bmp);
    using var up = bmp.Resize(new SKImageInfo(W * 6, H * 6, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.None);
    using var upImg = SKImage.FromBitmap(up);
    using var data = upImg.Encode(SKEncodedImageFormat.Png, 90);
    using var fs = File.Create(path);
    data.SaveTo(fs);
}

public static (double Score, double Hot) FrameDistance(SKBitmap jsBmp, SKBitmap csBmp)
{
    const int W = 96, H = 54;
    var ga = DownGray(jsBmp, W, H);
    var gb = DownGray(csBmp, W, H);
    double sum = 0;
    var hot = 0;
    for (var i = 0; i < W * H; i++)
    {
        var d = Math.Abs(ga[i] - gb[i]);
        sum += d;
        if (d > 48) hot++;
    }
    return (sum / (W * H), hot / (double)(W * H));
}
public static double[] DownGray(SKBitmap bmp, int w, int h)
{
    // 两级 Medium 缩放：实证基线（单步 High 有振铃，指标更差）。
    // 残差 ~10/255 是两个 Skia 文本栈的 AA 特性差（结构正确性由 plan/layout 探针保证）
    using var mid = bmp.Resize(new SKImageInfo(Math.Max(1, bmp.Width / 5), Math.Max(1, bmp.Height / 5), SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.Medium);
    using var tmp = mid!.Resize(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.Medium);
    var dst = new double[w * h];
    for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var c = tmp.GetPixel(x, y);
            dst[y * w + x] = 0.2126 * c.Red + 0.7152 * c.Green + 0.0722 * c.Blue;
        }
    return dst;
}
    public static void WriteDiffMapPub(byte[] a, SKBitmap b, string c) => WriteDiffMap(a, b, c);

    // JS Float32Array 被 JSON.stringify 成 {"0":v,...}：还原成数组
    public static JToken NormalizeJs(JToken t)
    {
        if (t is JObject o)
        {
            var keys = o.Properties().Select(p => p.Name).ToList();
            if (keys.Count > 0 && keys.All(k => uint.TryParse(k, out _)))
                return new JArray(keys.OrderBy(uint.Parse).Select(k => NormalizeJs(o[k]!)));
            var r = new JObject();
            foreach (var kv in o) r[kv.Key] = NormalizeJs(kv.Value!);
            return r;
        }
        if (t is JArray a)
        {
            var r = new JArray();
            foreach (var v in a) r.Add(NormalizeJs(v));
            return r;
        }
        return t;
    }

    // JS 对「无意义的默认」整键省略（transP:{} / transDur:0 等）：cs 侧有值平凡且 js 缺键时剥掉
    public static void DropTrivial(JToken cs, JToken js)
    {
        if (cs is JObject co && js is JObject jo)
        {
            foreach (var k in co.Properties().Select(p => p.Name).ToList())
            {
                if (!jo.ContainsKey(k))
                {
                    var v = co[k]!;
                    var trivial = v.Type == JTokenType.Object && !v.HasValues
                                  || v.Type == JTokenType.Integer && (int)v == 0
                                  || v.Type == JTokenType.Float && Math.Abs((double)v) < 1e-12
                                  || v.Type == JTokenType.Boolean && !(bool)v;
                    if (trivial) { co.Remove(k); continue; }
                }
                DropTrivial(co[k]!, jo[k] ?? JObject.Parse("{}"));
            }
        }
        else if (cs is JArray ca && js is JArray ja)
            for (var i = 0; i < ca.Count && i < ja.Count; i++) DropTrivial(ca[i], ja[i]);
    }
    public static void Remember(SKSurface s, int w, int h) => KeepAlive[(w, h)] = s;
}

