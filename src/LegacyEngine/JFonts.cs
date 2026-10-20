using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using JizuraLive.App; using JizuraLive.Logging;
using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace JizuraLive.LegacyEngine;

public static class JFonts
{

    public const string JpSansFb = "\"Noto Sans JP\",\"Noto Sans CJK JP\",\"Hiragino Sans\",\"Yu Gothic\",\"Meiryo\",sans-serif";
    public const string JpSerifFb = "\"Noto Serif JP\",\"Noto Serif CJK JP\",\"Hiragino Mincho ProN\",\"Yu Mincho\",serif";

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

        public static void Install() => _ = Table; 
    public static bool Has(string key) => Table.ContainsKey(key);

        private static readonly Dictionary<string, SKTypeface> UserFaces = new();

        public static string UserFontDir { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JizuraLivePVPV", "fonts");

        public static string FontCss(string key, double px)
    {
        var f = JLang.FaceOf(key);
        var v = px == 0 ? 0 : px;         return $"{f.Weight} {v.ToString("F2", CultureInfo.InvariantCulture)}px {f.Family},{f.Fb}";
    }

        public static IEnumerable<string> FontDirs()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "fonts");
        yield return Path.Combine(AppContext.BaseDirectory, "font");
        yield return UserFontDir;
    }

    private static readonly Dictionary<string, List<SKTypeface>> DirFaces = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object DirLock = new();
    private static bool _dirScanned;
    private static DateTime _dirScannedAt = DateTime.MinValue;

    private static string _dirFingerprint = "";

        public static void ScanFontDirs(bool force = false)
    {
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
                }
            }
            if (fp.ToString() == _dirFingerprint) return; 
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
            InvalidateResolveCache();
        }
    }

        private static string FaceKey(string family) => (family ?? "").Replace(" ", "").ToLowerInvariant();

    private static readonly Dictionary<string, bool> InstalledCache = new(StringComparer.Ordinal);

        private static bool TryFamily(string familyQuoted, int weight, out SKTypeface tf)
    {
        tf = null!;
        var fam = familyQuoted.Trim().Trim('"');
        if (fam.Length == 0) return false;
        if (fam is "sans-serif" or "serif" or "monospace" or "cursive" or "fantasy") return false; 
        if (UserFaces.TryGetValue(fam, out var uf))
        {
            tf = uf;
            return true;
        }

        ScanFontDirs();
        lock (DirLock)
        {
            if (DirFaces.TryGetValue(FaceKey(fam), out var faces) && faces.Count > 0)
            {
                tf = PickWeight(faces, weight);
                return true;
            }
        }

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
        }
        return false;
    }

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

    private static readonly Dictionary<string, SKTypeface> ResolveCache = new();
    private static readonly object ResolveLock = new();

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
            if (ResolveCache.Count > 4096) ResolveCache.Clear();             ResolveCache[key] = r;
        }
        return r;
    }

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
        }
        return _lastResort = SKTypeface.Default;
    }

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

        public static string[] FaceChain(FontFace f) =>
        new[] { f.Family }.Concat(FbFamilies(f.Fb)).Concat(StackFor(f.Kind)).ToArray();

        internal static IEnumerable<string> FbFamilies(string? fb) =>
        (fb ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0);

    private static readonly Dictionary<string, SKTypeface> CoverCache = new();
    private static readonly object CoverLock = new();

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

        public static void InvalidateResolveCache()
    {
        lock (ResolveLock) ResolveCache.Clear();
        lock (CoverLock) CoverCache.Clear();
        lock (DirLock) InstalledCache.Clear();
        _lastResort = null;         JLang.Glyphs?.Clear();
        JLang.Metrics?.Clear();
        JText.OnFontsChanged();
    }

    }
