// 移植自 frontend/src/engine/06_layouts.js
//
// layout 契约（JS 源 L269）：fits(n) 筛台词，plan(rng, ctx, st) 抽参数存进 cut.params 并随 AE 导出，
// Draw(env) 绘制并返回主体 bbox（JS 的 render(env) 返回值；C# Draw 是 Action<Env>，结果写回 env.Body）。
// plan 返回的参数对象逐字段 new JObject{["key"]=…} 对齐，键名是数据契约（planner/锁定行/AE 导出共用），一字不改。
// draw item 也用 JObject（与前端 duck-typed 对象一一对应）；主绘制 MainDraw/DrawFx（JS L6–182）已随
// 03_text+05_anim+09_render 回接定型（函数值字段经 Env 的动画管线桥字段传递，见 MainDraw 注释）。

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using JizuraLive.Logging;
using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace JizuraLive.LegacyEngine;

/// <summary>主体 bbox（JS mainDraw/drawItem 返回的松散对象 {x0,y0,x1,y1,boxes,cx,cy}）。</summary>
public sealed class BBox
{
    public double X0, Y0, X1, Y1, Cx, Cy;

    /// <summary>
    /// 逐字盒（设计空间，中心 + 宽高）。性能专项：原实现逐字 new JObject（每字形 ~500B，
    /// 实测 cutpass 阶段 270-410KB/帧、44MB/s 分配率的头号来源）——改为紧凑结构数组，
    /// <see cref="Boxes"/> 首次读取时才物化成与旧口径完全一致的 JObject 列表（消费方零改动）。
    /// </summary>
    public readonly struct GlyphBox
    {
        public readonly double X, Y, W, H;

        public GlyphBox(double x, double y, double w, double h)
        {
            X = x;
            Y = y;
            W = w;
            H = h;
        }
    }

    private List<GlyphBox>? _gb;
    private List<JObject>? _jo;

    /// <summary>逐字热路径追加（零 JObject 分配）。</summary>
    public void AddGlyph(double x, double y, double w, double h) =>
        (_gb ??= new List<GlyphBox>(8)).Add(new GlyphBox(x, y, w, h));

    /// <summary>逐字盒数量（不必物化即可判断空非空）。</summary>
    public int BoxCount => _gb?.Count ?? _jo?.Count ?? 0;

    /// <summary>只读紧凑视图（消费方只读 x/y/w/h 时用，不物化 JObject）。</summary>
    public IReadOnlyList<GlyphBox> Glyphs => _gb ?? (IReadOnlyList<GlyphBox>)Array.Empty<GlyphBox>();

    /// <summary>紧凑逐字盒的搬运（layered 路径把内层 bbox 原样搬出，不触发 JObject 物化）。</summary>
    public void TakeGlyphsFrom(BBox src)
    {
        if (src._gb is { Count: > 0 }) _gb = new List<GlyphBox>(src._gb);
        else if (src._jo is { Count: > 0 }) _jo = src._jo; // 旧口径：直接共享同一列表引用
    }

    /// <summary>JObject 视图（旧字段口径；首读物化，之后缓存）。</summary>
    public List<JObject> Boxes
    {
        get
        {
            if (_jo != null) return _jo;
            _jo = new List<JObject>(_gb?.Count ?? 0);
            if (_gb != null)
                foreach (var b in _gb)
                    _jo.Add(new JObject { ["x"] = b.X, ["y"] = b.Y, ["w"] = b.W, ["h"] = b.H });
            return _jo;
        }
        set
        {
            _gb = null;
            _jo = value;
        }
    }
}

/// <summary>J.measure 的返回 {w, h, lay}；lay 是 layoutText 的排版结果（03_text 移植后定型）。</summary>
public sealed class TextMetrics
{
    public double W;
    public double H;
    public JObject? Lay;
}

public static class JLayouts
{
    // ---------------- 顺序表（JS J.LAYOUT_ORDER / J.ENTER_ORDER / J.HOLD_ORDER / J.EXIT_ORDER，08_planner 依赖） ----------------

    public static readonly string[] LayoutOrder =
        { "center", "mixed", "vcols", "marquee", "tile", "scatter", "ring", "wave", "huge", "labels", "condensed", "gloss", "type", "diag", "circle", "stack", "pill" };

    public static readonly string[] EnterOrder =
        { "cut", "assemble", "slice", "type", "pop", "drop", "stretch", "wipe", "blur", "spin", "flicker", "scramble", "zoom" };

    public static readonly string[] HoldOrder = { "still", "jitter", "drift", "breathe", "wave", "glitchtick" };

    public static readonly string[] ExitOrder =
        { "cut", "explode", "fall", "drift", "slice", "wipe", "shrink", "blur", "stretch", "scatter", "glitch" };

    // ---------------- 未移植引擎部件的桩（对位符号见 TODO） ----------------

    /// <summary>
    /// J.mainDraw（06_layouts.js L6-43）：单个 item 的进场/持续/退场流水线，末尾交给 drawFx。
    /// item 上装不进 JObject 的函数值字段（charFns/pieceFns/charFn/pieceFn）走 Env 桥字段
    /// （It/Ap/CharFns/PieceFns/CharFn/PieceFn，mainDraw 每 item 清空重建）；
    /// enter.pieces / enter.cursor / *.bar 之类挂在 def 上的 flag MoveDef 没有对应属性，按 key 特判
    /// （JAnim 移植时的既定约定）。
    /// </summary>
    public static BBox? MainDraw(Env env, JObject it)
    {
        var cut = env.Cut!;
        var mi = (int)Num(it, "mi"); // JS (it.mi | 0)：向零截断（mi 可为 0.25 这类小数）
        // it.seed 缺省：h(cut.seed, mi|0 + 1, 7)（存 double 对齐 JS number）
        var seedTok = it["seed"];
        if (seedTok == null || seedTok.Type == JTokenType.Null)
            it["seed"] = (double)J.H(unchecked((int)cut.Seed), mi + 1, 7);
        env.CharFns.Clear();
        env.PieceFns.Clear();
        env.CharFn = null;
        env.PieceFn = null;
        // it.pre/it.post/it.clipFn 由调用方先设 env 桥；捕获与清空在 DrawFx 入口（消费处）
        env.It = it;
        var stagger = cut.Stagger;
        it["delay"] = (double)mi * stagger;
        var lt0 = env.Lt;
        var ltI = lt0 - (double)it["delay"]!;
        var pIn = J.Clamp(ltI / Math.Max(0.01, cut.InDur));
        var outStart = cut.Dur - cut.OutDur;
        var pOut = cut.OutDur > 0 ? J.Clamp((lt0 - outStart) / cut.OutDur) : 0;
        Reg.Enter.TryGetValue(S(it, "enter") ?? cut.Enter, out var en);
        en ??= Reg.Enter["cut"];
        Reg.Exit.TryGetValue(S(it, "exit") ?? cut.Exit, out var ex);
        ex ??= Reg.Exit["cut"];
        Reg.Hold.TryGetValue(S(it, "hold") ?? cut.Hold, out var ho);
        ho ??= Reg.Hold["still"];
        // it.plain：layout 自己画底板就置真，跳过文字特效
        if (!string.IsNullOrEmpty(cut.Treat) && !Truthy(it["plain"]) && Reg.Treat.TryGetValue(cut.Treat, out var tr) && tr != null)
        {
            try
            {
                using var _pt = new Prof.Scope("treat", cut.Treat); // 叶子归因（性能专项）
                tr.Draw?.Invoke(env); // JS apply(env, it, cut.treatP)：C# 的 Draw(env) 从 env.It / env.Cut.TreatP 取
            }
            catch (Exception e)
            {
                Log.Warn($"treat {cut.Treat}: {e.Message}");
            }
        }
        // enter.pieces：05_anim 里只有 assemble（explode/fall/drift 的 pieces+shatter 是 exit 且 Draw 内自置 shatter）
        var enPieces = en.Key == "assemble";
        if (ltI < 0 && en.Key == "cut") return null;
        if (en.Key != "cut" && (pIn < 1 || enPieces))
        {
            env.Ap = pIn;
            env.Lt = ltI;
            try
            {
                using var _pe = new Prof.Scope("enter", en.Key); // 叶子归因（性能专项）
                en.Draw?.Invoke(env);
            }
            finally
            {
                env.Lt = lt0;
            }
        }
        if (ltI < 0 && !enPieces) return null;
        var amt = J.Clamp((ltI - cut.InDur * 0.85) / 0.25) * (1 - pOut);
        if (amt > 0 && !Truthy(it["noHold"]))
        {
            env.Ap = amt;
            using var _ph = new Prof.Scope("hold", ho.Key); // 叶子归因（性能专项）
            ho.Draw?.Invoke(env);
        }
        if (pOut > 0 && ex.Key != "cut")
        {
            env.Ap = pOut;
            using var _px = new Prof.Scope("exit", ex.Key); // 叶子归因（性能专项）
            ex.Draw?.Invoke(env);
        }
        env.CharFn = JAnim.CombineChar(env.CharFns);
        env.PieceFn = JAnim.CombinePiece(env.PieceFns);
        using var _pf = new Prof.Scope("drawfx", S(it, "enter") ?? cut.Enter); // 叶子归因（性能专项）
        return DrawFx(env, it);
    }

