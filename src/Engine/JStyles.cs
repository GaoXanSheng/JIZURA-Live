using Newtonsoft.Json.Linq;

namespace JizuraLive.Engine;

// 移植自 frontend/src/engine/04_styles.js
//
// 风格包：每包 = cut 间轮换的几套配色 scheme + 字体角色 + 质感 + ghost 色 + 版式/手法倾向 bias。
// scheme 字段：bg/fg/sub/accent/accent2/ink（贴纸和盒子的底色）/dim（压暗文字）/ghostA/ghostB/grad。
//
// 对位关系：
//   J.STYLES.xxx（核心 12 风格）→ Reg.Styles[key] = StyleDef（Reg 没有 style 组的 Register 重载，
//     直接写字典；name 必填语义由字面量保证。核心条目没有 tags/w 字段 → Tags 留空）。
//   J.STYLE_ORDER → StyleOrder（可变 List，11p_styles 追加风格时沿用「不覆盖核心风格」语义）。
//   J.resolveStyle / J.keyMode / J.KEY_BG → ResolveStyle / KeyMode / KeyBg。
//   scheme 的键全部原样保留在 Scheme.Extra（渲染数据契约，一字不改）；风格包其余字段
//   （desc/fonts/texture/ghost/glow/useGrad/key）放进 StyleDef.Extra。
public static class JStyles
{
    // ---------------- J.STYLE_ORDER：UI 显示顺序 ----------------

    public static readonly List<string> StyleOrder = new()
    {
        "noir", "crimson", "caution", "magenta", "paper", "hud", "mint", "specimen", "transit", "blueprint", "rouge", "mono"
    };

    // J.KEY_BG：合成用背景（绿幕/黑幕）。
    public static readonly Dictionary<string, string> KeyBg = new()
    {
        ["green"] = "#00FF00",
        ["black"] = "#000000"
    };

    static JStyles() => RegisterAll();

