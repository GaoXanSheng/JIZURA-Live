using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using JizuraLive.App; // EngineRenderGate：字体重扫的 Dispose 必须与渲染串行（JFontFetch/JText 同款引用）
using JizuraLive.Logging;
using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace JizuraLive.Engine;

// 移植自 frontend/src/engine/02_fonts.js
//
// 字体目录 + 字体解析/用户字体。目录数据（J.FONTS → Table）与纯逻辑（safeFamily/SAFE_FONT_KEY/
// fontCSS/fontsOfPlan）逐行对齐 JS；浏览器专属部分（attachFamily 挂 <link>、document.fonts.load
// 按 unicode-range 分片、IndexedDB 存用户字体）在 C# 侧按「字体目录 + 系统字体栈」重做：
//   - attachFamily/cssJobs → 字体目录扫描（exe 旁 fonts\ 与 %LOCALAPPDATA%\JizuraLive\fonts）；
//   - document.fonts.load → ResolveChain（用户字体 → 目录 → 系统族 → 语言×气质栈 → 逐字兜底）；
//   - Google Fonts CDN → FontFetch：按需拉完整 TTF 进用户字体库（离线自托管，jsDelivr 镜像）；
//   - saveFontData/loadFontData → SaveFontData/RestoreUserFonts（用户字体库落盘）。
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
    /// C# 只做目录登记 + 标记 User；SKTypeface 的实际加载由 LoadFontFile 做（设置页字体导入已接）。</summary>
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
        InvalidateResolveCache(); // 字体重登记（新增/换名）：解析与字形缓存全失效
    }

    /// <summary>J.SAFE_FONT_KEY：页面只接受纯 key（project 文件是不可信输入）。</summary>
    public static readonly Regex SafeFontKey = new("^user_[A-Za-z0-9_-]{1,80}$", RegexOptions.Compiled);

    // JS 的 \w 是 [A-Za-z0-9_]；.NET 的 \w 吃 Unicode 字母，必须 ECMAScript 语义
    private static readonly Regex JsSafeFamilyChars = new("[^\\w\\- ]", RegexOptions.ECMAScript | RegexOptions.Compiled);
    private static readonly Regex JsNonWord = new("[^\\w]", RegexOptions.ECMAScript | RegexOptions.Compiled);

    /// <summary>J.safeFamily：family 名消毒（只留 \w 空格连字符），截 80 字。</summary>
    public static string SafeFamily(string? s)
    {
        var r = JsSafeFamilyChars.Replace(s ?? "", "_");
        return r.Length > 80 ? r[..80] : r;
    }

    /// <summary>J.loadFontFile 返回 {key, label, family, weight}。</summary>
    public sealed record UserFontRef(string Key, string Label, string Family, int Weight);

    /// <summary>用户字体是否已加载（02_fonts.js 运行时置位）。</summary>
    public static bool IsLoaded(string key) => Table.TryGetValue(key, out var f) && f.Loaded;

    /// <summary>已加载的用户字体 typeface（family 不带引号 → tf）。03_text 渲染时优先取。</summary>
    private static readonly Dictionary<string, SKTypeface> UserFaces = new();

    /// <summary>用户字体库目录（旧前端的 IndexedDB 对应物）：上传的字体本体落这里，重启/换工程还能恢复。
    /// 可写属性：App 侧启动时按 AppDirs 口径覆盖（引擎不依赖 App 层）。</summary>
    public static string UserFontDir { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JizuraLive", "fonts");

    private static readonly object UserFontLock = new();

    /// <summary>
    /// J.loadFontFile：加载上传的字体文件。JS 收 File/Blob + FontFace；C# 收文件路径，
    /// SKTypeface.FromFile 即真正加载（失败抛错，对齐 FontFace.load() 会 reject）。
    /// 同时把字节存进用户字体库（SaveFontData），刷新/重启后由 RestoreUserFonts 读回。
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
        lock (UserFontLock)
        {
            UserFaces[fam] = tf;
            var label = stem.Length > 80 ? stem[..80] : stem;
            var key = "user_" + fam;
            AddUserFont(key, label, fam, 400, "custom");
            Table[key].Loaded = true;
            SaveFontData(key, File.ReadAllBytes(path)); // 对齐 J.saveFontData：Refresh 后字体还在
            return new UserFontRef(key, label, fam, 400);
        }
    }

    /// <summary>J.restoreUserFonts：工程用到的上传字体从用户字体库读回并登记；返回拿不到的 label 列表。
    /// 分两类：有本体文件的（上传字体）从 UserFontDir 读；没有本体的是「本机字体」（按名字用系统已装的族），
    /// 只需重新登记目录项，但本机没装这个名字时同样算缺。</summary>
    public static List<string> RestoreUserFonts(IEnumerable<UserFontRef>? list)
    {
        var missing = new List<string>();
        foreach (var u in list ?? Enumerable.Empty<UserFontRef>())
        {
            if (Table.TryGetValue(u.Key, out var f) && f.Loaded) continue;
            var fam = SafeFamily(string.IsNullOrEmpty(u.Family) ? u.Key : u.Family);
            var path = Path.Combine(UserFontDir, u.Key + ".font");
            try
            {
                if (File.Exists(path))
                {
                    var tf = SKTypeface.FromFile(path);
                    if (tf == null) throw new InvalidOperationException("字体本体读不出: " + path);
                    lock (UserFontLock)
                    {
                        UserFaces[fam] = tf;
                        RegisterUserFace(u.Key, u.Label, fam, u.Weight, loaded: true);
                    }
                    continue;
                }

                // 本机字体：族名系统里查得到才算有
                if (IsFamilyInstalled(fam))
                {
                    RegisterUserFace(u.Key, u.Label, fam, u.Weight, loaded: true);
                    continue;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"JIZURA: 用户字体恢复失败 {u.Label}（{u.Key}）：{ex.Message}");
            }

            RegisterUserFace(u.Key, u.Label, fam, u.Weight, loaded: false); // 登记成「缺」以便 UI 提示与回退
            missing.Add(string.IsNullOrEmpty(u.Label) ? u.Key : u.Label);
        }
        return missing;
    }

    /// <summary>登记一条用户字体目录项（family 用存下来的原名，不从文件名反推）。</summary>
    private static void RegisterUserFace(string key, string label, string family, int weight, bool loaded)
    {
        var k = SafeFontKey.IsMatch(key) ? key : "user_" + family;
        Table[k] = new FontFace
        {
            Label = string.IsNullOrEmpty(label) ? family : label,
            Family = "\"" + family + "\"",
            Weight = Math.Clamp(weight, 100, 900),
            Kind = "custom",
            Fb = JpSansFb,
            User = true,
            Loaded = loaded
        };
        InvalidateResolveCache();
    }

    /// <summary>saveFontData（IndexedDB 存字节 → 用户字体库落盘）。单个 key 一个文件，Skia 按内容认格式。</summary>
    public static byte[] SaveFontData(string key, byte[] buf)
    {
        try
        {
            Directory.CreateDirectory(UserFontDir);
            var path = Path.Combine(UserFontDir, key + ".font");
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, buf);
            File.Move(tmp, path, overwrite: true); // 原子替换：写一半崩了也不会留半截字体
        }
        catch (Exception ex)
        {
            Log.Warn("JIZURA: 用户字体落盘失败：" + ex.Message);
        }
        return buf;
    }

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

    // ---------------- 字体解析（客户端字体管线） ----------------
    //
    // 浏览器里的 attachFamily/document.fonts.load 在 C# 侧对应「字体目录 + 系统字体栈」：
    //   1) 用户字体：上传脸（UserFaces）、工程里的本机字体名
    //   2) 字体目录：exe 旁 fonts\（随包自托管，也是用户手动投放字体的地方）与用户字体库
    //      %LOCALAPPDATA%\JizuraLive\fonts（网络拉取的缓存 + 上传的字体本体），启动时扫描入索引
    //   3) 系统已装 family：精确同名才算命中（FromFamilyName 缺字体时返回默认体，须比对族名）
    //   4) 语言 × 气质的系统字体栈（StackFor）：离线机器上没有 webfont 时按 kind 落到本机有的
    //      黑体/明朝/圆体/像素/书法/等宽脸，保证各字体键观感有别，而不是所有键一张脸
    //   5) 逐字兜底（CoverFace）：主脸缺字时按字找能画的脸（CJK 永不 .notdef）
    //
    // 网络拉取（原前端 fetch-fonts.mjs 离线自托管的 C# 对应物）：FontFetch 按需把目录里的家族
    // 从 Google Fonts 仓库镜像（jsDelivr→raw.githubusercontent）拉成完整 TTF 存进用户字体库，
    // 拉到后热更新字形缓存。都不命中时用系统微软雅黑收尾（中文 Windows 必然有）——SKTypeface.Default 在 CJK 上只画
    // .notdef 方块，且 SkiaSharp 3.119 下有原生崩（AV）记录（见 git 历史/PreviewEngine 旧注释），不再使用。

    /// <summary>随包自托管字体目录：exe 旁 fonts\（构建时把 assets/fonts 拷进来，用户也能自己往里放）。
    /// 同一位置两种命名都认（fonts / font），便于用户手动投放；用户字体库最后扫。</summary>
    public static IEnumerable<string> FontDirs()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "fonts");
        yield return Path.Combine(AppContext.BaseDirectory, "font");
        yield return UserFontDir;
    }

    // 字体目录索引：family（小写去空格）→ 该族的全部脸（多字重文件都在时按请求字重挑最近档）。
    // 目录内容会变（网络拉到新字体、用户投字体），重新扫描时整体重建。
    private static readonly Dictionary<string, List<SKTypeface>> DirFaces = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object DirLock = new();
    private static bool _dirScanned;
    private static DateTime _dirScannedAt = DateTime.MinValue;

    // 目录文件清单指纹（名字+大小+时间）：变了才重建索引，省得每帧读盘
    private static string _dirFingerprint = "";

    /// <summary>扫描字体目录（幂等；目录内容变了才重建索引）。启动与字体落盘后自动跑。</summary>
    public static void ScanFontDirs(bool force = false)
    {
        // 重建会 Dispose 渲染线程可能正持着的旧 SKTypeface（FontJobs/ResolveCache 缓存的是同一批
        // 引用）：必须先过渲染闸再拿 DirLock——锁序与渲染路径（gate→DirLock）一致，反序 ABBA 死锁。
        // 渲染热路径调用时已在闸内，Monitor 可重入。
        lock (EngineRenderGate.Lock)
        {
            lock (DirLock)
            {
            if (_dirScanned && !force && (DateTime.UtcNow - _dirScannedAt).TotalSeconds < 5) return;
            _dirScannedAt = DateTime.UtcNow;
            _dirScanned = true;

            var files = new List<string>();
            foreach (var d in FontDirs())
            {
                try
                {
                    if (!Directory.Exists(d)) continue;
                    foreach (var f in Directory.EnumerateFiles(d))
                        if (f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".font", StringComparison.OrdinalIgnoreCase))
                            files.Add(f);
                }
                catch (Exception ex)
                {
                    Log.Debug("[字体目录] 扫描失败 " + d + "：" + ex.Message);
                }
            }
            files.Sort(StringComparer.OrdinalIgnoreCase);

            var fp = new StringBuilder();
            foreach (var f in files)
            {
                try
                {
                    var fi = new FileInfo(f);
                    fp.Append(f).Append('|').Append(fi.Length).Append('|').Append(fi.LastWriteTimeUtc.Ticks).Append('\n');
                }
                catch
                {
                    // 文件刚被删：指纹里不体现，下次扫描再修正
                }
            }
            if (fp.ToString() == _dirFingerprint) return; // 没变：留用现有索引

            foreach (var list in DirFaces.Values)
                foreach (var f in list)
                    f.Dispose();
            DirFaces.Clear();
            var n = 0;
            foreach (var f in files)
            {
                try
                {
                    var tf = SKTypeface.FromFile(f);
                    if (tf == null) continue;
                    var k = FaceKey(tf.FamilyName);
                    if (!DirFaces.TryGetValue(k, out var list)) DirFaces[k] = list = new List<SKTypeface>();
                    list.Add(tf);
                    n++;
                }
                catch (Exception ex)
                {
                    Log.Debug($"[字体目录] 读不出字体 {Path.GetFileName(f)}：{ex.Message}");
                }
            }
            _dirFingerprint = fp.ToString();
            Log.Info($"[字体目录] {DirFaces.Count} 个族 / {n} 张脸 / {files.Count} 个文件（" +
                     string.Join("、", FontDirs().Where(Directory.Exists).Select(Path.GetFileName)) + "）");
            }
            // 目录内容变了（新字体落盘/用户投字体）：字形与解析缓存全清（首次扫描时无缓存可清）
            InvalidateResolveCache();
        }
    }

    /// <summary>族名归一（大小写与空格无关：字体的 FamilyName 与目录项写法常不一致）。</summary>
    private static string FaceKey(string family) => (family ?? "").Replace(" ", "").ToLowerInvariant();

    /// <summary>字体目录里有没有这个族（不含系统字体）。</summary>
    public static bool InFontDirs(string family)
    {
        ScanFontDirs();
        lock (DirLock)
            return DirFaces.ContainsKey(FaceKey(family.Trim().Trim('"')));
    }

    /// <summary>系统里装了这个族没有（FromFamilyName 缺字体时返回默认体，须比对族名）。</summary>
    public static bool IsFamilyInstalled(string family)
    {
        var fam = family.Trim().Trim('"');
        if (fam.Length == 0) return false;
        try
        {
            using var t = SKTypeface.FromFamilyName(fam, SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
            return t != null && string.Equals(FaceKey(t.FamilyName), FaceKey(fam), StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>单族解析：用户字体 → 字体目录 → 系统族。命中返回 true（不打日志；上层统一缓存）。</summary>
    private static bool TryFamily(string familyQuoted, int weight, out SKTypeface tf)
    {
        tf = null!;
        var fam = familyQuoted.Trim().Trim('"');
        if (fam.Length == 0) return false;
        if (fam is "sans-serif" or "serif" or "monospace" or "cursive" or "fantasy") return false; // 泛型族交给气质栈

        // 用户上传的字体（family 是 UF_xxx 之类的合成名，系统里必然没有）
        if (UserFaces.TryGetValue(fam, out var uf))
        {
            tf = uf;
            return true;
        }

        // 字体目录（网络拉取 / 自托管 / 用户投放）
        ScanFontDirs();
        lock (DirLock)
        {
            if (DirFaces.TryGetValue(FaceKey(fam), out var faces) && faces.Count > 0)
            {
                tf = PickWeight(faces, weight);
                return true;
            }
        }

        // 系统
        try
        {
            var t = SKTypeface.FromFamilyName(fam, (SKFontStyleWeight)weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
            if (t != null && string.Equals(FaceKey(t.FamilyName), FaceKey(fam), StringComparison.Ordinal))
            {
                tf = t;
                return true;
            }
            t?.Dispose();
        }
        catch
        {
            // 对齐 JS document.fonts.load(...).catch(() => null)：没有就试下一级
        }
        return false;
    }

    /// <summary>同一个族有多张脸时挑字重最近的一张。</summary>
    private static SKTypeface PickWeight(List<SKTypeface> faces, int weight)
    {
        SKTypeface best = faces[0];
        var bestScore = int.MaxValue;
        foreach (var f in faces)
        {
            var d = Math.Abs((int)f.FontStyle.Weight - weight);
            if (d < bestScore)
            {
                bestScore = d;
                best = f;
            }
        }
        return best;
    }

    // 解析结果缓存：weight|family 链 → 脸（渲染热路径按字形/字号另有一层缓存）
    private static readonly Dictionary<string, SKTypeface> ResolveCache = new();
    private static readonly object ResolveLock = new();

    /// <summary>CSS 字体栈级联：整条链逐级下探（链尾由调用方补气质栈），永不返回 null。
    /// public：字体管线的对外入口（渲染层与引擎测试都从这里取脸）。</summary>
    public static SKTypeface ResolveChain(IEnumerable<string> families, int weight)
    {
        var list = families.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
        var key = weight + "|" + string.Join(";", list);
        lock (ResolveLock)
        {
            if (ResolveCache.TryGetValue(key, out var hit)) return hit;
        }
        SKTypeface? r = null;
        foreach (var fam in list)
            if (TryFamily(fam, weight, out var tf))
            {
                r = tf;
                break;
            }
        r ??= LastResort();
        lock (ResolveLock)
        {
            if (ResolveCache.Count > 4096) ResolveCache.Clear(); // 容量护栏（太さ 逐字重会灌满）
            ResolveCache[key] = r;
        }
        return r;
    }

    /// <summary>最后兜底：系统的微软雅黑（中文 Windows 必然有，字形覆盖中日韩）。实在没有才退
    /// 系统逐字匹配、再退 Skia 默认体（此前一律默认体，CJK 出方块）。</summary>
    private static SKTypeface? _lastResort;

    private static SKTypeface LastResort()
    {
        if (_lastResort != null) return _lastResort;
        foreach (var fam in new[] { "Microsoft YaHei UI", "Microsoft YaHei" })
            if (TryFamily(fam, 400, out var t))
                return _lastResort = t;
        try
        {
            var m = SKFontManager.Default.MatchCharacter('漢');
            if (m != null) return _lastResort = m;
        }
        catch
        {
            // 系统连一个 CJK 脸都找不到：只能退默认体
        }
        return _lastResort = SKTypeface.Default;
    }

    // ---------------- 气质字体栈（语言 × kind） ----------------

    // 客户端离线可用的系统字体栈：每项按序精确同名命中，都没有再逐字兜底。
    // 语言在前（歌词语言的气质），后面接本机常见的 CJK 脸，保证任何 Windows 都有得用。
    private static readonly Dictionary<string, string[]> SysStacks = new()
    {
        ["ja|gothic"] = new[] { "Yu Gothic UI", "Yu Gothic", "Meiryo UI", "Meiryo", "Hiragino Sans", "BIZ UDGothic", "MS UI Gothic", "MS Gothic", "Noto Sans CJK JP", "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "DengXian" },
        ["ja|mincho"] = new[] { "Yu Mincho", "MS Mincho", "Hiragino Mincho ProN", "BIZ UDMincho", "Noto Serif CJK JP", "SimSun", "STSong", "PMingLiU", "Microsoft YaHei UI", "Microsoft YaHei" },
        ["ja|display"] = new[] { "Yu Gothic UI", "Yu Gothic", "Hiragino Sans", "Meiryo", "BIZ UDPGothic", "MS Gothic", "Microsoft YaHei UI", "Microsoft YaHei", "SimHei" },
        ["ja|round"] = new[] { "HGMaruGothicMPRO", "Hiragino Maru Gothic ProN", "Yu Gothic UI", "Meiryo UI", "Microsoft YaHei UI", "Microsoft YaHei", "DengXian", "Yu Gothic" },
        ["ja|pixel"] = new[] { "MS Gothic", "MS UI Gothic", "SimSun", "DotGothic16", "Microsoft YaHei UI", "Microsoft YaHei" },
        ["ja|brush"] = new[] { "HGPSoeiKakugothicUB", "Yu Mincho", "MS Mincho", "Hiragino Mincho ProN", "KaiTi", "DFKai-SB", "STKaiti", "SimSun", "Microsoft YaHei UI", "Microsoft YaHei" },
        ["ja|mono"] = new[] { "IBM Plex Mono", "Consolas", "Cascadia Mono", "MS Gothic", "Microsoft YaHei UI", "Microsoft YaHei" },

        ["zh-Hans|gothic"] = new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Noto Sans SC", "Source Han Sans SC", "PingFang SC", "DengXian", "SimHei", "SimSun" },
        ["zh-Hans|mincho"] = new[] { "Noto Serif SC", "Source Han Serif SC", "SimSun", "STSong", "FangSong", "PMingLiU", "Yu Mincho" },
        ["zh-Hans|display"] = new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "DengXian", "SimSun" },
        ["zh-Hans|round"] = new[] { "Microsoft YaHei UI", "Microsoft YaHei", "DengXian", "SimHei", "SimSun" },
        ["zh-Hans|pixel"] = new[] { "SimSun", "MS Gothic", "SimHei", "DengXian" },
        ["zh-Hans|brush"] = new[] { "KaiTi", "STKaiti", "DFKai-SB", "SimSun", "FangSong" },
        ["zh-Hans|mono"] = new[] { "Consolas", "IBM Plex Mono", "Cascadia Mono", "Microsoft YaHei UI" },

        ["zh-Hant|gothic"] = new[] { "Microsoft JhengHei UI", "Microsoft JhengHei", "Noto Sans TC", "PingFang TC", "MingLiU", "PMingLiU", "Microsoft YaHei UI", "Microsoft YaHei" },
        ["zh-Hant|mincho"] = new[] { "PMingLiU", "MingLiU", "Noto Serif TC", "Source Han Serif TC", "DFKai-SB", "Yu Mincho", "Microsoft YaHei UI", "Microsoft YaHei" },
        ["zh-Hant|display"] = new[] { "Microsoft JhengHei UI", "Microsoft JhengHei", "MingLiU", "PMingLiU", "Microsoft YaHei UI", "Microsoft YaHei" },
        ["zh-Hant|round"] = new[] { "Microsoft JhengHei UI", "Microsoft JhengHei", "PMingLiU", "Microsoft YaHei UI", "Microsoft YaHei" },
        ["zh-Hant|pixel"] = new[] { "MingLiU", "PMingLiU", "SimSun", "Microsoft YaHei UI", "Microsoft YaHei" },
        ["zh-Hant|brush"] = new[] { "DFKai-SB", "KaiTi", "STKaiti", "PMingLiU", "Microsoft YaHei UI", "Microsoft YaHei" },
        ["zh-Hant|mono"] = new[] { "Consolas", "IBM Plex Mono", "Cascadia Mono", "Microsoft JhengHei UI", "Microsoft YaHei UI", "Microsoft YaHei" },

        ["ko|gothic"] = new[] { "Malgun Gothic", "Noto Sans KR", "Apple SD Gothic Neo", "Gulim", "Dotum", "Microsoft YaHei UI", "Microsoft YaHei" },
        ["ko|mincho"] = new[] { "Batang", "Gungsuh", "Noto Serif KR", "Gowun Batang", "Microsoft YaHei UI", "Microsoft YaHei" },
        ["ko|display"] = new[] { "Malgun Gothic", "Noto Sans KR", "Black Han Sans", "Gulim", "Microsoft YaHei UI", "Microsoft YaHei" },
        ["ko|round"] = new[] { "Jua", "Malgun Gothic", "Gulim", "Microsoft YaHei UI", "Microsoft YaHei" },
        ["ko|pixel"] = new[] { "Gulim", "Dotum", "Batang", "Microsoft YaHei UI", "Microsoft YaHei" },
        ["ko|brush"] = new[] { "Nanum Brush Script", "Gungsuh", "Batang", "Microsoft YaHei UI", "Microsoft YaHei" },
        ["ko|mono"] = new[] { "Consolas", "IBM Plex Mono", "Cascadia Mono", "Malgun Gothic", "Microsoft YaHei UI", "Microsoft YaHei" },

        ["en|gothic"] = new[] { "Segoe UI", "Arial", "Tahoma", "Verdana" },
        ["en|mincho"] = new[] { "Georgia", "Times New Roman", "Cambria", "Microsoft YaHei UI", "Microsoft YaHei" },
        ["en|display"] = new[] { "Impact", "Segoe UI Black", "Segoe UI", "Arial Black" },
        ["en|round"] = new[] { "Comic Sans MS", "Segoe UI", "Verdana" },
        ["en|pixel"] = new[] { "Consolas", "Courier New" },
        ["en|brush"] = new[] { "Segoe Script", "Ink Free", "Georgia" },
        ["en|mono"] = new[] { "Consolas", "Cascadia Mono", "Courier New", "Microsoft YaHei UI", "Microsoft YaHei" },
    };

    /// <summary>某语言下某气质（font key 的 kind）的系统字体栈；未知 kind 按黑体（gothic）走。
    /// 英文栈太薄：真缺了用日文栈的 CJK 脸兜（英文歌词里混汉字/假名时不至于掉进 .notdef）。</summary>
    public static string[] StackFor(string? kind)
    {
        var lang = JLang.Lang;
        var k = kind switch
        {
            "mincho" => "mincho",
            "brush" or "hand" => "brush",
            "round" => "round",
            "display" => "display",
            "pixel" => "pixel",
            "mono" => "mono",
            _ => "gothic"
        };
        var ja = SysStacks["ja|" + k];
        if (lang == "ja") return ja;
        if (SysStacks.TryGetValue(lang + "|" + k, out var s) && lang != "en") return s;
        return lang == "en" ? s!.Concat(ja).ToArray() : ja;
    }

    /// <summary>字体键实际要画的字体栈：目录项 family + 它的 CSS fallback 链 + 语言×气质系统栈。</summary>
    public static string[] FaceChain(FontFace f) =>
        new[] { f.Family }.Concat(FbFamilies(f.Fb)).Concat(StackFor(f.Kind)).ToArray();

    /// <summary>CSS fallback 串（"\"A\",\"B\",sans-serif"）→ family 列表。</summary>
    internal static IEnumerable<string> FbFamilies(string? fb) =>
        (fb ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0);

    // ---------------- 逐字兜底（CJK 永不 .notdef） ----------------

    // 主脸缺字时按字找脸：气质栈逐个试，再问系统 MatchCharacter。键含主族名+码点，容量有护栏。
    private static readonly Dictionary<string, SKTypeface> CoverCache = new();
    private static readonly object CoverLock = new();

    /// <summary>主脸没有这个字时，找一张有的脸（气质栈/系统逐字匹配）；都没有则原脸。</summary>
    public static SKTypeface CoverFace(SKTypeface primary, int codepoint, int weight, string? kind)
    {
        if (primary.ContainsGlyph(codepoint)) return primary;
        var key = primary.FamilyName + "|" + weight + "|" + JLang.Lang + "|" + kind + "|" + codepoint.ToString("X");
        lock (CoverLock)
        {
            if (CoverCache.TryGetValue(key, out var hit)) return hit;
        }
        SKTypeface? r = null;
        foreach (var fam in StackFor(kind))
        {
            if (!TryFamily(fam, weight, out var tf) || !tf.ContainsGlyph(codepoint)) continue;
            r = tf;
            break;
        }
        if (r == null)
        {
            try
            {
                var m = SKFontManager.Default.MatchCharacter(codepoint);
                if (m != null && m.ContainsGlyph(codepoint)) r = m;
            }
            catch
            {
                // 系统匹配不了（罕见字/私有区）：保留原脸
            }
        }
        r ??= primary;
        lock (CoverLock)
        {
            if (CoverCache.Count > 4096) CoverCache.Clear();
            CoverCache[key] = r;
        }
        return r;
    }

    /// <summary>字体目录/用户字体/语言变化后清空解析缓存（字形与 SKFont 缓存一并失效）。</summary>
    public static void InvalidateResolveCache()
    {
        lock (ResolveLock) ResolveCache.Clear();
        lock (CoverLock) CoverCache.Clear();
        _lastResort = null; // 兜底脸可能来自目录（rescan 后已 Dispose），悬空引用必须重建
        JLang.Glyphs?.Clear();
        JLang.Metrics?.Clear();
        JText.OnFontsChanged();
    }

    /// <summary>J.ensureFonts：确保要画的字形已加载。keys = null → 目录全部；顺带按需补网络字体。</summary>
    public static async Task EnsureFonts(string? text, IEnumerable<string>? keys = null)
    {
        _ = UniqueCodePoints(text ?? ""); // 对齐 JS 的 uniq 计算（C# 侧按脸解析不按字，这里只保持调用形状）
        var list = (keys ?? (IEnumerable<string>)Table.Keys).Where(Has).ToList();

        // 目录项 + 语言基础脸（含 fallback 链与气质栈）逐个解析：把脸与字形缓存预热好
        var faces = new List<FontFace>();
        foreach (var k in list) faces.Add(JLang.FaceOf(k));
        foreach (var b in JLang.LangBaseFaces(list))
            faces.Add(new FontFace { Family = "\"" + b.Family + "\"", Weight = b.Weight, Gf = b.Gf });
        if (keys != null && keys.Contains("@var"))
        {
            // 太さ：可变的 Noto Sans / Serif JP（目录里没有时落到气质栈的黑体/明朝）
            faces.Add(new FontFace { Family = "\"Noto Sans JP\"", Weight = 100, Kind = "gothic", Fb = JpSansFb });
            faces.Add(new FontFace { Family = "\"Noto Sans JP\"", Weight = 900, Kind = "gothic", Fb = JpSansFb });
            faces.Add(new FontFace { Family = "\"Noto Serif JP\"", Weight = 200, Kind = "mincho", Fb = JpSerifFb });
            faces.Add(new FontFace { Family = "\"Noto Serif JP\"", Weight = 900, Kind = "mincho", Fb = JpSerifFb });
        }

        var seen = new HashSet<string>();
        foreach (var f in faces)
        {
            if (!seen.Add(f.Weight + " " + f.Family)) continue;
            _ = ResolveChain(FaceChain(f), f.Weight);
        }

        // 目录/系统都没有的家族：后台按需拉（老前端「网络拉取 + 本地缓存」，拉到后热更新）。
        // 注意这里不清字形缓存——字体集合没变时清缓存会把整帧排版重算一遍（性能专项的教训）；
        // 真正变化的事件（用户字体登记/新字体落盘）各自走 InvalidateResolveCache。
        FontFetch.Request(faces);

        // JS 末尾还 await document.fonts.ready —— C# 加载在解析时同步完成，没有对应物
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
