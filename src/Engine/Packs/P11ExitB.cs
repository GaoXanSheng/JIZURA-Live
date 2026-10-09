// 移植自 frontend/src/engine/11p_exitB.js（exit/hold 表现包第二批：exit 39 条 + hold 12 条，pack = "exitB"）。
//
// 契约与 P11Exit.cs 相同（MoveDef.Draw(env)：it = env.It、p = env.Ap；逐字推 env.CharFns、
// 逐片推 env.PieceFns；字段直接写 item）。随机数全部是 J.R/J.Rs/J.Rr/J.H/J.Noise1 确定性哈希
// （无顺序流），哈希入参逐字保持——同 seed 同动画轨迹。
//
// 管线缺口与降级（本文件内解决，不改共享文件）：
// 1. JS 的 merged/addC 自带合并：C# JAnim.CombineChar 全字段合并，语义等价（DrawItem 侧
//    a<=0.002 即跳过），不需要。
// 2. it.post/it.pre（chainPost/chainPre）：JLayouts.DrawFx 已接（Env.PreFn/PostFn，pre 画在文字
//    前、post 画在文字后）。本包仍在 Draw 调用点内就地执行——内容/几何一致，层级在主文字
//    下方（可回迁到 env.PreFn/PostFn）。文字副本（drawFx/drawItem 的 copyOf）经 DrawFxCopy/
//    DrawCopy 就地画。
// 3. it.clipFn（任意路径裁剪，peelOff/tearOut/scorchOut/scanOut/stripesOut/halftoneOut/eraserOut/
//    clockOut/floodOut/slashOut 用）：管线已接（Env.ClipFn → DrawFx ClipPath）——主文字与自画副本
//    均走真 SKPath 裁剪（精确）。
//    clapShut 单字分支与 shredOut 的轴对齐矩形分别折算成 it.clip / it.clipY（DrawFx 原生消费）。
//    注：JLayouts.DrawFx 的 clip/clipY ClipRect 参数疑似按 (x,y,w,h) 传入（SKRect 是
//    (l,t,r,b)）——共享文件的既有问题，本文件不修，仅在汇报中记录。
// 4. `ae`（AE 导出模板名）：MoveDef 无对应属性，未注册——对位见各条目行内注释。
// 5. env.beat 对象（len/since/index）C# Env 未携带（Env.Beat 只有归一相位 double），beatLen/
//    beatSince/beatIdx 走 JS 无 beat 的 ltb 回退式（TODO(渲染器)：Env 挂 beat 对象后回迁）。
// 6. mosaicOut 的 DOM 离屏 canvas 对位为常驻 SKSurface（静态复用，不足时扩容）。
//
// 跨文件符号对位：J.measure→JText.Measure、J.layoutText→JText.LayoutText、J.itemBox→JAnim.ItemBox、
// J.glyphCount→JLayouts.GlyphCount、J.pool→JLang.Pool、J.fitContrast→J.FitContrast、
// J.noise1→J.Noise1、J.rgba(本地 Rgba)/r/rs/rr/h/mix/lum/smooth/clamp/lerp→J.*。

using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace JizuraLive.Engine;

public static class P11ExitB
{
    private const string P = "exitB";
    private static int _installed;

    // ================= 本包 helper（JS L8-314 逐条对位） =================

    private static bool IsHex(string? c) => c != null && c.Length > 0 && c[0] == '#' && (c.Length == 7 || c.Length == 4);

    private static string MixC(string? a, string? b, double t)
    {
        t = J.Clamp(t);
        if (t <= 0.002) return a ?? "#ffffff";
        if (t >= 0.998) return b ?? "#ffffff";
        return IsHex(a) && IsHex(b) ? J.Mix(a!, b!, t) : (t < 0.5 ? a! : b!);
    }

    /// <summary>JS colOf：isHex(it.color) ? it.color : '#ffffff'。</summary>
    private static string ColOf(JObject it)
    {
        var c = S(it, "color");
        return IsHex(c) ? c! : "#ffffff";
    }

    private static string? ScK(Env env, string k) => env.Sc?.Str(k);

    private static double MotionK(Env env) => J.Clamp(env.Fx.Motion / 0.7, 0, 1.6);

    private static bool IsSp(string? ch) => ch is " " or "　";

    private static JText.TextLayout LayOf(JObject it) => JText.Measure(it).Lay;

    /// <summary>JS box：每次重算（sx/sy/size 可能刚变过；Measure 按内容 memo）。</summary>
    private static ItemBounds Box(JObject it) => JAnim.ItemBox(it);

    private static int CutN(Env env) => Math.Max(1, JLayouts.GlyphCount(env.Cut?.Text ?? ""));
    private static bool CutBit(Env env, int k) => (J.H(unchecked((int)(env.Cut?.Seed ?? 0)), k, 1991) & 1) == 1;
    private static double CutR(Env env, int k) => J.R(unchecked((int)(env.Cut?.Seed ?? 0)), k, 1993);

    /// <summary>一个 item 只有一个字形，mixed/scatter 布局才有。</summary>
    private static bool IsSingle(Env env, JObject it) => LayOf(it).N == 1 && CutN(env) > 1;

    private static double Win(double p, double o, double spread) => J.Clamp((p - o * spread) / (1 - spread));

    private static double[] Rot2(double x, double y, double r) =>
        new[] { x * Math.Cos(r) - y * Math.Sin(r), x * Math.Sin(r) + y * Math.Cos(r) };

    private static double[] ToD(JObject it, double lx, double ly)
    {
        var xy = Rot2(lx, ly, N0(it, "rot") * J.DEG);
        return new[] { N0(it, "x") + xy[0], N0(it, "y") + xy[1] };
    }

    private static double[] ToL(JObject it, double x, double y) =>
        Rot2(x - N0(it, "x"), y - N0(it, "y"), -N0(it, "rot") * J.DEG);

    /// <summary>设计坐标方向向量改用 item 空间表示，charFn 偏移才沿屏幕轴。</summary>
    private static double[] DownI(JObject it)
    {
        var r = N0(it, "rot") * J.DEG;
        return new[] { Math.Sin(r), Math.Cos(r) };
    }

    private static double[] RightI(JObject it)
    {
        var r = N0(it, "rot") * J.DEG;
        return new[] { Math.Cos(r), -Math.Sin(r) };
    }

    private static double[] DirI(JObject it, double x, double y)
    {
        var ri = RightI(it);
        var di = DownI(it);
        return new[] { ri[0] * x + di[0] * y, ri[1] * x + di[1] * y };
    }

    private static double FadeEnd(double p, double a = 0.85) => 1 - J.Smooth(a, 1, p);

    /// <summary>字形 i 在 cut 里的次序 0..1；单字 item 用它的 mi 排。</summary>
    private static Func<int, int, double> OrderOf(Env env, JObject it)
    {
        var nCut = CutN(env);
        var mi = Num(it, "mi");
        return (i, n) => n > 1 ? i / (double)(n - 1) : nCut > 1 ? J.Clamp(mi / (nCut - 1)) : 0;
    }

    private static JAnim.CharFn Glyphs(Env env, JAnim.CharFn fn)
    {
        env.CharFns.Add(fn); // JS addC：管线侧 CombineChar 折叠合并
        return fn;
    }

    /// <summary>withCopies 的对位：主绘制走 mainFn；copyFn 各画一份副本（就地 post，层级在主文字下方）。</summary>
    private static void WithCopies(Env env, JObject it, JAnim.CharFn mainFn, IReadOnlyList<JAnim.CharFn> copyFns)
    {
        var prev = env.CharFns.ToList();
        Glyphs(env, mainFn);
        foreach (var f in copyFns)
        {
            var cf = JAnim.CombineChar(prev.Concat(new[] { f }).ToList());
            DrawCopy(env, it, cf, c =>
            {
                c["streak"] = null;
                c["echo"] = null;
            });
        }
    }

    /// <summary>JS 的 post 阶段干净副本（COPY_RESET：函数值字段走 env 桥不落 JObject）。</summary>
    private static JObject CopyOfReset(JObject src, Action<JObject>? over = null)
    {
        var c = (JObject)src.DeepClone();
        foreach (var k in new[] { "streak", "echo", "wipeBar", "clip", "clipY", "bands", "vbands" }) c[k] = null;
        c["cursorAt"] = -1;
        over?.Invoke(c);
        return c;
    }

    /// <summary>JS J.drawItem 直调的副本：DeepClone + 置换 charFn/pieceFn。</summary>
    private static BBox? DrawCopy(Env env, JObject src, JAnim.CharFn? charFn, Action<JObject>? mutate = null)
    {
        var c = (JObject)src.DeepClone();
        mutate?.Invoke(c);
        var prevC = env.CharFn;
        var prevP = env.PieceFn;
        env.CharFn = charFn;
        env.PieceFn = null;
        try
        {
            return JText.DrawItem(env, c, new JText.TextDrawOpts
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

    /// <summary>JS J.drawFx 直调的副本：走真管线 DrawFx（bands/clip 副本消费），外挂任意路径裁剪。</summary>
    private static BBox? DrawFxCopy(Env env, JObject src, JAnim.CharFn? charFn, SKPath? clip, Action<JObject>? mutate = null)
    {
        var c = CopyOfReset(src, mutate);
        var prevC = env.CharFn;
        var prevP = env.PieceFn;
        env.CharFn = charFn;
        env.PieceFn = null;
        try
        {
            if (clip != null)
            {
                env.Ctx.Save();
                env.Ctx.ClipPath(clip, SKClipOperation.Intersect, true);
            }
            try
            {
                return JLayouts.DrawFx(env, c);
            }
            finally
            {
                if (clip != null) env.Ctx.Restore();
            }
        }
        finally
        {
            env.CharFn = prevC;
            env.PieceFn = prevP;
        }
    }

    /// <summary>当前 item 的合并 charFn（JS post 阶段 it2.charFn 的对位）。</summary>
    private static JAnim.CharFn? MergedNow(Env env) => env.CharFns.Count == 0 ? null : JAnim.CombineChar(env.CharFns);

    /// <summary>在 item 自己的坐标系里画，含旋转。</summary>
    private static void InItem(Env env, JObject it, Action fn)
    {
        env.Ctx.Save();
        env.Ctx.Translate((float)N0(it, "x"), (float)N0(it, "y"));
        var rot = N0(it, "rot");
        if (rot != 0) env.Ctx.RotateDegrees((float)rot);
        try
        {
            fn();
        }
        finally
        {
            env.Ctx.Restore();
        }
    }

    private sealed class LnExt
    {
        public int Li;
        public double X0, X1, Y0, Y1;
    }

    /// <summary>每行（竖排每列）在 item 空间的范围。</summary>
    private static List<LnExt> LineExt(JObject it)
    {
        var lay = JText.LayoutText(it);
        var sx = N1(it, "sx");
        var sy = N1(it, "sy");
        var size = N0(it, "size");
        var vert = B(it, "vertical");
        var map = new Dictionary<int, LnExt>();
        foreach (var g in lay.Cells)
        {
            if (IsSp(g.Ch)) continue;
            if (!map.TryGetValue(g.Li, out var l)) l = map[g.Li] = new LnExt { Li = g.Li, X0 = 1e9, X1 = -1e9, Y0 = 1e9, Y1 = -1e9 };
            var hw = (vert ? size : g.W) / 2;
            var hh = (vert ? g.H : size) / 2;
            l.X0 = Math.Min(l.X0, (g.X - hw) * sx);
            l.X1 = Math.Max(l.X1, (g.X + hw) * sx);
            l.Y0 = Math.Min(l.Y0, (g.Y - hh) * sy);
            l.Y1 = Math.Max(l.Y1, (g.Y + hh) * sy);
        }
        return map.Values.OrderBy(l => l.Li).ToList();
    }

    private sealed class Lbx
    {
        public double X0, Y0, X1, Y1, Cx, Cy, W, H;
    }

    /// <summary>item 空间的 box，相对 it.x/it.y，不旋转。</summary>
    private static Lbx LBox(JObject it, double pad = 0)
    {
        var b = Box(it);
        return new Lbx
        {
            X0 = b.X0 - N0(it, "x") - pad,
            Y0 = b.Y0 - N0(it, "y") - pad,
            X1 = b.X1 - N0(it, "x") + pad,
            Y1 = b.Y1 - N0(it, "y") + pad,
            Cx = b.Cx - N0(it, "x"),
            Cy = b.Cy - N0(it, "y"),
            W = b.W + pad * 2,
            H = b.H + pad * 2,
        };
    }

    private sealed class Dbx
    {
        public double X0, Y0, X1, Y1, Cx, Cy, W, H;
    }

    /// <summary>旋转后的 item box 求设计空间 AABB，含 pad 与 skew 让位。</summary>
    private static Dbx DBox(JObject it, double pad = 0)
    {
        var b = Box(it);
        double x0 = 1e9, y0 = 1e9, x1 = -1e9, y1 = -1e9;
        foreach (var (px, py) in new[] { (b.X0, b.Y0), (b.X1, b.Y0), (b.X0, b.Y1), (b.X1, b.Y1) })
        {
            var p = ToD(it, px - N0(it, "x"), py - N0(it, "y"));
            x0 = Math.Min(x0, p[0]);
            x1 = Math.Max(x1, p[0]);
            y0 = Math.Min(y0, p[1]);
            y1 = Math.Max(y1, p[1]);
        }
        var sk = Math.Abs(Math.Tan(N0(it, "skew") * J.DEG)) * b.H * 0.5;
        return new Dbx
        {
            X0 = x0 - pad - sk,
            Y0 = y0 - pad,
            X1 = x1 + pad + sk,
            Y1 = y1 + pad,
            Cx = (x0 + x1) / 2,
            Cy = (y0 + y1) / 2,
            W = x1 - x0 + 2 * pad + 2 * sk,
            H = y1 - y0 + 2 * pad,
        };
    }

    private static void ScaleAbout(JObject it, double kx, double ky, double? ax = null, double? ay = null)
    {
        double axx, ayy;
        if (ax == null)
        {
            var b = Box(it);
            axx = b.Cx - N0(it, "x");
            ayy = b.Cy - N0(it, "y");
        }
        else
        {
            axx = ax.Value;
            ayy = ay!.Value;
        }
        var d = Rot2(axx * (1 - kx), ayy * (1 - ky), N0(it, "rot") * J.DEG);
        it["x"] = N0(it, "x") + d[0];
        it["y"] = N0(it, "y") + d[1];
        it["sx"] = N1(it, "sx") * kx;
        it["sy"] = N1(it, "sy") * ky;
    }

    private static void SizeAbout(JObject it, double k)
    {
        var b = Box(it);
        var ax = b.Cx - N0(it, "x");
        var ay = b.Cy - N0(it, "y");
        var d = Rot2(ax * (1 - k), ay * (1 - k), N0(it, "rot") * J.DEG);
        it["x"] = N0(it, "x") + d[0];
        it["y"] = N0(it, "y") + d[1];
        it["size"] = N0(it, "size") * k;
    }

    /// <summary>绕 item 空间一点旋转 {x, y, rot}。</summary>
    private static void RotateAbout(JObject it, double deg, double ax, double ay)
    {
        var r0 = N0(it, "rot") * J.DEG;
        var r1 = r0 + deg * J.DEG;
        var p0 = Rot2(ax, ay, r0);
        var p1 = Rot2(ax, ay, r1);
        it["x"] = N0(it, "x") + p0[0] - p1[0];
        it["y"] = N0(it, "y") + p0[1] - p1[1];
        it["rot"] = N0(it, "rot") + deg;
    }

    /// <summary>tearOut/glassBreak 的半边/碎片位姿（JS 的普通对象 {x, y, rot}）。</summary>
    private sealed class HState
    {
        public double X, Y, Rot;
    }

    private static void RotateAboutH(HState h, double deg, double ax, double ay)
    {
        var r0 = h.Rot * J.DEG;
        var r1 = r0 + deg * J.DEG;
        var p0 = Rot2(ax, ay, r0);
        var p1 = Rot2(ax, ay, r1);
        h.X += p0[0] - p1[0];
        h.Y += p0[1] - p1[1];
        h.Rot += deg;
    }

    /// <summary>item 空间区间换算成逐字形剪裁比例。</summary>
    private static (double[]? CX, double[]? CY) Gclip(JObject g, JObject it, double gx, double gy, double kx, double ky, double[]? xr, double[]? yr)
    {
        var sx = N1(it, "sx") * kx;
        var sy = N1(it, "sy") * ky;
        double[]? cx = null, cy = null;
        if (!B(g, "r90"))
        {
            if (xr != null) cx = new[] { (xr[0] - gx) / (D(g, "w") * sx), (xr[1] - gx) / (D(g, "w") * sx) };
            if (yr != null) cy = new[] { (yr[0] - gy) / (D(g, "h") * sy), (yr[1] - gy) / (D(g, "h") * sy) };
        }
        else
        {
            // 字形转了 90°，局部 x 沿 item +y，局部 y 沿 item -x
            if (yr != null) cx = new[] { (yr[0] - gy) / (sx * D(g, "w")), (yr[1] - gy) / (sx * D(g, "w")) };
            if (xr != null) cy = new[] { -(xr[1] - gx) / (sy * D(g, "h")), -(xr[0] - gx) / (sy * D(g, "h")) };
        }
        return (cx, cy);
    }

    /// <summary>字形在 item 空间的静止中心。</summary>
    private static double GCx(JObject g, JObject it) => (D(g, "x") + D(g, "vx")) * N1(it, "sx");
    private static double GCy(JObject g, JObject it) => (D(g, "y") + D(g, "vy")) * N1(it, "sy");

    /// <summary>GCx/GCy 的强类型版（直接吃排版的 GlyphCell）。</summary>
    private static double GCx(JText.GlyphCell g, JObject it) => (g.X + g.Vx) * N1(it, "sx");
    private static double GCy(JText.GlyphCell g, JObject it) => (g.Y + g.Vy) * N1(it, "sy");

    /// <summary>单位方向 (dx, dy) 上的半平面（keep u≥s 一侧），画成大四边形裁剪路径。</summary>
    private static SKPath HalfPlanePath(double dx, double dy, double s, double l = 1e5)
    {
        double tx = -dy, ty = dx;
        double a = s, b = s + l;
        var path = new SKPath();
        path.MoveTo((float)(dx * a + tx * l), (float)(dy * a + ty * l));
        path.LineTo((float)(dx * b + tx * l), (float)(dy * b + ty * l));
        path.LineTo((float)(dx * b - tx * l), (float)(dy * b - ty * l));
        path.LineTo((float)(dx * a - tx * l), (float)(dy * a - ty * l));
        path.Close();
        return path;
    }

    /// <summary>item 空间多边形 → 设计空间 SKPath（跟 h 的位姿）。</summary>
    private static SKPath PolyPath(double[][] pts, double x, double y, double rotDeg)
    {
        var r = rotDeg * J.DEG;
        var c = Math.Cos(r);
        var s = Math.Sin(r);
        var path = new SKPath();
        for (var i = 0; i < pts.Length; i++)
        {
            var px = x + c * pts[i][0] - s * pts[i][1];
            var py = y + s * pts[i][0] + c * pts[i][1];
            if (i == 0) path.MoveTo((float)px, (float)py);
            else path.LineTo((float)px, (float)py);
        }
        path.Close();
        return path;
    }

    /// <summary>轴对齐矩形 [x, y, w, h] 集合 → 裁剪路径。</summary>
    private static SKPath RectsPath(IEnumerable<double[]> rects, double pad = 0)
    {
        var path = new SKPath();
        foreach (var r in rects)
            path.AddRect(new SKRect((float)r[0], (float)r[1], (float)(r[0] + r[2] + pad), (float)(r[1] + r[3] + pad)));
        return path;
    }

    /// <summary>有没有字形吃到逐字 blur（字形 filter 上再叠 shadow 开销极大）。</summary>
    private static bool GlyphBlurOn(Env env, JObject it)
    {
        if (env.CharFns.Count == 0) return false;
        var lay = LayOf(it);
        foreach (var g in lay.Cells)
            foreach (var f in env.CharFns)
                if (f(g.I, JText.CellJson(g), lay.N) is { Blur: > 0.4 })
                    return true;
        return false;
    }

    /// <summary>item 上所有逐字 blur 乘 k，顺带把旧函数折叠成一个。</summary>
    private static void ScaleGlyphBlur(Env env, double k)
    {
        if (env.CharFns.Count == 0) return;
        var m = JAnim.CombineChar(env.CharFns.ToArray());
        env.CharFns.Clear();
        env.CharFns.Add((i, g, n) =>
        {
            var r = m(i, g, n);
            if (r == null || r.Blur is null or 0) return r;
            var o = CloneXf(r);
            o.Blur = r.Blur * k;
            return o;
        });
    }

    private static CharXf CloneXf(CharXf r) => new()
    {
        Hide = r.Hide,
        Dx = r.Dx,
        Dy = r.Dy,
        Rot = r.Rot,
        S = r.S,
        A = r.A,
        Sx = r.Sx,
        Sy = r.Sy,
        Ch = r.Ch,
        Color = r.Color,
        Skew = r.Skew,
        Blur = r.Blur,
        Outline = r.Outline,
        ClipX = r.ClipX,
        ClipY = r.ClipY,
    };

    // env.beat {since,len,index} 由渲染器每帧挂 Env（BeatHas/BeatSince/BeatLen/BeatIndex，09_render beatAt）；
    // BeatHas=false（plan 无节拍）回 JS 的 ltb 回退式（JS beatLen/beatSince/beatIdx 对位）
    private static double BeatLen(Env env, double fb) => env.BeatHas && env.BeatLen > 0 ? env.BeatLen : fb;
    private static double BeatSince(Env env, double fb) => env.BeatHas ? env.BeatSince : ((env.Ltb % fb) + fb) % fb;
    private static int BeatIdx(Env env, double fb) => env.BeatHas ? env.BeatIndex : (int)Math.Floor(env.Ltb / fb);

    private static bool DarkBg(Env env) => J.Lum(ScK(env, "bg") ?? "#ffffff") < 0.5;

    /// <summary>装饰只画一次：淡化副本和描边副本不画。</summary>
    private static bool Primary(JObject it) => NN(it, "alpha", 1) > 0.9 && !IsFalse(it["fill"]);

    private static bool IsFalse(JToken? t) => t is JValue v && v.Type == JTokenType.Boolean && !(bool)v.Value!;

    /// <summary>JS J.rgba(h, a) → "rgba(r,g,b,a)"（shadow.color 吃这个格式）。</summary>
    private static string Rgba(string hex, double a)
    {
        var (r, g, b) = J.Hex(hex);
        return "rgba(" + r + "," + g + "," + b + "," + a + ")";
    }

    // 松散读取（对齐 JS 语义）：(x||0)/(x||1)/(x??d)
    private static string? S(JObject o, string k) => JLayouts.S(o, k);
    private static double D(JObject o, string k, double d = 0) => JLayouts.D(o, k, d);
    private static bool B(JObject o, string k) => JLayouts.B(o, k);
    private static double Num(JObject o, string k) => JAnim.Num(o, k);
    private static double N0(JObject o, string k) => JAnim.Num(o, k);
    private static double NN(JObject o, string k, double d) => JAnim.NumNull(o, k, d);
    private static double N1(JObject o, string k) => JAnim.NumOr1(o, k);
    private static int SeedOf(JObject o) => JAnim.SeedOf(o);

    // ================= 注册（JS X/H 字面量序，Object.keys 语义） =================

    /// <summary>exit 条目统一包一层 JS 的兜底：p≈1 时不管配方留下什么，一定完全消失。</summary>
    private static void Reg1(string key, MoveDef def)
    {
        var f = def.Draw;
        def.Draw = env =>
        {
            if (env.Ap >= 0.998)
            {
                env.It!["alpha"] = 0.0;
                return;
            }
            f?.Invoke(env);
        };
        Reg.Register("exit", key, def, P);
    }

    private static void RegH(string key, MoveDef def) => Reg.Register("hold", key, def, P);

    /// <summary>幂等注册本文件全部 exit/hold 条目（顺序 = JS 源 X/H 字面量序；不挂静态构造函数）。</summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;
        PeelOff();        // X.peelOff
        CrumpleOut();     // X.crumpleOut
        TearOut();        // X.tearOut
        ScorchOut();      // X.scorchOut
        OverexposeOut();  // X.overexposeOut
        ScanOut();        // X.scanOut
        StripesOut();     // X.stripesOut
        HalftoneOut();    // X.halftoneOut
        EraserOut();      // X.eraserOut
        VacuumOut();      // X.vacuumOut
        SandOut();        // X.sandOut
        ShredOut();       // X.shredOut
        DominoOut();      // X.dominoOut
        HingeOut();       // X.hingeOut
        RocketOff();      // X.rocketOff
        BounceOff();      // X.bounceOff
        BalloonOff();     // X.balloonOff
        DeflateOut();     // X.deflateOut
        HazeOut();        // X.hazeOut
        GlassBreak();     // X.glassBreak
        ZipOut();         // X.zipOut
        ClapShut();       // X.clapShut
        LampOff();        // X.lampOff
        SlotOut();        // X.slotOut
        ClockOut();       // X.clockOut
        MatrixOut();      // X.matrixOut
        TornadoOut();     // X.tornadoOut
        RollUpOut();      // X.rollUpOut
        SnakeOut();       // X.snakeOut
        FlutterOut();     // X.flutterOut
        RollOff();        // X.rollOff
        FanClose();       // X.fanClose
        RgbSplitOut();    // X.rgbSplitOut
        ShockOut();       // X.shockOut
        FloodOut();       // X.floodOut
        SlashOut();       // X.slashOut
        MosaicOut();      // X.mosaicOut
        ScribbleOut();    // X.scribbleOut
        CandleOut();      // X.candleOut
        GlowFlicker();    // H.glowFlicker
        WindGust();       // H.windGust
        Dangle();         // H.dangle
        EqBounce();       // H.eqBounce
        FlashBox();       // H.flashBox
        GlintSweep();     // H.glintSweep
        FlipSwap();       // H.flipSwap
        ShadowSway();     // H.shadowSway
        MagnetJiggle();   // H.magnetJiggle
        TypeRattle();     // H.typeRattle
        FocusRack();      // H.focusRack
        PluckString();    // H.pluckString
    }

    // ---------------- peelOff（X.peelOff；JS ae:'wipe'） ----------------

    private static void PeelOff() => Reg1("peelOff", new MoveDef
    {
        Name = "ステッカー剥がし",
        Tags = new[] { "pop", "graphic" },
        W = 1,
        OutDur = (dur, _) => J.Clamp(dur * 0.36, 0.3, 0.7),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var q = IsSingle(env, it) ? Win(p, OrderOf(env, it)(0, 1), 0.4) : p;
            if (q <= 0) return;
            var seed = unchecked((int)(env.Cut?.Seed ?? 0));
            var v = (int)((uint)J.H(seed, 311) >> 5) & 3;
            var dx = new[] { 0.83, -0.83, 0.83, -0.83 }[v];
            var dy = new[] { 0.56, 0.56, -0.56, -0.56 }[v];
            var sz = N0(it, "size");
            var bb = DBox(it, sz * 0.06);
            var us = new[] { bb.X0 * dx + bb.Y0 * dy, bb.X1 * dx + bb.Y0 * dy, bb.X0 * dx + bb.Y1 * dy, bb.X1 * dx + bb.Y1 * dy };
            var u0 = us.Min();
            var u1 = us.Max();
            const double pe = 0.74;
            var f = J.Clamp(q / pe);
            var fly = J.Clamp((q - pe) / (1 - pe));
            var s = J.Lerp(u0, u1, 0.1 * f + 0.9 * f * f * (3 - 2 * f));
            var a0 = NN(it, "alpha", 1);
            var c0 = ColOf(it);
            var bg = ScK(env, "bg") ?? "#ffffff";
            var back = MixC(MixC(c0, bg, 0.5), ScK(env, "accent") ?? "#ffffff", 0.18);
            if (f < 1)
            {
                // JS clipFn = halfPlane(ctx, dx, dy, s, 1)（保留侧半平面）→ Env.ClipFn 桥
                env.ClipFn = (_, _) => HalfPlanePath(dx, dy, s);
            }
            else it["alpha"] = 0.0;
            // JS chainPost（撕起的背面 flap）——就地执行
            var fa = a0 * (1 - J.E.InQuad(fly));
            if (fa <= 0.01) return;
            var lift = sz * (0.05 + 0.1 * f);
            var ctx = env.Ctx;
            ctx.Save();
            if (fly <= 0)
                using (var hp = HalfPlanePath(dx, dy, s))
                    ctx.ClipPath(hp, SKClipOperation.Intersect, true);
            else
            {
                var m = sz * 2.2 * fly * fly;
                ctx.Translate((float)(dx * m), (float)(dy * m - sz * 1.2 * fly));
            }
            // ctx.transform(1-2dx², -2dxdy, -2dxdy, 1-2dy², 2s·dx, 2s·dy)：沿 u=s 的镜像
            ctx.Concat(new SKMatrix(
                (float)(1 - 2 * dx * dx), (float)(-2 * dx * dy), (float)(2 * s * dx),
                (float)(-2 * dx * dy), (float)(1 - 2 * dy * dy), (float)(2 * s * dy),
                0, 0, 1));
            var cf = MergedNow(env);
            DrawCopy(env, it, cf, c =>
            {
                c["streak"] = null;
                c["echo"] = null;
                c["alpha"] = fa;
                c["color"] = back;
                c["gradient"] = null;
                c["pattern"] = null;
                c["extrude"] = null;
                c["blur"] = 0.0;
                c["ghost"] = false;
                if (env.Pass == "main")
                    c["shadow"] = new JObject
                    {
                        ["color"] = Rgba("#000000", DarkBg(env) ? 0.5 : 0.28),
                        ["blur"] = sz * 0.12,
                        ["dx"] = dx * lift,
                        ["dy"] = dy * lift + lift,
                    };
                else
                    c["shadow"] = null;
            });
            ctx.Restore();
        },
    });

    // ---------------- crumpleOut（X.crumpleOut；JS ae:'shrink'） ----------------

    private static void CrumpleOut() => Reg1("crumpleOut", new MoveDef
    {
        Name = "丸めて捨てる",
        Tags = new[] { "pop", "emotional" },
        W = 0.9,
        MinDur = 0.8,
        OutDur = (dur, _) => J.Clamp(dur * 0.42, 0.36, 0.85),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var seed = SeedOf(it);
            var c0 = ColOf(it);
            var bg = ScK(env, "bg") ?? "#ffffff";
            var g1 = J.E.OutBack(J.Clamp(p / 0.15), 1.6);
            var g2 = J.E.OutBack(J.Clamp((p - 0.17) / 0.15), 1.6);
            var g3 = J.E.OutBack(J.Clamp((p - 0.34) / 0.15), 1.6);
            var cr = J.Clamp(0.45 * g1 + 0.3 * g2 + 0.25 * g3, 0, 1.08);
            var k = 1 - 0.93 * Math.Min(1, cr);
            var v = J.Clamp((p - 0.5) / 0.5);
            var dir = CutBit(env, 21) ? 1 : -1;
            var spin = dir * (cr * 25 + 620 * v * v);
            var tx = dir * env.W * 0.42 * v;
            var ty = -env.H * 0.8 * v + env.H * 1.75 * v * v;
            var alpha = FadeEnd(p, 0.88);
            if (IsSingle(env, it))
            {
                var mi = (int)Num(it, "mi");
                var seedCut = unchecked((int)(env.Cut?.Seed ?? 0));
                var jx = J.Rs(seedCut, mi, 23) * N0(it, "size") * 0.25;
                var jy = J.Rs(seedCut, mi, 24) * N0(it, "size") * 0.25;
                var rr = Rot2((N0(it, "x") - env.W / 2.0) * k + jx * cr, (N0(it, "y") - env.H / 2.0) * k + jy * cr, spin * J.DEG);
                it["x"] = env.W / 2.0 + rr[0] + tx;
                it["y"] = env.H / 2.0 + rr[1] + ty;
                it["rot"] = N0(it, "rot") + spin + J.Rs(seed, 22) * 150 * cr;
                it["size"] = N0(it, "size") * (1 - 0.5 * Math.Min(1, cr));
                it["skew"] = N0(it, "skew") + J.Rs(seed, 25) * 22 * cr;
                it["alpha"] = NN(it, "alpha", 1) * alpha;
                return;
            }
            var b = LBox(it);
            var ox = b.Cx;
            var oy = b.Cy;
            var sz = N0(it, "size");
            var ti = DirI(it, tx, ty);
            Glyphs(env, (i, g, _) =>
            {
                if (g == null) return null;
                var px = GCx(g, it) - ox;
                var py = GCy(g, it) - oy;
                var jx = J.Rs(seed, i, 26) * sz * 0.22;
                var jy = J.Rs(seed, i, 27) * sz * 0.22;
                var rr = Rot2(px * k + jx * cr, py * k + jy * cr, spin * J.DEG);
                var r1 = J.R(seed, i, 28);
                var r2 = J.R(seed, i, 29);
                return new CharXf
                {
                    Dx = rr[0] - px + ti[0],
                    Dy = rr[1] - py + ti[1],
                    Rot = spin + J.Rs(seed, i, 30) * 170 * cr,
                    S = 1 - 0.55 * Math.Min(1, cr),
                    Sx = 1 - 0.35 * r1 * cr,
                    Sy = 1 - 0.35 * r2 * cr,
                    Skew = J.Rs(seed, i, 31) * 26 * cr,
                    Color = r1 < 0.45 ? MixC(c0, bg, 0.3 * Math.Min(1, cr)) : null,
                    A = alpha,
                };
            });
        },
    });

