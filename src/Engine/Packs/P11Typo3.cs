// 移植自 frontend/src/engine/11p_typo3.js（排版装饰 + 文字 treat + 两个打字机式转场，
// pack = 'typo3'：6 decor + 4 treat + 2 trans）。
//
// 契约与 JDecor.cs / P11Decor、P11TreatTrans 相同：
// · decor：DecorDef.Draw(env, DecorSpec)，bb 从 env.Body 读（空时 getBB 回退画面中央 + 按 cut
//   缓存最近有效框）；参数 Pd → DecorSpec（right/low/v/n/accent…planner 的 DecorParams 供给）。
// · treat：TreatDef.Draw(env) 在 MainDraw 的 treat 调用点执行；env.It 是当前 item、
//   env.Cut.TreatP 是 plan 参数（键名 = JS plan 对象字段，一字不改）；charFns.push → env.CharFns.Add。
// · trans：TransDef.Draw = TransDrawFn(ctx, A, B, p, TransCtx)（Renderer.cs M2 定型）；trReg 的
//   p<=0 精确输出 A / p>=1 精确输出 B 包装本地重做（P11TreatTrans.Wrap 私有，不跨文件引用）。
//
// 源文件 reg/trReg 的 Object.assign({set:'typo'}, def)：`set`（11q_sets 手法集归属）与 `ae`
// （AE 导出模板名）在 Def 类上没有对应属性，未注册——TODO(Sets/11_export)。
//
// 管线缺口与降级（本文件内解决，与 P11TreatTrans 同约定，不改共享文件）：
// 1. addPost：已接管线钩子（Env.PostFn → JLayouts.DrawFx，画在主文字上方；AddPost 链式）。
// 2. lineSpans / tyIndexSup 的 it.charFn：apply 时管线尚未 CombineChar，读 env.CharFn（此时为
//    null）→ 字形按静态布局位置取（JS 里该 fn 也只影响藏字判定）。
// 3. tyColophon 的 cornerSpot 传 {…Pd, low: Pd.low !== false}：planner 的 DecorParams 恒填
//    bool low，`!== false` 恒等于原值——直接传 Pd。
// 4. trans 的 part()：JS drawImage 九参裁剪拷贝 → SKCanvas.DrawImage(srcRect, dstRect)。
//
// 跨文件符号对位：J.layoutText→JText.LayoutText、J.measure→JLayouts.Measure、J.centerBB→
// JLayouts.CenterBb、J.romaji/fmtTime/glyphCount/isKanji/isPunct/isSmallKana/mix/contrast→J.*、
// env.st.fonts[role][0]→JLayouts.Font0、rng.*→Rng.*。

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace JizuraLive.Engine;

public static class P11Typo3
{
    private const string P = "typo3";
    private static int _installed;

    // JS reg 助手：Object.assign({set: SET('typo')}, def)
    private static void RegD(string key, DecorDef def) { def.Set = "typo"; Reg.Register("decor", key, def, P); }
    private static void RegT(string key, TreatDef def) { def.Set = "typo"; Reg.Register("treat", key, def, P); }
    private static void RegX(string key, TransDef def) { def.Set = "typo"; Reg.Register("trans", key, def, P); }

    // ================= 本包 helper（JS L10-88 + L316-366 + L492-515 逐条对位） =================

    private static bool IsSp(string c) => c is " " or "　";

    private static double U(Env env) => Math.Min(env.W, env.H) / 1080.0;
    private static double MG(Env env) => Math.Round(Math.Min(env.W, env.H) * 0.05, MidpointRounding.AwayFromZero);
    private static string MonoF(Env env) => JLayouts.Font0(env.St, "mono") ?? "mono";
    private static string BodyF(Env env) => JLayouts.Font0(env.St, "body") ?? "gothic_med";
    private static string SerifF(Env env) => JLayouts.Font0(env.St, "serif") ?? "mincho";

    /// <summary>outE(env)：退场剩余量 1 - inCubic(pOut)。JS L14。</summary>
    private static double OutE(Env env) => 1 - J.E.InCubic(env.POut);

    /// <summary>inE(env, d, delay)：outExpo 进场。JS L15。</summary>
    private static double InE(Env env, double d = 0.4, double delay = 0) =>
        J.E.OutExpo(J.Clamp((env.Lt - delay) / d));

    private static string Pad2(double n) => ((long)n).ToString(CultureInfo.InvariantCulture).PadLeft(2, '0');

    /// <summary>FS(env)：注记基准字号。JS L17。</summary>
    private static double FS(Env env) => Math.Max(11, 16 * U(env));

    private static string AccentOf(Scheme sc) =>
        J.Contrast(sc.Str("accent") ?? "", sc.Str("bg") ?? "") >= 1.8 ? sc.Str("accent")! : sc.Str("fg")!;

    private static double Bw(BBox bb) => bb.X1 - bb.X0;
    private static double Bh(BBox bb) => bb.Y1 - bb.Y0;

    private static bool HitBB(double x0, double y0, double x1, double y1, BBox bb, double pad = 0) =>
        !(x1 < bb.X0 - pad || x0 > bb.X1 + pad || y1 < bb.Y0 - pad || y0 > bb.Y1 + pad);

    // 记住 cut 最近一次的真实 bbox，歌词隐藏时 decor 不至于跳位（JS WeakMap → CWT）
    private static readonly ConditionalWeakTable<Cut, BBox> Bbc = new();

    private static BBox GetBB(Env env, BBox? bb)
    {
        var sum = bb != null ? bb.X0 + bb.X1 + bb.Y0 + bb.Y1 : double.NaN;
        if (bb != null && !double.IsNaN(sum) && !double.IsInfinity(sum) && bb.X1 > bb.X0 && bb.Y1 > bb.Y0)
        {
            var b = new BBox
            {
                X0 = Math.Max(bb.X0, -env.W * 0.1),
                X1 = Math.Min(bb.X1, env.W * 1.1),
                Y0 = Math.Max(bb.Y0, -env.H * 0.1),
                Y1 = Math.Min(bb.Y1, env.H * 1.1),
                Boxes = bb.Boxes ?? new List<JObject>(),
                Cx = bb.Cx,
                Cy = bb.Cy,
            };
            if (env.Cut != null)
            {
                Bbc.Remove(env.Cut);
                Bbc.Add(env.Cut, b);
            }
            return b;
        }
        var c = env.Cut != null && Bbc.TryGetValue(env.Cut, out var hit) ? hit : null;
        return c ?? JLayouts.CenterBb(env, null);
    }

