using Newtonsoft.Json.Linq;

namespace JizuraLive.LegacyEngine;

public sealed class MoodDef
{
    public string Name = "";

        public string? Set;

        public readonly List<(string Key, double Lo, double Hi)> Fx = new();

    public string[]? Layout;
    public string[]? Enter;
    public string[]? Exit;
    public string[]? Styles;

        public string[]? NoHold;

        public double Sprinkle;

    public MoodDef(string name, string? set, (string Key, double Lo, double Hi)[] fx)
    {
        Name = name;
        Set = set;
        Fx.AddRange(fx);
    }
}

public sealed class ThemeDef
{
    public string Name = "";
    public string[] Moods = Array.Empty<string>();

        public string? Set;

        public bool Wa;

        public int[]? Koma;
}

public static class Omakase
{

    public static readonly string[] MoodOrder =
        { "glitch", "calm", "pop", "graphic", "editorial", "emotional", "horror", "chaos" };

    public static readonly Dictionary<string, MoodDef> Moods = new();

    static Omakase()
    {
        Moods["glitch"] = new MoodDef("グリッチ", null, new[]
        {
            ("motion", 0.6, 0.9), ("glitch", 0.75, 1.0), ("chroma", 0.75, 1.0), ("decor", 0.3, 0.6),
            ("density", 0.6, 0.9), ("texture", 0.5, 0.9), ("bgSwitch", 0.3, 0.6)
        })
        {
            Layout = new[] { "center", "condensed", "huge", "tile", "marquee", "vcols", "scatter", "stack" },
            Enter = new[] { "slice", "scramble", "assemble", "flicker", "zoom", "stretch" },
            Exit = new[] { "glitch", "slice", "explode", "fall" },
            Styles = new[] { "noir", "crimson", "mint", "mono", "hud" }
        };
        Moods["calm"] = new MoodDef("しっとり", null, new[]
        {
            ("motion", 0.3, 0.55), ("glitch", 0.05, 0.25), ("chroma", 0.2, 0.5), ("decor", 0.2, 0.5),
            ("density", 0.25, 0.45), ("texture", 0.5, 0.85), ("bgSwitch", 0.1, 0.3)
        })
        {
            Layout = new[] { "center", "vcols", "gloss", "stack", "circle", "type", "mixed" },
            Enter = new[] { "blur", "type", "wipe", "assemble" },
            Exit = new[] { "blur", "drift", "wipe", "shrink" },
            Styles = new[] { "specimen", "paper", "hud", "noir" },
            NoHold = new[] { "glitchtick", "jitter" }
        };
        Moods["pop"] = new MoodDef("ポップ", null, new[]
        {
            ("motion", 0.7, 1.0), ("glitch", 0.1, 0.35), ("chroma", 0.3, 0.6), ("decor", 0.6, 1.0),
            ("density", 0.5, 0.8), ("texture", 0.2, 0.5), ("bgSwitch", 0.4, 0.8)
        })
        {
            Layout = new[] { "mixed", "scatter", "wave", "labels", "pill", "ring", "huge", "diag", "center" },
            Enter = new[] { "pop", "drop", "spin", "stretch", "zoom" },
            Exit = new[] { "scatter", "shrink", "stretch", "blur" },
            Styles = new[] { "magenta", "caution", "transit", "blueprint", "rouge" }
        };
        Moods["graphic"] = new MoodDef("グラフィック", null, new[]
        {
            ("motion", 0.5, 0.8), ("glitch", 0.2, 0.5), ("chroma", 0.4, 0.7), ("decor", 0.7, 1.0),
            ("density", 0.5, 0.8), ("texture", 0.4, 0.7), ("bgSwitch", 0.3, 0.7)
        })
        {
            Layout = new[] { "diag", "labels", "marquee", "tile", "condensed", "huge", "circle", "pill" },
            Enter = new[] { "wipe", "slice", "stretch", "zoom" },
            Exit = new[] { "wipe", "slice", "stretch" },
            Styles = new[] { "blueprint", "caution", "rouge", "mint", "transit" }
        };
        Moods["editorial"] = new MoodDef("エディトリアル", null, new[]
        {
            ("motion", 0.4, 0.65), ("glitch", 0.1, 0.3), ("chroma", 0.2, 0.45), ("decor", 0.4, 0.7),
            ("density", 0.35, 0.6), ("texture", 0.6, 0.9), ("bgSwitch", 0.2, 0.4)
        })
        {
            Layout = new[] { "gloss", "vcols", "mixed", "stack", "type", "center", "circle" },
            Enter = new[] { "type", "blur", "wipe", "assemble" },
            Exit = new[] { "blur", "drift", "wipe" },
            Styles = new[] { "specimen", "paper", "noir", "mono", "hud" }
        };
        Moods["emotional"] = new MoodDef("エモーショナル", null, new[]
        {
            ("motion", 0.55, 0.85), ("glitch", 0.3, 0.6), ("chroma", 0.5, 0.85), ("decor", 0.3, 0.6),
            ("density", 0.4, 0.7), ("texture", 0.6, 1.0), ("bgSwitch", 0.2, 0.5)
        })
        {
            Layout = new[] { "huge", "center", "vcols", "stack", "condensed", "mixed", "circle" },
            Enter = new[] { "assemble", "blur", "zoom", "wipe", "slice" },
            Exit = new[] { "drift", "explode", "fall", "blur" },
            Styles = new[] { "noir", "paper", "hud", "mono", "crimson" }
        };
        Moods["horror"] = new MoodDef("ホラー", "horror", new[]
        {
            ("motion", 0.35, 0.65), ("glitch", 0.3, 0.7), ("chroma", 0.2, 0.5), ("decor", 0.3, 0.6),
            ("density", 0.3, 0.55), ("texture", 0.7, 1.0), ("bgSwitch", 0.1, 0.3)
        })
        {
            Layout = new[] { "center", "vcols", "stack", "huge", "type" },
            Enter = new[] { "flicker", "blur", "type", "scramble" },
            Exit = new[] { "blur", "glitch", "fall", "drift" },
            Styles = new[] { "noir", "mono", "crimson" },
            NoHold = new[] { "wave" },
            Sprinkle = 0.1
        };
        Moods["chaos"] = new MoodDef("全部入り", null, new[]
        {
            ("motion", 0.5, 1.0), ("glitch", 0.3, 1.0), ("chroma", 0.4, 1.0), ("decor", 0.4, 1.0),
            ("density", 0.45, 0.9), ("texture", 0.3, 1.0), ("bgSwitch", 0.3, 0.9)
        });

        Install();
    }

