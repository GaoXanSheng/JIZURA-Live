// 移植自 frontend/src/engine/11p_decor.js（decor 表现包 A，pack = 'decor'，45 条）。
//
// 契约与 JDecor.cs 相同：DecorDef.Draw(env, DecorSpec)，bb 从 env.Body 读（空时 getBB 回退
// 画面中央 + 按 cut 缓存最近有效框）。参数 P → DecorSpec（seed/n/corner/accent/right/low/big/
// mode/from/to/v/r）；随机全走 J.R/J.Rs/J.Rr/J.H 的无状态哈希（入参逐字保持，同 seed 同轨迹）。
//
// 本包私有 helper（JS L7-274 的顶层 const 与 function）见「本包 helper」区：
// 折线/批量段/批量圆/矩形/多边形合一个 SKPath 一次描边或填充（对位 Canvas2D beginPath 批画，
// 重叠多边形不会因逐个 fill 叠深）；part() 折线按长度占比截段；ghost 通道换色走 Env.ColOf。
//
// Env 缺口（本文件内补，不改 Env.cs）：
// 1. JS env.beat 是 {since,len,index}，C# Env.Beat 只装归一相位——BeatOf(env) 从
//    env.Plan.Beats + env.T 重建（与 P11LayoutsB.BeatOf 同口径，09_render 的 beatAt）。
// 2. JS env.energy != null 判定对位 env.Plan.Energy is { Length: > 0 }（C# Env.Energy 缺省 0）。
// 3. 每个 cut 记住最近有效 bbox 的 WeakMap → ConditionalWeakTable<Cut, BBox>。
//
// 桩/TODO：无跨文件桩。跨文件符号对位：J.measure→JLayouts.Measure、J.centerBB→JLayouts.CenterBb、
// J.fontsOf[0]→JLayouts.Font0、J.romaji/fmtTime→J.*、ctx.*→SKCanvas 同构直译。

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace JizuraLive.Engine;

public static class P11Decor
{
    private const string Pk = "decor";
    private static int _installed;

    // ================= 本包 helper（JS L7-274 逐条对位） =================

    private static double U(Env env) => Math.Min(env.W, env.H) / 1080.0; // 1080p 短边时为 1
    private static int MG(Env env) => (int)Math.Round(Math.Min(env.W, env.H) * 0.05, MidpointRounding.AwayFromZero); // 安全边距
    private static string MonoF(Env env) => JLayouts.Font0(env.St, "mono") ?? "mono";
    private static string BodyF(Env env) => JLayouts.Font0(env.St, "body") ?? "gothic_med";
    private static string SerifF(Env env) => JLayouts.Font0(env.St, "serif") ?? "mincho";
    private static string? Col(Env env, string k) => env.Sc?.Str(k);
    private static bool Dark(Env env) => J.Lum(Col(env, "bg") ?? "#000000") < 0.5;
    private static double InE(Env env, double d = 0.4, double delay = 0, Func<double, double>? ease = null) =>
        (ease ?? J.E.OutExpo)(J.Clamp((env.Lt - delay) / d));
    private static double OutE(Env env) => 1 - J.E.InCubic(env.POut);
    private static double[] L2(double[] a, double[] b, double t) => new[] { a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t };
    private static double FS(Env env) => Math.Max(12, 16 * U(env)); // 小标签字号

    // 每个 cut 记住最近一次有效 bbox，歌词隐藏期间 decor 不会跳回屏幕中心（JS WeakMap BBC）
    private static readonly ConditionalWeakTable<Cut, BBox> Bbc = new();

    private static BBox GetBB(Env env, BBox? bb)
    {
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

    private static bool HitBb(double x0, double y0, double x1, double y1, BBox bb, double pad = 0) =>
        !(x1 < bb.X0 - pad || x0 > bb.X1 + pad || y1 < bb.Y0 - pad || y0 > bb.Y1 + pad);

    private readonly record struct Spot(double X, double Y, double Cx, double Cy, bool Ok, int Sx, int Sy);

    /// <summary>在歌词旁找一个 w×h 的位置（安全边距内、不压歌词）。JS nearBB。</summary>
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

    /// <summary>安全边距内的屏幕角落，尽量避开歌词。JS cornerSpot。</summary>
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

    // ---- 批量绘制助手（合一个 SKPath，重叠多边形不叠深；colOf 走 Env.ColOf） ----

    /// <summary>画折线（close/cap/join 可设）；g=false 时只在 main pass 上色。JS stroke。</summary>
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

    /// <summary>一个路径里画一批直线段 [[x0,y0,x1,y1],…]。JS segs。</summary>
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

    /// <summary>一个路径里画一批实心圆 [[x,y,r],…]。JS dots。</summary>
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

    /// <summary>一批实心矩形 [[x,y,w,h],…]（一个 path）。JS rects。</summary>
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

    /// <summary>一个路径里画一批实心多边形（nonzero 填充，重叠处不叠深）。JS polys。</summary>
    private static void Polys(Env env, IEnumerable<double[]?[]> list, string? c, double a = 1, bool g = false,
        SKBlendMode? blend = null, SKImageFilter? filter = null)
    {
        var k = env.ColOf(c, g);
        var arr = list as ICollection<double[]?[]> ?? list?.ToArray();
        if (k == null || a <= 0.003 || arr == null || arr.Count == 0) return;
        using var path = new SKPath();
        foreach (var pts in arr)
        {
            if (pts == null || pts.Length < 3) continue;
            path.MoveTo((float)pts[0][0]!, (float)pts[0][1]!);
            for (var i = 1; i < pts.Length; i++) path.LineTo((float)pts[i][0]!, (float)pts[i][1]!);
            path.Close();
        }
        using var p = new SKPaint
        {
            IsAntialias = true,
            Color = Env.SkColor(k, Math.Min(1, a)),
            BlendMode = blend ?? env.Blend,
            ImageFilter = filter,
        };
        env.Ctx.DrawPath(path, p);
    }

    /// <summary>直线虚线，只画 main pass。JS dash。</summary>
    private static void Dash(Env env, double x0, double y0, double x1, double y1, double on, double off,
        string? c, double lw, double a = 1, double phase = 0)
    {
        if (env.Pass != "main" || a <= 0.003) return;
        var l = Hypot(x1 - x0, y1 - y0);
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

    /// <summary>取折线上长度占比 e0..e1 的一段。JS part。</summary>
    private static double[][] Part(IReadOnlyList<double[]> pts, double e0, double e1)
    {
        e0 = J.Clamp(e0);
        e1 = J.Clamp(e1);
        if (e1 <= e0 || pts.Count < 2) return Array.Empty<double[]>();
        var d = new double[pts.Count];
        for (var i = 1; i < pts.Count; i++)
            d[i] = d[i - 1] + Hypot(pts[i][0] - pts[i - 1][0], pts[i][1] - pts[i - 1][1]);
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

    /// <summary>小号辅助文字，永不 ghost（Object.assign 覆盖 → JObject 合并）。</summary>
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

    /// <summary>从歌词里挑一个字：优先汉字，其次假名。JS lyricChar。</summary>
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

    /// <summary>五角星折线（10 点）。JS star5。</summary>
    private static double[][] Star5(double cx, double cy, double r, double rr, double rot = -90)
    {
        var o = new List<double[]>();
        for (var i = 0; i < 10; i++)
        {
            var a = (rot + i * 36) * J.DEG;
            var q = i % 2 != 0 ? rr : r;
            o.Add(new[] { cx + Math.Cos(a) * q, cy + Math.Sin(a) * q });
        }
        return o.ToArray();
    }

    /// <summary>四角凹边星形。JS glint。</summary>
    private static double[][] Glint(double cx, double cy, double r, double w, double rot = 0)
    {
        var o = new List<double[]>();
        const int n = 4;
        for (var i = 0; i < n * 2; i++)
        {
            var a = rot * J.DEG + i * Math.PI / n;
            var q = i % 2 != 0 ? w : r;
            o.Add(new[] { cx + Math.Cos(a) * q, cy + Math.Sin(a) * q });
        }
        var @out = new List<double[]>();
        for (var i = 0; i < o.Count; i++)
        {
            @out.Add(o[i]);
            var mid = L2(o[i], o[(i + 1) % o.Count], 0.5);
            @out.Add(L2(mid, new[] { cx, cy }, 0.35));
        }
        return @out.ToArray();
    }

    /// <summary>心形折线（28 点）。JS heart。</summary>
    private static double[][] Heart(double cx, double cy, double s)
    {
        var o = new List<double[]>();
        for (var i = 0; i < 28; i++)
        {
            var t = i / 28.0 * J.TAU;
            var x = 16 * Math.Pow(Math.Sin(t), 3);
            var y = 13 * Math.Cos(t) - 5 * Math.Cos(2 * t) - 2 * Math.Cos(3 * t) - Math.Cos(4 * t);
            o.Add(new[] { cx + x * s / 17, cy - y * s / 17 });
        }
        return o.ToArray();
    }

    private static double[] Pt(double x, double y) => new[] { x, y };

    /// <summary>Math.Sqrt(dx²+dy²)（Math 没有 Hypot，与引擎其余文件同式）。</summary>
    private static double Hypot(double dx, double dy) => Math.Sqrt(dx * dx + dy * dy);

    /// <summary>JS env.beat（09_render 的 beatAt）本文件内的重建：{since, len, index}。</summary>
    internal readonly record struct BeatInfo(bool Has, double Since, double Len, int Index);

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

    /// <summary>有音频能量数据才走 energy 通道（JS env.energy != null）。</summary>
    private static bool HasEnergy(Env env) => env.Plan.Energy is { Length: > 0 };

    // ================= 注册（JS DEF 键序） =================

    /// <summary>幂等注册本文件全部 decor（顺序 = JS DEF 对象键序 = Object.keys 序）。</summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;
        RegisterCrosshair();
        RegisterCropMarks();
        RegisterReticle();
        RegisterRadar();
        RegisterProgressRing();
        RegisterTimecodeBar();
        RegisterRulerEdge();
        RegisterDimension();
        RegisterIndexNum();
        RegisterDateStamp();
        RegisterQrBlock();
        RegisterGlitchRects();
        RegisterConcentricSquares();
        RegisterTriangleSpin();
        RegisterLineBurst();
        RegisterPlusGrid();
        RegisterGuides();
        RegisterWaveLine();
        RegisterSpiralLine();
        RegisterHalftonePatch();
        RegisterCheckerStrip();
        RegisterBeatRing();
        RegisterOrbitDots();
        RegisterConstellation();
        RegisterConfetti();
        RegisterPetals();
        RegisterRainStreaks();
        RegisterSnow();
        RegisterLightLeak();
        RegisterBokeh();
        RegisterSpeedCorner();
        RegisterRisingParticles();
        RegisterTwinkle();
        RegisterBrushStroke();
        RegisterTapePieces();
        RegisterScribbleCircle();
        RegisterScribbleUnder();
        RegisterCrossOut();
        RegisterHighlightMark();
        RegisterHeartsStars();
        RegisterWatermarkKanji();
        RegisterVerticalStrip();
        RegisterRomajiLine();
        RegisterBracketsJp();
        RegisterSeal();
    }

    private static void Reg0(string key, DecorDef def) => Reg.Register("decor", key, def, Pk);

    // ---------------- crosshair（照準線） ----------------

    private static void RegisterCrosshair() => Reg0("crosshair", new DecorDef
    {
        Name = "照準線",
        Tags = new[] { "graphic", "editorial", "glitch" },
        W = 1,
        Layer = "front",
        Draw = (env, p) =>
        {
            if (env.Pass != "main") return;
            var bb = GetBB(env, env.Body);
            var sc = env.Sc!;
            var u = U(env);
            var m = MG(env);
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0) return;
            var cx = (bb.X0 + bb.X1) / 2;
            var cy = (bb.Y0 + bb.Y1) / 2;
            var gx = 26 * u + Bw(bb) * 0.02;
            var gy = 22 * u + Bh(bb) * 0.1;
            var v = p.V % 3;
            var lw = Math.Max(1, 1.3 * u);
            double[][] a = { new[] { cx, bb.Y0 - gy }, new[] { cx, bb.Y1 + gy }, new[] { bb.X0 - gx, cy }, new[] { bb.X1 + gx, cy } };
            double[][] b = { new[] { cx, m * 0.5 }, new[] { cx, env.H - m * 0.5 }, new[] { m * 0.5, cy }, new[] { env.W - m * 0.5, cy } };
            var pOut = J.E.InCubic(env.POut);
            for (var i = 0; i < 4; i++)
            {
                var len = Hypot(b[i][0] - a[i][0], b[i][1] - a[i][1]);
                if (len < 40 * u) continue;
                var ee = J.E.OutExpo(J.Clamp((env.Lt - i * 0.06) / 0.6));
                if (ee <= pOut) continue;
                Stroke(env, new[] { L2(a[i], b[i], pOut), L2(a[i], b[i], ee) }, Col(env, "sub"), lw, 0.75);
                var dx = (b[i][0] - a[i][0]) / len;
                var dy = (b[i][1] - a[i][1]) / len;
                var nx = -dy;
                var ny = dx;
                if (v != 2 || i < 2)
                {
                    var st = 10 * u;
                    var nt = Math.Min(20, Math.Floor(len * 0.5 / st));
                    var list = new List<double[]>();
                    for (var t = 1; t <= nt; t++)
                    {
                        var d = t * st;
                        if (d > len * ee || d < len * pOut) continue;
                        var tl = (t % 5 == 0 ? 8 : 3.5) * u * J.Clamp((ee * len - d) / (40 * u));
                        list.Add(new[] { a[i][0] + dx * d - nx * tl, a[i][1] + dy * d - ny * tl, a[i][0] + dx * d + nx * tl, a[i][1] + dy * d + ny * tl });
                    }
                    Segs(env, list, Col(env, "fg"), lw, 0.8 * o);
                }
                var cap = 9 * u * ee;
                if (pOut < 0.02)
                    Stroke(env, new[] { new[] { a[i][0] - nx * cap, a[i][1] - ny * cap }, new[] { a[i][0] + nx * cap, a[i][1] + ny * cap } },
                        i < 2 || p.Accent ? Col(env, "accent") : Col(env, "fg"), 2.4 * u, ee * o);
                if (v == 1) env.Circle(b[i][0], b[i][1], 3 * u * ee, null, Col(env, "fg"), lw, o * ee, false);
            }
            if (v != 2)
            {
                var e = InE(env, 0.3, 0.35) * o;
                var fs = FS(env) * 0.85;
                Label(env, $"Y {Pad2(bb.Y0, 4)}", cx + 12 * u, bb.Y0 - gy - fs * 0.9, JO("size", fs, "alpha", e));
                Label(env, $"Y {Pad2(bb.Y1, 4)}", cx + 12 * u, bb.Y1 + gy + fs * 0.9, JO("size", fs, "alpha", e));
                Label(env, $"X {Pad2(cx, 4)}", cx - 12 * u, env.H - m * 0.5 - fs * 0.6,
                    JO("size", fs, "align", "right", "alpha", e, "color", Col(env, "fg")));
            }
        },
    });

    // ---------------- cropMarks（トンボ） ----------------

    private static void RegisterCropMarks() => Reg0("cropMarks", new DecorDef
    {
        Name = "トンボ",
        Tags = new[] { "editorial", "graphic", "calm" },
        W = 1.1,
        Layer = "front",
        Draw = (env, p) =>
        {
            if (env.Pass != "main") return;
            var bb = GetBB(env, env.Body);
            var sc = env.Sc!;
            var u = U(env);
            var o = OutE(env);
            if (o <= 0.003) return;
            var pad = 14 * u + Math.Min(Bw(bb), Bh(bb)) * 0.08 + p.R * 10 * u;
            var cm = 12 * u;
            var x0 = Math.Max(cm, bb.X0 - pad);
            var x1 = Math.Min(env.W - cm, bb.X1 + pad);
            var y0 = Math.Max(cm, bb.Y0 - pad);
            var y1 = Math.Min(env.H - cm, bb.Y1 + pad);
            var g = 8 * u;
            var lm = 26 * u + Math.Min(Bw(bb), Bh(bb)) * 0.05;
            var b = 9 * u;
            var lw = Math.Max(1, 1 * u);
            var col = Col(env, "fg");
            var corners = new[] { new[] { -1, -1 }, new[] { 1, -1 }, new[] { 1, 1 }, new[] { -1, 1 } };
            for (var k = 0; k < 4; k++)
            {
                var sx = corners[k][0];
                var sy = corners[k][1];
                var e = J.E.OutExpo(J.Clamp((env.Lt - k * 0.05) / 0.45));
                if (e <= 0) continue;
                var x = sx < 0 ? x0 : x1;
                var y = sy < 0 ? y0 : y1;
                var hl = new[] { new[] { x + sx * g, (double)y }, new[] { x + sx * (g + lm * e), (double)y } };
                var vl = new[] { new[] { (double)x, y + sy * g }, new[] { (double)x, y + sy * (g + lm * e) } };
                Segs(env, new[] { new[] { hl[0][0], hl[0][1], hl[1][0], hl[1][1] }, new[] { vl[0][0], vl[0][1], vl[1][0], vl[1][1] } }, col, lw, 0.9 * o);
                Segs(env, new[]
                {
                    new[] { hl[0][0], hl[0][1] + sy * b, hl[1][0], hl[1][1] + sy * b },
                    new[] { vl[0][0] + sx * b, vl[0][1], vl[1][0] + sx * b, vl[1][1] },
                }, col, lw, 0.55 * o);
            }
            var e2 = InE(env, 0.45, 0.15);
            if (e2 <= 0) return;
            var cx = (x0 + x1) / 2;
            var cy = (y0 + y1) / 2;
            var cl = 20 * u * e2;
            var rr = 6 * u;
            var rc = p.Accent ? Col(env, "accent") : Col(env, "fg");
            var marks = new List<double[]> { new[] { cx, y0 - g, 0.0, -1.0 }, new[] { cx, y1 + g, 0.0, 1.0 } };
            if (p.Big || Bh(bb) > Bw(bb)) marks.Add(new[] { x0 - g, cy, -1.0, 0.0 });
            if (p.Big || Bh(bb) > Bw(bb)) marks.Add(new[] { x1 + g, cy, 1.0, 0.0 });
            foreach (var mk in marks)
            {
                var x = mk[0];
                var y = mk[1];
                var dx = mk[2];
                var dy = mk[3];
                var ex = x + dx * cl;
                var ey = y + dy * cl;
                var mx = x + dx * cl * 0.55;
                var my = y + dy * cl * 0.55;
                Segs(env, new[] { new[] { x, y, ex, ey }, new[] { mx - dy * cl * 0.45, my - dx * cl * 0.45, mx + dy * cl * 0.45, my + dx * cl * 0.45 } }, rc, lw, 0.85 * o);
                if (p.Big) env.Circle(mx, my, rr * e2, null, rc, lw, 0.85 * o, false);
            }
        },
    });

    // ---------------- reticle（ロックオン） ----------------

    private static void RegisterReticle() => Reg0("reticle", new DecorDef
    {
        Name = "ロックオン",
        Tags = new[] { "glitch", "graphic" },
        W = 0.9,
        Layer = "front",
        Draw = (env, p) =>
        {
            if (env.Pass != "main") return;
            var bb = GetBB(env, env.Body);
            var sc = env.Sc!;
            var u = U(env);
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0) return;
            var r0 = J.Clamp(Math.Min(Bw(bb), Bh(bb)) * 0.3, 40 * u, 72 * u);
            var lw0 = FS(env) * 3.6;
            var sp = NearBB(env, bb, r0 * 2.8 + lw0, r0 * 2.6, p, 18 * u);
            var lLeft = sp.Cx > env.W / 2;
            var cx = lLeft ? sp.X + lw0 + r0 * 1.4 : sp.X + r0 * 1.4;
            var cy = sp.Cy;
            var lockE = J.E.OutExpo(J.Clamp(env.Lt / 0.6));
            var r = r0 * (2.1 - 1.1 * lockE) * (1 + 0.25 * J.E.InCubic(env.POut));
            var rot = (1 - lockE) * 140 + env.Ltb * 16 + p.R * 90;
            var lw = Math.Max(1.2, 1.6 * u);
            var a = Math.Min(1, env.Lt / 0.12) * o;
            var on = lockE > 0.97 || env.Step % 2 == 0;
            for (var k = 0; k < 4; k++)
                env.Arc(cx, cy, r, rot + k * 90 - 28, rot + k * 90 + 28, Col(env, "fg"), lw, a * (on ? 1 : 0.5), false);
            env.Arc(cx, cy, r * 0.62, -90, -90 + 360 * lockE, Col(env, "sub"), Math.Max(1, u), a * 0.6, false);
            var tk = new List<double[]>();
            for (var k = 0; k < 4; k++)
            {
                var an = k * 90 * J.DEG;
                tk.Add(new[] { cx + Math.Cos(an) * r * 0.74, cy + Math.Sin(an) * r * 0.74, cx + Math.Cos(an) * r * 1.16, cy + Math.Sin(an) * r * 1.16 });
            }
            Segs(env, tk, Col(env, "fg"), Math.Max(1, u), a * 0.9);
            var tri = new List<double[][]>();
            var r2 = r * 1.32;
            for (var k = 0; k < 3; k++)
            {
                var an = (-env.Ltb * 30 + k * 120 + p.R * 60) * J.DEG;
                var px = cx + Math.Cos(an) * r2;
                var py = cy + Math.Sin(an) * r2;
                var s = 5 * u;
                var ix = -Math.Cos(an);
                var iy = -Math.Sin(an);
                var nx = -iy;
                var ny = ix;
                tri.Add(new[]
                {
                    new[] { px + ix * s, py + iy * s },
                    new[] { px - ix * s * 0.6 + nx * s * 0.8, py - iy * s * 0.6 + ny * s * 0.8 },
                    new[] { px - ix * s * 0.6 - nx * s * 0.8, py - iy * s * 0.6 - ny * s * 0.8 },
                });
            }
            Polys(env, tri, Col(env, "accent"), a);
            env.Circle(cx, cy, 2.6 * u, Col(env, "accent"), null, 0, a * (lockE > 0.97 ? 1 : env.Step % 2 != 0 ? 1 : 0.2), false);
            var fs = FS(env) * 0.9;
            var lx = lLeft ? cx - r0 * 1.5 : cx + r0 * 1.5;
            var al = lLeft ? "right" : "left";
            Label(env, lockE > 0.97 ? "LOCK" : "SCAN", lx, cy - r0 * 0.9,
                JO("size", fs, "align", al, "color", lockE > 0.97 ? Col(env, "accent") : Col(env, "sub"), "alpha", a));
            Label(env, (lockE * 100).ToString("F1", CultureInfo.InvariantCulture).PadLeft(5, '0'), lx, cy - r0 * 0.9 + fs * 1.3,
                JO("size", fs, "align", al, "alpha", a * 0.8));
        },
    });

