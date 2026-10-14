using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace JizuraLive.Engine;

/// <summary>
/// 绘制环境（移植自 09_render.js 的 makeEnv）：每个注册项收到 env 直接往 SKCanvas 画。
/// 字段与 JS env 对齐；助手方法（rect/line/drawItem…）的完整集合随渲染器移植（M2）补齐。
/// </summary>
public sealed class Env
{
    // ---------------- 每线程共享 Skia 对象（性能：SKPaint/SKPath 每帧几百次的 new 会灌满 finalizer 队列） ----------------
    // 线程本地：预览泵可同进程多线程渲染，共享实例不得跨线程互踩。
    // 使用规矩：取出的 paint/path 只在当次 Draw 调用内有效（Draw 同步消费），用完即改属性复用；
    // 绝不 Dispose 缓存实例；带 Shader 的绘制用完必须置回 null（免 shader 被 paint 拖住不释放）。
    [ThreadStatic] private static SKPaint? _paint;
    [ThreadStatic] private static SKPath? _path;

    /// <summary>线程本地共享 SKPaint（属性全量赋值后使用；Shader 用完置 null）。</summary>
    internal static SKPaint SharedPaint()
    {
        var p = _paint ??= new SKPaint();
        // 归一化描边态：Arc/RRect/Circle 的描边分支不设 Join/Cap/Miter，依赖 new 实例默认值；
        // 共享实例会把上一用户（morph Put 的 Round join 等）带进来 → 像素漂移。归一化后与 new 等价。
        p.StrokeJoin = SKStrokeJoin.Miter;
        p.StrokeCap = SKStrokeCap.Butt;
        p.StrokeMiter = 4;
        p.StrokeWidth = 0;
        return p;
    }

    /// <summary>线程本地共享 SKPath（每次取出已 Reset）。</summary>
    internal static SKPath SharedPath()
    {
        var p = _path ??= new SKPath();
        p.Reset();
        return p;
    }
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

    // ---- JS env.beat {since, len, index} 对位（09_render beatAt(plan.beats, tq)；渲染器每帧二分） ----
    // BeatHas=false ⇔ JS env.beat === null（plan 无节拍或 t 之前没有拍）——包侧按 ltb 回退式。

    /// <summary>env.beat 非 null（plan.Beats 有值且当帧 t 之前存在拍）。</summary>
    public bool BeatHas { get; set; }

    /// <summary>env.beat.since：距上一拍秒数。</summary>
    public double BeatSince { get; set; }

    /// <summary>env.beat.len：拍长秒数（beatAt 已保证 ≥0.2）。</summary>
    public double BeatLen { get; set; }

    /// <summary>env.beat.index：拍索引。</summary>
    public int BeatIndex { get; set; }

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

    // ---- 动画管线桥（05_anim / JAnim ↔ mainDraw ↔ drawItem；09_render 移植回接时由管线方写入） ----
    // JS 的 apply(env, it, p, ctx) 定型为 MoveDef.Draw(env)：it/进度/函数值字段都是 item 上的
    // 松散状态，JObject 装不下函数，统一挂 env（mainDraw 每 item 重置 CharFns/PieceFns）。

    /// <summary>当前 item（JS mainDraw 的 it；MoveDef.Draw(env) 与 TreatDef.Draw(env) 从这里读）。</summary>
    public JObject? It { get; set; }

    /// <summary>MoveDef.Draw 的进度参数 p：enter→pIn、exit→pOut、hold→amt（mainDraw 每次调用前写）。</summary>
    public double Ap { get; set; }

    /// <summary>it.charFns 对位：逐字动画函数列表（mainDraw 每 item 清空，MoveDef.Draw 往里推）。</summary>
    public List<JAnim.CharFn> CharFns { get; } = new();

    /// <summary>it.pieceFns 对位：逐片动画函数列表。</summary>
    public List<JAnim.PieceFn> PieceFns { get; } = new();

    /// <summary>combineChar 产物（JS 的 it.charFn）：drawItem 逐字应用。</summary>
    public JAnim.CharFn? CharFn { get; set; }

    /// <summary>combinePiece 产物（JS 的 it.pieceFn）：drawItem 逐片应用。</summary>
    public JAnim.PieceFn? PieceFn { get; set; }

    // ---- it.pre/it.post/it.clipFn 桥（06_layouts drawFx 的函数值字段；JObject 装不下函数）----
    // MoveDef/TreatDef/DecorDef 的 Draw(env) 在 DrawFx 之前执行，把钩子挂到 env；DrawFx 按同一
    // 顺序消费：pre（裁剪前、主文字下方）→ clip/clipY/clipFn → 文字 → post（文字上方）。
    // mainDraw 每 item 清空（对齐 JS 每 item 重建配方对象）。