    private static readonly (string G, string K, string[] M)[] MoodTagTable =
    {
        ("hold", "still", new[] { "calm", "editorial", "emotional", "graphic" }),
        ("hold", "drift", new[] { "calm", "emotional", "editorial" }),
        ("hold", "breathe", new[] { "calm", "emotional" }),
        ("hold", "wave", new[] { "pop" }),
        ("hold", "jitter", new[] { "glitch", "pop" }),
        ("hold", "glitchtick", new[] { "glitch" }),
        ("decor", "brackets", new[] { "graphic", "editorial" }),
        ("decor", "rings", new[] { "graphic", "emotional" }),
        ("decor", "dots", new[] { "pop", "graphic" }),
        ("decor", "arrows", new[] { "pop", "graphic" }),
        ("decor", "slash", new[] { "glitch", "graphic" }),
        ("decor", "sparks", new[] { "pop" }),
        ("decor", "leaders", new[] { "editorial", "calm" }),
        ("decor", "waveform", new[] { "emotional", "calm" }),
        ("decor", "barcode", new[] { "glitch", "graphic" }),
        ("decor", "grid", new[] { "graphic", "editorial" }),
        ("decor", "stripes", new[] { "graphic", "pop" }),
        ("decor", "blobs", new[] { "pop", "emotional" }),
        ("decor", "bars", new[] { "graphic", "glitch" }),
        ("decor", "shapes", new[] { "pop", "graphic" }),
        ("decor", "counter", new[] { "graphic", "editorial" }),
        ("fx", "chroma", new[] { "glitch", "emotional", "pop", "graphic" }),
        ("fx", "shake", new[] { "pop", "glitch", "emotional" }),
        ("fx", "slice", new[] { "glitch" }),
        ("fx", "block", new[] { "glitch" }),
        ("fx", "invert", new[] { "glitch", "graphic" }),
        ("fx", "flash", new[] { "pop", "emotional", "glitch" }),
        ("fx", "zoom", new[] { "pop", "emotional" }),
        ("fx", "mosaic", new[] { "glitch" })
    };

