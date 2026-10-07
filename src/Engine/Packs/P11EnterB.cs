// 移植自 frontend/src/engine/11p_enterB.js（enterB 手法包：第二批入场，物理/纸张/光/数码/图形类，47 条，pack = "enterB"）。
//
// 契约与 P11Enter（enter 第一批）完全相同：MoveDef.Draw(env) 里 it = env.It、p = env.Ap、
// env.Cut 携带 {dur,inDur,outDur}；逐字动画推 env.CharFns（管线 mainDraw 统一 CombineChar）、
// 逐片动画推 env.PieceFns；条目字段直接写 item（bands/vbands/clipY/streak/echo/shadow/dash/
// fillAlpha/stroke/strokeUnder/pattern…JLayouts.DrawFx / JText.DrawItem 全部消费）。
// 随机数全部是 J.R/J.Rs/J.H 确定性哈希（无顺序流），哈希入参逐字保持（JS J.h 内部 |0 截断 →
// C# 传 int，env.step 这类小数同样截断）——同 seed 同动画轨迹。
//
// 管线缺口与降级（本文件内解决，不改共享文件；约定沿用 P11Enter）：
// 1. it.pre/addPre、it.post/addPost：JLayouts.DrawFx 尚未接（TODO(11p)）。pre 在 Draw 调用点就地
//    执行（JS 画在文字前，此处同序，正确）；post 就地提前到 Draw 内画（内容/几何一致，层级在
//    文字下方；管线接上 post 后回迁）。
// 2. it.clipFn（clipLocal 任意路径裁剪）：管线未接，主文字裁不掉。
//    - 轴对齐单矩形（quarters/invertBox/tokoroten）→ 每字形 clipX/clipY 精确对位（ClipRectLocal）；
//    - 其余非矩形（对角半面/毛边/扫描线/棋盘/笔刷/墨渍/扇形/液面/斜带）→ 按字形中心（或四角）
//      做可见性近似（ClipHideGlyphs，行内注明）。
//    我们自己 drawItem 的副本（copyDraw）不受此限：canvas ClipPath 全保真。
// 3. JS 的 copyDraw 走 J.drawItem 直调；C# 对位 CopyDraw → DrawIt（NoPieces=true 顶替 JS 副本
//    pieceFn:null）。条目克隆用 DeepClone + JText.ShareLay（对位 JS Object.assign 共享 _lay）。
// 4. JS it._m/_lay 失效（scaleXY/scaleAbout/tiltUp 改 size/sx/sy 后置 null）：C# JText.GetLay 按
//    对象缓存、无失效入口——measure 走 JText.Measure（内容键 memo，改 size 后自动重算，Box 不受
//    影响）；直接 GetLay 的渲染路径与 JS 在「改 size 后的同一 item」上有已知差异（P11Enter 同款，
//    引擎级 TODO，本文件不另行处理）。
// 5. `ae`（AE 导出模板名，11_export 消费）MoveDef 无对应属性，未注册——对位见各条目行内注释。
//
// 跨文件符号对位：J.measure→JText.Measure、J.layoutText→JText.LayoutText、J.pool→JLang.Pool、
// J.mainDraw→JLayouts.MainDraw、r/rs/h/mix/lum/rgba(本地 Rgba)/smooth/clamp/lerp→J.*。
// env.st.fonts.mono[0] → JLayouts.Font0(env.St, "mono")（internal，同程序集）。

using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace JizuraLive.Engine;

public static class P11EnterB
{
    private const string P = "enterB";
    private static int _installed;

    private static void Reg0(string key, MoveDef def) => Reg.Register("enter", key, def, P);

    // ================= 本包 helper（JS L10-195 逐条对位） =================

    private static double OCubic(double x) => 1 - Math.Pow(1 - J.Clamp(x), 3);
    private static double OQuart(double x) => 1 - Math.Pow(1 - J.Clamp(x), 4);
    private static double OQuint(double x) => 1 - Math.Pow(1 - J.Clamp(x), 5);

    private static double IoCubic(double x)
    {
        x = J.Clamp(x);
        return x < 0.5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2;
    }

    private static double IoQuart(double x)
    {
        x = J.Clamp(x);
        return x < 0.5 ? 8 * x * x * x * x : 1 - Math.Pow(-2 * x + 2, 4) / 2;
    }

    private static double OBack(double x, double s) => J.E.OutBack(J.Clamp(x), s);
    private static double Sstep(double a, double b, double x) => J.Smooth(a, b, x);

    /// <summary>阻尼弹簧：t=0 时是 1，t=1 时正好到 0，中途会过冲穿过 0（JS spring）。</summary>
    private static double Spring(double t, double k, double f)
    {
        t = J.Clamp(t);
        return Math.Exp(-k * t) * Math.Cos(f * Math.PI * t) * (1 - t * t * t);
    }

    /// <summary>k 是字的 0..1 次序，spread 是入场进度里拿来做错峰的比例。JS stg。</summary>
    private static double Stg(double p, double k, double spread) => J.Clamp((p - spread * k) / (1 - spread));

    private static double OrdLR(int i, int n) => n > 1 ? i / (double)(n - 1) : 0;
    private static double OrdRL(int i, int n) => n > 1 ? 1 - i / (double)(n - 1) : 0;

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

    private static string HotOf(Env env, JObject it)
    {
        var bg = Pick(env.Sc!.Str("bg"));
        return J.Lum(bg) < 0.5 ? "#ffffff" : Pick(env.Sc.Str("accent"), env.Sc.Str("fg"));
    }

    private static bool Blank(JObject g) => S(g, "ch") is " " or "　";
    private static bool Blank(JText.GlyphCell g) => g.Ch is " " or "　";

    /// <summary>主歌词条目判定：排除淡出/描边副本和多条目布局里的单字，装饰图形只挂在它身上。JS isPrimary。</summary>
    private static bool IsPrimary(JObject it) => JAnim.NumNull(it, "alpha", 1) >= 0.85 && FillOn(it) && GlyphN(it) > 1;

    /// <summary>JS isMain：排除淡出/描边副本。</summary>
    private static bool IsMain(JObject it) => JAnim.NumNull(it, "alpha", 1) >= 0.85 && FillOn(it);

    /// <summary>JS it.fill !== false：只有显式 boolean false 才算关。</summary>
    private static bool FillOn(JObject o) =>
        !(o["fill"] is JValue v && v.Type == JTokenType.Boolean && !(bool)v.Value!);

    private static JAnim.CharFn Glyphs(Env env, JAnim.CharFn fn)
    {
        env.CharFns.Add(fn);
        return fn;
    }

    // ---- 条目克隆（JS Object.assign({}, x, STRIP, extra)：浅拷贝 + 剥函数值/副本字段） ----

    private static readonly string[] StripKeys = { "streak", "echo", "bands", "vbands", "wipeBar", "cursorAt" };

    private static JObject Shallow(JObject x)
    {
        var c = (JObject)x.DeepClone();
        JText.ShareLay(c, JText.GetLay(x)); // JS 浅拷贝共享 _lay 的对位
        foreach (var k in StripKeys) c[k] = null;
        return c;
    }

    /// <summary>再画一份条目（JS copyDraw）：fn 顶替 glyph 函数里的 main；extra 并进副本。</summary>
    private static BBox? CopyDraw(Env env, JObject x, JAnim.CharFn? main, JAnim.CharFn? fn, JObject? extra = null)
    {
        var others = env.CharFns.Where(f => !ReferenceEquals(f, main)).ToList();
        var parts = new List<JAnim.CharFn>(others);
        if (fn != null) parts.Add(fn);
        var c = Shallow(x);
        if (extra != null)
            foreach (var kv in extra)
                c[kv.Key] = kv.Value == null ? null : kv.Value.DeepClone();
        return DrawIt(env, c, JAnim.CombineChar(parts));
    }

    /// <summary>JS J.drawItem 直调：临时置换 env.CharFn/PieceFn（NoPieces 对位 JS 副本 pieceFn:null）。</summary>
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

    // ---- 钩子就地执行（it.pre/it.post/clipLocal 管线未挂，见文件头 1/2） ----

    /// <summary>JS addPre 的对位：在 Draw 调用点就地执行——JS 里 pre 画在文字之前，此处同序。</summary>
    private static void DrawPre(Env env, JObject it, Action<Env, JObject> fn) => fn(env, it);

    /// <summary>
    /// JS addPost 的对位：内容就地执行——JS 里画在文字之后，此处提前到 Draw 内
    /// （内容/几何一致，层级在文字下方；管线接上 post 后回迁）。
    /// </summary>
    private static void DrawPost(Env env, JObject it, Action<Env, JObject> fn) => fn(env, it);

    /// <summary>JS postLocal：alpha 门 + save + toLocal + fn + restore。post 内容层级在文字下方（见上）。</summary>
    private static void DrawPostLocal(Env env, JObject it, Action<Env, EBox, JObject, double> fn)
    {
        var a = J.Clamp(JAnim.NumNull(it, "alpha", 1));
        if (a <= 0.01) return;
        var b = Box(it);
        env.Ctx.Save();
        ToLocal(env.Ctx, it);
        try
        {
            fn(env, b, it, a);
        }
        finally
        {
            env.Ctx.Restore();
        }
    }

    /// <summary>JS preLocal：alpha 门 + save + toLocal + fn + restore（画在文字之前，此处同序，层级正确）。</summary>
    private static void DrawPreLocal(Env env, JObject it, Action<Env, EBox, JObject, double> fn)
    {
        var a = J.Clamp(JAnim.NumNull(it, "alpha", 1));
        if (a <= 0.01) return;
        var b = Box(it);
        env.Ctx.Save();
        ToLocal(env.Ctx, it);
        try
        {
            fn(env, b, it, a);
        }
        finally
        {
            env.Ctx.Restore();
        }
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

    // ---- 条目几何 ----

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

    /// <summary>按条目本地（已旋转）坐标系平移。JS moveLocal。</summary>
    private static void MoveLocal(JObject it, double dx, double dy)
    {
        var r = N0(it, "rot") * J.DEG;
        var c = Math.Cos(r);
        var s = Math.Sin(r);
        it["x"] = N0(it, "x") + c * dx - s * dy;
        it["y"] = N0(it, "y") + s * dx + c * dy;
    }

    /// <summary>以条目自身盒子中心为基准缩放 x/y。JS scaleXY（_m/_lay 失效见文件头 4）。</summary>
    private static void ScaleXY(JObject it, double kx, double ky, EBox? b = null)
    {
        b ??= Box(it);
        it["sx"] = N1(it, "sx") * kx;
        it["sy"] = N1(it, "sy") * ky;
        MoveLocal(it, -b.Cx * (kx - 1), -b.Cy * (ky - 1));
    }

    /// <summary>以条目自身盒子中心缩放（有些布局把条目锚在左/上）。JS scaleAbout。</summary>
    private static void ScaleAbout(JObject it, double k, EBox? b = null)
    {
        if (k == 1) return;
        b ??= Box(it);
        it["size"] = N0(it, "size") * k;
        MoveLocal(it, -b.Cx * (k - 1), -b.Cy * (k - 1));
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

    private static double[] GlyphPos(JObject g, double isx, double isy) =>
        new[] { (JLayouts.D(g, "x") + JLayouts.D(g, "vx")) * isx, (JLayouts.D(g, "y") + JLayouts.D(g, "vy")) * isy };

    private static double[] GlyphPos(JText.GlyphCell g, double isx, double isy) =>
        new[] { (g.X + g.Vx) * isx, (g.Y + g.Vy) * isy };

    /// <summary>每行的局部范围 [{li, u0, u1, v, n}]：u 沿阅读方向，v 是行在横向的中心。JS lines。</summary>
    private sealed class LineRng
    {
        public int Li, N, First, Last;
        public double U0, U1, V;
    }

    private static List<LineRng> LinesOf(JObject it, EBox b)
    {
        var vert = B(it, "vertical");
        var isx = N1(it, "sx");
        var isy = N1(it, "sy");
        var map = new Dictionary<int, LineRng>();
        var order = new List<LineRng>();
        foreach (var g in b.Lay.Cells)
        {
            if (Blank(g)) continue;
            var gp = GlyphPos(g, isx, isy);
            var hu = (vert ? g.H * isy : g.W * isx) / 2;
            var u = vert ? gp[1] : gp[0];
            var v = vert ? g.X * isx : g.Y * isy;
            if (!map.TryGetValue(g.Li, out var L))
            {
                L = new LineRng { Li = g.Li, U0 = 1e9, U1 = -1e9, V = v, First = g.I, Last = g.I };
                map[g.Li] = L;
                order.Add(L);
            }
            L.U0 = Math.Min(L.U0, u - hu);
            L.U1 = Math.Max(L.U1, u + hu);
            L.N++;
            L.Last = g.I;
        }
        return order;
    }

    /// <summary>JS pt：局部 (u 沿阅读方向, v 横向) → [x, y]。</summary>
    private static double[] Pt(bool vert, double u, double v) => vert ? new[] { v, u } : new[] { u, v };

    private static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);

    private static double ModTau(double x) => x - Math.Floor(x / J.TAU) * J.TAU;

    // ---- clipLocal 的 C# 对位 ----

    /// <summary>
    /// it.clipFn 的矩形对位：管线未接任意路径裁剪（JLayouts.DrawFx TODO(11p)），把局部空间
    /// 轴对齐矩形 (rx0,ry0,rx1,ry1) 折算成每字形 clipX/clipY 推一个额外 charFn（精确对位，
    /// 与 P11Enter 同款）。返回推入的 fn——JS 里 clipFn 不进 charFns，copyDraw(main:null) 的
    /// 副本不带裁剪；C# 侧这类副本把它当 main 传给 CopyDraw 以排除。
    /// </summary>
    private static JAnim.CharFn ClipRectLocal(Env env, JObject it, double isx, double isy, double rx0, double ry0, double rx1, double ry1)
    {
        JAnim.CharFn fn = (_, g, _) =>
        {
            if (g == null) return null;
            return RectGlyphClip(g, isx, isy, rx0, ry0, rx1, ry1);
        };
        env.CharFns.Add(fn);
        return fn;
    }

    private static CharXf RectGlyphClip(JObject g, double isx, double isy, double rx0, double ry0, double rx1, double ry1)
    {
        var gx = (JLayouts.D(g, "x") + JLayouts.D(g, "vx")) * isx;
        var gy = (JLayouts.D(g, "y") + JLayouts.D(g, "vy")) * isy;
        var hx = Math.Max(1e-6, JLayouts.D(g, "w") * isx / 2);
        var hy = Math.Max(1e-6, JLayouts.D(g, "h") * isy / 2);
        if (JLayouts.B(g, "r90")) // 字形本体带 +90° 旋转：裁剪框换轴
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

    /// <summary>
    /// 非矩形遮罩的字形粒度可见性近似：visFn 返回 false 时该字形隐藏（遮罩边缘按字形粒度离散）。
    /// 返回推入的 fn（copyDraw 副本排除用，理由同 ClipRectLocal）。
    /// </summary>
    private static JAnim.CharFn ClipHideGlyphs(Env env, Func<JObject, bool> visFn)
    {
        JAnim.CharFn fn = (_, g, _) =>
        {
            if (g == null) return null;
            return visFn(g) ? null : new CharXf { Hide = true };
        };
        env.CharFns.Add(fn);
        return fn;
    }

    /// <summary>
    /// 副本绘制用的 canvas 裁剪（我们自己 drawItem 的 copy，不受主文字裁剪管线缺口影响，全保真）：
    /// JS「save → beginPath → save → toLocal → 路径 → restore → clip → 画 → restore」的同构。
    /// </summary>
    private static void ClipPathDraw(Env env, JObject localSpaceOf, Action<SKCanvas, SKPath> buildPath, Action draw)
    {
        var ctx = env.Ctx;
        ctx.Save();
        using var path = new SKPath();
        ctx.Save();
        ToLocal(ctx, localSpaceOf);
        buildPath(ctx, path);
        ctx.Restore();
        ctx.ClipPath(path);
        try
        {
            draw();
        }
        finally
        {
            ctx.Restore();
        }
    }

    /// <summary>JS ctx.createRadialGradient + fillRect 的对位（径向渐变矩形；r<=0 跳过）。</summary>
    private static void RadialRect(Env env, double cx, double cy, double r, SKColor c0, SKColor c1, SKBlendMode blend)
    {
        if (r <= 0) return;
        using var shader = SKShader.CreateRadialGradient(new SKPoint((float)cx, (float)cy), (float)r,
            new[] { c0, c1 }, new[] { 0f, 1f }, SKShaderTileMode.Clamp);
        using var p = new SKPaint { IsAntialias = true, Color = SKColors.White, Shader = shader, BlendMode = blend };
        env.Ctx.DrawRect(new SKRect((float)(cx - r), (float)(cy - r), (float)(cx + r), (float)(cy + r)), p);
    }

    /// <summary>J.rgba(h, a)：hex + alpha → "rgba(r,g,b,a)"（shadow.color 吃这个格式）。</summary>
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

    /// <summary>JS it.xxx || 1 语义：缺省/非数值回 1（供条目读 sx/sy 等缩放）。</summary>
    private static double N1(JObject it, string k)
    {
        var v = it?[k];
        return v != null && (v.Type == JTokenType.Float || v.Type == JTokenType.Integer) ? (double)v : 1;
    }

    private static int SeedOf(JObject o) => JAnim.SeedOf(o);

    /// <summary>Sutherland–Hodgman 裁剪：保留 f(v) >= 0（f 线性）的那部分多边形。JS clipHalf。</summary>
    private static List<double[]> ClipHalf(double[][] poly, Func<double[], double> f)
    {
        var @out = new List<double[]>();
        for (var k = 0; k < poly.Length; k++)
        {
            var a = poly[k];
            var b = poly[(k + 1) % poly.Length];
            var fa = f(a);
            var fb = f(b);
            if (fa >= 0) @out.Add(a);
            if (fa >= 0 != fb >= 0)
            {
                var t = fa / (fa - fb);
                @out.Add(new[] { a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t });
            }
        }
        return @out;
    }

    /// <summary>JS BAYER 4×4 有序抖动表。</summary>
    private static readonly int[] Bayer = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };

    // ================= 注册（JS DEFS 字面量序，Object.keys 语义） =================

    /// <summary>幂等注册本文件全部 enter 条目（顺序 = JS 源 DEFS 字面量序；不挂静态构造函数）。</summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;
        SpringIn();       // JS L199
        Pendulum();       // JS L221
        RollIn();         // JS L256
        Slingshot();      // JS L274
        RockSettle();     // JS L314
        BounceBall();     // JS L341
        SnapRail();       // JS L400
        FanOpen();        // JS L459
        Cylinder();       // JS L483
        Shuffle();        // JS L513
        StopMotion();     // JS L556
        Ripple();         // JS L579
        Zipper();         // JS L611
        ZoomAlt();        // JS L646
        TiltUp();         // JS L664
        StickerPeel();    // JS L687
        Crumple();        // JS L762
        NoteUnfold();     // JS L816
        TornJoin();       // JS L871
        SplitFlap();      // JS L944
        Overexpose();     // JS L1013
        Glint();          // JS L1040
        Loupe();          // JS L1113
        FilmFeed();       // JS L1152
        Backlight();      // JS L1181
        LightLeak();      // JS L1212
        HeatHaze();       // JS L1255
        CrtOn();          // JS L1277
        Interlace();      // JS L1302
        LoadingBar();     // JS L1334
        Dither();         // JS L1398
        Odometer();       // JS L1431
        MatrixRain();     // JS L1475
        HatchFill();      // JS L1511
        BrushReveal();    // JS L1547
        InkDrop();        // JS L1591
        Quarters();       // JS L1641
        InvertBox();      // JS L1685
        PrintRegister();  // JS L1730
        EchoCount();      // JS L1768
        LiquidFill();     // JS L1790
        WindBlown();      // JS L1841
        StrokeOrder();    // JS L1877
        ClockWipe();      // JS L1909
        ShadowFirst();    // JS L1962
        Bubbles();        // JS L2002
        Tokoroten();      // JS L2042
    }

