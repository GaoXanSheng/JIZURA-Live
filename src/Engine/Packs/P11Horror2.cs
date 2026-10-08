// 移植自 frontend/src/engine/11p_horror2.js（horror 包 2/3：入场/退场/保持动效与文字加工）。
//
// MoveDef 条目（enter/hold/exit）的 apply(env, it, p) → Draw(env)：it→env.It、p→env.Ap
// （enter→pIn / exit→pOut / hold→amt，JLayouts.MainDraw 调用前写入）、cut→env.Cut。
// it.charFns.push(fn) → env.CharFns.Add(fn)； TreatDef 条目 apply(env, it, P) → Draw(env)，
// P 从 env.Cut.TreatP 取（与 P11TreatTrans 同约定）。
//
// RNG：Draw 侧全走 J.R/J.Rs/J.Rr/J.H 确定性哈希；plan 侧 Rng 顺序流。注册序 = JS 源出现序。
//
// extra/set 标记：JS 的 `set: 'horror'` 已落 IRegEntry.Set 真字段（Reg0/RegT 统一赋值）；`ae: '…'` 行内注释。
//
// 已知偏差（与 P11TreatTrans 同约定）：
// 1. JS 的 addPre/addPost：管线钩子已接（Env.PreFn/PostFn → JLayouts.DrawFx，pre 画在文字前、
//    post 画在文字上）。hrVhold 同步条/hrSwallow 黑渍/hrDrain 墨滴/hrInkBleed 淌墨/hrEroded 麻点/
//    hrRedact 黑条 → env.PostFn；hrDoubleExp 的重影副本 → env.PreFn。
// 2. it.clipFn：管线钩子已接（Env.ClipFn → DrawFx ClipPath）。hrClawReveal 的斜向爪痕、
//    hrSwallow 的整屏 rect + 反序 blob（nonzero 挖洞）按 JS 逐行回迁。
// 3. glyphList 的逐字位移按 treat apply 时刻的 env.CharFn（此时 enter/exit 尚未 apply）近似。

using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace JizuraLive.Engine;

public static class P11Horror2
{
    private const string P = "horror2";
    private static int _installed;

    private static void Reg0(string g, string key, MoveDef def)
    {
        def.Set = "horror"; // JS reg 助手：Object.assign({set: 'horror'}, def)
        Reg.Register(g, key, def, P);
    }
    private static void RegT(string key, TreatDef def)
    {
        def.Set = "horror";
        Reg.Register("treat", key, def, P);
    }

    // ================= 本包 helper（JS L10-129 逐条对位） =================

    /// <summary>HIDE（JS Object.freeze({hide:true})；drawItem 只读其字段，共享安全）。</summary>
    private static readonly CharXf HideXf = new() { Hide = true };

    private static bool IsHex(string? c) => c != null && Regex.IsMatch(c, @"^#([0-9a-f]{3}|[0-9a-f]{6})$", RegexOptions.IgnoreCase);

    /// <summary>colOf(it)：it.color 是 hex 用之，否则 '#ffffff'。</summary>
    private static string ColOf(JObject it) => IsHex(JLayouts.S(it, "color")) ? JLayouts.S(it, "color")! : "#ffffff";

    private static string? MixC(string? a, string? b, double t)
    {
        if (IsHex(a) && IsHex(b)) return J.Mix(a!, b!, J.Clamp(t));
        return t < 0.5 ? a : b;
    }

    private static double MotionK(Env env) => J.Clamp(env.Fx.Motion / 0.7, 0, 1.6);

    /// <summary>layOf(it).N：measure 缓存的排版字数（TextMetrics.Lay 是 JSON 同构 JObject）。</summary>
    private static int LayN(JObject it) => JLayouts.Measure(it).Lay?["N"]?.Value<int>() ?? 0;

    private static int CutN(Env env) => Math.Max(1, JLayouts.GlyphCount(env.Cut?.Text ?? ""));

    private static int Cseed(Env env) => unchecked((int)(env.Cut?.Seed ?? 0));

    /// <summary>Math.hypot 对位（.NET BCL 无 Math.Hypot）。</summary>
    private static double Hypot(double a, double b) => Math.Sqrt(a * a + b * b);

    /// <summary>orderOf(env, it)：字 i 在整句歌词中的位置 0..1（多字项按 i/(n-1)，单字项按 motion index）。</summary>
    private static Func<int, int, double> OrderOf(Env env, JObject it)
    {
        var N = CutN(env);
        var mi = JAnim.Num(it, "mi");
        return (i, n) => n > 1 ? i / (double)(n - 1) : N > 1 ? J.Clamp(mi / (N - 1)) : 0;
    }

    /// <summary>indexOf(env, it)：字 i 在整句歌词中的整数下标。</summary>
    private static Func<int, int, int> IndexOf(Env env, JObject it)
    {
        var N = CutN(env);
        var mi = (int)Math.Round(JAnim.Num(it, "mi"), MidpointRounding.AwayFromZero); // JS Math.round 半位远离零
        return (i, n) => n > 1 ? i : N > 1 ? Math.Min(N - 1, mi) : 0;
    }

    private static bool IsDarkBg(Env env) => J.Lum(ScK(env.Sc!, "bg")) < 0.45;

    private static string NightC(Scheme sc) => J.Mix(ScK(sc, "bg"), "#000000", J.Lum(ScK(sc, "bg")) < 0.45 ? 0.72 : 0.9);

    /// <summary>配色读取兜底（JS 直接读 sc.xxx）。</summary>
    private static string ScK(Scheme sc, string k) => sc.Str(k) ?? k switch
    {
        "bg" => "#000000",
        "fg" => "#FFFFFF",
        "sub" => "#9A9A9A",
        "ink" => "#141414",
        "dim" => "#3A3A3A",
        "accent" => "#FF3B30",
        "accent2" => "#16F4D4",
        _ => "#FFFFFF",
    };

    /// <summary>layCenter(it)：排版结果在项内空间的中心 [cx, cy, w, h]。JS L30-40。</summary>
    private static (double Cx, double Cy, double W, double H) LayCenter(JObject it)
    {
        var lay = JText.LayoutText(it);
        double x0 = 1e9, x1 = -1e9, y0 = 1e9, y1 = -1e9;
        foreach (var g in lay.Cells)
        {
            x0 = Math.Min(x0, g.X - g.W / 2);
            x1 = Math.Max(x1, g.X + g.W / 2);
            y0 = Math.Min(y0, g.Y - g.H / 2);
            y1 = Math.Max(y1, g.Y + g.H / 2);
        }

        return lay.Cells.Count > 0 ? ((x0 + x1) / 2, (y0 + y1) / 2, x1 - x0, y1 - y0) : (0, 0, 0, 0);
    }