    private sealed class Spot
    {
        public double X, Y;
        public bool Ok;
        public int Sx = -1, Sy = -1;
    }

    /// <summary>边距内一块不压到歌词的屏幕角落；ok=false 表示四个角都放不下。JS cornerSpot L43-51。</summary>
    private static Spot CornerSpot(Env env, BBox bb, double w, double h, DecorSpec p)
    {
        var W = (double)env.W;
        var H = (double)env.H;
        var m = MG(env);
        var sx0 = p.Right ? 1 : -1;
        var sy0 = p.Low ? 1 : -1;
        foreach (var (sx, sy) in new[] { (sx0, sy0), (-sx0, sy0), (sx0, -sy0), (-sx0, -sy0) })
        {
            var x = sx > 0 ? W - m - w : m;
            var y = sy > 0 ? H - m - h : m;
            if (!HitBB(x, y, x + w, y + h, bb, 14 * U(env)))
                return new Spot { X = x, Y = y, Ok = true, Sx = sx, Sy = sy };
        }
        return new Spot { X = m, Y = m, Ok = false, Sx = -1, Sy = -1 };
    }

    /// <summary>label：小注记（只走主通道 env.draw）。JS L53-64。</summary>
    private static void Label(Env env, string text, double x, double y,
        double? size = null, string? font = null, string? align = null, double? track = null,
        string? color = null, double? alpha = null)
    {
        DrawIt(env, new JObject
        {
            ["text"] = text,
            ["font"] = font ?? MonoF(env),
            ["size"] = size ?? FS(env),
            ["x"] = x,
            ["y"] = y,
            ["align"] = align ?? "left",
            ["track"] = track ?? 0.12,
            ["color"] = color ?? env.Sc?.Str("sub") ?? "#9A9A9A",
            ["alpha"] = alpha ?? 1,
            ["ghost"] = false,
        });
    }

    private static double TextW(string text, string font, double size, double track = 0.12) =>
        JLayouts.Measure(new JObject { ["text"] = text, ["font"] = font, ["size"] = size, ["track"] = track }).W;

    /// <summary>keyChar：歌词的关键字。JS L66-70。</summary>
    private static string? KeyChar(string? text)
    {
        var arr = JLayouts.CodePoints(text ?? "");
        var k = arr.FirstOrDefault(c => c.Length > 0 && J.IsKanji(c[0]))
             ?? arr.FirstOrDefault(c => !IsSp(c) && !J.IsPunct(c[0]) && !J.IsSmallKana(c[0]))
             ?? arr.FirstOrDefault(c => !IsSp(c));
        return string.IsNullOrEmpty(k) ? null : k;
    }

    /// <summary>歌词的字形边缘位置（横排取 x），取自 bbox 的 glyph boxes，缺了就均匀铺。JS L72-88。</summary>
    private static List<double> GlyphEdges(Env env, BBox bb)
    {
        var @out = new List<double>();
        if (bb.Boxes is { Count: > 0 } && bb.Boxes.Count <= 40)
        {
            foreach (var b in bb.Boxes)
            {
                var x0 = bb.Cx + JAnim.Num(b, "x") - JAnim.Num(b, "w") / 2;
                var x1 = bb.Cx + JAnim.Num(b, "x") + JAnim.Num(b, "w") / 2;
                if (x0 > bb.X0 - 4 && x1 < bb.X1 + 4)
                {
                    @out.Add(x0);
                    @out.Add(x1);
                }
            }
        }
        if (@out.Count < 2)
        {
            var n = Math.Max(1, Math.Min(16, JLayouts.GlyphCount(env.Cut?.Text ?? "")));
            for (var i = 0; i <= n; i++) @out.Add(J.Lerp(bb.X0, bb.X1, i / (double)n));
        }
        @out.Sort();
        var ded = new List<double>();
        foreach (var x in @out)
            if (ded.Count == 0 || x - ded[^1] > 3)
                ded.Add(x);
        return ded.Take(34).ToList();
    }

    /// <summary>env.draw 对位：直接画一条 item（不走 mainDraw 流水线；防残留 fn 串扰同 P11LayoutsB）。</summary>
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

    // ---------------- treat 侧 helper（JS L316-366） ----------------

    private static bool IsFalse(JToken? t) => t is JValue v && v.Type == JTokenType.Boolean && !(bool)v.Value!;

    /// <summary>alive(it, amin)。JS L316。</summary>
    private static bool Alive(JObject it, double amin = 0.9) =>
        !IsFalse(it["fill"]) && JAnim.NumNull(it, "alpha", 1) >= amin
        && !string.IsNullOrEmpty(JLayouts.S(it, "text")) && JAnim.Num(it, "size") > 1;

    /// <summary>
    /// addPost 的对位：钩子挂 env.PostFn（JLayouts.DrawFx 在主文字之后消费，层级与 JS 一致；链式）。
    /// </summary>
    private static void AddPost(Env env, JObject it, Action<Env, JObject> fn)
    {
        var prev = env.PostFn;
        env.PostFn = prev != null
            ? (e, x, bb) => { prev(e, x, bb); fn(e, x); }
            : (Action<Env, JObject, BBox?>)((e, x, _) => fn(e, x));
    }

    /// <summary>nonSp(it)：非空白字数。JS L324。</summary>
    private static int NonSp(JObject it) =>
        JLayouts.CodePoints(JLayouts.S(it, "text") ?? "").Count(c => !char.IsWhiteSpace(c[0]));

    private sealed class Span3
    {
        public int Li;
        public double A0, A1, C;
    }

