using JizuraLive.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JizuraLive.Engine;

// 手法注册表（移植自 05b_registry.js）。10 组：layout/enter/hold/exit/decor/treat/bg/cam/fx/trans。
// JS 的注册条目是对象字面量；C# 对位为带委托属性的 Def 类，表现包用对象初始化器注册：
// J.register('layout','key',{...}) → Reg.Register("layout","key", new LayoutDef{...}, "packName")。
// 条目通用字段：name（日文标签必填）/ tags（氛围 key）/ w（抽取权重）/ pack / special。

public interface IRegEntry
{
    /// <summary>注册键（Register 时写入）。</summary>
    string Key { get; set; }

    /// <summary>所属表现包（Register 时写入，缺省 core）。</summary>
    string? Pack { get; set; }

    /// <summary>special 条目不进 order（缩略图墙/随机不可见）。</summary>
    bool Special { get; set; }

    string Name { get; set; }
    string[] Tags { get; set; }
    double? W { get; set; }
}

// ---------------- 配色（04_styles 的 scheme；字段松散，键访问保持 JSON 语义） ----------------

public sealed class Scheme
{
    [JsonExtensionData]
    public Dictionary<string, JToken>? Extra { get; set; } = new();

    public JToken? Get(string k) => Extra != null && Extra.TryGetValue(k, out var v) ? v : null;

    public string? Str(string k)
    {
        var v = Get(k);
        return v != null && v.Type == JTokenType.String ? (string?)v : null;
    }

    public double Num(string k, double d = 0)
    {
        var v = Get(k);
        return v != null && (v.Type == JTokenType.Float || v.Type == JTokenType.Integer) ? (double)v : d;
    }
}

// ---------------- 风格（04_styles / 11p_styles 注册到 Reg.Styles） ----------------

public sealed class StyleDef
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string[] Tags { get; set; } = System.Array.Empty<string>();

    /// <summary>HUD 默认开关（fx.hud === 'auto' 时用它）。</summary>
    public bool Hud { get; set; }

    /// <summary>glitch 事件强度乘数。</summary>
    public double GlitchBoost { get; set; } = 1;

    /// <summary>抽取权重偏置：{ layout:{key:w}, enter:{...}, exit, hold, cam, treat, bg, trans, fx }。</summary>
    public JObject Bias { get; set; } = new();

    /// <summary>装饰偏置：{ key: w }（pickDecor 用 st.decor，不带组前缀）。</summary>
    public JObject Decor { get; set; } = new();

    public List<Scheme> Schemes { get; set; } = new();

    /// <summary>其余风格字段（纸纹参数等）原样保留。</summary>
    [JsonExtensionData]
    public Dictionary<string, JToken>? Extra { get; set; } = new();
}

// ---------------- 注册条目 Def 类 ----------------

/// <summary>layout: 每刀的版式。Fits/Plan 是 planner 依赖；Draw 是渲染入口。</summary>
public sealed class LayoutDef : IRegEntry
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string[] Tags { get; set; } = System.Array.Empty<string>();
    public double? W { get; set; }
    public bool Special { get; set; }
    public string? Pack { get; set; }

    /// <summary>词数上限判定。</summary>
    public Func<int, bool>? Fits { get; set; }

    /// <summary>竖帧权重乘数；null 走 planner 的 PORTRAIT_W 表。</summary>
    public double? Portrait { get; set; }

    /// <summary>强调行权重乘数。</summary>
    public double? Emph { get; set; }

    /// <summary>true：版式自带画面，背景强制 none（busy）。</summary>
    public bool Busy { get; set; }

    /// <summary>文字加工：false 禁用；"safe" 只允许 safe 加工；其余允许全部。</summary>
    public object? Treat { get; set; }

    /// <summary>enter 抽取偏置（覆盖内置 LAYOUT_ENTER 表）。</summary>
    public JObject? EnterBias { get; set; }

    /// <summary>false：非 push 相机压到 0.05 权重。</summary>
    public bool? Cam { get; set; }

    /// <summary>版式参数规划（planner 在切分时调用）：(rng, ctx, st) → params。</summary>
    public Func<Rng, LayoutCtx, StyleDef, JObject>? Plan { get; set; }

    public Action<Env>? Draw { get; set; }
}