    /// <summary>04_styles.js 的 IIFE 载入即注册；显式公开供引擎装配按文件序调用。</summary>
    public static void RegisterAll()
    {
        Reg.Styles["noir"] = Style("noir", "ノワール・クロマ", "黒地・白文字・シアン/琥珀の色ズレ",
            new[]
            {
                Sch(
                    ("bg", "#060607"), ("fg", "#F5EEEA"), ("sub", "#BDB6B2"), ("accent", "#F5A50C"),
                    ("accent2", "#16F4D4"), ("ink", "#F5EEEA"), ("dim", "#2A2A2E"),
                    ("ghostA", "#F5A50C"), ("ghostB", "#16F4D4")),
                Sch(
                    ("bg", "#F2EDE8"), ("fg", "#0B0B0C"), ("sub", "#4A4644"), ("accent", "#E0600C"),
                    ("accent2", "#0FAE98"), ("ink", "#0B0B0C"), ("dim", "#D9D2CC"),
                    ("ghostA", "#F5A50C"), ("ghostB", "#16C4B4"), ("swap", true)),
            },
            Fonts(new[] { "gothic_black", "dela", "zenkaku" }, new[] { "mincho_light", "mincho" },
                new[] { "gothic_med" }, new[] { "mono" }),
            Tex(0.9, 0, 0), 1.0,
            Bias(
                W(("vcols", 2), ("condensed", 2), ("marquee", 1.6), ("tile", 1.4), ("center", 1.2)),
                W(("assemble", 2.2), ("slice", 1.8), ("stretch", 1.4)),
                W(("explode", 1.8), ("fall", 1.2), ("drift", 1.4))),
            W(("rings", 0.8), ("hud", 0.4), ("slash", 0.6)),
            false);

        Reg.Styles["crimson"] = Style("crimson", "クリムゾン・シグナル", "深紅地・白と黒の二段組み・データ破損",
            new[]
            {
                Sch(
                    ("bg", "#C8103F"), ("fg", "#FFFFFF"), ("sub", "#FFD9E2"), ("accent", "#140509"),
                    ("accent2", "#39F2C8"), ("ink", "#140509"), ("dim", "#B00D37"),
                    ("ghostA", "#FFFFFF"), ("ghostB", "#39F2C8")),
                Sch(
                    ("bg", "#FF6F98"), ("fg", "#FFFFFF"), ("sub", "#FFE3EB"), ("accent", "#1A0710"),
                    ("accent2", "#39F2C8"), ("ink", "#1A0710"), ("dim", "#F25C87"),
                    ("ghostA", "#FFFFFF"), ("ghostB", "#1A0710")),
                Sch(
                    ("bg", "#150509"), ("fg", "#FF3D6E"), ("sub", "#FF9DB6"), ("accent", "#FFFFFF"),
                    ("accent2", "#39F2C8"), ("ink", "#FF3D6E"), ("dim", "#2A0B14"),
                    ("ghostA", "#FF3D6E"), ("ghostB", "#39F2C8")),
            },
            Fonts(new[] { "gothic_black", "zenkaku" }, new[] { "mincho" },
                new[] { "gothic_med", "sansui" }, new[] { "mono" }),
            Tex(0.6, 0, 0.4), 0.8,
            Bias(
                W(("huge", 2), ("marquee", 1.6), ("scatter", 1.5), ("stack", 1.3), ("type", 1.3)),
                W(("scramble", 1.6), ("slice", 1.6), ("type", 1.3)),
                W(("glitch", 2), ("slice", 1.6))),
            W(("hud", 1), ("arrows", 0.8), ("rings", 0.8)),
            true, 1.4);

        Reg.Styles["caution"] = Style("caution", "コーション", "黄色地・赤と青のアクセント・計器UI",
            new[]
            {
                Sch(
                    ("bg", "#F4D21F"), ("fg", "#141414"), ("sub", "#3A3510"), ("accent", "#E0231C"),
                    ("accent2", "#1F3FD8"), ("ink", "#141414"), ("dim", "#E6C413"),
                    ("ghostA", "#E0231C"), ("ghostB", "#1F3FD8")),
                Sch(
                    ("bg", "#E0231C"), ("fg", "#F4D21F"), ("sub", "#FFE9A0"), ("accent", "#141414"),
                    ("accent2", "#FFFFFF"), ("ink", "#141414"), ("dim", "#C81E17"),
                    ("ghostA", "#141414"), ("ghostB", "#F4D21F")),
                Sch(
                    ("bg", "#18181A"), ("fg", "#F4D21F"), ("sub", "#DDD6B0"), ("accent", "#E0231C"),
                    ("accent2", "#FFFFFF"), ("ink", "#F4D21F"), ("dim", "#26262A"),
                    ("ghostA", "#E0231C"), ("ghostB", "#1F3FD8")),
            },
            Fonts(new[] { "mincho_black", "gothic_black" }, new[] { "mincho_black", "mincho_bold" },
                new[] { "gothic_bold" }, new[] { "mono" }),
            Tex(0.5, 0.25, 0), 0.55,
            Bias(
                W(("ring", 2.2), ("mixed", 2), ("circle", 1.4), ("gloss", 1.2)),
                W(("pop", 1.6), ("spin", 1.5), ("wipe", 1.2)),
                W(("scatter", 1.5), ("shrink", 1.2))),
            W(("hud", 1), ("rings", 1), ("arrows", 1), ("counter", 0.8), ("barcode", 0.8)),
            true);

        Reg.Styles["magenta"] = Style("magenta", "ポップ・マゼンタ", "ショッキングピンク×白・太丸ゴシック・引き出し線",
            new[]
            {
                Sch(
                    ("bg", "#FF0A8C"), ("fg", "#FFFFFF"), ("sub", "#FFD2EA"), ("accent", "#FFFFFF"),
                    ("accent2", "#2B2BD9"), ("ink", "#FFFFFF"), ("dim", "#F0077F"),
                    ("ghostA", "#FF8CC8"), ("ghostB", "#2B2BD9")),
                Sch(
                    ("bg", "#FFFFFF"), ("fg", "#FF0A8C"), ("sub", "#FF6DB6"), ("accent", "#2B2BD9"),
                    ("accent2", "#FF0A8C"), ("ink", "#FF0A8C"), ("dim", "#FFE4F2"),
                    ("ghostA", "#2B2BD9"), ("ghostB", "#FF8CC8")),
                Sch(
                    ("bg", "#2B2BD9"), ("fg", "#FFFFFF"), ("sub", "#C9C9FF"), ("accent", "#FF0A8C"),
                    ("accent2", "#FFFFFF"), ("ink", "#FFFFFF"), ("dim", "#2424C4"),
                    ("ghostA", "#FF0A8C"), ("ghostB", "#FFFFFF")),
            },
            Fonts(new[] { "round", "pop", "gothic_black" }, new[] { "mincho_bold" },
                new[] { "round", "gothic_bold" }, new[] { "mono" }),
            Tex(0.3, 0, 0), 0.35,
            Bias(
                W(("wave", 2.2), ("gloss", 1.6), ("huge", 1.6), ("pill", 1.4), ("scatter", 1.2)),
                W(("pop", 2), ("drop", 1.6), ("spin", 1.3), ("blur", 1.2)),
                W(("scatter", 1.6), ("shrink", 1.4), ("blur", 1.2))),
            W(("leaders", 1), ("counter", 1), ("sparks", 0.8), ("shapes", 0.6)),
            false);

        Reg.Styles["paper"] = Style("paper", "ペーパー・インク", "紙の質感・藍とマゼンタ・明朝の残像",
            new[]
            {
                Sch(
                    ("bg", "#ECE9E3"), ("fg", "#1B2350"), ("sub", "#4D5270"), ("accent", "#C2185B"),
                    ("accent2", "#111111"), ("ink", "#111111"), ("dim", "#DAD6CE"),
                    ("ghostA", "#C2185B"), ("ghostB", "#1B2350"), ("paper", true)),
                Sch(
                    ("bg", "#151515"), ("fg", "#F0EDE7"), ("sub", "#B8B4AC"), ("accent", "#C2185B"),
                    ("accent2", "#1B2350"), ("ink", "#F0EDE7"), ("dim", "#232323"),
                    ("ghostA", "#C2185B"), ("ghostB", "#3A4690"), ("paper", true)),
                Sch(
                    ("bg", "#C2185B"), ("fg", "#FFFFFF"), ("sub", "#F6C6D8"), ("accent", "#1B2350"),
                    ("accent2", "#111111"), ("ink", "#1B2350"), ("dim", "#B5154F"),
                    ("ghostA", "#1B2350"), ("ghostB", "#FFFFFF"), ("paper", true)),
                Sch(
                    ("bg", "#1B2350"), ("fg", "#F0EDE7"), ("sub", "#AEB2CC"), ("accent", "#C2185B"),
                    ("accent2", "#FFFFFF"), ("ink", "#F0EDE7"), ("dim", "#1F2858"),
                    ("ghostA", "#C2185B"), ("ghostB", "#FFFFFF"), ("paper", true)),
            },
            Fonts(new[] { "mincho_black", "tokumin" }, new[] { "mincho_black", "mincho_bold" },
                new[] { "mincho" }, new[] { "mono" }),
            Tex(0.7, 1, 0), 0.5,
            Bias(
                W(("stack", 2.2), ("mixed", 1.8), ("huge", 1.6), ("vcols", 1.4), ("circle", 1.2)),
                W(("wipe", 1.6), ("stretch", 1.4), ("blur", 1.2), ("slice", 1.2)),
                W(("drift", 1.6), ("wipe", 1.4))),
            W(("bars", 1), ("blobs", 0.8), ("shapes", 0.6), ("waveform", 0.4)),
            false);

        Reg.Styles["hud"] = Style("hud", "ダークHUD", "炭色地・細線フレーム・橙の差し色・日食",
            new[]
            {
                Sch(
                    ("bg", "#131315"), ("fg", "#EFEDEA"), ("sub", "#8E8B88"), ("accent", "#F25A2B"),
                    ("accent2", "#FFFFFF"), ("ink", "#EFEDEA"), ("dim", "#1E1E21"),
                    ("ghostA", "#F25A2B"), ("ghostB", "#7FD7FF")),
                Sch(
                    ("bg", "#0B0B0C"), ("fg", "#FFFFFF"), ("sub", "#9A9796"), ("accent", "#F25A2B"),
                    ("accent2", "#FFFFFF"), ("ink", "#F25A2B"), ("dim", "#18181A"),
                    ("ghostA", "#F25A2B"), ("ghostB", "#FFFFFF")),
            },
            Fonts(new[] { "gothic_black", "zenkaku" }, new[] { "mincho_bold", "mincho_light" },
                new[] { "gothic_med" }, new[] { "mono" }),
            Tex(1, 0, 0.2), 0.6,
            Bias(
                W(("circle", 2), ("ring", 1.6), ("vcols", 1.4), ("center", 1.2), ("gloss", 1)),
                W(("blur", 1.6), ("type", 1.4), ("assemble", 1.3)),
                W(("blur", 1.4), ("drift", 1.4), ("explode", 1.2))),
            W(("hud", 1), ("rings", 1), ("arrows", 1), ("grid", 0.8), ("slash", 0.6)),
            true, null, ("glow", 1.4));

        Reg.Styles["mint"] = Style("mint", "ミント・ターミナル", "黒×青緑×ライム・ラベル貼り・スリットスキャン",
            new[]
            {
                Sch(
                    ("bg", "#0A0E0D"), ("fg", "#E6FFF5"), ("sub", "#7FB9A8"), ("accent", "#9CFF3A"),
                    ("accent2", "#2E8C74"), ("ink", "#E6FFF5"), ("dim", "#142420"),
                    ("ghostA", "#FF3B6B"), ("ghostB", "#2EE6C8")),
                Sch(
                    ("bg", "#3FAE93"), ("fg", "#0A0E0D"), ("sub", "#123A31"), ("accent", "#FFFFFF"),
                    ("accent2", "#9CFF3A"), ("ink", "#0A0E0D"), ("dim", "#39A087"),
                    ("ghostA", "#FFFFFF"), ("ghostB", "#0A0E0D")),
                Sch(
                    ("bg", "#F2F2EE"), ("fg", "#0A0E0D"), ("sub", "#40504B"), ("accent", "#2E8C74"),
                    ("accent2", "#9CFF3A"), ("ink", "#0A0E0D"), ("dim", "#E2E4DE"),
                    ("ghostA", "#2E8C74"), ("ghostB", "#9CFF3A")),
            },
            Fonts(new[] { "gothic_black", "dela" }, new[] { "mincho" },
                new[] { "gothic_med", "sansui" }, new[] { "mono", "dot" }),
            Tex(0.8, 0, 0.6), 0.9,
            Bias(
                W(("labels", 2.4), ("tile", 1.6), ("marquee", 1.4), ("type", 1.4), ("diag", 1.2)),
                W(("scramble", 1.8), ("type", 1.6), ("flicker", 1.4)),
                W(("glitch", 1.6), ("slice", 1.4))),
            W(("hud", 1), ("grid", 0.8), ("barcode", 0.8), ("sparks", 0.5)),
            true);

        Reg.Styles["specimen"] = Style("specimen", "スペシメン", "墨色地・明朝・辞書の注釈と引き出し線",
            new[]
            {
                Sch(
                    ("bg", "#1B1A1C"), ("fg", "#F2F0EC"), ("sub", "#A19E99"), ("accent", "#F2F0EC"),
                    ("accent2", "#C8B98C"), ("ink", "#F2F0EC"), ("dim", "#2A292C"),
                    ("ghostA", "#6E6A66"), ("ghostB", "#C8B98C")),
                Sch(
                    ("bg", "#F2F0EC"), ("fg", "#1B1A1C"), ("sub", "#5E5B57"), ("accent", "#1B1A1C"),
                    ("accent2", "#8A7A4E"), ("ink", "#1B1A1C"), ("dim", "#E3E0DA"),
                    ("ghostA", "#B9B4AD"), ("ghostB", "#8A7A4E")),
            },
            Fonts(new[] { "mincho_bold", "mincho_black" }, new[] { "mincho", "mincho_light" },
                new[] { "mincho" }, new[] { "mono" }),
            Tex(0.6, 0.3, 0), 0.25,
            Bias(
                W(("gloss", 2.6), ("vcols", 1.8), ("mixed", 1.4), ("center", 1.2), ("tile", 1)),
                W(("type", 1.8), ("blur", 1.6), ("wipe", 1.2)),
                W(("blur", 1.6), ("drift", 1.2), ("wipe", 1.2))),
            W(("leaders", 1), ("slash", 0.8), ("rings", 0.4)),
            false);

        Reg.Styles["transit"] = Style("transit", "トランジット", "オリーブ×黄色・矢印と標識・網点",
            new[]
            {
                Sch(
                    ("bg", "#5B582B"), ("fg", "#FFFFFF"), ("sub", "#E6E2BC"), ("accent", "#E8C21A"),
                    ("accent2", "#1A1A1A"), ("ink", "#E8C21A"), ("dim", "#67633A"),
                    ("ghostA", "#E8C21A"), ("ghostB", "#1A1A1A")),
                Sch(
                    ("bg", "#1A1A1A"), ("fg", "#FFFFFF"), ("sub", "#B8B5A0"), ("accent", "#E8C21A"),
                    ("accent2", "#FFFFFF"), ("ink", "#E8C21A"), ("dim", "#242424"),
                    ("ghostA", "#E8C21A"), ("ghostB", "#7C7A55")),
                Sch(
                    ("bg", "#9C9A94"), ("fg", "#FFFFFF"), ("sub", "#F0EEE6"), ("accent", "#E8C21A"),
                    ("accent2", "#1A1A1A"), ("ink", "#1A1A1A"), ("dim", "#A6A49E"),
                    ("ghostA", "#E8C21A"), ("ghostB", "#1A1A1A")),
            },
            Fonts(new[] { "zenkaku", "gothic_black" }, new[] { "mincho_bold" },
                new[] { "gothic_bold" }, new[] { "mono" }),
            Tex(0.8, 0.2, 0), 0.5,
            Bias(
                W(("mixed", 2), ("scatter", 1.6), ("diag", 1.4), ("huge", 1.2)),
                W(("spin", 1.6), ("drop", 1.4), ("pop", 1.2), ("stretch", 1.2)),
                W(("scatter", 1.4), ("stretch", 1.4))),
            W(("arrows", 1.4), ("shapes", 1), ("counter", 0.8), ("rings", 0.6)),
            false);

        Reg.Styles["blueprint"] = Style("blueprint", "ブループリント", "鮮青×白×黒・図形コラージュ・斜め帯",
            new[]
            {
                Sch(
                    ("bg", "#1B1BE8"), ("fg", "#FFFFFF"), ("sub", "#C7C7FF"), ("accent", "#000000"),
                    ("accent2", "#FFFFFF"), ("ink", "#000000"), ("dim", "#2323F0"),
                    ("ghostA", "#000000"), ("ghostB", "#8C8CFF")),
                Sch(
                    ("bg", "#000000"), ("fg", "#FFFFFF"), ("sub", "#9A9AFF"), ("accent", "#1B1BE8"),
                    ("accent2", "#FFFFFF"), ("ink", "#1B1BE8"), ("dim", "#0A0A30"),
                    ("ghostA", "#1B1BE8"), ("ghostB", "#FFFFFF")),
                Sch(
                    ("bg", "#FFFFFF"), ("fg", "#1B1BE8"), ("sub", "#5A5AF0"), ("accent", "#000000"),
                    ("accent2", "#1B1BE8"), ("ink", "#1B1BE8"), ("dim", "#EDEDFF"),
                    ("ghostA", "#000000"), ("ghostB", "#8C8CFF")),
            },
            Fonts(new[] { "dela", "gothic_black" }, new[] { "mincho_bold" },
                new[] { "gothic_bold" }, new[] { "mono", "dot" }),
            Tex(0.4, 0, 0), 0.6,
            Bias(
                W(("diag", 2.2), ("labels", 1.4), ("huge", 1.4), ("condensed", 1.2)),
                W(("wipe", 1.6), ("slice", 1.6), ("stretch", 1.3)),
                W(("wipe", 1.6), ("slice", 1.4), ("glitch", 1.2))),
            W(("shapes", 1.4), ("stripes", 1), ("slash", 1), ("grid", 0.6)),
            false);

        Reg.Styles["rouge"] = Style("rouge", "ルージュ・グラデ", "明るいグレー地・赤のグラデーション・カプセル",
            new[]
            {
                Sch(
                    ("bg", "#E4E2E0"), ("fg", "#141414"), ("sub", "#6B6866"), ("accent", "#D40F1C"),
                    ("accent2", "#141414"), ("ink", "#141414"), ("dim", "#D8D6D4"),
                    ("ghostA", "#D40F1C"), ("ghostB", "#6B6866"),
                    ("grad", new JArray("#E3141F", "#4A0005"))),
                Sch(
                    ("bg", "#140405"), ("fg", "#FFFFFF"), ("sub", "#C98A8E"), ("accent", "#E3141F"),
                    ("accent2", "#FFFFFF"), ("ink", "#E3141F"), ("dim", "#220A0C"),
                    ("ghostA", "#E3141F"), ("ghostB", "#FFFFFF"),
                    ("grad", new JArray("#FF4A52", "#6A0008"))),
            },
            Fonts(new[] { "gothic_black", "dela" }, new[] { "mincho_black" },
                new[] { "gothic_med" }, new[] { "mono" }),
            Tex(0.4, 0, 0), 0.4,
            Bias(
                W(("huge", 2.2), ("pill", 2), ("mixed", 1.4), ("labels", 1.2), ("center", 1.2)),
                W(("zoom", 1.6), ("wipe", 1.4), ("pop", 1.2)),
                W(("shrink", 1.6), ("wipe", 1.2))),
            W(("hud", 0.8), ("leaders", 0.8), ("stripes", 0.6)),
            true, null, ("useGrad", true));

        Reg.Styles["mono"] = Style("mono", "モノ・RGB", "灰色の空間・白い明朝・強いRGB分離・座標の円",
            new[]
            {
                Sch(
                    ("bg", "#3B3D41"), ("fg", "#FFFFFF"), ("sub", "#B9BBBF"), ("accent", "#FFFFFF"),
                    ("accent2", "#FFE34D"), ("ink", "#1A1B1D"), ("dim", "#45474C"),
                    ("ghostA", "#FF2A2A"), ("ghostB", "#2AA8FF")),
                Sch(
                    ("bg", "#141517"), ("fg", "#FFFFFF"), ("sub", "#9EA0A4"), ("accent", "#FFE34D"),
                    ("accent2", "#FFFFFF"), ("ink", "#FFFFFF"), ("dim", "#1E1F22"),
                    ("ghostA", "#FF2A2A"), ("ghostB", "#2AFF7A")),
            },
            Fonts(new[] { "mincho_black", "mincho_bold" }, new[] { "mincho_bold" },
                new[] { "mincho" }, new[] { "mono" }),
            Tex(0.9, 0, 0.3), 1.3,
            Bias(
                W(("circle", 1.8), ("ring", 1.6), ("pill", 1.4), ("tile", 1.4), ("vcols", 1.3)),
                W(("assemble", 1.4), ("blur", 1.4), ("zoom", 1.3)),
                W(("explode", 1.4), ("glitch", 1.4), ("blur", 1.2))),
            W(("rings", 1.4), ("hud", 0.6), ("dots", 1)),
            false);
    }