    public static readonly Dictionary<string, ThemeDef> Themes = new()
    {
        ["lyricpv"] = new ThemeDef { Name = "文字PV", Moods = new[] { "editorial", "graphic", "emotional" }, Set = "typo" },
        ["kinetic"] = new ThemeDef { Name = "キネティック", Moods = new[] { "pop", "graphic", "glitch" }, Set = "kinetic" },
        ["wa"] = new ThemeDef { Name = "和風", Moods = new[] { "calm", "emotional", "editorial" }, Wa = true },
        ["horror"] = new ThemeDef { Name = "ホラー", Moods = new[] { "horror" }, Set = "horror" },
        ["pop"] = new ThemeDef { Name = "ポップ", Moods = new[] { "pop" } },
        ["ballad"] = new ThemeDef { Name = "バラード", Moods = new[] { "calm", "emotional" }, Koma = new[] { 0, 0, 12 } }
    };

    private static readonly HashSet<string> BasePacks = new(StringComparer.Ordinal)
        { "core", "layoutsA", "layoutsB", "enter", "exitHold", "decor", "looks" };

    private static readonly HashSet<string> BaseStyles = new(StringComparer.Ordinal)
    {
        "noir", "crimson", "caution", "magenta", "paper", "hud", "mint", "specimen", "transit", "blueprint", "rouge", "mono"
    };

    private static readonly HashSet<string> ExtraFonts = new(StringComparer.Ordinal)
        { "reggae", "rampart", "potta", "kiwi", "klee", "shippori" };

        private static readonly Dictionary<string, bool> SetDefault = new()
    {
        ["horror"] = false, ["typo"] = true, ["kinetic"] = true
    };

        private static readonly Dictionary<string, string[]> WaKeys = new()
    {
        ["layout"] = new[] { "ema", "chochin", "noren", "tanzaku", "omikuji", "kakejiku", "shoji", "karuta", "origami", "postcard", "letterPaper", "genkou", "hanko" },
        ["enter"] = new[] { "fanOpen", "brushReveal" },
        ["exit"] = new[] { "fanClose" },
        ["decor"] = new[] { "seal", "kamon", "seigaiha", "asanoha", "chochin", "shimenawa", "sensu", "tsukiKumo", "momiji", "namiGashira", "kasumi", "brushStroke", "petals" },
        ["bg"] = new[] { "seigaiha", "asanoha" },
        ["treat"] = new[] { "monoGrid" },
        ["style"] = new[] { "sakura", "sumi" }
    };

    private static readonly Dictionary<string, string> MarkedSet = new();
    private static readonly Dictionary<string, string> MarkedSetForStyle = new();
    private static readonly HashSet<string> MarkedExtra = new();
    private static readonly HashSet<string> MarkedWa = new();

    private static string Key2(string g, string k) => g + "\u0000" + k;

    private static bool _installed;

        public static void Install()
    {
        if (_installed) return;
        _installed = true;
        Reg.RandomOk = RandomOk;
    }

    private static readonly Dictionary<string, HashSet<string>> MoodTagLookup = new();

    private static void BuildMoodTags()
    {
        MoodTagLookup.Clear();
        void Add(string g, string k, string mood)
        {
            var d = RegDef(g, k);
            if (d == null) return;
            if (d.Pack != null && !string.Equals(d.Pack, "core", StringComparison.Ordinal)) return;
            if (!MoodTagLookup.TryGetValue(Key2(g, k), out var set))
                MoodTagLookup[Key2(g, k)] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(mood);
        }
        foreach (var mood in MoodOrder)
        {
            var M = Moods[mood];
            foreach (var (g, list) in new[] { ("layout", M.Layout), ("enter", M.Enter), ("exit", M.Exit) })
            {
                if (list == null) continue;
                foreach (var k in list) Add(g, k, mood);
            }
        }
        foreach (var (g, k, moods) in MoodTagTable)
            foreach (var m in moods) Add(g, k, m);
    }

    private static Dictionary<string, string> _attrSet = new();
    private static HashSet<string> _attrExtra = new();
    private static HashSet<string> _attrWa = new();
    private static int _attrStamp = -1;

