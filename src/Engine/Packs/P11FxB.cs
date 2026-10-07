// 移植自 frontend/src/engine/11p_fxB.js（fx 表现包 B，pack = 'fxB'，34 条：
// 镜头光学 / glitch / 印刷·胶片质感 / 漫画系遮罩）。
//
// fx 插件路径：FxDef.Draw = (env, ev)（Registry 冻结，装不下 JS 的 draw(ctx, ev, k, I)）——
// k 与 I.cw/ch/S/tmp/tmp2/transparent 由 Renderer.Post 的 FXE 插件段装配进 FxPlug 桥（文件底部），
// I.sc/st/step/allowFilter 直接取 env.Sc/env.St/env.Step/env.AllowFilter。渲染器接入点：
// Renderer.Post 的事件循环里「非内置（!Builtin && Draw != null）→ FxBegin → Draw → finally FxEnd」。
//
// Canvas2D → SkiaSharp 对位（同 Engine 约定）：
//   globalCompositeOperation → 每 paint 的 BlendMode（screen/multiply/difference/saturation/
//   lighten/darken/overlay/color-burn/lighter/destination-in/destination-over）；
//   ctx.filter='blur(px)' → JText.BlurFilter(px)；contrast()/hue-rotate()/saturate() → 4x5 颜色矩阵
//   （CSS filter 规范矩阵，本文件私有实现）；
//   createRadial/LinearGradient + fill → SKShader + DrawPath/DrawRect；
//   createPattern(setTransform) + fillRect → SKShader.CreateBitmap(Repeat, Repeat, localMatrix)；
//   imageSmoothingEnabled=false → SKFilterMode.Nearest 采样。
//
// 已知降级（不改共享文件）：
// 1. 元数据 scratch/ae 无 FxDef 字段：渲染器插件段对全部非内置 fx 恒拷 scratch 帧（S），
//    是否读 S 由各 Draw 自身决定（JS 无 scratch 标记的条目不读 S，行为一致）；
//    ae（AE 导出映射标签）丢弃——TODO(AE 导出对位，无消费者)。
// 2. buf() 模块级离屏画布 → [ThreadStatic] SKSurface 组（同 JS：按索引复用、尺寸变化重建）。
// 3. DOT/ROWS/BAYER/STATIC 纹理缓存 → 静态缓存（确定性生成，跨 renderer 共享只读安全）。

using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace JizuraLive.Engine;

/// <summary>
/// Renderer.Post 的 FXE 插件段 ↔ 表现包 Draw 的一次性信息桥（JS 的 I 对位）。
/// FxDef.Draw(Env, PlanEvent) 装不下 (k, I)：每次插件调用前由渲染器 FxBegin 装配、finally FxEnd 清空。
/// 静态字段＝JS 的模块级单例语义；Renderer 线程不安全（每画布/每导出管线一个），与 JS 同约束。
/// </summary>
internal static class FxPlug
{
    /// <summary>事件进度 0..1（渲染器已按 JS clamp）。</summary>
    internal static double K;

    /// <summary>设备像素帧尺寸（JS 的 I.cw/I.ch）。</summary>
    internal static int Cw, Ch;

    /// <summary>特效前整帧（JS 的 I.S；渲染器 scratch 面，fx 内只读不写，FxEnd 由渲染器清桥）。</summary>
    internal static SKSurface? S;

    /// <summary>JS 的 I.tmp / I.tmp2：转场临时画布（渲染器的复用面工厂）。</summary>
    internal static Func<int, int, SKSurface>? Tmp;
    internal static Func<int, int, SKSurface>? Tmp2;

    /// <summary>JS 的 I.opt.transparent。</summary>
    internal static bool Transparent;
}

public static class P11FxB
{
    private const string Pk = "fxB";
    private static int _installed;