    // ---------------- J.resolveStyle ----------------

    /// <summary>J.resolveStyle：把风格包和用户的颜色/字体覆盖合成出最终生效的 style。未知风格回退 noir。</summary>
    public static StyleDef ResolveStyle(Project project)
    {
        // 静态构造器已保证核心 12 风格注册，noir 恒存在（对齐 JS 的 J.STYLES.noir）
        var baseStyle = Reg.Styles.GetValueOrDefault(project?.Style ?? "") ?? Reg.Styles.GetValueOrDefault("noir")!;
        // JS 的 JSON.parse(JSON.stringify(base)) → 深拷贝，后续改动不触碰注册表本体
        var st = CloneStyle(baseStyle);
        if (project == null) return st;

        var ov = project.Colors ?? new JObject();
        // 底色（背景/文字）只替换第一套 scheme（pickDefined(ov, ['bg','fg','sub'])）
        if (Truthy(ov["enabled"]))
        {
            var s0 = st.Schemes[0];
            s0.Extra ??= new Dictionary<string, JToken>();
            foreach (var k in new[] { "bg", "fg", "sub" })
                if (Truthy(ov[k]))
                    s0.Extra[k] = ov[k]!.DeepClone();
        }

        // accent 和色差 ghost 色应用到每套 scheme；accent 按各自背景重新调对比度
        if (Truthy(ov["accentOn"]))
        {
            st.Schemes = st.Schemes.Select(s =>
            {
                var o = CloneScheme(s);
                var bg = s.Str("bg") ?? "#000000"; // JS 里 s.bg 恒存在；缺省按黑底兜底
                var accent = Str(ov, "accent");
                if (accent != null)
                {
                    o.Extra!["accent"] = J.FitContrast(accent, bg, 2.4);
                    if (s.Str("ink") == s.Str("accent")) o.Extra["ink"] = o.Extra["accent"];
                }

                // ghost 只要在该 scheme 的背景上看得清就行，对比度门槛放低
                var ga = Str(ov, "ghostA");
                if (ga != null) o.Extra!["ghostA"] = J.FitContrast(ga, bg, 1.35);
                var gb = Str(ov, "ghostB");
                if (gb != null) o.Extra!["ghostB"] = J.FitContrast(gb, bg, 1.35);
                if (accent != null && s.Get("grad") != null)
                    o.Extra!["grad"] = new JArray(J.FitContrast(accent, bg, 2.4), J.Mix(accent, "#000000", 0.7));
                return o;
            }).ToList();
        }

        var fo = project.Fonts ?? new Dictionary<string, string>();
        foreach (var role in new[] { "display", "serif", "body" })
            // J.FONTS[fo[role]] 存在性判定（用户字体覆盖是否命中目录 → JFonts.Table，
        // 运行期用户自定义字体 JFonts.AddUserFont 注册后同样命中，对齐 JS）
        if (fo.TryGetValue(role, out var fk) && !string.IsNullOrEmpty(fk) && JFonts.Has(fk))
                if (st.Extra != null && st.Extra.TryGetValue("fonts", out var ft) && ft is JObject fonts)
                    fonts[role] = new JArray(fk);

        if (KeyMode(project) != null) KeyStyle(st);
        return st;
    }

