// 移植自 frontend/src/engine/11p_kinetic1.js（kinetic 第 1 包：按词计时的版式，pack = 'kinetic1'，12 条）。
//
// 契约与 P11LayoutsB 相同：Fits 筛台词；Plan(rng, ctx, st) 抽参数存 cut.params（键名一字不改）；
// Draw(env) 绘制并写回 env.Body（JS render(env) 的返回值，null → env.Body = null）。
// RNG 消费顺序逐行对位（rng.pick/chance/range/int 的调用次序就是画面轨迹；JS 里靠 ?:
// 短路的抽取在 C# 里同样短路）。JS reg 助手的 Object.assign(def, {set:'kinetic'}) 对位为
// Omakase.MarkSet(group, key, "kinetic")（IRegEntry 无 set 字段，Omakase 的注记注入口）。
//
// 本包私有 helper（JS L9-199 顶层 const/function 逐条对位）见「本包 helper」区：
// U/isPort/strip/hasLatin/flat/gcount/fontsOf/bodyF/monoF/tout/meas/em/box/UB/miAt/rotV/
// sprg/bellK/onCol/accOn/splitUnit/unitsOf/onsets/curIdx/partitions/flowUnits + loopPath/pathAt。
// 词时钟/词块缓存（JS WeakMap/Map）对位 ConditionalWeakTable<Cut,…> 与定容 Dictionary。
//
// 跨文件符号对位：J.fontsOf→JLayouts.FontsOf、J.measure→JLayouts.Measure、J.fitSize→JLayouts.FitSize、
// J.layoutText→JText.LayoutText、J.mainDraw→JLayouts.MainDraw、J.unionBB→JLayouts.UnionBb、
// J.centerBB→JLayouts.CenterBb、J.splitLines→JLayouts.SplitLines、J.chunkText→Planner.ChunkText、
// J.glyphCount(码点数)→JLayouts.CpLen、J.metrics.adv→JLayouts.MetricsAdv、
// J.TYPESET→JText.Typeset、J.drawItem(env.draw)→env.DrawItem、J.isKanji→J.IsKanji、
// J.r/rs/h→J.R/J.Rs/J.H、clamp/lerp/smooth/lum/contrast/mix/DEG/TAU→J.*、E.*→J.E.*。
//
// env 缺口（本文件内补）：JS env.beat {since,len,index} → 本文件 BeatInfo/BeatOf（与 P11LayoutsB
// 同口径，从 env.Plan.Beats + env.T 重建）。ctx.setLineDash → 本文件 DashedLine（SKPathEffect）。
//
// 条目字段对位：ae（AE 导出模板名）LayoutDef 无对应属性，未注册（各条目行内注明）。

using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace JizuraLive.Engine;

public static class P11Kinetic1
{
    private const string P = "kinetic1";
    private static int _installed;

    /// <summary>JS reg(key, def) = J.register('layout', key, {…def, set:'kinetic'}, P)。</summary>
    private static void Reg0(string key, LayoutDef def)
    {
        Reg.Register("layout", key, def, P);
        Omakase.MarkSet("layout", key, "kinetic"); // JS def 上的 set:'kinetic'
    }

    // ================= 本包 helper（JS L9-199 逐条对位） =================

    private static double U(Env env) => Math.Min(env.W, env.H);
    private static bool IsPort(Env env) => env.H > env.W * 1.08;

    private static string Strip(string? t) => Regex.Replace(t ?? "", @"\s+", "");
    private static bool HasLatin(string? t) => Regex.IsMatch(t ?? "", "[A-Za-z]");

    private static string Flat(string? t) =>
        HasLatin(t) ? Regex.Replace((t ?? "").Trim(), @"\s+", " ") : Strip(t);

    private static int Gcount(string? t) => JLayouts.CpLen(Strip(t));

    private static string[] FontsOf(StyleDef st, params string[] roles) => JLayouts.FontsOf(st, roles);

    private static string BodyF(Env env) => JLayouts.Font0(env.St, "body") ?? "gothic_med";
    private static string MonoF(Env env) => JLayouts.Font0(env.St, "mono") ?? "mono";

    private static double Tout(Env env) => 1 - J.E.InCubic(env.POut);

    /// <summary>meas(text, font, size, o)：JS J.measure(Object.assign({text,font,size}, o))。</summary>
    private static TextMetrics Meas(string text, string font, double size, JObject? o = null)
    {
        var probe = new JObject { ["text"] = text, ["font"] = font, ["size"] = size };
        if (o != null)
            foreach (var p in o.Properties())
                probe[p.Name] = p.Value;
        return JLayouts.Measure(probe);
    }

    /// <summary>em(text, font, o)：size 100 的宽度换算成 em。</summary>
    private static double Em(string text, string font, JObject? o = null) => Meas(text, font, 100, o).W / 100;

    private static BBox Box(double x0, double y0, double x1, double y1) => new()
    { X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, Cx = (x0 + x1) / 2, Cy = (y0 + y1) / 2 };

    private static BBox? UB(BBox? a, BBox? b) => JLayouts.UnionBb(a, b);

    /// <summary>换算成 motion index，让 mainDraw 在本地时间 t 开始这个 item 的入场。</summary>
    private static double MiAt(Env env, double t) =>
        Math.Max(0, t) / Math.Max(0.005, env.Cut!.Stagger != 0 ? env.Cut.Stagger : 0.04);

    private static double[] RotV(double x, double y, double a)
    {
        var c = Math.Cos(a * J.DEG);
        var s = Math.Sin(a * J.DEG);
        return new[] { x * c - y * s, x * s + y * c };
    }

    /// <summary>阻尼弹簧 1 → 0，会冲过 0 再荡回来；t 秒。</summary>
    private static double Sprg(double t, double k = 7, double f = 18) =>
        t <= 0 ? 1 : Math.Exp(-k * t) * Math.Cos(f * t);

    private static double BellK(double k) => Math.Sin(Math.PI * J.Clamp(k));

    /// <summary>在底板色上读得出的文字色。</summary>
    private static string OnCol(Scheme sc, string? fill)
    {
        string? best = null;
        var bv = 0.0;
        foreach (var c in new[] { sc.Str("bg"), sc.Str("fg"), sc.Str("ink"), sc.Str("accent"), sc.Str("sub") })
        {
            if (c == null || c == fill) continue;
            var k = J.Contrast(c, fill ?? "#000");
            if (k > bv)
            {
                bv = k;
                best = c;
            }
        }
        return bv >= 2.6 && best != null
            ? best
            : J.Lum(fill ?? "#000") > 0.5 ? "#111111" : "#FFFFFF";
    }

    /// <summary>背景上读得出的 accent，不然退回 fg。</summary>
    private static string AccOn(Scheme sc) =>
        J.Contrast(sc.Str("accent") ?? "#000", sc.Str("bg") ?? "#000") >= 1.7
            ? sc.Str("accent")!
            : sc.Str("fg") ?? "#FFFFFF";

    /// <summary>JS env.beat（09_render 的 beatAt）本文件内的重建：{since, len, index}。</summary>
    private readonly record struct BeatInfo(bool Has, double Since, double Len, int Index);

    private static BeatInfo BeatOf(Env env)
    {
        var beats = env.Plan.Beats;
        if (beats == null || beats.Length == 0) return default;
        var t = env.T;
        int lo = 0, hi = beats.Length - 1, i = -1;
        while (lo <= hi)
        {
            var m = (lo + hi) >> 1;
            if (beats[m] <= t)
            {
                i = m;
                lo = m + 1;
            }
            else hi = m - 1;
        }
        if (i < 0) return default;
        var len = i + 1 < beats.Length ? beats[i + 1] - beats[i] : i > 0 ? beats[i] - beats[i - 1] : 0.5;
        return new BeatInfo(true, t - beats[i], Math.Max(0.2, len), i);
    }

    // ---- 词单元：把整句切成数量好用的词块（JS L48-104） ----

    private static readonly Dictionary<string, string[]> UnitCache = new();

    private static string[] SplitUnit(string s)
    {
        var t = (s ?? "").Trim();
        if (Regex.IsMatch(t, @"\s"))
        {
            // 拉丁文：在靠中间的空格处切
            var mid = t.Length / 2.0;
            var bi = -1;
            var bd = 1e9;
            for (var i = 0; i < t.Length; i++)
                if (t[i] == ' ' && Math.Abs(i - mid) < bd)
                {
                    bd = Math.Abs(i - mid);
                    bi = i;
                }
            return new[] { t[..bi].Trim(), t[(bi + 1)..].Trim() };
        }
        var n = JLayouts.CpLen(t);
        var parts = JLayouts.SplitLines(t, (int)Math.Ceiling(n / 2.0)).Split('\n');
        return parts.Length >= 2
            ? new[] { parts[0], string.Join("", parts.Skip(1)) }
            : new[] { t };
    }

