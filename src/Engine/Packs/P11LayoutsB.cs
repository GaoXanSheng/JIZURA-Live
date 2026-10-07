// 移植自 frontend/src/engine/11p_layoutsB.js（layout 表现包 B，pack = 'layoutsB'，27 条）。
//
// 契约与 JLayouts.cs / P11LayoutsA.cs 相同：fits(n) 筛台词；plan(rng, ctx, st) 抽参数存进
// cut.params（键名是数据契约，planner/锁定行/AE 导出共用，一字不改）；Draw(env) 绘制并写回 env.Body
// （JS render(env) 的返回值）。RNG 消费顺序逐行对位——rng.pick / rng.chance / rng.range / rng.int
// 的调用次序就是画面轨迹；JS 里靠 && / ?: 短路的抽取在 C# 里同样短路（rain 的 monos.length &&
// rng.chance()、cut.n <= 7 && rng.chance()、zoomRepeat 的 rng.chance(0.35) ? range*pick : 0）。
//
// 本包私有 helper（JS L6-122 的顶层 const 与各 layout 之间的 function）见「本包 helper」区；
// Env.cs 已有的助手（rect/line/polyPartial/circle/arc/rrect/poly/blob + drawItem）直接复用。
// 跨文件符号对位：J.fontsOf→JLayouts.FontsOf、J.measure→JLayouts.Measure、J.fitSize→JLayouts.FitSize、
// J.fastRow→JLayouts.FastRow、J.mainDraw→JLayouts.MainDraw、J.unionBB→JLayouts.UnionBb、
// J.splitLines→JLayouts.SplitLines、J.glyphCount→JLayouts.GlyphCount、J.metrics.adv→JLayouts.MetricsAdv、
// J.itemBox→JAnim.ItemBox、J.chunkText/J.segments→Planner.ChunkText/Planner.Segments、
// J.pool→JLang.Pool、J.faceOf→JLang.FaceOf、J.FONTS→JFonts.Table、
// J.romaji/fmtTime/lum/contrast/mix/rgba(本地)/r/rs/rr/h/noise1/smooth→J.*。
//
// Env 缺口（本文件内补，不改 Env.cs）：JS env.beat 是 {since,len,index}，C# Env.Beat 只装归一相位
// （Renderer.BeatNorm）——用本文件 BeatOf(env) 从 env.Plan.Beats + env.T 重建（与 09_render 的
// beatAt 同口径）。JS 的 env.energy != null 判定对位 env.Plan.Energy is { Length: > 0 }（C# 侧
// energy 缺省是 0 而不是 null）。
//
// 桩/TODO：无跨文件桩——dotMatrix 的 dotTile/dotSample 在 JS 里用 2D canvas 光栅采样，C# 本地改用
// SKBitmap（Alpha8 覆盖率采样 + 本地贴图 shader 平铺），不依赖未移植文件。

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace JizuraLive.Engine;

public static class P11LayoutsB
{
    private const string P = "layoutsB";
    private static int _installed;

    private static void Reg0(string key, LayoutDef def) => Reg.Register("layout", key, def, P);

    // ================= 本包 helper（JS L6-122 逐条对位 + 绘制/光栅本地实现） =================

    /// <summary>clean(t)：去掉所有空白（含全角空格）。JS L6。</summary>
    private static string Clean(string? t) => Regex.Replace(t ?? "", @"\s+", "");

    /// <summary>slotsOf(t)：连续空白压成单个 ' ' 槽位，按 Unicode 码点切（拉丁歌词用）。JS L8。</summary>
    private static List<string> SlotsOf(string? t) =>
        JLayouts.CodePoints(Regex.Replace((t ?? "").Trim(), @"[\s\u3000]+", " "));

    /// <summary>romaOf(t)：含假名才转大写罗马字，纯拉丁/汉字返回 null。JS L10-15。</summary>
    private static string? RomaOf(string? t)
    {
        var c = Clean(t);
        if (!Regex.IsMatch(c, @"[\u3041-\u30ff]")) return null;
        var r = J.Romaji(c);
        return string.IsNullOrEmpty(r) ? null : r.ToUpperInvariant();
    }

    /// <summary>splitL(t, per)：J.splitLines 的包装——不留纯标点行（并进上一行）。JS L17-24。</summary>
    private static string SplitL(string t, int per)
    {
        var outL = new List<string>();
        foreach (var l in JLayouts.SplitLines(t, per).Split('\n'))
        {
            if (outL.Count > 0 && JLayouts.CodePoints(l).All(c => J.IsPunct(c[0]) || c == " ")) outL[^1] += l;
            else outL.Add(l);
        }
        return string.Join("\n", outL);
    }

    private static string[] FontsOf(StyleDef st, params string[] roles) => JLayouts.FontsOf(st, roles);

    /// <summary>monoF(env)：(env.st.fonts.mono && env.st.fonts.mono[0]) || 'mono'。JS L26。</summary>
    private static string MonoF(Env env) => JLayouts.Font0(env.St, "mono") ?? "mono";

    /// <summary>inE(env, d, delay)：outExpo 进场。JS L27。</summary>
    private static double InE(Env env, double d = 0.35, double delay = 0) =>
        J.E.OutExpo(J.Clamp((env.Lt - delay) / d));

    /// <summary>outK(env)：退场剩余量 1 - inCubic(pOut)。JS L28。</summary>
    private static double OutK(Env env) => 1 - J.E.InCubic(env.POut);

    /// <summary>gIn(env)：色差 ghost 只在大色板飞入阶段开，不许拖进下一刀。JS L30。</summary>
    private static bool GIn(Env env) => env.Lt < 0.6 && env.POut <= 0;

    /// <summary>plateHold(env)：独占 key/cell/plate 的文字只接受不挪位的 hold。JS L32。</summary>
    private static bool PlateHold(Env env) =>
        !new[] { "still", "jitter", "breathe", "glitchtick" }.Contains(env.Cut?.Hold ?? "still");

    private static BBox BbRect(double x0, double y0, double x1, double y1) => new()
    {
        X0 = x0,
        Y0 = y0,
        X1 = x1,
        Y1 = y1,
        Cx = (x0 + x1) / 2,
        Cy = (y0 + y1) / 2,
        Boxes = new List<JObject>(),
    };

    private static BBox? U(BBox? a, BBox? b) => JLayouts.UnionBb(a, b);

    /// <summary>miAt(env, t)：让 mainDraw 从本地时间 t 起跳该字入场。JS L36。</summary>
    private static double MiAt(Env env, double t)
    {
        var sg = env.Cut?.Stagger ?? 0;
        return Math.Max(0, t) / Math.Max(0.005, sg != 0 ? sg : 0.04);
    }

    /// <summary>配色读取：JS 直接读 sc.xxx；缺字段时给兜底色。</summary>
    private static string Col(Scheme sc, string k) => sc.Str(k) ?? k switch
    {
        "bg" => "#000000",
        "fg" => "#FFFFFF",
        "sub" => "#9A9A9A",
        "ink" => "#141414",
        "dim" => "#3A3A3A",
        "accent" => "#FF3B30",
        "accent2" => "#16F4D4",
        _ => "#FFFFFF",
    };

    /// <summary>J.rgba(h, a)：hex + alpha → "rgba(r,g,b,a)"（drawItem 的 shadow.color 吃这个格式）。</summary>
    private static string Rgba(string hex, double a)
    {
        var (r, g, b) = J.Hex(hex);
        return "rgba(" + r.ToString(CultureInfo.InvariantCulture) + "," + g.ToString(CultureInfo.InvariantCulture) + ","
               + b.ToString(CultureInfo.InvariantCulture) + "," + a.ToString(CultureInfo.InvariantCulture) + ")";
    }

    // 文字落在色板 fill 上时，从配色里挑对比最好的那个（JS L38 _onc 缓存）
    private static readonly Dictionary<string, string> OnColCache = new();

    private static string OnCol(Scheme sc, string fill)
    {
        var key = fill + Col(sc, "bg") + Col(sc, "fg") + Col(sc, "ink") + Col(sc, "accent");
        if (OnColCache.TryGetValue(key, out var hit)) return hit;
        string? best = null;
        var bv = 0.0;
        foreach (var c in new[] { Col(sc, "bg"), Col(sc, "fg"), Col(sc, "ink"), Col(sc, "accent"), Col(sc, "sub") })
        {
            if (string.IsNullOrEmpty(c) || c == fill) continue;
            var k = J.Contrast(c, fill);
            if (k > bv)
            {
                bv = k;
                best = c;
            }
        }
        var v = bv >= 2.4 && best != null ? best : J.Lum(fill) > 0.5 ? "#111111" : "#FFFFFF";
        if (OnColCache.Count > 200) OnColCache.Clear();
        OnColCache[key] = v;
        return v;
    }

    /// <summary>plateCol(sc, pref)：从配色里挑一个在背景上可读的颜色（色板/横条用）。JS L58-61。</summary>
    private static string PlateCol(Scheme sc, params string[] pref)
    {
        foreach (var c in pref)
            if (!string.IsNullOrEmpty(c) && J.Contrast(c, Col(sc, "bg")) >= 1.6) return c;
        return Col(sc, "fg");
    }

    /// <summary>排版结果的字形中心点（设计坐标，未乘旋转）。JS L63-79 glyphPts。</summary>
    private sealed class GlyphPt
    {
        public string Ch = "";
        public double X, Y, W, H;
        public int Li, Ci, I;
    }

    private static List<GlyphPt> GlyphPts(JObject it)
    {
        var lay = JText.LayoutText(it);
        var sx = JLayouts.D(it, "sx");
        if (sx == 0) sx = 1;
        var sy = JLayouts.D(it, "sy");
        if (sy == 0) sy = 1;
        var x = JLayouts.D(it, "x");
        var y = JLayouts.D(it, "y");
        var list = new List<GlyphPt>();
        foreach (var g in lay.Cells)
        {
            if (g.Ch is " " or "　") continue;
            list.Add(new GlyphPt
            {
                Ch = g.Ch,
                X = x + g.X * sx,
                Y = y + g.Y * sy,
                W = g.W * sx,
                H = g.H * sy,
                Li = g.Li,
                Ci = g.Ci,
                I = list.Count,
            });
        }
        return list;
    }

    /// <summary>逐字放置：fn(i) 返回落点/缩放/透明度/颜色，null = 藏字。JS L80-91 pathText。</summary>
    private sealed class Place
    {
        public double X, Y, Rot, S = 1, A = 1;
        public string? Color;
    }

    private static void PathText(Env env, IReadOnlyList<string> chars, string font, double size,
        Func<int, Place?> fn, JObject? extra = null)
    {
        if (chars.Count == 0 || size < 1) return;
        var it = new JObject
        {
            ["text"] = string.Concat(chars),
            ["font"] = font,
            ["size"] = size,
            ["x"] = 0,
            ["y"] = 0,
            ["ghost"] = false,
        };
        if (extra != null)
            foreach (var kv in extra)
                it[kv.Key] = kv.Value?.DeepClone();
        var lay = JText.LayoutText(it);
        JText.ShareLay(it, lay); // JS it._lay = J.layoutText(it)
        DrawIt(env, it, (i, g, _) =>
        {
            var r = fn(i);
            if (r == null) return new CharXf { Hide = true };
            var gx = g != null ? JLayouts.D(g, "x") : 0;
            var gy = g != null ? JLayouts.D(g, "y") : 0;
            return new CharXf
            {
                Dx = r.X - gx,
                Dy = r.Y - gy,
                Rot = r.Rot,
                S = r.S,
                A = r.A,
                Color = r.Color,
            };
        });
    }

