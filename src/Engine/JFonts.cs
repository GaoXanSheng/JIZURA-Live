using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace JizuraLive.Engine;

// 移植自 frontend/src/engine/02_fonts.js
//
// 字体目录 + 字体加载/用户字体。目录数据（J.FONTS → Table）与纯逻辑（safeFamily/SAFE_FONT_KEY/
// fontCSS/fontsOfPlan）逐行对齐 JS；浏览器专属部分的处理：
//   - attachFamily/cssJobs（往 head 挂 <link>，本地缺回退 Google CDN）→ AttachFamily 查本地
//     fonts 目录（frontend/public/fonts/css/<CssFileOf(spec)>.css），解析分片留 TODO(M2)；
//   - document.fonts.load(spec, uniq)（按 unicode-range 子集加载）→ EnsureTypeface 用
//     SKTypeface.FromFamilyName/FromFile 兜底，TODO(M2) 字体管线定型；
//   - saveFontData/loadFontData（IndexedDB，刷新后用户字体还在）→ SaveFontData/RestoreUserFonts
//     先抛 NotImplementedException，TODO(M3) 接 UI 时落 %LOCALAPPDATA%。
// metrics（canvas measureText）/GlyphCache（canvas 光栅连通域）/fragments（多边形碎裂）虽在
// 本 JS 文件，但 C# 侧按 README 对位表归 03_text（JText.cs 矢量路径版）；清缓存经由
// JLang.Glyphs/JLang.Metrics 登记（null 时跳过，对齐 JS 的 if (J.glyphs)）。
public static class JFonts
{
    // ---------------- J.FONTS 字体目录 ----------------

    // JS 模板串常量：日文字体的 CSS fallback 链（数据契约，逐字一致）
    public const string JpSansFb = "\"Noto Sans JP\",\"Noto Sans CJK JP\",\"Hiragino Sans\",\"Yu Gothic\",\"Meiryo\",sans-serif";
    public const string JpSerifFb = "\"Noto Serif JP\",\"Noto Serif CJK JP\",\"Hiragino Mincho ProN\",\"Yu Mincho\",serif";