    /// <summary>uneasyTimes(seed, N, span)：别扭的时间轴——随机累加间隔（有些特别长），归一化 0..span。</summary>
    private static double[] UneasyTimes(int seed, int N, double span)
    {
        var gaps = new double[N];
        var tot = 0.0;
        for (var k = 0; k < N; k++)
        {
            var r = J.R(seed, k, 901);
            var g = 0.25 + (r < 0.25 ? 2.2 : r * 1.1);
            gaps[k] = g;
            tot += g;
        }

        var @out = new double[N];
        var acc = 0.0;
        for (var k = 0; k < N; k++)
        {
            @out[k] = acc / tot * span;
            acc += gaps[k];
        }

        return @out;
    }

    /// <summary>alive(it, amin)：JS L129。</summary>
    private static bool Alive(JObject it, double amin = 0.9) =>
        !(it["fill"] is JValue { Type: JTokenType.Boolean } fb && !(bool)fb.Value!) &&
        NumNull(it, "alpha", 1) >= amin && !string.IsNullOrEmpty(JLayouts.S(it, "text")) && JAnim.Num(it, "size") > 1;

    private static double NumNull(JObject o, string k, double d) =>
        o[k] is JValue v && (v.Type == JTokenType.Float || v.Type == JTokenType.Integer) ? (double)v : d;

    /// <summary>inItem(env, it, fn)：在项内空间里执行 fn（应用位移/旋转/斜切）。JS L117-128。</summary>
    private static void InItem(Env env, JObject it, Action fn)
    {
        var ctx = env.Ctx;
        ctx.Save();
        try
        {
            ctx.Translate((float)JAnim.Num(it, "x"), (float)JAnim.Num(it, "y"));
            var rot = JAnim.Num(it, "rot");
            if (rot != 0) ctx.RotateDegrees((float)rot);
            var skew = JAnim.Num(it, "skew");
            if (skew != 0) ctx.Skew((float)Math.Tan(skew * J.DEG), 0);
            fn();
        }
        finally
        {
            ctx.Restore();
        }
    }

    private sealed class Gly
    {
        public JText.GlyphCell G = null!;
        public double X, Y, W, H, A, Rot;
    }

    /// <summary>glyphList(it)：项内空间里应用逐字动效后的可见字形（charFn 走 env.CharFn 桥）。JS L97-115。</summary>
    private static List<Gly> GlyphList(Env env, JObject it)
    {
        var lay = JText.LayoutText(it);
        var sx = JAnim.NumOr1(it, "sx");
        var sy = JAnim.NumOr1(it, "sy");
        var @out = new List<Gly>();
        foreach (var g in lay.Cells)
        {
            if (g.Ch is " " or "　") continue;
            var c = env.CharFn?.Invoke(g.I, JText.CellJson(g), lay.N);
            if (c != null && c.Hide) continue;
            var s = c?.S ?? 1;
            @out.Add(new Gly
            {
                G = g,
                X = g.X * sx + g.Vx * sx + (c?.Dx ?? 0),
                Y = g.Y * sy + g.Vy * sy + (c?.Dy ?? 0),
                W = g.W * sx * s,
                H = g.H * sy * s,
                A = c?.A ?? 1,
                Rot = c?.Rot ?? 0,
            });
        }

        return @out;
    }

    /// <summary>需裁掉 g 个字形时的 clipY 对位（hrDrain）。</summary>

    /// <summary>复制一份干净 item 给叠加通道用，钩子与特效字段全清空（JS bare）。JS L73-95。</summary>
    private static JObject Bare(JObject i, JObject? extra = null)
    {
        var c = (JObject)i.DeepClone();
        foreach (var k in new[]
                 {
                     "pre", "post", "echo", "streak", "shadow", "extrude", "pattern", "patternBg", "gradient",
                     "pieceFn", "strokeDash", "wipeBar", "cursorAt", "bands", "vbands", "clip", "clipY", "clipFn",
                     "blend", "_lay",
                 })
            c[k] = null;
        c["strokeUnder"] = false;
        if (extra != null)
            foreach (var kv in extra)
                c[kv.Key] = kv.Value;
        return c;
    }

    /// <summary>副本绘制：临时清空 env.CharFn（函数字段不进 JObject；JS bare 已置 charFn 为空）。</summary>
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

    /// <summary>J.rgba(h, a)。</summary>
    private static string Rgba(string hex, double a)
    {
        var (r, g, b) = J.Hex(hex);
        return "rgba(" + r.ToString(CultureInfo.InvariantCulture) + "," + g.ToString(CultureInfo.InvariantCulture) + ","
               + b.ToString(CultureInfo.InvariantCulture) + "," + a.ToString("0.######", CultureInfo.InvariantCulture) + ")";
    }

    // ================= 注册（JS 源 reg(...) 出现序） =================

