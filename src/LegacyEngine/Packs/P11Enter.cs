// 移植自 frontend/src/engine/11p_enter.js（enter 表现包第一批，pack = 'enter'，40 条）。
//
// 契约与 JAnim.cs（核心 13 enter）相同：MoveDef.Draw(env) 里 it = env.It、p = env.Ap、
// env.Cut 携带 {dur,inDur,outDur}；逐字动画推 env.CharFns（管线 mainDraw 统一 CombineChar）、
// 逐片动画推 env.PieceFns；条目字段直接写 item（bands/vbands/clip/clipY/streak/echo/
// shadow/dash/fillAlpha/stroke…JLayouts.DrawFx 全部消费）。随机数全部是 J.R/J.Rs/J.H
// 确定性哈希（无顺序流），哈希入参逐字保持——同 seed 同动画轨迹。
//
// 管线缺口与降级（本文件内解决，不改共享文件）：
// 1. it.pre/it.post（addPre/addPost/postLocal）：管线钩子已接（Env.PreFn/PostFn → JLayouts.DrawFx，
//    pre 画在文字前、post 画在文字后）。本包经 DrawPre/DrawPostLocal 挂钩（链式、addPre 同语义）。
// 2. it.clipFn（clipLocal 任意路径裁剪）：任意路径裁剪管线已接（Env.ClipFn → JLayouts.DrawFx 的
//    ClipPath，用法见 P11Exit）；本包未升级，仍按旧法近似——矩形折算成每字形 clipX/clipY（glyph
//    盒比例、中心 0，与 JText.drawItem 的 ClipRect 同口径）精确对位，非矩形遮罩（圆/棋盘/百叶/
//    扫描线）按字形粒度做可见性近似（ClipHideGlyphs，行内注明）。
// 3. JS 的 mergeChar/_enterFix 再合并（J.combineChar 丢 clipX/skew/blur 扩展字段的补丁）：
//    C# JAnim.CombineChar 全字段合并，不需要。
// 4. `ae`（AE 导出的模板名，11_export 消费）与 pieces: true（本包无 pieces 条目）MoveDef
//    没有对应属性，未注册——对位见各条目行内注释。
//
// 跨文件符号对位：J.measure→JText.Measure（本包用返回 TextLayout 的强类型版）、
// J.layoutText→JText.LayoutText、J.pool→JLang.Pool、J.drawItem→JText.DrawItem、
// J.mainDraw→JLayouts.MainDraw、J.rgba(本地 Rgba)/r/rs/h/mix/lum/smooth/clamp/lerp→J.*。

using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace JizuraLive.LegacyEngine;

public static class P11Enter
{
    private const string P = "enter";
    private static int _installed;

    private static void Reg0(string key, MoveDef def) => Reg.Register("enter", key, def, P);

    // ================= 本包 helper（JS L10-195 逐条对位） =================

    private static double OQuart(double x) => 1 - Math.Pow(1 - J.Clamp(x), 4);
    private static double OQuint(double x) => 1 - Math.Pow(1 - J.Clamp(x), 5);

    private static double IoQuart(double x)
    {
        x = J.Clamp(x);
        return x < 0.5 ? 8 * x * x * x * x : 1 - Math.Pow(-2 * x + 2, 4) / 2;
    }

    private static double OBack(double x, double s) => J.E.OutBack(J.Clamp(x), s);

    /// <summary>k 是字的 0..1 次序，spread 是入场进度里拿来做错峰的比例。JS stg。</summary>
    private static double Stg(double p, double k, double spread) => J.Clamp((p - spread * k) / (1 - spread));

    private static double OrdLR(int i, int n) => n > 1 ? i / (double)(n - 1) : 0;
    private static double OrdC(int i, int n) => n > 1 ? Math.Abs(i - (n - 1) / 2.0) / ((n - 1) / 2.0) : 0;

    private static bool IsHex(string? c) => c != null && c.Length > 0 && c[0] == '#' && (c.Length == 7 || c.Length == 4);

    private static string ColOf(JObject it)
    {
        var c = S(it, "color");
        return IsHex(c) ? c! : "#ffffff";
    }

    private static string Pick(params string?[] cs) => cs.FirstOrDefault(IsHex) ?? "#ffffff";

    /// <summary>JS dirOf：条目级方向币（每次 Draw 重掷，同帧内一致）。</summary>
    private static int DirOf(Env env, int salt) => J.R(unchecked((int)env.Cut!.Seed), salt, 5) < 0.5 ? -1 : 1;

    /// <summary>去空白后的码点数（JS [...str.replace(/\s/g,'')].length）。</summary>
    private static int GlyphN(JObject it) => JLayouts.CpLen(Regex.Replace(S(it, "text") ?? "", @"\s", ""));

    private static bool Blank(JObject g) => S(g, "ch") is " " or "　";

    private static JAnim.CharFn Glyphs(Env env, JAnim.CharFn fn)
    {
        env.CharFns.Add(fn);
        return fn;
    }

    /// <summary>条目本地空间：原点在 it.x/it.y，旋转之前。JS box。</summary>
    private sealed class EBox
    {
        public double X0, Y0, X1, Y1, W, H, Cx, Cy;
        public JText.TextLayout Lay = null!;
    }

    private static EBox Box(JObject it)
    {
        var m = JText.Measure(it);
        var align = S(it, "align");
        var vert = B(it, "vertical");
        var x0 = vert ? -m.W / 2 : align == "left" ? 0 : align == "right" ? -m.W : -m.W / 2;
        var y0 = vert && align == "left" ? 0 : -m.H / 2;
        return new EBox
        {
            X0 = x0,
            Y0 = y0,
            X1 = x0 + m.W,
            Y1 = y0 + m.H,
            W = m.W,
            H = m.H,
            Cx = x0 + m.W / 2,
            Cy = y0 + m.H / 2,
            Lay = m.Lay,
        };
    }

    /// <summary>局部盒子在设计空间的 AABB（考虑旋转）。JS dRange。</summary>
    private static (double X0, double X1, double Y0, double Y1) DRange(JObject it, EBox b)
    {
        var r = N0(it, "rot") * J.DEG;
        var c = Math.Cos(r);
        var s = Math.Sin(r);
        double x0 = 1e9, x1 = -1e9, y0 = 1e9, y1 = -1e9;
        foreach (var (u, v) in new[] { (b.X0, b.Y0), (b.X1, b.Y0), (b.X0, b.Y1), (b.X1, b.Y1) })
        {
            var X = N0(it, "x") + c * u - s * v;
            var Y = N0(it, "y") + s * u + c * v;
            x0 = Math.Min(x0, X);
            x1 = Math.Max(x1, X);
            y0 = Math.Min(y0, Y);
            y1 = Math.Max(y1, Y);
        }
        return (x0, x1, y0, y1);
    }

    /// <summary>以条目自身盒子中心缩放（有些布局把条目锚在左/上）。JS scaleAbout。</summary>
    private static void ScaleAbout(JObject it, double k, EBox? b = null)
    {
        if (k == 1) return;
        b ??= Box(it);
        it["size"] = N0(it, "size") * k;
        var cx = b.Cx * (k - 1);
        var cy = b.Cy * (k - 1);
        if (cx != 0 || cy != 0)
        {
            var r = N0(it, "rot") * J.DEG;
            var c = Math.Cos(r);
            var s = Math.Sin(r);
            it["x"] = N0(it, "x") - (c * cx - s * cy);
            it["y"] = N0(it, "y") - (s * cx + c * cy);
        }
    }

    /// <summary>改字距但让盒子停在原地。JS setTrack。</summary>
    private static void SetTrack(JObject it, double tr)
    {
        var b0 = Box(it);
        it["track"] = tr;
        var b1 = Box(it);
        var cx = b1.Cx - b0.Cx;
        var cy = b1.Cy - b0.Cy;
        if (cx != 0 || cy != 0)
        {
            var r = N0(it, "rot") * J.DEG;
            var c = Math.Cos(r);
            var s = Math.Sin(r);
            it["x"] = N0(it, "x") - (c * cx - s * cy);
            it["y"] = N0(it, "y") - (s * cx + c * cy);
        }
    }

    /// <summary>盖在条目上的横向 glitch 切条（设计空间）。JS hBands → it.bands（DrawFx 消费）。</summary>
    private static List<double[]> HBands(JObject it, int n, Func<int, double> dxFn)
    {
        var R = DRange(it, Box(it));
        var pad = N0(it, "size") * 0.3;
        var y0 = R.Y0 - pad;
        var h = R.Y1 - R.Y0 + pad * 2;
        var seed = SeedOf(it);
        var cuts = new List<double> { 0 };
        for (var i = 1; i < n; i++) cuts.Add(J.R(seed, n, i, 3));
        cuts.Add(1);
        cuts.Sort();
        var @out = new List<double[]>();
        for (var i = 0; i < cuts.Count - 1; i++)
            @out.Add(new[] { y0 + cuts[i] * h, y0 + cuts[i + 1] * h, dxFn(i) });
        return @out;
    }

    /// <summary>单字遮罩：遮罩停在字的落位盒上，字本身位移 (fx, fy) 个字盒。JS masked。</summary>
    private static CharXf Masked(JObject g, double fx, double fy, double isx, double isy)
    {
        var o = new CharXf { Dx = fx * JLayouts.D(g, "w") * isx, Dy = fy * JLayouts.D(g, "h") * isy };
        if (B(g, "r90"))
        {
            o.A = J.Clamp(1 - Math.Max(Math.Abs(fx), Math.Abs(fy)) * 1.4);
            return o;
        }
        o.ClipX = new[] { -2 - fx, 2 - fx };
        o.ClipY = new[] { -0.66 - fy, 0.66 - fy };
        if (fx != 0) o.ClipX = new[] { -0.62 - fx, 0.62 - fx };
        if (fy == 0) o.ClipY = new[] { -2.0, 2.0 };
        return o;
    }

