using SkiaSharp;
using Newtonsoft.Json.Linq;

namespace JizuraLive.Engine;

/// <summary>
/// 绘制环境（移植自 09_render.js 的 makeEnv）：每个注册项收到 env 直接往 SKCanvas 画。
/// 字段与 JS env 对齐；助手方法（rect/line/drawItem…）的完整集合随渲染器移植（M2）补齐。
/// </summary>
public sealed class Env
{
    public SKCanvas Ctx { get; set; } = null!;

    /// <summary>画面区域宽高（设计像素；中央を空ける时是条带宽高，对齐 JS 的 zone ? zone.w : plan.W）。</summary>
    public int W { get; set; }
    public int H { get; set; }

    /// <summary>当前配色方案。</summary>
    public Scheme? Sc { get; set; }

    public StyleDef St { get; set; } = null!;
    public FxSettings Fx { get; set; } = new();
    public int Fps { get; set; } = 24;
    public Cut? Cut { get; set; }
    public Plan Plan { get; set; } = null!;

    /// <summary>切分本地时间（秒，从切分 start 起算）。</summary>
    public double Lt { get; set; }

    /// <summary>makeEnv 的 o.t：量化后的绝对时间（曲内秒）。type 版式的时间戳用它。</summary>
    public double T { get; set; }

    /// <summary>makeEnv 的 o.ltb：主 pass 是 tq - cut.start；fx/env 带 lag 的变体由渲染器传入。</summary>
    public double Ltb { get; set; }

    /// <summary>动画步长（koma 量化，秒）。</summary>
    public double Step { get; set; }

    /// <summary>音频能量 0..1。</summary>
    public double Energy { get; set; }

    /// <summary>距最近节拍的归一相位。</summary>
    public double Beat { get; set; }

    /// <summary>中央を空ける的条带（cut.Zone 的运行时镜像）。</summary>
    public Zone? Zone { get; set; }

    /// <summary>色差 ghost pass：'B' / 'A' / 'main'。</summary>
    public string? Pass { get; set; }

    /// <summary>ghost pass 的着色。</summary>
    public string? PassColor { get; set; }

    /// <summary>输出缩放（设计像素 → 设备像素）。</summary>
    public double Scale { get; set; } = 1;

    // ---- 派生（makeEnv 里计算） ----

    /// <summary>进场进度 0..1。</summary>
    public double PIn { get; set; }

    /// <summary>出场进度 0..1。</summary>
    public double POut { get; set; }

    /// <summary>本切分的版式参数（cut.params）。</summary>
    public JObject P => Cut?.Params ?? EmptyParams;

    private static readonly JObject EmptyParams = new();

    /// <summary>
    /// layout Draw 的返回值桥：JS render(env) 返回主体 bbox，C# LayoutDef.Draw 是 Action&lt;Env&gt;，
    /// 各 Draw 把结果写回这里，渲染器/前景装饰（drawCut 的 bb）从这里读。
    /// </summary>
    public BBox? Body { get; set; }

    /// <summary>由渲染器装配；pIn/pOut 按 cut.inDur/outDur 派生（对齐 makeEnv）。</summary>
    public static Env Make(SKCanvas ctx, Plan plan, Cut? cut, Scheme? sc, double lt, double step,
        double energy, double beat, Zone? zone, string pass, string? passColor, double scale,
        double t = 0, double ltb = 0)
    {
        var w = zone?.W ?? plan.W;
        var h = zone?.H ?? plan.H;
        var env = new Env
        {
            Ctx = ctx,
            Plan = plan,
            Cut = cut,
            Sc = sc,
            St = plan.Style,
            Fx = plan.Fx,
            Fps = plan.Fps,
            W = w,
            H = h,
            Lt = lt,
            T = t,
            Ltb = ltb,
            Step = step,
            Energy = energy,
            Beat = beat,
            Zone = zone,
            Pass = pass,
            PassColor = passColor,
            Scale = scale
        };
        if (cut != null)
        {
            env.PIn = J.Clamp(lt / Math.Max(0.01, cut.InDur));
            env.POut = cut.OutDur > 0 ? J.Clamp((lt - (cut.Dur - cut.OutDur)) / cut.OutDur) : 0;
        }
        else
        {
            env.PIn = 1;
            env.POut = 0;
        }
        return env;
    }