    /// <summary>J.FONTS：key → {label, family, weight, kind, fb, gf}（与 02b_lang 的 FontFace POCO 同形）。</summary>
    public static readonly Dictionary<string, FontFace> Table = new()
    {
        ["gothic_black"] = new FontFace
        {
            Label = "Noto Sans JP Black",
            Family = "\"Noto Sans JP\"",
            Weight = 900,
            Kind = "gothic",
            Fb = JpSansFb,
            Gf = "Noto+Sans+JP:wght@300;500;700;900"
        },
        ["gothic_bold"] = new FontFace
        {
            Label = "Noto Sans JP Bold",
            Family = "\"Noto Sans JP\"",
            Weight = 700,
            Kind = "gothic",
            Fb = JpSansFb,
            Gf = "Noto+Sans+JP:wght@300;500;700;900"
        },
        ["gothic_med"] = new FontFace
        {
            Label = "Noto Sans JP Medium",
            Family = "\"Noto Sans JP\"",
            Weight = 500,
            Kind = "gothic",
            Fb = JpSansFb,
            Gf = "Noto+Sans+JP:wght@300;500;700;900"
        },
        ["gothic_light"] = new FontFace
        {
            Label = "Noto Sans JP Light",
            Family = "\"Noto Sans JP\"",
            Weight = 300,
            Kind = "gothic",
            Fb = JpSansFb,
            Gf = "Noto+Sans+JP:wght@300;500;700;900"
        },
        ["dela"] = new FontFace
        {
            Label = "Dela Gothic One",
            Family = "\"Dela Gothic One\"",
            Weight = 400,
            Kind = "display",
            Fb = JpSansFb,
            Gf = "Dela+Gothic+One"
        },
        ["zenkaku"] = new FontFace
        {
            Label = "Zen Kaku Gothic New Black",
            Family = "\"Zen Kaku Gothic New\"",
            Weight = 900,
            Kind = "gothic",
            Fb = JpSansFb,
            Gf = "Zen+Kaku+Gothic+New:wght@900"
        },
        ["mincho_black"] = new FontFace
        {
            Label = "Zen Old Mincho Black",
            Family = "\"Zen Old Mincho\"",
            Weight = 900,
            Kind = "mincho",
            Fb = JpSerifFb,
            Gf = "Zen+Old+Mincho:wght@900"
        },
        ["mincho_bold"] = new FontFace
        {
            Label = "Noto Serif JP Bold",
            Family = "\"Noto Serif JP\"",
            Weight = 700,
            Kind = "mincho",
            Fb = JpSerifFb,
            Gf = "Noto+Serif+JP:wght@300;500;700"
        },
        ["mincho"] = new FontFace
        {
            Label = "Noto Serif JP Medium",
            Family = "\"Noto Serif JP\"",
            Weight = 500,
            Kind = "mincho",
            Fb = JpSerifFb,
            Gf = "Noto+Serif+JP:wght@300;500;700"
        },
        ["mincho_light"] = new FontFace
        {
            Label = "Noto Serif JP Light",
            Family = "\"Noto Serif JP\"",
            Weight = 300,
            Kind = "mincho",
            Fb = JpSerifFb,
            Gf = "Noto+Serif+JP:wght@300;500;700"
        },
        ["tokumin"] = new FontFace
        {
            Label = "Kaisei Tokumin",
            Family = "\"Kaisei Tokumin\"",
            Weight = 800,
            Kind = "mincho",
            Fb = JpSerifFb,
            Gf = "Kaisei+Tokumin:wght@800"
        },
        ["round"] = new FontFace
        {
            Label = "M PLUS Rounded 1c",
            Family = "\"M PLUS Rounded 1c\"",
            Weight = 800,
            Kind = "round",
            Fb = JpSansFb,
            Gf = "M+PLUS+Rounded+1c:wght@800"
        },
        ["pop"] = new FontFace
        {
            Label = "Mochiy Pop One",
            Family = "\"Mochiy Pop One\"",
            Weight = 400,
            Kind = "display",
            Fb = JpSansFb,
            Gf = "Mochiy+Pop+One"
        },
        ["dot"] = new FontFace
        {
            Label = "DotGothic16",
            Family = "\"DotGothic16\"",
            Weight = 400,
            Kind = "pixel",
            Fb = JpSansFb,
            Gf = "DotGothic16"
        },
        ["brush"] = new FontFace
        {
            Label = "Yuji Syuku",
            Family = "\"Yuji Syuku\"",
            Weight = 400,
            Kind = "brush",
            Fb = JpSerifFb,
            Gf = "Yuji+Syuku"
        },
        ["mono"] = new FontFace
        {
            Label = "IBM Plex Mono",
            Family = "\"IBM Plex Mono\"",
            Weight = 500,
            Kind = "mono",
            Fb = "\"IBM Plex Sans JP\"," + JpSansFb,
            Gf = "IBM+Plex+Mono:wght@500;600"
        },
        ["reggae"] = new FontFace
        {
            Label = "Reggae One",
            Family = "\"Reggae One\"",
            Weight = 400,
            Kind = "display",
            Fb = JpSansFb,
            Gf = "Reggae+One"
        },
        ["rampart"] = new FontFace
        {
            Label = "Rampart One",
            Family = "\"Rampart One\"",
            Weight = 400,
            Kind = "display",
            Fb = JpSansFb,
            Gf = "Rampart+One"
        },
        ["potta"] = new FontFace
        {
            Label = "Potta One", Family = "\"Potta One\"", Weight = 400, Kind = "brush", Fb = JpSansFb, Gf = "Potta+One"
        },
        ["kiwi"] = new FontFace
        {
            Label = "Kiwi Maru",
            Family = "\"Kiwi Maru\"",
            Weight = 500,
            Kind = "round",
            Fb = JpSansFb,
            Gf = "Kiwi+Maru:wght@500"
        },
        ["klee"] = new FontFace
        {
            Label = "Klee One",
            Family = "\"Klee One\"",
            Weight = 600,
            Kind = "hand",
            Fb = JpSerifFb,
            Gf = "Klee+One:wght@600"
        },
        ["shippori"] = new FontFace
        {
            Label = "Shippori Mincho B1",
            Family = "\"Shippori Mincho B1\"",
            Weight = 800,
            Kind = "mincho",
            Fb = JpSerifFb,
            Gf = "Shippori+Mincho+B1:wght@800"
        },
        ["sansui"] = new FontFace
        {
            Label = "IBM Plex Sans JP",
            Family = "\"IBM Plex Sans JP\"",
            Weight = 500,
            Kind = "gothic",
            Fb = JpSansFb,
            Gf = "IBM+Plex+Sans+JP:wght@400;500;700"
        },
    };

    /// <summary>02_fonts.js 的 IIFE 载入即建目录；显式公开供 EngineBoot 按 index.ts 导入序调用
    /// （02b_lang 之后、04_styles 之前）。幂等。</summary>
    public static void Install() => _ = Table; // 静态字段初始化即建目录