    /// <summary>幂等注册本文件全部条目（顺序 = JS fx() 出现序）。</summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;
        RegisterRadialChroma();   // JS L112
        RegisterBloomFlash();     // L139
        RegisterBulge();          // L188
        RegisterPixelSort();      // L224
        RegisterInterlace();      // L274
        RegisterMacroBlock();     // L308
        RegisterHalftone();       // L381
        RegisterDuotone();        // L404
        RegisterDitherBit();      // L453
        RegisterRotateSnap();     // L496
        RegisterEchoFrames();     // L530
        RegisterKaleido();        // L556
        RegisterBandInvert();     // L591
        RegisterLightRays();      // L608
        RegisterAnamorphic();     // L641
        RegisterHeartbeat();      // L686
        RegisterTvStatic();       // L726
        RegisterDustScratches();  // L752
        RegisterFilmAdvance();    // L807
        RegisterPerspectiveTilt();// L845
        RegisterRipple();         // L879
        RegisterFocusLines();     // L920
        RegisterSpeedLines();     // L947
        RegisterStarGlint();      // L980
        RegisterColorBars();      // L1018
        RegisterZoomStutter();    // L1043
        RegisterNegativeRing();   // L1062
        RegisterEdgeDetect();     // L1101
        RegisterShatter();        // L1158
        RegisterDefocus();        // L1238
        RegisterSnapshot();       // L1272
        RegisterSquash();         // L1310
        RegisterScanBar();        // L1327
        RegisterLoopScroll();     // L1367
    }

    // ================= 本包 helper（JS L8-109 + 各条目间 function 逐条对位） =================

    private static double D(JObject o, string k)
    {
        var v = o[k];
        return v != null && (v.Type == JTokenType.Float || v.Type == JTokenType.Integer) ? (double)v : 0;
    }

    private static string ScK(Scheme sc, string k) => sc.Str(k) ?? "#000000";

    private static bool IsDark(string? c) => J.Lum(c ?? "#000000") < 0.45;

    /// <summary>JS ToInt32 语义（大 uint/double 入 J.h/J.r 前的回绕；J.* 的 int 形参消费）。</summary>
    private static int ToI(double v) => unchecked((int)(long)v);

    private static double Bell(double k) => Math.Sin(Math.PI * J.Clamp(k));

    private static uint EvS(PlanEvent ev) => J.H(ToI(Math.Round(ev.T * 1000)), 9127);

    private static double AmpOf(PlanEvent ev) => J.Clamp(ev.Amp, 0.3, 1.6);

    /// <summary>攻击(0..a)→保持→释放(b..1)（JS ahr）。</summary>
    private static double Ahr(double k, double a, double b, Func<double, double>? inE = null, Func<double, double>? outE = null)
    {
        inE ??= J.E.OutCubic;
        outE ??= J.E.InCubic;
        return k < a ? inE(k / a) : k > b ? 1 - outE((k - b) / Math.Max(1e-3, 1 - b)) : 1;
    }

    private static double HueD(double a, double b)
    {
        var d = Math.Abs(a - b) % 360;
        return d > 180 ? 360 - d : d;
    }

    private static string? Darkest(IEnumerable<string?> list)
    {
        string? best = null;
        foreach (var c in list)
        {
            if (string.IsNullOrEmpty(c)) continue;
            if (best == null || J.Lum(c) < J.Lum(best)) best = c;
        }
        return best;
    }

    private static string? Lightest(IEnumerable<string?> list)
    {
        string? best = null;
        foreach (var c in list)
        {
            if (string.IsNullOrEmpty(c)) continue;
            if (best == null || J.Lum(c) > J.Lum(best)) best = c;
        }
        return best;
    }

    /// <summary>配色里最鲜艳的颜色；黑白配色回退到冷蓝（JS vivid）。</summary>
    private static string Vivid(Scheme sc, string fb = "#4FB8FF")
    {
        string? best = null;
        var bv = 0.18;
        foreach (var c in new[] { sc.Str("accent"), sc.Str("accent2"), sc.Str("ghostA"), sc.Str("ghostB"), sc.Str("fg") })
        {
            if (string.IsNullOrEmpty(c)) continue;
            var (_, s, l) = J.ToHsl(c);
            var v = s * (1 - Math.Abs(l - 0.55) * 1.1);
            if (!(v > bv)) continue;
            bv = v;
            best = c;
        }
        return best ?? fb;
    }

    /// <summary>双色调的两个色相：配色中最鲜艳的一个，再加一个色相差明显的（JS huePair）。</summary>
    private static (double H1, double H2) HuePair(Scheme sc)
    {
        var cs = new List<(double H, double V)>();
        foreach (var c in new[] { sc.Str("accent"), sc.Str("accent2"), sc.Str("ghostA"), sc.Str("ghostB"), sc.Str("fg"), sc.Str("bg") })
        {
            if (string.IsNullOrEmpty(c)) continue;
            var (h, s, l) = J.ToHsl(c);
            cs.Add((h, s * (1 - Math.Abs(l - 0.5) * 1.2)));
        }
        cs.Sort((p, q) => q.V.CompareTo(p.V));
        if (cs.Count == 0 || cs[0].V < 0.15) return (330, 195);
        var bi = cs.FindIndex(p => p.V > 0.15 && HueD(p.H, cs[0].H) > 50);
        return (cs[0].H, bi >= 0 ? cs[bi].H : cs[0].H + 170);
    }

    private static string Rgba(string hex, double a)
    {
        var (r, g, b) = J.Hex(hex);
        return "rgba(" + r + "," + g + "," + b + "," + a.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) + ")";
    }

    // ---- 离屏画布组（JS buf(i, w, h)：模块级数组、按索引复用、尺寸变化重建） ----

    [ThreadStatic] private static SKSurface?[]? _buf;

    private static SKSurface Buf(int i, int w, int h)
    {
        _buf ??= new SKSurface[8];
        w = Math.Max(1, w);
        h = Math.Max(1, h);
        var s = _buf[i];
        if (s == null || s.Canvas.DeviceClipBounds.Width != w || s.Canvas.DeviceClipBounds.Height != h)
        {
            s?.Dispose();
            s = SKSurface.Create(new SKImageInfo(w, h));
            _buf[i] = s;
        }
        return s;
    }

    private static readonly SKSamplingOptions SmpN = new(SKFilterMode.Nearest, SKMipmapMode.None);
    private static readonly SKSamplingOptions SmpL = new(SKFilterMode.Linear, SKMipmapMode.None);

    /// <summary>alpha 白画笔（drawImage 的 globalAlpha/globalCompositeOperation 对位）。</summary>
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

    /// <summary>以 (cx,cy) 为锚点把 img 缩放绘制（JS drawScaled）。</summary>
    private static void DrawScaled(SKCanvas ctx, SKImage img, double s, double cx, double cy, double cw, double ch,
        double alpha = 1, SKBlendMode blend = SKBlendMode.SrcOver)
    {
        using var p = ImgPaint(alpha, blend);
        ctx.DrawImage(img, new SKRect((float)(cx - cx * s), (float)(cy - cy * s),
            (float)(cx - cx * s + cw * s), (float)(cy - cy * s + ch * s)), SmpL, p);
    }

    private static void DrawImg(SKCanvas ctx, SKImage img, double dx, double dy, double alpha = 1,
        SKBlendMode blend = SKBlendMode.SrcOver, SKSamplingOptions? smp = null)
    {
        using var p = ImgPaint(alpha, blend);
        ctx.DrawImage(img, new SKPoint((float)dx, (float)dy), smp ?? SmpL, p);
    }

    /// <summary>canvas.drawImage(img, sx,sy,sw,sh, dx,dy,dw,dh)。</summary>
    private static void DrawImg9(SKCanvas ctx, SKImage img, double sx, double sy, double sw, double sh,
        double dx, double dy, double dw, double dh, double alpha = 1, SKBlendMode blend = SKBlendMode.SrcOver,
        SKSamplingOptions? smp = null)
    {
        using var p = ImgPaint(alpha, blend);
        ctx.DrawImage(img, new SKRect((float)sx, (float)sy, (float)(sx + sw), (float)(sy + sh)),
            new SKRect((float)dx, (float)dy, (float)(dx + dw), (float)(dy + dh)), smp ?? SmpL, p);
    }

    /// <summary>|帧-bg| 灰度蒙版：暗配色是黑底亮墨，亮配色再反相（JS greyMask）。</summary>
    private static SKSurface GreyMask(SKSurface t, SKSurface s, Scheme sc, int w, int h, bool dk)
    {
        var x = t.Canvas;
        x.SetMatrix(SKMatrix.Identity);
        x.Clear();
        using (var simg = s.Snapshot())
        using (var p = new SKPaint())
            x.DrawImage(simg, new SKRect(0, 0, w, h), SmpL, p);
        FillMode(x, 0, 0, w, h, ScK(sc, "bg"), 1, SKBlendMode.Difference);
        FillMode(x, 0, 0, w, h, "#808080", 1, SKBlendMode.Saturation);
        if (!dk) FillMode(x, 0, 0, w, h, "#ffffff", 1, SKBlendMode.Difference);
        return t;
    }

    /// <summary>蒙版染上 col：暗配色用 multiply 叠加，亮配色用 screen（JS tintMask）。</summary>
    private static SKSurface TintMask(SKSurface t, SKSurface m, string col, bool dk)
    {
        var x = t.Canvas;
        var w = (float)t.Canvas.DeviceClipBounds.Width;
        var h = (float)t.Canvas.DeviceClipBounds.Height;
        x.SetMatrix(SKMatrix.Identity);
        x.Clear();
        using (var p = new SKPaint())
            x.DrawImage(m.Snapshot(), 0, 0, p);
        FillMode(x, 0, 0, w, h, col, 1, dk ? SKBlendMode.Multiply : SKBlendMode.Screen);
        return t;
    }

    /// <summary>fx()/fxReg() 包装：save/restore + 进度钳到 0..1（渲染器侧也已 clamp，双保险同 JS）。</summary>
    private static Action<Env, PlanEvent> Wrapped(Action<Env, PlanEvent, double> draw) => (env, ev) =>
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

    // ================= 注册（JS fx(k, d) 出现序） =================

    private static void RegisterRadialChroma() => Reg.Register("fx", "radialChroma", new FxDef
    {
        Name = "放射色収差",
        Tags = new[] { "glitch", "emotional", "pop" },
        W = 0.9,
        Dur = 4,
        Amp = 1,
        Mid = true,
        // scratch: true, ae: 'chroma' —— 无 FxDef 字段（见头注降级 1）
        Draw = Wrapped(DrawRadialChroma),
    }, Pk);

    private static void DrawRadialChroma(Env env, PlanEvent ev, double k)
    {
        var S = FxPlug.S;
        var cw = FxPlug.Cw;
        var ch = FxPlug.Ch;
        var sc = env.Sc!;
        if (S == null) return;
        var ctx = env.Ctx;
        var dk = IsDark(ScK(sc, "bg"));
        var s = EvS(ev);
        var d = (0.03 + 0.016 * J.R(ToI(s), 1)) * AmpOf(ev) * (0.4 + 0.6 * J.E.OutQuad(k)) * (k > 0.8 ? 1 - (k - 0.8) * 2.5 : 1);
        var w = Math.Max(2, (int)Math.Round(cw / 3.0));
        var h = Math.Max(2, (int)Math.Round(ch / 3.0));
        var m = GreyMask(Buf(0, w, h), S, sc, w, h, dk);
        var r = TintMask(Buf(1, w, h), m, "#FF3020", dk);
        var c = TintMask(Buf(2, w, h), m, "#18E0FF", dk);
        using (var ri = r.Snapshot())
        using (var ci = c.Snapshot())
        {
            var cx = cw / 2.0;
            var cy = ch / 2.0;
            DrawScaled(ctx, ri, 1 + d, cx, cy, cw, ch, 0.95, dk ? SKBlendMode.Screen : SKBlendMode.Multiply);
            DrawScaled(ctx, ci, 1 - d * 0.7, cx, cy, cw, ch, 0.95, dk ? SKBlendMode.Screen : SKBlendMode.Multiply);
            DrawScaled(ctx, ri, 1 + d * 2.2, cx, cy, cw, ch, 0.45, dk ? SKBlendMode.Screen : SKBlendMode.Multiply);
        }
    }

    private static void RegisterBloomFlash() => Reg.Register("fx", "bloomFlash", new FxDef
    {
        Name = "ブルーム",
        Tags = new[] { "pop", "emotional", "calm" },
        W = 1,
        Dur = 6,
        Amp = 1,
        Mid = true,
        Draw = Wrapped(DrawBloomFlash),
    }, Pk);

    private static void DrawBloomFlash(Env env, PlanEvent ev, double k)
    {
        var S = FxPlug.S;
        var cw = FxPlug.Cw;
        var ch = FxPlug.Ch;
        var sc = env.Sc!;
        if (S == null) return;
        var ctx = env.Ctx;
        var a = J.Clamp(AmpOf(ev), 0.5, 1.3) * (k < 0.12 ? J.E.OutCubic(k / 0.12) : Math.Pow(1 - (k - 0.12) / 0.88, 1.5));
        if (a < 0.02) return;
        var dk = IsDark(ScK(sc, "bg"));
        var w1 = Math.Max(4, (int)Math.Round(cw / 6.0));
        var h1 = Math.Max(4, (int)Math.Round(ch / 6.0));
        var w2 = Math.Max(2, (int)Math.Round(cw / 24.0));
        var h2 = Math.Max(2, (int)Math.Round(ch / 24.0));
        var t1 = Buf(0, w1, h1);
        var x1 = t1.Canvas;
        x1.SetMatrix(SKMatrix.Identity);
        x1.Clear();
        using (var b1 = new SKPaint())
        {
            if (env.AllowFilter) b1.ImageFilter = JText.BlurFilter(Math.Max(1, w1 / 220.0));
            using (var simg = S.Snapshot())
                x1.DrawImage(simg, new SKRect(0, 0, w1, h1), SmpL, b1);
        }
        var t2 = Buf(1, w2, h2);
        var x2 = t2.Canvas;
        x2.SetMatrix(SKMatrix.Identity);
        x2.Clear();
        using (var b2 = new SKPaint())
        {
            if (env.AllowFilter) b2.ImageFilter = JText.BlurFilter(1.5);
            using var i1 = t1.Snapshot();
            x2.DrawImage(i1, new SKRect(0, 0, w2, h2), SmpL, b2);
        }
        var cx = cw / 2.0;
        var cy = ch / 2.0;
        using var i2 = t2.Snapshot();
        using (var i1b = t1.Snapshot())
        {
            if (dk)
            {
                DrawImg(ctx, i1b, 0, 0, a, SKBlendMode.Screen);
                DrawScaled(ctx, i2, 1.05, cx, cy, cw, ch, Math.Min(1, a * 1.25), SKBlendMode.Screen);
                FillMode(ctx, 0, 0, cw, ch, ScK(sc, "fg"), 0.1 * a);
            }
            else
            {
                // 亮配色走曝光过度：亮纸在墨上泛光，整体提向白
                DrawImg(ctx, i1b, 0, 0, 0.75 * a, SKBlendMode.Screen);
                DrawScaled(ctx, i2, 1.04, cx, cy, cw, ch, 0.5 * a, SKBlendMode.Screen);
            }
        }
    }

    /// <summary>可分离的镜头扭曲：先列后行，分段线性条带（无接缝）。JS L187。</summary>
    private static void RegisterBulge() => Reg.Register("fx", "bulge", new FxDef
    {
        Name = "魚眼",
        Tags = new[] { "pop", "graphic", "glitch" },
        W = 0.8,
        Dur = 5,
        Amp = 1,
        Mid = true,
        Draw = Wrapped(DrawBulge),
    }, Pk);

    private static void DrawBulge(Env env, PlanEvent ev, double k)
    {
        var S = FxPlug.S;
        var cw = FxPlug.Cw;
        var ch = FxPlug.Ch;
        if (S == null) return;
        var ctx = env.Ctx;
        var s = EvS(ev);
        var pinch = J.R(ToI(s), 1) < 0.25;
        var amt = k < 0.3 ? J.E.OutCubic(k / 0.3) : 1 - J.E.InOutCubic((k - 0.3) / 0.7);
        var a = J.Clamp((pinch ? -0.26 : 0.27) * J.Clamp(AmpOf(ev), 0.5, 1.35) * amt, -0.36, 0.38);
        if (Math.Abs(a) < 0.004) return;
        var cx = cw * (0.5 + J.Rs(ToI(s), 2) * 0.05);
        var cy = ch * (0.5 + J.Rs(ToI(s), 3) * 0.04);
        double g(double u) => u * (1 - a * (1 - u * u));
        double map(double u, double c, double l) => c + u * (u < 0 ? c : l - c);
        const int n = 40;
        var t = Buf(0, cw, ch);
        var x = t.Canvas;
        x.SetMatrix(SKMatrix.Identity);
        x.Clear();
        using var simg = S.Snapshot();
        using var bp = new SKPaint();
        double p = 0, ps = 0;
        for (var i = 1; i <= n; i++)
        {
            var u = -1 + 2 * i / (double)n;
            var d = i == n ? cw : Math.Round(map(u, cx, cw));
            var sx = i == n ? cw : map(g(u), cx, cw);
            if (d > p && sx > ps)
                x.DrawImage(simg, new SKRect((float)ps, 0, (float)sx, (float)ch),
                    new SKRect((float)p, 0, (float)d, (float)ch), SmpL, bp);
            p = d;
            ps = sx;
        }
        using var tim = t.Snapshot();
        ctx.Clear();
        p = 0;
        ps = 0;
        for (var i = 1; i <= n; i++)
        {
            var u = -1 + 2 * i / (double)n;
            var d = i == n ? ch : Math.Round(map(u, cy, ch));
            var sy = i == n ? ch : map(g(u), cy, ch);
            if (d > p && sy > ps)
                ctx.DrawImage(tim, new SKRect(0, (float)ps, (float)cw, (float)sy),
                    new SKRect(0, (float)p, (float)cw, (float)d), SmpL, bp);
            p = d;
            ps = sy;
        }
    }

    /// <summary>纵向熔融：一段段列的窗口向下（或向上）拉伸，平滑噪声保证连贯。JS L223。</summary>
    private static void RegisterPixelSort() => Reg.Register("fx", "pixelSort", new FxDef
    {
        Name = "ピクセルソート",
        Tags = new[] { "glitch" },
        W = 0.8,
        Dur = 3,
        Amp = 1,
        Glitchy = true,
        Mid = true,
        Draw = Wrapped(DrawPixelSort),
    }, Pk);

    private static void DrawPixelSort(Env env, PlanEvent ev, double k)
    {
        var S = FxPlug.S;
        var cw = FxPlug.Cw;
        var ch = FxPlug.Ch;
        if (S == null) return;
        var ctx = env.Ctx;
        var a = AmpOf(ev);
        var s = EvS(ev);
        var st = ToI(env.Step * 19 + s);
        var up = J.R(ToI(s), 1) < 0.3;
        var xa = cw * J.Rr(0.02, 0.35, st, 1);
        var xb = Math.Min(cw, xa + cw * J.Rr(0.4, 0.7, st, 2));
        using var simg = S.Snapshot();
        using var p = new SKPaint();
        var x = (int)Math.Round(xa);
        var i = 0;
        while (x < xb && i < 200)
        {
            var wr = Math.Max(1, (int)Math.Round(cw * J.Rr(0.002, 0.009, st, i, 3)));
            var on = J.Noise1(x / (double)cw * 9, st + 5) > -0.25 && J.R(st, i, 4) < 0.85;
            if (on)
            {
                var n1 = J.Noise1(x / (double)cw * 7, st + 9) * 0.5 + 0.5;
                var n2 = J.Noise1(x / (double)cw * 23, st + 13) * 0.5 + 0.5;
                var win = ch * (0.08 + 0.12 * n1);
                var str = 1 + (0.5 + 2 * n2) * a;
                if (!up)
                {
                    var y0 = ch * (0.36 + 0.12 * n2);
                    var dh = Math.Min(ch - y0, win * str);
                    DrawImg9(ctx, simg, x, y0, wr, win, x, y0, wr, dh);
                }
                else
                {
                    var y1 = ch * (0.64 - 0.12 * n2);
                    var dh = Math.Min(y1, win * str);
                    DrawImg9(ctx, simg, x, y1 - win, wr, win, x, y1 - dh, wr, dh);
                }
            }
            x += wr;
            i++;
        }
    }

    private static void RegisterInterlace() => Reg.Register("fx", "interlace", new FxDef
    {
        Name = "インターレース",
        Tags = new[] { "glitch", "emotional" },
        W = 0.7,
        Dur = 3,
        Amp = 1,
        Glitchy = true,
        Mid = true,
        Draw = Wrapped(DrawInterlace),
    }, Pk);

    /// <summary>1×2L 条纹贴图：上半黑、下半透明（JS rowTile，按 L 缓存）。</summary>
    private static readonly ConcurrentDictionary<int, SKBitmap> Rows = new();

    private static SKBitmap RowTile(int l)
    {
        if (Rows.TryGetValue(l, out var t)) return t;
        var b = new SKBitmap(1, 2 * l);
        using (var x = new SKCanvas(b))
        using (var p = new SKPaint { Color = SKColors.Black })
            x.DrawRect(new SKRect(0, 0, 1, l), p);
        if (Rows.Count > 32)
        {
            foreach (var kv in Rows) kv.Value.Dispose();
            Rows.Clear();
        }
        Rows[l] = b;
        return b;
    }

    private static void DrawInterlace(Env env, PlanEvent ev, double k)
    {
        var S = FxPlug.S;
        var cw = FxPlug.Cw;
        var ch = FxPlug.Ch;
        var sc = env.Sc!;
        if (S == null) return;
        var ctx = env.Ctx;
        var a = AmpOf(ev);
        var st = ToI(env.Step * 7 + EvS(ev));
        var l = Math.Max(1, (int)Math.Round(ch / 200.0));
        var dx = (J.R(st, 1) < 0.5 ? 1 : -1) * cw * (0.012 + 0.02 * J.R(st, 2)) * a * (1 - 0.45 * k);
        FillMode(ctx, 0, 0, cw, ch, ScK(sc, "bg"));
        using (var simg = S.Snapshot())
            DrawImg(ctx, simg, Math.Round(-dx * 0.35), 0);
        var t = Buf(0, cw, ch);
        var x = t.Canvas;
        x.SetMatrix(SKMatrix.Identity);
        x.Clear();
        using (var simg2 = S.Snapshot())
            DrawImg(x, simg2, Math.Round(dx), 0);
        // destination-in + 条纹 pattern（translate y = (step%2)*L）
        using (var tim = t.Snapshot())
        using (var shader = SKShader.CreateBitmap(RowTile(l), SKShaderTileMode.Repeat, SKShaderTileMode.Repeat,
                   SKMatrix.CreateTranslation(0, (float)((env.Step % 2) * l))))
        using (var ip = new SKPaint { BlendMode = SKBlendMode.DstIn, Shader = shader })
            x.DrawRect(new SKRect(0, 0, cw, ch), ip);
        using (var outImg = t.Snapshot())
            DrawImg(ctx, outImg, 0, 0);
    }

    /// <summary>压缩劣化：网格对齐的块被拉平、从顶行向下拖影、量化或错位。JS L307。</summary>
    private static void RegisterMacroBlock() => Reg.Register("fx", "macroBlock", new FxDef
    {
        Name = "ブロックノイズ",
        Tags = new[] { "glitch" },
        W = 0.8,
        Dur = 3,
        Amp = 1,
        Glitchy = true,
        Mid = true,
        Draw = Wrapped(DrawMacroBlock),
    }, Pk);

    private static void DrawMacroBlock(Env env, PlanEvent ev, double k)
    {
        var S = FxPlug.S;
        var cw = FxPlug.Cw;
        var ch = FxPlug.Ch;
        var sc = env.Sc!;
        if (S == null) return;
        var ctx = env.Ctx;
        var a = AmpOf(ev);
        var st = ToI(env.Step * 23 + EvS(ev));
        var b = Math.Max(6, (int)Math.Round(Math.Min(cw, ch) / 18.0));
        var nx = (int)Math.Ceiling(cw / (double)b);
        var ny = (int)Math.Ceiling(ch / (double)b);
        var t = Buf(3, 3, 3);
        var tx = t.Canvas;
        tx.SetMatrix(SKMatrix.Identity);
        var nC = 2 + (int)(J.H(st, 1) % 3);
        using var simg = S.Snapshot();
        for (var c = 0; c < nC; c++)
        {
            var gw = Math.Min(nx, 3 + (int)(J.H(st, c, 2) % 7));
            var gh = 1 + (int)(J.H(st, c, 3) % 3);
            var i0 = (int)Math.Floor(J.R(st, c, 4) * (nx - gw + 1));
            var j0 = (int)Math.Round(ny * (0.38 + 0.24 * J.R(st, c, 5)) - gh / 2.0);
            for (var j = 0; j < gh; j++)
            for (var i = 0; i < gw; i++)
            {
                var id = i * 16 + j;
                if (J.R(st, c, id, 6) < 0.12) continue;
                var bx = (i0 + i) * b;
                var by = J.Clamp(j0 + j, 0, ny - 1) * b;
                var bw = Math.Min(b, cw - bx);
                var bh = Math.Min(b, ch - by);
                var m = J.R(st, c, id, 7);
                if (bw < 1 || bh < 1) continue;
                if (m < 0.42)
                {
                    // datamosh：块的顶行向下拖 1..3 格
                    var n = 1 + (int)(J.H(st, c, id, 9) % 3);
                    DrawImg9(ctx, simg, bx, by, bw, 1, bx, by, bw, Math.Min(ch - by, (double)b * n));
                }
                else if (m < 0.7)
                {
                    // 量化成 3×3 平坦色块
                    tx.Clear();
                    using (var qp = new SKPaint())
                        tx.DrawImage(simg, new SKRect(bx, by, bx + bw, by + bh), new SKRect(0, 0, 3, 3), SmpL, qp);
                    using (var qimg = t.Snapshot())
                        DrawImg9(ctx, qimg, 0, 0, 3, 3, bx, by, bw, bh, smp: SmpN);
                }
                else if (m < 0.9)
                {
                    // 从邻块拷贝（错位）
                    var sx = J.Clamp(bx + (J.R(st, c, id, 10) < 0.5 ? -1 : 1) * (1 + (int)(J.H(st, c, id, 12) % 2)) * b, 0, cw - bw);
                    DrawImg9(ctx, simg, sx, by, bw, bh, bx, by, bw, bh);
                }
                else
                {
                    // 整块拉平
                    DrawImg9(ctx, simg, Math.Min(cw - 1, bx + bw / 2.0), Math.Min(ch - 1, by + bh / 2.0), 1, 1, bx, by, bw, bh, smp: SmpN);
                }
                if (m >= 0.42 && J.R(st, c, id, 8) < 0.06 * a)
                {
                    FillMode(ctx, bx, by, bw, bh,
                        J.R(st, c, id, 11) < 0.5 ? ScK(sc, "ghostA") : ScK(sc, "ghostB"),
                        0.3, SKBlendMode.Difference);
                }
            }
        }
    }

    private static void RegisterHalftone() => Reg.Register("fx", "halftone", new FxDef
    {
        Name = "網点",
        Tags = new[] { "pop", "graphic", "editorial" },
        W = 0.9,
        Dur = 5,
        Amp = 1,
        Mid = true,
        Draw = Wrapped(DrawHalftone),
    }, Pk);

    /// <summary>圆点网屏贴图（JS dotTile，按 c|r 缓存；3×3 邻接圆保证平铺无缝）。</summary>
    private static readonly ConcurrentDictionary<string, SKBitmap> Dots = new();

    private static SKBitmap DotTile(int c, double r)
    {
        var key = c + "|" + r.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        if (Dots.TryGetValue(key, out var t)) return t;
        var b = new SKBitmap(c, c);
        using (var x = new SKCanvas(b))
        using (var p = new SKPaint { Color = SKColors.Black, IsAntialias = true })
            for (var j = -1; j <= 1; j++)
            for (var i = -1; i <= 1; i++)
                x.DrawCircle(c / 2f + i * c, c / 2f + j * c, (float)r, p);
        if (Dots.Count > 64)
        {
            foreach (var kv in Dots) kv.Value.Dispose();
            Dots.Clear();
        }
        Dots[key] = b;
        return b;
    }

    private static void DrawHalftone(Env env, PlanEvent ev, double k)
    {
        var cw = FxPlug.Cw;
        var ch = FxPlug.Ch;
        var sc = env.Sc!;
        var ctx = env.Ctx;
        var c = Math.Max(5, (int)Math.Round(Math.Min(cw, ch) / 44.0));
        var amt = Ahr(k, 0.3, 0.72);
        var r = (int)Math.Round(c * J.Lerp(0.72, 0.42, amt) * 4) / 4.0;
        if (r >= c * 0.71) return;
        var q = 45 * J.DEG;
        var mat = new SKMatrix
        {
            ScaleX = (float)Math.Cos(q),
            SkewX = (float)-Math.Sin(q),
            TransX = (float)(cw / 2.0),
            SkewY = (float)Math.Sin(q),
            ScaleY = (float)Math.Cos(q),
            TransY = (float)(ch / 2.0),
            Persp0 = 1,
            Persp1 = 0,
            Persp2 = 1,
        };
        using (var shader = SKShader.CreateBitmap(DotTile(c, r), SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, mat))
        using (var p = new SKPaint { BlendMode = SKBlendMode.DstIn, Shader = shader, IsAntialias = true })
            ctx.DrawRect(new SKRect(0, 0, cw, ch), p);
        if (FxPlug.Transparent) return;
        using (var p2 = new SKPaint { BlendMode = SKBlendMode.DstOver, Color = Env.SkColor(J.Mix(ScK(sc, "bg"), ScK(sc, "fg"), 0.16)) })
            ctx.DrawRect(new SKRect(0, 0, cw, ch), p2);
    }

    private static void RegisterDuotone() => Reg.Register("fx", "duotone", new FxDef
    {
        Name = "ダブルトーン",
        Tags = new[] { "pop", "emotional", "graphic" },
        W = 0.8,
        Dur = 4,
        Amp = 1,
        Mid = true,
        Draw = Wrapped(DrawDuotone),
    }, Pk);

    private static void DrawDuotone(Env env, PlanEvent ev, double k)
    {
        var cw = FxPlug.Cw;
        var ch = FxPlug.Ch;
        var sc = env.Sc!;
        var ctx = env.Ctx;
        var s = EvS(ev);
        var a = J.Clamp(AmpOf(ev), 0.6, 1) * Ahr(k, 0.12, 0.7);
        if (a < 0.02) return;
        var (h1, h2) = HuePair(sc);
        if (J.R(ToI(s), 1) < 0.4) (h1, h2) = (h2, h1);
        var dark = J.Hsl(h1, 0.8, 0.15);
        var light = J.Hsl(h2, 1, 0.76);
        FillMode(ctx, 0, 0, cw, ch, "#808080", a, SKBlendMode.Saturation);
        FillMode(ctx, 0, 0, cw, ch, light, a, SKBlendMode.Multiply);
        FillMode(ctx, 0, 0, cw, ch, dark, a, SKBlendMode.Screen);
    }

    /// <summary>Bayer 8×8 有序抖动阈值贴图（JS bayerTile，建一次）。</summary>
    private static SKBitmap? _bayer;

    private static SKBitmap BayerTile()
    {
        if (_bayer != null) return _bayer;
        var m = new List<List<int>> { new() { 0 } };
        while (m.Count < 8)
        {
            var n = m.Count;
            var next = new List<List<int>>();
            for (var y = 0; y < 2 * n; y++)
            {
                var row = new List<int>();
                for (var x = 0; x < 2 * n; x++)
                {
                    var v = 4 * m[y % n][x % n];
                    row.Add(v + (y < n ? new[] { 0, 2 }[x < n ? 0 : 1] : new[] { 3, 1 }[x < n ? 0 : 1]));
                }
                next.Add(row);
            }
            m = next;
        }
        var b = new SKBitmap(8, 8);
        using (var x = new SKCanvas(b))
        {
            for (var y = 0; y < 8; y++)
            for (var i = 0; i < 8; i++)
            {
                var g = (int)Math.Round((m[y][i] + 0.5) / 64 * 127.5);
                using var p = new SKPaint { Color = new SKColor((byte)g, (byte)g, (byte)g) };
                x.DrawRect(new SKRect(i, y, i + 1, y + 1), p);
            }
        }
        _bayer = b;
        return b;
    }

    /// <summary>有序抖动 → 硬阈值（contrast 滤镜）→ 映射到配色的最深/最浅色。JS L452。</summary>
    private static void RegisterDitherBit() => Reg.Register("fx", "ditherBit", new FxDef
    {
        Name = "1bitディザ",
        Tags = new[] { "glitch", "graphic", "pop" },
        W = 0.7,
        Dur = 3,
        Amp = 1,
        Mid = true,
        Draw = Wrapped(DrawDitherBit),
    }, Pk);

    /// <summary>CSS contrast(c)：c' = (c-0.5)*c + 0.5 → 4x5 颜色矩阵。</summary>
    private static SKColorFilter ContrastFilter(double c) => SKColorFilter.CreateColorMatrix(new float[]
    {
        (float)c, 0, 0, 0, (float)(0.5 * (1 - c)),
        0, (float)c, 0, 0, (float)(0.5 * (1 - c)),
        0, 0, (float)c, 0, (float)(0.5 * (1 - c)),
        0, 0, 0, 1, 0,
    });

    private static void DrawDitherBit(Env env, PlanEvent ev, double k)
    {
        var S = FxPlug.S;
        var cw = FxPlug.Cw;
        var ch = FxPlug.Ch;
        var sc = env.Sc!;
        if (S == null) return;
        var ctx = env.Ctx;
        var p = Math.Max(2, (int)Math.Round(ch / 200.0) * (k < 0.34 ? 2 : 1));
        var w = (int)Math.Ceiling(cw / (double)p);
        var h = (int)Math.Ceiling(ch / (double)p);
        var t = Buf(2, w, h);
        var x = t.Canvas;
        x.SetMatrix(SKMatrix.Identity);
        x.Clear();
        using (var cp = new SKPaint { BlendMode = SKBlendMode.Src, Shader = SKShader.CreateBitmap(BayerTile(), SKShaderTileMode.Repeat, SKShaderTileMode.Repeat) })
            x.DrawRect(new SKRect(0, 0, w, h), cp);
        using (var simg = S.Snapshot())
        using (var lp = ImgPaint(0.5, SKBlendMode.Plus))
            x.DrawImage(simg, new SKRect(0, 0, w, h), SmpL, lp);
        FillMode(x, 0, 0, w, h, "#808080", 1, SKBlendMode.Saturation);
        var t2 = Buf(3, w, h);
        var y = t2.Canvas;
        y.SetMatrix(SKMatrix.Identity);
        y.Clear();
        using (var tim = t.Snapshot())
        using (var cp2 = new SKPaint())
        {
            if (env.AllowFilter) cp2.ColorFilter = ContrastFilter(60);
            y.DrawImage(tim, 0, 0, cp2);
        }
        var c0 = Darkest(new[] { sc.Str("bg"), sc.Str("fg"), sc.Str("ink") }) ?? "#000000";
        var c1 = Lightest(new[] { sc.Str("bg"), sc.Str("fg"), sc.Str("ink") }) ?? "#ffffff";
        FillMode(y, 0, 0, w, h, c1, 1, SKBlendMode.Multiply);
        FillMode(y, 0, 0, w, h, c0, 1, SKBlendMode.Screen);
        using (var t2img = t2.Snapshot())
            DrawImg9(ctx, t2img, 0, 0, w, h, 0, 0, w * p, h * p, smp: SmpN);
    }

    /// <summary>首帧咬合到位再阻尼弹簧回正的倾斜。JS L496。</summary>
    private static void RegisterRotateSnap() => Reg.Register("fx", "rotateSnap", new FxDef
    {
        Name = "傾きスナップ",
        Tags = new[] { "pop", "graphic", "glitch" },
        W = 0.9,
        Dur = 5,
        Amp = 1,
        Mid = true,
        Draw = Wrapped(DrawRotateSnap),
    }, Pk);

    private static void DrawRotateSnap(Env env, PlanEvent ev, double k)
    {
        var S = FxPlug.S;
        var cw = FxPlug.Cw;
        var ch = FxPlug.Ch;
        var sc = env.Sc!;
        if (S == null) return;
        var ctx = env.Ctx;
        var s = EvS(ev);
        var dir = J.R(ToI(s), 1) < 0.5 ? 1 : -1;
        var e = k < 0.12 ? 1 : Math.Exp(-4.5 * (k - 0.12)) * Math.Cos((k - 0.12) * Math.PI * 2.4);
        var th = dir * (3 + 2 * J.R(ToI(s), 2)) * J.Clamp(AmpOf(ev), 0.5, 1.3) * e * J.DEG;
        if (Math.Abs(th) < 0.0006) return;
        var c = Math.Abs(Math.Cos(th));
        var sn = Math.Abs(Math.Sin(th));
        var z = Math.Max((cw * c + ch * sn) / cw, (cw * sn + ch * c) / ch) * (1 + 0.025 * Math.Abs(e));
        FillMode(ctx, 0, 0, cw, ch, ScK(sc, "bg"));
        using (var simg = S.Snapshot())
        {
            ctx.Save();
            ctx.Translate((float)(cw / 2.0), (float)(ch / 2.0));
            ctx.RotateRadians((float)th);
            ctx.Scale((float)z, (float)z);
            DrawImg(ctx, simg, -cw / 2.0, -ch / 2.0);
            if (k < 0.12)
            {
                ctx.RotateRadians((float)(-th * 0.45));
                DrawImg(ctx, simg, -cw / 2.0, -ch / 2.0, 0.35);
            }
            ctx.Restore();
        }
    }

    /// <summary>帧的阶梯残影；暗底用 lighten、亮底用 darken，背景不受影响。JS L529。</summary>
    private static void RegisterEchoFrames() => Reg.Register("fx", "echoFrames", new FxDef
    {
        Name = "残像エコー",
        Tags = new[] { "emotional", "pop", "glitch" },
        W = 0.9,
        Dur = 6,
        Amp = 1,
        Mid = true,
        Draw = Wrapped(DrawEchoFrames),
    }, Pk);

    private static void DrawEchoFrames(Env env, PlanEvent ev, double k)
    {
        var S = FxPlug.S;
        var cw = FxPlug.Cw;
        var ch = FxPlug.Ch;
        var sc = env.Sc!;
        if (S == null) return;
        var ctx = env.Ctx;
        var s = EvS(ev);
        var dk = IsDark(ScK(sc, "bg"));
        var a = J.Clamp(AmpOf(ev), 0.5, 1.3);
        var ang = J.R(ToI(s), 1) < 0.65
            ? (J.R(ToI(s), 2) < 0.5 ? 0 : Math.PI)
            : (J.R(ToI(s), 3) < 0.5 ? 0.25 : 0.75) * Math.PI + (J.R(ToI(s), 2) < 0.5 ? 0 : Math.PI);
        var d = Math.Min(cw, ch) * (0.02 + 0.035 * J.E.OutCubic(k)) * a;
        var fade = k < 0.1 ? 1 : 1 - J.E.InQuad((k - 0.1) / 0.9);
        if (fade < 0.02) return;
        using var simg = S.Snapshot();
        for (var i = 4; i >= 1; i--)
            DrawImg(ctx, simg, Math.Round(Math.Cos(ang) * d * i), Math.Round(Math.Sin(ang) * d * i),
                fade * (0.64 - i * 0.12), dk ? SKBlendMode.Lighten : SKBlendMode.Darken);
    }

    /// <summary>中心周围 n 个镜像楔形，真万华镜而不是轴镜像。JS L555。</summary>
    private static void RegisterKaleido() => Reg.Register("fx", "kaleido", new FxDef
    {
        Name = "万華鏡",
        Tags = new[] { "pop", "graphic", "emotional" },
        W = 0.6,
        Dur = 4,
        Amp = 1,
        Mid = true,
        Draw = Wrapped(DrawKaleido),
    }, Pk);

    private static void DrawKaleido(Env env, PlanEvent ev, double k)
    {
        var S = FxPlug.S;
        var cw = FxPlug.Cw;
        var ch = FxPlug.Ch;
        if (S == null) return;
        var ctx = env.Ctx;
        var s = EvS(ev);
        var n = J.R(ToI(s), 1) < 0.5 ? 6 : 8;
        var th = J.TAU / n;
        var r = Math.Sqrt(cw * (double)cw + ch * (double)ch);
        var cx = cw / 2.0;
        var cy = ch / 2.0;
        var rot = J.R(ToI(s), 2) * J.TAU + k * 0.5 * (J.R(ToI(s), 3) < 0.5 ? 1 : -1);
        var src = J.R(ToI(s), 4) < 0.5 ? 0 : Math.PI;
        var z = 1.05 + 0.12 * k;
        var alpha = k > 0.75 ? 1 - (k - 0.75) / 0.25 * 0.5 : 1;
        using var simg = S.Snapshot();
        for (var i = 0; i < n; i++)
        {
            ctx.Save();
            ctx.Translate((float)cx, (float)cy);
            ctx.RotateRadians((float)(rot + i * th));
            if (i % 2 == 1) ctx.Scale(1, -1);
            using (var wedge = new SKPath())
            {
                wedge.MoveTo(0, 0);
                wedge.ArcTo(new SKRect((float)-r, (float)-r, (float)r, (float)r),
                    (float)((-th / 2 - 0.006) / J.DEG), (float)((th + 0.012) / J.DEG), false);
                wedge.Close();
                ctx.ClipPath(wedge, antialias: true);
            }
            ctx.RotateRadians((float)-src);
            ctx.Scale((float)z, (float)z);
            DrawImg(ctx, simg, -cx, -cy, alpha);
            ctx.Restore();
        }
    }

    private static void RegisterBandInvert() => Reg.Register("fx", "bandInvert", new FxDef
    {
        Name = "帯反転",
        Tags = new[] { "glitch", "graphic" },
        W = 0.7,
        Dur = 3,
        Amp = 1,
        Glitchy = true,
        Mid = true,
        Draw = Wrapped(DrawBandInvert),
    }, Pk);

    private static void DrawBandInvert(Env env, PlanEvent ev, double k)
    {
        var cw = FxPlug.Cw;
        var ch = FxPlug.Ch;
        var ctx = env.Ctx;
        var s = EvS(ev);
        var st = ToI(env.Step * 13 + s);
        var n = 2 + (int)(J.H(st, 1) % 4);
        var vert = J.R(ToI(s), 2) < 0.22;
        var l = vert ? cw : ch;
        var m = vert ? ch : cw;
        var a = J.Clamp(AmpOf(ev), 0.6, 1.3);
        for (var i = 0; i < n; i++)
        {
            var h = Math.Max(2, l * J.Rr(0.012, 0.12, st, i, 3) * a);
            var y = (int)Math.Round(l * J.Rr(0.12, 0.88, st, i, 4) - h / 2);
            var part = J.R(st, i, 5) < 0.35;
            var x0 = part ? m * J.Rr(0, 0.5, st, i, 6) : 0;
            var w = part ? m * J.Rr(0.25, 0.6, st, i, 7) : m;
            if (vert) FillMode(ctx, y, x0, h, w, "#ffffff", 1, SKBlendMode.Difference);
            else FillMode(ctx, x0, y, w, h, "#ffffff", 1, SKBlendMode.Difference);
        }
    }

    private static void RegisterLightRays() => Reg.Register("fx", "lightRays", new FxDef
    {
        Name = "光芒",
        Tags = new[] { "emotional", "pop", "calm" },
        W = 0.9,
        Dur = 8,
        Amp = 1,
        Mid = true,
        Draw = Wrapped(DrawLightRays),
    }, Pk);

    private static void DrawLightRays(Env env, PlanEvent ev, double k)
    {
        var cw = FxPlug.Cw;
        var ch = FxPlug.Ch;
        var sc = env.Sc!;
        var ctx = env.Ctx;
        var s = EvS(ev);
        var dk = IsDark(ScK(sc, "bg"));
        var a = J.Clamp(AmpOf(ev), 0.5, 1.2) * Math.Pow(Bell(k), 0.6) * (0.9 + 0.1 * J.R(unchecked((int)env.Step), 5));
        if (a < 0.02) return;
        var cx = cw * (0.5 + J.Rs(ToI(s), 1) * 0.12);
        var cy = ch * (0.42 + J.Rs(ToI(s), 2) * 0.1);
        var m = Math.Min(cw, ch);
        var r = Math.Sqrt(cw * (double)cw + ch * (double)ch) * (0.4 + 0.6 * J.E.OutCubic(k));
        var n = 12 + (int)(J.H(ToI(s), 3) % 8);
        var rot = J.R(ToI(s), 4) * J.TAU + k * 0.22 * (J.R(ToI(s), 7) < 0.5 ? 1 : -1);
        var col = dk ? J.Mix(ScK(sc, "fg"), "#ffffff", 0.3) : J.Mix(Vivid(sc), "#ffffff", 0.35);
        using (var shader = SKShader.CreateRadialGradient(new SKPoint((float)cx, (float)cy), (float)r,
                   new[]
                   {
                       Env.SkColor(col, 0.55 * a),
                       Env.SkColor(col, 0.24 * a),
                       Env.SkColor(col, 0),
                   }, new float[] { 0, 0.3f, 1 }, SKShaderTileMode.Clamp))
        using (var p = new SKPaint { IsAntialias = true, Shader = shader, BlendMode = dk ? SKBlendMode.Screen : SKBlendMode.Multiply })
        using (var path = new SKPath())
        {
            for (var i = 0; i < n; i++)
            {
                var an = rot + i / (double)n * J.TAU + J.Rs(ToI(s), i, 5) * 0.12;
                var w = J.TAU / n * J.Rr(0.14, 0.45, ToI(s), i, 6);
                path.MoveTo((float)cx, (float)cy);
                path.LineTo((float)(cx + Math.Cos(an - w / 2) * r), (float)(cy + Math.Sin(an - w / 2) * r));
                path.LineTo((float)(cx + Math.Cos(an + w / 2) * r), (float)(cy + Math.Sin(an + w / 2) * r));
                path.Close();
            }
            ctx.DrawPath(path, p);
        }
        using (var g2 = SKShader.CreateRadialGradient(new SKPoint((float)cx, (float)cy), (float)(m * 0.3),
                   new[] { Env.SkColor(col, 0.4 * a), Env.SkColor(col, 0) }, new float[] { 0, 1 }, SKShaderTileMode.Clamp))
        using (var p2 = new SKPaint { Shader = g2 })
            ctx.DrawRect(new SKRect((float)(cx - m * 0.3), (float)(cy - m * 0.3), (float)(cx + m * 0.3), (float)(cy + m * 0.3)), p2);
    }

    private static void RegisterAnamorphic() => Reg.Register("fx", "anamorphic", new FxDef
    {
        Name = "アナモフレア",
        Tags = new[] { "emotional", "pop", "calm" },
        W = 0.8,
        Dur = 7,
        Amp = 1,
        Mid = true,
        Draw = Wrapped(DrawAnamorphic),
    }, Pk);

    private static void DrawAnamorphic(Env env, PlanEvent ev, double k)
    {
        var cw = FxPlug.Cw;
        var ch = FxPlug.Ch;
        var sc = env.Sc!;
        var ctx = env.Ctx;
        var s = EvS(ev);
        var dk = IsDark(ScK(sc, "bg"));
        var a = J.Clamp(AmpOf(ev), 0.5, 1.2) * Math.Pow(Bell(k), 0.5) * (0.88 + 0.12 * J.R(unchecked((int)env.Step), 7));
        if (a < 0.02) return;
        var col = Vivid(sc);
        var m = Math.Min(cw, ch);
        var dir = J.R(ToI(s), 3) < 0.5 ? 1 : -1;
        var y = ch * (0.5 + J.Rs(ToI(s), 1) * 0.1);
        var x = cw * (0.5 + J.Rs(ToI(s), 2) * 0.22) + (k - 0.5) * cw * 0.14 * dir;

        void Ell(double sx, double sy, (double Pos, SKColor Col)[] stops)
        {
            ctx.Save();
            ctx.Translate((float)x, (float)y);
            ctx.Scale((float)sx, (float)sy);
            using var g = SKShader.CreateRadialGradient(new SKPoint(0, 0), 1,
                stops.Select(t => t.Col).ToArray(), stops.Select(t => (float)t.Pos).ToArray(), SKShaderTileMode.Clamp);
            using var p = new SKPaint { IsAntialias = true, Shader = g };
            ctx.DrawCircle(0, 0, 1, p);
            ctx.Restore();
        }

        SKColor Cs(string hex, double al) => Env.SkColor(hex, al);
        if (dk)
        {
            Ell(cw * 0.8, m * 0.05, new[] { (0.0, Cs(col, 0.55 * a)), (0.4, Cs(col, 0.18 * a)), (1, Cs(col, 0)) });
            Ell(cw * 0.62, m * 0.0065, new[]
            {
                (0.0, Cs("#ffffff", 0.95 * a)),
                (0.5, Cs(J.Mix(col, "#ffffff", 0.5), 0.6 * a)),
                (1, Cs(col, 0)),
            });
            Ell(m * 0.07, m * 0.07, new[] { (0.0, Cs("#ffffff", 0.8 * a)), (1, Cs("#ffffff", 0)) });
            // 沿过画面中心直线的镜头鬼影
            for (var j = 0; j < 3; j++)
            {
                var f = new[] { 0.55, 1.1, 1.7 }[j];
                var gx = cw / 2.0 + (cw / 2.0 - x) * f;
                var gy = ch / 2.0 + (ch / 2.0 - y) * f;
                var r2 = m * new[] { 0.03, 0.06, 0.02 }[j];
                using var p = new SKPaint { IsAntialias = true, Color = Env.SkColor(col, 0.14 * a) };
                ctx.DrawCircle((float)gx, (float)gy, (float)r2, p);
            }
        }
        else
        {
            var c2 = J.Mix(col, "#ffffff", 0.2);
            Ell(cw * 0.85, m * 0.06, new[] { (0.0, Cs(c2, 0.7 * a)), (0.45, Cs(c2, 0.25 * a)), (1, Cs(c2, 0)) });
            Ell(cw * 0.65, m * 0.008, new[] { (0.0, Cs(col, a)), (0.6, Cs(col, 0.6 * a)), (1, Cs(col, 0)) });
            Ell(m * 0.05, m * 0.05, new[] { (0.0, Cs(col, 0.5 * a)), (1, Cs(col, 0)) });
        }
    }

    /// <summary>双心跳：彩色暗角收拢两次，带一点推近。JS L685。</summary>
    private static void RegisterHeartbeat() => Reg.Register("fx", "heartbeat", new FxDef
    {
        Name = "鼓動",
        Tags = new[] { "emotional", "calm" },
        W = 0.8,
        Dur = 10,
        Amp = 1,
        Mid = true,
        Draw = Wrapped(DrawHeartbeat),
    }, Pk);

    private static void DrawHeartbeat(Env env, PlanEvent ev, double k)
    {
        var S = FxPlug.S;
        var cw = FxPlug.Cw;
        var ch = FxPlug.Ch;
        var sc = env.Sc!;
        if (S == null) return;
        var ctx = env.Ctx;
        double Pulse(double x, double c, double w)
        {
            var u = (x - c) / w;
            return u < 0 || u > 1 ? 0 : u < 0.22 ? J.E.OutCubic(u / 0.22) : 1 - J.E.InOutCubic((u - 0.22) / 0.78);
        }
        var p2 = Math.Max(Pulse(k, 0, 0.36), 0.8 * Pulse(k, 0.44, 0.56)) * J.Clamp(AmpOf(ev), 0.5, 1.2);
        if (p2 < 0.01) return;
        var dk = IsDark(ScK(sc, "bg"));
        var m = Math.Min(cw, ch);
        var r = Math.Sqrt(cw * (double)cw + ch * (double)ch) / 2;
        using (var simg = S.Snapshot())
            DrawScaled(ctx, simg, 1 + 0.03 * p2, cw / 2.0, ch / 2.0, cw, ch);
        var col = dk ? J.Mix(Vivid(sc), "#000000", 0.1) : J.Mix(Vivid(sc), "#000000", 0.25);
        var pk = dk ? 0.55 : 0.8;
        using (var shader = SKShader.CreateTwoPointConicalGradient(new SKPoint((float)(cw / 2.0), (float)(ch / 2.0)),
                   (float)(m * (0.52 - 0.2 * p2)), new SKPoint((float)(cw / 2.0), (float)(ch / 2.0)), (float)r,
                   new[] { Env.SkColor(col, 0), Env.SkColor(col, 0.4 * pk * p2), Env.SkColor(col, pk * p2) },
                   new float[] { 0, 0.6f, 1 }, SKShaderTileMode.Clamp))
        using (var p = new SKPaint { Shader = shader, BlendMode = dk ? SKBlendMode.Screen : SKBlendMode.Multiply })
            ctx.DrawRect(new SKRect(0, 0, cw, ch), p);
    }

    /// <summary>256² 砂嵐噪声贴图（JS staticTex，确定性生成建一次）。</summary>
    private static SKBitmap? _staticTex;

    private static SKBitmap StaticTex()
    {
        if (_staticTex != null) return _staticTex;
        const int n = 256;
        var b = new SKBitmap(n, n);
        var px = b.PeekPixels();
        var ptr = px.GetPixels();
        {
            var buf = new byte[px.Info.BytesSize];
            System.Runtime.InteropServices.Marshal.Copy(ptr, buf, 0, buf.Length);
            for (var y = 0; y < n; y++)
            for (var i = 0; i < n; i++)
            {
                var v = (byte)(Math.Pow(J.R(i, y, 77), 1.3) * 255);
                var q = (y * n + i) * 4;
                buf[q] = v;
                buf[q + 1] = v;
                buf[q + 2] = v;
                buf[q + 3] = 255;
            }
            System.Runtime.InteropServices.Marshal.Copy(buf, 0, ptr, buf.Length);
        }
        _staticTex = b;
        return b;
    }

    private static void RegisterTvStatic() => Reg.Register("fx", "tvStatic", new FxDef
    {
        Name = "砂嵐",
        Tags = new[] { "glitch", "emotional" },
        W = 0.6,
        Dur = 5,
        Pre = 2,
        Amp = 1,
        Glitchy = true,
        Draw = Wrapped(DrawTvStatic),
    }, Pk);

    private static void DrawTvStatic(Env env, PlanEvent ev, double k)
    {
        var cw = FxPlug.Cw;
        var ch = FxPlug.Ch;
        var ctx = env.Ctx;
        var st = ToI(env.Step * 3 + EvS(ev));
        const double b = 0.4;
        var cov = (k < b ? 0.5 + 0.5 * J.E.OutQuad(k / b) : 1 - J.E.OutQuad((k - b) / (1 - b))) * J.Clamp(AmpOf(ev), 0.7, 1.1);
        if (cov < 0.02) return;
        var n = StaticTex();
        var sz = Math.Max(1, (int)Math.Round(ch / 540.0));
        var mat = new SKMatrix
        {
            ScaleX = sz * 2f,
            SkewX = 0,
            TransX = -(float)Math.Floor(J.R(st, 1) * 256) * sz * 2,
            SkewY = 0,
            ScaleY = sz,
            TransY = -(float)Math.Floor(J.R(st, 2) * 256) * sz,
            Persp0 = 1,
            Persp1 = 0,
            Persp2 = 1,
        };
        using (var shader = SKShader.CreateBitmap(n, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, mat))
        using (var p = new SKPaint { Shader = shader, Color = SKColors.White.WithAlpha((byte)Math.Round(Math.Min(1, cov) * 255)) })
            ctx.DrawRect(new SKRect(0, 0, cw, ch), p);
        FillMode(ctx, 0, (J.R(st, 3) * 1.2 - 0.1) * ch, cw, ch * 0.16, "#000000", 0.3 * cov);
        using (var wp = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(0.5 * cov * 255)) })
            for (var i = 0; i < 4; i++)
                ctx.DrawRect(new SKRect(0, (float)(J.R(st, i, 4) * ch), (float)cw,
                    (float)(J.R(st, i, 4) * ch + Math.Max(1, ch * 0.003))), wp);
    }

    private static void RegisterDustScratches() => Reg.Register("fx", "dustScratches", new FxDef
    {
        Name = "フィルム傷",
        Tags = new[] { "emotional", "calm", "editorial" },
        W = 0.8,
        Dur = 8,
        Amp = 1,
        Mid = true,
        Draw = Wrapped(DrawDustScratches),
    }, Pk);

    private static void DrawDustScratches(Env env, PlanEvent ev, double k)
    {
        var cw = FxPlug.Cw;
        var ch = FxPlug.Ch;
        var sc = env.Sc!;
        var ctx = env.Ctx;
        var s = EvS(ev);
        var st = ToI(env.Step * 31 + s);
        var dk = IsDark(ScK(sc, "bg"));
        var a = Ahr(k, 0.08, 0.7) * J.Clamp(AmpOf(ev), 0.6, 1.2);
        if (a < 0.02) return;
        var m = Math.Min(cw, ch);
        var lw = Math.Max(1, m * 0.0017);
        FillMode(ctx, 0, 0, cw, ch, J.R(st, 1) < 0.5 ? "#FFFDF5" : "#181008", 0.06 * a * J.R(st, 2));
        var nS = 1 + (int)(J.H(ToI(s), 3) % 3);
        for (var i = 0; i < nS; i++)
        {
            if (J.R(st, i, 4) < 0.18) continue;
            var x = cw * (0.1 + 0.8 * J.R(ToI(s), i, 5)) + J.Rs(st, i, 6) * m * 0.012;
            var y0 = J.R(ToI(s), i, 7) < 0.5 ? -2 : ch * 0.5 * J.R(st, i, 8);
            var y1 = J.R(ToI(s), i, 9) < 0.5 ? ch + 2 : y0 + ch * J.Rr(0.3, 0.7, st, i, 10);
                using var sp = new SKPaint
                {
                    IsAntialias = true,
                    IsStroke = true,
                    StrokeWidth = (float)(lw * J.Rr(0.7, 1.7, ToI(s), i, 11)),
                    StrokeCap = SKStrokeCap.Round,
                    Color = Env.SkColor(dk ? "#FFFDF5" : "#181008", 0.55 * a),
                };
            using var path = new SKPath();
            path.MoveTo((float)x, (float)y0);
            path.QuadTo((float)(x + J.Rs(st, i, 12) * m * 0.008), (float)((y0 + y1) / 2),
                (float)(x + J.Rs(ToI(s), i, 13) * m * 0.006), (float)y1);
            ctx.DrawPath(path, sp);
        }
        var nD = 7 + (int)(J.H(st, 14) % 9);
        for (var i = 0; i < nD; i++)
        {
            var x = J.R(st, i, 15) * cw;
            var y = J.R(st, i, 16) * ch;
            var r = m * J.Rr(0.0018, 0.007, st, i, 17);
            var cHex = J.R(st, i, 18) < (dk ? 0.7 : 0.25) ? "#FFFDF5" : "#181008";
            var cA = J.Rr(0.45, 0.9, st, i, 19) * a;
            if (J.R(st, i, 20) < 0.72)
            {
                ctx.Save();
                ctx.Translate((float)x, (float)y);
                ctx.RotateRadians((float)(J.R(st, i, 22) * J.TAU));
                using (var dp = new SKPaint { IsAntialias = true, Color = Env.SkColor(cHex, cA) })
                    ctx.DrawOval(0, 0, (float)r, (float)(r * J.Rr(0.45, 1, st, i, 21)), dp);
                ctx.Restore();
            }
            else
            {
                var l = m * J.Rr(0.02, 0.06, st, i, 23);
                var an = J.R(st, i, 24) * J.TAU;
                using var sp = new SKPaint
                {
                    IsAntialias = true,
                    IsStroke = true,
                    StrokeWidth = (float)(lw * 0.8),
                    StrokeCap = SKStrokeCap.Round,
                    Color = Env.SkColor(cHex, cA),
                };
                using var path = new SKPath();
                path.MoveTo((float)x, (float)y);
                path.CubicTo(
                    (float)(x + Math.Cos(an) * l * 0.4 + J.Rs(st, i, 25) * l * 0.4), (float)(y + Math.Sin(an) * l * 0.4),
                    (float)(x + Math.Cos(an + 0.8) * l * 0.8), (float)(y + Math.Sin(an + 0.8) * l * 0.8),
                    (float)(x + Math.Cos(an + 0.3) * l), (float)(y + Math.Sin(an + 0.3) * l));
                ctx.DrawPath(path, sp);
            }
        }
    }

    /// <summary>画面缩进带齿孔的胶片条上，胶片被拉过一格。JS L806。</summary>
    private static void RegisterFilmAdvance() => Reg.Register("fx", "filmAdvance", new FxDef
    {
        Name = "フィルム送り",
        Tags = new[] { "emotional", "editorial", "calm" },
        W = 0.6,
        Dur = 9,
        Pre = 4,
        Amp = 1,
        Draw = Wrapped(DrawFilmAdvance),
    }, Pk);

    private static void DrawFilmAdvance(Env env, PlanEvent ev, double k)
    {
        var S = FxPlug.S;
        var cw = FxPlug.Cw;
        var ch = FxPlug.Ch;
        if (S == null) return;
        var ctx = env.Ctx;
        var s = EvS(ev);
        var dir = J.R(ToI(s), 1) < 0.7 ? 1 : -1;
        var e = Ahr(k, 0.22, 0.78, J.E.InOutCubic, J.E.InOutCubic);
        if (e < 0.002) return;
        var z = 1 - 0.17 * e;
        var fw = cw * z;
        var fh = ch * z;
        var x0 = (cw - fw) / 2;
        var gap = Math.Max(2, ch * 0.03);
        var pitch = fh + gap;
        var off = J.E.InOutCubic(J.Clamp((k - 0.2) / 0.6)) * pitch * dir;
        var yc = (ch - fh) / 2 - off;
        FillMode(ctx, 0, 0, cw, ch, "#0d0b09");
        using (var simg = S.Snapshot())
        using (var p = ImgPaint(1))
            for (var j = -2; j <= 2; j++)
            {
                var y = yc + j * pitch;
                if (y > ch || y + fh < 0) continue;
                // drawImage(S, x0, y, fw, fh)：4 参 = 目标宽高（源整图缩放）
                ctx.DrawImage(simg, new SKRect((float)x0, (float)y, (float)(x0 + fw), (float)(y + fh)), SmpL, p);
            }
        if (x0 > 3)
        {
            var hp = pitch / 4;
            var hw = x0 * 0.42;
            var hh = hp * 0.46;
            using var p = new SKPaint { IsAntialias = true, Color = Env.SkColor("#ECE6DA", 0.9 * e) };
            for (var y = ((yc % hp) + hp) % hp - hp; y < ch; y += hp)
            {
                // JS 的 rrect 路径累积后一次 fill；C# 逐个 DrawRoundRect 同 paint（视觉一致）
                ctx.DrawRoundRect((float)(x0 * 0.29), (float)(y + (hp - hh) / 2), (float)hw, (float)hh,
                    (float)(hw * 0.2), (float)(hw * 0.2), p);
                ctx.DrawRoundRect((float)(cw - x0 * 0.29 - hw), (float)(y + (hp - hh) / 2), (float)hw, (float)hh,
                    (float)(hw * 0.2), (float)(hw * 0.2), p);
            }
        }
    }

    /// <summary>画面绕竖直（或水平）轴做 3D 摆动：投影条带形成透视梯形。JS L844。</summary>
    private static void RegisterPerspectiveTilt() => Reg.Register("fx", "perspectiveTilt", new FxDef
    {
        Name = "パース揺れ",
        Tags = new[] { "pop", "graphic", "emotional" },
        W = 0.8,
        Dur = 8,
        Amp = 1,
        Mid = true,
        Draw = Wrapped(DrawPerspectiveTilt),
    }, Pk);

    private static void DrawPerspectiveTilt(Env env, PlanEvent ev, double k)
    {
        var S = FxPlug.S;
        var cw = FxPlug.Cw;
        var ch = FxPlug.Ch;
        var sc = env.Sc!;
        if (S == null) return;
        var ctx = env.Ctx;
        var s = EvS(ev);
        var cols = J.R(ToI(s), 1) < (cw >= ch ? 0.7 : 0.35);
        var dir = J.R(ToI(s), 2) < 0.5 ? 1 : -1;
        var e = k < 0.25 ? J.E.OutCubic(k / 0.25) : Math.Cos((k - 0.25) / 0.75 * Math.PI * 1.5) * Math.Exp(-2.6 * (k - 0.25) / 0.75);
        var phi = dir * (30 + 10 * J.R(ToI(s), 3)) * J.Clamp(AmpOf(ev), 0.5, 1.2) * e * J.DEG;
        if (Math.Abs(phi) < 0.002) return;
        var l = cols ? cw : ch;
        var mx = cols ? ch : cw;
        var hl = l / 2;
        var dd = 1.15 * Math.Max(cw, ch);
        var co = Math.Cos(phi);
        var si = Math.Sin(phi);
        FillMode(ctx, 0, 0, cw, ch, IsDark(ScK(sc, "bg")) ? ScK(sc, "bg") : J.Mix(ScK(sc, "bg"), ScK(sc, "fg"), 0.1));
        const int n = 48;
        var sw = l / (double)n;
        using var simg = S.Snapshot();
        using var p = new SKPaint();
        double? pd0 = null, pd1 = null;
        for (var i = 0; i <= n; i++)
        {
            var u = -1 + 2 * i / (double)n;
            var pr = dd / (dd + u * hl * si);
            var d = hl + u * hl * co * pr;
            if (pd0.HasValue)
            {
                var h = mx * (pd1.Value + pr) / 2;
                var x = pd0.Value;
                var w = d - pd0.Value + 0.7;
                var s0 = (i - 1) * sw;
                if (cols)
                    ctx.DrawImage(simg, new SKRect((float)s0, 0, (float)(s0 + sw), (float)ch),
                        new SKRect((float)x, (float)((ch - h) / 2), (float)(x + w), (float)((ch - h) / 2 + h)), SmpL, p);
                else
                    ctx.DrawImage(simg, new SKRect(0, (float)s0, (float)cw, (float)(s0 + sw)),
                        new SKRect((float)((cw - h) / 2), (float)x, (float)((cw - h) / 2 + h), (float)(x + w)), SmpL, p);
            }
            pd0 = d;
            pd1 = pr;
        }
    }

    /// <summary>水波：落点周围的圆环逐个按径向位移轻微缩放重画。JS L878。</summary>
    private static void RegisterRipple() => Reg.Register("fx", "ripple", new FxDef
    {
        Name = "波紋",
        Tags = new[] { "emotional", "calm", "pop" },
        W = 0.8,
        Dur = 10,
        Amp = 1,
        Mid = true,
        Draw = Wrapped(DrawRipple),
    }, Pk);

    private static void DrawRipple(Env env, PlanEvent ev, double k)
    {
        var S = FxPlug.S;
        var cw = FxPlug.Cw;
        var ch = FxPlug.Ch;
        var sc = env.Sc!;
        if (S == null) return;
        var ctx = env.Ctx;
        var s = EvS(ev);
        var cx = cw * (0.5 + J.Rs(ToI(s), 1) * 0.1);
        var cy = ch * (0.5 + J.Rs(ToI(s), 2) * 0.08);
        var m = Math.Min(cw, ch);
        var dk = IsDark(ScK(sc, "bg"));
        var rmax = Math.Sqrt(Math.Max(cx, cw - cx) * Math.Max(cx, cw - cx) + Math.Max(cy, ch - cy) * Math.Max(cy, ch - cy));
        var lam = m * 0.1;
        var front = (0.05 + 0.95 * J.E.OutQuad(k)) * rmax * 1.05;
        var amp = m * 0.022 * J.Clamp(AmpOf(ev), 0.5, 1.3) * (1 - 0.7 * k);
        var dr = lam / 5;
        var r0 = Math.Max(0, front - 2.6 * lam);
        using var simg = S.Snapshot();
        var n = 0;
        for (var r = r0; r < front + dr && n < 40; r += dr, n++)
        {
            var rm = r + dr / 2;
            var ph = (front - rm) / lam;
            var e = Math.Exp(-ph * 0.8) * J.Clamp(ph * 4 + 1);
            var disp = amp * Math.Sin(ph * J.TAU) * e;
            if (Math.Abs(disp) < 0.3 || rm < 2) continue;
            var mm = J.Clamp(rm / Math.Max(1, rm - disp), 0.7, 1.4);
            ctx.Save();
            using (var ring = new SKPath { FillType = SKPathFillType.EvenOdd })
            {
                ring.AddCircle((float)cx, (float)cy, (float)(r + dr + 0.6));
                ring.AddCircle((float)cx, (float)cy, (float)Math.Max(0, r - 0.6));
                ctx.ClipPath(ring, antialias: true);
            }
            DrawImg(ctx, simg, cx - cx * mm, cy - cy * mm);
            ctx.Restore();
        }
        // 波峰高光
        using var sp = new SKPaint { IsAntialias = true, IsStroke = true, StrokeWidth = (float)Math.Max(1, dr * 0.9) };
        for (var j = 0; j < 3; j++)
        {
            var rc = front - lam * (j + 0.25);
            if (rc <= 2) continue;
            var al = 0.16 * Math.Exp(-j * 0.9) * (1 - 0.6 * k);
            sp.Color = dk ? Env.SkColor("#ffffff", al) : Env.SkColor(J.Mix(ScK(sc, "fg"), ScK(sc, "bg"), 0.4), al);
            sp.BlendMode = dk ? SKBlendMode.Screen : SKBlendMode.Multiply;
            ctx.DrawCircle((float)cx, (float)cy, (float)rc, sp);
        }
    }

    /// <summary>集中線：向中心汇聚的细楔形，每帧重画模拟手绘。JS L918。</summary>
    private static string InkCol(Scheme sc) =>
        IsDark(ScK(sc, "bg"))
            ? Lightest(new[] { sc.Str("fg"), sc.Str("ink"), "#FFFFFF" }) ?? "#FFFFFF"
            : Darkest(new[] { sc.Str("fg"), sc.Str("ink"), "#111111" }) ?? "#111111";

    private static void RegisterFocusLines() => Reg.Register("fx", "focusLines", new FxDef
    {
        Name = "集中線",
        Tags = new[] { "pop", "graphic", "emotional" },
        W = 0.9,
        Dur = 6,
        Amp = 1,
        Mid = true,
        Draw = Wrapped(DrawFocusLines),
    }, Pk);

    private static void DrawFocusLines(Env env, PlanEvent ev, double k)
    {
        var cw = FxPlug.Cw;
        var ch = FxPlug.Ch;
        var sc = env.Sc!;
        var ctx = env.Ctx;
        var s = EvS(ev);
        var st = ToI(env.Step * 5 + s);
        var a = Ahr(k, 0.1, 0.72);
        if (a < 0.02) return;
        var cx = cw / 2.0 + J.Rs(ToI(s), 1) * cw * 0.03;
        var cy = ch / 2.0 + J.Rs(ToI(s), 2) * ch * 0.03;
        var m = Math.Min(cw, ch);
        var rx = cw * (0.4 + 0.06 * (1 - a));
        var ry = ch * (0.34 + 0.06 * (1 - a));
        var r = Math.Sqrt(cw * (double)cw + ch * (double)ch) * 0.75;
        var n = 90 + (int)(J.H(ToI(s), 3) % 50);
        using var p = new SKPaint { IsAntialias = true, Color = Env.SkColor(InkCol(sc), 0.85 * a) };
        using var path = new SKPath();
        for (var i = 0; i < n; i++)
        {
            var an = (i + J.R(st, i, 4) * 0.8) / n * J.TAU;
            var w = m * J.Rr(0.002, 0.011, st, i, 5);
            var tip = J.Rr(1.0, 1.45, st, i, 6);
            var tx = cx + Math.Cos(an) * rx * tip;
            var ty = cy + Math.Sin(an) * ry * tip;
            var nx = -Math.Sin(an);
            var ny = Math.Cos(an);
            var ox = cx + Math.Cos(an) * r;
            var oy = cy + Math.Sin(an) * r;
            path.MoveTo((float)tx, (float)ty);
            path.LineTo((float)(ox + nx * w), (float)(oy + ny * w));
            path.LineTo((float)(ox - nx * w), (float)(oy - ny * w));
            path.Close();
        }
        ctx.DrawPath(path, p);
    }

    /// <summary>流線：横穿画面的尖尾速度线，避开中央条带。JS L946。</summary>
    private static void RegisterSpeedLines() => Reg.Register("fx", "speedLines", new FxDef
    {
        Name = "流線",
        Tags = new[] { "pop", "graphic" },
        W = 0.8,
        Dur = 6,
        Amp = 1,
        Mid = true,
        Draw = Wrapped(DrawSpeedLines),
    }, Pk);

    private static void DrawSpeedLines(Env env, PlanEvent ev, double k)
    {
        var cw = FxPlug.Cw;
        var ch = FxPlug.Ch;
        var sc = env.Sc!;
        var ctx = env.Ctx;
        var s = EvS(ev);
        var a = Ahr(k, 0.12, 0.7);
        if (a < 0.02) return;
        var vert = cw < ch ? J.R(ToI(s), 1) < 0.5 : J.R(ToI(s), 1) < 0.15;
        var dir = J.R(ToI(s), 2) < 0.5 ? 1 : -1;
        var l = vert ? ch : cw;
        var m = vert ? cw : ch;
        var n = 22 + (int)(J.H(ToI(s), 3) % 12);
        var c1 = InkCol(sc);
        var c2 = Vivid(sc);
        for (var i = 0; i < n; i++)
        {
            var q = J.R(ToI(s), i, 4);
            if (Math.Abs(q - 0.5) < 0.12 && J.R(ToI(s), i, 9) < 0.8)
                q = q < 0.5 ? 0.38 - J.R(ToI(s), i, 10) * 0.33 : 0.62 + J.R(ToI(s), i, 10) * 0.33;
            var pos = q * m;
            var len = l * J.Rr(0.15, 0.55, ToI(s), i, 5);
            var th = Math.Max(1, m * J.Rr(0.002, 0.008, ToI(s), i, 6));
            var sp2 = J.Rr(1.3, 2.6, ToI(s), i, 7);
            var f = (J.R(ToI(s), i, 8) + k * sp2) % 1;
            var head = -len * 0.2 + f * (l + len * 1.2);
            var tail = head - len;
            var h = dir > 0 ? head : l - head;
            var tl = dir > 0 ? tail : l - tail;
            using var p = new SKPaint
            {
                IsAntialias = true,
                Color = Env.SkColor(J.R(ToI(s), i, 12) < 0.22 ? c2 : c1, a * J.Rr(0.4, 0.9, ToI(s), i, 11)),
            };
            using var path = new SKPath();
            if (!vert)
            {
                path.MoveTo((float)tl, (float)pos);
                path.LineTo((float)h, (float)(pos - th / 2));
                path.LineTo((float)h, (float)(pos + th / 2));
            }
            else
            {
                path.MoveTo((float)pos, (float)tl);
                path.LineTo((float)(pos - th / 2), (float)h);
                path.LineTo((float)(pos + th / 2), (float)h);
            }
            path.Close();
            ctx.DrawPath(path, p);
        }
    }

    /// <summary>キラッ：歌词带周围的八角星光依次弹出。JS L979。</summary>
    private static void RegisterStarGlint() => Reg.Register("fx", "starGlint", new FxDef
    {
        Name = "キラッ",
        Tags = new[] { "pop", "emotional" },
        W = 0.8,
        Dur = 9,
        Amp = 1,
        Mid = true,
        Draw = Wrapped(DrawStarGlint),
    }, Pk);

    private static void DrawStarGlint(Env env, PlanEvent ev, double k)
    {
        var cw = FxPlug.Cw;
        var ch = FxPlug.Ch;
        var sc = env.Sc!;
        var ctx = env.Ctx;
        var s = EvS(ev);
        var dk = IsDark(ScK(sc, "bg"));
        var m = Math.Min(cw, ch);
        var col = Vivid(sc);
        var n = 2 + (int)(J.H(ToI(s), 1) % 3);
        var port = ch > cw;
        for (var i = 0; i < n; i++)
        {
            var t0 = i * 0.16 + J.R(ToI(s), i, 4) * 0.06;
            var u = (k - t0) / 0.55;
            if (u <= 0 || u >= 1) continue;
            var g = Math.Pow(Math.Sin(Math.PI * u), 0.8);
            var r = m * (0.1 + 0.07 * J.R(ToI(s), i, 5)) * g * J.Clamp(AmpOf(ev), 0.6, 1.3);
            var x = cw * (0.5 + J.Rs(ToI(s), i, 2) * (port ? 0.3 : 0.34));
            var y = ch * (0.5 + J.Rs(ToI(s), i, 3) * (port ? 0.14 : 0.1));
            var rot = (J.Rs(ToI(s), i, 6) * 12 + u * 30) * J.DEG;
            if (r < 0.5) continue;
            using (var glow = SKShader.CreateRadialGradient(new SKPoint((float)x, (float)y), (float)(r * 0.6),
                       new[] { Env.SkColor(col, 0.55 * g), Env.SkColor(col, 0) }, new float[] { 0, 1 }, SKShaderTileMode.Clamp))
            using (var gp = new SKPaint { Shader = glow, BlendMode = dk ? SKBlendMode.Screen : SKBlendMode.SrcOver })
                ctx.DrawRect(new SKRect((float)(x - r), (float)(y - r), (float)(x + r), (float)(y + r)), gp);
            using (var p = new SKPaint { IsAntialias = true, Color = dk ? SKColors.White : Env.SkColor(col) })
            using (var path = new SKPath())
            {
                for (var j = 0; j < 16; j++)
                {
                    var an = rot + j * Math.PI / 8;
                    var rr = j % 2 == 1 ? r * 0.07 : j % 4 == 0 ? r : r * 0.42;
                    if (j == 0) path.MoveTo((float)(x + Math.Cos(an) * rr), (float)(y + Math.Sin(an) * rr));
                    else path.LineTo((float)(x + Math.Cos(an) * rr), (float)(y + Math.Sin(an) * rr));
                }
                path.Close();
                ctx.DrawPath(path, p);
            }
            using (var dp = new SKPaint { Color = SKColors.White })
                ctx.DrawCircle((float)x, (float)y, (float)(r * 0.08), dp);
        }
    }

    /// <summary>细色条以不同速度扫过画面。JS L1017。</summary>
    private static void RegisterColorBars() => Reg.Register("fx", "colorBars", new FxDef
    {
        Name = "カラーバー",
        Tags = new[] { "pop", "graphic", "glitch" },
        W = 0.8,
        Dur = 6,
        Amp = 1,
        Mid = true,
        Draw = Wrapped(DrawColorBars),
    }, Pk);

    private static void DrawColorBars(Env env, PlanEvent ev, double k)
    {
        var cw = FxPlug.Cw;
        var ch = FxPlug.Ch;
        var sc = env.Sc!;
        var ctx = env.Ctx;
        var s = EvS(ev);
        var vert = J.R(ToI(s), 1) < (cw >= ch ? 0.35 : 0.15);
        var dir = J.R(ToI(s), 2) < 0.5 ? 1 : -1;
        var cols = new[] { sc.Str("accent"), sc.Str("accent2"), sc.Str("ghostA"), sc.Str("ghostB"), sc.Str("fg") }
            .Where(c => !string.IsNullOrEmpty(c) && J.Contrast(c, ScK(sc, "bg")) > 1.35).ToList();
        if (cols.Count == 0) cols = new List<string> { InkCol(sc) };
        var l = vert ? cw : ch;
        var m = vert ? ch : cw;
        var n = 4 + (int)(J.H(ToI(s), 3) % 4);
        for (var i = 0; i < n; i++)
        {
            var th = Math.Max(2, l * J.Rr(0.008, 0.045, ToI(s), i, 4));
            var d = J.R(ToI(s), i, 5) * 0.4;
            var sp2 = J.Rr(0.9, 1.5, ToI(s), i, 6);
            var u = J.Clamp((k - d) / (1 - d) * sp2);
            if (u <= 0 || u >= 1) continue;
            var pos = J.Lerp(-th, l + th, u);
            if (dir < 0) pos = l - pos;
            var part = J.R(ToI(s), i, 7) < 0.4;
            var m0 = part ? m * J.Rr(0, 0.5, ToI(s), i, 8) : 0;
            var mw = part ? m * J.Rr(0.3, 0.6, ToI(s), i, 9) : m;
            var col = cols[(int)((i + (int)(J.H(ToI(s), 10) % cols.Count)) % cols.Count)];
            if (vert) FillMode(ctx, pos - th / 2, m0, th, mw, col, 0.92);
            else FillMode(ctx, m0, pos - th / 2, mw, th, col, 0.92);
        }
    }

    /// <summary>三段硬变焦（ダダダン），每段焦点略偏，最后弹回。JS L1042。</summary>
    private static void RegisterZoomStutter() => Reg.Register("fx", "zoomStutter", new FxDef
    {
        Name = "ズーム連打",
        Tags = new[] { "pop", "graphic", "glitch" },
        W = 0.8,
        Dur = 6,
        Amp = 1,
        Mid = true,
        Draw = Wrapped(DrawZoomStutter),
    }, Pk);

    private static void DrawZoomStutter(Env env, PlanEvent ev, double k)
    {
        var S = FxPlug.S;
        var cw = FxPlug.Cw;
        var ch = FxPlug.Ch;
        if (S == null) return;
        var ctx = env.Ctx;
        var s = EvS(ev);
        var j = Math.Min(2, (int)Math.Floor(k * 3));
        var a = J.Clamp(AmpOf(ev), 0.5, 1.3);
        var z = 1 + (j + 1) * (0.035 + 0.012 * J.R(ToI(s), 1)) * a;
        using var simg = S.Snapshot();
        DrawScaled(ctx, simg, z, cw * (0.5 + J.Rs(ToI(s), 2, j) * 0.04), ch * (0.5 + J.Rs(ToI(s), 3, j) * 0.04), cw, ch);
    }

    /// <summary>反转环（圆或菱形）从中心爆出，随后一圈更细的回声环。JS L1061。</summary>
    private static void RegisterNegativeRing() => Reg.Register("fx", "negativeRing", new FxDef
    {
        Name = "反転リング",
        Tags = new[] { "graphic", "pop", "glitch" },
        W = 0.8,
        Dur = 6,
        Amp = 1,
        Mid = true,
        Draw = Wrapped(DrawNegativeRing),
    }, Pk);

    private static void DrawNegativeRing(Env env, PlanEvent ev, double k)
    {
        var cw = FxPlug.Cw;
        var ch = FxPlug.Ch;
        var ctx = env.Ctx;
        var s = EvS(ev);
        var m = Math.Min(cw, ch);
        var dia = J.R(ToI(s), 1) < 0.35;
        var cx = cw / 2.0 + J.Rs(ToI(s), 2) * cw * 0.05;
        var cy = ch / 2.0 + J.Rs(ToI(s), 3) * ch * 0.05;
        var rm = Math.Sqrt(cw * (double)cw + ch * (double)ch) * (dia ? 0.75 : 0.56);

        void Shape(SKPath path, double r)
        {
            if (dia)
            {
                path.MoveTo((float)cx, (float)(cy - r));
                path.LineTo((float)(cx + r), (float)cy);
                path.LineTo((float)cx, (float)(cy + r));
                path.LineTo((float)(cx - r), (float)cy);
                path.Close();
            }
            else
            {
                path.MoveTo((float)(cx + r), (float)cy);
                path.AddCircle((float)cx, (float)cy, (float)r);
            }
        }

        void Ring(double p, double th)
        {
            if (p <= 0 || p >= 1) return;
            var r = J.E.OutCubic(p) * rm;
            var r0 = Math.Max(0, r - th * (1 - 0.55 * p));
            using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
            Shape(path, r);
            if (r0 > 0.5) Shape(path, r0);
            using var pnt = new SKPaint { IsAntialias = true, Color = SKColors.White, BlendMode = SKBlendMode.Difference };
            ctx.DrawPath(path, pnt);
        }

        var a = J.Clamp(AmpOf(ev), 0.6, 1.3);
        Ring(k * 1.05 + 0.04, m * 0.16 * a);
        Ring((k - 0.22) * 1.35, m * 0.05 * a);
    }

    /// <summary>边缘检测：|帧-偏移帧| → 暗底霓虹描边、亮底墨线画。JS L1096。</summary>
    private static string InvHex(string h)
    {
        var (r, g, b) = J.Hex(h);
        return J.ToHex(255 - r, 255 - g, 255 - b);
    }

    private static void RegisterEdgeDetect() => Reg.Register("fx", "edgeDetect", new FxDef
    {
        Name = "輪郭抽出",
        Tags = new[] { "graphic", "glitch", "editorial" },
        W = 0.7,
        Dur = 4,
        Amp = 1,
        Mid = true,
        Draw = Wrapped(DrawEdgeDetect),
    }, Pk);

    private static void DrawEdgeDetect(Env env, PlanEvent ev, double k)
    {
        var S = FxPlug.S;
        var cw = FxPlug.Cw;
        var ch = FxPlug.Ch;
        var sc = env.Sc!;
        if (S == null) return;
        var ctx = env.Ctx;
        var dk = IsDark(ScK(sc, "bg"));
        var a = Ahr(k, 0.1, 0.72);
        if (a < 0.02) return;
        var w = Math.Max(2, (int)Math.Round(cw / 2.0));
        var h = Math.Max(2, (int)Math.Round(ch / 2.0));
        var o = Math.Max(1, (int)Math.Round(h / 320.0));
        var t = Buf(0, w, h);
        var x = t.Canvas;
        x.SetMatrix(SKMatrix.Identity);
        x.Clear();
        using (var simg = S.Snapshot())
        {
            using (var p = new SKPaint())
                x.DrawImage(simg, new SKRect(0, 0, w, h), SmpL, p);
            using (var dp = new SKPaint())
                x.DrawImage(simg, new SKRect(0, 0, cw, ch), new SKRect(o, o, o + w, o + h), SmpL, dp);
        }
        FillMode(x, 0, 0, w, h, "#808080", 1, SKBlendMode.Saturation);
        var t2 = Buf(1, w, h);
        var y = t2.Canvas;
        y.SetMatrix(SKMatrix.Identity);
        y.Clear();
        // 噪底：浅灰 color-burn 即 max(0,(v-0.07)/0.93)，去掉抖动渐变的台阶；之后增益 ×4
        using (var tim = t.Snapshot())
        using (var cp = new SKPaint())
            y.DrawImage(tim, 0, 0, cp);
        FillMode(y, 0, 0, w, h, "#EDEDED", 1, SKBlendMode.ColorBurn);
        using (var t2imgSelf = t2.Snapshot())
        using (var lp = new SKPaint { BlendMode = SKBlendMode.Plus })
        {
            y.DrawImage(t2imgSelf, 0, 0, lp);
            y.DrawImage(t2imgSelf, 0, 0, lp);
        }
        var edge = dk ? J.Mix(Vivid(sc), "#ffffff", 0.45) : Darkest(new[] { sc.Str("fg"), sc.Str("ink"), "#111111" }) ?? "#111111";
        // 半分辨率上色后一次放大：暗底压暗衬托霓虹线 / 亮底纸色上的墨线
        FillMode(y, 0, 0, w, h, dk ? edge : InvHex(edge), 1, SKBlendMode.Multiply);
        if (dk) FillMode(y, 0, 0, w, h, J.Mix(ScK(sc, "bg"), "#000000", 0.6), 1, SKBlendMode.Screen);
        else
        {
            FillMode(y, 0, 0, w, h, "#ffffff", 1, SKBlendMode.Difference);
            FillMode(y, 0, 0, w, h, ScK(sc, "bg"), 1, SKBlendMode.Multiply);
        }
        using (var outImg = t2.Snapshot())
            DrawImg9(ctx, outImg, 0, 0, w, h, 0, 0, cw, ch, alpha: a);
    }

    /// <summary>玻璃碎裂：旧帧先出裂纹，新帧以碎片漂离再拼回。JS L1157。</summary>
    private static void RegisterShatter() => Reg.Register("fx", "shatter", new FxDef
    {
        Name = "ガラス割れ",
        Tags = new[] { "glitch", "pop", "emotional" },
        W = 0.5,
        Dur = 8,
        Pre = 1,
        Amp = 1,
        Draw = Wrapped(DrawShatter),
    }, Pk);

    private sealed class Shard
    {
        public List<(double X, double Y)> Pts = new();
        public double Cxm, Cym, Ox, Oy, Rot;
    }

    private static void DrawShatter(Env env, PlanEvent ev, double k)
    {
        var S = FxPlug.S;
        var cw = FxPlug.Cw;
        var ch = FxPlug.Ch;
        var sc = env.Sc!;
        if (S == null) return;
        var ctx = env.Ctx;
        var s = EvS(ev);
        var m = Math.Min(cw, ch);
        var dk = IsDark(ScK(sc, "bg"));
        var a = J.Clamp(AmpOf(ev), 0.6, 1.3);
        var px = cw * (0.5 + J.Rs(ToI(s), 1) * 0.15);
        var py = ch * (0.5 + J.Rs(ToI(s), 2) * 0.12);
        var n = 9 + (int)(J.H(ToI(s), 3) % 4);
        var radii = new[] { 0.0, m * J.Rr(0.1, 0.17, ToI(s), 4), m * J.Rr(0.32, 0.45, ToI(s), 5), Math.Sqrt(cw * (double)cw + ch * (double)ch) * 1.1 };
        var ang = new double[n];
        for (var i = 0; i < n; i++) ang[i] = (i + J.Rs(ToI(s), i, 6) * 0.35) / n * J.TAU;

        (double X, double Y) V(int i, int j)
        {
            if (j == 0) return (px, py);
            var q = ang[i % n] + J.Rs(ToI(s), i % n, j, 7) * 0.12;
            var r = radii[j] * (j < 3 ? 1 + J.Rs(ToI(s), i % n, j, 8) * 0.18 : 1);
            return (px + Math.Cos(q) * r, py + Math.Sin(q) * r);
        }

        const double b = 1 / 8.0;
        var u = (k - b) / (1 - b);
        var sep = u <= 0 ? 0 : u < 0.28 ? J.E.OutCubic(u / 0.28) : 1 - J.E.InOutCubic((u - 0.28) / 0.72);
        var crack = u <= 0 ? 1 : Math.Max(0, 1 - u * 1.4);
        var shards = new List<Shard>();
        for (var i = 0; i < n; i++)
        for (var j = 0; j < 3; j++)
        {
            var pts = j == 0
                ? new[] { V(i, 0), V(i, 1), V(i + 1, 1) }
                : new[] { V(i, j), V(i + 1, j), V(i + 1, j + 1), V(i, j + 1) };
            var cxm = pts.Sum(p => p.X) / pts.Length;
            var cym = pts.Sum(p => p.Y) / pts.Length;
            var dx = cxm - px;
            var dy = cym - py;
            var dl = Math.Sqrt(dx * dx + dy * dy);
            if (dl < 1) dl = 1;
            var d = sep * m * (dk ? 0.04 : 0.028) * a * (0.5 + 0.3 * j + 0.5 * J.R(ToI(s), i, j, 9));
            shards.Add(new Shard
            {
                Pts = pts.Select(p => (p.X, p.Y)).ToList(),
                Cxm = cxm,
                Cym = cym,
                Ox = dx / dl * d,
                Oy = dy / dl * d,
                Rot = sep * J.Rs(ToI(s), i, j, 10) * 4 * J.DEG,
            });
        }
        using var simg = S.Snapshot();
        if (sep > 0.002)
        {
            FillMode(ctx, 0, 0, cw, ch, dk ? "#000000" : J.Mix(ScK(sc, "bg"), "#000000", 0.55));
            foreach (var sh in shards)
            {
                ctx.Save();
                ctx.Translate((float)(sh.Cxm + sh.Ox), (float)(sh.Cym + sh.Oy));
                ctx.RotateRadians((float)sh.Rot);
                ctx.Translate((float)-sh.Cxm, (float)-sh.Cym);
                using (var clip = new SKPath())
                {
                    clip.MoveTo((float)sh.Pts[0].X, (float)sh.Pts[0].Y);
                    for (var i = 1; i < sh.Pts.Count; i++) clip.LineTo((float)sh.Pts[i].X, (float)sh.Pts[i].Y);
                    clip.Close();
                    ctx.ClipPath(clip, antialias: true);
                }
                DrawImg(ctx, simg, 0, 0);
                ctx.Restore();
            }
        }
        var la = Math.Max(crack, sep * 0.6);
        if (la > 0.02)
        {
            using var sp = new SKPaint
            {
                IsAntialias = true,
                IsStroke = true,
                StrokeWidth = (float)Math.Max(1, m * 0.0022),
                StrokeJoin = SKStrokeJoin.Round,
                Color = dk ? Env.SkColor("#ffffff", 0.75 * la)
                    : Env.SkColor(Darkest(new[] { sc.Str("fg"), sc.Str("ink") }) ?? "#111111", 0.6 * la),
            };
            using var path = new SKPath();
            foreach (var sh in shards)
            {
                var c = Math.Cos(sh.Rot);
                var si = Math.Sin(sh.Rot);
                for (var i = 0; i < sh.Pts.Count; i++)
                {
                    var p0 = sh.Pts[i];
                    var x = sh.Cxm + sh.Ox + (p0.X - sh.Cxm) * c - (p0.Y - sh.Cym) * si;
                    var y = sh.Cym + sh.Oy + (p0.X - sh.Cxm) * si + (p0.Y - sh.Cym) * c;
                    if (i == 0) path.MoveTo((float)x, (float)y);
                    else path.LineTo((float)x, (float)y);
                }
                path.Close();
            }
            ctx.DrawPath(path, sp);
        }
        if (k < b * 1.5) // 撞击闪光
        {
            var f = 1 - k / (b * 1.5);
            using var g = SKShader.CreateRadialGradient(new SKPoint((float)px, (float)py), (float)(m * 0.25),
                new[] { SKColors.White.WithAlpha((byte)Math.Round(0.85 * f * 255)), SKColors.Transparent },
                new float[] { 0, 1 }, SKShaderTileMode.Clamp);
            using var p = new SKPaint { Shader = g, BlendMode = dk ? SKBlendMode.Screen : SKBlendMode.SrcOver };
            ctx.DrawRect(new SKRect((float)(px - m * 0.25), (float)(py - m * 0.25), (float)(px + m * 0.25), (float)(py + m * 0.25)), p);
        }
    }

    /// <summary>变焦虚焦：画面在切点失焦到峰值，再拉回。JS L1237。</summary>
    private static void RegisterDefocus() => Reg.Register("fx", "defocus", new FxDef
    {
        Name = "ピンぼけ",
        Tags = new[] { "emotional", "calm", "editorial" },
        W = 0.9,
        Dur = 8,
        Pre = 4,
        Amp = 1,
        Mid = true,
        Draw = Wrapped(DrawDefocus),
    }, Pk);

    private static void DrawDefocus(Env env, PlanEvent ev, double k)
    {
        var S = FxPlug.S;
        var cw = FxPlug.Cw;
        var ch = FxPlug.Ch;
        if (S == null) return;
        var ctx = env.Ctx;
        var amt = (k < 0.5 ? J.E.InOutCubic(k / 0.5) : 1 - J.E.InOutCubic((k - 0.5) / 0.5)) * J.Clamp(AmpOf(ev), 0.6, 1.2);
        if (amt < 0.01) return;
        var w = Math.Max(2, (int)Math.Ceiling(cw / 4.0));
        var h = Math.Max(2, (int)Math.Ceiling(ch / 4.0));
        var t = Buf(0, w, h);
        var x = t.Canvas;
        x.SetMatrix(SKMatrix.Identity);
        x.Clear();
        if (env.AllowFilter)
        {
            using (var bp = new SKPaint { ImageFilter = JText.BlurFilter(amt * h / 55.0) })
            using (var simg = S.Snapshot())
                x.DrawImage(simg, new SKRect(0, 0, w, h), SmpL, bp);
        }
        else
        {
            var w2 = Math.Max(2, (int)Math.Ceiling(w / (1 + 3 * amt)));
            var h2 = Math.Max(2, (int)Math.Ceiling(h / (1 + 3 * amt)));
            var t2 = Buf(1, w2, h2);
            var y = t2.Canvas;
            y.SetMatrix(SKMatrix.Identity);
            y.Clear();
            using (var simg = S.Snapshot())
            using (var p = new SKPaint())
                y.DrawImage(simg, new SKRect(0, 0, w2, h2), SmpL, p);
            using (var i2 = t2.Snapshot())
            using (var p = new SKPaint())
                x.DrawImage(i2, new SKRect(0, 0, w, h), SmpL, p);
        }
        using (var tim = t.Snapshot())
            DrawScaled(ctx, tim, 1 + 0.025 * amt, cw / 2.0, ch / 2.0, cw, ch, Math.Min(1, amt * 1.8));
    }

    /// <summary>快门：白闪，画面变成暗底上斜置的拍立得，再变焦回位。JS L1271。</summary>
    private static void RegisterSnapshot() => Reg.Register("fx", "snapshot", new FxDef
    {
        Name = "シャッター",
        Tags = new[] { "pop", "emotional", "editorial" },
        W = 0.6,
        Dur = 10,
        Amp = 1,
        Mid = true,
        Draw = Wrapped(DrawSnapshot),
    }, Pk);

    private static void DrawSnapshot(Env env, PlanEvent ev, double k)
    {
        var S = FxPlug.S;
        var cw = FxPlug.Cw;
        var ch = FxPlug.Ch;
        var sc = env.Sc!;
        if (S == null) return;
        var ctx = env.Ctx;
        var s = EvS(ev);
        var m = Math.Min(cw, ch);
        var e = k < 0.16 ? J.E.OutBack(k / 0.16, 1.4) : k > 0.8 ? 1 - J.E.InOutCubic((k - 0.8) / 0.2) : 1;
        if (e > 0.002)
        {
            var z = 1 - 0.15 * e;
            var rot = (J.R(ToI(s), 1) < 0.5 ? -1 : 1) * (2 + 2.5 * J.R(ToI(s), 2)) * e * J.DEG;
            var bw = m * 0.024 * J.Clamp(e, 0, 1);
            var fw = cw * z;
            var fh = ch * z;
            var bb = bw * 3.2;
            using (var simg = S.Snapshot())
            {
                ctx.Save();
                FillMode(ctx, 0, 0, cw, ch, J.Mix(ScK(sc, "bg"), "#000000", IsDark(ScK(sc, "bg")) ? 0.5 : 0.45), J.Clamp(e * 1.2));
                ctx.Translate((float)(cw / 2.0), (float)(ch / 2.0));
                ctx.RotateRadians((float)rot);
                FillMode(ctx, -fw / 2 - bw + m * 0.012, -fh / 2 - bw + m * 0.02, fw + 2 * bw, fh + bw + bb,
                    "#000000", 0.3 * e);
                FillMode(ctx, -fw / 2 - bw, -fh / 2 - bw, fw + 2 * bw, fh + bw + bb, "#F7F5F0");
                ctx.DrawImage(simg, new SKRect((float)(-fw / 2), (float)(-fh / 2), (float)(-fw / 2 + fw), (float)(-fh / 2 + fh)),
                    SmpL, ImgPaint(1));
                ctx.Restore();
            }
        }
        if (k < 0.22)
            FillMode(ctx, 0, 0, cw, ch, "#ffffff", 0.9 * (1 - k / 0.22));
    }

    /// <summary>整帧的弹性挤压拉伸（阻尼弹簧）。JS L1309。</summary>
    private static void RegisterSquash() => Reg.Register("fx", "squash", new FxDef
    {
        Name = "伸縮",
        Tags = new[] { "pop", "graphic" },
        W = 0.8,
        Dur = 6,
        Amp = 1,
        Mid = true,
        Draw = Wrapped(DrawSquash),
    }, Pk);

    private static void DrawSquash(Env env, PlanEvent ev, double k)
    {
        var S = FxPlug.S;
        var cw = FxPlug.Cw;
        var ch = FxPlug.Ch;
        var sc = env.Sc!;
        if (S == null) return;
        var ctx = env.Ctx;
        var s = EvS(ev);
        var hor = J.R(ToI(s), 1) < 0.6;
        var e = Math.Cos(k * Math.PI * 2.5) * Math.Exp(-1.8 * k) * (1 - Math.Pow(k, 6));
        var a = 0.17 * J.Clamp(AmpOf(ev), 0.5, 1.3) * e;
        if (Math.Abs(a) < 0.002) return;
        var sx = hor ? 1 + a : 1 - a * 0.6;
        var sy = hor ? 1 - a * 0.6 : 1 + a;
        FillMode(ctx, 0, 0, cw, ch, ScK(sc, "bg"));
        using (var simg = S.Snapshot())
            ctx.DrawImage(simg, new SKRect((float)(cw / 2.0 - cw * sx / 2), (float)(ch / 2.0 - ch * sy / 2),
                (float)(cw / 2.0 + cw * sx / 2), (float)(ch / 2.0 + ch * sy / 2)), SmpL, ImgPaint(1));
    }

    /// <summary>扫描：发光条扫过画面，未扫到的部分压暗，条后沿的行抖动。JS L1326。</summary>
    private static void RegisterScanBar() => Reg.Register("fx", "scanBar", new FxDef
    {
        Name = "スキャン",
        Tags = new[] { "graphic", "editorial", "glitch" },
        W = 0.7,
        Dur = 8,
        Amp = 1,
        Mid = true,
        Draw = Wrapped(DrawScanBar),
    }, Pk);

    private static void DrawScanBar(Env env, PlanEvent ev, double k)
    {
        var S = FxPlug.S;
        var cw = FxPlug.Cw;
        var ch = FxPlug.Ch;
        var sc = env.Sc!;
        if (S == null) return;
        var ctx = env.Ctx;
        var s = EvS(ev);
        var dk = IsDark(ScK(sc, "bg"));
        var down = J.R(ToI(s), 1) < 0.7;
        var st = ToI(env.Step * 3 + s);
        var bh = ch * 0.05;
        var yy = -bh + k * (ch + 2 * bh);
        var y = down ? yy : ch - yy;
        var y0 = down ? Math.Max(0, y) : 0;
        var y1 = down ? ch : Math.Min(ch, y);
        if (y1 > y0)
            FillMode(ctx, 0, y0, cw, y1 - y0, dk ? "rgba(0,0,0,0.6)" : ScK(sc, "bg"), dk ? 1 : 0.7);
        var band = ch * 0.035;
        var sy = J.Clamp(down ? y - band : y, 0, ch - 1);
        var sh = Math.Min(band, ch - sy);
        if (sh > 1)
        {
            using var simg = S.Snapshot();
            for (var i = 0; i < 3; i++)
            {
                var hh = sh / 3;
                var yy2 = sy + i * hh;
                DrawImg9(ctx, simg, 0, yy2, cw, hh, J.Rs(st, i, 2) * cw * 0.012, yy2, cw, hh);
            }
        }
        var col = dk ? J.Mix(Vivid(sc), "#ffffff", 0.4) : Vivid(sc);
        using (var shader = SKShader.CreateLinearGradient(new SKPoint(0, (float)(y - bh)), new SKPoint(0, (float)(y + bh)),
                   new[] { Env.SkColor(col, 0), Env.SkColor(col, dk ? 0.5 : 0.35), Env.SkColor(col, 0) },
                   new float[] { 0, 0.5f, 1 }, SKShaderTileMode.Clamp))
        using (var p = new SKPaint { Shader = shader, BlendMode = dk ? SKBlendMode.Screen : SKBlendMode.Multiply })
            ctx.DrawRect(new SKRect(0, (float)(y - bh), (float)cw, (float)(y + bh)), p);
        FillMode(ctx, 0, y - Math.Max(1, ch * 0.0015), cw, Math.Max(2, ch * 0.003), dk ? "#FFFFFF" : col, 0.9);
    }

    /// <summary>整帧滚动一整屏宽（回绕）带运动模糊，终点恰为起点。JS L1366。</summary>
    private static void RegisterLoopScroll() => Reg.Register("fx", "loopScroll", new FxDef
    {
        Name = "横ループ",
        Tags = new[] { "pop", "graphic", "glitch" },
        W = 0.7,
        Dur = 6,
        Pre = 3,
        Amp = 1,
        Draw = Wrapped(DrawLoopScroll),
    }, Pk);

    private static void DrawLoopScroll(Env env, PlanEvent ev, double k)
    {
        var S = FxPlug.S;
        var cw = FxPlug.Cw;
        var ch = FxPlug.Ch;
        var sc = env.Sc!;
        if (S == null) return;
        var ctx = env.Ctx;
        var s = EvS(ev);
        var vert = cw < ch ? J.R(ToI(s), 1) < 0.6 : J.R(ToI(s), 1) < 0.2;
        var dir = J.R(ToI(s), 2) < 0.5 ? 1 : -1;
        var l = vert ? ch : cw;
        var q = J.E.InOutCubic(k);
        var o = ((q * l * dir) % l + l) % l;
        var v = k < 0.5 ? 12 * k * k : 12 * (1 - k) * (1 - k); // d(inOutCubic)/dk, 0..3
        var blur = l * 0.05 * v / 3;

        void Wrap(SKCanvas c, SKImage img, double off, double w, double h)
        {
            if (vert)
            {
                DrawImg(c, img, 0, off - h);
                DrawImg(c, img, 0, off);
            }
            else
            {
                DrawImg(c, img, off - w, 0);
                DrawImg(c, img, off, 0);
            }
        }

        using var simg = S.Snapshot();
        if (blur < 2)
        {
            Wrap(ctx, simg, o, cw, ch);
            return;
        }
        var w = Math.Max(2, (int)Math.Round(cw / 2.0));
        var h = Math.Max(2, (int)Math.Round(ch / 2.0));
        var t = Buf(0, w, h);
        var x = t.Canvas;
        x.SetMatrix(SKMatrix.Identity);
        x.Clear();
        FillMode(x, 0, 0, w, h, ScK(sc, "bg"));
        Wrap(x, simg, o / 2, w, h);
        var t2 = Buf(1, w, h);
        var y2 = t2.Canvas;
        y2.SetMatrix(SKMatrix.Identity);
        y2.Clear();
        using (var tim = t.Snapshot())
            DrawImg(y2, tim, 0, 0);
        const int n = 6;
        using (var tim2 = t.Snapshot())
            for (var i = 1; i < n; i++)
            {
                var d = (i / (double)(n - 1) - 0.5) * blur / 2 * dir;
                var alpha = 1 / (double)(i + 1);
                if (vert)
                {
                    DrawImg(y2, tim2, 0, d, alpha);
                    DrawImg(y2, tim2, 0, d - Math.Sign(d == 0 ? 1 : d) * h, alpha);
                }
                else
                {
                    DrawImg(y2, tim2, d, 0, alpha);
                    DrawImg(y2, tim2, d - Math.Sign(d == 0 ? 1 : d) * w, 0, alpha);
                }
            }
        using (var o2 = t2.Snapshot())
            DrawImg9(ctx, o2, 0, 0, w, h, 0, 0, cw, ch);
    }

    // ================= Renderer → 插件桥（见文件头注释） =================

    /// <summary>Renderer.Post 的 FXE 插件段在 def.Draw(env, ev) 前调用；s = 渲染器 scratch 面。
    /// public：对拍/冒烟测试直调插件 Draw 时也要装桥（引擎测试工程无 InternalsVisibleTo）。</summary>
    public static void FxBegin(int cw, int ch, double k, SKSurface? s,
        Func<int, int, SKSurface> tmp, Func<int, int, SKSurface> tmp2, bool transparent)
    {
        FxPlug.Cw = cw;
        FxPlug.Ch = ch;
        FxPlug.K = k;
        FxPlug.S = s;
        FxPlug.Tmp = tmp;
        FxPlug.Tmp2 = tmp2;
        FxPlug.Transparent = transparent;
    }

    /// <summary>finally 里清桥（S 快照由调用方的 using 释放）。</summary>
    public static void FxEnd()
    {
        FxPlug.S = null;
        FxPlug.Tmp = null;
        FxPlug.Tmp2 = null;
    }
}
