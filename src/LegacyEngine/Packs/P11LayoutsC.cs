
using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace JizuraLive.LegacyEngine;

public static class P11LayoutsC
{
    static P11LayoutsC() => Install();

        public static void Install()
    {
        if (_installed) return;
        _installed = true;
        RegisterMagazine();
        RegisterHeadlineDeck();
        RegisterContents();
        RegisterFootnote();
        RegisterProofread();
        RegisterNumbered();
        RegisterPoster();
        RegisterSwissGrid();
        RegisterDictionary();
        RegisterEma();
        RegisterRansom();
        RegisterNewspaper();
        RegisterVinyl();
        RegisterCassette();
        RegisterBookSpine();
        RegisterPolaroid();
        RegisterStampSheet();
        RegisterPostcard();
        RegisterLetterPaper();
        RegisterCalendar();
        RegisterChochin();
        RegisterRouteMap();
        RegisterStationSign();
        RegisterNoren();
        RegisterTanzaku();
        RegisterOmikuji();
        RegisterKakejiku();
        RegisterShoji();
        RegisterClapper();
        RegisterWarningLabel();
        RegisterPriceTag();
        RegisterNameTag();
        RegisterStickyNotes();
        RegisterKaruta();
    }

    private static bool _installed;

        private const string PackC = "layoutsC";

    private static void RegC(string key, LayoutDef def) => Reg.Register("layout", key, def, PackC);

        private static double U(Env env) => Math.Min(env.W, env.H);

        private static bool IsPort(Env env) => env.H > env.W * 1.08;

        private static bool PortOf(LayoutCtx cut) => cut.H > cut.W * 1.08;

        private static string Strip(string? t) => Regex.Replace(t ?? "", @"\s+", "");

    private static string Pad2(int n) => n.ToString(CultureInfo.InvariantCulture).PadLeft(2, '0');
    private static string Pad3(int n) => n.ToString(CultureInfo.InvariantCulture).PadLeft(3, '0');

        private static int LineN(Env env) => Math.Max(0, env.Cut!.Line) + 1;
    private static string LineNo(Env env) => Pad2(LineN(env));

    private static string BodyF(Env env) => JLayouts.Font0(env.St, "body") ?? "gothic_med";
    private static string MonoF(Env env) => JLayouts.Font0(env.St, "mono") ?? "mono";
    private static string SerifF(Env env) => JLayouts.Font0(env.St, "serif") ?? "mincho";

    private static string[] FontsOf(StyleDef st, params string[] roles) => JLayouts.FontsOf(st, roles);

        private static double Tin(Env env, double d = 0, double len = 0.4, Func<double, double>? ease = null)
    {
        ease ??= J.E.OutExpo;
        return ease(J.Clamp((env.Lt - d) / Math.Max(0.01, len)));
    }

        private static double Tout(Env env) => 1 - J.E.InCubic(env.POut);

    private static TextMetrics Meas(string text, string font, double size, JObject? o = null)
    {
        var it = new JObject { ["text"] = text, ["font"] = font, ["size"] = size };
        if (o != null)
            foreach (var kv in o)
                it[kv.Key] = kv.Value?.DeepClone();
        return JLayouts.Measure(it);
    }

    private static BBox Box(double x0, double y0, double x1, double y1) => new()
    {
        X0 = x0, Y0 = y0, X1 = x1, Y1 = y1,
        Cx = (x0 + x1) / 2, Cy = (y0 + y1) / 2,
        Boxes = new List<JObject>(),
    };

    private static double SmallSize(Env env) => J.Clamp(U(env) * 0.024, 14, 34);

    private static bool HasLatin(string? t) => Regex.IsMatch(t ?? "", "[A-Za-z]");

        private static string Flat(string? t) => HasLatin(t) ? Regex.Replace((t ?? "").Trim(), @"\s+", " ") : Strip(t);

        private static string VText(string? t) => HasLatin(t) ? Flat(t) : Strip(t);

    private static string? RomaOfText(string? t)
    {
        if (HasLatin(t) || string.IsNullOrEmpty(t)) return null;
        var r = J.Romaji(Strip(t));
        return r != null ? r.ToUpperInvariant() : null;
    }

    private static string? RomajiOf(Env env) => RomaOfText(env.Cut!.Text);

        private static string? DeckCopy(Env env)
    {
        var c = env.Cut!;
        if (!string.IsNullOrEmpty(c.LineText) && Strip(c.LineText) != Strip(c.Text)) return Flat(c.LineText);
        return JLayouts.OrStr(c.Note, RomajiOf(env));
    }

    private static string MetaLine(Env env) => "No." + LineNo(env) + "  ／  " + J.FmtTime(env.Cut!.Start) + "  ／  " +
        JLayouts.GlyphCount(env.Cut.Text) + (HasLatin(env.Cut.Text) ? " CHARS" : "字");

        private static bool IsBad(char c) => J.IsSmallKana(c) || J.IsPunct(c) || c == 'ー' || c == ' ';

    private const string KNum = "〇一二三四五六七八九";

    private static string KanjiNum(int n)
    {
        n = Math.Max(0, n);
        if (n < 10) return KNum[n].ToString();
        if (n < 20) return "十" + (n % 10 != 0 ? KNum[n % 10].ToString() : "");
        if (n < 100) return KNum[n / 10].ToString() + "十" + (n % 10 != 0 ? KNum[n % 10].ToString() : "");
        return n.ToString(CultureInfo.InvariantCulture);
    }

        private static double LtU(Env env) => env.Ltb;

        private static double POutU(Env env)
    {
        var c = env.Cut!;
        return c.OutDur > 0 ? J.Clamp((env.Ltb - (c.Dur - c.OutDur)) / c.OutDur) : 0;
    }

        private static bool GIn(Env env) => env.Lt < 0.6 && env.POut <= 0;

        private static bool PlateHold(Env env) => env.Cut!.Hold is not ("still" or "jitter" or "breathe" or "glitchtick");

        private static double MiAt(Env env, double t) => Math.Max(0, t) / Math.Max(0.005, env.Cut!.Stagger != 0 ? env.Cut.Stagger : 0.04);

    private static List<string> Pal(Scheme sc) => new[] { "bg", "fg", "ink", "sub", "accent", "accent2", "dim" }
        .Select(sc.Str).Where(c => !string.IsNullOrEmpty(c)).Select(c => c!).ToList();

    private static string Lightest(Scheme sc)
    {
        var p = Pal(sc);
        if (p.Count == 0) return "#FFFFFF";
        var best = p[0];
        foreach (var c in p)
            if (J.Lum(c) > J.Lum(best)) best = c;
        return best;
    }

    private static string Darkest(Scheme sc)
    {
        var p = Pal(sc);
        if (p.Count == 0) return "#000000";
        var best = p[0];
        foreach (var c in p)
            if (J.Lum(c) < J.Lum(best)) best = c;
        return best;
    }

    private static readonly Dictionary<string, string> OncCache = new();

    private static string OnCol(Scheme sc, string fill)
    {
        var key = fill + sc.Str("bg") + sc.Str("fg") + sc.Str("ink") + sc.Str("accent") + sc.Str("sub");
        if (OncCache.TryGetValue(key, out var hit)) return hit;
        string? best = null;
        var bv = 0.0;
        foreach (var c in new[] { sc.Str("bg"), sc.Str("fg"), sc.Str("ink"), sc.Str("accent"), sc.Str("sub") })
        {
            if (string.IsNullOrEmpty(c) || c == fill) continue;
            var k = J.Contrast(c!, fill);
            if (k > bv)
            {
                bv = k;
                best = c;
            }
        }
        var v = bv >= 2.4 ? best! : (J.Lum(fill) > 0.5 ? "#111111" : "#FFFFFF");
        if (OncCache.Count > 300) OncCache.Clear();
        OncCache[key] = v;
        return v;
    }

        private static string PlateCol(Scheme sc, IEnumerable<string?> pref, string? against = null, double min = 1.6)
    {
        var bgc = against ?? sc.Str("bg")!;
        foreach (var c in pref)
            if (!string.IsNullOrEmpty(c) && J.Contrast(c!, bgc) >= min) return c!;
        return J.Contrast(sc.Str("fg")!, bgc) >= J.Contrast(sc.Str("bg")!, bgc) ? sc.Str("fg")! : sc.Str("bg")!;
    }

        private sealed class CardCol
    {
        public string Fill = "#FFFFFF";
        public bool Edge;
        public string Text = "#111111";
        public string Acc = "#000000";
        public string Faint = "#888888";
        public string Line = "#555555";
    }

    private static CardCol Card(Scheme sc)
    {
        var fill = Lightest(sc);
        var edge = J.Contrast(fill, sc.Str("bg")!) < 1.4;
        var text = OnCol(sc, fill);
        var acc = PlateCol(sc, new[] { sc.Str("accent"), sc.Str("accent2"), sc.Str("ink") }, fill, 2);
        var faint = J.Mix(fill, text, 0.22);
        return new CardCol { Fill = fill, Edge = edge, Text = text, Acc = acc, Faint = faint, Line = J.Mix(fill, text, 0.35) };
    }

        private static void ShadowR(Env env, double x, double y, double w, double h, double r, double a = 1, double? d = null)
    {
        if (env.Pass != "main" || a <= 0.01) return;
        var k = d ?? U(env) * 0.012;
        env.RRect(x + k * 0.6, y + k, w, h, r, Darkest(env.Sc!), a * (J.Lum(env.Sc!.Str("bg")!) > 0.5 ? 0.22 : 0.5), false);
    }

    private static HashSet<int> SegBounds(string t)
    {
        var o = new HashSet<int>();
        var i = 0;
        try
        {
            foreach (var sg in Planner.Segments(t))
            {
                i += JLayouts.CpLen(sg);
                o.Add(i);
            }
        }
        catch
        {
        }
        return o;
    }

    private static List<string> Split2(string word, bool force = false)
    {
        var chars = JLayouts.CodePoints(word);
        var n = chars.Count;
        var sb = SegBounds(word);
        var best = Math.Max(1, n / 2);
        var bs = -1e9;
        for (var c = 1; c < n; c++)
        {
            var a = chars[c - 1][0];
            var b = chars[c][0];
            if (!force && ((c == 1 && !J.IsKanji(a)) || (c == n - 1 && !J.IsKanji(b)))) continue;
            var s = -Math.Abs(c - n / 2.0) * 0.9;
            if (a == ' ' || a == '　' || (J.IsPunct(a) && a != 'ー')) s += 5;
            if (J.IsHira(a) && !J.IsHira(b) && !IsBad(b)) s += 3;
            if (sb.Contains(c)) s += 2;
            if (IsBad(b)) s -= 6;
            if (J.IsKanji(a) && J.IsKanji(b)) s -= 2;
            if (J.IsKanji(a) && J.IsHira(b)) s -= sb.Contains(c) ? 0.5 : 2.5;
            if (J.IsHira(a) && J.IsHira(b) && !sb.Contains(c)) s -= 2.5;
            if (s > bs)
            {
                bs = s;
                best = c;
            }
        }
        if (!force && bs < -1.5 && !HasLatin(word)) return new List<string> { word };
        var parts = new List<string>
        {
            string.Concat(chars.Take(best)).Trim(),
            string.Concat(chars.Skip(best)).Trim(),
        };
        return parts.Where(p => p.Length > 0).ToList();
    }

    private static List<string> SplitK(string text, int k, int force = 0)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return new List<string> { "" };
        var latin = HasLatin(t);
        var words = latin
            ? Regex.Split(t, @"\s+").Where(w => w.Length > 0).ToList()
            : Planner.ChunkText(t).Select(w => w.Trim()).Where(w => w.Length > 0).ToList();
        if (words.Count == 0) words = new List<string> { t };
        k = Math.Max(1, Math.Min(k, JLayouts.GlyphCount(t)));
        var hard = new HashSet<string>();
        for (var guard = 0; words.Count < k && guard < 16; guard++)
        {
            var bi = -1;
            var bl = 1;
            for (var i = 0; i < words.Count; i++)
            {
                var l = JLayouts.GlyphCount(words[i]);
                if (l > bl && !hard.Contains(words[i]))
                {
                    bl = l;
                    bi = i;
                }
            }
            if (bi < 0) break;
            var parts = latin ? new List<string> { words[bi] } : Split2(words[bi]);
            if (parts.Count < 2 && force > 0 && words.Count < force && !latin) parts = Split2(words[bi], true);
            if (parts.Count < 2)
            {
                hard.Add(words[bi]);
                continue;
            }
            words.RemoveAt(bi);
            words.InsertRange(bi, parts);
        }
        if (k <= 1) return new List<string> { string.Join(latin ? " " : "", words) };
        if (words.Count <= k) return words;
        List<List<string>> Part(List<string> ws)
        {
            var lens = ws.Select(w => JLayouts.GlyphCount(w) + 0.5).ToList();
            var pre = new List<double> { 0 };
            foreach (var l in lens) pre.Add(pre[^1] + l);
            var n = ws.Count;
            var tgt = pre[n] / k;
            List<int>? best = null;
            var bestS = double.PositiveInfinity;
            var guard = 0;
            void Rec(int start, int g, List<int> cuts)
            {
                if (++guard > 5000) return;
                if (g == k - 1)
                {
                    var s = 0.0;
                    var prev = 0;
                    foreach (var c in cuts.Append(n))
                    {
                        var d = pre[c] - pre[prev] - tgt;
                        s += d * d;
                        prev = c;
                    }
                    if (s < bestS)
                    {
                        bestS = s;
                        best = cuts.Append(n).ToList();
                    }
                    return;
                }
                for (var c = start + 1; c <= n - (k - 1 - g); c++)
                {
                    var next = new List<int>(cuts) { c };
                    Rec(c, g + 1, next);
                }
            }
            Rec(0, 0, new List<int>());
            if (best == null) return ws.Select(w => new List<string> { w }).ToList();
            var res = new List<List<string>>();
            var prevI = 0;
            foreach (var c in best)
            {
                res.Add(ws.Skip(prevI).Take(c - prevI).ToList());
                prevI = c;
            }
            return res;
        }
        var groups = Part(words);
        for (var it = 0; it < 3; it++)
        {
            var gl = groups.Select(g => g.Sum(w => JLayouts.GlyphCount(w))).ToList();
            double mx = gl.Max(), mn = gl.Min();
            if (mx <= mn * 1.7 + 1) break;
            var g = groups[gl.IndexOf((int)mx)];
            string? bw = null;
            foreach (var w in g)
                if (!hard.Contains(w) && JLayouts.GlyphCount(w) >= 3 && (bw == null || JLayouts.GlyphCount(w) > JLayouts.GlyphCount(bw))) bw = w;
            if (bw == null) break;
            var parts = latin ? new List<string> { bw } : Split2(bw);
            if (parts.Count < 2)
            {
                hard.Add(bw);
                continue;
            }
            var wi = words.IndexOf(bw);
            words.RemoveAt(wi);
            words.InsertRange(wi, parts);
            groups = Part(words);
        }
        return groups.Select(g => string.Join(latin ? " " : "", g)).ToList();
    }

        private static List<string> CharUnits(string text)
    {
        var o = new List<string>();
        foreach (var ch in Strip(text))
        {
            if (o.Count > 0 && IsBad(ch)) o[^1] += ch;
            else o.Add(ch.ToString());
        }
        return o;
    }

    private static string Brk(string text, int maxPer)
    {
        var t = (text ?? "").Trim();
        var n = JLayouts.GlyphCount(t);
        if (n <= maxPer) return t;
        return string.Join("\n", SplitK(t, (int)Math.Ceiling(n / (double)maxPer)));
    }

    private static readonly Dictionary<string, object> Memo = new();

    private static T MemoGet<T>(string key, Func<T> fn) where T : class
    {
        lock (Memo)
        {
            if (Memo.TryGetValue(key, out var v)) return (T)v;
            var nv = fn();
            if (Memo.Count > 600) Memo.Clear();
            Memo[key] = nv;
            return nv;
        }
    }

    private sealed class FitBlockResult
    {
        public string Text = "";
        public double Size;
        public int Lines;
        public double Score;
    }

        private static FitBlockResult FitBlock(string text, string font, double aw, double ah, JObject? o = null, int maxLines = 4)
    {
        var t = (text ?? "").Trim();
        double Num_(string k) => o?[k] is JValue v && v.Type is JTokenType.Float or JTokenType.Integer ? Convert.ToDouble(v.Value, CultureInfo.InvariantCulture) : 0;
        var key = string.Join("|", "fb", t, font, ((int)aw).ToString(CultureInfo.InvariantCulture),
            ((int)ah).ToString(CultureInfo.InvariantCulture),
            Num_("lead").ToString(CultureInfo.InvariantCulture), Num_("track").ToString(CultureInfo.InvariantCulture),
            JLayouts.Truthy(o?["vertical"]) ? "1" : "0", Num_("sx") != 0 ? Num_("sx").ToString(CultureInfo.InvariantCulture) : "1",
            maxLines.ToString(CultureInfo.InvariantCulture));
        return MemoGet(key, () =>
        {
            var n = Math.Max(1, JLayouts.GlyphCount(t));
            FitBlockResult? best = null;
            var seen = new HashSet<string>();
            for (var L = 1; L <= Math.Min(maxLines, n); L++)
            {
                var s = L == 1 ? t : Brk(t, (int)Math.Ceiling(n / (double)L));
                if (!seen.Add(s)) continue;
                var lines = s.Split('\n').Length;
                if (lines > maxLines) continue;
                var size = JLayouts.FitSize(s, font, aw, ah, o);
                var lone = lines > 1 && n > 2 && s.Split('\n').Any(l => JLayouts.GlyphCount(l) < 2);
                var score = size * (1 - 0.06 * (lines - 1)) * (lone ? 0.8 : 1);
                if (best == null || score > best.Score) best = new FitBlockResult { Text = s, Size = size, Lines = lines, Score = score };
            }
            return best ?? new FitBlockResult { Text = t, Size = JLayouts.FitSize(t, font, aw, ah, o), Lines = 1, Score = 0 };
        });
    }

    private static double RowW(string text, string font, double size)
    {
        var w = 0.0;
        foreach (var ch in JLayouts.CodePoints(text))
            w += JText.Metrics.Adv(font, ch) * size;
        return w;
    }

        private static void FastRow(Env env, string text, string font, double size, double x, double y, double sp, string color, double alpha, string align = "left")
        => JLayouts.FastRow(env, text, font, size, x, y, sp, color, alpha, align);

        private static void BodyRows(Env env, string src, string font, double fs, double x, double y, double w, int rows,
        double lh, string color, double alpha, int seed, double reveal = 1)
    {
        if (env.Pass != "main" || alpha <= 0.01 || rows <= 0 || w < fs * 2) return;
        var chars = JLayouts.CodePoints(src);
        if (chars.Count == 0) return;
        var adv = JText.Metrics.Adv(font, "あ");
        if (adv == 0) adv = 1;
        var cpr = Math.Max(2, (int)Math.Floor(w / (fs * adv * 1.04)));
        var off = (int)(seed % chars.Count);
        for (var r = 0; r < rows && r < 80; r++)
        {
            var vis = J.Clamp(reveal * rows * 1.2 - r);
            if (vis <= 0) break;
            var last = r == rows - 1 || J.R(seed, r, 5) < 0.14;
            var m = last ? Math.Max(2, (int)Math.Floor(cpr * J.Rr(0.3, 0.8, seed, r, 6))) : cpr;
            var row = "";
            for (var j = 0; j < m; j++) row += chars[(off + j) % chars.Count];
            off += m;
            var sp = last ? fs * 0.04 : (w - RowW(row, font, fs)) / Math.Max(1, m - 1);
            FastRow(env, row, font, fs, x, y + r * lh, sp, color, alpha * vis);
        }
    }

        private static void VBody(Env env, string src, string font, double fs, double xR, double y, double h, int cols,
        double pitch, string color, double alpha, int seed, double reveal = 1)
    {
        if (env.Pass != "main" || alpha <= 0.01 || cols <= 0) return;
        var chars = JLayouts.CodePoints(Strip(src)).Where(c => !J.IsLatin(c[0])).ToList();
        if (chars.Count == 0) return;
        var per = Math.Max(2, (int)Math.Floor(h / (fs * 1.02)));
        cols = Math.Min(cols, Math.Max(1, (int)Math.Floor(260.0 / per)));
        var off = (int)(seed % chars.Count);
        for (var c = 0; c < cols; c++)
        {
            var vis = J.Clamp(reveal * cols * 1.2 - c);
            if (vis <= 0) break;
            var last = c == cols - 1 || J.R(seed, c, 5) < 0.14;
            var m = last ? Math.Max(2, (int)Math.Floor(per * J.Rr(0.3, 0.8, seed, c, 6))) : per;
            var t = "";
            for (var j = 0; j < m; j++) t += chars[(off + j) % chars.Count];
            off += m;
            env.DrawItem(new JObject
            {
                ["text"] = t,
                ["font"] = font,
                ["size"] = fs,
                ["vertical"] = true,
                ["align"] = "left",
                ["x"] = xR - (c + 0.5) * pitch,
                ["y"] = y,
                ["color"] = color,
                ["alpha"] = alpha * vis,
                ["ghost"] = false,
            });
        }
    }

        private static void Greek(Env env, double x, double y, double w, double h, double lh, string col, double a,
        int seed, bool vertical = false, double reveal = 1)
    {
        if (env.Pass != "main" || a <= 0.01) return;
        var ctx = env.Ctx;
        ctx.Save();
        var g = lh / 1.45;         var n = Math.Min(160, (int)Math.Floor((vertical ? w : h) / lh));
        var span = vertical ? h : w;
        using var paint = new SKPaint
        {
            IsAntialias = true,
            IsStroke = true,
            StrokeWidth = (float)Math.Max(1, g * 0.72),
            Color = Env.SkColor(col, a),
            BlendMode = env.Blend,
            StrokeCap = SKStrokeCap.Butt,
            PathEffect = SKPathEffect.CreateDash(new[] { (float)(g * 0.78), (float)(g * 0.24) }, 0),
        };
        using var path = new SKPath();
        for (var r = 0; r < n; r++)
        {
            if (r >= n * reveal * 1.1) break;
            var endP = J.R(seed, r, 3) < 0.12;
            var ind = r == 0 || J.R(seed, r - 1, 3) < 0.12 ? g * 1.02 : 0;
            var len = (endP ? span * J.Rr(0.2, 0.75, seed, r, 4) : span) - ind;
            if (len <= g) continue;
            if (vertical)
            {
                var xx = x + w - (r + 0.5) * lh;
                path.MoveTo((float)xx, (float)(y + ind));
                path.LineTo((float)xx, (float)(y + ind + len));
            }
            else
            {
                var yy = y + (r + 0.5) * lh;
                path.MoveTo((float)(x + ind), (float)yy);
                path.LineTo((float)(x + ind + len), (float)yy);
            }
        }
        ctx.DrawPath(path, paint);
        ctx.Restore();
    }

        private static void Halftone(Env env, double x, double y, double w, double h, string col, string bgc, double a,
        int seed, int kind = 0)
    {
        if (env.Pass != "main" || a <= 0.01 || w < 4 || h < 4) return;
        var ctx = env.Ctx;
        ctx.Save();
        using (var bg = new SKPaint { IsAntialias = true, Color = Env.SkColor(bgc, a), BlendMode = env.Blend })
            ctx.DrawRect(new SKRect((float)x, (float)y, (float)(x + w), (float)(y + h)), bg);
        ctx.ClipRect(new SKRect((float)x, (float)y, (float)(x + w), (float)(y + h)));
        var d = Math.Max(4, Math.Min(w, h) / 22);
        var cols = Math.Min(60, (int)Math.Ceiling(w / d));
        var rows = Math.Min(60, (int)Math.Ceiling(h / d));
        var hx = 0.35 + 0.3 * J.R(seed, 1);
        const double hy = 0.36;
        using var p = new SKPaint { IsAntialias = true, Color = Env.SkColor(col, a), BlendMode = env.Blend };
        for (var j = 0; j <= rows; j++)
            for (var i = 0; i <= cols; i++)
            {
                var px = (i + (j % 2) * 0.5) / cols;
                var py = j / (double)rows;
                double v;
                if (kind == 0)                 {
                    var dh = Math.Sqrt(Math.Pow((px - hx) * w / h, 2) + Math.Pow(py - hy, 2)) / 0.2;
                    var ds = Math.Sqrt(Math.Pow((px - hx) * w / h * 0.55, 2) + Math.Pow((py - 1.05) / 0.9, 2)) / 0.36;
                    v = Math.Max(0, 1 - Math.Min(dh, ds) * 0.85) * 0.9 + 0.12 * (1 - py);
                }
                else                 {
                    v = py > 0.62
                        ? 0.75 - (py - 0.62)
                        : 0.18 + 0.5 * Math.Max(0, 1 - Math.Sqrt(Math.Pow(px - hx, 2) + Math.Pow(py - 0.4, 2)) / 0.18);
                }
                var r = d * 0.5 * Math.Sqrt(J.Clamp(v));
                if (r < 0.4) continue;
                var cx = x + px * w;
                var cy = y + py * h;
                ctx.DrawCircle((float)cx, (float)cy, (float)r, p);
            }
        ctx.Restore();
    }

    private static List<double[]> CloseLoop(IEnumerable<double[]> pts)
    {
        var arr = pts.ToArray();
        var o = new List<double[]>(arr) { arr[0], arr[1] };
        return o;
    }

        private static List<double[]> PenEllipse(double cx, double cy, double rx, double ry, int seed, double turns = 1.12, double a0 = -2.2)
    {
        var pts = new List<double[]>();
        const int M = 40;
        for (var i = 0; i <= M; i++)
        {
            var u = i / (double)M;
            var a = a0 + u * J.TAU * turns;
            var k = 1 + J.Rs(seed, i >> 2, 7) * 0.05 + u * 0.06;
            pts.Add(new[] { cx + Math.Cos(a) * rx * k, cy + Math.Sin(a) * ry * k });
        }
        return pts;
    }

        private sealed class GlyphPt
    {
        public string Ch = "";
        public double X, Y, W, H;
        public int Li, I;
    }

    private static List<GlyphPt> GlyphPts(JObject it)
    {
        var lay = JText.LayoutText(it);
        var sx = JLayouts.D(it, "sx");
        if (sx == 0) sx = 1;
        var sy = JLayouts.D(it, "sy");
        if (sy == 0) sy = 1;
        var x0 = JLayouts.D(it, "x");
        var y0 = JLayouts.D(it, "y");
        var @out = new List<GlyphPt>();
        foreach (var g in lay.Cells)
        {
            if (g.Ch == " " || g.Ch == "　") continue;
            @out.Add(new GlyphPt
            {
                Ch = g.Ch,
                X = x0 + (g.X + g.Vx) * sx,
                Y = y0 + (g.Y + g.Vy) * sy,
                W = g.W * sx,
                H = g.H * sy,
                Li = g.Li,
                I = @out.Count,
            });
        }
        return @out;
    }

        private static void ArcText(Env env, string text, string font, double size, double cx, double cy, double R,
        double midDeg, string color, double alpha, double track = 0.1, bool inward = false)
    {
        if (string.IsNullOrEmpty(text) || alpha <= 0.01) return;
        var it = new JObject
        {
            ["text"] = text,
            ["font"] = font,
            ["size"] = size,
            ["x"] = 0,
            ["y"] = 0,
            ["track"] = track,
            ["ghost"] = false,
            ["color"] = color,
            ["alpha"] = alpha,
        };
        var lay = JText.LayoutText(it);
        JText.ShareLay(it, lay);
        var total = lay.W;
        env.CharFn = (i, g, n) =>
        {
            var gx = g != null ? JLayouts.D(g, "x") : 0;
            var gy = g != null ? JLayouts.D(g, "y") : 0;
            var s = (gx + total / 2) - total / 2;             var ang = midDeg * J.DEG + (inward ? -s : s) / R;
            var x = cx + Math.Cos(ang) * R;
            var y = cy + Math.Sin(ang) * R;
            return new CharXf { Dx = x - gx, Dy = y - gy, Rot = ang / J.DEG + (inward ? -90 : 90) };
        };
        env.DrawItem(it);
        env.CharFn = null;     }

        private static void Leader(Env env, double x0, double x1, double y, string col, double a, double step, double r)
    {
        if (env.Pass != "main" || a <= 0.01 || x1 - x0 < step) return;
        var ctx = env.Ctx;
        ctx.Save();
        using var p = new SKPaint { IsAntialias = true, Color = Env.SkColor(col, a), BlendMode = env.Blend };
        var n = Math.Min(200, (int)Math.Floor((x1 - x0) / step));
        for (var i = 0; i <= n; i++)
        {
            var x = x0 + (x1 - x0) * (n != 0 ? i / (double)n : 0);
            ctx.DrawCircle((float)x, (float)y, (float)r, p);
        }
        ctx.Restore();
    }

    private static void LeaderV(Env env, double x, double y0, double y1, string col, double a, double step, double r)
    {
        if (env.Pass != "main" || a <= 0.01 || y1 - y0 < step) return;
        var ctx = env.Ctx;
        ctx.Save();
        using var p = new SKPaint { IsAntialias = true, Color = Env.SkColor(col, a), BlendMode = env.Blend };
        var n = Math.Min(200, (int)Math.Floor((y1 - y0) / step));
        for (var i = 0; i <= n; i++)
        {
            var y = y0 + (y1 - y0) * (n != 0 ? i / (double)n : 0);
            ctx.DrawCircle((float)x, (float)y, (float)r, p);
        }
        ctx.Restore();
    }

        private static void Stripes(Env env, double x, double y, double w, double h, string? c1, string c2, double sw,
        double ang = 45, double a = 1, double off = 0)
    {
        if (env.Pass != "main" || a <= 0.01 || w <= 0 || h <= 0) return;
        var ctx = env.Ctx;
        ctx.Save();
        ctx.ClipRect(new SKRect((float)x, (float)y, (float)(x + w), (float)(y + h)));
        if (!string.IsNullOrEmpty(c1))
            using (var p = new SKPaint { IsAntialias = true, Color = Env.SkColor(c1!, a), BlendMode = env.Blend })
                ctx.DrawRect(new SKRect((float)x, (float)y, (float)(x + w), (float)(y + h)), p);
        var t = Math.Tan(ang * J.DEG);
        var n = Math.Min(160, (int)Math.Ceiling((w + h * Math.Abs(t)) / (sw * 2)) + 2);
        var o = ((off % (sw * 2)) + sw * 2) % (sw * 2);
        using var paint = new SKPaint { IsAntialias = true, Color = Env.SkColor(c2, a), BlendMode = env.Blend };
        using var path = new SKPath();
        for (var i = -1; i < n; i++)
        {
            var x0 = x - h * Math.Abs(t) + i * sw * 2 + o;
            path.MoveTo((float)x0, (float)(y + h));
            path.LineTo((float)(x0 + sw), (float)(y + h));
            path.LineTo((float)(x0 + sw + h * t), (float)y);
            path.LineTo((float)(x0 + h * t), (float)y);
            path.Close();
        }
        ctx.DrawPath(path, paint);
        ctx.Restore();
    }

        private static void Tombo(Env env, double x0, double y0, double x1, double y1, double g, double L, string col,
        double lw, double a)
    {
        if (a <= 0.01) return;
        foreach (var c in new[]
        {
            new[] { x0, y0, -1.0, -1.0 }, new[] { x1, y0, 1.0, -1.0 }, new[] { x0, y1, -1.0, 1.0 }, new[] { x1, y1, 1.0, 1.0 }
        })
        {
            double x = c[0], y = c[1], dx = c[2], dy = c[3];
            env.Line(new[] { new[] { x + dx * g, y }, new[] { x + dx * (g + L), y } }, col, lw, a, false);
            env.Line(new[] { new[] { x + dx * g * 1.8, y + dy * g * 0.8 }, new[] { x + dx * (g + L), y + dy * g * 0.8 } }, col, lw, a, false);
            env.Line(new[] { new[] { x, y + dy * g }, new[] { x, y + dy * (g + L) } }, col, lw, a, false);
            env.Line(new[] { new[] { x + dx * g * 0.8, y + dy * g * 1.8 }, new[] { x + dx * g * 0.8, y + dy * (g + L) } }, col, lw, a, false);
        }
        var cx = (x0 + x1) / 2;
        var cy = (y0 + y1) / 2;
        foreach (var c in new[]
        {
            new[] { cx, y0 - g - L / 2, 0.0 }, new[] { cx, y1 + g + L / 2, 0.0 },
            new[] { x0 - g - L / 2, cy, 1.0 }, new[] { x1 + g + L / 2, cy, 1.0 }
        })
        {
            double x = c[0], y = c[1], v = c[2];
            if (v != 0)
            {
                env.Line(new[] { new[] { x - L / 2, y }, new[] { x + L / 2, y } }, col, lw, a, false);
                env.Line(new[] { new[] { x, y - L * 0.9 }, new[] { x, y + L * 0.9 } }, col, lw, a, false);
            }
            else
            {
                env.Line(new[] { new[] { x - L * 0.9, y }, new[] { x + L * 0.9, y } }, col, lw, a, false);
                env.Line(new[] { new[] { x, y - L / 2 }, new[] { x, y + L / 2 } }, col, lw, a, false);
            }
        }
    }

    private static void RegisterMagazine()
    {
        RegC("magazine", new LayoutDef
        {
            Name = "見開き",
            Tags = new[] { "editorial", "calm", "emotional" },
            W = 0.9,
            Treat = "safe",
            Fits = n => n <= 18,
            EnterBias = new JObject { ["blur"] = 1.3, ["wipe"] = 1.3, ["type"] = 1.2, ["cut"] = 1.2 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display", "serif", "serif")),
                ["qf"] = rng.Pick(FontsOf(st, "serif")),
                ["variant"] = rng.Pick(new[] { "headline", "vertical", "plate" }),
                ["folio"] = 2 * rng.Int(6, 90),
                ["kicker"] = rng.Pick(new[] { "FEATURE", "ESSAY", "INTERVIEW", "COLUMN", "STORY" }),
                ["seed"] = rng.Int(1, 9999),
                ["img"] = rng.Pick(new[] { "sun", "bars", "arc" }),
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var ctx = env.Ctx;
                var p = env.P;
                var u = U(env);
                var port = IsPort(env);
                var C = Card(sc);
                double pw, ph;
                if (!port)
                {
                    ph = Math.Min(H * 0.84, W * 0.44 / 0.72);
                    pw = ph * 0.72;
                }
                else
                {
                    pw = W * 0.88;
                    ph = Math.Min(H * 0.445, pw * 1.02);
                }
                var cx = W / 2;
                var cy = H / 2;
                var vert = JLayouts.S(p, "variant") == "vertical" && !HasLatin(env.Cut.Text);
                double ax, ay;
                if (port) { ax = cx - pw / 2; ay = cy - ph; }
                else if (vert) { ax = cx; ay = cy - ph / 2; }
                else { ax = cx - pw; ay = cy - ph / 2; }
                var sideB = port ? 1 : vert ? -1 : 1;                 var outK = 1 - J.Clamp((env.POut - 0.72) / 0.28);
                var aA = Tin(env, 0, 0.28, J.E.OutCubic) * outK;
                var uf = J.E.InOutCubic(J.Clamp((env.Lt - 0.06) / 0.5));
                var fold = J.E.InOutCubic(env.POut);
                var q = uf * (1 - 2 * fold);                 var ls = J.Clamp(pw * 0.03, 11, 26);
                var mx = pw * 0.1;
                var my = ph * 0.075;
                var lw = Math.Max(1, u * 0.0014);
                if (aA > 0.01)
                {
                    var sAx = port ? pw : pw * (1 + Math.Max(0, q));
                    var sx0 = port ? ax : sideB > 0 ? ax : ax - pw * Math.Max(0, q);
                    var sh0 = port ? ph * (1 + Math.Max(0, q)) : ph;
                    ShadowR(env, sx0, ay, sAx, sh0, 0, aA, u * 0.016);
                    env.Rect(ax, ay, pw, ph, C.Fill, aA, false);
                    if (C.Edge)
                        env.Line(new[] { new[] { ax, ay }, new[] { ax + pw, ay }, new[] { ax + pw, ay + ph }, new[] { ax, ay + ph }, new[] { ax, ay } },
                            C.Line, lw, aA * 0.7, false);
                }
                var gx = cx;
                var gy = cy;
                if (q > 0.002 && aA > 0.01)
                {
                    ctx.Save();
                    if (port)
                    {
                        ctx.Translate((float)ax, (float)gy);
                        ctx.Scale(1, (float)q);
                    }
                    else
                    {
                        ctx.Translate((float)gx, (float)ay);
                        ctx.Scale((float)(q * sideB), 1);
                    }
                    var bw = pw;
                    var bh = ph;                     env.Rect(0, 0, bw, bh, C.Fill, aA, false);
                    if (C.Edge)
                        env.Line(new[] { new[] { 0.0, 0.0 }, new[] { bw, 0.0 }, new[] { bw, bh }, new[] { 0.0, bh } }, C.Line, lw, aA * 0.7, false);
                    if (!port && sideB < 0)
                    {
                        ctx.Scale(-1, 1);
                        ctx.Translate((float)-bw, 0);
                    }                     var ca = aA * J.Clamp((q - 0.3) / 0.5);
                    if (ca > 0.01)
                    {
                        var bx = mx;
                        var by = my;
                        var iw = bw - mx * 2;
                        var ih = bh - my * 2;
                        var imgH = ih * (JLayouts.S(p, "variant") == "plate" ? 0.3 : 0.46);
                        var imgKind = JLayouts.S(p, "img");
                        var imgC = PlateCol(sc, new[] { imgKind == "sun" ? Darkest(sc) : sc.Str("accent"), sc.Str("ink"), sc.Str("fg") }, C.Fill, 1.8);
                        var iy = JLayouts.S(p, "variant") == "plate" ? by + ih - imgH : by;
                        env.Rect(bx, iy, iw, imgH, imgC, ca, false);
                        var oc = PlateCol(sc, new[] { sc.Str("accent"), sc.Str("accent2"), C.Fill }, imgC, 1.6);
                        if (imgKind == "sun") env.Circle(bx + iw * 0.62, iy + imgH * 0.52, Math.Min(iw, imgH) * 0.3, oc, null, 0, ca, false);
                        else if (imgKind == "bars")
                            for (var i = 0; i < 5; i++)
                                env.Rect(bx + iw * (0.12 + i * 0.16), iy + imgH * (0.25 + 0.5 * J.R(JLayouts.I(p, "seed"), i, 9)), iw * 0.08,
                                    imgH * 0.75 * (1 - 0.5 * J.R(JLayouts.I(p, "seed"), i, 9)), oc, ca, false);
                        else env.Arc(bx + iw * 0.5, iy + imgH * 1.02, imgH * 0.7, 180, 360, oc, Math.Max(3, imgH * 0.09), ca, false);
                        var fs = J.Clamp(pw * 0.022, 9, 20);
                        var lh = fs * 1.75;
                        var src = Flat(env.Cut.LineText.Length > 0 ? env.Cut.LineText : env.Cut.Text) + (HasLatin(env.Cut.Text) ? ". " : "。");
                        var colW = (iw - fs * 1.5) / 2;
                        var ty = JLayouts.S(p, "variant") == "plate" ? by + ls * 2 : iy + imgH + fs * 2;
                        var tAvail = JLayouts.S(p, "variant") == "plate" ? ih - imgH - ls * 3 : ih - imgH - fs * 2;
                        var qc = DeckCopy(env);
                        if (JLayouts.S(p, "variant") == "plate" && qc != null)
                        {
                            var qt = "「" + qc + "」";
                            var qb = FitBlock(qt, JLayouts.S(p, "qf")!, iw, tAvail * 0.42, new JObject { ["lead"] = 1.3 }, 3);
                            var qs = Math.Min(qb.Size, pw * 0.07);
                            env.DrawItem(new JObject
                            {
                                ["text"] = qb.Text,
                                ["font"] = JLayouts.S(p, "qf"),
                                ["size"] = qs,
                                ["lead"] = 1.3,
                                ["align"] = "left",
                                ["x"] = bx,
                                ["y"] = ty + qs * qb.Lines * 0.65,
                                ["color"] = C.Acc,
                                ["alpha"] = ca,
                                ["ghost"] = false,
                            });
                            var r0 = ty + qs * (qb.Lines * 1.3 + 0.8);
                            var rows = Math.Max(0, (int)Math.Floor((by + ih - imgH - fs - r0) / lh));
                            BodyRows(env, src, BodyF(env), fs, bx, r0, colW, rows, lh, C.Text, ca * 0.55, JLayouts.I(p, "seed"), uf);
                            BodyRows(env, src, BodyF(env), fs, bx + colW + fs * 1.5, r0, colW, rows, lh, C.Text, ca * 0.55, JLayouts.I(p, "seed") + 77, uf);
                        }
                        else
                        {
                            var rows = Math.Max(0, (int)Math.Floor((JLayouts.S(p, "variant") == "plate" ? ih - imgH - fs * 2 : tAvail) / lh));
                            BodyRows(env, src, BodyF(env), fs, bx, ty, colW, rows, lh, C.Text, ca * 0.55, JLayouts.I(p, "seed"), uf);
                            BodyRows(env, src, BodyF(env), fs, bx + colW + fs * 1.5, ty, colW, rows, lh, C.Text, ca * 0.55, JLayouts.I(p, "seed") + 77, uf);
                        }
                        env.DrawItem(new JObject
                        {
                            ["text"] = Pad3(JLayouts.I(p, "folio") + 1),
                            ["font"] = MonoF(env),
                            ["size"] = ls * 0.85,
                            ["align"] = "right",
                            ["x"] = bw - mx * 0.5,
                            ["y"] = bh - my * 0.45,
                            ["color"] = C.Text,
                            ["alpha"] = ca * 0.8,
                            ["ghost"] = false,
                        });
                    }
                    if (q < 0.98) env.Rect(0, 0, bw, bh, Darkest(sc), aA * (1 - q) * 0.35, false);
                    ctx.Restore();
                }
                if (env.Pass == "main" && aA > 0.01)
                {
                    var gw = pw * 0.07;
                    ctx.Save();
                    using var paint = new SKPaint { IsAntialias = true, BlendMode = env.Blend };
                    var p0 = port ? new SKPoint(0, (float)(gy - gw)) : new SKPoint((float)(gx - gw), 0);
                    var p1 = port ? new SKPoint(0, (float)(gy + gw)) : new SKPoint((float)(gx + gw), 0);
                    paint.Shader = SKShader.CreateLinearGradient(p0, p1, new[]
                    {
                        Env.SkColor(C.Text, 0), Env.SkColor(C.Text, 0.28 * (aA * 0.5)), Env.SkColor(C.Text, 0)
                    }, new[] { 0f, 0.5f, 1f }, SKShaderTileMode.Clamp);
                    if (port) ctx.DrawRect(new SKRect((float)ax, (float)(gy - gw), (float)(ax + pw), (float)(gy + gw)), paint);
                    else ctx.DrawRect(new SKRect((float)(gx - gw), (float)ay, (float)(gx + gw), (float)(ay + ph)), paint);
                    ctx.Restore();
                }
                Func<BBox?> drawA = () =>
                {
                    var fa = Tin(env, 0.12, 0.4, J.E.OutCubic) * outK;
                    var axx = ax + mx;
                    var ayy = ay + my;
                    var aw = pw - mx * 2;
                    var ah = ph - my * 2;
                    BBox? bb = null;
                    var t0 = env.Cut.Text.Trim();
                    if (JLayouts.S(p, "variant") == "plate")
                    {
                        var plate = PlateCol(sc, new[] { Darkest(sc), sc.Str("accent"), sc.Str("ink") }, C.Fill, 2.2);
                        var pe = J.E.InOutCubic(J.Clamp(env.Lt / 0.45)) * outK;
                        if (pe > 0) env.Rect(ax, ay + ph * (1 - pe), pw, ph * pe, plate, aA, false);
                        var tc = OnCol(sc, plate);
                        var stripped = Strip(t0);
                        var big = stripped.Length > 0 ? stripped[0].ToString() : "";
                        env.DrawItem(new JObject
                        {
                            ["text"] = big,
                            ["font"] = JLayouts.S(p, "font"),
                            ["size"] = ph * 0.7,
                            ["x"] = ax + pw * 0.62,
                            ["y"] = ay + ph * 0.36,
                            ["color"] = tc,
                            ["alpha"] = 0.07 * pe,
                            ["ghost"] = false,
                        });
                        env.DrawItem(new JObject
                        {
                            ["text"] = JLayouts.S(p, "kicker") + "  —  No." + LineNo(env),
                            ["font"] = MonoF(env),
                            ["size"] = ls,
                            ["track"] = 0.2,
                            ["align"] = "left",
                            ["x"] = axx,
                            ["y"] = ayy + ls * 0.5,
                            ["color"] = tc,
                            ["alpha"] = fa,
                            ["ghost"] = false,
                        });
                        var fb = FitBlock(t0, JLayouts.S(p, "font")!, aw, ah * 0.5, new JObject { ["lead"] = 1.12, ["track"] = 0.02 }, 4);
                        var size = Math.Min(fb.Size, u * 0.15);
                        var m = Meas(fb.Text, JLayouts.S(p, "font")!, size, new JObject { ["lead"] = 1.12, ["track"] = 0.02 });
                        bb = JLayouts.MainDraw(env, new JObject
                        {
                            ["text"] = fb.Text,
                            ["font"] = JLayouts.S(p, "font"),
                            ["size"] = size,
                            ["x"] = axx,
                            ["y"] = ay + ph - my - ls * 2 - m.H / 2,
                            ["align"] = "left",
                            ["lead"] = 1.12,
                            ["track"] = 0.02,
                            ["color"] = tc,
                            ["noHold"] = PlateHold(env),
                        });
                        env.DrawItem(new JObject
                        {
                            ["text"] = Pad3(JLayouts.I(p, "folio")),
                            ["font"] = MonoF(env),
                            ["size"] = ls * 0.85,
                            ["align"] = "left",
                            ["x"] = ax + mx * 0.5,
                            ["y"] = ay + ph - my * 0.45,
                            ["color"] = tc,
                            ["alpha"] = fa * 0.8,
                            ["ghost"] = false,
                        });
                        return bb ?? Box(axx, ay + ph * 0.5, axx + aw, ay + ph - my);
                    }
                    if (vert)
                    {
                        var fb = FitBlock(Strip(t0), JLayouts.S(p, "font")!, aw * (port ? 0.6 : 0.55), ah * 0.86,
                            new JObject { ["vertical"] = true, ["lead"] = 1.25, ["track"] = 0.04 }, 3);
                        var size = Math.Min(fb.Size, u * 0.15);
                        var m = Meas(fb.Text, JLayouts.S(p, "font")!, size, new JObject { ["vertical"] = true, ["lead"] = 1.25, ["track"] = 0.04 });
                        var x = axx + aw - m.W / 2;
                        var top = ayy + ls * 2.2;
                        bb = JLayouts.MainDraw(env, new JObject
                        {
                            ["text"] = fb.Text,
                            ["font"] = JLayouts.S(p, "font"),
                            ["size"] = size,
                            ["x"] = x,
                            ["y"] = top,
                            ["vertical"] = true,
                            ["align"] = "left",
                            ["lead"] = 1.25,
                            ["track"] = 0.04,
                            ["color"] = C.Text,
                            ["noHold"] = PlateHold(env),
                        });
                        env.DrawItem(new JObject
                        {
                            ["text"] = JLayouts.S(p, "kicker"),
                            ["font"] = MonoF(env),
                            ["size"] = ls,
                            ["track"] = 0.2,
                            ["align"] = "right",
                            ["x"] = axx + aw,
                            ["y"] = ayy + ls * 0.5,
                            ["color"] = C.Acc,
                            ["alpha"] = fa,
                            ["ghost"] = false,
                        });
                        var gw2 = aw - m.W - size * 0.8;
                        var gh = ah * 0.55;
                        var vfs = J.Clamp(pw * 0.024, 9, 20);
                        if (gw2 > ls * 3)
                            VBody(env, env.Cut.LineText.Length > 0 ? env.Cut.LineText : t0, BodyF(env), vfs, axx + gw2, ayy + ah - gh, gh,
                                (int)Math.Floor(gw2 / (vfs * 1.7)), vfs * 1.7, C.Text, fa * 0.55, JLayouts.I(p, "seed"), fa);
                        env.Line(new[] { new[] { axx, ayy + ah - gh - ls }, new[] { axx + gw2 * fa, ayy + ah - gh - ls } },
                            C.Acc, Math.Max(2, lw * 2), fa, false);
                        env.DrawItem(new JObject
                        {
                            ["text"] = Pad3(JLayouts.I(p, "folio")),
                            ["font"] = MonoF(env),
                            ["size"] = ls * 0.85,
                            ["align"] = "right",
                            ["x"] = ax + pw - mx * 0.5,
                            ["y"] = ay + ph - my * 0.45,
                            ["color"] = C.Text,
                            ["alpha"] = fa * 0.8,
                            ["ghost"] = false,
                        });
                        return bb ?? Box(x - m.W / 2, top, x + m.W / 2, top + m.H);
                    }
                    env.Rect(axx, ayy + ls * 0.1, ls * 0.8, ls * 0.8, C.Acc, fa, false);
                    env.DrawItem(new JObject
                    {
                        ["text"] = JLayouts.S(p, "kicker") + "  No." + LineNo(env),
                        ["font"] = MonoF(env),
                        ["size"] = ls,
                        ["track"] = 0.2,
                        ["align"] = "left",
                        ["x"] = axx + ls * 1.4,
                        ["y"] = ayy + ls * 0.5,
                        ["color"] = C.Text,
                        ["alpha"] = fa,
                        ["ghost"] = false,
                    });
                    var fb3 = FitBlock(t0, JLayouts.S(p, "font")!, aw, ah * 0.52, new JObject { ["lead"] = 1.1, ["track"] = 0.01 }, 4);
                    var size3 = Math.Min(fb3.Size, u * 0.15);
                    var m3 = Meas(fb3.Text, JLayouts.S(p, "font")!, size3, new JObject { ["lead"] = 1.1, ["track"] = 0.01 });
                    var hy = ayy + ls * 2.4 + m3.H / 2;
                    bb = JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = fb3.Text,
                        ["font"] = JLayouts.S(p, "font"),
                        ["size"] = size3,
                        ["x"] = axx,
                        ["y"] = hy,
                        ["align"] = "left",
                        ["lead"] = 1.1,
                        ["track"] = 0.01,
                        ["color"] = C.Text,
                        ["noHold"] = PlateHold(env),
                    });
                    var ry = hy + m3.H / 2 + size3 * 0.35;
                    var re = Tin(env, 0.15, 0.6, J.E.InOutCubic) * outK;
                    env.Line(new[] { new[] { axx, ry }, new[] { axx + aw * re, ry } }, C.Text, Math.Max(2, lw * 2), 0.9 * aA, false);
                    var deck = JLayouts.OrStr(DeckCopy(env), MetaLine(env))!;
                    var ds = J.Clamp(pw * 0.034, 11, 30);
                    var dper = Math.Max(4, (int)Math.Floor(aw / (ds * 1.05)));
                    var dt = string.Join("\n", JLayouts.SplitLines(deck, dper).Split('\n').Take(3));
                    env.DrawItem(new JObject
                    {
                        ["text"] = dt,
                        ["font"] = SerifF(env),
                        ["size"] = ds,
                        ["lead"] = 1.55,
                        ["align"] = "left",
                        ["x"] = axx,
                        ["y"] = ry + ds * 1.2 + (dt.Split('\n').Length - 1) * ds * 0.78,
                        ["color"] = C.Text,
                        ["alpha"] = fa * 0.75,
                        ["ghost"] = false,
                    });
                    env.DrawItem(new JObject
                    {
                        ["text"] = Pad3(JLayouts.I(p, "folio")) + "   " + Sl(JLayouts.OrStr(RomajiOf(env), "JIZURA")!, 18),
                        ["font"] = MonoF(env),
                        ["size"] = ls * 0.85,
                        ["track"] = 0.1,
                        ["align"] = "left",
                        ["x"] = ax + mx * 0.5,
                        ["y"] = ay + ph - my * 0.45,
                        ["color"] = C.Text,
                        ["alpha"] = fa * 0.8,
                        ["ghost"] = false,
                    });
                    return bb ?? Box(axx, hy - m3.H / 2, axx + m3.W, hy + m3.H / 2);
                };
                var res = drawA();
                if (q < -0.002 && aA > 0.01)
                {
                    var k = -q;
                    ctx.Save();
                    if (port)
                    {
                        ctx.Translate((float)ax, (float)gy);
                        ctx.Scale(1, (float)-k);
                    }
                    else
                    {
                        ctx.Translate((float)gx, (float)ay);
                        ctx.Scale((float)(-k * sideB), 1);
                    }
                    env.Rect(0, 0, pw, ph, C.Fill, aA, false);
                    if (C.Edge)
                        env.Line(new[] { new[] { 0.0, 0.0 }, new[] { pw, 0.0 }, new[] { pw, ph }, new[] { 0.0, ph } }, C.Line, lw, aA * 0.7, false);
                    env.Rect(0, 0, pw, ph, Darkest(sc), aA * (1 - k) * 0.3, false);
                    ctx.Restore();
                }
                env.Body = res;
            },
        });
    }

        private static string Sl(string s, int n) => s.Length > n ? s[..n] : s;

    private static void RegisterHeadlineDeck()
    {
        RegC("headlineDeck", new LayoutDef
        {
            Name = "見出しとリード",
            Tags = new[] { "editorial", "graphic", "calm" },
            W = 1.1,
            Fits = n => n <= 22,
            EnterBias = new JObject { ["wipe"] = 1.4, ["slice"] = 1.2, ["stretch"] = 1.2 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display", "display", "serif")),
                ["variant"] = PortOf(cut)
                    ? rng.Pick(new[] { "top", "bottom" })
                    : rng.Pick(new[] { "top", "bottom", "split" }),
                ["kicker"] = rng.Pick(new[] { "特集", "FEATURE", "COVER STORY", "ESSAY", "連載", "REPORT" }),
                ["mark"] = rng.Pick(new[] { "none", "bar", "none", "dot" }),
                ["dbl"] = rng.Chance(0.5),
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var p = env.P;
                var u = U(env);
                var port = IsPort(env);
                var outK = Tout(env);
                var mx = W * (port ? 0.08 : 0.075);
                var aw = W - mx * 2;
                var split = JLayouts.S(p, "variant") == "split" && !port;
                var hw = split ? aw * 0.6 : aw;
                var t0 = env.Cut.Text.Trim();
                var fb = FitBlock(t0, JLayouts.S(p, "font")!, hw, H * (split ? 0.62 : 0.46),
                    new JObject { ["lead"] = 1.02, ["track"] = -0.01 }, port ? 5 : 3);
                var size = Math.Min(fb.Size, u * (port ? 0.27 : 0.24));
                var m = Meas(fb.Text, JLayouts.S(p, "font")!, size, new JObject { ["lead"] = 1.02, ["track"] = -0.01 });
                var ls = SmallSize(env) * 1.1;
                var ds = J.Clamp(u * 0.032, 16, 44);
                var dc = DeckCopy(env);
                var dw = split ? aw * 0.32 : Math.Min(aw, port ? aw : aw * 0.62);
                var dper = Math.Max(5, (int)Math.Floor(dw / (ds * (HasLatin(dc ?? "") ? 0.55 : 1.02))));
                var dlines = dc != null ? JLayouts.SplitLines(dc, dper).Split('\n').Take(4).ToArray() : new[] { MetaLine(env) };
                var dfont = dc != null ? SerifF(env) : MonoF(env);
                var dsz = dc != null ? ds : ls;
                var deckH = dlines.Length * ds * 1.6;
                var kickH = ls * 2.2;
                var lw = Math.Max(2, u * 0.003);
                double top, hy, ry = 0, dy;
                if (split)
                {
                    top = H / 2 - m.H / 2;
                    hy = H / 2;
                    dy = H / 2 - deckH / 2;
                }
                else if (JLayouts.S(p, "variant") == "bottom")
                {
                    var tot = deckH + ls * 1.6 + m.H + kickH;
                    top = Math.Max(H * 0.08, H * 0.9 - tot);
                    dy = top;
                    ry = top + deckH + ls * 0.6;
                    hy = ry + ls + kickH + m.H / 2;
                }
                else
                {
                    var tot = kickH + m.H + size * 0.3 + ls + deckH;
                    top = Math.Max(H * 0.07, (H - tot) / 2 - H * 0.03);
                    hy = top + kickH + m.H / 2;
                    ry = hy + m.H / 2 + size * 0.22;
                    dy = ry + ls * 1.2;
                }
                var ka = Tin(env, 0.05, 0.4) * outK;
                var ky = hy - m.H / 2 - ls * 1.1;
                var kt = JLayouts.S(p, "kicker") + "  " + LineNo(env);
                var km = Meas(kt, MonoF(env), ls, new JObject { ["track"] = 0.2 });
                if (ka > 0.01)
                {
                    env.Rect(mx, ky - ls * 0.8, (km.W + ls * 1.2) * ka, ls * 1.6, sc.Str("accent"), outK, false);
                    env.DrawItem(new JObject
                    {
                        ["text"] = kt,
                        ["font"] = MonoF(env),
                        ["size"] = ls,
                        ["track"] = 0.2,
                        ["align"] = "left",
                        ["x"] = mx + ls * 0.6,
                        ["y"] = ky,
                        ["color"] = OnCol(sc, sc.Str("accent")!),
                        ["alpha"] = J.Clamp(ka * 2 - 1),
                        ["ghost"] = false,
                    });
                }
                var hx = mx;
                var bb = JLayouts.MainDraw(env, new JObject
                {
                    ["text"] = fb.Text,
                    ["font"] = JLayouts.S(p, "font"),
                    ["size"] = size,
                    ["x"] = hx,
                    ["y"] = hy,
                    ["align"] = "left",
                    ["lead"] = 1.02,
                    ["track"] = -0.01,
                    ["color"] = sc.Str("fg"),
                });
                if (JLayouts.S(p, "mark") != "none" && bb != null)
                {
                    var lines = fb.Text.Split('\n');
                    var nL = lines.Length;
                    var lastW = RowW(lines[nL - 1], JLayouts.S(p, "font")!, size);
                    var e = Tin(env, env.Cut.InDur * 0.8, 0.45, J.E.InOutCubic) * outK;
                    var ly = hy + (nL - 1) / 2.0 * size * 1.02;
                    if (JLayouts.S(p, "mark") == "bar" && e > 0)
                        env.Rect(hx, ly + size * 0.5, lastW * e, Math.Max(4, size * 0.07), sc.Str("accent"), 1, false);
                    if (JLayouts.S(p, "mark") == "dot" && e > 0)
                        env.Circle(hx + lastW + size * 0.25, ly + size * 0.3, size * 0.09 * J.E.OutBack(e, 2), sc.Str("accent"), null, 0, 1, false);
                }
                var re = Tin(env, 0.1, 0.7, J.E.InOutCubic) * outK;
                if (split)
                {
                    var vx = mx + aw * 0.64;
                    env.Line(new[]
                    {
                        new[] { vx, H / 2 - Math.Max(m.H, deckH) / 2 * re },
                        new[] { vx, H / 2 + Math.Max(m.H, deckH) / 2 * re }
                    }, sc.Str("fg"), lw * 0.6, 0.8, false);
                    if (JLayouts.Truthy(p["dbl"])) env.Line(new[] { new[] { mx, H * 0.1 }, new[] { mx + aw * re, H * 0.1 } }, sc.Str("fg"), lw, 1, false);
                }
                else
                {
                    env.Line(new[] { new[] { mx, ry }, new[] { mx + aw * re, ry } }, sc.Str("fg"), lw, 1, false);
                    if (JLayouts.Truthy(p["dbl"]))
                        env.Line(new[] { new[] { mx, ry + lw * 2.5 }, new[] { mx + aw * re, ry + lw * 2.5 } }, sc.Str("fg"), lw * 0.4, 0.8, false);
                }
                var dx = split ? mx + aw * 0.68 : mx;
                for (var i = 0; i < dlines.Length; i++)
                {
                    var l = dlines[i];
                    var a = Tin(env, env.Cut.InDur * 0.6 + 0.08 * i, 0.4, J.E.OutCubic) * outK;
                    if (a <= 0.01) continue;
                    env.DrawItem(new JObject
                    {
                        ["text"] = l,
                        ["font"] = dfont,
                        ["size"] = dsz,
                        ["track"] = dc != null ? 0.0 : 0.12,
                        ["align"] = "left",
                        ["x"] = dx,
                        ["y"] = dy + ds * 0.8 + i * ds * 1.6 + (1 - a) * ds * 0.5,
                        ["color"] = i == 0 && dc != null ? sc.Str("fg") : sc.Str("sub"),
                        ["alpha"] = a,
                        ["ghost"] = false,
                    });
                }
                var by = split ? dy + deckH + ls * 1.5
                    : JLayouts.S(p, "variant") == "bottom" ? top - ls * 1.4 : dy + deckH + ls * 0.8;
                var ba = Tin(env, env.Cut.InDur + 0.15, 0.4, J.E.OutCubic) * outK;
                if (dc != null && by < H * 0.95 && by > H * 0.04)
                    env.DrawItem(new JObject
                    {
                        ["text"] = "— " + J.FmtTime(env.Cut.Start) + "  /  " +
                                   Sl(JLayouts.OrStr(RomajiOf(env), "No." + LineNo(env))!, 22),
                        ["font"] = MonoF(env),
                        ["size"] = ls * 0.85,
                        ["track"] = 0.12,
                        ["align"] = split ? "left" : "right",
                        ["x"] = split ? dx : mx + aw,
                        ["y"] = by,
                        ["color"] = sc.Str("sub"),
                        ["alpha"] = ba,
                        ["ghost"] = false,
                    });
                env.Body = bb ?? Box(hx, hy - m.H / 2, hx + m.W, hy + m.H / 2);
            },
        });
    }

    private static void RegisterContents()
    {
        RegC("contents", new LayoutDef
        {
            Name = "目次",
            Tags = new[] { "editorial", "calm" },
            W = 0.9,
            Fits = n => n >= 2 && n <= 20,
            EnterBias = new JObject { ["wipe"] = 1.5, ["type"] = 1.3, ["slice"] = 1.2 },
            Plan = (rng, cut, st) =>
            {
                var n = cut.N;
                var port = PortOf(cut);
                var k = n <= 4 ? 1 : n <= 9 ? 2 : n <= 14 ? 3 : 4;
                return new JObject
                {
                    ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
                    ["chunks"] = new JArray(SplitK(cut.Text, k, k)),
                    ["variant"] = port || HasLatin(cut.Text) ? "rows" : rng.Pick(new[] { "rows", "rows", "tate" }),
                    ["page0"] = rng.Int(3, 40),
                    ["step"] = rng.Int(6, 22),
                    ["mark"] = rng.Pick(new[] { "bar", "tri", "dot" }),
                    ["ctx"] = rng.Chance(0.75),
                };
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var p = env.P;
                var u = U(env);
                var port = IsPort(env);
                var chunks = (p["chunks"] as JArray)?.Select(t => (string?)t).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();
                if (chunks == null || chunks.Count == 0) chunks = new List<string> { env.Cut.Text.Trim() };
                var k = chunks.Count;
                var outK = Tout(env);
                var ls = SmallSize(env);
                var lw = Math.Max(1.2, u * 0.0016);
                var cur = Math.Min(k - 1, (int)Math.Floor(J.Clamp(env.Lt / Math.Max(0.3, env.Cut.Dur * 0.92)) * k));
                var pages = chunks.Select((c, i) => Pad3(JLayouts.I(p, "page0") + i * JLayouts.I(p, "step"))).ToList();
                var hasCtx = JLayouts.Truthy(p["ctx"]);
                var ctxRows = hasCtx
                    ? new[] { new[] { "序", "000" }, new[] { "終", Pad3(JLayouts.I(p, "page0") + k * JLayouts.I(p, "step") + 14) } }
                    : null;
                BBox? bb = null;
                if (JLayouts.S(p, "variant") == "tate")
                {
                    var cols = k + (ctxRows != null ? 2 : 0);
                    var colW = Math.Min(W * 0.86 / (cols + 1.0), u * 0.22);
                    var hTop = H * 0.14;
                    var hBot = H * 0.86;
                    var x0 = W / 2 + (cols + 1.2) * colW / 2;
                    var size = Math.Min(colW * 0.7, chunks.Min(c => (hBot - hTop) * 0.62 / Math.Max(1, JLayouts.GlyphCount(c)) / 1.03));
                    var ta = Tin(env, 0, 0.4, J.E.OutCubic) * outK;
                    env.DrawItem(new JObject
                    {
                        ["text"] = "目次",
                        ["font"] = SerifF(env),
                        ["size"] = colW * 0.5,
                        ["vertical"] = true,
                        ["align"] = "left",
                        ["track"] = 0.6,
                        ["x"] = x0 - colW * 0.45,
                        ["y"] = hTop,
                        ["color"] = sc.Str("fg"),
                        ["alpha"] = ta,
                        ["ghost"] = false,
                    });
                    var le = Tin(env, 0.05, 0.6, J.E.InOutCubic) * outK;
                    env.Line(new[] { new[] { x0 - colW * 1.05, hTop }, new[] { x0 - colW * 1.05, hTop + (hBot - hTop) * le } }, sc.Str("fg"), lw, 1, false);
                    var ci = 0;
                    double ColX(int c) => x0 - colW * 1.2 - (c + 0.5) * colW;
                    void DrawCtx(string lab, string pg, int c, double d)
                    {
                        var a = Tin(env, d, 0.4, J.E.OutCubic) * outK * 0.5;
                        env.DrawItem(new JObject
                        {
                            ["text"] = lab,
                            ["font"] = SerifF(env),
                            ["size"] = size * 0.6,
                            ["vertical"] = true,
                            ["align"] = "left",
                            ["x"] = ColX(c),
                            ["y"] = hTop,
                            ["color"] = sc.Str("sub"),
                            ["alpha"] = a,
                            ["ghost"] = false,
                        });
                        LeaderV(env, ColX(c), hTop + size * 1.2, hBot - ls * 3.5, sc.Str("sub")!, a, ls * 0.6, Math.Max(1, ls * 0.07));
                        env.DrawItem(new JObject
                        {
                            ["text"] = pg,
                            ["font"] = MonoF(env),
                            ["size"] = ls * 0.9,
                            ["vertical"] = true,
                            ["align"] = "left",
                            ["x"] = ColX(c),
                            ["y"] = hBot - ls * 2.7,
                            ["color"] = sc.Str("sub"),
                            ["alpha"] = a,
                            ["ghost"] = false,
                        });
                    }
                    if (ctxRows != null) DrawCtx(ctxRows[0][0], ctxRows[0][1], ci++, 0.05);
                    for (var i = 0; i < chunks.Count; i++)
                    {
                        var c = chunks[i];
                        var x = ColX(ci++);
                        var txt = Strip(c);
                        var isCur = i == cur;
                        var it = new JObject
                        {
                            ["text"] = txt,
                            ["font"] = JLayouts.S(p, "font"),
                            ["size"] = size,
                            ["x"] = x,
                            ["y"] = hTop,
                            ["vertical"] = true,
                            ["align"] = "left",
                            ["track"] = 0.03,
                            ["color"] = sc.Str("fg"),
                            ["mi"] = i * 3,
                        };
                        bb = JLayouts.UnionBb(bb, JLayouts.MainDraw(env, it));
                        var endY = hTop + JLayouts.GlyphCount(txt) * size * 1.03;
                        var a = Tin(env, 0.15 + i * 0.1, 0.5, J.E.OutCubic) * outK;
                        var ly = endY + size * 0.4;
                        var py = hBot - ls * 2.7;
                        LeaderV(env, x, ly, J.Lerp(ly, py - ls * 0.9, a), isCur ? sc.Str("accent")! : sc.Str("sub")!, a, ls * 0.6, Math.Max(1.2, ls * 0.08));
                        env.DrawItem(new JObject
                        {
                            ["text"] = pages[i],
                            ["font"] = MonoF(env),
                            ["size"] = ls,
                            ["vertical"] = true,
                            ["align"] = "left",
                            ["x"] = x,
                            ["y"] = py,
                            ["color"] = isCur ? sc.Str("accent") : sc.Str("fg"),
                            ["alpha"] = a,
                            ["ghost"] = false,
                        });
                        if (isCur)
                        {
                            var ma = J.Clamp(a * 2 - 0.5);
                            env.Rect(x - colW * 0.5, hTop - ls * 1.6, colW, ls * 0.35, sc.Str("accent"), ma, false);
                        }
                    }
                    if (ctxRows != null) DrawCtx(ctxRows[1][0], ctxRows[1][1], ci++, 0.1 + k * 0.1);
                    env.Body = bb ?? Box(ColX(cols - 1) - colW / 2, hTop, x0, hBot);
                    return;
                }
                var nRows = k + (ctxRows != null ? 2 : 0);
                var bw = W * (port ? 0.84 : 0.66);
                var bx = (W - bw) / 2;
                var numW = ls * 3.4;
                var pgW = ls * 5;
                var tw = bw - numW - pgW - ls * 2;
                var cxH = ls * 2.6;
                var rowH0 = Math.Min((H * 0.66 - (ctxRows != null ? cxH * 2 : 0)) / k, u * 0.26);
                var size2 = Math.Min(rowH0 * 0.64, chunks.Min(c => JLayouts.FitSize(c, JLayouts.S(p, "font")!, tw * 0.92, rowH0 * 0.7, new JObject { ["track"] = 0.03 })));
                var rowH = Math.Max(size2 * 1.62, ls * 2.4);
                var blockH = k * rowH + (ctxRows != null ? cxH * 2 : 0);
                var y0 = H / 2 - blockH / 2 + ls * 1.8;
                var ha = Tin(env, 0, 0.4, J.E.OutCubic) * outK;
                var hy = y0 - ls * 2.8;
                env.DrawItem(new JObject
                {
                    ["text"] = "目次",
                    ["font"] = SerifF(env),
                    ["size"] = ls * 2.1,
                    ["track"] = 0.5,
                    ["align"] = "left",
                    ["x"] = bx,
                    ["y"] = hy - ls * 0.2,
                    ["color"] = sc.Str("fg"),
                    ["alpha"] = ha,
                    ["ghost"] = false,
                });
                env.DrawItem(new JObject
                {
                    ["text"] = "CONTENTS",
                    ["font"] = MonoF(env),
                    ["size"] = ls * 0.85,
                    ["track"] = 0.3,
                    ["align"] = "right",
                    ["x"] = bx + bw,
                    ["y"] = hy,
                    ["color"] = sc.Str("sub"),
                    ["alpha"] = ha,
                    ["ghost"] = false,
                });
                var le2 = Tin(env, 0.05, 0.6, J.E.InOutCubic) * outK;
                env.Line(new[] { new[] { bx, hy + ls * 1.4 }, new[] { bx + bw * le2, hy + ls * 1.4 } }, sc.Str("fg"), lw * 1.6, 1, false);
                env.Line(new[] { new[] { bx + bw, y0 + blockH + ls * 0.2 }, new[] { bx + bw - bw * le2, y0 + blockH + ls * 0.2 } }, sc.Str("fg"), lw, 0.7, false);
                var off0 = ctxRows != null ? cxH : 0;
                double RowY(int i) => y0 + off0 + (i + 0.5) * rowH;
                void DrawCtx2(string lab, string pg, double y, double d)
                {
                    var a = Tin(env, d, 0.4, J.E.OutCubic) * outK * 0.45;
                    env.DrawItem(new JObject
                    {
                        ["text"] = lab + "章",
                        ["font"] = SerifF(env),
                        ["size"] = ls * 1.1,
                        ["align"] = "left",
                        ["x"] = bx + numW,
                        ["y"] = y,
                        ["color"] = sc.Str("sub"),
                        ["alpha"] = a,
                        ["ghost"] = false,
                    });
                    Leader(env, bx + numW + ls * 3.4, bx + bw - pgW, y + ls * 0.3, sc.Str("sub")!, a, ls * 0.55, Math.Max(1, ls * 0.07));
                    env.DrawItem(new JObject
                    {
                        ["text"] = pg,
                        ["font"] = MonoF(env),
                        ["size"] = ls,
                        ["align"] = "right",
                        ["x"] = bx + bw,
                        ["y"] = y,
                        ["color"] = sc.Str("sub"),
                        ["alpha"] = a,
                        ["ghost"] = false,
                    });
                }
                if (ctxRows != null) DrawCtx2(ctxRows[0][0], ctxRows[0][1], y0 + cxH * 0.5, 0.05);
                for (var i = 0; i < chunks.Count; i++)
                {
                    var c = chunks[i];
                    var y = RowY(i);
                    var isCur = i == cur;
                    var a = Tin(env, 0.1 + i * 0.1, 0.5, J.E.OutCubic) * outK;
                    env.DrawItem(new JObject
                    {
                        ["text"] = Pad2(i + 1),
                        ["font"] = MonoF(env),
                        ["size"] = ls * 1.2,
                        ["align"] = "left",
                        ["x"] = bx,
                        ["y"] = y,
                        ["color"] = isCur ? sc.Str("accent") : sc.Str("sub"),
                        ["alpha"] = a,
                        ["ghost"] = false,
                    });
                    bb = JLayouts.UnionBb(bb, JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = c,
                        ["font"] = JLayouts.S(p, "font"),
                        ["size"] = size2,
                        ["x"] = bx + numW,
                        ["y"] = y,
                        ["align"] = "left",
                        ["track"] = 0.03,
                        ["color"] = sc.Str("fg"),
                        ["mi"] = i * 3,
                    }));
                    var tx1 = bx + numW + Meas(c, JLayouts.S(p, "font")!, size2, new JObject { ["track"] = 0.03 }).W + ls * 0.8;
                    var lx1 = bx + bw - pgW;
                    Leader(env, tx1, J.Lerp(tx1, lx1, a), y + size2 * 0.3, isCur ? sc.Str("accent")! : sc.Str("sub")!, a, ls * 0.55, Math.Max(1.2, ls * 0.08));
                    env.DrawItem(new JObject
                    {
                        ["text"] = pages[i],
                        ["font"] = MonoF(env),
                        ["size"] = ls * 1.4,
                        ["align"] = "right",
                        ["x"] = bx + bw,
                        ["y"] = y,
                        ["color"] = isCur ? sc.Str("accent") : sc.Str("fg"),
                        ["alpha"] = a,
                        ["ghost"] = false,
                    });
                    if (isCur)
                    {
                        var ma = J.Clamp(a * 2 - 0.4);
                        if (JLayouts.S(p, "mark") == "bar")
                            env.Rect(bx - ls * 1.1, y - size2 * 0.45, ls * 0.35, size2 * 0.9, sc.Str("accent"), ma, false);
                        else if (JLayouts.S(p, "mark") == "tri")
                            env.Poly(new[] { new[] { bx - ls * 1.3, y - ls * 0.5 }, new[] { bx - ls * 0.4, y }, new[] { bx - ls * 1.3, y + ls * 0.5 } },
                                sc.Str("accent"), ma, false);
                        else env.Circle(bx - ls * 0.9, y, ls * 0.3, sc.Str("accent"), null, 0, ma, false);
                    }
                }
                if (ctxRows != null) DrawCtx2(ctxRows[1][0], ctxRows[1][1], y0 + off0 + k * rowH + cxH * 0.5, 0.1 + k * 0.1);
                env.Body = bb ?? Box(bx, y0, bx + bw, y0 + blockH);
            },
        });
    }

    private static void RegisterFootnote()
    {
        RegC("footnote", new LayoutDef
        {
            Name = "脚注",
            Tags = new[] { "editorial", "calm", "emotional" },
            W = 0.9,
            Fits = n => n >= 2 && n <= 22,
            EnterBias = new JObject { ["blur"] = 1.3, ["type"] = 1.3, ["wipe"] = 1.2 },
            Plan = (rng, cut, st) =>
            {
                var n = cut.N;
                var k = n <= 3 ? 1 : n <= 8 ? 2 : 3;
                return new JObject
                {
                    ["font"] = rng.Pick(FontsOf(st, "serif", "display", "serif")),
                    ["chunks"] = new JArray(SplitK(cut.Text, k)),
                    ["marks"] = rng.Pick(new[] { "num", "kome", "star" }),
                    ["align"] = rng.Pick(new[] { "left", "center", "left" }),
                    ["top"] = rng.Chance(0.5),
                };
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var p = env.P;
                var u = U(env);
                var port = IsPort(env);
                var outK = Tout(env);
                var chunks = (p["chunks"] as JArray)?.Select(t => (string?)t).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();
                if (chunks == null || chunks.Count == 0) chunks = new List<string> { env.Cut.Text.Trim() };
                var k = chunks.Count;
                var latin = HasLatin(env.Cut.Text);
                var t0 = string.Join(latin ? " " : "", chunks);
                var mx = W * 0.09;
                var aw = W - mx * 2;
                var ls = SmallSize(env);
                var notesH = k * ls * 1.95 + ls * 2.2;
                var areaTop = H * 0.12;
                var areaBot = H - notesH - H * 0.1;
                var fb = FitBlock(t0, JLayouts.S(p, "font")!, aw * (port ? 0.95 : 0.88), (areaBot - areaTop) * 0.8,
                    new JObject { ["lead"] = 1.25, ["track"] = 0.03 }, port ? 5 : 3);
                var size = Math.Min(fb.Size, u * 0.17);
                var left = JLayouts.S(p, "align") == "left";
                var it = new JObject
                {
                    ["text"] = fb.Text,
                    ["font"] = JLayouts.S(p, "font"),
                    ["size"] = size,
                    ["x"] = left ? mx : W / 2,
                    ["y"] = (areaTop + areaBot) / 2,
                    ["align"] = left ? "left" : "center",
                    ["lead"] = 1.25,
                    ["track"] = 0.03,
                    ["color"] = sc.Str("fg"),
                };
                var bb = JLayouts.MainDraw(env, it);
                var gl = GlyphPts(it);
                string MarkOf(int i) => JLayouts.S(p, "marks") == "num" ? (i + 1).ToString(CultureInfo.InvariantCulture)
                    : JLayouts.S(p, "marks") == "kome" ? "※" + (i + 1) : "*" + (i + 1);
                var acc = 0;
                var ms = Math.Max(ls * 1.05, size * 0.3);
                for (var i = 0; i < chunks.Count; i++)
                {
                    acc += JLayouts.GlyphCount(chunks[i]);
                    if (gl.Count == 0) break;
                    var g = gl[Math.Min(gl.Count - 1, acc - 1)];
                    var t = env.Cut.InDur * 0.7 + 0.12 + i * 0.14;
                    var e = J.E.OutBack(J.Clamp((env.Lt - t) / 0.25), 2.2) * outK;
                    if (e <= 0.01) continue;
                    env.DrawItem(new JObject
                    {
                        ["text"] = MarkOf(i),
                        ["font"] = MonoF(env),
                        ["size"] = ms * e,
                        ["align"] = "left",
                        ["x"] = g.X + g.W * 0.5,
                        ["y"] = g.Y - size * 0.42,
                        ["color"] = sc.Str("accent"),
                        ["ghost"] = false,
                    });
                }
                var ny = H - notesH - H * 0.04 + ls;
                var re = Tin(env, env.Cut.InDur * 0.6, 0.5, J.E.InOutCubic) * outK;
                var rw = Math.Min(aw * 0.32, u * 0.5);
                env.Line(new[] { new[] { mx, ny }, new[] { mx + rw * re, ny } }, sc.Str("fg"), Math.Max(1.2, u * 0.0018), 0.9, false);
                var nfs = ls * 1.15;
                for (var i = 0; i < chunks.Count; i++)
                {
                    var c = chunks[i];
                    var a = Tin(env, env.Cut.InDur * 0.7 + 0.2 + i * 0.12, 0.4, J.E.OutCubic) * outK;
                    if (a <= 0.01) continue;
                    var y = ny + ls * 1.5 + i * ls * 1.95;
                    var rom = RomaOfText(c);
                    var note = Flat(c) + "　" + (rom ?? J.FmtTime(env.Cut.Start + env.Cut.Dur * i / k)) +
                        (i == k - 1 && env.Cut.LineText.Length > 0 && Strip(env.Cut.LineText) != Strip(env.Cut.Text)
                            ? "　／　" + Sl(Flat(env.Cut.LineText), 24) : "");
                    env.DrawItem(new JObject
                    {
                        ["text"] = MarkOf(i),
                        ["font"] = MonoF(env),
                        ["size"] = nfs,
                        ["align"] = "left",
                        ["x"] = mx + (1 - a) * ls,
                        ["y"] = y,
                        ["color"] = sc.Str("accent"),
                        ["alpha"] = a,
                        ["ghost"] = false,
                    });
                    env.DrawItem(new JObject
                    {
                        ["text"] = note,
                        ["font"] = BodyF(env),
                        ["size"] = nfs,
                        ["track"] = 0.05,
                        ["align"] = "left",
                        ["x"] = mx + ls * 2.4 + (1 - a) * ls,
                        ["y"] = y,
                        ["color"] = sc.Str("sub"),
                        ["alpha"] = a,
                        ["ghost"] = false,
                    });
                }
                var fa = Tin(env, 0.2, 0.4, J.E.OutCubic) * outK;
                env.DrawItem(new JObject
                {
                    ["text"] = "— " + Pad3(LineN(env) * 7 + 3) + " —",
                    ["font"] = MonoF(env),
                    ["size"] = ls * 0.8,
                    ["track"] = 0.2,
                    ["x"] = W / 2,
                    ["y"] = H - H * 0.045,
                    ["color"] = sc.Str("sub"),
                    ["alpha"] = fa * 0.7,
                    ["ghost"] = false,
                });
                if (JLayouts.Truthy(p["top"]))
                {
                    env.DrawItem(new JObject
                    {
                        ["text"] = "NOTES  " + LineNo(env),
                        ["font"] = MonoF(env),
                        ["size"] = ls * 0.8,
                        ["track"] = 0.3,
                        ["align"] = "right",
                        ["x"] = W - mx,
                        ["y"] = H * 0.06,
                        ["color"] = sc.Str("sub"),
                        ["alpha"] = fa * 0.7,
                        ["ghost"] = false,
                    });
                    env.Line(new[] { new[] { W - mx, H * 0.06 + ls }, new[] { W - mx - rw * re, H * 0.06 + ls } }, sc.Str("sub"), 1, 0.6, false);
                }
                env.Body = bb;
            },
        });
    }

        private static readonly Dictionary<string, string> ProofNotes = new()
    {
        ["circle"] = "ママ", ["wave"] = "強調", ["box"] = "太字", ["dots"] = "イキ",
    };

    private static void RegisterProofread()
    {
        RegC("proofread", new LayoutDef
        {
            Name = "校正刷り",
            Tags = new[] { "editorial", "graphic", "calm" },
            W = 0.8,
            Fits = n => n >= 2 && n <= 20,
            EnterBias = new JObject { ["type"] = 1.4, ["blur"] = 1.2, ["cut"] = 1.2 },
            Plan = (rng, cut, st) =>
            {
                var all = new[] { "circle", "wave", "box", "dots" };
                var first = rng.Pick(new[] { "circle", "circle", "box" });
                var rest = all.Where(m => m != first).ToArray();
                var marks = new JArray(first, rng.Pick(rest));
                return new JObject
                {
                    ["font"] = rng.Pick(FontsOf(st, "serif", "serif", "display")),
                    ["pen"] = rng.Chance(0.6) ? "klee" : rng.Pick(FontsOf(st, "body")),
                    ["marks"] = marks,
                    ["stamp"] = rng.Chance(0.65),
                    ["tombo"] = rng.Chance(0.8),
                    ["g1"] = rng.Int(0, 99),
                    ["g2"] = rng.Int(0, 99),
                    ["rot"] = rng.Range(-8, 8),
                };
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var ctx = env.Ctx;
                var p = env.P;
                var u = U(env);
                var port = IsPort(env);
                var outK = 1 - J.E.OutCubic(J.Clamp(env.POut * 1.15));                 var t0 = env.Cut.Text.Trim();
                var aw = W * (port ? 0.7 : 0.62);
                var ah = H * (port ? 0.34 : 0.4);
                var fb = FitBlock(t0, JLayouts.S(p, "font")!, aw, ah, new JObject { ["lead"] = 1.45, ["track"] = 0.06 }, port ? 5 : 3);
                var size = Math.Min(fb.Size, u * 0.14);
                var cx = W / 2 - (port ? 0 : W * 0.06);
                var cy = H / 2 + (port ? H * 0.02 : 0);
                var it = new JObject
                {
                    ["text"] = fb.Text,
                    ["font"] = JLayouts.S(p, "font"),
                    ["size"] = size,
                    ["x"] = cx,
                    ["y"] = cy,
                    ["lead"] = 1.45,
                    ["track"] = 0.06,
                    ["color"] = sc.Str("fg"),
                };
                var m = Meas(fb.Text, JLayouts.S(p, "font")!, size, new JObject { ["lead"] = 1.45, ["track"] = 0.06 });
                var lw = Math.Max(1, u * 0.0014);
                var pad = size * 0.75;
                var x0 = cx - m.W / 2 - pad;
                var x1 = cx + m.W / 2 + pad;
                var y0 = cy - m.H / 2 - pad;
                var y1 = cy + m.H / 2 + pad;
                if (JLayouts.Truthy(p["tombo"]))
                {
                    var te = Tin(env, 0, 0.5, J.E.OutCubic) * outK;
                    Tombo(env, x0, y0, x1, y1, size * 0.18, size * 0.55 * te, sc.Str("sub")!, lw, 0.8 * te);
                }
                var bb = JLayouts.MainDraw(env, it);
                var pen = PlateCol(sc, new[] { sc.Str("accent"), sc.Str("accent2"), sc.Str("fg") });
                var gl = GlyphPts(it);
                if (gl.Count == 0)
                {
                    env.Body = bb;
                    return;
                }
                var pw = Math.Max(2, size * 0.035);
                var ns = Math.Max(SmallSize(env) * 1.5, size * 0.42);
                var t1 = env.Cut.InDur * 0.8 + 0.1;
                var nChunks = env.Cut.Words.Length > 0 ? env.Cut.Words : new[] { t0 };
                var used = new HashSet<int>();
                GlyphPt PickGlyph(int seed, Func<char, bool> pred)
                {
                    var cand = gl.Where(g => pred(g.Ch[0]) && !used.Contains(g.I)).ToList();
                    var list = cand.Count > 0 ? cand : gl.Where(g => !used.Contains(g.I)).ToList();
                    var g = list.Count > 0 ? list[seed % list.Count] : gl[0];
                    used.Add(g.I);
                    return g;
                }
                var noteSide = port ? "top" : "right";
                var allMarks = ((JArray)p["marks"]!).Select(t => (string)t!).ToList();
                var marks = gl.Count <= 4 ? allMarks.Take(1).ToList() : allMarks;
                for (var mi = 0; mi < marks.Count; mi++)
                {
                    var mk = marks[mi];
                    var e = J.Clamp((env.Lt - t1 - mi * 0.35) / 0.45);
                    if (e <= 0) continue;
                    var a = outK;
                    var seed = mi != 0 ? JLayouts.I(p, "g2") : JLayouts.I(p, "g1");
                    double[]? anchor = null;
                    if (mk == "circle")
                    {
                        var g = PickGlyph(seed, c => J.IsKanji(c) || J.IsKata(c));
                        var pts = PenEllipse(g.X, g.Y, g.W * 0.72, g.H * 0.66, seed);
                        env.PolyPartial(pts.ToArray(), J.E.OutCubic(J.Clamp(e * 1.6)), pen, pw, a, false);
                        anchor = new[] { g.X + g.W * 0.5, g.Y - g.H * 0.55 };
                    }
                    else if (mk is "box" or "wave" or "dots")
                    {
                        var g0 = PickGlyph(seed, c => !J.IsPunct(c));
                        var want = Math.Max(2, Math.Min(4, JLayouts.GlyphCount(nChunks.Length > 0 ? nChunks[0] : "")));
                        var run = new List<GlyphPt> { g0 };
                        foreach (var g in gl)
                        {
                            if (run.Count >= want) break;
                            if (g.I > run[^1].I && g.Li == g0.Li && !used.Contains(g.I) && g.I == run[^1].I + 1) run.Add(g);
                        }
                        foreach (var g in run) used.Add(g.I);
                        var rx0 = run.Min(g => g.X - g.W / 2);
                        var rx1 = run.Max(g => g.X + g.W / 2);
                        var ry = g0.Y;
                        if (mk == "box")
                        {
                            var q = size * 0.14;
                            env.PolyPartial(new[]
                            {
                                new[] { rx0 - q, ry - size * 0.62 }, new[] { rx1 + q, ry - size * 0.6 },
                                new[] { rx1 + q * 0.8, ry + size * 0.6 }, new[] { rx0 - q * 1.1, ry + size * 0.62 },
                                new[] { rx0 - q, ry - size * 0.7 }
                            }, J.E.OutCubic(J.Clamp(e * 1.5)), pen, pw, a, false);
                            anchor = new[] { rx1 + q, ry - size * 0.6 };
                        }
                        else if (mk == "wave")
                        {
                            var pts = new List<double[]>();
                            const int M = 30;
                            var yy = ry + size * 0.66;
                            for (var i = 0; i <= M; i++)
                            {
                                var xx = J.Lerp(rx0, rx1, i / (double)M);
                                pts.Add(new[] { xx, yy + Math.Sin(i / (double)M * (rx1 - rx0) / (size * 0.18)) * size * 0.06 });
                            }
                            env.PolyPartial(pts.ToArray(), J.E.InOutCubic(J.Clamp(e * 1.5)), pen, pw, a, false);
                            anchor = new[] { rx1, yy };
                        }
                        else
                        {
                            var n2 = run.Count;
                            for (var j = 0; j < run.Count; j++)
                            {
                                var g = run[j];
                                var d = J.Clamp(e * 1.6 * n2 - j);
                                if (d > 0) env.Circle(g.X, ry + size * 0.7, pw * 1.2 * d, pen, null, 0, a, false);
                            }
                            anchor = new[] { rx1, ry + size * 0.7 };
                        }
                    }
                    var note = ProofNotes.GetValueOrDefault(mk, "ママ");
                    var le = J.E.OutCubic(J.Clamp(e * 1.4 - 0.4));
                    if (le <= 0 || anchor == null) continue;
                    double nx, ny;
                    if (noteSide == "right")
                    {
                        nx = Math.Min(W * 0.93 - ns * 2.2, x1 + size * 0.7 + mi * ns * 0.4);
                        ny = y0 + (mi != 0 ? m.H * 0.7 : m.H * 0.15);
                    }
                    else
                    {
                        nx = J.Clamp(anchor[0] + (mi != 0 ? ns * 2 : -ns * 2), W * 0.1, W * 0.85);
                        ny = y0 - size * 0.9 - mi * ns * 1.6;
                    }
                    var mxp = noteSide == "right"
                        ? new[] { J.Lerp(anchor[0], nx, 0.5), anchor[1] - size * 0.25 }
                        : new[] { anchor[0], J.Lerp(anchor[1], ny, 0.5) };
                    env.PolyPartial(new[] { anchor, mxp, new[] { nx - ns * 0.2, ny } }, le, pen, pw * 0.7, a, false);
                    var na = J.Clamp((le - 0.6) / 0.4);
                    if (na > 0)
                        env.DrawItem(new JObject
                        {
                            ["text"] = note,
                            ["font"] = JLayouts.S(p, "pen"),
                            ["size"] = ns,
                            ["align"] = "left",
                            ["x"] = nx,
                            ["y"] = ny,
                            ["rot"] = -4,
                            ["color"] = pen,
                            ["alpha"] = na * a,
                            ["ghost"] = false,
                        });
                }
                if (JLayouts.Truthy(p["stamp"]))
                {
                    var ts = env.Cut.InDur + 0.55 + marks.Count * 0.25;
                    var x = (env.Lt - ts) / 0.18;
                    if (x > 0)
                    {
                        var S = Math.Max(SmallSize(env) * 4.2, size * 1.05);
                        var sxp = port ? W * 0.78 : Math.Min(W * 0.9 - S * 0.6, x1 + S * 0.5);
                        var syp = port ? Math.Min(H * 0.88, y1 + S * 0.9) : y1 - S * 0.1;
                        var k2 = J.Lerp(1.5, 1, J.E.OutBack(J.Clamp(x), 1.4));
                        var a = J.Clamp(x * 3) * outK;
                        ctx.Save();
                        ctx.Translate((float)sxp, (float)syp);
                        ctx.RotateDegrees((float)JLayouts.D(p, "rot"));
                        ctx.Scale((float)k2, (float)k2);
                        var w2 = S * 1.25;
                        var h2 = S * 0.72;
                        env.RRect(-w2 / 2, -h2 / 2, w2, h2, S * 0.08, null, a * 0.9, false, pen, Math.Max(2, S * 0.05));
                        env.RRect(-w2 / 2 + S * 0.07, -h2 / 2 + S * 0.07, w2 - S * 0.14, h2 - S * 0.14, S * 0.05, null, a * 0.9, false, pen, Math.Max(1, S * 0.02));
                        env.DrawItem(new JObject
                        {
                            ["text"] = "校了",
                            ["font"] = SerifF(env),
                            ["size"] = S * 0.36,
                            ["track"] = 0.2,
                            ["x"] = 0,
                            ["y"] = -S * 0.06,
                            ["color"] = pen,
                            ["alpha"] = a * 0.9,
                            ["ghost"] = false,
                        });
                        env.DrawItem(new JObject
                        {
                            ["text"] = J.FmtTime(env.Cut.Start),
                            ["font"] = MonoF(env),
                            ["size"] = S * 0.12,
                            ["x"] = 0,
                            ["y"] = S * 0.2,
                            ["color"] = pen,
                            ["alpha"] = a * 0.9,
                            ["ghost"] = false,
                        });
                        ctx.Restore();
                    }
                }
                env.Body = bb;
            },
        });
    }

    private static void RegisterNumbered()
    {
        RegC("numbered", new LayoutDef
        {
            Name = "番号付き",
            Tags = new[] { "graphic", "editorial", "pop" },
            W = 1,
            Fits = n => n >= 2 && n <= 18,
            EnterBias = new JObject { ["slice"] = 1.3, ["wipe"] = 1.3, ["drop"] = 1.2 },
            Plan = (rng, cut, st) =>
            {
                var n = cut.N;
                var port = PortOf(cut);
                var k = n <= 3 ? Math.Min(3, n) : n <= 7 ? 2 : n <= 12 ? 3 : 4;
                var chunks = n <= 3 ? CharUnits(cut.Text).Take(3).ToList() : SplitK(cut.Text, k, k);
                return new JObject
                {
                    ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
                    ["numFont"] = rng.Pick(FontsOf(st, "display")),
                    ["chunks"] = new JArray(chunks),
                    ["variant"] = port ? rng.Pick(new[] { "rows", "rows", "behind" }) : rng.Pick(new[] { "cols", "rows", "behind" }),
                    ["num"] = rng.Pick(new[] { "outline", "accent", "dim" }),
                    ["label"] = rng.Pick(new[] { "STEP", "PART", "No.", "SCENE" }),
                };
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var ctx = env.Ctx;
                var p = env.P;
                var u = U(env);
                var port = IsPort(env);
                var chunks = (p["chunks"] as JArray)?.Select(t => (string?)t).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();
                if (chunks == null || chunks.Count == 0) chunks = new List<string> { env.Cut.Text.Trim() };
                var k = chunks.Count;
                var outK = Tout(env);
                var ls = SmallSize(env);
                var lw = Math.Max(1.2, u * 0.0018);
                JObject NumItem(int i, double size, double x, double y, string align, bool big)
                {
                    var it = new JObject
                    {
                        ["text"] = Pad2(i + 1),
                        ["font"] = JLayouts.S(p, "numFont"),
                        ["size"] = size,
                        ["x"] = x,
                        ["y"] = y,
                        ["align"] = align,
                        ["track"] = -0.02,
                        ["ghost"] = false,
                    };
                    if (JLayouts.S(p, "num") == "outline" || big)
                    {
                        it["fill"] = false;
                        it["stroke"] = Math.Max(1.5, size * (big ? 0.008 : 0.012));
                        it["strokeColor"] = JLayouts.S(p, "num") == "accent" ? sc.Str("accent") : sc.Str("fg");
                        it["color"] = sc.Str("fg");
                        it["alpha"] = big ? 0.55 : 1;
                    }
                    else if (JLayouts.S(p, "num") == "accent") it["color"] = sc.Str("accent");
                    else
                    {
                        it["color"] = sc.Str("sub");
                        it["alpha"] = 0.45;
                    }
                    return it;
                }
                void DrawNum(JObject it, int i, double[] clipBox)
                {
                    var e = Tin(env, 0.04 + i * 0.1, 0.55, J.E.OutExpo);
                    var o = J.E.InCubic(env.POut);
                    if (e <= 0 || o >= 1) return;
                    ctx.Save();
                    ctx.ClipRect(new SKRect((float)clipBox[0], (float)clipBox[1], (float)(clipBox[0] + clipBox[2]), (float)(clipBox[1] + clipBox[3])));
                    var c2 = (JObject)it.DeepClone();
                    c2["y"] = JLayouts.D(it, "y") + (1 - e) * JLayouts.D(it, "size") * 1.1 + o * JLayouts.D(it, "size") * 1.1;
                    env.DrawItem(c2);
                    ctx.Restore();
                }
                void Lab(int i, double x, double y, string align, double a) => env.DrawItem(new JObject
                {
                    ["text"] = JLayouts.S(p, "label") == "No." ? "No." : JLayouts.S(p, "label"),
                    ["font"] = MonoF(env),
                    ["size"] = ls * 0.85,
                    ["track"] = 0.3,
                    ["align"] = align,
                    ["x"] = x,
                    ["y"] = y,
                    ["color"] = i == 0 ? sc.Str("accent") : sc.Str("sub"),
                    ["alpha"] = a,
                    ["ghost"] = false,
                });
                BBox? bb = null;
                if (JLayouts.S(p, "variant") == "cols" && !port && k <= 3)
                {
                    var cw = W * 0.86 / k;
                    var x0 = W * 0.07;
                    var csize = Math.Min(chunks.Min(c => JLayouts.FitSize(c, JLayouts.S(p, "font")!, cw * 0.86, H * 0.3, new JObject { ["track"] = 0.02 })), u * 0.2);
                    var nsz = Math.Min(cw * 0.42, Math.Max(csize * 1.5, u * 0.14));
                    nsz = Math.Min(nsz, H * 0.3);
                    var top = H / 2 - (nsz * 1.05 + csize * 1.4) / 2 + ls;
                    for (var i = 0; i < chunks.Count; i++)
                    {
                        var c = chunks[i];
                        var x = x0 + i * cw;
                        var re = Tin(env, 0.1 + i * 0.08, 0.6, J.E.InOutCubic) * outK;
                        if (i > 0)
                            env.Line(new[] { new[] { x, H / 2 - H * 0.28 * re }, new[] { x, H / 2 + H * 0.28 * re } }, sc.Str("sub"), lw, 0.7, false);
                        var xl = x + cw * 0.07;
                        Lab(i, xl, top - ls * 0.9, "left", Tin(env, 0.1 + i * 0.1, 0.4, J.E.OutCubic) * outK);
                        DrawNum(NumItem(i, nsz, xl, top + nsz * 0.5, "left", false), i, new[] { x, top, cw, nsz * 1.02 });
                        env.Line(new[] { new[] { xl, top + nsz * 1.05 }, new[] { xl + cw * 0.82 * re, top + nsz * 1.05 } }, sc.Str("fg"), lw * 1.4, 1, false);
                        bb = JLayouts.UnionBb(bb, JLayouts.MainDraw(env, new JObject
                        {
                            ["text"] = c,
                            ["font"] = JLayouts.S(p, "font"),
                            ["size"] = csize,
                            ["x"] = xl,
                            ["y"] = top + nsz * 1.05 + csize * 0.85,
                            ["align"] = "left",
                            ["track"] = 0.02,
                            ["color"] = sc.Str("fg"),
                            ["mi"] = i * 3,
                        }));
                    }
                    env.Body = bb;
                    return;
                }
                if (JLayouts.S(p, "variant") == "behind")
                {
                    var bw = W * (port ? 0.8 : 0.66);
                    var rowH = H * (port ? 0.66 : 0.72) / k;
                    var csize = Math.Min(chunks.Min(c => JLayouts.FitSize(c, JLayouts.S(p, "font")!, bw * 0.8, rowH * 0.62, new JObject { ["track"] = 0.02 })), u * 0.2);
                    for (var i = 0; i < chunks.Count; i++)
                    {
                        var c = chunks[i];
                        var y = H / 2 + (i - (k - 1) / 2.0) * rowH;
                        var sh = k > 1 ? (i / (double)(k - 1) - 0.5) * bw * 0.2 : 0;
                        var cm = Meas(c, JLayouts.S(p, "font")!, csize, new JObject { ["track"] = 0.02 });
                        var x = W / 2 + sh - cm.W / 2;
                        var nsz = Math.Min(rowH * 1.02, u * 0.5);
                        var nw = Meas("00", JLayouts.S(p, "numFont")!, nsz).W;
                        var nx = J.Clamp(x + Math.Min(cm.W, nw) * 0.3, W * 0.04 + nw / 2, W * 0.96 - nw / 2);
                        DrawNum(NumItem(i, nsz, nx, y, "center", true), i, new[] { 0.0, y - rowH * 0.75, W, rowH * 1.5 });
                        bb = JLayouts.UnionBb(bb, JLayouts.MainDraw(env, new JObject
                        {
                            ["text"] = c,
                            ["font"] = JLayouts.S(p, "font"),
                            ["size"] = csize,
                            ["x"] = x,
                            ["y"] = y,
                            ["align"] = "left",
                            ["track"] = 0.02,
                            ["color"] = sc.Str("fg"),
                            ["mi"] = i * 3,
                        }));
                    }
                    env.Body = bb;
                    return;
                }
                var bw2 = W * (port ? 0.86 : 0.7);
                var bx = (W - bw2) / 2;
                var rowH2 = Math.Min(H * (port ? 0.62 : 0.72) / k, u * (port ? 0.3 : 0.34));
                var nsz2 = rowH2 * (port ? 0.56 : 0.72);
                var nW = Meas("00", JLayouts.S(p, "numFont")!, nsz2).W + nsz2 * 0.3;
                var csize2 = Math.Min(chunks.Min(c => JLayouts.FitSize(c, JLayouts.S(p, "font")!, (bw2 - nW) * 0.96, rowH2 * 0.6, new JObject { ["track"] = 0.02 })), u * 0.2);
                var y0 = H / 2 - k * rowH2 / 2;
                for (var i = 0; i < chunks.Count; i++)
                {
                    var c = chunks[i];
                    var y = y0 + (i + 0.5) * rowH2;
                    var re = Tin(env, 0.06 + i * 0.08, 0.6, J.E.InOutCubic) * outK;
                    env.Line(new[] { new[] { bx, y0 + (i + 1) * rowH2 }, new[] { bx + bw2 * re, y0 + (i + 1) * rowH2 } }, sc.Str("sub"), lw, 0.8, false);
                    if (i == 0) env.Line(new[] { new[] { bx + bw2, y0 }, new[] { bx + bw2 - bw2 * re, y0 } }, sc.Str("fg"), lw * 1.6, 1, false);
                    if (i == 0) Lab(0, bx + bw2, y0 - ls * 0.9, "right", Tin(env, 0.1, 0.4, J.E.OutCubic) * outK);
                    DrawNum(NumItem(i, nsz2, bx, y + nsz2 * 0.02, "left", false), i, new[] { bx - 2, y - rowH2 / 2 + 2, nW, rowH2 - 4 });
                    bb = JLayouts.UnionBb(bb, JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = c,
                        ["font"] = JLayouts.S(p, "font"),
                        ["size"] = csize2,
                        ["x"] = bx + nW,
                        ["y"] = y,
                        ["align"] = "left",
                        ["track"] = 0.02,
                        ["color"] = sc.Str("fg"),
                        ["mi"] = i * 3,
                    }));
                }
                env.Body = bb;
            },
        });
    }

    private static void RegisterPoster()
    {
        RegC("poster", new LayoutDef
        {
            Name = "ポスター",
            Tags = new[] { "graphic", "pop", "editorial" },
            W = 1,
            Emph = 1.3,
            Fits = n => n <= 16,
            EnterBias = new JObject { ["slice"] = 1.4, ["stretch"] = 1.3, ["wipe"] = 1.2 },
            Plan = (rng, cut, st) =>
            {
                var n = cut.N;
                var port = PortOf(cut);
                var L = n <= 3 ? 1 : Math.Min(4, (int)Math.Ceiling(n / (port ? 3.2 : 4.6)));
                return new JObject
                {
                    ["font"] = rng.Pick(FontsOf(st, "display")),
                    ["lines"] = new JArray(SplitK(cut.Text, L, L)),
                    ["variant"] = rng.Pick(new[] { "stack", "stack", "block", "tate" }),
                    ["dot"] = rng.Chance(0.55),
                    ["head"] = rng.Pick(new[] { "LYRIC", "SIDE A", "VOL.", "LIVE", "TOUR" }),
                    ["ang"] = rng.Pick(new[] { 0, 0, -90 }),
                };
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var p = env.P;
                var u = U(env);
                var port = IsPort(env);
                var outK = Tout(env);
                var ls = SmallSize(env);
                var mx = W * 0.07;
                var my = H * 0.07;
                var aw = W - mx * 2;
                var lines = (p["lines"] as JArray)?.Select(t => (string?)t).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();
                if (lines == null || lines.Count == 0) lines = new List<string> { env.Cut.Text.Trim() };
                var latin = HasLatin(env.Cut.Text);
                var credH = ls * 4.2;
                var topH = ls * 2;
                var y0 = my + topH + ls * 0.6;
                var y1 = H - my - credH - ls * 0.8;
                var avH = y1 - y0;
                var block = JLayouts.S(p, "variant") == "block";
                var tate = JLayouts.S(p, "variant") == "tate" && !latin;
                var plate = block ? PlateCol(sc, new[] { sc.Str("accent"), sc.Str("ink") }) : null;
                var tc = block ? OnCol(sc, plate!) : sc.Str("fg")!;
                var lw = Math.Max(1.2, u * 0.0016);
                if (block)
                {
                    var e = Tin(env, 0, 0.5, J.E.InOutExpo) * (1 - J.E.InCubic(env.POut));
                    env.Rect(0, 0, W, (y1 + ls * 0.4) * e, plate, 1, GIn(env));
                }
                var ha = Tin(env, 0.1, 0.4, J.E.OutCubic) * outK;
                env.DrawItem(new JObject
                {
                    ["text"] = JLayouts.S(p, "head") + "  " + LineNo(env),
                    ["font"] = MonoF(env),
                    ["size"] = ls,
                    ["track"] = 0.3,
                    ["align"] = "left",
                    ["x"] = mx,
                    ["y"] = my + ls * 0.5,
                    ["color"] = tc,
                    ["alpha"] = ha,
                    ["ghost"] = false,
                });
                env.DrawItem(new JObject
                {
                    ["text"] = J.FmtTime(env.Cut.Start),
                    ["font"] = MonoF(env),
                    ["size"] = ls,
                    ["track"] = 0.2,
                    ["align"] = "right",
                    ["x"] = mx + aw,
                    ["y"] = my + ls * 0.5,
                    ["color"] = tc,
                    ["alpha"] = ha,
                    ["ghost"] = false,
                });
                var re = Tin(env, 0.05, 0.6, J.E.InOutCubic) * outK;
                env.Line(new[] { new[] { mx, my + ls * 1.5 }, new[] { mx + aw * re, my + ls * 1.5 } }, tc, lw, 0.8, false);
                if (tate)
                {
                    if (!block)
                    {
                        var t = Strip(env.Cut.Text);
                        var n = JLayouts.GlyphCount(t);
                        var per = port ? 6 : 5;
                        var kc = (int)Math.Ceiling(n / (double)per);
                        var mxG = Math.Min(per, (int)Math.Ceiling(n / (double)kc));
                        var sz = Math.Min(avH / (mxG * 0.98), aw * 0.8 / (kc * 1.14));
                        var free = aw - kc * sz * 1.14;
                        var r = Math.Min(avH * 0.36, free * 0.42);
                        var q = J.E.OutBack(J.Clamp((env.Lt - 0.05) / 0.45), 1.3) * (1 - J.E.InCubic(env.POut));
                        if (r > u * 0.06) env.Circle(mx + free * 0.48, y0 + avH / 2, r * q, sc.Str("accent"), null, 0, 1, GIn(env));
                    }
                }
                else if (JLayouts.Truthy(p["dot"]) && !block)
                {
                    var r = Math.Min(aw, avH) * (port ? 0.34 : 0.3);
                    var q = J.E.OutBack(J.Clamp((env.Lt - 0.05) / 0.45), 1.3) * (1 - J.E.InCubic(env.POut));
                    env.Circle(mx + aw - r * 0.9, y0 + r * 0.95, r * q, sc.Str("accent"), null, 0, 1, GIn(env));
                }
                BBox? bb = null;
                if (tate)
                {
                    var t = Strip(env.Cut.Text);
                    var n = JLayouts.GlyphCount(t);
                    var per = port ? 6 : 5;
                    var cols = MemoGet("pt|" + t + per, () => SplitK(t, (int)Math.Ceiling(n / (double)per), (int)Math.Ceiling(n / (double)per)).Select(Strip).ToList());
                    var mxG = cols.Max(c => JLayouts.GlyphCount(c));
                    const double gap = 0.14;
                    var sz = Math.Min(avH / (mxG * 0.98), aw * 0.8 / (cols.Count * (1 + gap)));
                    var x = mx + aw - sz / 2;
                    for (var i = 0; i < cols.Count; i++)
                    {
                        var c = cols[i];
                        bb = JLayouts.UnionBb(bb, JLayouts.MainDraw(env, new JObject
                        {
                            ["text"] = c,
                            ["font"] = JLayouts.S(p, "font"),
                            ["size"] = sz,
                            ["x"] = x,
                            ["y"] = y0 + (avH - mxG * sz * 0.98) / 2,
                            ["vertical"] = true,
                            ["align"] = "left",
                            ["track"] = -0.02,
                            ["color"] = tc,
                            ["mi"] = i * 3,
                        }));
                        x -= sz * (1 + gap);
                    }
                }
                else
                {
                    var w1 = lines.Select(l => Meas(l, JLayouts.S(p, "font")!, 100, new JObject { ["track"] = -0.02 }).W / 100).ToList();
                    var sz = w1.Select(w => aw / Math.Max(0.5, w)).ToList();
                    var tot = sz.Sum(v => v * 0.98);
                    var f = Math.Min(1, avH / tot);
                    sz = sz.Select(v => v * f).ToList();
                    var th = sz.Sum(v => v * 0.98);
                    var y = y0 + (avH - th) * (block ? 1 : 0.5);
                    for (var i = 0; i < lines.Count; i++)
                    {
                        y += sz[i] * 0.49;
                        bb = JLayouts.UnionBb(bb, JLayouts.MainDraw(env, new JObject
                        {
                            ["text"] = lines[i],
                            ["font"] = JLayouts.S(p, "font"),
                            ["size"] = sz[i],
                            ["x"] = mx,
                            ["y"] = y,
                            ["align"] = "left",
                            ["track"] = -0.02,
                            ["color"] = tc,
                            ["mi"] = i * 3,
                        }));
                        y += sz[i] * 0.49;
                    }
                }
                var cy = H - my - credH;
                var lw2 = Math.Max(4, u * 0.008);
                env.Rect(mx, cy, aw * re, lw2, sc.Str("fg"), 1, false);
                var cols3 = new[]
                {
                    new[] { "DATE", J.FmtTime(env.Cut.Start) },
                    new[] { "No.", LineNo(env) + " / " + Pad2(JLayouts.GlyphCount(env.Cut.Text)) },
                    new[] { "WORDS", JLayouts.OrStr(DeckCopy(env), RomajiOf(env), Flat(env.Cut.Text))! },
                };
                var cw3 = aw / 3;
                for (var i = 0; i < cols3.Length; i++)
                {
                    var k2 = cols3[i][0];
                    var v = cols3[i][1];
                    var a = Tin(env, 0.2 + i * 0.08, 0.4, J.E.OutCubic) * outK;
                    if (a <= 0.01) continue;
                    var x = mx + i * cw3;
                    env.DrawItem(new JObject
                    {
                        ["text"] = k2,
                        ["font"] = MonoF(env),
                        ["size"] = ls * 0.8,
                        ["track"] = 0.3,
                        ["align"] = "left",
                        ["x"] = x,
                        ["y"] = cy + lw2 + ls * 0.9,
                        ["color"] = sc.Str("sub"),
                        ["alpha"] = a,
                        ["ghost"] = false,
                    });
                    var maxC = Math.Max(3, (int)Math.Floor(cw3 * 0.9 / (ls * (HasLatin(v) ? 0.62 : 1.05))));
                    var vv = JLayouts.CpLen(v) > maxC ? Sl(v, maxC - 1) + "…" : v;
                    env.DrawItem(new JObject
                    {
                        ["text"] = vv,
                        ["font"] = BodyF(env),
                        ["size"] = ls * 1.05,
                        ["align"] = "left",
                        ["x"] = x,
                        ["y"] = cy + lw2 + ls * 2.4,
                        ["color"] = sc.Str("fg"),
                        ["alpha"] = a,
                        ["ghost"] = false,
                    });
                    if (i > 0)
                        env.Line(new[] { new[] { x - ls * 0.5, cy + lw2 + ls * 0.4 }, new[] { x - ls * 0.5, cy + credH - ls * 0.3 } },
                            sc.Str("sub"), lw, 0.6 * a, false);
                }
                env.Body = bb ?? Box(mx, y0, mx + aw, y1);
            },
        });
    }

    private static void RegisterSwissGrid()
    {
        RegC("swissGrid", new LayoutDef
        {
            Name = "スイスグリッド",
            Tags = new[] { "graphic", "editorial", "calm" },
            W = 1,
            Fits = n => n <= 18,
            EnterBias = new JObject { ["wipe"] = 1.4, ["slice"] = 1.3, ["cut"] = 1.2 },
            Plan = (rng, cut, st) =>
            {
                var n = cut.N;
                var k = n <= 4 ? 1 : n <= 9 ? 2 : 3;
                return new JObject
                {
                    ["font"] = rng.Pick(FontsOf(st, "display")),
                    ["chunks"] = new JArray(SplitK(cut.Text, k, k)),
                    ["variant"] = rng.Pick(new[] { "a", "b" }),
                    ["shape"] = rng.Pick(new[] { "circle", "square", "circle", "bar" }),
                    ["label"] = rng.Chance(0.7),
                };
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var p = env.P;
                var u = U(env);
                var port = IsPort(env);
                var outK = Tout(env);
                var ls = SmallSize(env);
                var cols = port ? 4 : 6;
                var rows = port ? 8 : 6;
                var m = u * 0.07;
                var gx0 = m * 1.35;
                var gx1 = W - m;
                var gy0 = m;
                var gy1 = H - m;
                var cw = (gx1 - gx0) / cols;
                var rh = (gy1 - gy0) / rows;
                double X(double c) => gx0 + c * cw;
                double Y(double r) => gy0 + r * rh;
                var lw = Math.Max(1, u * 0.0012);
                for (var c = 0; c <= cols; c++)
                {
                    var e = Tin(env, c * 0.035, 0.55, J.E.InOutCubic) * outK;
                    if (e > 0) env.Line(new[] { new[] { X(c), gy0 }, new[] { X(c), gy0 + (gy1 - gy0) * e } }, sc.Str("sub"), lw, 0.38, false);
                }
                for (var r = 0; r <= rows; r++)
                {
                    var e = Tin(env, 0.05 + r * 0.035, 0.55, J.E.InOutCubic) * outK;
                    if (e > 0) env.Line(new[] { new[] { gx0, Y(r) }, new[] { gx0 + (gx1 - gx0) * e, Y(r) } }, sc.Str("sub"), lw, 0.38, false);
                }
                var na = Tin(env, 0.2, 0.4, J.E.OutCubic) * outK;
                for (var c = 0; c < cols; c++)
                    env.DrawItem(new JObject
                    {
                        ["text"] = Pad2(c + 1),
                        ["font"] = MonoF(env),
                        ["size"] = ls * 0.7,
                        ["align"] = "left",
                        ["x"] = X(c) + ls * 0.3,
                        ["y"] = gy0 - ls * 0.6,
                        ["color"] = sc.Str("sub"),
                        ["alpha"] = na * 0.8,
                        ["ghost"] = false,
                    });
                var chunks = (p["chunks"] as JArray)?.Select(t => (string?)t).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();
                if (chunks == null || chunks.Count == 0) chunks = new List<string> { env.Cut.Text.Trim() };
                var k = chunks.Count;
                var half = (int)Math.Ceiling(cols / 2.0);
                var pl = new List<(double C, double R, double Span, double Hr)>();
                if (k == 1) pl.Add((0, port ? 2 : 1, cols, port ? 2.4 : 2.6));
                else if (JLayouts.S(p, "variant") == "a")
                {
                    pl.Add((0, 1, cols, port ? 2 : 2.2));
                    pl.Add((port ? 1 : half, rows - (k > 2 ? 3 : 2), cols - (port ? 1 : half), 1));
                    pl.Add((port ? 1 : half, rows - 2 + (port ? 0.2 : 0.1), cols - (port ? 1 : half), 1));
                }
                else
                {
                    for (var i = 0; i < 3; i++)
                    {
                        var c = (int)Math.Round(i * cols / (double)(k + (port ? 1.5 : 0.8)));
                        pl.Add((c, (port ? 1 : 0.6) + i * (rows - 1.6) / k, cols - c, port ? 1.6 : 1.4));
                    }
                }
                var sizes = new List<double>();
                for (var i = 0; i < chunks.Count; i++)
                {
                    var q = pl[i];
                    sizes.Add(Math.Min(JLayouts.FitSize(chunks[i], JLayouts.S(p, "font")!, q.Span * cw - cw * 0.12, q.Hr * rh * 0.9, new JObject { ["track"] = -0.01 }), u * 0.28));
                }
                if (k > 1 && JLayouts.S(p, "variant") == "a")
                    for (var i = 1; i < k; i++) sizes[i] = Math.Min(sizes[i], Math.Min(sizes[0] * 0.6, sizes.Skip(1).Min()));
                var boxes = new List<double[]>();
                BBox? bb = null;
                for (var i = 0; i < chunks.Count; i++)
                {
                    var ch = chunks[i];
                    var q = pl[i];
                    var sz = sizes[i];
                    var x = X(q.C) + cw * 0.06;
                    var y = Y(q.R) + sz * 0.56;
                    var w = Meas(ch, JLayouts.S(p, "font")!, sz, new JObject { ["track"] = -0.01 }).W;
                    boxes.Add(new[] { x, Y(q.R), x + w, Y(q.R) + sz * 1.12 });
                    var e = Tin(env, 0.12 + i * 0.1, 0.5, J.E.InOutCubic) * outK;
                    env.Rect(X(q.C), Y(q.R) - Math.Max(3, u * 0.005), Math.Min(q.Span * cw, w + cw * 0.12) * e, Math.Max(3, u * 0.005), sc.Str("fg"), 1, false);
                    bb = JLayouts.UnionBb(bb, JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = ch,
                        ["font"] = JLayouts.S(p, "font"),
                        ["size"] = sz,
                        ["x"] = x,
                        ["y"] = y,
                        ["align"] = "left",
                        ["track"] = -0.01,
                        ["color"] = sc.Str("fg"),
                        ["mi"] = i * 3,
                    }));
                }
                var cands = new[]
                {
                    new[] { cols - 2.0, rows - 2.0, 2.0 }, new[] { 0.0, rows - 2.0, 2.0 }, new[] { cols - 2.0, 0.0, 2.0 },
                    new[] { cols - 1.0, rows - 1.0, 1.0 }, new[] { 0.0, rows - 1.0, 1.0 }, new[] { cols - 1.0, 0.0, 1.0 }
                };
                var free = cands.FirstOrDefault(c => !boxes.Any(b =>
                    b[0] < X(c[0] + c[2]) && b[2] > X(c[0]) && b[1] < Y(c[1] + c[2]) && b[3] > Y(c[1])));
                if (free != null)
                {
                    double c = free[0], r = free[1], s2 = free[2];
                    var q = J.E.OutBack(J.Clamp((env.Lt - 0.25) / 0.45), 1.2) * (1 - J.E.InCubic(env.POut));
                    var bw2 = s2 * cw;
                    var bh2 = s2 * rh;
                    var cxs = X(c) + bw2 / 2;
                    var cys = Y(r) + bh2 / 2;
                    var R = Math.Min(bw2, bh2) * 0.46;
                    if (q > 0)
                    {
                        if (JLayouts.S(p, "shape") == "circle") env.Circle(cxs, cys, R * q, sc.Str("accent"), null, 0, 1, GIn(env));
                        else if (JLayouts.S(p, "shape") == "square") env.Rect(cxs - R * q, cys - R * q, R * 2 * q, R * 2 * q, sc.Str("accent"), 1, GIn(env));
                        else env.Rect(X(c) + cw * 0.06, cys - rh * 0.12, (bw2 - cw * 0.12) * q, rh * 0.24, sc.Str("accent"), 1, GIn(env));
                    }
                }
                if (JLayouts.Truthy(p["label"]))
                {
                    var a = Tin(env, 0.3, 0.4, J.E.OutCubic) * outK;
                    env.DrawItem(new JObject
                    {
                        ["text"] = "JIZURA  ／  No." + LineNo(env) + "  ／  " + J.FmtTime(env.Cut.Start),
                        ["font"] = MonoF(env),
                        ["size"] = ls * 0.8,
                        ["track"] = 0.25,
                        ["x"] = m * 0.62,
                        ["y"] = (gy0 + gy1) / 2,
                        ["rot"] = -90,
                        ["color"] = sc.Str("sub"),
                        ["alpha"] = a,
                        ["ghost"] = false,
                    });
                }
                env.Body = bb;
            },
        });
    }

    private static void RegisterDictionary()
    {
        RegC("dictionary", new LayoutDef
        {
            Name = "辞書",
            Tags = new[] { "editorial", "calm", "emotional" },
            W = 0.9,
            Fits = n => n >= 1 && n <= 16,
            EnterBias = new JObject { ["blur"] = 1.3, ["type"] = 1.3, ["wipe"] = 1.2 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "serif", "display")),
                ["variant"] = rng.Pick(new[] { "entry", "page", "page" }),
                ["pos"] = rng.Pick(new[] { "名", "連語", "感", "形動", "副" }),
                ["page"] = rng.Int(120, 1480),
                ["tabY"] = rng.Range(0.2, 0.75),
                ["mark"] = rng.Pick(new[] { "◆", "▼", "■" }),
                ["seed"] = rng.Int(1, 9999),
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var p = env.P;
                var u = U(env);
                var port = IsPort(env);
                var outK = Tout(env);
                var ls = SmallSize(env);
                var t0 = env.Cut.Text.Trim();
                var page = JLayouts.S(p, "variant") == "page";
                var mx = W * (port ? 0.08 : 0.1);
                var aw = W - mx * 2 - (port ? W * 0.04 : W * 0.03);
                var lw = Math.Max(1, u * 0.0014);
                var fb = FitBlock(t0, JLayouts.S(p, "font")!, aw * (port ? 0.84 : 0.72), H * (port ? 0.26 : 0.3),
                    new JObject { ["track"] = 0.02, ["lead"] = 1.1 }, port ? 3 : 2);
                var size = Math.Min(fb.Size, u * 0.17);
                var m = Meas(fb.Text, JLayouts.S(p, "font")!, size, new JObject { ["track"] = 0.02, ["lead"] = 1.1 });
                var hx = mx + size * 0.62;
                var hy = H * (page ? 0.44 : 0.4);
                var fs = J.Clamp(u * 0.02, 11, 24);
                var lh = fs * 1.8;
                var src = Flat(env.Cut.LineText.Length > 0 ? env.Cut.LineText : t0) + (HasLatin(t0) ? " — " : "。");
                var ca = Tin(env, 0, 0.5, J.E.OutCubic) * outK;
                var top = hy - m.H / 2 - size * 0.55 - ls * 1.6;
                var defs = new[]
                {
                    JLayouts.OrStr(DeckCopy(env), "歌詞 第" + KanjiNum(LineN(env)) + "行。")!,
                    J.FmtTime(env.Cut.Start) + " ─ " + J.FmtTime(env.Cut.End) + "　" + JLayouts.GlyphCount(t0) + (HasLatin(t0) ? " chars" : "字"),
                };
                var dfs = J.Clamp(u * 0.028, 14, 36);
                var defTop = hy + m.H / 2 + size * 0.45;
                var defBot = defTop + defs.Length * dfs * 1.7 + dfs;
                if (page)
                {
                    var rowsA = Math.Max(0, (int)Math.Floor((top - H * 0.1) / lh));
                    BodyRows(env, src, BodyF(env), fs, mx, top - rowsA * lh, aw, rowsA, lh, sc.Str("sub")!, ca * 0.4, JLayouts.I(p, "seed"), ca);
                    var rowsB = Math.Max(0, (int)Math.Floor((H * 0.9 - defBot) / lh));
                    BodyRows(env, src, BodyF(env), fs, mx, defBot + lh * 0.5, aw, rowsB, lh, sc.Str("sub")!, ca * 0.4, JLayouts.I(p, "seed") + 31, ca);
                    var he = Tin(env, env.Cut.InDur * 0.6, 0.5, J.E.InOutCubic) * (1 - J.E.OutCubic(J.Clamp(env.POut * 1.15)));
                    if (he > 0)
                    {
                        var nL = fb.Text.Split('\n');
                        for (var i = 0; i < nL.Length; i++)
                        {
                            var w = Meas(nL[i], JLayouts.S(p, "font")!, size, new JObject { ["track"] = 0.02 }).W;
                            var ly = hy + (i - (nL.Length - 1) / 2.0) * size * 1.1;
                            env.Rect(hx - size * 0.1, ly - size * 0.05, (w + size * 0.2) * J.Clamp(he * nL.Length - i), size * 0.5, sc.Str("accent"), 0.45, false);
                        }
                    }
                }
                var ga = Tin(env, 0.1, 0.4, J.E.OutCubic) * outK;
                env.DrawItem(new JObject
                {
                    ["text"] = Pad3(JLayouts.I(p, "page") % 1000),
                    ["font"] = MonoF(env),
                    ["size"] = ls,
                    ["align"] = "left",
                    ["x"] = mx,
                    ["y"] = H * 0.055,
                    ["color"] = sc.Str("sub"),
                    ["alpha"] = ga,
                    ["ghost"] = false,
                });
                var st0 = Strip(t0);
                env.DrawItem(new JObject
                {
                    ["text"] = (st0.Length > 0 ? st0[0].ToString() : "") + "  ─  " + Sl(JLayouts.OrStr(RomajiOf(env), Flat(t0))!, 14),
                    ["font"] = SerifF(env),
                    ["size"] = ls,
                    ["align"] = "right",
                    ["x"] = mx + aw,
                    ["y"] = H * 0.055,
                    ["color"] = sc.Str("sub"),
                    ["alpha"] = ga,
                    ["ghost"] = false,
                });
                env.Line(new[] { new[] { mx, H * 0.055 + ls * 0.9 }, new[] { mx + aw * ga, H * 0.055 + ls * 0.9 } }, sc.Str("sub"), lw, 0.6, false);
                var fa = Tin(env, 0.05, 0.35, J.E.OutCubic) * outK;
                env.DrawItem(new JObject
                {
                    ["text"] = JLayouts.S(p, "mark"),
                    ["font"] = BodyF(env),
                    ["size"] = size * 0.32,
                    ["x"] = mx,
                    ["y"] = hy - m.H / 2 + size * 0.5,
                    ["color"] = sc.Str("accent"),
                    ["alpha"] = fa,
                    ["ghost"] = false,
                });
                var rom = RomajiOf(env);
                if (rom != null)
                    env.DrawItem(new JObject
                    {
                        ["text"] = rom.ToLowerInvariant(),
                        ["font"] = SerifF(env),
                        ["size"] = ls * 1.2,
                        ["track"] = 0.15,
                        ["align"] = "left",
                        ["x"] = hx,
                        ["y"] = hy - m.H / 2 - ls * 1.2,
                        ["color"] = sc.Str("sub"),
                        ["alpha"] = fa,
                        ["ghost"] = false,
                    });
                var bb = JLayouts.MainDraw(env, new JObject
                {
                    ["text"] = fb.Text,
                    ["font"] = JLayouts.S(p, "font"),
                    ["size"] = size,
                    ["x"] = hx,
                    ["y"] = hy,
                    ["align"] = "left",
                    ["track"] = 0.02,
                    ["lead"] = 1.1,
                    ["color"] = sc.Str("fg"),
                });
                var lastL = fb.Text.Split('\n')[^1];
                var lastW = Meas(lastL, JLayouts.S(p, "font")!, size, new JObject { ["track"] = 0.02 }).W;
                var ly2 = hy + (fb.Text.Split('\n').Length - 1) / 2.0 * size * 1.1;
                var pa = Tin(env, env.Cut.InDur * 0.7, 0.35, x => J.E.OutBack(x)) * outK;
                if (pa > 0.01)
                {
                    var posStr = JLayouts.S(p, "pos")!;
                    var pw = ls * (posStr.Length * 1.25 + 0.9);
                    var px = Math.Min(hx + lastW + size * 0.3, W - mx - pw);
                    var pyy = ly2 + (hx + lastW + size * 0.3 > W - mx - pw ? size * 0.75 : 0);
                    env.RRect(px, pyy - ls * 0.85, pw, ls * 1.7, ls * 0.3, null, J.Clamp(pa), false, sc.Str("fg"), lw * 1.4);
                    env.DrawItem(new JObject
                    {
                        ["text"] = posStr,
                        ["font"] = SerifF(env),
                        ["size"] = ls * 1.1,
                        ["x"] = px + pw / 2,
                        ["y"] = pyy,
                        ["color"] = sc.Str("fg"),
                        ["alpha"] = J.Clamp(pa),
                        ["ghost"] = false,
                    });
                }
                for (var i = 0; i < defs.Length; i++)
                {
                    var d = defs[i];
                    var a = Tin(env, env.Cut.InDur * 0.8 + 0.1 + i * 0.12, 0.4, J.E.OutCubic) * outK;
                    if (a <= 0.01) continue;
                    var y = defTop + dfs * 0.8 + i * dfs * 1.7;
                    env.Circle(hx + dfs * 0.45, y, dfs * 0.48, sc.Str("fg"), null, 0, a, false);
                    env.DrawItem(new JObject
                    {
                        ["text"] = (i + 1).ToString(CultureInfo.InvariantCulture),
                        ["font"] = MonoF(env),
                        ["size"] = dfs * 0.62,
                        ["x"] = hx + dfs * 0.45,
                        ["y"] = y,
                        ["color"] = sc.Str("bg"),
                        ["alpha"] = a,
                        ["ghost"] = false,
                    });
                    var maxC = Math.Max(4, (int)Math.Floor((aw - dfs * 2) / (dfs * (HasLatin(d) ? 0.55 : 1.02))));
                    var dd = JLayouts.CpLen(d) > maxC ? Sl(d, maxC - 1) + "…" : d;
                    env.DrawItem(new JObject
                    {
                        ["text"] = dd,
                        ["font"] = SerifF(env),
                        ["size"] = dfs,
                        ["align"] = "left",
                        ["x"] = hx + dfs * 1.4 + (1 - a) * dfs,
                        ["y"] = y,
                        ["color"] = sc.Str("fg"),
                        ["alpha"] = a,
                        ["ghost"] = false,
                    });
                }
                var tq = Tin(env, 0.15, 0.5, J.E.OutExpo) * outK;
                if (tq > 0.01)
                {
                    var tw = u * 0.075;
                    var th = u * 0.16;
                    var ty = H * 0.1 + (H * 0.8 - th) * JLayouts.D(p, "tabY");
                    var tabC = PlateCol(sc, new[] { sc.Str("ink"), sc.Str("fg") });
                    env.Line(new[] { new[] { W - tw * 1.05, H * 0.04 }, new[] { W - tw * 1.05, H * 0.96 } }, sc.Str("sub"), lw, 0.35 * tq, false);
                    env.Rect(W - tw * tq, ty, tw + 2, th, tabC, 1, false);
                    env.DrawItem(new JObject
                    {
                        ["text"] = st0.Length > 0 ? st0[0].ToString() : "",
                        ["font"] = JLayouts.S(p, "font"),
                        ["size"] = tw * 0.62,
                        ["x"] = W - tw * tq + tw * 0.5,
                        ["y"] = ty + th / 2,
                        ["color"] = OnCol(sc, tabC),
                        ["ghost"] = false,
                    });
                }
                env.Body = bb;
            },
        });
    }

        private static List<string> SlotsOf(string? t) =>
        JLayouts.CodePoints(Regex.Replace((t ?? "").Trim(), @"[\s　]+", " "));

        private static double[][] EmaPts(double w, double h)
    {
        var r = h * 0.26;
        return new[]
        {
            new[] { -w / 2, -h / 2 + r }, new[] { 0.0, -h / 2 }, new[] { w / 2, -h / 2 + r },
            new[] { w / 2, h / 2 }, new[] { -w / 2, h / 2 }
        };
    }

        private static JArray EmaBack(Rng rng)
    {
        var back = new JArray();
        for (var i = 0; i < 7; i++)
            back.Add(new JArray(rng.Range(-1, 1), rng.Range(-8, 8), rng.Range(0.8, 1.05), rng.Int(0, 99)));
        return back;
    }

    private static void RegisterEma()
    {
        RegC("ema", new LayoutDef
        {
            Name = "絵馬",
            Tags = new[] { "emotional", "calm", "pop" },
            W = 0.7,
            Treat = "safe",
            Fits = n => n >= 1 && n <= 16,
            EnterBias = new JObject { ["blur"] = 1.3, ["cut"] = 1.3, ["type"] = 1.3, ["slice"] = 0.5, ["stretch"] = 0.5 },
            Plan = (rng, cut, st) =>
            {
                return new JObject
                {
                    ["font"] = rng.Chance(0.55) ? rng.Pick(new[] { "klee", "brush" }) : rng.Pick(FontsOf(st, "serif", "display")),
                    ["vert"] = !HasLatin(cut.Text) && rng.Chance(0.5),
                    ["emblem"] = rng.Pick(new[] { "sun", "wave", "mount", "knot" }),
                    ["swing"] = rng.Range(12, 20) * rng.Pick(new[] { 1, -1 }),
                    ["back"] = EmaBack(rng),
                };
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var ctx = env.Ctx;
                var p = env.P;
                var u = U(env);
                var port = IsPort(env);
                var outK = Tout(env);
                var ls = SmallSize(env);
                var C = Card(sc);
                var wood = J.Mix(C.Fill, PlateCol(sc, new[] { sc.Str("accent"), sc.Str("accent2"), sc.Str("sub") }, C.Fill, 1.3), 0.16);
                var woodD = J.Mix(wood, Darkest(sc), 0.35);
                var ink = OnCol(sc, wood);
                var cord = PlateCol(sc, new[] { sc.Str("accent"), sc.Str("accent2"), sc.Str("fg") }, wood, 1.6);
                var hw = port ? W * 0.8 : Math.Min(W * 0.52, H * 0.9);
                var hh = hw * 0.7;
                var cordL = H * (port ? 0.06 : 0.08);
                var rackY = port ? H / 2 - cordL - hh * 0.55 : H * 0.13;
                var re = Tin(env, 0, 0.45, J.E.OutCubic) * outK;
                env.Rect(W * 0.04, rackY - u * 0.012, W * 0.92 * re, u * 0.024, woodD, 1, false);
                var backArr = (JArray)p["back"]!;
                var nb = port ? 4 : 7;
                var bw = port ? W * 0.22 : Math.Min(W * 0.12, H * 0.22);
                var bh = bw * 0.72;
                for (var i = 0; i < nb; i++)
                {
                    var b = (JArray)backArr[i % backArr.Count]!;
                    double b0 = (double)b[0]!, b1 = (double)b[1]!, b2 = (double)b[2]!;
                    var b3 = (int)(double)b[3]!;
                    var x = W * (0.08 + 0.84 * (i + 0.5) / nb) + b0 * bw * 0.12;
                    var t1 = 0.02 + i * 0.03;
                    var e = J.E.OutBack(J.Clamp((env.Lt - t1) / 0.4), 1.2);
                    if (e <= 0) continue;
                    var sw = b1 + Math.Sin(env.Ltb * 1.1 + i) * 1.2;
                    var k2 = b2;
                    ctx.Save();
                    ctx.Translate((float)x, (float)rackY);
                    ctx.RotateDegrees((float)sw);
                    ctx.Translate(0, (float)(bh * 0.55 * k2 + bh * 0.2));
                    var a = J.Clamp(e * 2) * outK * 0.8;
                    env.Line(new[]
                    {
                        new[] { -bw * 0.08, -bh * 0.55 * k2 - bh * 0.2 + 2 }, new[] { 0.0, -bh * 0.3 * k2 },
                        new[] { bw * 0.08, -bh * 0.55 * k2 - bh * 0.2 + 2 }
                    }, cord, Math.Max(1.5, u * 0.002), a, false);
                    var pts = EmaPts(bw * k2, bh * k2);
                    env.Poly(pts, J.Mix(wood, sc.Str("bg")!, 0.35), a, false);
                    env.Line(CloseLoop(pts).ToArray(), woodD, Math.Max(1, u * 0.0016), a, false);
                    Greek(env, -bw * k2 * 0.34, -bh * k2 * 0.05, bw * k2 * 0.68, bh * k2 * 0.4, bh * k2 * 0.13, ink, a * 0.35, b3);
                    ctx.Restore();
                }
                var t0 = env.Cut.Text.Trim();
                var hx = W / 2;
                var td = Math.Max(0, env.Lt - 0.08);
                var drop = (1 - J.E.OutBack(J.Clamp(td / 0.45), 1.3)) * -H * 0.5;
                var ang = JLayouts.D(p, "swing") * Math.Exp(-td * 2.6) * Math.Cos(td * 6) * J.Clamp(td / 0.1)
                          + Math.Sin(env.Ltb * 0.9) * 0.8 + J.E.InCubic(env.POut) * JLayouts.D(p, "swing");
                var ha = J.Clamp(env.Lt / 0.12) * (1 - J.Clamp((env.POut - 0.5) / 0.5));
                if (ha <= 0.01)
                {
                    env.Body = null;
                    return;
                }
                ctx.Save();
                ctx.Translate((float)hx, (float)(rackY + drop));
                ctx.RotateDegrees((float)ang);
                env.Line(new[]
                {
                    new[] { 0.0, 0.0 }, new[] { -hw * 0.06, cordL }, new[] { 0.0, cordL + hh * 0.1 },
                    new[] { hw * 0.06, cordL }, new[] { 0.0, 0.0 }
                }, cord, Math.Max(2, u * 0.003), ha, false);
                ctx.Translate(0, (float)(cordL + hh * 0.5));
                var ptsM = EmaPts(hw, hh);
                if (env.Pass == "main")
                {
                    ctx.Save();
                    ctx.Translate((float)(u * 0.01), (float)(u * 0.014));
                    env.Poly(ptsM, Darkest(sc), ha * (J.Lum(sc.Str("bg")!) > 0.5 ? 0.22 : 0.5), false);
                    ctx.Restore();
                }
                env.Poly(ptsM, wood, ha, false);
                var r = hh * 0.26;
                env.Line(new[] { new[] { -hw / 2, -hh / 2 + r }, new[] { 0.0, -hh / 2 }, new[] { hw / 2, -hh / 2 + r } }, woodD, Math.Max(4, hh * 0.05), ha, false);
                if (env.Pass == "main")
                {
                    ctx.Save();
                    using var wdPaint = new SKPaint
                    {
                        IsAntialias = true,
                        IsStroke = true,
                        StrokeWidth = (float)Math.Max(1, u * 0.0015),
                        Color = Env.SkColor(woodD, ha * 0.12),
                        BlendMode = env.Blend,
                    };
                    using var wd = new SKPath();
                    for (var i = 0; i < 7; i++)
                    {
                        var y = -hh / 2 + r + (hh - r) * (i + 0.5) / 7;
                        wd.MoveTo((float)(-hw / 2 + 4), (float)y);
                        wd.CubicTo((float)(-hw * 0.2), (float)(y + hh * 0.02 * Math.Sin(i)),
                            (float)(hw * 0.2), (float)(y - hh * 0.02),
                            (float)(hw / 2 - 4), (float)(y + hh * 0.01));
                    }
                    ctx.DrawPath(wd, wdPaint);
                    ctx.Restore();
                }
                env.Circle(0, -hh / 2 + hh * 0.1, hh * 0.035, sc.Str("bg"), woodD, 1.5, ha, false);
                var ey = -hh / 2 + r * 1.05;
                var es = hh * 0.1;
                var emb = PlateCol(sc, new[] { sc.Str("accent"), sc.Str("accent2"), ink }, wood, 1.8);
                var emblem = JLayouts.S(p, "emblem");
                if (emblem == "sun") env.Circle(-hw * 0.3, ey, es, emb, null, 0, ha, false);
                else if (emblem == "wave")
                {
                    for (var j = 0; j < 2; j++)
                    {
                        var pp = new List<double[]>();
                        for (var i = 0; i <= 12; i++)
                            pp.Add(new[] { -hw * 0.38 + hw * 0.16 * i / 12, ey + j * es * 0.7 + Math.Sin(i / 12.0 * J.TAU) * es * 0.25 });
                        env.Line(pp.ToArray(), emb, Math.Max(2, es * 0.18), ha, false);
                    }
                }
                else if (emblem == "mount")
                    env.Poly(new[] { new[] { -hw * 0.4, ey + es * 0.7 }, new[] { -hw * 0.3, ey - es * 0.8 }, new[] { -hw * 0.2, ey + es * 0.7 } }, emb, ha, false);
                else
                {
                    env.Circle(-hw * 0.32, ey, es * 0.6, null, emb, Math.Max(2, es * 0.2), ha, false);
                    env.Circle(-hw * 0.26, ey, es * 0.6, null, emb, Math.Max(2, es * 0.2), ha, false);
                }
                env.DrawItem(new JObject
                {
                    ["text"] = "奉納",
                    ["font"] = SerifF(env),
                    ["size"] = es * 1.1,
                    ["track"] = 0.3,
                    ["x"] = hw * 0.3,
                    ["y"] = ey,
                    ["color"] = ink,
                    ["alpha"] = ha * 0.8,
                    ["ghost"] = false,
                });
                var axw = hw * 0.84;
                var ay = -hh / 2 + r * 1.6;
                var ah = hh / 2 - ay - hh * 0.14;
                BBox? bb;
                if (JLayouts.Truthy(p["vert"]))
                {
                    var fb = FitBlock(VText(t0), JLayouts.S(p, "font")!, axw, ah,
                        new JObject { ["vertical"] = true, ["lead"] = 1.3, ["track"] = 0.04 }, 3);
                    var size = Math.Min(fb.Size, hh * 0.34);
                    var mm = Meas(fb.Text, JLayouts.S(p, "font")!, size, new JObject { ["vertical"] = true, ["lead"] = 1.3, ["track"] = 0.04 });
                    bb = JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = fb.Text,
                        ["font"] = JLayouts.S(p, "font"),
                        ["size"] = size,
                        ["x"] = 0,
                        ["y"] = ay + (ah - mm.H) / 2,
                        ["vertical"] = true,
                        ["align"] = "left",
                        ["lead"] = 1.3,
                        ["track"] = 0.04,
                        ["rot"] = 1.5,
                        ["color"] = ink,
                        ["noHold"] = true,
                        ["plain"] = true,
                        ["mi"] = MiAt(env, 0.35),
                    });
                }
                else
                {
                    var fb = FitBlock(t0, JLayouts.S(p, "font")!, axw, ah, new JObject { ["lead"] = 1.2, ["track"] = 0.02 }, 3);
                    var size = Math.Min(fb.Size, hh * 0.3);
                    bb = JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = fb.Text,
                        ["font"] = JLayouts.S(p, "font"),
                        ["size"] = size,
                        ["x"] = 0,
                        ["y"] = ay + ah / 2,
                        ["lead"] = 1.2,
                        ["track"] = 0.02,
                        ["rot"] = -1.5,
                        ["color"] = ink,
                        ["noHold"] = true,
                        ["plain"] = true,
                        ["mi"] = MiAt(env, 0.35),
                    });
                }
                env.DrawItem(new JObject
                {
                    ["text"] = "No." + LineNo(env) + "  " + J.FmtTime(env.Cut.Start),
                    ["font"] = MonoF(env),
                    ["size"] = ls * 0.8,
                    ["track"] = 0.15,
                    ["align"] = "right",
                    ["x"] = hw * 0.44,
                    ["y"] = hh * 0.42,
                    ["color"] = ink,
                    ["alpha"] = ha * 0.6,
                    ["ghost"] = false,
                });
                ctx.Restore();
                env.Body = bb != null ? Box(hx - hw / 2, rackY + cordL, hx + hw / 2, rackY + cordL + hh) : null;
            },
        });
    }

    private static void RegisterRansom()
    {
        RegC("ransom", new LayoutDef
        {
            Name = "切り抜き文字",
            Tags = new[] { "pop", "glitch", "graphic" },
            W = 0.8,
            Treat = false,
            Fits = n => n >= 1 && n <= 16,
            EnterBias = new JObject { ["cut"] = 2, ["pop"] = 1.5, ["drop"] = 1.3, ["blur"] = 0.4, ["wipe"] = 0.4, ["slice"] = 0.5 },
            Plan = (rng, cut, st) =>
            {
                var fonts = new List<string>();
                foreach (var f in FontsOf(st, "display", "serif", "body").Concat(FontsOf(st, "mono"))
                             .Concat(new[] { "mincho_black", "gothic_black", "pop", "dot", "brush" }.Where(f => JFonts.Has(f) && rng.Chance(0.35))))
                    if (!fonts.Contains(f)) fonts.Add(f);
                var units = SlotsOf(cut.Text);
                var look = new JArray();
                foreach (var _ in units)
                    look.Add(new JArray(rng.Int(0, fonts.Count - 1), rng.Int(0, 5), rng.Range(-9, 9),
                        rng.Range(0.84, 1.16), rng.Range(-0.1, 0.1), rng.Int(0, 999)));
                return new JObject
                {
                    ["fonts"] = new JArray(fonts),
                    ["look"] = look,
                    ["tilt"] = rng.Range(-3, 3),
                    ["shadow"] = rng.Chance(0.7),
                };
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var ctx = env.Ctx;
                var p = env.P;
                var u = U(env);
                var port = IsPort(env);
                var units = SlotsOf(env.Cut.Text);
                var n = units.Count;
                if (n == 0)
                {
                    env.Body = null;
                    return;
                }
                var outK = 1 - J.E.OutCubic(J.Clamp(env.POut * 1.1));
                var perRow = port
                    ? Math.Min(n, n <= 4 ? 4 : (int)Math.Ceiling(n / Math.Ceiling(n / 4.0)))
                    : Math.Min(n, n <= 8 ? 8 : (int)Math.Ceiling(n / 2.0));
                var rowsA = new List<List<int>>();
                if (HasLatin(env.Cut.Text))
                {
                    var cur = new List<int>();
                    var len = 0;
                    var i0 = 0;
                    var words = new List<int[]>();
                    for (var i = 0; i <= n; i++)
                    {
                        if (i == n || units[i] == " ")
                        {
                            if (i > i0) words.Add(new[] { i0, i });
                            i0 = i + 1;
                        }
                    }
                    var target = Math.Max(perRow, words.Max(w => w[1] - w[0]));
                    foreach (var w in words)
                    {
                        var wl = w[1] - w[0];
                        if (cur.Count > 0 && len + 1 + wl > target)
                        {
                            rowsA.Add(cur);
                            cur = new List<int>();
                            len = 0;
                        }
                        if (cur.Count > 0)
                        {
                            cur.Add(-1);
                            len++;
                        }
                        for (var i = w[0]; i < w[1]; i++) cur.Add(i);
                        len += wl;
                    }
                    if (cur.Count > 0) rowsA.Add(cur);
                }
                else
                    for (var i = 0; i < n; i += perRow)
                        rowsA.Add(Enumerable.Range(i, Math.Min(perRow, n - i)).ToList());
                perRow = rowsA.Max(r => r.Count);
                var rowsN = rowsA.Count;
                var @base = Math.Min(Math.Min(W * 0.84 / (perRow * 1.18), H * 0.64 / (rowsN * 1.35)), u * 0.26);
                var place = new Dictionary<int, int[]>();
                for (var ri = 0; ri < rowsA.Count; ri++)
                    for (var j = 0; j < rowsA[ri].Count; j++)
                    {
                        var idx = rowsA[ri][j];
                        if (idx >= 0) place[idx] = new[] { ri, j, rowsA[ri].Count };
                    }
                var C = Card(sc);
                var pal = new[]
                {
                    new[] { PlateCol(sc, new[] { sc.Str("ink"), sc.Str("fg") }), (string?)null },
                    new[] { C.Fill, null },
                    new[] { PlateCol(sc, new[] { sc.Str("accent"), sc.Str("ink") }), null },
                    new[] { PlateCol(sc, new[] { sc.Str("accent2"), sc.Str("sub"), sc.Str("accent") }), null },
                    new[] { Darkest(sc), null },
                    new[] { C.Fill, "line" },
                };
                BBox? bb = null;
                var fonts = (p["fonts"] as JArray)?.Select(t => (string)t!).ToList();
                if (fonts == null || fonts.Count == 0) fonts = new List<string> { "gothic_black" };
                for (var i = 0; i < n; i++)
                {
                    var ch = units[i];
                    if (ch == " ") continue;
                    var lookArr = p["look"] as JArray;
                    var lk = lookArr != null && i < lookArr.Count ? (JArray)lookArr[i]! : new JArray(0, 0, 0, 1, 0, i);
                    if (!place.TryGetValue(i, out var pl)) continue;
                    double row = pl[0], j = pl[1], cnt = pl[2];
                    var lk0 = (int)(double)lk[0]!;
                    var lk1 = (int)(double)lk[1]!;
                    var lk2 = (double)lk[2]!;
                    var lk3 = (double)lk[3]!;
                    var lk4 = (double)lk[4]!;
                    var lk5 = (int)(double)lk[5]!;
                    var sz = @base * lk3;
                    var x = W / 2 + (j - (cnt - 1) / 2) * @base * 1.18 + J.Rs(lk5, 1) * @base * 0.05;
                    var y = H / 2 + (row - (rowsN - 1) / 2.0) * @base * 1.35 + lk4 * @base;
                    var font = fonts[lk0 % fonts.Count];
                    var fill = pal[lk1 % pal.Length][0]!;
                    var mode = pal[lk1 % pal.Length][1];
                    if (J.Contrast(fill, sc.Str("bg")!) < 1.25 && mode != "line") mode = "edge";
                    var tc = OnCol(sc, fill);
                    var rot = lk2 + JLayouts.D(p, "tilt");
                    var t0 = 0.03 + i * J.Clamp(0.4 / n, 0.025, 0.07);
                    var q = J.Clamp((env.Lt - t0) / 0.16);
                    if (q <= 0) continue;
                    var k = J.Lerp(1.5, 1, J.E.OutCubic(q)) * (1 - J.E.InCubic(env.POut) * 0.25);
                    var a = J.Clamp(q * 2.5) * outK;
                    var adv = Math.Max(0.55, JText.Metrics.Adv(font, ch));
                    var w = sz * (adv + 0.26 + 0.14 * J.R(lk5, 11));
                    var h = sz * (1.2 + 0.18 * J.R(lk5, 12));
                    var pts = new List<double[]>();
                    const int M = 4;
                    for (var s2 = 0; s2 < 4; s2++)
                        for (var m = 0; m < M; m++)
                        {
                            var f = m / (double)M;
                            var jit = J.Rs(lk5, s2, m) * sz * 0.045;
                            if (s2 == 0) pts.Add(new[] { -w / 2 + w * f, -h / 2 + jit });
                            else if (s2 == 1) pts.Add(new[] { w / 2 + jit, -h / 2 + h * f });
                            else if (s2 == 2) pts.Add(new[] { w / 2 - w * f, h / 2 + jit });
                            else pts.Add(new[] { -w / 2 + jit, h / 2 - h * f });
                        }
                    ctx.Save();
                    ctx.Translate((float)x, (float)y);
                    ctx.RotateDegrees((float)rot);
                    ctx.Scale((float)k, (float)k);
                    if (JLayouts.Truthy(p["shadow"]) && env.Pass == "main")
                    {
                        ctx.Save();
                        ctx.Translate((float)(sz * 0.05), (float)(sz * 0.07));
                        env.Poly(pts.ToArray(), Darkest(sc), a * 0.45, false);
                        ctx.Restore();
                    }
                    if (mode == "line")
                    {
                        env.Poly(pts.ToArray(), sc.Str("bg"), a, false);
                        env.Line(CloseLoop(pts).ToArray(), sc.Str("fg"), Math.Max(1.5, sz * 0.02), a, false);
                    }
                    else
                    {
                        env.Poly(pts.ToArray(), fill, a, false);
                        if (mode == "edge")
                            env.Line(CloseLoop(pts).ToArray(), J.Mix(sc.Str("fg")!, fill, 0.4), Math.Max(1, sz * 0.012), a, false);
                    }
                    var r = JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = ch,
                        ["font"] = font,
                        ["size"] = sz,
                        ["x"] = 0,
                        ["y"] = sz * 0.02,
                        ["color"] = mode == "line" ? sc.Str("fg") : tc,
                        ["plain"] = true,
                        ["noHold"] = PlateHold(env),
                        ["mi"] = MiAt(env, t0),
                    });
                    ctx.Restore();
                    if (r != null) bb = JLayouts.UnionBb(bb, Box(x - w / 2, y - h / 2, x + w / 2, y + h / 2));
                }
                env.Body = bb;
            },
        });
    }

    private static void RegisterNewspaper()
    {
        RegC("newspaper", new LayoutDef
        {
            Name = "新聞",
            Tags = new[] { "editorial", "graphic", "pop" },
            W = 0.8,
            Busy = true,
            Treat = "safe",
            Emph = 1.3,
            Fits = n => n >= 1 && n <= 16,
            EnterBias = new JObject { ["cut"] = 1.4, ["zoom"] = 1.3, ["slice"] = 1.2 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
                ["variant"] = HasLatin(cut.Text) ? "yoko" : rng.Pick(new[] { "yoko", "tate", "tate" }),
                ["spin"] = rng.Chance(0.45),
                ["rev"] = rng.Chance(0.6),
                ["seed"] = rng.Int(1, 9999),
                ["mast"] = rng.Pick(new[] { "字面新聞", "歌詞新報", "夜更新聞" }),
                ["issue"] = rng.Int(1000, 29999),
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var ctx = env.Ctx;
                var p = env.P;
                var u = U(env);
                var port = IsPort(env);
                var C = Card(sc);
                var ls = SmallSize(env);
                var pw = W * (port ? 0.92 : 0.9);
                var ph = H * (port ? 0.9 : 0.9);
                var cx = W / 2;
                var cy = H / 2;
                double rot = 0, k = 1, dy = 0;
                var e = J.Clamp(env.Lt / 0.6);
                if (JLayouts.Truthy(p["spin"]))
                {
                    var q = J.E.OutCubic(e);
                    rot = (1 - q) * 720;
                    k = Math.Max(0.001, q);
                }
                else dy = (1 - J.E.OutExpo(J.Clamp(env.Lt / 0.45))) * H * 0.9;
                var o = J.E.InCubic(env.POut);
                dy += o * H * 0.08;
                var a = 1 - o;
                if (a <= 0.01)
                {
                    env.Body = null;
                    return;
                }
                ctx.Save();
                ctx.Translate((float)cx, (float)(cy + dy));
                if (rot != 0) ctx.RotateDegrees((float)rot);
                ctx.Scale((float)k, (float)k);
                ctx.Translate((float)(-pw / 2), (float)(-ph / 2));
                ShadowR(env, 0, 0, pw, ph, 0, a, u * 0.02);
                env.Rect(0, 0, pw, ph, C.Fill, a, false);
                if (C.Edge) env.Line(new[] { new[] { 0.0, 0.0 }, new[] { pw, 0.0 }, new[] { pw, ph }, new[] { 0.0, ph }, new[] { 0.0, 0.0 } }, C.Line, 1, a * 0.8, false);
                var m = pw * 0.035;
                var tx = C.Text;
                var lw = Math.Max(1, u * 0.0013);
                var hl = PlateCol(sc, new[] { sc.Str("accent") }, C.Fill, 2) == sc.Str("accent") ? sc.Str("accent")! : tx;
                var mw = Math.Min(pw * 0.13, ph * 0.12);
                var mh = Math.Min(ph * 0.34, mw * 2.6);
                var mX = pw - m - mw;
                var mY = m;
                env.Rect(mX, mY, mw, mh, hl, a, false);
                var mast = JLayouts.S(p, "mast")!;
                env.DrawItem(new JObject
                {
                    ["text"] = mast,
                    ["font"] = JLayouts.S(p, "font"),
                    ["size"] = Math.Min(mw * 0.62, mh * 0.9 / mast.Length),
                    ["vertical"] = true,
                    ["x"] = mX + mw / 2,
                    ["y"] = mY + mh / 2,
                    ["track"] = 0.05,
                    ["color"] = OnCol(sc, hl),
                    ["alpha"] = a,
                    ["ghost"] = false,
                });
                env.DrawItem(new JObject
                {
                    ["text"] = "第" + JLayouts.I(p, "issue") + "号",
                    ["font"] = BodyF(env),
                    ["size"] = ls * 0.7,
                    ["x"] = mX + mw / 2,
                    ["y"] = mY + mh + ls * 0.8,
                    ["color"] = tx,
                    ["alpha"] = a * 0.8,
                    ["ghost"] = false,
                });
                env.DrawItem(new JObject
                {
                    ["text"] = J.FmtTime(env.Cut.Start),
                    ["font"] = MonoF(env),
                    ["size"] = ls * 0.7,
                    ["x"] = mX + mw / 2,
                    ["y"] = mY + mh + ls * 1.8,
                    ["color"] = tx,
                    ["alpha"] = a * 0.8,
                    ["ghost"] = false,
                });
                var bodyX1 = mX - m * 0.6;
                var fs = J.Clamp(u * 0.012, 7, 15);
                var lh = fs * 1.45;
                var t0 = env.Cut.Text.Trim();
                BBox? bb = null;
                var reveal = Tin(env, 0.2, 0.9, J.E.Lin);
                void Tier(double x0, double y0, double x1, double y1, int seed)
                    => Greek(env, x0, y0 + fs * 0.4, x1 - x0, y1 - y0 - fs * 0.8, lh, tx, a * 0.42, seed, true, reveal);
                void Photo(double x0, double y0, double w, double h, int kind)
                {
                    Halftone(env, x0, y0, w, h, tx, J.Mix(C.Fill, tx, 0.12), a * reveal, JLayouts.I(p, "seed"), kind);
                    env.Line(new[] { new[] { x0, y0 + h + fs * 0.6 }, new[] { x0 + w * 0.7, y0 + h + fs * 0.6 } }, tx, fs * 0.5, a * 0.35, false);
                }
                void Rule(double x0, double y, double x1) => env.Line(new[] { new[] { x0, y }, new[] { x1, y } }, tx, lw, a * 0.7, false);
                if (JLayouts.S(p, "variant") == "tate")
                {
                    var hw = pw * (port ? 0.34 : 0.26);
                    var hh = ph - m * 2;
                    var hX1 = mX - m * 0.8;
                    var fb = FitBlock(Strip(t0), JLayouts.S(p, "font")!, hw * 0.92, hh * 0.94,
                        new JObject { ["vertical"] = true, ["lead"] = 1.1, ["track"] = -0.02 }, 2);
                    var size = fb.Size;
                    var mm = Meas(fb.Text, JLayouts.S(p, "font")!, size, new JObject { ["vertical"] = true, ["lead"] = 1.1, ["track"] = -0.02 });
                    var rev = JLayouts.Truthy(p["rev"]);
                    if (rev) env.Rect(hX1 - mm.W - size * 0.3, m, mm.W + size * 0.3, mm.H + size * 0.4, tx, a, false);
                    bb = JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = fb.Text,
                        ["font"] = JLayouts.S(p, "font"),
                        ["size"] = size,
                        ["x"] = hX1 - size * 0.15 - mm.W / 2,
                        ["y"] = m + size * 0.2,
                        ["vertical"] = true,
                        ["align"] = "left",
                        ["lead"] = 1.1,
                        ["track"] = -0.02,
                        ["color"] = rev ? C.Fill : tx,
                        ["noHold"] = PlateHold(env),
                        ["mi"] = MiAt(env, JLayouts.Truthy(p["spin"]) ? 0.45 : 0.3),
                    });
                    var bx1 = hX1 - mm.W - size * 0.5;
                    var sub = DeckCopy(env);
                    if (sub != null)
                        env.DrawItem(new JObject
                        {
                            ["text"] = string.Concat(JLayouts.CodePoints(Strip(sub)).Take(14)),
                            ["font"] = JLayouts.S(p, "font"),
                            ["size"] = Math.Min(size * 0.3, (ph - m * 2) / 15),
                            ["vertical"] = true,
                            ["align"] = "left",
                            ["x"] = bx1 - size * 0.2,
                            ["y"] = m + size * 0.2,
                            ["color"] = tx,
                            ["alpha"] = a,
                            ["ghost"] = false,
                        });
                    var gx1 = bx1 - (sub != null ? size * 0.5 : 0);
                    var tiers = port ? 5 : 4;
                    var th2 = (ph - m * 2) / tiers;
                    for (var i = 0; i < tiers; i++)
                    {
                        var y0 = m + i * th2;
                        if (i > 0) Rule(m, y0, gx1);
                        if (i == 1 && !port)
                        {
                            var iw = (gx1 - m) * 0.42;
                            Photo(m, y0 + fs, iw, th2 * 2 - fs * 3, 0);
                            Tier(m + iw + fs, y0, gx1, y0 + th2, JLayouts.I(p, "seed") + i);
                            continue;
                        }
                        if (i == 2 && !port)
                        {
                            Tier(m + (gx1 - m) * 0.42 + fs, y0, gx1, y0 + th2, JLayouts.I(p, "seed") + i);
                            continue;
                        }
                        Tier(m, y0, gx1, y0 + th2, JLayouts.I(p, "seed") + i);
                    }
                    Rule(bodyX1 + m * 0.3, m + mh + ls * 2.6, pw - m);
                    Tier(mX, m + mh + ls * 2.8, pw - m, ph - m, JLayouts.I(p, "seed") + 9);
                }
                else
                {
                    var hbw = bodyX1 - m;
                    var hbh = ph * (port ? 0.3 : 0.32);
                    var fb = FitBlock(t0, JLayouts.S(p, "font")!, hbw * 0.94, hbh * 0.84, new JObject { ["lead"] = 1.05, ["track"] = -0.02 }, port ? 3 : 2);
                    var size = fb.Size;
                    var rev = JLayouts.Truthy(p["rev"]);
                    if (rev) env.Rect(m, m, hbw, hbh, tx, a, false);
                    bb = JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = fb.Text,
                        ["font"] = JLayouts.S(p, "font"),
                        ["size"] = size,
                        ["x"] = m + hbw / 2,
                        ["y"] = m + hbh / 2,
                        ["lead"] = 1.05,
                        ["track"] = -0.02,
                        ["color"] = rev ? C.Fill : tx,
                        ["noHold"] = PlateHold(env),
                        ["mi"] = MiAt(env, JLayouts.Truthy(p["spin"]) ? 0.45 : 0.3),
                    });
                    var yb = m + hbh + fs;
                    Rule(m, yb, bodyX1);
                    var tiers = port ? 5 : 3;
                    var th2 = (ph - m - yb) / tiers;
                    for (var i = 0; i < tiers; i++)
                    {
                        var y0 = yb + i * th2;
                        if (i > 0) Rule(m, y0, pw - m);
                        var x1 = i == 0 ? bodyX1 : pw - m;
                        if (i == 1)
                        {
                            var iw = (x1 - m) * (port ? 0.5 : 0.3);
                            Photo(x1 - iw, y0 + fs, iw, th2 - fs * 3, 1);
                            Tier(m, y0, x1 - iw - fs, y0 + th2, JLayouts.I(p, "seed") + i);
                            continue;
                        }
                        Tier(m, y0, x1, y0 + th2, JLayouts.I(p, "seed") + i);
                    }
                    Tier(mX, m + mh + ls * 2.8, pw - m, yb, JLayouts.I(p, "seed") + 9);
                }
                ctx.Restore();
                var pb = Box(cx - pw / 2, cy - ph / 2 + dy, cx + pw / 2, cy + ph / 2 + dy);
                env.Body = bb != null ? pb : null;
            },
        });
    }

        private static void Disc(Env env, double cx, double cy, double R, double ang, string labC, double a, string? txt, int seed, bool big = false)
    {
        var sc = env.Sc!;
        var ctx = env.Ctx;
        var vin = J.Lum(sc.Str("bg")!) < 0.3 ? J.Mix(Darkest(sc), Lightest(sc), 0.1) : Darkest(sc);
        env.Circle(cx, cy, R, vin, null, 0, a, false);
        if (env.Pass == "main")
        {
            ctx.Save();
            var light = Lightest(sc);
            using (var groove = new SKPaint
            {
                IsAntialias = true,
                IsStroke = true,
                StrokeWidth = (float)Math.Max(1, R * 0.004),
                Color = Env.SkColor(light, a * 0.1),
                BlendMode = env.Blend,
            })
            {
                using var gp = new SKPath();
                for (var i = 0; i < 16; i++)
                {
                    var r = R * (0.42 + 0.55 * i / 15);
                    gp.AddCircle((float)cx, (float)cy, (float)r);
                }
                ctx.DrawPath(gp, groove);
            }
            using (var hl = new SKPaint
            {
                IsAntialias = true,
                BlendMode = env.Blend,
                Shader = SKShader.CreateSweepGradient(
                        new SKPoint((float)cx, (float)cy),
                        new[]
                        {
                            Env.SkColor(light, 0), Env.SkColor(light, 0.16 * a), Env.SkColor(light, 0),
                            Env.SkColor(light, 0), Env.SkColor(light, 0.12 * a), Env.SkColor(light, 0), Env.SkColor(light, 0)
                        },
                        new[] { 0f, 0.08f, 0.16f, 0.5f, 0.58f, 0.66f, 1f },
                        SKShaderTileMode.Clamp, (float)(90 + -0.6 / J.DEG), (float)(90 + -0.6 / J.DEG + 360)),
            })
            {
                using var donut = new SKPath();
                donut.AddCircle((float)cx, (float)cy, (float)(R * 0.98));
                donut.AddCircle((float)cx, (float)cy, (float)(R * 0.4));
                donut.FillType = SKPathFillType.EvenOdd;
                ctx.DrawPath(donut, hl);
            }
            ctx.Restore();
        }
        var LR = R * (big ? 0.5 : 0.36);
        env.Circle(cx, cy, LR, labC, null, 0, a, false);
        var lc = OnCol(env.Sc!, labC);
        ctx.Save();
        ctx.Translate((float)cx, (float)cy);
        ctx.RotateDegrees((float)ang);
        if (!string.IsNullOrEmpty(txt)) ArcText(env, txt!, MonoF(env), R * 0.034, 0, 0, LR * 0.84, -90, lc, a * 0.9, 0.25);
        env.Arc(0, 0, LR * 0.7, 20, 160, lc, Math.Max(1, R * 0.004), a * 0.6, false);
        ctx.Restore();
        env.Circle(cx, cy, R * 0.022, sc.Str("bg"), null, 0, a, false);
    }

    private static void RegisterVinyl()
    {
        RegC("vinyl", new LayoutDef
        {
            Name = "レコード",
            Tags = new[] { "emotional", "pop", "calm" },
            W = 0.8,
            Treat = "safe",
            Fits = n => n <= 16,
            EnterBias = new JObject { ["blur"] = 1.3, ["zoom"] = 1.2, ["spin"] = 0.4 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
                ["variant"] = cut.N <= 6 ? rng.Pick(new[] { "sleeve", "label" }) : "sleeve",
                ["side"] = rng.Pick(new[] { "A", "B" }),
                ["rpm"] = rng.Pick(new[] { "33⅓", "45" }),
                ["sleeve"] = rng.Pick(new[] { "ink", "accent", "card" }),
                ["cat"] = rng.Int(100, 9999),
                ["arm"] = rng.Chance(0.7),
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var ctx = env.Ctx;
                var p = env.P;
                var u = U(env);
                var port = IsPort(env);
                var outK = Tout(env);
                var ls = SmallSize(env);
                var t0 = env.Cut.Text.Trim();
                var labC = PlateCol(sc, new[] { sc.Str("accent"), sc.Str("accent2"), sc.Str("fg") }, Darkest(sc), 1.5);
                var arc = "SIDE " + JLayouts.S(p, "side") + "  ·  " + JLayouts.S(p, "rpm") + " RPM  ·  No." + LineNo(env) + "  ·  ";
                if (JLayouts.S(p, "variant") == "label")
                {
                    var R = Math.Min(H * 0.44, W * 0.44);
                    var cx = W / 2;
                    var cy = H / 2;
                    var a = Tin(env, 0, 0.3, J.E.OutCubic) * (1 - J.Clamp((env.POut - 0.6) / 0.4));
                    var T = Math.Max(0.5, env.Cut.InDur + 0.35);
                    var ang = 540 * (1 - J.E.OutCubic(J.Clamp(env.Lt / T))) - 300 * J.E.InCubic(env.POut);
                    var k = J.Lerp(0.9, 1, J.E.OutCubic(J.Clamp(env.Lt / 0.4)));
                    ctx.Save();
                    ctx.Translate((float)cx, (float)cy);
                    ctx.Scale((float)k, (float)k);
                    ctx.Translate((float)-cx, (float)-cy);
                    Disc(env, cx, cy, R, ang, labC, a, JLayouts.Repeat(arc, 2), JLayouts.I(p, "cat"), true);
                    if (JLayouts.Truthy(p["arm"]))
                    {
                        var q = Tin(env, 0.2, 0.7, J.E.InOutCubic) * outK;
                        var px = cx + R * 1.02;
                        var py = cy - R * 0.92;
                        var L = R * 1.05;
                        var th = (-58 + q * 30) * J.DEG + Math.PI / 2;
                        var ex = px + Math.Cos(th) * L;
                        var ey = py + Math.Sin(th) * L;
                        env.Line(new[] { new[] { px, py }, new[] { ex, ey } }, sc.Str("sub"), Math.Max(3, R * 0.02), a, false);
                        env.Rect(ex - R * 0.035, ey - R * 0.035, R * 0.07, R * 0.07, sc.Str("fg"), a, false);
                        env.Circle(px, py, R * 0.06, sc.Str("sub"), null, 0, a, false);
                    }
                    var lab = R * 0.5;
                    var fb = FitBlock(t0, JLayouts.S(p, "font")!, lab * 1.3, lab * 0.8, new JObject { ["lead"] = 1.1 }, 2);
                    ctx.Save();
                    ctx.Translate((float)cx, (float)cy);
                    ctx.RotateDegrees((float)ang);
                    var bb0 = JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = fb.Text,
                        ["font"] = JLayouts.S(p, "font"),
                        ["size"] = Math.Min(fb.Size, lab * 0.55),
                        ["x"] = 0,
                        ["y"] = 0,
                        ["lead"] = 1.1,
                        ["color"] = OnCol(sc, labC),
                        ["noHold"] = true,
                        ["plain"] = true,
                    });
                    ctx.Restore();
                    ctx.Restore();
                    env.Body = bb0 != null ? Box(cx - lab, cy - lab, cx + lab, cy + lab) : null;
                    return;
                }
                var S = port ? Math.Min(W * 0.74, H * 0.4) : Math.Min(H * 0.74, W * 0.42);
                var sx = port ? W / 2 : W / 2 - S * 0.28;
                var sy = port ? H / 2 - S * 0.28 : H / 2;
                var ein = J.E.OutExpo(J.Clamp(env.Lt / 0.45));
                var slide = J.E.InOutCubic(J.Clamp((env.Lt - 0.25) / 0.7)) * (1 - J.E.InOutCubic(env.POut));
                var a2 = J.Clamp(ein * 2) * (1 - J.Clamp((env.POut - 0.5) / 0.5));
                var off = (1 - ein) * S * 0.25;
                var R2 = S * 0.47;
                var dcx = port ? sx : sx + slide * S * 0.56;
                var dcy = port ? sy + slide * S * 0.56 : sy;
                Disc(env, dcx + (port ? 0 : off), dcy + (port ? off : 0), R2, env.Ltb * 120, labC, a2, JLayouts.Repeat(arc, 2), JLayouts.I(p, "cat"));
                var sleeveKind = JLayouts.S(p, "sleeve");
                var slC = sleeveKind == "card"
                    ? Card(sc).Fill
                    : PlateCol(sc, sleeveKind == "accent" ? new[] { sc.Str("accent"), sc.Str("ink") } : new[] { sc.Str("ink"), sc.Str("fg") });
                var tc = OnCol(sc, slC);
                var x0 = sx - S / 2 + (port ? 0 : off);
                var y0 = sy - S / 2 + (port ? off : 0);
                ShadowR(env, x0, y0, S, S, 0, a2, u * 0.015);
                env.Rect(x0, y0, S, S, slC, a2, false);
                if (J.Contrast(slC, sc.Str("bg")!) < 1.4)
                    env.Line(new[] { new[] { x0, y0 }, new[] { x0 + S, y0 }, new[] { x0 + S, y0 + S }, new[] { x0, y0 + S }, new[] { x0, y0 } },
                        J.Mix(sc.Str("fg")!, slC, 0.4), 1.5, a2, false);
                var m = S * 0.07;
                var fa = Tin(env, 0.15, 0.4, J.E.OutCubic) * outK;
                env.DrawItem(new JObject
                {
                    ["text"] = "JZR-" + JLayouts.I(p, "cat"),
                    ["font"] = MonoF(env),
                    ["size"] = ls * 0.9,
                    ["track"] = 0.2,
                    ["align"] = "right",
                    ["x"] = x0 + S - m,
                    ["y"] = y0 + m + ls * 0.3,
                    ["color"] = tc,
                    ["alpha"] = fa,
                    ["ghost"] = false,
                });
                env.DrawItem(new JObject
                {
                    ["text"] = "SIDE " + JLayouts.S(p, "side"),
                    ["font"] = MonoF(env),
                    ["size"] = ls * 0.9,
                    ["track"] = 0.2,
                    ["align"] = "left",
                    ["x"] = x0 + m,
                    ["y"] = y0 + m + ls * 0.3,
                    ["color"] = tc,
                    ["alpha"] = fa,
                    ["ghost"] = false,
                });
                env.Line(new[] { new[] { x0 + m, y0 + m + ls * 1.3 }, new[] { x0 + m + (S - m * 2) * fa, y0 + m + ls * 1.3 } }, tc, Math.Max(1, u * 0.0015), 0.8, false);
                var fb2 = FitBlock(t0, JLayouts.S(p, "font")!, S - m * 2, S * 0.52, new JObject { ["lead"] = 1.05, ["track"] = 0.01 }, 4);
                var size = Math.Min(fb2.Size, S * 0.3);
                var mm = Meas(fb2.Text, JLayouts.S(p, "font")!, size, new JObject { ["lead"] = 1.05, ["track"] = 0.01 });
                var bb = JLayouts.MainDraw(env, new JObject
                {
                    ["text"] = fb2.Text,
                    ["font"] = JLayouts.S(p, "font"),
                    ["size"] = size,
                    ["x"] = x0 + m,
                    ["y"] = y0 + S - m - ls * 1.6 - mm.H / 2,
                    ["align"] = "left",
                    ["lead"] = 1.05,
                    ["track"] = 0.01,
                    ["color"] = tc,
                    ["noHold"] = PlateHold(env),
                    ["mi"] = MiAt(env, 0.22),
                });
                env.DrawItem(new JObject
                {
                    ["text"] = Sl(JLayouts.OrStr(RomajiOf(env), J.FmtTime(env.Cut.Start))!, 26),
                    ["font"] = MonoF(env),
                    ["size"] = ls * 0.8,
                    ["track"] = 0.15,
                    ["align"] = "left",
                    ["x"] = x0 + m,
                    ["y"] = y0 + S - m,
                    ["color"] = tc,
                    ["alpha"] = fa * 0.8,
                    ["ghost"] = false,
                });
                env.Body = bb ?? Box(x0, y0, x0 + S, y0 + S);
            },
        });
    }

    private static void RegisterCassette()
    {
        RegC("cassette", new LayoutDef
        {
            Name = "カセット",
            Tags = new[] { "emotional", "pop", "calm" },
            W = 0.8,
            Treat = "safe",
            Portrait = 0.7,
            Fits = n => n <= 16,
            EnterBias = new JObject { ["type"] = 1.6, ["wipe"] = 1.3, ["cut"] = 1.2 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Chance(0.55) ? "klee" : rng.Pick(FontsOf(st, "body", "display")),
                ["shell"] = rng.Pick(new[] { "ink", "accent", "clear" }),
                ["band"] = rng.Pick(new[] { "accent", "ink", "stripe" }),
                ["side"] = rng.Pick(new[] { "A", "B" }),
                ["tilt"] = rng.Range(-4, 4),
                ["len"] = rng.Pick(new[] { "C-46", "C-60", "C-90" }),
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var ctx = env.Ctx;
                var p = env.P;
                var u = U(env);
                var port = IsPort(env);
                var outK = Tout(env);
                var ls = SmallSize(env);
                var cw = port ? W * 0.92 : Math.Min(W * 0.66, H * 0.8 * 1.58);
                var ch = cw / 1.58;
                var ein = J.E.OutCubic(J.Clamp(env.Lt / 0.5));
                var eo = J.E.InCubic(env.POut);
                var a = J.Clamp(ein * 2) * (1 - eo);
                if (a <= 0.01)
                {
                    env.Body = null;
                    return;
                }
                var cx = W / 2;
                var cy = H / 2 + (1 - ein) * H * 0.6 + eo * H * 0.15;
                var rot = JLayouts.D(p, "tilt") * (1 - eo) + (1 - ein) * 12;
                var clear = JLayouts.S(p, "shell") == "clear";
                var shellC = clear
                    ? sc.Str("bg")!
                    : PlateCol(sc, JLayouts.S(p, "shell") == "accent" ? new[] { sc.Str("accent"), sc.Str("ink") } : new[] { sc.Str("ink"), sc.Str("fg") });
                var lineC = clear ? sc.Str("fg")! : J.Mix(shellC, OnCol(sc, shellC), 0.3);
                var C = Card(sc);
                var labF = J.Contrast(C.Fill, shellC) > 1.3 ? C.Fill : PlateCol(sc, new[] { sc.Str("bg"), sc.Str("fg") }, shellC, 1.5);
                var labT = OnCol(sc, labF);
                var bandC = JLayouts.S(p, "band") == "ink"
                    ? PlateCol(sc, new[] { sc.Str("ink"), sc.Str("fg") }, labF, 2)
                    : PlateCol(sc, new[] { sc.Str("accent"), sc.Str("accent2"), sc.Str("ink") }, labF, 1.6);
                ctx.Save();
                ctx.Translate((float)cx, (float)cy);
                ctx.RotateDegrees((float)rot);
                var x0 = -cw / 2;
                var y0 = -ch / 2;
                var r = ch * 0.05;
                ShadowR(env, x0, y0, cw, ch, r, a, u * 0.015);
                env.RRect(x0, y0, cw, ch, r, shellC, a, false, clear ? sc.Str("fg") : null, Math.Max(2, u * 0.003));
                foreach (var s in new[]
                {
                    new[] { x0 + ch * 0.07, y0 + ch * 0.07 }, new[] { x0 + cw - ch * 0.07, y0 + ch * 0.07 },
                    new[] { x0 + ch * 0.07, y0 + ch - ch * 0.07 }, new[] { x0 + cw - ch * 0.07, y0 + ch - ch * 0.07 },
                    new[] { 0.0, y0 + ch * 0.9 }
                })
                    env.Circle(s[0], s[1], ch * 0.022, null, lineC, Math.Max(1, u * 0.0015), a, false);
                var lx = x0 + cw * 0.06;
                var ly = y0 + ch * 0.08;
                var lw2 = cw * 0.88;
                var lh2 = ch * 0.62;
                env.RRect(lx, ly, lw2, lh2, ch * 0.02, labF, a, false);
                if (JLayouts.S(p, "band") == "stripe")
                {
                    env.Rect(lx, ly + lh2 * 0.62, lw2, lh2 * 0.06, PlateCol(sc, new[] { sc.Str("accent") }, labF, 1.4), a, false);
                    env.Rect(lx, ly + lh2 * 0.7, lw2, lh2 * 0.06, PlateCol(sc, new[] { sc.Str("accent2"), sc.Str("ink") }, labF, 1.4), a, false);
                }
                else env.Rect(lx, ly + lh2 * 0.62, lw2, lh2 * 0.12, bandC, a, false);
                var sb = lh2 * 0.3;
                env.Rect(lx + lw2 * 0.03, ly + lh2 * 0.08, sb, sb, labT, a, false);
                env.DrawItem(new JObject
                {
                    ["text"] = JLayouts.S(p, "side"),
                    ["font"] = "gothic_black",
                    ["size"] = sb * 0.78,
                    ["x"] = lx + lw2 * 0.03 + sb / 2,
                    ["y"] = ly + lh2 * 0.08 + sb / 2,
                    ["color"] = labF,
                    ["alpha"] = a,
                    ["ghost"] = false,
                });
                env.DrawItem(new JObject
                {
                    ["text"] = JLayouts.S(p, "len") + "  ·  NR  ·  No." + LineNo(env),
                    ["font"] = MonoF(env),
                    ["size"] = ls * 0.75,
                    ["track"] = 0.2,
                    ["align"] = "right",
                    ["x"] = lx + lw2 * 0.97,
                    ["y"] = ly + lh2 * 0.9,
                    ["color"] = labT,
                    ["alpha"] = a * 0.8,
                    ["ghost"] = false,
                });
                var tx0 = lx + lw2 * 0.06 + sb;
                var tw = lw2 * 0.9 - sb;
                var rl = ly + lh2 * 0.5;
                env.Line(new[] { new[] { tx0, rl }, new[] { tx0 + tw, rl } }, labT, Math.Max(1, u * 0.0012), a * 0.35, false);
                var wy0 = y0 + ch * 0.44;
                env.RRect(-cw * 0.3, wy0, cw * 0.6, ch * 0.24, ch * 0.12, J.Mix(shellC, Darkest(sc), 0.35), a, false);
                var rr = ch * 0.1;
                var rxs = new[] { -cw * 0.19, cw * 0.19 };
                var ry = wy0 + ch * 0.12;
                var spin = env.Ltb * 200 * (1 + eo * 3);
                for (var i = 0; i < rxs.Length; i++)
                {
                    var rx = rxs[i];
                    env.Circle(rx, ry, rr * (i != 0 ? 0.75 : 1.05), J.Mix(Darkest(sc), shellC, 0.2), null, 0, a, false);
                    env.Circle(rx, ry, rr * 0.5, labF, null, 0, a, false);
                    for (var k = 0; k < 6; k++)
                    {
                        var an = (spin + k * 60) * J.DEG;
                        env.Line(new[]
                        {
                            new[] { rx + Math.Cos(an) * rr * 0.2, ry + Math.Sin(an) * rr * 0.2 },
                            new[] { rx + Math.Cos(an) * rr * 0.46, ry + Math.Sin(an) * rr * 0.46 }
                        }, labT, Math.Max(2, rr * 0.08), a, false);
                    }
                }
                var t0 = env.Cut.Text.Trim();
                var fb = FitBlock(t0, JLayouts.S(p, "font")!, tw, lh2 * 0.46, new JObject { ["lead"] = 1.05 }, 2);
                var size = Math.Min(fb.Size, lh2 * 0.32);
                var mm = Meas(fb.Text, JLayouts.S(p, "font")!, size, new JObject { ["lead"] = 1.05 });
                var bb = JLayouts.MainDraw(env, new JObject
                {
                    ["text"] = fb.Text,
                    ["font"] = JLayouts.S(p, "font"),
                    ["size"] = size,
                    ["x"] = tx0 + size * 0.1,
                    ["y"] = rl - mm.H / 2 - size * 0.08,
                    ["align"] = "left",
                    ["lead"] = 1.05,
                    ["rot"] = -1.2,
                    ["color"] = labT,
                    ["noHold"] = PlateHold(env),
                    ["mi"] = MiAt(env, 0.28),
                });
                ctx.Restore();
                env.Body = bb != null ? Box(cx - cw / 2, cy - ch / 2, cx + cw / 2, cy + ch / 2) : null;
            },
        });
    }

        private static JArray BookSpineBooks(Rng rng, int nb)
    {
        var books = new JArray();
        for (var i = 0; i < nb; i++)
            books.Add(new JArray(rng.Range(0.5, 1), rng.Range(0.62, 0.92), rng.Int(0, 5), rng.Int(0, 3)));
        return books;
    }

    private static void RegisterBookSpine()
    {
        RegC("bookSpine", new LayoutDef
        {
            Name = "背表紙",
            Tags = new[] { "calm", "editorial", "emotional" },
            W = 0.8,
            Treat = "safe",
            Portrait = 1.1,
            Fits = n => n >= 1 && n <= 14,
            EnterBias = new JObject { ["wipe"] = 1.3, ["blur"] = 1.2, ["cut"] = 1.2 },
            Plan = (rng, cut, st) =>
            {
                var port = PortOf(cut);
                var nb = port ? rng.Int(5, 7) : rng.Int(8, 12);
                return new JObject
                {
                    ["font"] = rng.Pick(FontsOf(st, "serif", "display")),
                    ["variant"] = HasLatin(cut.Text) ? "pile" : rng.Pick(new[] { "shelf", "shelf", "pile" }),
                    ["books"] = BookSpineBooks(rng, nb),
                    ["hero"] = rng.Pick(new[] { "accent", "ink" }),
                    ["vol"] = rng.Int(1, 24),
                };
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var ctx = env.Ctx;
                var p = env.P;
                var u = U(env);
                var port = IsPort(env);
                var outK = Tout(env);
                var ls = SmallSize(env);
                var t0 = env.Cut.Text.Trim();
                var heroC = PlateCol(sc, JLayouts.S(p, "hero") == "accent" ? new[] { sc.Str("accent"), sc.Str("ink") } : new[] { sc.Str("ink"), sc.Str("accent") });
                var htc = OnCol(sc, heroC);
                var pal = new[]
                {
                    J.Mix(sc.Str("bg")!, sc.Str("fg")!, 0.18), J.Mix(sc.Str("bg")!, sc.Str("sub")!, 0.45),
                    J.Mix(sc.Str("bg")!, sc.Str("accent")!, 0.35), sc.Str("dim")!,
                    J.Mix(sc.Str("bg")!, sc.Str("fg")!, 0.32), J.Mix(sc.Str("bg")!, sc.Str("accent2") ?? sc.Str("sub")!, 0.3)
                };
                var eo = J.E.InCubic(env.POut);
                BBox? bb = null;
                var booksArr = (JArray)p["books"]!;
                if (JLayouts.S(p, "variant") == "pile")
                {
                    var bw = port ? W * 0.84 : Math.Min(W * 0.62, H * 1.1);
                    var fb = FitBlock(t0, JLayouts.S(p, "font")!, bw * 0.78, H * 0.16, new JObject { ["track"] = 0.04 }, 1);
                    var size = Math.Min(fb.Size, u * 0.12);
                    var hh = size * 1.7;
                    var nb = Math.Min(6, booksArr.Count);
                    var hi = (int)Math.Floor(nb / 2.0);
                    var hs = new double[nb];
                    for (var i = 0; i < nb; i++)
                    {
                        var b = (JArray)booksArr[i]!;
                        hs[i] = i == hi ? hh : H * 0.045 + (double)b[0]! * H * 0.05;
                    }
                    var total = hs.Sum();
                    var y = H / 2 + total / 2 + H * 0.03;
                    var floor = y;
                    env.Line(new[] { new[] { W * 0.08, floor }, new[] { W * 0.92, floor } }, sc.Str("sub"), Math.Max(2, u * 0.003), Tin(env, 0, 0.4) * outK, false);
                    for (var i = 0; i < nb; i++)
                    {
                        var b = (JArray)booksArr[i]!;
                        var h = hs[i];
                        var w = i == hi ? bw : bw * (0.72 + 0.26 * (double)b[1]!);
                        var b2 = (int)(double)b[2]!;
                        var xo = J.Rs(b2, i, 3) * bw * 0.05;
                        var t = 0.04 + i * 0.09;
                        var e = J.E.OutCubic(J.Clamp((env.Lt - t) / 0.3));
                        y -= h;
                        if (e <= 0) continue;
                        var yy = y - (1 - e) * H * 0.6 - eo * (nb - i) * H * 0.02;
                        var col = i == hi ? heroC : pal[b2 % pal.Length];
                        var a = J.Clamp(e * 3) * (1 - eo);
                        env.Rect(W / 2 - w / 2 + xo, yy, w, h - 2, col, a, false);
                        var bc = i == hi ? htc : J.Mix(col, sc.Str("fg")!, 0.35);
                        env.Rect(W / 2 - w / 2 + xo + w * 0.06, yy, Math.Max(2, w * 0.008), h - 2, bc, a * 0.7, false);
                        env.Rect(W / 2 + w / 2 + xo - w * 0.07, yy, Math.Max(2, w * 0.008), h - 2, bc, a * 0.7, false);
                        if (i == hi)
                        {
                            env.DrawItem(new JObject
                            {
                                ["text"] = Pad2(JLayouts.I(p, "vol")),
                                ["font"] = MonoF(env),
                                ["size"] = h * 0.24,
                                ["x"] = W / 2 + w / 2 + xo - w * 0.035,
                                ["y"] = yy + h / 2,
                                ["rot"] = -90,
                                ["color"] = htc,
                                ["alpha"] = a,
                                ["ghost"] = false,
                            });
                            bb = JLayouts.MainDraw(env, new JObject
                            {
                                ["text"] = fb.Text,
                                ["font"] = JLayouts.S(p, "font"),
                                ["size"] = size,
                                ["x"] = W / 2 + xo - w * 0.02,
                                ["y"] = yy + h / 2,
                                ["track"] = 0.04,
                                ["color"] = htc,
                                ["noHold"] = PlateHold(env),
                                ["mi"] = MiAt(env, 0.1 + hi * 0.09 + 0.2),
                            });
                        }
                        else if (h > H * 0.05)
                            env.Rect(W / 2 - w * 0.22 + xo, yy + h * 0.42, w * 0.3 * (double)b[1]!, h * 0.16, J.Mix(col, sc.Str("fg")!, 0.3), a * 0.7, false);
                    }
                    env.Body = bb;
                    return;
                }
                var n = JLayouts.GlyphCount(t0);
                var cols = n > (port ? 12 : 10) ? 2 : 1;
                var vt = cols > 1 ? Brk(Strip(t0), (int)Math.Ceiling(n / 2.0)) : Strip(t0);
                var per = vt.Split('\n').Max(l => JLayouts.GlyphCount(l));
                var shelfY = H * (port ? 0.84 : 0.88);
                var maxH = H * (port ? 0.7 : 0.78);
                var size2 = Math.Min(maxH * 0.76 / (Math.Max(per, 3) * 1.02 + 1.9), u * (cols > 1 ? 0.09 : port ? 0.14 : 0.115));
                var hw = size2 * (cols > 1 ? 2.9 : 1.75);
                var hh2 = Math.Min(maxH, (per * size2 * 1.02 + size2 * 1.9) / 0.76);
                var nb2 = booksArr.Count;
                var hi2 = (int)Math.Floor(nb2 / 2.0);
                var ws = new double[nb2];
                for (var i = 0; i < nb2; i++)
                {
                    var b = (JArray)booksArr[i]!;
                    ws[i] = i == hi2 ? hw : size2 * (0.9 + 0.8 * (double)b[0]!);
                }
                var tot = ws.Sum() + nb2 * 2;
                var x = W / 2 - tot / 2;
                var shA = Tin(env, 0, 0.4) * outK;
                env.Rect(W * 0.03, shelfY, W * 0.94 * shA, Math.Max(4, u * 0.008), sc.Str("sub"), 1, false);
                for (var i = 0; i < nb2; i++)
                {
                    var b = (JArray)booksArr[i]!;
                    var w = ws[i];
                    var h = i == hi2 ? hh2 : Math.Min(maxH, hh2 * (0.7 + 0.3 * (double)b[1]!));
                    var t = 0.03 + Math.Abs(i - hi2) * 0.05;
                    var e = J.E.OutExpo(J.Clamp((env.Lt - t) / 0.4));
                    var bx = x;
                    x += w + 2;
                    if (e <= 0 || bx + w < 0 || bx > W) continue;
                    var lift = i == hi2 ? J.E.InOutCubic(J.Clamp((env.Lt - 0.35) / 0.4)) * size2 * 0.6 * (1 - eo) : 0;
                    var yy = shelfY - h * e - lift + eo * h * 1.05;
                    var a = 1 - eo * 0.4;
                    ctx.Save();
                    ctx.ClipRect(new SKRect((float)-W, (float)-H, (float)(W * 2), (float)(shelfY - 1)));
                    var col = i == hi2 ? heroC : pal[(int)(double)b[2]! % pal.Length];
                    env.Rect(bx, yy, w, h, col, a, false);
                    var bc = i == hi2 ? htc : J.Mix(col, sc.Str("fg")!, 0.3);
                    var bandH = Math.Max(2, h * 0.012);
                    foreach (var f in new[] { 0.06, 0.08, 0.92, 0.94 })
                        env.Rect(bx, yy + h * f, w, bandH, bc, a * 0.75, false);
                    if (i == hi2)
                    {
                        env.Rect(bx + w * 0.2, yy + h * 0.11, w * 0.6, size2 * 0.8, htc, a, false);
                        env.DrawItem(new JObject
                        {
                            ["text"] = KanjiNum(JLayouts.I(p, "vol")),
                            ["font"] = SerifF(env),
                            ["size"] = size2 * 0.46,
                            ["x"] = bx + w / 2,
                            ["y"] = yy + h * 0.11 + size2 * 0.4,
                            ["color"] = heroC,
                            ["alpha"] = a,
                            ["ghost"] = false,
                        });
                        env.Circle(bx + w / 2, yy + h * 0.87 - size2 * 0.3, size2 * 0.26, null, htc, Math.Max(1.5, size2 * 0.04), a, false);
                        bb = JLayouts.MainDraw(env, new JObject
                        {
                            ["text"] = vt,
                            ["font"] = JLayouts.S(p, "font"),
                            ["size"] = size2,
                            ["x"] = bx + w / 2,
                            ["y"] = yy + h * 0.11 + size2 * 1.2,
                            ["vertical"] = true,
                            ["align"] = "left",
                            ["lead"] = 1.24,
                            ["track"] = 0.02,
                            ["color"] = htc,
                            ["noHold"] = PlateHold(env),
                            ["mi"] = MiAt(env, 0.3),
                        });
                    }
                    else
                    {
                        var tl = h * (0.25 + 0.3 * (double)b[1]!);
                        var b3 = (int)(double)b[3]!;
                        if (b3 > 0) env.Rect(bx + w * 0.38, yy + h * 0.16, w * 0.24, tl, bc, a * 0.55, false);
                        if (b3 == 2) env.Circle(bx + w / 2, yy + h * 0.84, w * 0.14, bc, null, 0, a * 0.55, false);
                    }
                    ctx.Restore();
                }
                env.Body = bb;
            },
        });
    }

    private static void RegisterPolaroid()
    {
        RegC("polaroid", new LayoutDef
        {
            Name = "ポラロイド",
            Tags = new[] { "emotional", "calm", "pop" },
            W = 0.9,
            Treat = "safe",
            Fits = n => n >= 1 && n <= 16,
            EnterBias = new JObject { ["blur"] = 1.6, ["cut"] = 1.2, ["drop"] = 1.2, ["slice"] = 0.5 },
            Plan = (rng, cut, st) =>
            {
                var n = cut.N;
                var k = n <= 5 ? 1 : n <= 10 ? 2 : 3;
                return new JObject
                {
                    ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
                    ["pen"] = rng.Chance(0.6) ? "klee" : rng.Pick(FontsOf(st, "body")),
                    ["chunks"] = new JArray(SplitK(cut.Text, k, k)),
                    ["tilts"] = new JArray(rng.Range(-7, 7), rng.Range(-7, 7), rng.Range(-7, 7)),
                    ["img"] = rng.Pick(new[] { "dark", "accent", "dusk" }),
                    ["tape"] = rng.Chance(0.5),
                };
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var ctx = env.Ctx;
                var p = env.P;
                var u = U(env);
                var port = IsPort(env);
                var chunks = (p["chunks"] as JArray)?.Select(t => (string?)t).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();
                if (chunks == null || chunks.Count == 0) chunks = new List<string> { env.Cut.Text.Trim() };
                var k = chunks.Count;
                var C = Card(sc);
                var eo = J.E.InCubic(env.POut);
                double fw;
                if (port) fw = Math.Min(W * (k == 1 ? 0.78 : 0.62), H * 0.78 / (k == 1 ? 1.2 : 1 + (k - 1) * 0.72) / 1.2);
                else fw = Math.Min(H * 0.8 / 1.2, W * 0.86 / (k == 1 ? 1 : k * 0.92));
                var fh = fw * 1.2;
                var iw = fw * 0.88;
                double[] Pos(int i)
                {
                    if (k == 1) return new[] { W / 2.0, H / 2.0 };
                    if (port) return new[] { W / 2 + (i % 2 != 0 ? 1 : -1) * W * 0.07, H / 2 + (i - (k - 1) / 2.0) * fh * 0.72 };
                    return new[] { W / 2 + (i - (k - 1) / 2.0) * fw * 0.92, H / 2 + (i % 2 != 0 ? 1 : -1) * H * 0.025 };
                }
                var imgKind = JLayouts.S(p, "img");
                var imgC = imgKind == "accent"
                    ? PlateCol(sc, new[] { sc.Str("accent"), sc.Str("ink") }, C.Fill, 1.8)
                    : imgKind == "dusk" ? J.Mix(Darkest(sc), sc.Str("accent")!, 0.35) : Darkest(sc);
                var tc = OnCol(sc, imgC);
                var tilts = (JArray)p["tilts"]!;
                BBox? bb = null;
                for (var i = 0; i < chunks.Count; i++)
                {
                    var ch = chunks[i];
                    var pos = Pos(i);
                    double x = pos[0], y = pos[1];
                    var t = 0.02 + i * 0.16;
                    var e = J.E.OutCubic(J.Clamp((env.Lt - t) / 0.4));
                    if (e <= 0) continue;
                    var rot = (double)tilts[i % 3]! * (k == 1 ? 0.6 : 1) + (1 - e) * 14;
                    var yy = y - (1 - e) * H * 0.15 + eo * H * 0.1 * (i + 1);
                    var a = J.Clamp(e * 2.5) * (1 - eo);
                    ctx.Save();
                    ctx.Translate((float)x, (float)yy);
                    ctx.RotateDegrees((float)rot);
                    ShadowR(env, -fw / 2, -fh / 2, fw, fh, fw * 0.01, a, u * 0.014);
                    env.Rect(-fw / 2, -fh / 2, fw, fh, C.Fill, a, false);
                    if (C.Edge)
                        env.Line(new[] { new[] { -fw / 2, -fh / 2 }, new[] { fw / 2, -fh / 2 }, new[] { fw / 2, fh / 2 }, new[] { -fw / 2, fh / 2 }, new[] { -fw / 2, -fh / 2 } },
                            C.Line, 1.2, a, false);
                    var ix = -iw / 2;
                    var iy = -fh / 2 + fw * 0.06;
                    var dev = J.E.InOutCubic(J.Clamp((env.Lt - t - 0.1) / 0.9));
                    env.Rect(ix, iy, iw, iw, J.Mix(J.Mix(C.Fill, sc.Str("sub")!, 0.35), imgC, dev), a, false);
                    var light = Lightest(sc);
                    env.Circle(ix + iw * 0.72, iy + iw * 0.26, iw * 0.3, light, null, 0, a * 0.07 * dev, false);
                    env.Circle(ix + iw * 0.72, iy + iw * 0.26, iw * 0.16, light, null, 0, a * 0.08 * dev, false);
                    if (JLayouts.Truthy(p["tape"])) env.Rect(-fw * 0.16, -fh / 2 - fw * 0.05, fw * 0.32, fw * 0.1, light, a * 0.55, false);
                    var fb = FitBlock(ch, JLayouts.S(p, "font")!, iw * 0.84, iw * 0.7, new JObject { ["lead"] = 1.1 }, 3);
                    var r = JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = fb.Text,
                        ["font"] = JLayouts.S(p, "font"),
                        ["size"] = Math.Min(fb.Size, iw * 0.34),
                        ["x"] = 0,
                        ["y"] = iy + iw / 2,
                        ["lead"] = 1.1,
                        ["color"] = tc,
                        ["alpha"] = 0.25 + 0.75 * dev,
                        ["noHold"] = PlateHold(env),
                        ["mi"] = MiAt(env, t + 0.15),
                    });
                    var capA = J.Clamp((env.Lt - t - 0.5) / 0.4) * a;
                    var cap = i == k - 1 ? JLayouts.OrStr(RomajiOf(env), J.FmtTime(env.Cut.Start))! : "No." + LineNo(env) + "-" + (i + 1);
                    if (capA > 0.01)
                        env.DrawItem(new JObject
                        {
                            ["text"] = Sl(cap, 20),
                            ["font"] = JLayouts.S(p, "pen"),
                            ["size"] = Math.Min(fw * 0.07, fw * 0.84 / Math.Max(6, cap.Length * 0.62)),
                            ["x"] = 0,
                            ["y"] = iy + iw + (fh / 2 - iy - iw) * 0.5,
                            ["rot"] = -2,
                            ["color"] = C.Text,
                            ["alpha"] = capA * 0.85,
                            ["ghost"] = false,
                        });
                    ctx.Restore();
                    if (r != null) bb = JLayouts.UnionBb(bb, Box(x - fw / 2, yy - fh / 2, x + fw / 2, yy + fh / 2));
                }
                env.Body = bb;
            },
        });
    }

        private static void PerfRect(Env env, double x, double y, double w, double h, double hole, string col, double a)
    {
        if (env.Pass != "main" || a <= 0.01) return;
        var ctx = env.Ctx;
        ctx.Save();
        using var paint = new SKPaint { IsAntialias = true, Color = Env.SkColor(col, a), BlendMode = env.Blend };
        var nx = Math.Max(2, (int)Math.Round(w / (hole * 3.2)));
        var ny = Math.Max(2, (int)Math.Round(h / (hole * 3.2)));
        for (var i = 0; i <= nx; i++)
        {
            var xx = x + w * i / nx;
            ctx.DrawCircle((float)xx, (float)y, (float)hole, paint);
            ctx.DrawCircle((float)xx, (float)(y + h), (float)hole, paint);
        }
        for (var j = 1; j < ny; j++)
        {
            var yy = y + h * j / ny;
            ctx.DrawCircle((float)x, (float)yy, (float)hole, paint);
            ctx.DrawCircle((float)(x + w), (float)yy, (float)hole, paint);
        }
        ctx.Restore();
    }

    private static void RegisterStampSheet()
    {
        RegC("stampSheet", new LayoutDef
        {
            Name = "切手シート",
            Tags = new[] { "pop", "graphic", "calm" },
            W = 0.7,
            Treat = "safe",
            Fits = n => n >= 1 && n <= 12,
            EnterBias = new JObject { ["pop"] = 1.4, ["cut"] = 1.3, ["blur"] = 1.1 },
            Plan = (rng, cut, st) =>
            {
                var port = PortOf(cut);
                var cols = port ? 3 : 5;
                var rows = port ? 5 : 3;
                return new JObject
                {
                    ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
                    ["cols"] = cols,
                    ["rows"] = rows,
                    ["hc"] = port ? rng.Int(0, 1) : 1,
                    ["hr"] = port ? 1 : rng.Int(0, 1),
                    ["val"] = rng.Pick(new[] { 63, 84, 94, 110, 120, 140 }),
                    ["motif"] = rng.Pick(new[] { "circle", "wave", "char" }),
                    ["tear"] = rng.Chance(0.6),
                };
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var ctx = env.Ctx;
                var p = env.P;
                var u = U(env);
                var outK = Tout(env);
                var ls = SmallSize(env);
                var cols = JLayouts.I(p, "cols");
                var rows = JLayouts.I(p, "rows");
                var C = Card(sc);
                var cell = Math.Min(W * 0.86 / (cols + 0.4), H * 0.84 / (rows + 0.6));
                var sw = cols * cell;
                var sh = rows * cell;
                var sx = W / 2 - sw / 2;
                var sy = H / 2 - sh / 2 + cell * 0.1;
                var m = cell * 0.2;
                var sa = Tin(env, 0, 0.35, J.E.OutCubic) * (1 - J.Clamp((env.POut - 0.5) / 0.5));
                if (sa <= 0.01)
                {
                    env.Body = null;
                    return;
                }
                ShadowR(env, sx - m, sy - m * 1.8, sw + m * 2, sh + m * 2.8, 0, sa, u * 0.012);
                env.Rect(sx - m, sy - m * 1.8, sw + m * 2, sh + m * 2.8, C.Fill, sa, false);
                if (C.Edge)
                    env.Line(new[]
                    {
                        new[] { sx - m, sy - m * 1.8 }, new[] { sx + sw + m, sy - m * 1.8 }, new[] { sx + sw + m, sy + sh + m },
                        new[] { sx - m, sy + sh + m }, new[] { sx - m, sy - m * 1.8 }
                    }, C.Line, 1.2, sa, false);
                env.DrawItem(new JObject
                {
                    ["text"] = "JIZURA POST  ·  " + JLayouts.I(p, "val") + " × " + (cols * rows - 3) + "  ·  No." + LineNo(env),
                    ["font"] = MonoF(env),
                    ["size"] = Math.Min(ls * 0.8, m * 0.7),
                    ["track"] = 0.2,
                    ["align"] = "left",
                    ["x"] = sx,
                    ["y"] = sy - m * 0.9,
                    ["color"] = C.Text,
                    ["alpha"] = sa * 0.7,
                    ["ghost"] = false,
                });
                var port2 = rows > cols;
                var spc = port2 ? 2 : 3;
                var spr = port2 ? 3 : 2;
                var hc = Math.Min(JLayouts.I(p, "hc"), cols - spc);
                var hr = Math.Min(JLayouts.I(p, "hr"), rows - spr);
                var tones = new[]
                {
                    PlateCol(sc, new[] { sc.Str("accent") }, C.Fill, 1.3), J.Mix(C.Fill, C.Text, 0.18),
                    PlateCol(sc, new[] { sc.Str("accent2"), sc.Str("sub") }, C.Fill, 1.3), J.Mix(C.Fill, sc.Str("accent")!, 0.45)
                };
                var t0 = env.Cut.Text.Trim();
                var st0 = Strip(t0);
                var first = st0.Length > 0 ? st0[0].ToString() : "";
                var hole = cell * 0.035;
                for (var r = 0; r < rows; r++)
                    for (var c = 0; c < cols; c++)
                    {
                        var hero = c >= hc && c < hc + spc && r >= hr && r < hr + spr;
                        if (hero) continue;
                        var e = J.E.OutBack(J.Clamp((env.Lt - 0.05 - (c + r) * 0.03) / 0.3), 1.5) * outK;
                        if (e <= 0.01) continue;
                        var x = sx + c * cell;
                        var y = sy + r * cell;
                        var pad = cell * 0.1;
                        var tone = tones[(c + r * 2) % tones.Length];
                        var tt = OnCol(sc, tone);
                        var q = (cell - pad * 2) * e;
                        var ox = x + cell / 2 - q / 2;
                        var oy = y + cell / 2 - q / 2;
                        env.Rect(ox, oy, q, q, tone, 1, false);
                        if (e > 0.6)
                        {
                            var cxm = x + cell / 2;
                            var cym = y + cell / 2;
                            var motif = JLayouts.S(p, "motif");
                            if (motif == "circle") env.Circle(cxm, cym + q * 0.08, q * 0.24, null, tt, Math.Max(1.5, q * 0.03), 0.6, false);
                            else if (motif == "wave")
                            {
                                var pts = new List<double[]>();
                                for (var i = 0; i <= 16; i++)
                                    pts.Add(new[] { ox + q * 0.15 + q * 0.7 * i / 16, cym + q * 0.1 + Math.Sin(i / 16.0 * J.TAU * 1.5) * q * 0.08 });
                                env.Line(pts.ToArray(), tt, Math.Max(1.5, q * 0.03), 0.6, false);
                            }
                            else
                                env.DrawItem(new JObject
                                {
                                    ["text"] = first,
                                    ["font"] = JLayouts.S(p, "font"),
                                    ["size"] = q * 0.45,
                                    ["x"] = cxm,
                                    ["y"] = cym + q * 0.08,
                                    ["color"] = tt,
                                    ["alpha"] = 0.5,
                                    ["ghost"] = false,
                                });
                            env.DrawItem(new JObject
                            {
                                ["text"] = JLayouts.I(p, "val").ToString(CultureInfo.InvariantCulture),
                                ["font"] = MonoF(env),
                                ["size"] = q * 0.16,
                                ["align"] = "left",
                                ["x"] = ox + q * 0.08,
                                ["y"] = oy + q * 0.13,
                                ["color"] = tt,
                                ["alpha"] = 0.9,
                                ["ghost"] = false,
                            });
                        }
                    }
                var holeCol = sc.Str("bg") == C.Fill ? J.Mix(C.Fill, C.Text, 0.2) : sc.Str("bg")!;
                for (var r = 0; r < rows; r++)
                    for (var c = 0; c < cols; c++)
                        PerfRect(env, sx + c * cell, sy + r * cell, cell, cell, hole, holeCol, sa * 0.9);
                var hx = sx + hc * cell;
                var hy = sy + hr * cell;
                var hw = cell * spc;
                var hh = cell * spr;
                var hs = Math.Min(hw, hh);
                var he = J.E.OutBack(J.Clamp((env.Lt - 0.12) / 0.35), 1.3);
                var tear = JLayouts.Truthy(p["tear"]) ? J.E.InCubic(env.POut) : 0;
                var ha = J.Clamp(he * 2) * (JLayouts.Truthy(p["tear"]) ? 1 - J.Clamp((env.POut - 0.6) / 0.4) : outK);
                ctx.Save();
                ctx.Translate((float)(hx + hw / 2 + tear * cell * 0.4), (float)(hy + hh / 2 - tear * cell * 0.8));
                ctx.RotateDegrees((float)(tear * 14 + (1 - he) * -6));
                ctx.Scale((float)(0.9 + 0.1 * he + tear * 0.06), (float)(0.9 + 0.1 * he + tear * 0.06));
                if (tear > 0) ShadowR(env, -hw / 2, -hh / 2, hw, hh, 0, ha, u * 0.02 * (1 + tear * 2));
                env.Rect(-hw / 2, -hh / 2, hw, hh, C.Fill, ha, false);
                PerfRect(env, -hw / 2, -hh / 2, hw, hh, hole * 1.2, holeCol, ha);
                var ip = hs * 0.07;
                var heroC = PlateCol(sc, new[] { sc.Str("accent"), sc.Str("ink") }, C.Fill, 1.8);
                env.Rect(-hw / 2 + ip, -hh / 2 + ip, hw - ip * 2, hh - ip * 2, heroC, ha, false);
                var htc = OnCol(sc, heroC);
                env.DrawItem(new JObject
                {
                    ["text"] = JLayouts.I(p, "val").ToString(CultureInfo.InvariantCulture),
                    ["font"] = MonoF(env),
                    ["size"] = hs * 0.1,
                    ["align"] = "left",
                    ["x"] = -hw / 2 + ip * 1.8,
                    ["y"] = -hh / 2 + ip * 2.4,
                    ["color"] = htc,
                    ["alpha"] = ha,
                    ["ghost"] = false,
                });
                env.DrawItem(new JObject
                {
                    ["text"] = "LYRIC  " + LineNo(env),
                    ["font"] = MonoF(env),
                    ["size"] = hs * 0.045,
                    ["track"] = 0.3,
                    ["align"] = "right",
                    ["x"] = hw / 2 - ip * 1.8,
                    ["y"] = hh / 2 - ip * 1.9,
                    ["color"] = htc,
                    ["alpha"] = ha * 0.8,
                    ["ghost"] = false,
                });
                var fb = FitBlock(t0, JLayouts.S(p, "font")!, hw * 0.76, hh * 0.56, new JObject { ["lead"] = 1.08 }, 3);
                var bb = JLayouts.MainDraw(env, new JObject
                {
                    ["text"] = fb.Text,
                    ["font"] = JLayouts.S(p, "font"),
                    ["size"] = Math.Min(fb.Size, hs * 0.34),
                    ["x"] = 0,
                    ["y"] = hs * 0.03,
                    ["lead"] = 1.08,
                    ["color"] = htc,
                    ["noHold"] = PlateHold(env),
                    ["mi"] = MiAt(env, 0.15),
                });
                ctx.Restore();
                env.Body = bb != null ? Box(hx, hy, hx + hw, hy + hh) : null;
            },
        });
    }

        private static string PostcardZip(Rng rng)
    {
        var zip = "";
        for (var i = 0; i < 7; i++) zip += rng.Int(0, 9).ToString(CultureInfo.InvariantCulture);
        return zip;
    }

    private static void RegisterPostcard()
    {
        RegC("postcard", new LayoutDef
        {
            Name = "はがき",
            Tags = new[] { "emotional", "calm", "editorial" },
            W = 0.8,
            Treat = "safe",
            Fits = n => n >= 1 && n <= 18,
            EnterBias = new JObject { ["blur"] = 1.3, ["type"] = 1.3, ["wipe"] = 1.2 },
            Plan = (rng, cut, st) =>
            {
                return new JObject
                {
                    ["font"] = rng.Chance(0.4) ? "klee" : rng.Pick(FontsOf(st, "serif", "display")),
                    ["tilt"] = rng.Range(-5, 5),
                    ["val"] = rng.Pick(new[] { 63, 85, 110 }),
                    ["zip"] = PostcardZip(rng),
                    ["mark"] = rng.Range(-18, 18),
                    ["stamp"] = rng.Pick(new[] { "circle", "mount", "wave" }),
                };
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var ctx = env.Ctx;
                var p = env.P;
                var u = U(env);
                var port = IsPort(env);
                var outK = Tout(env);
                var ls = SmallSize(env);
                var C = Card(sc);
                var vertCard = port;
                var cw = vertCard ? Math.Min(W * 0.8, H * 0.8 / 1.48) : Math.Min(W * 0.66, H * 0.84 * 1.48);
                var ch = vertCard ? cw * 1.48 : cw / 1.48;
                var ein = J.E.OutCubic(J.Clamp(env.Lt / 0.55));
                var eo = J.E.InCubic(env.POut);
                var a = J.Clamp(ein * 2) * (1 - eo);
                if (a <= 0.01)
                {
                    env.Body = null;
                    return;
                }
                var cx = W / 2 - (1 - ein) * W * 0.4 + eo * W * 0.3;
                var cy = H / 2 + (1 - ein) * H * 0.05;
                var rot = JLayouts.D(p, "tilt") + (1 - ein) * -18 + eo * 10;
                ctx.Save();
                ctx.Translate((float)cx, (float)cy);
                ctx.RotateDegrees((float)rot);
                var x0 = -cw / 2;
                var y0 = -ch / 2;
                ShadowR(env, x0, y0, cw, ch, cw * 0.012, a, u * 0.016);
                env.RRect(x0, y0, cw, ch, cw * 0.012, C.Fill, a, false, C.Edge ? C.Line : null, 1.2);
                var red = PlateCol(sc, new[] { sc.Str("accent"), sc.Str("accent2"), C.Text }, C.Fill, 1.8);
                var m = Math.Min(cw, ch) * 0.07;
                var stw = Math.Min(cw, ch) * 0.2;
                var sth = stw * 1.2;
                var stx = x0 + m;
                var sty = y0 + m;
                var stC = PlateCol(sc, new[] { sc.Str("accent2"), sc.Str("accent"), sc.Str("ink") }, C.Fill, 1.5);
                env.Rect(stx, sty, stw, sth, stC, a, false);
                PerfRect(env, stx, sty, stw, sth, stw * 0.035, C.Fill, a);
                var stt = OnCol(sc, stC);
                var stampKind = JLayouts.S(p, "stamp");
                if (stampKind == "circle") env.Circle(stx + stw / 2, sty + sth * 0.56, stw * 0.26, stt, null, 0, a * 0.8, false);
                else if (stampKind == "mount")
                    env.Poly(new[]
                    {
                        new[] { stx + stw * 0.12, sty + sth * 0.78 }, new[] { stx + stw * 0.5, sty + sth * 0.3 },
                        new[] { stx + stw * 0.88, sty + sth * 0.78 }
                    }, stt, a * 0.8, false);
                else
                {
                    var pts = new List<double[]>();
                    for (var i = 0; i <= 12; i++)
                        pts.Add(new[] { stx + stw * (0.12 + 0.76 * i / 12), sty + sth * 0.6 + Math.Sin(i / 12.0 * J.TAU) * sth * 0.08 });
                    env.Line(pts.ToArray(), stt, Math.Max(2, stw * 0.05), a, false);
                }
                env.DrawItem(new JObject
                {
                    ["text"] = JLayouts.I(p, "val").ToString(CultureInfo.InvariantCulture),
                    ["font"] = MonoF(env),
                    ["size"] = stw * 0.2,
                    ["align"] = "left",
                    ["x"] = stx + stw * 0.1,
                    ["y"] = sty + sth * 0.14,
                    ["color"] = stt,
                    ["alpha"] = a,
                    ["ghost"] = false,
                });
                var bs = Math.Min(cw, ch) * 0.062;
                var gap = bs * 0.22;
                var zx1 = x0 + cw - m;
                var zy = y0 + m * 0.9;
                var za = Tin(env, 0.25, 0.4, J.E.OutCubic) * outK;
                var zip = JLayouts.S(p, "zip")!;
                for (var i = 0; i < 7; i++)
                {
                    var bx = zx1 - (7 - i) * (bs + gap) - (i < 3 ? gap * 1.5 : 0);
                    env.RRect(bx, zy, bs, bs * 1.25, bs * 0.08, null, za, false, red, Math.Max(1.2, bs * 0.06));
                    if (i == 3)
                        env.Line(new[] { new[] { bx - gap * 2.2, zy + bs * 0.62 }, new[] { bx - gap * 0.6, zy + bs * 0.62 } }, red, Math.Max(1.2, bs * 0.06), za, false);
                    env.DrawItem(new JObject
                    {
                        ["text"] = zip[i].ToString(),
                        ["font"] = JLayouts.S(p, "font"),
                        ["size"] = bs * 0.8,
                        ["x"] = bx + bs / 2,
                        ["y"] = zy + bs * 0.66,
                        ["color"] = C.Text,
                        ["alpha"] = za * 0.9,
                        ["ghost"] = false,
                    });
                }
                env.DrawItem(new JObject
                {
                    ["text"] = vertCard ? "郵便はがき" : "POST CARD",
                    ["font"] = vertCard ? SerifF(env) : MonoF(env),
                    ["size"] = ls * (vertCard ? 1.2 : 0.9),
                    ["track"] = 0.5,
                    ["x"] = vertCard ? 0 : x0 + cw * 0.46,
                    ["y"] = vertCard ? y0 + m + sth + ls * 0.9 : y0 + m * 0.75,
                    ["color"] = C.Text,
                    ["alpha"] = a * 0.75,
                    ["ghost"] = false,
                });
                var t0 = env.Cut.Text.Trim();
                BBox? bb;
                var la = Tin(env, 0.2, 0.5, J.E.OutCubic) * outK;
                if (vertCard && !HasLatin(t0))
                {
                    var top = y0 + m + sth + ls * 2.4;
                    var bot = y0 + ch - m * 1.4;
                    var fb = FitBlock(Strip(t0), JLayouts.S(p, "font")!, cw * 0.5, (bot - top) * 0.84,
                        new JObject { ["vertical"] = true, ["lead"] = 1.3, ["track"] = 0.06 }, 2);
                    var size = Math.Min(fb.Size, cw * 0.2);
                    var mm = Meas(fb.Text, JLayouts.S(p, "font")!, size, new JObject { ["vertical"] = true, ["lead"] = 1.3, ["track"] = 0.06 });
                    var lx = x0 + cw * 0.62;
                    for (var i = 0; i < 4; i++)
                    {
                        var xx = x0 + cw * (0.84 - i * 0.2);
                        LeaderV(env, xx, top, bot, C.Text, la * 0.3, ls * 0.45, Math.Max(1, ls * 0.05));
                    }
                    var ty = top + Math.Max(0, bot - top - mm.H - size * 1.3) * 0.3;
                    bb = JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = fb.Text,
                        ["font"] = JLayouts.S(p, "font"),
                        ["size"] = size,
                        ["x"] = lx,
                        ["y"] = ty,
                        ["vertical"] = true,
                        ["align"] = "left",
                        ["lead"] = 1.3,
                        ["track"] = 0.06,
                        ["color"] = C.Text,
                        ["noHold"] = PlateHold(env),
                        ["mi"] = MiAt(env, 0.3),
                    });
                    var lastLen = JLayouts.GlyphCount(fb.Text.Split('\n')[^1]);
                    env.DrawItem(new JObject
                    {
                        ["text"] = "様",
                        ["font"] = JLayouts.S(p, "font"),
                        ["size"] = size * 0.7,
                        ["x"] = lx - (fb.Lines - 1) * size * 0.65,
                        ["y"] = ty + lastLen * size * 1.06 + size * 0.75,
                        ["color"] = C.Text,
                        ["alpha"] = la,
                        ["ghost"] = false,
                    });
                }
                else
                {
                    var top = y0 + m + sth + ls * 1.8;
                    var bot = y0 + ch - m;
                    const int lines = 4;
                    var lh = (bot - top) / lines;
                    for (var i = 1; i <= lines; i++)
                        Leader(env, x0 + m, x0 + cw - m, top + lh * i, C.Text, la * 0.3, ls * 0.45, Math.Max(1, ls * 0.05));
                    var fb = FitBlock(t0, JLayouts.S(p, "font")!, cw - m * 2.4, lh * 2.4, new JObject { ["lead"] = 1.2, ["track"] = 0.02 }, 2);
                    var size = Math.Min(fb.Size, lh * 1.05);
                    var nL = fb.Text.Split('\n').Length;
                    bb = JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = fb.Text,
                        ["font"] = JLayouts.S(p, "font"),
                        ["size"] = size,
                        ["x"] = x0 + m * 1.2,
                        ["y"] = top + lh * (nL > 1 ? 2.5 : 2.0) - size * 0.3,
                        ["align"] = "left",
                        ["lead"] = lh / size,
                        ["track"] = 0.02,
                        ["color"] = C.Text,
                        ["noHold"] = PlateHold(env),
                        ["mi"] = MiAt(env, 0.3),
                    });
                }
                var pt = (env.Lt - env.Cut.InDur - 0.2) / 0.18;
                if (pt > 0)
                {
                    var q = J.Lerp(1.4, 1, J.E.OutBack(J.Clamp(pt), 1.3));
                    var pa = J.Clamp(pt * 3) * outK * 0.85;
                    var R = stw * 0.62;
                    ctx.Save();
                    ctx.Translate((float)(stx + stw * 0.95), (float)(sty + sth * 0.62));
                    ctx.RotateDegrees((float)JLayouts.D(p, "mark"));
                    ctx.Scale((float)q, (float)q);
                    env.Circle(0, 0, R, null, C.Text, Math.Max(1.5, R * 0.05), pa, false);
                    env.Circle(0, 0, R * 0.8, null, C.Text, Math.Max(1, R * 0.025), pa, false);
                    env.DrawItem(new JObject
                    {
                        ["text"] = J.FmtTime(env.Cut.Start),
                        ["font"] = MonoF(env),
                        ["size"] = R * 0.26,
                        ["x"] = 0,
                        ["y"] = 0,
                        ["color"] = C.Text,
                        ["alpha"] = pa,
                        ["ghost"] = false,
                    });
                    env.Line(new[] { new[] { -R * 0.8, -R * 0.36 }, new[] { R * 0.8, -R * 0.36 } }, C.Text, Math.Max(1, R * 0.025), pa, false);
                    env.Line(new[] { new[] { -R * 0.8, R * 0.36 }, new[] { R * 0.8, R * 0.36 } }, C.Text, Math.Max(1, R * 0.025), pa, false);
                    for (var k2 = 0; k2 < 3; k2++)
                    {
                        var pts = new List<double[]>();
                        for (var i = 0; i <= 20; i++)
                            pts.Add(new[] { R * 1.15 + i * R * 0.1, (k2 - 1) * R * 0.32 + Math.Sin(i * 0.9) * R * 0.08 });
                        env.Line(pts.ToArray(), C.Text, Math.Max(1.5, R * 0.04), pa, false);
                    }
                    ctx.Restore();
                }
                ctx.Restore();
                env.Body = bb != null ? Box(cx - cw / 2, cy - ch / 2, cx + cw / 2, cy + ch / 2) : null;
            },
        });
    }

    private static void RegisterLetterPaper()
    {
        RegC("letterPaper", new LayoutDef
        {
            Name = "便箋",
            Tags = new[] { "emotional", "calm" },
            W = 0.9,
            Treat = "safe",
            Portrait = 1.1,
            Fits = n => n >= 1 && n <= 22,
            EnterBias = new JObject { ["type"] = 1.8, ["wipe"] = 1.5, ["blur"] = 1.3, ["slice"] = 0.4, ["stretch"] = 0.4 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Chance(0.5) ? "klee" : rng.Pick(FontsOf(st, "serif")),
                ["variant"] = HasLatin(cut.Text) ? "yoko" : rng.Pick(new[] { "tate", "tate", "yoko" }),
                ["rule"] = rng.Pick(new[] { "accent", "accent", "accent", "sub" }),
                ["sign"] = rng.Chance(0.7),
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var ctx = env.Ctx;
                var p = env.P;
                var u = U(env);
                var port = IsPort(env);
                var C = Card(sc);
                var outK = Tout(env);
                var ls = SmallSize(env);
                var tate = JLayouts.S(p, "variant") == "tate";
                double pw, ph;
                if (tate)
                {
                    pw = port ? W * 0.88 : Math.Min(W * 0.8, H * 0.86 * 1.45);
                    ph = port ? Math.Min(H * 0.74, pw * 1.5) : pw / 1.45;
                }
                else
                {
                    ph = port ? Math.Min(H * 0.8, W * 0.9 * 1.35) : H * 0.86;
                    pw = port ? ph / 1.35 : Math.Min(W * 0.8, ph * 1.1);
                }
                var x0 = W / 2 - pw / 2;
                var y0 = H / 2 - ph / 2 + (port ? (1 - J.E.OutCubic(J.Clamp(env.Lt / 0.45))) * H * 0.25 + J.E.InCubic(env.POut) * H * 0.05 : 0);
                var ruleC = JLayouts.S(p, "rule") == "accent" ? PlateCol(sc, new[] { sc.Str("accent"), sc.Str("accent2") }, C.Fill, 1.6) : J.Mix(C.Fill, C.Text, 0.4);
                var lw = Math.Max(1.5, u * 0.002);
                var fadeOut = 1 - J.Clamp((env.POut - 0.55) / 0.45);
                var uf = port ? 1 : J.E.InOutCubic(J.Clamp((LtU(env) - 0.05) / 0.45));
                var fold = port ? 0 : J.E.InOutCubic(J.Clamp(POutU(env) / 0.7));
                var k = uf * (1 - fold);
                var a0 = Tin(env, 0, 0.2, J.E.OutCubic) * fadeOut;
                if (a0 <= 0.01)
                {
                    env.Body = null;
                    return;
                }
                var third = port ? ph / 3 : tate ? pw / 3 : ph / 3;
                var vis = new[]
                {
                    new[] { x0 + third * (tate ? 1 - k : 0), y0 + (tate ? 0 : third * (1 - k)) },
                    new[] { tate ? third * (1 + 2 * k) : pw, tate ? ph : third * (1 + 2 * k) }
                };
                ShadowR(env, vis[0][0], vis[0][1], vis[1][0], vis[1][1], 0, a0, u * 0.014);
                env.Rect(vis[0][0], vis[0][1], vis[1][0], vis[1][1], C.Fill, a0, false);
                if (C.Edge)
                    env.Line(new[]
                    {
                        new[] { vis[0][0], vis[0][1] }, new[] { vis[0][0] + vis[1][0], vis[0][1] },
                        new[] { vis[0][0] + vis[1][0], vis[0][1] + vis[1][1] }, new[] { vis[0][0], vis[0][1] + vis[1][1] },
                        new[] { vis[0][0], vis[0][1] }
                    }, C.Line, 1.2, a0, false);
                ctx.Save();
                ctx.ClipRect(new SKRect((float)vis[0][0], (float)vis[0][1], (float)(vis[0][0] + vis[1][0]), (float)(vis[0][1] + vis[1][1])));
                var t0 = env.Cut.Text.Trim();
                BBox? bb = null;
                var mT = ph * 0.1;
                var mS = pw * 0.06;
                if (tate)
                {
                    var top = y0 + mT;
                    var bot = y0 + ph - mT;
                    env.Rect(x0 + mS, top - lw * 4, pw - mS * 2, lw * 2.5, ruleC, a0, false);
                    env.Rect(x0 + mS, bot + lw * 1.5, pw - mS * 2, lw * 2.5, ruleC, a0, false);
                    var fb = FitBlock(Strip(t0), JLayouts.S(p, "font")!, port ? pw * 0.6 : third * 0.9, (bot - top) * 0.92,
                        new JObject { ["vertical"] = true, ["lead"] = 1.55, ["track"] = 0.08 }, 3);
                    var size = Math.Min(fb.Size, u * (port ? 0.17 : 0.13));
                    var pitch = size * 1.55;
                    var nl = fb.Text.Split('\n').Length;
                    var @base = W / 2 - nl / 2.0 * pitch;                     var iMin = (int)Math.Ceiling((x0 + mS - @base) / pitch);
                    var iMax = (int)Math.Floor((x0 + pw - mS - @base) / pitch);
                    for (var i = iMin; i <= iMax; i++)
                        env.Line(new[] { new[] { @base + i * pitch, top }, new[] { @base + i * pitch, bot } }, ruleC, lw, a0 * 0.75, false);
                    bb = JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = fb.Text,
                        ["font"] = JLayouts.S(p, "font"),
                        ["size"] = size,
                        ["x"] = W / 2,
                        ["y"] = top + size * 0.35,
                        ["vertical"] = true,
                        ["align"] = "left",
                        ["lead"] = 1.55,
                        ["track"] = 0.08,
                        ["color"] = C.Text,
                        ["noHold"] = PlateHold(env),
                    });
                    var a2 = J.Clamp((k - 0.6) / 0.4) * outK;
                    var ctxT = DeckCopy(env);
                    var cs = size * 0.62;
                    if (ctxT != null && a2 > 0.01 && @base + (nl + 1.5) * pitch < x0 + pw - mS)
                        env.DrawItem(new JObject
                        {
                            ["text"] = string.Concat(JLayouts.CodePoints(Strip(ctxT)).Take((int)Math.Floor((bot - top) / (cs * 1.1)))),
                            ["font"] = JLayouts.S(p, "font"),
                            ["size"] = cs,
                            ["vertical"] = true,
                            ["align"] = "left",
                            ["track"] = 0.08,
                            ["x"] = @base + (nl + 1.5) * pitch,
                            ["y"] = top + cs * 0.4,
                            ["color"] = C.Text,
                            ["alpha"] = a2 * 0.5,
                            ["ghost"] = false,
                        });
                    if (JLayouts.Truthy(p["sign"]) && a2 > 0.01 && @base - 1.5 * pitch > x0 + mS)
                        env.DrawItem(new JObject
                        {
                            ["text"] = "No." + LineNo(env),
                            ["font"] = JLayouts.S(p, "font"),
                            ["size"] = ls,
                            ["vertical"] = true,
                            ["align"] = "left",
                            ["x"] = @base - 1.5 * pitch,
                            ["y"] = bot - ls * 5,
                            ["color"] = C.Text,
                            ["alpha"] = a2 * 0.7,
                            ["ghost"] = false,
                        });
                }
                else
                {
                    var left = x0 + pw * 0.1;
                    var right = x0 + pw * 0.9;
                    var top = y0 + ph * 0.1;
                    var bot = y0 + ph * 0.92;
                    var fb = FitBlock(t0, JLayouts.S(p, "font")!, right - left, (bot - top) * 0.46,
                        new JObject { ["lead"] = 1.7, ["track"] = 0.04 }, port ? 3 : 2);
                    var size = Math.Min(fb.Size, u * 0.15);
                    var pitch = size * 1.7;
                    var nRow = Math.Max(3, (int)Math.Floor((bot - top) / pitch));
                    for (var i = 0; i <= nRow; i++)
                        env.Line(new[] { new[] { left - pw * 0.03, top + i * pitch }, new[] { right + pw * 0.03, top + i * pitch } }, ruleC, lw, a0 * 0.75, false);
                    env.Line(new[] { new[] { left - pw * 0.03, top - lw * 5 }, new[] { right + pw * 0.03, top - lw * 5 } }, ruleC, lw * 2.5, a0, false);
                    var nl = fb.Text.Split('\n').Length;
                    var r0 = Math.Max(0, (int)Math.Floor(nRow / 2.0 - nl / 2.0));
                    bb = JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = fb.Text,
                        ["font"] = JLayouts.S(p, "font"),
                        ["size"] = size,
                        ["x"] = left,
                        ["y"] = top + (r0 + nl / 2.0) * pitch - size * 0.12,
                        ["align"] = "left",
                        ["lead"] = 1.7,
                        ["track"] = 0.04,
                        ["color"] = C.Text,
                        ["noHold"] = PlateHold(env),
                    });
                    var a2 = J.Clamp((k - 0.6) / 0.4) * outK;
                    var ctxT = DeckCopy(env);
                    if (ctxT != null && a2 > 0.01 && r0 > 0)
                        env.DrawItem(new JObject
                        {
                            ["text"] = ctxT,
                            ["font"] = JLayouts.S(p, "font"),
                            ["size"] = size * 0.6,
                            ["align"] = "left",
                            ["x"] = left,
                            ["y"] = top + (r0 - 0.5) * pitch - size * 0.1,
                            ["color"] = C.Text,
                            ["alpha"] = a2 * 0.45,
                            ["ghost"] = false,
                        });
                    if (JLayouts.Truthy(p["sign"]) && a2 > 0.01)
                        env.DrawItem(new JObject
                        {
                            ["text"] = "— No." + LineNo(env),
                            ["font"] = JLayouts.S(p, "font"),
                            ["size"] = ls,
                            ["align"] = "right",
                            ["x"] = right,
                            ["y"] = top + (nRow - 0.5) * pitch - ls * 0.2,
                            ["color"] = C.Text,
                            ["alpha"] = a2 * 0.7,
                            ["ghost"] = false,
                        });
                }
                ctx.Restore();
                var shade = (1 - k) * 0.35;
                foreach (var i in new[] { 0, 2 })
                {
                    var cx2 = tate ? (i == 0 ? x0 + third * (1 - k) : x0 + third * 2) : x0;
                    var cy2 = tate ? y0 : (i == 0 ? y0 + third * (1 - k) : y0 + third * 2);
                    if (shade > 0.01) env.Rect(cx2, cy2, tate ? third * k : pw, tate ? ph : third * k, Darkest(sc), a0 * shade, false);
                }
                var cr = J.Mix(C.Fill, C.Text, 0.12);
                if (tate)
                {
                    env.Line(new[] { new[] { x0 + third, y0 }, new[] { x0 + third, y0 + ph } }, cr, lw, a0 * 0.8, false);
                    env.Line(new[] { new[] { x0 + third * 2, y0 }, new[] { x0 + third * 2, y0 + ph } }, cr, lw, a0 * 0.8 * k, false);
                }
                else
                {
                    env.Line(new[] { new[] { x0, y0 + third }, new[] { x0 + pw, y0 + third } }, cr, lw, a0 * 0.8 * k, false);
                    env.Line(new[] { new[] { x0, y0 + third * 2 }, new[] { x0 + pw, y0 + third * 2 } }, cr, lw, a0 * 0.8 * k, false);
                }
                env.Body = bb;
            },
        });
    }

    private const string WD_J = "日月火水木金土";

    private static readonly string[] WD_E = { "SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT" };

    private static readonly string[] MON_E =
    {
        "JANUARY", "FEBRUARY", "MARCH", "APRIL", "MAY", "JUNE", "JULY", "AUGUST", "SEPTEMBER", "OCTOBER", "NOVEMBER", "DECEMBER"
    };

    private static readonly string[] ROKUYO = { "大安", "赤口", "先勝", "友引", "先負", "仏滅" };

    private static void RegisterCalendar()
    {
        RegC("calendar", new LayoutDef
        {
            Name = "カレンダー",
            Tags = new[] { "pop", "editorial", "calm" },
            W = 0.7,
            Treat = "safe",
            Fits = n => n >= 1 && n <= 16,
            EnterBias = new JObject { ["pop"] = 1.3, ["cut"] = 1.3, ["zoom"] = 1.2 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
                ["variant"] = rng.Pick(new[] { "month", "himekuri" }),
                ["month"] = rng.Int(0, 11),
                ["off"] = rng.Int(0, 6),
                ["day"] = rng.Int(3, 27),
                ["days"] = rng.Pick(new[] { 30, 31 }),
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var ctx = env.Ctx;
                var p = env.P;
                var u = U(env);
                var port = IsPort(env);
                var outK = Tout(env);
                var ls = SmallSize(env);
                var C = Card(sc);
                var t0 = env.Cut.Text.Trim();
                var days = JLayouts.I(p, "days");
                var month = JLayouts.I(p, "month");
                var day = (JLayouts.I(p, "day") + Math.Max(0, env.Cut.Line)) % days + 1;
                var wd = (JLayouts.I(p, "off") + day - 1) % 7;
                var red = PlateCol(sc, new[] { sc.Str("accent"), sc.Str("accent2") }, C.Fill, 2.2);
                var blue = PlateCol(sc, new[] { sc.Str("accent2"), sc.Str("accent") }, C.Fill, 2.2);
                string DayCol(int w) => w == 0 ? red : w == 6 ? blue : C.Text;
                if (JLayouts.S(p, "variant") == "himekuri")
                {
                    var pw = port ? W * 0.84 : Math.Min(W * 0.6, H * 0.86 * 0.95);
                    var ph = port ? Math.Min(H * 0.7, pw * 1.3) : H * 0.84;
                    var x0 = W / 2 - pw / 2;
                    var y0 = H / 2 - ph / 2 + ph * 0.03;
                    var a = Tin(env, 0, 0.3, J.E.OutCubic);
                    var bindH = ph * 0.08;
                    var bindC = new[] { Darkest(sc), sc.Str("ink")!, sc.Str("accent")!, sc.Str("sub")! }
                        .FirstOrDefault(c => J.Contrast(c, sc.Str("bg")!) >= 1.5 && J.Contrast(c, C.Fill) >= 1.8) ?? sc.Str("sub")!;
                    for (var i = 3; i >= 1; i--)
                        env.Rect(x0 + i * 2, y0 + bindH, pw, ph - bindH + i * u * 0.006, J.Mix(C.Fill, C.Text, 0.08 * i), a, false);
                    ShadowR(env, x0, y0, pw, ph, 0, a, u * 0.012);
                    env.Rect(x0, y0 + bindH, pw, ph - bindH, C.Fill, a, false);
                    var tear = J.E.InCubic(env.POut);
                    env.DrawItem(new JObject
                    {
                        ["text"] = (day % days + 1).ToString(CultureInfo.InvariantCulture),
                        ["font"] = JLayouts.S(p, "font"),
                        ["size"] = ph * 0.3,
                        ["x"] = W / 2,
                        ["y"] = y0 + ph * 0.55,
                        ["color"] = DayCol((wd + 1) % 7),
                        ["alpha"] = a * 0.18,
                        ["ghost"] = false,
                    });
                    ctx.Save();
                    var hx = x0 + pw * (JLayouts.I(p, "day") % 2 != 0 ? 0.1 : 0.9);
                    var hy = y0 + bindH;
                    ctx.Translate((float)hx, (float)hy);
                    ctx.RotateDegrees((float)((JLayouts.I(p, "day") % 2 != 0 ? 1 : -1) * tear * 28));
                    ctx.Translate((float)-hx, (float)(-hy + tear * tear * ph * 0.6));
                    var pa = a * (1 - J.Clamp((env.POut - 0.7) / 0.3));
                    env.Rect(x0, y0 + bindH, pw, ph - bindH, C.Fill, pa, false);
                    if (C.Edge)
                        env.Line(new[]
                        {
                            new[] { x0, y0 + bindH }, new[] { x0 + pw, y0 + bindH }, new[] { x0 + pw, y0 + ph },
                            new[] { x0, y0 + ph }, new[] { x0, y0 + bindH }
                        }, C.Line, 1.2, pa, false);
                    var fa = Tin(env, 0.1, 0.4, J.E.OutCubic) * pa;
                    var m = pw * 0.07;
                    env.DrawItem(new JObject
                    {
                        ["text"] = (month + 1).ToString(CultureInfo.InvariantCulture),
                        ["font"] = JLayouts.S(p, "font"),
                        ["size"] = ph * 0.1,
                        ["align"] = "left",
                        ["x"] = x0 + m,
                        ["y"] = y0 + bindH + ph * 0.09,
                        ["color"] = C.Text,
                        ["alpha"] = fa,
                        ["ghost"] = false,
                    });
                    env.DrawItem(new JObject
                    {
                        ["text"] = MON_E[month],
                        ["font"] = MonoF(env),
                        ["size"] = ls * 0.9,
                        ["track"] = 0.3,
                        ["align"] = "left",
                        ["x"] = x0 + m + ph * 0.1 * 0.9,
                        ["y"] = y0 + bindH + ph * 0.1,
                        ["color"] = C.Text,
                        ["alpha"] = fa * 0.8,
                        ["ghost"] = false,
                    });
                    env.RRect(x0 + pw - m - ls * 3.2, y0 + bindH + ph * 0.06, ls * 3.2, ls * 1.6, ls * 0.2, null, fa, false, red, Math.Max(1.5, ls * 0.08));
                    env.DrawItem(new JObject
                    {
                        ["text"] = ROKUYO[(day + month) % 6],
                        ["font"] = SerifF(env),
                        ["size"] = ls * 1.05,
                        ["x"] = x0 + pw - m - ls * 1.6,
                        ["y"] = y0 + bindH + ph * 0.06 + ls * 0.8,
                        ["color"] = red,
                        ["alpha"] = fa,
                        ["ghost"] = false,
                    });
                    var dcol0 = DayCol(wd);
                    var dcol = J.Contrast(dcol0, C.Fill) >= 3 ? dcol0 : C.Text;
                    var fb = FitBlock(t0, JLayouts.S(p, "font")!, pw - m * 2, ph * 0.46, new JObject { ["lead"] = 1.08, ["track"] = 0.02 }, 3);
                    var size = Math.Min(fb.Size, u * 0.24);
                    var bb = JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = fb.Text,
                        ["font"] = JLayouts.S(p, "font"),
                        ["size"] = size,
                        ["x"] = W / 2,
                        ["y"] = y0 + bindH + ph * 0.47,
                        ["lead"] = 1.08,
                        ["track"] = 0.02,
                        ["color"] = dcol == C.Text ? C.Text : dcol,
                        ["noHold"] = PlateHold(env),
                    });
                    env.Line(new[] { new[] { x0 + m, y0 + ph * 0.8 }, new[] { x0 + pw - m, y0 + ph * 0.8 } }, C.Text, Math.Max(1, u * 0.0015), fa * 0.5, false);
                    env.DrawItem(new JObject
                    {
                        ["text"] = WD_J[wd] + "曜日",
                        ["font"] = SerifF(env),
                        ["size"] = ls * 1.5,
                        ["align"] = "left",
                        ["x"] = x0 + m,
                        ["y"] = y0 + ph * 0.88,
                        ["color"] = dcol,
                        ["alpha"] = fa,
                        ["ghost"] = false,
                    });
                    env.DrawItem(new JObject
                    {
                        ["text"] = day + "  " + WD_E[wd],
                        ["font"] = MonoF(env),
                        ["size"] = ls * 1.1,
                        ["track"] = 0.2,
                        ["align"] = "right",
                        ["x"] = x0 + pw - m,
                        ["y"] = y0 + ph * 0.88,
                        ["color"] = dcol,
                        ["alpha"] = fa,
                        ["ghost"] = false,
                    });
                    ctx.Restore();
                    env.Rect(x0 - pw * 0.02, y0, pw * 1.04, bindH, bindC, a, false);
                    foreach (var f in new[] { 0.3, 0.7 })
                        env.Circle(x0 + pw * f, y0 + bindH * 0.5, bindH * 0.22, sc.Str("bg"), null, 0, a, false);
                    env.Body = bb;
                    return;
                }
                const int cols = 7, rows = 5;
                var gw = port ? W * 0.9 : Math.Min(W * 0.84, H * 1.5);
                var cell = gw / cols;
                var gh = cell * rows * (port ? 1 : 0.72);
                var rh = gh / rows;
                var hdrH = ls * 2.2;
                var gx = W / 2 - gw / 2;
                var gy = H / 2 - (gh + hdrH + ls * 3) / 2 + ls * 3 + hdrH;
                var ga = Tin(env, 0, 0.3, J.E.OutCubic) * outK;
                env.DrawItem(new JObject
                {
                    ["text"] = (month + 1).ToString(CultureInfo.InvariantCulture),
                    ["font"] = JLayouts.S(p, "font"),
                    ["size"] = ls * 2.6,
                    ["align"] = "left",
                    ["x"] = gx,
                    ["y"] = gy - hdrH - ls * 1.7,
                    ["color"] = sc.Str("fg"),
                    ["alpha"] = ga,
                    ["ghost"] = false,
                });
                env.DrawItem(new JObject
                {
                    ["text"] = MON_E[month],
                    ["font"] = MonoF(env),
                    ["size"] = ls,
                    ["track"] = 0.4,
                    ["align"] = "left",
                    ["x"] = gx + ls * 2.6,
                    ["y"] = gy - hdrH - ls * 1.3,
                    ["color"] = sc.Str("sub"),
                    ["alpha"] = ga,
                    ["ghost"] = false,
                });
                var gridC = sc.Str("sub");
                for (var c = 0; c < 7; c++)
                    env.DrawItem(new JObject
                    {
                        ["text"] = WD_E[c],
                        ["font"] = MonoF(env),
                        ["size"] = ls * 0.8,
                        ["track"] = 0.2,
                        ["x"] = gx + (c + 0.5) * cell,
                        ["y"] = gy - hdrH * 0.45,
                        ["color"] = c == 0 ? sc.Str("accent") : c == 6 ? (J.Contrast(sc.Str("accent2")!, sc.Str("bg")!) > 1.6 ? sc.Str("accent2") : sc.Str("sub")) : sc.Str("sub"),
                        ["alpha"] = ga,
                        ["ghost"] = false,
                    });
                var zoom = J.E.InOutCubic(J.Clamp((env.Lt - 0.12) / 0.35));
                var dim = 1 - zoom * 0.55;
                for (var r = 0; r <= rows; r++)
                {
                    var e = Tin(env, r * 0.03, 0.4, J.E.InOutCubic) * outK;
                    env.Line(new[] { new[] { gx, gy + r * rh }, new[] { gx + gw * e, gy + r * rh } }, gridC, 1, 0.45 * dim, false);
                }
                double tcx = 0, tcy = 0;
                for (var d = 1; d <= days; d++)
                {
                    var idx = JLayouts.I(p, "off") + d - 1;
                    var c = idx % 7;
                    var r = (int)Math.Floor(idx / 7.0) % rows;
                    var x = gx + c * cell;
                    var y = gy + r * rh;
                    if (d == day)
                    {
                        tcx = x;
                        tcy = y;
                    }
                    var a = J.Clamp((env.Lt - 0.02 - idx * 0.006) / 0.15) * outK * dim;
                    if (a <= 0.01 || d == day) continue;
                    env.DrawItem(new JObject
                    {
                        ["text"] = d.ToString(CultureInfo.InvariantCulture),
                        ["font"] = MonoF(env),
                        ["size"] = Math.Min(rh * 0.28, cell * 0.24),
                        ["align"] = "left",
                        ["x"] = x + cell * 0.08,
                        ["y"] = y + rh * 0.22,
                        ["color"] = c == 0 ? sc.Str("accent") : sc.Str("fg"),
                        ["alpha"] = a * 0.8,
                        ["ghost"] = false,
                    });
                }
                var PW = port ? W * 0.84 : Math.Min(W * 0.7, gw * 0.9);
                var PH = port ? H * 0.34 : H * 0.5;
                var X1 = W / 2 - PW / 2;
                var Y1 = gy + gh / 2 - PH / 2;
                var bx = J.Lerp(tcx, X1, zoom);
                var by = J.Lerp(tcy, Y1, zoom);
                var bw = J.Lerp(cell, PW, zoom);
                var bh = J.Lerp(rh, PH, zoom);
                var plate = PlateCol(sc, new[] { sc.Str("accent"), sc.Str("ink") });
                var pa2 = J.Clamp(env.Lt / 0.12) * outK;
                ShadowR(env, bx, by, bw, bh, 0, pa2 * zoom, u * 0.015);
                env.Rect(bx, by, bw, bh, plate, pa2, false);
                var tc = OnCol(sc, plate);
                var k = bw / PW;
                env.DrawItem(new JObject
                {
                    ["text"] = day.ToString(CultureInfo.InvariantCulture),
                    ["font"] = MonoF(env),
                    ["size"] = Math.Max(rh * 0.28, PH * 0.12 * k),
                    ["align"] = "left",
                    ["x"] = bx + bw * 0.04,
                    ["y"] = by + bh * 0.13,
                    ["color"] = tc,
                    ["alpha"] = pa2,
                    ["ghost"] = false,
                });
                env.DrawItem(new JObject
                {
                    ["text"] = WD_E[wd],
                    ["font"] = MonoF(env),
                    ["size"] = Math.Max(6, PH * 0.06 * k),
                    ["track"] = 0.3,
                    ["align"] = "right",
                    ["x"] = bx + bw * 0.96,
                    ["y"] = by + bh * 0.12,
                    ["color"] = tc,
                    ["alpha"] = pa2 * zoom,
                    ["ghost"] = false,
                });
                var fb2 = FitBlock(t0, JLayouts.S(p, "font")!, PW * 0.88, PH * 0.62, new JObject { ["lead"] = 1.08, ["track"] = 0.02 }, 3);
                ctx.Save();
                ctx.Translate((float)bx, (float)by);
                ctx.Scale((float)Math.Max(0.01, k), (float)Math.Max(0.01, bh / PH));
                var bb2 = JLayouts.MainDraw(env, new JObject
                {
                    ["text"] = fb2.Text,
                    ["font"] = JLayouts.S(p, "font"),
                    ["size"] = Math.Min(fb2.Size, u * 0.2),
                    ["x"] = PW / 2,
                    ["y"] = PH * 0.57,
                    ["lead"] = 1.08,
                    ["track"] = 0.02,
                    ["color"] = tc,
                    ["noHold"] = PlateHold(env),
                    ["mi"] = MiAt(env, 0.2),
                });
                ctx.Restore();
                env.Body = bb2 != null ? Box(X1, Y1, X1 + PW, Y1 + PH) : null;
            },
        });
    }

        private sealed class LanternGeo
    {
        public double Y0, Y1, Bh;
        public Func<double, double> Hw = _ => 0;
    }

        private static LanternGeo Lantern(Env env, double cx, double top, double lw, double lh, string bodyC, double a, bool glow, double flick)
    {
        var sc = env.Sc!;
        var ctx = env.Ctx;
        var capH = lh * 0.07;
        var capC = PlateCol(sc, new[] { Darkest(sc), sc.Str("ink") }, bodyC, 1.6);
        double Hw(double t) => lw / 2 * (0.62 + 0.38 * Math.Sin(Math.PI * t));
        var y0 = top + capH;
        var y1 = top + lh - capH;
        var bh = y1 - y0;
        if (glow && env.Pass == "main" && a > 0.01)
        {
            using var g = new SKPaint
            {
                IsAntialias = true,
                BlendMode = env.Blend,
                Color = Env.SkColor("#FFFFFF", a),
                Shader = SKShader.CreateRadialGradient(
                    new SKPoint((float)cx, (float)(top + lh / 2)), (float)(lw * 1.3),
                    new[] { Env.SkColor(bodyC, 0.35 * flick), Env.SkColor(bodyC, 0) },
                    new[] { 0f, 1f }, SKShaderTileMode.Clamp),
            };
            ctx.DrawRect(new SKRect((float)(cx - lw * 1.4), (float)(top - lw * 0.4), (float)(cx + lw * 1.4), (float)(top + lh + lw * 0.4)), g);
        }
        var pts = new List<double[]>();
        const int M = 24;
        for (var i = 0; i <= M; i++)
        {
            var t = i / (double)M;
            pts.Add(new[] { cx + Hw(t), y0 + bh * t });
        }
        for (var i = M; i >= 0; i--)
        {
            var t = i / (double)M;
            pts.Add(new[] { cx - Hw(t), y0 + bh * t });
        }
        env.Poly(pts.ToArray(), bodyC, a, false);
        if (env.Pass == "main")
        {
            ctx.Save();
            using var rib = new SKPaint
            {
                IsAntialias = true,
                IsStroke = true,
                StrokeWidth = (float)Math.Max(1, lw * 0.006),
                Color = Env.SkColor(OnCol(sc, bodyC), a * 0.22),
                BlendMode = env.Blend,
            };
            using var ribPath = new SKPath();
            const int R = 13;
            for (var k = 1; k < R; k++)
            {
                var t = k / (double)R;
                var y = y0 + bh * t;
                var w2 = Hw(t);
                ribPath.MoveTo((float)(cx - w2), (float)y);
                ribPath.QuadTo((float)cx, (float)(y + bh * 0.035), (float)(cx + w2), (float)y);
            }
            ctx.DrawPath(ribPath, rib);
            ctx.Restore();
        }
        env.RRect(cx - lw * 0.33, top, lw * 0.66, capH * 1.05, capH * 0.2, capC, a, false);
        env.RRect(cx - lw * 0.33, y1 - capH * 0.05, lw * 0.66, capH * 1.05, capH * 0.2, capC, a, false);
        return new LanternGeo { Y0 = y0, Y1 = y1, Bh = bh, Hw = Hw };
    }

    private static void RegisterChochin()
    {
        RegC("chochin", new LayoutDef
        {
            Name = "提灯",
            Tags = new[] { "emotional", "calm", "pop" },
            W = 0.7,
            Treat = "safe",
            Portrait = 1.1,
            Fits = n => n >= 1 && n <= 12,
            EnterBias = new JObject { ["blur"] = 1.4, ["flicker"] = 1.4, ["cut"] = 1.2, ["slice"] = 0.4 },
            Plan = (rng, cut, st) =>
            {
                var n = cut.N;
                var variant = n <= 6 && !HasLatin(cut.Text) ? rng.Pick(new[] { "single", "single", "row" }) : "row";
                List<string> units;
                if (variant == "row")
                    units = HasLatin(cut.Text)
                        ? SplitK(cut.Text, 6)
                        : n <= 6
                            ? CharUnits(cut.Text)
                            : SplitK(cut.Text, Math.Min(6, (int)Math.Ceiling(n / 2.0)), Math.Min(6, (int)Math.Ceiling(n / 2.0)));
                else
                    units = new List<string> { HasLatin(cut.Text) ? Flat(cut.Text) : Strip(cut.Text) };
                return new JObject
                {
                    ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
                    ["variant"] = variant,
                    ["units"] = new JArray(units),
                    ["body"] = rng.Pick(new[] { "accent", "accent", "paper" }),
                    ["ph"] = rng.Range(0, 6),
                };
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var ctx = env.Ctx;
                var p = env.P;
                var u = U(env);
                var port = IsPort(env);
                var outK = Tout(env);
                var C = Card(sc);
                var paperB = JLayouts.S(p, "body") == "paper" && J.Contrast(C.Fill, sc.Str("bg")!) >= 1.5;
                var bodyC = paperB ? C.Fill : PlateCol(sc, new[] { sc.Str("accent"), sc.Str("ink") }, sc.Str("bg"), 1.3);
                var tc = paperB ? PlateCol(sc, new[] { sc.Str("accent"), Darkest(sc) }, C.Fill, 2.2) : OnCol(sc, bodyC);
                var units = (p["units"] as JArray)?.Select(t => (string?)t).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();
                if (units == null || units.Count == 0) units = new List<string> { Strip(env.Cut.Text) };
                var flick = 0.8 + 0.2 * J.Noise1(env.Ltb * 3, unchecked((int)env.Cut.Seed));
                var ein = J.E.OutCubic(J.Clamp(env.Lt / 0.5));
                var a = J.Clamp(ein * 2) * (1 - J.Clamp((env.POut - 0.4) / 0.6));
                if (a <= 0.01)
                {
                    env.Body = null;
                    return;
                }
                BBox? bb = null;
                if (JLayouts.S(p, "variant") == "single")
                {
                    var t = units[0];
                    var n = JLayouts.GlyphCount(t);
                    var lw = port ? W * 0.56 : Math.Min(W * 0.34, H * 0.56);
                    var lh = Math.Min(lw * 1.45, H * 0.8);
                    var top = H / 2 - lh / 2 + H * 0.03 - (1 - ein) * H * 0.3;
                    var sw = Math.Sin(env.Ltb * 1.3 + JLayouts.D(p, "ph")) * 2.2 + (1 - ein) * 6;
                    env.Line(new[] { new[] { W / 2, -5.0 }, new[] { W / 2, top } }, sc.Str("sub"), Math.Max(2, u * 0.003), a, false);
                    ctx.Save();
                    ctx.Translate((float)(W / 2), (float)top);
                    ctx.RotateDegrees((float)sw);
                    ctx.Translate((float)(-W / 2), (float)-top);
                    var L = Lantern(env, W / 2, top, lw, lh, bodyC, a, true, flick);
                    var cols = n > 5 ? 2 : 1;
                    var vt = cols > 1 ? Brk(t, (int)Math.Ceiling(n / 2.0)) : t;
                    var per = vt.Split('\n').Max(l => JLayouts.GlyphCount(l));
                    var size = Math.Min(L.Bh * 0.8 / per, lw * (cols > 1 ? 0.3 : 0.46));
                    var r = JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = vt,
                        ["font"] = JLayouts.S(p, "font"),
                        ["size"] = size,
                        ["x"] = W / 2,
                        ["y"] = L.Y0 + L.Bh / 2 - per * size * 0.5,
                        ["vertical"] = true,
                        ["align"] = "left",
                        ["lead"] = 1.15,
                        ["color"] = tc,
                        ["noHold"] = PlateHold(env),
                        ["mi"] = MiAt(env, 0.2),
                    });
                    ctx.Restore();
                    if (r != null) bb = Box(W / 2 - lw / 2, top, W / 2 + lw / 2, top + lh);
                    env.Body = bb;
                    return;
                }
                var k = units.Count;
                var rowsN = port && k > 3 ? 2 : 1;
                var perX = (int)Math.Ceiling(k / (double)rowsN);
                var sp = W * 0.88 / perX;
                var lw2 = Math.Min(sp * 0.78, H * (rowsN > 1 ? 0.2 : 0.3));
                var lh2 = lw2 * 1.4;
                for (var r = 0; r < rowsN; r++)
                {
                    var cnt = Math.Min(perX, k - r * perX);
                    var wireY = rowsN > 1 ? H * (r != 0 ? 0.55 : 0.14) : H * 0.22;
                    var x0 = W / 2 - (cnt - 1) / 2.0 * sp;
                    var sag = H * 0.05;
                    double Wy(double x) => wireY + sag * (1 - Math.Pow((x - W / 2) / (W * 0.5), 2));
                    var we = Tin(env, 0, 0.5, J.E.InOutCubic) * outK;
                    var pts = new List<double[]>();
                    for (var i = 0; i <= 30; i++)
                    {
                        var x = J.Lerp(-10, W + 10, i / 30.0);
                        pts.Add(new[] { x, Wy(x) });
                    }
                    env.PolyPartial(pts.ToArray(), we, sc.Str("sub"), Math.Max(1.5, u * 0.0025), 0.8, false);
                    for (var j = 0; j < cnt; j++)
                    {
                        var i = r * perX + j;
                        var t = units[i];
                        var x = x0 + j * sp;
                        var ty = Wy(x) + lw2 * 0.1;
                        var t1 = 0.08 + i * 0.07;
                        var e = J.E.OutBack(J.Clamp((env.Lt - t1) / 0.35), 1.4);
                        if (e <= 0) continue;
                        var sw = Math.Sin(env.Ltb * 1.6 + i * 1.1 + JLayouts.D(p, "ph")) * 3 + (1 - e) * 10 * (i % 2 != 0 ? 1 : -1);
                        ctx.Save();
                        ctx.Translate((float)x, (float)ty);
                        ctx.RotateDegrees((float)sw);
                        ctx.Scale((float)e, (float)e);
                        ctx.Translate((float)-x, (float)-ty);
                        env.Line(new[] { new[] { x, ty - lw2 * 0.1 }, new[] { x, ty } }, sc.Str("sub"), Math.Max(1.5, u * 0.002), a, false);
                        var L = Lantern(env, x, ty, lw2, lh2, bodyC, a, true, flick * (0.85 + 0.15 * J.Noise1(env.Ltb * 4 + i, 3)));
                        var n = JLayouts.GlyphCount(t);
                        var lat = HasLatin(t);
                        var size = lat
                            ? Math.Min(JLayouts.FitSize(t, JLayouts.S(p, "font")!, lw2 * 0.8, L.Bh * 0.5), lw2 * 0.4)
                            : Math.Min(L.Bh * 0.78 / Math.Max(1, n), lw2 * 0.5);
                        var rr = lat
                            ? JLayouts.MainDraw(env, new JObject
                            {
                                ["text"] = t,
                                ["font"] = JLayouts.S(p, "font"),
                                ["size"] = size,
                                ["x"] = x,
                                ["y"] = L.Y0 + L.Bh / 2,
                                ["color"] = tc,
                                ["noHold"] = PlateHold(env),
                                ["mi"] = MiAt(env, t1 + 0.12),
                            })
                            : JLayouts.MainDraw(env, new JObject
                            {
                                ["text"] = Strip(t),
                                ["font"] = JLayouts.S(p, "font"),
                                ["size"] = size,
                                ["x"] = x,
                                ["y"] = L.Y0 + L.Bh / 2 - n * size * 0.5,
                                ["vertical"] = true,
                                ["align"] = "left",
                                ["color"] = tc,
                                ["noHold"] = PlateHold(env),
                                ["mi"] = MiAt(env, t1 + 0.12),
                            });
                        ctx.Restore();
                        if (rr != null) bb = JLayouts.UnionBb(bb, Box(x - lw2 / 2, ty, x + lw2 / 2, ty + lh2));
                    }
                }
                env.Body = bb;
            },
        });
    }

    private static void RegisterRouteMap()
    {
        RegC("routeMap", new LayoutDef
        {
            Name = "路線図",
            Tags = new[] { "graphic", "pop", "editorial" },
            W = 0.8,
            Fits = n => n >= 2 && n <= 18,
            EnterBias = new JObject { ["wipe"] = 1.4, ["pop"] = 1.3, ["type"] = 1.2 },
            Plan = (rng, cut, st) =>
            {
                var n = cut.N;
                var k = n <= 6 ? 2 : n <= 10 ? 3 : 4;
                return new JObject
                {
                    ["font"] = rng.Pick(FontsOf(st, "display", "body")),
                    ["chunks"] = new JArray(n <= 3 ? CharUnits(cut.Text) : SplitK(cut.Text, k)),
                    ["shape"] = rng.Pick(new[] { "straight", "bend", "straight" }),
                    ["letter"] = rng.Pick(new[] { "Z", "J", "M", "K", "S" }),
                    ["num0"] = rng.Int(1, 14),
                };
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var p = env.P;
                var u = U(env);
                var port = IsPort(env);
                var outK = Tout(env);
                var ls = SmallSize(env);
                var chunks = (p["chunks"] as JArray)?.Select(t => (string?)t).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();
                if (chunks == null || chunks.Count == 0) chunks = new List<string> { env.Cut.Text.Trim() };
                var k = chunks.Count;
                var lineC = PlateCol(sc, new[] { sc.Str("accent"), sc.Str("accent2"), sc.Str("fg") });
                var lw = Math.Max(8, u * 0.024);
                var bend = !port && JLayouts.S(p, "shape") == "bend" && k >= 2;
                double[][] pts;
                if (port) pts = new[] { new[] { W * 0.22, H * 0.07 }, new[] { W * 0.22, H * 0.93 } };
                else if (bend)
                    pts = new[]
                    {
                        new[] { W * 0.04, H * 0.36 }, new[] { W * 0.42, H * 0.36 }, new[] { W * 0.58, H * 0.64 }, new[] { W * 0.96, H * 0.64 }
                    };
                else pts = new[] { new[] { W * 0.04, H * 0.54 }, new[] { W * 0.96, H * 0.54 } };
                var segL = new List<double>();
                var tot = 0.0;
                for (var i = 1; i < pts.Length; i++)
                {
                    var d = Math.Sqrt(Math.Pow(pts[i][0] - pts[i - 1][0], 2) + Math.Pow(pts[i][1] - pts[i - 1][1], 2));
                    segL.Add(d);
                    tot += d;
                }
                double[] At(double f)
                {
                    var d = f * tot;
                    for (var i = 0; i < segL.Count; i++)
                    {
                        if (d <= segL[i] || i == segL.Count - 1)
                        {
                            var q = J.Clamp(d / segL[i]);
                            return new[] { J.Lerp(pts[i][0], pts[i + 1][0], q), J.Lerp(pts[i][1], pts[i + 1][1], q) };
                        }
                        d -= segL[i];
                    }
                    return pts[^1];
                }
                var stations = new List<(double X, double Y, double F, int Side, double MaxW)>();
                double Spread(double a0, double a1, int m, int i) => m > 1 ? J.Lerp(a0, a1, i / (double)(m - 1)) : (a0 + a1) / 2;
                if (port)
                {
                    for (var i = 0; i < chunks.Count; i++)
                    {
                        var y = Spread(H * 0.15, H * 0.85, k, i);
                        stations.Add((pts[0][0], y, (y - pts[0][1]) / tot, 1, W * 0.66));
                    }
                }
                else if (bend)
                {
                    var cu = (int)Math.Ceiling(k / 2.0);
                    var cl = k - cu;
                    for (var i = 0; i < chunks.Count; i++)
                    {
                        var up = i < cu;
                        var j = up ? i : i - cu;
                        var m = up ? cu : cl;
                        var x = up ? Spread(W * 0.09, W * 0.38, m, j) : Spread(W * 0.63, W * 0.92, m, j);
                        var f = up ? (x - pts[0][0]) / tot : (segL[0] + segL[1] + x - pts[2][0]) / tot;
                        stations.Add((x, up ? pts[0][1] : pts[2][1], f, up ? -1 : 1, m > 1 ? W * 0.29 * 0.84 : W * 0.34));
                    }
                }
                else
                {
                    var alt = k >= 3;
                    var gap = W * 0.76 / Math.Max(1, k - 1);
                    for (var i = 0; i < chunks.Count; i++)
                    {
                        var x = Spread(W * 0.12, W * 0.88, k, i);
                        stations.Add((x, pts[0][1], (x - pts[0][0]) / tot, alt && i % 2 != 0 ? 1 : -1,
                            Math.Min(W * 0.42, (alt ? gap * 2 : gap) * 0.88)));
                    }
                }
                var maxH = port ? Math.Min(H * 0.7 / k * 0.45, H * 0.1) : H * 0.13;
                var size = Math.Min(chunks.Select((c, i) => JLayouts.FitSize(c, JLayouts.S(p, "font")!, stations[i].MaxW, maxH, new JObject { ["track"] = 0.02 })).Min(),
                    u * (port ? 0.15 : 0.13));
                var le = Tin(env, 0, 0.6, J.E.InOutCubic) * (1 - J.E.InCubic(env.POut));
                env.PolyPartial(pts, le, lineC, lw, 1, false);
                var prog = J.Clamp((env.Lt - 0.3) / Math.Max(0.4, env.Cut.Dur * 0.7));
                var cur = Math.Min(k - 1, (int)Math.Floor(prog * k));
                if (le > 0.9)
                {
                    var f1 = stations[cur].F;
                    var f0 = cur > 0 ? stations[cur - 1].F : Math.Max(0, f1 - 0.08);
                    var f = J.Lerp(f0, f1, J.E.InOutCubic(J.Clamp(prog * k - cur)));
                    var tp = At(f);
                    double tx = tp[0], ty = tp[1];
                    var tw = lw * (port ? 1.3 : 2.6);
                    var th = lw * (port ? 2.6 : 1.3);
                    env.RRect(tx - tw / 2, ty - th / 2, tw, th, Math.Min(tw, th) * 0.45, sc.Str("fg"), outK, false, sc.Str("bg"), Math.Max(2, lw * 0.18));
                }
                BBox? bb = null;
                var bs = ls * 0.9;
                for (var i = 0; i < chunks.Count; i++)
                {
                    var c = chunks[i];
                    var S = stations[i];
                    var ta = J.Clamp((le * 1.05 - S.F) * 6);
                    if (ta <= 0) continue;
                    var isCur = i == cur;
                    var r = lw * (isCur ? 1.05 : 0.8);
                    if (isCur)
                    {
                        if (port)
                            env.RRect(S.X - r * 1.05, S.Y - r * 1.5, r * 2.1, r * 3, r, sc.Str("bg"), 1, false, sc.Str("fg"), Math.Max(3, lw * 0.35));
                        else
                            env.RRect(S.X - r * 1.5, S.Y - r * 1.05, r * 3, r * 2.1, r, sc.Str("bg"), 1, false, sc.Str("fg"), Math.Max(3, lw * 0.35));
                    }
                    else env.Circle(S.X, S.Y, r * ta, sc.Str("bg"), sc.Str("fg"), Math.Max(3, lw * 0.32), 1, false);
                    var code = JLayouts.S(p, "letter") + Pad2(JLayouts.I(p, "num0") + i);
                    var rom = RomaOfText(c);
                    if (port)
                    {
                        var lx = S.X + lw * 1.8;
                        env.RRect(S.X - lw * 1.4 - bs * 2.8, S.Y - bs * 0.9, bs * 2.8, bs * 1.8, bs * 0.3, null, ta * outK, false, lineC, Math.Max(2, bs * 0.12));
                        env.DrawItem(new JObject
                        {
                            ["text"] = code,
                            ["font"] = MonoF(env),
                            ["size"] = bs * 0.8,
                            ["x"] = S.X - lw * 1.4 - bs * 1.4,
                            ["y"] = S.Y,
                            ["color"] = sc.Str("fg"),
                            ["alpha"] = ta * outK,
                            ["ghost"] = false,
                        });
                        bb = JLayouts.UnionBb(bb, JLayouts.MainDraw(env, new JObject
                        {
                            ["text"] = c,
                            ["font"] = JLayouts.S(p, "font"),
                            ["size"] = size,
                            ["x"] = lx,
                            ["y"] = S.Y - (rom != null ? ls * 0.4 : 0),
                            ["align"] = "left",
                            ["track"] = 0.02,
                            ["color"] = sc.Str("fg"),
                            ["mi"] = MiAt(env, 0.1 + i * 0.12),
                        }));
                        if (rom != null)
                            env.DrawItem(new JObject
                            {
                                ["text"] = rom,
                                ["font"] = MonoF(env),
                                ["size"] = ls * 0.8,
                                ["track"] = 0.15,
                                ["align"] = "left",
                                ["x"] = lx,
                                ["y"] = S.Y + size * 0.55,
                                ["color"] = sc.Str("sub"),
                                ["alpha"] = ta * outK,
                                ["ghost"] = false,
                            });
                    }
                    else
                    {
                        var d = S.Side;
                        var w = Meas(c, JLayouts.S(p, "font")!, size, new JObject { ["track"] = 0.02 }).W;
                        var lx = J.Clamp(S.X, W * 0.04 + w / 2, W * 0.96 - w / 2);
                        var by = S.Y + d * (lw * 1.25) + (d < 0 ? -bs * 1.8 : 0);
                        env.RRect(S.X - bs * 1.4, by, bs * 2.8, bs * 1.8, bs * 0.3, null, ta * outK, false, lineC, Math.Max(2, bs * 0.12));
                        env.DrawItem(new JObject
                        {
                            ["text"] = code,
                            ["font"] = MonoF(env),
                            ["size"] = bs * 0.8,
                            ["x"] = S.X,
                            ["y"] = by + bs * 0.9,
                            ["color"] = sc.Str("fg"),
                            ["alpha"] = ta * outK,
                            ["ghost"] = false,
                        });
                        var ny = S.Y + d * (lw * 1.25 + bs * 2.2 + size * 0.5 + (d < 0 && rom != null ? ls * 1.2 : 0));
                        bb = JLayouts.UnionBb(bb, JLayouts.MainDraw(env, new JObject
                        {
                            ["text"] = c,
                            ["font"] = JLayouts.S(p, "font"),
                            ["size"] = size,
                            ["x"] = lx,
                            ["y"] = ny,
                            ["track"] = 0.02,
                            ["color"] = sc.Str("fg"),
                            ["mi"] = MiAt(env, 0.1 + i * 0.12),
                        }));
                        if (rom != null)
                            env.DrawItem(new JObject
                            {
                                ["text"] = rom,
                                ["font"] = MonoF(env),
                                ["size"] = ls * 0.75,
                                ["track"] = 0.15,
                                ["x"] = lx,
                                ["y"] = d < 0 ? ny + size * 0.5 + ls * 0.7 : ny + size * 0.5 + ls * 0.9,
                                ["color"] = sc.Str("sub"),
                                ["alpha"] = ta * outK,
                                ["ghost"] = false,
                            });
                    }
                }
                var ba = Tin(env, 0.05, 0.4, x => J.E.OutBack(x)) * outK;
                if (ba > 0.01)
                {
                    var R = lw * 1.7;
                    var cx2 = port ? pts[0][0] : pts[0][0] + R * 0.2;
                    var cy2 = port ? pts[0][1] + R * 0.1 : pts[0][1] + (stations[0].Side < 0 ? 1 : -1) * R * 2;
                    env.Circle(cx2, cy2, R * ba, sc.Str("bg"), lineC, Math.Max(3, lw * 0.5), 1, false);
                    env.DrawItem(new JObject
                    {
                        ["text"] = JLayouts.S(p, "letter"),
                        ["font"] = "gothic_black",
                        ["size"] = R * 1.1 * ba,
                        ["x"] = cx2,
                        ["y"] = cy2,
                        ["color"] = sc.Str("fg"),
                        ["ghost"] = false,
                    });
                }
                env.Body = bb;
            },
        });
    }

    private static void RegisterStationSign()
    {
        RegC("stationSign", new LayoutDef
        {
            Name = "駅名標",
            Tags = new[] { "graphic", "pop", "editorial" },
            W = 0.8,
            Treat = "safe",
            Portrait = 0.6,
            Fits = n => n >= 1 && n <= 12,
            EnterBias = new JObject { ["cut"] = 1.4, ["wipe"] = 1.3, ["slice"] = 1.2 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display", "body")),
                ["letter"] = rng.Pick(new[] { "JZ", "LY", "KT", "SN" }),
                ["num"] = rng.Int(1, 36),
                ["band"] = rng.Pick(new[] { "accent", "accent2", "ink" }),
                ["posts"] = rng.Chance(0.7),
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var p = env.P;
                var u = U(env);
                var port = IsPort(env);
                var outK = Tout(env);
                var ls = SmallSize(env);
                var C = Card(sc);
                var t0 = env.Cut.Text.Trim();
                var bw = port ? W * 0.92 : Math.Min(W * 0.86, H * 2.2);
                var bh = port ? bw * 0.62 : bw * 0.36;
                var e = J.E.OutCubic(J.Clamp(env.Lt / 0.45));
                var eo = J.E.InCubic(env.POut);
                var a = J.Clamp(e * 2) * (1 - eo);
                if (a <= 0.01)
                {
                    env.Body = null;
                    return;
                }
                var x0 = W / 2 - bw / 2;
                var y0 = H / 2 - bh / 2 - (port ? H * 0.04 : H * 0.03) - (1 - e) * H * 0.2 + eo * H * 0.05;
                var boardC = J.Contrast(C.Fill, sc.Str("bg")!) >= 1.4 ? C.Fill : PlateCol(sc, new[] { sc.Str("ink"), sc.Str("fg") });
                var txC = OnCol(sc, boardC);
                var bandKind = JLayouts.S(p, "band");
                var bandC = PlateCol(sc,
                    bandKind == "accent2" ? new[] { sc.Str("accent2"), sc.Str("accent") }
                    : bandKind == "ink" ? new[] { sc.Str("ink"), sc.Str("accent") }
                    : new[] { sc.Str("accent"), sc.Str("accent2") }, boardC, 1.4);
                if (JLayouts.Truthy(p["posts"]))
                {
                    var pw = bw * 0.018;
                    foreach (var f in new[] { 0.12, 0.88 })
                        env.Rect(x0 + bw * f - pw / 2, y0 + bh, pw, H - y0 - bh + 10, sc.Str("sub"), a, false);
                }
                ShadowR(env, x0, y0, bw, bh, bh * 0.04, a, u * 0.012);
                env.RRect(x0, y0, bw, bh, bh * 0.04, boardC, a, false, null, 1.5);
                var L = Flat(env.Cut.LineText ?? "");
                var T = Flat(t0);
                var idx = L.IndexOf(T, StringComparison.Ordinal);
                string CutS(string s2, int n2, bool fromEnd)
                {
                    var arr = JLayouts.CodePoints(s2.Trim());
                    var take = fromEnd ? arr.Skip(Math.Max(0, arr.Count - n2)) : arr.Take(n2);
                    return string.Concat(take);
                }
                var prev = idx > 0 ? CutS(L[..idx], 6, true) : "";
                var next = idx >= 0 && idx + T.Length < L.Length ? CutS(L[(idx + T.Length)..], 6, false) : "";
                var bandY = y0 + bh * (port ? 0.66 : 0.64);
                var bandH = bh * (port ? 0.075 : 0.1);
                var be = Tin(env, 0.15, 0.5, J.E.InOutCubic) * outK;
                env.Rect(x0, bandY, bw * be, bandH, bandC, a, false);
                var cx = W / 2;
                var bxW = bw * 0.28;
                if (be > 0.5)
                {
                    env.Rect(cx - bxW / 2, bandY - bandH * 0.25, bxW, bandH * 1.5, bandC, a, false);
                    env.Poly(new[]
                    {
                        new[] { x0 + bw * 0.97, bandY - bandH * 0.4 }, new[] { x0 + bw, bandY + bandH / 2 },
                        new[] { x0 + bw * 0.97, bandY + bandH * 1.4 }
                    }, bandC, a * J.Clamp((be - 0.5) * 2), false);
                }
                var bs = bh * (port ? 0.16 : 0.2);
                var fb = FitBlock(t0, JLayouts.S(p, "font")!, bw - (bw * 0.06 + bs * 1.4) * 2, bh * (port ? 0.42 : 0.4),
                    new JObject { ["track"] = 0.12, ["lead"] = 1.05 }, port ? 2 : 1);
                var size = Math.Min(fb.Size, bh * 0.36);
                var ny = y0 + bh * 0.3;
                var bb = JLayouts.MainDraw(env, new JObject
                {
                    ["text"] = fb.Text,
                    ["font"] = JLayouts.S(p, "font"),
                    ["size"] = size,
                    ["x"] = cx,
                    ["y"] = ny,
                    ["track"] = 0.12,
                    ["lead"] = 1.05,
                    ["color"] = txC,
                    ["noHold"] = PlateHold(env),
                    ["mi"] = MiAt(env, 0.15),
                });
                var fa = Tin(env, 0.25, 0.4, J.E.OutCubic) * outK;
                var rom = RomajiOf(env);
                var sub2 = rom != null ? rom[..1] + rom[1..].ToLowerInvariant() : "No." + LineNo(env);
                env.DrawItem(new JObject
                {
                    ["text"] = sub2,
                    ["font"] = BodyF(env),
                    ["size"] = ls * (port ? 1.1 : 1.2),
                    ["track"] = 0.1,
                    ["x"] = cx,
                    ["y"] = bandY - bandH * 0.25 - ls * 1.3,
                    ["color"] = txC,
                    ["alpha"] = fa,
                    ["ghost"] = false,
                });
                var bx2 = x0 + bw * 0.06;
                var by2 = ny - bs / 2;
                env.RRect(bx2, by2, bs, bs * 1.1, bs * 0.12, boardC, fa, false, bandC, Math.Max(2, bs * 0.08));
                env.Rect(bx2, by2, bs, bs * 0.34, bandC, fa, false);
                env.DrawItem(new JObject
                {
                    ["text"] = JLayouts.S(p, "letter"),
                    ["font"] = MonoF(env),
                    ["size"] = bs * 0.24,
                    ["x"] = bx2 + bs / 2,
                    ["y"] = by2 + bs * 0.17,
                    ["color"] = OnCol(sc, bandC),
                    ["alpha"] = fa,
                    ["ghost"] = false,
                });
                env.DrawItem(new JObject
                {
                    ["text"] = Pad2(JLayouts.I(p, "num")),
                    ["font"] = "gothic_black",
                    ["size"] = bs * 0.5,
                    ["x"] = bx2 + bs / 2,
                    ["y"] = by2 + bs * 0.72,
                    ["color"] = txC,
                    ["alpha"] = fa,
                    ["ghost"] = false,
                });
                var py = bandY + bandH + (bh - (bandY - y0) - bandH) * 0.5;
                var num = JLayouts.I(p, "num");
                var letter = JLayouts.S(p, "letter");
                var pv = prev.Length > 0 ? prev : "← " + letter + Pad2(Math.Max(0, num - 1));
                var nx = next.Length > 0 ? next : letter + Pad2(num + 1) + " →";
                var pf = prev.Length > 0 ? JLayouts.S(p, "font")! : MonoF(env);
                var nf = next.Length > 0 ? JLayouts.S(p, "font")! : MonoF(env);
                env.DrawItem(new JObject
                {
                    ["text"] = pv,
                    ["font"] = pf,
                    ["size"] = ls * 1.25,
                    ["align"] = "left",
                    ["x"] = x0 + bw * 0.04,
                    ["y"] = py,
                    ["color"] = txC,
                    ["alpha"] = fa * (prev.Length > 0 ? 1 : 0.7),
                    ["ghost"] = false,
                });
                env.DrawItem(new JObject
                {
                    ["text"] = nx,
                    ["font"] = nf,
                    ["size"] = ls * 1.25,
                    ["align"] = "right",
                    ["x"] = x0 + bw * 0.96,
                    ["y"] = py,
                    ["color"] = txC,
                    ["alpha"] = fa * (next.Length > 0 ? 1 : 0.7),
                    ["ghost"] = false,
                });
                env.Body = bb != null ? Box(x0, y0, x0 + bw, y0 + bh) : null;
            },
        });
    }

    private static void RegisterNoren()
    {
        RegC("noren", new LayoutDef
        {
            Name = "暖簾",
            Tags = new[] { "calm", "emotional", "graphic" },
            W = 0.8,
            Treat = "safe",
            Portrait = 1.1,
            Fits = n => n >= 1 && n <= 12,
            EnterBias = new JObject { ["wipe"] = 1.5, ["blur"] = 1.3, ["cut"] = 1.2, ["slice"] = 0.4 },
            Plan = (rng, cut, st) =>
            {
                var n = cut.N;
                var units = n <= 4
                    ? CharUnits(cut.Text)
                    : SplitK(cut.Text, Math.Min(4, (int)Math.Ceiling(n / 3.0)), Math.Min(4, (int)Math.Ceiling(n / 3.0)));
                return new JObject
                {
                    ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
                    ["units"] = new JArray(units),
                    ["cloth"] = rng.Pick(new[] { "ink", "accent", "ink" }),
                    ["mon"] = rng.Chance(0.35),
                    ["ph"] = rng.Range(0, 6),
                    ["wind"] = rng.Pick(new[] { 1, -1 }),
                };
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var ctx = env.Ctx;
                var p = env.P;
                var u = U(env);
                var port = IsPort(env);
                var units = (p["units"] as JArray)?.Select(t => (string?)t).Where(s => !string.IsNullOrEmpty(s)).Select(VText).ToList();
                if (units == null || units.Count == 0) units = new List<string> { VText(env.Cut.Text) };
                if (units.Count == 1) units = new List<string> { "", units[0], "" };                 var k = units.Count;
                var clothC = PlateCol(sc, JLayouts.S(p, "cloth") == "accent" ? new[] { sc.Str("accent"), sc.Str("ink") } : new[] { sc.Str("ink"), sc.Str("accent") });
                var tc = OnCol(sc, clothC);
                var nw = port ? W * 0.88 : Math.Min(W * 0.74, H * 1.3);
                var nh = port ? H * 0.56 : H * 0.7;
                var rodY = H * (port ? 0.18 : 0.12);
                var x0 = W / 2 - nw / 2;
                var gap = nw * 0.012;
                var pw = (nw - gap * (k - 1)) / k;
                var rodE = Tin(env, 0, 0.4, J.E.OutCubic);
                var fadeO = 1 - J.Clamp((env.POut - 0.55) / 0.45);
                var lw = Math.Max(4, u * 0.012);
                env.RRect(x0 - nw * 0.06, rodY - lw / 2, nw * 1.12 * rodE, lw, lw / 2, sc.Str("sub"), fadeO, false);
                var part = J.E.InOutCubic(env.POut);
                BBox? bb = null;
                var maxG = units.Max(t => JLayouts.GlyphCount(t));
                var hem = nh * 0.08;
                var size = Math.Min((nh - hem * 1.6) * 0.84 / Math.Max(maxG, 1.6), pw * 0.66);
                for (var i = 0; i < k; i++)
                {
                    var t1 = 0.04 + i * 0.06;
                    var drop = J.E.OutCubic(J.Clamp((env.Lt - t1) / 0.45));
                    if (drop <= 0) continue;
                    var px = x0 + i * (pw + gap);
                    var side = (i + 0.5) / k - 0.5;
                    var sway = Math.Sin(env.Ltb * 1.2 + i * 0.8 + JLayouts.D(p, "ph")) * 1.1 * JLayouts.D(p, "wind")
                               + part * Math.Sign(side == 0 ? 0.001 : side) * 26 * (0.4 + Math.Abs(side) * 1.4);
                    var hh = nh * drop;
                    ctx.Save();
                    ctx.Translate((float)(px + pw / 2), (float)rodY);
                    ctx.RotateDegrees((float)sway);
                    ctx.Save();
                    ctx.ClipRect(new SKRect((float)(-pw / 2 - 2), (float)-lw, (float)(-pw / 2 - 2 + pw + 4), (float)(-lw + hh + lw)));
                    env.Rect(-pw / 2, -lw * 0.2, pw, nh, clothC, fadeO, false);
                    if (env.Pass == "main")
                    {
                        env.Rect(-pw / 2, -lw * 0.2, pw, hem, J.Mix(clothC, Darkest(sc), 0.25), fadeO, false);
                        using var g = new SKPaint
                        {
                            IsAntialias = true,
                            BlendMode = env.Blend,
                            Color = Env.SkColor("#FFFFFF", fadeO),
                            Shader = SKShader.CreateLinearGradient(
                                new SKPoint((float)(-pw / 2), 0), new SKPoint((float)(pw / 2), 0),
                                new[] { Env.SkColor(Darkest(sc), 0.18), Env.SkColor(Darkest(sc), 0), Env.SkColor(Darkest(sc), 0.12) },
                                new[] { 0f, 0.5f, 1f }, SKShaderTileMode.Clamp),
                        };
                        ctx.DrawRect(new SKRect((float)(-pw / 2), (float)hem, (float)(pw / 2), (float)nh), g);
                    }
                    if (JLayouts.Truthy(p["mon"]) && i == (int)Math.Floor(k / 2.0) - (k % 2 != 0 ? 0 : 1) && k > 1)
                    {
                        var mx0 = k % 2 != 0 ? 0.0 : pw / 2 + gap / 2;
                        var my0 = hem + pw * 0.2;
                        var mr = pw * 0.1;
                        env.Circle(mx0, my0, mr, tc, null, 0, fadeO, false);
                        env.Circle(mx0, my0, mr * 0.62, clothC, null, 0, fadeO, false);
                        env.Circle(mx0, my0, mr * 0.3, tc, null, 0, fadeO, false);
                    }
                    var t = units[i];
                    if (!string.IsNullOrEmpty(t))
                    {
                        var g2 = JLayouts.GlyphCount(t);
                        var r = JLayouts.MainDraw(env, new JObject
                        {
                            ["text"] = t,
                            ["font"] = JLayouts.S(p, "font"),
                            ["size"] = size,
                            ["x"] = 0,
                            ["y"] = hem + (nh - hem) * 0.5 - g2 * size * 0.52 + (JLayouts.Truthy(p["mon"]) ? pw * 0.12 : 0),
                            ["vertical"] = true,
                            ["align"] = "left",
                            ["track"] = 0.04,
                            ["color"] = tc,
                            ["noHold"] = PlateHold(env),
                            ["mi"] = MiAt(env, t1 + 0.15),
                        });
                        if (r != null) bb = JLayouts.UnionBb(bb, Box(px, rodY, px + pw, rodY + nh));
                    }
                    ctx.Restore();
                    ctx.Restore();
                }
                env.Body = bb;
            },
        });
    }

    private static void RegisterTanzaku()
    {
        RegC("tanzaku", new LayoutDef
        {
            Name = "短冊",
            Tags = new[] { "emotional", "calm", "pop" },
            W = 0.7,
            Treat = "safe",
            Portrait = 1.2,
            Fits = n => n >= 1 && n <= 16,
            EnterBias = new JObject { ["blur"] = 1.3, ["drop"] = 1.3, ["cut"] = 1.2, ["slice"] = 0.4, ["stretch"] = 0.4 },
            Plan = (rng, cut, st) =>
            {
                var n = cut.N;
                var k = n <= 6 ? 1 : n <= 11 ? 2 : 3;
                return new JObject
                {
                    ["font"] = rng.Chance(0.4) ? "brush" : rng.Pick(FontsOf(st, "serif", "display")),
                    ["chunks"] = new JArray(HasLatin(cut.Text) ? SplitK(cut.Text, k) : SplitK(Strip(cut.Text), k, k)),
                    ["extra"] = rng.Int(2, 3),
                    ["cols"] = new JArray(Enumerable.Range(0, 6).Select(_ => rng.Int(0, 4))),
                    ["ph"] = rng.Range(0, 6),
                    ["dir"] = rng.Pick(new[] { 1, -1 }),
                };
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var ctx = env.Ctx;
                var p = env.P;
                var u = U(env);
                var port = IsPort(env);
                var outK = Tout(env);
                var C = Card(sc);
                var chunks = (p["chunks"] as JArray)?.Select(t => (string?)t).Where(s => !string.IsNullOrEmpty(s)).Select(VText).ToList();
                if (chunks == null || chunks.Count == 0) chunks = new List<string> { VText(env.Cut.Text) };
                var k = chunks.Count;
                var pal = new[]
                {
                    PlateCol(sc, new[] { sc.Str("accent"), sc.Str("ink") }), C.Fill,
                    PlateCol(sc, new[] { sc.Str("accent2"), sc.Str("sub"), sc.Str("accent") }),
                    J.Mix(sc.Str("bg")!, sc.Str("accent")!, 0.55), PlateCol(sc, new[] { sc.Str("ink"), sc.Str("fg") })
                };
                var dir = JLayouts.D(p, "dir");
                double By(double x) => H * 0.1 + x / W * H * 0.06 * dir + Math.Sin(x / W * 3) * H * 0.012;
                var be = Tin(env, 0, 0.5, J.E.InOutCubic) * outK;
                var bpts = new List<double[]>();
                for (var i = 0; i <= 24; i++)
                {
                    var x = J.Lerp(-20, W + 20, i / 24.0);
                    bpts.Add(new[] { x, By(x) });
                }
                var stemC = J.Mix(PlateCol(sc, new[] { sc.Str("accent2"), sc.Str("sub") }), sc.Str("sub")!, 0.3);
                env.PolyPartial(bpts.ToArray(), be, stemC, Math.Max(5, u * 0.01), 1, false);
                for (var i = 1; i < 8; i++)
                {
                    var nx = W * i / 8;
                    if (nx / W < be)
                        env.Line(new[] { new[] { nx, By(nx) - u * 0.009 }, new[] { nx, By(nx) + u * 0.009 } },
                            J.Mix(stemC, Darkest(sc), 0.4), Math.Max(3, u * 0.005), outK, false);
                }
                for (var i = 0; i < 9; i++)
                {
                    var lx = W * (0.05 + i * 0.115);
                    var la = J.Clamp(be * 9 - i) * outK;
                    if (la <= 0) continue;
                    var ang = (i % 2 != 0 ? 1 : -1) * 35 + Math.Sin(env.Ltb * 1.4 + i) * 6;
                    var L = u * 0.07;
                    var wdt = u * 0.018;
                    var ly = By(lx);
                    var ca = Math.Cos(ang * J.DEG);
                    var sa = Math.Sin(ang * J.DEG);
                    var pts = new[]
                    {
                        new[] { lx, ly },
                        new[] { lx + ca * L * 0.5 - sa * wdt, ly + sa * L * 0.5 + ca * wdt },
                        new[] { lx + ca * L, ly + sa * L },
                        new[] { lx + ca * L * 0.5 + sa * wdt, ly + sa * L * 0.5 - ca * wdt }
                    };
                    env.Blob(pts, stemC, la * 0.85, false);
                }
                var nS = k + (port ? 1 : JLayouts.I(p, "extra"));
                var mid = (int)Math.Floor((nS - k) / 2.0);
                var maxG = chunks.Max(c => JLayouts.GlyphCount(c));
                var sw = Math.Min(W * (port ? 0.9 : 0.62) / (nS * 1.3), u * (port ? 0.2 : 0.16));
                var shMax = H * (port ? 0.7 : 0.72);
                var size = Math.Min(sw * 0.64, (shMax - sw * 0.4) / (Math.Max(maxG, 2.5) * 1.08 + 1.6));
                var cols = (JArray)p["cols"]!;
                BBox? bb = null;
                for (var i = 0; i < nS; i++)
                {
                    var isL = i >= mid && i < mid + k;
                    var t = isL ? chunks[i - mid] : "";
                    var x = W / 2 + (i - (nS - 1) / 2.0) * sw * 1.3 * (port ? 1 : 1.1);
                    var top = By(x) + u * 0.012;
                    var sh = isL ? Math.Max(sw * 2.6, JLayouts.GlyphCount(t) * size * 1.08 + size * 1.6) : sw * (2.4 + i % 3 * 0.5);
                    var @string = u * (0.03 + i % 3 * 0.022);
                    var t1 = 0.05 + Math.Abs(i - (nS - 1) / 2.0) * 0.07;
                    var e = J.Clamp((env.Lt - t1) / 0.5);
                    if (e <= 0) continue;
                    var drop = (1 - J.E.OutBack(e, 1.6)) * H * 0.25;
                    var sway = Math.Sin(env.Ltb * 1.3 + i * 0.9 + JLayouts.D(p, "ph")) * 2.5 + J.E.InCubic(env.POut) * (i % 2 != 0 ? 1 : -1) * 20;
                    var a = J.Clamp(e * 3) * (1 - J.Clamp((env.POut - 0.4) / 0.6));
                    ctx.Save();
                    ctx.Translate((float)x, (float)top);
                    ctx.RotateDegrees((float)sway);
                    ctx.Translate(0, (float)-drop);
                    env.Line(new[] { new[] { 0.0, 0.0 }, new[] { 0.0, @string } }, sc.Str("sub"), Math.Max(1, u * 0.0016), a, false);
                    var ci = (int)(double)cols[i % 6]!;
                    var col = isL ? pal[ci % 3 == 1 ? 1 : ci % 3 == 2 ? 2 : 0] : pal[(ci + 3) % 5];
                    ShadowR(env, -sw / 2, @string, sw, sh, 0, a, u * 0.008);
                    env.Rect(-sw / 2, @string, sw, sh, col, a, false);
                    if (J.Contrast(col, sc.Str("bg")!) < 1.3)
                        env.Line(new[]
                        {
                            new[] { -sw / 2, @string }, new[] { sw / 2, @string }, new[] { sw / 2, @string + sh },
                            new[] { -sw / 2, @string + sh }, new[] { -sw / 2, @string }
                        }, J.Mix(sc.Str("fg")!, col, 0.5), 1.2, a, false);
                    env.Circle(0, @string + sw * 0.18, sw * 0.05, sc.Str("bg"), null, 0, a, false);
                    if (isL)
                    {
                        var r = JLayouts.MainDraw(env, new JObject
                        {
                            ["text"] = t,
                            ["font"] = JLayouts.S(p, "font"),
                            ["size"] = size,
                            ["x"] = 0,
                            ["y"] = @string + size * 0.9,
                            ["vertical"] = true,
                            ["align"] = "left",
                            ["track"] = 0.06,
                            ["color"] = OnCol(sc, col),
                            ["noHold"] = PlateHold(env),
                            ["mi"] = MiAt(env, t1 + 0.25),
                        });
                        bb = JLayouts.UnionBb(bb, Box(x - sw / 2, top + @string, x + sw / 2, top + @string + sh));
                    }
                    ctx.Restore();
                }
                env.Body = bb;
            },
        });
    }

        private static readonly string[] Kuji = { "大吉", "吉", "中吉", "小吉", "末吉", "大吉" };

    private static readonly string[] KujiCat = { "願望", "待人", "失物", "旅行", "商売", "学問", "恋愛", "健康" };

        private static JArray KujiCats(Rng rng)
    {
        var pool = KujiCat.ToList();
        for (var i = pool.Count - 1; i > 0; i--)
        {
            var j = rng.Int(0, i);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }
        return new JArray(pool.Take(4));
    }

    private static void RegisterOmikuji()
    {
        RegC("omikuji", new LayoutDef
        {
            Name = "おみくじ",
            Tags = new[] { "emotional", "calm", "editorial" },
            W = 0.7,
            Treat = "safe",
            Portrait = 1.1,
            Fits = n => n >= 1 && n <= 16,
            EnterBias = new JObject { ["wipe"] = 1.5, ["blur"] = 1.3, ["type"] = 1.2 },
            Plan = (rng, cut, st) =>
            {
                return new JObject
                {
                    ["font"] = rng.Chance(0.4) ? "brush" : rng.Pick(FontsOf(st, "serif")),
                    ["rank"] = rng.Pick(Kuji),
                    ["cats"] = KujiCats(rng),
                };
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var ctx = env.Ctx;
                var p = env.P;
                var u = U(env);
                var port = IsPort(env);
                var outK = Tout(env);
                var ls = SmallSize(env);
                var C = Card(sc);
                var red = PlateCol(sc, new[] { sc.Str("accent"), sc.Str("accent2"), C.Text }, C.Fill, 1.7);
                var t0 = VText(env.Cut.Text);
                var sw = port ? W * 0.84 : Math.Min(W * 0.88, H * 1.7);
                var sh = port ? Math.Min(H * 0.82, sw * 1.7) : Math.Min(H * 0.66, sw * 0.46);
                var x0 = W / 2 - sw / 2;
                var y0 = H / 2 - sh / 2;
                var op = J.E.InOutCubic(J.Clamp((LtU(env) - 0.03) / 0.55)) * (1 - J.E.InOutCubic(J.Clamp((POutU(env) - 0.1) / 0.8)));
                var a = Tin(env, 0, 0.2, J.E.OutCubic) * (1 - J.Clamp((env.POut - 0.75) / 0.25));
                if (a <= 0.01)
                {
                    env.Body = null;
                    return;
                }
                var vis = Math.Max(0.12, op);
                var cx0 = port ? x0 : x0 + sw * (1 - vis);
                var cy0 = y0;
                var cw = port ? sw : sw * vis;
                var ch = port ? sh * vis : sh;
                ShadowR(env, cx0, cy0, cw, ch, 0, a, u * 0.012);
                env.Rect(cx0, cy0, cw, ch, C.Fill, a, false);
                if (C.Edge)
                    env.Line(new[] { new[] { cx0, cy0 }, new[] { cx0 + cw, cy0 }, new[] { cx0 + cw, cy0 + ch }, new[] { cx0, cy0 + ch }, new[] { cx0, cy0 } },
                        C.Line, 1.2, a, false);
                ctx.Save();
                ctx.ClipRect(new SKRect((float)cx0, (float)cy0, (float)(cx0 + cw), (float)(cy0 + ch)));
                var m = Math.Min(sw, sh) * 0.05;
                var lw = Math.Max(1.5, u * 0.002);
                env.Line(new[]
                {
                    new[] { x0 + m, y0 + m }, new[] { x0 + sw - m, y0 + m }, new[] { x0 + sw - m, y0 + sh - m },
                    new[] { x0 + m, y0 + sh - m }, new[] { x0 + m, y0 + m }
                }, red, lw * 1.8, a, false);
                env.Line(new[]
                {
                    new[] { x0 + m * 1.35, y0 + m * 1.35 }, new[] { x0 + sw - m * 1.35, y0 + m * 1.35 },
                    new[] { x0 + sw - m * 1.35, y0 + sh - m * 1.35 }, new[] { x0 + m * 1.35, y0 + sh - m * 1.35 },
                    new[] { x0 + m * 1.35, y0 + m * 1.35 }
                }, red, lw * 0.7, a, false);
                var ix0 = x0 + m * 2;
                var iy0 = y0 + m * 2;
                var iw = sw - m * 4;
                var ih = sh - m * 4;
                var no = "第" + KanjiNum(LineN(env)) + "番";
                BBox? bb;
                var cats = ((JArray)p["cats"]!).Select(t => (string)t!).ToList();
                if (!port)
                {
                    var colW = ih * 0.14;
                    var hx = ix0 + iw - colW * 0.6;
                    env.DrawItem(new JObject
                    {
                        ["text"] = no,
                        ["font"] = SerifF(env),
                        ["size"] = Math.Min(colW * 0.6, ih * 0.8 / no.Length),
                        ["vertical"] = true,
                        ["align"] = "left",
                        ["x"] = hx,
                        ["y"] = iy0 + ih * 0.06,
                        ["color"] = C.Text,
                        ["alpha"] = a,
                        ["ghost"] = false,
                    });
                    var rw = colW * 1.2;
                    var rx = hx - colW * 0.5 - rw - m * 0.4;
                    env.Rect(rx, iy0 + ih * 0.04, rw, ih * 0.92, red, a, false);
                    var rt = JLayouts.S(p, "rank")!;
                    env.DrawItem(new JObject
                    {
                        ["text"] = rt,
                        ["font"] = SerifF(env),
                        ["size"] = Math.Min(rw * 0.7, ih * 0.8 / rt.Length),
                        ["vertical"] = true,
                        ["x"] = rx + rw / 2,
                        ["y"] = iy0 + ih / 2,
                        ["track"] = 0.2,
                        ["color"] = OnCol(sc, red),
                        ["alpha"] = a,
                        ["ghost"] = false,
                    });
                    var px1 = rx - m;
                    var catW = iw * 0.3;
                    var pw = px1 - (ix0 + catW) - m;
                    var fb = FitBlock(t0, JLayouts.S(p, "font")!, pw, ih * 0.9,
                        new JObject { ["vertical"] = true, ["lead"] = 1.35, ["track"] = 0.06 }, 3);
                    var size = Math.Min(fb.Size, ih * 0.3);
                    var mm = Meas(fb.Text, JLayouts.S(p, "font")!, size, new JObject { ["vertical"] = true, ["lead"] = 1.35, ["track"] = 0.06 });
                    bb = JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = fb.Text,
                        ["font"] = JLayouts.S(p, "font"),
                        ["size"] = size,
                        ["x"] = px1 - pw / 2,
                        ["y"] = iy0 + (ih - mm.H) / 2,
                        ["vertical"] = true,
                        ["align"] = "left",
                        ["lead"] = 1.35,
                        ["track"] = 0.06,
                        ["color"] = C.Text,
                        ["noHold"] = PlateHold(env),
                        ["mi"] = MiAt(env, 0.22),
                    });
                    env.Line(new[] { new[] { ix0 + catW + m * 0.5, iy0 }, new[] { ix0 + catW + m * 0.5, iy0 + ih } }, red, lw, a, false);
                    for (var i = 0; i < cats.Count; i++)
                    {
                        var c = cats[i];
                        var cx = ix0 + catW - (i % 2 + 0.5) * catW / 2;
                        var cy = iy0 + (int)Math.Floor(i / 2.0) * ih / 2;
                        env.DrawItem(new JObject
                        {
                            ["text"] = c,
                            ["font"] = SerifF(env),
                            ["size"] = ls * 1.05,
                            ["vertical"] = true,
                            ["align"] = "left",
                            ["x"] = cx + catW * 0.12,
                            ["y"] = cy + ls * 0.8,
                            ["color"] = red,
                            ["alpha"] = a,
                            ["ghost"] = false,
                        });
                        Greek(env, cx - catW * 0.24, cy + ls * 0.8, catW * 0.28, ih / 2 - ls * 2, ls * 0.9, C.Text, a * 0.35, i + 7, true);
                        if (i % 2 == 0)
                            env.Line(new[] { new[] { ix0, cy + ih / 2 }, new[] { ix0 + catW, cy + ih / 2 } }, red, lw * 0.6, a * (i == 0 ? 1 : 0), false);
                    }
                }
                else
                {
                    var hh = ih * 0.1;
                    env.DrawItem(new JObject
                    {
                        ["text"] = no,
                        ["font"] = SerifF(env),
                        ["size"] = hh * 0.5,
                        ["track"] = 0.3,
                        ["x"] = W / 2,
                        ["y"] = iy0 + hh * 0.45,
                        ["color"] = C.Text,
                        ["alpha"] = a,
                        ["ghost"] = false,
                    });
                    var rw = iw * 0.4;
                    var rh = hh * 1.2;
                    env.Rect(W / 2 - rw / 2, iy0 + hh, rw, rh, red, a, false);
                    env.DrawItem(new JObject
                    {
                        ["text"] = JLayouts.S(p, "rank"),
                        ["font"] = SerifF(env),
                        ["size"] = rh * 0.62,
                        ["track"] = 0.3,
                        ["x"] = W / 2,
                        ["y"] = iy0 + hh + rh / 2,
                        ["color"] = OnCol(sc, red),
                        ["alpha"] = a,
                        ["ghost"] = false,
                    });
                    var catH = ih * 0.2;
                    var pt = iy0 + hh + rh + m;
                    var pb = iy0 + ih - catH - m;
                    var fb = FitBlock(t0, JLayouts.S(p, "font")!, iw * 0.84, pb - pt,
                        new JObject { ["vertical"] = true, ["lead"] = 1.35, ["track"] = 0.06 }, 3);
                    var size = Math.Min(fb.Size, iw * 0.3);
                    var mm = Meas(fb.Text, JLayouts.S(p, "font")!, size, new JObject { ["vertical"] = true, ["lead"] = 1.35, ["track"] = 0.06 });
                    bb = JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = fb.Text,
                        ["font"] = JLayouts.S(p, "font"),
                        ["size"] = size,
                        ["x"] = W / 2,
                        ["y"] = pt + (pb - pt - mm.H) / 2,
                        ["vertical"] = true,
                        ["align"] = "left",
                        ["lead"] = 1.35,
                        ["track"] = 0.06,
                        ["color"] = C.Text,
                        ["noHold"] = PlateHold(env),
                        ["mi"] = MiAt(env, 0.25),
                    });
                    env.Line(new[] { new[] { ix0, pb + m * 0.5 }, new[] { ix0 + iw, pb + m * 0.5 } }, red, lw, a, false);
                    for (var i = 0; i < cats.Count; i++)
                    {
                        var c = cats[i];
                        var cw2 = iw / 4;
                        var cx = ix0 + iw - (i + 0.5) * cw2;
                        env.DrawItem(new JObject
                        {
                            ["text"] = c,
                            ["font"] = SerifF(env),
                            ["size"] = ls,
                            ["vertical"] = true,
                            ["align"] = "left",
                            ["x"] = cx + cw2 * 0.22,
                            ["y"] = pb + m,
                            ["color"] = red,
                            ["alpha"] = a,
                            ["ghost"] = false,
                        });
                        Greek(env, cx - cw2 * 0.4, pb + m, cw2 * 0.5, catH - m, ls * 0.8, C.Text, a * 0.35, i + 7, true);
                    }
                }
                ctx.Restore();
                var cr = J.Mix(C.Fill, C.Text, 0.12);
                for (var i = 1; i < 4; i++)
                {
                    if (port)
                    {
                        var y = y0 + sh * i / 4;
                        if (y < cy0 + ch) env.Line(new[] { new[] { x0, y }, new[] { x0 + sw, y } }, cr, 1, a * 0.8, false);
                    }
                    else
                    {
                        var x = x0 + sw * i / 4;
                        if (x > cx0) env.Line(new[] { new[] { x, y0 }, new[] { x, y0 + sh } }, cr, 1, a * 0.8, false);
                    }
                }
                env.Body = bb;
            },
        });
    }

    private static void RegisterKakejiku()
    {
        RegC("kakejiku", new LayoutDef
        {
            Name = "掛け軸",
            Tags = new[] { "calm", "emotional", "editorial" },
            W = 0.7,
            Treat = "safe",
            Portrait = 1.3,
            Fits = n => n >= 1 && n <= 14,
            EnterBias = new JObject { ["blur"] = 1.4, ["wipe"] = 1.3, ["cut"] = 1.2, ["slice"] = 0.4, ["stretch"] = 0.4 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Chance(0.5) ? "brush" : rng.Pick(FontsOf(st, "serif")),
                ["variant"] = PortOf(cut) ? "kake" : rng.Pick(new[] { "kake", "kake", "yoko" }),
                ["mount"] = rng.Pick(new[] { "accent", "ink", "sub" }),
                ["seal"] = rng.Chance(0.7),
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var ctx = env.Ctx;
                var p = env.P;
                var u = U(env);
                var port = IsPort(env);
                var outK = Tout(env);
                var C = Card(sc);
                var t0 = VText(env.Cut.Text);
                var mountKind = JLayouts.S(p, "mount");
                var mountC = J.Mix(PlateCol(sc, mountKind == "accent"
                    ? new[] { sc.Str("accent"), sc.Str("ink") }
                    : mountKind == "ink" ? new[] { sc.Str("ink"), sc.Str("accent") } : new[] { sc.Str("sub"), sc.Str("ink") }), Darkest(sc), 0.3);
                var goldC = J.Mix(PlateCol(sc, new[] { sc.Str("accent"), sc.Str("accent2"), sc.Str("sub") }, mountC, 1.5), mountC, 0.25);
                var rodC = J.Mix(Darkest(sc), sc.Str("sub")!, 0.35);
                var seal = PlateCol(sc, new[] { sc.Str("accent"), sc.Str("accent2") }, C.Fill, 1.6);
                var open = J.E.InOutCubic(J.Clamp((LtU(env) - 0.08) / 0.7)) * (1 - J.E.InOutCubic(J.Clamp(POutU(env) / 0.85)));
                var a = Tin(env, 0, 0.2, J.E.OutCubic) * (1 - J.Clamp((env.POut - 0.8) / 0.2));
                if (a <= 0.01)
                {
                    env.Body = null;
                    return;
                }
                BBox? bb = null;
                if (JLayouts.S(p, "variant") == "yoko" && !port)
                {
                    var sw = W * 0.86;
                    var sh = Math.Min(H * 0.56, sw * 0.4);
                    var x1 = W / 2 + sw / 2;
                    var y0 = H / 2 - sh / 2;
                    var xr = x1 - sw * open;
                    var rodW = sh * 0.07;
                    ctx.Save();
                    ctx.ClipRect(new SKRect((float)xr, (float)(y0 - sh), (float)(xr + (x1 - xr + rodW)), (float)(y0 - sh + sh * 3)));
                    env.Rect(x1 - sw, y0, sw, sh, mountC, a, false);
                    var px0 = x1 - sw + sw * 0.08;
                    var pw = sw * 0.84;
                    var py0 = y0 + sh * 0.12;
                    var ph2 = sh * 0.76;
                    env.Rect(px0 - sh * 0.02, py0 - sh * 0.02, pw + sh * 0.04, ph2 + sh * 0.04, goldC, a, false);
                    env.Rect(px0, py0, pw, ph2, C.Fill, a, false);
                    var fb = FitBlock(t0, JLayouts.S(p, "font")!, pw * 0.8, ph2 * 0.86,
                        new JObject { ["vertical"] = true, ["lead"] = 1.4, ["track"] = 0.06 }, 4);
                    var size = Math.Min(fb.Size, ph2 * 0.4);
                    var mm = Meas(fb.Text, JLayouts.S(p, "font")!, size, new JObject { ["vertical"] = true, ["lead"] = 1.4, ["track"] = 0.06 });
                    bb = JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = fb.Text,
                        ["font"] = JLayouts.S(p, "font"),
                        ["size"] = size,
                        ["x"] = px0 + pw / 2 + mm.W * 0.1,
                        ["y"] = py0 + (ph2 - mm.H) / 2,
                        ["vertical"] = true,
                        ["align"] = "left",
                        ["lead"] = 1.4,
                        ["track"] = 0.06,
                        ["color"] = C.Text,
                        ["noHold"] = PlateHold(env),
                        ["mi"] = MiAt(env, 0.25),
                    });
                    if (JLayouts.Truthy(p["seal"]))
                        env.Rect(px0 + pw / 2 - mm.W * 0.5 - size * 0.7, py0 + (ph2 + mm.H) / 2 - size * 0.55, size * 0.45, size * 0.45,
                            seal, a * J.Clamp((env.Lt - 0.6) * 4), false);
                    ctx.Restore();
                    env.RRect(x1, y0 - rodW * 0.8, rodW, sh + rodW * 1.6, rodW * 0.4, rodC, a, false);
                    env.RRect(xr - rodW, y0 - rodW * 1.4, rodW * 1.3, sh + rodW * 2.8, rodW * 0.5, rodC, a, false);
                    env.Body = bb;
                    return;
                }
                var sw2 = port ? W * 0.6 : Math.Min(W * 0.32, H * 0.42);
                var sh2 = H * (port ? 0.8 : 0.86);
                var cx = W / 2;
                var yy0 = H / 2 - sh2 / 2 + H * 0.03;
                var rodH = sw2 * 0.06;
                env.Line(new[] { new[] { cx - sw2 * 0.28, yy0 }, new[] { cx, yy0 - H * 0.06 }, new[] { cx + sw2 * 0.28, yy0 } },
                    sc.Str("sub"), Math.Max(1.5, u * 0.0025), a, false);
                env.Circle(cx, yy0 - H * 0.06, u * 0.006, sc.Str("fg"), null, 0, a, false);
                var yb = yy0 + sh2 * Math.Max(0.03, open);
                ctx.Save();
                ctx.ClipRect(new SKRect((float)(cx - sw2), (float)(yy0 - 2), (float)(cx + sw2), (float)yb));
                env.Rect(cx - sw2 / 2, yy0, sw2, sh2, mountC, a, false);
                var pt = yy0 + sh2 * 0.2;
                var pb = yy0 + sh2 * 0.84;
                var pw2 = sw2 * 0.78;
                env.Rect(cx - pw2 / 2 - sw2 * 0.02, pt - sh2 * 0.03, pw2 + sw2 * 0.04, pb - pt + sh2 * 0.06, goldC, a, false);
                env.Rect(cx - pw2 / 2, pt, pw2, pb - pt, C.Fill, a, false);
                var fb2 = FitBlock(t0, JLayouts.S(p, "font")!, pw2 * 0.82, (pb - pt) * 0.86,
                    new JObject { ["vertical"] = true, ["lead"] = 1.35, ["track"] = 0.08 }, 2);
                var size2 = Math.Min(fb2.Size, pw2 * 0.5);
                var mm2 = Meas(fb2.Text, JLayouts.S(p, "font")!, size2, new JObject { ["vertical"] = true, ["lead"] = 1.35, ["track"] = 0.08 });
                var ty = pt + (pb - pt - mm2.H) * 0.4;
                bb = JLayouts.MainDraw(env, new JObject
                {
                    ["text"] = fb2.Text,
                    ["font"] = JLayouts.S(p, "font"),
                    ["size"] = size2,
                    ["x"] = cx,
                    ["y"] = ty,
                    ["vertical"] = true,
                    ["align"] = "left",
                    ["lead"] = 1.35,
                    ["track"] = 0.08,
                    ["color"] = C.Text,
                    ["noHold"] = PlateHold(env),
                    ["mi"] = MiAt(env, 0.2),
                });
                if (JLayouts.Truthy(p["seal"]))
                {
                    var ss = Math.Min(Math.Max(size2 * 0.42, pw2 * 0.1), pw2 * 0.16);
                    var sxx = Math.Max(cx - pw2 / 2 + ss * 0.3, cx - mm2.W / 2 - ss * 1.1);
                    var syy = Math.Min(pb - ss * 1.3, ty + mm2.H - ss * 0.6);
                    var sa = a * J.Clamp((env.Lt - 0.7) * 4);
                    env.Rect(sxx, syy, ss, ss, seal, sa, false);
                    env.Rect(sxx + ss * 0.18, syy + ss * 0.18, ss * 0.64, ss * 0.64, J.Mix(seal, C.Fill, 0.35), sa * 0.5, false);
                }
                ctx.Restore();
                env.RRect(cx - sw2 * 0.53, yy0 - rodH * 0.5, sw2 * 1.06, rodH, rodH * 0.4, rodC, a, false);
                env.RRect(cx - sw2 * 0.55, yb - rodH * 0.3, sw2 * 1.1, rodH * 1.3, rodH * 0.5, rodC, a, false);
                env.RRect(cx - sw2 * 0.62, yb - rodH * 0.45, sw2 * 0.08, rodH * 1.6, rodH * 0.3, goldC, a, false);
                env.RRect(cx + sw2 * 0.54, yb - rodH * 0.45, sw2 * 0.08, rodH * 1.6, rodH * 0.3, goldC, a, false);
                env.Body = bb != null ? Box(cx - sw2 / 2, yy0, cx + sw2 / 2, yy0 + sh2) : null;
            },
        });
    }

        private static void ShojiPanel(Env env, double x, double y, double w, double h, string? paperC, string woodC,
        double a, int cols, int rows, double? glow = null)
    {
        var ctx = env.Ctx;
        var fw = Math.Max(4, w * 0.045);
        var bw = Math.Max(2, w * 0.012);
        if (paperC != null)
        {
            env.Rect(x, y, w, h, paperC, a, false);
            if (glow != null && glow < 1) env.Rect(x, y, w, h, env.Sc!.Str("bg"), a * (1 - glow.Value) * 0.6, false);
        }
        env.Rect(x, y, w, fw, woodC, a, false);
        env.Rect(x, y + h - fw * 1.6, w, fw * 1.6, woodC, a, false);
        env.Rect(x, y, fw, h, woodC, a, false);
        env.Rect(x + w - fw, y, fw, h, woodC, a, false);
        if (env.Pass != "main" || a <= 0.01) return;
        using var paint = new SKPaint { IsAntialias = true, Color = Env.SkColor(woodC, a), BlendMode = env.Blend };
        for (var c = 1; c < cols; c++)
            ctx.DrawRect(new SKRect((float)(x + fw + (w - fw * 2) * c / cols - bw / 2), (float)y,
                (float)(x + fw + (w - fw * 2) * c / cols + bw / 2), (float)(y + h)), paint);
        for (var r = 1; r < rows; r++)
            ctx.DrawRect(new SKRect((float)x, (float)(y + fw + (h - fw * 2.6) * r / rows - bw / 2),
                (float)(x + w), (float)(y + fw + (h - fw * 2.6) * r / rows + bw / 2)), paint);
    }

    private static void RegisterShoji()
    {
        RegC("shoji", new LayoutDef
        {
            Name = "障子",
            Tags = new[] { "calm", "emotional", "graphic" },
            W = 0.7,
            Busy = true,
            Treat = "safe",
            Fits = n => n >= 1 && n <= 16,
            EnterBias = new JObject { ["blur"] = 1.6, ["cut"] = 1.2, ["wipe"] = 0.6, ["slice"] = 0.4 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "serif", "display")),
                ["variant"] = rng.Pick(new[] { "shadow", "open", "shadow" }),
                ["rows"] = rng.Int(4, 6),
                ["cols"] = rng.Int(2, 3),
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var ctx = env.Ctx;
                var p = env.P;
                var u = U(env);
                var port = IsPort(env);
                var C = Card(sc);
                var paperC = J.Lum(sc.Str("bg")!) < 0.5 ? J.Mix(C.Fill, sc.Str("accent")!, 0.08) : J.Mix(C.Fill, sc.Str("dim")!, 0.2);
                var woodC = J.Mix(Darkest(sc), PlateCol(sc, new[] { sc.Str("accent"), sc.Str("sub") }), 0.35);
                var nP = port ? 2 : 4;
                var pw = W / nP;
                var ph = H;
                var t0 = env.Cut.Text.Trim();
                var glow = 0.92 + 0.08 * J.Noise1(env.Ltb * 0.8, unchecked((int)env.Cut.Seed));
                BBox? bb = null;
                if (JLayouts.S(p, "variant") == "shadow")
                {
                    var a = Tin(env, 0, 0.3, J.E.OutCubic) * (1 - J.Clamp((env.POut - 0.5) / 0.5));
                    if (a <= 0.01)
                    {
                        env.Body = null;
                        return;
                    }
                    env.Rect(0, 0, W, H, paperC, a * glow, false);
                    if (env.Pass == "main")
                    {
                        using var g = new SKPaint
                        {
                            IsAntialias = true,
                            BlendMode = env.Blend,
                            Color = Env.SkColor("#FFFFFF", a),
                            Shader = SKShader.CreateRadialGradient(
                                new SKPoint((float)(W / 2), (float)(H / 2)), (float)(Math.Sqrt(W * W + H * H) * 0.6),
                                new[] { Env.SkColor(Lightest(sc), 0.25), Env.SkColor(Darkest(sc), 0.25) },
                                new[] { 0f, 1f }, SKShaderTileMode.Clamp),
                        };
                        ctx.DrawRect(new SKRect(0, 0, (float)W, (float)H), g);
                    }
                    var fb = FitBlock(t0, JLayouts.S(p, "font")!, W * 0.8, H * 0.5, new JObject { ["lead"] = 1.15, ["track"] = 0.04 }, port ? 4 : 2);
                    var size = Math.Min(fb.Size, u * 0.26);
                    var near = J.E.OutCubic(J.Clamp(env.Lt / 0.9));
                    var shade = J.Mix(paperC, Darkest(sc), 0.86);
                    bb = JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = fb.Text,
                        ["font"] = JLayouts.S(p, "font"),
                        ["size"] = size,
                        ["x"] = W / 2 + (1 - near) * W * 0.03,
                        ["y"] = H / 2,
                        ["lead"] = 1.15,
                        ["track"] = 0.04,
                        ["color"] = shade,
                        ["blur"] = env.AllowFilter ? J.Lerp(size * 0.08, size * 0.012, near) : 0,
                        ["sx"] = J.Lerp(1.06, 1, near),
                        ["sy"] = J.Lerp(1.06, 1, near),
                    });
                    for (var i = 0; i < nP; i++) ShojiPanel(env, i * pw, 0, pw, ph, null, woodC, a, JLayouts.I(p, "cols"), JLayouts.I(p, "rows"));
                    env.Body = bb;
                    return;
                }
                var fb2 = FitBlock(t0, JLayouts.S(p, "font")!, W * (port ? 0.6 : 0.5), H * 0.46, new JObject { ["lead"] = 1.15, ["track"] = 0.04 }, port ? 4 : 3);
                var size2 = Math.Min(fb2.Size, u * 0.24);
                var mm = Meas(fb2.Text, JLayouts.S(p, "font")!, size2, new JObject { ["lead"] = 1.15, ["track"] = 0.04 });
                bb = JLayouts.MainDraw(env, new JObject
                {
                    ["text"] = fb2.Text,
                    ["font"] = JLayouts.S(p, "font"),
                    ["size"] = size2,
                    ["x"] = W / 2,
                    ["y"] = H / 2,
                    ["lead"] = 1.15,
                    ["track"] = 0.04,
                    ["color"] = sc.Str("fg"),
                    ["mi"] = MiAt(env, 0.12),
                });
                var gapW = Math.Min(W * (port ? 0.76 : 0.9), mm.W + size2 * 1.4);
                var op = J.E.InOutCubic(J.Clamp((env.Lt - 0.05) / 0.55)) * (1 - J.E.InOutCubic(env.POut));
                var half = nP / 2.0;
                for (var i = 0; i < nP; i++)
                {
                    var left = i < half;
                    var dx = (left ? -1 : 1) * (gapW / 2) * op;
                    ShojiPanel(env, i * pw + dx, 0, pw, ph, paperC, woodC, 1, JLayouts.I(p, "cols"), JLayouts.I(p, "rows"), glow);
                }
                env.Body = bb;
            },
        });
    }

    private static void RegisterClapper()
    {
        RegC("clapper", new LayoutDef
        {
            Name = "カチンコ",
            Tags = new[] { "pop", "graphic", "editorial" },
            W = 0.6,
            Treat = "safe",
            Fits = n => n >= 1 && n <= 16,
            EnterBias = new JObject { ["cut"] = 1.8, ["pop"] = 1.2, ["blur"] = 0.5 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Chance(0.5) ? "klee" : rng.Pick(FontsOf(st, "display", "body")),
                ["tilt"] = rng.Range(-6, 6),
                ["roll"] = "A" + rng.Int(1, 9),
                ["take"] = rng.Int(1, 12),
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var ctx = env.Ctx;
                var p = env.P;
                var u = U(env);
                var port = IsPort(env);
                var outK = Tout(env);
                var ls = SmallSize(env);
                var bw = port ? W * 0.88 : Math.Min(W * 0.56, H * 0.74 * 1.3);
                var bh = bw * 0.62;
                var slate = PlateCol(sc, new[] { Darkest(sc), sc.Str("ink"), sc.Str("fg") }, sc.Str("bg"), 1.4);
                var chalk = OnCol(sc, slate);
                var e = J.E.OutCubic(J.Clamp(env.Lt / 0.4));
                var eo = J.E.InCubic(env.POut);
                var a = J.Clamp(e * 2) * (1 - eo);
                if (a <= 0.01)
                {
                    env.Body = null;
                    return;
                }
                var cx = W / 2;
                var cy = H / 2 + bh * 0.1 + (1 - e) * H * 0.5 + eo * H * 0.2;
                const double tcl = 0.42;
                var openA = env.Lt < tcl
                    ? -28 * J.E.OutCubic(J.Clamp(env.Lt / 0.25)) * (1 - J.E.InQuad(J.Clamp((env.Lt - 0.28) / (tcl - 0.28))))
                    : 0;
                var shake = env.Lt > tcl && env.Lt < tcl + 0.15
                    ? J.Rs((int)env.Step, 7) * u * 0.006 * (1 - (env.Lt - tcl) / 0.15)
                    : 0;
                ctx.Save();
                ctx.Translate((float)(cx + shake), (float)(cy + shake * 0.6));
                ctx.RotateDegrees((float)JLayouts.D(p, "tilt"));
                var x0 = -bw / 2;
                var y0 = -bh / 2;
                ShadowR(env, x0, y0 - bh * 0.3, bw, bh * 1.3, bh * 0.03, a, u * 0.016);
                env.RRect(x0, y0, bw, bh, bh * 0.03, slate, a, false, J.Contrast(slate, sc.Str("bg")!) < 1.5 ? sc.Str("fg") : null, 2);
                var sh = bh * 0.14;
                void DrawStick(double yy)
                {
                    if (env.Pass != "main") return;
                    Stripes(env, x0, yy, bw, sh, slate, chalk, bw * 0.055, -35, a);
                    ctx.Save();
                    using var sp = new SKPaint
                    {
                        IsAntialias = true,
                        IsStroke = true,
                        StrokeWidth = 1.5f,
                        Color = Env.SkColor(chalk, a),
                        BlendMode = env.Blend,
                    };
                    ctx.DrawRect(new SKRect((float)x0, (float)yy, (float)(x0 + bw), (float)(yy + sh)), sp);
                    ctx.Restore();
                }
                DrawStick(y0 - sh - bh * 0.01);
                ctx.Save();
                ctx.Translate((float)x0, (float)(y0 - sh - bh * 0.01));
                ctx.RotateDegrees((float)openA);
                ctx.Translate((float)-x0, (float)(-(y0 - sh - bh * 0.01)));
                DrawStick(y0 - sh * 2 - bh * 0.02);
                ctx.Restore();
                env.Circle(x0 + sh * 0.5, y0 - sh * 1.05, sh * 0.22, chalk, null, 0, a, false);
                var lw = Math.Max(1.5, u * 0.0022);
                var r1 = y0 + bh * 0.5;
                var r2 = y0 + bh * 0.76;
                env.Line(new[] { new[] { x0, r1 }, new[] { x0 + bw, r1 } }, chalk, lw, a * 0.8, false);
                env.Line(new[] { new[] { x0, r2 }, new[] { x0 + bw, r2 } }, chalk, lw, a * 0.8, false);
                foreach (var f in new[] { 1 / 3.0, 2 / 3.0 })
                    env.Line(new[] { new[] { x0 + bw * f, r1 }, new[] { x0 + bw * f, y0 + bh } }, chalk, lw, a * 0.8, false);
                void Lab(string t, double x, double y) => env.DrawItem(new JObject
                {
                    ["text"] = t,
                    ["font"] = MonoF(env),
                    ["size"] = ls * 0.72,
                    ["track"] = 0.2,
                    ["align"] = "left",
                    ["x"] = x,
                    ["y"] = y,
                    ["color"] = chalk,
                    ["alpha"] = a * 0.75,
                    ["ghost"] = false,
                });
                void Val(string t, double x, double y, double s2) => env.DrawItem(new JObject
                {
                    ["text"] = t,
                    ["font"] = JLayouts.S(p, "font"),
                    ["size"] = s2,
                    ["align"] = "left",
                    ["x"] = x,
                    ["y"] = y,
                    ["color"] = chalk,
                    ["alpha"] = a * J.Clamp((env.Lt - 0.2) * 4),
                    ["ghost"] = false,
                });
                var pad = bw * 0.025;
                Lab("PROD.", x0 + pad, y0 + pad + ls * 0.4);
                var rowH = r2 - r1;
                var row1 = new[]
                {
                    new[] { "SCENE", LineNo(env) }, new[] { "TAKE", JLayouts.I(p, "take").ToString(CultureInfo.InvariantCulture) },
                    new[] { "ROLL", JLayouts.S(p, "roll")! }
                };
                for (var i = 0; i < row1.Length; i++)
                {
                    Lab(row1[i][0], x0 + bw * i / 3 + pad, r1 + ls * 0.6);
                    Val(row1[i][1], x0 + bw * i / 3 + pad, r1 + rowH * 0.62, rowH * 0.46);
                }
                var row2 = new[]
                {
                    new[] { "DATE", J.FmtTime(env.Cut.Start) }, new[] { "DIR.", "JIZURA" }, new[] { "CAM.", "A" }
                };
                for (var i = 0; i < row2.Length; i++)
                {
                    Lab(row2[i][0], x0 + bw * i / 3 + pad, r2 + ls * 0.6);
                    Val(row2[i][1], x0 + bw * i / 3 + pad + ls * 3.2, r2 + (y0 + bh - r2) * 0.55, Math.Min((y0 + bh - r2) * 0.42, ls * 1.3));
                }
                var fb = FitBlock(env.Cut.Text.Trim(), JLayouts.S(p, "font")!, bw - pad * 2, r1 - y0 - ls * 1.6, new JObject { ["lead"] = 1.08 }, 2);
                var size = Math.Min(fb.Size, bh * 0.3);
                var bb = JLayouts.MainDraw(env, new JObject
                {
                    ["text"] = fb.Text,
                    ["font"] = JLayouts.S(p, "font"),
                    ["size"] = size,
                    ["x"] = x0 + pad * 1.4,
                    ["y"] = y0 + ls * 1.2 + (r1 - y0 - ls * 1.2) / 2,
                    ["align"] = "left",
                    ["lead"] = 1.08,
                    ["color"] = chalk,
                    ["noHold"] = PlateHold(env),
                    ["mi"] = MiAt(env, 0.25),
                });
                ctx.Restore();
                env.Body = bb != null ? Box(cx - bw / 2, cy - bh / 2, cx + bw / 2, cy + bh / 2) : null;
            },
        });
    }

        private static readonly string[][] Warn =
    {
        new[] { "WARNING", "警告" }, new[] { "CAUTION", "注意" }, new[] { "DANGER", "危険" }, new[] { "NOTICE", "お知らせ" }
    };

    private static void WarnTri(Env env, double cx, double cy, double s, string fill, string mark, double a, double flash)
    {
        var pts = new[]
        {
            new[] { cx, cy - s * 0.52 }, new[] { cx + s * 0.58, cy + s * 0.46 }, new[] { cx - s * 0.58, cy + s * 0.46 }
        };
        env.Poly(pts, fill, a, false);
        env.Line(CloseLoop(pts).ToArray(), fill, Math.Max(2, s * 0.1), a, false);
        var ma = a * flash;
        env.RRect(cx - s * 0.055, cy - s * 0.24, s * 0.11, s * 0.42, s * 0.05, mark, ma, false);
        env.Circle(cx, cy + s * 0.3, s * 0.065, mark, null, 0, ma, false);
    }

    private static void RegisterWarningLabel()
    {
        RegC("warningLabel", new LayoutDef
        {
            Name = "警告ラベル",
            Tags = new[] { "graphic", "glitch", "pop" },
            W = 0.7,
            Treat = "safe",
            Fits = n => n >= 1 && n <= 16,
            EnterBias = new JObject { ["cut"] = 1.5, ["flicker"] = 1.5, ["pop"] = 1.3, ["blur"] = 0.5 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display")),
                ["variant"] = rng.Pick(new[] { "header", "stripe", "side" }),
                ["word"] = rng.Int(0, 3),
                ["tilt"] = rng.Range(-3, 3),
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var ctx = env.Ctx;
                var p = env.P;
                var u = U(env);
                var port = IsPort(env);
                var outK = Tout(env);
                var ls = SmallSize(env);
                var C = Card(sc);
                var lw0 = port ? W * 0.9 : Math.Min(W * 0.78, H * 1.55);
                var lh0 = lw0 * (port ? 0.78 : 0.5);
                var q = J.E.OutBack(J.Clamp(env.Lt / 0.32), 1.5);
                var eo = J.E.InCubic(env.POut);
                var a = J.Clamp(q * 3) * (1 - eo);
                if (a <= 0.01)
                {
                    env.Body = null;
                    return;
                }
                var warnC = PlateCol(sc, new[] { sc.Str("accent"), sc.Str("accent2"), sc.Str("ink") }, C.Fill, 1.6);
                var inkC = PlateCol(sc, new[] { Darkest(sc), sc.Str("ink"), sc.Str("fg") }, C.Fill, 3);                 var hazard = PlateCol(sc, new[] { sc.Str("accent"), sc.Str("accent2"), C.Fill }, inkC, 1.8);
                var wa = Warn[JLayouts.I(p, "word") % Warn.Length];
                string we = wa[0], wj = wa[1];
                var flash = env.Lt < 0.9 ? ((int)Math.Floor(env.Lt / 0.12) % 2 != 0 ? 0.25 : 1) : 1;
                ctx.Save();
                ctx.Translate((float)(W / 2), (float)(H / 2));
                ctx.RotateDegrees((float)(JLayouts.D(p, "tilt") + (1 - q) * 4));
                ctx.Scale((float)(0.85 + 0.15 * q), (float)(0.85 + 0.15 * q));
                var x0 = -lw0 / 2;
                var y0 = -lh0 / 2;
                ShadowR(env, x0, y0, lw0, lh0, lh0 * 0.04, a, u * 0.014);
                env.RRect(x0, y0, lw0, lh0, lh0 * 0.04, C.Fill, a, false, inkC, Math.Max(3, u * 0.004));
                var ax0 = x0;
                var ay0 = y0;
                var aw = lw0;
                var ah = lh0;
                var variant = JLayouts.S(p, "variant");
                if (variant == "header")
                {
                    var hh = lh0 * 0.26;
                    var he = Tin(env, 0.06, 0.35, J.E.OutCubic);
                    env.RRect(x0, y0, lw0, hh, lh0 * 0.04, warnC, a, false);
                    env.Rect(x0, y0 + hh * 0.5, lw0, hh * 0.5, warnC, a, false);
                    WarnTri(env, x0 + hh * 0.75, y0 + hh * 0.52, hh * 0.72, OnCol(sc, warnC), warnC, a * he, flash);
                    var ht = we + "  " + wj;
                    var hs = Math.Min(hh * 0.46, JLayouts.FitSize(ht, "gothic_black", lw0 - hh * 1.8, hh, new JObject { ["track"] = 0.12 }));
                    env.DrawItem(new JObject
                    {
                        ["text"] = ht,
                        ["font"] = "gothic_black",
                        ["size"] = hs,
                        ["track"] = 0.12,
                        ["align"] = "left",
                        ["x"] = x0 + hh * 1.45,
                        ["y"] = y0 + hh * 0.52,
                        ["color"] = OnCol(sc, warnC),
                        ["alpha"] = a * he,
                        ["ghost"] = false,
                    });
                    ay0 = y0 + hh;
                    ah = lh0 - hh;
                }
                else if (variant == "stripe")
                {
                    var sb = lh0 * 0.11;
                    var off = env.Ltb * sb * 1.2;
                    Stripes(env, x0 + 2, y0 + 2, lw0 - 4, sb, inkC, hazard, sb * 0.7, 45, a, off);
                    Stripes(env, x0 + 2, y0 + lh0 - sb - 2, lw0 - 4, sb, inkC, hazard, sb * 0.7, 45, a, -off);
                    WarnTri(env, x0 + lw0 / 2, y0 + sb + lh0 * 0.16, lh0 * 0.2, warnC, OnCol(sc, warnC), a, flash);
                    env.DrawItem(new JObject
                    {
                        ["text"] = wj + "　" + we,
                        ["font"] = "gothic_black",
                        ["size"] = lh0 * 0.06,
                        ["track"] = 0.2,
                        ["x"] = x0 + lw0 / 2,
                        ["y"] = y0 + sb + lh0 * 0.33,
                        ["color"] = inkC,
                        ["alpha"] = a,
                        ["ghost"] = false,
                    });
                    ay0 = y0 + sb + lh0 * 0.38;
                    ah = lh0 - sb * 2 - lh0 * 0.38;
                }
                else
                {
                    var sw2 = lw0 * (port ? 0.3 : 0.26);
                    env.RRect(x0, y0, sw2, lh0, lh0 * 0.04, warnC, a, false);
                    env.Rect(x0 + sw2 * 0.5, y0, sw2 * 0.5, lh0, warnC, a, false);
                    WarnTri(env, x0 + sw2 / 2, y0 + lh0 * 0.42, sw2 * 0.68, OnCol(sc, warnC), warnC, a, flash);
                    env.DrawItem(new JObject
                    {
                        ["text"] = we,
                        ["font"] = "gothic_black",
                        ["size"] = sw2 * 0.14,
                        ["track"] = 0.1,
                        ["x"] = x0 + sw2 / 2,
                        ["y"] = y0 + lh0 * 0.78,
                        ["color"] = OnCol(sc, warnC),
                        ["alpha"] = a,
                        ["ghost"] = false,
                    });
                    ax0 = x0 + sw2;
                    aw = lw0 - sw2;
                }
                var fb = FitBlock(env.Cut.Text.Trim(), JLayouts.S(p, "font")!, aw * 0.86, ah * 0.66,
                    new JObject { ["lead"] = 1.08, ["track"] = 0.02 }, port ? 3 : 2);
                var size = Math.Min(fb.Size, u * 0.2);
                var bb = JLayouts.MainDraw(env, new JObject
                {
                    ["text"] = fb.Text,
                    ["font"] = JLayouts.S(p, "font"),
                    ["size"] = size,
                    ["x"] = ax0 + aw / 2,
                    ["y"] = ay0 + ah / 2 - (variant == "stripe" ? 0 : ah * 0.04),
                    ["lead"] = 1.08,
                    ["track"] = 0.02,
                    ["color"] = inkC,
                    ["noHold"] = PlateHold(env),
                    ["mi"] = MiAt(env, 0.12),
                });
                if (variant != "stripe")
                    env.DrawItem(new JObject
                    {
                        ["text"] = "No." + LineNo(env) + "  —  " + J.FmtTime(env.Cut.Start),
                        ["font"] = MonoF(env),
                        ["size"] = ls * 0.8,
                        ["track"] = 0.2,
                        ["align"] = "right",
                        ["x"] = ax0 + aw - ls,
                        ["y"] = ay0 + ah - ls * 0.9,
                        ["color"] = inkC,
                        ["alpha"] = a * 0.6 * outK,
                        ["ghost"] = false,
                    });
                ctx.Restore();
                env.Body = bb != null ? Box(W / 2 - lw0 / 2, H / 2 - lh0 / 2, W / 2 + lw0 / 2, H / 2 + lh0 / 2) : null;
            },
        });
    }

        private static string Yen(int v) => "¥" + v.ToString("N0", CultureInfo.InvariantCulture);

    private static void RegisterPriceTag()
    {
        RegC("priceTag", new LayoutDef
        {
            Name = "値札",
            Tags = new[] { "pop", "graphic" },
            W = 0.6,
            Treat = "safe",
            Fits = n => n >= 1 && n <= 14,
            EnterBias = new JObject { ["pop"] = 1.5, ["cut"] = 1.3, ["drop"] = 1.2, ["slice"] = 0.5 },
            Plan = (rng, cut, st) =>
            {
                var price = rng.Pick(new[] { 980, 1280, 1980, 2480, 3300, 4980, 580, 12800 });
                return new JObject
                {
                    ["font"] = rng.Pick(FontsOf(st, "display", "body")),
                    ["variant"] = rng.Pick(new[] { "hang", "hang", "shelf" }),
                    ["price"] = price,
                    ["was"] = Math.Round(price * rng.Range(1.25, 1.6) / 10) * 10,
                    ["col"] = rng.Pick(new[] { "card", "accent" }),
                    ["swing"] = rng.Range(10, 18) * rng.Pick(new[] { 1, -1 }),
                };
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var ctx = env.Ctx;
                var p = env.P;
                var u = U(env);
                var port = IsPort(env);
                var outK = Tout(env);
                var ls = SmallSize(env);
                var C = Card(sc);
                var t0 = env.Cut.Text.Trim();
                var e = J.Clamp(env.Lt / 0.45);
                var eo = J.E.InCubic(env.POut);
                var a = J.Clamp(e * 3) * (1 - eo);
                if (a <= 0.01)
                {
                    env.Body = null;
                    return;
                }
                var tagC = JLayouts.S(p, "col") == "accent" ? PlateCol(sc, new[] { sc.Str("accent"), sc.Str("ink") }) : C.Fill;
                var tc = OnCol(sc, tagC);
                var red = PlateCol(sc, new[] { sc.Str("accent"), sc.Str("accent2"), tc }, tagC, 1.8);
                var price = JLayouts.I(p, "price");
                if (JLayouts.S(p, "variant") == "shelf")
                {
                    var bw = port ? W * 0.9 : Math.Min(W * 0.8, H * 1.8);
                    var bh = bw * (port ? 0.5 : 0.34);
                    var x0 = W / 2 - bw / 2;
                    var y0 = H / 2 - bh / 2 + (1 - J.E.OutBack(e, 1.3)) * H * 0.3 + eo * H * 0.1;
                    ShadowR(env, x0, y0, bw, bh, bh * 0.03, a, u * 0.012);
                    env.RRect(x0, y0, bw, bh, bh * 0.03, tagC, a, false, J.Contrast(tagC, sc.Str("bg")!) < 1.4 ? J.Mix(sc.Str("fg")!, tagC, 0.4) : null, 1.5);
                    var hdr = bh * 0.2;
                    env.Rect(x0, y0, bw, hdr, red, a, false);
                    env.DrawItem(new JObject
                    {
                        ["text"] = "特价  ·  No." + LineNo(env),
                        ["font"] = BodyF(env),
                        ["size"] = hdr * 0.5,
                        ["track"] = 0.2,
                        ["align"] = "left",
                        ["x"] = x0 + bw * 0.03,
                        ["y"] = y0 + hdr / 2,
                        ["color"] = OnCol(sc, red),
                        ["alpha"] = a,
                        ["ghost"] = false,
                    });
                    var nameW = bw * (port ? 0.9 : 0.62);
                    var nTop = y0 + hdr;
                    var nBot = y0 + bh * (port ? 0.62 : 0.8);
                    var fb = FitBlock(t0, JLayouts.S(p, "font")!, nameW * 0.92, (nBot - nTop) * 0.86, new JObject { ["lead"] = 1.05 }, 2);
                    var size = Math.Min(fb.Size, bh * 0.3);
                    var bb = JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = fb.Text,
                        ["font"] = JLayouts.S(p, "font"),
                        ["size"] = size,
                        ["x"] = x0 + bw * 0.04,
                        ["y"] = (nTop + nBot) / 2,
                        ["align"] = "left",
                        ["lead"] = 1.05,
                        ["color"] = tc,
                        ["noHold"] = PlateHold(env),
                        ["mi"] = MiAt(env, 0.2),
                    });
                    var pa = Tin(env, 0.3, 0.35, x => J.E.OutBack(x)) * outK;
                    var px = x0 + bw * 0.96;
                    var py = port ? y0 + bh * 0.76 : y0 + hdr + (bh - hdr) * 0.44;
                    var ps = Math.Min((bh - hdr) * (port ? 0.26 : 0.3), size * 0.8);
                    env.DrawItem(new JObject
                    {
                        ["text"] = Yen(price),
                        ["font"] = "gothic_black",
                        ["size"] = ps * J.Clamp(pa),
                        ["align"] = "right",
                        ["x"] = px,
                        ["y"] = py,
                        ["color"] = red,
                        ["alpha"] = J.Clamp(pa),
                        ["ghost"] = false,
                    });
                    env.DrawItem(new JObject
                    {
                        ["text"] = "税込",
                        ["font"] = BodyF(env),
                        ["size"] = ls * 0.9,
                        ["align"] = "right",
                        ["x"] = px,
                        ["y"] = py + ps * 0.62,
                        ["color"] = tc,
                        ["alpha"] = J.Clamp(pa) * 0.8,
                        ["ghost"] = false,
                    });
                    var bx = x0 + bw * 0.04;
                    var byy = y0 + bh * 0.88;
                    var xx = bx;
                    for (var i = 0; i < 36 && xx < bx + bw * 0.22; i++)
                    {
                        var w2 = bw * (0.002 + 0.004 * J.R(price, i, 3));
                        env.Rect(xx, byy - bh * 0.06, w2, bh * 0.09, tc, a * 0.8, false);
                        xx += w2 + bw * (0.002 + 0.003 * J.R(price, i, 4));
                    }
                    env.Body = bb != null ? Box(x0, y0, x0 + bw, y0 + bh) : null;
                    return;
                }
                var tw = port ? W * 0.62 : Math.Min(W * 0.34, H * 0.46);
                var th = tw * 1.45;
                var hx = W / 2 + (port ? 0 : W * 0.04);
                var hy = H * 0.08;
                var cord = H * (port ? 0.12 : 0.1);
                var t = Math.Max(0, env.Lt);
                var swing = JLayouts.D(p, "swing") * Math.Exp(-t * 2.2) * Math.Cos(t * 5.2) * (1 - eo)
                            + Math.Sin(env.Ltb * 1.1) * 1.2 + eo * JLayouts.D(p, "swing") * 1.5;
                var drop = (1 - J.E.OutBack(e, 1.4)) * -H * 0.4;
                env.Circle(hx, hy, u * 0.008, sc.Str("sub"), null, 0, a, false);
                ctx.Save();
                ctx.Translate((float)hx, (float)(hy + drop));
                ctx.RotateDegrees((float)swing);
                env.Line(new[] { new[] { 0.0, 0.0 }, new[] { -tw * 0.1, cord }, new[] { 0.0, cord + tw * 0.12 }, new[] { tw * 0.1, cord }, new[] { 0.0, 0.0 } },
                    sc.Str("sub"), Math.Max(1.5, u * 0.002), a, false);
                var top = cord + tw * 0.05;
                var ch = tw * 0.22;
                var pts = new[]
                {
                    new[] { -tw / 2 + ch, top }, new[] { tw / 2 - ch, top }, new[] { tw / 2, top + ch },
                    new[] { tw / 2, top + th }, new[] { -tw / 2, top + th }, new[] { -tw / 2, top + ch }
                };
                if (env.Pass == "main")
                {
                    ctx.Save();
                    ctx.Translate((float)(u * 0.008), (float)(u * 0.012));
                    env.Poly(pts, Darkest(sc), a * (J.Lum(sc.Str("bg")!) > 0.5 ? 0.22 : 0.5), false);
                    ctx.Restore();
                }
                env.Poly(pts, tagC, a, false);
                if (J.Contrast(tagC, sc.Str("bg")!) < 1.4)
                    env.Line(CloseLoop(pts).ToArray(), J.Mix(sc.Str("fg")!, tagC, 0.4), 1.5, a, false);
                env.Circle(0, top + tw * 0.13, tw * 0.045, sc.Str("bg"), J.Mix(tagC, tc, 0.3), Math.Max(1.5, tw * 0.012), a, false);
                var m = tw * 0.1;
                var fb2 = FitBlock(t0, JLayouts.S(p, "font")!, tw - m * 2, th * 0.4, new JObject { ["lead"] = 1.08 }, 3);
                var size2 = Math.Min(fb2.Size, tw * 0.3);
                var bb2 = JLayouts.MainDraw(env, new JObject
                {
                    ["text"] = fb2.Text,
                    ["font"] = JLayouts.S(p, "font"),
                    ["size"] = size2,
                    ["x"] = 0,
                    ["y"] = top + th * 0.36,
                    ["lead"] = 1.08,
                    ["color"] = tc,
                    ["noHold"] = true,
                    ["plain"] = true,
                    ["mi"] = MiAt(env, 0.2),
                });
                env.Line(new[] { new[] { -tw / 2 + m, top + th * 0.62 }, new[] { tw / 2 - m, top + th * 0.62 } }, tc, Math.Max(1, u * 0.0015), a * 0.5, false);
                var pa2 = Tin(env, 0.35, 0.35, x => J.E.OutBack(x)) * outK;
                var was = Yen(JLayouts.I(p, "was"));
                var now = Yen(price);
                var ws = tw * 0.1;
                env.DrawItem(new JObject
                {
                    ["text"] = was,
                    ["font"] = MonoF(env),
                    ["size"] = ws,
                    ["x"] = 0,
                    ["y"] = top + th * 0.71,
                    ["color"] = tc,
                    ["alpha"] = J.Clamp(pa2) * 0.6,
                    ["ghost"] = false,
                });
                var wm = Meas(was, MonoF(env), ws).W;
                env.Line(new[]
                {
                    new[] { -wm / 2 - ws * 0.2, top + th * 0.71 },
                    new[] { -wm / 2 - ws * 0.2 + (wm + ws * 0.4) * J.Clamp(pa2), top + th * 0.71 }
                }, red, Math.Max(2, ws * 0.12), 1, false);
                env.DrawItem(new JObject
                {
                    ["text"] = now,
                    ["font"] = "gothic_black",
                    ["size"] = tw * 0.17 * J.Clamp(pa2),
                    ["x"] = 0,
                    ["y"] = top + th * 0.86,
                    ["color"] = red,
                    ["alpha"] = J.Clamp(pa2),
                    ["ghost"] = false,
                });
                ctx.Restore();
                env.Body = bb2 != null ? Box(hx - tw / 2, hy + cord, hx + tw / 2, hy + cord + th) : null;
            },
        });
    }

    private static void RegisterNameTag()
    {
        RegC("nameTag", new LayoutDef
        {
            Name = "名札",
            Tags = new[] { "pop", "emotional" },
            W = 0.6,
            Treat = "safe",
            Fits = n => n >= 1 && n <= 14,
            EnterBias = new JObject { ["cut"] = 1.4, ["type"] = 1.3, ["pop"] = 1.2, ["slice"] = 0.5 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Chance(0.6) ? "klee" : rng.Pick(FontsOf(st, "display", "body")),
                ["variant"] = rng.Pick(new[] { "hello", "hello", "school" }),
                ["tilt"] = rng.Range(-6, 6),
                ["grade"] = rng.Int(1, 6),
                ["cls"] = rng.Int(1, 4),
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var ctx = env.Ctx;
                var p = env.P;
                var u = U(env);
                var port = IsPort(env);
                var outK = Tout(env);
                var ls = SmallSize(env);
                var C = Card(sc);
                var t0 = env.Cut.Text.Trim();
                var e = J.Clamp(env.Lt / 0.5);
                var eo = J.E.InCubic(env.POut);
                var a = J.Clamp(e * 3) * (1 - J.Clamp((env.POut - 0.5) / 0.5));
                if (a <= 0.01)
                {
                    env.Body = null;
                    return;
                }
                var bandC = PlateCol(sc, new[] { sc.Str("accent"), sc.Str("accent2"), sc.Str("ink") }, C.Fill, 1.6);
                var variant = JLayouts.S(p, "variant");
                var bw = port ? W * 0.86 : Math.Min(W * 0.62, H * 1.2);
                var bh = bw * (variant == "hello" ? 0.66 : 0.56);
                var q = J.E.OutBack(e, 1.6);
                var rot = JLayouts.D(p, "tilt") * q + (1 - q) * -25 + eo * 18;
                var cx = W / 2 + eo * W * 0.1;
                var cy = H / 2 + (1 - q) * -H * 0.35 + eo * H * 0.5;
                ctx.Save();
                ctx.Translate((float)cx, (float)cy);
                ctx.RotateDegrees((float)rot);
                var x0 = -bw / 2;
                var y0 = -bh / 2;
                var r = bh * 0.08;
                ShadowR(env, x0, y0, bw, bh, r, a, u * 0.014);
                BBox? bb;
                if (variant == "hello")
                {
                    env.RRect(x0, y0, bw, bh, r, bandC, a, false);
                    var hh = bh * 0.3;
                    env.Rect(x0 + bw * 0.035, y0 + hh, bw * 0.93, bh * 0.58, C.Fill, a, false);
                    var hc = OnCol(sc, bandC);
                    env.DrawItem(new JObject
                    {
                        ["text"] = "HELLO",
                        ["font"] = "gothic_black",
                        ["size"] = hh * 0.52,
                        ["track"] = 0.08,
                        ["x"] = 0,
                        ["y"] = y0 + hh * 0.4,
                        ["color"] = hc,
                        ["alpha"] = a,
                        ["ghost"] = false,
                    });
                    env.DrawItem(new JObject
                    {
                        ["text"] = "my name is",
                        ["font"] = BodyF(env),
                        ["size"] = hh * 0.2,
                        ["track"] = 0.15,
                        ["x"] = 0,
                        ["y"] = y0 + hh * 0.82,
                        ["color"] = hc,
                        ["alpha"] = a,
                        ["ghost"] = false,
                    });
                    var fb = FitBlock(t0, JLayouts.S(p, "font")!, bw * 0.84, bh * 0.46, new JObject { ["lead"] = 1.05 }, 2);
                    var size = Math.Min(fb.Size, bh * 0.36);
                    bb = JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = fb.Text,
                        ["font"] = JLayouts.S(p, "font"),
                        ["size"] = size,
                        ["x"] = 0,
                        ["y"] = y0 + hh + bh * 0.29,
                        ["lead"] = 1.05,
                        ["rot"] = -2,
                        ["color"] = C.Text,
                        ["noHold"] = PlateHold(env),
                        ["mi"] = MiAt(env, 0.3),
                    });
                }
                else
                {
                    env.RRect(x0, y0, bw, bh, r, C.Fill, a, false, bandC, Math.Max(4, bw * 0.014));
                    var pinY = y0 - bh * 0.06;
                    env.Line(new[] { new[] { x0 + bw * 0.2, pinY }, new[] { x0 + bw * 0.8, pinY } }, sc.Str("sub"), Math.Max(3, u * 0.004), a, false);
                    env.Circle(x0 + bw * 0.8, pinY, u * 0.008, sc.Str("sub"), null, 0, a, false);
                    var fy = y0 + bh * 0.2;
                    env.DrawItem(new JObject
                    {
                        ["text"] = JLayouts.I(p, "grade") + " ねん　" + JLayouts.I(p, "cls") + " くみ",
                        ["font"] = JLayouts.S(p, "font"),
                        ["size"] = bh * 0.1,
                        ["align"] = "left",
                        ["x"] = x0 + bw * 0.08,
                        ["y"] = fy,
                        ["color"] = C.Text,
                        ["alpha"] = a,
                        ["ghost"] = false,
                    });
                    env.Line(new[] { new[] { x0 + bw * 0.06, fy + bh * 0.09 }, new[] { x0 + bw * 0.94, fy + bh * 0.09 } }, bandC, Math.Max(2, u * 0.003), a, false);
                    env.DrawItem(new JObject
                    {
                        ["text"] = "名字",
                        ["font"] = BodyF(env),
                        ["size"] = bh * 0.07,
                        ["align"] = "left",
                        ["x"] = x0 + bw * 0.08,
                        ["y"] = fy + bh * 0.19,
                        ["color"] = bandC,
                        ["alpha"] = a,
                        ["ghost"] = false,
                    });
                    env.Circle(x0 + bw * 0.88, y0 + bh * 0.2, bh * 0.08, bandC, null, 0, a, false);
                    var fb = FitBlock(t0, JLayouts.S(p, "font")!, bw * 0.84, bh * 0.44, new JObject { ["lead"] = 1.05 }, 2);
                    var size = Math.Min(fb.Size, bh * 0.34);
                    bb = JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = fb.Text,
                        ["font"] = JLayouts.S(p, "font"),
                        ["size"] = size,
                        ["x"] = 0,
                        ["y"] = y0 + bh * 0.66,
                        ["lead"] = 1.05,
                        ["color"] = C.Text,
                        ["noHold"] = PlateHold(env),
                        ["mi"] = MiAt(env, 0.3),
                    });
                }
                ctx.Restore();
                env.Body = bb != null ? Box(cx - bw / 2, cy - bh / 2, cx + bw / 2, cy + bh / 2) : null;
            },
        });
    }

    private static void RegisterStickyNotes()
    {
        RegC("stickyNotes", new LayoutDef
        {
            Name = "付箋",
            Tags = new[] { "pop", "emotional", "calm" },
            W = 0.8,
            Treat = "safe",
            Fits = n => n >= 1 && n <= 18,
            EnterBias = new JObject { ["cut"] = 1.5, ["pop"] = 1.4, ["type"] = 1.2, ["slice"] = 0.5, ["stretch"] = 0.5 },
            Plan = (rng, cut, st) =>
            {
                var n = cut.N;
                var k = n <= 4 ? 1 : n <= 9 ? 2 : n <= 14 ? 3 : 4;
                return new JObject
                {
                    ["font"] = rng.Chance(0.6) ? "klee" : rng.Pick(FontsOf(st, "display", "body")),
                    ["chunks"] = new JArray(SplitK(cut.Text, k, k)),
                    ["layout"] = rng.Pick(new[] { "scatter", "cascade" }),
                    ["rots"] = new JArray(Enumerable.Range(0, 4).Select(_ => rng.Range(-7, 7))),
                    ["offs"] = new JArray(Enumerable.Range(0, 4).Select(_ => rng.Range(-1, 1))),
                    ["cols"] = new JArray(Enumerable.Range(0, 4).Select(_ => rng.Int(0, 3))),
                    ["pin"] = rng.Pick(new[] { "glue", "tape" }),
                };
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var ctx = env.Ctx;
                var p = env.P;
                var u = U(env);
                var port = IsPort(env);
                var C = Card(sc);
                var chunks = (p["chunks"] as JArray)?.Select(t => (string?)t).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();
                if (chunks == null || chunks.Count == 0) chunks = new List<string> { env.Cut.Text.Trim() };
                var k = chunks.Count;
                var acc = PlateCol(sc, new[] { sc.Str("accent"), sc.Str("ink") });
                var col2 = !string.IsNullOrEmpty(sc.Str("accent2")) && J.Lum(sc.Str("accent2")!) > 0.3 && J.Contrast(sc.Str("accent2")!, sc.Str("bg")!) > 1.3
                    ? sc.Str("accent2")!
                    : J.Mix(C.Fill, acc, 0.2);
                var pal = new[] { acc, J.Mix(C.Fill, acc, 0.45), col2, C.Fill };
                double ns;
                var pos = new List<double[]>();
                var offs = (JArray)p["offs"]!;
                if (k == 1)
                {
                    ns = Math.Min(W, H) * 0.62;
                    pos.Add(new[] { W / 2.0, H / 2.0 });
                }
                else if (JLayouts.S(p, "layout") == "cascade" || port)
                {
                    var dx = port ? 0.14 : 0.88;
                    var dy = port ? 0.86 : 0.3;
                    ns = Math.Min(Math.Min(W * 0.86 / (1 + (k - 1) * dx), H * 0.84 / (1 + (k - 1) * dy)), Math.Min(W, H) * 0.62);
                    for (var i = 0; i < chunks.Count; i++)
                        pos.Add(new[] { W / 2 + (i - (k - 1) / 2.0) * ns * dx, H / 2 + (i - (k - 1) / 2.0) * ns * dy });
                }
                else
                {
                    ns = Math.Min(W * 0.84 / (k * 1.02), H * 0.62);
                    for (var i = 0; i < chunks.Count; i++)
                        pos.Add(new[] { W / 2 + (i - (k - 1) / 2.0) * ns * 1.02, H / 2 + (double)offs[i % 4]! * H * 0.06 });
                }
                var rots = (JArray)p["rots"]!;
                var colsArr = (JArray)p["cols"]!;
                BBox? bb = null;
                for (var i = 0; i < chunks.Count; i++)
                {
                    var c = chunks[i];
                    var x = pos[i][0];
                    var y = pos[i][1];
                    var t1 = 0.03 + i * 0.13;
                    var e = J.Clamp((env.Lt - t1) / 0.22);
                    if (e <= 0) continue;
                    var sq = J.Lerp(1.18, 1, J.E.OutCubic(e));
                    var peel = J.E.InCubic(J.Clamp(env.POut * 1.3 - i * 0.1));
                    var a = J.Clamp(e * 3) * (1 - J.Clamp((peel - 0.6) / 0.4));
                    if (a <= 0.01) continue;
                    var col = pal[(int)(double)colsArr[i % 4]! % pal.Length];
                    var tc = OnCol(sc, col);
                    var rot = (double)rots[i % 4]! + peel * 25 * (i % 2 != 0 ? 1 : -1);
                    ctx.Save();
                    ctx.Translate((float)x, (float)(y - ns / 2 + peel * H * 0.3));
                    ctx.RotateDegrees((float)rot);
                    ctx.Scale((float)sq, (float)(sq * (1 - peel * 0.3)));
                    ctx.Translate(0, (float)(ns / 2));
                    var hs = ns / 2;
                    if (env.Pass == "main")
                    {
                        using var sh = new SKPaint
                        {
                            IsAntialias = true,
                            Color = Env.SkColor(Darkest(sc), a * (J.Lum(sc.Str("bg")!) > 0.5 ? 0.22 : 0.55)),
                            BlendMode = env.Blend,
                        };
                        using var shp = new SKPath();
                        shp.MoveTo((float)(-hs + ns * 0.02), (float)(-hs + ns * 0.04));
                        shp.LineTo((float)(hs + ns * 0.025), (float)(-hs + ns * 0.04));
                        shp.LineTo((float)(hs + ns * 0.02), (float)(hs - ns * 0.02));
                        shp.QuadTo((float)(hs - ns * 0.1), (float)(hs + ns * 0.05), (float)(-hs + ns * 0.04), (float)(hs + ns * 0.03));
                        shp.Close();
                        ctx.DrawPath(shp, sh);
                    }
                    env.Poly(new[]
                    {
                        new[] { -hs, -hs }, new[] { hs, -hs }, new[] { hs, hs - ns * 0.06 },
                        new[] { hs - ns * 0.1, hs }, new[] { -hs, hs }
                    }, col, a, false);
                    env.Poly(new[]
                    {
                        new[] { hs, hs - ns * 0.06 }, new[] { hs - ns * 0.1, hs }, new[] { hs - ns * 0.085, hs - ns * 0.075 }
                    }, J.Mix(col, Darkest(sc), 0.25), a, false);
                    if (J.Contrast(col, sc.Str("bg")!) < 1.3)
                        env.Line(new[]
                        {
                            new[] { -hs, -hs }, new[] { hs, -hs }, new[] { hs, hs - ns * 0.06 },
                            new[] { hs - ns * 0.1, hs }, new[] { -hs, hs }, new[] { -hs, -hs }
                        }, J.Mix(sc.Str("fg")!, col, 0.4), 1.2, a, false);
                    if (JLayouts.S(p, "pin") == "glue") env.Rect(-hs, -hs, ns, ns * 0.1, J.Mix(col, Darkest(sc), 0.08), a, false);
                    else env.Rect(-ns * 0.18, -hs - ns * 0.05, ns * 0.36, ns * 0.11, Lightest(sc), a * 0.55, false);
                    var fb = FitBlock(c, JLayouts.S(p, "font")!, ns * 0.8, ns * 0.64, new JObject { ["lead"] = 1.12 }, 3);
                    var r = JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = fb.Text,
                        ["font"] = JLayouts.S(p, "font"),
                        ["size"] = Math.Min(fb.Size, ns * 0.36),
                        ["x"] = 0,
                        ["y"] = ns * 0.04,
                        ["lead"] = 1.12,
                        ["rot"] = -1.5,
                        ["color"] = tc,
                        ["noHold"] = PlateHold(env),
                        ["mi"] = MiAt(env, t1 + 0.08),
                    });
                    ctx.Restore();
                    if (r != null) bb = JLayouts.UnionBb(bb, Box(x - hs, y - hs, x + hs, y + hs));
                }
                env.Body = bb;
            },
        });
    }

    private static void RegisterKaruta()
    {
        RegC("karuta", new LayoutDef
        {
            Name = "かるた札",
            Tags = new[] { "pop", "emotional", "editorial" },
            W = 0.6,
            Treat = "safe",
            Fits = n => n >= 1 && n <= 16,
            EnterBias = new JObject { ["cut"] = 1.6, ["pop"] = 1.2, ["blur"] = 0.6, ["slice"] = 0.5 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "serif", "display")),
                ["variant"] = PortOf(cut) ? rng.Pick(new[] { "single", "pair" }) : rng.Pick(new[] { "pair", "pair", "single" }),
                ["from"] = rng.Pick(new[] { 1, -1 }),
                ["tilt"] = rng.Range(-4, 4),
                ["art"] = rng.Pick(new[] { "sun", "wave", "mount" }),
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var ctx = env.Ctx;
                var p = env.P;
                var u = U(env);
                var port = IsPort(env);
                var outK = Tout(env);
                var ls = SmallSize(env);
                var C = Card(sc);
                var t0 = VText(env.Cut.Text);
                var cps = JLayouts.CodePoints(t0);
                var first = cps.Count > 0 ? cps[0] : "";
                var frameC = new[] { sc.Str("accent")!, sc.Str("accent2")!, sc.Str("ink")!, C.Text }
                    .FirstOrDefault(c => J.Contrast(c, C.Fill) >= 1.3 && J.Contrast(c, sc.Str("bg")!) >= 1.3) ?? C.Text;
                var red = PlateCol(sc, new[] { sc.Str("accent"), sc.Str("accent2"), C.Text }, C.Fill, 1.7);
                var pair = JLayouts.S(p, "variant") == "pair";
                var ch = port
                    ? Math.Min(H * (pair ? 0.42 : 0.72), W * (pair ? 0.56 : 0.8) / 0.72)
                    : Math.Min(H * 0.8, pair ? W * 0.9 / 2.2 / 0.72 : H);
                var cw = ch * 0.72;
                var eo = J.E.InCubic(env.POut);
                (double E, double Q, double Bump, double T1) Slap(int i)
                {
                    var t1 = i * 0.12;
                    var e = J.Clamp((env.Lt - t1) / 0.28);
                    var q = J.E.OutCubic(e);
                    var bump = e >= 1 ? Math.Exp(-(env.Lt - t1 - 0.28) * 14) * Math.Sin((env.Lt - t1 - 0.28) * 40) * 0.02 : 0;
                    return (e, q, bump, t1);
                }
                void Impact(double x, double y, double w, double h, double t1)
                {
                    var k = (env.Lt - t1 - 0.26) / 0.25;
                    if (k <= 0 || k >= 1) return;
                    var hyp = Math.Sqrt(w * w + h * h);
                    for (var j = 0; j < 10; j++)
                    {
                        var ang = j / 10.0 * J.TAU + 0.3;
                        var r0 = hyp * (0.55 + k * 0.1);
                        var r1 = r0 + u * 0.05 * (1 - k);
                        env.Line(new[]
                        {
                            new[] { x + Math.Cos(ang) * r0 * (w / hyp) * 1.4, y + Math.Sin(ang) * r0 * (h / hyp) * 1.4 },
                            new[] { x + Math.Cos(ang) * r1 * (w / hyp) * 1.4, y + Math.Sin(ang) * r1 * (h / hyp) * 1.4 }
                        }, sc.Str("sub"), Math.Max(2, u * 0.003), 1 - k, false);
                    }
                }
                BBox? CardAt(int i, double cx, double cy, Func<double, (double E, double Q, double Bump, double T1), BBox?> draw)
                {
                    var S = Slap(i);
                    if (S.E <= 0) return null;
                    var dx = (1 - S.Q) * W * 0.7 * JLayouts.D(p, "from");
                    var rot = (1 - S.Q) * 35 * JLayouts.D(p, "from") + JLayouts.D(p, "tilt") * (i != 0 ? -0.6 : 1);
                    var a = J.Clamp(S.E * 3) * (1 - eo);
                    Impact(cx, cy, cw, ch, S.T1);
                    ctx.Save();
                    ctx.Translate((float)(cx + dx), (float)(cy + eo * H * 0.15));
                    ctx.RotateDegrees((float)rot);
                    ctx.Scale((float)(1 + S.Bump), (float)(1 - S.Bump));
                    ShadowR(env, -cw / 2, -ch / 2, cw, ch, cw * 0.05, a, u * 0.014);
                    env.RRect(-cw / 2, -ch / 2, cw, ch, cw * 0.05, frameC, a, false);
                    env.RRect(-cw / 2 + cw * 0.06, -ch / 2 + cw * 0.06, cw * 0.88, ch - cw * 0.12, cw * 0.03, C.Fill, a, false);
                    var r = draw(a, S);
                    ctx.Restore();
                    return r;
                }
                BBox? Tori(double a)
                {
                    var R = cw * 0.2;
                    var ccx = cw * 0.2;
                    var ccy = -ch / 2 + cw * 0.3;
                    env.Circle(ccx, ccy, R, C.Fill, red, Math.Max(3, cw * 0.02), a, false);
                    env.DrawItem(new JObject
                    {
                        ["text"] = first,
                        ["font"] = JLayouts.S(p, "font"),
                        ["size"] = R * 1.3,
                        ["x"] = ccx,
                        ["y"] = ccy,
                        ["color"] = red,
                        ["alpha"] = a,
                        ["ghost"] = false,
                    });
                    var ay = ch * 0.12;
                    var aw = cw * 0.66;
                    var artC = PlateCol(sc, new[] { sc.Str("accent2"), sc.Str("accent"), sc.Str("sub") }, C.Fill, 1.3);
                    var art = JLayouts.S(p, "art");
                    if (art == "sun")
                    {
                        env.Circle(0, ay, aw * 0.26, artC, null, 0, a * 0.9, false);
                        env.Rect(-aw / 2, ay + aw * 0.2, aw, aw * 0.05, C.Text, a * 0.6, false);
                    }
                    else if (art == "wave")
                    {
                        for (var j = 0; j < 3; j++)
                        {
                            var pts = new List<double[]>();
                            for (var i = 0; i <= 20; i++)
                                pts.Add(new[] { -aw / 2 + aw * i / 20, ay + j * aw * 0.14 + Math.Sin(i / 20.0 * J.TAU * 1.5) * aw * 0.05 });
                            env.Line(pts.ToArray(), artC, Math.Max(3, aw * 0.03), a * 0.9, false);
                        }
                    }
                    else
                        env.Poly(new[]
                        {
                            new[] { -aw / 2, ay + aw * 0.3 }, new[] { -aw * 0.1, ay - aw * 0.25 }, new[] { aw * 0.1, ay + aw * 0.02 },
                            new[] { aw * 0.25, ay - aw * 0.12 }, new[] { aw / 2, ay + aw * 0.3 }
                        }, artC, a * 0.9, false);
                    return null;
                }
                BBox? Verse(double a, (double E, double Q, double Bump, double T1) S, bool big)
                {
                    var iw = cw * 0.72;
                    var ih = big ? ch - cw * 0.72 : ch * 0.8;
                    var fb = FitBlock(t0, JLayouts.S(p, "font")!, iw, ih,
                        new JObject { ["vertical"] = true, ["lead"] = 1.3, ["track"] = 0.05 }, 3);
                    var size = Math.Min(fb.Size, cw * 0.3);
                    var mm = Meas(fb.Text, JLayouts.S(p, "font")!, size, new JObject { ["vertical"] = true, ["lead"] = 1.3, ["track"] = 0.05 });
                    var y = big ? -ch / 2 + cw * 0.58 + (ih - mm.H) * 0.3 : -mm.H / 2;
                    return JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = fb.Text,
                        ["font"] = JLayouts.S(p, "font"),
                        ["size"] = size,
                        ["x"] = 0,
                        ["y"] = y,
                        ["vertical"] = true,
                        ["align"] = "left",
                        ["lead"] = 1.3,
                        ["track"] = 0.05,
                        ["color"] = C.Text,
                        ["noHold"] = PlateHold(env),
                        ["mi"] = MiAt(env, S.T1 + 0.2),
                    });
                }
                BBox? bb = null;
                if (pair)
                {
                    var gap = cw * 0.18;
                    var ax = port ? W / 2.0 : W / 2 + (cw + gap) / 2;
                    var ay = port ? H / 2 - (ch + gap) / 2 : H / 2;
                    var bx = port ? W / 2.0 : W / 2 - (cw + gap) / 2;
                    var by = port ? H / 2 + (ch + gap) / 2 : H / 2;
                    var r = CardAt(0, ax, ay, (a, S) =>
                    {
                        env.DrawItem(new JObject
                        {
                            ["text"] = "読",
                            ["font"] = SerifF(env),
                            ["size"] = cw * 0.1,
                            ["x"] = cw * 0.34,
                            ["y"] = -ch / 2 + cw * 0.16,
                            ["color"] = red,
                            ["alpha"] = a,
                            ["ghost"] = false,
                        });
                        return Verse(a, S, false);
                    });
                    if (r != null) bb = Box(ax - cw / 2, ay - ch / 2, ax + cw / 2, ay + ch / 2);
                    CardAt(1, bx, by, (a, _) => Tori(a));
                    env.Body = bb;
                    return;
                }
                var r2 = CardAt(0, W / 2, H / 2, (a, S) =>
                {
                    var R = cw * 0.2;
                    var ccx = cw * 0.2;
                    var ccy = -ch / 2 + cw * 0.3;
                    env.Circle(ccx, ccy, R, C.Fill, red, Math.Max(3, cw * 0.02), a, false);
                    env.DrawItem(new JObject
                    {
                        ["text"] = first,
                        ["font"] = JLayouts.S(p, "font"),
                        ["size"] = R * 1.3,
                        ["x"] = ccx,
                        ["y"] = ccy,
                        ["color"] = red,
                        ["alpha"] = a,
                        ["ghost"] = false,
                    });
                    return Verse(a, S, true);
                });
                env.Body = r2 != null ? Box(W / 2 - cw / 2, H / 2 - ch / 2, W / 2 + cw / 2, H / 2 + ch / 2) : null;
            },
        });
    }

}