    /// <summary>每行的范围，item 空间（静态 layout；charFn 读 env.CharFn——见文件头注 2）。JS L326-344。</summary>
    private static List<Span3> LineSpans(Env env, JObject it)
    {
        var lay = JText.LayoutText(it);
        var sx = JAnim.NumOr1(it, "sx");
        var sy = JAnim.NumOr1(it, "sy");
        var v = JLayouts.Truthy(it["vertical"]);
        var map = new SortedDictionary<int, Span3>();
        foreach (var g in lay.Cells)
        {
            if (IsSp(g.Ch)) continue;
            if (env.CharFn != null)
            {
                var c = env.CharFn(g.I, JText.CellJson(g), lay.N);
                if (c != null && (c.Hide || (c.A != null && c.A.Value < 0.05))) continue;
            }
            var a0 = v ? (g.Y - g.H / 2) * sy : (g.X - g.W / 2) * sx;
            var a1 = v ? (g.Y + g.H / 2) * sy : (g.X + g.W / 2) * sx;
            if (!map.TryGetValue(g.Li, out var L))
                map[g.Li] = new Span3 { Li = g.Li, A0 = a0, A1 = a1, C = v ? g.X * sx : g.Y * sy };
            else
            {
                L.A0 = Math.Min(L.A0, a0);
                L.A1 = Math.Max(L.A1, a1);
            }
        }
        return map.Values.ToList();
    }

    /// <summary>itemSpace：clip/平移/旋转/skew 的 item 本地空间。JS L345-366。
    /// clip/clipY 与 JLayouts.DrawFx 是同一份 JS `ctx.rect(x, y, w, h)` 的两个移植点：
    /// SKRect 要 left/top/right/bottom，右/下边界必须是绝对坐标（写成 w/h 会让夹取带反向）。</summary>
    private static void ItemSpace(Env env, JObject it, Action fn)
    {
        var ctx = env.Ctx;
        ctx.Save();
        if (it["clip"] is JArray clip)
            ctx.ClipRect(new SKRect((float)(double)clip[0]!, (float)-env.H,
                (float)(double)clip[1]!, (float)(env.H * 2)));
        if (it["clipY"] is JArray clipY)
            ctx.ClipRect(new SKRect((float)-env.W, (float)(double)clipY[0]!,
                (float)(env.W * 2), (float)(double)clipY[1]!));
        ctx.Translate((float)JAnim.Num(it, "x"), (float)JAnim.Num(it, "y"));
        var rot = JAnim.Num(it, "rot");
        if (rot != 0) ctx.RotateDegrees((float)rot);
        var skew = JAnim.Num(it, "skew");
        if (skew != 0) ctx.Skew((float)Math.Tan(skew * J.DEG), 0);
        try
        {
            fn();
        }
        finally
        {
            ctx.Restore();
        }
    }

    // ---------------- trans 侧 helper（JS L492-515） ----------------

    private static double Bell(double k) => Math.Sin(Math.PI * J.Clamp(k));

    /// <summary>part()：drawImage 九参裁剪拷贝 → DrawImage(srcRect→dstRect)。JS L493-498。</summary>
    private static void Part(SKCanvas ctx, SKImage img, double x, double y, double w, double h, int cw, int ch)
    {
        var x0 = Math.Max(0, (int)Math.Floor(x));
        var y0 = Math.Max(0, (int)Math.Floor(y));
        var x1 = Math.Min(cw, (int)Math.Ceiling(x + w));
        var y1 = Math.Min(ch, (int)Math.Ceiling(y + h));
        if (x1 - x0 < 1 || y1 - y0 < 1) return;
        ctx.DrawImage(img, new SKRect(x0, y0, x1, y1), new SKRect(x0, y0, x1, y1));
    }

    /// <summary>tAcc(I)：转场里在前后两版配色上都可读的强调色。JS L499-503。</summary>
    private static string TAcc(TransCtx i)
    {
        var sc = i.Sc;
        var pb = i.ScPrev.Str("bg") ?? sc.Str("bg") ?? "";
        foreach (var c in new[] { sc.Str("accent"), sc.Str("accent2"), sc.Str("fg") })
            if (!string.IsNullOrEmpty(c) && J.Contrast(c, sc.Str("bg") ?? "") >= 1.8 && J.Contrast(c, pb) >= 1.4)
                return c!;
        return sc.Str("fg") ?? "#FFFFFF";
    }

    /// <summary>trReg 包装：p<=0 精确输出 A、p>=1 精确输出 B，结束时 ctx 状态干净（JS L504-515）。</summary>
    private static TransDrawFn Wrap(Action<SKCanvas, SKImage, SKImage, double, TransCtx, JObject> draw) =>
        (ctx, a, b, p, i) =>
        {
            ctx.Save();
            try
            {
                if (!(p > 0)) ctx.DrawImage(a, 0, 0);
                else if (p >= 1) ctx.DrawImage(b, 0, 0);
                else draw(ctx, a, b, p, i, i.P ?? new JObject());
            }
            finally
            {
                ctx.Restore();
            }
        };

    // 松散读取
    private static string? S(JObject p, string k) => JLayouts.S(p, k);
    private static double D(JObject p, string k, double d = 0) => JLayouts.D(p, k, d);
    private static int I(JObject p, string k, int d = 0) => JLayouts.I(p, k, d);
    private static bool Bo(JObject p, string k) => JLayouts.B(p, k);

    // ================= 注册（JS 源 reg/trReg 出现序） =================