    // ---------------- makeEnv 的绘制 helper（09_render.js L702-832）→ SkiaSharp ----------------
    // Canvas2D 直译：globalAlpha → paint.Alpha；fillStyle/strokeStyle → SKPaint.Color（hex → SKColor）。

    /// <summary>色差 ghost pass（pass !== 'main'）。</summary>
    public bool GhostPass => Pass != "main";

    /// <summary>colOf：ghost pass 时换 passColor；g === false 的元素（版式自绘底板/整帧特效）直接跳过。</summary>
    public string? ColOf(string? c, bool ghostable) => GhostPass ? (ghostable ? PassColor : null) : c;

    /// <summary>hex（#rgb/#rrggbb/#rrggbbaa）+ alpha → SKColor。C2 风格公共帮手，JLayouts 等直接复用。</summary>
    public static SKColor SkColor(string hex, double alpha = 1)
    {
        var (r, g, b) = J.Hex(hex);
        return new SKColor((byte)r, (byte)g, (byte)b, (byte)Math.Round(J.Clamp(alpha) * 255));
    }

    /// <summary>env.draw 对位（J.drawItem，03_text.js）。逐字排版绘制，03_text 移植前不可用。</summary>
    public BBox? DrawItem(JObject it) =>
        throw new NotImplementedException("TODO 03_text: J.drawItem（env.draw 对位符号）");

    /// <summary>env.rect：fillRect。a 是 alpha，g 是 ghost pass 可换色标记。</summary>
    public void Rect(double x, double y, double w, double h, string? c, double a = 1, bool ghostable = true)
    {
        var col = ColOf(c, ghostable);
        if (col == null || a <= 0) return;
        using var p = new SKPaint { IsAntialias = true, Color = SkColor(col, a) };
        Ctx.DrawRect(new SKRect((float)x, (float)y, (float)(x + w), (float)(y + h)), p);
    }

