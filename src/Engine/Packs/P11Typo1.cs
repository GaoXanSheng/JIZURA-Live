// 移植自 frontend/src/engine/11p_typo1.js（文字 PV 版式第 1 包，18 条 layout）。
//
// 契约与 JLayouts.cs / P11LayoutsA–D 相同：fits(n) 筛台词；plan(rng, cut, st) 抽参数存进
// cut.params（键名是数据契约，planner/锁定行/AE 导出共用，一字不改）；Draw(env) 绘制并写回
// env.Body（JS render(env) 的返回值）。RNG 消费顺序逐行对位（rng.pick/chance/range/int 的
// 调用次序就是画面轨迹）。plan 全部不消费 cut（与 JS 一致，rng 只在字面量抽取序里流动）。
//
// 源文件 reg(key, def) 的 Object.assign({set:'typo'}, def)：`set`（11q_sets 的手法集归属）与
// `ae`（AE 导出模板名）在 LayoutDef 上没有对应属性，未注册——TODO(Sets/11_export) 移植时随
// 该两层补（P11Enter 对 MoveDef 的 ae 同约定）。
//
// 本包私有 helper（JS L9-119 顶层 const + L745 groupLines）见「本包 helper」区；Env 已有
// 助手（rect/line/circle/rrect/poly + DrawItem）直接复用。跨文件符号对位：
// J.fontsOf→JLayouts.FontsOf、J.measure→JLayouts.Measure、J.fitSize→JLayouts.FitSize、
// J.mainDraw→JLayouts.MainDraw、J.unionBB→JLayouts.UnionBb、J.splitLines→JLayouts.SplitLines、
// J.glyphCount→JLayouts.GlyphCount、J.segments→Planner.Segments、J.romaji/fmtTime/isPunct/
// isKanji/isHira/isKata/isSmallKana/clamp/lerp/mix/contrast/lum→J.*；env.draw→本地 DrawIt。
//
// Env 缺口（本文件内补，不改 Env.cs）：JS ctx.setLineDash（tyIndexTable 的虚线引导线）Env.Line
// 无对应参数 → 本地 DashLine；tySplitType 的多矩形联合 clip → 本地 ClipUnion（Canvas 多 rect
// 一次 clip 是 nonzero 并集，SKCanvas.ClipRect 连续调用是交集，不等价）。

using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace JizuraLive.Engine;

public static class P11Typo1
{
    private const string P = "typo1";
    private static int _installed;

    private static void Reg0(string key, LayoutDef def)
    {
        def.Set = "typo"; // JS reg 助手：Object.assign({set: SET('typo')}, def)
        Reg.Register("layout", key, def, P);
    }

    // ================= 本包 helper（JS L9-119 + L745 groupLines 逐条对位） =================

    /// <summary>clean(t)：去掉所有空白。JS L9。</summary>
    private static string Clean(string? t) => Regex.Replace(t ?? "", @"\s+", "");

    /// <summary>slotsOf(t)：逐字槽位，连续空白压成单个 ' '（拉丁歌词保留词间空格）。JS L11。</summary>
    private static List<string> SlotsOf(string? t) =>
        JLayouts.CodePoints(Regex.Replace((t ?? "").Trim(), @"[\s　]+", " "));

    private static bool IsSp(string c) => c is " " or "　";

    private static string[] FontsOf(StyleDef st, params string[] roles) => JLayouts.FontsOf(st, roles);

    /// <summary>(env.st.fonts.mono && env.st.fonts.mono[0]) || 'mono'。JS L14。</summary>
    private static string MonoF(Env env) => JLayouts.Font0(env.St, "mono") ?? "mono";

    /// <summary>(env.st.fonts.body && env.st.fonts.body[0]) || 'gothic_med'。JS L15。</summary>
    private static string BodyF(Env env) => JLayouts.Font0(env.St, "body") ?? "gothic_med";

    /// <summary>inE(env, d, delay)：outExpo 进场。JS L16。</summary>
    private static double InE(Env env, double d = 0.35, double delay = 0) =>
        J.E.OutExpo(J.Clamp((env.Lt - delay) / d));

    /// <summary>outK(env)：退场剩余量 1 - inCubic(pOut)。JS L17。</summary>
    private static double OutK(Env env) => 1 - J.E.InCubic(env.POut);

    private static string Pad2(double n) => ((long)n).ToString(CultureInfo.InvariantCulture).PadLeft(2, '0');

    /// <summary>hair(env)：发丝线宽。JS L19。</summary>
    private static double Hair(Env env) => Math.Max(1, Math.Min(env.W, env.H) * 0.0014);

    private static BBox BbRect(double x0, double y0, double x1, double y1) => new()
    {
        X0 = x0,
        Y0 = y0,
        X1 = x1,
        Y1 = y1,
        Cx = (x0 + x1) / 2,
        Cy = (y0 + y1) / 2,
        Boxes = new List<JObject>(),
    };

    private static BBox? U(BBox? a, BBox? b) => JLayouts.UnionBb(a, b);

    /// <summary>romaOf(t)：含假名才转大写罗马字。JS L22-27。</summary>
    private static string? RomaOf(string? t)
    {
        var c = Clean(t);
        if (!Regex.IsMatch(c, "[ぁ-ヿ]")) return null;
        var r = J.Romaji(c);
        return string.IsNullOrEmpty(r) ? null : r.ToUpperInvariant();
    }

    /// <summary>splitL(t, per)：J.splitLines 变体——不允许某行只剩标点。JS L29-36。</summary>
    private static string SplitL(string t, int per)
    {
        var outL = new List<string>();
        foreach (var l in JLayouts.SplitLines(t, Math.Max(1, per)).Split('\n'))
        {
            if (outL.Count > 0 && JLayouts.CodePoints(l).All(c => J.IsPunct(c[0]) || c == " ")) outL[^1] += l;
            else outL.Add(l);
        }
        return string.Join("\n", outL);
    }

    /// <summary>mainLines(text, W, H, perL, perP)：主文本折行，竖屏行更短。JS L38-43。</summary>
    private static string MainLines(string? text, double W, double H, int perL = 11, int perP = 5)
    {
        var t = (text ?? "").Trim();
        var n = JLayouts.GlyphCount(t);
        var per = W < H ? perP : perL;
        if (n <= per) return t;
        return SplitL(t, (int)Math.Ceiling(n / Math.Ceiling(n / (double)per)));
    }

    /// <summary>排好版的字形中心，设计空间、未含旋转。JS L45-61。</summary>
    private sealed class GlyphPt
    {
        public string Ch = "";
        public double X, Y, W, H;
        public int Li, Ci, I;
    }

    private static List<GlyphPt> GlyphPts(JObject it)
    {
        var lay = JText.LayoutText(it);
        var sx = JLayouts.D(it, "sx");
        if (sx == 0) sx = 1;
        var sy = JLayouts.D(it, "sy");
        if (sy == 0) sy = 1;
        var x = JLayouts.D(it, "x");
        var y = JLayouts.D(it, "y");
        var list = new List<GlyphPt>();
        foreach (var g in lay.Cells)
        {
            if (IsSp(g.Ch)) continue;
            list.Add(new GlyphPt
            {
                Ch = g.Ch,
                X = x + g.X * sx,
                Y = y + g.Y * sy,
                W = g.W * sx,
                H = g.H * sy,
                Li = g.Li,
                Ci = g.Ci,
                I = list.Count,
            });
        }
        return list;
    }

    /// <summary>关键字下标：最长汉字连串的首字，没有就取第一个普通字。JS L63-86。</summary>
    private static int KeyIndex(List<string> arr, string mode)
    {
        bool Ok(string? c) => !string.IsNullOrEmpty(c) && !IsSp(c) && !J.IsPunct(c[0]) && !J.IsSmallKana(c[0]);
        if (mode == "mid")
        {
            var mid = (arr.Count - 1) / 2;
            for (var d = 0; d < arr.Count; d++)
            {
                if (mid + d < arr.Count && Ok(arr[mid + d])) return mid + d;
                if (mid - d >= 0 && Ok(arr[mid - d])) return mid - d;
            }
        }
        var best = -1;
        var bl = 0;
        for (var i = 0; i < arr.Count;)
        {
            if (!J.IsKanji(arr[i][0]))
            {
                i++;
                continue;
            }
            var j = i;
            while (j < arr.Count && J.IsKanji(arr[j][0])) j++;
            if (j - i > bl)
            {
                bl = j - i;
                best = i;
            }
            i = j; // JS for 循环体尾的 i++：汉字连串后从断点字继续（断点非汉字，等价）
        }
        if (best >= 0) return best;
        for (var i = 0; i < arr.Count; i++)
            if (Ok(arr[i]))
                return i;
        return 0;
    }

    /// <summary>底板上对比最好的方案文字色。JS L88-99。</summary>
    private static string OnCol(Scheme sc, string fill)
    {
        string? best = null;
        var bv = 0.0;
        foreach (var c in new[] { sc.Str("bg"), sc.Str("fg"), sc.Str("ink"), sc.Str("accent"), sc.Str("sub") })
        {
            if (string.IsNullOrEmpty(c) || c == fill) continue;
            var k = J.Contrast(c, fill);
            if (k > bv)
            {
                bv = k;
                best = c;
            }
        }
        return bv >= 2.4 && best != null ? best : J.Lum(fill) > 0.5 ? "#111111" : "#FFFFFF";
    }

    /// <summary>plateCol(sc, pref)：从配色里挑在背景上可读的。JS L100-103。</summary>
    private static string PlateCol(Scheme sc, params string[] pref)
    {
        foreach (var c in pref)
            if (!string.IsNullOrEmpty(c) && J.Contrast(c, sc.Str("bg") ?? "#000000") >= 1.6)
                return c;
        return sc.Str("fg") ?? "#FFFFFF";
    }

    /// <summary>accentOn(sc)：accent 在背景上可读就用 accent。JS L104。</summary>
    private static string AccentOn(Scheme sc) =>
        J.Contrast(sc.Str("accent") ?? "#FF3B30", sc.Str("bg") ?? "#000000") >= 1.8
            ? sc.Str("accent") ?? "#FF3B30"
            : sc.Str("fg") ?? "#FFFFFF";

    /// <summary>label：小注记标签（只走主通道 env.draw）。JS L106-117（o.extra 无调用方，未移植）。</summary>
    private static void Label(Env env, string text, double x, double y,
        double? size = null, string? font = null, string? align = null, double? track = null,
        string? color = null, double? alpha = null)
    {
        var it = new JObject
        {
            ["text"] = text,
            ["font"] = font ?? MonoF(env),
            ["size"] = size ?? J.Clamp(Math.Min(env.W, env.H) * 0.018, 11, 22),
            ["x"] = x,
            ["y"] = y,
            ["align"] = align ?? "left",
            ["track"] = track ?? 0.12,
            ["color"] = color ?? env.Sc?.Str("sub") ?? "#9A9A9A",
            ["alpha"] = alpha ?? 1,
            ["ghost"] = false,
        };
        DrawIt(env, it);
    }

    /// <summary>isLatinT(t)：纯拉丁文本（含拉丁但含 CJK/全角的算否）。JS L118。</summary>
    private static bool IsLatinT(string? t) =>
        t != null && Regex.IsMatch(t, "[A-Za-z]") && !Regex.IsMatch(t, "[\u3040-\u30ff\u3400-\u9fff\uff00-\uffef]");

    /// <summary>labelSize(env)。JS L119。</summary>
    private static double LabelSize(Env env) => J.Clamp(Math.Min(env.W, env.H) * 0.018, 11, 22);

    /// <summary>groupLines(units, L)：按总字数均分 L 行。JS L745-761。</summary>
    private static List<string> GroupLines(List<string> units, int L)
    {
        var lens = units.Select(u => JLayouts.GlyphCount(u)).ToList();
        var tot = lens.Sum();
        var outL = new List<string>();
        var cur = "";
        var acc = 0;
        for (var i = 0; i < units.Count; i++)
        {
            cur += units[i];
            acc += lens[i];
            var target = tot * (double)(outL.Count + 1) / L;
            if (outL.Count < L - 1 && acc >= target - lens[i] * 0.4 && i < units.Count - 1)
            {
                outL.Add(cur.Trim());
                cur = "";
            }
        }
        if (cur.Trim().Length > 0) outL.Add(cur.Trim());
        return outL;
    }