/// <summary>enter / exit / hold 共用：文字进出场/持续动画。</summary>
public sealed class MoveDef : IRegEntry
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string[] Tags { get; set; } = System.Array.Empty<string>();
    public double? W { get; set; }
    public bool Special { get; set; }
    public string? Pack { get; set; }

    /// <summary>时长短于该值时权重 ×0.15。</summary>
    public double? MinDur { get; set; }

    /// <summary>词数超过该值时权重 ×0.2。</summary>
    public int? MaxChars { get; set; }

    /// <summary>enter 的进场时长函数 (dur, n) → 秒。</summary>
    public Func<double, int, double>? InDur { get; set; }

    /// <summary>exit 的出场时长函数 (dur, n) → 秒。</summary>
    public Func<double, int, double>? OutDur { get; set; }

    public Action<Env>? Draw { get; set; }
}

/// <summary>decor: 装饰图形。</summary>
public sealed class DecorDef : IRegEntry
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string[] Tags { get; set; } = System.Array.Empty<string>();
    public double? W { get; set; }
    public bool Special { get; set; }
    public string? Pack { get; set; }

    /// <summary>"back"：busy 版式下被排除（除非 Subtle）。</summary>
    public string? Layer { get; set; }
    public bool Subtle { get; set; }

    public Action<Env, DecorSpec>? Draw { get; set; }
}

/// <summary>treat: 文字加工（太さ等）。</summary>
public sealed class TreatDef : IRegEntry
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string[] Tags { get; set; } = System.Array.Empty<string>();
    public double? W { get; set; }
    public bool Special { get; set; }
    public string? Pack { get; set; }

    /// <summary>只允许在 treat === "safe" 的版式上使用。</summary>
    public bool Safe { get; set; }

    public Func<Rng, StyleDef, JObject>? Plan { get; set; }
    public Action<Env>? Draw { get; set; }
}

/// <summary>bg: 整屏背景（按行/间奏取）。</summary>
public sealed class BgDef : IRegEntry
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string[] Tags { get; set; } = System.Array.Empty<string>();
    public double? W { get; set; }
    public bool Special { get; set; }
    public string? Pack { get; set; }
    public bool Subtle { get; set; }

    public Func<Rng, StyleDef, JObject>? Plan { get; set; }
    public Action<Env>? Draw { get; set; }
}

/// <summary>cam: 切分上的镜头运动。</summary>
public sealed class CamState
{
    public double S { get; set; } = 1;
    public double X { get; set; }
    public double Y { get; set; }
    public double Rot { get; set; }
    public double Skx { get; set; }
    public double Blur { get; set; }
}

public sealed class CamDef : IRegEntry
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string[] Tags { get; set; } = System.Array.Empty<string>();
    public double? W { get; set; }
    public bool Special { get; set; }
    public string? Pack { get; set; }

    /// <summary>强动作：权重 = 0.25 + 0.9*motion (+ 强调 0.6)。</summary>
    public bool Strong { get; set; }

    public Func<Rng, StyleDef, JObject>? Plan { get; set; }
    public Func<Env, CamState>? Get { get; set; }
}

/// <summary>fx: 事件后期/屏幕特效。builtin 走渲染器内置分支，其余带 Draw。</summary>
public sealed class FxDef : IRegEntry
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string[] Tags { get; set; } = System.Array.Empty<string>();
    public double? W { get; set; }
    public bool Special { get; set; }
    public string? Pack { get; set; }
    public bool Builtin { get; set; }
    public double Amp { get; set; } = 1;
    public double Dur { get; set; } = 3;
    public bool Glitchy { get; set; }

    /// <summary>false：不当切分边界转场用。</summary>
    public bool Edge { get; set; } = true;

    /// <summary>可否作切分中段点缀。</summary>
    public bool Mid { get; set; }

    /// <summary>提前量（帧数）：打点时刻前移。</summary>
    public int? Pre { get; set; }

    public Action<Env, PlanEvent>? Draw { get; set; }
}

/// <summary>trans: 切到切转场。Draw 在设备像素下把上一刀画面 A 和本刀 B 合成。</summary>
public sealed class TransDef : IRegEntry
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string[] Tags { get; set; } = System.Array.Empty<string>();
    public double? W { get; set; }
    public bool Special { get; set; }
    public string? Pack { get; set; }

    /// <summary>默认时长（秒），被 clamp 到 [0.12, min(0.6, dur*0.45)]。</summary>
    public double? Dur { get; set; }

    public Func<Rng, StyleDef, JObject>? Plan { get; set; }

    /// <summary>M2 渲染器接入：JS 的 draw(ctx, A, B, p, info) 的 C# 形态由渲染器移植时定型。</summary>
    public Delegate? Draw { get; set; }
}