    /// <summary>env.line：折线描边（miter join / butt cap）。</summary>
    public void Line(double[][] pts, string? c, double lw = 1, double a = 1, bool ghostable = true)
    {
        var col = ColOf(c, ghostable);
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
            Color = SkColor(col, a),
        };
        Ctx.DrawPath(path, p);
    }

    /// <summary>env.polyPartial：折线按总长画到 e∈(0,1] 处截断。</summary>
    public void PolyPartial(double[][] pts, double e, string? c, double lw = 1, double a = 1, bool ghostable = true)
    {
        if (e <= 0) return;
        var L = 0.0;
        var seg = new double[pts.Length - 1];
        for (var i = 1; i < pts.Length; i++)
        {
            var d = Math.Sqrt((pts[i][0] - pts[i - 1][0]) * (pts[i][0] - pts[i - 1][0]) + (pts[i][1] - pts[i - 1][1]) * (pts[i][1] - pts[i - 1][1]));
            seg[i - 1] = d;
            L += d;
        }
        var rem = L * J.Clamp(e);
        var @out = new List<double[]> { pts[0] };
        for (var i = 1; i < pts.Length && rem > 0; i++)
        {
            var d = seg[i - 1];
            if (rem >= d)
            {
                @out.Add(pts[i]);
                rem -= d;
            }
            else
            {
                var k = rem / d;
                @out.Add(new[] { J.Lerp(pts[i - 1][0], pts[i][0], k), J.Lerp(pts[i - 1][1], pts[i][1], k) });
                rem = 0;
            }
        }
        Line(@out.ToArray(), c, lw, a, ghostable);
    }

    /// <summary>env.circle：整圆（fill 与 stroke 可并存；r&lt;=0 或 a&lt;=0 跳过）。</summary>
    public void Circle(double cx, double cy, double r, string? fill, string? stroke, double lw = 1, double a = 1, bool ghostable = true)
    {
        if (r <= 0 || a <= 0) return;
        var f = fill != null ? ColOf(fill, ghostable) : null;
        var s = stroke != null ? ColOf(stroke, ghostable) : null;
        if (f == null && s == null) return;
        if (f != null)
            using (var p = new SKPaint { IsAntialias = true, Color = SkColor(f, a) })
                Ctx.DrawCircle((float)cx, (float)cy, (float)r, p);
        if (s != null)
            using (var p = new SKPaint { IsAntialias = true, IsStroke = true, StrokeWidth = (float)lw, Color = SkColor(s, a) })
                Ctx.DrawCircle((float)cx, (float)cy, (float)r, p);
    }

    /// <summary>env.arc：圆弧描边，角度为度（正 x 轴起、顺时针，与 Canvas2D 同向）。</summary>
    public void Arc(double cx, double cy, double r, double a0, double a1, string? c, double lw = 1, double a = 1, bool ghostable = true)
    {
        var col = ColOf(c, ghostable);
        if (col == null || a <= 0 || r <= 0) return;
        using var path = new SKPath();
        path.AddArc(new SKRect((float)(cx - r), (float)(cy - r), (float)(cx + r), (float)(cy + r)), (float)a0, (float)(a1 - a0));
        using var p = new SKPaint { IsAntialias = true, IsStroke = true, StrokeWidth = (float)lw, Color = SkColor(col, a) };
        Ctx.DrawPath(path, p);
    }

    /// <summary>
    /// env.rrect：圆角矩形。fill 可以是 hex 字符串，也可以是 SKShader（pill 的线性渐变——
    /// JS 侧传 CanvasGradient；ghost pass 时按 colOf 换 passColor，g===false 时跳过）。
    /// </summary>
    public void RRect(double x, double y, double w, double h, double r, object? fill, double a = 1, bool ghostable = true,
        string? stroke = null, double lw = 1)
    {
        if (a <= 0 || w <= 0 || h <= 0) return;
        object? f;
        if (fill == null) f = null;
        else if (GhostPass) f = ghostable ? PassColor : null;   // colOf：渐变在 ghost pass 也换成 passColor
        else f = fill;
        var s = stroke != null ? ColOf(stroke, ghostable) : null;
        if (f == null && s == null) return;
        r = Math.Min(r, Math.Min(w / 2, h / 2));
        using var rr = new SKRoundRect(new SKRect((float)x, (float)y, (float)(x + w), (float)(y + h)), (float)r);
        if (f is string fs)
        {
            using var p = new SKPaint { IsAntialias = true, Color = SkColor(fs, a) };
            Ctx.DrawRoundRect(rr, p);
        }
        else if (f is SKShader shader)
        {
            using var p = new SKPaint { IsAntialias = true, Color = SkColor("#FFFFFF", a), Shader = shader };
            Ctx.DrawRoundRect(rr, p);
        }
        if (s != null)
        {
            using var p = new SKPaint { IsAntialias = true, IsStroke = true, StrokeWidth = (float)lw, Color = SkColor(s, a) };
            Ctx.DrawRoundRect(rr, p);
        }
    }

    /// <summary>env.poly：闭合多边形填充。</summary>
    public void Poly(double[][] pts, string? c, double a = 1, bool ghostable = true)
    {
        var col = ColOf(c, ghostable);
        if (col == null || a <= 0) return;
        using var path = new SKPath();
        for (var i = 0; i < pts.Length; i++)
        {
            if (i == 0) path.MoveTo((float)pts[i][0], (float)pts[i][1]);
            else path.LineTo((float)pts[i][0], (float)pts[i][1]);
        }
        path.Close();
        using var p = new SKPaint { IsAntialias = true, Color = SkColor(col, a) };
        Ctx.DrawPath(path, p);
    }

    /// <summary>env.blob：过各边中点的二次贝塞尔闭合曲线填充（柔边斑块）。</summary>
    public void Blob(double[][] pts, string? c, double a = 1, bool ghostable = true)
    {
        var col = ColOf(c, ghostable);
        if (col == null || a <= 0) return;
        var n = pts.Length;
        double[] Mid(int i) => new[] { (pts[i % n][0] + pts[(i + 1) % n][0]) / 2, (pts[i % n][1] + pts[(i + 1) % n][1]) / 2 };
        using var path = new SKPath();
        var m0 = Mid(0);
        path.MoveTo((float)m0[0], (float)m0[1]);
        for (var i = 1; i <= n; i++)
        {
            var p = pts[i % n];
            var m = Mid(i);
            path.QuadTo((float)p[0], (float)p[1], (float)m[0], (float)m[1]);
        }
        path.Close();
        using var paint = new SKPaint { IsAntialias = true, Color = SkColor(col, a) };
        Ctx.DrawPath(path, paint);
    }
}
