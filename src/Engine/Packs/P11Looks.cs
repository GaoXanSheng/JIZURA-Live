// 移植自 frontend/src/engine/11p_looks.js（pack = 'looks'：treat 24 + bg 24 + cam 15 + fx 24）。
//
// 契约（与 P11TreatTrans 同约定，不改共享文件）：
// - treat：TreatDef.Plan(rng, st) → JObject（键名 = JS plan 字段，planner 存 cut.treatP）；
//   Draw(env) 在 JLayouts.MainDraw 的 treat 调用点执行（JS apply(env, it, cut.treatP)）：
//   env.It 是当前 item、env.Cut.TreatP 是参数。item 就地改写直接写 env.It；
//   charFns.push → env.CharFns.Add（管线 CombineChar 合并）。
// - addPre/addPost/sized：C# DrawFx 已接钩子字段（Env.PreFn/PostFn）——钩子体仍在本 Draw 内就地
//   执行（pre 与 JS 同层=文字下方；post 的内容提前画，层级在主文字下，内容/几何一致；可回迁）。
//   sized 因此只按 apply 时的 size 算一次（出入场改 size 时不刷新——已知近似）。
// - lineSpans/glyphList 的 it.charFn：apply 时管线尚未 CombineChar → 字形按静态布局位置取。
// - bg：BgDef.Draw(env)（JS draw(env, cut.bgP || {})，P 从 env.Cut.BgP 取，save/restore 包装）。
// - cam：CamDef.Get(env) → CamState（JS get(env, cut.camP)，P 从 env.Cut.CamP 取）。
// - fx：与 P11FxB 同一插件路径——FxDef.Draw(env, ev)，k/I 信息经 FxPlug 桥（Renderer.Post 的
//   FXE 插件段装配）。注意 looks 的 evS 种子是 7331（fxB 是 9127），逐字保持。
//
// Env 缺口（本文件内补）：JS env.beat 是 {since,len,index}，C# Env.Beat 只装归一相位——
// 用 BeatOf(env) 从 env.Plan.Beats + env.T 重建（与 P11LayoutsB/09_render 的 beatAt 同口径）。
// JS 的 env.energy != null 判定对位 env.Plan.Energy is { Length: > 0 }（C# 侧缺省是 0 不是 null）。
// WeakMap（bgRun/stepCache）→ ConditionalWeakTable。
//
// 跨文件符号对位：J.drawItem→env.DrawItem、J.layoutText/measure→JText.LayoutText/Measure、
// J.fontsOf→JLayouts.FontsOf、J.rgba(本地 Rgba)/r/rs/rr/h/mix/lum/contrast/fitContrast/smooth/
// noise1/isKanji/isKata/isPunct→J.*；E.*→J.E.*；rng.*→Rng.*。

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace JizuraLive.Engine;

public static class P11Looks
{
    private const string Pk = "looks";
    private static int _installed;

    /// <summary>幂等注册本文件全部条目（顺序 = JS reg/bgReg/reg('cam')/fxReg 出现序）。</summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;
        RegisterTreatOutline();        // JS L181
        RegisterTreatOutlineFill();    // L194
        RegisterTreatDoubleOutline();  // L208
        RegisterTreatExtrude();        // L244
        RegisterTreatLongShadow();     // L268
        RegisterTreatHardShadow();     // L290
        RegisterTreatSoftShadow();     // L303
        RegisterTreatGlow();           // L387
        RegisterTreatMarker();         // L410
        RegisterTreatUnderline();      // L453
        RegisterTreatStrike();         // L485
        RegisterTreatBoxed();          // L518
        RegisterTreatGradientV();      // L551
        RegisterTreatSplitColor();     // L563
        RegisterTreatHalftone();       // L588
        RegisterTreatStripes();        // L595
        RegisterTreatHatch();          // L602
        RegisterTreatDotted();         // L610
        RegisterTreatAlternate();      // L626
        RegisterTreatItalic();         // L643
        RegisterTreatWide();           // L655
        RegisterTreatTall();           // L668
        RegisterTreatEchoOutline();    // L681
        RegisterTreatEmphasisDots();   // L710

        RegisterBgSunburst();          // L776
        RegisterBgConcentric();        // L809
        RegisterBgHalftoneFade();      // L837
        RegisterBgBigStripes();        // L879
        RegisterBgSplitV();            // L902
        RegisterBgSplitH();            // L923
        RegisterBgSplitDiag();         // L944
        RegisterBgGradientSweep();     // L977
        RegisterBgSpotlight();         // L1001
        RegisterBgTvBars();            // L1036
        RegisterBgChecker();           // L1054
        RegisterBgBigChar();           // L1078
        RegisterBgSpeedLines();        // L1114
        RegisterBgScanBars();          // L1135
        RegisterBgDotGrid();           // L1155
        RegisterBgRetroGrid();         // L1192
        RegisterBgBokeh();             // L1253
        RegisterBgParticles();         // L1280
        RegisterBgRipples();           // L1304
        RegisterBgPolka();             // L1348
        RegisterBgEqBars();            // L1372
        RegisterBgBorderFrame();       // L1420
        RegisterBgLetterbox();         // L1444
        RegisterBgNoiseField();        // L1462

        RegisterCamPullOut();          // L1494
        RegisterCamPanL();             // L1500
        RegisterCamPanR();             // L1506
        RegisterCamTiltUp();           // L1512
        RegisterCamDutch();            // L1518
        RegisterCamHandheld();         // L1530
        RegisterCamBeatPunch();        // L1543
        RegisterCamWhipIn();           // L1558
        RegisterCamCrashZoom();        // L1575
        RegisterCamBounce();           // L1590
        RegisterCamRoll();             // L1599
        RegisterCamDriftDiag();        // L1605
        RegisterCamShakeHard();        // L1618
        RegisterCamDollyIn();          // L1634
        RegisterCamStepZoom();         // L1661