    private static string[] UnitsOf(Cut cut, int maxU = 6, int minU = 1)
    {
        var text = cut.Text ?? "";
        var key = text + "\u0002" + string.Join("\u0001", cut.Words) + "\u0002" + maxU + ":" + minU;
        if (UnitCache.TryGetValue(key, out var hit)) return hit;
        var lat = HasLatin(text);
        var w = (cut.Words.Length > 0 ? cut.Words : Planner.ChunkText(text).ToArray())
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (Strip(string.Join("", w)) != Strip(text))
            w = Planner.ChunkText(text).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (w.Count == 0) w.Add(text.Trim().Length > 0 ? text.Trim() : "…");
        while (w.Count > maxU) // 合并最短的一对相邻词
        {
            var bi = 0;
            var bv = 1e9;
            for (var i = 0; i < w.Count - 1; i++)
            {
                var v = Gcount(w[i]) + Gcount(w[i + 1]);
                if (v < bv)
                {
                    bv = v;
                    bi = i;
                }
            }
            w[bi] = w[bi] + (lat ? " " : "") + w[bi + 1]; // JS w.splice(bi, 2, merged)
            w.RemoveAt(bi + 1);
        }
        for (var g = 0; g < 8 && w.Count < minU; g++) // 拆开最长的可拆单元
        {
            var bi = -1;
            var bv = 1.0;
            for (var i = 0; i < w.Count; i++)
            {
                var n = Gcount(w[i]);
                var ok = Regex.IsMatch(w[i], @"\s") || (!HasLatin(w[i]) && n >= 2);
                if (ok && n > bv)
                {
                    bv = n;
                    bi = i;
                }
            }
            if (bi < 0) break;
            var parts = SplitUnit(w[bi]);
            if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0) break;
            w.RemoveAt(bi); // JS w.splice(bi, 1, parts[0], parts[1])
            w.Insert(bi, parts[0]);
            w.Insert(bi + 1, parts[1]);
        }
        if (UnitCache.Count > 400) UnitCache.Clear();
        var arr = w.ToArray();
        UnitCache[key] = arr;
        return arr;
    }

    // ---- 词时钟：每个词的入场时刻；靠近节拍就吸附到节拍上（JS L106-148） ----

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Cut, Dictionary<string, double[]>> ClockCache = new();

    private static double[] Onsets(Env env, int n, double frac = 0.5, double gap = 0.38, double t0 = 0)
    {
        var c = env.Cut!;
        var m = ClockCache.GetOrCreateValue(c);
        var key = n + ":" + frac + ":" + gap + ":" + t0;
        if (m.TryGetValue(key, out var hit)) return hit;
        var dur = c.Dur;
        var last = Math.Max(0, Math.Min(dur * frac, (n - 1) * gap));
        var v = new List<double>();
        for (var i = 0; i < n; i++) v.Add(t0 + (n > 1 ? last * i / (n - 1) : 0));
        var beats = env.Plan.Beats;
        if (beats is { Length: > 0 } && n > 1)
        {
            var pick = new List<double>();
            var prev = t0;
            foreach (var b in beats)
            {
                var r = b - c.Start;
                if (r <= t0 + 0.12) continue;
                if (r > dur * 0.72) break;
                if (r - prev >= 0.2)
                {
                    pick.Add(r);
                    prev = r;
                }
                if (pick.Count >= n - 1) break;
            }
            if (pick.Count >= n - 1 && pick[n - 2] <= Math.Max(last * 1.35, dur * 0.55))
                v = new List<double> { t0 }.Concat(pick).ToList();
        }
        var arr = v.ToArray();
        m[key] = arr;
        return arr;
    }

    /// <summary>当前时刻已过的最新词序号，第一个词之前是 -1。</summary>
    private static int CurIdx(double[] ts, double t)
    {
        var k = -1;
        for (var i = 0; i < ts.Length; i++)
            if (t >= ts[i])
                k = i;
        return k;
    }

    // ---- 把词流进等重的行并塞进框里（JS L150-199） ----

    private static List<int[][]> Partitions(int n, int L)
    {
        var @out = new List<int[][]>();
        void Rec(int start, int left, List<int[]> acc)
        {
            if (left == 1)
            {
                acc.Add(new[] { start, n });
                @out.Add(acc.ToArray());
                acc.RemoveAt(acc.Count - 1);
                return;
            }
            for (var e = start + 1; e <= n - left + 1; e++)
            {
                acc.Add(new[] { start, e });
                Rec(e, left - 1, acc);
                acc.RemoveAt(acc.Count - 1);
            }
        }
        if (L >= 1 && L <= n) Rec(0, L, new List<int[]>());
        return @out;
    }

    public sealed class FlowUnitPos
    {
        public double X, Y, W;
        public int Li;
    }

    public sealed class FlowLayout
    {
        public double Size, W, H;
        public int L;
        public FlowUnitPos[] Pos = System.Array.Empty<FlowUnitPos>();
        public double[] Ws = System.Array.Empty<double>();
    }

    private static FlowLayout FlowUnits(string[] units, string font, double maxW, double maxH,
        double? sp = null, double lead = 1.22, double track = 0, int maxLines = 4, double maxSize = 1e9,
        double lineBonus = 1.1)
    {
        var lat = units.Any(HasLatin);
        var spV = sp ?? (lat ? 0.32 : 0.08);
        var ws = units.Select(t => Em(t, font, new JObject { ["track"] = track })).ToArray();
        var n = units.Length;
        (double Size, int L, int[][] Pt, double[] Lw)? best = null;
        for (var L = 1; L <= Math.Min(n, maxLines); L++)
        {
            int[][]? bp = null;
            double[]? blw = null;
            double bw = 1e9;
            foreach (var pt in Partitions(n, L).Take(80))
            {
                var lw = pt.Select(p =>
                {
                    double w = 0;
                    for (var i = p[0]; i < p[1]; i++) w += ws[i] + (i > p[0] ? spV : 0);
                    return w;
                }).ToArray();
                var mw = lw.Length > 0 ? lw.Max() : 0;
                if (mw < bw)
                {
                    bw = mw;
                    bp = pt;
                    blw = lw;
                }
            }
            if (bp == null || blw == null) continue;
            var size = Math.Min(Math.Min(maxW / bw, maxH / (L * lead - (lead - 1))), maxSize);
            if (best == null || size > best.Value.Size * lineBonus)
                best = (size, L, bp, blw);
        }
        var b = best!.Value;
        var sizeF = b.Size;
        var pos = new FlowUnitPos[n];
        for (var li = 0; li < b.L; li++)
        {
            var seg = b.Pt[li];
            var y = (li - (b.L - 1) / 2.0) * lead * sizeF;
            var x = -b.Lw[li] * sizeF / 2;
            for (var i = seg[0]; i < seg[1]; i++)
            {
                var w = ws[i] * sizeF;
                pos[i] = new FlowUnitPos { X = x + w / 2, Y = y, W = w, Li = li };
                x += w + spV * sizeF;
            }
        }
        return new FlowLayout
        {
            Size = sizeF,
            Pos = pos,
            L = b.L,
            W = b.Lw.Max() * sizeF,
            H = (b.L * lead - (lead - 1)) * sizeF,
            Ws = ws,
        };
    }

    // ================= 注册（JS 源里 reg(...) 的出现序） =================

    /// <summary>幂等注册本文件全部 layout（顺序 = JS 源文件 reg 调用序；不挂静态构造函数）。</summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;
        KnSlamStack();    // JS L201
        KnQuarterTurn();  // JS L302
        KnSwapCenter();   // JS L424
        KnZoomDive();     // JS L518
        KnFlowSnap();     // JS L608
        KnSeesaw();       // JS L735
        KnTypeSlam();     // JS L835
        KnRhythmCuts();   // JS L948
        KnPathRide();     // JS L1089
        KnGearWords();    // JS L1155
        KnCollide();      // JS L1235
        KnTumble();       // JS L1337
        KnReflow();       // JS L1413
        KnPadGrid();      // JS L1489
    }

    // ---------------- knSlamStack（JS L201） ----------------

    private static void KnSlamStack() => Reg0("knSlamStack", new LayoutDef
    {
        Name = "積み上げ",
        Tags = new[] { "pop", "graphic" },
        W = 1.1,
        Fits = n => n >= 2 && n <= 18,
        EnterBias = new JObject { ["cut"] = 3, ["blur"] = 0.7, ["pop"] = 0.8, ["slice"] = 0.3, ["wipe"] = 0.4 },
        Plan = (rng, _, st) => new JObject
        {
            // ae: 'justified'（AE 导出模板名，LayoutDef 无对应属性）
            ["font"] = rng.Pick(FontsOf(st, "display")),
            ["align"] = rng.Pick(new[] { "center", "center", "left", "right" }),
            ["from"] = rng.Pick(new[] { "scale", "scale", "drop", "side" }),
            ["acc"] = rng.Int(0, 5),
            ["tilt"] = rng.Chance(0.35) ? rng.Range(2, 4) * rng.Pick(new[] { 1, -1 }) : 0,
            ["rule"] = rng.Chance(0.55),
        },
        Draw = env =>
        {
            var W = env.W;
            var H = env.H;
            var sc = env.Sc!;
            var c = env.Cut!;
            var Pm = env.P;
            var lt = env.Lt;
            var u = U(env);
            var units = UnitsOf(c, IsPort(env) ? 6 : 5, 2);
            var n = units.Length;
            var ts = Onsets(env, n, frac: 0.42, gap: 0.32);
            var ms = units.Select(t => Em(t, JLayouts.S(Pm, "font")!, new JObject { ["track"] = 0.02 })).ToArray();
            var tilt = JLayouts.D(Pm, "tilt");
            var maxW = W * (tilt != 0 ? 0.8 : 0.86);
            var hb = H * (IsPort(env) ? 0.66 : 0.8);
            var cap = Math.Min(H * 0.34, W * 0.44);
            const double gap = 0.08;

            (double[] Sizes, double[] Ys, double Sw) Lay(int k)
            {
                var w = maxW;
                var sizes = new double[k];
                for (var q = 0; q < 5; q++)
                {
                    for (var i = 0; i < k; i++) sizes[i] = Math.Min(w / ms[i], cap);
                    var lo = sizes.Min() * 2.2; // 最小字之上最多留 2.2 倍，保持大小对比可读
                    for (var i = 0; i < k; i++) sizes[i] = Math.Min(sizes[i], lo);
                    var s = sizes.Sum() + gap * sizes[0] * (k - 1);
                    if (s <= hb) break;
                    w *= hb / s;
                }
                var g = gap * sizes[0];
                var tot = sizes.Sum() + g * (k - 1);
                var y = -tot / 2;
                var ys = new double[k];
                for (var i = 0; i < k; i++)
                {
                    ys[i] = y + sizes[i] / 2;
                    y += sizes[i] + g;
                }
                var sw = 0.0;
                for (var i = 0; i < k; i++) sw = Math.Max(sw, sizes[i] * ms[i]);
                return (sizes, ys, sw);
            }

            var k = CurIdx(ts, lt) + 1;
            if (k <= 0)
            {
                env.Body = null;
                return;
            }
            var aL = Lay(k);
            var bL = k > 1 ? Lay(k - 1) : aL;
            var e = J.E.OutExpo(J.Clamp((lt - ts[k - 1]) / 0.34));
            var sw = J.Lerp(bL.Sw, aL.Sw, e);
            var cx = W / 2.0;
            var cy = H / 2.0;
            var al = JLayouts.S(Pm, "align");
            var @out = Tout(env);
            BBox? bb = null;
            for (var i = 0; i < k; i++)
            {
                var fresh = i == k - 1;
                var size = fresh ? aL.Sizes[i] : J.Lerp(bL.Sizes[i], aL.Sizes[i], e);
                var y = fresh ? aL.Ys[i] : J.Lerp(bL.Ys[i], aL.Ys[i], e);
                var x = al == "left" ? -sw / 2 : al == "right" ? sw / 2 : 0;
                double rot = 0, alpha = 1;
                var q = (lt - ts[i]) / 0.24;
                if (q < 1)
                {
                    var f = 1 - J.E.OutExpo(J.Clamp(q));
                    var from = JLayouts.S(Pm, "from");
                    if (from == "scale")
                    {
                        size *= 1 + 1.6 * f;
                        alpha = J.Clamp(q * 5);
                        rot = f * 8 * (i % 2 != 0 ? 1 : -1);
                    }
                    else if (from == "drop")
                    {
                        y -= H * 0.7 * Math.Pow(1 - J.Clamp(q), 2);
                    }
                    else
                    {
                        x += (i % 2 != 0 ? 1 : -1) * W * 0.9 * f;
                    }
                }
                // 新一行砸下时，已落地的行跟着震一下
                var land = lt - ts[k - 1] - 0.12;
                if (!fresh && k > 1 && land > 0)
                    y += aL.Sizes[k - 1] * 0.07 * Sprg(land, 9, 26) * (land < 0.6 ? 1 : 0);
                var px = cx + x;
                var py = cy + y;
                if (tilt != 0)
                {
                    var r = RotV(x, y, tilt);
                    px = cx + r[0];
                    py = cy + r[1];
                    rot += tilt;
                }
                var acc = (int)JLayouts.D(Pm, "acc");
                var it = new JObject
                {
                    ["text"] = units[i],
                    ["font"] = JLayouts.S(Pm, "font"),
                    ["size"] = size,
                    ["x"] = px,
                    ["y"] = py,
                    ["align"] = al == "left" ? "left" : al == "right" ? "right" : "center",
                    ["track"] = 0.02,
                    ["rot"] = rot,
                    ["alpha"] = alpha,
                    ["color"] = n > 1 && i == acc % n ? AccOn(sc) : sc.Str("fg"),
                    ["mi"] = MiAt(env, ts[i]),
                };
                var r2 = JLayouts.MainDraw(env, it);
                bb = UB(bb, r2);
                if (JLayouts.Truthy(Pm["rule"]) && r2 != null && tilt == 0 && i < k - 1)
                {
                    var lw = Math.Max(1.5, u * 0.003);
                    var gy = py + size * 0.5 + aL.Sizes[0] * gap * 0.5;
                    var w0 = al == "left" ? cx - sw / 2 : al == "right" ? cx + sw / 2 - sw : cx - sw / 2;
                    var re = J.E.OutExpo(J.Clamp((lt - ts[i + 1] - 0.05) / 0.35)) * @out;
                    if (re > 0)
                        env.Line(new[]
                        {
                            new[] { w0, gy }, new[] { w0 + sw * re, gy },
                        }, sc.Str("sub"), lw, 0.8, false);
                }
            }
            env.Body = bb;
        },
    });

    // ---------------- knQuarterTurn（JS L302） ----------------

    private static void KnQuarterTurn() => Reg0("knQuarterTurn", new LayoutDef
    {
        Name = "直角ターン",
        Tags = new[] { "pop", "graphic", "editorial" },
        W = 0.9,
        Portrait = 0.9,
        Fits = n => n >= 2 && n <= 16,
        EnterBias = new JObject { ["cut"] = 3, ["pop"] = 0.8, ["blur"] = 0.8, ["slice"] = 0.2, ["wipe"] = 0.3 },
        Plan = (rng, _, st) => new JObject
        {
            // ae: 'sideways'
            ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
            ["sgn"] = rng.Pick(new[] { 1, -1 }),
            ["end"] = "all",
            ["acc"] = rng.Int(0, 3),
            ["joint"] = rng.Chance(0.6),
        },
        Draw = env =>
        {
            var W = env.W;
            var H = env.H;
            var sc = env.Sc!;
            var c = env.Cut!;
            var Pm = env.P;
            var lt = env.Lt;
            var u = U(env);
            var units = UnitsOf(c, 4, 2);
            var n = units.Length;
            var ts = Onsets(env, n, frac: 0.46, gap: 0.5);
            var font = JLayouts.S(Pm, "font")!;
            // 以 em 串链：词 i 沿方向 a_i 走，拐角处转 90°（阶梯式，转向左右交替）
            var lLen = units.Select(t => Em(t, font, new JObject { ["track"] = 0.03 })).ToArray();
            var lastAdv = units.Select(t =>
            {
                var cps = JLayouts.CodePoints(t.Trim());
                var ch = cps.Count > 0 ? cps[^1] : "字";
                return JLayouts.MetricsAdv(font, ch);
            }).ToArray();
            var chA = new double[n];
            var chCx = new double[n];
            var chCy = new double[n];
            var chEx = new double[n];
            var chEy = new double[n];
            double sx0 = 0, sy0 = 0, a = 0;
            for (var i = 0; i < n; i++)
            {
                if (i > 0)
                {
                    var pd = RotV(1, 0, a);
                    var na = a + 90 * JLayouts.D(Pm, "sgn") * (i % 2 != 0 ? 1 : -1);
                    var nd = RotV(1, 0, na);
                    sx0 = chEx[i - 1] - pd[0] * lastAdv[i - 1] * 0.5 + nd[0] * 0.62;
                    sy0 = chEy[i - 1] - pd[1] * lastAdv[i - 1] * 0.5 + nd[1] * 0.62;
                    a = na;
                }
                var d = RotV(1, 0, a);
                chA[i] = a;
                chCx[i] = sx0 + d[0] * lLen[i] / 2;
                chCy[i] = sy0 + d[1] * lLen[i] / 2;
                chEx[i] = sx0 + d[0] * lLen[i];
                chEy[i] = sy0 + d[1] * lLen[i];
            }
            // 每个词一台机位：转正、居中、缩放到放得下
            double ZW(int i) => Math.Min(Math.Min(W * 0.76 / lLen[i], H * 0.3), W * 0.42);
            var camR = new double[n];
            var camX = new double[n];
            var camY = new double[n];
            var camZ = new double[n];
            for (var i = 0; i < n; i++)
            {
                camR[i] = -chA[i];
                camX[i] = chCx[i];
                camY[i] = chCy[i];
                camZ[i] = ZW(i);
            }
            // 全景机位：一眼看全，第一个词保持水平
            double x0 = 1e9, y0 = 1e9, x1 = -1e9, y1 = -1e9;
            for (var i = 0; i < n; i++)
            {
                var hw = lLen[i] / 2;
                const double hh = 0.55;
                var v = Math.Abs(chA[i] % 180) > 45;
                var w2 = v ? hh : hw;
                var h2 = v ? hw : hh;
                x0 = Math.Min(x0, chCx[i] - w2);
                x1 = Math.Max(x1, chCx[i] + w2);
                y0 = Math.Min(y0, chCy[i] - h2);
                y1 = Math.Max(y1, chCy[i] + h2);
            }
            var ovR = 0.0;
            var ovX = (x0 + x1) / 2;
            var ovY = (y0 + y1) / 2;
            var ovZ = Math.Min(Math.Min(W * 0.84 / (x1 - x0), H * 0.78 / (y1 - y0)), H * 0.3);
            var k = CurIdx(ts, lt);
            if (k < 0)
            {
                env.Body = null;
                return;
            }
            var tOv = Math.Min(ts[n - 1] + 0.62, Math.Max(ts[n - 1] + 0.3, c.Dur - c.OutDur - 0.55));
            var useOv = JLayouts.S(Pm, "end") != "last";
            var ar = camR[Math.Max(0, k - 1)];
            var ax = camX[Math.Max(0, k - 1)];
            var ay = camY[Math.Max(0, k - 1)];
            var az = camZ[Math.Max(0, k - 1)];
            var br = camR[k];
            var bx = camX[k];
            var by = camY[k];
            var bz = camZ[k];
            var e = k > 0 ? J.E.InOutCubic(J.Clamp((lt - ts[k]) / 0.36)) : 1;
            var same = k == 0;
            if (useOv && lt >= tOv)
            {
                ar = camR[n - 1];
                ax = camX[n - 1];
                ay = camY[n - 1];
                az = camZ[n - 1];
                br = ovR;
                bx = ovX;
                by = ovY;
                bz = ovZ;
                e = J.E.InOutCubic(J.Clamp((lt - tOv) / 0.5));
                same = false;
            }
            var dip = 1 - 0.22 * BellK(e) * (same ? 0 : 1);
            var cam = new[]
            {
                J.Lerp(ar, br, e),
                J.Lerp(ax, bx, e),
                J.Lerp(ay, by, e),
                Math.Exp(J.Lerp(Math.Log(az), Math.Log(bz), e)) * dip,
            };
            BBox? bb = null;
            var @out = Tout(env);
            var acc = (int)JLayouts.D(Pm, "acc");
            for (var i = 0; i <= k; i++)
            {
                var q = J.Clamp((lt - ts[i]) / 0.22);
                var pop = i == 0 ? 1 : J.Lerp(0.3, 1, J.E.OutBack(q, 2.2));
                var rv = RotV(chCx[i] - cam[1], chCy[i] - cam[2], cam[0]);
                var it = new JObject
                {
                    ["text"] = units[i],
                    ["font"] = font,
                    ["size"] = cam[3] * pop,
                    ["x"] = W / 2.0 + rv[0] * cam[3],
                    ["y"] = H / 2.0 + rv[1] * cam[3],
                    ["rot"] = chA[i] + cam[0],
                    ["track"] = 0.03,
                    ["color"] = i == acc % n ? AccOn(sc) : sc.Str("fg"),
                    ["mi"] = MiAt(env, ts[i]),
                    ["alpha"] = i == 0 ? 1 : J.Clamp(q * 4),
                };
                bb = UB(bb, JLayouts.MainDraw(env, it));
                // 拐角标记：转折处放一小块 accent 方点
                if (JLayouts.Truthy(Pm["joint"]) && i > 0)
                {
                    var pd = RotV(1, 0, chA[i - 1]);
                    var jx = chEx[i - 1] + pd[0] * 0.35;
                    var jy = chEy[i - 1] + pd[1] * 0.35;
                    var ev = RotV(jx - cam[1], jy - cam[2], cam[0]);
                    var s = cam[3] * 0.16 * J.E.OutBack(J.Clamp(q * 1.3), 2) * @out;
                    if (s > 0.5)
                    {
                        var ctx = env.Ctx;
                        ctx.Save();
                        ctx.Translate((float)(W / 2.0 + ev[0] * cam[3]), (float)(H / 2.0 + ev[1] * cam[3]));
                        ctx.RotateDegrees((float)((cam[0] + 45) * J.DEG));
                        env.Rect(-s / 2, -s / 2, s, s, sc.Str("accent"), 1, false);
                        ctx.Restore();
                    }
                }
            }
            env.Body = bb != null && u > 0 ? bb : Box(W * 0.3, H * 0.4, W * 0.7, H * 0.6);
        },
    });

    // ---------------- knSwapCenter（JS L424） ----------------

    private static void KnSwapCenter() => Reg0("knSwapCenter", new LayoutDef
    {
        Name = "入れ替わり",
        Tags = new[] { "pop", "graphic", "glitch" },
        W = 1,
        Fits = n => n >= 2 && n <= 18,
        EnterBias = new JObject { ["cut"] = 3, ["pop"] = 0.7, ["blur"] = 0.8, ["slice"] = 0.3, ["wipe"] = 0.3 },
        Plan = (rng, _, st) => new JObject
        {
            // ae: 'slotMachine'
            ["font"] = rng.Pick(FontsOf(st, "display")),
            ["fontL"] = rng.Pick(FontsOf(st, "display", "serif")),
            ["mode"] = rng.Pick(new[] { "roll", "punch", "slide", "roll" }),
            ["ticks"] = rng.Chance(0.7),
            ["acc"] = rng.Chance(0.5),
        },
        Draw = env =>
        {
            var W = env.W;
            var H = env.H;
            var sc = env.Sc!;
            var c = env.Cut!;
            var Pm = env.P;
            var lt = env.Lt;
            var u = U(env);
            var port = IsPort(env);
            var units = UnitsOf(c, 6, 2);
            var n = units.Length;
            var ts = Onsets(env, n, frac: 0.4, gap: 0.36);
            var font = JLayouts.S(Pm, "font")!;
            var fontL = JLayouts.S(Pm, "fontL")!;
            var big = units.Select(t => Math.Min(Math.Min(
                JLayouts.FitSize(t, font, W * 0.8, H * 0.4, new JObject { ["track"] = 0.02 }), H * 0.34), W * 0.6)).ToArray();
            var f = FlowUnits(units, fontL, W * 0.84, H * (port ? 0.46 : 0.34),
                maxSize: H * (port ? 0.13 : 0.2), track: 0.03);
            var tR = ts[n - 1] + 0.5;
            var lim = c.Dur - c.OutDur - 0.4;
            if (tR > lim) tR = Math.Max(ts[n - 1] + 0.18, lim);
            var k = CurIdx(ts, lt);
            if (k < 0)
            {
                env.Body = null;
                return;
            }
            var cx = W / 2.0;
            var cy = H / 2.0;
            var @out = Tout(env);
            BBox? bb = null;
            var accC = AccOn(sc);
            var accOn = JLayouts.Truthy(Pm["acc"]);
            var mode = JLayouts.S(Pm, "mode");

            void DrawUnit(int i, double q, bool old)
            {
                var s0 = big[i];
                var x = cx;
                var y = cy;
                var size = s0;
                double sy = 1, alpha = 1;
                if (mode == "roll")
                {
                    var d = s0 * 1.05 * (old ? -q : 1 - q);
                    y += d;
                    sy = old ? 1 - q : q;
                }
                else if (mode == "punch")
                {
                    size = s0 * (old ? J.Lerp(1, 0.45, q) : J.Lerp(1.9, 1, q));
                    alpha = old ? 1 - q : J.Clamp(q * 3);
                }
                else
                {
                    x += (old ? -q : 1 - J.E.OutBack(q, 1.3)) * W * 0.7;
                    alpha = old ? 1 - q * q : 1;
                }
                if (sy < 0.02 || alpha < 0.01) return;
                var it = new JObject
                {
                    ["text"] = units[i],
                    ["font"] = font,
                    ["size"] = size,
                    ["x"] = x,
                    ["y"] = y,
                    ["sy"] = sy,
                    ["track"] = 0.02,
                    ["alpha"] = alpha,
                    ["color"] = accOn && i % 2 != 0 ? accC : sc.Str("fg"),
                    ["mi"] = MiAt(env, ts[i]),
                    ["noHold"] = old,
                };
                bb = UB(bb, JLayouts.MainDraw(env, it));
            }

            if (lt < tR)
            {
                var e = k > 0 ? J.E.OutExpo(J.Clamp((lt - ts[k]) / 0.26)) : 1;
                if (k > 0 && e < 1) DrawUnit(k - 1, e, true);
                DrawUnit(k, e, false);
                if (JLayouts.Truthy(Pm["ticks"]) && n > 1)
                {
                    var tw = u * 0.035;
                    var th = Math.Max(3, u * 0.006);
                    var gx = tw * 1.5;
                    var y = Math.Min(H * 0.9, cy + big[k] * 0.5 + u * 0.08);
                    for (var i = 0; i < n; i++)
                    {
                        var x = cx + (i - (n - 1) / 2.0) * gx - tw / 2;
                        var on = i <= k;
                        env.Rect(x, y, tw, th, on ? accC : sc.Str("sub"), (on ? 1 : 0.4) * @out * J.Clamp(lt / 0.2), false);
                    }
                }
                env.Body = bb;
                return;
            }
            // 收束：每个词飞回整行里的位置
            for (var i = 0; i < n; i++)
            {
                var last = i == n - 1;
                var q = J.E.OutExpo(J.Clamp((lt - tR - (last ? 0 : 0.05 + i * 0.035)) / 0.42));
                var p = f.Pos[i];
                var size = last ? J.Lerp(big[i], f.Size, q) : f.Size * J.Lerp(0.3, 1, q);
                var it = new JObject
                {
                    ["text"] = units[i],
                    ["font"] = last ? (q > 0.5 ? fontL : font) : fontL,
                    ["size"] = size,
                    ["x"] = J.Lerp(cx, cx + p.X, q),
                    ["y"] = J.Lerp(cy, cy + p.Y, q),
                    ["track"] = last ? J.Lerp(0.02, 0.03, q) : 0.03,
                    ["alpha"] = last ? 1 : J.Clamp(q * 2.5),
                    ["color"] = accOn && i % 2 != 0 ? accC : sc.Str("fg"),
                    ["mi"] = last ? MiAt(env, ts[i]) : MiAt(env, tR),
                };
                bb = UB(bb, JLayouts.MainDraw(env, it));
            }
            env.Body = bb;
        },
    });

    // ---------------- knZoomDive（JS L518） ----------------

    private static void KnZoomDive() => Reg0("knZoomDive", new LayoutDef
    {
        Name = "文字へ潜る",
        Tags = new[] { "pop", "emotional", "graphic" },
        W = 0.9,
        Fits = n => n >= 2 && n <= 16,
        EnterBias = new JObject { ["cut"] = 3, ["blur"] = 0.8, ["pop"] = 0.5, ["slice"] = 0.2, ["wipe"] = 0.2 },
        Plan = (rng, _, st) => new JObject
        {
            // ae: 'zoomRepeat'
            ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
            ["cap"] = rng.Chance(0.75),
            ["acc"] = rng.Chance(0.5),
        },
        Draw = env =>
        {
            var W = env.W;
            var H = env.H;
            var sc = env.Sc!;
            var c = env.Cut!;
            var Pm = env.P;
            var lt = env.Lt;
            var u = U(env);
            var units = UnitsOf(c, 5, 2);
            var n = units.Length;
            var ts = Onsets(env, n, frac: 0.5, gap: 0.55);
            var k = CurIdx(ts, lt);
            if (k < 0)
            {
                env.Body = null;
                return;
            }
            var font = JLayouts.S(Pm, "font")!;
            var accOn = JLayouts.Truthy(Pm["acc"]);
            double Size(int i) => Math.Min(Math.Min(
                JLayouts.FitSize(units[i], font, W * 0.78, H * 0.4, new JObject { ["track"] = 0.02 }), H * 0.34), W * 0.6);

            // 焦点字：第一个汉字（没有就取中间字），em 单位、相对词中心
            double[] Focus(int i)
            {
                var lay = JText.LayoutText(new JObject
                { ["text"] = units[i], ["font"] = font, ["size"] = 1, ["track"] = 0.02 });
                var gs = lay.Cells.Where(g => g.Ch.Trim().Length > 0).ToList();
                var g = gs.FirstOrDefault(q => J.IsKanji(q.Ch[0])) ?? (gs.Count > 0 ? gs[gs.Count / 2] : null);
                return g != null ? new[] { g.X, g.Y } : new[] { 0.0, 0.0 };
            }

            var cx = W / 2.0;
            var cy = H / 2.0;
            var accC = AccOn(sc);
            BBox? bb = null;
            const double t = 0.42;
            // 上一个词绕焦点字放大，镜头从这个字里穿过去
            if (k > 0)
            {
                var e = J.Clamp((lt - ts[k]) / t);
                if (e < 1)
                {
                    var i = k - 1;
                    var s0 = Size(i);
                    var foc = Focus(i);
                    var px = J.Lerp(cx + foc[0] * s0, cx, J.E.InOutCubic(e));
                    var py = J.Lerp(cy + foc[1] * s0, cy, J.E.InOutCubic(e));
                    var s = Math.Exp(Math.Pow(e, 1.6) * Math.Log(36));
                    JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = units[i],
                        ["font"] = font,
                        ["size"] = s0 * s,
                        ["x"] = px - foc[0] * s0 * s,
                        ["y"] = py - foc[1] * s0 * s,
                        ["track"] = 0.02,
                        ["alpha"] = 1 - J.Smooth(0.55, 1, e),
                        ["color"] = accOn && i % 2 != 0 ? accC : sc.Str("fg"),
                        ["mi"] = MiAt(env, ts[i]),
                        ["noHold"] = true,
                    });
                }
            }
            var s1 = Size(k);
            var q = k > 0 ? J.E.OutExpo(J.Clamp((lt - ts[k] - t * 0.35) / 0.45)) : 1;
            if (q > 0.001)
            {
                bb = JLayouts.MainDraw(env, new JObject
                {
                    ["text"] = units[k],
                    ["font"] = font,
                    ["size"] = s1 * J.Lerp(0.03, 1, q),
                    ["x"] = cx,
                    ["y"] = cy,
                    ["track"] = 0.02,
                    ["alpha"] = J.Clamp(q * 3),
                    ["color"] = accOn && k % 2 != 0 ? accC : sc.Str("fg"),
                    ["mi"] = MiAt(env, ts[k]),
                });
            }
            // 最后一个词落定后，整句缩成底部小字幕
            if (JLayouts.Truthy(Pm["cap"]) && n > 1 && k == n - 1 && bb != null)
            {
                var aCap = J.E.OutCubic(J.Clamp((lt - ts[k] - 0.55) / 0.35)) * Tout(env);
                if (aCap > 0.01)
                {
                    var txt = Flat(c.Text);
                    var body = BodyF(env);
                    var fs = Math.Min(J.Clamp(u * 0.034, 14, 40),
                        JLayouts.FitSize(txt, body, W * 0.8, H * 0.1, new JObject { ["track"] = 0.18 }));
                    var capY = Math.Min(H * 0.92, bb.Y1 + fs * 1.6);
                    env.DrawItem(new JObject
                    {
                        ["text"] = txt,
                        ["font"] = body,
                        ["size"] = fs,
                        ["track"] = 0.18,
                        ["x"] = cx,
                        ["y"] = capY,
                        ["color"] = sc.Str("sub"),
                        ["alpha"] = aCap,
                        ["ghost"] = false,
                    });
                    var lw = Math.Max(1, u * 0.002);
                    var w = JLayouts.Measure(new JObject { ["text"] = txt, ["font"] = body, ["size"] = fs, ["track"] = 0.18 }).W * aCap;
                    env.Line(new[]
                    {
                        new[] { cx - w / 2, capY - fs * 1.1 }, new[] { cx + w / 2, capY - fs * 1.1 },
                    }, sc.Str("sub"), lw, 0.6, false);
                }
            }
            env.Body = bb ?? Box(W * 0.3, H * 0.4, W * 0.7, H * 0.6);
        },
    });

    // ---------------- knFlowSnap（JS L608） ----------------

    private static void KnFlowSnap() => Reg0("knFlowSnap", new LayoutDef
    {
        Name = "流れて整列",
        Tags = new[] { "graphic", "pop", "editorial" },
        W = 0.9,
        Fits = n => n >= 3 && n <= 16,
        EnterBias = new JObject { ["cut"] = 3, ["blur"] = 0.6, ["pop"] = 0.6, ["slice"] = 0.2, ["wipe"] = 0.2 },
        Plan = (rng, _, st) => new JObject
        {
            // ae: 'gridCells'
            ["font"] = rng.Pick(FontsOf(st, "display", "body")),
            ["amp"] = rng.Range(0.1, 0.16),
            ["lam"] = rng.Range(0.45, 0.7),
            ["grid"] = rng.Pick(new[] { "cells", "cells", "rules" }),
            ["acc"] = rng.Int(0, 15),
        },
        Draw = env =>
        {
            var W = env.W;
            var H = env.H;
            var sc = env.Sc!;
            var c = env.Cut!;
            var Pm = env.P;
            var lt = env.Lt;
            var u = U(env);
            var port = IsPort(env);
            var font = JLayouts.S(Pm, "font")!;
            var lat = HasLatin(c.Text);
            var t0 = Flat(c.Text);
            var chars = JLayouts.CodePoints(t0);
            var n = chars.Count;
            // 终态排版：日文方格阵，拉丁文均衡分行
            string ft;
            double trk;
            if (lat)
            {
                ft = JLayouts.SplitLines(t0, port ? 10 : 18);
                trk = 0.02;
            }
            else
            {
                var m = JLayouts.CpLen(t0);
                var c0 = Math.Max(2, Math.Min(port ? 4 : 6, (int)Math.Ceiling(Math.Sqrt(m * (port ? 0.7 : 1.6)))));
                var cols = (int)Math.Ceiling(m / (double)Math.Ceiling(m / (double)c0));
                var rows = new List<string>();
                for (var i = 0; i < m; i += cols)
                    rows.Add(string.Concat(chars.Skip(i).Take(cols)));
                ft = string.Join("\n", rows);
                trk = 0.3;
            }
            var lead = lat ? 1.25 : 1.3;
            var fsz = Math.Min(
                JLayouts.FitSize(ft, font, W * 0.8, H * (port ? 0.5 : 0.62), new JObject { ["track"] = trk, ["lead"] = lead }), H * 0.2);
            var lay = JText.LayoutText(new JObject
            {
                ["text"] = ft,
                ["font"] = font,
                ["size"] = fsz,
                ["track"] = trk,
                ["lead"] = lead,
                ["align"] = lat ? "center" : "left",
            });
            var mmW = lay.W;
            var mmH = lay.H;
            var ox = W / 2.0 - (lat ? 0 : mmW / 2);
            var oy = H / 2.0;
            var gl = lay.Cells.Where(g => g.Ch != "\n").ToList();
            // 吸附时刻：约在镜内 40% 处
            var tS = J.Clamp(c.Dur * 0.42, 0.45, 1.5);
            var advs = gl.Select((g, i) =>
                JLayouts.MetricsAdv(font, g.Ch) * 1.08 + (i > 0 && g.Li != gl[i - 1].Li ? 0.35 : 0)).ToList();
            var totA = advs.Sum();
            var fs = Math.Min(H * 0.15, W * 0.95 / Math.Max(4, totA));
            var offs = new double[gl.Count];
            double acc0 = 0;
            for (var i = 0; i < advs.Count; i++)
            {
                offs[i] = (acc0 + advs[i] / 2) * fs;
                acc0 += advs[i];
            }
            // 字列从右缘冲入，向中间减速，然后吸附到位
            var xs = W / 2.0 - totA * fs / 2;
            var x0 = W + fs * 0.8;
            var amp = Math.Min(H * JLayouts.D(Pm, "amp"), W * 0.16);
            var lam = W * JLayouts.D(Pm, "lam") * (port ? 1.7 : 1);

            double Cy0(double x, double t) => H / 2.0 + amp * Math.Sin(x / lam * J.TAU + t * 3.2);

            double[] FlowAt(int i, double t)
            {
                var x = J.Lerp(x0, xs, J.E.OutCubic(J.Clamp(t / tS))) + (i < offs.Length ? offs[i] : 0);
                var y = Cy0(x, t);
                var sl = (Cy0(x + 2, t) - Cy0(x - 2, t)) / 4;
                return new[] { x, y, Math.Atan(sl) / J.DEG };
            }

            var tf = Math.Min(lt, tS);
            BBox? bb = null;
            var @out = Tout(env);
            var accC = AccOn(sc);
            var acc = (int)JLayouts.D(Pm, "acc");
            for (var i = 0; i < gl.Count; i++)
            {
                var g = gl[i];
                if (g.Ch is " " or "　") continue;
                var fl = FlowAt(i, tf);
                var q = J.E.OutExpo(J.Clamp((lt - tS - i * 0.018) / 0.3));
                var x = J.Lerp(fl[0], ox + g.X, q);
                var y = J.Lerp(fl[1], oy + g.Y, q);
                var rot = fl[2] * (1 - q);
                var size = J.Lerp(fs, fsz * (g.Fs != 0 ? g.Fs : 1), q);
                if (x > W + size && q <= 0) continue;
                var it = new JObject
                {
                    ["text"] = g.Ch,
                    ["font"] = font,
                    ["size"] = size,
                    ["x"] = x,
                    ["y"] = y,
                    ["rot"] = rot,
                    ["color"] = !lat && i == acc % n ? accC : sc.Str("fg"),
                    ["mi"] = i * 0.3,
                };
                bb = UB(bb, JLayouts.MainDraw(env, it));
            }
            // 格线跟着吸附一起画出来
            var ge = J.E.OutExpo(J.Clamp((lt - tS - 0.08) / 0.5)) * @out;
            if (ge > 0.01)
            {
                var lw = Math.Max(1, u * 0.0016);
                if (!lat && JLayouts.S(Pm, "grid") == "cells")
                {
                    var cell = fsz * (1 + trk);
                    var fts = ft.Split('\n');
                    var rows = fts.Length;
                    var cols = fts.Max(r => JLayouts.CpLen(r));
                    var gx0 = ox - fsz * trk / 2;
                    var gy0 = oy - mmH / 2 - (cell - fsz) / 2 - (fsz * lead - cell) / 2;
                    var rh = fsz * lead;
                    for (var r = 0; r <= rows; r++)
                    {
                        var y = gy0 + r * rh;
                        env.Line(new[]
                        {
                            new[] { gx0, y }, new[] { gx0 + cols * cell * ge, y },
                        }, sc.Str("sub"), lw, 0.55, false);
                    }
                    for (var q2 = 0; q2 <= cols; q2++)
                    {
                        var x = gx0 + q2 * cell;
                        env.Line(new[]
                        {
                            new[] { x, gy0 }, new[] { x, gy0 + rows * rh * ge },
                        }, sc.Str("sub"), lw, 0.55, false);
                    }
                }
                else
                {
                    var lines = ft.Split('\n');
                    var rh = fsz * lead;
                    for (var li = 0; li < lines.Length; li++)
                    {
                        var ln = lines[li];
                        var w = JLayouts.Measure(new JObject { ["text"] = ln, ["font"] = font, ["size"] = fsz, ["track"] = trk }).W;
                        var y = oy + (li - (lines.Length - 1) / 2.0) * rh + fsz * 0.62;
                        var lx = lat ? W / 2.0 - w / 2 : ox;
                        env.Line(new[]
                        {
                            new[] { lx, y }, new[] { lx + w * ge, y },
                        }, sc.Str("sub"), lw * 1.5, 0.7, false);
                    }
                }
            }
            // 字列流过的轨迹，吸附后淡出
            var pa = (1 - J.Clamp((lt - tS) / 0.25)) * J.Clamp(lt / 0.15) * 0.35;
            if (pa > 0.01)
            {
                var pts = new List<double[]>();
                for (var x = -20.0; x <= W + 20; x += W / 48.0)
                    pts.Add(new[] { x, Cy0(x, tf) });
                env.Line(pts.ToArray(), sc.Str("sub"), Math.Max(1, u * 0.0015), pa, false);
            }
            env.Body = bb;
        },
    });

    // ---------------- knSeesaw（JS L735） ----------------

    private static void KnSeesaw() => Reg0("knSeesaw", new LayoutDef
    {
        Name = "シーソー",
        Tags = new[] { "pop", "graphic" },
        W = 0.8,
        Portrait = 0.4,
        Fits = n => n >= 2 && n <= 14,
        EnterBias = new JObject { ["cut"] = 3, ["pop"] = 0.6, ["blur"] = 0.5, ["slice"] = 0.2, ["wipe"] = 0.2 },
        Plan = (rng, _, st) => new JObject
        {
            // ae: 'bounceLine'
            ["font"] = rng.Pick(FontsOf(st, "display")),
            ["order"] = rng.Pick(new[] { "lr", "lr", "out" }),
            ["acc"] = rng.Chance(0.5),
            ["fulc"] = rng.Pick(new[] { "tri", "tri", "round" }),
        },
        Draw = env =>
        {
            var W = env.W;
            var H = env.H;
            var sc = env.Sc!;
            var ctx = env.Ctx;
            var c = env.Cut!;
            var Pm = env.P;
            var lt = env.Lt;
            var port = IsPort(env);
            var units = UnitsOf(c, 6, 2);
            var n = units.Length;
            var font = JLayouts.S(Pm, "font")!;
            var f = FlowUnits(units, font, W * 0.78, H * 0.2,
                maxLines: 1, maxSize: H * 0.17, track: 0.03, sp: HasLatin(c.Text) ? 0.4 : 0.22);
            var size = f.Size;
            var th = Math.Max(4, size * 0.1);
            var half = f.W / 2 + size * 0.6;
            var px = W / 2.0;
            var py = H * (port ? 0.55 : 0.6);
            // 落地顺序：从左到右，或从中间向两边
            var ord = Enumerable.Range(0, n).ToList();
            if (JLayouts.S(Pm, "order") == "out")
                ord.Sort((a, b2) => Math.Abs(f.Pos[a].X).CompareTo(Math.Abs(f.Pos[b2].X)));
            var ts0 = Onsets(env, n, frac: 0.45, gap: 0.4);
            const double fall = 0.24;
            var tl = new double[n];
            for (var r = 0; r < ord.Count; r++)
                tl[ord[r]] = ts0[r] + fall;
            var mass = units.Select(Gcount).ToArray();
            var mTot = mass.Sum();
            // 板子角度：每次落地往已落词的力矩方向压，全员落定后回平
            var targ = new List<double> { 0 };
            double tq = 0;
            for (var r = 0; r < ord.Count; r++)
            {
                var i = ord[r];
                tq += mass[i] * f.Pos[i].X;
                targ.Add(r == n - 1 ? 0 : J.Clamp(tq / (mTot * half) * 30, -11, 11));
            }
            double th0 = 0;
            for (var r = 0; r < n; r++)
            {
                var t0 = ts0[r] + fall;
                if (lt < t0) break;
                th0 = targ[r + 1] + (targ[r] - targ[r + 1]) * Sprg(lt - t0, 3.6, 10);
            }
            var ang = th0;
            var e = J.E.OutExpo(J.Clamp(lt / 0.35));
            var @out = Tout(env);
            var plateC = sc.Str("sub");
            var accC = AccOn(sc);
            var accOn = JLayouts.Truthy(Pm["acc"]);
            // 支点和跷板
            var fh = size * 0.62;
            if (JLayouts.S(Pm, "fulc") == "tri")
                env.Poly(new[]
                {
                    new[] { px, py + th / 2 },
                    new[] { px - fh * 0.62 * e, py + th / 2 + fh * e },
                    new[] { px + fh * 0.62 * e, py + th / 2 + fh * e },
                }, accC, @out, false);
            else
                env.Circle(px, py + th / 2 + fh * 0.45, fh * 0.45 * e, accC, null, 0, @out, false);
            ctx.Save();
            ctx.Translate((float)px, (float)py);
            ctx.RotateDegrees((float)ang);
            env.Rect(-half * e, -th / 2, half * 2 * e, th, plateC, 0.9 * @out, false);
            ctx.Restore();
            BBox? bb = null;
            var cr = Math.Cos(ang * J.DEG);
            var sr = Math.Sin(ang * J.DEG);
            var maxMass = mass.Length > 0 ? mass.Max() : 0;
            for (var i = 0; i < n; i++)
            {
                var t0 = tl[i] - fall;
                if (lt < t0) continue;
                var p = f.Pos[i];
                var v = -th / 2 - size * 0.54;
                var x = px + p.X * cr - v * sr;
                var y = py + p.X * sr + v * cr;
                double rot = ang, sy = 1, sx = 1;
                var d = lt - tl[i];
                if (d < 0)
                {
                    var kk = -d / fall;
                    y -= H * 0.55 * kk * kk;
                    rot = ang * (1 - kk);
                }
                else
                {
                    var q = Math.Exp(-d * 11) * 0.22;
                    sy = 1 - q;
                    sx = 1 + q * 0.6;
                    x += sr * size * q * 0.5;
                    y += cr * size * q * 0.5;
                }
                var it = new JObject
                {
                    ["text"] = units[i],
                    ["font"] = font,
                    ["size"] = size,
                    ["x"] = x,
                    ["y"] = y,
                    ["rot"] = rot,
                    ["sx"] = sx,
                    ["sy"] = sy,
                    ["track"] = 0.03,
                    ["color"] = accOn && mass[i] == maxMass ? accC : sc.Str("fg"),
                    ["mi"] = MiAt(env, t0),
                };
                bb = UB(bb, JLayouts.MainDraw(env, it));
            }
            env.Body = bb;
        },
    });

    // ---------------- knTypeSlam（JS L835） ----------------

    private static void KnTypeSlam() => Reg0("knTypeSlam", new LayoutDef
    {
        Name = "タイプ→スラム",
        Tags = new[] { "pop", "graphic", "editorial" },
        W = 1,
        Fits = n => n >= 2 && n <= 24,
        EnterBias = new JObject { ["cut"] = 3, ["blur"] = 0.5, ["pop"] = 0.5, ["slice"] = 0.2, ["wipe"] = 0.3 },
        Plan = (rng, _, st) => new JObject
        {
            // ae: 'type'
            ["font"] = rng.Pick(FontsOf(st, "display")),
            ["key"] = rng.Pick(new[] { "long", "long", "last" }),
            ["side"] = rng.Pick(new[] { "below", "below", "above" }),
            ["burst"] = rng.Chance(0.7),
        },
        Draw = env =>
        {
            var W = env.W;
            var H = env.H;
            var sc = env.Sc!;
            var c = env.Cut!;
            var Pm = env.P;
            var lt = env.Lt;
            var u = U(env);
            var port = IsPort(env);
            var t0 = Flat(c.Text);
            var units = UnitsOf(c, 6, 1);
            var ki = units.Length - 1;
            if (JLayouts.S(Pm, "key") == "long")
            {
                var bv = -1.0;
                for (var i = 0; i < units.Length; i++)
                {
                    var v = Gcount(units[i]) + (Regex.IsMatch(units[i], "[一-鿿]") ? 0.5 : 0);
                    if (v > bv)
                    {
                        bv = v;
                        ki = i;
                    }
                }
            }
            var key = units[ki];
            var tf = MonoF(env);
            var font = JLayouts.S(Pm, "font")!;
            var ts = Math.Min(
                JLayouts.FitSize(t0, tf, W * 0.86, H * 0.08, new JObject { ["track"] = 0.06 }), u * 0.065);
            var tm = JLayouts.Measure(new JObject { ["text"] = t0, ["font"] = tf, ["size"] = ts, ["track"] = 0.06 });
            var ks = Math.Min(Math.Min(
                JLayouts.FitSize(key, font, W * 0.84, H * (port ? 0.3 : 0.44), new JObject { ["track"] = 0.01 }), H * 0.34), W * 0.5);
            var above = JLayouts.S(Pm, "side") == "above";
            var ky = H / 2.0 + (above ? ts * 1.2 : -ts * 1.2);
            var ty = above ? ky - ks * 0.62 - ts * 1.5 : ky + ks * 0.62 + ts * 1.5;
            var nG = JLayouts.CpLen(t0);
            var tType = J.Clamp(nG * 0.045, 0.25, Math.Min(1.1, c.Dur * 0.4));
            var tS = tType + 0.14;
            var x0 = W / 2.0 - tm.W / 2;
            var seed = unchecked((int)c.Seed);
            var step = (int)env.Step;
            // 打字行：clip 逐字露出，光标跟在末尾
            var k = (int)Math.Floor(J.Clamp(lt / tType) * nG + 1e-6);
            var pre = string.Concat(JLayouts.CodePoints(t0).Take(k));
            var wk = k >= nG
                ? tm.W + ts
                : JLayouts.Measure(new JObject { ["text"] = pre, ["font"] = tf, ["size"] = ts, ["track"] = 0.06 }).W + (k > 0 ? ts * 0.06 : 0);
            var imp = lt - tS - 0.1;
            var shake = imp > 0 ? Math.Exp(-imp * 10) * ts * 0.35 : 0;
            var itT = new JObject
            {
                ["text"] = t0,
                ["font"] = tf,
                ["size"] = ts,
                ["x"] = x0 + J.Rs(seed, step, 3) * shake,
                ["y"] = ty + J.Rs(seed, step, 4) * shake,
                ["align"] = "left",
                ["track"] = 0.06,
                ["color"] = sc.Str("fg"),
                ["mi"] = 0.0,
            };
            if (k < nG) itT["clip"] = new JArray(x0 - ts, x0 + wk);
            var bb = JLayouts.MainDraw(env, itT);
            var @out = Tout(env);
            var accC = AccOn(sc);
            if (k < nG || step % 2 == 0)
            {
                if (lt < tS + 0.6)
                    env.Rect(x0 + wk + ts * 0.1, ty - ts * 0.5, ts * 0.55, ts, accC, @out, false);
            }
            // 关键词砸进来
            if (lt >= tS)
            {
                var q = J.Clamp((lt - tS) / 0.16);
                var fq = 1 - J.E.InQuad(q);
                var land = lt - tS - 0.16;
                var sq = land > 0 ? Math.Exp(-land * 12) * 0.12 : 0;
                var it = new JObject
                {
                    ["text"] = key,
                    ["font"] = font,
                    ["size"] = ks * (1 + 2.4 * fq),
                    ["x"] = W / 2.0,
                    ["y"] = ky,
                    ["sx"] = 1 + sq * 0.5,
                    ["sy"] = 1 - sq,
                    ["track"] = 0.01,
                    ["alpha"] = J.Clamp(q * 4),
                    ["color"] = sc.Str("fg"),
                    ["mi"] = MiAt(env, tS),
                };
                bb = UB(bb, JLayouts.MainDraw(env, it));
                if (JLayouts.Truthy(Pm["burst"]) && land > 0 && land < 0.5)
                {
                    var a = 1 - land / 0.5;
                    var r0Base = ks * 0.9;
                    var lw = Math.Max(2, u * 0.004);
                    var mk = JLayouts.Measure(new JObject { ["text"] = key, ["font"] = font, ["size"] = ks, ["track"] = 0.01 });
                    for (var j = 0; j < 10; j++)
                    {
                        var an = j / 10.0 * J.TAU + J.R(seed, j, 5) * 0.4;
                        var r0 = Math.Max(mk.W, mk.H) * 0.55 + r0Base * 0.15 + land * u * 0.25;
                        var r1 = r0 + u * 0.05 * a;
                        var sxj = 1.0;
                        var syy = mk.H / Math.Max(mk.W, mk.H) + 0.35;
                        env.Line(new[]
                        {
                            new[] { W / 2.0 + Math.Cos(an) * r0 * sxj, ky + Math.Sin(an) * r0 * syy },
                            new[] { W / 2.0 + Math.Cos(an) * r1 * sxj, ky + Math.Sin(an) * r1 * syy },
                        }, accC, lw, a * @out, false);
                    }
                }
                // 在打字行里给关键词下划线
                var idx = t0.IndexOf(key.Trim(), StringComparison.Ordinal);
                if (idx >= 0 && land > 0)
                {
                    var a0 = JLayouts.Measure(new JObject
                    {
                        ["text"] = t0[..idx].Length > 0 ? t0[..idx] : " ",
                        ["font"] = tf,
                        ["size"] = ts,
                        ["track"] = 0.06,
                    }).W * (idx != 0 ? 1 : 0) + (idx != 0 ? ts * 0.06 : 0);
                    var kw = JLayouts.Measure(new JObject { ["text"] = key.Trim(), ["font"] = tf, ["size"] = ts, ["track"] = 0.06 }).W;
                    var e2 = J.E.OutExpo(J.Clamp(land / 0.3)) * @out;
                    env.Rect(x0 + a0, ty + ts * 0.62, kw * e2, Math.Max(2, ts * 0.12), accC, 1, false);
                }
            }
            env.Body = bb;
        },
    });

    // ---------------- knRhythmCuts（JS L947-1053） ----------------

    private static readonly string[] Shots = { "huge", "vert", "small", "crop", "band", "tilt" };

    private static void KnRhythmCuts() => Reg0("knRhythmCuts", new LayoutDef
    {
        Name = "語のカット割り",
        Tags = new[] { "pop", "graphic", "glitch" },
        W = 0.9,
        Fits = n => n >= 2 && n <= 18,
        EnterBias = new JObject { ["cut"] = 3.5, ["pop"] = 0.4, ["blur"] = 0.4, ["slice"] = 0.2, ["wipe"] = 0.2 },
        Plan = (rng, _, st) =>
        {
            // ae: 'panels'
            var sh = Shots.ToArray();
            for (var i = sh.Length - 1; i > 0; i--)
            {
                var j = rng.Int(0, i);
                (sh[i], sh[j]) = (sh[j], sh[i]);
            }
            return new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display")),
                ["fontB"] = rng.Pick(FontsOf(st, "display", "serif")),
                ["shots"] = new JArray(sh),
                ["side"] = rng.Pick(new[] { 1, -1 }),
                ["fin"] = rng.Pick(new[] { "center", "center", "left" }),
            };
        },
        Draw = env =>
        {
            var W = env.W;
            var H = env.H;
            var sc = env.Sc!;
            var c = env.Cut!;
            var Pm = env.P;
            var lt = env.Lt;
            var u = U(env);
            var port = IsPort(env);
            var units = UnitsOf(c, 5, 2);
            var n = units.Length;
            var ts = Onsets(env, n, frac: 0.5, gap: 0.46);
            var font = JLayouts.S(Pm, "font")!;
            var fontB = JLayouts.S(Pm, "fontB")!;
            var tF = Math.Min(ts[n - 1] + 0.55, c.Dur - c.OutDur - 0.45);
            tF = Math.Max(tF, ts[n - 1] + 0.28);
            var k = CurIdx(ts, lt);
            if (k < 0)
            {
                env.Body = null;
                return;
            }
            var @out = Tout(env);
            var accC = AccOn(sc);
            var seed = unchecked((int)c.Seed);
            if (lt < tF)
            {
                var t = units[k];
                var shots = Pm["shots"] as JArray;
                var shot = shots != null && k < shots.Count ? (string?)shots[k] : Shots[k % 6];
                var dt = lt - ts[k];
                var punch = 1 + 0.1 * Math.Exp(-dt * 14);
                var it = new JObject
                {
                    ["text"] = t,
                    ["font"] = font,
                    ["x"] = W / 2.0,
                    ["y"] = H / 2.0,
                    ["color"] = sc.Str("fg"),
                    ["track"] = 0.02,
                    ["mi"] = MiAt(env, ts[k]),
                };
                var vtxt = HasLatin(t) ? t : Strip(t);
                if (shot == "huge")
                {
                    it["size"] = Math.Min(JLayouts.FitSize(t, font, W * 0.82, H * 0.6, new JObject { ["track"] = 0.02 }), H * 0.54);
                }
                else if (shot == "vert" && !HasLatin(t))
                {
                    it["text"] = vtxt;
                    it["vertical"] = true;
                    it["x"] = W / 2.0 + JLayouts.D(Pm, "side") * W * (port ? 0.18 : 0.22);
                    it["size"] = Math.Min(JLayouts.FitSize(vtxt, font, W * 0.4, H * 0.8, new JObject { ["vertical"] = true, ["track"] = 0.02 }),
                        W * (port ? 0.34 : 0.26));
                }
                else if (shot == "small" || shot == "vert")
                {
                    it["size"] = Math.Min(JLayouts.FitSize(t, fontB, W * 0.4, H * 0.12, new JObject { ["track"] = 0.12 }), H * 0.1);
                    it["font"] = fontB;
                    it["track"] = 0.12;
                    var m = JLayouts.Measure(it);
                    var pw = m.W / 2 + JLayouts.D(it, "size") * 0.8;
                    var ph = m.H / 2 + JLayouts.D(it, "size") * 0.6;
                    var ll = JLayouts.D(it, "size") * 0.6;
                    var lw = Math.Max(2, u * 0.003);
                    var e = J.E.OutExpo(J.Clamp(dt / 0.2));
                    foreach (var (a2, b2) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
                    {
                        var ccx = W / 2.0 + a2 * pw * (1.3 - 0.3 * e);
                        var ccy = H / 2.0 + b2 * ph * (1.3 - 0.3 * e);
                        env.Line(new[]
                        {
                            new[] { ccx - a2 * ll, ccy }, new[] { ccx, ccy }, new[] { ccx, ccy - b2 * ll },
                        }, accC, lw, @out, false);
                    }
                }
                else if (shot == "crop")
                {
                    it["size"] = Math.Min(JLayouts.FitSize(t, font, W * 1.02, H * 0.7, new JObject { ["track"] = 0.0 }), H * 0.66);
                    it["align"] = "left";
                    it["track"] = 0.0;
                    var m = JLayouts.Measure(it);
                    it["x"] = W * 0.97 - m.W;
                    it["y"] = H / 2.0 + H * 0.04;
                }
                else if (shot == "band")
                {
                    it["size"] = Math.Min(JLayouts.FitSize(t, font, W * 0.8, H * 0.24, new JObject { ["track"] = 0.06 }), H * 0.22);
                    it["track"] = 0.06;
                    var bh = JLayouts.D(it, "size") * 1.55;
                    var e = J.E.OutExpo(J.Clamp(dt / 0.18));
                    env.Rect(0, H / 2.0 - bh / 2 * e, W, bh * e, sc.Str("ink"), @out, false);
                    it["color"] = OnCol(sc, sc.Str("ink"));
                    it["plain"] = true;
                }
                else
                {
                    it["size"] = Math.Min(JLayouts.FitSize(t, font, W * 0.72, H * 0.4, new JObject { ["track"] = 0.02 }), H * 0.34);
                    it["rot"] = -8 * JLayouts.D(Pm, "side");
                    it["color"] = accC;
                }
                it["size"] = JLayouts.D(it, "size") * punch;
                env.Body = JLayouts.MainDraw(env, it);
                return;
            }
            // 收尾镜头：整行干净排版
            var f = FlowUnits(units, fontB, W * 0.84, H * (port ? 0.42 : 0.34), maxSize: H * 0.16, track: 0.04);
            var left = JLayouts.S(Pm, "fin") == "left";
            var ox = left ? W * 0.08 + f.W / 2 : W / 2.0;
            var dtF = lt - tF;
            var punchF = 1 + 0.06 * Math.Exp(-dtF * 14);
            BBox? bb = null;
            for (var i = 0; i < units.Length; i++)
            {
                var p = f.Pos[i];
                bb = UB(bb, JLayouts.MainDraw(env, new JObject
                {
                    ["text"] = units[i],
                    ["font"] = fontB,
                    ["size"] = f.Size * punchF,
                    ["x"] = ox + p.X * punchF,
                    ["y"] = H / 2.0 + p.Y * punchF,
                    ["track"] = 0.04,
                    ["color"] = sc.Str("fg"),
                    ["mi"] = MiAt(env, tF + i * 0.03),
                }));
            }
            if (bb != null)
            {
                var e = J.E.OutExpo(J.Clamp(dtF / 0.35)) * @out;
                var lw = Math.Max(2, u * 0.004);
                env.Rect(left ? W * 0.08 : W / 2.0 - f.W / 2, bb.Y1 + f.Size * 0.25, f.W * e, lw, accC, 1, false);
            }
            env.Body = bb;
        },
    });

    // ---- ループ軌道（JS L1055-1087 的 pathCache/loopPath/pathAt） ----

    private sealed class LoopPathData
    {
        public double[][] Pts = System.Array.Empty<double[]>();
        public double[] L = System.Array.Empty<double>();
        public double Tot;
        public double ST;
    }

    private static readonly Dictionary<string, LoopPathData> PathCache = new();

    private static LoopPathData LoopPath(double W, double H, double xT, double yL, double R, int side)
    {
        var key = string.Join(":", W, H, (int)xT, (int)yL, (int)R, side);
        if (PathCache.TryGetValue(key, out var hit)) return hit;
        var pts = new List<double[]> { new[] { -W * 0.9, yL }, new[] { xT, yL } };
        const int n = 72;
        // 在 (xT, yL) 与直线相切的一整圈：side 1 从上方绕回，side -1 从下方
        for (var i = 1; i <= n; i++)
        {
            var a = i / (double)n * J.TAU;
            pts.Add(new[] { xT + Math.Sin(a) * R, yL - side * (1 - Math.Cos(a)) * R });
        }
        pts.Add(new[] { W * 2.2, yL });
        var lArr = new List<double> { 0 };
        for (var i = 1; i < pts.Count; i++)
            lArr.Add(lArr[i - 1] + Math.Sqrt(
                Math.Pow(pts[i][0] - pts[i - 1][0], 2) + Math.Pow(pts[i][1] - pts[i - 1][1], 2)));
        var data = new LoopPathData
        {
            Pts = pts.ToArray(),
            L = lArr.ToArray(),
            Tot = lArr[^1],
            ST = lArr[1],
        };
        if (PathCache.Count > 60) PathCache.Clear();
        PathCache[key] = data;
        return data;
    }

    private static double[] PathAt(LoopPathData p0, double s)
    {
        var pts = p0.Pts;
        var l = p0.L;
        s = J.Clamp(s, 0, p0.Tot - 0.01);
        int lo = 0, hi = l.Length - 1;
        while (hi - lo > 1)
        {
            var m = (lo + hi) >> 1;
            if (l[m] <= s) lo = m;
            else hi = m;
        }
        var a = pts[lo];
        var b = pts[hi];
        var k = (s - l[lo]) / Math.Max(1e-6, l[hi] - l[lo]);
        return new[]
        {
            J.Lerp(a[0], b[0], k), J.Lerp(a[1], b[1], k),
            Math.Atan2(b[1] - a[1], b[0] - a[0]) / J.DEG,
        };
    }

    /// <summary>ctx.setLineDash 对位（Env.Line 无 dash 参数）：本文件内的虚线描边。</summary>
    private static void DashedLine(Env env, double[][] pts, string? c, double lw, double a, double[] dash)
    {
        if (c == null || a <= 0 || pts.Length < 2) return;
        using var path = new SKPath();
        path.MoveTo((float)pts[0][0], (float)pts[0][1]);
        for (var i = 1; i < pts.Length; i++) path.LineTo((float)pts[i][0], (float)pts[i][1]);
        using var pe = SKPathEffect.CreateDash(dash.Select(v => (float)v).ToArray(), 0);
        using var p = new SKPaint
        {
            IsAntialias = true,
            IsStroke = true,
            StrokeWidth = (float)lw,
            Color = Env.SkColor(c, a),
            PathEffect = pe,
            BlendMode = env.Blend,
        };
        env.Ctx.DrawPath(path, p);
    }

    // ---------------- knPathRide（JS L1089） ----------------

    private static void KnPathRide() => Reg0("knPathRide", new LayoutDef
    {
        Name = "ループ軌道",
        Tags = new[] { "pop", "graphic" },
        W = 0.8,
        Portrait = 0.4,
        Fits = n => n >= 2 && n <= 14,
        EnterBias = new JObject { ["cut"] = 3, ["blur"] = 0.6, ["pop"] = 0.3, ["slice"] = 0.2, ["wipe"] = 0.2 },
        Plan = (rng, _, st) => new JObject
        {
            // ae: 'wave'
            ["font"] = rng.Pick(FontsOf(st, "display")),
            ["side"] = rng.Pick(new[] { 1, 1, -1 }),
            ["rail"] = rng.Pick(new[] { "dash", "line", "dots" }),
            ["acc"] = rng.Chance(0.5),
        },
        Draw = env =>
        {
            var W = env.W;
            var H = env.H;
            var sc = env.Sc!;
            var c = env.Cut!;
            var Pm = env.P;
            var lt = env.Lt;
            var u = U(env);
            var port = IsPort(env);
            var font = JLayouts.S(Pm, "font")!;
            var t0 = Flat(c.Text);
            var chars = JLayouts.CodePoints(t0);
            var size = Math.Min(
                JLayouts.FitSize(t0, font, W * (port ? 0.86 : 0.72), H * 0.2, new JObject { ["track"] = 0.04 }), H * 0.17);
            var adv = chars.Select(ch => JLayouts.MetricsAdv(font, ch) * size * 1.04).ToList();
            var lTot = adv.Sum();
            var offs = new double[chars.Count];
            double acc0 = 0;
            for (var i = 0; i < adv.Count; i++)
            {
                offs[i] = acc0 + adv[i] / 2;
                acc0 += adv[i];
            }
            var side = (int)JLayouts.D(Pm, "side");
            var r = J.Clamp(lTot / J.TAU * 1.15, size * 1.5, Math.Min(H * 0.28, W * 0.3));
            var yL = H / 2.0 + side * r * (port ? 0.5 : 0.6);
            var xT = Math.Max(W * 0.06 + r * 0.2, W / 2.0 - lTot / 2 - size * 0.4);
            var p0 = LoopPath(W, H, xT, yL, r, side);
            var sEnd = p0.ST + J.TAU * r + (W / 2.0 + lTot / 2 - xT);
            var sStart = p0.ST - (xT + W * 0.05);
            var t = J.Clamp(c.Dur * 0.55, 0.7, 1.8);
            var q = J.Clamp(lt / t);
            var sHead = J.Lerp(sStart, sEnd, 1 - Math.Pow(1 - q, 2.6));
            sHead += J.E.InCubic(env.POut) * W * 1.3;
            var @out = Tout(env);
            var accC = AccOn(sc);
            var accOn = JLayouts.Truthy(Pm["acc"]);
            // 轨道
            var ra = J.E.OutCubic(J.Clamp(lt / 0.3)) * @out * (1 - 0.6 * J.Smooth(t, t + 0.5, lt));
            if (ra > 0.01)
            {
                var lw = Math.Max(1.2, u * 0.0022);
                var pts = p0.Pts.Where(p => p[0] > -W * 0.1 && p[0] < W * 1.1).ToList();
                var rail = JLayouts.S(Pm, "rail");
                if (rail == "dots")
                {
                    for (var i = 0; i < pts.Count; i += 3)
                        env.Circle(pts[i][0], pts[i][1], lw * 1.3, sc.Str("sub"), null, 0, ra * 0.7, false);
                }
                else
                {
                    if (rail == "dash")
                        DashedLine(env, pts.ToArray(), sc.Str("sub"), lw, ra * 0.85, new[] { lw * 5, lw * 4 });
                    else
                        env.Line(pts.ToArray(), sc.Str("sub"), lw, ra * 0.85, false);
                }
            }
            BBox? bb = null;
            for (var i = 0; i < chars.Count; i++)
            {
                var ch = chars[i];
                if (ch == " ") continue;
                var sg = sHead - (lTot - offs[i]);
                var pa = PathAt(p0, sg);
                if (pa[0] < -size || pa[0] > W + size) continue;
                var it = new JObject
                {
                    ["text"] = ch,
                    ["font"] = font,
                    ["size"] = size,
                    ["x"] = pa[0],
                    ["y"] = pa[1],
                    ["rot"] = pa[2],
                    ["color"] = accOn && i == chars.Count - 1 ? accC : sc.Str("fg"),
                    ["mi"] = i * 0.2,
                };
                bb = UB(bb, JLayouts.MainDraw(env, it));
            }
            env.Body = bb ?? Box(W * 0.3, H * 0.4, W * 0.7, H * 0.6);
        },
    });

    // ---------------- knGearWords（JS L1155） ----------------

    private static void KnGearWords() => Reg0("knGearWords", new LayoutDef
    {
        Name = "歯車",
        Tags = new[] { "pop", "graphic" },
        W = 0.7,
        Treat = "safe",
        Fits = n => n >= 2 && n <= 16,
        EnterBias = new JObject { ["cut"] = 3, ["pop"] = 0.5, ["blur"] = 0.4, ["slice"] = 0.1, ["wipe"] = 0.1 },
        Plan = (rng, _, st) => new JObject
        {
            // ae: 'circleWords'
            ["font"] = rng.Pick(FontsOf(st, "display")),
            ["dir"] = rng.Pick(new[] { 1, -1 }),
            ["fillMode"] = rng.Pick(new[] { "alt", "alt", "ink", "ring" }),
        },
        Draw = env =>
        {
            var W = env.W;
            var H = env.H;
            var sc = env.Sc!;
            var c = env.Cut!;
            var Pm = env.P;
            var lt = env.Lt;
            var port = IsPort(env);
            var font = JLayouts.S(Pm, "font")!;
            var units = UnitsOf(c, port ? 4 : 5, Math.Min(port ? 4 : 5, Math.Max(2, (int)Math.Ceiling(Gcount(c.Text) / 4.0))));
            var n = units.Length;
            var ts = Onsets(env, n, frac: 0.45, gap: 0.42);
            var txt = units.Select(t =>
                !HasLatin(t) && Gcount(t) > 3 ? JLayouts.SplitLines(Strip(t), (int)Math.Ceiling(Gcount(t) / 2.0)) : t).ToArray();
            var rs0 = txt.Select(t =>
            {
                var m = Meas(t, font, 1, new JObject { ["track"] = 0.02, ["lead"] = 1.05 });
                return Math.Sqrt(m.W * m.W + m.H * m.H) / 2 * 1.12 + 0.18;
            }).ToArray();
            var med = rs0.OrderBy(v => v).ElementAt(n / 2);
            var rs = rs0.Select(r0 => J.Clamp(r0, med * 0.75, med * 1.3)).ToArray();
            var tk = rs0.Select((r0, i) => Math.Min(1, rs[i] / r0)).ToArray();
            // 齿轮横排相切（横屏）或竖向之字形（竖屏）
            const double gap = 0.1;
            var tot = rs.Sum(r0 => r0 * 2) + gap * (n - 1);
            var mr = rs.Max();
            var k = port
                ? Math.Min(H * 0.84 / tot, W * 0.62 / (mr * 2))
                : Math.Min(Math.Min(W * 0.9 / tot, H * 0.6 / (mr * 2)), H * 0.22 / 0.7);
            var cs = new double[n][];
            double accPos = -tot / 2;
            for (var i = 0; i < n; i++)
            {
                accPos += rs[i];
                var z = (i % 2 != 0 ? 1 : -1) * mr * (port ? 0.32 : 0.12);
                cs[i] = port
                    ? new[] { W / 2.0 + z * k, H / 2.0 + accPos * k }
                    : new[] { W / 2.0 + accPos * k, H / 2.0 + z * k };
                accPos += rs[i] + gap;
            }
            var @out = Tout(env);
            var dir = JLayouts.D(Pm, "dir");
            var fillMode = JLayouts.S(Pm, "fillMode");
            BBox? bb = null;
            for (var i = 0; i < n; i++)
            {
                if (lt < ts[i]) continue;
                // 每来一个词整列车转一整圈（相邻反转，新齿轮滚入）
                double ang = 0;
                for (var j = i; j < n; j++)
                    if (lt >= ts[j])
                        ang += 360 * (1 - J.E.InOutCubic(J.Clamp((lt - ts[j]) / 0.62))) * (j == i ? 1 : rs[j] / rs[i] * 0.35);
                ang *= (i % 2 != 0 ? -1 : 1) * dir;
                var pop = J.E.OutBack(J.Clamp((lt - ts[i]) / 0.3), 1.8);
                var r = rs[i] * k * pop * @out;
                var cx = cs[i][0];
                var cy = cs[i][1];
                var fill = fillMode == "ink" ? sc.Str("ink") : fillMode == "ring" ? null : (i % 2 != 0 ? sc.Str("accent") : sc.Str("ink"));
                var rimC = fill ?? AccOn(sc);
                if (r > 1)
                {
                    var nt = Math.Max(10, (int)Math.Round(J.TAU * rs[i] * 6));
                    var th = r * 0.12;
                    var pts = new List<double[]>();
                    for (var t2 = 0; t2 < nt; t2++)
                    {
                        var a0 = (t2 / (double)nt * 360 + ang) * J.DEG;
                        var w = J.TAU / nt;
                        pts.Add(new[] { cx + Math.Cos(a0 - w * 0.25) * r, cy + Math.Sin(a0 - w * 0.25) * r });
                        pts.Add(new[] { cx + Math.Cos(a0 - w * 0.15) * (r + th), cy + Math.Sin(a0 - w * 0.15) * (r + th) });
                        pts.Add(new[] { cx + Math.Cos(a0 + w * 0.15) * (r + th), cy + Math.Sin(a0 + w * 0.15) * (r + th) });
                        pts.Add(new[] { cx + Math.Cos(a0 + w * 0.25) * r, cy + Math.Sin(a0 + w * 0.25) * r });
                    }
                    env.Poly(pts.ToArray(), rimC, 1, false);
                    if (fill != null) env.Circle(cx, cy, r * 0.96, fill, null, 0, 1, false);
                    else env.Circle(cx, cy, r * 0.93, sc.Str("bg"), null, 0, 1, false);
                    env.Circle(cx, cy, r * 0.86, null, fill != null ? OnCol(sc, fill) : rimC, Math.Max(1, r * 0.02), 0.35, false);
                }
                var tc = fill != null ? OnCol(sc, fill) : sc.Str("fg");
                var it = new JObject
                {
                    ["text"] = txt[i],
                    ["font"] = font,
                    ["size"] = k * pop * tk[i],
                    ["x"] = cx,
                    ["y"] = cy,
                    ["rot"] = ang,
                    ["lead"] = 1.05,
                    ["track"] = 0.02,
                    ["color"] = tc,
                    ["mi"] = MiAt(env, ts[i]),
                };
                bb = UB(bb, JLayouts.MainDraw(env, it));
            }
            env.Body = bb;
        },
    });

    // ---------------- knCollide（JS L1235） ----------------

    private static void KnCollide() => Reg0("knCollide", new LayoutDef
    {
        Name = "正面衝突",
        Tags = new[] { "pop", "graphic", "glitch" },
        W = 0.9,
        Fits = n => n >= 2 && n <= 16,
        EnterBias = new JObject { ["cut"] = 3.5, ["pop"] = 0.3, ["blur"] = 0.4, ["slice"] = 0.2, ["wipe"] = 0.2 },
        Plan = (rng, _, st) => new JObject
        {
            // ae: 'splitHalves'
            ["font"] = rng.Pick(FontsOf(st, "display")),
            ["acc"] = rng.Pick(new[] { "A", "B", "none" }),
            ["spark"] = rng.Chance(0.8),
            ["at"] = rng.Range(0.28, 0.36),
        },
        Draw = env =>
        {
            var W = env.W;
            var H = env.H;
            var sc = env.Sc!;
            var c = env.Cut!;
            var Pm = env.P;
            var lt = env.Lt;
            var u = U(env);
            var port = IsPort(env);
            var font = JLayouts.S(Pm, "font")!;
            var all = UnitsOf(c, 8, 2);
            var lat = HasLatin(c.Text);
            // 按字数切成两半
            var best = 1;
            var bd = 1e9;
            var tot = all.Sum(Gcount);
            double acc0 = 0;
            for (var i = 1; i < all.Length; i++)
            {
                acc0 += Gcount(all[i - 1]);
                var d = Math.Abs(acc0 - tot / 2.0);
                if (d < bd)
                {
                    bd = d;
                    best = i;
                }
            }
            var aTxt = string.Join(lat ? " " : "", all.Take(best));
            var bTxt = string.Join(lat ? " " : "", all.Skip(best));
            var tC = J.Clamp(c.Dur * JLayouts.D(Pm, "at") * 0.65, 0.25, 0.55);
            var d0 = lt - tC;
            var accC = AccOn(sc);
            var @out = Tout(env);
            double size, axis;
            double[] pa, pb;
            if (!port)
            {
                var g = lat ? 0.35 : 0.14;
                var full = aTxt + (lat ? " " : "") + bTxt;
                size = Math.Min(JLayouts.FitSize(full, font, W * 0.86, H * 0.3, new JObject { ["track"] = 0.02 }), H * 0.24);
                var wa = Em(aTxt, font, new JObject { ["track"] = 0.02 }) * size;
                var wb = Em(bTxt, font, new JObject { ["track"] = 0.02 }) * size;
                var x0 = W / 2.0 - (wa + wb + g * size) / 2;
                pa = new[] { x0 + wa / 2, H / 2.0 };
                pb = new[] { x0 + wa + g * size + wb / 2, H / 2.0 };
                axis = 0;
            }
            else
            {
                size = Math.Min(Math.Min(
                    JLayouts.FitSize(aTxt, font, W * 0.86, H * 0.2, new JObject { ["track"] = 0.02 }),
                    JLayouts.FitSize(bTxt, font, W * 0.86, H * 0.2, new JObject { ["track"] = 0.02 })), W * 0.3);
                pa = new[] { W / 2.0, H / 2.0 - size * 0.62 };
                pb = new[] { W / 2.0, H / 2.0 + size * 0.62 };
                axis = 1;
            }
            // 两半从画框外起步，加速对撞
            var dMax = axis != 0
                ? Math.Max(pa[1], H - pb[1]) + size * 0.7
                : Math.Max(pa[0] + Em(aTxt, font, new JObject { ["track"] = 0.02 }) * size / 2,
                    W - pb[0] + Em(bTxt, font, new JObject { ["track"] = 0.02 }) * size / 2) + size * 0.2;
            double offA;
            var sqA = 1.0;
            if (d0 < 0)
            {
                var q = J.Clamp(lt / tC);
                offA = -dMax * (1 - Math.Pow(q, 1.6));
            }
            else
            {
                offA = -size * 0.28 * Math.Exp(-d0 * 6) * Math.Sin(d0 * 15);
                sqA = 1 - 0.22 * Math.Exp(-d0 * 14);
            }
            var jit = d0 > 0 ? Math.Exp(-d0 * 9) * size * 0.05 : 0;
            var accMode = JLayouts.S(Pm, "acc");
            var seed = unchecked((int)c.Seed);
            var step = (int)env.Step;

            BBox? Mk(string t, double[] p, int sgn, string? col)
            {
                var o = offA * sgn;
                var it = new JObject
                {
                    ["text"] = t,
                    ["font"] = font,
                    ["size"] = size,
                    ["x"] = p[0] + (axis != 0 ? 0 : o) + J.Rs(seed, step, sgn, 1) * jit,
                    ["y"] = p[1] + (axis != 0 ? o : 0) + J.Rs(seed, step, sgn, 2) * jit,
                    ["track"] = 0.02,
                    ["color"] = col,
                    ["mi"] = 0.0,
                };
                if (axis != 0) it["sy"] = sqA;
                else it["sx"] = sqA;
                return JLayouts.MainDraw(env, it);
            }

            var bb = Mk(aTxt, pa, 1, accMode == "A" ? accC : sc.Str("fg"));
            bb = UB(bb, Mk(bTxt, pb, -1, accMode == "B" ? accC : sc.Str("fg")));
            // 飞行时身后拖速度线，接触点迸火花
            var lw = Math.Max(1.5, u * 0.0028);
            if (d0 < 0 && lt > 0.02)
            {
                var q = J.Clamp(lt / tC);
                var a2 = 0.7 * q * @out;
                for (var j = 0; j < 5; j++)
                {
                    var fv = (j - 2) / 2.2 * size * 0.42;
                    var len = size * (1.2 + J.R(seed, j, 7) * 1.6);
                    if (axis == 0)
                    {
                        var xa = pa[0] + offA - Em(aTxt, font) * size / 2 - size * 0.2;
                        var xb = pb[0] - offA + Em(bTxt, font) * size / 2 + size * 0.2;
                        env.Line(new[]
                        {
                            new[] { xa - len, pa[1] + fv }, new[] { xa, pa[1] + fv },
                        }, sc.Str("sub"), lw, a2, false);
                        env.Line(new[]
                        {
                            new[] { xb, pb[1] - fv }, new[] { xb + len, pb[1] - fv },
                        }, sc.Str("sub"), lw, a2, false);
                    }
                    else
                    {
                        var ya = pa[1] + offA - size * 0.7;
                        var yb = pb[1] - offA + size * 0.7;
                        env.Line(new[]
                        {
                            new[] { pa[0] + fv * 1.6, ya - len }, new[] { pa[0] + fv * 1.6, ya },
                        }, sc.Str("sub"), lw, a2, false);
                        env.Line(new[]
                        {
                            new[] { pb[0] - fv * 1.6, yb }, new[] { pb[0] - fv * 1.6, yb + len },
                        }, sc.Str("sub"), lw, a2, false);
                    }
                }
            }
            if (JLayouts.Truthy(Pm["spark"]) && d0 > 0 && d0 < 0.45)
            {
                var a3 = (1 - d0 / 0.45) * @out;
                var cx = axis != 0 ? W / 2.0
                    : (pa[0] + Em(aTxt, font) * size / 2 + pb[0] - Em(bTxt, font) * size / 2) / 2;
                var cy = H / 2.0;
                for (var j = 0; j < 12; j++)
                {
                    var an = (j / 12.0 + J.R(seed, j, 9) * 0.05) * J.TAU;
                    var r0 = size * (0.25 + d0 * 2.2);
                    var r1 = r0 + size * 0.5 * a3;
                    env.Line(new[]
                    {
                        new[] { cx + Math.Cos(an) * r0, cy + Math.Sin(an) * r0 },
                        new[] { cx + Math.Cos(an) * r1, cy + Math.Sin(an) * r1 },
                    }, accC, lw * 1.4, a3, false);
                }
            }
            env.Body = bb;
        },
    });

    // ---------------- knTumble（JS L1337） ----------------

    private static void KnTumble() => Reg0("knTumble", new LayoutDef
    {
        Name = "箱転がし",
        Tags = new[] { "pop", "graphic" },
        W = 0.8,
        Fits = n => n >= 2 && n <= 16,
        EnterBias = new JObject { ["cut"] = 3.5, ["pop"] = 0.3, ["blur"] = 0.4, ["slice"] = 0.1, ["wipe"] = 0.1 },
        Plan = (rng, _, st) => new JObject
        {
            // ae: 'dominoes'
            ["font"] = rng.Pick(FontsOf(st, "display")),
            ["box"] = rng.Pick(new[] { "plate", "frame", "none" }),
            ["acc"] = rng.Int(0, 5),
            ["floor"] = rng.Chance(0.7),
        },
        Draw = env =>
        {
            var W = env.W;
            var H = env.H;
            var sc = env.Sc!;
            var ctx = env.Ctx;
            var c = env.Cut!;
            var Pm = env.P;
            var lt = env.Lt;
            var u = U(env);
            var port = IsPort(env);
            var units = UnitsOf(c, 5, 2);
            var n = units.Length;
            var font = JLayouts.S(Pm, "font")!;
            var f = FlowUnits(units, font, W * 0.82, H * (port ? 0.4 : 0.3),
                maxSize: H * 0.2, track: 0.02, sp: HasLatin(c.Text) ? 0.5 : 0.3, lead: 1.6);
            var size = f.Size;
            var pad = size * 0.14;
            var h = size + pad * 2;
            var ts = Onsets(env, n, frac: 0.42, gap: 0.36);
            const double t = 0.5;
            var @out = Tout(env);
            var accC = AccOn(sc);
            BBox? bb = null;
            var lw = Math.Max(1.5, u * 0.0025);
            var acc = (int)JLayouts.D(Pm, "acc");
            var boxMode = JLayouts.S(Pm, "box");
            for (var i = 0; i < n; i++)
            {
                var p = f.Pos[i];
                var w = p.W + pad * 2;
                var @base = H / 2.0 + p.Y + h / 2;
                var xF = W / 2.0 + p.X - w / 2;
                if (JLayouts.Truthy(Pm["floor"]) && (i == 0 || f.Pos[i - 1].Li != p.Li))
                {
                    var row = f.Pos.Where(q2 => q2.Li == p.Li).ToList();
                    var rx0 = W / 2.0 + row[0].X - row[0].W / 2 - pad * 3;
                    var rx1 = W / 2.0 + row[^1].X + row[^1].W / 2 + pad * 3;
                    var e0 = J.E.OutExpo(J.Clamp(lt / 0.4)) * @out;
                    env.Line(new[]
                    {
                        new[] { rx1 - (rx1 - rx0) * e0, @base + lw }, new[] { rx1, @base + lw },
                    }, sc.Str("sub"), lw, 0.8, false);
                }
                if (lt < ts[i]) continue;
                // 从右侧绕左下角滚入：四分之一圈一步，落点宽高交替
                var m = Math.Min(3, Math.Max(1, (int)Math.Ceiling((W - xF) / (2 * (w + h)))));
                var steps = 4 * m;
                var tm = t * (0.7 + 0.3 * m);
                var q = J.Clamp((lt - ts[i]) / tm) * steps;
                var j = Math.Min(steps - 1, (int)Math.Floor(q));
                var fv = q >= steps ? 1 : q - j;
                var xL = xF + m * 2 * (w + h);
                for (var s2 = 0; s2 < j; s2++) xL -= s2 % 2 != 0 ? w : h;
                var fw = j % 2 != 0 ? h : w;
                var fh = j % 2 != 0 ? w : h;
                var ph = -90 * J.E.InOutSine(fv);
                var rv = RotV(fw / 2, -fh / 2, ph);
                var cx = xL + rv[0];
                var cy = @base + rv[1];
                var rot = -90 * j + ph;
                var done = q >= steps;
                var land = done ? lt - ts[i] - tm : -1;
                var sq = land >= 0 ? Math.Exp(-land * 14) * 0.1 : 0;
                if (boxMode != "none")
                {
                    ctx.Save();
                    ctx.Translate((float)cx, (float)(cy + sq * h * 0.5));
                    ctx.RotateDegrees((float)rot);
                    ctx.Scale((float)(1 + sq * 0.5), (float)(1 - sq));
                    if (boxMode == "plate")
                        env.Rect(-w / 2, -h / 2, w, h, i == acc % n ? accC : sc.Str("ink"), @out, false);
                    else
                        env.RRect(-w / 2, -h / 2, w, h, 2, null, @out, false, sc.Str("fg"), lw);
                    ctx.Restore();
                }
                var plate = boxMode == "plate";
                var it = new JObject
                {
                    ["text"] = units[i],
                    ["font"] = font,
                    ["size"] = size,
                    ["x"] = cx,
                    ["y"] = cy + sq * h * 0.5,
                    ["rot"] = rot,
                    ["sx"] = 1 + sq * 0.5,
                    ["sy"] = 1 - sq,
                    ["track"] = 0.02,
                    ["color"] = plate
                        ? OnCol(sc, i == acc % n ? accC : sc.Str("ink"))
                        : i == acc % n ? accC : sc.Str("fg"),
                    ["plain"] = plate,
                    ["mi"] = MiAt(env, ts[i]),
                    ["noHold"] = !done,
                };
                bb = UB(bb, JLayouts.MainDraw(env, it));
            }
            env.Body = bb;
        },
    });

    // ---------------- knReflow（JS L1413） ----------------

    private static void KnReflow() => Reg0("knReflow", new LayoutDef
    {
        Name = "縦から横へ",
        Tags = new[] { "editorial", "graphic", "emotional" },
        W = 0.9,
        Fits = n => n >= 2 && n <= 16,
        EnterBias = new JObject { ["cut"] = 2.5, ["blur"] = 1, ["pop"] = 0.4, ["slice"] = 0.2, ["wipe"] = 0.3 },
        Plan = (rng, ctx, st) =>
        {
            // ae: 'halfVertical'
            var port = ctx.H > ctx.W * 1.08;
            var lat = Regex.IsMatch(ctx.Text ?? "", "[A-Za-z]");
            return new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "serif", "display")),
                ["dir"] = !lat && port && rng.Chance(0.5) ? "h2v" : "v2h",
                ["swirl"] = rng.Pick(new[] { 1, -1 }),
                ["at"] = rng.Range(0.36, 0.46),
            };
        },
        Draw = env =>
        {
            var W = env.W;
            var H = env.H;
            var sc = env.Sc!;
            var c = env.Cut!;
            var Pm = env.P;
            var lt = env.Lt;
            var port = IsPort(env);
            var font = JLayouts.S(Pm, "font")!;
            var lat = HasLatin(c.Text);
            var t0 = lat ? Flat(c.Text) : Strip(c.Text);
            var chars = JLayouts.CodePoints(t0);
            var n = chars.Count;
            var vCols = n <= (port ? 7 : 5) ? 1 : n <= 12 ? 2 : 3;
            var per = (int)Math.Ceiling(n / (double)vCols);
            var vt = new List<string>();
            for (var i = 0; i < n; i += per)
                vt.Add(string.Concat(chars.Skip(i).Take(per)));
            var vtext = string.Join("\n", vt);
            var htext = JLayouts.SplitLines(t0, lat ? (port ? 13 : 20) : (port ? 7 : 14));
            var sv = Math.Min(
                JLayouts.FitSize(vtext, font, W * 0.5, H * 0.8,
                    new JObject { ["vertical"] = true, ["lead"] = 1.3, ["track"] = 0.04 }), W * 0.3);
            var sh = Math.Min(
                JLayouts.FitSize(htext, font, W * 0.84, H * 0.4, new JObject { ["lead"] = 1.25, ["track"] = 0.04 }), H * 0.24);
            var lv = JText.LayoutText(new JObject
            {
                ["text"] = vtext, ["font"] = font, ["size"] = sv,
                ["vertical"] = true, ["lead"] = 1.3, ["track"] = 0.04,
            }).Cells.Where(g => g.Ch.Trim().Length > 0).ToList();
            var lh = JText.LayoutText(new JObject
            {
                ["text"] = htext, ["font"] = font, ["size"] = sh, ["lead"] = 1.25, ["track"] = 0.04,
            }).Cells.Where(g => g.Ch.Trim().Length > 0).ToList();
            var h2v = JLayouts.S(Pm, "dir") == "h2v";
            var aL = h2v ? lh : lv;
            var bL = h2v ? lv : lh;
            var sA = h2v ? sh : sv;
            var sB = h2v ? sv : sh;
            var tR = J.Clamp(c.Dur * JLayouts.D(Pm, "at"), 0.5, 1.5);
            var m = Math.Min(aL.Count, bL.Count);
            var swirl = JLayouts.D(Pm, "swirl");
            BBox? bb = null;
            for (var i = 0; i < m; i++)
            {
                var a = aL[i];
                var b = bL[i];
                var q = J.E.InOutCubic(J.Clamp((lt - tR - i * Math.Min(0.05, 0.5 / m)) / 0.5));
                var ax = W / 2.0 + a.X + a.Vx;
                var ay = H / 2.0 + a.Y + a.Vy;
                var bx = W / 2.0 + b.X + b.Vx;
                var by = H / 2.0 + b.Y + b.Vy;
                // 沿一条侧向鼓起的弧线：字形打着旋从竖排换到横排
                var mx = (ax + bx) / 2;
                var my = (ay + by) / 2;
                var dx = bx - ax;
                var dy = by - ay;
                var ll = Math.Sqrt(dx * dx + dy * dy);
                if (ll == 0) ll = 1;
                var kk = swirl * 0.35 * (i % 2 != 0 ? 1 : 0.6);
                var cx = mx - dy / ll * ll * kk;
                var cy = my + dx / ll * ll * kk;
                var x = (1 - q) * (1 - q) * ax + 2 * (1 - q) * q * cx + q * q * bx;
                var y = (1 - q) * (1 - q) * ay + 2 * (1 - q) * q * cy + q * q * by;
                var r0 = a.R90 ? 90 : 0;
                var r1 = b.R90 ? 90 : 0;
                var it = new JObject
                {
                    ["text"] = a.Ch,
                    ["font"] = font,
                    ["size"] = J.Lerp(sA * (a.Fs != 0 ? a.Fs : 1), sB * (b.Fs != 0 ? b.Fs : 1), q),
                    ["x"] = x,
                    ["y"] = y,
                    ["rot"] = J.Lerp(r0, r1, q) + swirl * 180 * BellK(q) * 0.25,
                    ["color"] = sc.Str("fg"),
                    ["mi"] = i * 0.4,
                };
                bb = UB(bb, JLayouts.MainDraw(env, it));
            }
            env.Body = bb;
        },
    });

    // ---------------- knPadGrid（JS L1489） ----------------

    private static void KnPadGrid() => Reg0("knPadGrid", new LayoutDef
    {
        Name = "パッド",
        Tags = new[] { "pop", "graphic", "glitch" },
        W = 0.8,
        Fits = n => n >= 2 && n <= 18,
        EnterBias = new JObject { ["cut"] = 3, ["pop"] = 0.8, ["blur"] = 0.4, ["slice"] = 0.2, ["wipe"] = 0.2 },
        Plan = (rng, _, st) => new JObject
        {
            // ae: 'gridCells'
            ["font"] = rng.Pick(FontsOf(st, "display")),
            ["round"] = rng.Chance(0.5),
            ["flash"] = rng.Pick(new[] { "accent", "accent", "ink" }),
            ["beat"] = rng.Chance(0.7),
        },
        Draw = env =>
        {
            var W = env.W;
            var H = env.H;
            var sc = env.Sc!;
            var c = env.Cut!;
            var Pm = env.P;
            var lt = env.Lt;
            var u = U(env);
            var port = IsPort(env);
            var font = JLayouts.S(Pm, "font")!;
            var units = UnitsOf(c, 6, Math.Min(6, Math.Max(2, (int)Math.Ceiling(Gcount(c.Text) / 4.0))));
            var n = units.Length;
            var cols = port ? (n <= 3 ? 1 : 2) : n <= 3 ? n : n == 4 ? 2 : 3;
            var rows = (int)Math.Ceiling(n / (double)cols);
            var gw = W * (port ? 0.84 : 0.86);
            var gh = H * (port ? 0.62 : 0.72);
            var g = u * 0.02;
            var cw = (gw - g * (cols - 1)) / cols;
            var chh = Math.Min((gh - g * (rows - 1)) / rows, cw * (port ? 0.8 : 0.75));
            var x0 = W / 2.0 - gw / 2;
            var y0 = H / 2.0 - (chh * rows + g * (rows - 1)) / 2;
            var ts = Onsets(env, n, frac: 0.45, gap: 0.34);
            var @out = Tout(env);
            var flashC = JLayouts.S(Pm, "flash") == "ink" ? sc.Str("ink") : AccOn(sc);
            var lw = Math.Max(1.5, u * 0.003);
            var round = JLayouts.Truthy(Pm["round"]);
            var beatOn = JLayouts.Truthy(Pm["beat"]);
            var beat = BeatOf(env);
            BBox? bb = null;
            var sizes = units.Select(t => Math.Min(
                JLayouts.FitSize(t, font, cw * 0.8, chh * 0.62, new JObject { ["track"] = 0.02 }), chh * 0.5)).ToArray();
            var sz = sizes.Min() * 1.25;
            for (var i = 0; i < cols * rows; i++)
            {
                var r = i / cols;
                var q = i % cols;
                var x = x0 + q * (cw + g);
                var y = y0 + r * (chh + g);
                var e = J.E.OutBack(J.Clamp((lt - i * 0.04) / 0.3), 1.4) * @out;
                if (e <= 0.01) continue;
                var ix = x + cw / 2 * (1 - e);
                var iy = y + chh / 2 * (1 - e);
                env.RRect(ix, iy, cw * e, chh * e, round ? chh * 0.12 : 0, null, 1, false, sc.Str("sub"), lw);
                if (i >= n || lt < ts[i]) continue;
                // 命中：格子全亮再衰减成微光；节拍每次只重触发一格
                var fl = Math.Exp(-(lt - ts[i]) * 6);
                if (beatOn && beat.Has && lt > ts[n - 1] + 0.4 && beat.Index % n == i)
                    fl = Math.Max(fl, 0.55 * Math.Exp(-beat.Since * 7));
                var pf = 0.1 + 0.9 * fl;
                env.RRect(x + lw, y + lw, cw - lw * 2, chh - lw * 2, round ? chh * 0.12 : 0, flashC, pf * @out, false);
                var tc = pf > 0.5 ? OnCol(sc, flashC) : sc.Str("fg");
                var it = new JObject
                {
                    ["text"] = units[i],
                    ["font"] = font,
                    ["size"] = Math.Min(sizes[i], sz) * (1 + 0.12 * Math.Exp(-(lt - ts[i]) * 12)),
                    ["x"] = x + cw / 2,
                    ["y"] = y + chh / 2,
                    ["track"] = 0.02,
                    ["color"] = tc,
                    ["mi"] = MiAt(env, ts[i]),
                };
                bb = UB(bb, JLayouts.MainDraw(env, it));
            }
            env.Body = bb;
        },
    });
}
