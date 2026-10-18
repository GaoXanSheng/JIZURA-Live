using JizuraLive.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JizuraLive.LegacyEngine;

public interface IRegEntry
{
        string Key { get; set; }

        string? Pack { get; set; }

        bool Special { get; set; }

    string Name { get; set; }
    string[] Tags { get; set; }
    double? W { get; set; }

        string? Set { get; set; }

        bool Extra { get; set; }
}

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

}

public sealed class StyleDef
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string[] Tags { get; set; } = System.Array.Empty<string>();

        public bool Hud { get; set; }

        public double GlitchBoost { get; set; } = 1;

        public JObject Bias { get; set; } = new();

        public JObject Decor { get; set; } = new();

    public List<Scheme> Schemes { get; set; } = new();

        [JsonExtensionData]
    public Dictionary<string, JToken>? Extra { get; set; } = new();
}

public sealed class LayoutDef : IRegEntry
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string[] Tags { get; set; } = System.Array.Empty<string>();
    public double? W { get; set; }
    public bool Special { get; set; }
    public string? Pack { get; set; }

        public string? Set { get; set; }

        public bool Extra { get; set; }

        public Func<int, bool>? Fits { get; set; }

        public double? Portrait { get; set; }

        public double? Emph { get; set; }

        public bool Busy { get; set; }

        public object? Treat { get; set; }

        public JObject? EnterBias { get; set; }

        public bool? Cam { get; set; }

        public Func<Rng, LayoutCtx, StyleDef, JObject>? Plan { get; set; }

    public Action<Env>? Draw { get; set; }
}

public sealed class MoveDef : IRegEntry
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string[] Tags { get; set; } = System.Array.Empty<string>();
    public double? W { get; set; }
    public bool Special { get; set; }
    public string? Pack { get; set; }

        public string? Set { get; set; }

        public bool Extra { get; set; }

        public double? MinDur { get; set; }

        public int? MaxChars { get; set; }

        public Func<double, int, double>? InDur { get; set; }

        public Func<double, int, double>? OutDur { get; set; }

    public Action<Env>? Draw { get; set; }
}

public sealed class DecorDef : IRegEntry
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string[] Tags { get; set; } = System.Array.Empty<string>();
    public double? W { get; set; }
    public bool Special { get; set; }
    public string? Pack { get; set; }

        public string? Set { get; set; }

        public bool Extra { get; set; }

        public string? Layer { get; set; }
    public bool Subtle { get; set; }

    public Action<Env, DecorSpec>? Draw { get; set; }
}

public sealed class TreatDef : IRegEntry
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string[] Tags { get; set; } = System.Array.Empty<string>();
    public double? W { get; set; }
    public bool Special { get; set; }
    public string? Pack { get; set; }

        public string? Set { get; set; }

        public bool Extra { get; set; }

        public bool Safe { get; set; }

    public Func<Rng, StyleDef, JObject>? Plan { get; set; }
    public Action<Env>? Draw { get; set; }
}

public sealed class BgDef : IRegEntry
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string[] Tags { get; set; } = System.Array.Empty<string>();
    public double? W { get; set; }
    public bool Special { get; set; }
    public string? Pack { get; set; }

        public string? Set { get; set; }

        public bool Extra { get; set; }
    public bool Subtle { get; set; }

    public Func<Rng, StyleDef, JObject>? Plan { get; set; }
    public Action<Env>? Draw { get; set; }
}

public sealed class CamState
{
    public double S { get; set; } = 1;
    public double X { get; set; }
    public double Y { get; set; }
    public double Rot { get; set; }
    public double Skx { get; set; }
    public double Blur { get; set; }
    public double? Sx { get; set; }
    public double? Sy { get; set; }
}

public sealed class CamDef : IRegEntry
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string[] Tags { get; set; } = System.Array.Empty<string>();
    public double? W { get; set; }
    public bool Special { get; set; }
    public string? Pack { get; set; }

        public string? Set { get; set; }

        public bool Extra { get; set; }

        public bool Strong { get; set; }

    public Func<Rng, StyleDef, JObject>? Plan { get; set; }
    public Func<Env, CamState>? Get { get; set; }
}

public sealed class FxDef : IRegEntry
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string[] Tags { get; set; } = System.Array.Empty<string>();
    public double? W { get; set; }
    public bool Special { get; set; }
    public string? Pack { get; set; }

        public string? Set { get; set; }

        public bool Extra { get; set; }
    public bool Builtin { get; set; }
    public double Amp { get; set; } = 1;
    public double Dur { get; set; } = 3;
    public bool Glitchy { get; set; }

        public bool Edge { get; set; } = true;

        public bool Mid { get; set; }

        public int? Pre { get; set; }

    public Action<Env, PlanEvent>? Draw { get; set; }
}

public sealed class TransDef : IRegEntry
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string[] Tags { get; set; } = System.Array.Empty<string>();
    public double? W { get; set; }
    public bool Special { get; set; }
    public string? Pack { get; set; }

        public string? Set { get; set; }

        public bool Extra { get; set; }

        public double? Dur { get; set; }

    public Func<Rng, StyleDef, JObject>? Plan { get; set; }

        public Delegate? Draw { get; set; }
}

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

        public static Func<Project, string, string, bool>? RandomOk { get; set; }

        public static void SeedCore()
    {
        if (TreatOrder.Count > 0) return;         Treat["none"] = new TreatDef { Key = "none", Name = "なし" };
        TreatOrder.Add("none");
        Bg["none"] = new BgDef { Key = "none", Name = "無地" };
        BgOrder.Add("none");
        Camera["push"] = new CamDef
        {
            Key = "push",
            Name = "ゆっくり寄る",
            Tags = new[] { "calm", "editorial", "emotional", "graphic", "pop", "glitch" },
            W = 5,
            Get = env => new CamState
            {
                S = 1 + 0.03 * env.Fx.Motion * J.Clamp(env.Lt / Math.Max(0.3, env.Cut?.Dur ?? 0))
            }
        };
        CameraOrder.Add("push");
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
        def.Set ??= DeriveSet(def.Pack);
        if (prevPack != null && prevPack != def.Pack)
            Log.Warn($"JIZURA: {group}.{key} is being replaced");
        reg[key] = def;
        if (!def.Special && !order.Contains(key)) order.Add(key);
    }

        private static string? DeriveSet(string? pack)
    {
        if (string.IsNullOrEmpty(pack)) return null;
        var name = pack.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        return name is "horror" or "kinetic" or "typo" ? name : null;
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

    public static bool RandomAllows(Project project, string g, string k) => RandomOk == null || RandomOk(project, g, k);

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

}

public sealed class LayoutCtx
{
    public string Text { get; set; } = "";
    public int N { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public double Dur { get; set; }
}