        RegisterFxPanelWipe();         // L1695
        RegisterFxIrisTrans();         // L1728
        RegisterFxDoors();             // L1752
        RegisterFxBlindsTrans();       // L1772
        RegisterFxRgbSplit();          // L1789
        RegisterFxSmear();             // L1831
        RegisterFxVhsRoll();           // L1849
        RegisterFxTrackingNoise();     // L1899
        RegisterFxMirrorFlash();       // L1932
        RegisterFxStrobe();            // L1958
        RegisterFxPosterize();         // L1968
        RegisterFxHueShift();          // L1987
        RegisterFxTileShift();         // L2009
        RegisterFxFilmBurn();          // L2040
        RegisterFxWhipBlur();          // L2068
        RegisterFxBlackFrame();        // L2107
        RegisterFxWhiteFrame();        // L2108
        RegisterFxWaveWarp();          // L2128
        RegisterFxPixelDrift();        // L2143
        RegisterFxZoomPunch();         // L2160
        RegisterFxLightSweep();        // L2172
        RegisterFxCrtOff();            // L2198
        RegisterFxSplitSlide();        // L2231
    }

    // ================= 本包 helper（JS L8-179 + L742-773 + L1489-1492 + L1677-1693 逐条对位） =================

    private static double D(JObject o, string k)
    {
        var v = o[k];
        return v != null && (v.Type == JTokenType.Float || v.Type == JTokenType.Integer) ? (double)v : 0;
    }

    /// <summary>JS 的 P.x || d：缺字段或 0 时取 d。</summary>
    private static double DOr(JObject o, string k, double d)
    {
        var v = D(o, k);
        return v != 0 ? v : d;
    }

    private static string? S(JObject o, string k) => o[k] is JValue v && v.Type == JTokenType.String ? (string?)v.Value : null;

    private static double NumNull(JObject o, string k, double d) =>
        o[k] != null && (o[k]!.Type == JTokenType.Float || o[k]!.Type == JTokenType.Integer) ? (double)o[k]! : d;

    private static bool Truthy(JToken? t) => JLayouts.Truthy(t);

    private static string ScK(Scheme sc, string k) => sc.Str(k) ?? "#000000";

    private static double Ctr(string? a, string? b) => J.Contrast(a ?? "#000000", b ?? "#000000");

    private static bool IsDark(string? c) => J.Lum(c ?? "#000000") < 0.45;

    private static string? FirstOK(IEnumerable<string?> list, Func<string, bool> test, string? fb)
    {
        foreach (var c in list)
            if (c != null && test(c))
                return c;
        return fb;
    }

    private static string Best(IEnumerable<string?> list, string against)
    {
        var b = "";
        var bv = -1.0;
        foreach (var c in list)
        {
            if (c == null) continue;
            var v = Ctr(c, against);
            if (!(v > bv)) continue;
            bv = v;
            b = c;
        }
        return b;
    }

    /// <summary>和 col 拉开差距、又在方案背景上读得清的 accent（JS accentFor）。</summary>
    private static string AccentFor(Scheme sc, string col, double min = 1.6) =>
        FirstOK(new[] { sc.Str("accent"), sc.Str("accent2"), sc.Str("ghostA"), sc.Str("ghostB") },
            c => Ctr(c, col) >= min && Ctr(c, ScK(sc, "bg")) >= 1.5,
            J.FitContrast(ScK(sc, "accent"), col, min + 0.3))!;

    /// <summary>压在 box 上的文字色（JS textOn）。</summary>
    private static string TextOn(string box, string pref, Scheme sc) =>
        Ctr(pref, box) >= 3 ? pref : Best(new[] { sc.Str("bg"), sc.Str("fg"), sc.Str("ink"), "#111111", "#FFFFFF" }, box);

    /// <summary>和方案背景拉开差距的颜色，画在文字旁的记号用（JS markCol）。</summary>
    private static string MarkCol(Scheme sc, string col) =>
        FirstOK(new[] { sc.Str("accent"), sc.Str("accent2"), col }, c => Ctr(c, ScK(sc, "bg")) >= 2, col)!;

    private static bool Alive(JObject it, double amin = 0.9) =>
        !(it["fill"] is JValue { Type: JTokenType.Boolean } fb && !(bool)fb.Value!) &&
        NumNull(it, "alpha", 1) >= amin && !string.IsNullOrEmpty(S(it, "text")) && D(it, "size") > 1;

    private static string ColOfIt(Env env, JObject it) => S(it, "color") ?? ScK(env.Sc!, "fg");

    private static double InP(Env env, JObject it, double d, double len) =>
        J.Clamp((env.Lt - NumNull(it, "delay", 0) - d) / len);

    private static int GlyphN(string? t)
    {
        var n = 0;
        foreach (var cp in JLayouts.CodePoints(t ?? ""))
            if (cp.Trim().Length > 0) n++;
        return n;
    }

    private static bool IsSp(string ch) => ch is " " or "　";

    /// <summary>碎块型出入场进行中返回 true（JS inPieces；带 gradient 或无填充会废掉 piece）。</summary>
    private static bool InPieces(Env env, JObject it)
    {
        var c = env.Cut!;
        var enterKey = S(it, "enter") ?? c.Enter;
        var exitKey = S(it, "exit") ?? c.Exit;
        if (enterKey == "assemble" && env.Lt - NumNull(it, "delay", 0) < c.InDur * 1.3 + 0.05) return true;
        if (exitKey is "explode" or "fall" or "drift" && c.OutDur > 0 && env.Lt > c.Dur - c.OutDur - 0.3) return true;
        return false;
    }

    /// <summary>在和 J.drawFx 相同的 clip/bands 下跑 fn；local=true 再进 item 空间（JS withFx）。</summary>
    private static void WithFx(Env env, JObject it, Action fn, bool local = true)
    {
        var ctx = env.Ctx;
        double w = env.W, h = env.H;
        ctx.Save();
        try
        {
            var clip = it["clip"] as JArray;
            if (clip is { Count: >= 2 })
            {
                var x = D0(clip[0]);
                ctx.ClipRect(new SKRect((float)x, (float)(-h), (float)(x + D0(clip[1]) - x), (float)(2 * h)));
            }
            var clipY = it["clipY"] as JArray;
            if (clipY is { Count: >= 2 })
            {
                var y = D0(clipY[0]);
                ctx.ClipRect(new SKRect((float)(-w), (float)y, (float)(2 * w), (float)(y + D0(clipY[1]) - y)));
            }
            void Run()
            {
                if (!local)
                {
                    fn();
                    return;
                }
                ctx.Save();
                ctx.Translate((float)D(it, "x"), (float)D(it, "y"));
                var rot = D(it, "rot");
                if (rot != 0) ctx.RotateDegrees((float)rot);
                var skew = D(it, "skew");
                if (skew != 0) ctx.Skew((float)Math.Tan(skew * J.DEG), 0);
                fn();
                ctx.Restore();
            }
            var vb = it["vbands"] as JArray;
            var b = it["bands"] as JArray;
            if (vb is { Count: > 0 })
            {
                foreach (var v in vb)
                {
                    ctx.Save();
                    ctx.ClipRect(new SKRect((float)D0(v[0]), (float)(-2 * h), (float)D0(v[1]), (float)(3 * h)));
                    ctx.Translate(0, (float)D0(v[2]));
                    Run();
                    ctx.Restore();
                }
            }
            else if (b is { Count: > 0 })
            {
                foreach (var v in b)
                {
                    ctx.Save();
                    ctx.ClipRect(new SKRect((float)(-2 * w), (float)D0(v[0]), (float)(3 * w), (float)D0(v[1])));
                    ctx.Translate((float)D0(v[2]), 0);
                    Run();
                    ctx.Restore();
                }
                var lo = D0(b[0][0]);
                var hi = D0(b[^1][1]);
                using var outside = new SKPath();
                outside.AddRect(new SKRect((float)(-2 * w), (float)(-3 * h), (float)(3 * w), (float)lo));
                outside.AddRect(new SKRect((float)(-2 * w), (float)hi, (float)(3 * w), (float)(hi + 4 * h)));
                ctx.Save();
                ctx.ClipPath(outside, antialias: false);
                Run();
                ctx.Restore();
            }
            else Run();
        }
        finally
        {
            ctx.Restore();
        }
    }

    /// <summary>JS addPre 的对位：钩子挂 env.PreFn（DrawFx 在裁剪/主文字前消费，时点层级与 JS 一致）；
    /// WithFx 语境（clip/bands/local）在钩子体内重建（body 里已包 WithFx 的用法传 fn 直通）。</summary>
    private static void AddPre(Env env, Action<Env, JObject> fn)
    {
        var prev = env.PreFn;
        env.PreFn = prev != null ? (e, x) => { prev(e, x); fn(e, x); } : (Action<Env, JObject>)fn;
    }

    /// <summary>JS addPost 的对位：钩子挂 env.PostFn（DrawFx 在主文字后消费，层级与 JS 一致）。</summary>
    private static void AddPost(Env env, Action<Env, JObject> fn)
    {
        var prev = env.PostFn;
        env.PostFn = prev != null
            ? (e, x, bb) => { prev(e, x, bb); fn(e, x); }
            : (Action<Env, JObject, BBox?>)((e, x, _) => fn(e, x));
    }

    private static double D0(JToken? t) => t != null && (t.Type == JTokenType.Float || t.Type == JTokenType.Integer) ? (double)t : 0;

    private sealed class Span
    {
        public int Li;
        public double A0, A1, C;
    }

    /// <summary>静态布局下每行（竖排每列）的 item 空间范围（JS lineSpans；charFn 藏掉的字不算）。</summary>
    private static List<Span> LineSpans(Env env, JObject it)
    {
        var lay = JText.LayoutText(it);
        var sx = DOr(it, "sx", 1);
        var sy = DOr(it, "sy", 1);
        var vert = Truthy(it["vertical"]);
        var map = new Dictionary<int, Span>();
        foreach (var g in lay.Cells)
        {
            if (IsSp(g.Ch)) continue;
            if (env.CharFn != null)
            {
                var c = env.CharFn(g.I, null, lay.N);
                if (c != null && (c.Hide || (c.A != null && c.A < 0.05))) continue;
            }
            var a0 = vert ? (g.Y - g.H / 2) * sy : (g.X - g.W / 2) * sx;
            var a1 = vert ? (g.Y + g.H / 2) * sy : (g.X + g.W / 2) * sx;
            if (!map.TryGetValue(g.Li, out var l))
                map[g.Li] = new Span { Li = g.Li, A0 = a0, A1 = a1, C = vert ? g.X * sx : g.Y * sy };
            else
            {
                l.A0 = Math.Min(l.A0, a0);
                l.A1 = Math.Max(l.A1, a1);
            }
        }
        return map.Values.OrderBy(v => v.Li).ToList();
    }

    private sealed class Gly
    {
        public JText.GlyphCell G = null!;
        public double X, Y, W, H, S, Rot, A;
    }

    /// <summary>可见字形列表，位置带上 sx/sy（JS glyphList；charFn 位移按 apply 时刻近似，见头注）。</summary>
    private static List<Gly> GlyphList(Env env, JObject it)
    {
        var lay = JText.LayoutText(it);
        var sx = DOr(it, "sx", 1);
        var sy = DOr(it, "sy", 1);
        var @out = new List<Gly>();
        foreach (var g in lay.Cells)
        {
            if (IsSp(g.Ch)) continue;
            var c = env.CharFn?.Invoke(g.I, null, lay.N);
            if (c != null && c.Hide) continue;
            @out.Add(new Gly
            {
                G = g,
                X = g.X * sx + (c?.Dx ?? 0),
                Y = g.Y * sy + (c?.Dy ?? 0),
                W = g.W * sx,
                H = g.H * sy,
                S = c?.S ?? 1,
                Rot = c?.Rot ?? 0,
                A = c?.A ?? 1,
            });
        }
        return @out;
    }

    /// <summary>字形中心在行内的 0..1 位置（JS lineFracs），marker 划过哪几个字就翻哪几个的色。</summary>
    private static double[] LineFracs(JObject it)
    {
        var lay = JText.LayoutText(it);
        var ext = new Dictionary<int, (double A, double B)>();
        var fr = new double[lay.Cells.Count > 0 ? lay.Cells.Max(g => g.I) + 1 : 0];
        var vert = Truthy(it["vertical"]);
        foreach (var g in lay.Cells)
        {
            var p = vert ? g.Y : g.X;
            var h = (vert ? g.H : g.W) / 2;
            var e = ext.TryGetValue(g.Li, out var got) ? got : (1e9, -1e9);
            ext[g.Li] = (Math.Min(e.Item1, p - h), Math.Max(e.Item2, p + h));
        }
        foreach (var g in lay.Cells)
        {
            var e = ext[g.Li];
            var p = vert ? g.Y : g.X;
            fr[g.I] = (p - e.A) / Math.Max(1, e.B - e.A);
        }
        return fr;
    }

    /// <summary>复制一份干净 item 给叠加通道用，钩子与特效字段全清空（JS 的 bare 副本字面量）。</summary>
    private static JObject Bare(JObject i, JObject? extra = null)
    {
        var c = (JObject)i.DeepClone();
        foreach (var k in new[] { "pre", "post", "echo", "streak", "shadow", "extrude", "pattern", "gradient",
                 "strokeDash", "wipeBar", "cursorAt", "bands", "vbands", "clip", "clipY", "blend" })
            c[k] = null;
        c["strokeUnder"] = false;
        if (extra != null)
            foreach (var kv in extra)
                c[kv.Key] = kv.Value;
        return c;
    }

    /// <summary>副本绘制：临时换 env.CharFn（函数字段不进 JObject；piece 走 env 默认=null）。</summary>
    private static void DrawCopy(Env env, JObject copy)
    {
        var saved = env.CharFn;
        env.CharFn = null;
        try
        {
            env.DrawItem(copy);
        }
        finally
        {
            env.CharFn = saved;
        }
    }

    private static string Rgba(string hex, double a)
    {
        var (r, g, b) = J.Hex(hex);
        return "rgba(" + r + "," + g + "," + b + "," + a.ToString("0.######", CultureInfo.InvariantCulture) + ")";
    }

    private static JObject Shadow(string color, double blur, double dx, double dy) =>
        new() { ["color"] = color, ["blur"] = blur, ["dx"] = dx, ["dy"] = dy };

    private static JArray Grad2(params (double Pos, string Col)[] stops)
    {
        var a = new JArray();
        foreach (var (pos, col) in stops)
            a.Add(new JArray { pos, col });
        return a;
    }

    private static string TwoTone(Scheme sc, string col) =>
        FirstOK(new[] { sc.Str("accent"), sc.Str("accent2"), sc.Str("ghostA"), sc.Str("ghostB") },
            c => Ctr(c, col) >= 1.5 && Ctr(c, ScK(sc, "bg")) >= 2, J.Mix(col, ScK(sc, "bg"), 0.45))!;

    // ---- bg 侧 helper（JS L742-773） ----

    private static string LayC(Scheme sc, double k) => J.Mix(ScK(sc, "bg"), ScK(sc, "fg"), k);
    private static string TintC(Scheme sc, double k) => J.Mix(ScK(sc, "bg"), ScK(sc, "accent"), k);
    private static double Umin(Env env) => Math.Min(env.W, env.H);
    private static int Bs(Rng rng) => rng.Int(1, 1_000_000_000);

    private static double WrapV(double v, double m) => ((v % m) + m) % m;

    /// <summary>JS addColorStop 用 rgba 字符串；C# 直接 SKColor（本地不需要）。</summary>
    private static SKColor Css(string hex, double a) => Env.SkColor(hex, a);

    private sealed class BgRunBox { public double S; }

    /// <summary>当前背景已经跑了多久；同一背景连续的几个 cut 算一段（JS bgT + WeakMap）。</summary>
    private static readonly ConditionalWeakTable<Cut, BgRunBox> BgRun = new();

    private static double BgT(Env env)
    {
        var c = env.Cut;
        if (c == null) return env.T;
        if (BgRun.TryGetValue(c, out var box)) return env.T - box.S;
        var s = c.Start;
        var cs = env.Plan?.Cuts;
        var seed0 = D(c.BgP, "seed");
        if (cs != null)
            for (var i = c.Index - 1; i >= 0 && i < cs.Count; i--)
            {
                var p = cs[i];
                if (p.Bg == c.Bg && D(p.BgP, "seed") == seed0 && Math.Abs(p.End - s) < 0.06) s = p.Start;
                else break;
            }
        BgRun.Add(c, new BgRunBox { S = s });
        return env.T - s;
    }

    /// <summary>bgReg 包装：draw(env, P || {}, ctx) 的 save/restore 对位。</summary>
    private static Action<Env> BgWrap(Action<Env, JObject> draw) => env =>
    {
        env.Ctx.Save();
        try
        {
            draw(env, env.Cut?.BgP ?? new JObject());
        }
        finally
        {
            env.Ctx.Restore();
        }
    };

    private static void RegBg(string key, BgDef def) => Reg.Register("bg", key, def, Pk);

    // ---- cam 侧 helper（JS L1489-1492 + stepTimes L1643-1660） ----

    private static double Km(Env env) => J.Clamp(env.Fx.Motion * 1.25, 0, 1.25);
    private static double CuOf(Env env) => J.Clamp(env.Lt / Math.Max(0.3, env.Cut?.Dur ?? 0));
    private static double PanP(double u) => u * 0.6 + J.E.InOutSine(u) * 0.4;
    private static double LagOf(Env env) => Math.Max(0, env.Ltb - env.Lt);

    private readonly record struct BeatInfo(bool Has, double Since, double Len, int Index);

    /// <summary>env.Plan.Beats + env.T 重建 {since,len,index}（与 09_render beatAt 同口径；Env 只装得下归一相位）。</summary>
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

    private sealed class StepBox { public int N; public double[] V = Array.Empty<double>(); }

    private static readonly ConditionalWeakTable<Cut, StepBox> StepCache = new();

    /// <summary>切分内的打点时刻（最多 n 个，间隔 ≥0.3s；无节拍数据时均分）（JS stepTimes）。</summary>
    private static double[] StepTimes(Env env, int n)
    {
        var c = env.Cut!;
        if (StepCache.TryGetValue(c, out var box) && box.N == n) return box.V;
        var beats = env.Plan?.Beats ?? Array.Empty<double>();
        var v = new List<double>();
        foreach (var b in beats)
        {
            if (b <= c.Start + 0.2 || b >= c.End - 0.15) continue;
            var r = b - c.Start;
            if (v.Count == 0 || r - v[^1] >= 0.3) v.Add(r);
            if (v.Count >= n) break;
        }
        if (v.Count == 0)
            for (var i = 0; i < n; i++) v.Add(c.Dur * (i + 1) / (n + 1));
        StepCache.Remove(c);
        StepCache.Add(c, new StepBox { N = n, V = v.ToArray() });
        return v.ToArray();
    }

    private static void RegCam(string key, CamDef def) => Reg.Register("cam", key, def, Pk);

    // ---- fx 侧 helper（JS L1677-1693；evS 种子 7331，与 fxB 的 9127 不同） ----

    private static double Bell(double k) => Math.Sin(Math.PI * J.Clamp(k));

    private static uint EvS(PlanEvent ev) => J.H(unchecked((int)(long)Math.Round(ev.T * 1000)), 7331);

    private static double AmpOf(PlanEvent ev) => J.Clamp(ev.Amp, 0.3, 1.6);

    /// <summary>0 到 1，在边界 b 处全遮，再落回 0（JS cover）。</summary>
    private static double Cover(double k, double b, Func<double, double>? inE = null, Func<double, double>? outE = null)
    {
        inE ??= J.E.InCubic;
        outE ??= J.E.OutCubic;
        return k < b ? inE(k / b) : 1 - outE((k - b) / Math.Max(0.01, 1 - b));
    }

    private static string InkOf(Scheme sc) =>
        FirstOK(new[] { sc.Str("ink"), sc.Str("fg") }, c => Ctr(c, ScK(sc, "bg")) >= 1.6, ScK(sc, "fg"))!;

    /// <summary>fxReg 包装：save/restore（进度已由渲染器 clamp）。</summary>
    private static Action<Env, PlanEvent> FxWrapped(Action<Env, PlanEvent, double> draw) => (env, ev) =>
    {
        env.Ctx.Save();
        try
        {
            draw(env, ev, J.Clamp(FxPlug.K));
        }
        finally
        {
            env.Ctx.Restore();
        }
    };

    private static readonly SKSamplingOptions SmpN = new(SKFilterMode.Nearest, SKMipmapMode.None);
    private static readonly SKSamplingOptions SmpL = new(SKFilterMode.Linear, SKMipmapMode.None);

    private static SKPaint ImgPaint(double alpha = 1, SKBlendMode blend = SKBlendMode.SrcOver) => new()
    {
        Color = SKColors.White.WithAlpha((byte)Math.Round(J.Clamp(alpha) * 255)),
        BlendMode = blend,
    };

    private static void FillMode(SKCanvas x, double x0, double y0, double w, double h, string color,
        double alpha = 1, SKBlendMode blend = SKBlendMode.SrcOver)
    {
        if (alpha <= 0) return;
        using var p = new SKPaint { Color = Env.SkColor(color, alpha), BlendMode = blend };
        x.DrawRect(new SKRect((float)x0, (float)y0, (float)(x0 + w), (float)(y0 + h)), p);
    }

    // ================= treat（JS reg('treat', …) 出现序） =================

    private static void RegT(string key, TreatDef def) => Reg.Register("treat", key, def, Pk);

    private static void RegisterTreatOutline() => RegT("outline", new TreatDef
    {
        Name = "袋文字",
        Tags = new[] { "graphic", "pop", "glitch", "emotional" },
        W = 1.2,
        Plan = (rng, st) => new JObject { ["k"] = rng.Range(0.022, 0.038) },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it, 0.5) || InPieces(env, it)) return;
            it["strokeColor"] = ColOfIt(env, it);
            it["fill"] = false;
            it["stroke"] = Math.Max(1.4, D(it, "size") * DOr(p, "k", 0.03)); // sized 就地（size 依赖，见头注）
        },
    });

    private static void RegisterTreatOutlineFill() => RegT("outlineFill", new TreatDef
    {
        Name = "縁取り",
        Tags = new[] { "pop", "graphic" },
        W = 1,
        Plan = (rng, st) => new JObject { ["k"] = rng.Range(0.075, 0.12), ["c"] = (double)rng.Int(0, 2) },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it)) return;
            var sc = env.Sc!;
            var col = ColOfIt(env, it);
            it["strokeColor"] = D(p, "c") == 2
                ? FirstOK(new[] { sc.Str("accent2"), sc.Str("accent") }, c => Ctr(c, col) >= 1.8, AccentFor(sc, col, 1.8))
                : AccentFor(sc, col, 1.8);
            it["strokeUnder"] = true;
            it["stroke"] = Math.Max(2, D(it, "size") * DOr(p, "k", 0.09));
        },
    });

    private static void RegisterTreatDoubleOutline() => RegT("doubleOutline", new TreatDef
    {
        Name = "二重縁",
        Tags = new[] { "pop", "graphic" },
        W = 0.7,
        Plan = (rng, st) => new JObject { ["a"] = rng.Range(0.07, 0.09), ["b"] = rng.Range(0.08, 0.11) },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it)) return;
            var sc = env.Sc!;
            var col = ColOfIt(env, it);
            var ring = AccentFor(sc, col, 1.6);
            var gap = Ctr(ScK(sc, "bg"), col) >= 1.5 && Ctr(ScK(sc, "bg"), ring) >= 1.3
                ? ScK(sc, "bg")
                : Best(new[] { sc.Str("ink"), sc.Str("fg"), "#000000", "#FFFFFF" }, ring);
            it["strokeColor"] = gap;
            it["strokeUnder"] = true;
            it["stroke"] = Math.Max(2, D(it, "size") * D(p, "a"));
            // 外圈：addPre → env.PreFn（item 底下垫一份只描边的粗副本）
            var c = Bare(it, new JObject
            {
                ["fill"] = false,
                ["strokeUnder"] = false,
                ["stroke"] = Math.Max(4, D(it, "size") * (D(p, "a") + D(p, "b"))),
                ["strokeColor"] = ring,
            });
            AddPre(env, (e2, x2) => WithFx(e2, x2, () => DrawCopy(e2, c), false));
        },
    });

    private static void RegisterTreatExtrude() => RegT("extrude", new TreatDef
    {
        Name = "立体",
        Tags = new[] { "pop", "graphic" },
        W = 0.9,
        Plan = (rng, st) => new JObject
        {
            ["d"] = rng.Range(0.09, 0.13),
            ["dir"] = new JArray(rng.Pick(new[] { new[] { 1.0, 1.0 }, new[] { 1.0, 1.0 }, new[] { -1.0, 1.0 }, new[] { 1.0, 0.55 } })),
            ["c"] = (double)rng.Int(0, 1),
        },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it)) return;
            var sc = env.Sc!;
            var col = ColOfIt(env, it);
            var n = GlyphN(S(it, "text"));
            var dir = (p["dir"] as JArray)?.Select(D0).ToArray() ?? new[] { 1.0, 1.0 };
            var @base = D(p, "c") != 0
                ? FirstOK(new[] { sc.Str("ink"), sc.Str("accent2") }, c => Ctr(c, col) >= 1.6 && Ctr(c, ScK(sc, "bg")) >= 1.4, AccentFor(sc, col, 1.6))
                : AccentFor(sc, col, 1.6);
            var ec = J.Mix(@base!, "#000000", IsDark(ScK(sc, "bg")) ? 0.3 : 0.2);
            var l = D(it, "size") * D(p, "d");
            it["extrude"] = new JObject
            {
                ["n"] = (double)J.Clamp((int)Math.Round(l / 2.5), 4, n > 10 ? 12 : 22),
                ["dx"] = dir[0] * l,
                ["dy"] = dir[1] * l,
                ["color"] = ec,
            };
        },
    });

    private static void RegisterTreatLongShadow() => RegT("longShadow", new TreatDef
    {
        Name = "長い影",
        Tags = new[] { "pop", "graphic" },
        W = 0.6,
        Plan = (rng, st) => new JObject { ["L"] = rng.Range(0.35, 0.65), ["ang"] = rng.Pick(new[] { 45.0, 45.0, 35.0, 60.0, 135.0 }) },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it)) return;
            var sc = env.Sc!;
            var col = ColOfIt(env, it);
            var n = GlyphN(S(it, "text"));
            var sh = IsDark(ScK(sc, "bg")) ? J.Mix(ScK(sc, "bg"), AccentFor(sc, col, 1.5), 0.5) : J.Mix(ScK(sc, "bg"), ScK(sc, "fg"), 0.28);
            var ca = Math.Cos(D(p, "ang") * J.DEG);
            var sa = Math.Sin(D(p, "ang") * J.DEG);
            var l = D(it, "size") * D(p, "L");
            it["extrude"] = new JObject
            {
                ["n"] = (double)J.Clamp((int)Math.Round(l / Math.Max(2, D(it, "size") * 0.018)), 10, n > 10 ? 14 : 24),
                ["dx"] = ca * l,
                ["dy"] = sa * l,
                ["color"] = sh,
                ["fade"] = true,
                ["a"] = 0.9,
            };
        },
    });

    private static void RegisterTreatHardShadow() => RegT("hardShadow", new TreatDef
    {
        Name = "ずらし影",
        Tags = new[] { "pop", "graphic", "glitch" },
        W = 1,
        Plan = (rng, st) => new JObject
        {
            ["d"] = rng.Range(0.05, 0.085),
            ["dir"] = new JArray(rng.Pick(new[] { new[] { 1.0, 1.0 }, new[] { 1.0, 1.0 }, new[] { -1.0, 1.0 }, new[] { 1.0, -1.0 }, new[] { 0.45, 1.0 } })),
        },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it)) return;
            var sh = AccentFor(env.Sc!, ColOfIt(env, it), 1.6);
            var dir = (p["dir"] as JArray)?.Select(D0).ToArray() ?? new[] { 1.0, 1.0 };
            var l = D(it, "size") * D(p, "d");
            it["extrude"] = new JObject { ["n"] = 1.0, ["dx"] = dir[0] * l, ["dy"] = dir[1] * l, ["color"] = sh };
        },
    });

    private static void RegisterTreatSoftShadow() => RegT("softShadow", new TreatDef
    {
        Name = "ぼかし影",
        Tags = new[] { "calm", "emotional", "editorial" },
        W = 0.8,
        Safe = true,
        Plan = (rng, st) => new JObject { ["b"] = rng.Range(0.08, 0.14), ["dy"] = rng.Range(0.03, 0.07) },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it)) return;
            var sc = env.Sc!;
            var s = D(it, "size");
            var c = IsDark(ScK(sc, "bg"))
                ? Rgba(J.Mix(ScK(sc, "bg"), ScK(sc, "accent"), 0.45), 0.75)
                : Rgba(J.Mix(ScK(sc, "fg"), "#000000", 0.5), 0.36);
            it["shadow"] = Shadow(c, s * D(p, "b"), s * 0.02, s * D(p, "dy"));
        },
    });

    // ---- 软光晕（JS halo L317-386）：item 缩小画进复用离屏，模糊再放大回来 ----

    [ThreadStatic] private static SKSurface? _haloA;
    [ThreadStatic] private static SKSurface? _haloB;

    private static SKSurface HaloCv(string k, int w, int h)
    {
        ref var slot = ref k == "a" ? ref _haloA : ref _haloB;
        w = Math.Max(2, w);
        h = Math.Max(2, h);
        var s = slot;
        if (s == null || s.Canvas.DeviceClipBounds.Width < w || s.Canvas.DeviceClipBounds.Height < h)
        {
            s?.Dispose();
            s = SKSurface.Create(new SKImageInfo(w, h)); // JS 是 grow-only，重建即等价（尺寸单调不回缩）
            slot = s;
        }
        return s;
    }

    private static void Halo(Env e, JObject i, string col, double b, bool dk)
    {
        var m = JText.Measure(i);
        var s = D(i, "size");
        var hw0 = m.W / 2;
        var hh0 = m.H / 2;
        var cx = D(i, "x");
        var cy = D(i, "y");
        var vert = Truthy(i["vertical"]);
        var align = S(i, "align");
        if (vert)
        {
            if (align == "left") cy += hh0;
        }
        else if (align == "left") cx += hw0;
        else if (align == "right") cx -= hw0;
        var hw = hw0;
        var hh = hh0;
        var rot = D(i, "rot");
        if (rot != 0)
        {
            var r = Math.Sqrt(hw0 * hw0 + hh0 * hh0);
            var a = rot * J.DEG;
            var dx = cx - D(i, "x");
            var dy = cy - D(i, "y");
            cx = D(i, "x") + dx * Math.Cos(a) - dy * Math.Sin(a);
            cy = D(i, "y") + dx * Math.Sin(a) + dy * Math.Cos(a);
            hw = hh = r;
        }
        var pad = s * b * 2.2 + s * 0.1;
        var x0 = cx - hw - pad;
        var y0 = cy - hh - pad;
        var wd = (hw + pad) * 2;
        var hd = (hh + pad) * 2;
        var q = Math.Min((e.Scale != 0 ? e.Scale : 1) * 0.25, 900 / Math.Max(wd, hd));
        var wp = Math.Max(2, (int)Math.Ceiling(wd * q));
        var hp = Math.Max(2, (int)Math.Ceiling(hd * q));
        var a1 = HaloCv("a", wp, hp);
        var b1 = HaloCv("b", wp, hp);
        var ax = a1.Canvas;
        ax.SetMatrix(SKMatrix.Identity);
        ax.Clear();
        ax.SetMatrix(Mat(q, 0, 0, q, -x0 * q, -y0 * q));
        var c = Bare(i, new JObject
        {
            ["fill"] = true,
            ["alpha"] = 1.0,
            ["color"] = col,
            ["strokeColor"] = col,
            ["stroke"] = s * 0.07,
            ["strokeUnder"] = false,
            ["blur"] = 0.0,
            ["pieceFn"] = null,
            ["dash"] = null,
        });
        var envH = Env.Make(ax, e.Plan, e.Cut, e.Sc, e.Lt, e.Step, 0, 0, null, "main", null, q, e.T, e.Ltb);
        envH.AllowFilter = false;
        envH.It = c;
        envH.DrawItem(c);
        var bx = b1.Canvas;
        bx.SetMatrix(SKMatrix.Identity);
        bx.Clear();
        using (var ai = a1.Snapshot())
        using (var bp = new SKPaint { ImageFilter = JText.BlurFilter(Math.Max(1, s * b * q * 0.9)) })
            bx.DrawImage(ai, 0, 0, bp);
        var ctx = e.Ctx;
        ctx.Save();
        using (var bi = b1.Snapshot())
        using (var p = ImgPaint(NumNull(i, "alpha", 1) * (dk ? 1 : 0.75), dk ? SKBlendMode.Screen : SKBlendMode.SrcOver))
        {
            ctx.DrawImage(bi, new SKRect(0, 0, wp, hp),
                new SKRect((float)x0, (float)y0, (float)(x0 + wd), (float)(y0 + hd)), SmpL, p);
            if (dk)
                ctx.DrawImage(bi, new SKRect(0, 0, wp, hp),
                    new SKRect((float)x0, (float)y0, (float)(x0 + wd), (float)(y0 + hd)), SmpL, p);
        }
        ctx.Restore();
    }

    private static void RegisterTreatGlow() => RegT("glow", new TreatDef
    {
        Name = "発光",
        Tags = new[] { "emotional", "calm", "glitch" },
        W = 0.9,
        Safe = true,
        Plan = (rng, st) => new JObject { ["b"] = rng.Range(0.16, 0.26), ["self"] = rng.Chance(0.5) },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it)) return;
            var sc = env.Sc!;
            var col = ColOfIt(env, it);
            var dk = IsDark(ScK(sc, "bg"));
            var self = Truthy(p["self"]);
            var gc = dk
                ? FirstOK(self ? new[] { col, sc.Str("accent"), sc.Str("accent2") } : new[] { sc.Str("accent"), sc.Str("accent2"), col },
                    c => J.Lum(c) > J.Lum(ScK(sc, "bg")) + 0.2, col)!
                : FirstOK(new[] { sc.Str("accent"), sc.Str("accent2"), sc.Str("ghostA"), sc.Str("ghostB") },
                    c => Ctr(c, ScK(sc, "bg")) >= 2 && J.Lum(c) > 0.08 && Ctr(c, col) >= 1.4, col)!;
            var small = D(it, "size") < Math.Min(env.W, env.H) * 0.09; // 小字直接用 canvas shadow，便宜也够
            var useShadow = !(env.AllowFilter && !small);
            if (useShadow)
                it["shadow"] = Shadow(Rgba(gc, 0.95), D(it, "size") * D(p, "b") * 0.6, 0, 0);
            else
                it["shadow"] = null;
            // addPre → env.PreFn：大字在主通道垫离屏软光晕
            if (env.Pass == "main" && env.AllowFilter && !small)
            {
                var bParam = D(p, "b") * (0.9 + 0.1 * Math.Sin(env.Ltb * 3.2));
                AddPre(env, (e2, x2) => WithFx(e2, x2, () => Halo(e2, x2, gc, bParam, dk), false));
            }
        },
    });

    private static void RegisterTreatMarker() => RegT("marker", new TreatDef
    {
        Name = "マーカー",
        Tags = new[] { "pop", "graphic", "editorial" },
        W = 1,
        Plan = (rng, st) => new JObject { ["v"] = rng.Pick(new[] { "box", "box", "skew", "half" }), ["c"] = (double)rng.Int(0, 1) },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it, 0.9)) return;
            var sc = env.Sc!;
            var col = ColOfIt(env, it);
            var v = S(p, "v") ?? "box";
            var half = v == "half";
            var box = half
                ? J.FitContrast(FirstOK(new[] { sc.Str("accent"), sc.Str("accent2") }, c => Ctr(c, col) >= 2, sc.Str("accent"))!, col, 2.2)
                : FirstOK(D(p, "c") != 0
                        ? new[] { sc.Str("accent2"), sc.Str("accent"), sc.Str("ink"), sc.Str("fg") }
                        : new[] { sc.Str("accent"), sc.Str("accent2"), sc.Str("ink"), sc.Str("fg") },
                    c => Ctr(c, ScK(sc, "bg")) >= 2.2, ScK(sc, "fg"))!;
            var tc = half ? col : TextOn(box, col, sc);
            (double O, double Q) Prog(Env e, int li) =>
                (J.E.InCubic(e.POut), J.E.OutExpo(InP(e, it, li * 0.07, 0.32)));
            if (tc != col)
            {
                var fr = LineFracs(it);
                env.CharFns.Add((gi, g, n) =>
                {
                    var li = g != null && g["li"] != null ? (int)D0(g["li"]) : 0;
                    var (o, q) = Prog(env, li);
                    var f = gi >= 0 && gi < fr.Length ? fr[gi] : 0.5;
                    return f >= o && f <= q ? new CharXf { Color = tc } : null;
                });
            }
            // addPre → env.PreFn：荧光笔画在文字下方
            AddPre(env, (e2, _) => WithFx(e2, it, () =>
            {
                var s = D(it, "size");
                var sx = DOr(it, "sx", 1);
                var sy = DOr(it, "sy", 1);
                var a = NumNull(it, "alpha", 1);
                foreach (var l in LineSpans(env, it))
                {
                    var (o, q) = Prog(env, l.Li);
                    if (q <= 0 || o >= 1) continue;
                    var pad = s * 0.14;
                    var a0 = l.A0 - pad;
                    var len = l.A1 + pad - a0;
                    var p0 = a0 + len * o;
                    var p1 = a0 + len * q;
                    if (p1 - p0 < 1) continue;
                    var th = s * (Truthy(it["vertical"]) ? sx : sy) * (half ? 0.52 : 1.08);
                    var c0 = half ? l.C + s * (Truthy(it["vertical"]) ? sx : sy) * 0.02 : l.C - th / 2;
                    if (!Truthy(it["vertical"]))
                    {
                        if (v == "skew")
                        {
                            var kk = th * 0.22;
                            env.Poly(new[] { new[] { p0 + kk, c0 }, new[] { p1 + kk, c0 }, new[] { p1 - kk, c0 + th }, new[] { p0 - kk, c0 + th } }, box, a, true);
                        }
                        else env.Rect(p0, c0, p1 - p0, th, box, half ? a * 0.92 : a, true);
                    }
                    else
                    {
                        if (v == "skew")
                        {
                            var kk = th * 0.22;
                            env.Poly(new[] { new[] { c0, p0 - kk }, new[] { c0 + th, p0 + kk }, new[] { c0 + th, p1 + kk }, new[] { c0, p1 - kk } }, box, a, true);
                        }
                        else env.Rect(c0, p0, th, p1 - p0, box, half ? a * 0.92 : a, true);
                    }
                }
            }));
        },
    });

    private static void RegisterTreatUnderline() => RegT("underline", new TreatDef
    {
        Name = "下線",
        Tags = new[] { "editorial", "calm", "graphic" },
        W = 0.8,
        Plan = (rng, st) => new JObject { ["v"] = rng.Pick(new[] { "bar", "bar", "double", "wave" }), ["k"] = rng.Range(0.05, 0.075) },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it, 0.9) || GlyphN(S(it, "text")) < 2) return;
            var v = S(p, "v") ?? "bar";
            var lc = MarkCol(env.Sc!, ColOfIt(env, it));
            // addPost → env.PostFn（层级在主文字上，与 JS 一致）
            AddPost(env, (e2, _) => WithFx(e2, it, () =>
            {
                var s = D(it, "size");
                var a = NumNull(it, "alpha", 1);
                var o = J.E.InCubic(env.POut);
                var vert = Truthy(it["vertical"]);
                var cross = s * (vert ? DOr(it, "sx", 1) : DOr(it, "sy", 1));
                var spans = LineSpans(env, it);
                for (var k = 0; k < spans.Count; k++)
                {
                    var l = spans[k];
                    var q = J.E.OutExpo(InP(env, it, 0.08 + k * 0.08, 0.45));
                    if (q <= 0 || o >= 1) continue;
                    var a0 = l.A0 - s * 0.04;
                    var len = l.A1 + s * 0.04 - a0;
                    var p0 = a0 + len * o;
                    var p1 = a0 + len * q;
                    if (p1 - p0 < 1) continue;
                    var off = l.C + cross * 0.6;
                    var th = Math.Max(2, s * DOr(p, "k", 0.06));
                    double[] Pt(double u, double d) => vert ? new[] { off + d, u } : new[] { u, off + d };
                    if (v == "wave")
                    {
                        var pts = new List<double[]>();
                        var wl = s * 0.32;
                        var mm = Math.Min(160, (int)Math.Ceiling((p1 - p0) / (wl / 8)) + 1);
                        for (var j = 0; j < mm; j++)
                        {
                            var u = p0 + (p1 - p0) * j / (mm - 1);
                            pts.Add(Pt(u, Math.Sin((u - a0) / wl * J.TAU) * th * 0.7));
                        }
                        env.Line(pts.ToArray(), lc, th * 0.75, a, false);
                    }
                    else if (v == "double")
                    {
                        env.Line(new[] { Pt(p0, -th * 0.55), Pt(p1, -th * 0.55) }, lc, th * 0.42, a, false);
                        env.Line(new[] { Pt(p0, th * 0.55), Pt(p1, th * 0.55) }, lc, th * 0.42, a, false);
                    }
                    else env.Line(new[] { Pt(p0, 0), Pt(p1, 0) }, lc, th, a, false);
                }
            }));
        },
    });

    private static void RegisterTreatStrike() => RegT("strike", new TreatDef
    {
        Name = "取り消し線",
        Tags = new[] { "glitch", "editorial", "emotional" },
        W = 0.5,
        Plan = (rng, st) => new JObject
        {
            ["v"] = rng.Pick(new[] { "one", "one", "two" }),
            ["ang"] = rng.Range(-4, 4),
            ["k"] = rng.Range(0.06, 0.085),
        },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it, 0.9)) return;
            var sc = env.Sc!;
            var v = S(p, "v") ?? "one";
            var lc = AccentFor(sc, ColOfIt(env, it), 1.8);
            var vert = Truthy(it["vertical"]);
            // addPost → env.PostFn（见头注）
            AddPost(env, (e2, _) => WithFx(e2, it, () =>
            {
                var s = D(it, "size");
                var a = NumNull(it, "alpha", 1);
                var o = J.E.InCubic(env.POut);
                var ctx = env.Ctx;
                var spans = LineSpans(env, it);
                for (var k = 0; k < spans.Count; k++)
                {
                    var l = spans[k];
                    var q = J.E.OutExpo(InP(env, it, env.Cut!.InDur * 0.7 + k * 0.1, 0.3));
                    if (q <= 0 || o >= 1) continue;
                    var a0 = l.A0 - s * 0.08;
                    var len = l.A1 + s * 0.08 - a0;
                    var p0 = a0 + len * o;
                    var p1 = a0 + len * q;
                    if (p1 - p0 < 1) continue;
                    var th = Math.Max(2, s * D(p, "k"));
                    var mid = (l.A0 + l.A1) / 2;
                    ctx.Save();
                    if (vert)
                    {
                        ctx.Translate((float)l.C, (float)mid);
                        ctx.RotateDegrees((float)D(p, "ang"));
                    }
                    else
                    {
                        ctx.Translate((float)mid, (float)(l.C + s * 0.02));
                        ctx.RotateDegrees((float)D(p, "ang"));
                    }
                    double[] Pt(double u, double d) => vert ? new[] { d, u - mid } : new[] { u - mid, d };
                    if (v == "two")
                    {
                        env.Line(new[] { Pt(p0, -th * 0.8), Pt(p1, -th * 0.8) }, lc, th * 0.6, a, true);
                        env.Line(new[] { Pt(p0, th * 0.8), Pt(p1, th * 0.8) }, lc, th * 0.6, a, true);
                    }
                    else env.Line(new[] { Pt(p0, 0), Pt(p1, 0) }, lc, th, a, true);
                    ctx.Restore();
                }
            }));
        },
    });

    private static void RegisterTreatBoxed() => RegT("boxed", new TreatDef
    {
        Name = "箱組",
        Tags = new[] { "graphic", "editorial", "pop" },
        W = 0.7,
        Plan = (rng, st) => new JObject { ["v"] = rng.Pick(new[] { "solid", "solid", "alt", "frame" }) },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it, 0.9)) return;
            var sc = env.Sc!;
            var col = ColOfIt(env, it);
            var v = S(p, "v") ?? "solid";
            var frame = v == "frame";
            var off = (int)D(it, "mi");
            var boxA = FirstOK(new[] { sc.Str("ink"), sc.Str("fg"), sc.Str("accent") }, c => Ctr(c, ScK(sc, "bg")) >= 2.2, ScK(sc, "fg"))!;
            var boxB = v == "alt"
                ? FirstOK(new[] { sc.Str("accent"), sc.Str("accent2") }, c => Ctr(c, boxA) >= 1.5 && Ctr(c, ScK(sc, "bg")) >= 1.8, boxA)!
                : boxA;
            var tcA = frame ? col : TextOn(boxA, ScK(sc, "bg"), sc);
            var tcB = frame ? col : TextOn(boxB, ScK(sc, "bg"), sc);
            double QOf(Env e, int gi) =>
                J.E.OutBack(J.Clamp((e.Lt - NumNull(it, "delay", 0) - gi * 0.035) / 0.24), 1.7)
                * (1 - J.E.InCubic(J.Clamp(e.POut * 1.4 - gi * 0.03)));
            bool Alt(int gi) => v == "alt" && (gi + off) % 2 == 1;
            if (!frame)
                env.CharFns.Add((gi, g, n) => QOf(env, gi) > 0.55 ? new CharXf { Color = Alt(gi) ? tcB : tcA } : null);
            // addPre → env.PreFn：逐字底盒
            AddPre(env, (e2, _) => WithFx(e2, it, () =>
            {
                var s = D(it, "size");
                var sx = DOr(it, "sx", 1);
                var sy = DOr(it, "sy", 1);
                var a = NumNull(it, "alpha", 1);
                var ctx = env.Ctx;
                var vert = Truthy(it["vertical"]);
                foreach (var g in GlyphList(env, it))
                {
                    var q = QOf(env, g.G.I);
                    if (q <= 0.01) continue;
                    var w = vert ? s * sx * 1.02 : Math.Max(g.W * 0.94, s * sx * 0.42);
                    var h = vert ? Math.Max(g.H * 0.94, s * sy * 0.42) : s * sy * 1.02;
                    var c = Alt(g.G.I) ? boxB : boxA;
                    ctx.Save();
                    ctx.Translate((float)g.X, (float)g.Y);
                    if (g.Rot != 0) ctx.RotateDegrees((float)g.Rot);
                    ctx.Scale((float)(g.S * q), (float)(g.S * q));
                    if (frame) env.RRect(-w / 2, -h / 2, w, h, 0, null, a * g.A, true, c, Math.Max(1.5, s * 0.035));
                    else env.Rect(-w / 2, -h / 2, w, h, c, a * g.A, true);
                    ctx.Restore();
                }
            }));
        },
    });

    private static void RegisterTreatGradientV() => RegT("gradientV", new TreatDef
    {
        Name = "縦グラデ",
        Tags = new[] { "emotional", "pop" },
        W = 0.9,
        Plan = (rng, st) => new JObject { ["up"] = rng.Chance(0.4), ["g"] = rng.Chance(0.5) },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it) || InPieces(env, it)) return;
            var sc = env.Sc!;
            var col = ColOfIt(env, it);
            var gArr = sc.Get("grad") as JArray;
            var useScheme = Truthy(p["g"]) && gArr != null && gArr.All(c => Ctr((string?)c, ScK(sc, "bg")) >= 1.6);
            var g = useScheme ? new JArray(gArr!) : new JArray { col, TwoTone(sc, col) };
            if (Truthy(p["up"]))
            {
                var rev = new JArray(((JArray)g).Reverse());
                g = rev;
            }
            it["gradient"] = g;
        },
    });

    private static void RegisterTreatSplitColor() => RegT("splitColor", new TreatDef
    {
        Name = "上下二色",
        Tags = new[] { "pop", "graphic" },
        W = 0.8,
        Plan = (rng, st) => new JObject { ["sp"] = rng.Range(0.5, 0.57), ["top"] = rng.Chance(0.35) },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it) || InPieces(env, it)) return;
            var sc = env.Sc!;
            var col = ColOfIt(env, it);
            var c2 = TwoTone(sc, col);
            var a = Truthy(p["top"]) ? c2 : col;
            var b = Truthy(p["top"]) ? col : c2;
            it["gradient"] = Grad2((0, a), (D(p, "sp"), a), (D(p, "sp"), b), (1, b));
        },
    });

    /// <summary>JS patternTreat(kind, alt)：图案填充类 treat 的共用体。</summary>
    private static void PatternTreat(Env env, JObject it, JObject p, string kind, string? alt)
    {
        if (!Alive(it)) return;
        var sc = env.Sc!;
        var col = ColOfIt(env, it);
        var v = S(p, "v") ?? "tone";
        it["pattern"] = v == "lines" && alt != null ? alt : kind;
        it["patternColor"] = col;
        if (v == "duo")
        {
            it["patternBg"] = AccentFor(sc, col, 1.6);
        }
        else
        {
            it["patternBg"] = J.Mix(col, ScK(sc, "bg"), 0.62);
            it["strokeColor"] = col;
            it["stroke"] = Math.Max(1.2, D(it, "size") * 0.018); // sized 就地
        }
    }

    private static void RegisterTreatHalftone() => RegT("halftone", new TreatDef
    {
        Name = "網点",
        Tags = new[] { "pop", "graphic" },
        W = 0.7,
        Plan = (rng, st) => new JObject { ["v"] = rng.Pick(new[] { "tone", "tone", "duo" }) },
        Draw = env => { if (env.It != null) PatternTreat(env, env.It, env.Cut!.TreatP, "dots", null); },
    });

    private static void RegisterTreatStripes() => RegT("stripes", new TreatDef
    {
        Name = "ストライプ",
        Tags = new[] { "pop", "graphic" },
        W = 0.6,
        Plan = (rng, st) => new JObject { ["v"] = rng.Pick(new[] { "tone", "duo", "lines" }) },
        Draw = env => { if (env.It != null) PatternTreat(env, env.It, env.Cut!.TreatP, "stripes", "lines"); },
    });

    private static void RegisterTreatHatch() => RegT("hatch", new TreatDef
    {
        Name = "斜線",
        Tags = new[] { "graphic", "editorial", "glitch" },
        W = 0.5,
        Plan = (rng, st) => new JObject { ["v"] = rng.Pick(new[] { "tone", "tone", "duo" }) },
        Draw = env => { if (env.It != null) PatternTreat(env, env.It, env.Cut!.TreatP, "hatch", null); },
    });

    private static void RegisterTreatDotted() => RegT("dotted", new TreatDef
    {
        Name = "点線輪郭",
        Tags = new[] { "calm", "editorial", "graphic" },
        W = 0.5,
        Plan = (rng, st) => new JObject { ["k"] = rng.Range(0.03, 0.04), ["spd"] = (double)rng.Pick(new[] { 0, 1, 1 }) },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it, 0.5) || InPieces(env, it)) return;
            it["strokeColor"] = ColOfIt(env, it);
            it["fill"] = false;
            var s = D(it, "size");
            var a = s * 0.05;
            var g = s * 0.036;
            var per = a + g;
            it["stroke"] = Math.Max(1.6, s * D(p, "k"));
            var off = Truthy(p["spd"]) ? ((env.Ltb * s * 0.22) % per + per) % per : 0;
            it["strokeDash"] = new JArray(off < a ? a - off : 0, g - (off < a ? 0 : off - a), a, off < a ? off : off - a);
        },
    });

    private static void RegisterTreatAlternate() => RegT("alternate", new TreatDef
    {
        Name = "交互色",
        Tags = new[] { "pop", "graphic" },
        W = 0.8,
        Plan = (rng, st) => new JObject { ["v"] = rng.Pick(new[] { "alt", "alt", "kanji" }) },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it)) return;
            var sc = env.Sc!;
            var col = ColOfIt(env, it);
            var c2 = AccentFor(sc, col, 1.6);
            var text = S(it, "text") ?? "";
            var chars = JLayouts.CodePoints(text).Where(c => c.Trim().Length > 0).ToList();
            bool IsK(string c) => c.Length == 1 && (J.IsKanji(c[0]) || J.IsKata(c[0]));
            var mixed = chars.Any(IsK) && chars.Any(c => !IsK(c));
            var v = S(p, "v") ?? "alt";
            if (v == "kanji" && (mixed || chars.Count == 1))
                env.CharFns.Add((gi, g, n) => g != null && g["ch"] != null && IsK((string)g["ch"]!) ? new CharXf { Color = c2 } : null);
            else
            {
                var off = (int)D(it, "mi");
                env.CharFns.Add((gi, g, n) => (gi + off) % 2 == 1 ? new CharXf { Color = c2 } : null);
            }
        },
    });

    private static void RegisterTreatItalic() => RegT("italic", new TreatDef
    {
        Name = "斜体",
        Tags = new[] { "editorial", "pop", "emotional" },
        W = 0.8,
        Safe = true,
        Plan = (rng, st) => new JObject { ["a"] = rng.Range(10, 15) },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || string.IsNullOrEmpty(S(it, "text"))) return;
            if (Truthy(it["vertical"]))
            {
                var r = D(p, "a") * 0.65;
                env.CharFns.Add((gi, g, n) => new CharXf { Rot = r });
            }
            else it["skew"] = NumNull(it, "skew", 0) - D(p, "a");
        },
    });

    private static void RegisterTreatWide() => RegT("wide", new TreatDef
    {
        Name = "平体",
        Tags = new[] { "graphic", "pop" },
        W = 0.7,
        Safe = true,
        Draw = env =>
        {
            var it = env.It;
            if (it == null || string.IsNullOrEmpty(S(it, "text"))) return;
            if (!Truthy(it["vertical"]))
            {
                // 别让原本放得下的行超出版面
                var w0 = JText.Measure(it).W;
                var lim = env.W * 0.9;
                it["sx"] = DOr(it, "sx", 1) * 1.1;
                if (w0 <= lim && w0 * 1.1 > lim) it["size"] = D(it, "size") * (lim / (w0 * 1.1));
            }
            it["sy"] = DOr(it, "sy", 1) * 0.84;
        },
    });

    private static void RegisterTreatTall() => RegT("tall", new TreatDef
    {
        Name = "長体",
        Tags = new[] { "editorial", "calm", "graphic", "emotional" },
        W = 0.7,
        Safe = true,
        Draw = env =>
        {
            var it = env.It;
            if (it == null || string.IsNullOrEmpty(S(it, "text"))) return;
            it["sx"] = DOr(it, "sx", 1) * 0.8;
            if (Truthy(it["vertical"])) it["sy"] = DOr(it, "sy", 1) * 1.02;
            else
            {
                it["sy"] = DOr(it, "sy", 1) * 1.05;
                it["size"] = D(it, "size") * 1.05;
            }
        },
    });

    private static void RegisterTreatEchoOutline() => RegT("echoOutline", new TreatDef
    {
        Name = "輪郭の残響",
        Tags = new[] { "glitch", "emotional", "graphic" },
        W = 0.7,
        Plan = (rng, st) => new JObject
        {
            ["v"] = rng.Pick(new[] { "diag", "diag", "zoom", "rise", "side" }),
            ["n"] = (double)rng.Int(3, 4),
            ["d"] = rng.Range(0.045, 0.07),
        },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it)) return;
            var c = AccentFor(env.Sc!, ColOfIt(env, it), 1.4);
            var amt = J.E.OutCubic(InP(env, it, 0.05, 0.45)) * (1 - J.E.InCubic(env.POut)) * (0.85 + 0.15 * Math.Sin(env.Ltb * 3.4));
            if (amt <= 0.01)
            {
                it["echo"] = null;
                return;
            }
            var d = D(it, "size") * D(p, "d") * amt;
            var v = S(p, "v") ?? "diag";
            var o = new JObject { ["n"] = D(p, "n"), ["a"] = 0.8, ["decay"] = 0.72, ["outline"] = true, ["color"] = c };
            if (v == "zoom") o["scale"] = 1 + 0.035 * amt;
            else if (v == "rise") o["dy"] = -d * 1.2;
            else if (v == "side") o["dx"] = d * 1.3;
            else
            {
                o["dx"] = d;
                o["dy"] = d;
            }
            it["echo"] = o;
        },
    });

    private static void RegisterTreatEmphasisDots() => RegT("emphasisDots", new TreatDef
    {
        Name = "傍点",
        Tags = new[] { "editorial", "emotional", "calm" },
        W = 0.8,
        Plan = (rng, st) => new JObject { ["v"] = rng.Pick(new[] { "dot", "dot", "sesame", "ring" }) },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it, 0.9)) return;
            var v = S(p, "v") ?? "dot";
            var dc = MarkCol(env.Sc!, ColOfIt(env, it));
            var vert = Truthy(it["vertical"]);
            var sx = DOr(it, "sx", 1);
            var sy = DOr(it, "sy", 1);
            // addPost → env.PostFn（见头注）
            AddPost(env, (e2, _) => WithFx(e2, it, () =>
            {
                if (env.Pass != "main") return;
                var s = D(it, "size");
                var a = NumNull(it, "alpha", 1);
                var o = 1 - J.E.InCubic(env.POut);
                var ctx = env.Ctx;
                foreach (var g in GlyphList(env, it))
                {
                    if (g.G.Ch.Length == 1 && J.IsPunct(g.G.Ch[0])) continue;
                    var q = J.E.OutBack(InP(env, it, env.Cut!.InDur * 0.45 + g.G.I * 0.04, 0.22), 2.2) * o;
                    if (q <= 0.01) continue;
                    var r = s * 0.07 * g.S * q * Math.Min(sx, sy);
                    var x = vert ? g.X + s * sx * 0.64 * g.S : g.X;
                    var y = vert ? g.Y : g.Y - s * sy * 0.64 * g.S;
                    if (v == "ring") env.Circle(x, y, r * 0.85, null, dc, Math.Max(1, r * 0.42), a * g.A, false);
                    else if (v == "sesame")
                    {
                        ctx.Save();
                        ctx.Translate((float)x, (float)y);
                        ctx.RotateRadians((float)(-40 * J.DEG));
                        using var dp = new SKPaint { IsAntialias = true, Color = Env.SkColor(dc, a * g.A) };
                        ctx.DrawOval(0, 0, (float)(r * 1.25), (float)(r * 0.6), dp);
                        ctx.Restore();
                    }
                    else env.Circle(x, y, r, dc, null, 0, a * g.A, false);
                }
            }));
        },
    });

    // ================= bg（JS bgReg(k, d) 出现序） =================

    private static void RegisterBgSunburst() => RegBg("sunburst", new BgDef
    {
        Name = "放射",
        Tags = new[] { "pop", "graphic" },
        W = 0.8,
        Plan = (rng, st) => new JObject
        {
            ["seed"] = (double)Bs(rng),
            ["n"] = rng.Pick(new[] { 12.0, 16.0, 20.0, 24.0 }),
            ["cx"] = rng.Pick(new[] { 0.5, 0.5, 0.3, 0.7 }),
            ["cy"] = rng.Pick(new[] { 0.5, 0.5, 0.62, 1.05 }),
            ["spd"] = rng.Range(3, 7) * rng.Pick(new[] { 1.0, -1.0 }),
            ["k"] = rng.Range(0.07, 0.1),
        },
        Draw = BgWrap((env, p) =>
        {
            var w = env.W;
            var h = env.H;
            var sc = env.Sc!;
            var cx = w * NumNull(p, "cx", 0.5);
            var cy = h * NumNull(p, "cy", 0.5);
            var n = (int)DOr(p, "n", 16);
            var e = J.E.OutCubic(J.Clamp(BgT(env) / 0.5));
            var r = Math.Sqrt(w * (double)w + h * (double)h) * 1.1 * (0.25 + 0.75 * e);
            var rot = (env.T * DOr(p, "spd", 4) + (1 - e) * 25) * J.DEG;
            var w2 = J.TAU / n * 0.5;
            var ctx = env.Ctx;
            using (var pt = new SKPaint { IsAntialias = true, Color = Env.SkColor(LayC(sc, DOr(p, "k", 0.08))) })
            using (var path = new SKPath())
            {
                for (var i = 0; i < n; i++)
                {
                    var a0 = rot + i / (double)n * J.TAU;
                    path.MoveTo((float)cx, (float)cy);
                    path.LineTo((float)(cx + Math.Cos(a0) * r), (float)(cy + Math.Sin(a0) * r));
                    path.LineTo((float)(cx + Math.Cos(a0 + w2) * r), (float)(cy + Math.Sin(a0 + w2) * r));
                    path.Close();
                }
                ctx.DrawPath(path, pt);
            }
            var rr = Umin(env) * 0.55;
            using (var g = SKShader.CreateRadialGradient(new SKPoint((float)cx, (float)cy), (float)rr,
                       new[] { Css(ScK(sc, "bg"), 0.85), Css(ScK(sc, "bg"), 0) }, new float[] { 0, 1 }, SKShaderTileMode.Clamp))
            using (var pt = new SKPaint { Shader = g })
                ctx.DrawRect(new SKRect((float)(cx - rr), (float)(cy - rr), (float)(cx + rr), (float)(cy + rr)), pt);
        }),
    });

    private static void RegisterBgConcentric() => RegBg("concentric", new BgDef
    {
        Name = "同心円",
        Tags = new[] { "calm", "graphic", "emotional" },
        W = 0.9,
        Plan = (rng, st) => new JObject
        {
            ["seed"] = (double)Bs(rng),
            ["gap"] = rng.Range(0.07, 0.11),
            ["spd"] = rng.Range(0.15, 0.35) * rng.Pick(new[] { 1.0, 1.0, -1.0 }),
            ["k"] = rng.Range(0.08, 0.12),
            ["cy"] = rng.Pick(new[] { 0.5, 0.5, 0.58 }),
        },
        Draw = BgWrap((env, p) =>
        {
            var w = env.W;
            var h = env.H;
            var sc = env.Sc!;
            var cx = w / 2.0;
            var cy = h * DOr(p, "cy", 0.5);
            var gap = Umin(env) * DOr(p, "gap", 0.09);
            var ph = WrapV(env.T * DOr(p, "spd", 0.2), 1);
            var bt = BeatOf(env);
            var pulse = bt.Has ? Math.Exp(-bt.Since * 5) : 0.5 + 0.5 * Math.Sin(env.T * J.TAU * 0.5);
            var rm = Math.Sqrt(w * (double)w + h * (double)h) * 0.6;
            var e = J.E.OutCubic(J.Clamp(BgT(env) / 0.6));
            using var pt = new SKPaint
            {
                IsAntialias = true,
                IsStroke = true,
                StrokeWidth = (float)(gap * (0.14 + 0.1 * pulse)),
                Color = Env.SkColor(LayC(sc, DOr(p, "k", 0.1) * (0.8 + 0.4 * pulse))),
            };
            using var path = new SKPath();
            for (var r = ph * gap; r < rm * e; r += gap)
            {
                if (r < 2) continue;
                path.AddCircle((float)cx, (float)cy, (float)r);
            }
            env.Ctx.DrawPath(path, pt);
        }),
    });

    private static readonly Dictionary<string, SKBitmap> HtCache = new();

    private static void RegisterBgHalftoneFade() => RegBg("halftoneFade", new BgDef
    {
        Name = "網点グラデ",
        Tags = new[] { "pop", "graphic", "editorial" },
        W = 1,
        Plan = (rng, st) => new JObject
        {
            ["seed"] = (double)Bs(rng),
            ["dir"] = (double)rng.Pick(new[] { 0, 45, 90, 135, 180, 225, 270, 315 }),
            ["cell"] = rng.Range(0.032, 0.045),
            ["k"] = rng.Range(0.12, 0.17),
        },
        Draw = BgWrap((env, p) =>
        {
            var w = env.W;
            var h = env.H;
            var sc = env.Sc!;
            var cell = Umin(env) * DOr(p, "cell", 0.032);
            var col = LayC(sc, DOr(p, "k", 0.14));
            var q = Math.Min(1, env.Scale != 0 ? env.Scale : 1);
            var key = string.Create(CultureInfo.InvariantCulture,
                $"{D(p, "seed"):0}|{w}|{h}|{q:0.###}|{col}|{D(p, "dir"):0}|{D(p, "cell"):0.######}");
            if (!HtCache.TryGetValue(key, out var cv))
            {
                if (HtCache.Count > 2)
                {
                    foreach (var kv in HtCache) kv.Value.Dispose();
                    HtCache.Clear();
                }
                var wc = w + cell * 2;
                var hc = h + cell * 2;
                var bw = Math.Max(2, (int)Math.Ceiling(wc * q));
                var bh = Math.Max(2, (int)Math.Ceiling(hc * q));
                cv = new SKBitmap(bw, bh);
                using (var x = new SKCanvas(cv))
                {
                    x.Scale((float)q, (float)q);
                    var ang = DOr(p, "dir", 0) * J.DEG;
                    var dx = Math.Cos(ang);
                    var dy = Math.Sin(ang);
                    var ext = Math.Abs(dx) * w / 2 + Math.Abs(dy) * h / 2;
                    using var pt = new SKPaint { IsAntialias = true, Color = Env.SkColor(col) };
                    using var path = new SKPath();
                    var row = 0;
                    for (var y = 0.0; y < hc + cell; y += cell * 0.866, row++)
                        for (var px = (row & 1) * cell / 2.0; px < wc + cell; px += cell)
                        {
                            var u = ((px - cell - w / 2) * dx + (y - cell - h / 2) * dy) / ext;
                            var r = cell * 0.52 * J.Smooth(-0.8, 1, u);
                            if (r < cell * 0.06) continue;
                            path.AddCircle((float)px, (float)y, (float)r);
                        }
                    x.DrawPath(path, pt);
                }
                HtCache[key] = cv;
            }
            using (var pt = ImgPaint(J.E.OutCubic(J.Clamp(BgT(env) / 0.5))))
                env.Ctx.DrawBitmap(cv, new SKRect((float)(-cell * 2 + WrapV(env.T * cell * 0.35, cell)), (float)-cell,
                    (float)(-cell * 2 + WrapV(env.T * cell * 0.35, cell) + w + cell * 2), (float)(-cell + h + cell * 2)), pt);
        }),
    });

    private static void RegisterBgBigStripes() => RegBg("bigStripes", new BgDef
    {
        Name = "大きな斜線",
        Tags = new[] { "graphic", "pop" },
        W = 0.9,
        Plan = (rng, st) => new JObject
        {
            ["seed"] = (double)Bs(rng),
            ["ang"] = rng.Pick(new[] { 30.0, 45.0, -30.0, -45.0, 60.0 }),
            ["w"] = rng.Range(0.06, 0.11),
            ["spd"] = rng.Range(0.03, 0.08) * rng.Pick(new[] { 1.0, -1.0 }),
            ["fill"] = rng.Pick(new[] { 0.5, 0.5, 0.3 }),
            ["k"] = rng.Range(0.05, 0.08),
        },
        Draw = BgWrap((env, p) =>
        {
            var w = env.W;
            var h = env.H;
            var sc = env.Sc!;
            var r = Math.Sqrt(w * (double)w + h * (double)h) / 2 + 10;
            var per = Umin(env) * DOr(p, "w", 0.08) * 2;
            var e = J.E.OutExpo(J.Clamp(BgT(env) / 0.5));
            var off = WrapV(env.T * Umin(env) * DOr(p, "spd", 0.05), per);
            var ctx = env.Ctx;
            ctx.Save();
            ctx.Translate((float)(w / 2.0), (float)(h / 2.0));
            ctx.RotateDegrees((float)DOr(p, "ang", 45));
            using (var pt = new SKPaint { IsAntialias = true, Color = Env.SkColor(LayC(sc, DOr(p, "k", 0.06))) })
            using (var path = new SKPath())
            {
                for (var x = -r - per + off; x < r; x += per)
                    path.AddRect(new SKRect((float)x, (float)-r, (float)(x + per * DOr(p, "fill", 0.5) * e), (float)r));
                ctx.DrawPath(path, pt);
            }
            ctx.Restore();
        }),
    });

    private static string SplitCol(Scheme sc, JObject p) =>
        S(p, "c") == "tint" ? TintC(sc, DOr(p, "k", 0.08) * 1.8) : LayC(sc, DOr(p, "k", 0.08));

    private static void RegisterBgSplitV() => RegBg("splitV", new BgDef
    {
        Name = "左右二色",
        Tags = new[] { "graphic", "editorial", "pop" },
        W = 1,
        Plan = (rng, st) => new JObject
        {
            ["seed"] = (double)Bs(rng),
            ["side"] = (double)rng.Pick(new[] { 1, -1 }),
            ["pos"] = rng.Range(0.4, 0.6),
            ["c"] = rng.Pick(new[] { "lay", "lay", "tint" }),
            ["k"] = rng.Range(0.08, 0.12),
        },
        Draw = BgWrap((env, p) =>
        {
            var w = env.W;
            var h = env.H;
            var sc = env.Sc!;
            var e = J.E.OutExpo(J.Clamp(BgT(env) / 0.5));
            var x = w * (DOr(p, "pos", 0.5) + 0.012 * Math.Sin(env.T * 0.6));
            var right = DOr(p, "side", 1) > 0;
            var edge = right ? J.Lerp(w, x, e) : J.Lerp(0, x, e);
            var ctx = env.Ctx;
            FillMode(ctx, right ? edge : -1, 0, right ? w - edge + 1 : edge + 1, h, SplitCol(sc, p));
            using (var pt = new SKPaint { Color = Env.SkColor(TintC(sc, 0.45), 0.6 * e) })
                ctx.DrawRect(new SKRect((float)(edge - 1), 0, (float)(edge + 1), (float)h), pt);
        }),
    });

    private static void RegisterBgSplitH() => RegBg("splitH", new BgDef
    {
        Name = "上下二色",
        Tags = new[] { "graphic", "editorial", "calm" },
        W = 0.9,
        Plan = (rng, st) => new JObject
        {
            ["seed"] = (double)Bs(rng),
            ["side"] = (double)rng.Pick(new[] { 1, -1 }),
            ["pos"] = rng.Range(0.42, 0.6),
            ["c"] = rng.Pick(new[] { "lay", "lay", "tint" }),
            ["k"] = rng.Range(0.08, 0.12),
        },
        Draw = BgWrap((env, p) =>
        {
            var w = env.W;
            var h = env.H;
            var sc = env.Sc!;
            var e = J.E.OutExpo(J.Clamp(BgT(env) / 0.5));
            var y = h * (DOr(p, "pos", 0.5) + 0.012 * Math.Sin(env.T * 0.5));
            var low = DOr(p, "side", 1) > 0;
            var edge = low ? J.Lerp(h, y, e) : J.Lerp(0, y, e);
            var ctx = env.Ctx;
            FillMode(ctx, 0, low ? edge : -1, w, low ? h - edge + 1 : edge + 1, SplitCol(sc, p));
            using (var pt = new SKPaint { Color = Env.SkColor(TintC(sc, 0.45), 0.6 * e) })
                ctx.DrawRect(new SKRect(0, (float)(edge - 1), (float)w, (float)(edge + 1)), pt);
        }),
    });

    private static void RegisterBgSplitDiag() => RegBg("splitDiag", new BgDef
    {
        Name = "斜め二色",
        Tags = new[] { "graphic", "pop" },
        W = 1,
        Plan = (rng, st) => new JObject
        {
            ["seed"] = (double)Bs(rng),
            ["side"] = (double)rng.Pick(new[] { 1, -1 }),
            ["pos"] = rng.Range(0.42, 0.58),
            ["ang"] = rng.Range(14, 30) * rng.Pick(new[] { 1.0, -1.0 }),
            ["c"] = rng.Pick(new[] { "lay", "lay", "tint" }),
            ["k"] = rng.Range(0.08, 0.12),
        },
        Draw = BgWrap((env, p) =>
        {
            var w = env.W;
            var h = env.H;
            var sc = env.Sc!;
            var e = J.E.OutExpo(J.Clamp(BgT(env) / 0.5));
            var side = DOr(p, "side", 1) > 0 ? 1 : -1;
            var tn = Math.Tan(DOr(p, "ang", 20) * J.DEG);
            var cx = w * DOr(p, "pos", 0.5) + (1 - e) * w * 0.9 * side + w * 0.01 * Math.Sin(env.T * 0.5);
            var xt = cx - tn * h / 2;
            var xb = cx + tn * h / 2;
            var x = side > 0 ? w + 10 : -10;
            var ctx = env.Ctx;
            using (var pt = new SKPaint { IsAntialias = true, Color = Env.SkColor(SplitCol(sc, p)) })
            using (var path = new SKPath())
            {
                path.MoveTo((float)xt, -1);
                path.LineTo((float)x, -1);
                path.LineTo((float)x, (float)(h + 1));
                path.LineTo((float)xb, (float)(h + 1));
                path.Close();
                ctx.DrawPath(path, pt);
            }
            using (var pt = new SKPaint
                   {
                       IsAntialias = true,
                       IsStroke = true,
                       StrokeWidth = 2,
                       Color = Env.SkColor(TintC(sc, 0.45), 0.6 * e),
                   })
            using (var path = new SKPath())
            {
                path.MoveTo((float)xt, -1);
                path.LineTo((float)xb, (float)(h + 1));
                ctx.DrawPath(path, pt);
            }
        }),
    });

    private static void RegisterBgGradientSweep() => RegBg("gradientSweep", new BgDef
    {
        Name = "グラデ",
        Tags = new[] { "calm", "emotional", "pop" },
        W = 1,
        Subtle = true,
        Plan = (rng, st) => new JObject
        {
            ["seed"] = (double)Bs(rng),
            ["spd"] = rng.Range(0.25, 0.5),
            ["k"] = rng.Range(0.16, 0.26),
            ["c"] = rng.Pick(new[] { "accent", "accent", "accent2", "fg" }),
            ["a0"] = rng.Range(0, 6.28),
        },
        Draw = BgWrap((env, p) =>
        {
            var w = env.W;
            var h = env.H;
            var sc = env.Sc!;
            var ang = DOr(p, "a0", 0) + env.T * 0.12;
            var dx = Math.Cos(ang);
            var dy = Math.Sin(ang);
            var l = Math.Sqrt(w * (double)w + h * (double)h) / 2;
            var cKey = S(p, "c") ?? "accent";
            var baseCol = sc.Str(cKey) ?? ScK(sc, "accent");
            var tint = J.Mix(ScK(sc, "bg"), baseCol, DOr(p, "k", 0.2));
            var pp = 0.5 + 0.32 * Math.Sin(env.T * DOr(p, "spd", 0.35));
            using (var g = SKShader.CreateLinearGradient(
                       new SKPoint((float)(w / 2.0 - dx * l), (float)(h / 2.0 - dy * l)),
                       new SKPoint((float)(w / 2.0 + dx * l), (float)(h / 2.0 + dy * l)),
                       new[] { Css(tint, 0), Css(tint, 1), Css(tint, 0) },
                       new[] { 0f, (float)pp, 1f }, SKShaderTileMode.Clamp))
            using (var pt = new SKPaint { Shader = g, Color = SKColors.White.WithAlpha((byte)Math.Round(J.E.OutCubic(J.Clamp(BgT(env) / 0.6)) * 255)) })
                env.Ctx.DrawRect(new SKRect(0, 0, w, h), pt);
        }),
    });

    private static void RegisterBgSpotlight() => RegBg("spotlight", new BgDef
    {
        Name = "スポットライト",
        Tags = new[] { "emotional", "calm", "editorial" },
        W = 0.9,
        Subtle = true,
        Plan = (rng, st) => new JObject { ["seed"] = (double)Bs(rng), ["beam"] = rng.Chance(0.6), ["k"] = rng.Range(0.1, 0.16) },
        Draw = BgWrap((env, p) =>
        {
            var w = env.W;
            var h = env.H;
            var sc = env.Sc!;
            var t = env.T;
            var s = DOr(p, "seed", 1);
            var u = Umin(env);
            var dk = IsDark(ScK(sc, "bg"));
            var cx = w * (0.5 + 0.2 * Math.Sin(t * 0.31 + s % 7));
            var cy = h * (0.52 + 0.08 * Math.Sin(t * 0.23 + s % 5));
            var r = u * 0.62;
            var e = J.E.OutCubic(J.Clamp(BgT(env) / 0.6));
            var lc = dk ? ScK(sc, "fg") : "#FFFFFF";
            var k = DOr(p, "k", 0.12) * e;
            var ctx = env.Ctx;
            using (var g1 = SKShader.CreateTwoPointConicalGradient(new SKPoint((float)cx, (float)cy), (float)(r * 0.3),
                       new SKPoint((float)cx, (float)cy), (float)(r * 1.7),
                       new[] { SKColors.Transparent, new SKColor(0, 0, 0, (byte)Math.Round((dk ? 0.35 : 0.14) * e * 255)) },
                       new float[] { 0, 1 }, SKShaderTileMode.Clamp))
            using (var pt = new SKPaint { Shader = g1 })
                ctx.DrawRect(new SKRect(0, 0, w, h), pt);
            using (var g2 = SKShader.CreateRadialGradient(new SKPoint((float)cx, (float)cy), (float)r,
                       new[] { Css(lc, k), Css(lc, k * 0.5), Css(lc, 0) }, new float[] { 0, 0.6f, 1 }, SKShaderTileMode.Clamp))
            using (var pt = new SKPaint { Shader = g2 })
                ctx.DrawRect(new SKRect((float)(cx - r), (float)(cy - r), (float)(cx + r), (float)(cy + r)), pt);
            if (Truthy(p["beam"]))
            {
                var tx = cx + (w / 2.0 - cx) * 0.5;
                using (var g3 = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, (float)cy),
                           new[] { Css(lc, k * 0.7), Css(lc, 0) }, new float[] { 0, 1 }, SKShaderTileMode.Clamp))
                using (var pt = new SKPaint { IsAntialias = true, Shader = g3 })
                using (var path = new SKPath())
                {
                    path.MoveTo((float)(tx - u * 0.04), -2);
                    path.LineTo((float)(tx + u * 0.04), -2);
                    path.LineTo((float)(cx + r * 0.75), (float)cy);
                    path.LineTo((float)(cx - r * 0.75), (float)cy);
                    path.Close();
                    ctx.DrawPath(path, pt);
                }
            }
        }),
    });

    private static void RegisterBgTvBars() => RegBg("tvBars", new BgDef
    {
        Name = "テレビの帯",
        Tags = new[] { "glitch", "graphic", "emotional" },
        W = 0.6,
        Plan = (rng, st) => new JObject
        {
            ["seed"] = (double)Bs(rng),
            ["n"] = (double)rng.Int(4, 7),
            ["spd"] = rng.Range(0.05, 0.14),
            ["k"] = rng.Range(0.06, 0.09),
        },
        Draw = BgWrap((env, p) =>
        {
            var w = env.W;
            var h = env.H;
            var sc = env.Sc!;
            var s = unchecked((int)DOr(p, "seed", 1));
            var k = DOr(p, "k", 0.07);
            var ctx = env.Ctx;
            for (var i = 0; i < (int)DOr(p, "n", 5); i++)
            {
                var hh = h * J.Rr(0.02, 0.15, s, i, 1);
                var v = h * DOr(p, "spd", 0.08) * (0.5 + J.R(s, i, 2));
                var y = WrapV(env.T * v + J.R(s, i, 3) * h * 1.4, h * 1.4) - h * 0.2;
                FillMode(ctx, 0, y, w, hh, i % 3 == 0 ? TintC(sc, k * 1.7) : LayC(sc, k * (0.6 + 0.8 * J.R(s, i, 4))));
                if (J.R(s, i, 5) < 0.5)
                    FillMode(ctx, 0, y - h * 0.012, w, Math.Max(1.5, h * 0.002), LayC(sc, k * 1.4));
            }
        }),
    });

    private static void RegisterBgChecker() => RegBg("checker", new BgDef
    {
        Name = "市松",
        Tags = new[] { "graphic", "pop" },
        W = 0.7,
        Plan = (rng, st) => new JObject
        {
            ["seed"] = (double)Bs(rng),
            ["n"] = (double)rng.Int(5, 8),
            ["k"] = rng.Range(0.04, 0.065),
            ["spd"] = rng.Range(0.1, 0.25),
            ["rot"] = (double)rng.Pick(new[] { 0, 0, 45 }),
        },
        Draw = BgWrap((env, p) =>
        {
            var w = env.W;
            var h = env.H;
            var sc = env.Sc!;
            var cell = Umin(env) / DOr(p, "n", 6) * (DOr(p, "rot", 0) != 0 ? 1.2 : 1);
            var r = Math.Sqrt(w * (double)w + h * (double)h) / 2 + cell * 2;
            var off = WrapV(env.T * cell * DOr(p, "spd", 0.15), cell * 2);
            var n = (int)Math.Ceiling(r / cell) + 2;
            var e = J.E.OutCubic(J.Clamp(BgT(env) / 0.4));
            var ctx = env.Ctx;
            ctx.Save();
            ctx.Translate((float)(w / 2.0), (float)(h / 2.0));
            ctx.RotateDegrees((float)DOr(p, "rot", 0));
            using (var pt = new SKPaint { IsAntialias = true, Color = Env.SkColor(LayC(sc, DOr(p, "k", 0.05)), e) })
            using (var path = new SKPath())
            {
                for (var j = -n; j <= n; j++)
                for (var i = -n; i <= n; i++)
                    if (((i + j) & 1) == 1)
                        path.AddRect(new SKRect((float)(i * cell + off - cell), (float)(j * cell + off - cell),
                            (float)(i * cell + off), (float)(j * cell + off)));
                ctx.DrawPath(path, pt);
            }
            ctx.Restore();
        }),
    });

    private static void RegisterBgBigChar() => RegBg("bigChar", new BgDef
    {
        Name = "巨大文字",
        Tags = new[] { "editorial", "emotional", "graphic", "calm" },
        W = 1,
        Subtle = true,
        Plan = (rng, st) => new JObject
        {
            ["seed"] = (double)Bs(rng),
            ["side"] = (double)rng.Pick(new[] { -1, 1 }),
            ["font"] = rng.Pick(new[] { "display", "serif" }),
            ["outline"] = rng.Chance(0.3),
            ["k"] = rng.Range(0.07, 0.1),
        },
        Draw = BgWrap((env, p) =>
        {
            var w = env.W;
            var h = env.H;
            var sc = env.Sc!;
            var src = !string.IsNullOrEmpty(env.Cut?.LineText) ? env.Cut.LineText : env.Cut?.Text ?? "";
            var arr = JLayouts.CodePoints(Regex.Replace(src, @"\s+", ""));
            string? ch = null;
            foreach (var c in arr)
                if (c.Length != 1 || !J.IsPunct(c[0]))
                {
                    ch = c;
                    break;
                }
            ch ??= arr.FirstOrDefault();
            if (string.IsNullOrEmpty(ch)) return;
            var font = JLayouts.FontsOf(env.St, S(p, "font") ?? "display")[0];
            var side = DOr(p, "side", 1);
            var e = J.E.OutCubic(J.Clamp(BgT(env) / 0.7));
            var size = Math.Max(w, h) * 0.6 * (1.06 - 0.06 * e + 0.015 * Math.Sin(env.T * 0.3));
            var it = new JObject
            {
                ["text"] = ch,
                ["font"] = font,
                ["size"] = size,
                ["x"] = w / 2.0 + side * w * 0.2 + Math.Sin(env.T * 0.2) * w * 0.01,
                ["y"] = h * 0.53,
                ["rot"] = side * 4.0,
                ["color"] = LayC(sc, DOr(p, "k", 0.08)),
                ["alpha"] = e,
                ["ghost"] = false,
            };
            if (Truthy(p["outline"]))
            {
                it["fill"] = false;
                it["stroke"] = Math.Max(2, size * 0.006);
                it["strokeColor"] = LayC(sc, DOr(p, "k", 0.08) * 2.4);
            }
            env.DrawItem(it);
        }),
    });

    private static void RegisterBgSpeedLines() => RegBg("speedLines", new BgDef
    {
        Name = "集中線",
        Tags = new[] { "pop", "emotional", "graphic" },
        W = 0.7,
        Plan = (rng, st) => new JObject
        {
            ["seed"] = (double)Bs(rng),
            ["n"] = (double)rng.Int(90, 140),
            ["k"] = rng.Range(0.2, 0.28),
            ["clear"] = rng.Range(0.3, 0.36),
        },
        Draw = BgWrap((env, p) =>
        {
            var w = env.W;
            var h = env.H;
            var sc = env.Sc!;
            var s = unchecked((int)DOr(p, "seed", 1));
            var st = unchecked((int)env.Step);
            var n = (int)DOr(p, "n", 100);
            var cx = w / 2.0;
            var cy = h / 2.0;
            var e = J.E.OutCubic(J.Clamp(BgT(env) / 0.3));
            var rx = w * DOr(p, "clear", 0.38) * (1 + (1 - e) * 0.6);
            var ry = h * DOr(p, "clear", 0.38) * 1.05 * (1 + (1 - e) * 0.6);
            var r = Math.Sqrt(w * (double)w + h * (double)h) * 0.6;
            using var pt = new SKPaint { IsAntialias = true, Color = Env.SkColor(LayC(sc, DOr(p, "k", 0.16))) };
            using var path = new SKPath();
            for (var i = 0; i < n; i++)
            {
                var a = (i + J.R(s, i, st, 1) * 0.8) / n * J.TAU;
                var f = 1 + J.R(s, i, st, 2) * 0.5;
                var w2 = J.TAU / n * (0.15 + 0.45 * J.R(s, i, st, 3));
                path.MoveTo((float)(cx + Math.Cos(a) * rx * f), (float)(cy + Math.Sin(a) * ry * f));
                path.LineTo((float)(cx + Math.Cos(a - w2 / 2) * r), (float)(cy + Math.Sin(a - w2 / 2) * r));
                path.LineTo((float)(cx + Math.Cos(a + w2 / 2) * r), (float)(cy + Math.Sin(a + w2 / 2) * r));
                path.Close();
            }
            env.Ctx.DrawPath(path, pt);
        }),
    });

    private static void RegisterBgScanBars() => RegBg("scanBars", new BgDef
    {
        Name = "走査線の帯",
        Tags = new[] { "calm", "glitch", "editorial" },
        W = 0.9,
        Subtle = true,
        Plan = (rng, st) => new JObject
        {
            ["seed"] = (double)Bs(rng),
            ["n"] = (double)rng.Int(2, 3),
            ["spd"] = rng.Range(0.06, 0.12),
            ["k"] = rng.Range(0.05, 0.08),
        },
        Draw = BgWrap((env, p) =>
        {
            var w = env.W;
            var h = env.H;
            var sc = env.Sc!;
            var s = unchecked((int)DOr(p, "seed", 1));
            var k = DOr(p, "k", 0.06);
            var ctx = env.Ctx;
            for (var i = 0; i < (int)DOr(p, "n", 2); i++)
            {
                var hh = h * (0.12 + 0.12 * J.R(s, i, 1));
                var y = WrapV(env.T * h * DOr(p, "spd", 0.08) * (0.8 + 0.4 * J.R(s, i, 2)) + J.R(s, i, 3) * h * 1.5, h * 1.5) - h * 0.3;
                using (var g = SKShader.CreateLinearGradient(new SKPoint(0, (float)y), new SKPoint(0, (float)(y + hh)),
                           new[] { Css(ScK(sc, "fg"), 0), Css(ScK(sc, "fg"), k), Css(ScK(sc, "fg"), 0) },
                           new[] { 0f, 0.85f, 1f }, SKShaderTileMode.Clamp))
                using (var pt = new SKPaint { Shader = g })
                    ctx.DrawRect(new SKRect(0, (float)y, (float)w, (float)(y + hh)), pt);
                FillMode(ctx, 0, y + hh * 0.86, w, Math.Max(1.5, h * 0.0018), ScK(sc, "fg"), k * 1.6);
            }
        }),
    });

    private static void RegisterBgDotGrid() => RegBg("dotGrid", new BgDef
    {
        Name = "ドット格子",
        Tags = new[] { "calm", "editorial", "graphic" },
        W = 1,
        Subtle = true,
        Plan = (rng, st) => new JObject
        {
            ["seed"] = (double)Bs(rng),
            ["sp"] = rng.Range(0.045, 0.065),
            ["k"] = rng.Range(0.22, 0.3),
            ["plus"] = rng.Chance(0.4),
        },
        Draw = BgWrap((env, p) =>
        {
            var w = env.W;
            var h = env.H;
            var sc = env.Sc!;
            var s = unchecked((int)DOr(p, "seed", 1));
            var u = Umin(env);
            var sp = u * DOr(p, "sp", 0.055);
            var r = Math.Max(1.5, u * 0.0036);
            var mx = env.T * 0.15;
            var my = env.T * 0.08;
            var ox = WrapV(mx * sp, sp);
            var oy = WrapV(my * sp, sp);
            var bx = Math.Floor(mx);
            var by = Math.Floor(my);
            var e = J.E.OutCubic(J.Clamp(BgT(env) / 0.5));
            var tw = (int)Math.Floor(env.T * 2);
            var ctx = env.Ctx;
            ctx.Save();
            using (var pt = new SKPaint { IsAntialias = true, Color = Env.SkColor(LayC(sc, DOr(p, "k", 0.2)), e) })
            using (var path = new SKPath())
            {
                var acc = new List<(double X, double Y)>();
                var j = -1;
                for (var y = oy - sp; y < h + sp; y += sp, j++)
                {
                    var i = -1;
                    for (var x = ox - sp; x < w + sp; x += sp, i++)
                    {
                        var id = (int)J.H(s, unchecked((int)(i - bx)), unchecked((int)(j - by)));
                        if (Truthy(p["plus"]) && Mod((int)(i - bx), 4) == 0 && Mod((int)(j - by), 4) == 0)
                        {
                            path.AddRect(new SKRect((float)(x - r * 3), (float)(y - r * 0.5), (float)(x + r * 3), (float)(y + r * 0.5)));
                            path.AddRect(new SKRect((float)(x - r * 0.5), (float)(y - r * 3), (float)(x + r * 0.5), (float)(y + r * 3)));
                            continue;
                        }
                        path.AddCircle((float)x, (float)y, (float)r);
                        if (J.R(id, tw) < 0.012) acc.Add((x, y));
                    }
                }
                ctx.DrawPath(path, pt);
                if (acc.Count > 0)
                {
                    using var pt2 = new SKPaint { IsAntialias = true, Color = Env.SkColor(TintC(sc, 0.55)) };
                    using var path2 = new SKPath();
                    foreach (var (x, y) in acc) path2.AddCircle((float)x, (float)y, (float)(r * 2));
                    ctx.DrawPath(path2, pt2);
                }
            }
            ctx.Restore();
        }),
    });

    /// <summary>JS 的 % 取余对负数回绕（dotGrid/eqBars 用）。</summary>
    private static int Mod(int v, int m) => ((v % m) + m) % m;

    private static void RegisterBgRetroGrid() => RegBg("retroGrid", new BgDef
    {
        Name = "レトロ格子",
        Tags = new[] { "pop", "glitch", "graphic" },
        W = 0.6,
        Plan = (rng, st) => new JObject
        {
            ["seed"] = (double)Bs(rng),
            ["hz"] = rng.Range(0.56, 0.64),
            ["spd"] = rng.Range(0.4, 0.8),
            ["sun"] = rng.Chance(0.4),
            ["k"] = rng.Range(0.38, 0.5),
        },
        Draw = BgWrap((env, p) =>
        {
            var w = env.W;
            var h = env.H;
            var sc = env.Sc!;
            var yh = h * DOr(p, "hz", 0.6);
            var col = TintC(sc, DOr(p, "k", 0.35));
            var e = J.E.OutCubic(J.Clamp(BgT(env) / 0.6));
            var ctx = env.Ctx;
            if (Truthy(p["sun"]))
            {
                var r = Math.Min(w, h) * 0.2;
                var sy = yh - r * 0.35;
                ctx.Save();
                ctx.ClipRect(new SKRect(0, 0, w, (float)yh));
                using (var pt = new SKPaint { IsAntialias = true, Color = Env.SkColor(TintC(sc, DOr(p, "k", 0.35) * 0.45)) })
                    ctx.DrawCircle((float)(w / 2.0), (float)sy, (float)(r * e), pt);
                for (var i = 0; i < 5; i++)
                {
                    var yy = sy + r * (0.1 + i * 0.18);
                    var hh = r * 0.03 * (1 + i * 0.6);
                    FillMode(ctx, w / 2.0 - r, yy, r * 2, hh, ScK(sc, "bg"));
                }
                ctx.Restore();
            }
            using (var g = SKShader.CreateLinearGradient(new SKPoint(0, (float)yh), new SKPoint(0, (float)h),
                       new[] { Css(col, 0), Css(col, 0.55 * e), Css(col, e) }, new float[] { 0, 0.3f, 1 }, SKShaderTileMode.Clamp))
            using (var pt = new SKPaint
                   {
                       IsAntialias = true,
                       IsStroke = true,
                       StrokeWidth = (float)Math.Max(1.5, Umin(env) * 0.0025),
                       Shader = g,
                   })
            using (var path = new SKPath())
            {
                const int nV = 12;
                var spB = w * 0.18;
                for (var i = -nV; i <= nV; i++)
                {
                    path.MoveTo((float)(w / 2.0 + i * spB * 0.06), (float)yh);
                    path.LineTo((float)(w / 2.0 + i * spB * 1.6), (float)(h + h * 0.3));
                }
                var ph = WrapV(env.T * DOr(p, "spd", 0.6), 1);
                for (var j = 0; j < 26; j++)
                {
                    var z = j + 1 - ph;
                    if (z <= 0.2) continue;
                    var y = yh + (h - yh) * 0.9 / z;
                    if (y > h + 2 || y - yh < 1.5) continue;
                    path.MoveTo(0, (float)y);
                    path.LineTo((float)w, (float)y);
                }
                ctx.DrawPath(path, pt);
            }
            using (var gh = SKShader.CreateLinearGradient(new SKPoint(0, (float)(yh - h * 0.1)), new SKPoint(0, (float)yh),
                       new[] { Css(col, 0), Css(col, 0.35 * e) }, new float[] { 0, 1 }, SKShaderTileMode.Clamp))
            using (var pt = new SKPaint { Shader = gh })
                ctx.DrawRect(new SKRect(0, (float)(yh - h * 0.1), (float)w, (float)yh), pt);
            FillMode(ctx, 0, yh - 1, w, 2, col, 0.8 * e);
        }),
    });

    private static void RegisterBgBokeh() => RegBg("bokehBg", new BgDef
    {
        Name = "ボケ玉",
        Tags = new[] { "emotional", "calm", "pop" },
        W = 1,
        Plan = (rng, st) => new JObject
        {
            ["seed"] = (double)Bs(rng),
            ["n"] = (double)rng.Int(12, 20),
            ["k"] = rng.Range(0.18, 0.28),
        },
        Draw = BgWrap((env, p) =>
        {
            var w = env.W;
            var h = env.H;
            var sc = env.Sc!;
            var s = unchecked((int)DOr(p, "seed", 1));
            var u = Umin(env);
            var t = env.T;
            var dk = IsDark(ScK(sc, "bg"));
            var cols = new[] { sc.Str("accent"), sc.Str("accent2"), sc.Str("fg"), sc.Str("ghostA"), sc.Str("ghostB") };
            var e = J.E.OutCubic(J.Clamp(BgT(env) / 0.8));
            var ctx = env.Ctx;
            for (var i = 0; i < (int)DOr(p, "n", 12); i++)
            {
                var r = u * J.Rr(0.035, 0.13, s, i, 1);
                var vx = J.Rs(s, i, 2) * u * 0.03;
                var vy = -u * J.Rr(0.01, 0.04, s, i, 3);
                var x = WrapV(J.R(s, i, 4) * w + t * vx, w + 2 * r) - r + Math.Sin(t * 0.4 + i) * u * 0.015;
                var y = WrapV(J.R(s, i, 5) * h + t * vy, h + 2 * r) - r;
                var c = cols[i % cols.Length] ?? "#ffffff";
                var a = DOr(p, "k", 0.2) * e * (0.5 + 0.5 * J.R(s, i, 6))
                        * (0.75 + 0.25 * Math.Sin(t * (0.6 + J.R(s, i, 7)) + i)) * (dk ? 1 : 0.7);
                using (var g = SKShader.CreateRadialGradient(new SKPoint((float)x, (float)y), (float)r,
                           new[] { Css(c, a * 0.7), Css(c, a), Css(c, a * 0.5), Css(c, 0) },
                           new float[] { 0, 0.8f, 0.92f, 1 }, SKShaderTileMode.Clamp))
                using (var pt = new SKPaint { IsAntialias = true, Shader = g, BlendMode = dk ? SKBlendMode.Screen : SKBlendMode.SrcOver })
                    ctx.DrawCircle((float)x, (float)y, (float)r, pt);
            }
        }),
    });

    private static void RegisterBgParticles() => RegBg("particlesBg", new BgDef
    {
        Name = "舞い上がる粒",
        Tags = new[] { "emotional", "calm", "pop" },
        W = 1,
        Plan = (rng, st) => new JObject
        {
            ["seed"] = (double)Bs(rng),
            ["n"] = (double)rng.Int(60, 100),
            ["k"] = rng.Range(0.45, 0.65),
            ["sq"] = rng.Chance(0.4),
        },
        Draw = BgWrap((env, p) =>
        {
            var w = env.W;
            var h = env.H;
            var sc = env.Sc!;
            var s = unchecked((int)DOr(p, "seed", 1));
            var u = Umin(env);
            var t = env.T;
            var n = Math.Min(140, (int)Math.Round(DOr(p, "n", 60) * Math.Sqrt(w * (double)h / (1920.0 * 1080))));
            var e = J.E.OutCubic(J.Clamp(BgT(env) / 0.6));
            var ctx = env.Ctx;
            for (var i = 0; i < n; i++)
            {
                var v = h * J.Rr(0.04, 0.14, s, i, 1);
                var y = h + 10 - WrapV(t * v + J.R(s, i, 2) * h * 1.2, h * 1.2);
                var x = J.R(s, i, 3) * w + Math.Sin(t * J.Rr(0.4, 1.2, s, i, 4) + i) * u * 0.02;
                var sz = u * J.Rr(0.0025, 0.008, s, i, 5);
                var a = DOr(p, "k", 0.45) * e * J.Clamp(y / (h * 0.3)) * (0.55 + 0.45 * J.R(s, i, 6));
                if (a <= 0.01) continue;
                var col = J.R(s, i, 7) < 0.22 ? ScK(sc, "accent") : LayC(sc, 0.6);
                if (Truthy(p["sq"])) FillMode(ctx, x - sz, y - sz, sz * 2, sz * 2, col, a);
                else
                {
                    using var pt = new SKPaint { IsAntialias = true, Color = Env.SkColor(col, a) };
                    ctx.DrawCircle((float)x, (float)y, (float)sz, pt);
                }
            }
        }),
    });

    private static void RegisterBgRipples() => RegBg("ripples", new BgDef
    {
        Name = "波紋",
        Tags = new[] { "calm", "emotional", "graphic" },
        W = 0.9,
        Plan = (rng, st) => new JObject
        {
            ["seed"] = (double)Bs(rng),
            ["centre"] = rng.Chance(0.4),
            ["life"] = rng.Range(1.6, 2.4),
            ["k"] = rng.Range(0.24, 0.34),
        },
        Draw = BgWrap((env, p) =>
        {
            var w = env.W;
            var h = env.H;
            var sc = env.Sc!;
            var s = unchecked((int)DOr(p, "seed", 1));
            var u = Umin(env);
            var life = DOr(p, "life", 1.8);
            var rings = new List<(int Idx, double Age)>();
            var bt = BeatOf(env);
            if (bt.Has && bt.Len > 0.2)
            {
                var per = bt.Len;
                var stp = per < 0.4 ? 2 : 1;
                var b0 = bt.Index;
                var since = bt.Since;
                if (stp == 2 && b0 % 2 == 1)
                {
                    since += per;
                    b0 -= 1;
                }
                for (var j = 0; j < 8; j++)
                {
                    var age = since + j * per * stp;
                    if (age > life) break;
                    rings.Add((b0 - j * stp, age));
                }
            }
            else
            {
                const double per = 0.7;
                var idx = (int)Math.Floor(env.T / per);
                for (var j = 0; j < 4; j++)
                {
                    var age = env.T - (idx - j) * per;
                    if (age > life) break;
                    rings.Add((idx - j, age));
                }
            }
            var ctx = env.Ctx;
            foreach (var (idx, age) in rings)
            {
                var q = J.Clamp(age / life);
                var r = u * (0.05 + 0.75 * J.E.OutCubic(q));
                var a = DOr(p, "k", 0.2) * Math.Pow(1 - q, 1.4);
                var x = Truthy(p["centre"]) ? w / 2.0 : w * J.Rr(0.15, 0.85, s, idx, 1);
                var y = Truthy(p["centre"]) ? h / 2.0 : h * J.Rr(0.2, 0.8, s, idx, 2);
                using (var pt = new SKPaint
                       {
                           IsAntialias = true,
                           IsStroke = true,
                           StrokeWidth = (float)(u * (0.008 * (1 - q) + 0.002)),
                           Color = Env.SkColor(ScK(sc, "fg"), a),
                       })
                    ctx.DrawCircle((float)x, (float)y, (float)r, pt);
                using (var pt = new SKPaint
                       {
                           IsAntialias = true,
                           IsStroke = true,
                           StrokeWidth = (float)(u * 0.0015),
                           Color = Env.SkColor(ScK(sc, "fg"), a * 0.5),
                       })
                    ctx.DrawCircle((float)x, (float)y, (float)(r * 0.8), pt);
            }
        }),
    });

    private static void RegisterBgPolka() => RegBg("polka", new BgDef
    {
        Name = "水玉",
        Tags = new[] { "pop", "graphic" },
        W = 0.7,
        Plan = (rng, st) => new JObject
        {
            ["seed"] = (double)Bs(rng),
            ["sp"] = rng.Range(0.11, 0.16),
            ["r"] = rng.Range(0.2, 0.3),
            ["k"] = rng.Range(0.06, 0.09),
            ["acc"] = rng.Chance(0.3),
        },
        Draw = BgWrap((env, p) =>
        {
            var w = env.W;
            var h = env.H;
            var sc = env.Sc!;
            var sp = Umin(env) * DOr(p, "sp", 0.13);
            var r = sp * DOr(p, "r", 0.25);
            var rh = sp * 0.866;
            var ox = WrapV(env.T * sp * 0.25, sp);
            var oy = WrapV(env.T * sp * 0.15, rh * 2);
            var e = J.E.OutBack(J.Clamp(BgT(env) / 0.45), 1.5);
            using var pt = new SKPaint { IsAntialias = true, Color = Env.SkColor(Truthy(p["acc"]) ? TintC(sc, DOr(p, "k", 0.07) * 1.8) : LayC(sc, DOr(p, "k", 0.07))) };
            using var path = new SKPath();
            var j = 0;
            for (var y = oy - rh * 2; y < h + rh; y += rh, j++)
                for (var x = ox - sp * 2 + (j & 1) * sp / 2; x < w + sp; x += sp)
                    path.AddCircle((float)x, (float)y, (float)(r * e));
            env.Ctx.DrawPath(path, pt);
        }),
    });

    private static void RegisterBgEqBars() => RegBg("eqBars", new BgDef
    {
        Name = "背景イコライザー",
        Tags = new[] { "pop", "glitch", "graphic" },
        W = 0.6,
        Plan = (rng, st) => new JObject
        {
            ["seed"] = (double)Bs(rng),
            ["n"] = rng.Pick(new[] { 24.0, 32.0, 40.0 }),
            ["mode"] = rng.Pick(new[] { "bottom", "bottom", "mirror", "center" }),
            ["seg"] = rng.Chance(0.4),
            ["k"] = rng.Range(0.09, 0.14),
        },
        Draw = BgWrap((env, p) =>
        {
            var w = env.W;
            var h = env.H;
            var sc = env.Sc!;
            var s = unchecked((int)DOr(p, "seed", 1));
            var n = (int)DOr(p, "n", 32);
            var bw = w / (double)n;
            var t = env.T;
            var en = env.Plan.Energy is { Length: > 0 } ? env.Energy : 0.45 + 0.2 * J.Noise1(t * 2, s);
            var bt = BeatOf(env);
            var beatK = bt.Has ? Math.Exp(-bt.Since * 6) : Math.Exp(-WrapV(t * 2, 1) * 4);
            var e = J.E.OutCubic(J.Clamp(BgT(env) / 0.4));
            var mode = S(p, "mode") ?? "bottom";
            var segH = bw * 0.45;
            var main = LayC(sc, DOr(p, "k", 0.11));
            var cap = TintC(sc, 0.4);
            var ctx = env.Ctx;

            void Bar(SKPath path, double x, double y0, double hh, int dir)
            {
                if (hh < 1) return;
                if (Truthy(p["seg"]))
                    for (var y = 0.0; y + segH * 0.7 <= hh; y += segH)
                        path.AddRect(new SKRect((float)x, (float)(dir > 0 ? y0 + y : y0 - y - segH * 0.7),
                            (float)(x + bw * 0.62), (float)((dir > 0 ? y0 + y : y0 - y - segH * 0.7) + segH * 0.7)));
                else
                    path.AddRect(new SKRect((float)x, (float)(dir > 0 ? y0 : y0 - hh), (float)(x + bw * 0.62), (float)(dir > 0 ? y0 + hh : y0)));
            }

            using (var pt = new SKPaint { IsAntialias = true, Color = Env.SkColor(main) })
            using (var path = new SKPath())
            {
                var tops = new List<(double X, double Y)>();
                for (var i = 0; i < n; i++)
                {
                    var sh = 0.55 + 0.45 * Math.Sin(Math.PI * (i + 0.5) / n);
                    var nz = 0.5 + 0.5 * J.Noise1(t * 4 + i * 1.7, s + i);
                    var lvl = J.Clamp(sh * (0.25 + 0.75 * en) * (0.55 + 0.45 * nz) + beatK * 0.18 * nz) * e;
                    var hh = h * 0.32 * lvl;
                    var x = i * bw + bw * 0.19;
                    if (mode == "center")
                    {
                        Bar(path, x, h / 2.0, hh * 0.7, -1);
                        Bar(path, x, h / 2.0, hh * 0.7, 1);
                    }
                    else
                    {
                        Bar(path, x, h, hh, -1);
                        tops.Add((x, h - hh));
                        if (mode == "mirror") Bar(path, x, 0, hh * 0.7, 1);
                    }
                }
                ctx.DrawPath(path, pt);
                if (tops.Count > 0)
                {
                    using var pt2 = new SKPaint { IsAntialias = true, Color = Env.SkColor(cap) };
                    using var path2 = new SKPath();
                    foreach (var (x, y) in tops)
                        path2.AddRect(new SKRect((float)x, (float)(y - bw * 0.3), (float)(x + bw * 0.62), (float)(y - bw * 0.3 + Math.Max(2, bw * 0.12))));
                    ctx.DrawPath(path2, pt2);
                }
            }
        }),
    });

    private static void RegisterBgBorderFrame() => RegBg("borderFrame", new BgDef
    {
        Name = "太枠",
        Tags = new[] { "graphic", "pop", "editorial" },
        W = 0.7,
        Plan = (rng, st) => new JObject
        {
            ["seed"] = (double)Bs(rng),
            ["th"] = rng.Range(0.012, 0.022),
            ["m"] = rng.Range(0.03, 0.05),
            ["acc"] = rng.Chance(0.6),
            ["dbl"] = rng.Chance(0.4),
        },
        Draw = BgWrap((env, p) =>
        {
            var w = env.W;
            var h = env.H;
            var sc = env.Sc!;
            var u = Umin(env);
            var m = u * DOr(p, "m", 0.04);
            var th = u * DOr(p, "th", 0.016);
            var e = J.E.InOutCubic(J.Clamp(BgT(env) / 0.6));
            if (e <= 0) return;
            var col = FirstOK(Truthy(p["acc"]) ? new[] { sc.Str("accent"), sc.Str("ink"), sc.Str("fg") } : new[] { sc.Str("ink"), sc.Str("fg") },
                c => Ctr(c, ScK(sc, "bg")) >= 1.6, ScK(sc, "fg"))!;
            var x0 = m + th / 2;
            var y0 = m + th / 2;
            var x1 = w - m - th / 2;
            var y1 = h - m - th / 2;
            env.PolyPartial(new[] { new[] { x0 - th / 2, y0 }, new[] { x1, y0 }, new[] { x1, y1 + th / 2 } }, e, col, th, 1, false);
            env.PolyPartial(new[] { new[] { x1 + th / 2, y1 }, new[] { x0, y1 }, new[] { x0, y0 - th / 2 } }, e, col, th, 1, false);
            if (Truthy(p["dbl"]))
            {
                var m2 = m + th * 2.4;
                var lw = Math.Max(1, th * 0.18);
                env.PolyPartial(new[] { new[] { m2, m2 }, new[] { w - m2, m2 }, new[] { w - m2, h - m2 }, new[] { m2, h - m2 }, new[] { m2, m2 } },
                    e, col, lw, 0.45, false);
            }
        }),
    });

    private static void RegisterBgLetterbox() => RegBg("letterbox", new BgDef
    {
        Name = "シネスコ帯",
        Tags = new[] { "emotional", "editorial", "calm" },
        W = 0.6,
        Plan = (rng, st) => new JObject { ["seed"] = (double)Bs(rng), ["k"] = rng.Range(0.085, 0.11), ["line"] = rng.Chance(0.6) },
        Draw = BgWrap((env, p) =>
        {
            var w = env.W;
            var h = env.H;
            var sc = env.Sc!;
            var dk = IsDark(ScK(sc, "bg"));
            var e = J.E.OutExpo(J.Clamp(BgT(env) / 0.55));
            var bh = (w >= h ? h * DOr(p, "k", 0.1) : h * 0.05) * e;
            if (bh < 0.5) return;
            var ctx = env.Ctx;
            FillMode(ctx, 0, -1, w, bh + 1, dk ? J.Mix(ScK(sc, "bg"), "#000000", 0.85) : J.Mix(ScK(sc, "fg"), "#000000", 0.3));
            FillMode(ctx, 0, h - bh, w, bh + 1, dk ? J.Mix(ScK(sc, "bg"), "#000000", 0.85) : J.Mix(ScK(sc, "fg"), "#000000", 0.3));
            if (Truthy(p["line"]) || dk)
            {
                FillMode(ctx, 0, bh, w, Math.Max(1.2, h * 0.0015), dk ? LayC(sc, 0.3) : TintC(sc, 0.6));
                FillMode(ctx, 0, h - bh - Math.Max(1.2, h * 0.0015), w, Math.Max(1.2, h * 0.0015), dk ? LayC(sc, 0.3) : TintC(sc, 0.6));
            }
        }),
    });

    private static void RegisterBgNoiseField() => RegBg("noiseField", new BgDef
    {
        Name = "ノイズの揺らぎ",
        Tags = new[] { "glitch", "emotional" },
        W = 0.6,
        Plan = (rng, st) => new JObject
        {
            ["seed"] = (double)Bs(rng),
            ["n"] = (double)rng.Int(14, 22),
            ["k"] = rng.Range(0.05, 0.085),
            ["th"] = rng.Range(0.56, 0.66),
        },
        Draw = BgWrap((env, p) =>
        {
            var w = env.W;
            var h = env.H;
            var sc = env.Sc!;
            var s = unchecked((int)DOr(p, "seed", 1));
            var cell = Umin(env) / DOr(p, "n", 18);
            var t = env.T;
            var st = unchecked((int)env.Step);
            var cols = (int)Math.Ceiling(w / cell);
            var rows = (int)Math.Ceiling(h / cell);
            var th = DOr(p, "th", 0.6);
            var k = DOr(p, "k", 0.07);
            var paths = new[] { new SKPath(), new SKPath(), new SKPath() };
            using var acc = new SKPath();
            var e = J.Clamp(BgT(env) / 0.3);
            for (var j = 0; j < rows; j++)
            {
                var shift = J.R(s, j, st, 7) < 0.05 ? (int)Math.Round(J.Rs(s, j, st, 8) * 4) * cell : 0;
                for (var i = 0; i < cols; i++)
                {
                    var v = 0.5 + 0.5 * (J.Noise1(i * 0.23 + t * 0.7, s + j * 31) * 0.6 + J.Noise1(j * 0.29 - t * 0.5, s + i * 17 + 999) * 0.4);
                    v += (J.R(s, i, j, st) - 0.5) * 0.12;
                    if (v < th + (1 - e) * 0.4) continue;
                    var lv = v > th + 0.2 ? 2 : v > th + 0.1 ? 1 : 0;
                    var target = J.R(s, i, j, st >> 1) < 0.01 ? acc : paths[lv];
                    target.AddRect(new SKRect((float)(i * cell + shift), (float)(j * cell),
                        (float)(i * cell + shift + cell - 1), (float)(j * cell + cell - 1)));
                }
            }
            var ctx = env.Ctx;
            var mults = new[] { 0.6, 1.0, 1.6 };
            for (var l = 0; l < 3; l++)
            {
                using var pt = new SKPaint { Color = Env.SkColor(LayC(sc, k * mults[l])) };
                ctx.DrawPath(paths[l], pt);
                paths[l].Dispose();
            }
            using (var pt = new SKPaint { Color = Env.SkColor(TintC(sc, 0.3)) })
                ctx.DrawPath(acc, pt);
        }),
    });

    // ================= cam（JS reg('cam', …) 出现序） =================

    private static void RegisterCamPullOut() => RegCam("pullOut", new CamDef
    {
        Name = "引き",
        Tags = new[] { "calm", "emotional", "editorial" },
        W = 1,
        Plan = (rng, st) => new JObject { ["a"] = rng.Range(0.06, 0.09) },
        Get = env => new CamState
        {
            S = 1 + DOr(env.Cut?.CamP ?? new JObject(), "a", 0.07) * Km(env) * (1 - J.E.OutCubic(CuOf(env))),
        },
    });

    private static void RegisterCamPanL() => RegCam("panL", new CamDef
    {
        Name = "左パン",
        Tags = new[] { "calm", "editorial", "emotional", "graphic" },
        W = 0.9,
        Plan = (rng, st) => new JObject { ["a"] = rng.Range(0.018, 0.028) },
        Get = env => new CamState
        {
            X = (PanP(CuOf(env)) - 0.5) * 2 * env.W * DOr(env.Cut?.CamP ?? new JObject(), "a", 0.022) * Km(env),
            S = 1.02,
        },
    });

    private static void RegisterCamPanR() => RegCam("panR", new CamDef
    {
        Name = "右パン",
        Tags = new[] { "calm", "editorial", "emotional", "graphic" },
        W = 0.9,
        Plan = (rng, st) => new JObject { ["a"] = rng.Range(0.018, 0.028) },
        Get = env => new CamState
        {
            X = -(PanP(CuOf(env)) - 0.5) * 2 * env.W * DOr(env.Cut?.CamP ?? new JObject(), "a", 0.022) * Km(env),
            S = 1.02,
        },
    });

    private static void RegisterCamTiltUp() => RegCam("tiltUp", new CamDef
    {
        Name = "ティルト",
        Tags = new[] { "calm", "emotional", "editorial" },
        W = 0.8,
        Plan = (rng, st) => new JObject { ["a"] = rng.Range(0.02, 0.03) },
        Get = env => new CamState
        {
            Y = (PanP(CuOf(env)) - 0.5) * 2 * env.H * DOr(env.Cut?.CamP ?? new JObject(), "a", 0.025) * Km(env),
            S = 1.02,
        },
    });

    private static void RegisterCamDutch() => RegCam("dutch", new CamDef
    {
        Name = "ダッチ",
        Tags = new[] { "emotional", "glitch", "graphic" },
        W = 0.8,
        Plan = (rng, st) => new JObject { ["dir"] = (double)rng.Pick(new[] { 1, -1 }), ["a"] = rng.Range(2.5, 4.5) },
        Get = env =>
        {
            var p = env.Cut?.CamP ?? new JObject();
            var k = Km(env);
            return new CamState
            {
                Rot = DOr(p, "dir", 1) * Math.Min(5, DOr(p, "a", 3.5) * k)
                      * J.E.InOutSine(J.Clamp(env.Lt / Math.Max(0.3, (env.Cut?.Dur ?? 0) * 0.8))),
                S = 1 + 0.03 * k * CuOf(env),
            };
        },
    });

    private static void RegisterCamHandheld() => RegCam("handheld", new CamDef
    {
        Name = "手持ち",
        Tags = new[] { "emotional", "calm", "editorial" },
        W = 1,
        Plan = (rng, st) => new JObject { ["f"] = rng.Range(0.8, 1.3) },
        Get = env =>
        {
            var k = Km(env);
            var f = DOr(env.Cut?.CamP ?? new JObject(), "f", 1);
            var t = env.Lt;
            var sd = unchecked((int)(env.Cut?.Seed ?? 0));
            return new CamState
            {
                X = (J.Noise1(t * f * 1.1, sd) * 0.7 + J.Noise1(t * f * 2.9, sd + 1) * 0.3) * env.W * 0.007 * k,
                Y = (J.Noise1(t * f * 0.9, sd + 2) * 0.7 + J.Noise1(t * f * 3.3, sd + 3) * 0.3) * env.H * 0.009 * k,
                Rot = J.Noise1(t * f * 0.8, sd + 4) * 0.7 * k,
                S = 1.012,
            };
        },
    });

    private static void RegisterCamBeatPunch() => RegCam("beatPunch", new CamDef
    {
        Name = "拍でズーム",
        Tags = new[] { "pop", "glitch", "graphic" },
        W = 0.9,
        Plan = (rng, st) => new JObject { ["a"] = rng.Range(0.03, 0.045) },
        Get = env =>
        {
            var bt = BeatOf(env);
            double since;
            double len = 0.5;
            if (bt.Has)
            {
                len = bt.Len;
                since = bt.Since - LagOf(env);
                if (since < 0) since += len;
            }
            else since = WrapV(env.Lt, 0.5);
            var k = Math.Exp(-since * 9);
            return new CamState
            {
                S = 1 + DOr(env.Cut?.CamP ?? new JObject(), "a", 0.035) * Km(env) * k,
                Y = -env.H * 0.004 * Km(env) * k,
            };
        },
    });

    private static void RegisterCamWhipIn() => RegCam("whipIn", new CamDef
    {
        Name = "ホイップイン",
        Tags = new[] { "pop", "glitch", "graphic" },
        W = 0.7,
        Strong = true,
        Plan = (rng, st) => new JObject
        {
            ["dir"] = rng.Pick(new[] { "L", "R", "L", "R", "U", "D" }),
            ["d"] = rng.Range(0.16, 0.24),
        },
        Get = env =>
        {
            var p = env.Cut?.CamP ?? new JObject();
            var k = Km(env);
            var r = 1 - J.E.OutExpo(J.Clamp(env.Lt / 0.3));
            if (r <= 0.001) return new CamState();
            var d = DOr(p, "d", 0.2) * k * r;
            var dir = S(p, "dir") ?? "R";
            var sg = dir is "L" or "U" ? -1 : 1;
            var hor = dir is "L" or "R";
            return new CamState
            {
                X = hor ? sg * env.W * d : 0,
                Y = hor ? 0 : sg * env.H * d * 0.7,
                Skx = hor ? sg * 9 * r * k : 0,
                S = 1 + 0.04 * r,
                Blur = 22 * k * r,
            };
        },
    });

    private static void RegisterCamCrashZoom() => RegCam("crashZoom", new CamDef
    {
        Name = "クラッシュズーム",
        Tags = new[] { "pop", "glitch", "emotional" },
        W = 0.6,
        Strong = true,
        Plan = (rng, st) => new JObject { ["at"] = rng.Range(0.6, 0.75), ["a"] = rng.Range(0.08, 0.11) },
        Get = env =>
        {
            var p = env.Cut?.CamP ?? new JObject();
            var k = Km(env);
            var dur = env.Cut?.Dur ?? 0;
            var tc = dur < 0.8 ? dur * 0.5 : Math.Max(dur * DOr(p, "at", 0.65), dur - 0.6);
            var dt = env.Lt - tc;
            if (dt < 0)
                return new CamState { S = 1 - 0.008 * k * J.Clamp((env.Lt - tc + 0.25) / 0.25) };
            var q = J.E.OutExpo(J.Clamp(dt / 0.1));
            var sd = unchecked((int)(env.Cut?.Seed ?? 0));
            var sh = Math.Exp(-dt * 6) * q;
            return new CamState
            {
                S = 1 + Math.Min(0.12, DOr(p, "a", 0.1) * k) * q,
                Blur = 12 * k * Math.Max(0, 1 - Math.Abs(dt - 0.05) / 0.08),
                X = J.Rs(sd, unchecked((int)env.Step), 1) * env.W * 0.004 * sh * k,
                Y = J.Rs(sd, unchecked((int)env.Step), 2) * env.H * 0.004 * sh * k,
            };
        },
    });

    private static void RegisterCamBounce() => RegCam("bounce", new CamDef
    {
        Name = "バウンス",
        Tags = new[] { "pop", "graphic" },
        W = 0.9,
        Plan = (rng, st) => new JObject { ["a"] = rng.Range(0.045, 0.065) },
        Get = env =>
        {
            var k = Km(env);
            var t = env.Lt;
            var d = Math.Exp(-t * 5.5);
            return new CamState
            {
                S = 1 - DOr(env.Cut?.CamP ?? new JObject(), "a", 0.07) * k * d * Math.Cos(t * 16),
                Y = -env.H * 0.012 * k * d * Math.Sin(t * 16),
            };
        },
    });

    private static void RegisterCamRoll() => RegCam("roll", new CamDef
    {
        Name = "ロール",
        Tags = new[] { "emotional", "calm", "glitch" },
        W = 0.8,
        Plan = (rng, st) => new JObject { ["dir"] = (double)rng.Pick(new[] { 1, -1 }), ["a"] = rng.Range(2.5, 4) },
        Get = env => new CamState
        {
            Rot = DOr(env.Cut?.CamP ?? new JObject(), "dir", 1) * (CuOf(env) - 0.5) * DOr(env.Cut?.CamP ?? new JObject(), "a", 3) * Km(env),
            S = 1.025,
        },
    });

    private static void RegisterCamDriftDiag() => RegCam("driftDiag", new CamDef
    {
        Name = "斜めドリフト",
        Tags = new[] { "calm", "emotional", "editorial", "graphic" },
        W = 1,
        Plan = (rng, st) => new JObject { ["dx"] = (double)rng.Pick(new[] { 1, -1 }), ["dy"] = (double)rng.Pick(new[] { 1, -1 }) },
        Get = env =>
        {
            var p = env.Cut?.CamP ?? new JObject();
            var k = Km(env);
            var u = J.E.InOutSine(CuOf(env)) * 0.5 + CuOf(env) * 0.5;
            return new CamState
            {
                X = (u - 0.5) * env.W * 0.035 * k * DOr(p, "dx", 1),
                Y = (u - 0.5) * env.H * 0.03 * k * DOr(p, "dy", 1),
                S = 1.02 + 0.025 * k * u,
            };
        },
    });

    private static void RegisterCamShakeHard() => RegCam("shakeHard", new CamDef
    {
        Name = "強い揺れ",
        Tags = new[] { "glitch", "pop", "emotional" },
        W = 0.6,
        Strong = true,
        Get = env =>
        {
            var k = Km(env);
            var amp = Math.Exp(-env.Lt * 4.5) * k;
            if (amp < 0.01) return new CamState();
            var sd = unchecked((int)(env.Cut?.Seed ?? 0));
            var st = unchecked((int)env.Step);
            return new CamState
            {
                X = J.Rs(sd, st, 1) * env.W * 0.022 * amp,
                Y = J.Rs(sd, st, 2) * env.H * 0.02 * amp,
                Rot = J.Rs(sd, st, 3) * 1.6 * amp,
                S = 1 + 0.03 * amp,
                Blur = 2.5 * amp,
            };
        },
    });

    private static void RegisterCamDollyIn() => RegCam("dollyIn", new CamDef
    {
        Name = "ドリー",
        Tags = new[] { "emotional", "calm", "editorial" },
        W = 1,
        Plan = (rng, st) => new JObject { ["a"] = rng.Range(0.08, 0.11) },
        Get = env =>
        {
            var k = Km(env);
            var q = J.E.InCubic(CuOf(env));
            return new CamState
            {
                S = 1 + Math.Min(0.14, DOr(env.Cut?.CamP ?? new JObject(), "a", 0.1) * k) * q,
                Y = -env.H * 0.008 * k * q,
            };
        },
    });

    private static void RegisterCamStepZoom() => RegCam("stepZoom", new CamDef
    {
        Name = "段階ズーム",
        Tags = new[] { "pop", "graphic", "glitch" },
        W = 0.8,
        Plan = (rng, st) => new JObject { ["n"] = (double)rng.Int(2, 3), ["a"] = rng.Range(0.035, 0.045) },
        Get = env =>
        {
            var p = env.Cut?.CamP ?? new JObject();
            var k = Km(env);
            var a = DOr(p, "a", 0.04) * k;
            var s = 1.0;
            var blur = 0.0;
            foreach (var ti in StepTimes(env, (int)DOr(p, "n", 2)))
            {
                var d = env.Lt - ti;
                if (d < 0) continue;
                s += a * J.E.OutExpo(J.Clamp(d / 0.07));
                blur += 5 * k * (1 - J.Clamp(d / 0.06));
            }
            return new CamState { S = Math.Min(1.15, s), Blur = blur };
        },
    });

    // ================= fx（JS fxReg(k, d) 出现序；坐标一律设备像素） =================

    private static void RegF(string key, FxDef def) => Reg.Register("fx", key, def, Pk);

    private static void RegisterFxPanelWipe() => RegF("panelWipe", new FxDef
    {
        Name = "パネルワイプ",
        Tags = new[] { "pop", "graphic" },
        W = 1,
        Dur = 5,
        Pre = 2,
        Amp = 1,
        Draw = FxWrapped((env, ev, k) =>
        {
            var cw = FxPlug.Cw;
            var ch = FxPlug.Ch;
            var sc = env.Sc!;
            var ctx = env.Ctx;
            var s = unchecked((int)EvS(ev));
            const double b = 0.4;
            var dir = J.R(s, 1) < 0.5 ? 1 : -1;
            var vert = J.R(s, 2) < 0.28;
            var l = vert ? ch : cw;
            var m = vert ? cw : ch;
            var sl = m * 0.3;
            double a0, a1;
            if (k < b)
            {
                a0 = 0;
                a1 = J.E.OutCubic(k / b);
            }
            else
            {
                a0 = J.E.InCubic((k - b) / (1 - b));
                a1 = 1;
            }
            double[] Pt(double x, double y)
            {
                var xx = dir > 0 ? x : l - x;
                return vert ? new[] { y, xx } : new[] { xx, y };
            }
            void Band(double u0, double u1, string col)
            {
                if (u1 - u0 <= 0.0005) return;
                var x0 = u0 * (l + sl);
                var x1 = u1 * (l + sl);
                var q = new[] { Pt(x0, 0), Pt(x1, 0), Pt(x1 - sl, m), Pt(x0 - sl, m) };
                using var pt = new SKPaint { IsAntialias = true, Color = Env.SkColor(col) };
                using var path = new SKPath();
                path.MoveTo((float)q[0][0], (float)q[0][1]);
                for (var i = 1; i < q.Length; i++) path.LineTo((float)q[i][0], (float)q[i][1]);
                path.Close();
                ctx.DrawPath(path, pt);
            }
            var second = Best(new[] { sc.Str("ink"), sc.Str("fg"), sc.Str("bg") }, ScK(sc, "accent"));
            if (k < b) Band(a1, Math.Min(1, a1 + 0.06), second);
            else Band(Math.Max(0, a0 - 0.06), a0, second);
            Band(a0, a1, ScK(sc, "accent"));
        }),
    });

    private static void RegisterFxIrisTrans() => RegF("irisTrans", new FxDef
    {
        Name = "アイリス",
        Tags = new[] { "pop", "editorial" },
        W = 0.7,
        Dur = 8,
        Pre = 4,
        Amp = 1,
        Draw = FxWrapped((env, ev, k) =>
        {
            var cw = FxPlug.Cw;
            var ch = FxPlug.Ch;
            var sc = env.Sc!;
            var ctx = env.Ctx;
            var s = unchecked((int)EvS(ev));
            var c = Cover(k, 0.5);
            var cx = cw / 2.0 + J.Rs(s, 1) * cw * 0.08;
            var cy = ch / 2.0 + J.Rs(s, 2) * ch * 0.06;
            var r = Math.Sqrt(Math.Max(cx, cw - cx) * Math.Max(cx, cw - cx) + Math.Max(cy, ch - cy) * Math.Max(cy, ch - cy));
            var rr = r * (1 - c);
            using (var pt = new SKPaint { IsAntialias = true, Color = Env.SkColor(IsDark(ScK(sc, "bg")) ? "#000000" : IsDark(ScK(sc, "ink")) ? ScK(sc, "ink") : "#111111") })
            using (var path = new SKPath { FillType = SKPathFillType.EvenOdd })
            {
                path.AddRect(new SKRect(0, 0, cw, ch));
                if (rr > 0.5) path.AddCircle((float)cx, (float)cy, (float)rr);
                ctx.DrawPath(path, pt);
            }
            if (rr > 1 && c > 0.02)
            {
                using var pt = new SKPaint
                {
                    IsAntialias = true,
                    IsStroke = true,
                    StrokeWidth = (float)Math.Max(2, ch * 0.008),
                    Color = Env.SkColor(ScK(sc, "accent")),
                };
                ctx.DrawCircle((float)cx, (float)cy, (float)rr, pt);
            }
        }),
    });

    private static void RegisterFxDoors() => RegF("doors", new FxDef
    {
        Name = "扉",
        Tags = new[] { "graphic", "pop" },
        W = 0.7,
        Dur = 8,
        Pre = 4,
        Amp = 1,
        Draw = FxWrapped((env, ev, k) =>
        {
            var cw = FxPlug.Cw;
            var ch = FxPlug.Ch;
            var sc = env.Sc!;
            var ctx = env.Ctx;
            var s = unchecked((int)EvS(ev));
            var c = Cover(k, 0.5, J.E.InQuad, J.E.OutCubic);
            var vert = J.R(s, 3) < 0.3;
            if (c <= 0.001) return;
            var l = vert ? ch : cw;
            var half = l / 2.0 * c + 1;
            var lw = Math.Max(2, Math.Min(cw, ch) * 0.008);
            var col = InkOf(sc);
            var m = vert ? cw : ch;

            void R(double a, double b2, double w, double h, string color, double alpha = 1)
            {
                if (vert) FillMode(ctx, b2, a, h, w, color, alpha);
                else FillMode(ctx, a, b2, w, h, color, alpha);
            }
            R(0, 0, half, m, col);
            R(l - half, 0, half, m, col);
            R(half - lw, 0, lw, m, ScK(sc, "accent"));
            R(l - half, 0, lw, m, ScK(sc, "accent"));
            R(half - lw * 4, 0, lw * 0.5, m, col, 0.35);
            R(l - half + lw * 3.5, 0, lw * 0.5, m, col, 0.35);
        }),
    });

    private static void RegisterFxBlindsTrans() => RegF("blindsTrans", new FxDef
    {
        Name = "ブラインド",
        Tags = new[] { "graphic", "editorial" },
        W = 0.7,
        Dur = 6,
        Pre = 3,
        Amp = 1,
        Draw = FxWrapped((env, ev, k) =>
        {
            var cw = FxPlug.Cw;
            var ch = FxPlug.Ch;
            var sc = env.Sc!;
            var ctx = env.Ctx;
            var s = unchecked((int)EvS(ev));
            var c = Cover(k, 0.5);
            var n = 7 + (int)(J.H(s, 4) % 6);
            var vert = J.R(s, 5) < 0.35;
            if (c <= 0.001) return;
            var l = vert ? cw : ch;
            var m = vert ? ch : cw;
            var slat = l / (double)n;
            using var pt = new SKPaint { Color = Env.SkColor(J.R(s, 6) < 0.5 ? ScK(sc, "accent") : InkOf(sc)) };
            using var path = new SKPath();
            for (var i = 0; i < n; i++)
            {
                var ci = J.Clamp(c * 1.35 - i / (double)n * 0.35);
                var hh = slat * ci + (ci >= 1 ? 1 : 0);
                if (hh <= 0.2) continue;
                if (vert) path.AddRect(new SKRect((float)(i * slat), 0, (float)(i * slat + hh), (float)m));
                else path.AddRect(new SKRect(0, (float)(i * slat), (float)m, (float)(i * slat + hh)));
            }
            ctx.DrawPath(path, pt);
        }),
    });

    private static void RegisterFxRgbSplit() => RegF("rgbSplit", new FxDef
    {
        Name = "RGB分離",
        Tags = new[] { "glitch", "emotional" },
        W = 1,
        Dur = 4,
        Amp = 1,
        Glitchy = true,
        Mid = true,
        Draw = FxWrapped((env, ev, k) =>
        {
            var S = FxPlug.S;
            var cw = FxPlug.Cw;
            var ch = FxPlug.Ch;
            var sc = env.Sc!;
            if (S == null || FxPlug.Tmp == null || FxPlug.Tmp2 == null) return;
            var ctx = env.Ctx;
            var a = AmpOf(ev);
            var dk = IsDark(ScK(sc, "bg"));
            var st = unchecked((int)env.Step + (int)EvS(ev));
            var d = Math.Max(2, cw * (0.006 + 0.01 * J.R(st, 5)) * a * (1 - 0.6 * k));
            var dy = J.Rs(st, 6) * ch * 0.004 * a;
            // 墨版：帧和背景做差把文字抠出来，着色副本只添色边，背景不被染色
            var hw = Math.Max(2, (int)Math.Round(cw / 2.0));
            var hh = Math.Max(2, (int)Math.Round(ch / 2.0));

            SKSurface Tint(SKSurface t, string c)
            {
                var x = t.Canvas;
                x.SetMatrix(SKMatrix.Identity);
                x.Clear();
                using (var simg = S.Snapshot())
                using (var p = new SKPaint())
                    x.DrawImage(simg, new SKRect(0, 0, hw, hh), SmpL, p);
                FillMode(x, 0, 0, hw, hh, ScK(sc, "bg"), 1, SKBlendMode.Difference);
                if (dk) FillMode(x, 0, 0, hw, hh, c, 1, SKBlendMode.Multiply);
                else
                {
                    FillMode(x, 0, 0, hw, hh, "#ffffff", 1, SKBlendMode.Difference);
                    FillMode(x, 0, 0, hw, hh, c, 1, SKBlendMode.Screen);
                }
                return t;
            }

            var t1 = Tint(FxPlug.Tmp(hw, hh), "#FF2A2A");
            var t2 = Tint(FxPlug.Tmp2(hw, hh), "#1EE6FF");
            using (var i1 = t1.Snapshot())
            using (var i2 = t2.Snapshot())
            using (var p = ImgPaint(0.9, dk ? SKBlendMode.Screen : SKBlendMode.Multiply))
            {
                ctx.DrawImage(i1, new SKRect(0, 0, hw, hh), new SKRect((float)-d, (float)-dy, (float)(-d + cw), (float)(-dy + ch)), SmpL, p);
                ctx.DrawImage(i2, new SKRect(0, 0, hw, hh), new SKRect((float)d, (float)dy, (float)(d + cw), (float)(dy + ch)), SmpL, p);
            }
        }),
    });

    private static void RegisterFxSmear() => RegF("smear", new FxDef
    {
        Name = "横スミア",
        Tags = new[] { "glitch" },
        W = 0.8,
        Dur = 3,
        Amp = 1,
        Glitchy = true,
        Mid = true,
        Draw = FxWrapped((env, ev, k) =>
        {
            var S = FxPlug.S;
            var cw = FxPlug.Cw;
            var ch = FxPlug.Ch;
            if (S == null) return;
            var ctx = env.Ctx;
            var a = AmpOf(ev);
            var st = unchecked((int)env.Step * 13 + (int)EvS(ev));
            var n = 7 + (int)(J.H(st, 2) % 6);
            var sw = Math.Max(1, (int)Math.Round(cw * 0.003));
            using var simg = S.Snapshot();
            using var p = ImgPaint(0.92);
            for (var i = 0; i < n; i++)
            {
                var hh = Math.Max(2, (int)Math.Round(ch * J.Rr(0.008, 0.06, st, i, 2)));
                var y = (int)Math.Round(J.Clamp(ch * (0.22 + 0.56 * J.R(st, i, 1)), 0, ch - hh));
                var sx = (int)Math.Round(cw * J.Rr(0.25, 0.75, st, i, 3));
                var len = cw * J.Rr(0.12, 0.45, st, i, 4) * a;
                var dir = J.R(st, i, 5) < 0.5 ? 1 : -1;
                ctx.DrawImage(simg, new SKRect(sx, y, sx + sw, y + hh),
                    new SKRect((float)(dir > 0 ? sx : sx - len), y, (float)((dir > 0 ? sx : sx - len) + len), y + hh), SmpL, p);
            }
        }),
    });

    private static void RegisterFxVhsRoll() => RegF("vhsRoll", new FxDef
    {
        Name = "VHSロール",
        Tags = new[] { "glitch", "emotional" },
        W = 0.8,
        Dur = 6,
        Amp = 1,
        Glitchy = true,
        Mid = true,
        Draw = FxWrapped((env, ev, k) =>
        {
            var S = FxPlug.S;
            var cw = FxPlug.Cw;
            var ch = FxPlug.Ch;
            if (S == null) return;
            var ctx = env.Ctx;
            var a = AmpOf(ev);
            var st = unchecked((int)env.Step * 7 + (int)EvS(ev));
            var oy = (int)Math.Round(ch * 0.2 * a * Bell(k));
            var dx = (int)Math.Round(J.Rs(st, 1) * cw * 0.004 * a);
            if (oy < 1) return;
            using var simg = S.Snapshot();
            using (var p = ImgPaint(1))
            {
                ctx.DrawImage(simg, new SKPoint(dx, oy), SmpL, p);
                ctx.DrawImage(simg, new SKPoint(dx, oy - ch), SmpL, p);
            }
            for (var i = 0; i < 5; i++)
            {
                var hh = Math.Max(2, (int)Math.Round(ch * 0.008));
                var yy = oy + i * hh * 1.6;
                var src = yy - oy;
                if (yy + hh > ch || src < 0) break;
                using (var p = ImgPaint(1))
                    ctx.DrawImage(simg, new SKRect(0, (float)src, cw, (float)(src + hh)),
                        new SKRect((float)(J.Rs(st, i, 2) * cw * 0.03 * a), (float)yy, (float)(J.Rs(st, i, 2) * cw * 0.03 * a + cw), (float)(yy + hh)), SmpL, p);
            }
            var bh = Math.Max(3, ch * 0.028);
            FillMode(ctx, 0, oy - bh, cw, bh, "#000000", 0.78);
            FillMode(ctx, 0, oy - bh - Math.Max(1, ch * 0.003), cw, Math.Max(1, ch * 0.003), "#ffffff", 0.75);
        }),
    });

    /// <summary>trackingNoise 用的条纹噪点纹理（JS noiseTex，确定性生成建一次）。</summary>
    private static SKImage? _noiseTex;

    private static SKImage NoiseTex()
    {
        if (_noiseTex != null) return _noiseTex;
        const int w = 256, h = 64;
        var b = new SKBitmap(w, h);
        var px = b.PeekPixels();
        var ptr = px.GetPixels();
        var buf = new byte[px.Info.BytesSize];
        Marshal.Copy(ptr, buf, 0, buf.Length);
        for (var y = 0; y < h; y++)
        {
            var run = 1 + (int)(J.H(y, 3) % 7);
            for (var i = 0; i < w; i++)
            {
                var v = (byte)(Math.Pow(J.R(y, (int)Math.Floor(i / (double)run), 11), 2.2) * 255);
                var p = (y * w + i) * 4;
                buf[p] = v;
                buf[p + 1] = v;
                buf[p + 2] = v;
                buf[p + 3] = 255;
            }
        }
        Marshal.Copy(buf, 0, ptr, buf.Length);
        _noiseTex = SKImage.FromBitmap(b);
        b.Dispose();
        return _noiseTex;
    }

    private static void RegisterFxTrackingNoise() => RegF("trackingNoise", new FxDef
    {
        Name = "トラッキングノイズ",
        Tags = new[] { "glitch", "emotional" },
        W = 0.8,
        Dur = 4,
        Amp = 1,
        Glitchy = true,
        Mid = true,
        Draw = FxWrapped((env, ev, k) =>
        {
            var S = FxPlug.S;
            var cw = FxPlug.Cw;
            var ch = FxPlug.Ch;
            var sc = env.Sc!;
            if (S == null) return;
            var ctx = env.Ctx;
            var a = AmpOf(ev);
            var s = unchecked((int)EvS(ev));
            var st = unchecked((int)env.Step * 5 + s);
            var n = NoiseTex();
            var dk = IsDark(ScK(sc, "bg"));

            void Band(double yc, double bh, int rows)
            {
                var y0 = (int)J.Clamp(Math.Round(yc - bh / 2), 0, ch - 2);
                var hh = (int)Math.Min(ch - y0, Math.Round(bh));
                using var simg = S.Snapshot();
                using (var p = ImgPaint(1))
                    for (var i = 0; i < rows; i++)
                    {
                        var y = y0 + (int)Math.Floor(i * hh / (double)rows);
                        var rowH = Math.Max(1, (int)Math.Floor(hh / (double)rows));
                        ctx.DrawImage(simg, new SKRect(0, y, cw, y + rowH),
                            new SKRect((float)(J.Rs(st, i, 9) * cw * 0.035 * a), y,
                                (float)(J.Rs(st, i, 9) * cw * 0.035 * a + cw), y + rowH), SmpL, p);
                    }
                using (var p = ImgPaint(0.75, dk ? SKBlendMode.Screen : SKBlendMode.Multiply))
                {
                    var nx = J.R(st, 3) * n.Width * 0.5;
                    var ny = (int)(J.H(st, 4) % 32);
                    ctx.DrawImage(n, new SKRect((float)nx, ny, (float)(nx + n.Width * 0.5), ny + 32),
                        new SKRect(0, y0, cw, y0 + hh), SmpN, p);
                }
                using (var p = new SKPaint { Color = Env.SkColor(dk ? "#ffffff" : "#000000", 0.8 * 1) })
                    for (var j = 0; j < 7; j++)
                        ctx.DrawRect(new SKRect((float)(J.R(st, j, 5) * cw), (float)(y0 + J.R(st, j, 6) * hh),
                            (float)(J.R(st, j, 5) * cw + cw * J.Rr(0.05, 0.4, st, j, 7)),
                            (float)(y0 + J.R(st, j, 6) * hh + Math.Max(1, ch * 0.0025))), p);
            }

            Band(ch * (0.25 + 0.5 * J.R(s, 1)) + (k - 0.5) * ch * 0.12, ch * (0.05 + 0.06 * J.R(s, 2)) * a, 6);
            Band(ch * (0.1 + 0.8 * J.R(s, 8)) - k * ch * 0.08, ch * 0.018 * a, 2);
        }),
    });

    /// <summary>JS setTransform(a,b,c,d,e,f) → SKMatrix。</summary>
    private static SKMatrix Mat(double a, double b, double c, double d, double e, double f) => new()
    {
        ScaleX = (float)a,
        SkewY = (float)b,
        SkewX = (float)c,
        ScaleY = (float)d,
        TransX = (float)e,
        TransY = (float)f,
        Persp0 = 1,
        Persp1 = 0,
        Persp2 = 1,
    };

    private static void RegisterFxMirrorFlash() => RegF("mirrorFlash", new FxDef
    {
        Name = "ミラー",
        Tags = new[] { "glitch", "graphic" },
        W = 0.6,
        Dur = 2,
        Amp = 1,
        Glitchy = true,
        Mid = true,
        Draw = FxWrapped((env, ev, k) =>
        {
            var S = FxPlug.S;
            var cw = FxPlug.Cw;
            var ch = FxPlug.Ch;
            if (S == null) return;
            var ctx = env.Ctx;
            var m = (int)(J.H((int)EvS(ev), 9) % 4);
            var hw = (int)Math.Floor(cw / 2.0);
            var hh = (int)Math.Floor(ch / 2.0);
            using var simg = S.Snapshot();
            using var p = ImgPaint(1);
            switch (m)
            {
                case 0:
                    ctx.SetMatrix(Mat(-1, 0, 0, 1, cw, 0));
                    ctx.DrawImage(simg, new SKRect(0, 0, hw, ch), new SKRect(0, 0, hw, ch), SmpL, p);
                    break;
                case 1:
                    ctx.SetMatrix(Mat(-1, 0, 0, 1, cw, 0));
                    ctx.DrawImage(simg, new SKRect(cw - hw, 0, cw, ch), new SKRect(cw - hw, 0, cw, ch), SmpL, p);
                    break;
                case 2:
                    ctx.SetMatrix(Mat(1, 0, 0, -1, 0, ch));
                    ctx.DrawImage(simg, new SKRect(0, 0, cw, hh), new SKRect(0, 0, cw, hh), SmpL, p);
                    break;
                default:
                    ctx.SetMatrix(Mat(-1, 0, 0, 1, cw, 0));
                    ctx.DrawImage(simg, new SKRect(0, 0, hw, hh), new SKRect(0, 0, hw, hh), SmpL, p);
                    ctx.SetMatrix(Mat(1, 0, 0, -1, 0, ch));
                    ctx.DrawImage(simg, new SKRect(0, 0, cw, hh), new SKRect(0, 0, cw, hh), SmpL, p);
                    ctx.SetMatrix(Mat(-1, 0, 0, -1, cw, ch));
                    ctx.DrawImage(simg, new SKRect(0, 0, hw, hh), new SKRect(0, 0, hw, hh), SmpL, p);
                    break;
            }
        }),
    });

    private static void RegisterFxStrobe() => RegF("strobe", new FxDef
    {
        Name = "ストロボ",
        Tags = new[] { "glitch", "pop" },
        W = 0.5,
        Dur = 4,
        Amp = 1,
        Mid = true,
        Draw = FxWrapped((env, ev, k) =>
        {
            if (Math.Floor(k * 4) % 2 == 1) return;
            FillMode(env.Ctx, 0, 0, FxPlug.Cw, FxPlug.Ch, "#ffffff", 1, SKBlendMode.Difference);
        }),
    });

    /// <summary>CSS saturate(s) → 4x4 颜色矩阵（线性部分）。</summary>
    private static float[][] SatMat(double s)
    {
        return new[]
        {
            new[] { (float)(0.213 + 0.787 * s), (float)(0.715 - 0.715 * s), (float)(0.072 - 0.072 * s), 0f },
            new[] { (float)(0.213 - 0.213 * s), (float)(0.715 + 0.285 * s), (float)(0.072 - 0.072 * s), 0f },
            new[] { (float)(0.213 - 0.213 * s), (float)(0.715 - 0.715 * s), (float)(0.072 + 0.928 * s), 0f },
            new float[] { 0, 0, 0, 1 },
        };
    }

    /// <summary>CSS hue-rotate(θ) → 4x4 颜色矩阵（线性部分）。</summary>
    private static float[][] HueMat(double deg)
    {
        var c = Math.Cos(deg * J.DEG);
        var sn = Math.Sin(deg * J.DEG);
        return new[]
        {
            new[] { (float)(0.213 + c * 0.787 - sn * 0.213), (float)(0.715 - c * 0.715 - sn * 0.715), (float)(0.072 - c * 0.072 + sn * 0.928), 0f },
            new[] { (float)(0.213 - c * 0.213 + sn * 0.143), (float)(0.715 + c * 0.285 + sn * 0.140), (float)(0.072 - c * 0.072 - sn * 0.283), 0f },
            new[] { (float)(0.213 - c * 0.213 - sn * 0.787), (float)(0.715 - c * 0.715 + sn * 0.715), (float)(0.072 + c * 0.928 + sn * 0.072), 0f },
            new float[] { 0, 0, 0, 1 },
        };
    }

    /// <summary>4x4 矩阵 + 偏移 → 4x5 颜色矩阵行布局。</summary>
    private static float[] CmLinear(float[][] m, float off = 0)
    {
        var f = new float[20];
        for (var i = 0; i < 4; i++)
        {
            for (var j = 0; j < 4; j++) f[i * 5 + j] = m[i][j];
            f[i * 5 + 4] = i < 3 ? off : 0;
        }
        return f;
    }

    /// <summary>合成两个 4x5 颜色矩阵（a ∘ b：先 b 后 a；alpha 行保持恒等）。</summary>
    private static float[] CmCompose(float[] a, float[] b)
    {
        var o = new float[20];
        for (var i = 0; i < 4; i++)
        {
            for (var j = 0; j < 4; j++)
            {
                float v = 0;
                for (var k = 0; k < 4; k++) v += a[i * 5 + k] * b[k * 5 + j];
                o[i * 5 + j] = v;
            }
            float tv = 0;
            for (var k = 0; k < 4; k++) tv += a[i * 5 + k] * b[k * 5 + 4];
            o[i * 5 + 4] = tv + a[i * 5 + 4];
        }
        return o;
    }

    private static SKColorFilter Cm(float[] m) => SKColorFilter.CreateColorMatrix(m);

    private static void RegisterFxPosterize() => RegF("posterize", new FxDef
    {
        Name = "ポスタリゼ",
        Tags = new[] { "glitch", "pop", "graphic" },
        W = 0.7,
        Dur = 3,
        Amp = 1,
        Mid = true,
        Draw = FxWrapped((env, ev, k) =>
        {
            var S = FxPlug.S;
            if (S == null) return;
            var ctx = env.Ctx;
            var a = AmpOf(ev) * Math.Pow(1 - k, 1.2);
            if (a < 0.02) return;
            using var simg = S.Snapshot();
            if (env.AllowFilter)
            {
                // ctx.filter = contrast(c) saturate(s)：先 saturate 后 contrast
                using var cf = Cm(CmCompose(CmLinear(new float[4][]
                    {
                        new[] { (float)(1 + 1.8 * a), 0, 0, 0 },
                        new[] { 0f, (float)(1 + 1.8 * a), 0, 0 },
                        new[] { 0f, 0f, (float)(1 + 1.8 * a), 0 },
                        new float[] { 0, 0, 0, 1 },
                    }, (float)(0.5 * (1 - (1 + 1.8 * a)))),
                    CmLinear(SatMat(1 + 2.6 * a))));
                using var p = new SKPaint { ColorFilter = cf };
                ctx.DrawImage(simg, 0, 0, p);
            }
            else
            {
                using var p = ImgPaint(Math.Min(1, a), SKBlendMode.Overlay);
                ctx.DrawImage(simg, 0, 0, p);
            }
        }),
    });

    private static void RegisterFxHueShift() => RegF("hueShift", new FxDef
    {
        Name = "色相シフト",
        Tags = new[] { "glitch", "pop", "emotional" },
        W = 0.7,
        Dur = 3,
        Amp = 1,
        Mid = true,
        Draw = FxWrapped((env, ev, k) =>
        {
            var S = FxPlug.S;
            var cw = FxPlug.Cw;
            var ch = FxPlug.Ch;
            var sc = env.Sc!;
            if (S == null) return;
            var ctx = env.Ctx;
            var kk = double.IsFinite(k) ? J.Clamp(k, 0, 1) : 1; // 进度可能越过终点，低帧率下的短事件
            var s = unchecked((int)EvS(ev));
            var a = Math.Pow(1 - kk, 0.8) * Math.Min(1, AmpOf(ev));
            var deg = (int)Math.Round(90 + 180 * J.R(s, 3));
            var dk = IsDark(ScK(sc, "bg"));
            if (!(a > 0.002)) return;
            using var simg = S.Snapshot();
            if (env.AllowFilter)
            {
                // ctx.filter = hue-rotate(deg) saturate(1.6)：先 hue 后 saturate
                using var cf = Cm(CmCompose(CmLinear(SatMat(1.6)), CmLinear(HueMat(deg))));
                using var p = new SKPaint { ColorFilter = cf, Color = SKColors.White.WithAlpha((byte)Math.Round(a * 255)) };
                ctx.DrawImage(simg, 0, 0, p);
            }
            using (var p = new SKPaint
                   {
                       Color = Env.SkColor(J.R(s, 4) < 0.5 ? ScK(sc, "ghostA") : ScK(sc, "ghostB"), 0.5 * a),
                       BlendMode = dk ? SKBlendMode.Multiply : SKBlendMode.Screen,
                   })
                ctx.DrawRect(new SKRect(0, 0, cw, ch), p);
        }),
    });

    private static void RegisterFxTileShift() => RegF("tileShift", new FxDef
    {
        Name = "タイルずらし",
        Tags = new[] { "glitch", "graphic" },
        W = 0.8,
        Dur = 3,
        Amp = 1,
        Glitchy = true,
        Mid = true,
        Draw = FxWrapped((env, ev, k) =>
        {
            var S = FxPlug.S;
            var cw = FxPlug.Cw;
            var ch = FxPlug.Ch;
            var sc = env.Sc!;
            if (S == null) return;
            var ctx = env.Ctx;
            var a = AmpOf(ev);
            var s0 = unchecked((int)env.Step * 11 + (int)EvS(ev));
            var gx = 3 + (int)(J.H(s0, 1) % 4);
            var gy = 2 + (int)(J.H(s0, 2) % 3);
            FillMode(ctx, 0, 0, cw, ch, ScK(sc, "bg"));
            using var simg = S.Snapshot();
            using var p = ImgPaint(1);
            for (var j = 0; j < gy; j++)
            for (var i = 0; i < gx; i++)
            {
                var x0 = (int)Math.Round(i * cw / (double)gx);
                var x1 = (int)Math.Round((i + 1) * cw / (double)gx);
                var y0 = (int)Math.Round(j * ch / (double)gy);
                var y1 = (int)Math.Round((j + 1) * ch / (double)gy);
                var tw = x1 - x0;
                var th = y1 - y0;
                var moved = J.R(s0, i, j, 3) < 0.6;
                var dx = moved ? J.Rs(s0, i, j, 4) * tw * 0.2 * a : 0;
                var dy = moved ? J.Rs(s0, i, j, 5) * th * 0.15 * a : 0;
                var si = i;
                var sj = j;
                if (J.R(s0, i, j, 6) < 0.12)
                {
                    si = (int)(J.H(s0, i, j, 7) % gx);
                    sj = (int)(J.H(s0, i, j, 8) % gy);
                }
                var sx0 = (int)Math.Round(si * cw / (double)gx);
                var sy0 = (int)Math.Round(sj * ch / (double)gy);
                ctx.DrawImage(simg, new SKRect(sx0, sy0, sx0 + Math.Min(tw, cw - sx0), sy0 + Math.Min(th, ch - sy0)),
                    new SKRect((float)(x0 + dx), (float)(y0 + dy), (float)(x0 + dx + tw), (float)(y0 + dy + th)), SmpL, p);
            }
        }),
    });

    private static void RegisterFxFilmBurn() => RegF("filmBurn", new FxDef
    {
        Name = "フィルム焼け",
        Tags = new[] { "emotional", "calm", "editorial" },
        W = 0.7,
        Dur = 7,
        Pre = 2,
        Amp = 1,
        Mid = true,
        Draw = FxWrapped((env, ev, k) =>
        {
            var cw = FxPlug.Cw;
            var ch = FxPlug.Ch;
            var sc = env.Sc!;
            var ctx = env.Ctx;
            var s = unchecked((int)EvS(ev));
            const double b = 2 / 7.0;
            var a = (k < b ? J.E.OutCubic(k / b) : 1 - J.E.InOutCubic((k - b) / (1 - b)))
                    * J.Clamp(AmpOf(ev), 0.5, 1.2) * (0.85 + 0.15 * J.R(unchecked((int)env.Step), 3));
            if (a <= 0.01) return;
            var cn = (int)(J.H(s, 5) % 4);
            var cx = (cn & 1) != 0 ? cw * 1.02 : -cw * 0.02;
            var cy = (cn & 2) != 0 ? ch * (0.7 + 0.3 * J.R(s, 6)) : ch * 0.3 * J.R(s, 6);
            var r = Math.Sqrt(cw * (double)cw + ch * (double)ch) * (0.55 + 0.5 * k);
            if (!IsDark(ScK(sc, "bg")))
            {
                using (var g2 = SKShader.CreateRadialGradient(new SKPoint((float)cx, (float)cy), (float)r,
                           new[]
                           {
                               new SKColor(255, 120, 40, (byte)Math.Round(0.45 * a * 255)),
                               new SKColor(255, 120, 40, 0),
                           }, new float[] { 0, 1 }, SKShaderTileMode.Clamp))
                using (var pt = new SKPaint { Shader = g2, BlendMode = SKBlendMode.Multiply })
                    ctx.DrawRect(new SKRect(0, 0, cw, ch), pt);
            }
            using (var g = SKShader.CreateRadialGradient(new SKPoint((float)cx, (float)cy), (float)r,
                       new[]
                       {
                           new SKColor(255, 244, 214, (byte)Math.Round(0.95 * a * 255)),
                           new SKColor(255, 170, 70, (byte)Math.Round(0.75 * a * 255)),
                           new SKColor(255, 80, 20, (byte)Math.Round(0.32 * a * 255)),
                           new SKColor(255, 60, 0, 0),
                       }, new float[] { 0, 0.3f, 0.62f, 1 }, SKShaderTileMode.Clamp))
            using (var pt = new SKPaint { Shader = g, BlendMode = SKBlendMode.Screen })
                ctx.DrawRect(new SKRect(0, 0, cw, ch), pt);
        }),
    });

    private static void RegisterFxWhipBlur() => RegF("whipBlur", new FxDef
    {
        Name = "ホイップブラー",
        Tags = new[] { "pop", "graphic", "emotional" },
        W = 1,
        Dur = 5,
        Pre = 2,
        Amp = 1,
        Draw = FxWrapped((env, ev, k) =>
        {
            var S = FxPlug.S;
            var cw = FxPlug.Cw;
            var ch = FxPlug.Ch;
            var sc = env.Sc!;
            if (S == null || FxPlug.Tmp2 == null) return;
            var ctx = env.Ctx;
            var amt = Cover(k, 0.4);
            var d = cw * 0.045 * AmpOf(ev) * amt;
            var vert = J.R(unchecked((int)EvS(ev)), 2) < 0.25;
            if (d < 1) return;
            var hw = Math.Max(2, (int)Math.Round(cw / 2.0));
            var hh = Math.Max(2, (int)Math.Round(ch / 2.0));
            var t = FxPlug.Tmp2(hw, hh);
            var x = t.Canvas;
            x.SetMatrix(SKMatrix.Identity);
            x.Clear();
            FillMode(x, 0, 0, hw, hh, ScK(sc, "bg"));
            const int n = 11;
            using (var simg = S.Snapshot())
                for (var i = 0; i < n; i++)
                {
                    var o = (i / (double)(n - 1) - 0.5) * d;
                    using (var p = ImgPaint(1 / (double)(i + 1)))
                        x.DrawImage(simg, new SKRect((float)(vert ? 0 : o), (float)(vert ? o : 0), (float)((vert ? 0 : o) + hw), (float)((vert ? o : 0) + hh)), SmpL, p);
                }
            using (var tim = t.Snapshot())
            using (var p = ImgPaint(1))
                ctx.DrawImage(tim, new SKRect(0, 0, hw, hh), new SKRect(0, 0, cw, ch), SmpL, p);
        }),
    });

    /// <summary>黒コマ/白コマ共用体（JS koma(col)）。</summary>
    private static FxDef Koma(string col) => new()
    {
        Tags = col == "#000000" ? new[] { "editorial", "emotional", "glitch" } : new[] { "emotional", "pop" },
        Dur = 2,
        Pre = 1,
        Amp = 1,
        Mid = false,
        Draw = FxWrapped((env, ev, k) =>
        {
            var one = J.R(unchecked((int)(long)EvS(ev)), 9) < 0.5;
            if (one && k >= 0.5) return;
            FillMode(env.Ctx, 0, 0, FxPlug.Cw, FxPlug.Ch, col);
        }),
    };

    private static void RegisterFxBlackFrame()
    {
        var d = Koma("#000000");
        d.Name = "黒コマ";
        d.W = 0.6;
        RegF("blackFrame", d);
    }

    private static void RegisterFxWhiteFrame()
    {
        var d = Koma("#ffffff");
        d.Name = "白コマ";
        d.W = 0.5;
        RegF("whiteFrame", d);
    }

    private static void RegisterFxWaveWarp() => RegF("waveWarp", new FxDef
    {
        Name = "波ゆがみ",
        Tags = new[] { "emotional", "glitch" },
        W = 0.7,
        Dur = 5,
        Amp = 1,
        Mid = true,
        Draw = FxWrapped((env, ev, k) =>
        {
            var S = FxPlug.S;
            var cw = FxPlug.Cw;
            var ch = FxPlug.Ch;
            if (S == null) return;
            var ctx = env.Ctx;
            var s = unchecked((int)EvS(ev));
            var a = cw * 0.034 * AmpOf(ev) * Bell(k);
            if (a < 0.5) return;
            const int n = 54;
            var h = ch / (double)n;
            var fr = 2 + J.R(s, 1) * 2;
            var ph = k * 8 + J.R(s, 2) * 6;
            using var simg = S.Snapshot();
            using var p = ImgPaint(1);
            for (var i = 0; i < n; i++)
            {
                var y = (int)Math.Floor(i * h);
                var hh = Math.Min(ch - y, (int)Math.Ceiling(h) + 1);
                var dx = Math.Sin(i / (double)n * J.TAU * fr + ph) * a;
                ctx.DrawImage(simg, new SKRect(0, y, cw, y + hh),
                    new SKRect((float)dx, y, (float)(dx + cw), y + hh), SmpL, p);
            }
        }),
    });

    private static void RegisterFxPixelDrift() => RegF("pixelDrift", new FxDef
    {
        Name = "ピクセルずれ",
        Tags = new[] { "glitch" },
        W = 0.8,
        Dur = 3,
        Amp = 1,
        Glitchy = true,
        Mid = true,
        Draw = FxWrapped((env, ev, k) =>
        {
            var S = FxPlug.S;
            var cw = FxPlug.Cw;
            var ch = FxPlug.Ch;
            if (S == null) return;
            var ctx = env.Ctx;
            var a = AmpOf(ev);
            var s0 = unchecked((int)env.Step * 7 + (int)EvS(ev));
            var m = 16 + (int)(J.H(s0, 1) % 14);
            using var simg = S.Snapshot();
            using var p = ImgPaint(1);
            for (var i = 0; i < m; i++)
            {
                var hh = Math.Max(1, (int)Math.Round(ch * J.Rr(0.003, 0.022, s0, i, 2)));
                var y = (int)Math.Round(J.Clamp(ch * (0.2 + 0.6 * J.R(s0, i, 1)), 0, ch - hh));
                var len = (int)Math.Round(cw * J.Rr(0.05, 0.3, s0, i, 4));
                var x0 = (int)Math.Round(J.R(s0, i, 3) * (cw - len));
                var dx = J.Rs(s0, i, 5) * cw * 0.07 * a;
                if (J.R(s0, i, 6) < 0.35)
                    ctx.DrawImage(simg, new SKRect(x0, y, x0 + 1, y + hh),
                        new SKRect((float)(dx > 0 ? x0 : x0 - len), y, (float)((dx > 0 ? x0 : x0 - len) + len), y + hh), SmpL, p);
                else
                    ctx.DrawImage(simg, new SKRect(x0, y, x0 + len, y + hh),
                        new SKRect((float)(x0 + dx), y, (float)(x0 + dx + len), y + hh), SmpL, p);
            }
        }),
    });

    private static void RegisterFxZoomPunch() => RegF("zoomPunch", new FxDef
    {
        Name = "ズームパンチ",
        Tags = new[] { "pop", "graphic", "glitch" },
        W = 1,
        Dur = 4,
        Amp = 1,
        Mid = true,
        Draw = FxWrapped((env, ev, k) =>
        {
            var S = FxPlug.S;
            var cw = FxPlug.Cw;
            var ch = FxPlug.Ch;
            if (S == null) return;
            var ctx = env.Ctx;
            var amt = k < 0.25 ? J.E.OutExpo(k / 0.25) : 1 - J.E.InOutCubic((k - 0.25) / 0.75);
            var z = 1 + (0.06 + 0.04 * J.R(unchecked((int)EvS(ev)), 1)) * J.Clamp(AmpOf(ev), 0.5, 1.3) * amt;
            if (z <= 1.001) return;
            using (var simg = S.Snapshot())
            using (var p = ImgPaint(1))
                ctx.DrawImage(simg, new SKRect((float)(cw / 2.0 - cw * z / 2), (float)(ch / 2.0 - ch * z / 2),
                    (float)(cw / 2.0 + cw * z / 2), (float)(ch / 2.0 + ch * z / 2)), SmpL, p);
        }),
    });

    private static void RegisterFxLightSweep() => RegF("lightSweep", new FxDef
    {
        Name = "光の筋",
        Tags = new[] { "pop", "emotional", "calm" },
        W = 0.9,
        Dur = 7,
        Amp = 1,
        Mid = true,
        Draw = FxWrapped((env, ev, k) =>
        {
            var cw = FxPlug.Cw;
            var ch = FxPlug.Ch;
            var sc = env.Sc!;
            var ctx = env.Ctx;
            var s = unchecked((int)EvS(ev));
            var dk = IsDark(ScK(sc, "bg"));
            var ang = (20 + 20 * J.R(s, 1)) * J.DEG * (J.R(s, 2) < 0.5 ? 1 : -1);
            var pp = J.E.InOutCubic(k);
            var dir = J.R(s, 3) < 0.5 ? 1 : -1;
            var xc = dir > 0 ? J.Lerp(-0.3, 1.3, pp) * cw : J.Lerp(1.3, -0.3, pp) * cw;
            var bw = cw * 0.11;
            var hh = Math.Sqrt(cw * (double)cw + ch * (double)ch);
            var a = Math.Min(1, AmpOf(ev)) * Math.Pow(Bell(k), 0.5);
            var col = dk ? ScK(sc, "fg") : J.Mix(ScK(sc, "accent"), "#ffffff", 0.35);
            ctx.Save();
            ctx.Translate((float)xc, (float)(ch / 2.0));
            ctx.RotateRadians((float)ang);

            void Strip(double x, double w, double al)
            {
                using var g = SKShader.CreateLinearGradient(new SKPoint((float)(x - w / 2), 0), new SKPoint((float)(x + w / 2), 0),
                    new[] { Css(col, 0), Css(col, al), Css(col, 0) }, new float[] { 0, 0.5f, 1 }, SKShaderTileMode.Clamp);
                using var pt = new SKPaint { Shader = g, BlendMode = dk ? SKBlendMode.Screen : SKBlendMode.Multiply };
                ctx.DrawRect(new SKRect((float)(x - w / 2), (float)-hh, (float)(x + w / 2), (float)hh), pt);
            }
            Strip(0, bw, 0.6 * a);
            Strip(-bw * 0.95 * dir, bw * 0.25, 0.45 * a);
            ctx.Restore();
        }),
    });

    private static void RegisterFxCrtOff() => RegF("crtOff", new FxDef
    {
        Name = "ブラウン管オフ",
        Tags = new[] { "glitch", "emotional" },
        W = 0.3,
        Dur = 8,
        Pre = 4,
        Amp = 1,
        Draw = FxWrapped((env, ev, k) =>
        {
            var S = FxPlug.S;
            var cw = FxPlug.Cw;
            var ch = FxPlug.Ch;
            if (S == null) return;
            var ctx = env.Ctx;
            double sy, sx, br;
            if (k < 0.5)
            {
                var q = k / 0.5;
                sy = Math.Max(0.004, 1 - J.E.InCubic(Math.Min(1, q * 1.15)));
                sx = q > 0.8 ? Math.Max(0.01, 1 - J.E.InCubic((q - 0.8) / 0.2) * 0.99) : 1;
                br = J.E.InQuad(q);
            }
            else
            {
                var q = (k - 0.5) / 0.5;
                sy = Math.Max(0.004, J.E.OutExpo(J.Clamp(q * 1.4 - 0.15)));
                sx = q < 0.15 ? Math.Max(0.01, J.E.OutCubic(q / 0.15)) : 1;
                br = 1 - J.E.OutCubic(q);
            }
            var w = cw * sx;
            var h = Math.Max(1, ch * sy);
            var x = cw / 2.0 - w / 2;
            var y = ch / 2.0 - h / 2;
            FillMode(ctx, 0, 0, cw, ch, "#000000");
            using (var simg = S.Snapshot())
            using (var p = ImgPaint(1))
                ctx.DrawImage(simg, new SKRect((float)x, (float)y, (float)(x + w), (float)(y + h)), SmpL, p);
            FillMode(ctx, x, y, w, h, "#ffffff", br * 0.85, SKBlendMode.Screen);
            var gh = ch * 0.05 * br + 1;
            using (var g = SKShader.CreateLinearGradient(new SKPoint(0, (float)(ch / 2.0 - gh)), new SKPoint(0, (float)(ch / 2.0 + gh)),
                       new[]
                       {
                           SKColors.Transparent,
                           SKColors.White.WithAlpha((byte)Math.Round(0.6 * br * 255)),
                           SKColors.Transparent,
                       }, new float[] { 0, 0.5f, 1 }, SKShaderTileMode.Clamp))
            using (var pt = new SKPaint { Shader = g })
                ctx.DrawRect(new SKRect((float)(x - w * 0.05), (float)(ch / 2.0 - gh), (float)(x - w * 0.05 + w * 1.1), (float)(ch / 2.0 + gh)), pt);
        }),
    });

    private static void RegisterFxSplitSlide() => RegF("splitSlide", new FxDef
    {
        Name = "上下スライド",
        Tags = new[] { "graphic", "pop", "glitch" },
        W = 0.9,
        Dur = 6,
        Pre = 2,
        Amp = 1,
        Mid = true,
        Draw = FxWrapped((env, ev, k) =>
        {
            var S = FxPlug.S;
            var cw = FxPlug.Cw;
            var ch = FxPlug.Ch;
            var sc = env.Sc!;
            if (S == null) return;
            var ctx = env.Ctx;
            var s = unchecked((int)EvS(ev));
            var amt = Cover(k, 1 / 3.0, J.E.OutCubic, J.E.InOutCubic);
            var vert = J.R(s, 1) < 0.3;
            var sp = 0.5 + J.Rs(s, 2) * 0.08;
            if (amt <= 0.002) return;
            var lw = Math.Max(2, (int)Math.Round(Math.Min(cw, ch) * 0.004));
            FillMode(ctx, 0, 0, cw, ch, ScK(sc, "bg"));
            using var simg = S.Snapshot();
            if (!vert)
            {
                var d = cw * 0.09 * AmpOf(ev) * amt;
                var ys = (int)Math.Round(ch * sp);
                using (var p = ImgPaint(1))
                {
                    ctx.DrawImage(simg, new SKRect(0, 0, cw, ys), new SKRect((float)-d, 0, (float)(-d + cw), ys), SmpL, p);
                    ctx.DrawImage(simg, new SKRect(0, ys, cw, ch), new SKRect((float)d, ys, (float)(d + cw), ch), SmpL, p);
                }
                FillMode(ctx, 0, ys - lw / 2.0, cw, lw, ScK(sc, "accent"), amt);
            }
            else
            {
                var d = ch * 0.1 * AmpOf(ev) * amt;
                var xs = (int)Math.Round(cw * sp);
                using (var p = ImgPaint(1))
                {
                    ctx.DrawImage(simg, new SKRect(0, 0, xs, ch), new SKRect(0, (float)-d, xs, (float)(-d + ch)), SmpL, p);
                    ctx.DrawImage(simg, new SKRect(xs, 0, cw, ch), new SKRect(xs, (float)d, cw, (float)(d + ch)), SmpL, p);
                }
                FillMode(ctx, xs - lw / 2.0, 0, lw, ch, ScK(sc, "accent"), amt);
            }
        }),
    });
}