    private static void EnsureAttributes()
    {
        var stamp = 0;
        foreach (var g in Reg.GroupKeys) stamp = stamp * 31 + Reg.OrderOf(g).Count;
        stamp = stamp * 31 + Reg.Styles.Count;
        stamp = stamp * 31 + JFonts.Table.Count;
        if (stamp == _attrStamp) return;
        _attrStamp = stamp;

        var set = new Dictionary<string, string>();
        var extra = new HashSet<string>();
        var wa = new HashSet<string>();
        foreach (var g in Reg.GroupKeys)
            foreach (var k in Reg.OrderOf(g))
            {
                var d = RegDef(g, k);
                if (d == null) continue;
                var s = d.Set ?? MarkedSet.GetValueOrDefault(Key2(g, k));
                if (s == null && d.Pack != null && SetDefault.ContainsKey(d.Pack)) s = d.Pack;
                if (s != null) set[Key2(g, k)] = s;
            }
        foreach (var g in Reg.GroupKeys)
            foreach (var k in Reg.OrderOf(g))
            {
                var d = RegDef(g, k);
                if (d == null) continue;
                var hasSet = set.ContainsKey(Key2(g, k));
                if (d.Extra || MarkedExtra.Contains(Key2(g, k)) || (!hasSet && d.Pack != null && !BasePacks.Contains(d.Pack)))
                    extra.Add(Key2(g, k));
            }
        foreach (var (style, s) in MarkedSetForStyle) set[Key2("style", style)] = s;
        foreach (var k in JStyles.StyleOrder)
        {
            if (set.ContainsKey(Key2("style", k))) continue;
            var sd = Reg.Styles.GetValueOrDefault(k);
            if (sd?.Extra != null && sd.Extra.TryGetValue("set", out var sv) && sv is JValue jv && jv.Type == JTokenType.String)
                set[Key2("style", k)] = (string)jv.Value!;
        }
        foreach (var k in JStyles.StyleOrder)
            if (!BaseStyles.Contains(k) && !set.ContainsKey(Key2("style", k))) extra.Add(Key2("style", k));
        foreach (var k in ExtraFonts)
            if (JFonts.Table.ContainsKey(k)) extra.Add(Key2("font", k));
        foreach (var (g, keys) in WaKeys)
            foreach (var k in keys) wa.Add(Key2(g, k));
        foreach (var kv in MarkedWa) wa.Add(kv);

        _attrSet = set;
        _attrExtra = extra;
        _attrWa = wa;
        BuildMoodTags();     }

    public static bool IsExtra(string g, string k)
    {
        EnsureAttributes();
        return _attrExtra.Contains(Key2(g, k));
    }

    public static bool IsWa(string g, string k)
    {
        EnsureAttributes();
        return _attrWa.Contains(Key2(g, k));
    }

    public static string? SetOf(string g, string k)
    {
        var live = RegDef(g, k)?.Set;
        if (live != null) return live;
        EnsureAttributes();
        return _attrSet.GetValueOrDefault(Key2(g, k));
    }

        public static bool SetOn(Project? project, string set)
    {
        if (project == null) return SetDefault.GetValueOrDefault(set);
        return set switch
        {
            "extra" => project.Extra,
            "wa" => project.Wa,
            "horror" => project.Horror,
            "typo" => project.Typo,
            "kinetic" => project.Kinetic,
            _ => SetDefault.GetValueOrDefault(set)
        };
    }

        public static bool RandomOk(Project? project, string g, string k)
    {
        if (!Exists(g, k)) return false;
        if (IsExtra(g, k) && !(project != null && project.Extra)) return false;
        if (IsWa(g, k) && project != null && !project.Wa) return false;
        var set = SetOf(g, k);
        if (set != null && !SetOn(project, set)) return false;
        return true;
    }

    private static IRegEntry? RegDef(string g, string k) => g switch
    {
        "layout" => Reg.Layouts.GetValueOrDefault(k),
        "enter" => Reg.Enter.GetValueOrDefault(k),
        "hold" => Reg.Hold.GetValueOrDefault(k),
        "exit" => Reg.Exit.GetValueOrDefault(k),
        "decor" => Reg.Decor.GetValueOrDefault(k),
        "treat" => Reg.Treat.GetValueOrDefault(k),
        "bg" => Reg.Bg.GetValueOrDefault(k),
        "cam" => Reg.Camera.GetValueOrDefault(k),
        "fx" => Reg.Fxe.GetValueOrDefault(k),
        "trans" => Reg.Trans.GetValueOrDefault(k),
        _ => null
    };

        public static bool Exists(string g, string k) => g switch
    {
        "style" => Reg.Styles.ContainsKey(k),
        "font" => JFonts.Table.ContainsKey(k),
        _ => RegDef(g, k) != null
    };

}