    /// <summary>env.draw 对位：直接画一条 item（不走 mainDraw 流水线）。临时置换 env.CharFn/PieceFn，
    /// 防止上一条 mainDraw 的残留串到直画 item 上（P11LayoutsB 同约定）。</summary>
    private static BBox? DrawIt(Env env, JObject it, JAnim.CharFn? charFn = null)
    {
        var prevC = env.CharFn;
        var prevP = env.PieceFn;
        env.CharFn = charFn;
        env.PieceFn = null;
        try
        {
            return JText.DrawItem(env, it, new JText.TextDrawOpts
            {
                HideText = env.HideText,
                GlyphLog = env.GlyphLog,
                AllowFilter = env.AllowFilter,
            });
        }
        finally
        {
            env.CharFn = prevC;
            env.PieceFn = prevP;
        }
    }

    /// <summary>Object.assign({}, base, over) 的深拷贝合并。</summary>
    private static JObject Merge(JObject baseObj, JObject over)
    {
        var o = (JObject)baseObj.DeepClone();
        foreach (var kv in over) o[kv.Key] = kv.Value?.DeepClone();
        return o;
    }

    /// <summary>多矩形 nonzero 并集 clip（Canvas 一次 beginPath+多 rect+clip 的对位）。</summary>
    private static void ClipUnion(Env env, IEnumerable<SKRect> rects)
    {
        using var path = new SKPath();
        foreach (var r in rects) path.AddRect(r);
        env.Ctx.ClipPath(path);
    }

    /// <summary>ctx.setLineDash([dash, gap]) + env.line 的本地对位（Env.Line 无 dash 参数；g=false）。</summary>
    private static void DashLine(Env env, double[][] pts, string? c, double lw, double a, double dash, double gap)
    {
        var col = env.ColOf(c, false);
        if (col == null || a <= 0 || pts.Length < 2) return;
        using var path = new SKPath();
        path.MoveTo((float)pts[0][0], (float)pts[0][1]);
        for (var i = 1; i < pts.Length; i++) path.LineTo((float)pts[i][0], (float)pts[i][1]);
        using var p = new SKPaint
        {
            IsAntialias = true,
            IsStroke = true,
            StrokeWidth = (float)lw,
            StrokeJoin = SKStrokeJoin.Miter,
            StrokeCap = SKStrokeCap.Butt,
            Color = Env.SkColor(col, a),
            BlendMode = env.Blend,
            PathEffect = SKPathEffect.CreateDash(new float[] { (float)dash, (float)gap }, 0),
        };
        env.Ctx.DrawPath(path, p);
    }

    // 松散读取（对齐 JS 语义）
    private static string? S(JObject p, string k) => JLayouts.S(p, k);
    private static double D(JObject p, string k, double d = 0) => JLayouts.D(p, k, d);
    private static bool B(JObject p, string k) => JLayouts.B(p, k);

    // ================= 注册（JS 源 reg(...) 出现序） =================

