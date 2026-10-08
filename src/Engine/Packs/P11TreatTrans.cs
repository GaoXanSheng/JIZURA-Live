// 移植自 frontend/src/engine/11p_treattrans.js（pack = 'treattrans'：treat 27 条 + trans 20 条）。
//
// treat：TreatDef.Plan(rng, st) → JObject（键名 = JS plan 对象字段，planner 存 cut.treatP）；
// Draw(env) 在 JLayouts.MainDraw 的 treat 调用点执行（JS apply(env, it, cut.treatP)）：
// env.It 是当前 item、env.Cut.TreatP 是参数。apply 对 item 的就地改写（fill/stroke/gradient/
// track/size/rot/lead…）直接写 env.It；charFns.push → env.CharFns.Add（管线 CombineChar 合并）。
//
// 管线缺口与降级（本文件内解决，与 P11Exit 同约定，不改共享文件）：
// 1. addPre/addPost/sized：管线钩子已接（Env.PreFn/PostFn → JLayouts.DrawFx，pre 画在文字下、
//    post 画在文字上）。本包经 PreFx/PostFx 挂钩（addPre/addPost 链式语义保留，WithFx 语境在
//    钩子体内重建）。sized 因此只按 apply 时的 size 算一次（出入场改 size 时不刷新——已知近似）。
// 2. withFx 的 ctx 级 blur（it.blur>0.4）：副本经 DrawItem 自带 it.blur 通道，不再重复施加
//    （blur<0.4 的主文字本就不过 ctx.filter，等价）。
// 3. lineSpans/glyphList 的 it.charFn：apply 时管线尚未 CombineChar，读 env.CharFn（此时为
//    null）→ 字形按静态布局位置取（JS 里该 fn 也只影响藏字判定，影响面 = 播到逐字入场时）。
// 4. QUOTED/SPOT 这两个 WeakMap<env> → ConditionalWeakTable<Env, box>（每帧 env 新建，语义同）。
//
// trans：TransDef.Draw = TransDrawFn(SKCanvas, SKImage prevA, SKImage curB, double p, TransCtx)。
// JS 的 trReg 包装保证 p<=0 精确输出 A、p>=1 精确输出 B（draw 包 save/restore）——每个条目的
// Draw 就是该包装。渲染器接入点：Renderer.cs Frame() 的 cut 间转场分支（L450-511，M2 已定型）：
// transOn 窗口内把上一刀收尾帧渲进 _transA、当前帧拷进 _transB，以 clamp(lt/dur) 调
// tdef.Draw(ctx, A, B, p, new TransCtx{Cw,Ch,Sc,ScPrev,St,P=cut.TransP,Step,T,Scale,AllowFilter,
// Seed=cut.Seed,Tmp=(w,h)=>Ensure(ref _transC,w,h)})——本包注册即生效，无需再改渲染器。
//
// 跨文件符号对位：J.drawItem→JText.DrawItem（副本经 env.DrawItem）、J.combineChar→JAnim.CombineChar、
// J.itemBands→JAnim.ItemBands(+JAnim.JArr)、J.layoutText/measure→JText/JLayouts、
// J.rgba(本地 Rgba)/r/rs/rr/h/mix/lum/contrast/fitContrast/smooth→J.*；rng.*→Rng.*。

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace JizuraLive.Engine;

public static class P11TreatTrans
{
    private const string Pk = "treattrans";
    private static int _installed;

    /// <summary>幂等注册本文件全部条目（顺序 = JS reg/trReg 出现序）。</summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;
        RegisterNeonOutline();
        RegisterChrome();
        RegisterRainbow();
        RegisterGlitchSplit();
        RegisterShadowStack();
        RegisterStencilGap();
        RegisterWaterline();
        RegisterKaraoke();
        RegisterSizeWave();
        RegisterRotateAlt();
        RegisterBaselineShift();
        RegisterFauxBold();
        RegisterCircled();
        RegisterBracketsQuote();
        RegisterReflection();
        RegisterInline();
        RegisterSticker();
        RegisterGradientSweep();
        RegisterKerningWide();
        RegisterMonoGrid();
        RegisterOutlineOffset();
        RegisterToneShadow();
        RegisterFadeChars();
        RegisterCutShift();
        RegisterFocusPull();
        RegisterSpotChar();
        RegisterRansom();