    // ---------------- radar（レーダー） ----------------

    private static void RegisterRadar() => Reg0("radar", new DecorDef
    {
        Name = "レーダー",
        Tags = new[] { "glitch", "graphic" },
        W = 0.7,
        Layer = "front",
        Draw = (env, p) =>
        {
            if (env.Pass != "main") return;
            var bb = GetBB(env, env.Body);
            var sc = env.Sc!;
            var u = U(env);
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0) return;
            var rr0 = J.Clamp(Math.Min(env.W, env.H) * 0.07, 40 * u, 96 * u);
            var fs = FS(env) * 0.85;
            var sp = CornerSpot(env, bb, rr0 * 2 + 8 * u, rr0 * 2 + fs * 2.2, p);
            var cx = sp.Cx;
            var cy = sp.Y + rr0 + 4 * u;
            var e = J.E.OutExpo(J.Clamp(env.Lt / 0.5));
            var s = e * (1 - 0.15 * J.E.InCubic(env.POut));
            var rr = rr0 * s;
            var lw = Math.Max(1, u);
            var a = o * (sp.Ok ? 1 : 0.35);
            var rings = new[] { 1.0, 0.66, 0.33 };
            for (var i = 0; i < rings.Length; i++)
                env.Arc(cx, cy, rr * rings[i], -90, -90 + 360 * J.Clamp(e * 1.4 - i * 0.15), Col(env, "sub"), lw, 0.55 * a, false);
            Segs(env, new[] { new[] { cx - rr, cy, cx + rr, cy }, new[] { cx, cy - rr, cx, cy + rr } }, Col(env, "sub"), lw, 0.3 * a);
            var tk = new List<double[]>();
            for (var i = 0; i < 36; i++)
            {
                var an = i * 10 * J.DEG;
                var l = i % 3 == 0 ? 5 * u : 2.5 * u;
                tk.Add(new[] { cx + Math.Cos(an) * rr, cy + Math.Sin(an) * rr, cx + Math.Cos(an) * (rr + l), cy + Math.Sin(an) * (rr + l) });
            }
            Segs(env, tk, Col(env, "fg"), lw, 0.6 * a);
            var sw = (env.Ltb * 150 + p.R * 360) % 360;
            for (var k = 14; k >= 0; k--)
            {
                var an = (sw - k * 3.2) * J.DEG;
                Stroke(env, new[] { new[] { cx, cy }, new[] { cx + Math.Cos(an) * rr, cy + Math.Sin(an) * rr } },
                    Col(env, "accent"), k != 0 ? lw : 1.6 * lw, a * (k != 0 ? 0.3 * (1 - k / 15.0) : 0.95));
            }
            var bl = new List<double[]>();
            for (var i = 0; i < 3 + p.N; i++)
            {
                var ang = J.R(p.Seed, i, 1) * 360;
                var rad = J.Rr(0.2, 0.9, p.Seed, i, 2);
                var since = (((sw - ang) % 360) + 360) % 360;
                var glow = Math.Exp(-since / 70);
                if (glow > 0.03)
                    bl.Add(new[] { cx + Math.Cos(ang * J.DEG) * rr * rad, cy + Math.Sin(ang * J.DEG) * rr * rad, (2 + 2.2 * glow) * u, glow });
            }
            foreach (var bpt in bl) env.Circle(bpt[0], bpt[1], bpt[2], Col(env, "fg"), null, 0, a * bpt[3], false);
            var ea = InE(env, 0.3, 0.3) * a;
            Label(env, $"RDR-{Pad2((env.Cut?.Line ?? 0) + 1)}", cx - rr0, sp.Y + rr0 * 2 + fs * 1.4, JO("size", fs, "alpha", ea));
            Label(env, $"{Pad2(sw, 3)}°", cx + rr0, sp.Y + rr0 * 2 + fs * 1.4,
                JO("size", fs, "align", "right", "alpha", ea, "color", Col(env, "fg")));
        },
    });

    // ---------------- progressRing（進行リング） ----------------

    private static void RegisterProgressRing() => Reg0("progressRing", new DecorDef
    {
        Name = "進行リング",
        Tags = new[] { "graphic", "editorial", "calm" },
        W = 0.9,
        Layer = "front",
        Draw = (env, p) =>
        {
            if (env.Pass != "main") return;
            var bb = GetBB(env, env.Body);
            var sc = env.Sc!;
            var u = U(env);
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0) return;
            var rr = J.Clamp(Math.Min(env.W, env.H) * 0.05, 34 * u, 64 * u);
            var sp = NearBB(env, bb, rr * 2 + 24 * u, rr * 2 + 24 * u, p, 22 * u);
            var cx = sp.Cx;
            var cy = sp.Cy;
            var lw = Math.Max(1, u);
            var e = J.E.OutExpo(J.Clamp(env.Lt / 0.5));
            var a = o * (sp.Ok ? 1 : 0.4);
            var prog = J.Clamp(env.Lt / Math.Max(0.1, env.Cut?.Dur ?? 0));
            var tk = new List<double[]>();
            var nT = 60;
            for (var i = 0; i < nT * e; i++)
            {
                var an = (-90 + i * 6) * J.DEG;
                var l = i % 5 == 0 ? 6 * u : 3 * u;
                var r0 = rr + 6 * u;
                tk.Add(new[] { cx + Math.Cos(an) * r0, cy + Math.Sin(an) * r0, cx + Math.Cos(an) * (r0 + l), cy + Math.Sin(an) * (r0 + l) });
            }
            Segs(env, tk, Col(env, "sub"), lw, 0.55 * a);
            if (p.V % 2 != 0)
            {
                const int ns = 24;
                for (var i = 0; i < ns; i++)
                {
                    var a0 = -90 + i * 360.0 / ns + 2;
                    var a1 = a0 + 360.0 / ns - 4;
                    var lit = (i + 1) / (double)ns <= prog + 1e-3;
                    if (i / (double)ns > e) break;
                    env.Arc(cx, cy, rr, a0, a1, lit ? Col(env, "accent") : Col(env, "sub"), lit ? 3 * u : lw, a * (lit ? 1 : 0.35), false);
                }
            }
            else
            {
                env.Arc(cx, cy, rr, -90, -90 + 360 * e, Col(env, "sub"), lw, 0.35 * a, false);
                env.Arc(cx, cy, rr, -90, -90 + 360 * prog * e, Col(env, "accent"), 2.6 * u, a, false);
                var ha = (-90 + 360 * prog * e) * J.DEG;
                env.Circle(cx + Math.Cos(ha) * rr, cy + Math.Sin(ha) * rr, 3.2 * u, Col(env, "accent"), null, 0, a, false);
            }
            var fs = rr * 0.52;
            Label(env, Pad2(prog * 100), cx - fs * 0.12, cy,
                JO("size", fs, "align", "center", "color", Col(env, "fg"), "alpha", a * e, "track", 0.02));
            Label(env, "%", cx + fs * 0.72, cy + fs * 0.12, JO("size", fs * 0.42, "align", "left", "alpha", a * e));
        },
    });

    // ---------------- timecodeBar（タイムコード） ----------------

    private static void RegisterTimecodeBar() => Reg0("timecodeBar", new DecorDef
    {
        Name = "タイムコード",
        Tags = new[] { "editorial", "glitch", "graphic" },
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
            var fs = FS(env);
            var w = Math.Min(w2 * 0.38, 460 * u);
            var h = fs * 3.4;
            var low = p.Low;
            var x0 = p.Right ? w2 - m - w : m;
            var y0 = low ? h2 - m - h : m;
            if (HitBb(x0, y0, x0 + w, y0 + h, bb, 10 * u))
            {
                low = !low;
                y0 = low ? h2 - m - h : m;
            }
            var a = o * (HitBb(x0, y0, x0 + w, y0 + h, bb, 0) ? 0.3 : 1);
            var e = J.E.OutExpo(J.Clamp(env.Lt / 0.5));
            var t = Math.Max(0, env.T);
            var fr = Math.Floor(t * 24);
            var tc = $"{Pad2(t / 3600)}:{Pad2(t / 60 % 60)}:{Pad2(t % 60)}:{Pad2(fr % 24)}";
            var n = (int)Math.Ceiling(tc.Length * J.Clamp(env.Lt / 0.35));
            var ty = y0 + fs * 0.8;
            var tcW = TextW("TC ", MonoF(env), fs * 0.8, 0.1);
            Label(env, "TC", x0, ty + fs * 0.12, JO("size", fs * 0.8, "color", Col(env, "accent"), "alpha", a));
            Label(env, tc[..Math.Min(n, tc.Length)], x0 + tcW, ty, JO("size", fs * 1.25, "color", Col(env, "fg"), "alpha", a, "track", 0.06));
            Label(env, $"F {Pad2(Math.Floor(env.Lt * 24), 4)}", x0 + w, ty + fs * 0.12,
                JO("size", fs * 0.8, "align", "right", "alpha", a * e));
            var by = y0 + h - fs * 0.6;
            var lw = Math.Max(1, u);
            var prog = J.Clamp(env.Lt / Math.Max(0.1, env.Cut?.Dur ?? 0));
            Stroke(env, new[] { new[] { x0, by }, new[] { x0 + w * e, by } }, Col(env, "sub"), lw, 0.6 * a);
            var tk = new List<double[]>();
            for (var i = 0; i <= 48; i++)
            {
                var x = x0 + w * i / 48.0;
                if (x > x0 + w * e) break;
                var l = i % 12 == 0 ? 7 * u : i % 4 == 0 ? 4 * u : 2 * u;
                tk.Add(new[] { x, by, x, by - l });
            }
            Segs(env, tk, Col(env, "sub"), lw, 0.6 * a);
            var px = x0 + w * prog * e;
            Stroke(env, new[] { new[] { x0, by }, new[] { px, by } }, Col(env, "fg"), 2.2 * u, a);
            Polys(env, new double[][][] { new[] { new[] { px - 5 * u, by - 12 * u }, new[] { px + 5 * u, by - 12 * u }, new[] { px, by - 4 * u } } }, Col(env, "accent"), a);
            Segs(env, new[] { new[] { x0, by + 4 * u, x0, by + 10 * u }, new[] { x0 + w * e, by + 4 * u, x0 + w * e, by + 10 * u } }, Col(env, "fg"), lw, a * e);
            Label(env, "IN", x0 + 4 * u, by + 9 * u + fs * 0.35, JO("size", fs * 0.6, "alpha", a * e * 0.8));
            Label(env, "OUT", x0 + w * e - 4 * u, by + 9 * u + fs * 0.35, JO("size", fs * 0.6, "align", "right", "alpha", a * e * 0.8));
        },
    });

    // ---------------- rulerEdge（端の定規） ----------------

    private static void RegisterRulerEdge() => Reg0("rulerEdge", new DecorDef
    {
        Name = "端の定規",
        Tags = new[] { "editorial", "graphic", "calm" },
        W = 1,
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
            var vert = p.Corner;
            var fs = FS(env) * 0.72;
            var lw = Math.Max(1, u);
            var st = 10 * u;
            var e = J.E.OutCubic(J.Clamp(env.Lt / 0.55));
            var len = vert ? (double)h2 : w2;
            var a0 = (double)m;
            var a1 = len - m;
            var side = vert ? (p.Right ? 1 : -1) : (p.Low ? 1 : -1);
            var @base = vert ? (side > 0 ? w2 - m * 0.55 : m * 0.55) : (side > 0 ? h2 - m * 0.55 : m * 0.55);
            var inward = -side;
            double[] P2(double along, double off) =>
                vert ? new[] { @base + inward * off, along } : new[] { along, @base + inward * off };
            var n = (int)Math.Floor((a1 - a0) / st);
            var list = new List<double[]>();
            var head = a0 + (a1 - a0) * e;
            for (var i = 0; i <= n; i++)
            {
                var s = a0 + i * st;
                if (s > head) break;
                var l = (i % 10 == 0 ? 14 : i % 5 == 0 ? 9 : 4.5) * u * J.Clamp((head - s) / (60 * u));
                var pA = P2(s, 0);
                var q = P2(s, l);
                list.Add(new[] { pA[0], pA[1], q[0], q[1] });
            }
            var a = o;
            Segs(env, list, Col(env, "sub"), lw, 0.7 * a);
            var bl0 = P2(a0, 0);
            var bl1 = P2(head, 0);
            Stroke(env, new[] { bl0, bl1 }, Col(env, "sub"), lw, 0.5 * a);
            for (var i = 0; i <= n; i += 10)
            {
                var s = a0 + i * st;
                if (s > head - 20 * u) break;
                var q = P2(s + (vert ? 0 : 3 * u), 20 * u + fs * 0.4);
                Label(env, Pad2(i, 3), q[0], q[1], JO("size", fs, "align", vert ? (inward > 0 ? "left" : "right") : "left", "alpha", 0.7 * a));
            }
            var em = InE(env, 0.5, 0.2, J.E.OutCubic) * o;
            if (em <= 0) return;
            var lo = vert ? bb.Y0 : bb.X0;
            var hi = vert ? bb.Y1 : bb.X1;
            var mid = (lo + hi) / 2;
            var from = vert ? h2 / 2.0 : w2 / 2.0;
            var lo2 = J.Lerp(from, J.Clamp(lo, a0, a1), em);
            var hi2 = J.Lerp(from, J.Clamp(hi, a0, a1), em);
            var mid2 = J.Lerp(from, J.Clamp(mid, a0, a1), em);
            var off = 26 * u;
            Stroke(env, new[] { P2(lo2, off), P2(hi2, off) }, Col(env, "accent"), 2 * u, 0.9 * em);
            var s1 = P2(lo2, off - 5 * u);
            var s2 = P2(lo2, off + 5 * u);
            var s3 = P2(hi2, off - 5 * u);
            var s4 = P2(hi2, off + 5 * u);
            Segs(env, new[] { new[] { s1[0], s1[1], s2[0], s2[1] }, new[] { s3[0], s3[1], s4[0], s4[1] } }, Col(env, "accent"), 2 * u, 0.9 * em);
            var tp = P2(mid2, 3 * u);
            var s5 = 5 * u;
            var tri = vert
                ? new[] { new[] { tp[0], tp[1] }, new[] { tp[0] + inward * s5 * 1.6, tp[1] - s5 }, new[] { tp[0] + inward * s5 * 1.6, tp[1] + s5 } }
                : new[] { new[] { tp[0], tp[1] }, new[] { tp[0] - s5, tp[1] + inward * s5 * 1.6 }, new[] { tp[0] + s5, tp[1] + inward * s5 * 1.6 } };
            Polys(env, new[] { tri }, Col(env, "fg"), em);
            var lp = P2(mid2, off + 8 * u + fs);
            Label(env, $"{Pad2(hi - lo, 4)}", lp[0] + (vert ? 0 : 6 * u), lp[1],
                JO("size", fs, "color", Col(env, "fg"), "align", vert ? (inward > 0 ? "left" : "right") : "left", "alpha", em));
        },
    });

    // ---------------- dimension（寸法線） ----------------

    private static void RegisterDimension() => Reg0("dimension", new DecorDef
    {
        Name = "寸法線",
        Tags = new[] { "editorial", "graphic" },
        W = 1,
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
            var lw = Math.Max(1, u);
            var fs = FS(env) * 0.85;
            var arch = p.V % 2 == 1;
            var gap = 20 * u + Bh(bb) * 0.1;
            var below = p.Low;
            var y = below ? bb.Y1 + gap : bb.Y0 - gap;
            if (y < m * 0.6 || y > h2 - m * 0.6)
            {
                below = !below;
                y = below ? bb.Y1 + gap : bb.Y0 - gap;
            }
            var e = J.E.OutExpo(J.Clamp(env.Lt / 0.55));
            var e2 = InE(env, 0.35, 0.05);
            var x0 = bb.X0;
            var x1 = bb.X1;
            var cx = (x0 + x1) / 2;
            var sy = below ? 1 : -1;
            var lab = $"{Math.Round(x1 - x0)}";
            var lwid = TextW(lab, MonoF(env), fs, 0.08) + 14 * u;
            var x0e = J.Lerp(cx, x0, e);
            var x1e = J.Lerp(cx, x1, e);
            var yEdge = below ? bb.Y1 : bb.Y0;
            Segs(env, new[]
            {
                new[] { x0, yEdge + sy * 6 * u, x0, J.Lerp(yEdge + sy * 6 * u, y + sy * 8 * u, e2) },
                new[] { x1, yEdge + sy * 6 * u, x1, J.Lerp(yEdge + sy * 6 * u, y + sy * 8 * u, e2) },
            }, Col(env, "sub"), lw, 0.7 * o);
            if (x1e - x0e > lwid)
                Segs(env, new[] { new[] { x0e, y, cx - lwid / 2, y }, new[] { cx + lwid / 2, y, x1e, y } }, Col(env, "fg"), lw, 0.9 * o);
            var ea = J.Clamp((e - 0.7) / 0.3) * o;
            if (arch)
                Segs(env, new[] { new[] { x0 - 5 * u, y + 5 * u, x0 + 5 * u, y - 5 * u }, new[] { x1 - 5 * u, y + 5 * u, x1 + 5 * u, y - 5 * u } }, Col(env, "fg"), 1.6 * u, ea);
            else
                Polys(env, new double[][][]
                {
                    new[] { new[] { x0, y }, new[] { x0 + 10 * u, y - 3.5 * u }, new[] { x0 + 10 * u, y + 3.5 * u } },
                    new[] { new[] { x1, y }, new[] { x1 - 10 * u, y - 3.5 * u }, new[] { x1 - 10 * u, y + 3.5 * u } },
                }, Col(env, "fg"), ea);
            Label(env, lab, cx, y, JO("size", fs, "align", "center", "color", p.Accent ? Col(env, "accent") : Col(env, "fg"), "alpha", e2 * o));
            if (p.Big || p.V % 3 == 0)
            {
                var gx = 20 * u + Bw(bb) * 0.02;
                var right = p.Right;
                var x = right ? bb.X1 + gx : bb.X0 - gx;
                if (x < m || x > w2 - m)
                {
                    right = !right;
                    x = right ? bb.X1 + gx : bb.X0 - gx;
                }
                var sx = right ? 1 : -1;
                var cy = (bb.Y0 + bb.Y1) / 2;
                var ye0 = J.Lerp(cy, bb.Y0, e);
                var ye1 = J.Lerp(cy, bb.Y1, e);
                var labH = $"{Math.Round(Bh(bb))}";
                var lh = TextW(labH, MonoF(env), fs, 0.08) + 14 * u;
                var xe = right ? bb.X1 : bb.X0;
                Segs(env, new[]
                {
                    new[] { xe + sx * 6 * u, bb.Y0, J.Lerp(xe + sx * 6 * u, x + sx * 8 * u, e2), bb.Y0 },
                    new[] { xe + sx * 6 * u, bb.Y1, J.Lerp(xe + sx * 6 * u, x + sx * 8 * u, e2), bb.Y1 },
                }, Col(env, "sub"), lw, 0.7 * o);
                if (ye1 - ye0 > lh)
                    Segs(env, new[] { new[] { x, ye0, x, cy - lh / 2 }, new[] { x, cy + lh / 2, x, ye1 } }, Col(env, "fg"), lw, 0.9 * o);
                if (arch)
                    Segs(env, new[]
                    {
                        new[] { x - 5 * u, bb.Y0 + 5 * u, x + 5 * u, bb.Y0 - 5 * u },
                        new[] { x - 5 * u, bb.Y1 + 5 * u, x + 5 * u, bb.Y1 - 5 * u },
                    }, Col(env, "fg"), 1.6 * u, ea);
                else
                    Polys(env, new double[][][]
                    {
                        new[] { new[] { x, bb.Y0 }, new[] { x - 3.5 * u, bb.Y0 + 10 * u }, new[] { x + 3.5 * u, bb.Y0 + 10 * u } },
                        new[] { new[] { x, bb.Y1 }, new[] { x - 3.5 * u, bb.Y1 - 10 * u }, new[] { x + 3.5 * u, bb.Y1 - 10 * u } },
                    }, Col(env, "fg"), ea);
                Label(env, labH, x, cy, JO("size", fs, "align", "center", "rot", -90.0, "color", Col(env, "fg"), "alpha", e2 * o));
            }
        },
    });

    // ---------------- indexNum（通し番号） ----------------

    private static void RegisterIndexNum() => Reg0("indexNum", new DecorDef
    {
        Name = "通し番号",
        Tags = new[] { "editorial", "graphic", "calm" },
        W = 1,
        Layer = "front",
        Draw = (env, p) =>
        {
            if (env.Pass != "main") return;
            var bb = GetBB(env, env.Body);
            var sc = env.Sc!;
            var u = U(env);
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0) return;
            var lines = env.Plan?.Lines.Count ?? 0;
            var idx = env.Cut!.Line >= 0 ? env.Cut.Line + 1 : env.Cut.Index + 1;
            var num = Pad2(idx);
            var tot = "/" + Pad2(Math.Max(lines, idx));
            var font = p.V % 2 != 0 ? "mincho_light" : "gothic_light";
            var s0 = J.Clamp(Math.Min(env.W, env.H) * 0.1, 64 * u, 132 * u);
            var ts = s0 * 0.3;
            var nw = TextW(num, font, s0, 0.02);
            var tw = TextW(tot, font, ts, 0.06);
            var w = nw + tw + s0 * 0.12;
            var h = s0 * 1.25;
            var sp = CornerSpot(env, bb, w, h, p, 1.1);
            var a = o * (sp.Ok ? 1 : 0.35) * J.E.OutCubic(J.Clamp(env.Lt / 0.2));
            var x0 = sp.X;
            var @base = sp.Y + s0 * 0.8;
            var ctx = env.Ctx;
            var digits = num.ToArray();
            var x = x0;
            if (env.Pass == "main")
            {
                for (var i = 0; i < digits.Length; i++)
                {
                    var d = digits[i];
                    var dw = TextW(d.ToString(), font, s0, 0.02);
                    var pd = J.E.OutExpo(J.Clamp((env.Lt - 0.04 - i * 0.08) / 0.55));
                    var steps = p.Mode == "count" ? 6 + i * 3 : 3;
                    var off = (1 - pd) * steps;
                    ctx.Save();
                    ctx.ClipRect(new SKRect((float)(x - 2), (float)(@base - s0 * 0.5), (float)(x + dw + 2), (float)(@base - s0 * 0.5 + s0 * 0.98)));
                    var fl = Math.Floor(off);
                    var frac = off - fl;
                    for (var k = 0; k <= (frac > 0.001 ? 1 : 0); k++)
                    {
                        var val = ((d - '0') - ((int)fl + k)) % 10;
                        val = (val % 10 + 10) % 10;
                        var dy = (k - frac) * s0 * 1.25;
                        env.DrawItem(new JObject
                        {
                            ["text"] = val.ToString(CultureInfo.InvariantCulture),
                            ["font"] = font,
                            ["size"] = s0,
                            ["x"] = x + dw / 2,
                            ["y"] = @base + dy,
                            ["color"] = Col(env, "fg"),
                            ["align"] = "center",
                            ["track"] = 0.02,
                            ["alpha"] = a * (1 - Math.Abs(k - frac) * 0.6),
                            ["ghost"] = false,
                        });
                    }
                    ctx.Restore();
                    x += dw;
                }
            }
            else x += nw;
            var e2 = InE(env, 0.4, 0.25);
            Label(env, tot, x + s0 * 0.1, @base + s0 * 0.26, JO("font", font, "size", ts, "color", Col(env, "sub"), "alpha", a * e2, "track", 0.06));
            var ry = @base + s0 * 0.52;
            Stroke(env, new[] { new[] { x0, ry }, new[] { x0 + w * J.E.OutExpo(J.Clamp((env.Lt - 0.1) / 0.5)), ry } }, Col(env, "sub"), Math.Max(1, u), 0.8 * a);
            env.Rect(x0, ry - 1.5 * u, s0 * 0.22 * e2, 3 * u, Col(env, "accent"), a, false);
            Label(env, env.Cut.Line >= 0 ? "LYRIC" : "INTRO", x0, ry + FS(env) * 0.9,
                JO("size", FS(env) * 0.72, "alpha", a * e2, "track", 0.3));
        },
    });

    // 七段数码管的段定义（JS SEG）
    private static readonly Dictionary<int, string> Seg = new()
    {
        [0] = "abcdef", [1] = "bc", [2] = "abged", [3] = "abgcd", [4] = "fgbc",
        [5] = "afgcd", [6] = "afgedc", [7] = "abc", [8] = "abcdefg", [9] = "abcdfg",
    };

    /// <summary>七段数码管单字的多边形段。JS segDigit。</summary>
    private static List<KeyValuePair<char, double[][]>> SegDigit(double x, double y, double dw, double dh, double t, int d, double sk)
    {
        var @out = new List<KeyValuePair<char, double[][]>>();
        var h2 = dh / 2;
        var k = t / 2;
        double[][] Hs(double x0, double x1, double yy) => new[]
        {
            new[] { x0 + k, yy }, new[] { x0 + 2 * k, yy - k }, new[] { x1 - 2 * k, yy - k },
            new[] { x1 - k, yy }, new[] { x1 - 2 * k, yy + k }, new[] { x0 + 2 * k, yy + k },
        };
        double[][] Vs(double xx, double y0, double y1) => new[]
        {
            new[] { xx, y0 + k }, new[] { xx + k, y0 + 2 * k }, new[] { xx + k, y1 - 2 * k },
            new[] { xx, y1 - k }, new[] { xx - k, y1 - 2 * k }, new[] { xx - k, y0 + 2 * k },
        };
        var s2 = new Dictionary<char, double[][]>
        {
            ['a'] = Hs(x, x + dw, y),
            ['g'] = Hs(x, x + dw, y + h2),
            ['d'] = Hs(x, x + dw, y + dh),
            ['f'] = Vs(x, y, y + h2),
            ['b'] = Vs(x + dw, y, y + h2),
            ['e'] = Vs(x, y + h2, y + dh),
            ['c'] = Vs(x + dw, y + h2, y + dh),
        };
        var seg = Seg.GetValueOrDefault(d, "");
        foreach (var c in seg)
            @out.Add(new KeyValuePair<char, double[][]>(c,
                s2[c].Select(p => new[] { p[0] + (y + dh / 2 - p[1]) * sk, p[1] }).ToArray()));
        return @out;
    }

    // ---------------- dateStamp（日付写真） ----------------

    private static void RegisterDateStamp() => Reg0("dateStamp", new DecorDef
    {
        Name = "日付写真",
        Tags = new[] { "emotional", "pop", "calm" },
        W = 0.7,
        Layer = "front",
        Draw = (env, p) =>
        {
            if (env.Pass != "main") return;
            var bb = GetBB(env, env.Body);
            var sc = env.Sc!;
            var u = U(env);
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0) return;
            var yy = J.H(p.Seed, 1) % 2 != 0 ? 90 + (int)(J.H(p.Seed, 2) % 10) : (int)(J.H(p.Seed, 3) % 27);
            var mm = 1 + (int)(J.H(p.Seed, 4) % 12);
            var dd = 1 + (int)(J.H(p.Seed, 5) % 28);
            var groups = new[] { Pad2(yy), mm.ToString(CultureInfo.InvariantCulture), Pad2(dd) };
            var dh = J.Clamp(Math.Min(env.W, env.H) * 0.034, 24 * u, 46 * u);
            var dw = dh * 0.52;
            var t = dh * 0.12;
            var gapD = dw * 0.42;
            var gapG = dw * 1.35;
            var width = groups.Sum(g => g.Length * (dw + gapD)) + gapG * 2 + dw * 0.5;
            var sp = CornerSpot(env, bb, width, dh * 1.4,
                new DecorSpec { Right = p.V % 3 == 2 ? !p.Right : true, Low = true }, 1.2);
            var x = sp.X + dw * 0.5;
            var y = sp.Y + dh * 0.2;
            var pieces = new List<KeyValuePair<char, double[][]>>();
            pieces.Add(new KeyValuePair<char, double[][]>('q', new[]
            {
                new[] { x - dw * 0.1, y - t * 0.2 }, new[] { x + t * 0.9, y - t * 0.2 },
                new[] { x + t * 0.2, y + dh * 0.32 }, new[] { x - dw * 0.1 - t * 0.6, y + dh * 0.32 },
            }));
            x += dw * 0.35;
            foreach (var g in groups)
            {
                foreach (var ch in g)
                {
                    pieces.AddRange(SegDigit(x, y, dw, dh, t, ch - '0', 0.1));
                    x += dw + gapD;
                }
                x += gapG - gapD;
            }
            var col = Col(env, "accent");
            if (env.Pass != "main") return;
            var on = new List<double[][]>();
            for (var i = 0; i < pieces.Count; i++)
            {
                var t0 = 0.05 + J.R(p.Seed, i, 7) * 0.35;
                if (env.Lt < t0) continue;
                var fl = env.Lt - t0 < 0.12 ? (J.R(p.Seed, i, unchecked((int)env.Step)) < 0.5 ? 0.3 : 1) : 1;
                if (fl == 1) on.Add(pieces[i].Value);
            }
            var onDim = new List<double[][]>();
            for (var i = 0; i < pieces.Count; i++)
            {
                var t0 = 0.05 + J.R(p.Seed, i, 7) * 0.35;
                if (env.Lt < t0) continue;
                var fl = env.Lt - t0 < 0.12 ? (J.R(p.Seed, i, unchecked((int)env.Step)) < 0.5 ? 0.3 : 1) : 1;
                if (fl != 1) onDim.Add(pieces[i].Value);
            }
            var filter = env.AllowFilter
                ? SKImageFilter.CreateDropShadow(0, 0, (float)(10 * u * env.Scale / 2), (float)(10 * u * env.Scale / 2),
                    Env.SkColor(col ?? "#000000", 0.8))
                : null;
            Polys(env, on, col, 0.92 * o * (sp.Ok ? 1 : 0.4), false, filter: filter);
            Polys(env, onDim, col, 0.3 * o, false, filter: filter);
        },
    });

    // ---------------- qrBlock（QR風ブロック） ----------------

    private static void RegisterQrBlock() => Reg0("qrBlock", new DecorDef
    {
        Name = "QR風ブロック",
        Tags = new[] { "graphic", "glitch", "pop" },
        W = 0.7,
        Layer = "front",
        Draw = (env, p) =>
        {
            if (env.Pass != "main") return;
            var bb = GetBB(env, env.Body);
            var sc = env.Sc!;
            var u = U(env);
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0) return;
            const int n = 21;
            var s = J.Clamp(Math.Min(env.W, env.H) * 0.1, 76 * u, 140 * u);
            var c = s / n;
            var fs = FS(env) * 0.72;
            var sp = CornerSpot(env, bb, s, s + fs * 2, p, 1.1);
            var x0 = sp.X;
            var y0 = sp.Y;
            var a = o * (sp.Ok ? 1 : 0.35);
            var e = J.Clamp(env.Lt / 0.55);
            var pOut = env.POut;
            var fg = new List<double[]>();
            var acc = new List<double[]>();
            int Finder(int i, int j)
            {
                int[][] anchors = { new[] { 0, 0 }, new[] { n - 7, 0 }, new[] { 0, n - 7 } };
                foreach (var an in anchors)
                {
                    var di = i - an[0];
                    var dj = j - an[1];
                    if (di >= 0 && di < 7 && dj >= 0 && dj < 7)
                    {
                        var r = Math.Max(Math.Abs(di - 3), Math.Abs(dj - 3));
                        return r == 3 || r <= 1 ? (r <= 1 ? 2 : 1) : 0;
                    }
                }
                return -1;
            }
            for (var j = 0; j < n; j++)
                for (var i = 0; i < n; i++)
                {
                    var f = Finder(i, j);
                    if (f == -1)
                    {
                        if ((i == 7 || j == 7) && (i < 8 && j < 8 || i > n - 9 && j < 8 || i < 8 && j > n - 9)) f = 0;
                        else if (i == 6 || j == 6) f = (i + j) % 2 == 0 ? 1 : 0;
                        else f = J.R(p.Seed, i, j, 3) < 0.48 ? 1 : 0;
                    }
                    if (f == 0) continue;
                    var d = (i + j) / (2.0 * n - 2);
                    if (d > e * 1.25 - 0.1 + (J.R(p.Seed, i, j, 9) - 0.5) * 0.1) continue;
                    if (pOut > 0 && J.R(p.Seed, i, j, 11) < pOut * 1.1) continue;
                    (f == 2 && p.Accent ? acc : fg).Add(new[] { x0 + i * c, y0 + j * c, c + 0.35, c + 0.35 });
                }
            Rects(env, fg, Col(env, "fg"), a);
            Rects(env, acc, Col(env, "accent"), a);
            if (e < 1)
            {
                var sy = y0 + s * e * 1.1;
                if (sy < y0 + s)
                    Stroke(env, new[] { new[] { x0 - 6 * u, sy }, new[] { x0 + s + 6 * u, sy } }, Col(env, "accent"), 1.5 * u, a);
            }
            Label(env, $"ID {(J.H(p.Seed, 12) % 0xffffff).ToString("X6", CultureInfo.InvariantCulture)}", x0, y0 + s + fs * 1.2,
                JO("size", fs, "alpha", a * InE(env, 0.3, 0.4), "track", 0.14));
        },
    });

    // ---------------- glitchRects（グリッチ片） ----------------

    private static void RegisterGlitchRects() => Reg0("glitchRects", new DecorDef
    {
        Name = "グリッチ片",
        Tags = new[] { "glitch" },
        W = 0.9,
        Layer = "front",
        Draw = (env, p) =>
        {
            var bb = GetBB(env, env.Body);
            var w2 = env.W;
            var h2 = env.H;
            var sc = env.Sc!;
            var u = U(env);
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0) return;
            var burst = Math.Max(1 - J.Clamp(env.Lt / 0.45), env.POut > 0 ? 1 - env.POut * 0.6 : 0);
            var st = unchecked((int)env.Step);
            var idle = (int)(J.H(p.Seed, st, 1) % 5) == 0;
            var n = (int)Math.Round((burst > 0 ? 7 + 6 * burst : idle ? 3 : 1) * (0.7 + 0.2 * p.N));
            var gap = 10 * u + Bh(bb) * 0.06;
            var cols = new[] { Col(env, "fg"), Col(env, "accent"), ColOr(env, "ghostA", "accent"), ColOr(env, "ghostB", "sub") };
            var solid = new List<double[]>[4] { new(), new(), new(), new() };
            var hollow = new List<double[]>();
            for (var i = 0; i < n; i++)
            {
                double R(int k) => J.R(p.Seed, st, i, k);
                var right = R(1) < 0.5;
                var h = (1.5 + R(3) * 9) * u;
                var room = right ? w2 - bb.X1 - gap : bb.X0 - gap;
                var w = (18 + R(4) * R(4) * 220) * u;
                double x, y;
                if (room > 60 * u && R(8) < 0.75)
                {
                    y = J.Lerp(bb.Y0, bb.Y1, R(2));
                    w = Math.Min(w, room - 8 * u);
                    x = right
                        ? bb.X1 + gap + R(5) * Math.Max(0, room - w - 8 * u) * 0.5
                        : bb.X0 - gap - w - R(5) * Math.Max(0, room - w - 8 * u) * 0.5;
                }
                else
                {
                    var top = R(9) < 0.5;
                    y = top ? bb.Y0 - gap - R(2) * 40 * u : bb.Y1 + gap + R(2) * 40 * u;
                    x = J.Lerp(bb.X0, bb.X1 - w, R(5));
                }
                x = J.Clamp(x, 4 * u, w2 - w - 4 * u);
                if (HitBb(x, y, x + w, y + h, bb, 2)) continue;
                if (R(6) < 0.25) hollow.Add(new[] { x, y - h * 1.5, w * 0.7, h * 3.5 });
                else solid[(int)Math.Floor(R(7) * 4)].Add(new[] { x, y, w, h });
            }
            for (var k = 0; k < 4; k++) Rects(env, solid[k], cols[k], 0.9 * o, k < 2);
            foreach (var hr in hollow)
                Stroke(env, new[]
                {
                    new[] { hr[0], hr[1] }, new[] { hr[0] + hr[2], hr[1] },
                    new[] { hr[0] + hr[2], hr[1] + hr[3] }, new[] { hr[0], hr[1] + hr[3] },
                }, Col(env, "fg"), Math.Max(1, u), 0.8 * o, false, close: true);
            if (burst > 0.2 || idle)
            {
                double R(int k) => J.R(p.Seed, st, 99, k);
                var right = R(1) < 0.5;
                var x = right ? bb.X1 + gap : bb.X0 - gap;
                var y = J.Lerp(bb.Y0, bb.Y1, R(2));
                Label(env, $"0x{(J.H(p.Seed, st, 5) % 0xffff).ToString("X4", CultureInfo.InvariantCulture)}", x, y - 10 * u,
                    JO("size", FS(env) * 0.75, "align", right ? "left" : "right", "color", Col(env, "accent"), "alpha", o));
            }
        },
    });

    private static string? ColOr(Env env, string a, string b)
    {
        var v = Col(env, a);
        return !string.IsNullOrEmpty(v) ? v : Col(env, b);
    }

    // ---------------- concentricSquares（同心四角） ----------------

    private static void RegisterConcentricSquares() => Reg0("concentricSquares", new DecorDef
    {
        Name = "同心四角",
        Tags = new[] { "graphic", "calm", "editorial" },
        W = 1,
        Layer = "front",
        Draw = (env, p) =>
        {
            if (env.Pass != "main") return;
            var bb = GetBB(env, env.Body);
            var sc = env.Sc!;
            var u = U(env);
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0) return;
            var s = J.Clamp(Math.Min(env.W, env.H) * 0.17, 100 * u, 220 * u);
            var sp = p.Corner ? CornerSpot(env, bb, s * 1.2, s * 1.2, p) : NearBB(env, bb, s * 1.2, s * 1.2, p, 24 * u);
            var cx = sp.Cx;
            var cy = sp.Cy;
            var n = 5 + p.N;
            var lw = Math.Max(1, 1.2 * u);
            var tunnel = p.V % 2 == 1;
            var a = o * (sp.Ok ? 1 : 0.35);
            for (var i = 0; i < n; i++)
            {
                double sq, rot, al = 1;
                if (tunnel)
                {
                    var f = (i / (double)n + env.Ltb * 0.18) % 1;
                    sq = s * 0.5 * (0.08 + 0.92 * f);
                    rot = 45 * (p.R > 0.5 ? 1 : 0);
                    al = Math.Sin(Math.PI * f);
                }
                else
                {
                    sq = s * 0.5 * (1 - i / (double)n * 0.86);
                    rot = i * (6 + p.R * 8) + env.Ltb * 7 * (p.Right ? 1 : -1);
                }
                var e = J.E.OutExpo(J.Clamp((env.Lt - i * 0.045) / 0.5));
                if (e <= 0) continue;
                var pts = new List<double[]>();
                for (var k = 0; k <= 4; k++)
                {
                    var an = (rot + 45 + k * 90) * J.DEG;
                    pts.Add(new[] { cx + Math.Cos(an) * sq * Math.Sqrt(2), cy + Math.Sin(an) * sq * Math.Sqrt(2) });
                }
                Stroke(env, Part(pts, 0, e), i == p.N ? Col(env, "accent") : Col(env, "fg"),
                    i == p.N ? lw * 1.6 : lw, a * al * (0.45 + 0.55 * (1 - i / (double)n)));
            }
            env.Circle(cx, cy, 2.4 * u, Col(env, "accent"), null, 0, a * InE(env, 0.3, 0.3), false);
        },
    });

    // ---------------- triangleSpin（回転三角） ----------------

    private static void RegisterTriangleSpin() => Reg0("triangleSpin", new DecorDef
    {
        Name = "回転三角",
        Tags = new[] { "graphic", "pop", "glitch" },
        W = 0.9,
        Layer = "front",
        Draw = (env, p) =>
        {
            var bb = GetBB(env, env.Body);
            var w2 = env.W;
            var h2 = env.H;
            var sc = env.Sc!;
            var u = U(env);
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0) return;
            var e = J.E.OutExpo(J.Clamp(env.Lt / 0.6));
            var lw = Math.Max(1, 1.4 * u);
            double[][] Tri(double cx, double cy, double rr, double rot)
            {
                var l = new List<double[]>();
                for (var k = 0; k <= 3; k++)
                {
                    var an = (rot - 90 + k * 120) * J.DEG;
                    l.Add(new[] { cx + Math.Cos(an) * rr, cy + Math.Sin(an) * rr });
                }
                return l.ToArray();
            }
            var dir = p.Right ? 1 : -1;
            var rin = Hypot(Bw(bb) / 2, Bh(bb) / 2) + 18 * u;
            if (p.Big && rin * 2 < Math.Max(w2, h2) * 0.62)
            {
                var cx = (bb.X0 + bb.X1) / 2;
                var cy = (bb.Y0 + bb.Y1) / 2;
                var rr = rin * 2 * (1 + 0.08 * J.E.InCubic(env.POut));
                var rot = dir * ((1 - e) * -70 + env.Ltb * 5) + p.R * 120;
                if (env.Pass != "main") return;
                Stroke(env, Part(Tri(cx, cy, rr, rot), 0, e), Col(env, "fg"), lw, 0.75 * o);
                Stroke(env, Part(Tri(cx, cy, rr * 1.07, -rot * 0.6 + 60), 0, J.E.OutExpo(J.Clamp((env.Lt - 0.12) / 0.6))),
                    p.Accent ? Col(env, "accent") : Col(env, "sub"), lw, 0.45 * o);
                return;
            }
            var s = J.Clamp(Math.Min(w2, h2) * 0.19, 110 * u, 230 * u);
            var sp = p.Corner ? CornerSpot(env, bb, s, s, p) : NearBB(env, bb, s, s, p, 24 * u);
            var a = o * (sp.Ok ? 1 : 0.35);
            var cx2 = sp.Cx;
            var cy2 = sp.Cy + s * 0.06;
            var r0 = s * 0.42;
            var r1 = dir * (env.Ltb * 16 + (1 - e) * -90) + p.R * 120;
            var r2 = -(dir) * (env.Ltb * 24 + (1 - e) * -140) + 60;
            Stroke(env, Part(Tri(cx2, cy2, r0, r1), 0, e), Col(env, "fg"), lw * 1.1, a);
            Stroke(env, Part(Tri(cx2, cy2, r0 * 0.62, r2), 0, J.E.OutExpo(J.Clamp((env.Lt - 0.1) / 0.6))), Col(env, "sub"), lw, 0.8 * a);
            var q = J.E.OutBack(J.Clamp((env.Lt - 0.25) / 0.35), 1.8);
            if (q > 0)
            {
                var an = (env.Ltb * 50 * dir + p.R * 360) * J.DEG;
                var rr = r0 * 1.18;
                Polys(env, new[] { Tri(cx2 + Math.Cos(an) * rr, cy2 + Math.Sin(an) * rr, r0 * 0.13 * q, env.Ltb * 90 * dir) },
                    Col(env, "accent"), a, true);
            }
            env.Circle(cx2, cy2, 2.2 * u, Col(env, "accent"), null, 0, a * e, false);
        },
    });

    /// <summary>超椭圆半径（JS superR）。</summary>
    private static double SuperR(double a, double b, double pp, double th)
    {
        var c = Math.Abs(Math.Cos(th));
        var s = Math.Abs(Math.Sin(th));
        return 1 / Math.Pow(Math.Pow(c / a, pp) + Math.Pow(s / b, pp), 1 / pp);
    }

    // ---------------- lineBurst（放射線） ----------------

    private static void RegisterLineBurst() => Reg0("lineBurst", new DecorDef
    {
        Name = "放射線",
        Tags = new[] { "pop", "graphic", "emotional" },
        W = 1.1,
        Layer = "front",
        Draw = (env, p) =>
        {
            var bb = GetBB(env, env.Body);
            var sc = env.Sc!;
            var u = U(env);
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0 || env.Pass != "main") return;
            var cx = (bb.X0 + bb.X1) / 2;
            var cy = (bb.Y0 + bb.Y1) / 2;
            var pad = 18 * u + Bh(bb) * 0.14;
            var a0 = Bw(bb) / 2 * 1.08 + pad;
            var b0 = Bh(bb) / 2 * 1.12 + pad;
            const int m = 180;
            var pts = new List<double[]>();
            var cum = new List<double> { 0 };
            for (var i = 0; i <= m; i++)
            {
                var th = i / (double)m * J.TAU;
                var r = SuperR(a0, b0, 4, th);
                pts.Add(new[] { cx + Math.Cos(th) * r, cy + Math.Sin(th) * r });
                if (i > 0) cum.Add(cum[i - 1] + Hypot(pts[i][0] - pts[i - 1][0], pts[i][1] - pts[i - 1][1]));
            }
            var l = cum[m];
            var n = (int)Math.Round(J.Clamp(l / (34 * u), 24, 76) * (0.8 + 0.1 * p.N));
            var bt = BeatOf(env);
            var beat = bt.Has ? Math.Exp(-bt.Since * 7) : 0.5 + 0.5 * Math.Sin(env.Ltb * 5);
            var main = new List<double[]>();
            var acc = new List<double[]>();
            var pOut = J.E.InCubic(env.POut);
            var j = 1;
            for (var i = 0; i < n; i++)
            {
                var s = ((i + 0.5 + J.Rs(p.Seed, i, 1) * 0.3) / n) * l;
                while (j < m && cum[j] < s) j++;
                var k = (s - cum[j - 1]) / Math.Max(1e-6, cum[j] - cum[j - 1]);
                var pp = L2(pts[j - 1], pts[j], k);
                var tx = pts[j][0] - pts[j - 1][0];
                var ty = pts[j][1] - pts[j - 1][1];
                var tl = Hypot(tx, ty);
                if (tl == 0) tl = 1;
                tx /= tl;
                ty /= tl;
                var nx = ty;
                var ny = -tx;
                if (nx * (pp[0] - cx) + ny * (pp[1] - cy) < 0)
                {
                    nx = -nx;
                    ny = -ny;
                }
                var len = J.Rr(10, 30, p.Seed, i, 3) * u * (p.Big ? 1.5 : 1) * (1 + 0.2 * beat * J.R(p.Seed, i, 4));
                var sE = J.E.OutExpo(J.Clamp((env.Lt - J.R(p.Seed, i, 5) * 0.18) / 0.35));
                if (sE <= 0) continue;
                var d0 = J.R(p.Seed, i, 2) * 8 * u + pOut * 60 * u;
                var d1 = d0 + len * sE;
                (i % 6 == 0 ? acc : main).Add(new[] { pp[0] + nx * d0, pp[1] + ny * d0, pp[0] + nx * d1, pp[1] + ny * d1 });
            }
            Segs(env, main, Col(env, "fg"), 1.6 * u, 0.85 * o, false, SKStrokeCap.Round);
            Segs(env, acc, Col(env, "accent"), 2.4 * u, o, false, SKStrokeCap.Round);
        },
    });

    // ---------------- plusGrid（プラス格子） ----------------

    private static void RegisterPlusGrid() => Reg0("plusGrid", new DecorDef
    {
        Name = "プラス格子",
        Tags = new[] { "graphic", "editorial", "calm" },
        W = 1,
        Layer = "front",
        Draw = (env, p) =>
        {
            if (env.Pass != "main") return;
            var bb = GetBB(env, env.Body);
            var sc = env.Sc!;
            var u = U(env);
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0) return;
            var cols = 4 + p.N + p.V % 3;
            var rows = 2 + p.V % 2 + (p.Big ? 1 : 0);
            var s = J.Clamp(Math.Min(env.W, env.H) * 0.034, 24 * u, 44 * u);
            var ps = s * 0.22;
            var w = (cols - 1) * (double)s + ps * 2;
            var h = (rows - 1) * (double)s + ps * 2;
            var sp = p.Corner ? CornerSpot(env, bb, w, h, p) : NearBB(env, bb, w, h, p, 26 * u);
            var a = o * (sp.Ok ? 1 : 0.35);
            var list = new List<double[]>();
            var hl = (int)Math.Floor(env.Ltb * 7 + p.R * 50) % (cols * rows);
            var hx = 0.0;
            var hy = 0.0;
            for (var j = 0; j < rows; j++)
                for (var i = 0; i < cols; i++)
                {
                    var q = J.E.OutBack(J.Clamp((env.Lt - (i + j) * 0.03) / 0.3), 2);
                    if (q <= 0) continue;
                    var x = sp.X + ps + i * s;
                    var y = sp.Y + ps + j * s;
                    var k = ps * q;
                    if (j * cols + i == hl && env.Lt > 0.5)
                    {
                        hx = x;
                        hy = y;
                        continue;
                    }
                    list.Add(new[] { x - k, y, x + k, y });
                    list.Add(new[] { x, y - k, x, y + k });
                }
            Segs(env, list, Col(env, "sub"), Math.Max(1, 1.3 * u), a);
            if (hx != 0)
            {
                var k2 = ps * 1.5;
                Segs(env, new[] { new[] { hx - k2, hy - k2, hx + k2, hy + k2 }, new[] { hx - k2, hy + k2, hx + k2, hy - k2 } },
                    Col(env, "accent"), 1.8 * u, a);
            }
        },
    });

    // ---------------- guides（ガイド線） ----------------

    private static void RegisterGuides() => Reg0("guides", new DecorDef
    {
        Name = "ガイド線",
        Tags = new[] { "editorial", "graphic", "calm" },
        W = 1,
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
            var pad = 10 * u + Bh(bb) * 0.05;
            var e = J.E.OutExpo(J.Clamp(env.Lt / 0.6));
            var lw = Math.Max(1, 1.1 * u);
            var x0 = bb.X0 - pad;
            var x1 = bb.X1 + pad;
            var y0 = bb.Y0 - pad;
            var y1 = bb.Y1 + pad;
            var cx = (x0 + x1) / 2;
            var cy = (y0 + y1) / 2;
            var acc2 = Col(env, "accent2");
            var col = p.Accent ? Col(env, "accent")
                : !string.IsNullOrEmpty(acc2) && J.Contrast(acc2!, Col(env, "bg") ?? "#000000") > 2 ? acc2 : Col(env, "accent");
            var hw = w2 * 0.55 * e;
            var hh = h2 * 0.55 * e;
            var a = 0.75 * o;
            var vert = p.Big || p.V % 2 == 1 || Bh(bb) > Bw(bb);
            foreach (var y in new[] { y0, y1 })
                if (y > 0 && y < h2)
                    Dash(env, cx - hw, y, cx + hw, y, 7 * u, 5 * u, col, lw, a);
            if (vert)
                foreach (var x in new[] { x0, x1 })
                    if (x > 0 && x < w2)
                        Dash(env, x, cy - hh, x, cy + hh, 7 * u, 5 * u, col, lw, a);
            var ea = InE(env, 0.3, 0.3) * o;
            var hs = 3.5 * u;
            foreach (var c in new[] { new[] { x0, y0 }, new[] { x1, y0 }, new[] { x0, y1 }, new[] { x1, y1 } })
                Stroke(env, new[] { new[] { c[0] - hs, c[1] - hs }, new[] { c[0] + hs, c[1] - hs }, new[] { c[0] + hs, c[1] + hs }, new[] { c[0] - hs, c[1] + hs } },
                    Col(env, "fg"), lw, ea, false, close: true);
            var fs = FS(env) * 0.75;
            var m = MG(env) * 0.5;
            Label(env, $"Y {Pad2(y0, 4)}", m, y0 - fs * 0.8, JO("size", fs, "color", col, "alpha", ea));
            Label(env, $"Y {Pad2(y1, 4)}", m, y1 + fs * 0.9, JO("size", fs, "color", col, "alpha", ea));
            if (vert) Label(env, $"X {Pad2(x1, 4)}", x1 + 6 * u, m + fs * 0.6, JO("size", fs, "color", col, "alpha", ea));
        },
    });

    // ---------------- waveLine（波線） ----------------

    private static (bool Low, double Room, double Y) FreeBand(Env env, BBox bb, bool low)
    {
        var h = env.H;
        var m = MG(env);
        var above = bb.Y0 - m * 0.5;
        var below = h - m * 0.5 - bb.Y1;
        var lo = low;
        if ((lo ? below : above) < 60 * U(env) && (lo ? above : below) > (lo ? below : above)) lo = !lo;
        var room = lo ? below : above;
        var d = Math.Min(room * 0.5, 40 * U(env) + room * 0.22);
        return (lo, room, lo ? bb.Y1 + d : bb.Y0 - d);
    }

    private static void RegisterWaveLine() => Reg0("waveLine", new DecorDef
    {
        Name = "波線",
        Tags = new[] { "calm", "emotional", "graphic" },
        W = 1,
        Layer = "front",
        Draw = (env, p) =>
        {
            if (env.Pass != "main") return;
            var bb = GetBB(env, env.Body);
            var w2 = env.W;
            var sc = env.Sc!;
            var u = U(env);
            var m = MG(env);
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0) return;
            var fb = FreeBand(env, bb, p.Low);
            if (fb.Room < 30 * u) return;
            var a0 = J.Clamp(fb.Room * 0.1, 5 * u, 14 * u);
            var lam = J.Clamp(Math.Min(w2, env.H) * 0.07, 44 * u, 96 * u);
            var dir = p.Right ? 1 : -1;
            var ph = env.Ltb * 2.4 * dir;
            var x0 = (double)m;
            var x1 = (double)(env.W - m);
            var e = J.E.InOutCubic(J.Clamp(env.Lt / 0.7));
            var pOut = J.E.InCubic(env.POut);
            double[][] Mk(double amp, double off)
            {
                var l = new List<double[]>();
                for (var x = x0; x <= x1 + 0.1; x += 6 * u)
                {
                    var env2 = Math.Min(1, Math.Min((x - x0) / (lam * 1.5), (x1 - x) / (lam * 1.5)));
                    l.Add(new[] { x, fb.Y + Math.Sin((x - x0) / lam * J.TAU + ph + off) * amp * env2 });
                }
                return l.ToArray();
            }
            if (p.V % 3 == 2)
            {
                var pts = Mk(a0, 0);
                var d = new List<double[]>();
                for (var i = 0; i < pts.Length; i += 2)
                {
                    var f = i / (double)pts.Length;
                    if (f <= e && f >= pOut) d.Add(new[] { pts[i][0], pts[i][1], 1.8 * u });
                }
                Dots(env, d, Col(env, "fg"), 0.85 * o);
            }
            else
            {
                Stroke(env, Part(Mk(a0, 0), pOut, e), Col(env, "fg"), 1.4 * u, 0.85 * o);
                Stroke(env, Part(Mk(a0 * 0.55, Math.PI * 0.6), pOut, J.E.InOutCubic(J.Clamp((env.Lt - 0.12) / 0.7))),
                    p.Accent ? Col(env, "accent") : Col(env, "sub"), Math.Max(1, u), 0.6 * o);
            }
        },
    });

    // ---------------- spiralLine（渦巻き） ----------------

    private static void RegisterSpiralLine() => Reg0("spiralLine", new DecorDef
    {
        Name = "渦巻き",
        Tags = new[] { "graphic", "pop", "calm" },
        W = 0.8,
        Layer = "front",
        Draw = (env, p) =>
        {
            if (env.Pass != "main") return;
            var bb = GetBB(env, env.Body);
            var sc = env.Sc!;
            var u = U(env);
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0) return;
            var rr = J.Clamp(Math.Min(env.W, env.H) * 0.085, 54 * u, 104 * u);
            var sp = NearBB(env, bb, rr * 2.3, rr * 2.3, p, 20 * u);
            var cx = sp.Cx;
            var cy = sp.Cy;
            var a = o * (sp.Ok ? 1 : 0.35);
            var turns = 3 + p.N * 0.5;
            var pts = new List<double[]>();
            var sq = p.V % 2 == 1;
            var rot = env.Ltb * 18 * (p.Right ? 1 : -1) * J.DEG + p.R * J.TAU;
            if (sq)
            {
                var n = (int)Math.Round(turns * 4);
                var d = rr / (n / 2.0);
                double x = 0, y = 0;
                pts.Add(new[] { 0.0, 0.0 });
                int[][] dirs = { new[] { 1, 0 }, new[] { 0, 1 }, new[] { -1, 0 }, new[] { 0, -1 } };
                for (var k = 0; k < n; k++)
                {
                    var l = d * (Math.Floor(k / 2.0) + 1);
                    x += dirs[k % 4][0] * l;
                    y += dirs[k % 4][1] * l;
                    pts.Add(new[] { x, y });
                }
            }
            else
            {
                var m = (int)Math.Round(turns * 40);
                for (var k = 0; k <= m; k++)
                {
                    var th = k / (double)m * turns * J.TAU;
                    var r = rr * k / (double)m;
                    pts.Add(new[] { Math.Cos(th) * r, Math.Sin(th) * r });
                }
            }
            var tp = pts.Select(q => new[] { cx + q[0] * Math.Cos(rot) - q[1] * Math.Sin(rot), cy + q[0] * Math.Sin(rot) + q[1] * Math.Cos(rot) }).ToArray();
            var e = J.E.InOutCubic(J.Clamp(env.Lt / 0.7));
            var pOut = J.E.InCubic(env.POut);
            var seg = Part(tp, pOut, e);
            Stroke(env, seg, Col(env, "fg"), 1.4 * u, 0.85 * a, false, cap: SKStrokeCap.Round, join: SKStrokeJoin.Round);
            if (seg.Length > 0)
            {
                var q2 = seg[^1];
                env.Circle(q2[0], q2[1], 3.2 * u, Col(env, "accent"), null, 0, a, false);
            }
        },
    });

    // ---------------- halftonePatch（網点） ----------------

    private static void RegisterHalftonePatch() => Reg0("halftonePatch", new DecorDef
    {
        Name = "網点",
        Tags = new[] { "graphic", "pop", "editorial" },
        W = 1,
        Layer = "back",
        Subtle = true,
        Draw = (env, p) =>
        {
            var w2 = env.W;
            var h2 = env.H;
            var sc = env.Sc!;
            var u = U(env);
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0 || env.Pass != "main") return;
            var sx = p.Right ? 1 : -1;
            var sy = p.Low ? 1 : -1;
            var ox = sx > 0 ? (double)w2 : 0;
            var oy = sy > 0 ? (double)h2 : 0;
            var pw = w2 * J.Rr(0.38, 0.58, p.Seed, 1);
            var ph = h2 * J.Rr(0.38, 0.6, p.Seed, 2);
            var g = J.Clamp(Math.Min(w2, h2) * 0.018, 12 * u, 22 * u);
            while (pw / g * (ph / g) > 1100) g *= 1.15;
            var col = p.Accent ? Col(env, "accent") : Col(env, "sub");
            var al = (p.Accent ? 0.3 : 0.2) * (Dark(env) ? 1 : 0.8) * o;
            var lines = p.V % 2 == 1;
            var grow = J.Clamp(env.Lt / 0.7) * 1.3;
            double F(double x, double y)
            {
                var d = Hypot((x - ox) / pw, (y - oy) / ph);
                return J.Clamp(1 - d) * J.Clamp((grow - d) * 3);
            }
            if (lines)
            {
                var list = new List<double[]>();
                for (var y = oy - sy * g * 0.5; Math.Abs(y - oy) < ph; y -= sy * g)
                {
                    var top = new List<double[]>();
                    var bot = new List<double[]>();
                    for (var x = ox; Math.Abs(x - ox) <= pw; x -= sx * g * 0.5)
                    {
                        var t = g * 0.46 * Math.Pow(F(x, y), 1.1);
                        top.Add(new[] { x, y - t });
                        bot.Add(new[] { x, y + t });
                    }
                    bot.Reverse();
                    list.AddRange(top.Concat(bot));
                }
                Polys(env, To2D(list), col, al);
            }
            else
            {
                var list = new List<double[]>();
                var row = 0;
                for (var y = oy - sy * g * 0.5; Math.Abs(y - oy) < ph; y -= sy * g, row++)
                    for (var x = ox - sx * (row % 2 != 0 ? g : g * 0.5); Math.Abs(x - ox) <= pw; x -= sx * g)
                    {
                        var r = g * 0.5 * Math.Pow(F(x, y), 1.2);
                        if (r > 0.4) list.Add(new[] { x, y, r });
                    }
                Dots(env, list, col, al);
            }
        },
    });

    /// <summary>扁平点列（top+bot 串成一条折线）→ 多边形列表。</summary>
    private static List<double[][]> To2D(List<double[]> flat)
    {
        var l = new List<double[][]>();
        if (flat.Count >= 3) l.Add(flat.ToArray());
        return l;
    }

    // ---------------- checkerStrip（市松の帯） ----------------

    private static void RegisterCheckerStrip() => Reg0("checkerStrip", new DecorDef
    {
        Name = "市松の帯",
        Tags = new[] { "pop", "graphic" },
        W = 0.8,
        Layer = "front",
        Draw = (env, p) =>
        {
            if (env.Pass != "main") return;
            var bb = GetBB(env, env.Body);
            var w2 = env.W;
            var sc = env.Sc!;
            var u = U(env);
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0) return;
            var v = p.V % 3;
            var rows = v == 1 ? 3 : v == 2 ? 1 : 2;
            var c = J.Clamp(Math.Min(w2, env.H) * 0.016, 10 * u, 18 * u);
            var l = Math.Min(w2 * 0.32, 400 * u);
            var h = rows * c;
            var sp = CornerSpot(env, bb, l, h + (v == 2 ? 10 * u : 0), p, 1);
            var a = o * (sp.Ok ? 1 : 0.35);
            var x0 = sp.X;
            var y0 = sp.Y + (v == 2 ? 5 * u : 0);
            var e = J.E.OutExpo(J.Clamp(env.Lt / 0.55));
            var vis = l * e;
            var off = (env.Ltb * c * 1.6 * (p.Right ? -1 : 1)) % (2 * c);
            var list = new List<double[]>();
            for (var j = 0; j < rows; j++)
                for (var i = -2; i * c < l + 2 * c; i++)
                {
                    if ((i + j) % 2 != 0) continue;
                    var x = x0 + i * c + off;
                    var w = c;
                    var k = v == 1 ? J.Clamp(1 - i * c / l * 0.95) : 1;
                    var cw = w * k;
                    var ch = c * k;
                    var cx0 = x + (w - cw) / 2;
                    var xa = Math.Max(cx0, x0);
                    var xb = Math.Min(cx0 + cw, x0 + vis);
                    if (xb <= xa) continue;
                    list.Add(new[] { xa, y0 + j * c + (c - ch) / 2, xb - xa, ch });
                }
            Rects(env, list, p.Accent ? Col(env, "accent") : Col(env, "fg"), 0.9 * a);
            if (v == 2)
            {
                var lw = Math.Max(1, u);
                Segs(env, new[] { new[] { x0, y0 - 5 * u, x0 + vis, y0 - 5 * u }, new[] { x0, y0 + h + 5 * u, x0 + vis, y0 + h + 5 * u } },
                    Col(env, "fg"), lw, 0.8 * a);
            }
        },
    });

    // ---------------- beatRing（拍の輪） ----------------

    private static void RegisterBeatRing() => Reg0("beatRing", new DecorDef
    {
        Name = "拍の輪",
        Tags = new[] { "pop", "emotional", "calm" },
        W = 1,
        Layer = "back",
        Subtle = true,
        Draw = (env, p) =>
        {
            if (env.Pass != "main") return;
            var w2 = env.W;
            var h2 = env.H;
            var sc = env.Sc!;
            var u = U(env);
            var o = OutE(env) * InE(env, 0.4, 0, J.E.OutCubic);
            if (o <= 0.003 || env.Lt < 0) return;
            double since, len;
            var bt = BeatOf(env);
            if (bt.Has)
            {
                since = bt.Since;
                len = bt.Len;
            }
            else
            {
                len = 0.5;
                since = ((env.Ltb % len) + len) % len;
            }
            var cx = w2 / 2.0;
            var cy = h2 / 2.0;
            var r0 = Math.Min(w2, h2) * 0.2;
            var r1 = Hypot(w2, h2) * 0.55;
            var col = p.Accent ? Col(env, "accent") : Col(env, "sub");
            var @base = (p.Accent ? 0.45 : 0.4) * o;
            var dashed = p.V % 2 == 1;
            for (var k = 0; k < 3; k++)
            {
                var prog = (since + k * len) / (len * 3);
                if (prog >= 1) continue;
                var r = J.Lerp(r0, r1, J.E.OutCubic(prog));
                var al = @base * Math.Pow(1 - prog, 1.6);
                if (dashed)
                    for (var s = 0; s < 48; s++)
                        env.Arc(cx, cy, r, s * 7.5, s * 7.5 + 4, col, 1.4 * u, al, false);
                else env.Circle(cx, cy, r, null, col, (1 + 1.5 * (1 - prog)) * u, al, false);
            }
            var pulse = Math.Exp(-since * 9);
            env.Circle(cx, cy, r0 * (1 + 0.03 * pulse), null, col, (1 + 2.2 * pulse) * u, @base * 0.6, false);
        },
    });

    // ---------------- orbitDots（周回する点） ----------------

    private static void RegisterOrbitDots() => Reg0("orbitDots", new DecorDef
    {
        Name = "周回する点",
        Tags = new[] { "calm", "emotional", "graphic" },
        W = 1,
        Layer = "front",
        Draw = (env, p) =>
        {
            if (env.Pass != "main") return;
            var bb = GetBB(env, env.Body);
            var w2 = env.W;
            var sc = env.Sc!;
            var u = U(env);
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0) return;
            var cx = (bb.X0 + bb.X1) / 2;
            var cy = (bb.Y0 + bb.Y1) / 2;
            var tall = Bh(bb) > Bw(bb) * 1.2;
            var rx = tall ? Math.Max(Bw(bb) * 0.9, 50 * u) : Math.Min(Bw(bb) / 2 + 50 * u + Bw(bb) * 0.08, w2 * 0.47);
            var ry = tall ? Math.Min(Bh(bb) / 2 + 50 * u + Bh(bb) * 0.08, env.H * 0.47) : Math.Max(Bh(bb) * 0.9, 50 * u);
            var tilt = (p.R - 0.5) * 22 * J.DEG;
            var ct = Math.Cos(tilt);
            var st = Math.Sin(tilt);
            double[] Pt2(double th)
            {
                var x = Math.Cos(th) * rx;
                var y = Math.Sin(th) * ry;
                return new[] { cx + x * ct - y * st, cy + x * st + y * ct };
            }
            var pad2v = 8 * u;
            bool Inside(double[] q) => q[0] > bb.X0 - pad2v && q[0] < bb.X1 + pad2v && q[1] > bb.Y0 - pad2v && q[1] < bb.Y1 + pad2v;
            var e = J.E.InOutCubic(J.Clamp(env.Lt / 0.7));
            var th0 = p.R * J.TAU;
            const int mm = 160;
            var segsL = new List<double[]>();
            double[]? prev = null;
            for (var k = 0; k <= mm * e; k++)
            {
                var q = Pt2(th0 + k / (double)mm * J.TAU);
                if (prev != null && !Inside(q) && !Inside(prev)) segsL.Add(new[] { prev[0], prev[1], q[0], q[1] });
                prev = q;
            }
            Segs(env, segsL, Col(env, "sub"), Math.Max(1, u), 0.55 * o);
            var nd = 2 + p.N % 3;
            var dir = p.Right ? 1 : -1;
            for (var d = 0; d < nd; d++)
            {
                var w = (0.35 + d * 0.22) * dir * (1 + 2 * J.E.InCubic(env.POut));
                var th = th0 + d * J.TAU / nd + env.Ltb * w;
                var sz = (d == 0 ? 5 : 3.2) * u;
                var trail = new List<(double[] P1, double[] P2, double F)>();
                for (var k = 0; k < 10; k++)
                {
                    var a1 = th - k * 0.045 * Math.Sign(w);
                    var a2b = th - (k + 1) * 0.045 * Math.Sign(w);
                    var p1 = Pt2(a1);
                    var p2 = Pt2(a2b);
                    if (!Inside(p1) && !Inside(p2)) trail.Add((p1, p2, 1 - k / 10.0));
                }
                foreach (var (p1, p2b, f) in trail)
                    Stroke(env, new[] { p1, p2b }, d == 0 ? Col(env, "accent") : Col(env, "fg"), sz * 0.8 * f, 0.6 * f * o * e);
                var q0 = Pt2(th);
                if (!Inside(q0)) env.Circle(q0[0], q0[1], sz * e, d == 0 ? Col(env, "accent") : Col(env, "fg"), null, 0, o, false);
            }
        },
    });

    // ---------------- constellation（星座） ----------------

    private static void RegisterConstellation() => Reg0("constellation", new DecorDef
    {
        Name = "星座",
        Tags = new[] { "calm", "emotional", "editorial" },
        W = 0.9,
        Layer = "front",
        Draw = (env, p) =>
        {
            if (env.Pass != "main") return;
            var bb = GetBB(env, env.Body);
            var sc = env.Sc!;
            var u = U(env);
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0) return;
            var w = J.Clamp(Math.Min(env.W, env.H) * 0.3, 190 * u, 340 * u);
            var h = w * 0.62;
            var sp = p.Corner ? CornerSpot(env, bb, w, h, p) : NearBB(env, bb, w, h, p, 30 * u);
            var a = o * (sp.Ok ? 1 : 0.35);
            var n = 5 + p.N;
            var cells = new List<double[]>();
            for (var i = 0; i < 12; i++) cells.Add(new[] { i % 4, Math.Floor(i / 4.0), J.R(p.Seed, i, 1) });
            cells.Sort((x, y) => x[2].CompareTo(y[2]));
            var raw = cells.Take(n)
                .Select((c, i) => new[] { sp.X + w * (c[0] + 0.5 + J.Rs(p.Seed, i, 2) * 0.35) / 4, sp.Y + h * (c[1] + 0.5 + J.Rs(p.Seed, i, 3) * 0.35) / 3 })
                .ToList();
            raw.Sort((x, y) => x[0].CompareTo(y[0]));
            var pts = new List<double[]> { raw[0] };
            raw.RemoveAt(0);
            while (raw.Count > 0)
            {
                var q = pts[^1];
                var bi = 0;
                var bd = 1e18;
                for (var i = 0; i < raw.Count; i++)
                {
                    var d = Math.Pow(raw[i][0] - q[0], 2) + Math.Pow(raw[i][1] - q[1], 2);
                    if (!(d < bd)) continue;
                    bd = d;
                    bi = i;
                }
                pts.Add(raw[bi]);
                raw.RemoveAt(bi);
            }
            var path = pts.ToList();
            var branch = new[]
            {
                pts[(int)Math.Floor(n / 2.0)],
                new[] { sp.X + w * J.Rr(0.3, 0.7, p.Seed, 9), sp.Y + h * (pts[(int)Math.Floor(n / 2.0)][1] - sp.Y > h / 2 ? 0.08 : 0.92) },
            };
            var e = J.E.InOutCubic(J.Clamp((env.Lt - 0.1) / 0.8));
            Stroke(env, Part(path, 0, e), Col(env, "sub"), Math.Max(1, u), 0.7 * a);
            Stroke(env, Part(branch, 0, J.Clamp((env.Lt - 0.6) / 0.4)), Col(env, "sub"), Math.Max(1, u), 0.7 * a);
            var all = pts.Concat(new[] { branch[1] }).ToList();
            var stars = new List<double[]>();
            var big = new List<(double[] P, double Tw)>();
            for (var i = 0; i < all.Count; i++)
            {
                var q = J.E.OutBack(J.Clamp((env.Lt - i * 0.06) / 0.3), 2);
                if (q <= 0) continue;
                var tw = 0.65 + 0.35 * J.Noise1(env.Ltb * 2.5 + i * 3, p.Seed);
                var r = (J.R(p.Seed, i, 4) < 0.3 ? 3.6 : 2.2) * u * q;
                stars.Add(new[] { all[i][0], all[i][1], r });
                if (r > 3 * u * q && i % 2 == 0) big.Add((all[i], tw));
            }
            Dots(env, stars, Col(env, "fg"), a);
            foreach (var (q0, tw) in big)
            {
                var l = 11 * u * tw;
                Segs(env, new[] { new[] { q0[0] - l, q0[1], q0[0] + l, q0[1] }, new[] { q0[0], q0[1] - l, q0[0], q0[1] + l } },
                    Col(env, "accent"), Math.Max(1, u), a * tw);
            }
            Label(env, $"C-{Pad2((env.Cut?.Line ?? 0) + 1)}", all[0][0] + 8 * u, all[0][1] - 12 * u,
                JO("size", FS(env) * 0.72, "alpha", a * InE(env, 0.3, 0.5)));
        },
    });

    /// <summary>粒子越靠近歌词越透明（0..1）。JS clearOf。</summary>
    private static double ClearOf(BBox bb, double x, double y, double pad, double soft)
    {
        var dx = Math.Max(Math.Max(bb.X0 - pad - x, 0), x - bb.X1 - pad);
        var dy = Math.Max(Math.Max(bb.Y0 - pad - y, 0), y - bb.Y1 - pad);
        return J.Clamp(Hypot(dx, dy) / soft);
    }

    private static double Wrap(double v, double lo, double span) => lo + (((v - lo) % span) + span) % span;

    // ---------------- confetti（紙吹雪） ----------------

    private static void RegisterConfetti() => Reg0("confetti", new DecorDef
    {
        Name = "紙吹雪",
        Tags = new[] { "pop", "emotional" },
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
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0) return;
            var n = Math.Min(56, 26 + p.N * 9);
            var cols = new[] { Col(env, "accent"), ColOr(env, "accent2", "fg"), Col(env, "fg"), Col(env, "sub") };
            var buckets = new List<double[][]>[4] { new(), new(), new(), new() };
            var pad = 10 * u;
            var soft = 24 * u;
            for (var i = 0; i < n; i++)
            {
                double R(int k) => J.R(p.Seed, i, k);
                var q = J.E.OutBack(J.Clamp((env.Lt - R(1) * 0.35) / 0.3), 1.8);
                if (q <= 0) continue;
                var vy = (70 + R(2) * 110) * u * (1 + J.E.InCubic(env.POut));
                var span = h2 + 80 * u;
                var y = Wrap(R(3) * h2 + vy * env.Ltb, -40 * u, span);
                var x = R(4) * w2 + Math.Sin(env.Ltb * (1 + R(5) * 1.8) + R(6) * 6) * (10 + R(7) * 30) * u;
                var al = ClearOf(bb, x, y, pad, soft);
                if (al <= 0.02) continue;
                var w = (11 + R(8) * 11) * u * q;
                var h = w * (0.4 + R(9) * 0.3);
                var rot = R(10) * J.TAU + env.Ltb * (R(11) - 0.5) * 8;
                var flip = Math.Max(0.15, Math.Abs(Math.Cos(env.Ltb * (2 + R(12) * 4) + R(13) * 6)));
                var c = Math.Cos(rot);
                var s = Math.Sin(rot);
                var hw = w / 2 * flip;
                var hh = h / 2;
                double[][] corners = { new[] { -hw, -hh }, new[] { hw, -hh }, new[] { hw, hh }, new[] { -hw, hh } };
                var pts = corners.Select(q0 => new[] { x + q0[0] * c - q0[1] * s, y + q0[0] * s + q0[1] * c }).ToArray();
                if (al < 1)
                {
                    if (al > 0.5) buckets[i % 4].Add(pts);
                }
                else buckets[i % 4].Add(pts);
            }
            for (var k = 0; k < 4; k++) Polys(env, buckets[k], cols[k], 0.95 * o);
        },
    });

    // 花瓣形状（单位轮廓，JS PETAL IIFE）
    private static readonly double[][] Petal = BuildPetal();

    private static double[][] BuildPetal()
    {
        var h = new List<double[]>
        {
            new[] { 0.0, -0.5 }, new[] { 0.16, -0.38 }, new[] { 0.3, -0.18 }, new[] { 0.36, 0.04 },
            new[] { 0.32, 0.24 }, new[] { 0.22, 0.42 }, new[] { 0.1, 0.5 }, new[] { 0.0, 0.4 },
        };
        var mirror = h.Skip(1).Take(h.Count - 2).Reverse().Select(q => new[] { -q[0], q[1] });
        return h.Concat(mirror).ToArray();
    }

    // ---------------- petals（花びら） ----------------

    private static void RegisterPetals() => Reg0("petals", new DecorDef
    {
        Name = "花びら",
        Tags = new[] { "emotional", "calm", "pop" },
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
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0) return;
            var n = Math.Min(26, 10 + p.N * 5);
            var dir = p.Right ? 1 : -1;
            var ca = new List<double[][]>();
            var cb = new List<double[][]>();
            for (var i = 0; i < n; i++)
            {
                double R(int k) => J.R(p.Seed, i, k);
                var q = J.E.OutCubic(J.Clamp((env.Lt - R(1) * 0.4) / 0.35));
                if (q <= 0) continue;
                var vy = (40 + R(2) * 60) * u;
                var vx = dir * (30 + R(3) * 50) * u;
                var x = Wrap(R(4) * w2 + vx * env.Ltb + Math.Sin(env.Ltb * (0.8 + R(5)) + R(6) * 6) * 30 * u, -60 * u, w2 + 120 * u);
                var y = Wrap(R(7) * h2 + vy * env.Ltb, -60 * u, h2 + 120 * u);
                var al = ClearOf(bb, x, y, 12 * u, 28 * u);
                if (al <= 0.5) continue;
                var l = (24 + R(8) * 20) * u * q;
                var rot = R(9) * J.TAU + env.Ltb * (R(10) - 0.5) * 3;
                var fx = Math.Max(0.2, Math.Abs(Math.Cos(env.Ltb * (1.2 + R(11) * 2) + R(12) * 6)));
                var c = Math.Cos(rot);
                var s = Math.Sin(rot);
                var pts = Petal.Select(q0 =>
                {
                    var x0 = q0[0] * l * fx;
                    var y0 = q0[1] * l;
                    return new[] { x + x0 * c - y0 * s, y + x0 * s + y0 * c };
                }).ToArray();
                (R(13) < 0.7 ? ca : cb).Add(pts);
            }
            Polys(env, ca, Col(env, "accent"), 0.9 * o);
            Polys(env, cb, Dark(env) ? Col(env, "fg") : Col(env, "sub"), 0.85 * o);
        },
    });

    // ---------------- rainStreaks（雨の筋） ----------------

    private static void RegisterRainStreaks() => Reg0("rainStreaks", new DecorDef
    {
        Name = "雨の筋",
        Tags = new[] { "emotional", "calm", "glitch" },
        W = 0.9,
        Layer = "back",
        Subtle = true,
        Draw = (env, p) =>
        {
            var w2 = env.W;
            var h2 = env.H;
            var sc = env.Sc!;
            var u = U(env);
            var o = OutE(env) * InE(env, 0.45, 0, J.E.OutCubic);
            if (o <= 0.003 || env.Lt < 0 || env.Pass != "main") return;
            var n = Math.Min(110, 60 + p.N * 16);
            var sl = (p.Right ? 1 : -1) * (8 + p.R * 10) * J.DEG;
            var tn = Math.Tan(sl);
            var b = new List<double[]>[3] { new(), new(), new() };
            for (var i = 0; i < n; i++)
            {
                double R(int k) => J.R(p.Seed, i, k);
                var z = R(1);
                var v = (1200 + z * 1300) * u;
                var len = (36 + z * 80) * u;
                var span = h2 + len + 80 * u;
                var y = Wrap(R(2) * span + v * env.Ltb, -len - 40 * u, span);
                var x = R(3) * (w2 + h2 * Math.Abs(tn)) - (tn > 0 ? h2 * tn : 0) + y * tn;
                b[z < 0.4 ? 0 : z < 0.8 ? 1 : 2].Add(new[] { x, y, x + len * tn, y + len });
            }
            var col = Dark(env) ? Col(env, "sub") : Col(env, "fg");
            var k = Dark(env) ? 1 : 0.75;
            Segs(env, b[0], col, Math.Max(1, 0.9 * u), 0.18 * o * k);
            Segs(env, b[1], col, Math.Max(1, 1.2 * u), 0.3 * o * k);
            Segs(env, b[2], col, 1.6 * u, 0.46 * o * k);
        },
    });

    // ---------------- snow（雪） ----------------

    private static void RegisterSnow() => Reg0("snow", new DecorDef
    {
        Name = "雪",
        Tags = new[] { "calm", "emotional" },
        W = 0.8,
        Layer = "back",
        Subtle = true,
        Draw = (env, p) =>
        {
            var w2 = env.W;
            var h2 = env.H;
            var sc = env.Sc!;
            var u = U(env);
            var o = OutE(env) * InE(env, 0.5, 0, J.E.OutCubic);
            if (o <= 0.003 || env.Lt < 0 || env.Pass != "main") return;
            var n = Math.Min(90, 46 + p.N * 14);
            var l = new List<double[]>[3] { new(), new(), new() };
            var halo = new List<double[]>();
            for (var i = 0; i < n; i++)
            {
                double R(int k) => J.R(p.Seed, i, k);
                var z = R(1);
                var lay = z < 0.5 ? 0 : z < 0.85 ? 1 : 2;
                var rad = new[] { 2.0, 3.4, 5.6 }[lay] * u * (0.8 + R(2) * 0.4);
                var vy = (22 + lay * 22 + R(3) * 14) * u;
                var y = Wrap(R(4) * h2 + vy * env.Ltb, -10 * u, h2 + 20 * u);
                var x = Wrap(R(5) * w2 + Math.Sin(env.Ltb * (0.6 + R(6)) + R(7) * 6) * (8 + lay * 10) * u + (p.Right ? 1 : -1) * env.Ltb * 8 * u,
                    -10 * u, w2 + 20 * u);
                l[lay].Add(new[] { x, y, rad });
                if (lay != 0) halo.Add(new[] { x, y, rad * 2.2 });
            }
            var col = Dark(env) ? Col(env, "fg") : Col(env, "sub");
            var k = Dark(env) ? 1 : 0.55;
            Dots(env, halo, col, 0.08 * o * k);
            Dots(env, l[0], col, 0.35 * o * k);
            Dots(env, l[1], col, 0.55 * o * k);
            Dots(env, l[2], col, 0.75 * o * k);
        },
    });

    // ---------------- lightLeak（光漏れ） ----------------

    private static void RegisterLightLeak() => Reg0("lightLeak", new DecorDef
    {
        Name = "光漏れ",
        Tags = new[] { "emotional", "calm", "pop" },
        W = 1,
        Layer = "back",
        Subtle = true,
        Draw = (env, p) =>
        {
            var w2 = env.W;
            var h2 = env.H;
            var sc = env.Sc!;
            var ctx = env.Ctx;
            if (env.Pass != "main" || env.Lt < 0) return;
            var o = OutE(env) * InE(env, 0.6, 0, J.E.OutCubic);
            if (o <= 0.003) return;
            var dk = Dark(env);
            string? Pick(string? c) => c != null && (dk ? J.Lum(c) > 0.2 : J.Lum(c) < 0.85) ? c : null;
            var c1 = Pick(Col(env, "accent")) ?? Pick(Col(env, "accent2")) ?? Col(env, "sub");
            var c2 = Pick(Col(env, "accent2")) ?? c1;
            if (c1 == null) return;
            var breathe = 0.85 + 0.15 * Math.Sin(env.Ltb * 1.4 + p.R * 6);
            var sx = p.Right ? 1 : -1;
            var sy = p.Low ? 1 : -1;
            var blend = dk ? SKBlendMode.Screen : SKBlendMode.Multiply;
            void Blob(double x, double y, double rr, string c, double a)
            {
                using var shader = SKShader.CreateRadialGradient(new SKPoint((float)x, (float)y), (float)rr,
                    new[] { Env.SkColor(c, a), Env.SkColor(c, a * 0.5), Env.SkColor(c, 0) },
                    new float[] { 0f, 0.35f, 1f }, SKShaderTileMode.Clamp);
                using var paint = new SKPaint { IsAntialias = true, Shader = shader, BlendMode = blend };
                ctx.DrawRect(new SKRect((float)(x - rr), (float)(y - rr), (float)(x + rr), (float)(y + rr)), paint);
            }
            var a2 = (dk ? 0.5 : 0.28) * o * breathe;
            var mm = Math.Max(w2, h2);
            var drift = env.Ltb * 18 * (p.Corner ? 1 : -1);
            Blob((sx > 0 ? w2 : 0) + sx * mm * 0.05, (sy > 0 ? h2 : 0) * 0.9 + h2 * 0.05 + drift,
                mm * J.Rr(0.42, 0.6, p.Seed, 1), c1, a2);
            if (c2 != null)
                Blob((sx > 0 ? w2 : 0) - sx * mm * 0.02, h2 * J.Rr(0.3, 0.7, p.Seed, 2) - drift,
                    mm * J.Rr(0.22, 0.32, p.Seed, 3), c2, a2 * 0.8);
            if (p.V % 2 != 0)
            {
                ctx.Save();
                ctx.Translate(sx > 0 ? w2 * 0.82f : w2 * 0.18f, h2 / 2f);
                ctx.RotateDegrees(sx * 14); // JS rotate(sx * 14 * DEG)：弧度 → 14 度
                var w = w2 * 0.12;
                using var shader = SKShader.CreateLinearGradient(
                    new SKPoint((float)-w, 0), new SKPoint((float)w, 0),
                    new[] { Env.SkColor(c1, 0), Env.SkColor(c1, a2 * 0.5), Env.SkColor(c1, 0) },
                    new float[] { 0f, 0.5f, 1f }, SKShaderTileMode.Clamp);
                using var paint = new SKPaint { IsAntialias = true, Shader = shader, BlendMode = blend };
                ctx.DrawRect(new SKRect((float)(-w + drift * 0.5), -h2, (float)(w + drift * 0.5), h2), paint);
                ctx.Restore();
            }
        },
    });

    // ---------------- bokeh（ボケ玉） ----------------

    private static void RegisterBokeh() => Reg0("bokeh", new DecorDef
    {
        Name = "ボケ玉",
        Tags = new[] { "emotional", "calm", "pop" },
        W = 1,
        Layer = "back",
        Subtle = true,
        Draw = (env, p) =>
        {
            var w2 = env.W;
            var h2 = env.H;
            var sc = env.Sc!;
            var u = U(env);
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0 || env.Pass != "main") return;
            var n = Math.Min(18, 8 + p.N * 3);
            var hex = p.V % 2 == 1;
            var mm = Math.Min(w2, h2);
            var cols = new[] { Col(env, "accent"), ColOr(env, "accent2", "fg"), Col(env, "fg") };
            var dk = Dark(env);
            for (var i = 0; i < n; i++)
            {
                double R(int k) => J.R(p.Seed, i, k);
                var f = J.E.OutCubic(J.Clamp((env.Lt - R(1) * 0.4) / 0.6));
                if (f <= 0) continue;
                var r0 = mm * (0.025 + R(2) * R(2) * 0.075);
                var rr = r0 * (1.5 - 0.5 * f);
                var x = R(3) * w2 + Math.Sin(env.Ltb * 0.5 + R(4) * 6) * 14 * u;
                var y = Wrap(R(5) * h2 - env.Ltb * (6 + R(6) * 12) * u, -r0, h2 + r0 * 2);
                var c = cols[i % 3];
                var al = (dk ? 0.06 + R(7) * 0.1 : 0.05 + R(7) * 0.07) * f * o;
                if (c == null) continue;
                if (hex)
                {
                    var pts = new List<double[]>();
                    for (var k = 0; k < 6; k++)
                    {
                        var an = (k * 60 + 15) * J.DEG;
                        pts.Add(new[] { x + Math.Cos(an) * rr, y + Math.Sin(an) * rr });
                    }
                    Polys(env, new[] { pts.ToArray() }, c, al);
                    Stroke(env, pts, c, 1.4 * u, al * 1.4, false, close: true);
                }
                else
                {
                    env.Circle(x, y, rr, c, null, 0, al, false);
                    env.Circle(x, y, rr * 0.97, null, c, 1.6 * u, al * 1.3, false);
                }
            }
        },
    });

    // ---------------- speedCorner（集中線） ----------------

    private static void RegisterSpeedCorner() => Reg0("speedCorner", new DecorDef
    {
        Name = "集中線",
        Tags = new[] { "pop", "emotional", "glitch" },
        W = 0.9,
        Layer = "front",
        Draw = (env, p) =>
        {
            var bb = GetBB(env, env.Body);
            var w2 = env.W;
            var h2 = env.H;
            var sc = env.Sc!;
            var u = U(env);
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0 || env.Pass != "main") return;
            var cx = (bb.X0 + bb.X1) / 2;
            var cy = (bb.Y0 + bb.Y1) / 2;
            var pad = 30 * u + Bh(bb) * 0.2;
            var a0 = Bw(bb) / 2 * 1.1 + pad;
            var b0 = Bh(bb) / 2 * 1.15 + pad;
            var n = Math.Min(150, 80 + p.N * 22);
            var corners = p.Corner;
            var pOut = J.E.InCubic(env.POut);
            var list = new List<double[][]>();
            for (var i = 0; i < n; i++)
            {
                double R(int k) => J.R(p.Seed, i, k);
                var th = R(1) * J.TAU;
                if (corners)
                {
                    var q = (int)Math.Floor(R(2) * 4);
                    var bas = Math.Atan2((q < 2 ? -1 : 1) * (double)h2, (q % 2 != 0 ? 1 : -1) * (double)w2);
                    th = bas + (R(3) - 0.5) * 0.7;
                }
                var c = Math.Cos(th);
                var s = Math.Sin(th);
                var tx = c > 0 ? (w2 + 4 - cx) / c : c < 0 ? (-4 - cx) / c : 1e9;
                var ty = s > 0 ? (h2 + 4 - cy) / s : s < 0 ? (-4 - cy) / s : 1e9;
                var dEdge = Math.Min(tx, ty);
                var dIn = SuperR(a0, b0, 4, th) + (20 + R(4) * 0.35 * Math.Max(0, dEdge - SuperR(a0, b0, 4, th)));
                if (dEdge - dIn < 30 * u) continue;
                var jit = 0.85 + 0.15 * J.R(p.Seed, i, unchecked((int)env.Step));
                var e = J.E.OutExpo(J.Clamp((env.Lt - R(5) * 0.15) / 0.3));
                var tip = J.Lerp(dEdge, J.Lerp(dEdge, dIn, jit), e);
                var tipO = J.Lerp(tip, dEdge, pOut);
                if (dEdge - tipO < 4 * u) continue;
                var wd = (1.2 + R(6) * 4.5) * u;
                var nx = -s * wd;
                var ny = c * wd;
                var ex = cx + c * dEdge;
                var ey = cy + s * dEdge;
                list.Add(new[] { new[] { ex + nx, ey + ny }, new[] { ex - nx, ey - ny }, new[] { cx + c * tipO, cy + s * tipO } });
            }
            Polys(env, list, Dark(env) ? Col(env, "fg") : ColOr(env, "ink", "fg"), 0.8 * o);
        },
    });

    // ---------------- risingParticles（立ち上る粒） ----------------

    private static void RegisterRisingParticles() => Reg0("risingParticles", new DecorDef
    {
        Name = "立ち上る粒",
        Tags = new[] { "emotional", "calm", "glitch" },
        W = 0.9,
        Layer = "back",
        Subtle = true,
        Draw = (env, p) =>
        {
            var w2 = env.W;
            var h2 = env.H;
            var sc = env.Sc!;
            var u = U(env);
            var o = OutE(env) * InE(env, 0.45, 0, J.E.OutCubic);
            if (o <= 0.003 || env.Lt < 0 || env.Pass != "main") return;
            var n = Math.Min(54, 24 + p.N * 10);
            var shape = p.V % 3;
            var col = p.Accent || shape == 1 ? Col(env, "accent") : Dark(env) ? Col(env, "fg") : Col(env, "sub");
            var headsPoly = new List<double[][]>[3] { new(), new(), new() };
            var headsDot = new List<double[]>[3] { new(), new(), new() };
            var trails = new List<double[]>[3] { new(), new(), new() };
            var glow = new List<double[]>();
            for (var i = 0; i < n; i++)
            {
                double R(int k) => J.R(p.Seed, i, k);
                var vy = (40 + R(1) * 90) * u;
                var span = h2 + 120 * u;
                var y = h2 + 60 * u - Wrap(R(2) * span + vy * env.Ltb, 0, span);
                var prog = J.Clamp(1 - y / h2);
                var fade = Math.Pow(Math.Sin(Math.PI * J.Clamp(prog)), 0.7);
                if (fade <= 0.05) continue;
                var ph = R(5) * 6;
                var f = 0.7 + R(4);
                var sw = 14 * u;
                var x = R(3) * w2 + Math.Sin(env.Ltb * f + ph) * sw;
                var dx = Math.Cos(env.Ltb * f + ph) * f * sw;
                var s = (1.6 + R(6) * 2.6) * u;
                var b = fade > 0.66 ? 2 : fade > 0.33 ? 1 : 0;
                var tl = vy * 0.28;
                if (shape == 2)
                    headsPoly[b].Add(new[] { new[] { x, y - s * 1.6 }, new[] { x + s, y }, new[] { x, y + s * 1.6 }, new[] { x - s, y } });
                else headsDot[b].Add(new[] { x, y, s });
                trails[b].Add(new[] { x, y + s * 1.5, x - dx * tl / vy, y + s * 1.5 + tl });
                if (s > 3 * u) glow.Add(new[] { x, y, s * 3.2 });
            }
            Dots(env, glow, col, 0.06 * o);
            var lw = Math.Max(1, u);
            for (var b = 0; b < 3; b++)
            {
                Segs(env, trails[b], col, lw, new[] { 0.08, 0.16, 0.26 }[b] * o, false, SKStrokeCap.Round);
                if (shape == 2) Polys(env, headsPoly[b], col, new[] { 0.25, 0.5, 0.85 }[b] * o);
                else Dots(env, headsDot[b], col, new[] { 0.25, 0.5, 0.85 }[b] * o);
            }
        },
    });

    // ---------------- twinkle（きらめき） ----------------

    private static void RegisterTwinkle() => Reg0("twinkle", new DecorDef
    {
        Name = "きらめき",
        Tags = new[] { "pop", "emotional", "calm" },
        W = 1,
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
            var k2 = Math.Min(7, 3 + p.N + (p.Big ? 1 : 0));
            var pad = 16 * u + Bh(bb) * 0.12;
            var x0 = bb.X0 - pad;
            var x1 = bb.X1 + pad;
            var y0 = bb.Y0 - pad;
            var y1 = bb.Y1 + pad;
            var per = 2 * (x1 - x0 + y1 - y0);
            var fills = new List<double[][]>[2] { new(), new() };
            var lines = new List<double[]>();
            for (var i = 0; i < k2; i++)
            {
                double R(int k) => J.R(p.Seed, i, k);
                var t = (i + R(1) * 0.6) / k2 * per;
                double x, y;
                if (t < x1 - x0)
                {
                    x = x0 + t;
                    y = y0;
                }
                else if ((t -= x1 - x0) < y1 - y0)
                {
                    x = x1;
                    y = y0 + t;
                }
                else if ((t -= y1 - y0) < x1 - x0)
                {
                    x = x1 - t;
                    y = y1;
                }
                else
                {
                    t -= x1 - x0;
                    x = x0;
                    y = y1 - t;
                }
                x += J.Rs(p.Seed, i, 2) * 14 * u;
                y += J.Rs(p.Seed, i, 3) * 14 * u;
                x = J.Clamp(x, 16 * u, w2 - 16 * u);
                y = J.Clamp(y, 16 * u, h2 - 16 * u);
                if (ClearOf(bb, x, y, 4 * u, 1) < 1) continue;
                var rr = (i == 0 ? 30 : 13 + R(4) * 12) * u;
                var cyc = 1.3 + R(5) * 0.8;
                var ph = ((env.Ltb - R(6) * 0.3) / cyc + R(7)) % 1;
                var intro = J.E.OutBack(J.Clamp((env.Lt - R(6) * 0.3) / 0.3), 2);
                var s = Math.Min(intro, env.Lt > 0.6 ? 0.25 + 0.75 * Math.Pow(Math.Sin(Math.PI * ph), 2) : intro) * o;
                if (s <= 0.02) continue;
                fills[i % 3 == 0 ? 1 : 0].Add(Glint(x, y, rr * s, rr * s * 0.2, 0));
                var l = rr * 1.9 * s;
                lines.Add(new[] { x - l, y, x + l, y });
                lines.Add(new[] { x, y - l, x, y + l });
            }
            Segs(env, lines, Col(env, "fg"), Math.Max(1, 0.9 * u), 0.5 * o);
            Polys(env, fills[0], Col(env, "fg"), o);
            Polys(env, fills[1], Col(env, "accent"), o);
        },
    });

    // ---------------- brushStroke（筆の払い） ----------------

    private static void RegisterBrushStroke() => Reg0("brushStroke", new DecorDef
    {
        Name = "筆の払い",
        Tags = new[] { "emotional", "editorial", "pop" },
        W = 1,
        Layer = "back",
        Draw = (env, p) =>
        {
            var w2 = env.W;
            var h2 = env.H;
            var sc = env.Sc!;
            var ctx = env.Ctx;
            var u = U(env);
            if (env.Pass != "main" || env.Lt < 0) return;
            var o = OutE(env);
            if (o <= 0.003) return;
            var v = p.V % 3;
            string?[] cands = v == 1
                ? new[] { Col(env, "dim"), Col(env, "accent2"), Col(env, "sub") }
                : new[] { Col(env, "accent"), Col(env, "accent2"), Col(env, "ghostA"), Col(env, "ghostB"), Col(env, "sub"), Col(env, "dim") };
            string? col = null;
            var al = 0.0;
            foreach (var c in cands)
            {
                if (string.IsNullOrEmpty(c)) continue;
                if (J.Contrast(c, Col(env, "bg") ?? "#000000") < 1.15) continue;
                for (var a = v == 1 ? 0.9 : 0.55; a >= 0.2; a -= 0.07)
                    if (J.Contrast(J.Mix(Col(env, "bg") ?? "#000000", c, a), Col(env, "fg") ?? "#FFFFFF") >= 3)
                    {
                        col = c;
                        al = a;
                        break;
                    }
                if (col != null) break;
            }
            if (col == null) return;
            al *= o;
            var t = Math.Min(w2, h2) * J.Rr(0.11, 0.16, p.Seed, 1);
            var yc = h2 * 0.5 + (p.Low ? 1 : -1) * h2 * J.Rr(0.0, 0.05, p.Seed, 2) + (w2 < h2 ? 0 : t * 0.1);
            var ltr = p.Right;
            var xa = w2 * J.Rr(0.06, 0.16, p.Seed, 3);
            var xb = w2 * J.Rr(0.84, 0.95, p.Seed, 4);
            var tilt = J.Rs(p.Seed, 5) * t * 0.35;
            var bow = J.Rs(p.Seed, 6) * t * 0.25;
            var head = J.E.OutCubic(J.Clamp(env.Lt / 0.5));
            const int k2 = 22;
            const int s2 = 26;
            var bands = new List<List<double[]>>[2] { new(), new() }; // 元素 = 一条带（多段折线组）
            for (var j = 0; j < k2; j++)
            {
                var f = j / (double)(k2 - 1) - 0.5;
                double R(int kk) => J.R(p.Seed, j, kk);
                var edge = Math.Abs(f) * 2;
                var t0 = R(1) * 0.05 + edge * edge * 0.06;
                var t1 = 1 - edge * J.Rr(0.12, 0.4, p.Seed, j, 2) - R(3) * 0.06;
                var te = Math.Min(t1, head);
                if (te <= t0) continue;
                var gapAt = R(4) < 0.45 ? J.Rr(0.55, 0.9, p.Seed, j, 5) : 2;
                var gapL = 0.03 + R(6) * 0.05;
                var groups = new List<List<double[]>>();
                List<double[]>? cur = null;
                for (var kk = 0; kk <= s2; kk++)
                {
                    var tt = t0 + (te - t0) * kk / (double)s2;
                    if (tt > gapAt && tt < gapAt + gapL)
                    {
                        cur = null; // 断口：当前折线收束，下一点开新折线
                        continue;
                    }
                    var tt2 = ltr ? tt : 1 - tt;
                    var x = J.Lerp(xa, xb, tt2);
                    var taper = 1 - Math.Pow(tt, 3) * 0.55;
                    var y = yc + tilt * (tt2 - 0.5) + bow * Math.Sin(tt2 * Math.PI) + f * t * taper + J.Rs(p.Seed, j, kk, 8) * 0.8 * u;
                    if (cur == null)
                    {
                        cur = new List<double[]>();
                        groups.Add(cur);
                    }
                    cur.Add(new[] { x, y });
                }
                bands[edge > 0.6 || R(7) < 0.3 ? 1 : 0].AddRange(groups);
            }
            for (var bi = 0; bi < 2; bi++)
            {
                using var path = new SKPath();
                foreach (var pts in bands[bi])
                {
                    if (pts.Count < 2) continue;
                    path.MoveTo((float)pts[0][0], (float)pts[0][1]);
                    for (var i = 1; i < pts.Count; i++) path.LineTo((float)pts[i][0], (float)pts[i][1]);
                }
                using var paint = new SKPaint
                {
                    IsAntialias = true,
                    IsStroke = true,
                    StrokeWidth = (float)(t / k2 * (bi != 0 ? 1.3 : 2.2)),
                    StrokeCap = SKStrokeCap.Round,
                    StrokeJoin = SKStrokeJoin.Round,
                    Color = Env.SkColor(col, Math.Min(1, al)),
                    BlendMode = env.Blend,
                };
                ctx.DrawPath(path, paint);
            }
        },
    });

    // ---------------- tapePieces（マスキングテープ） ----------------

    private static void RegisterTapePieces() => Reg0("tapePieces", new DecorDef
    {
        Name = "マスキングテープ",
        Tags = new[] { "pop", "emotional", "editorial" },
        W = 0.9,
        Layer = "front",
        Draw = (env, p) =>
        {
            var bb = GetBB(env, env.Body);
            var sc = env.Sc!;
            var ctx = env.Ctx;
            var u = U(env);
            if (env.Pass != "main" || env.Lt < 0) return;
            var o = OutE(env);
            if (o <= 0.003) return;
            var lt = J.Clamp(Math.Min(env.W, env.H) * 0.115, 90 * u, 150 * u) * (0.9 + p.R * 0.2);
            var ht = lt * 0.4;
            var v = p.V % 3;
            int[][] cs = p.Big
                ? new[] { new[] { -1, -1 }, new[] { 1, -1 }, new[] { 1, 1 }, new[] { -1, 1 } }
                : p.Right ? new[] { new[] { 1, -1 }, new[] { -1, 1 } } : new[] { new[] { -1, -1 }, new[] { 1, 1 } };
            var acc2 = Col(env, "accent2");
            var col = new[]
            {
                !string.IsNullOrEmpty(acc2) && J.Contrast(acc2!, Col(env, "bg") ?? "#000000") > 1.6 ? acc2 : Col(env, "sub"),
                Col(env, "accent"),
                Col(env, "sub"),
            }[v];
            for (var k = 0; k < cs.Length; k++)
            {
                var sx = cs[k][0];
                var sy = cs[k][1];
                var q = J.Clamp((env.Lt - 0.08 - k * 0.1) / 0.25);
                if (q <= 0) continue;
                var eq = J.E.OutCubic(q);
                var x = sx < 0 ? bb.X0 : bb.X1;
                var y = sy < 0 ? bb.Y0 : bb.Y1;
                var cx = x + sx * ht * 0.55;
                var cy = y + sy * ht * 0.55;
                var ang = (-sx * sy * 40 + J.Rs(p.Seed, k, 1) * 10 + (1 - eq) * 10 * sx) * J.DEG;
                var s = 1 + 0.2 * (1 - eq);
                ctx.Save();
                ctx.Translate((float)cx, (float)cy);
                ctx.RotateDegrees((float)(ang / J.DEG));
                ctx.Scale((float)s, (float)s);
                var hl = lt / 2;
                var hh = ht / 2;
                const int teeth = 5;
                var pts = new List<double[]>();
                for (var i = 0; i <= teeth; i++)
                    pts.Add(new[] { -hl + (i % 2 != 0 ? 3 : 0) * u + J.Rs(p.Seed, k, i, 2) * 2 * u, -hh + i / (double)teeth * ht });
                for (var i = teeth; i >= 0; i--)
                    pts.Add(new[] { hl - (i % 2 != 0 ? 3 : 0) * u + J.Rs(p.Seed, k, i, 3) * 2 * u, -hh + i / (double)teeth * ht });
                Polys(env, new[] { pts.ToArray() }, col, 0.62 * eq * o);
                if (v != 1)
                {
                    ctx.Save();
                    ctx.ClipRect(new SKRect((float)(-hl + 3 * u), (float)-hh, (float)(-hl + 3 * u + lt - 6 * u), (float)(-hh + ht)));
                    var list = new List<double[]>();
                    for (var x2 = -hl - ht; x2 < hl + ht; x2 += 9 * u) list.Add(new[] { x2, hh, x2 + ht, -hh });
                    Segs(env, list, Dark(env) ? Col(env, "bg") : Col(env, "fg"), 2 * u, 0.14 * eq * o);
                    ctx.Restore();
                }
                ctx.Restore();
            }
        },
    });

    /// <summary>手绘椭圆环的点列（JS handLoop）。</summary>
    private static double[][] HandLoop(Env env, BBox bb, DecorSpec p, double turns)
    {
        var u = U(env);
        var w2 = env.W;
        var h2 = env.H;
        var pad = 14 * u + Math.Min(Bw(bb), Bh(bb)) * 0.08;
        var hw = Bw(bb) / 2;
        var hh = Bh(bb) / 2;
        var cx = (bb.X0 + bb.X1) / 2;
        var cy = (bb.Y0 + bb.Y1) / 2;
        var lx = Math.Max(hw * 0.9, Math.Min(cx, w2 - cx) - 10 * u);
        var ly = Math.Max(hh * 0.9, Math.Min(cy, h2 - cy) - 10 * u);
        var a0 = Math.Min(hw * 1.22 + pad, lx);
        var b0 = Math.Min(hh * 1.22 + pad, ly);
        if (Math.Pow(hw / a0, 2) + Math.Pow(hh / b0, 2) > 1)
        {
            if (a0 < hw * 1.22 + pad)
                b0 = Math.Min(ly, hh / Math.Sqrt(Math.Max(0.06, 1 - Math.Pow(hw / a0, 2))) * 0.96 + pad * 0.3);
            else
                a0 = Math.Min(lx, hw / Math.Sqrt(Math.Max(0.06, 1 - Math.Pow(hh / b0, 2))) * 0.96 + pad * 0.3);
        }
        var th0 = (p.Right ? -0.3 : 0.3) * Math.PI - Math.PI / 2;
        var dir = p.Right ? 1 : -1;
        const int m = 140;
        var pts = new List<double[]>();
        var tilt = J.Rs(p.Seed, 1) * 2 * J.DEG;
        for (var i = 0; i <= m; i++)
        {
            var t = i / (double)m;
            var th = th0 + dir * t * turns * J.TAU;
            var k = 1 + 0.03 * J.Noise1(t * 6 + p.Seed % 97, p.Seed) + 0.05 * Math.Max(0, t * turns - 0.85);
            var x = Math.Cos(th) * a0 * k;
            var y = Math.Sin(th) * b0 * k;
            pts.Add(new[] { cx + x * Math.Cos(tilt) - y * Math.Sin(tilt), cy + x * Math.Sin(tilt) + y * Math.Cos(tilt) });
        }
        return pts.ToArray();
    }

    // ---------------- scribbleCircle（手描きの囲み） ----------------

    private static void RegisterScribbleCircle() => Reg0("scribbleCircle", new DecorDef
    {
        Name = "手描きの囲み",
        Tags = new[] { "pop", "emotional", "editorial" },
        W = 1,
        Layer = "front",
        Draw = (env, p) =>
        {
            if (env.Pass != "main") return;
            var bb = GetBB(env, env.Body);
            var sc = env.Sc!;
            var u = U(env);
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0) return;
            var pts = HandLoop(env, bb, p, p.V % 2 != 0 ? 1.85 : 1.12);
            var e = J.E.InOutCubic(J.Clamp((env.Lt - 0.05) / 0.6));
            Stroke(env, Part(pts, 0, e), p.Accent || !Dark(env) ? Col(env, "accent") : Col(env, "fg"), 3 * u, 0.95 * o, false,
                cap: SKStrokeCap.Round, join: SKStrokeJoin.Round);
        },
    });

    // ---------------- scribbleUnder（手描き下線） ----------------

    private static void RegisterScribbleUnder() => Reg0("scribbleUnder", new DecorDef
    {
        Name = "手描き下線",
        Tags = new[] { "pop", "emotional", "editorial" },
        W = 1.1,
        Layer = "front",
        Draw = (env, p) =>
        {
            if (env.Pass != "main") return;
            var bb = GetBB(env, env.Body);
            var sc = env.Sc!;
            var u = U(env);
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0) return;
            var vert = Bh(bb) > Bw(bb) * 1.3;
            var v = p.V % 3;
            var l0 = vert ? bb.Y0 : bb.X0;
            var l1 = vert ? bb.Y1 : bb.X1;
            var len = l1 - l0;
            var @base = vert ? bb.X1 + 14 * u + Bw(bb) * 0.1 : bb.Y1 + 14 * u + Bh(bb) * 0.14;
            double[] Map(double[] ac) => vert ? new[] { @base + ac[1], ac[0] } : new[] { ac[0], @base + ac[1] };
            double N(double t, int k) => J.Noise1(t * 3 + k * 7, p.Seed) * 3 * u;
            var strokes = new List<double[][]>();
            if (v == 0)
            {
                var s1 = new List<double[]>();
                var s2 = new List<double[]>();
                for (var i = 0; i <= 24; i++)
                {
                    var t = i / 24.0;
                    s1.Add(new[] { l0 - 8 * u + (len + 16 * u) * t, Math.Sin(t * Math.PI) * 5 * u + N(t, 1) - t * 4 * u });
                }
                for (var i = 0; i <= 16; i++)
                {
                    var t = i / 16.0;
                    s2.Add(new[] { l0 + len * 0.18 + len * 0.78 * t, 11 * u + Math.Sin(t * Math.PI) * 4 * u + N(t, 2) });
                }
                strokes.Add(s1.ToArray());
                strokes.Add(s2.ToArray());
            }
            else if (v == 1)
            {
                var s = new List<double[]>();
                var lam = J.Clamp(len / 22, 30 * u, 64 * u);
                for (var x = 0.0; x <= len + 0.1; x += 5 * u)
                    s.Add(new[] { l0 + x, Math.Sin(x / lam * J.TAU) * 5 * u + N(x / len, 3) });
                strokes.Add(s.ToArray());
            }
            else
            {
                var s = new List<double[]>();
                const int passes = 5;
                for (var i = 0; i <= passes; i++)
                {
                    var t = i / (double)passes;
                    s.Add(new[] { l0 + (i % 2 != 0 ? len * 0.96 : len * 0.04) + J.Rs(p.Seed, i, 4) * 8 * u, t * 14 * u + N(t, 5) });
                }
                strokes.Add(s.ToArray());
            }
            var col = p.Accent || !Dark(env) ? Col(env, "accent") : Col(env, "fg");
            for (var k = 0; k < strokes.Count; k++)
            {
                var e = J.E.InOutCubic(J.Clamp((env.Lt - 0.05 - k * 0.28) / (v == 2 ? 0.55 : 0.35)));
                Stroke(env, Part(strokes[k].Select(Map).ToArray(), 0, e), col, (k != 0 ? 2.6 : 3.4) * u, 0.95 * o, false,
                    cap: SKStrokeCap.Round, join: SKStrokeJoin.Round);
            }
        },
    });

    // ---------------- crossOut（推敲の走り書き） ----------------

    private static void RegisterCrossOut() => Reg0("crossOut", new DecorDef
    {
        Name = "推敲の走り書き",
        Tags = new[] { "editorial", "emotional" },
        W = 0.4,
        Layer = "front",
        Draw = (env, p) =>
        {
            if (env.Pass != "main") return;
            var bb = GetBB(env, env.Body);
            var sc = env.Sc!;
            var u = U(env);
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0) return;
            var txt = Regex.Replace(env.Cut?.Text ?? "", @"\s+", "");
            var arr = JLayouts.CodePoints(txt);
            if (arr.Count < 1) return;
            var w0 = Regex.Replace(env.Cut?.Words is { Length: > 0 } ? env.Cut.Words[0] : "", @"\s+", "");
            var w0Len = JLayouts.CodePoints(w0).Count;
            var word = w0Len >= 2 && w0Len <= 4
                ? w0
                : string.Join("", arr.Take(Math.Min(arr.Count, 2 + p.V % 3)));
            var font = SerifF(env);
            var fs = J.Clamp(Bh(bb) * 0.3, 22 * u, 46 * u);
            var tw = TextW(word, font, fs, 0.1);
            var sp = NearBB(env, bb, tw + 24 * u, fs * 1.8,
                new DecorSpec { Right = p.Right, Low = false, Corner = p.Corner, Big = p.Big, V = p.V, R = p.R, Seed = p.Seed, N = p.N, Accent = p.Accent }, 16 * u);
            var cx = sp.Cx;
            var cy = sp.Cy;
            var a = o * (sp.Ok ? 1 : 0.4);
            Label(env, word, cx, cy, JO("font", font, "size", fs, "align", "center", "color", Col(env, "sub"),
                "alpha", a * InE(env, 0.2), "track", 0.1));
            var col = Col(env, "accent");
            var x0 = cx - tw / 2 - 6 * u;
            var x1 = cx + tw / 2 + 6 * u;
            double[][][] strikes;
            if (p.V % 2 != 0)
                strikes = new[]
                {
                    new[] { new[] { x0, cy - fs * 0.05 }, new[] { x1, cy - fs * 0.12 } },
                    new[] { new[] { x0 + 4 * u, cy + fs * 0.1 }, new[] { x1 - 2 * u, cy + fs * 0.02 } },
                };
            else
            {
                var s = new List<double[]>();
                for (var i = 0; i <= 6; i++)
                    s.Add(new[] { J.Lerp(x0, x1, i / 6.0), cy + (i % 2 != 0 ? -1 : 1) * fs * 0.28 });
                strikes = new[] { s.ToArray() };
            }
            for (var k = 0; k < strikes.Length; k++)
                Stroke(env, Part(strikes[k], 0, J.E.InOutCubic(J.Clamp((env.Lt - 0.3 - k * 0.14) / 0.25))), col, 2.4 * u, 0.95 * a, false,
                    cap: SKStrokeCap.Round, join: SKStrokeJoin.Round);
            var e3 = J.E.OutCubic(J.Clamp((env.Lt - 0.6) / 0.3));
            if (e3 <= 0) return;
            double[] c1, c3;
            if (cy + fs * 0.6 < bb.Y0)
            {
                c1 = new[] { cx + tw * 0.25, cy + fs * 0.75 };
                c3 = new[] { J.Clamp(cx + tw * 0.35, bb.X0 + 10 * u, bb.X1 - 10 * u), bb.Y0 - 6 * u };
            }
            else if (cy - fs * 0.6 > bb.Y1)
            {
                c1 = new[] { cx + tw * 0.25, cy - fs * 0.75 };
                c3 = new[] { J.Clamp(cx + tw * 0.35, bb.X0 + 10 * u, bb.X1 - 10 * u), bb.Y1 + 6 * u };
            }
            else if (cx < bb.X0)
            {
                c1 = new[] { cx + tw / 2 + 8 * u, cy + fs * 0.3 };
                c3 = new[] { bb.X0 - 6 * u, J.Clamp(cy + fs, bb.Y0 + 10 * u, bb.Y1 - 10 * u) };
            }
            else
            {
                c1 = new[] { cx - tw / 2 - 8 * u, cy + fs * 0.3 };
                c3 = new[] { bb.X1 + 6 * u, J.Clamp(cy + fs, bb.Y0 + 10 * u, bb.Y1 - 10 * u) };
            }
            var dd = Hypot(c3[0] - c1[0], c3[1] - c1[1]);
            if (dd > 14 * u && dd < 260 * u)
            {
                var nx = -(c3[1] - c1[1]) / dd;
                var ny = (c3[0] - c1[0]) / dd;
                var bend = dd * 0.25;
                var c2 = new[] { (c1[0] + c3[0]) / 2 + nx * bend, (c1[1] + c3[1]) / 2 + ny * bend };
                var pts = new List<double[]>();
                for (var i = 0; i <= 12; i++)
                {
                    var t = i / 12.0;
                    pts.Add(new[]
                    {
                        (1 - t) * (1 - t) * c1[0] + 2 * t * (1 - t) * c2[0] + t * t * c3[0],
                        (1 - t) * (1 - t) * c1[1] + 2 * t * (1 - t) * c2[1] + t * t * c3[1],
                    });
                }
                Stroke(env, Part(pts, 0, e3), col, 2 * u, 0.9 * a, false, cap: SKStrokeCap.Round, join: SKStrokeJoin.Round);
                if (e3 > 0.95)
                {
                    var q1 = pts[^1];
                    var q2 = pts[^3];
                    var an = Math.Atan2(q1[1] - q2[1], q1[0] - q2[0]);
                    var l = 9 * u;
                    Stroke(env, new[]
                    {
                        new[] { q1[0] - Math.Cos(an - 0.5) * l, q1[1] - Math.Sin(an - 0.5) * l },
                        q1,
                        new[] { q1[0] - Math.Cos(an + 0.5) * l, q1[1] - Math.Sin(an + 0.5) * l },
                    }, col, 2 * u, 0.9 * a, false, cap: SKStrokeCap.Round, join: SKStrokeJoin.Round);
                }
            }
        },
    });

    // ---------------- highlightMark（蛍光マーカー） ----------------

    private static void RegisterHighlightMark() => Reg0("highlightMark", new DecorDef
    {
        Name = "蛍光マーカー",
        Tags = new[] { "pop", "editorial", "emotional" },
        W = 1,
        Layer = "front",
        Draw = (env, p) =>
        {
            var bb = GetBB(env, env.Body);
            var sc = env.Sc!;
            var u = U(env);
            if (env.Pass != "main" || env.Lt < 0) return;
            var o = OutE(env);
            if (o <= 0.003) return;
            var dk = Dark(env);
            var cands = new[] { Col(env, "accent"), Col(env, "accent2"), Col(env, "ghostA"), Col(env, "ghostB"), Col(env, "sub") }
                .Where(c => !string.IsNullOrEmpty(c)).ToList();
            var lighten = J.Lum(Col(env, "fg") ?? "#FFFFFF") > J.Lum(Col(env, "bg") ?? "#000000");
            var lb = J.Lum(Col(env, "bg") ?? "#000000");
            string Side(string c) => lighten
                ? J.Lum(c) > lb + 0.06 ? c : ""
                : J.Lum(c) < lb - 0.06 ? c : "";
            var pool = cands.Where(c => Side(c) != "").ToList();
            string? col = pool.FirstOrDefault(c => J.Contrast(c, Col(env, "fg") ?? "#FFFFFF") >= 1.6);
            var weak = 1.0;
            if (col == null)
            {
                if (pool.Count == 0) return;
                col = pool.OrderByDescending(c => J.Contrast(c, Col(env, "fg") ?? "#FFFFFF")).First();
                weak = 0.7;
            }
            var vert = Bh(bb) > Bw(bb) * 1.25;
            var lines = new List<(double K, double X0, double X1, double Y0, double Y1)>();
            if (bb.Boxes is { Count: > 0 } && bb.Cx != 0)
            {
                foreach (var b in bb.Boxes)
                {
                    var bx0 = bb.Cx + Num(b, "x") - Num(b, "w") / 2;
                    var bx1 = bb.Cx + Num(b, "x") + Num(b, "w") / 2;
                    var by0 = bb.Cy + Num(b, "y") - Num(b, "h") / 2;
                    var by1 = bb.Cy + Num(b, "y") + Num(b, "h") / 2;
                    var key = vert ? (bx0 + bx1) / 2 : (by0 + by1) / 2;
                    var sz = vert ? bx1 - bx0 : by1 - by0;
                    var idx = -1;
                    for (var i = 0; i < lines.Count; i++)
                        if (Math.Abs(lines[i].K - key) < sz * 0.4)
                        {
                            idx = i;
                            break;
                        }
                    if (idx < 0) lines.Add((key, bx0, bx1, by0, by1));
                    else lines[idx] = (lines[idx].K,
                        Math.Min(lines[idx].X0, bx0), Math.Max(lines[idx].X1, bx1),
                        Math.Min(lines[idx].Y0, by0), Math.Max(lines[idx].Y1, by1));
                }
                lines.Sort((x, y) => vert ? y.K.CompareTo(x.K) : x.K.CompareTo(y.K));
            }
            if (lines.Count == 0) lines.Add((0, bb.X0, bb.X1, bb.Y0, bb.Y1));
            var blend = lighten ? SKBlendMode.Screen : SKBlendMode.Multiply;
            for (var li = 0; li < Math.Min(4, lines.Count); li++)
            {
                var l = lines[li];
                var e = J.E.InOutCubic(J.Clamp((env.Lt - 0.1 - li * 0.22) / 0.4));
                if (e <= 0) continue;
                var pts = new List<double[]>();
                var s = vert ? l.X1 - l.X0 : l.Y1 - l.Y0;
                const int m = 14;
                var a0 = (vert ? l.Y0 : l.X0) - s * 0.12;
                var a1 = (vert ? l.Y1 : l.X1) + s * 0.12;
                var aE = J.Lerp(a0, a1, e);
                var c0 = vert ? l.X0 + s * 0.52 : l.Y0 + s * 0.46;
                var c1 = vert ? l.X1 + s * 0.06 : l.Y1 + s * 0.06;
                var sl = s * 0.18;
                for (var i = 0; i <= m; i++)
                {
                    var t = i / (double)m;
                    var a = J.Lerp(a0 + sl, aE, t);
                    pts.Add(vert ? new[] { c1 + J.Noise1(t * 4, p.Seed) * 1.5 * u, a } : new[] { a, c0 + J.Noise1(t * 4, p.Seed) * 1.5 * u });
                }
                for (var i = m; i >= 0; i--)
                {
                    var t = i / (double)m;
                    var a = J.Lerp(a0, aE - (e < 1 ? 0 : sl), t);
                    pts.Add(vert ? new[] { c0 + J.Noise1(t * 4 + 9, p.Seed) * 1.5 * u, a } : new[] { a, c1 + J.Noise1(t * 4 + 9, p.Seed) * 1.5 * u });
                }
                Polys(env, new[] { pts.ToArray() }, col, (dk ? 0.6 : 0.62) * o * weak, false, blend);
            }
        },
    });

    private static double Num(JObject o, string k)
    {
        var v = o[k];
        return v != null && (v.Type == JTokenType.Float || v.Type == JTokenType.Integer) ? (double)v : 0;
    }

    // ---------------- heartsStars（ハートと星） ----------------

    private static void RegisterHeartsStars() => Reg0("heartsStars", new DecorDef
    {
        Name = "ハートと星",
        Tags = new[] { "pop", "emotional" },
        W = 0.9,
        Layer = "front",
        Draw = (env, p) =>
        {
            var bb = GetBB(env, env.Body);
            var w2 = env.W;
            var h2 = env.H;
            var sc = env.Sc!;
            var u = U(env);
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0) return;
            var k2 = Math.Min(7, 3 + p.N + (p.Big ? 1 : 0));
            var v = p.V % 3;
            var pad = 26 * u + Bh(bb) * 0.15;
            var x0 = bb.X0 - pad;
            var x1 = bb.X1 + pad;
            var y0 = bb.Y0 - pad;
            var y1 = bb.Y1 + pad;
            var per = 2 * (x1 - x0 + y1 - y0);
            var acc2 = Col(env, "accent2");
            var cols = new[]
            {
                Col(env, "accent"), Col(env, "fg"),
                !string.IsNullOrEmpty(acc2) && J.Contrast(acc2!, Col(env, "bg") ?? "#000000") > 1.8 ? acc2 : Col(env, "accent"),
            };
            for (var i = 0; i < k2; i++)
            {
                double R(int k) => J.R(p.Seed, i, k);
                var t = (i + 0.3 + R(1) * 0.4) / k2 * per;
                double x, y;
                if (t < x1 - x0)
                {
                    x = x0 + t;
                    y = y0;
                }
                else if ((t -= x1 - x0) < y1 - y0)
                {
                    x = x1;
                    y = y0 + t;
                }
                else if ((t -= y1 - y0) < x1 - x0)
                {
                    x = x1 - t;
                    y = y1;
                }
                else
                {
                    t -= x1 - x0;
                    x = x0;
                    y = y1 - t;
                }
                x = J.Clamp(x, 24 * u, w2 - 24 * u);
                y = J.Clamp(y, 24 * u, h2 - 24 * u);
                if (ClearOf(bb, x, y, 6 * u, 1) < 1) continue;
                var q = J.E.OutBack(J.Clamp((env.Lt - 0.05 - i * 0.07) / 0.3), 2.4) * (1 - J.E.InCubic(env.POut));
                if (q <= 0.01) continue;
                var s = (i == 0 ? 36 : 18 + R(2) * 14) * u * q;
                var yy = y + Math.Sin(env.Ltb * 3 + i * 1.7) * 3 * u;
                var rot = Math.Sin(env.Ltb * 2 + i) * 10 + J.Rs(p.Seed, i, 3) * 15;
                var isHeart = v == 0 || (v == 2 && i % 2 == 0);
                var pts = isHeart ? Heart(0, 0, s) : Star5(0, 0, s, s * 0.45);
                var c = Math.Cos(rot * J.DEG);
                var sn = Math.Sin(rot * J.DEG);
                pts = pts.Select(q0 => new[] { x + q0[0] * c - q0[1] * sn, yy + q0[0] * sn + q0[1] * c }).ToArray();
                var col = cols[i % 3];
                if (R(4) < 0.35)
                    Stroke(env, pts, col, 2.2 * u, o, false, close: true, join: SKStrokeJoin.Round);
                else Polys(env, new[] { pts }, col, o, true);
            }
        },
    });

    // ---------------- watermarkKanji（透かし大漢字） ----------------

    private static void RegisterWatermarkKanji() => Reg0("watermarkKanji", new DecorDef
    {
        Name = "透かし大漢字",
        Tags = new[] { "editorial", "emotional", "calm" },
        W = 1,
        Layer = "back",
        Draw = (env, p) =>
        {
            if (env.Pass != "main") return;
            var w2 = env.W;
            var h2 = env.H;
            var sc = env.Sc!;
            var u = U(env);
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0) return;
            var ch = LyricChar(env, p.V);
            if (string.IsNullOrEmpty(ch)) return;
            var e = J.E.OutCubic(J.Clamp(env.Lt / 0.6));
            var s = Math.Min(h2 * 0.86, w2 * 0.92) * (1.06 - 0.06 * e);
            var x = p.Right ? w2 - s * 0.3 : s * 0.3;
            var y = h2 * 0.5 + (p.Low ? 1 : -1) * h2 * 0.06 - env.Ltb * 5 * u;
            var outline = p.V % 2 == 1;
            var font = p.V % 3 == 0 ? JLayouts.Font0(env.St, "display") ?? SerifF(env) : SerifF(env);
            if (outline)
                env.DrawItem(new JObject
                {
                    ["text"] = ch, ["font"] = font, ["size"] = s, ["x"] = x, ["y"] = y,
                    ["color"] = Col(env, "sub"), ["fill"] = false, ["stroke"] = 1.6 * u,
                    ["alpha"] = 0.32 * e * o, ["ghost"] = false,
                });
            else
                env.DrawItem(new JObject
                {
                    ["text"] = ch, ["font"] = font, ["size"] = s, ["x"] = x, ["y"] = y,
                    ["color"] = Col(env, "dim"), ["alpha"] = e * o, ["ghost"] = false,
                });
        },
    });

    // ---------------- verticalStrip（縦書き帯） ----------------

    private static void RegisterVerticalStrip() => Reg0("verticalStrip", new DecorDef
    {
        Name = "縦書き帯",
        Tags = new[] { "editorial", "calm", "emotional" },
        W = 1,
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
            var all = JLayouts.CodePoints(Regex.Replace(env.Cut?.LineText ?? env.Cut?.Text ?? "", @"\s+", "　")).Take(26).ToList();
            if (string.IsNullOrWhiteSpace(string.Join("", all))) return;
            var fs = J.Clamp(Math.Min(w2, h2) * 0.022, 15 * u, 24 * u);
            const double track = 0.28;
            var step = fs * (1 + track);
            var clear = 44 * u;
            var font = p.V % 2 != 0 ? SerifF(env) : BodyF(env);
            (bool Right, bool Top, double X, double? Y0, double? YEnd, int N)? best = null;
            foreach (var right in new[] { p.Right, !p.Right })
                foreach (var top in new[] { !p.Low, p.Low })
                {
                    var x = right ? w2 - m * 0.95 : m * 0.95;
                    var hClear = x + fs * 1.8 < bb.X0 - clear || x - fs * 1.8 > bb.X1 + clear;
                    var y0 = top ? m * 1.6 + fs : (double?)null;
                    var yEnd = top ? (double?)null : h2 - m * 1.3;
                    var avail = hClear ? h2 - m * 2.9 - fs : top ? bb.Y0 - clear - y0!.Value : yEnd!.Value - (bb.Y1 + clear);
                    var n = Math.Min(all.Count, (int)Math.Floor(avail / step));
                    if (n >= 4 && (best == null || n > best.Value.N + 1)) best = (right, top, x, y0, yEnd, n);
                }
            if (best == null) return;
            var b = best.Value;
            var chars = all.Take(b.N).ToList();
            var th = b.N * step - fs * track;
            var yy0 = b.Top ? b.Y0!.Value : b.YEnd!.Value - th;
            var shown = (int)Math.Ceiling(b.N * J.Clamp((env.Lt - 0.08) / 0.55));
            if (shown > 0)
                env.DrawItem(new JObject
                {
                    ["text"] = string.Join("", chars.Take(shown)),
                    ["font"] = font,
                    ["size"] = fs,
                    ["x"] = b.X,
                    ["y"] = yy0,
                    ["vertical"] = true,
                    ["align"] = "left",
                    ["track"] = track,
                    ["color"] = Col(env, "fg"),
                    ["alpha"] = 0.9 * o,
                    ["ghost"] = false,
                });
            var rx = b.X + (b.Right ? -1 : 1) * fs * 1.1;
            var e = J.E.OutExpo(J.Clamp(env.Lt / 0.6));
            Stroke(env, new[] { new[] { rx, yy0 - fs * 0.2 }, new[] { rx, yy0 - fs * 0.2 + (th + fs * 0.4) * e } }, Col(env, "sub"), Math.Max(1, u), 0.7 * o);
            env.Rect(rx - 2 * u, yy0 - fs * 0.2 - 12 * u, 4 * u, 8 * u, Col(env, "accent"), o * e, false);
            Label(env, Pad2((env.Cut?.Line ?? 0) + 1), b.X, yy0 - fs * 1.6, JO("size", FS(env) * 0.75, "align", "center", "alpha", o * e));
        },
    });

    private const string Az = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    // ---------------- romajiLine（ローマ字） ----------------

    private static void RegisterRomajiLine() => Reg0("romajiLine", new DecorDef
    {
        Name = "ローマ字",
        Tags = new[] { "editorial", "calm", "graphic" },
        W = 1,
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
            var raw = Regex.Replace(env.Cut?.Text ?? "", @"\s+", " ").Trim();
            if (raw.Length == 0) return;
            string? str = env.Cut?.Note != null ? env.Cut.Note : null;
            if (str == null)
            {
                var cleaned = Regex.Replace(raw, @"[、。！？!?,.・「」]", " ");
                var rj = J.Romaji(cleaned);
                str = !string.IsNullOrEmpty(rj)
                    ? Regex.Replace(rj.ToUpperInvariant(), @"\s+", " ").Trim()
                    : string.Join(" ", JLayouts.CodePoints(raw).Where(c => c.Trim().Length > 0).Take(6)
                        .Select(c => "U+" + char.ConvertToUtf32(c, 0).ToString("X", CultureInfo.InvariantCulture)));
            }
            var font = p.V % 2 != 0 ? BodyF(env) : MonoF(env);
            const double track = 0.34;
            var fs = J.Clamp(Math.Min(w2, h2) * 0.018, 12 * u, 20 * u);
            var maxW = Math.Min(w2 - m * 2.4, Math.Max(Bw(bb) * 1.15, w2 * 0.4));
            var tw = TextW(str, font, fs, track);
            if (tw > maxW)
            {
                fs = Math.Max(10 * u, fs * maxW / tw);
                tw = TextW(str, font, fs, track);
            }
            while (tw > maxW && str.Length > 4)
            {
                str = str[..^2];
                tw = TextW(str + "...", font, fs, track);
                if (tw <= maxW)
                {
                    str += "...";
                    break;
                }
            }
            var gap = 18 * u + Bh(bb) * 0.1;
            var below = p.Low;
            var y = below ? bb.Y1 + gap + fs * 0.5 : bb.Y0 - gap - fs * 0.5;
            if (y < m * 0.6 || y > h2 - m * 0.6)
            {
                below = !below;
                y = below ? bb.Y1 + gap + fs * 0.5 : bb.Y0 - gap - fs * 0.5;
            }
            var cx = J.Clamp((bb.X0 + bb.X1) / 2, m + tw / 2, w2 - m - tw / 2);
            var chars = JLayouts.CodePoints(str);
            var n = chars.Count;
            var outChars = chars.Select((c, i) =>
            {
                if (c == " ") return " ";
                var ti = 0.06 + i / (double)Math.Max(1, n) * 0.5;
                if (env.Lt >= ti) return c;
                if (env.Lt < ti - 0.22) return " ";
                return Az[(int)(J.H(p.Seed, i, unchecked((int)env.Step)) % 26)].ToString();
            }).ToList();
            Label(env, string.Join("", outChars), cx - tw / 2, y,
                JO("font", font, "size", fs, "color", Col(env, "sub"), "alpha", o * J.E.OutCubic(J.Clamp(env.Lt / 0.2)), "track", track));
            var e = J.E.OutExpo(J.Clamp((env.Lt - 0.1) / 0.5));
            var hl = 36 * u * e;
            var g = 14 * u;
            Segs(env, new[]
            {
                new[] { cx - tw / 2 - g - hl, y, cx - tw / 2 - g, y },
                new[] { cx + tw / 2 + g, y, cx + tw / 2 + g + hl, y },
            }, p.Accent ? Col(env, "accent") : Col(env, "sub"), Math.Max(1, u), 0.8 * o);
        },
    });

    /// <summary>【】形括弧的点列。JS lentil。</summary>
    private static double[][] Lentil(double xo, double yt, double yb, double aw, double tb, int side)
    {
        var pts = new List<double[]> { new[] { xo, yt }, new[] { xo - side * aw, yt } };
        const int m = 16;
        var h = yb - yt;
        for (var i = 1; i < m; i++)
        {
            var t = i / (double)m;
            var b = Math.Pow(Math.Sin(t * Math.PI), 0.7);
            pts.Add(new[] { xo - side * (aw - (aw - tb) * b), yt + h * t });
        }
        pts.Add(new[] { xo - side * aw, yb });
        pts.Add(new[] { xo, yb });
        return pts.ToArray();
    }

    // ---------------- bracketsJP（隅付き括弧） ----------------

    private static void RegisterBracketsJp() => Reg0("bracketsJP", new DecorDef
    {
        Name = "隅付き括弧",
        Tags = new[] { "pop", "graphic", "editorial" },
        W = 1,
        Layer = "front",
        Draw = (env, p) =>
        {
            var bb = GetBB(env, env.Body);
            var w2 = env.W;
            var h2 = env.H;
            var sc = env.Sc!;
            var u = U(env);
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0) return;
            var e = J.E.OutExpo(J.Clamp(env.Lt / 0.45));
            var pOut = J.E.InCubic(env.POut);
            var sl = (1 - e + pOut * 0.6) * 60 * u;
            var vert = Bh(bb) > Bw(bb) * 1.25;
            var col = p.Accent ? Col(env, "accent") : Col(env, "fg");
            var a = o * J.Clamp(env.Lt / 0.12);
            if (!vert)
            {
                var h = J.Clamp(Bh(bb) * 1.08 + 16 * u, 40 * u, h2 * 0.6) * (0.5 + 0.5 * e);
                var aw = Math.Min(h * 0.24, 26 * u + Math.Min(w2, h2) * 0.028);
                var tb = aw * 0.3;
                var gap = 12 * u + Bh(bb) * 0.05;
                var yc = (bb.Y0 + bb.Y1) / 2;
                var xl = Math.Max(bb.X0 - gap, aw + 6 * u);
                var xr = Math.Min(bb.X1 + gap, w2 - aw - 6 * u);
                Polys(env, new[]
                {
                    Lentil(xl - aw - sl, yc - h / 2, yc + h / 2, aw, tb, -1),
                    Lentil(xr + aw + sl, yc - h / 2, yc + h / 2, aw, tb, 1),
                }, col, a, true);
            }
            else
            {
                var wv = J.Clamp(Bw(bb) * 1.05 + 16 * u, 40 * u, w2 * 0.6) * (0.5 + 0.5 * e);
                var aw = Math.Min(wv * 0.24, 26 * u + Math.Min(w2, h2) * 0.028);
                var tb = aw * 0.3;
                var gap = 12 * u + Bw(bb) * 0.05;
                var xc = (bb.X0 + bb.X1) / 2;
                var xl = xc - wv / 2;
                const int m = 16;
                var yt = Math.Max(bb.Y0 - gap, aw + 6 * u) - sl;
                var yb = Math.Min(bb.Y1 + gap, h2 - aw - 6 * u) + sl;
                double[][] Mk(double yi, int dir)
                {
                    var l = new List<double[]> { new[] { xl, yi + dir * aw }, new[] { xl, yi } };
                    for (var i = 1; i < m; i++)
                    {
                        var t = i / (double)m;
                        var b = Math.Pow(Math.Sin(t * Math.PI), 0.7);
                        l.Add(new[] { xl + wv * t, yi + dir * (aw - tb) * b });
                    }
                    l.Add(new[] { xl + wv, yi });
                    l.Add(new[] { xl + wv, yi + dir * aw });
                    return l.ToArray();
                }
                Polys(env, new[] { Mk(yt, -1), Mk(yb, 1) }, col, a, true);
            }
        },
    });

    // ---------------- seal（落款） ----------------

    private static void RegisterSeal() => Reg0("seal", new DecorDef
    {
        Name = "落款",
        Tags = new[] { "editorial", "emotional", "calm" },
        W = 0.9,
        Layer = "front",
        Draw = (env, p) =>
        {
            if (env.Pass != "main") return;
            var bb = GetBB(env, env.Body);
            var sc = env.Sc!;
            var ctx = env.Ctx;
            var u = U(env);
            var o = OutE(env);
            if (o <= 0.003 || env.Lt < 0.08) return;
            var ch = LyricChar(env, 3 + p.V);
            if (string.IsNullOrEmpty(ch)) return;
            var s = J.Clamp(Math.Min(env.W, env.H) * 0.074, 48 * u, 92 * u);
            var sp = NearBB(env, bb, s * 1.3, s * 1.3,
                new DecorSpec { Right = p.V % 3 != 2, Low = true, Corner = p.Corner, Big = p.Big, V = p.V, R = p.R, Seed = p.Seed, N = p.N, Accent = p.Accent }, 14 * u);
            var prog = J.Clamp((env.Lt - 0.08) / 0.3);
            var q = J.E.OutCubic(prog);
            var a = J.Clamp(prog * 4) * o * (sp.Ok ? 1 : 0.4);
            var scl = J.Lerp(1.5, 1, q);
            var rot = (J.Rs(p.Seed, 1) * 5 - (1 - q) * 12) * J.DEG;
            var round = p.V % 2 == 1;
            ctx.Save();
            ctx.Translate((float)sp.Cx, (float)sp.Cy);
            ctx.RotateDegrees((float)(rot / J.DEG));
            ctx.Scale((float)scl, (float)scl);
            var h = s / 2;
            var pts = new List<double[]>();
            if (round)
            {
                for (var i = 0; i < 28; i++)
                {
                    var an = i / 28.0 * J.TAU;
                    var r = h * (1 + J.Rs(p.Seed, i, 2) * 0.025);
                    pts.Add(new[] { Math.Cos(an) * r, Math.Sin(an) * r });
                }
            }
            else
            {
                var c = new[] { new[] { -h, -h }, new[] { h, -h }, new[] { h, h }, new[] { -h, h } };
                for (var k = 0; k < 4; k++)
                {
                    var p0 = c[k];
                    var p1 = c[(k + 1) % 4];
                    for (var i = 0; i < 4; i++)
                    {
                        var t = i / 4.0;
                        var j = J.Rs(p.Seed, k, i, 3) * 1.3 * u;
                        pts.Add(new[] { J.Lerp(p0[0], p1[0], t) + (k % 2 != 0 ? j : 0), J.Lerp(p0[1], p1[1], t) + (k % 2 != 0 ? 0 : j) });
                    }
                }
            }
            Polys(env, new[] { pts.ToArray() }, Col(env, "accent"), a * 0.95, false);
            if (env.Pass == "main")
            {
                var inner = p.V % 3 == 2;
                if (inner)
                {
                    if (round) env.Circle(0, 0, h * 0.84, null, Col(env, "bg"), 1.6 * u, a, false);
                    else Stroke(env, new[] { new[] { -h * 0.84, -h * 0.84 }, new[] { h * 0.84, -h * 0.84 }, new[] { h * 0.84, h * 0.84 }, new[] { -h * 0.84, h * 0.84 } },
                        Col(env, "bg"), 1.6 * u, a, false, close: true);
                }
                env.DrawItem(new JObject
                {
                    ["text"] = ch,
                    ["font"] = SerifF(env) == "mincho_light" ? "mincho_bold" : SerifF(env),
                    ["size"] = s * 0.62,
                    ["x"] = 0,
                    ["y"] = s * 0.02,
                    ["color"] = Col(env, "bg"),
                    ["alpha"] = a,
                    ["ghost"] = false,
                });
                var sp2 = new List<double[]>();
                for (var i = 0; i < 9; i++)
                    sp2.Add(new[] { J.Rs(p.Seed, i, 5) * h * 0.9, J.Rs(p.Seed, i, 6) * h * 0.9, (0.6 + J.R(p.Seed, i, 7) * 1.6) * u });
                Dots(env, sp2, Col(env, "bg"), a * 0.7);
            }
            ctx.Restore();
        },
    });
}