    /// <summary>
    /// J.drawFx（06_layouts.js L46-182）：裁剪、streak/echo 副本、wipeBar、打字光标都叠在这一层。
    /// it.pre/it.post/it.clipFn 经 env 桥（Env.PreFn/PostFn/ClipFn，JObject 装不下函数）：
    /// pre 在所有裁剪之前（不被裁剪、主文字下方）、clipFn 包住文字绘制、post 在主文字之后（上方）。
    /// </summary>
    public static BBox? DrawFx(Env env, JObject it)
    {
        // it.pre/it.post/it.clipFn：调用方（表现包/测试）先设 env 桥再经 MainDraw 进来。
        // 入口捕获为局部再从 env 清空（JS 里这些是配方对象自带的函数值字段，每个 item 自带一份；
        // 清空保证下一 item 从干净状态开始），消费点全部走局部变量
        var preFn = env.PreFn;
        var postFn = env.PostFn;
        var clipFn = env.ClipFn;
        env.PreFn = null;
        env.PostFn = null;
        env.ClipFn = null;
        var ctx = env.Ctx;
        BBox? bb = null;
        // 副本共用的 TextDrawOpts：env 的内部标记原样透传
        JText.TextDrawOpts Opts() => new()
        {
            HideText = env.HideText,
            GlyphLog = env.GlyphLog,
            AllowFilter = env.AllowFilter,
        };

        void Draw()
        {
            var opts = Opts();
            var streak = it["streak"] as JObject;
            if (streak != null && D(streak, "a") > 0.01)
            {
                using var _ps = new Prof.Scope("fx", "streak"); // 叶子归因（性能专项）
                var lay0 = GetLayShared(it); // 拖尾副本共享排版，免每帧每副本重排
                var n = I(streak, "n");
                for (var k = n; k >= 1; k--)
                {
                    var c = CloneShared(it, lay0);
                    c["x"] = Num(it, "x") + D(streak, "dx") * k;
                    c["y"] = Num(it, "y") + D(streak, "dy") * k;
                    c["alpha"] = NumNull(it, "alpha", 1) * D(streak, "a") * (1 - k / (double)(n + 1));
                    c["shadow"] = null;
                    c["extrude"] = null;
                    JText.DrawItem(env, c, new JText.TextDrawOpts
                    {
                        HideText = opts.HideText,
                        GlyphLog = opts.GlyphLog,
                        AllowFilter = opts.AllowFilter,
                        NoPieces = true, // JS 副本 pieceFn:null
                    });
                }
            }
            var echo = it["echo"] as JObject;
            if (echo != null && I(echo, "n") > 0) // echo：主文字背后几层错位副本，描边或染色
            {
                using var _pe = new Prof.Scope("fx", "echo"); // 叶子归因（性能专项）
                var lay0 = GetLayShared(it);
                var n = I(echo, "n");
                for (var k = n; k >= 1; k--)
                {
                    var c = CloneShared(it, lay0);
                    c["x"] = Num(it, "x") + D(echo, "dx") * k;
                    c["y"] = Num(it, "y") + D(echo, "dy") * k;
                    c["size"] = Num(it, "size") * Math.Pow(D(echo, "scale", 1), k);
                    c["rot"] = Num(it, "rot") + D(echo, "rot") * k;
                    c["alpha"] = NumNull(it, "alpha", 1) * D(echo, "a", 0.5) * Math.Pow(D(echo, "decay", 0.7), k - 1);
                    c["shadow"] = null;
                    c["extrude"] = null;
                    c["pattern"] = null;
                    c["gradient"] = null;
                    c["color"] = OrStr(S(echo, "color"), S(it, "color"));
                    if (Truthy(echo["outline"]))
                    {
                        c["fill"] = false;
                        c["stroke"] = Math.Max(1, Num(it, "size") * 0.012);
                        c["strokeColor"] = OrStr(S(echo, "color"), S(it, "color"));
                    }
                    JText.DrawItem(env, c, new JText.TextDrawOpts
                    {
                        HideText = opts.HideText,
                        GlyphLog = opts.GlyphLog,
                        AllowFilter = opts.AllowFilter,
                        NoPieces = true,
                    });
                }
            }
            using var _pm = new Prof.Scope("fx", "main"); // 叶子归因（性能专项）
            var r = JText.DrawItem(env, it, opts);
            if (r != null) bb = r;
        }

        // JS it.pre：裁剪应用之前画（钩子内容不被 clip/clipY/clipFn 裁）；JS try/catch console.warn 对位
        if (preFn != null)
        {
            try
            {
                preFn(env, it);
            }
            catch (Exception e)
            {
                Log.Warn($"item pre: {e.GetType().Name}: {e.Message}\n{FirstStack(e)}");
            }
        }
        // JS 这几处是 ctx.rect(x, y, w, h)：SKRect 不是 (x,y,w,h) 而是 left/top/right/bottom，
        // 必须写成 (x, y, x+w, y+h)——否则裁剪带按 [x, w] 收窄，退场「括号合上」这类从中间夹走的
        // 效果会变成从左往右裁掉右半边。
        if (it["clip"] is JArray clip)
        {
            ctx.Save();
            var x0 = (float)(double)clip[0]!;
            var x1 = (float)(double)clip[1]!;
            ctx.ClipRect(new SKRect(x0, -env.H, x1, env.H * 2f));
        }
        if (it["clipY"] is JArray clipY)
        {
            ctx.Save();
            var y0 = (float)(double)clipY[0]!;
            var y1 = (float)(double)clipY[1]!;
            ctx.ClipRect(new SKRect(-env.W * 1f, y0, env.W * 2f, y1));
        }
        // JS it.clipFn：ctx.beginPath() 后由钩子在 ctx 上构造路径再 ctx.clip()——C# 返回 SKPath 由这里裁
        if (clipFn != null)
        {
            using var clipPath = clipFn(env, it);
            ctx.Save();
            ctx.ClipPath(clipPath);
        }
        if (it["vbands"] is JArray vbands)
        {
            foreach (var band in vbands)
            {
                var b = (JArray)band!;
                ctx.Save();
                ctx.ClipRect(new SKRect((float)(double)b[0]!, -env.H * 2f, (float)(double)b[1]!, env.H * 3f));
                ctx.Translate(0, (float)(double)b[2]!);
                Draw();
                ctx.Restore();
            }
        }
        else if (it["bands"] is JArray bands)
        {
            foreach (var band in bands)
            {
                var b = (JArray)band!;
                ctx.Save();
                ctx.ClipRect(new SKRect(-env.W * 2f, (float)(double)b[0]!, env.W * 3f, (float)(double)b[1]!));
                ctx.Translate((float)(double)b[2]!, 0);
                Draw();
                ctx.Restore();
            }
            // bands 区间之外的部分（JS ctx.rect(-W*2, -H*3, W*5, lo+H*3) / (-W*2, hi, W*5, H*4)）
            var lo = (double)bands[0]![0]!;
            var hi = (double)bands[bands.Count - 1]![1]!;
            using var outPath = new SKPath();
            outPath.AddRect(new SKRect(-env.W * 2f, -env.H * 3f, env.W * 3f, (float)lo));
            outPath.AddRect(new SKRect(-env.W * 2f, (float)hi, env.W * 3f, (float)(hi + env.H * 4)));
            ctx.Save();
            ctx.ClipPath(outPath);
            Draw();
            ctx.Restore();
        }
        else Draw();
        if (clipFn != null) ctx.Restore();
        if (it["clipY"] != null) ctx.Restore();
        if (it["clip"] != null) ctx.Restore();
        // JS it.post(env, it, bb)：主文字之后画（bb 是主文字 bbox，可能 null）
        if (postFn != null)
        {
            try
            {
                postFn(env, it, bb);
            }
            catch (Exception e)
            {
                Log.Warn($"item post: {e.GetType().Name}: {e.Message}\n{FirstStack(e)}");
            }
        }
        if (it["wipeBar"] is JObject wb)
            env.Rect(Num(wb, "x") - Math.Max(4, Num(it, "size") * 0.035), Num(it, "y") - Num(wb, "h") / 2,
                Math.Max(8, Num(it, "size") * 0.07), Num(wb, "h"), env.Sc!.Str("accent"), 1, false);
        var cursorTok = it["cursorAt"];
        if (cursorTok != null && cursorTok.Type != JTokenType.Null && (double)cursorTok >= 0)
        {
            var m = Measure(it);
            var cursorAt = (int)(double)cursorTok;
            var blink = cursorAt >= (m.Lay["N"]?.Value<int>() ?? 0) ? (int)env.Step % 2 == 0 : true;
            if (blink)
            {
                double x;
                if (bb is { Boxes.Count: > 0 })
                {
                    var last = bb.Boxes[^1];
                    x = Num(it, "x") + (double)last["x"]! + (double)last["w"]! / 2 + Num(it, "size") * 0.08;
                }
                else x = S(it, "align") == "left" ? Num(it, "x") : Num(it, "x") - m.W / 2;
                env.Rect(x, Num(it, "y") - Num(it, "size") * 0.45, Num(it, "size") * 0.5, Num(it, "size") * 0.9,
                    env.Sc!.Str("accent"), 1);
            }
        }
        return bb;
    }

    /// <summary>drawFx 副本：DeepClone + 排版共享（对位 JS Object.assign 克隆带走 _lay）。</summary>
    private static JObject CloneShared(JObject src, JText.TextLayout lay)
    {
        var c = (JObject)src.DeepClone();
        JText.ShareLay(c, lay);
        return c;
    }

    /// <summary>drawFx 副本排版源（JS it._lay || (it._lay = J.layoutText(it))）。</summary>
    private static JText.TextLayout GetLayShared(JObject it) => JText.GetLay(it);

    /// <summary>J.measure（03_text.js L585）→ {w, h, lay}，JText.Measure 的 JObject-lay 对位（TextMetrics.Lay 是
    /// TextLayout.ToJson 的 JSON 同构，只读 N/W/H 的调用方零成本穿透）。</summary>
    public static TextMetrics Measure(JObject it)
    {
        var r = JText.Measure(it);
        return new TextMetrics { W = r.W, H = r.H, Lay = r.Lay.ToJson() };
    }

    /// <summary>J.fitSize（03_text.js L603）：求正好装进盒子的字号。</summary>
    public static double FitSize(string text, string font, double maxW, double maxH, JObject? opt = null) =>
        JText.FitSize(text, font, maxW, maxH, opt);

    /// <summary>J.fastRow（03_text.js L403）：密排小字一次画一行；只该在 main pass 用，sp 为每字额外间距 px。</summary>
    public static void FastRow(Env env, string text, string font, double size, double x, double y, double sp,
        string color, double alpha, string align = "left") =>
        JText.FastRow(env, text, font, size, x, y, sp, color, alpha, align);

    /// <summary>J.metrics.adv（03_text.js）：单字 advance。</summary>
    public static double MetricsAdv(string font, string ch) => JText.Metrics.Adv(font, ch);

    /// <summary>drawFx 告警的前几行堆栈（JS console.warn(e) 带错误体的对位；定位钩子异常用）。</summary>
    private static string? FirstStack(Exception e) => string.Join(" <- ", (e.StackTrace ?? "")
        .Split('\n')
        .Where(l => l.Contains("JizuraLive"))
        .Select(l => l.Trim().Replace(Environment.NewLine, "")));

    /// <summary>drawFx 的松散数值读取（JS 的 it.k 直接读，JAnim 内部帮手同型复用）。</summary>
    private static double Num(JObject o, string k) => JAnim.Num(o, k);

    private static double NumNull(JObject o, string k, double d) => JAnim.NumNull(o, k, d);

    /// <summary>J.FONTS[k] 存在性（02_fonts.js 的字体目录 → JFonts.Table）。fontsOf 过滤无效字体 key 用。</summary>
    private static bool FontExists(string k) => JFonts.Has(k);

    // ---------------- 注册 ----------------

    static JLayouts() => Install();

    /// <summary>幂等注册本文件全部 layout（含 special 的 title/interlude；special 不进 Reg.LayoutOrder，
    /// 注册顺序即 JS J.LAYOUT_ORDER）。</summary>
    public static void Install()
    {
        RegisterCenter();
        RegisterMixed();
        RegisterVcols();
        RegisterMarquee();
        RegisterTile();
        RegisterScatter();
        RegisterRing();
        RegisterWave();
        RegisterHuge();
        RegisterLabels();
        RegisterCondensed();
        RegisterGloss();
        RegisterType();
        RegisterDiag();
        RegisterCircle();
        RegisterStack();
        RegisterPill();
        RegisterTitle();
        RegisterInterlude();
    }

    // ---------------- layout：常规 17 种 ----------------