// ---------------- 注册表本体 ----------------

public static class Reg
{
    public static readonly Dictionary<string, LayoutDef> Layouts = new();
    public static readonly List<string> LayoutOrder = new();
    public static readonly Dictionary<string, MoveDef> Enter = new();
    public static readonly List<string> EnterOrder = new();
    public static readonly Dictionary<string, MoveDef> Hold = new();
    public static readonly List<string> HoldOrder = new();
    public static readonly Dictionary<string, MoveDef> Exit = new();
    public static readonly List<string> ExitOrder = new();
    public static readonly Dictionary<string, DecorDef> Decor = new();
    public static readonly List<string> DecorOrder = new();
    public static readonly Dictionary<string, TreatDef> Treat = new();
    public static readonly List<string> TreatOrder = new();
    public static readonly Dictionary<string, BgDef> Bg = new();
    public static readonly List<string> BgOrder = new();
    public static readonly Dictionary<string, CamDef> Camera = new();
    public static readonly List<string> CameraOrder = new();
    public static readonly Dictionary<string, FxDef> Fxe = new();
    public static readonly List<string> FxeOrder = new();
    public static readonly Dictionary<string, TransDef> Trans = new();
    public static readonly List<string> TransOrder = new();
    public static readonly Dictionary<string, StyleDef> Styles = new();

    public static readonly string[] GroupKeys =
        { "layout", "enter", "hold", "exit", "decor", "treat", "bg", "cam", "fx", "trans" };

    /// <summary>11q_sets 的 J.randomOk（随机锁）经此委托注入；null 视为全放行（对齐 JS 的 !J.randomOk 短路）。</summary>
    public static Func<Project, string, string, bool>? RandomOk { get; set; }

    /// <summary>
    /// 05b_registry.js 载入时预置的核心条目（各表现包再往里注册）。
    /// 顺序与 JS 的 *_ORDER 一致——它是抽取候选顺序，不能变。
    /// </summary>
    public static void SeedCore()
    {
        if (TreatOrder.Count > 0) return; // 幂等
        Treat["none"] = new TreatDef { Key = "none", Name = "なし" };
        TreatOrder.Add("none");
        Bg["none"] = new BgDef { Key = "none", Name = "無地" };
        BgOrder.Add("none");
        Camera["push"] = new CamDef
        {
            Key = "push",
            Name = "ゆっくり寄る",
            Tags = new[] { "calm", "editorial", "emotional", "graphic", "pop", "glitch" },
            W = 5,
            // get: (env) => ({s: 1 + 0.03 * (env.fx.motion ?? 0.7) * clamp(lt / max(0.3, cut.dur))})
            Get = env => new CamState
            {
                S = 1 + 0.03 * env.Fx.Motion * J.Clamp(env.Lt / Math.Max(0.3, env.Cut?.Dur ?? 0))
            }
        };
        CameraOrder.Add("push");
        // 事件后期内置分支（渲染器实现）；顺序 = J.FXE_ORDER
        Fxe["chroma"] = new FxDef { Key = "chroma", Name = "色ズレの跳ね", Builtin = true, Mid = true, Glitchy = true };
        Fxe["shake"] = new FxDef { Key = "shake", Name = "揺れ", Builtin = true, Mid = true };
        Fxe["slice"] = new FxDef { Key = "slice", Name = "スライスグリッチ", Builtin = true, Mid = true, Glitchy = true };
        Fxe["block"] = new FxDef { Key = "block", Name = "ブロックグリッチ", Builtin = true, Mid = true, Glitchy = true };
        Fxe["invert"] = new FxDef { Key = "invert", Name = "反転", Builtin = true };
        Fxe["flash"] = new FxDef { Key = "flash", Name = "フラッシュ", Builtin = true, Edge = false };
        Fxe["zoom"] = new FxDef { Key = "zoom", Name = "ズームブラー", Builtin = true, Mid = true };
        Fxe["mosaic"] = new FxDef { Key = "mosaic", Name = "モザイク", Builtin = true };
        foreach (var k in new[] { "chroma", "shake", "slice", "block", "invert", "flash", "zoom", "mosaic" })
            FxeOrder.Add(k);
    }