    // ---------------- J.keyMode / keyStyle ----------------

    /// <summary>J.keyMode：'green' | 'black' | null。</summary>
    public static string? KeyMode(Project? project)
        => project != null && KeyBg.ContainsKey(project.KeyBg ?? "") ? project.KeyBg : null;

    /// <summary>J.keyStyle：合成用背景（绿幕/黑幕）：每套 scheme 都改成黑底白字、质感归零，渲染器最后把整帧
    /// 去色，green 再 screen 上纯绿。黑始终代表"空"，键控（green）和 screen/明度混合（black）等价。</summary>
    private static void KeyStyle(StyleDef st)
    {
        st.Schemes = st.Schemes.Select(s =>
        {
            var o = new Scheme
            {
                Extra = new Dictionary<string, JToken>
                {
                    ["bg"] = "#000000",
                    ["fg"] = "#FFFFFF",
                    ["sub"] = "#D2D2D2",
                    ["accent"] = "#FFFFFF",
                    ["accent2"] = "#BDBDBD",
                    ["ink"] = "#FFFFFF",
                    ["dim"] = "#1E1E1E",
                    ["ghostA"] = "#9A9A9A",
                    ["ghostB"] = "#5E5E5E"
                }
            };
            if (s.Get("grad") != null) o.Extra!["grad"] = new JArray("#FFFFFF", "#A8A8A8");
            return o;
        }).ToList();
        if (st.Extra == null) st.Extra = new Dictionary<string, JToken>();
        st.Extra["texture"] = new JObject { ["grain"] = 0, ["paper"] = 0, ["scan"] = 0 };
        st.Extra["key"] = true;
    }