    /// <summary>it.pre 对位：drawFx 开头调（JS pre(env, it)）——不被任何裁剪影响、画在主文字下方。</summary>
    public Action<Env, JObject>? PreFn { get; set; }

    /// <summary>it.post 对位：drawFx 主文字后调（JS post(env, it, bb)，bb 是主文字 bbox，可能 null）。</summary>
    public Action<Env, JObject, BBox?>? PostFn { get; set; }

    /// <summary>
    /// it.clipFn 对位：返回裁剪路径（JS 的 it.clipFn(ctx, env, it) 在 ctx 上 beginPath 后 ctx.clip()；
    /// C# 无持久 ctx 路径状态 → 返回 SKPath 由 DrawFx ClipPath）。路径在设计像素空间（DrawFx 调用点
    /// 的画布坐标系）。DrawFx 不持有引用：每次调用新建的路径由 DrawFx Dispose。
    /// </summary>
    public Func<Env, JObject, SKPath>? ClipFn { get; set; }

    // ---- 渲染器管理的内部标记（JS env.layer/hideText/allowFilter/glyphLog；09_render 移植时由渲染器写入） ----

    /// <summary>分层渲染（RenderOpts.Layer）：'back' = 背景图+歌词后面的装饰，'front'/'null' = 其余。</summary>
    public string? Layer { get; set; }

    /// <summary>env.hideText：morph 抓取期间只记不画。</summary>
    public bool HideText { get; set; }

    /// <summary>env.allowFilter：渲染器 filterOK &amp;&amp; !opt.fast。</summary>
    public bool AllowFilter { get; set; } = true;

    /// <summary>env.glyphLog：非 null 时 drawItem 抓取字形落点（モーフ原料）。</summary>
    public List<JText.GlyphLogEntry>? GlyphLog { get; set; }

    /// <summary>
    /// ghost pass 的整 pass 混合模式对位：JS 在 pass 循环里设 X.globalCompositeOperation =
    /// lum(bg)&gt;0.55 ? 'multiply' : 'source-over'，本 pass 内所有绘制（含文字）都被它染。
    /// C# 无持久 ctx 状态：Env 助手与 drawItem 从这里取 paint.BlendMode（缺省 SrcOver）。
    /// </summary>
    public SKBlendMode Blend { get; set; } = SKBlendMode.SrcOver;

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

    /// <summary>env.draw 对位（J.drawItem，03_text.js）。逐字排版绘制；env 的
    /// HideText/GlyphLog/AllowFilter 经 TextDrawOpts 传入，charFn/pieceFn 走 env 桥字段。</summary>
    public BBox? DrawItem(JObject it)
    {
        // JS 的逐字/逐片函数挂在 item 上（it.charFns → it.charFn）：只有 mainDraw 正在画的那个 item
        // 吃 env.CharFn。布局里 env.DrawItem(新 item)（kicker/deck/标签/装饰文字…）不该继承上一个
        // item 的动画——字形数不同时，手法回调里的 g0.Map[i] 会直接索引到界外（实测 knBeatLean +
        // headlineDeck / windowChrome 逐帧抛 IndexOutOfRange，整段文字都画不出来）。
        if (ReferenceEquals(It, it)) return DrawItemCore(it);
        var (c, p) = (CharFn, PieceFn);
        CharFn = null;
        PieceFn = null;
        try
        {
            return DrawItemCore(it);
        }
        finally
        {
            CharFn = c;
            PieceFn = p;
        }
    }

    private BBox? DrawItemCore(JObject it) => JText.DrawItem(this, it, new JText.TextDrawOpts
    {
        HideText = HideText,
        GlyphLog = GlyphLog,
        AllowFilter = AllowFilter,
    });

    /// <summary>env.rect：fillRect。a 是 alpha，g 是 ghost pass 可换色标记。</summary>
    public void Rect(double x, double y, double w, double h, string? c, double a = 1, bool ghostable = true)
    {
        var col = ColOf(c, ghostable);
        if (col == null || a <= 0) return;
        using var p = new SKPaint { IsAntialias = true, Color = SkColor(col, a), BlendMode = Blend };
        Ctx.DrawRect(new SKRect((float)x, (float)y, (float)(x + w), (float)(y + h)), p);
    }