    public static bool Has(string key) => Table.ContainsKey(key);

    // ---------------- 用户字体（运行时注册） ----------------

    /// <summary>J.addUserFont：登记用户字体到目录（本地 family 名或上传的文件）。
    /// C# 只做目录登记 + 标记 User；SKTypeface 的实际加载由 LoadFontFile 做，TODO(M3) 接 UI。</summary>
    public static void AddUserFont(string key, string label, string family, int weight = 400, string kind = "custom")
    {
        Table[key] = new FontFace
        {
            Label = label,
            Family = "\"" + family.Replace("\"", "") + "\"",
            Weight = weight,
            Kind = kind,
            Fb = JpSansFb,
            User = true
        };
        JLang.Glyphs?.Clear();
        JText.OnFontsChanged(); // SKFont 缓存按 fontKey 键控，字体重登记后须失效
    }

    /// <summary>J.SAFE_FONT_KEY：页面只接受纯 key（project 文件是不可信输入）。</summary>
    public static readonly Regex SafeFontKey = new("^user_[A-Za-z0-9_-]{1,80}$", RegexOptions.Compiled);

    // JS 的 \w 是 [A-Za-z0-9_]；.NET 的 \w 吃 Unicode 字母，必须 ECMAScript 语义
    private static readonly Regex JsSafeFamilyChars = new("[^\\w\\- ]", RegexOptions.ECMAScript | RegexOptions.Compiled);
    private static readonly Regex JsNonWord = new("[^\\w]", RegexOptions.ECMAScript | RegexOptions.Compiled);
    private static readonly Regex JsCssNameChars = new("[^\\w.-]+", RegexOptions.ECMAScript | RegexOptions.Compiled);

    /// <summary>J.safeFamily：family 名消毒（只留 \w 空格连字符），截 80 字。</summary>
    public static string SafeFamily(string? s)
    {
        var r = JsSafeFamilyChars.Replace(s ?? "", "_");
        return r.Length > 80 ? r[..80] : r;
    }

    /// <summary>J.loadFontFile 返回 {key, label, family, weight}。</summary>
    public sealed record UserFontRef(string Key, string Label, string Family, int Weight);

    /// <summary>已加载的用户字体 typeface（family 不带引号 → tf）。03_text 渲染时优先取。</summary>
    private static readonly Dictionary<string, SKTypeface> UserFaces = new();

    /// <summary>
    /// J.loadFontFile：加载上传的字体文件。JS 收 File/Blob + FontFace；C# 收文件路径，
    /// SKTypeface.FromFile 即真正加载（失败抛错，对齐 FontFace.load() 会 reject）。
    /// </summary>
    public static UserFontRef LoadFontFile(string path)
    {
        var name = Path.GetFileName(path ?? "");
        var stem = StemOf(name);
        // fam = 'UF_' + stem.replace(/[^\w]/g,'_').slice(0,60)（JS 的 slice 只作用于 replace 结果）
        var safe = JsNonWord.Replace(stem, "_");
        var fam = "UF_" + (safe.Length > 60 ? safe[..60] : safe);
        var tf = SKTypeface.FromFile(path);
        if (tf == null) throw new InvalidOperationException("字体加载失败: " + name); // 对齐 FontFace.load() 失败
        UserFaces[fam] = tf;
        var label = stem.Length > 80 ? stem[..80] : stem;
        var key = "user_" + fam;
        AddUserFont(key, label, fam, 400, "custom");
        Table[key].Loaded = true;
        // JS: if (J.saveFontData) J.saveFontData(key, buf) —— 存进浏览器刷新后字体还在。
        // TODO(M3): C# 落 %LOCALAPPDATA%（见 SaveFontData），接字体上传 UI 时一并做。
        return new UserFontRef(key, label, fam, 400);
    }

    /// <summary>J.restoreUserFonts：项目用到的上传字体从存档注册；返回拿不到的 label 列表。
    /// JS 从 IndexedDB 读回字节 → FontFace；C# TODO(M3) 从 %LOCALAPPDATA% 读回。</summary>
    public static List<string> RestoreUserFonts(IEnumerable<UserFontRef>? list) =>
        throw new NotImplementedException("TODO(M3): 用户字体持久化（IndexedDB → %LOCALAPPDATA%），接字体上传 UI 时实现");

    /// <summary>saveFontData/loadFontData（IndexedDB 字节存取）。TODO(M3)。</summary>
    public static byte[] SaveFontData(string key, byte[] buf) =>
        throw new NotImplementedException("TODO(M3): 用户字体持久化（IndexedDB → %LOCALAPPDATA%），接字体上传 UI 时实现");