        RegisterWipe();
        RegisterDiagonalWipe();
        RegisterClockWipe();
        RegisterIrisOpen();
        RegisterPushSlide();
        RegisterCover();
        RegisterUncover();
        RegisterZoomThrough();
        RegisterDoorsOpen();
        RegisterBlinds();
        RegisterChecker();
        RegisterBlockDissolve();
        RegisterWhipPan();
        RegisterSpinOut();
        RegisterInkBlob();
        RegisterShatterTiles();
        RegisterSliceShift();
        RegisterCubeTurn();
        RegisterFlashCross();
        RegisterPixelate();
    }

    // ================= 本包 helper（JS L8-199 + L1073-1103 逐条对位） =================

    private static double D(JObject o, string k)
    {
        var v = o[k];
        return v != null && (v.Type == JTokenType.Float || v.Type == JTokenType.Integer) ? (double)v : 0;
    }

    private static double D0(JToken? t) => t != null && (t.Type == JTokenType.Float || t.Type == JTokenType.Integer) ? (double)t : 0;

    private static string? S(JObject o, string k) => o[k] is JValue v && v.Type == JTokenType.String ? (string?)v.Value : null;

    private static double NumNull(JObject o, string k, double d) => o[k] != null && (o[k]!.Type == JTokenType.Float || o[k]!.Type == JTokenType.Integer) ? (double)o[k]! : d;

    private static bool Truthy(JToken? t) => t switch
    {
        null => false,
        JValue { Type: JTokenType.Boolean } b => (bool)b.Value!,
        JValue { Type: JTokenType.String } s => s.Value is not (null or "" or "false" or "0"),
        JValue { Type: JTokenType.Integer or JTokenType.Float } n => (double)n.Value! != 0,
        _ => true,
    };

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

    /// <summary>挑一个和 col 拉得开、在背景上也读得出的 accent（JS accentFor）。</summary>
    private static string AccentFor(Scheme sc, string col, double min = 1.6) =>
        FirstOK(new[] { sc.Str("accent"), sc.Str("accent2"), sc.Str("ghostA"), sc.Str("ghostB") },
            c => Ctr(c, col) >= min && Ctr(c, ScK(sc, "bg")) >= 1.5,
            J.FitContrast(ScK(sc, "accent"), col, min + 0.3))!;

    /// <summary>画在 box 底色上的文字色（JS textOn）。</summary>
    private static string TextOn(string box, string pref, Scheme sc) =>
        Ctr(pref, box) >= 3 ? pref : Best(new[] { sc.Str("bg"), sc.Str("fg"), sc.Str("ink"), "#111111", "#FFFFFF" }, box);

    /// <summary>在背景上够显眼的标记色（JS markCol）。</summary>
    private static string MarkCol(Scheme sc, string col) =>
        FirstOK(new[] { sc.Str("accent"), sc.Str("accent2"), col }, c => Ctr(c, ScK(sc, "bg")) >= 2, col)!;

    private const string Tr = "rgba(0,0,0,0)";

    /// <summary>文字色和背景太近 = 躺在版面自己的底板上（JS onPlate）。</summary>
    private static bool OnPlate(Scheme sc, string col) => Ctr(col, ScK(sc, "bg")) < 1.5;

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

    /// <summary>碎块型出入场进行中返回 true（JS inPieces；enter.pieces=assemble、exit.pieces=explode/fall/drift）。</summary>
    private static bool InPieces(Env env, JObject it)
    {
        var c = env.Cut!;
        var enterKey = S(it, "enter") ?? c.Enter;
        var exitKey = S(it, "exit") ?? c.Exit;
        if (enterKey == "assemble" && env.Lt - NumNull(it, "delay", 0) < c.InDur * 1.3 + 0.05) return true;
        if (exitKey is "explode" or "fall" or "drift" && c.OutDur > 0 && env.Lt > c.Dur - c.OutDur - 0.3) return true;
        return false;
    }

    /// <summary>addPre + withFx 组合：钩子挂 env.PreFn（DrawFx 在裁剪/主文字前消费，层级与 JS 一致），
    /// WithFx 语境（clip/bands/local）在钩子体内重建。</summary>
    private static void PreFx(Env env, JObject it, Action fn, bool local = true)
    {
        var prev = env.PreFn;
        env.PreFn = prev != null
            ? (e, x) => { prev(e, x); WithFx(e, x, fn, local); }
            : (Action<Env, JObject>)((e, x) => WithFx(e, x, fn, local));
    }

    /// <summary>addPost + withFx 组合：钩子挂 env.PostFn（DrawFx 在主文字后消费，层级与 JS 一致）。</summary>
    private static void PostFx(Env env, JObject it, Action fn, bool local = true)
    {
        var prev = env.PostFn;
        env.PostFn = prev != null
            ? (e, x, bb) => { prev(e, x, bb); WithFx(e, x, fn, local); }
            : (Action<Env, JObject, BBox?>)((e, x, _) => WithFx(e, x, fn, local));
    }

    /// <summary>让 fn 处在和画文字相同的 clip/bands 下；local=true 再变换到 item 坐标（JS withFx）。</summary>
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

    private sealed class Span
    {
        public int Li;
        public double A0, A1, C;
        public int N;
    }

    /// <summary>逐行（竖排逐列）在 item 空间的行范围（JS lineSpans；charFn 藏掉的字不算）。</summary>
    private static List<Span> LineSpans(Env env, JObject it)
    {
        var lay = JText.LayoutText(it);
        var sx = NumNull(it, "sx", 1);
        var sy = NumNull(it, "sy", 1);
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
            {
                map[g.Li] = new Span { Li = g.Li, A0 = a0, A1 = a1, C = vert ? g.X * sx : g.Y * sy, N = 1 };
            }
            else
            {
                l.A0 = Math.Min(l.A0, a0);
                l.A1 = Math.Max(l.A1, a1);
                l.N++;
            }
        }
        return map.Values.OrderBy(v => v.Li).ToList();
    }

    private sealed class Gly
    {
        public JText.GlyphCell G = null!;
        public double X, Y, W, H, S, Rot, A;
    }

    /// <summary>item 空间里的可见字形（JS glyphList；出入场逐字位移按 apply 时刻近似，见头注 3）。</summary>
    private static List<Gly> GlyphList(Env env, JObject it)
    {
        var lay = JText.LayoutText(it);
        var sx = NumNull(it, "sx", 1);
        var sy = NumNull(it, "sy", 1);
        var @out = new List<Gly>();
        foreach (var g in lay.Cells)
        {
            if (IsSp(g.Ch)) continue;
            var c = env.CharFn?.Invoke(g.I, null, lay.N);
            if (c != null && c.Hide) continue;
            @out.Add(new Gly
            {
                G = g,
                X = g.X * sx + g.Vx * sx + (c?.Dx ?? 0),
                Y = g.Y * sy + g.Vy * sy + (c?.Dy ?? 0),
                W = g.W * sx,
                H = g.H * sy,
                S = c?.S ?? 1,
                Rot = c?.Rot ?? 0,
                A = c?.A ?? 1,
            });
        }
        return @out;
    }

    /// <summary>复制一份干净 item 给叠加通道用，钩子与特效字段全清空（JS bare）。</summary>
    private static JObject Bare(JObject i, JObject? extra = null)
    {
        var c = (JObject)i.DeepClone();
        foreach (var k in new[] { "shadow", "extrude", "pattern", "patternBg", "gradient", "strokeDash", "bands", "vbands", "clip", "clipY", "blend" })
            c[k] = null;
        c["strokeUnder"] = false;
        if (extra != null)
            foreach (var kv in extra)
                c[kv.Key] = kv.Value;
        return c;
    }

    /// <summary>dash 描字没画完时别叠 fill 副本（JS tracing）。</summary>
    private static bool Tracing(JObject i) => i["dash"] != null && D(i, "dash") < 1;

    /// <summary>副本绘制：临时换 env.CharFn（函数字段不进 JObject）。</summary>
    private static void DrawCopy(Env env, JObject copy, JAnim.CharFn? charFn = null)
    {
        var saved = env.CharFn;
        env.CharFn = charFn;
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
        return "rgba(" + r.ToString(CultureInfo.InvariantCulture) + "," + g.ToString(CultureInfo.InvariantCulture) + ","
               + b.ToString(CultureInfo.InvariantCulture) + "," + a.ToString("0.######", CultureInfo.InvariantCulture) + ")";
    }

    private static JObject Shadow(string color, double blur, double dx, double dy) =>
        new() { ["color"] = color, ["blur"] = blur, ["dx"] = dx, ["dy"] = dy };

    private static JObject SizedStroke(double stroke) => new() { ["stroke"] = stroke };

    /// <summary>渐变 stops → JArray [[pos, color],…]。</summary>
    private static JArray Grad(params (double Pos, string Col)[] stops)
    {
        var a = new JArray();
        foreach (var (pos, col) in stops)
            a.Add(new JArray { pos, col });
        return a;
    }

    // ---------------- trans 侧 helper（JS L1073-1103） ----------------

    private static double Bell(double k) => Math.Sin(Math.PI * J.Clamp(k));
    private static double IoQuart(double k) => k < 0.5 ? 8 * k * k * k * k : 1 - 8 * Math.Pow(1 - k, 4);
    private static double MinD(TransCtx i) => Math.Min(i.Cw, i.Ch);
    private static double LwOf(TransCtx i, double k = 0.006) => Math.Max(2, MinD(i) * k);

    /// <summary>在前后两个背景上都读得出的 accent（JS tAcc）。</summary>
    private static string TAcc(TransCtx i, bool second = false)
    {
        var sc = i.Sc;
        var pb = ScK(i.ScPrev ?? sc, "bg");
        var list = second
            ? new[] { sc.Str("accent2"), sc.Str("accent"), sc.Str("fg") }
            : new[] { sc.Str("accent"), sc.Str("accent2"), sc.Str("fg") };
        return FirstOK(list, c => Ctr(c, ScK(sc, "bg")) >= 1.8 && Ctr(c, pb) >= 1.4, ScK(sc, "fg"))!;
    }

    private static readonly SKSamplingOptions SampNearest = new(SKFilterMode.Nearest, SKMipmapMode.None);
    private static readonly SKSamplingOptions SampLinear = new(SKFilterMode.Linear, SKMipmapMode.None);

    private static SKPaint AlphaPaint(double alpha) => new()
    {
        Color = alpha < 0.999 ? new SKColor(255, 255, 255, (byte)Math.Round(J.Clamp(alpha) * 255)) : SKColors.White,
    };

    /// <summary>把 C 的子矩形 (x,y,w,h) 原地平移 (dx,dy)，越界部分裁掉（JS part）。</summary>
    private static void PartImg(SKCanvas ctx, SKImage c, double x, double y, double w, double h, double dx = 0, double dy = 0)
    {
        var x0 = Math.Max(0, (int)Math.Floor(x));
        var y0 = Math.Max(0, (int)Math.Floor(y));
        var x1 = Math.Min(c.Width, (int)Math.Ceiling(x + w));
        var y1 = Math.Min(c.Height, (int)Math.Ceiling(y + h));
        if (x1 - x0 < 1 || y1 - y0 < 1) return;
        using var p = AlphaPaint(1);
        ctx.DrawImage(c, new SKRect(x0, y0, x1, y1),
            new SKRect((float)(x0 + dx), (float)(y0 + dy), (float)(x0 + dx + (x1 - x0)), (float)(y0 + dy + (y1 - y0))), SampNearest, p);
    }

    /// <summary>JS scaled：以 (cx,cy) 为中心缩放整图绘制。</summary>
    private static void Scaled(SKCanvas ctx, SKImage c, double cw, double ch, double s, double? cx = null, double? cy = null,
        bool smooth = true, double alpha = 1)
    {
        var gx = cx ?? cw / 2;
        var gy = cy ?? ch / 2;
        using var p = AlphaPaint(alpha);
        ctx.DrawImage(c, new SKRect((float)(gx - gx * s), (float)(gy - gy * s), (float)(gx - gx * s + cw * s), (float)(gy - gy * s + ch * s)),
            smooth ? SampLinear : SampNearest, p);
    }

    private static void DrawImg(SKCanvas ctx, SKImage img, double x, double y, double alpha = 1, bool smooth = true)
    {
        if (alpha <= 0.003) return;
        using var p = AlphaPaint(alpha);
        ctx.DrawImage(img, new SKPoint((float)x, (float)y), smooth ? SampLinear : SampNearest, p);
    }

    private static void FillRect(SKCanvas ctx, double x, double y, double w, double h, string? color, double alpha = 1,
        SKBlendMode blend = SKBlendMode.SrcOver)
    {
        if (alpha <= 0.003) return;
        using var p = new SKPaint { Color = Env.SkColor(color ?? "#000000", Math.Min(1, alpha)), BlendMode = blend };
        ctx.DrawRect(new SKRect((float)x, (float)y, (float)(x + w), (float)(y + h)), p);
    }

    private static void StrokeRectImg(SKCanvas ctx, double x, double y, double w, double h, string? color, double lw, double alpha = 1)
    {
        if (alpha <= 0.003) return;
        using var p = new SKPaint
        {
            IsAntialias = true,
            IsStroke = true,
            StrokeWidth = (float)lw,
            Color = Env.SkColor(color ?? "#000000", Math.Min(1, alpha)),
        };
        ctx.DrawRect(new SKRect((float)x, (float)y, (float)(x + w), (float)(y + h)), p);
    }

    /// <summary>线性渐变矩形（JS createLinearGradient + fillRect）。</summary>
    private static void GradientRect(SKCanvas ctx, double x, double y, double w, double h, SKPoint g0, SKPoint g1,
        (float Pos, string Col, double A)[] stops)
    {
        using var shader = SKShader.CreateLinearGradient(g0, g1,
            stops.Select(s => Env.SkColor(s.Col, s.A)).ToArray(),
            stops.Select(s => s.Pos).ToArray(), SKShaderTileMode.Clamp);
        using var p = new SKPaint { IsAntialias = true, Shader = shader };
        ctx.DrawRect(new SKRect((float)x, (float)y, (float)(x + w), (float)(y + h)), p);
    }

    /// <summary>trReg 包装：p<=0 精确输出 A、p>=1 精确输出 B，结束时 ctx 状态干净。</summary>
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

    private static string DirPick(Rng rng, string[] list, double[] w)
    {
        var pairs = new List<(string V, double W)>();
        for (var i = 0; i < list.Length; i++) pairs.Add((list[i], w[i]));
        return rng.WPick(pairs);
    }

    private static void RegT(string key, TreatDef def) => Reg.Register("treat", key, def, Pk);
    private static void RegX(string key, TransDef def) => Reg.Register("trans", key, def, Pk);

    // ================= treat（JS reg('treat', …) 出现序） =================

    private static void RegisterNeonOutline() => RegT("neonOutline", new TreatDef
    {
        Name = "ネオン管",
        Tags = new[] { "glitch", "emotional", "pop" },
        W = 0.8,
        Plan = (rng, st) => new JObject { ["k"] = rng.Range(0.024, 0.032), ["fl"] = rng.Chance(0.75) },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it, 0.5) || InPieces(env, it)) return;
            var sc = env.Sc!;
            var col = ColOfIt(env, it);
            var dk = IsDark(ScK(sc, "bg"));
            var gc = dk
                ? FirstOK(new[] { sc.Str("accent"), sc.Str("accent2"), sc.Str("ghostA"), sc.Str("ghostB") },
                    c => J.Lum(c) > J.Lum(ScK(sc, "bg")) + 0.15 && Ctr(c, col) >= 1.2, col)!
                : FirstOK(new[] { sc.Str("accent"), sc.Str("accent2"), sc.Str("ghostA"), sc.Str("ghostB") },
                    c => Ctr(c, ScK(sc, "bg")) >= 1.8, col)!;
            var tube = dk ? J.Mix(col, "#FFFFFF", 0.4) : col;
            it["fill"] = false;
            it["strokeColor"] = tube;
            it["stroke"] = Math.Max(1.4, D(it, "size") * D(p, "k"));
            it["shadow"] = Shadow(Rgba(gc, dk ? 1 : 0.7), D(it, "size") * 0.07, 0, 0);
            // 泛光垫底（addPre 就地执行；主通道只画一次）
            if (env.Pass == "main" && !Tracing(it))
            {
                var s = D(it, "size");
                var c = Bare(it, new JObject
                {
                    ["fill"] = false,
                    ["stroke"] = s * D(p, "k") * 4.5,
                    ["strokeColor"] = gc,
                    ["alpha"] = NumNull(it, "alpha", 1) * (dk ? 0.22 : 0.16),
                    ["shadow"] = Shadow(Rgba(gc, 0.9), s * 0.16, 0, 0),
                });
                PreFx(env, it, () => DrawCopy(env, c), false);
            }
            if (Truthy(p["fl"]))
            {
                var mi = (int)D(it, "mi");
                var s0 = unchecked((int)J.H(unchecked((int)env.Cut.Seed), mi + 3, 41));
                env.CharFns.Add((gi, _g2, _n2) =>
                {
                    var r = J.R(s0, gi, unchecked((int)env.Step));
                    return r < 0.03 ? new CharXf { A = 0.22 } : r < 0.05 ? new CharXf { A = 0.6 } : null;
                });
            }
        },
    });

    private static void RegisterChrome() => RegT("chrome", new TreatDef
    {
        Name = "クローム",
        Tags = new[] { "pop", "graphic" },
        W = 0.7,
        Plan = (rng, st) => new JObject { ["v"] = rng.Pick(new[] { "silver", "silver", "sunset" }), ["h"] = rng.Range(0.5, 0.56) },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it) || InPieces(env, it)) return;
            var sc = env.Sc!;
            var col = ColOfIt(env, it);
            var light = J.Lum(col) > 0.42;
            var h = D(p, "h");
            var acc = AccentFor(sc, col, 1.3);
            var top0 = light ? "#FFFFFF" : J.Mix(col, "#FFFFFF", 0.55);
            var top1 = light ? J.Mix(col, "#000000", 0.42) : col;
            var band = J.Mix(col, "#000000", light ? 0.62 : 0.45);
            var low0 = S(p, "v") == "sunset" ? J.Mix(acc, "#FFFFFF", 0.5) : J.Mix(col, "#FFFFFF", light ? 0.75 : 0.42);
            var low1 = S(p, "v") == "sunset" ? acc : J.Mix(col, "#000000", light ? 0.12 : 0.25);
            it["gradient"] = Grad((0.08, top0), (h, top1), (h, band), (h + 0.03, band), (h + 0.03, low0), (0.94, low1));
            it["strokeColor"] = light ? J.Mix(col, "#000000", 0.7) : J.Mix(col, "#000000", 0.35);
            it["stroke"] = Math.Max(1.2, D(it, "size") * 0.022);
        },
    });

    private static void RegisterRainbow() => RegT("rainbow", new TreatDef
    {
        Name = "虹色",
        Tags = new[] { "pop", "emotional" },
        W = 0.7,
        Plan = (rng, st) => new JObject
        {
            ["v"] = rng.Pick(new[] { "drift", "drift", "steps" }),
            ["sp"] = rng.Range(0.28, 0.45),
            ["dir"] = rng.Chance(0.5) ? 1 : -1,
        },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it)) return;
            var sc = env.Sc!;
            var col = ColOfIt(env, it);
            if (OnPlate(sc, col)) return;
            var pal = new List<string> { col };
            foreach (var c in new[] { sc.Str("accent"), sc.Str("accent2"), sc.Str("ghostA"), sc.Str("ghostB"), sc.Str("sub") })
                if (c != null && Ctr(c, ScK(sc, "bg")) >= 1.8 && pal.All(q => Ctr(q, c) >= 1.25)) pal.Add(c);
            if (pal.Count < 3)
                pal.Add(J.FitContrast(J.Mix(ScK(sc, "accent"), string.IsNullOrEmpty(sc.Str("accent2")) ? ColOfIt(env, it) : sc.Str("accent2")!, 0.5), ScK(sc, "bg"), 2));
            var l = pal.Count;
            var off = (int)D(it, "mi");
            var v = S(p, "v");
            var sp = D(p, "sp");
            var dir = D(p, "dir");
            env.CharFns.Add((gi, g, n) =>
            {
                if (v == "steps")
                {
                    var k = ((gi + off + (int)Math.Floor(env.Ltb * 2.2) * (int)dir) % l + l) % l;
                    return new CharXf { Color = pal[k] };
                }
                var u = (gi + off) * sp - env.Ltb * 0.75 * dir;
                var k2 = ((u % l) + l) % l;
                var a = (int)Math.Floor(k2);
                var f = J.Smooth(0.3, 0.7, k2 - a);
                return new CharXf { Color = J.Mix(pal[a], pal[(a + 1) % l], f) };
            });
        },
    });

    private static void RegisterGlitchSplit() => RegT("glitchSplit", new TreatDef
    {
        Name = "色版ズレ",
        Tags = new[] { "glitch", "pop" },
        W = 0.8,
        Plan = (rng, st) => new JObject { ["d"] = rng.Range(0.06, 0.08), ["up"] = rng.Chance(0.3) },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it) || InPieces(env, it)) return;
            var sc = env.Sc!;
            var col = ColOfIt(env, it);
            var dk = IsDark(ScK(sc, "bg"));
            var cands = new[] { sc.Str("ghostA"), sc.Str("ghostB"), sc.Str("accent"), sc.Str("accent2") }
                .Where(c => c != null && Ctr(c, ScK(sc, "bg")) >= 1.4 && Ctr(c, col) >= 1.15).ToList();
            var ca = cands.Count > 0 ? cands[0] : AccentFor(sc, col, 1.4);
            var cb = cands.Find(c => Ctr(c, ca) >= 1.3) ?? J.Mix(ca, ScK(sc, "bg"), 0.4);
            var mi = (int)D(it, "mi");
            var s0 = unchecked((int)J.H(unchecked((int)env.Cut.Seed), mi + 5, 43));
            // addPre（就地执行）
            var step = unchecked((int)env.Step);
            var r = J.R(s0, step);
            var hit = r < 0.2 && env.Lt > env.Cut.InDur * 0.5;
            if (hit && (it["bands"] == null) && (it["vbands"] == null) && env.Pass == "main")
            {
                var size0 = D(it, "size");
                it["bands"] = JAnim.JArr(JAnim.ItemBands(env, it, 4, (k, n) =>
                    J.R(s0, step, k, 7) < 0.55 ? J.Rs(s0, step, k, 8) * size0 * 0.16 : 0));
            }
            if (env.Pass != "main" || Tracing(it)) return;
            var s = D(it, "size");
            var d = s * D(p, "d") * (hit ? 1.8 + J.R(s0, step, 2) : 1);
            var dy = (Truthy(p["up"]) ? d * 0.45 : 0) + (hit ? J.Rs(s0, step, 3) * s * 0.02 : 0);
            JObject Cp(string c, double k) => Bare(it, new JObject
            {
                ["x"] = D(it, "x") + d * k,
                ["y"] = D(it, "y") + dy * k,
                ["color"] = c,
                ["strokeColor"] = c,
                ["stroke"] = 0.0,
                ["blend"] = dk ? "screen" : "multiply",
                ["alpha"] = NumNull(it, "alpha", 1) * 0.95,
            });
            PreFx(env, it, () =>
            {
                DrawCopy(env, Cp(ca, -1));
                DrawCopy(env, Cp(cb, 1));
            }, false);
        },
    });

    private static void RegisterShadowStack() => RegT("shadowStack", new TreatDef
    {
        Name = "多重影",
        Tags = new[] { "pop", "graphic" },
        W = 0.8,
        Plan = (rng, st) => new JObject
        {
            ["d"] = rng.Range(0.04, 0.055),
            ["dir"] = new JArray(rng.Pick(new[] { new[] { 1.0, 1.0 }, new[] { 1.0, 1.0 }, new[] { -1.0, 1.0 }, new[] { 1.0, 0.5 }, new[] { 0.0, 1.0 } })),
            ["n"] = (double)rng.Pick(new[] { 3, 3, 4 }),
        },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it) || InPieces(env, it)) return;
            var sc = env.Sc!;
            var col = ColOfIt(env, it);
            var cols = new List<string>();
            foreach (var c in new[] { sc.Str("accent"), sc.Str("accent2"), sc.Str("ghostB"), sc.Str("ghostA"), sc.Str("ink"), sc.Str("sub"), sc.Str("fg") })
                if (c != null && Ctr(c, ScK(sc, "bg")) >= 1.4 && Ctr(c, col) >= 1.12 && cols.All(q => Ctr(q, c) >= 1.12)) cols.Add(c);
            var n = (int)D(p, "n");
            var dir = ((JArray?)p["dir"])?.Select(D0).ToArray() ?? new[] { 1.0, 1.0 };
            while (cols.Count < n)
                cols.Add(J.Mix(cols.Count > 0 ? cols[^1] : AccentFor(sc, col), ScK(sc, "bg"), 0.35));
            it["strokeColor"] = ScK(sc, "bg");
            it["strokeUnder"] = true;
            it["stroke"] = Math.Max(1.5, D(it, "size") * 0.028);
            // addPre（就地执行）
            if (env.Pass != "main" || Tracing(it)) return;
            var s = D(it, "size");
            var d = s * D(p, "d");
            PreFx(env, it, () =>
            {
                for (var k = n; k >= 1; k--)
                {
                    if (k - 1 >= cols.Count) continue;
                    DrawCopy(env, Bare(it, new JObject
                    {
                        ["x"] = D(it, "x") + dir[0] * d * k,
                        ["y"] = D(it, "y") + dir[1] * d * k,
                        ["color"] = cols[k - 1],
                        ["stroke"] = Math.Max(1.5, s * 0.028),
                        ["strokeColor"] = ScK(sc, "bg"),
                        ["strokeUnder"] = true,
                    }));
                }
            }, false);
        },
    });

    private static void RegisterStencilGap() => RegT("stencilGap", new TreatDef
    {
        Name = "ステンシル字",
        Tags = new[] { "graphic", "editorial", "glitch" },
        W = 0.6,
        Plan = (rng, st) => new JObject
        {
            ["v"] = rng.Pick(new[] { "one", "one", "two" }),
            ["at"] = rng.Range(-0.05, 0.06),
            ["g"] = rng.Range(0.034, 0.048),
        },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it) || InPieces(env, it)) return;
            var g = D(p, "g") * J.E.OutCubic(InP(env, it, 0.04, 0.4)) * (1 - J.E.InCubic(env.POut));
            if (g < 0.003) return;
            var cuts = S(p, "v") == "two" ? new[] { -0.13, 0.13 } : new[] { D(p, "at") };
            var parts = new List<(double Lo, double Hi)>();
            var lo = -0.8;
            foreach (var c in cuts)
            {
                parts.Add((lo, c - g));
                lo = c + g;
            }
            parts.Add((lo, 0.8));
            var fns = parts.Select(pr => (JAnim.CharFn)((gi, gl, n) => new CharXf { ClipY = new[] { pr.Lo, pr.Hi } })).ToArray();
            env.CharFns.Add(fns[0]);
            // addPre：其余切段的副本（就地执行）
            if (Tracing(it)) return;
            for (var k = 1; k < fns.Length; k++)
            {
                var fk = fns[k];
                var rest = env.CharFns.Where(f => f != fns[0]).ToList();
                var combined = JAnim.CombineChar(rest.Concat(new[] { fk }).ToList());
                var c = Bare(it);
                PreFx(env, it, () => DrawCopy(env, c, combined), false);
            }
        },
    });

    private static void RegisterWaterline() => RegT("waterline", new TreatDef
    {
        Name = "水位",
        Tags = new[] { "emotional", "pop", "calm" },
        W = 0.6,
        Plan = (rng, st) => new JObject
        {
            ["lvl"] = rng.Range(0.46, 0.58),
            ["c"] = (double)rng.Int(0, 1),
            ["k"] = rng.Range(0.02, 0.026),
        },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it, 0.5) || InPieces(env, it)) return;
            var sc = env.Sc!;
            var col = ColOfIt(env, it);
            var liq = D(p, "c") != 0 ? AccentFor(sc, col, 1.3) : col;
            var q = J.E.InOutCubic(InP(env, it, 0.05, 0.8)) * (1 - J.E.InCubic(env.POut));
            var mi = (int)D(it, "mi");
            var lvl = J.Lerp(1.02, D(p, "lvl"), q) + Math.Sin(env.Ltb * 2.6 + mi) * 0.022 * q;
            var surf = J.Mix(liq, "#FFFFFF", IsDark(liq) ? 0.35 : 0.5);
            it["gradient"] = Grad(
                (0.0, Tr),
                (J.Clamp(lvl - 0.001), Tr),
                (J.Clamp(lvl), surf),
                (J.Clamp(lvl + 0.025), surf),
                (J.Clamp(lvl + 0.025), liq),
                (1.0, liq));
            it["strokeColor"] = col;
            it["stroke"] = Math.Max(1.3, D(it, "size") * D(p, "k"));
            if (env.Pass != "main") it["fill"] = false;
        },
    });

    private static void RegisterKaraoke() => RegT("karaoke", new TreatDef
    {
        Name = "カラオケ",
        Tags = new[] { "emotional", "pop", "editorial" },
        W = 0.9,
        Plan = (rng, st) => new JObject { ["sp"] = rng.Range(0.7, 0.85), ["ol"] = rng.Chance(0.55) },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it, 0.9)) return;
            var sc = env.Sc!;
            var col = ColOfIt(env, it);
            var hot = AccentFor(sc, col, 1.8);
            var cut = env.Cut;
            if (OnPlate(sc, col)) return;
            if (Truthy(p["ol"]))
            {
                var oc = Best(new[] { sc.Str("bg"), sc.Str("ink"), "#111111", "#FFFFFF" }, col);
                it["strokeColor"] = oc;
                it["strokeUnder"] = true;
                it["stroke"] = Math.Max(1.5, D(it, "size") * 0.06);
            }
            // addPost（就地执行）
            if (env.Pass != "main" || Tracing(it)) return;
            var t0 = cut.InDur * 0.6;
            var t = Math.Max(0.3, (cut.Dur - cut.OutDur - t0) * D(p, "sp"));
            var q = J.E.InOutSine(J.Clamp((env.Lt - NumNull(it, "delay", 0) - t0) / t));
            if (q <= 0) return;
            var spans = LineSpans(env, it);
            if (spans.Count == 0) return;
            var s = D(it, "size");
            var sx = NumNull(it, "sx", 1);
            var sy = NumNull(it, "sy", 1);
            var pad = s * 0.12;
            var tot = spans.Sum(l => l.A1 - l.A0);
            var rem = q * tot;
            var c = Bare(it, new JObject { ["x"] = 0.0, ["y"] = 0.0, ["rot"] = 0.0, ["skew"] = 0.0, ["color"] = hot, ["blur"] = 0.0, ["strokeUnder"] = it["strokeUnder"] });
            var vertical = Truthy(it["vertical"]);
            PostFx(env, it, () =>
            {
                var ctx = env.Ctx;
                ctx.Save();
                using (var clip = new SKPath())
                {
                    foreach (var l in spans)
                    {
                        if (rem <= 0) break;
                        var len = l.A1 - l.A0;
                        var take = Math.Min(len, rem);
                        rem -= take;
                        var a0 = l.A0 - pad;
                        var a1 = l.A0 + take + (take >= len - 0.01 ? pad : 0);
                        var cr = s * (vertical ? sx : sy) * 0.72;
                        if (vertical) clip.AddRect(new SKRect((float)(l.C - cr), (float)a0, (float)(l.C + cr), (float)a1));
                        else clip.AddRect(new SKRect((float)a0, (float)(l.C - cr), (float)a1, (float)(l.C + cr)));
                    }
                    ctx.ClipPath(clip, antialias: false);
                }
                DrawCopy(env, c);
                ctx.Restore();
            });
        },
    });

    private static void RegisterSizeWave() => RegT("sizeWave", new TreatDef
    {
        Name = "大小リズム",
        Tags = new[] { "pop", "graphic", "editorial" },
        W = 0.7,
        Plan = (rng, st) => new JObject
        {
            ["v"] = rng.Pick(new[] { "alt", "kanji", "kanji", "ramp", "wave" }),
            ["k"] = rng.Range(0.64, 0.74),
            ["rev"] = rng.Chance(0.4),
        },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it, 0.5)) return;
            var lay = JText.LayoutText(it);
            var n = lay.N;
            var off = (int)D(it, "mi");
            if (n == 0) return;
            var chars = lay.Cells.Select(g => g.Ch).Where(c => c.Trim().Length > 0).ToList();
            bool IsK(char c) => J.IsKanji(c) || J.IsKata(c) || J.IsLatin(c);
            var v = S(p, "v") ?? "alt";
            if (v == "kanji" && !(chars.Any(c => IsK(c[0])) && chars.Any(c => !IsK(c[0])))) v = "alt";
            if ((v is "ramp" or "wave") && chars.Count < 3) v = "alt";
            var sp = new double[n];
            for (var i = 0; i < n; i++) sp[i] = 1;
            foreach (var g in lay.Cells)
            {
                var u = g.N > 1 ? g.Ci / (double)(g.N - 1) : 0.5;
                double s;
                if (v == "alt") s = (g.Ci + g.Li + off) % 2 != 0 ? D(p, "k") : 1.04;
                else if (v == "kanji") s = IsK(g.Ch[0]) ? 1.08 : D(p, "k") + 0.04;
                else if (v == "ramp") s = J.Lerp(1.1, D(p, "k"), Truthy(p["rev"]) ? 1 - u : u);
                else s = 0.87 + 0.17 * Math.Sin(g.Ci * 1.25 + off);
                if (J.IsPunct(g.Ch[0])) s = Math.Min(s, 0.9);
                if (g.I < n) sp[g.I] = s;
            }
            // 沿行重排让小字靠拢；偏移按 em 存
            var fArr = new double[n];
            var size0 = D(it, "size");
            var tr = NumNull(it, "track", 0) * size0;
            var vert = Truthy(it["vertical"]);
            var byLine = lay.Cells.GroupBy(g => g.Li).ToDictionary(kvp => kvp.Key, kvp => kvp.ToList());
            foreach (var gs in byLine.Values)
            {
                double Ext(JText.GlyphCell g) => vert ? g.H : g.W;
                double Pos(JText.GlyphCell g) => vert ? g.Y : g.X;
                var tot = gs.Sum(g => Ext(g) * (g.I < n ? sp[g.I] : 1)) + tr * (gs.Count - 1);
                var old0 = Pos(gs[0]) - Ext(gs[0]) / 2;
                var old1 = Pos(gs[^1]) + Ext(gs[^1]) / 2;
                var align = S(it, "align");
                var pp = align == "left" ? old0 : align == "right" && !vert ? old1 - tot : (old0 + old1) / 2 - tot / 2;
                foreach (var g in gs)
                {
                    var w = Ext(g) * (g.I < n ? sp[g.I] : 1);
                    if (g.I < n) fArr[g.I] = (pp + w / 2 - Pos(g)) / size0;
                    pp += w + tr;
                }
            }
            var sy = NumNull(it, "sy", 1);
            var sx = NumNull(it, "sx", 1);
            env.CharFns.Add((gi, g, n2) =>
            {
                if (gi < 0 || gi >= n) return null;
                var s = sp[gi];
                return vert
                    ? new CharXf { S = s, Dy = fArr[gi] * D(g!, "w") * sy }
                    : new CharXf { S = s, Dx = fArr[gi] * D(g!, "h") * sx, Dy = (1 - s) * 0.4 * D(g!, "h") * sy };
            });
        },
    });

    private static void RegisterRotateAlt() => RegT("rotateAlt", new TreatDef
    {
        Name = "揺れ字",
        Tags = new[] { "pop", "emotional" },
        W = 0.7,
        Safe = true,
        Plan = (rng, st) => new JObject { ["a"] = rng.Range(9, 14), ["v"] = rng.Pick(new[] { "alt", "alt", "rand" }) },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it, 0.5)) return;
            var off = (int)D(it, "mi");
            var s0 = unchecked((int)J.H(unchecked((int)env.Cut.Seed), off + 9, 47));
            var a = D(p, "a");
            var v = S(p, "v");
            env.CharFns.Add((gi, g, n) =>
            {
                if (g != null && J.IsPunct((S(g, "ch") ?? " ")[0])) return null;
                var sg = (gi + off) % 2 != 0 ? 1 : -1;
                var r = v == "rand" ? sg * a * J.Rr(0.45, 1.25, s0, gi) : sg * a;
                return new CharXf { Rot = r, S = 0.94 };
            });
        },
    });

    private static void RegisterBaselineShift() => RegT("baselineShift", new TreatDef
    {
        Name = "段違い",
        Tags = new[] { "pop", "graphic" },
        W = 0.7,
        Safe = true,
        Plan = (rng, st) => new JObject
        {
            ["v"] = rng.Pick(new[] { "alt", "alt", "stairs", "arc" }),
            ["k"] = rng.Range(0.08, 0.11),
            ["dir"] = rng.Chance(0.5) ? 1 : -1,
        },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it, 0.5)) return;
            var off = (int)D(it, "mi");
            var vert = Truthy(it["vertical"]);
            var kk = (S(it, "text") ?? "").Contains('\n') ? 0.7 : 1;
            var v = S(p, "v");
            var k = D(p, "k");
            var dir = D(p, "dir");
            var sx = NumNull(it, "sx", 1);
            var sy = NumNull(it, "sy", 1);
            env.CharFns.Add((gi, g, n) =>
            {
                if (g == null) return null;
                var cn = (int)D(g, "n");
                var ci = (int)D(g, "ci");
                var u = cn > 1 ? ci / (double)(cn - 1) : 0.5;
                double o;
                if (v == "stairs") o = (u - 0.5) * k * Math.Min(3.2, cn * 0.55) * dir;
                else if (v == "arc") o = (Math.Sin(Math.PI * u) - 0.6) * k * 2.2;
                else o = ((ci + off) % 2 != 0 ? 1 : -1) * k;
                return vert
                    ? new CharXf { Dx = o * kk * D(g, "w") * sx }
                    : new CharXf { Dy = -o * kk * D(g, "h") * sy };
            });
        },
    });

    private static void RegisterFauxBold() => RegT("fauxBold", new TreatDef
    {
        Name = "極太",
        Tags = new[] { "graphic", "pop", "editorial" },
        W = 0.5,
        Safe = true,
        Plan = (rng, st) => new JObject { ["k"] = rng.Range(0.035, 0.05) },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it)) return;
            it["strokeColor"] = ColOfIt(env, it);
            it["track"] = NumNull(it, "track", 0) + D(p, "k");
            it["stroke"] = InPieces(env, it) ? 0 : Math.Max(1, D(it, "size") * D(p, "k"));
        },
    });

    private static void RegisterCircled() => RegT("circled", new TreatDef
    {
        Name = "丸囲み",
        Tags = new[] { "pop", "graphic", "editorial" },
        W = 0.6,
        Plan = (rng, st) => new JObject
        {
            ["v"] = rng.Pick(new[] { "ring", "ring", "disc" }),
            ["k"] = rng.Range(0.68, 0.74),
        },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it, 0.9)) return;
            var sc = env.Sc!;
            var col = ColOfIt(env, it);
            var disc = S(p, "v") == "disc";
            if (OnPlate(sc, col)) return;
            var rc = disc
                ? FirstOK(new[] { sc.Str("accent"), sc.Str("accent2"), sc.Str("ink"), sc.Str("fg") },
                    c => Ctr(c, ScK(sc, "bg")) >= 2 && Ctr(c, col) >= 1.3, sc.Str("fg"))!
                : MarkCol(sc, col);
            var tc = disc ? TextOn(rc, col, sc) : col;
            double QOf(Env e, int gi) =>
                J.E.OutBack(J.Clamp((e.Lt - NumNull(it, "delay", 0) - gi * 0.04) / 0.26), 1.6)
                * (1 - J.E.InCubic(J.Clamp(e.POut * 1.4 - gi * 0.03)));
            var k = D(p, "k");
            env.CharFns.Add((gi, g, n) =>
            {
                if (g != null && J.IsPunct((S(g, "ch") ?? " ")[0])) return null;
                return disc && tc != col && QOf(env, gi) > 0.55
                    ? new CharXf { S = k, Color = tc }
                    : new CharXf { S = k };
            });
            // addPre（就地执行）
            PreFx(env, it, () =>
            {
                var s = D(it, "size");
                var a = NumNull(it, "alpha", 1);
                var m = Math.Min(NumNull(it, "sx", 1), NumNull(it, "sy", 1));
                foreach (var gl in GlyphList(env, it))
                {
                    if (J.IsPunct(gl.G.Ch[0])) continue;
                    var q = QOf(env, gl.G.I);
                    if (q <= 0.01) continue;
                    var r = s * 0.49 * m * (gl.S / k) * q;
                    if (disc) env.Circle(gl.X, gl.Y, r, rc, null, 0, a * gl.A, true);
                    else env.Circle(gl.X, gl.Y, r * 0.97, null, rc, Math.Max(1.5, s * 0.045), a * gl.A, true);
                }
            });
        },
    });

    // 单字 item 首末配对标记（JS QUOTED WeakMap）
    private static readonly ConditionalWeakTable<Env, StrongBox<bool>> Quoted = new();

    private static void RegisterBracketsQuote() => RegT("bracketsQuote", new TreatDef
    {
        Name = "かぎ括弧",
        Tags = new[] { "editorial", "emotional", "calm" },
        W = 0.6,
        Plan = (rng, st) => new JObject
        {
            ["v"] = rng.Pick(new[] { "single", "single", "double" }),
            ["k"] = rng.Range(0.05, 0.065),
        },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it, 0.9)) return;
            static string Norm(string? t) => Regex.Replace(t ?? "", @"[\s\u3000]", "");
            var whole = Norm(env.Cut!.Text);
            var mine = Norm(S(it, "text"));
            if (mine.Length == 0 || whole.Length == 0) return;
            var open = whole.StartsWith(mine, StringComparison.Ordinal);
            var close = whole.EndsWith(mine, StringComparison.Ordinal);
            if (!open && !close) return;
            if (mine == whole) Quoted.AddOrUpdate(env, new StrongBox<bool>(true));
            else if (JLayouts.CodePoints(mine).Count == 1)
            {
                var n = JLayouts.CodePoints(whole).Count;
                var miTok = it["mi"];
                var miV = miTok != null ? D0(miTok) : -1;
                if ((Quoted.TryGetValue(env, out var box) && box.Value) || miTok == null || miV != Math.Round(miV)) return;
                open = open && (int)miV == 0;
                close = close && (int)miV == n - 1;
                if (!open && !close) return;
            }
            var sc = env.Sc!;
            var lc = MarkCol(sc, ColOfIt(env, it));
            // 超宽就缩字号，给括弧让位
            var m = JLayouts.Measure(it);
            var vert = Truthy(it["vertical"]);
            var along = vert ? m.H : m.W;
            var room = D(it, "size") * 1.1;
            var cap = (vert ? (double)env.H : env.W) * 0.92;
            if (along + room > cap && along < cap * 1.05)
                it["size"] = D(it, "size") * Math.Max(0.72, cap / (along + room));
            // addPost（就地执行）
            PostFx(env, it, () =>
            {
                var spans = LineSpans(env, it);
                if (spans.Count == 0) return;
                var s = D(it, "size") * Math.Min(NumNull(it, "sx", 1), NumNull(it, "sy", 1));
                var a = NumNull(it, "alpha", 1);
                var q = J.E.OutCubic(InP(env, it, env.Cut!.InDur * 0.45, 0.35)) * (1 - J.E.InCubic(env.POut));
                if (q <= 0.01) return;
                var l0 = spans[0];
                var l1 = spans[^1];
                var lw = Math.Max(1.5, s * D(p, "k"));
                var g = s * 0.3;
                var arm = s * 0.36;
                var leg = s * 0.74;
                void DrawSeg(double[][] pts) => env.PolyPartial(pts, q, lc, lw, a, false);
                void One(double d)
                {
                    if (!vert)
                    {
                        var xl = l0.A0 - g - d;
                        var yT = l0.C - s * 0.52 - d;
                        var xr = l1.A1 + g + d;
                        var yB = l1.C + s * 0.52 + d;
                        if (open) DrawSeg(new[] { new[] { xl + arm, yT }, new[] { xl, yT }, new[] { xl, yT + leg } });
                        if (close) DrawSeg(new[] { new[] { xr - arm, yB }, new[] { xr, yB }, new[] { xr, yB - leg } });
                    }
                    else
                    {
                        var xr = l0.C + s * 0.52 + d;
                        var yT = l0.A0 - g - d;
                        var xl = l1.C - s * 0.52 - d;
                        var yB = l1.A1 + g + d;
                        if (open) DrawSeg(new[] { new[] { xr, yT + arm }, new[] { xr, yT }, new[] { xr - leg, yT } });
                        if (close) DrawSeg(new[] { new[] { xl, yB - arm }, new[] { xl, yB }, new[] { xl + leg, yB } });
                    }
                }
                One(0);
                if (S(p, "v") == "double") One(-lw * 2.2);
            });
        },
    });

    private static void RegisterReflection() => RegT("reflection", new TreatDef
    {
        Name = "映り込み",
        Tags = new[] { "emotional", "calm", "editorial" },
        W = 0.6,
        Plan = (rng, st) => new JObject
        {
            ["k"] = rng.Range(0.6, 0.78),
            ["a"] = rng.Range(0.34, 0.46),
            ["gap"] = rng.Range(0.04, 0.09),
        },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it, 0.9)) return;
            var col = ColOfIt(env, it);
            // addPre（就地执行）
            if (env.Pass != "main" || Tracing(it) || InPieces(env, it)) return;
            var m = JLayouts.Measure(it);
            var s = D(it, "size");
            var sy = NumNull(it, "sy", 1);
            var k = D(p, "k");
            var vert = Truthy(it["vertical"]);
            var alignLeft = S(it, "align") == "left";
            var yb = vert && alignLeft ? m.H : m.H / 2;
            var gap = s * sy * D(p, "gap");
            var cf = env.CharFn;
            JAnim.CharFn? wrapFn = null;
            if (cf != null)
                wrapFn = (gi, g, n) =>
                {
                    var r = cf(gi, g, n);
                    if (r == null) return null;
                    return new CharXf
                    {
                        Hide = r.Hide,
                        Dx = r.Dx,
                        Dy = -(r.Dy) * k,
                        Rot = -(r.Rot),
                        S = r.S,
                        A = r.A,
                        Sx = r.Sx,
                        Sy = r.Sy,
                        Ch = r.Ch,
                        Skew = r.Skew,
                        Blur = r.Blur,
                        ClipX = r.ClipX,
                        ClipY = r.ClipY,
                    };
                };
            var c = Bare(it, new JObject
            {
                ["x"] = 0.0,
                ["y"] = yb + gap + yb * k,
                ["rot"] = 0.0,
                ["skew"] = 0.0,
                ["sy"] = -sy * k,
                ["blur"] = 0.0,
                ["gradient"] = Grad((0.0, Tr), (0.42, Rgba(col, 0.12)), (1.0, Rgba(col, 1.0))),
                ["alpha"] = NumNull(it, "alpha", 1) * D(p, "a"),
            });
            PreFx(env, it, () =>
            {
                var ctx = env.Ctx;
                ctx.Save();
                ctx.ClipRect(new SKRect((float)(-env.W * 3), (float)(yb + gap * 0.5), (float)(env.W * 3), (float)(yb + gap * 0.5 + gap * 0.5 + s * sy * k * 1.05)));
                DrawCopy(env, c, wrapFn);
                ctx.Restore();
            });
        },
    });

    private static void RegisterInline() => RegT("inline", new TreatDef
    {
        Name = "インライン",
        Tags = new[] { "editorial", "pop", "graphic" },
        W = 0.6,
        Plan = (rng, st) => new JObject
        {
            ["a"] = rng.Range(0.017, 0.022),
            ["b"] = rng.Range(0.016, 0.021),
            ["c"] = rng.Chance(0.3),
        },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it) || InPieces(env, it)) return;
            var sc = env.Sc!;
            var col = ColOfIt(env, it);
            var line = Truthy(p["c"])
                ? FirstOK(new[] { sc.Str("accent"), sc.Str("accent2") }, c => Ctr(c, col) >= 2, sc.Str("bg"))!
                : ScK(sc, "bg");
            // addPost（就地执行）
            if (env.Pass != "main" || Tracing(it) || InPieces(env, it)) return;
            var s = D(it, "size");
            PostFx(env, it, () =>
            {
                DrawCopy(env, Bare(it, new JObject { ["fill"] = false, ["stroke"] = s * (D(p, "a") + D(p, "b")) * 2, ["strokeColor"] = line }), null);
                DrawCopy(env, Bare(it, new JObject { ["fill"] = false, ["stroke"] = s * D(p, "a") * 2, ["strokeColor"] = col }), null);
            }, false);
        },
    });

    private static void RegisterSticker() => RegT("sticker", new TreatDef
    {
        Name = "シール縁",
        Tags = new[] { "pop", "graphic" },
        W = 0.8,
        Plan = (rng, st) => new JObject
        {
            ["k"] = rng.Range(0.13, 0.17),
            ["rot"] = rng.Range(2, 4) * (rng.Chance(0.5) ? 1 : -1),
            ["sh"] = rng.Range(0.035, 0.05),
        },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it, 0.9)) return;
            var sc = env.Sc!;
            var col = ColOfIt(env, it);
            var dk = IsDark(ScK(sc, "bg"));
            var paper = FirstOK(new[] { sc.Str("ink"), sc.Str("fg"), sc.Str("sub") }, c => J.Lum(c) > 0.78, "#FFFFFF")!;
            var key = Ctr(col, paper) < 2.5
                ? FirstOK(new[] { sc.Str("accent"), sc.Str("accent2"), sc.Str("ghostB"), sc.Str("ghostA") },
                    c => Ctr(c, paper) >= 2.2 && Ctr(c, col) >= 1.8, "#111111")
                : null;
            var edge = J.Mix(paper, "#000000", 0.16);
            if (!Truthy(it["vertical"]) || GlyphN(S(it, "text")) <= 4)
                it["rot"] = NumNull(it, "rot", 0) + D(p, "rot") * ((int)D(it, "mi") % 2 != 0 ? -0.7 : 1);
            // addPre（就地执行）
            if (env.Pass != "main" || InPieces(env, it)) return;
            var s = D(it, "size");
            PreFx(env, it, () =>
            {
                DrawCopy(env, Bare(it, new JObject
                {
                    ["fill"] = false,
                    ["stroke"] = s * (D(p, "k") + 0.016),
                    ["strokeColor"] = edge,
                    ["shadow"] = Shadow(Rgba("#000000", dk ? 0.6 : 0.32), s * 0.05, s * 0.015, s * D(p, "sh")),
                }), null);
                DrawCopy(env, Bare(it, new JObject { ["fill"] = false, ["stroke"] = s * D(p, "k"), ["strokeColor"] = paper }), null);
                if (key != null)
                    DrawCopy(env, Bare(it, new JObject { ["fill"] = false, ["stroke"] = s * 0.07, ["strokeColor"] = key }), null);
            }, false);
        },
    });

    private static void RegisterGradientSweep() => RegT("gradientSweep", new TreatDef
    {
        Name = "光沢スイープ",
        Tags = new[] { "pop", "emotional" },
        W = 0.7,
        Plan = (rng, st) => new JObject
        {
            ["v"] = rng.Pick(new[] { "glint", "glint", "tide" }),
            ["per"] = rng.Range(1.5, 2.3),
            ["ph"] = rng.Range(0, 1),
        },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it) || InPieces(env, it)) return;
            var sc = env.Sc!;
            var col = ColOfIt(env, it);
            var t = env.Ltb / D(p, "per") + D(p, "ph");
            if (S(p, "v") == "tide")
            {
                var c2 = AccentFor(sc, col, 1.5);
                var u = 0.52 + 0.3 * Math.Sin(t * J.TAU);
                var line = J.Mix(c2, "#FFFFFF", 0.45);
                it["gradient"] = Grad((0.0, col), (u - 0.02, col), (u - 0.02, line), (u + 0.01, line), (u + 0.01, c2), (1.0, c2));
                return;
            }
            var hl = J.Lum(col) > 0.6
                ? FirstOK(new[] { sc.Str("accent"), sc.Str("accent2"), sc.Str("ghostA"), sc.Str("ghostB") },
                    c => J.Lum(c) > 0.3 && Ctr(c, col) >= 1.3, J.Mix(col, ScK(sc, "bg"), 0.45))!
                : J.Mix(col, "#FFFFFF", 0.72);
            var u2 = (t % 1) * 1.7 - 0.35;
            const double w = 0.13;
            it["gradient"] = Grad(
                (0.0, col),
                (J.Clamp(u2 - w), col),
                (J.Clamp(u2), hl),
                (J.Clamp(u2 + w * 0.35), hl),
                (J.Clamp(u2 + w * 1.2), col),
                (1.0, col));
        },
    });

    private static void RegisterKerningWide() => RegT("kerningWide", new TreatDef
    {
        Name = "字間広め",
        Tags = new[] { "editorial", "calm", "emotional" },
        W = 0.6,
        Safe = true,
        Plan = (rng, st) => new JObject { ["t"] = rng.Range(0.32, 0.55), ["grow"] = rng.Range(1.04, 1.14) },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || string.IsNullOrEmpty(S(it, "text")) || GlyphN(S(it, "text")) < 2) return;
            var vert = Truthy(it["vertical"]);
            var m0 = JLayouts.Measure(it);
            var a0 = vert ? m0.H : m0.W;
            it["track"] = NumNull(it, "track", 0) + (vert ? D(p, "t") * 0.7 : D(p, "t"));
            var m1 = JLayouts.Measure(it);
            var a1 = vert ? m1.H : m1.W;
            var cap = (vert ? (double)env.H : env.W) * 0.9;
            var k = Math.Min(1, a0 * D(p, "grow") / Math.Max(1, a1));
            if (a1 * k > cap && a0 <= cap) k = Math.Min(k, cap / a1);
            it["size"] = D(it, "size") * Math.Max(0.55, k);
        },
    });

    private static void RegisterMonoGrid() => RegT("monoGrid", new TreatDef
    {
        Name = "原稿用紙風",
        Tags = new[] { "editorial", "calm", "emotional" },
        W = 0.5,
        Plan = (rng, st) => new JObject { ["pitch"] = rng.Range(1.18, 1.26), ["c"] = (double)(rng.Chance(0.65) ? 1 : 0) },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it, 0.9)) return;
            var sc = env.Sc!;
            var col = ColOfIt(env, it);
            var vert = Truthy(it["vertical"]);
            var lc = D(p, "c") != 0
                ? FirstOK(new[] { sc.Str("accent"), sc.Str("accent2") }, c => Ctr(c, ScK(sc, "bg")) >= 1.5, sc.Str("sub"))!
                : sc.Str("sub")!;
            var lines = (S(it, "text") ?? "").Split('\n');
            var nMax = Math.Max(1, lines.Max(l => JLayouts.CodePoints(l).Count));
            var pitch = D(p, "pitch");
            var m0 = JLayouts.Measure(it);
            var along0 = vert ? m0.H : m0.W;
            var along1 = nMax * pitch * D(it, "size") * (vert ? NumNull(it, "sy", 1) : NumNull(it, "sx", 1));
            if (along1 > along0 * 1.06) it["size"] = D(it, "size") * (along0 * 1.06 / along1);
            it["lead"] = Math.Max(NumNull(it, "lead", 1.3), pitch + 0.16);
            double Tgt(int ci, int n, double sz)
            {
                var q = pitch * sz;
                var align = S(it, "align");
                if (align == "left") return (ci + 0.5) * q;
                if (align == "right" && !vert) return -(n - ci - 0.5) * q;
                return (ci - (n - 1) / 2.0) * q;
            }
            var sy = NumNull(it, "sy", 1);
            var sx = NumNull(it, "sx", 1);
            env.CharFns.Add((gi, g, n) =>
            {
                if (g == null) return null;
                var gci = (int)D(g, "ci");
                var gn = (int)D(g, "n");
                return vert
                    ? new CharXf { Dy = (Tgt(gci, gn, D(g, "w")) - D(g, "y")) * sy, S = 0.86 }
                    : new CharXf { Dx = (Tgt(gci, gn, D(g, "h")) - D(g, "x")) * sx, S = 0.86 };
            });
            if (GlyphN(S(it, "text")) < 2 || OnPlate(sc, col)) return; // 单字 item、底板上的文字只吸附不画格
            var tint = J.Mix(ScK(sc, "bg"), lc, IsDark(ScK(sc, "bg")) ? 0.1 : 0.08);
            PreFx(env, it, () =>
            {
                var q = J.E.OutCubic(InP(env, it, 0, 0.45)) * (1 - J.E.InCubic(env.POut));
                if (q <= 0.01) return;
                var lay = JText.LayoutText(it);
                var s = D(it, "size");
                var sxg = NumNull(it, "sx", 1);
                var syg = NumNull(it, "sy", 1);
                var a = NumNull(it, "alpha", 1) * q;
                var ctx = env.Ctx;
                var main = env.Pass == "main";
                var cell = pitch * s;
                var lw = Math.Max(1, s * 0.013);
                var gut = cell * 0.24;
                var rows = new List<JText.GlyphCell>();
                foreach (var g in lay.Cells)
                    if (rows.All(r => r.Li != g.Li)) rows.Add(g);
                foreach (var g in rows)
                {
                    var n = g.N;
                    if (n == 0) continue;
                    var c0 = Tgt(0, n, s) - cell / 2;
                    var len = n * cell * q;
                    var cross = vert ? g.X : g.Y;
                    var v0 = cross - cell / 2;
                    var v1 = cross + cell / 2;
                    double[] R(double u0, double u1, double q0, double q1) => vert
                        ? new[] { q0 * sxg, u0 * syg, (q1 - q0) * sxg, (u1 - u0) * syg }
                        : new[] { u0 * sxg, q0 * syg, (u1 - u0) * sxg, (q1 - q0) * syg };
                    double[][] L(double u0, double u1, double v) => vert
                        ? new[] { new[] { v * sxg, u0 * syg }, new[] { v * sxg, u1 * syg } }
                        : new[] { new[] { u0 * sxg, v * syg }, new[] { u1 * sxg, v * syg } };
                    if (main)
                    {
                        var r = R(c0, c0 + len, v0, v1);
                        FillRectLocal(ctx, r[0], r[1], r[2], r[3], tint, a * 0.9, env);
                    }
                    env.Line(L(c0, c0 + len, v0), lc, lw, a * 0.85, false);
                    env.Line(L(c0, c0 + len, v1), lc, lw, a * 0.85, false);
                    env.Line(L(c0 - cell * 0.1, c0 + len + cell * 0.1, vert ? v1 + gut : v0 - gut), lc, lw, a * 0.55, false);
                    for (var k = 0; k <= n; k++)
                    {
                        var pq = c0 + k * cell;
                        if (pq - c0 > len + 0.5) break;
                        env.Line(vert
                            ? new[] { new[] { v0 * sxg, pq * syg }, new[] { v1 * sxg, pq * syg } }
                            : new[] { new[] { pq * sxg, v0 * syg }, new[] { pq * sxg, v1 * syg } }, lc, lw, a * 0.85, false);
                    }
                }
            });
        },
    });

    /// <summary>withFx 的 item 空间内 fillRect（ghostable 底色随 pass 走）。</summary>
    private static void FillRectLocal(SKCanvas ctx, double x, double y, double w, double h, string color, double a, Env env)
    {
        var col = env.ColOf(color, true);
        if (col == null || a <= 0.003) return;
        using var p = new SKPaint { Color = Env.SkColor(col, Math.Min(1, a)), BlendMode = env.Blend };
        ctx.DrawRect(new SKRect((float)x, (float)y, (float)(x + w), (float)(y + h)), p);
    }

    private static void RegisterOutlineOffset() => RegT("outlineOffset", new TreatDef
    {
        Name = "版ズレ袋文字",
        Tags = new[] { "pop", "graphic", "editorial" },
        W = 0.8,
        Plan = (rng, st) => new JObject
        {
            ["d"] = rng.Range(0.055, 0.08),
            ["dir"] = new JArray(rng.Pick(new[] { new[] { 1.0, 1.0 }, new[] { 1.0, 1.0 }, new[] { -1.0, 1.0 }, new[] { 1.0, 0.35 }, new[] { 0.4, 1.0 } })),
            ["k"] = rng.Range(0.022, 0.03),
        },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it) || InPieces(env, it)) return;
            var sc = env.Sc!;
            var col = ColOfIt(env, it);
            var fc = AccentFor(sc, col, 1.4);
            it["fill"] = false;
            it["strokeColor"] = col;
            it["stroke"] = Math.Max(1.4, D(it, "size") * D(p, "k"));
            // addPre（就地执行）
            if (env.Pass != "main" || Tracing(it)) return;
            var d = D(it, "size") * D(p, "d");
            var dir = ((JArray?)p["dir"])?.Select(D0).ToArray() ?? new[] { 1.0, 1.0 };
            PreFx(env, it, () =>
                DrawCopy(env, Bare(it, new JObject
                {
                    ["fill"] = true,
                    ["stroke"] = 0.0,
                    ["color"] = fc,
                    ["x"] = D(it, "x") + dir[0] * d,
                    ["y"] = D(it, "y") + dir[1] * d,
                }), null), false);
        },
    });

    private static void RegisterToneShadow() => RegT("toneShadow", new TreatDef
    {
        Name = "トーン影",
        Tags = new[] { "pop", "graphic", "editorial" },
        W = 0.7,
        Plan = (rng, st) => new JObject
        {
            ["v"] = rng.Pick(new[] { "dots", "dots", "hatch", "stripes" }),
            ["d"] = rng.Range(0.09, 0.12),
            ["dir"] = new JArray(rng.Pick(new[] { new[] { 1.0, 1.0 }, new[] { 1.0, 1.0 }, new[] { -1.0, 1.0 }, new[] { 1.0, 0.6 } })),
        },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it)) return;
            var sc = env.Sc!;
            var col = ColOfIt(env, it);
            var tc = FirstOK(new[] { sc.Str("accent"), sc.Str("accent2"), sc.Str("fg"), sc.Str("ink") },
                c => Ctr(c, ScK(sc, "bg")) >= 2.2 && Ctr(c, col) >= 1.3, MarkCol(sc, col))!;
            // addPre（就地执行）
            if (env.Pass != "main" || Tracing(it) || InPieces(env, it)) return;
            var d = D(it, "size") * D(p, "d");
            var dir = ((JArray?)p["dir"])?.Select(D0).ToArray() ?? new[] { 1.0, 1.0 };
            PreFx(env, it, () =>
                DrawCopy(env, Bare(it, new JObject
                {
                    ["x"] = D(it, "x") + dir[0] * d,
                    ["y"] = D(it, "y") + dir[1] * d,
                    ["color"] = tc,
                    ["stroke"] = 0.0,
                    ["pattern"] = S(p, "v"),
                    ["patternColor"] = tc,
                    ["patternBg"] = null,
                }), null), false);
        },
    });

    private static void RegisterFadeChars() => RegT("fadeChars", new TreatDef
    {
        Name = "余韻",
        Tags = new[] { "emotional", "calm" },
        W = 0.5,
        Safe = true,
        Plan = (rng, st) => new JObject
        {
            ["v"] = rng.Pick(new[] { "tail", "tail", "both", "head" }),
            ["lo"] = rng.Range(0.3, 0.4),
        },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it, 0.9)) return;
            var v = S(p, "v");
            var lo = D(p, "lo");
            env.CharFns.Add((gi, g, n) =>
            {
                if (g == null || (int)D(g, "n") < 3) return null;
                var cn = (int)D(g, "n");
                var ci = (int)D(g, "ci");
                var u = ci / (double)(cn - 1);
                var f = v == "both" ? Math.Pow(Math.Abs(u - 0.5) * 2, 1.4) : v == "head" ? J.E.InQuad(1 - u) : J.E.InQuad(u);
                return new CharXf { A = 1 - (1 - lo) * f };
            });
        },
    });

    private static void RegisterCutShift() => RegT("cutShift", new TreatDef
    {
        Name = "断ち切り",
        Tags = new[] { "graphic", "glitch", "pop" },
        W = 0.7,
        Plan = (rng, st) => new JObject
        {
            ["at"] = rng.Range(-0.08, 0.06),
            ["d"] = rng.Range(0.11, 0.16) * (rng.Chance(0.5) ? 1 : -1),
            ["line"] = rng.Chance(0.6),
        },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it) || InPieces(env, it)) return;
            var sc = env.Sc!;
            var vert = Truthy(it["vertical"]);
            var lc = AccentFor(sc, ColOfIt(env, it), 1.6);
            var at = D(p, "at");
            var topFn = (JAnim.CharFn)((gi, g, n) => vert
                ? new CharXf { ClipX = new[] { -0.75, at } }
                : new CharXf { ClipY = new[] { -0.75, at } });
            env.CharFns.Add(topFn);
            // addPre（就地执行）
            if (Tracing(it)) return;
            var q = J.E.OutBack(InP(env, it, env.Cut.InDur * 0.55, 0.3), 2.2) * (1 - J.E.InCubic(env.POut));
            var s = D(it, "size");
            var d = s * D(p, "d") * q;
            var botFn = (JAnim.CharFn)((gi, g, n) => vert
                ? new CharXf { ClipX = new[] { at, 0.75 }, Dy = d }
                : new CharXf { ClipY = new[] { at, 0.75 }, Dx = d });
            var rest = env.CharFns.Where(f => f != topFn).ToList();
            var combined = JAnim.CombineChar(rest.Concat(new[] { botFn }).ToList());
            var c = Bare(it);
            PreFx(env, it, () => DrawCopy(env, c, combined), false);
            if (Truthy(p["line"]) && env.Pass == "main" && q > 0.02)
            {
                PreFx(env, it, () =>
                {
                    var lw = Math.Max(1.2, s * 0.012);
                    var ex = s * 0.35 * q;
                    var sx = NumNull(it, "sx", 1);
                    var sy = NumNull(it, "sy", 1);
                    var alpha = NumNull(it, "alpha", 1) * Math.Min(1, q);
                    foreach (var l in LineSpans(env, it))
                    {
                        var cp = l.C + at * s * (vert ? sx : sy);
                        if (vert) env.Line(new[] { new[] { cp, l.A0 - ex }, new[] { cp, l.A1 + ex + d } }, lc, lw, alpha, false);
                        else env.Line(new[] { new[] { l.A0 - ex, cp }, new[] { l.A1 + ex + d, cp } }, lc, lw, alpha, false);
                    }
                });
            }
        },
    });

    private static void RegisterFocusPull() => RegT("focusPull", new TreatDef
    {
        Name = "ぼかし送り",
        Tags = new[] { "emotional", "calm", "editorial" },
        W = 0.6,
        Plan = (rng, st) => new JObject { ["b"] = rng.Range(0.035, 0.05), ["rev"] = rng.Chance(0.3) },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it, 0.9)) return;
            var cut = env.Cut;
            var lay = JText.LayoutText(it);
            var n = lay.N;
            var whole = GlyphN(cut.Text);
            var miTok = it["mi"];
            Func<int, double>? pos = null;
            if (n >= 3) pos = gi => gi / (double)(n - 1);
            else if (whole >= 3 && miTok != null && D0(miTok) == Math.Round(D0(miTok)) && (int)D0(miTok) < whole)
                pos = _ => (int)D0(miTok) / (double)(whole - 1);
            else return;
            var f0 = J.Clamp((env.Lt - cut.InDur * 0.4) / Math.Max(0.4, cut.Dur - cut.OutDur - cut.InDur * 0.4));
            var f = J.Lerp(-0.15, 1.15, Truthy(p["rev"]) ? 1 - f0 : f0);
            var b = D(p, "b");
            env.CharFns.Add((gi, g, n2) =>
            {
                var d = J.Clamp(Math.Abs(pos!(gi) - f) * 2.4 - 0.2);
                if (d <= 0.02) return null;
                return new CharXf
                {
                    Blur = Math.Min(D(g!, "w"), D(g, "h")) * b * d,
                    A = 1 - 0.35 * d,
                };
            });
        },
    });

    // 一字强调的整句唯一标记（JS SPOT WeakMap）
    private static readonly ConditionalWeakTable<Env, StrongBox<bool>> SpotMap = new();

    private static void RegisterSpotChar() => RegT("spotChar", new TreatDef
    {
        Name = "一字マーク",
        Tags = new[] { "pop", "graphic", "editorial", "emotional" },
        W = 0.7,
        Plan = (rng, st) => new JObject
        {
            ["v"] = rng.Pick(new[] { "disc", "disc", "square", "diamond" }),
            ["k"] = rng.Range(1.06, 1.14),
            ["r"] = rng.Range(0, 1),
        },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it, 0.9)) return;
            if (SpotMap.TryGetValue(env, out var seen) && seen.Value) return; // 整句只挑一处
            var lay = JText.LayoutText(it);
            var gs = lay.Cells.Where(g => !IsSp(g.Ch) && !J.IsPunct(g.Ch[0])).ToList();
            if (gs.Count == 0) return;
            JText.GlyphCell Choose(IEnumerable<JText.GlyphCell> list)
            {
                var l = list.ToList();
                var kk = l.Where(g => J.IsKanji(g.Ch[0])).ToList();
                var t = l.Where(g => J.IsKata(g.Ch[0])).ToList();
                var pool = kk.Count > 0 ? kk : t.Count > 0 ? t : l;
                return pool[(int)Math.Floor(D(p, "r") * pool.Count) % pool.Count];
            }
            int tIdx;
            if (gs.Count == 1)
            {
                var all = JLayouts.CodePoints(env.Cut!.Text)
                    .Select((ch, i) => (Ch: ch, I: i))
                    .Where(q => !IsSp(q.Ch) && !J.IsPunct(q.Ch[0]))
                    .ToList();
                if (all.Count == 0) return;
                var pick = Choose(all.Select(q => new JText.GlyphCell { Ch = q.Ch, I = q.I }).AsEnumerable()).I;
                var miV = it["mi"] != null ? D0(it["mi"]) : -1;
                if (it["mi"] == null || miV != Math.Round(miV) || (int)miV != pick || gs[0].Ch != all.First(q => q.I == pick).Ch) return;
                tIdx = gs[0].I;
            }
            else tIdx = Choose(gs).I;
            SpotMap.AddOrUpdate(env, new StrongBox<bool>(true));
            var sc = env.Sc!;
            var col = ColOfIt(env, it);
            if (OnPlate(sc, col)) return;
            var pc = FirstOK(new[] { sc.Str("accent"), sc.Str("accent2"), sc.Str("ink"), sc.Str("fg") },
                c => Ctr(c, ScK(sc, "bg")) >= 2 && Ctr(c, col) >= 1.4, AccentFor(sc, col, 1.6))!;
            var tc = TextOn(pc, col, sc);
            double QOf(Env e) =>
                J.E.OutBack(InP(e, it, e.Cut!.InDur * 0.5, 0.3), 1.8) * (1 - J.E.InCubic(J.Clamp(e.POut * 1.3)));
            var k = D(p, "k");
            var v = S(p, "v");
            env.CharFns.Add((gi, g, n) => gi != tIdx ? null
                : QOf(env) > 0.5 && tc != col ? new CharXf { S = k, Color = tc } : new CharXf { S = k });
            PreFx(env, it, () =>
            {
                var gl = GlyphList(env, it).FirstOrDefault(x => x.G.I == tIdx);
                if (gl == null) return;
                var q = QOf(env);
                if (q <= 0.01) return;
                var s = D(it, "size") * Math.Min(NumNull(it, "sx", 1), NumNull(it, "sy", 1)) * (gl.S / k) * k;
                var a = NumNull(it, "alpha", 1) * gl.A;
                var ctx = env.Ctx;
                if (v == "disc")
                {
                    env.Circle(gl.X, gl.Y, s * 0.64 * q, pc, null, 0, a, true);
                    return;
                }
                ctx.Save();
                ctx.Translate((float)gl.X, (float)gl.Y);
                ctx.RotateDegrees((float)((v == "diamond" ? 45 : -6) + gl.Rot + (1 - q) * 40));
                var h = s * (v == "diamond" ? 0.68 : 0.6) * q;
                FillRectLocal(ctx, -h, -h, h * 2, h * 2, pc, a, env);
                ctx.Restore();
            });
        },
    });

    private static void RegisterRansom() => RegT("ransom", new TreatDef
    {
        Name = "切り貼り文字",
        Tags = new[] { "pop", "glitch", "graphic" },
        W = 0.6,
        Plan = (rng, st) => new JObject { ["s"] = (double)rng.Int(1, 1000000) },
        Draw = env =>
        {
            var it = env.It;
            var p = env.Cut!.TreatP;
            if (it == null || !Alive(it, 0.9)) return;
            var sc = env.Sc!;
            var col = ColOfIt(env, it);
            var s0 = unchecked((int)J.H(unchecked((int)D(p, "s")), (int)D(it, "mi"), 53));
            if (OnPlate(sc, col)) return;
            var plates = new List<string>();
            foreach (var c in new[] { sc.Str("ink"), sc.Str("fg"), sc.Str("accent"), sc.Str("accent2"), sc.Str("sub"), sc.Str("ghostB") })
                if (c != null && Ctr(c, ScK(sc, "bg")) >= 1.5 && plates.All(q => Ctr(q, c) >= 1.2)) plates.Add(c);
            if (plates.Count == 0) plates.Add(J.FitContrast(ScK(sc, "accent"), ScK(sc, "bg"), 2));
            var lay = JText.LayoutText(it);
            var n = lay.N;
            var r = new List<(string Pc, string Tc, double Rot, double S, double Dy, double[] Pad, double[] Jj)>();
            for (var k = 0; k < n; k++)
            {
                var pc = plates[(int)(J.H(s0, k, 1) % (uint)plates.Count)];
                var alt = Best(new[] { sc.Str("bg"), col, sc.Str("fg"), sc.Str("ink"), "#111111", "#FFFFFF" }.Where(c => c != pc), pc);
                r.Add((pc,
                    Ctr(col, pc) >= 3 && J.R(s0, k, 2) < 0.5 ? col : alt,
                    J.Rs(s0, k, 3) * 8,
                    J.Rr(0.84, 1.02, s0, k, 4),
                    J.Rs(s0, k, 5) * 0.05,
                    new[] { J.Rr(0.06, 0.16, s0, k, 6), J.Rr(0.06, 0.16, s0, k, 7), J.Rr(0.06, 0.16, s0, k, 8), J.Rr(0.06, 0.16, s0, k, 9) },
                    new[] { 0, 1, 2, 3 }.Select(q => J.Rs(s0, k, 10 + q) * 0.06).ToArray()));
            }
            double QOf(Env e, int gi) =>
                J.E.OutBack(J.Clamp((e.Lt - NumNull(it, "delay", 0) - gi * 0.03) / 0.22), 1.5)
                * (1 - J.E.InCubic(J.Clamp(e.POut * 1.4 - gi * 0.03)));
            var syN = NumNull(it, "sy", 1);
            env.CharFns.Add((gi, g, n2) =>
            {
                if (gi >= r.Count || g == null || IsSp(S(g, "ch") ?? " ")) return null;
                var q = r[gi];
                return new CharXf { Rot = q.Rot, S = q.S, Dy = q.Dy * D(g, "h"), Color = QOf(env, gi) > 0.5 ? q.Tc : null };
            });
            PreFx(env, it, () =>
            {
                var ctx = env.Ctx;
                var a = NumNull(it, "alpha", 1);
                foreach (var gl in GlyphList(env, it))
                {
                    if (gl.G.I >= r.Count) continue;
                    var q = r[gl.G.I];
                    var qq = QOf(env, gl.G.I);
                    if (qq <= 0.01) continue;
                    var size = D(it, "size");
                    var w = Math.Max(gl.W, size * 0.55 * NumNull(it, "sx", 1)) / 2;
                    var h = size * syN / 2;
                    var pad = q.Pad;
                    var jj = q.Jj;
                    ctx.Save();
                    ctx.Translate((float)gl.X, (float)gl.Y);
                    ctx.RotateDegrees((float)gl.Rot);
                    ctx.Scale((float)(gl.S * qq), (float)(gl.S * qq));
                    env.Poly(new[]
                    {
                        new[] { -w - pad[0] * size, -h - pad[1] * size + jj[0] * size },
                        new[] { w + pad[2] * size, -h - pad[1] * size + jj[1] * size },
                        new[] { w + pad[2] * size + jj[2] * size, h + pad[3] * size },
                        new[] { -w - pad[0] * size + jj[3] * size, h + pad[3] * size },
                    }, q.Pc, a * gl.A, true);
                    ctx.Restore();
                }
            });
        },
    });

    // ================= trans（JS trReg('trans', …) 出现序） =================

    private static void RegisterWipe() => RegX("wipe", new TransDef
    {
        Name = "エッジワイプ",
        Tags = new[] { "graphic", "editorial", "pop" },
        W = 1.2,
        Dur = 0.35,
        Plan = (rng, st) => new JObject { ["dir"] = DirPick(rng, new[] { "L", "R", "U", "D" }, new[] { 3.0, 2.0, 1.4, 0.8 }) },
        Draw = Wrap((ctx, a, b, p, i, pp) =>
        {
            var cw = (double)i.Cw;
            var ch = (double)i.Ch;
            var e = J.E.InOutCubic(p);
            var lw = LwOf(i, 0.007);
            var ac = TAcc(i);
            DrawImg(ctx, a, 0, 0);
            double bx, by, bw2, bh2, off;
            if (S(pp, "dir") == "R")
            {
                var x = cw * e;
                PartImg(ctx, b, 0, 0, x, ch);
                (bx, by, bw2, bh2, off) = (x - lw / 2, 0, lw, ch, -1);
            }
            else if (S(pp, "dir") == "U")
            {
                var y = ch * (1 - e);
                PartImg(ctx, b, 0, y, cw, ch - y);
                (bx, by, bw2, bh2, off) = (0, y - lw / 2, cw, lw, 1);
            }
            else if (S(pp, "dir") == "D")
            {
                var y = ch * e;
                PartImg(ctx, b, 0, 0, cw, y);
                (bx, by, bw2, bh2, off) = (0, y - lw / 2, cw, lw, -1);
            }
            else
            {
                var x = cw * (1 - e);
                PartImg(ctx, b, x, 0, cw - x, ch);
                (bx, by, bw2, bh2, off) = (x - lw / 2, 0, lw, ch, 1);
            }
            var alpha = Math.Pow(Bell(p), 0.6);
            FillRect(ctx, bx, by, bw2, bh2, ac, alpha);
            var dx = lw * 3.2 * off;
            FillRect(ctx, bw2 == lw ? bx + dx : 0, bw2 == lw ? 0 : by + dx,
                bw2 == lw ? Math.Max(1, lw * 0.35) : cw, bw2 == lw ? ch : Math.Max(1, lw * 0.35), ac, alpha * 0.5);
        }),
    });

    private static void RegisterDiagonalWipe() => RegX("diagonalWipe", new TransDef
    {
        Name = "斜め帯ワイプ",
        Tags = new[] { "pop", "graphic" },
        W = 1,
        Dur = 0.35,
        Plan = (rng, st) => new JObject
        {
            ["k"] = rng.Range(0.3, 0.55) * (rng.Chance(0.5) ? 1 : -1),
            ["rev"] = rng.Chance(0.4),
        },
        Draw = Wrap((ctx, a, b, p, i, pp) =>
        {
            var cw = (double)i.Cw;
            var ch = (double)i.Ch;
            var e = J.E.InOutCubic(p);
            var sl = D(pp, "k") * ch;
            var band = MinD(i) * 0.07 * Bell(p);
            var span = cw + Math.Abs(sl) + band * 2 + 4;
            var x = -Math.Abs(sl) / 2 - band - 2 + span * e;
            SKPath Poly(double x0, double x1)
            {
                var path = new SKPath();
                path.MoveTo((float)(x0 - sl / 2), 0);
                path.LineTo((float)(x1 - sl / 2), 0);
                path.LineTo((float)(x1 + sl / 2), (float)ch);
                path.LineTo((float)(x0 + sl / 2), (float)ch);
                path.Close();
                return path;
            }
            DrawImg(ctx, a, 0, 0);
            var rev = Truthy(pp["rev"]);
            if (rev)
            {
                ctx.Translate((float)cw, 0);
                ctx.Scale(-1, 1);
            }
            using (var clip = Poly(-cw * 2, x))
            {
                ctx.Save();
                ctx.ClipPath(clip, antialias: false);
                if (rev)
                {
                    ctx.Translate((float)cw, 0);
                    ctx.Scale(-1, 1);
                }
                DrawImg(ctx, b, 0, 0);
                ctx.Restore();
            }
            if (band > 0.5)
            {
                using (var band1 = Poly(x, x + band))
                    ctx.DrawPath(band1, new SKPaint { Color = Env.SkColor(TAcc(i), 1) });
                using var band2 = Poly(x + band * 1.35, x + band * 1.6);
                ctx.DrawPath(band2, new SKPaint { Color = Env.SkColor(TAcc(i, true), 0.85f) });
            }
        }),
    });

    private static void RegisterClockWipe() => RegX("clockWipe", new TransDef
    {
        Name = "クロックワイプ",
        Tags = new[] { "graphic", "pop", "editorial" },
        W = 0.7,
        Dur = 0.45,
        Plan = (rng, st) => new JObject
        {
            ["dir"] = (double)(rng.Chance(0.7) ? 1 : -1),
            ["a0"] = rng.Pick(new[] { -90.0, -90.0, 0.0, 180.0 }),
        },
        Draw = Wrap((ctx, a, b, p, i, pp) =>
        {
            var cw = (double)i.Cw;
            var ch = (double)i.Ch;
            var e = J.E.InOutCubic(p);
            var cx = cw / 2;
            var cy = ch / 2;
            var r = Math.Sqrt(cw * cw + ch * ch) / 2 + 4;
            var a0 = D(pp, "a0") * J.DEG;
            var dir = D(pp, "dir");
            var a1 = a0 + e * J.TAU * dir;
            DrawImg(ctx, a, 0, 0);
            ctx.Save();
            using (var clip = new SKPath())
            {
                clip.MoveTo((float)cx, (float)cy);
                clip.ArcTo(new SKRect((float)(cx - r), (float)(cy - r), (float)(cx + r), (float)(cy + r)),
                    (float)(a0 / J.DEG), (float)((a1 - a0) / J.DEG), false);
                clip.Close();
                ctx.ClipPath(clip, antialias: false);
            }
            DrawImg(ctx, b, 0, 0);
            ctx.Restore();
            var alpha = Math.Pow(Bell(p), 0.5);
            var lw = LwOf(i, 0.006);
            using (var pnt = new SKPaint
            {
                IsAntialias = true,
                IsStroke = true,
                StrokeWidth = (float)lw,
                StrokeCap = SKStrokeCap.Round,
                Color = Env.SkColor(TAcc(i), Math.Min(1, alpha)),
            })
            {
                using var path = new SKPath();
                path.MoveTo((float)cx, (float)cy);
                path.LineTo((float)(cx + Math.Cos(a1) * r), (float)(cy + Math.Sin(a1) * r));
                ctx.DrawPath(path, pnt);
            }
            FillRect(ctx, cx - lw * 2.2, cy - lw * 2.2, lw * 4.4, lw * 4.4, TAcc(i), alpha);
        }),
    });

    private static void RegisterIrisOpen() => RegX("irisOpen", new TransDef
    {
        Name = "アイリスイン",
        Tags = new[] { "emotional", "pop", "editorial" },
        W = 0.8,
        Dur = 0.4,
        Plan = (rng, st) => new JObject { ["x"] = 0.5 + rng.Range(-0.12, 0.12), ["y"] = 0.5 + rng.Range(-0.1, 0.1) },
        Draw = Wrap((ctx, a, b, p, i, pp) =>
        {
            var cw = (double)i.Cw;
            var ch = (double)i.Ch;
            var cx = cw * D(pp, "x");
            var cy = ch * D(pp, "y");
            var e = J.E.InOutCubic(p);
            var r = Math.Sqrt(Math.Pow(Math.Max(cx, cw - cx), 2) + Math.Pow(Math.Max(cy, ch - cy), 2)) + 4;
            var rr = r * e;
            DrawImg(ctx, a, 0, 0);
            if (rr > 0.5)
            {
                ctx.Save();
                using (var clip = new SKPath())
                {
                    clip.AddCircle((float)cx, (float)cy, (float)rr);
                    ctx.ClipPath(clip, antialias: true);
                }
                DrawImg(ctx, b, 0, 0);
                ctx.Restore();
            }
            var alpha = Math.Pow(Bell(p), 0.6);
            var lw = LwOf(i, 0.009);
            using (var pnt = new SKPaint
            {
                IsAntialias = true,
                IsStroke = true,
                StrokeWidth = (float)lw,
                Color = Env.SkColor(TAcc(i), Math.Min(1, alpha)),
            })
            using (var path = new SKPath())
            {
                path.AddCircle((float)cx, (float)cy, (float)(rr + lw / 2));
                ctx.DrawPath(path, pnt);
            }
            using (var pnt2 = new SKPaint
            {
                IsAntialias = true,
                IsStroke = true,
                StrokeWidth = (float)(lw * 0.4),
                Color = Env.SkColor(TAcc(i, true), Math.Min(1, alpha * 0.6)),
            })
            using (var path2 = new SKPath())
            {
                path2.AddCircle((float)cx, (float)cy, (float)(rr * 1.06 + lw * 2.5));
                ctx.DrawPath(path2, pnt2);
            }
        }),
    });

    private static void RegisterPushSlide() => RegX("pushSlide", new TransDef
    {
        Name = "プッシュ",
        Tags = new[] { "graphic", "pop", "editorial" },
        W = 1,
        Dur = 0.35,
        Plan = (rng, st) => new JObject { ["dir"] = DirPick(rng, new[] { "L", "R", "U", "D" }, new[] { 3.0, 1.6, 1.4, 0.6 }) },
        Draw = Wrap((ctx, a, b, p, i, pp) =>
        {
            var cw = (double)i.Cw;
            var ch = (double)i.Ch;
            var e = J.E.InOutCubic(p);
            var dir = S(pp, "dir");
            var h = dir is "L" or "R";
            var l = h ? cw : ch;
            var sg = dir is "L" or "U" ? -1 : 1;
            var off = Math.Round(e * l, MidpointRounding.AwayFromZero) * sg;
            if (h)
            {
                DrawImg(ctx, a, off, 0);
                DrawImg(ctx, b, off - sg * cw, 0);
            }
            else
            {
                DrawImg(ctx, a, 0, off);
                DrawImg(ctx, b, 0, off - sg * ch);
            }
            var lw = LwOf(i, 0.005);
            var q = h ? (sg < 0 ? cw + off : off) : (sg < 0 ? ch + off : off);
            var alpha = Math.Pow(Bell(p), 0.6);
            if (h) FillRect(ctx, q - lw / 2, 0, lw, ch, TAcc(i), alpha);
            else FillRect(ctx, 0, q - lw / 2, cw, lw, TAcc(i), alpha);
        }),
    });

    private static void RegisterCover() => RegX("cover", new TransDef
    {
        Name = "カバー",
        Tags = new[] { "editorial", "graphic", "calm" },
        W = 0.9,
        Dur = 0.35,
        Plan = (rng, st) => new JObject { ["dir"] = DirPick(rng, new[] { "L", "R", "U", "D" }, new[] { 2.0, 2.0, 1.5, 1.0 }) },
        Draw = Wrap((ctx, a, b, p, i, pp) =>
        {
            var cw = (double)i.Cw;
            var ch = (double)i.Ch;
            var e = J.E.InOutCubic(p);
            var dir = S(pp, "dir");
            var h = dir is "L" or "R";
            var sg = dir is "L" or "U" ? -1 : 1;
            var l = h ? cw : ch;
            var bo = Math.Round((1 - e) * l, MidpointRounding.AwayFromZero) * -sg;
            var ao = Math.Round(e * l * 0.18, MidpointRounding.AwayFromZero) * sg;
            if (h) DrawImg(ctx, a, ao, 0);
            else DrawImg(ctx, a, 0, ao);
            FillRect(ctx, 0, 0, cw, ch, "#000000", 0.45 * e);
            // 入场边前方一道软阴影
            var sw = MinD(i) * 0.06;
            var edge = h ? (sg > 0 ? cw + bo : bo) : (sg > 0 ? ch + bo : bo);
            ShadowBand(ctx, h, cw, ch, edge, sg * sw, 0.45 * Bell(p));
            if (h) DrawImg(ctx, b, bo, 0);
            else DrawImg(ctx, b, 0, bo);
        }),
    });

    /// <summary>入场边前方一道软阴影：沿边线的线性渐变矩形（cover/uncover 共用）。</summary>
    private static void ShadowBand(SKCanvas ctx, bool h, double cw, double ch, double edge, double dSw, double alpha)
    {
        SKPoint g0, g1;
        SKRect rect;
        if (h)
        {
            g0 = new SKPoint((float)edge, 0);
            g1 = new SKPoint((float)(edge + dSw), 0);
            rect = SKRect.Create((float)(dSw > 0 ? edge : edge + dSw), 0, (float)Math.Abs(dSw), (float)ch);
        }
        else
        {
            g0 = new SKPoint(0, (float)edge);
            g1 = new SKPoint(0, (float)(edge + dSw));
            rect = SKRect.Create(0, (float)(dSw > 0 ? edge : edge + dSw), (float)cw, (float)Math.Abs(dSw));
        }
        using var p = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(g0, g1,
                new[] { Env.SkColor("#000000", alpha), Env.SkColor("#000000", 0) },
                new float[] { 0f, 1f }, SKShaderTileMode.Clamp),
        };
        ctx.DrawRect(rect, p);
    }

    private static void RegisterUncover() => RegX("uncover", new TransDef
    {
        Name = "アンカバー",
        Tags = new[] { "editorial", "calm", "emotional" },
        W = 0.8,
        Dur = 0.35,
        Plan = (rng, st) => new JObject { ["dir"] = DirPick(rng, new[] { "L", "R", "U", "D" }, new[] { 2.0, 2.0, 1.6, 1.0 }) },
        Draw = Wrap((ctx, a, b, p, i, pp) =>
        {
            var cw = (double)i.Cw;
            var ch = (double)i.Ch;
            var e = J.E.InOutCubic(p);
            var dir = S(pp, "dir");
            var h = dir is "L" or "R";
            var sg = dir is "L" or "U" ? -1 : 1;
            var s = 1.05 - 0.05 * J.E.OutCubic(p);
            FillRect(ctx, 0, 0, cw, ch, ScK(i.Sc, "bg"), 1);
            Scaled(ctx, b, cw, ch, s);
            FillRect(ctx, 0, 0, cw, ch, "#000000", 0.4 * (1 - e));
            var l = h ? cw : ch;
            var ao = Math.Round(e * l, MidpointRounding.AwayFromZero) * sg;
            var edge = h ? (sg > 0 ? ao : cw + ao) : (sg > 0 ? ao : ch + ao);
            var sw = MinD(i) * 0.07;
            ShadowBand(ctx, h, cw, ch, edge, -sg * sw, 0.5 * Bell(p));
            if (h) DrawImg(ctx, a, ao, 0);
            else DrawImg(ctx, a, 0, ao);
        }),
    });

    private static void RegisterZoomThrough() => RegX("zoomThrough", new TransDef
    {
        Name = "ズームスルー",
        Tags = new[] { "pop", "emotional", "glitch" },
        W = 1,
        Dur = 0.35,
        Plan = (rng, st) => new JObject { ["z"] = rng.Range(1.5, 2.2) },
        Draw = Wrap((ctx, a, b, p, i, pp) =>
        {
            var cw = (double)i.Cw;
            var ch = (double)i.Ch;
            var z = D(pp, "z");
            var sa = 1 + (z - 1) * Math.Pow(p, 1.4);
            var aa = 1 - J.E.InCubic(J.Clamp(p * 1.12));
            var sb = 0.86 + 0.14 * J.E.OutCubic(p);
            FillRect(ctx, 0, 0, cw, ch, ScK(i.Sc, "bg"), 1);
            Scaled(ctx, b, cw, ch, sb);
            if (aa > 0.003)
            {
                Scaled(ctx, a, cw, ch, sa, smooth: false, alpha: aa);
                if (i.AllowFilter && p > 0.08 && aa > 0.15)
                    Scaled(ctx, a, cw, ch, sa * (1 + 0.08 * p), smooth: false, alpha: aa * 0.35);
            }
        }),
    });

    private static void RegisterDoorsOpen() => RegX("doorsOpen", new TransDef
    {
        Name = "観音開き",
        Tags = new[] { "graphic", "pop", "emotional" },
        W = 0.7,
        Dur = 0.4,
        Plan = (rng, st) => new JObject { ["vert"] = rng.Chance(0.3) },
        Draw = Wrap((ctx, a, b, p, i, pp) =>
        {
            var cw = (double)i.Cw;
            var ch = (double)i.Ch;
            var e = J.E.InOutCubic(p);
            var lw = LwOf(i, 0.005);
            var ac = TAcc(i);
            var sb = 0.93 + 0.07 * J.E.OutCubic(p);
            FillRect(ctx, 0, 0, cw, ch, ScK(i.Sc, "bg"), 1);
            Scaled(ctx, b, cw, ch, sb);
            FillRect(ctx, 0, 0, cw, ch, "#000000", 0.35 * (1 - e));
            var alpha = Math.Pow(Bell(p), 0.5);
            var vert = Truthy(pp["vert"]);
            if (!vert)
            {
                var hw = (int)Math.Floor(cw / 2);
                var off = Math.Round(e * (cw - hw + lw * 2), MidpointRounding.AwayFromZero);
                PartImg(ctx, a, 0, 0, hw, ch, -off, 0);
                PartImg(ctx, a, hw, 0, cw - hw, ch, off, 0);
                FillRect(ctx, hw - off - lw, 0, lw, ch, ac, alpha);
                FillRect(ctx, hw + off, 0, lw, ch, ac, alpha);
            }
            else
            {
                var hh = (int)Math.Floor(ch / 2);
                var off = Math.Round(e * (ch - hh + lw * 2), MidpointRounding.AwayFromZero);
                PartImg(ctx, a, 0, 0, cw, hh, 0, -off);
                PartImg(ctx, a, 0, hh, cw, ch - hh, 0, off);
                FillRect(ctx, 0, hh - off - lw, cw, lw, ac, alpha);
                FillRect(ctx, 0, hh + off, cw, lw, ac, alpha);
            }
        }),
    });

    private static void RegisterBlinds() => RegX("blinds", new TransDef
    {
        Name = "ブラインド転換",
        Tags = new[] { "graphic", "editorial", "calm" },
        W = 0.7,
        Dur = 0.4,
        Plan = (rng, st) => new JObject
        {
            ["n"] = (double)rng.Int(7, 12),
            ["vert"] = rng.Chance(0.35),
            ["rev"] = rng.Chance(0.4),
        },
        Draw = Wrap((ctx, a, b, p, i, pp) =>
        {
            var cw = (double)i.Cw;
            var ch = (double)i.Ch;
            var n = (int)D(pp, "n");
            var l = Truthy(pp["vert"]) ? cw : ch;
            var lw = Math.Max(1, LwOf(i, 0.003));
            var ac = TAcc(i);
            var vert = Truthy(pp["vert"]);
            var rev = Truthy(pp["rev"]);
            DrawImg(ctx, a, 0, 0);
            for (var ii = 0; ii < n; ii++)
            {
                var k = rev ? n - 1 - ii : ii;
                var q = J.E.InOutCubic(J.Clamp(p * 1.55 - k / (double)n * 0.55));
                var a0 = Math.Round(ii * l / n, MidpointRounding.AwayFromZero);
                var a1 = Math.Round((ii + 1) * l / n, MidpointRounding.AwayFromZero);
                if (q <= 0) continue;
                var len = q >= 1 ? a1 - a0 : (a1 - a0) * q;
                if (vert) PartImg(ctx, b, a0, 0, len, ch);
                else PartImg(ctx, b, 0, a0, cw, len);
                if (q < 1)
                {
                    if (vert) FillRect(ctx, a0 + len, 0, lw, ch, ac, 1 - q);
                    else FillRect(ctx, 0, a0 + len, cw, lw, ac, 1 - q);
                }
            }
        }),
    });

    private static void RegisterChecker() => RegX("checker", new TransDef
    {
        Name = "市松転換",
        Tags = new[] { "pop", "graphic" },
        W = 0.6,
        Dur = 0.45,
        Plan = (rng, st) => new JObject { ["n"] = (double)rng.Int(4, 6), ["rev"] = rng.Chance(0.5) },
        Draw = Wrap((ctx, a, b, p, i, pp) =>
        {
            var cw = (double)i.Cw;
            var ch = (double)i.Ch;
            var n = (int)D(pp, "n");
            var cell = MinD(i) / n;
            var cols = (int)Math.Ceiling(cw / cell);
            var rows = (int)Math.Ceiling(ch / cell);
            var rev = Truthy(pp["rev"]);
            DrawImg(ctx, a, 0, 0);
            for (var j = 0; j < rows; j++)
                for (var ii = 0; ii < cols; ii++)
                {
                    var d = ((ii + j) % 2) * 0.32 + 0.18 * ((rev ? cols - 1 - ii : ii) + j) / Math.Max(1, cols + rows - 2);
                    var t = J.Clamp((p - d) / 0.5);
                    if (t <= 0) continue;
                    var x0 = Math.Round(ii * cw / cols, MidpointRounding.AwayFromZero);
                    var x1 = Math.Round((ii + 1) * cw / cols, MidpointRounding.AwayFromZero);
                    var y0 = Math.Round(j * ch / rows, MidpointRounding.AwayFromZero);
                    var y1 = Math.Round((j + 1) * ch / rows, MidpointRounding.AwayFromZero);
                    if (t >= 1)
                    {
                        PartImg(ctx, b, x0, y0, x1 - x0, y1 - y0);
                        continue;
                    }
                    var q = J.E.OutCubic(t);
                    if (q > 0.97)
                    {
                        PartImg(ctx, b, x0, y0, x1 - x0, y1 - y0);
                        continue;
                    }
                    var w = (x1 - x0) * q;
                    var h = (y1 - y0) * q;
                    PartImg(ctx, b, (x0 + x1) / 2 - w / 2, (y0 + y1) / 2 - h / 2, w, h);
                }
        }),
    });

    private static void RegisterBlockDissolve() => RegX("blockDissolve", new TransDef
    {
        Name = "ブロック崩し",
        Tags = new[] { "glitch", "graphic" },
        W = 0.8,
        Dur = 0.4,
        Plan = (rng, st) => new JObject { ["n"] = (double)rng.Int(7, 11), ["side"] = (double)rng.Pick(new[] { 0.0, 0.0, 1.0, 2.0 }) },
        Draw = Wrap((ctx, a, b, p, i, pp) =>
        {
            var cw = (double)i.Cw;
            var ch = (double)i.Ch;
            var n = (int)D(pp, "n");
            var side = (int)D(pp, "side");
            var cell = MinD(i) / n;
            var cols = (int)Math.Ceiling(cw / cell);
            var rows = (int)Math.Ceiling(ch / cell);
            var s0 = i.Seed;
            var ac = TAcc(i);
            DrawImg(ctx, a, 0, 0);
            for (var j = 0; j < rows; j++)
                for (var ii = 0; ii < cols; ii++)
                {
                    var bias = side == 1 ? ii / (double)Math.Max(1, cols - 1) : side == 2 ? j / (double)Math.Max(1, rows - 1) : 0.5;
                    var r = 0.02 + 0.84 * (side != 0 ? 0.55 * J.R(s0, ii, j, 5) + 0.45 * bias : J.R(s0, ii, j, 5));
                    if (p < r) continue;
                    var x0 = Math.Round(ii * cw / cols, MidpointRounding.AwayFromZero);
                    var x1 = Math.Round((ii + 1) * cw / cols, MidpointRounding.AwayFromZero);
                    var y0 = Math.Round(j * ch / rows, MidpointRounding.AwayFromZero);
                    var y1 = Math.Round((j + 1) * ch / rows, MidpointRounding.AwayFromZero);
                    PartImg(ctx, b, x0, y0, x1 - x0, y1 - y0);
                    var f = 1 - (p - r) / 0.1;
                    if (f > 0) FillRect(ctx, x0, y0, x1 - x0, y1 - y0, ac, f * 0.75);
                }
        }),
    });

    private static void RegisterWhipPan() => RegX("whipPan", new TransDef
    {
        Name = "ホイップパン",
        Tags = new[] { "pop", "emotional", "glitch" },
        W = 1,
        Dur = 0.3,
        Plan = (rng, st) => new JObject { ["dir"] = (double)(rng.Chance(0.65) ? -1 : 1), ["vert"] = rng.Chance(0.2) },
        Draw = Wrap((ctx, a, b, p, i, pp) =>
        {
            var cw = (double)i.Cw;
            var ch = (double)i.Ch;
            var e = IoQuart(p);
            var vert = Truthy(pp["vert"]);
            var dir = D(pp, "dir");
            var l = vert ? ch : cw;
            var off = e * l * dir;
            var blur = l * 0.13 * Math.Pow(Bell(p), 2);
            void Put(SKCanvas c2, SKImage c, double o, double k)
            {
                using var pp = AlphaPaint(1);
                if (vert) c2.DrawImage(c, new SKRect(0, 0, c.Width, c.Height), new SKRect(0, (float)(o * k), (float)(cw * k), (float)(o * k + ch * k)), SampLinear, pp);
                else c2.DrawImage(c, new SKRect(0, 0, c.Width, c.Height), new SKRect((float)(o * k), 0, (float)(o * k + cw * k), (float)(ch * k)), SampLinear, pp);
            }
            if (blur < 3)
            {
                FillRect(ctx, 0, 0, cw, ch, ScK(i.Sc, "bg"), 1);
                Put(ctx, a, off, 1);
                Put(ctx, b, off - l * dir, 1);
                return;
            }
            const double k = 1 / 3.0;
            var w = Math.Max(2, (int)Math.Round(cw * k));
            var h = Math.Max(2, (int)Math.Round(ch * k));
            var t = i.Tmp(w, h);
            var x = t.Canvas;
            x.Save();
            x.SetMatrix(SKMatrix.Identity);
            x.Clear(Env.SkColor(ScK(i.Sc, "bg"), 1));
            var n = i.AllowFilter ? 12 : 6;
            for (var q = 0; q < n; q++)
            {
                var o = (q / (double)(n - 1) - 0.5) * blur;
                PutAlpha(x, a, off + o, k, vert, cw, ch, 1 / (double)(q + 1));
                PutAlpha(x, b, off - l * dir + o, k, vert, cw, ch, 1 / (double)(q + 1));
            }
            x.Restore();
            using var img = t.Snapshot();
            using var pnt = AlphaPaint(1);
            ctx.DrawImage(img, new SKRect(0, 0, w, h), new SKRect(0, 0, (float)cw, (float)ch), SampLinear, pnt);
        }),
    });

    private static void PutAlpha(SKCanvas c2, SKImage c, double o, double k, bool vert, double cw, double ch, double alpha)
    {
        using var p = new SKPaint { Color = new SKColor(255, 255, 255, (byte)Math.Round(J.Clamp(alpha) * 255)) };
        if (vert) c2.DrawImage(c, new SKRect(0, 0, c.Width, c.Height), new SKRect(0, (float)(o * k), (float)(cw * k), (float)(o * k + ch * k)), p);
        else c2.DrawImage(c, new SKRect(0, 0, c.Width, c.Height), new SKRect((float)(o * k), 0, (float)(o * k + cw * k), (float)(ch * k)), p);
    }

    private static void RegisterSpinOut() => RegX("spinOut", new TransDef
    {
        Name = "回転アウト",
        Tags = new[] { "pop", "glitch" },
        W = 0.6,
        Dur = 0.45,
        Plan = (rng, st) => new JObject { ["rot"] = rng.Range(100, 200) * (rng.Chance(0.5) ? 1 : -1) },
        Draw = Wrap((ctx, a, b, p, i, pp) =>
        {
            var cw = (double)i.Cw;
            var ch = (double)i.Ch;
            var e = J.E.InCubic(p);
            var s = 1 - e;
            var sb = 1.08 - 0.08 * J.E.OutCubic(p);
            FillRect(ctx, 0, 0, cw, ch, ScK(i.Sc, "bg"), 1);
            Scaled(ctx, b, cw, ch, sb);
            FillRect(ctx, 0, 0, cw, ch, "#000000", 0.4 * (1 - J.E.OutCubic(p)));
            if (s < 0.004) return;
            ctx.Save();
            ctx.Translate((float)(cw / 2), (float)(ch / 2));
            ctx.RotateDegrees((float)(D(pp, "rot") * e));
            ctx.Scale((float)s, (float)s);
            FillRect(ctx, -cw / 2 + MinD(i) * 0.02, -ch / 2 + MinD(i) * 0.03, cw, ch, "#000000", 0.35);
            DrawImg(ctx, a, -cw / 2, -ch / 2);
            var lw = LwOf(i, 0.008) / s;
            StrokeRectImg(ctx, -cw / 2 + lw / 2, -ch / 2 + lw / 2, cw - lw, ch - lw, TAcc(i), lw, Math.Min(1, p * 6));
            ctx.Restore();
        }),
    });

    /// <summary>墨滴路径（JS blobPath：过各边中点的二次贝塞尔闭合曲线）。</summary>
    private static SKPath BlobPath(double cx, double cy, double r, int s0, double ph, int n = 56)
    {
        var pts = new List<double[]>();
        for (var i = 0; i < n; i++)
        {
            var ang = i / (double)n * J.TAU;
            var w = 1 + 0.13 * Math.Sin(ang * 3 + J.R(s0, 1) * 6 + ph)
                      + 0.08 * Math.Sin(ang * 5 + J.R(s0, 2) * 6 - ph * 1.3)
                      + 0.05 * Math.Sin(ang * 9 + J.R(s0, 3) * 6 + ph * 0.7);
            pts.Add(new[] { cx + Math.Cos(ang) * r * w, cy + Math.Sin(ang) * r * w });
        }
        double[] Mid(int idx) => new[] { (pts[idx % n][0] + pts[(idx + 1) % n][0]) / 2, (pts[idx % n][1] + pts[(idx + 1) % n][1]) / 2 };
        var path = new SKPath();
        var m0 = Mid(0);
        path.MoveTo((float)m0[0], (float)m0[1]);
        for (var i = 1; i <= n; i++)
        {
            var q = pts[i % n];
            var m = Mid(i);
            path.QuadTo((float)q[0], (float)q[1], (float)m[0], (float)m[1]);
        }
        path.Close();
        return path;
    }

    private static void RegisterInkBlob() => RegX("inkBlob", new TransDef
    {
        Name = "インク",
        Tags = new[] { "emotional", "calm", "pop" },
        W = 0.7,
        Dur = 0.5,
        Plan = (rng, st) => new JObject
        {
            ["x"] = rng.Pick(new[] { 0.5, 0.5, 0.15, 0.85 }) + rng.Range(-0.08, 0.08),
            ["y"] = rng.Pick(new[] { 0.5, 0.25, 0.8 }) + rng.Range(-0.06, 0.06),
        },
        Draw = Wrap((ctx, a, b, p, i, pp) =>
        {
            var cw = (double)i.Cw;
            var ch = (double)i.Ch;
            var cx = cw * D(pp, "x");
            var cy = ch * D(pp, "y");
            var s0 = i.Seed;
            var r = Math.Sqrt(Math.Pow(Math.Max(cx, cw - cx), 2) + Math.Pow(Math.Max(cy, ch - cy), 2)) * 1.36;
            var e = J.E.InOutSine(p);
            var rr = r * e;
            var ph = p * 2.4;
            DrawImg(ctx, a, 0, 0);
            var rimA = 1 - J.Smooth(0.75, 0.98, p);
            var rim = MinD(i) * 0.035 * (0.4 + e);
            if (rimA > 0.01)
            {
                using (var path = BlobPath(cx, cy, rr + rim, s0, ph))
                using (var pnt = new SKPaint { IsAntialias = true, Color = Env.SkColor(TAcc(i), Math.Min(1, rimA)) })
                    ctx.DrawPath(path, pnt);
                for (var k = 0; k < 6; k++)
                {
                    var ang = J.R(s0, k, 7) * J.TAU;
                    var d = rr * (1.12 + 0.3 * J.R(s0, k, 8)) + rim;
                    var r2 = MinD(i) * (0.008 + 0.02 * J.R(s0, k, 9)) * J.Clamp(p * 4);
                    using var pnt2 = new SKPaint { IsAntialias = true, Color = Env.SkColor(TAcc(i), Math.Min(1, rimA)) };
                    ctx.DrawCircle((float)(cx + Math.Cos(ang) * d), (float)(cy + Math.Sin(ang) * d), (float)r2, pnt2);
                }
            }
            if (rr > 0.5)
            {
                ctx.Save();
                using (var path = BlobPath(cx, cy, rr, s0, ph))
                    ctx.ClipPath(path, antialias: true);
                DrawImg(ctx, b, 0, 0);
                ctx.Restore();
            }
        }),
    });

    private static void RegisterShatterTiles() => RegX("shatterTiles", new TransDef
    {
        Name = "タイル崩落",
        Tags = new[] { "glitch", "pop", "emotional" },
        W = 0.6,
        Dur = 0.5,
        Plan = (rng, st) => new JObject
        {
            ["n"] = (double)rng.Int(6, 9),
            ["x"] = rng.Range(0.3, 0.7),
            ["y"] = rng.Range(0.3, 0.6),
        },
        Draw = Wrap((ctx, a, b, p, i, pp) =>
        {
            var cw = (double)i.Cw;
            var ch = (double)i.Ch;
            var n = (int)D(pp, "n");
            var cell = Math.Max(cw, ch) / n;
            var cols = (int)Math.Ceiling(cw / cell);
            var rows = (int)Math.Ceiling(ch / cell);
            var s0 = i.Seed;
            DrawImg(ctx, b, 0, 0);
            FillRect(ctx, 0, 0, cw, ch, "#000000", 0.3 * (1 - J.E.OutCubic(p)));
            var cx = cw * D(pp, "x");
            var cy = ch * D(pp, "y");
            var dAll = Math.Sqrt(cw * cw + ch * ch);
            var moving = new List<(double X0, double Y0, double X1, double Y1, double T, int I, int J)>();
            ctx.Save();
            using (var stationary = new SKPath())
            {
                var any = false;
                for (var j = 0; j < rows; j++)
                    for (var ii = 0; ii < cols; ii++)
                    {
                        var x0 = Math.Round(ii * cw / cols, MidpointRounding.AwayFromZero);
                        var x1 = Math.Round((ii + 1) * cw / cols, MidpointRounding.AwayFromZero);
                        var y0 = Math.Round(j * ch / rows, MidpointRounding.AwayFromZero);
                        var y1 = Math.Round((j + 1) * ch / rows, MidpointRounding.AwayFromZero);
                        var d = 0.5 * Math.Sqrt(Math.Pow((x0 + x1) / 2 - cx, 2) + Math.Pow((y0 + y1) / 2 - cy, 2)) / dAll * 1.4 + 0.08 * J.R(s0, ii, j, 3);
                        var t = J.Clamp((p - Math.Min(0.55, d)) / 0.45);
                        if (t <= 0)
                        {
                            stationary.AddRect(new SKRect((float)x0, (float)y0, (float)x1, (float)y1));
                            any = true;
                        }
                        else moving.Add((x0, y0, x1, y1, t, ii, j));
                    }
                if (any)
                {
                    ctx.ClipPath(stationary, antialias: false);
                    DrawImg(ctx, a, 0, 0);
                }
            }
            ctx.Restore();
            var lw = Math.Max(1, LwOf(i, 0.002));
            foreach (var m in moving)
            {
                var (x0, y0, x1, y1, t, ii, j) = m;
                var w = x1 - x0;
                var h = y1 - y0;
                var al = 1 - J.Smooth(0.7, 1, t);
                if (al <= 0.003) continue;
                var dx = J.Rs(s0, ii, j, 4) * cw * 0.12 * t;
                var dy = (t * t * 1.25 - J.R(s0, ii, j, 6) * 0.08 * t) * ch;
                var rot = J.Rs(s0, ii, j, 5) * 70 * t;
                var s = 1 - 0.25 * t;
                ctx.Save();
                ctx.Translate((float)(x0 + w / 2 + dx), (float)(y0 + h / 2 + dy));
                ctx.RotateDegrees((float)rot);
                ctx.Scale((float)s, (float)s);
                using (var pnt = AlphaPaint(al))
                    ctx.DrawImage(a, new SKRect((float)x0, (float)y0, (float)x1, (float)y1),
                        new SKRect((float)(-w / 2), (float)(-h / 2), (float)(w / 2), (float)(h / 2)), SampNearest, pnt);
                StrokeRectImg(ctx, -w / 2, -h / 2, w, h, "#000000", lw, 0.35);
                ctx.Restore();
            }
        }),
    });

    private static void RegisterSliceShift() => RegX("sliceShift", new TransDef
    {
        Name = "短冊ずらし",
        Tags = new[] { "glitch", "graphic", "pop" },
        W = 0.8,
        Dur = 0.35,
        Plan = (rng, st) => new JObject { ["n"] = (double)rng.Int(5, 9), ["vert"] = rng.Chance(0.25) },
        Draw = Wrap((ctx, a, b, p, i, pp) =>
        {
            var cw = (double)i.Cw;
            var ch = (double)i.Ch;
            var n = (int)D(pp, "n");
            var vert = Truthy(pp["vert"]);
            var l = vert ? cw : ch;
            var m2 = vert ? ch : cw;
            var lw = Math.Max(1, LwOf(i, 0.003));
            var ac = TAcc(i);
            for (var ii = 0; ii < n; ii++)
            {
                var a0 = Math.Round(ii * l / n, MidpointRounding.AwayFromZero);
                var a1 = Math.Round((ii + 1) * l / n, MidpointRounding.AwayFromZero);
                var sg = ii % 2 != 0 ? 1 : -1;
                var t = J.E.InOutCubic(J.Clamp((p - ii / (double)Math.Max(1, n - 1) * 0.3) / 0.7));
                var o = Math.Round(t * m2, MidpointRounding.AwayFromZero) * sg;
                if (vert)
                {
                    PartImg(ctx, a, a0, 0, a1 - a0, ch, 0, o);
                    PartImg(ctx, b, a0, 0, a1 - a0, ch, 0, o - sg * ch);
                }
                else
                {
                    PartImg(ctx, a, 0, a0, cw, a1 - a0, o, 0);
                    PartImg(ctx, b, 0, a0, cw, a1 - a0, o - sg * cw, 0);
                }
            }
            var alpha = Math.Pow(Bell(p), 0.7) * 0.9;
            for (var ii = 1; ii < n; ii++)
            {
                var aq = Math.Round(ii * l / n, MidpointRounding.AwayFromZero);
                if (vert) FillRect(ctx, aq - lw / 2, 0, lw, ch, ac, alpha);
                else FillRect(ctx, 0, aq - lw / 2, cw, lw, ac, alpha);
            }
        }),
    });

    private static void RegisterCubeTurn() => RegX("cubeTurn", new TransDef
    {
        Name = "キューブ",
        Tags = new[] { "graphic", "pop" },
        W = 0.6,
        Dur = 0.45,
        Plan = (rng, st) => new JObject { ["dir"] = (double)(rng.Chance(0.6) ? 1 : -1) },
        Draw = Wrap((ctx, a, b, p, i, pp) =>
        {
            var cw = (double)i.Cw;
            var ch = (double)i.Ch;
            var phi = J.E.InOutCubic(p) * Math.PI / 2;
            const double dd = 3.4;
            var f = dd - 1;
            var cs = Math.Cos(phi);
            var sn = Math.Sin(phi);
            var dir = D(pp, "dir");
            var back = J.Mix(J.Mix(ScK(i.ScPrev ?? i.Sc, "bg"), ScK(i.Sc, "bg"), p), "#000000", 0.55);
            FillRect(ctx, 0, 0, cw, ch, back, 1);
            // 竖棱的 (x,z) 旋转后投影成屏幕 x 和半高
            double[] Prj(double x, double z)
            {
                var xr = (x * cs - z * sn) * dir;
                var zr = x * sn + z * cs;
                var k = f / (dd - zr);
                return new[] { cw / 2 + xr * k * cw / 2, k * ch / 2 };
            }
            void Face(SKImage c, double e00, double e01, double e10, double e11, double shade)
            {
                var nSlices = i.AllowFilter ? 28 : 14;
                var q0 = Prj(e00, e01);
                var q1 = Prj(e10, e11);
                if ((q1[0] - q0[0]) * dir <= 0.5) return;
                var prev = q0;
                using var pnt = AlphaPaint(1);
                for (var k = 1; k <= nSlices; k++)
                {
                    var u = k / (double)nSlices;
                    var q = Prj(J.Lerp(e00, e10, u), J.Lerp(e01, e11, u));
                    var xa = Math.Min(prev[0], q[0]);
                    var xb = Math.Max(prev[0], q[0]);
                    var hh = (prev[1] + q[1]) / 2;
                    var su = dir > 0 ? (k - 1) / (double)nSlices : 1 - k / (double)nSlices;
                    ctx.DrawImage(c, new SKRect((float)(su * cw), 0, (float)(su * cw + cw / nSlices), (float)ch),
                        new SKRect((float)Math.Floor(xa), (float)(ch / 2 - hh), (float)(Math.Floor(xa) + Math.Ceiling(xb) - Math.Floor(xa) + 1), (float)(ch / 2 + hh)), SampNearest, pnt);
                    prev = q;
                }
                if (shade > 0.005)
                {
                    using var sp = new SKPaint { Color = new SKColor(0, 0, 0, (byte)Math.Round(shade * 255)) };
                    using var path = new SKPath();
                    path.MoveTo((float)q0[0], (float)(ch / 2 - q0[1]));
                    path.LineTo((float)q1[0], (float)(ch / 2 - q1[1]));
                    path.LineTo((float)q1[0], (float)(ch / 2 + q1[1]));
                    path.LineTo((float)q0[0], (float)(ch / 2 + q0[1]));
                    path.Close();
                    ctx.DrawPath(path, sp);
                }
            }
            // A 贴正面，B 贴侧面；dir<0 时镜像
            Face(a, -1, 1, 1, 1, 0.55 * (1 - cs));
            Face(b, 1, 1, 1, -1, 0.55 * (1 - sn));
        }),
    });

    private static void RegisterFlashCross() => RegX("flashCross", new TransDef
    {
        Name = "フラッシュ転換",
        Tags = new[] { "emotional", "pop", "calm" },
        W = 0.9,
        Dur = 0.3,
        Plan = (rng, st) => new JObject { ["c"] = rng.Chance(0.3) ? "accent" : "white" },
        Draw = Wrap((ctx, a, b, p, i, pp) =>
        {
            var cw = (double)i.Cw;
            var ch = (double)i.Ch;
            var pb = ScK(i.ScPrev ?? i.Sc, "bg");
            var light = J.Lum(pb) > 0.62 && J.Lum(ScK(i.Sc, "bg")) > 0.62;
            var fl = S(pp, "c") == "accent" || light ? TAcc(i) : "#FFFFFF";
            var x = J.Smooth(0.3, 0.62, p);
            DrawImg(ctx, a, 0, 0);
            if (x > 0) DrawImg(ctx, b, 0, 0, x);
            var alpha = p < 0.45 ? J.E.InQuad(p / 0.45) : 1 - J.E.OutCubic((p - 0.45) / 0.55);
            if (alpha > 0.003) FillRect(ctx, 0, 0, cw, ch, fl, alpha * 0.92);
        }),
    });

    private static void RegisterPixelate() => RegX("pixelate", new TransDef
    {
        Name = "モザイク転換",
        Tags = new[] { "glitch", "pop" },
        W = 0.6,
        Dur = 0.4,
        Plan = (rng, st) => new JObject { ["k"] = rng.Range(11, 17) },
        Draw = Wrap((ctx, a, b, p, i, pp) =>
        {
            var cw = (double)i.Cw;
            var ch = (double)i.Ch;
            var maxB = MinD(i) / D(pp, "k");
            void Pix(SKImage c, double t, double alpha)
            {
                var bs = 1 + (maxB - 1) * t;
                if (alpha <= 0.003) return;
                if (bs < 1.6)
                {
                    DrawImg(ctx, c, 0, 0);
                    return;
                }
                var w = Math.Max(1, (int)Math.Ceiling(cw / bs));
                var h = Math.Max(1, (int)Math.Ceiling(ch / bs));
                var t2 = i.Tmp(w, h);
                var x = t2.Canvas;
                x.SetMatrix(SKMatrix.Identity);
                using (var src = new SKPaint { BlendMode = SKBlendMode.Src })
                    x.DrawImage(c, new SKRect(0, 0, c.Width, c.Height), new SKRect(0, 0, w, h), SampLinear, src);
                using var img = t2.Snapshot();
                using var pnt = AlphaPaint(alpha);
                ctx.DrawImage(img, new SKRect(0, 0, w, h), new SKRect(0, 0, (float)(w * bs), (float)(h * bs)), SampNearest, pnt);
            }
            var ta = J.E.InCubic(J.Clamp(p / 0.55));
            var tb = J.E.InCubic(J.Clamp((1 - p) / 0.55));
            var x2 = J.Smooth(0.4, 0.6, p);
            Pix(a, ta, 1);
            Pix(b, tb, x2);
        }),
    });
}