    /// <summary>env.draw 对位：直接画一条 item（不走 mainDraw 的 enter/hold/exit 流水线）。
    /// JS 的 it.charFn 是本 item 私有的函数值字段；C# 桥只有 env.CharFn/env.PieceFn——
    /// 这里按 JS 语义临时置位/清空（否则上一条 mainDraw 的残留会串到直画的 item 上）。</summary>
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
            });
        }
        finally
        {
            env.CharFn = prevC;
            env.PieceFn = prevP;
        }
    }

    /// <summary>mainLines(text, W, H, perL, perP)：按宽度预算给主文案断行，竖屏行更短。JS L92-98。</summary>
    private static string MainLines(string? text, double W, double H, int perL = 11, int perP = 5)
    {
        var t = (text ?? "").Trim();
        var n = JLayouts.GlyphCount(t);
        var per = W < H ? perP : perL;
        if (n <= per) return t;
        return SplitL(t, (int)Math.Ceiling(n / (double)Math.Ceiling(n / (double)per)));
    }

    private static readonly Dictionary<string, List<string>> PoolCache = new();

    /// <summary>poolOf(cut)：歌词自有的非拉丁字符 + 隔一个取的假名池。JS L99-112。</summary>
    private static List<string> PoolOf(Cut cut)
    {
        var key = JLang.Lang + "|" + cut.LineText + "|" + cut.Text;
        if (PoolCache.TryGetValue(key, out var hit)) return hit;
        var own = JLayouts.CodePoints(Clean((cut.LineText ?? "") + cut.Text))
            .Where(c => !J.IsLatin(c[0]) && !J.IsPunct(c[0]) && c != "・")
            .ToList();
        var p = own.Concat(JLayouts.CodePoints(JLang.Pool("kana") ?? "").Where((_, i) => i % 2 == 0)).ToList();
        if (PoolCache.Count > 100) PoolCache.Clear();
        PoolCache[key] = p;
        return p;
    }

    /// <summary>rrPath：在路径上走圆角矩形（JS ctx.moveTo/arcTo×4 的等价几何）。JS L113-122。</summary>
    private static void RrPath(SKPath p, double x, double y, double w, double h, double r)
    {
        if (w <= 0 || h <= 0) return;
        r = Math.Max(0, Math.Min(r, Math.Min(w / 2, h / 2)));
        p.AddRoundRect(new SKRoundRect(new SKRect((float)x, (float)y, (float)(x + w), (float)(y + h)), (float)r));
    }

    // ---------------- 等分格阵（JS L1316-1330 cellRows） ----------------

    private sealed class Cell
    {
        public int I, R, J;
        public string Ch = "";
        public double X, Y;
    }

    private sealed class CellGrid
    {
        public List<Cell> Cells = new();
        public double K;
        public int Rows;
    }

    private static CellGrid CellRows(IReadOnlyList<string> chs, double W, double H, int maxPerRow,
        double cellAsp, double maxK, double gapK = 0.14, bool stagger = false)
    {
        var n = chs.Count;
        var rows = (int)Math.Ceiling(n / (double)maxPerRow);
        var per = (int)Math.Ceiling(n / (double)rows);
        var wk = per + (per - 1) * gapK + (stagger && rows > 1 ? 0.5 : 0);
        var hk = rows * cellAsp + (rows - 1) * gapK * 1.6;
        var k = Math.Min(Math.Min(W * 0.86 / wk, H * 0.7 / hk), maxK);
        var g = new CellGrid { K = k, Rows = rows };
        for (var i = 0; i < n; i++)
        {
            var r = i / per;
            var j = i - r * per;
            var cnt = Math.Min(per, n - r * per);
            var x = W / 2 + (j - (cnt - 1) / 2.0) * k * (1 + gapK)
                    + (stagger && rows > 1 ? (r % 2 == 1 ? 0.25 : -0.25) * k : 0);
            var y = H / 2 + (r - (rows - 1) / 2.0) * k * (cellAsp + gapK * 1.6);
            g.Cells.Add(new Cell { I = i, Ch = chs[i], X = x, Y = y, R = r, J = j });
        }
        return g;
    }

    // ---------------- dotMatrix 的点阵采样/贴图（JS L2210-2262，canvas 光栅 → SKBitmap） ----------------

    private sealed class DotSample
    {
        public int Cols, Rows;
        public List<int> Lit = new();
        public double Fscale, D;
    }

    private sealed class DotTile
    {
        public SKBitmap Cv = null!;
        public int T;
    }

    private static readonly Dictionary<string, DotSample> DotSamples = new();
    private static readonly Dictionary<string, DotTile> DotTiles = new();

    /// <summary>dotTile(col, shape)：熄灭的 LED 底贴图（64×64，圆/方点），按颜色+形状缓存。JS L2213-2229。</summary>
    private static DotTile DotTileOf(string col, string shape)
    {
        var key = col + "|" + shape;
        if (DotTiles.TryGetValue(key, out var hit)) return hit;
        const int T = 64;
        var bmp = new SKBitmap(T, T);
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(SKColors.Transparent);
            using var p = new SKPaint { IsAntialias = true, Color = Env.SkColor(col, 1) };
            var rr = T * (shape == "round" ? 0.38 : 0.4);
            if (shape == "round") c.DrawCircle(T / 2f, T / 2f, (float)rr, p);
            else c.DrawRect(new SKRect((float)(T / 2 - rr), (float)(T / 2 - rr), (float)(T / 2 + rr), (float)(T / 2 + rr)), p);
        }
        var t = new DotTile { Cv = bmp, T = T };
        if (DotTiles.Count > 24)
        {
            foreach (var v in DotTiles.Values) v.Cv.Dispose();
            DotTiles.Clear();
        }
        DotTiles[key] = t;
        return t;
    }

    /// <summary>dotSample(mt, font, D, lead)：4× 超采样里按 4×4 覆盖率 > 0.38 取亮点。JS L2230-2262。</summary>
    private static DotSample DotSampleOf(string mt, string font, int D, double lead)
    {
        var key = mt + "|" + font + "|" + D.ToString(CultureInfo.InvariantCulture) + "|"
                  + lead.ToString(CultureInfo.InvariantCulture);
        if (DotSamples.TryGetValue(key, out var hit)) return hit;
        const int SS = 4;
        var fpx = D * SS * 0.94;
        var lay = JText.LayoutText(new JObject { ["text"] = mt, ["font"] = font, ["size"] = fpx, ["lead"] = lead });
        var cols = (int)Math.Ceiling(lay.W / SS) + 2;
        var rows = (int)Math.Ceiling(lay.H / SS) + 2;
        var cw = cols * SS;
        var chh = rows * SS;
        var bmp = new SKBitmap(new SKImageInfo(cw, chh, SKColorType.Alpha8, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Transparent);
            var f = JText.FaceFont(font, fpx); // 缓存实例，禁 Dispose
            f.GetFontMetrics(out var fm);
            var yMid = (-fm.Ascent - fm.Descent) / 2; // Canvas textBaseline='middle' 对位
            using var p = new SKPaint { IsAntialias = true, Color = SKColors.White };
            foreach (var g in lay.Cells)
            {
                if (g.Ch is " " or "　") continue;
                canvas.DrawText(g.Ch, (float)(cw / 2.0 + g.X), (float)(chh / 2.0 + g.Y + yMid), SKTextAlign.Center, f, p);
            }
        }
        var lit = new List<int>();
        using (var px = bmp.PeekPixels())
        {
            for (var r0 = 0; r0 < rows; r0++)
                for (var c0 = 0; c0 < cols; c0++)
                {
                    var a = 0;
                    for (var yy = 0; yy < SS; yy++)
                        for (var xx = 0; xx < SS; xx++)
                            a += px.GetPixelColor(c0 * SS + xx, r0 * SS + yy).Alpha;
                    if (a / (double)(SS * SS * 255) > 0.38)
                    {
                        lit.Add(c0);
                        lit.Add(r0);
                    }
                }
        }
        bmp.Dispose();
        var r = new DotSample { Cols = cols, Rows = rows, Lit = lit, Fscale = 0.94 * D, D = D };
        if (DotSamples.Count > 40) DotSamples.Clear();
        DotSamples[key] = r;
        return r;
    }

    // ---------------- tape 的撕边胶带路径（JS L2928-2937） ----------------

    private static SKPath TapePath(double L, double h, int seed, int k)
    {
        const int m = 7;
        var tooth = h * 0.09;
        var p = new SKPath();
        p.MoveTo(0, (float)(-h / 2));
        p.LineTo((float)L, (float)(-h / 2));
        for (var i = 1; i <= m; i++)
            p.LineTo((float)(L + (i % 2 == 1 ? tooth : -tooth * 0.3) * (0.6 + 0.8 * J.R(seed, k, i, 1))),
                (float)(-h / 2 + h * i / m));
        p.LineTo(0, (float)(h / 2));
        for (var i = m - 1; i >= 1; i--)
            p.LineTo((float)((i % 2 == 1 ? -tooth : tooth * 0.3) * (0.6 + 0.8 * J.R(seed, k, i, 2))),
                (float)(-h / 2 + h * i / m));
        p.Close();
        return p;
    }

    // ---------------- stickerBomb 的贴纸轮廓（JS L1027-1039） ----------------

    private static SKPath StickerPath(string shape, double w, double h, double grow, int seed)
    {
        var x = -w / 2 - grow;
        var y = -h / 2 - grow;
        var w2 = w + grow * 2;
        var h2 = h + grow * 2;
        var p = new SKPath();
        if (shape == "circle") p.AddCircle(0, 0, (float)(Math.Max(w2, h2) / 2));
        else if (shape == "burst")
        {
            var R = Math.Max(w2, h2) / 2 * 1.08;
            const int m = 16;
            for (var i = 0; i < m * 2; i++)
            {
                var a = i / (double)(m * 2) * J.TAU;
                var r = i % 2 == 1 ? R * 0.84 : R;
                var px = Math.Cos(a) * r;
                var py = Math.Sin(a) * r;
                if (i == 0) p.MoveTo((float)px, (float)py);
                else p.LineTo((float)px, (float)py);
            }
            p.Close();
        }
        else RrPath(p, x, y, w2, h2, shape == "pill" ? h2 / 2 : Math.Min(w2, h2) * 0.2);
        return p;
    }

    // ---------------- 松散读取小工具 ----------------

    private static string? S(JObject p, string k) => JLayouts.S(p, k);
    private static double D(JObject p, string k, double d = 0) => JLayouts.D(p, k, d);
    private static int I(JObject p, string k, int d = 0) => JLayouts.I(p, k, d);
    private static bool B(JObject p, string k) => JLayouts.B(p, k);
    private static bool Truthy(JToken? t) => JLayouts.Truthy(t);

    /// <summary>弧度 → 角度（JS ctx.rotate 收弧度，Skia RotateDegrees 收角度）。</summary>
    private static double Deg(double radians) => radians / J.DEG;

    /// <summary>plan 里的字体数组（wordCloud 的 Pm.fonts）。</summary>
    private static List<string> StrArr(JToken? t) => t is JArray a
        ? a.Where(x => x.Type == JTokenType.String).Select(x => (string)x!).ToList()
        : new List<string>();

    /// <summary>JS ENTER/EXIT 的 pieces 标志（MoveDef 无该字段，按 key 判定；JAnim 侧同约定）。</summary>
    private static bool HasPieces(bool enter, string key) =>
        enter ? key == "assemble" : key is "explode" or "fall" or "drift";

    /// <summary>JS env.beat（09_render 的 beatAt）本文件内的重建：{since, len, index}。
    /// C# Env.Beat 只装归一相位（Renderer.BeatNorm），够不着 index/since——不改 Env.cs，这里补。</summary>
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

    // ================= 注册（JS 源里 reg(...) 的出现序） =================

    /// <summary>幂等注册本文件全部 layout（顺序 = JS 源文件 J.register 调用序；不挂静态构造函数）。</summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;
        RegisterRain();        // JS L124
        RegisterHanging();     // JS L262
        RegisterOrbit();       // JS L377
        RegisterTunnel();      // JS L481
        RegisterWordCloud();   // JS L578
        RegisterBounceLine();  // JS L730
        RegisterElastic();     // JS L834
        RegisterCrossBands();  // JS L938
        RegisterStickerBomb(); // JS L1040
        RegisterNeon();        // JS L1201
        RegisterKeycaps();     // JS L1332
        RegisterBubbles();     // JS L1447
        RegisterSlotMachine(); // JS L1518
        RegisterFlipBoard();   // JS L1650
        RegisterCredits();     // JS L1765
        RegisterZoomRepeat();  // JS L1887
        RegisterSplitHalves(); // JS L1969
        RegisterColumnsBig();  // JS L2034
        RegisterCircleWords(); // JS L2129
        RegisterDotMatrix();   // JS L2263
        RegisterDepthStack();  // JS L2371
        RegisterTypeSpecimen();// JS L2427
        RegisterKanjiFocus();  // JS L2537
        RegisterHalfVertical();// JS L2616
        RegisterCurtain();     // JS L2738
        RegisterEqualizer();   // JS L2835
        RegisterTape();        // JS L2938
    }

    // ---------------- rain（JS L124-260） ----------------

    private static void RegisterRain() => Reg0("rain", new LayoutDef
    {
        Name = "文字の雨",
        Tags = new[] { "glitch", "graphic", "emotional" },
        W = 0.9,
        Busy = true,
        Fits = n => n >= 1 && n <= 16,
        EnterBias = new JObject
        {
            ["cut"] = 2.4,
            ["flicker"] = 1.4,
            ["scramble"] = 1.4,
            ["blur"] = 1.1,
            ["type"] = 0.3,
            ["wipe"] = 0.4,
            ["slice"] = 0.4,
            ["stretch"] = 0.5,
            ["assemble"] = 0.5,
        },
        Plan = (rng, cut, st) =>
        {
            var port = cut.H > cut.W;
            var monos = JLayouts.RoleFonts(st, "mono").Where(k => k == "dot").ToArray();
            var p = new JObject { ["font"] = rng.Pick(FontsOf(st, "display", "serif")) };
            // JS: monos.length && rng.chance(0.5) ? 'dot' : rng.pick(fontsOf(st,['body']))
            p["rf"] = monos.Length > 0 && rng.Chance(0.5) ? "dot" : rng.Pick(FontsOf(st, "body"));
            p["orient"] = cut.N <= 7 && rng.Chance(port ? 0.6 : 0.3) ? "v" : "h";
            p["density"] = rng.Range(0.5, 0.72);
            p["speed"] = rng.Range(0.85, 1.25);
            p["tint"] = rng.Pick(new[] { "sub", "sub", "accent" });
            p["order"] = rng.Pick(new[] { "ltr", "random", "random" });
            return p;
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var s = (int)cut.Seed;
            var M = Math.Min(W, H);
            var lt = env.Lt;
            var vert = S(Pm, "orient") == "v";
            var mt = vert ? Clean(cut.Text) : MainLines(cut.Text, W, H, 11, 6);
            var font = S(Pm, "font")!;
            var rf = S(Pm, "rf")!;
            var baseIt = new JObject
            {
                ["text"] = mt,
                ["font"] = font,
                ["x"] = W / 2,
                ["y"] = H / 2,
                ["track"] = 0.1,
                ["lead"] = 1.25,
                ["vertical"] = vert,
            };
            var baseSize = Math.Min(
                JLayouts.FitSize(mt, font, W * (vert ? 0.5 : 0.84), H * (vert ? 0.8 : 0.5), baseIt),
                vert ? H * 0.15 : H * 0.24);
            baseIt["size"] = baseSize;
            var gl = GlyphPts(baseIt);
            var n = gl.Count;
            var lb = JAnim.ItemBox(baseIt);
            var kx0 = lb.X0 - baseSize * 0.3;
            var kx1 = lb.X1 + baseSize * 0.3;
            var ky0 = lb.Y0 - baseSize * 0.3;
            var ky1 = lb.Y1 + baseSize * 0.3;
            // 背景字流：静止的字格阵，每条活跃列有一个亮头往下扫
            var pool = PoolOf(cut);
            var NP = pool.Count;
            var cell = J.Clamp(M * 0.036, 16, 64);
            var cols = (int)Math.Floor(W / cell);
            var rows = (int)Math.Ceiling(H / cell);
            var ox = (W - cols * cell) / 2;
            var bgA = J.E.OutCubic(J.Clamp(env.Ltb / 0.35)) * OutK(env);
            var rs = cell * 0.72;
            var trk = (cell - rs) / rs;
            var tail = S(Pm, "tint") == "accent" ? Col(sc, "accent") : Col(sc, "sub");
            var density = D(Pm, "density");
            var speed = D(Pm, "speed");
            if (bgA > 0.01)
            {
                for (var c = 0; c < cols; c++)
                {
                    if (J.R(s, c, 1) > density) continue;
                    var v = J.Rr(8, 16, s, c, 2) * speed;
                    var Ls = J.Rr(6, 15, s, c, 3);
                    var per = rows + Ls + J.Rr(2, rows * 0.8, s, c, 4);
                    var hy = ((env.Ltb * v + J.R(s, c, 5) * per) % per) - 1;
                    var r0 = (int)Math.Max(0, Math.Ceiling(hy - Ls));
                    var r1 = (int)Math.Min(rows - 1, Math.Floor(hy));
                    if (r1 < r0) continue;
                    var x = ox + (c + 0.5) * cell;
                    var chs = new StringBuilder();
                    for (var r = r0; r <= r1; r++)
                        chs.Append(pool[(int)(J.H(s, c, r,
                            (int)Math.Floor(((int)env.Step + (int)(J.H(s, r, c) & 15)) / 14.0)) % (uint)NP)]);
                    var inK = x > kx0 && x < kx1;
                    DrawIt(env, new JObject
                    {
                        ["text"] = chs.ToString(),
                        ["font"] = rf,
                        ["size"] = rs,
                        ["track"] = trk,
                        ["vertical"] = true,
                        ["align"] = "left",
                        ["x"] = x,
                        ["y"] = r0 * cell + cell * 0.5 - rs * 0.5,
                        ["color"] = Col(sc, "sub"),
                        ["ghost"] = false,
                        ["alpha"] = bgA,
                    }, (i, _, _) =>
                    {
                        var r = r0 + i;
                        var d = hy - r;
                        var a = d < 1 ? 1.0 : 0.1 + Math.Pow(1 - d / Ls, 1.4) * 0.6;
                        var cy = (r + 0.5) * cell;
                        if (inK && cy > ky0 && cy < ky1) a *= 0.22;
                        return d < 1
                            ? new CharXf { A = a, Color = Col(sc, "fg") }
                            : new CharXf { A = a };
                    });
                }
            }
            // 歌词本体：每个字由一条下落的字流送达，落位后点亮
            var span = J.Clamp(cut.Dur * 0.2, 0.1, 0.5);
            var fd = J.Clamp(cut.Dur * 0.1, 0.14, 0.26);
            var rank = Enumerable.Range(0, n).ToList();
            // JS 的 Array.sort 稳定（V8 TimSort）；OrderBy 同为稳定排序，同 seed 同结果
            if (S(Pm, "order") == "random") rank = rank.OrderBy(i => J.R(s, i, 31)).ToList();
            var pos = new int[n];
            for (var k = 0; k < rank.Count; k++) pos[rank[k]] = k;
            BBox? bb = null;
            const int trailN = 8;
            var ts = J.Clamp(baseSize * 0.32, cell * 0.8, cell * 1.5);
            var tsp = ts * 1.12;
            for (var i = 0; i < gl.Count; i++)
            {
                var g = gl[i];
                var k = vert ? n - 1 - i : pos[i];
                var ta = 0.05 + fd + (n > 1 ? k / (double)(n - 1) : 0) * span;
                var u = (lt - (ta - fd)) / fd;
                if (u > 0 && u < 2)
                {
                    var e = J.E.InQuad(Math.Min(1, u));
                    var yh = J.Lerp(-tsp * 2, g.Y, e);
                    var fade = u < 1 ? 1.0 : 1 - (u - 1);
                    var chs = new List<string>();
                    for (var j = trailN - 1; j >= 0; j--)
                        chs.Add(pool[(int)(J.H(s, i, j, (int)env.Step >> 1) % (uint)NP)]);
                    var hs = u < 1 ? J.Lerp(ts, baseSize, Math.Pow(u, 3)) : baseSize;
                    DrawIt(env, new JObject
                    {
                        ["text"] = string.Concat(chs),
                        ["font"] = rf,
                        ["size"] = ts,
                        ["track"] = 0.12,
                        ["vertical"] = true,
                        ["align"] = "left",
                        ["x"] = g.X,
                        ["y"] = yh - hs * 0.5 - trailN * tsp,
                        ["color"] = tail,
                        ["ghost"] = false,
                        ["alpha"] = fade * OutK(env),
                    }, (j, _, _) => new CharXf { A = Math.Pow((j + 1) / (double)trailN, 1.6) * 0.95 });
                    if (u < 1)
                        DrawIt(env, new JObject
                        {
                            ["text"] = g.Ch,
                            ["font"] = font,
                            ["size"] = hs,
                            ["x"] = g.X,
                            ["y"] = yh,
                            ["vertical"] = vert,
                            ["color"] = Col(sc, "fg"),
                            ["ghost"] = false,
                            ["alpha"] = 0.6 + 0.4 * u,
                        });
                }
                var fl = J.Clamp((lt - ta) / 0.45);
                var it = new JObject
                {
                    ["text"] = g.Ch,
                    ["font"] = font,
                    ["size"] = baseSize,
                    ["x"] = g.X,
                    ["y"] = g.Y,
                    ["vertical"] = vert,
                    ["color"] = fl < 1 ? J.Mix(Col(sc, "accent"), Col(sc, "fg"), J.E.OutCubic(fl)) : Col(sc, "fg"),
                    ["mi"] = MiAt(env, ta),
                };
                bb = U(bb, JLayouts.MainDraw(env, it));
            }
            env.Body = bb;
        },
    });

    // ---------------- hanging（JS L262-375） ----------------

    private static void RegisterHanging() => Reg0("hanging", new LayoutDef
    {
        Name = "吊り下げ",
        Tags = new[] { "pop", "calm", "emotional" },
        W = 0.9,
        Treat = "safe",
        Portrait = 0.8,
        Fits = n => n >= 2 && n <= 12,
        EnterBias = new JObject
        {
            ["drop"] = 1.8,
            ["pop"] = 1.3,
            ["cut"] = 1.5,
            ["slice"] = 0.3,
            ["wipe"] = 0.3,
            ["stretch"] = 0.4,
        },
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
            ["shape"] = rng.Pick(new[] { "arc", "wave", "random", "stair" }),
            ["dir"] = rng.Pick(new[] { 1, -1 }),
            ["tag"] = rng.Chance(0.35),
            ["rail"] = rng.Chance(0.6),
            ["amp"] = rng.Range(0.06, 0.11),
            ["kick"] = rng.Range(0.16, 0.26),
            ["ph"] = rng.Range(0, 6),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var s = (int)cut.Seed;
            var M = Math.Min(W, H);
            var lt = env.Lt;
            var chars = SlotsOf(cut.Text);
            var n = chars.Count;
            if (n == 0)
            {
                env.Body = null;
                return;
            }
            var rowsN = W < H && n > 5 ? 2 : 1;
            var per = (int)Math.Ceiling(n / (double)rowsN);
            var sp = W * (rowsN > 1 ? 0.84 / (per + 0.5) : 0.88 / per);
            var tag = B(Pm, "tag");
            var rail = B(Pm, "rail");
            var size = Math.Min(sp * (tag ? 0.76 : 0.84), M * 0.19);
            var tagW = size * 1.12;
            var tagH = size * 1.36;
            var csize = tag ? size * 0.8 : size;
            var railY = rail ? H * 0.075 : -2.0;
            var outK = OutK(env);
            var inA = InE(env, 0.45);
            if (rail)
                env.Line(new[] { new[] { W * 0.5 - W * 0.52 * inA, railY }, new[] { W * 0.5 + W * 0.52 * inA, railY } },
                    Col(sc, "sub"), Math.Max(1.2, M * 0.0022), 0.8 * outK, false);
            var gap = J.Clamp(cut.Dur * 0.35 / n, 0.03, 0.08);
            BBox? bb = null;
            var strokeW = Math.Max(1, M * 0.0016);
            var items = new List<(int I, double Ccx, double Ccy, double Th, double Ta, double Tau)>();
            for (var i = 0; i < n; i++)
            {
                if (chars[i] == " ") continue;
                var row = (int)Math.Floor(i / (double)per);
                var j = i - row * per;
                var cnt = Math.Min(per, n - row * per);
                var u = cnt > 1 ? j / (double)(cnt - 1) : 0.5;
                var amp = rowsN > 1 ? 0.45 : 1.0;
                double dev;
                if (S(Pm, "shape") == "arc") dev = (Math.Sin(Math.PI * u) - 0.55) * H * 0.13 * D(Pm, "dir");
                else if (S(Pm, "shape") == "wave") dev = Math.Sin(u * J.TAU * 1.1 + D(Pm, "ph")) * H * 0.08;
                else if (S(Pm, "shape") == "stair") dev = (u - 0.5) * H * 0.2 * D(Pm, "dir");
                else dev = J.Rs(s, i, 3) * H * 0.08;
                dev = Math.Max(-H * 0.14, Math.Min(H * 0.14, dev * amp));
                var ax = W / 2 + (j - (cnt - 1) / 2.0) * sp + (rowsN > 1 ? (row != 0 ? 0.25 : -0.25) * sp : 0);
                var cy = rowsN > 1 ? (row != 0 ? H * 0.64 : H * 0.4) + dev * 0.5 : H * 0.52 + dev;
                var attach = tag ? tagH * 0.5 - tagH * 0.1 : size * 0.56;
                var L = Math.Max(H * 0.06, cy - attach - railY);
                var T = 1.5 * Math.Sqrt(L / (H * 0.45));
                var w = J.TAU / T;
                var ta = 0.12 + i * gap;
                var tau = lt - ta;
                var ampK = rowsN > 1 && row == 1 ? 0.5 : 1.0;
                var dk = sp / L / J.DEG; // 横向每挪一个字距对应的摆角（度）
                var th = D(Pm, "amp") * dk * ampK * Math.Sin(w * env.Ltb + J.R(s, i, 5) * 6);
                if (tau > 0)
                    th += D(Pm, "kick") * dk * ampK * (J.R(s, i, 6) < 0.5 ? 1 : -1) * Math.Exp(-tau / 0.7) * Math.Sin(w * tau * 1.3);
                var bounce = tau > 0 ? 1 + 0.05 * Math.Exp(-tau / 0.18) * Math.Cos(tau * 30) : 1;
                var r = th * J.DEG;
                var grow = J.E.OutCubic(J.Clamp((lt - (ta - 0.3)) / 0.3));
                var Ls = L * bounce * grow;
                var ex = ax + Math.Sin(r) * Ls;
                var ey = railY + Math.Cos(r) * Ls;
                if (grow > 0)
                {
                    env.Line(new[] { new[] { ax, railY }, new[] { ex, ey } }, Col(sc, "sub"), strokeW, 0.9 * outK, false);
                    if (rail) env.Circle(ax, railY, Math.Max(2.5, M * 0.004), Col(sc, "fg"), null, 0, outK, false);
                }
                var ccx = ax + Math.Sin(r) * (Ls + attach);
                var ccy = railY + Math.Cos(r) * (Ls + attach);
                items.Add((i, ccx, ccy, th, ta, tau));
            }
            foreach (var q in items)
            {
                if (q.Tau < 0) continue;
                if (tag)
                {
                    var pc = q.I % 2 != 0
                        ? PlateCol(sc, Col(sc, "accent"), Col(sc, "ink"))
                        : PlateCol(sc, Col(sc, "ink"), Col(sc, "fg"));
                    var e = J.E.OutBack(J.Clamp(q.Tau / 0.18), 1.4) * outK;
                    if (e > 0)
                    {
                        env.Ctx.Save();
                        env.Ctx.Translate((float)q.Ccx, (float)q.Ccy);
                        env.Ctx.RotateDegrees((float)(-q.Th)); // JS: rotate(-q.th * DEG)
                        env.Ctx.Scale((float)e, (float)e);
                        env.RRect(-tagW / 2, -tagH / 2, tagW, tagH, tagW * 0.12, pc, 1, GIn(env));
                        env.Circle(0, -tagH / 2 + tagH * 0.1, Math.Max(2, size * 0.055), Col(sc, "bg"), null, 0, 1, false);
                        env.Ctx.Restore();
                    }
                    bb = U(bb, JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = chars[q.I],
                        ["font"] = S(Pm, "font")!,
                        ["size"] = csize,
                        ["x"] = q.Ccx + Math.Sin(q.Th * J.DEG) * tagH * 0.06,
                        ["y"] = q.Ccy + Math.Cos(q.Th * J.DEG) * tagH * 0.06,
                        ["rot"] = -q.Th,
                        ["color"] = OnCol(sc, pc),
                        ["plain"] = true,
                        ["noHold"] = PlateHold(env),
                        ["mi"] = MiAt(env, q.Ta),
                    }));
                }
                else
                {
                    bb = U(bb, JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = chars[q.I],
                        ["font"] = S(Pm, "font")!,
                        ["size"] = csize,
                        ["x"] = q.Ccx,
                        ["y"] = q.Ccy,
                        ["rot"] = -q.Th,
                        ["color"] = Col(sc, "fg"),
                        ["mi"] = MiAt(env, q.Ta),
                    }));
                }
            }
            env.Body = bb;
        },
    });

    // ---------------- orbit（JS L377-479） ----------------

    private sealed class OrbitRing
    {
        public double Rx, Ry, Tilt, Os, Sp;
        public List<string> Unit = new();
        public int Cnt;
        public string Font = "";
    }

    private static void RegisterOrbit() => Reg0("orbit", new LayoutDef
    {
        Name = "周回",
        Tags = new[] { "calm", "graphic", "emotional" },
        W = 1,
        Fits = n => n >= 1 && n <= 14,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
            ["fo"] = rng.Pick(FontsOf(st, "body", "serif", "display")),
            ["variant"] = rng.Pick(new[] { "ring", "ring", "atom", "wide" }),
            ["tilt"] = rng.Range(5, 13) * rng.Pick(new[] { 1, -1 }),
            ["speed"] = rng.Range(22, 40) * rng.Pick(new[] { 1, -1 }),
            ["unit"] = rng.Pick(new[] { "line", "self", "line" }),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var s = (int)cut.Seed;
            var M = Math.Min(W, H);
            var mt = MainLines(cut.Text, W, H, 10, 5);
            var port = W < H;
            var font = S(Pm, "font")!;
            var fo = S(Pm, "fo")!;
            var size = Math.Min(
                JLayouts.FitSize(mt, font, W * (port ? 0.64 : 0.54), H * 0.3, new JObject { ["track"] = 0.04, ["lead"] = 1.15 }),
                H * 0.19);
            var mm = JLayouts.Measure(new JObject { ["text"] = mt, ["font"] = font, ["size"] = size, ["track"] = 0.04, ["lead"] = 1.15 });
            var cx = W / 2;
            var cy = H / 2;
            var e = InE(env, 0.7);
            var outK = OutK(env);
            var txt = Clean(cut.Text);
            var line = Clean(cut.LineText);
            var rom = RomaOf(txt);
            var units = new List<string>
            {
                S(Pm, "unit") == "line" && line.Length > 0 && line != txt && JLayouts.CpLen(line) <= 40
                    ? line + "・"
                    : txt + "・",
                rom != null
                    ? rom + " ・ "
                    : cut.Words is { Length: > 1 }
                        ? string.Join("・", cut.Words) + "・"
                        : txt + " ・ ",
            };
            var variant = S(Pm, "variant")!;
            var nR = variant == "atom" ? 2 : 1;
            var rings = new List<OrbitRing>();
            for (var k = 0; k < nR; k++)
            {
                var wide = variant == "wide";
                var rx0 = J.Clamp(Math.Max(mm.W / 2 + size * (wide ? 1.4 : 1.05), M * (wide ? 0.44 : 0.36)), M * 0.2, W * 0.47);
                var ry0 = Math.Min(rx0 * 0.62,
                    Math.Max(rx0 * (variant == "atom" ? 0.36 : wide ? 0.2 : 0.27), mm.H / 2 + size * (k != 0 ? 0.75 : 0.5)));
                var grow = (0.7 + 0.3 * e) * (1 + 0.25 * env.POut);
                var rx = rx0 * grow;
                var ry = ry0 * grow;
                var tilt = (variant == "atom"
                    ? (k != 0 ? -1 : 1) * (Math.Abs(D(Pm, "tilt")) * 0.6 + 8)
                    : D(Pm, "tilt")) * J.DEG;
                var os = J.Clamp(M * (wide ? 0.05 : 0.042), 14, 64) * (k != 0 ? 0.82 : 1);
                var unit = JLayouts.CodePoints(units[k]);
                var perim = Math.PI * (3 * (rx + ry) - Math.Sqrt((3 * rx + ry) * (rx + 3 * ry)));
                var cnt = (int)Math.Max(Math.Min(unit.Count, 44), Math.Min(44, Math.Floor(perim / (os * 1.7))));
                rings.Add(new OrbitRing
                {
                    Rx = rx,
                    Ry = ry,
                    Tilt = tilt,
                    Os = os,
                    Unit = unit,
                    Cnt = cnt,
                    Sp = D(Pm, "speed") * (k != 0 ? -0.8 : 1),
                    Font = k != 0 ? MonoF(env) : fo,
                });
            }

            void DrawRing(OrbitRing R, bool front)
            {
                var cT = Math.Cos(R.Tilt);
                var sT = Math.Sin(R.Tilt);
                var a0 = env.Ltb * R.Sp * J.DEG + J.R(s, 9) * J.TAU;
                var chars = new List<string>();
                for (var j = 0; j < R.Cnt; j++) chars.Add(R.Unit[j % R.Unit.Count]);
                var col = Col(sc, "fg");
                var sub = Col(sc, "sub");
                var accent = Col(sc, "accent");
                PathText(env, chars, R.Font, R.Os, j =>
                {
                    var a = a0 + j / (double)R.Cnt * J.TAU;
                    var z = Math.Sin(a);
                    if (z >= 0 != front) return null;
                    var lx = Math.Cos(a) * R.Rx;
                    var ly = z * R.Ry;
                    var d = (z + 1) / 2;
                    var sep = chars[j] == "・";
                    return new Place
                    {
                        X = cx + lx * cT - ly * sT,
                        Y = cy + lx * sT + ly * cT,
                        S = 0.55 + 0.62 * d,
                        A = (0.18 + 0.82 * Math.Pow(d, 1.3)) * e * outK,
                        Color = sep ? accent : d > 0.5 ? col : sub,
                    };
                });
            }

            double[][] Ellipse(OrbitRing R, bool front)
            {
                var pts = new List<double[]>();
                var cT = Math.Cos(R.Tilt);
                var sT = Math.Sin(R.Tilt);
                for (var j = 0; j <= 40; j++)
                {
                    var a = front ? j / 40.0 * Math.PI : Math.PI + j / 40.0 * Math.PI;
                    var lx = Math.Cos(a) * R.Rx;
                    var ly = Math.Sin(a) * R.Ry;
                    pts.Add(new[] { cx + lx * cT - ly * sT, cy + lx * sT + ly * cT });
                }
                return pts.ToArray();
            }

            var lw = Math.Max(1, M * 0.0016);
            foreach (var R in rings)
            {
                env.Line(Ellipse(R, false), Col(sc, "sub"), lw, 0.4 * e * outK, false);
                DrawRing(R, false);
            }
            var bb = JLayouts.MainDraw(env, new JObject
            {
                ["text"] = mt,
                ["font"] = font,
                ["size"] = size,
                ["x"] = cx,
                ["y"] = cy,
                ["track"] = 0.04,
                ["lead"] = 1.15,
                ["color"] = Col(sc, "fg"),
            });
            var wb = bb ?? BbRect(cx - mm.W / 2, cy - mm.H / 2, cx + mm.W / 2, cy + mm.H / 2);
            foreach (var R in rings)
            {
                if (env.Pass == "main")
                {
                    // ctx.rect(整屏) + ctx.rect(文字块) 的 evenodd clip → 只画文字块之外的前半环
                    env.Ctx.Save();
                    using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
                    path.AddRect(new SKRect((float)-W, (float)-H, (float)(W * 3), (float)(H * 3)));
                    path.AddRect(new SKRect(
                        (float)(wb.X0 - size * 0.12), (float)(wb.Y0 - size * 0.1),
                        (float)(wb.X1 + size * 0.12), (float)(wb.Y1 + size * 0.1)));
                    env.Ctx.ClipPath(path);
                    env.Line(Ellipse(R, true), Col(sc, "sub"), lw, 0.65 * e * outK, false);
                    env.Ctx.Restore();
                }
                DrawRing(R, true);
            }
            env.Body = bb;
        },
    });

    // ---------------- tunnel（JS L481-575） ----------------

    private static JObject Merge(JObject baseObj, JObject over)
    {
        var o = (JObject)baseObj.DeepClone();
        foreach (var kv in over) o[kv.Key] = kv.Value?.DeepClone();
        return o;
    }

    private static void RegisterTunnel() => Reg0("tunnel", new LayoutDef
    {
        Name = "トンネル",
        Tags = new[] { "glitch", "graphic", "emotional" },
        W = 0.9,
        Emph = 1.3,
        Busy = true,
        Fits = n => n >= 1 && n <= 14,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "display", "body")),
            ["fontC"] = rng.Pick(FontsOf(st, "display", "serif")),
            ["q"] = rng.Range(1.42, 1.62),
            ["speed"] = rng.Range(0.22, 0.42) * rng.Pick(new[] { 1, 1, -1 }),
            ["style"] = rng.Pick(new[] { "outline", "fill", "alt" }),
            ["unit"] = rng.Pick(new[] { "line", "text", "line" }),
            ["persp"] = rng.Chance(0.65),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var M = Math.Min(W, H);
            var cx = W / 2;
            var cy = H / 2;
            var mt = MainLines(cut.Text, W, H, 10, 5);
            var font = S(Pm, "font")!;
            var fontC = S(Pm, "fontC")!;
            var size = Math.Min(JLayouts.FitSize(mt, fontC, W * (W < H ? 0.44 : 0.5), H * 0.26,
                new JObject { ["track"] = 0.05, ["lead"] = 1.15 }), H * 0.16);
            var mm = JLayouts.Measure(new JObject { ["text"] = mt, ["font"] = fontC, ["size"] = size, ["track"] = 0.05, ["lead"] = 1.15 });
            var hw = mm.W / 2 + size * 0.55;
            var hh = mm.H / 2 + size * 0.5;
            // 画框比例：横屏取屏幕比例，竖屏往文字块比例折半，否则画框永远出不来
            var ra = W < H ? Math.Min(0.95, Math.Sqrt(hw / hh * (W / H))) : W / H;
            var A = W / 2;
            var Bh = A / ra;
            var s0 = Math.Max(Math.Max(hw / A, hh / Bh), 0.12);
            var q = D(Pm, "q");
            var Lmax = Math.Log(Math.Max(W * 0.66 / A, H * 0.66 / Bh) / s0) / Math.Log(q);
            var e = InE(env, 0.6);
            var outK = OutK(env);
            var spd = D(Pm, "speed");
            var phase0 = env.Ltb * spd + (1 - e) * 1.2 * Math.Sign(spd == 0 ? 1 : spd);
            var ph = ((phase0 % 1) + 1) % 1;
            var unitSrc = S(Pm, "unit") == "line" && !string.IsNullOrEmpty(cut.LineText) && Clean(cut.LineText) != Clean(cut.Text)
                ? cut.LineText
                : cut.Text;
            var unit = JLayouts.CodePoints(Regex.Replace(unitSrc, @"\s+", " ").Trim() + "　");
            var NU = unit.Count;
            var lw = Math.Max(1, M * 0.0015);
            // 从屏幕四角连向最内层画框的透视线
            if (B(Pm, "persp"))
            {
                var pa0 = s0 * A;
                var pb0 = s0 * Bh;
                foreach (var (sx, sy) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
                    env.Line(new[]
                    {
                        new[] { cx + sx * W * 0.75, cy + sy * H * 0.75 },
                        new[] { cx + sx * pa0, cy + sy * pb0 },
                    }, Col(sc, "sub"), lw, 0.28 * e * outK, false);
            }
            var K = (int)Math.Ceiling(Lmax) + 1;
            for (var k = K; k >= 0; k--)
            {
                var L = k + ph - 1;
                if (L < 0 || L > Lmax + 0.2) continue;
                var sc1 = s0 * Math.Pow(q, L);
                var a = sc1 * A;
                var b = sc1 * Bh;
                var f = Math.Min(sc1 * Math.Min(A, Bh) * 0.15, M * 0.11);
                if (f < 3) continue;
                var alpha = J.Smooth(0, 0.9, L) * (0.3 + 0.7 * J.Clamp(L / Lmax * 1.4)) * e * outK;
                if (alpha < 0.02) continue;
                var kf = (int)Math.Floor(phase0);
                var style = S(Pm, "style");
                var outline = style == "outline" || (style == "alt" && (k + kf) % 2 == 0);
                var col = (k + kf) % 3 == 0 ? Col(sc, "accent") : Col(sc, "fg");
                var inset = f * 0.62;
                // JS 这里负数取模会得到负下标（取到 undefined 字）；C# 折正（tunnel 竖屏反速时
                // 最内一层前几个字的 JS 行为是空串，视觉无差——见移植报告 divergence 注）
                var gi = (k * 7 + kf * 3) % NU;
                if (gi < 0) gi += NU;
                string Edge(double len)
                {
                    var out2 = new StringBuilder();
                    var acc = 0.0;
                    for (var t = 0; t < 80; t++)
                    {
                        var ch = unit[(gi + t) % NU];
                        var ad = JText.Metrics.Adv(font, ch) * f * 1.06;
                        if (acc + ad > len)
                        {
                            gi += t;
                            break;
                        }
                        acc += ad;
                        out2.Append(ch);
                    }
                    return out2.ToString();
                }
                var common = new JObject
                {
                    ["font"] = font,
                    ["size"] = f,
                    ["track"] = 0.06,
                    ["ghost"] = false,
                    ["alpha"] = alpha,
                    ["color"] = col,
                };
                if (outline)
                {
                    common["fill"] = false;
                    common["stroke"] = Math.Max(1, f * 0.035);
                    common["strokeColor"] = col;
                }
                var hl = 2 * a - inset * 2.4;
                var vl = 2 * b - inset * 2.4;
                DrawIt(env, Merge(common, new JObject { ["text"] = Edge(hl), ["x"] = cx, ["y"] = cy - b + inset }));
                DrawIt(env, Merge(common, new JObject { ["text"] = Edge(vl), ["x"] = cx + a - inset, ["y"] = cy, ["rot"] = 90 }));
                DrawIt(env, Merge(common, new JObject { ["text"] = Edge(hl), ["x"] = cx, ["y"] = cy + b - inset, ["rot"] = 180 }));
                DrawIt(env, Merge(common, new JObject { ["text"] = Edge(vl), ["x"] = cx - a + inset, ["y"] = cy, ["rot"] = -90 }));
                env.Rect(cx - a, cy - b, a * 2, lw, Col(sc, "sub"), alpha * 0.5, false);
                env.Rect(cx - a, cy + b - lw, a * 2, lw, Col(sc, "sub"), alpha * 0.5, false);
            }
            env.Body = JLayouts.MainDraw(env, new JObject
            {
                ["text"] = mt,
                ["font"] = fontC,
                ["size"] = size,
                ["x"] = cx,
                ["y"] = cy,
                ["track"] = 0.05,
                ["lead"] = 1.15,
                ["color"] = Col(sc, "fg"),
            });
        },
    });

    // ---------------- wordCloud（JS L577-728） ----------------

    private sealed class CloudWord
    {
        public string Text = "";
        public string Font = "";
        public double Size, X, Y, Delay, A;
        public bool Vertical, Outline;
        public string Col = "s";
    }

    private static readonly Dictionary<string, List<CloudWord>> CloudCache = new();

    private static void RegisterWordCloud() => Reg0("wordCloud", new LayoutDef
    {
        Name = "ワードクラウド",
        Tags = new[] { "pop", "editorial", "graphic" },
        W = 0.9,
        Busy = true,
        Fits = n => n >= 1 && n <= 14,
        Plan = (rng, cut, st) =>
        {
            var pool = FontsOf(st, "display", "serif", "body");
            var p = new JObject { ["font"] = rng.Pick(FontsOf(st, "display")) };
            p["fonts"] = new JArray(
                rng.Pick(pool),
                rng.Pick(pool),
                rng.Pick(FontsOf(st, "body", "serif")));
            p["vert"] = rng.Pick(new[] { 0.0, 0.3, 0.5 });
            p["accentN"] = rng.Int(1, 3);
            p["outlineK"] = rng.Pick(new[] { 0.0, 0.2, 0.35 });
            p["wide"] = rng.Range(1.2, 1.7);
            return p;
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var s = (int)cut.Seed;
            var M = Math.Min(W, H);
            var mt = MainLines(cut.Text, W, H, 9, 5);
            var font = S(Pm, "font")!;
            var size = Math.Min(JLayouts.FitSize(mt, font, W * (W < H ? 0.7 : 0.52), H * 0.24,
                new JObject { ["track"] = 0.03, ["lead"] = 1.1 }), H * 0.18);
            var key = cut.Seed + "|" + W + "x" + H + "|" + cut.Text;
            if (!CloudCache.TryGetValue(key, out var lay))
            {
                lay = BuildCloud(env, mt, size);
                if (CloudCache.Count > 60) CloudCache.Clear();
                CloudCache[key] = lay;
            }
            var outK = OutK(env);
            for (var k = 0; k < lay.Count; k++)
            {
                var w = lay[k];
                var q = J.Clamp((env.Lt - w.Delay) / 0.32);
                if (q <= 0) continue;
                var sq = J.E.OutBack(q, 1.7) * (1 - 0.25 * J.E.InCubic(env.POut));
                var dx = J.Noise1(env.Ltb * 0.35 + k * 3.1, s) * M * 0.004;
                var dy = J.Noise1(env.Ltb * 0.3 + k * 5.7, s + 1) * M * 0.004;
                var it = new JObject
                {
                    ["text"] = w.Text,
                    ["font"] = w.Font,
                    ["size"] = w.Size * sq,
                    ["x"] = w.X + dx,
                    ["y"] = w.Y + dy,
                    ["vertical"] = w.Vertical,
                    ["track"] = 0.02,
                    ["color"] = w.Col == "a" ? Col(sc, "accent") : w.Col == "f" ? Col(sc, "fg") : Col(sc, "sub"),
                    ["alpha"] = Math.Min(1, q * 2.5) * outK * w.A,
                    ["ghost"] = false,
                };
                if (w.Outline)
                {
                    it["fill"] = false;
                    it["stroke"] = Math.Max(1, w.Size * 0.03);
                    it["strokeColor"] = (string)it["color"]!;
                }
                DrawIt(env, it);
            }
            env.Body = JLayouts.MainDraw(env, new JObject
            {
                ["text"] = mt,
                ["font"] = font,
                ["size"] = size,
                ["x"] = W / 2,
                ["y"] = H / 2,
                ["track"] = 0.03,
                ["lead"] = 1.1,
                ["color"] = Col(sc, "fg"),
            });
        },
    });

    private static List<CloudWord> BuildCloud(Env env, string mt, double size)
    {
        double W = env.W, H = env.H;
        var cut = env.Cut!;
        var Pm = env.P;
        var s = (int)cut.Seed;
        var M = Math.Min(W, H);
        var font = S(Pm, "font")!;
        var txt = Clean(cut.Text);
        var pool = new List<string>();
        void Add(string? w0)
        {
            var w = (w0 ?? "").Trim();
            if (w.Length > 0 && JLayouts.GlyphCount(w) <= 14 && !pool.Contains(w)
                && !JLayouts.CodePoints(w).All(c => J.IsPunct(c[0]))) pool.Add(w);
        }
        var lineSrc = string.IsNullOrEmpty(cut.LineText) ? cut.Text : cut.LineText;
        foreach (var w in Planner.ChunkText(lineSrc)) Add(w);
        foreach (var w in cut.Words) Add(w);
        foreach (var g0 in Planner.Segments(lineSrc))
        {
            var g = g0.Trim();
            if (JLayouts.GlyphCount(g) >= 2 || JLayouts.CodePoints(g).Any(c => J.IsKanji(c[0]))) Add(g);
        }
        var rom = RomaOf(txt);
        if (rom != null) Add(rom);
        if (!string.IsNullOrEmpty(cut.Note)) Add(cut.Note);
        Add(txt);
        foreach (var c in JLayouts.CodePoints(txt).Where(c => J.IsKanji(c[0]))) Add(c);
        if (JLayouts.GlyphCount(cut.LineText) <= 14) Add(cut.LineText);
        if (pool.Count == 0) pool.Add(txt.Length > 0 ? txt : "・");
        var mm = JLayouts.Measure(new JObject { ["text"] = mt, ["font"] = font, ["size"] = size, ["track"] = 0.03, ["lead"] = 1.1 });
        var boxes = new List<double[]>
        {
            new[]
            {
                W / 2 - mm.W / 2 - size * 0.25, H / 2 - mm.H / 2 - size * 0.18,
                W / 2 + mm.W / 2 + size * 0.25, H / 2 + mm.H / 2 + size * 0.18,
            },
        };
        var outL = new List<CloudWord>();
        var port = W < H;
        var stretchX = port ? 0.75 : D(Pm, "wide");
        var stretchY = port ? 1.3 : 1.0;
        var maxR = Math.Sqrt(W * W + H * H) * 0.55;
        var acc = 0;
        var fonts = StrArr(Pm["fonts"]);
        if (fonts.Count == 0) fonts = new List<string> { font };
        var vertK = D(Pm, "vert");
        var accentN = I(Pm, "accentN");
        var outlineK = D(Pm, "outlineK");
        for (var k = 0; k < 70 && outL.Count < 44; k++)
        {
            var word = pool[k % pool.Count];
            var latin = Regex.IsMatch(word, "[A-Za-z]");
            var vertical = !latin && JLayouts.GlyphCount(word) <= 6 && J.R(s, k, 3) < vertK;
            var tier = Math.Pow(0.95, outL.Count);
            var fs = Math.Max(M * 0.018,
                Math.Min(Math.Min(W * 0.4 / Math.Max(1.5, JLayouts.GlyphCount(word)), size * 0.6),
                    M * 0.125 * tier * (0.65 + 0.7 * J.R(s, k, 4))));
            var fontW = fonts[k % fonts.Count];
            var it = new JObject { ["text"] = word, ["font"] = fontW, ["size"] = fs, ["track"] = 0.02, ["vertical"] = vertical };
            var m = JLayouts.Measure(it);
            var maxW = W * 0.9;
            var maxH = H * 0.9;
            if (m.W > maxW || m.H > maxH)
            {
                var fk = Math.Min(maxW / m.W, maxH / m.H);
                fs *= fk;
                it["size"] = fs;
                m = JLayouts.Measure(it);
            }
            var pad = fs * 0.12;
            var w2 = m.W / 2 + pad;
            var h2 = m.H / 2 + pad;
            var a0 = J.R(s, k, 5) * J.TAU;
            double[]? placed = null;
            for (var t = 0; t < 520; t++)
            {
                var ang = a0 + t * 0.42;
                var rad = t / 520.0 * maxR;
                var x = W / 2 + Math.Cos(ang) * rad * stretchX;
                var y = H / 2 + Math.Sin(ang) * rad * stretchY * 0.8;
                if (x - w2 < W * 0.035 || x + w2 > W * 0.965 || y - h2 < H * 0.045 || y + h2 > H * 0.955) continue;
                var hit = false;
                foreach (var b in boxes)
                    if (x - w2 < b[2] && x + w2 > b[0] && y - h2 < b[3] && y + h2 > b[1])
                    {
                        hit = true;
                        break;
                    }
                if (!hit)
                {
                    placed = new[] { x, y };
                    break;
                }
            }
            if (placed == null)
            {
                acc++;
                if (acc > 16) break;
                continue;
            }
            boxes.Add(new[] { placed[0] - w2, placed[1] - h2, placed[0] + w2, placed[1] + h2 });
            var d = Math.Sqrt(Math.Pow((placed[0] - W / 2) / W, 2) + Math.Pow((placed[1] - H / 2) / H, 2));
            var idx = outL.Count;
            var pos4 = new[] { 4, 9, 15 }.ToList().IndexOf(idx);
            outL.Add(new CloudWord
            {
                Text = word,
                Font = fontW,
                Size = fs,
                X = placed[0],
                Y = placed[1],
                Vertical = vertical,
                Delay = 0.06 + d * 0.9 + idx * 0.012,
                Col = pos4 >= 0 && pos4 < accentN ? "a" : idx % 2 != 0 ? "s" : "f",
                A = idx % 2 != 0 ? 0.8 : 0.5 + 0.2 * tier,
                Outline = J.R(s, k, 7) < outlineK,
            });
        }
        return outL;
    }

    // ---------------- bounceLine（JS L730-832） ----------------

    private static void RegisterBounceLine() => Reg0("bounceLine", new LayoutDef
    {
        Name = "跳ねる",
        Tags = new[] { "pop" },
        W = 1,
        Fits = n => n >= 2 && n <= 16,
        EnterBias = new JObject
        {
            ["drop"] = 1.8,
            ["pop"] = 1.5,
            ["cut"] = 1.2,
            ["slice"] = 0.4,
            ["stretch"] = 0.5,
        },
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "display")),
            ["mode"] = rng.Pick(new[] { "wave", "wave", "beat", "hop" }),
            ["hop"] = rng.Range(0.38, 0.6),
            ["tempo"] = rng.Range(0.42, 0.6),
            ["shadow"] = rng.Chance(0.7),
            ["line"] = rng.Pick(new[] { "line", "line", "dots", "none" }),
            ["tilt"] = rng.Chance(0.5),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var s = (int)cut.Seed;
            var M = Math.Min(W, H);
            var lt = env.Lt;
            var font = S(Pm, "font")!;
            var mt = MainLines(cut.Text, W, H, 11, 6);
            var nL = mt.Split('\n').Length;
            var baseIt = new JObject
            {
                ["text"] = mt,
                ["font"] = font,
                ["x"] = W / 2,
                ["y"] = H / 2 + (nL > 1 ? 0 : H * 0.03),
                ["track"] = 0.1,
                ["lead"] = 2.0,
            };
            var size = Math.Min(
                JLayouts.FitSize(mt, font, W * 0.84, H * (nL > 1 ? 0.52 : 0.3), baseIt),
                H * 0.2);
            baseIt["size"] = size;
            var gl = GlyphPts(baseIt);
            var n = gl.Count;
            var outK = OutK(env);
            var inA = InE(env, 0.5);
            var mode = S(Pm, "mode")!;
            var tempo = D(Pm, "tempo");
            var hopH = size * D(Pm, "hop");
            var hd = J.Clamp(tempo * 0.62, 0.22, 0.34);
            const double sq = 0.13;
            // 跳跃曲线，tau 是起跳后经过的时间
            (double H, double Sx, double Sy) Prof(double tau)
            {
                if (tau < -0.07 || tau > hd + sq) return (0, 1, 1);
                if (tau < 0)
                {
                    var kk = Math.Sin(Math.PI * (tau + 0.07) / 0.07);
                    return (0, 1 + 0.08 * kk, 1 - 0.1 * kk);
                }
                if (tau < hd)
                {
                    var u = tau / hd;
                    return (4 * u * (1 - u), 0.94, 1.08);
                }
                var k2 = Math.Sin(Math.PI * (tau - hd) / sq);
                return (0, 1 + 0.2 * k2, 1 - 0.24 * k2);
            }
            const double start = 0.25;
            BBox? bb = null;
            var lines = new SortedDictionary<int, (double X0, double X1, double Y)>();
            for (var i = 0; i < gl.Count; i++)
            {
                var g = gl[i];
                var tau = -1.0;
                var t = lt - start;
                if (t > -0.1)
                {
                    if (mode == "wave")
                    {
                        var gap = Math.Min(0.09, 0.9 / n);
                        var P0 = Math.Max(tempo * 2.2, n * gap + hd + 0.35);
                        var tt = t - i * gap;
                        tau = tt < -0.1 ? -1 : ((tt + 0.07) % P0) - 0.07;
                    }
                    else if (mode == "beat")
                    {
                        var bt = BeatOf(env);
                        if (bt.Has && bt.Len > 0.2)
                            tau = bt.Index % n == i ? bt.Since : -1;
                        else
                        {
                            var kk = (int)Math.Floor(t / tempo);
                            tau = ((kk % n) + n) % n == i ? t - kk * tempo : -1;
                        }
                    }
                    else
                    {
                        var P1 = J.Rr(0.9, 1.7, s, i, 7);
                        var ph = J.R(s, i, 8) * P1;
                        tau = ((t + ph) % P1) - 0.07;
                        if (t + ph < P1 - 0.07) tau = -1;
                    }
                }
                var pr = Prof(tau);
                var yb = g.Y + size * 0.5;
                var lk = g.Li;
                if (!lines.TryGetValue(lk, out var L0))
                {
                    L0 = (g.X - g.W / 2, g.X + g.W / 2, yb);
                    lines[lk] = L0;
                }
                lines[lk] = (Math.Min(L0.X0, g.X - g.W / 2), Math.Max(L0.X1, g.X + g.W / 2), yb);
                if (B(Pm, "shadow"))
                {
                    var kk = 1 - pr.H * 0.55;
                    env.Ctx.Save();
                    env.Ctx.Translate((float)g.X, (float)(yb + size * 0.06));
                    env.Ctx.Scale(1, 0.2f);
                    env.Circle(0, 0, size * 0.34 * kk * pr.Sx, Col(sc, "sub"), null, 0, 0.28 * kk * outK * inA, false);
                    env.Ctx.Restore();
                }
                bb = U(bb, JLayouts.MainDraw(env, new JObject
                {
                    ["text"] = g.Ch,
                    ["font"] = font,
                    ["size"] = size,
                    ["x"] = g.X,
                    ["y"] = yb - size * 0.5 * pr.Sy - pr.H * hopH,
                    ["sx"] = pr.Sx,
                    ["sy"] = pr.Sy,
                    ["rot"] = B(Pm, "tilt") && pr.H > 0 ? Math.Sin(tau / hd * Math.PI * 2) * 7 * (i % 2 != 0 ? 1 : -1) : 0,
                    ["color"] = mode == "beat" && pr.H > 0 ? Col(sc, "accent") : Col(sc, "fg"),
                    ["mi"] = i,
                }));
            }
            var lw = Math.Max(1.5, M * 0.0022);
            var lineStyle = S(Pm, "line");
            foreach (var kv in lines)
            {
                var L = kv.Value;
                var pad = size * 0.35;
                var x0 = L.X0 - pad;
                var x1 = L.X1 + pad;
                var y = L.Y + size * 0.1;
                if (lineStyle == "line")
                    env.Line(new[] { new[] { x0, y }, new[] { J.Lerp(x0, x1, inA), y } }, Col(sc, "sub"), lw, 0.7 * outK, false);
                else if (lineStyle == "dots")
                {
                    var m = Math.Max(4, (int)Math.Round((x1 - x0) / (size * 0.25)));
                    for (var j = 0; j <= m; j++)
                        if (j / (double)m <= inA)
                            env.Circle(J.Lerp(x0, x1, j / (double)m), y, lw * 1.2, Col(sc, "sub"), null, 0, 0.8 * outK, false);
                }
            }
            env.Body = bb;
        },
    });

    // ---------------- elastic（JS L834-936） ----------------

    private static void RegisterElastic() => Reg0("elastic", new LayoutDef
    {
        Name = "ゴム",
        Tags = new[] { "pop", "graphic" },
        W = 0.9,
        Portrait = 0.85,
        Fits = n => n >= 2 && n <= 12,
        EnterBias = new JObject { ["cut"] = 1.6, ["stretch"] = 0.3, ["pop"] = 1.2 },
        Plan = (rng, cut, st) =>
        {
            var port = cut.H > cut.W;
            return new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
                ["orient"] = port
                    ? cut.N <= 5 ? rng.Pick(new[] { "v", "v", "h" }) : "v"
                    : rng.Pick(new[] { "h", "h", "diag" }),
                ["ang"] = rng.Range(6, 12) * rng.Pick(new[] { 1, -1 }),
                ["every"] = rng.Range(1.1, 1.6),
                ["amp"] = rng.Range(0.3, 0.45),
                ["anchor"] = rng.Pick(new[] { "dot", "ring", "pin" }),
            };
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var s = (int)cut.Seed;
            var M = Math.Min(W, H);
            var lt = env.Lt;
            var font = S(Pm, "font")!;
            var orient = S(Pm, "orient")!;
            var vert = orient == "v";
            var txt = vert ? Clean(cut.Text) : cut.Text.Trim();
            var ang = orient == "diag" ? D(Pm, "ang") : 0;
            var baseIt = new JObject
            {
                ["text"] = txt,
                ["font"] = font,
                ["x"] = 0,
                ["y"] = 0,
                ["track"] = 0.08,
                ["vertical"] = vert,
            };
            var avail = vert ? H * 0.58 : W < H ? W * 0.7 : W * 0.62;
            var size = Math.Min(
                JLayouts.FitSize(txt, font, vert ? W * 0.3 : avail, vert ? avail : H * 0.22, baseIt),
                H * 0.2);
            baseIt["size"] = size;
            var gl = GlyphPts(baseIt);
            var n = gl.Count;
            if (n == 0)
            {
                env.Body = null;
                return;
            }
            double Along(GlyphPt g) => vert ? g.Y : g.X;
            var a0 = Along(gl[0]) - (vert ? gl[0].H : gl[0].W) / 2;
            var a1 = Along(gl[n - 1]) + (vert ? gl[n - 1].H : gl[n - 1].W) / 2;
            var gap = size * 0.85;
            var half = (a1 - a0) / 2 + gap;
            // 弹簧：锚点从中心弹出并过冲
            var te = lt - 0.02;
            var spr = te <= 0 ? 0 : 1 - Math.Exp(-te / 0.1) * Math.Cos(te * 15);
            var k = Math.Max(0.04, spr) * (1 + 0.6 * J.E.InCubic(env.POut));
            // 拨弦：带子上的驻波
            var tau = -1.0;
            var ampK = 1.0;
            var bt = BeatOf(env);
            if (bt.Has && bt.Len > 0.2 && lt > 0.5)
            {
                tau = bt.Since;
                ampK = bt.Index % 2 != 0 ? 0.55 : 1;
            }
            else if (lt > 0.45) tau = (lt - 0.45) % D(Pm, "every");
            var A = size * D(Pm, "amp") * ampK * (lt < 0.45 ? 0 : 1);
            double Wv(double u) => tau < 0 ? 0 : A * Math.Sin(Math.PI * u) * Math.Cos(tau * 22) * Math.Exp(-tau / 0.42);
            var cx = W / 2;
            var cy = H / 2;
            var cr = Math.Cos(ang * J.DEG);
            var sr = Math.Sin(ang * J.DEG);
            double[] P2(double a, double d) => vert ? new[] { cx + d, cy + a } : new[] { cx + a * cr - d * sr, cy + a * sr + d * cr };
            var aA = -half * k;
            var aB = half * k;
            double UOf(double a) => (a - aA) / Math.Max(1, aB - aA);
            var outK = OutK(env);
            var inA = InE(env, 0.3);
            // 橡皮筋段：锚点到文字两端
            var lw = Math.Max(1.5, size * 0.03);
            double[][] Seg(double from, double to)
            {
                var pts = new List<double[]>();
                for (var j = 0; j <= 12; j++)
                {
                    var a = J.Lerp(from, to, j / 12.0);
                    pts.Add(P2(a, Wv(UOf(a))));
                }
                return pts.ToArray();
            }
            var tA = a0 * k - size * 0.12;
            var tB = a1 * k + size * 0.12;
            env.Line(Seg(aA, tA), Col(sc, "sub"), lw, 0.9 * outK * inA, false);
            env.Line(Seg(tB, aB), Col(sc, "sub"), lw, 0.9 * outK * inA, false);
            BBox? bb = null;
            for (var i = 0; i < gl.Count; i++)
            {
                var g = gl[i];
                var a = Along(g) * k;
                var u = UOf(a);
                var d = Wv(u);
                var slope = (Wv(u + 0.01) - Wv(u - 0.01)) / (0.02 * Math.Max(1, aB - aA));
                var xy = P2(a, d);
                var st = J.Clamp(k, 0.15, 1.6);
                bb = U(bb, JLayouts.MainDraw(env, new JObject
                {
                    ["text"] = g.Ch,
                    ["font"] = font,
                    ["size"] = size,
                    ["x"] = xy[0],
                    ["y"] = xy[1],
                    ["vertical"] = vert,
                    ["sx"] = vert ? 1 / Math.Sqrt(st) : st,
                    ["sy"] = vert ? st : 1 / Math.Sqrt(st),
                    ["rot"] = (vert ? -Math.Atan(slope) : Math.Atan(slope)) / J.DEG + ang,
                    ["color"] = Col(sc, "fg"),
                    ["mi"] = i * 0.5,
                }));
            }
            // 两端锚点
            var R = Math.Max(5, size * 0.1);
            foreach (var a in new[] { aA, aB })
            {
                var xy = P2(a, 0);
                var q = J.E.OutBack(J.Clamp(lt / 0.2), 2) * outK;
                if (q <= 0) continue;
                var anchor = S(Pm, "anchor");
                if (anchor == "dot") env.Circle(xy[0], xy[1], R * q, Col(sc, "accent"), null, 0, 1, true);
                else if (anchor == "ring")
                {
                    env.Circle(xy[0], xy[1], R * 1.3 * q, null, Col(sc, "accent"), lw * 1.3, 1, true);
                    env.Circle(xy[0], xy[1], R * 0.45 * q, Col(sc, "fg"), null, 0, 1, false);
                }
                else
                {
                    var pp = P2(a, -R * 3.2);
                    env.Line(new[] { new[] { pp[0], pp[1] }, new[] { xy[0], xy[1] } }, Col(sc, "fg"), lw, q, false);
                    env.Circle(pp[0], pp[1], R * 0.9 * q, Col(sc, "accent"), null, 0, 1, true);
                    env.Circle(xy[0], xy[1], R * 0.4 * q, Col(sc, "fg"), null, 0, 1, false);
                }
            }
            env.Body = bb;
        },
    });

    // ---------------- crossBands（JS L938-1025） ----------------

    private static void RegisterCrossBands() => Reg0("crossBands", new LayoutDef
    {
        Name = "交差帯",
        Tags = new[] { "graphic", "pop", "glitch" },
        W = 1,
        Treat = "safe",
        Busy = true,
        Fits = n => n >= 1 && n <= 14,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "display")),
            ["fb"] = rng.Pick(FontsOf(st, "body", "display")),
            ["ang"] = rng.Range(13, 22),
            ["plate"] = rng.Pick(new[] { "box", "double", "shadow" }),
            ["speed"] = rng.Range(0.7, 1.2),
            ["swap"] = rng.Chance(0.5),
            ["sep"] = rng.Pick(new[] { "／", "・", "　", "×" }),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var M = Math.Min(W, H);
            var lt = env.Lt;
            var font = S(Pm, "font")!;
            var fb = S(Pm, "fb")!;
            var port = W < H;
            var ang = port ? D(Pm, "ang") + 16 : D(Pm, "ang");
            var bh = M * (port ? 0.12 : 0.105);
            var cols = new List<string>
            {
                PlateCol(sc, Col(sc, "ink"), Col(sc, "fg")),
                PlateCol(sc, Col(sc, "accent"), Col(sc, "accent2"), Col(sc, "sub")),
            };
            if (B(Pm, "swap")) cols.Reverse();
            var len = Math.Sqrt(W * W + H * H) * 1.15;
            var unit = Regex.Replace(!string.IsNullOrEmpty(cut.LineText) ? cut.LineText : cut.Text, @"\s+", " ").Trim()
                       + "　" + S(Pm, "sep") + "　";
            var fsz = bh * 0.46;
            var per = JLayouts.Measure(new JObject { ["text"] = unit, ["font"] = fb, ["size"] = fsz, ["track"] = 0.08 }).W;
            var reps = Math.Min(40, (int)Math.Ceiling(len * 1.2 / Math.Max(1, per)) + 2);
            var outE = J.E.InCubic(env.POut);
            var speed = D(Pm, "speed");
            for (var b = 0; b < 2; b++)
            {
                var e = J.E.OutExpo(J.Clamp((lt - b * 0.08) / 0.5)) * (1 - outE);
                if (e <= 0) continue;
                var a = (b != 0 ? -ang : ang) * J.DEG;
                var dir = b != 0 ? -1 : 1;
                env.Ctx.Save();
                env.Ctx.Translate((float)(W / 2), (float)(H / 2));
                env.Ctx.RotateRadians((float)a);
                var L = len * e;
                var x0 = dir > 0 ? -len / 2 : len / 2 - L;
                env.Rect(x0, -bh / 2, L, bh, cols[b], 1, lt < 0.6);
                env.Ctx.Save();
                env.Ctx.ClipRect(new SKRect((float)x0, (float)(-bh / 2), (float)(x0 + L), (float)(bh / 2)));
                var off = ((env.Ltb * speed * M * 0.12 * dir) % per + per) % per;
                // 背景带整行一次画：逐字 draw 是本版式的主要开销（JS 未传 alpha → globalAlpha 保持 1）
                JLayouts.FastRow(env, JLayouts.Repeat(unit, reps), fb, fsz, -len * 0.6 - per + off, 0, fsz * 0.08,
                    OnCol(sc, cols[b]), 1);
                env.Ctx.Restore();
                env.Ctx.Restore();
            }
            // 交叉处的歌词色板
            var mt = MainLines(cut.Text, W, H, 9, 5);
            var size = Math.Min(JLayouts.FitSize(mt, font, W * (port ? 0.66 : 0.5), H * 0.24,
                new JObject { ["track"] = 0.04, ["lead"] = 1.12 }), H * 0.17);
            var mm = JLayouts.Measure(new JObject { ["text"] = mt, ["font"] = font, ["size"] = size, ["track"] = 0.04, ["lead"] = 1.12 });
            var pw = mm.W + size * 0.9;
            var ph = mm.H + size * 0.7;
            var q = J.E.OutBack(J.Clamp((lt - 0.1) / 0.28), 1.6) * (1 - outE);
            if (q > 0)
            {
                var lw = Math.Max(2, size * 0.035);
                env.Ctx.Save();
                env.Ctx.Translate((float)(W / 2), (float)(H / 2));
                env.Ctx.Scale((float)q, (float)q);
                if (S(Pm, "plate") == "shadow")
                {
                    var o = size * 0.12;
                    env.Rect(-pw / 2 + o, -ph / 2 + o, pw, ph, cols[1], 1, false);
                }
                env.Rect(-pw / 2, -ph / 2, pw, ph, Col(sc, "bg"), 1, false);
                env.RRect(-pw / 2, -ph / 2, pw, ph, 0, null, 1, false, Col(sc, "fg"), lw);
                if (S(Pm, "plate") == "double")
                {
                    var o = lw * 2.6;
                    env.RRect(-pw / 2 + o, -ph / 2 + o, pw - o * 2, ph - o * 2, 0, null, 1, false, Col(sc, "fg"), lw * 0.5);
                }
                env.Ctx.Restore();
            }
            env.Body = JLayouts.MainDraw(env, new JObject
            {
                ["text"] = mt,
                ["font"] = font,
                ["size"] = size,
                ["x"] = W / 2,
                ["y"] = H / 2,
                ["track"] = 0.04,
                ["lead"] = 1.12,
                ["color"] = Col(sc, "fg"),
            });
        },
    });

    // ---------------- stickerBomb（JS L1027-1199） ----------------

    /// <summary>贴纸填充（矩形/圆/星形轮廓按 grow 外扩）。</summary>
    private static void FillSticker(Env env, string shape, double w, double h, double grow, int seed,
        string col, double alpha, bool ghostable)
    {
        var c = env.ColOf(col, ghostable);
        if (c == null || alpha <= 0) return;
        using var path = StickerPath(shape, w, h, grow, seed);
        using var p = new SKPaint { IsAntialias = true, Color = Env.SkColor(c, alpha), BlendMode = env.Blend };
        env.Ctx.DrawPath(path, p);
    }

    private static void RegisterStickerBomb() => Reg0("stickerBomb", new LayoutDef
    {
        Name = "ステッカー",
        Tags = new[] { "pop", "graphic" },
        W = 0.9,
        Treat = "safe",
        Fits = n => n >= 1 && n <= 12,
        EnterBias = new JObject
        {
            ["cut"] = 2,
            ["pop"] = 1.2,
            ["slice"] = 0.4,
            ["wipe"] = 0.5,
            ["assemble"] = 0.4,
        },
        Plan = (rng, cut, st) =>
        {
            var n = cut.N;
            return new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display")),
                ["fs"] = rng.Pick(FontsOf(st, "body", "display")),
                ["main"] = n <= 3
                    ? rng.Pick(new[] { "circle", "burst", "rrect" })
                    : n <= 5
                        ? rng.Pick(new[] { "rrect", "burst", "pill" })
                        : rng.Pick(new[] { "rrect", "pill" }),
                ["cnt"] = rng.Int(4, 6),
                ["rot"] = rng.Range(-5, 5),
                ["spin"] = rng.Range(0, 6),
            };
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var s = (int)cut.Seed;
            var M = Math.Min(W, H);
            var lt = env.Lt;
            var port = W < H;
            var font = S(Pm, "font")!;
            var fsFont = S(Pm, "fs")!;
            var mainShape = S(Pm, "main")!;
            var mt = MainLines(cut.Text, W, H, 8, 5);
            var size = Math.Min(
                JLayouts.FitSize(mt, font, W * (port ? 0.64 : 0.5), H * 0.26, new JObject { ["track"] = 0.03, ["lead"] = 1.1 }),
                H * (mainShape is "circle" or "burst" ? 0.15 : 0.19));
            var mm = JLayouts.Measure(new JObject { ["text"] = mt, ["font"] = font, ["size"] = size, ["track"] = 0.03, ["lead"] = 1.1 });
            var pw = mm.W + size * 0.8;
            var ph = mm.H + size * 0.6;
            var dark = J.Lum(Col(sc, "bg")) < 0.45;
            var border = dark ? Col(sc, "fg") : Col(sc, "bg");
            var bw = Math.Max(4, size * 0.09);
            var outK = 1 - J.E.InCubic(env.POut);
            var grow = 1 + 0.08 * J.E.OutCubic(env.POut);
            (double S, double A, double R)? Slap(double t0)
            {
                var q = J.Clamp((lt - t0) / 0.16);
                if (q <= 0) return null;
                return (1 + 0.3 * Math.Pow(1 - q, 2) - 0.06 * Math.Sin(Math.PI * q) * (q < 1 ? 1 : 0),
                    Math.Min(1, q * 3), (1 - J.E.OutCubic(q)) * 14);
            }
            void DrawSticker(double x, double y, double rot, string sh, double w, double h, string fill,
                (double S, double A, double R) q, int seed)
            {
                env.Ctx.Save();
                env.Ctx.Translate((float)x, (float)y);
                env.Ctx.RotateDegrees((float)(rot + q.R)); // JS: rotate((rot + q.r) * DEG)
                env.Ctx.Scale((float)(q.S * grow), (float)(q.S * grow));
                if (env.Pass == "main")
                {
                    if (!dark)
                    {
                        env.Ctx.Save();
                        env.Ctx.Translate((float)(bw * 0.5), (float)(bw * 0.7));
                        FillSticker(env, sh, w, h, bw, seed, Col(sc, "fg"), 0.22 * q.A * outK, false);
                        env.Ctx.Restore();
                    }
                    FillSticker(env, sh, w, h, bw, seed, border, q.A * outK, false);
                    FillSticker(env, sh, w, h, 0, seed, fill, q.A * outK, false);
                }
                else if (env.PassColor != null && GIn(env))
                    FillSticker(env, sh, w, h, bw, seed, env.PassColor, q.A, true);
                env.Ctx.Restore();
            }
            // 主贴纸周围的小贴纸
            var txt = Clean(cut.Text);
            var pool = new List<string>();
            void Add(string? w0)
            {
                var w = (w0 ?? "").Trim();
                if (w.Length > 0 && w != txt && JLayouts.GlyphCount(w) <= 12 && !pool.Contains(w)) pool.Add(w);
            }
            foreach (var w in cut.Words) Add(w);
            var rom = RomaOf(txt);
            if (rom != null) Add(rom);
            foreach (var w in Planner.ChunkText(cut.LineText)) Add(w);
            Add("No." + (Math.Max(0, cut.Line) + 1).ToString("D2", CultureInfo.InvariantCulture));
            Add("♡");
            Add("!!");
            if (pool.Count == 0) pool.Add(txt.Length > 0 ? txt : "★");
            var fills = new[] { Col(sc, "accent"), Col(sc, "ink"), Col(sc, "accent2"), Col(sc, "fg") }
                .Select(c => PlateCol(sc, c)).ToList();
            var mainFill = PlateCol(sc, Col(sc, "ink"), Col(sc, "accent"));
            var cnt = I(Pm, "cnt");
            var spin = D(Pm, "spin");
            var hw = pw / 2;
            var hh = ph / 2;
            var items = new List<(string Word, string Sh, double Fsz, double W, double H, double X, double Y, double Rot, string Fill, double T0)>();
            for (var k = 0; k < cnt; k++)
            {
                var word = pool[k % pool.Count];
                var glyphs = JLayouts.GlyphCount(word);
                var sh = glyphs <= 2
                    ? J.R(s, k, 2) < 0.5 ? "circle" : "burst"
                    : J.R(s, k, 2) < 0.5 ? "pill" : "rrect";
                var fsz = J.Clamp(M * J.Rr(0.042, 0.06, s, k, 3), 12, 80);
                var m = JLayouts.Measure(new JObject { ["text"] = word, ["font"] = fsFont, ["size"] = fsz, ["track"] = 0.06 });
                var w = m.W + fsz * 1.0;
                var h = fsz * 1.7;
                if (sh is "circle" or "burst") w = h = Math.Max(m.W, fsz) + fsz * 1.2;
                var a = k / (double)cnt * J.TAU + J.Rs(s, k, 4) * 0.35 + spin;
                var x = W / 2 + Math.Cos(a) * (hw + w * 0.32);
                var y = H / 2 + Math.Sin(a) * (hh + h * 0.4);
                x = J.Clamp(x, W * 0.05 + w / 2, W * 0.95 - w / 2);
                y = J.Clamp(y, H * 0.06 + h / 2, H * 0.94 - h / 2);
                var fk = fills[k % fills.Count];
                items.Add((word, sh, fsz, w, h, x, y, J.Rs(s, k, 5) * 18,
                    fk == mainFill ? fills[(k + 1) % fills.Count] : fk, 0.14 + k * 0.07));
            }
            foreach (var it in items)
            {
                var q = Slap(it.T0);
                if (q == null) continue;
                DrawSticker(it.X, it.Y, it.Rot, it.Sh, it.W, it.H, it.Fill, q.Value, s);
                env.Ctx.Save();
                env.Ctx.Translate((float)it.X, (float)it.Y);
                env.Ctx.RotateDegrees((float)(it.Rot + q.Value.R));
                env.Ctx.Scale((float)(q.Value.S * grow), (float)(q.Value.S * grow));
                DrawIt(env, new JObject
                {
                    ["text"] = it.Word,
                    ["font"] = fsFont,
                    ["size"] = it.Fsz,
                    ["track"] = 0.06,
                    ["x"] = 0,
                    ["y"] = 0,
                    ["color"] = OnCol(sc, it.Fill),
                    ["alpha"] = q.Value.A * outK,
                    ["ghost"] = false,
                });
                env.Ctx.Restore();
            }
            // 主贴纸最后贴，盖在最上面
            var q2 = Slap(0.02);
            if (q2 == null)
            {
                env.Body = null;
                return;
            }
            var sh2 = mainShape;
            double w2 = pw, h2 = ph;
            if (sh2 is "circle" or "burst") w2 = h2 = Math.Max(pw, ph) * 1.02;
            DrawSticker(W / 2, H / 2, D(Pm, "rot"), sh2, w2, h2, mainFill, q2.Value, s);
            env.Ctx.Save();
            env.Ctx.Translate((float)(W / 2), (float)(H / 2));
            env.Ctx.RotateDegrees((float)(D(Pm, "rot") + q2.Value.R));
            env.Ctx.Scale((float)q2.Value.S, (float)q2.Value.S);
            var lb = JLayouts.MainDraw(env, new JObject
            {
                ["text"] = mt,
                ["font"] = font,
                ["size"] = size,
                ["x"] = 0,
                ["y"] = 0,
                ["track"] = 0.03,
                ["lead"] = 1.1,
                ["color"] = OnCol(sc, mainFill),
                ["noHold"] = PlateHold(env),
            });
            env.Ctx.Restore();
            env.Body = lb != null
                ? BbRect(W / 2 - w2 / 2, H / 2 - h2 / 2, W / 2 + w2 / 2, H / 2 + h2 / 2)
                : null;
        },
    });

    // ---------------- neon（JS L1201-1314） ----------------

    /// <summary>Canvas2D 的 ctx.shadowColor/shadowBlur 对位：带阴影的描边（neon 外框）。</summary>
    private static void GlowLine(Env env, double[][] pts, string color, double lw, double alpha, double blurPx)
    {
        if (alpha <= 0 || pts.Length < 2) return;
        using var path = new SKPath();
        path.MoveTo((float)pts[0][0], (float)pts[0][1]);
        for (var i = 1; i < pts.Length; i++) path.LineTo((float)pts[i][0], (float)pts[i][1]);
        using var p = new SKPaint
        {
            IsAntialias = true,
            IsStroke = true,
            StrokeWidth = (float)lw,
            StrokeJoin = SKStrokeJoin.Round,
            StrokeCap = SKStrokeCap.Round,
            Color = Env.SkColor(color, alpha),
        };
        if (blurPx > 0.5)
            p.ImageFilter = SKImageFilter.CreateDropShadow(0, 0, (float)(blurPx / 2), (float)(blurPx / 2), Env.SkColor(color, alpha));
        env.Ctx.DrawPath(path, p);
    }

    private static void GlowRRect(Env env, double x, double y, double w, double h, double r,
        string stroke, double lw, double alpha, double blurPx)
    {
        if (alpha <= 0) return;
        r = Math.Min(r, Math.Min(w / 2, h / 2));
        using var rr = new SKRoundRect(new SKRect((float)x, (float)y, (float)(x + w), (float)(y + h)), (float)r);
        using var p = new SKPaint
        {
            IsAntialias = true,
            IsStroke = true,
            StrokeWidth = (float)lw,
            Color = Env.SkColor(stroke, alpha),
        };
        if (blurPx > 0.5)
            p.ImageFilter = SKImageFilter.CreateDropShadow(0, 0, (float)(blurPx / 2), (float)(blurPx / 2), Env.SkColor(stroke, alpha));
        env.Ctx.DrawRoundRect(rr, p);
    }

    private static void RegisterNeon() => Reg0("neon", new LayoutDef
    {
        Name = "ネオン",
        Tags = new[] { "calm", "emotional" },
        W = 1,
        Treat = false,
        Fits = n => n >= 1 && n <= 14,
        EnterBias = new JObject
        {
            ["flicker"] = 2.6,
            ["blur"] = 1.4,
            ["cut"] = 1.3,
            ["assemble"] = 0.3,
            ["slice"] = 0.5,
            ["scramble"] = 0.6,
        },
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "display", "body")),
            ["tube"] = rng.Pick(new[] { "accent", "accent", "accent2", "fg" }),
            ["frame"] = rng.Pick(new[] { "box", "under", "bracket", "none" }),
            ["flick"] = rng.Chance(0.75),
            ["sub"] = rng.Chance(0.5),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var s = (int)cut.Seed;
            var M = Math.Min(W, H);
            var lt = env.Lt;
            var font = S(Pm, "font")!;
            var mt = MainLines(cut.Text, W, H, 9, 5);
            var baseIt = new JObject
            {
                ["text"] = mt,
                ["font"] = font,
                ["x"] = W / 2,
                ["y"] = H / 2,
                ["track"] = 0.08,
                ["lead"] = 1.25,
            };
            var size = Math.Min(JLayouts.FitSize(mt, font, W * 0.72, H * 0.34, baseIt), H * 0.19);
            baseIt["size"] = size;
            var dark = J.Lum(Col(sc, "bg")) < 0.45;
            var tubeSel = S(Pm, "tube");
            var cand = tubeSel == "accent2"
                ? new[] { Col(sc, "accent2"), Col(sc, "accent"), Col(sc, "fg") }
                : tubeSel == "fg"
                    ? new[] { Col(sc, "fg"), Col(sc, "accent") }
                    : new[] { Col(sc, "accent"), Col(sc, "accent2"), Col(sc, "fg") };
            var tube = cand.FirstOrDefault(c => !string.IsNullOrEmpty(c) && J.Contrast(c, Col(sc, "bg")) >= 2.2) ?? Col(sc, "fg");
            var core = dark ? J.Mix(tube, "#FFFFFF", 0.72) : J.Mix(tube, "#FFFFFF", 0.35);
            var enKey = Reg.Enter.TryGetValue(cut.Enter, out var edn) ? edn.Key : null;
            var exKey = Reg.Exit.TryGetValue(cut.Exit, out var exd) ? exd.Key : null;
            var solid = (enKey != null && HasPieces(true, enKey) && env.PIn < 1)
                        || (exKey != null && HasPieces(false, exKey) && env.POut > 0);
            var gl = GlyphPts(baseIt);
            var tw = Math.Max(1.5, size * (dark ? 0.05 : 0.06));
            var cw = Math.Max(1, size * 0.018);
            var glow = new JObject
            {
                ["color"] = Rgba(tube, dark ? 0.95 : 0.55),
                ["blur"] = Math.Min(60, size * (dark ? 0.24 : 0.14)),
            };
            BBox? bb = null;
            // 点亮 + 偶发闪烁，走 ≤24Hz 的 step 时钟
            double On(int i, double t0)
            {
                var t = lt - t0;
                if (t < 0) return 0;
                if (t < 0.32) return J.R(s, i, (int)env.Step, 3) < 0.25 + t * 2 ? 1 : 0.12;
                if (B(Pm, "flick") && J.R(s, i, (int)Math.Floor(env.Step / 2), 4) < 0.007) return 0.2;
                return 1;
            }
            for (var i = 0; i < gl.Count; i++)
            {
                var g = gl[i];
                var t0 = 0.04 + J.R(s, i, 5) * 0.28;
                var k = On(i, t0);
                if (k <= 0) continue;
                var common = new JObject
                {
                    ["text"] = g.Ch,
                    ["font"] = font,
                    ["size"] = size,
                    ["x"] = g.X,
                    ["y"] = g.Y,
                    ["mi"] = i * 0.4,
                };
                if (solid)
                {
                    bb = U(bb, JLayouts.MainDraw(env, Merge(common, new JObject
                    {
                        ["color"] = tube,
                        ["shadow"] = glow,
                        ["alpha"] = k,
                    })));
                    continue;
                }
                var r = JLayouts.MainDraw(env, Merge(common, new JObject
                {
                    ["fill"] = false,
                    ["stroke"] = tw,
                    ["strokeColor"] = tube,
                    ["color"] = tube,
                    ["shadow"] = k > 0.5 ? glow : null,
                    ["alpha"] = k,
                }));
                JLayouts.MainDraw(env, Merge(common, new JObject
                {
                    ["fill"] = false,
                    ["stroke"] = cw,
                    ["strokeColor"] = core,
                    ["color"] = core,
                    ["alpha"] = k > 0.5 ? 1 : 0.3,
                    ["ghost"] = false,
                }));
                bb = U(bb, r);
            }
            // 霓虹外框
            var fb = bb ?? BbRect(W / 2 - 10, H / 2 - 10, W / 2 + 10, H / 2 + 10);
            var frame = S(Pm, "frame")!;
            var fk = On(99, 0.22) * (1 - J.E.InCubic(env.POut));
            if (frame != "none" && fk > 0 && bb != null)
            {
                var px = size * 0.55;
                var py = size * 0.42;
                var x0 = fb.X0 - px;
                var x1 = fb.X1 + px;
                var y0 = fb.Y0 - py;
                var y1 = fb.Y1 + py;
                var lw = Math.Max(1.5, size * 0.035);
                var path = new List<double[][]>();
                if (frame == "under") path.Add(new[] { new[] { x0 + px * 0.5, y1 }, new[] { x1 - px * 0.5, y1 } });
                else if (frame == "bracket")
                {
                    var c = size * 0.5;
                    path.Add(new[] { new[] { x0, y0 + c }, new[] { x0, y0 }, new[] { x0 + c, y0 } });
                    path.Add(new[] { new[] { x1 - c, y0 }, new[] { x1, y0 }, new[] { x1, y0 + c } });
                    path.Add(new[] { new[] { x1, y1 - c }, new[] { x1, y1 }, new[] { x1 - c, y1 } });
                    path.Add(new[] { new[] { x0 + c, y1 }, new[] { x0, y1 }, new[] { x0, y1 - c } });
                }
                var glowBlur = D(glow, "blur") * 0.8 * env.Scale;
                if (env.Pass == "main")
                {
                    if (frame == "box") GlowRRect(env, x0, y0, x1 - x0, y1 - y0, size * 0.3, tube, lw, fk, glowBlur);
                    else foreach (var p in path) GlowLine(env, p, tube, lw, fk, glowBlur);
                    if (frame == "box") env.RRect(x0, y0, x1 - x0, y1 - y0, size * 0.3, null, fk, false, core, lw * 0.35);
                    else foreach (var p in path) env.Line(p, core, lw * 0.35, fk, false);
                }
                else
                {
                    if (frame == "box") env.RRect(x0, y0, x1 - x0, y1 - y0, size * 0.3, null, fk, true, tube, lw);
                    else foreach (var p in path) env.Line(p, tube, lw, fk, true);
                }
                if (B(Pm, "sub") && env.Pass == "main")
                {
                    var rom = RomaOf(cut.Text);
                    var st2 = rom ?? (cut.LineText != cut.Text ? cut.LineText : null);
                    if (!string.IsNullOrEmpty(st2))
                    {
                        var fs = J.Clamp(size * 0.2, 12, 34);
                        var c2 = new[] { Col(sc, "accent2"), Col(sc, "fg"), Col(sc, "sub") }
                                     .FirstOrDefault(c => !string.IsNullOrEmpty(c) && c != tube && J.Contrast(c, Col(sc, "bg")) >= 2)
                                 ?? Col(sc, "sub");
                        DrawIt(env, new JObject
                        {
                            ["text"] = st2,
                            ["font"] = MonoF(env),
                            ["size"] = fs,
                            ["track"] = 0.3,
                            ["x"] = (x0 + x1) / 2,
                            ["y"] = y1 + fs * 1.6,
                            ["color"] = c2,
                            ["alpha"] = fk * On(98, 0.4),
                            ["ghost"] = false,
                            ["shadow"] = new JObject { ["color"] = Rgba(c2, 0.9), ["blur"] = fs * 0.6 },
                        });
                    }
                }
            }
            env.Body = bb;
        },
    });

    // ---------------- keycaps（JS L1332-1445） ----------------

    private static void RegisterKeycaps() => Reg0("keycaps", new LayoutDef
    {
        Name = "キーキャップ",
        Tags = new[] { "pop", "graphic" },
        W = 0.6,
        Treat = "safe",
        Fits = n => n >= 1 && n <= 10,
        EnterBias = new JObject
        {
            ["cut"] = 2.5,
            ["pop"] = 1.2,
            ["blur"] = 0.6,
            ["slice"] = 0.3,
            ["wipe"] = 0.3,
            ["stretch"] = 0.3,
            ["assemble"] = 0.3,
        },
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "display", "body")),
            ["style"] = rng.Pick(new[] { "light", "light", "dark" }),
            ["stagger"] = rng.Chance(0.6),
            ["accent"] = rng.Int(0, 20),
            ["legend"] = rng.Chance(0.7),
            ["plate"] = rng.Chance(0.45),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var s = (int)cut.Seed;
            var M = Math.Min(W, H);
            var lt = env.Lt;
            var font = S(Pm, "font")!;
            var chs = SlotsOf(cut.Text);
            var n = chs.Count;
            if (n == 0)
            {
                env.Body = null;
                return;
            }
            var port = W < H;
            var grid = CellRows(chs, W, H, port ? 4 : n > 8 ? 5 : 10, 1.06, M * 0.26, 0.16, B(Pm, "stagger"));
            var cells = grid.Cells;
            var k = grid.K;
            var lightC = J.Lum(Col(sc, "fg")) > J.Lum(Col(sc, "bg")) ? Col(sc, "fg") : Col(sc, "bg");
            var darkC = lightC == Col(sc, "fg") ? Col(sc, "bg") : Col(sc, "fg");
            var d = k * 0.15;
            var rr = k * 0.16;
            var outK = OutK(env);
            var gap = J.Clamp(cut.Dur * 0.38 / n, 0.05, 0.13);
            var acc = n > 2 ? I(Pm, "accent") % n : -1;
            BBox? bb = null;
            if (B(Pm, "plate"))
            {
                var e = InE(env, 0.35) * outK;
                double x0 = 1e9, x1 = -1e9, y0 = 1e9, y1 = -1e9;
                foreach (var c in cells)
                {
                    x0 = Math.Min(x0, c.X - k / 2);
                    x1 = Math.Max(x1, c.X + k / 2);
                    y0 = Math.Min(y0, c.Y - k / 2);
                    y1 = Math.Max(y1, c.Y + k / 2 + d);
                }
                var p = k * 0.22;
                env.RRect(x0 - p, y0 - p, x1 - x0 + p * 2, (y1 - y0 + p * 2) * e, rr * 1.4,
                    J.Mix(Col(sc, "bg"), darkC == Col(sc, "bg") ? lightC : darkC, 0.1), 1, false, Col(sc, "sub"), 1);
            }
            foreach (var c in cells)
            {
                if (c.Ch == " ") continue;
                var ti = 0.14 + c.I * gap;
                var q = J.E.OutBack(J.Clamp((lt - c.I * 0.025) / 0.2), 1.6);
                if (q <= 0 || outK <= 0) continue;
                var gh = GIn(env);
                // 按压：打字时各按一次，之后偶尔随机再压
                double Press(double t) => t < 0 ? 0 : t < 0.05 ? t / 0.05 : t < 0.09 ? 1 : t < 0.22 ? 1 - (t - 0.09) / 0.13 : 0;
                var pr = Press(lt - ti);
                var re = lt - (0.14 + n * gap + 0.3);
                if (re > 0)
                {
                    var bt = BeatOf(env);
                    if (bt.Has && bt.Len > 0.2)
                    {
                        if ((int)(J.H(s, bt.Index, 3) % (uint)n) == c.I) pr = Math.Max(pr, Press(bt.Since));
                    }
                    else
                    {
                        const double P0 = 0.55;
                        var kk = (int)Math.Floor(re / P0);
                        if ((int)(J.H(s, kk, 3) % (uint)n) == c.I) pr = Math.Max(pr, Press(re - kk * P0));
                    }
                }
                var isA = c.I == acc;
                string top, side, leg;
                if (isA)
                {
                    top = PlateCol(sc, Col(sc, "accent"), Col(sc, "ink"));
                    side = J.Mix(top, darkC, 0.4);
                    leg = OnCol(sc, top);
                }
                else if (S(Pm, "style") == "light")
                {
                    top = lightC;
                    side = J.Mix(lightC, darkC, 0.32);
                    leg = darkC;
                }
                else
                {
                    top = J.Mix(darkC, lightC, 0.16);
                    side = J.Mix(darkC, lightC, 0.06);
                    leg = lightC;
                }
                env.Ctx.Save();
                env.Ctx.Translate((float)c.X, (float)c.Y);
                env.Ctx.Scale((float)q, (float)q);
                var dy = pr * d * 0.75;
                env.RRect(-k / 2, -k / 2 + d * 0.35, k, k + d * 0.65, rr, side, outK, gh);
                var ins = k * 0.09;
                env.RRect(-k / 2 + ins * 0.5, -k / 2 + dy, k - ins, k - ins * 0.9, rr * 0.85, J.Mix(top, side, 0.35), outK, false);
                env.RRect(-k / 2 + ins, -k / 2 + dy + ins * 0.35, k - ins * 2, k - ins * 2.1, rr * 0.7, top, outK, false,
                    S(Pm, "style") == "dark" && !isA ? Col(sc, "sub") : null, 1);
                if (B(Pm, "legend") && lt > ti)
                {
                    var rom = J.Romaji(c.Ch);
                    if (!string.IsNullOrEmpty(rom))
                        DrawIt(env, new JObject
                        {
                            ["text"] = rom.ToUpperInvariant(),
                            ["font"] = MonoF(env),
                            ["size"] = k * 0.13,
                            ["align"] = "left",
                            ["x"] = -k / 2 + ins * 1.9,
                            ["y"] = -k / 2 + dy + ins * 1.7,
                            ["color"] = leg,
                            ["alpha"] = 0.7 * outK,
                            ["ghost"] = false,
                        });
                }
                env.Ctx.Restore();
                var it = new JObject
                {
                    ["text"] = c.Ch,
                    ["font"] = font,
                    ["size"] = k * 0.52,
                    ["x"] = c.X,
                    ["y"] = c.Y - k * 0.03 + dy * q,
                    ["color"] = leg,
                    ["noHold"] = PlateHold(env),
                    ["mi"] = MiAt(env, ti),
                };
                if (q < 1)
                {
                    it["size"] = k * 0.52 * q;
                    it["x"] = c.X;
                    it["y"] = c.Y + ((c.Y - k * 0.03 + dy * q) - c.Y) * q;
                }
                bb = U(bb, JLayouts.MainDraw(env, it));
            }
            env.Body = bb;
        },
    });

    // ---------------- bubbles（JS L1447-1516） ----------------

    private static void RegisterBubbles() => Reg0("bubbles", new LayoutDef
    {
        Name = "泡",
        Tags = new[] { "pop", "calm" },
        W = 0.8,
        Treat = "safe",
        Fits = n => n >= 1 && n <= 12,
        EnterBias = new JObject
        {
            ["pop"] = 1.6,
            ["cut"] = 1.5,
            ["blur"] = 1.2,
            ["slice"] = 0.3,
            ["wipe"] = 0.3,
            ["stretch"] = 0.3,
        },
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "display", "body")),
            ["style"] = rng.Pick(new[] { "soap", "soap", "solid", "mixed" }),
            ["rise"] = rng.Range(0.018, 0.035),
            ["wob"] = rng.Range(0.6, 1.2),
            ["motes"] = rng.Chance(0.75),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var s = (int)cut.Seed;
            var M = Math.Min(W, H);
            var lt = env.Lt;
            var font = S(Pm, "font")!;
            var chs = SlotsOf(cut.Text);
            var n = chs.Count;
            if (n == 0)
            {
                env.Body = null;
                return;
            }
            var port = W < H;
            var grid = CellRows(chs, W, H, port ? 4 : 7, 1.1, M * 0.3, 0.12, true);
            var cells = grid.Cells;
            var k = grid.K;
            var outK = OutK(env);
            var popK = 1 + 0.35 * J.E.OutCubic(env.POut);
            var lift = -H * D(Pm, "rise") * env.Ltb;
            // 背景上浮的小气泡
            if (B(Pm, "motes"))
            {
                for (var m = 0; m < 14; m++)
                {
                    var sp = J.Rr(0.05, 0.12, s, m, 1) * H;
                    var per = H * 1.2 / sp;
                    var t = (env.Ltb + J.R(s, m, 2) * per) % per;
                    var y = H * 1.08 - t * sp;
                    var x = J.Rr(0.04, 0.96, s, m, 3) * W + Math.Sin(env.Ltb * 1.7 + m) * M * 0.012;
                    var r = J.Rr(0.006, 0.02, s, m, 4) * M;
                    env.Circle(x, y, r, null, Col(sc, "sub"), Math.Max(1, r * 0.12), 0.45 * outK * InE(env, 0.5), false);
                }
            }
            BBox? bb = null;
            var style = S(Pm, "style");
            var wob = D(Pm, "wob");
            foreach (var c in cells)
            {
                if (c.Ch == " ") continue;
                var kind = J.IsKanji(c.Ch[0]) ? 1.0 : J.IsSmallKana(c.Ch[0]) || J.IsPunct(c.Ch[0]) ? 0.7 : 0.86;
                var R = k * 0.5 * kind * (0.94 + 0.12 * J.R(s, c.I, 5));
                var t0 = 0.04 + c.I * 0.05 + J.R(s, c.I, 6) * 0.08;
                var q0 = J.Clamp((lt - t0) / 0.3);
                if (q0 <= 0) continue;
                var q = J.E.OutBack(q0, 2.2);
                var ph = J.R(s, c.I, 7) * J.TAU;
                var x = c.X + Math.Sin(env.Ltb * 1.6 * wob + ph) * R * 0.1 + J.Rs(s, c.I, 8) * k * 0.1;
                var y = c.Y + lift * (0.7 + 0.6 * J.R(s, c.I, 9)) + Math.Cos(env.Ltb * 1.2 + ph) * R * 0.06
                        + J.Rs(s, c.I, 10) * k * 0.14;
                var solid = style == "solid" || (style == "mixed" && J.R(s, c.I, 11) < 0.4);
                var Rr = R * q * popK;
                var tc = Col(sc, "fg");
                if (solid)
                {
                    var f = c.I % 3 == 1
                        ? PlateCol(sc, Col(sc, "accent"), Col(sc, "ink"))
                        : PlateCol(sc, Col(sc, "ink"), Col(sc, "accent"));
                    env.Circle(x, y, Rr, f, null, 0, outK, GIn(env));
                    tc = OnCol(sc, f);
                }
                else
                {
                    env.Circle(x, y, Rr, Col(sc, "fg"), null, 0, 0.07 * outK, false);
                    env.Circle(x, y, Rr, null, Col(sc, "fg"), Math.Max(1.2, R * 0.035), 0.85 * outK, GIn(env));
                    env.Arc(x, y, Rr * 0.78, 200, 245, Col(sc, "fg"), Math.Max(1.5, R * 0.07), 0.9 * outK, false);
                    env.Circle(x + Rr * 0.52, y - Rr * 0.52, Math.Max(1.5, R * 0.05), Col(sc, "fg"), null, 0, 0.9 * outK, false);
                }
                bb = U(bb, JLayouts.MainDraw(env, new JObject
                {
                    ["text"] = c.Ch,
                    ["font"] = font,
                    ["size"] = R * 1.05 * Math.Min(1, q),
                    ["x"] = x,
                    ["y"] = y,
                    ["color"] = tc,
                    ["noHold"] = PlateHold(env),
                    ["mi"] = MiAt(env, t0),
                }));
            }
            env.Body = bb;
        },
    });

    // ---------------- slotMachine（JS L1518-1648） ----------------

    private static void RegisterSlotMachine() => Reg0("slotMachine", new LayoutDef
    {
        Name = "スロット",
        Tags = new[] { "pop", "glitch" },
        W = 0.6,
        Treat = "safe",
        Fits = n => n >= 1 && n <= 10,
        EnterBias = new JObject
        {
            ["cut"] = 3,
            ["flicker"] = 0.6,
            ["blur"] = 0.5,
            ["slice"] = 0.2,
            ["wipe"] = 0.2,
            ["assemble"] = 0.2,
            ["type"] = 0.2,
        },
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "display")),
            ["style"] = rng.Pick(new[] { "cabinet", "window", "cabinet" }),
            ["v"] = rng.Range(13, 18),
            ["line"] = rng.Chance(0.7),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var s = (int)cut.Seed;
            var M = Math.Min(W, H);
            var lt = env.Lt;
            var font = S(Pm, "font")!;
            var chs = SlotsOf(cut.Text);
            var n = chs.Count;
            if (n == 0)
            {
                env.Body = null;
                return;
            }
            var port = W < H;
            var grid = CellRows(chs, W, H, port ? 5 : 10, 1.34, M * 0.24, 0.1);
            var cells = grid.Cells;
            var k = grid.K;
            var w = k;
            var h = k * 1.34;
            var step = k * 0.92;
            var gsz = k * 0.7;
            var pool = PoolOf(cut);
            var NP = pool.Count;
            var outK = OutK(env);
            var inA = InE(env, 0.25);
            var gap = J.Clamp(cut.Dur * 0.3 / n, 0.08, 0.2);
            var t1 = J.Clamp(cut.Dur * 0.16, 0.2, 0.45);
            var cab = S(Pm, "style") == "cabinet";
            var panel = PlateCol(sc, Col(sc, "ink"), Col(sc, "fg"));
            var winC = cab
                ? OnCol(sc, panel) == Col(sc, "bg") ? Col(sc, "bg") : J.Mix(Col(sc, "bg"), Col(sc, "fg"), 0.06)
                : Col(sc, "bg");
            var glyC = J.Contrast(Col(sc, "fg"), winC) > 2.5 ? Col(sc, "fg") : OnCol(sc, winC);
            double x0 = 1e9, x1 = -1e9, y0 = 1e9, y1 = -1e9;
            foreach (var c in cells)
            {
                x0 = Math.Min(x0, c.X - w / 2);
                x1 = Math.Max(x1, c.X + w / 2);
                y0 = Math.Min(y0, c.Y - h / 2);
                y1 = Math.Max(y1, c.Y + h / 2);
            }
            if (cab)
            {
                var p = k * 0.22;
                env.RRect(x0 - p, y0 - p, x1 - x0 + p * 2, y1 - y0 + p * 2, p * 1.2, panel, outK * inA, GIn(env));
            }
            BBox? bb = null;
            for (var ci = 0; ci < cells.Count; ci++)
            {
                var c = cells[ci];
                if (c.Ch == " ") continue;
                var ts = t1 + c.I * gap;
                var tau = ts - lt;
                const double FIN = 1000;
                var p = tau > 0
                    ? FIN - D(Pm, "v") * tau
                    : FIN + 0.22 * Math.Sin(-tau * 30) * Math.Exp(tau / 0.08);
                var wx = c.X - w / 2;
                var wy = c.Y - h / 2;
                env.Rect(wx, wy, w, h * inA, winC, outK, false);
                env.Ctx.Save();
                env.Ctx.ClipRect(new SKRect((float)wx, (float)wy, (float)(wx + w), (float)(wy + h)));
                var fast = tau > 0.05;
                var j0 = (int)Math.Floor(p) - 1;
                var j1 = (int)Math.Ceiling(p) + 1;
                for (var j = j0; j <= j1; j++)
                {
                    var y = c.Y + (j - p) * step;
                    if (j == FIN && tau <= 0) continue;
                    var ch = j == FIN ? c.Ch : pool[(int)(J.H(s, c.I, j) % (uint)NP)];
                    if (fast)
                    {
                        // 廉价运动模糊：同一个（有缓存的）字沿滚轮拖影
                        foreach (var o in new[] { -0.16, 0.16 })
                            DrawIt(env, new JObject
                            {
                                ["text"] = ch,
                                ["font"] = font,
                                ["size"] = gsz,
                                ["x"] = c.X,
                                ["y"] = y + o * step,
                                ["color"] = glyC,
                                ["alpha"] = 0.22 * outK,
                                ["ghost"] = false,
                            });
                        DrawIt(env, new JObject
                        {
                            ["text"] = ch,
                            ["font"] = font,
                            ["size"] = gsz,
                            ["x"] = c.X,
                            ["y"] = y,
                            ["color"] = glyC,
                            ["alpha"] = 0.5 * outK,
                            ["ghost"] = false,
                        });
                    }
                    else
                        DrawIt(env, new JObject
                        {
                            ["text"] = ch,
                            ["font"] = font,
                            ["size"] = gsz,
                            ["x"] = c.X,
                            ["y"] = y,
                            ["color"] = glyC,
                            ["alpha"] = 0.9 * outK,
                            ["ghost"] = false,
                        });
                }
                if (tau <= 0)
                    bb = U(bb, JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = c.Ch,
                        ["font"] = font,
                        ["size"] = gsz,
                        ["x"] = c.X,
                        ["y"] = c.Y + (FIN - p) * step,
                        ["color"] = glyC,
                        ["noHold"] = PlateHold(env),
                        ["mi"] = MiAt(env, ts),
                    }));
                env.Ctx.Restore();
                if (env.Pass == "main")
                {
                    // 滚筒上下压暗（几何+色相同的渐变每帧重建一次，语义等价；JS 侧是缓存）
                    using var shader = SKShader.CreateLinearGradient(
                        new SKPoint(0, (float)wy), new SKPoint(0, (float)(wy + h)),
                        new[]
                        {
                            Env.SkColor(winC, 0.95 * outK), Env.SkColor(winC, 0),
                            Env.SkColor(winC, 0), Env.SkColor(winC, 0.95 * outK),
                        },
                        new[] { 0f, 0.3f, 0.7f, 1f }, SKShaderTileMode.Clamp);
                    using var gp = new SKPaint { IsAntialias = true, Shader = shader };
                    env.Ctx.DrawRect(new SKRect((float)wx, (float)wy, (float)(wx + w), (float)(wy + h)), gp);
                }
                env.RRect(wx, wy, w, h, k * 0.06, null, outK * inA, false,
                    cab ? J.Mix(panel, winC, 0.5) : Col(sc, "fg"), Math.Max(1.5, k * 0.02));
            }
            if (B(Pm, "line"))
            {
                var rowsY = cells.Select(c => c.Y).Distinct().ToList();
                foreach (var y in rowsY)
                {
                    var tri = k * 0.1;
                    var xa = x0 - k * 0.12;
                    var xb = x1 + k * 0.12;
                    env.Line(new[] { new[] { xa, y }, new[] { J.Lerp(xa, xb, inA), y } }, Col(sc, "accent"),
                        Math.Max(1.2, k * 0.012), 0.7 * outK, false);
                    env.Poly(new[]
                    {
                        new[] { xa - tri * 1.6, y - tri }, new[] { xa, y }, new[] { xa - tri * 1.6, y + tri },
                    }, Col(sc, "accent"), outK * inA, false);
                    env.Poly(new[]
                    {
                        new[] { xb + tri * 1.6, y - tri }, new[] { xb, y }, new[] { xb + tri * 1.6, y + tri },
                    }, Col(sc, "accent"), outK * inA, false);
                }
            }
            env.Body = bb ?? BbRect(x0, y0, x1, y1);
        },
    });

    // ---------------- flipBoard（JS L1650-1763） ----------------

    private static void RegisterFlipBoard() => Reg0("flipBoard", new LayoutDef
    {
        Name = "パタパタ",
        Tags = new[] { "graphic", "editorial" },
        W = 0.7,
        Treat = "safe",
        Fits = n => n >= 1 && n <= 12,
        EnterBias = new JObject
        {
            ["cut"] = 3,
            ["flicker"] = 0.5,
            ["blur"] = 0.4,
            ["slice"] = 0.2,
            ["wipe"] = 0.3,
            ["assemble"] = 0.2,
            ["type"] = 0.3,
        },
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "display", "body")),
            ["header"] = rng.Chance(0.65),
            ["flips"] = rng.Int(3, 5),
            ["style"] = rng.Pick(new[] { "ink", "ink", "fg" }),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var s = (int)cut.Seed;
            var M = Math.Min(W, H);
            var lt = env.Lt;
            var font = S(Pm, "font")!;
            var chs = JLayouts.CodePoints(cut.Text.Trim()).Where(c => c != "　").ToList();
            var n = chs.Count;
            if (n == 0)
            {
                env.Body = null;
                return;
            }
            var port = W < H;
            var grid = CellRows(chs, W, H, port ? 5 : 12, 1.32, M * 0.24, 0.08);
            var cells = grid.Cells;
            var k = grid.K;
            var w = k;
            var h = k * 1.32;
            var gsz = k * 0.78;
            var panel = PlateCol(sc, S(Pm, "style") == "fg" ? Col(sc, "fg") : Col(sc, "ink"),
                S(Pm, "style") == "fg" ? Col(sc, "ink") : Col(sc, "fg"));
            var flap = J.Mix(panel, Col(sc, "bg"), 0.08);
            var gc = OnCol(sc, panel);
            var pool = PoolOf(cut);
            var NP = pool.Count;
            const double fd = 0.065;
            const double t0 = 0.06;
            var outK = OutK(env);
            var inA = InE(env, 0.2);
            var lw = Math.Max(1.5, k * 0.018);
            BBox? bb = null;
            double x0 = 1e9, x1 = -1e9, y0 = 1e9;
            foreach (var c in cells)
            {
                x0 = Math.Min(x0, c.X - w / 2);
                x1 = Math.Max(x1, c.X + w / 2);
                y0 = Math.Min(y0, c.Y - h / 2);
                var q = J.E.OutCubic(J.Clamp((lt - c.I * 0.02) / 0.18)) * outK;
                if (q <= 0) continue;
                var wx = c.X - w / 2;
                var wy = c.Y - h / 2;
                env.RRect(wx, wy, w, h, k * 0.07, panel, q, GIn(env));
                var F = I(Pm, "flips") + c.I % 3 + (int)Math.Floor(c.I * 0.7);
                var settle = t0 + F * fd;
                var space = c.Ch == " ";
                string ChAt(int m) => m <= 0 ? "" : m >= F ? c.Ch : pool[(int)(J.H(s, c.I, m) % (uint)NP)];
                var u = (lt - t0) / fd;
                void Half(string ch, bool top, double sy)
                {
                    if (string.IsNullOrEmpty(ch) || ch == " " || sy <= 0.01) return;
                    DrawIt(env, new JObject
                    {
                        ["text"] = ch,
                        ["font"] = font,
                        ["size"] = gsz,
                        ["x"] = c.X,
                        ["y"] = c.Y,
                        ["color"] = gc,
                        ["ghost"] = false,
                        ["alpha"] = q,
                    }, (_, _, _) => new CharXf
                    {
                        ClipY = top ? new[] { -0.75, 0.0 } : new[] { 0.0, 0.75 },
                        Sy = sy,
                    });
                }
                if (lt >= settle)
                {
                    if (!space)
                        bb = U(bb, JLayouts.MainDraw(env, new JObject
                        {
                            ["text"] = c.Ch,
                            ["font"] = font,
                            ["size"] = gsz,
                            ["x"] = c.X,
                            ["y"] = c.Y,
                            ["color"] = gc,
                            ["alpha"] = q,
                            ["noHold"] = PlateHold(env),
                            ["mi"] = MiAt(env, settle),
                        }));
                }
                else if (u > 0)
                {
                    var m = (int)Math.Floor(u);
                    var f = u - m;
                    var A = ChAt(m);
                    var B2 = ChAt(m + 1);
                    Half(B2, true, 1);
                    Half(A, false, 1);
                    // 正在下落的翻片（压扁比例取整到 1/6，压扁的字形才好命中缓存）
                    var sy = Math.Round(Math.Abs(Math.Cos(f * Math.PI)) * 6) / 6;
                    if (f < 0.5)
                    {
                        env.Rect(wx, c.Y - h / 2 * sy, w, h / 2 * sy, flap, q, false);
                        Half(A, true, sy);
                        env.Rect(wx, c.Y - h / 2 * sy, w, h / 2 * sy, Col(sc, "bg"), q * f * 0.5, false);
                    }
                    else
                    {
                        env.Rect(wx, c.Y, w, h / 2 * sy, flap, q, false);
                        Half(B2, false, sy);
                        env.Rect(wx, c.Y, w, h / 2 * sy, Col(sc, "bg"), q * (1 - f) * 0.5, false);
                    }
                }
                env.Rect(wx, c.Y - lw / 2, w, lw, Col(sc, "bg"), q, false);
                env.Rect(wx - lw * 0.6, c.Y - h * 0.07, lw * 1.2, h * 0.14, J.Mix(panel, Col(sc, "bg"), 0.5), q, false);
                env.Rect(wx + w - lw * 0.6, c.Y - h * 0.07, lw * 1.2, h * 0.14, J.Mix(panel, Col(sc, "bg"), 0.5), q, false);
            }
            if (B(Pm, "header"))
            {
                var fs = J.Clamp(k * 0.2, 12, 30);
                var lab = "LINE " + (Math.Max(0, cut.Line) + 1).ToString("D2", CultureInfo.InvariantCulture);
                var tm = J.FmtTime(cut.Start);
                var a = inA * outK;
                DrawIt(env, new JObject
                {
                    ["text"] = lab,
                    ["font"] = MonoF(env),
                    ["size"] = fs,
                    ["track"] = 0.2,
                    ["align"] = "left",
                    ["x"] = x0,
                    ["y"] = y0 - fs * 1.4,
                    ["color"] = Col(sc, "sub"),
                    ["alpha"] = a,
                    ["ghost"] = false,
                });
                DrawIt(env, new JObject
                {
                    ["text"] = tm,
                    ["font"] = MonoF(env),
                    ["size"] = fs,
                    ["track"] = 0.2,
                    ["align"] = "right",
                    ["x"] = x1,
                    ["y"] = y0 - fs * 1.4,
                    ["color"] = Col(sc, "accent"),
                    ["alpha"] = a,
                    ["ghost"] = false,
                });
            }
            env.Body = bb;
        },
    });

    // ---------------- credits（JS L1765-1885） ----------------

    private static void RegisterCredits() => Reg0("credits", new LayoutDef
    {
        Name = "エンドロール",
        Tags = new[] { "calm", "editorial", "emotional" },
        W = 1,
        Fits = n => n >= 1 && n <= 16,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "serif", "display")),
            ["fc"] = rng.Pick(FontsOf(st, "serif", "body")),
            ["variant"] = rng.Pick(new[] { "center", "center", "side", "single" }),
            ["speed"] = rng.Range(0.035, 0.06),
            ["off"] = rng.Range(0, 10),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var M = Math.Min(W, H);
            var port = W < H;
            var variant0 = S(Pm, "variant")!;
            var variant = port && variant0 == "side" ? "center" : variant0;
            var side = variant == "side";
            var font = S(Pm, "font")!;
            var fc = S(Pm, "fc")!;
            var mt = MainLines(cut.Text, W, H, side ? 6 : 10, 5);
            var size = Math.Min(JLayouts.FitSize(mt, font, side ? W * 0.44 : W * 0.72, H * 0.26,
                new JObject { ["track"] = 0.06, ["lead"] = 1.2 }), H * 0.16);
            var mx = side ? W * 0.3 : W / 2;
            // 职员表条目
            var txt = Clean(cut.Text);
            var rom = RomaOf(txt);
            var vals = new List<(string R, string V)>();
            var seen = new HashSet<string>();
            void Add(string r, string? v0)
            {
                var v = (v0 ?? "").Trim();
                if (v.Length > 0 && !seen.Contains(v))
                {
                    seen.Add(v);
                    vals.Add((r, v));
                }
            }
            var line = string.IsNullOrEmpty(cut.LineText) ? cut.Text : cut.LineText;
            Add("詞", line);
            var chunks = Planner.ChunkText(line);
            for (var i = 0; i < chunks.Count; i++) Add(i == 0 ? "語" : "", chunks[i]);
            foreach (var g in Planner.Segments(line))
                if (JLayouts.GlyphCount(g) >= 2) Add("", g);
            if (rom != null) Add("READING", rom);
            if (!string.IsNullOrEmpty(cut.Note)) Add("NOTE", cut.Note);
            Add("LINE", (Math.Max(0, cut.Line) + 1).ToString("D2", CultureInfo.InvariantCulture));
            Add("TIME", J.FmtTime(cut.Start));
            var fs = J.Clamp(M * 0.026, 14, 36);
            var rowH = fs * 2.3;
            var block = vals.Count * rowH + rowH * 2;
            var scroll = (env.Ltb + D(Pm, "off")) * D(Pm, "speed") * H;
            var a0 = InE(env, 0.6) * OutK(env);
            var cx = side ? W * 0.74 : W / 2;
            var bandH = side ? 0.0 : size * mt.Split('\n').Length * 1.25 / 2 + fs * 2.2;
            var lw = Math.Max(1, M * 0.0012);
            var mono = MonoF(env);
            var yStart = H * 1.05 - (scroll % block) - block * Math.Ceiling(H * 1.1 / block);
            var rep = 0;
            for (var y = yStart; y < H * 1.05 && rep < 12; y += block, rep++)
            {
                for (var i = 0; i < vals.Count; i++)
                {
                    var (r, v) = vals[i];
                    var yy = y + i * rowH;
                    if (yy < -rowH || yy > H + rowH) continue;
                    var edge = J.Smooth(H * 0.02, H * 0.14, yy) * J.Smooth(H * 0.98, H * 0.86, yy);
                    var band = side ? 1.0 : J.Smooth(bandH, bandH + fs * 2, Math.Abs(yy - H / 2));
                    var a = a0 * edge * band;
                    if (a < 0.01) continue;
                    if (variant == "single")
                    {
                        if (r.Length > 0)
                            DrawIt(env, new JObject
                            {
                                ["text"] = r,
                                ["font"] = mono,
                                ["size"] = fs * 0.62,
                                ["track"] = 0.3,
                                ["x"] = cx,
                                ["y"] = yy - fs * 0.95,
                                ["color"] = Col(sc, "sub"),
                                ["alpha"] = a * 0.8,
                                ["ghost"] = false,
                            });
                        DrawIt(env, new JObject
                        {
                            ["text"] = v,
                            ["font"] = fc,
                            ["size"] = fs,
                            ["track"] = 0.12,
                            ["x"] = cx,
                            ["y"] = yy,
                            ["color"] = Col(sc, "fg"),
                            ["alpha"] = a * 0.85,
                            ["ghost"] = false,
                        });
                    }
                    else
                    {
                        var g = fs * 0.9;
                        if (r.Length > 0)
                            DrawIt(env, new JObject
                            {
                                ["text"] = r,
                                ["font"] = Regex.IsMatch(r, "[A-Z]") ? mono : fc,
                                ["size"] = fs * 0.72,
                                ["track"] = 0.25,
                                ["align"] = "right",
                                ["x"] = cx - g,
                                ["y"] = yy,
                                ["color"] = Col(sc, "sub"),
                                ["alpha"] = a * 0.85,
                                ["ghost"] = false,
                            });
                        DrawIt(env, new JObject
                        {
                            ["text"] = v,
                            ["font"] = fc,
                            ["size"] = fs,
                            ["track"] = 0.1,
                            ["align"] = "left",
                            ["x"] = cx + g,
                            ["y"] = yy,
                            ["color"] = Col(sc, "fg"),
                            ["alpha"] = a * 0.85,
                            ["ghost"] = false,
                        });
                    }
                }
            }
            if (side)
                env.Line(new[]
                {
                    new[] { W * 0.52, H * 0.2 },
                    new[] { W * 0.52, H * 0.2 + H * 0.6 * InE(env, 0.8) },
                }, Col(sc, "sub"), lw, 0.5 * OutK(env), false);
            env.Body = JLayouts.MainDraw(env, new JObject
            {
                ["text"] = mt,
                ["font"] = font,
                ["size"] = size,
                ["x"] = mx,
                ["y"] = H / 2,
                ["track"] = 0.06,
                ["lead"] = 1.2,
                ["color"] = Col(sc, "fg"),
            });
        },
    });

    // ---------------- zoomRepeat（JS L1887-1967） ----------------

    private static void RegisterZoomRepeat() => Reg0("zoomRepeat", new LayoutDef
    {
        Name = "連続拡大",
        Tags = new[] { "glitch", "emotional", "graphic" },
        W = 0.9,
        Emph = 1.5,
        Busy = true,
        Fits = n => n >= 1 && n <= 12,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
            ["dir"] = rng.Pick(new[] { 1, 1, -1 }),
            ["style"] = rng.Pick(new[] { "alt", "alt", "outline", "fill" }),
            ["twist"] = rng.Chance(0.35) ? rng.Range(3, 7) * rng.Pick(new[] { 1, -1 }) : 0,
            ["q"] = rng.Range(1.38, 1.6),
            ["speed"] = rng.Range(0.35, 0.6),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var M = Math.Min(W, H);
            var font = S(Pm, "font")!;
            var mt = MainLines(cut.Text, W, H, 9, 5);
            var size = Math.Min(JLayouts.FitSize(mt, font, W * 0.64, H * 0.3,
                new JObject { ["track"] = 0.03, ["lead"] = 1.1 }), H * 0.2);
            var mm = JLayouts.Measure(new JObject { ["text"] = mt, ["font"] = font, ["size"] = size, ["track"] = 0.03, ["lead"] = 1.1 });
            var q = D(Pm, "q");
            var Lmax = Math.Log(Math.Max(W / mm.W, H / mm.H) * 2.6) / Math.Log(q);
            var e = InE(env, 0.5);
            var outK = OutK(env);
            var dir = D(Pm, "dir");
            var ph0 = env.Ltb * D(Pm, "speed") * dir + (1 - e) * 1.5 * dir;
            var ph = ((ph0 % 1) + 1) % 1;
            var baseN = (int)Math.Floor(ph0);
            var K = Math.Min(9, (int)Math.Ceiling(Lmax) + 1);
            var style = S(Pm, "style");
            for (var k = K; k >= 0; k--)
            {
                var L = k + ph;
                if (L < 0.3 || L > Lmax) continue;
                var fs = size * Math.Pow(q, L);
                var a = J.Smooth(0.3, 0.95, L) * (1 - J.Smooth(Lmax * 0.35, Lmax, L)) * e * outK * 0.7;
                if (a < 0.02) continue;
                var idx = k - baseN;
                var outline = style == "outline" || (style == "alt" && (idx % 2 + 2) % 2 == 0);
                var it = new JObject
                {
                    ["text"] = mt,
                    ["font"] = font,
                    ["size"] = fs,
                    ["x"] = W / 2,
                    ["y"] = H / 2,
                    ["track"] = 0.03,
                    ["lead"] = 1.1,
                    ["rot"] = D(Pm, "twist") * L,
                    ["ghost"] = false,
                };
                if (outline)
                {
                    it["fill"] = false;
                    it["stroke"] = Math.Max(1.2, fs * 0.01);
                    it["strokeColor"] = Col(sc, "sub");
                    it["alpha"] = a * 0.75;
                }
                else
                {
                    it["color"] = J.Mix(Col(sc, "bg"), Col(sc, "sub"), 0.16);
                    it["alpha"] = a;
                }
                DrawIt(env, it);
            }
            JLayouts.MainDraw(env, new JObject
            {
                ["text"] = mt,
                ["font"] = font,
                ["size"] = size,
                ["x"] = W / 2,
                ["y"] = H / 2,
                ["track"] = 0.03,
                ["lead"] = 1.1,
                ["fill"] = false,
                ["stroke"] = size * 0.2,
                ["strokeColor"] = Col(sc, "bg"),
                ["ghost"] = false,
                ["plain"] = true,
            });
            env.Body = JLayouts.MainDraw(env, new JObject
            {
                ["text"] = mt,
                ["font"] = font,
                ["size"] = size,
                ["x"] = W / 2,
                ["y"] = H / 2,
                ["track"] = 0.03,
                ["lead"] = 1.1,
                ["color"] = Col(sc, "fg"),
            });
        },
    });

    // ---------------- splitHalves（JS L1969-2032） ----------------

    private static void RegisterSplitHalves() => Reg0("splitHalves", new LayoutDef
    {
        Name = "上下割り",
        Tags = new[] { "graphic", "glitch", "editorial" },
        W = 1,
        Emph = 1.2,
        Fits = n => n >= 1 && n <= 14,
        EnterBias = new JObject { ["cut"] = 1.8, ["slice"] = 0.3, ["wipe"] = 0.6 },
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
            ["variant"] = rng.Pick(new[] { "slide", "slide", "shear", "duo" }),
            ["dir"] = rng.Pick(new[] { 1, -1 }),
            ["line"] = rng.Pick(new[] { "full", "short" }),
            ["gap"] = rng.Pick(new[] { 0.07, 0.1, 0.13 }),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var M = Math.Min(W, H);
            var lt = env.Lt;
            var font = S(Pm, "font")!;
            var mt = MainLines(cut.Text, W, H, 11, 5);
            var lines = mt.Split('\n');
            var size = Math.Min(JLayouts.FitSize(mt, font, W * 0.84, H * 0.5,
                new JObject { ["track"] = 0.04, ["lead"] = 1.3 }), H * 0.24);
            var variant = S(Pm, "variant");
            var gap = D(Pm, "gap");
            var lead = size * 1.3 + gap * size;
            var offD = W * 0.32; // JS 的 D：两半初始错位量
            var e = J.E.OutExpo(J.Clamp((lt - 0.04) / 0.7));
            var ex = J.E.InExpo(env.POut);
            var lw = Math.Max(1.5, size * 0.016);
            BBox? bb = null;
            for (var li = 0; li < lines.Length; li++)
            {
                var ln = lines[li];
                var y = H / 2 + (li - (lines.Length - 1) / 2.0) * lead;
                var m = JLayouts.Measure(new JObject { ["text"] = ln, ["font"] = font, ["size"] = size, ["track"] = 0.04 });
                var dirL = D(Pm, "dir") * (li % 2 != 0 ? -1 : 1);
                var off = offD * (1 - e)
                          + (variant == "shear" ? size * 0.14 * e * (1 + 0.25 * Math.Sin(env.Ltb * 1.7)) : 0)
                          + W * 0.4 * ex;
                var g = gap * size / 2;
                for (var hSel = 0; hSel < 2; hSel++)
                {
                    var dx = (hSel != 0 ? -1 : 1) * off * dirL;
                    env.Ctx.Save();
                    env.Ctx.ClipRect(hSel == 0
                        ? new SKRect((float)-W, (float)(y - size * 2 - g), (float)(W * 2), (float)(y - g))
                        : new SKRect((float)-W, (float)(y + g), (float)(W * 2), (float)(y + g + size * 2)));
                    var it = new JObject
                    {
                        ["text"] = ln,
                        ["font"] = font,
                        ["size"] = size,
                        ["x"] = W / 2 + dx,
                        ["y"] = y + (hSel != 0 ? g : -g),
                        ["track"] = 0.04,
                        ["color"] = Col(sc, "fg"),
                        ["mi"] = li * 2 + hSel,
                    };
                    if (variant == "duo" && hSel != 0) it["color"] = Col(sc, "accent");
                    var r = JLayouts.MainDraw(env, it);
                    env.Ctx.Restore();
                    if (r != null) bb = U(bb, r);
                }
                // 切割线
                var le = J.E.OutExpo(J.Clamp((lt - 0.1) / 0.6)) * (1 - J.E.InCubic(env.POut));
                if (le > 0)
                {
                    var half = S(Pm, "line") == "full" ? W * 0.5 : m.W / 2 + size * 0.7;
                    env.Line(new[]
                    {
                        new[] { W / 2 - half * le, y }, new[] { W / 2 + half * le, y },
                    }, Col(sc, "accent"), lw, 1, true);
                    if (S(Pm, "line") == "short")
                    {
                        env.Circle(W / 2 - half * le, y, lw * 1.6, Col(sc, "accent"), null, 0, 1, false);
                        env.Circle(W / 2 + half * le, y, lw * 1.6, Col(sc, "accent"), null, 0, 1, false);
                    }
                }
            }
            env.Body = bb;
        },
    });

    // ---------------- columnsBig（JS L2034-2127） ----------------

    private static void RegisterColumnsBig() => Reg0("columnsBig", new LayoutDef
    {
        Name = "大小縦組",
        Tags = new[] { "editorial", "calm", "emotional" },
        W = 1,
        Portrait = 1.4,
        Fits = n => n >= 1 && n <= 10,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
            ["fs"] = rng.Pick(FontsOf(st, "serif", "body")),
            ["side"] = rng.Pick(new[] { "left", "left", "right" }),
            ["rule"] = rng.Chance(0.7),
            ["mark"] = rng.Pick(new[] { "bar", "dot", "none" }),
            ["off"] = rng.Range(-0.05, 0.05),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var M = Math.Min(W, H);
            var lt = env.Lt;
            var font = S(Pm, "font")!;
            var fsFont = S(Pm, "fs")!;
            var port = W < H;
            var txt = Regex.Replace(cut.Text.Trim(), @"[\s\u3000]+", " ");
            var size = Math.Min(JLayouts.FitSize(txt, font, port ? W * 0.44 : W * 0.3, H * 0.84,
                new JObject { ["vertical"] = true, ["track"] = 0.02 }), H * 0.42);
            var colH = JLayouts.Measure(new JObject { ["text"] = txt, ["font"] = font, ["size"] = size, ["vertical"] = true, ["track"] = 0.02 }).H;
            var left = S(Pm, "side") == "left";
            var hx = W * (port ? left ? 0.62 : 0.38 : left ? 0.58 : 0.4) + D(Pm, "off") * W;
            var top = H / 2 - colH / 2;
            var bb = JLayouts.MainDraw(env, new JObject
            {
                ["text"] = txt,
                ["font"] = font,
                ["size"] = size,
                ["x"] = hx,
                ["y"] = H / 2,
                ["vertical"] = true,
                ["track"] = 0.02,
                ["color"] = Col(sc, "fg"),
            });
            var fs = J.Clamp(M * 0.031, 14, 44);
            var perCol = (int)Math.Max(4, Math.Floor(colH * 0.92 / (fs * 1.08)));
            var line = Regex.Replace(!string.IsNullOrEmpty(cut.LineText) ? cut.LineText : cut.Text, @"\s+", "");
            var lineT = SplitL(line, perCol);
            var rom = RomaOf(txt);
            var sub2 = rom ?? (!string.IsNullOrEmpty(cut.Note)
                ? cut.Note!
                : "No." + (Math.Max(0, cut.Line) + 1).ToString("D2", CultureInfo.InvariantCulture) + " " + J.FmtTime(cut.Start));
            var sgn = left ? -1 : 1;
            var gap = size * 0.5 + fs * 1.9;
            var x1 = hx + sgn * gap;
            var nL = lineT.Split('\n').Length;
            var x2 = x1 + sgn * (nL * fs * 1.7 + fs * 0.6);
            JAnim.CharFn Reveal(double t0, int cnt)
            {
                var k = (int)Math.Floor(J.Clamp((lt - t0) / 0.7) * (cnt + 0.99));
                return (i, _, _) => i >= k ? new CharXf { Hide = true } : null;
            }
            var outK = OutK(env);
            var c1 = JLayouts.GlyphCount(lineT);
            // 注释块：竖排多列文本，从第一列起向左生长
            DrawIt(env, new JObject
            {
                ["text"] = lineT,
                ["font"] = fsFont,
                ["size"] = fs,
                ["vertical"] = true,
                ["align"] = "left",
                ["lead"] = 1.7,
                ["track"] = 0.06,
                ["x"] = x1 + sgn * (nL - 1) * fs * 0.85,
                ["y"] = top,
                ["color"] = Col(sc, "fg"),
                ["alpha"] = 0.9 * outK,
                ["ghost"] = false,
            }, Reveal(0.18, c1));
            DrawIt(env, new JObject
            {
                ["text"] = sub2,
                ["font"] = rom != null ? MonoF(env) : fsFont,
                ["size"] = fs * 0.72,
                ["vertical"] = true,
                ["align"] = "left",
                ["track"] = 0.18,
                ["x"] = x2,
                ["y"] = top,
                ["color"] = Col(sc, "sub"),
                ["alpha"] = 0.9 * outK,
                ["ghost"] = false,
            }, Reveal(0.35, JLayouts.GlyphCount(sub2) + 2));
            var lw = Math.Max(1, M * 0.0014);
            if (B(Pm, "rule"))
            {
                var rx = hx + sgn * (size * 0.5 + fs * 0.85);
                env.Line(new[]
                {
                    new[] { rx, top },
                    new[] { rx, top + colH * J.E.OutCubic(J.Clamp((lt - 0.1) / 0.6)) },
                }, Col(sc, "sub"), lw, 0.7 * outK, false);
            }
            if (S(Pm, "mark") != "none")
            {
                var q = J.E.OutBack(J.Clamp((lt - 0.15) / 0.3), 2) * outK;
                if (S(Pm, "mark") == "bar")
                    env.Rect(hx - size * 0.5, top - size * 0.28, size * q, Math.Max(3, size * 0.06), Col(sc, "accent"), 1, true);
                else
                    env.Circle(hx + (size * 0.5 + fs * 0.85) * sgn, top - fs * 0.9, fs * 0.28 * q, Col(sc, "accent"), null, 0, 1, true);
            }
            env.Body = bb;
        },
    });

    // ---------------- circleWords（JS L2129-2208） ----------------

    private static void RegisterCircleWords() => Reg0("circleWords", new LayoutDef
    {
        Name = "同心円",
        Tags = new[] { "graphic", "calm", "editorial" },
        W = 1,
        Fits = n => n >= 1 && n <= 14,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
            ["fr"] = rng.Pick(FontsOf(st, "body", "serif")),
            ["rings"] = rng.Pick(new[] { 2, 3, 3 }),
            ["speed"] = rng.Range(7, 13),
            ["dir"] = rng.Pick(new[] { 1, -1 }),
            ["ticks"] = rng.Chance(0.6),
            ["guides"] = rng.Chance(0.7),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var s = (int)cut.Seed;
            var M = Math.Min(W, H);
            var lt = env.Lt;
            var font = S(Pm, "font")!;
            var fr = S(Pm, "fr")!;
            var cx = W / 2;
            var cy = H / 2;
            var Rmax = Math.Min(W, H) * 0.46;
            var gapR = M * 0.072;
            var rings = I(Pm, "rings");
            var R0 = Rmax - gapR * (rings - 1);
            var txt = Regex.Replace(cut.Text.Trim(), @"[\s\u3000]+", " ");
            var nGlyph = JLayouts.GlyphCount(txt);
            var mt = nGlyph <= 4 ? txt : SplitL(txt, (int)Math.Ceiling(nGlyph / (double)Math.Ceiling(nGlyph / 5.0)));
            var size = Math.Min(JLayouts.FitSize(mt, font, R0 * 1.45, R0 * 1.05,
                new JObject { ["track"] = 0.03, ["lead"] = 1.1 }), R0 * 0.55);
            var rom = RomaOf(txt);
            var units = new List<string>
            {
                Regex.Replace(!string.IsNullOrEmpty(cut.LineText) ? cut.LineText : cut.Text, @"\s+", " ").Trim() + "　✦　",
                (rom ?? (cut.Words is { Length: > 0 } ? string.Join(" / ", cut.Words) : txt)) + "  —  ",
                Clean(txt) + "・",
            };
            var outK = OutK(env);
            var lw = Math.Max(1, M * 0.0014);
            var dirK = D(Pm, "dir");
            var spd = D(Pm, "speed");
            var guides = B(Pm, "guides");
            for (var k = 0; k < rings; k++)
            {
                var e = J.E.OutExpo(J.Clamp((lt - k * 0.08) / 0.7));
                if (e <= 0) continue;
                var R = (R0 + k * gapR) * (0.9 + 0.1 * e);
                var f = J.Clamp(gapR * 0.46 * (k == 1 ? 0.8 : 1), 10, 52);
                var fnt = k == 1 && rom != null ? MonoF(env) : fr;
                var unit = JLayouts.CodePoints(units[k % units.Count]);
                var uAdv = 0.0;
                foreach (var ch in unit) uAdv += JText.Metrics.Adv(fnt, ch) * f * 1.08;
                var reps = Math.Max(1, Math.Min(12, (int)Math.Round(J.TAU * R / Math.Max(1, uAdv))));
                var chars = new List<string>();
                for (var r = 0; r < reps; r++) chars.AddRange(unit);
                if (chars.Count > 160) chars.RemoveRange(160, chars.Count - 160);
                var tot = chars.Sum(ch => JText.Metrics.Adv(fnt, ch) * f * 1.08);
                var kk = J.TAU * R / Math.Max(1, tot);
                var a0 = (env.Ltb * spd * (k % 2 != 0 ? -1 : 1) * dirK + J.R(s, k, 3) * 360) * J.DEG;
                var acc = 0.0;
                var pos = new List<double>();
                foreach (var ch in chars)
                {
                    var ad = JText.Metrics.Adv(fnt, ch) * f * 1.08 * kk;
                    pos.Add(a0 + (acc + ad / 2) / R);
                    acc += ad;
                }
                var colRing = k == 0 ? Col(sc, "fg") : Col(sc, "sub");
                PathText(env, chars, fnt, f, i =>
                {
                    var a = pos[i];
                    return new Place
                    {
                        X = cx + Math.Sin(a) * R,
                        Y = cy - Math.Cos(a) * R,
                        Rot = a / J.DEG,
                        A = 0.9 * e * outK,
                        Color = chars[i] == "✦" ? Col(sc, "accent") : colRing,
                    };
                });
                if (guides)
                {
                    env.Circle(cx, cy, R + gapR * 0.5, null, Col(sc, "sub"), lw, 0.35 * e * outK, false);
                    if (k == 0) env.Circle(cx, cy, R - gapR * 0.5, null, Col(sc, "sub"), lw, 0.35 * e * outK, false);
                }
            }
            if (B(Pm, "ticks"))
            {
                var Rt = R0 + (rings - 1) * gapR + gapR * 0.5;
                var e = InE(env, 0.9) * outK;
                const int m = 72;
                for (var i = 0; i < m; i++)
                {
                    if (i / (double)m > e) break;
                    var a = (i / (double)m * 360 - env.Ltb * spd * 0.5 * dirK) * J.DEG;
                    var L = i % 6 == 0 ? gapR * 0.3 : gapR * 0.14;
                    env.Line(new[]
                    {
                        new[] { cx + Math.Sin(a) * Rt, cy - Math.Cos(a) * Rt },
                        new[] { cx + Math.Sin(a) * (Rt + L), cy - Math.Cos(a) * (Rt + L) },
                    }, i % 18 == 0 ? Col(sc, "accent") : Col(sc, "sub"), lw, 0.6, false);
                }
            }
            env.Body = JLayouts.MainDraw(env, new JObject
            {
                ["text"] = mt,
                ["font"] = font,
                ["size"] = size,
                ["x"] = cx,
                ["y"] = cy,
                ["track"] = 0.03,
                ["lead"] = 1.1,
                ["color"] = Col(sc, "fg"),
            });
        },
    });

    // ---------------- dotMatrix（JS L2210-2369） ----------------

    private static void RegisterDotMatrix() => Reg0("dotMatrix", new LayoutDef
    {
        Name = "ドット表示",
        Tags = new[] { "graphic", "glitch", "pop" },
        W = 0.7,
        Treat = false,
        Fits = n => n >= 1 && n <= 12,
        EnterBias = new JObject
        {
            ["cut"] = 2.5,
            ["flicker"] = 1.6,
            ["scramble"] = 0.2,
            ["assemble"] = 0.2,
            ["type"] = 1.2,
        },
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(new[] { "dot", "gothic_black", "dot" }),
            ["reveal"] = rng.Pick(new[] { "sweep", "sweep", "scroll", "random" }),
            ["panel"] = rng.Chance(0.7),
            ["col"] = rng.Pick(new[] { "accent", "accent", "fg" }),
            ["shape"] = rng.Pick(new[] { "round", "round", "square" }),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var s = (int)cut.Seed;
            var M = Math.Min(W, H);
            var lt = env.Lt;
            var font = S(Pm, "font")!;
            var mt = MainLines(cut.Text, W, H, 8, 4);
            var lines = mt.Split('\n');
            var maxL = lines.Select(l => JLayouts.GlyphCount(l)).DefaultIfEmpty(1).Max();
            if (maxL < 1) maxL = 1;
            var dotD = J.Clamp(Math.Floor(W * 0.86 / (maxL * M * 0.011)), 10, 16);
            const double lead = 1.3;
            var smp = DotSampleOf(mt, font, (int)dotD, lead);
            var p = Math.Min(W * 0.88 / smp.Cols, H * 0.62 / smp.Rows);
            var gw = smp.Cols * p;
            var gh = smp.Rows * p;
            var gx = W / 2 - gw / 2;
            var gy = H / 2 - gh / 2;
            var dark = J.Lum(Col(sc, "bg")) < 0.45;
            var panel = B(Pm, "panel")
                ? dark
                    ? J.Mix(Col(sc, "bg"), Col(sc, "fg"), 0.05)
                    : J.Lum(Col(sc, "fg")) < J.Lum(Col(sc, "ink")) ? Col(sc, "fg") : Col(sc, "ink")
                : Col(sc, "bg");
            var colPref = S(Pm, "col") == "accent" ? Col(sc, "accent") : Col(sc, "fg");
            var litC = new[] { colPref, Col(sc, "accent"), Col(sc, "fg"), Col(sc, "accent2"), Col(sc, "bg") }
                           .FirstOrDefault(c => !string.IsNullOrEmpty(c) && J.Contrast(c, panel) >= 2.5)
                       ?? OnCol(sc, panel);
            var offC = J.Mix(panel, litC, 0.13);
            var outK = OutK(env);
            var inA = InE(env, 0.3);
            var shape = S(Pm, "shape")!;
            var rr = p * (shape == "round" ? 0.38 : 0.4);
            void Dot(SKPath path, double x, double y)
            {
                if (shape == "round") path.AddCircle((float)x, (float)y, (float)rr);
                else path.AddRect(new SKRect((float)(x - rr), (float)(y - rr), (float)(x + rr), (float)(y + rr)));
            }
            // LED 面板不吃色差 ghost，clip 也能省一层
            if (env.Pass != "main")
            {
                env.Body = null;
                return;
            }
            if (outK > 0)
            {
                if (B(Pm, "panel"))
                {
                    var pd = p * 1.2;
                    env.RRect(gx - pd, gy - pd, gw + pd * 2, gh + pd * 2, p * 1.2, panel, inA * outK, false,
                        J.Mix(panel, litC, 0.3), Math.Max(1, p * 0.12));
                }
                var tile = DotTileOf(offC, shape);
                var fr = J.Clamp(lt / 0.3);
                if (fr > 0)
                {
                    using var shader = SKShader.CreateBitmap(tile.Cv, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat,
                        SKMatrix.Concat(SKMatrix.CreateTranslation((float)gx, (float)gy),
                            SKMatrix.CreateScale((float)(p / tile.T), (float)(p / tile.T))));
                    using var tp = new SKPaint
                    {
                        IsAntialias = true,
                        Color = SKColors.White.WithAlpha((byte)Math.Round(J.Clamp(inA * outK) * 255)),
                        Shader = shader,
                    };
                    env.Ctx.DrawRect(new SKRect((float)gx, (float)gy, (float)(gx + gw * fr), (float)(gy + gh)), tp);
                }
            }
            // 亮点：歌词本体透过自身采样出的点阵模板画出
            var T = J.Clamp(cut.Dur * 0.3, 0.25, 0.7);
            const double t0 = 0.12;
            var u = J.Clamp((lt - t0) / T);
            var reveal = S(Pm, "reveal");
            var shift = 0.0;
            if (reveal == "scroll") shift = Math.Round((1 - J.E.OutCubic(u)) * smp.Cols);
            var front = reveal == "sweep" ? u * (smp.Cols + 4) - 2 : 1e9;
            var lit = smp.Lit;
            env.Ctx.Save();
            using (var clip = new SKPath())
            {
                var any = false;
                for (var k = 0; k < lit.Count; k += 2)
                {
                    var c = lit[k] + shift;
                    var r = lit[k + 1];
                    if (c >= smp.Cols || c > front) continue;
                    if (reveal == "random" && J.R(s, lit[k], r, 7) > u * 1.05) continue;
                    Dot(clip, gx + (c + 0.5) * p, gy + (r + 0.5) * p);
                    any = true;
                }
                if (!any) clip.AddRect(new SKRect(-10, -10, -9, -9));
                env.Ctx.ClipPath(clip);
                var fsz = smp.Fscale * p;
                var bb = JLayouts.MainDraw(env, new JObject
                {
                    ["text"] = mt,
                    ["font"] = font,
                    ["size"] = fsz,
                    ["x"] = W / 2 + shift * p,
                    ["y"] = H / 2,
                    ["lead"] = lead,
                    ["color"] = litC,
                    ["stroke"] = p * 0.9,
                    ["strokeColor"] = litC,
                    ["strokeUnder"] = true,
                    ["noHold"] = true,
                    ["ghost"] = false,
                    ["mi"] = MiAt(env, t0),
                });
                env.Ctx.Restore();
                // 扫描前沿的亮列
                if (reveal == "sweep" && u > 0 && u < 1)
                {
                    var fc = (int)Math.Floor(front);
                    using var hp = new SKPaint { IsAntialias = true, Color = Env.SkColor(Col(sc, "fg"), 0.9 * outK) };
                    using var hpPath = new SKPath();
                    for (var k = 0; k < lit.Count; k += 2)
                        if (lit[k] == fc) Dot(hpPath, gx + (lit[k] + 0.5) * p, gy + (lit[k + 1] + 0.5) * p);
                    env.Ctx.DrawPath(hpPath, hp);
                }
                env.Body = bb != null ? BbRect(gx, gy, gx + gw, gy + gh) : null;
            }
        },
    });

    // ---------------- depthStack（JS L2371-2424） ----------------

    private static void RegisterDepthStack() => Reg0("depthStack", new LayoutDef
    {
        Name = "奥行き重ね",
        Tags = new[] { "graphic", "emotional", "glitch" },
        W = 1,
        Emph = 1.3,
        Fits = n => n >= 1 && n <= 12,
        Plan = (rng, cut, st) =>
        {
            var a = rng.Pick(new[] { -150, -120, -60, -30, 30, 60, 120, 150, -90, 90 }) + rng.Range(-12, 12);
            return new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
                ["ang"] = a,
                ["copies"] = rng.Int(5, 8),
                ["dist"] = rng.Range(0.42, 0.62),
                ["style"] = rng.Pick(new[] { "outline", "outline", "dim", "lines" }),
                ["sway"] = rng.Chance(0.7),
            };
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var M = Math.Min(W, H);
            var lt = env.Lt;
            var font = S(Pm, "font")!;
            var mt = MainLines(cut.Text, W, H, 9, 5);
            var size = Math.Min(JLayouts.FitSize(mt, font, W * 0.66, H * 0.3,
                new JObject { ["track"] = 0.03, ["lead"] = 1.1 }), H * 0.2);
            var a = D(Pm, "ang") * J.DEG;
            var dx = Math.Cos(a);
            var dy = Math.Sin(a);
            var cx = W / 2 - dx * M * 0.05;
            var cy = H / 2 - dy * M * 0.05;
            var vx = cx + dx * M * D(Pm, "dist");
            var vy = cy + dy * M * D(Pm, "dist");
            if (B(Pm, "sway"))
            {
                vx += Math.Sin(env.Ltb * 0.7) * M * 0.05;
                vy += Math.Cos(env.Ltb * 0.55) * M * 0.035;
            }
            var N = I(Pm, "copies");
            var e = J.E.OutCubic(J.Clamp(lt / 0.7));
            var outK = 1 - J.E.InCubic(env.POut);
            var depth = e * outK;
            var mm = JLayouts.Measure(new JObject { ["text"] = mt, ["font"] = font, ["size"] = size, ["track"] = 0.03, ["lead"] = 1.1 });
            var style = S(Pm, "style");
            if (style == "lines" && depth > 0.01)
            {
                var sN = 1 / (1 + N * 0.26 * depth);
                foreach (var (ox, oy) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
                {
                    var x0 = cx + ox * mm.W / 2;
                    var y0 = cy + oy * mm.H / 2;
                    var x1 = J.Lerp(cx, vx, 1 - sN) + ox * mm.W / 2 * sN;
                    var y1 = J.Lerp(cy, vy, 1 - sN) + oy * mm.H / 2 * sN;
                    env.Line(new[] { new[] { x0, y0 }, new[] { x1, y1 } }, Col(sc, "sub"), Math.Max(1, M * 0.0013), 0.5 * outK, false);
                }
            }
            for (var k = N; k >= 1; k--)
            {
                var s1 = 1 / (1 + k * 0.26 * depth);
                if (depth <= 0.001) break;
                var x = J.Lerp(cx, vx, 1 - s1);
                var y = J.Lerp(cy, vy, 1 - s1);
                var f = k / (double)N;
                var it = new JObject
                {
                    ["text"] = mt,
                    ["font"] = font,
                    ["size"] = size * s1,
                    ["x"] = x,
                    ["y"] = y,
                    ["track"] = 0.03,
                    ["lead"] = 1.1,
                    ["ghost"] = false,
                };
                if (style == "dim")
                {
                    it["color"] = J.Mix(Col(sc, "bg"), Col(sc, "sub"), 0.55 - 0.4 * f);
                    it["alpha"] = outK;
                }
                else
                {
                    it["fill"] = false;
                    it["stroke"] = Math.Max(1, size * s1 * 0.014);
                    it["strokeColor"] = k == 1 ? Col(sc, "accent") : Col(sc, "sub");
                    it["alpha"] = (0.85 - 0.6 * f) * outK;
                }
                DrawIt(env, it);
            }
            env.Body = JLayouts.MainDraw(env, new JObject
            {
                ["text"] = mt,
                ["font"] = font,
                ["size"] = size,
                ["x"] = cx,
                ["y"] = cy,
                ["track"] = 0.03,
                ["lead"] = 1.1,
                ["color"] = Col(sc, "fg"),
            });
        },
    });

    // ---------------- typeSpecimen（JS L2426-2535） ----------------

    private static readonly string[] SpecFonts =
    {
        "gothic_black", "mincho", "round", "dot", "brush", "pop", "dela", "tokumin", "zenkaku",
        "gothic_light", "mincho_black", "sansui", "mincho_light",
    };

    private static void RegisterTypeSpecimen() => Reg0("typeSpecimen", new LayoutDef
    {
        Name = "書体見本",
        Tags = new[] { "editorial", "graphic" },
        W = 0.8,
        Fits = n => n >= 1 && n <= 8,
        Plan = (rng, cut, st) =>
        {
            var main = rng.Pick(FontsOf(st, "display", "serif"));
            var pool = SpecFonts.Where(k => k != main && JFonts.Has(k)).ToList();
            // JS Fisher-Yates（rng.int(0,i)，从尾到头）
            for (var i = pool.Count - 1; i > 0; i--)
            {
                var j = rng.Int(0, i);
                (pool[i], pool[j]) = (pool[j], pool[i]);
            }
            var port = cut.H > cut.W;
            return new JObject
            {
                ["main"] = main,
                ["fonts"] = new JArray(pool.Take(6).Select(x => (JToken)x)),
                ["grid"] = port ? "list" : rng.Pick(new[] { "g2", "g3", "list" }),
                ["num"] = rng.Int(1, 30),
            };
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var M = Math.Min(W, H);
            var lt = env.Lt;
            var txt = cut.Text.Trim();
            var outK = OutK(env);
            var lw = Math.Max(1, M * 0.0014);
            var cap = J.Clamp(M * 0.017, 11, 24);
            var mono = MonoF(env);
            var mainFont = S(Pm, "main")!;
            var fontsList = StrArr(Pm["fonts"]);
            var grid = S(Pm, "grid")!;
            var cells = new List<(double X, double Y, double W, double H, string Font, bool Main)>();
            var mx = W * 0.07;
            var my = H * 0.1;
            var gw = W - mx * 2;
            var gh = H - my * 2;
            string FontAt(int i) => i - 1 >= 0 && i - 1 < fontsList.Count ? fontsList[i - 1] : mainFont;
            if (grid == "g2")
            {
                var g = M * 0.02;
                var cw = (gw - g) / 2;
                var ch = (gh - g) / 2;
                for (var i = 0; i < 4; i++)
                    cells.Add((mx + i % 2 * (cw + g), my + Math.Floor(i / 2.0) * (ch + g), cw, ch,
                        i == 0 ? mainFont : FontAt(i), i == 0));
            }
            else if (grid == "g3")
            {
                var g = M * 0.018;
                var cw = (gw - g * 2) / 3;
                var ch = (gh - g * 2) / 3;
                cells.Add((mx, my, cw * 2 + g, ch * 2 + g, mainFont, true));
                var rest = new[] { (2, 0), (2, 1), (0, 2), (1, 2), (2, 2) };
                for (var i = 0; i < rest.Length; i++)
                    cells.Add((mx + rest[i].Item1 * (cw + g), my + rest[i].Item2 * (ch + g), cw, ch, FontAt(i + 1), false));
            }
            else
            {
                var rowsN = W < H ? 5 : 4;
                var hs = new List<double> { 2.2 };
                for (var i = 1; i < rowsN; i++) hs.Add(1);
                var tot = hs.Sum();
                var y = my;
                for (var i = 0; i < hs.Count; i++)
                {
                    var h = gh * hs[i] / tot;
                    cells.Add((mx, y, gw, h, i == 0 ? mainFont : FontAt(i), i == 0));
                    y += h;
                }
            }
            BBox? bb = null;
            for (var i = 0; i < cells.Count; i++)
            {
                var c = cells[i];
                var d = 0.06 + i * 0.07;
                var e = J.E.OutCubic(J.Clamp((lt - d) / 0.4)) * outK;
                if (e <= 0) continue;
                // 每格顶部的横线和字体标注
                env.Line(new[]
                {
                    new[] { c.X, c.Y },
                    new[] { c.X + c.W * J.E.OutExpo(J.Clamp((lt - d) / 0.5)), c.Y },
                }, c.Main ? Col(sc, "accent") : Col(sc, "sub"), c.Main ? lw * 3 : lw, (c.Main ? 1 : 0.6) * outK, false);
                var F = JLang.FaceOf(c.Font); // JS: J.FONTS[c.font] && J.faceOf ? J.faceOf(c.font) : J.FONTS[c.font]
                var label = (I(Pm, "num") + i).ToString("D2", CultureInfo.InvariantCulture)
                            + "  " + (F.Name ?? F.Label ?? c.Font).ToUpperInvariant()
                            + "  " + F.Weight;
                DrawIt(env, new JObject
                {
                    ["text"] = label,
                    ["font"] = mono,
                    ["size"] = cap,
                    ["align"] = "left",
                    ["track"] = 0.08,
                    ["x"] = c.X,
                    ["y"] = c.Y + cap * 1.1,
                    ["color"] = c.Main ? Col(sc, "accent") : Col(sc, "sub"),
                    ["alpha"] = e,
                    ["ghost"] = false,
                });
                var list = grid == "list";
                var tw = list ? c.W * (c.Main ? 1 : 0.8) : c.W * 0.9;
                var th = c.H - cap * (list ? 1.8 : 2.8);
                var fsz = Math.Min(JLayouts.FitSize(txt, c.Font, tw, th * (list ? 0.78 : 0.7),
                    new JObject { ["track"] = 0.02 }), c.Main ? H * 0.3 : H * 0.14);
                var it = new JObject
                {
                    ["text"] = txt,
                    ["font"] = c.Font,
                    ["size"] = fsz,
                    ["track"] = 0.02,
                    ["x"] = list ? c.X : c.X + c.W / 2,
                    ["y"] = c.Y + cap * (list ? 1.8 : 2.2) + th / 2,
                    ["align"] = list ? "left" : "center",
                    ["color"] = Col(sc, "fg"),
                };
                if (list && !c.Main)
                {
                    it["x"] = c.X + c.W * 0.2;
                    it["y"] = c.Y + c.H / 2 + cap * 0.3;
                }
                if (c.Main) bb = JLayouts.MainDraw(env, it);
                else
                {
                    it["color"] = Col(sc, "sub");
                    it["alpha"] = e * 0.9;
                    it["y"] = (double)it["y"]! + (1 - e) * cap * 1.5;
                    it["ghost"] = false;
                    DrawIt(env, it);
                }
            }
            env.Body = bb;
        },
    });

    // ---------------- kanjiFocus（JS L2537-2614） ----------------

    private static void RegisterKanjiFocus() => Reg0("kanjiFocus", new LayoutDef
    {
        Name = "一字強調",
        Tags = new[] { "emotional", "editorial", "calm" },
        W = 1,
        Emph = 1.6,
        Fits = n => n >= 2 && n <= 16,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
            ["fs"] = rng.Pick(FontsOf(st, "serif", "display", "body")),
            ["mode"] = rng.Pick(new[] { "dim", "outline", "tint" }),
            ["pos"] = rng.Pick(new[] { "center", "side", "side" }),
            ["low"] = rng.Chance(0.45),
            ["dots"] = rng.Chance(0.7),
            ["dir"] = rng.Pick(new[] { 1, -1 }),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var M = Math.Min(W, H);
            var lt = env.Lt;
            var font = S(Pm, "font")!;
            var fsFont = S(Pm, "fs")!;
            var port = W < H;
            var raw = cut.Text.Trim();
            var chs = JLayouts.CodePoints(raw);
            var fi = chs.FindIndex(c => J.IsKanji(c[0]));
            if (fi < 0)
                fi = chs.FindIndex(c => c.Trim().Length > 0 && !J.IsPunct(c[0]) && !J.IsSmallKana(c[0]));
            if (fi < 0) fi = 0;
            var fch = chs[fi];
            // 背景的巨大单字
            var big = port ? W * 1.05 : H * 1.02;
            var side = !port && S(Pm, "pos") == "side";
            var dir = D(Pm, "dir");
            var bx = side ? W / 2 + dir * W * 0.2 : W / 2;
            var by = H / 2;
            var u = J.Clamp(lt / Math.Max(0.5, cut.Dur));
            var e = J.E.OutCubic(J.Clamp(lt / 0.8));
            var outK = OutK(env);
            var sz = big * (1.08 - 0.08 * J.E.OutCubic(u)) * (1 + 0.04 * J.E.InCubic(env.POut));
            var bi = new JObject
            {
                ["text"] = fch,
                ["font"] = font,
                ["size"] = sz,
                ["x"] = bx,
                ["y"] = by,
                ["ghost"] = false,
                ["alpha"] = e * outK,
            };
            var mode = S(Pm, "mode");
            if (mode == "dim") bi["color"] = J.Mix(Col(sc, "bg"), Col(sc, "fg"), 0.13);
            else if (mode == "tint") bi["color"] = J.Mix(Col(sc, "bg"), Col(sc, "accent"), 0.22);
            else
            {
                bi["fill"] = false;
                bi["stroke"] = Math.Max(1.2, sz * 0.004);
                bi["strokeColor"] = Col(sc, "sub");
                bi["alpha"] = e * outK * 0.7;
            }
            DrawIt(env, bi);
            // 前景的小号完整歌词
            var mt = MainLines(raw, W, H, 16, 8);
            var baseIt = new JObject
            {
                ["text"] = mt,
                ["font"] = fsFont,
                ["x"] = side ? W / 2 - dir * W * 0.12 : W / 2,
                ["y"] = B(Pm, "low") ? H * 0.74 : H / 2,
                ["track"] = 0.14,
                ["lead"] = 1.5,
            };
            var baseSize = Math.Min(JLayouts.FitSize(mt, fsFont, side ? W * 0.44 : W * 0.7, H * 0.2, baseIt), M * 0.075);
            baseIt["size"] = baseSize;
            var gl = GlyphPts(baseIt);
            BBox? bb = null;
            var head = raw.Length > fi ? raw[..fi] : raw; // JS raw.slice(0, fi)（按 UTF-16 单元）
            var fiAdj = fi - JLayouts.CodePoints(head).Count(c => c == " " || c == "　");
            for (var i = 0; i < gl.Count; i++)
            {
                var g = gl[i];
                var isF = i == fiAdj;
                bb = U(bb, JLayouts.MainDraw(env, new JObject
                {
                    ["text"] = g.Ch,
                    ["font"] = fsFont,
                    ["size"] = baseSize,
                    ["x"] = g.X,
                    ["y"] = g.Y,
                    ["color"] = isF ? Col(sc, "accent") : Col(sc, "fg"),
                    ["mi"] = i * 0.6,
                }));
                if (isF && B(Pm, "dots"))
                {
                    var q = J.E.OutBack(J.Clamp((lt - 0.35) / 0.25), 2) * outK;
                    env.Circle(g.X, g.Y - baseSize * 0.78, baseSize * 0.075 * q, Col(sc, "accent"), null, 0, 1, true);
                }
            }
            if (bb != null)
            {
                var le = J.E.OutExpo(J.Clamp((lt - 0.2) / 0.6)) * outK;
                var y = bb.Y1 + baseSize * 0.7;
                var x0 = bb.X0;
                var x1 = bb.X1;
                env.Line(new[] { new[] { x0, y }, new[] { J.Lerp(x0, x1, le), y } }, Col(sc, "sub"),
                    Math.Max(1, M * 0.0013), 0.6, false);
            }
            env.Body = bb;
        },
    });

    // ---------------- halfVertical（JS L2616-2736） ----------------

    private static void RegisterHalfVertical() => Reg0("halfVertical", new LayoutDef
    {
        Name = "縦横混植",
        Tags = new[] { "editorial", "graphic" },
        W = 1,
        Fits = n => n >= 3 && n <= 14,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
            ["shape"] = rng.Pick(new[] { "rowCol", "rowCol", "colRow" }),
            ["guide"] = rng.Pick(new[] { "bracket", "tick", "bracket" }),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var M = Math.Min(W, H);
            var lt = env.Lt;
            var font = S(Pm, "font")!;
            var txt = Regex.Replace(cut.Text.Trim(), @"[\s\u3000]+", " ");
            var arr = JLayouts.CodePoints(txt);
            var n = arr.Count;
            var rowCol = S(Pm, "shape") == "rowCol";
            var port = W < H;
            // 沿画幅长边的那支拿更多字，断点在目标附近找最自然的边界
            var tgt = n * (port ? rowCol ? 0.38 : 0.62 : rowCol ? 0.6 : 0.4);
            var segB = new HashSet<int>();
            var acc = 0;
            foreach (var g in Planner.Segments(txt))
            {
                acc += JLayouts.CpLen(g);
                segB.Add(acc);
            }
            var cutAt = Math.Max(1, (int)Math.Round(tgt, MidpointRounding.AwayFromZero)); // JS Math.round：半值向上
            var bs = -1e9;
            for (var k = 1; k < n; k++)
            {
                var pa = arr[k - 1];
                var pb = arr[k];
                var sc0 = -Math.Abs(k - tgt) * 1.2;
                if (segB.Contains(k)) sc0 += 2;
                if (pa == " " || pb == " ") sc0 += 3;
                if (J.IsSmallKana(pb[0]) || J.IsPunct(pb[0]) || pb == "ー") sc0 -= 8;
                if (J.IsSmallKana(pa[0])) sc0 -= 3;
                if (J.IsHira(pa[0]) && !J.IsHira(pb[0])) sc0 += 1.5;
                if (sc0 > bs)
                {
                    bs = sc0;
                    cutAt = k;
                }
            }
            var A = string.Concat(arr.Take(cutAt)).Trim();
            if (A.Length == 0) A = arr[0];
            var B = string.Concat(arr.Skip(cutAt)).Trim();
            const double tr = 0.06;
            var mA = JLayouts.Measure(new JObject { ["text"] = A, ["font"] = font, ["size"] = 100, ["track"] = tr, ["vertical"] = !rowCol });
            var mB = JLayouts.Measure(new JObject { ["text"] = B, ["font"] = font, ["size"] = 100, ["track"] = tr, ["vertical"] = rowCol });
            // 以字号为单位的占位范围
            var wU = rowCol ? mA.W / 100 : 1.25 + mB.W / 100;
            var hU = rowCol ? 1.25 + mB.H / 100 : mA.H / 100;
            var size = Math.Min(Math.Min(W * 0.82 / wU, H * 0.8 / hU), M * 0.26);
            var x0 = W / 2 - wU * size / 2;
            var y0 = H / 2 - hU * size / 2;
            BBox? bb = null;
            var outK = OutK(env);
            double[] corner;
            if (rowCol)
            {
                var itA = new JObject
                {
                    ["text"] = A,
                    ["font"] = font,
                    ["size"] = size,
                    ["x"] = x0,
                    ["y"] = y0 + size / 2,
                    ["align"] = "left",
                    ["track"] = tr,
                    ["color"] = Col(sc, "fg"),
                    ["mi"] = 0,
                };
                var gA = GlyphPts(itA);
                var last = gA.Count > 0 ? gA[^1].X : x0 + size / 2;
                bb = U(bb, JLayouts.MainDraw(env, itA));
                if (B.Length > 0)
                    bb = U(bb, JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = B,
                        ["font"] = font,
                        ["size"] = size,
                        ["x"] = last,
                        ["y"] = y0 + size * 1.25,
                        ["vertical"] = true,
                        ["align"] = "left",
                        ["track"] = tr,
                        ["color"] = Col(sc, "fg"),
                        ["mi"] = 3,
                    }));
                corner = new[] { last, y0 + size / 2 };
            }
            else
            {
                var itA = new JObject
                {
                    ["text"] = A,
                    ["font"] = font,
                    ["size"] = size,
                    ["x"] = x0 + size / 2,
                    ["y"] = y0,
                    ["vertical"] = true,
                    ["align"] = "left",
                    ["track"] = tr,
                    ["color"] = Col(sc, "fg"),
                    ["mi"] = 0,
                };
                var gA = GlyphPts(itA);
                var last = gA.Count > 0 ? gA[^1].Y : y0 + size / 2;
                bb = U(bb, JLayouts.MainDraw(env, itA));
                if (B.Length > 0)
                    bb = U(bb, JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = B,
                        ["font"] = font,
                        ["size"] = size,
                        ["x"] = x0 + size * 1.25,
                        ["y"] = last,
                        ["align"] = "left",
                        ["track"] = tr,
                        ["color"] = Col(sc, "fg"),
                        ["mi"] = 3,
                    }));
                corner = new[] { x0 + size / 2, last };
            }
            // 贴在转角外侧的细引导线
            var g2 = size * 0.42;
            var lw = Math.Max(1.2, M * 0.0016);
            var le = J.E.OutCubic(J.Clamp((lt - 0.15) / 0.7)) * outK;
            var guide = S(Pm, "guide");
            if (guide == "bracket" && le > 0)
            {
                double[][] pts = rowCol
                    ? new[]
                    {
                        new[] { x0 - g2 * 0.3, y0 - g2 * 0.55 },
                        new[] { corner[0] + size / 2 + g2 * 0.55, y0 - g2 * 0.55 },
                        new[] { corner[0] + size / 2 + g2 * 0.55, y0 + hU * size + g2 * 0.3 },
                    }
                    : new[]
                    {
                        new[] { x0 - g2 * 0.55, y0 - g2 * 0.3 },
                        new[] { x0 - g2 * 0.55, corner[1] + size / 2 + g2 * 0.55 },
                        new[] { x0 + wU * size + g2 * 0.3, corner[1] + size / 2 + g2 * 0.55 },
                    };
                env.PolyPartial(pts, le, Col(sc, "sub"), lw, 0.8, false);
            }
            var q = J.E.OutBack(J.Clamp((lt - 0.3) / 0.25), 2) * outK;
            if (q > 0)
            {
                var cs = size * 0.12;
                var px = rowCol ? corner[0] + size / 2 + g2 * 0.55 : x0 - g2 * 0.55;
                var py = rowCol ? y0 - g2 * 0.55 : corner[1] + size / 2 + g2 * 0.55;
                env.Rect(px - cs / 2 * q, py - cs / 2 * q, cs * q, cs * q, Col(sc, "accent"), 1, true);
            }
            env.Body = bb;
        },
    });

    // ---------------- curtain（JS L2738-2833） ----------------

    private static void RegisterCurtain() => Reg0("curtain", new LayoutDef
    {
        Name = "幕",
        Tags = new[] { "emotional", "pop", "graphic" },
        W = 0.9,
        Emph = 1.4,
        Fits = n => n >= 1 && n <= 16,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
            ["variant"] = rng.Pick(new[] { "side", "side", "shutter", "rise" }),
            ["col"] = rng.Pick(new[] { "velvet", "velvet", "accent", "ink" }),
            ["drape"] = rng.Chance(0.7),
            ["pleats"] = rng.Chance(0.75),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var M = Math.Min(W, H);
            var lt = env.Lt;
            var font = S(Pm, "font")!;
            var variant = S(Pm, "variant")!;
            var drape = B(Pm, "drape");
            var mt = MainLines(cut.Text, W, H, 10, 5);
            var size = Math.Min(JLayouts.FitSize(mt, font, W * (drape && variant == "side" ? 0.7 : 0.8),
                H * (variant == "shutter" ? 0.34 : 0.44), new JObject { ["track"] = 0.05, ["lead"] = 1.15 }), H * 0.2);
            const double t0 = 0.02;
            var T = J.Clamp(cut.Dur * 0.16, 0.22, 0.48);
            var bb = JLayouts.MainDraw(env, new JObject
            {
                ["text"] = mt,
                ["font"] = font,
                ["size"] = size,
                ["x"] = W / 2,
                ["y"] = H / 2,
                ["track"] = 0.05,
                ["lead"] = 1.15,
                ["color"] = Col(sc, "fg"),
                ["mi"] = MiAt(env, t0 + T * 0.25),
            });
            var open = J.E.InOutCubic(J.Clamp((lt - t0) / T)) * (1 - J.E.InOutCubic(env.POut));
            var dark = J.Lum(Col(sc, "bg")) < 0.45;
            var colK = S(Pm, "col");
            var panel = colK == "velvet"
                ? J.Mix(Col(sc, "bg"), Col(sc, "accent"), dark ? 0.38 : 0.6)
                : PlateCol(sc, colK == "accent" ? Col(sc, "accent") : Col(sc, "ink"),
                    colK == "accent" ? Col(sc, "ink") : Col(sc, "accent"));
            var shade = J.Mix(panel, Col(sc, "bg"), 0.22);
            var lite = J.Mix(panel, OnCol(sc, panel), 0.12);
            const bool moving = false; // 大幅移动的面板：不开色差 ghost
            var lw = Math.Max(1, M * 0.002);
            void Pleat(double px0, double px1, double py0, double py1, double k)
            {
                // 垂直褶皱，随面板收窄而压缩
                if (!B(Pm, "pleats") || env.Pass != "main") return;
                const int m = 7;
                var w = px1 - px0;
                if (w < 4) return;
                for (var i = 1; i < m; i++)
                {
                    var x = px0 + w * i / m;
                    env.Rect(x - lw * 1.5, py0, lw * 3, py1 - py0, i % 2 != 0 ? shade : lite, 0.55 * k, false);
                }
            }
            if (variant == "side")
            {
                var rest = drape ? W * 0.075 : -W * 0.02;
                var xl = J.Lerp(W / 2, rest, open);
                var xr = J.Lerp(W / 2, W - rest, open);
                var bulge = Math.Sin(Math.PI * J.Clamp(open)) * W * 0.02;
                var L = new[]
                {
                    new[] { -5.0, -5.0 }, new[] { xl, -5.0 }, new[] { xl + bulge, H * 0.5 }, new[] { xl, H + 5 }, new[] { -5.0, H + 5 },
                };
                var R = new[]
                {
                    new[] { W + 5, -5.0 }, new[] { xr, -5.0 }, new[] { xr - bulge, H * 0.5 }, new[] { xr, H + 5 }, new[] { W + 5, H + 5 },
                };
                env.Poly(L, panel, 1, moving);
                env.Poly(R, panel, 1, moving);
                Pleat(0, xl, 0, H, 1);
                Pleat(xr, W, 0, H, 1);
                if (drape && open > 0.5)
                {
                    // 窗帘绑带
                    var q = J.Clamp((open - 0.5) * 2);
                    var strap = Col(sc, "accent") == panel ? Col(sc, "fg") : Col(sc, "accent");
                    env.Rect(xl - W * 0.012, H * 0.62, W * 0.012 + 2, H * 0.018, strap, q, false);
                    env.Rect(xr - 2, H * 0.62, W * 0.012 + 2, H * 0.018, strap, q, false);
                }
            }
            else if (variant == "shutter")
            {
                var rest = drape ? H * 0.12 : -H * 0.02;
                var yt = J.Lerp(H / 2, rest, open);
                var yb = J.Lerp(H / 2, H - rest, open);
                env.Rect(-5, -5, W + 10, yt + 5, panel, 1, moving);
                env.Rect(-5, yb, W + 10, H - yb + 5, panel, 1, moving);
                if (drape && open > 0.9)
                {
                    var fs = J.Clamp(H * 0.018, 11, 22);
                    var a = J.Clamp((open - 0.9) * 10);
                    DrawIt(env, new JObject
                    {
                        ["text"] = (Math.Max(0, cut.Line) + 1).ToString("D2", CultureInfo.InvariantCulture) + " ／ " + J.FmtTime(cut.Start),
                        ["font"] = MonoF(env),
                        ["size"] = fs,
                        ["track"] = 0.3,
                        ["align"] = "left",
                        ["x"] = W * 0.05,
                        ["y"] = yt / 2,
                        ["color"] = OnCol(sc, panel),
                        ["alpha"] = a,
                        ["ghost"] = false,
                    });
                }
            }
            else
            {
                // 剧场幕上升，留一排扇贝形帷幔
                var rest = drape ? H * 0.1 : -H * 0.05;
                var yb = J.Lerp(H + 5, rest, open);
                const int m = 9;
                var sw = W / m;
                var dip = Math.Min(H * 0.035, Math.Max(0, yb) * 0.5);
                var pts = new List<double[]>
                {
                    new[] { -5.0, -5.0 }, new[] { W + 5, -5.0 }, new[] { W + 5, yb },
                };
                for (var i = m; i >= 0; i--)
                {
                    pts.Add(new[] { i * sw, yb });
                    if (i > 0) pts.Add(new[] { i * sw - sw / 2, yb + dip });
                }
                env.Poly(pts.ToArray(), panel, 1, moving);
                Pleat(0, W, 0, Math.Max(0, yb), 1);
            }
            env.Body = bb;
        },
    });

    // ---------------- equalizer（JS L2835-2926） ----------------

    private static void RegisterEqualizer() => Reg0("equalizer", new LayoutDef
    {
        Name = "イコライザー",
        Tags = new[] { "pop", "graphic", "glitch" },
        W = 0.8,
        Portrait = 0.9,
        Fits = n => n >= 1 && n <= 12,
        EnterBias = new JObject
        {
            ["cut"] = 1.5,
            ["pop"] = 1.3,
            ["drop"] = 1.2,
            ["slice"] = 0.4,
            ["wipe"] = 0.5,
        },
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "display")),
            ["style"] = rng.Pick(new[] { "bars", "blocks", "blocks", "mirror" }),
            ["thin"] = rng.Pick(new[] { 0, 2, 3 }),
            ["peaks"] = rng.Chance(0.7),
            ["col"] = rng.Pick(new[] { "accent", "accent", "duo", "fg" }),
            ["tempo"] = rng.Range(0.42, 0.55),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var s = (int)cut.Seed;
            var M = Math.Min(W, H);
            var lt = env.Lt;
            var font = S(Pm, "font")!;
            var chs = SlotsOf(cut.Text);
            var n = chs.Count;
            if (n == 0)
            {
                env.Body = null;
                return;
            }
            var port = W < H;
            var rows = port && n > 6 ? 2 : 1;
            var per = (int)Math.Ceiling(n / (double)rows);
            var sw = W * 0.86 / per;
            var size = Math.Min(sw * 0.78, H * (rows > 1 ? 0.1 : 0.14));
            var bw = Math.Min(sw * 0.6, size * 1.15);
            var e = InE(env, 0.45);
            var outK = OutK(env);
            var colA = PlateCol(sc, Col(sc, "accent"), Col(sc, "fg"));
            var colB = PlateCol(sc, Col(sc, "accent2"), Col(sc, "sub"), Col(sc, "fg"));
            var tempo = D(Pm, "tempo");
            var style = S(Pm, "style")!;
            var colK = S(Pm, "col")!;
            var peaks = B(Pm, "peaks");
            var thin = I(Pm, "thin");
            var hasEnergy = env.Plan.Energy is { Length: > 0 };
            // 频段 x（连续下标）在 t 时刻的电平
            double Level(double x, double t)
            {
                var v = 0.5 + 0.5 * J.Noise1(t * 3.1 + x * 0.83, s);
                v = 0.25 + 0.6 * v;
                double pulse;
                var bt = BeatOf(env);
                if (bt.Has && bt.Len > 0.2)
                    pulse = Math.Exp(-bt.Since * 7) * (0.4 + 0.6 * J.R(s, bt.Index, (int)Math.Round(x * 3)));
                else
                {
                    var k = (int)Math.Floor(t / tempo);
                    var ph = t - k * tempo;
                    pulse = Math.Exp(-ph * 7) * (0.3 + 0.7 * J.R(s, k, (int)Math.Round(x * 3)));
                }
                v = v * 0.75 + pulse * 0.45;
                if (hasEnergy) v = v * (0.45 + 0.75 * env.Energy);
                return J.Clamp(v, 0.06, 1);
            }
            var lw = Math.Max(1, M * 0.0016);
            BBox? bb = null;
            for (var r = 0; r < rows; r++)
            {
                var cnt = Math.Min(per, n - r * per);
                var yb = rows > 1 ? r != 0 ? H * 0.84 : H * 0.47 : H * 0.74;
                var hmax = (rows > 1 ? H * 0.26 : H * 0.44) - size * 0.4;
                var x0 = W / 2 - cnt * sw / 2;
                env.Line(new[]
                {
                    new[] { x0 - sw * 0.2, yb },
                    new[] { x0 - sw * 0.2 + (cnt * sw + sw * 0.4) * e, yb },
                }, Col(sc, "sub"), lw, 0.7 * outK, false);
                void DrawBar(double x, double w, double h, string col, double a)
                {
                    if (h <= 0.5) return;
                    if (style == "blocks")
                    {
                        var seg = Math.Max(4, sw * 0.14);
                        var gap = seg * 0.28;
                        var m = (int)Math.Floor(h / seg);
                        for (var k = 0; k < m; k++)
                            env.Rect(x - w / 2, yb - (k + 1) * seg + gap / 2, w, seg - gap,
                                k > hmax / seg * 0.72 ? colB : col, a, false);
                    }
                    else env.Rect(x - w / 2, yb - h, w, h, col, a, false);
                    if (style == "mirror") env.Rect(x - w / 2, yb + lw * 2, w, h * 0.35, col, a * 0.22, false);
                }
                // 字母柱之间的细频谱柱
                if (thin > 0)
                    for (var j = 0; j <= cnt; j++)
                        for (var q = 1; q <= thin; q++)
                        {
                            if (j == cnt && q > 0) break;
                            var xi = j + q / (double)(thin + 1);
                            var x = x0 + xi * sw;
                            DrawBar(x, sw * 0.06,
                                hmax * 0.8 * Level(xi + r * 7 + 0.5, env.Ltb) * e * outK, Col(sc, "sub"), 0.55);
                        }
                for (var j = 0; j < cnt; j++)
                {
                    var i = r * per + j;
                    if (chs[i] == " ") continue;
                    var x = x0 + (j + 0.5) * sw;
                    var lv = Level(j + 0.5 + r * 7, env.Ltb);
                    var h = hmax * lv * e * outK;
                    var col = colK == "duo" ? j % 2 != 0 ? colB : colA : colK == "fg" ? Col(sc, "fg") : colA;
                    DrawBar(x, bw, h, col, colK == "fg" ? 0.35 : 0.9);
                    if (peaks)
                    {
                        var pk = 0.0;
                        for (var k = 0; k < 6; k++)
                        {
                            var tau = k * 0.1;
                            pk = Math.Max(pk, Level(j + 0.5 + r * 7, env.Ltb - tau) - tau * 0.55);
                        }
                        var ph = hmax * pk * e * outK;
                        env.Rect(x - bw / 2, yb - ph - Math.Max(3, sw * 0.05) - sw * 0.04, bw, Math.Max(3, sw * 0.05),
                            Col(sc, "fg"), 0.9 * outK * e, false);
                    }
                    bb = U(bb, JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = chs[i],
                        ["font"] = font,
                        ["size"] = size,
                        ["x"] = x,
                        ["y"] = yb - h - size * 0.62 - (peaks ? sw * 0.1 : 0),
                        ["color"] = Col(sc, "fg"),
                        ["mi"] = i,
                    }));
                }
            }
            env.Body = bb;
        },
    });

    // ---------------- tape（JS L2928-3096） ----------------

    private static void RegisterTape() => Reg0("tape", new LayoutDef
    {
        Name = "テープ",
        Tags = new[] { "pop", "editorial", "graphic" },
        W = 1,
        Treat = "safe",
        Fits = n => n >= 1 && n <= 16,
        Plan = (rng, cut, st) =>
        {
            var n = cut.N;
            var port = cut.H > cut.W;
            return new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display", "body")),
                ["fs"] = rng.Pick(FontsOf(st, "body", "serif")),
                ["variant"] = n > 9 || (port && n > 5)
                    ? rng.Pick(new[] { "stack", "stack", "single" })
                    : rng.Pick(new[] { "single", "cross", "stack" }),
                ["ang"] = rng.Range(4, 11) * rng.Pick(new[] { 1, -1 }),
                ["col"] = rng.Pick(new[] { "accent", "ink", "accent" }),
                ["piece"] = rng.Chance(0.75),
                ["lines"] = rng.Chance(0.6),
            };
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var Pm = env.P;
            var cut = env.Cut!;
            var s = (int)cut.Seed;
            var M = Math.Min(W, H);
            var lt = env.Lt;
            var font = S(Pm, "font")!;
            var fsFont = S(Pm, "fs")!;
            var variant = S(Pm, "variant")!;
            var port = W < H;
            var outK = OutK(env);
            var dark = J.Lum(Col(sc, "bg")) < 0.45;
            var colK = S(Pm, "col");
            var tapeC = PlateCol(sc, colK == "accent" ? Col(sc, "accent") : Col(sc, "ink"),
                colK == "accent" ? Col(sc, "ink") : Col(sc, "accent"));
            var tapeC2 = colK == "accent"
                ? PlateCol(sc, Col(sc, "ink"), Col(sc, "fg"))
                : PlateCol(sc, Col(sc, "accent"), Col(sc, "fg"));
            var txtC = OnCol(sc, tapeC);
            BBox? Strip(double x, double y, double ang, double L, double hh, string col, int k,
                double t0, double dur, Func<double, double, BBox?>? content)
            {
                var u = J.E.OutCubic(J.Clamp((lt - t0) / dur));
                if (u <= 0) return null;
                var vis = L * u;
                env.Ctx.Save();
                env.Ctx.Translate((float)x, (float)y);
                env.Ctx.RotateDegrees((float)ang); // JS: rotate(ang * DEG)
                env.Ctx.Translate((float)(-L / 2), 0);
                if (env.Pass == "main")
                {
                    env.Ctx.Save();
                    env.Ctx.ClipRect(new SKRect((float)-hh, (float)-hh, (float)(-hh + vis + hh * (u >= 1 ? 2 : 0)), (float)hh));
                    using (var path = TapePath(L, hh, s, k))
                    using (var p = new SKPaint { IsAntialias = true, Color = Env.SkColor(col, 0.9 * outK), BlendMode = env.Blend })
                        env.Ctx.DrawPath(path, p);
                    if (B(Pm, "lines"))
                    {
                        using var lp = new SKPaint { IsAntialias = true, Color = Env.SkColor(OnCol(sc, col), 0.07 * outK), BlendMode = env.Blend };
                        for (var j = 1; j < 6; j++)
                            env.Ctx.DrawRect(new SKRect(0, (float)(-hh / 2 + hh * j / 6), (float)L,
                                (float)(-hh / 2 + hh * j / 6 + Math.Max(1, hh * 0.012))), lp);
                    }
                    using (var ep = new SKPaint { IsAntialias = true, Color = Env.SkColor(dark ? "#000000" : "#FFFFFF", 0.12 * outK), BlendMode = env.Blend })
                    {
                        env.Ctx.DrawRect(new SKRect(0, (float)(-hh / 2), (float)L, (float)(-hh / 2 + hh * 0.08)), ep);
                        env.Ctx.DrawRect(new SKRect(0, (float)(hh / 2 - hh * 0.08), (float)L, (float)(hh / 2)), ep);
                    }
                    env.Ctx.Restore();
                }
                BBox? r = null;
                if (content != null)
                {
                    env.Ctx.Save();
                    env.Ctx.ClipRect(new SKRect((float)-hh, (float)(-hh * 2), (float)(-hh + vis + hh * (u >= 1 ? 2 : 0)), (float)(hh * 2)));
                    r = content(L / 2, 0);
                    env.Ctx.Restore();
                }
                env.Ctx.Restore();
                return r;
            }
            BBox? bb = null;
            var txt = cut.Text.Trim();
            if (variant == "stack")
            {
                var parts = (cut.Words is { Length: > 1 } ? cut.Words : new[] { txt })
                    .Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
                if (parts.Count > 4)
                {
                    var k = (int)Math.Ceiling(parts.Count / 4.0);
                    var q = new List<string>();
                    for (var i = 0; i < parts.Count; i += k)
                        q.Add(string.Concat(parts.Skip(i).Take(k)));
                    parts = q;
                }
                if (parts.Count == 1 && JLayouts.GlyphCount(txt) > (port ? 5 : 9))
                    parts = JLayouts.SplitLines(txt, (int)Math.Ceiling(JLayouts.GlyphCount(txt) / 2.0)).Split('\n').ToList();
                var np = parts.Count;
                var longest = parts.Aggregate(1.0, (a, p) => Math.Max(a,
                    JLayouts.Measure(new JObject { ["text"] = p, ["font"] = font, ["size"] = 100, ["track"] = 0.05 }).W / 100));
                var size = Math.Min(Math.Min(W * 0.72 / longest, H * 0.62 / (np * 1.75)), M * 0.17);
                var h = size * 1.5;
                for (var i = 0; i < parts.Count; i++)
                {
                    var p = parts[i];
                    var m = JLayouts.Measure(new JObject { ["text"] = p, ["font"] = font, ["size"] = size, ["track"] = 0.05 });
                    var L = m.W + size * 1.2;
                    var y = H / 2 + (i - (np - 1) / 2.0) * h * 1.12;
                    var x = W / 2 + J.Rs(s, i, 3) * W * 0.05;
                    var ang = (i % 2 != 0 ? -1 : 1) * Math.Abs(D(Pm, "ang")) * 0.45 + J.Rs(s, i, 4) * 1.5;
                    var col = i % 3 == 1 ? tapeC2 : tapeC;
                    var t0 = 0.03 + i * 0.12;
                    var r = Strip(x, y, ang, L, h, col, i, t0, 0.3, (cx2, cy2) => JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = p,
                        ["font"] = font,
                        ["size"] = size,
                        ["x"] = cx2,
                        ["y"] = cy2,
                        ["track"] = 0.05,
                        ["color"] = OnCol(sc, col),
                        ["noHold"] = PlateHold(env),
                        ["mi"] = MiAt(env, 0.05 + i * 0.12),
                    }));
                    if (r != null) bb = U(bb, BbRect(x - L / 2, y - h / 2, x + L / 2, y + h / 2));
                }
                env.Body = bb;
                return;
            }
            var mt = MainLines(txt, W, H, 11, 6);
            var size2 = Math.Min(JLayouts.FitSize(mt, font, W * 0.7, H * 0.3,
                new JObject { ["track"] = 0.05, ["lead"] = 1.15 }), H * 0.17);
            var mm = JLayouts.Measure(new JObject { ["text"] = mt, ["font"] = font, ["size"] = size2, ["track"] = 0.05, ["lead"] = 1.15 });
            var Lm = mm.W + size2 * 1.6;
            var hm = mm.H + size2 * 0.75;
            if (variant == "cross")
            {
                var unit = Regex.Replace(!string.IsNullOrEmpty(cut.LineText) ? cut.LineText : cut.Text, @"\s+", " ").Trim();
                var fs = hm * 0.26;
                var L2 = Math.Min(Math.Sqrt(W * W + H * H) * 0.9,
                    JLayouts.Measure(new JObject { ["text"] = unit, ["font"] = fsFont, ["size"] = fs, ["track"] = 0.1 }).W * 1.3 + fs * 6);
                Strip(W / 2 + Lm * 0.15, H / 2 + hm * 0.1, -D(Pm, "ang") * 2.4, L2, hm * 0.5, tapeC2, 9, 0.0, 0.4,
                    (cx2, cy2) => DrawIt(env, new JObject
                    {
                        ["text"] = unit,
                        ["font"] = fsFont,
                        ["size"] = fs,
                        ["track"] = 0.1,
                        ["x"] = cx2,
                        ["y"] = cy2,
                        ["color"] = OnCol(sc, tapeC2),
                        ["alpha"] = outK,
                        ["ghost"] = false,
                    }));
            }
            var r2 = Strip(W / 2, H / 2, D(Pm, "ang"), Lm, hm, tapeC, 0, 0.06, 0.34,
                (cx2, cy2) => JLayouts.MainDraw(env, new JObject
                {
                    ["text"] = mt,
                    ["font"] = font,
                    ["size"] = size2,
                    ["x"] = cx2,
                    ["y"] = cy2,
                    ["track"] = 0.05,
                    ["lead"] = 1.15,
                    ["color"] = txtC,
                    ["noHold"] = PlateHold(env),
                    ["mi"] = MiAt(env, 0.1),
                }));
            if (B(Pm, "piece"))
            {
                var ph = hm * 0.42;
                var pl = ph * 2.6;
                var angK = D(Pm, "ang");
                var ex = W / 2 + Math.Cos(angK * J.DEG) * Lm * 0.5;
                var ey = H / 2 + Math.Sin(angK * J.DEG) * Lm * 0.5;
                var rom = RomaOf(txt);
                var lab = rom != null
                    ? rom.Length > 12 ? rom[..12] : rom
                    : "No." + (Math.Max(0, cut.Line) + 1).ToString("D2", CultureInfo.InvariantCulture);
                Strip(ex - ph * 0.3, ey - ph * 0.4, angK - 38 * (angK == 0 ? 1 : Math.Sign(angK)), pl, ph, tapeC2, 5, 0.32, 0.18,
                    (cx2, cy2) => DrawIt(env, new JObject
                    {
                        ["text"] = lab,
                        ["font"] = MonoF(env),
                        ["size"] = Math.Min(ph * 0.34, pl * 0.7 / Math.Max(3, lab.Length) * 1.6),
                        ["track"] = 0.12,
                        ["x"] = cx2,
                        ["y"] = cy2,
                        ["color"] = OnCol(sc, tapeC2),
                        ["alpha"] = outK,
                        ["ghost"] = false,
                    }));
            }
            env.Body = r2 != null
                ? BbRect(W / 2 - Lm / 2, H / 2 - hm / 2, W / 2 + Lm / 2, H / 2 + hm / 2)
                : null;
        },
    });
}