    public static void Register(string group, string key, LayoutDef def, string? pack = null) => RegisterInto(Layouts, LayoutOrder, group, key, def, pack);
    public static void Register(string group, string key, MoveDef def, string? pack = null)
    {
        switch (group)
        {
            case "enter": RegisterInto(Enter, EnterOrder, group, key, def, pack); break;
            case "hold": RegisterInto(Hold, HoldOrder, group, key, def, pack); break;
            case "exit": RegisterInto(Exit, ExitOrder, group, key, def, pack); break;
            default: throw new ArgumentException("unknown group " + group);
        }
    }
    public static void Register(string group, string key, DecorDef def, string? pack = null) => RegisterInto(Decor, DecorOrder, group, key, def, pack);
    public static void Register(string group, string key, TreatDef def, string? pack = null) => RegisterInto(Treat, TreatOrder, group, key, def, pack);
    public static void Register(string group, string key, BgDef def, string? pack = null) => RegisterInto(Bg, BgOrder, group, key, def, pack);
    public static void Register(string group, string key, CamDef def, string? pack = null) => RegisterInto(Camera, CameraOrder, group, key, def, pack);
    public static void Register(string group, string key, FxDef def, string? pack = null) => RegisterInto(Fxe, FxeOrder, group, key, def, pack);
    public static void Register(string group, string key, TransDef def, string? pack = null) => RegisterInto(Trans, TransOrder, group, key, def, pack);

    private static void RegisterInto<T>(Dictionary<string, T> reg, List<string> order, string group, string key, T def, string? pack)
        where T : class, IRegEntry
    {
        if (def.Name == null || def.Name.Length == 0)
            throw new InvalidOperationException($"{group}.{key}: name is required");
        def.Key = key;
        var prevPack = reg.TryGetValue(key, out var old) ? old.Pack : null;
        def.Pack = def.Pack ?? pack ?? "core";
        if (prevPack != null && prevPack != def.Pack)
            Log.Warn($"JIZURA: {group}.{key} is being replaced");
        reg[key] = def;
        if (!def.Special && !order.Contains(key)) order.Add(key);
    }

    public static IReadOnlyList<string> OrderOf(string g) => g switch
    {
        "layout" => LayoutOrder,
        "enter" => EnterOrder,
        "hold" => HoldOrder,
        "exit" => ExitOrder,
        "decor" => DecorOrder,
        "treat" => TreatOrder,
        "bg" => BgOrder,
        "cam" => CameraOrder,
        "fx" => FxeOrder,
        "trans" => TransOrder,
        _ => throw new ArgumentException("unknown group " + g)
    };

    /// <summary>tags 含该氛围 key 的条目（おまかせ用）。J.taggedWith。</summary>
    public static List<string> TaggedWith(string group, string mood) => OrderOf(group)
        .Where(k => RegistryDef(group, k)?.Tags.Contains(mood) == true).ToList();

    public static bool RandomAllows(Project project, string g, string k) => RandomOk == null || RandomOk(project, g, k);

    /// <summary>J.defaultProject 的 enabled 映射：没显式关掉的都算开。</summary>
    public static Dictionary<string, Dictionary<string, bool>> DefaultEnabled()
    {
        var en = new Dictionary<string, Dictionary<string, bool>>();
        foreach (var g in GroupKeys)
        {
            var m = new Dictionary<string, bool>();
            foreach (var k in OrderOf(g)) m[k] = true;
            en[g] = m;
        }
        return en;
    }

    private static IRegEntry? RegistryDef(string group, string key) => group switch
    {
        "layout" => Layouts.GetValueOrDefault(key),
        "enter" => Enter.GetValueOrDefault(key),
        "hold" => Hold.GetValueOrDefault(key),
        "exit" => Exit.GetValueOrDefault(key),
        "decor" => Decor.GetValueOrDefault(key),
        "treat" => Treat.GetValueOrDefault(key),
        "bg" => Bg.GetValueOrDefault(key),
        "cam" => Camera.GetValueOrDefault(key),
        "fx" => Fxe.GetValueOrDefault(key),
        "trans" => Trans.GetValueOrDefault(key),
        _ => null
    };
}

// ---------------- 版式参数上下文（JS plan(rng, {text,n,W,H,dur}, st) 的中参） ----------------

public sealed class LayoutCtx
{
    public string Text { get; set; } = "";
    public int N { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public double Dur { get; set; }
}