    private static double[] GlyphPos(JObject g, double isx, double isy) =>
        new[] { (JLayouts.D(g, "x") + JLayouts.D(g, "vx")) * isx, (JLayouts.D(g, "y") + JLayouts.D(g, "vy")) * isy };

    private static double[] GlyphPos(JText.GlyphCell g, double isx, double isy) =>
        new[] { (g.X + g.Vx) * isx, (g.Y + g.Vy) * isy };

    /// <summary>
    /// it.clipFn 的矩形对位：任意路径裁剪管线已接（Env.ClipFn → DrawFx ClipPath），但矩形这里仍走
    /// 每字形折算（与逐字遮罩同口径自然叠加）——把局部空间
    /// 轴对齐矩形 (rx0,ry0,rx1,ry1) 折算成每字形 clipX/clipY 推一个额外 charFn——
    /// 比例 = 局部位移 / 字形盒（与 drawItem 的 ClipRect(cx[0]*g.W) 同口径，r90 字形按 +90°
    /// 旋转换轴）。combineChar 对 clipX/clipY 取交集，与条目自身的逐字遮罩自然叠加。
    /// </summary>
    private static void ClipRectLocal(Env env, JObject it, double isx, double isy, double rx0, double ry0, double rx1, double ry1)
    {
        env.CharFns.Add((_, g, _) =>
        {
            if (g == null) return null;
            return RectGlyphClip(g, isx, isy, rx0, ry0, rx1, ry1);
        });
    }

    private static CharXf RectGlyphClip(JObject g, double isx, double isy, double rx0, double ry0, double rx1, double ry1)
    {
        var gx = (JLayouts.D(g, "x") + JLayouts.D(g, "vx")) * isx;
        var gy = (JLayouts.D(g, "y") + JLayouts.D(g, "vy")) * isy;
        var hx = Math.Max(1e-6, JLayouts.D(g, "w") * isx / 2);
        var hy = Math.Max(1e-6, JLayouts.D(g, "h") * isy / 2);
        if (B(g, "r90")) // 字形本体带 +90° 旋转：裁剪框换轴
            return new CharXf
            {
                ClipX = new[] { (ry0 - gy) / (2 * hy), (ry1 - gy) / (2 * hy) },
                ClipY = new[] { -(rx1 - gx) / (2 * hx), -(rx0 - gx) / (2 * hx) },
            };
        return new CharXf
        {
            ClipX = new[] { (rx0 - gx) / (2 * hx), (rx1 - gx) / (2 * hx) },
            ClipY = new[] { (ry0 - gy) / (2 * hy), (ry1 - gy) / (2 * hy) },
        };
    }

    /// <summary>非矩形遮罩的字形粒度可见性近似：返回 null=保持原样、Hide=该字形还没被遮罩扫到。</summary>
    private static void ClipHideGlyphs(Env env, Func<JObject, bool?> visFn) =>
        env.CharFns.Add((_, g, _) =>
        {
            if (g == null) return null;
            var v = visFn(g);
            return v == false ? new CharXf { Hide = true } : null;
        });

    /// <summary>
    /// it.pre/addPre 的对位：把钩子挂 env.PreFn（addPre 链式语义，先挂的先跑），
    /// JLayouts.DrawFx 在所有裁剪之前、主文字之前消费——层级与 JS 一致。
    /// </summary>
    private static void DrawPre(Env env, JObject it, Action<Env, JObject> fn)
    {
        var prev = env.PreFn;
        env.PreFn = prev != null ? (e, x) => { prev(e, x); fn(e, x); } : (Action<Env, JObject>)fn;
    }

    /// <summary>
    /// it.post/postLocal 的对位：把钩子挂 env.PostFn（DrawFx 在主文字之后消费，层级与 JS 一致）；
    /// 钩子体内做 postLocal 的 save/toLocal/restore 包装与 alpha 门限（alpha 在 drawFx 时点读，
    /// 与 JS post 阶段同序）。
    /// </summary>
    private static void DrawPostLocal(Env env, JObject it, Action<Env, EBox, JObject, double> fn)
    {
        void Hook(Env e, JObject x, BBox? _)
        {
            var a = J.Clamp(JAnim.NumNull(x, "alpha", 1));
            if (a <= 0.01) return;
            var b = Box(x);
            e.Ctx.Save();
            ToLocal(e.Ctx, x);
            try
            {
                fn(e, b, x, a);
            }
            finally
            {
                e.Ctx.Restore();
            }
        }
        var prev = env.PostFn;
        env.PostFn = prev != null ? (e, x, bb) => { prev(e, x, bb); Hook(e, x, bb); } : (Action<Env, JObject, BBox?>)Hook;
    }

    /// <summary>JS toLocal：translate(it.x,it.y) → rotate(it.rot) → skewX(it.skew)。</summary>
    private static void ToLocal(SKCanvas ctx, JObject it)
    {
        ctx.Translate((float)N0(it, "x"), (float)N0(it, "y"));
        var rot = N0(it, "rot");
        if (rot != 0) ctx.RotateDegrees((float)rot);
        var skew = N0(it, "skew");
        if (skew != 0) ctx.Skew((float)Math.Tan(skew * J.DEG), 0);
    }

