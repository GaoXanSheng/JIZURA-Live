using Newtonsoft.Json.Linq;

namespace JizuraLive.Engine;

// 移植自 frontend/src/engine/08b_omakase.js（おまかせ = 一键随机）与 11q_sets.js（套装/追加分/和風 的随机放行）。
// 纯引擎层：无 WPF / IO 依赖。Planner 的随机提取经 Reg.RandomAllows → Reg.RandomOk，MCP randomize 与
// UI 一键随机都走 OmakaseApply。
//
// C# 侧的两处对位（JS 里靠 def 上的 set/extra/wa 字段 + J.SETS/J.WA/J.BASE_PACKS 全局表）：
//   1. 属性表：IRegEntry / StyleDef 没有 set/extra/wa 字段，这里按 pack 名派生 + 显式 Mark* 覆盖，
//      结果缓存在 Attributes（注册表变化时自动重建）；
//   2. Reg.RandomOk 委托接线：Install() 里赋值，Planner 经 Reg.RandomAllows 读它。
//
// 补 mood 标签的范围与 JS 的载入顺序一致：08b_omakase.js 在 11p_* 表现包之前载入，所以当时只能给
// 「已经注册的核心部品」（pack = core）补标签；追加分条目的氛围标签来自各自的源文件。C# 里所有包都
// 先装好，只能靠 pack 名区分，因此这里同样只补 Pack == "core" 的条目。

/// <summary>氛围（J.MOODS[k]）：fx 滑杆区间 + 版式/进出场偏好 + 风格池 + 套装门禁。</summary>
public sealed class MoodDef
{
    public string Name = "";

    /// <summary>绑定套装（ホラー）：该套装没开时这个氛围不参与抽取。</summary>
    public string? Set;

    /// <summary>滑杆区间，顺序与 JS 字面量一致（抽取时按序消费随机数）。</summary>
    public readonly List<(string Key, double Lo, double Hi)> Fx = new();

    public string[]? Layout;
    public string[]? Enter;
    public string[]? Exit;
    public string[]? Styles;

    /// <summary>这个氛围里要显式关掉的 hold 手法。</summary>
    public string[]? NoHold;

    /// <summary>非偏好条目的开启概率（缺省 0.22）。</summary>
    public double Sprinkle;

    public MoodDef(string name, string? set, (string Key, double Lo, double Hi)[] fx)
    {
        Name = name;
        Set = set;
        Fx.AddRange(fx);
    }
}

/// <summary>主题（J.THEMES[k]）：在一个方向内一键随机。</summary>
public sealed class ThemeDef
{
    public string Name = "";
    public string[] Moods = Array.Empty<string>();

    /// <summary>主题开启的套装（typo/kinetic/horror）。</summary>
    public string? Set;

    /// <summary>和風主题：开 wa + extra。</summary>
    public bool Wa;

    /// <summary>主题固定的 koma 候选（ballad）。</summary>
    public int[]? Koma;
}

public static class Omakase
{
    // ---------------- J.MOODS ----------------

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
        // ホラー：仅当项目开了ホラー开关才参与抽取，horror 套装部品随之一起来
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
        // chaos 的 layout/enter/exit/styles 在 JS 里是 null（= 全量候选）