    // ---------------- tearOut（X.tearOut；JS ae:'slice'） ----------------

    private static void TearOut() => Reg1("tearOut", new MoveDef
    {
        Name = "破り捨て",
        Tags = new[] { "emotional", "graphic" },
        W = 0.9,
        OutDur = (dur, _) => J.Clamp(dur * 0.38, 0.3, 0.75),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var vert = B(it, "vertical");
            var seed = SeedOf(it);
            var sz = N0(it, "size");
            double[][] polyA, polyB;
            double[] piv;
            if (IsSingle(env, it) && !vert)
            {
                // 单字 item：撕口横贯屏幕中心
                var cs = unchecked((int)(env.Cut?.Seed ?? 0));
                var bb = DBox(it, sz * 0.3);
                var kN = J.Clamp((int)Math.Ceiling((bb.Y1 - bb.Y0) / (sz * 0.08)), 4, 40);
                var jd = new List<double[]>();
                for (var k = 0; k <= kN; k++)
                {
                    var y2 = J.Lerp(bb.Y0, bb.Y1, k / (double)kN);
                    jd.Add(new[]
                    {
                        env.W / 2.0 + J.Noise1(y2 / (env.H * 0.035), cs + 5) * env.H * 0.02
                        + J.Rs(cs, (int)Math.Round(y2 / (env.H * 0.008)), 331) * env.H * 0.006,
                        y2,
                    });
                }
                polyA = new[] { new[] { -env.W * 1.0, bb.Y0 } }.Concat(jd).Concat(new[] { new[] { -env.W * 1.0, bb.Y1 } }).Select(p2 => ToL(it, p2[0], p2[1])).ToArray();
                polyB = new[] { new[] { 2.0 * env.W, bb.Y0 } }.Concat(jd).Concat(new[] { new[] { 2.0 * env.W, bb.Y1 } }).Select(p2 => ToL(it, p2[0], p2[1])).ToArray();
                piv = ToL(it, env.W / 2.0, env.H * 0.62);
            }
            else
            {
                var b = LBox(it, sz * 0.3);
                var mid = vert ? b.Cy : b.Cx;
                var a0 = vert ? b.X0 : b.Y0;
                var a1 = vert ? b.X1 : b.Y1;
                var kN = J.Clamp((int)Math.Ceiling((a1 - a0) / (sz * 0.08)), 6, 48);
                var jag = new List<double[]>();
                for (var k = 0; k <= kN; k++)
                {
                    var t2 = J.Lerp(a0, a1, k / (double)kN);
                    var off = J.Rs(seed, k, 331) * sz * 0.05 + J.Noise1(k * 0.35, seed + 5) * sz * 0.16;
                    jag.Add(vert ? new[] { t2, mid + off } : new[] { mid + off, t2 });
                }
                // A 是左半（竖排为上半），B 是右半（下半）
                polyA = vert
                    ? new[] { new[] { b.X0, b.Y0 - sz } }.Concat(jag).Concat(new[] { new[] { b.X1, b.Y0 - sz } }).ToArray()
                    : new[] { new[] { b.X0 - sz, b.Y0 } }.Concat(jag).Concat(new[] { new[] { b.X0 - sz, b.Y1 } }).ToArray();
                polyB = vert
                    ? new[] { new[] { b.X0, b.Y1 + sz } }.Concat(jag).Concat(new[] { new[] { b.X1, b.Y1 + sz } }).ToArray()
                    : new[] { new[] { b.X1 + sz, b.Y0 } }.Concat(jag).Concat(new[] { new[] { b.X1 + sz, b.Y1 } }).ToArray();
                piv = jag[^1]; // 从远端起撕，近端先张开
            }
            var o = J.E.OutCubic(J.Clamp(p / 0.32));
            var v = J.Clamp((p - 0.26) / 0.74);
            var th = 7 * o + 40 * v * v;
            var fall = env.H * 1.1 * v * v;
            var side = sz * 1.6 * v;
            void Place(HState h, int sgn)
            {
                RotateAboutH(h, (vert ? -sgn : sgn) * th, piv[0], piv[1]);
                if (vert)
                {
                    h.X += sgn * side * 0.35 - side * 0.4;
                    h.Y += fall + sgn * sz * 0.3 * v;
                }
                else
                {
                    h.X += sgn * side;
                    h.Y += fall * (sgn < 0 ? 1 : 0.85);
                }
            }
            var ha = new HState { X = N0(it, "x"), Y = N0(it, "y"), Rot = N0(it, "rot") };
            var hb = new HState { X = N0(it, "x"), Y = N0(it, "y"), Rot = N0(it, "rot") };
            Place(ha, -1);
            Place(hb, 1);
            it["x"] = ha.X;
            it["y"] = ha.Y;
            it["rot"] = ha.Rot;
            it["alpha"] = NN(it, "alpha", 1) * FadeEnd(p, 0.82);
            // JS clipFn = polyL(ctx, it3, polyA)（item 空间多边形、按 drawFx 时点的 item 位姿换算）→ Env.ClipFn 桥
            var pa = polyA;
            env.ClipFn = (_, it3) => PolyPath(pa, N0(it3, "x"), N0(it3, "y"), N0(it3, "rot"));
            // JS chainPost：drawFx 画 polyB 半（B 位姿）——就地执行
            var pb = polyB;
            using var bPath = PolyPath(pb, hb.X, hb.Y, hb.Rot);
            DrawFxCopy(env, it, MergedNow(env), bPath, c =>
            {
                c["x"] = hb.X;
                c["y"] = hb.Y;
                c["rot"] = hb.Rot;
            });
        },
    });

    // ---------------- scorchOut（X.scorchOut；JS ae:'wipe'） ----------------

    private static void ScorchOut() => Reg1("scorchOut", new MoveDef
    {
        Name = "焦げて消える",
        Tags = new[] { "emotional", "glitch" },
        W = 0.8,
        OutDur = (dur, _) => J.Clamp(dur * 0.4, 0.32, 0.8),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var single = IsSingle(env, it);
            var q = p; // 单字 item 共用一条贯穿屏幕的火线
            var vert = B(it, "vertical");
            var sz = N0(it, "size");
            var t = env.Ltb;
            var acc = ScK(env, "accent") ?? "#ffffff";
            var c0 = ColOf(it);
            var bg = ScK(env, "bg") ?? "#ffffff";
            var seed = single ? unchecked((int)(env.Cut?.Seed ?? 0)) : SeedOf(it);
            var uu = single ? Math.Min(env.W, env.H) * 0.16 : sz;
            var bb = DBox(it, sz * 0.2);
            var d = vert ? new[] { 0.22, 1.0 } : new[] { 1.0, 0.22 };
            var dl = Math.Sqrt(d[0] * d[0] + d[1] * d[1]);
            d = new[] { d[0] / dl, d[1] / dl };
            if (CutBit(env, 31)) d = new[] { -d[0], -d[1] };
            var tn = new[] { -d[1], d[0] };
            double[,] cs = { { bb.X0, bb.Y0 }, { bb.X1, bb.Y0 }, { bb.X0, bb.Y1 }, { bb.X1, bb.Y1 } };
            var us = new double[4];
            var ws = new double[4];
            for (var i = 0; i < 4; i++)
            {
                us[i] = cs[i, 0] * d[0] + cs[i, 1] * d[1];
                ws[i] = cs[i, 0] * tn[0] + cs[i, 1] * tn[1];
            }
            var w0 = ws.Min();
            var w1 = ws.Max();
            double[] ux;
            if (single)
            {
                double[,] corners = { { 0, 0 }, { (double)env.W, 0 }, { 0, (double)env.H }, { (double)env.W, (double)env.H } };
                ux = new double[4];
                for (var i = 0; i < 4; i++) ux[i] = corners[i, 0] * d[0] + corners[i, 1] * d[1];
            }
            else ux = us;
            var u0 = ux.Min();
            var u1 = ux.Max();
            var rough = uu * 0.2;
            var band = uu * 0.4;
            double S(double x) => J.Lerp(u0 - rough * 1.3, u1 + rough * 1.3 + band, J.E.InOutSine(x));
            var s = S(q);
            var kN = J.Clamp((int)Math.Ceiling((w1 - w0) / (uu * 0.07)), 6, 60);
            double[] Pt(double u2, double w2) => new[] { d[0] * u2 + tn[0] * w2, d[1] * u2 + tn[1] * w2 };
            double EdgeU(double w2) => s + rough * (0.75 * J.Noise1(w2 / uu * 2.4, seed) + 0.3 * J.Noise1(w2 / uu * 8 + t * 6, seed + 3));
            var edge = new double[kN + 1][];
            for (var k = 0; k <= kN; k++)
            {
                var w2 = J.Lerp(w0, w1, k / (double)kN);
                edge[k] = new[] { EdgeU(w2), w2 };
            }
            var uMax = us.Max();
            if (edge.All(e2 => e2[0] > uMax + rough))
            {
                it["alpha"] = 0.0;
                return;
            }
            var far = Math.Max(u1, uMax) + uu * 4;
            SKPath BurnPath(double off0, double? off1)
            {
                var path = new SKPath();
                for (var k = 0; k < edge.Length; k++)
                {
                    var p2 = Pt(edge[k][0] + off0, edge[k][1]);
                    if (k == 0) path.MoveTo((float)p2[0], (float)p2[1]);
                    else path.LineTo((float)p2[0], (float)p2[1]);
                }
                if (off1 == null)
                {
                    var a2 = Pt(far, w1);
                    var b2 = Pt(far, w0);
                    path.LineTo((float)a2[0], (float)a2[1]);
                    path.LineTo((float)b2[0], (float)b2[1]);
                }
                else
                {
                    for (var k = edge.Length - 1; k >= 0; k--)
                    {
                        var p2 = Pt(edge[k][0] + off1.Value, edge[k][1]);
                        path.LineTo((float)p2[0], (float)p2[1]);
                    }
                }
                path.Close();
                return path;
            }
            // JS clipFn = path(ctx, 0, null)（火线远侧直到 far 的区域）→ Env.ClipFn 桥
            env.ClipFn = (_, _) => BurnPath(0, null);
            var heat = MixC(c0, acc, 0.85);
            var soot = DarkBg(env) ? MixC(bg, acc, 0.25) : MixC(bg, "#000000", 0.45);
            var lw = Math.Max(1.5, uu * 0.03);
            var a0 = NN(it, "alpha", 1);
            var rimA = a0 * (1 - J.Smooth(0.86, 1, q));
            // JS chainPost——就地执行
            using (var bandPath = BurnPath(0, band))
                DrawFxCopy(env, it, MergedNow(env), bandPath, c =>
                {
                    c["color"] = heat;
                    c["gradient"] = null;
                    c["pattern"] = null;
                    c["alpha"] = a0 * 0.9;
                });
            if (env.Pass != "main") return;
            var ep = edge.Select(e2 => Pt(e2[0], e2[1])).ToArray();
            var sp = edge.Select(e2 => Pt(e2[0] - lw * 1.6, e2[1])).ToArray();
            env.Line(sp, soot, lw * 2.2, 0.8 * rimA, false);
            env.Line(ep, acc, lw * 4, 0.22 * rimA, false);
            env.Line(ep, acc, lw, rimA, false);
            var sparks = single ? 6 : 22; // 火星在火线处生成，向后上方飘
            for (var j = 0; j < sparks; j++)
            {
                var t0 = J.R(seed, j, 341) * 0.85;
                var age = (q - t0) / 0.28;
                if (age <= 0 || age >= 1) continue;
                var w2 = J.Lerp(w0, w1, J.R(seed, j, 342));
                var xy0 = Pt(EdgeU(w2) - (s - S(t0)) - uu * 0.05, w2);
                var x1 = xy0[0] - d[0] * uu * 0.3 * age + Math.Sin(age * 7 + j) * uu * 0.06;
                var y1 = xy0[1] - d[1] * uu * 0.3 * age - uu * 0.9 * age;
                env.Circle(x1, y1, Math.Max(0.8, uu * 0.028 * (1 - age)), acc, null, 0, a0 * (1 - age), false);
            }
        },
    });

    // ---------------- overexposeOut（X.overexposeOut；JS ae:'blur'） ----------------

    private static void OverexposeOut() => Reg1("overexposeOut", new MoveDef
    {
        Name = "白飛び",
        Tags = new[] { "emotional", "calm", "pop" },
        W = 1,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var dark = DarkBg(env);
            var c0 = ColOf(it);
            var sz = N0(it, "size");
            var acc = ScK(env, "accent") ?? "#ffffff";
            var hot = dark ? "#ffffff" : ScK(env, "bg") ?? "#ffffff";
            var glow = dark ? MixC(acc, "#ffffff", 0.45) : acc;
            var a = J.E.InQuad(J.Clamp(p / 0.5));
            var e = J.E.OutCubic(J.Clamp(p / 0.7));
            it["color"] = MixC(c0, hot, a);
            if (it["gradient"] != null) it["gradient"] = null;
            if (it["pattern"] != null) it["pattern"] = null;
            ScaleGlyphBlur(env, 1 - J.Smooth(0, 0.12, p)); // 给 bloom 让路，字形 filter 上叠 shadow 太贵
            if (p > 0.08)
                it["extrude"] = null; // 过曝阶段把立体这类深度效果吞掉；JS 还 chainPre 了「treatment 在 pre 里再给回来」的兜底——管线未挂 pre，略
            SizeAbout(it, 1 + 0.07 * e);
            it["track"] = N0(it, "track") + 0.06 * e;
            var fade = 1 - J.E.InOutSine(J.Clamp((p - 0.38) / 0.62));
            if (env.Pass == "main")
                it["shadow"] = new JObject { ["color"] = Rgba(glow, 0.95), ["blur"] = sz * (0.05 + 0.4 * e), ["dx"] = 0, ["dy"] = 0 };
            it["alpha"] = NN(it, "alpha", 1) * fade;
            if (env.Pass == "main" && e > 0.02 && p > 0.12)
            {
                // JS chainPre（更宽的 bloom 垫层）——就地执行（层级在主文字下方）
                var ga = NN(it, "alpha", 1);
                DrawCopy(env, it, MergedNow(env), c =>
                {
                    c["streak"] = null;
                    c["echo"] = null;
                    c["color"] = glow;
                    c["gradient"] = null;
                    c["pattern"] = null;
                    c["extrude"] = null;
                    c["stroke"] = 0.0;
                    c["alpha"] = ga * 0.8;
                    c["blur"] = 0.0;
                    c["shadow"] = new JObject { ["color"] = Rgba(glow, 1), ["blur"] = sz * 0.9 * e, ["dx"] = 0, ["dy"] = 0 };
                });
            }
            var st = Math.Sin(Math.PI * J.Clamp((p - 0.12) / 0.88));
            if (st > 0.02 && Primary(it) && (!IsSingle(env, it) || Math.Round(Num(it, "mi")) == 0))
            {
                // JS chainPost（镜头横纹）——就地执行
                var single = IsSingle(env, it);
                var bbx = single ? null : DBox(it, 0);
                var cx = single ? env.W / 2.0 : bbx!.Cx;
                var cy = single ? N0(it, "y") : bbx!.Cy;
                var w = (single ? env.W * 0.5 : bbx!.W) * (0.8 + 0.9 * e);
                env.Rect(cx - w / 2, cy - sz * 0.09, w, sz * 0.18, glow, 0.14 * st, false);
                env.Rect(cx - w * 0.75, cy - sz * 0.03, w * 1.5, sz * 0.06, glow, 0.35 * st, false);
                env.Rect(cx - w * 0.9, cy - sz * 0.009, w * 1.8, sz * 0.018, glow, 0.95 * st, false);
            }
        },
    });

    // ---------------- scanOut（X.scanOut；JS ae:'slice'） ----------------

    private static void ScanOut() => Reg1("scanOut", new MoveDef
    {
        Name = "走査線消去",
        Tags = new[] { "glitch", "graphic" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var sz = N0(it, "size");
            var seed = SeedOf(it);
            var bb = DBox(it, sz * 0.3);
            var h = bb.Y1 - bb.Y0;
            var pitch = Math.Max(2.5, sz * 0.065);
            if (h / pitch > 64) pitch = h / 64;
            var n = (int)Math.Ceiling(h / pitch);
            var ph = h / n;
            var single = IsSingle(env, it);
            var l = single ? env.H * 0.14 : Math.Max(sz * 0.9, h * 0.3);
            // 单字 item 用一条贯穿屏幕的扫描线
            var ys = single ? J.Lerp(env.H * 0.2, env.H * 0.8 + l, p) : J.Lerp(bb.Y0 - sz * 0.02, bb.Y1 + l, p);
            var rects = new List<double[]>();
            var lastScan = -1;
            for (var j = 0; j < n; j++)
            {
                var y0 = bb.Y0 + j * ph;
                var yc = y0 + ph / 2;
                var a = J.Clamp((ys - yc) / l);
                if (a <= 0) break;
                lastScan = j;
                var keep = (j & 1) != 0 ? 1 - J.Clamp(a / 0.4) : 1 - J.E.InQuad(a);
                var hh = ph * keep * 0.8;
                if (hh > 0.08) rects.Add(new[] { yc - hh / 2, hh });
            }
            var yU = bb.Y0 + (lastScan + 1) * ph; // 没扫到的区域从这里开始
            if (rects.Count == 0 && yU >= bb.Y1 - 0.5)
            {
                it["alpha"] = 0.0;
                return;
            }
            // JS clipFn：隔行扫描条 + 未扫区（x 从 bb.x0-sz*3 起、宽 bb.w+sz*6）→ Env.ClipFn 桥
            var rectsLoc = rects;
            var bbY1 = bb.Y1;
            var bbX0 = bb.X0;
            var bbW = bb.W;
            env.ClipFn = (_, _) =>
            {
                var path = new SKPath();
                var rx = bbX0 - sz * 3;
                var rw = bbW + sz * 6;
                foreach (var r in rectsLoc)
                    path.AddRect(new SKRect((float)rx, (float)r[0], (float)(rx + rw), (float)(r[0] + r[1])));
                if (yU < bbY1)
                    path.AddRect(new SKRect((float)rx, (float)yU, (float)(rx + rw), (float)(bbY1 + 1)));
                return path;
            };
            const int gN = 7;
            var gh = (yU - bb.Y0) / gN;
            var bands = new JArray();
            if (yU > bb.Y0 + 0.5 && env.Pass == "main")
                for (var k = 0; k < gN; k++)
                {
                    var y0 = bb.Y0 + k * gh;
                    var a = J.Clamp((ys - (y0 + gh / 2)) / l);
                    var dxv = (k & 1) != 0 ? -1.0 : 1.0;
                    dxv *= sz * (0.04 + 1.1 * a * a) * (0.5 + 0.5 * J.R(seed, k, 351));
                    bands.Add(new JArray(y0, y0 + gh + 0.3, dxv));
                }
            if (bands.Count > 0)
            {
                bands.Add(new JArray(yU, bb.Y1, 0.0));
                it["bands"] = bands;
            }
            if (ys < bb.Y1 + sz * 0.1 && ys > bb.Y0 - sz * 0.1)
            {
                var acc = ScK(env, "accent") ?? "#ffffff";
                var lw = Math.Max(1.5, sz * 0.025);
                var a0 = NN(it, "alpha", 1) * (1 - J.Smooth(0.8, 1, p));
                // JS chainPost——就地执行
                env.Rect(bb.X0 - sz * 0.2, ys - lw * 3, bb.W + sz * 0.4, lw * 6, acc, 0.18 * a0, false);
                env.Rect(bb.X0 - sz * 0.2, ys - lw / 2, bb.W + sz * 0.4, lw, acc, a0, false);
            }
        },
    });

    // ---------------- stripesOut（X.stripesOut；JS ae:'wipe'） ----------------

    private static void StripesOut() => Reg1("stripesOut", new MoveDef
    {
        Name = "ストライプ消去",
        Tags = new[] { "graphic", "pop" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var sz = N0(it, "size");
            var bb = DBox(it, sz * 0.12);
            var acc = ScK(env, "accent") ?? "#ffffff";
            var ang = (CutBit(env, 41) ? 64 : 116) * J.DEG;
            var ld = new[] { Math.Cos(ang), Math.Sin(ang) };
            var ad = new[] { -ld[1], ld[0] };
            double[,] cs = { { bb.X0, bb.Y0 }, { bb.X1, bb.Y0 }, { bb.X0, bb.Y1 }, { bb.X1, bb.Y1 } };
            var asx = new double[4];
            var lsx = new double[4];
            for (var i = 0; i < 4; i++)
            {
                asx[i] = cs[i, 0] * ad[0] + cs[i, 1] * ad[1];
                lsx[i] = cs[i, 0] * ld[0] + cs[i, 1] * ld[1];
            }
            var a0 = asx.Min();
            var a1 = asx.Max();
            var l0 = lsx.Min();
            var l1 = lsx.Max();
            var n = Math.Max(2, (int)Math.Ceiling((a1 - a0) / Math.Max(6, sz * 0.38)));
            if (n > 48) n = 48;
            var sw = (a1 - a0) / n;
            var rem = new List<double[]>();
            var caps = new List<double[]>();
            var rev = CutBit(env, 42);
            for (var k = 0; k < n; k++)
            {
                var o = n > 1 ? k / (double)(n - 1) : 0;
                var stt = (rev ? 1 - o : o) * 0.42;
                var q = J.E.InOutCubic(J.Clamp((p - stt) / 0.58));
                if (q >= 1) continue;
                var fromStart = (k & 1) == 1;
                var la = fromStart ? J.Lerp(l0, l1, q) : l0;
                var lb = fromStart ? l1 : J.Lerp(l1, l0, q);
                rem.Add(new[] { a0 + k * sw, a0 + (k + 1) * sw + 0.6, la, lb });
                if (q > 0)
                    caps.Add(new[] { a0 + k * sw, a0 + (k + 1) * sw, fromStart ? la : lb, fromStart ? 1 : -1, 1 - J.Smooth(0.8, 1, q) });
            }
            if (rem.Count == 0)
            {
                it["alpha"] = 0.0;
                return;
            }
            // JS clipFn：斜条纹四边形并集（pt(a,l) = Ad*a + Ld*l）→ Env.ClipFn 桥
            var remLoc = rem;
            env.ClipFn = (_, _) =>
            {
                double[] Pt(double a2, double l2) => new[] { ad[0] * a2 + ld[0] * l2, ad[1] * a2 + ld[1] * l2 };
                var path = new SKPath();
                foreach (var r in remLoc)
                {
                    var a2 = Pt(r[0], r[2]);
                    var b2 = Pt(r[1], r[2]);
                    var c2 = Pt(r[1], r[3]);
                    var e2 = Pt(r[0], r[3]);
                    path.MoveTo((float)a2[0], (float)a2[1]);
                    path.LineTo((float)b2[0], (float)b2[1]);
                    path.LineTo((float)c2[0], (float)c2[1]);
                    path.LineTo((float)e2[0], (float)e2[1]);
                    path.Close();
                }
                return path;
            };
            var th = Math.Max(2, sz * 0.045);
            var al = NN(it, "alpha", 1);
            var tb = DBox(it, sz * 0.02);
            if (caps.Count > 0 && env.Pass == "main")
            {
                // JS chainPost（刻度帽，clip 到 tb）——就地执行
                var capsLoc = caps;
                env.Ctx.Save();
                env.Ctx.ClipRect(new SKRect((float)tb.X0, (float)tb.Y0, (float)(tb.X0 + tb.W), (float)(tb.Y0 + tb.H))); // 刻度只画在文字范围内
                foreach (var cap in capsLoc)
                    env.Poly(new[]
                    {
                        new[] { ad[0] * (cap[0] + 0.5) + ld[0] * (cap[2] - cap[3] * th), ad[1] * (cap[0] + 0.5) + ld[1] * (cap[2] - cap[3] * th) },
                        new[] { ad[0] * (cap[1] - 0.5) + ld[0] * (cap[2] - cap[3] * th), ad[1] * (cap[1] - 0.5) + ld[1] * (cap[2] - cap[3] * th) },
                        new[] { ad[0] * (cap[1] - 0.5) + ld[0] * cap[2], ad[1] * (cap[1] - 0.5) + ld[1] * cap[2] },
                        new[] { ad[0] * (cap[0] + 0.5) + ld[0] * cap[2], ad[1] * (cap[0] + 0.5) + ld[1] * cap[2] },
                    }, acc, al * cap[4], false);
                env.Ctx.Restore();
            }
        },
    });

    // ---------------- halftoneOut（X.halftoneOut；JS ae:'shrink'） ----------------

    private static void HalftoneOut() => Reg1("halftoneOut", new MoveDef
    {
        Name = "網点に消える",
        Tags = new[] { "graphic", "calm" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var sz = N0(it, "size");
            var bb = DBox(it, sz * 0.1);
            var w = bb.X1 - bb.X0;
            var h = bb.Y1 - bb.Y0;
            var vert = B(it, "vertical");
            var c = Math.Max(3, sz * 0.15);
            while ((w / c + 2) * (h / (c * 0.866) + 2) > 220) c *= 1.1;
            var rows = (int)Math.Ceiling(h / (c * 0.866)) + 1;
            var cols = (int)Math.Ceiling(w / c) + 2;
            var r0 = c * 0.64;
            var rev = CutBit(env, 51);
            var dots = new List<double>();
            for (var r = 0; r < rows; r++)
                for (var q = 0; q < cols; q++)
                {
                    var cx = bb.X0 + (q + (r & 1) * 0.5 - 0.5) * c;
                    var cy = bb.Y0 + r * c * 0.866;
                    var pos = vert ? (cy - bb.Y0) / h * 0.8 + (cx - bb.X0) / w * 0.2 : (cx - bb.X0) / w * 0.8 + (cy - bb.Y0) / h * 0.2;
                    if (rev) pos = 1 - pos;
                    var k = J.Clamp(p * 1.55 - pos * 0.55);
                    var rr = r0 * (1 - J.E.InOutSine(k));
                    if (rr > 0.3)
                    {
                        dots.Add(cx);
                        dots.Add(cy);
                        dots.Add(rr);
                    }
                }
            if (dots.Count == 0)
            {
                it["alpha"] = 0.0;
                return;
            }
            // JS clipFn：网点并集（moveTo + arc 逐圆）→ Env.ClipFn 桥
            var dotsLoc = dots;
            env.ClipFn = (_, _) =>
            {
                var path = new SKPath();
                for (var j = 0; j < dotsLoc.Count; j += 3)
                    path.AddCircle((float)dotsLoc[j], (float)dotsLoc[j + 1], (float)dotsLoc[j + 2]);
                return path;
            };
            it["color"] = MixC(ColOf(it), ScK(env, "accent") ?? "#ffffff", 0.4 * J.Smooth(0.05, 0.5, p));
        },
    });

    // ---------------- eraserOut（X.eraserOut；JS ae:'wipe'） ----------------

    private static void EraserOut() => Reg1("eraserOut", new MoveDef
    {
        Name = "黒板消し",
        Tags = new[] { "editorial", "calm" },
        W = 0.9,
        MinDur = 0.9,
        OutDur = (dur, _) => J.Clamp(dur * 0.42, 0.36, 0.85),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var sz = N0(it, "size");
            var vert = B(it, "vertical");
            var single = IsSingle(env, it);
            var bb = single
                ? new Dbx { X0 = env.W * 0.03, Y0 = env.H * 0.3, X1 = env.W * 0.97, Y1 = env.H * 0.7, Cx = env.W / 2.0, Cy = env.H / 2.0, W = env.W * 0.94, H = env.H * 0.4 }
                : DBox(it, sz * 0.12);
            var uu = single ? env.H * 0.13 : sz;
            var span = vert ? bb.W : bb.H;
            var len = vert ? bb.H : bb.W;
            var kN = single ? 3 : J.Clamp((int)Math.Round(span / (uu * 0.7)), 2, 10);
            var lh = span / kN;
            var ew = uu * 0.8;
            var leg = len + ew;
            var pos = J.Clamp(p / 0.9) * kN * leg;
            var lane = Math.Min(kN - 1, (int)Math.Floor(pos / leg));
            var along = pos - lane * leg - ew / 2; // 板擦中心到该条起点的距离
            bool Fwd(int j) => (j & 1) == 0;

            // 第 j 条的横向区间和擦除轴区间（s 从条起点沿擦除方向量）
            double[] LaneR(int j, double s0, double s1)
            {
                var a0 = vert ? bb.X1 - (j + 1) * lh : bb.Y0 + j * lh;
                var l0 = vert ? bb.Y0 : bb.X0;
                var t0 = Fwd(j) ? l0 + s0 : l0 + len - s1;
                var t1 = Fwd(j) ? l0 + s1 : l0 + len - s0;
                return vert ? new[] { a0, t0, lh, t1 - t0 } : new[] { t0, a0, t1 - t0, lh };
            }
            var cut = J.Clamp(along + ew * 0.3, 0, len);
            var keep = new List<double[]>();
            var gone = new List<double[]>();
            if (along + ew * 0.3 < len + 0.5) keep.Add(LaneR(lane, cut, len));
            for (var j = lane + 1; j < kN; j++) keep.Add(LaneR(j, 0, len));
            for (var j = 0; j < lane; j++) gone.Add(LaneR(j, 0, len));
            if (cut > 0) gone.Add(LaneR(lane, 0, cut));
            if (keep.Count == 0 && p > 0.9) it["alpha"] = 0.0;
            var a0 = NN(it, "alpha", 1);
            // JS clipFn = pad(keep, ctx)（保留矩形并集，w/h 各 +0.4）→ Env.ClipFn 桥
            var keepLoc = keep;
            env.ClipFn = (_, _) => RectsPath(keepLoc, 0.4);
            var smear = a0 * 0.26 * (1 - J.Smooth(0.35, 0.95, p));
            var eA = 1 - J.Smooth(0.9, 1, p);
            var acc = ScK(env, "accent") ?? "#ffffff";
            var felt = MixC(acc, ScK(env, "bg") ?? "#ffffff", 0.45);
            var drawEraser = !single || Math.Round(Num(it, "mi")) == 0;
            // JS chainPost——就地执行
            if (smear > 0.01 && gone.Count > 0 && env.Pass == "main")
            {
                // 粉笔痕：加模糊，顺擦除方向拖长
                var goneLoc = gone;
                using var gonePath = RectsPath(goneLoc, 0.4);
                DrawFxCopy(env, it, MergedNow(env), gonePath, c =>
                {
                    c["alpha"] = smear;
                    c["blur"] = Math.Min(sz * 0.1, 28);
                    c["gradient"] = null;
                    c["pattern"] = null;
                    c["shadow"] = null;
                    c["extrude"] = null;
                    c["color"] = MixC(ColOf(it), ScK(env, "bg") ?? "#ffffff", 0.25);
                    if (vert) c["sy"] = N1(it, "sy") * 1.12;
                    else c["sx"] = N1(it, "sx") * 1.1;
                });
            }
            if (!drawEraser || eA <= 0.01) return;
            var cc = J.Clamp(along, -ew / 2, len + ew / 2);
            var r = LaneR(lane, cc - ew / 2, cc + ew / 2);
            var x = r[0] - (vert ? lh * 0.03 : 0);
            var y = r[1] - (vert ? 0 : lh * 0.03);
            var w = r[2] + (vert ? lh * 0.06 : 0);
            var h = r[3] + (vert ? 0 : lh * 0.06);
            env.RRect(x, y, w, h, Math.Min(w, h) * 0.18, acc, eA, false);
            const double kf = 0.3; // 拖尾一侧的毛毡条
            if (vert)
            {
                var fy = Fwd(lane) ? y : y + h * (1 - kf);
                env.RRect(x, fy, w, h * kf, Math.Min(w, h) * 0.12, felt, eA, false);
            }
            else
            {
                var fx = Fwd(lane) ? x : x + w * (1 - kf);
                env.RRect(fx, y, w * kf, h, Math.Min(w, h) * 0.12, felt, eA, false);
            }
        },
    });

    // ---------------- vacuumOut（X.vacuumOut；JS ae:'shrink'） ----------------

    private static void VacuumOut() => Reg1("vacuumOut", new MoveDef
    {
        Name = "一点に吸われる",
        Tags = new[] { "pop", "graphic" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var vert = B(it, "vertical");
            var sz = N0(it, "size");
            var acc = ScK(env, "accent") ?? "#ffffff";
            var rev = CutBit(env, 61);
            var seed = SeedOf(it);
            var single = IsSingle(env, it);

            void Dot(double[] t, double fr, bool drawIt)
            {
                if (!drawIt) return;
                var pulse = 1 + 0.25 * Math.Sin(p * 40) * (1 - p);
                var rr = sz * (0.05 + 0.09 * fr) * pulse * (1 - J.Smooth(0.9, 1, p));
                var ring = J.Clamp((p - 0.86) / 0.14);
                // JS chainPost——就地执行
                if (rr > 0.3) env.Circle(t[0], t[1], rr, acc, null, 0, 1, true);
                if (ring > 0 && ring < 1)
                    env.Circle(t[0], t[1], sz * (0.15 + 0.5 * J.E.OutCubic(ring)), null, acc, Math.Max(1, sz * 0.03 * (1 - ring)), 1 - ring, false);
            }
            if (single)
            {
                // 单字 item 整体飞向屏幕上一点
                var t = new[] { rev ? env.W * 0.08 : env.W * 0.92, env.H / 2.0 };
                var d0 = Math.Sqrt(Math.Pow(t[0] - N0(it, "x"), 2) + Math.Pow(t[1] - N0(it, "y"), 2));
                var dm = env.W * 0.85;
                var q = Win(p, J.Clamp(d0 / dm), 0.6);
                if (q >= 1) it["alpha"] = 0.0;
                else if (q > 0)
                {
                    var e = J.E.InCubic(q);
                    var bend = (J.R(unchecked((int)(env.Cut?.Seed ?? 0)), (int)Num(it, "mi"), 62) - 0.5) * d0 * 0.5;
                    var nx = -(t[1] - N0(it, "y")) / Math.Max(1, d0);
                    var ny = (t[0] - N0(it, "x")) / Math.Max(1, d0);
                    it["x"] = N0(it, "x") + (t[0] - N0(it, "x")) * e + nx * bend * Math.Sin(Math.PI * e);
                    it["y"] = N0(it, "y") + (t[1] - N0(it, "y")) * e + ny * bend * Math.Sin(Math.PI * e);
                    it["size"] = N0(it, "size") * (1 - 0.85 * e);
                    it["sx"] = N1(it, "sx") * (1 + 1.3 * q * q);
                    it["sy"] = N1(it, "sy") * (1 - 0.4 * q * q);
                }
                Dot(t, J.Clamp(p * 1.2), Math.Round(Num(it, "mi")) == 0);
                return;
            }
            var b = LBox(it);
            var lay = LayOf(it);
            var tl = vert
                ? new[] { b.Cx, rev ? b.Y0 - sz * 0.8 : b.Y1 + sz * 0.8 }
                : new[] { rev ? b.X0 - sz * 0.8 : b.X1 + sz * 0.8, b.Cy };
            var dm2 = 1.0;
            foreach (var g in lay.Cells)
                dm2 = Math.Max(dm2, Math.Sqrt(Math.Pow(tl[0] - GCx(g, it), 2) + Math.Pow(tl[1] - GCy(g, it), 2)));
            var alongX = !vert;
            Glyphs(env, (i, g, _) =>
            {
                if (g == null) return null;
                var gx = GCx(g, it);
                var gy = GCy(g, it);
                var vx = tl[0] - gx;
                var vy = tl[1] - gy;
                var dd = Math.Sqrt(vx * vx + vy * vy);
                var q = Win(p, J.Clamp((dd - sz * 0.8) / Math.Max(1, dm2 - sz * 0.8)), 0.5);
                if (q <= 0) return null;
                if (q >= 0.94) return new CharXf { Hide = true };
                var e = Math.Min(1, J.E.InQuad(q / 0.94));
                var bend = J.Rs(seed, i, 63) * dd * 0.1 * Math.Sin(Math.PI * e);
                var nx = -vy / Math.Max(1, dd);
                var ny = vx / Math.Max(1, dd);
                var st = Math.Min(1.8, 3 * q * q);
                var along = alongX != B(g, "r90"); // 拉伸沿行进方向的那个局部轴
                return new CharXf
                {
                    Dx = vx * e + nx * bend,
                    Dy = vy * e + ny * bend,
                    S = 1 - 0.8 * e,
                    Sx = along ? 1 + st : 1 - 0.3 * st / 1.8,
                    Sy = along ? 1 - 0.3 * st / 1.8 : 1 + st,
                };
            });
            var fr = 0;
            foreach (var g in lay.Cells)
            {
                var dd2 = Math.Sqrt(Math.Pow(tl[0] - GCx(g, it), 2) + Math.Pow(tl[1] - GCy(g, it), 2));
                if (Win(p, J.Clamp((dd2 - sz * 0.8) / Math.Max(1, dm2 - sz * 0.8)), 0.5) >= 0.94) fr++;
            }
            Dot(ToD(it, tl[0], tl[1]), fr / (double)Math.Max(1, lay.N), true);
        },
    });

    // ---------------- sandOut（X.sandOut；JS ae:'drift'） ----------------

    private static void SandOut() => Reg1("sandOut", new MoveDef
    {
        Name = "砂になって飛ぶ",
        Tags = new[] { "emotional", "calm" },
        W = 1,
        OutDur = (dur, _) => J.Clamp(dur * 0.44, 0.36, 0.9),
        MinDur = 0.8,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var seed = SeedOf(it);
            var sz = N0(it, "size");
            var vert = B(it, "vertical");
            var dir = CutBit(env, 71) ? 1 : -1;
            var wi = DirI(it, dir, 0);
            var ui = DirI(it, 0, -1);
            var sdir = Math.Atan2(wi[1], wi[0]) / J.DEG;
            var b = LBox(it);
            var single = IsSingle(env, it);
            var og = OrderOf(env, it)(0, 1);

            double PosOf(double ox, double oy)
            {
                var pos = vert ? (oy - b.Y0) / Math.Max(1, b.H) : (ox - b.X0) / Math.Max(1, b.W);
                if (!vert && dir > 0) pos = 1 - pos;
                if (single) pos = 0.8 * (dir > 0 ? 1 - og : og) + 0.2 * pos;
                return J.Clamp(pos);
            }
            var dd = sz * 3 + env.W * 0.18;
            double[] Move(double x, double k1)
            {
                var along = dd * x * x;
                var lift = sz * 0.55 * x + Math.Sin(x * 5 + k1 * 6) * sz * 0.07 * x;
                return new[] { wi[0] * along + ui[0] * lift, wi[1] * along + ui[1] * lift };
            }
            if (it["gradient"] != null || IsFalse(it["fill"]) || it["dash"] != null || it["pattern"] != null)
            {
                // 带这些属性做不了 piece，退回字形级近似
                Glyphs(env, (i, g, _) =>
                {
                    if (g == null) return null;
                    var x = (p - PosOf(GCx(g, it), GCy(g, it)) * 0.5) / 0.5;
                    if (x <= 0) return null;
                    if (x >= 1) return new CharXf { Hide = true };
                    var m = Move(x, J.R(seed, i, 82));
                    return new CharXf
                    {
                        Dx = m[0],
                        Dy = m[1],
                        A = 1 - x,
                        S = 1 - 0.4 * x,
                        Sx = 1 + x,
                        Blur = env.Pass == "main" ? sz * 0.05 * x : 0,
                    };
                });
                return;
            }
            it["shatter"] = true;
            env.PieceFns.Add((ci, pj, _, ox, oy, _) =>
            {
                var t0 = PosOf(ox, oy) * 0.5 + J.R(seed, ci, pj, 81) * 0.18;
                var x = (p - t0) / 0.32;
                if (x <= 0) return JAnim.Pid;
                if (x >= 1) return null;
                var m = Move(x, J.R(seed, ci, pj, 82));
                return JAnim.Pt(m[0], m[1], J.Rs(seed, ci, pj, 83) * 140 * x, 1 - 0.55 * x, 1 + 2.4 * x, sdir, 1 - x * x);
            });
        },
    });

    // ---------------- shredOut（X.shredOut；JS ae:'fall'） ----------------

    private static void ShredOut() => Reg1("shredOut", new MoveDef
    {
        Name = "シュレッダー",
        Tags = new[] { "graphic", "pop" },
        W = 0.8,
        MinDur = 0.9,
        OutDur = (dur, _) => J.Clamp(dur * 0.44, 0.38, 0.9),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var sz = N0(it, "size");
            var seed = SeedOf(it);
            var single = IsSingle(env, it);
            var bb = DBox(it, sz * 0.06);
            var ySlot = single ? Math.Max(bb.Y1 + sz * 0.1, env.H * 0.66) : bb.Y1 + sz * 0.12;
            var travel = ySlot - bb.Y0 + sz * 0.1;
            var f = J.Clamp((p - 0.08) / 0.72);
            var dd = travel * (0.2 * f * f + 0.8 * f);
            var v = J.Clamp((p - 0.72) / 0.28);
            var drop = env.H * 0.9 * v * v;
            it["y"] = N0(it, "y") + dd;
            it["clipY"] = new JArray(-1e5, ySlot); // 口以上完好
            var n = J.Clamp((int)Math.Round(bb.W / (sz * 0.2)), 5, 20);
            var sw = bb.W / n;
            var below = Math.Max(0, bb.Y1 + dd - ySlot);
            var curl = J.Clamp(below / (sz * 1.5));
            var ink = ScK(env, "ink") ?? ScK(env, "fg") ?? "#ffffff";
            var lw = Math.Max(2, sz * 0.05);
            var barA = J.Smooth(0, 0.1, p) * (1 - J.Smooth(0.82, 0.98, p));
            var a0 = NN(it, "alpha", 1) * (1 - J.Smooth(0.8, 0.97, p));
            // JS chainPost——就地执行
            if (below > 0.5 && env.Pass == "main")
                for (var k = 0; k < n; k++)
                {
                    var xs0 = bb.X0 + k * sw;
                    var xs1 = xs0 + sw * 0.74;
                    var xc = (xs0 + xs1) / 2;
                    var ang = ((k - (n - 1) / 2.0) / Math.Max(1, n) * 0.5 + J.Rs(seed, k, 131) * 0.12) * curl + Math.Sin(p * 9 + k) * 0.03 * curl;
                    var lx0 = xs0 - N0(it, "x") - sz * 0.6;
                    var lx1 = xs1 - N0(it, "x") + sz * 0.6;
                    var cfExtra = (JAnim.CharFn)((_, g, _) =>
                    {
                        if (g == null) return new CharXf { Hide = true };
                        var gx = GCx(g, it);
                        return gx + D(g, "w") * N1(it, "sx") / 2 < lx0 || gx - D(g, "w") * N1(it, "sx") / 2 > lx1
                            ? new CharXf { Hide = true }
                            : null;
                    });
                    var cf = JAnim.CombineChar(env.CharFns.Concat(new[] { cfExtra }).ToList());
                    env.Ctx.Save();
                    env.Ctx.Translate((float)xc, (float)ySlot);
                    env.Ctx.RotateDegrees((float)(ang / J.DEG)); // ang 是弧度，RotateDegrees 收角度
                    env.Ctx.RotateDegrees((float)(ang / J.DEG));
                    env.Ctx.Translate((float)-xc, (float)(-ySlot + drop * (0.7 + 0.6 * J.R(seed, k, 132))));
                    env.Ctx.ClipRect(new SKRect((float)xs0, (float)ySlot, (float)xs1, (float)(ySlot + env.H * 3)));
                    DrawCopy(env, it, cf, c => c["alpha"] = a0);
                    env.Ctx.Restore();
                }
            if (barA > 0.01)
            {
                var w = (bb.W + sz * 0.5) * J.E.OutCubic(J.Smooth(0, 0.12, p));
                env.Rect(bb.Cx - w / 2, ySlot - lw / 2, w, lw, ink, barA, false);
            }
        },
    });

    // ---------------- dominoOut（X.dominoOut；JS ae:'fall'） ----------------

    private static void DominoOut() => Reg1("dominoOut", new MoveDef
    {
        Name = "ドミノ倒し",
        Tags = new[] { "pop", "graphic" },
        W = 0.9,
        MinDur = 0.8,
        OutDur = (dur, n) => J.Clamp(dur * 0.42, 0.36, 0.85),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var ord0 = OrderOf(env, it);
            var rev = CutBit(env, 91);
            var sgn = rev ? -1 : 1;
            var sx0 = N1(it, "sx");
            var sy0 = N1(it, "sy");
            var sz = N0(it, "size");
            double Ord(int i, int n) => rev ? 1 - ord0(i, n) : ord0(i, n);
            var fade = 1 - J.Smooth(0.74, 0.98, p);
            var sink = sz * 0.9 * J.E.InQuad(J.Clamp((p - 0.7) / 0.3));
            var dn = DownI(it);
            Glyphs(env, (i, g, n) =>
            {
                if (g == null) return null;
                var q = Win(p, Ord(i, n), 0.55);
                if (q <= 0) return null;
                const double tf = 0.62;
                double th;
                if (q < tf) th = 90 * J.E.InCubic(q / tf);
                else
                {
                    var u = (q - tf) / (1 - tf);
                    th = 90 - 10 * Math.Sin(Math.PI * Math.Min(1, u * 1.6)) * (1 - u);
                }
                var px = sgn * D(g, "w") * sx0 / 2;
                var py = sz * sy0 * 0.5; // 倒下那侧的底角，item 空间
                var rr = Rot2(-px, -py, sgn * th * J.DEG);
                return new CharXf { Dx = px + rr[0] + dn[0] * sink, Dy = py + rr[1] + dn[1] * sink, Rot = sgn * th, A = fade };
            });
        },
    });

    // ---------------- hingeOut（X.hingeOut；JS ae:'fall'） ----------------

    private static void HingeOut() => Reg1("hingeOut", new MoveDef
    {
        Name = "片留めが外れる",
        Tags = new[] { "pop", "editorial" },
        W = 0.9,
        MinDur = 0.9,
        OutDur = (dur, _) => J.Clamp(dur * 0.46, 0.42, 0.95),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var single = IsSingle(env, it);
            var q = single ? Win(p, J.R(unchecked((int)(env.Cut?.Seed ?? 0)), (int)Num(it, "mi"), 101), 0.3) : p;
            if (q <= 0) return;
            var sz = N0(it, "size");
            var b = LBox(it, sz * 0.02);
            var vert = B(it, "vertical");
            var left = !CutBit(env, 102);
            var acc = ScK(env, "accent") ?? "#ffffff";
            var sg = left ? 1 : -1;
            var ax = (left ? b.X0 : b.X1) + sg * Math.Min(sz * 0.14, b.W * 0.2);
            var ay = b.Y0 + Math.Min(sz * 0.12, b.H * 0.2);
            var reach = Math.Max(1, b.W - sz * 0.14);
            var hang = vert ? 24 : Math.Min(58, Math.Max(14, Math.Asin(J.Clamp(env.H * 0.3 / reach, 0, 1)) / J.DEG));
            const double t1 = 0.64;
            var u = J.Clamp(q / t1);
            var v = J.Clamp((q - t1) / (1 - t1));
            var th = hang * (1 - Math.Exp(-u * 4.5) * Math.Cos(u * 12)) + 30 * v * v;
            var pin0 = ToD(it, ax, ay);
            var bx = left ? b.X1 - sz * 0.14 : b.X0 + sz * 0.14;
            var pin1 = ToD(it, bx, ay);
            RotateAbout(it, sg * th, ax, ay);
            it["y"] = N0(it, "y") + env.H * 1.25 * v * v;
            it["x"] = N0(it, "x") + sg * sz * 0.4 * v;
            it["alpha"] = NN(it, "alpha", 1) * (1 - J.Smooth(0.9, 1, q));
            var r = Math.Max(2, sz * 0.045);
            var pa = 1 - J.Smooth(0.05, 0.25, v);
            var fl = J.Clamp(q / 0.16);
            // JS chainPost（销钉）——就地执行
            if (pa > 0.01) env.Circle(pin0[0], pin0[1], r, acc, null, 0, pa, false);
            if (fl < 1) env.Circle(pin1[0] + sg * sz * 0.5 * fl, pin1[1] - sz * 0.6 * fl + sz * 1.6 * fl * fl, r, acc, null, 0, 1 - fl, false);
        },
    });

    // ---------------- rocketOff（X.rocketOff；JS ae:'drift'） ----------------

    private static void RocketOff() => Reg1("rocketOff", new MoveDef
    {
        Name = "打ち上げ",
        Tags = new[] { "pop" },
        W = 0.9,
        MinDur = 0.8,
        OutDur = (dur, _) => J.Clamp(dur * 0.44, 0.36, 0.9),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var seed = SeedOf(it);
            var sz = N0(it, "size");
            var acc = ScK(env, "accent") ?? "#ffffff";
            var sub = ScK(env, "sub") ?? "#ffffff";
            var sy0 = N1(it, "sy");
            var lay = LayOf(it);
            var dn = DownI(it);
            var bb = DBox(it, 0);
            var dd = bb.Y1 + sz * 1.6;
            var step = (int)env.Step;
            double Q(int i) => Win(p, J.R(seed, i, 111) * 0.85, 0.55);
            double Lift(double u) => dd * Math.Pow(u, 2.3);
            Glyphs(env, (i, g, _) =>
            {
                if (g == null) return null;
                var q = Q(i);
                if (q <= 0) return null;
                if (q >= 1) return new CharXf { Hide = true };
                var hh = D(g, "h") * sy0 / 2;
                var r90 = B(g, "r90");
                if (q < 0.3)
                {
                    var u = q / 0.3;
                    var sq = Math.Sin(u * Math.PI / 2);
                    var k = 1 - 0.22 * sq;
                    var w = 1 + 0.12 * sq;
                    var sh = J.Rs(seed, i, step, 112) * sz * 0.02 * u;
                    return new CharXf { Dx = dn[0] * (1 - k) * hh + sh, Dy = dn[1] * (1 - k) * hh, Sx = r90 ? k : w, Sy = r90 ? w : k };
                }
                var u2 = (q - 0.3) / 0.7;
                var l = Lift(u2);
                var st = 1 + Math.Min(0.8, 3 * u2 * u2);
                return new CharXf
                {
                    Dx = -dn[0] * l,
                    Dy = -dn[1] * l,
                    Sx = r90 ? st : 1 - 0.12 * (st - 1),
                    Sy = r90 ? 1 - 0.12 * (st - 1) : st,
                };
            });
            // JS chainPost（尾焰 + 扬尘）——就地执行
            if (env.Pass != "main") return;
            InItem(env, it, () =>
            {
                foreach (var g in lay.Cells)
                {
                    if (IsSp(g.Ch)) continue;
                    var q = Q(g.I);
                    if (q < 0.3 || q >= 1) continue;
                    var u = (q - 0.3) / 0.7;
                    var l = Lift(u);
                    var hh = g.H * sy0 / 2;
                    var gx = GCx(g, it);
                    var gy = GCy(g, it);
                    var bx = gx - dn[0] * l + dn[0] * hh;
                    var by = gy - dn[1] * l + dn[1] * hh; // 字形底部当前位置
                    var tl = Math.Min(l, sz * 2.6);
                    var a = 1 - u * 0.8;
                    env.Line(new[] { new[] { bx, by }, new[] { bx + dn[0] * tl, by + dn[1] * tl } }, acc, Math.Max(1.5, sz * 0.2 * (1 - u * 0.5)), 0.28 * a, false);
                    env.Line(new[] { new[] { bx, by }, new[] { bx + dn[0] * tl * 0.75, by + dn[1] * tl * 0.75 } }, acc, Math.Max(1, sz * 0.08), 0.95 * a, false);
                    env.Poly(new[]
                    {
                        new[] { bx - dn[1] * sz * 0.1, by + dn[0] * sz * 0.1 },
                        new[] { bx + dn[1] * sz * 0.1, by - dn[0] * sz * 0.1 },
                        new[] { bx + dn[0] * sz * 0.45, by + dn[1] * sz * 0.45 },
                    }, acc, a, false);
                    var ox = gx + dn[0] * hh;
                    var oy = gy + dn[1] * hh; // 发射台扬起的烟尘
                    for (var k = 0; k < 3; k++)
                        env.Circle(ox + (k - 1) * sz * 0.25 * (0.4 + u), oy - sz * 0.05, sz * (0.08 + 0.22 * u) * (1 - Math.Abs(k - 1) * 0.3), sub, null, 0, 0.35 * (1 - u), false);
                }
            });
        },
    });

    // ---------------- bounceOff（X.bounceOff；JS ae:'scatter'） ----------------

    /// <summary>越跳越小的弹跳：返回 (高度 0..1, 落地压扁 0..1)。</summary>
    private static (double H, double Land) Hops(double q)
    {
        const double r = 0.64;
        const int nh = 4;
        double s = 0;
        for (var j = 0; j < nh; j++) s += Math.Pow(r, j);
        var t = q * s;
        for (var j = 0; j < nh; j++)
        {
            var d = Math.Pow(r, j);
            if (t <= d || j == nh - 1)
            {
                var u = J.Clamp(t / d);
                var hk = d * d;
                return (hk * 4 * u * (1 - u), Math.Max(0, 1 - Math.Min(u, 1 - u) / 0.09) * Math.Sqrt(hk));
            }
            t -= d;
        }
        return (0, 0);
    }

    private static void BounceOff() => Reg1("bounceOff", new MoveDef
    {
        Name = "弾んで去る",
        Tags = new[] { "pop" },
        W = 0.9,
        MinDur = 0.8,
        OutDur = (dur, _) => J.Clamp(dur * 0.45, 0.38, 0.9),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var dir = CutBit(env, 121) ? 1 : -1;
            var sz = N0(it, "size");
            var ord = OrderOf(env, it);
            var sy0 = N1(it, "sy");
            var ri = RightI(it);
            var dn = DownI(it);
            Glyphs(env, (i, g, n) =>
            {
                if (g == null) return null;
                var o = dir > 0 ? 1 - ord(i, n) : ord(i, n);
                var q = Win(p, o, 0.42);
                if (q <= 0) return null;
                if (q >= 1) return new CharXf { Hide = true };
                var gxD = ToD(it, GCx(g, it), GCy(g, it))[0];
                var dist = dir > 0 ? env.W - gxD + sz * 1.2 : gxD + sz * 1.2;
                var along = dist * Math.Pow(q, 1.25);
                var hp = Hops(q);
                var h = hp.H * sz * 1.4;
                var sq = 0.3 * hp.Land;
                var hh = D(g, "h") * sy0 / 2;
                var r90 = B(g, "r90");
                var k = 1 - sq;
                var w = 1 + sq * 0.7;
                return new CharXf
                {
                    Dx = ri[0] * dir * along - dn[0] * h + dn[0] * (1 - k) * hh,
                    Dy = ri[1] * dir * along - dn[1] * h + dn[1] * (1 - k) * hh,
                    Rot = dir * 14 * Math.Sin(Math.PI * Math.Min(1, hp.H * 4)),
                    Sx = r90 ? k : w,
                    Sy = r90 ? w : k,
                };
            });
        },
    });

    // ---------------- balloonOff（X.balloonOff；JS ae:'drift'） ----------------

    private static void BalloonOff() => Reg1("balloonOff", new MoveDef
    {
        Name = "風船で飛ぶ",
        Tags = new[] { "emotional", "calm", "pop" },
        W = 0.9,
        MinDur = 0.9,
        OutDur = (dur, _) => J.Clamp(dur * 0.48, 0.42, 1.0),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var seed = SeedOf(it);
            var sz = N0(it, "size");
            var sub = ScK(env, "sub") ?? "#ffffff";
            var sy0 = N1(it, "sy");
            var lay = LayOf(it);
            var bb = DBox(it, 0);
            var dd = bb.Y1 + sz * 1.8;
            double Q(int i) => Win(p, J.R(seed, i, 141), 0.45);
            (double L, double Sw, double Rot, double Inf) St(int i, double q)
            {
                var l = dd * Math.Pow(q, 1.7);
                var ph = J.R(seed, i, 142) * J.TAU;
                var w = 7 + 3 * J.R(seed, i, 143);
                var amp = Math.Min(1, q * 3);
                return (l, Math.Sin(q * w + ph) * sz * 0.22 * amp, -Math.Cos(q * w + ph) * 11 * amp, 1 + 0.07 * Math.Sin(Math.PI * Math.Min(1, q * 2.5)));
            }
            Glyphs(env, (i, g, _) =>
            {
                if (g == null) return null;
                var q = Q(i);
                if (q <= 0) return null;
                if (q >= 1) return new CharXf { Hide = true };
                var s = St(i, q);
                var dxy = DirI(it, s.Sw, -s.L);
                return new CharXf { Dx = dxy[0], Dy = dxy[1], Rot = s.Rot, S = s.Inf };
            });
            // JS chainPost（绳子拖尾，摆动带滞后）——就地执行
            if (env.Pass != "main") return;
            InItem(env, it, () =>
            {
                var dn = DownI(it);
                var ri = RightI(it);
                foreach (var g in lay.Cells)
                {
                    if (IsSp(g.Ch)) continue;
                    var q = Q(g.I);
                    if (q <= 0 || q >= 1) continue;
                    var s = St(g.I, q);
                    var dxy = DirI(it, s.Sw, -s.L);
                    var r = s.Rot * J.DEG;
                    var hh = g.H * sy0 / 2 * s.Inf;
                    var bx = GCx(g, it) + dxy[0] - Math.Sin(r) * hh;
                    var by = GCy(g, it) + dxy[1] + Math.Cos(r) * hh;
                    var pts = new double[7][];
                    for (var k = 0; k <= 6; k++)
                    {
                        var u = k / 6.0;
                        var lag = Math.Sin(q * 8 + J.R(seed, g.I, 142) * J.TAU - u * 1.6) * sz * 0.1 * u;
                        pts[k] = new[] { bx + dn[0] * sz * 0.95 * u + ri[0] * lag, by + dn[1] * sz * 0.95 * u + ri[1] * lag };
                    }
                    env.Line(pts, sub, Math.Max(1, sz * 0.014), 0.85 * Math.Min(1, q * 8), false);
                }
            });
        },
    });

    // ---------------- deflateOut（X.deflateOut；JS ae:'scatter'） ----------------

    private static void DeflateOut() => Reg1("deflateOut", new MoveDef
    {
        Name = "しぼんで飛ぶ",
        Tags = new[] { "pop" },
        W = 0.8,
        OutDur = (dur, _) => J.Clamp(dur * 0.42, 0.36, 0.85),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var seed = SeedOf(it);
            var sz = N0(it, "size");
            Glyphs(env, (i, _, _) =>
            {
                var q = Win(p, J.R(seed, i, 151) * 0.8, 0.4);
                if (q <= 0) return null;
                if (q >= 0.97) return new CharXf { Hide = true };
                if (q < 0.16)
                {
                    // 鼓起绷紧
                    var u = q / 0.16;
                    return new CharXf
                    {
                        S = 1 + 0.16 * J.E.OutCubic(u),
                        Sx = 1 + 0.05 * Math.Sin(u * 25),
                        Sy = 1 - 0.05 * Math.Sin(u * 25),
                        Rot = J.Rs(seed, i, 152) * 4 * u,
                    };
                }
                var u2 = (q - 0.16) / 0.81;
                var th0 = J.R(seed, i, 153) * J.TAU;
                double x = 0, y = 0;
                var th = th0;
                const int ns = 14;
                for (var k = 0; k < ns; k++)
                {
                    // 分段积分乱窜的航向
                    var t = (k + 0.5) / ns * u2;
                    th = th0 + 7 * J.Noise1(t * 5, seed + i * 7) + 3 * t;
                    var v = sz * 7.5 * (0.5 + t) * (u2 / ns);
                    x += Math.Cos(th) * v;
                    y += Math.Sin(th) * v;
                }
                var wob = Math.Sin(u2 * 70) * 0.14 * (1 - u2);
                return new CharXf
                {
                    Dx = x,
                    Dy = y,
                    Rot = (th - th0) / J.DEG * 0.5,
                    S = 1.16 * (1 - Math.Pow(u2, 1.3)),
                    Sx = 1 + wob,
                    Sy = 1 - wob,
                };
            });
        },
    });

    // ---------------- hazeOut（X.hazeOut；JS ae:'blur'） ----------------

    private static void HazeOut() => Reg1("hazeOut", new MoveDef
    {
        Name = "陽炎に消える",
        Tags = new[] { "emotional", "calm" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var sz = N0(it, "size");
            var t = env.Ltb;
            var vert = B(it, "vertical");
            var e = J.E.InQuad(p);
            var b0 = Box(it);
            ScaleAbout(it, 1 - 0.05 * e, 1 + 0.3 * e, b0.Cx - N0(it, "x"), b0.Y1 - N0(it, "y"));
            it["y"] = N0(it, "y") - sz * 0.3 * e;
            it["color"] = MixC(ColOf(it), ScK(env, "accent") ?? "#ffffff", 0.3 * J.Smooth(0.1, 0.7, p));
            it["alpha"] = NN(it, "alpha", 1) * (1 - J.Smooth(0.3, 0.94, p));
            var bb = DBox(it, sz * 0.3);
            var a = sz * (0.02 + 0.26 * J.E.InOutSine(p));
            if (a < 0.4 || env.Pass != "main") return;
            if (!vert)
            {
                var n = J.Clamp((int)Math.Ceiling(bb.H / (sz * 0.09)), 6, IsSingle(env, it) ? 7 : 14);
                var bh = bb.H / n;
                var @out = new JArray();
                for (var j = 0; j < n; j++)
                {
                    var y = bb.Y0 + j * bh;
                    var c = (y + bh / 2) / sz;
                    @out.Add(new JArray(y, y + bh + 0.4, a * Math.Sin(c * 6.5 - t * 12) * (0.6 + 0.4 * Math.Sin(c * 2.3 + t * 5))));
                }
                it["bands"] = @out;
            }
            else
            {
                var n = J.Clamp((int)Math.Ceiling(bb.W / (sz * 0.08)), 6, 16);
                var bw = bb.W / n;
                var @out = new JArray();
                for (var j = 0; j < n; j++)
                {
                    var x = bb.X0 + j * bw;
                    var c = (x + bw / 2) / sz;
                    @out.Add(new JArray(x, x + bw + 0.4, a * Math.Sin(c * 6.5 - t * 12) * (0.6 + 0.4 * Math.Sin(c * 2.3 + t * 5))));
                }
                it["vbands"] = @out;
            }
        },
    });

    // ---------------- glassBreak（X.glassBreak；JS ae:'explode'） ----------------

    private static void GlassBreak() => Reg1("glassBreak", new MoveDef
    {
        Name = "ガラス割れ",
        Tags = new[] { "graphic", "pop", "glitch" },
        W = 0.8,
        OutDur = (dur, _) => J.Clamp(dur * 0.42, 0.36, 0.85),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var single = IsSingle(env, it);
            var q = single ? Win(p, OrderOf(env, it)(0, 1), 0.4) : p;
            if (q <= 0) return;
            var sz = N0(it, "size");
            var seed = SeedOf(it);
            var b = LBox(it, sz * 0.12);
            var ix = b.Cx + J.Rs(seed, 151) * b.W * 0.2;
            var iy = b.Cy + J.Rs(seed, 152) * b.H * 0.12;
            var kN = single ? 4 : J.Clamp((int)Math.Round(5 + b.W / Math.Max(1, b.H) * 0.8), 6, 10);
            var bPts = new double[kN][];
            var rPts = new double[kN][];
            for (var k = 0; k < kN; k++)
            {
                var a = (k + 0.35 * J.Rs(seed, k, 153)) / kN * J.TAU;
                var c = Math.Cos(a);
                var s = Math.Sin(a);
                var tx = c > 0 ? (b.X1 - ix) / c : c < 0 ? (b.X0 - ix) / c : 1e9;
                var ty = s > 0 ? (b.Y1 - iy) / s : s < 0 ? (b.Y0 - iy) / s : 1e9;
                var tt = Math.Min(tx, ty);
                var f = 0.3 + 0.25 * J.R(seed, k, 154);
                bPts[k] = new[] { ix + c * tt, iy + s * tt };
                rPts[k] = new[] { ix + c * tt * f, iy + s * tt * f };
            }
            double Norm(double a2) => ((a2 % J.TAU) + J.TAU) % J.TAU;
            var shards = new List<double[][]>();
            for (var k = 0; k < kN; k++)
            {
                var k2 = (k + 1) % kN;
                shards.Add(new[] { new[] { ix, iy }, rPts[k], rPts[k2] });
                shards.Add(new[] { rPts[k], bPts[k], bPts[k2], rPts[k2] });
            }
            // 落在两根辐条之间的 box 角归外侧碎片
            for (var k = 0; k < kN; k++)
            {
                var k2 = (k + 1) % kN;
                var a1 = Math.Atan2(bPts[k][1] - iy, bPts[k][0] - ix);
                var a2 = Math.Atan2(bPts[k2][1] - iy, bPts[k2][0] - ix);
                var corners = new[] { new[] { b.X1, b.Y1 }, new[] { b.X0, b.Y1 }, new[] { b.X0, b.Y0 }, new[] { b.X1, b.Y0 } }
                    .Where(cn =>
                    {
                        var da = Norm(Math.Atan2(cn[1] - iy, cn[0] - ix) - a1);
                        var ddd = Norm(a2 - a1);
                        return da > 0 && da < ddd;
                    })
                    .OrderBy(cn => Norm(Math.Atan2(cn[1] - iy, cn[0] - ix) - a1))
                    .ToList();
                if (corners.Count > 0)
                    shards[k * 2 + 1] = new[] { rPts[k], bPts[k] }.Concat(corners).Concat(new[] { bPts[k2], rPts[k2] }).ToArray();
            }
            var crack = J.Clamp(q / 0.2);
            var v = J.Clamp((q - 0.18) / 0.82);
            var a0 = NN(it, "alpha", 1);
            var bg = ScK(env, "bg") ?? "#ffffff";
            var acc = ScK(env, "accent") ?? "#ffffff";
            var lw = Math.Max(1.5, sz * 0.032);
            if (v <= 0)
            {
                // JS chainPost（裂纹）——就地执行
                if (env.Pass != "main") return;
                InItem(env, it, () =>
                {
                    for (var k = 0; k < kN; k++)
                        env.PolyPartial(new[] { new[] { ix, iy }, bPts[k] }, crack, bg, lw, 1, false);
                    if (crack > 0.5)
                        for (var k = 0; k < kN; k++)
                            env.PolyPartial(new[] { rPts[k], rPts[(k + 1) % kN] }, (crack - 0.5) * 2, bg, lw * 0.8, 1, false);
                    env.Circle(ix, iy, sz * (0.06 + 0.2 * crack), acc, null, 0, 0.5 * (1 - crack), false);
                });
                return;
            }
            it["alpha"] = 0.0;
            // JS chainPost（碎片，只画一次，碎裂时色差 ghost 不画）——就地执行
            if (env.Pass != "main") return;
            var sx2 = N1(it, "sx");
            var sy2 = N1(it, "sy");
            var vt = B(it, "vertical");
            var szIt = N0(it, "size");
            var itX = N0(it, "x");
            var itY = N0(it, "y");
            var itRot = N0(it, "rot");
            var bW = Math.Max(1, b.W);
            var bH = Math.Max(1, b.H);
            for (var j = 0; j < shards.Count; j++)
            {
                var poly = shards[j];
                double cx = 0, cy = 0;
                foreach (var pt in poly)
                {
                    cx += pt[0];
                    cy += pt[1];
                }
                cx /= poly.Length;
                cy /= poly.Length;
                var dsh = J.R(seed, j, 155) * 0.3 + (j % 2 == 1 ? 0 : 0.12);
                var u = J.Clamp((v - dsh) / (1 - dsh));
                var h = new HState { X = itX, Y = itY, Rot = itRot };
                if (u > 0)
                {
                    RotateAboutH(h, J.Rs(seed, j, 156) * 120 * u * u, cx, cy);
                    var ox = (cx - ix) / bW * sz * 1.2 * u;
                    var oy = (cy - iy) / bH * sz * 0.5 * u;
                    var od = Rot2(ox, oy, itRot * J.DEG);
                    h.X += od[0];
                    h.Y += od[1] + env.H * 1.1 * u * u;
                }
                double x0 = 1e9, x1 = -1e9, y0 = 1e9, y1 = -1e9;
                foreach (var pt in poly)
                {
                    x0 = Math.Min(x0, pt[0]);
                    x1 = Math.Max(x1, pt[0]);
                    y0 = Math.Min(y0, pt[1]);
                    y1 = Math.Max(y1, pt[1]);
                }
                var x0l = x0;
                var x1l = x1;
                var y0l = y0;
                var y1l = y1;
                var cfExtra = (JAnim.CharFn)((_, g, _) =>
                {
                    // 只留这块碎片能露出的字形
                    if (g == null) return new CharXf { Hide = true };
                    var gx = GCx(g, it);
                    var gy = GCy(g, it);
                    var hw = (vt ? szIt : D(g, "w")) * sx2 * 0.6;
                    var hh = (vt ? D(g, "h") : szIt) * sy2 * 0.6;
                    return gx + hw < x0l || gx - hw > x1l || gy + hh < y0l || gy - hh > y1l
                        ? new CharXf { Hide = true }
                        : null;
                });
                var cf = JAnim.CombineChar(env.CharFns.Concat(new[] { cfExtra }).ToList());
                using var path = PolyPath(poly, h.X, h.Y, h.Rot);
                DrawFxCopy(env, it, cf, path, c =>
                {
                    c["x"] = h.X;
                    c["y"] = h.Y;
                    c["rot"] = h.Rot;
                    c["alpha"] = a0 * (1 - J.Smooth(0.75, 1, u));
                });
            }
            if (v < 0.25)
                InItem(env, it, () =>
                {
                    for (var k = 0; k < kN; k++)
                        env.Line(new[] { new[] { ix, iy }, bPts[k] }, bg, lw * (1 - v * 4), 1, false);
                });
        },
    });

    // ---------------- zipOut（X.zipOut；JS ae:'wipe'） ----------------

    private static void ZipOut() => Reg1("zipOut", new MoveDef
    {
        Name = "ジッパー",
        Tags = new[] { "graphic", "pop" },
        W = 0.8,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var vert = B(it, "vertical");
            var sz = N0(it, "size");
            var acc = ScK(env, "accent") ?? "#ffffff";
            var sub = ScK(env, "sub") ?? "#ffffff";
            var rev = CutBit(env, 161);
            var single = IsSingle(env, it);
            var nCut = CutN(env);
            var pos = J.Lerp(-0.12, 1.12, J.E.InOutSine(J.Clamp(p / 0.8)));
            var lines = LineExt(it);
            var band = single ? 1.2 / Math.Max(1, nCut - 1) : 0.22;
            var og = OrderOf(env, it)(0, 1);
            var byLi = lines.ToDictionary(l => l.Li);
            double Along(LnExt l, JObject g)
            {
                var a0 = vert ? l.Y0 : l.X0;
                var a1 = vert ? l.Y1 : l.X1;
                var v2 = vert ? GCy(g, it) : GCx(g, it);
                var o = (v2 - a0) / Math.Max(1, a1 - a0);
                return rev ? 1 - o : o;
            }
            Glyphs(env, (_, g, _) =>
            {
                if (g == null || !byLi.TryGetValue((int)D(g, "li"), out var l)) return null;
                var o = single ? (rev ? 1 - og : og) : Along(l, g);
                var c = J.E.InOutSine(J.Clamp((pos - o) / band + 0.15));
                if (c <= 0) return null;
                if (c >= 0.99) return new CharXf { Hide = true };
                var k = 1 - c;
                var acrossX = vert != B(g, "r90");
                return acrossX ? new CharXf { Sx = k } : new CharXf { Sy = k };
            });
            var fa = NN(it, "alpha", 1) * (1 - J.Smooth(0.76, 0.96, p));
            // JS chainPost（链齿 + 滑块）——就地执行
            if (env.Pass != "main" || fa <= 0.01) return;
            InItem(env, it, () =>
            {
                foreach (var l in lines)
                {
                    var a0 = vert ? l.Y0 : l.X0;
                    var a1 = vert ? l.Y1 : l.X1;
                    var mid = vert ? (l.X0 + l.X1) / 2 : (l.Y0 + l.Y1) / 2;
                    var len = a1 - a0;
                    var sp = single ? a0 + len * ((pos - (rev ? 1 - og : og)) * Math.Max(1, nCut - 1) + 0.5) : a0 + len * pos;
                    if (rev) sp = a0 + a1 - sp;
                    var z0 = rev ? Math.Max(sp, a0 - sz * 0.1) : a0 - sz * 0.1;
                    var z1 = rev ? a1 + sz * 0.1 : Math.Min(sp, a1 + sz * 0.1);
                    var tp = Math.Max(3, sz * 0.09);
                    var th = sz * 0.05;
                    for (double a = z0, k = 0; a < z1; a += tp, k++)
                    {
                        // 链齿
                        var o = (k % 2 != 0 ? 1 : -1) * th * 0.5;
                        if (vert) env.Rect(mid - th * 0.5 + o, a, th, tp * 0.6, sub, fa, false);
                        else env.Rect(a, mid - th * 0.5 + o, tp * 0.6, th, sub, fa, false);
                    }
                    if (sp > a0 - sz * 0.5 && sp < a1 + sz * 0.5)
                    {
                        // 滑块和拉片
                        var w = sz * 0.3;
                        var h = sz * 0.46;
                        if (vert)
                        {
                            env.RRect(mid - h / 2, sp - w / 2, h, w, w * 0.3, acc, fa, false);
                            env.RRect(mid + h / 2 - sz * 0.02, sp - w * 0.2, sz * 0.36, w * 0.4, w * 0.2, acc, fa, false);
                        }
                        else
                        {
                            env.RRect(sp - w / 2, mid - h / 2, w, h, w * 0.3, acc, fa, false);
                            env.RRect(sp - w * 0.2, mid + h / 2 - sz * 0.02, w * 0.4, sz * 0.36, w * 0.2, acc, fa, false);
                        }
                    }
                }
            });
        },
    });

    // ---------------- clapShut（X.clapShut；JS ae:'wipe'） ----------------

    private static void ClapShut() => Reg1("clapShut", new MoveDef
    {
        Name = "中央で閉じる",
        Tags = new[] { "graphic", "pop" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var vert = B(it, "vertical");
            var sz = N0(it, "size");
            var acc = ScK(env, "accent") ?? "#ffffff";
            var e = J.E.InCubic(J.Clamp(p / 0.62));
            var hit = J.Clamp((p - 0.6) / 0.4);
            if (IsSingle(env, it))
            {
                var left = N0(it, "x") < env.W / 2.0;
                var dd = env.W * 0.47 * e;
                it["x"] = N0(it, "x") + (left ? dd : -dd);
                // JS clipFn 是设计空间轴对齐半屏矩形 → it.clip 精确对位（DrawFx 原生消费）
                it["clip"] = left
                    ? new JArray(-env.W * 1.0, -env.W * 1.0 + env.W * 1.5)
                    : new JArray(env.W / 2.0, env.W / 2.0 + env.W * 1.5);
                if (e >= 0.999) it["alpha"] = 0.0;
                if (hit > 0 && hit < 1 && Math.Round(Num(it, "mi")) == 0)
                {
                    var h = env.H * 0.3 * (1 + 0.6 * J.E.OutCubic(hit));
                    var lw = sz * 0.12 * (1 - hit);
                    // JS chainPost——就地执行
                    env.Rect(env.W / 2.0 - lw / 2, env.H / 2.0 - h / 2, lw, h, acc, 1 - hit, false);
                }
                return;
            }
            var b = LBox(it, sz * 0.04);
            var seam = vert ? b.Cy : b.Cx;
            var half = (vert ? b.H : b.W) / 2 + sz * 0.05;
            var dd2 = half * e;
            JAnim.CharFn Side(int sgn) => (_, g, _) =>
            {
                if (g == null) return new CharXf { Hide = true };
                var gx = GCx(g, it);
                var gy = GCy(g, it);
                var c2 = vert ? gy : gx;
                var hw = (vert ? D(g, "h") * N1(it, "sy") : D(g, "w") * N1(it, "sx")) / 2;
                if (sgn < 0 ? c2 - hw >= seam : c2 + hw <= seam) return new CharXf { Hide = true };
                var k = 1 - 0.12 * e;
                var nx = vert ? gx : gx - sgn * dd2;
                var ny = vert ? gy - sgn * dd2 : gy;
                var o = vert
                    ? (B(g, "r90") ? new CharXf { Sx = k } : new CharXf { Sy = k })
                    : (B(g, "r90") ? new CharXf { Sy = k } : new CharXf { Sx = k });
                o.Dx = nx - gx;
                o.Dy = ny - gy;
                var gc = vert
                    ? Gclip(g, it, nx, ny, 1, k, null, sgn < 0 ? new[] { -1e5, seam } : new[] { seam, 1e5 })
                    : Gclip(g, it, nx, ny, k, 1, sgn < 0 ? new[] { -1e5, seam } : new[] { seam, 1e5 }, null);
                o.ClipX = gc.CX;
                o.ClipY = gc.CY;
                return o;
            };
            WithCopies(env, it, Side(-1), new[] { Side(1) });
            var sa = J.Smooth(0, 0.15, p) * (1 - J.Smooth(0.6, 0.7, p));
            if (sa > 0.01 && env.Pass == "main")
            {
                var lw2 = Math.Max(1.5, sz * 0.025);
                var ext = (vert ? b.W : b.H) * 1.1 * J.E.OutCubic(J.Smooth(0, 0.15, p));
                var mc = vert ? b.Cx : b.Cy;
                // JS chainPost（接缝线）——就地执行
                InItem(env, it, () =>
                {
                    if (vert) env.Rect(mc - ext / 2, seam - lw2 / 2, ext, lw2, acc, sa, false);
                    else env.Rect(seam - lw2 / 2, mc - ext / 2, lw2, ext, acc, sa, false);
                });
            }
            if (hit > 0 && hit < 1 && env.Pass == "main")
            {
                var lw3 = sz * 0.14 * (1 - J.E.OutCubic(hit));
                var ext2 = (vert ? b.W : b.H) * (1 + 0.7 * J.E.OutCubic(hit));
                var fa = 1 - hit;
                // JS chainPost（撞击闪光 + 火花）——就地执行
                InItem(env, it, () =>
                {
                    var mc = vert ? b.Cx : b.Cy;
                    if (vert) env.Rect(mc - ext2 / 2, seam - lw3 / 2, ext2, lw3, acc, fa, false);
                    else env.Rect(seam - lw3 / 2, mc - ext2 / 2, lw3, ext2, acc, fa, false);
                    for (var k = 0; k < 4; k++)
                    {
                        // 撞击火花
                        var a = (k + 0.5) / 4 * J.TAU + 0.3;
                        var r0 = sz * (0.2 + 0.6 * hit);
                        var r1 = r0 + sz * 0.25 * (1 - hit);
                        var cx = vert ? mc : seam;
                        var cy = vert ? seam : mc;
                        env.Line(new[]
                        {
                            new[] { cx + Math.Cos(a) * r0, cy + Math.Sin(a) * r0 },
                            new[] { cx + Math.Cos(a) * r1, cy + Math.Sin(a) * r1 },
                        }, acc, Math.Max(1, sz * 0.03), fa, false);
                    }
                });
            }
        },
    });

    // ---------------- lampOff（X.lampOff；JS ae:'glitch'） ----------------

    private static void LampOff() => Reg1("lampOff", new MoveDef
    {
        Name = "消灯",
        Tags = new[] { "glitch", "emotional" },
        W = 0.8,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var seed = SeedOf(it);
            var step = (int)env.Step;
            var c0 = ColOf(it);
            var bg = ScK(env, "bg") ?? "#ffffff";
            var dark = DarkBg(env);
            var sz = N0(it, "size");
            var dimC = MixC(c0, bg, 0.78);
            var hot = dark ? MixC(c0, "#ffffff", 0.5) : c0;
            var nN = LayOf(it).N;
            double T(int i) => J.R(seed, i, 171) * 0.46;
            var lit = 0.0;
            for (var i = 0; i < nN; i++)
            {
                var q = (p - T(i)) / 0.36;
                lit += q <= 0 ? 1 : q < 0.5 ? 0.5 : 0;
            }
            Glyphs(env, (i, _, _) =>
            {
                var q = (p - T(i)) / 0.36;
                if (q <= 0) return null;
                if (q >= 1) return new CharXf { Hide = true };
                if (q > 0.55) return new CharXf { Color = dimC, A = 1 - (q - 0.55) / 0.45 };
                var off = J.R(seed, i, step, 172) < 0.3 + 0.7 * (q / 0.55);
                return new CharXf { Color = off ? dimC : hot };
            });
            if (dark && env.Pass == "main" && it["shadow"] == null)
                it["shadow"] = new JObject
                {
                    ["color"] = Rgba(MixC(c0, ScK(env, "accent") ?? "#ffffff", 0.4), 0.85),
                    ["blur"] = sz * 0.22 * lit / Math.Max(1, nN),
                    ["dx"] = 0,
                    ["dy"] = 0,
                };
        },
    });

    // ---------------- slotOut（X.slotOut；JS ae:'glitch'） ----------------

    private static void SlotOut() => Reg1("slotOut", new MoveDef
    {
        Name = "スロット回転",
        Tags = new[] { "glitch", "pop" },
        W = 0.8,
        OutDur = (dur, _) => J.Clamp(dur * 0.38, 0.3, 0.7),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var seed = SeedOf(it);
            var ord = OrderOf(env, it);
            var sy0 = N1(it, "sy");
            var acc = ScK(env, "accent") ?? "#ffffff";
            var pool = JLang.Pool("reel") ?? "";
            if (pool.Length == 0) pool = "0";
            int M(int i) => 3 + (int)(J.H(seed, i, 182) % 4);
            string ChOf(int i, int m) => pool[(int)(J.H(seed, i, m, 181) % (uint)pool.Length)].ToString();
            (double Q, double Ph, int Mm, double Fr, double Sp)? State(int i, int n)
            {
                var q = Win(p, ord(i, n), 0.35);
                if (q <= 0) return null;
                var ph = (M(i) + 1) * J.E.InOutCubic(q);
                var m = (int)Math.Floor(ph);
                return (q, ph, m, ph - m, Math.Min(0.6, 6 * Math.Sin(Math.PI * q) * 0.12));
            }
            JAnim.CharFn Reel(bool next) => (i, g, n) =>
            {
                if (g == null) return new CharXf { Hide = true };
                var s = State(i, n);
                if (s == null) return next ? new CharXf { Hide = true } : null;
                var m = s.Value.Mm + (next ? 1 : 0);
                if (m > M(i)) return new CharXf { Hide = true };
                var pitch = D(g, "h") * sy0 * 1.12;
                var dy = next ? (1 - s.Value.Fr) * pitch : -s.Value.Fr * pitch;
                var gx = GCx(g, it);
                var gy = GCy(g, it);
                var hh = D(g, "h") * sy0 * 0.55;
                var ky = 1 + s.Value.Sp;
                var gc = Gclip(g, it, gx, gy + dy, 1, ky, null, new[] { gy - hh, gy + hh });
                var o = new CharXf { Dy = dy, ClipX = gc.CX, ClipY = gc.CY };
                if (B(g, "r90")) o.Sx = ky;
                else o.Sy = ky;
                if (m > 0)
                {
                    o.Ch = ChOf(i, m);
                    if (J.R(seed, i, m, 183) < 0.3) o.Color = acc;
                }
                return o;
            };
            WithCopies(env, it, Reel(false), new[] { Reel(true) });
        },
    });

    // ---------------- clockOut（X.clockOut；JS ae:'wipe'） ----------------

    private sealed class CCell
    {
        public int I;
        public double Cx, Cy, Hw, Hh, E;
    }

    private static void ClockOut() => Reg1("clockOut", new MoveDef
    {
        Name = "時計ワイプ",
        Tags = new[] { "graphic", "editorial" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var sz = N0(it, "size");
            var acc = ScK(env, "accent") ?? "#ffffff";
            var cw = !CutBit(env, 191);
            var sg = cw ? 1 : -1;
            const double aStart = -Math.PI / 2;
            var ord = OrderOf(env, it);
            var lay = LayOf(it);
            var vert = B(it, "vertical");
            var sx0 = N1(it, "sx");
            var sy0 = N1(it, "sy");
            var cells = new List<CCell>();
            foreach (var g in lay.Cells)
            {
                if (IsSp(g.Ch)) continue;
                var q = Win(p, ord(g.I, lay.N), 0.55);
                if (q >= 1) continue; // 扫完的字形不在保留并集里 → 被裁掉
                cells.Add(new CCell
                {
                    I = g.I,
                    Cx = GCx(g, it),
                    Cy = GCy(g, it),
                    Hw = (vert ? sz * sx0 : g.W * sx0) * 0.56,
                    Hh = (vert ? g.H * sy0 : sz * sy0) * 0.56,
                    E = J.E.InOutSine(q),
                });
            }
            if (cells.Count == 0)
            {
                it["alpha"] = 0.0;
                return;
            }
            double[] P2(CCell c, double a)
            {
                var ca = Math.Cos(a);
                var sa = Math.Sin(a);
                var t = Math.Min(Math.Abs(ca) > 1e-6 ? c.Hw / Math.Abs(ca) : 1e9, Math.Abs(sa) > 1e-6 ? c.Hh / Math.Abs(sa) : 1e9);
                return new[] { c.Cx + ca * t, c.Cy + sa * t };
            }
            // 中心 → 表针 → 按扫过顺序的角点 → 12 点方向
            double[][] Region(CCell c)
            {
                var ah = aStart + sg * c.E * J.TAU;
                var span = (1 - c.E) * J.TAU;
                var pts = new List<double[]> { new[] { c.Cx, c.Cy }, P2(c, ah) };
                double Norm(double a2) => ((a2 % J.TAU) + J.TAU) % J.TAU;
                var cor = new[] { new[] { c.Hw, -c.Hh }, new[] { c.Hw, c.Hh }, new[] { -c.Hw, c.Hh }, new[] { -c.Hw, -c.Hh } }
                    .Select(v2 => new[] { Norm(Math.Atan2(v2[1], v2[0]) - ah), c.Cx + v2[0], c.Cy + v2[1] })
                    .Where(k => k[0] > 1e-6 && k[0] < span)
                    .OrderBy(k => k[0])
                    .ToList();
                foreach (var k in cor) pts.Add(new[] { k[1], k[2] });
                pts.Add(new[] { c.Cx, c.Cy - c.Hh });
                return pts.ToArray();
            }
            // JS clipFn：每字形扇形并集（polyL 按 drawFx 时点 item 位姿换算；e<=0.001 用整块矩形）→ Env.ClipFn 桥
            env.ClipFn = (_, it3) =>
            {
                var path = new SKPath();
                foreach (var c in cells)
                {
                    var pts = c.E <= 0.001
                        ? new[] { new[] { c.Cx - c.Hw, c.Cy - c.Hh }, new[] { c.Cx + c.Hw, c.Cy - c.Hh }, new[] { c.Cx + c.Hw, c.Cy + c.Hh }, new[] { c.Cx - c.Hw, c.Cy + c.Hh } }
                        : Region(c);
                    var r = N0(it3, "rot") * J.DEG;
                    var cr = Math.Cos(r);
                    var sr = Math.Sin(r);
                    var bx = N0(it3, "x");
                    var by = N0(it3, "y");
                    for (var i = 0; i < pts.Length; i++)
                    {
                        var px = bx + cr * pts[i][0] - sr * pts[i][1];
                        var py = by + sr * pts[i][0] + cr * pts[i][1];
                        if (i == 0) path.MoveTo((float)px, (float)py);
                        else path.LineTo((float)px, (float)py);
                    }
                    path.Close();
                }
                return path;
            };
            var fa = NN(it, "alpha", 1);
            // JS chainPost（表针 + 辉光）——就地执行
            if (env.Pass != "main") return;
            InItem(env, it, () =>
            {
                foreach (var c in cells)
                {
                    if (c.E <= 0.001) continue;
                    var ah = aStart + sg * c.E * J.TAU;
                    var glow = new List<double[]> { new[] { c.Cx, c.Cy } };
                    var a = fa * Math.Min(1, (1 - c.E) * 6);
                    for (var k = 0; k <= 5; k++) glow.Add(P2(c, ah - sg * Math.Min(c.E * J.TAU, 0.6) * k / 5.0));
                    env.Poly(glow.ToArray(), acc, 0.22 * a, false);
                    env.Line(new[] { new[] { c.Cx, c.Cy }, P2(c, ah) }, acc, Math.Max(1.5, sz * 0.03), a, false);
                    env.Circle(c.Cx, c.Cy, Math.Max(1.5, sz * 0.04), acc, null, 0, a, false);
                }
            });
        },
    });

    // ---------------- matrixOut（X.matrixOut；JS ae:'glitch'） ----------------

    private static void MatrixOut() => Reg1("matrixOut", new MoveDef
    {
        Name = "デジタル雨",
        Tags = new[] { "glitch" },
        W = 0.8,
        OutDur = (dur, _) => J.Clamp(dur * 0.42, 0.34, 0.85),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var seed = SeedOf(it);
            var step = (int)env.Step;
            var sz = N0(it, "size");
            var sy0 = N1(it, "sy");
            var acc = ScK(env, "accent") ?? "#ffffff";
            var c0 = ColOf(it);
            var bb = DBox(it, 0);
            var dd = env.H - bb.Y0 + sz * 1.2;
            var head = DarkBg(env) ? MixC(c0, "#ffffff", 0.4) : c0;
            double Q(int i) => Win(p, J.R(seed, i, 201) * 0.9, 0.5);
            var poolHalf = JLang.Pool("half") ?? "01";
            if (poolHalf.Length == 0) poolHalf = "0";
            string Rch(int i, int k) => poolHalf[(int)(J.H(seed, i, k, (int)Math.Floor(step / 2.0), 202) % (uint)poolHalf.Length)].ToString();
            (double Q, double U, double L, double Pitch)? Fall(int i, JObject g)
            {
                var q = Q(i);
                if (q <= 0) return null;
                var u = J.Clamp((q - 0.22) / 0.78);
                return (q, u, dd * Math.Pow(u, 1.7), D(g, "h") * sy0 * 0.92);
            }
            CharXf? HeadFn(int i, JObject? g, int _)
            {
                if (g == null) return null;
                var f = Fall(i, g);
                if (f == null) return null;
                if (f.Value.Q >= 1) return new CharXf { Hide = true };
                var dxy = DirI(it, 0, f.Value.L);
                if (f.Value.Q < 0.22)
                    return J.R(seed, i, step, 203) < f.Value.Q / 0.22 + 0.2
                        ? new CharXf { Ch = Rch(i, 0), Color = acc }
                        : null;
                return new CharXf { Dx = dxy[0], Dy = dxy[1], Ch = Rch(i, 0), Color = head };
            }
            JAnim.CharFn Trail(int k) => (i, g, _) =>
            {
                if (g == null) return new CharXf { Hide = true };
                var f = Fall(i, g);
                if (f == null || f.Value.Q < 0.22 || f.Value.Q >= 1 || f.Value.L < k * f.Value.Pitch * 0.6)
                    return new CharXf { Hide = true };
                var dxy = DirI(it, 0, f.Value.L - k * f.Value.Pitch);
                return new CharXf { Dx = dxy[0], Dy = dxy[1], Ch = Rch(i, k), Color = acc, A = (1 - k / 6.0) * (1 - 0.5 * f.Value.U) };
            };
            WithCopies(env, it, HeadFn, new[] { Trail(1), Trail(2), Trail(3), Trail(4), Trail(5) });
        },
    });

    // ---------------- tornadoOut（X.tornadoOut；JS ae:'scatter'） ----------------

    private static void TornadoOut() => Reg1("tornadoOut", new MoveDef
    {
        Name = "竜巻",
        Tags = new[] { "pop" },
        W = 0.8,
        MinDur = 0.8,
        OutDur = (dur, _) => J.Clamp(dur * 0.45, 0.38, 0.9),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var seed = SeedOf(it);
            var sz = N0(it, "size");
            var dir = CutBit(env, 211) ? 1 : -1;
            var single = IsSingle(env, it);
            var bb = DBox(it, 0);
            var up = bb.Y1 + sz * 1.8;
            var lay = LayOf(it);
            var cx = bb.Cx;
            var r0 = 1.0;
            if (single)
            {
                cx = env.W / 2.0;
                r0 = env.W * 0.45;
            }
            else
                foreach (var g in lay.Cells)
                    r0 = Math.Max(r0, Math.Abs(ToD(it, GCx(g, it), GCy(g, it))[0] - cx));
            r0 += sz * 0.3;
            // X0 是字形设计坐标 x，hx 是螺旋进度 0..1
            (double Dx, double Dy, double Z, double Tilt) Orbit(double x0, int i, double q, double hx)
            {
                var ox = x0 - cx;
                var th0 = Math.Asin(J.Clamp(ox / r0, -1, 1));
                var r2 = J.R(seed, i, 212);
                var th = th0 + dir * Math.PI * (3.2 + r2) * q * q;
                var lift = up * Math.Pow(q, 2.2) * (0.8 + 0.3 * r2) + sz * 1.4 * hx * J.E.OutCubic(q);
                var rr = r0 * (1 - 0.35 * J.E.OutCubic(q)) + lift * 0.25 + sz * 0.6 * q;
                return (rr * Math.Sin(th) - ox, -lift, Math.Cos(th), -Math.Sin(th) * 14 * q);
            }
            var alpha = 1 - J.Smooth(0.8, 0.98, p);
            if (single)
            {
                var q = Win(p, J.R(unchecked((int)(env.Cut?.Seed ?? 0)), (int)Num(it, "mi"), 213) * 0.5, 0.25);
                if (q <= 0) return;
                var o = Orbit(N0(it, "x"), (int)Num(it, "mi"), q, OrderOf(env, it)(0, 1));
                it["x"] = N0(it, "x") + o.Dx;
                it["y"] = N0(it, "y") + o.Dy;
                it["rot"] = N0(it, "rot") + o.Tilt;
                it["size"] = N0(it, "size") * (0.72 + 0.28 * o.Z);
                it["alpha"] = NN(it, "alpha", 1) * (0.3 + 0.7 * (o.Z + 1) / 2) * alpha;
                return;
            }
            Glyphs(env, (i, g, _) =>
            {
                if (g == null) return null;
                var q = Win(p, J.R(seed, i, 214) * 0.5, 0.25);
                if (q <= 0) return null;
                var x0 = ToD(it, GCx(g, it), GCy(g, it))[0];
                var o = Orbit(x0, i, q, lay.N > 1 ? i / (double)(lay.N - 1) : 0);
                var dxy = DirI(it, o.Dx, o.Dy);
                return new CharXf { Dx = dxy[0], Dy = dxy[1], Rot = o.Tilt, S = 0.72 + 0.28 * o.Z, A = (0.3 + 0.7 * (o.Z + 1) / 2) * alpha };
            });
        },
    });

    // ---------------- rollUpOut（X.rollUpOut；JS ae:'wipe'） ----------------

    private static void RollUpOut() => Reg1("rollUpOut", new MoveDef
    {
        Name = "巻き取る",
        Tags = new[] { "graphic", "editorial" },
        W = 0.8,
        OutDur = (dur, _) => J.Clamp(dur * 0.4, 0.32, 0.8),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var sz = N0(it, "size");
            var vert = B(it, "vertical");
            var rev = CutBit(env, 221);
            var single = IsSingle(env, it);
            var c0 = ColOf(it);
            var bg = ScK(env, "bg") ?? "#ffffff";
            var b = LBox(it, sz * 0.08);
            var sx0 = N1(it, "sx");
            var sy0 = N1(it, "sy");
            var a0 = vert ? b.Y0 : b.X0;
            var a1 = vert ? b.Y1 : b.X1;
            var c0x = vert ? b.X0 : b.Y0;
            var c1x = vert ? b.X1 : b.Y1;
            var r0 = sz * 0.26;
            var e = J.E.InOutSine(J.Clamp(p / 0.86));
            double xr;
            if (single)
            {
                // 单字 item：纸卷横穿整个屏幕
                var x0 = env.W * 0.03 - N0(it, "x");
                var x1 = env.W * 0.97 - N0(it, "x");
                xr = rev ? J.Lerp(x1, x0, e) : J.Lerp(x0, x1, e);
            }
            else xr = rev ? J.Lerp(a1 + r0, a0 - r0 * 3, e) : J.Lerp(a0 - r0, a1 + r0 * 3, e);
            var rolled = rev
                ? Math.Max(0, (single ? env.W * 0.97 - N0(it, "x") : a1) - xr)
                : Math.Max(0, xr - (single ? env.W * 0.03 - N0(it, "x") : a0));
            var r = r0 * Math.Sqrt(1 + rolled / (sz * 2));
            var sg = rev ? -1 : 1; // 纸卷沿轴的 +sg 方向走
            var edge = xr + sg * r; // 平纸从这条边开始
            Glyphs(env, (_, g, _) =>
            {
                if (g == null) return null;
                var c2 = vert ? GCy(g, it) : GCx(g, it);
                var hw = (vert ? D(g, "h") * sy0 : D(g, "w") * sx0) / 2;
                var d = (c2 - edge) * sg; // 到纸卷前方的距离
                if (d + hw <= 0) return new CharXf { Hide = true };
                var near = J.Clamp(1 - (d - hw) / (r * 2.6));
                if (near <= 0) return null;
                var k = 1 - 0.55 * Math.Pow(near, 1.6);
                var nc = c2 - sg * (1 - k) * hw; // 纸往卷上缠时会聚拢起皱
                var alongLocalX = vert ? B(g, "r90") : !B(g, "r90");
                var kx = alongLocalX ? k : 1;
                var ky = alongLocalX ? 1 : k;
                var o = new CharXf { Color = MixC(c0, bg, 0.45 * near), Sx = kx, Sy = ky };
                if (vert) o.Dy = nc - c2;
                else o.Dx = nc - c2;
                if ((nc - edge) * sg - k * hw < 0)
                {
                    var gc = vert
                        ? Gclip(g, it, GCx(g, it), nc, kx, ky, null, sg > 0 ? new[] { edge, 1e5 } : new[] { -1e5, edge })
                        : Gclip(g, it, nc, GCy(g, it), kx, ky, sg > 0 ? new[] { edge, 1e5 } : new[] { -1e5, edge }, null);
                    o.ClipX = gc.CX;
                    o.ClipY = gc.CY;
                }
                return o;
            });
            var fa = NN(it, "alpha", 1) * (1 - J.Smooth(0.82, 0.98, p));
            var back = MixC(c0, bg, 0.55);
            var hi = MixC(c0, bg, 0.25);
            var lo = MixC(c0, bg, 0.8);
            if (fa <= 0.01) return;
            // JS chainPost（纸卷）——就地执行
            InItem(env, it, () =>
            {
                var lift = J.E.InCubic(J.Clamp((p - 0.8) / 0.18));
                var cm = (c0x + c1x) / 2; // 最后纸卷被拿走，收缩到中点
                var ext0 = J.Lerp(c0x - sz * 0.06, cm, lift);
                var ext1 = J.Lerp(c1x + sz * 0.06, cm, lift);
                var x0 = xr - r * (1 - lift);
                var w = 2 * r * (1 - lift);
                if (w < 0.3 || ext1 - ext0 < 0.3) return;
                void R(double s0, double s1, string col, double a2)
                {
                    if (vert) env.Rect(ext0, x0 + s0 * w, ext1 - ext0, (s1 - s0) * w, col, a2, false);
                    else env.Rect(x0 + s0 * w, ext0, (s1 - s0) * w, ext1 - ext0, col, a2, false);
                }
                R(0, 1, back, fa);
                R(0.18, 0.38, hi, fa);
                R(0.72, 1, lo, fa);
            });
        },
    });

    // ---------------- snakeOut（X.snakeOut；JS ae:'stretch'） ----------------

    private static void SnakeOut() => Reg1("snakeOut", new MoveDef
    {
        Name = "列になって去る",
        Tags = new[] { "calm", "pop" },
        W = 0.9,
        MinDur = 0.8,
        OutDur = (dur, _) => J.Clamp(dur * 0.45, 0.38, 0.9),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var sz = N0(it, "size");
            var rev = CutBit(env, 231);
            var nd = CutBit(env, 232) ? 1 : -1;
            // 行进坐标 a2、横向偏移 o，返回路径点 (a, o, rotDeg)
            (double A, double O, double Rd) D2(double a2, double o, double aEnd, double rr)
            {
                if (a2 <= aEnd) return (a2, o, 0);
                var s = a2 - aEnd;
                var arc = rr * Math.PI / 2;
                if (s <= arc)
                {
                    var f = s / rr;
                    var ca = Math.Cos(f);
                    var sa = Math.Sin(f);
                    return (aEnd + rr * sa - nd * sa * o, nd * rr * (1 - ca) + ca * o, nd * f / J.DEG);
                }
                return (aEnd + rr - nd * o, nd * (rr + s - arc), nd * 90);
            }
            double Ease(double x) => 0.35 * x * x + 0.65 * Math.Pow(x, 2.4);
            if (IsSingle(env, it))
            {
                var ux = rev ? -1 : 1;
                var aEnd = rev ? -env.W * 0.1 : env.W * 0.9;
                var rr = Math.Min(env.W, env.H) * 0.2;
                var a = N0(it, "x") * ux;
                var o = (N0(it, "y") - env.H / 2.0) * ux; // v = rot90(u) = (0, ux)
                var dt = env.W * 1.2 + rr * 2 + env.H * 0.8;
                var res = D2(a + dt * Ease(p), o, aEnd, rr);
                it["x"] = res.A * ux;
                it["y"] = env.H / 2.0 + res.O * ux;
                it["rot"] = N0(it, "rot") + res.Rd;
                return;
            }
            var vert = B(it, "vertical");
            var b = LBox(it);
            var u = vert ? new[] { 0.0, 1.0 } : new[] { 1.0, 0.0 };
            if (rev) u = new[] { -u[0], -u[1] };
            var v2 = new[] { -u[1], u[0] };
            var lay = LayOf(it);
            double aMin = 1e9, aMax = -1e9;
            foreach (var g in lay.Cells)
            {
                var a = GCx(g, it) * u[0] + GCy(g, it) * u[1];
                aMin = Math.Min(aMin, a);
                aMax = Math.Max(aMax, a);
            }
            var aEnd2 = aMax + sz * 0.5;
            var rr2 = sz * 1.3;
            var oc = b.Cx * v2[0] + b.Cy * v2[1];
            var dt2 = (aEnd2 - aMin) + rr2 * 2 + Math.Max(env.W, env.H) * 0.75;
            var trav = dt2 * Ease(p);
            Glyphs(env, (_, g, _) =>
            {
                if (g == null) return null;
                var gx = GCx(g, it);
                var gy = GCy(g, it);
                var a = gx * u[0] + gy * u[1];
                var o = gx * v2[0] + gy * v2[1] - oc;
                var res = D2(a + trav, o, aEnd2, rr2);
                var x2 = u[0] * res.A + v2[0] * (res.O + oc);
                var y2 = u[1] * res.A + v2[1] * (res.O + oc);
                return new CharXf { Dx = x2 - gx, Dy = y2 - gy, Rot = res.Rd };
            });
            it["alpha"] = NN(it, "alpha", 1) * (1 - J.Smooth(0.9, 1, p));
        },
    });

    // ---------------- flutterOut（X.flutterOut；JS ae:'fall'） ----------------

    private static void FlutterOut() => Reg1("flutterOut", new MoveDef
    {
        Name = "ひらひら落ちる",
        Tags = new[] { "emotional", "calm" },
        W = 1,
        MinDur = 0.8,
        OutDur = (dur, _) => J.Clamp(dur * 0.48, 0.42, 1.0),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var seed = SeedOf(it);
            var sz = N0(it, "size");
            var bb = DBox(it, 0);
            var dd = env.H - bb.Y0 + sz * 1.2;
            var c0 = ColOf(it);
            var back = MixC(c0, ScK(env, "bg") ?? "#ffffff", 0.45);
            Glyphs(env, (i, _, _) =>
            {
                var q = Win(p, J.R(seed, i, 241), 0.5);
                if (q <= 0) return null;
                if (q >= 1) return new CharXf { Hide = true };
                var ph = J.R(seed, i, 242) * J.TAU;
                var w = 7 + 4 * J.R(seed, i, 243);
                var amp = Math.Min(1, q * 4);
                var sway = Math.Sin(q * w + ph) * sz * 0.55 * amp;
                var l = dd * (0.25 * q + 0.75 * Math.Pow(q, 1.6));
                var flip = Math.Cos(q * (9 + 5 * J.R(seed, i, 244)) + ph * 0.5);
                var dxy = DirI(it, sway, l);
                var shiver = q < 0.08 ? Math.Sin(q * 300) * 6 * (1 - q / 0.08) : 0;
                return new CharXf
                {
                    Dx = dxy[0],
                    Dy = dxy[1],
                    Rot = Math.Cos(q * w + ph) * 28 * amp + shiver,
                    Sx = J.Lerp(1, flip, amp),
                    Color = flip < 0 && amp > 0.5 ? back : null,
                };
            });
        },
    });

    // ---------------- rollOff（X.rollOff；JS ae:'scatter'） ----------------

    private static void RollOff() => Reg1("rollOff", new MoveDef
    {
        Name = "転がって去る",
        Tags = new[] { "pop" },
        W = 0.8,
        MinDur = 0.8,
        OutDur = (dur, _) => J.Clamp(dur * 0.45, 0.38, 0.9),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var dir = CutBit(env, 251) ? 1 : -1;
            var sz = N0(it, "size");
            var ord = OrderOf(env, it);
            var sx0 = N1(it, "sx");
            var sy0 = N1(it, "sy");
            var ri = RightI(it);
            var dn = DownI(it);
            Glyphs(env, (i, g, n) =>
            {
                if (g == null) return null;
                var o = dir > 0 ? 1 - ord(i, n) : ord(i, n);
                var q = Win(p, o, 0.45);
                if (q <= 0) return null;
                if (q >= 1) return new CharXf { Hide = true };
                var gxD = ToD(it, GCx(g, it), GCy(g, it))[0];
                var dist = dir > 0 ? env.W - gxD + sz * 1.3 : gxD + sz * 1.3;
                var r = Math.Max(D(g, "w") * sx0, sz * sy0 * 0.8) / 2;
                var along = dist * Math.Pow(q, 1.7);
                var th = along / r; // 按无滑动滚动算
                var ph = ((th % (Math.PI / 2)) + Math.PI / 2) % (Math.PI / 2);
                var rise = r * (Math.Sqrt(2) * Math.Cos(ph - Math.PI / 4) - 1); // 中心越过每个角时抬高
                return new CharXf
                {
                    Dx = ri[0] * dir * along - dn[0] * rise,
                    Dy = ri[1] * dir * along - dn[1] * rise,
                    Rot = dir * th / J.DEG,
                };
            });
        },
    });

    // ---------------- fanClose（X.fanClose；JS ae:'shrink'） ----------------

    private static void FanClose() => Reg1("fanClose", new MoveDef
    {
        Name = "扇を閉じる",
        Tags = new[] { "graphic", "editorial", "emotional" },
        W = 0.9,
        MinDur = 0.8,
        OutDur = (dur, _) => J.Clamp(dur * 0.44, 0.36, 0.85),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var sz = N0(it, "size");
            var rev = CutBit(env, 261);
            var bend = J.E.InOutSine(J.Clamp(p / 0.3));
            var close = J.E.InOutCubic(J.Clamp((p - 0.24) / 0.5));
            var fin = J.Clamp((p - 0.72) / 0.28);
            var fs = 1 - 0.75 * J.E.InQuad(fin);
            var alpha = 1 - J.Smooth(0.8, 0.99, p);
            var spin = (rev ? 1 : -1) * 0.5 * J.E.InQuad(fin);
            // a 是沿行方向到中心的距离，w 是朝轴心的偏移，轴心在该侧 R 远处
            (double Na, double Nw, double Rd) Place(double a, double w, double rr, double aLim)
            {
                var alT = J.Lerp(a / rr, aLim, close) + spin;
                var rrr = rr - w;
                return (J.Lerp(a, Math.Sin(alT) * rrr, bend), J.Lerp(w, rr - Math.Cos(alT) * rrr, bend), alT * bend / J.DEG);
            }
            if (IsSingle(env, it))
            {
                var rr = env.W * 0.55;
                var aLim = (rev ? 1 : -1) * env.W * 0.45 / rr;
                var res = Place(N0(it, "x") - env.W / 2.0, N0(it, "y") - env.H / 2.0, rr, aLim);
                var nCut = CutN(env);
                var lead = Math.Round(Num(it, "mi")) == (rev ? nCut - 1 : 0);
                it["x"] = env.W / 2.0 + res.Na;
                it["y"] = env.H / 2.0 + res.Nw;
                it["rot"] = N0(it, "rot") + res.Rd;
                it["size"] = N0(it, "size") * fs;
                it["alpha"] = NN(it, "alpha", 1) * alpha * (lead ? 1 : 1 - J.Smooth(0.55, 0.95, close));
                return;
            }
            var vert = B(it, "vertical");
            var b = LBox(it);
            var lay = LayOf(it);
            var u = vert ? new[] { 0.0, 1.0 } : new[] { 1.0, 0.0 };
            var v2 = new[] { -u[1], u[0] }; // 轴心在 +v 一侧：横排在下方，竖排在左边
            var ac = b.Cx * u[0] + b.Cy * u[1];
            var oc = b.Cx * v2[0] + b.Cy * v2[1];
            double aMin = 1e9, aMax = -1e9;
            foreach (var g in lay.Cells)
            {
                var a = GCx(g, it) * u[0] + GCy(g, it) * u[1] - ac;
                aMin = Math.Min(aMin, a);
                aMax = Math.Max(aMax, a);
            }
            var half = Math.Max(sz, (aMax - aMin) / 2);
            var rr2 = Math.Max(sz * 2, half * 1.15);
            var aLim2 = (rev ? aMax : aMin) / rr2;
            Glyphs(env, (_, g, _) =>
            {
                if (g == null) return null;
                var gx = GCx(g, it);
                var gy = GCy(g, it);
                var a = gx * u[0] + gy * u[1] - ac;
                var res = Place(a, gx * v2[0] + gy * v2[1] - oc, rr2, aLim2);
                var lead = Math.Abs(a / rr2 - aLim2) < 1e-3;
                return new CharXf
                {
                    Dx = u[0] * (res.Na + ac) + v2[0] * (res.Nw + oc) - gx,
                    Dy = u[1] * (res.Na + ac) + v2[1] * (res.Nw + oc) - gy,
                    Rot = res.Rd,
                    S = fs,
                    A = alpha * (lead ? 1 : 1 - J.Smooth(0.55, 0.95, close)),
                };
            });
        },
    });

    // ---------------- rgbSplitOut（X.rgbSplitOut；JS ae:'glitch'） ----------------

    private static void RgbSplitOut() => Reg1("rgbSplitOut", new MoveDef
    {
        Name = "色分解",
        Tags = new[] { "glitch", "pop" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var sz = N0(it, "size");
            var seed = SeedOf(it);
            var step = (int)env.Step;
            var dark = DarkBg(env);
            var cols = new[]
            {
                ScK(env, "ghostA") ?? ScK(env, "accent") ?? "#ffffff",
                ScK(env, "ghostB") ?? ScK(env, "fg") ?? "#ffffff",
                ScK(env, "accent") ?? "#ffffff",
            };
            var e = J.E.InCubic(p);
            var sp = sz * (0.05 + 1.5 * e);
            double[][] dirs = { new[] { -1.0, -0.3 }, new[] { 1.0, 0.25 }, new[] { 0.15, 0.9 } };
            var a0 = NN(it, "alpha", 1);
            var ca = a0 * J.Smooth(0, 0.1, p) * (1 - J.Smooth(0.55, 0.96, p));
            it["alpha"] = a0 * (1 - J.Smooth(0.05, 0.35, p));
            // JS chainPost（3 通道副本）——就地执行
            if (env.Pass != "main" || ca <= 0.01) return;
            var cf = MergedNow(env);
            for (var k = 0; k < 3; k++)
            {
                var jx = J.Rs(seed, step, k, 271) * sz * 0.06 * (0.3 + e);
                var jy = J.Rs(seed, step, k, 272) * sz * 0.015;
                var ox = dirs[k][0] * sp + jx;
                var oy = dirs[k][1] * sp * 0.5 + jy;
                var kLoc = k;
                DrawCopy(env, it, cf, c =>
                {
                    c["streak"] = null;
                    c["echo"] = null;
                    c["x"] = N0(it, "x") + ox;
                    c["y"] = N0(it, "y") + oy;
                    c["size"] = N0(it, "size") * (1 + 0.08 * kLoc * e);
                    c["_m"] = null;
                    c["_lay"] = null;
                    c["color"] = cols[kLoc];
                    c["gradient"] = null;
                    c["pattern"] = null;
                    c["shadow"] = null;
                    c["extrude"] = null;
                    c["alpha"] = ca * (kLoc == 2 ? 0.8 : 0.9);
                    c["blend"] = dark ? "screen" : "multiply";
                    c["fill"] = true;
                });
            }
        },
    });

    // ---------------- shockOut（X.shockOut；JS ae:'explode'） ----------------

    private static void ShockOut() => Reg1("shockOut", new MoveDef
    {
        Name = "衝撃波",
        Tags = new[] { "pop", "graphic" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var sz = N0(it, "size");
            var seed = SeedOf(it);
            var acc = ScK(env, "accent") ?? "#ffffff";
            var single = IsSingle(env, it);
            var pre = Math.Sin(Math.PI * J.Clamp(p / 0.16)) * (p < 0.16 ? 1 : 0);
            double rr = 0;
            double[] center;

            // 返回 (径向位移, 缩放, alpha, rot)
            (double M, double S, double A, double Rt) Push(double dd, int i)
            {
                var passed = rr - dd;
                if (passed <= 0) return (-pre * sz * 0.08, 1 - 0.05 * pre, 1, 0);
                var k = 1 - Math.Exp(-passed / (sz * 0.7));
                return (sz * 1.6 * k,
                    1 + 0.2 * Math.Exp(-passed / (sz * 0.25)) - 0.45 * J.Smooth(0, sz * 2, passed),
                    1 - J.Smooth(sz * 0.15, sz * 1.5, passed),
                    J.Rs(seed, i, 281) * 50 * k);
            }
            if (single)
            {
                center = new[] { env.W / 2.0, env.H / 2.0 };
                var rmax = Math.Sqrt(env.W * (double)env.W + env.H * (double)env.H) * 0.55;
                rr = rmax * J.E.OutCubic(J.Clamp((p - 0.12) / 0.88));
                var dx = N0(it, "x") - center[0];
                var dy = N0(it, "y") - center[1];
                var dd = Math.Sqrt(dx * dx + dy * dy);
                if (dd == 0) dd = 1;
                var push = Push(dd, (int)Num(it, "mi"));
                it["x"] = N0(it, "x") + dx / dd * push.M;
                it["y"] = N0(it, "y") + dy / dd * push.M;
                it["size"] = N0(it, "size") * Math.Max(0.05, push.S);
                it["rot"] = N0(it, "rot") + push.Rt;
                it["alpha"] = NN(it, "alpha", 1) * push.A;
                if (Math.Round(Num(it, "mi")) != 0) return;
            }
            else
            {
                var b = LBox(it);
                center = ToD(it, b.Cx, b.Cy);
                var rmax = Math.Sqrt(b.W * b.W + b.H * b.H) / 2 + sz * 1.8;
                rr = rmax * J.E.OutCubic(J.Clamp((p - 0.12) / 0.88));
                Glyphs(env, (i, g, _) =>
                {
                    if (g == null) return null;
                    var dx = GCx(g, it) - b.Cx;
                    var dy = GCy(g, it) - b.Cy;
                    var dd = Math.Sqrt(dx * dx + dy * dy);
                    if (dd == 0) dd = 1;
                    var push = Push(dd, i);
                    return new CharXf { Dx = dx / dd * push.M, Dy = dy / dd * push.M, S = Math.Max(0.05, push.S), A = push.A, Rot = push.Rt };
                });
            }
            if (rr <= 0.5 || !Primary(it)) return;
            var fa = 1 - J.Smooth(0.3, 1, p);
            // JS chainPost（冲击环）——就地执行
            env.Circle(center[0], center[1], rr, null, acc, Math.Max(1.5, sz * 0.09 * fa), fa, false);
            env.Circle(center[0], center[1], rr * 0.82, null, acc, Math.Max(1, sz * 0.025 * fa), fa * 0.6, false);
        },
    });

    // ---------------- floodOut（X.floodOut；JS ae:'wipe'） ----------------

    private static void FloodOut() => Reg1("floodOut", new MoveDef
    {
        Name = "水没",
        Tags = new[] { "emotional", "calm" },
        W = 0.9,
        OutDur = (dur, _) => J.Clamp(dur * 0.4, 0.32, 0.8),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var sz = N0(it, "size");
            var t = env.Ltb;
            var acc = ScK(env, "accent") ?? "#ffffff";
            var c0 = ColOf(it);
            var single = IsSingle(env, it);
            var bb = DBox(it, sz * 0.15);
            var uu = single ? env.H * 0.12 : sz;
            var a = uu * 0.07;
            var e = J.E.InOutSine(J.Clamp(p / 0.9));
            var yY = single ? J.Lerp(env.H * 0.74, env.H * 0.24, e) : J.Lerp(bb.Y1 + a * 2, bb.Y0 - a * 3, e);
            var kN = J.Clamp((int)Math.Ceiling(bb.W / (uu * 0.12)), 8, 80);
            var x0 = bb.X0 - sz * 0.2;
            var x1 = bb.X1 + sz * 0.2;
            var surf = new double[kN + 1][];
            for (var k = 0; k <= kN; k++)
            {
                var x = J.Lerp(x0, x1, k / (double)kN);
                surf[k] = new[] { x, yY + a * Math.Sin(x / uu * 4.2 + t * 5) + a * 0.5 * Math.Sin(x / uu * 9.5 - t * 7.3) };
            }
            if (yY < bb.Y0 - a * 2) it["alpha"] = 0.0;
            // JS clipFn = above（水面以上区域，远端 -H*2 封口）→ Env.ClipFn 桥
            var surfLoc = surf;
            var envH = env.H;
            env.ClipFn = (_, _) => SurfPath(surfLoc, false, envH);
            var a0 = NN(it, "alpha", 1);
            var uw = (1 - J.Smooth(0.3, 0.95, p)) * 0.5;
            var fa = 1 - J.Smooth(0.82, 0.98, p);
            // JS chainPost——就地执行
            if (uw > 0.01 && env.Pass == "main")
            {
                // 水下部分：折射、染色、淡出
                const int nSeg = 6;
                var h = bb.H + sz;
                var @out = new JArray();
                for (var j = 0; j < nSeg; j++)
                {
                    var y = bb.Y0 - sz * 0.5 + j * h / nSeg;
                    @out.Add(new JArray(y, y + h / nSeg + 0.4, Math.Sin(y / sz * 7 + t * 6) * sz * 0.04));
                }
                using var belowPath = SurfPath(surf, true, env.H);
                DrawFxCopy(env, it, MergedNow(env), belowPath, c =>
                {
                    c["y"] = N0(it, "y") + sz * 0.05;
                    c["alpha"] = a0 * uw;
                    c["color"] = MixC(c0, acc, 0.55);
                    c["gradient"] = null;
                    c["pattern"] = null;
                    c["shadow"] = null;
                    c["bands"] = @out;
                });
            }
            if (env.Pass != "main" || fa <= 0.01) return;
            env.Line(surf, acc, Math.Max(4, uu * 0.1), 0.15 * fa, false);
            env.Line(surf, acc, Math.Max(1.5, uu * 0.028), fa, false);
            for (var j = 0; j < 7; j++)
            {
                // 往水面冒的气泡
                var x = J.Lerp(x0, x1, J.R(SeedOf(it), j, 291));
                var ph = (p * 2.2 + J.R(SeedOf(it), j, 292)) % 1;
                var y = yY + a + (1 - ph) * sz * 0.9;
                if (y > bb.Y1 + sz * 0.3) continue;
                env.Circle(x + Math.Sin(ph * 9 + j) * sz * 0.03, y, Math.Max(1, sz * 0.03 * (0.5 + ph)), null, acc, Math.Max(1, sz * 0.01), 0.7 * fa * (1 - ph * 0.5), false);
            }
        },
    });

    /// <summary>水面折线 + 远端封口 → 裁剪路径。below=false 取上方区域，true 取下方。</summary>
    private static SKPath SurfPath(double[][] surf, bool below, double h)
    {
        var path = new SKPath();
        for (var i = 0; i < surf.Length; i++)
        {
            if (i == 0) path.MoveTo((float)surf[i][0], (float)surf[i][1]);
            else path.LineTo((float)surf[i][0], (float)surf[i][1]);
        }
        path.LineTo((float)surf[^1][0], (float)(below ? h * 3 : -h * 2));
        path.LineTo((float)surf[0][0], (float)(below ? h * 3 : -h * 2));
        path.Close();
        return path;
    }

    // ---------------- slashOut（X.slashOut；JS ae:'slice'） ----------------

    private static void SlashOut() => Reg1("slashOut", new MoveDef
    {
        Name = "一刀両断",
        Tags = new[] { "graphic", "emotional", "pop" },
        W = 1,
        OutDur = (dur, _) => J.Clamp(dur * 0.4, 0.32, 0.8),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var sz = N0(it, "size");
            var vert = B(it, "vertical");
            var acc = ScK(env, "accent") ?? "#ffffff";
            var b = LBox(it, sz * 0.2);
            var seed = SeedOf(it);
            var tilt = (CutBit(env, 301) ? 1 : -1) * (vert ? 16 + 8 * CutR(env, 302) : 7 + 5 * CutR(env, 302));
            var th = ((vert ? 90 : 0) + tilt) * J.DEG;
            var d = new[] { Math.Cos(th), Math.Sin(th) };
            var nn = new[] { -d[1], d[0] };
            var c = new[] { b.Cx, b.Cy };
            var l = Math.Sqrt(b.W * b.W + b.H * b.H) + sz * 4;
            double[][] HalfL(int sign)
            {
                var o = new[] { c[0] + nn[0] * sign * l, c[1] + nn[1] * sign * l };
                return new[]
                {
                    new[] { c[0] - d[0] * l, c[1] - d[1] * l },
                    new[] { c[0] + d[0] * l, c[1] + d[1] * l },
                    new[] { o[0] + d[0] * l, o[1] + d[1] * l },
                    new[] { o[0] - d[0] * l, o[1] - d[1] * l },
                };
            }
            var lower = HalfL(1);  // 横排时 +n 是切口下方
            var upper = HalfL(-1);
            var down = d[1] > 0 ? d : new[] { -d[0], -d[1] }; // 沿切口的下坡方向
            var fl = J.Clamp(p / 0.12);
            var gap = J.Smooth(0.12, 0.3, p);
            var v = J.Clamp((p - 0.3) / 0.7);
            var slide = sz * 0.06 * gap + sz * 2.2 * J.E.InQuad(v);
            var a0 = NN(it, "alpha", 1);
            var uOff = Rot2(down[0] * slide - nn[0] * sz * 0.05 * gap, down[1] * slide - nn[1] * sz * 0.05 * gap, N0(it, "rot") * J.DEG);
            var lOff = Rot2(
                -down[0] * sz * 0.35 * J.E.InQuad(v) + nn[0] * sz * 0.03 * gap,
                -down[1] * sz * 0.35 * J.E.InQuad(v) + nn[1] * (sz * 0.03 * gap + sz * 0.5 * J.E.InQuad(v)), N0(it, "rot") * J.DEG);
            var x0 = N0(it, "x");
            var y0 = N0(it, "y");
            it["x"] = x0 + lOff[0];
            it["y"] = y0 + lOff[1];
            it["alpha"] = a0 * (1 - J.Smooth(0.5, 0.95, p));
            // JS clipFn = polyL(ctx, it3, lower)（下半，item 空间多边形、按 drawFx 时点 item 位姿）→ Env.ClipFn 桥
            var lowerLoc = lower;
            env.ClipFn = (_, it3) => PolyPath(lowerLoc, N0(it3, "x"), N0(it3, "y"), N0(it3, "rot"));
            var ua = a0 * (1 - J.Smooth(0.62, 0.98, p));
            // JS chainPost——就地执行
            var rot = N0(it, "rot");
            using var upperPath = PolyPath(upper, x0 + uOff[0], y0 + uOff[1], rot);
            DrawFxCopy(env, it, MergedNow(env), upperPath, c2 =>
            {
                c2["x"] = x0 + uOff[0];
                c2["y"] = y0 + uOff[1];
                c2["alpha"] = ua;
            });
            if (env.Pass != "main") return;
            var fa = 1 - J.Smooth(0.12, 0.34, p);
            if (fa <= 0.01) return;
            var ext = (vert ? b.H : b.W) / 2 + sz * 0.9;
            var p0 = new[] { c[0] - d[0] * ext, c[1] - d[1] * ext };
            var p1 = new[] { c[0] + d[0] * ext, c[1] + d[1] * ext };
            var st = CutBit(env, 303);
            var aa = st ? p1 : p0;
            var bb2 = st ? p0 : p1;
            var ee = J.E.OutExpo(fl);
            var tail = J.Smooth(0.08, 0.3, p);
            var ss = new[] { J.Lerp(aa[0], bb2[0], tail), J.Lerp(aa[1], bb2[1], tail) };
            var tt = new[] { J.Lerp(aa[0], bb2[0], ee), J.Lerp(aa[1], bb2[1], ee) };
            InItem(env, new JObject { ["x"] = x0, ["y"] = y0, ["rot"] = rot }, () =>
            {
                env.Line(new[] { ss, tt }, acc, Math.Max(2, sz * 0.09 * fa), 0.35 * fa, false);
                env.Line(new[] { ss, tt }, acc, Math.Max(1.5, sz * 0.03 * fa), fa, false);
            });
        },
    });

    // ---------------- mosaicOut（X.mosaicOut；JS ae:'glitch'） ----------------

    /// <summary>JS 的 DOM 离屏 canvas（模块级 MOS）对位：常驻离屏面，不足时扩容。</summary>
    private static SKSurface? _mos;

    private static void MosaicOut() => Reg1("mosaicOut", new MoveDef
    {
        Name = "モザイク",
        Tags = new[] { "glitch", "graphic" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var sz = N0(it, "size");
            var blk = sz * (0.01 + 0.34 * Math.Pow(p, 1.5));
            if (blk * (env.Scale != 0 ? env.Scale : 1) < 1.6) return;
            var a0 = NN(it, "alpha", 1);
            var fade = 1 - J.Smooth(0.55, 0.97, p);
            var bb = DBox(it, sz * 0.15);
            it["alpha"] = 0.0;
            // JS chainPost——就地执行（每帧只离屏一次，色差 ghost 不参与）
            if (env.Pass != "main") return;
            var b = blk;
            var ow = (int)Math.Ceiling(bb.W / b);
            var oh = (int)Math.Ceiling(bb.H / b);
            if (ow > 360 || oh > 360)
            {
                b *= Math.Max(ow, oh) / 360.0;
                ow = (int)Math.Ceiling(bb.W / b);
                oh = (int)Math.Ceiling(bb.H / b);
            }
            if (ow <= 0 || oh <= 0) return;
            var needW = Math.Max(ow + 2, 64);
            var needH = Math.Max(oh + 2, 64);
            if (_mos == null || _mos.Canvas.DeviceClipBounds.Width < needW || _mos.Canvas.DeviceClipBounds.Height < needH)
            {
                _mos?.Dispose();
                _mos = SKSurface.Create(new SKImageInfo(needW, needH));
            }
            var cv = _mos!;
            var c2 = cv.Canvas;
            c2.RestoreToCount(0);
            c2.Clear(SKColors.Transparent);
            c2.Scale((float)(1 / b));
            c2.Translate((float)-bb.X0, (float)-bb.Y0);
            var e2 = CloneEnvFor(env, c2, 1 / b, allowFilter: false);
            e2.CharFn = MergedNow(env);
            e2.PieceFn = null;
            JText.DrawItem(e2, CopyOfReset(it, c =>
            {
                c["alpha"] = a0 * fade;
                c["blur"] = 0.0;
                c["shadow"] = null;
                c["blend"] = null;
            }), new JText.TextDrawOpts { AllowFilter = false, NoPieces = true });
            c2.RestoreToCount(0);
            var ctx = env.Ctx;
            ctx.Save();
            using var img = cv.Snapshot();
            using var paint = new SKPaint { IsAntialias = false };
            ctx.DrawImage(img, new SKRect(0, 0, ow, oh),
                new SKRect((float)bb.X0, (float)bb.Y0, (float)(bb.X0 + ow * b), (float)(bb.Y0 + oh * b)),
                new SKSamplingOptions(SKFilterMode.Nearest), paint); // imageSmoothingEnabled = false
            ctx.Restore();
        },
    });

    /// <summary>JS 的 Object.assign({}, env2, {ctx, scale, allowFilter}) 对位。</summary>
    private static Env CloneEnvFor(Env e, SKCanvas ctx, double scale, bool allowFilter) => new()
    {
        Ctx = ctx,
        W = e.W,
        H = e.H,
        Sc = e.Sc,
        St = e.St,
        Fx = e.Fx,
        Fps = e.Fps,
        Cut = e.Cut,
        Plan = e.Plan,
        Lt = e.Lt,
        T = e.T,
        Ltb = e.Ltb,
        Step = e.Step,
        Energy = e.Energy,
        Beat = e.Beat,
        BeatHas = e.BeatHas,
        BeatSince = e.BeatSince,
        BeatLen = e.BeatLen,
        BeatIndex = e.BeatIndex,
        Zone = e.Zone,
        Pass = e.Pass,
        PassColor = e.PassColor,
        Scale = scale,
        PIn = e.PIn,
        POut = e.POut,
        It = e.It,
        Ap = e.Ap,
        Blend = e.Blend,
        Layer = e.Layer,
        HideText = e.HideText,
        AllowFilter = allowFilter,
        GlyphLog = e.GlyphLog,
    };

    // ---------------- scribbleOut（X.scribbleOut；JS ae:'wipe'） ----------------

    private static void ScribbleOut() => Reg1("scribbleOut", new MoveDef
    {
        Name = "ぐしゃぐしゃ消し",
        Tags = new[] { "editorial", "emotional", "pop" },
        W = 0.8,
        OutDur = (dur, _) => J.Clamp(dur * 0.4, 0.32, 0.8),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var sz = N0(it, "size");
            var seed = SeedOf(it);
            var vert = B(it, "vertical");
            var acc = ScK(env, "accent") ?? "#ffffff";
            var lines = LineExt(it);
            if (lines.Count == 0) return;
            var paths = new List<double[][]>();
            for (var li = 0; li < lines.Count; li++)
            {
                var l = lines[li];
                var a0 = (vert ? l.Y0 : l.X0) - sz * 0.15;
                var a1 = (vert ? l.Y1 : l.X1) + sz * 0.15;
                var mid = vert ? (l.X0 + l.X1) / 2 : (l.Y0 + l.Y1) / 2;
                var half = (vert ? l.X1 - l.X0 : l.Y1 - l.Y0) / 2;
                var pts = new List<double[]>();
                double[] P2(double a, double o) => vert ? new[] { mid + o, a } : new[] { a, mid + o };
                var a = a0;
                var k = 0;
                while (a < a1 && k < 200)
                {
                    // 去程：笔触不匀，手上下漂
                    var drift = Math.Sin(a / sz * 1.3 + li) * half * 0.18;
                    pts.Add(P2(a, drift + (k % 2 != 0 ? 1 : -1) * half * (0.8 + 0.45 * J.R(seed, li, k, 312))));
                    a += sz * (0.14 + 0.2 * J.R(seed, li, k, 311));
                    k++;
                }
                a = a1;
                while (a > a0 && k < 400)
                {
                    // 回程：更平更快，补缝
                    var drift = Math.Sin(a / sz * 2.1 + li * 3) * half * 0.25;
                    pts.Add(P2(a, drift + (k % 2 != 0 ? 1 : -1) * half * (0.45 + 0.4 * J.R(seed, li, k, 314))));
                    a -= sz * (0.2 + 0.25 * J.R(seed, li, k, 313));
                    k++;
                }
                paths.Add(pts.ToArray());
            }
            var dr = J.E.InOutSine(J.Clamp(p / 0.58));
            var lw = sz * 0.15 * (1 - 0.7 * J.Smooth(0.62, 0.95, p));
            var sa = 1 - J.Smooth(0.66, 0.97, p);
            it["alpha"] = NN(it, "alpha", 1) * (1 - J.Smooth(0.45, 0.75, p));
            // JS chainPost（粗马克笔涂痕）——就地执行
            if (env.Pass != "main" || sa <= 0.01) return;
            InItem(env, it, () =>
            {
                foreach (var pts in paths)
                {
                    if (pts.Length == 0) continue;
                    double tot = 0;
                    var seg = new double[pts.Length - 1];
                    for (var k = 1; k < pts.Length; k++)
                    {
                        var s2 = Math.Sqrt(Math.Pow(pts[k][0] - pts[k - 1][0], 2) + Math.Pow(pts[k][1] - pts[k - 1][1], 2));
                        seg[k - 1] = s2;
                        tot += s2;
                    }
                    var rem = tot * dr;
                    using var path = new SKPath();
                    path.MoveTo((float)pts[0][0], (float)pts[0][1]);
                    for (var k = 1; k < pts.Length && rem > 0; k++)
                    {
                        var s2 = seg[k - 1];
                        if (rem >= s2) path.LineTo((float)pts[k][0], (float)pts[k][1]);
                        else
                        {
                            var f = rem / s2;
                            path.LineTo((float)J.Lerp(pts[k - 1][0], pts[k][0], f), (float)J.Lerp(pts[k - 1][1], pts[k][1], f));
                        }
                        rem -= s2;
                    }
                    using var paint = new SKPaint
                    {
                        IsAntialias = true,
                        IsStroke = true,
                        StrokeWidth = (float)Math.Max(1, lw),
                        StrokeJoin = SKStrokeJoin.Round,
                        StrokeCap = SKStrokeCap.Round,
                        Color = Env.SkColor(acc, sa),
                        BlendMode = env.Blend,
                    };
                    env.Ctx.DrawPath(path, paint);
                }
            });
        },
    });

    // ---------------- candleOut（X.candleOut；JS ae:'drift'） ----------------

    private static void CandleOut() => Reg1("candleOut", new MoveDef
    {
        Name = "吹き消す",
        Tags = new[] { "emotional", "calm" },
        W = 0.9,
        OutDur = (dur, _) => J.Clamp(dur * 0.45, 0.38, 0.9),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var seed = SeedOf(it);
            var sz = N0(it, "size");
            var step = (int)env.Step;
            var ord0 = OrderOf(env, it);
            var dir = CutBit(env, 321) ? 1 : -1;
            double Ord(int i, int n) => dir > 0 ? ord0(i, n) : 1 - ord0(i, n);
            var warm = MixC(ColOf(it), ScK(env, "accent") ?? "#ffffff", 0.5);
            var sub = ScK(env, "sub") ?? "#ffffff";
            var lay = LayOf(it);
            var sy0 = N1(it, "sy");
            double U(int i, int n) => (p - Ord(i, n) * 0.42) / 0.5;
            Glyphs(env, (i, _, n) =>
            {
                var u = U(i, n);
                if (u <= 0) return null;
                if (u >= 0.45) return new CharXf { Hide = true };
                var lean = Math.Sin(Math.PI * Math.Min(1, u / 0.45));
                var fl = J.R(seed, i, step, 322);
                return new CharXf
                {
                    Skew = -dir * 22 * lean,
                    Dx = dir * sz * 0.06 * lean,
                    Color = warm,
                    A = u > 0.3 ? (1 - (u - 0.3) / 0.15) * (fl < 0.5 ? 1 : 0.6) : 0.65 + 0.35 * fl,
                    S = 1 + 0.04 * lean,
                };
            });
            // JS chainPost（一缕烟）——就地执行
            if (env.Pass != "main") return;
            InItem(env, it, () =>
            {
                var uu = DirI(it, 0, -1);
                var ri = DirI(it, 1, 0);
                foreach (var g in lay.Cells)
                {
                    if (IsSp(g.Ch)) continue;
                    var u = U(g.I, lay.N);
                    if (u < 0.36 || u >= 1) continue;
                    var age = (u - 0.36) / 0.64;
                    var tx = GCx(g, it) + uu[0] * g.H * sy0 * 0.4;
                    var ty = GCy(g, it) + uu[1] * g.H * sy0 * 0.4;
                    var pts = new double[10][];
                    for (var k = 0; k <= 9; k++)
                    {
                        var f = k / 9.0;
                        var h = sz * (0.25 + 1.3 * age) * f;
                        var w = Math.Sin(f * 5 + age * 7 + g.I) * sz * 0.09 * f + dir * sz * 0.4 * age * f;
                        pts[k] = new[] { tx + uu[0] * h + ri[0] * w, ty + uu[1] * h + ri[1] * w };
                    }
                    env.Line(pts, sub, Math.Max(1.2, sz * 0.03 * (1 - age * 0.5)), 0.9 * (1 - age), false);
                }
            });
        },
    });

    // ================= hold（JS H 字面量序） =================

    // ---------------- glowFlicker（H.glowFlicker；JS ae:'breathe'） ----------------

    private static void GlowFlicker() => RegH("glowFlicker", new MoveDef
    {
        Name = "灯火のゆらぎ",
        Tags = new[] { "calm", "emotional" },
        W = 0.5,
        Draw = env =>
        {
            var it = env.It!;
            var k = env.Ap * MotionK(env);
            if (k < 0.01) return;
            var t = env.Ltb;
            var seed = SeedOf(it);
            var sz = N0(it, "size");
            var kk = Math.Min(1, k);
            var sy0 = N1(it, "sy");
            var f = 0.6 + 0.28 * J.Noise1(t * 3.3, seed) + 0.12 * J.Noise1(t * 11, seed + 5);
            var lvl = J.Clamp(f * (1 - 0.45 * J.Smooth(0.35, 0.8, J.Noise1(t * 1.1, seed + 9))));
            var acc = ScK(env, "accent") ?? "#ffffff";
            var glow = DarkBg(env) ? MixC(acc, "#ffffff", 0.3) : acc;
            if (env.Pass == "main" && it["shadow"] == null && it["extrude"] == null && !GlyphBlurOn(env, it))
                it["shadow"] = new JObject
                {
                    ["color"] = Rgba(glow, DarkBg(env) ? 0.9 : 0.6),
                    ["blur"] = sz * (0.06 + 0.3 * lvl) * kk,
                    ["dx"] = 0,
                    ["dy"] = -sz * 0.02 * lvl * kk,
                };
            it["alpha"] = NN(it, "alpha", 1) * (1 - 0.16 * (1 - lvl) * kk);
            it["color"] = MixC(ColOf(it), acc, 0.1 * lvl * kk);
            Glyphs(env, (i, g, _) =>
            {
                if (g == null) return null;
                var st = 1 + 0.025 * k * (0.5 + 0.5 * J.Noise1(t * 6 + i * 2.3, seed + i));
                return B(g, "r90") ? new CharXf { Sx = st } : new CharXf { Sy = st, Dy = -(st - 1) * sz * sy0 * 0.5 };
            });
        },
    });

    // ---------------- windGust（H.windGust；JS ae:'wave'） ----------------

    private static void WindGust() => RegH("windGust", new MoveDef
    {
        Name = "突風",
        Tags = new[] { "pop", "emotional" },
        W = 0.5,
        Draw = env =>
        {
            var it = env.It!;
            var k = env.Ap * MotionK(env);
            if (k < 0.01) return;
            var cs = unchecked((int)(env.Cut?.Seed ?? 0));
            var sz = N0(it, "size");
            var ord = OrderOf(env, it);
            double cyc, since;
            // env.beat 分支：C# Env 未挂 beat 对象，走 ltb 回退式（第一阵风在 cut 开始约 0.5s 后）
            const double per = 2.2;
            var t = env.Ltb - 0.5 - (cs % 5) * 0.06;
            cyc = Math.Floor(t / per);
            since = t - cyc * per;
            var dir = J.R(cs, (int)cyc, 601) < 0.5 ? 1 : -1;
            var str = 0.7 + 0.3 * J.R(cs, (int)cyc, 602);
            Glyphs(env, (i, _, n) =>
            {
                var tau = since - (dir > 0 ? ord(i, n) : 1 - ord(i, n)) * 0.4;
                if (tau <= 0 || tau > 1.7) return null;
                var r = tau < 0.25 ? J.Smooth(0, 0.25, tau)
                    : tau < 0.6 ? 1 + 0.08 * Math.Sin((tau - 0.25) * 30)
                    : Math.Cos((tau - 0.6) * 9) * Math.Exp(-(tau - 0.6) * 4);
                return new CharXf { Dx = dir * sz * 0.12 * r * k * str, Skew = -dir * 18 * r * k * str, Rot = dir * 3 * r * k * str };
            });
        },
    });

    // ---------------- dangle（H.dangle；JS ae:'wave'） ----------------

    private static void Dangle() => RegH("dangle", new MoveDef
    {
        Name = "ぶら下がり",
        Tags = new[] { "calm", "emotional" },
        W = 0.5,
        Draw = env =>
        {
            var it = env.It!;
            var k = env.Ap * MotionK(env);
            if (k < 0.01) return;
            var t = env.Ltb;
            var seed = SeedOf(it);
            var sz = N0(it, "size");
            var sy0 = N1(it, "sy");
            var vert = B(it, "vertical");
            Glyphs(env, (i, g, _) =>
            {
                if (g == null) return null;
                var w = J.TAU * (0.5 + 0.28 * J.R(seed, i, 611));
                var ph = J.R(seed, i, 612) * J.TAU;
                var th = (6.5 * Math.Sin(w * t + ph) + 1.6 * Math.Sin(w * 2.7 * t + ph * 2)) * k * J.DEG;
                var hh = (vert ? D(g, "h") : sz) * sy0 / 2; // 轴在字形顶边
                return new CharXf { Dx = -hh * Math.Sin(th), Dy = hh * (Math.Cos(th) - 1), Rot = th / J.DEG };
            });
        },
    });

    // ---------------- eqBounce（H.eqBounce；JS ae:'breathe'） ----------------

    private static void EqBounce() => RegH("eqBounce", new MoveDef
    {
        Name = "音圧で伸びる",
        Tags = new[] { "pop", "graphic" },
        W = 0.5,
        Draw = env =>
        {
            var it = env.It!;
            var k = env.Ap * MotionK(env);
            if (k < 0.01) return;
            var t = env.Ltb;
            var seed = SeedOf(it);
            var sz = N0(it, "size");
            var sy0 = N1(it, "sy");
            var acc = ScK(env, "accent") ?? "#ffffff";
            var c0 = ColOf(it);
            var vert = B(it, "vertical");
            var @base = J.Clamp(env.Energy * 1.2); // env.energy 恒有值（C# double 非可空）
            var mi = Num(it, "mi");
            Glyphs(env, (i, g, _) =>
            {
                if (g == null) return null;
                var band = J.Clamp(@base * (0.3 + 0.7 * (0.5 + 0.5 * J.Noise1(t * 6.5 + (i + mi) * 1.9, seed + 3))) * 1.15);
                var st = 1 + 0.32 * band * k;
                var col = band > 0.72 ? MixC(c0, acc, (band - 0.72) / 0.28 * 0.7 * Math.Min(1, k)) : null;
                if (vert) return B(g, "r90") ? new CharXf { Sy = st, Color = col } : new CharXf { Sx = st, Color = col };
                return new CharXf { Sy = st, Dy = -(st - 1) * sz * sy0 * 0.5, Color = col };
            });
        },
    });

    // ---------------- flashBox（H.flashBox；JS ae:'glitchtick'） ----------------

    private static void FlashBox() => RegH("flashBox", new MoveDef
    {
        Name = "拍で反転",
        Tags = new[] { "pop", "graphic", "glitch" },
        W = 0.4,
        Draw = env =>
        {
            var it = env.It!;
            var k = env.Ap * MotionK(env);
            if (k < 0.35) return;
            var len = BeatLen(env, 0.62);
            var since = BeatSince(env, 0.62);
            var idx = BeatIdx(env, 0.62);
            var u = since / (len * 0.6);
            if (u >= 1) return;
            var nCut = CutN(env);
            var lay = LayOf(it);
            var vis = lay.Cells.Where(g => !IsSp(g.Ch)).ToList();
            if (vis.Count == 0) return;
            var pick = (int)(J.H(unchecked((int)(env.Cut?.Seed ?? 0)), idx, 621) % Math.Max(1, nCut));
            JText.GlyphCell gSel;
            if (IsSingle(env, it))
            {
                if (Math.Round(Num(it, "mi")) != pick) return;
                gSel = vis[0];
            }
            else gSel = vis[pick % vis.Count];
            var pop = J.E.OutBack(J.Clamp(u / 0.18), 2.2);
            var sz = N0(it, "size");
            var sx0 = N1(it, "sx");
            var sy0 = N1(it, "sy");
            var acc = ScK(env, "accent") ?? "#ffffff";
            var vert = B(it, "vertical");
            var bg = J.FitContrast(ScK(env, "bg") ?? "#000000", acc, 2.5);
            var gI = gSel.I;
            Glyphs(env, (i, _, _) => i == gI ? new CharXf { Color = bg, S = 1 + 0.05 * (1 - u) } : null);
            // JS chainPre（accent 色块，画在文字前）——就地执行（层级同 JS，文字下方）
            if (env.Pass != "main") return;
            InItem(env, it, () =>
            {
                var w = (vert ? sz * sx0 : gSel.W * sx0) * 1.06 * pop;
                var h = (vert ? gSel.H * sy0 : sz * sy0) * 1.06 * pop;
                env.Rect(GCx(gSel, it) - w / 2, GCy(gSel, it) - h / 2, w, h, acc, 1, false);
            });
        },
    });

    // ---------------- glintSweep（H.glintSweep；JS ae:'still'） ----------------

    private static void GlintSweep() => RegH("glintSweep", new MoveDef
    {
        Name = "光沢",
        Tags = new[] { "graphic", "calm" },
        W = 0.5,
        Draw = env =>
        {
            var it = env.It!;
            var k = env.Ap * MotionK(env);
            if (k < 0.05 || env.Pass != "main") return;
            const double per = 2.4;
            var t = env.Ltb - 0.5 - Num(it, "mi") * 0.04; // 第一次扫过在 cut 开始约 0.5s 后
            var u = ((t % per) + per) % per / 0.75;
            if (u >= 1) return;
            var sz = N0(it, "size");
            var bb = DBox(it, sz * 0.2);
            var c0 = ColOf(it);
            var gc = J.Lum(c0) > 0.72 ? ScK(env, "accent") ?? "#ffffff" : MixC(c0, "#ffffff", 0.85);
            var bw = sz * 0.34;
            var sl = bb.H * 0.45;
            var x = J.Lerp(bb.X0 - bw - sl, bb.X1 + bw + sl, J.E.InOutSine(u));
            var a = Math.Min(1, k) * 0.9;
            // JS chainPost（clip 斜光带内画亮色副本）——就地执行
            using var band = new SKPath();
            void Band(double x0, double w)
            {
                band.MoveTo((float)(x0 + sl), (float)bb.Y0);
                band.LineTo((float)(x0 + sl + w), (float)bb.Y0);
                band.LineTo((float)(x0 - sl + w), (float)bb.Y1);
                band.LineTo((float)(x0 - sl), (float)bb.Y1);
                band.Close();
            }
            Band(x - bw / 2, bw);
            Band(x + bw * 0.75, bw * 0.28);
            var prevC = env.CharFn;
            var prevP = env.PieceFn;
            env.CharFn = MergedNow(env);
            env.PieceFn = null;
            try
            {
                env.Ctx.Save();
                env.Ctx.ClipPath(band, SKClipOperation.Intersect, true);
                try
                {
                    JText.DrawItem(env, CopyOfReset(it, c =>
                    {
                        c["color"] = gc;
                        c["gradient"] = null;
                        c["pattern"] = null;
                        c["shadow"] = null;
                        c["extrude"] = null;
                        c["alpha"] = NN(it, "alpha", 1) * a;
                    }), new JText.TextDrawOpts { HideText = env.HideText, GlyphLog = env.GlyphLog, AllowFilter = env.AllowFilter, NoPieces = true });
                }
                finally
                {
                    env.Ctx.Restore();
                }
            }
            finally
            {
                env.CharFn = prevC;
                env.PieceFn = prevP;
            }
        },
    });

    // ---------------- flipSwap（H.flipSwap；JS ae:'glitchtick'） ----------------

    private static void FlipSwap() => RegH("flipSwap", new MoveDef
    {
        Name = "時々裏返る",
        Tags = new[] { "glitch", "pop" },
        W = 0.4,
        Draw = env =>
        {
            var it = env.It!;
            var k = env.Ap * MotionK(env);
            if (k < 0.5) return;
            var cs = unchecked((int)(env.Cut?.Seed ?? 0));
            const double per = 1.6;
            var t = env.Ltb - 0.45;
            var cyc = (int)Math.Floor(t / per);
            var u = (t - cyc * per) / 0.95;
            if (u >= 1) return;
            var nCut = CutN(env);
            var lay = LayOf(it);
            var vis = lay.Cells.Where(g => !IsSp(g.Ch)).ToList();
            if (vis.Count == 0) return;
            var pick = (int)(J.H(cs, cyc, 631) % Math.Max(1, nCut));
            int gi;
            if (IsSingle(env, it))
            {
                if (Math.Round(Num(it, "mi")) != pick) return;
                gi = vis[0].I;
            }
            else gi = vis[pick % vis.Count].I;
            var poolKana = JLang.Pool("kana") ?? "ア";
            if (poolKana.Length == 0) poolKana = "ア";
            var ch = poolKana[(int)(J.H(cs, cyc, 632) % (uint)poolKana.Length)];
            var acc = ScK(env, "accent") ?? "#ffffff";
            double sx;
            bool alt;
            if (u < 0.18)
            {
                sx = Math.Cos(u / 0.18 * Math.PI / 2);
                alt = false;
            }
            else if (u < 0.36)
            {
                sx = Math.Sin((u - 0.18) / 0.18 * Math.PI / 2);
                alt = true;
            }
            else if (u < 0.64)
            {
                sx = 1;
                alt = true;
            }
            else if (u < 0.82)
            {
                sx = Math.Cos((u - 0.64) / 0.18 * Math.PI / 2);
                alt = true;
            }
            else
            {
                sx = Math.Sin((u - 0.82) / 0.18 * Math.PI / 2);
                alt = false;
            }
            sx = Math.Max(0.02, sx);
            Glyphs(env, (i, g, _) =>
            {
                if (i != gi || g == null) return null;
                var o = B(g, "r90") ? new CharXf { Sy = sx } : new CharXf { Sx = sx };
                if (alt)
                {
                    o.Ch = ch.ToString();
                    o.Color = acc;
                }
                return o;
            });
        },
    });

    // ---------------- shadowSway（H.shadowSway；JS ae:'drift'） ----------------

    private static void ShadowSway() => RegH("shadowSway", new MoveDef
    {
        Name = "影が揺れる",
        Tags = new[] { "calm", "emotional" },
        W = 0.5,
        Draw = env =>
        {
            var it = env.It!;
            var k = env.Ap * MotionK(env);
            if (k < 0.01 || it["extrude"] != null || env.Pass != "main") return;
            var t = env.Ltb;
            var seed = SeedOf(it);
            var sz = N0(it, "size");
            var ang = (60 + 45 * Math.Sin(t * J.TAU / 5.5 + seed % 10)) * J.DEG;
            var l = sz * (0.12 + 0.05 * Math.Sin(t * J.TAU / 3.3 + 1)) * Math.Min(1.3, k);
            var bg = ScK(env, "bg") ?? "#000000";
            var col = DarkBg(env) ? MixC(bg, ScK(env, "accent") ?? "#ffffff", 0.42) : MixC(bg, ColOf(it), 0.3);
            it["extrude"] = new JObject { ["n"] = 12, ["dx"] = Math.Cos(ang) * l, ["dy"] = Math.Sin(ang) * l, ["color"] = col, ["fade"] = true, ["a"] = 0.9 };
        },
    });

    // ---------------- magnetJiggle（H.magnetJiggle；JS ae:'jitter'） ----------------

    private static void MagnetJiggle() => RegH("magnetJiggle", new MoveDef
    {
        Name = "磁力",
        Tags = new[] { "pop", "glitch" },
        W = 0.4,
        Draw = env =>
        {
            var it = env.It!;
            var k = env.Ap * MotionK(env);
            if (k < 0.01) return;
            var t = env.Ltb;
            var seed = SeedOf(it);
            var sz = N0(it, "size");
            var step = (int)env.Step;
            double mx, my;
            bool toItem;
            if (IsSingle(env, it))
            {
                var x2 = env.W / 2.0 + Math.Sin(t * 0.9 + env.Cut!.Seed % 9) * env.W * 0.36;
                var y2 = env.H / 2.0 + Math.Sin(t * 1.7 + 1) * env.H * 0.12;
                var l2 = ToL(it, x2, y2);
                mx = l2[0];
                my = l2[1];
                toItem = true;
            }
            else
            {
                var b = LBox(it);
                mx = b.Cx + Math.Sin(t * 0.9 + seed % 9) * b.W * 0.5;
                my = b.Cy + Math.Sin(t * 1.7 + 1) * b.H * 0.9;
                toItem = false;
            }
            Glyphs(env, (i, g, _) =>
            {
                if (g == null) return null;
                var gx = toItem ? 0 : GCx(g, it);
                var gy = toItem ? 0 : GCy(g, it);
                var vx = mx - gx;
                var vy = my - gy;
                var d = Math.Sqrt(vx * vx + vy * vy);
                if (d == 0) d = 1;
                var f = 1 / (1 + Math.Pow(d / (sz * 1.1), 2));
                var pull = sz * 0.2 * f * k;
                var buzz = f > 0.35 ? J.Rs(seed, step, i, 641) * sz * 0.014 * k * f : 0;
                return new CharXf { Dx = vx / d * pull + buzz, Dy = vy / d * pull, Rot = vx / d * 7 * f * k };
            });
        },
    });

    // ---------------- typeRattle（H.typeRattle；JS ae:'jitter'） ----------------

    private static void TypeRattle() => RegH("typeRattle", new MoveDef
    {
        Name = "タイプの震え",
        Tags = new[] { "editorial", "glitch" },
        W = 0.5,
        Draw = env =>
        {
            var it = env.It!;
            var k = env.Ap * MotionK(env);
            if (k < 0.01) return;
            var idx = BeatIdx(env, 0.45);
            var since = BeatSince(env, 0.45);
            var seed = SeedOf(it);
            var sz = N0(it, "size");
            var kk = Math.Min(1, k);
            Glyphs(env, (i, _, _) =>
            {
                var ep = -1;
                for (var b = idx; b > idx - 7; b--)
                    if (J.R(seed, b, i, 651) < 0.3)
                    {
                        ep = b;
                        break;
                    }
                var hit = ep == idx && since < 0.1 ? 1 - since / 0.1 : 0;
                return new CharXf
                {
                    Dy = J.Rs(seed, ep, i, 652) * sz * 0.045 * k + hit * sz * 0.04 * k,
                    Dx = J.Rs(seed, ep, i, 655) * sz * 0.01 * k,
                    Rot = J.Rs(seed, ep, i, 653) * 3.2 * k,
                    A = 1 - 0.2 * J.R(seed, ep, i, 654) * kk - 0.15 * hit * kk,
                };
            });
        },
    });

    // ---------------- focusRack（H.focusRack；JS ae:'breathe'） ----------------

    private static void FocusRack() => RegH("focusRack", new MoveDef
    {
        Name = "ピント送り",
        Tags = new[] { "calm", "emotional" },
        W = 0.5,
        Draw = env =>
        {
            var it = env.It!;
            var k = env.Ap * MotionK(env);
            if (k < 0.01) return;
            var ord = OrderOf(env, it);
            var t = env.Ltb;
            var seed = unchecked((int)(env.Cut?.Seed ?? 0));
            var sz = N0(it, "size");
            var kk = Math.Min(1, k);
            var fpos = 0.5 + 0.62 * Math.Sin(t * J.TAU / 4.6 + seed % 7);
            // 立体文字不做逐字 blur，太贵
            var maxB = env.Pass == "main" && env.AllowFilter && it["extrude"] == null ? Math.Min(sz * 0.065, 18) * kk : 0;
            Glyphs(env, (i, _, n) =>
            {
                var df = J.Clamp((Math.Abs(ord(i, n) - fpos) - 0.1) / 0.5);
                if (df <= 0) return null;
                return new CharXf { Blur = maxB * df * df, A = 1 - 0.28 * df * kk, S = 1 + 0.035 * df * k };
            });
        },
    });

    // ---------------- pluckString（H.pluckString；JS ae:'wave'） ----------------

    private static void PluckString() => RegH("pluckString", new MoveDef
    {
        Name = "弦の振動",
        Tags = new[] { "pop", "emotional" },
        W = 0.4,
        Draw = env =>
        {
            var it = env.It!;
            var k = env.Ap * MotionK(env);
            if (k < 0.01) return;
            double tau;
            // env.beat 分支：C# Env 未挂 beat 对象，走 ltb 回退式
            const double per = 2.4;
            tau = ((env.Ltb - 0.45) % per + per) % per;
            var sz = N0(it, "size");
            var a = sz * 0.18 * k * Math.Exp(-tau * 1.7) * Math.Min(1, tau / 0.04);
            if (a < 0.3) return;
            var vert = B(it, "vertical");
            var single = IsSingle(env, it);
            var nCut = CutN(env);
            var mi = Num(it, "mi");
            var w1 = Math.Cos(tau * J.TAU * 3.2);
            var w2 = Math.Cos(tau * J.TAU * 6.6 + 1);
            Glyphs(env, (_, g, _) =>
            {
                if (g == null) return null;
                var x = single ? (mi + 1) / (nCut + 1) : (D(g, "ci") + 1) / (D(g, "n") + 1);
                var y = a * (Math.Sin(Math.PI * x) * w1 + 0.3 * Math.Sin(J.TAU * x) * w2);
                return vert ? new CharXf { Dx = y } : new CharXf { Dy = y };
            });
        },
    });
}