    private static void RegisterCenter()
    {
        Reg.Register("layout", "center", new LayoutDef
        {
            Name = "中央",
            Fits = _ => true,
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, rng.Chance(0.7) ? "display" : "serif")),
                ["sx"] = rng.Pick(new double[] { 1, 1, 1, 1.25, 1.45, 0.78 }),
                ["track"] = rng.Range(0.02, 0.14),
                ["sub"] = rng.Chance(0.45),
                ["under"] = rng.Chance(0.3),
                ["accent"] = rng.Chance(0.18),
                ["ox"] = rng.Range(-0.05, 0.05),
                ["oy"] = rng.Range(-0.06, 0.06),
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var P = env.P;
                var cut = env.Cut!;
                var text = SplitLines(cut.Text, W < H ? 5 : 11);
                var font = S(P, "font")!;
                var size = Math.Min(
                    FitSize(text, font, W * 0.84, H * 0.5, new JObject { ["sx"] = D(P, "sx"), ["track"] = D(P, "track"), ["lead"] = 1.2 }),
                    H * 0.33);
                var bb = MainDraw(env, new JObject
                {
                    ["text"] = text,
                    ["font"] = font,
                    ["size"] = size,
                    ["x"] = W / 2 + D(P, "ox") * W,
                    ["y"] = H / 2 + D(P, "oy") * H,
                    ["sx"] = D(P, "sx"),
                    ["track"] = D(P, "track"),
                    ["lead"] = 1.2,
                    ["color"] = B(P, "accent") ? sc.Str("accent") : sc.Str("fg"),
                });
                if (bb != null && B(P, "sub") && cut.LineText != cut.Text)
                {
                    env.DrawItem(new JObject
                    {
                        ["text"] = cut.LineText,
                        ["font"] = Font0(env.St, "body"),
                        ["size"] = J.Clamp(H * 0.026, 16, 34),
                        ["x"] = W / 2 + D(P, "ox") * W,
                        ["y"] = bb.Y1 + H * 0.07,
                        ["track"] = 0.22,
                        ["color"] = sc.Str("sub"),
                        ["alpha"] = J.E.OutCubic(env.PIn),
                        ["ghost"] = false,
                    });
                }
                if (bb != null && B(P, "under"))
                {
                    var e = J.E.OutExpo(env.PIn * 1.2 - 0.2);
                    var o = J.E.InCubic(env.POut);
                    if (e > 0 && o < 1)
                        env.Line(
                            new[] { new[] { J.Lerp(bb.X0, bb.X1, o), bb.Y1 + size * 0.14 }, new[] { J.Lerp(bb.X0, bb.X1, e), bb.Y1 + size * 0.14 } },
                            sc.Str("accent"), Math.Max(2, size * 0.03), 1);
                }
                env.Body = bb;
            },
        });
    }

    private static void RegisterMixed()
    {
        Reg.Register("layout", "mixed", new LayoutDef
        {
            Name = "大小ミックス",
            Fits = n => n >= 2 && n <= 16,
            Plan = (rng, cut, st) => new JObject
            {
                ["fontBig"] = rng.Pick(FontsOf(st, "display", "serif")),
                ["fontSmall"] = rng.Pick(FontsOf(st, "serif", "body")),
                ["mode"] = rng.Pick(new[] { "line", "stair", "line", "wave" }),
                ["rotAmp"] = rng.Range(2, 10),
                ["smallK"] = rng.Range(0.42, 0.6),
                ["accentIdx"] = rng.Int(0, 20),
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var P = env.P;
                var cut = env.Cut!;
                var seed = (int)cut.Seed;
                var fontBig = S(P, "fontBig")!;
                var fontSmall = S(P, "fontSmall")!;
                var smallK = D(P, "smallK");
                var accentIdx = I(P, "accentIdx");
                var rotAmp = D(P, "rotAmp");
                var mode = S(P, "mode");
                var chars = CodePoints(Regex.Replace(cut.Text, @"\s+", ""));
                var n = chars.Count;
                var rows = n > 9 ? 2 : 1;
                var perRow = (int)Math.Ceiling(n / (double)rows);
                var items = chars.Select((ch, i) =>
                {
                    var k = J.IsKanji(ch[0]) ? 1.0 : J.IsKata(ch[0]) ? 0.88 : J.IsLatin(ch[0]) ? 0.8 : J.IsPunct(ch[0]) ? 0.42 : smallK + J.R(seed, i, 3) * 0.14;
                    if (J.IsSmallKana(ch[0])) k *= 0.8;
                    var font = J.IsKanji(ch[0]) || J.IsKata(ch[0]) ? fontBig : J.R(seed, i, 4) < 0.55 ? fontSmall : fontBig;
                    return new { ch, k, font, w = MetricsAdv(font, ch) * k * 0.96 };
                }).ToList();
                BBox? bbAll = null;
                for (var r = 0; r < rows; r++)
                {
                    var row = items.Skip(r * perRow).Take(perRow).ToList();
                    var sumW = row.Sum(c => c.w);
                    var bas = Math.Min(W * 0.86 / sumW, H * (rows > 1 ? 0.3 : 0.4));
                    var x = W / 2 - sumW * bas / 2;
                    var baseline = H / 2 + bas * 0.38 + (r - (rows - 1) / 2.0) * bas * 1.05;
                    for (var j = 0; j < row.Count; j++)
                    {
                        var c = row[j];
                        var i = r * perRow + j;
                        var size = c.k * bas;
                        var y = baseline - size / 2 + J.Rs(seed, i, 5) * bas * 0.06;
                        if (mode == "stair") y += (j - (row.Count - 1) / 2.0) * bas * 0.12;
                        if (mode == "wave") y += Math.Sin(j * 1.1) * bas * 0.1;
                        var it = new JObject
                        {
                            ["text"] = c.ch,
                            ["font"] = c.font,
                            ["size"] = size,
                            ["x"] = x + c.w * bas / 2,
                            ["y"] = y,
                            ["rot"] = J.Rs(seed, i, 6) * rotAmp,
                            ["color"] = i == accentIdx % n && !J.IsKanji(c.ch[0]) ? sc.Str("accent") : sc.Str("fg"),
                            ["mi"] = i,
                        };
                        bbAll = UnionBb(bbAll, MainDraw(env, it));
                        x += c.w * bas;
                    }
                }
                env.Body = bbAll;
            },
        });
    }

    private static void RegisterVcols()
    {
        Reg.Register("layout", "vcols", new LayoutDef
        {
            Name = "縦書き",
            Fits = n => n <= 18,
            Plan = (rng, cut, st) =>
            {
                var n = GlyphCount(cut.Text);
                var variant = n <= 5 ? rng.Pick(new[] { "repeat", "repeat", "split" }) : n <= 9 ? rng.Pick(new[] { "split", "repeat" }) : "split";
                return new JObject
                {
                    ["variant"] = variant,
                    ["cols"] = n <= 4 ? rng.Pick(new[] { 3, 5, 5 }) : 3,
                    ["font"] = rng.Pick(FontsOf(st, "serif", "serif", "display")),
                    ["side"] = rng.Pick(new[] { "same", "outline", "dim" }),
                    ["perCol"] = rng.Int(3, 6),
                };
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var P = env.P;
                var text = Regex.Replace(env.Cut!.Text, @"\s+", "");
                var n = GlyphCount(text);
                var font = S(P, "font")!;
                var variant = S(P, "variant");
                var sideMode = S(P, "side");
                if (variant == "repeat" && n <= 9)
                {
                    var cols = I(P, "cols");
                    var size = Math.Min(H * 0.8 / (n * 1.04), W * 0.86 / (cols * 1.75));
                    BBox? bb = null;
                    var mid = (cols - 1) / 2.0;
                    for (var i = 0; i < cols; i++)
                    {
                        var side = i != Math.Round(mid);
                        var it = new JObject
                        {
                            ["text"] = text,
                            ["font"] = font,
                            ["size"] = size,
                            ["x"] = W / 2 + (i - mid) * size * 1.75,
                            ["y"] = H / 2.0,
                            ["vertical"] = true,
                            ["track"] = 0.04,
                            ["color"] = sc.Str("fg"),
                            ["mi"] = Math.Abs(i - mid) * 2,
                        };
                        if (side && sideMode == "outline")
                        {
                            it["fill"] = false;
                            it["stroke"] = Math.Max(1.2, size * 0.012);
                        }
                        if (side && sideMode == "dim") it["alpha"] = 0.38;
                        var r = MainDraw(env, it);
                        if (!side) bb = r;
                    }
                    env.Body = bb;
                    return;
                }
                var per = (int)Math.Max(2, Math.Min(I(P, "perCol") + 1, Math.Ceiling(n / Math.Ceiling(n / 7.0))));
                var colsArr = new List<string>();
                var arr = CodePoints(text);
                for (var i = 0; i < arr.Count; i += per) colsArr.Add(string.Concat(arr.Skip(i).Take(per)));
                var t2 = string.Join("\n", colsArr);
                var size2 = Math.Min(H * 0.78 / (per * 1.03), W * 0.8 / (colsArr.Count * 1.4));
                var colH = per * size2 * 1.03;
                env.Body = MainDraw(env, new JObject
                {
                    ["text"] = t2,
                    ["font"] = font,
                    ["size"] = size2,
                    ["x"] = W / 2.0,
                    ["y"] = H / 2.0 - colH / 2,
                    ["vertical"] = true,
                    ["lead"] = 1.4,
                    ["align"] = "left",
                    ["track"] = 0.03,
                    ["color"] = sc.Str("fg"),
                });
            },
        });
    }

    private static void RegisterMarquee()
    {
        Reg.Register("layout", "marquee", new LayoutDef
        {
            Name = "流れる帯",
            Fits = n => n <= 12,
            Plan = (rng, cut, st) => new JObject
            {
                ["rows"] = rng.Pick(new[] { 2, 4, 4, 2 }),
                ["rowStyle"] = rng.Pick(new[] { "outline", "dim", "box" }),
                ["speed"] = rng.Range(0.5, 1.2),
                ["font"] = rng.Pick(FontsOf(st, "display")),
                ["sx"] = rng.Pick(new double[] { 1.25, 1.45, 1.6 }),
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var P = env.P;
                var text = env.Cut!.Text;
                var font = S(P, "font")!;
                var sx = D(P, "sx");
                var size = Math.Min(FitSize(text, font, W * 0.84, H * 0.3, new JObject { ["sx"] = sx, ["track"] = 0.05 }), H * 0.3);
                var rs = size * 0.42;
                var lb = env.Ltb;
                var unit = text + "　";
                var period = Measure(new JObject { ["text"] = unit, ["font"] = font, ["size"] = rs, ["sx"] = sx, ["track"] = 0.05 }).W + rs * 0.05;
                var reps = (int)Math.Ceiling(W * 2.4 / period) + 1;
                var ys = I(P, "rows") == 2 ? new[] { -1, 1 } : new[] { -2, -1, 1, 2 };
                for (var r = 0; r < ys.Length; r++)
                {
                    var k = ys[r];
                    var y = H / 2 + Math.Sign(k) * (size * 0.5 + rs * 0.95) + (Math.Abs(k) - 1) * Math.Sign(k) * rs * 1.25;
                    var a = J.Clamp((lb - Math.Abs(k) * 0.05) / 0.12);
                    if (a <= 0) continue;
                    var dir = r % 2 == 1 ? 1 : -1;
                    var off = (lb * D(P, "speed") * W * 0.22 * dir + r * period * 0.37) % period;
                    off = (off + period) % period - period / 2;
                    var row = new JObject
                    {
                        ["text"] = Repeat(unit, reps),
                        ["font"] = font,
                        ["size"] = rs,
                        ["sx"] = sx,
                        ["track"] = 0.05,
                        ["x"] = W / 2 + off,
                        ["y"] = y,
                        ["ghost"] = false,
                        ["alpha"] = a,
                    };
                    var rowStyle = S(P, "rowStyle");
                    if (rowStyle == "outline")
                    {
                        row["fill"] = false;
                        row["stroke"] = Math.Max(1.2, rs * 0.02);
                        row["strokeColor"] = sc.Str("fg");
                        row["alpha"] = a * 0.9;
                    }
                    else if (rowStyle == "dim")
                    {
                        row["color"] = sc.Str("sub");
                        row["alpha"] = a * 0.35;
                    }
                    else
                    {
                        env.Rect(-10, y - rs * 0.62, W + 20, rs * 1.24, sc.Str("ink"), a, false);
                        row["color"] = sc.Str("bg");
                    }
                    env.DrawItem(row);
                }
                env.Body = MainDraw(env, new JObject
                {
                    ["text"] = text,
                    ["font"] = font,
                    ["size"] = size,
                    ["x"] = W / 2.0,
                    ["y"] = H / 2.0,
                    ["sx"] = sx,
                    ["track"] = 0.05,
                    ["color"] = sc.Str("fg"),
                });
            },
        });
    }

    private static void RegisterTile()
    {
        Reg.Register("layout", "tile", new LayoutDef
        {
            Name = "敷き詰め",
            Fits = n => n <= 12,
            Plan = (rng, cut, st) => new JObject
            {
                ["unit"] = rng.Pick(new[] { "chunk", "line", "chunk" }),
                ["knock"] = rng.Pick(new[] { "stroke", "box" }),
                ["flicker"] = rng.Chance(0.6),
                ["font"] = rng.Pick(FontsOf(st, "display")),
                ["tileFont"] = rng.Pick(FontsOf(st, "serif", "body", "display")),
                ["rowsN"] = rng.Pick(new[] { 12, 14, 16, 18 }),
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var P = env.P;
                var cut = env.Cut!;
                var text = cut.Text;
                var lb = env.Ltb;
                var seed = (int)cut.Seed;
                var font = S(P, "font")!;
                var tileFont = S(P, "tileFont")!;
                var unitText = (S(P, "unit") == "line" ? cut.LineText : text) + "　";
                var rowsN = I(P, "rowsN");
                var rowH = H / (double)rowsN;
                var ts = rowH * 0.72;
                var period = Measure(new JObject { ["text"] = unitText, ["font"] = tileFont, ["size"] = ts, ["track"] = 0.02 }).W;
                var reps = (int)Math.Ceiling(W * 1.6 / period) + 2;
                var flicker = B(P, "flicker");
                for (var r = 0; r <= rowsN; r++)
                {
                    var ap = J.R(seed, r, 91) * cut.InDur * 1.6;
                    if (lb < ap) continue;
                    if (flicker && J.R(seed, (int)env.Step, r, 92) < 0.16) continue;
                    var dir = r % 2 == 1 ? 1 : -1;
                    var off = ((r % 2) * period * 0.5 + lb * 26 * dir) % period;
                    off = (off + period) % period;
                    // 背景平铺行整行一次画：逐字 draw 时每帧上千字形，是本版式卡顿的来源
                    FastRow(env, Repeat(unitText, reps), tileFont, ts,
                        -period + off - period * 0.5, (r + 0.5) * rowH, ts * 0.02, sc.Str("sub")!,
                        0.42 * J.Clamp((lb - ap) / 0.1));
                }
                var mt = W < H ? SplitLines(text, 5) : text;
                var size = Math.Min(FitSize(mt, font, W * 0.8, H * 0.34, new JObject { ["track"] = 0.04, ["lead"] = 1.15 }), H * 0.3);
                var it = new JObject
                {
                    ["text"] = mt,
                    ["font"] = font,
                    ["size"] = size,
                    ["x"] = W / 2.0,
                    ["y"] = H / 2.0,
                    ["track"] = 0.04,
                    ["lead"] = 1.15,
                    ["color"] = sc.Str("fg"),
                };
                if (S(P, "knock") == "box")
                {
                    var m = Measure(it);
                    var e = J.E.OutExpo(env.PIn * 1.4);
                    env.Rect(W / 2 - (m.W / 2 + size * 0.35) * e, H / 2 - m.H / 2 - size * 0.28, (m.W + size * 0.7) * e, m.H + size * 0.56, sc.Str("bg"), 1, false);
                }
                else
                {
                    var strokeIt = (JObject)it.DeepClone();
                    strokeIt["fill"] = false;
                    strokeIt["stroke"] = size * 0.16;
                    strokeIt["strokeColor"] = sc.Str("bg");
                    strokeIt["ghost"] = false;
                    MainDraw(env, strokeIt);
                }
                env.Body = MainDraw(env, it);
            },
        });
    }

    private static void RegisterScatter()
    {
        Reg.Register("layout", "scatter", new LayoutDef
        {
            Name = "散らし",
            Fits = n => n >= 2 && n <= 14,
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display")),
                ["fontB"] = rng.Pick(FontsOf(st, "serif", "display")),
                ["extras"] = rng.Chance(0.65),
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var P = env.P;
                var cut = env.Cut!;
                var s = (int)cut.Seed;
                var font = S(P, "font")!;
                var fontB = S(P, "fontB")!;
                var chars = CodePoints(Regex.Replace(cut.Text, @"\s+", ""));
                var n = chars.Count;
                if (B(P, "extras"))
                {
                    for (var k = 0; k < 9; k++)
                    {
                        var ap = J.R(s, k, 81) * cut.Dur * 0.5;
                        if (env.Ltb < ap) continue;
                        var top = J.R(s, k, 82) < 0.5;
                        env.DrawItem(new JObject
                        {
                            ["text"] = cut.Text,
                            ["font"] = Font0(env.St, "body"),
                            ["size"] = J.Rr(H * 0.022, H * 0.045, s, k, 83),
                            ["x"] = J.Rr(W * 0.08, W * 0.92, s, k, 84),
                            ["y"] = top ? J.Rr(H * 0.08, H * 0.26, s, k, 85) : J.Rr(H * 0.74, H * 0.92, s, k, 85),
                            ["rot"] = J.Rs(s, k, 86) * 18,
                            ["color"] = sc.Str("sub"),
                            ["alpha"] = 0.75,
                            ["ghost"] = false,
                        });
                    }
                }
                var bas = Math.Min(H * 0.3, W * 0.9 / n * 1.15);
                BBox? bb = null;
                for (var i = 0; i < n; i++)
                {
                    var ch = chars[i];
                    var x = W * (0.1 + 0.8 * (i + 0.5) / n) + J.Rs(s, i, 71) * W * 0.035;
                    var y = H / 2 + J.Rs(s, i, 72) * H * 0.18;
                    var k2 = 0.62 + J.R(s, i, 73) * 0.85 * (J.IsKanji(ch[0]) ? 1 : 0.7);
                    bb = UnionBb(bb, MainDraw(env, new JObject
                    {
                        ["text"] = ch,
                        ["font"] = i % 3 == 1 ? fontB : font,
                        ["size"] = bas * k2,
                        ["x"] = x,
                        ["y"] = y,
                        ["rot"] = J.Rs(s, i, 74) * 24,
                        ["color"] = J.R(s, i, 75) < 0.15 ? sc.Str("accent") : sc.Str("fg"),
                        ["mi"] = i,
                    }));
                }
                env.Body = bb;
            },
        });
    }

    private static void RegisterRing()
    {
        Reg.Register("layout", "ring", new LayoutDef
        {
            Name = "円環",
            Fits = n => n >= 2 && n <= 16,
            Plan = (rng, cut, st) => new JObject
            {
                ["orient"] = rng.Pick(new[] { "tangent", "tangent", "upright" }),
                ["center"] = rng.Pick(new[] { "word", "disc", "word", "none" }),
                ["speed"] = rng.Range(4, 12) * rng.Pick(new double[] { 1, -1 }),
                ["R"] = rng.Range(0.28, 0.35),
                ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
                ["fontC"] = rng.Pick(FontsOf(st, "display", "serif")),
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var P = env.P;
                var cut = env.Cut!;
                var text = Regex.Replace(cut.Text, @"\s+", "");
                var R = Math.Min(H * D(P, "R"), W * 0.4);
                var cx = W / 2.0;
                var cy = H / 2.0;
                var lb = env.Ltb;
                var n = GlyphCount(text);
                var unit = CodePoints(text + "・");
                var font = S(P, "font")!;
                var fontC = S(P, "fontC")!;
                var speed = D(P, "speed");
                var sizeRing = Math.Min(H * 0.07, J.TAU * R / ((n + 1) * 1.25));
                var cnt = Math.Max(unit.Count, Math.Min(44, (int)Math.Floor(J.TAU * R / (sizeRing * 1.2))));
                env.Circle(cx, cy, R * 0.86, null, sc.Str("sub"), 1.2, 0.55, false);
                env.Circle(cx, cy, R * 1.15, null, sc.Str("sub"), 1.2, 0.35, false);
                BBox? bb = null;
                var center = S(P, "center");
                if (center == "disc")
                {
                    var e = J.E.OutBack(J.Clamp(env.Lt / (cut.InDur * 0.9)), 1.6) * (1 - J.E.InCubic(env.POut));
                    env.Circle(cx, cy, R * 0.72 * e, sc.Str("accent"), null, 0, 1, true);
                    var size = FitSize(text, fontC, R * 1.15, R * 0.8);
                    bb = MainDraw(env, new JObject
                    {
                        ["text"] = text,
                        ["font"] = fontC,
                        ["size"] = Math.Min(size, H * 0.2),
                        ["x"] = cx,
                        ["y"] = cy,
                        ["color"] = sc.Str("bg"),
                        ["mi"] = 0,
                    });
                }
                else if (center == "word")
                {
                    var size = FitSize(text, fontC, R * 1.3, R * 0.85);
                    bb = MainDraw(env, new JObject
                    {
                        ["text"] = text,
                        ["font"] = fontC,
                        ["size"] = Math.Min(size, H * 0.22),
                        ["x"] = cx,
                        ["y"] = cy,
                        ["color"] = sc.Str("fg"),
                        ["mi"] = 0,
                    });
                }
                var orient = S(P, "orient");
                for (var i = 0; i < cnt; i++)
                {
                    var ch = unit[i % unit.Count];
                    var ang = i / (double)cnt * 360 + lb * speed - 90;
                    var rad = ang * J.DEG;
                    var it = new JObject
                    {
                        ["text"] = ch,
                        ["font"] = font,
                        ["size"] = sizeRing,
                        ["x"] = cx + Math.Cos(rad) * R,
                        ["y"] = cy + Math.Sin(rad) * R,
                        ["rot"] = orient == "tangent" ? ang + 90 : 0.0,
                        ["color"] = ch == "・" ? sc.Str("accent") : sc.Str("fg"),
                        ["mi"] = i * 0.25,
                        ["noHold"] = true,
                    };
                    var b = MainDraw(env, it);
                    // 每个环上字符的盒子都要并进来，只留首个字符会锚到随时间旋转的单字
                    bb = UnionBb(bb, b);
                }
                env.Body = bb ?? new BBox { X0 = cx - R, X1 = cx + R, Y0 = cy - R, Y1 = cy + R, Cx = cx, Cy = cy };
            },
        });
    }

    private static void RegisterWave()
    {
        Reg.Register("layout", "wave", new LayoutDef
        {
            Name = "波の軌跡",
            Fits = n => n >= 2 && n <= 16,
            Plan = (rng, cut, st) => new JObject
            {
                ["amp"] = rng.Range(0.08, 0.17),
                ["freq"] = rng.Range(0.8, 1.6),
                ["trail"] = rng.Pick(new[] { 5, 7, 9 }),
                ["font"] = rng.Pick(FontsOf(st, "display")),
                ["travel"] = rng.Range(0.25, 0.5) * rng.Pick(new double[] { 1, -1 }),
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var P = env.P;
                var cut = env.Cut!;
                var text = Regex.Replace(cut.Text, @"\s+", "");
                var font = S(P, "font")!;
                var amp = D(P, "amp");
                var freq = D(P, "freq");
                var travel = D(P, "travel");
                var trail = I(P, "trail");
                var chars = CodePoints(text);
                var n = chars.Count;
                var size = Math.Min(H * 0.2, W * 0.72 / n);
                var du = size * 1.05 / W;
                var lb = env.Ltb;
                var u0 = 0.5 - (env.Lt / cut.Dur - 0.5) * travel;
                double[] Path(double u) => new[] { W * u, H / 2 + H * amp * Math.Sin(J.TAU * freq * u + lb * 1.3) };
                double AngAt(double u)
                {
                    var a = Path(u - 0.002);
                    var b = Path(u + 0.002);
                    return Math.Atan2(b[1] - a[1], b[0] - a[0]) / J.DEG;
                }
                BBox? bb = null;
                for (var k = trail; k >= 1; k--)
                {
                    for (var i = 0; i < n; i++)
                    {
                        var ch = chars[i];
                        var u = u0 + (i - (n - 1) / 2.0) * du + k * du * 0.2 * Math.Sign(travel);
                        var xy = Path(u);
                        env.DrawItem(new JObject
                        {
                            ["text"] = ch,
                            ["font"] = font,
                            ["size"] = size * (1 - k * 0.035),
                            ["x"] = xy[0],
                            ["y"] = xy[1],
                            ["rot"] = AngAt(u),
                            ["color"] = sc.Str("sub"),
                            ["alpha"] = 0.5 * (1 - k / (double)(trail + 1)) * J.E.OutCubic(env.PIn),
                            ["ghost"] = false,
                        });
                    }
                }
                for (var i = 0; i < n; i++)
                {
                    var ch = chars[i];
                    var u = u0 + (i - (n - 1) / 2.0) * du;
                    var xy = Path(u);
                    bb = UnionBb(bb, MainDraw(env, new JObject
                    {
                        ["text"] = ch,
                        ["font"] = font,
                        ["size"] = size,
                        ["x"] = xy[0],
                        ["y"] = xy[1],
                        ["rot"] = AngAt(u),
                        ["color"] = sc.Str("fg"),
                        ["mi"] = i * 0.5,
                    }));
                }
                env.Body = bb;
            },
        });
    }

    private static void RegisterHuge()
    {
        Reg.Register("layout", "huge", new LayoutDef
        {
            Name = "画面突き抜け",
            Fits = n => n <= 8,
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display")),
                ["grad"] = Truthy(StTok(st, "useGrad")) && rng.Chance(0.75),
                ["dir"] = rng.Pick(new[] { 1, -1 }),
                ["label"] = rng.Chance(0.8),
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var P = env.P;
                var cut = env.Cut!;
                var text0 = Regex.Replace(cut.Text, @"\s+", "");
                var n = GlyphCount(text0);
                var text = n >= 5 ? SplitLines(text0, (int)Math.Ceiling(n / 2.0)) : text0;
                var lines = text.Split('\n').Length;
                var size = lines > 1
                    ? Math.Min(H * 0.56, W * 1.2 / (Math.Ceiling(n / 2.0) * 0.98))
                    : Math.Min(H * 0.98, W * 1.3 / (n * 0.96));
                var u = env.Lt / cut.Dur;
                var bb = MainDraw(env, new JObject
                {
                    ["text"] = text,
                    ["font"] = S(P, "font"),
                    ["size"] = size,
                    ["x"] = W / 2 + (0.5 - u) * W * 0.16 * D(P, "dir"),
                    ["y"] = H / 2 + H * 0.02,
                    ["lead"] = 0.98,
                    ["track"] = -0.02,
                    ["color"] = sc.Str("fg"),
                    ["gradient"] = B(P, "grad") ? sc.Get("grad") : null,
                });
                if (B(P, "label"))
                {
                    var ls = J.Clamp(H * 0.028, 16, 30);
                    var a = J.E.OutCubic(J.Clamp((env.Lt - cut.InDur * 0.5) / 0.2)) * (1 - env.POut);
                    var lt = Measure(new JObject { ["text"] = cut.Text, ["font"] = Font0(env.St, "body"), ["size"] = ls, ["track"] = 0.12 });
                    env.Rect(W * 0.05, H * 0.86 - ls, lt.W + ls * 1.4, ls * 2, sc.Str("ink"), a, false);
                    env.DrawItem(new JObject
                    {
                        ["text"] = cut.Text,
                        ["font"] = Font0(env.St, "body"),
                        ["size"] = ls,
                        ["track"] = 0.12,
                        ["align"] = "left",
                        ["x"] = W * 0.05 + ls * 0.7,
                        ["y"] = H * 0.86,
                        ["color"] = sc.Str("bg"),
                        ["alpha"] = a,
                        ["ghost"] = false,
                    });
                }
                env.Body = bb;
            },
        });
    }

    private static void RegisterLabels()
    {
        Reg.Register("layout", "labels", new LayoutDef
        {
            Name = "ラベル貼り",
            Fits = n => n >= 1 && n <= 16,
            Plan = (rng, cut, st) => new JObject
            {
                ["variant"] = rng.Pick(new[] { "radial", "rows", "scatter" }),
                ["unit"] = GlyphCount(cut.Text) <= 6 ? "char" : rng.Pick(new[] { "char", "word" }),
                ["center"] = rng.Pick(new[] { "orb", "word", "none" }),
                ["font"] = rng.Pick(FontsOf(st, "display", "body")),
                ["fontC"] = rng.Pick(FontsOf(st, "display")),
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var P = env.P;
                var cut = env.Cut!;
                var s = (int)cut.Seed;
                var lb = env.Ltb;
                var font = S(P, "font")!;
                var fontC = S(P, "fontC")!;
                var variant = S(P, "variant");
                var center = S(P, "center");
                // 帧不变量（text/units 只由 cut+params 决定）：按切分缓存（性能专项，稳态省 ~70KB/帧的
                // Regex/LINQ/List 重建），plan 换则随 Cut 一起回收
                var inv = CutCache.Get<LabelsInv>(cut, "labels");
                if (inv == null)
                {
                    var text0 = Regex.Replace(cut.Text, @"\s+", "");
                    var units0 = S(P, "unit") == "char"
                        ? CodePoints(text0).Where(c => !J.IsPunct(c[0])).ToList()
                        : new List<string>(cut.Words is { Length: > 0 } ? cut.Words : new[] { text0 });
                    if (units0.Count == 0) units0 = new List<string> { text0 };
                    inv = new LabelsInv(text0, units0);
                    CutCache.Put(cut, "labels", inv);
                }
                var text = inv.Text;
                var units = inv.Units;
                var outK = 1 - J.E.InCubic(env.POut);

                void DrawLabel(string u, double x, double y, double rot, double fs, double q, int i)
                {
                    if (q <= 0) return;
                    var m = Measure(new JObject { ["text"] = u, ["font"] = font, ["size"] = fs, ["track"] = 0.04 });
                    var w = m.W + fs * 0.7;
                    var h = fs * 1.36;
                    env.Ctx.Save();
                    env.Ctx.Translate((float)x, (float)y);
                    env.Ctx.RotateDegrees((float)rot); // JS: rotate(rot * DEG) 收弧度；RotateDegrees 直接收度
                    env.Ctx.Scale((float)q, (float)q);
                    env.Rect(-w / 2, -h / 2, w, h, sc.Str("ink"), 1);
                    env.DrawItem(new JObject { ["text"] = u, ["font"] = font, ["size"] = fs, ["track"] = 0.04, ["x"] = 0, ["y"] = 0, ["color"] = sc.Str("bg"), ["ghost"] = false });
                    env.Ctx.Restore();
                }

                BBox? bb;
                if (variant == "radial")
                {
                    var m = Math.Max(units.Count, 10);
                    var R = Math.Min(H * 0.3, W * 0.36);
                    var fs = Math.Min(H * 0.062, W * 0.052);
                    if (center == "orb")
                    {
                        var e = J.E.OutBack(J.Clamp(env.Lt / 0.35), 1.4) * outK;
                        env.Circle(W / 2.0, H / 2.0, R * 0.52 * e, sc.Str("accent"), null, 0, 1, true);
                    }
                    bb = null;
                    for (var i = 0; i < m; i++)
                    {
                        var ang = i / (double)m * 360 + lb * 7 - 90;
                        var q = J.E.OutBack(J.Clamp((env.Lt - i * 0.025) / 0.22), 2) * outK;
                        DrawLabel(units[i % units.Count], W / 2 + Math.Cos(ang * J.DEG) * R, H / 2 + Math.Sin(ang * J.DEG) * R, ang, fs, q, i);
                    }
                    if (center == "word")
                        bb = MainDraw(env, new JObject
                        {
                            ["text"] = text,
                            ["font"] = fontC,
                            ["size"] = Math.Min(FitSize(text, fontC, R * 1.1, R * 0.7), H * 0.18),
                            ["x"] = W / 2.0,
                            ["y"] = H / 2.0,
                            ["color"] = sc.Str("fg"),
                        });
                    env.Body = bb ?? new BBox { X0 = W / 2 - R, X1 = W / 2 + R, Y0 = H / 2 - R, Y1 = H / 2 + R, Cx = W / 2.0, Cy = H / 2.0 };
                    return;
                }
                if (variant == "rows")
                {
                    var k = units.Count;
                    var fs = Math.Min(H * 0.1, H * 0.7 / (k * 1.5));
                    for (var i = 0; i < k; i++)
                    {
                        var q = J.E.OutBack(J.Clamp((env.Lt - i * 0.05) / 0.22), 2) * outK;
                        DrawLabel(units[i], W / 2 + J.Rs(s, i, 5) * W * 0.12, H / 2 + (i - (k - 1) / 2.0) * fs * 1.55, J.Rs(s, i, 6) * 4, fs, q, i);
                    }
                    env.Body = new BBox { X0 = W * 0.3, X1 = W * 0.7, Y0 = H / 2 - k * fs * 0.8, Y1 = H / 2 + k * fs * 0.8, Cx = W / 2.0, Cy = H / 2.0 };
                    return;
                }
                var fs2 = H * 0.085;
                for (var i = 0; i < units.Count; i++)
                {
                    var q = J.E.OutBack(J.Clamp((env.Lt - i * 0.05) / 0.22), 2) * outK;
                    DrawLabel(units[i],
                        W * (0.15 + 0.7 * ((i + 0.5) / units.Count)) + J.Rs(s, i, 7) * W * 0.04,
                        H / 2 + J.Rs(s, i, 8) * H * 0.25, J.Rs(s, i, 9) * 22, fs2 * (0.8 + J.R(s, i, 10) * 0.5), q, i);
                }
                env.Body = new BBox { X0 = W * 0.15, X1 = W * 0.85, Y0 = H * 0.3, Y1 = H * 0.7, Cx = W / 2.0, Cy = H / 2.0 };
            },
        });
    }

    /// <summary>labels 的帧不变量（CutCache 载体）。</summary>
    private sealed record LabelsInv(string Text, List<string> Units);

    /// <summary>stack 等版式的 FitSize 常量 opt（只读共享实例，免逐帧 new JObject）。</summary>
    private static readonly JObject TrackOpt3 = new() { ["track"] = 0.03 };

    /// <summary>pill 的 FitSize 常量 opt。</summary>
    private static readonly JObject PillTrackOpt = new() { ["track"] = 0.04 };

    private static void RegisterCondensed()
    {
        Reg.Register("layout", "condensed", new LayoutDef
        {
            Name = "縦長圧縮",
            Fits = n => n <= 10,
            Plan = (rng, cut, st) =>
            {
                var n = GlyphCount(cut.Text);
                return new JObject
                {
                    ["count"] = n <= 4 ? rng.Pick(new[] { 3, 2, 1 }) : n <= 7 ? rng.Pick(new[] { 2, 1 }) : 1,
                    ["sx"] = rng.Range(0.42, 0.58),
                    ["sy"] = rng.Range(1.1, 1.3),
                    ["font"] = rng.Pick(FontsOf(st, "display", "body")),
                };
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var P = env.P;
                var text = Regex.Replace(env.Cut!.Text, @"\s+", "");
                var font = S(P, "font")!;
                var sx = D(P, "sx");
                var sy = D(P, "sy");
                var count = I(P, "count");
                var slot = W * 0.92 / count;
                var size = Math.Min(FitSize(text, font, slot * 0.94, H * 0.8, new JObject { ["sx"] = sx, ["sy"] = sy, ["track"] = 0.04 }), H * 0.62);
                BBox? bb = null;
                int[] order = { 1, 0, 2, 3 };
                for (var i = 0; i < count; i++)
                {
                    var r = MainDraw(env, new JObject
                    {
                        ["text"] = text,
                        ["font"] = font,
                        ["size"] = size,
                        ["sx"] = sx,
                        ["sy"] = sy,
                        ["track"] = 0.04,
                        ["x"] = W / 2 + (i - (count - 1) / 2.0) * slot,
                        ["y"] = H / 2.0,
                        ["color"] = sc.Str("fg"),
                        ["mi"] = count > 1 ? (double)order[i] * 2 : 0.0,
                    });
                    bb = UnionBb(bb, r);
                }
                env.Body = bb;
            },
        });
    }

    private static void RegisterGloss()
    {
        Reg.Register("layout", "gloss", new LayoutDef
        {
            Name = "注釈",
            Fits = n => n <= 12,
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "serif", "display")),
                ["side"] = rng.Pick(new[] { "right", "left" }),
                ["bgText"] = rng.Chance(0.6),
                ["vertNote"] = rng.Chance(0.45),
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var P = env.P;
                var cut = env.Cut!;
                var text = cut.Text;
                var lb = env.Ltb;
                var font = S(P, "font")!;
                var compact = Regex.Replace(text, @"\s+", "");
                if (B(P, "bgText"))
                {
                    for (var r = 0; r < 3; r++)
                    {
                        var bs0 = H * 0.3;
                        FastRow(env, Repeat(compact, 6), font, bs0,
                            W / 2 + lb * 20 * (r % 2 == 1 ? 1 : -1) % (bs0 * 2), H * (0.18 + r * 0.32), 0, sc.Str("dim")!, 1, "center");
                    }
                }
                var right = S(P, "side") == "right";
                var size = Math.Min(FitSize(text, font, W * 0.5, H * 0.3, new JObject { ["track"] = 0.03 }), H * 0.24);
                var bb = MainDraw(env, new JObject
                {
                    ["text"] = text,
                    ["font"] = font,
                    ["size"] = size,
                    ["x"] = right ? W * 0.4 : W * 0.6,
                    ["y"] = H * 0.54,
                    ["track"] = 0.03,
                    ["color"] = sc.Str("fg"),
                });
                if (bb == null)
                {
                    env.Body = null;
                    return;
                }
                var e = J.E.OutExpo(J.Clamp((env.Lt - cut.InDur * 0.4) / 0.45)) * (1 - J.E.InCubic(env.POut));
                if (e <= 0)
                {
                    env.Body = bb;
                    return;
                }
                var ax = right ? bb.X1 + size * 0.1 : bb.X0 - size * 0.1;
                var ay = bb.Y0 + size * 0.2;
                var nx = right ? Math.Min(W * 0.9, bb.X1 + W * 0.1) : Math.Max(W * 0.1, bb.X0 - W * 0.1);
                var ny = Math.Max(H * 0.12, bb.Y0 - H * 0.12);
                var mx = J.Lerp(ax, nx, 0.45);
                var pts = new[] { new[] { ax, ay }, new[] { mx, ay }, new[] { nx, ny } };
                env.PolyPartial(pts, e, sc.Str("sub"), 1.3, 1, false);
                env.Circle(ax, ay, 4, sc.Str("accent"), null, 0, e, false);
                var note = OrStr(cut.Note, J.Romaji(compact), cut.LineText);
                var ns = J.Clamp(H * 0.024, 14, 26);
                var body = Font0(env.St, "body");
                var serif = Font0(env.St, "serif");
                var al = right ? "left" : "right";
                env.DrawItem(new JObject
                {
                    ["text"] = "【" + compact + "】",
                    ["font"] = serif,
                    ["size"] = ns * 1.2,
                    ["align"] = al,
                    ["x"] = nx,
                    ["y"] = ny - ns * 1.2,
                    ["color"] = sc.Str("fg"),
                    ["alpha"] = e,
                    ["ghost"] = false,
                });
                if (B(P, "vertNote"))
                    env.DrawItem(new JObject
                    {
                        ["text"] = cut.LineText,
                        ["font"] = serif,
                        ["size"] = ns,
                        ["vertical"] = true,
                        ["align"] = "left",
                        ["x"] = nx + (right ? ns : -ns),
                        ["y"] = ny + ns * 0.8,
                        ["color"] = sc.Str("sub"),
                        ["alpha"] = e,
                        ["ghost"] = false,
                    });
                else
                    env.DrawItem(new JObject
                    {
                        ["text"] = note,
                        ["font"] = body,
                        ["size"] = ns,
                        ["align"] = al,
                        ["x"] = nx,
                        ["y"] = ny + ns * 0.4,
                        ["track"] = 0.08,
                        ["color"] = sc.Str("sub"),
                        ["alpha"] = e,
                        ["ghost"] = false,
                    });
                env.DrawItem(new JObject
                {
                    ["text"] = $"No.{cut.Line + 1:D2}",
                    ["font"] = Font0(env.St, "mono") ?? "mono",
                    ["size"] = ns * 0.8,
                    ["align"] = al,
                    ["x"] = nx,
                    ["y"] = ny + ns * 2,
                    ["color"] = sc.Str("accent"),
                    ["alpha"] = e,
                    ["ghost"] = false,
                });
                env.Body = bb;
            },
        });
    }

    private static void RegisterType()
    {
        Reg.Register("layout", "type", new LayoutDef
        {
            Name = "タイプ",
            Fits = n => n <= 28,
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "body", "serif", "mono")),
                ["align"] = rng.Pick(new[] { "left", "center" }),
                ["prompt"] = rng.Chance(0.6),
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var P = env.P;
                var cut = env.Cut!;
                var text = SplitLines(cut.Text, 14);
                var font = S(P, "font")!;
                var size = Math.Min(H * 0.11, FitSize(text, font, W * 0.74, H * 0.36, new JObject { ["track"] = 0.06, ["lead"] = 1.35 }));
                var left = S(P, "align") == "left";
                var x = left ? W * 0.13 : W / 2.0;
                var mono = Font0(env.St, "mono") ?? "mono";
                if (B(P, "prompt"))
                    env.DrawItem(new JObject
                    {
                        ["text"] = ">",
                        ["font"] = mono,
                        ["size"] = size * 0.8,
                        ["x"] = (left ? x : x - Measure(new JObject { ["text"] = text, ["font"] = font, ["size"] = size, ["track"] = 0.06 }).W / 2) - size * 0.9,
                        ["y"] = H / 2 - (text.Split('\n').Length - 1) * size * 0.67,
                        ["color"] = sc.Str("accent"),
                        ["ghost"] = false,
                    });
                var it = new JObject
                {
                    ["text"] = text,
                    ["font"] = font,
                    ["size"] = size,
                    ["x"] = x,
                    ["y"] = H / 2.0,
                    ["align"] = left ? "left" : "center",
                    ["track"] = 0.06,
                    ["lead"] = 1.35,
                    ["color"] = sc.Str("fg"),
                };
                if (cut.Enter == "cut") it["enter"] = "type";
                var bb = MainDraw(env, it);
                var ms = J.Clamp(H * 0.02, 12, 20);
                env.DrawItem(new JObject
                {
                    ["text"] = $"LINE {cut.Line + 1:D2} ─ {J.FmtTime(env.T)}",
                    ["font"] = mono,
                    ["size"] = ms,
                    ["align"] = "left",
                    ["x"] = W * 0.13,
                    ["y"] = H * 0.8,
                    ["color"] = sc.Str("sub"),
                    ["alpha"] = 0.8,
                    ["ghost"] = false,
                });
                env.Body = bb;
            },
        });
    }

    private static void RegisterDiag()
    {
        Reg.Register("layout", "diag", new LayoutDef
        {
            Name = "斜め帯",
            Fits = n => n <= 14,
            Plan = (rng, cut, st) => new JObject
            {
                ["ang"] = rng.Range(10, 22) * rng.Pick(new double[] { 1, -1 }),
                ["band"] = rng.Pick(new[] { "accent", "ink" }),
                ["second"] = rng.Chance(0.7),
                ["font"] = rng.Pick(FontsOf(st, "display")),
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var P = env.P;
                var cut = env.Cut!;
                var text = cut.Text;
                var lb = env.Ltb;
                var font = S(P, "font")!;
                var ang = D(P, "ang");
                var bandCol = S(P, "band") == "accent" ? sc.Str("accent") : sc.Str("ink");
                var txtCol = J.Lum(bandCol!) > 0.5
                    ? J.Lum(sc.Str("bg")!) < 0.5 ? sc.Str("bg") : "#111111"
                    : J.Lum(sc.Str("fg")!) > 0.5 ? sc.Str("fg") : "#FFFFFF";
                var size = Math.Min(FitSize(text, font, W * 0.72, H * 0.24, new JObject { ["track"] = 0.05 }), H * 0.2);
                var bh = size * 1.6;
                var e = J.E.OutExpo(J.Clamp(env.Lt / (cut.InDur * 0.8))) * (1 - J.E.InExpo(env.POut));
                env.Ctx.Save();
                env.Ctx.Translate(W / 2f, H / 2f);
                env.Ctx.RotateDegrees((float)-ang); // JS: rotate(-P.ang * DEG)；RotateDegrees 直接收度
                env.Rect(-W * 1.2, -bh / 2 * e, W * 2.4, bh * e, bandCol, 1);
                if (B(P, "second"))
                {
                    var y2 = bh * 0.95;
                    var h2 = bh * 0.32;
                    env.Rect(-W * 1.2, y2 - h2 / 2, W * 2.4 * e, h2, sc.Str("fg"), 0.9, false);
                    var unit = cut.LineText + "　／　";
                    var period = Measure(new JObject { ["text"] = unit, ["font"] = Font0(env.St, "body"), ["size"] = h2 * 0.55, ["track"] = 0.1 }).W;
                    FastRow(env, Repeat(unit, (int)Math.Ceiling(W * 3 / period)), Font0(env.St, "body")!, h2 * 0.55,
                        -(lb * 120) % period, y2, h2 * 0.055, sc.Str("bg")!, e, "center");
                }
                env.Ctx.Restore();
                env.Body = MainDraw(env, new JObject
                {
                    ["text"] = text,
                    ["font"] = font,
                    ["size"] = size,
                    ["x"] = W / 2.0,
                    ["y"] = H / 2.0,
                    ["rot"] = -ang,
                    ["track"] = 0.05,
                    ["color"] = txtCol,
                });
            },
        });
    }

    private static void RegisterCircle()
    {
        Reg.Register("layout", "circle", new LayoutDef
        {
            Name = "円窓",
            Fits = n => n <= 10,
            Plan = (rng, cut, st) => new JObject
            {
                ["variant"] = rng.Pick(new[] { "disc", "eclipse", "ring" }),
                ["vertical"] = GlyphCount(cut.Text) <= 4 && rng.Chance(0.5),
                ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
                ["off"] = rng.Range(-0.12, 0.12),
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var P = env.P;
                var cut = env.Cut!;
                var text = Regex.Replace(cut.Text, @"\s+", "");
                var font = S(P, "font")!;
                var variant = S(P, "variant");
                var cx = W / 2 + D(P, "off") * W;
                var cy = H / 2.0;
                var outK = 1 - J.E.InCubic(env.POut);
                if (variant == "eclipse")
                {
                    var size = Math.Min(FitSize(text, font, W * 0.82, H * 0.46, new JObject { ["track"] = 0.02 }), H * 0.36);
                    var bb = MainDraw(env, new JObject
                    {
                        ["text"] = SplitLines(text, 6),
                        ["font"] = font,
                        ["size"] = size,
                        ["x"] = W / 2.0,
                        ["y"] = H / 2.0,
                        ["lead"] = 1.05,
                        ["color"] = sc.Str("fg"),
                    });
                    var R = H * 0.19;
                    var u = env.Lt / cut.Dur;
                    var ex = W / 2 + J.Lerp(-0.08, 0.08, u) * W;
                    var ey = H / 2 + H * 0.12;
                    if (env.Pass == "main")
                    {
                        // ctx.shadowColor/shadowBlur → SKImageFilter.CreateDropShadow（Canvas2D blur 半径 ≈ 2σ）
                        env.Ctx.Save();
                        var blur = (float)(38 * env.Scale / 2);
                        using var p = new SKPaint
                        {
                            IsAntialias = true,
                            IsStroke = true,
                            StrokeWidth = 3,
                            Color = Env.SkColor(sc.Str("fg")!, 0.9),
                        };
                        p.ImageFilter = JText.ShadowSigmaFilter(0, 0, blur, blur, p.Color); // 记忆滤镜，不 Dispose
                        env.Ctx.DrawCircle((float)ex, (float)ey, (float)(R * 1.01 * outK), p);
                        env.Ctx.Restore();
                        env.Circle(ex, ey, R * outK, J.Mix(sc.Str("bg")!, "#000000", 0.35), null, 0, 1, false);
                    }
                    env.Body = bb;
                    return;
                }
                var R2 = Math.Min(H * 0.3, W * 0.4);
                var e2 = J.E.OutBack(J.Clamp(env.Lt / (cut.InDur * 0.9)), 1.5) * outK;
                if (variant == "disc") env.Circle(cx, cy, R2 * e2, sc.Str("accent"), null, 0, 1, true);
                else env.Arc(cx, cy, R2, -90, -90 + 360 * J.E.OutExpo(J.Clamp(env.Lt / (cut.InDur * 1.3))) * outK, sc.Str("fg"), 3, 1);
                var vertical = B(P, "vertical");
                var size2 = vertical
                    ? Math.Min(FitSize(text, font, R2 * 1.1, R2 * 1.35, new JObject { ["vertical"] = true }), R2 * 0.9)
                    : Math.Min(FitSize(text, font, R2 * 1.45, R2 * 0.9), R2 * 0.8);
                env.Body = MainDraw(env, new JObject
                {
                    ["text"] = text,
                    ["font"] = font,
                    ["size"] = size2,
                    ["x"] = cx,
                    ["y"] = cy,
                    ["vertical"] = vertical,
                    ["color"] = variant == "disc" ? sc.Str("bg") : sc.Str("fg"),
                });
            },
        });
    }

    private static void RegisterStack()
    {
        Reg.Register("layout", "stack", new LayoutDef
        {
            Name = "残像スタック",
            Fits = n => n <= 12,
            Plan = (rng, cut, st) => new JObject
            {
                ["copies"] = rng.Pick(new[] { 3, 4, 5 }),
                ["dir"] = rng.Pick(new[] { 1, -1 }),
                ["style"] = rng.Pick(new[] { "fade", "outline", "fade" }),
                ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
                ["gap"] = rng.Range(0.82, 1.02),
                ["xs"] = rng.Range(-0.04, 0.04),
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var P = env.P;
                var cut = env.Cut!;
                var text = cut.Text;
                var font = S(P, "font")!;
                var style = S(P, "style");
                var dir = D(P, "dir");
                var gap = D(P, "gap");
                var xs = D(P, "xs");
                // 帧不变量：字号只由 (text, font, W, H) 决定，按切分缓存（性能专项）
                var size = CutCache.Get<double>(cut, "stack.size");
                if (size <= 0)
                {
                    size = Math.Min(FitSize(text, font, W * 0.8, H * 0.22, TrackOpt3), H * 0.19);
                    CutCache.Put(cut, "stack.size", size);
                }
                var step = size * gap;
                var n = I(P, "copies");
                var y0 = H / 2 - dir * (n - 1) * step / 2;
                BBox? bb = null;
                for (var k = n - 1; k >= 0; k--)
                {
                    var it = new JObject
                    {
                        ["text"] = text,
                        ["font"] = font,
                        ["size"] = size,
                        ["x"] = W / 2 + xs * W * k,
                        ["y"] = y0 + dir * k * step,
                        ["track"] = 0.03,
                        ["color"] = sc.Str("fg"),
                        ["mi"] = k * 1.2,
                    };
                    if (k > 0)
                    {
                        if (style == "outline")
                        {
                            it["fill"] = false;
                            it["stroke"] = Math.Max(1.2, size * 0.014);
                            it["alpha"] = 0.85;
                        }
                        else it["alpha"] = 0.6 * Math.Pow(0.58, k - 1);
                    }
                    var r = MainDraw(env, it);
                    if (k == 0) bb = r;
                }
                env.Body = bb;
            },
        });
    }

    private static void RegisterPill()
    {
        Reg.Register("layout", "pill", new LayoutDef
        {
            Name = "カプセル",
            Fits = n => n <= 14,
            Plan = (rng, cut, st) => new JObject
            {
                ["grad"] = Truthy(StTok(st, "useGrad")) || rng.Chance(0.35),
                ["font"] = rng.Pick(FontsOf(st, "display", "body")),
                ["smalls"] = rng.Chance(0.75),
            },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var P = env.P;
                var cut = env.Cut!;
                var text = cut.Text;
                var font = S(P, "font")!;
                // 帧不变量：字号与主盒宽只由 (text, font, W, H) 决定，按切分缓存（性能专项）
                var size = CutCache.Get<double>(cut, "pill.size");
                if (size <= 0)
                {
                    size = Math.Min(FitSize(text, font, W * 0.62, H * 0.2, PillTrackOpt), H * 0.17);
                    CutCache.Put(cut, "pill.size", size);
                }
                var m = CutCache.Get<(double W, double H)>(cut, "pill.m");
                if (m == default)
                {
                    var mm = Measure(new JObject { ["text"] = text, ["font"] = font, ["size"] = size, ["track"] = 0.04 });
                    m = (mm.W, mm.H);
                    CutCache.Put(cut, "pill.m", m);
                }
                var w = m.W + size * 1.3;
                var h = size * 1.6;
                var e = J.E.OutExpo(J.Clamp(env.Lt / (cut.InDur * 0.9))) * (1 - J.E.InExpo(env.POut));
                var grad = sc.Get("grad") as JArray;
                string[] fillC = B(P, "grad") && grad is { Count: >= 2 }
                    ? new[] { (string)grad[0]!, (string)grad[1]! }
                    : new[] { sc.Str("accent")!, sc.Str("accent")! };
                var ww = Math.Max(h, w * e);
                if (env.Pass == "main")
                {
                    // ctx.createLinearGradient(W/2 - ww/2, 0, W/2 + ww/2, 0) + addColorStop(0/1)
                    using var shader = SKShader.CreateLinearGradient(
                        new SKPoint((float)(W / 2 - ww / 2), 0), new SKPoint((float)(W / 2 + ww / 2), 0),
                        new[] { Env.SkColor(fillC[0]), Env.SkColor(fillC[1]) }, new float[] { 0, 1 }, SKShaderTileMode.Clamp);
                    env.RRect(W / 2 - ww / 2, H / 2 - h / 2, ww, h, h / 2, shader, 1, true);
                }
                else env.RRect(W / 2 - ww / 2, H / 2 - h / 2, ww, h, h / 2, sc.Str("accent"), 1, true);
                var tc = J.Lum(fillC[0]) > 0.55 ? "#111111" : "#FFFFFF";
                env.Ctx.Save();
                env.Ctx.ClipRect(new SKRect((float)(W / 2 - ww / 2), 0, (float)(W / 2 + ww / 2), H));
                var bb = MainDraw(env, new JObject
                {
                    ["text"] = text,
                    ["font"] = font,
                    ["size"] = size,
                    ["x"] = W / 2.0,
                    ["y"] = H / 2.0,
                    ["track"] = 0.04,
                    ["color"] = tc,
                });
                env.Ctx.Restore();
                if (B(P, "smalls"))
                {
                    var labs = new[]
                    {
                        J.Romaji(Regex.Replace(text, @"\s+", "")) ?? "LYRIC",
                        $"No.{cut.Line + 1:D2}",
                        J.FmtTime(cut.Start),
                    };
                    var fs = J.Clamp(H * 0.022, 13, 24);
                    var body = Font0(env.St, "body");
                    for (var i = 0; i < labs.Length; i++)
                    {
                        var l = labs[i];
                        var q = J.E.OutBack(J.Clamp((env.Lt - 0.15 - i * 0.06) / 0.25), 2) * (1 - env.POut);
                        if (q <= 0) continue;
                        var mm = Measure(new JObject { ["text"] = l, ["font"] = body, ["size"] = fs, ["track"] = 0.1 });
                        var px = W / 2 + (i == 0 ? -w * 0.3 : i == 1 ? w * 0.42 : w * 0.1);
                        var py = H / 2 + (i == 1 ? -h * 0.95 : h * 0.95);
                        env.RRect(px - (mm.W / 2 + fs * 0.8) * q, py - fs * 0.85, (mm.W + fs * 1.6) * q, fs * 1.7, fs * 0.85, null, 1, false, sc.Str("fg"), 1.3);
                        env.DrawItem(new JObject
                        {
                            ["text"] = l,
                            ["font"] = body,
                            ["size"] = fs,
                            ["track"] = 0.1,
                            ["x"] = px,
                            ["y"] = py,
                            ["color"] = sc.Str("fg"),
                            ["alpha"] = q,
                            ["ghost"] = false,
                        });
                    }
                }
                env.Body = bb;
            },
        });
    }

    // ---------------- layout：special（不参与自动挑选，由标题卡、间奏直接指定） ----------------

    private static void RegisterTitle()
    {
        Reg.Register("layout", "title", new LayoutDef
        {
            Name = "タイトル",
            Special = true,
            Fits = _ => false,
            Plan = (rng, cut, st) => new JObject { ["font"] = rng.Pick(FontsOf(st, "display", "serif")) },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var P = env.P;
                var cut = env.Cut!;
                var font = S(P, "font")!;
                var size = Math.Min(FitSize(cut.Text, font, W * 0.7, H * 0.2, new JObject { ["track"] = 0.08 }), H * 0.16);
                var bb = MainDraw(env, new JObject
                {
                    ["text"] = cut.Text,
                    ["font"] = font,
                    ["size"] = size,
                    ["x"] = W / 2.0,
                    ["y"] = H / 2.0,
                    ["track"] = 0.08,
                    ["color"] = sc.Str("fg"),
                });
                if (!string.IsNullOrEmpty(cut.Note))
                    env.DrawItem(new JObject
                    {
                        ["text"] = cut.Note,
                        ["font"] = Font0(env.St, "body"),
                        ["size"] = J.Clamp(H * 0.03, 16, 32),
                        ["x"] = W / 2.0,
                        ["y"] = H / 2 + size * 0.95,
                        ["track"] = 0.3,
                        ["color"] = sc.Str("sub"),
                        ["alpha"] = J.E.OutCubic(J.Clamp((env.Lt - 0.3) / 0.4)) * (1 - env.POut),
                        ["ghost"] = false,
                    });
                env.Body = bb;
            },
        });
    }

    private static void RegisterInterlude()
    {
        Reg.Register("layout", "interlude", new LayoutDef
        {
            Name = "間奏",
            Special = true,
            Fits = _ => false,
            Plan = (rng, cut, st) => new JObject { ["variant"] = rng.Pick(new[] { "counter", "rings" }) },
            Draw = env =>
            {
                var W = env.W;
                var H = env.H;
                var sc = env.Sc!;
                var P = env.P;
                var cut = env.Cut!;
                var lb = env.Ltb;
                var fs = J.Clamp(H * 0.022, 12, 22);
                var body = Font0(env.St, "body");
                // 长间奏的安静变体，只留曲名（variant/showTitle/titleText 由 planner 侧注入，不在本 plan 里）
                if (S(P, "variant") == "quiet")
                {
                    var titleText = S(P, "titleText");
                    if (B(P, "showTitle") && !string.IsNullOrEmpty(titleText))
                    {
                        var a = J.Clamp(env.Lt / 0.8) * J.Clamp((cut.Dur - env.Lt) / 0.8);
                        env.DrawItem(new JObject
                        {
                            ["text"] = titleText,
                            ["font"] = body,
                            ["size"] = fs * 1.1,
                            ["x"] = W / 2.0,
                            ["y"] = H * 0.88,
                            ["track"] = 0.3,
                            ["color"] = sc.Str("sub"),
                            ["alpha"] = a,
                            ["ghost"] = false,
                        });
                    }
                    env.Body = new BBox { X0 = W * 0.3, X1 = W * 0.7, Y0 = H * 0.3, Y1 = H * 0.7, Cx = W / 2.0, Cy = H / 2.0 };
                    return;
                }
                if (S(P, "variant") == "counter")
                {
                    var remain = Math.Max(0, cut.Dur - env.Lt);
                    env.DrawItem(new JObject
                    {
                        ["text"] = remain.ToString("F1", CultureInfo.InvariantCulture),
                        ["font"] = Font0(env.St, "display"),
                        ["size"] = H * 0.36,
                        ["x"] = W / 2.0,
                        ["y"] = H / 2.0,
                        ["color"] = sc.Str("fg"),
                        ["alpha"] = 0.9,
                    });
                }
                for (var k = 0; k < 3; k++)
                    env.Circle(W / 2.0, H / 2.0, H * (0.2 + k * 0.1) * (1 + 0.04 * Math.Sin(lb * 2 + k)), null, sc.Str("sub"), 1.2, 0.5, false);
                env.DrawItem(new JObject
                {
                    ["text"] = string.IsNullOrEmpty(cut.Text) ? "— interlude —" : cut.Text,
                    ["font"] = body,
                    ["size"] = fs,
                    ["x"] = W / 2.0,
                    ["y"] = H * 0.82,
                    ["track"] = 0.4,
                    ["color"] = sc.Str("sub"),
                    ["ghost"] = false,
                });
                env.Body = new BBox { X0 = W * 0.35, X1 = W * 0.65, Y0 = H * 0.3, Y1 = H * 0.7, Cx = W / 2.0, Cy = H / 2.0 };
            },
        });
    }

    // ---------------- 文本断行（planner 依赖） ----------------

    // 拉丁文本只在词间断行，行数取能装下的最少值，再做均衡分配
    private static string SplitWords(string text, int maxPer)
    {
        var ws = Regex.Split(text.Trim(), @"\s+");
        var n = ws.Length;
        var total = ws.Sum(w => (double)CpLen(w)) + n - 1;
        var nLines = Math.Min(n, (int)Math.Ceiling(total / maxPer));
        if (nLines <= 1) return string.Join(" ", ws);
        // DP：最小化各行与理想行长的平方差
        double Len(int a, int b) => ws.Skip(a).Take(b - a).Sum(w => (double)CpLen(w)) + (b - a - 1);
        var ideal = total / nLines;
        var memo = new Dictionary<(int, int), (double C, int[] Cuts)>();
        Func<int, int, (double C, int[] Cuts)> best = null!;
        best = (i, l) =>
        {
            if (l == 1)
            {
                var d0 = Len(i, n) - ideal;
                return (d0 * d0, Array.Empty<int>());
            }
            var key = (i, l);
            if (memo.TryGetValue(key, out var hit)) return hit;
            var rc = double.PositiveInfinity;
            var cuts = Array.Empty<int>();
            for (var j = i + 1; j <= n - l + 1; j++)
            {
                var d = Len(i, j) - ideal;
                var sub = best(j, l - 1);
                var c = d * d + sub.C;
                if (c < rc)
                {
                    rc = c;
                    cuts = new[] { j }.Concat(sub.Cuts).ToArray();
                }
            }
            memo[key] = (rc, cuts);
            return (rc, cuts);
        };
        var allCuts = new List<int> { 0 };
        allCuts.AddRange(best(0, nLines).Cuts);
        allCuts.Add(n);
        var outLines = new List<string>();
        for (var k = 0; k < allCuts.Count - 1; k++)
            outLines.Add(string.Join(" ", ws.Skip(allCuts[k]).Take(allCuts[k + 1] - allCuts[k])));
        return string.Join("\n", outLines);
    }

    /// <summary>J.splitLines：长文本均衡断行，断点优先挑标点/空格之后、小假名之前。</summary>
    public static string SplitLines(string text, int maxPer)
    {
        var src = text ?? "";
        var arr = CodePoints(src);
        if (arr.Count <= maxPer) return src;
        if (IsLatinText(src)) return Regex.IsMatch(src.Trim(), @"\s") ? SplitWords(src, maxPer) : src;   // 一个词保持完整
        var nLines = (int)Math.Ceiling(arr.Count / (double)maxPer);
        var per = arr.Count / (double)nLines;
        var outLines = new List<string>();
        var start = 0;
        for (var l = 1; l < nLines; l++)
        {
            var target = (int)Math.Round(per * l, MidpointRounding.AwayFromZero);
            var best = target;
            var bestScore = -1.0;
            for (var k = Math.Max(start + 1, target - 3); k <= Math.Min(arr.Count - 1, target + 3); k++)
            {
                var a = arr[k - 1];
                var b = arr[k];
                var s = 3 - Math.Abs(k - target);
                if (J.IsHira(a[0]) && !J.IsHira(b[0])) s += 3;
                if (J.IsPunct(a[0]) || a[0] == ' ' || a[0] == '　') s += 5;
                if (J.IsSmallKana(b[0]) || "ーっ、。".Contains(b)) s -= 6;
                if (s > bestScore)
                {
                    bestScore = s;
                    best = k;
                }
            }
            outLines.Add(string.Concat(arr.Skip(start).Take(best - start)).Trim());
            start = best;
        }
        outLines.Add(string.Concat(arr.Skip(start)).Trim());
        return string.Join("\n", outLines);
    }

    // 以拉丁字母为主的文本（英语、印尼语、越南语……歌词）。
    // 对位 08_planner.js 的 J.isLatinText。
    private static bool IsLatinText(string? t)
    {
        var s = Regex.Replace(t ?? "", @"\s", "");
        if (s.Length == 0) return false;
        var cps = CodePoints(s);
        var n = 0;
        foreach (var cp in cps)
        {
            var c = cp[0];
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '\u00c0' and <= '\u024f' or >= '\u1e00' and <= '\u1eff') n++;
        }
        return n / (double)cps.Count >= 0.6;
    }

    // ---------------- 本文件小工具（JS J.glyphCount / J.fontsOf / J.unionBB / J.centerBB） ----------------

    /// <summary>J.glyphCount：去空白后的码点数。</summary>
    public static int GlyphCount(string t)
    {
        var n = 0;
        foreach (var cp in CodePoints(t ?? ""))
            if (!char.IsWhiteSpace(cp[0])) n++;
        return n;
    }

    /// <summary>J.fontsOf：按角色取风格字体列表，过滤掉字体目录里不存在的 key；全空则回退 display（再空 gothic_black）。</summary>
    public static string[] FontsOf(StyleDef st, params string[] roles)
    {
        var f = new List<string>();
        foreach (var r in roles)
            foreach (var k in RoleFonts(st, r))
                if (FontExists(k)) f.Add(k);
        if (f.Count > 0) return f.ToArray();
        var disp = RoleFonts(st, "display");
        return disp.Count > 0 ? disp.ToArray() : new[] { "gothic_black" };
    }

    /// <summary>st.fonts[role]（04_styles 的字体三/四角色 → key 数组）。</summary>
    internal static List<string> RoleFonts(StyleDef st, string role)
    {
        var fonts = StTok(st, "fonts") as JObject;
        var arr = fonts?[role] as JArray;
        var list = new List<string>();
        if (arr != null)
            foreach (var t in arr)
                if (t.Type == JTokenType.String) list.Add((string)t!);
        return list;
    }

    /// <summary>env.st.fonts[role][0]。</summary>
    internal static string? Font0(StyleDef st, string role)
    {
        var arr = RoleFonts(st, role);
        return arr.Count > 0 ? arr[0] : null;
    }

    /// <summary>J.unionBB：两个主体 bbox 的并集（boxes 不保留）。</summary>
    public static BBox? UnionBb(BBox? a, BBox? b)
    {
        if (a == null) return b;
        if (b == null) return a;
        return new BBox
        {
            X0 = Math.Min(a.X0, b.X0),
            Y0 = Math.Min(a.Y0, b.Y0),
            X1 = Math.Max(a.X1, b.X1),
            Y1 = Math.Max(a.Y1, b.Y1),
            Boxes = new List<JObject>(),
            Cx = (Math.Min(a.X0, b.X0) + Math.Max(a.X1, b.X1)) / 2,
            Cy = (Math.Min(a.Y0, b.Y0) + Math.Max(a.Y1, b.Y1)) / 2,
        };
    }

    /// <summary>J.centerBB：bb 为空时给画面中央的默认 bbox。</summary>
    public static BBox CenterBb(Env env, BBox? bb) => bb ?? new BBox
    {
        X0 = env.W * 0.35,
        X1 = env.W * 0.65,
        Y0 = env.H * 0.4,
        Y1 = env.H * 0.6,
        Cx = env.W / 2.0,
        Cy = env.H / 2.0,
        Boxes = new List<JObject>(),
    };

    // ---------------- 松散 JSON / 码点帮手 ----------------

    /// <summary>JS 侧 item/params 是 duck-typed 对象 → JObject；这里收拢松散读取。</summary>
    internal static string? S(JObject p, string k) => p[k] is JValue v && v.Type == JTokenType.String ? (string?)v.Value : null;

    internal static double D(JObject p, string k, double d = 0) =>
        p[k] is JValue v && v.Type is JTokenType.Float or JTokenType.Integer ? Convert.ToDouble(v.Value, CultureInfo.InvariantCulture) : d;

    internal static int I(JObject p, string k, int d = 0) =>
        p[k] is JValue v && v.Type is JTokenType.Float or JTokenType.Integer ? Convert.ToInt32(v.Value, CultureInfo.InvariantCulture) : d;

    internal static bool B(JObject p, string k) => p[k] is JValue v && v.Type == JTokenType.Boolean && (bool)v.Value!;

    /// <summary>JS 真值（false/0/""/null/undefined 为假）。</summary>
    internal static bool Truthy(JToken? t) => t switch
    {
        null => false,
        JValue v when v.Type == JTokenType.Null => false,
        JValue v when v.Type == JTokenType.Boolean => (bool)v.Value!,
        JValue v when v.Type == JTokenType.Integer => Convert.ToInt64(v.Value, CultureInfo.InvariantCulture) != 0,
        JValue v when v.Type == JTokenType.Float => !double.IsNaN(Convert.ToDouble(v.Value, CultureInfo.InvariantCulture)) && Convert.ToDouble(v.Value, CultureInfo.InvariantCulture) != 0,
        JValue v when v.Type == JTokenType.String => ((string?)v.Value)?.Length > 0,
        _ => true,
    };

    internal static JToken? StTok(StyleDef st, string k) => st.Extra != null && st.Extra.TryGetValue(k, out var v) ? v : null;

    /// <summary>[...str] 的对位：按 Unicode 码点切分（代理对合并；孤立代理保留为单元素）。</summary>
    internal static List<string> CodePoints(string s)
    {
        var list = new List<string>(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                list.Add(s.Substring(i, 2));
                i++;
            }
            else list.Add(s[i].ToString());
        }
        return list;
    }

    /// <summary>码点数（[...str].length 对位）。</summary>
    internal static int CpLen(string s) => CodePoints(s).Count;

    /// <summary>JS string.prototype.repeat。</summary>
    internal static string Repeat(string s, int n)
    {
        var sb = new StringBuilder(s.Length * Math.Max(0, n));
        for (var i = 0; i < n; i++) sb.Append(s);
        return sb.ToString();
    }

    /// <summary>JS 的 a || b || c（字符串链：取第一个非空值）。</summary>
    internal static string? OrStr(params string?[] xs) => xs.FirstOrDefault(x => !string.IsNullOrEmpty(x));
}