        Install();
    }

    // 08b_omakase.js 载入时给「当时已注册的核心部品」补 mood 标签（那时只有 core 包；
    // 追加分包条目各自的氛围标签在自己的源文件里）。C# 不改注册表 Tags：planner 会读 Tags
    //（glitch 权重乘数），改 Tags 会让 plan 与跨语言对拍夹具漂移 —— 这里把同一张表收在本模块内，
    // 供随机抽取的 prefer 集合（J.taggedWith 的对位，见 MoodTaggedWith）查询。
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

    // ---------------- J.THEMES ----------------

    public static readonly string[] ThemeOrder = { "lyricpv", "kinetic", "wa", "horror", "pop", "ballad" };

    public static readonly Dictionary<string, ThemeDef> Themes = new()
    {
        ["lyricpv"] = new ThemeDef { Name = "文字PV", Moods = new[] { "editorial", "graphic", "emotional" }, Set = "typo" },
        ["kinetic"] = new ThemeDef { Name = "キネティック", Moods = new[] { "pop", "graphic", "glitch" }, Set = "kinetic" },
        ["wa"] = new ThemeDef { Name = "和風", Moods = new[] { "calm", "emotional", "editorial" }, Wa = true },
        ["horror"] = new ThemeDef { Name = "ホラー", Moods = new[] { "horror" }, Set = "horror" },
        ["pop"] = new ThemeDef { Name = "ポップ", Moods = new[] { "pop" } },
        ["ballad"] = new ThemeDef { Name = "バラード", Moods = new[] { "calm", "emotional" }, Koma = new[] { 0, 0, 12 } }
    };

    // ---------------- 11q_sets：套装 / 追加分 / 和風 ----------------

    // 首发版自带的 pack（不在表里 = 追加分）与风格、追加字体
    private static readonly HashSet<string> BasePacks = new(StringComparer.Ordinal)
        { "core", "layoutsA", "layoutsB", "enter", "exitHold", "decor", "looks" };

    private static readonly HashSet<string> BaseStyles = new(StringComparer.Ordinal)
    {
        "noir", "crimson", "caution", "magenta", "paper", "hud", "mint", "specimen", "transit", "blueprint", "rouge", "mono"
    };

    private static readonly HashSet<string> ExtraFonts = new(StringComparer.Ordinal)
        { "reggae", "rampart", "potta", "kiwi", "klee", "shippori" };

    /// <summary>J.SETS：套装默认开关（on = 老工程没这个字段时的默认值）。</summary>
    private static readonly Dictionary<string, bool> SetDefault = new()
    {
        ["horror"] = false, ["typo"] = true, ["kinetic"] = true
    };

    /// <summary>J.WA：和風条目按组登记的 key（def 上直接标 wa 的走 MarkWa）。</summary>
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

    // 显式覆盖表（pack 端口可在注册时调用 Mark* 说明 def 上的 set/extra/wa；
    // C# 的 IRegEntry 没有这三个字段，追加分包移植时用它们补齐）
    private static readonly Dictionary<string, string> MarkedSet = new();
    private static readonly Dictionary<string, string> MarkedSetForStyle = new();
    private static readonly HashSet<string> MarkedExtra = new();
    private static readonly HashSet<string> MarkedWa = new();

    private static string Key2(string g, string k) => g + "\u0000" + k;

    public static void MarkSet(string group, string key, string set) => MarkedSet[Key2(group, key)] = set;
    public static void MarkStyleSet(string style, string set) => MarkedSetForStyle[style] = set;
    public static void MarkExtra(string group, string key) => MarkedExtra.Add(Key2(group, key));
    public static void MarkWa(string group, string key) => MarkedWa.Add(Key2(group, key));

    // ---------------- 装机（幂等） ----------------

    private static bool _installed;

    /// <summary>
    /// 接线 Reg.RandomOk（追加分/和風/套装的随机放行）。幂等；App 启动与 MCP 直调路径都会调用。
    /// mood 标签表不在这里建：注册表可能还没装完（App 的 Omakase.Install 早于 PreviewEngine 的
    /// EngineBoot.Install），由 EnsureAttributes 按注册表快照懒建/重建。
    /// </summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        Reg.RandomOk = RandomOk;
    }

    // (组, key) → 08b 载入时补的 mood 标签（仅 core 包条目，与 JS 载入顺序一致）
    private static readonly Dictionary<string, HashSet<string>> MoodTagLookup = new();

    private static void BuildMoodTags()
    {
        MoodTagLookup.Clear();
        void Add(string g, string k, string mood)
        {
            var d = RegDef(g, k);
            if (d == null) return;
            // JS 载入序：只覆盖当时已注册的核心部品（Reg.SeedCore 直写字典的条目 Pack 为 null，
            // 与 J.BASE_PACKS 里的 undefined 同义 → 都算核心）
            if (d.Pack != null && !string.Equals(d.Pack, "core", StringComparison.Ordinal)) return;
            if (!MoodTagLookup.TryGetValue(Key2(g, k), out var set))
                MoodTagLookup[Key2(g, k)] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(mood);
        }
        // MOODS 表：layout/enter/exit 的偏好清单
        foreach (var mood in MoodOrder)
        {
            var M = Moods[mood];
            foreach (var (g, list) in new[] { ("layout", M.Layout), ("enter", M.Enter), ("exit", M.Exit) })
            {
                if (list == null) continue;
                foreach (var k in list) Add(g, k, mood);
            }
        }
        // 补充表：hold/decor/fx
        foreach (var (g, k, moods) in MoodTagTable)
            foreach (var m in moods) Add(g, k, m);
    }

    /// <summary>
    /// J.taggedWith 的对位：顺序表里「源标签命中 mood」或「08b 补的 mood 标签命中」的 key。
    /// （JS 是直接往 def.tags 里 push；C# 不改注册表，避免影响 planner 的 tag 权重与对拍结果。）
    /// </summary>
    private static List<string> MoodTaggedWith(string g, string mood)
    {
        EnsureAttributes();
        var outp = new List<string>();
        foreach (var k in Reg.OrderOf(g))
        {
            var d = RegDef(g, k);
            if (d == null) continue;
            if (d.Tags.Contains(mood) || MoodTagLookup.GetValueOrDefault(Key2(g, k))?.Contains(mood) == true)
                outp.Add(k);
        }
        return outp;
    }

    // ---------------- 属性派生（set / extra / wa） ----------------

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
        // 先标套装归属；带 set 的条目不算追加分，它们有自己的开关。
        // 优先读条目上的真实字段 IRegEntry.Set（JS def 的 set:'typo'/'kinetic'/'horror'）；
        // MarkSet 注记保留为旁路兼容（老接线/测试用）。
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
        // 风格上的 set（JS 11p_horror3 等给风格标 set:'horror'）落 StyleDef.Extra["set"]——真实数据源
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
        BuildMoodTags(); // 与属性表同一份注册表快照：注册表变了就一起重建
    }

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
        // 实时条目优先（注册/替换后即时生效，测试探针与后续包端口都依赖）；
        // 快照兜底（含风格 Extra["set"] 等派生数据）
        var live = RegDef(g, k)?.Set;
        if (live != null) return live;
        EnsureAttributes();
        return _attrSet.GetValueOrDefault(Key2(g, k));
    }

    /// <summary>J.setOn：套装在这个工程里是否开着（老工程没这个字段时用默认值）。</summary>
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

    /// <summary>
    /// J.randomOk：随机能不能用这个条目（g 是组 key、'style' 或 'font'）；先看追加分，再看和風，最后看套装。
    /// 未注册的条目一律 false。
    /// </summary>
    public static bool RandomOk(Project? project, string g, string k)
    {
        if (!Exists(g, k)) return false;
        if (IsExtra(g, k) && !(project != null && project.Extra)) return false;
        if (IsWa(g, k) && project != null && !project.Wa) return false;
        var set = SetOf(g, k);
        if (set != null && !SetOn(project, set)) return false;
        return true;
    }

    // ---------------- 一键随机 ----------------

    /// <summary>一键随机结果（对位 J.omakase 返回的对象 + switches）。</summary>
    public sealed class Result
    {
        public string Mood = "";
        public string Style = "";
        public FxSettings Fx = new();
        public Dictionary<string, Dictionary<string, bool>> Enabled = new();
        public Dictionary<string, string> Fonts = new();
        public JObject Colors = new();
        public Dictionary<string, JObject> Overrides = new();
        public uint Seed;

        /// <summary>主题/氛围要求打开的套装开关（extra/wa/horror/typo/kinetic）。</summary>
        public Dictionary<string, bool> Switches = new();
    }

    private static readonly Dictionary<string, int> MinEnabled = new()
    {
        ["layout"] = 6, ["enter"] = 5, ["exit"] = 5, ["hold"] = 3, ["decor"] = 6,
        ["treat"] = 4, ["bg"] = 4, ["cam"] = 3, ["fx"] = 4, ["trans"] = 3
    };

    private static readonly Dictionary<string, int[]> KomaTable = new()
    {
        ["horror"] = new[] { 12, 8, 0 },
        ["glitch"] = new[] { 12, 12, 8 },
        ["pop"] = new[] { 12, 12, 8, 0 },
        ["calm"] = new[] { 0, 0, 12 },
        ["editorial"] = new[] { 0, 12 },
        ["emotional"] = new[] { 12, 0 },
        ["graphic"] = new[] { 12, 12, 0 }
    };

    /// <summary>
    /// J.omakase：掷 mood、样式、特效强度、手法开关、字体、配色和新 seed；
    /// 歌词、时序、输出设置不动，锁定行保留。纯函数（返回结果，不改传入 project）。
    /// </summary>
    public static Result Randomize(Project project, Func<double>? rnd = null, string? themeId = null)
    {
        Install();
        EnsureAttributes();
        rnd ??= Random.Shared.NextDouble;

        var T = themeId != null && Themes.TryGetValue(themeId, out var td) ? td : null;
        var switches = new Dictionary<string, bool>();
        if (T?.Set != null) switches[T.Set] = true;
        if (T is { Wa: true })
        {
            switches["wa"] = true;
            switches["extra"] = true; // 大多数和风部品属于「追加分」
        }

        // 主题路径：JS 用 Object.assign({}, project, switches) 造一份「按主题开关投影过的工程」
        var pj = project;
        if (T != null && switches.Count > 0)
        {
            pj = Clone(project);
            foreach (var (k, v) in switches) SetSwitch(pj, k, v);
        }

        bool ThemePart(string g, string k)
        {
            if (T == null) return false;
            if (!Exists(g, k)) return false;
            if (T.Set != null && SetOf(g, k) == T.Set) return true;
            return T.Wa && IsWa(g, k);
        }

        T Pick<T>(IReadOnlyList<T> a) => a[(int)(rnd() * a.Count) % a.Count];
        double Range(double lo, double hi) => Math.Round(lo + (hi - lo) * rnd(), 2, MidpointRounding.AwayFromZero);

        bool MoodOk(string k) => Moods[k].Set == null || SetOn(pj, Moods[k].Set);

        var moods = MoodOrder.Where(k => k != pj.Mood && MoodOk(k)).ToList();
        if (T != null)
        {
            var tm = T.Moods.Where(k => Moods.ContainsKey(k) && MoodOk(k)).ToList();
            moods = tm.Where(k => k != pj.Mood).ToList();
            if (moods.Count == 0)
                moods = tm.Count > 0 ? tm : MoodOrder.Where(MoodOk).ToList();
        }
        // 开了ホラー开关时一键随机偏向ホラー氛围（可重复）；有主题时交给主题
        var mood = T == null && MoodOk("horror") && rnd() < 0.55 ? "horror" : Pick(moods);
        var M = Moods[mood];

        // 绑定氛围的套装（ホラー）只在该氛围里启用
        bool MoodSetOk(string g, string k)
        {
            var set = SetOf(g, k);
            if (set == null) return true;
            if (!Moods.Values.Any(m => m.Set == set)) return true;
            return M.Set == set;
        }

        bool OkStyle(string k) => Reg.Styles.ContainsKey(k) && RandomOk(pj, "style", k)
                                  && MoodSetOk("style", k);

        var moodStyles = (M.Styles ?? Array.Empty<string>())
            .Concat(JStyles.StyleOrder.Where(k => Reg.Styles.TryGetValue(k, out var sd) && sd.Tags.Contains(mood)))
            .Distinct()
            .Where(OkStyle)
            .ToArray();
        var pool = (moodStyles.Length > 0 && rnd() < 0.72 ? moodStyles : JStyles.StyleOrder.Where(OkStyle).ToArray())
            .Where(k => k != pj.Style).ToList();
        if (T != null)
        {
            var ts = JStyles.StyleOrder.Where(k => OkStyle(k) && ThemePart("style", k) && k != pj.Style).ToArray();
            if (ts.Length > 0 && rnd() < 0.8) pool = ts.ToList();
        }
        if (pool.Count == 0) pool = JStyles.StyleOrder.Where(k => k != pj.Style && OkStyle(k)).ToList();
        if (pool.Count == 0) pool = JStyles.StyleOrder.Where(k => k != pj.Style).ToList();
        var style = Pick(pool);

        var fx = CopyFx(pj.Fx);
        foreach (var (k, lo, hi) in M.Fx) FxSet(fx, k, Range(lo, hi));
        fx.Koma = Pick(T?.Koma ?? KomaTable.GetValueOrDefault(mood, new[] { 12, 8, 0 }));
        fx.OnTwos = fx.Koma > 0;
        fx.Flash = rnd() < 0.65;
        fx.Hud = Pick(new[] { "auto", "auto", "on", "off" });

        // 每组的手法开关：氛围标签命中的全开（外加氛围自带的核心清单），其余撒一点，
        // 再保一个最小数量，planner 才有变化余地
        var enabled = new Dictionary<string, Dictionary<string, bool>>();
        foreach (var g in Reg.GroupKeys)
        {
            var order = Reg.OrderOf(g)
                .Where(k => !(RegDef(g, k)?.Special ?? true) && RandomOk(pj, g, k) && MoodSetOk(g, k))
                .ToArray();
            var hand = g is "layout" or "enter" or "exit"
                ? (g == "layout" ? M.Layout : g == "enter" ? M.Enter : M.Exit) ?? Array.Empty<string>()
                : Array.Empty<string>();
            var prefer = mood == "chaos"
                ? null
                : new HashSet<string>(hand.Concat(MoodTaggedWith(g, mood)));
            if (prefer != null && T != null)
                foreach (var k in order)
                    if (ThemePart(g, k))
                        prefer.Add(k);
            var on = new Dictionary<string, bool>();
            // 属于其他氛围套装的条目要显式关掉，缺 key 会被当作开启
            foreach (var k in Reg.OrderOf(g))
                if (!MoodSetOk(g, k))
                    on[k] = false;
            foreach (var k in order)
                on[k] = prefer != null
                    ? prefer.Contains(k) || rnd() < (M.Sprinkle > 0 ? M.Sprinkle : 0.22)
                    : rnd() < 0.8;
            var offs = order.Where(k => !on[k]).ToList();
            var n = order.Length - offs.Count;
            var min = Math.Min(MinEnabled.GetValueOrDefault(g, 3), order.Length);
            while (n < min && offs.Count > 0)
            {
                var k = offs[(int)(rnd() * offs.Count) % offs.Count];
                offs.Remove(k);
                on[k] = true;
                n++;
            }
            enabled[g] = on;
        }
        enabled["enter"]["cut"] = true;
        enabled["exit"]["cut"] = true;
        if (enabled.TryGetValue("treat", out var treat)) treat["none"] = true;
        if (enabled.TryGetValue("bg", out var bg)) bg["none"] = true;
        if (enabled.TryGetValue("cam", out var cam)) cam["push"] = true;
        if (M.NoHold != null)
            foreach (var k in M.NoHold)
                if (enabled.TryGetValue("hold", out var hold) && hold.ContainsKey(k))
                    hold[k] = false;
        enabled["hold"]["still"] = true;

        // 字体：偶尔把标题/明朝位换成目录里其他字体
        var fonts = new Dictionary<string, string>();
        var faces = JFonts.Table
            .Where(kv => !kv.Value.User && kv.Value.Kind is not ("mono" or "pixel") && RandomOk(pj, "font", kv.Key))
            .ToArray();
        if (rnd() < 0.4)
        {
            var cand = faces.Where(kv => kv.Value.Weight >= 700 || kv.Value.Kind is "display" or "round").ToArray();
            if (cand.Length > 0) fonts["display"] = Pick(cand).Key;
        }
        if (rnd() < 0.3)
        {
            var cand = faces.Where(kv => kv.Value.Kind is "mincho" or "brush").ToArray();
            if (cand.Length > 0) fonts["serif"] = Pick(cand).Key;
        }
        if (mood == "chaos" && rnd() < 0.2 && JFonts.Table.ContainsKey("dot")) fonts["display"] = "dot";

        // 配色：多数用样式自带色板，偶尔换新的 accent/幽灵色对
        var colors = pj.Colors != null ? (JObject)pj.Colors.DeepClone() : new JObject();
        colors["enabled"] = false;
        colors["accentOn"] = false;
        if (rnd() < 0.38)
        {
            var bgHex = Reg.Styles.TryGetValue(style, out var sd) && sd.Schemes.Count > 0
                ? sd.Schemes[0].Str("bg") ?? "#000000"
                : "#000000";
            var pal = J.RandomPalette(bgHex, rnd);
            colors["accent"] = pal.Accent;
            colors["ghostA"] = pal.GhostA;
            colors["ghostB"] = pal.GhostB;
            colors["accentOn"] = true;
            colors.Remove("mode");
        }

        // 锁定行保留，其余逐行选择丢弃（切分时刻属于时序，不属于外观）
        var overrides = new Dictionary<string, JObject>();
        foreach (var (i, o) in pj.Overrides)
        {
            if (Truthy(o["lock"])) overrides[i] = (JObject)o.DeepClone();
            else if (o["cutTime"] != null) overrides[i] = new JObject { ["cutTime"] = o["cutTime"]!.DeepClone() };
        }

        var result = new Result
        {
            Mood = mood,
            Style = style,
            Fx = fx,
            Enabled = enabled,
            Fonts = fonts,
            Colors = colors,
            Overrides = overrides,
            Seed = (uint)Math.Floor(rnd() * 1e9),
            Switches = switches
        };
        return result;
    }

    /// <summary>
    /// UI / MCP 一键随一的共同落点：掷随机 → 写回工程 → 恢复锁定（tech 组开关原样、params 参数原样）。
    /// 对位 look.ts 的 omakase()（历史栈、UI 刷新、预览复位由调用方负责）。
    /// </summary>
    public static Result OmakaseApply(Project project, Func<double>? rnd = null, string? themeId = null)
    {
        var keepEnabled = LockedEnabled(project);
        var keepParams = LockedParams(project);
        var r = Randomize(project, rnd, themeId);
        ApplyTo(project, r);
        RestoreEnabled(project, keepEnabled);
        RestoreParams(project, keepParams);
        return r;
    }

    /// <summary>把 Randomize 的结果写回工程（对位 UI 的 Object.assign(S.project, r)）。</summary>
    public static void ApplyTo(Project project, Result r)
    {
        project.Mood = r.Mood;
        project.Style = r.Style;
        project.Fx = r.Fx;
        project.Enabled = r.Enabled;
        project.Fonts = r.Fonts;
        project.Colors = r.Colors;
        project.Overrides = r.Overrides;
        project.Seed = r.Seed;
        foreach (var (k, v) in r.Switches) SetSwitch(project, k, v);
    }

    private static void SetSwitch(Project p, string key, bool v)
    {
        switch (key)
        {
            case "extra": p.Extra = v; break;
            case "wa": p.Wa = v; break;
            case "horror": p.Horror = v; break;
            case "typo": p.Typo = v; break;
            case "kinetic": p.Kinetic = v; break;
        }
    }

    // ---------------- 锁（look.ts 的 lockedEnabled / lockedParams / restore*） ----------------

    /// <summary>锁定的手法组 → 该组开关表原样快照。</summary>
    public static Dictionary<string, Dictionary<string, bool>> LockedEnabled(Project project)
    {
        var outp = new Dictionary<string, Dictionary<string, bool>>();
        foreach (var g in project.Locks.Tech.Keys)
            if (project.Locks.Tech[g] && project.Enabled.TryGetValue(g, out var m))
                outp[g] = new Dictionary<string, bool>(m);
        return outp;
    }

    public static void RestoreEnabled(Project project, Dictionary<string, Dictionary<string, bool>> keep)
    {
        foreach (var (g, m) in keep) project.Enabled[g] = m;
    }

    /// <summary>锁定的效果参数 → 当前值（koma/flash 有专用读法，其余按 fx 键）。</summary>
    public static Dictionary<string, object> LockedParams(Project project)
    {
        var outp = new Dictionary<string, object>();
        foreach (var (k, on) in project.Locks.Params)
        {
            if (!on) continue;
            var v = FxGet(project.Fx, k);
            if (v != null) outp[k] = v;
        }
        return outp;
    }

    public static void RestoreParams(Project project, Dictionary<string, object> keep)
    {
        foreach (var (k, v) in keep) FxSet(project.Fx, k, v);
    }

    // ---------------- fx 键访问（JS 的 S.project.fx[k] 松散键 ↔ C# 的 FxSettings 强类型） ----------------

    /// <summary>J.FX 键名（camelCase，与 JS/JSON 契约一致）：能随机抽取/被锁定的全量参数。</summary>
    public static readonly string[] FxKeys =
    {
        "motion", "glitch", "chroma", "decor", "density", "texture", "bgSwitch",
        "flash", "onTwos", "koma", "hud", "hideNo", "hideTime"
    };

    public static bool IsFxKey(string k) => FxKeys.Contains(k);

    /// <summary>读一个 fx 参数（未知名返回 null）；koma 的读法对位 J.komaOf。</summary>
    public static object? FxGet(FxSettings f, string k) => k switch
    {
        "motion" => f.Motion,
        "glitch" => f.Glitch,
        "chroma" => f.Chroma,
        "decor" => f.Decor,
        "density" => f.Density,
        "texture" => f.Texture,
        "bgSwitch" => f.BgSwitch,
        "flash" => f.Flash,
        "onTwos" => f.OnTwos,
        "koma" => f.OnTwos ? f.Koma : 0,
        "hud" => f.Hud,
        "hideNo" => f.HideNo,
        "hideTime" => f.HideTime,
        _ => null
    };

    /// <summary>写一个 fx 参数（未知名忽略）。</summary>
    public static void FxSet(FxSettings f, string k, object? v)
    {
        double D(double d) => v is double d2 ? d2 : v is float f2 ? f2 : v is int i2 ? i2 : v is long l2 ? l2 : d;
        bool B(bool d) => v is bool b ? b : d;
        switch (k)
        {
            case "motion": f.Motion = D(f.Motion); break;
            case "glitch": f.Glitch = D(f.Glitch); break;
            case "chroma": f.Chroma = D(f.Chroma); break;
            case "decor": f.Decor = D(f.Decor); break;
            case "density": f.Density = D(f.Density); break;
            case "texture": f.Texture = D(f.Texture); break;
            case "bgSwitch": f.BgSwitch = D(f.BgSwitch); break;
            case "flash": f.Flash = B(f.Flash); break;
            case "onTwos": f.OnTwos = B(f.OnTwos); break;
            case "koma": f.Koma = (int)D(f.Koma); break;
            case "hud": f.Hud = v is string s ? s : f.Hud; break;
            case "hideNo": f.HideNo = B(f.HideNo); break;
            case "hideTime": f.HideTime = B(f.HideTime); break;
        }
    }

    // ---------------- 小工具 ----------------

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

    /// <summary>条目是否存在（g = 组 key / 'style' / 'font'）。</summary>
    public static bool Exists(string g, string k) => g switch
    {
        "style" => Reg.Styles.ContainsKey(k),
        "font" => JFonts.Table.ContainsKey(k),
        _ => RegDef(g, k) != null
    };

    private static FxSettings CopyFx(FxSettings f) => new()
    {
        Motion = f.Motion, Glitch = f.Glitch, Chroma = f.Chroma, Decor = f.Decor,
        Density = f.Density, Texture = f.Texture, Flash = f.Flash, OnTwos = f.OnTwos,
        Koma = f.Koma, Hud = f.Hud, BgSwitch = f.BgSwitch, HideNo = f.HideNo, HideTime = f.HideTime
    };

    private static bool Truthy(JToken? t) => t != null &&
        (t.Type == JTokenType.Boolean && (bool)t
         || t.Type is JTokenType.Integer or JTokenType.Float && (double)t != 0);

    private static Project Clone(Project p) =>
        Newtonsoft.Json.JsonConvert.DeserializeObject<Project>(
            Newtonsoft.Json.JsonConvert.SerializeObject(p)) ?? p;
}