    /// <summary>幂等注册本文件全部 layout（顺序 = JS 源 J.register 出现序；不挂静态构造函数）。</summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;
        RegisterTyKeySplit();   // JS L121
        RegisterTyCropGiant();  // JS L267
        RegisterTyCross();      // JS L351
        RegisterTyBandHide();   // JS L438
        RegisterTyRuby();       // JS L506
        RegisterTyBaseline();   // JS L564
        RegisterTyScaleSteps(); // JS L636
        RegisterTyJustify();    // JS L762
        RegisterTyIndexTable(); // JS L828
        RegisterTySplitType();  // JS L894
        RegisterTyErode();      // JS L950
        RegisterTyVRuler();     // JS L1003
        RegisterTyFullTrack();  // JS L1063
        RegisterTyStatCount();  // JS L1131
        RegisterTyMargin();     // JS L1211
        RegisterTyRotBlock();   // JS L1276
        RegisterTySquare();     // JS L1373
        RegisterTyLineFocus();  // JS L1438
    }

    // ---------------- tyKeySplit（JS L121-265） ----------------

    private static void RegisterTyKeySplit() => Reg0("tyKeySplit", new LayoutDef
    {
        Name = "大字挟み",
        Tags = new[] { "editorial", "graphic", "emotional" },
        W = 1.1, // JS ae:'mixed'（未注册）
        Emph = 1.4,
        Fits = n => n is >= 2 and <= 16,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "display", "serif")) ,
            ["fs"] = rng.Pick(FontsOf(st, "serif", "body", "display")),
            ["mode"] = rng.Pick(new[] { "kanji", "kanji", "mid" }),
            ["big"] = rng.Pick(new[] { "fill", "fill", "accent", "outline" }),
            ["rule"] = rng.Chance(0.75),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var lt = env.Lt;
            var M = Math.Min(W, H);
            var port = W < H;
            var arr = SlotsOf(cut.Text);
            var ki = KeyIndex(arr, S(Pm, "mode") ?? "kanji");
            var key = ki < arr.Count ? arr[ki] : "?";
            var before = string.Concat(arr.Take(ki)).Trim();
            var after = string.Concat(arr.Skip(ki + 1)).Trim();
            if (IsLatinT(cut.Text)) // 拉丁文：最长的词当关键字
            {
                var ws = Regex.Split(cut.Text.Trim(), @"\s+");
                var wi = 0;
                for (var i = 0; i < ws.Length; i++)
                    if (ws[i].Length > ws[wi].Length)
                        wi = i;
                key = ws[wi];
                before = string.Join(" ", ws.Take(wi));
                after = string.Join(" ", ws.Skip(wi + 1));
                ki = string.Join(" ", ws.Take(wi)).Length + (wi != 0 ? 1 : 0);
            }
            var font = S(Pm, "font")!;
            var fs = S(Pm, "fs")!;
            var bigS = port ? Math.Min(W * 0.66, H * 0.36) : Math.Min(H * 0.64, W * 0.34);
            bigS = Math.Min(bigS, JLayouts.FitSize(key, font, port ? W * 0.84 : W * 0.5, 1e6));
            var bw = JLayouts.Measure(new JObject { ["text"] = key, ["font"] = font, ["size"] = bigS }).W;
            var gap = bigS * 0.12;
            var per = port ? 6 : 5;
            var bt = before.Length > 0 ? SplitL(before, per) : "";
            var at = after.Length > 0 ? SplitL(after, per) : "";
            var o = new JObject { ["track"] = 0.04, ["lead"] = 1.2 };
            double ss;
            if (port)
            {
                var avail = (H * 0.86 - bigS) / 2 - gap;
                ss = Math.Min(Math.Min(bt.Length > 0 ? JLayouts.FitSize(bt, fs, W * 0.84, avail, o) : 1e9,
                        at.Length > 0 ? JLayouts.FitSize(at, fs, W * 0.84, avail, o) : 1e9), bigS * 0.3);
            }
            else
            {
                var side = (W * 0.9 - bw - gap * 2) / (((bt.Length > 0 ? 1 : 0) + (at.Length > 0 ? 1 : 0)) | 1);
                ss = Math.Min(Math.Min(bt.Length > 0 ? JLayouts.FitSize(bt, fs, side, bigS * 0.62, o) : 1e9,
                        at.Length > 0 ? JLayouts.FitSize(at, fs, side, bigS * 0.62, o) : 1e9), bigS * 0.3);
            }
            var mB = bt.Length > 0
                ? JLayouts.Measure(Merge(o, new JObject { ["text"] = bt, ["font"] = fs, ["size"] = ss }))
                : null;
            var mA = at.Length > 0
                ? JLayouts.Measure(Merge(o, new JObject { ["text"] = at, ["font"] = fs, ["size"] = ss }))
                : null;
            double bx, by;
            JObject? itB = null, itA = null;
            double BigTop() => by - bigS * 0.46;
            double BigBot() => by + bigS * 0.46;
            if (port == false)
            {
                var tot = (mB != null ? mB.W + gap : 0) + bw + (mA != null ? mA.W + gap : 0);
                var x0 = W / 2 - tot / 2;
                bx = x0 + (mB != null ? mB.W + gap : 0) + bw / 2;
                by = H / 2;
                if (mB != null)
                    itB = Merge(o, new JObject
                    {
                        ["text"] = bt, ["font"] = fs, ["size"] = ss,
                        ["align"] = "right",
                        ["x"] = bx - bw / 2 - gap,
                        ["y"] = BigTop() + mB.H / 2,
                        ["color"] = sc.Str("fg"),
                        ["mi"] = 0,
                    });
                if (mA != null)
                    itA = Merge(o, new JObject
                    {
                        ["text"] = at, ["font"] = fs, ["size"] = ss,
                        ["align"] = "left",
                        ["x"] = bx + bw / 2 + gap,
                        ["y"] = BigBot() - mA.H / 2,
                        ["color"] = sc.Str("fg"),
                        ["mi"] = 4,
                    });
            }
            else
            {
                var tot = (mB != null ? mB.H + gap : 0) + bigS + (mA != null ? mA.H + gap : 0);
                var y0 = H / 2 - tot / 2;
                bx = W / 2;
                by = y0 + (mB != null ? mB.H + gap : 0) + bigS / 2;
                var L = W / 2 - Math.Max(bw, Math.Max(mB?.W ?? 0, mA?.W ?? 0)) / 2;
                var R = W / 2 + Math.Max(bw, Math.Max(mB?.W ?? 0, mA?.W ?? 0)) / 2;
                if (mB != null)
                    itB = Merge(o, new JObject
                    {
                        ["text"] = bt, ["font"] = fs, ["size"] = ss,
                        ["align"] = "left",
                        ["x"] = Math.Max(W * 0.08, Math.Min(L, bx - bw / 2)),
                        ["y"] = by - bigS / 2 - gap - mB.H / 2,
                        ["color"] = sc.Str("fg"),
                        ["mi"] = 0,
                    });
                if (mA != null)
                    itA = Merge(o, new JObject
                    {
                        ["text"] = at, ["font"] = fs, ["size"] = ss,
                        ["align"] = "right",
                        ["x"] = Math.Min(W * 0.92, Math.Max(R, bx + bw / 2)),
                        ["y"] = by + bigS / 2 + gap + mA.H / 2,
                        ["color"] = sc.Str("fg"),
                        ["mi"] = 4,
                    });
            }
            var @out = OutK(env);
            var lw = Hair(env);
            // 细线把两侧小块连到关键字上
            if (B(Pm, "rule"))
            {
                var e = J.E.OutCubic(J.Clamp((lt - 0.2) / 0.6)) * @out;
                if (e > 0 && itB != null)
                {
                    var y = port ? D(itB, "y") + mB!.H / 2 + gap * 0.5 : D(itB, "y") + mB!.H / 2 + ss * 0.35;
                    var xa = port ? D(itB, "x") : D(itB, "x") - mB.W;
                    var xb = port ? D(itB, "x") + Math.Max(mB.W, bw) : bx + bw * 0.5;
                    env.Line(new[] { new[] { xa, y }, new[] { J.Lerp(xa, xb, e), y } }, sc.Str("sub"), lw, 0.7, false);
                }
                if (e > 0 && itA != null)
                {
                    var y = port ? D(itA, "y") - mA!.H / 2 - gap * 0.5 : D(itA, "y") - mA!.H / 2 - ss * 0.35;
                    var xb = port ? D(itA, "x") : D(itA, "x") + mA.W;
                    var xa = port ? D(itA, "x") - Math.Max(mA.W, bw) : bx - bw * 0.5;
                    env.Line(new[] { new[] { xb, y }, new[] { J.Lerp(xb, xa, e), y } }, sc.Str("sub"), lw, 0.7, false);
                }
            }
            var big = new JObject
            {
                ["text"] = key,
                ["font"] = font,
                ["size"] = bigS,
                ["x"] = bx,
                ["y"] = by,
                ["color"] = S(Pm, "big") == "accent" ? AccentOn(sc) : sc.Str("fg"),
                ["mi"] = 2,
            };
            if (S(Pm, "big") == "outline")
            {
                big["fill"] = false;
                big["stroke"] = Math.Max(1.5, bigS * 0.014);
                big["strokeColor"] = sc.Str("fg");
            }
            var bb = JLayouts.MainDraw(env, big);
            if (itB != null) bb = U(bb, JLayouts.MainDraw(env, itB));
            if (itA != null) bb = U(bb, JLayouts.MainDraw(env, itA));
            var la = J.E.OutCubic(J.Clamp((lt - 0.3) / 0.3)) * @out;
            var nG = arr.Count(c => !IsSp(c));
            var kiG = arr.Take(ki).Count(c => !IsSp(c));
            if (la > 0)
                Label(env, $"{Pad2(kiG + 1)} / {Pad2(nG)}", bx - bw / 2, by + bigS * 0.56,
                    alpha: la, size: LabelSize(env) * 0.9);
            env.Body = bb;
        },
    });

    // ---------------- tyCropGiant（JS L267-349） ----------------

    private static void RegisterTyCropGiant() => Reg0("tyCropGiant", new LayoutDef
    {
        Name = "見切れ大文字", // JS ae:'huge'（未注册）
        Tags = new[] { "graphic", "editorial", "pop" },
        W = 1,
        Fits = n => n is >= 1 and <= 14,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "display")),
            ["fm"] = rng.Pick(FontsOf(st, "display", "serif")),
            ["edge"] = rng.Pick(new[] { "a", "b" }),
            ["style"] = rng.Pick(new[] { "dim", "dim", "outline" }),
            ["dir"] = rng.Pick(new[] { 1, -1 }),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var lt = env.Lt;
            var port = W < H;
            var txt = cut.Text.Trim();
            var u = J.Clamp(lt / Math.Max(0.5, cut.Dur));
            var inG = J.E.OutCubic(J.Clamp(lt / 0.8));
            var outG = J.E.InCubic(env.POut);
            var lowEdge = S(Pm, "edge") == "a"; // 横屏走下缘，竖屏走右缘
            var font = S(Pm, "font")!;
            // 被画框裁掉一截的巨型副本
            var gi = new JObject
            {
                ["text"] = port ? Clean(txt) : txt,
                ["font"] = font,
                ["ghost"] = false,
                ["alpha"] = 1,
            };
            if (port == false)
            {
                var G = H * 0.66;
                gi["size"] = G;
                gi["x"] = W / 2 + (0.5 - u) * W * 0.14 * D(Pm, "dir");
                gi["y"] = (lowEdge ? H + G * 0.03 : -G * 0.03)
                          + (lowEdge ? 1 : -1) * ((1 - inG) + outG) * G * 0.45;
            }
            else
            {
                var G = W * 0.62;
                gi["size"] = G;
                gi["vertical"] = true;
                gi["x"] = (lowEdge ? W + G * 0.03 : -G * 0.03)
                          + (lowEdge ? 1 : -1) * ((1 - inG) + outG) * G * 0.45;
                gi["y"] = H / 2 + (0.5 - u) * H * 0.1 * D(Pm, "dir");
            }
            if (S(Pm, "style") == "outline")
            {
                gi["fill"] = false;
                gi["stroke"] = Math.Max(1.4, D(gi, "size") * 0.006);
                gi["strokeColor"] = sc.Str("sub");
                gi["color"] = sc.Str("sub");
                gi["alpha"] = 0.65;
            }
            else gi["color"] = J.Mix(sc.Str("bg") ?? "#000000", sc.Str("fg") ?? "#FFFFFF", 0.16);
            DrawIt(env, gi);
            // 空白侧的可读歌词
            var mt = MainLines(txt, W, H, 10, 6);
            var fm = S(Pm, "fm")!;
            var o = new JObject { ["lead"] = 1.18, ["track"] = 0.03 };
            JObject it;
            if (port == false)
            {
                var size = Math.Min(JLayouts.FitSize(mt, fm, W * 0.74, H * 0.34, o), H * 0.19);
                it = Merge(o, new JObject
                {
                    ["text"] = mt, ["font"] = fm, ["size"] = size,
                    ["align"] = "left",
                    ["x"] = W * 0.08,
                    ["y"] = lowEdge ? H * 0.38 : H * 0.62,
                    ["color"] = sc.Str("fg"),
                });
            }
            else
            {
                var size = Math.Min(JLayouts.FitSize(mt, fm, W * 0.58, H * 0.5, o), W * 0.2);
                it = Merge(o, new JObject
                {
                    ["text"] = mt, ["font"] = fm, ["size"] = size,
                    ["align"] = lowEdge ? "left" : "right",
                    ["x"] = lowEdge ? W * 0.08 : W * 0.92,
                    ["y"] = H / 2,
                    ["color"] = sc.Str("fg"),
                });
            }
            var bb = JLayouts.MainDraw(env, it);
            var m = JLayouts.Measure(it);
            var la = J.E.OutCubic(J.Clamp((lt - 0.2) / 0.35)) * OutK(env);
            var ls = LabelSize(env);
            if (la > 0)
            {
                var ty = D(it, "y") - m.H / 2 - ls * 1.6;
                var x0 = S(it, "align") == "right" ? D(it, "x") - m.W : D(it, "x");
                Label(env, $"No.{Pad2(cut.Line + 1)}", x0, ty, alpha: la, color: AccentOn(sc));
                env.Line(new[]
                {
                    new[] { x0 + ls * 4, ty },
                    new[] { x0 + ls * 4 + Math.Min(m.W, W * 0.3) * J.E.OutExpo(J.Clamp((lt - 0.25) / 0.5)), ty },
                }, sc.Str("sub"), Hair(env), 0.7 * la, false);
            }
            env.Body = bb;
        },
    });

    // ---------------- tyCross（JS L351-436） ----------------

    private static void RegisterTyCross() => Reg0("tyCross", new LayoutDef
    {
        Name = "十字組", // JS ae:'vcols'（未注册）
        Tags = new[] { "graphic", "editorial" },
        W = 0.9,
        Fits = n => n is >= 3 and <= 11,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
            ["mode"] = rng.Pick(new[] { "kanji", "mid", "mid" }),
            ["rules"] = rng.Chance(0.8),
            ["key"] = rng.Pick(new[] { "accent", "accent", "fg" }),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var lt = env.Lt;
            var M = Math.Min(W, H);
            var arr = SlotsOf(env.Cut!.Text);
            var n = arr.Count;
            if (n == 0)
            {
                env.Body = null;
                return;
            }
            var font = S(Pm, "font")!;
            var ki = KeyIndex(arr, S(Pm, "mode") ?? "kanji");
            const double tr = 0.08;
            var tA = string.Concat(arr.Take(ki)).Trim();
            var tK = arr[ki];
            var tB = string.Concat(arr.Skip(ki + 1)).Trim(); // JS 局部名 B 与本文件 B() 帮手撞名，改名 tA/tK/tB
            double W100(string s) => s.Length > 0
                ? JLayouts.Measure(new JObject { ["text"] = s, ["font"] = font, ["size"] = 100, ["track"] = tr }).W
                : 0;
            double H100(string s) => s.Length > 0
                ? JLayouts.Measure(new JObject
                    { ["text"] = s, ["font"] = font, ["size"] = 100, ["track"] = tr, ["vertical"] = true }).H
                : 0;
            var kw = W100(tK);
            var g100 = 100 * tr + 6;
            var Lx = (tA.Length > 0 ? W100(tA) + g100 : 0) + kw / 2;
            var Rx = (tB.Length > 0 ? W100(tB) + g100 : 0) + kw / 2;
            var Ty = (tA.Length > 0 ? H100(tA) + g100 : 0) + 50;
            var By = (tB.Length > 0 ? H100(tB) + g100 : 0) + 50;
            var size = Math.Min(Math.Min(W * 0.86 / ((Lx + Rx) / 100), H * 0.86 / ((Ty + By) / 100)), M * 0.2);
            var k = size / 100;
            var cx = W / 2 - (Rx - Lx) * k / 2;
            var cy = H / 2 - (By - Ty) * k / 2;
            var @out = OutK(env);
            var lw = Hair(env);
            if (B(Pm, "rules"))
            {
                var e = J.E.OutExpo(J.Clamp((lt - 0.1) / 0.7)) * @out;
                if (e > 0)
                {
                    env.Line(new[] { new[] { cx - W * e, cy }, new[] { cx + W * e, cy } }, sc.Str("sub"), lw, 0.28, false);
                    env.Line(new[] { new[] { cx, cy - H * e }, new[] { cx, cy + H * e } }, sc.Str("sub"), lw, 0.28, false);
                    env.Circle(cx, cy, size * 0.78 * (0.6 + 0.4 * e), null, sc.Str("sub"), lw, 0.5 * e, false);
                }
            }
            var g = size * tr + 6 * k;
            var baseIt = new JObject { ["font"] = font, ["size"] = size, ["track"] = tr, ["color"] = sc.Str("fg") };
            BBox? bb = null;
            if (tA.Length > 0)
                bb = U(bb, JLayouts.MainDraw(env, Merge(baseIt, new JObject
                {
                    ["text"] = tA, ["align"] = "right",
                    ["x"] = cx - kw * k / 2 - g, ["y"] = cy, ["mi"] = 0,
                })));
            var kb = JLayouts.MainDraw(env, Merge(baseIt, new JObject
            {
                ["text"] = tK, ["x"] = cx, ["y"] = cy,
                ["color"] = S(Pm, "key") == "accent" ? AccentOn(sc) : sc.Str("fg"), ["mi"] = 1,
            }));
            bb = U(bb, kb);
            if (tB.Length > 0)
                bb = U(bb, JLayouts.MainDraw(env, Merge(baseIt, new JObject
                {
                    ["text"] = tB, ["align"] = "left",
                    ["x"] = cx + kw * k / 2 + g, ["y"] = cy, ["mi"] = 2,
                })));
            if (tA.Length > 0)
            {
                var h = H100(tA) * k;
                bb = U(bb, JLayouts.MainDraw(env, Merge(baseIt, new JObject
                {
                    ["text"] = tA, ["vertical"] = true,
                    ["x"] = cx, ["y"] = cy - size / 2 - g - h / 2, ["mi"] = 3,
                })));
            }
            if (tB.Length > 0)
            {
                var h = H100(tB) * k;
                bb = U(bb, JLayouts.MainDraw(env, Merge(baseIt, new JObject
                {
                    ["text"] = tB, ["vertical"] = true,
                    ["x"] = cx, ["y"] = cy + size / 2 + g + h / 2, ["mi"] = 4,
                })));
            }
            var la = J.E.OutCubic(J.Clamp((lt - 0.35) / 0.3)) * @out;
            if (la > 0)
                Label(env, $"{Pad2(ki + 1)}×{Pad2(ki + 1)}", cx + size * 0.62, cy - size * 0.62,
                    alpha: la, size: LabelSize(env) * 0.9);
            env.Body = bb;
        },
    });

    // ---------------- tyBandHide（JS L438-504） ----------------

    /// <summary>tyBandHide 的帧不变量（CutCache 载体，性能专项）。</summary>
    private sealed record TyBandInv(string Mt, int Lines, double Size, double Fs, double Per, string Rep);

    private static void RegisterTyBandHide() => Reg0("tyBandHide", new LayoutDef
    {
        Name = "帯隠れ", // JS ae:'diag'（未注册）
        Tags = new[] { "graphic", "pop", "editorial" },
        W = 1,
        Fits = n => n is >= 1 and <= 14,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "display")),
            ["band"] = rng.Pick(new[] { "ink", "accent", "fg" }),
            ["pos"] = rng.Pick(new[] { "low", "low", "high" }),
            ["speed"] = rng.Range(40, 90),
            ["dir"] = rng.Pick(new[] { 1, -1 }),
            ["k"] = rng.Range(0.3, 0.38),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var lt = env.Lt;
            const double lead = 1.32;
            // 帧不变量（性能专项）：清洗/折行/FitSize/主盒度量、跑马灯单元串与单元宽只由 (cut, params, W, H) 决定，
            // 逐帧重算曾是本版式的分配主要来源（--leaf 归因）；按切分缓存（plan 换随 Cut 回收）
            var inv = CutCache.Get<TyBandInv>(cut, "tyBandHide");
            if (inv == null)
            {
                var txt0 = cut.Text.Trim();
                var font0 = S(Pm, "font")!;
                var mt0 = MainLines(txt0, W, H, 10, 5);
                var size0 = Math.Min(JLayouts.FitSize(mt0, font0, W * 0.8, H * 0.66,
                    new JObject { ["lead"] = lead, ["track"] = 0.02 }), H * 0.3);
                var unit0 = Regex.Replace(string.IsNullOrEmpty(cut.LineText) ? txt0 : cut.LineText, @"\s+", " ").Trim() + "　／　";
                var fs0 = size0 * (D(Pm, "k") + 0.08) * 0.44;
                var per0 = JLayouts.Measure(new JObject { ["text"] = unit0, ["font"] = BodyF(env), ["size"] = fs0, ["track"] = 0.12 }).W;
                var rep0 = per0 > 1 ? JLayouts.Repeat(unit0, Math.Min(40, (int)Math.Ceiling(W * 2 / per0) + 2)) : unit0;
                inv = new TyBandInv(mt0, mt0.Split('\n').Length, size0, fs0, per0, rep0);
                CutCache.Put(cut, "tyBandHide", inv);
            }
            var mt = inv.Mt;
            var L = inv.Lines;
            var size = inv.Size;
            var bb = JLayouts.MainDraw(env, new JObject
            {
                ["text"] = mt, ["font"] = S(Pm, "font")!, ["size"] = size,
                ["x"] = W / 2, ["y"] = H / 2, ["lead"] = lead, ["track"] = 0.02,
                ["color"] = sc.Str("fg"),
            });
            var bandKey = S(Pm, "band") ?? "ink";
            var bandC = PlateCol(sc,
                bandKey == "accent" ? new[] { sc.Str("accent"), sc.Str("ink"), sc.Str("fg") }!
                : bandKey == "ink" ? new[] { sc.Str("ink"), sc.Str("accent"), sc.Str("fg") }!
                : new[] { sc.Str("fg"), sc.Str("accent") }!);
            var tc = OnCol(sc, bandC);
            var @out = 1 - J.E.InExpo(env.POut);
            for (var li = 0; li < L; li++)
            {
                var e = J.E.OutExpo(J.Clamp((lt - 0.12 - li * 0.08) / 0.5)) * @out;
                if (e <= 0) continue;
                var yl = H / 2 + (li - (L - 1) / 2.0) * size * lead;
                var h = size * (D(Pm, "k") + 0.08);
                var y0 = S(Pm, "pos") == "low" ? yl + size * (0.5 - D(Pm, "k")) : yl - size * 0.58;
                var d = (li % 2 != 0 ? -1 : 1) * D(Pm, "dir");
                var bw = (W + 20) * e;
                var x0 = d > 0 ? -10 : W + 10 - bw;
                env.Rect(x0, y0, bw, h, bandC, 1, false);
                if (env.Pass == "main" && inv.Per > 1)
                {
                    var fs = inv.Fs;
                    var per = inv.Per;
                    env.Ctx.Save();
                    env.Ctx.ClipRect(new SKRect((float)x0, (float)y0, (float)(x0 + bw), (float)(y0 + h)));
                    var off = ((env.Ltb * D(Pm, "speed") * d) % per + per) % per;
                    DrawIt(env, new JObject
                    {
                        ["text"] = inv.Rep,
                        ["font"] = BodyF(env),
                        ["size"] = fs,
                        ["track"] = 0.12,
                        ["align"] = "left",
                        ["x"] = -per + off,
                        ["y"] = y0 + h / 2,
                        ["color"] = tc,
                        ["ghost"] = false,
                    });
                    env.Ctx.Restore();
                }
            }
            env.Body = bb;
        },
    });

    // ---------------- tyRuby（JS L506-562） ----------------

    private static void RegisterTyRuby() => Reg0("tyRuby", new LayoutDef
    {
        Name = "ルビ振り", // JS ae:'gloss'（未注册）
        Tags = new[] { "editorial", "calm", "emotional" },
        W = 1,
        Fits = n => n is >= 1 and <= 14,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "serif", "display")),
            ["idx"] = rng.Chance(0.7),
            ["up"] = rng.Chance(0.5),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var lt = env.Lt;
            var txt = env.Cut!.Text.Trim();
            var font = S(Pm, "font")!;
            var mt = MainLines(txt, W, H, 9, 5);
            var o = new JObject { ["track"] = 0.16, ["lead"] = 2.0 };
            var size = Math.Min(Math.Min(JLayouts.FitSize(mt, font, W * 0.84, H * 0.62, o), H * 0.2), W * 0.24);
            var it = Merge(o, new JObject
            {
                ["text"] = mt, ["font"] = font, ["size"] = size,
                ["x"] = W / 2, ["y"] = H / 2 + size * 0.1,
                ["color"] = sc.Str("fg"),
            });
            var bb = JLayouts.MainDraw(env, it);
            var gl = GlyphPts(it);
            var @out = OutK(env);
            var mono = MonoF(env);
            var lw = Hair(env);
            var rs = Math.Max(10, size * 0.3);
            for (var i = 0; i < gl.Count; i++)
            {
                var g = gl[i];
                var a = J.E.OutCubic(J.Clamp((lt - 0.18 - i * 0.045) / 0.3)) * @out;
                if (a <= 0) continue;
                var ry = g.Y - size * 0.74;
                var rt = J.IsHira(g.Ch[0]) || J.IsKata(g.Ch[0]) ? J.Romaji(g.Ch) : null;
                if (rt == "") rt = null;
                if (rt != null)
                {
                    var w0 = JLayouts.Measure(new JObject { ["text"] = rt, ["font"] = mono, ["size"] = rs, ["track"] = 0.02 }).W;
                    var fs = w0 > g.W * 1.05 ? rs * g.W * 1.05 / w0 : rs;
                    DrawIt(env, new JObject
                    {
                        ["text"] = B(Pm, "up") ? rt.ToUpperInvariant() : rt,
                        ["font"] = mono,
                        ["size"] = fs,
                        ["track"] = 0.02,
                        ["x"] = g.X,
                        ["y"] = ry + (1 - a) * rs * 0.5,
                        ["color"] = sc.Str("sub"),
                        ["alpha"] = a,
                        ["ghost"] = false,
                    });
                }
                else if (J.IsKanji(g.Ch[0]))
                {
                    var w = g.W * 0.5 * a;
                    env.Line(new[]
                    {
                        new[] { g.X - w, ry + rs * 0.2 }, new[] { g.X - w, ry - rs * 0.15 },
                        new[] { g.X + w, ry - rs * 0.15 }, new[] { g.X + w, ry + rs * 0.2 },
                    }, AccentOn(sc), Math.Max(1.2, lw * 1.2), a, false);
                }
                if (B(Pm, "idx"))
                {
                    var iy = g.Y + size * 0.72;
                    env.Line(new[] { new[] { g.X, iy - rs * 0.55 }, new[] { g.X, iy - rs * 0.2 } },
                        sc.Str("sub"), lw, 0.6 * a, false);
                    DrawIt(env, new JObject
                    {
                        ["text"] = Pad2(i + 1),
                        ["font"] = mono,
                        ["size"] = rs * 0.72,
                        ["x"] = g.X,
                        ["y"] = iy + rs * 0.12,
                        ["color"] = sc.Str("sub"),
                        ["alpha"] = 0.75 * a,
                        ["ghost"] = false,
                    });
                }
            }
            env.Body = bb;
        },
    });

    // ---------------- tyBaseline（JS L564-634） ----------------

    private static void RegisterTyBaseline() => Reg0("tyBaseline", new LayoutDef
    {
        Name = "罫線組", // JS ae:'type'（未注册）
        Tags = new[] { "editorial", "calm", "graphic" },
        W = 1.1,
        Fits = n => n is >= 2 and <= 18,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "serif", "display", "body")),
            ["nl"] = rng.Pick(new[] { 2, 3, 3 }),
            ["key"] = rng.Chance(0.8),
            ["right"] = rng.Chance(0.5),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var lt = env.Lt;
            var port = W < H;
            var txt = cut.Text.Trim();
            var nG = JLayouts.GlyphCount(txt);
            var font = S(Pm, "font")!;
            var per = port ? 4 : Math.Max(4, (int)Math.Ceiling(nG / (double)I(Pm, "nl")));
            var lines = (nG <= 4 ? txt : SplitL(txt, per)).Split('\n').Take(6).ToList();
            if (IsLatinT(txt))
            {
                var ws = Regex.Split(txt, @"\s+");
                lines = GroupLines(ws.Select((w, i) => (i != 0 ? " " : "") + w).ToList(),
                    Math.Min(ws.Length, port ? 4 : I(Pm, "nl")));
            }
            var L = lines.Count;
            const double leadK = 1.62;
            var size = Math.Min(H * 0.7 / (L * leadK), port ? W * 0.17 : H * 0.16);
            foreach (var l in lines)
                size = Math.Min(size, JLayouts.FitSize(l, font, port ? W * 0.76 : W * 0.74, 1e6,
                    new JObject { ["track"] = 0.04 }));
            var x0 = port ? W * 0.16 : W * 0.14;
            var mx = W * 0.06;
            var @out = OutK(env);
            var lw = Hair(env);
            var mono = MonoF(env);
            var ls = LabelSize(env);
            // 关键字（基线上要打 accent 记号）
            var all = string.Concat(lines);
            var arr = JLayouts.CodePoints(all);
            var ki = KeyIndex(arr, "kanji");
            var kEnd = ki;
            while (kEnd + 1 < arr.Count && J.IsKanji(arr[kEnd + 1][0]) && J.IsKanji(arr[ki][0])) kEnd++;
            var acc = 0;
            BBox? bb = null;
            for (var li = 0; li < L; li++)
            {
                var ln = lines[li];
                var y = H / 2 + (li - (L - 1) / 2.0) * size * leadK;
                var yb = y + size * 0.6;
                var e = J.E.OutExpo(J.Clamp((lt - li * 0.1) / 0.6)) * @out;
                if (e > 0)
                {
                    env.Line(new[] { new[] { mx, yb }, new[] { J.Lerp(mx, W - mx, e), yb } }, sc.Str("sub"), lw, 0.55, false);
                    Label(env, Pad2(li + 1), mx, y + size * 0.2, alpha: e, size: ls * 0.9, font: mono);
                    if (B(Pm, "right"))
                        Label(env, $"{JLayouts.GlyphCount(ln)}", W - mx, y + size * 0.2,
                            alpha: e * 0.8, size: ls * 0.9, align: "right");
                }
                var it = new JObject
                {
                    ["text"] = ln, ["font"] = font, ["size"] = size,
                    ["align"] = "left", ["x"] = x0, ["y"] = y,
                    ["track"] = 0.04, ["color"] = sc.Str("fg"), ["mi"] = li * 4,
                };
                bb = U(bb, JLayouts.MainDraw(env, it));
                var n = JLayouts.CodePoints(ln).Count;
                if (B(Pm, "key") && ki >= acc && ki < acc + n && e > 0)
                {
                    var gp = GlyphPts(it);
                    var idx0 = JLayouts.CodePoints(ln).Take(ki - acc).Count(c => !IsSp(c));
                    var idx1 = Math.Min(gp.Count - 1, idx0 + Math.Min(kEnd, acc + n - 1) - ki);
                    if (idx0 < gp.Count && gp[idx0] != null && idx1 >= 0 && idx1 < gp.Count)
                    {
                        var a0 = gp[idx0].X - gp[idx0].W / 2;
                        var a1 = gp[idx1].X + gp[idx1].W / 2;
                        var q = J.E.OutExpo(J.Clamp((lt - 0.35 - li * 0.1) / 0.45)) * @out;
                        env.Rect(a0, yb - Math.Max(2, size * 0.035), (a1 - a0) * q, Math.Max(3, size * 0.07),
                            AccentOn(sc), 1, false);
                    }
                }
                acc += n;
            }
            env.Body = bb;
        },
    });

    // ---------------- tyScaleSteps（JS L636-743） ----------------

    private static void RegisterTyScaleSteps() => Reg0("tyScaleSteps", new LayoutDef
    {
        Name = "級数上げ", // JS ae:'mixed'（未注册）
        Tags = new[] { "graphic", "editorial", "pop" },
        W = 1,
        Fits = n => n is >= 2 and <= 16,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
            ["dir"] = rng.Pick(new[] { "up", "up", "down" }),
            ["labels"] = rng.Chance(0.8),
            ["ratio"] = rng.Range(2.0, 2.8),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var lt = env.Lt;
            var M = Math.Min(W, H);
            var port = W < H;
            var txt = cut.Text.Trim();
            var font = S(Pm, "font")!;
            var units = cut.Words.Select(w => w.Trim()).Where(w => w.Length > 0).ToList();
            var chars = SlotsOf(txt).Where(c => !IsSp(c)).ToList();
            if (units.Count < 2 || units.Count > 7)
            {
                if (chars.Count <= 7) units = chars;
                else
                {
                    var kN = Math.Min(5, (int)Math.Ceiling(chars.Count / 3.0));
                    units = SplitL(txt, (int)Math.Ceiling(chars.Count / (double)kN)).Split('\n').ToList();
                }
            }
            var k = units.Count;
            var ratio = D(Pm, "ratio");
            var up = S(Pm, "dir") == "up";
            var f = units.Select((_, i) =>
            {
                var t = k > 1 ? i / (double)(k - 1) : 1;
                return Math.Pow(ratio, up ? t : 1 - t);
            }).ToList();
            var fmax = f.Max();
            const double tr = 0.02;
            var @out = OutK(env);
            var lw = Hair(env);
            var ls = LabelSize(env);
            double WOf(string u, double s) =>
                JLayouts.Measure(new JObject { ["text"] = u, ["font"] = font, ["size"] = s, ["track"] = tr }).W;
            BBox? bb = null;
            var items = new List<(JObject It, double W, double Yb)>();
            if (port == false)
            {
                const double gapK = 0.16;
                var latin = IsLatinT(txt) && units.Count > 1 && units.Count < chars.Count;
                var tot = 0.0;
                for (var i = 0; i < units.Count; i++)
                    tot += WOf(units[i], 100 * f[i]) + (i != 0 ? 100 * gapK * f[i] * (latin ? 1.6 : 0.6) : 0);
                var @base = Math.Min(W * 0.86 / tot * 100, H * 0.52 / fmax);
                var x = W / 2 - tot * @base / 100 / 2;
                var yb = H / 2 + @base * fmax * 0.36;
                for (var i = 0; i < units.Count; i++)
                {
                    var s = @base * f[i];
                    if (i != 0) x += s * gapK * (latin ? 1.6 : 0.6);
                    items.Add((new JObject
                    {
                        ["text"] = units[i], ["font"] = font, ["size"] = s,
                        ["align"] = "left", ["x"] = x, ["y"] = yb - s * 0.46,
                        ["track"] = tr, ["color"] = sc.Str("fg"), ["mi"] = i * 2,
                    }, WOf(units[i], s), yb));
                    x += WOf(units[i], s);
                }
                var e = J.E.OutExpo(J.Clamp((lt - 0.05) / 0.7)) * @out;
                var xa = D(items[0].It, "x");
                var xb = x;
                if (e > 0)
                    env.Line(new[] { new[] { xa, yb + @base * 0.08 }, new[] { J.Lerp(xa, xb, e), yb + @base * 0.08 } },
                        sc.Str("sub"), lw, 0.6, false);
            }
            else
            {
                var tot = 0.0;
                for (var i = 0; i < units.Count; i++) tot += 100 * f[i] * 1.12;
                var @base = H * 0.78 / tot * 100;
                for (var i = 0; i < units.Count; i++)
                    @base = Math.Min(@base, W * 0.8 / Math.Max(0.01, WOf(units[i], 100 * f[i]) / 100));
                @base = Math.Min(@base, W * 0.5 / fmax);
                var y = H / 2 - tot * @base / 100 / 2;
                for (var i = 0; i < units.Count; i++)
                {
                    var s = @base * f[i];
                    items.Add((new JObject
                    {
                        ["text"] = units[i], ["font"] = font, ["size"] = s,
                        ["align"] = "left", ["x"] = W * 0.1, ["y"] = y + s * 0.56,
                        ["track"] = tr, ["color"] = sc.Str("fg"), ["mi"] = i * 2,
                    }, WOf(units[i], s), y + s * 1.06));
                    y += s * 1.12;
                }
            }
            for (var i = 0; i < items.Count; i++)
            {
                var (it, w, yb) = items[i];
                bb = U(bb, JLayouts.MainDraw(env, it));
                if (B(Pm, "labels"))
                {
                    var a = J.E.OutCubic(J.Clamp((lt - 0.25 - i * 0.07) / 0.3)) * @out;
                    if (a > 0)
                    {
                        var pt = (int)Math.Round(D(it, "size") / (M / 1080) * 0.75, MidpointRounding.AwayFromZero);
                        if (port == false)
                            Label(env, $"{pt}pt", D(it, "x"), yb + ls * 1.3, alpha: a, size: ls * 0.85);
                        else
                            Label(env, $"{pt}pt", D(it, "x") + w + ls * 0.8, yb - ls * 0.6, alpha: a, size: ls * 0.85);
                    }
                }
            }
            env.Body = bb;
        },
    });

    // ---------------- tyJustify（JS L762-826） ----------------

    private static void RegisterTyJustify() => Reg0("tyJustify", new LayoutDef
    {
        Name = "幅揃え", // JS ae:'stack'（未注册）
        Tags = new[] { "graphic", "pop", "editorial" },
        W = 1.1,
        Fits = n => n is >= 3 and <= 16,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "display")),
            ["rules"] = rng.Chance(0.7),
            ["acc"] = rng.Int(0, 3),
            ["align"] = rng.Pick(new[] { "center", "left" }),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var lt = env.Lt;
            var port = W < H;
            var txt = cut.Text.Trim();
            var n = JLayouts.GlyphCount(txt);
            var font = S(Pm, "font")!;
            var L = n <= 5 ? 2 : n <= 10 ? 3 : 4;
            var units = cut.Words.Where(w => w.Trim().Length > 0).ToList();
            if (IsLatinT(txt))
                units = Regex.Split(txt, @"\s+").Select((w, i) => (i != 0 ? " " : "") + w).ToList();
            else
            {
                if (units.Count < 2) units = Planner.Segments(txt).Where(w => w.Trim().Length > 0).ToList();
                if (units.Count < 2) units = JLayouts.CodePoints(txt);
            }
            L = Math.Min(L, units.Count);
            var lines = GroupLines(units, L);
            for (var i = 1; i < lines.Count; i++)
            {
                // 行头标点并回上一行（JS [...] 首码点 + slice(c.length)）
                while (lines[i].Length > 0 && J.IsPunct(JLayouts.CodePoints(lines[i])[0][0]))
                {
                    var c = JLayouts.CodePoints(lines[i])[0];
                    lines[i - 1] += c;
                    lines[i] = string.Concat(JLayouts.CodePoints(lines[i]).Skip(1)).Trim();
                }
            }
            lines = lines.Where(l => l.Length > 0).ToList();
            if (lines.Count < 2 && !IsLatinT(txt))
                lines = SplitL(txt, (int)Math.Ceiling(n / 2.0)).Split('\n').ToList();
            var Wb = port ? W * 0.84 : Math.Min(W * 0.62, H * 1.15);
            const double tr = 0.01;
            const double gapK = 0.1;
            var sizes = lines.Select(l => Math.Min(JLayouts.FitSize(l, font, Wb, 1e6, new JObject { ["track"] = tr }), H * 0.36)).ToList();
            var tot = sizes.Sum(s => s * (1 + gapK)) - (sizes.Count > 0 ? sizes[^1] * gapK : 0);
            var k = Math.Min(1, H * 0.84 / tot);
            sizes = sizes.Select(s => s * k).ToList();
            tot *= k;
            var x0 = W / 2 - Wb * k / 2;
            var @out = OutK(env);
            var lw = Hair(env);
            var y = H / 2 - tot / 2;
            BBox? bb = null;
            var align = S(Pm, "align") ?? "center";
            var accI = I(Pm, "acc") % lines.Count;
            for (var li = 0; li < lines.Count; li++)
            {
                var s = sizes[li];
                var it = new JObject
                {
                    ["text"] = lines[li], ["font"] = font, ["size"] = s,
                    ["x"] = align == "left" ? x0 : W / 2,
                    ["align"] = align == "left" ? "left" : "center",
                    ["y"] = y + s / 2, ["track"] = tr,
                    ["color"] = li == accI && lines.Count > 2 ? AccentOn(sc) : sc.Str("fg"),
                    ["mi"] = li * 3,
                };
                bb = U(bb, JLayouts.MainDraw(env, it));
                if (B(Pm, "rules") && li < lines.Count - 1)
                {
                    var e = J.E.OutExpo(J.Clamp((lt - 0.15 - li * 0.08) / 0.6)) * @out;
                    var ry = y + s + s * gapK * k * 0.5;
                    if (e > 0)
                        env.Line(new[]
                        {
                            new[] { W / 2 - Wb * k / 2 * e, ry },
                            new[] { W / 2 + Wb * k / 2 * e, ry },
                        }, sc.Str("sub"), lw, 0.55, false);
                }
                y += s * (1 + gapK);
            }
            env.Body = bb;
        },
    });

    // ---------------- tyIndexTable（JS L828-892） ----------------

    private static void RegisterTyIndexTable() => Reg0("tyIndexTable", new LayoutDef
    {
        Name = "一覧表", // JS ae:'gloss'（未注册）
        Tags = new[] { "editorial", "graphic", "calm" },
        W = 0.8,
        Portrait = 1.3,
        Fits = n => n is >= 2 and <= 9,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
            ["info"] = rng.Pick(new[] { "roma", "roma", "code" }),
            ["head"] = rng.Chance(0.8),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var lt = env.Lt;
            var M = Math.Min(W, H);
            var port = W < H;
            var chars = JLayouts.CodePoints(Clean(cut.Text));
            var n = chars.Count;
            if (n == 0)
            {
                env.Body = null;
                return;
            }
            var font = S(Pm, "font")!;
            var rowH = Math.Min(H * 0.78 / n, M * 0.2);
            var gs = rowH * 0.86;
            var Wt = port ? W * 0.84 : Math.Min(W * 0.56, H * 1.0);
            var x0 = W / 2 - Wt / 2;
            var x1 = W / 2 + Wt / 2;
            var xg = x0 + Wt * 0.24;
            var top = H / 2 - n * rowH / 2;
            var @out = OutK(env);
            var lw = Hair(env);
            var mono = MonoF(env);
            var fs = J.Clamp(rowH * 0.3, 11, 34);
            var e0 = J.E.OutExpo(J.Clamp(lt / 0.6)) * @out;
            if (e0 > 0)
            {
                env.Line(new[] { new[] { x0, top }, new[] { J.Lerp(x0, x1, e0), top } }, sc.Str("fg"), lw * 2, 0.9, false);
                env.Line(new[] { new[] { x0, top + n * rowH }, new[] { J.Lerp(x0, x1, e0), top + n * rowH } },
                    sc.Str("fg"), lw * 2, 0.9, false);
                if (B(Pm, "head"))
                {
                    Label(env, "No.", x0, top - fs * 0.9, alpha: e0, size: fs * 0.8);
                    Label(env, S(Pm, "info") == "code" ? "CODE" : "READING", x1, top - fs * 0.9,
                        alpha: e0, size: fs * 0.8, align: "right");
                }
            }
            BBox? bb = null;
            for (var i = 0; i < n; i++)
            {
                var ch = chars[i];
                var y = top + (i + 0.5) * rowH;
                bb = U(bb, JLayouts.MainDraw(env, new JObject
                    { ["text"] = ch, ["font"] = font, ["size"] = gs, ["x"] = xg, ["y"] = y, ["color"] = sc.Str("fg"), ["mi"] = i }));
                var a = J.E.OutCubic(J.Clamp((lt - 0.12 - i * 0.06) / 0.3)) * @out;
                if (a <= 0) continue;
                Label(env, Pad2(i + 1), x0, y, alpha: a, size: fs, color: i == 0 ? AccentOn(sc) : sc.Str("sub"));
                string? info = null;
                if (S(Pm, "info") != "code" && (J.IsHira(ch[0]) || J.IsKata(ch[0])))
                {
                    var r = J.Romaji(ch);
                    if (!string.IsNullOrEmpty(r)) info = r!.ToUpperInvariant();
                }
                if (info == null)
                    info = "U+" + char.ConvertToUtf32(ch, 0).ToString("X", CultureInfo.InvariantCulture).PadLeft(4, '0');
                var iw = JLayouts.Measure(new JObject { ["text"] = info, ["font"] = mono, ["size"] = fs, ["track"] = 0.12 }).W;
                Label(env, info, x1, y, alpha: a, size: fs, align: "right");
                var la = xg + gs * 0.72;
                var lb = x1 - iw - fs * 0.8;
                if (lb > la && env.Pass == "main")
                    DashLine(env, new[] { new[] { la, y + fs * 0.2 }, new[] { J.Lerp(la, lb, a), y + fs * 0.2 } },
                        sc.Str("sub"), Math.Max(2, lw * 2), a, Math.Max(2, lw * 2), Math.Max(5, fs * 0.5));
                if (i < n - 1)
                    env.Line(new[] { new[] { x0, top + (i + 1) * rowH }, new[] { x1, top + (i + 1) * rowH } },
                        sc.Str("sub"), lw, 0.22 * a, false);
            }
            env.Body = bb;
        },
    });

    // ---------------- tySplitType（JS L894-948） ----------------

    private static void RegisterTySplitType() => Reg0("tySplitType", new LayoutDef
    {
        Name = "断ち割り", // JS ae:'center'（未注册）
        Tags = new[] { "graphic", "glitch", "pop" },
        W = 1,
        Fits = n => n is >= 1 and <= 14,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "display")),
            ["off"] = rng.Range(0.08, 0.15) * rng.Pick(new[] { 1, -1 }),
            ["cutY"] = rng.Pick(new[] { 0.02, -0.06, 0.08 }),
            ["cap"] = rng.Chance(0.75),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var lt = env.Lt;
            var txt = cut.Text.Trim();
            var font = S(Pm, "font")!;
            var mt = MainLines(txt, W, H, 11, 5);
            var L = mt.Split('\n').Length;
            const double lead = 1.16;
            var size = Math.Min(JLayouts.FitSize(mt, font, W * 0.76, H * 0.56,
                new JObject { ["lead"] = lead, ["track"] = 0.03 }), H * 0.3);
            var e = J.E.OutExpo(J.Clamp((lt - 0.12) / 0.7));
            var d = size * D(Pm, "off") * e;
            var gy = size * 0.05 * e;
            var @base = new JObject
            {
                ["text"] = mt, ["font"] = font, ["size"] = size, ["y"] = H / 2,
                ["lead"] = lead, ["track"] = 0.03, ["color"] = sc.Str("fg"), ["mi"] = 0,
            };
            var ys = new List<double>();
            for (var li = 0; li < L; li++)
                ys.Add(H / 2 + (li - (L - 1) / 2.0) * size * lead + size * D(Pm, "cutY"));

            BBox? Part(bool topPart)
            {
                env.Ctx.Save();
                try
                {
                    // Canvas 多 rect 一次 clip（nonzero 并集）→ SKPath + ClipPath
                    var rects = new List<SKRect>();
                    for (var li = 0; li < ys.Count; li++)
                    {
                        var yc = ys[li];
                        var a = yc - size * lead / 2;
                        var b = yc + size * lead / 2;
                        if (topPart)
                        {
                            var yTop = li != 0 ? a : -H;
                            rects.Add(new SKRect((float)-W, (float)yTop, (float)(W * 2), (float)yc));
                        }
                        else
                        {
                            var yBot = li < L - 1 ? b : H * 2;
                            rects.Add(new SKRect((float)-W, (float)yc, (float)(W * 2), (float)yBot));
                        }
                    }
                    ClipUnion(env, rects);
                    return JLayouts.MainDraw(env, Merge(@base, new JObject
                    {
                        ["x"] = W / 2 + (topPart ? d : -d),
                        ["y"] = H / 2 + (topPart ? -gy : gy),
                    }));
                }
                finally
                {
                    env.Ctx.Restore();
                }
            }

            var bb = U(Part(true), Part(false));
            var @out = OutK(env);
            var lw = Hair(env);
            if (e * @out > 0.01)
            {
                for (var li = 0; li < ys.Count; li++)
                {
                    var yc = ys[li];
                    var q = J.E.OutExpo(J.Clamp((lt - 0.2 - li * 0.08) / 0.6)) * @out;
                    env.Line(new[] { new[] { W * 0.04, yc }, new[] { J.Lerp(W * 0.04, W * 0.96, q), yc } },
                        AccentOn(sc), Math.Max(1.2, lw * 1.2), 0.9, false);
                }
                if (B(Pm, "cap"))
                {
                    var ls = LabelSize(env) * 0.85;
                    var cap = (RomaOf(txt) ?? Regex.Replace(string.IsNullOrEmpty(cut.LineText) ? txt : cut.LineText, @"\s+", " ").Trim());
                    if (cap.Length > 48) cap = cap[..48];
                    Label(env, cap, W * 0.96, ys[^1] + ls * 0.9,
                        align: "right",
                        alpha: @out * J.E.OutCubic(J.Clamp((lt - 0.4) / 0.3)),
                        size: ls, track: 0.2);
                }
            }
            env.Body = bb;
        },
    });

    // ---------------- tyErode（JS L950-1001） ----------------

    private static void RegisterTyErode() => Reg0("tyErode", new LayoutDef
    {
        Name = "削り反復", // JS ae:'stack'（未注册）
        Tags = new[] { "emotional", "editorial", "calm" },
        W = 0.9,
        Portrait = 0.6,
        Fits = n => n is >= 3 and <= 12,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "serif", "display")),
            ["mode"] = rng.Pick(new[] { "grow", "grow", "erode" }),
            ["idx"] = rng.Chance(0.7),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var lt = env.Lt;
            var font = S(Pm, "font")!;
            var arr = SlotsOf(env.Cut!.Text);
            var nS = arr.Count;
            var R = Math.Min(5, Math.Max(2, arr.Count(c => !IsSp(c))));
            var lens = new List<int>();
            for (var r = 0; r < R; r++)
            {
                var l = Math.Max(1, (int)Math.Round(nS * (r + 1) / (double)R, MidpointRounding.AwayFromZero));
                if (!lens.Contains(l)) lens.Add(l);
            }
            if (S(Pm, "mode") == "erode") lens.Reverse();
            var rows = lens
                .Select(l => string.Concat(arr.Take(l)).Trim())
                .Where(t => t.Length > 0).ToList();
            var Rn = rows.Count;
            var full = string.Concat(arr);
            const double leadK = 1.34;
            var size = Math.Min(Math.Min(JLayouts.FitSize(full, font, W * 0.74, 1e6, new JObject { ["track"] = 0.04 }),
                H * 0.8 / (Rn * leadK)), H * 0.16);
            var fw = JLayouts.Measure(new JObject { ["text"] = full, ["font"] = font, ["size"] = size, ["track"] = 0.04 }).W;
            var x0 = W / 2 - fw / 2;
            var @out = OutK(env);
            var mainR = S(Pm, "mode") == "erode" ? 0 : Rn - 1;
            BBox? bb = null;
            for (var r = 0; r < Rn; r++)
            {
                var t = rows[r];
                var y = H / 2 + (r - (Rn - 1) / 2.0) * size * leadK;
                var it = new JObject
                    { ["text"] = t, ["font"] = font, ["size"] = size, ["align"] = "left", ["x"] = x0, ["y"] = y, ["track"] = 0.04 };
                var tIn = r * 0.11;
                if (r == mainR)
                {
                    it["color"] = sc.Str("fg");
                    it["mi"] = r * 2.5;
                    bb = JLayouts.MainDraw(env, it);
                }
                else
                {
                    var dist = Math.Abs(r - mainR) / (double)Math.Max(1, Rn - 1);
                    var a = J.Clamp((lt - tIn) / 0.12) * @out * (0.75 - 0.45 * dist);
                    if (a > 0)
                    {
                        it["color"] = sc.Str("sub");
                        it["alpha"] = a;
                        it["ghost"] = false;
                        DrawIt(env, it);
                    }
                }
                if (B(Pm, "idx"))
                {
                    var a = J.Clamp((lt - tIn) / 0.12) * @out;
                    if (a > 0)
                        Label(env, Pad2(JLayouts.GlyphCount(t)), x0 + fw + size * 0.5, y,
                            alpha: a * 0.85, size: Math.Min(LabelSize(env), size * 0.4),
                            color: r == mainR ? AccentOn(sc) : sc.Str("sub"));
                }
            }
            env.Body = bb;
        },
    });

    // ---------------- tyVRuler（JS L1003-1061） ----------------

    private static void RegisterTyVRuler() => Reg0("tyVRuler", new LayoutDef
    {
        Name = "縦目盛り", // JS ae:'vcols'（未注册）
        Tags = new[] { "editorial", "calm", "graphic" },
        W = 0.9,
        Portrait = 1.3,
        Fits = n => n is >= 1 and <= 12,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "serif", "display")),
            ["side"] = rng.Pick(new[] { 1, -1 }),
            ["lab"] = rng.Pick(new[] { "time", "time", "roma" }),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var lt = env.Lt;
            var font = S(Pm, "font")!;
            var side = D(Pm, "side");
            var txt = Regex.Replace(cut.Text.Trim(), @"[\s　]+", "　");
            const double tr = 0.14;
            var size = Math.Min(Math.Min(JLayouts.FitSize(txt, font, W * 0.3, H * 0.78,
                new JObject { ["vertical"] = true, ["track"] = tr }), W * 0.2), H * 0.2);
            var cx = W / 2 - side * size * 0.9;
            var it = new JObject
            {
                ["text"] = txt, ["font"] = font, ["size"] = size, ["x"] = cx, ["y"] = H / 2,
                ["vertical"] = true, ["track"] = tr, ["color"] = sc.Str("fg"),
            };
            var bb = JLayouts.MainDraw(env, it);
            var gl = GlyphPts(it);
            if (gl.Count == 0)
            {
                env.Body = bb;
                return;
            }
            var @out = OutK(env);
            var lw = Hair(env);
            var mono = MonoF(env);
            var rx = cx + side * size * 0.85;
            var y0 = gl[0].Y - size * 0.7;
            var y1 = gl[^1].Y + size * 0.7;
            var e = J.E.OutExpo(J.Clamp(lt / 0.7)) * @out;
            if (e <= 0)
            {
                env.Body = bb;
                return;
            }
            env.Line(new[] { new[] { rx, y0 }, new[] { rx, J.Lerp(y0, y1, e) } }, sc.Str("sub"), lw, 0.8, false);
            var fs = J.Clamp(size * 0.2, 10, 24);
            var n = gl.Count;
            var rom = S(Pm, "lab") == "roma";
            for (var i = 0; i < n; i++)
            {
                var g = gl[i];
                var a = J.E.OutCubic(J.Clamp((lt - 0.1 - i * 0.05) / 0.25)) * @out;
                if (a <= 0) continue;
                env.Line(new[] { new[] { rx, g.Y }, new[] { rx + side * size * 0.3, g.Y } }, sc.Str("sub"), lw, 0.9 * a, false);
                if (i < n - 1)
                {
                    var ym = (g.Y + gl[i + 1].Y) / 2;
                    env.Line(new[] { new[] { rx, ym }, new[] { rx + side * size * 0.14, ym } }, sc.Str("sub"), lw, 0.5 * a, false);
                }
                string? t = null;
                if (rom && (J.IsHira(g.Ch[0]) || J.IsKata(g.Ch[0])))
                    t = (J.Romaji(g.Ch) ?? "").ToUpperInvariant();
                if (string.IsNullOrEmpty(t)) t = J.FmtTime(cut.Start + cut.Dur * i / (double)n);
                Label(env, t, rx + side * size * 0.45, g.Y,
                    alpha: a, size: fs, align: side > 0 ? "left" : "right");
            }
            // 播放头
            var u = J.Clamp(lt / Math.Max(0.3, cut.Dur * 0.92));
            var py = J.Lerp(gl[0].Y, gl[n - 1].Y, u);
            var ts = fs * 0.7;
            var ac = AccentOn(sc);
            env.Poly(new[]
            {
                new[] { rx - side * 2, py },
                new[] { rx - side * (ts * 1.4), py - ts },
                new[] { rx - side * (ts * 1.4), py + ts },
            }, ac, e, false);
            env.Body = bb;
        },
    });

    // ---------------- tyFullTrack（JS L1063-1129） ----------------

    private static void RegisterTyFullTrack() => Reg0("tyFullTrack", new LayoutDef
    {
        Name = "全幅字送り", // JS ae:'center'（未注册）
        Tags = new[] { "editorial", "calm", "graphic" },
        W = 1,
        Fits = n => n is >= 3 and <= 14,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "display", "serif", "body")),
            ["caps"] = rng.Chance(0.8),
            ["rule"] = rng.Chance(0.75),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var lt = env.Lt;
            var port = W < H;
            var font = S(Pm, "font")!;
            var s = SlotsOf(cut.Text);
            var k = s.Count;
            var m0 = port ? H * 0.08 : W * 0.06;
            var size = port
                ? Math.Min(Math.Min(W * 0.24, (H - m0 * 2) / k * 0.8), H * 0.11)
                : Math.Min(H * 0.2, (W - m0 * 2) / k * 0.8);
            var m = m0 + size * 0.5;
            var span = (port ? H : W) - m * 2;
            var @out = OutK(env);
            var lw = Hair(env);
            BBox? bb = null;
            var gi = 0;
            for (var i = 0; i < s.Count; i++)
            {
                var ch = s[i];
                if (IsSp(ch)) continue;
                var u = k > 1 ? i / (double)(k - 1) : 0.5;
                var x = port ? W / 2 : m + span * u;
                var y = port ? m + span * u : H / 2;
                bb = U(bb, JLayouts.MainDraw(env, new JObject
                    { ["text"] = ch, ["font"] = font, ["size"] = size, ["x"] = x, ["y"] = y, ["color"] = sc.Str("fg"), ["mi"] = gi++ }));
            }
            var e = J.E.OutExpo(J.Clamp((lt - 0.05) / 0.8)) * @out;
            if (e > 0)
            {
                if (B(Pm, "rule"))
                {
                    if (port == false)
                    {
                        var y = H / 2 + size * 0.68;
                        env.Line(new[] { new[] { W / 2 - span / 2 * e, y }, new[] { W / 2 + span / 2 * e, y } },
                            sc.Str("sub"), lw, 0.6, false);
                    }
                    else
                    {
                        var x = W / 2 + size * 0.7;
                        env.Line(new[] { new[] { x, H / 2 - span / 2 * e }, new[] { x, H / 2 + span / 2 * e } },
                            sc.Str("sub"), lw, 0.6, false);
                    }
                }
                if (B(Pm, "caps"))
                {
                    var ls = LabelSize(env);
                    var cap = Regex.Replace(string.IsNullOrEmpty(cut.LineText) ? cut.Text : cut.LineText, @"\s+", " ").Trim();
                    if (cap.Length > 40) cap = cap[..40];
                    if (port == false)
                    {
                        Label(env, cap, m, H / 2 - size * 0.62 - ls,
                            alpha: e * 0.9, size: ls, font: BodyF(env), track: 0.14);
                        Label(env, $"No.{Pad2(cut.Line + 1)}  {J.FmtTime(cut.Start)}", W - m, H / 2 - size * 0.62 - ls,
                            alpha: e * 0.9, size: ls, align: "right");
                    }
                    else
                    {
                        Label(env, $"No.{Pad2(cut.Line + 1)}", W / 2 - size * 0.7, m - size * 0.9,
                            alpha: e * 0.9, size: ls, align: "right");
                        Label(env, J.FmtTime(cut.Start), W / 2 + size * 0.7, H - m + size * 0.9,
                            alpha: e * 0.9, size: ls);
                    }
                }
            }
            env.Body = bb;
        },
    });

    // ---------------- tyStatCount（JS L1131-1209） ----------------

    private static void RegisterTyStatCount() => Reg0("tyStatCount", new LayoutDef
    {
        Name = "字数表示", // JS ae:'type'（未注册）
        Tags = new[] { "editorial", "graphic" },
        W = 0.8,
        Fits = n => n is >= 1 and <= 16,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
            ["nf"] = rng.Pick(FontsOf(st, "display", "mono")),
            ["acc"] = rng.Chance(0.5),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var lt = env.Lt;
            var port = W < H;
            var txt = cut.Text.Trim();
            var nG = JLayouts.GlyphCount(txt);
            var font = S(Pm, "font")!;
            var nf = S(Pm, "nf")!;
            var mt = MainLines(txt, W, H, 8, 5);
            var o = new JObject { ["lead"] = 1.2, ["track"] = 0.03 };
            var @out = OutK(env);
            var lw = Hair(env);
            var ls = LabelSize(env);
            var latin = txt.All(c => c <= 0x7f);
            var cnt = (int)Math.Round(nG * J.E.OutCubic(J.Clamp((lt - 0.1) / 0.9)), MidpointRounding.AwayFromZero);
            var e = J.E.OutExpo(J.Clamp((lt - 0.05) / 0.7)) * @out;
            JObject it;
            double nx, ny, ns;
            if (port == false)
            {
                var size = Math.Min(JLayouts.FitSize(mt, font, W * 0.5, H * 0.54, o), H * 0.2);
                it = Merge(o, new JObject
                {
                    ["text"] = mt, ["font"] = font, ["size"] = size,
                    ["align"] = "left", ["x"] = W * 0.08, ["y"] = H / 2, ["color"] = sc.Str("fg"),
                });
                var rx = W * 0.65;
                if (e > 0)
                    env.Line(new[] { new[] { rx, H / 2 - H * 0.22 * e }, new[] { rx, H / 2 + H * 0.22 * e } },
                        sc.Str("sub"), lw, 0.7, false);
                nx = rx + W * 0.04;
                ny = H / 2 - H * 0.05;
                ns = H * 0.26;
            }
            else
            {
                var size = Math.Min(JLayouts.FitSize(mt, font, W * 0.84, H * 0.42, o), W * 0.18);
                it = Merge(o, new JObject
                {
                    ["text"] = mt, ["font"] = font, ["size"] = size,
                    ["align"] = "left", ["x"] = W * 0.08, ["y"] = H * 0.36, ["color"] = sc.Str("fg"),
                });
                var ry = H * 0.62;
                if (e > 0)
                    env.Line(new[] { new[] { W * 0.08, ry }, new[] { W * 0.08 + W * 0.84 * e, ry } },
                        sc.Str("sub"), lw, 0.7, false);
                nx = W * 0.08;
                ny = ry + H * 0.1;
                ns = W * 0.3;
            }
            var bb = JLayouts.MainDraw(env, it);
            ns = Math.Min(ns, (W * 0.93 - nx) / Math.Max(0.3,
                JLayouts.Measure(new JObject { ["text"] = "00", ["font"] = nf, ["size"] = 1 }).W));
            if (e > 0)
            {
                DrawIt(env, new JObject
                {
                    ["text"] = Pad2(cnt), ["font"] = nf, ["size"] = ns,
                    ["align"] = "left", ["x"] = nx, ["y"] = ny,
                    ["color"] = B(Pm, "acc") ? AccentOn(sc) : sc.Str("fg"),
                    ["alpha"] = e, ["ghost"] = false,
                });
                var y2 = ny + ns * 0.62;
                Label(env, latin ? "CHARACTERS" : "文字 / CHARACTERS", nx, y2,
                    alpha: e, size: ls, font: latin ? MonoF(env) : BodyF(env));
                Label(env, $"LINE {Pad2(cut.Line + 1)}  ─  {J.FmtTime(cut.Start)}", nx, y2 + ls * 1.8,
                    alpha: e * 0.8, size: ls * 0.85);
            }
            env.Body = bb;
        },
    });

    // ---------------- tyMargin（JS L1211-1274） ----------------

    private static void RegisterTyMargin() => Reg0("tyMargin", new LayoutDef
    {
        Name = "余白", // JS ae:'center'（未注册）
        Tags = new[] { "calm", "editorial", "emotional" },
        W = 0.8,
        Emph = 0.4,
        Fits = n => n is >= 1 and <= 18,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "serif", "body", "display")),
            ["pos"] = rng.Pick(new[] { "bl", "bl", "tr", "br", "lc" }),
            ["mark"] = rng.Chance(0.7),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var lt = env.Lt;
            var M = Math.Min(W, H);
            var port = W < H;
            var txt = cut.Text.Trim();
            var font = S(Pm, "font")!;
            var mt = JLayouts.GlyphCount(txt) > (port ? 9 : 14) ? SplitL(txt, port ? 8 : 12) : txt;
            var size = Math.Min(M * 0.068, JLayouts.FitSize(mt, font, W * 0.66, H * 0.22,
                new JObject { ["track"] = 0.1, ["lead"] = 1.4 }));
            var pos = S(Pm, "pos") ?? "bl";
            var right = pos is "tr" or "br";
            var y = pos == "tr" ? H * 0.2 : pos == "lc" ? H * 0.5 : H * 0.8;
            var x = right ? W * 0.92 : W * 0.08;
            var it = new JObject
            {
                ["text"] = mt, ["font"] = font, ["size"] = size, ["x"] = x, ["y"] = y,
                ["align"] = right ? "right" : "left",
                ["track"] = 0.1, ["lead"] = 1.4, ["color"] = sc.Str("fg"),
            };
            var bb = JLayouts.MainDraw(env, it);
            var m = JLayouts.Measure(it);
            var @out = OutK(env);
            var lw = Hair(env);
            var ls = LabelSize(env) * 0.9;
            var e = J.E.OutExpo(J.Clamp((lt - 0.2) / 0.9)) * @out;
            if (e > 0)
            {
                var tx0 = right ? x - m.W : x;
                var tx1 = right ? x : x + m.W;
                var ly = y - m.H / 2 - ls * 1.4;
                Label(env, $"{Pad2(cut.Line + 1)}  —  {J.FmtTime(cut.Start)}", x, ly,
                    alpha: e * 0.9, size: ls, align: right ? "right" : "left");
                var a0 = right ? tx0 - size * 0.8 : tx1 + size * 0.8;
                var a1 = right ? W * 0.08 : W * 0.92;
                if ((a1 - a0) * (right ? -1 : 1) > W * 0.05)
                {
                    var yl = y + (JLayouts.GlyphCount(mt) > 0 ? 0 : 0); // JS 原样（恒 0）
                    env.Line(new[] { new[] { a0, yl }, new[] { J.Lerp(a0, a1, e), yl } }, sc.Str("sub"), lw, 0.6, false);
                    if (B(Pm, "mark"))
                    {
                        var q = size * 0.16;
                        env.Rect(J.Lerp(a0, a1, e) - q / 2, yl - q / 2, q, q, AccentOn(sc), e, false);
                    }
                }
                var rom = RomaOf(txt);
                if (rom != null)
                    Label(env, rom, x, y + m.H / 2 + ls * 1.3,
                        alpha: e * 0.75, size: ls * 0.9, align: right ? "right" : "left", track: 0.3);
            }
            env.Body = bb;
        },
    });

    // ---------------- tyRotBlock（JS L1276-1371） ----------------

    private static void RegisterTyRotBlock() => Reg0("tyRotBlock", new LayoutDef
    {
        Name = "回転ブロック", // JS ae:'sideways'（未注册）
        Tags = new[] { "graphic", "pop", "editorial" },
        W = 1,
        Fits = n => n is >= 4 and <= 16,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "display")),
            ["fb"] = rng.Pick(FontsOf(st, "display", "serif")),
            ["rot"] = rng.Pick(new[] { -90, -90, 90 }),
            ["accent"] = rng.Chance(0.5),
            ["rule"] = rng.Chance(0.8),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var lt = env.Lt;
            var port = W < H;
            var font = S(Pm, "font")!;
            var fb = S(Pm, "fb")!;
            var arr = SlotsOf(cut.Text);
            var n = arr.Count;
            var ws = cut.Words.Select(w => w).ToList();
            var cutAt = Math.Max(1, (int)Math.Round(n * 0.36, MidpointRounding.AwayFromZero));
            if (ws.Count >= 2)
            {
                var l0 = JLayouts.CodePoints(ws[0].Trim()).Count;
                if (l0 >= 1 && l0 <= Math.Ceiling(n * 0.55)) cutAt = l0;
            }
            while (cutAt < n - 1 && (J.IsSmallKana(arr[cutAt][0]) || J.IsPunct(arr[cutAt][0]))) cutAt++;
            var tA = string.Concat(arr.Take(cutAt)).Trim(); // JS 局部名 A/B 与本文件帮手撞名，改名 tA/tB
            if (tA.Length == 0) tA = arr[0];
            var tB = string.Concat(arr.Skip(cutAt)).Trim();
            if (tB.Length == 0) tB = arr[n - 1];
            var labelA = false;
            if (IsLatinT(cut.Text)) // 拉丁文：在词间空格处切；只有一个词时旋转块退化为行号小标签
            {
                var t = cut.Text.Trim();
                var sp = t.IndexOf(' ');
                if (sp > 0)
                {
                    tA = t[..sp];
                    tB = t[(sp + 1)..].Trim();
                }
                else
                {
                    tA = $"LINE {Pad2(cut.Line + 1)}";
                    tB = t;
                    labelA = true;
                }
            }
            var Hb = port ? H * 0.44 : H * 0.62;
            var wA100 = JLayouts.Measure(new JObject { ["text"] = tA, ["font"] = font, ["size"] = 100, ["track"] = 0.02 }).W;
            var sizeA = Math.Min(Math.Min(Hb / wA100 * 100, port ? W * 0.3 : W * 0.24),
                labelA ? Math.Min(W, H) * 0.06 : 1e9);
            var gap = sizeA * 0.28;
            var Wb = (port ? W * 0.86 : Math.Min(W * 0.84, Hb * 2.4)) - sizeA - gap;
            (string Bt, double S)? best = null;
            var nB = JLayouts.GlyphCount(tB);
            for (var Lc = 1; Lc <= 3; Lc++)
            {
                if (Lc > nB) break;
                var bt = Lc == 1 ? tB : SplitL(tB, (int)Math.Ceiling(nB / (double)Lc));
                if (Lc > 1 && IsLatinT(tB))
                {
                    var bw = Regex.Split(tB, @"\s+");
                    if (bw.Length < Lc) break;
                    bt = string.Join("\n", GroupLines(bw.Select((w, i) => (i != 0 ? " " : "") + w).ToList(), Lc));
                }
                var s = Math.Min(JLayouts.FitSize(bt, fb, Wb, Hb, new JObject { ["lead"] = 1.08, ["track"] = 0.02 }), Hb * 0.6);
                if (best == null || s > best.Value.S * 1.05) best = (bt, s);
            }
            var bestBt = best!.Value.Bt;
            var bestS = best.Value.S;
            var mB = JLayouts.Measure(new JObject { ["text"] = bestBt, ["font"] = fb, ["size"] = bestS, ["lead"] = 1.08, ["track"] = 0.02 });
            var wA = wA100 * sizeA / 100;
            var tot = sizeA + gap + mB.W;
            var xA = W / 2 - tot / 2 + sizeA / 2;
            var xB = xA + sizeA / 2 + gap;
            var @out = OutK(env);
            var lw = Hair(env);
            var bh = Math.Max(wA, mB.H);
            if (B(Pm, "rule"))
            {
                var e = J.E.OutExpo(J.Clamp((lt - 0.1) / 0.6)) * @out;
                if (e > 0)
                    env.Line(new[]
                    {
                        new[] { xA + sizeA / 2 + gap / 2, H / 2 - bh / 2 * e },
                        new[] { xA + sizeA / 2 + gap / 2, H / 2 + bh / 2 * e },
                    }, sc.Str("sub"), lw, 0.7, false);
            }
            var itA = new JObject
            {
                ["text"] = tA, ["font"] = font, ["size"] = sizeA,
                ["x"] = xA, ["y"] = H / 2, ["rot"] = D(Pm, "rot"),
                ["track"] = 0.02,
                ["color"] = B(Pm, "accent") ? AccentOn(sc) : sc.Str("fg"),
                ["mi"] = 0,
            };
            if (labelA)
            {
                itA["font"] = MonoF(env);
                itA["color"] = sc.Str("sub");
                itA["alpha"] = J.E.OutCubic(J.Clamp(lt / 0.4)) * @out;
                itA["ghost"] = false;
                DrawIt(env, itA);
            }
            else JLayouts.MainDraw(env, itA);
            JLayouts.MainDraw(env, new JObject
            {
                ["text"] = bestBt, ["font"] = fb, ["size"] = bestS,
                ["x"] = xB, ["y"] = H / 2, ["align"] = "left",
                ["lead"] = 1.08, ["track"] = 0.02, ["color"] = sc.Str("fg"), ["mi"] = 3,
            });
            env.Body = BbRect(xA - sizeA / 2, H / 2 - bh / 2, xB + mB.W, H / 2 + bh / 2);
        },
    });

    // ---------------- tySquare（JS L1373-1436） ----------------

    private static void RegisterTySquare() => Reg0("tySquare", new LayoutDef
    {
        Name = "方形組", // JS ae:'gridCells'（未注册）
        Tags = new[] { "graphic", "editorial", "pop" },
        W = 1,
        Fits = n => n is >= 3 and <= 16,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
            ["order"] = rng.Pick(new[] { "yoko", "yoko", "tate" }),
            ["frame"] = rng.Chance(0.75),
            ["acc"] = rng.Chance(0.7),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var lt = env.Lt;
            var M = Math.Min(W, H);
            var chars = JLayouts.CodePoints(Clean(env.Cut!.Text));
            var n = chars.Count;
            if (n == 0)
            {
                env.Body = null;
                return;
            }
            var font = S(Pm, "font")!;
            var tate = S(Pm, "order") == "tate";
            var a = (int)Math.Ceiling(Math.Sqrt(n));
            var b = (int)Math.Ceiling(n / (double)a);
            var cols = tate ? b : a;
            var rows = tate ? a : b;
            var cell = Math.Min(Math.Min(W * 0.78 / cols, H * 0.74 / rows), M * 0.3);
            var x0 = W / 2 - cols * cell / 2;
            var y0 = H / 2 - rows * cell / 2;
            var ki = KeyIndex(chars, "kanji");
            var @out = OutK(env);
            var lw = Hair(env);
            var ls = LabelSize(env);
            BBox? bb = null;
            (int C, int Rw) Pos(int i) =>
                tate ? (cols - 1 - i / rows, i % rows) : (i % cols, i / cols);
            for (var i = 0; i < cols * rows; i++)
            {
                var (c, r) = Pos(i);
                var x = x0 + (c + 0.5) * cell;
                var y = y0 + (r + 0.5) * cell;
                if (i < n)
                    bb = U(bb, JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = chars[i], ["font"] = font, ["size"] = cell * 0.9,
                        ["x"] = x, ["y"] = y, ["vertical"] = tate,
                        ["color"] = B(Pm, "acc") && i == ki ? AccentOn(sc) : sc.Str("fg"), ["mi"] = i,
                    }));
                else
                {
                    var q = J.E.OutBack(J.Clamp((lt - 0.2 - i * 0.03) / 0.3), 2) * @out;
                    var s = cell * 0.08;
                    if (q > 0)
                    {
                        env.Line(new[] { new[] { x - s * q, y }, new[] { x + s * q, y } }, sc.Str("sub"), lw, 0.8, false);
                        env.Line(new[] { new[] { x, y - s * q }, new[] { x, y + s * q } }, sc.Str("sub"), lw, 0.8, false);
                    }
                }
            }
            if (B(Pm, "frame"))
            {
                var e = J.E.OutExpo(J.Clamp((lt - 0.05) / 0.7)) * @out;
                if (e > 0)
                {
                    var xa = x0;
                    var xb = x0 + cols * cell;
                    var g = cell * 0.12;
                    env.Line(new[] { new[] { xa, y0 - g }, new[] { J.Lerp(xa, xb, e), y0 - g } },
                        sc.Str("fg"), Math.Max(2, lw * 2.4), 0.9, false);
                    env.Line(new[] { new[] { xb, y0 + rows * cell + g }, new[] { J.Lerp(xb, xa, e), y0 + rows * cell + g } },
                        sc.Str("sub"), lw, 0.8, false);
                    Label(env, $"{cols}×{rows}", xb, y0 + rows * cell + g + ls * 1.1,
                        align: "right", alpha: e, size: ls * 0.9);
                    Label(env, tate ? "縦組" : "YOKO", xa, y0 + rows * cell + g + ls * 1.1,
                        alpha: e * 0.8, size: ls * 0.9, font: tate ? BodyF(env) : MonoF(env));
                }
            }
            env.Body = bb;
        },
    });

    // ---------------- tyLineFocus（JS L1438-1529） ----------------

    private static void RegisterTyLineFocus() => Reg0("tyLineFocus", new LayoutDef
    {
        Name = "行中強調", // JS ae:'center'（未注册）
        Tags = new[] { "editorial", "emotional", "calm" },
        W = 1,
        Fits = n => n is >= 1 and <= 16,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "serif", "display", "body")),
            ["mark"] = rng.Pick(new[] { "bar", "bar", "dot", "box" }),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var lt = env.Lt;
            var M = Math.Min(W, H);
            var port = W < H;
            var font = S(Pm, "font")!;
            var cutT = cut.Text.Trim();
            var line = (string.IsNullOrEmpty(cut.LineText) ? cutT : cut.LineText).Trim();
            if (JLayouts.GlyphCount(line) > 36) line = cutT;
            int NsI(string s, int end) =>
                JLayouts.CodePoints(s).Take(end).Count(c => !char.IsWhiteSpace(c[0]));
            double s0, s1;
            var same = false;
            var at = line.IndexOf(cutT, StringComparison.Ordinal);
            if (at < 0 || line == cutT)
            {
                line = cutT;
                same = true;
                var arr = JLayouts.CodePoints(line);
                var ki = KeyIndex(arr, "kanji");
                var ke = ki;
                if (ki < arr.Count && J.IsKanji(arr[ki][0]))
                {
                    while (ke + 1 < arr.Count && J.IsKanji(arr[ke + 1][0])) ke++;
                }
                else
                {
                    while (ke + 1 < arr.Count && ke - ki < 2 && !IsSp(arr[ke + 1]) && !J.IsPunct(arr[ke + 1][0])) ke++;
                }
                if (IsLatinT(line))
                {
                    string? best = null;
                    var bestAt = 0;
                    foreach (Match mm in Regex.Matches(line, @"\S+"))
                        if (best == null || mm.Value.Length > best.Length)
                        {
                            best = mm.Value;
                            bestAt = mm.Index;
                        }
                    if (best != null)
                    {
                        ki = JLayouts.CodePoints(line[..bestAt]).Count;
                        ke = ki + JLayouts.CodePoints(best).Count - 1;
                    }
                }
                s0 = NsI(line, ki);
                s1 = NsI(line, ke + 1);
            }
            else
            {
                var pre = JLayouts.CodePoints(line[..at]).Count;
                s0 = NsI(line, pre);
                s1 = s0 + JLayouts.GlyphCount(cutT);
            }
            var nAll = JLayouts.GlyphCount(line);
            var mt = nAll > (port ? 7 : 12)
                ? SplitL(line, (int)Math.Ceiling(nAll / Math.Ceiling(nAll / (double)(port ? 6 : 11))))
                : line;
            var o = new JObject { ["lead"] = 1.7, ["track"] = 0.08 };
            var size = Math.Min(JLayouts.FitSize(mt, font, W * 0.82, H * 0.56, o), M * 0.12);
            var it = Merge(o, new JObject { ["text"] = mt, ["font"] = font, ["size"] = size, ["x"] = W / 2, ["y"] = H / 2 });
            var gl = GlyphPts(it);
            var @out = OutK(env);
            var ac = AccentOn(sc);
            var dimA = J.E.OutCubic(J.Clamp(lt / 0.3)) * @out;
            BBox? bb = null;
            var j = 0;
            var spanBy = new SortedDictionary<int, (double A, double B, List<GlyphPt> G)>();
            for (var i = 0; i < gl.Count; i++)
            {
                var g = gl[i];
                if (i >= s0 && i < s1)
                {
                    bb = U(bb, JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = g.Ch, ["font"] = font, ["size"] = size,
                        ["x"] = g.X, ["y"] = g.Y, ["color"] = sc.Str("fg"), ["mi"] = j++,
                    }));
                    if (!spanBy.TryGetValue(g.Li, out var Lv))
                        Lv = (1e9, -1e9, new List<GlyphPt>());
                    Lv.A = Math.Min(Lv.A, g.X - g.W / 2);
                    Lv.B = Math.Max(Lv.B, g.X + g.W / 2);
                    Lv.G.Add(g);
                    spanBy[g.Li] = Lv;
                }
                else if (dimA > 0)
                    DrawIt(env, new JObject
                    {
                        ["text"] = g.Ch, ["font"] = font, ["size"] = size,
                        ["x"] = g.X, ["y"] = g.Y,
                        ["color"] = same ? sc.Str("fg") : sc.Str("sub"),
                        ["alpha"] = dimA * (same ? 0.5 : 0.42),
                        ["ghost"] = false,
                    });
            }
            var k = 0;
            foreach (var Lv in spanBy.Values)
            {
                var q = J.E.OutExpo(J.Clamp((lt - 0.25 - k * 0.1) / 0.5)) * @out;
                k++;
                if (q <= 0) continue;
                var y = Lv.G[0].Y;
                var mark = S(Pm, "mark");
                if (mark == "bar")
                    env.Rect(Lv.A, y + size * 0.62, (Lv.B - Lv.A) * q, Math.Max(3, size * 0.07), ac, 1, false);
                else if (mark == "dot")
                    foreach (var g in Lv.G)
                        env.Circle(g.X, g.Y - size * 0.72, size * 0.07 * q, ac, null, 0, 1, false);
                else
                    env.RRect(Lv.A - size * 0.14, y - size * 0.64, Lv.B - Lv.A + size * 0.28, size * 1.28, 0,
                        null, q, false, ac, Math.Max(1.5, size * 0.03));
            }
            env.Body = bb ?? BbRect(W * 0.3, H * 0.4, W * 0.7, H * 0.6);
        },
    });

    private static int I(JObject p, string k, int d = 0) => JLayouts.I(p, k, d);
}