    /// <summary>幂等注册本文件全部 enter/exit/hold/treat（顺序 = JS 源 J.register 调用序）。</summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;
        RegisterHrBlinkCreep();  // enter, JS L132
        RegisterHrJumpScare();   // enter, JS L161
        RegisterHrUneasy();      // enter, JS L183
        RegisterHrVhold();       // enter, JS L205
        RegisterHrMirrorSnap();  // enter, JS L228
        RegisterHrManifest();    // enter, JS L244
        RegisterHrClawReveal();  // enter, JS L265
        RegisterHrPulledDown();  // exit,  JS L296
        RegisterHrLookBack();    // exit,  JS L328
        RegisterHrTurnAway();    // exit,  JS L359
        RegisterHrShiver();      // exit,  JS L380
        RegisterHrSwallow();     // exit,  JS L395
        RegisterHrFlickerDie();  // exit,  JS L426
        RegisterHrDrain();       // exit,  JS L444
        RegisterHrTwitch();      // hold,  JS L480
        RegisterHrStare();       // hold,  JS L501
        RegisterHrLagOne();      // hold,  JS L520
        RegisterHrFlickerLight();// hold,  JS L537
        RegisterHrInkBleed();    // treat, JS L553
        RegisterHrEroded();      // treat, JS L592
        RegisterHrRedact();      // treat, JS L620
        RegisterHrDoubleExp();   // treat, JS L663
    }

    // ---------------- enter: hrBlinkCreep（JS L132-158） ----------------

    private static void RegisterHrBlinkCreep() => Reg0("enter", "hrBlinkCreep", new MoveDef
    {
        Name = "瞬きの間に",
        Tags = new[] { "horror", "emotional" },
        W = 0.9,
        MinDur = 1,
        InDur = (dur, _) => J.Clamp(dur * 0.5, 0.6, 1.3),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var sz = JAnim.Num(it, "size");
            var cs = Cseed(env);
            var ord = OrderOf(env, it);
            double[] B = { 0.26, 0.52, 0.78 };
            const double bw = 0.05;
            foreach (var b in B)
                if (p >= b && p < b + bw)
                {
                    env.CharFns.Add((_, _, _) => HideXf);
                    return;
                }

            var stage = B.Count(b => p >= b + bw);
            var f = stage / B.Length;
            env.CharFns.Add((i, _, n) =>
            {
                var k = unchecked((int)J.H(cs, (int)Math.Round(ord(i, n) * 97, MidpointRounding.AwayFromZero), 11));
                var ang = J.R(k, 1) * J.TAU;
                var dist = sz * (0.9 + J.R(k, 2) * 1.1);
                var r = 1 - f;
                if (r <= 0) return null;
                return new CharXf
                {
                    Dx = Math.Cos(ang) * dist * r,
                    Dy = Math.Sin(ang) * dist * r * 0.6,
                    S = 1 - 0.45 * r,
                    Rot = J.Rs(k, 3) * 25 * r,
                    A = 0.3 + 0.7 * (1 - r),
                };
            });
        },
    });

    // ---------------- enter: hrJumpScare（JS L161-180） ----------------

    private static void RegisterHrJumpScare() => Reg0("enter", "hrJumpScare", new MoveDef
    {
        Name = "飛び出し",
        Tags = new[] { "horror", "glitch", "pop" },
        W = 0.7,
        MinDur = 0.9,
        InDur = (dur, _) => J.Clamp(dur * 0.42, 0.45, 1),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            const double at = 0.62;
            var seed = JAnim.SeedOf(it);
            var st = (int)env.Step;
            if (p < at)
            {
                var q = p / at;
                it["size"] = JAnim.Num(it, "size") * (0.42 + 0.08 * q);
                it["alpha"] = NumNull(it, "alpha", 1) * (0.1 + 0.12 * q) * (J.R(seed, st, 3) < 0.15 ? 0.3 : 1);
                it["x"] = JAnim.Num(it, "x") + J.Rs(seed, st, 1) * JAnim.Num(it, "size") * 0.02;
                it["y"] = JAnim.Num(it, "y") + J.Rs(seed, st, 2) * JAnim.Num(it, "size") * 0.02;
                return;
            }

            var q2 = (p - at) / (1 - at);
            var e = J.E.OutExpo(q2);
            var k = 1 - e;
            it["size"] = JAnim.Num(it, "size") * (1 + 0.95 * k);
            it["x"] = JAnim.Num(it, "x") + J.Rs(seed, st, 4) * JAnim.Num(it, "size") * 0.12 * k;
            it["y"] = JAnim.Num(it, "y") + J.Rs(seed, st, 5) * JAnim.Num(it, "size") * 0.12 * k;
            it["rot"] = NumNull(it, "rot", 0) + J.Rs(seed, st, 6) * 7 * k;
        },
    });

    // ---------------- enter: hrUneasy（JS L183-202） ----------------

    private static void RegisterHrUneasy() => Reg0("enter", "hrUneasy", new MoveDef
    {
        Name = "間の悪い出現",
        Tags = new[] { "horror", "editorial" },
        W = 1,
        MinDur = 0.9,
        InDur = (dur, _) => J.Clamp(dur * 0.55, 0.5, 1.6),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var cs = Cseed(env);
            var N = CutN(env);
            var idx = IndexOf(env, it);
            var times = UneasyTimes(cs, N, 0.86);
            var sz = JAnim.Num(it, "size");
            var acc = ScK(env.Sc!, "accent");
            env.CharFns.Add((i, _, n) =>
            {
                var ix = idx(i, n);
                var t0 = times[Math.Min(N - 1, ix)];
                var q = (p - t0) / 0.1;
                if (q < 0) return HideXf;
                if (q >= 1) return null;
                var k = unchecked((int)J.H(cs, ix, 21));
                return new CharXf
                {
                    Dx = J.Rs(k, 1) * sz * 0.18 * (1 - q),
                    Dy = J.Rs(k, 2) * sz * 0.1 * (1 - q),
                    Rot = J.Rs(k, 3) * 18 * (1 - q),
                    Color = q < 0.5 ? acc : null,
                };
            });
        },
    });

    // ---------------- enter: hrVhold（JS L205-225） ----------------

    private static void RegisterHrVhold() => Reg0("enter", "hrVhold", new MoveDef
    {
        Name = "垂直同期",
        Tags = new[] { "horror", "glitch" },
        W = 0.8,
        InDur = (dur, _) => J.Clamp(dur * 0.4, 0.35, 0.8),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var bx = JAnim.ItemBox(it);
            var pad = JAnim.Num(it, "size") * 0.25;
            var y0 = bx.Y0 - pad;
            var y1 = bx.Y1 + pad;
            var Hh = y1 - y0;
            if (Hh <= 1) return;
            var e = J.E.OutCubic(p);
            const double rolls = 2.6;
            var m = (((1 - e) * rolls * Hh) % Hh + Hh) % Hh;
            it["clipY"] = new JArray(y0, y1);
            it["y"] = JAnim.Num(it, "y") + m;
            it["echo"] = new JObject { ["n"] = 1, ["dx"] = 0.0, ["dy"] = -Hh, ["a"] = 1.0, ["decay"] = 1.0 };
            it["x"] = JAnim.Num(it, "x") + J.Rs(JAnim.SeedOf(it), (int)env.Step, 1) * JAnim.Num(it, "size") * 0.05 * (1 - e);
            it["alpha"] = NumNull(it, "alpha", 1) * Math.Min(1, 0.4 + p * 2);
            var bar = J.Mix(ScK(env.Sc!, "bg"), "#000000", 0.6);
            var single = LayN(it) == 1 && CutN(env) > 1;
            // JS addPost → env.PostFn：同步条画在文字上方（层级与 JS 一致）
            env.PostFn = (e2, _, _) =>
            {
                if (e2.Pass != "main" || m < 1 || single) return;
                var yb = y0 + m;
                var fa = 1 - J.Smooth(0.75, 1, p);
                e2.Rect(bx.X0 - pad * 2, yb - Hh * 0.05, bx.X1 - bx.X0 + pad * 4, Hh * 0.07, bar, 0.85 * fa, false);
                e2.Rect(bx.X0 - pad * 2, yb + Hh * 0.03, bx.X1 - bx.X0 + pad * 4, Math.Max(1, Hh * 0.008),
                    ScK(e2.Sc!, "fg"), 0.35 * fa, false);
            };
        },
    });

    // ---------------- enter: hrMirrorSnap（JS L228-241） ----------------

    private static void RegisterHrMirrorSnap() => Reg0("enter", "hrMirrorSnap", new MoveDef
    {
        Name = "鏡文字",
        Tags = new[] { "horror", "glitch" },
        W = 0.8,
        MinDur = 0.8,
        InDur = (dur, _) => J.Clamp(dur * 0.45, 0.5, 1.1),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var seed = JAnim.SeedOf(it);
            var cx = LayCenter(it).Cx;
            var sx = JAnim.NumOr1(it, "sx");
            var st = (int)env.Step;
            var s = -1.0;
            if (p >= 0.55 && p < 0.72) s = J.R(seed, st, 7) < 0.5 ? -1 : 0.25;
            else if (p >= 0.72) s = J.Lerp(-0.2, 1, J.E.OutBack((p - 0.72) / 0.28, 2.2));
            it["alpha"] = NumNull(it, "alpha", 1) * Math.Min(1, p / 0.18);
            if (p < 0.55) it["rot"] = NumNull(it, "rot", 0) + Math.Sin(p * 30) * 1.5;
            if (s == 1) return;
            env.CharFns.Add((_, g, _) => new CharXf
            {
                Sx = Math.Abs(s) < 0.04 ? 0.04 : s,
                Dx = (cx - (g != null ? JAnim.Num(g, "x") : 0)) * sx * (1 - s),
            });
        },
    });

    // ---------------- enter: hrManifest（JS L244-262） ----------------

    private static void RegisterHrManifest() => Reg0("enter", "hrManifest", new MoveDef
    {
        Name = "浮かび上がる",
        Tags = new[] { "horror", "emotional", "calm" },
        W = 1,
        MinDur = 0.8,
        InDur = (dur, _) => J.Clamp(dur * 0.5, 0.5, 1.4),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var cs = Cseed(env);
            var idx = IndexOf(env, it);
            var sz = JAnim.Num(it, "size");
            var fin = J.Smooth(0.72, 1, p);
            env.CharFns.Add((i, _, n) =>
            {
                var k = unchecked((int)J.H(cs, idx(i, n), 31));
                var ph = J.R(k, 1) * J.TAU;
                var f = 7 + J.R(k, 2) * 6;
                var wav = J.Clamp(p * 1.3) * (0.45 + 0.55 * Math.Max(0, Math.Sin(p * f + ph)));
                var a = J.Lerp(wav, 1, fin);
                if (a >= 0.999) return null;
                return new CharXf
                {
                    A = a,
                    Dx = J.Noise1(p * 4 + ph, k) * sz * 0.12 * (1 - fin),
                    Dy = J.Noise1(p * 3 + ph, k + 1) * sz * 0.08 * (1 - fin),
                };
            });
        },
    });

    // ---------------- enter: hrClawReveal（JS L265-293） ----------------

    private static void RegisterHrClawReveal() => Reg0("enter", "hrClawReveal", new MoveDef
    {
        Name = "爪痕から",
        Tags = new[] { "horror", "graphic" },
        W = 0.8,
        InDur = (dur, _) => J.Clamp(dur * 0.36, 0.3, 0.7),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var seed = JAnim.SeedOf(it);
            var bx = JAnim.ItemBox(it);
            var pad = JAnim.Num(it, "size") * 0.3;
            var x0 = bx.X0 - pad;
            var x1 = bx.X1 + pad;
            var y0 = bx.Y0 - pad;
            var y1 = bx.Y1 + pad;
            var W = x1 - x0;
            var Hh = y1 - y0;
            const int n = 4;
            var sp = W / n;
            var slope = Math.Min(0.55 * Hh, sp * 1.4);
            var dir = (J.H(seed, 3) & 1) != 0 ? 1 : -1;
            if (p > 0.97) return;
            // JS clipFn：4 道斜向爪痕条带并集（逐条撑开，seed/k/slope 上的 J.rs 噪声）→ Env.ClipFn 桥
            env.ClipFn = (_, _) =>
            {
                var path = new SKPath();
                for (var k = -2; k < n + 2; k++)
                {
                    var q = J.Clamp((p - J.Clamp(k, 0, n - 1) * 0.08) / 0.7)
                            * (k < 0 || k >= n ? J.Smooth(0.5, 0.9, p) : 1);
                    if (q <= 0) continue;
                    var e = J.E.InOutCubic(q);
                    var cx = x0 + (k + 0.5) * sp;
                    var w = sp * 0.08 + sp * 1.1 * e * e;
                    // 斜向条带，从一端扫到另一端
                    var reach = J.E.OutCubic(J.Clamp(q * 3));
                    var ya = dir > 0 ? y0 : y1;
                    var yb = J.Lerp(ya, dir > 0 ? y1 : y0, reach);
                    double Off(double y) => (y - y0) / Hh * slope - slope / 2;
                    double J1(int a, int b) => J.Rs(seed, k, a, b) * sp * 0.06;
                    double[][] Pts =
                    {
                        new[] { cx - w / 2 + Off(ya) + J1(1, 1), ya },
                        new[] { cx + w / 2 + Off(ya) + J1(1, 2), ya },
                        new[] { cx + w / 2 + Off((ya + yb) / 2) + J1(2, 1), (ya + yb) / 2 },
                        new[] { cx + w / 2 * 0.3 + Off(yb), yb },
                        new[] { cx - w / 2 * 0.3 + Off(yb), yb },
                        new[] { cx - w / 2 + Off((ya + yb) / 2) + J1(2, 2), (ya + yb) / 2 },
                    };
                    path.MoveTo((float)Pts[0][0], (float)Pts[0][1]);
                    for (var i = 1; i < Pts.Length; i++) path.LineTo((float)Pts[i][0], (float)Pts[i][1]);
                    path.Close();
                }
                return path;
            };
        },
    });

    // ---------------- exit: hrPulledDown（JS L296-325） ----------------

    private static void RegisterHrPulledDown() => Reg0("exit", "hrPulledDown", new MoveDef
    {
        Name = "引きずり込み",
        Tags = new[] { "horror", "glitch", "emotional" },
        W = 1,
        OutDur = (dur, _) => J.Clamp(dur * 0.4, 0.4, 0.9),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var cs = Cseed(env);
            var N = CutN(env);
            var idx = IndexOf(env, it);
            var H = (double)env.H;
            var sz = JAnim.Num(it, "size");
            var st = (int)env.Step;
            var last = (int)(J.H(cs, 41) % (uint)N);
            env.CharFns.Add((i, _, n) =>
            {
                var ix = idx(i, n);
                var t0 = ix == last ? 0.7 : J.R(cs, ix, 42) * 0.5;
                var q = (p - t0) / 0.28;
                if (q < 0)
                {
                    var tr = J.Clamp(1 + q * 1.5) * (ix == last ? 1.6 : 0.6);
                    return tr > 0
                        ? new CharXf
                        {
                            Dx = J.Rs(cs, ix, st, 43) * sz * 0.03 * tr,
                            Dy = J.Rs(cs, ix, st, 44) * sz * 0.02 * tr,
                            Rot = J.Rs(cs, ix, st, 45) * 6 * tr,
                        }
                        : null;
                }

                if (q >= 1) return HideXf;
                var e = q * q;
                return new CharXf
                {
                    Dy = e * H * 1.1,
                    Sy = 1 + q * 1.8,
                    Sx = 1 - q * 0.3,
                    Rot = J.Rs(cs, ix, 46) * 12 * q,
                    A = 1 - J.E.InCubic(q),
                };
            });
        },
    });

    // ---------------- exit: hrLookBack（JS L328-356） ----------------

    private static void RegisterHrLookBack() => Reg0("exit", "hrLookBack", new MoveDef
    {
        Name = "一字残る",
        Tags = new[] { "horror", "emotional" },
        W = 0.9,
        MinDur = 1,
        OutDur = (dur, _) => J.Clamp(dur * 0.45, 0.55, 1.2),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var cs = Cseed(env);
            var N = CutN(env);
            var idx = IndexOf(env, it);
            var sz = JAnim.Num(it, "size");
            var st = (int)env.Step;
            var txt = JLayouts.CodePoints(Regex.Replace(env.Cut?.Text ?? "", @"\s+", ""));
            var pick = (int)(J.H(cs, 51) % (uint)N);
            for (var k = 0; k < N; k++)
            {
                var j = (pick + k) % N;
                var ch = j < txt.Count ? txt[j] : "";
                if ((ch.Length == 1 && J.IsKanji(ch[0])) || Regex.IsMatch(ch, "[A-Za-z]"))
                {
                    pick = j;
                    break;
                }
            }

            var dir = (J.H(cs, 52) & 1) != 0 ? 1 : -1;
            var acc = ScK(env.Sc!, "accent");
            env.CharFns.Add((i, _, n) =>
            {
                if (idx(i, n) != pick) return p < 0.06 ? new CharXf { A = 0.4 } : HideXf;
                if (p >= 0.86) return HideXf;
                var q = J.E.InOutSine(J.Clamp((p - 0.12) / 0.55));
                var tw = J.R(cs, st, 53) < 0.12 ? J.Rs(cs, st, 54) * 6 : 0;
                return new CharXf
                {
                    Rot = dir * 24 * q + tw,
                    S = 1 + 0.16 * q,
                    Dx = J.Rs(cs, st, 55) * sz * 0.01 * q,
                    Color = q > 0.3 ? acc : null,
                };
            });
        },
    });

    // ---------------- exit: hrTurnAway（JS L359-377） ----------------

    private static void RegisterHrTurnAway() => Reg0("exit", "hrTurnAway", new MoveDef
    {
        Name = "背を向ける",
        Tags = new[] { "horror", "calm", "emotional" },
        W = 0.9,
        OutDur = (dur, _) => J.Clamp(dur * 0.4, 0.4, 0.9),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var ord = OrderOf(env, it);
            var c0 = ColOf(it);
            var dk = NightC(env.Sc!);
            var sz = JAnim.Num(it, "size");
            env.CharFns.Add((i, _, n) =>
            {
                var q = J.Clamp((p - ord(i, n) * 0.4) / 0.6);
                if (q <= 0) return null;
                if (q >= 1) return HideXf;
                var sx = Math.Cos(q * Math.PI * 0.95);
                return new CharXf
                {
                    Sx = Math.Abs(sx) < 0.04 ? 0.04 : sx,
                    Color = q > 0.5 ? MixC(c0, dk, 0.35 + q * 0.5) : null,
                    A = 1 - J.Smooth(0.6, 1, q),
                    Dy = q * sz * 0.08,
                };
            });
        },
    });

    // ---------------- exit: hrShiver（JS L380-392） ----------------

    private static void RegisterHrShiver() => Reg0("exit", "hrShiver", new MoveDef
    {
        Name = "震えて消える",
        Tags = new[] { "horror", "glitch" },
        W = 0.9,
        OutDur = (dur, _) => J.Clamp(dur * 0.35, 0.35, 0.8),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var cs = Cseed(env);
            var idx = IndexOf(env, it);
            var sz = JAnim.Num(it, "size");
            var st = (int)env.Step;
            env.CharFns.Add((i, _, n) =>
            {
                var ix = idx(i, n);
                var t0 = 0.3 + J.R(cs, ix, 61) * 0.62;
                if (p >= t0) return HideXf;
                var a = sz * (0.025 + 0.13 * p);
                return new CharXf
                {
                    Dx = J.Rs(cs, ix, st, 62) * a,
                    Dy = J.Rs(cs, ix, st, 63) * a,
                    Rot = J.Rs(cs, ix, st, 64) * 14 * p,
                };
            });
        },
    });

    // ---------------- exit: hrSwallow（JS L395-423） ----------------

    private static void RegisterHrSwallow() => Reg0("exit", "hrSwallow", new MoveDef
    {
        Name = "闇に呑まれる",
        Tags = new[] { "horror", "emotional", "graphic" },
        W = 0.9,
        OutDur = (dur, _) => J.Clamp(dur * 0.4, 0.4, 0.9),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var seed = JAnim.SeedOf(it);
            var bx = JAnim.ItemBox(it);
            var sz = JAnim.Num(it, "size");
            var cx = J.Lerp(bx.X0, bx.X1, J.Rr(0.15, 0.85, seed, 71));
            var cy = J.Lerp(bx.Y0, bx.Y1, J.Rr(0.3, 0.7, seed, 72));
            var Rmax = Hypot(Math.Max(cx - bx.X0, bx.X1 - cx), Math.Max(cy - bx.Y0, bx.Y1 - cy)) + sz * 0.2;
            var R = Rmax * J.E.InCubic(J.Clamp(p / 0.75)) * 1.08;
            var pts = new List<double[]>();
            const int m = 22;
            for (var k = 0; k < m; k++)
            {
                var a = k / (double)m * J.TAU;
                var r = R * (0.72 + 0.4 * J.R(seed, k, 73) + 0.08 * Math.Sin(p * 9 + k));
                pts.Add(new[] { cx + Math.Cos(a) * r, cy + Math.Sin(a) * r });
            }

            // JS clipFn：整屏 rect + 反序 blob 子路径（nonzero 环绕把 blob 挖成洞）→ Env.ClipFn 桥
            var ptsArr = pts.ToArray();
            env.ClipFn = (e2, _) =>
            {
                var path = new SKPath();
                path.AddRect(new SKRect((float)(-e2.W * 2), (float)(-e2.H * 2), (float)(e2.W * 3), (float)(e2.H * 3)));
                if (R > 0.5)
                {
                    path.MoveTo((float)ptsArr[0][0], (float)ptsArr[0][1]);
                    for (var k = m - 1; k >= 0; k--) path.LineTo((float)ptsArr[k][0], (float)ptsArr[k][1]);
                    path.Close();
                }
                return path;
            };
            var dk = NightC(env.Sc!);
            var fade = 1 - J.Smooth(0.62, 0.95, p);
            // JS addPost → env.PostFn：黑渍画在文字上方（层级与 JS 一致）
            env.PostFn = (e2, _, _) =>
            {
                if (e2.Pass == "main" && R > 0.5) e2.Blob(ptsArr, dk, 0.85 * fade, false);
            };
            if (p > 0.97) it["alpha"] = 0.0;
        },
    });

    // ---------------- exit: hrFlickerDie（JS L426-441） ----------------

    private static void RegisterHrFlickerDie() => Reg0("exit", "hrFlickerDie", new MoveDef
    {
        Name = "明滅して消える",
        Tags = new[] { "horror", "glitch" },
        W = 0.9,
        OutDur = (dur, _) => J.Clamp(dur * 0.38, 0.35, 0.8),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var seed = JAnim.SeedOf(it);
            var st = (int)env.Step;
            var r = J.R(seed, st, 81);
            if (p > 0.92 || r > Math.Pow(1 - p, 1.3) * 0.95)
            {
                env.CharFns.Add((_, _, _) => HideXf);
                return;
            }

            if (J.R(seed, st, 82) < 0.22 && p > 0.15)
            {
                var cx = LayCenter(it).Cx;
                var sx = JAnim.NumOr1(it, "sx");
                var acc = ScK(env.Sc!, "accent");
                env.CharFns.Add((_, g, _) => new CharXf
                {
                    Sx = -1,
                    Dx = (cx - (g != null ? JAnim.Num(g, "x") : 0)) * sx * 2,
                    Color = acc,
                });
                it["y"] = JAnim.Num(it, "y") + J.Rs(seed, st, 83) * JAnim.Num(it, "size") * 0.1;
            }
            else
            {
                it["alpha"] = NumNull(it, "alpha", 1) * (0.55 + 0.45 * J.R(seed, st, 84));
            }
        },
    });

    // ---------------- exit: hrDrain（JS L444-477） ----------------

    private static void RegisterHrDrain() => Reg0("exit", "hrDrain", new MoveDef
    {
        Name = "滴り落ちる",
        Tags = new[] { "horror", "emotional" },
        W = 0.8,
        MinDur = 0.8,
        OutDur = (dur, _) => J.Clamp(dur * 0.45, 0.5, 1.1),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var cs = Cseed(env);
            var idx = IndexOf(env, it);
            var ord = OrderOf(env, it);
            var sz = JAnim.Num(it, "size");

            double Lev(int i, int n) => J.E.InOutSine(J.Clamp((p - ord(i, n) * 0.25) / 0.72));

            env.CharFns.Add((i, _, n) =>
            {
                var q = Lev(i, n);
                if (q <= 0) return null;
                if (q >= 0.995) return HideXf;
                return new CharXf { ClipY = new[] { -0.7 + q * 1.4, 0.7 } };
            });
            var c = ColOf(it);
            // JS addPost → env.PostFn：墨滴画在文字上方（层级与 JS 一致）
            if (env.Pass != "main") return;
            env.PostFn = (e2, _, _) =>
            {
                InItem(e2, it, () =>
                {
                    var lay = JText.LayoutText(it);
                    var sx = JAnim.NumOr1(it, "sx");
                    var sy = JAnim.NumOr1(it, "sy");
                    foreach (var g in lay.Cells)
                    {
                        if (g.Ch is " " or "　") continue;
                        var q = Lev(g.I, lay.N);
                        if (q <= 0.02) continue;
                        var k = unchecked((int)J.H(cs, idx(g.I, lay.N), 91));
                        var nd = 1 + (int)(k % 2);
                        for (var d = 0; d < nd; d++)
                        {
                            var x = (g.X + J.Rs(k, d, 1) * g.W * 0.3) * sx;
                            var yb = (g.Y + g.H * 0.4) * sy;
                            var l2 = sz * (0.3 + 1.4 * J.R(k, d, 2)) * J.E.OutCubic(q);
                            var a = NumNull(it, "alpha", 1) * (1 - J.Smooth(0.75, 1, q));
                            e2.Line(new[] { new[] { x, yb - sz * 0.05 }, new[] { x, yb + l2 } }, c, Math.Max(1, sz * 0.035), a, false);
                            e2.Circle(x, yb + l2, sz * 0.03, c, null, 0, a, false);
                        }
                    }
                });
            };
        },
    });

    // ---------------- hold: hrTwitch（JS L480-498） ----------------

    private static void RegisterHrTwitch() => Reg0("hold", "hrTwitch", new MoveDef
    {
        Name = "痙攣",
        Tags = new[] { "horror", "glitch" },
        W = 1,
        Draw = env =>
        {
            var it = env.It!;
            var k = env.Ap * MotionK(env);
            if (k < 0.02) return;
            var cs = Cseed(env);
            var st = (int)env.Step;
            var idx = IndexOf(env, it);
            var N = CutN(env);
            var sz = JAnim.Num(it, "size");
            if (J.R(cs, st, 101) > 0.07 * k) return;
            var whole = J.R(cs, st, 102) < 0.25;
            var target = (int)(J.H(cs, st, 103) % (uint)N);
            env.CharFns.Add((i, _, n) =>
            {
                if (!whole && idx(i, n) != target) return null;
                return new CharXf
                {
                    Dx = J.Rs(cs, st, 104) * sz * 0.22 * k,
                    Dy = J.Rs(cs, st, 105) * sz * 0.14 * k,
                    Rot = J.Rs(cs, st, 106) * 28 * k,
                    S = 1 + J.R(cs, st, 107) * 0.12 * k,
                };
            });
        },
    });

    // ---------------- hold: hrStare（JS L501-517） ----------------

    private static void RegisterHrStare() => Reg0("hold", "hrStare", new MoveDef
    {
        Name = "見つめる字",
        Tags = new[] { "horror", "emotional" },
        W = 0.8,
        Draw = env =>
        {
            var it = env.It!;
            var k = env.Ap * MotionK(env);
            if (k < 0.02) return;
            var cs = Cseed(env);
            var idx = IndexOf(env, it);
            var N = CutN(env);
            const double per = 2;
            var t = env.Ltb - 0.3;
            if (t <= 0) return;
            var cyc = (int)Math.Floor(t / per);
            var u = (t - cyc * per) / per;
            var target = (int)(J.H(cs, cyc, 111) % (uint)N);
            var q = u < 0.6 ? J.E.InOutSine(u / 0.6) : u < 0.85 ? 1 : 1 - J.E.OutExpo((u - 0.85) / 0.15);
            var dir = (J.H(cs, cyc, 112) & 1) != 0 ? 1 : -1;
            env.CharFns.Add((i, g, n) => idx(i, n) == target
                ? new CharXf
                {
                    Rot = dir * 22 * q * k,
                    S = 1 + 0.12 * q * k,
                    Dy = -(g != null ? JAnim.Num(g, "h") : 0) * 0.04 * q * k,
                }
                : null);
        },
    });

    // ---------------- hold: hrLagOne（JS L520-534） ----------------

    private static void RegisterHrLagOne() => Reg0("hold", "hrLagOne", new MoveDef
    {
        Name = "遅れる一字",
        Tags = new[] { "horror", "calm" },
        W = 0.8,
        Draw = env =>
        {
            var it = env.It!;
            var k = env.Ap * MotionK(env);
            if (k < 0.02) return;
            var cs = Cseed(env);
            var idx = IndexOf(env, it);
            var N = CutN(env);
            var sz = JAnim.Num(it, "size");
            var t = env.Ltb;
            var late = (int)(J.H(cs, 121) % (uint)N);

            double[] Sw(double tt) => new[]
            {
                J.Noise1(tt * 0.9, cs) * sz * 0.1 * k,
                J.Noise1(tt * 0.7 + 5, cs + 1) * sz * 0.06 * k,
                J.Noise1(tt * 0.5 + 9, cs + 2) * 4 * k,
            };

            var a = Sw(t);
            var b = Sw(t - 0.75);
            env.CharFns.Add((i, _, n) =>
            {
                var v = idx(i, n) == late ? b : a;
                return new CharXf { Dx = v[0], Dy = v[1], Rot = v[2] };
            });
        },
    });

    // ---------------- hold: hrFlickerLight（JS L537-550） ----------------

    private static void RegisterHrFlickerLight() => Reg0("hold", "hrFlickerLight", new MoveDef
    {
        Name = "切れかけの灯",
        Tags = new[] { "horror", "glitch", "emotional" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var k = env.Ap * J.Clamp(MotionK(env), 0.4, 1.3);
            if (k < 0.02) return;
            var cs = Cseed(env);
            var st = (int)env.Step;
            var run = st >> 1;
            var lvl = 0.9 + 0.1 * J.R(cs, st, 131);
            if (J.R(cs, run, 132) < 0.09 * k) lvl = J.R(cs, st, 133) < 0.5 ? 0.08 : 0.35;
            else if (J.R(cs, st, 134) < 0.05 * k) lvl = 0.5;
            var a = 1 - (1 - lvl) * Math.Min(1, k);
            it["alpha"] = NumNull(it, "alpha", 1) * a;
            if (a < 0.6) it["color"] = MixC(ColOf(it), ScK(env.Sc!, "bg"), 0.3);
        },
    });

    // ---------------- treat: hrInkBleed（JS L553-589） ----------------

    private static void RegisterHrInkBleed() => RegT("hrInkBleed", new TreatDef
    {
        Name = "滲み垂れ",
        Tags = new[] { "horror", "emotional" },
        W = 0.8,
        Plan = (rng, _) => new JObject { ["drips"] = rng.Range(0.25, 0.45), ["red"] = rng.Chance(0.4) },
        Draw = env =>
        {
            var it = env.It;
            var pm = env.Cut!.TreatP;
            if (it == null || !Alive(it)) return;
            var sc = env.Sc!;
            var c = ColOf(it);
            var halo = JLayouts.B(pm, "red")
                ? ScK(sc, "accent")
                : IsDarkBg(env) ? MixC(c, ScK(sc, "bg"), 0.5) : MixC(c, ScK(sc, "bg"), 0.3);
            var sz = JAnim.Num(it, "size");
            var single = LayN(it) == 1 && CutN(env) > 4;
            if (it["shadow"] == null && !single)
                it["shadow"] = new JObject
                {
                    ["color"] = Rgba(IsHex(halo) ? halo : "#000000", 0.8),
                    ["blur"] = sz * 0.07,
                    ["dx"] = 0.0,
                    ["dy"] = sz * 0.02,
                };
            var cs = Cseed(env);
            var rate = NumNull(pm, "drips", 0.35);
            // JS addPost → env.PostFn（见头注；层级与 JS 一致）
            if (env.Pass != "main") return;
            env.PostFn = (e2, _, _) =>
            {
                var cut = e2.Cut!;
                var d0 = cut.InDur * 1.1;
                var grow = J.Clamp((e2.Lt - d0) / Math.Max(0.8, cut.Dur * 0.7));
                if (grow <= 0) return;
                var a = NumNull(it, "alpha", 1) * (1 - e2.POut);
                if (a < 0.05) return;
                var dc = JLayouts.B(pm, "red") ? ScK(sc, "accent") : c;
                InItem(e2, it, () =>
                {
                    foreach (var q in GlyphList(e2, it))
                    {
                        var k = unchecked((int)J.H(cs, q.G.I, (int)Math.Round(JAnim.Num(it, "x")), 141));
                        if (J.R(k, 1) > rate || J.IsPunct(q.G.Ch[0])) continue;
                        var x = q.X + J.Rs(k, 2) * q.W * 0.25;
                        var y = q.Y + q.H * 0.36;
                        var l2 = sz * (0.2 + 0.9 * J.R(k, 3)) * J.E.OutCubic(grow);
                        var lw = Math.Max(1, sz * (0.018 + 0.02 * J.R(k, 4)));
                        e2.Line(new[] { new[] { x, y }, new[] { x + J.Rs(k, 5) * sz * 0.02, y + l2 } }, dc, lw, a * q.A * 0.9, false);
                        e2.Circle(x + J.Rs(k, 5) * sz * 0.02, y + l2, lw * 0.9, dc, null, 0, a * q.A * 0.9, false);
                    }
                });
            };
        },
    });

    // ---------------- treat: hrEroded（JS L592-617） ----------------

    private static void RegisterHrEroded() => RegT("hrEroded", new TreatDef
    {
        Name = "風化",
        Tags = new[] { "horror", "editorial", "graphic" },
        W = 0.8,
        Plan = (rng, _) => new JObject { ["dens"] = rng.Range(0.7, 1.2), ["scr"] = rng.Chance(0.7) },
        Draw = env =>
        {
            var it = env.It;
            var pm = env.Cut!.TreatP;
            if (it == null || !Alive(it)) return;
            var cs = Cseed(env);
            var bg = ScK(env.Sc!, "bg");
            var sz = JAnim.Num(it, "size");
            var dens = NumNull(pm, "dens", 1);
            // JS addPost → env.PostFn（见头注；层级与 JS 一致）
            if (env.Pass != "main" || NumNull(it, "alpha", 1) < 0.3) return;
            env.PostFn = (e2, _, _) =>
            {
                InItem(e2, it, () =>
                {
                    var budget = 420;
                    foreach (var q in GlyphList(e2, it))
                    {
                        if (q.A < 0.3) continue;
                        var k = unchecked((int)J.H(cs, q.G.I, 151));
                        var n = (int)Math.Round((16 + J.R(k, 1) * 14) * dens);
                        for (var j = 0; j < n && budget-- > 0; j++)
                        {
                            var r = sz * (0.01 + 0.045 * Math.Pow(J.R(k, j, 2), 2));
                            e2.Circle(q.X + J.Rs(k, j, 3) * q.W * 0.45, q.Y + J.Rs(k, j, 4) * q.H * 0.45, r, bg, null, 0, 1, false);
                        }

                        if (JLayouts.B(pm, "scr") && J.R(k, 5) < 0.45)
                        {
                            var a = J.R(k, 6) * Math.PI;
                            var l2 = q.W * 0.6;
                            e2.Line(new[]
                            {
                                new[] { q.X - Math.Cos(a) * l2 / 2, q.Y - Math.Sin(a) * l2 / 2 },
                                new[] { q.X + Math.Cos(a) * l2 / 2, q.Y + Math.Sin(a) * l2 / 2 },
                            }, bg, Math.Max(1.5, sz * 0.022), 1, false);
                        }
                    }
                });
            };
        },
    });

    // ---------------- treat: hrRedact（JS L620-660） ----------------

    private static void RegisterHrRedact() => RegT("hrRedact", new TreatDef
    {
        Name = "黒塗り",
        Tags = new[] { "horror", "editorial", "graphic" },
        W = 0.7,
        Plan = (rng, _) => new JObject
        {
            ["at"] = rng.Range(0.25, 0.5),
            ["frac"] = rng.Range(0.3, 0.6),
            ["from"] = rng.Range(0, 1),
        },
        Draw = env =>
        {
            var it = env.It;
            var pm = env.Cut!.TreatP;
            if (it == null || !Alive(it)) return;
            var N = LayN(it);
            var cs = Cseed(env);
            var ord = OrderOf(env, it);
            if (N == 1 && CutN(env) > 1)
            {
                // 单字项：按它在整句歌词中的位置决定是否参与遮盖
                var o = ord(0, 1);
                var a = JLayouts.D(pm, "from") * (1 - JLayouts.D(pm, "frac"));
                if (o < a || o > a + JLayouts.D(pm, "frac")) return;
            }

            var c = ColOf(it);
            var sc = env.Sc!;
            // JS addPost → env.PostFn（见头注；层级与 JS 一致）
            if (env.Pass != "main") return;
            env.PostFn = (e2, _, _) =>
            {
                var cut = e2.Cut!;
                var t0 = cut.InDur + (cut.Dur - cut.InDur - cut.OutDur) * JLayouts.D(pm, "at");
                var k = J.E.InOutCubic(J.Clamp((e2.Lt - t0) / 0.35));
                var gl = GlyphList(e2, it);
                if (gl.Count == 0) return;
                var s0 = 0;
                var s1 = gl.Count - 1;
                if (gl.Count > 2)
                {
                    var mm = Math.Max(1, (int)Math.Round(gl.Count * JLayouts.D(pm, "frac")));
                    s0 = Math.Min(gl.Count - mm, (int)Math.Floor(JLayouts.D(pm, "from") * (gl.Count - mm + 1)));
                    s1 = s0 + mm - 1;
                }

                double x0 = 1e9, x1 = -1e9, y0 = 1e9, y1 = -1e9;
                for (var j = s0; j <= s1; j++)
                {
                    var q = gl[j];
                    x0 = Math.Min(x0, q.X - q.W / 2);
                    x1 = Math.Max(x1, q.X + q.W / 2);
                    y0 = Math.Min(y0, q.Y - q.H / 2);
                    y1 = Math.Max(y1, q.Y + q.H / 2);
                }

                var pd = JAnim.Num(it, "size") * 0.06;
                var a2 = NumNull(it, "alpha", 1);
                InItem(e2, it, () =>
                {
                    var w = x1 - x0 + pd * 2;
                    if (k < 1)
                        e2.Rect(x0 - pd + w * k, y0 - pd * 0.5, w * (1 - k), y1 - y0 + pd, c, a2, false);
                    if (k > 0)
                        e2.Line(new[]
                        {
                            new[] { x0 - pd, y0 - pd * 0.5 }, new[] { x1 + pd, y0 - pd * 0.5 },
                            new[] { x1 + pd, y1 + pd * 0.5 }, new[] { x0 - pd, y1 + pd * 0.5 },
                            new[] { x0 - pd, y0 - pd * 0.5 },
                        }, ScK(sc, "sub"), Math.Max(1, JAnim.Num(it, "size") * 0.01), a2 * 0.5 * k, false);
                });
            };
        },
    });

    // ---------------- treat: hrDoubleExp（JS L663-684） ----------------

    private static void RegisterHrDoubleExp() => RegT("hrDoubleExp", new TreatDef
    {
        Name = "二重露光",
        Tags = new[] { "horror", "emotional", "glitch" },
        W = 0.8,
        Plan = (rng, _) => new JObject { ["amp"] = rng.Range(0.18, 0.28), ["k"] = rng.Range(1.03, 1.08) },
        Draw = env =>
        {
            var it = env.It;
            var pm = env.Cut!.TreatP;
            if (it == null || !Alive(it)) return;
            var cs = Cseed(env);
            var sc = env.Sc!;
            string? col = null;
            foreach (var c in new[] { sc.Str("sub"), sc.Str("ghostA"), sc.Str("fg") })
                if (!string.IsNullOrEmpty(c) && c != JLayouts.S(it, "color") && J.Contrast(c, ScK(sc, "bg")) > 1.3)
                {
                    col = c;
                    break;
                }

            col ??= ScK(sc, "sub");
            // JS addPre → env.PreFn（层级/时点与 JS 一致）
            if (env.Pass != "main") return;
            env.PreFn = (e2, _) =>
            {
                if (NumNull(it, "alpha", 1) < 0.05) return;
                var t = e2.Ltb;
                var sz = JAnim.Num(it, "size");
                var amp = NumNull(pm, "amp", 0.12);
                var c2 = Bare(it, new JObject
                {
                    ["x"] = JAnim.Num(it, "x") + J.Noise1(t * 0.6, cs + 3) * sz * amp,
                    ["y"] = JAnim.Num(it, "y") + J.Noise1(t * 0.5, cs + 4) * sz * amp * 0.7,
                    ["size"] = sz * NumNull(pm, "k", 1.05),
                    ["color"] = col,
                    ["alpha"] = NumNull(it, "alpha", 1) * 0.4,
                    ["ghost"] = false,
                });
                DrawCopy(e2, c2);
            };
        },
    });
}