    /// <summary>J.missingUserFonts：plan 要画、但本页面没有的上传字体。</summary>
    public static List<string> MissingUserFonts(IEnumerable<string>? keys) =>
        (keys ?? Enumerable.Empty<string>())
        .Where(k => Table.TryGetValue(k, out var f) && f.User && !f.Loaded)
        .Select(k => Table[k].Label)
        .ToList();

    // ---------------- fontCSS ----------------

    /// <summary>J.fontCSS：`{weight} {px}px {family},{fb}`（faceOf 按语言选脸，未知 key 兜底 gothic_bold）。</summary>
    public static string FontCss(string key, double px)
    {
        var f = JLang.FaceOf(key);
        var v = px == 0 ? 0 : px; // 规范 -0（JS toFixed 不带负号）
        return $"{f.Weight} {v.ToString("F2", CultureInfo.InvariantCulture)}px {f.Family},{f.Fb}";
    }

    // ---------------- 字体加载（浏览器专属部分的 C# 对应物） ----------------

    // 本地文件名按 fetch-fonts.mjs 的 safe 规则生成（spec.replace(/[^\w.-]+/g, '_')），
    // 请求必须用同一规则（JS L246 注释：encodeURIComponent 会把 + : @ ; 编成 %xx 而全部 404）。
    // 例: 'Noto+Sans+JP:wght@100..900' → 'Noto_Sans_JP_wght_100..900.css'
    internal static string CssFileOf(string spec) => JsCssNameChars.Replace(spec, "_") + ".css";

