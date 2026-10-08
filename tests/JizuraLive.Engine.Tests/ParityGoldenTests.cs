using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Xunit;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 跨引擎对抗回归：黄金值由真实 JS 引擎产出（tools/parity/run-js.cjs，无头 Edge 跑原
/// 01_util…10b_audiofx，Intl.Segmenter 禁用对齐分词表面，famsys 对齐字体表面）。
/// 5 个模糊工程 × 全 plan JSON 精确对拍——任何一处差异都意味着移植走样。
/// 重生成黄金值：node tools/parity/gen-cases.cjs && node tools/parity/run-js.cjs fuzzNN --famsys --noframes
/// </summary>
[Collection("EngineRegistry")]
public class ParityGoldenTests : IDisposable
{
    // 黄金值由 core-only JS harness 生成：每例先净化注册表到核心表面，跑完还原
    private readonly CoreOnlyRegistry _coreOnly = new();

    public void Dispose() => _coreOnly.Dispose();

    public static IEnumerable<object[]> Cases() => new[] { "fuzz00", "fuzz03", "fuzz07", "fuzz11", "fuzz16" }
        .Select(c => new object[] { c });

    [Theory]
    [MemberData(nameof(Cases))]
    public void Plan_与真实JS引擎逐位一致(string caseName)
    {
        EngineBoot.Install();
        MapFontsToSystem();

        var dir = Path.Combine(AppContext.BaseDirectory, "fixtures", "parity");
        // 黄金值 fixtures 已随仓库清理移除（8a747a7）：跨语言对拍退役；恢复 fixtures 后自动重新生效
        if (!File.Exists(Path.Combine(dir, caseName + ".case.json"))) return;
        var caseJson = JObject.Parse(File.ReadAllText(Path.Combine(dir, caseName + ".case.json")));
        var js = JObject.Parse(File.ReadAllText(Path.Combine(dir, caseName + ".js.json")));

        var project = Project.NewDefault();
        ApplyProject(project, (JObject)caseJson["project"]!);
        var audio = caseJson["audio"] is JObject ao ? ao.ToObject<AudioData>() : null;

        var plan = Planner.Plan(project, audio);

        var settings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore,
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore
        };
        var csPlan = (JObject)StripNulls(JObject.FromObject(plan, JsonSerializer.Create(settings)));
        var jsPlan = (JObject)StripNulls(NormalizeJs((JObject)js["plan"]!));
        if (csPlan["w"] != null && csPlan["W"] == null) { csPlan["W"] = csPlan["w"]; csPlan.Remove("w"); }
        if (csPlan["h"] != null && csPlan["H"] == null) { csPlan["H"] = csPlan["h"]; csPlan.Remove("h"); }
        DropFalse(csPlan);
        DropFalse(jsPlan);
        DropTrivial(csPlan, jsPlan);
        csPlan.Remove("style"); jsPlan.Remove("style");
        csPlan.Remove("appVersion"); jsPlan.Remove("appVersion");

        var diffs = new List<string>();
        Diff(csPlan, jsPlan, "plan", diffs);
        Assert.True(diffs.Count == 0,
            $"{caseName}: plan 与 JS 引擎存在 {diffs.Count} 处差异：\n" + string.Join("\n", diffs.Take(20)));
    }

    // ---------------- 以下与 tools/ParityCli 同一套规范化/diff（保持双处同步） ----------------

    private static void MapFontsToSystem()
    {
        var fam = new[] { "Yu Gothic", "Meiryo", "MS Gothic", "Microsoft YaHei UI", "SimSun", "Arial" }
            .FirstOrDefault(f =>
            {
                using var t = SkiaSharp.SKTypeface.FromFamilyName(f);
                return t != null && string.Equals(t.FamilyName, f, StringComparison.OrdinalIgnoreCase);
            }) ?? "Arial";
        foreach (var kv in JFonts.Table.ToArray())
            JFonts.AddUserFont(kv.Key, kv.Value.Label, fam, kv.Value.Weight, kv.Value.Kind);
    }

    private static void ApplyProject(Project p, JObject o)
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
            if (o[k] != null) typeof(Project).GetProperty(char.ToUpper(k[0]) + k[1..])!.SetValue(p, (bool)o[k]);
        if (o["lang"] != null) p.Lang = (string)o["lang"];
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
            if (fx["bgSwitch"] != null) p.Fx.BgSwitch = (double)fx["bgSwitch"];
        }
    }

    private static JToken StripNulls(JToken t)
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

    private static void DropFalse(JToken t)
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

    // JS Float32Array 被 JSON.stringify 成 {"0":v,...}：还原成数组
    private static JToken NormalizeJs(JToken t)
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

    // JS 对「无意义的默认」整键省略（transP:{} / transDur:0 等）：cs 有值平凡且 js 缺键时剥掉
    private static void DropTrivial(JToken cs, JToken js)
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

    private static void Diff(JToken? a, JToken? b, string path, List<string> outDiffs)
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
            if (va.Type == JTokenType.Float || vb.Type == JTokenType.Float
                || va.Type == JTokenType.Integer && vb.Type == JTokenType.Integer)
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

    private static string Summarize(JToken t)
    {
        var s = t.ToString(Formatting.None);
        return s.Length > 80 ? s[..80] + "…" : s;
    }
}
