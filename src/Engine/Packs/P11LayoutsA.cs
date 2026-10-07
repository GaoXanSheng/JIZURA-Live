// 移植自 frontend/src/engine/11p_layoutsA.js（layout 表现包 A，pack = 'layoutsA'，28 条）。
//
// 契约与 JLayouts.cs 相同：fits(n) 筛台词；plan(rng, ctx, st) 抽参数存进 cut.params（键名是数据契约，
// planner/锁定行/AE 导出共用，一字不改）；Draw(env) 绘制并写回 env.Body（JS render(env) 的返回值）。
// RNG 消费顺序与 JS 逐行一致——rng.pick / rng.chance / rng.range / rng.int 的调用次序就是画面轨迹，
// JS 里靠 && / ?: 短路的抽取在 C# 里同样短路。
//
// 文件内的 JS 局部 helper（U/isPort/tin/tout/meas/box/splitK/brk/sealGlyphs/bubblePoly/…，JS L7-262
// 与各 layout 之间的 function 定义）在本文件内私有实现；Env 已有助手直接复用（rect/line/polyPartial/
// circle/arc/rrect/poly/blob + 渲染器装配的 drawItem）。
// 跨文件符号对位：J.fontsOf→JLayouts.FontsOf、J.measure→JLayouts.Measure、J.fitSize→JLayouts.FitSize、
// J.fastRow→JLayouts.FastRow、J.mainDraw→JLayouts.MainDraw、J.unionBB→JLayouts.UnionBb、
// J.centerBB→JLayouts.CenterBb、J.splitLines→JLayouts.SplitLines、J.glyphCount→JLayouts.GlyphCount、
// J.metrics.adv→JLayouts.MetricsAdv、J.segments/chunkText/isLatinText→Planner.*、
// J.romaji/fmtTime/lum/contrast/r/rs/rr→J.*、J.textPattern→JText.TextPattern。

using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace JizuraLive.Engine;

public static class P11LayoutsA
{
    private const string P = "layoutsA";

    private static void Reg0(string key, LayoutDef def) => Reg.Register("layout", key, def, P);

    // ================= 文件级 helper（JS L7-262 逐条对位） =================

    /// <summary>U(env)：min(W,H)。</summary>
    private static double U(Env env) => Math.Min(env.W, env.H);

    /// <summary>isPort(env)：竖帧判定。</summary>
    private static bool IsPort(Env env) => env.H > env.W * 1.08;

    /// <summary>strip(t)：去掉所有空白（含全角空格）。</summary>
    private static string Strip(string? t) => Regex.Replace(t ?? "", @"\s+", "");

    /// <summary>pad2(n)：两位补零。</summary>
    private static string Pad2(int n) => n.ToString("D2", CultureInfo.InvariantCulture);

    /// <summary>lineNo(env)：行号（1 起）。</summary>
    private static string LineNo(Env env) => Pad2(Math.Max(0, env.Cut?.Line ?? 0) + 1);

    private static string BodyF(Env env) => JLayouts.Font0(env.St, "body") ?? "gothic_med";
    private static string MonoF(Env env) => JLayouts.Font0(env.St, "mono") ?? "mono";

    /// <summary>tin(env, d, len, ease)：进场进度。</summary>
    private static double Tin(Env env, double d = 0, double len = 0.4, Func<double, double>? ease = null)
        => (ease ?? J.E.OutExpo)(J.Clamp((env.Lt - d) / Math.Max(0.01, len)));

    /// <summary>tout(env)：退场剩余量 1 - inCubic(pOut)。</summary>
    private static double Tout(Env env) => 1 - J.E.InCubic(env.POut);

    /// <summary>meas(text, font, size, o) = J.measure(Object.assign({text,font,size}, o))。</summary>
    private static TextMetrics Meas(string text, string font, double size, JObject? o = null)
    {
        var it = new JObject { ["text"] = text, ["font"] = font, ["size"] = size };
        if (o != null)
            foreach (var kv in o)
                it[kv.Key] = kv.Value?.DeepClone();
        return JLayouts.Measure(it);
    }

    /// <summary>box(x0,y0,x1,y1)。</summary>
    private static BBox Box(double x0, double y0, double x1, double y1) => new()
    {
        X0 = x0,
        Y0 = y0,
        X1 = x1,
        Y1 = y1,
        Cx = (x0 + x1) / 2,
        Cy = (y0 + y1) / 2,
        Boxes = new List<JObject>(),
    };

    /// <summary>smallSize(env)。</summary>
    private static double SmallSize(Env env) => J.Clamp(U(env) * 0.024, 14, 34);

    /// <summary>romajiOf(env)：含拉丁字母的歌词不做注音，否则罗马字大写。</summary>
    private static string? RomajiOf(Env env)
    {
        if (Regex.IsMatch(env.Cut?.Text ?? "", "[A-Za-z]")) return null;
        var r = J.Romaji(Strip(env.Cut?.Text));
        return string.IsNullOrEmpty(r) ? null : r.ToUpperInvariant();
    }

    private static bool HasLatin(string? t) => Regex.IsMatch(t ?? "", "[A-Za-z]");

    /// <summary>flat(t)：整行当一段；含拉丁就保留词间空格，纯日文去掉所有空白。</summary>
    private static string Flat(string? t) => HasLatin(t)
        ? Regex.Replace((t ?? "").Trim(), @"\s+", " ")
        : Strip(t);

    /// <summary>配色字段（JS 直接读 sc.xxx；缺失时给兜底色）。</summary>
    private static string Col(Scheme sc, string k) => sc.Str(k) ?? k switch
    {
        "bg" => "#000000",
        "fg" => "#FFFFFF",
        "sub" => "#9A9A9A",
        "ink" => "#141414",
        "dim" => "#3A3A3A",
        "accent" => "#FF3B30",
        _ => "#FFFFFF",
    };

    /// <summary>onCol(sc, plate)：色板上的可读文字色。</summary>
    private static string OnCol(Scheme sc, string plate)
    {
        var a = J.Contrast(Col(sc, "bg"), plate);
        var b = J.Contrast(Col(sc, "fg"), plate);
        if (Math.Max(a, b) >= 2.4) return a >= b ? Col(sc, "bg") : Col(sc, "fg");
        return J.Lum(plate) > 0.5 ? "#111111" : "#FFFFFF";
    }

    /// <summary>altCopy(env)：副文案（lineText → note → romaji → 行号）。</summary>
    private static string AltCopy(Env env)
    {
        var c = env.Cut!;
        if (!string.IsNullOrEmpty(c.LineText) && Strip(c.LineText) != Strip(c.Text)) return c.LineText;
        return !string.IsNullOrEmpty(c.Note) ? c.Note! : RomajiOf(env) ?? ("No." + LineNo(env));
    }

    /// <summary>isBad(c)：小假名/标点/长音符/空格。</summary>
    private static bool IsBad(string c) =>
        c.Length == 0 || J.IsSmallKana(c[0]) || J.IsPunct(c[0]) || c == "ー" || c == " ";

    // ---------------- 文本切分（JS L43-195：segBounds/split2/splitK/charUnits/brk） ----------------

    /// <summary>segBounds(t)：J.segments 的累积码点断点集合。</summary>
    private static HashSet<int> SegBounds(string t)
    {
        var outS = new HashSet<int>();
        var i = 0;
        try
        {
            foreach (var sg in Planner.Segments(t))
            {
                i += JLayouts.CpLen(sg);
                outS.Add(i);
            }
        }
        catch
        {
            // JS 里是 try/catch 吞掉（J.segments 失败就当没有断点）
        }
        return outS;
    }

    /// <summary>split2(word, force)：把词切成两半，优先自然断点。</summary>
    private static List<string> Split2(string word, bool force = false)
    {
        var chars = JLayouts.CodePoints(word);
        var n = chars.Count;
        var sb = SegBounds(word);
        var best = Math.Max(1, n / 2);
        var bs = -1e9;
        for (var c = 1; c < n; c++)
        {
            var a = chars[c - 1];
            var b = chars[c];
            // 首尾不能剩单个假名
            if (!force && ((c == 1 && !J.IsKanji(a[0])) || (c == n - 1 && !J.IsKanji(b[0])))) continue;
            var s = -Math.Abs(c - n / 2.0) * 0.9;
            if (a == " " || a == "\u3000" || (J.IsPunct(a[0]) && a != "\u30fc")) s += 5;
            if (J.IsHira(a[0]) && !J.IsHira(b[0]) && !IsBad(b)) s += 3;
            if (sb.Contains(c)) s += 2;
            if (IsBad(b)) s -= 6;
            if (J.IsKanji(a[0]) && J.IsKanji(b[0])) s -= 2;
            if (J.IsKanji(a[0]) && J.IsHira(b[0])) s -= sb.Contains(c) ? 0.5 : 2.5;
            if (J.IsHira(a[0]) && J.IsHira(b[0]) && !sb.Contains(c)) s -= 2.5;
            if (s > bs)
            {
                bs = s;
                best = c;
            }
        }
        if (!force && bs < -1.5 && !HasLatin(word)) return new List<string> { word };
        var outL = new List<string>();
        var head = string.Concat(chars.Take(best)).Trim();
        var tail = string.Concat(chars.Skip(best)).Trim();
        if (head.Length > 0) outL.Add(head);
        if (tail.Length > 0) outL.Add(tail);
        return outL;
    }