    // ---------------- springIn（JS L199） ----------------

    private static void SpringIn() => Reg0("springIn", new MoveDef
    {
        Name = "バネ", // JS: ae: 'drop'（MoveDef 暂无该属性，未注册）
        Tags = new[] { "pop" },
        W = 0.9,
        MinDur = 0.5,
        InDur = (dur, _) => J.Clamp(dur * 0.45, 0.2, 0.8),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var vert = B(it, "vertical");
            var size = N0(it, "size");
            var Dv = size * 1.7;
            Glyphs(env, (i, _, n) =>
            {
                var q = Stg(p, OrdLR(i, n), 0.45);
                if (q <= 0) return new CharXf { Hide = true };
                if (q >= 1) return null;
                var f = Spring(q, 4.2, 3);
                var v = (Spring(q + 0.01, 4.2, 3) - f) / 0.01;
                var st = 1 + Math.Min(0.5, Math.Abs(v) * 0.075);
                var cp = 1 / Math.Sqrt(st);
                return vert
                    ? new CharXf { Dx = -f * Dv, Sx = st, Sy = cp, A = J.Clamp(q * 6) }
                    : new CharXf { Dy = f * Dv, Sy = st, Sx = cp, A = J.Clamp(q * 6) };
            });
        },
    });

    // ---------------- pendulum（JS L221） ----------------

    private static void Pendulum() => Reg0("pendulum", new MoveDef
    {
        Name = "振り子", // JS: ae: 'spin'
        Tags = new[] { "pop", "emotional", "calm" },
        W = 0.9,
        MinDur = 0.5,
        InDur = (dur, _) => J.Clamp(dur * 0.5, 0.22, 0.9),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var isx = N1(it, "sx");
            var isy = N1(it, "sy");
            var dir = DirOf(env, 41);
            var size = N0(it, "size");

            double Qf(int i, int n) => Stg(p, OrdLR(i, n), 0.45);
            double Ang(double q) => dir * 62 * Spring(q, 3.1, 2.4);
            double Len(JObject g) => JLayouts.D(g, "h") * isy * 0.5 + size * 0.45;
            double LenC(JText.GlyphCell g) => g.H * isy * 0.5 + size * 0.45;
            Glyphs(env, (i, g, n) =>
            {
                var q = Qf(i, n);
                if (q <= 0) return new CharXf { Hide = true };
                if (q >= 1) return null;
                var th = Ang(q);
                var r = th * J.DEG;
                var l = g == null ? size : Len(g);
                return new CharXf { Dx = -l * Math.Sin(r), Dy = l * Math.Cos(r) - l, Rot = th, A = J.Clamp(q * 5) };
            });
            if (!IsMain(it)) return;
            // JS postLocal（吊绳 + 钉子）——就地执行（层级在文字下）
            DrawPostLocal(env, it, (env2, b, x, A) =>
            {
                var lw = Math.Max(1.2, size * 0.012);
                var n = b.Lay.N;
                foreach (var g in b.Lay.Cells)
                {
                    if (Blank(g)) continue;
                    var q = Qf(g.I, n);
                    if (q <= 0 || q >= 1) continue;
                    var a = A * (1 - Sstep(0.45, 0.9, q)) * J.Clamp(q * 5);
                    if (a <= 0.01) continue;
                    var gp = GlyphPos(g, isx, isy);
                    var l = LenC(g);
                    var r = Ang(q) * J.DEG;
                    var px = gp[0];
                    var py = gp[1] - l;
                    var tx = px - Math.Sin(r) * (l - g.H * isy * 0.5);
                    var ty = py + Math.Cos(r) * (l - g.H * isy * 0.5);
                    env2.Line(new[] { new[] { px, py }, new[] { tx, ty } }, env2.Sc!.Str("sub"), lw, a * 0.8, false);
                    env2.Circle(px, py, Math.Max(2, size * 0.035), env2.Sc.Str("accent"), null, 1, a, false);
                }
            });
        },
    });

    // ---------------- rollIn（JS L256） ----------------

    private static void RollIn() => Reg0("rollIn", new MoveDef
    {
        Name = "転がり", // JS: ae: 'spin'
        Tags = new[] { "pop", "graphic" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var vert = B(it, "vertical");
            var isx = N1(it, "sx");
            var isy = N1(it, "sy");
            var size = N0(it, "size");
            var dir = DirOf(env, 43);
            var Dv = size * 2.3 * (0.75 + 0.4 * env.Fx.Motion);
            Glyphs(env, (i, g, n) =>
            {
                // 队首的字先出发，轮子才不会互相碾过去
                var q = Stg(p, dir < 0 ? OrdRL(i, n) : OrdLR(i, n), 0.22);
                if (q <= 0) return new CharXf { Hide = true };
                if (q >= 1) return null;
                var gw = g == null ? size : JLayouts.D(g, "w") * isx;
                var gh = g == null ? size : JLayouts.D(g, "h") * isy;
                var off = dir * Dv * (1 - OBack(q, 1.25));
                var r = Math.Max(4, Math.Min(gw, gh) * 0.5);
                var rot = -off / r / J.DEG;
                var k = J.Lerp(0.72, 1, OCubic(q));
                return vert
                    ? new CharXf { Dy = off, Rot = -rot, S = k, A = J.Clamp(q * 5) }
                    : new CharXf { Dx = off, Rot = rot, S = k, A = J.Clamp(q * 5) };
            });
        },
    });

    // ---------------- slingshot（JS L274） ----------------

    private static void Slingshot() => Reg0("slingshot", new MoveDef
    {
        Name = "パチンコ", // JS: ae: 'stretch'
        Tags = new[] { "pop", "graphic" },
        W = 0.7,
        MinDur = 0.55,
        InDur = (dur, _) => J.Clamp(dur * 0.5, 0.25, 0.9),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var vert = B(it, "vertical");
            var b = Box(it);
            var size = N0(it, "size");
            const double tA = 0.36;
            var pull = size * 0.7 + (vert ? b.W : b.H) * 0.35;
            double f, along = 1, across = 1, a = 1, band = 1;
            if (p < tA)
            {
                var t = p / tA;
                f = J.Lerp(0.2, 1, J.E.OutCubic(t));
                a = J.Clamp(t * 3);
                along = 1 - 0.14 * J.E.OutCubic(t);
                across = 1 + 0.07 * J.E.OutCubic(t);
            }
            else
            {
                var t = (p - tA) / (1 - tA);
                var v = (Spring(t + 0.01, 4.6, 2.6) - Spring(t, 4.6, 2.6)) / 0.01;
                f = Spring(t, 4.6, 2.6);
                along = 1 + Math.Min(0.4, Math.Abs(v) * 0.05);
                across = 1 / Math.Sqrt(along);
                band = 1 - Sstep(0.03, 0.2, t);
                if (t > 0.9 && Math.Abs(f) < 0.002) return;
            }
            var R0 = DRange(it, b);
            var prim = IsPrimary(it);
            if (vert) ScaleXY(it, along, across, b);
            else ScaleXY(it, across, along, b);
            if (vert) it["x"] = N0(it, "x") + f * pull;
            else it["y"] = N0(it, "y") + f * pull;
            it["alpha"] = NN(it, "alpha", 1) * a;
            if (band <= 0.01 || !prim) return;
            // JS addPost（两根皮筋与立柱）——就地执行（层级在文字下）
            DrawPost(env, it, (env2, x) =>
            {
                var R = DRange(x, Box(x));
                var m = size * 0.35;
                var lw = Math.Max(1.5, size * 0.03);
                var col = env2.Sc!.Str("accent");
                var A = band * J.Clamp(JAnim.NumNull(x, "alpha", 1));
                double[][] posts = vert
                    ? new[] { new[] { (R0.X0 + R0.X1) / 2, R0.Y0 - m }, new[] { (R0.X0 + R0.X1) / 2, R0.Y1 + m } }
                    : new[] { new[] { R0.X0 - m, (R0.Y0 + R0.Y1) / 2 }, new[] { R0.X1 + m, (R0.Y0 + R0.Y1) / 2 } };
                double[][] ends = vert
                    ? new[] { new[] { R.X1, R.Y0 + (R.Y1 - R.Y0) * 0.15 }, new[] { R.X1, R.Y1 - (R.Y1 - R.Y0) * 0.15 } }
                    : new[] { new[] { R.X0 + (R.X1 - R.X0) * 0.12, R.Y1 }, new[] { R.X1 - (R.X1 - R.X0) * 0.12, R.Y1 } };
                for (var k = 0; k < 2; k++)
                {
                    env2.Line(new[] { posts[k], ends[k] }, col, lw, A, false);
                    env2.Circle(posts[k][0], posts[k][1], lw * 1.6, col, null, 1, A, false);
                }
            });
        },
    });

    // ---------------- rockSettle（JS L314） ----------------

    private static void RockSettle() => Reg0("rockSettle", new MoveDef
    {
        Name = "ぐらぐら着地", // JS: ae: 'drop'
        Tags = new[] { "pop" },
        W = 0.8,
        MinDur = 0.55,
        InDur = (dur, _) => J.Clamp(dur * 0.5, 0.22, 0.9),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var isx = N1(it, "sx");
            var isy = N1(it, "sy");
            var seed = SeedOf(it);
            const double tL = 0.3;
            Glyphs(env, (i, g, n) =>
            {
                var q = Stg(p, OrdLR(i, n), 0.4);
                if (q <= 0) return new CharXf { Hide = true };
                if (q >= 1) return null;
                var gw = g == null ? 0 : JLayouts.D(g, "w") * isx / 2;
                var gh = g == null ? 0 : JLayouts.D(g, "h") * isy / 2;
                var th0 = (J.R(seed, i, 45) < 0.5 ? -1 : 1) * (16 + 10 * J.R(seed, i, 46));
                double th, dy = 0;
                if (q < tL)
                {
                    var t = q / tL;
                    th = th0;
                    dy = -(1 - t * t) * gh * 4;
                }
                else
                {
                    var t = (q - tL) / (1 - tL);
                    th = th0 * Math.Pow(1 - t, 1.5) * Math.Cos(Math.PI * (1.4 * t + 1.9 * t * t));
                }
                var r = th * J.DEG;
                var c = Math.Cos(r);
                var s = Math.Sin(r);
                var cx = th >= 0 ? gw : -gw;
                var cy = gh;
                return new CharXf { Dx = cx - (cx * c - cy * s), Dy = dy + cy - (cx * s + cy * c), Rot = th, A = J.Clamp(q * 8) };
            });
        },
    });

    // ---------------- bounceBall（JS L341） ----------------

    private static void BounceBall() => Reg0("bounceBall", new MoveDef
    {
        Name = "バウンドボール", // JS: ae: 'pop'
        Tags = new[] { "pop", "emotional" },
        W = 0.8,
        MinDur = 0.8,
        MaxChars = 20,
        InDur = (dur, n) => J.Clamp(0.3 + n * 0.06, 0.45, Math.Max(0.45, Math.Min(1.25, dur * 0.6))),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var vert = B(it, "vertical");
            var isx = N1(it, "sx");
            var isy = N1(it, "sy");
            var size = N0(it, "size");
            var lay = JText.LayoutText(it);
            var gl = lay.Cells.Where(g => !Blank(g)).ToList();
            var n = gl.Count;
            if (n == 0) return;
            double Hit(int k) => n > 1 ? J.Lerp(0.16, 0.76, k / (double)(n - 1)) : 0.45;
            var hitOf = new Dictionary<int, double>();
            for (var k = 0; k < n; k++) hitOf[gl[k].I] = Hit(k);
            Glyphs(env, (i, g, _) =>
            {
                if (!hitOf.TryGetValue(i, out var h)) return null;
                if (p < h) return new CharXf { Hide = true };
                var u = (p - h) / 0.2;
                if (u >= 1) return null;
                var sq = J.Lerp(0.55, 1, OBack(u, 2.4));
                var gw = g == null ? size : JLayouts.D(g, "w") * isx;
                var gh = g == null ? size : JLayouts.D(g, "h") * isy;
                var gs = vert ? gw : gh;
                return vert
                    ? new CharXf { Sx = sq, Sy = 1 + 0.3 * (1 - OQuint(u)), Dx = (1 - sq) * gs / 2 }
                    : new CharXf { Sy = sq, Sx = 1 + 0.3 * (1 - OQuint(u)), Dy = (1 - sq) * gs / 2 };
            });
            var R = Math.Max(3, size * 0.13);
            var solo = n == 1;
            // 每个字的接触点（横排顶边，竖排右边）和「上方」方向
            double[] Contact(JText.GlyphCell g)
            {
                var gp = GlyphPos(g, isx, isy);
                return vert
                    ? new[] { gp[0] + g.W * isx * 0.5 + R, gp[1] }
                    : new[] { gp[0], gp[1] - g.H * isy * 0.5 - R };
            }
            var up = vert ? new[] { 1.0, 0.0 } : new[] { 0.0, -1.0 };
            var fw = vert ? new[] { 0.0, 1.0 } : new[] { 1.0, 0.0 };
            var H = size * 0.6;
            double[] p0, p1;
            double s, alpha = 1;
            var c0 = Contact(gl[0]);
            var cl = Contact(gl[n - 1]);
            if (p < Hit(0))
            {
                var back = solo ? 0 : 1.3;
                var t0 = solo ? Hit(0) * 0.2 : 0;
                p0 = solo ? c0 : new[] { c0[0] - fw[0] * size * back + up[0] * size * 0.5, c0[1] - fw[1] * size * back + up[1] * size * 0.5 };
                p1 = c0;
                s = (p - t0) / (Hit(0) - t0);
                alpha = J.Clamp(s * 6);
                if (s <= 0) return;
            }
            else if (p >= Hit(n - 1))
            {
                p0 = cl;
                p1 = new[] { cl[0] + fw[0] * size * 1.4 - up[0] * size * 0.2, cl[1] + fw[1] * size * 1.4 - up[1] * size * 0.2 };
                s = (p - Hit(n - 1)) / 0.2;
                alpha = 1 - Sstep(0.4, 1, s);
            }
            else
            {
                var k = 1;
                while (k < n - 1 && p >= Hit(k)) k++;
                p0 = Contact(gl[k - 1]);
                p1 = Contact(gl[k]);
                s = (p - Hit(k - 1)) / (Hit(k) - Hit(k - 1));
            }
            if (alpha <= 0.01 || s > 1 || !IsMain(it)) return;
            var arc = solo && p < Hit(0) ? size * 0.85 * (1 - s * s) : 4 * H * s * (1 - s);
            var bx = J.Lerp(p0[0], p1[0], s) + up[0] * arc;
            var by = J.Lerp(p0[1], p1[1], s) + up[1] * arc;
            // JS postLocal（accent 小球）——就地执行（层级在文字下）
            DrawPostLocal(env, it, (env2, _, _, A) => env2.Circle(bx, by, R, env2.Sc!.Str("accent"), null, 1, A * alpha, false));
        },
    });

    // ---------------- snapRail（JS L400） ----------------

    private static void SnapRail() => Reg0("snapRail", new MoveDef
    {
        Name = "スナップ整列", // JS: ae: 'assemble'
        Tags = new[] { "graphic", "pop", "editorial" },
        W = 0.9,
        MinDur = 0.55,
        InDur = (dur, _) => J.Clamp(dur * 0.5, 0.25, 0.9),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var vert = B(it, "vertical");
            var size = N0(it, "size");
            var seed = SeedOf(it);
            Glyphs(env, (i, _, _) =>
            {
                var d = J.R(seed, i, 53) * 0.2;
                var a = J.Clamp((p - d) / 0.2);
                if (a <= 0) return new CharXf { Hide = true };
                var ts = 0.46 + J.R(seed, i, 54) * 0.06;
                var sgn = J.R(seed, i, 51) < 0.5 ? -1 : 1;
                var off0 = sgn * size * (0.3 + 0.6 * J.R(seed, i, 52));
                var rot0 = J.Rs(seed, i, 55) * 16;
                double off, rot, sq = 1;
                if (p < ts)
                {
                    var k = 1 - 0.14 * (p / ts);
                    off = off0 * k;
                    rot = rot0 * k;
                }
                else
                {
                    var t = (p - ts) / 0.13;
                    if (t < 1)
                    {
                        var e = J.E.InCubic(t);
                        off = off0 * 0.86 * (1 - e);
                        rot = rot0 * 0.86 * (1 - e);
                    }
                    else
                    {
                        var u = J.Clamp((t - 1) / 1.6);
                        if (u >= 1) return null;
                        off = -sgn * size * 0.06 * Math.Sin(Math.PI * u) * (1 - u);
                        rot = 0;
                        sq = 1 - 0.16 * Math.Sin(Math.PI * J.Clamp(u * 2)) * (1 - u);
                    }
                }
                var o = new CharXf { A = a, Rot = rot };
                if (vert)
                {
                    o.Dx = off;
                    o.Sx = sq;
                    o.Sy = 1 / Math.Sqrt(sq);
                }
                else
                {
                    o.Dy = off;
                    o.Sy = sq;
                    o.Sx = 1 / Math.Sqrt(sq);
                }
                return o;
            });
            if (!IsPrimary(it)) return;
            // JS postLocal（accent 导轨）——就地执行（层级在文字下）
            DrawPostLocal(env, it, (env2, b, x, A) =>
            {
                var grow = OQuart(p / 0.4);
                var flash = Math.Exp(-Math.Pow((p - 0.6) / 0.05, 2));
                var gone = Sstep(0.68, 0.95, p);
                if (grow <= 0 || gone >= 1) return;
                var t = Math.Max(2, size * 0.028) * (1 + flash * 1.2);
                var isx = N1(x, "sx");
                var isy = N1(x, "sy");
                foreach (var L in LinesOf(x, b))
                {
                    var half = (L.U1 - L.U0) / 2 + size * 0.15;
                    var c = (L.U0 + L.U1) / 2;
                    var len = half * grow * (1 - gone);
                    var v = L.V + (vert ? -1 : 1) * size * 0.62 * (vert ? isx : isy);
                    if (vert) env2.Rect(v - t / 2, c - len, t, len * 2, env2.Sc!.Str("accent"), A, false);
                    else env2.Rect(c - len, v - t / 2, len * 2, t, env2.Sc!.Str("accent"), A, false);
                }
            });
        },
    });

    // ---------------- fanOpen（JS L459） ----------------

    private static void FanOpen() => Reg0("fanOpen", new MoveDef
    {
        Name = "扇開き", // JS: ae: 'spin'
        Tags = new[] { "emotional", "graphic", "calm" },
        W = 0.9,
        InDur = (dur, _) => J.Clamp(dur * 0.45, 0.2, 0.8),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var vert = B(it, "vertical");
            var isx = N1(it, "sx");
            var isy = N1(it, "sy");
            var size = N0(it, "size");
            var b = Box(it);
            // 支点在行首下方（竖排列顶端的左侧）：合拢时字沿一根扇骨按顺序排好
            var R0 = size * 0.8 + (vert ? b.W : b.H) / 2;
            var pv = vert ? new[] { b.X0 - R0, b.Y0 + size * 0.5 } : new[] { b.X0 + size * 0.5, b.Y1 + R0 };
            var aMin = 1e9;
            foreach (var g in b.Lay.Cells)
            {
                var gp = GlyphPos(g, isx, isy);
                aMin = Math.Min(aMin, Math.Atan2(gp[1] - pv[1], gp[0] - pv[0]));
            }
            var a0 = aMin - 0.45;
            var e = OBack(p, 1.15);
            Glyphs(env, (i, g, _) =>
            {
                if (g == null) return null;
                var gp = GlyphPos(g, isx, isy);
                var vx = gp[0] - pv[0];
                var vy = gp[1] - pv[1];
                var d = (a0 - Math.Atan2(vy, vx)) * (1 - e);
                var c = Math.Cos(d);
                var s = Math.Sin(d);
                var nx = c * vx - s * vy;
                var ny = s * vx + c * vy;
                return new CharXf { Dx = nx - vx, Dy = ny - vy, Rot = d / J.DEG, A = J.Clamp(p * 3.5) };
            });
        },
    });

    // ---------------- cylinder（JS L483） ----------------

    private static void Cylinder() => Reg0("cylinder", new MoveDef
    {
        Name = "円筒回転", // JS: ae: 'spin'
        Tags = new[] { "graphic", "pop", "editorial" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var vert = B(it, "vertical");
            var isx = N1(it, "sx");
            var isy = N1(it, "sy");
            var size = N0(it, "size");
            var b = Box(it);
            var U0 = vert ? b.Y0 : b.X0;
            var U1 = vert ? b.Y1 : b.X1;
            var uc = (U0 + U1) / 2;
            var R = Math.Max(size * 0.6, (U1 - U0) / 2) / 1.2;
            var dir = DirOf(env, 61);
            var phi = dir * (1 - OQuart(p)) * 2.4;
            var flat = Sstep(0.5, 1, p);
            var bg = Pick(env.Sc!.Str("bg"));
            var col = ColOf(it);
            var A = J.Clamp(p * 6);
            Glyphs(env, (i, g, _) =>
            {
                if (g == null) return null;
                var gp = GlyphPos(g, isx, isy);
                var u = (vert ? gp[1] : gp[0]) - uc;
                var th = u / R + phi;
                var cu = Math.Cos(th);
                if (cu <= 0.04 && flat < 0.5) return new CharXf { Hide = true };
                var nu = J.Lerp(R * Math.Sin(th), u, flat);
                var k = J.Lerp(Math.Max(0.04, cu), 1, flat);
                var o = new CharXf
                {
                    A = A * J.Lerp(J.Clamp(cu * 1.6), 1, flat),
                    Color = J.Mix(col, bg, (1 - J.Lerp(J.Clamp(cu), 1, flat)) * 0.6),
                };
                if (vert)
                {
                    o.Dy = nu - u;
                    o.Sy = k;
                }
                else
                {
                    o.Dx = nu - u;
                    o.Sx = k;
                }
                return o;
            });
        },
    });

    // ---------------- shuffle（JS L513） ----------------

    private static void Shuffle() => Reg0("shuffle", new MoveDef
    {
        Name = "シャッフル", // JS: ae: 'scramble'
        Tags = new[] { "pop", "glitch", "graphic" },
        W = 0.9,
        MinDur = 0.55,
        InDur = (dur, _) => J.Clamp(dur * 0.5, 0.25, 0.9),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var vert = B(it, "vertical");
            var size = N0(it, "size");
            var seed = SeedOf(it);
            // JS addPre 的置换计算（S.lay/S.perm）：就地执行，charFn 在渲染期读
            var holder = new ShuffleState();
            {
                var lay = JText.LayoutText(it);
                var ids = new List<int>();
                foreach (var g in lay.Cells)
                    if (!Blank(g)) ids.Add(g.I);
                var n = ids.Count;
                var arr = new List<int>(ids);
                for (var k = n - 1; k > 0; k--)
                {
                    var j = (int)Math.Floor(J.R(seed, k, 71) * (k + 1));
                    (arr[k], arr[j]) = (arr[j], arr[k]);
                }
                if (n > 1 && arr.Where((v, k) => v != ids[k]).Count() == 0)
                {
                    var first = arr[0];
                    arr.RemoveAt(0);
                    arr.Add(first);
                }
                var perm = new Dictionary<int, int>();
                for (var k = 0; k < n; k++) perm[ids[k]] = arr[k];
                holder.Lay = lay;
                holder.Perm = perm;
            }
            var A = J.Clamp(p / 0.12);
            var isx = N1(it, "sx");
            var isy = N1(it, "sy");
            Glyphs(env, (i, g, _) =>
            {
                if (g == null || holder.Lay == null || holder.Perm == null) return null;
                var ci = Math.Min(holder.Perm.GetValueOrDefault(i, i), holder.Lay.Cells.Count - 1);
                if (ci < 0) return null;
                var src = holder.Lay.Cells[ci];
                var q = Stg(J.Clamp((p - 0.16) / 0.84), J.R(seed, i, 72), 0.3);
                if (q >= 1) return null;
                var sgp = GlyphPos(src, isx, isy);
                var tgp = GlyphPos(g, isx, isy);
                var ex = sgp[0] - tgp[0];
                var ey = sgp[1] - tgp[1];
                var d = Hypot(ex, ey);
                var e = IoCubic(q);
                if (d < 0.5) return new CharXf { A = A, S = J.Lerp(0.8, 1, OBack(q, 2)) };
                var sgn = (vert ? ey : ex) > 0 ? 1 : -1;
                var h = Math.Min(size * 0.85, d * 0.4) * Math.Sin(Math.PI * e);
                var nx = -ey / d * sgn;
                var ny = ex / d * sgn; // 垂直方向：前进时弧线拱到「上面」
                return new CharXf
                {
                    Dx = ex * (1 - e) + nx * h * (vert ? -1 : 1),
                    Dy = ey * (1 - e) + ny * h * (vert ? -1 : 1),
                    S = 1 + sgn * 0.14 * Math.Sin(Math.PI * e),
                    A = A,
                };
            });
        },
    });

    private sealed class ShuffleState
    {
        public JText.TextLayout? Lay;
        public Dictionary<int, int>? Perm;
    }

    // ---------------- stopMotion（JS L556） ----------------

    private static void StopMotion() => Reg0("stopMotion", new MoveDef
    {
        Name = "コマ撮り", // JS: ae: 'pop'
        Tags = new[] { "pop", "emotional" },
        W = 0.8,
        MinDur = 0.5,
        InDur = (dur, _) => J.Clamp(dur * 0.45, 0.25, 0.8),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var size = N0(it, "size");
            var seed = SeedOf(it);
            const int K = 5;
            Glyphs(env, (i, _, n) =>
            {
                var q = Stg(p, J.R(seed, i, 81) * 0.5 + OrdLR(i, n) * 0.5, 0.45);
                if (q <= 0) return new CharXf { Hide = true };
                var k = Math.Min(K - 1, (int)Math.Floor(q * K));
                if (k >= K - 1) return null;
                var f = 1 - OCubic(k / (double)(K - 1));
                var ang = J.R(seed, i, 82) * J.TAU;
                var Dv = size * (0.7 + 0.5 * J.R(seed, i, 83));
                return new CharXf
                {
                    Dx = Math.Cos(ang) * Dv * f + J.Rs(seed, i, k, 84) * size * 0.05,
                    Dy = Math.Sin(ang) * Dv * f + J.Rs(seed, i, k, 85) * size * 0.05,
                    Rot = J.Rs(seed, i, k, 86) * 22 * f + J.Rs(seed, i, 87) * 6 * f,
                    S = 1 + J.Rs(seed, i, k, 88) * 0.14 * f,
                };
            });
        },
    });

    // ---------------- ripple（JS L579） ----------------

    private static void Ripple() => Reg0("ripple", new MoveDef
    {
        Name = "波紋", // JS: ae: 'pop'
        Tags = new[] { "emotional", "calm", "graphic" },
        W = 1,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var isx = N1(it, "sx");
            var isy = N1(it, "sy");
            var size = N0(it, "size");
            var b0 = Box(it);
            var Rmax = Hypot(b0.W, b0.H) / 2 + size * 0.3;
            Glyphs(env, (i, g, _) =>
            {
                if (g == null) return null;
                var gp = GlyphPos(g, isx, isy);
                var vx = gp[0] - b0.Cx;
                var vy = gp[1] - b0.Cy;
                var d = Hypot(vx, vy);
                var pd = 0.68 * d / Rmax;
                var q = J.Clamp((p - pd) / 0.32);
                if (q <= 0) return new CharXf { Hide = true };
                if (q >= 1) return null;
                var w = Math.Sin(q * Math.PI * 2) * Math.Pow(1 - q, 2);
                var push = size * 0.28 * w;
                var o = new CharXf { S = 1 + 0.4 * Math.Sin(q * Math.PI) * (1 - q), A = J.Clamp(q * 5) };
                if (d > 1)
                {
                    o.Dx = vx / d * push;
                    o.Dy = vy / d * push;
                }
                return o;
            });
            if (!IsMain(it)) return;
            // JS postLocal（两圈 accent 波环）——就地执行（层级在文字下）
            DrawPostLocal(env, it, (env2, b, x, A) =>
            {
                var lw = Math.Max(1.5, size * 0.022);
                for (var k = 0; k < 2; k++)
                {
                    var f = J.Clamp(p / 0.68 - k * 0.12);
                    if (f <= 0) continue;
                    var r = Rmax * f * 1.05;
                    var a = A * (1 - f) * (k == 1 ? 0.5 : 0.85);
                    if (a > 0.01 && r > 1) env2.Circle(b.Cx, b.Cy, r, null, env2.Sc!.Str("accent"), lw * (1 - f * 0.5), a, false);
                }
            });
        },
    });

    // ---------------- zipper（JS L611） ----------------

    private static void Zipper() => Reg0("zipper", new MoveDef
    {
        Name = "ジッパー", // JS: ae: 'slice'
        Tags = new[] { "graphic", "pop" },
        W = 0.8,
        InDur = (dur, _) => J.Clamp(dur * 0.42, 0.18, 0.75),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var vert = B(it, "vertical");
            var isx = N1(it, "sx");
            var isy = N1(it, "sy");
            var size = N0(it, "size");
            var b = Box(it);
            var U0 = vert ? b.Y0 : b.X0;
            var U1 = vert ? b.Y1 : b.X1;
            var Lv = size * 2.4;
            var amp = size * 0.8;
            var s = J.Lerp(U0 - size * 0.3, U1 + size * 0.4, J.E.InOutSine(J.Clamp(p / 0.92)));
            var fade = J.Clamp(p * 5);
            var solo = GlyphN(it) <= 1;
            var mi = (int)N0(it, "mi"); // JS (it.mi | 0)
            Glyphs(env, (i, g, _) =>
            {
                if (g == null) return null;
                var gp = GlyphPos(g, isx, isy);
                var u = vert ? gp[1] : gp[0];
                var o = J.Clamp((u - s) / Lv + 0.25);
                if (o <= 0) return null;
                var sgn = (g.Value<int?>("ci") + (solo ? mi : 0)) % 2 != 0 ? 1 : -1;
                var e = Math.Pow(o, 0.8) * amp;
                return vert
                    ? new CharXf { Dx = sgn * e, Rot = -sgn * 14 * o, A = fade }
                    : new CharXf { Dy = sgn * e, Rot = sgn * 14 * o, A = fade };
            });
            if (!IsPrimary(it)) return;
            // JS postLocal（拉链头）——就地执行（层级在文字下）
            DrawPostLocal(env, it, (env2, bb, x, A2) =>
            {
                var a = A2 * fade * (1 - Sstep(0.82, 0.98, p));
                if (a <= 0.01) return;
                var w = size * 0.3;
                var h = size * 0.46;
                var col = env2.Sc!.Str("accent");
                foreach (var L in LinesOf(x, bb))
                {
                    var pnt = Pt(vert, s, L.V);
                    var px = pnt[0];
                    var py = pnt[1];
                    if (vert) env2.RRect(px - h / 2, py - w / 2, h, w, w * 0.2, col, a, false);
                    else env2.RRect(px - w / 2, py - h / 2, w, h, w * 0.2, col, a, false);
                    var tab = size * 0.2;
                    if (vert) env2.Rect(px + h / 2, py - tab * 0.25, tab, tab * 0.5, col, a, false);
                    else env2.Rect(px - tab * 0.25, py + h / 2, tab * 0.5, tab, col, a, false);
                }
            });
        },
    });

    // ---------------- zoomAlt（JS L646） ----------------

    private static void ZoomAlt() => Reg0("zoomAlt", new MoveDef
    {
        Name = "交互ズーム", // JS: ae: 'zoom'
        Tags = new[] { "pop", "graphic", "glitch" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var size = N0(it, "size");
            var bl = Math.Min(30, size * 0.1);
            var mi = (int)N0(it, "mi");
            var solo = GlyphN(it) <= 1;
            Glyphs(env, (i, g, n) =>
            {
                var q = Stg(p, OrdLR(i, n), 0.35);
                if (q <= 0) return new CharXf { Hide = true };
                if (q >= 1) return null;
                var parity = ((g?.Value<int?>("ci") ?? 0) + (solo ? mi : 0)) % 2 == 0;
                if (parity)
                {
                    var e = OQuint(q);
                    return new CharXf { S = J.Lerp(2.4, 1, e), Blur = bl * (1 - e), A = J.Clamp(q * 2.5) };
                }
                return new CharXf { S = Math.Max(0.02, OBack(q, 2.2)), A = J.Clamp(q * 4) };
            });
        },
    });

    // ---------------- tiltUp（JS L664） ----------------

    private static void TiltUp() => Reg0("tiltUp", new MoveDef
    {
        Name = "起立", // JS: ae: 'stretch'
        Tags = new[] { "graphic", "editorial", "pop" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var th = 86 * Spring(p, 3.2, 1.25);
            var k = Math.Max(0.03, Math.Cos(th * J.DEG));
            var b = Box(it);
            var size = N0(it, "size");
            var bg = Pick(env.Sc!.Str("bg"));
            var col = ColOf(it);
            var main = IsMain(it);
            it["sy"] = N1(it, "sy") * k;
            MoveLocal(it, 0, b.Y1 * (1 - k));
            it["alpha"] = NN(it, "alpha", 1) * J.Clamp(p * 6);
            if (IsHex(S(it, "color"))) it["color"] = J.Mix(col, bg, (1 - k) * 0.55);
            if (!main) return;
            // JS postLocal（地面 accent 线）——就地执行（层级在文字下）
            DrawPostLocal(env, it, (env2, bb, x, A) =>
            {
                var a = A * (1 - Sstep(0.35, 0.8, p));
                if (a <= 0.01) return;
                var m = size * 0.2;
                var t = Math.Max(1.5, size * 0.025);
                env2.Rect(bb.X0 - m, bb.Y1 + t, bb.W + m * 2, t, env2.Sc!.Str("accent"), a, false);
            });
        },
    });

    // ---------------- stickerPeel（JS L687） ----------------

    private static void StickerPeel() => Reg0("stickerPeel", new MoveDef
    {
        Name = "シール貼り", // JS: ae: 'wipe'
        Tags = new[] { "pop", "graphic", "editorial" },
        W = 0.9,
        MinDur = 0.5,
        InDur = (dur, _) => J.Clamp(dur * 0.45, 0.2, 0.85),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var isx = N1(it, "sx");
            var isy = N1(it, "sy");
            var size = N0(it, "size");
            var pad = size * 0.12;
            var bg = Pick(env.Sc!.Str("bg"));
            var col = ColOf(it);
            double Qf(int i, int n) => Stg(p, OrdLR(i, n), 0.5);
            // 字盒和沿 45° 对角线（左下 → 右上）的折叠进度
            StickerGeo Geo(JObject g, double u)
            {
                var gp = GlyphPos(g, isx, isy);
                var hw = JLayouts.D(g, "w") * isx / 2 + pad;
                var hh = JLayouts.D(g, "h") * isy / 2 + pad;
                return new StickerGeo
                {
                    Gx = gp[0],
                    Gy = gp[1],
                    L = gp[0] - hw,
                    R = gp[0] + hw,
                    T = gp[1] - hh,
                    B = gp[1] + hh,
                    C = J.Lerp(-0.02, 1.02, IoCubic(u)) * (hw * 2 + hh * 2),
                };
            }
            double[][] Quad(StickerGeo G) =>
                new[] { new[] { G.L, G.T }, new[] { G.R, G.T }, new[] { G.R, G.B }, new[] { G.L, G.B } };
            // TODO(11p 管线钩子)：JS clipLocal 是每字形对角半面（非矩形）——按字形中心
            // 在折痕哪一侧做可见性近似（斜边按字形粒度离散）。
            {
                var bx = Box(it);
                var nN = bx.Lay.N;
                ClipHideGlyphs(env, g =>
                {
                    var u = Qf(g.Value<int?>("i") ?? 0, nN);
                    if (u <= 0) return false;
                    var G = Geo(g, u);
                    var gcx = (JLayouts.D(g, "x") + JLayouts.D(g, "vx")) * isx;
                    var gcy = (JLayouts.D(g, "y") + JLayouts.D(g, "vy")) * isy;
                    return G.C - ((gcx - G.L) + (G.B - gcy)) >= 0;
                });
            }
            // JS addPost：没贴住的部分仍翘起约 62°，正面看就是把字朝折痕方向透视压扁（每 pass 都画）——就地执行
            DrawPost(env, it, (env2, x) =>
            {
                var A = J.Clamp(JAnim.NumNull(x, "alpha", 1));
                if (A <= 0.01) return;
                var lay = JText.LayoutText(x);
                var n = lay.N;
                var lift = J.Mix(col, bg, 0.3);
                foreach (var g in lay.Cells)
                {
                    if (Blank(g)) continue;
                    var u = Qf(g.I, n);
                    if (u <= 0 || u >= 1) continue;
                    var G = Geo(CellJson(g), u);
                    var poly = ClipHalf(Quad(G), v => -(G.C - ((v[0] - G.L) + (G.B - v[1]))));
                    if (poly.Count < 3) continue;
                    var px = G.L + G.C / 2;
                    var py = G.B - G.C / 2;
                    var kk = 1 - Math.Cos(62 * J.DEG);
                    var m11 = 1 - kk / 2;
                    var m12 = kk / 2;
                    var gi = g.I;
                    var ctx = env2.Ctx;
                    ctx.Save();
                    ToLocal(ctx, x);
                    ctx.Translate((float)px, (float)py);
                    ctx.Concat(new SKMatrix((float)m11, (float)m12, 0, (float)m12, (float)m11, 0, 0, 0, 1));
                    ctx.Translate((float)-px, (float)-py);
                    using var clip = new SKPath();
                    for (var k = 0; k < poly.Count; k++)
                    {
                        if (k == 0) clip.MoveTo((float)poly[k][0], (float)poly[k][1]);
                        else clip.LineTo((float)poly[k][0], (float)poly[k][1]);
                    }
                    clip.Close();
                    ctx.ClipPath(clip);
                    var c = Shallow(x);
                    c["x"] = 0.0;
                    c["y"] = 0.0;
                    c["rot"] = 0.0;
                    c["skew"] = 0.0;
                    c["shadow"] = null;
                    c["extrude"] = null;
                    c["gradient"] = null;
                    c["pattern"] = null;
                    c["color"] = lift;
                    c["strokeColor"] = lift;
                    c["alpha"] = A * J.Clamp(u * 6);
                    DrawIt(env2, c, (j, _, _) => j == gi ? null : new CharXf { Hide = true });
                    ctx.Restore();
                }
            });
        },
    });

    /// <summary>stickerPeel 的字盒几何（JS geo 返回对象）。</summary>
    private sealed class StickerGeo
    {
        public double Gx, Gy, L, R, T, B, C;
    }

    private static JObject CellJson(JText.GlyphCell g) => JText.CellJson(g);

    // ---------------- crumple（JS L762） ----------------

    private static void Crumple() => Reg0("crumple", new MoveDef
    {
        Name = "くしゃ戻り", // JS: ae: 'assemble', pieces: true（走 env.PieceFns）
        Tags = new[] { "pop", "graphic", "emotional" },
        W = 0.7,
        MinDur = 0.5,
        InDur = (dur, _) => J.Clamp(dur * 0.45, 0.22, 0.8),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            if (p >= 1) return;
            var seed = SeedOf(it);
            var isx = N1(it, "sx");
            var isy = N1(it, "sy");
            var nN = Math.Max(1, JText.LayoutText(it).N);
            if (!FillOn(it) || it["gradient"] != null)
            {
                Glyphs(env, (i, _, n) =>
                {
                    var q = Stg(p, OrdLR(i, n), 0.45);
                    if (q <= 0) return new CharXf { Hide = true };
                    if (q >= 1) return null;
                    var e = OBack(q, 1.6);
                    return new CharXf { S = J.Lerp(0.35, 1, e), Rot = J.Rs(seed, i, 92) * 120 * (1 - e), A = J.Clamp(q * 4) };
                });
                return;
            }
            env.PieceFns.Add((ci, pj, _, ox, oy, g) =>
            {
                var q = Stg(p, nN > 1 ? ci / (double)(nN - 1) : 0, 0.45);
                if (q <= 0) return null;
                if (q >= 1) return JAnim.Pid;
                var gx = g != null ? (JLayouts.D(g, "x") + JLayouts.D(g, "vx")) * isx : ox;
                var gy = g != null ? (JLayouts.D(g, "y") + JLayouts.D(g, "vy")) * isy : oy;
                var vx = ox - gx;
                var vy = oy - gy;
                const double tB = 0.3;
                double k, beta, s, a = 1, rot;
                var rot0 = J.Rs(seed, ci, pj, 91) * 160;
                var beta0 = J.Rs(seed, ci, 92) * 2.4;
                if (q < tB)
                {
                    var t = q / tB;
                    k = 0.78;
                    beta = beta0 * (1.3 - 0.3 * t);
                    s = 0.72 * OBack(t, 2);
                    a = J.Clamp(t * 4);
                    rot = rot0;
                }
                else
                {
                    var t = (q - tB) / (1 - tB);
                    if (t > 0.95) return JAnim.Pid;
                    var e = OBack(t, 1.5);
                    k = 0.78 * (1 - e);
                    beta = beta0 * (1 - e);
                    s = J.Lerp(0.72, 1, e);
                    rot = rot0 * (1 - e);
                }
                var c = Math.Cos(beta);
                var sn = Math.Sin(beta);
                var fx = vx * (1 - k);
                var fy = vy * (1 - k);
                var nx = c * fx - sn * fy;
                var ny = sn * fx + c * fy;
                return JAnim.Pt(nx - vx, ny - vy, rot + beta / J.DEG, s, 1, 0, a);
            });
        },
    });

    // ---------------- noteUnfold（JS L816） ----------------

    private static void NoteUnfold() => Reg0("noteUnfold", new MoveDef
    {
        Name = "手紙開き", // JS: ae: 'pop'
        Tags = new[] { "emotional", "calm", "graphic" },
        W = 0.8,
        MinDur = 0.6,
        InDur = (dur, _) => J.Clamp(dur * 0.5, 0.25, 0.9),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var col = ColOf(it);
            var bg = Pick(env.Sc!.Str("bg"));
            var size = N0(it, "size");
            var isx = N1(it, "sx");
            var isy = N1(it, "sy");
            double Qf(int i, int n) => Stg(p, OrdLR(i, n), 0.45);
            double[] T1 = { 0.14, 0.52 }, T2 = { 0.5, 0.94 };
            double Op(double q, double[] T) => OCubic(J.Clamp((q - T[0]) / (T[1] - T[0])));
            string Shade(double e) => J.Mix(col, bg, (1 - e) * 0.55);
            var main = Glyphs(env, (i, g, n) =>
            {
                var q = Qf(i, n);
                if (q <= 0) return new CharXf { Hide = true };
                if (q >= 1) return null;
                if (g != null && JLayouts.B(g, "r90")) return new CharXf { A = J.Clamp(q * 2) };
                if (q < T1[1])
                    return new CharXf
                    {
                        A = J.Clamp(q / T1[0]),
                        S = J.Lerp(0.9, 1, OBack(q / T1[0], 2)),
                        ClipX = new[] { -0.64, 0.004 },
                        ClipY = new[] { -0.66, 0.004 },
                    };
                return new CharXf { ClipX = new[] { -0.7, 0.7 }, ClipY = new[] { -0.66, 0.004 } };
            });
            // JS addPost（两片折页 + 折痕线）——就地执行（层级在文字下）
            DrawPost(env, it, (env2, x) =>
            {
                CopyDraw(env2, x, main, (i, g, n) =>
                {
                    var q = Qf(i, n);
                    if (q <= T1[0] || q >= T1[1] || (g != null && JLayouts.B(g, "r90"))) return new CharXf { Hide = true };
                    var e = Op(q, T1);
                    return new CharXf { Sx = Math.Max(0.03, e), ClipX = new[] { 0.0, 0.64 }, ClipY = new[] { -0.66, 0.004 }, Color = Shade(e) };
                }, new JObject { ["shadow"] = null, ["extrude"] = null });
                CopyDraw(env2, x, main, (i, g, n) =>
                {
                    var q = Qf(i, n);
                    if (q <= T2[0] || q >= 1 || (g != null && JLayouts.B(g, "r90"))) return new CharXf { Hide = true };
                    var e = Op(q, T2);
                    return new CharXf { Sy = Math.Max(0.03, e), ClipY = new[] { 0.0, 0.66 }, ClipX = new[] { -0.7, 0.7 }, Color = Shade(e) };
                }, new JObject { ["shadow"] = null, ["extrude"] = null });
                if (env2.Pass != "main" || !IsMain(x)) return;
                var A = J.Clamp(JAnim.NumNull(x, "alpha", 1));
                var lay = JText.LayoutText(x);
                var n2 = lay.N;
                var lw = Math.Max(1, size * 0.012);
                var cc = env2.Sc!.Str("sub");
                var ctx = env2.Ctx;
                ctx.Save();
                ToLocal(ctx, x);
                foreach (var g in lay.Cells) // 还折着时的淡淡折痕
                {
                    if (Blank(g) || g.R90) continue;
                    var q = Qf(g.I, n2);
                    if (q <= 0 || q >= 1) continue;
                    var gp = GlyphPos(g, isx, isy);
                    var gx = gp[0];
                    var gy = gp[1];
                    var hw = g.W * isx * 0.55;
                    var hh = g.H * isy * 0.58;
                    var a = A * 0.6 * (1 - Sstep(0.85, 1, q)) * J.Clamp(q * 6);
                    env2.Line(new[] { new[] { gx, gy - hh }, new[] { gx, q < T1[1] ? gy : gy + hh } }, cc, lw, a, false);
                    env2.Line(new[] { new[] { gx - hw, gy }, new[] { q < T1[0] ? gx : gx + hw, gy } }, cc, lw, a, false);
                }
                ctx.Restore();
            });
        },
    });

    // ---------------- tornJoin（JS L871） ----------------

    private static void TornJoin() => Reg0("tornJoin", new MoveDef
    {
        Name = "破れ合わせ", // JS: ae: 'slice'
        Tags = new[] { "graphic", "emotional", "pop" },
        W = 0.8,
        InDur = (dur, _) => J.Clamp(dur * 0.42, 0.18, 0.75),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var vert = B(it, "vertical");
            var size = N0(it, "size");
            var seed = SeedOf(it);
            var b0 = Box(it);
            var Dv = size * 2 + (vert ? b0.H : b0.W) * 0.25;
            var e = OQuart(p / 0.72);
            var k = 1 - e;
            var hit = Sstep(0.62, 0.72, p) * (1 - Sstep(0.72, 1, p));
            // 横跨阅读方向的毛边（局部坐标）
            double[][] Seam(EBox b)
            {
                var U = vert ? b.Cy : b.Cx;
                var V0 = (vert ? b.X0 : b.Y0) - size * 0.15;
                var V1 = (vert ? b.X1 : b.Y1) + size * 0.15;
                const int K = 11;
                var @out = new List<double[]>();
                for (var j = 0; j <= K; j++)
                    @out.Add(new[] { U + J.Rs(seed, j, 95) * size * 0.1 + (j % 2 != 0 ? 1 : -1) * size * 0.03, J.Lerp(V0, V1, j / (double)K) });
                return @out.ToArray();
            }
            // 半边的多边形（局部坐标），ov 是压进另一半的重叠量
            double[][] Half(EBox b, int side, double ov = 0)
            {
                var s = Seam(b);
                var far = (vert ? b.H : b.W) + size * 6;
                var U = vert ? b.Cy : b.Cx;
                var pts = s.Select(q => Pt(vert, q[0] - side * ov, q[1])).ToList();
                pts.Add(Pt(vert, U + side * far, s[s.Length - 1][1]));
                pts.Add(Pt(vert, U + side * far, s[0][1]));
                return pts.ToArray();
            }
            var fib = Pick(env.Sc!.Str("fg"));
            var fa = 1 - Sstep(0.7, 0.95, p);
            var prim = IsPrimary(it);
            it["alpha"] = NN(it, "alpha", 1) * J.Clamp(p * 5);
            var jolt = hit * size * 0.035 * Math.Sin(p * 90);
            if (vert) it["x"] = N0(it, "x") + jolt;
            else it["y"] = N0(it, "y") + jolt;
            void Fibres(Env env2, JObject q)
            {
                var ctx = env2.Ctx;
                ctx.Save();
                ToLocal(ctx, q);
                var sm = Seam(Box(q));
                env2.Line(sm.Select(q2 => Pt(vert, q2[0], q2[1])).ToArray(),
                    J.Mix(fib, Pick(env2.Sc!.Str("bg")), 0.35), Math.Max(1, size * 0.018),
                    fa * 0.8 * J.Clamp(JAnim.NumNull(q, "alpha", 1)), false);
                ctx.Restore();
            }
            if (k <= 1e-4)
            {
                // 两半已接上：整行直接画，只剩纤维残留
                if (fa > 0.01 && prim)
                    DrawPost(env, it, (env2, x) =>
                    {
                        if (env2.Pass == "main") Fibres(env2, x);
                    });
                return;
            }
            var dX = -(vert ? 0 : Dv * k);
            var dY = -(vert ? Dv * k : 0);
            var dR = -5 * k;
            it["x"] = N0(it, "x") + dX;
            it["y"] = N0(it, "y") + dY;
            it["rot"] = N0(it, "rot") + dR;
            // TODO(11p 管线钩子)：JS clipLocal 是毛边多边形——按字形中心在毛边哪一侧做可见性近似
            JAnim.CharFn clipFn;
            {
                var seamPts = Seam(b0);
                var isx = N1(it, "sx");
                var isy = N1(it, "sy");
                clipFn = ClipHideGlyphs(env, g =>
                {
                    var gp = GlyphPos(g, isx, isy);
                    var v = vert ? gp[0] : gp[1];
                    var u = vert ? gp[1] : gp[0];
                    return u <= SeamUAt(seamPts, v, vert);
                });
            }
            // JS addPre：另一半（side=+1）的副本（毛边 canvas 裁剪全保真）+ 两道纤维
            DrawPre(env, it, (env2, x) =>
            {
                var o = Shallow(x);
                o["x"] = N0(x, "x") - 2 * dX;
                o["y"] = N0(x, "y") - 2 * dY;
                o["rot"] = N0(x, "rot") - 2 * dR;
                ClipPathDraw(env2, o, (ctx, pth) =>
                {
                    var poly = Half(Box(o), 1, 1.2 / (env2.Scale != 0 ? env2.Scale : 1));
                    for (var j = 0; j < poly.Length; j++)
                    {
                        if (j == 0) pth.MoveTo((float)poly[j][0], (float)poly[j][1]);
                        else pth.LineTo((float)poly[j][0], (float)poly[j][1]);
                    }
                    pth.Close();
                }, () => CopyDraw(env2, o, clipFn, null)); // 副本不带主文字的裁剪可见性（JS clipFn 不进 charFns）
                if (fa > 0.01 && env2.Pass == "main" && prim)
                {
                    Fibres(env2, x);
                    Fibres(env2, o);
                }
            });
        },
    });

    /// <summary>tornJoin：毛边折线在横坐标 v 处的 u（线性插值；折线存于局部 (u,v) 空间）。</summary>
    private static double SeamUAt(double[][] seamPts, double v, bool vert)
    {
        _ = vert;
        for (var j = 1; j < seamPts.Length; j++)
        {
            var v0 = seamPts[j - 1][1];
            var v1 = seamPts[j][1];
            if (v <= v1 || j == seamPts.Length - 1)
            {
                var t = v1 == v0 ? 0 : J.Clamp((v - v0) / (v1 - v0));
                return J.Lerp(seamPts[j - 1][0], seamPts[j][0], t);
            }
        }
        return seamPts[^1][0];
    }

    // ---------------- splitFlap（JS L944） ----------------

    private static void SplitFlap() => Reg0("splitFlap", new MoveDef
    {
        Name = "パタパタ", // JS: ae: 'scramble'
        Tags = new[] { "glitch", "editorial", "graphic" },
        W = 0.9,
        MinDur = 0.75,
        InDur = (dur, n) => J.Clamp(0.35 + n * 0.03, 0.45, Math.Max(0.45, Math.Min(1.0, dur * 0.55))),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var seed = SeedOf(it);
            var col = ColOf(it);
            var bg = Pick(env.Sc!.Str("bg"));
            var size = N0(it, "size");
            var isx = N1(it, "sx");
            var isy = N1(it, "sy");
            var pool = JLang.Pool("signs") ?? "";

            (double Q, int F, int J, double Ff, string? OldCh, string? NewCh) St(int i, int n)
            {
                var q = Stg(p, OrdLR(i, n), 0.4);
                var F = 3 + (i % 2);
                if (q <= 0 || q >= 1) return (q, F, 0, 0, null, null);
                var x = q * F;
                var j = Math.Min(F - 1, (int)Math.Floor(x));
                var f = x - j;
                string? ChOf(int kk) => kk <= 0 ? null : kk >= F ? "" : pool[(int)(J.H(seed, i, kk, 97) % (uint)pool.Length)].ToString();
                return (q, F, j, f, ChOf(j), ChOf(j + 1));
            }
            var main = Glyphs(env, (i, g, n) =>
            {
                var st = St(i, n);
                if (st.Q <= 0) return new CharXf { Hide = true };
                if (st.Q >= 1) return null;
                if (g != null && JLayouts.B(g, "r90")) return new CharXf { A = st.Q };
                var c1 = st.Ff < 0.5 ? Math.Cos(st.Ff * Math.PI) : 0;
                var o = new CharXf { ClipY = new[] { -0.64, -0.64 * c1 }, ClipX = new[] { -0.7, 0.7 } };
                if (!string.IsNullOrEmpty(st.NewCh)) o.Ch = st.NewCh;
                return o;
            });
            // JS addPre：旧字的下半，翻页盖住前一直显示——就地执行
            DrawPre(env, it, (env2, x) =>
            {
                CopyDraw(env2, x, main, (i, g, n) =>
                {
                    var st = St(i, n);
                    if (st.Q <= 0 || st.Q >= 1 || (g != null && JLayouts.B(g, "r90")) || st.OldCh == null) return new CharXf { Hide = true };
                    var c2 = st.Ff >= 0.5 ? -Math.Cos(st.Ff * Math.PI) : 0;
                    var o = new CharXf { ClipY = new[] { 0.64 * c2, 0.66 }, ClipX = new[] { -0.7, 0.7 } };
                    if (!string.IsNullOrEmpty(st.OldCh)) o.Ch = st.OldCh;
                    return o;
                }, new JObject { ["shadow"] = null });
            });
            // JS addPost：正在翻落的那一页 + 两页之间的细缝——就地执行（层级在文字下）
            DrawPost(env, it, (env2, x) =>
            {
                CopyDraw(env2, x, main, (i, g, n) =>
                {
                    var st = St(i, n);
                    if (st.Q <= 0 || st.Q >= 1 || (g != null && JLayouts.B(g, "r90"))) return new CharXf { Hide = true };
                    if (st.Ff < 0.5)
                    {
                        if (st.OldCh == null) return new CharXf { Hide = true };
                        var c = Math.Max(0.03, Math.Cos(st.Ff * Math.PI));
                        var o = new CharXf { Sy = c, ClipY = new[] { -0.64, 0.0 }, ClipX = new[] { -0.7, 0.7 }, Color = J.Mix(col, bg, (1 - c) * 0.5) };
                        if (!string.IsNullOrEmpty(st.OldCh)) o.Ch = st.OldCh;
                        return o;
                    }
                    var c2 = Math.Max(0.03, -Math.Cos(st.Ff * Math.PI));
                    var o2 = new CharXf { Sy = c2, ClipY = new[] { 0.0, 0.66 }, ClipX = new[] { -0.7, 0.7 }, Color = J.Mix(col, bg, (1 - c2) * 0.5) };
                    if (!string.IsNullOrEmpty(st.NewCh)) o2.Ch = st.NewCh;
                    return o2;
                }, new JObject { ["shadow"] = null });
                if (env2.Pass != "main") return;
                var A = J.Clamp(JAnim.NumNull(x, "alpha", 1));
                var lay = JText.LayoutText(x);
                var n2 = lay.N;
                var ctx = env2.Ctx;
                ctx.Save();
                ToLocal(ctx, x);
                foreach (var g in lay.Cells) // 两页之间的细缝
                {
                    if (Blank(g) || g.R90) continue;
                    var st = St(g.I, n2);
                    if (st.Q <= 0 || st.Q >= 1) continue;
                    var gp = GlyphPos(g, isx, isy);
                    var w = g.W * isx * 1.04;
                    var t = Math.Max(1, size * 0.018);
                    env2.Rect(gp[0] - w / 2, gp[1] - t / 2, w, t, bg, A, false);
                }
                ctx.Restore();
            });
        },
    });

    // ---------------- overexpose（JS L1013） ----------------

    private static void Overexpose() => Reg0("overexpose", new MoveDef
    {
        Name = "露出オーバー", // JS: ae: 'blur'
        Tags = new[] { "emotional", "pop", "glitch" },
        W = 1,
        InDur = (dur, _) => J.Clamp(dur * 0.42, 0.18, 0.75),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var hot = HotOf(env, it);
            var size = N0(it, "size");
            const double tF = 0.12;
            var ex = p < tF ? 1 : 1 - J.E.InOutSine(J.Clamp((p - tF) / (1 - tF)));
            if (p < tF) it["alpha"] = NN(it, "alpha", 1) * J.E.OutCubic(p / tF);
            if (IsHex(S(it, "color"))) it["color"] = J.Mix(S(it, "color")!, hot, ex * 0.96);
            var sc0 = IsHex(S(it, "strokeColor")) ? S(it, "strokeColor")! : ColOf(it);
            if (N0(it, "stroke") > 0 || !FillOn(it)) it["strokeColor"] = J.Mix(sc0, hot, ex * 0.96);
            if (FillOn(it) && ex > 0.02)
            {
                it["stroke"] = N0(it, "stroke") + size * 0.045 * ex;
                it["strokeColor"] = N0(it, "stroke") > size * 0.045 * ex + 0.01 ? S(it, "strokeColor") : J.Mix(ColOf(it), hot, 1);
                it["strokeUnder"] = false;
            }
            it["blur"] = N0(it, "blur") + Math.Min(14, size * 0.04) * ex * ex;
            var sh = it["shadow"];
            if ((sh == null || sh.Type == JTokenType.Null) && ex > 0.02)
                it["shadow"] = new JObject
                {
                    ["color"] = Rgba(hot, 0.9 * ex),
                    ["blur"] = Math.Min(90, size * 0.5) * ex,
                    ["dx"] = 0.0,
                    ["dy"] = 0.0,
                };
            ScaleAbout(it, 1 + 0.06 * ex);
        },
    });

    // ---------------- glint（JS L1040） ----------------

    private static void Glint() => Reg0("glint", new MoveDef
    {
        Name = "グリント", // JS: ae: 'wipe'
        Tags = new[] { "editorial", "emotional", "graphic" },
        W = 1,
        InDur = (dur, _) => J.Clamp(dur * 0.45, 0.2, 0.8),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var vert = B(it, "vertical");
            var size = N0(it, "size");
            const double kS = 0.45;
            var hot = J.Lum(ColOf(it)) > 0.7 || J.Lum(Pick(env.Sc!.Str("bg"))) > 0.5
                ? Pick(env.Sc.Str("accent"), env.Sc.Str("fg"))
                : "#ffffff";
            (double U0, double U1, double V0, double V1, double Vc, double Bw, double B, double Big) Geo(EBox b)
            {
                var U0 = vert ? b.Y0 : b.X0;
                var U1 = vert ? b.Y1 : b.X1;
                var V0 = (vert ? b.X0 : b.Y0) - size * 0.3;
                var V1 = (vert ? b.X1 : b.Y1) + size * 0.3;
                var vc = (V0 + V1) / 2;
                var bw = size * 0.55;
                var sl = kS * (V1 - V0) / 2;
                return (U0, U1, V0, V1, vc, bw,
                    J.Lerp(U0 - bw - sl, U1 + bw + sl, J.E.InOutSine(J.Clamp(p / 0.9))),
                    (U1 - U0) + size * 8);
            }
            // u + kS·(v - vc) 落在 [a, b] 之间的区域（局部坐标路径）
            void Band(SKPath path, (double U0, double U1, double V0, double V1, double Vc, double Bw, double B, double Big) G, double a, double b2)
            {
                var q = new[]
                {
                    new[] { a - kS * (G.V0 - G.Vc), G.V0 }, new[] { b2 - kS * (G.V0 - G.Vc), G.V0 },
                    new[] { b2 - kS * (G.V1 - G.Vc), G.V1 }, new[] { a - kS * (G.V1 - G.Vc), G.V1 },
                };
                for (var j = 0; j < q.Length; j++)
                {
                    var p2 = Pt(vert, q[j][0], q[j][1]);
                    if (j == 0) path.MoveTo((float)p2[0], (float)p2[1]);
                    else path.LineTo((float)p2[0], (float)p2[1]);
                }
                path.Close();
            }
            // TODO(11p 管线钩子)：JS clipLocal 是斜向半带——按字形中心做可见性近似
            JAnim.CharFn clipFn;
            {
                var bx = Box(it);
                var G = Geo(bx);
                var isx = N1(it, "sx");
                var isy = N1(it, "sy");
                clipFn = ClipHideGlyphs(env, g =>
                {
                    var gp = GlyphPos(g, isx, isy);
                    var u = vert ? gp[1] : gp[0];
                    var v = vert ? gp[0] : gp[1];
                    return u + kS * (v - G.Vc) <= G.B;
                });
            }
            var ghostA = 0.2 * J.Clamp(p * 6);
            // JS addPre：淡幽灵态副本——就地执行
            DrawPre(env, it, (env2, x) =>
            {
                var G = Geo(Box(x));
                ClipPathDraw(env2, x, (ctx, pth) => Band(pth, G, G.B, G.U1 + G.Big), () =>
                    CopyDraw(env2, x, clipFn, null, new JObject // 副本不带裁剪可见性
                    {
                        ["alpha"] = NN(x, "alpha", 1) * ghostA,
                        ["shadow"] = null,
                    }));
            });
            // JS addPost：扫过之处的点亮副本 ×2——就地执行（层级在文字下）
            DrawPost(env, it, (env2, x) =>
            {
                if (env2.Pass != "main") return;
                var G = Geo(Box(x));
                var A = J.Clamp(JAnim.NumNull(x, "alpha", 1)) * (1 - Sstep(0.85, 1, p));
                double[][] ranges = { new[] { G.B - G.Bw, G.B + G.Bw * 0.15, 0.45 }, new[] { G.B - G.Bw * 0.45, G.B, 1.0 } };
                foreach (var r in ranges)
                {
                    ClipPathDraw(env2, x, (ctx, pth) => Band(pth, G, r[0], r[1]), () =>
                        CopyDraw(env2, x, clipFn, null, new JObject // 副本不带裁剪可见性
                        {
                            ["color"] = hot,
                            ["strokeColor"] = hot,
                            ["alpha"] = A * r[2],
                            ["shadow"] = null,
                            ["gradient"] = null,
                            ["pattern"] = null,
                        }));
                }
            });
        },
    });

    // ---------------- loupe（JS L1113） ----------------

    private static void Loupe() => Reg0("loupe", new MoveDef
    {
        Name = "ルーペ", // JS: ae: 'zoom'
        Tags = new[] { "editorial", "pop", "calm" },
        W = 0.8,
        MinDur = 0.6,
        InDur = (dur, _) => J.Clamp(dur * 0.5, 0.25, 0.9),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var vert = B(it, "vertical");
            var isx = N1(it, "sx");
            var isy = N1(it, "sy");
            var size = N0(it, "size");
            var b = Box(it);
            var U0 = vert ? b.Y0 : b.X0;
            var U1 = vert ? b.Y1 : b.X1;
            var V0 = vert ? b.X0 : b.Y0;
            var V1 = vert ? b.X1 : b.Y1;
            var vc = (V0 + V1) / 2;
            var r = Math.Max(size * 0.95, (V1 - V0) * 0.62);
            var lc = J.Lerp(U0 - r * 0.7, U1 + r * 1.4, J.E.InOutSine(J.Clamp(p / 0.9)));
            var pre = 0.14 * J.Clamp(p * 5);
            var fin = J.Clamp(p * 6);
            Glyphs(env, (i, g, _) =>
            {
                if (g == null) return null;
                var gp = GlyphPos(g, isx, isy);
                var u = vert ? gp[1] : gp[0];
                var v = vert ? gp[0] : gp[1];
                var du = u - lc;
                var dv = v - vc;
                var d = Hypot(du, dv) / r;
                if (d >= 1) return du > 0 ? new CharXf { A = pre } : null;
                var m = 1 - d * d;
                var o = new CharXf { S = 1 + 0.6 * m, A = fin * (du > 0 ? J.Lerp(pre, 1, Math.Min(1, m * 3)) : 1) };
                var push = 0.35 * m;
                if (vert)
                {
                    o.Dy = du * push;
                    o.Dx = dv * push;
                }
                else
                {
                    o.Dx = du * push;
                    o.Dy = dv * push;
                }
                return o;
            });
            if (!IsPrimary(it)) return;
            // JS postLocal（放大镜圈 + 柄）——就地执行（层级在文字下）
            DrawPostLocal(env, it, (env2, bb, x, A) =>
            {
                var a = A * J.Clamp(p * 8) * (1 - Sstep(0.8, 0.97, p));
                if (a <= 0.01) return;
                var cp = Pt(vert, lc, vc);
                var cx = cp[0];
                var cy = cp[1];
                var lw = Math.Max(2, size * 0.05);
                var col = env2.Sc!.Str("accent");
                env2.Circle(cx, cy, r, null, col, lw, a, false);
                env2.Arc(cx, cy, r * 0.8, 200, 250, env2.Sc.Str("fg"), lw * 0.5, a * 0.5, false);
                var hx = cx + Math.Cos(Math.PI / 4) * r;
                var hy = cy + Math.Sin(Math.PI / 4) * r;
                env2.Line(new[] { new[] { hx, hy }, new[] { hx + r * 0.55, hy + r * 0.55 } }, col, lw * 2, a, false);
            });
        },
    });

    // ---------------- filmFeed（JS L1152） ----------------

    private static void FilmFeed() => Reg0("filmFeed", new MoveDef
    {
        Name = "フィルム送り", // JS: ae: 'flicker'
        Tags = new[] { "emotional", "glitch", "editorial" },
        W = 0.8,
        MinDur = 0.55,
        InDur = (dur, _) => J.Clamp(dur * 0.48, 0.22, 0.85),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var size = N0(it, "size");
            var seed = unchecked((int)env.Cut!.Seed);
            var b = Box(it);
            var R0 = DRange(it, b);
            var FH = (R0.Y1 - R0.Y0) + size * 0.7;
            var e = OCubic(p / 0.82);
            var off = (FH * 2.35 * (1 - e)) % FH + FH;
            off %= FH;
            var gate = new[] { (R0.Y0 + R0.Y1) / 2 - FH / 2, (R0.Y0 + R0.Y1) / 2 + FH / 2 };
            var fl = 1 - Sstep(0.55, 0.95, p);
            it["alpha"] = NN(it, "alpha", 1) * J.Clamp(p * 7) * (1 - 0.45 * fl * J.R(seed, (int)env.Step, 99));
            it["x"] = N0(it, "x") + J.Rs(seed, (int)env.Step, 98) * size * 0.02 * fl;
            if (off < 0.5) return;
            it["y"] = N0(it, "y") + off;
            it["clipY"] = new JArray(gate[0], gate[1]);
            // JS addPre：上一格画面（clip 到片门）+ 片门亮条——就地执行
            DrawPre(env, it, (env2, x) =>
            {
                var ctx = env2.Ctx;
                ctx.Save();
                ctx.ClipRect(new SKRect((float)(-env2.W), (float)gate[0], (float)(env2.W * 3), (float)FH));
                var up1 = Shallow(x);
                up1["y"] = N0(x, "y") - FH;
                CopyDraw(env2, up1, null, null);
                ctx.Restore();
                var yb = gate[0] + off - size * 0.06;
                var w = (R0.X1 - R0.X0) + size * 1.2;
                env2.Rect((R0.X0 + R0.X1) / 2 - w / 2, yb, w, size * 0.12,
                    J.Mix(Pick(env2.Sc!.Str("bg")), Pick(env2.Sc.Str("fg")), 0.18), J.Clamp(JAnim.NumNull(x, "alpha", 1)), false);
            });
        },
    });

    // ---------------- backlight（JS L1181） ----------------

    private static void Backlight() => Reg0("backlight", new MoveDef
    {
        Name = "逆光", // JS: ae: 'blur'
        Tags = new[] { "emotional", "calm" },
        W = 0.9,
        MinDur = 0.5,
        InDur = (dur, _) => J.Clamp(dur * 0.48, 0.22, 0.85),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var size = N0(it, "size");
            var bg = Pick(env.Sc!.Str("bg"));
            var col = ColOf(it);
            var glow = Pick(env.Sc.Str("accent"), env.Sc.Str("fg"));
            var rise = OCubic(p / 0.4);
            var lit = J.E.InOutSine(J.Clamp((p - 0.3) / 0.6));
            var halo = rise * (1 - Sstep(0.45, 1, p));
            var main = IsMain(it);
            if (IsHex(S(it, "color"))) it["color"] = J.Mix(J.Mix(bg, col, 0.12), col, lit);
            var scT = S(it, "strokeColor") ?? col;
            if (!FillOn(it) && IsHex(scT)) it["strokeColor"] = J.Mix(bg, scT, Math.Max(0.25, lit));
            it["alpha"] = NN(it, "alpha", 1) * J.Clamp(p * 5);
            var sh = it["shadow"];
            if ((sh == null || sh.Type == JTokenType.Null) && halo > 0.01)
                it["shadow"] = new JObject
                {
                    ["color"] = Rgba(glow, 0.95 * halo),
                    ["blur"] = Math.Min(110, size * 0.55) * (0.5 + 0.5 * rise),
                    ["dx"] = 0.0,
                    ["dy"] = 0.0,
                };
            ScaleAbout(it, 1 + 0.035 * (1 - lit));
            if (!main) return;
            // JS preLocal（字后的径向光晕）——就地执行（画在文字之前，层级正确）
            DrawPreLocal(env, it, (env2, b, x, A) =>
            {
                if (env2.Pass != "main" || halo <= 0.01) return;
                var r = Hypot(b.W, b.H) / 2 + size * 0.8;
                RadialRect(env2, b.Cx, b.Cy, r, Env.SkColor(glow, 0.3 * halo * A), Env.SkColor(glow, 0), SKBlendMode.SrcOver);
            });
        },
    });

    // ---------------- lightLeak（JS L1212） ----------------

    private static void LightLeak() => Reg0("lightLeak", new MoveDef
    {
        Name = "光漏れ", // JS: ae: 'blur'
        Tags = new[] { "emotional", "calm", "pop" },
        W = 0.9,
        MinDur = 0.5,
        InDur = (dur, _) => J.Clamp(dur * 0.5, 0.25, 0.9),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var vert = B(it, "vertical");
            var isx = N1(it, "sx");
            var isy = N1(it, "sy");
            var size = N0(it, "size");
            var b = Box(it);
            var warm = Pick(env.Sc!.Str("accent"), env.Sc.Str("fg"));
            var dark = J.Lum(Pick(env.Sc.Str("bg"))) < 0.5;
            var core = J.Mix(warm, "#ffffff", dark ? 0.45 : 0.2);
            var col = ColOf(it);
            var U0 = vert ? b.Y0 : b.X0;
            var U1 = vert ? b.Y1 : b.X1;
            var span = Math.Max(1, U1 - U0);
            var bl = Math.Min(20, size * 0.06);
            Glyphs(env, (i, g, _) =>
            {
                if (g == null) return null;
                var gp = GlyphPos(g, isx, isy);
                var f = ((vert ? gp[1] : gp[0]) - U0) / span;
                var q = J.Clamp((p - 0.55 * f) / 0.45);
                if (q <= 0) return new CharXf { Hide = true };
                if (q >= 1) return null;
                var e = OCubic(q);
                var heat = 1 - e;
                return new CharXf
                {
                    A = J.Clamp(q * 4),
                    Color = J.Mix(col, heat > 0.6 ? J.Mix(warm, core, (heat - 0.6) / 0.4) : warm, Math.Min(1, heat * 1.5)),
                    S = 1 + 0.07 * heat,
                    Blur = bl * heat * heat,
                };
            });
            if (!IsPrimary(it)) return;
            // JS postLocal（暖色漏光）——就地执行（层级在文字下）
            DrawPostLocal(env, it, (env2, bb, x, A) =>
            {
                if (env2.Pass != "main") return;
                var a = A * J.Clamp(p * 6) * (1 - Sstep(0.55, 0.95, p));
                if (a <= 0.01) return;
                var H = J.Lerp(U0 - size * 0.5, U1 + size * 0.5, J.Clamp(p / 0.62));
                var cp = Pt(vert, H, vert ? bb.Cx : bb.Cy);
                var r = Math.Max(size * 1.4, (vert ? bb.W : bb.H) * 1.1);
                var tint = dark ? warm : J.Mix(warm, "#ffffff", 0.55);
                var k = dark ? 0.55 : 0.4;
                RadialRect(env2, cp[0], cp[1], r,
                    Env.SkColor(dark ? core : tint, k * a), Env.SkColor(tint, 0), dark ? SKBlendMode.Screen : SKBlendMode.Multiply);
            });
        },
    });

    // ---------------- heatHaze（JS L1255） ----------------

    private static void HeatHaze() => Reg0("heatHaze", new MoveDef
    {
        Name = "陽炎", // JS: ae: 'slice'
        Tags = new[] { "emotional", "calm", "glitch" },
        W = 0.9,
        InDur = (dur, _) => J.Clamp(dur * 0.45, 0.2, 0.8),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var vert = B(it, "vertical");
            var size = N0(it, "size");
            var R = DRange(it, Box(it));
            var t = env.Lt;
            var amp = size * 0.36 * (1 - J.E.InOutSine(p));
            it["alpha"] = NN(it, "alpha", 1) * J.E.OutCubic(J.Clamp(p * 2.4));
            if (env.Pass != "main" || amp < 0.5) return; // 滞后的残影副本不跟着变形（省性能）
            var pad = size * 0.3;
            if (!vert)
            {
                var y0 = R.Y0 - pad;
                var h = R.Y1 - R.Y0 + pad * 2;
                var nN = (int)J.Clamp(Math.Round(h / (size * 0.085)), 8, 14);
                var bands = new JArray();
                for (var k = 0; k < nN; k++)
                    bands.Add(new JArray(y0 + h * k / nN, y0 + h * (k + 1) / nN + 0.5,
                        amp * Math.Sin(k * 0.9 + t * 17) * (0.55 + 0.45 * Math.Sin(k * 0.37 + t * 6))));
                it["bands"] = bands;
            }
            else
            {
                var x0 = R.X0 - pad;
                var w = R.X1 - R.X0 + pad * 2;
                var nN = (int)J.Clamp(Math.Round(w / (size * 0.085)), 8, 14);
                var vb = new JArray();
                for (var k = 0; k < nN; k++)
                    vb.Add(new JArray(x0 + w * k / nN, x0 + w * (k + 1) / nN + 0.5,
                        amp * Math.Sin(k * 0.9 + t * 17) * (0.55 + 0.45 * Math.Sin(k * 0.37 + t * 6))));
                it["vbands"] = vb;
            }
        },
    });

    // ---------------- crtOn（JS L1277） ----------------

    private static void CrtOn() => Reg0("crtOn", new MoveDef
    {
        Name = "CRT電源", // JS: ae: 'stretch'
        Tags = new[] { "glitch", "pop", "graphic" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var size = N0(it, "size");
            var b = Box(it);
            var hot = HotOf(env, it);
            var col = ColOf(it);
            var prim = IsPrimary(it);
            var e1 = OQuart(p / 0.3);
            var e2 = OBack(J.Clamp((p - 0.24) / 0.5), 1.6);
            var cool = Sstep(0.3, 0.95, p);
            var ky = Math.Max(0.012, e2);
            var kx = J.Lerp(1.18, 1, OCubic((p - 0.24) / 0.5));
            ScaleXY(it, kx, ky, b);
            if (IsHex(S(it, "color"))) it["color"] = J.Mix(col, hot, 1 - cool);
            it["alpha"] = NN(it, "alpha", 1) * (p < 0.24 ? 0 : 1);
            if (!prim) return;
            // JS addPost（亮点扫描线）——就地执行（层级在文字下）
            DrawPostLocal(env, it, (env2, bb, x, A) =>
            {
                var la = (1 - Sstep(0.3, 0.55, p)) * J.Clamp(p * 12);
                if (la <= 0.01) return;
                var w = (b.W + size * 0.6) * e1;
                var th = Math.Max(2, size * 0.045) * (1 + 2 * Sstep(0.2, 0.3, p));
                env2.Rect(bb.Cx - w / 2, bb.Cy - th * 2, w, th * 4, hot, la * 0.25, false);
                env2.Rect(bb.Cx - w / 2, bb.Cy - th / 2, w, th, hot, la, false);
            });
        },
    });

    // ---------------- interlace（JS L1302） ----------------

    private static void Interlace() => Reg0("interlace", new MoveDef
    {
        Name = "インターレース", // JS: ae: 'slice'
        Tags = new[] { "glitch", "graphic", "editorial" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var size = N0(it, "size");
            var hr = Math.Max(2.5, size * 0.065);
            (double V0, double V1, int N) Geo(EBox b)
            {
                var pad = size * 0.25;
                var V0 = b.Y0 - pad;
                var V1 = b.Y1 + pad;
                return (V0, V1, Math.Min(260, (int)Math.Ceiling((V1 - V0) / hr)));
            }
            var f1 = J.Clamp(p / 0.52);
            var f2 = J.Clamp((p - 0.48) / 0.52);
            // TODO(11p 管线钩子)：JS clipLocal 是隔行扫描线的并集——按字形中心所在扫描线做可见性近似
            {
                var bx = Box(it);
                var G = Geo(bx);
                var isx = N1(it, "sx");
                var isy = N1(it, "sy");
                ClipHideGlyphs(env, g =>
                {
                    var gp = GlyphPos(g, isx, isy);
                    var gy = gp[1];
                    if (gy < G.V0 || gy > G.V1 || G.N <= 0) return false;
                    var row = J.Clamp((int)((gy - G.V0) / hr), 0, G.N - 1);
                    var f = row % 2 != 0 ? f2 : f1;
                    return row * hr / Math.Max(1e-9, G.V1 - G.V0) < f;
                });
            }
            if (!IsPrimary(it)) return;
            // JS postLocal（扫描亮条）——就地执行（层级在文字下）
            DrawPostLocal(env, it, (env2, b, x, A) =>
            {
                var G = Geo(b);
                var f = p < 0.5 ? f1 : f2;
                if (f <= 0 || f >= 1) return;
                var y = J.Lerp(G.V0, G.V1, f);
                var m = size * 0.3;
                var fade = p < 0.5 ? 1 : 1 - Sstep(0.85, 0.99, f);
                env2.Rect(b.X0 - m, y - 1, b.W + m * 2, Math.Max(1.5, size * 0.02), HotOf(env2, x), A * 0.9 * fade, false);
            });
        },
    });

    // ---------------- loadingBar（JS L1334） ----------------

    private static void LoadingBar() => Reg0("loadingBar", new MoveDef
    {
        Name = "ローディング", // JS: ae: 'wipe'
        Tags = new[] { "graphic", "editorial", "glitch" },
        W = 0.8,
        MinDur = 0.7,
        InDur = (dur, _) => J.Clamp(dur * 0.55, 0.3, 1.0),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var vert = B(it, "vertical");
            var isx = N1(it, "sx");
            var isy = N1(it, "sy");
            var size = N0(it, "size");
            var seed = unchecked((int)env.Cut!.Seed);
            double[][] keys =
            {
                new[] { 0.06, 0.0 }, new[] { 0.2, 0.16 + 0.08 * J.R(seed, 1) }, new[] { 0.3, 0.24 + 0.08 * J.R(seed, 2) },
                new[] { 0.42, 0.52 + 0.1 * J.R(seed, 3) }, new[] { 0.52, 0.6 + 0.08 * J.R(seed, 4) }, new[] { 0.7, 1.0 },
            };
            var pr = 0.0;
            for (var k = 1; k < keys.Length; k++)
                if (p >= keys[k - 1][0])
                    pr = J.Lerp(keys[k - 1][1], keys[k][1], OCubic((p - keys[k - 1][0]) / (keys[k][0] - keys[k - 1][0])));
            var b0 = Box(it);
            var U0 = vert ? b0.Y0 : b0.X0;
            var U1 = vert ? b0.Y1 : b0.X1;
            var span = Math.Max(1, U1 - U0);
            Glyphs(env, (i, g, _) =>
            {
                if (g == null) return null;
                var gp = GlyphPos(g, isx, isy);
                var f = ((vert ? gp[1] : gp[0]) - U0) / span;
                var q = J.Clamp((pr - f) / 0.12 + 0.35);
                if (q <= 0) return new CharXf { Hide = true };
                if (q >= 1) return null;
                var e = OBack(q, 2);
                return vert
                    ? new CharXf { A = J.Clamp(q * 3), Dx = -(1 - e) * size * 0.25 }
                    : new CharXf { A = J.Clamp(q * 3), Dy = (1 - e) * size * 0.25 };
            });
            if (!IsPrimary(it)) return;
            var @out = Sstep(0.74, 0.96, p);
            var barA = J.Clamp(p / 0.08) * (1 - Sstep(0.88, 1, p));
            var font = JLayouts.Font0(env.St, "mono") ?? "mono";
            // JS postLocal（进度条 + % 计数）——就地执行（层级在文字下）
            DrawPostLocal(env, it, (env2, b, x, A) =>
            {
                var a = A * barA;
                if (a <= 0.01) return;
                var th = Math.Max(3, size * 0.07);
                var gap = size * 0.24;
                var L = vert ? b.H : b.W;
                var fs = Math.Max(14, size * 0.26);
                var lo = L * @out;
                var hi = L * pr;
                if (!vert)
                {
                    var y = b.Y1 + gap;
                    env2.Rect(b.X0 + lo, y, L - lo, th, env2.Sc!.Str("sub"), a * 0.3, false);
                    if (hi > lo) env2.Rect(b.X0 + lo, y, hi - lo, th, env2.Sc.Str("accent"), a, false);
                    env2.DrawItem(new JObject
                    {
                        ["text"] = Math.Round(pr * 100) + "%",
                        ["font"] = font,
                        ["size"] = fs,
                        ["x"] = b.X1 + fs * 0.4,
                        ["y"] = y + th / 2,
                        ["align"] = "left",
                        ["color"] = env2.Sc.Str("sub"),
                        ["alpha"] = a * (1 - @out),
                        ["ghost"] = false,
                    });
                }
                else
                {
                    var x0 = b.X0 - gap - th;
                    env2.Rect(x0, b.Y0 + lo, th, L - lo, env2.Sc!.Str("sub"), a * 0.3, false);
                    if (hi > lo) env2.Rect(x0, b.Y0 + lo, th, hi - lo, env2.Sc.Str("accent"), a, false);
                    env2.DrawItem(new JObject
                    {
                        ["text"] = Math.Round(pr * 100) + "%",
                        ["font"] = font,
                        ["size"] = fs,
                        ["x"] = x0 + th / 2,
                        ["y"] = b.Y1 + fs * 0.9,
                        ["color"] = env2.Sc.Str("sub"),
                        ["alpha"] = a * (1 - @out),
                        ["ghost"] = false,
                    });
                }
            });
        },
    });

    // ---------------- dither（JS L1398） ----------------

    private static void Dither() => Reg0("dither", new MoveDef
    {
        Name = "ディザ", // JS: ae: 'flicker'
        Tags = new[] { "glitch", "graphic" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var size = N0(it, "size");
            var dir = DirOf(env, 101);
            // TODO(11p 管线钩子)：JS clipLocal 是 Bayer 4×4 有序抖动格的并集——
            // 按字形中心所在格子的阈值做可见性近似
            var b = Box(it);
            var pad = size * 0.2;
            var W = b.W + pad * 2;
            var H = b.H + pad * 2;
            var cs = Math.Max(3, size * 0.075);
            while (W / cs * (H / cs) > 1400) cs *= 1.2;
            var nx = (int)Math.Ceiling(W / cs);
            var ny = (int)Math.Ceiling(H / cs);
            var ox = b.Cx - nx * cs / 2;
            var oy = b.Cy - ny * cs / 2;
            var isx = N1(it, "sx");
            var isy = N1(it, "sy");
            ClipHideGlyphs(env, g =>
            {
                var gp = GlyphPos(g, isx, isy);
                var ix = J.Clamp((int)((gp[0] - ox) / cs), 0, nx - 1);
                var iy = J.Clamp((int)((gp[1] - oy) / cs), 0, ny - 1);
                var th = (Bayer[(iy & 3) * 4 + (ix & 3)] + 0.5) / 16.0;
                var u = nx > 1 ? ix / (double)(nx - 1) : 0;
                return p * 1.35 - 0.3 * (dir > 0 ? u : 1 - u) > th;
            });
        },
    });

    // ---------------- odometer（JS L1431） ----------------

    private static void Odometer() => Reg0("odometer", new MoveDef
    {
        Name = "ドラム回転", // JS: ae: 'scramble'
        Tags = new[] { "glitch", "editorial", "pop" },
        W = 0.9,
        MinDur = 0.55,
        InDur = (dur, _) => J.Clamp(dur * 0.5, 0.25, 0.9),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var seed = SeedOf(it);
            var isy = N1(it, "sy");
            var text = S(it, "text") ?? "";
            var digits = Regex.IsMatch(text, "^[0-9\\s]+$");
            var pool = digits ? "0123456789" : JLang.Pool("signs") ?? "";
            (double Q, int R, double S) Reel(int i, int n)
            {
                var q = Stg(p, OrdLR(i, n), 0.45);
                var R = 3 + (int)(J.H(seed, i, 111) % 3);
                return (q, R, R * (1 - OBack(q, 1.15)));
            }
            CharXf Cell(JObject g, int j, double s, string? chs)
            {
                // 滚轮上第 j 个字符（0 是真字），裁到窗口内
                var f = (s - j) * 1.02;
                if (Math.Abs(f) >= 1) return new CharXf { Hide = true };
                var o = new CharXf
                {
                    Dy = f * JLayouts.D(g, "h") * isy,
                    ClipY = new[] { -0.58 - f, 0.58 - f },
                    ClipX = new[] { -2.0, 2.0 },
                    A = 1 - 0.35 * Math.Abs(f),
                };
                if (!string.IsNullOrEmpty(chs)) o.Ch = chs;
                return o;
            }
            var main = Glyphs(env, (i, g, n) =>
            {
                if (g == null) return null;
                var r = Reel(i, n);
                if (r.Q <= 0) return new CharXf { Hide = true };
                if (r.Q >= 1) return null;
                if (JLayouts.B(g, "r90")) return new CharXf { A = r.Q };
                return Cell(g, 0, r.S, null);
            });
            // JS addPre：滚轮上的相邻字符（上/下各一份副本）——就地执行
            DrawPre(env, it, (env2, x) =>
            {
                for (var side = 0; side <= 1; side++)
                {
                    var sd = side;
                    CopyDraw(env2, x, main, (i, g, n) =>
                    {
                        if (g == null) return new CharXf { Hide = true };
                        var r = Reel(i, n);
                        if (r.Q <= 0 || r.Q >= 1 || JLayouts.B(g, "r90")) return new CharXf { Hide = true };
                        var j = sd != 0 ? (int)Math.Ceiling(r.S) : (int)Math.Floor(r.S);
                        if (j < 1 || j > r.R || (sd != 0 && j == (int)Math.Floor(r.S))) return new CharXf { Hide = true };
                        return Cell(g, j, r.S, pool[(int)(J.H(seed, i, j, 112) % (uint)pool.Length)].ToString());
                    }, new JObject { ["shadow"] = null });
                }
            });
        },
    });

    // ---------------- matrixRain（JS L1475） ----------------

    private static void MatrixRain() => Reg0("matrixRain", new MoveDef
    {
        Name = "データ降下", // JS: ae: 'scramble'
        Tags = new[] { "glitch", "graphic" },
        W = 0.8,
        MinDur = 0.55,
        InDur = (dur, _) => J.Clamp(dur * 0.5, 0.25, 0.9),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var vert = B(it, "vertical");
            var isy = N1(it, "sy");
            var size = N0(it, "size");
            var seed = SeedOf(it);
            var step = (int)env.Step;
            var hot = HotOf(env, it);
            var acc = Pick(env.Sc!.Str("accent"), env.Sc.Str("fg"));
            var col = ColOf(it);
            const int K = 4;
            const double land = 0.62;
            var lay0 = JText.LayoutText(it);
            var nL = 1;
            foreach (var g in lay0.Cells) nL = Math.Max(nL, g.Li + 1);
            // 位置低的字先落地，雨丝不会盖过已落地的字
            double Key(JObject g) => 0.35 * J.R(seed, g.Value<int?>("i") ?? 0, 121)
                + 0.65 * (vert
                    ? ((g.Value<int?>("n") ?? 1) > 1 ? 1 - (g.Value<int?>("ci") ?? 0) / (double)((g.Value<int?>("n") ?? 1) - 1) : 0)
                    : (nL > 1 ? (nL - 1 - (g.Value<int?>("li") ?? 0)) / (double)(nL - 1) : 0));
            (double Q, double Y) St(JObject g)
            {
                var q = Stg(p, Key(g), 0.45);
                var Dv = size * (2.2 + J.R(seed, g.Value<int?>("i") ?? 0, 122));
                return (q, -Dv * (1 - OCubic(q / land)));
            }
            string Rnd(int i, int k)
            {
                var pl = JLang.Pool("signs") ?? "";
                return pl[(int)(J.H(seed, i, k, step, 123) % (uint)pl.Length)].ToString();
            }
            var main = Glyphs(env, (i, g, _) =>
            {
                if (g == null) return null;
                var st = St(g);
                if (st.Q <= 0) return new CharXf { Hide = true };
                if (st.Q >= 1) return null;
                if (st.Q < land) return new CharXf { Dy = st.Y, Ch = Rnd(i, 0), Color = hot };
                return new CharXf { Color = J.Mix(col, hot, 1 - OCubic((st.Q - land) / (1 - land))) };
            });
            // JS addPre：K 串尾随的雨丝副本——就地执行
            DrawPre(env, it, (env2, x) =>
            {
                for (var k = 1; k <= K; k++)
                {
                    var kk = k;
                    CopyDraw(env2, x, main, (i, g, _) =>
                    {
                        if (g == null) return new CharXf { Hide = true };
                        var st = St(g);
                        if (st.Q <= 0 || st.Q >= 1) return new CharXf { Hide = true };
                        var a = (1 - kk / (double)(K + 1)) * 0.75 * (1 - Sstep(land * 0.9, 1, st.Q)) * J.Clamp(st.Q * 6);
                        if (a <= 0.01) return new CharXf { Hide = true };
                        return new CharXf
                        {
                            Dy = st.Y - kk * JLayouts.D(g, "h") * isy * 0.92,
                            Ch = Rnd(i, kk),
                            Color = acc,
                            A = a,
                            S = 0.9,
                        };
                    }, new JObject { ["shadow"] = null, ["extrude"] = null, ["gradient"] = null, ["pattern"] = null });
                }
            });
        },
    });

    // ---------------- hatchFill（JS L1511） ----------------

    private static void HatchFill() => Reg0("hatchFill", new MoveDef
    {
        Name = "ハッチ→ベタ", // JS: ae: 'blur'
        Tags = new[] { "graphic", "editorial" },
        W = 0.9,
        InDur = (dur, _) => J.Clamp(dur * 0.45, 0.2, 0.8),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var size = N0(it, "size");
            var col = ColOf(it);
            string[] pats = { "stripes", "hatch", "dots" };
            var pat = pats[(int)(J.H(unchecked((int)env.Cut!.Seed), 131) % 3)];
            double Qf(int i, int n) => Stg(p, OrdLR(i, n), 0.45);
            var main = Glyphs(env, (i, _, n) =>
            {
                var q = Qf(i, n);
                if (q <= 0.35) return new CharXf { Hide = true };
                if (q >= 1) return null;
                return new CharXf { A = J.E.InOutSine((q - 0.35) / 0.55) };
            });
            if (!FillOn(it)) return;
            // JS addPre（网线/网点纹样 + 细描边）——就地执行
            DrawPre(env, it, (env2, x) =>
            {
                if (env2.Pass != "main") return;
                CopyDraw(env2, x, main, (i, g, n) =>
                {
                    var q = Qf(i, n);
                    if (q <= 0 || q >= 1) return new CharXf { Hide = true };
                    return new CharXf
                    {
                        A = J.Clamp(q * 5) * (1 - Sstep(0.75, 1, q)),
                        S = J.Lerp(1.1, 1, OCubic(q / 0.4)),
                    };
                }, new JObject
                {
                    ["pattern"] = pat,
                    ["patternColor"] = S(x, "color") ?? col,
                    ["patternBg"] = null,
                    ["gradient"] = null,
                    ["stroke"] = Math.Max(1, size * 0.014),
                    ["strokeColor"] = S(x, "color") ?? col,
                    ["strokeUnder"] = false,
                    ["shadow"] = null,
                    ["extrude"] = null,
                });
            });
        },
    });

    // ---------------- brushReveal（JS L1547） ----------------

    private static void BrushReveal() => Reg0("brushReveal", new MoveDef
    {
        Name = "筆払い", // JS: ae: 'wipe'
        Tags = new[] { "emotional", "editorial", "calm" },
        W = 1,
        InDur = (dur, _) => J.Clamp(dur * 0.45, 0.2, 0.85),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var vert = B(it, "vertical");
            var size = N0(it, "size");
            var seed = SeedOf(it);
            const int K = 14;
            var tail = size * 1.1;
            var pad = size * 0.2;
            // 遍历每行每根笔毛条：fn(line, v, 厚度, u起点, u终点, q)
            void Strips(JObject x, EBox b, Action<LineRng, double, double, double, double, double> fn)
            {
                var ls = LinesOf(x, b);
                var nL = ls.Count;
                foreach (var L in ls)
                {
                    var q = Stg(p, nL > 1 ? L.Li / (double)(nL - 1) : 0, nL > 1 ? 0.4 : 0);
                    if (q <= 0) continue;
                    var F = J.Lerp(L.U0 - pad, L.U1 + pad + tail, J.E.InOutSine(q));
                    var hv = size * 0.66 * (vert ? N1(x, "sx") : N1(x, "sy"));
                    var th = hv * 2 / K;
                    for (var k = 0; k < K; k++)
                    {
                        var lag = tail * Math.Pow(J.R(seed, L.Li, k, 141), 1.6);
                        var end = F - lag;
                        if (end > L.U0 - pad) fn(L, L.V - hv + k * th, th, L.U0 - pad, end, q);
                    }
                }
            }
            // TODO(11p 管线钩子)：JS clipLocal 是每行毛边笔刷条带的并集——按字形中心
            // 落在哪根毛条里做可见性近似
            {
                var bx = Box(it);
                var isx = N1(it, "sx");
                var isy = N1(it, "sy");
                var ls = LinesOf(it, bx);
                var nL = ls.Count;
                ClipHideGlyphs(env, g =>
                {
                    var gp = GlyphPos(g, isx, isy);
                    var u = vert ? gp[1] : gp[0];
                    var v = vert ? gp[0] : gp[1];
                    var L = ls.FirstOrDefault(l => l.Li == (g.Value<int?>("li") ?? 0));
                    if (L == null) return false;
                    var q = Stg(p, nL > 1 ? L.Li / (double)(nL - 1) : 0, nL > 1 ? 0.4 : 0);
                    if (q <= 0) return false;
                    var F = J.Lerp(L.U0 - pad, L.U1 + pad + tail, J.E.InOutSine(q));
                    var hv = size * 0.66 * (vert ? isx : isy);
                    var th = hv * 2 / K;
                    for (var k = 0; k < K; k++)
                    {
                        var lag = tail * Math.Pow(J.R(seed, L.Li, k, 141), 1.6);
                        var end = F - lag;
                        if (end > L.U0 - pad && v >= L.V - hv + k * th && v <= L.V - hv + (k + 1) * th && u <= end)
                            return true;
                    }
                    return false;
                });
            }
            if (!IsMain(it)) return;
            // JS postLocal（accent 笔锋尖）——就地执行（层级在文字下）
            DrawPostLocal(env, it, (env2, b, x, A) =>
            {
                var tip = size * 0.32;
                Strips(x, b, (L, v, th, u0, u1, q) =>
                {
                    var a = A * 0.9 * (1 - Sstep(0.7, 0.95, q));
                    if (a <= 0.01 || u1 > L.U1 + pad + tip) return;
                    var s0 = Math.Max(u0, u1 - tip);
                    if (vert) env2.Rect(v, s0, th * 0.92, u1 - s0, env2.Sc!.Str("accent"), a, false);
                    else env2.Rect(s0, v, u1 - s0, th * 0.92, env2.Sc!.Str("accent"), a, false);
                });
            });
        },
    });

    // ---------------- inkDrop（JS L1591） ----------------

    private static void InkDrop() => Reg0("inkDrop", new MoveDef
    {
        Name = "インク滴", // JS: ae: 'blur'
        Tags = new[] { "calm", "emotional", "graphic" },
        W = 1,
        InDur = (dur, _) => J.Clamp(dur * 0.45, 0.2, 0.85),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var isx = N1(it, "sx");
            var isy = N1(it, "sy");
            var size = N0(it, "size");
            var seed = SeedOf(it);
            double Qf(int i) => Stg(p, J.R(seed, i, 151), 0.5);
            (double Px, double Py, double R) Drop(JObject g)
            {
                var gp = GlyphPos(g, isx, isy);
                var hw = JLayouts.D(g, "w") * isx / 2;
                var hh = JLayouts.D(g, "h") * isy / 2;
                var px = gp[0] + J.Rs(seed, g.Value<int?>("i") ?? 0, 152) * hw * 0.4;
                var py = gp[1] + J.Rs(seed, g.Value<int?>("i") ?? 0, 153) * hh * 0.4;
                return (px, py, Hypot(hw + Math.Abs(px - gp[0]), hh + Math.Abs(py - gp[1])) + size * 0.08);
            }
            // TODO(11p 管线钩子)：JS clipLocal 是不规则墨渍多边形——按字形中心到墨滴
            // 中心的距离做可见性近似（波浪边缘按字形粒度离散）
            ClipHideGlyphs(env, g =>
            {
                var q = Qf(g.Value<int?>("i") ?? 0);
                if (q >= 1) return true;
                if (q <= 0.12) return false;
                var Dv = Drop(g);
                var r = Dv.R * OCubic((q - 0.12) / 0.88);
                var gx = (JLayouts.D(g, "x") + JLayouts.D(g, "vx")) * isx;
                var gy = (JLayouts.D(g, "y") + JLayouts.D(g, "vy")) * isy;
                return Hypot(gx - Dv.Px, gy - Dv.Py) <= r;
            });
            if (!IsMain(it)) return;
            // JS postLocal（落下的 accent 墨点）——就地执行（层级在文字下）
            DrawPostLocal(env, it, (env2, b, x, A) =>
            {
                foreach (var g in b.Lay.Cells)
                {
                    if (Blank(g)) continue;
                    var q = Qf(g.I);
                    if (q <= 0 || q >= 0.4) continue;
                    var Dv = Drop(CellJson(g));
                    var t = q / 0.12;
                    var r = size * 0.07 * (q < 0.12 ? OBack(t, 2) : 1 + (q - 0.12) * 4);
                    env2.Circle(Dv.Px, Dv.Py, r, env2.Sc!.Str("accent"), null, 1, A * (q < 0.12 ? 1 : 1 - (q - 0.12) / 0.28), false);
                }
            });
        },
    });

    // ---------------- quarters（JS L1641） ----------------

    private static void Quarters() => Reg0("quarters", new MoveDef
    {
        Name = "四方集結", // JS: ae: 'assemble'
        Tags = new[] { "graphic", "pop" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var size = N0(it, "size");
            var b0 = Box(it);
            var Dv = size * (GlyphN(it) > 1 ? 1.1 : 0.5) + Math.Max(b0.W, b0.H) * 0.12;
            var big = size * 30;
            var dir = DirOf(env, 161);
            int[][] Q = { new[] { -1, -1, 0 }, new[] { 1, 1, 1 }, new[] { 1, -1, 2 }, new[] { -1, 1, 3 } }; // 对角的两块同时到
            double Ek(int k) => OQuint(Stg(p, k < 2 ? 0 : 1, 0.2));
            JObject Place(JObject x, int k)
            {
                var sx = Q[k][0];
                var sy = Q[k][1];
                var e = Ek(k);
                var o = Shallow(x);
                MoveLocal(o, sx * Dv * (1 - e), sy * Dv * (1 - e));
                o["rot"] = N0(x, "rot") + dir * sx * sy * 9 * (1 - e);
                return o;
            }
            // 局部空间象限矩形（JS quad 的坐标）
            (double X0, double Y0, double X1, double Y1) Quad(EBox b, int k, double ov = 0)
            {
                var sx = Q[k][0];
                var sy = Q[k][1];
                var x0 = sx < 0 ? b.Cx - big : b.Cx - ov;
                var y0 = sy < 0 ? b.Cy - big : b.Cy - ov;
                return (x0, y0, x0 + big + ov, y0 + big + ov);
            }
            it["alpha"] = NN(it, "alpha", 1) * J.Clamp(p * 5);
            if (Ek(0) > 0.9995 && Ek(2) > 0.9995) return; // 已拼合：整行直接画
            var m = Place(it, 0);
            var dX = N0(m, "x") - N0(it, "x");
            var dY = N0(m, "y") - N0(it, "y");
            var dR = N0(m, "rot") - N0(it, "rot");
            it["x"] = N0(m, "x");
            it["y"] = N0(m, "y");
            it["rot"] = N0(m, "rot");
            var isx = N1(it, "sx");
            var isy = N1(it, "sy");
            // 象限 0 是轴对齐矩形 → 每字形 clipX/clipY 精确对位
            JAnim.CharFn clipFn;
            {
                var q0 = Quad(b0, 0);
                clipFn = ClipRectLocal(env, it, isx, isy, q0.X0, q0.Y0, q0.X1, q0.Y1);
            }
            // JS addPre：其余三块的副本（象限 canvas 裁剪全保真）——就地执行
            DrawPre(env, it, (env2, x) =>
            {
                var rest = Shallow(x);
                rest["x"] = N0(x, "x") - dX;
                rest["y"] = N0(x, "y") - dY;
                rest["rot"] = N0(x, "rot") - dR;
                for (var k = 1; k < 4; k++)
                {
                    var o = Place(rest, k);
                    var kk = k;
                    ClipPathDraw(env2, o, (ctx, pth) =>
                    {
                        var bb = Box(o);
                        var q = Quad(bb, kk, 1.2 / (env2.Scale != 0 ? env2.Scale : 1));
                        pth.AddRect(new SKRect((float)q.X0, (float)q.Y0, (float)q.X1, (float)q.Y1));
                    }, () => CopyDraw(env2, o, clipFn, null)); // 副本不带主文字的象限裁剪
                }
            });
        },
    });

    // ---------------- invertBox（JS L1685） ----------------

    private static void InvertBox() => Reg0("invertBox", new MoveDef
    {
        Name = "反転抜け", // JS: ae: 'wipe'
        Tags = new[] { "graphic", "editorial", "pop" },
        W = 0.9,
        InDur = (dur, _) => J.Clamp(dur * 0.45, 0.2, 0.8),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var size = N0(it, "size");
            var m = size * 0.16;
            var bg = Pick(env.Sc!.Str("bg"));
            var e1 = OQuart(p / 0.34);
            var e2 = IoQuart((p - 0.42) / 0.55);
            (double X0, double Y0, double X1, double Y1) Blk(EBox b) => (
                b.X0 - m,
                J.Lerp(b.Y0 - m, b.Y1 + m, e2),
                J.Lerp(b.X0 - m, b.X1 + m, e1),
                b.Y1 + m);
            // 轴对齐矩形 → 每字形 clipX/clipY 精确对位
            {
                var b = Box(it);
                var B = Blk(b);
                if (e2 <= 0)
                    Glyphs(env, (_, _, _) => new CharXf { Hide = true });
                else
                    ClipRectLocal(env, it, N1(it, "sx"), N1(it, "sy"),
                        b.X0 - size * 20, b.Y0 - size * 20, b.X0 - size * 20 + (b.W + size * 40), B.Y0);
            }
            // JS postLocal（实心色版 + 镂空 bg 副本）——就地执行（层级在文字下）
            DrawPostLocal(env, it, (env2, b, x, A) =>
            {
                var B = Blk(b);
                if (B.X1 - B.X0 < 0.5 || B.Y1 - B.Y0 < 0.5) return;
                var fill = IsHex(S(x, "color")) ? S(x, "color")! : Pick(env2.Sc!.Str("fg"));
                env2.Rect(B.X0, B.Y0, B.X1 - B.X0, B.Y1 - B.Y0, fill, A, false);
                if (env2.Pass != "main") return;
                var ctx = env2.Ctx;
                ctx.Save();
                ctx.ClipRect(new SKRect((float)B.X0, (float)B.Y0, (float)(B.X1 - B.X0), (float)(B.Y1 - B.Y0)));
                var c = Shallow(x);
                c["x"] = 0.0;
                c["y"] = 0.0;
                c["rot"] = 0.0;
                c["skew"] = 0.0;
                c["color"] = bg;
                c["strokeColor"] = bg;
                c["gradient"] = null;
                c["pattern"] = null;
                c["shadow"] = null;
                c["extrude"] = null;
                DrawIt(env2, c, null);
                ctx.Restore();
            });
        },
    });

    // ---------------- printRegister（JS L1730） ----------------

    private static void PrintRegister() => Reg0("printRegister", new MoveDef
    {
        Name = "版ズレ", // JS: ae: 'slice'
        Tags = new[] { "graphic", "pop", "editorial" },
        W = 0.9,
        InDur = (dur, _) => J.Clamp(dur * 0.45, 0.2, 0.8),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var size = N0(it, "size");
            var seed = unchecked((int)env.Cut!.Seed);
            string[] plates = { Pick(env.Sc!.Str("accent")), Pick(env.Sc.Str("accent2"), env.Sc.Str("ghostA"), env.Sc.Str("accent")) };
            var x4 = J.Clamp(p / 0.8) * 4;
            var k = Math.Min(3, (int)Math.Floor(x4));
            var e = p >= 0.8 ? 1 : (k + OBack(J.Clamp((x4 - k) / 0.3), 2.2)) / 4;
            var a0 = J.R(seed, 171) * J.TAU;
            var Dv = size * 0.42 * (1 - e);
            double[] Off(int j) => new[] { Math.Cos(a0 + j * J.TAU / 3) * Dv, Math.Sin(a0 + j * J.TAU / 3) * Dv };
            var m0 = Off(0);
            var mx = m0[0];
            var my = m0[1];
            it["x"] = N0(it, "x") + mx;
            it["y"] = N0(it, "y") + my;
            it["alpha"] = NN(it, "alpha", 1) * J.Clamp(p * 4);
            if (Dv < 0.3) return;
            var pa = 1 - Sstep(0.8, 1, p);
            // JS addPre（两块网点色版）——就地执行
            DrawPre(env, it, (env2, x) =>
            {
                if (env2.Pass != "main") return;
                for (var j = 1; j <= 2; j++)
                {
                    var o = Off(j);
                    var c = Shallow(x);
                    c["x"] = N0(x, "x") - mx + o[0];
                    c["y"] = N0(x, "y") - my + o[1];
                    CopyDraw(env2, c, null, null, new JObject
                    {
                        ["color"] = plates[j - 1],
                        ["strokeColor"] = plates[j - 1],
                        ["alpha"] = J.Clamp(JAnim.NumNull(x, "alpha", 1)) * pa,
                        ["pattern"] = j == 1 ? "dots" : "lines",
                        ["patternColor"] = plates[j - 1],
                        ["patternBg"] = null,
                        ["gradient"] = null,
                        ["shadow"] = null,
                        ["extrude"] = null,
                    });
                }
            });
        },
    });

    // ---------------- echoCount（JS L1768） ----------------

    private static void EchoCount() => Reg0("echoCount", new MoveDef
    {
        Name = "カウントイン", // JS: ae: 'zoom'
        Tags = new[] { "pop", "graphic", "glitch" },
        W = 0.8,
        MinDur = 0.55,
        InDur = (dur, _) => J.Clamp(dur * 0.5, 0.25, 0.9),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var size = N0(it, "size");
            var dir = DirOf(env, 181);
            var x4 = J.Clamp(p / 0.88) * 4;
            var b = Math.Min(3, (int)Math.Floor(x4));
            var f = x4 - b;
            var n = p >= 0.88 ? 0 : 3 - b;
            var punch = p >= 1 ? 0
                : 0.14 * Math.Exp(-f * 7) * (p < 0.88 ? 1 : 0)
                  + (p >= 0.88 ? 0.1 * Math.Exp(-(p - 0.88) * 40) * (1 - Sstep(0.9, 0.99, p)) : 0);
            ScaleAbout(it, 1 + punch);
            it["alpha"] = NN(it, "alpha", 1) * J.Clamp(p * 10);
            if (n > 0)
                it["echo"] = new JObject
                {
                    ["n"] = n,
                    ["dx"] = dir * size * 0.16,
                    ["dy"] = size * 0.11,
                    ["a"] = 0.75,
                    ["decay"] = 0.72,
                    ["color"] = Pick(env.Sc!.Str("accent"), env.Sc.Str("sub")),
                };
        },
    });

    // ---------------- liquidFill（JS L1790） ----------------

    private static void LiquidFill() => Reg0("liquidFill", new MoveDef
    {
        Name = "水位上昇", // JS: ae: 'wipe'
        Tags = new[] { "emotional", "calm", "pop" },
        W = 0.9,
        MinDur = 0.5,
        InDur = (dur, _) => J.Clamp(dur * 0.5, 0.25, 0.9),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var size = N0(it, "size");
            var t = env.Ltb;
            var ph = J.R(unchecked((int)env.Cut!.Seed), 201) * J.TAU;
            var col = ColOf(it);
            var e = J.E.InOutSine(J.Clamp(p / 0.94));
            var amp = size * 0.08 * (1 - Sstep(0.75, 1, p));
            var kx = J.TAU / (size * 1.7);
            var samples = new List<double[]>(); // [x, 波面 y]
            double Surface(EBox b, Action<double, double, int>? fn)
            {
                var m = size * 0.3;
                var lvl = J.Lerp(b.Y1 + m, b.Y0 - m - amp * 2, e);
                var nN = (int)J.Clamp(Math.Ceiling((b.W + m * 2) / (size * 0.12)), 8, 90);
                for (var k = 0; k <= nN; k++)
                {
                    var x = b.X0 - m + (b.W + m * 2) * k / (double)nN;
                    var y = lvl + amp * Math.Sin(x * kx + t * 7 + ph) + amp * 0.45 * Math.Sin(x * kx * 2.3 - t * 5);
                    fn?.Invoke(x, y, k);
                }
                return m;
            }
            // TODO(11p 管线钩子)：JS clipLocal 是波浪液面下的多边形——按字形中心在
            // 液面上/下做可见性近似（波峰按字形粒度离散）
            JAnim.CharFn clipFn;
            {
                var b = Box(it);
                Surface(b, (x, y, _) => samples.Add(new[] { x, y }));
                var isx = N1(it, "sx");
                var isy = N1(it, "sy");
                double WaveY(double x)
                {
                    if (samples.Count == 0) return double.MinValue;
                    if (x <= samples[0][0]) return samples[0][1];
                    for (var k = 1; k < samples.Count; k++)
                        if (x <= samples[k][0])
                            return J.Lerp(samples[k - 1][1], samples[k][1], (x - samples[k - 1][0]) / Math.Max(1e-9, samples[k][0] - samples[k - 1][0]));
                    return samples[^1][1];
                }
                clipFn = ClipHideGlyphs(env, g =>
                {
                    if (samples.Count == 0) return false;
                    var gp = GlyphPos(g, isx, isy);
                    return gp[0] >= samples[0][0] && gp[0] <= samples[^1][0] && gp[1] >= WaveY(gp[0]);
                });
            }
            var oa = J.Clamp(p * 6) * (1 - Sstep(0.8, 1, p));
            if (!FillOn(it) || oa <= 0.01) return;
            // JS addPre（细描边的空杯）——就地执行
            DrawPre(env, it, (env2, x) =>
            {
                if (env2.Pass != "main") return;
                CopyDraw(env2, x, clipFn, null, new JObject // 副本不带液面可见性（JS 副本无裁剪）
                {
                    ["fill"] = false,
                    ["stroke"] = Math.Max(1, size * 0.014),
                    ["strokeColor"] = IsHex(S(x, "color")) ? S(x, "color") : col,
                    ["alpha"] = J.Clamp(JAnim.NumNull(x, "alpha", 1)) * 0.45 * oa,
                    ["shadow"] = null,
                    ["extrude"] = null,
                    ["gradient"] = null,
                    ["pattern"] = null,
                });
            });
            if (!IsPrimary(it)) return;
            // JS postLocal（accent 液面线）——就地执行（层级在文字下）
            DrawPostLocal(env, it, (env2, b, x, A) =>
            {
                samples.Clear();
                Surface(b, (xx, y, _) => samples.Add(new[] { xx, y }));
                if (samples.Count == 0) return;
                env2.Line(samples.ToArray(), env2.Sc!.Str("accent"), Math.Max(1.5, size * 0.025), A * oa * (e > 0.02 ? 1 : 0), false);
            });
        },
    });

    // ---------------- windBlown（JS L1841） ----------------

    private static void WindBlown() => Reg0("windBlown", new MoveDef
    {
        Name = "風に乗って", // JS: ae: 'assemble', pieces: true（走 env.PieceFns）
        Tags = new[] { "emotional", "calm" },
        W = 0.8,
        MinDur = 0.55,
        InDur = (dur, _) => J.Clamp(dur * 0.5, 0.25, 0.9),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            if (p >= 1) return;
            var dir = DirOf(env, 191);
            var seed = SeedOf(it);
            var size = N0(it, "size");
            var nN = Math.Max(1, JText.LayoutText(it).N);
            var Dv = env.W * 0.3 + size * 2;
            if (!FillOn(it) || it["gradient"] != null)
            {
                Glyphs(env, (i, _, n) =>
                {
                    var q = Stg(p, dir > 0 ? OrdRL(i, n) : OrdLR(i, n), 0.5);
                    if (q <= 0) return new CharXf { Hide = true };
                    if (q >= 1) return null;
                    var k = 1 - OCubic(q);
                    var ph = J.R(seed, i, 193) * J.TAU;
                    return new CharXf
                    {
                        Dx = -dir * Dv * k,
                        Dy = Math.Sin(ph + k * 6) * 0.5 * size * k,
                        Rot = Math.Sin(ph * 2 + k * 7) * 120 * k,
                        A = J.Clamp(q * 4),
                    };
                });
                return;
            }
            env.PieceFns.Add((ci, pj, _, _, _, _) =>
            {
                var o = nN > 1 ? (dir > 0 ? 1 - ci / (double)(nN - 1) : ci / (double)(nN - 1)) : 0;
                var q = Stg(p, 0.55 * o + 0.45 * J.R(seed, ci, pj, 192), 0.55);
                if (q <= 0) return null;
                if (q >= 1) return JAnim.Pid;
                var e = OCubic(q);
                var k = 1 - e;
                var ph = J.R(seed, ci, pj, 193) * J.TAU;
                var dx = -dir * Dv * k * (0.8 + 0.4 * J.R(seed, ci, pj, 194));
                var dy = (Math.Sin(ph + k * 6) * 0.55 - 0.5 * J.Rs(seed, ci, pj, 195)) * size * k;
                return JAnim.Pt(dx, dy, Math.Sin(ph * 2 + k * 7) * 170 * k, 1 - 0.3 * k, 1, 0, J.Clamp(q * 4));
            });
        },
    });

    // ---------------- strokeOrder（JS L1877） ----------------

    private static void StrokeOrder() => Reg0("strokeOrder", new MoveDef
    {
        Name = "一画ずつ", // JS: ae: 'assemble', pieces: true（走 env.PieceFns）
        Tags = new[] { "calm", "emotional", "editorial" },
        W = 0.9,
        MinDur = 0.6,
        InDur = (dur, n) => J.Clamp(0.3 + n * 0.06, 0.4, Math.Max(0.4, Math.Min(1.2, dur * 0.6))),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            if (p >= 1) return;
            var nN = Math.Max(1, JText.LayoutText(it).N);
            var size = N0(it, "size");
            if (!FillOn(it) || it["gradient"] != null)
            {
                Glyphs(env, (i, _, n) =>
                {
                    var q = Stg(p, OrdLR(i, n), 0.6);
                    if (q <= 0) return new CharXf { Hide = true };
                    if (q >= 1) return null;
                    return new CharXf { A = J.E.InOutSine(q), S = J.Lerp(1.15, 1, OCubic(q)) };
                });
                return;
            }
            env.PieceFns.Add((ci, _, _, ox, oy, g) =>
            {
                // JS pieceFn 第 3 参是 piece 对象（p.cx/cy，em 空间中心）；JAnim.PieceFn 定型为 int，
                // 这里按 drawPieces 的正变换从 (ox,oy,g) 逆推出等价的 cx/cy（见 JText.DrawPieces）。
                var cx = 0.0;
                var cy = 0.0;
                if (g != null)
                {
                    var isx = N1(it, "sx");
                    var isy = N1(it, "sy");
                    var crot = (JLayouts.B(g, "r90") ? 90 : 0) * J.DEG;
                    var cr = Math.Cos(crot);
                    var sr = Math.Sin(crot);
                    var dx = ox - (JLayouts.D(g, "x") + JLayouts.D(g, "vx")) * isx;
                    var dy = oy - (JLayouts.D(g, "y") + JLayouts.D(g, "vy")) * isy;
                    cx = (dx * cr + dy * sr) / Math.Max(1e-6, size * isx);
                    cy = (-dx * sr + dy * cr) / Math.Max(1e-6, size * isy);
                }
                var gq = Stg(p, nN > 1 ? ci / (double)(nN - 1) : 0, nN > 1 ? 0.6 : 0);
                var kp = J.Clamp((cy + 0.5) * 0.62 + (cx + 0.5) * 0.38);
                var u = J.Clamp((gq - kp * 0.72) / 0.28);
                if (u <= 0) return null;
                if (u >= 1) return JAnim.Pid;
                return JAnim.Pt(0, -(1 - OCubic(u)) * size * 0.04, 0, J.Lerp(1.3, 1, OBack(u, 1.8)), 1, 0, J.Clamp(u * 3));
            });
        },
    });

    // ---------------- clockWipe（JS L1909） ----------------

    private static void ClockWipe() => Reg0("clockWipe", new MoveDef
    {
        Name = "時計回り", // JS: ae: 'wipe'
        Tags = new[] { "graphic", "pop", "editorial" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var isx = N1(it, "sx");
            var isy = N1(it, "sy");
            var size = N0(it, "size");
            var seed = SeedOf(it);
            double Qf(int i, int n) => Stg(p, OrdLR(i, n), 0.5);
            (double Gx, double Gy, double R, double A0, double Sw, bool Cw) Geo(JObject g, double q)
            {
                var gp = GlyphPos(g, isx, isy);
                return (gp[0], gp[1],
                    Hypot(JLayouts.D(g, "w") * isx, JLayouts.D(g, "h") * isy) * 0.55 + size * 0.05,
                    -Math.PI / 2,
                    J.E.InOutSine(q) * J.TAU,
                    J.R(seed, g.Value<int?>("i") ?? 0, 211) < 0.5);
            }
            // TODO(11p 管线钩子)：JS clipLocal 是绕字中心的扇形——按「最晚被扫到的四角」
            // 做可见性近似（扇形边缘按字形粒度离散）
            {
                var bx = Box(it);
                var nN = bx.Lay.N;
                ClipHideGlyphs(env, g =>
                {
                    var q = Qf(g.Value<int?>("i") ?? 0, nN);
                    if (q >= 1) return true;
                    if (q <= 0) return false;
                    var G = Geo(g, q);
                    var hw = JLayouts.D(g, "w") * isx / 2;
                    var hh = JLayouts.D(g, "h") * isy / 2;
                    double maxNeed = 0;
                    foreach (var (ex, ey) in new[] { (-hw, -hh), (hw, -hh), (-hw, hh), (hw, hh) })
                    {
                        if (Hypot(ex, ey) > G.R) return false;
                        var an = Math.Atan2(ey, ex);
                        var need = G.Cw ? ModTau(an - G.A0) : ModTau(G.A0 - an);
                        maxNeed = Math.Max(maxNeed, need);
                    }
                    return maxNeed <= G.Sw;
                });
            }
            if (!IsMain(it)) return;
            // JS postLocal（表针 + 轴点）——就地执行（层级在文字下）
            DrawPostLocal(env, it, (env2, b, x, A) =>
            {
                var n = b.Lay.N;
                var lw = Math.Max(1.5, size * 0.028);
                foreach (var g in b.Lay.Cells)
                {
                    if (Blank(g)) continue;
                    var q = Qf(g.I, n);
                    if (q <= 0 || q >= 1) continue;
                    var G = Geo(CellJson(g), q);
                    var an = G.A0 + (G.Cw ? G.Sw : -G.Sw);
                    var a = A * (1 - Sstep(0.8, 1, q)) * J.Clamp(q * 8);
                    env2.Line(new[] { new[] { G.Gx, G.Gy }, new[] { G.Gx + Math.Cos(an) * G.R * 0.9, G.Gy + Math.Sin(an) * G.R * 0.9 } },
                        env2.Sc!.Str("accent"), lw, a, false);
                    env2.Circle(G.Gx, G.Gy, lw * 1.3, env2.Sc.Str("accent"), null, 1, a, false);
                }
            });
        },
    });

    // ---------------- shadowFirst（JS L1962） ----------------

    private static void ShadowFirst() => Reg0("shadowFirst", new MoveDef
    {
        Name = "影落とし", // JS: ae: 'drop'
        Tags = new[] { "pop", "graphic", "emotional" },
        W = 0.9,
        MinDur = 0.5,
        InDur = (dur, _) => J.Clamp(dur * 0.48, 0.22, 0.85),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var size = N0(it, "size");
            var bg = Pick(env.Sc!.Str("bg"));
            var sh = J.Mix(bg, J.Lum(bg) < 0.5 ? Pick(env.Sc.Str("fg")) : "#000000", 0.28);
            const double tL = 0.72;
            double Qf(int i, int n) => Stg(p, OrdLR(i, n), 0.45);
            var main = Glyphs(env, (i, _, n) =>
            {
                var q = Qf(i, n);
                if (q <= 0.12) return new CharXf { Hide = true };
                if (q >= 1) return null;
                if (q < tL)
                {
                    var e = J.E.InQuad((q - 0.12) / (tL - 0.12));
                    return new CharXf
                    {
                        S = J.Lerp(1.75, 1, e),
                        Dx = -size * 0.22 * (1 - e),
                        Dy = -size * 0.34 * (1 - e),
                        A = J.Clamp((q - 0.12) * 4),
                    };
                }
                var u = (q - tL) / (1 - tL);
                return new CharXf { S = 1 - 0.07 * Math.Sin(Math.PI * u) * (1 - u) };
            });
            // JS addPre（落地的影子）——就地执行
            DrawPre(env, it, (env2, x) =>
            {
                CopyDraw(env2, x, main, (i, g, n) =>
                {
                    var q = Qf(i, n);
                    if (q <= 0 || q >= tL + 0.05) return new CharXf { Hide = true };
                    var e = J.Clamp(q / tL);
                    return new CharXf
                    {
                        Color = sh,
                        S = J.Lerp(0.55, 1, J.E.InQuad(e)),
                        A = J.Clamp(q * 5) * 0.85,
                        Blur = size * 0.05 * (1 - e),
                    };
                }, new JObject
                {
                    ["color"] = sh,
                    ["strokeColor"] = sh,
                    ["gradient"] = null,
                    ["pattern"] = null,
                    ["shadow"] = null,
                    ["extrude"] = null,
                });
            });
        },
    });

    // ---------------- bubbles（JS L2002） ----------------

    private static void Bubbles() => Reg0("bubbles", new MoveDef
    {
        Name = "泡", // JS: ae: 'pop'
        Tags = new[] { "pop", "emotional", "calm" },
        W = 0.8,
        MinDur = 0.55,
        InDur = (dur, _) => J.Clamp(dur * 0.5, 0.25, 0.9),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var isx = N1(it, "sx");
            var isy = N1(it, "sy");
            var size = N0(it, "size");
            var seed = SeedOf(it);
            const double tP = 0.68;
            (double Q, double Dx, double Dy) St(int i, int n)
            {
                var q = Stg(p, 0.5 * J.R(seed, i, 221) + 0.5 * OrdLR(i, n), 0.45);
                var f = J.Clamp(q / tP);
                var ph = J.R(seed, i, 222) * J.TAU;
                return (q, Math.Sin(ph + f * 9) * size * 0.1 * (1 - f), size * 1.7 * (1 - OCubic(f)));
            }
            Glyphs(env, (i, _, n) =>
            {
                var st = St(i, n);
                if (st.Q <= 0) return new CharXf { Hide = true };
                if (st.Q >= 1) return null;
                if (st.Q < tP) return new CharXf { Dx = st.Dx, Dy = st.Dy, S = 0.68, A = J.Clamp(st.Q * 6) };
                return new CharXf { S = J.Lerp(0.68, 1, OBack((st.Q - tP) / (1 - tP), 2.6)) };
            });
            if (!IsMain(it)) return;
            // JS postLocal（泡泡圈 + 高光）——就地执行（层级在文字下）
            DrawPostLocal(env, it, (env2, b, x, A) =>
            {
                var n = b.Lay.N;
                var lw = Math.Max(1.2, size * 0.018);
                foreach (var g in b.Lay.Cells)
                {
                    if (Blank(g)) continue;
                    var st = St(g.I, n);
                    if (st.Q <= 0 || st.Q >= 1) continue;
                    var gp = GlyphPos(g, isx, isy);
                    var r = Math.Max(g.W * isx, g.H * isy) * 0.5;
                    if (st.Q < tP)
                    {
                        var cx = gp[0] + st.Dx;
                        var cy = gp[1] + st.Dy;
                        var a = A * J.Clamp(st.Q * 6);
                        env2.Circle(cx, cy, r, null, env2.Sc!.Str("accent"), lw, a * 0.85, false);
                        env2.Arc(cx, cy, r * 0.72, 200, 245, env2.Sc.Str("fg"), lw * 1.2, a * 0.6, false);
                    }
                    else
                    {
                        var u = (st.Q - tP) / (1 - tP);
                        if (u < 0.5)
                            env2.Circle(gp[0], gp[1], r * (1 + 0.6 * OCubic(u / 0.5)), null, env2.Sc!.Str("accent"), lw * (1 - u), A * (1 - u / 0.5), false);
                    }
                }
            });
        },
    });

    // ---------------- tokoroten（JS L2042） ----------------

    private static void Tokoroten() => Reg0("tokoroten", new MoveDef
    {
        Name = "ところてん", // JS: ae: 'stretch'
        Tags = new[] { "pop", "graphic" },
        W = 0.7,
        MinDur = 0.5,
        InDur = (dur, _) => J.Clamp(dur * 0.48, 0.22, 0.85),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var vert = B(it, "vertical");
            var isx = N1(it, "sx");
            var isy = N1(it, "sy");
            var size = N0(it, "size");
            var b0 = Box(it);
            var U0 = vert ? b0.Y0 : b0.X0;
            var U1 = vert ? b0.Y1 : b0.X1;
            var slit = U0 - size * 0.12;
            var L = U1 - U0 + size * 0.3;
            var off = -(1 - J.E.InOutSine(J.Clamp(p / 0.9))) * L;
            var rel = size * 2;
            const double km = 0.18;
            var c = 1 - Sstep(0.55, 1, p);
            // 压缩率从缝口的 km 渐变到过缝 rel 距离后的 1；DE() 是过缝后的压缩距离
            double KAt(double e) => 1 - c * (1 - (km + (1 - km) * J.Clamp(e / rel)));
            double DE(double e)
            {
                if (e <= 0) return e * (1 - c * (1 - km));
                var r = Math.Min(e, rel);
                var k0 = 1 - c * (1 - km);
                return k0 * r + (1 - k0) * r * r / (2 * rel) + Math.Max(0, e - rel);
            }
            Glyphs(env, (i, g, _) =>
            {
                if (g == null) return null;
                var gp = GlyphPos(g, isx, isy);
                var u = vert ? gp[1] : gp[0];
                var hu = (vert ? JLayouts.D(g, "h") * isy : JLayouts.D(g, "w") * isx) / 2;
                var e = u + off - slit;
                if (e + hu < 0) return new CharXf { Hide = true };
                if (off >= -0.5 && c <= 0) return null;
                var k = Math.Max(0.05, KAt(e));
                var du = slit + DE(e) - u;
                var st = 1 + 0.3 * (1 - k);
                return vert
                    ? new CharXf { Dy = du, Sy = k, Sx = st }
                    : new CharXf { Dx = du, Sx = k, Sy = st };
            });
            // 轴对齐矩形 → 每字形 clipX/clipY 精确对位
            {
                var b = Box(it);
                var m = size * 0.3;
                if (vert)
                    ClipRectLocal(env, it, isx, isy, b.X0 - m * 4, slit, b.X0 - m * 4 + (b.W + m * 8), slit + (b.H + size * 20));
                else
                    ClipRectLocal(env, it, isx, isy, slit, b.Y0 - m * 4, slit + (b.W + size * 20), b.Y0 - m * 4 + (b.H + m * 8));
            }
            if (!IsPrimary(it)) return;
            // JS postLocal（缝口 accent 条）——就地执行（层级在文字下）
            DrawPostLocal(env, it, (env2, b, x, A) =>
            {
                var a = A * J.Clamp(p * 8) * (1 - Sstep(0.85, 1, p));
                if (a <= 0.01) return;
                var t = Math.Max(3, size * 0.07);
                var ext = size * 0.25;
                if (vert) env2.Rect(b.X0 - ext, slit - t, b.W + ext * 2, t, env2.Sc!.Str("accent"), a, false);
                else env2.Rect(slit - t, b.Y0 - ext, t, b.H + ext * 2, env2.Sc!.Str("accent"), a, false);
            });
        },
    });
}