    /// <summary>再画一份条目（JS J.drawItem 直调）：临时置换 env.CharFn/PieceFn（NoPieces 对位 JS 副本 pieceFn:null）。</summary>
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
                NoPieces = true,
            });
        }
        finally
        {
            env.CharFn = prevC;
            env.PieceFn = prevP;
        }
    }

    /// <summary>J.rgba(h, a)：hex + alpha → "rgba(r,g,b,a)"（drawItem 的 shadow.color 吃这个格式）。</summary>
    private static string Rgba(string hex, double a)
    {
        var (r, g, b) = J.Hex(hex);
        return "rgba(" + r + "," + g + "," + b + "," + a + ")";
    }

    // 松散读取（对齐 JS 语义）：(x||0)/(x||1)/(x??d)
    private static string? S(JObject o, string k) => JLayouts.S(o, k);
    private static double D(JObject o, string k, double d = 0) => JLayouts.D(o, k, d);
    private static bool B(JObject o, string k) => JLayouts.B(o, k);
    private static double N0(JObject o, string k) => JAnim.Num(o, k);
    private static double NN(JObject o, string k, double d) => JAnim.NumNull(o, k, d);
    private static int SeedOf(JObject o) => JAnim.SeedOf(o);

    /// <summary>JS it.fill !== false：只有显式 boolean false 才算关。</summary>
    private static bool FillOn(JObject o) =>
        !(o["fill"] is JValue v && v.Type == JTokenType.Boolean && !(bool)v.Value!);

    // ================= 注册（JS DEFS 字面量序，Object.keys 语义） =================

    /// <summary>幂等注册本文件全部 enter 条目（顺序 = JS 源 DEFS 字面量序；不挂静态构造函数）。</summary>
    /// <summary>JS 的 it.xxx || 1 语义：缺省/非数值回 1（供 enter 条目读 sx/sy 等缩放）。</summary>
    private static double N1(JObject it, string k)
    {
        var v = it?[k];
        return v != null && (v.Type == JTokenType.Float || v.Type == JTokenType.Integer) ? (double)v : 1;
    }

    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;
        RiseMask();      // JS L201
        DropMask();      // JS L213
        SlideL();        // JS L226
        SlideR();        // JS L240
        SlideWhole();    // JS L254
        FlipX();         // JS L264
        FlipY();         // JS L288
        Domino();        // JS L309
        Fold();          // JS L326
        Unroll();        // JS L342
        StrokeDraw();    // JS L376
        OutlineFill();   // JS L386
        SplitJoin();     // JS L436
        VSlice();        // JS L469
        Shutter();       // JS L485
        Iris();          // JS L521
        DiagWipe();      // JS L540
        Blinds();        // JS L578
        Checker();       // JS L600
        RandomOrder();   // JS L635
        BounceBig();     // JS L652
        SquashDrop();    // JS L680
        Rubber();        // JS L705
        GlitchIn();      // JS L720
        EchoIn();        // JS L744
        Whip();          // JS L787
        SkewIn();        // JS L803
        TrackIn();       // JS L821
        TrackOut();      // JS L831
        BlurStagger();   // JS L844
        FadeStagger();   // JS L858
        WaveIn();        // JS L872
        SpiralIn();      // JS L886
        ZoomOut();       // JS L907
        Resolve();       // JS L917
        Magnet();        // JS L949
        InkBleed();      // JS L980
        NeonOn();        // JS L1003
        CursorSweep();   // JS L1026
        Stamp();         // JS L1054
    }

    // ---------------- riseMask（JS L201） ----------------

    private static void RiseMask() => Reg0("riseMask", new MoveDef
    {
        Name = "下から出現",
        Tags = new[] { "editorial", "graphic", "calm", "emotional" },
        W = 1.4,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var isx = N1(it, "sx");
            var isy = N1(it, "sy");
            Glyphs(env, (i, g, n) =>
            {
                var q = Stg(p, OrdLR(i, n), 0.45);
                if (q <= 0) return new CharXf { Hide = true };
                if (q >= 1) return null;
                return g == null ? null : Masked(g, 0, 1.15 * (1 - OQuint(q)), isx, isy);
            });
        },
    });

    // ---------------- dropMask（JS L213） ----------------

    private static void DropMask() => Reg0("dropMask", new MoveDef
    {
        Name = "上から出現",
        Tags = new[] { "editorial", "graphic", "pop" },
        W = 1.2,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var isx = N1(it, "sx");
            var isy = N1(it, "sy");
            Glyphs(env, (i, g, n) =>
            {
                var q = Stg(p, OrdLR(i, n), 0.45);
                if (q <= 0) return new CharXf { Hide = true };
                if (q >= 1) return null;
                return g == null ? null : Masked(g, 0, -1.15 * (1 - OBack(q, 1.35)), isx, isy);
            });
        },
    });

    // ---------------- slideL（JS L226） ----------------

    private static void SlideL() => Reg0("slideL", new MoveDef
    {
        Name = "左からスライド",
        Tags = new[] { "editorial", "calm", "pop" },
        W = 1,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var size = N0(it, "size");
            Glyphs(env, (i, _, n) =>
            {
                var q = Stg(p, OrdLR(i, n), 0.5);
                if (q <= 0) return new CharXf { Hide = true };
                if (q >= 1) return null;
                var e = OQuint(q);
                return new CharXf { Dx = -(1 - e) * size * 0.85, A = Math.Pow(J.Clamp(q * 1.6), 1.6) };
            });
        },
    });

    // ---------------- slideR（JS L240） ----------------

    private static void SlideR() => Reg0("slideR", new MoveDef
    {
        Name = "右からスライド",
        Tags = new[] { "editorial", "graphic", "pop" },
        W = 1,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var isx = N1(it, "sx");
            var isy = N1(it, "sy");
            Glyphs(env, (i, g, n) =>
            {
                var q = Stg(p, OrdLR(i, n), 0.45);
                if (q <= 0) return new CharXf { Hide = true };
                if (q >= 1) return null;
                return g == null ? null : Masked(g, 1.2 * (1 - OQuint(q)), 0, isx, isy);
            });
        },
    });

    // ---------------- slideWhole（JS L254） ----------------

    private static void SlideWhole() => Reg0("slideWhole", new MoveDef
    {
        Name = "全体スライド",
        Tags = new[] { "pop", "graphic" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var dir = DirOf(env, 11);
            var e = OBack(p, 1.7);
            var dd = N0(it, "size") * (GlyphN(it) <= 1 ? 1.3 : 3.2) * (0.7 + 0.6 * env.Fx.Motion);
            if (B(it, "vertical")) it["y"] = N0(it, "y") + dir * dd * (1 - e);
            else it["x"] = N0(it, "x") + dir * dd * (1 - e);
            it["alpha"] = NN(it, "alpha", 1) * J.Clamp(p * 4);
        },
    });

    // ---------------- flipX（JS L264） ----------------

    private static void FlipX() => Reg0("flipX", new MoveDef
    {
        Name = "縦軸フリップ",
        Tags = new[] { "pop", "graphic", "editorial" },
        W = 1,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var sc = env.Sc!;
            var col = ColOf(it);
            var bg = Pick(sc.Str("bg"));
            var back = S(it, "color") == sc.Str("accent")
                ? Pick(sc.Str("accent2"), sc.Str("fg"))
                : Pick(sc.Str("accent"), sc.Str("fg"));
            Glyphs(env, (i, g, n) =>
            {
                var q = Stg(p, OrdLR(i, n), 0.45);
                if (q <= 0) return new CharXf { Hide = true };
                if (q >= 1) return null;
                var pop = OBack(q / 0.22, 1.6);
                var th = (1 - OBack((q - 0.22) / 0.78, 1.25)) * 180 * J.DEG;
                var c = Math.Cos(th);
                var sh = Math.Abs(Math.Sin(th));
                if (c < 0)
                    return new CharXf
                    {
                        Ch = "■",
                        Sx = Math.Max(0.04, -c),
                        S = 1.02 * pop,
                        Color = J.Mix(back, bg, sh * 0.35),
                        A = J.Clamp(q * 8),
                    };
                return new CharXf { Sx = Math.Max(0.04, c), Color = J.Mix(col, bg, Math.Min(0.5, sh * 0.5)) };
            });
        },
    });

    // ---------------- flipY（JS L288） ----------------

    private static void FlipY() => Reg0("flipY", new MoveDef
    {
        Name = "横軸フリップ",
        Tags = new[] { "pop", "graphic" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var col = ColOf(it);
            var bg = Pick(env.Sc!.Str("bg"));
            Glyphs(env, (i, _, n) =>
            {
                var q = Stg(p, OrdC(i, n), 0.4);
                if (q <= 0) return new CharXf { Hide = true };
                if (q >= 1) return null;
                var th = (1 - OQuart(q)) * 270 * J.DEG;
                var c = Math.Cos(th);
                if (Math.Abs(c) < 0.04) c = c < 0 ? -0.04 : 0.04;
                return new CharXf
                {
                    Sy = c,
                    A = J.Clamp(q * 3),
                    Color = J.Mix(col, bg, Math.Min(0.75, Math.Abs(Math.Sin(th)) * 0.6 + (c < 0 ? 0.2 : 0))),
                };
            });
        },
    });

    // ---------------- domino（JS L309） ----------------

    private static void Domino() => Reg0("domino", new MoveDef
    {
        Name = "ドミノ",
        Tags = new[] { "pop" },
        W = 0.7,
        MinDur = 0.5,
        InDur = (dur, _) => J.Clamp(dur * 0.42, 0.15, 0.75),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var isx = N1(it, "sx");
            var isy = N1(it, "sy");
            Glyphs(env, (i, g, n) =>
            {
                if (g == null) return null;
                var q = Stg(p, OrdLR(i, n), 0.55);
                if (q <= 0) return new CharXf { Hide = true };
                if (q >= 1) return null;
                // 从右侧倒下到立起，支点是右下角
                var th = (1 - OBack(q, 1.5)) * 90 * J.DEG;
                var hw = JLayouts.D(g, "w") * isx / 2;
                var hh = JLayouts.D(g, "h") * isy / 2;
                var vx = -hw;
                var vy = -hh;
                var c = Math.Cos(th);
                var s = Math.Sin(th);
                return new CharXf
                {
                    Dx = c * vx - s * vy - vx,
                    Dy = s * vx + c * vy - vy,
                    Rot = th / J.DEG,
                    A = J.Clamp(q * 4),
                };
            });
        },
    });

    // ---------------- fold（JS L326） ----------------

    private static void Fold() => Reg0("fold", new MoveDef
    {
        Name = "折り開き",
        Tags = new[] { "graphic", "editorial" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var isy = N1(it, "sy");
            var col = ColOf(it);
            var bg = Pick(env.Sc!.Str("bg"));
            Glyphs(env, (i, g, n) =>
            {
                if (g == null) return null;
                var q = Stg(p, OrdLR(i, n), 0.5);
                if (q <= 0) return new CharXf { Hide = true };
                if (q >= 1) return null;
                var sy = Math.Max(0.02, OBack(q, 1.6));
                var dy = (i % 2 != 0 ? 1 : -1) * (1 - sy) * JLayouts.D(g, "h") * isy / 2;
                return new CharXf
                {
                    Sy = sy,
                    Dy = dy,
                    A = J.Clamp(q * 4),
                    Color = J.Mix(col, bg, J.Clamp(1 - sy) * 0.6),
                };
            });
        },
    });

    // ---------------- unroll（JS L342） ----------------

    private static void Unroll() => Reg0("unroll", new MoveDef
    {
        Name = "巻き開き",
        Tags = new[] { "calm", "editorial", "emotional" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var vert = B(it, "vertical");
            var isx = N1(it, "sx");
            var isy = N1(it, "sy");
            var size = N0(it, "size");

            double Q(int i, int n) => Stg(p, OrdLR(i, n), 0.5);
            Glyphs(env, (i, g, n) =>
            {
                if (g == null) return null;
                var u = Q(i, n);
                if (u <= 0) return new CharXf { Hide = true };
                if (u >= 1) return null;
                var e = J.E.OutCubic(u);
                var k = J.Lerp(0.3, 1, e);
                var r = J.Lerp(-0.62, 0.62, e);
                return vert
                    ? new CharXf { Sy = k, Dy = -(1 - k) * JLayouts.D(g, "h") * isy / 2, ClipY = new[] { -2.0, r }, ClipX = new[] { -2.0, 2.0 } }
                    : new CharXf { Sx = k, Dx = -(1 - k) * JLayouts.D(g, "w") * isx / 2, ClipX = new[] { -2.0, r }, ClipY = new[] { -2.0, 2.0 } };
            });
            // JS postLocal（卷边的 accent 细条，画在文字后）——管线未挂 post，就地执行（层级在文字下）
            DrawPostLocal(env, it, (env2, _, x, A) =>
            {
                var lay = JText.LayoutText(x);
                var n = lay.N;
                var t = Math.Max(1.5, size * 0.028);
                foreach (var g in lay.Cells)
                {
                    if (g.Ch is " " or "　") continue;
                    var u = Q(g.I, n);
                    if (u <= 0 || u >= 1) continue;
                    var e = J.E.OutCubic(u);
                    var k = J.Lerp(0.3, 1, e);
                    var r = J.Lerp(-0.62, 0.62, e);
                    var a = Math.Pow(1 - e, 0.6) * A;
                    var gpos = GlyphPos(g, isx, isy);
                    var gx = gpos[0];
                    var gy = gpos[1];
                    if (vert)
                    {
                        var y = gy - (1 - k) * g.H * isy / 2 + r * g.H * isy * k;
                        env2.Rect(gx - g.W * isx * 0.56, y - t / 2, g.W * isx * 1.12, t, env2.Sc!.Str("accent"), a, false);
                    }
                    else
                    {
                        var X = gx - (1 - k) * g.W * isx / 2 + r * g.W * isx * k;
                        env2.Rect(X - t / 2, gy - g.H * isy * 0.56, t, g.H * isy * 1.12, env2.Sc!.Str("accent"), a, false);
                    }
                }
            });
        },
    });

    // ---------------- strokeDraw（JS L376） ----------------

    private static void StrokeDraw() => Reg0("strokeDraw", new MoveDef
    {
        Name = "線画から塗り",
        Tags = new[] { "calm", "emotional", "editorial" },
        W = 1,
        MinDur = 0.8,
        InDur = (dur, _) => J.Clamp(dur * 0.5, 0.2, 0.95),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            it["dash"] = J.E.OutQuad(J.Clamp(p / 0.86));
            it["fillAlpha"] = NN(it, "fillAlpha", 1) * J.Smooth(0.42, 0.9, p);
            if (!(N0(it, "stroke") > 0))
                it["stroke"] = Math.Max(0.6, N0(it, "size") * 0.032 * (1 - J.Smooth(0.55, 0.86, p)));
        },
    });

    // ---------------- outlineFill（JS L386） ----------------

    private static void OutlineFill() => Reg0("outlineFill", new MoveDef
    {
        Name = "輪郭→塗り",
        Tags = new[] { "graphic", "pop", "editorial" },
        W = 1,
        MinDur = 0.7,
        InDur = (dur, _) => J.Clamp(dur * 0.5, 0.2, 0.9),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var sw = N0(it, "stroke") > 0 ? N0(it, "stroke") : Math.Max(1.2, N0(it, "size") * 0.028);
            var hasFill = FillOn(it);

            double Q(int i, int n) => Stg(p, OrdLR(i, n), 0.4);
            CharXf? FnOut(int i, JObject? g, int n)
            {
                var u = Q(i, n);
                if (u <= 0 || (hasFill && u >= 0.9)) return new CharXf { Hide = true };
                var e = OQuint(u / 0.35);
                return new CharXf { Outline = true, S = J.Lerp(1.3, 1, e), A = J.Clamp(u * 5) * (1 - J.Smooth(0.68, 0.9, u)) };
            }
            if (!hasFill)
            {
                Glyphs(env, (i, _, n) =>
                {
                    var u = Q(i, n);
                    if (u <= 0) return new CharXf { Hide = true };
                    if (u >= 1) return null;
                    return new CharXf { S = J.Lerp(1.3, 1, OQuint(u / 0.35)), A = J.Clamp(u * 5) };
                });
                return;
            }
            var main = Glyphs(env, (i, g, n) =>
            {
                var u = Q(i, n);
                if (u <= 0.3) return new CharXf { Hide = true };
                if (u >= 1) return null;
                if (g != null && B(g, "r90")) return new CharXf { A = J.Clamp((u - 0.3) / 0.55) };
                var f = IoQuart((u - 0.3) / 0.55);
                if (f >= 1) return null;
                return new CharXf { ClipY = new[] { J.Lerp(0.66, -0.68, f), 0.72 }, ClipX = new[] { -2.0, 2.0 } };
            });
            // JS addPre：先画一份「只有描边」的整行（无填充/阴影/渐变，字遮罩用 fnOut）
            DrawPre(env, it, (env2, x) =>
            {
                var others = env2.CharFns.Where(f => !ReferenceEquals(f, main)).ToList();
                var parts = new List<JAnim.CharFn>(others) { FnOut };
                var c = (JObject)x.DeepClone();
                c["charFn"] = null; // 函数值字段走 env 桥（DrawIt 的 charFn 形参）
                c["stroke"] = sw;
                c["strokeColor"] = JLayouts.OrStr(S(x, "strokeColor"), S(x, "color"));
                c["shadow"] = null;
                c["extrude"] = null;
                c["dash"] = null;
                c["pattern"] = null;
                c["gradient"] = null;
                c["blur"] = 0;
                DrawIt(env2, c, JAnim.CombineChar(parts));
            });
        },
    });

    // ---------------- splitJoin（JS L436） ----------------

    private static void SplitJoin() => Reg0("splitJoin", new MoveDef
    {
        Name = "上下合体",
        Tags = new[] { "graphic", "editorial" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var vert = B(it, "vertical");
            var isx = N1(it, "sx");
            var isy = N1(it, "sy");
            var dir = DirOf(env, 13);

            double Q(int i, int n) => Stg(p, OrdLR(i, n), 0.4);
            CharXf Half(JObject g, double u, int sgn)
            {
                var f = sgn * dir * 1.1 * (1 - OQuint(u));
                if (B(g, "r90")) return new CharXf { A = J.Clamp(u * 2) };
                return vert
                    ? new CharXf
                    {
                        Dy = f * JLayouts.D(g, "h") * isy,
                        ClipY = new[] { -0.62 - f, 0.62 - f },
                        ClipX = sgn < 0 ? new[] { 0.0, 2.0 } : new[] { -2.0, 0.004 },
                    }
                    : new CharXf
                    {
                        Dx = f * JLayouts.D(g, "w") * isx,
                        ClipX = new[] { -0.62 - f, 0.62 - f },
                        ClipY = sgn < 0 ? new[] { -2.0, 0.004 } : new[] { 0.0, 2.0 },
                    };
            }
            Glyphs(env, (i, g, n) =>
            {
                if (g == null) return null;
                var u = Q(i, n);
                if (u <= 0) return new CharXf { Hide = true };
                if (u >= 1) return null;
                return Half(g, u, -1);
            });
            CharXf? FnB(int i, JObject? g, int n)
            {
                if (g == null) return new CharXf { Hide = true };
                var u = Q(i, n);
                if (u <= 0 || u >= 1 || B(g, "r90")) return new CharXf { Hide = true };
                return Half(g, u, 1);
            }
            // JS addPre：另一半（sgn=+1）的副本
            DrawPre(env, it, (env2, x) =>
            {
                var c = (JObject)x.DeepClone();
                c["shadow"] = null;
                DrawIt(env2, c, FnB);
            });
        },
    });

    // ---------------- vSlice（JS L469） ----------------

    private static void VSlice() => Reg0("vSlice", new MoveDef
    {
        Name = "縦スライス",
        Tags = new[] { "graphic", "glitch" },
        W = 1,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var b = Box(it);
            var R = DRange(it, b);
            var size = N0(it, "size");
            var pad = size * 0.3;
            var X0 = R.X0 - pad;
            var X1 = R.X1 + pad;
            var n = J.Clamp((int)Math.Round((X1 - X0) / (size * 0.55)), 4, 8);
            var w = (X1 - X0) / n;
            var dist = (R.Y1 - R.Y0) * 0.9 + size * 1.4;
            var vb = new JArray();
            for (var k = 0; k < n; k++)
            {
                var e = OQuint(Stg(p, OrdC(k, n), 0.5));
                vb.Add(new JArray(X0 + k * w, X0 + (k + 1) * w + 0.6, (k % 2 != 0 ? 1 : -1) * dist * (1 - e)));
            }
            it["vbands"] = vb;
            it["alpha"] = NN(it, "alpha", 1) * J.Clamp(p * 3);
        },
    });

    // ---------------- shutter（JS L485） ----------------

    private static void Shutter() => Reg0("shutter", new MoveDef
    {
        Name = "シャッター",
        Tags = new[] { "graphic", "editorial" },
        W = 1,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var vert = B(it, "vertical");
            var size = N0(it, "size");
            var grow = OQuint(p / 0.32);
            var open = IoQuart((p - 0.18) / 0.82);
            var m = size * 0.2;
            var b = Box(it);
            // JS clipLocal：单一矩形 → 每字形 clipX/clipY（精确对位；open<=0 时全遮）
            if (open <= 0)
                Glyphs(env, (_, _, _) => new CharXf { Hide = true });
            else if (vert)
                ClipRectLocal(env, it, N1(it, "sx"), N1(it, "sy"), b.Cx - (b.W / 2 + m) * open, b.Y0 - m, b.Cx + (b.W / 2 + m) * open, b.Y1 + m);
            else
                ClipRectLocal(env, it, N1(it, "sx"), N1(it, "sy"), b.X0 - m, b.Cy - (b.H / 2 + m) * open, b.X1 + m, b.Cy + (b.H / 2 + m) * open);
            // JS postLocal（开合的 accent 边框线）——就地执行
            DrawPostLocal(env, it, (env2, bb, x, A) =>
            {
                var a = (1 - J.Smooth(0.6, 0.9, p)) * A;
                if (a <= 0) return;
                var t = Math.Max(2, size * 0.03);
                var col = env2.Sc!.Str("accent");
                if (vert)
                {
                    var L = (bb.H + m * 2) * grow;
                    var y = bb.Cy - L / 2;
                    var hw = (bb.W / 2 + m) * open;
                    env2.Rect(bb.Cx - hw - t / 2, y, t, L, col, a, false);
                    if (open > 0) env2.Rect(bb.Cx + hw - t / 2, y, t, L, col, a, false);
                }
                else
                {
                    var L = (bb.W + m * 2) * grow;
                    var x0 = bb.Cx - L / 2;
                    var hh = (bb.H / 2 + m) * open;
                    env2.Rect(x0, bb.Cy - hh - t / 2, L, t, col, a, false);
                    if (open > 0) env2.Rect(x0, bb.Cy + hh - t / 2, L, t, col, a, false);
                }
            });
        },
    });

    // ---------------- iris（JS L521） ----------------

    private static void Iris() => Reg0("iris", new MoveDef
    {
        Name = "アイリス",
        Tags = new[] { "pop", "emotional", "graphic" },
        W = 0.8,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var e = OQuart(p);
            ScaleAbout(it, J.Lerp(1.14, 1, e));
            var size = N0(it, "size");
            var radius = (Math.Sqrt(Math.Pow(Box(it).W, 2) + Math.Pow(Box(it).H, 2)) / 2 + size * 0.25) * e;
            // clipLocal 是圆形路径裁剪：任意路径裁剪管线已接（Env.ClipFn → JLayouts.DrawFx 的 ClipPath，
            // 用法见 P11Exit）；此处仍按字形中心到盒心的距离做可见性近似（遮罩边缘按字形粒度离散）。
            {
                var bx = Box(it);
                var cx = bx.Cx;
                var cy = bx.Cy;
                var isx = N1(it, "sx");
                var isy = N1(it, "sy");
                ClipHideGlyphs(env, g =>
                {
                    var gpos = GlyphPos(g, isx, isy);
                    var gx = gpos[0];
                    var gy = gpos[1];
                    var dx = gx - cx;
                    var dy = gy - cy;
                    return Math.Sqrt(dx * dx + dy * dy) <= radius ? (bool?)true : false;
                });
            }
            // JS postLocal（iris 圆环描边）——就地执行
            DrawPostLocal(env, it, (env2, b, x, A) =>
            {
                var a = (1 - J.Smooth(0.3, 0.8, p)) * A;
                if (a <= 0) return;
                var r = (Math.Sqrt(b.W * b.W + b.H * b.H) / 2 + size * 0.25) * e;
                env2.Circle(b.Cx, b.Cy, Math.Max(0.01, r), null, env2.Sc!.Str("accent"), Math.Max(2, size * 0.03), a, false);
            });
        },
    });

    // ---------------- diagWipe（JS L540） ----------------

    private static void DiagWipe() => Reg0("diagWipe", new MoveDef
    {
        Name = "斜めワイプ",
        Tags = new[] { "graphic", "editorial", "pop" },
        W = 1,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var vert = B(it, "vertical");
            var size = N0(it, "size");
            var dir = DirOf(env, 17);
            const double k = 0.5;
            var eL = OQuart(p / 0.5);
            var eT = J.E.InOutCubic((p - 0.18) / 0.74);

            (double Uc, double Vc, double Hv, double L, double T) Geo(EBox b)
            {
                var U0 = vert ? b.Y0 : b.X0;
                var U1 = vert ? b.Y1 : b.X1;
                var V0 = vert ? b.X0 : b.Y0;
                var V1 = vert ? b.X1 : b.Y1;
                var m = size * 0.16;
                var hv = (V1 - V0) / 2 + m;
                var half = (U1 - U0) / 2 + m + k * hv;
                return ((U0 + U1) / 2, (V0 + V1) / 2, hv, J.Lerp(-half, half, eL), J.Lerp(-half, half, eT));
            }
            double[] Pt(double uc, double vc, double du, double dv)
            {
                var u = uc + dir * du;
                var v = vc + dv;
                return vert ? new[] { v, u } : new[] { u, v };
            }
            double[][] Para(double uc, double vc, double hv, double a0, double a1) =>
                new[]
                {
                    Pt(uc, vc, a0 - k * hv, -hv), Pt(uc, vc, a1 - k * hv, -hv),
                    Pt(uc, vc, a1 + k * hv, hv), Pt(uc, vc, a0 + k * hv, hv),
                };
            // clipLocal 是斜平行四边形：任意路径裁剪管线已接（Env.ClipFn → DrawFx ClipPath）；
            // 此处仍按字形中心沿阅读方向近似（du ≤ T 时可见；斜边按字形粒度离散）。
            {
                var bx = Box(it);
                var G = Geo(bx);
                var isx = N1(it, "sx");
                var isy = N1(it, "sy");
                ClipHideGlyphs(env, g =>
                {
                    var gpos = GlyphPos(g, isx, isy);
                    var gx = gpos[0];
                    var gy = gpos[1];
                    var u = vert ? gy : gx;
                    return (u - G.Uc) * dir <= G.T ? (bool?)true : false;
                });
            }
            // JS postLocal（accent 色版扫入）——就地执行
            DrawPostLocal(env, it, (env2, b, x, A) =>
            {
                var G = Geo(b);
                if (G.L - G.T < 0.5) return;
                env2.Poly(Para(G.Uc, G.Vc, G.Hv, G.T, G.L), env2.Sc!.Str("accent"), A, false);
            });
        },
    });

    // ---------------- blinds（JS L578） ----------------

    private static void Blinds() => Reg0("blinds", new MoveDef
    {
        Name = "ブラインド",
        Tags = new[] { "graphic", "editorial" },
        W = 0.8,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var vert = B(it, "vertical");
            var size = N0(it, "size");
            var b = Box(it);
            // clipLocal 是百叶条纹的并集：任意路径裁剪管线已接（Env.ClipFn → DrawFx ClipPath）；
            // 此处仍按「字形所在的条纹开了没有」做可见性近似（条纹宽度变化按字形粒度离散）。
            {
                var m = size * 0.2;
                var V0 = (vert ? b.X0 : b.Y0) - m;
                var V1 = (vert ? b.X1 : b.Y1) + m;
                var pitch = Math.Max(size * 0.2, (V1 - V0) / 40);
                var nN = Math.Max(1, (int)Math.Ceiling((V1 - V0) / pitch));
                var isx = N1(it, "sx");
                var isy = N1(it, "sy");
                ClipHideGlyphs(env, g =>
                {
                    var gpos = GlyphPos(g, isx, isy);
                    var gx = gpos[0];
                    var gy = gpos[1];
                    var cv = vert ? gx : gy;
                    var kk = J.Clamp((int)((cv - V0) / pitch), 0, nN - 1);
                    var e = OQuart(Stg(p, OrdLR(vert ? nN - 1 - kk : kk, nN), 0.55));
                    return e >= 0.98 ? (bool?)true : false;
                });
            }
        },
    });

    // ---------------- checker（JS L600） ----------------

    private static void Checker() => Reg0("checker", new MoveDef
    {
        Name = "市松",
        Tags = new[] { "graphic", "glitch", "pop" },
        W = 0.7,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var size = N0(it, "size");
            var seed = unchecked((int)env.Cut!.Seed);

            // tiles：fn(cx, cy, cell, u)；可揭示的棋盘格
            void Tiles(EBox bb, Action<double, double, double, double> fn)
            {
                var m = size * 0.15;
                var W = bb.W + m * 2;
                var H = bb.H + m * 2;
                var cell = size * 0.34;
                while (W / cell * (H / cell) > 320) cell *= 1.25;
                var nx = (int)Math.Ceiling(W / cell);
                var ny = (int)Math.Ceiling(H / cell);
                var ox = bb.Cx - nx * cell / 2;
                var oy = bb.Cy - ny * cell / 2;
                for (var iy = 0; iy < ny; iy++)
                    for (var ix = 0; ix < nx; ix++)
                    {
                        var d = ((ix + iy) & 1) * 0.25 + OrdLR(ix, nx) * 0.3 + J.R(seed, ix, iy, 7) * 0.08;
                        var u = J.Clamp((p - d) / 0.22);
                        if (u > 0) fn(ox + (ix + 0.5) * cell, oy + (iy + 0.5) * cell, cell, u);
                    }
            }
            // clipLocal 是棋盘格并集：任意路径裁剪管线已接（Env.ClipFn → DrawFx ClipPath）；
            // 此处选择不裁剪主文字（色块动画保留）。
            // JS postLocal（accent 色块收尾）——就地执行
            DrawPostLocal(env, it, (env2, bb, x, A) =>
            {
                Tiles(bb, (cx, cy, cell, u) =>
                {
                    if (u >= 0.75) return;
                    var hs = cell * 0.5 * (1 - J.E.InCubic(u / 0.75)) * Math.Min(1, u * 6);
                    if (hs > 0.5) env2.Rect(cx - hs, cy - hs, hs * 2, hs * 2, env2.Sc!.Str("accent"), 0.9 * A, false);
                });
            });
        },
    });

    // ---------------- randomOrder（JS L635） ----------------

    private static void RandomOrder() => Reg0("randomOrder", new MoveDef
    {
        Name = "ランダム順",
        Tags = new[] { "pop", "glitch" },
        W = 1,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var sc = env.Sc!;
            var seed = SeedOf(it);
            var fl = S(it, "color") == sc.Str("accent")
                ? Pick(sc.Str("accent2"), sc.Str("fg"))
                : Pick(sc.Str("accent"), sc.Str("fg"));
            Glyphs(env, (i, _, _) =>
            {
                var d = J.R(seed, i, 61) * 0.62;
                var u = J.Clamp((p - d) / 0.38);
                if (u <= 0) return new CharXf { Hide = true };
                if (u >= 1) return null;
                var o = new CharXf { S = J.Lerp(1.45, 1, OQuint(u)), A = J.Clamp(u * 6) };
                if (u < 0.42) o.Color = fl;
                return o;
            });
        },
    });

    // ---------------- bounceBig（JS L652） ----------------

    private static void BounceBig() => Reg0("bounceBig", new MoveDef
    {
        Name = "大ジャンプ",
        Tags = new[] { "pop" },
        W = 0.7,
        MinDur = 0.7,
        InDur = (dur, _) => J.Clamp(dur * 0.5, 0.2, 0.95),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var isy = N1(it, "sy");
            var seed = SeedOf(it);
            const double PI = Math.PI;
            Glyphs(env, (i, g, n) =>
            {
                if (g == null) return null;
                var q = Stg(p, OrdLR(i, n), 0.35);
                if (q <= 0) return new CharXf { Hide = true };
                if (q >= 1) return null;
                double y, rot = 0;
                if (q < 0.5)
                {
                    var s = q / 0.5;
                    y = 7.666 * s * s - 9.266 * s + 1.6;
                    rot = J.Rs(seed, i, 21) * 28 * Math.Sin(PI * s);
                }
                else if (q < 0.76)
                {
                    var s = (q - 0.5) / 0.26;
                    y = -0.24 * 4 * s * (1 - s);
                }
                else if (q < 0.9)
                {
                    var s = (q - 0.76) / 0.14;
                    y = -0.06 * 4 * s * (1 - s);
                }
                else y = 0;
                var imp = Math.Exp(-Math.Pow((q - 0.5) / 0.035, 2)) + 0.6 * Math.Exp(-Math.Pow((q - 0.76) / 0.03, 2));
                var sy = 1 - 0.3 * imp;
                var sx = 1 + 0.24 * imp;
                var h = JLayouts.D(g, "h") * isy;
                return new CharXf
                {
                    Dy = y * h + (1 - sy) * h / 2,
                    Sy = sy,
                    Sx = sx,
                    Rot = rot,
                    A = J.Clamp(q * 7),
                };
            });
        },
    });

    // ---------------- squashDrop（JS L680） ----------------

    private static void SquashDrop() => Reg0("squashDrop", new MoveDef
    {
        Name = "潰れて着地",
        Tags = new[] { "pop" },
        W = 0.8,
        MinDur = 0.5,
        InDur = (dur, _) => J.Clamp(dur * 0.42, 0.15, 0.75),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var isy = N1(it, "sy");
            Glyphs(env, (i, g, n) =>
            {
                if (g == null) return null;
                var q = Stg(p, OrdLR(i, n), 0.4);
                if (q <= 0) return new CharXf { Hide = true };
                if (q >= 1) return null;
                var h = JLayouts.D(g, "h") * isy;
                const double tA = 0.22;
                double dy = 0, sy;
                if (q < tA)
                {
                    var t = q / tA;
                    dy = -(1 - t * t) * h * 1.7;
                    sy = 1 + 0.4 * t;
                }
                else
                {
                    var t = (q - tA) / (1 - tA);
                    sy = 1 - 0.58 * Math.Exp(-3 * t) * Math.Cos(t * 6.2) * (1 - t);
                }
                var sx = 1 / Math.Pow(sy, 0.8);
                return new CharXf { Dy = dy + (1 - sy) * h / 2, Sy = sy, Sx = sx, A = J.Clamp(q * 10) };
            });
        },
    });

    // ---------------- rubber（JS L705） ----------------

    private static void Rubber() => Reg0("rubber", new MoveDef
    {
        Name = "ゴム伸び",
        Tags = new[] { "pop" },
        W = 0.8,
        MinDur = 0.45,
        InDur = (dur, _) => J.Clamp(dur * 0.42, 0.15, 0.75),
        Draw = env =>
        {
            var p = env.Ap;
            const double PI = Math.PI;
            Glyphs(env, (i, _, n) =>
            {
                var q = Stg(p, OrdLR(i, n), 0.4);
                if (q <= 0) return new CharXf { Hide = true };
                if (q >= 1) return null;
                var f = Math.Pow(1 - q, 2) * Math.Cos(q * 3.5 * PI);
                var sx = Math.Max(0.03, 1 - f);
                return new CharXf
                {
                    Sx = sx,
                    Sy = J.Clamp(1 + (1 - sx) * 0.45, 0.72, 1.45),
                    A = J.Clamp(q * 6),
                };
            });
        },
    });

    // ---------------- glitchIn（JS L720） ----------------

    private static void GlitchIn() => Reg0("glitchIn", new MoveDef
    {
        Name = "グリッチ出現",
        Tags = new[] { "glitch" },
        W = 1.2,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var sc = env.Sc!;
            var seed = SeedOf(it);
            var step = (int)env.Step;
            var size = N0(it, "size");
            var cols = new[] { sc.Str("accent"), sc.Str("ghostA"), sc.Str("ghostB"), sc.Str("accent2") }
                .Where(IsHex).Select(c => c!).ToList();
            Glyphs(env, (i, _, _) =>
            {
                var settle = 0.38 + 0.52 * J.R(seed, i, 71);
                if (p >= settle) return null;
                var u = p / settle;
                if (J.R(seed, i, step, 72) > 0.25 * J.Clamp(p * 12) + 0.75 * u) return new CharXf { Hide = true };
                var k = 1 - u;
                var o = new CharXf
                {
                    Dx = J.Rs(seed, i, step, 73) * size * 0.5 * k,
                    Dy = J.Rs(seed, i, step, 74) * size * 0.14 * k,
                };
                if (cols.Count > 0 && J.R(seed, i, step, 75) < 0.55)
                    o.Color = cols[(int)(J.H(seed, i, step, 76) % (uint)cols.Count)];
                if (J.R(seed, i, step, 77) < 0.35)
                    o.Sx = J.Lerp(0.55, 1.9, J.R(seed, i, step, 78));
                return o;
            });
            var k2 = Math.Pow(1 - p, 1.2);
            if (p < 0.85 && J.R(seed, step, 79) < 0.6)
                it["bands"] = JAnim.JArr(HBands(it, 5, b =>
                    J.R(seed, step, b, 80) < 0.7 ? J.Rs(seed, step, b, 81) * size * 0.6 * k2 : 0));
        },
    });

    // ---------------- echoIn（JS L744） ----------------

    private static void EchoIn() => Reg0("echoIn", new MoveDef
    {
        Name = "残像集束",
        Tags = new[] { "graphic", "emotional", "pop" },
        W = 1,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var col = Pick(env.Sc!.Str("accent"), env.Sc.Str("fg"));

            double Qf(int i, int n) => Stg(p, OrdLR(i, n), 0.35);
            Glyphs(env, (i, _, n) =>
            {
                var q = Qf(i, n);
                if (q <= 0) return new CharXf { Hide = true };
                if (q >= 1) return null;
                return new CharXf { A = J.Clamp(q * 2.2), S = J.Lerp(0.88, 1, OQuart(q)) };
            });
            // JS addPre：3→1 圈同心 accent 描边向字收拢（每圈一份描边副本）
            DrawPre(env, it, (env2, x) =>
            {
                for (var m = 3; m >= 1; m--)
                {
                    var mm = m;
                    CharXf? Fn(int i, JObject? _, int n)
                    {
                        var q = Qf(i, n);
                        if (q <= 0 || q >= 1) return new CharXf { Hide = true };
                        var k = 1 - OQuart(q);
                        return new CharXf
                        {
                            Outline = true,
                            S = 1 + mm * 0.3 * k,
                            A = (0.95 - mm * 0.2) * Math.Min(1, k * 1.8) * J.Clamp(q * 6),
                        };
                    }
                    var c = (JObject)x.DeepClone();
                    c["fill"] = false;
                    c["stroke"] = Math.Max(1.4, N0(x, "size") * 0.02);
                    c["strokeColor"] = col;
                    c["color"] = col;
                    c["shadow"] = null;
                    c["extrude"] = null;
                    c["pattern"] = null;
                    c["gradient"] = null;
                    c["dash"] = null;
                    DrawIt(env2, c, Fn);
                }
            });
        },
    });

    // ---------------- whip（JS L787） ----------------

    private static void Whip() => Reg0("whip", new MoveDef
    {
        Name = "ホイップ",
        Tags = new[] { "pop", "graphic" },
        W = 1,
        InDur = (dur, _) => J.Clamp(dur * 0.3, 0.12, 0.45),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            const double PI = Math.PI;
            var dir = DirOf(env, 19);
            var b = Box(it);
            var dd = Math.Max(N0(it, "size") * 4.5, b.W * 0.9);
            var e = J.E.OutExpo(p);
            it["x"] = N0(it, "x") + dir * dd * (1 - e);
            var lean = dir * 34 * Math.Pow(1 - e, 0.7);
            var follow = -dir * 14 * Math.Sin(PI * J.Clamp((p - 0.3) / 0.7)) * (1 - J.Smooth(0.6, 1, p));
            it["skew"] = N0(it, "skew") + lean + follow;
            it["streak"] = new JObject { ["n"] = 6, ["dx"] = dir * N0(it, "size") * 0.42 * (1 - e), ["dy"] = 0, ["a"] = 0.5 * (1 - e) };
            it["alpha"] = NN(it, "alpha", 1) * J.Clamp(p * 8);
        },
    });

    // ---------------- skewIn（JS L803） ----------------

    private static void SkewIn() => Reg0("skewIn", new MoveDef
    {
        Name = "スキュー",
        Tags = new[] { "editorial", "graphic" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var dir = DirOf(env, 23);
            var isy = N1(it, "sy");
            var size = N0(it, "size");
            Glyphs(env, (i, g, n) =>
            {
                if (g == null) return null;
                var q = Stg(p, OrdLR(i, n), 0.45);
                if (q <= 0) return new CharXf { Hide = true };
                if (q >= 1) return null;
                var sk = J.Clamp(dir * 60 * (1 - OBack(q, 1.6)), -66, 66);
                return new CharXf
                {
                    Skew = sk,
                    Dx = -Math.Tan(sk * J.DEG) * JLayouts.D(g, "h") * isy / 2 + dir * (1 - OQuint(q)) * size * 0.7,
                    A = J.Clamp(q * 3),
                };
            });
        },
    });

    // ---------------- trackIn（JS L821） ----------------

    private static void TrackIn() => Reg0("trackIn", new MoveDef
    {
        Name = "字間収束",
        Tags = new[] { "calm", "editorial", "emotional" },
        W = 1.2,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var e = OQuint(p);
            if (GlyphN(it) <= 1)
                it["x"] = env.W / 2.0 + (N0(it, "x") - env.W / 2.0) * (1 + 0.9 * (1 - e));
            else
                SetTrack(it, N0(it, "track") + 1.6 * (1 - e));
            it["alpha"] = NN(it, "alpha", 1) * J.E.OutCubic(J.Clamp(p * 1.7));
        },
    });

    // ---------------- trackOut（JS L831） ----------------

    private static void TrackOut() => Reg0("trackOut", new MoveDef
    {
        Name = "字間拡張",
        Tags = new[] { "calm", "editorial", "pop" },
        W = 1,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var e = OQuint(p);
            if (GlyphN(it) <= 1)
            {
                it["x"] = J.Lerp(env.W / 2.0, N0(it, "x"), e);
                it["y"] = J.Lerp(env.H / 2.0, N0(it, "y"), e);
            }
            else SetTrack(it, N0(it, "track") - 0.72 * (1 - e));
            it["alpha"] = NN(it, "alpha", 1) * J.Clamp(p * 2.5);
            Glyphs(env, (_, _, _) => new CharXf { A = J.Lerp(0.55, 1, e) });
        },
    });

    // ---------------- blurStagger（JS L844） ----------------

    private static void BlurStagger() => Reg0("blurStagger", new MoveDef
    {
        Name = "ブラー段差",
        Tags = new[] { "calm", "emotional" },
        W = 1.2,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var size = N0(it, "size");
            var bl = Math.Min(42, size * 0.14);
            Glyphs(env, (i, _, n) =>
            {
                var q = Stg(p, OrdLR(i, n), 0.55);
                if (q <= 0) return new CharXf { Hide = true };
                if (q >= 1) return null;
                var e = J.E.OutCubic(q);
                return new CharXf
                {
                    Blur = bl * (1 - e),
                    A = Math.Pow(e, 0.6),
                    S = J.Lerp(1.35, 1, e),
                    Dy = -(1 - e) * size * 0.16,
                };
            });
        },
    });

    // ---------------- fadeStagger（JS L858） ----------------

    private static void FadeStagger() => Reg0("fadeStagger", new MoveDef
    {
        Name = "字ごとフェード",
        Tags = new[] { "calm", "emotional" },
        W = 1.2,
        InDur = (dur, _) => J.Clamp(dur * 0.45, 0.15, 0.85),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var size = N0(it, "size");
            Glyphs(env, (i, _, n) =>
            {
                var q = Stg(p, OrdLR(i, n), 0.62);
                if (q <= 0) return new CharXf { Hide = true };
                if (q >= 1) return null;
                return new CharXf { A = J.E.InOutSine(q), Dy = (1 - J.E.OutCubic(q)) * size * 0.07 };
            });
        },
    });

    // ---------------- waveIn（JS L872） ----------------

    private static void WaveIn() => Reg0("waveIn", new MoveDef
    {
        Name = "波立ち",
        Tags = new[] { "pop", "emotional" },
        W = 0.9,
        MinDur = 0.5,
        InDur = (dur, _) => J.Clamp(dur * 0.45, 0.15, 0.85),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var size = N0(it, "size");
            var damp = Math.Pow(1 - p, 1.6);
            const double PI = Math.PI;
            Glyphs(env, (i, _, n) =>
            {
                var a = J.Clamp(p * 2.4 - OrdLR(i, n) * 1.3);
                if (a <= 0) return new CharXf { Hide = true };
                var ph = p * 3.6 * PI - i * 0.8;
                return new CharXf
                {
                    Dy = -Math.Sin(ph) * size * 0.66 * damp,
                    Rot = Math.Cos(ph) * 13 * damp,
                    A = a,
                };
            });
        },
    });

    // ---------------- spiralIn（JS L886） ----------------

    private static void SpiralIn() => Reg0("spiralIn", new MoveDef
    {
        Name = "螺旋集合",
        Tags = new[] { "pop", "emotional" },
        W = 0.7,
        MinDur = 0.5,
        InDur = (dur, _) => J.Clamp(dur * 0.42, 0.15, 0.75),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var b = Box(it);
            var isx = N1(it, "sx");
            var isy = N1(it, "sy");
            var size = N0(it, "size");
            var dir = DirOf(env, 29);
            var seed = unchecked((int)env.Cut!.Seed);
            Glyphs(env, (i, g, n) =>
            {
                if (g == null) return null;
                var q = Stg(p, OrdLR(i, n), 0.15);
                if (q <= 0) return new CharXf { Hide = true };
                if (q >= 1) return null;
                var e = J.E.OutCubic(q);
                var k = 1 - e;
                var __gp1 = GlyphPos(g, isx, isy);
                var gx = __gp1[0];
                var gy = __gp1[1];
                var px = gx - b.Cx;
                var py = gy - b.Cy;
                var ang = dir * k * 200 * J.DEG;
                var c = Math.Cos(ang);
                var s = Math.Sin(ang);
                var r = 1 + 0.7 * k;
                var th = J.R(seed, i, 31) * J.TAU + ang;
                var orb = size * 1.0 * k;
                var nx = (c * px - s * py) * r + Math.Cos(th) * orb;
                var ny = (s * px + c * py) * r + Math.Sin(th) * orb;
                return new CharXf
                {
                    Dx = nx - px,
                    Dy = ny - py,
                    Rot = dir * k * 320,
                    S = J.Lerp(0.45, 1, e),
                    A = J.Clamp(q * 5),
                };
            });
        },
    });

    // ---------------- zoomOut（JS L907） ----------------

    private static void ZoomOut() => Reg0("zoomOut", new MoveDef
    {
        Name = "巨大→等倍",
        Tags = new[] { "pop", "graphic", "glitch" },
        W = 1,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            const double PI = Math.PI;
            var e = J.E.OutExpo(p);
            var dip = 0.035 * Math.Sin(PI * J.Clamp((p - 0.22) / 0.58));
            ScaleAbout(it, 1 + 3.4 * (1 - e) - dip);
            it["alpha"] = NN(it, "alpha", 1) * J.Clamp(p * 6);
        },
    });

    // ---------------- resolve（JS L917） ----------------

    private static void Resolve() => Reg0("resolve", new MoveDef
    {
        Name = "解読",
        Tags = new[] { "glitch", "editorial" },
        W = 0.9,
        MinDur = 0.6,
        InDur = (dur, n) => J.Clamp(0.2 + n * 0.045, 0.3, Math.Max(0.3, Math.Min(0.95, dur * 0.55))),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var sc = env.Sc!;
            var seed = SeedOf(it);
            var step = (int)env.Step;
            var isx = N1(it, "sx");
            var isy = N1(it, "sy");
            var lay = JText.LayoutText(it);
            var nN = lay.N;
            var f = p * (nN + 1.8);
            var front = (int)Math.Floor(f);
            var hi = Pick(sc.Str("accent"), sc.Str("fg"));
            var dim = Pick(sc.Str("sub"), sc.Str("fg"));
            if (p < 0.03)
            {
                Glyphs(env, (_, _, _) => new CharXf { Hide = true });
                return;
            }
            Glyphs(env, (i, _, _) =>
            {
                if (i < front) return f - i - 1 < 0.6 ? new CharXf { Color = hi } : null;
                if (i == front) return new CharXf { Hide = true };
                if (i <= front + 4)
                {
                    var pool = JLang.Pool("signs") ?? "";
                    return new CharXf
                    {
                        Ch = pool[(int)(J.H(seed, i, step, 51) % (uint)pool.Length)].ToString(),
                        A = 0.5,
                        Color = dim,
                    };
                }
                return new CharXf { Hide = true };
            });
            if (front < nN)
            {
                var gc = lay.Cells[front];
                // JS postLocal（解読光标块）——就地执行
                DrawPostLocal(env, it, (env2, _, x, A) =>
                {
                    var __gp2 = GlyphPos(gc, isx, isy);
                    var gx = __gp2[0];
                    var gy = __gp2[1];
                    var w = gc.W * isx * 0.9;
                    var h = gc.H * isy * 0.98;
                    env2.Rect(gx - w / 2, gy - h / 2, w, h, sc.Str("accent"), A, false);
                });
            }
        },
    });

    // ---------------- magnet（JS L949） ----------------

    private static void Magnet() => Reg0("magnet", new MoveDef
    {
        Name = "磁石",
        Tags = new[] { "pop", "graphic" },
        W = 0.8,
        MinDur = 0.6,
        InDur = (dur, _) => J.Clamp(dur * 0.48, 0.2, 0.85),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var seed = SeedOf(it);
            var size = N0(it, "size");
            Glyphs(env, (i, _, n) =>
            {
                var d = n > 1 ? J.R(seed, i, 81) * 0.3 : 0;
                var u = J.Clamp((p - d) / 0.7);
                if (u <= 0) return new CharXf { Hide = true };
                if (u >= 1) return null;
                var ang = J.R(seed, i, 82) * J.TAU;
                var dist = size * (1.3 + 1.5 * J.R(seed, i, 83));
                var rot0 = J.Rs(seed, i, 84) * 55;
                double f, a;
                if (u < 0.36)
                {
                    f = 1 - 0.16 * J.E.OutCubic(u / 0.36);
                    a = 0.5 * J.Clamp(u * 8);
                }
                else if (u < 0.6)
                {
                    var t = (u - 0.36) / 0.24;
                    f = 0.84 * (1 - J.E.InCubic(t));
                    a = J.Lerp(0.5, 1, J.Clamp(t * 3));
                }
                else
                {
                    var t = (u - 0.6) / 0.4;
                    f = -0.1 * Math.Sin(t * J.TAU) * (1 - t);
                    a = 1;
                }
                var s = 1 + 0.14 * Math.Exp(-Math.Pow((u - 0.6) / 0.05, 2));
                return new CharXf
                {
                    Dx = Math.Cos(ang) * dist * f,
                    Dy = Math.Sin(ang) * dist * f,
                    Rot = rot0 * f,
                    S = s,
                    A = a,
                };
            });
        },
    });

    // ---------------- inkBleed（JS L980） ----------------

    private static void InkBleed() => Reg0("inkBleed", new MoveDef
    {
        Name = "にじみ",
        Tags = new[] { "calm", "emotional" },
        W = 1,
        InDur = (dur, _) => J.Clamp(dur * 0.45, 0.15, 0.8),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var seed = SeedOf(it);
            var size = N0(it, "size");
            var bl = Math.Min(36, size * 0.16);
            Glyphs(env, (i, _, n) =>
            {
                var d = n > 1 ? J.R(seed, i, 91) * 0.45 : 0;
                var u = J.Clamp((p - d) / 0.55);
                if (u <= 0) return new CharXf { Hide = true };
                if (u >= 1) return null;
                var e = J.E.OutCubic(u);
                return new CharXf
                {
                    S = J.Lerp(0.5, 1, OBack(u, 1.1)),
                    Blur = bl * Math.Pow(1 - e, 1.3),
                    A = J.Clamp(u * 3.2),
                };
            });
            var h = 1 - J.Smooth(0.2, 0.88, p);
            if (it["shadow"] == null && h > 0.02)
                it["shadow"] = new JObject
                {
                    ["color"] = Rgba(ColOf(it), 0.55 * h),
                    ["blur"] = size * 0.3 * h,
                    ["dx"] = 0,
                    ["dy"] = 0,
                };
        },
    });

    // ---------------- neonOn（JS L1003） ----------------

    private static void NeonOn() => Reg0("neonOn", new MoveDef
    {
        Name = "ネオン点灯",
        Tags = new[] { "glitch", "emotional", "pop" },
        W = 0.8,
        MinDur = 0.6,
        InDur = (dur, _) => J.Clamp(dur * 0.5, 0.2, 0.9),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var seed = SeedOf(it);
            var step = (int)env.Step;
            Glyphs(env, (i, _, _) =>
            {
                var ig = 0.3 + 0.45 * J.R(seed, i, 101);
                if (p >= ig + 0.12) return null;
                if (p < ig - 0.22) return new CharXf { A = 0.1 * J.Clamp(p * 8) };
                var on = J.R(seed, i, step, 102) < 0.25 + 0.75 * J.Clamp((p - ig + 0.22) / 0.34);
                return on ? null : new CharXf { A = 0.1 };
            });
            var glow = J.Smooth(0.1, 0.4, p) * (1 - J.Smooth(0.62, 0.92, p));
            var gc = J.Lum(ColOf(it)) > 0.35 ? ColOf(it) : Pick(env.Sc!.Str("accent"), ColOf(it));
            if (glow > 0.01 && it["shadow"] == null)
                it["shadow"] = new JObject
                {
                    ["color"] = Rgba(gc, 0.95),
                    ["blur"] = Math.Min(80, N0(it, "size") * 0.32) * glow,
                    ["dx"] = 0,
                    ["dy"] = 0,
                };
        },
    });

    // ---------------- cursorSweep（JS L1026） ----------------

    private static void CursorSweep() => Reg0("cursorSweep", new MoveDef
    {
        Name = "カーソル掃引",
        Tags = new[] { "editorial", "graphic" },
        W = 1,
        InDur = (dur, _) => J.Clamp(dur * 0.4, 0.14, 0.7),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var vert = B(it, "vertical");
            var size = N0(it, "size");
            var isx = N1(it, "sx");
            var isy = N1(it, "sy");
            var b0 = Box(it);
            var bw = size * 0.28 * (vert ? isy : isx);
            var U0 = (vert ? b0.Y0 : b0.X0) - bw;
            var U1 = (vert ? b0.Y1 : b0.X1) + bw * 1.5;
            var pos = J.Lerp(U0, U1, J.E.OutQuad(J.Clamp(p / 0.85)));
            Glyphs(env, (i, g, _) =>
            {
                if (g == null) return null;
                var __gp3 = GlyphPos(g, isx, isy);
                var gx = __gp3[0];
                var gy = __gp3[1];
                var gu = vert ? gy : gx;
                var hs = (vert ? JLayouts.D(g, "h") * isy : JLayouts.D(g, "w") * isx) / 2;
                var u = J.Clamp((pos - bw / 2 - (gu - hs)) / (hs * 2 + size * 0.2));
                if (u <= 0) return new CharXf { Hide = true };
                if (u >= 1) return null;
                var e = OQuint(u);
                var o = new CharXf { A = J.Clamp(u * 3), S = J.Lerp(0.7, 1, e) };
                if (vert) o.Dy = (1 - e) * size * 0.3;
                else o.Dx = (1 - e) * size * 0.3;
                return o;
            });
            // JS postLocal（扫过的 accent 光标条）——就地执行
            DrawPostLocal(env, it, (env2, b, x, A) =>
            {
                var t = bw * (1 - J.Smooth(0.78, 0.94, p));
                if (t < 0.5) return;
                var m = size * 0.15;
                if (vert) env2.Rect(b.X0 - m, pos - t / 2, b.W + m * 2, t, env2.Sc!.Str("accent"), A, false);
                else env2.Rect(pos - t / 2, b.Y0 - m, t, b.H + m * 2, env2.Sc!.Str("accent"), A, false);
            });
        },
    });

    // ---------------- stamp（JS L1054） ----------------

    private static void Stamp() => Reg0("stamp", new MoveDef
    {
        Name = "スタンプ",
        Tags = new[] { "pop", "graphic" },
        W = 0.9,
        InDur = (dur, _) => J.Clamp(dur * 0.4, 0.14, 0.7),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            const double tI = 0.42;
            const double TAU = J.TAU;
            var dir = DirOf(env, 37);
            var b = Box(it);
            var size = N0(it, "size");
            if (p < tI)
            {
                var t = J.E.InQuad(p / tI);
                ScaleAbout(it, J.Lerp(2.3, 1, t), b);
                it["rot"] = N0(it, "rot") + dir * -22 * (1 - t);
                it["alpha"] = NN(it, "alpha", 1) * J.Clamp(p / tI * 3);
            }
            else
            {
                var t = (p - tI) / (1 - tI);
                var dec = Math.Pow(1 - t, 2);
                it["x"] = N0(it, "x") + J.Rs(unchecked((int)env.Cut!.Seed), (int)env.Step, 1) * size * 0.05 * dec;
                it["y"] = N0(it, "y") + J.Rs(unchecked((int)env.Cut!.Seed), (int)env.Step, 2) * size * 0.03 * dec;
                ScaleAbout(it, 1 - 0.05 * Math.Exp(-t * 9) * (1 - t), b);
                var lt = J.Clamp(t / 0.7);
                if (lt < 1)
                    // JS postLocal（印章冲击线）——就地执行
                    DrawPostLocal(env, it, (env2, bb, x, A) =>
                    {
                        var m = size * 0.28;
                        var rx = bb.W / 2 + m;
                        var ry = bb.H / 2 + m;
                        var e = OQuart(lt);
                        var a = (1 - lt) * A;
                        var L = size * 0.42;
                        var lw = Math.Max(2, size * 0.05);
                        var nN = GlyphN(x) <= 1 ? 6 : 12;
                        for (var k = 0; k < nN; k++)
                        {
                            var an = k / (double)nN * TAU + 0.26;
                            var c = Math.Cos(an);
                            var s = Math.Sin(an);
                            var r0 = 1 + 0.08 * e;
                            var x0 = bb.Cx + c * rx * r0 + c * L * 0.6 * e;
                            var y0 = bb.Cy + s * ry * r0 + s * L * 0.6 * e;
                            env2.Line(new[]
                                {
                                    new[] { x0, y0 },
                                    new[] { x0 + c * L * (1 - e * 0.6), y0 + s * L * (1 - e * 0.6) },
                                }, env2.Sc!.Str("accent"), lw, a, false);
                        }
                    });
            }
        },
    });
}