    /// <summary>env.line：折线描边（miter join / butt cap）。</summary>
    public void Line(double[][] pts, string? c, double lw = 1, double a = 1, bool ghostable = true)
    {
        var col = ColOf(c, ghostable);
        if (col == null || a <= 0 || pts.Length < 2) return;
        var path = SharedPath();
        path.MoveTo((float)pts[0][0], (float)pts[0][1]);
        for (var i = 1; i < pts.Length; i++) path.LineTo((float)pts[i][0], (float)pts[i][1]);
        var p = SharedPaint();
        p.IsAntialias = true;
        p.IsStroke = true;
        p.StrokeWidth = (float)lw;
        p.StrokeJoin = SKStrokeJoin.Miter;
        p.StrokeCap = SKStrokeCap.Butt;
        p.Shader = null;
        p.ImageFilter = null;
        p.Color = SkColor(col, a);
        p.BlendMode = Blend;
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
        {
            var p = SharedPaint();
            p.IsAntialias = true;
            p.IsStroke = false;
            p.Shader = null;
            p.Color = SkColor(f, a);
            p.BlendMode = Blend;
            Ctx.DrawCircle((float)cx, (float)cy, (float)r, p);
        }
        if (s != null)
        {
            var p = SharedPaint();
            p.IsAntialias = true;
            p.IsStroke = true;
            p.StrokeWidth = (float)lw;
            p.Shader = null;
            p.Color = SkColor(s, a);
            p.BlendMode = Blend;
            Ctx.DrawCircle((float)cx, (float)cy, (float)r, p);
        }
    }

    /// <summary>env.arc：圆弧描边，角度为度（正 x 轴起、顺时针，与 Canvas2D 同向）。</summary>
    public void Arc(double cx, double cy, double r, double a0, double a1, string? c, double lw = 1, double a = 1, bool ghostable = true)
    {
        var col = ColOf(c, ghostable);
        if (col == null || a <= 0 || r <= 0) return;
        var path = SharedPath();
        path.AddArc(new SKRect((float)(cx - r), (float)(cy - r), (float)(cx + r), (float)(cy + r)), (float)a0, (float)(a1 - a0));
        var p = SharedPaint();
        p.IsAntialias = true;
        p.IsStroke = true;
        p.StrokeWidth = (float)lw;
        p.Shader = null;
        p.Color = SkColor(col, a);
        p.BlendMode = Blend;
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
            var p = SharedPaint();
            p.IsAntialias = true;
            p.IsStroke = false;
            p.Shader = null;
            p.Color = SkColor(fs, a);
            p.BlendMode = Blend;
            Ctx.DrawRoundRect(rr, p);
        }
        else if (f is SKShader shader)
        {
            var p = SharedPaint();
            p.IsAntialias = true;
            p.IsStroke = false;
            p.Color = SkColor("#FFFFFF", a);
            p.Shader = shader;
            p.BlendMode = Blend;
            Ctx.DrawRoundRect(rr, p);
            p.Shader = null;
        }
        if (s != null)
        {
            var p = SharedPaint();
            p.IsAntialias = true;
            p.IsStroke = true;
            p.StrokeWidth = (float)lw;
            p.Shader = null;
            p.Color = SkColor(s, a);
            p.BlendMode = Blend;
            Ctx.DrawRoundRect(rr, p);
        }
    }

    /// <summary>env.poly：闭合多边形填充。</summary>
    public void Poly(double[][] pts, string? c, double a = 1, bool ghostable = true)
    {
        var col = ColOf(c, ghostable);
        if (col == null || a <= 0) return;
        var path = SharedPath();
        for (var i = 0; i < pts.Length; i++)
        {
            if (i == 0) path.MoveTo((float)pts[i][0], (float)pts[i][1]);
            else path.LineTo((float)pts[i][0], (float)pts[i][1]);
        }
        path.Close();
        var p = SharedPaint();
        p.IsAntialias = true;
        p.IsStroke = false;
        p.Shader = null;
        p.Color = SkColor(col, a);
        p.BlendMode = Blend;
        Ctx.DrawPath(path, p);
    }

    /// <summary>env.blob：过各边中点的二次贝塞尔闭合曲线填充（柔边斑块）。</summary>
    public void Blob(double[][] pts, string? c, double a = 1, bool ghostable = true)
    {
        var col = ColOf(c, ghostable);
        if (col == null || a <= 0) return;
        var n = pts.Length;
        double[] Mid(int i) => new[] { (pts[i % n][0] + pts[(i + 1) % n][0]) / 2, (pts[i % n][1] + pts[(i + 1) % n][1]) / 2 };
        var path = SharedPath();
        var m0 = Mid(0);
        path.MoveTo((float)m0[0], (float)m0[1]);
        for (var i = 1; i <= n; i++)
        {
            var pt = pts[i % n];
            var m = Mid(i);
            path.QuadTo((float)pt[0], (float)pt[1], (float)m[0], (float)m[1]);
        }
        path.Close();
        var paint = SharedPaint();
        paint.IsAntialias = true;
        paint.IsStroke = false;
        paint.Shader = null;
        paint.Color = SkColor(col, a);
        paint.BlendMode = Blend;
        Ctx.DrawPath(path, paint);
    }
}