    /// <summary>本地自托管字体根目录（scripts/fetch-fonts.mjs 抓到 frontend/public/fonts/，
    /// 含 css/ 与 files/）。发布态 TODO(M2) 由打包管线拷贝，先找仓库内目录。</summary>
    internal static string? FontsRoot()
    {
        foreach (var dir in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "fonts"),
                     Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "frontend", "public", "fonts")
                 })
        {
            var full = Path.GetFullPath(dir);
            if (Directory.Exists(full)) return full;
        }
        return null;
    }

    // cssJobs：一次一个 family，只挂 plan 真正用到的脸；同 spec 只处理一次
    private static readonly HashSet<string> CssJobs = new();

    /// <summary>attachFamily：JS 往 head 挂 &lt;link&gt;（本地 /fonts/css/*.css，缺则回退 Google CDN）。
    /// C# 没有样式表：先按同一命名规则确认本地分片 css 在不在；解析 @font-face/unicode-range
    /// 并按字符集挑分片文件 TODO(M2) 字体管线定型。解析结果经 EnsureTypeface 缓存。</summary>
    private static void AttachFamily(string spec)
    {
        if (string.IsNullOrEmpty(spec) || !CssJobs.Add(spec)) return;
        var root = FontsRoot();
        if (root != null && File.Exists(Path.Combine(root, "css", CssFileOf(spec))))
            return; // TODO(M2): 解析分片，把 uniq 覆盖的分片 woff2 交给 SKTypeface
        // 本地没抓过这份字体：JS 回退 Google CDN；C# 不做网络加载，走系统字体兜底（EnsureTypeface）
    }

    // document.fonts.load(spec, uniq) 的 C# 对应物："(weight) 64px (family)" → SKTypeface，同键只解析一次。
    // null = 没找到（对齐 JS 的 .catch(() => null)，渲染层自行兜底）。
    private static readonly Dictionary<string, SKTypeface?> FaceJobs = new();

    private static SKTypeface? EnsureTypeface(string familyQuoted, int weight)
    {
        var k = weight + "|" + familyQuoted;
        if (FaceJobs.TryGetValue(k, out var cached)) return cached;
        var fam = familyQuoted.Trim('"');
        SKTypeface? r = null;
        if (UserFaces.TryGetValue(fam, out var uf)) r = uf; // 上传字体优先
        else
        {
            try
            {
                var t = SKTypeface.FromFamilyName(fam, (SKFontStyleWeight)weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
                // FromFamilyName 缺字体时返回默认体；family 对不上视为没找到
                if (t != null && string.Equals(t.FamilyName, fam, StringComparison.OrdinalIgnoreCase)) r = t;
            }
            catch
            {
                // 没有就 null（对齐 JS document.fonts.load(...).catch(() => null)）
            }
        }
        FaceJobs[k] = r;
        return r;
    }

    /// <summary>J.ensureFonts：确保要画的字形已加载。keys = null → 目录全部。</summary>
    public static async Task EnsureFonts(string? text, IEnumerable<string>? keys = null)
    {
        var uniq = UniqueCodePoints(text ?? "");
        if (uniq.Length == 0) uniq = "あ";
        var list = (keys ?? (IEnumerable<string>)Table.Keys).Where(Has).ToList();
        if (keys != null && keys.Contains("@var"))
        {
            // 太さ：可变的 Noto Sans / Serif JP
            AttachFamily("Noto+Sans+JP:wght@100..900");
            AttachFamily("Noto+Serif+JP:wght@200..900");
            foreach (var (family, weight) in new[]
                     {
                         ("\"Noto Sans JP\"", 100), ("\"Noto Sans JP\"", 900),
                         ("\"Noto Serif JP\"", 200), ("\"Noto Serif JP\"", 900)
                     })
                _ = EnsureTypeface(family, weight);
        }

        // 当前歌词语言的脸（含它的 fallback sans/serif），各自按实际绘制的字重加载
        var faces = new List<FontFace>();
        foreach (var k in list) faces.Add(JLang.FaceOf(k));
        foreach (var b in JLang.LangBaseFaces(list))
            faces.Add(new FontFace { Family = "\"" + b.Family + "\"", Weight = b.Weight, Gf = b.Gf });

        foreach (var gf in faces.Select(f => f.Gf).Where(g => !string.IsNullOrEmpty(g)).Distinct().ToList())
            AttachFamily(gf);
        var seen = new HashSet<string>();
        foreach (var f in faces)
        {
            var spec = $"{f.Weight} 64px {f.Family}";
            if (!seen.Add(spec)) continue;
            _ = EnsureTypeface(f.Family, f.Weight);
        }

        // JS 末尾还 await document.fonts.ready —— C# 加载是同步完成的，没有对应物
        JLang.Glyphs?.Clear();
        JLang.Metrics?.Clear();
        JText.OnFontsChanged();
        await Task.CompletedTask;
    }

    /// <summary>[...new Set([...String(text)])].join('')：按码点去重（含增补平面汉字），保持首现顺序。</summary>
    private static string UniqueCodePoints(string s)
    {
        var seen = new HashSet<int>();
        var sb = new StringBuilder(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            if (i + 1 < s.Length && char.IsHighSurrogate(s[i]) && char.IsLowSurrogate(s[i + 1]))
            {
                if (seen.Add(char.ConvertToUtf32(s, i))) sb.Append(s, i, 2);
                i += 2;
            }
            else
            {
                if (seen.Add(s[i])) sb.Append(s[i]);
                i++;
            }
        }
        return sb.ToString();
    }

    /// <summary>JS file.name.replace(/\.[^.]+$/, '')：去最后一个扩展名（点后必须有非点字符）。</summary>
    private static string StemOf(string name)
    {
        var i = name.LastIndexOf('.');
        return i >= 0 && i < name.Length - 1 && !name[(i + 1)..].Contains('.') ? name[..i] : name;
    }

    // ---------------- fontsOfPlan ----------------

    /// <summary>J.fontsOfPlan：plan 会用到的字体 key——风格角色、各 cut 的字体参数、HUD 的 mono；
    /// 有 weightGrow 的切分再补 '@var'（太さ可变的 Noto Sans / Serif JP）。</summary>
    public static List<string> FontsOfPlan(Plan? plan)
    {
        // Set 保插入序：mono → 风格角色 → 各 cut 参数
        var set = new List<string>();
        var seen = new HashSet<string>();
        void Add(string k)
        {
            if (seen.Add(k)) set.Add(k);
        }
        Add("mono");
        if (plan == null) return set;

        var fonts = JLayouts.StTok(plan.Style, "fonts") as JObject; // plan.style.fonts || {}
        if (fonts != null)
            foreach (var p in fonts.Properties())
                if (p.Value is JArray arr)
                    foreach (var t in arr)
                        if (t.Type == JTokenType.String)
                            Add((string)t!);

        foreach (var c in plan.Cuts)
        {
            foreach (var p in c.Params.Properties())
            {
                if (p.Value is JValue { Type: JTokenType.String } sv)
                {
                    var v = (string)sv.Value!;
                    if (Has(v)) Add(v);
                }
                else if (p.Value is JArray a)
                {
                    foreach (var t in a)
                        if (t.Type == JTokenType.String && Has((string)t!))
                            Add((string)t!);
                }
            }
        }

        var outList = set.Where(Has).ToList();
        if (plan.Cuts.Any(c => c.WeightGrow)) outList.Add("@var"); // 太さ：可变的 Noto Sans / Serif JP
        return outList;
    }
}
