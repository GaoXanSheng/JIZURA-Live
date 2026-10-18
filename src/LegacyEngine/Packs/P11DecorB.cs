// 移植自 frontend/src/engine/11p_decorB.js（decor 表现包 B，pack = 'decorB'，54 条）。
//
// 契约与 P11Decor.cs 相同：DecorDef.Draw(env, DecorSpec)；随机全走 J.R/J.Rs/J.Rr/J.H 的
// 无状态哈希（入参逐字保持）；ghost 通道换色走 Env.ColOf。与 A 包的差异：
// 1. getBB 在 pOut>0 且有缓存时按住静止 bbox（JS L27 退场锁框）。
// 2. vis/ACC/ACC2：在背景上真正看得清的配色（contrast 达标才用，否则退 fg）。
// 3. withSettle（SETTLE 名单）：相对歌词摆放的 decor 等入场落定后再起表（lt/ltb 前移 settleT）。
// 4. glyphLines：歌词字形框按行（竖排按列）归组（glyphLock 用）。
//
// JS 条目上的 `ae:` 字段（AE 导出模板名）引擎侧无任何消费者（全源 grep 无读取点），
// DecorDef 亦无对应属性，未注册——语义为死元数据。
//
// Env 缺口（本文件内补，不改 Env.cs）：env.beat → BeatOf（同 P11Decor）；env.energy != null →
// env.Plan.Energy is { Length: > 0 }；WeakMap → ConditionalWeakTable。
//
// 桩/TODO：无跨文件桩。ctx.* 全部 SKCanvas 同构直译（clip/transform/gradient/blend）。

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace JizuraLive.LegacyEngine;

public static class P11DecorB
{
    private const string Pk = "decorB";
    private static int _installed;

    // ================= 本包 helper（JS L7-440 逐条对位） =================

    private static double U(Env env) => Math.Min(env.W, env.H) / 1080.0;
    private static int MG(Env env) => (int)Math.Round(Math.Min(env.W, env.H) * 0.05, MidpointRounding.AwayFromZero);
    private static string MonoF(Env env) => JLayouts.Font0(env.St, "mono") ?? "mono";
    private static string BodyF(Env env) => JLayouts.Font0(env.St, "body") ?? "gothic_med";
    private static string SerifF(Env env) => JLayouts.Font0(env.St, "serif") ?? "mincho";
    private static string DispF(Env env) => JLayouts.Font0(env.St, "display") ?? "gothic_black";
    private static string? Col(Env env, string k) => env.Sc?.Str(k);
    private static bool Dark(Env env) => J.Lum(Col(env, "bg") ?? "#000000") < 0.5;
    private static double InE(Env env, double d = 0.4, double delay = 0, Func<double, double>? ease = null) =>
        (ease ?? J.E.OutExpo)(J.Clamp((env.Lt - delay) / d));
    private static double OutE(Env env) => 1 - J.E.InCubic(env.POut);
    private static double[] L2(double[] a, double[] b, double t) => new[] { a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t };
    private static double FS(Env env) => Math.Max(12, 16 * U(env));

    /// <summary>在背景上真正看得清的配色，太接近背景就退回 fg（JS vis）。</summary>
    private static string Vis(Env env, string? c, double min = 1.5) =>
        !string.IsNullOrEmpty(c) && J.Contrast(c, Col(env, "bg") ?? "#000000") >= min ? c : Col(env, "fg") ?? "#FFFFFF";
    private static string Acc(Env env) => Vis(env, Col(env, "accent"));
    private static string Acc2(Env env) => Vis(env, string.IsNullOrEmpty(Col(env, "accent2")) ? Col(env, "accent") : Col(env, "accent2"));

    // 每个 cut 记住最近一次有效 bbox（JS WeakMap BBC）
    private static readonly ConditionalWeakTable<Cut, BBox> Bbc = new();

    private static BBox GetBB(Env env, BBox? bb)
    {
        // 退场期间按住静止 bbox，文字可能放大或飞走
        if (env.POut > 0 && env.Cut != null && Bbc.TryGetValue(env.Cut, out var held)) return held;
        if (bb != null && double.IsFinite(bb.X0 + bb.X1 + bb.Y0 + bb.Y1) && bb.X1 > bb.X0 && bb.Y1 > bb.Y0)
        {
            var b = new BBox
            {
                X0 = Math.Max(bb.X0, -env.W * 0.1),
                X1 = Math.Min(bb.X1, env.W * 1.1),
                Y0 = Math.Max(bb.Y0, -env.H * 0.1),
                Y1 = Math.Min(bb.Y1, env.H * 1.1),
                Boxes = bb.Boxes ?? new List<JObject>(),
                Cx = bb.Cx,
                Cy = bb.Cy,
            };
            if (b.X1 <= b.X0) { b.X0 = bb.X0; b.X1 = bb.X1; }
            if (b.Y1 <= b.Y0) { b.Y0 = bb.Y0; b.Y1 = bb.Y1; }
            if (env.Cut != null)
            {
                Bbc.Remove(env.Cut);
                Bbc.Add(env.Cut, b);
            }
            return b;
        }
        var c = env.Cut != null && Bbc.TryGetValue(env.Cut, out var hit) ? hit : null;
        return c ?? JLayouts.CenterBb(env, null);
    }

    private static double Bw(BBox bb) => bb.X1 - bb.X0;
    private static double Bh(BBox bb) => bb.Y1 - bb.Y0;
    private static bool IsVert(BBox bb) => Bh(bb) > Bw(bb) * 1.25;

    private static bool HitBb(double x0, double y0, double x1, double y1, BBox bb, double pad = 0) =>
        !(x1 < bb.X0 - pad || x0 > bb.X1 + pad || y1 < bb.Y0 - pad || y0 > bb.Y1 + pad);

    private readonly record struct Spot(double X, double Y, double Cx, double Cy, bool Ok, int Sx, int Sy);

    private static Spot NearBB(Env env, BBox bb, double w, double h, DecorSpec p, double gap)
    {
        int w2 = env.W, h2 = env.H;
        var m = MG(env) * 0.8;
        var sx0 = p.Right ? 1 : -1;
        var sy0 = p.Low ? 1 : -1;
        var order = p.Corner
            ? new[] { new[] { sx0, sy0 }, new[] { -sx0, sy0 }, new[] { sx0, -sy0 }, new[] { -sx0, -sy0 } }
            : new[] { new[] { sx0, sy0 }, new[] { sx0, -sy0 }, new[] { -sx0, sy0 }, new[] { -sx0, -sy0 } };
        var tries = new List<double[]>();
        foreach (var o in order)
        {
            var sx = o[0];
            var sy = o[1];
            var ax = sx > 0 ? bb.X1 - w : bb.X0;
            var ox = sx > 0 ? bb.X1 + gap : bb.X0 - gap - w;
            var ay = sy > 0 ? bb.Y1 + gap : bb.Y0 - gap - h;
            var iy = sy > 0 ? bb.Y1 - h : bb.Y0;
            if (p.V % 2 != 0)
            {
                tries.Add(new[] { ox, iy, sx, sy });
                tries.Add(new[] { ax, ay, sx, sy });
                tries.Add(new[] { ox, ay, sx, sy });
            }
            else
            {
                tries.Add(new[] { ax, ay, sx, sy });
                tries.Add(new[] { ox, iy, sx, sy });
                tries.Add(new[] { ox, ay, sx, sy });
            }
        }

        foreach (var t in tries)
        {
            var x = J.Clamp(t[0], m, Math.Max(m, w2 - m - w));
            var y = J.Clamp(t[1], m, Math.Max(m, h2 - m - h));
            if (!HitBb(x, y, x + w, y + h, bb, gap * 0.4))
                return new Spot(x, y, x + w / 2, y + h / 2, true, (int)t[2], (int)t[3]);
        }
        return CornerSpot(env, bb, w, h, p);
    }

    private static Spot CornerSpot(Env env, BBox bb, double w, double h, DecorSpec p, double mk = 1)
    {
        int w2 = env.W, h2 = env.H;
        var m = MG(env) * mk;
        var sx0 = p.Right ? 1 : -1;
        var sy0 = p.Low ? 1 : -1;
        Spot? best = null;
        var bestA = 1e18;
        foreach (var o in new[] { new[] { sx0, sy0 }, new[] { -sx0, sy0 }, new[] { sx0, -sy0 }, new[] { -sx0, -sy0 } })
        {
            var sx = o[0];
            var sy = o[1];
            var x = sx > 0 ? w2 - m - w : m;
            var y = sy > 0 ? h2 - m - h : m;
            if (!HitBb(x, y, x + w, y + h, bb, 8))
                return new Spot(x, y, x + w / 2, y + h / 2, true, sx, sy);
            var ov = Math.Max(0, Math.Min(x + w, bb.X1) - Math.Max(x, bb.X0))
                     * Math.Max(0, Math.Min(y + h, bb.Y1) - Math.Max(y, bb.Y0));
            if (ov < bestA)
            {
                bestA = ov;
                best = new Spot(x, y, x + w / 2, y + h / 2, false, sx, sy);
            }
        }
        return best!.Value;
    }

    private static Spot SpotP(Env env, BBox bb, double w, double h, DecorSpec p, double gap) =>
        p.Corner ? CornerSpot(env, bb, w, h, p) : NearBB(env, bb, w, h, p, gap);

    private sealed class FreeRect
    {
        public double X, Y, W, H;
        public string Side = "";
    }

    /// <summary>歌词四周的可用矩形（上/下/左/右），最好用的排最前（JS freeRects）。</summary>
    private static List<FreeRect> FreeRects(Env env, BBox bb, double gap)
    {
        int w2 = env.W, h2 = env.H;
        var m = MG(env) * 0.8;
        var @out = new List<FreeRect>
        {
            new() { X = m, Y = m, W = w2 - 2 * m, H = bb.Y0 - gap - m, Side = "t" },
            new() { X = m, Y = bb.Y1 + gap, W = w2 - 2 * m, H = h2 - m - bb.Y1 - gap, Side = "b" },
            new() { X = m, Y = m, W = bb.X0 - gap - m, H = h2 - 2 * m, Side = "l" },
            new() { X = bb.X1 + gap, Y = m, W = w2 - m - bb.X1 - gap, H = h2 - 2 * m, Side = "r" },
        }.Where(r => r.W > 30 && r.H > 30).ToList();
        @out.Sort((a, b) => Math.Min(b.W, b.H * 1.5).CompareTo(Math.Min(a.W, a.H * 1.5)));
        return @out;
    }

    /// <summary>在歌词下方（或上方）的空带里放一个居中的 w×h 块，放不下就退回 spot()（JS bandSpot）。</summary>
    private static Spot BandSpot(Env env, BBox bb, double w, double h, DecorSpec p, double gap)
    {
        int w2 = env.W, h2 = env.H;
        var m = MG(env) * 0.8;
        foreach (var below in p.Low ? new[] { false, true } : new[] { true, false })
        {
            var y = below ? bb.Y1 + gap : bb.Y0 - gap - h;
            if (y < m || y + h > h2 - m) continue;
            var x = J.Clamp((bb.X0 + bb.X1) / 2 - w / 2, m, w2 - m - w);
            return new Spot(x, y, x + w / 2, y + h / 2, true, 0, 0);
        }
        return SpotP(env, bb, w, h, p, gap);
    }

    // ---- 批量绘制助手（同 P11Decor 的直译约定） ----

    private static void Stroke(Env env, IReadOnlyList<double[]> pts, string? c, double lw, double a = 1,
        bool g = false, bool close = false, SKStrokeCap cap = SKStrokeCap.Butt, SKStrokeJoin join = SKStrokeJoin.Miter)
    {
        var k = env.ColOf(c, g);
        if (k == null || a <= 0.003 || pts == null || pts.Count < 2) return;
        using var path = new SKPath();
        path.MoveTo((float)pts[0][0], (float)pts[0][1]);
        for (var i = 1; i < pts.Count; i++) path.LineTo((float)pts[i][0], (float)pts[i][1]);
        if (close) path.Close();
        using var p = new SKPaint
        {
            IsAntialias = true,
            IsStroke = true,
            StrokeWidth = (float)lw,
            StrokeCap = cap,
            StrokeJoin = join,
            Color = Env.SkColor(k, Math.Min(1, a)),
            BlendMode = env.Blend,
        };
        env.Ctx.DrawPath(path, p);
    }

    /// <summary>一个路径里画多条折线（JS strokes）。</summary>
    private static void Strokes(Env env, IEnumerable<double[][]> list, string? c, double lw, double a = 1,
        bool g = false, bool close = false, SKStrokeCap cap = SKStrokeCap.Butt, SKStrokeJoin join = SKStrokeJoin.Miter)
    {
        var k = env.ColOf(c, g);
        var arr = list as ICollection<double[][]> ?? list?.ToArray();
        if (k == null || a <= 0.003 || arr == null || arr.Count == 0) return;
        using var path = new SKPath();
        foreach (var pts in arr)
        {
            if (pts == null || pts.Length < 2) continue;
            path.MoveTo((float)pts[0][0], (float)pts[0][1]);
            for (var i = 1; i < pts.Length; i++) path.LineTo((float)pts[i][0], (float)pts[i][1]);
            if (close) path.Close();
        }
        using var p = new SKPaint
        {
            IsAntialias = true,
            IsStroke = true,
            StrokeWidth = (float)lw,
            StrokeCap = cap,
            StrokeJoin = join,
            Color = Env.SkColor(k, Math.Min(1, a)),
            BlendMode = env.Blend,
        };
        env.Ctx.DrawPath(path, p);
    }

    private static void Segs(Env env, IEnumerable<double[]> list, string? c, double lw, double a = 1,
        bool g = false, SKStrokeCap cap = SKStrokeCap.Butt)
    {
        var k = env.ColOf(c, g);
        var arr = list as ICollection<double[]> ?? list?.ToArray();
        if (k == null || a <= 0.003 || arr == null || arr.Count == 0) return;
        using var path = new SKPath();
        foreach (var s in arr)
        {
            path.MoveTo((float)s[0], (float)s[1]);
            path.LineTo((float)s[2], (float)s[3]);
        }
        using var p = new SKPaint
        {
            IsAntialias = true,
            IsStroke = true,
            StrokeWidth = (float)lw,
            StrokeCap = cap,
            Color = Env.SkColor(k, Math.Min(1, a)),
            BlendMode = env.Blend,
        };
        env.Ctx.DrawPath(path, p);
    }

    /// <summary>一个路径里画一批圆弧 [[cx,cy,r,a0(rad),a1(rad)],…]（弧度制）。JS arcs。</summary>
    private static void ArcsB(Env env, IEnumerable<double[]> list, string? c, double lw, double a = 1,
        bool g = false, SKStrokeCap cap = SKStrokeCap.Butt)
    {
        var k = env.ColOf(c, g);
        var arr = list as ICollection<double[]> ?? list?.ToArray();
        if (k == null || a <= 0.003 || arr == null || arr.Count == 0) return;
        using var path = new SKPath();
        foreach (var s in arr)
        {
            if (s[2] <= 0) continue;
            var sweep = (float)((s[4] - s[3]) / J.DEG);
            var start = (float)(s[3] / J.DEG);
            path.MoveTo((float)(s[0] + Math.Cos(s[3]) * s[2]), (float)(s[1] + Math.Sin(s[3]) * s[2]));
            path.ArcTo(new SKRect((float)(s[0] - s[2]), (float)(s[1] - s[2]), (float)(s[0] + s[2]), (float)(s[1] + s[2])),
                start, sweep, false);
        }
        using var p = new SKPaint
        {
            IsAntialias = true,
            IsStroke = true,
            StrokeWidth = (float)lw,
            StrokeCap = cap,
            Color = Env.SkColor(k, Math.Min(1, a)),
            BlendMode = env.Blend,
        };
        env.Ctx.DrawPath(path, p);
    }

    private static void Dots(Env env, IEnumerable<double[]> list, string? c, double a = 1, bool g = false)
    {
        var k = env.ColOf(c, g);
        var arr = list as ICollection<double[]> ?? list?.ToArray();
        if (k == null || a <= 0.003 || arr == null || arr.Count == 0) return;
        using var p = new SKPaint { IsAntialias = true, Color = Env.SkColor(k, Math.Min(1, a)), BlendMode = env.Blend };
        foreach (var d in arr)
        {
            if (d[2] <= 0.05) continue;
            env.Ctx.DrawCircle((float)d[0], (float)d[1], (float)d[2], p);
        }
    }

    /// <summary>一批描边圆 [[x,y,r],…]（JS rings）。</summary>
    private static void RingsB(Env env, IEnumerable<double[]> list, string? c, double lw, double a = 1, bool g = false)
    {
        var k = env.ColOf(c, g);
        var arr = list as ICollection<double[]> ?? list?.ToArray();
        if (k == null || a <= 0.003 || arr == null || arr.Count == 0) return;
        using var path = new SKPath();
        foreach (var d in arr)
        {
            if (d[2] <= 0.05) continue;
            path.AddCircle((float)d[0], (float)d[1], (float)d[2]);
        }
        using var p = new SKPaint
        {
            IsAntialias = true,
            IsStroke = true,
            StrokeWidth = (float)lw,
            Color = Env.SkColor(k, Math.Min(1, a)),
            BlendMode = env.Blend,
        };
        env.Ctx.DrawPath(path, p);
    }

    private static void Rects(Env env, IEnumerable<double[]> list, string? c, double a = 1, bool g = false)
    {
        var k = env.ColOf(c, g);
        var arr = list as ICollection<double[]> ?? list?.ToArray();
        if (k == null || a <= 0.003 || arr == null || arr.Count == 0) return;
        using var path = new SKPath();
        foreach (var r in arr)
            if (r[2] > 0 && r[3] > 0)
                path.AddRect(new SKRect((float)r[0], (float)r[1], (float)(r[0] + r[2]), (float)(r[1] + r[3])));
        using var p = new SKPaint { IsAntialias = true, Color = Env.SkColor(k, Math.Min(1, a)), BlendMode = env.Blend };
        env.Ctx.DrawPath(path, p);
    }

    private static void Polys(Env env, IEnumerable<double[][]> list, string? c, double a = 1, bool g = false,
        SKBlendMode? blend = null)
    {
        var k = env.ColOf(c, g);
        var arr = list as ICollection<double[][]> ?? list?.ToArray();
        if (k == null || a <= 0.003 || arr == null || arr.Count == 0) return;
        using var path = new SKPath();
        foreach (var pts in arr)
        {
            if (pts == null || pts.Length < 3) continue;
            path.MoveTo((float)pts[0][0], (float)pts[0][1]);
            for (var i = 1; i < pts.Length; i++) path.LineTo((float)pts[i][0], (float)pts[i][1]);
            path.Close();
        }
        using var p = new SKPaint
        {
            IsAntialias = true,
            Color = Env.SkColor(k, Math.Min(1, a)),
            BlendMode = blend ?? env.Blend,
        };
        env.Ctx.DrawPath(path, p);
    }

    /// <summary>圆角矩形描边（只画 main pass）。JS rrStroke。</summary>
    private static void RrStroke(Env env, double x, double y, double w, double h, double r, string? c, double lw, double a)
    {
        if (env.Pass != "main" || a <= 0.003 || w <= 0 || h <= 0 || c == null) return;
        r = Math.Max(0, Math.Min(r, Math.Min(w / 2, h / 2)));
        using var rr = new SKRoundRect(new SKRect((float)x, (float)y, (float)(x + w), (float)(y + h)), (float)r);
        using var p = new SKPaint
        {
            IsAntialias = true,
            IsStroke = true,
            StrokeWidth = (float)lw,
            Color = Env.SkColor(c, Math.Min(1, a)),
            BlendMode = env.Blend,
        };
        env.Ctx.DrawRoundRect(rr, p);
    }

    /// <summary>圆角矩形填充（只画 main pass）。JS rrFill。</summary>
    private static void RrFill(Env env, double x, double y, double w, double h, double r, string? c, double a)
    {
        if (env.Pass != "main" || a <= 0.003 || w <= 0 || h <= 0 || c == null) return;
        r = Math.Max(0, Math.Min(r, Math.Min(w / 2, h / 2)));
        using var rr = new SKRoundRect(new SKRect((float)x, (float)y, (float)(x + w), (float)(y + h)), (float)r);
        using var p = new SKPaint
        {
            IsAntialias = true,
            Color = Env.SkColor(c, Math.Min(1, a)),
            BlendMode = env.Blend,
        };
        env.Ctx.DrawRoundRect(rr, p);
    }

    private static void Dash(Env env, double x0, double y0, double x1, double y1, double on, double off,
        string? c, double lw, double a = 1, double phase = 0)
    {
        if (env.Pass != "main" || a <= 0.003) return;
        var l = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
        if (l < 1) return;
        var dx = (x1 - x0) / l;
        var dy = (y1 - y0) / l;
        var per = on + off;
        var list = new List<double[]>();
        var s = -(((phase % per) + per) % per);
        for (var i = 0; i < 600 && s < l; i++, s += per)
        {
            var a0 = Math.Max(0, s);
            var a1 = Math.Min(l, s + on);
            if (a1 > a0) list.Add(new[] { x0 + dx * a0, y0 + dy * a0, x0 + dx * a1, y0 + dy * a1 });
        }
        Segs(env, list, c, lw, a, false);
    }

    private static double[][] Part(IReadOnlyList<double[]> pts, double e0, double e1)
    {
        e0 = J.Clamp(e0);
        e1 = J.Clamp(e1);
        if (e1 <= e0 || pts.Count < 2) return Array.Empty<double[]>();
        var d = new double[pts.Count];
        for (var i = 1; i < pts.Count; i++)
            d[i] = d[i - 1] + Math.Sqrt(Math.Pow(pts[i][0] - pts[i - 1][0], 2) + Math.Pow(pts[i][1] - pts[i - 1][1], 2));
        var l = d[^1];
        if (l <= 0) return Array.Empty<double[]>();
        var a0 = e0 * l;
        var b0 = e1 * l;
        double[] At(double s)
        {
            var i = 1;
            while (i < d.Length - 1 && d[i] < s) i++;
            var k = (s - d[i - 1]) / Math.Max(1e-6, d[i] - d[i - 1]);
            return L2(pts[i - 1], pts[i], J.Clamp(k));
        }
        var @out = new List<double[]> { At(a0) };
        for (var i = 1; i < pts.Count - 1; i++)
            if (d[i] > a0 && d[i] < b0)
                @out.Add(pts[i]);
        @out.Add(At(b0));
        return @out.ToArray();
    }

    /// <summary>长度占比 t 处的点和切线角（JS along）。</summary>
    private static (double X, double Y, double Ang) Along(IReadOnlyList<double[]> pts, double t)
    {
        var p = Part(pts, 0, J.Clamp(t, 0.0005, 1));
        var a = p[^1];
        var b = p.Length > 1 ? p[^2] : pts[0];
        return (a[0], a[1], Math.Atan2(a[1] - b[1], a[0] - b[0]));
    }

    /// <summary>圆弧点列（角度制，JS arcP）。</summary>
    private static double[][] ArcP(double cx, double cy, double r, double a0, double a1, int n = 48)
    {
        var o = new List<double[]>();
        for (var i = 0; i <= n; i++)
        {
            var a = (a0 + (a1 - a0) * i / n) * J.DEG;
            o.Add(new[] { cx + Math.Cos(a) * r, cy + Math.Sin(a) * r });
        }
        return o.ToArray();
    }

    /// <summary>椭圆弧点列（角度制 + 旋转，JS ellP）。</summary>
    private static double[][] EllP(double cx, double cy, double rx, double ry, double rot, double a0, double a1, int n = 48)
    {
        var o = new List<double[]>();
        var c = Math.Cos(rot);
        var s = Math.Sin(rot);
        for (var i = 0; i <= n; i++)
        {
            var a = (a0 + (a1 - a0) * i / n) * J.DEG;
            var x = Math.Cos(a) * rx;
            var y = Math.Sin(a) * ry;
            o.Add(new[] { cx + x * c - y * s, cy + x * s + y * c });
        }
        return o.ToArray();
    }

    /// <summary>点列绕 (cx,cy) 旋转缩放（JS xf）。</summary>
    private static double[][] Xf(IReadOnlyList<double[]> pts, double cx, double cy, double ang = 0, double s = 1)
    {
        var c = Math.Cos(ang);
        var sn = Math.Sin(ang);
        return pts.Select(q => new[] { cx + (q[0] * c - q[1] * sn) * s, cy + (q[0] * sn + q[1] * c) * s }).ToArray();
    }

    /// <summary>三次贝塞尔采样（JS bez）。</summary>
    private static double[][] Bez(double[] p0, double[] p1, double[] p2, double[] p3, int n = 20)
    {
        var o = new List<double[]>();
        for (var i = 0; i <= n; i++)
        {
            var t = i / (double)n;
            var mt = 1 - t;
            o.Add(new[]
            {
                mt * mt * mt * p0[0] + 3 * mt * mt * t * p1[0] + 3 * mt * t * t * p2[0] + t * t * t * p3[0],
                mt * mt * mt * p0[1] + 3 * mt * mt * t * p1[1] + 3 * mt * t * t * p2[1] + t * t * t * p3[1],
            });
        }
        return o.ToArray();
    }

    private static void Label(Env env, object text, double x, double y, JObject? o = null)
    {
        var it = new JObject
        {
            ["text"] = text.ToString(),
            ["font"] = MonoF(env),
            ["size"] = FS(env),
            ["x"] = x,
            ["y"] = y,
            ["color"] = Col(env, "sub"),
            ["align"] = "left",
            ["track"] = 0.08,
            ["ghost"] = false,
        };
        if (o != null)
            foreach (var kv in o)
                it[kv.Key] = kv.Value;
        env.DrawItem(it);
    }

    private static JObject JO(params object[] kv)
    {
        var o = new JObject();
        for (var i = 0; i + 1 < kv.Length; i += 2)
            o[(string)kv[i]] = kv[i + 1] switch
            {
                string s => new JValue(s),
                bool b => new JValue(b),
                int n => new JValue((double)n),
                long n => new JValue((double)n),
                double d => new JValue(d),
                JToken t => t,
                null => JValue.CreateNull(),
                _ => new JValue(kv[i + 1].ToString()),
            };
        return o;
    }

    private static double TextW(string text, string font, double size, double track = 0.08) =>
        JLayouts.Measure(new JObject { ["text"] = text, ["font"] = font, ["size"] = size, ["track"] = track }).W;

    private static string Pad2(double n, int k = 2) =>
        Math.Max(0, Math.Floor(n)).ToString("F0", CultureInfo.InvariantCulture).PadLeft(k, '0');

    private static string LyricChar(Env env, int salt)
    {
        var cut = env.Cut;
        var src = !string.IsNullOrEmpty(cut?.Text) ? cut.Text : cut?.LineText ?? "";
        var arr = JLayouts.CodePoints(src).Where(c => c.Trim().Length > 0 && !J.IsPunct(c[0])).ToList();
        if (arr.Count == 0) return "";
        var kan = arr.Where(c => J.IsKanji(c[0])).ToList();
        var pool = kan.Count > 0 ? kan : arr.Where(c => J.IsKata(c[0]) || J.IsHira(c[0]) || J.IsLatin(c[0])).ToList();
        var p = pool.Count > 0 ? pool : arr;
        return p[(int)(J.H(unchecked((int)(cut?.Seed ?? 0)), salt, 5) % (uint)p.Count)];
    }

    private static double ClearOf(BBox bb, double x, double y, double pad, double soft)
    {
        var dx = Math.Max(Math.Max(bb.X0 - pad - x, 0), x - bb.X1 - pad);
        var dy = Math.Max(Math.Max(bb.Y0 - pad - y, 0), y - bb.Y1 - pad);
        return J.Clamp(Math.Sqrt(dx * dx + dy * dy) / soft);
    }

    private static double Wrap(double v, double lo, double span) => lo + (((v - lo) % span) + span) % span;

    private readonly record struct BeatInfo(bool Has, double Since, double Len, int Index);

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

    private sealed class GlyphLine
    {
        public double K;
        public List<double[]> G = new(); // [x0, y0, x1, y1]
    }

    private sealed class GlyphLines
    {
        public bool Vert;
        public List<GlyphLine> Lines = new();
    }

    /// <summary>歌词的字形框换算到屏幕坐标，按行归组（竖排按列）。JS glyphLines。</summary>
    private static GlyphLines GlyphLinesOf(BBox bb)
    {
        var vert = IsVert(bb);
        var res = new GlyphLines { Vert = vert };
        if (bb.Boxes is { Count: > 0 } && bb.Cx != 0)
        {
            foreach (var b in bb.Boxes)
            {
                var g = new[]
                {
                    bb.Cx + Num(b, "x") - Num(b, "w") / 2, bb.Cy + Num(b, "y") - Num(b, "h") / 2,
                    bb.Cx + Num(b, "x") + Num(b, "w") / 2, bb.Cy + Num(b, "y") + Num(b, "h") / 2,
                };
                var key = vert ? (g[0] + g[2]) / 2 : (g[1] + g[3]) / 2;
                var sz = vert ? g[2] - g[0] : g[3] - g[1];
                var l = res.Lines.FirstOrDefault(x => Math.Abs(x.K - key) < sz * 0.4);
                if (l == null)
                {
                    l = new GlyphLine { K = key };
                    res.Lines.Add(l);
                }
                l.G.Add(g);
            }
            res.Lines.Sort((a, b) => vert ? b.K.CompareTo(a.K) : a.K.CompareTo(b.K));
            foreach (var l in res.Lines)
                l.G.Sort((a, b) => vert ? a[1].CompareTo(b[1]) : a[0].CompareTo(b[0]));
        }
        return res;
    }

    private static double Num(JObject o, string k)
    {
        var v = o[k];
        return v != null && (v.Type == JTokenType.Float || v.Type == JTokenType.Integer) ? (double)v : 0;
    }

    /// <summary>相对歌词摆放的 decor 等入场基本落定（JS settleT + withSettle：lt/ltb 前移）。</summary>
    private static void WithSettle(Env env, DecorSpec p, Action<Env, DecorSpec> draw)
    {
        var s = J.Clamp((env.Cut?.InDur ?? 0) * 0.6, 0, 0.35);
        if (s <= 0.001)
        {
            draw(env, p);
            return;
        }
        if (env.Lt < s) return;
        var lt0 = env.Lt;
        var ltb0 = env.Ltb;
        try
        {
            env.Lt = lt0 - s;
            env.Ltb = ltb0 - s;
            draw(env, p);
        }
        finally
        {
            env.Lt = lt0;
            env.Ltb = ltb0;
        }
    }

    // ================= 注册（JS DEF 键序 + SETTLE 名单） =================