    /// <summary>JS splitK 的 part(ws)：按目标长度把词表均衡分组成 k 组。</summary>
    private static List<List<string>> SplitPart(List<string> ws, int k)
    {
        var pre = new List<double> { 0 };
        foreach (var w in ws) pre.Add(pre[^1] + JLayouts.GlyphCount(w) + 0.5);
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
                double s = 0;
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
            for (var c = start + 1; c <= n - (k - 1 - g); c++) Rec(c, g + 1, cuts.Append(c).ToList());
        }
        Rec(0, 0, new List<int>());
        if (best == null) return ws.Select(w => new List<string> { w }).ToList();
        var outL = new List<List<string>>();
        var prev2 = 0;
        foreach (var c in best)
        {
            outL.Add(ws.Skip(prev2).Take(c - prev2).ToList());
            prev2 = c;
        }
        return outL;
    }

    /// <summary>splitK(text, k, force)：把文本切成 k 个尽量均衡的块。</summary>
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
            // 绝不切进拉丁词内部
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
        var groups = SplitPart(words, k);
        // 组间长短差太多时，把最长词切开重新分组
        for (var it = 0; it < 3; it++)
        {
            var gl = groups.Select(g => g.Sum(JLayouts.GlyphCount)).ToList();
            var mx = gl.Max();
            var mn = gl.Min();
            if (mx <= mn * 1.7 + 1) break;
            var g = groups[gl.IndexOf(mx)];
            string? bw = null;
            foreach (var w in g)
                if (!hard.Contains(w) && JLayouts.GlyphCount(w) >= 3
                    && (bw == null || JLayouts.GlyphCount(w) > JLayouts.GlyphCount(bw)))
                    bw = w;
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
            groups = SplitPart(words, k);
        }
        return groups.Select(g => string.Join(latin ? " " : "", g)).ToList();
    }

    /// <summary>charUnits(text)：逐字拆分，小假名/标点/ー 挂到前一个字上。</summary>
    private static List<string> CharUnits(string text)
    {
        var outL = new List<string>();
        foreach (var ch in JLayouts.CodePoints(Strip(text)))
        {
            if (outL.Count > 0 && IsBad(ch)) outL[^1] += ch;
            else outL.Add(ch);
        }
        return outL;
    }

    /// <summary>brk(text, maxPer)：展示用断行（沿 chunk 边界均衡切）。</summary>
    private static string Brk(string text, int maxPer)
    {
        var t = (text ?? "").Trim();
        var n = JLayouts.GlyphCount(t);
        if (n <= maxPer) return t;
        return string.Join("\n", SplitK(t, (int)Math.Ceiling(n / (double)maxPer)));
    }

    // ---------------- 折线/小图形助手（JS L180-262、L1258-1300、L2053-2070、L2255-2274、L2596-2708、L3043-3056） ----------------

    /// <summary>segsPartial：多条折线按总长进度 e(0..1) 顺序画出。</summary>
    private static void SegsPartial(Env env, List<List<double[]>> segs, double e, string col, double lw,
        double a = 1, bool ghost = false)
    {
        if (e <= 0) return;
        var lens = new List<double>();
        foreach (var s in segs)
        {
            double L = 0;
            for (var i = 1; i < s.Count; i++)
                L += Math.Sqrt(Math.Pow(s[i][0] - s[i - 1][0], 2) + Math.Pow(s[i][1] - s[i - 1][1], 2));
            lens.Add(L);
        }
        var tot = lens.Sum();
        if (tot == 0) tot = 1;
        var rem = tot * J.Clamp(e);
        for (var i = 0; i < segs.Count && rem > 0; i++)
        {
            var k = Math.Min(1, rem / Math.Max(1e-6, lens[i]));
            env.PolyPartial(segs[i].ToArray(), k, col, lw, a, ghost);
            rem -= lens[i];
        }
    }

    /// <summary>dash：虚线直线，d 为段长 g 为间隔；只该在 main pass 里调。</summary>
    private static void Dash(Env env, double x0, double y0, double x1, double y1, double d, double g,
        string col, double lw, double a = 1)
    {
        var L = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
        if (L < 1) return;
        var n = (int)Math.Min(400, Math.Ceiling(L / (d + g)));
        var ux = (x1 - x0) / L;
        var uy = (y1 - y0) / L;
        for (var i = 0; i < n; i++)
        {
            var s = i * (d + g);
            var t = Math.Min(L, s + d);
            env.Line(new[]
            {
                new[] { x0 + ux * s, y0 + uy * s },
                new[] { x0 + ux * t, y0 + uy * t }
            }, col, lw, a, false);
        }
    }

    /// <summary>closeLoop(pts)：闭合折线（末尾补 pts[0]、pts[1]）。</summary>
    private static List<double[]> CloseLoop(List<double[]> pts)
    {
        var outL = new List<double[]>(pts);
        if (pts.Count >= 2)
        {
            outL.Add(pts[0]);
            outL.Add(pts[1]);
        }
        return outL;
    }

    /// <summary>arrowHead：尺寸线的箭头。</summary>
    private static void ArrowHead(Env env, double x, double y, double ang, double s, string col, double a)
    {
        var c = Math.Cos(ang);
        var sn = Math.Sin(ang);
        env.Poly(new[]
        {
            new[] { x, y },
            new[] { x - c * s + sn * s * 0.35, y - sn * s - c * s * 0.35 },
            new[] { x - c * s - sn * s * 0.35, y - sn * s + c * s * 0.35 }
        }, col, a, false);
    }

    /// <summary>magnifier：放大镜图标。</summary>
    private static void Magnifier(Env env, double x, double y, double r, string col, double lw, double a)
    {
        env.Circle(x - r * 0.15, y - r * 0.15, r * 0.62, null, col, lw, a, false);
        env.Line(new[]
        {
            new[] { x + r * 0.3, y + r * 0.3 },
            new[] { x + r * 0.8, y + r * 0.8 }
        }, col, lw * 1.2, a, false);
    }

    /// <summary>noteIcon：音符图标。</summary>
    private static void NoteIcon(Env env, double x, double y, double s, string col, double a)
    {
        env.Circle(x - s * 0.18, y + s * 0.2, s * 0.17, col, null, 0, a, false);
        env.Rect(x - s * 0.03, y - s * 0.32, s * 0.07, s * 0.52, col, a, false);
        env.Poly(new[]
        {
            new[] { x + s * 0.04, y - s * 0.32 },
            new[] { x + s * 0.3, y - s * 0.18 },
            new[] { x + s * 0.04, y - s * 0.12 }
        }, col, a, false);
    }

    /// <summary>sealGlyphs(text)：印章要刻的字。</summary>
    private static List<string> SealGlyphs(string text)
    {
        var g = JLayouts.CodePoints(Strip(text)).Where(c => !J.IsPunct(c[0]) && !J.IsSmallKana(c[0])).ToList();
        var kan = g.Where(c => J.IsKanji(c[0])).ToList();
        if (kan.Count >= 2 || (kan.Count == 1 && g.Count > 4)) return kan.Take(4).ToList();
        if (g.Count <= 4) return g.Count > 0 ? g : JLayouts.CodePoints(Strip(text)).Take(1).ToList();
        return g.Take(2).ToList();
    }

    /// <summary>气泡多边形（round/ellipse）与尾巴尖。</summary>
    private sealed class BubbleShape
    {
        public List<double[]> Pts = new();
        public double[] Tip = { 0, 0 };
    }

    /// <summary>bubblePoly(shape, cx, cy, w, h, size, tailSide)。</summary>
    private static BubbleShape BubblePoly(string shape, double cx, double cy, double w, double h, double size, int tailSide)
    {
        var x0 = cx - w / 2;
        var y0 = cy - h / 2;
        var x1 = cx + w / 2;
        var y1 = cy + h / 2;
        var tipX = cx + tailSide * w * 0.36;
        var tipY = y1 + size * 0.75;
        var shapeOut = new BubbleShape { Tip = new[] { tipX, tipY } };
        if (shape == "ellipse")
        {
            var rx = w / 2;
            var ry = h / 2;
            const int M = 56;
            var ta = (tailSide > 0 ? 62 : 118) * J.DEG;
            const double dA = 0.16;
            var tipDone = false;
            for (var i = 0; i < M; i++)
            {
                var a = i / (double)M * J.TAU;
                if (Math.Abs(a - ta) < dA)
                {
                    if (Math.Abs(a - ta) < J.TAU / M / 2 + 1e-6 || (!tipDone && a > ta))
                    {
                        shapeOut.Pts.Add(new[] { tipX, tipY });
                        tipDone = true;
                    }
                    continue;
                }
                shapeOut.Pts.Add(new[] { cx + Math.Cos(a) * rx, cy + Math.Sin(a) * ry });
            }
            if (!tipDone) shapeOut.Pts.Add(new[] { tipX, tipY });
            return shapeOut;
        }
        var r = Math.Min(h * 0.42, size * 0.9);
        const int seg = 6;
        void Corner(double ccx, double ccy, double a0)
        {
            for (var i = 0; i <= seg; i++)
            {
                var a = (a0 + 90.0 * i / seg) * J.DEG;
                shapeOut.Pts.Add(new[] { ccx + Math.Cos(a) * r, ccy + Math.Sin(a) * r });
            }
        }
        Corner(x1 - r, y0 + r, -90);
        Corner(x1 - r, y1 - r, 0);
        var bw = Math.Min(size * 0.55, w * 0.18);
        var bc = cx + tailSide * w * 0.2;
        if (tailSide > 0) shapeOut.Pts.AddRange(new[]
        {
            new[] { bc + bw / 2, y1 }, new[] { tipX, tipY }, new[] { bc - bw / 2, y1 }
        });
        else shapeOut.Pts.AddRange(new[]
        {
            new[] { bc + bw / 2, y1 }, new[] { tipX, tipY }, new[] { bc - bw / 2, y1 }
        });
        Corner(x0 + r, y1 - r, 90);
        Corner(x0 + r, y0 + r, 180);
        return shapeOut;
    }

    /// <summary>clipHalf(poly, nx, ny, c)：半平面裁剪，保留 nx*x + ny*y &lt;= c 的一侧。</summary>
    private static List<double[]> ClipHalf(List<double[]> poly, double nx, double ny, double c)
    {
        var outL = new List<double[]>();
        for (var i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            var da = nx * a[0] + ny * a[1] - c;
            var db = nx * b[0] + ny * b[1] - c;
            if (da <= 0) outL.Add(a);
            if ((da <= 0) != (db <= 0))
            {
                var t = da / (da - db);
                outL.Add(new[] { a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t });
            }
        }
        return outL;
    }

    /// <summary>pathOf(ctx, poly)：多边形路径（调用方负责 Dispose）。</summary>
    private static SKPath PathOf(List<double[]> poly)
    {
        var path = new SKPath();
        for (var i = 0; i < poly.Count; i++)
        {
            if (i == 0) path.MoveTo((float)poly[i][0], (float)poly[i][1]);
            else path.LineTo((float)poly[i][0], (float)poly[i][1]);
        }
        path.Close();
        return path;
    }

    /// <summary>ticketParts：票面（main）与副券（stub）轮廓。</summary>
    private sealed class TicketShape
    {
        public List<double[]> Main = new();
        public List<double[]> Stub = new();
    }

    /// <summary>ticketParts(x0, y0, w, h, px, nr, r)。</summary>
    private static TicketShape TicketParts(double x0, double y0, double w, double h, double px, double nr, double r)
    {
        const int seg = 6;
        var x1 = x0 + w;
        var y1 = y0 + h;
        List<double[]> Arc(double cx, double cy, double rr, double a0, double a1)
        {
            var o = new List<double[]>();
            for (var i = 0; i <= seg; i++)
            {
                var a = (a0 + (a1 - a0) * i / seg) * J.DEG;
                o.Add(new[] { cx + Math.Cos(a) * rr, cy + Math.Sin(a) * rr });
            }
            return o;
        }
        var main = new List<double[]>();
        main.AddRange(Arc(x0 + r, y0 + r, r, 180, 270));
        main.Add(new[] { px - nr, y0 });
        main.AddRange(Arc(px, y0, nr, 180, 90));
        main.Add(new[] { px, y1 - nr });
        main.AddRange(Arc(px, y1, nr, 270, 180));
        main.AddRange(Arc(x0 + r, y1 - r, r, 90, 180));
        var stub = new List<double[]>();
        stub.Add(new[] { px + nr, y0 });
        stub.AddRange(Arc(x1 - r, y0 + r, r, 270, 360));
        stub.AddRange(Arc(x1 - r, y1 - r, r, 0, 90));
        stub.Add(new[] { px + nr, y1 });
        stub.AddRange(Arc(px, y1, nr, 360, 270));
        stub.Add(new[] { px, y0 + nr });
        stub.AddRange(Arc(px, y0, nr, 90, 0));
        return new TicketShape { Main = main, Stub = stub };
    }

    // ---------------- 排版 memo / 墨迹框 / rowW（JS L214-262） ----------------

    private const string MemoSentinel = "\u0001layoutsA";
    private static readonly Dictionary<string, object> MemoCache = new();
    private static readonly object MemoLock = new();

    /// <summary>memo(key, fn)：排版搜索结果缓存；字体 metrics 重置时整表清空（靠 SENT 哨兵检测）。</summary>
    private static object Memo(string key, Func<object> fn)
    {
        lock (MemoLock)
        {
            if (!JText.Metrics.M.ContainsKey(MemoSentinel))
            {
                MemoCache.Clear();
                JText.Metrics.M[MemoSentinel] = 1;
            }
            if (MemoCache.TryGetValue(key, out var v)) return v;
            v = fn();
            if (MemoCache.Count > 400) MemoCache.Clear();
            MemoCache[key] = v;
            return v;
        }
    }

    /// <summary>字形墨迹范围，单位 em，相对 drawItem 用的居中/middle 锚点。</summary>
    private sealed class InkBox
    {
        public double L, R, T, B;
    }

    /// <summary>inkBox(font, ch)：JS 用 canvas actualBoundingBox；C# 用 SKFont 矢量轮廓包围盒折算。</summary>
    private static InkBox InkBoxOf(string font, string ch) => (InkBox)Memo("ink|" + font + "|" + ch, () =>
    {
        try
        {
            var f = JText.FaceFont(font, 100); // 缓存实例，禁 Dispose
            var cp = ch.Length > 0 ? char.ConvertToUtf32(ch, 0) : 0;
            var gid = f.GetGlyph(cp);
            using var path = f.GetGlyphPath(gid);
            if (path == null || path.IsEmpty) return (object)new InkBox { L = -0.4, R = 0.4, T = -0.45, B = 0.45 };
            f.GetFontMetrics(out var fm);
            var adv = (double)f.MeasureText(ch);
            var yMid = (-fm.Ascent - fm.Descent) / 2.0;
            var b = path.Bounds;
            return new InkBox
            {
                L = (b.Left - adv / 2) / 100.0,
                R = (b.Right - adv / 2) / 100.0,
                T = (yMid + b.Top) / 100.0,
                B = (yMid + b.Bottom) / 100.0,
            };
        }
        catch
        {
            return (object)new InkBox { L = -0.4, R = 0.4, T = -0.45, B = 0.45 };
        }
    });

    /// <summary>rowW(text, font, size)：整行宽（em 步进和 × 字号）。</summary>
    private static double RowW(string text, string font, double size)
    {
        var w = 0.0;
        foreach (var ch in JLayouts.CodePoints(text)) w += JLayouts.MetricsAdv(font, ch) * size;
        return w;
    }

    // ---------------- 松散读取小工具 ----------------

    /// <summary>JArray → List&lt;string&gt;（plan 里存的切块）。</summary>
    private static List<string> StrArr(JToken? t) => t is JArray a
        ? a.Where(x => x.Type == JTokenType.String).Select(x => (string)x!).ToList()
        : new List<string>();

    /// <summary>List&lt;string&gt; → JArray（plan 里的切块，键名是数据契约）。</summary>
    private static JArray JA(IEnumerable<string> xs) => new(xs.Select(x => (JToken)x).ToArray());

    private static double D(JObject p, string k, double d = 0) => JLayouts.D(p, k, d);
    private static int I(JObject p, string k, int d = 0) => JLayouts.I(p, k, d);
    private static string? S(JObject p, string k) => JLayouts.S(p, k);

    /// <summary>弧度 → 角度（JS ctx.rotate 收弧度，Skia RotateDegrees 收角度）。</summary>
    private static double Deg(double radians) => radians / J.DEG;

    // ================= 注册（JS 的 reg() 调用序，L264 起） =================

    /// <summary>幂等注册本文件全部 layout（顺序 = JS 源文件 reg() 调用序）。</summary>
    public static void Install()
    {
        RegisterLowerThird();
        RegisterCorners();
        RegisterStaircase();
        RegisterZigzag();
        RegisterArcTop();
        RegisterSpiral();
        RegisterGridCells();
        RegisterDropCap();
        RegisterJustified();
        RegisterFrameBox();
        RegisterBubble();
        RegisterSubtitleBar();
        RegisterTicker();
        RegisterSplitScreen();
        RegisterMirror();
        RegisterSideways();
        RegisterEdgeFrame();
        RegisterPerspective();
        RegisterHanko();
        RegisterGenkou();
        RegisterPanels();
        RegisterFilmstrip();
        RegisterQuote();
        RegisterRuler();
        RegisterSearchBar();
        RegisterChat();
        RegisterNotification();
        RegisterTicket();
    }

    // ---------------- lowerThird（JS L264-364） ----------------

    private static void RegisterLowerThird() => Reg0("lowerThird", new LayoutDef
    {
        Name = "下部テロップ",
        Tags = new[] { "editorial", "calm", "emotional" },
        W = 1.1,
        Fits = n => n <= 20,
        Portrait = 0.9,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(JLayouts.FontsOf(st, "display", "display", "serif")),
            ["side"] = rng.Pick(new[] { "left", "left", "right" }),
            ["bar"] = rng.Pick(new[] { "line", "tab", "line" }),
            ["label"] = rng.Pick(new[] { "romaji", "no", "copy" }),
            ["lift"] = rng.Range(0, 0.04),
        },
        Draw = env =>
        {
            var W = env.W;
            var H = env.H;
            var sc = env.Sc!;
            var p = env.P;
            var u = U(env);
            var port = IsPort(env);
            var t0 = env.Cut!.Text.Trim();
            var n = JLayouts.GlyphCount(t0);
            var text = port ? Brk(t0, 6) : n > 13 ? Brk(t0, (int)Math.Ceiling(n / 2.0)) : t0;
            var o = new JObject { ["track"] = 0.03, ["lead"] = 1.14 };
            var font = S(p, "font")!;
            var size = Math.Min(JLayouts.FitSize(text, font, W * 0.84, H * (port ? 0.24 : 0.3), o),
                u * (port ? 0.17 : 0.14));
            var m = Meas(text, font, size, o);
            var left = S(p, "side") != "right";
            var mx = W * 0.07;
            var barY = H * ((port ? 0.8 : 0.83) - D(p, "lift"));
            var th = Math.Max(3, size * 0.055);
            var cy = barY - size * 0.24 - m.H / 2;
            var x = left ? mx : W - mx;
            // accent bar 从屏幕边伸到文字末端，退场时缩回边缘
            var reach = mx + m.W + size * 0.45;
            var L = reach * Tin(env, 0, 0.55) * (1 - J.E.InCubic(env.POut));
            if (L > 1)
            {
                env.Rect(left ? 0 : W - L, barY, L, th, Col(sc, "accent"), 1, true);
                var sq = th * 2.6;
                var ex = left ? L : W - L;
                env.Rect(ex - sq / 2, barY + th / 2 - sq / 2, sq, sq, Col(sc, "accent"), 1, true);
            }
            // 歌词上方的小标签
            var ls = SmallSize(env);
            var la = Tin(env, 0.12, 0.4, J.E.OutCubic) * Tout(env);
            if (la > 0.01)
            {
                var ly = cy - m.H / 2 - size * 0.22 - ls * 0.7;
                var sl = (1 - la) * ls * 2 * (left ? -1 : 1);
                var tag = Pad2(Math.Max(0, env.Cut!.Line) + 1);
                var label = S(p, "label");
                var copy = label == "romaji" ? RomajiOf(env) ?? AltCopy(env) : label == "no" ? "LINE " + tag : AltCopy(env);
                if (copy.StartsWith("No.")) copy = J.FmtTime(env.Cut!.Start);
                var tm = Meas(tag, MonoF(env), ls, new JObject { ["track"] = 0.1 });
                var cx = x + sl;
                if (S(p, "bar") == "tab")
                {
                    var tw = tm.W + ls * 1.1;
                    var th2 = ls * 1.6;
                    env.Rect(left ? cx : cx - tw, ly - th2 / 2, tw, th2, Col(sc, "ink"), la, false);
                    env.DrawItem(new JObject
                    {
                        ["text"] = tag,
                        ["font"] = MonoF(env),
                        ["size"] = ls,
                        ["track"] = 0.1,
                        ["x"] = left ? cx + tw / 2 : cx - tw / 2,
                        ["y"] = ly,
                        ["color"] = OnCol(sc, Col(sc, "ink")),
                        ["alpha"] = la,
                        ["ghost"] = false,
                    });
                    cx += (tw + ls * 0.7) * (left ? 1 : -1);
                }
                else
                {
                    var q = ls * 0.55;
                    env.Rect(left ? cx : cx - q, ly - q / 2, q, q, Col(sc, "accent"), la, false);
                    cx += (q + ls * 0.6) * (left ? 1 : -1);
                    env.DrawItem(new JObject
                    {
                        ["text"] = tag,
                        ["font"] = MonoF(env),
                        ["size"] = ls,
                        ["track"] = 0.1,
                        ["align"] = left ? "left" : "right",
                        ["x"] = cx,
                        ["y"] = ly,
                        ["color"] = Col(sc, "fg"),
                        ["alpha"] = la,
                        ["ghost"] = false,
                    });
                    cx += (tm.W + ls * 0.8) * (left ? 1 : -1);
                }
                env.DrawItem(new JObject
                {
                    ["text"] = copy,
                    ["font"] = label == "copy" ? BodyF(env) : MonoF(env),
                    ["size"] = ls,
                    ["track"] = 0.12,
                    ["align"] = left ? "left" : "right",
                    ["x"] = cx,
                    ["y"] = ly,
                    ["color"] = Col(sc, "sub"),
                    ["alpha"] = la,
                    ["ghost"] = false,
                });
            }
            var bb = JLayouts.MainDraw(env, new JObject
            {
                ["text"] = text,
                ["font"] = font,
                ["size"] = size,
                ["x"] = x,
                ["y"] = cy,
                ["align"] = left ? "left" : "right",
                ["track"] = 0.03,
                ["lead"] = 1.14,
                ["color"] = Col(sc, "fg"),
            });
            env.Body = bb ?? Box(left ? x : x - m.W, cy - m.H / 2, left ? x + m.W : x, cy + m.H / 2);
        },
    });

    // ---------------- corners（JS L366-484） ----------------

    private static void RegisterCorners() => Reg0("corners", new LayoutDef
    {
        Name = "対角配置",
        Tags = new[] { "graphic", "editorial", "calm" },
        W = 1,
        Fits = n => n >= 2 && n <= 18,
        Plan = (rng, cut, st) => new JObject
        {
            ["chunks"] = JA(SplitK(cut.Text, 2, 2)),
            ["font"] = rng.Pick(JLayouts.FontsOf(st, "display", "serif")),
            ["diag"] = rng.Pick(new[] { "main", "main", "anti" }),
            ["link"] = rng.Pick(new[] { "elbow", "straight", "elbow" }),
            ["ratio"] = rng.Pick(new[] { 1.0, 1.0, 0.76 }),
        },
        Draw = env =>
        {
            var W = env.W;
            var H = env.H;
            var sc = env.Sc!;
            var p = env.P;
            var u = U(env);
            var port = IsPort(env);
            var ch = StrArr(p["chunks"]);
            if (ch.Count == 0) ch = SplitK(env.Cut!.Text, 2, 2);
            if (ch.Count < 2) ch = SplitK(env.Cut!.Text, 2, 2);
            var A0 = ch[0];
            var B0 = ch.Count > 1 ? string.Join(HasLatin(ch[0]) ? " " : "", ch.Skip(1)) : "";
            var maxPer = port ? 5 : 9;
            var A = Brk(A0, maxPer);
            var B = B0.Length > 0 ? Brk(B0, maxPer) : "";
            var o = new JObject { ["track"] = 0.02, ["lead"] = 1.1 };
            var bw = W * (port ? 0.84 : 0.58);
            var bh = H * (port ? 0.24 : 0.3);
            var ratio = D(p, "ratio", 1);
            var font = S(p, "font")!;
            var s = Math.Min(Math.Min(
                    JLayouts.FitSize(A, font, bw, bh, o),
                    B.Length > 0 ? JLayouts.FitSize(B, font, bw, bh, o) / ratio : 1e9),
                u * 0.22);
            var sA = s;
            var sB = s * ratio;
            var mA = Meas(A, font, sA, o);
            var mB = B.Length > 0 ? Meas(B, font, sB, o) : new TextMetrics { W = 0, H = 0 };
            var mx = W * 0.075;
            var my = H * (port ? 0.15 : 0.13);
            var aL = S(p, "diag") != "anti";
            var ax = aL ? mx : W - mx;
            var ay = my + mA.H / 2;
            var bx = aL ? W - mx : mx;
            var by = H - my - mB.H / 2;
            var Ab = Box(aL ? ax : ax - mA.W, ay - mA.H / 2, aL ? ax + mA.W : ax, ay + mA.H / 2);
            var Bb = Box(aL ? bx - mB.W : bx, by - mB.H / 2, aL ? bx : bx + mB.W, by + mB.H / 2);
            // 两角之间的连接线，撞到文字就改走中缝
            var g = u * 0.028;
            List<double[]> pts;
            if (S(p, "link") == "elbow")
            {
                var xb = aL ? Bb.X0 + sB * 0.5 : Bb.X1 - sB * 0.5;
                var clear = aL ? xb > Ab.X1 + g * 2 : xb < Ab.X0 - g * 2;
                if (clear)
                    pts = new List<double[]>
                    {
                        new[] { aL ? Ab.X1 + g : Ab.X0 - g, Ab.Cy },
                        new[] { xb, Ab.Cy },
                        new[] { xb, Bb.Y0 - g },
                    };
                else
                {
                    var xm = aL ? Math.Max(Ab.X0, Bb.X0) + sB * 0.5 : Math.Min(Ab.X1, Bb.X1) - sB * 0.5;
                    pts = new List<double[]> { new[] { xm, Ab.Y1 + g }, new[] { xm, Bb.Y0 - g } };
                }
            }
            else
                pts = new List<double[]>
                {
                    new[] { aL ? Ab.X1 : Ab.X0, Ab.Y1 + g },
                    new[] { aL ? Bb.X0 : Bb.X1, Bb.Y0 - g },
                };
            var e = Tin(env, env.Cut!.InDur * 0.5, 0.55, J.E.InOutCubic) * (1 - J.E.InCubic(env.POut));
            var lw = Math.Max(1.5, u * 0.0022);
            if (e > 0)
            {
                env.PolyPartial(pts.ToArray(), e, Col(sc, "sub"), lw, 1, false);
                var a0 = pts[0];
                env.Circle(a0[0], a0[1], u * 0.007, Col(sc, "bg"), Col(sc, "sub"), lw, Math.Min(1, e * 4), false);
                if (e > 0.97)
                {
                    var z = pts[^1];
                    env.Circle(z[0], z[1], u * 0.007, Col(sc, "accent"), null, 0, 1, false);
                }
                // 在最长一段上放小注记
                var bi = 0;
                var bl = 0.0;
                for (var i = 1; i < pts.Count; i++)
                {
                    var l = Math.Sqrt(Math.Pow(pts[i][0] - pts[i - 1][0], 2) + Math.Pow(pts[i][1] - pts[i - 1][1], 2));
                    if (l > bl)
                    {
                        bl = l;
                        bi = i;
                    }
                }
                var q0 = pts[bi - 1];
                var q1 = pts[bi];
                var ls = SmallSize(env) * 0.9;
                var horiz = Math.Abs(q1[1] - q0[1]) < Math.Abs(q1[0] - q0[0]) * 0.3;
                var mxp = (q0[0] + q1[0]) / 2;
                var myp = (q0[1] + q1[1]) / 2;
                var ca = J.Clamp((e - 0.5) * 3);
                var cap = "No." + LineNo(env) + "  " + (RomajiOf(env) ?? "");
                if (ca > 0 && bl > ls * 8)
                {
                    if (horiz)
                        env.DrawItem(new JObject
                        {
                            ["text"] = cap.Trim(),
                            ["font"] = MonoF(env),
                            ["size"] = ls,
                            ["track"] = 0.1,
                            ["x"] = mxp,
                            ["y"] = myp - ls * 1.1,
                            ["color"] = Col(sc, "sub"),
                            ["alpha"] = ca,
                            ["ghost"] = false,
                        });
                    else
                        env.DrawItem(new JObject
                        {
                            ["text"] = cap.Trim(),
                            ["font"] = MonoF(env),
                            ["size"] = ls,
                            ["track"] = 0.1,
                            ["align"] = aL ? "left" : "right",
                            ["x"] = mxp + (aL ? ls * 0.8 : -ls * 0.8) * (S(p, "link") == "straight" ? -1 : 1),
                            ["y"] = myp,
                            ["color"] = Col(sc, "sub"),
                            ["alpha"] = ca,
                            ["ghost"] = false,
                        });
                }
            }
            var nA = JLayouts.GlyphCount(A0);
            var bb = JLayouts.MainDraw(env, new JObject
            {
                ["text"] = A,
                ["font"] = font,
                ["size"] = sA,
                ["x"] = ax,
                ["y"] = ay,
                ["align"] = aL ? "left" : "right",
                ["track"] = 0.02,
                ["lead"] = 1.1,
                ["color"] = Col(sc, "fg"),
                ["mi"] = 0,
            });
            if (B.Length > 0)
                bb = JLayouts.UnionBb(bb, JLayouts.MainDraw(env, new JObject
                {
                    ["text"] = B,
                    ["font"] = font,
                    ["size"] = sB,
                    ["x"] = bx,
                    ["y"] = by,
                    ["align"] = aL ? "right" : "left",
                    ["track"] = 0.02,
                    ["lead"] = 1.1,
                    ["color"] = Col(sc, "fg"),
                    ["mi"] = Math.Min(8, nA + 2),
                }));
            env.Body = bb ?? Box(Math.Min(Ab.X0, Bb.X0), Ab.Y0, Math.Max(Ab.X1, Bb.X1), Bb.Y1);
        },
    });

    // ---------------- staircase（JS L486-585） ----------------

    /// <summary>staircase 的行内排版结果 {t, s, w, x, y}。</summary>
    private sealed class StepQ
    {
        public string T = "";
        public double S, W, X, Y;
    }

    private static void RegisterStaircase() => Reg0("staircase", new LayoutDef
    {
        Name = "階段",
        Tags = new[] { "graphic", "pop", "editorial" },
        W = 1,
        Fits = n => n >= 2 && n <= 18,
        Plan = (rng, cut, st) =>
        {
            var port = cut.H > cut.W * 1.08;
            var n = cut.N;
            var latin = HasLatin(cut.Text);
            var units = latin
                ? SplitK(cut.Text, port ? 6 : 4)
                : port
                    ? (n <= 9 ? CharUnits(cut.Text) : SplitK(cut.Text, Math.Min(6, (int)Math.Ceiling(n / 2.6))))
                    : (n <= 6 ? CharUnits(cut.Text) : SplitK(cut.Text, n <= 10 ? 3 : 4));
            return new JObject
            {
                ["units"] = JA(units),
                ["dir"] = port ? "down" : rng.Pick(new[] { "down", "down", "up" }),
                ["flip"] = !port && rng.Chance(0.22),
                ["shrink"] = rng.Range(0.82, 0.9),
                ["font"] = rng.Pick(JLayouts.FontsOf(st, "display", "display", "serif")),
                ["tread"] = rng.Pick(new[] { "line", "line", "none" }),
                ["nums"] = rng.Chance(0.55),
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
            var units = StrArr(p["units"]);
            if (units.Count == 0) units = new List<string> { Strip(env.Cut!.Text) };
            var k = units.Count;
            var font = S(p, "font")!;
            var shrink = D(p, "shrink");
            var r = k > 1 ? Math.Max(shrink != 0 ? shrink : 0.86, Math.Pow(0.5, 1.0 / (k - 1))) : 1;
            var up = S(p, "dir") == "up" && !port;
            var fx = up ? 1 : port ? 0.5 : 0.92;
            var L = new List<StepQ>();
            for (var i = 0; i < units.Count; i++)
            {
                var t = units[i];
                var s = 100 * Math.Pow(r, i);
                var w = Meas(t, font, s, new JObject { ["track"] = 0.02 }).W;
                double x = 0, y = 0;
                if (i > 0)
                {
                    var q = L[i - 1];
                    x = q.X + q.W * fx + q.S * 0.14;
                    y = q.Y + (q.S + s) / 2 * 1.05 * (up ? -1 : 1);
                }
                L.Add(new StepQ { T = t, S = s, W = w, X = x, Y = y });
            }
            double x0 = 1e9, x1 = -1e9, y0 = 1e9, y1 = -1e9;
            foreach (var q in L)
            {
                x0 = Math.Min(x0, q.X);
                x1 = Math.Max(x1, q.X + q.W);
                y0 = Math.Min(y0, q.Y - q.S / 2);
                y1 = Math.Max(y1, q.Y + q.S * 0.62);
            }
            var k2 = Math.Min(Math.Min(W * 0.86 / (x1 - x0), H * 0.78 / (y1 - y0)), u * 0.3 / 100);
            var ox = W / 2 - (x0 + x1) / 2 * k2;
            var oy = H / 2 - (y0 + y1) / 2 * k2;
            double X(StepQ q) => ox + q.X * k2;
            double Y(StepQ q) => oy + q.Y * k2;
            var flip = JLayouts.Truthy(p["flip"]);
            double Mirror(double xx) => flip ? W - xx : xx;
            // 沿台阶下缘的踏面/立面参考线
            if (S(p, "tread") != "none" || JLayouts.Truthy(p["nums"]))
            {
                var e = Tin(env, 0, Math.Max(0.5, env.Cut!.InDur * 1.6), J.E.InOutCubic) * (1 - J.E.InCubic(env.POut));
                var lw = Math.Max(2, 100 * k2 * 0.022);
                var pts = new List<double[]>();
                for (var i = 0; i < L.Count; i++)
                {
                    var q = L[i];
                    var yb = Y(q) + q.S * k2 * 0.58;
                    var gap = q.S * k2 * 0.14;
                    if (i == 0) pts.Add(new[] { X(q) - gap, yb });
                    if (i < k - 1)
                    {
                        var nx = L[i + 1];
                        var xr = X(nx) - gap / 2;
                        pts.Add(new[] { xr, yb });
                        pts.Add(new[] { xr, Y(nx) + nx.S * k2 * 0.58 });
                    }
                    else pts.Add(new[] { X(q) + q.W * k2 + gap, yb });
                }
                if (S(p, "tread") != "none" && e > 0)
                    env.PolyPartial(pts.Select(q => new[] { Mirror(q[0]), q[1] }).ToArray(),
                        e, Col(sc, "accent"), lw, 1, false);
                if (JLayouts.Truthy(p["nums"]) && e > 0)
                {
                    var ls = SmallSize(env) * 0.85;
                    for (var i = 0; i < L.Count; i++)
                    {
                        var q = L[i];
                        var a = J.Clamp((e * k - i) * 1.5);
                        if (a <= 0) continue;
                        var yb = Y(q) + q.S * k2 * 0.58;
                        env.DrawItem(new JObject
                        {
                            ["text"] = Pad2(i + 1),
                            ["font"] = MonoF(env),
                            ["size"] = ls,
                            ["align"] = flip ? "right" : "left",
                            ["x"] = Mirror(X(q)),
                            ["y"] = yb + ls * 1.1,
                            ["color"] = Col(sc, "sub"),
                            ["alpha"] = a,
                            ["ghost"] = false,
                        });
                    }
                }
            }
            BBox? bb = null;
            for (var i = 0; i < L.Count; i++)
            {
                var q = L[i];
                var s = q.S * k2;
                var xl = X(q);
                bb = JLayouts.UnionBb(bb, JLayouts.MainDraw(env, new JObject
                {
                    ["text"] = q.T,
                    ["font"] = font,
                    ["size"] = s,
                    ["x"] = flip ? W - xl - q.W * k2 : xl,
                    ["y"] = Y(q),
                    ["align"] = "left",
                    ["track"] = 0.02,
                    ["color"] = Col(sc, "fg"),
                    ["mi"] = i * 2,
                }));
            }
            env.Body = bb ?? Box(W / 2 - (x1 - x0) * k2 / 2, H / 2 - (y1 - y0) * k2 / 2,
                W / 2 + (x1 - x0) * k2 / 2, H / 2 + (y1 - y0) * k2 / 2);
        },
    });

    // ---------------- zigzag（JS L587-648） ----------------

    private static void RegisterZigzag() => Reg0("zigzag", new LayoutDef
    {
        Name = "ジグザグ",
        Tags = new[] { "pop", "graphic" },
        W = 0.9,
        Fits = n => n >= 3 && n <= 16,
        Plan = (rng, cut, st) =>
        {
            var port = cut.H > cut.W * 1.08;
            return new JObject
            {
                ["font"] = rng.Pick(JLayouts.FontsOf(st, "display")),
                ["orient"] = port && cut.N > 5 ? "v" : "h",
                ["amp"] = rng.Range(0.24, 0.34),
                ["phase"] = rng.Pick(new[] { 1.0, -1.0 }),
                ["rails"] = rng.Pick(new[] { "under", "both", "under", "over" }),
                ["tilt"] = rng.Chance(0.35),
            };
        },
        Draw = env =>
        {
            var W = env.W;
            var H = env.H;
            var sc = env.Sc!;
            var p = env.P;
            var u = U(env);
            var chars = HasLatin(env.Cut!.Text)
                ? JLayouts.CodePoints(Regex.Replace(env.Cut!.Text.Trim(), @"\s+", " "))
                : JLayouts.CodePoints(Strip(env.Cut!.Text));
            var n = chars.Count;
            var v = S(p, "orient") == "v";
            var font = S(p, "font")!;
            var amp = D(p, "amp");
            var phase = D(p, "phase");
            var size = Math.Min((v ? H * 0.78 : W * 0.8) / n / 1.08, u * 0.2);
            var step = size * 1.08;
            var A = size * amp;
            double[] Pos(int i)
            {
                var s = (i % 2 != 0 ? 1 : -1) * phase;
                var kk = i - (n - 1) / 2.0;
                return v ? new[] { W / 2 + s * A, H / 2 + kk * step } : new[] { W / 2 + kk * step, H / 2 + s * A };
            }
            // 折线轨道贴在字形外侧，向两端延伸到版心边距
            var off = size * (0.52 + amp * 0.9);
            var e = Tin(env, 0, Math.Max(0.45, env.Cut!.InDur * 1.5), J.E.InOutCubic) * (1 - J.E.InCubic(env.POut));
            if (e > 0)
            {
                var lw = Math.Max(2, size * 0.03);
                var lim = v ? H * 0.04 : W * 0.04;
                var ext = (int)Math.Ceiling(((v ? H : W) / 2 - n * step / 2 - lim) / step);
                List<double[]> Rail(double sgn)
                {
                    var pts = new List<double[]>();
                    for (var i = -ext; i <= n - 1 + ext; i++)
                    {
                        var q = Pos(i);
                        var pt = v ? new[] { q[0] + sgn * off, q[1] } : new[] { q[0], q[1] + sgn * off };
                        var c = v ? pt[1] : pt[0];
                        if (c < lim || c > (v ? H : W) - lim) continue;
                        pts.Add(pt);
                    }
                    return pts;
                }
                var rails = S(p, "rails");
                var sides = rails == "both" ? new[] { 1.0, -1.0 } : rails == "over" ? new[] { -1.0 } : new[] { 1.0 };
                for (var k = 0; k < sides.Length; k++)
                {
                    var pts = Rail(sides[k]);
                    if (pts.Count < 2) continue;
                    var col = k == 0 ? Col(sc, "accent") : Col(sc, "sub");
                    env.PolyPartial(pts.ToArray(), e, col, lw, 1, false);
                    var tip = Math.Min(pts.Count - 1, (int)Math.Floor(e * (pts.Count - 1)));
                    env.Circle(pts[0][0], pts[0][1], lw * 2.2, col, null, 0, 1, false);
                    if (e > 0.98) env.Circle(pts[tip][0], pts[tip][1], lw * 2.2, col, null, 0, 1, false);
                }
            }
            BBox? bb = null;
            for (var i = 0; i < chars.Count; i++)
            {
                var ch = chars[i];
                var q = Pos(i);
                if (ch == " ") continue;
                var tilt = JLayouts.Truthy(p["tilt"]);
                var rot = (tilt ? (i % 2 != 0 ? 1 : -1) * phase * 6 * (v ? -1 : 1) : 0)
                          + (v && (J.VertRotate.Contains(ch) || J.IsLatin(ch[0])) ? 90 : 0);
                bb = JLayouts.UnionBb(bb, JLayouts.MainDraw(env, new JObject
                {
                    ["text"] = ch,
                    ["font"] = font,
                    ["size"] = size,
                    ["x"] = q[0],
                    ["y"] = q[1],
                    ["rot"] = rot,
                    ["color"] = Col(sc, "fg"),
                    ["mi"] = i,
                }));
            }
            env.Body = bb ?? (v
                ? Box(W / 2 - A - size / 2, H / 2 - step * n / 2, W / 2 + A + size / 2, H / 2 + step * n / 2)
                : Box(W / 2 - step * n / 2, H / 2 - A - size / 2, W / 2 + step * n / 2, H / 2 + A + size / 2));
        },
    });

    // ---------------- arcTop（JS L650-736） ----------------

    private static void RegisterArcTop() => Reg0("arcTop", new LayoutDef
    {
        Name = "虹の弧",
        Tags = new[] { "pop", "emotional", "graphic" },
        W = 0.9,
        Fits = n => n >= 3 && n <= 16,
        Plan = (rng, cut, st) =>
        {
            var port = cut.H > cut.W * 1.08;
            return new JObject
            {
                ["font"] = rng.Pick(JLayouts.FontsOf(st, "display", "serif", "display")),
                ["span"] = port ? rng.Range(150, 190) : rng.Range(105, 145),
                ["guide"] = rng.Pick(new[] { "double", "ticks", "double" }),
                ["under"] = rng.Pick(new[] { "copy", "romaji", "no" }),
            };
        },
        Draw = env =>
        {
            var W = env.W;
            var H = env.H;
            var sc = env.Sc!;
            var p = env.P;
            var u = U(env);
            var chars = JLayouts.CodePoints(env.Cut!.Text.Trim());
            var n = chars.Count;
            var font = S(p, "font")!;
            var spanDeg = Math.Min(D(p, "span"), Math.Max(50, n * 30));
            var half = spanDeg / 2 * J.DEG;
            var dth = spanDeg * J.DEG / n;
            var k = dth / 1.1;
            var wid = half < Math.PI / 2 ? 2 * Math.Sin(half) : 2;
            var hollow = half >= 70 * J.DEG;
            var R = Math.Min(Math.Min(W * 0.86 / (wid + k),
                    H * 0.66 / (1 - Math.Cos(half) + k * 1.3 + (hollow ? 0 : 0.18))),
                u * 0.21 / k);
            var size = R * k;
            var ls = SmallSize(env);
            // 弧和下方文案作为一个整体在竖直方向居中
            var topOff = -(R + size * 0.8);
            var endY = -R * Math.Cos(half) + size * 0.55;
            var capY = hollow ? Math.Max(0, -R * Math.Cos(half) - R * 0.1) : endY + ls * 1.6;
            var botOff = Math.Max(endY, capY + ls * 2.2);
            var cx = W / 2;
            var cy = H / 2 - (topOff + botOff) / 2;
            var e = Tin(env, 0, 0.7, J.E.InOutCubic) * (1 - J.E.InCubic(env.POut));
            var lw = Math.Max(1.2, size * 0.018);
            var a0 = -90 - spanDeg / 2 - 3;
            var a1 = -90 + spanDeg / 2 + 3;
            if (e > 0)
            {
                var sp = (a1 - a0) / 2 * e;
                var guide = S(p, "guide");
                if (guide == "double")
                {
                    env.Arc(cx, cy, R + size * 0.74, -90 - sp, -90 + sp, Col(sc, "sub"), lw, 0.9, false);
                    env.Arc(cx, cy, R - size * 0.72, -90 - sp, -90 + sp, Col(sc, "sub"), lw, 0.6, false);
                }
                else
                {
                    env.Arc(cx, cy, R - size * 0.72, -90 - sp, -90 + sp, Col(sc, "sub"), lw, 0.8, false);
                    for (var i = 0; i <= n; i++)
                    {
                        var th = -90 + (i - n / 2.0) * spanDeg / n;
                        if (Math.Abs(th + 90) > sp + 0.01) continue;
                        var c = Math.Cos(th * J.DEG);
                        var s = Math.Sin(th * J.DEG);
                        var r0 = R - size * 0.72;
                        var r1 = r0 - size * (i % 2 != 0 ? 0.12 : 0.22);
                        env.Line(new[]
                        {
                            new[] { cx + c * r0, cy + s * r0 },
                            new[] { cx + c * r1, cy + s * r1 }
                        }, Col(sc, "sub"), lw, 0.8, false);
                    }
                }
                env.Circle(cx + Math.Cos((-90 - sp) * J.DEG) * (R + size * 0.74),
                    cy + Math.Sin((-90 - sp) * J.DEG) * (R + size * 0.74), size * 0.05, Col(sc, "accent"), null, 0, 1, false);
                env.Circle(cx + Math.Cos((-90 + sp) * J.DEG) * (R + size * 0.74),
                    cy + Math.Sin((-90 + sp) * J.DEG) * (R + size * 0.74), size * 0.05, Col(sc, "accent"), null, 0, 1, false);
            }
            // 弧下的说明文案
            var ca = Tin(env, 0.25, 0.4, J.E.OutCubic) * Tout(env);
            if (ca > 0.01)
            {
                var under = S(p, "under");
                var copy = under == "romaji"
                    ? RomajiOf(env) ?? AltCopy(env)
                    : under == "no"
                        ? "No." + LineNo(env) + "  —  " + J.FmtTime(env.Cut!.Start)
                        : AltCopy(env);
                var maxW = hollow ? 2 * (R - size) * 0.8 : W * 0.7;
                var cs = Math.Min(ls * 1.15,
                    JLayouts.FitSize(copy, BodyF(env), maxW, ls * 2, new JObject { ["track"] = 0.12 }));
                env.DrawItem(new JObject
                {
                    ["text"] = copy,
                    ["font"] = BodyF(env),
                    ["size"] = cs,
                    ["track"] = 0.12,
                    ["x"] = cx,
                    ["y"] = cy + capY,
                    ["color"] = Col(sc, "sub"),
                    ["alpha"] = ca,
                    ["ghost"] = false,
                });
                var rw = Math.Min(maxW * 0.5, size * 1.4) * ca;
                env.Line(new[]
                {
                    new[] { cx - rw / 2, cy + capY + cs * 1.1 },
                    new[] { cx + rw / 2, cy + capY + cs * 1.1 }
                }, Col(sc, "accent"), Math.Max(2, lw * 1.4), ca, false);
            }
            BBox? bb = null;
            for (var i = 0; i < chars.Count; i++)
            {
                var ch = chars[i];
                if (ch == " " || ch == "\u3000") continue;
                var th = -90 + (i - (n - 1) / 2.0) * spanDeg / n;
                var x = cx + Math.Cos(th * J.DEG) * R;
                var y = cy + Math.Sin(th * J.DEG) * R;
                bb = JLayouts.UnionBb(bb, JLayouts.MainDraw(env, new JObject
                {
                    ["text"] = ch,
                    ["font"] = font,
                    ["size"] = size,
                    ["x"] = x,
                    ["y"] = y,
                    ["rot"] = th + 90,
                    ["color"] = Col(sc, "fg"),
                    ["mi"] = i,
                }));
            }
            env.Body = bb ?? Box(cx - R * wid / 2 - size / 2, cy - R - size / 2,
                cx + R * wid / 2 + size / 2, cy + endY);
        },
    });

    // ---------------- spiral（JS L738-831） ----------------

    /// <summary>spiral 的字符序列项 {c, main, i}。</summary>
    private sealed class SpiralItem
    {
        public string C = "";
        public bool Main;
        public int I;
    }

    private static void RegisterSpiral() => Reg0("spiral", new LayoutDef
    {
        Name = "螺旋",
        Tags = new[] { "emotional", "graphic", "calm" },
        W = 0.8,
        Fits = n => n >= 2 && n <= 16,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(JLayouts.FontsOf(st, "display", "serif")),
            ["dir"] = rng.Pick(new[] { 1.0, -1.0 }),
            ["speed"] = rng.Range(4, 9),
            ["guide"] = rng.Chance(0.7),
            ["fill"] = cut.N <= 7 ? "repeat" : cut.N <= 11 ? rng.Pick(new[] { "repeat", "single" }) : "single",
        },
        Draw = env =>
        {
            var W = env.W;
            var H = env.H;
            var sc = env.Sc!;
            var p = env.P;
            var u = U(env);
            var chars = HasLatin(env.Cut!.Text)
                ? JLayouts.CodePoints(Regex.Replace(env.Cut!.Text.Trim(), @"\s+", " "))
                : JLayouts.CodePoints(Strip(env.Cut!.Text));
            var n = chars.Count;
            var font = S(p, "font")!;
            var dir = D(p, "dir");
            var speed = D(p, "speed");
            var fill = S(p, "fill");
            var seq = new List<SpiralItem>();
            for (var i = 0; i < chars.Count; i++) seq.Add(new SpiralItem { C = chars[i], Main = true, I = i });
            if (fill == "repeat")
            {
                var reps = Math.Max(1, (int)Math.Ceiling(20.0 / (n + 1)) - 1);
                for (var r = 0; r < reps && seq.Count < 40; r++)
                {
                    seq.Add(new SpiralItem { C = "・", Main = false });
                    foreach (var ch in chars) seq.Add(new SpiralItem { C = ch, Main = false });
                }
            }
            var N = seq.Count;
            var f = fill == "repeat" ? 0.8 : 0.58;
            var cf = Math.Min(0.42, 0.92 * Math.Sqrt(5.82 * f / N));
            var dth = cf * 1.08;
            var TH = N * dth;
            var R0 = u * 0.45 / (1 + cf * 0.55);
            double RAt(double th) => R0 * (1 - f * th / TH);
            // dir>0 时歌词排在顶部，字头朝外、顺时针；dir<0 排在底部，字头朝内、逆时针
            var mid = dir > 0 ? -90 : 90;
            var baseA = mid - dir * Math.Min((n - 1) * dth / 2 / J.DEG, 70);
            var drift = (env.Ltb - env.Cut!.Dur / 2) * speed * dir;
            // 静止状态下把字形云整体摆到屏幕中央
            double bx0 = 1e9, bx1 = -1e9, by0 = 1e9, by1 = -1e9;
            for (var j = 0; j < seq.Count; j++)
            {
                var th = j * dth;
                var rr = RAt(th);
                var sz = cf * rr * 0.5;
                var ph = (baseA + dir * th / J.DEG) * J.DEG;
                var x = Math.Cos(ph) * rr;
                var y = Math.Sin(ph) * rr;
                bx0 = Math.Min(bx0, x - sz);
                bx1 = Math.Max(bx1, x + sz);
                by0 = Math.Min(by0, y - sz);
                by1 = Math.Max(by1, y + sz);
            }
            var cx = W / 2 - (bx0 + bx1) / 2;
            var cy = H / 2 - (by0 + by1) / 2;
            var rot0 = baseA + drift;
            var outA = Tout(env);
            if (JLayouts.Truthy(p["guide"]))
            {
                var e = Tin(env, 0, 0.9, J.E.InOutCubic) * outA;
                if (e > 0)
                {
                    var pts = new List<double[]>();
                    const int M = 100;
                    for (var k = 0; k <= M; k++)
                    {
                        var th = k / (double)M * (TH - dth * 0.5);
                        var rr = RAt(th) * (1 + cf * 0.62);
                        var ph = (rot0 + dir * th / J.DEG) * J.DEG;
                        pts.Add(new[] { cx + Math.Cos(ph) * rr, cy + Math.Sin(ph) * rr });
                    }
                    env.PolyPartial(pts.ToArray(), e, Col(sc, "sub"), Math.Max(1.2, u * 0.0016), 0.55, false);
                }
            }
            var dotE = J.E.OutBack(J.Clamp(env.Lt / 0.35), 2) * outA;
            env.Circle(cx, cy, u * 0.008 * dotE, Col(sc, "accent"), null, 0, 1, false);
            BBox? bb = null;
            for (var j = 0; j < seq.Count; j++)
            {
                var q = seq[j];
                var th = j * dth;
                var rr = RAt(th);
                var size = cf * rr;
                var ph = rot0 + dir * th / J.DEG;
                var x = cx + Math.Cos(ph * J.DEG) * rr;
                var y = cy + Math.Sin(ph * J.DEG) * rr;
                var rot = dir > 0 ? ph + 90 : ph - 90;
                if (q.C == " ") continue;
                if (q.Main)
                    bb = JLayouts.UnionBb(bb, JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = q.C,
                        ["font"] = font,
                        ["size"] = size,
                        ["x"] = x,
                        ["y"] = y,
                        ["rot"] = rot,
                        ["color"] = Col(sc, "fg"),
                        ["mi"] = q.I,
                        ["noHold"] = true,
                    }));
                else
                {
                    var a = J.Clamp((env.Lt - 0.15 - (j - n) * 0.025) / 0.25) * outA * J.Lerp(0.22, 0.7, rr / R0);
                    if (a > 0.01)
                        env.DrawItem(new JObject
                        {
                            ["text"] = q.C,
                            ["font"] = font,
                            ["size"] = size,
                            ["x"] = x,
                            ["y"] = y,
                            ["rot"] = rot,
                            ["color"] = Col(sc, "sub"),
                            ["alpha"] = a,
                            ["ghost"] = false,
                        });
                }
            }
            env.Body = bb ?? Box(cx + bx0, cy + by0, cx + bx1, cy + by1);
        },
    });

    // ---------------- gridCells（JS L833-903） ----------------

    private static void RegisterGridCells() => Reg0("gridCells", new LayoutDef
    {
        Name = "升目",
        Tags = new[] { "graphic", "editorial", "pop" },
        W = 1,
        Fits = n => n >= 2 && n <= 18,
        Treat = "safe",
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(JLayouts.FontsOf(st, "display", "serif")),
            ["gap"] = rng.Pick(new[] { 0.0, 0.0, 0.1, 0.16 }),
            ["acc"] = rng.Int(0, 99),
            ["fill"] = rng.Pick(new[] { "outline", "outline", "ink" }),
            ["nums"] = rng.Chance(0.6),
        },
        Draw = env =>
        {
            var W = env.W;
            var H = env.H;
            var sc = env.Sc!;
            var p = env.P;
            var u = U(env);
            var port = IsPort(env);
            var chars = JLayouts.CodePoints(Strip(env.Cut!.Text));
            var n = chars.Count;
            var font = S(p, "font")!;
            var maxCols = port ? (n <= 8 ? 4 : 5) : (n <= 10 ? 10 : 8);
            var rows = (int)Math.Ceiling(n / (double)Math.Min(n, maxCols));
            var cols = (int)Math.Ceiling(n / (double)rows);
            var g = D(p, "gap");
            var cell = Math.Min(Math.Min(W * 0.86 / (cols + (cols - 1) * g), H * 0.72 / (rows + (rows - 1) * g)), u * 0.3);
            var stp = cell * (1 + g);
            var gw = cols * cell + (cols - 1) * cell * g;
            var gh = rows * cell + (rows - 1) * cell * g;
            var x0 = W / 2 - gw / 2;
            var y0 = H / 2 - gh / 2;
            var kan = chars.Select((ch, i) => J.IsKanji(ch[0]) ? i : -1).Where(i => i >= 0).ToList();
            var ok = chars.Select((ch, i) => !J.IsPunct(ch[0]) && !J.IsSmallKana(ch[0]) ? i : -1).Where(i => i >= 0).ToList();
            var acc = I(p, "acc");
            var ai = kan.Count > 0 ? kan[acc % kan.Count]
                : ok.Count > 0 ? ok[acc % ok.Count] : acc % Math.Max(1, n);
            var outA = Tout(env);
            var lw = Math.Max(1.5, cell * 0.012);
            var plate = S(p, "fill") == "ink";
            var total = rows * cols;
            for (var i = 0; i < total; i++)
            {
                var cx = x0 + i % cols * stp;
                var cy = y0 + (int)Math.Floor(i / (double)cols) * stp;
                var d = i * 0.03;
                var e = Tin(env, d, 0.35, J.E.InOutCubic) * outA;
                if (e <= 0) continue;
                var empty = i >= n;
                if (i == ai || (plate && !empty))
                {
                    var q = J.E.OutBack(J.Clamp((env.Lt - d - 0.08) / 0.28), 1.6) * outA;
                    var col = i == ai ? Col(sc, "accent") : Col(sc, "ink");
                    if (q > 0)
                        env.Rect(cx + cell / 2 * (1 - q), cy + cell / 2 * (1 - q), cell * q, cell * q, col, 1, i == ai);
                }
                var loop = new[]
                {
                    new[] { cx, cy }, new[] { cx + cell, cy }, new[] { cx + cell, cy + cell },
                    new[] { cx, cy + cell }, new[] { cx, cy }
                };
                env.PolyPartial(loop, e, empty ? Col(sc, "sub") : Col(sc, "fg"), lw, empty ? 0.35 : 0.9, false);
                if (JLayouts.Truthy(p["nums"]) && !empty)
                {
                    var ns = Math.Max(10, cell * 0.1);
                    env.DrawItem(new JObject
                    {
                        ["text"] = Pad2(i + 1),
                        ["font"] = MonoF(env),
                        ["size"] = ns,
                        ["align"] = "left",
                        ["x"] = cx + ns * 0.6,
                        ["y"] = cy + ns * 1.1,
                        ["color"] = i == ai ? OnCol(sc, Col(sc, "accent")) : plate ? OnCol(sc, Col(sc, "ink")) : Col(sc, "sub"),
                        ["alpha"] = J.Clamp(e * 1.5 - 0.5) * 0.85,
                        ["ghost"] = false,
                    });
                }
            }
            BBox? bb = null;
            for (var i = 0; i < chars.Count; i++)
            {
                var cx = x0 + i % cols * stp + cell / 2;
                var cy = y0 + (int)Math.Floor(i / (double)cols) * stp + cell / 2;
                var col = i == ai ? OnCol(sc, Col(sc, "accent")) : plate ? OnCol(sc, Col(sc, "ink")) : Col(sc, "fg");
                bb = JLayouts.UnionBb(bb, JLayouts.MainDraw(env, new JObject
                {
                    ["text"] = chars[i],
                    ["font"] = font,
                    ["size"] = cell * 0.64,
                    ["x"] = cx,
                    ["y"] = cy + cell * 0.02,
                    ["color"] = col,
                    ["mi"] = i,
                }));
            }
            env.Body = bb ?? Box(x0, y0, x0 + gw, y0 + gh);
        },
    });

    // ---------------- dropCap（JS L905-1047） ----------------

    /// <summary>dropCap 的解析式排版结果。</summary>
    private sealed class DropCapFit
    {
        public double S;
        public int Cl;
        public List<string> Lines = new();
        public double CapS;
        public double Gap;
    }

    private static void RegisterDropCap() => Reg0("dropCap", new LayoutDef
    {
        Name = "大きな頭文字",
        Tags = new[] { "editorial", "emotional", "calm" },
        W = 1,
        EnterBias = new JObject { ["blur"] = 1.4, ["wipe"] = 1.3, ["type"] = 1.2 },
        Fits = n => n >= 3 && n <= 24,
        Portrait = 0.8,
        Plan = (rng, cut, st) => new JObject
        {
            ["capFont"] = rng.Pick(JLayouts.FontsOf(st, "serif", "display")),
            ["font"] = rng.Pick(JLayouts.FontsOf(st, "serif", "body", "display")),
            ["cap"] = rng.Pick(new[] { "fill", "accent", "outline" }),
            ["rules"] = rng.Chance(0.65),
            ["meta"] = rng.Chance(0.7),
        },
        Draw = env =>
        {
            var W = env.W;
            var H = env.H;
            var sc = env.Sc!;
            var p = env.P;
            var u = U(env);
            var port = IsPort(env);
            var arr = JLayouts.CodePoints(env.Cut!.Text.Trim());
            var cap = arr.Count > 0 ? arr[0] : "";
            var rest = string.Concat(arr.Skip(1)).Trim();
            var nR = JLayouts.GlyphCount(rest);
            var colW = W * (port ? 0.84 : 0.66);
            const double lead = 1.3;
            const double tr = 0.04;
            var capFont = S(p, "capFont")!;
            var font = S(p, "font")!;
            var capAdv0 = JLayouts.MetricsAdv(capFont, cap);
            var capAdv = capAdv0 != 0 ? capAdv0 : 1;
            // 解析式选断行和字号：每个候选切法都取能放下的最大字号，结果 memo 缓存
            double W1(string t) => RowW(t, font, 1) + Math.Max(0, JLayouts.CpLen(t) - 1) * tr;
            var L = (DropCapFit)Memo(string.Join("|", "dc", rest, W, H, font, capFont, cap), () =>
            {
                var cands = new List<List<string>>();
                var seen = new HashSet<string>();
                for (var m = 1; m <= Math.Max(1, nR); m++)
                {
                    var t = JLayouts.SplitLines(rest, m);
                    if (!seen.Add(t)) continue;
                    cands.Add(t.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList());
                }
                var nat = new HashSet<int>();
                var acc2 = 0;
                foreach (var cch in Planner.ChunkText(rest))
                {
                    acc2 += JLayouts.GlyphCount(cch);
                    nat.Add(acc2);
                }
                DropCapFit? best = null;
                var bs = -1.0;
                foreach (var lines in cands)
                {
                    double q = 1;
                    var cum = 0;
                    for (var i = 0; i < lines.Count; i++)
                    {
                        cum += JLayouts.GlyphCount(lines[i]);
                        if (i < lines.Count - 1 && !nat.Contains(cum) && !HasLatin(lines[i])) q *= 0.82;
                    }
                    if (nR >= 4 && lines.Count < 2) continue;
                    if (nR >= 4 && lines.Any(l => JLayouts.GlyphCount(l) < 2)) continue;
                    if (nR <= 3 && lines.Count > 1) continue;
                    foreach (var cl in nR <= 10 ? new[] { 2 } : new[] { 3, 2 })
                    {
                        var kc = ((cl - 1) * lead + 1) / 0.86;
                        var nl = Math.Max(lines.Count, cl);
                        var sm = Math.Min(u * 0.12, H * 0.62 / ((nl - 1) * lead + 1));
                        for (var i = 0; i < lines.Count; i++)
                            sm = Math.Min(sm, i < cl ? colW / (W1(lines[i]) + capAdv * kc + 0.5) : colW / W1(lines[i]));
                        var score = sm * q * (1 - 0.1 * Math.Max(0, lines.Count - cl));
                        if (score > bs)
                        {
                            bs = score;
                            best = new DropCapFit { S = sm, Cl = cl, Lines = lines, CapS = sm * kc, Gap = sm * 0.5 };
                        }
                    }
                }
                best ??= new DropCapFit
                {
                    S = u * 0.06, Cl = 2, Lines = new List<string> { rest },
                    CapS = u * 0.06 * 2.67, Gap = u * 0.03
                };
                return best;
            });
            var s = L.S;
            var capLines = L.Cl;
            var capS = L.CapS;
            var capW = capAdv * capS;
            var nl = L.Lines.Count;
            var besideOnly = nl <= capLines;
            var usedW = 0.0;
            for (var i = 0; i < L.Lines.Count; i++)
                usedW = Math.Max(usedW, (i < capLines ? capW + L.Gap : 0) + W1(L.Lines[i]) * L.S);
            usedW = Math.Max(usedW, capW + L.Gap + s);
            var hBlock = (Math.Max(nl, capLines) - 1) * lead * s + s;
            var x0 = W / 2 - usedW / 2;
            var meta = JLayouts.Truthy(p["meta"]);
            var top = H / 2 - hBlock / 2 - (meta ? s * 0.3 : 0);
            var capBand = ((capLines - 1) * lead + 1) * s;
            var capCy = top + capBand / 2;
            double LineY(int i) => besideOnly
                ? capCy + (i - (nl - 1) / 2.0) * lead * s * (nl < capLines ? 1.15 : 1)
                : top + s / 2 + i * lead * s;
            var outA = Tout(env);
            var yT = top - s * 0.45;
            var yB = top + hBlock + s * 0.45;
            if (JLayouts.Truthy(p["rules"]))
            {
                var e = Tin(env, 0.05, 0.6, J.E.InOutCubic) * outA;
                var lw = Math.Max(1.5, s * 0.02);
                env.Line(new[] { new[] { x0, yT }, new[] { x0 + usedW * e, yT } }, Col(sc, "fg"), lw, 0.9, false);
                env.Line(new[] { new[] { x0 + usedW, yB }, new[] { x0 + usedW - usedW * e, yB } }, Col(sc, "fg"), lw, 0.9, false);
            }
            if (meta)
            {
                var a = Tin(env, 0.3, 0.4, J.E.OutCubic) * outA;
                var ls = SmallSize(env) * 0.9;
                var yM = yB + ls * 1.3;
                env.DrawItem(new JObject
                {
                    ["text"] = "No." + LineNo(env),
                    ["font"] = MonoF(env),
                    ["size"] = ls,
                    ["track"] = 0.1,
                    ["align"] = "left",
                    ["x"] = x0,
                    ["y"] = yM,
                    ["color"] = Col(sc, "accent"),
                    ["alpha"] = a,
                    ["ghost"] = false,
                });
                env.DrawItem(new JObject
                {
                    ["text"] = RomajiOf(env) ?? AltCopy(env),
                    ["font"] = MonoF(env),
                    ["size"] = ls,
                    ["track"] = 0.1,
                    ["align"] = "right",
                    ["x"] = x0 + usedW,
                    ["y"] = yM,
                    ["color"] = Col(sc, "sub"),
                    ["alpha"] = a,
                    ["ghost"] = false,
                });
            }
            var capKind = S(p, "cap");
            var capIt = new JObject
            {
                ["text"] = cap,
                ["font"] = capFont,
                ["size"] = capS,
                ["x"] = x0 + capW / 2,
                ["y"] = capCy + capS * 0.02,
                ["color"] = capKind == "accent" ? Col(sc, "accent") : Col(sc, "fg"),
                ["mi"] = 0,
            };
            if (capKind == "outline")
            {
                capIt["fill"] = false;
                capIt["stroke"] = Math.Max(2, capS * 0.014);
            }
            var bb = JLayouts.MainDraw(env, capIt);
            for (var i = 0; i < L.Lines.Count; i++)
                bb = JLayouts.UnionBb(bb, JLayouts.MainDraw(env, new JObject
                {
                    ["text"] = L.Lines[i],
                    ["font"] = font,
                    ["size"] = s,
                    ["x"] = i < capLines ? x0 + capW + L.Gap : x0,
                    ["y"] = LineY(i),
                    ["align"] = "left",
                    ["track"] = tr,
                    ["color"] = Col(sc, "fg"),
                    ["mi"] = 2 + i * 2,
                }));
            env.Body = bb ?? Box(x0, top, x0 + usedW, top + hBlock);
        },
    });

    // ---------------- justified（JS L1049-1171） ----------------

    private static void RegisterJustified() => Reg0("justified", new LayoutDef
    {
        Name = "版面",
        Tags = new[] { "editorial", "calm", "emotional" },
        W = 0.8,
        Fits = n => n >= 2 && n <= 22,
        Busy = true,
        Treat = "safe",
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(JLayouts.FontsOf(st, "display", "serif")),
            ["fillFont"] = rng.Pick(JLayouts.FontsOf(st, "body", "serif")),
            ["mark"] = rng.Pick(new[] { "band", "under", "bracket" }),
            ["pos"] = rng.Pick(new[] { 0.28, 0.5, 0.66 }),
            ["dens"] = rng.Range(0.062, 0.078),
        },
        Draw = env =>
        {
            var W = env.W;
            var H = env.H;
            var sc = env.Sc!;
            var p = env.P;
            var u = U(env);
            var text = env.Cut!.Text.Trim();
            var n = JLayouts.GlyphCount(text);
            var font = S(p, "font")!;
            var fillFont = S(p, "fillFont")!;
            var bx0 = W * 0.08;
            var bw = W * 0.84;
            var by0 = H * 0.09;
            var bh = H * 0.82;
            var dens = D(p, "dens");
            if (dens == 0) dens = 0.07;
            var rowsN = Math.Max(8, (int)Math.Round(bh / (u * dens), MidpointRounding.AwayFromZero));
            var rowH = bh / rowsN;
            var fs = rowH * 0.6;
            // 歌词行撑满整个版心宽度
            var capH = rowH * (n <= 5 ? 3.4 : 2.6);
            var lines = new List<string> { text };
            double FitL(string l, double hmax) =>
                Math.Min(hmax, JLayouts.FitSize(l, font, bw, hmax, new JObject { ["track"] = 0.04 }));
            if (FitL(text, capH) < rowH * 1.6 && n >= 4) lines = SplitK(text, 2);
            var two = lines.Count > 1;
            var s2 = two ? lines.Min(l => FitL(l, rowH * 2.4)) : 0;
            var sizes = two ? lines.Select(_ => s2).ToList() : new List<double> { FitL(text, capH) };
            var lyH = sizes.Sum() + (lines.Count - 1) * rowH * 0.25;
            var span = (int)Math.Ceiling(lyH / rowH + 0.8);
            var r0 = (int)Math.Round(D(p, "pos") * (rowsN - span), MidpointRounding.AwayFromZero);
            var outA = Tout(env);
            // 填充字：整段连排循环铺版，歌词占用的行跳过
            var src = JLayouts.CodePoints(Flat(JLayouts.OrStr(env.Cut!.LineText, text)) + (HasLatin(text) ? " / " : "。")).ToArray();
            var adv0 = JLayouts.MetricsAdv(fillFont, "あ");
            var adv = adv0 != 0 ? adv0 : 1;
            var cpr = Math.Max(4, (int)Math.Floor(bw / (fs * adv * 1.06)));
            var off = 0;
            for (var r = 0; r < rowsN; r++)
            {
                if (r >= r0 && r < r0 + span) continue;
                var row = "";
                for (var j = 0; j < cpr; j++) row += src[(off + j) % src.Length];
                off += cpr;
                var a = J.Clamp((env.Lt - r * 0.018) / 0.2) * outA;
                if (a <= 0.01) continue;
                var sp = (bw - RowW(row, fillFont, fs)) / Math.Max(1, cpr - 1);
                JLayouts.FastRow(env, row, fillFont, fs, bx0, by0 + (r + 0.5) * rowH, sp, Col(sc, "sub"), a * 0.34);
            }
            var yc = by0 + (r0 + span / 2.0) * rowH;
            var mx = sizes[0];
            var plate = S(p, "mark") == "band";
            var e = Tin(env, 0.05, 0.5, J.E.InOutExpo) * outA;
            if (plate && e > 0)
                env.Rect(bx0 - mx * 0.2, yc - lyH / 2 - mx * 0.2, (bw + mx * 0.4) * e, lyH + mx * 0.4,
                    Col(sc, "accent"), 1, false);
            if (S(p, "mark") == "under" && e > 0)
            {
                var lw = Math.Max(3, mx * 0.05);
                env.Rect(bx0, yc + lyH / 2 + mx * 0.2, bw * e, lw, Col(sc, "accent"), 1, true);
                env.Rect(bx0 + bw * (1 - e), yc - lyH / 2 - mx * 0.2 - lw, bw * e, lw, Col(sc, "accent"), 1, true);
            }
            if (S(p, "mark") == "bracket" && e > 0)
            {
                var aL = mx * 0.45 * e;
                var lw = Math.Max(2, mx * 0.04);
                var yt = yc - lyH / 2 - mx * 0.16;
                var yb = yc + lyH / 2 + mx * 0.16;
                var g = mx * 0.14;
                env.Line(new[]
                {
                    new[] { bx0 - g + aL, yt }, new[] { bx0 - g, yt }, new[] { bx0 - g, yb }, new[] { bx0 - g + aL, yb }
                }, Col(sc, "accent"), lw, 1, false);
                env.Line(new[]
                {
                    new[] { bx0 + bw + g - aL, yt }, new[] { bx0 + bw + g, yt },
                    new[] { bx0 + bw + g, yb }, new[] { bx0 + bw + g - aL, yb }
                }, Col(sc, "accent"), lw, 1, false);
            }
            var ms = Math.Max(11, rowH * 0.28);
            var fa = Tin(env, 0.1, 0.4, J.E.OutCubic) * outA;
            env.DrawItem(new JObject
            {
                ["text"] = "No." + LineNo(env),
                ["font"] = MonoF(env),
                ["size"] = ms,
                ["align"] = "left",
                ["x"] = bx0,
                ["y"] = by0 - rowH * 0.45,
                ["color"] = Col(sc, "sub"),
                ["alpha"] = fa,
                ["ghost"] = false,
            });
            env.DrawItem(new JObject
            {
                ["text"] = rowsN + " × " + cpr,
                ["font"] = MonoF(env),
                ["size"] = ms,
                ["align"] = "right",
                ["x"] = bx0 + bw,
                ["y"] = by0 + bh + rowH * 0.45,
                ["color"] = Col(sc, "sub"),
                ["alpha"] = fa,
                ["ghost"] = false,
            });
            BBox? bb = null;
            var y = yc - lyH / 2;
            var col = plate ? OnCol(sc, Col(sc, "accent")) : Col(sc, "fg");
            for (var i = 0; i < lines.Count; i++)
            {
                var ln = lines[i];
                var ls = sizes[i];
                var nG = JLayouts.CpLen(ln);
                y += ls / 2;
                JObject it;
                if (two)
                    it = new JObject
                    {
                        ["text"] = ln,
                        ["font"] = font,
                        ["size"] = ls,
                        ["track"] = 0.04,
                        ["align"] = i == 0 ? "left" : "right",
                        ["x"] = i == 0 ? bx0 : bx0 + bw,
                        ["y"] = y,
                        ["color"] = col,
                        ["mi"] = i * 3,
                    };
                else if (nG > 1)
                {
                    var w0 = Meas(ln, font, ls).W;
                    it = new JObject
                    {
                        ["text"] = ln,
                        ["font"] = font,
                        ["size"] = ls,
                        ["track"] = (bw - w0) / (ls * (nG - 1)),
                        ["align"] = "left",
                        ["x"] = bx0,
                        ["y"] = y,
                        ["color"] = col,
                        ["mi"] = 0,
                    };
                }
                else
                    it = new JObject
                    {
                        ["text"] = ln,
                        ["font"] = font,
                        ["size"] = ls,
                        ["x"] = bx0 + bw / 2,
                        ["y"] = y,
                        ["color"] = col,
                        ["mi"] = 0,
                    };
                bb = JLayouts.UnionBb(bb, JLayouts.MainDraw(env, it));
                y += ls / 2 + rowH * 0.25;
            }
            env.Body = bb ?? Box(bx0, yc - lyH / 2, bx0 + bw, yc + lyH / 2);
        },
    });

    // ---------------- frameBox（JS L1173-1256） ----------------

    private static void RegisterFrameBox() => Reg0("frameBox", new LayoutDef
    {
        Name = "額縁",
        Tags = new[] { "editorial", "calm", "graphic" },
        W = 1.1,
        Fits = n => n <= 20,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(JLayouts.FontsOf(st, "display", "serif", "serif")),
            ["style"] = rng.Pick(new[] { "full", "double", "corners" }),
            ["caps"] = rng.Pick(new[] { "tl-br", "top-bottom" }),
            ["shape"] = rng.Pick(new[] { "tight", "wide" }),
        },
        Draw = env =>
        {
            var W = env.W;
            var H = env.H;
            var sc = env.Sc!;
            var p = env.P;
            var u = U(env);
            var port = IsPort(env);
            var font = S(p, "font")!;
            var text = Brk(env.Cut!.Text.Trim(), port ? 6 : 12);
            var o = new JObject { ["track"] = 0.04, ["lead"] = 1.2 };
            var size = Math.Min(JLayouts.FitSize(text, font, W * (port ? 0.74 : 0.68), H * 0.36, o), u * 0.19);
            var m = Meas(text, font, size, o);
            var fw = m.W + size * 1.6;
            var fh = m.H + size * 1.4;
            if (S(p, "shape") == "wide")
            {
                fw = Math.Max(fw, W * (port ? 0.82 : 0.62));
                fh = Math.Max(fh, fw * (port ? 0.9 : 0.5));
            }
            fw = Math.Min(fw, W * 0.9);
            fh = Math.Min(fh, H * 0.86);
            var x0 = W / 2 - fw / 2;
            var y0 = H / 2 - fh / 2;
            var x1 = x0 + fw;
            var y1 = y0 + fh;
            var ls = SmallSize(env) * 0.9;
            var gp = ls * 0.7;
            var c1 = "No." + LineNo(env);
            var c2 = RomajiOf(env) ?? J.FmtTime(env.Cut!.Start);
            var w1 = Meas(c1, MonoF(env), ls, new JObject { ["track"] = 0.12 }).W;
            var w2 = Meas(c2, MonoF(env), ls, new JObject { ["track"] = 0.12 }).W;
            var tb = S(p, "caps") == "top-bottom";
            var a1 = tb ? W / 2 - w1 / 2 : x0 + ls * 2.2;
            var b2 = tb ? W / 2 + w2 / 2 : x1 - ls * 2.2;
            var e = Tin(env, 0, 0.85, J.E.InOutCubic) * (1 - J.E.InCubic(env.POut));
            var lw = Math.Max(2, size * 0.02);
            List<List<double[]>> SegsFor(double X0, double Y0, double Xb, double Yb, bool gaps) => gaps
                ? new List<List<double[]>>
                {
                    new() { new[] { X0, Y0 }, new[] { a1 - gp, Y0 } },
                    new() { new[] { a1 + w1 + gp, Y0 }, new[] { Xb, Y0 }, new[] { Xb, Yb }, new[] { b2 + gp, Yb } },
                    new() { new[] { b2 - w2 - gp, Yb }, new[] { X0, Yb }, new[] { X0, Y0 } },
                }
                : new List<List<double[]>>
                {
                    new() { new[] { X0, Y0 }, new[] { Xb, Y0 }, new[] { Xb, Yb }, new[] { X0, Yb }, new[] { X0, Y0 } }
                };
            if (e > 0)
            {
                if (S(p, "style") == "corners")
                {
                    var arm = Math.Min(fw, fh) * 0.22 * e;
                    var corners = new[]
                    {
                        new[] { x0, y0, 1.0, 1.0 }, new[] { x1, y0, -1.0, 1.0 },
                        new[] { x1, y1, -1.0, -1.0 }, new[] { x0, y1, 1.0, -1.0 }
                    };
                    foreach (var cq in corners)
                        env.Line(new[]
                        {
                            new[] { cq[0] + cq[2] * arm, cq[1] },
                            new[] { cq[0], cq[1] },
                            new[] { cq[0], cq[1] + cq[3] * arm }
                        }, Col(sc, "fg"), lw * 1.4, 1, false);
                }
                else
                {
                    SegsPartial(env, SegsFor(x0, y0, x1, y1, true), e, Col(sc, "fg"), lw, 1, false);
                    if (S(p, "style") == "double")
                    {
                        var d = size * 0.16;
                        SegsPartial(env, SegsFor(x0 + d, y0 + d, x1 - d, y1 - d, false),
                            J.Clamp(e * 1.15 - 0.15), Col(sc, "sub"), Math.Max(1, lw * 0.5), 0.8, false);
                    }
                }
                var ca = J.Clamp(e * 2 - 0.6);
                if (ca > 0)
                {
                    env.DrawItem(new JObject
                    {
                        ["text"] = c1,
                        ["font"] = MonoF(env),
                        ["size"] = ls,
                        ["track"] = 0.12,
                        ["align"] = "left",
                        ["x"] = a1,
                        ["y"] = y0,
                        ["color"] = Col(sc, "accent"),
                        ["alpha"] = ca,
                        ["ghost"] = false,
                    });
                    env.DrawItem(new JObject
                    {
                        ["text"] = c2,
                        ["font"] = MonoF(env),
                        ["size"] = ls,
                        ["track"] = 0.12,
                        ["align"] = "right",
                        ["x"] = b2,
                        ["y"] = y1,
                        ["color"] = Col(sc, "sub"),
                        ["alpha"] = ca,
                        ["ghost"] = false,
                    });
                }
            }
            var bb = JLayouts.MainDraw(env, new JObject
            {
                ["text"] = text,
                ["font"] = font,
                ["size"] = size,
                ["x"] = W / 2,
                ["y"] = H / 2,
                ["track"] = 0.04,
                ["lead"] = 1.2,
                ["color"] = Col(sc, "fg"),
            });
            env.Body = bb ?? Box(W / 2 - m.W / 2, H / 2 - m.H / 2, W / 2 + m.W / 2, H / 2 + m.H / 2);
        },
    });

    // ---------------- bubble（JS L1258-1375） ----------------

    private static void RegisterBubble() => Reg0("bubble", new LayoutDef
    {
        Name = "吹き出し",
        Tags = new[] { "pop", "emotional" },
        W = 0.9,
        EnterBias = new JObject { ["pop"] = 2, ["drop"] = 1.4, ["spin"] = 0.5 },
        Fits = n => n <= 20,
        Treat = "safe",
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(JLayouts.FontsOf(st, "display", "body")),
            ["shape"] = rng.Pick(new[] { "round", "ellipse", "thought", "round" }),
            ["style"] = rng.Pick(new[] { "fill", "outline", "fill" }),
            ["tail"] = rng.Pick(new[] { 1.0, -1.0 }),
            ["off"] = rng.Range(-0.05, 0.05),
            ["tilt"] = rng.Range(-3, 3),
            ["burst"] = rng.Chance(0.6),
        },
        Draw = env =>
        {
            var W = env.W;
            var H = env.H;
            var sc = env.Sc!;
            var p = env.P;
            var u = U(env);
            var port = IsPort(env);
            var ctx = env.Ctx;
            var shape = S(p, "shape")!;
            var ell = shape != "round";
            var t0 = env.Cut!.Text.Trim();
            var n0 = JLayouts.GlyphCount(t0);
            var font = S(p, "font")!;
            var text = ell && n0 >= 5 && !port
                ? Brk(t0, Math.Max(3, (int)Math.Ceiling(n0 / 2.0)))
                : Brk(t0, port ? 6 : 10);
            var o = new JObject { ["track"] = 0.02, ["lead"] = 1.2 };
            var size = Math.Min(JLayouts.FitSize(text, font, W * (port ? 0.62 : ell ? 0.5 : 0.6), H * 0.32, o), u * 0.15);
            var m = Meas(text, font, size, o);
            var w = m.W * (ell ? 1.34 : 1) + size * (ell ? 1.1 : 1.2);
            var h = m.H * (ell ? 1.3 : 1) + size * (ell ? 1.0 : 0.95);
            var cx = W / 2 + D(p, "off") * W;
            var cy = H / 2 - size * 0.3;
            var fill = S(p, "style") == "fill" || shape == "thought";
            var plate = Col(sc, "ink");
            var tcol = fill ? OnCol(sc, plate) : Col(sc, "fg");
            var q = J.E.OutBack(J.Clamp(env.Lt / 0.32), 1.7) * (1 - J.E.InCubic(J.Clamp(env.POut * 1.2)));
            var sh = shape == "thought" ? "ellipse" : shape;
            var tail = (int)D(p, "tail");
            var B = BubblePoly(sh, cx, cy, w, h, size, tail);
            var pivot = shape == "thought" ? new[] { cx, cy } : B.Tip;
            ctx.Save();
            ctx.Translate((float)pivot[0], (float)pivot[1]);
            ctx.RotateDegrees((float)Deg(D(p, "tilt") * J.DEG * (1 - q) * 3));
            var qs = (float)Math.Max(0.001, q);
            ctx.Scale(qs, qs);
            ctx.Translate((float)-pivot[0], (float)-pivot[1]);
            if (q > 0.001)
            {
                if (shape == "thought")
                {
                    const int M = 22;
                    var pts = new List<double[]>();
                    for (var i = 0; i < M; i++)
                    {
                        var a = i / (double)M * J.TAU;
                        var k = i % 2 != 0 ? 1.08 : 0.97;
                        pts.Add(new[] { cx + Math.Cos(a) * w / 2 * k, cy + Math.Sin(a) * h / 2 * k });
                    }
                    env.Blob(pts.ToArray(), plate, 1, false);
                    var tx = cx + tail * w * 0.34;
                    var ty = cy + h / 2;
                    var bubbles = new[] { new[] { 0.45, 0.2 }, new[] { 0.85, 0.12 }, new[] { 1.15, 0.07 } };
                    foreach (var bb2 in bubbles)
                        env.Circle(tx + tail * size * bb2[0] * 0.8, ty + size * bb2[0] * 0.75,
                            size * bb2[1] * 1.4, plate, null, 0, 1, false);
                }
                else if (fill) env.Poly(B.Pts.ToArray(), plate, 1, false);
                else env.Line(CloseLoop(B.Pts).ToArray(), Col(sc, "fg"), Math.Max(3, size * 0.05), 1, false);
            }
            var bb = JLayouts.MainDraw(env, new JObject
            {
                ["text"] = text,
                ["font"] = font,
                ["size"] = size,
                ["x"] = cx,
                ["y"] = cy + size * 0.02,
                ["track"] = 0.02,
                ["lead"] = 1.2,
                ["color"] = tcol,
            });
            ctx.Restore();
            // 顶角的强调短线
            if (JLayouts.Truthy(p["burst"]))
            {
                var a = J.Clamp((env.Lt - 0.2) / 0.2) * Tout(env);
                if (a > 0)
                {
                    var bx = cx - tail * (w / 2 + size * 0.05);
                    var by = cy - h / 2 - size * 0.05;
                    var L = size * 0.45 * J.E.OutExpo(a);
                    var ds = new[] { -30.0, 0.0, 30.0 };
                    for (var i = 0; i < ds.Length; i++)
                    {
                        var ang = (-90 - tail * 45 + ds[i] * 0.9) * J.DEG;
                        var r0 = size * 0.25;
                        var r1 = r0 + L * (i == 1 ? 1.2 : 0.9);
                        env.Line(new[]
                        {
                            new[] { bx + Math.Cos(ang) * r0, by + Math.Sin(ang) * r0 },
                            new[] { bx + Math.Cos(ang) * r1, by + Math.Sin(ang) * r1 }
                        }, Col(sc, "accent"), Math.Max(3, size * 0.05), 1, false);
                    }
                }
            }
            env.Body = bb ?? Box(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2);
        },
    });

    // ---------------- subtitleBar（JS L1377-1511） ----------------

    private static void RegisterSubtitleBar() => Reg0("subtitleBar", new LayoutDef
    {
        Name = "字幕帯",
        Tags = new[] { "emotional", "calm", "editorial" },
        W = 0.9,
        Fits = n => n <= 26,
        Busy = true,
        Treat = "safe",
        Portrait = 0.7,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(JLayouts.FontsOf(st, "body", "serif")),
            ["bigFont"] = rng.Pick(JLayouts.FontsOf(st, "display", "serif")),
            ["big"] = rng.Pick(new[] { "dim", "outline" }),
            ["bar"] = rng.Range(0.1, 0.13),
            ["tc"] = rng.Pick(new[] { "rec", "scene" }),
            ["place"] = rng.Pick(new[] { "bar", "band" }),
            ["drift"] = rng.Pick(new[] { 1.0, -1.0 }),
        },
        Draw = env =>
        {
            var W = env.W;
            var H = env.H;
            var sc = env.Sc!;
            var p = env.P;
            var u = U(env);
            var port = IsPort(env);
            var text = env.Cut!.Text.Trim();
            var lb = env.Ltb;
            var cand = new[] { Col(sc, "bg"), Col(sc, "fg"), Col(sc, "ink"), Col(sc, "dim") };
            var barC = cand[0];
            foreach (var c in cand)
                if (J.Lum(c) < J.Lum(barC))
                    barC = c;
            // 深色配色时改为提亮画面区
            var pic = barC == Col(sc, "bg") && Math.Abs(J.Lum(Col(sc, "dim")) - J.Lum(Col(sc, "bg"))) > 0.03;
            var bh = H * (port ? D(p, "bar") * 0.62 : D(p, "bar"));
            var e = Tin(env, 0, 0.55, J.E.OutExpo) * (1 - J.E.InCubic(env.POut));
            var outA = Tout(env);
            var hh0 = bh * e;
            if (pic && hh0 > 0.5)
                env.Rect(-2, hh0, W + 4, H - hh0 * 2, Col(sc, "dim"), J.Clamp(e * 1.4), false);
            // 背后超大号的浅色副本
            var bt = Flat(text);
            var bigFont = S(p, "bigFont")!;
            var bs = Math.Min(JLayouts.FitSize(bt, bigFont, W * 0.94, H * 0.6), H * 0.6);
            var ba = Tin(env, 0.05, 0.6, J.E.OutCubic) * outA;
            if (ba > 0.01)
            {
                var bx = W / 2 + (lb / Math.Max(1, env.Cut!.Dur) - 0.5) * W * 0.03 * D(p, "drift");
                if (S(p, "big") == "outline")
                    env.DrawItem(new JObject
                    {
                        ["text"] = bt,
                        ["font"] = bigFont,
                        ["size"] = bs,
                        ["x"] = bx,
                        ["y"] = H / 2,
                        ["fill"] = false,
                        ["stroke"] = Math.Max(1.2, bs * 0.006),
                        ["strokeColor"] = Col(sc, "sub"),
                        ["alpha"] = 0.4 * ba,
                        ["ghost"] = false,
                    });
                else
                    JLayouts.FastRow(env, bt, bigFont, bs, bx, H / 2, 0,
                        pic ? Col(sc, "bg") : Col(sc, "dim"), ba * (pic ? 0.7 : 1), "center");
            }
            // 上下 letterbox 遮幅
            var hh = bh * e;
            if (hh > 0.5)
            {
                env.Rect(-2, -2, W + 4, hh + 2, barC, 1, false);
                env.Rect(-2, H - hh, W + 4, hh + 2, barC, 1, false);
                env.Line(new[] { new[] { 0.0, hh }, new[] { (double)W, hh } }, Col(sc, "sub"), 1.2, 0.35, false);
                env.Line(new[] { new[] { 0.0, H - hh }, new[] { (double)W, H - hh } }, Col(sc, "sub"), 1.2, 0.35, false);
            }
            // 上栏里的 REC / 场次时间码
            var ls = Math.Min(SmallSize(env) * 0.9, bh * 0.3);
            var ta = J.Clamp((e - 0.6) * 2.5) * outA;
            if (ta > 0)
            {
                var y = hh / 2;
                if (S(p, "tc") == "rec")
                {
                    if (env.Step % 2 == 0)
                        env.Circle(W * 0.05, y, ls * 0.35, Col(sc, "accent"), null, 0, ta, false);
                    env.DrawItem(new JObject
                    {
                        ["text"] = "REC",
                        ["font"] = MonoF(env),
                        ["size"] = ls,
                        ["track"] = 0.12,
                        ["align"] = "left",
                        ["x"] = W * 0.05 + ls * 0.8,
                        ["y"] = y,
                        ["color"] = OnCol(sc, barC),
                        ["alpha"] = ta,
                        ["ghost"] = false,
                    });
                }
                else
                    env.DrawItem(new JObject
                    {
                        ["text"] = "SCENE " + LineNo(env) + "  /  CUT " + Pad2(env.Cut!.Index + 1),
                        ["font"] = MonoF(env),
                        ["size"] = ls,
                        ["track"] = 0.12,
                        ["align"] = "left",
                        ["x"] = W * 0.05,
                        ["y"] = y,
                        ["color"] = OnCol(sc, barC),
                        ["alpha"] = ta,
                        ["ghost"] = false,
                    });
                env.DrawItem(new JObject
                {
                    ["text"] = J.FmtTime(env.T, 24),
                    ["font"] = MonoF(env),
                    ["size"] = ls,
                    ["track"] = 0.08,
                    ["align"] = "right",
                    ["x"] = W * 0.95,
                    ["y"] = y,
                    ["color"] = Col(sc, "sub"),
                    ["alpha"] = ta,
                    ["ghost"] = false,
                });
            }
            var o = new JObject { ["track"] = 0.06, ["lead"] = 1.25 };
            var t2 = text;
            var font = S(p, "font")!;
            var maxH = S(p, "place") == "bar" ? bh * 0.62 : bh * 0.8;
            var ss = Math.Min(JLayouts.FitSize(t2, font, W * 0.84, maxH, o), u * 0.058);
            if (ss < u * 0.04 && JLayouts.GlyphCount(text) > 8)
            {
                t2 = Brk(text, (int)Math.Ceiling(JLayouts.GlyphCount(text) / 2.0));
                ss = Math.Min(JLayouts.FitSize(t2, font, W * 0.84, maxH * 1.5, o), u * 0.05);
            }
            var m = Meas(t2, font, ss, o);
            double sy;
            string scol;
            if (S(p, "place") == "bar")
            {
                sy = H - bh / 2;
                scol = OnCol(sc, barC);
            }
            else
            {
                sy = H - bh - m.H / 2 - ss * 0.9;
                var pa = J.Clamp((e - 0.3) * 2) * outA;
                if (pa > 0)
                    env.Rect(W / 2 - m.W / 2 - ss * 0.8, sy - m.H / 2 - ss * 0.35, m.W + ss * 1.6, m.H + ss * 0.7,
                        barC, 0.72 * pa, false);
                scol = OnCol(sc, barC);
            }
            var bb = JLayouts.MainDraw(env, new JObject
            {
                ["text"] = t2,
                ["font"] = font,
                ["size"] = ss,
                ["x"] = W / 2,
                ["y"] = sy,
                ["track"] = 0.06,
                ["lead"] = 1.25,
                ["color"] = scol,
            });
            env.Body = bb ?? Box(W / 2 - m.W / 2, sy - m.H / 2, W / 2 + m.W / 2, sy + m.H / 2);
        },
    });

    // ---------------- ticker（JS L1513-1633） ----------------

    private static void RegisterTicker() => Reg0("ticker", new LayoutDef
    {
        Name = "ティッカー",
        Tags = new[] { "pop", "glitch", "graphic" },
        W = 0.9,
        EnterBias = new JObject { ["wipe"] = 1.6, ["slice"] = 1.4, ["type"] = 1.2 },
        Fits = n => n <= 18,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(JLayouts.FontsOf(st, "display")),
            ["tag"] = rng.Pick(new[] { "LIVE", "NOW", "ON AIR", "LIVE" }),
            ["speed"] = rng.Range(0.8, 1.3),
            ["main"] = rng.Pick(new[] { "center", "left", "center" }),
            ["bug"] = rng.Chance(0.6),
        },
        Draw = env =>
        {
            var W = env.W;
            var H = env.H;
            var sc = env.Sc!;
            var p = env.P;
            var u = U(env);
            var port = IsPort(env);
            var ctx = env.Ctx;
            var text0 = env.Cut!.Text.Trim();
            var bandH = u * 0.075;
            var bandY = H - bandH - H * (port ? 0.12 : 0.085);
            var outA = 1 - J.E.InCubic(env.POut);
            var eB = Tin(env, 0, 0.5, J.E.OutExpo) * outA;
            var tagC = Col(sc, "accent");
            var bandC = Col(sc, "ink") == Col(sc, "accent") ? Col(sc, "fg") : Col(sc, "ink");
            var ts = bandH * 0.42;
            var tag = S(p, "tag")!;
            var tagW = Meas(tag, MonoF(env), ts, new JObject { ["track"] = 0.12 }).W + ts * 2.6;
            var x0 = W * 0.04;
            // 横带和循环滚动的文案
            var bw = (W - x0) * eB;
            if (bw > 1)
            {
                env.Rect(x0, bandY, bw, bandH, bandC, 1, false);
                if (env.Pass == "main")
                {
                    var unit = Flat(JLayouts.OrStr(env.Cut!.LineText, text0)) + "　◆　";
                    var fs = bandH * 0.46;
                    var per = RowW(unit, BodyF(env), fs);
                    var reps = Math.Min(40, (int)Math.Ceiling(W * 1.2 / Math.Max(1, per)) + 2);
                    var off = (env.Ltb * D(p, "speed") * u * 0.22 % per + per) % per;
                    ctx.Save();
                    ctx.ClipRect(new SKRect((float)(x0 + tagW), (float)bandY,
                        (float)(x0 + tagW + Math.Max(0, bw - tagW)), (float)(bandY + bandH)));
                    JLayouts.FastRow(env, JLayouts.Repeat(unit, reps), BodyF(env), fs,
                        x0 + tagW + ts * 0.8 - off, bandY + bandH / 2, 0, OnCol(sc, bandC), 1);
                    ctx.Restore();
                }
            }
            // 左端的标签牌
            var tq = J.E.OutBack(J.Clamp((env.Lt - 0.08) / 0.3), 1.6) * outA;
            if (tq > 0.01)
            {
                var th = bandH * 1.0 * tq;
                env.Rect(x0, bandY + bandH / 2 - th / 2 - bandH * 0.12 * tq, tagW, th + bandH * 0.24 * tq, tagC, 1, true);
                var tc = OnCol(sc, tagC);
                if (env.Step % 3 != 0)
                    env.Circle(x0 + ts * 0.95, bandY + bandH / 2, ts * 0.28 * tq, tc, null, 0, 1, false);
                env.DrawItem(new JObject
                {
                    ["text"] = tag,
                    ["font"] = MonoF(env),
                    ["size"] = ts * tq,
                    ["track"] = 0.12,
                    ["align"] = "left",
                    ["x"] = x0 + ts * 1.6,
                    ["y"] = bandY + bandH / 2,
                    ["color"] = tc,
                    ["ghost"] = false,
                });
            }
            // 横带右端上方的小时间框
            var ca = J.Clamp((eB - 0.7) * 3.3);
            if (ca > 0)
            {
                var tt = J.FmtTime(env.T);
                var tsz = ts * 0.9;
                var tw = Meas(tt, MonoF(env), tsz, new JObject { ["track"] = 0.08 }).W + tsz * 1.4;
                env.Rect(W - W * 0.04 - tw, bandY - tsz * 1.7, tw, tsz * 1.7, Col(sc, "fg"), ca, false);
                env.DrawItem(new JObject
                {
                    ["text"] = tt,
                    ["font"] = MonoF(env),
                    ["size"] = tsz,
                    ["track"] = 0.08,
                    ["x"] = W - W * 0.04 - tw / 2,
                    ["y"] = bandY - tsz * 0.85,
                    ["color"] = OnCol(sc, Col(sc, "fg")),
                    ["alpha"] = ca,
                    ["ghost"] = false,
                });
            }
            if (JLayouts.Truthy(p["bug"]))
            {
                var a = Tin(env, 0.2, 0.4, J.E.OutCubic) * outA;
                var bs = SmallSize(env) * 0.85;
                env.DrawItem(new JObject
                {
                    ["text"] = "CH." + LineNo(env),
                    ["font"] = MonoF(env),
                    ["size"] = bs,
                    ["track"] = 0.2,
                    ["align"] = "right",
                    ["x"] = W * 0.95,
                    ["y"] = H * 0.07,
                    ["color"] = Col(sc, "sub"),
                    ["alpha"] = a,
                    ["ghost"] = false,
                });
                env.Rect(W * 0.95 - bs * 0.3, H * 0.07 + bs * 0.9, bs * 0.3, bs * 0.3, Col(sc, "accent"), a, false);
            }
            // 主标题，就是歌词本体
            var left = S(p, "main") == "left";
            var font = S(p, "font")!;
            var text = Brk(text0, port ? 6 : 11);
            var o = new JObject { ["track"] = 0.03, ["lead"] = 1.15 };
            var avail = bandY - H * 0.1;
            var size = Math.Min(JLayouts.FitSize(text, font, W * 0.84, avail * 0.7, o), u * 0.2);
            var m = Meas(text, font, size, o);
            var y = left ? bandY - bandH * 0.55 - m.H / 2 - size * 0.2 : H * 0.1 + avail / 2;
            var x = left ? x0 + size * 0.1 : W / 2;
            if (left)
            {
                var le = Tin(env, 0.1, 0.5) * outA;
                env.Rect(x0, y - m.H / 2 - size * 0.28, Math.Max(0, size * 1.2 * le), Math.Max(3, size * 0.06),
                    Col(sc, "accent"), 1, false);
            }
            var bb = JLayouts.MainDraw(env, new JObject
            {
                ["text"] = text,
                ["font"] = font,
                ["size"] = size,
                ["x"] = x,
                ["y"] = y,
                ["align"] = left ? "left" : "center",
                ["track"] = 0.03,
                ["lead"] = 1.15,
                ["color"] = Col(sc, "fg"),
            });
            env.Body = bb ?? Box(left ? x : x - m.W / 2, y - m.H / 2, left ? x + m.W : x + m.W / 2, y + m.H / 2);
        },
    });

    // ---------------- splitScreen（JS L1635-1730） ----------------

    private static void RegisterSplitScreen() => Reg0("splitScreen", new LayoutDef
    {
        Name = "二分割",
        Tags = new[] { "graphic", "pop", "editorial" },
        W = 0.9,
        Emph = 1.6,
        EnterBias = new JObject { ["wipe"] = 1.5, ["slice"] = 1.4, ["stretch"] = 1.2 },
        Fits = n => n <= 14,
        Busy = true,
        Treat = false,
        Plan = (rng, cut, st) =>
        {
            var port = cut.H > cut.W * 1.08;
            return new JObject
            {
                ["font"] = rng.Pick(JLayouts.FontsOf(st, "display")),
                ["split"] = port ? rng.Pick(new[] { "h", "diag", "h" }) : rng.Pick(new[] { "v", "diag", "v", "h" }),
                ["plate"] = rng.Pick(new[] { "fg", "accent", "fg" }),
                ["side"] = rng.Pick(new[] { 1.0, -1.0 }),
                ["tilt"] = rng.Range(12, 22),
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
            var ctx = env.Ctx;
            var tOff = J.Contrast(Col(sc, "fg"), Col(sc, "bg")) > 2 ? Col(sc, "fg") : OnCol(sc, Col(sc, "bg"));
            var plateC = S(p, "plate") == "accent" ? Col(sc, "accent") : Col(sc, "fg");
            // 反白后仍要可读
            if (OnCol(sc, plateC) == tOff || J.Contrast(plateC, Col(sc, "bg")) < 1.6) plateC = Col(sc, "fg");
            var tOn = OnCol(sc, plateC);
            var e = Tin(env, 0, 0.5, J.E.OutExpo) * (1 - J.E.InExpo(env.POut));
            var sd = (int)D(p, "side");
            var split = S(p, "split")!;
            // 色板多边形和它的补集；分界线从色板外侧扫入
            List<double[]> plate;
            List<double[]> other;
            if (split == "h")
            {
                var by = H / 2 + sd * (1 - e) * H * 0.55;
                plate = sd > 0
                    ? new List<double[]> { new[] { -W, by }, new[] { 2.0 * W, by }, new[] { 2.0 * W, 2.0 * H }, new[] { -W, 2.0 * H } }
                    : new List<double[]> { new[] { -(double)W, -(double)H }, new[] { 2.0 * W, -H }, new[] { 2.0 * W, by }, new[] { -W, by } };
                other = sd > 0
                    ? new List<double[]> { new[] { -(double)W, -(double)H }, new[] { 2.0 * W, -H }, new[] { 2.0 * W, by }, new[] { -W, by } }
                    : new List<double[]> { new[] { -W, by }, new[] { 2.0 * W, by }, new[] { 2.0 * W, 2.0 * H }, new[] { -W, 2.0 * H } };
            }
            else
            {
                var dx = split == "diag" ? Math.Tan(D(p, "tilt") * J.DEG) * H / 2 : 0;
                var sh = sd * (1 - e) * (W * 0.55 + Math.Abs(dx));
                // JS 里 ta/tb 算出后未使用（保留对位说明，不做绘制）
                var top = new[] { W / 2 + dx - 2 * dx * (-H / H) + sh, -H };
                var bot = new[] { W / 2 + dx - 2 * dx * (2.0 * H / H) + sh, 2.0 * H };
                plate = sd > 0
                    ? new List<double[]> { top, new[] { 3.0 * W, -H }, new[] { 3.0 * W, 2.0 * H }, bot }
                    : new List<double[]> { new[] { -2.0 * W, -H }, top, bot, new[] { -2.0 * W, 2.0 * H } };
                other = sd > 0
                    ? new List<double[]> { new[] { -2.0 * W, -H }, top, bot, new[] { -2.0 * W, 2.0 * H } }
                    : new List<double[]> { top, new[] { 3.0 * W, -H }, new[] { 3.0 * W, 2.0 * H }, bot };
            }
            void ClipTo(List<double[]> poly)
            {
                using var path = PathOf(poly);
                ctx.ClipPath(path);
            }
            if (e > 0) env.Poly(plate.ToArray(), plateC, 1, false);
            var font = S(p, "font")!;
            var text = Brk(env.Cut!.Text.Trim(), port ? 5 : 10);
            var o = new JObject { ["track"] = 0.02, ["lead"] = 1.08 };
            var size = Math.Min(JLayouts.FitSize(text, font, W * 0.86, H * (split == "h" ? 0.36 : 0.42), o), u * 0.3);
            var m = Meas(text, font, size, o);
            var cy = split == "h" ? H / 2 + (m.H > size * 1.5 ? 0 : size * 0.04) : H / 2;
            JObject It() => new()
            {
                ["text"] = text,
                ["font"] = font,
                ["size"] = size,
                ["x"] = W / 2,
                ["y"] = cy,
                ["track"] = 0.02,
                ["lead"] = 1.08,
            };
            // 两半各自的角标
            var la = Tin(env, 0.25, 0.4, J.E.OutCubic) * Tout(env);
            var ls = SmallSize(env) * 0.9;
            BBox? bb;
            ctx.Save();
            ClipTo(other);
            var itOff = It();
            itOff["color"] = tOff;
            bb = JLayouts.MainDraw(env, itOff);
            if (la > 0)
                env.DrawItem(new JObject
                {
                    ["text"] = "No." + LineNo(env),
                    ["font"] = MonoF(env),
                    ["size"] = ls,
                    ["track"] = 0.15,
                    ["align"] = sd > 0 ? "left" : "right",
                    ["x"] = sd > 0 ? W * 0.05 : W * 0.95,
                    ["y"] = sd > 0 || split != "h" ? H * 0.07 : H * 0.93,
                    ["color"] = Col(sc, "sub"),
                    ["alpha"] = la,
                    ["ghost"] = false,
                });
            ctx.Restore();
            if (env.Pass == "main" && e > 0)
            {
                ctx.Save();
                ClipTo(plate);
                var itOn = It();
                itOn["color"] = tOn;
                var b2 = JLayouts.MainDraw(env, itOn);
                bb = JLayouts.UnionBb(bb, b2);
                if (la > 0)
                    env.DrawItem(new JObject
                    {
                        ["text"] = RomajiOf(env) ?? J.FmtTime(env.Cut!.Start),
                        ["font"] = MonoF(env),
                        ["size"] = ls,
                        ["track"] = 0.15,
                        ["align"] = sd > 0 ? "right" : "left",
                        ["x"] = sd > 0 ? W * 0.95 : W * 0.05,
                        ["y"] = sd > 0 || split != "h" ? H * 0.93 : H * 0.07,
                        ["color"] = tOn,
                        ["alpha"] = la,
                        ["ghost"] = false,
                    });
                ctx.Restore();
            }
            env.Body = bb ?? Box(W / 2 - m.W / 2, cy - m.H / 2, W / 2 + m.W / 2, cy + m.H / 2);
        },
    });

    // ---------------- mirror（JS L1732-1788） ----------------

    private static void RegisterMirror() => Reg0("mirror", new LayoutDef
    {
        Name = "鏡像",
        Tags = new[] { "calm", "emotional", "graphic" },
        W = 1,
        Fits = n => n <= 16,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(JLayouts.FontsOf(st, "display", "serif")),
            ["strength"] = rng.Range(0.34, 0.5),
            ["squash"] = rng.Pick(new[] { 1.0, 0.7, 0.85 }),
            ["ripple"] = rng.Chance(0.5),
            ["ticks"] = rng.Chance(0.6),
        },
        Draw = env =>
        {
            var W = env.W;
            var H = env.H;
            var sc = env.Sc!;
            var p = env.P;
            var u = U(env);
            var port = IsPort(env);
            var ctx = env.Ctx;
            var font = S(p, "font")!;
            var text = Brk(env.Cut!.Text.Trim(), port ? 6 : 12);
            var o = new JObject { ["track"] = 0.04, ["lead"] = 1.15 };
            var size = Math.Min(JLayouts.FitSize(text, font, W * 0.84, H * 0.3, o), u * 0.22);
            var m = Meas(text, font, size, o);
            var squash = D(p, "squash");
            var RH = m.H * squash + size * 0.1;
            var cy = H / 2 - RH * 0.42;
            var hz = cy + m.H / 2 + size * 0.08;
            var outA = Tout(env);
            // 地平线
            var e = Tin(env, 0, 0.7, J.E.InOutCubic) * outA;
            if (e > 0)
            {
                var hw = W * 0.44 * e;
                env.Line(new[] { new[] { W / 2 - hw, hz }, new[] { W / 2 + hw, hz } },
                    Col(sc, "sub"), Math.Max(1.2, u * 0.0016), 0.85, false);
                if (JLayouts.Truthy(p["ticks"]))
                {
                    for (var k = -8; k <= 8; k++)
                    {
                        var x = W / 2 + k * W * 0.05;
                        if (Math.Abs(x - W / 2) > hw) continue;
                        env.Line(new[]
                        {
                            new[] { x, hz - u * 0.006 },
                            new[] { x, hz + u * 0.006 * (k % 4 == 0 ? 2 : 1) }
                        }, Col(sc, "sub"), 1.2, 0.6, false);
                    }
                }
                env.Circle(W / 2 - hw, hz, u * 0.005, Col(sc, "accent"), null, 0, 1, false);
                env.Circle(W / 2 + hw, hz, u * 0.005, Col(sc, "accent"), null, 0, 1, false);
            }
            var baseIt = new JObject
            {
                ["text"] = text,
                ["font"] = font,
                ["size"] = size,
                ["x"] = W / 2,
                ["y"] = cy,
                ["track"] = 0.04,
                ["lead"] = 1.15,
                ["color"] = Col(sc, "fg"),
            };
            // 倒影：横向分条翻转绘制，越深越淡，可加微波
            if (env.Pass == "main")
            {
                const int K = 9;
                for (var k = 0; k < K; k++)
                {
                    var y0 = hz + RH * k / K;
                    var y1 = hz + RH * (k + 1) / K;
                    var a = D(p, "strength") * Math.Pow(1 - (k + 0.5) / K, 1.5);
                    var dx = JLayouts.Truthy(p["ripple"])
                        ? Math.Sin(env.Ltb * 2.4 + k * 0.9) * size * 0.035 * (k + 1) / K
                        : 0;
                    ctx.Save();
                    ctx.ClipRect(new SKRect((float)-W, (float)y0, (float)(W * 3), (float)(y1 - y0 + 0.6)));
                    ctx.Translate((float)dx, (float)hz);
                    ctx.Scale(1, (float)-squash);
                    ctx.Translate(0, (float)-hz);
                    var it = (JObject)baseIt.DeepClone();
                    it["alpha"] = a;
                    it["ghost"] = false;
                    it["mi"] = 0;
                    JLayouts.MainDraw(env, it);
                    ctx.Restore();
                }
            }
            var bb = JLayouts.MainDraw(env, (JObject)baseIt.DeepClone());
            env.Body = bb ?? Box(W / 2 - m.W / 2, cy - m.H / 2, W / 2 + m.W / 2, cy + m.H / 2);
        },
    });

    // ---------------- sideways（JS L1790-1910） ----------------

    private static void RegisterSideways() => Reg0("sideways", new LayoutDef
    {
        Name = "縦倒し",
        Tags = new[] { "editorial", "graphic", "pop" },
        W = 1,
        Emph = 1.3,
        EnterBias = new JObject { ["wipe"] = 1.4, ["slice"] = 1.3, ["stretch"] = 1.3 },
        Fits = n => n >= 3 && n <= 16,
        Portrait = 1.3,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(JLayouts.FontsOf(st, "display", "display", "serif")),
            ["side"] = rng.Pick(new[] { "left", "right" }),
            ["copy"] = rng.Pick(new[] { "stack", "number" }),
            ["rule"] = rng.Chance(0.75),
        },
        Draw = env =>
        {
            var W = env.W;
            var H = env.H;
            var sc = env.Sc!;
            var p = env.P;
            var u = U(env);
            var port = IsPort(env);
            var ctx = env.Ctx;
            var t0 = env.Cut!.Text.Trim();
            var n = JLayouts.GlyphCount(t0);
            var font = S(p, "font")!;
            var text = (port ? n > 12 : n > 6) ? Brk(t0, (int)Math.Ceiling(n / 2.0)) : t0;
            var o = new JObject { ["track"] = 0.02, ["lead"] = 1.02 };
            var LW = H * 0.88;
            var T = W * (port ? 0.4 : 0.3);
            var size = Math.Min(JLayouts.FitSize(text, font, LW, T, o), u * (port ? 0.34 : 0.3));
            var m = Meas(text, font, size, o);
            var left = S(p, "side") == "left";
            var mx = W * 0.05;
            var cx = left ? mx + m.H / 2 : W - mx - m.H / 2;
            var rot = left ? -90 : 90;
            var outA = Tout(env);
            // 竖排歌词旁的分隔细线
            var rx = left ? cx + m.H / 2 + W * 0.035 : cx - m.H / 2 - W * 0.035;
            if (JLayouts.Truthy(p["rule"]))
            {
                var e = Tin(env, 0.05, 0.6, J.E.InOutCubic) * outA;
                env.Line(new[] { new[] { rx, H * 0.06 }, new[] { rx, H * 0.06 + H * 0.88 * e } },
                    Col(sc, "accent"), Math.Max(2, u * 0.003), 1, false);
            }
            // 空余区域里的横排文案块
            var ax0 = left ? rx + W * 0.04 : W * 0.07;
            var ax1 = left ? W * 0.93 : rx - W * 0.04;
            var aw = ax1 - ax0;
            var ca = Tin(env, 0.2, 0.45, J.E.OutCubic) * outA;
            if (ca > 0.01 && aw > u * 0.2)
            {
                var ls = SmallSize(env);
                var copy = JLayouts.OrStr(env.Cut!.LineText, env.Cut!.Text)!;
                var cs = Math.Min(u * 0.05,
                    JLayouts.FitSize(Brk(copy, Math.Max(4, (int)Math.Floor(aw / (u * 0.05)))), BodyF(env), aw, H * 0.3,
                        new JObject { ["lead"] = 1.5 }));
                var ct = Brk(copy, Math.Max(4, (int)Math.Floor(aw / (cs * 1.02))));
                var cm = Meas(ct, BodyF(env), cs, new JObject { ["lead"] = 1.5 });
                var sl = (1 - ca) * u * 0.03;
                if (S(p, "copy") == "number")
                {
                    var ns = Math.Min(aw * 0.5, H * 0.3);
                    env.DrawItem(new JObject
                    {
                        ["text"] = LineNo(env),
                        ["font"] = font,
                        ["size"] = ns,
                        ["align"] = "left",
                        ["x"] = ax0 - ns * 0.04,
                        ["y"] = H * 0.1 + ns * 0.45 + sl,
                        ["fill"] = false,
                        ["stroke"] = Math.Max(1.5, ns * 0.012),
                        ["strokeColor"] = Col(sc, "sub"),
                        ["alpha"] = ca,
                        ["ghost"] = false,
                    });
                }
                else
                {
                    env.DrawItem(new JObject
                    {
                        ["text"] = "No." + LineNo(env),
                        ["font"] = MonoF(env),
                        ["size"] = ls,
                        ["track"] = 0.15,
                        ["align"] = "left",
                        ["x"] = ax0,
                        ["y"] = H * 0.1 + sl,
                        ["color"] = Col(sc, "accent"),
                        ["alpha"] = ca,
                        ["ghost"] = false,
                    });
                    env.DrawItem(new JObject
                    {
                        ["text"] = J.FmtTime(env.Cut!.Start),
                        ["font"] = MonoF(env),
                        ["size"] = ls,
                        ["track"] = 0.15,
                        ["align"] = "left",
                        ["x"] = ax0,
                        ["y"] = H * 0.1 + ls * 1.6 + sl,
                        ["color"] = Col(sc, "sub"),
                        ["alpha"] = ca,
                        ["ghost"] = false,
                    });
                }
                var by = H * 0.9 - cm.H / 2;
                env.DrawItem(new JObject
                {
                    ["text"] = ct,
                    ["font"] = BodyF(env),
                    ["size"] = cs,
                    ["lead"] = 1.5,
                    ["align"] = "left",
                    ["x"] = ax0,
                    ["y"] = by - sl,
                    ["color"] = Col(sc, "fg"),
                    ["alpha"] = ca * 0.9,
                    ["ghost"] = false,
                });
                var rom = RomajiOf(env);
                if (rom != null && rom != copy)
                    env.DrawItem(new JObject
                    {
                        ["text"] = rom,
                        ["font"] = MonoF(env),
                        ["size"] = ls * 0.85,
                        ["track"] = 0.2,
                        ["align"] = "left",
                        ["x"] = ax0,
                        ["y"] = by - cm.H / 2 - ls * 1.4 - sl,
                        ["color"] = Col(sc, "sub"),
                        ["alpha"] = ca,
                        ["ghost"] = false,
                    });
            }
            ctx.Save();
            ctx.Translate((float)cx, (float)(H / 2));
            ctx.RotateDegrees((float)rot);
            var bb = JLayouts.MainDraw(env, new JObject
            {
                ["text"] = text,
                ["font"] = font,
                ["size"] = size,
                ["x"] = 0,
                ["y"] = 0,
                ["track"] = 0.02,
                ["lead"] = 1.02,
                ["color"] = Col(sc, "fg"),
            });
            ctx.Restore();
            var B = Box(cx - m.H / 2, H / 2 - m.W / 2, cx + m.H / 2, H / 2 + m.W / 2);
            env.Body = bb != null ? B : null;
        },
    });

    // ---------------- edgeFrame（JS L1912-1979） ----------------

    private static void RegisterEdgeFrame() => Reg0("edgeFrame", new LayoutDef
    {
        Name = "外周",
        Tags = new[] { "graphic", "editorial", "glitch" },
        W = 0.9,
        Fits = n => n <= 16,
        Busy = true,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(JLayouts.FontsOf(st, "display", "serif")),
            ["edgeFont"] = rng.Pick(new[] { "body", "mono" }),
            ["sep"] = rng.Pick(new[] { "　／　", "　・　", "　—　" }),
            ["speed"] = rng.Range(0.6, 1.2) * rng.Pick(new[] { 1.0, -1.0 }),
            ["corner"] = rng.Pick(new[] { "square", "cross" }),
            ["inner"] = rng.Chance(0.6),
        },
        Draw = env =>
        {
            var W = env.W;
            var H = env.H;
            var sc = env.Sc!;
            var p = env.P;
            var u = U(env);
            var port = IsPort(env);
            var ctx = env.Ctx;
            var d = u * 0.045;
            var es = u * 0.024;
            var band = es * 1.7;
            var outA = Tout(env);
            var ef = S(p, "edgeFont") == "mono" ? MonoF(env) : BodyF(env);
            var unit = Flat(JLayouts.OrStr(env.Cut!.LineText, env.Cut!.Text)) + S(p, "sep");
            var per = Math.Max(1, RowW(unit, ef, es) + JLayouts.CpLen(unit) * es * 0.08);
            var edges = new[]
            {
                new[] { d + band, d, W - 2 * d - 2 * band, 0.0 },
                new[] { W - d, d + band, H - 2 * d - 2 * band, 90.0 },
                new[] { W - d - band, H - d, W - 2 * d - 2 * band, 180.0 },
                new[] { d, H - d - band, H - 2 * d - 2 * band, 270.0 }
            };
            if (env.Pass == "main")
            {
                for (var k = 0; k < edges.Length; k++)
                {
                    var ed = edges[k];
                    var e = Tin(env, k * 0.07, 0.5, J.E.InOutCubic) * outA;
                    if (e <= 0.001) continue;
                    var reps = Math.Min(60, (int)Math.Ceiling(ed[2] / per) + 2);
                    var off = (env.Ltb * D(p, "speed") * u * 0.07 % per + per) % per;
                    ctx.Save();
                    ctx.Translate((float)ed[0], (float)ed[1]);
                    ctx.RotateDegrees((float)ed[3]);
                    ctx.ClipRect(new SKRect(0, (float)(-band / 2), (float)(ed[2] * e), (float)(band / 2)));
                    JLayouts.FastRow(env, JLayouts.Repeat(unit, reps), ef, es, -per + off, 0, es * 0.08,
                        Col(sc, "sub"), 0.85);
                    ctx.Restore();
                }
            }
            // 四角标记
            var cq = J.E.OutBack(J.Clamp((env.Lt - 0.1) / 0.3), 2) * outA;
            if (cq > 0)
            {
                var corners = new[] { new[] { d, d }, new[] { W - d, d }, new[] { W - d, H - d }, new[] { d, H - d } };
                foreach (var q in corners)
                {
                    if (S(p, "corner") == "square")
                    {
                        var qq = es * 0.55 * cq;
                        env.Rect(q[0] - qq / 2, q[1] - qq / 2, qq, qq, Col(sc, "accent"), 1, false);
                    }
                    else
                    {
                        var qq = es * 0.6 * cq;
                        var lw = Math.Max(1.5, es * 0.08);
                        env.Line(new[] { new[] { q[0] - qq, q[1] }, new[] { q[0] + qq, q[1] } }, Col(sc, "accent"), lw, 1, false);
                        env.Line(new[] { new[] { q[0], q[1] - qq }, new[] { q[0], q[1] + qq } }, Col(sc, "accent"), lw, 1, false);
                    }
                }
            }
            if (JLayouts.Truthy(p["inner"]))
            {
                var e = Tin(env, 0.15, 0.8, J.E.InOutCubic) * outA;
                var g = d + band * 0.95;
                env.PolyPartial(new[]
                {
                    new[] { g, g }, new[] { W - g, g }, new[] { W - g, H - g }, new[] { g, H - g }, new[] { g, g }
                }, e, Col(sc, "sub"), 1.2, 0.45, false);
            }
            var font = S(p, "font")!;
            var text = Brk(env.Cut!.Text.Trim(), port ? 6 : 11);
            var o = new JObject { ["track"] = 0.04, ["lead"] = 1.15 };
            var size = Math.Min(JLayouts.FitSize(text, font, W * 0.72, H * 0.5, o), u * 0.22);
            var bb = JLayouts.MainDraw(env, new JObject
            {
                ["text"] = text,
                ["font"] = font,
                ["size"] = size,
                ["x"] = W / 2,
                ["y"] = H / 2,
                ["track"] = 0.04,
                ["lead"] = 1.15,
                ["color"] = Col(sc, "fg"),
            });
            env.Body = bb ?? JLayouts.CenterBb(env, null);
        },
    });

    // ---------------- perspective（JS L1981-2051） ----------------

    private static void RegisterPerspective() => Reg0("perspective", new LayoutDef
    {
        Name = "奥行き",
        Tags = new[] { "graphic", "emotional", "glitch" },
        W = 0.9,
        Emph = 1.3,
        EnterBias = new JObject { ["zoom"] = 1.6, ["stretch"] = 1.3 },
        Fits = n => n <= 14,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(JLayouts.FontsOf(st, "display")),
            ["mode"] = rng.Pick(new[] { "floor", "side", "floor" }),
            ["copies"] = rng.Int(4, 6),
            ["speed"] = rng.Range(0.25, 0.45),
            ["guides"] = rng.Chance(0.7),
            ["vx"] = rng.Pick(new[] { 1.0, -1.0 }),
        },
        Draw = env =>
        {
            var W = env.W;
            var H = env.H;
            var sc = env.Sc!;
            var p = env.P;
            var u = U(env);
            var port = IsPort(env);
            var font = S(p, "font")!;
            var text = Brk(env.Cut!.Text.Trim(), port ? 6 : 12);
            var o = new JObject { ["track"] = 0.03, ["lead"] = 1.1 };
            var side = S(p, "mode") == "side";
            var size = Math.Min(JLayouts.FitSize(text, font, W * (side ? 0.7 : 0.84), H * 0.22, o), u * 0.18);
            var m = Meas(text, font, size, o);
            var vx = D(p, "vx");
            var x0 = side ? W / 2 - vx * W * 0.12 : W / 2;
            var y0 = H * 0.74 - m.H / 2 + size * 0.5;
            var V = side ? new[] { W / 2 + vx * W * 0.36, H * 0.12 } : new[] { W / 2, H * 0.1 };
            var outA = Tout(env);
            var K = I(p, "copies");
            var ph = env.Ltb * D(p, "speed") % 1;
            var diag = Math.Sqrt(Math.Pow(x0 - V[0], 2) + Math.Pow(y0 - V[1], 2));
            var Dd = diag * (Math.Abs(y0 - V[1]) / Math.Max(1, diag));
            var sy0 = side ? 1 : 0.8;
            var X = 2 * Dd / (m.H * sy0 * 1.35);
            var r = J.Clamp((X - 1) / (X + 1), 0.45, 0.8);
            (double K, double X, double Y) At(double z)
            {
                var k = Math.Pow(r, z);
                return (k, V[0] + (x0 - V[0]) * k, V[1] + (y0 - V[1]) * k);
            }
            // 从前排文字连向消失点的透视线
            if (JLayouts.Truthy(p["guides"]))
            {
                var e = Tin(env, 0, 0.8, J.E.InOutCubic) * outA;
                if (e > 0)
                {
                    var hw = m.W / 2 + size * 0.3;
                    var yb = y0 + m.H / 2 + size * 0.12;
                    foreach (var q in new[] { new[] { x0 - hw, yb }, new[] { x0 + hw, yb } })
                        env.PolyPartial(new[] { q, V }, e, Col(sc, "sub"), 1.2, 0.4, false);
                    env.Circle(V[0], V[1], u * 0.005, Col(sc, "accent"), null, 0, e, false);
                }
            }
            // 退向纵深的复制层，随时间缓慢漂远
            var ca = Tin(env, 0.05, 0.4, J.E.OutCubic) * outA;
            for (var j = K; j >= 1; j--)
            {
                var z = j + ph;
                var q = At(z);
                var a = ca * J.Clamp((z - 1) / 0.6) * J.Clamp((K + 1 - z) / 1.2) * (0.8 - 0.45 * (z / (K + 1)));
                if (a <= 0.01) continue;
                var sk = side ? -vx * 14 * (1 - q.K) : 0;
                env.DrawItem(new JObject
                {
                    ["text"] = text,
                    ["font"] = font,
                    ["size"] = size * q.K,
                    ["sy"] = side ? 1 : 0.8,
                    ["skew"] = sk,
                    ["x"] = q.X,
                    ["y"] = q.Y,
                    ["track"] = 0.03,
                    ["lead"] = 1.1,
                    ["color"] = Col(sc, "sub"),
                    ["alpha"] = a,
                    ["ghost"] = false,
                });
            }
            var bb = JLayouts.MainDraw(env, new JObject
            {
                ["text"] = text,
                ["font"] = font,
                ["size"] = size,
                ["x"] = x0,
                ["y"] = y0,
                ["track"] = 0.03,
                ["lead"] = 1.1,
                ["color"] = Col(sc, "fg"),
            });
            env.Body = bb ?? Box(x0 - m.W / 2, y0 - m.H / 2, x0 + m.W / 2, y0 + m.H / 2);
        },
    });

    // ---------------- hanko（JS L2061-2155） ----------------

    private static void RegisterHanko() => Reg0("hanko", new LayoutDef
    {
        Name = "落款",
        Tags = new[] { "calm", "emotional", "editorial" },
        W = 0.8,
        Emph = 1.3,
        EnterBias = new JObject { ["blur"] = 1.6, ["wipe"] = 1.3, ["type"] = 1.2 },
        Fits = n => n >= 1 && n <= 10,
        Portrait = 1.2,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(JLayouts.FontsOf(st, "serif")),
            ["sealFont"] = rng.Pick(JLayouts.FontsOf(st, "serif", "display")),
            ["vert"] = cut.H > cut.W * 1.08 ? true : rng.Chance(0.6),
            ["seal"] = rng.Pick(new[] { "haku", "shu", "haku" }),
            ["rot"] = rng.Range(-7, 7),
        },
        Draw = env =>
        {
            var W = env.W;
            var H = env.H;
            var sc = env.Sc!;
            var p = env.P;
            var u = U(env);
            var port = IsPort(env);
            var ctx = env.Ctx;
            var font = S(p, "font")!;
            var sealFont = S(p, "sealFont")!;
            var text = Strip(env.Cut!.Text);
            var n = JLayouts.GlyphCount(text);
            var outA = Tout(env);
            double size, Sz, sx, sy;
            BBox bbox;
            JObject it;
            if (JLayouts.Truthy(p["vert"]))
            {
                var cols = n > (port ? 8 : 6) ? 2 : 1;
                var vt = cols > 1 ? Brk(text, (int)Math.Ceiling(n / 2.0)) : text;
                var per = vt.Split('\n').Max(l => JLayouts.GlyphCount(l));
                size = Math.Min(Math.Min(H * 0.7 / Math.Max(per, 2.5), u * 0.2), W * 0.3 / cols);
                Sz = size * (n <= 2 ? 1.35 : 1.5);
                const double lead = 1.35;
                var bw = size * (1 + (cols - 1) * lead);
                var colH = per * size;
                var x = W / 2 + Sz * 0.45 + (cols - 1) * size * lead / 2;
                var top = H / 2 - colH / 2 - Sz * 0.25;
                it = new JObject
                {
                    ["text"] = vt,
                    ["font"] = font,
                    ["size"] = size,
                    ["x"] = x,
                    ["y"] = top,
                    ["vertical"] = true,
                    ["align"] = "left",
                    ["lead"] = lead,
                    ["color"] = Col(sc, "fg"),
                };
                bbox = Box(x - bw / 2, top, x + bw / 2, top + colH);
                var lastLen = JLayouts.GlyphCount(vt.Split('\n')[^1]);
                sx = x - bw / 2 - Sz * 0.72;
                sy = Math.Max(top + lastLen * size, top + Sz * 0.5) + Sz * 0.1;
            }
            else
            {
                size = Math.Min(JLayouts.FitSize(text, font, W * 0.6, H * 0.3), u * 0.17);
                Sz = size * 1.7;
                var m = Meas(text, font, size);
                var x = W / 2 - Sz * 0.5;
                var y = H / 2;
                it = new JObject
                {
                    ["text"] = text,
                    ["font"] = font,
                    ["size"] = size,
                    ["x"] = x,
                    ["y"] = y,
                    ["color"] = Col(sc, "fg"),
                };
                bbox = Box(x - m.W / 2, y - m.H / 2, x + m.W / 2, y + m.H / 2);
                sx = x + m.W / 2 + Sz * 0.75;
                sy = y + size * 0.3;
            }
            var bb = JLayouts.MainDraw(env, it);
            // 歌词落定后印章砸下来，带落点抖动
            var t0 = env.Cut!.InDur * 0.7 + 0.08;
            var xx = (env.Lt - t0) / 0.2;
            if (xx > 0 && outA > 0)
            {
                var seal = S(p, "seal");
                var sc2 = J.Lerp(1.55, 1, J.E.OutBack(J.Clamp(xx), 1.4));
                var a = J.Clamp(xx * 3) * outA;
                var shake = xx > 1 && xx < 1.8 ? J.Rs((int)env.Step, 91) * Sz * 0.015 * (1.8 - xx) : 0;
                ctx.Save();
                ctx.Translate((float)(sx + shake), (float)sy);
                ctx.RotateDegrees((float)D(p, "rot"));
                ctx.Scale((float)sc2, (float)sc2);
                var pts = new List<double[]>();
                const int M = 11;
                var seed = env.Cut!.Seed;
                double Jit(int i, int k) => J.Rs((int)seed, i, k, 3) * Sz * 0.014;
                for (var i = 0; i < M; i++) pts.Add(new[] { -Sz / 2 + Sz * i / M, -Sz / 2 + Jit(i, 1) });
                for (var i = 0; i < M; i++) pts.Add(new[] { Sz / 2 + Jit(i, 2), -Sz / 2 + Sz * i / M });
                for (var i = 0; i < M; i++) pts.Add(new[] { Sz / 2 - Sz * i / M, Sz / 2 + Jit(i, 3) });
                for (var i = 0; i < M; i++) pts.Add(new[] { -Sz / 2 + Jit(i, 4), Sz / 2 - Sz * i / M });
                var G = SealGlyphs(text);
                var gn = G.Count;
                var cols2 = gn >= 3 ? 2 : 1;
                var rows2 = (int)Math.Ceiling(gn / (double)cols2);
                var inner = Sz * (seal == "haku" ? 0.8 : 0.72);
                var cw = inner / cols2;
                var chh = inner / rows2;
                var gs = Math.Min(cw, chh) * 0.92;
                var gsx = J.Clamp(cw / chh, 0.8, 1.7);
                var gcol = seal == "haku" ? Col(sc, "bg") : Col(sc, "accent");
                if (seal == "haku") env.Poly(pts.ToArray(), Col(sc, "accent"), a, true);
                else env.Line(CloseLoop(pts).ToArray(), Col(sc, "accent"), Sz * 0.07, a, true);
                for (var i = 0; i < G.Count; i++)
                {
                    // 印面从右列起，自上而下
                    var col = cols2 - 1 - (int)Math.Floor(i / (double)rows2);
                    var row = i % rows2;
                    var gx = -inner / 2 + cw * (col + 0.5);
                    var gy = -inner / 2 + chh * (row + 0.5);
                    env.DrawItem(new JObject
                    {
                        ["text"] = G[i],
                        ["font"] = sealFont,
                        ["size"] = gs,
                        ["sx"] = gs * gsx > cw ? cw / gs : gsx,
                        ["x"] = gx,
                        ["y"] = gy,
                        ["color"] = gcol,
                        ["alpha"] = a,
                        ["ghost"] = false,
                    });
                }
                if (seal == "haku")
                    for (var i = 0; i < 12; i++)
                        env.Circle(J.Rs((int)seed, i, 5) * Sz * 0.46, J.Rs((int)seed, i, 6) * Sz * 0.46,
                            Sz * J.Rr(0.004, 0.012, (int)seed, i, 7), Col(sc, "bg"), null, 0, a * 0.9, false);
                ctx.Restore();
            }
            env.Body = bb != null ? bbox : null;
        },
    });

    // ---------------- genkou（JS L2157-2253） ----------------

    private static void RegisterGenkou() => Reg0("genkou", new LayoutDef
    {
        Name = "原稿用紙",
        Tags = new[] { "calm", "editorial", "emotional" },
        W = 0.8,
        EnterBias = new JObject { ["type"] = 2.2, ["blur"] = 1.4, ["assemble"] = 1.3 },
        Fits = n => n <= 22,
        Portrait = 1.2,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(JLayouts.FontsOf(st, "serif")),
            ["lineC"] = rng.Pick(new[] { "accent", "sub" }),
            ["indent"] = rng.Chance(0.5),
            ["pad"] = rng.Int(2, 4),
        },
        Draw = env =>
        {
            var W = env.W;
            var H = env.H;
            var sc = env.Sc!;
            var p = env.P;
            var u = U(env);
            var port = IsPort(env);
            var chars = JLayouts.CodePoints(Strip(env.Cut!.Text));
            var n = chars.Count;
            var font = S(p, "font")!;
            var ind = JLayouts.Truthy(p["indent"]) ? 1 : 0;
            var maxR = port ? 14 : 10;
            var used = Math.Max(1, (int)Math.Ceiling((n + ind) / (double)maxR));
            var Rd = Math.Max(7, Math.Ceiling((n + ind) / (double)used) + (used == 1 && n + ind < maxR ? 1 : 0));
            // 空文本时 JS 会算出 Infinity 行数（什么都不画）；这里按 7 行兜底，正常路径不受影响
            var R = double.IsInfinity(Rd) ? 7 : (int)Rd;
            const double gapK = 0.3;
            var cell = Math.Min(H * 0.78 / R, W * 0.9 / ((used + 2) * (1 + gapK)));
            var colW = cell * (1 + gapK);
            var pad = I(p, "pad");
            var C = Math.Max(used + 2, Math.Min((int)Math.Floor(W * 0.92 / colW), used + pad * 2));
            var gw = C * colW - cell * gapK;
            var gh = R * cell;
            var gx0 = W / 2 - gw / 2;
            var gy0 = H / 2 - gh / 2;
            // 起始列号，从右往左数
            var cStart = (int)Math.Floor((C - used) / 2.0);
            double ColX(int c) => gx0 + gw - cell - c * colW;
            var lc = S(p, "lineC") == "accent" ? Col(sc, "accent") : Col(sc, "sub");
            var outA = Tout(env);
            var lw = Math.Max(1.5, cell * 0.02);
            for (var c = 0; c < C; c++)
            {
                var e = Tin(env, c * 0.035, 0.45, J.E.InOutCubic) * outA;
                if (e <= 0) continue;
                var x = ColX(c);
                var dist = Math.Abs(c - (cStart + (used - 1) / 2.0)) / Math.Max(1, C / 2.0);
                var a = 0.85 * (1 - dist * 0.6);
                env.Line(new[] { new[] { x, gy0 }, new[] { x, gy0 + gh * e } }, lc, lw, a, false);
                env.Line(new[] { new[] { x + cell, gy0 }, new[] { x + cell, gy0 + gh * e } }, lc, lw, a, false);
                for (var r = 0; r <= R; r++)
                {
                    var y = gy0 + r * cell;
                    if (y > gy0 + gh * e + 0.5) break;
                    env.Line(new[] { new[] { x, y }, new[] { x + cell, y } }, lc, lw, a * (r == 0 || r == R ? 1 : 0.8), false);
                }
            }
            var fa = Tin(env, 0.3, 0.4, J.E.OutCubic) * outA;
            if (fa > 0)
            {
                var fs = Math.Max(11, cell * 0.14);
                env.DrawItem(new JObject
                {
                    ["text"] = "(" + R + "×" + C + ")",
                    ["font"] = MonoF(env),
                    ["size"] = fs,
                    ["align"] = "left",
                    ["x"] = gx0,
                    ["y"] = gy0 + gh + fs * 1.4,
                    ["color"] = lc,
                    ["alpha"] = fa * 0.8,
                    ["ghost"] = false,
                });
                env.DrawItem(new JObject
                {
                    ["text"] = "No." + LineNo(env),
                    ["font"] = MonoF(env),
                    ["size"] = fs,
                    ["align"] = "right",
                    ["x"] = gx0 + gw,
                    ["y"] = gy0 + gh + fs * 1.4,
                    ["color"] = lc,
                    ["alpha"] = fa * 0.8,
                    ["ghost"] = false,
                });
            }
            BBox? bb = null;
            for (var i = 0; i < chars.Count; i++)
            {
                var ch = chars[i];
                var k = i + ind;
                var c = cStart + (int)Math.Floor(k / (double)R);
                var r = k % R;
                var x = ColX(c) + cell / 2;
                var y = gy0 + (r + 0.5) * cell;
                var rot = 0.0;
                if (J.IsSmallKana(ch[0]))
                {
                    x += cell * 0.1;
                    y -= cell * 0.1;
                }
                if ("、。，．".Contains(ch))
                {
                    x += cell * 0.28;
                    y -= cell * 0.28;
                }
                if (J.VertRotate.Contains(ch) || J.IsLatin(ch[0])) rot = 90;
                bb = JLayouts.UnionBb(bb, JLayouts.MainDraw(env, new JObject
                {
                    ["text"] = ch,
                    ["font"] = font,
                    ["size"] = cell * 0.8,
                    ["x"] = x,
                    ["y"] = y,
                    ["rot"] = rot,
                    ["color"] = Col(sc, "fg"),
                    ["mi"] = i,
                }));
            }
            env.Body = bb ?? Box(ColX(cStart + used - 1), gy0, ColX(cStart) + cell, gy0 + gh);
        },
    });

    // ---------------- panels（JS L2255-2406） ----------------

    /// <summary>panels 的一格多边形（JS 挂在数组上的 along 属性）。</summary>
    private sealed class PanelPoly
    {
        public double[][] Pts = System.Array.Empty<double[]>();
        public double Along;
    }

    private static void RegisterPanels() => Reg0("panels", new LayoutDef
    {
        Name = "コマ割り",
        Tags = new[] { "pop", "graphic", "emotional" },
        W = 0.9,
        Emph = 1.4,
        EnterBias = new JObject { ["pop"] = 1.4, ["zoom"] = 1.3, ["slice"] = 1.2 },
        Fits = n => n >= 2 && n <= 18,
        Busy = true,
        Treat = "safe",
        Plan = (rng, cut, st) =>
        {
            var k = cut.N >= 6 ? 3 : 2;
            return new JObject
            {
                ["chunks"] = JA(SplitK(cut.Text, k, 2)),
                ["font"] = rng.Pick(JLayouts.FontsOf(st, "display", "serif")),
                ["acc"] = rng.Int(0, 2),
                ["fx"] = rng.Pick(new[] { "focus", "tone", "focus", "none" }),
                ["slant"] = rng.Range(5, 11),
                ["widths"] = new JArray(rng.Range(0.8, 1.25), rng.Range(0.8, 1.25), rng.Range(0.8, 1.25)),
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
            var ctx = env.Ctx;
            // 拉丁歌词从左往右读
            var ltr = !port && Planner.IsLatinText(env.Cut!.Text);
            var ch = StrArr(p["chunks"]);
            if (ch.Count == 0) ch = SplitK(env.Cut!.Text, 2, 2);
            var k = ch.Count;
            var font = S(p, "font")!;
            var m = u * 0.05;
            var g = u * 0.028;
            var lw = Math.Max(3, u * 0.0065);
            var outA = Tout(env);
            // 沿阅读轴分格：横屏右→左分列，竖屏上→下分行
            var L = port ? H - 2 * m : W - 2 * m;
            var cross = port ? W - 2 * m : H - 2 * m;
            var widths = (p["widths"] as JArray)?.Select(t => (double)t!).ToList() ?? new List<double> { 1, 1, 1 };
            var ws = new List<double>();
            for (var i = 0; i < ch.Count; i++)
            {
                var ww = i % 3 < widths.Count ? widths[i % 3] : 1;
                if (ww == 0) ww = 1;
                ws.Add(Math.Sqrt(JLayouts.GlyphCount(ch[i]) + 2) * ww);
            }
            var tot = ws.Sum();
            var sl = Math.Min(Math.Tan(D(p, "slant") * J.DEG) * cross / 2, L / k * 0.18);
            var bounds = new List<double> { 0 };
            var acc2 = 0.0;
            foreach (var ww in ws)
            {
                acc2 += ww;
                bounds.Add(acc2 / tot * L);
            }
            var polys = new List<PanelPoly>();
            for (var i = 0; i < k; i++)
            {
                var a = bounds[i];
                var b = bounds[i + 1];
                var sa = i == 0 ? 0 : sl * (i % 2 != 0 ? 1 : -1);
                var sb = i == k - 1 ? 0 : sl * ((i + 1) % 2 != 0 ? 1 : -1);
                var a0 = a + (i == 0 ? 0 : g / 2);
                var b0 = b - (i == k - 1 ? 0 : g / 2);
                // 先在阅读轴坐标系 (t, c) 里算，c 的取值范围 [0, cross]
                var q = new[]
                {
                    new[] { a0 + sa, 0.0 }, new[] { b0 + sb, 0.0 },
                    new[] { b0 - sb, cross }, new[] { a0 - sa, cross }
                };
                var poly = q.Select(tc => port
                    ? new[] { m + tc[1], m + tc[0] }
                    : ltr ? new[] { m + tc[0], m + tc[1] } : new[] { W - m - tc[0], m + tc[1] }).ToArray();
                polys.Add(new PanelPoly
                {
                    Pts = poly,
                    Along = Math.Min((b0 + sb) - (a0 + sa), (b0 - sb) - (a0 - sa)),
                });
            }
            var stag = Math.Max(0.01, env.Cut!.Stagger);
            var acc = I(p, "acc");
            var fx = S(p, "fx");
            BBox? bb = null;
            for (var i = 0; i < polys.Count; i++)
            {
                var poly = polys[i];
                var d = 0.06 + i * 0.16;
                var e = J.E.InOutCubic(J.Clamp((env.Lt - d) / 0.3));
                if (e <= 0) continue;
                // 沿阅读方向擦入
                var rev = poly.Pts.ToList();
                if (e < 1)
                {
                    var xs = rev.Select(q2 => port ? q2[1] : ltr ? q2[0] : -q2[0]).ToList();
                    var lo = xs.Min();
                    var hi = xs.Max();
                    rev = ClipHalf(rev, port ? 0 : ltr ? 1 : -1, port ? 1 : 0, lo + (hi - lo) * e);
                }
                if (rev.Count < 3) continue;
                var accent = i == acc % k;
                var cx = rev.Sum(q2 => q2[0]) / 4;
                var cy = rev.Sum(q2 => q2[1]) / 4;
                var pw = port ? cross : poly.Along;
                var ph = port ? poly.Along : cross;
                var iw = pw * 0.8;
                var ih = ph * 0.8;
                var vert = ih > iw * 1.25 && !HasLatin(ch[i]);
                var c0 = vert ? Strip(ch[i]) : ch[i];
                var cn = JLayouts.GlyphCount(c0);
                var txt = c0;
                var size = JLayouts.FitSize(c0, font, Math.Max(10, iw), Math.Max(10, ih),
                    new JObject { ["vertical"] = vert, ["lead"] = 1.15 });
                if (cn >= 4)
                {
                    var t2 = Brk(c0, (int)Math.Ceiling(cn / 2.0));
                    var s2 = JLayouts.FitSize(t2, font, Math.Max(10, iw), Math.Max(10, ih),
                        new JObject { ["vertical"] = vert, ["lead"] = 1.15 });
                    if (s2 > size * 1.15)
                    {
                        txt = t2;
                        size = s2;
                    }
                }
                size = Math.Min(size, u * 0.24);
                if (accent) env.Poly(rev.ToArray(), Col(sc, "ink"), outA, false);
                if (env.Pass == "main" && outA > 0)
                {
                    ctx.Save();
                    using (var path = PathOf(rev)) ctx.ClipPath(path);
                    if (accent && fx == "focus")
                    {
                        var R0 = Math.Sqrt(pw * pw + ph * ph);
                        var rin = size * (vert ? 0.9 : 0.75)
                                  + Math.Max(0, (vert ? JLayouts.GlyphCount(txt) * size : Meas(txt, font, size).W) * 0.35);
                        for (var j = 0; j < 48; j++)
                        {
                            var ang = (j / 48.0 + J.R((int)env.Cut!.Seed, j, 61) * 0.01) * J.TAU;
                            var r1 = rin * (1 + J.R((int)env.Cut!.Seed, j, 62) * 0.5);
                            env.Line(new[]
                            {
                                new[] { cx + Math.Cos(ang) * R0, cy + Math.Sin(ang) * R0 },
                                new[] { cx + Math.Cos(ang) * r1, cy + Math.Sin(ang) * r1 }
                            }, OnCol(sc, Col(sc, "ink")), 1 + J.R((int)env.Cut!.Seed, j, 63) * 2.5, 0.22 * outA, false);
                        }
                    }
                    if (!accent && fx == "tone" && i == (acc + 1) % k)
                    {
                        using var paint = new SKPaint
                        {
                            IsAntialias = true,
                            Shader = JText.TextPattern(ctx, "dots", Col(sc, "sub"), null, u * 0.12,
                                env.Scale != 0 ? env.Scale : 1),
                            Color = new SKColor(255, 255, 255, (byte)Math.Round(J.Clamp(0.3 * outA) * 255)),
                            BlendMode = env.Blend,
                        };
                        using var path = PathOf(rev);
                        ctx.DrawPath(path, paint);
                    }
                    ctx.Restore();
                }
                env.Line(CloseLoop(rev).ToArray(), Col(sc, "fg"), lw, outA, false);
                ctx.Save();
                using (var path2 = PathOf(rev)) ctx.ClipPath(path2);
                var it = vert
                    ? new JObject
                    {
                        ["text"] = txt,
                        ["font"] = font,
                        ["size"] = size,
                        ["x"] = cx,
                        ["y"] = cy,
                        ["vertical"] = true,
                        ["color"] = accent ? OnCol(sc, Col(sc, "ink")) : Col(sc, "fg"),
                        ["mi"] = (d + 0.08) / stag,
                    }
                    : new JObject
                    {
                        ["text"] = txt,
                        ["font"] = font,
                        ["size"] = size,
                        ["x"] = cx,
                        ["y"] = cy,
                        ["lead"] = 1.15,
                        ["color"] = accent ? OnCol(sc, Col(sc, "ink")) : Col(sc, "fg"),
                        ["mi"] = (d + 0.08) / stag,
                    };
                bb = JLayouts.UnionBb(bb, JLayouts.MainDraw(env, it));
                ctx.Restore();
            }
            env.Body = bb ?? Box(m, m, W - m, H - m);
        },
    });

    // ---------------- filmstrip（JS L2408-2524） ----------------

    private static void RegisterFilmstrip() => Reg0("filmstrip", new LayoutDef
    {
        Name = "フィルム",
        Tags = new[] { "emotional", "calm", "graphic" },
        W = 0.8,
        Fits = n => n >= 1 && n <= 18,
        Busy = true,
        Treat = "safe",
        Portrait = 0.8,
        Plan = (rng, cut, st) => new JObject
        {
            ["chunks"] = JA(SplitK(cut.Text, Math.Min(3, cut.N), cut.N <= 4 ? 3 : 2)),
            ["font"] = rng.Pick(JLayouts.FontsOf(st, "display", "serif")),
            ["dir"] = rng.Pick(new[] { 1.0, -1.0 }),
            ["tone"] = rng.Pick(new[] { "ink", "fg" }),
            ["codes"] = rng.Chance(0.75),
            ["tilt"] = rng.Pick(new[] { 0.0, 0.0, -3.0, 3.0 }),
        },
        Draw = env =>
        {
            var W = env.W;
            var H = env.H;
            var sc = env.Sc!;
            var p = env.P;
            var u = U(env);
            var port = IsPort(env);
            var ctx = env.Ctx;
            var font = S(p, "font")!;
            var chunks = StrArr(p["chunks"]);
            var ch = chunks.Count > 0 ? chunks.Take(3).ToList() : new List<string> { env.Cut!.Text };
            var slots = ch.Count == 3
                ? ch.Select(x => (string?)x).ToList()
                : ch.Count == 2 ? new List<string?> { ch[0], ch[1], null } : new List<string?> { null, ch[0], null };
            var v = port; // 竖屏时胶片竖放
            var A = v ? H : W;
            var Bd = v ? W : H; // A 沿带方向，Bd 横跨带方向
            var fw0 = Math.Min(A * 0.88 / 3.2, Bd * 0.62 * 1.3); // 单帧沿带方向的长度
            var fh = fw0 / 1.3;
            var band = fh / 0.72;
            var gap = fw0 * 0.08;
            var pitch = fw0 + gap;
            var stripC = S(p, "tone") == "fg" ? Col(sc, "fg") : Col(sc, "ink");
            var tcol = OnCol(sc, Col(sc, "bg")) == Col(sc, "bg") ? Col(sc, "fg") : OnCol(sc, Col(sc, "bg"));
            var eIn = Tin(env, 0, 0.6, J.E.OutExpo);
            var eOut = J.E.InExpo(env.POut);
            var dir = D(p, "dir");
            var off = (1 - eIn) * A * 0.9 * dir - eOut * A * 1.4 * dir + (env.Ltb - env.Cut!.Dur / 2) * u * 0.03 * dir;
            var fa = 1 - J.Smooth(0.55, 1, env.POut);
            double[] P2(double a, double b) => v ? new[] { W / 2 + b, a } : new[] { a, H / 2 + b };
            ctx.Save();
            ctx.Translate((float)(W / 2), (float)(H / 2));
            ctx.RotateDegrees((float)D(p, "tilt"));
            ctx.Translate((float)(-W / 2), (float)(-H / 2));
            // 胶片带
            var bandLen = A * 1.6;
            var c0 = A / 2 + off;
            if (v) env.Rect(W / 2 - band / 2, c0 - bandLen / 2, band, bandLen, stripC, fa, false);
            else env.Rect(c0 - bandLen / 2, H / 2 - band / 2, bandLen, band, stripC, fa, false);
            // 齿孔
            var hp = band * 0.11;
            var hw = hp * 0.55;
            var hh = hp * 0.72;
            var hm = (band - fh) / 4;
            var nH = Math.Min(120, (int)Math.Ceiling(bandLen / hp));
            for (var i = 0; i < nH; i++)
            {
                var a = c0 - bandLen / 2 + (i + 0.5) * hp;
                if (a < -hp || a > A + hp) continue;
                foreach (var sd in new[] { -1.0, 1.0 })
                {
                    var q = P2(a, sd * (band / 2 - hm));
                    if (v) env.RRect(q[0] - hh / 2, q[1] - hw / 2, hh, hw, hw * 0.25, Col(sc, "bg"), fa, false);
                    else env.RRect(q[0] - hw / 2, q[1] - hh / 2, hw, hh, hw * 0.25, Col(sc, "bg"), fa, false);
                }
            }
            // 片边标记码
            if (JLayouts.Truthy(p["codes"]))
            {
                var cs = Math.Max(10, hm * 0.9);
                var ca = J.Clamp(eIn * 2 - 1) * fa;
                for (var i = -2; i <= 2; i++)
                {
                    var a = c0 + i * pitch - pitch / 2;
                    var q = P2(a, (band / 2 - hm * 2.2) * (v ? 1 : -1));
                    env.DrawItem(new JObject
                    {
                        ["text"] = "▸" + (12 + i + env.Cut!.Index) + (i % 2 != 0 ? "A" : ""),
                        ["font"] = MonoF(env),
                        ["size"] = cs,
                        ["x"] = q[0],
                        ["y"] = q[1],
                        ["rot"] = v ? 90 : 0,
                        ["color"] = Col(sc, "accent"),
                        ["alpha"] = ca * 0.9,
                        ["ghost"] = false,
                    });
                }
            }
            // 帧格，整条胶片共用一个字号
            var stag = Math.Max(0.01, env.Cut!.Stagger);
            var fwX0 = v ? fh : fw0;
            var fhY0 = v ? fw0 : fh;
            var fsz = Math.Min(u * 0.2, slots.Where(t => !string.IsNullOrEmpty(t))
                .Select(t => JLayouts.FitSize(Brk(t!, v ? 6 : 4), font, fwX0 * 0.8, fhY0 * 0.7,
                    new JObject { ["lead"] = 1.1 })).Min());
            BBox? bb = null;
            for (var i = 0; i < slots.Count; i++)
            {
                var t = slots[i];
                var a = c0 + (i - 1) * pitch;
                var q = P2(a, 0);
                var fx0 = v ? q[0] - fh / 2 : q[0] - fw0 / 2;
                var fy0 = v ? q[1] - fw0 / 2 : q[1] - fh / 2;
                var fwX = v ? fh : fw0;
                var fhY = v ? fw0 : fh;
                env.RRect(fx0, fy0, fwX, fhY, fh * 0.04, Col(sc, "bg"), fa, false);
                ctx.Save();
                ctx.ClipRect(new SKRect((float)fx0, (float)fy0, (float)(fx0 + fwX), (float)(fy0 + fhY)));
                if (!string.IsNullOrEmpty(t))
                {
                    var tt = Brk(t!, v ? 6 : 4);
                    bb = JLayouts.UnionBb(bb, JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = tt,
                        ["font"] = font,
                        ["size"] = fsz,
                        ["x"] = q[0],
                        ["y"] = q[1],
                        ["lead"] = 1.1,
                        ["color"] = tcol,
                        ["mi"] = 0.15 * i / stag,
                    }));
                }
                else
                {
                    var r = Math.Min(fwX, fhY) * 0.3;
                    var la = J.Clamp(eIn * 2 - 1) * fa;
                    env.Circle(q[0], q[1], r, null, Col(sc, "sub"), 1.5, 0.5 * la, false);
                    env.Line(new[] { new[] { q[0] - r * 1.4, q[1] }, new[] { q[0] + r * 1.4, q[1] } }, Col(sc, "sub"), 1.2, 0.4 * la, false);
                    env.Line(new[] { new[] { q[0], q[1] - r * 1.4 }, new[] { q[0], q[1] + r * 1.4 } }, Col(sc, "sub"), 1.2, 0.4 * la, false);
                    env.DrawItem(new JObject
                    {
                        ["text"] = (i == 0 ? 3 : 1).ToString(CultureInfo.InvariantCulture),
                        ["font"] = font,
                        ["size"] = r * 1.1,
                        ["x"] = q[0],
                        ["y"] = q[1],
                        ["color"] = Col(sc, "sub"),
                        ["alpha"] = 0.6 * la,
                        ["ghost"] = false,
                    });
                }
                ctx.Restore();
            }
            ctx.Restore();
            var ext = v ? fh : fw0 * 1.6;
            var extY = v ? fw0 * 1.6 : fh;
            env.Body = bb ?? Box(W / 2 - ext / 2, H / 2 - extY / 2, W / 2 + ext / 2, H / 2 + extY / 2);
        },
    });

    // ---------------- quote（JS L2526-2594） ----------------

    private static void RegisterQuote() => Reg0("quote", new LayoutDef
    {
        Name = "引用",
        Tags = new[] { "editorial", "emotional", "calm" },
        W = 1,
        Emph = 1.2,
        EnterBias = new JObject { ["blur"] = 1.4, ["type"] = 1.3, ["wipe"] = 1.2 },
        Fits = n => n <= 20,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(JLayouts.FontsOf(st, "serif", "display")),
            ["markFont"] = rng.Pick(JLayouts.FontsOf(st, "serif")),
            ["marks"] = HasLatin(cut.Text) ? "latin" : rng.Pick(new[] { "kagi", "double", "kagi" }),
            ["markC"] = rng.Pick(new[] { "accent", "sub" }),
            ["attrib"] = rng.Chance(0.75),
        },
        Draw = env =>
        {
            var W = env.W;
            var H = env.H;
            var sc = env.Sc!;
            var p = env.P;
            var u = U(env);
            var port = IsPort(env);
            var font = S(p, "font")!;
            var markFont = S(p, "markFont")!;
            var text = Brk(env.Cut!.Text.Trim(), port ? 6 : 11);
            var o = new JObject { ["track"] = 0.04, ["lead"] = 1.25 };
            var size = Math.Min(JLayouts.FitSize(text, font, W * (port ? 0.58 : 0.62), H * 0.4, o), u * 0.16);
            var m = Meas(text, font, size, o);
            var attrib = JLayouts.Truthy(p["attrib"]);
            var cy = H / 2 - (attrib ? size * 0.25 : 0);
            var bx0 = W / 2 - m.W / 2;
            var bx1 = W / 2 + m.W / 2;
            var by0 = cy - m.H / 2;
            var by1 = cy + m.H / 2;
            var outA = Tout(env);
            var e = J.E.OutExpo(J.Clamp((env.Lt - 0.02) / 0.5)) * (1 - J.E.InCubic(env.POut));
            var marks = S(p, "marks")!;
            var mc = S(p, "markC") == "accent" ? Col(sc, "accent") : Col(sc, "sub");
            var latin = marks == "latin";
            var o1 = latin ? "\u201C" : marks == "double" ? "\u300E" : "\u300C";
            var c1 = latin ? "\u201D" : marks == "double" ? "\u300F" : "\u300D";
            var ia = InkBoxOf(markFont, o1);
            var ib = InkBoxOf(markFont, c1);
            var gap = size * 0.2;
            var inkW = Math.Max(0.05, Math.Max(ia.R - ia.L, ib.R - ib.L));
            var ms = Math.Min(Math.Min(Math.Max(size * (latin ? 3.2 : 4), m.H * 1.8), u * 0.5),
                (W * 0.94 - m.W - gap * 2) / (2 * inkW));
            var far = u * 0.35 * (1 - e);
            if (e > 0.001)
            {
                var al = J.Clamp(e * 1.5);
                var ax = bx0 - gap - ia.R * ms - far;
                var ay = by0 - size * 0.1 - ia.T * ms - far * 0.6;
                var bxx = bx1 + gap - ib.L * ms + far;
                var byy = (latin ? by1 - size * 0.95 - ib.T * ms : by1 + size * 0.1 - ib.B * ms) + far * 0.6;
                env.DrawItem(new JObject
                {
                    ["text"] = o1,
                    ["font"] = markFont,
                    ["size"] = ms,
                    ["x"] = ax,
                    ["y"] = ay,
                    ["color"] = mc,
                    ["alpha"] = al,
                    ["ghost"] = false,
                });
                env.DrawItem(new JObject
                {
                    ["text"] = c1,
                    ["font"] = markFont,
                    ["size"] = ms,
                    ["x"] = bxx,
                    ["y"] = byy,
                    ["color"] = mc,
                    ["alpha"] = al,
                    ["ghost"] = false,
                });
            }
            if (attrib)
            {
                var a = Tin(env, 0.35, 0.4, J.E.OutCubic) * outA;
                var ls = SmallSize(env);
                var at = "— " + (RomajiOf(env) ?? "No." + LineNo(env));
                var aw = Math.Min(W * 0.4, Meas(at, BodyF(env), ls, new JObject { ["track"] = 0.12 }).W);
                env.Line(new[]
                {
                    new[] { W / 2 - aw / 2 - ls * 2, by1 + size * 0.95 },
                    new[] { W / 2 - aw / 2 - ls * 0.8, by1 + size * 0.95 }
                }, mc, 1.5, a, false);
                env.DrawItem(new JObject
                {
                    ["text"] = at[2..],
                    ["font"] = BodyF(env),
                    ["size"] = ls,
                    ["track"] = 0.12,
                    ["x"] = W / 2,
                    ["y"] = by1 + size * 0.95,
                    ["color"] = Col(sc, "sub"),
                    ["alpha"] = a,
                    ["ghost"] = false,
                });
            }
            var bb = JLayouts.MainDraw(env, new JObject
            {
                ["text"] = text,
                ["font"] = font,
                ["size"] = size,
                ["x"] = W / 2,
                ["y"] = cy,
                ["track"] = 0.04,
                ["lead"] = 1.25,
                ["color"] = Col(sc, "fg"),
            });
            env.Body = bb ?? Box(bx0, by0, bx1, by1);
        },
    });

    // ---------------- ruler（JS L2596-2702） ----------------

    private static void RegisterRuler() => Reg0("ruler", new LayoutDef
    {
        Name = "寸法線",
        Tags = new[] { "graphic", "editorial", "glitch" },
        W = 0.8,
        Fits = n => n <= 16,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(JLayouts.FontsOf(st, "display", "serif", "body")),
            ["dims"] = rng.Pick(new[] { "topRight", "bottomLeft" }),
            ["ticks"] = rng.Chance(0.7),
            ["guides"] = rng.Chance(0.8),
            ["unit"] = rng.Pick(new[] { "px", "pt", "px" }),
        },
        Draw = env =>
        {
            var W = env.W;
            var H = env.H;
            var sc = env.Sc!;
            var p = env.P;
            var u = U(env);
            var port = IsPort(env);
            var font = S(p, "font")!;
            var text = Brk(env.Cut!.Text.Trim(), port ? 6 : 12);
            var o = new JObject { ["track"] = 0.04, ["lead"] = 1.15 };
            var size = Math.Min(JLayouts.FitSize(text, font, W * 0.66, H * 0.34, o), u * 0.19);
            var m = Meas(text, font, size, o);
            var cx = W / 2.0;
            var cy = H / 2.0;
            var x0 = cx - m.W / 2;
            var x1 = cx + m.W / 2;
            var y0 = cy - m.H / 2;
            var y1 = cy + m.H / 2;
            var outA = Tout(env);
            var e = Tin(env, 0.08, 0.75, J.E.InOutCubic) * outA;
            var lw = Math.Max(1.2, u * 0.0016);
            var ls = SmallSize(env) * 0.9;
            var ah = ls * 0.7;
            var top = S(p, "dims") == "topRight";
            if (e > 0)
            {
                if (JLayouts.Truthy(p["guides"]))
                {
                    var gx = W * 0.47 * e;
                    var gy = H * 0.47 * e;
                    Dash(env, x0, cy - gy, x0, cy + gy, 8, 8, Col(sc, "sub"), 1, 0.35);
                    Dash(env, x1, cy - gy, x1, cy + gy, 8, 8, Col(sc, "sub"), 1, 0.35);
                    Dash(env, cx - gx, y0, cx + gx, y0, 8, 8, Col(sc, "sub"), 1, 0.35);
                    Dash(env, cx - gx, y1, cx + gx, y1, 8, 8, Col(sc, "sub"), 1, 0.35);
                }
                // 横向尺寸标注
                var dy = top ? y0 - size * 0.55 : y1 + size * 0.55;
                var ext = top ? -1 : 1;
                var hx = m.W / 2 * e;
                env.Line(new[]
                {
                    new[] { x0, top ? y0 - size * 0.08 : y1 + size * 0.08 },
                    new[] { x0, dy + ext * ls * 0.6 }
                }, Col(sc, "sub"), lw, 0.8, false);
                env.Line(new[]
                {
                    new[] { x1, top ? y0 - size * 0.08 : y1 + size * 0.08 },
                    new[] { x1, dy + ext * ls * 0.6 }
                }, Col(sc, "sub"), lw, 0.8, false);
                env.Line(new[] { new[] { cx - hx, dy }, new[] { cx + hx, dy } }, Col(sc, "sub"), lw, 1, false);
                ArrowHead(env, cx - hx, dy, Math.PI, ah, Col(sc, "sub"), 1);
                ArrowHead(env, cx + hx, dy, 0, ah, Col(sc, "sub"), 1);
                var wv = (long)Math.Round(m.W * e, MidpointRounding.AwayFromZero);
                env.DrawItem(new JObject
                {
                    ["text"] = wv + " " + S(p, "unit"),
                    ["font"] = MonoF(env),
                    ["size"] = ls,
                    ["track"] = 0.08,
                    ["x"] = cx,
                    ["y"] = dy + ext * ls * 0.95,
                    ["color"] = Col(sc, "accent"),
                    ["alpha"] = J.Clamp(e * 2 - 0.3),
                    ["ghost"] = false,
                });
                // 纵向尺寸标注
                var dx = top ? x1 + size * 0.5 : x0 - size * 0.5;
                var ex = top ? 1 : -1;
                var vy = m.H / 2 * e;
                env.Line(new[]
                {
                    new[] { top ? x1 + size * 0.08 : x0 - size * 0.08, y0 },
                    new[] { dx + ex * ls * 0.6, y0 }
                }, Col(sc, "sub"), lw, 0.8, false);
                env.Line(new[]
                {
                    new[] { top ? x1 + size * 0.08 : x0 - size * 0.08, y1 },
                    new[] { dx + ex * ls * 0.6, y1 }
                }, Col(sc, "sub"), lw, 0.8, false);
                env.Line(new[] { new[] { dx, cy - vy }, new[] { dx, cy + vy } }, Col(sc, "sub"), lw, 1, false);
                ArrowHead(env, dx, cy - vy, -Math.PI / 2, ah, Col(sc, "sub"), 1);
                ArrowHead(env, dx, cy + vy, Math.PI / 2, ah, Col(sc, "sub"), 1);
                env.DrawItem(new JObject
                {
                    ["text"] = (long)Math.Round(m.H * e, MidpointRounding.AwayFromZero) + " " + S(p, "unit"),
                    ["font"] = MonoF(env),
                    ["size"] = ls,
                    ["track"] = 0.08,
                    ["x"] = dx + ex * ls * 1.1,
                    ["y"] = cy,
                    ["rot"] = top ? 90 : -90,
                    ["color"] = Col(sc, "accent"),
                    ["alpha"] = J.Clamp(e * 2 - 0.3),
                    ["ghost"] = false,
                });
                // 字符刻度和 n= 字数
                if (JLayouts.Truthy(p["ticks"]))
                {
                    var ty = top ? y1 + size * 0.28 : y0 - size * 0.28;
                    var sg = top ? 1 : -1;
                    var cells = m.Lay?["cells"] as JArray;
                    var a = J.Clamp(e * 1.6 - 0.4);
                    if (cells is { Count: > 0 })
                    {
                        var li = top ? (int)cells[^1]!["li"]! : 0;
                        var gl = cells.Where(g => (int)g!["li"]! == li && (string?)g["ch"] != " ").ToList();
                        if (gl.Count > 0)
                        {
                            var lx0 = cx + gl.Min(g => (double)g!["x"]! - (double)g["w"]! / 2);
                            var lx1 = cx + gl.Max(g => (double)g!["x"]! + (double)g["w"]! / 2);
                            env.Line(new[] { new[] { lx0, ty }, new[] { lx0 + (lx1 - lx0) * e, ty } }, Col(sc, "sub"), lw, 0.7 * a, false);
                            foreach (var g in gl)
                            {
                                var gx = cx + (double)g!["x"]! - (double)g["w"]! / 2;
                                if (gx <= lx0 + (lx1 - lx0) * e + 0.5)
                                    env.Line(new[] { new[] { gx, ty }, new[] { gx, ty + sg * ls * 0.5 } }, Col(sc, "sub"), lw, a, false);
                            }
                            env.Line(new[] { new[] { lx1, ty }, new[] { lx1, ty + sg * ls * 0.5 } }, Col(sc, "sub"), lw, a, false);
                            env.DrawItem(new JObject
                            {
                                ["text"] = "n=" + JLayouts.GlyphCount(env.Cut!.Text),
                                ["font"] = MonoF(env),
                                ["size"] = ls,
                                ["track"] = 0.08,
                                ["align"] = top ? "left" : "right",
                                ["x"] = top ? lx0 : lx1,
                                ["y"] = ty + sg * ls * 1.4,
                                ["color"] = Col(sc, "sub"),
                                ["alpha"] = a,
                                ["ghost"] = false,
                            });
                        }
                    }
                }
            }
            var bb = JLayouts.MainDraw(env, new JObject
            {
                ["text"] = text,
                ["font"] = font,
                ["size"] = size,
                ["x"] = cx,
                ["y"] = cy,
                ["track"] = 0.04,
                ["lead"] = 1.15,
                ["color"] = Col(sc, "fg"),
            });
            env.Body = bb ?? Box(x0, y0, x1, y1);
        },
    });

    // ---------------- searchBar（JS L2709-2808） ----------------

    private static void RegisterSearchBar() => Reg0("searchBar", new LayoutDef
    {
        Name = "検索窓",
        Tags = new[] { "pop", "graphic" },
        W = 0.6,
        EnterBias = new JObject { ["type"] = 3, ["scramble"] = 1.5, ["cut"] = 1.5 },
        Fits = n => n <= 18,
        Treat = "safe",
        Portrait = 0.8,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(JLayouts.FontsOf(st, "body", "display")),
            ["shape"] = rng.Pick(new[] { "pill", "rect" }),
            ["fill"] = rng.Pick(new[] { "outline", "filled" }),
            ["sugg"] = rng.Int(3, 4),
            ["pos"] = rng.Pick(new[] { "center", "upper" }),
        },
        Draw = env =>
        {
            var W = env.W;
            var H = env.H;
            var sc = env.Sc!;
            var p = env.P;
            var u = U(env);
            var port = IsPort(env);
            var font = S(p, "font")!;
            var text = env.Cut!.Text.Trim();
            var bw = W * (port ? 0.9 : 0.7);
            var ts = Math.Min(JLayouts.FitSize(text, font, bw - u * 0.22, u * 0.11,
                new JObject { ["track"] = 0.02 }), u * 0.095);
            var bh = ts * 2.1;
            var r = S(p, "shape") == "pill" ? bh / 2 : bh * 0.16;
            var by = H * (S(p, "pos") == "upper" ? 0.28 : 0.38);
            var outA = Tout(env);
            var e = Tin(env, 0, 0.45, J.E.OutExpo) * outA;
            var filled = S(p, "fill") == "filled";
            var plate = Col(sc, "ink");
            var tc = filled ? OnCol(sc, plate) : Col(sc, "fg");
            var w = bw * (0.3 + 0.7 * e);
            var bx0 = W / 2 - w / 2;
            if (e > 0)
            {
                if (filled) env.RRect(bx0, by - bh / 2, w, bh, r, plate, e, false);
                else env.RRect(bx0, by - bh / 2, w, bh, r, null, e, false, Col(sc, "fg"), Math.Max(2, ts * 0.05));
                Magnifier(env, bx0 + bh * 0.5, by, ts * 0.55, tc, Math.Max(2, ts * 0.06), e);
                var xr = bx0 + w - bh * 0.45;
                var q = ts * 0.2;
                env.Line(new[] { new[] { xr - q, by - q }, new[] { xr + q, by + q } }, tc, Math.Max(1.5, ts * 0.04), 0.6 * e, false);
                env.Line(new[] { new[] { xr - q, by + q }, new[] { xr + q, by - q } }, tc, Math.Max(1.5, ts * 0.04), 0.6 * e, false);
            }
            var tx = W / 2 - bw / 2 + bh * 0.95;
            var it = new JObject
            {
                ["text"] = text,
                ["font"] = font,
                ["size"] = ts,
                ["x"] = tx,
                ["y"] = by,
                ["align"] = "left",
                ["track"] = 0.02,
                ["color"] = tc,
            };
            if (env.Cut!.Enter == "cut") it["enter"] = "type";
            var bb = JLayouts.MainDraw(env, it);
            // 打完字后闪烁的光标
            var typed = env.Cut!.Enter == "cut" || env.Cut!.Enter == "type" ? env.Cut!.InDur + 0.05 : env.Cut!.InDur;
            if (env.Lt > typed && outA > 0.5 && env.Step % 2 == 0)
            {
                var tw = Meas(text, font, ts, new JObject { ["track"] = 0.02 }).W;
                env.Rect(tx + tw + ts * 0.12, by - ts * 0.55, Math.Max(2, ts * 0.06), ts * 1.1, Col(sc, "accent"), 1, false);
            }
            // 联想搜索建议
            var sugg = I(p, "sugg");
            var rom = HasLatin(text) ? null : J.Romaji(Strip(text));
            var rows = new[]
            {
                text + " lyrics",
                !string.IsNullOrEmpty(env.Cut!.LineText) && Strip(env.Cut!.LineText) != Strip(text) ? env.Cut!.LineText : text + " meaning",
                rom != null ? rom.ToLowerInvariant() : text + " mv",
                text + " cover"
            }.Take(sugg).ToList();
            var rs = ts * 0.52;
            var rh = rs * 2.4;
            var py = by + bh / 2 + rs * 0.8;
            var t0 = Math.Max(0.3, typed);
            var pa = Tin(env, t0, 0.3, J.E.OutCubic) * outA;
            if (pa > 0)
            {
                var ph = rows.Count * rh + rs * 0.6;
                env.RRect(W / 2 - bw / 2, py, bw, ph * pa, bh * 0.16, filled ? plate : null, 0.9 * pa, false,
                    filled ? null : Col(sc, "sub"), 1.2);
                for (var i = 0; i < rows.Count; i++)
                {
                    var rt = rows[i];
                    var a = J.Clamp((env.Lt - t0 - 0.05 - i * 0.07) / 0.2) * outA;
                    if (a <= 0 || (i + 1) * rh > ph * pa) continue;
                    var y = py + rs * 0.3 + (i + 0.5) * rh;
                    Magnifier(env, W / 2 - bw / 2 + bh * 0.5, y, rs * 0.7, filled ? OnCol(sc, plate) : Col(sc, "sub"), 1.5, a * 0.7);
                    var xt = W / 2 - bw / 2 + bh * 0.95;
                    var pre = rt.StartsWith(text) ? text : "";
                    var rest = rt[pre.Length..];
                    var pw = pre.Length > 0 ? Meas(pre, font, rs, new JObject { ["track"] = 0.02 }).W : 0;
                    if (pre.Length > 0)
                        env.DrawItem(new JObject
                        {
                            ["text"] = pre,
                            ["font"] = font,
                            ["size"] = rs,
                            ["track"] = 0.02,
                            ["align"] = "left",
                            ["x"] = xt,
                            ["y"] = y,
                            ["color"] = filled ? OnCol(sc, plate) : Col(sc, "fg"),
                            ["alpha"] = a,
                            ["ghost"] = false,
                        });
                    env.DrawItem(new JObject
                    {
                        ["text"] = rest,
                        ["font"] = BodyF(env),
                        ["size"] = rs,
                        ["track"] = 0.02,
                        ["align"] = "left",
                        ["x"] = xt + pw,
                        ["y"] = y,
                        ["color"] = filled ? OnCol(sc, plate) : Col(sc, "sub"),
                        ["alpha"] = a * (filled ? 0.65 : 1),
                        ["ghost"] = false,
                    });
                }
            }
            env.Body = bb ?? Box(W / 2 - bw / 2, by - bh / 2, W / 2 + bw / 2, by + bh / 2);
        },
    });

    // ---------------- chat（JS L2810-2912） ----------------

    /// <summary>chat 的一条消息。</summary>
    private sealed class ChatMsg
    {
        public string T = "";
        public bool Typing;
        public int Side;
    }

    private static void RegisterChat() => Reg0("chat", new LayoutDef
    {
        Name = "チャット",
        Tags = new[] { "pop", "emotional" },
        W = 0.6,
        EnterBias = new JObject { ["pop"] = 2, ["cut"] = 1.5, ["type"] = 1.3 },
        Fits = n => n <= 22,
        Treat = "safe",
        Plan = (rng, cut, st) =>
        {
            var nw = Planner.ChunkText(cut.Text).Count;
            return new JObject
            {
                ["msgs"] = cut.N <= 4
                    ? JA(new[] { cut.Text.Trim() })
                    : JA(SplitK(cut.Text, J.Clamp(nw, 2, cut.N > 12 ? 4 : 3))),
                ["font"] = rng.Pick(JLayouts.FontsOf(st, "body", "display")),
                ["side0"] = rng.Pick(new[] { 1.0, -1.0 }),
                ["alt"] = rng.Chance(0.55),
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
            var ctx = env.Ctx;
            var font = S(p, "font")!;
            var msgs = StrArr(p["msgs"]);
            if (msgs.Count == 0) msgs = new List<string> { env.Cut!.Text.Trim() };
            var colW = W * (port ? 0.86 : W / (double)H < 1.5 ? 0.66 : 0.52);
            var maxW = colW * 0.8;
            var sentC = Col(sc, "accent");
            var recvC = Col(sc, "ink") != Col(sc, "accent") ? Col(sc, "ink") : Col(sc, "sub");
            var side0 = (int)D(p, "side0");
            var alt = JLayouts.Truthy(p["alt"]);
            var list = new List<ChatMsg>();
            if (msgs.Count == 1) list.Add(new ChatMsg { Typing = true, Side = -side0 });
            for (var i = 0; i < msgs.Count; i++)
                list.Add(new ChatMsg { T = msgs[i], Side = alt ? (i % 2 != 0 ? -side0 : side0) : side0 });
            var nL = list.Count;
            // 字号压到整组气泡能放下为止
            var fs = Math.Min(u * 0.088, H * 0.62 / (nL * 2.2));
            var longest = Math.Max(1, list.Max(q => q.Typing ? 1 : JLayouts.GlyphCount(q.T)));
            // 优先让消息单行放下
            fs = Math.Max(Math.Min(fs, (maxW - fs * 1.4) / (longest * 1.02)), fs * 0.62);
            var lines = list.Select(q => q.Typing ? "" : Brk(q.T, Math.Max(3, (int)Math.Floor(maxW / (fs * 1.02))))).ToList();
            for (var i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                if (l.Length > 0)
                    fs = Math.Min(fs, JLayouts.FitSize(l, font, maxW - fs * 1.4, H * 0.3, new JObject { ["lead"] = 1.25 }));
            }
            var pad = fs * 0.62;
            var sp = fs * 0.45;
            var dims = new List<double[]>();
            for (var i = 0; i < list.Count; i++)
            {
                var q = list[i];
                if (q.Typing)
                {
                    dims.Add(new[] { fs * 3, fs * 1.9 });
                    continue;
                }
                var m = Meas(lines[i], font, fs, new JObject { ["lead"] = 1.25 });
                dims.Add(new[] { m.W + pad * 2, m.H + pad * 1.45 });
            }
            var gapT = Math.Min(0.3, Math.Max(0.12, env.Cut!.Dur * 0.45 / nL));
            double TAt(int i) => list[0].Typing ? (i == 0 ? 0 : 0.35 + (i - 1) * gapT) : i * gapT;
            var stag = Math.Max(0.01, env.Cut!.Stagger);
            var bottom = H * (port ? 0.78 : 0.8);
            var outA = Tout(env);
            BBox? bb = null;
            // 每个气泡的 y：会被之后出现的气泡往上顶
            var lift = new List<double>();
            for (var i = 0; i < list.Count; i++)
            {
                var y = 0.0;
                for (var j = i + 1; j < nL; j++)
                    y += (dims[j][1] + sp) * J.E.OutExpo(J.Clamp((env.Lt - TAt(j)) / 0.28));
                lift.Add(y);
            }
            for (var i = 0; i < list.Count; i++)
            {
                var q = list[i];
                var lt = env.Lt - TAt(i);
                if (lt < 0) continue;
                var pop = J.E.OutBack(J.Clamp(lt / 0.25), 1.6) * outA;
                var bw = dims[i][0];
                var bh = dims[i][1];
                var right = q.Side > 0;
                var x0 = right ? W / 2 + colW / 2 - bw : W / 2 - colW / 2;
                var y1 = bottom - lift[i];
                var y0 = y1 - bh;
                var col = right ? sentC : recvC;
                var tc = OnCol(sc, col);
                var px = right ? x0 + bw : x0;
                var py = y1;
                ctx.Save();
                ctx.Translate((float)px, (float)py);
                var ps = (float)Math.Max(0.001, pop);
                ctx.Scale(ps, ps);
                ctx.Translate((float)-px, (float)-py);
                env.RRect(x0, y0, bw, bh, Math.Min(bh / 2, fs * 0.9), col, 1, false);
                env.Poly(right
                    ? new[]
                    {
                        new[] { x0 + bw - fs * 0.5, y1 - fs * 0.3 },
                        new[] { x0 + bw + fs * 0.28, y1 + fs * 0.05 },
                        new[] { x0 + bw - fs * 0.1, y1 - fs * 0.8 }
                    }
                    : new[]
                    {
                        new[] { x0 + fs * 0.5, y1 - fs * 0.3 },
                        new[] { x0 - fs * 0.28, y1 + fs * 0.05 },
                        new[] { x0 + fs * 0.1, y1 - fs * 0.8 }
                    }, col, 1, false);
                if (q.Typing)
                {
                    for (var k = 0; k < 3; k++)
                    {
                        var ph = Math.Sin(env.Ltb * 9 - k * 0.9) * 0.5 + 0.5;
                        env.Circle(x0 + bw / 2 + (k - 1) * fs * 0.62, y0 + bh / 2 - ph * fs * 0.12, fs * 0.16,
                            tc, null, 0, 0.45 + ph * 0.5, false);
                    }
                }
                else
                    bb = JLayouts.UnionBb(bb, JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = lines[i],
                        ["font"] = font,
                        ["size"] = fs,
                        ["lead"] = 1.25,
                        ["x"] = x0 + bw / 2,
                        ["y"] = y0 + bh / 2,
                        ["color"] = tc,
                        ["mi"] = TAt(i) / stag,
                    }));
                ctx.Restore();
                if (i == nL - 1 && pop > 0.9)
                {
                    var ms = Math.Max(11, fs * 0.36);
                    env.DrawItem(new JObject
                    {
                        ["text"] = (right ? "Read " : "") + J.FmtTime(env.Cut!.Start)[..5],
                        ["font"] = MonoF(env),
                        ["size"] = ms,
                        ["align"] = right ? "right" : "left",
                        ["x"] = right ? x0 + bw : x0,
                        ["y"] = y1 + ms * 1.4,
                        ["color"] = Col(sc, "sub"),
                        ["alpha"] = outA,
                        ["ghost"] = false,
                    });
                }
            }
            env.Body = bb ?? Box(W / 2 - colW / 2, bottom - H * 0.3, W / 2 + colW / 2, bottom);
        },
    });

    // ---------------- notification（JS L2914-3041） ----------------

    private static void RegisterNotification() => Reg0("notification", new LayoutDef
    {
        Name = "通知",
        Tags = new[] { "pop", "emotional", "calm" },
        W = 0.5,
        EnterBias = new JObject { ["cut"] = 1.6, ["type"] = 1.3, ["blur"] = 1.2 },
        Fits = n => n <= 24,
        Treat = "safe",
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(JLayouts.FontsOf(st, "body", "display")),
            ["pos"] = rng.Pick(new[] { "banner", "lock", "center" }),
            ["stack"] = rng.Chance(0.5),
            ["app"] = rng.Pick(new[] { "MUSIC", "LYRICS", "MESSAGE" }),
        },
        Draw = env =>
        {
            var W = env.W;
            var H = env.H;
            var sc = env.Sc!;
            var p = env.P;
            var u = U(env);
            var port = IsPort(env);
            var font = S(p, "font")!;
            var text = env.Cut!.Text.Trim();
            var cw = W * (port ? 0.9 : W / (double)H < 1.5 ? 0.66 : 0.54);
            var pd = cw * 0.05;
            var hs = Math.Max(12, cw * 0.032);
            var plate = Col(sc, "ink");
            var tc = OnCol(sc, plate);
            var mt = text;
            var ms = Math.Min(JLayouts.FitSize(mt, font, cw - pd * 2, u * 0.11, new JObject { ["track"] = 0.02 }), u * 0.095);
            if (ms < u * 0.07 && JLayouts.GlyphCount(text) > 5)
            {
                mt = Brk(text, (int)Math.Ceiling(JLayouts.GlyphCount(text) / 2.0));
                ms = Math.Min(JLayouts.FitSize(mt, font, cw - pd * 2, u * 0.2,
                    new JObject { ["track"] = 0.02, ["lead"] = 1.3 }), u * 0.085);
            }
            var mm = Meas(mt, font, ms, new JObject { ["track"] = 0.02, ["lead"] = 1.3 });
            var headH = hs * 2.2;
            var titleH = hs * 1.9;
            var ch = pd + headH + titleH + mm.H + pd * 1.1;
            var outA = Tout(env);
            // 锁屏模式先画大时钟
            var clockY = H * (port ? 0.2 : 0.22);
            var pos = S(p, "pos");
            if (pos == "lock")
            {
                var a = Tin(env, 0, 0.5, J.E.OutCubic) * outA;
                var hhv = (int)(Math.Floor(env.Cut!.Start / 60) % 24);
                if (hhv == 0) hhv = 12;
                var hh = Pad2(hhv);
                var mi2 = Pad2((int)Math.Floor(env.Cut!.Start) % 60);
                var cs = Math.Min(u * 0.2, H * 0.2);
                env.DrawItem(new JObject
                {
                    ["text"] = hh + ":" + mi2,
                    ["font"] = JLayouts.FontsOf(env.St, "display")[0],
                    ["size"] = cs,
                    ["x"] = W / 2,
                    ["y"] = clockY,
                    ["track"] = 0.02,
                    ["color"] = Col(sc, "sub"),
                    ["alpha"] = a * 0.8,
                    ["ghost"] = false,
                });
                env.DrawItem(new JObject
                {
                    ["text"] = "LINE " + LineNo(env) + "  ·  " + J.FmtTime(env.Cut!.Start),
                    ["font"] = MonoF(env),
                    ["size"] = hs,
                    ["track"] = 0.2,
                    ["x"] = W / 2,
                    ["y"] = clockY - cs * 0.62,
                    ["color"] = Col(sc, "sub"),
                    ["alpha"] = a * 0.8,
                    ["ghost"] = false,
                });
            }
            var target = pos == "banner" ? H * 0.06
                : pos == "lock" ? clockY + Math.Min(u * 0.2, H * 0.2) * 0.75
                : H / 2 - ch / 2;
            var eIn = J.E.OutBack(J.Clamp(env.Lt / 0.45), 1.1);
            var eOut = J.E.InCubic(env.POut);
            var y0 = target - (1 - eIn) * (target + ch + 30) - eOut * (target + ch + 30);
            var x0 = W / 2 - cw / 2;
            if (JLayouts.Truthy(p["stack"]))
            {
                var sw = cw * 0.92;
                env.RRect(W / 2 - sw / 2, y0 + ch - pd * 0.4, sw, pd * 1.4, pd * 0.7, plate, 0.45, false);
            }
            env.RRect(x0, y0, cw, ch, pd * 0.9, plate, 0.96, false);
            // 通知头部：图标、应用名、时间
            var iy = y0 + pd + headH / 2 - hs * 0.2;
            var isz = hs * 1.7;
            env.RRect(x0 + pd, iy - isz / 2, isz, isz, isz * 0.24, Col(sc, "accent"), 1, false);
            NoteIcon(env, x0 + pd + isz / 2, iy, isz * 0.8, OnCol(sc, Col(sc, "accent")), 1);
            env.DrawItem(new JObject
            {
                ["text"] = S(p, "app"),
                ["font"] = MonoF(env),
                ["size"] = hs,
                ["track"] = 0.15,
                ["align"] = "left",
                ["x"] = x0 + pd + isz + hs * 0.7,
                ["y"] = iy,
                ["color"] = tc,
                ["alpha"] = 0.6,
                ["ghost"] = false,
            });
            env.DrawItem(new JObject
            {
                ["text"] = "now",
                ["font"] = MonoF(env),
                ["size"] = hs,
                ["track"] = 0.1,
                ["align"] = "right",
                ["x"] = x0 + cw - pd,
                ["y"] = iy,
                ["color"] = tc,
                ["alpha"] = 0.5,
                ["ghost"] = false,
            });
            var ty = y0 + pd + headH + titleH / 2;
            env.DrawItem(new JObject
            {
                ["text"] = RomajiOf(env) ?? "No." + LineNo(env),
                ["font"] = BodyF(env),
                ["size"] = hs * 1.15,
                ["track"] = 0.06,
                ["align"] = "left",
                ["x"] = x0 + pd,
                ["y"] = ty,
                ["color"] = tc,
                ["alpha"] = 0.9,
                ["ghost"] = false,
            });
            var my = ty + titleH / 2 + mm.H / 2;
            var bb = JLayouts.MainDraw(env, new JObject
            {
                ["text"] = mt,
                ["font"] = font,
                ["size"] = ms,
                ["x"] = x0 + pd,
                ["y"] = my,
                ["align"] = "left",
                ["track"] = 0.02,
                ["lead"] = 1.3,
                ["color"] = tc,
            });
            env.Body = bb ?? Box(x0 + pd, my - mm.H / 2, x0 + pd + mm.W, my + mm.H / 2);
        },
    });

    // ---------------- ticket（JS L3043-3188） ----------------

    private static void RegisterTicket() => Reg0("ticket", new LayoutDef
    {
        Name = "チケット",
        Tags = new[] { "pop", "graphic", "editorial" },
        W = 0.6,
        Fits = n => n <= 18,
        Treat = "safe",
        Portrait = 0.6,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(JLayouts.FontsOf(st, "display", "serif")),
            ["fill"] = rng.Pick(new[] { "accent", "ink", "outline" }),
            ["tilt"] = rng.Range(-5, 5),
            ["label"] = rng.Pick(new[] { "ADMIT ONE", "LIVE", "TICKET" }),
            ["serial"] = rng.Int(1, 999999),
        },
        Draw = env =>
        {
            var W = env.W;
            var H = env.H;
            var sc = env.Sc!;
            var p = env.P;
            var u = U(env);
            var port = IsPort(env);
            var ctx = env.Ctx;
            var font = S(p, "font")!;
            var label = S(p, "label")!;
            var serial = I(p, "serial");
            var fillKind = S(p, "fill")!;
            var tw = Math.Min(W * (port ? 0.9 : 0.74), H * 1.9);
            var th = Math.Min(tw * (port ? 0.46 : 0.38), H * 0.5);
            var stubW = tw * 0.22;
            var px0 = -tw / 2 + tw - stubW;
            var nr = th * 0.075;
            var r = th * 0.05;
            var outline = fillKind == "outline";
            var plate = fillKind == "accent" ? Col(sc, "accent") : Col(sc, "ink");
            var tc = outline ? Col(sc, "fg") : OnCol(sc, plate);
            var lc = outline ? Col(sc, "sub") : tc;
            var eIn = J.E.OutBack(J.Clamp(env.Lt / 0.5), 1.2);
            var eOut = J.E.InCubic(env.POut);
            var cx = W / 2.0;
            var cy = H / 2.0 + (1 - eIn) * H * 0.7;
            var rot = D(p, "tilt") * (1 - eOut * 0.5) + (1 - eIn) * 10;
            var T = TicketParts(-tw / 2, -th / 2, tw, th, px0, nr, r);
            var a = J.Clamp(eIn * 3) * (1 - eOut);
            ctx.Save();
            ctx.Translate((float)cx, (float)cy);
            ctx.RotateDegrees((float)rot);
            // 副券退场时撕走
            var tear = eOut;
            ctx.Save();
            ctx.Translate((float)(tear * tw * 0.12), (float)(tear * th * 0.2));
            ctx.RotateDegrees((float)(tear * 12));
            if (outline) env.Line(CloseLoop(T.Stub).ToArray(), Col(sc, "fg"), Math.Max(2, th * 0.012), a, false);
            else env.Poly(T.Stub.ToArray(), plate, a, false);
            var ls = Math.Max(11, th * 0.07);
            var sx = px0 + stubW / 2;
            env.DrawItem(new JObject
            {
                ["text"] = label,
                ["font"] = MonoF(env),
                ["size"] = ls,
                ["track"] = 0.25,
                ["x"] = sx - stubW * 0.22,
                ["y"] = 0,
                ["rot"] = -90,
                ["color"] = tc,
                ["alpha"] = a,
                ["ghost"] = false,
            });
            env.DrawItem(new JObject
            {
                ["text"] = "No." + serial.ToString("D6", CultureInfo.InvariantCulture),
                ["font"] = MonoF(env),
                ["size"] = ls * 0.8,
                ["track"] = 0.12,
                ["x"] = sx + stubW * 0.02,
                ["y"] = 0,
                ["rot"] = -90,
                ["color"] = lc,
                ["alpha"] = a * 0.8,
                ["ghost"] = false,
            });
            var bx = sx + stubW * 0.26;
            var bh2 = th * 0.7;
            var y = -bh2 / 2;
            for (var i = 0; i < 40 && y < bh2 / 2; i++)
            {
                var hgt = th * (0.006 + 0.014 * J.R(serial, i, 3));
                env.Rect(bx - stubW * 0.08, y, stubW * 0.16, hgt, tc, a * 0.85, false);
                y += hgt + th * (0.006 + 0.01 * J.R(serial, i, 4));
            }
            ctx.Restore();
            if (outline) env.Line(CloseLoop(T.Main).ToArray(), Col(sc, "fg"), Math.Max(2, th * 0.012), a, false);
            else env.Poly(T.Main.ToArray(), plate, a, false);
            // 撕票孔虚线
            for (var yy = -th / 2 + nr * 1.6; yy < th / 2 - nr * 1.6; yy += th * 0.05)
                env.Rect(px0 - 1, yy, 2.5, th * 0.025, lc, a * 0.7, false);
            // 票面上下的小字信息
            var mx0 = -tw / 2 + th * 0.12;
            var mx1 = px0 - th * 0.12;
            env.DrawItem(new JObject
            {
                ["text"] = label + "  ·  No." + LineNo(env),
                ["font"] = MonoF(env),
                ["size"] = ls,
                ["track"] = 0.2,
                ["align"] = "left",
                ["x"] = mx0,
                ["y"] = -th / 2 + th * 0.14,
                ["color"] = lc,
                ["alpha"] = a,
                ["ghost"] = false,
            });
            env.Line(new[] { new[] { mx0, -th / 2 + th * 0.24 }, new[] { mx1, -th / 2 + th * 0.24 } }, lc, 1.2, a * 0.6, false);
            env.Line(new[] { new[] { mx0, th / 2 - th * 0.24 }, new[] { mx1, th / 2 - th * 0.24 } }, lc, 1.2, a * 0.6, false);
            env.DrawItem(new JObject
            {
                ["text"] = "GATE " + (char)(65 + serial % 6) + "   ROW " + Pad2(1 + serial % 30) + "   SEAT " + Pad2(1 + (serial >> 3) % 40),
                ["font"] = MonoF(env),
                ["size"] = ls * 0.9,
                ["track"] = 0.15,
                ["align"] = "left",
                ["x"] = mx0,
                ["y"] = th / 2 - th * 0.14,
                ["color"] = lc,
                ["alpha"] = a,
                ["ghost"] = false,
            });
            env.DrawItem(new JObject
            {
                ["text"] = J.FmtTime(env.Cut!.Start),
                ["font"] = MonoF(env),
                ["size"] = ls * 0.9,
                ["track"] = 0.1,
                ["align"] = "right",
                ["x"] = mx1,
                ["y"] = th / 2 - th * 0.14,
                ["color"] = lc,
                ["alpha"] = a,
                ["ghost"] = false,
            });
            // 票面主标题就是歌词
            var t0 = env.Cut!.Text.Trim();
            var text = Brk(t0, port ? 6 : 9);
            var aw = mx1 - mx0;
            var ah = th * 0.44;
            var size = Math.Min(JLayouts.FitSize(text, font, aw, ah, new JObject { ["track"] = 0.03, ["lead"] = 1.1 }), th * 0.3);
            var bb = JLayouts.MainDraw(env, new JObject
            {
                ["text"] = text,
                ["font"] = font,
                ["size"] = size,
                ["x"] = mx0,
                ["y"] = 0,
                ["align"] = "left",
                ["track"] = 0.03,
                ["lead"] = 1.1,
                ["color"] = tc,
            });
            ctx.Restore();
            env.Body = bb != null ? Box(cx - tw / 2, cy - th / 2, cx + tw / 2, cy + th / 2) : null;
        },
    });
}