    // ---------------- 构造/克隆助手 ----------------

    private static StyleDef Style(string key, string name, string desc, Scheme[] schemes,
        JObject fonts, JObject texture, double ghost, JObject bias, JObject decor, bool hud,
        double? glitchBoost = null, params (string K, object V)[] extra)
    {
        var st = new StyleDef
        {
            Key = key,
            Name = name,
            Hud = hud,
            GlitchBoost = glitchBoost ?? 1,
            Bias = bias,
            Decor = decor,
            Schemes = schemes.ToList(),
            Extra = new Dictionary<string, JToken>
            {
                ["desc"] = desc,
                ["fonts"] = fonts,
                ["texture"] = texture,
                ["ghost"] = ToTok(ghost)
            }
        };
        for (var i = 0; i < extra.Length; i += 2) st.Extra[extra[i].K] = ToTok(extra[i].V);
        return st;
    }

    private static StyleDef CloneStyle(StyleDef s) => new()
    {
        Key = s.Key,
        Name = s.Name,
        Tags = (string[])s.Tags.Clone(),
        Hud = s.Hud,
        GlitchBoost = s.GlitchBoost,
        Bias = (JObject)s.Bias.DeepClone(),
        Decor = (JObject)s.Decor.DeepClone(),
        Schemes = s.Schemes.Select(CloneScheme).ToList(),
        Extra = s.Extra == null ? null : s.Extra.ToDictionary(e => e.Key, e => e.Value.DeepClone())
    };