    /// <summary>幂等注册本文件全部条目（顺序 = JS 源出现序；不挂静态构造函数）。</summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;
        RegisterTyColophon();    // JS L91
        RegisterTyRunningHead(); // JS L128
        RegisterTyGlyphBody();   // JS L160
        RegisterTyTextRule();    // JS L193
        RegisterTyTypeScale();   // JS L235
        RegisterTyBigPunct();    // JS L284
        RegisterTyHollowKey();   // JS L369
        RegisterTyHeadRules();   // JS L391
        RegisterTyHeadBig();     // JS L417
        RegisterTyIndexSup();    // JS L450
        RegisterTyRuleWipe();    // JS L518
        RegisterTyGridCells();   // JS L547
    }

    // ---------------- decor: tyColophon（JS L91-125）奥付 ----------------

    private static void RegisterTyColophon() => RegD("tyColophon", new DecorDef
    {
        Name = "奥付", // JS ae:'leaders'（未注册）
        Tags = new[] { "editorial", "calm", "graphic" },
        W = 1,
        Layer = "front",
        Draw = (env, p) =>
        {
            if (env.Pass != "main") return;
            var bb = GetBB(env, env.Body);
            var sc = env.Sc!;
            var cut = env.Cut!;
            var u = U(env);
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0) return;
            var fs = FS(env) * 1.1;
            var lead = fs * 1.7;
            var txt = (cut.Text ?? "").Trim();
            var raw = Regex.Replace(txt, @"\s+", "");
            var rj = J.Romaji(raw);
            var rom = !string.IsNullOrEmpty(rj) && Regex.IsMatch(raw, "[ぁ-ヿ]") ? rj!.ToUpperInvariant() : null;
            var lt = Regex.Replace(string.IsNullOrEmpty(cut.LineText) ? txt : cut.LineText, @"\s+", " ").Trim();
            string Trunc(string t, int max) => t.Length > max ? t[..max] + "…" : t;
            var lines = new List<(string T, string F, string? C)>
            {
                (Trunc(lt, 18), BodyF(env), sc.Str("fg")),
                ($"No.{Pad2(cut.Line + 1)}  {J.FmtTime(cut.Start)} – {J.FmtTime(cut.End)}", MonoF(env), sc.Str("sub")),
                ($"{JLayouts.GlyphCount(txt)} CHARS"
                 + (rom != null ? "  /  " + (rom.Length > 16 ? rom[..15] + "…" : rom) : ""), MonoF(env), sc.Str("sub")),
            };
            var w = 0.0;
            foreach (var (t, f, _) in lines) w = Math.Max(w, TextW(t, f, fs));
            var h = lead * lines.Count;
            // JS {…Pd, low: Pd.low !== false}：planner 恒填 bool low，等价直接传 Pd
            var sp = CornerSpot(env, bb, w + fs * 1.4, h, p);
            if (!sp.Ok) return;
            var x = sp.X + fs * 1.4;
            var e = InE(env, 0.6) * o;
            env.Line(new[] { new[] { sp.X, sp.Y }, new[] { sp.X, sp.Y + h * e } },
                AccentOf(sc), Math.Max(1.5, 2 * u), o, false);
            for (var i = 0; i < lines.Count; i++)
            {
                var (t, f, c) = lines[i];
                var arr = JLayouts.CodePoints(t);
                var k = (int)Math.Floor(arr.Count * J.Clamp((env.Lt - 0.1 - i * 0.14) / 0.45));
                if (k > 0)
                    Label(env, string.Concat(arr.Take(k)), x, sp.Y + lead * (i + 0.5),
                        font: f, size: fs, color: c, alpha: o * (i != 0 ? 0.85 : 1));
            }
        },
    });

    // ---------------- decor: tyRunningHead（JS L128-157）柱とノンブル ----------------

    private static void RegisterTyRunningHead() => RegD("tyRunningHead", new DecorDef
    {
        Name = "柱とノンブル", // JS ae:'timecodeBar'（未注册）
        Tags = new[] { "editorial", "calm" },
        W = 1,
        Layer = "front",
        Draw = (env, p) =>
        {
            if (env.Pass != "main") return;
            var bb = GetBB(env, env.Body);
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var cut = env.Cut!;
            var u = U(env);
            var m = MG(env);
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0) return;
            var fs = FS(env) * 0.9;
            var e = InE(env, 0.7) * o;
            var lw = Math.Max(1, u);
            var yH = m + fs * 0.6;
            if (!HitBB(0, yH - fs, W, yH + fs, bb, 8 * u))
            {
                var head = $"{Pad2(cut.Line + 1)}　{Regex.Replace(string.IsNullOrEmpty(cut.LineText) ? cut.Text ?? "" : cut.LineText, @"\s+", " ").Trim()}";
                var hs = string.Concat(JLayouts.CodePoints(head).Take(30));
                var tw = TextW(hs, BodyF(env), fs, 0.2);
                var right = p.Right;
                var x0 = right ? W - m - tw : m;
                Label(env, hs, x0, yH, font: BodyF(env), size: fs, color: sc.Str("sub"), alpha: e, track: 0.2);
                var a0 = right ? x0 - fs : x0 + tw + fs;
                var a1 = right ? m : W - m;
                if ((a1 - a0) * (right ? -1 : 1) > 20)
                    env.Line(new[] { new[] { a0, yH }, new[] { J.Lerp(a0, a1, e), yH } }, sc.Str("sub"), lw, 0.6 * o, false);
            }
            var yF = H - m - fs * 0.2;
            var folio = (cut.Index + 1).ToString(CultureInfo.InvariantCulture).PadLeft(3, '0');
            var fw = TextW(folio, MonoF(env), fs * 1.3, 0.2);
            var xF = p.Right ? m + fw / 2 : W - m - fw / 2;
            if (!HitBB(xF - fw, yF - fs * 1.2, xF + fw, yF + fs, bb, 8 * u))
            {
                var a = J.E.OutCubic(J.Clamp((env.Lt - 0.2) / 0.4)) * o;
                Label(env, folio, xF, yF, size: fs * 1.3, align: "center", color: sc.Str("fg"), alpha: a, track: 0.2);
                env.Line(new[] { new[] { xF - fw * 0.9, yF - fs * 1.1 }, new[] { xF + fw * 0.9, yF - fs * 1.1 } },
                    AccentOf(sc), Math.Max(1.5, 2 * u), a, false);
            }
        },
    });

    // ---------------- decor: tyGlyphBody（JS L160-190）字割り線 ----------------

    private static void RegisterTyGlyphBody() => RegD("tyGlyphBody", new DecorDef
    {
        Name = "字割り線", // JS ae:'guides'（未注册）
        Tags = new[] { "editorial", "graphic", "calm" },
        W = 0.9,
        Layer = "front",
        Draw = (env, p) =>
        {
            if (env.Pass != "main") return;
            var bb = GetBB(env, env.Body);
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var u = U(env);
            var m = MG(env) * 0.6;
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0) return;
            if (Bh(bb) > Bw(bb) * 1.3) return; // 竖排歌词：横向没有可分的线
            var xs = GlyphEdges(env, bb);
            var gap = 10 * u + Bh(bb) * 0.06;
            var lw = Math.Max(1, 0.9 * u);
            var col = sc.Str("sub");
            var a = 0.45 * o;
            for (var i = 0; i < xs.Count; i++)
            {
                var x = xs[i];
                var e = J.E.OutExpo(J.Clamp((env.Lt - i * 0.015) / 0.7));
                if (e <= 0) continue;
                var t0 = bb.Y0 - gap;
                var t1 = t0 - (t0 - m) * e;
                var b0 = bb.Y1 + gap;
                var b1 = b0 + (H - m - b0) * e;
                if (t0 > m) env.Line(new[] { new[] { x, t0 }, new[] { x, t1 } }, col, lw, a, false);
                if (b0 < H - m) env.Line(new[] { new[] { x, b0 }, new[] { x, b1 } }, col, lw, a, false);
            }
            // 身体上下线引到左右边距
            var e2 = InE(env, 0.8, 0.1) * o;
            var ac = AccentOf(sc);
            foreach (var y in new[] { bb.Y0, bb.Y1 })
            {
                env.Line(new[] { new[] { bb.X0 - gap, y }, new[] { bb.X0 - gap - (bb.X0 - gap - m) * e2, y } }, col, lw, a, false);
                env.Line(new[] { new[] { bb.X1 + gap, y }, new[] { bb.X1 + gap + (W - m - bb.X1 - gap) * e2, y } }, col, lw, a, false);
            }
            var fs = FS(env) * 0.75;
            if (bb.Y0 - gap > m + fs * 2)
                Label(env, $"{Pad2(JLayouts.GlyphCount(env.Cut?.Text ?? ""))} / W{Math.Round(Bw(bb) / u, MidpointRounding.AwayFromZero)}",
                    xs[0] + 4 * u, bb.Y0 - gap - fs * 0.8, size: fs, color: ac, alpha: e2);
        },
    });

    // ---------------- decor: tyTextRule（JS L193-232）文字罫 ----------------

    private static void RegisterTyTextRule() => RegD("tyTextRule", new DecorDef
    {
        Name = "文字罫", // JS ae:'verticalStrip'（未注册）
        Tags = new[] { "editorial", "graphic", "calm" },
        W = 1,
        Layer = "front",
        Subtle = true,
        Draw = (env, p) =>
        {
            if (env.Pass != "main") return;
            var bb = GetBB(env, env.Body);
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var cut = env.Cut!;
            var u = U(env);
            var m = MG(env);
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0) return;
            var fs = FS(env) * 0.72;
            var font = p.V % 2 != 0 ? MonoF(env) : BodyF(env);
            var unit = Regex.Replace(string.IsNullOrEmpty(cut.LineText) ? cut.Text ?? "" : cut.LineText, @"\s+", " ").Trim() + "　／　";
            var per = TextW(unit, font, fs, 0.18);
            if (per < 4) return;
            var rows = p.N >= 2
                ? new[] { m * 0.75, H - m * 0.75 }
                : new[] { p.Low ? H - m * 0.75 : m * 0.75 };
            for (var k = 0; k < rows.Length; k++)
            {
                var y = rows[k];
                if (HitBB(0, y - fs, W, y + fs, bb, 6 * u)) continue;
                var e = J.E.OutExpo(J.Clamp((env.Lt - k * 0.1) / 0.8));
                var dir = k % 2 != 0 ? 1 : -1;
                var off = ((env.Ltb * 26 * u * dir) % per + per) % per;
                var L = (W - m * 1.2) * e;
                var x0 = (W - L) / 2;
                env.Ctx.Save();
                env.Ctx.ClipRect(new SKRect((float)x0, (float)(y - fs), (float)(x0 + L), (float)(y - fs + fs * 2)));
                DrawIt(env, new JObject
                {
                    ["text"] = JLayouts.Repeat(unit, Math.Min(30, (int)Math.Ceiling(W * 1.2 / per) + 2)),
                    ["font"] = font,
                    ["size"] = fs,
                    ["align"] = "left",
                    ["track"] = 0.18,
                    ["x"] = x0 - per + off,
                    ["y"] = y,
                    ["color"] = sc.Str("sub"),
                    ["alpha"] = 0.8 * o,
                    ["ghost"] = false,
                });
                env.Ctx.Restore();
                var ry = y + (k != 0 || p.Low ? -1 : 1) * fs * 1.05;
                env.Line(new[] { new[] { x0, ry }, new[] { x0 + L, ry } }, sc.Str("sub"), Math.Max(1, 0.8 * u), 0.5 * o, false);
            }
        },
    });

    // ---------------- decor: tyTypeScale（JS L235-281）級数見本 ----------------

    private static void RegisterTyTypeScale() => RegD("tyTypeScale", new DecorDef
    {
        Name = "級数見本", // JS ae:'indexNum'（未注册）
        Tags = new[] { "editorial", "graphic" },
        W = 0.8,
        Layer = "front",
        Draw = (env, p) =>
        {
            if (env.Pass != "main") return;
            var bb = GetBB(env, env.Body);
            var sc = env.Sc!;
            var u = U(env);
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0) return;
            var ch = KeyChar(env.Cut?.Text);
            if (ch == null) return;
            var S0 = Math.Min(env.W, env.H) * 0.085;
            double[] ks = { 1, 0.72, 0.52, 0.37, 0.26 };
            var font = p.V % 2 != 0 ? SerifF(env) : JLayouts.Font0(env.St, "display") ?? "gothic_black";
            const double gapK = 0.18;
            var w = 0.0;
            foreach (var k in ks) w += S0 * k * (1 + gapK);
            var fs = FS(env) * 0.7;
            var h = S0 + fs * 2.2;
            var sp = CornerSpot(env, bb, w, h, p);
            if (!sp.Ok) return;
            var yb = sp.Y + S0 * 0.95;
            var x = sp.X;
            for (var i = 0; i < ks.Length; i++)
            {
                var k = ks[i];
                var s = S0 * k;
                var a = J.E.OutCubic(J.Clamp((env.Lt - 0.1 - i * 0.07) / 0.3)) * o;
                if (a > 0)
                {
                    DrawIt(env, new JObject
                    {
                        ["text"] = ch, ["font"] = font, ["size"] = s,
                        ["x"] = x + s / 2, ["y"] = yb - s * 0.46,
                        ["color"] = i != 0 ? sc.Str("sub") : sc.Str("fg"),
                        ["alpha"] = a, ["ghost"] = false,
                    });
                    Label(env, Math.Round(s / u * 0.75, MidpointRounding.AwayFromZero)
                        .ToString(CultureInfo.InvariantCulture), x + s / 2, yb + fs * 1.1,
                        size: fs, align: "center", alpha: a * 0.9, track: 0.05);
                }
                x += s * (1 + gapK);
            }
            var e = InE(env, 0.6) * o;
            env.Line(new[] { new[] { sp.X, yb + fs * 0.25 }, new[] { sp.X + (x - sp.X) * e, yb + fs * 0.25 } },
                AccentOf(sc), Math.Max(1, u), 0.8 * o, false);
        },
    });

    // ---------------- decor: tyBigPunct（JS L284-314）大きな約物 ----------------

    private static void RegisterTyBigPunct() => RegD("tyBigPunct", new DecorDef
    {
        Name = "大きな約物", // JS ae:'bracketsJP'（未注册）
        Tags = new[] { "editorial", "emotional", "graphic" },
        W = 0.9,
        Layer = "front",
        Draw = (env, p) =>
        {
            if (env.Pass != "main") return;
            var bb = GetBB(env, env.Body);
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0) return;
            string[][] pairs = { new[] { "「", "」" }, new[] { "『", "』" }, new[] { "“", "”" } };
            var pair = pairs[p.V % 3];
            var quote = p.V % 3 == 2;
            var s = J.Clamp(Bh(bb) * (quote ? 1.6 : 2.1), Math.Min(W, H) * 0.22, Math.Min(W, H) * 0.55);
            var gap = s * 0.04;
            var e = J.E.OutCubic(J.Clamp(env.Lt / 0.7));
            var sl = (1 - e) * s * 0.25;
            var font = SerifF(env);
            var col = p.Accent ? J.Mix(sc.Str("bg") ?? "#000000", sc.Str("accent") ?? "#FF3B30", 0.42)
                               : J.Mix(sc.Str("bg") ?? "#000000", sc.Str("fg") ?? "#FFFFFF", 0.2);
            // 墨盒位置：「 在字身框右上、」在左下、引号靠中偏上
            double xl, yt, xr, yb;
            if (!quote)
            {
                xl = bb.X0 - gap - 0.2 * s;
                yt = bb.Y0 - gap + 0.4 * s;
                xr = bb.X1 + gap + 0.2 * s;
                yb = bb.Y1 + gap - 0.4 * s;
            }
            else
            {
                xl = bb.X0 - gap - 0.22 * s;
                yt = bb.Y0 + 0.42 * s;
                xr = bb.X1 + gap + 0.22 * s;
                yb = bb.Y1 + 0.12 * s;
            }
            DrawIt(env, new JObject { ["text"] = pair[0], ["font"] = font, ["size"] = s, ["x"] = xl - sl, ["y"] = yt - sl, ["color"] = col, ["alpha"] = e * o, ["ghost"] = false });
            DrawIt(env, new JObject { ["text"] = pair[1], ["font"] = font, ["size"] = s, ["x"] = xr + sl, ["y"] = yb + sl, ["color"] = col, ["alpha"] = e * o, ["ghost"] = false });
        },
    });

    // ---------------- treat: tyHollowKey（JS L369-388）一字抜き ----------------

    private static void RegisterTyHollowKey() => RegT("tyHollowKey", new TreatDef
    {
        Name = "一字抜き", // JS ae:'spotChar'（未注册）
        Tags = new[] { "graphic", "editorial", "pop" },
        W = 0.9,
        Safe = true,
        Plan = (rng, _) => new JObject { ["rev"] = rng.Chance(0.35) },
        Draw = env =>
        {
            var it = env.It!;
            var pt = env.Cut!.TreatP;
            if (!Alive(it, 0.5)) return;
            var lay = JText.LayoutText(it);
            var plain = lay.Cells.Where(g => !IsSp(g.Ch)).ToList();
            if (plain.Count == 0) return;
            if (plain.Count == 1)
            {
                var kc = KeyChar(env.Cut.Text);
                if (plain[0].Ch != kc) return;
                if (!Bo(pt, "rev")) env.CharFns.Add((_, _, _) => new CharXf { Outline = true });
                return;
            }
            var kc2 = KeyChar(JLayouts.S(it, "text"));
            var g = plain.FirstOrDefault(q => q.Ch == kc2);
            var ki = g != null ? g.I : plain[0].I;
            var rev = Bo(pt, "rev");
            env.CharFns.Add((i, _, _) => (i == ki) != rev ? new CharXf { Outline = true } : null);
        },
    });

    // ---------------- treat: tyHeadRules（JS L391-414）天地罫 ----------------

    private static void RegisterTyHeadRules() => RegT("tyHeadRules", new TreatDef
    {
        Name = "天地罫", // JS ae:'underline'（未注册）
        Tags = new[] { "editorial", "graphic", "calm" },
        W = 0.8,
        Plan = (rng, _) => new JObject { ["k"] = rng.Range(0.06, 0.09), ["acc"] = rng.Chance(0.5) },
        Draw = env =>
        {
            var it = env.It!;
            var pt = env.Cut!.TreatP;
            if (!Alive(it) || NonSp(it) < 2) return;
            var sc = env.Sc!;
            var col = Bo(pt, "acc") ? AccentOf(sc) : JLayouts.S(it, "color") ?? sc.Str("fg") ?? "#FFFFFF";
            var k = D(pt, "k");
            AddPost(env, it, (e, i) => ItemSpace(e, i, () =>
            {
                var s = JAnim.Num(i, "size");
                var a = JAnim.NumNull(i, "alpha", 1);
                var o = J.E.InCubic(e.POut);
                var v = JLayouts.Truthy(i["vertical"]);
                var cross = s * (v ? JAnim.NumOr1(i, "sx") : JAnim.NumOr1(i, "sy"));
                var spans = LineSpans(e, i);
                for (var li = 0; li < spans.Count; li++)
                {
                    var L = spans[li];
                    var q = J.E.OutExpo(J.Clamp((e.Lt - JAnim.Num(i, "delay") - 0.05 - li * 0.08) / 0.5));
                    if (q <= 0 || o >= 1) continue;
                    var pad = s * 0.12;
                    var a0 = L.A0 - pad;
                    var a1 = L.A1 + pad;
                    var len = a1 - a0;
                    var p0 = a0 + len * o;
                    var p1 = a0 + len * q;
                    if (p1 - p0 < 1) continue;
                    var thick = Math.Max(3, s * k);
                    var thin = Math.Max(1, s * 0.018);
                    var top = L.C + (v ? 1 : -1) * cross * 0.66;
                    var bot = L.C + (v ? -1 : 1) * cross * 0.66;
                    double[] Pt(double u, double d) => v ? new[] { d, u } : new[] { u, d };
                    e.Line(new[] { Pt(p0, top), Pt(p1, top) }, col, thick, a, false);
                    e.Line(new[] { Pt(a1 - (p1 - a0), bot), Pt(a1 - (p0 - a0), bot) }, col, thin, a, false);
                }
            }));
        },
    });

    // ---------------- treat: tyHeadBig（JS L417-447）頭字強調 ----------------

    private static void RegisterTyHeadBig() => RegT("tyHeadBig", new TreatDef
    {
        Name = "頭字強調", // JS ae:'sizeWave'（未注册）
        Tags = new[] { "editorial", "pop", "emotional" },
        W = 0.9,
        Plan = (rng, _) => new JObject { ["k"] = rng.Range(1.35, 1.6), ["acc"] = rng.Chance(0.7) },
        Draw = env =>
        {
            var it = env.It!;
            var pt = env.Cut!.TreatP;
            if (!Alive(it) || NonSp(it) < 2) return;
            var lay = JText.LayoutText(it);
            var g0 = lay.Cells.FirstOrDefault(g => !IsSp(g.Ch) && !J.IsPunct(g.Ch[0]));
            if (g0 == null) return;
            var k = D(pt, "k");
            var v = JLayouts.Truthy(it["vertical"]);
            var sx = JAnim.NumOr1(it, "sx");
            var sy = JAnim.NumOr1(it, "sy");
            var ac = Bo(pt, "acc") && J.Contrast(env.Sc?.Str("accent") ?? "", env.Sc?.Str("bg") ?? "") >= 1.8
                ? env.Sc!.Str("accent")
                : null;
            var align = JLayouts.S(it, "align");
            var extra = (k - 1) * (v ? g0.H * sy : g0.W * sx);
            var center = align != "left" && align != "right";
            env.CharFns.Add((i, g, _) =>
            {
                if (g == null || (int)JAnim.Num(g, "li") != g0.Li) return null;
                var sh = center ? -extra / 2 : align == "right" ? -extra : 0;
                if (i == g0.I)
                {
                    var o = new CharXf { S = k };
                    if (v)
                    {
                        o.Dy = sh + extra / 2;
                        o.Dx = 0;
                    }
                    else
                    {
                        o.Dx = sh + extra / 2;
                        o.Dy = -(k - 1) * JAnim.Num(g, "h") * sy * 0.36;
                    }
                    if (ac != null) o.Color = ac;
                    return o;
                }
                if ((int)JAnim.Num(g, "ci") < g0.Ci) return v ? new CharXf { Dy = sh } : new CharXf { Dx = sh };
                return v ? new CharXf { Dy = sh + extra } : new CharXf { Dx = sh + extra };
            });
        },
    });

    // ---------------- treat: tyIndexSup（JS L450-490）字番号 ----------------

    private static void RegisterTyIndexSup() => RegT("tyIndexSup", new TreatDef
    {
        Name = "字番号", // JS ae:'emphasisDots'（未注册）
        Tags = new[] { "editorial", "graphic" },
        W = 0.7,
        Plan = (rng, _) => new JObject { ["acc"] = rng.Chance(0.6) },
        Draw = env =>
        {
            var it = env.It!;
            var pt = env.Cut!.TreatP;
            if (!Alive(it)) return;
            var sc = env.Sc!;
            var col = Bo(pt, "acc") ? AccentOf(sc) : sc.Str("sub") ?? "#9A9A9A";
            var single = NonSp(it) == 1;
            var @base = single ? (int)Math.Round(JAnim.Num(it, "mi"), MidpointRounding.AwayFromZero) : 0;
            AddPost(env, it, (e, i) =>
            {
                if (e.Pass != "main") return;
                ItemSpace(e, i, () =>
                {
                    var lay = JText.LayoutText(i);
                    var sx = JAnim.NumOr1(i, "sx");
                    var sy = JAnim.NumOr1(i, "sy");
                    var v = JLayouts.Truthy(i["vertical"]);
                    var a = JAnim.NumNull(i, "alpha", 1)
                            * J.Clamp((e.Lt - JAnim.Num(i, "delay") - 0.15) / 0.3)
                            * (1 - J.E.InCubic(e.POut));
                    if (a <= 0.01) return;
                    var fs = Math.Max(10, JAnim.Num(i, "size") * 0.26);
                    var mono = MonoF(e);
                    var n = @base;
                    foreach (var g in lay.Cells)
                    {
                        if (IsSp(g.Ch)) continue;
                        n++;
                        if (n > 40) break;
                        var c = e.CharFn?.Invoke(g.I, JText.CellJson(g), lay.N);
                        if (c is { Hide: true }) continue;
                        var s = c?.S ?? 1;
                        var x = g.X * sx + (c?.Dx ?? 0);
                        var y = g.Y * sy + (c?.Dy ?? 0);
                        var ox = v ? g.W * sx * 0.55 * s : g.W * sx * 0.52 * s;
                        var oy = v ? -g.H * sy * 0.3 * s : -g.H * sy * 0.42 * s;
                        DrawIt(e, new JObject
                        {
                            ["text"] = n.ToString(CultureInfo.InvariantCulture),
                            ["font"] = mono,
                            ["size"] = fs,
                            ["x"] = x + ox,
                            ["y"] = y + oy,
                            ["align"] = "left",
                            ["color"] = col,
                            ["alpha"] = a * (c?.A ?? 1),
                            ["ghost"] = false,
                        });
                    }
                });
            });
        },
    });

    // ---------------- trans: tyRuleWipe（JS L518-544）罫線ワイプ ----------------

    private static void RegisterTyRuleWipe() => RegX("tyRuleWipe", new TransDef
    {
        Name = "罫線ワイプ", // JS ae:'blinds'（未注册）
        Tags = new[] { "editorial", "graphic", "calm" },
        W = 0.9,
        Dur = 0.45,
        Plan = (rng, _) => new JObject { ["n"] = rng.Int(7, 12), ["rtl"] = rng.Chance(0.25) },
        Draw = Wrap((ctx, a, b, p, i, pt) =>
        {
            var cw = i.Cw;
            var ch = i.Ch;
            var n = Math.Max(1, I(pt, "n", 9));
            var bh = ch / (double)n;
            var ac = TAcc(i);
            var lw = Math.Max(1, Math.Min(cw, ch) * 0.0015);
            ctx.DrawImage(a, 0, 0);
            var st = 0.5 / n;
            for (var row = 0; row < n; row++)
            {
                var q = J.E.InOutCubic(J.Clamp((p - row * st) / (1 - (n - 1) * st)));
                var y0 = Math.Round(row * bh, MidpointRounding.AwayFromZero);
                var y1 = Math.Round((row + 1) * bh, MidpointRounding.AwayFromZero);
                var x = cw * q;
                if (q > 0)
                {
                    if (Bo(pt, "rtl")) Part(ctx, b, cw - x, y0, x, y1 - y0, cw, ch);
                    else Part(ctx, b, 0, y0, x, y1 - y0, cw, ch);
                }
                if (q > 0 && q < 1)
                {
                    var cwid = Math.Max(3, bh * 0.14);
                    var xc = Bo(pt, "rtl") ? cw - x - cwid : x;
                    using var pf = new SKPaint { IsAntialias = false, Color = Env.SkColor(ac, 1) };
                    ctx.DrawRect(new SKRect((float)xc, (float)(y0 + bh * 0.2), (float)(xc + cwid), (float)(y0 + bh * 0.8)), pf);
                }
            }
            // 条带之间的罫线，随进度淡入淡出
            using (var pf = new SKPaint { IsAntialias = false, Color = Env.SkColor(ac, 0.6 * Bell(p)) })
                for (var row = 1; row < n; row++)
                {
                    var y = Math.Round(row * bh, MidpointRounding.AwayFromZero) - lw / 2;
                    ctx.DrawRect(new SKRect(0, (float)y, cw, (float)(y + lw)), pf);
                }
        }),
    });

    // ---------------- trans: tyGridCells（JS L547-585）升目送り ----------------

    private static void RegisterTyGridCells() => RegX("tyGridCells", new TransDef
    {
        Name = "升目送り", // JS ae:'checker'（未注册）
        Tags = new[] { "editorial", "graphic" },
        W = 0.8,
        Dur = 0.5,
        Plan = (rng, _) => new JObject { ["rows"] = rng.Int(4, 6), ["tate"] = rng.Chance(0.7) },
        Draw = Wrap((ctx, a, b, p, i, pt) =>
        {
            var cw = i.Cw;
            var ch = i.Ch;
            var rows = Math.Max(1, I(pt, "rows", 5));
            var cell = ch / (double)rows;
            var cols = (int)Math.Ceiling(cw / cell);
            var ox = (cw - cols * cell) / 2;
            var n = rows * cols;
            var ac = TAcc(i);
            var lw = Math.Max(1, Math.Min(cw, ch) * 0.0016);
            ctx.DrawImage(a, 0, 0);
            var k = J.E.InOutCubic(J.Clamp(p / 0.92)) * n;
            for (var j = 0; j < n; j++)
            {
                int c, r;
                if (Bo(pt, "tate"))
                {
                    c = cols - 1 - j / rows;
                    r = j % rows;
                }
                else
                {
                    r = j / cols;
                    c = j % cols;
                }
                var x = ox + c * cell;
                var y = r * cell;
                if (j < Math.Floor(k))
                    Part(ctx, b, x, y, cell + 1, cell + 1, cw, ch);
                else if (j == Math.Floor(k))
                {
                    var f = k - j;
                    ctx.Save();
                    using (var pf = new SKPaint { IsAntialias = false, Color = SKColors.White.WithAlpha((byte)Math.Round(J.Clamp(f) * 255)) })
                        DrawPartial(ctx, b, x, y, cell + 1, cell + 1, cw, ch, pf);
                    ctx.Restore();
                    using var sp = new SKPaint
                    {
                        IsAntialias = false,
                        IsStroke = true,
                        StrokeWidth = (float)(lw * 3),
                        Color = Env.SkColor(ac, 1),
                    };
                    ctx.DrawRect(new SKRect((float)(x + lw * 1.5), (float)(y + lw * 1.5),
                        (float)(x + lw * 1.5 + (cell - lw * 3)), (float)(y + lw * 1.5 + (cell - lw * 3))), sp);
                }
            }
            using (var pf = new SKPaint { IsAntialias = false, Color = Env.SkColor(ac, 0.5 * Bell(p)) })
            {
                for (var c = 0; c <= cols; c++)
                {
                    var x = Math.Round(ox + c * cell, MidpointRounding.AwayFromZero) - lw / 2;
                    ctx.DrawRect(new SKRect((float)x, 0, (float)(x + lw), ch), pf);
                }
                for (var r = 1; r < rows; r++)
                {
                    var y = Math.Round(r * cell, MidpointRounding.AwayFromZero) - lw / 2;
                    ctx.DrawRect(new SKRect(0, (float)y, cw, (float)(y + lw)), pf);
                }
            }
        }),
    });

    /// <summary>带 paint 的 part 变体（tyGridCells 的 globalAlpha=f 逐格拷贝）。</summary>
    private static void DrawPartial(SKCanvas ctx, SKImage img, double x, double y, double w, double h, int cw, int ch, SKPaint paint)
    {
        var x0 = Math.Max(0, (int)Math.Floor(x));
        var y0 = Math.Max(0, (int)Math.Floor(y));
        var x1 = Math.Min(cw, (int)Math.Ceiling(x + w));
        var y1 = Math.Min(ch, (int)Math.Ceiling(y + h));
        if (x1 - x0 < 1 || y1 - y0 < 1) return;
        ctx.DrawImage(img, new SKRect(x0, y0, x1, y1), new SKRect(x0, y0, x1, y1), paint);
    }
}