    /// <summary>幂等注册本文件全部 decor（顺序 = JS DEF 对象键序；SETTLE 名单的条目套 WithSettle）。</summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;
        RegisterKamon();
        RegisterSeigaiha();
        RegisterAsanoha();
        RegisterHanabi();
        RegisterChochin();
        RegisterShimenawa();
        RegisterSensu();
        RegisterTsukiKumo();
        RegisterMomiji();
        RegisterNamiGashira();
        RegisterKasumi();
        RegisterHexGrid();
        RegisterSpectrumRing();
        RegisterDataColumns();
        RegisterSpinner();
        RegisterHeadingTape();
        RegisterGlyphLock();
        RegisterAtomOrbit();
        RegisterSonarArcs();
        RegisterCircuit();
        RegisterSwatches();
        RegisterRegistration();
        RegisterPunchHoles();
        RegisterStaple();
        RegisterPaperClip();
        RegisterIndexTabs();
        RegisterVines();
        RegisterCloudPuffs();
        RegisterStarField();
        RegisterMoonPhases();
        RegisterSunRays();
        RegisterRainRipples();
        RegisterBubbles();
        RegisterSmoke();
        RegisterDandelion();
        RegisterFireflies();
        RegisterMemphis();
        RegisterZigzagRibbon();
        RegisterPolkaPatch();
        RegisterStripeCircle();
        RegisterDecoCorners();
        RegisterHalfCircles();
        RegisterLoopArrows();
        RegisterStarburst();
        RegisterTally();
        RegisterCursorClick();
        RegisterWindowChrome();
        RegisterProgressBar();
        RegisterToggleSwitch();
        RegisterNotifBell();
        RegisterLikeCounter();
        RegisterMediaControls();
        RegisterVolumeBars();
        RegisterMusicNotes();
    }

    private static void Reg0(string key, DecorDef def) => Reg.Register("decor", key, def, Pk);

    private static readonly string[] KamonNames = { "三巴纹", "七宝纹", "梅钵纹", "轮违纹", "三轮纹" };

    /// <summary>单位半径的家纹路径（闭合折线），局部坐标（JS kamonPaths）。</summary>
    private static List<double[][]> KamonPaths(int kind)
    {
        var @out = new List<double[][]>();
        switch (kind)
        {
            case 0: // 三巴纹：三条旋涡
                for (var k = 0; k < 3; k++)
                {
                    var th0 = (-90 + k * 120) * J.DEG;
                    var sw = 150 * J.DEG;
                    const double h0 = 0.27;
                    const int m = 30;
                    var outer = new List<double[]>();
                    var inner = new List<double[]>();
                    for (var i = 0; i <= m; i++)
                    {
                        var t = i / (double)m;
                        var th = th0 + t * sw;
                        var rc = 0.38 + 0.46 * t;
                        var w = h0 * Math.Pow(1 - t, 0.85);
                        outer.Add(new[] { Math.Cos(th) * (rc + w), Math.Sin(th) * (rc + w) });
                        inner.Add(new[] { Math.Cos(th) * (rc - w), Math.Sin(th) * (rc - w) });
                    }
                    var hc = new[] { Math.Cos(th0) * 0.38, Math.Sin(th0) * 0.38 };
                    var rh = new[] { Math.Cos(th0), Math.Sin(th0) };
                    var thv = new[] { -Math.Sin(th0), Math.Cos(th0) };
                    var cap = new List<double[]>();
                    for (var i = 1; i < 16; i++)
                    {
                        var f = i / 16.0 * Math.PI;
                        cap.Add(new[] { hc[0] + h0 * (-rh[0] * Math.Cos(f) - thv[0] * Math.Sin(f)), hc[1] + h0 * (-rh[1] * Math.Cos(f) - thv[1] * Math.Sin(f)) });
                    }
                    inner.Reverse();
                    var poly = outer.Concat(inner).Concat(cap).ToList();
                    poly.Add(outer[0]);
                    @out.Add(poly.ToArray());
                }
                break;
            case 1: // 七宝纹：四个过圆心的圆
                for (var k = 0; k < 4; k++)
                {
                    var a = k * 90;
                    @out.Add(ArcP(Math.Cos(a * J.DEG) * 0.5, Math.Sin(a * J.DEG) * 0.5, 0.5, a + 180, a + 540, 40));
                }
                @out.Add(ArcP(0, 0, 0.13, -90, 270, 20));
                break;
            case 2: // 梅钵纹：五个圆瓣绕小圆心
                for (var k = 0; k < 5; k++)
                {
                    var a = -90 + k * 72;
                    @out.Add(ArcP(Math.Cos(a * J.DEG) * 0.6, Math.Sin(a * J.DEG) * 0.6, 0.3, a + 180, a + 540, 32));
                }
                @out.Add(ArcP(0, 0, 0.17, -90, 270, 20));
                break;
            case 3: // 轮违纹：两个相扣的环
                @out.Add(ArcP(-0.3, 0, 0.55, 0, 360, 48));
                @out.Add(ArcP(0.3, 0, 0.55, 180, 540, 48));
                break;
            default: // 三轮纹：三个相扣的环
                for (var k = 0; k < 3; k++)
                {
                    var a = -90 + k * 120;
                    @out.Add(ArcP(Math.Cos(a * J.DEG) * 0.4, Math.Sin(a * J.DEG) * 0.4, 0.46, a + 180, a + 540, 40));
                }
                break;
        }
        return @out;
    }

    private static void RegisterKamon() => Reg0("kamon", new DecorDef
    {
        Name = "家紋",
        Tags = new[] { "editorial", "calm", "emotional" },
        W = 0.8,
        Layer = "front",
        Draw = (env, p) => WithSettle(env, p, (env2, p2) =>
        {
            if (env2.Pass != "main") return;
            var bb = GetBB(env2, env2.Body);
            var sc = env2.Sc!;
            var u = U(env2);
            var o = OutE(env2);
            if (o <= 0.003 || env2.Lt < 0) return;
            var r0 = J.Clamp(Math.Min(env2.W, env2.H) * 0.075, 48 * u, 96 * u);
            var fs = FS(env2) * 0.85;
            var sp = SpotP(env2, bb, r0 * 2.3, r0 * 2.3 + fs * 2.2, p2, 24 * u);
            var cx = sp.Cx;
            var cy = sp.Y + r0 * 1.15;
            var a = o * (sp.Ok ? 1 : 0.35);
            var kind = p2.V % 5;
            var lw = Math.Max(1, 1.5 * u);
            var e0 = J.E.InOutCubic(J.Clamp(env2.Lt / 0.6));
            Stroke(env2, ArcP(cx, cy, r0, -90, -90 + 360 * e0, 90), Col(env2, "fg"), lw, a);
            Stroke(env2, ArcP(cx, cy, r0 * 0.9, 90, 90 + 360 * e0, 90), Col(env2, "fg"), Math.Max(1, 0.8 * u), a * 0.55);
            var spin = kind == 0 || kind == 4 ? env2.Ltb * 8 * (p2.Right ? 1 : -1) : 0;
            var ang = ((1 - J.E.OutExpo(J.Clamp(env2.Lt / 0.9))) * -70 + spin + (kind == 0 ? p2.R * 120 : 0)) * J.DEG;
            var paths = KamonPaths(kind);
            var col = p2.Accent ? Acc(env2) : Col(env2, "fg");
            var s = r0 * 0.8;
            for (var i = 0; i < paths.Count; i++)
            {
                var e = J.E.InOutCubic(J.Clamp((env2.Lt - 0.12 - i * 0.07) / 0.55));
                if (e <= 0) continue;
                Stroke(env2, Part(Xf(paths[i], cx, cy, ang, s), 0, e), col, lw * 1.1, a, false, join: SKStrokeJoin.Round);
            }
            env2.Circle(cx, cy, 2.2 * u, Acc(env2), null, 0, a * InE(env2, 0.3, 0.5), false);
            var le = InE(env2, 0.4, 0.45, J.E.OutCubic);
            Label(env2, KamonNames[kind], cx, cy + r0 + fs * 1.35,
                JO("font", SerifF(env2), "size", fs, "align", "center", "track", 0.32, "color", Col(env2, "sub"), "alpha", a * le));
        }),
    });

    private static void RegisterSeigaiha() => Reg0("seigaiha", new DecorDef
    {
        Name = "青海波",
        Tags = new[] { "calm", "emotional", "editorial" },
        W = 0.8,
        Layer = "back",
        Subtle = true,
        Draw = (env, p) =>
        {
            if (env.Pass != "main" || env.Lt < 0) return;
            var w2 = env.W;
            var h2 = env.H;
            var sc = env.Sc!;
            var u = U(env);
            var o = OutE(env);
            if (o <= 0.003) return;
            var r = J.Clamp(Math.Min(w2, h2) * 0.05, 34 * u, 60 * u);
            var sx = p.Right ? 1 : -1;
            var sy = p.Low ? 1 : -1;
            var ox = sx > 0 ? (double)w2 : 0;
            var oy = sy > 0 ? (double)h2 : 0;
            var pw = w2 * J.Rr(0.42, 0.62, p.Seed, 1);
            var ph = h2 * J.Rr(0.45, 0.65, p.Seed, 2);
            var grow = J.E.OutCubic(J.Clamp(env.Lt / 1.0)) * 1.3;
            var drift = (env.Ltb * 7 * u % (2 * r)) * -sx;
            const int nr = 4;
            var gam = new List<double>();
            for (var k = 0; k < nr; k++)
            {
                var rho = r * (1 - k / (double)nr);
                var ca = (rho * rho + 1.25 * r * r - r * r) / (2 * rho * 1.118 * r);
                gam.Add(Math.Max(0, Math.Acos(J.Clamp(ca, -1, 1)) - 26.565 * J.DEG));
            }
            var buckets = new List<double[]>[4] { new(), new(), new(), new() };
            var rows = (int)Math.Ceiling(ph / (r / 2)) + 2;
            var cols = (int)Math.Ceiling(pw / (2 * r)) + 2;
            var y0 = sy > 0 ? h2 - ph : -r;
            var x0 = sx > 0 ? w2 - pw - 2 * r : -2 * r;
            for (var j = 0; j < rows && j < 80; j++)
            {
                var y = y0 + j * r / 2;
                for (var i = 0; i < cols && i < 60; i++)
                {
                    var x = x0 + i * 2 * r + (j % 2) * r + drift;
                    var d = Math.Sqrt(Math.Pow((x - ox) / pw, 2) + Math.Pow((y - oy) / ph, 2));
                    var f = Math.Pow(J.Clamp(1 - d), 0.6) * J.Clamp((grow - d) * 3);
                    if (f <= 0.04) continue;
                    var b = Math.Min(3, (int)Math.Floor(f * 4));
                    for (var k = 0; k < nr; k++)
                        buckets[b].Add(new[] { x, y, r * (1 - k / (double)nr), Math.PI + gam[k], J.TAU - gam[k] });
                }
            }
            var col = p.Accent ? Acc(env) : Col(env, "sub");
            var @base = (p.Accent ? 0.5 : 0.46) * (Dark(env) ? 1 : 0.8) * o;
            var lw = Math.Max(1, 1.3 * u);
            for (var b = 0; b < 4; b++) ArcsB(env, buckets[b], col, lw, @base * (b + 1) / 4.0);
        },
    });

    private static void RegisterAsanoha() => Reg0("asanoha", new DecorDef
    {
        Name = "麻の葉",
        Tags = new[] { "calm", "editorial", "emotional" },
        W = 0.8,
        Layer = "back",
        Subtle = true,
        Draw = (env, p) =>
        {
            if (env.Pass != "main" || env.Lt < 0) return;
            var w2 = env.W;
            var h2 = env.H;
            var sc = env.Sc!;
            var ctx = env.Ctx;
            var u = U(env);
            var o = OutE(env);
            if (o <= 0.003) return;
            var m = Math.Min(w2, h2);
            var r0 = m * J.Rr(0.27, 0.36, p.Seed, 1);
            var portrait = h2 > w2 * 1.1;
            var cx = portrait ? w2 * (0.5 + (p.Right ? 0.14 : -0.14)) : w2 * (0.5 + (p.Right ? 0.22 : -0.22)) + J.Rs(p.Seed, 2) * w2 * 0.04;
            var cy = portrait ? h2 * (p.Low ? 0.66 : 0.34) : h2 * (0.5 + (p.Low ? 0.07 : -0.07));
            var e = J.E.OutCubic(J.Clamp(env.Lt / 0.8));
            var rr = r0 * e * (1 + 0.06 * J.E.InCubic(env.POut));
            if (rr < 2) return;
            var sq = p.Corner;
            var s = r0 / 3.2;
            var h3 = s * Math.Sqrt(3) / 2;
            var rot = (p.R * 60 + env.Ltb * 2.5) * J.DEG;
            var c = Math.Cos(rot);
            var sn = Math.Sin(rot);
            double[] T(double x, double y) => new[] { cx + x * c - y * sn, cy + x * sn + y * c };
            var col = p.Accent ? Acc(env) : Col(env, "sub");
            var al = (Dark(env) ? 0.3 : 0.34) * o;
            var lw = Math.Max(1, 1.1 * u);
            ctx.Save();
            using (var clipPath = new SKPath())
            {
                if (sq)
                {
                    var rct = Math.Max(0, Math.Min(6 * u, rr));
                    clipPath.AddRoundRect(new SKRect((float)(cx - rr), (float)(cy - rr), (float)(cx + rr), (float)(cy + rr)), (float)rct, (float)rct);
                }
                else clipPath.AddCircle((float)cx, (float)cy, (float)rr);
                ctx.ClipPath(clipPath, antialias: true);
            }
            var n = (int)Math.Ceiling(r0 * 1.5 / s) + 1;
            var grid = new List<double[]>();
            var star = new List<double[]>();
            var bloom = J.E.OutCubic(J.Clamp((env.Lt - 0.15) / 0.8));
            for (var j = -n; j <= n; j++)
                for (var i = -n; i <= n; i++)
                {
                    var px = i * s + j * s / 2;
                    var py = j * h3;
                    if (Math.Sqrt(px * px + py * py) > r0 * 1.45) continue;
                    var a = T(px, py);
                    var b = T(px + s, py);
                    var cpt = T(px + s / 2, py + h3);
                    var d = T(px + s * 1.5, py + h3);
                    grid.Add(new[] { a[0], a[1], b[0], b[1] });
                    grid.Add(new[] { a[0], a[1], cpt[0], cpt[1] });
                    grid.Add(new[] { b[0], b[1], cpt[0], cpt[1] });
                    double[][][] tris = { new[] { a, b, cpt }, new[] { b, d, cpt } };
                    foreach (var tri in tris)
                    {
                        var g = new[] { (tri[0][0] + tri[1][0] + tri[2][0]) / 3, (tri[0][1] + tri[1][1] + tri[2][1]) / 3 };
                        foreach (var v in tri)
                            star.Add(new[] { g[0], g[1], J.Lerp(g[0], v[0], bloom), J.Lerp(g[1], v[1], bloom) });
                    }
                }
            Segs(env, grid, col, lw, al * 0.8);
            if (bloom > 0) Segs(env, star, col, lw, al);
            ctx.Restore();
            var e2 = J.E.InOutCubic(J.Clamp(env.Lt / 0.9));
            if (sq)
                Stroke(env, Part(new[] { new[] { cx - rr, cy - rr }, new[] { cx + rr, cy - rr }, new[] { cx + rr, cy + rr }, new[] { cx - rr, cy + rr }, new[] { cx - rr, cy - rr } }, 0, e2),
                    col, lw * 1.3, al * 1.6);
            else Stroke(env, ArcP(cx, cy, rr, -90, -90 + 360 * e2, 96), col, lw * 1.3, al * 1.6);
            var r2 = rr + 7 * u;
            if (!sq) Stroke(env, ArcP(cx, cy, r2, 90, 90 + 300 * e2, 90), col, Math.Max(1, 0.8 * u), al * 0.9);
        },
    });

    private static void RegisterHanabi() => Reg0("hanabi", new DecorDef
    {
        Name = "花火",
        Tags = new[] { "emotional", "pop", "calm" },
        W = 0.8,
        Layer = "front",
        Draw = (env, p) => WithSettle(env, p, (env2, p2) =>
        {
            if (env2.Pass != "main") return;
            var bb = GetBB(env2, env2.Body);
            var sc = env2.Sc!;
            var u = U(env2);
            var o = OutE(env2);
            if (o <= 0.003 || env2.Lt < 0) return;
            var fr = FreeRects(env2, bb, 26 * u);
            if (fr.Count == 0) return;
            var nb = 2 + p2.N % 2;
            const double t = 2.2;
            var style = p2.V % 3;
            string[] cols = { Acc(env2), Acc2(env2), Col(env2, "fg") ?? "#FFFFFF" };
            var pad = 8 * u;
            var soft = 30 * u;
            for (var k = 0; k < nb; k++)
            {
                var all = env2.Ltb - 0.02 - k * 0.6;
                if (all < 0) continue;
                var cyc = (int)Math.Floor(all / t);
                var tau = all - cyc * t;
                double Rr(int i) => J.R(p2.Seed, k, cyc, i);
                var reg = fr[(k + cyc) % Math.Min(2, fr.Count)];
                var r0 = J.Clamp(Math.Min(reg.W * 0.5, reg.H) * 0.5, 44 * u, 190 * u) * (0.75 + 0.3 * Rr(3));
                var x = reg.X + r0 * 0.9 + Math.Max(0, reg.W - r0 * 1.8) * Rr(1);
                var y = reg.Y + r0 * 0.8 + Math.Max(0, reg.H - r0 * 1.7) * Rr(2);
                var c1 = cols[(k + cyc) % 3];
                var c2 = cols[(k + cyc + 1) % 3];
                const double rise = 0.3;
                if (tau < rise)
                {
                    var q = J.E.OutCubic(tau / rise);
                    var yy = y + r0 * 1.1 * (1 - q);
                    var al = ClearOf(bb, x, yy, pad, soft);
                    Segs(env2, new[] { new[] { x, yy, x, yy + 34 * u * (1 - q * 0.7) } }, c1, 1.4 * u, 0.7 * o * al, false, SKStrokeCap.Round);
                    Dots(env2, new[] { new[] { x, yy, 2.2 * u } }, c1, o * al);
                    continue;
                }
                var tb = tau - rise;
                var life = style == 2 ? 1.75 : 1.4;
                if (tb > life) continue;
                var fade = 1 - J.Clamp((tb - life * 0.4) / (life * 0.6));
                var g = r0 * (style == 2 ? 0.75 : 0.28);
                if (tb < 0.16)
                    Dots(env2, new[] { new[] { x, y, r0 * 0.16 * (1 - tb / 0.16) + 2 * u } }, Col(env2, "fg"), 0.6 * o * ClearOf(bb, x, y, pad, soft));
                var rings = new[] { (style == 1 ? 32 : 28, 1.0, c1, 1.4 * u), (style == 2 ? 0 : 16, 0.52, c2, 1.2 * u) };
                foreach (var (m, rk, col, lw) in rings)
                {
                    if (m == 0) continue;
                    var segL = new List<double[]>();
                    var segT = new List<double[]>();
                    var hd = new List<double[]>();
                    for (var i = 0; i < m; i++)
                    {
                        var th = (i + J.R(p2.Seed, k, cyc, i, (int)(rk * 10)) * 0.35) / (double)m * J.TAU + rk;
                        var sp = rk * (0.86 + 0.18 * J.R(p2.Seed, k, cyc, i, 3));
                        Func<double, double[]> pos;
                        if (style == 2)
                            pos = tt => new[] { x + Math.Cos(th) * (r0 * sp * (1 - Math.Exp(-tt * 2.6))), y + Math.Sin(th) * (r0 * sp * (1 - Math.Exp(-tt * 2.6))) + g * 0.55 * tt * tt };
                        else
                            pos = tt => new[] { x + Math.Cos(th) * (r0 * sp * J.E.OutCubic(J.Clamp(tt / 0.85))), y + Math.Sin(th) * (r0 * sp * J.E.OutCubic(J.Clamp(tt / 0.85))) + g * tt * tt };
                        var ptx = pos(tb);
                        var al = ClearOf(bb, ptx[0], ptx[1], pad, soft);
                        if (al <= 0.3) continue;
                        var tw = tb > life * 0.5 && J.R(p2.Seed, k, i, unchecked((int)env2.Step)) < 0.4 ? 0.2 : 1;
                        if (style == 1)
                        {
                            hd.Add(new[] { ptx[0], ptx[1], 2.6 * u * (0.5 + 0.5 * fade) * tw });
                            var q = pos(Math.Max(0, tb - 0.05));
                            segL.Add(new[] { q[0], q[1], ptx[0], ptx[1] });
                        }
                        else if (style == 2)
                        {
                            var q = new List<double[]>();
                            for (var j = 0; j <= 7; j++) q.Add(pos(Math.Max(0, tb - 0.9 + j * 0.9 / 7.0)));
                            for (var j = 3; j <= 7; j++) segL.Add(new[] { q[j - 1][0], q[j - 1][1], q[j][0], q[j][1] });
                            for (var j = 1; j < 3; j++) segT.Add(new[] { q[j - 1][0], q[j - 1][1], q[j][0], q[j][1] });
                            hd.Add(new[] { ptx[0], ptx[1], 1.6 * u * tw });
                        }
                        else
                        {
                            var q = pos(Math.Max(0, tb - 0.2));
                            segL.Add(new[] { q[0], q[1], ptx[0], ptx[1] });
                            hd.Add(new[] { ptx[0], ptx[1], 2 * u * tw });
                        }
                    }
                    Segs(env2, segT, col, lw, 0.3 * o * fade, false, SKStrokeCap.Round);
                    Segs(env2, segL, col, lw, (style == 1 ? 0.35 : 0.8) * o * fade, false, SKStrokeCap.Round);
                    Dots(env2, hd, col, o * fade);
                }
            }
        }),
    });

    private static void RegisterChochin() => Reg0("chochin", new DecorDef
    {
        Name = "提灯",
        Tags = new[] { "emotional", "pop", "calm" },
        W = 0.7,
        Layer = "front",
        Draw = (env, p) => WithSettle(env, p, (env2, p2) =>
        {
            if (env2.Pass != "main") return;
            var bb = GetBB(env2, env2.Body);
            var w2 = env2.W;
            var h2 = env2.H;
            var sc = env2.Sc!;
            var ctx = env2.Ctx;
            var u = U(env2);
            var m = MG(env2);
            var o = OutE(env2);
            if (o <= 0.003 || env2.Lt < 0) return;
            var lw0 = J.Clamp(Math.Min(w2, h2) * 0.066, 42 * u, 86 * u);
            var lh = lw0 * 1.32;
            var gap = 24 * u;
            var k2 = Math.Max(5, (int)Math.Floor((w2 - 2 * m) / (lw0 * 1.9)));
            var cands = new List<(double X, double Room, int I)>();
            for (var i = 0; i < k2; i++)
            {
                var x = m + lw0 * 0.7 + (w2 - 2 * m - lw0 * 1.4) * i / (double)Math.Max(1, k2 - 1);
                var over = x + lw0 * 0.75 > bb.X0 - gap && x - lw0 * 0.75 < bb.X1 + gap;
                var floor = over ? bb.Y0 - gap : h2 * 0.62;
                var room = floor - lh * 1.3 - 10 * u;
                if (room >= 16 * u) cands.Add((x, room, i));
            }
            var want = Math.Min(k2, 3 + p2.N % 3);
            var pick = new List<(double X, double Room, int I)>();
            for (var j = 0; j < want; j++)
            {
                var ideal = Math.Round((j + 0.5) * k2 / (double)want - 0.5 + J.Rs(p2.Seed, j, 4) * 0.4, MidpointRounding.AwayFromZero);
                var c = cands.Where(cc => pick.All(pp => Math.Abs(pp.X - cc.X) > lw0 * 1.6))
                    .OrderBy(cc => Math.Abs(cc.I - ideal)).FirstOrDefault();
                if (c != default && Math.Abs(c.I - ideal) <= Math.Max(1, k2 / (double)want / 2)) pick.Add(c);
            }
            pick.Sort((a, b) => a.X.CompareTo(b.X));
            var body = Acc(env2);
            var rib = J.Mix(body, Col(env2, "bg") ?? "#000000", 0.45);
            var capC = Dark(env2) ? Col(env2, "fg") : ColOr(env2, "ink", "fg");
            var txtC = J.Lum(body) > 0.55 ? "#000000" : Col(env2, "bg");
            for (var j = 0; j < pick.Count; j++)
            {
                var pcur = pick[j];
                var q = J.Clamp((env2.Lt - 0.03 - j * 0.08) / 0.6);
                if (q <= 0) continue;
                var lf = Math.Min(pcur.Room, pcur.Room * (0.25 + 0.6 * J.R(p2.Seed, pcur.I, 2)) + 20 * u);
                var drop = J.E.OutBack(q, 1.4);
                var l = J.Lerp(-lh * 1.6, lf, drop);
                var amp = (2.5 + 8 * Math.Exp(-env2.Lt * 1.5)) * (J.R(p2.Seed, j, 3) < 0.5 ? 1 : -1);
                var ang = amp * Math.Sin(env2.Ltb * (1.7 + j * 0.23) + j * 1.3) * J.DEG;
                ctx.Save();
                ctx.Translate((float)pcur.X, 0);
                ctx.RotateDegrees((float)(ang / J.DEG));
                var top = l;
                var cy = top + lh / 2;
                if (top > 0) Segs(env2, new[] { new[] { 0.0, 0.0, 0.0, top } }, Col(env2, "sub"), Math.Max(1, u), 0.8 * o);
                if (Dark(env2))
                {
                    Dots(env2, new[] { new[] { 0.0, cy, lw0 * 1.05 } }, body, 0.06 * o);
                    Dots(env2, new[] { new[] { 0.0, cy, lw0 * 0.78 } }, body, 0.08 * o);
                }
                var shape = new List<double[]>();
                for (var i = 0; i <= 32; i++)
                {
                    var t = i / 32.0 * J.TAU;
                    var cs = Math.Cos(t);
                    var sn = Math.Sin(t);
                    shape.Add(new[] { Math.Sign(cs) * Math.Pow(Math.Abs(cs), 0.8) * lw0 / 2, Math.Sign(sn) * Math.Pow(Math.Abs(sn), 0.9) * lh * 0.42 + cy });
                }
                Polys(env2, new[] { shape.ToArray() }, body, 0.94 * o);
                var rl = new List<double[]>();
                for (var k = 1; k < 8; k++)
                {
                    var yy = -lh * 0.42 + lh * 0.84 * k / 8.0;
                    var sn = Math.Pow(Math.Abs(yy) / (lh * 0.42), 1 / 0.9);
                    var f = Math.Pow(Math.Sqrt(Math.Max(0, 1 - sn * sn)), 0.8) * lw0 / 2 - 1.5 * u;
                    if (f > 2 * u) rl.Add(new[] { -f, cy + yy, f, cy + yy });
                }
                Segs(env2, rl, rib, Math.Max(1, 1.1 * u), 0.7 * o);
                Rects(env2, new[] { new[] { -lw0 * 0.27, cy - lh * 0.5, lw0 * 0.54, lh * 0.1 }, new[] { -lw0 * 0.27, cy + lh * 0.4, lw0 * 0.54, lh * 0.1 } }, capC, o);
                var ch = LyricChar(env2, pcur.I * 7 + 1);
                if (ch.Length > 0)
                    env2.DrawItem(new JObject
                    {
                        ["text"] = ch,
                        ["font"] = SerifF(env2) == "mincho_light" ? "mincho_bold" : SerifF(env2),
                        ["size"] = lw0 * 0.5,
                        ["x"] = 0,
                        ["y"] = cy + lw0 * 0.02,
                        ["color"] = txtC,
                        ["alpha"] = o * J.Clamp((q - 0.35) / 0.4),
                        ["ghost"] = false,
                    });
                var ts = new List<double[]>();
                for (var k = -2; k <= 2; k++) ts.Add(new[] { k * 2.4 * u, cy + lh * 0.5, k * 3.2 * u, cy + lh * 0.5 + lh * 0.24 });
                Segs(env2, ts, body, Math.Max(1, u), 0.85 * o);
                ctx.Restore();
            }
        }),
    });

    private static string? ColOr(Env env, string a, string b)
    {
        var v = Col(env, a);
        return !string.IsNullOrEmpty(v) ? v : Col(env, b);
    }

    private static void RegisterShimenawa() => Reg0("shimenawa", new DecorDef
    {
        Name = "注連縄",
        Tags = new[] { "editorial", "emotional", "calm" },
        W = 0.6,
        Layer = "front",
        Draw = (env, p) => WithSettle(env, p, (env2, p2) =>
        {
            if (env2.Pass != "main") return;
            var bb = GetBB(env2, env2.Body);
            var w2 = env2.W;
            var h2 = env2.H;
            var sc = env2.Sc!;
            var ctx = env2.Ctx;
            var u = U(env2);
            var m = MG(env2);
            var o = OutE(env2);
            if (o <= 0.003 || env2.Lt < 0) return;
            var gap = 30 * u;
            var need0 = 210 * u;
            var topRoom = bb.Y0 - gap - m * 0.3;
            var botRoom = h2 - m * 0.4 - bb.Y1 - gap;
            var top = p2.Low ? !(botRoom >= need0 * 0.7 || botRoom > topRoom) : (topRoom >= need0 * 0.7 || topRoom >= botRoom);
            var room = top ? topRoom : botRoom;
            if (room < 60 * u) return;
            var k = J.Clamp(room / need0, 0.4, 1.15);
            var t0 = 17 * u * k;
            var sag = 46 * u * k;
            var shL = 112 * u * k;
            var shW = shL * 0.17;
            var ya = top ? m * 0.3 + t0 * 0.6 : bb.Y1 + gap + t0 * 0.6;
            double Y(double x) => ya + sag * 4 * (x / w2) * (1 - x / w2);
            double TK(double x) => t0 * (0.45 + 0.55 * Math.Sin(Math.PI * J.Clamp(x / w2)));
            var e = J.E.InOutCubic(J.Clamp(env2.Lt / 0.8));
            var half = (w2 / 2.0 + 12) * e;
            var xa = w2 / 2.0 - half;
            var xb = w2 / 2.0 + half;
            var col = Dark(env2) ? Col(env2, "fg") : ColOr(env2, "ink", "fg");
            var lw = Math.Max(1, 1.1 * u);
            var step = t0 * 1.05;
            var ph = env2.Ltb * 5 * u % step;
            var twist = new List<double[][]>();
            for (var x = -2 * step + ph; x < w2 + step; x += step)
            {
                if (x < xa - step * 0.5 || x > xb) continue;
                var xs = Math.Max(xa, x);
                var xe = Math.Min(xb, x + step * 0.82);
                var sl = TK(x) * 0.7;
                if (xe - xs < 1) continue;
                twist.Add(new[] { new[] { xs, Y(xs) - TK(xs) / 2 }, new[] { xe, Y(xe) - TK(xe) / 2 }, new[] { xe + sl, Y(xe) + TK(xe) / 2 }, new[] { xs + sl, Y(xs) + TK(xs) / 2 } });
            }
            Polys(env2, twist, col, 0.82 * o);
            var tp = new List<double[]>();
            var bt = new List<double[]>();
            for (var x = xa; x <= xb + 0.1; x += 8 * u)
            {
                tp.Add(new[] { x, Y(x) - TK(x) / 2 - 1.5 * u });
                bt.Add(new[] { x, Y(x) + TK(x) / 2 + 1.5 * u });
            }
            Strokes(env2, new[] { tp.ToArray(), bt.ToArray() }, col, lw, 0.45 * o);
            var ns = 3 + (p2.N % 2 != 0 ? 1 : 0);
            var paperC = Dark(env2) ? Col(env2, "fg") : Col(env2, "bg");
            for (var i = 0; i < ns; i++)
            {
                var x = w2 * (i + 1) / (double)(ns + 1);
                var ex = Math.Abs(x - w2 / 2) / (w2 / 2.0 + 12);
                if (e < ex) continue;
                var qy = J.E.OutBack(J.Clamp((env2.Lt - 0.2 - ex * 0.5) / 0.45), 1.6);
                if (qy <= 0) continue;
                var y = Y(x) + TK(x) / 2;
                var sw = Math.Sin(env2.Ltb * 1.4 + i * 1.7) * 3 * J.DEG;
                ctx.Save();
                ctx.Translate((float)x, (float)y);
                ctx.RotateDegrees((float)(sw / J.DEG));
                ctx.Scale(1, (float)qy);
                var h = shL / 4;
                var rs = new List<double[][]>();
                for (var s = 0; s < 4; s++)
                {
                    var dx = (s % 2 != 0 ? shW * 0.62 : 0) - shW / 2;
                    var sk = (s % 2 != 0 ? -1 : 1) * shW * 0.18;
                    rs.Add(new[] { new[] { dx, s * h }, new[] { dx + shW, s * h + sk * 0.3 }, new[] { dx + shW + sk, (s + 1) * h + 0.5 }, new[] { dx + sk, (s + 1) * h + 0.5 - sk * 0.3 } });
                }
                Polys(env2, rs, paperC, 0.94 * o);
                if (!Dark(env2)) Strokes(env2, rs, Col(env2, "fg"), Math.Max(1, u), 0.85 * o, false, close: true);
                ctx.Restore();
                if (i < ns - 1)
                {
                    var tx = w2 * (i + 1.5) / (double)(ns + 1);
                    var ty = Y(tx) + TK(tx) / 2;
                    var tl = shL * 0.36 * J.E.OutCubic(J.Clamp((env2.Lt - 0.4) / 0.4));
                    var t = new List<double[]>();
                    for (var s = -4; s <= 4; s++)
                        t.Add(new[] { tx + s * 1.4 * u, ty, tx + s * 2 * u + Math.Sin(env2.Ltb * 1.2 + i) * 2 * u, ty + tl * (1 - Math.Abs(s) * 0.05) });
                    if (tl > 1) Segs(env2, t, Col(env2, "sub"), Math.Max(1, u), 0.85 * o);
                }
            }
        }),
    });

    private static void RegisterSensu() => Reg0("sensu", new DecorDef
    {
        Name = "扇",
        Tags = new[] { "emotional", "editorial", "calm" },
        W = 0.8,
        Layer = "front",
        Draw = (env, p) => WithSettle(env, p, (env2, p2) =>
        {
            if (env2.Pass != "main") return;
            var bb = GetBB(env2, env2.Body);
            var sc = env2.Sc!;
            var u = U(env2);
            var o = OutE(env2);
            if (o <= 0.003 || env2.Lt < 0) return;
            var r0 = J.Clamp(Math.Min(env2.W, env2.H) * 0.105, 62 * u, 124 * u);
            var rIn = r0 * 0.36;
            var sp = SpotP(env2, bb, r0 * 2.05, r0 * 1.2, p2, 22 * u);
            var a = o * (sp.Ok ? 1 : 0.35);
            var tilt = (p2.Right ? 1 : -1) * (5 + p2.R * 9);
            var px = sp.Cx;
            var py = sp.Y + r0 * 1.12;
            var open = J.E.OutBack(J.Clamp((env2.Lt - 0.05) / 0.65), 1.1) * (1 - 0.9 * J.E.InCubic(env2.POut));
            var spread = 12 + 140 * open;
            const int n = 14;
            var v = p2.V % 3;
            double Ang(double i) => (-90 + tilt - spread / 2 + spread * i / n) * J.DEG;
            var outer = new List<double[]>();
            var inner = new List<double[]>();
            var ribs = new List<double[]>();
            var folds = new List<double[]>();
            for (var i = 0; i <= n; i++)
            {
                var an = Ang(i);
                var c = Math.Cos(an);
                var s = Math.Sin(an);
                var rr = r0 * (i % 2 != 0 ? 0.965 : 1);
                outer.Add(new[] { px + c * rr, py + s * rr });
                inner.Add(new[] { px + c * rIn, py + s * rIn });
                ribs.Add(new[] { px + c * r0 * 0.06, py + s * r0 * 0.06, px + c * rIn, py + s * rIn });
                if (i > 0 && i < n) folds.Add(new[] { px + c * rIn, py + s * rIn, px + c * rr, py + s * rr });
            }
            var leaf = outer.Concat(inner.AsEnumerable().Reverse()).ToList();
            var ac = Acc(env2);
            Polys(env2, new[] { leaf.ToArray() }, ac, 0.13 * a);
            if (v == 1 && open > 0.6)
            {
                var an = (-90 + tilt) * J.DEG;
                var rc = (rIn + r0) / 2;
                Dots(env2, new[] { new[] { px + Math.Cos(an) * rc, py + Math.Sin(an) * rc, (r0 - rIn) * 0.28 * J.Clamp((open - 0.6) / 0.4) } }, ac, 0.9 * a);
            }
            else if (v == 2)
            {
                foreach (var f in new[] { 0.55, 0.72 })
                {
                    var pts = new List<double[]>();
                    for (var i = 0; i <= 40; i++)
                    {
                        var an = Ang(i * n / 40.0);
                        var rr = J.Lerp(rIn, r0, f) + Math.Sin(i / 40.0 * J.TAU * 2 + env2.Ltb) * 3 * u;
                        pts.Add(new[] { px + Math.Cos(an) * rr, py + Math.Sin(an) * rr });
                    }
                    Stroke(env2, pts, ac, 2 * u, 0.8 * a);
                }
            }
            Segs(env2, folds, Col(env2, "fg"), Math.Max(1, 0.8 * u), 0.45 * a);
            Segs(env2, ribs, Col(env2, "fg"), Math.Max(1, 1.1 * u), 0.8 * a);
            Stroke(env2, outer, Col(env2, "fg"), Math.Max(1, 1.3 * u), 0.95 * a, false, join: SKStrokeJoin.Round);
            Stroke(env2, inner, Col(env2, "fg"), Math.Max(1, 1.1 * u), 0.8 * a);
            Segs(env2, new[] { new[] { px, py, outer[0][0], outer[0][1] }, new[] { px, py, outer[n][0], outer[n][1] } }, Col(env2, "fg"), Math.Max(1, 1.6 * u), a);
            env2.Circle(px, py, 4 * u, null, Col(env2, "fg"), Math.Max(1, u), a, false);
            env2.Circle(px, py, 1.6 * u, ac, null, 0, a, false);
        }),
    });

    /// <summary>风格化云轮廓：起伏的顶、平底、左侧的涡卷（JS kumo）。</summary>
    private static (double[][] Outline, double[][] Curl) Kumo(double x0, double yb, double l, double hc)
    {
        double[][] bumps = { new[] { 0.22, 0.5 }, new[] { 0.48, 0.78 }, new[] { 0.74, 0.55 }, new[] { 0.9, 0.32 } };
        var top = new List<double[]>();
        for (var i = 0; i <= 44; i++)
        {
            var x = x0 + l * i / 44.0;
            var y = yb;
            foreach (var bpt in bumps)
            {
                var dx = x - (x0 + l * bpt[0]);
                var rr = bpt[1] * hc;
                if (Math.Abs(dx) < rr) y = Math.Min(y, yb - Math.Sqrt(rr * rr - dx * dx));
            }
            top.Add(new[] { x, y });
        }
        var curl = new List<double[]>();
        var c = new[] { x0 - hc * 0.05, yb - hc * 0.3 };
        for (var i = 0; i <= 30; i++)
        {
            var t = i / 30.0;
            var an = (90 + t * 400) * J.DEG;
            var r = hc * 0.3 * (1 - t * 0.78);
            curl.Add(new[] { c[0] - Math.Cos(an) * r * 1.1, c[1] + Math.Sin(an) * r });
        }
        top.Add(new[] { x0 + l, yb });
        top.Add(new[] { x0, yb });
        return (top.ToArray(), curl.ToArray());
    }

    private static void RegisterTsukiKumo() => Reg0("tsukiKumo", new DecorDef
    {
        Name = "月に雲",
        Tags = new[] { "emotional", "calm", "editorial" },
        W = 0.8,
        Layer = "front",
        Draw = (env, p) => WithSettle(env, p, (env2, p2) =>
        {
            if (env2.Pass != "main") return;
            var bb = GetBB(env2, env2.Body);
            var sc = env2.Sc!;
            var u = U(env2);
            var o = OutE(env2);
            if (o <= 0.003 || env2.Lt < 0) return;
            var rm = J.Clamp(Math.Min(env2.W, env2.H) * 0.064, 40 * u, 84 * u);
            var bw0 = rm * 4.6;
            var bh0 = rm * 2.9;
            var sp = SpotP(env2, bb, bw0, bh0, p2, 24 * u);
            var a = o * (sp.Ok ? 1 : 0.35);
            var flip = sp.Cx < env2.W / 2 ? -1 : 1;
            var mx = sp.Cx + flip * rm * 0.9;
            var my = sp.Y + rm * 1.3;
            var e = J.E.OutCubic(J.Clamp(env2.Lt / 0.6));
            var moonC = Dark(env2) ? Col(env2, "fg") : Acc(env2);
            var cres = p2.V % 2 == 1;
            var rr = rm * (0.85 + 0.15 * e);
            if (cres)
            {
                var pts = ArcP(mx, my, rr, 60, 300, 40).ToList();
                const double k = 0.62;
                var inner = new List<double[]>();
                for (var i = 0; i <= 40; i++)
                {
                    var an = (300 - 240 * i / 40.0) * J.DEG;
                    inner.Add(new[] { mx + rr * 0.42 + Math.Cos(an) * rr * k * 1.12, my + Math.Sin(an) * rr * 0.93 });
                }
                Polys(env2, new[] { pts.Concat(inner).ToArray() }, moonC, 0.95 * a * e);
            }
            else Dots(env2, new[] { new[] { mx, my, rr } }, moonC, 0.95 * a * e);
            var e2 = J.E.InOutCubic(J.Clamp((env2.Lt - 0.1) / 0.7));
            Stroke(env2, ArcP(mx, my, rm * 1.28, -90, -90 + 360 * e2, 80), Col(env2, "sub"), Math.Max(1, u), 0.5 * a);
            Stroke(env2, ArcP(mx, my, rm * 1.5, 90, 90 + 200 * e2, 60), Col(env2, "sub"), Math.Max(1, 0.8 * u), 0.3 * a);
            var l = rm * 3.3;
            var hc = rm * 0.7;
            var drift = Math.Sin(env2.Ltb * 0.5 + p2.R * 6) * rm * 0.35 - flip * (1 - e) * rm * 1.4;
            var x0 = mx - l * 0.55 + drift;
            var yb = my + rm * 0.62;
            var cl = Kumo(x0, yb, l, hc);
            var ce = J.E.InOutCubic(J.Clamp((env2.Lt - 0.2) / 0.7));
            Polys(env2, new[] { cl.Outline }, Col(env2, "bg"), a * J.Clamp(ce * 2));
            Stroke(env2, Part(cl.Outline.Take(cl.Outline.Length - 1).ToArray(), 0, ce), Col(env2, "fg"), Math.Max(1, 1.4 * u), 0.9 * a, false, join: SKStrokeJoin.Round);
            Stroke(env2, Part(cl.Curl, 0, ce), Col(env2, "fg"), Math.Max(1, 1.4 * u), 0.9 * a, false, cap: SKStrokeCap.Round, join: SKStrokeJoin.Round);
            var inner2 = cl.Outline.Skip(6).Take(34).Select(q => new[] { q[0], q[1] + hc * 0.28 }).ToArray();
            Stroke(env2, Part(inner2, 0, ce), Col(env2, "sub"), Math.Max(1, u), 0.5 * a);
            var ce2 = J.E.InOutCubic(J.Clamp((env2.Lt - 0.35) / 0.7));
            var l2 = l * 0.55;
            var x2 = mx + flip * rm * 0.2 - l2 / 2 - drift * 0.6;
            var y2 = my - rm * 0.9;
            var c2 = Kumo(x2, y2, l2, hc * 0.55);
            Stroke(env2, Part(c2.Outline.Take(c2.Outline.Length - 1).ToArray(), 0, ce2), Col(env2, "sub"), Math.Max(1, 1.1 * u), 0.7 * a, false, join: SKStrokeJoin.Round);
            Stroke(env2, Part(c2.Curl, 0, ce2), Col(env2, "sub"), Math.Max(1, 1.1 * u), 0.7 * a, false, cap: SKStrokeCap.Round);
        }),
    });

    // 红叶形状（JS MAPLE IIFE）
    private static readonly double[][] Maple = BuildMaple();

    private static double[][] BuildMaple()
    {
        double[][] lobes = { new[] { -125, 0.36 }, new[] { -82, 0.66 }, new[] { -42, 0.9 }, new[] { 0, 1.0 }, new[] { 42, 0.9 }, new[] { 82, 0.66 }, new[] { 125, 0.36 } };
        var pts = new List<double[]>();
        double[] P2(double a, double r) => new[] { Math.Sin(a * J.DEG) * r, -Math.Cos(a * J.DEG) * r };
        pts.Add(P2(180, 0.1));
        for (var i = 0; i < lobes.Length; i++)
        {
            var a = lobes[i][0];
            var l = lobes[i][1];
            pts.Add(P2(a - 17, l * 0.6));
            pts.Add(P2(a - 11, l * 0.64));
            pts.Add(P2(a - 6, l * 0.84));
            pts.Add(P2(a, l));
            pts.Add(P2(a + 6, l * 0.84));
            pts.Add(P2(a + 11, l * 0.64));
            pts.Add(P2(a + 17, l * 0.6));
            var nx = i < lobes.Length - 1 ? lobes[i + 1][0] : 180;
            pts.Add(P2((a + nx) / 2, i < lobes.Length - 1 ? 0.3 : 0.1));
        }
        return pts.ToArray();
    }

    private static void RegisterMomiji() => Reg0("momiji", new DecorDef
    {
        Name = "紅葉",
        Tags = new[] { "emotional", "calm" },
        W = 0.8,
        Layer = "front",
        Draw = (env, p) =>
        {
            if (env.Pass != "main") return;
            var bb = GetBB(env, env.Body);
            var w2 = env.W;
            var h2 = env.H;
            var sc = env.Sc!;
            var u = U(env);
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0) return;
            var n = 4 + p.N + (p.Big ? 1 : 0);
            var ac = Acc(env);
            var cols = new[] { ac, J.Mix(ac, Col(env, "fg") ?? "#FFFFFF", 0.3), J.Mix(ac, Col(env, "bg") ?? "#000000", 0.3) };
            var vein = Col(env, "bg");
            for (var i = 0; i < n; i++)
            {
                double R(int k) => J.R(p.Seed, i, k);
                var q = J.E.OutCubic(J.Clamp((env.Lt - R(1) * 0.35) / 0.45));
                if (q <= 0) continue;
                var l = (40 + R(2) * 26) * u * (i == 0 ? 1.35 : 1);
                var v = (38 + R(3) * 30) * u;
                var om = 1.3 + R(4) * 0.9;
                var ph = R(5) * J.TAU;
                var span = h2 + l * 4;
                var y = Wrap(R(6) * span + v * env.Ltb, -l * 2, span);
                var x0 = w2 * (i + 0.5) / n + J.Rs(p.Seed, i, 7) * w2 * 0.4 / n;
                var a0 = (26 + R(8) * 34) * u;
                var s = Math.Sin(env.Ltb * om + ph);
                var x = x0 + a0 * s;
                var yy = y - Math.Abs(Math.Cos(env.Ltb * om + ph)) * a0 * 0.25;
                var al = ClearOf(bb, x, yy, 12 * u + l * 0.5, 30 * u);
                if (al <= 0.02) continue;
                var rot = (J.Rs(p.Seed, i, 9) * 40 + s * 38) * J.DEG;
                var fx = 0.55 + 0.45 * Math.Abs(Math.Cos(env.Ltb * om * 0.5 + ph));
                var c = Math.Cos(rot);
                var sn = Math.Sin(rot);
                double[] T(double[] pt)
                {
                    var xv = pt[0] * l * fx * q;
                    var yv = pt[1] * l * q;
                    return new[] { x + xv * c - yv * sn, yy + xv * sn + yv * c };
                }
                var col = cols[i % 3];
                var a = 0.92 * o * al;
                Polys(env, new[] { Maple.Select(T).ToArray() }, col, a);
                var vs = new List<double[]>();
                foreach (var (ang, lv) in new[] { (0.0, 0.82), (-42.0, 0.7), (42.0, 0.7), (-82.0, 0.5), (82.0, 0.5) })
                {
                    var pp0 = T(new[] { 0.0, 0.0 });
                    var pp1 = T(new[] { Math.Sin(ang * J.DEG) * lv, -Math.Cos(ang * J.DEG) * lv });
                    vs.Add(new[] { pp0[0], pp0[1], pp1[0], pp1[1] });
                }
                Segs(env, vs, vein, Math.Max(1, 0.9 * u), 0.45 * a);
                var s0 = T(new[] { 0.0, 0.05 });
                var s1 = T(new[] { 0.06, 0.42 });
                Segs(env, new[] { new[] { s0[0], s0[1], s1[0], s1[1] } }, col, Math.Max(1, 1.4 * u), a, false, SKStrokeCap.Round);
            }
        },
    });

    /// <summary>波浪头的轮廓/曲面/内线（JS waveCrest）。</summary>
    private static (double[][] Crest, double[][] Face, List<double[][]> Inner, double[] Top) WaveCrest(
        double x, double yb, double w, double h)
    {
        var back = Bez(new[] { x - w / 2, yb }, new[] { x - w * 0.18, yb }, new[] { x - w * 0.28, yb - h }, new[] { x, yb - h }, 16);
        var r0 = h * 0.4;
        var cx = x;
        var cy = yb - h + r0;
        var curl = new List<double[]>();
        for (var i = 1; i <= 30; i++)
        {
            var deg = i / 30.0 * 430;
            var an = (-90 + deg) * J.DEG;
            var rr = r0 * (deg < 90 ? 1 : 1 - 0.8 * (deg - 90) / 340.0);
            curl.Add(new[] { cx + Math.Cos(an) * rr, cy + Math.Sin(an) * rr });
        }
        var face = Bez(new[] { cx + r0, cy }, new[] { cx + r0 * 1.05, cy + h * 0.35 }, new[] { x + w * 0.3, yb }, new[] { x + w / 2, yb }, 12);
        var inner = new[] { 0.7, 0.44 }.Select(k => Bez(
            new[] { x - w / 2 + w * 0.12 * (1 - k), yb }, new[] { x - w * 0.2, yb },
            new[] { x - w * 0.24, yb - h * k }, new[] { x - w * 0.02, yb - h * k + h * 0.08 }, 12)).ToList();
        return (back.Concat(curl).ToArray(), face, inner, new[] { x + r0 * 0.9, yb - h });
    }

    private static void RegisterNamiGashira() => Reg0("namiGashira", new DecorDef
    {
        Name = "波頭",
        Tags = new[] { "emotional", "editorial", "graphic" },
        W = 0.8,
        Layer = "front",
        Draw = (env, p) => WithSettle(env, p, (env2, p2) =>
        {
            if (env2.Pass != "main") return;
            var bb = GetBB(env2, env2.Body);
            var w2 = env2.W;
            var h2 = env2.H;
            var sc = env2.Sc!;
            var u = U(env2);
            var m = MG(env2);
            var o = OutE(env2);
            if (o <= 0.003 || env2.Lt < 0) return;
            var room = h2 - m * 0.6 - (bb.Y1 + 26 * u);
            if (room < 34 * u) return;
            var h = Math.Min(Math.Min(w2, h2) * 0.085, room * 0.72);
            var w = h * 2.3;
            var e = J.E.OutCubic(J.Clamp(env2.Lt / 0.6));
            var sink = (1 - e) * h * 1.2 + J.E.InCubic(env2.POut) * h * 0.8;
            var yb = h2 - m * 0.6 + sink;
            var dir = p2.Right ? 1 : -1;
            var off = Wrap(env2.Ltb * 16 * u * dir, 0, w);
            var n = (int)Math.Ceiling(w2 / w) + 2;
            var col = Col(env2, "fg");
            var lw = Math.Max(1, 1.5 * u);
            var crest = new List<double[][]>();
            var faces = new List<double[][]>();
            var inner = new List<double[][]>();
            var spray = new List<double[]>();
            var de = J.E.InOutCubic(J.Clamp((env2.Lt - 0.05) / 0.8));
            for (var i = -1; i < n; i++)
            {
                var x = i * w + off - w * 0.5;
                var hh = h * (0.82 + 0.18 * Math.Sin(i * 1.7 + p2.R * 6));
                var bob = Math.Sin(env2.Ltb * 1.6 + i * 0.9) * h * 0.04;
                var wc = WaveCrest(x, yb + bob, w, hh);
                crest.Add(Part(wc.Crest, 0, de));
                faces.Add(Part(wc.Face, 0, de));
                inner.AddRange(wc.Inner.Select(q => Part(q, 0, de)));
                if (de > 0.8)
                    for (var k = 0; k < 4; k++)
                    {
                        var tw = 0.5 + 0.5 * Math.Sin(env2.Ltb * 5 + i * 3 + k * 2);
                        spray.Add(new[] { wc.Top[0] + (k * 0.12 + 0.06) * w * 0.45, wc.Top[1] - (k % 2 != 0 ? 0.1 : 0.2) * hh - k * 1.5 * u, (1.2 + 1.2 * tw) * u });
                    }
            }
            Strokes(env2, crest, col, lw, 0.9 * o, false, cap: SKStrokeCap.Round, join: SKStrokeJoin.Round);
            Strokes(env2, faces, col, lw, 0.9 * o, false, cap: SKStrokeCap.Round);
            Strokes(env2, inner, Col(env2, "sub"), Math.Max(1, u), 0.6 * o, false, cap: SKStrokeCap.Round);
            Dots(env2, spray, Acc(env2), 0.9 * o * J.Clamp((de - 0.8) / 0.2));
            Segs(env2, new[] { new[] { m * 0.5, yb + 3 * u, w2 - m * 0.5, yb + 3 * u } }, Col(env2, "sub"), Math.Max(1, 0.8 * u), 0.5 * o * de);
        }),
    });

    private static void RegisterKasumi() => Reg0("kasumi", new DecorDef
    {
        Name = "霞",
        Tags = new[] { "calm", "emotional", "editorial" },
        W = 0.8,
        Layer = "back",
        Subtle = true,
        Draw = (env, p) =>
        {
            if (env.Pass != "main" || env.Lt < 0) return;
            var w2 = env.W;
            var h2 = env.H;
            var sc = env.Sc!;
            var ctx = env.Ctx;
            var u = U(env);
            var o = OutE(env);
            if (o <= 0.003) return;
            var n = 2 + p.N % 2;
            var hb = J.Clamp(Math.Min(w2, h2) * 0.046, 28 * u, 58 * u);
            var fill = Col(env, "dim");
            var line = Acc(env);
            var dk = Dark(env);
            var fa = (dk ? 0.9 : 0.75) * o;
            for (var k = 0; k < n; k++)
            {
                double R(int i) => J.R(p.Seed, k, i);
                var dir = (k + (p.Right ? 1 : 0)) % 2 != 0 ? 1 : -1;
                var e = J.E.OutCubic(J.Clamp((env.Lt - k * 0.12) / 0.8));
                var lm = w2 * (0.34 + R(1) * 0.22);
                var yc = h2 * ((k + 0.5) / n) + (R(2) - 0.5) * h2 * 0.16;
                var xc = w2 * (0.5 + dir * (0.2 + R(3) * 0.18)) + dir * (1 - e) * w2 * 0.25 + dir * env.Ltb * 7 * u;
                var lu = lm * (0.45 + R(4) * 0.2);
                var xu = xc + (R(5) < 0.5 ? -1 : 1) * lm * 0.22;
                var hu = hb * 0.8;
                var ld = lm * (0.3 + R(6) * 0.2);
                var xd = xc - (xu - xc) * 0.8;
                ctx.Save();
                using (var path = new SKPath())
                {
                    AddRr(path, xc - lm / 2, yc - hb / 2, lm, hb, hb / 2);
                    AddRr(path, xu - lu / 2, yc - hb / 2 - hu + 1, lu, hu + 2, hu / 2);
                    if (R(7) < 0.6) AddRr(path, xd - ld / 2, yc + hb / 2 - 1, ld, hu * 0.85 + 2, hu * 0.42);
                    using var fillP = new SKPaint { IsAntialias = true, Color = Env.SkColor(fill ?? "#000000", Math.Min(1, fa * e)) };
                    ctx.DrawPath(path, fillP);
                }
                ctx.Restore();
                var lw = Math.Max(1, 1.2 * u);
                var la = 0.75 * o * e;
                var tl = new[] { new[] { xu - lu / 2 + hu / 2, yc - hb / 2 - hu + 1 }, new[] { xu + lu / 2 - hu / 2, yc - hb / 2 - hu + 1 } };
                Stroke(env, Part(tl, 0, J.E.InOutCubic(J.Clamp((env.Lt - 0.3 - k * 0.12) / 0.6))), line, lw, la);
                Stroke(env, Part(new[] { new[] { xc + lm / 2 - hb / 2, yc + hb / 2 }, new[] { xc - lm / 2 + hb / 2, yc + hb / 2 } }, 0,
                    J.E.InOutCubic(J.Clamp((env.Lt - 0.4 - k * 0.12) / 0.6))), line, lw, la * 0.7);
            }
        },
    });

    /// <summary>SKPath 上加圆角矩形子路径（JS rrPath；AddRoundRect 的圆角是椭圆弧，与 arcTo 圆角同观感）。</summary>
    private static void AddRr(SKPath path, double x, double y, double w, double h, double r)
    {
        r = Math.Max(0, Math.Min(r, Math.Min(w / 2, h / 2)));
        path.AddRoundRect(new SKRect((float)x, (float)y, (float)(x + w), (float)(y + h)), (float)r, (float)r);
    }

    private static void RegisterHexGrid() => Reg0("hexGrid", new DecorDef
    {
        Name = "六角格子",
        Tags = new[] { "glitch", "graphic" },
        W = 0.9,
        Layer = "front",
        Draw = (env, p) => WithSettle(env, p, (env2, p2) =>
        {
            if (env2.Pass != "main") return;
            var bb = GetBB(env2, env2.Body);
            var sc = env2.Sc!;
            var u = U(env2);
            var o = OutE(env2);
            if (o <= 0.003 || env2.Lt < 0) return;
            var rc = J.Clamp(Math.Min(env2.W, env2.H) * 0.021, 13 * u, 24 * u);
            var cols = 5 + p2.N + (p2.Big ? 1 : 0);
            var rows = 3 + p2.V % 2;
            var dx = rc * 1.5;
            var dy = rc * Math.Sqrt(3);
            var fs = FS(env2) * 0.72;
            var w = (cols - 1) * dx + rc * 2;
            var h = rows * dy + dy / 2 + fs * 1.8;
            var sp = SpotP(env2, bb, w, h, p2, 24 * u);
            var a = o * (sp.Ok ? 1 : 0.35);
            var x0 = sp.X + rc;
            var y0 = sp.Y + fs * 1.8 + dy / 2;
            var seedC = new[] { (int)Math.Floor(p2.R * cols), (int)Math.Floor(J.R(p2.Seed, 2) * rows) };
            var scan = ((env2.Ltb * 0.45 + p2.R) % 1.4) * (w + 40 * u) - 20 * u;
            var outl = new List<double[][]>();
            var hot = new List<double[][]>();
            var lit = new List<double[][]>();
            var litA = new List<double[][]>();
            var nl = 3 + p2.N;
            var tick = (int)Math.Floor(env2.Ltb * 2.5);
            var litSet = new HashSet<int>();
            for (var k = 0; k < nl; k++) litSet.Add((int)(J.H(p2.Seed, tick, k) % (uint)(cols * rows)));
            for (var i = 0; i < cols; i++)
                for (var j = 0; j < rows; j++)
                {
                    var cx = x0 + i * dx;
                    var cy = y0 + j * dy + (i % 2 != 0 ? dy / 2 : 0);
                    var d = Math.Sqrt(Math.Pow(i - seedC[0], 2) + Math.Pow(j - seedC[1], 2));
                    var q = J.E.OutBack(J.Clamp((env2.Lt - d * 0.045) / 0.3), 1.6);
                    if (q <= 0) continue;
                    var rr = rc * 0.88 * q;
                    var hx = new List<double[]>();
                    for (var k = 0; k <= 6; k++)
                    {
                        var an = k * 60 * J.DEG;
                        hx.Add(new[] { cx + Math.Cos(an) * rr, cy + Math.Sin(an) * rr });
                    }
                    var near = Math.Abs(cx - sp.X - scan) < rc * 1.2;
                    (near ? hot : outl).Add(hx.ToArray());
                    if (litSet.Contains(i * rows + j) && env2.Lt > 0.5)
                        (J.R(p2.Seed, i, j, 4) < 0.5 ? lit : litA).Add(
                            hx.Select(q2 => new[] { cx + (q2[0] - cx) * 0.62, cy + (q2[1] - cy) * 0.62 }).ToArray());
                }
            var lw = Math.Max(1, u);
            Strokes(env2, outl, Col(env2, "sub"), lw, 0.6 * a);
            Strokes(env2, hot, Col(env2, "fg"), lw * 1.3, 0.95 * a);
            Polys(env2, lit, Col(env2, "fg"), 0.5 * a);
            Polys(env2, litA, Acc(env2), 0.9 * a);
            var le = InE(env2, 0.3, 0.35);
            Label(env2, $"SECTOR {Pad2((env2.Cut?.Line ?? 0) + 1)}", sp.X, sp.Y + fs * 0.5, JO("size", fs, "alpha", a * le, "track", 0.2));
            Label(env2, $"{Pad2(litSet.Count)}/{cols * rows}", sp.X + w, sp.Y + fs * 0.5,
                JO("size", fs, "align", "right", "color", Col(env2, "fg"), "alpha", a * le));
            Segs(env2, new[] { new[] { sp.X, sp.Y + fs * 1.15, sp.X + w * le, sp.Y + fs * 1.15 } }, Col(env2, "sub"), lw, 0.5 * a);
        }),
    });

    private static void RegisterSpectrumRing() => Reg0("spectrumRing", new DecorDef
    {
        Name = "円形スペクトラム",
        Tags = new[] { "glitch", "pop", "graphic" },
        W = 0.9,
        Layer = "front",
        Draw = (env, p) => WithSettle(env, p, (env2, p2) =>
        {
            if (env2.Pass != "main") return;
            var bb = GetBB(env2, env2.Body);
            var sc = env2.Sc!;
            var u = U(env2);
            var o = OutE(env2);
            if (o <= 0.003 || env2.Lt < 0) return;
            var r0 = J.Clamp(Math.Min(env2.W, env2.H) * 0.052, 34 * u, 66 * u);
            var lm = r0 * 0.95;
            var sp = SpotP(env2, bb, (r0 + lm) * 2.1, (r0 + lm) * 2.1, p2, 18 * u);
            var a = o * (sp.Ok ? 1 : 0.35);
            var cx = sp.Cx;
            var cy = sp.Cy;
            const int n = 60;
            var e = J.E.OutExpo(J.Clamp(env2.Lt / 0.5));
            var en = env2.Plan.Energy is { Length: > 0 } ? 0.35 + env2.Energy * 1.1 : 1;
            var bt = BeatOf(env2);
            var beat = bt.Has ? Math.Exp(-bt.Since * 6) : 0.5 + 0.5 * Math.Sin(env2.Ltb * 7);
            var rot = (p2.R * 360 + env2.Ltb * 6) * J.DEG;
            var bars = new List<double[]>();
            var hot = new List<double[]>();
            var peaks = new List<double[]>();
            for (var i = 0; i < n; i++)
            {
                if (i / (double)n > e) break;
                var k = i < n / 2 ? i : n - 1 - i;
                var f = k / (n / 2.0);
                var v = J.Clamp(0.55 + 0.45 * J.Noise1(k * 0.5 + env2.Ltb * 5.5, p2.Seed) * (1 - f * 0.55) * en * (0.75 + 0.35 * beat));
                var an = rot + i / (double)n * J.TAU;
                var c = Math.Cos(an);
                var s = Math.Sin(an);
                var l = lm * v * e;
                var r1 = r0 + 3 * u + l;
                (v > 0.78 ? hot : bars).Add(new[] { cx + c * (r0 + 3 * u), cy + s * (r0 + 3 * u), cx + c * r1, cy + s * r1 });
                var pk = r0 + 3 * u + lm * J.Clamp(0.25 + 0.7 * (0.55 + 0.45 * J.Noise1(k * 0.5 + (env2.Ltb - 0.25) * 5.5, p2.Seed)) * (1 - f * 0.55) * en) * e + 5 * u;
                if (i % 2 == 0) peaks.Add(new[] { cx + c * Math.Max(pk, r1 + 4 * u), cy + s * Math.Max(pk, r1 + 4 * u), 1.3 * u });
            }
            var bl = Math.Max(1.4, Math.Min(3.2 * u, J.TAU * r0 / n * 0.55));
            Segs(env2, bars, Col(env2, "fg"), bl, 0.85 * a);
            Segs(env2, hot, Acc(env2), bl, a);
            Dots(env2, peaks, Col(env2, "sub"), 0.8 * a);
            env2.Arc(cx, cy, r0, -90, -90 + 360 * e, Col(env2, "sub"), Math.Max(1, u), 0.7 * a, false);
            env2.Arc(cx, cy, r0 * 0.72, 90, 90 + 360 * InE(env2, 0.5, 0.15), Col(env2, "sub"), Math.Max(1, 0.8 * u), 0.4 * a, false);
            var fs = r0 * 0.42;
            Label(env2, Pad2((env2.Cut?.Line ?? 0) + 1), cx, cy - fs * 0.1,
                JO("size", fs, "align", "center", "color", Col(env2, "fg"), "alpha", a * InE(env2, 0.3, 0.3), "track", 0.04));
            Label(env2, "Hz", cx, cy + fs * 0.75, JO("size", fs * 0.42, "align", "center", "alpha", a * InE(env2, 0.3, 0.4), "track", 0.2));
        }),
    });

    private static void RegisterDataColumns() => Reg0("dataColumns", new DecorDef
    {
        Name = "データ列",
        Tags = new[] { "glitch", "editorial", "graphic" },
        W = 0.9,
        Layer = "front",
        Draw = (env, p) => WithSettle(env, p, (env2, p2) =>
        {
            if (env2.Pass != "main") return;
            var bb = GetBB(env2, env2.Body);
            var sc = env2.Sc!;
            var ctx = env2.Ctx;
            var u = U(env2);
            var o = OutE(env2);
            if (o <= 0.003 || env2.Lt < 0) return;
            var fs = FS(env2) * 0.9;
            var rh = fs * 1.55;
            var nr = 6 + p2.N;
            var font = MonoF(env2);
            var chars = JLayouts.CodePoints(env2.Cut?.Text ?? "").Where(c => c.Trim().Length > 0).ToList();
            const string sample = "0x0000  00 00 00 00  U+0000";
            var w = TextW(sample, font, fs, 0.06) + 22 * u;
            var hwin = nr * rh;
            var h = hwin + fs * 2.2;
            var sp = SpotP(env2, bb, w, h, p2, 24 * u);
            var a = o * (sp.Ok ? 1 : 0.35);
            var x0 = sp.X + 14 * u;
            var yTop = sp.Y + fs * 2.2;
            var e = J.E.OutExpo(J.Clamp(env2.Lt / 0.5));
            var vis = hwin * e;
            static string Hex(uint n, int k) => ((uint)n).ToString("X", CultureInfo.InvariantCulture).PadLeft(k, '0')[^k..];
            Label(env2, "MEM", sp.X, sp.Y + fs * 0.6, JO("size", fs, "color", Acc(env2), "alpha", a * e, "track", 0.2));
            Label(env2, $"LYRIC_{Pad2((env2.Cut?.Line ?? 0) + 1)}", sp.X + w, sp.Y + fs * 0.6,
                JO("size", fs, "align", "right", "alpha", a * e, "track", 0.1));
            Segs(env2, new[] { new[] { sp.X, sp.Y + fs * 1.4, sp.X + w * e, sp.Y + fs * 1.4 } }, Col(env2, "sub"), Math.Max(1, u), 0.7 * a);
            Segs(env2, new[] { new[] { sp.X, yTop, sp.X, yTop + vis } }, Col(env2, "sub"), Math.Max(1, u), 0.5 * a);
            const double speed = 2.6;
            var off = env2.Ltb * speed;
            var @base = (int)Math.Floor(off);
            var frac = J.E.InOutCubic(J.Clamp((off - @base) * 2.5));
            ctx.Save();
            ctx.ClipRect(new SKRect((float)(sp.X - 2 * u), (float)yTop, (float)(sp.X + w + 2 * u), (float)(yTop + vis)));
            for (var k = -1; k <= nr; k++)
            {
                var nn = @base + k;
                var y = yTop + (k + 1 - frac) * rh - rh * 0.5;
                if (y < yTop - rh || y > yTop + vis + rh) continue;
                var bytes = string.Join(" ", new[] { 0, 1, 2, 3 }.Select(j => Hex(J.H(p2.Seed, nn, j), 2)));
                var ch = chars.Count > 0 ? chars[((nn % chars.Count) + chars.Count) % chars.Count] : "A";
                var txt = $"0x{Hex(((J.H(p2.Seed, 7) & 0xff) << 8) + (uint)(nn * 16), 4)}  {bytes}  U+{Hex((uint)char.ConvertToUtf32(ch, 0), 4)}";
                var newest = k == nr - 1;
                var age = (nr - 1 - k) / (double)nr;
                Label(env2, txt, x0, y, JO("size", fs, "font", font, "color", newest ? Col(env2, "fg") : Col(env2, "sub"),
                    "alpha", a * (newest ? 1 : 0.3 + 0.6 * (1 - age)), "track", 0.06));
                if (newest)
                    Polys(env2, new[] { new[] { new[] { sp.X + 3 * u, y - 4 * u }, new[] { sp.X + 9 * u, y }, new[] { sp.X + 3 * u, y + 4 * u } } }, Acc(env2), a);
            }
            ctx.Restore();
        }),
    });

    private static void RegisterSpinner() => Reg0("spinner", new DecorDef
    {
        Name = "読み込み",
        Tags = new[] { "glitch", "pop", "graphic" },
        W = 0.8,
        Layer = "front",
        Draw = (env, p) => WithSettle(env, p, (env2, p2) =>
        {
            if (env2.Pass != "main") return;
            var bb = GetBB(env2, env2.Body);
            var sc = env2.Sc!;
            var u = U(env2);
            var o = OutE(env2);
            if (o <= 0.003 || env2.Lt < 0) return;
            var r0 = J.Clamp(Math.Min(env2.W, env2.H) * 0.036, 22 * u, 44 * u);
            var fs = FS(env2) * 0.95;
            var font = MonoF(env2);
            var tw = TextW("LOADING...", font, fs, 0.16);
            var w = r0 * 2 + 14 * u + tw;
            var h = r0 * 2 + 4 * u;
            var sp = SpotP(env2, bb, w, h, p2, 22 * u);
            var a = o * (sp.Ok ? 1 : 0.35);
            var left = sp.Cx < env2.W / 2 || !sp.Ok;
            var cx = left ? sp.X + r0 : sp.X + w - r0;
            var cy = sp.Cy;
            var tDone = J.Clamp((env2.Cut?.Dur ?? 0) * 0.55, 0.9, 2.4);
            var done = J.Clamp((env2.Lt - tDone) / 0.35);
            var e = J.E.OutBack(J.Clamp(env2.Lt / 0.35), 1.6);
            var v = p2.V % 3;
            var lw = Math.Max(1.4, 2.2 * u);
            var ac = Acc(env2);
            var sa = a * (1 - done);
            if (sa > 0.01)
            {
                if (v == 0)
                {
                    var st = (int)Math.Floor(env2.Ltb * 12);
                    var list = new List<double[]>[3] { new(), new(), new() };
                    for (var i = 0; i < 12; i++)
                    {
                        var k = ((st - i) % 12 + 12) % 12;
                        var an = i * 30 * J.DEG;
                        list[k < 2 ? 0 : k < 6 ? 1 : 2].Add(new[] { cx + Math.Cos(an) * r0 * 0.5 * e, cy + Math.Sin(an) * r0 * 0.5 * e, cx + Math.Cos(an) * r0 * e, cy + Math.Sin(an) * r0 * e });
                    }
                    Segs(env2, list[0], Col(env2, "fg"), lw, sa, false, SKStrokeCap.Round);
                    Segs(env2, list[1], Col(env2, "fg"), lw, 0.5 * sa, false, SKStrokeCap.Round);
                    Segs(env2, list[2], Col(env2, "fg"), lw, 0.18 * sa, false, SKStrokeCap.Round);
                }
                else if (v == 1)
                {
                    var a1 = env2.Ltb * 300;
                    var a2 = -env2.Ltb * 200;
                    var sw = 90 + 60 * Math.Sin(env2.Ltb * 4);
                    env2.Arc(cx, cy, r0 * e, a1, a1 + sw, Col(env2, "fg"), lw, sa, false);
                    env2.Arc(cx, cy, r0 * 0.62 * e, a2, a2 + sw * 0.8, ac, lw, sa, false);
                    env2.Circle(cx, cy, r0 * e, null, Col(env2, "sub"), Math.Max(1, 0.8 * u), 0.25 * sa, false);
                }
                else
                {
                    var d = new List<double[]>();
                    for (var i = 0; i < 8; i++)
                    {
                        var an = (env2.Ltb * 280 - i * 26) * J.DEG;
                        d.Add(new[] { cx + Math.Cos(an) * r0 * 0.8 * e, cy + Math.Sin(an) * r0 * 0.8 * e, (3.4 - i * 0.35) * u * e });
                    }
                    Dots(env2, d.Take(1), ac, sa);
                    Dots(env2, d.Skip(1), Col(env2, "fg"), 0.7 * sa);
                }
            }
            if (done > 0)
            {
                var de = J.E.OutCubic(done);
                env2.Arc(cx, cy, r0, -90, -90 + 360 * de, ac, lw, a, false);
                var ck = new[] { new[] { cx - r0 * 0.42, cy + r0 * 0.02 }, new[] { cx - r0 * 0.1, cy + r0 * 0.32 }, new[] { cx + r0 * 0.45, cy - r0 * 0.3 } };
                Stroke(env2, Part(ck, 0, J.E.InOutCubic(J.Clamp((env2.Lt - tDone - 0.15) / 0.3))), ac, lw, a, false,
                    cap: SKStrokeCap.Round, join: SKStrokeJoin.Round);
            }
            var dotsN = (int)Math.Floor(env2.Ltb * 3) % 4;
            var txt = done > 0.5 ? "COMPLETE" : "LOADING" + new string('.', Math.Max(0, dotsN));
            var lx = left ? cx + r0 + 12 * u : cx - r0 - 12 * u - tw;
            Label(env2, txt, lx, cy - fs * 0.35, JO("size", fs, "font", font, "color", done > 0.5 ? ac : Col(env2, "fg"),
                "alpha", a * InE(env2, 0.3, 0.1), "track", 0.16));
            var pr = done > 0 ? 100 : Math.Min(99, (int)Math.Floor(100 * (1 - Math.Exp(-env2.Lt / tDone * 2.2))));
            Label(env2, $"{Pad2(pr, 3)}%  {((int)(env2.Lt * 1000)).ToString(CultureInfo.InvariantCulture).PadLeft(5, '0')}ms", lx, cy + fs * 0.75,
                JO("size", fs * 0.72, "font", font, "alpha", a * 0.8 * InE(env2, 0.3, 0.2), "track", 0.1));
        }),
    });

    private static void RegisterHeadingTape() => Reg0("headingTape", new DecorDef
    {
        Name = "方位テープ",
        Tags = new[] { "glitch", "graphic", "editorial" },
        W = 0.9,
        Layer = "front",
        Draw = (env, p) => WithSettle(env, p, (env2, p2) =>
        {
            if (env2.Pass != "main") return;
            var bb = GetBB(env2, env2.Body);
            var w2 = env2.W;
            var h2 = env2.H;
            var sc = env2.Sc!;
            var u = U(env2);
            var m = MG(env2);
            var o = OutE(env2);
            if (o <= 0.003 || env2.Lt < 0) return;
            var fs = FS(env2) * 0.85;
            var font = MonoF(env2);
            var gap = 22 * u;
            var ht = fs * 1.4 + 16 * u + fs * 2.2;
            var aboveRoom = bb.Y0 - gap - m * 0.6;
            var belowRoom = h2 - m * 0.6 - bb.Y1 - gap;
            var above = !p2.Low;
            if ((above ? aboveRoom : belowRoom) < ht && (above ? belowRoom : aboveRoom) > (above ? aboveRoom : belowRoom)) above = !above;
            var room = above ? aboveRoom : belowRoom;
            var compact = room < ht;
            if (compact) ht = fs * 1.4 + 18 * u;
            var yT = above
                ? Math.Max(m * 0.6, bb.Y0 - gap - ht - Math.Max(0, (room - ht) * 0.35))
                : bb.Y1 + gap + Math.Max(0, (room - ht) * 0.35);
            var a = o * (room >= ht * 0.8 ? 1 : 0.35);
            var wt = Math.Min(Math.Min(w2 * 0.56, 620 * u), w2 - 2 * m);
            var cx = J.Clamp((bb.X0 + bb.X1) / 2, m + wt / 2, w2 - m - wt / 2);
            var e = J.E.OutExpo(J.Clamp(env2.Lt / 0.55)) * (1 - 0.7 * J.E.InCubic(env2.POut));
            var half = wt / 2 * e;
            var hdg = ((p2.R * 360 + env2.Ltb * 7 * (p2.Right ? 1 : -1) + 18 * Math.Sin(env2.Ltb * 0.6 + p2.R * 5)) % 360 + 360) % 360;
            var k = wt / 100.0;
            var @base = yT + fs * 1.4 + 14 * u;
            var lw = Math.Max(1, u);
            var buckets = new List<double[]>[3] { new(), new(), new() };
            var d0 = (int)Math.Ceiling((hdg - 50) / 5) * 5;
            for (var d = d0; d <= hdg + 50; d += 5)
            {
                var x = cx + (d - hdg) * k;
                if (Math.Abs(x - cx) > half) continue;
                var f = Math.Abs(x - cx) / (wt / 2);
                var b = f < 0.5 ? 0 : f < 0.8 ? 1 : 2;
                var l = (d % 15 == 0 ? 12 : 6) * u;
                buckets[b].Add(new[] { x, @base, x, @base - l });
                if (d % 15 == 0 && f < 0.92)
                {
                    var dd = ((d % 360) + 360) % 360;
                    var lab = dd == 0 ? "N" : dd == 90 ? "E" : dd == 180 ? "S" : dd == 270 ? "W" : Pad2(dd, 3);
                    Label(env2, lab, x, @base - 12 * u - fs * 0.75,
                        JO("size", fs, "font", font, "align", "center", "color", lab.Length == 1 ? Acc(env2) : Col(env2, "sub"),
                            "alpha", a * (1 - f) * 1.2, "track", 0.04));
                }
            }
            for (var b = 0; b < 3; b++) Segs(env2, buckets[b], Col(env2, "fg"), lw, a * new[] { 0.9, 0.55, 0.22 }[b]);
            Segs(env2, new[] { new[] { cx - half, @base, cx + half, @base } }, Col(env2, "sub"), lw, 0.6 * a);
            var pw = 6 * u;
            var py = @base + 3 * u;
            Polys(env2, new[] { new[] { new[] { cx, py }, new[] { cx - pw, py + pw * 1.3 }, new[] { cx + pw, py + pw * 1.3 } } }, Acc(env2), a);
            var txt = $"{Pad2(hdg, 3)}°";
            var tw = TextW(txt, font, fs * 1.1, 0.08) + 14 * u;
            var bh0 = fs * 1.6;
            var bx = compact ? cx + half + 12 * u : cx - tw / 2;
            var by = compact ? @base - 6 * u - bh0 / 2 : py + pw * 1.3 + 4 * u;
            var be = InE(env2, 0.3, 0.25);
            RrStroke(env2, bx, by, tw, bh0 * be, 2 * u, Col(env2, "fg"), lw, a);
            if (be > 0.6)
                Label(env2, txt, bx + tw / 2, by + bh0 / 2, JO("size", fs * 1.1, "font", font, "align", "center", "color", Col(env2, "fg"),
                    "alpha", a * J.Clamp((be - 0.6) / 0.4), "track", 0.08));
            Label(env2, "HDG", compact ? cx - half - 10 * u : bx - 8 * u, by + bh0 / 2,
                JO("size", fs * 0.72, "font", font, "align", "right", "alpha", a * be, "track", 0.2));
        }),
    });

    private static void RegisterGlyphLock() => Reg0("glyphLock", new DecorDef
    {
        Name = "文字ロック",
        Tags = new[] { "glitch", "graphic", "editorial" },
        W = 0.9,
        Layer = "front",
        Draw = (env, p) => WithSettle(env, p, (env2, p2) =>
        {
            if (env2.Pass != "main") return;
            var bb = GetBB(env2, env2.Body);
            var w2 = env2.W;
            var h2 = env2.H;
            var sc = env2.Sc!;
            var u = U(env2);
            var m = MG(env2);
            var o = OutE(env2);
            if (o <= 0.003 || env2.Lt < 0) return;
            var gl = GlyphLinesOf(bb);
            var vert = gl.Vert;
            List<double[]> a, b;
            if (gl.Lines.Count > 0)
            {
                a = gl.Lines[0].G;
                b = gl.Lines[^1].G;
            }
            else
            {
                var gc = JLayouts.GlyphCount(Regex.Replace(env2.Cut?.Text ?? "", @"\s+", ""));
                var n = Math.Max(1, Math.Min(12, gc > 0 ? gc : 1)); // JS (…|| 1)
                var list = new List<double[]>();
                for (var i = 0; i < n; i++)
                    list.Add(vert
                        ? new[] { bb.X0, J.Lerp(bb.Y0, bb.Y1, i / (double)n), bb.X1, J.Lerp(bb.Y0, bb.Y1, (i + 1) / (double)n) }
                        : new[] { J.Lerp(bb.X0, bb.X1, i / (double)n), bb.Y0, J.Lerp(bb.X0, bb.X1, (i + 1) / (double)n), bb.Y1 });
                a = list;
                b = list;
            }
            var pad = 12 * u + (vert ? Bw(bb) : Bh(bb)) * 0.07;
            var lw = Math.Max(1.2, 1.9 * u);
            var ac = Acc(env2);
            const double hop = 0.34;
            var t = Math.Max(0, env2.Lt - 0.15);
            var k = (int)Math.Floor(t / hop);
            var f = J.E.OutExpo(J.Clamp((t - k * hop) / 0.16));
            int Idx(int i, List<double[]> l) => ((i + (int)(J.H(p2.Seed, 1) % 3)) % l.Count + l.Count) % l.Count;
            double[] LerpBox(List<double[]> l, int i)
            {
                var q0 = l[Idx(i - 1, l)];
                var q1 = l[Idx(i, l)];
                return k == 0 ? q1 : new[] { J.Lerp(q0[0], q1[0], f), J.Lerp(q0[1], q1[1], f), J.Lerp(q0[2], q1[2], f), J.Lerp(q0[3], q1[3], f) };
            }
            var e = J.E.OutExpo(J.Clamp(env2.Lt / 0.3));
            var tk = new List<double[]>();
            var nVis = Math.Min(a.Count, k + 1);
            for (var j = 0; j < nVis; j++)
            {
                var g = a[Idx(j, a)];
                if (vert)
                    tk.Add(new[] { bb.X1 + pad * 0.45, (g[1] + g[3]) / 2, bb.X1 + pad * 0.45 + 5 * u, (g[1] + g[3]) / 2 });
                else
                    tk.Add(new[] { (g[0] + g[2]) / 2, bb.Y0 - pad * 0.45, (g[0] + g[2]) / 2, bb.Y0 - pad * 0.45 - 5 * u });
            }
            Segs(env2, tk, Col(env2, "sub"), Math.Max(1, u), 0.6 * o);
            List<double[][]> Corners(double[] g, int side)
            {
                var s = (1 - e) * 10 * u;
                var list = new List<double[][]>();
                if (!vert)
                {
                    var y = side < 0 ? bb.Y0 - pad - s : bb.Y1 + pad + s;
                    var dy = side < 0 ? 1 : -1;
                    var lh = Math.Min((g[2] - g[0]) * 0.36, 22 * u);
                    var lv = pad * 0.8;
                    var x0 = g[0] - 3 * u - s;
                    var x1 = g[2] + 3 * u + s;
                    list.Add(new[] { new[] { x0, y + dy * lv }, new[] { x0, y }, new[] { x0 + lh, y } });
                    list.Add(new[] { new[] { x1 - lh, y }, new[] { x1, y }, new[] { x1, y + dy * lv } });
                }
                else
                {
                    var x = side < 0 ? bb.X1 + pad + s : bb.X0 - pad - s;
                    var dx = side < 0 ? -1 : 1;
                    var lh = Math.Min((g[3] - g[1]) * 0.36, 22 * u);
                    var lv = pad * 0.8;
                    var y0 = g[1] - 3 * u - s;
                    var y1 = g[3] + 3 * u + s;
                    list.Add(new[] { new[] { x + dx * lv, y0 }, new[] { x, y0 }, new[] { x, y0 + lh } });
                    list.Add(new[] { new[] { x, y1 - lh }, new[] { x, y1 }, new[] { x + dx * lv, y1 } });
                }
                return list;
            }
            var ga = LerpBox(a, k);
            var gb = LerpBox(b, k + 2);
            Strokes(env2, Corners(ga, -1), ac, lw, o * e);
            Strokes(env2, Corners(gb, 1), Col(env2, "fg"), lw, 0.8 * o * e);
            var fs = FS(env2) * 0.9;
            var textChars = JLayouts.CodePoints(Regex.Replace(env2.Cut?.Text ?? "", @"\s+", ""));
            var ch = textChars.Count > 0 ? textChars[Idx(k, a) % textChars.Count] : "";
            var txt = $"TGT {Pad2(Idx(k, a) + 1)}/{Pad2(a.Count)}";
            var le = InE(env2, 0.25, 0.2);
            if (!vert)
            {
                var lx = J.Clamp((ga[0] + ga[2]) / 2, m, w2 - m);
                var up = bb.Y0 - pad - 14 * u - fs > m * 0.4;
                var ly = up ? bb.Y0 - pad - 10 * u - fs * 0.6 : bb.Y1 + pad + 14 * u + fs * 0.6;
                Segs(env2, new[] { new[] { lx, up ? bb.Y0 - pad - 2 * u : bb.Y1 + pad + 2 * u, lx, up ? ly + fs * 0.6 : ly - fs * 0.6 } }, ac, Math.Max(1, u), o * le);
                Label(env2, txt, lx + 6 * u, ly, JO("size", fs, "color", Col(env2, "fg"), "alpha", o * le, "track", 0.12));
                if (ch.Length > 0)
                    Label(env2, ch, lx - 6 * u, ly, JO("size", fs * 1.1, "font", BodyF(env2), "align", "right", "color", ac, "alpha", o * le));
            }
            else
            {
                var ly = J.Clamp((ga[1] + ga[3]) / 2, m, h2 - m);
                var right = bb.X1 + pad + 16 * u + TextW(txt, MonoF(env2), fs, 0.12) < w2 - m * 0.4;
                var lx = right ? bb.X1 + pad + 12 * u : bb.X0 - pad - 12 * u;
                Label(env2, txt, lx, ly, JO("size", fs, "color", Col(env2, "fg"), "align", right ? "left" : "right", "alpha", o * le, "track", 0.12));
            }
        }),
    });

    private static void RegisterAtomOrbit() => Reg0("atomOrbit", new DecorDef
    {
        Name = "原子軌道",
        Tags = new[] { "graphic", "glitch", "calm" },
        W = 0.8,
        Layer = "front",
        Draw = (env, p) => WithSettle(env, p, (env2, p2) =>
        {
            if (env2.Pass != "main") return;
            var bb = GetBB(env2, env2.Body);
            var sc = env2.Sc!;
            var u = U(env2);
            var o = OutE(env2);
            if (o <= 0.003 || env2.Lt < 0) return;
            var r0 = J.Clamp(Math.Min(env2.W, env2.H) * 0.072, 46 * u, 92 * u);
            var fs = FS(env2) * 0.75;
            var sp = SpotP(env2, bb, r0 * 2.3, r0 * 2.3 + fs * 1.6, p2, 22 * u);
            var a = o * (sp.Ok ? 1 : 0.35);
            var cx = sp.Cx;
            var cy = sp.Y + r0 * 1.15;
            var ac = Acc(env2);
            var lw = Math.Max(1, 1.1 * u);
            var @base = p2.R * 180 + env2.Ltb * 5 * (p2.Right ? 1 : -1);
            var ry = r0 * 0.34;
            const int n = 3;
            var back = new List<(double[] P, List<double[]> Tr, int K)>();
            var front = new List<(double[] P, List<double[]> Tr, int K)>();
            for (var k = 0; k < n; k++)
            {
                var rot = (@base + k * 180.0 / n) * J.DEG;
                var e = J.E.InOutCubic(J.Clamp((env2.Lt - k * 0.07) / 0.45));
                Stroke(env2, EllP(cx, cy, r0, ry, rot, 0, 360 * e, 80), Col(env2, "fg"), lw, 0.55 * a);
                var w = (1.4 + k * 0.35) * (k % 2 != 0 ? -1 : 1);
                var ph = J.R(p2.Seed, k) * 360;
                double[] Pos(double t)
                {
                    var an = (ph + t * w * 90) * J.DEG;
                    var x = Math.Cos(an) * r0;
                    var y = Math.Sin(an) * ry;
                    return new[] { cx + x * Math.Cos(rot) - y * Math.Sin(rot), cy + x * Math.Sin(rot) + y * Math.Cos(rot), Math.Sin(an) };
                }
                if (e < 0.9) continue;
                var ptx = Pos(env2.Ltb);
                var tr = new List<double[]>();
                for (var j = 0; j < 8; j++)
                {
                    var q0 = Pos(env2.Ltb - j * 0.035);
                    var q1 = Pos(env2.Ltb - (j + 1) * 0.035);
                    tr.Add(new[] { q0[0], q0[1], q1[0], q1[1], 1 - j / 8.0 });
                }
                (ptx[2] < 0 ? back : front).Add((ptx, tr, k));
            }
            void DrawE((double[] P, List<double[]> Tr, int K) it)
            {
                var dim = it.P[2] < 0 ? 0.45 : 1;
                foreach (var s in it.Tr)
                    Segs(env2, new[] { new[] { s[0], s[1], s[2], s[3] } }, it.K == 0 ? ac : Col(env2, "fg"), 2.2 * u * s[4], 0.5 * s[4] * a * dim, false, SKStrokeCap.Round);
                env2.Circle(it.P[0], it.P[1], (it.P[2] < 0 ? 2.6 : 3.8) * u, it.K == 0 ? ac : Col(env2, "fg"), null, 0, a * dim, false);
            }
            back.ForEach(DrawE);
            var q = J.E.OutBack(J.Clamp((env2.Lt - 0.1) / 0.35), 2);
            var pulse = 1 + 0.06 * Math.Sin(env2.Ltb * 6);
            var nr = 5 * u * q * pulse;
            Dots(env2, new[] { new[] { cx - nr * 0.6, cy - nr * 0.3, nr }, new[] { cx + nr * 0.6, cy + nr * 0.35, nr } }, ac, a);
            Dots(env2, new[] { new[] { cx + nr * 0.5, cy - nr * 0.55, nr }, new[] { cx - nr * 0.5, cy + nr * 0.5, nr } }, Col(env2, "fg"), a);
            env2.Circle(cx, cy, nr * 2.6, null, Col(env2, "sub"), Math.Max(1, 0.8 * u), 0.5 * a * q, false);
            front.ForEach(DrawE);
            Label(env2, $"ORB-{n}  e⁻{n}", cx, cy + r0 * 1.15 + fs * 0.4, JO("size", fs, "align", "center", "alpha", a * InE(env2, 0.3, 0.5), "track", 0.2));
        }),
    });

    private static void RegisterSonarArcs() => Reg0("sonarArcs", new DecorDef
    {
        Name = "音波",
        Tags = new[] { "pop", "emotional", "graphic" },
        W = 0.9,
        Layer = "front",
        Draw = (env, p) => WithSettle(env, p, (env2, p2) =>
        {
            if (env2.Pass != "main") return;
            var bb = GetBB(env2, env2.Body);
            var w2 = env2.W;
            var h2 = env2.H;
            var sc = env2.Sc!;
            var u = U(env2);
            var m = MG(env2);
            var o = OutE(env2);
            if (o <= 0.003 || env2.Lt < 0) return;
            var cx = (bb.X0 + bb.X1) / 2;
            var cy = (bb.Y0 + bb.Y1) / 2;
            var roomH = Math.Min(bb.X0, w2 - bb.X1) - m * 0.5;
            var roomV = Math.Min(bb.Y0, h2 - bb.Y1) - m * 0.5;
            var vert = IsVert(bb);
            if ((vert ? Bh(bb) / h2 : Bw(bb) / w2) > 0.66 && (vert ? roomH : roomV) > 60 * u) vert = !vert;
            var g0 = 16 * u + (vert ? Bw(bb) : Bh(bb)) * 0.04;
            double[][] sides = vert
                ? new[] { new[] { cx, bb.Y0 - g0, -90.0, bb.Y0 - g0 - m * 0.5 }, new[] { cx, bb.Y1 + g0, 90.0, h2 - m * 0.5 - bb.Y1 - g0 } }
                : new[] { new[] { bb.X0 - g0, cy, 180.0, bb.X0 - g0 - m * 0.5 }, new[] { bb.X1 + g0, cy, 0.0, w2 - m * 0.5 - bb.X1 - g0 } };
            var bt = BeatOf(env2);
            var len = bt.Has ? bt.Len : 0.62;
            var since = bt.Has ? bt.Since : ((env2.Ltb % len) + len) % len;
            const double span = 0; // vert?40:34 → 见下方 spanV
            var spanV = vert ? 40 : 34;
            var e = J.E.OutCubic(J.Clamp(env2.Lt / 0.4));
            var ac = Acc(env2);
            foreach (var sd in sides)
            {
                var ex = sd[0];
                var ey = sd[1];
                var dir = sd[2];
                var room = sd[3];
                if (room < 30 * u) continue;
                var rMax = Math.Min(room, 190 * u);
                var r0 = 8 * u;
                var lists = new List<double[]>[3] { new(), new(), new() };
                for (var k = 0; k < 4; k++)
                {
                    var prog = (since + k * len) / (len * 4);
                    if (prog >= 1) continue;
                    var r = J.Lerp(r0, rMax, J.E.OutCubic(prog)) * e;
                    var al = Math.Pow(1 - prog, 1.3);
                    lists[al > 0.66 ? 0 : al > 0.33 ? 1 : 2].Add(new[] { ex, ey, r, (dir - spanV) * J.DEG, (dir + spanV) * J.DEG });
                }
                ArcsB(env2, lists[0], ac, 2.4 * u, 0.95 * o, false, SKStrokeCap.Round);
                ArcsB(env2, lists[1], Col(env2, "fg"), 1.6 * u, 0.6 * o, false, SKStrokeCap.Round);
                ArcsB(env2, lists[2], Col(env2, "fg"), 1.1 * u, 0.3 * o, false, SKStrokeCap.Round);
                var st = new List<double[]>();
                for (var j = 1; j <= 2; j++)
                    st.Add(new[] { ex, ey, j * 7 * u * e, (dir - spanV * 1.2) * J.DEG, (dir + spanV * 1.2) * J.DEG });
                ArcsB(env2, st, Col(env2, "sub"), Math.Max(1, 1.2 * u), 0.8 * o, false, SKStrokeCap.Round);
                env2.Circle(ex, ey, 2.2 * u * e, ac, null, 0, o, false);
            }
        }),
    });

    private static void RegisterCircuit() => Reg0("circuit", new DecorDef
    {
        Name = "回路",
        Tags = new[] { "glitch", "graphic" },
        W = 0.9,
        Layer = "front",
        Draw = (env, p) => WithSettle(env, p, (env2, p2) =>
        {
            if (env2.Pass != "main") return;
            var bb = GetBB(env2, env2.Body);
            var w2 = env2.W;
            var h2 = env2.H;
            var sc = env2.Sc!;
            var u = U(env2);
            var o = OutE(env2);
            if (o <= 0.003 || env2.Lt < 0) return;
            var gap = 18 * u + Math.Min(Bw(bb), Bh(bb)) * 0.05;
            var rooms = new Dictionary<string, double> { ["l"] = bb.X0 - gap, ["r"] = w2 - bb.X1 - gap, ["t"] = bb.Y0 - gap, ["b"] = h2 - bb.Y1 - gap };
            var order = rooms.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
            var pref = p2.Right ? "r" : "l";
            if (rooms[pref] > 140 * u) order = new[] { pref }.Concat(order.Where(s => s != pref)).ToList();
            var sides = new List<string> { order[0] };
            if (p2.Big && rooms[order[1]] > 140 * u) sides.Add(order[1]);
            var ac = Acc(env2);
            var lw = Math.Max(1, 1.3 * u);
            for (var si = 0; si < sides.Count; si++)
            {
                var side = sides[si];
                var sEnd = rooms[side];
                if (sEnd < 70 * u) continue;
                var horiz = side is "l" or "r";
                var tc = horiz ? J.Clamp((bb.Y0 + bb.Y1) / 2, 60 * u, h2 - 60 * u) : J.Clamp((bb.X0 + bb.X1) / 2, 60 * u, w2 - 60 * u);
                var across = horiz ? Bh(bb) : Bw(bb);
                var n = 4 + p2.N % 3;
                var pe = J.Clamp(across / n, 10 * u, 22 * u);
                var ps = pe * (1.6 + p2.R * 0.8);
                double[] M(double s, double t) => side switch
                {
                    "l" => new[] { s, t },
                    "r" => new[] { w2 - s, t },
                    "t" => new[] { t, s },
                    _ => new[] { t, h2 - s },
                };
                var paths = new List<double[][]>();
                var pads = new List<double[]>();
                var vias = new List<double[]>();
                for (var i = 0; i < n; i++)
                {
                    var c = i - (n - 1) / 2.0;
                    var t0 = tc + c * ps + J.Rs(p2.Seed, i, 1) * 3 * u;
                    var t1 = tc + c * pe;
                    var d = Math.Abs(t1 - t0);
                    var sm = sEnd * (0.45 + 0.15 * J.Rs(p2.Seed, si, 2));
                    var sa = Math.Max(10 * u, sm - d / 2);
                    var pts = new[] { M(-6 * u, t0), M(sa, t0), M(sa + d, t1), M(sEnd - (i % 2 != 0 ? 14 * u : 0), t1) };
                    paths.Add(pts);
                    pads.Add(pts[3]);
                    if (d > 3 * u) vias.Add(pts[1]);
                }
                var drawn = new List<double[][]>();
                for (var i = 0; i < paths.Count; i++)
                {
                    var e = J.E.InOutCubic(J.Clamp((env2.Lt - i * 0.06 - si * 0.1) / 0.6));
                    drawn.Add(Part(paths[i], 0, e));
                }
                Strokes(env2, drawn, Col(env2, "sub"), lw, 0.85 * o, false, join: SKStrokeJoin.Miter);
                var ea = InE(env2, 0.3, 0.55);
                RingsB(env2, pads.Select(q => new[] { q[0], q[1], 4 * u * ea }), Col(env2, "fg"), Math.Max(1, 1.2 * u), o);
                Dots(env2, pads.Select(q => new[] { q[0], q[1], 1.6 * u * ea }), ac, o);
                RingsB(env2, vias.Select(q => new[] { q[0], q[1], 2.6 * u * ea }), Col(env2, "sub"), Math.Max(1, u), 0.8 * o);
                var pl = new List<double[][]>();
                for (var i = 0; i < paths.Count; i++)
                {
                    var t = 1.3 + J.R(p2.Seed, i, 5) * 0.8;
                    var ph = ((env2.Ltb - 0.7 - J.R(p2.Seed, i, 6) * t) / t) % 1;
                    if (env2.Lt < 0.7 || ph < 0) continue;
                    pl.Add(Part(paths[i], Math.Max(0, ph - 0.1), ph));
                }
                Strokes(env2, pl, ac, 2.4 * u, o, false, cap: SKStrokeCap.Round);
            }
        }),
    });

    private static void RegisterSwatches() => Reg0("swatches", new DecorDef
    {
        Name = "色見本",
        Tags = new[] { "editorial", "graphic" },
        W = 0.9,
        Layer = "front",
        Draw = (env, p) => WithSettle(env, p, (env2, p2) =>
        {
            if (env2.Pass != "main") return;
            var bb = GetBB(env2, env2.Body);
            var sc = env2.Sc!;
            var u = U(env2);
            var o = OutE(env2);
            if (o <= 0.003 || env2.Lt < 0) return;
            var seen = new HashSet<string>();
            var cols = new List<(string? C, string K)>();
            foreach (var (c, k) in new[] { (Col(env2, "accent"), "ACC"), (Col(env2, "accent2"), "AC2"), (Col(env2, "fg"), "FG"), (Col(env2, "sub"), "SUB"), (Col(env2, "ink"), "INK"), (Col(env2, "dim"), "DIM") })
            {
                if (string.IsNullOrEmpty(c)) continue;
                var hexs = c.ToUpperInvariant();
                if (!seen.Add(hexs)) continue;
                cols.Add((c, k));
            }
            var sq = J.Clamp(Math.Min(env2.W, env2.H) * 0.037, 24 * u, 44 * u);
            var g = 2 * u;
            var fs = FS(env2) * 0.7;
            var n = Math.Min(6, cols.Count);
            var w = n * (sq + g) + sq * 1.4;
            var h = sq * 1.7 + fs * 3.4;
            var sp = SpotP(env2, bb, w, h,
                new DecorSpec { Right = p2.Right, Low = p2.Low || p2.Corner, Corner = p2.Corner, Big = p2.Big, V = p2.V, R = p2.R, Seed = p2.Seed, N = p2.N, Accent = p2.Accent }, 24 * u);
            var a = o * (sp.Ok ? 1 : 0.35);
            var x0 = sp.X;
            var y0 = sp.Y + fs * 1.6;
            var lw = Math.Max(1, u);
            Label(env2, $"COLOR BAR  {Pad2(n)}", x0, sp.Y + fs * 0.6, JO("size", fs, "alpha", a * InE(env2, 0.3), "track", 0.2));
            for (var i = 0; i < n; i++)
            {
                var (c, k) = cols[i];
                var q = J.E.OutExpo(J.Clamp((env2.Lt - 0.05 - i * 0.06) / 0.35));
                if (q <= 0) continue;
                var x = x0 + i * (sq + g);
                Rects(env2, new[] { new[] { x, y0, sq, sq * q } }, c, a);
                var q2 = J.E.OutExpo(J.Clamp((env2.Lt - 0.25 - i * 0.06) / 0.35));
                Rects(env2, new[] { new[] { x, y0 + sq + g, sq, sq * 0.7 * q2 } }, J.Mix(Col(env2, "bg") ?? "#000000", c!, 0.5), a);
                if (J.Contrast(c!, Col(env2, "bg") ?? "#000000") < 1.3) RrStroke(env2, x, y0, sq, sq * q, 0, Col(env2, "sub"), lw, 0.6 * a);
                Label(env2, k, x + sq / 2, y0 + sq * 1.7 + g + fs * 0.9, JO("size", fs, "align", "center", "alpha", a * q2, "track", 0.05));
                Label(env2, c!.Replace("#", "").ToUpper(CultureInfo.InvariantCulture).PadRight(6)[..6], x + sq / 2, y0 + sq * 1.7 + g + fs * 2.1,
                    JO("size", fs * 0.8, "align", "center", "color", Col(env2, "fg"), "alpha", 0.8 * a * q2, "track", 0.0));
            }
            var e = InE(env2, 0.4, 0.3);
            var rx = x0 + n * (sq + g) + sq * 0.7;
            var ry = y0 + sq * 0.85;
            var rr = sq * 0.42;
            env2.Arc(rx, ry, rr, -90, -90 + 360 * e, Col(env2, "fg"), lw, a, false);
            Segs(env2, new[] { new[] { rx - rr * 1.5 * e, ry, rx + rr * 1.5 * e, ry }, new[] { rx, ry - rr * 1.5 * e, rx, ry + rr * 1.5 * e } }, Col(env2, "fg"), lw, a);
        }),
    });

    private static void RegisterRegistration() => Reg0("registration", new DecorDef
    {
        Name = "見当合わせ",
        Tags = new[] { "editorial", "graphic", "glitch" },
        W = 0.8,
        Layer = "front",
        Draw = (env, p) => WithSettle(env, p, (env2, p2) =>
        {
            if (env2.Pass != "main") return;
            var bb = GetBB(env2, env2.Body);
            var sc = env2.Sc!;
            var u = U(env2);
            var o = OutE(env2);
            if (o <= 0.003 || env2.Lt < 0) return;
            var r0 = J.Clamp(Math.Min(env2.W, env2.H) * 0.045, 28 * u, 56 * u);
            var fs = FS(env2) * 0.72;
            var sp = SpotP(env2, bb, r0 * 3.2, r0 * 3.2 + fs * 2, p2, 22 * u);
            var a = o * (sp.Ok ? 1 : 0.35);
            var cx = sp.Cx;
            var cy = sp.Y + r0 * 1.6;
            var lockE = J.E.OutExpo(J.Clamp((env2.Lt - 0.05) / 0.9));
            var jit = env2.Lt > 1.2 && Math.Floor(env2.Ltb / 1.7) != Math.Floor((env2.Ltb - 0.08) / 1.7) ? 2.5 * u : 0;
            var d = 18 * u * (1 - lockE) + jit;
            var cols = new[] { Acc(env2), Acc2(env2), Col(env2, "fg") };
            double[][] dirs = { new[] { -1.0, -0.6 }, new[] { 0.9, -0.5 }, new[] { 0.1, 1.0 } };
            var lw = Math.Max(1, 1.3 * u);
            var e = J.E.OutBack(J.Clamp(env2.Lt / 0.35), 1.4);
            var blend = Dark(env2) ? SKBlendMode.Screen : SKBlendMode.Multiply;
            void CircBl(double x, double y, double r, string? c, double alpha)
            {
                using var pnt = new SKPaint { IsAntialias = true, IsStroke = true, StrokeWidth = (float)lw, Color = Env.SkColor(c ?? "#FFFFFF", Math.Min(1, alpha)), BlendMode = blend };
                env2.Ctx.DrawCircle((float)x, (float)y, (float)r, pnt);
            }
            for (var k = 0; k < 3; k++)
            {
                var c = cols[k];
                var x = cx + dirs[k][0] * d;
                var y = cy + dirs[k][1] * d;
                var r = r0 * e;
                CircBl(x, y, r * 0.98, c, 0.9 * a);
                CircBl(x, y, r * 0.62, c, 0.9 * a);
                Segs(env2, new[] { new[] { x - r * 1.45, y, x + r * 1.45, y }, new[] { x, y - r * 1.45, x, y + r * 1.45 } }, c, lw, 0.9 * a);
                double[][] Pie(double a0)
                {
                    var pts = new List<double[]> { new[] { x, y } };
                    for (var i = 0; i <= 8; i++)
                    {
                        var an = (a0 + i * 90 / 8.0) * J.DEG;
                        pts.Add(new[] { x + Math.Cos(an) * r * 0.32, y + Math.Sin(an) * r * 0.32 });
                    }
                    return pts.ToArray();
                }
                Polys(env2, new[] { Pie(-90), Pie(90) }, c, 0.9 * a);
            }
            var ok = lockE > 0.985 && jit == 0;
            var dx = d * 0.1;
            var t = $"ΔX {(dirs[0][0] * dx).ToString("F2", CultureInfo.InvariantCulture)}  ΔY {(dirs[0][1] * dx).ToString("F2", CultureInfo.InvariantCulture)}";
            var ly = cy + r0 * 1.6 + fs * 0.5;
            Label(env2, ok ? "REG  OK" : "REG", cx - r0 * 1.5, ly, JO("size", fs, "color", ok ? Acc(env2) : Col(env2, "sub"), "alpha", a * InE(env2, 0.3, 0.1), "track", 0.2));
            Label(env2, ok ? "±0.00mm" : t, cx + r0 * 1.5, ly, JO("size", fs, "align", "right", "color", Col(env2, "fg"), "alpha", a * InE(env2, 0.3, 0.1), "track", 0.06));
        }),
    });

    private static void RegisterPunchHoles() => Reg0("punchHoles", new DecorDef
    {
        Name = "パンチ穴",
        Tags = new[] { "editorial", "calm", "graphic" },
        W = 0.7,
        Layer = "front",
        Draw = (env, p) =>
        {
            if (env.Pass != "main") return;
            var bb = GetBB(env, env.Body);
            var w2 = env.W;
            var h2 = env.H;
            var sc = env.Sc!;
            var u = U(env);
            var m = MG(env);
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0) return;
            var r = J.Clamp(Math.Min(w2, h2) * 0.016, 10 * u, 19 * u);
            var n = p.V % 3 == 2 ? 3 : 2;
            var edges = (p.Right ? new[] { "r", "l" } : new[] { "l", "r" })
                .Concat(p.Low ? new[] { "b", "t" } : new[] { "t", "b" }).ToArray();
            (string Ed, bool VertE, double Spc, double C, double Mid)? pick = null;
            foreach (var ed in edges)
            {
                var vertE = ed is "l" or "r";
                var l = vertE ? (double)h2 : w2;
                var spc = Math.Min(l * 0.3, 260 * u) * (n == 3 ? 0.8 : 1);
                var c = ed == "l" ? m * 0.9 + r : ed == "r" ? w2 - m * 0.9 - r : ed == "t" ? m * 0.9 + r : h2 - m * 0.9 - r;
                var span = spc * (n - 1) / 2 + r * 3;
                var mid = vertE ? h2 / 2.0 : w2 / 2.0;
                var box = vertE
                    ? new[] { c - r * 5, mid - span, c + r * 5, mid + span }
                    : new[] { mid - span, c - r * 5, mid + span, c + r * 5 };
                if (!HitBb(box[0], box[1], box[2], box[3], bb, 6 * u))
                {
                    pick = (ed, vertE, spc, c, mid);
                    break;
                }
            }
            if (pick == null) return;
            var (ed2, vertE2, spc2, c2, mid2) = pick.Value;
            var lw = Math.Max(1, 1.2 * u);
            var inward = ed2 is "r" or "b" ? -1 : 1;
            double[] Pt(double along, double off) => vertE2 ? new[] { c2 + inward * off, along } : new[] { along, c2 + inward * off };
            var pos = new List<double>();
            for (var i = 0; i < n; i++) pos.Add(mid2 + (i - (n - 1) / 2.0) * spc2);
            var t0 = Math.Min(env.Cut?.InDur ?? 0, 0.6) * 0.9;
            var lt = env.Lt - t0;
            if (lt <= 0) return;
            var ge = J.E.InOutCubic(J.Clamp(lt / 0.7));
            var lh = (vertE2 ? h2 : w2) * 0.5 * ge;
            var g0 = Pt(mid2 - lh, r * 3.4);
            var g1 = Pt(mid2 + lh, r * 3.4);
            Dash(env, g0[0], g0[1], g1[0], g1[1], 6 * u, 6 * u, Col(env, "sub"), lw, 0.5 * o);
            for (var i = 0; i < pos.Count; i++)
            {
                var al = pos[i];
                var q = J.Clamp((lt - 0.08 - i * 0.14) / 0.22);
                if (q <= 0) continue;
                var xy = Pt(al, 0);
                var s = 1 + 0.35 * (1 - J.E.OutCubic(q));
                if (p.V % 2 == 0) env.Circle(xy[0], xy[1], r * 1.85 * s, null, Col(env, "sub"), r * 0.75, 0.18 * o * q, false);
                env.Circle(xy[0], xy[1], r * s, Col(env, "dim"), null, 0, 0.95 * o * q, false);
                env.Circle(xy[0], xy[1], r * s, null, Col(env, "fg"), lw, 0.9 * o * q, false);
                env.Arc(xy[0], xy[1], r * s * 0.76, 200, 290, Col(env, "sub"), 2 * u, 0.6 * o * q, false);
                if (q < 1)
                {
                    var b = new List<double[]>();
                    for (var k = 0; k < 6; k++)
                    {
                        var an = k * 60 * J.DEG + 0.3;
                        var d0 = r * 1.3 + q * 8 * u;
                        b.Add(new[] { xy[0] + Math.Cos(an) * d0, xy[1] + Math.Sin(an) * d0, xy[0] + Math.Cos(an) * (d0 + 5 * u), xy[1] + Math.Sin(an) * (d0 + 5 * u) });
                    }
                    Segs(env, b, Acc(env), lw, o * (1 - q));
                }
            }
            var de = J.E.OutExpo(J.Clamp((lt - 0.4) / 0.4));
            var fs = FS(env) * 0.72;
            if (de > 0)
            {
                var a0 = pos[0];
                var a1 = pos[^1];
                var am = (a0 + a1) / 2;
                var hl = (a1 - a0) / 2 * de;
                var gp = fs * 2.4;
                var off = r * 2.3;
                double[] S(double al, double d) => Pt(al, off + d);
                var l1a = S(am - hl, 0);
                var l1b = S(am - gp, 0);
                var l2a = S(am + gp, 0);
                var l2b = S(am + hl, 0);
                var sa0a = S(a0, -3 * u);
                var sa0b = S(a0, 3 * u);
                var sa1a = S(a1, -3 * u);
                var sa1b = S(a1, 3 * u);
                Segs(env, new[]
                {
                    new[] { l1a[0], l1a[1], l1b[0], l1b[1] },
                    new[] { l2a[0], l2a[1], l2b[0], l2b[1] },
                    new[] { sa0a[0], sa0a[1], sa0b[0], sa0b[1] },
                    new[] { sa1a[0], sa1a[1], sa1b[0], sa1b[1] },
                }, Col(env, "sub"), lw, 0.85 * o);
                var lp = S(am, 0);
                Label(env, $"{(n == 2 ? 80 : 108)}mm", lp[0], lp[1],
                    JO("size", fs, "align", "center", "rot", vertE2 ? -90.0 : 0.0, "alpha", o * de, "track", 0.1));
            }
        },
    });

    /// <summary>围着歌词画虚拟"纸张"的角线（JS sheetCorner，staple/paperClip 共用）。</summary>
    private static void SheetCorner(Env env, double x, double y, int sx, int sy, double l, double e, double a)
    {
        var u = U(env);
        var lw = Math.Max(1, u);
        var c1 = Part(new[] { new[] { x + sx * l, (double)y }, new[] { (double)x, (double)y }, new[] { (double)x, y + sy * l } }, 0.5 - 0.5 * e, 0.5 + 0.5 * e);
        var o = 5 * u;
        var c2 = Part(new[]
        {
            new[] { x + sx * l * 0.8 + sx * o, y + sy * o },
            new[] { x + sx * o, y + sy * o },
            new[] { x + sx * o, y + sy * l * 0.8 + sy * o },
        }, 0.5 - 0.5 * e, 0.5 + 0.5 * e);
        Stroke(env, c2, Col(env, "sub"), lw, 0.35 * a);
        Stroke(env, c1, Col(env, "sub"), lw, 0.8 * a);
    }

    private static void RegisterStaple() => Reg0("staple", new DecorDef
    {
        Name = "ホチキス",
        Tags = new[] { "editorial", "pop", "emotional" },
        W = 0.7,
        Layer = "front",
        Draw = (env, p) => WithSettle(env, p, (env2, p2) =>
        {
            if (env2.Pass != "main") return;
            var bb = GetBB(env2, env2.Body);
            var w2 = env2.W;
            var h2 = env2.H;
            var sc = env2.Sc!;
            var ctx = env2.Ctx;
            var u = U(env2);
            var m = MG(env2);
            var o = OutE(env2);
            if (o <= 0.003 || env2.Lt < 0) return;
            var ls = J.Clamp(Math.Min(w2, h2) * 0.058, 38 * u, 70 * u);
            var padP = ls * 0.7 + 14 * u + Bh(bb) * 0.04;
            var sx = p2.Right ? 1 : -1;
            var sy = p2.Low ? 1 : -1;
            var x = J.Clamp(sx < 0 ? bb.X0 - padP : bb.X1 + padP, m * 0.5, w2 - m * 0.5);
            var y = J.Clamp(sy < 0 ? bb.Y0 - padP : bb.Y1 + padP, m * 0.5, h2 - m * 0.5);
            var e = J.E.InOutCubic(J.Clamp(env2.Lt / 0.5));
            var l = Math.Min(ls * 3.4, Math.Max(Bw(bb), Bh(bb)) * 0.45);
            SheetCorner(env2, x, y, -sx, -sy, l, e, o);
            if (p2.V % 2 == 1)
            {
                var x2 = sx < 0 ? bb.X1 + padP * 0.6 : bb.X0 - padP * 0.6;
                var y2 = sy < 0 ? bb.Y1 + padP * 0.6 : bb.Y0 - padP * 0.6;
                var f = ls * 0.7 * InE(env2, 0.4, 0.3);
                if (x2 > m * 0.5 && x2 < w2 - m * 0.5 && y2 > m * 0.5 && y2 < h2 - m * 0.5 && f > 1)
                {
                    Stroke(env2, new[] { new[] { x2 + sx * l * 0.7 * e, y2 }, new[] { x2 + sx * f, y2 }, new[] { x2, y2 + sy * f }, new[] { x2, y2 + sy * l * 0.7 * e } }, Col(env2, "sub"), Math.Max(1, u), 0.8 * o);
                    Stroke(env2, new[] { new[] { x2 + sx * f, y2 }, new[] { x2 + sx * f, y2 + sy * f }, new[] { x2, y2 + sy * f } }, Col(env2, "sub"), Math.Max(1, u), 0.55 * o);
                }
            }
            var d = ls * 0.3;
            var cx = x - sx * d;
            var cy = y - sy * d;
            var ang = sx * sy > 0 ? -45 : 45;
            var q = J.Clamp((env2.Lt - 0.3) / 0.18);
            if (q <= 0) return;
            var s = 1 + 0.6 * (1 - J.E.OutCubic(q));
            var al = o * J.Clamp(q * 3);
            ctx.Save();
            ctx.Translate((float)cx, (float)cy);
            ctx.RotateDegrees((float)(ang + (1 - q) * 14));
            ctx.Scale((float)s, (float)s);
            var hl = ls / 2;
            var t = 2.6 * u;
            RrFill(env2, -hl + 2 * u, -t + 2.5 * u, ls, t * 2, t, Col(env2, "sub"), 0.3 * al);
            RrFill(env2, -hl, -t, ls, t * 2, t, Col(env2, "fg"), 0.95 * al);
            Segs(env2, new[] { new[] { -hl + t * 1.5, -t * 0.3, hl - t * 1.5, -t * 0.3 } }, Col(env2, "bg"), Math.Max(1, 0.8 * u), 0.45 * al);
            Rects(env2, new[] { new[] { -hl - 1.5 * u, -t * 1.6, 3 * u, t * 3.2 }, new[] { hl - 1.5 * u, -t * 1.6, 3 * u, t * 3.2 } }, Col(env2, "fg"), 0.8 * al);
            ctx.Restore();
            if (q < 1)
            {
                var b = new List<double[]>();
                for (var i = 0; i < 4; i++)
                {
                    var an = (ang + 90 + (i < 2 ? 0 : 180) + (i % 2 != 0 ? 28 : -28)) * J.DEG;
                    var d0 = 9 * u + q * 12 * u;
                    b.Add(new[] { cx + Math.Cos(an) * d0, cy + Math.Sin(an) * d0, cx + Math.Cos(an) * (d0 + 7 * u), cy + Math.Sin(an) * (d0 + 7 * u) });
                }
                Segs(env2, b, Acc(env2), Math.Max(1, 1.4 * u), o * (1 - q), false, SKStrokeCap.Round);
            }
        }),
    });

    // 回形针线材中轴（JS CLIP IIFE；[-0.34..0.46] × [-1.12..1.12] 局部坐标，已垂直居中）
    private static readonly double[][] ClipPath = BuildClip();

    private static double[][] BuildClip()
    {
        var p = new List<double[]>();
        void Seg(double[] a, double[] b)
        {
            for (var i = 0; i <= 6; i++) p.Add(L2(a, b, i / 6.0));
        }
        void Arc(double cx, double cy, double r, double a0, double a1)
        {
            for (var i = 0; i <= 14; i++)
            {
                var an = (a0 + (a1 - a0) * i / 14.0) * J.DEG;
                p.Add(new[] { cx + Math.Cos(an) * r, cy + Math.Sin(an) * r });
            }
        }
        Seg(new[] { -0.2, 0.95 }, new[] { -0.2, 2.45 });
        Arc(0.05, 2.45, 0.25, 180, 0);
        Seg(new[] { 0.3, 2.45 }, new[] { 0.3, 0.42 });
        Arc(-0.02, 0.42, 0.32, 0, -180);
        Seg(new[] { -0.34, 0.42 }, new[] { -0.34, 2.62 });
        Arc(0.06, 2.62, 0.4, 180, 0);
        Seg(new[] { 0.46, 2.62 }, new[] { 0.46, 0.85 });
        return p.Select(q => new[] { q[0], q[1] - 1.5 }).ToArray();
    }

    private static void RegisterPaperClip() => Reg0("paperClip", new DecorDef
    {
        Name = "クリップ",
        Tags = new[] { "editorial", "pop", "emotional" },
        W = 0.7,
        Layer = "front",
        Draw = (env, p) => WithSettle(env, p, (env2, p2) =>
        {
            if (env2.Pass != "main") return;
            var bb = GetBB(env2, env2.Body);
            var w2 = env2.W;
            var h2 = env2.H;
            var sc = env2.Sc!;
            var u = U(env2);
            var m = MG(env2);
            var o = OutE(env2);
            if (o <= 0.003 || env2.Lt < 0) return;
            var s = J.Clamp(Math.Min(w2, h2) * 0.035, 22 * u, 40 * u);
            var hc = s * 3.3;
            var low = p2.Low && h2 - bb.Y1 > hc * 1.2 + m;
            var padP = hc * 0.42 + 10 * u + Bh(bb) * 0.03;
            var y = low ? bb.Y1 + padP : Math.Max(m * 0.5 + hc * 0.55, bb.Y0 - padP);
            var sy = low ? 1 : -1;
            var sx = p2.Right ? 1 : -1;
            var xc = J.Clamp(sx < 0 ? bb.X0 - 26 * u - Bw(bb) * 0.02 : bb.X1 + 26 * u + Bw(bb) * 0.02, m * 0.5, w2 - m * 0.5);
            var e = J.E.InOutCubic(J.Clamp(env2.Lt / 0.45));
            var l = Math.Min(s * 7, Math.Max(Bw(bb) * 0.45, s * 4));
            SheetCorner(env2, xc, y, -sx, -sy, l, e, o);
            var cx = J.Clamp(xc - sx * l * 0.42, m, w2 - m);
            var tilt = (sx * 7 + J.Rs(p2.Seed, 1) * 5) * J.DEG;
            var slide = (1 - J.E.OutCubic(J.Clamp((env2.Lt - 0.1) / 0.45))) * 24 * u;
            var cy = y + sy * (hc * 0.12) - sy * slide;
            var pts = Xf(low ? ClipPath.Select(q => new[] { q[0], -q[1] }).ToArray() : ClipPath, cx, cy, tilt, s);
            var de = J.E.InOutCubic(J.Clamp((env2.Lt - 0.1) / 0.6));
            var wire = Part(pts, 0, de);
            Stroke(env2, wire, Col(env2, "fg"), Math.Max(1.4, 2.1 * u), 0.95 * o, false, cap: SKStrokeCap.Round, join: SKStrokeJoin.Round);
            Stroke(env2, wire.Select(q => new[] { q[0] - 0.8 * u, q[1] - 0.8 * u }).ToArray(), Col(env2, "bg"), Math.Max(1, 0.7 * u), 0.35 * o, false,
                cap: SKStrokeCap.Round, join: SKStrokeJoin.Round);
        }),
    });

    private static void RegisterIndexTabs() => Reg0("indexTabs", new DecorDef
    {
        Name = "見出しタブ",
        Tags = new[] { "editorial", "pop", "graphic" },
        W = 0.8,
        Layer = "front",
        Draw = (env, p) =>
        {
            if (env.Pass != "main") return;
            var bb = GetBB(env, env.Body);
            var w2 = env.W;
            var h2 = env.H;
            var sc = env.Sc!;
            var u = U(env);
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0) return;
            var t0 = Math.Min(env.Cut?.InDur ?? 0, 0.6) * 0.9;
            var lt = env.Lt - t0;
            if (lt <= 0) return;
            const int n = 5;
            var th = J.Clamp(Math.Min(w2, h2) * 0.066, 42 * u, 76 * u);
            var tw = th * 0.8;
            var g = 6 * u;
            var fs = FS(env) * 1.05;
            var edges = (p.Right ? new[] { "r", "l" } : new[] { "l", "r" }).Concat(new[] { "t", "b" }).ToArray();
            var span = n * th + (n - 1) * g;
            (string Ed, bool VertE, double Mid)? pick = null;
            foreach (var ed in edges)
            {
                var vertE = ed is "l" or "r";
                var mid = vertE ? h2 * (p.Low ? 0.58 : 0.42) : w2 / 2.0;
                var depth = tw + 22 * u;
                var box = ed switch
                {
                    "l" => new[] { 0.0, mid - span / 2, depth, mid + span / 2 },
                    "r" => new[] { w2 - depth, mid - span / 2, (double)w2, mid + span / 2 },
                    "t" => new[] { mid - span / 2, 0.0, mid + span / 2, depth },
                    _ => new[] { mid - span / 2, h2 - depth, mid + span / 2, (double)h2 },
                };
                if ((vertE ? h2 : w2) > span + 40 * u && !HitBb(box[0], box[1], box[2], box[3], bb, 26 * u))
                {
                    pick = (ed, vertE, mid);
                    break;
                }
            }
            if (pick == null) return;
            var (ed2, vertE2, mid2) = pick.Value;
            var cur = ((env.Cut?.Line ?? 0) % n + n) % n;
            var labs = new[]
            {
                new[] { "01", "02", "03", "04", "05" },
                new[] { "あ", "か", "さ", "た", "な" },
                new[] { "A", "B", "C", "D", "E" },
            }[p.V % 3];
            for (var i = 0; i < n; i++)
            {
                var q = J.E.OutBack(J.Clamp((lt - i * 0.06) / 0.35), 1.3);
                if (q <= 0) continue;
                var isC = i == cur;
                var d = (tw + (isC ? 16 * u : 0)) * q;
                var along = mid2 - span / 2 + i * (th + g);
                double x, y, w, h;
                if (ed2 == "l") { x = -12 * u; y = along; w = d + 12 * u; h = th; }
                else if (ed2 == "r") { x = w2 - d; y = along; w = d + 12 * u; h = th; }
                else if (ed2 == "t") { x = along; y = -12 * u; w = th; h = d + 12 * u; }
                else { x = along; y = h2 - d; w = th; h = d + 12 * u; }
                var col = isC ? Acc(env) : Col(env, "dim");
                RrFill(env, x, y, w, h, 6 * u, col, (isC ? 0.95 : 0.9) * o);
                if (!isC) RrStroke(env, x, y, w, h, 6 * u, Col(env, "sub"), Math.Max(1, u), 0.7 * o);
                var tx = ed2 == "l" ? d - tw * 0.5 : ed2 == "r" ? w2 - d + tw * 0.5 : x + th / 2;
                var ty = ed2 == "t" ? d - tw * 0.5 : ed2 == "b" ? h2 - d + tw * 0.5 : y + th / 2;
                var tc = isC ? (J.Lum(col) > 0.55 ? "#000000" : Col(env, "bg")) : Col(env, "fg");
                Label(env, labs[i], tx, ty, JO("size", fs * (labs[i].Length > 1 ? 1 : 1.15),
                    "font", labs[i].Length > 1 ? MonoF(env) : BodyF(env), "align", "center", "color", tc,
                    "alpha", o * J.Clamp((q - 0.5) * 2), "track", 0.04));
            }
            var ge = J.E.OutExpo(J.Clamp((lt - 0.1) / 0.5));
            var l = (vertE2 ? h2 : w2) * 0.5 * ge;
            double[][] pe = ed2 switch
            {
                "l" => new[] { new[] { 1.5 * u, mid2 - l }, new[] { 1.5 * u, mid2 + l } },
                "r" => new[] { new[] { w2 - 1.5 * u, mid2 - l }, new[] { w2 - 1.5 * u, mid2 + l } },
                "t" => new[] { new[] { mid2 - l, 1.5 * u }, new[] { mid2 + l, 1.5 * u } },
                _ => new[] { new[] { mid2 - l, h2 - 1.5 * u }, new[] { mid2 + l, h2 - 1.5 * u } },
            };
            Stroke(env, pe, Col(env, "sub"), Math.Max(1, u), 0.5 * o);
        },
    });

    private static void RegisterVines() => Reg0("vines", new DecorDef
    {
        Name = "蔓",
        Tags = new[] { "emotional", "calm", "pop" },
        W = 0.8,
        Layer = "front",
        Draw = (env, p) => WithSettle(env, p, (env2, p2) =>
        {
            if (env2.Pass != "main") return;
            var bb = GetBB(env2, env2.Body);
            var w2 = env2.W;
            var h2 = env2.H;
            var sc = env2.Sc!;
            var u = U(env2);
            var o = OutE(env2);
            if (o <= 0.003 || env2.Lt < 0) return;
            var nv = p2.Big ? 2 : 1;
            var ac = Acc(env2);
            var leafC = J.Mix(ac, Col(env2, "fg") ?? "#FFFFFF", 0.15);
            for (var v = 0; v < nv; v++)
            {
                var sx = (p2.Right ? 1 : -1) * (v != 0 ? -1 : 1);
                var sy = (p2.Low ? 1 : -1) * (v != 0 ? -1 : 1);
                var x0 = sx > 0 ? w2 - w2 * 0.08 : w2 * 0.08;
                var y0 = sy > 0 ? h2 + 4 * u : -4 * u;
                var th = Math.Atan2(-sy, -sx * 0.55);
                double x = x0, y = y0;
                var step = 7 * u;
                var lmax = Math.Min(w2, h2) * 0.62;
                var pts = new List<double[]> { new[] { x, y } };
                var seed = p2.Seed + v * 17;
                for (var s = 0.0; s < lmax && pts.Count < 200; s += step)
                {
                    th += Math.Sin(s / (90 * u) + seed % 7) * 0.05 + J.Noise1(s / (60 * u), seed) * 0.03;
                    x += Math.Cos(th) * step;
                    y += Math.Sin(th) * step;
                    if (ClearOf(bb, x, y, 22 * u, 1) < 1 || x < 4 || x > w2 - 4 || y < -8 || y > h2 + 8) break;
                    pts.Add(new[] { x, y });
                }
                if (pts.Count < 6) continue;
                var e = J.E.OutCubic(J.Clamp((env2.Lt - v * 0.12) / 1.0));
                var sway = Math.Sin(env2.Ltb * 0.9 + v) * 1.2 * J.DEG;
                var tt = Xf(pts.Select(q => new[] { q[0] - x0, q[1] - y0 }).ToArray(), x0, y0, sway);
                var stem = Part(tt, 0, e);
                Stroke(env2, stem, Col(env2, "fg"), Math.Max(1.2, 1.7 * u), 0.9 * o, false, cap: SKStrokeCap.Round, join: SKStrokeJoin.Round);
                var ltot = tt.Length;
                var leaves = new List<double[][]>();
                var curls = new List<double[][]>();
                for (var k = 1; k < 12; k++)
                {
                    var f = k / 12.0 + J.Rs(seed, k, 1) * 0.02;
                    if (f > e) break;
                    var pAlong = Along(tt, f);
                    var side = k % 2 != 0 ? 1 : -1;
                    var grow = J.E.OutBack(J.Clamp((e - f) / 0.12), 1.8);
                    if (k % 4 == 3)
                    {
                        var cp = new List<double[]>();
                        var an0 = pAlong.Ang + side * 1.2;
                        var r0 = 12 * u;
                        var cx = pAlong.X + Math.Cos(an0) * r0;
                        var cy = pAlong.Y + Math.Sin(an0) * r0;
                        for (var i = 0; i <= 20; i++)
                        {
                            var t = i / 20.0;
                            var an = an0 + Math.PI + side * t * 8;
                            var r = r0 * (1 - t * 0.85);
                            cp.Add(new[] { cx + Math.Cos(an) * r, cy + Math.Sin(an) * r });
                        }
                        curls.Add(Part(cp, 0, grow));
                        continue;
                    }
                    var ll = (17 + J.R(seed, k, 2) * 12) * u * grow;
                    var an2 = pAlong.Ang + side * 0.95;
                    var c = Math.Cos(an2);
                    var s = Math.Sin(an2);
                    var leaf = new List<double[]>();
                    for (var i = 0; i <= 10; i++)
                    {
                        var t = i / 10.0;
                        leaf.Add(new[] { t * ll, Math.Sin(t * Math.PI) * ll * 0.3 });
                    }
                    for (var i = 10; i >= 0; i--)
                    {
                        var t = i / 10.0;
                        leaf.Add(new[] { t * ll, -Math.Sin(t * Math.PI) * ll * 0.3 });
                    }
                    leaves.Add(leaf.Select(q => new[] { pAlong.X + q[0] * c - q[1] * s, pAlong.Y + q[0] * s + q[1] * c }).ToArray());
                }
                Polys(env2, leaves, leafC, 0.9 * o);
                Strokes(env2, curls, Col(env2, "fg"), Math.Max(1, 1.2 * u), 0.85 * o, false, cap: SKStrokeCap.Round, join: SKStrokeJoin.Round);
                if (stem.Length > 0 && e < 1)
                {
                    var q2 = stem[^1];
                    env2.Circle(q2[0], q2[1], 2.4 * u, ac, null, 0, o, false);
                }
            }
        }),
    });

    /// <summary>云的轮廓弧段/基线/圆组（JS cloudOutline）。</summary>
    private static (List<double[][]> Runs, double[][] Base, List<double[]> Circles) CloudOutline(double cx, double by, double wc, int seed)
    {
        double[][] shape = { new[] { -0.38, 0.15, 0.3 }, new[] { -0.21, 0.22, 0.5 }, new[] { 0.02, 0.26, 0.75 }, new[] { 0.23, 0.2, 0.5 }, new[] { 0.39, 0.13, 0.3 }, new[] { -0.07, 0.19, 1.35 } };
        var cs = shape.Select((f, i) =>
        {
            var r = f[1] * wc * (0.9 + 0.2 * J.R(seed, i, 1));
            return new[] { cx + f[0] * wc, by - r * f[2], r };
        }).ToList();
        var runs = new List<double[][]>();
        foreach (var c in cs)
        {
            var x = c[0];
            var y = c[1];
            var r = c[2];
            // 上半段（an: π→2π）与下半段（an: 0→π，只取基线以上）各自成 run
            foreach (var (from, to, steps) in new[] { (Math.PI, 2 * Math.PI, 40), (0.0, Math.PI, 20) })
            {
                List<double[]>? cur = null;
                for (var i = 0; i <= steps; i++)
                {
                    var an = from + (to - from) * i / steps;
                    var px = x + Math.Cos(an) * r;
                    var py = y + Math.Sin(an) * r;
                    var hidden = py > by || cs.Any(cc => (cc[0] != x || cc[1] != y) && Math.Sqrt(Math.Pow(px - cc[0], 2) + Math.Pow(py - cc[1], 2)) < cc[2] - 0.5);
                    if (hidden) { cur = null; continue; }
                    if (cur == null)
                    {
                        cur = new List<double[]>();
                        runs.Add(cur.ToArray());
                    }
                    cur.Add(new[] { px, py });
                }
            }
        }
        var xl = 1e9;
        var xr = -1e9;
        foreach (var c in cs)
            if (by - c[1] < c[2])
            {
                var d = Math.Sqrt(c[2] * c[2] - (by - c[1]) * (by - c[1]));
                xl = Math.Min(xl, c[0] - d);
                xr = Math.Max(xr, c[0] + d);
            }
        return (runs, new[] { new[] { xl, by }, new[] { xr, by } }, cs);
    }

    private static void RegisterCloudPuffs() => Reg0("cloudPuffs", new DecorDef
    {
        Name = "雲",
        Tags = new[] { "calm", "pop", "emotional" },
        W = 0.8,
        Layer = "front",
        Draw = (env, p) => WithSettle(env, p, (env2, p2) =>
        {
            if (env2.Pass != "main") return;
            var bb = GetBB(env2, env2.Body);
            var sc = env2.Sc!;
            var ctx = env2.Ctx;
            var u = U(env2);
            var o = OutE(env2);
            if (o <= 0.003 || env2.Lt < 0) return;
            var fr = FreeRects(env2, bb, 20 * u).Where(r => r.Side is "t" or "b" || r.H > r.W).ToList();
            if (fr.Count == 0) return;
            var reg = fr[0];
            var n = 2 + p2.N % 2;
            for (var i = 0; i < n; i++)
            {
                double R(int k) => J.R(p2.Seed, i, k);
                var wc = Math.Min(J.Clamp(reg.W * 0.42 * (0.6 + 0.4 * R(1)), 90 * u, 260 * u), reg.H / 0.56);
                if (wc < 50 * u) continue;
                var q = J.E.OutBack(J.Clamp((env2.Lt - i * 0.12) / 0.45), 1.5);
                if (q <= 0) continue;
                var wq = wc * (0.6 + 0.4 * q);
                var lane = (i + 0.5) / n;
                var dir = (p2.Right ? 1 : -1) * (i % 2 != 0 ? -1 : 1);
                var cx = Wrap(reg.X + reg.W * (lane + (R(2) - 0.5) * 0.25) + dir * env2.Ltb * (6 + R(3) * 8) * u, reg.X - wc * 0.2, reg.W + wc * 0.4);
                var by = reg.Y + wc * 0.52 + Math.Max(0, reg.H - wc * 0.56) * R(4) + Math.Sin(env2.Ltb * 0.8 + i) * 2 * u;
                if (cx - wc * 0.6 < reg.X - wc * 0.1 || cx + wc * 0.6 > reg.X + reg.W + wc * 0.1) continue;
                var cl = CloudOutline(cx, by, wq, p2.Seed + i);
                ctx.Save();
                ctx.ClipRect(new SKRect((float)(cx - wq * 2), (float)(by - wq * 2), (float)(cx + wq * 2), (float)by));
                using (var path = new SKPath())
                {
                    foreach (var c in cl.Circles)
                    {
                        if (c[2] <= 0.05) continue;
                        path.AddCircle((float)c[0], (float)c[1], (float)c[2]);
                    }
                    using var fillP = new SKPaint { IsAntialias = true, Color = Env.SkColor(Col(env2, "dim") ?? "#888888", Math.Min(1, 0.5 * o)) };
                    ctx.DrawPath(path, fillP);
                }
                ctx.Restore();
                Strokes(env2, cl.Runs, Col(env2, "fg"), Math.Max(1.2, 1.6 * u), 0.9 * o, false, cap: SKStrokeCap.Round, join: SKStrokeJoin.Round);
                if (cl.Base[0][0] < 1e8) Stroke(env2, cl.Base, Col(env2, "fg"), Math.Max(1.2, 1.6 * u), 0.9 * o, false, cap: SKStrokeCap.Round);
                var c0 = cl.Circles[2];
                Stroke(env2, ArcP(c0[0] - c0[2] * 0.1, c0[1] + c0[2] * 0.12, c0[2] * 0.55, 200, 290, 16), Col(env2, "sub"), Math.Max(1, u), 0.6 * o, false, cap: SKStrokeCap.Round);
            }
        }),
    });

    private static void RegisterStarField() => Reg0("starField", new DecorDef
    {
        Name = "星空",
        Tags = new[] { "calm", "emotional" },
        W = 0.8,
        Layer = "back",
        Subtle = true,
        Draw = (env, p) =>
        {
            if (env.Pass != "main" || env.Lt < 0) return;
            var w2 = env.W;
            var h2 = env.H;
            var sc = env.Sc!;
            var u = U(env);
            var o = OutE(env);
            if (o <= 0.003) return;
            var dk = Dark(env);
            var col = dk ? Col(env, "fg") : Col(env, "sub");
            var k = dk ? 1 : 0.7;
            var n = Math.Min(160, 90 + p.N * 25);
            var b = new List<double[]>[3] { new(), new(), new() };
            var bright = new List<double[]>();
            for (var i = 0; i < n; i++)
            {
                double R(int j) => J.R(p.Seed, i, j);
                var f = J.Clamp((env.Lt - R(1) * 0.5) / 0.3);
                if (f <= 0) continue;
                var x = R(2) * w2;
                var y = R(3) * h2;
                var tw = 0.5 + 0.5 * Math.Sin(env.Ltb * (1.5 + R(4) * 3) + R(5) * 6);
                var lvl = R(6) < 0.6 ? 0 : R(6) < 0.9 ? 1 : 2;
                b[lvl].Add(new[] { x, y, new[] { 1.2, 1.9, 2.8 }[lvl] * u * (0.6 + 0.4 * tw) * f });
                if (lvl == 2 && R(7) < 0.5) bright.Add(new[] { x, y, tw * f });
            }
            Dots(env, b[0], col, 0.45 * o * k);
            Dots(env, b[1], col, 0.65 * o * k);
            Dots(env, b[2], col, 0.9 * o * k);
            foreach (var br in bright)
            {
                var l = (7 + 7 * br[2]) * u;
                Segs(env, new[] { new[] { br[0] - l, br[1], br[0] + l, br[1] }, new[] { br[0], br[1] - l, br[0], br[1] + l } }, col, Math.Max(1, 0.8 * u), 0.5 * br[2] * o * k);
            }
            const double t = 2.4;
            var ph = env.Ltb - 0.6 - p.R * 0.8;
            if (ph < 0) return;
            var cyc = (int)Math.Floor(ph / t);
            var tt = (ph - cyc * t) / 0.55;
            if (tt > 1) return;
            var sx0 = w2 * J.Rr(0.15, 0.85, p.Seed, cyc, 1);
            var sy0 = h2 * J.Rr(0.05, 0.4, p.Seed, cyc, 2);
            var ang = (J.R(p.Seed, cyc, 3) < 0.5 ? 25 : 155) * J.DEG;
            var ls = Math.Min(w2, h2) * 0.35;
            var hx = sx0 + Math.Cos(ang) * ls * J.E.OutCubic(tt);
            var hy = sy0 + Math.Sin(ang) * ls * J.E.OutCubic(tt);
            var tail = ls * 0.35 * Math.Sin(Math.PI * tt);
            for (var s = 0; s < 6; s++)
            {
                var a0 = s / 6.0;
                var a1 = (s + 1) / 6.0;
                Segs(env, new[] { new[] { hx - Math.Cos(ang) * tail * a0, hy - Math.Sin(ang) * tail * a0, hx - Math.Cos(ang) * tail * a1, hy - Math.Sin(ang) * tail * a1 } },
                    col, 1.4 * u * (1 - a0), 0.8 * (1 - a0) * o * k, false, SKStrokeCap.Round);
            }
        },
    });

    private static readonly string[] MoonNames = { "新月", "三日月", "上弦", "十三夜", "満月", "居待月", "下弦", "有明" };

    private static void RegisterMoonPhases() => Reg0("moonPhases", new DecorDef
    {
        Name = "月齢",
        Tags = new[] { "calm", "emotional", "editorial" },
        W = 0.8,
        Layer = "front",
        Draw = (env, p) => WithSettle(env, p, (env2, p2) =>
        {
            if (env2.Pass != "main") return;
            var bb = GetBB(env2, env2.Body);
            var w2 = env2.W;
            var h2 = env2.H;
            var sc = env2.Sc!;
            var u = U(env2);
            var m = MG(env2);
            var o = OutE(env2);
            if (o <= 0.003 || env2.Lt < 0) return;
            const int n = 8;
            var fs = FS(env2) * 0.95;
            var r = J.Clamp(Math.Min(w2, h2) * 0.025, 14 * u, 27 * u);
            var dx = r * 2.8;
            if (dx * (n - 1) + r * 2 > w2 - 2 * m)
            {
                dx = (w2 - 2 * m - r * 2) / (n - 1);
                r = Math.Min(r, dx / 2.6);
            }
            var w = dx * (n - 1) + r * 2;
            var h = r * 2 + fs * 2.6;
            var gap = 26 * u;
            var above = !p2.Low;
            var y0 = above ? bb.Y0 - gap - h : bb.Y1 + gap;
            if (y0 < m * 0.6 || y0 + h > h2 - m * 0.6) y0 = above ? bb.Y1 + gap : bb.Y0 - gap - h;
            var sp = y0 >= m * 0.4 && y0 + h <= h2 - m * 0.4
                ? new Spot(J.Clamp((bb.X0 + bb.X1) / 2 - w / 2, m, w2 - m - w), y0, 0, 0, true, 0, 0)
                : SpotP(env2, bb, w, h, p2, gap);
            var a = o * (sp.Ok ? 1 : 0.35);
            var cy = sp.Y + r;
            var ac = Acc(env2);
            var target = (int)(J.H(unchecked((int)(env2.Cut?.Seed ?? 0)), 3) % (uint)n);
            var sel = J.E.InOutCubic(J.Clamp((env2.Lt - 0.25) / 0.7)) * target;
            var outl = new List<double[]>();
            var lit = new List<double[][]>();
            for (var k = 0; k < n; k++)
            {
                var q = J.E.OutBack(J.Clamp((env2.Lt - k * 0.05) / 0.3), 1.8);
                if (q <= 0) continue;
                var cx = sp.X + r + k * dx;
                var rr = r * q;
                var prog = k / (double)n;
                var f = (1 - Math.Cos(prog * J.TAU)) / 2;
                var wax = prog <= 0.5;
                outl.Add(new[] { cx, cy, rr });
                if (f > 0.02)
                {
                    var kx = 1 - 2 * f;
                    var pts = new List<double[]>();
                    for (var i = 0; i <= 16; i++)
                    {
                        var t = (-90 + 180 * i / 16.0) * J.DEG;
                        pts.Add(new[] { cx + (wax ? 1 : -1) * Math.Cos(t) * rr, cy + Math.Sin(t) * rr });
                    }
                    for (var i = 16; i >= 0; i--)
                    {
                        var t = (-90 + 180 * i / 16.0) * J.DEG;
                        pts.Add(new[] { cx + (wax ? 1 : -1) * kx * Math.Cos(t) * rr, cy + Math.Sin(t) * rr });
                    }
                    lit.Add(pts.ToArray());
                }
            }
            RingsB(env2, outl, Col(env2, "sub"), Math.Max(1, u), 0.8 * a);
            Polys(env2, lit, Col(env2, "fg"), 0.92 * a);
            var hx = sp.X + r + sel * dx;
            env2.Circle(hx, cy, r * 1.45, null, ac, Math.Max(1.2, 1.6 * u), a * InE(env2, 0.3, 0.2), false);
            Segs(env2, new[] { new[] { sp.X, cy + r * 1.8, sp.X + w * InE(env2, 0.5, 0.1), cy + r * 1.8 } }, Col(env2, "sub"), Math.Max(1, u), 0.5 * a);
            Segs(env2, new[] { new[] { hx, cy + r * 1.8 - 3 * u, hx, cy + r * 1.8 + 3 * u } }, ac, Math.Max(1, 1.4 * u), a);
            var done = J.Clamp((env2.Lt - 0.9) / 0.3);
            Label(env2, MoonNames[(int)Math.Round(sel) % n], hx, cy + r * 1.8 + fs * 1.1,
                JO("font", SerifF(env2), "size", fs, "align", "center", "color", Col(env2, "fg"), "alpha", a * (0.5 + 0.5 * done), "track", 0.2));
        }),
    });

    private static void RegisterSunRays() => Reg0("sunRays", new DecorDef
    {
        Name = "陽射し",
        Tags = new[] { "emotional", "pop", "calm" },
        W = 0.8,
        Layer = "back",
        Subtle = true,
        Draw = (env, p) =>
        {
            if (env.Pass != "main" || env.Lt < 0) return;
            var w2 = env.W;
            var h2 = env.H;
            var sc = env.Sc!;
            var u = U(env);
            var o = OutE(env);
            if (o <= 0.003) return;
            var bottom = p.V % 3 == 2;
            var cx = bottom ? w2 / 2.0 : p.Right ? w2 + w2 * 0.02 : -w2 * 0.02;
            var cy = bottom ? h2 + h2 * 0.08 : p.Low ? h2 + h2 * 0.03 : -h2 * 0.03;
            var n = 16 + p.N * 4;
            var r = Math.Sqrt((double)w2 * w2 + (double)h2 * h2) * 1.05;
            var e = J.E.OutCubic(J.Clamp(env.Lt / 0.8));
            var rot = (p.R * 30 + env.Ltb * 2.2 * (p.Right ? -1 : 1)) * J.DEG;
            var list = new List<double[][]>();
            for (var i = 0; i < n; i++)
            {
                var a0 = rot + i / (double)n * J.TAU;
                var a1 = a0 + J.TAU / n * 0.5;
                list.Add(new[] { new[] { cx, cy }, new[] { cx + Math.Cos(a0) * r * e, cy + Math.Sin(a0) * r * e }, new[] { cx + Math.Cos(a1) * r * e, cy + Math.Sin(a1) * r * e } });
            }
            var dk = Dark(env);
            Polys(env, list, p.Accent ? Acc(env) : Col(env, "dim"), (p.Accent ? (dk ? 0.07 : 0.09) : (dk ? 0.45 : 0.6)) * o);
            var r0 = Math.Min(w2, h2) * 0.13 * J.E.OutBack(J.Clamp(env.Lt / 0.5), 1.4);
            var ac = Acc(env);
            env.Circle(cx, cy, r0, null, ac, Math.Max(1, 1.4 * u), 0.5 * o, false);
            env.Circle(cx, cy, r0 * 1.18, null, ac, Math.Max(1, u), 0.25 * o, false);
            Dots(env, new[] { new[] { cx, cy, r0 * 0.8 } }, ac, 0.12 * o);
        },
    });

    private static void RegisterRainRipples() => Reg0("rainRipples", new DecorDef
    {
        Name = "雨の波紋",
        Tags = new[] { "calm", "emotional" },
        W = 0.8,
        Layer = "back",
        Subtle = true,
        Draw = (env, p) =>
        {
            if (env.Pass != "main" || env.Lt < 0) return;
            var w2 = env.W;
            var h2 = env.H;
            var sc = env.Sc!;
            var u = U(env);
            var o = OutE(env) * InE(env, 0.4, 0, J.E.OutCubic);
            if (o <= 0.003) return;
            var dk = Dark(env);
            var col = dk ? Col(env, "fg") : Col(env, "sub");
            var k = dk ? 0.55 : 0.6;
            var n = 9 + p.N * 3;
            var y0 = h2 * 0.6;
            var y1 = h2 * 0.97;
            var b = new List<double[][]>[3] { new(), new(), new() };
            var drops = new List<double[]>();
            for (var i = 0; i < n; i++)
            {
                var t = 1.5 + J.R(p.Seed, i, 1) * 0.9;
                var tt = env.Ltb + J.R(p.Seed, i, 2) * t;
                var cyc = (int)Math.Floor(tt / t);
                var tau = tt - cyc * t - 0.22;
                var x = w2 * J.Rr(0.03, 0.97, p.Seed, i, cyc, 3);
                var f = J.R(p.Seed, i, cyc, 4);
                var y = J.Lerp(y0, y1, f * f * 0.3 + f * 0.7);
                var depth = 0.45 + 0.55 * (y - y0) / (y1 - y0);
                var rm = (70 + J.R(p.Seed, i, cyc, 5) * 90) * u * depth;
                if (tau < 0)
                {
                    var q = 1 + tau / 0.22;
                    var l = 26 * u * depth;
                    drops.Add(new[] { x, y - (1 - q) * h2 * 0.18 - l, x, y - (1 - q) * h2 * 0.18 });
                    continue;
                }
                for (var j = 0; j < 3; j++)
                {
                    var tj = (tau - j * 0.14) / 1.1;
                    if (tj <= 0 || tj >= 1) continue;
                    var r = rm * J.E.OutCubic(tj) * (1 - j * 0.18);
                    var al = Math.Pow(1 - tj, 1.4);
                    b[al > 0.6 ? 0 : al > 0.3 ? 1 : 2].Add(EllP(x, y, r, r * 0.26 * (0.8 + 0.4 * depth), 0, 0, 360, 36));
                }
            }
            var lw = Math.Max(1, 1.1 * u);
            Strokes(env, b[0], col, lw, 0.8 * o * k);
            Strokes(env, b[1], col, lw, 0.5 * o * k);
            Strokes(env, b[2], col, lw, 0.22 * o * k);
            Segs(env, drops, col, lw, 0.6 * o * k, false, SKStrokeCap.Round);
        },
    });

    private static void RegisterBubbles() => Reg0("bubbles", new DecorDef
    {
        Name = "シャボン玉",
        Tags = new[] { "pop", "emotional", "calm" },
        W = 0.8,
        Layer = "front",
        Draw = (env, p) =>
        {
            if (env.Pass != "main") return;
            var bb = GetBB(env, env.Body);
            var w2 = env.W;
            var h2 = env.H;
            var sc = env.Sc!;
            var u = U(env);
            var m = MG(env);
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0) return;
            var n = Math.Min(14, 7 + p.N * 2);
            var iri = Acc2(env);
            var pad = 10 * u;
            var outl = new List<double[][]>();
            var hi = new List<double[][]>();
            var irid = new List<double[][]>();
            var glints = new List<double[]>();
            var pops = new List<double[]>();
            for (var i = 0; i < n; i++)
            {
                double R(int k) => J.R(p.Seed, i, k);
                var born = R(9) < 0.4 && bb.Y0 - pad - 30 * u > m;
                var tc = born ? 2.2 + R(1) * 1.2 : 3.4 + R(1) * 1.6;
                var t = born ? env.Ltb - 0.1 - R(2) * 1.4 : env.Ltb + R(2) * tc;
                if (t < 0) continue;
                var cyc = (int)Math.Floor(t / tc);
                var tau = t - cyc * tc;
                double Rc(int k) => J.R(p.Seed, i, cyc, k);
                var r0 = (14 + Rc(3) * Rc(3) * 30) * u * (born ? 0.8 : 1);
                var v = (born ? 45 + Rc(4) * 40 : 60 + Rc(4) * 60) * u;
                var x0 = born ? J.Lerp(bb.X0 + r0, bb.X1 - r0, Rc(5)) : m + (w2 - 2 * m) * Rc(5);
                var y0 = born ? bb.Y0 - pad - r0 : h2 + r0 + 10 * u;
                var life = born ? Math.Min(tc - 0.3, (y0 + r0) / v) : 2.2 + Rc(6) * (tc - 2.6);
                var x = x0 + Math.Sin(tau * 1.4 + Rc(7) * 6) * 16 * u * (born ? Math.Min(1, tau) : 1);
                var over = x0 + r0 + 16 * u > bb.X0 - pad && x0 - r0 - 16 * u < bb.X1 + pad;
                if (!born && over && y0 > bb.Y1) life = Math.Min(life, (y0 - (bb.Y1 + pad + r0)) / v);
                var y = y0 - v * Math.Min(tau, life);
                var inflate = born
                    ? J.E.OutBack(J.Clamp(tau / 0.45), 1.6)
                    : J.E.OutBack(J.Clamp(env.Lt / 0.35 + (cyc > 0 || R(2) * tc > 0.4 ? 1 : 0)), 1.6);
                if (tau < life)
                {
                    if (ClearOf(bb, x, y, pad * 0.5 + r0, 1) < 1) continue;
                    var wob = Math.Sin(tau * 5 + i) * 0.05;
                    var rx = r0 * (1 + wob) * inflate;
                    var ry = r0 * (1 - wob) * inflate;
                    outl.Add(EllP(x, y, rx, ry, 0, 0, 360, 32));
                    irid.Add(EllP(x, y, rx * 0.8, ry * 0.8, 0, 200, 262, 10));
                    hi.Add(EllP(x, y, rx * 0.8, ry * 0.8, 0, 292, 330, 8));
                    glints.Add(new[] { x - rx * 0.36, y - ry * 0.44, 1.6 * u * inflate });
                }
                else if (tau < life + 0.24)
                {
                    var q = (tau - life) / 0.24;
                    var d0 = r0 * (1 + q * 0.6);
                    for (var k = 0; k < 8; k++)
                    {
                        var an = k * 45 * J.DEG + i;
                        pops.Add(new[] { x + Math.Cos(an) * d0, y + Math.Sin(an) * d0, x + Math.Cos(an) * (d0 + 5 * u * (1 - q)), y + Math.Sin(an) * (d0 + 5 * u * (1 - q)), 1 - q });
                    }
                }
            }
            var lw = Math.Max(1, 1.2 * u);
            Strokes(env, outl, Col(env, "fg"), lw, 0.7 * o);
            Strokes(env, irid, iri, 1.8 * u, 0.8 * o, false, cap: SKStrokeCap.Round);
            Strokes(env, hi, Col(env, "fg"), 1.6 * u, 0.7 * o, false, cap: SKStrokeCap.Round);
            Dots(env, glints, Col(env, "fg"), 0.9 * o);
            foreach (var pop in pops) Segs(env, new[] { new[] { pop[0], pop[1], pop[2], pop[3] } }, Col(env, "fg"), lw, pop[4] * 0.9 * o, false, SKStrokeCap.Round);
        },
    });

    private static void RegisterSmoke() => Reg0("smoke", new DecorDef
    {
        Name = "煙",
        Tags = new[] { "calm", "emotional", "editorial" },
        W = 0.8,
        Layer = "back",
        Subtle = true,
        Draw = (env, p) =>
        {
            if (env.Pass != "main" || env.Lt < 0) return;
            var w2 = env.W;
            var h2 = env.H;
            var sc = env.Sc!;
            var u = U(env);
            var m = MG(env);
            var o = OutE(env);
            if (o <= 0.003) return;
            var incense = p.V % 2 == 1;
            var dk = Dark(env);
            var col = dk ? Col(env, "fg") : Col(env, "sub");
            var k = dk ? 0.5 : 0.6;
            var nw = incense ? 1 : 2 + p.N % 2;
            var hs = h2 * 0.72 * J.E.OutCubic(J.Clamp(env.Lt / 1.0));
            for (var w = 0; w < nw; w++)
            {
                var xb = incense ? (p.Right ? w2 - w2 * 0.12 : w2 * 0.12) : w2 * J.Rr(0.15, 0.85, p.Seed, w, 1);
                var yb = incense ? h2 - m - 90 * u : h2 + 10 * u;
                var seed = p.Seed + w * 31;
                if (incense && w == 0)
                {
                    Segs(env, new[] { new[] { xb, yb, xb + 1.5 * u, h2 - m * 0.4 } }, Col(env, "sub"), 2 * u, 0.8 * o);
                    var gl = 0.7 + 0.3 * Math.Sin(env.Ltb * 7);
                    Dots(env, new[] { new[] { xb, yb, 6 * u } }, Acc(env), 0.15 * o * gl);
                    Dots(env, new[] { new[] { xb, yb, 2.4 * u } }, Acc(env), o);
                }
                for (var s = 0; s < 4; s++)
                {
                    var pts = new List<double[]>();
                    var sp = s - 1.5;
                    for (var d = 0.0; d <= hs; d += 8 * u)
                    {
                        var a = 6 * u + d * 0.17;
                        var x = xb + sp * d * 0.035 + J.Noise1(d / (130 * u) - env.Ltb * 0.8 + s * 0.45, seed) * a
                                + Math.Sin(d / (75 * u) - env.Ltb * 1.5 + s * 0.8) * a * 0.4;
                        pts.Add(new[] { x, yb - d });
                    }
                    var n = pts.Count;
                    if (n < 3) continue;
                    for (var q = 0; q < 4; q++)
                    {
                        var seg = pts.Skip((int)Math.Floor(n * q / 4.0)).Take((int)Math.Floor(n * (q + 1) / 4.0) - (int)Math.Floor(n * q / 4.0) + 1).ToArray();
                        Stroke(env, seg, col, (1.7 - s * 0.25) * u, (0.6 - q * 0.13) * (1 - s * 0.18) * o * k, false,
                            cap: SKStrokeCap.Round, join: SKStrokeJoin.Round);
                    }
                }
            }
        },
    });

    private static void RegisterDandelion() => Reg0("dandelion", new DecorDef
    {
        Name = "綿毛",
        Tags = new[] { "calm", "emotional" },
        W = 0.8,
        Layer = "front",
        Draw = (env, p) =>
        {
            if (env.Pass != "main") return;
            var bb = GetBB(env, env.Body);
            var w2 = env.W;
            var h2 = env.H;
            var sc = env.Sc!;
            var u = U(env);
            var m = MG(env);
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0) return;
            var dir = p.Right ? 1 : -1;
            var n = 6 + p.N * 2 + (p.Big ? 3 : 0);
            var lw = Math.Max(1, 0.9 * u);
            var lines = new List<double[]>();
            var tips = new List<double[]>();
            var bodies = new List<double[]>();
            void SeedShape(double x, double y, double s, double rot)
            {
                var c = Math.Cos(rot);
                var sn = Math.Sin(rot);
                double[] T(double px, double py) => new[] { x + (px * c - py * sn) * s, y + (px * sn + py * c) * s };
                var top = T(0, -24 * u);
                var bot = T(0, 2 * u);
                lines.Add(new[] { bot[0], bot[1], top[0], top[1] });
                for (var k = 0; k < 13; k++)
                {
                    var an = (-90 + (k - 6) * 14) * J.DEG;
                    var l = 17 * u;
                    var q = T(Math.Cos(an) * l, -24 * u + Math.Sin(an) * l * 0.75);
                    lines.Add(new[] { top[0], top[1], q[0], q[1] });
                    tips.Add(new[] { q[0], q[1], 1.1 * u * s });
                }
                var b0 = T(0, 2 * u);
                var b1 = T(0, 10 * u);
                bodies.Add(new[] { b0[0], b0[1], b1[0], b1[1] });
            }
            var puff = p.Big;
            var px = p.Right ? w2 * 0.1 : w2 * 0.9;
            var py = h2 - m - h2 * 0.16;
            for (var i = 0; i < n; i++)
            {
                double R(int k) => J.R(p.Seed, i, k);
                var q = J.E.OutCubic(J.Clamp((env.Lt - R(1) * 0.35) / 0.4));
                if (q <= 0) continue;
                var v = (40 + R(2) * 50) * u;
                var span = w2 + 120 * u;
                var t = env.Ltb;
                double x, y;
                if (puff)
                {
                    var t2 = (w2 * 0.9) / v;
                    var age = Wrap(t + R(4) * t2, 0, t2);
                    x = px + dir * v * age;
                    y = py - v * 0.38 * age + Math.Sin(t * (1 + R(7)) + R(8) * 6) * 14 * u * Math.Min(1, age);
                    if (age < 0.15) continue;
                }
                else
                {
                    x = Wrap(R(3) * w2 + dir * v * (t + R(4) * 8), -60 * u, span);
                    y = Wrap(h2 * (0.12 + 0.76 * R(5)) - (18 + R(6) * 20) * u * t + Math.Sin(t * (1 + R(7)) + R(8) * 6) * 18 * u, -40 * u, h2 + 80 * u);
                }
                var al = ClearOf(bb, x, y, 14 * u, 26 * u);
                if (al < 0.6) continue;
                SeedShape(x, y, (0.8 + R(9) * 0.5) * q, (dir * 12 + Math.Sin(t * 1.2 + i) * 18) * J.DEG);
            }
            if (puff)
            {
                var e = J.E.OutCubic(J.Clamp(env.Lt / 0.5));
                var r0 = 30 * u * e;
                var st = new List<double[]>();
                for (var k = 0; k <= 12; k++)
                {
                    var f = k / 12.0;
                    st.Add(new[] { px + Math.Sin(f * 2) * 8 * u * (p.Right ? -1 : 1), h2 + 4 * u - (h2 + 4 * u - py) * f * e });
                }
                Stroke(env, st, Col(env, "sub"), 1.6 * u, 0.9 * o, false, cap: SKStrokeCap.Round);
                for (var k = 0; k < 30; k++)
                {
                    if (J.R(p.Seed, k, 44) < 0.25) continue;
                    var an = k / 30.0 * J.TAU;
                    var x1 = px + Math.Cos(an) * r0;
                    var y1 = py + Math.Sin(an) * r0;
                    lines.Add(new[] { px, py, x1, y1 });
                    tips.Add(new[] { x1, y1, 1.3 * u });
                }
                Dots(env, new[] { new[] { px, py, 4 * u * e } }, Col(env, "sub"), o);
            }
            Segs(env, lines, Col(env, "fg"), lw, 0.7 * o);
            Dots(env, tips, Col(env, "fg"), 0.8 * o);
            Segs(env, bodies, Acc(env), 2.2 * u, 0.9 * o, false, SKStrokeCap.Round);
        },
    });

    private static void RegisterFireflies() => Reg0("fireflies", new DecorDef
    {
        Name = "蛍",
        Tags = new[] { "emotional", "calm" },
        W = 0.8,
        Layer = "front",
        Draw = (env, p) =>
        {
            if (env.Pass != "main") return;
            var bb = GetBB(env, env.Body);
            var w2 = env.W;
            var h2 = env.H;
            var sc = env.Sc!;
            var u = U(env);
            var m = MG(env);
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0) return;
            string col;
            if (Dark(env))
            {
                var pair = new[] { Acc2(env), Acc(env) }.OrderByDescending(c => J.Lum(c)).ToArray();
                col = pair[0];
            }
            else col = Acc(env);
            var n = Math.Min(16, 9 + p.N * 2);
            var cx = (bb.X0 + bb.X1) / 2;
            var cy = (bb.Y0 + bb.Y1) / 2;
            for (var i = 0; i < n; i++)
            {
                double R(int k) => J.R(p.Seed, i, k);
                var f = J.Clamp((env.Lt - R(1) * 0.4) / 0.4);
                if (f <= 0) continue;
                double[] Pos(double t)
                {
                    var th = R(2) * J.TAU + J.Noise1(t * 0.18 + i * 3.1, p.Seed) * 1.6;
                    var rad = 0.55 + 0.45 * R(3) + 0.12 * J.Noise1(t * 0.3 + i * 5.3, p.Seed + 1);
                    return new[]
                    {
                        J.Clamp(cx + Math.Cos(th) * (w2 * 0.5 - m) * rad, m * 0.5, w2 - m * 0.5),
                        J.Clamp(cy + Math.Sin(th) * (h2 * 0.5 - m) * rad, m * 0.5, h2 - m * 0.5),
                    };
                }
                var pt = Pos(env.Ltb);
                var al = ClearOf(bb, pt[0], pt[1], 10 * u, 34 * u);
                if (al <= 0.02) continue;
                var b = Math.Pow(Math.Max(0, Math.Sin(env.Ltb * (1.6 + R(4) * 1.4) + R(5) * 6)), 2) * 0.85 + 0.15;
                var a = f * al * b;
                Dots(env, new[] { new[] { pt[0], pt[1], 20 * u } }, col, 0.1 * a * o);
                Dots(env, new[] { new[] { pt[0], pt[1], 8.5 * u } }, col, 0.26 * a * o);
                Dots(env, new[] { new[] { pt[0], pt[1], 3 * u } }, col, (0.35 + 0.65 * a) * o * Math.Min(1, a * 3));
                var q = Pos(env.Ltb - 0.25);
                Segs(env, new[] { new[] { q[0], q[1], pt[0], pt[1] } }, col, 1.2 * u, 0.2 * a * o, false, SKStrokeCap.Round);
            }
        },
    });

    private static void RegisterMemphis() => Reg0("memphis", new DecorDef
    {
        Name = "メンフィス",
        Tags = new[] { "pop", "graphic" },
        W = 0.9,
        Layer = "front",
        Draw = (env, p) => WithSettle(env, p, (env2, p2) =>
        {
            if (env2.Pass != "main") return;
            var bb = GetBB(env2, env2.Body);
            var w2 = env2.W;
            var h2 = env2.H;
            var sc = env2.Sc!;
            var ctx = env2.Ctx;
            var u = U(env2);
            var o = OutE(env2);
            if (o <= 0.003 || env2.Lt < 0) return;
            var k2 = Math.Min(8, 5 + p2.N);
            var pad = 34 * u + Bh(bb) * 0.22;
            var x0 = bb.X0 - pad;
            var x1 = bb.X1 + pad;
            var y0 = bb.Y0 - pad;
            var y1 = bb.Y1 + pad;
            var per = 2 * (x1 - x0 + y1 - y0);
            string[] cols = { Acc(env2), Acc2(env2), Col(env2, "fg") ?? "#FFFFFF" };
            var s = J.Clamp(Math.Min(w2, h2) * 0.036, 24 * u, 44 * u);
            var lw = 3.4 * u;
            var types = new[] { "squig", "zig", "tri", "dots", "half", "hatch" };
            for (var i = 0; i < k2; i++)
            {
                double R(int k) => J.R(p2.Seed, i, k);
                var t = (i + 0.2 + R(1) * 0.6) / k2 * per;
                double x, y;
                if (t < x1 - x0) { x = x0 + t; y = y0; }
                else if ((t -= x1 - x0) < y1 - y0) { x = x1; y = y0 + t; }
                else if ((t -= y1 - y0) < x1 - x0) { x = x1 - t; y = y1; }
                else { t -= x1 - x0; x = x0; y = y1 - t; }
                x = J.Clamp(x, s * 1.6, w2 - s * 1.6);
                y = J.Clamp(y, s * 1.6, h2 - s * 1.6);
                if (ClearOf(bb, x, y, s * 1.3, 1) < 1) continue;
                var q = J.Clamp((env2.Lt - 0.04 - i * 0.06) / 0.4);
                if (q <= 0) continue;
                var sc2 = J.E.OutBack(q, 2);
                var de = J.E.InOutCubic(q);
                var type = types[(i + p2.V) % types.Length];
                var col = cols[i % 3];
                var rot = (R(3) * 360 + Math.Sin(env2.Ltb * 1.3 + i) * 6) * J.DEG;
                ctx.Save();
                ctx.Translate((float)x, (float)y);
                ctx.RotateDegrees((float)(rot / J.DEG));
                switch (type)
                {
                    case "squig":
                    {
                        var pth = new List<double[]>();
                        for (var k = 0; k <= 24; k++)
                        {
                            var f = k / 24.0;
                            pth.Add(new[] { (f - 0.5) * s * 2.6, Math.Sin(f * J.TAU * 1.5) * s * 0.28 });
                        }
                        Stroke(env2, Part(pth, 0, de), col, lw, o, false, cap: SKStrokeCap.Round, join: SKStrokeJoin.Round);
                        break;
                    }
                    case "zig":
                    {
                        var pth = new List<double[]>();
                        for (var k = 0; k <= 6; k++) pth.Add(new[] { (k / 6.0 - 0.5) * s * 2.4, (k % 2 != 0 ? -1 : 1) * s * 0.3 });
                        Stroke(env2, Part(pth, 0, de), col, lw, o, false, cap: SKStrokeCap.Round, join: SKStrokeJoin.Round);
                        break;
                    }
                    case "tri":
                    {
                        var pth = new List<double[]>();
                        for (var k = 0; k <= 3; k++)
                        {
                            var an = (-90 + k * 120) * J.DEG;
                            pth.Add(new[] { Math.Cos(an) * s * 0.8 * sc2, Math.Sin(an) * s * 0.8 * sc2 });
                        }
                        Stroke(env2, pth, col, lw * 0.8, o, false, join: SKStrokeJoin.Round);
                        break;
                    }
                    case "dots":
                    {
                        var d = new List<double[]>();
                        for (var a = -1; a <= 1; a++)
                            for (var b = -1; b <= 1; b++)
                                d.Add(new[] { a * s * 0.5, b * s * 0.5, 2.6 * u * sc2 });
                        Dots(env2, d, col, o);
                        break;
                    }
                    case "half":
                        Polys(env2, new[] { ArcP(0, 0, s * 0.7 * sc2, 180, 360, 20) }, col, o);
                        break;
                    default:
                    {
                        var l = new List<double[]>();
                        for (var k = -1; k <= 1; k++)
                            l.Add(new[] { k * s * 0.45 - s * 0.3 * de, s * 0.5 * de, k * s * 0.45 + s * 0.3 * de, -s * 0.5 * de });
                        Segs(env2, l, col, lw * 0.9, o, false, SKStrokeCap.Round);
                        break;
                    }
                }
                ctx.Restore();
            }
        }),
    });

    private static void RegisterZigzagRibbon() => Reg0("zigzagRibbon", new DecorDef
    {
        Name = "ジグザグリボン",
        Tags = new[] { "pop", "graphic" },
        W = 0.8,
        Layer = "front",
        Draw = (env, p) => WithSettle(env, p, (env2, p2) =>
        {
            if (env2.Pass != "main") return;
            var bb = GetBB(env2, env2.Body);
            var w2 = env2.W;
            var h2 = env2.H;
            var sc = env2.Sc!;
            var u = U(env2);
            var o = OutE(env2);
            if (o <= 0.003 || env2.Lt < 0) return;
            var rw = J.Clamp(Math.Min(w2, h2) * 0.3, 200 * u, 420 * u);
            var rh = rw * 0.42;
            var sp = CornerSpot(env2, bb, rw, rh, p2, 0.6);
            var a = o * (sp.Ok ? 1 : 0.3);
            var sx = sp.Sx;
            var sy = sp.Sy;
            var ox = sx > 0 ? w2 + 20 * u : -20 * u;
            var oy = sy > 0 ? sp.Y + rh * 0.75 : sp.Y + rh * 0.25;
            var ix = sx > 0 ? sp.X : sp.X + rw;
            var iy = sy > 0 ? sp.Y + rh * 0.2 : sp.Y + rh * 0.8;
            var l = Math.Sqrt(Math.Pow(ix - ox, 2) + Math.Pow(iy - oy, 2));
            var dx = (ix - ox) / l;
            var dy = (iy - oy) / l;
            var nx = -dy;
            var ny = dx;
            const int seg = 7;
            var z = rh * 0.22;
            var wv = rh * 0.3;
            var vx = nx * 0.35;
            var vy = 1.0;
            var vl = Math.Sqrt(vx * vx + vy * vy);
            var vxu = vx / vl * wv;
            var vyu = vy / vl * wv;
            var e = J.E.OutCubic(J.Clamp(env2.Lt / 0.6)) * seg;
            double[] C(int k)
            {
                var s = l * k / (double)seg;
                return new[] { ox + dx * s + nx * (k % 2 != 0 ? z : -z), oy + dy * s + ny * (k % 2 != 0 ? z : -z) };
            }
            var front = new List<double[][]>();
            var back = new List<double[][]>();
            var edges = new List<double[][]>();
            for (var k = 0; k < seg; k++)
            {
                if (k >= e) break;
                var f = Math.Min(1, e - k);
                var p0 = C(k);
                var p1f = C(k + 1);
                var p1 = L2(p0, p1f, f);
                var quad = new[] { p0, p1, new[] { p1[0] + vxu, p1[1] + vyu }, new[] { p0[0] + vxu, p0[1] + vyu } };
                (k % 2 != 0 ? back : front).Add(quad);
                edges.Add(new[] { p0, p1 });
                edges.Add(new[] { new[] { p0[0] + vxu, p0[1] + vyu }, new[] { p1[0] + vxu, p1[1] + vyu } });
            }
            var c1 = Acc(env2);
            string c2;
            if (J.Contrast(Acc2(env2), Col(env2, "bg") ?? "#000000") > 1.6 && Acc2(env2) != c1) c2 = Acc2(env2);
            else c2 = J.Mix(c1, Col(env2, "bg") ?? "#000000", 0.4);
            Polys(env2, back, c2, 0.95 * a, true);
            Polys(env2, front, c1, 0.95 * a, true);
            Strokes(env2, edges, Col(env2, "bg"), Math.Max(1, 0.8 * u), 0.25 * a);
        }),
    });

    private static void RegisterPolkaPatch() => Reg0("polkaPatch", new DecorDef
    {
        Name = "水玉",
        Tags = new[] { "pop", "graphic", "calm" },
        W = 0.9,
        Layer = "front",
        Draw = (env, p) => WithSettle(env, p, (env2, p2) =>
        {
            if (env2.Pass != "main") return;
            var bb = GetBB(env2, env2.Body);
            var sc = env2.Sc!;
            var u = U(env2);
            var o = OutE(env2);
            if (o <= 0.003 || env2.Lt < 0) return;
            var cols = 6 + p2.N;
            var rows = 3 + p2.V % 2;
            var s = J.Clamp(Math.Min(env2.W, env2.H) * 0.03, 20 * u, 36 * u);
            var w = (cols - 0.5) * s;
            var h = (rows - 1) * s * 0.87 + s;
            var sp = SpotP(env2, bb, w, h, p2, 24 * u);
            var a = o * (sp.Ok ? 1 : 0.35);
            var round = p2.V % 3 == 2;
            var ac = Acc(env2);
            var main = new List<double[]>();
            var hot = new List<double[]>();
            var dir = p2.Right ? 1 : -1;
            for (var j = 0; j < rows; j++)
                for (var i = 0; i < cols; i++)
                {
                    var x = sp.X + s * 0.5 + i * s + (j % 2 != 0 ? s * 0.5 : 0);
                    var y = sp.Y + s * 0.5 + j * s * 0.87;
                    if (x > sp.X + w) continue;
                    if (round)
                    {
                        var fx = (x - sp.X - w / 2) / (w / 2);
                        var fy = (y - sp.Y - h / 2) / (h / 2);
                        if (fx * fx + fy * fy > 1.05) continue;
                    }
                    var d = (dir > 0 ? i : cols - i) + j;
                    var q = J.E.OutBack(J.Clamp((env2.Lt - d * 0.03) / 0.3), 2);
                    if (q <= 0) continue;
                    var wv = 0.5 + 0.5 * Math.Sin(env2.Ltb * 3.2 - d * 0.7);
                    var rr = s * 0.42 * (0.35 + 0.65 * wv) * q;
                    (wv > 0.93 && (i + j) % 3 == 0 ? hot : main).Add(new[] { x, y, rr });
                }
            Dots(env2, main, p2.Accent ? ac : Col(env2, "fg"), 0.9 * a);
            Dots(env2, hot, p2.Accent ? Col(env2, "fg") : ac, a);
        }),
    });

    private static void RegisterStripeCircle() => Reg0("stripeCircle", new DecorDef
    {
        Name = "縞の円",
        Tags = new[] { "graphic", "pop" },
        W = 0.9,
        Layer = "front",
        Draw = (env, p) => WithSettle(env, p, (env2, p2) =>
        {
            if (env2.Pass != "main") return;
            var bb = GetBB(env2, env2.Body);
            var sc = env2.Sc!;
            var ctx = env2.Ctx;
            var u = U(env2);
            var o = OutE(env2);
            if (o <= 0.003 || env2.Lt < 0) return;
            var r0 = J.Clamp(Math.Min(env2.W, env2.H) * 0.062, 40 * u, 80 * u);
            var sp = SpotP(env2, bb, r0 * 2.4, r0 * 2.4, p2, 22 * u);
            var a = o * (sp.Ok ? 1 : 0.35);
            var cx = sp.Cx - 3 * u;
            var cy = sp.Cy - 3 * u;
            var e = J.E.OutBack(J.Clamp(env2.Lt / 0.45), 1.5);
            var r = r0 * e;
            var v = p2.V % 2;
            if (r <= 1) return;
            var ac = Acc(env2);
            var half = v == 1;
            env2.Circle(cx + 7 * u, cy + 7 * u, r, null, Col(env2, "sub"), Math.Max(1, u), 0.7 * a * J.Clamp((env2.Lt - 0.2) / 0.3), false);
            ctx.Save();
            using (var clip = new SKPath())
            {
                if (half)
                    clip.AddArc(new SKRect((float)(cx - r), (float)(cy - r), (float)(cx + r), (float)(cy + r)), 0, 180);
                else clip.AddCircle((float)cx, (float)cy, (float)r);
                ctx.ClipPath(clip, antialias: true);
            }
            var th = (45 + env2.Ltb * (half ? 0 : 18) * (p2.Right ? 1 : -1)) * J.DEG;
            var sp2 = 8 * u;
            var shift = half ? env2.Ltb * 14 * u % sp2 : 0;
            var c = Math.Cos(th);
            var s = Math.Sin(th);
            var list = new List<double[]>();
            for (var k = -(int)Math.Ceiling(r0 / sp2) - 1; k <= (int)Math.Ceiling(r0 / sp2) + 1; k++)
            {
                var d = k * sp2 + shift;
                list.Add(new[] { cx + c * d - s * r0 * 1.2, cy + s * d + c * r0 * 1.2, cx + c * d + s * r0 * 1.2, cy + s * d - c * r0 * 1.2 });
            }
            Segs(env2, list, ac, 3.2 * u, a);
            ctx.Restore();
            if (half)
            {
                var he = J.E.InOutCubic(J.Clamp((env2.Lt - 0.2) / 0.5));
                var ar = new List<double[]>();
                for (var k = 1; k <= 3; k++)
                    ar.Add(new[] { cx, cy, r * k / 4.0, Math.PI, Math.PI + Math.PI * he });
                ArcsB(env2, ar, Col(env2, "fg"), Math.Max(1, 1.1 * u), 0.8 * a);
                Segs(env2, new[] { new[] { cx - r, cy, cx + r, cy } }, Col(env2, "fg"), Math.Max(1, 1.2 * u), a);
            }
            env2.Circle(cx, cy, r, null, Col(env2, "fg"), Math.Max(1, 1.5 * u), a, false);
        }),
    });

    private static void RegisterDecoCorners() => Reg0("decoCorners", new DecorDef
    {
        Name = "装飾コーナー",
        Tags = new[] { "editorial", "graphic", "calm" },
        W = 0.9,
        Layer = "front",
        Draw = (env, p) =>
        {
            if (env.Pass != "main") return;
            var bb = GetBB(env, env.Body);
            var w2 = env.W;
            var h2 = env.H;
            var sc = env.Sc!;
            var u = U(env);
            var m = MG(env);
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0) return;
            var lc = J.Clamp(Math.Min(w2, h2) * 0.15, 90 * u, 190 * u);
            var g = 9 * u;
            var lw = Math.Max(1, 1.3 * u);
            var ac = Acc(env);
            int[][] cs = p.Big
                ? new[] { new[] { -1, -1 }, new[] { 1, -1 }, new[] { 1, 1 }, new[] { -1, 1 } }
                : p.Corner ? new[] { new[] { -1, -1 }, new[] { 1, 1 } } : new[] { new[] { 1, -1 }, new[] { -1, 1 } };
            for (var k = 0; k < cs.Length; k++)
            {
                var sx = cs[k][0];
                var sy = cs[k][1];
                var x = sx < 0 ? m * 0.7 : w2 - m * 0.7;
                var y = sy < 0 ? m * 0.7 : h2 - m * 0.7;
                if (HitBb(Math.Min(x, x - sx * lc), Math.Min(y, y - sy * lc), Math.Max(x, x - sx * lc), Math.Max(y, y - sy * lc), bb, 6 * u)) continue;
                var e = J.E.OutCubic(J.Clamp((env.Lt - k * 0.06) / 0.55));
                if (e <= 0) continue;
                var ix = -sx;
                var iy = -sy;
                double[] P2(double dx, double dy) => new[] { x + ix * dx, y + iy * dy };
                var outer = new[] { P2(lc, 0), P2(0, 0), P2(0, lc) };
                var inner = new[] { P2(lc * 0.62, g), P2(g * 2, g), P2(g * 2, g * 2), P2(g, g * 2), P2(g, lc * 0.62) };
                Stroke(env, Part(outer, 0.5 - 0.5 * e, 0.5 + 0.5 * e), Col(env, "fg"), lw, 0.85 * o);
                Stroke(env, Part(inner, 0.5 - 0.5 * e, 0.5 + 0.5 * e), Col(env, "fg"), lw, 0.6 * o);
                var e2 = J.E.OutCubic(J.Clamp((env.Lt - 0.15 - k * 0.06) / 0.5));
                var a0 = Math.Atan2(iy, 0) / J.DEG;
                var a1 = Math.Atan2(0, ix) / J.DEG;
                var s0 = a0;
                var s1 = a1;
                if (Math.Abs(s1 - s0) > 180)
                {
                    if (s1 < s0) s1 += 360;
                    else s0 += 360;
                }
                var mid = (s0 + s1) / 2;
                var hs = (s1 - s0) / 2 * e2;
                Stroke(env, ArcP(x, y, lc * 0.42, mid - hs, mid + hs, 30), Col(env, "fg"), lw, 0.5 * o);
                Stroke(env, ArcP(x, y, lc * 0.42 + 5 * u, mid - hs * 0.6, mid + hs * 0.6, 20), Col(env, "sub"), Math.Max(1, 0.8 * u), 0.5 * o);
                var q = J.E.OutBack(J.Clamp((env.Lt - 0.35 - k * 0.06) / 0.3), 2);
                if (q <= 0) continue;
                double[] Dia(double dx, double dy, double r) => new[] { x + ix * dx, y + iy * dy, r };
                double[][] ToPoly(double[] d)
                {
                    var r = d[2];
                    return new[] { new[] { d[0], d[1] - r }, new[] { d[0] + r, d[1] }, new[] { d[0], d[1] + r }, new[] { d[0] - r, d[1] } };
                }
                var d1 = Dia(lc, 0, 4 * u * q);
                var d2 = Dia(0, lc, 4 * u * q);
                var dm = Dia(lc * 0.42 * 0.7071, lc * 0.42 * 0.7071, 5.5 * u * q);
                Polys(env, new[] { ToPoly(d1), ToPoly(d2) }, ac, o);
                Polys(env, new[] { ToPoly(dm) }, Col(env, "fg"), 0.9 * o);
                var corner = P2(g * 3.4, g * 3.4);
                Dots(env, new[] { new[] { corner[0], corner[1], 1.8 * u * q } }, ac, o);
            }
        },
    });

    private static void RegisterHalfCircles() => Reg0("halfCircles", new DecorDef
    {
        Name = "半円の積層",
        Tags = new[] { "graphic", "pop", "calm" },
        W = 0.9,
        Layer = "front",
        Draw = (env, p) => WithSettle(env, p, (env2, p2) =>
        {
            if (env2.Pass != "main") return;
            var bb = GetBB(env2, env2.Body);
            var sc = env2.Sc!;
            var u = U(env2);
            var o = OutE(env2);
            if (o <= 0.003 || env2.Lt < 0) return;
            var v = p2.V % 3;
            var r0 = J.Clamp(Math.Min(env2.W, env2.H) * 0.058, 38 * u, 74 * u);
            var lw = Math.Max(1.2, 1.6 * u);
            var bw0 = v == 1 ? r0 * 7.4 : r0 * 2.2;
            var bh0 = v == 0 ? r0 * 2.5 : v == 1 ? r0 * 1.3 : r0 * 1.2;
            var sp = SpotP(env2, bb, bw0, bh0, p2, 24 * u);
            var a = o * (sp.Ok ? 1 : 0.35);
            var ac = Acc(env2);
            var a2 = Acc2(env2);
            double Grow(int k) => J.E.OutBack(J.Clamp((env2.Lt - k * 0.08) / 0.35), 1.6);
            if (v == 0)
            {
                var yb = sp.Y + bh0;
                for (var k = 0; k < 4; k++)
                {
                    var r = r0 * (1 - k * 0.2);
                    var q = Grow(k);
                    if (q <= 0) break;
                    var cx = sp.Cx + Math.Sin(env2.Ltb * 1.2 + k) * 2 * u * k;
                    var pts = ArcP(cx, yb, r * q, 180, 360, 28);
                    if (k % 2 == 0) Polys(env2, new[] { pts }, k == 0 ? ac : Col(env2, "fg"), 0.95 * a);
                    else Stroke(env2, pts.Concat(new[] { pts[0] }).ToArray(), Col(env2, "fg"), lw, a, false, join: SKStrokeJoin.Round);
                    yb -= r * q * 0.62;
                }
            }
            else if (v == 1)
            {
                var r = r0 * 0.6;
                var y = sp.Cy;
                for (var k = 0; k < 6; k++)
                {
                    var q = Grow(k);
                    if (q <= 0) break;
                    var cx = sp.X + r + k * r * 2;
                    var up = k % 2 == 0;
                    var bob = Math.Sin(env2.Ltb * 2 + k) * 2 * u;
                    var pts = ArcP(cx, y + bob, r * q, up ? 180 : 0, up ? 360 : 180, 20);
                    if (k % 3 == 0) Polys(env2, new[] { pts }, ac, 0.95 * a);
                    else if (k % 3 == 1) Polys(env2, new[] { pts }, a2, 0.9 * a);
                    else Stroke(env2, pts.Concat(new[] { pts[0] }).ToArray(), Col(env2, "fg"), lw, a);
                }
                Segs(env2, new[] { new[] { sp.X, y, sp.X + bw0 * InE(env2, 0.5), y } }, Col(env2, "fg"), Math.Max(1, u), 0.6 * a);
            }
            else
            {
                var yb = sp.Y + bh0;
                var rot = Math.Sin(env2.Ltb * 0.8) * 3;
                for (var k = 0; k < 5; k++)
                {
                    var r = r0 * (1 - k * 0.18);
                    var q = Grow(4 - k);
                    if (q <= 0) continue;
                    var pts = ArcP(sp.Cx, yb, r, 180 + rot, 180 + rot + 180 * J.E.OutCubic(J.Clamp(q)), 36);
                    if (k == 4) Polys(env2, new[] { ArcP(sp.Cx, yb, r * q, 180, 360, 20) }, ac, a);
                    else Stroke(env2, pts, k % 2 != 0 ? ac : Col(env2, "fg"), 3 * u, a, false, cap: SKStrokeCap.Butt);
                }
                Segs(env2, new[] { new[] { sp.Cx - r0 * 1.1, yb, sp.Cx + r0 * 1.1, yb } }, Col(env2, "fg"), Math.Max(1, u), 0.7 * a * InE(env2, 0.4));
            }
        }),
    });

    private static void RegisterLoopArrows() => Reg0("loopArrows", new DecorDef
    {
        Name = "循環矢印",
        Tags = new[] { "graphic", "pop", "editorial" },
        W = 0.8,
        Layer = "front",
        Draw = (env, p) => WithSettle(env, p, (env2, p2) =>
        {
            if (env2.Pass != "main") return;
            var bb = GetBB(env2, env2.Body);
            var sc = env2.Sc!;
            var u = U(env2);
            var o = OutE(env2);
            if (o <= 0.003 || env2.Lt < 0) return;
            var r0 = J.Clamp(Math.Min(env2.W, env2.H) * 0.055, 36 * u, 70 * u);
            var sp = SpotP(env2, bb, r0 * 2.8, r0 * 2.8, p2, 22 * u);
            var a = o * (sp.Ok ? 1 : 0.35);
            var cx = sp.Cx;
            var cy = sp.Cy;
            var n = 2 + p2.N % 2;
            var dir = p2.Right ? 1 : -1;
            var ac = Acc(env2);
            var lw = Math.Max(1.4, 2.4 * u);
            var e = J.E.OutCubic(J.Clamp(env2.Lt / 0.5));
            var rot = dir * (env2.Ltb * 70 + (1 - e) * -120) + p2.R * 360;
            var seg = 360.0 / n;
            var sw = (seg - 38) * e;
            for (var k = 0; k < n; k++)
            {
                var a0 = rot + k * seg;
                var a1 = a0 + dir * sw;
                Stroke(env2, ArcP(cx, cy, r0, a0, a1, 30), Col(env2, "fg"), lw, a, false, cap: SKStrokeCap.Round);
                var an = a1 * J.DEG;
                var tx = -Math.Sin(an) * dir;
                var ty = Math.Cos(an) * dir;
                var px = cx + Math.Cos(an) * r0;
                var py = cy + Math.Sin(an) * r0;
                var hs = 7 * u * e;
                Polys(env2, new[] { new[] { new[] { px + tx * hs * 1.3, py + ty * hs * 1.3 }, new[] { px - tx * hs * 0.4 + Math.Cos(an) * hs, py - ty * hs * 0.4 + Math.Sin(an) * hs }, new[] { px - tx * hs * 0.4 - Math.Cos(an) * hs, py - ty * hs * 0.4 - Math.Sin(an) * hs } } }, ac, a);
            }
            env2.Circle(cx, cy, r0 * 1.32, null, Col(env2, "sub"), Math.Max(1, 0.8 * u), 0.35 * a * e, false);
            var fs = r0 * 0.5;
            var loops = 1 + (int)Math.Floor(env2.Lt / 1.1);
            Label(env2, $"×{loops}", cx, cy, JO("size", fs, "align", "center", "color", Col(env2, "fg"), "alpha", a * InE(env2, 0.3, 0.2), "track", 0.02));
        }),
    });

    private static void RegisterStarburst() => Reg0("starburst", new DecorDef
    {
        Name = "スターバースト",
        Tags = new[] { "pop", "graphic" },
        W = 0.8,
        Layer = "front",
        Draw = (env, p) => WithSettle(env, p, (env2, p2) =>
        {
            if (env2.Pass != "main") return;
            var bb = GetBB(env2, env2.Body);
            var sc = env2.Sc!;
            var ctx = env2.Ctx;
            var u = U(env2);
            var o = OutE(env2);
            if (o <= 0.003 || env2.Lt < 0) return;
            var r0 = J.Clamp(Math.Min(env2.W, env2.H) * 0.068, 44 * u, 88 * u);
            var sp = NearBB(env2, bb, r0 * 2.2, r0 * 2.2, p2, 14 * u);
            var a = o * (sp.Ok ? 1 : 0.35);
            var q = J.E.OutBack(J.Clamp((env2.Lt - 0.05) / 0.4), 2.2);
            if (q <= 0) return;
            var rot = (env2.Ltb * 9 + (1 - q) * -120 + p2.R * 30) * J.DEG;
            const int n = 22;
            var ac = Acc(env2);
            var pts = new List<double[]>();
            for (var i = 0; i < n * 2; i++)
            {
                var an = i / (double)(n * 2) * J.TAU;
                var r = i % 2 != 0 ? r0 * 0.83 : r0;
                pts.Add(new[] { Math.Cos(an) * r, Math.Sin(an) * r });
            }
            var txtC = J.Lum(ac) > 0.55 ? "#000000" : Col(env2, "bg");
            ctx.Save();
            ctx.Translate((float)sp.Cx, (float)sp.Cy);
            ctx.RotateDegrees((float)(rot / J.DEG));
            ctx.Scale((float)q, (float)q);
            Polys(env2, new[] { pts.ToArray() }, ac, 0.97 * a);
            env2.Circle(0, 0, r0 * 0.7, null, txtC, Math.Max(1, 1.2 * u), 0.55 * a, false);
            var v = p2.V % 5;
            var ch = LyricChar(env2, 9);
            var txt = new[] { $"No.{Pad2((env2.Cut?.Line ?? 0) + 1)}", "NEW", "HIT!", ch.Length > 0 ? ch : "♪", "LOVE" }[v];
            var font = v == 3 ? SerifF(env2) : DispF(env2);
            var fs = r0 * (v == 3 ? 0.7 : 0.42);
            var tw = TextW(txt, font, fs, 0.02);
            if (tw > r0 * 1.15) fs *= r0 * 1.15 / tw;
            ctx.RotateDegrees((float)(-rot / J.DEG * 0.85));
            env2.DrawItem(new JObject
            {
                ["text"] = txt, ["font"] = font, ["size"] = fs, ["x"] = 0, ["y"] = 0,
                ["color"] = txtC, ["alpha"] = a, ["track"] = 0.02, ["ghost"] = false,
            });
            ctx.Restore();
        }),
    });

    // 正字五画的笔画端点（单位坐标，JS SEI）
    private static readonly double[][][] Sei =
    {
        new[] { new[] { 0.1, 0.12 }, new[] { 0.9, 0.12 } },
        new[] { new[] { 0.5, 0.12 }, new[] { 0.5, 0.9 } },
        new[] { new[] { 0.5, 0.5 }, new[] { 0.84, 0.5 } },
        new[] { new[] { 0.22, 0.46 }, new[] { 0.22, 0.9 } },
        new[] { new[] { 0.02, 0.9 }, new[] { 0.98, 0.9 } },
    };

    private static void RegisterTally() => Reg0("tally", new DecorDef
    {
        Name = "正の字",
        Tags = new[] { "editorial", "pop", "emotional" },
        W = 0.8,
        Layer = "front",
        Draw = (env, p) => WithSettle(env, p, (env2, p2) =>
        {
            if (env2.Pass != "main") return;
            var bb = GetBB(env2, env2.Body);
            var sc = env2.Sc!;
            var u = U(env2);
            var o = OutE(env2);
            if (o <= 0.003 || env2.Lt < 0) return;
            var nc = 2 + p2.N % 2;
            var s = J.Clamp(Math.Min(env2.W, env2.H) * 0.064, 40 * u, 76 * u);
            var gp = s * 0.3;
            var fs = FS(env2) * 1.05;
            var w = nc * s + (nc - 1) * gp + fs * 3.2;
            var h = s * 1.1;
            var sp = SpotP(env2, bb, w, h, p2, 24 * u);
            var a = o * (sp.Ok ? 1 : 0.35);
            var total = nc * 5;
            var dt = J.Clamp(((env2.Cut?.Dur ?? 0) * 0.75 - 0.2) / total, 0.09, 0.26);
            var col = p2.Accent ? Acc(env2) : Col(env2, "fg");
            var lw = Math.Max(1.6, 3 * u);
            var count = 0;
            for (var c = 0; c < nc; c++)
            {
                var x0 = sp.X + c * (s + gp);
                var y0 = sp.Y + (h - s) / 2;
                var tilt = J.Rs(p2.Seed, c, 1) * 4 * J.DEG;
                for (var k = 0; k < 5; k++)
                {
                    var idx = c * 5 + k;
                    var t0 = 0.1 + idx * dt;
                    var e = J.E.OutCubic(J.Clamp((env2.Lt - t0) / Math.Min(0.14, dt * 0.9)));
                    if (e <= 0) continue;
                    if (e >= 1) count++;
                    double Jf(int i) => J.Rs(p2.Seed, idx, i) * 0.035;
                    var pts = Sei[k].Select((q, i) => new[] { q[0] + Jf(i * 2), q[1] + Jf(i * 2 + 1) })
                        .Select(q =>
                        {
                            var x = (q[0] - 0.5) * s;
                            var y = (q[1] - 0.5) * s;
                            return new[] { x0 + s / 2 + x * Math.Cos(tilt) - y * Math.Sin(tilt), y0 + s / 2 + x * Math.Sin(tilt) + y * Math.Cos(tilt) };
                        }).ToArray();
                    Stroke(env2, Part(pts, 0, e), col, lw, 0.95 * a, false, cap: SKStrokeCap.Round);
                }
            }
            var lx = sp.X + nc * (s + gp) - gp + fs * 0.8;
            Label(env2, "×", lx, sp.Y + h / 2, JO("size", fs, "alpha", a * InE(env2, 0.3, 0.1)));
            Label(env2, Pad2(count), lx + fs * 0.9, sp.Y + h / 2,
                JO("size", fs * 1.3, "color", count >= total ? Acc(env2) : Col(env2, "fg"), "alpha", a * InE(env2, 0.3, 0.1), "track", 0.04));
        }),
    });

    // 鼠标指针轮廓（单位坐标，JS CURSOR）
    private static readonly double[][] Cursor = { new[] { 0.0, 0.0 }, new[] { 0.0, 1.0 }, new[] { 0.27, 0.76 }, new[] { 0.45, 1.13 }, new[] { 0.6, 1.06 }, new[] { 0.42, 0.7 }, new[] { 0.74, 0.7 } };

    private static void RegisterCursorClick() => Reg0("cursorClick", new DecorDef
    {
        Name = "カーソル",
        Tags = new[] { "pop", "graphic", "glitch" },
        W = 0.8,
        Layer = "front",
        Draw = (env, p) =>
        {
            if (env.Pass != "main") return;
            var bb = GetBB(env, env.Body);
            var w2 = env.W;
            var h2 = env.H;
            var sc = env.Sc!;
            var u = U(env);
            var m = MG(env);
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0) return;
            var sc2 = J.Clamp(Math.Min(w2, h2) * 0.034, 24 * u, 42 * u);
            var ac = Acc(env);
            var marquee = p.V % 2 == 1;
            var pad = 14 * u + Bh(bb) * 0.06;
            var lw = Math.Max(1, 1.2 * u);
            double[] tip;
            if (marquee)
            {
                var a = new[] { J.Clamp(bb.X0 - pad, m * 0.4, w2), J.Clamp(bb.Y0 - pad, m * 0.4, h2) };
                var b = new[] { J.Clamp(bb.X1 + pad, 0.0, w2 - m * 0.4), J.Clamp(bb.Y1 + pad, 0.0, h2 - m * 0.4) };
                var enter = J.E.OutCubic(J.Clamp(env.Lt / 0.3));
                var drag = J.E.InOutCubic(J.Clamp((env.Lt - 0.3) / 0.6));
                var start = new[] { a[0] - 60 * u, a[1] - 90 * u };
                tip = drag <= 0 ? L2(start, a, enter) : L2(a, b, drag);
                if (env.Lt > 0.95)
                    tip = new[] { b[0] + 18 * u * J.E.OutCubic(J.Clamp((env.Lt - 0.95) / 0.4)), b[1] + 12 * u * J.E.OutCubic(J.Clamp((env.Lt - 0.95) / 0.4)) };
                if (drag > 0)
                {
                    var c = drag < 1 ? tip : b;
                    var rx0 = Math.Min(a[0], c[0]);
                    var ry0 = Math.Min(a[1], c[1]);
                    var rx1 = Math.Max(a[0], c[0]);
                    var ry1 = Math.Max(a[1], c[1]);
                    var ph = env.Ltb * 30 * u;
                    Dash(env, rx0, ry0, rx1, ry0, 6 * u, 4 * u, Col(env, "fg"), lw, 0.9 * o, ph);
                    Dash(env, rx1, ry0, rx1, ry1, 6 * u, 4 * u, Col(env, "fg"), lw, 0.9 * o, ph);
                    Dash(env, rx1, ry1, rx0, ry1, 6 * u, 4 * u, Col(env, "fg"), lw, 0.9 * o, ph);
                    Dash(env, rx0, ry1, rx0, ry0, 6 * u, 4 * u, Col(env, "fg"), lw, 0.9 * o, ph);
                    if (drag >= 1)
                    {
                        var hs = 3.5 * u;
                        var hq = J.E.OutBack(J.Clamp((env.Lt - 0.9) / 0.25), 2) * hs;
                        var hl = new List<double[]>();
                        foreach (var cpt in new[] { new[] { rx0, ry0 }, new[] { rx1, ry0 }, new[] { rx1, ry1 }, new[] { rx0, ry1 }, new[] { (rx0 + rx1) / 2, ry0 }, new[] { (rx0 + rx1) / 2, ry1 }, new[] { rx0, (ry0 + ry1) / 2 }, new[] { rx1, (ry0 + ry1) / 2 } })
                            hl.Add(new[] { cpt[0] - hq, cpt[1] - hq, hq * 2, hq * 2 });
                        Rects(env, hl, ac, o);
                        Label(env, $"{Math.Round(rx1 - rx0)} × {Math.Round(ry1 - ry0)}", rx1, ry1 + FS(env) * 1.1,
                            JO("size", FS(env) * 0.8, "align", "right", "color", Col(env, "fg"), "alpha", o * J.Clamp((env.Lt - 1) / 0.3)));
                    }
                }
            }
            else
            {
                var sx = p.Right ? 1 : -1;
                var target = new[] { sx > 0 ? bb.X1 + 10 * u : bb.X0 - 10 * u - sc2 * 0.7, (p.Low ? bb.Y1 : bb.Y0) + (p.Low ? 8 * u : -sc2 * 1.2) };
                target[0] = J.Clamp(target[0], m * 0.5, w2 - m * 0.5 - sc2);
                target[1] = J.Clamp(target[1], m * 0.5, h2 - m * 0.5 - sc2 * 1.2);
                var from = new[] { target[0] + sx * w2 * 0.22, target[1] + h2 * 0.18 };
                var mv = J.E.OutCubic(J.Clamp(env.Lt / 0.55));
                tip = new[] { J.Lerp(from[0], target[0], mv) + Math.Sin(mv * Math.PI) * 30 * u, J.Lerp(from[1], target[1], mv) };
                var away = J.E.InCubic(env.POut);
                tip = new[] { tip[0] + sx * away * 60 * u, tip[1] + away * 40 * u };
                foreach (var tc in new[] { 0.62, 0.84, 1.9 })
                {
                    var q = (env.Lt - tc) / 0.5;
                    if (q <= 0 || q >= 1) continue;
                    env.Circle(target[0], target[1], (6 + 26 * J.E.OutCubic(q)) * u, null, ac, 2 * u * (1 - q), o * (1 - q), false);
                }
                var tq = J.Clamp((env.Lt - 0.9) / 0.25);
                if (tq > 0)
                {
                    var txt = $"LYRIC {Pad2((env.Cut?.Line ?? 0) + 1)}";
                    var fs = FS(env) * 0.85;
                    var tw = TextW(txt, MonoF(env), fs, 0.1) + 16 * u;
                    var th = fs * 1.9;
                    var bx = J.Clamp(target[0] + sc2 * 0.9, m * 0.4, w2 - m * 0.4 - tw);
                    var by = J.Clamp(target[1] + sc2 * 1.25, m * 0.4, h2 - m * 0.4 - th);
                    if (!HitBb(bx, by, bx + tw, by + th, bb, 2))
                    {
                        RrFill(env, bx, by, tw, th * J.E.OutBack(tq, 1.5), 4 * u, Col(env, "fg"), 0.92 * o);
                        Label(env, txt, bx + tw / 2, by + th / 2, JO("size", fs, "align", "center", "color", Col(env, "bg"), "alpha", o * tq, "track", 0.1));
                    }
                }
            }
            var click = !marquee && new[] { 0.62, 0.84, 1.9 }.Any(tc => env.Lt > tc && env.Lt < tc + 0.1);
            var s = sc2 * (click ? 0.86 : 1);
            var pts = Cursor.Select(q => new[] { tip[0] + q[0] * s, tip[1] + q[1] * s }).ToArray();
            Polys(env, new[] { pts.Select(q => new[] { q[0] + 3 * u, q[1] + 4 * u }).ToArray() }, Col(env, "sub"), 0.3 * o);
            Polys(env, new[] { pts }, Col(env, "fg"), o);
            Strokes(env, new[] { pts }, Col(env, "bg"), Math.Max(1, 1.4 * u), o, false, close: true, join: SKStrokeJoin.Round);
        },
    });

    private static void RegisterWindowChrome() => Reg0("windowChrome", new DecorDef
    {
        Name = "ウィンドウ",
        Tags = new[] { "pop", "graphic", "glitch" },
        W = 0.8,
        Layer = "front",
        Draw = (env, p) => WithSettle(env, p, (env2, p2) =>
        {
            if (env2.Pass != "main") return;
            var bb = GetBB(env2, env2.Body);
            var w2 = env2.W;
            var h2 = env2.H;
            var sc = env2.Sc!;
            var u = U(env2);
            var m = MG(env2);
            var o = OutE(env2);
            if (o <= 0.003 || env2.Lt < 0) return;
            var bar = J.Clamp(Math.Min(w2, h2) * 0.036, 26 * u, 44 * u);
            var px = 30 * u + Bw(bb) * 0.03;
            var py = 20 * u + Bh(bb) * 0.08;
            var x0 = bb.X0 - px;
            var x1 = bb.X1 + px;
            var y0 = bb.Y0 - py - bar;
            var y1 = bb.Y1 + py + bar * 0.6;
            x0 = Math.Max(x0, m * 0.35);
            x1 = Math.Min(x1, w2 - m * 0.35);
            y0 = Math.Max(y0, m * 0.35);
            y1 = Math.Min(y1, h2 - m * 0.35);
            if (y0 + bar > bb.Y0 - 4 * u) y0 = bb.Y0 - 4 * u - bar;
            var e = J.E.OutExpo(J.Clamp(env2.Lt / 0.45)) * (1 - 0.06 * J.E.InCubic(env2.POut));
            var s = 0.94 + 0.06 * e;
            var cx = (x0 + x1) / 2;
            var cy = (y0 + y1) / 2;
            double[] T(double x, double y) => new[] { cx + (x - cx) * s, cy + (y - cy) * s };
            var t0 = T(x0, y0);
            var t1 = T(x1, y1);
            var lw = Math.Max(1, 1.4 * u);
            var a = o * J.Clamp(env2.Lt / 0.15);
            var r = 8 * u;
            var ac = Acc(env2);
            var mac = p2.V % 2 == 0;
            RrStroke(env2, t0[0], t0[1], t1[0] - t0[0], t1[1] - t0[1], r, Col(env2, "fg"), lw, 0.9 * a);
            RrFill(env2, t0[0], t0[1], t1[0] - t0[0], bar * s, r, Col(env2, "fg"), 0.1 * a);
            Segs(env2, new[] { new[] { t0[0], t0[1] + bar * s, t1[0], t0[1] + bar * s } }, Col(env2, "fg"), lw, 0.8 * a);
            var be = InE(env2, 0.3, 0.2);
            var by = t0[1] + bar * s / 2;
            var br = bar * 0.17;
            if (mac)
            {
                string[] cs2 = { ac, Acc2(env2), Col(env2, "sub") ?? "#888888" };
                for (var k = 0; k < 3; k++)
                    env2.Circle(t0[0] + bar * 0.55 + k * br * 3, by, br * be, cs2[k], null, 0, a, false);
            }
            else
            {
                var bx = t1[0] - bar * 0.6;
                var k = br * be;
                Segs(env2, new[] { new[] { bx - k, by - k, bx + k, by + k }, new[] { bx - k, by + k, bx + k, by - k } }, Col(env2, "fg"), lw, a);
                RrStroke(env2, bx - bar * 0.95 - k, by - k, k * 2, k * 2, 0, Col(env2, "fg"), lw, a);
                Segs(env2, new[] { new[] { bx - bar * 1.9 - k, by, bx - bar * 1.9 + k, by } }, Col(env2, "fg"), lw, a);
            }
            var fs = FS(env2) * 0.92;
            var title = $"lyric_{Pad2((env2.Cut?.Line ?? 0) + 1)}.txt";
            Label(env2, title, (t0[0] + t1[0]) / 2, by, JO("size", fs, "align", "center", "color", Col(env2, "fg"), "alpha", a * be, "track", 0.08));
            var sbx = t1[0] - 9 * u;
            var st = t0[1] + bar * s + 8 * u;
            var sbH = t1[1] - 8 * u - st;
            if (sbH > 30 * u && sbx > bb.X1 + 12 * u)
            {
                Segs(env2, new[] { new[] { sbx, st, sbx, st + sbH * be } }, Col(env2, "sub"), Math.Max(1, u), 0.4 * a);
                var th = sbH * 0.35;
                var ty = st + (sbH - th) * J.Clamp(env2.Lt / Math.Max(1, env2.Cut?.Dur ?? 1));
                RrFill(env2, sbx - 2 * u, ty, 4 * u, th * be, 2 * u, Col(env2, "sub"), 0.8 * a);
            }
            if (t1[1] - bb.Y1 > fs * 1.4)
                Label(env2, $"Ln {Pad2((env2.Cut?.Line ?? 0) + 1)}, Col {JLayouts.CodePoints(env2.Cut?.Text ?? "").Count}   UTF-8",
                    t1[0] - 14 * u, t1[1] - fs * 0.8, JO("size", fs * 0.8, "align", "right", "alpha", a * be * 0.8, "track", 0.08));
        }),
    });

    private static void RegisterProgressBar() => Reg0("progressBar", new DecorDef
    {
        Name = "読み込みバー",
        Tags = new[] { "pop", "graphic", "glitch" },
        W = 0.8,
        Layer = "front",
        Draw = (env, p) => WithSettle(env, p, (env2, p2) =>
        {
            if (env2.Pass != "main") return;
            var bb = GetBB(env2, env2.Body);
            var w2 = env2.W;
            var sc = env2.Sc!;
            var ctx = env2.Ctx;
            var u = U(env2);
            var o = OutE(env2);
            if (o <= 0.003 || env2.Lt < 0) return;
            var fs = FS(env2) * 0.9;
            var th = J.Clamp(Math.Min(w2, env2.H) * 0.014, 10 * u, 18 * u);
            var w = Math.Min(Math.Min(w2 * 0.4, 460 * u), Math.Max(Bw(bb) * 0.65, 260 * u));
            var h = th + fs * 3.2;
            var sp = BandSpot(env2, bb, w, h, p2, 26 * u);
            var a = o * (sp.Ok ? 1 : 0.35);
            var ac = Acc(env2);
            var x0 = sp.X;
            var ty = sp.Y + fs * 1.5;
            var e = J.E.OutExpo(J.Clamp(env2.Lt / 0.45));
            var tw = w * e;
            var tx = x0 + (w - tw) / 2;
            var t = Math.Max(0.8, (env2.Cut?.Dur ?? 0) * 0.85 - 0.3);
            var prog = 0.0;
            for (var k = 0; k < 5; k++)
            {
                var t0 = 0.25 + t * k / 5.0 + J.R(p2.Seed, k, 1) * t * 0.08;
                var d = t / 5 * (0.35 + J.R(p2.Seed, k, 2) * 0.4);
                prog += 0.2 * J.E.InOutCubic(J.Clamp((env2.Lt - t0) / d));
            }
            prog = J.Clamp(prog);
            RrStroke(env2, tx, ty, tw, th, th / 2, Col(env2, "fg"), Math.Max(1, 1.2 * u), 0.8 * a);
            var fw = Math.Max(0, (tw - 4 * u) * prog);
            if (fw > 1)
            {
                ctx.Save();
                using (var clip = new SKPath())
                {
                    AddRr(clip, tx + 2 * u, ty + 2 * u, fw, th - 4 * u, (th - 4 * u) / 2);
                    ctx.ClipPath(clip, antialias: true);
                }
                Rects(env2, new[] { new[] { tx, ty, tw, th } }, ac, a);
                var st = new List<double[]>();
                var gap = 12 * u;
                var off = env2.Ltb * 26 * u % gap;
                for (var x = tx - th + off; x < tx + fw + th; x += gap) st.Add(new[] { x, ty + th, x + th, ty });
                Segs(env2, st, J.Lum(ac) > 0.5 ? "#000000" : "#ffffff", 4 * u, 0.16 * a);
                ctx.Restore();
            }
            var done = prog >= 0.999;
            var le = InE(env2, 0.3, 0.15);
            var dots3 = new string('.', Math.Max(0, (int)Math.Floor(env2.Ltb * 3)) % 4); // 钳负值（JS Ltb 恒正不触发；C# 直调测试会碰负值）
            Label(env2, done ? "Complete" : "Loading" + dots3, tx, sp.Y + fs * 0.6,
                JO("size", fs, "font", BodyF(env2), "color", Col(env2, "fg"), "alpha", a * le, "track", 0.04));
            Label(env2, $"{(int)Math.Floor(prog * 100)}%", tx + tw, sp.Y + fs * 0.6,
                JO("size", fs, "align", "right", "color", done ? ac : Col(env2, "fg"), "alpha", a * le));
            var tot = 8 + (J.H(p2.Seed, 3) % 400) / 10.0;
            Label(env2, $"{(tot * prog).ToString("F1", CultureInfo.InvariantCulture)} MB / {tot.ToString("F1", CultureInfo.InvariantCulture)} MB", tx + tw, ty + th + fs * 1.1,
                JO("size", fs * 0.78, "align", "right", "alpha", a * le * 0.85));
            if (done)
            {
                var ck = new[] { new[] { tx + 2 * u, ty + th + fs * 1.1 }, new[] { tx + 6 * u, ty + th + fs * 1.1 + 4 * u }, new[] { tx + 13 * u, ty + th + fs * 1.1 - 5 * u } };
                Stroke(env2, Part(ck, 0, J.Clamp((env2.Lt - t - 0.3) * 4)), ac, 2 * u, a, false, cap: SKStrokeCap.Round, join: SKStrokeJoin.Round);
            }
        }),
    });

    private static readonly string[][] ToggleLabels =
    {
        new[] { "SHUFFLE", "REPEAT", "LYRICS" }, new[] { "想い", "記憶", "未練" },
        new[] { "LOVE", "MEMORY", "REPLAY" }, new[] { "声", "光", "夜" },
    };

    private static void RegisterToggleSwitch() => Reg0("toggleSwitch", new DecorDef
    {
        Name = "トグル",
        Tags = new[] { "pop", "graphic" },
        W = 0.7,
        Layer = "front",
        Draw = (env, p) => WithSettle(env, p, (env2, p2) =>
        {
            if (env2.Pass != "main") return;
            var bb = GetBB(env2, env2.Body);
            var sc = env2.Sc!;
            var u = U(env2);
            var o = OutE(env2);
            if (o <= 0.003 || env2.Lt < 0) return;
            var n = 2 + p2.N % 2;
            var th = J.Clamp(Math.Min(env2.W, env2.H) * 0.034, 24 * u, 44 * u);
            var tw = th * 1.8;
            var rowH = th * 1.7;
            var fs = th * 0.58;
            var labs = ToggleLabels[p2.V % ToggleLabels.Length];
            var lw0 = labs.Take(n).Max(l => TextW(l, BodyF(env2), fs, 0.12));
            var w = lw0 + 16 * u + tw;
            var h = n * rowH;
            var sp = SpotP(env2, bb, w, h, p2, 24 * u);
            var a = o * (sp.Ok ? 1 : 0.35);
            var ac = Acc(env2);
            var knobC = J.Lum(ac) > 0.6 ? "#000000" : Col(env2, "bg");
            for (var i = 0; i < n; i++)
            {
                var q = J.E.OutCubic(J.Clamp((env2.Lt - i * 0.08) / 0.3));
                if (q <= 0) continue;
                var y = sp.Y + i * rowH + (rowH - th) / 2;
                var x = sp.X + w - tw;
                var on = J.E.OutBack(J.Clamp((env2.Lt - 0.45 - i * 0.32) / 0.25), 1.6);
                Label(env2, labs[i], sp.X, y + th / 2, JO("size", fs, "font", BodyF(env2), "color", Col(env2, "fg"), "alpha", a * q, "track", 0.12));
                RrFill(env2, x, y, tw, th, th / 2, ac, a * J.Clamp(on) * q);
                RrStroke(env2, x, y, tw, th, th / 2, J.Clamp(on) > 0.5 ? ac : Col(env2, "sub"), Math.Max(1, 1.2 * u), a * q);
                var kx = x + th / 2 + (tw - th) * on;
                env2.Circle(kx, y + th / 2, th * 0.38 * q, J.Clamp(on) > 0.5 ? knobC : Col(env2, "fg"), null, 0, a, false);
                if (i < n - 1)
                    Segs(env2, new[] { new[] { sp.X, sp.Y + (i + 1) * rowH, sp.X + w * q, sp.Y + (i + 1) * rowH } }, Col(env2, "sub"), Math.Max(1, 0.8 * u), 0.3 * a);
            }
        }),
    });

    private static void RegisterNotifBell() => Reg0("notifBell", new DecorDef
    {
        Name = "通知",
        Tags = new[] { "pop", "emotional" },
        W = 0.7,
        Layer = "front",
        Draw = (env, p) => WithSettle(env, p, (env2, p2) =>
        {
            if (env2.Pass != "main") return;
            var bb = GetBB(env2, env2.Body);
            var sc = env2.Sc!;
            var ctx = env2.Ctx;
            var u = U(env2);
            var o = OutE(env2);
            if (o <= 0.003 || env2.Lt < 0) return;
            var s = J.Clamp(Math.Min(env2.W, env2.H) * 0.075, 48 * u, 92 * u);
            var fs = FS(env2) * 0.95;
            var sp = SpotP(env2, bb, s * 1.9, s * 1.7 + fs * 1.8, p2, 22 * u);
            var a = o * (sp.Ok ? 1 : 0.35);
            var ac = Acc(env2);
            var cx = sp.Cx - s * 0.15;
            var cy = sp.Y + s * 0.95;
            const double step = 0.5;
            var k = Math.Max(0, (int)Math.Floor((env2.Lt - 0.35) / step) + 1);
            var maxN = 3 + (int)(J.H(p2.Seed, 2) % 7);
            var cnt = Math.Min(maxN, k);
            var since = env2.Lt - (0.35 + (Math.Min(k, maxN) - 1) * step);
            var ring = cnt > 0 ? Math.Exp(-since * 5) * Math.Sin(since * 30) * 16 : 0;
            var e = J.E.OutBack(J.Clamp(env2.Lt / 0.4), 1.6);
            var body = new List<double[]> { new[] { -0.44, 0.36 } };
            body.AddRange(Bez(new[] { -0.34, 0.26 }, new[] { -0.38, -0.12 }, new[] { -0.3, -0.45 }, new[] { 0, -0.47 }, 12));
            body.AddRange(Bez(new[] { 0, -0.47 }, new[] { 0.3, -0.45 }, new[] { 0.38, -0.12 }, new[] { 0.34, 0.26 }, 12));
            body.Add(new[] { 0.44, 0.36 });
            ctx.Save();
            ctx.Translate((float)cx, (float)(cy - s * 0.5));
            ctx.RotateDegrees((float)ring);
            ctx.Translate(0, (float)(s * 0.5));
            ctx.Scale((float)e, (float)e);
            var pts = body.Select(q => new[] { q[0] * s, q[1] * s }).ToArray();
            Stroke(env2, pts.Concat(new[] { pts[0] }).ToArray(), Col(env2, "fg"), Math.Max(1.4, 2 * u), a, false, join: SKStrokeJoin.Round, cap: SKStrokeCap.Round);
            env2.Circle(0, -0.52 * s, 0.06 * s, null, Col(env2, "fg"), Math.Max(1.2, 1.6 * u), a, false);
            env2.Arc(Math.Sin(ring * J.DEG * 2) * s * 0.08, 0.44 * s, 0.1 * s, 0, 180, Col(env2, "fg"), Math.Max(1.4, 2 * u), a, false);
            ctx.Restore();
            if (cnt > 0)
            {
                var bump = 1 + 0.35 * Math.Exp(-since * 9);
                var br = s * 0.26 * bump;
                var bx = cx + s * 0.36;
                var by = cy - s * 0.42;
                env2.Circle(bx, by, br, ac, null, 0, a, false);
                env2.Circle(bx, by, br + 2 * u, null, Col(env2, "bg"), 2 * u, a, false);
                Label(env2, cnt >= 9 ? "9+" : cnt.ToString(CultureInfo.InvariantCulture), bx, by,
                    JO("size", br * 1.15, "font", DispF(env2), "align", "center", "color", J.Lum(ac) > 0.6 ? "#000000" : Col(env2, "bg"), "alpha", a, "track", 0.0));
            }
            Label(env2, $"通知 {Pad2(cnt)}件", cx, cy + s * 0.72 + fs * 0.8,
                JO("size", fs, "font", BodyF(env2), "align", "center", "alpha", a * InE(env2, 0.3, 0.3), "track", 0.1));
        }),
    });

    private static double[][] HeartPts(double cx, double cy, double s)
    {
        var o = new List<double[]>();
        for (var i = 0; i < 36; i++)
        {
            var t = i / 36.0 * J.TAU;
            var x = 16 * Math.Pow(Math.Sin(t), 3);
            var y = 13 * Math.Cos(t) - 5 * Math.Cos(2 * t) - 2 * Math.Cos(3 * t) - Math.Cos(4 * t);
            o.Add(new[] { cx + x * s / 17, cy - y * s / 17 });
        }
        return o.ToArray();
    }

    private static void RegisterLikeCounter() => Reg0("likeCounter", new DecorDef
    {
        Name = "いいね",
        Tags = new[] { "pop", "emotional" },
        W = 0.8,
        Layer = "front",
        Draw = (env, p) => WithSettle(env, p, (env2, p2) =>
        {
            if (env2.Pass != "main") return;
            var bb = GetBB(env2, env2.Body);
            var sc = env2.Sc!;
            var ctx = env2.Ctx;
            var u = U(env2);
            var o = OutE(env2);
            if (o <= 0.003 || env2.Lt < 0) return;
            var s = J.Clamp(Math.Min(env2.W, env2.H) * 0.042, 28 * u, 52 * u);
            var fs = s * 0.85;
            var font = MonoF(env2);
            var basen = 1000 + (int)(J.H(p2.Seed, 5) % 90000);
            const double tLike = 0.45;
            int Inc(double t) => t < tLike ? 0 : 1 + (int)Math.Floor(Math.Max(0, t - tLike - 0.4) / 0.38) * (1 + (int)(J.H(p2.Seed, 6) % 3));
            static string Fmt(int n) => n.ToString("N0", new CultureInfo("en-US"));
            var txtMax = Fmt(basen + Inc(env2.Cut?.Dur ?? 0));
            var w = s * 2.4 + TextW(txtMax, font, fs, 0.04) + 10 * u;
            var h = s * 2.4;
            var sp = SpotP(env2, bb, w, h, p2, 20 * u);
            var a = o * (sp.Ok ? 1 : 0.35);
            var ac = Acc(env2);
            var hx = sp.X + s * 1.2;
            var hy = sp.Cy;
            var liked = J.Clamp((env2.Lt - tLike) / 0.3);
            var pop = liked > 0 ? 1 + 0.35 * Math.Sin(Math.PI * J.Clamp(liked * 1.4)) : J.E.OutBack(J.Clamp(env2.Lt / 0.3), 1.6);
            var hp = HeartPts(hx, hy + s * 0.05, s * pop);
            if (liked > 0) Polys(env2, new[] { hp }, ac, a);
            Stroke(env2, hp.Concat(new[] { hp[0] }).ToArray(), liked > 0 ? ac : Col(env2, "fg"), Math.Max(1.4, 2 * u), a, false, join: SKStrokeJoin.Round);
            if (liked > 0 && liked < 1)
            {
                env2.Circle(hx, hy, s * (0.6 + 0.8 * liked), null, ac, 2 * u * (1 - liked), a * (1 - liked), false);
                var b = new List<double[]>();
                for (var k = 0; k < 7; k++)
                {
                    var an = (-90 + k * 360 / 7.0) * J.DEG;
                    var d0 = s * (0.9 + 0.7 * J.E.OutCubic(liked));
                    b.Add(new[] { hx + Math.Cos(an) * d0, hy + Math.Sin(an) * d0, 2.4 * u * (1 - liked) });
                }
                Dots(env2, b, ac, a);
            }
            var t = env2.Lt;
            var cur = basen + Inc(t);
            var prev = basen + Inc(Math.Max(0, t - 0.16));
            var lx = hx + s * 1.25;
            var ly = hy;
            ctx.Save();
            ctx.ClipRect(new SKRect((float)(lx - 2 * u), (float)(ly - fs * 0.75), (float)(lx - 2 * u + w), (float)(ly + fs * 0.75)));
            if (cur != prev)
            {
                var f = J.E.OutCubic(J.Clamp(((t - tLike - 0.4) % 0.38 + 0.38) % 0.38 / 0.16));
                Label(env2, Fmt(prev), lx, ly - fs * 1.2 * f, JO("size", fs, "font", font, "color", Col(env2, "fg"), "alpha", a * (1 - f), "track", 0.04));
                Label(env2, Fmt(cur), lx, ly + fs * 1.2 * (1 - f), JO("size", fs, "font", font, "color", liked > 0 ? ac : Col(env2, "fg"), "alpha", a * f, "track", 0.04));
            }
            else
                Label(env2, Fmt(cur), lx, ly, JO("size", fs, "font", font, "color", liked > 0 ? ac : Col(env2, "fg"), "alpha", a * InE(env2, 0.3, 0.1), "track", 0.04));
            ctx.Restore();
        }),
    });

    private static void RegisterMediaControls() => Reg0("mediaControls", new DecorDef
    {
        Name = "再生ボタン",
        Tags = new[] { "pop", "graphic", "emotional" },
        W = 0.8,
        Layer = "front",
        Draw = (env, p) => WithSettle(env, p, (env2, p2) =>
        {
            if (env2.Pass != "main") return;
            var bb = GetBB(env2, env2.Body);
            var sc = env2.Sc!;
            var u = U(env2);
            var o = OutE(env2);
            if (o <= 0.003 || env2.Lt < 0) return;
            var r0 = J.Clamp(Math.Min(env2.W, env2.H) * 0.04, 28 * u, 52 * u);
            var fs = FS(env2) * 0.9;
            var w = r0 * 6.4;
            var h = r0 * 2.2 + fs * 1.8;
            var sp = BandSpot(env2, bb, w, h, p2, 34 * u);
            var a = o * (sp.Ok ? 1 : 0.35);
            var ac = Acc(env2);
            var cx = sp.Cx;
            var cy = sp.Y + r0 * 1.1;
            var e = J.E.OutBack(J.Clamp(env2.Lt / 0.35), 1.6);
            var lw = Math.Max(1.2, 1.6 * u);
            const double tc = 0.55;
            var mmm = J.E.InOutCubic(J.Clamp((env2.Lt - tc) / 0.22));
            env2.Circle(cx, cy, r0 * e, null, Col(env2, "fg"), lw, a, false);
            if (env2.Lt > tc)
            {
                var q = J.Clamp((env2.Lt - tc) / 0.45);
                env2.Circle(cx, cy, r0 * (1 + 0.5 * J.E.OutCubic(q)), null, ac, 2 * u * (1 - q), a * (1 - q), false);
            }
            var s = r0 * 0.42 * e;
            double[][] a0p = { new[] { -0.35, -0.5 }, new[] { 0.075, -0.25 }, new[] { 0.075, 0.25 }, new[] { -0.35, 0.5 } };
            double[][] b0p = { new[] { 0.075, -0.25 }, new[] { 0.5, 0.0 }, new[] { 0.5, 0.0 }, new[] { 0.075, 0.25 } };
            double[][] a1p = { new[] { -0.4, -0.5 }, new[] { -0.12, -0.5 }, new[] { -0.12, 0.5 }, new[] { -0.4, 0.5 } };
            double[][] b1p = { new[] { 0.12, -0.5 }, new[] { 0.4, -0.5 }, new[] { 0.4, 0.5 }, new[] { 0.12, 0.5 } };
            double[][] Mix(double[][] p0v, double[][] p1v) => p0v.Select((q, i) => new[]
            {
                cx + J.Lerp(q[0], p1v[i][0], mmm) * s * 2 + (1 - mmm) * s * 0.12,
                cy + J.Lerp(q[1], p1v[i][1], mmm) * s * 2,
            }).ToArray();
            Polys(env2, new[] { Mix(a0p, a1p), Mix(b0p, b1p) }, Col(env2, "fg"), a);
            void Side(int dir, int k)
            {
                var q = J.E.OutBack(J.Clamp((env2.Lt - 0.1 - k * 0.06) / 0.3), 1.6);
                if (q <= 0) return;
                var x = cx + dir * r0 * 2.3;
                var ss = r0 * 0.36 * q;
                Polys(env2, new[] { new[] { new[] { x - dir * ss * 0.6, cy - ss }, new[] { x + dir * ss * 0.9, cy }, new[] { x - dir * ss * 0.6, cy + ss } } }, Col(env2, "fg"), a);
                Rects(env2, new[] { new[] { x + dir * ss * 0.9 - (dir > 0 ? 0 : 2.4 * u), cy - ss, 2.4 * u, ss * 2 } }, Col(env2, "fg"), a);
            }
            Side(-1, 0);
            Side(1, 1);
            var tot = env2.Plan?.Duration > 0 ? env2.Plan.Duration : 200;
            var t = Math.Max(0, env2.T);
            static string Mmss(double x) => $"{(int)Math.Floor(x / 60)}:{Pad2(x % 60)}";
            Label(env2, $"{Mmss(t)} / {Mmss(tot)}", cx, cy + r0 * 1.1 + fs * 1.1,
                JO("size", fs, "align", "center", "color", Col(env2, "sub"), "alpha", a * InE(env2, 0.3, 0.2), "track", 0.1));
        }),
    });

    private static void RegisterVolumeBars() => Reg0("volumeBars", new DecorDef
    {
        Name = "音量",
        Tags = new[] { "pop", "glitch", "graphic" },
        W = 0.8,
        Layer = "front",
        Draw = (env, p) => WithSettle(env, p, (env2, p2) =>
        {
            if (env2.Pass != "main") return;
            var bb = GetBB(env2, env2.Body);
            var sc = env2.Sc!;
            var u = U(env2);
            var o = OutE(env2);
            if (o <= 0.003 || env2.Lt < 0) return;
            var s = J.Clamp(Math.Min(env2.W, env2.H) * 0.042, 28 * u, 52 * u);
            const int nb = 8;
            var bwid = s * 0.3;
            var gap = s * 0.18;
            var fs = FS(env2) * 0.9;
            var w = s * 1.9 + nb * (bwid + gap);
            var h = s * 1.8 + fs * 1.6;
            var sp = SpotP(env2, bb, w, h, p2, 22 * u);
            var a = o * (sp.Ok ? 1 : 0.35);
            var ac = Acc(env2);
            var x0 = sp.X;
            var yb = sp.Y + s * 1.6;
            var lw = Math.Max(1.2, 1.6 * u);
            var e = J.E.OutBack(J.Clamp(env2.Lt / 0.3), 1.6);
            var cy = yb - s * 0.55;
            Polys(env2, new[] { new[] { new[] { x0, cy - s * 0.2 * e }, new[] { x0 + s * 0.25, cy - s * 0.2 * e }, new[] { x0 + s * 0.6, cy - s * 0.48 * e }, new[] { x0 + s * 0.6, cy + s * 0.48 * e }, new[] { x0 + s * 0.25, cy + s * 0.2 * e }, new[] { x0, cy + s * 0.2 * e } } }, Col(env2, "fg"), a);
            var en = env2.Plan.Energy is { Length: > 0 } ? J.Clamp(env2.Energy * 1.4) : 0.55 + 0.4 * J.Noise1(env2.Ltb * 3, p2.Seed);
            var lvl = (int)Math.Round(J.Clamp(en) * nb * J.E.OutCubic(J.Clamp((env2.Lt - 0.15) / 0.5)));
            var arcs = new List<double[]>
            {
                new[] { x0 + s * 0.62, cy, s * 0.35, -45 * J.DEG, 45 * J.DEG },
                new[] { x0 + s * 0.62, cy, s * 0.62, -45 * J.DEG, 45 * J.DEG },
            };
            ArcsB(env2, lvl > nb / 2 ? arcs : lvl > 0 ? arcs.Take(1).ToList() : new List<double[]>(), Col(env2, "fg"), lw, a, false, SKStrokeCap.Round);
            var lit = new List<double[]>();
            var hot = new List<double[]>();
            var off = new List<double[]>();
            for (var i = 0; i < nb; i++)
            {
                var q = J.E.OutBack(J.Clamp((env2.Lt - 0.05 - i * 0.03) / 0.25), 1.6);
                if (q <= 0) continue;
                var bh0 = s * (0.3 + 1.3 * (i + 1) / (double)nb) * q;
                var x = x0 + s * 1.5 + i * (bwid + gap);
                (i < lvl ? (i == lvl - 1 ? hot : lit) : off).Add(new[] { x, yb - bh0, bwid, bh0 });
            }
            Rects(env2, off, Col(env2, "sub"), 0.3 * a);
            Rects(env2, lit, Col(env2, "fg"), 0.9 * a);
            Rects(env2, hot, ac, a);
            Label(env2, $"VOL {Pad2(lvl)}", x0, yb + fs * 1.0, JO("size", fs, "alpha", a * InE(env2, 0.3, 0.2), "track", 0.16));
        }),
    });

    private static void RegisterMusicNotes() => Reg0("musicNotes", new DecorDef
    {
        Name = "音符",
        Tags = new[] { "pop", "emotional", "calm" },
        W = 0.8,
        Layer = "front",
        Draw = (env, p) => WithSettle(env, p, (env2, p2) =>
        {
            if (env2.Pass != "main") return;
            var bb = GetBB(env2, env2.Body);
            var w2 = env2.W;
            var h2 = env2.H;
            var sc = env2.Sc!;
            var ctx = env2.Ctx;
            var u = U(env2);
            var o = OutE(env2);
            if (o <= 0.003 || env2.Lt < 0) return;
            var n = Math.Min(10, 5 + p2.N * 2);
            var s = J.Clamp(Math.Min(w2, h2) * 0.036, 24 * u, 44 * u);
            var pad = 26 * u + Bh(bb) * 0.15;
            var x0 = bb.X0 - pad;
            var x1 = bb.X1 + pad;
            var y0 = bb.Y0 - pad;
            var y1 = bb.Y1 + pad;
            var per = 2 * (x1 - x0 + y1 - y0);
            string[] cols = { Col(env2, "fg") ?? "#FFFFFF", Acc(env2), Acc2(env2) };
            for (var i = 0; i < n; i++)
            {
                double R(int k) => J.R(p2.Seed, i, k);
                var t = 2.2 + R(1) * 1.2;
                var tt = env2.Ltb - R(2) * 0.8;
                if (tt < 0) continue;
                var cyc = (int)Math.Floor(tt / t);
                var tau = (tt - cyc * t) / t;
                var pt = ((i + J.R(p2.Seed, i, cyc, 3)) / n) * per;
                double x, y;
                if (pt < x1 - x0) { x = x0 + pt; y = y0; }
                else if ((pt -= x1 - x0) < y1 - y0) { x = x1; y = y0 + pt; }
                else if ((pt -= y1 - y0) < x1 - x0) { x = x1 - pt; y = y1; }
                else { pt -= x1 - x0; x = x0; y = y1 - pt; }
                var @out = new[] { Math.Sign(x - (bb.X0 + bb.X1) / 2) != 0 ? Math.Sign(x - (bb.X0 + bb.X1) / 2) : 1, -1 };
                x += @out[0] * tau * 40 * u + Math.Sin(tau * J.TAU + i) * 12 * u;
                y += -tau * 90 * u;
                x = J.Clamp(x, s, w2 - s);
                y = J.Clamp(y, s * 1.5, h2 - s);
                var al = ClearOf(bb, x, y, s * 1.1, 20 * u) * Math.Sin(Math.PI * tau) * 1.4;
                if (al <= 0.02) continue;
                var q = J.E.OutBack(J.Clamp(tau * 5), 1.8);
                var s2 = s * (0.75 + 0.4 * J.R(p2.Seed, i, cyc, 4)) * q;
                var col = cols[i % 3];
                ctx.Save();
                ctx.Translate((float)x, (float)y);
                ctx.RotateDegrees((float)((Math.Sin(tau * 5 + i) * 14 - 6)));
                var type = (i + (int)(J.H(p2.Seed, cyc))) % 3;
                var a = o * Math.Min(1, al);
                var lw = Math.Max(1.2, s2 * 0.07);
                double[][] Head(double hx) => EllP(hx, 0, s2 * 0.3, s2 * 0.21, -22 * J.DEG, 0, 360, 16);
                if (type == 2)
                {
                    Polys(env2, new[] { Head(-s2 * 0.35), Head(s2 * 0.45) }, col, a);
                    Segs(env2, new[]
                    {
                        new[] { -s2 * 0.35 + s2 * 0.26, -s2 * 0.05, -s2 * 0.35 + s2 * 0.26, -s2 * 1.05 },
                        new[] { s2 * 0.45 + s2 * 0.26, -s2 * 0.05, s2 * 0.45 + s2 * 0.26, -s2 * 1.15 },
                    }, col, lw, a);
                    Polys(env2, new[] { new[] { new[] { -s2 * 0.09 - lw / 2, -s2 * 1.05 }, new[] { s2 * 0.71 + lw / 2, -s2 * 1.15 }, new[] { s2 * 0.71 + lw / 2, -s2 * 0.97 }, new[] { -s2 * 0.09 - lw / 2, -s2 * 0.87 } } }, col, a);
                }
                else
                {
                    Polys(env2, new[] { Head(0) }, col, a);
                    Segs(env2, new[] { new[] { s2 * 0.26, -s2 * 0.05, s2 * 0.26, -s2 * 1.05 } }, col, lw, a);
                    if (type == 0)
                        Stroke(env2, Bez(new[] { s2 * 0.26, -s2 * 1.05 }, new[] { s2 * 0.3, -s2 * 0.8 }, new[] { s2 * 0.72, -s2 * 0.72 }, new[] { s2 * 0.55, -s2 * 0.35 }, 10), col, lw * 1.3, a, false, cap: SKStrokeCap.Round);
                }
                ctx.Restore();
            }
        }),
    });
}