    private static Scheme CloneScheme(Scheme s) => new()
    {
        Extra = s.Extra == null ? null : s.Extra.ToDictionary(e => e.Key, e => e.Value.DeepClone())
    };

    private static Scheme Sch(params (string K, object V)[] kv)
    {
        var s = new Scheme { Extra = new Dictionary<string, JToken>() };
        foreach (var (k, v) in kv) s.Extra[k] = ToTok(v);
        return s;
    }

    private static JObject Fonts(string[] display, string[] serif, string[] body, string[] mono) => new()
    {
        ["display"] = new JArray(display),
        ["serif"] = new JArray(serif),
        ["body"] = new JArray(body),
        ["mono"] = new JArray(mono)
    };

    private static JObject Tex(object grain, object paper, object scan) => new()
    {
        ["grain"] = ToTok(grain),
        ["paper"] = ToTok(paper),
        ["scan"] = ToTok(scan)
    };

    private static JObject Bias(JObject layout, JObject enter, JObject exit) => new()
    {
        ["layout"] = layout,
        ["enter"] = enter,
        ["exit"] = exit
    };

    private static JObject W(params (string K, object V)[] kv)
    {
        var o = new JObject();
        foreach (var (k, v) in kv) o[k] = ToTok(v);
        return o;
    }

    // JS 字面量是整数处保持整数（JSON 往返对齐 JSON.stringify：1 而非 1.0）
    private static JToken ToTok(object v) => v switch
    {
        JToken t => t,
        string s => new JValue(s),
        bool b => new JValue(b),
        int i => new JValue(i),
        long l => new JValue(l),
        double d => Math.Floor(d) == d && !double.IsInfinity(d) ? new JValue((long)d) : new JValue(d),
        _ => JToken.FromObject(v)
    };

    // JS 真值判定（ov.enabled / ov.accentOn / pickDefined 的 if (o[k]) 语义）
    private static bool Truthy(JToken? t) => t switch
    {
        null or JValue { Value: null } => false,
        JValue { Value: string s } => s.Length > 0,
        JValue { Value: bool b } => b,
        JValue { Value: long l } => l != 0,
        JValue { Value: double d } => d != 0 && !double.IsNaN(d),
        _ => true
    };

    private static string? Str(JObject o, string k)
        => o[k] is JValue { Type: JTokenType.String } v ? (string?)v.Value : null;
}
