// 移植自 frontend/src/engine/11p_horror1.js（horror 包 1/3：layout 构图，12 条）。
//
// 契约与 P11LayoutsB 相同：fits(n) 筛台词；plan(rng, ctx, st) 抽参数存进 cut.params（键名
// 一字不改）；Draw(env) 绘制并写回 env.Body（JS render(env) 的返回值）。RNG 消费顺序逐行
// 对位（plan 里的 rng.pick/range/int/chance 的调用次序 + && 短路同样短路）；Draw 侧全走
// J.R/J.Rs/J.Rr/J.H 确定性哈希，同 seed 同轨迹。
//
// 跨文件符号对位：J.splitLines→JLayouts.SplitLines、J.fitSize→JLayouts.FitSize、
// J.measure→JLayouts.Measure、J.mainDraw→JLayouts.MainDraw、J.unionBB→JLayouts.UnionBb、
// J.glyphCount→JLayouts.GlyphCount、J.fontsOf→JLayouts.FontsOf、J.FONTS.klee→JFonts.Table、
// J.layoutText→JText.LayoutText、J.pool→JLang.Pool、J.drawItem→env.DrawItem（本文件 DrawIt 包装）、
// J.romaji/lum/contrast/mix/fitContrast/h/r/rs/rr/noise1/clamp/lerp/smooth→J.*。
//
// extra/set 标记：JS 条目上的 `set: 'horror'` 已落 IRegEntry.Set 真字段（Reg0 统一赋值）；`ae: 'xxx'`
// （AE 导出映射，Export.js 未移植）在 IRegEntry/LayoutDef 无对应字段，C# 不落表——
// 等 Sets/AE 导出移植时再补。见移植汇报。
//
// 本文件私有 helper（JS L9-107 的顶层 const）：U/IsPort/Strip/HasLatin/Flat/BodyF/MonoF/
// HandOf/Tin/Tout/Bb/Pad2/MiAt/IsDark/LightOf/DarkOf/NightC/OnCol/RedOn/FakeDate/FakeTime/
// Slots/VeilHole/NoiseCv/DrawNoise。JS 的 shaky（L104）在源文件里从未被调用，不移植。
// Canvas 渐变 → SKShader（CreateRadialGradient 两点式/CreateLinearGradient）；噪点纹理
// canvas → SKBitmap（确定性 J.R 填充，4 张缓存，DrawImage 源矩形裁切 + Nearest 采样）。

using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace JizuraLive.Engine;

public static class P11Horror1
{
    private const string P = "horror1";
    private static int _installed;

    private static void Reg0(string key, LayoutDef def)
    {
        def.Set = "horror"; // JS reg 助手：Object.assign({set: 'horror'}, def)
        Reg.Register("layout", key, def, P);
    }

    // ================= 本包 helper（JS L9-107 逐条对位） =================

    private static double U(Env env) => Math.Min(env.W, env.H);
    private static bool IsPort(Env env) => env.H > env.W * 1.08;

    /// <summary>Math.hypot 对位（.NET BCL 无 Math.Hypot）。</summary>
    private static double Hypot(double a, double b) => Math.Sqrt(a * a + b * b);

    /// <summary>两点式 CreateRadialGradient 在本 SkiaSharp 版本收 SKColorF[]。</summary>
    private static SKColorF Cf(string hex, double a = 1)
    {
        var c = Env.SkColor(hex, a);
        return new SKColorF(c.Red / 255f, c.Green / 255f, c.Blue / 255f, c.Alpha / 255f);
    }

    /// <summary>
    /// JS createRadialGradient(x, y, r0, x, y, r1)（同心两点式）→ 单心 CreateRadialGradient。
    /// SkiaSharp 4.x 移除了两点式：r0 以内延续首色，[r0, r1] 线性映射 stops 的位置。
    /// </summary>
    private static SKShader Radial2(double cx, double cy, double r0, double r1, (double Pos, SKColorF Col)[] stops)
    {
        var k = r1 > r0 ? r0 / r1 : 0;
        var cols = new SKColorF[stops.Length + 1];
        var poss = new float[stops.Length + 1];
        cols[0] = stops[0].Col;
        poss[0] = 0f;
        for (var i = 0; i < stops.Length; i++)
        {
            cols[i + 1] = stops[i].Col;
            poss[i + 1] = (float)(k + stops[i].Pos * (1 - k));
        }

        return SKShader.CreateRadialGradient(new SKPoint((float)cx, (float)cy), (float)Math.Max(0.001, r1),
            cols, null, poss, SKShaderTileMode.Clamp);
    }

    /// <summary>strip(t)：去掉所有空白。</summary>
    private static string Strip(string? t) => Regex.Replace(t ?? "", @"\s+", "");

    private static bool HasLatin(string? t) => Regex.IsMatch(t ?? "", "[A-Za-z]");

    /// <summary>flat(t)：含拉丁字母折行内空白为单空格，否则全剥掉。JS L13。</summary>
    private static string Flat(string? t) =>
        HasLatin(t) ? Regex.Replace((t ?? "").Trim(), @"\s+", " ") : Strip(t);

    private static string[] FontsOf(StyleDef st, params string[] roles) => JLayouts.FontsOf(st, roles);

    /// <summary>bodyF(env)：(st.fonts.body[0]) || 'gothic_med'。JS L15。</summary>
    private static string BodyF(Env env) => JLayouts.Font0(env.St, "body") ?? "gothic_med";

    /// <summary>monoF(env)：(st.fonts.mono[0]) || 'mono'。JS L16。</summary>
    private static string MonoF(Env env) => JLayouts.Font0(env.St, "mono") ?? "mono";

    /// <summary>handOf(rng, st)：J.FONTS.klee && rng.chance(0.75) ? 'klee' : …。JS L17。</summary>
    private static string HandOf(Rng rng, StyleDef st) =>
        JFonts.Table.ContainsKey("klee") && rng.Chance(0.75) ? "klee" : rng.Pick(FontsOf(st, "body", "serif"));

    /// <summary>tin(env, d, len)：outExpo 进场。JS L18（ease 参数在源内未改用过）。</summary>
    private static double Tin(Env env, double d = 0, double len = 0.4) =>
        J.E.OutCubic(J.Clamp((env.Lt - d) / Math.Max(0.01, len)));

    /// <summary>tout(env)：退场剩余量 1 - inCubic(pOut)。JS L19。</summary>
    private static double Tout(Env env) => 1 - J.E.InCubic(env.POut);

    private static BBox Bb(double x0, double y0, double x1, double y1) => new()
    {
        X0 = x0,
        Y0 = y0,
        X1 = x1,
        Y1 = y1,
        Cx = (x0 + x1) / 2,
        Cy = (y0 + y1) / 2,
        Boxes = new List<JObject>(),
    };

    private static BBox? UB(BBox? a, BBox? b) => JLayouts.UnionBb(a, b);

    private static string Pad2(int n) => n.ToString(CultureInfo.InvariantCulture).PadLeft(2, '0');

    /// <summary>miAt(env, t)：让 mainDraw 从本地时间 t 起跳该字入场。JS L23。</summary>
    private static double MiAt(Env env, double t)
    {
        var sg = env.Cut?.Stagger ?? 0;
        return Math.Max(0, t) / Math.Max(0.005, sg != 0 ? sg : 0.04);
    }

    private static bool IsDark(string? c) => J.Lum(c ?? "#000000") < 0.45;

    private static string LightOf(Scheme sc) =>
        J.Lum(ScK(sc, "fg")) > J.Lum(ScK(sc, "bg")) ? ScK(sc, "fg") : ScK(sc, "bg");

    private static string DarkOf(Scheme sc) =>
        J.Lum(ScK(sc, "fg")) > J.Lum(ScK(sc, "bg")) ? ScK(sc, "bg") : ScK(sc, "fg");

    /// <summary>nightC(sc)：这个配色下"黑暗"用的颜色。JS L28。</summary>
    private static string NightC(Scheme sc) => J.Mix(ScK(sc, "bg"), "#000000", IsDark(ScK(sc, "bg")) ? 0.72 : 0.9);

    /// <summary>配色读取兜底（JS 直接读 sc.xxx；缺字段时给黑白系兜底色）。</summary>
    private static string ScK(Scheme sc, string k) => sc.Str(k) ?? k switch
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

    /// <summary>onCol(sc, fill)：底板上可读性最好的配色颜色。JS L30-41。</summary>
    private static string OnCol(Scheme sc, string fill)
    {
        string? best = null;
        var bv = 0.0;
        foreach (var c in new[] { ScK(sc, "bg"), ScK(sc, "fg"), ScK(sc, "ink"), ScK(sc, "sub"), ScK(sc, "accent") })
        {
            if (string.IsNullOrEmpty(c) || c == fill) continue;
            var k = J.Contrast(c, fill);
            if (k > bv)
            {
                bv = k;
                best = c;
            }
        }

        return bv >= 2.6 && best != null ? best : J.Lum(fill) > 0.5 ? "#111111" : "#FFFFFF";
    }

    /// <summary>redOn(sc, fill)：红笔在底板上的可读红。JS L42。</summary>
    private static string RedOn(Scheme sc, string fill) => J.FitContrast(ScK(sc, "accent"), fill, 2.4);

    /// <summary>fakeDate(seed, sep)：由 cut seed 生成的日期串（纯 ASCII）。JS L44-47。</summary>
    private static string FakeDate(int seed, string sep = "/")
    {
        var y = 1987 + (int)(J.H(seed, 1) % 19);
        var m = 1 + (int)(J.H(seed, 2) % 12);
        var d = 1 + (int)(J.H(seed, 3) % 28);
        return y.ToString(CultureInfo.InvariantCulture) + sep + Pad2(m) + sep + Pad2(d);
    }

    /// <summary>fakeTime(seed, t)：由 cut seed 生成、随 t 走的时钟串。JS L48-51。</summary>
    private static string FakeTime(int seed, double t)
    {
        var s0 = 2 * 3600 + (int)(J.H(seed, 4) % 7200) + (int)Math.Floor(Math.Max(0, t));
        return Pad2((int)Math.Floor(s0 / 3600.0) % 24) + ":" + Pad2((int)Math.Floor(s0 / 60.0) % 60) + ":" + Pad2(s0 % 60);
    }

    /// <summary>slots(it)：排版后逐字的中心点（跳过空格）。JS L53-60。</summary>
    private sealed class Slot
    {
        public string Ch = "";
        public double X, Y, W, H;
        public int Li;
    }

    private static List<Slot> Slots(JObject it)
    {
        var lay = JText.LayoutText(it);
        var sx = JLayouts.D(it, "sx");
        if (sx == 0) sx = 1;
        var sy = JLayouts.D(it, "sy");
        if (sy == 0) sy = 1;
        var x = JLayouts.D(it, "x");
        var y = JLayouts.D(it, "y");
        var list = new List<Slot>();
        foreach (var g in lay.Cells)
        {
            if (g.Ch is " " or "　") continue;
            list.Add(new Slot
            {
                Ch = g.Ch,
                X = x + g.X * sx,
                Y = y + g.Y * sy,
                W = g.W * sx,
                H = g.H * sy,
                Li = g.Li,
            });
        }

        return list;
    }

    /// <summary>env.draw 对位：直画一条 item（可挂私有 charFn；用完恢复 env 桥字段）。</summary>
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

    /// <summary>veilHole：带软孔洞的全屏遮罩，只在 main pass 画。JS L62-75。</summary>
    private static void VeilHole(Env env, double cx, double cy, double r, double a, string col,
        double inner = 0.35, double ry = 1)
    {
        if (env.Pass != "main" || a <= 0.002) return;
        double W = env.W, H = env.H;
        var ctx = env.Ctx;
        ctx.Save();
        ctx.Translate((float)cx, (float)cy);
        ctx.Scale(1, (float)ry);
        using var shader = Radial2(0, 0, Math.Max(0.1, r * inner), Math.Max(1, r),
            new[] { (0.0, Cf(col, 0)), (0.55, Cf(col, 0.3 * a)), (1.0, Cf(col, a)) });
        using var p = new SKPaint { IsAntialias = true, Shader = shader };
        ctx.DrawRect(new SKRect((float)(-W * 2 - cx), (float)(-(H * 2 + cy) / ry),
            (float)(-W * 2 - cx + W * 5), (float)(-(H * 2 + cy) / ry + H * 5 / ry)), p);
        ctx.Restore();
    }

    // 噪点纹理块：只建一次，小尺寸、确定性（JS L77-93）。JS 的 4 张 canvas → SKBitmap 缓存。
    private static readonly SKBitmap?[] Noise = new SKBitmap?[4];

    private static SKBitmap NoiseCv(int k)
    {
        k = ((k % 4) + 4) % 4;
        if (Noise[k] is { } hit) return hit;
        const int w = 96, h = 72;
        var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bytes = bmp.Bytes; // Bgra8888：B,G,R,A 逐像素
        for (var i = 0; i < w * h; i++)
        {
            var v = (byte)(Math.Pow(J.R(i, k, 4411), 1.4) * 255);
            bytes[i * 4] = v;
            bytes[i * 4 + 1] = v;
            bytes[i * 4 + 2] = v;
            bytes[i * 4 + 3] = 255;
        }

        bmp.NotifyPixelsChanged();
        Noise[k] = bmp;
        return bmp;
    }

    private static readonly SKSamplingOptions SampNearest = new(SKFilterMode.Nearest, SKMipmapMode.None);

    /// <summary>drawNoise：噪点纹理块裁切贴片（imageSmoothingEnabled=false → Nearest）。JS L94-103。</summary>
    private static void DrawNoise(Env env, double x, double y, double w, double h, double a, int step, int seed)
    {
        if (env.Pass != "main" || a <= 0.003 || w < 1 || h < 1) return;
        var n = NoiseCv(step + seed);
        var sx = (int)Math.Floor(J.R(seed, step, 1) * 32);
        var sy = (int)Math.Floor(J.R(seed, step, 2) * 24);
        using var img = SKImage.FromBitmap(n);
        using var p = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(J.Clamp(a) * 255)) };
        env.Ctx.DrawImage(img,
            new SKRect(sx, sy, n.Width - sx, n.Height - sy),
            new SKRect((float)x, (float)y, (float)(x + w), (float)(y + h)), SampNearest, p);
    }

    // ================= 注册（JS 源 reg(...) 出现序） =================

    /// <summary>幂等注册本文件全部 layout（顺序 = JS 源 J.register 调用序；不挂静态构造函数）。</summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;
        RegisterHrFlashlight(); // JS L109
        RegisterHrDoorGap();    // JS L180
        RegisterHrWallScrawl(); // JS L249
        RegisterHrCctv();       // JS L340
        RegisterHrOuija();      // JS L500
        RegisterHrMissing();    // JS L663
        RegisterHrWrongOne();   // JS L813
        RegisterHrRisingDark(); // JS L885
        RegisterHrRedacted();   // JS L953
        RegisterHrStaticTv();   // JS L1077
        RegisterHrSpiritPhoto();// JS L1163
        RegisterHrWrongShadow();// JS L1277
    }

    // ---------------- hrFlashlight（JS L109-178） ----------------

    private static void RegisterHrFlashlight() => Reg0("hrFlashlight", new LayoutDef
    {
        Name = "懐中電灯",
        Tags = new[] { "horror", "emotional" },
        W = 1,
        Busy = true,
        // JS: ae:'circle'（IRegEntry 无对应字段）
        Fits = n => n >= 1 && n <= 18,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(rng.Chance(0.6) ? FontsOf(st, "serif", "display") : FontsOf(st, "display")),
            ["path"] = rng.Pick(new[] { "read", "read", "search" }),
            ["rk"] = rng.Range(0.85, 1.1),
            ["dark"] = rng.Range(0.9, 0.96),
            ["dust"] = rng.Chance(0.7),
            ["sx"] = rng.Range(-0.3, 0.3),
            ["sy"] = rng.Range(-0.3, 0.3),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var cut = env.Cut!;
            var Pm = env.P;
            var lt = env.Lt;
            var s = unchecked((int)cut.Seed);
            var text = JLayouts.SplitLines(Flat(cut.Text), IsPort(env) ? 5 : 10);
            var o = new JObject { ["track"] = 0.06, ["lead"] = 1.25 };
            var size = Math.Min(JLayouts.FitSize(text, JLayouts.S(Pm, "font")!, W * 0.8, H * 0.42, o), H * 0.24);
            var it = new JObject
            {
                ["text"] = text,
                ["font"] = JLayouts.S(Pm, "font"),
                ["size"] = size,
                ["x"] = W / 2.0,
                ["y"] = H / 2.0,
                ["track"] = 0.06,
                ["lead"] = 1.25,
                ["color"] = ScK(sc, "fg"),
            };
            var bb = JLayouts.MainDraw(env, it);
            // 光束按阅读顺序逐字扫过，最后放大照亮整行
            var sl = Slots((JObject)it.DeepClone());
            var m = JLayouts.Measure(it);
            var R0 = Math.Max(size * 0.9 * JLayouts.D(Pm, "rk"), U(env) * 0.1);
            var Rall = Hypot(m.W, m.H) * 0.55 + size * 0.5;
            var dur = cut.Dur;
            var t0 = JLayouts.S(Pm, "path") == "search" ? dur * 0.22 : 0.1;
            var t1 = dur * 0.72;
            var u = J.Clamp((lt - t0) / Math.Max(0.2, t1 - t0));
            double bx, by;
            if (sl.Count == 0)
            {
                bx = W / 2.0;
                by = H / 2.0;
            }
            else
            {
                var f = u * (sl.Count - 1);
                var i = Math.Min(sl.Count - 2, (int)Math.Floor(f));
                var k = J.E.InOutSine(f - Math.Max(0, i));
                var a = sl[Math.Max(0, i)];
                var b = sl[Math.Min(sl.Count - 1, i + 1)];
                bx = sl.Count == 1 ? a.X : J.Lerp(a.X, b.X, k);
                by = sl.Count == 1 ? a.Y : J.Lerp(a.Y, b.Y, k);
            }

            if (JLayouts.S(Pm, "path") == "search" && lt < t0)
            {
                // 先在黑暗里乱晃，再找到字
                var q = J.E.InOutSine(J.Clamp(lt / t0));
                var sx = W * (0.5 + JLayouts.D(Pm, "sx")) + J.Noise1(lt * 1.3, s) * W * 0.25;
                var sy = H * (0.5 + JLayouts.D(Pm, "sy")) + J.Noise1(lt * 1.1, s + 3) * H * 0.2;
                bx = J.Lerp(sx, bx, q * q);
                by = J.Lerp(sy, by, q * q);
            }

            var tr = env.Ltb;
            bx += (J.Noise1(tr * 2.2, s + 5) * 0.7 + J.Noise1(tr * 7, s + 6) * 0.3) * size * 0.18;
            by += (J.Noise1(tr * 1.9, s + 7) * 0.7 + J.Noise1(tr * 6.3, s + 8) * 0.3) * size * 0.14;
            var open = J.E.InOutCubic(J.Clamp((lt - t1) / 0.7));
            bx = J.Lerp(bx, W / 2.0, open);
            by = J.Lerp(by, H / 2.0, open);
            var R = J.Lerp(R0, Math.Max(R0, Rall), open) * (0.3 + 0.7 * Tin(env, 0, 0.35));
            // 电池快没电：偶发骤暗
            var dip = J.R(s, (int)env.Step, 31) < 0.05 ? 0.55 : 1;
            var dark = JLayouts.D(Pm, "dark") * Tin(env, 0, 0.25) * (1 - J.E.InCubic(env.POut) * 0.6);
            VeilHole(env, bx, by, R * dip, dark, NightC(sc), 0.32, 0.86);
            if (JLayouts.B(Pm, "dust") && env.Pass == "main")
            {
                for (var k = 0; k < 14; k++)
                {
                    var ph = J.R(s, k, 41) * 10;
                    var px = bx + J.Noise1(tr * 0.3 + ph, s + k) * R * 0.8;
                    var py = by + J.Noise1(tr * 0.25 + ph + 4, s + k + 50) * R * 0.7;
                    var r = U(env) * J.Rr(0.0012, 0.003, s, k, 42);
                    env.Circle(px, py, r, ScK(sc, "fg"), null, 0,
                        0.35 * dark * (0.5 + 0.5 * Math.Sin(tr * 2 + ph)), false);
                }
            }

            env.Body = bb;
        },
    });

    // ---------------- hrDoorGap（JS L180-247） ----------------

    private static void RegisterHrDoorGap() => Reg0("hrDoorGap", new LayoutDef
    {
        Name = "扉の隙間",
        Tags = new[] { "horror", "editorial" },
        W = 0.9,
        Busy = true,
        Fits = n => n >= 1 && n <= 16,
        Plan = (rng, cut, st) =>
        {
            var n = JLayouts.GlyphCount(cut.Text);
            return new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "serif", "display")),
                // JS: n <= 7 && !hasLatin && rng.chance(0.75)——短路：n>7 时不消耗 rng
                ["vert"] = n <= 7 && !HasLatin(cut.Text) && rng.Chance(0.75),
                ["side"] = rng.Pick(new[] { -1, 1 }),
                ["pause"] = rng.Range(0.16, 0.26),
                ["wedge"] = rng.Chance(0.8),
            };
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var cut = env.Cut!;
            var Pm = env.P;
            var lt = env.Lt;
            var s = unchecked((int)cut.Seed);
            var vert = JLayouts.B(Pm, "vert");
            var text = vert ? Strip(cut.Text) : JLayouts.SplitLines(Flat(cut.Text), IsPort(env) ? 5 : 11);
            var o = new JObject { ["track"] = 0.08, ["lead"] = 1.2, ["vertical"] = vert };
            var size = vert
                ? Math.Min(JLayouts.FitSize(text, JLayouts.S(Pm, "font")!, W * 0.3, H * 0.74, o),
                    Math.Min(W * 0.2, H * 0.2))
                : Math.Min(JLayouts.FitSize(text, JLayouts.S(Pm, "font")!, W * 0.78, H * 0.36, o), H * 0.2);
            var it = new JObject
            {
                ["text"] = text,
                ["font"] = JLayouts.S(Pm, "font"),
                ["size"] = size,
                ["x"] = W / 2.0,
                ["y"] = H / 2.0,
                ["color"] = ScK(sc, "fg"),
                ["track"] = 0.08,
                ["lead"] = 1.2,
                ["vertical"] = vert,
            };
            var m = JLayouts.Measure(it);
            // 开门：先一条细缝，不安地停顿，再开到能读；退场时猛地关上
            var full = (vert ? m.W : m.H) + size * 0.9;
            var d = cut.Dur;
            var a1 = J.Clamp(lt / 0.5);
            var a2 = J.Clamp((lt - Math.Max(0.35, d * JLayouts.D(Pm, "pause"))) / 0.6);
            var open = 0.18 * J.E.OutCubic(a1) + 0.82 * J.E.InOutCubic(a2);
            open += J.Rs(s, (int)env.Step, 11) * 0.015 * (a2 > 0 && a2 < 1 ? 1 : 0);
            open *= 1 - J.E.InExpo(env.POut);
            var g = Math.Max(0, full * open);
            var bb = JLayouts.MainDraw(env, it);
            if (env.Pass != "main")
            {
                env.Body = bb;
                return;
            }

            var dk = NightC(sc);
            var L = LightOf(sc);
            var cx = W / 2.0;
            var cy = H / 2.0;
            // ctx.rect(整屏) + ctx.rect(缝) evenodd 填充 → SKPath FillType.EvenOdd
            using (var path = new SKPath { FillType = SKPathFillType.EvenOdd })
            {
                path.AddRect(new SKRect((float)-W, (float)-H, (float)(W * 2), (float)(H * 2)));
                if (vert)
                {
                    var h = m.H + size * 1.6;
                    path.AddRect(new SKRect((float)(cx - g / 2), (float)(cy - h / 2), (float)(cx + g / 2), (float)(cy + h / 2)));
                }
                else
                {
                    var w = m.W + size * 1.6;
                    path.AddRect(new SKRect((float)(cx - w / 2), (float)(cy - g / 2), (float)(cx + w / 2), (float)(cy + g / 2)));
                }

                using var p = new SKPaint { IsAntialias = true, Color = Env.SkColor(dk, 0.97 * Tin(env, 0, 0.2)) };
                env.Ctx.DrawPath(path, p);
            }

            if (g > 1)
            {
                // 缝隙边缘的亮边 + 洒向地面的光
                var rimA = 0.55 * Math.Min(1, g / (size * 0.3));
                if (vert)
                {
                    var h = m.H + size * 1.6;
                    var y0 = cy - h / 2;
                    var y1 = cy + h / 2;
                    env.Line(new[] { new[] { cx - g / 2, y0 }, new[] { cx - g / 2, y1 } }, L, Math.Max(1, size * 0.02), rimA, false);
                    env.Line(new[] { new[] { cx + g / 2, y0 }, new[] { cx + g / 2, y1 } }, L, Math.Max(1, size * 0.02), rimA * 0.6, false);
                    if (JLayouts.B(Pm, "wedge"))
                        env.Poly(new[]
                        {
                            new[] { cx - g / 2, y1 }, new[] { cx + g / 2, y1 },
                            new[] { cx + g * 2.4 + W * 0.08 * JLayouts.D(Pm, "side"), H * 1.05 },
                            new[] { cx - g * 1.6 + W * 0.08 * JLayouts.D(Pm, "side"), H * 1.05 },
                        }, L, 0.07 * Math.Min(1, g / size), false);
                }
                else
                {
                    var w = m.W + size * 1.6;
                    var x0 = cx - w / 2;
                    var x1 = cx + w / 2;
                    env.Line(new[] { new[] { x0, cy - g / 2 }, new[] { x1, cy - g / 2 } }, L, Math.Max(1, size * 0.02), rimA * 0.6, false);
                    env.Line(new[] { new[] { x0, cy + g / 2 }, new[] { x1, cy + g / 2 } }, L, Math.Max(1, size * 0.02), rimA, false);
                }
            }

            env.Body = bb;
        },
    });

    // ---------------- hrWallScrawl（JS L249-338） ----------------

    private static void RegisterHrWallScrawl() => Reg0("hrWallScrawl", new LayoutDef
    {
        Name = "壁の落書き",
        Tags = new[] { "horror", "glitch" },
        W = 0.9,
        Busy = true,
        Fits = n => n >= 1 && n <= 14,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
            ["hand"] = HandOf(rng, st),
            ["rows"] = (double)rng.Int(7, 10),
            ["mainHand"] = rng.Chance(0.4),
            ["red"] = rng.Range(0.06, 0.16),
            ["rise"] = rng.Range(0.55, 0.8),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var cut = env.Cut!;
            var Pm = env.P;
            var s = unchecked((int)cut.Seed);
            var lt = env.Lt;
            var unit = Flat(cut.Text);
            var mainFont = JLayouts.B(Pm, "mainHand") ? JLayouts.S(Pm, "hand")! : JLayouts.S(Pm, "font")!;
            var mo = new JObject { ["track"] = 0.04, ["lead"] = 1.1 };
            var mt = JLayouts.SplitLines(unit, IsPort(env) ? 5 : 10);
            var size = Math.Min(JLayouts.FitSize(mt, mainFont, W * 0.8, H * 0.3, mo), H * 0.2);
            var mm = JLayouts.Measure(new JObject
            {
                ["text"] = mt,
                ["font"] = mainFont,
                ["size"] = size,
                ["track"] = 0.04,
                ["lead"] = 1.1,
            });
            var clear = (x0: W / 2 - mm.W / 2 - size * 0.4, x1: W / 2 + mm.W / 2 + size * 0.4,
                y0: H / 2 - mm.H / 2 - size * 0.35, y1: H / 2 + mm.H / 2 + size * 0.35);
            // 一行接一行地抄写，越来越密
            var rows = (int)JLayouts.D(Pm, "rows");
            var rh = H / rows;
            var fs = rh * 0.58;
            var cw = JLayouts.Measure(new JObject { ["text"] = unit, ["font"] = JLayouts.S(Pm, "hand"), ["size"] = fs, ["track"] = 0.02 }).W;
            var slotsL = new List<(double X, double Y, double Sz, int R, int K)>();
            var gN = Math.Max(1, JLayouts.GlyphCount(unit));
            for (var r = 0; r < rows && slotsL.Count < 240; r++)
            {
                var y = (r + 0.5) * rh + J.Rs(s, r, 1) * rh * 0.12;
                var x = W * 0.03 + J.R(s, r, 2) * fs * 2;
                for (var k = 0; k < 16 && x < W * 0.97; k++)
                {
                    var sz = fs * J.Rr(0.75, 1.3, s, r, k, 3);
                    var w = cw * sz / fs;
                    var cxk = x + w / 2;
                    if (!(y + sz * 0.6 > clear.y0 && y - sz * 0.6 < clear.y1 && x + w > clear.x0 && x < clear.x1) && x + w < W * 1.02)
                        slotsL.Add((cxk, y, sz, r, k));
                    x += w + fs * J.Rr(0.4, 1.4, s, r, k, 4);
                }
            }

            var lim = Math.Max(8, Math.Min(64, (int)Math.Floor(420 / (double)gN)));
            if (slotsL.Count > lim)
            {
                var st2 = slotsL.Count / (double)lim;
                slotsL = Enumerable.Range(0, lim).Select(i => slotsL[(int)Math.Floor(i * st2)]).ToList();
            }

            var K = slotsL.Count;
            var T = Math.Max(0.6, cut.Dur * JLayouts.D(Pm, "rise"));
            var @out = Tout(env);
            var glyphBudget = 420;
            for (var q = 0; q < K; q++)
            {
                var S = slotsL[q];
                var ta = T * Math.Pow(q / Math.Max(1, K), 0.62);
                var e = J.Clamp((env.Ltb - ta) / 0.3);
                if (e <= 0) break;
                glyphBudget -= gN;
                if (glyphBudget < 0) break;
                var shown = (int)Math.Ceiling(e * gN);
                var seed = unchecked((int)J.H(s, q, 9));
                var red = J.R(seed, 5) < JLayouts.D(Pm, "red");
                DrawIt(env, new JObject
                {
                    ["text"] = unit,
                    ["font"] = JLayouts.S(Pm, "hand"),
                    ["size"] = S.Sz,
                    ["x"] = S.X,
                    ["y"] = S.Y,
                    ["track"] = 0.02,
                    ["rot"] = J.Rs(seed, 6) * 5,
                    ["color"] = red ? ScK(sc, "accent") : ScK(sc, "sub"),
                    ["alpha"] = (red ? 0.7 : 0.42) * @out,
                    ["ghost"] = false,
                }, (i, _, _) => i >= shown
                    ? new CharXf { Hide = true }
                    : new CharXf
                    {
                        Dy = J.Rs(seed, i, 7) * S.Sz * 0.12,
                        Rot = J.Rs(seed, i, 8) * 9,
                        S = 1 + J.Rs(seed, i, 10) * 0.12,
                    });
            }

            env.Body = JLayouts.MainDraw(env, new JObject
            {
                ["text"] = mt,
                ["font"] = mainFont,
                ["size"] = size,
                ["x"] = W / 2.0,
                ["y"] = H / 2.0,
                ["track"] = 0.04,
                ["lead"] = 1.1,
                ["color"] = ScK(sc, "fg"),
            });
        },
    });

    // ---------------- hrCctv（JS L340-496） ----------------

    private static void RegisterHrCctv() => Reg0("hrCctv", new LayoutDef
    {
        Name = "監視モニター",
        Tags = new[] { "horror", "glitch", "editorial" },
        W = 1,
        Busy = true,
        Treat = "safe",
        Fits = n => n >= 1 && n <= 18,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "display", "body")),
            ["v"] = rng.Pick(new[] { "quad", "quad", "single" }),
            ["act"] = (double)rng.Int(0, 3),
            ["cam0"] = (double)rng.Int(1, 12),
            ["box"] = rng.Chance(0.7),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var cut = env.Cut!;
            var Pm = env.P;
            var s = unchecked((int)cut.Seed);
            var lt = env.Lt;
            var st = (int)env.Step;
            var mono = MonoF(env);
            var port = IsPort(env);
            var @out = Tout(env);
            var inA = Tin(env, 0, 0.2);
            var fs = J.Clamp(U(env) * 0.022, 11, 28);
            var m = U(env) * 0.03;
            var lw = Math.Max(1, U(env) * 0.0016);
            var feedBg = J.Mix(DarkOf(sc), "#000000", 0.35);
            var LC = LightOf(sc);
            var frameC = J.Mix(feedBg, LC, 0.5);
            var date = FakeDate(s);
            var time = FakeTime(s, lt);
            var accC = J.FitContrast(ScK(sc, "accent"), feedBg, 3);

            void Osd(double x0, double y0, double x1, double y1, int n, bool live, double? bigFs = null)
            {
                var f = bigFs ?? fs;
                DrawIt(env, new JObject
                {
                    ["text"] = "CAM " + Pad2(n),
                    ["font"] = mono,
                    ["size"] = f,
                    ["align"] = "left",
                    ["x"] = x0 + f * 0.8,
                    ["y"] = y0 + f * 1.2,
                    ["color"] = LC,
                    ["alpha"] = 0.85 * @out * inA,
                    ["ghost"] = false,
                    ["track"] = 0.1,
                });
                DrawIt(env, new JObject
                {
                    ["text"] = date + " " + time,
                    ["font"] = mono,
                    ["size"] = f * 0.9,
                    ["align"] = "right",
                    ["x"] = x1 - f * 0.8,
                    ["y"] = y1 - f * 1.1,
                    ["color"] = LC,
                    ["alpha"] = 0.75 * @out * inA,
                    ["ghost"] = false,
                    ["track"] = 0.06,
                });
                if (!live) return;
                if (st % 4 < 2)
                    env.Circle(x1 - f * 3.9, y0 + f * 1.2, f * 0.32, accC, null, 0, @out * inA, false);
                DrawIt(env, new JObject
                {
                    ["text"] = "REC",
                    ["font"] = mono,
                    ["size"] = f,
                    ["align"] = "left",
                    ["x"] = x1 - f * 3.3,
                    ["y"] = y0 + f * 1.2,
                    ["color"] = LC,
                    ["alpha"] = 0.85 * @out * inA,
                    ["ghost"] = false,
                });
            }

            void EmptyFeed(double x0, double y0, double x1, double y1, int k)
            {
                env.Rect(x0, y0, x1 - x0, y1 - y0, feedBg, inA, false);
                var cx = (x0 + x1) / 2 + J.Rs(s, k, 3) * (x1 - x0) * 0.12;
                var cy = (y0 + y1) / 2 + J.Rs(s, k, 4) * (y1 - y0) * 0.08;
                var w = (x1 - x0) * 0.16;
                var h = (y1 - y0) * 0.2;
                // 从门上方俯视的走廊
                var c = J.Mix(feedBg, LC, 0.25);
                var a = 0.8 * inA * @out;
                env.Line(new[] { new[] { x0, y0 }, new[] { cx - w, cy - h } }, c, lw, a, false);
                env.Line(new[] { new[] { x1, y0 }, new[] { cx + w, cy - h } }, c, lw, a, false);
                env.Line(new[] { new[] { x0, y1 }, new[] { cx - w, cy + h } }, c, lw, a, false);
                env.Line(new[] { new[] { x1, y1 }, new[] { cx + w, cy + h } }, c, lw, a, false);
                env.Line(new[]
                {
                    new[] { cx - w, cy - h }, new[] { cx + w, cy - h }, new[] { cx + w, cy + h },
                    new[] { cx - w, cy + h }, new[] { cx - w, cy - h },
                }, c, lw, a, false);
                DrawNoise(env, x0, y0, x1 - x0, y1 - y0, 0.07 * inA * @out, st, s + k);
                if (J.R(s, k, 5) < 0.35)
                    DrawIt(env, new JObject
                    {
                        ["text"] = "NO SIGNAL",
                        ["font"] = mono,
                        ["size"] = fs * 1.1,
                        ["x"] = (x0 + x1) / 2,
                        ["y"] = (y0 + y1) / 2,
                        ["color"] = J.Mix(feedBg, LC, 0.6),
                        ["alpha"] = (st % 6 < 4 ? 0.8 : 0.3) * inA * @out,
                        ["ghost"] = false,
                        ["track"] = 0.2,
                    });
            }

            BBox? bb = null;
            var fx0 = m;
            var fy0 = m;
            var fx1 = W - m;
            var fy1 = H - m;
            if (JLayouts.S(Pm, "v") == "quad")
            {
                var g = lw * 3;
                const int cols = 2, rowsN = 2;
                var cw = (W - m * 2 - g) / cols;
                var chh = (H - m * 2 - g) / rowsN;
                for (var k = 0; k < 4; k++)
                {
                    var c = k % 2;
                    var r = (int)Math.Floor(k / 2.0);
                    var x0 = m + c * (cw + g);
                    var y0 = m + r * (chh + g);
                    if (k == (int)JLayouts.D(Pm, "act") % 4)
                    {
                        fx0 = x0;
                        fy0 = y0;
                        fx1 = x0 + cw;
                        fy1 = y0 + chh;
                        env.Rect(x0, y0, cw, chh, feedBg, inA, false);
                        continue;
                    }

                    EmptyFeed(x0, y0, x0 + cw, y0 + chh, k);
                    Osd(x0, y0, x0 + cw, y0 + chh, (int)JLayouts.D(Pm, "cam0") + k, false, fs * 0.8);
                }
            }
            else
            {
                env.Rect(fx0, fy0, fx1 - fx0, fy1 - fy0, feedBg, inA, false);
            }

            var fw = fx1 - fx0;
            var fh = fy1 - fy0;
            var text = JLayouts.SplitLines(Flat(cut.Text),
                JLayouts.S(Pm, "v") == "quad" ? (port ? 4 : 7) : (port ? 5 : 11));
            var size = Math.Min(JLayouts.FitSize(text, JLayouts.S(Pm, "font")!, fw * 0.82, fh * 0.46,
                new JObject { ["track"] = 0.05, ["lead"] = 1.2 }), fh * 0.3);
            DrawNoise(env, fx0, fy0, fw, fh, 0.05 * inA * @out, st, s + 7);
            bb = JLayouts.MainDraw(env, new JObject
            {
                ["text"] = text,
                ["font"] = JLayouts.S(Pm, "font"),
                ["size"] = size,
                ["x"] = fx0 + fw / 2,
                ["y"] = fy0 + fh / 2,
                ["track"] = 0.05,
                ["lead"] = 1.2,
                ["color"] = LC,
            });
            // 实时画面上的扫描线
            if (env.Pass == "main")
            {
                var sp = Math.Max(3, fh / 90);
                for (var y = fy0; y < fy1; y += sp * 2)
                    env.Rect(fx0, y, fw, sp * 0.6, "#000000", 0.12 * inA, false);
                var ry = fy0 + ((env.T * 0.13 + J.R(s, 9)) % 1) * fh;
                env.Rect(fx0, ry, fw, fh * 0.05, LC, 0.03 * inA * @out, false);
            }

            if (JLayouts.B(Pm, "box") && bb != null)
            {
                // 追着文字动的侦测框
                var e = Tin(env, cut.InDur * 0.6, 0.25) * @out;
                var jx = J.Rs(s, st >> 1, 21) * size * 0.08;
                var jy = J.Rs(s, st >> 1, 22) * size * 0.08;
                var pd = size * 0.25;
                if (e > 0)
                {
                    var x0 = bb.X0 - pd + jx;
                    var y0 = bb.Y0 - pd + jy;
                    var x1 = bb.X1 + pd + jx;
                    var y1 = bb.Y1 + pd + jy;
                    var L = size * 0.3;
                    var lw2 = Math.Max(1.2, lw * 1.4);
                    env.Line(new[] { new[] { x0, y0 + L }, new[] { x0, y0 }, new[] { x0 + L, y0 } }, accC, lw2, e, false);
                    env.Line(new[] { new[] { x1 - L, y0 }, new[] { x1, y0 }, new[] { x1, y0 + L } }, accC, lw2, e, false);
                    env.Line(new[] { new[] { x0, y1 - L }, new[] { x0, y1 }, new[] { x0 + L, y1 } }, accC, lw2, e, false);
                    env.Line(new[] { new[] { x1 - L, y1 }, new[] { x1, y1 }, new[] { x1, y1 - L } }, accC, lw2, e, false);
                    DrawIt(env, new JObject
                    {
                        ["text"] = "MOTION " + Pad2(1 + (int)(J.H(s, 23) % 9)),
                        ["font"] = mono,
                        ["size"] = fs * 0.75,
                        ["align"] = "left",
                        ["x"] = x0,
                        ["y"] = y0 - fs * 0.7,
                        ["color"] = accC,
                        ["alpha"] = e * (st % 3 != 0 ? 1 : 0.4),
                        ["ghost"] = false,
                        ["track"] = 0.1,
                    });
                }
            }

            Osd(fx0, fy0, fx1, fy1, (int)JLayouts.D(Pm, "cam0") + (int)JLayouts.D(Pm, "act") % 4, true);
            env.Line(new[]
            {
                new[] { fx0, fy0 }, new[] { fx1, fy0 }, new[] { fx1, fy1 }, new[] { fx0, fy1 }, new[] { fx0, fy0 },
            }, JLayouts.S(Pm, "v") == "quad" ? accC : frameC,
                lw * (JLayouts.S(Pm, "v") == "quad" ? 1.6 : 1),
                (JLayouts.S(Pm, "v") == "quad" ? 0.85 : 0.5) * inA * @out, false);
            env.Body = bb;
        },
    });

    // ---------------- hrOuija（JS L498-661） ----------------

    private const string KanaFallback = "あいうえおかきくけこさしすせそたちつてとなにぬねのはひふへほまみむめもやゆよらりるれろわをん";
    private const string Abc = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    private sealed class OuijaCell
    {
        public string Ch = "";
        public double X, Y, Rot;
    }

    private static void RegisterHrOuija() => Reg0("hrOuija", new LayoutDef
    {
        Name = "降霊盤",
        Tags = new[] { "horror" },
        W = 0.7,
        Portrait = 0.8,
        Fits = n => n >= 1 && n <= 14,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "serif", "display")),
            ["fontB"] = rng.Pick(FontsOf(st, "serif")),
            ["jit"] = rng.Range(0.45, 0.9),
            ["latin"] = HasLatin(cut.Text),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var cut = env.Cut!;
            var Pm = env.P;
            var s = unchecked((int)cut.Seed);
            var lt = env.Lt;
            var port = IsPort(env);
            var @out = Tout(env);
            var inA = Tin(env, 0, 0.35);
            var u = U(env);
            var lw = Math.Max(1, u * 0.0018);
            var bw = Math.Min(W * 0.88, H * (port ? 0.62 : 1.2));
            var bh = bw * (port ? 0.72 : 0.52);
            var bx = W / 2.0;
            var by = port ? H * 0.34 : H * 0.36;
            var plate = J.Mix(ScK(sc, "bg"), ScK(sc, "fg"), IsDark(ScK(sc, "bg")) ? 0.07 : 0.06);
            env.RRect(bx - bw / 2, by - bh / 2, bw, bh, bh * 0.14, plate, inA * @out, false, J.Mix(ScK(sc, "bg"), ScK(sc, "sub"), 0.6), lw);
            // 字符表按歌词语言取（J.pool）：中文/韩文歌词不再出现假名（ja 池本身就是假名）
            var KP = JLang.Pool("kana") ?? KanaFallback;
            var k1 = Math.Min(16, KP.Length);
            var k2 = Math.Min(32, KP.Length);
            var kpTail = k2 < KP.Length ? KP[k2..] : "";
            var rowsSrc = JLayouts.B(Pm, "latin")
                ? new[] { Abc[..13], Abc[13..], "1234567890" }
                : new[] { KP[..k1], KP[k1..k2], kpTail.Length > 0 ? kpTail : KP[..k1] };
            var cells = new List<OuijaCell>();
            var bf = bh * (JLayouts.B(Pm, "latin") ? 0.1 : 0.085);
            for (var r = 0; r < rowsSrc.Length; r++)
            {
                var row = rowsSrc[r];
                var arr = JLayouts.CodePoints(row);
                var n = arr.Count;
                var R = bw * (0.64 - r * 0.17);
                var cy = by + bh * (0.52 + r * 0.04);
                var arcSpan = (r != 0 ? 96 : 112) * (port ? 0.95 : 1);
                if (r == 2)
                {
                    for (var i = 0; i < n; i++)
                        cells.Add(new OuijaCell
                        {
                            Ch = arr[i],
                            X = bx + (i - (n - 1) / 2.0) * bw * 0.6 / Math.Max(1, n - 1),
                            Y = by + bh * 0.3,
                            Rot = 0,
                        });
                    continue;
                }

                for (var i = 0; i < n; i++)
                {
                    var a = (-90 - arcSpan / 2 + arcSpan * (n > 1 ? i / (double)(n - 1) : 0.5)) * J.DEG;
                    cells.Add(new OuijaCell
                    {
                        Ch = arr[i],
                        X = bx + Math.Cos(a) * R * 0.78,
                        Y = cy + Math.Sin(a) * R * 0.62,
                        Rot = (a / J.DEG + 90) * 0.7,
                    });
                }
            }

            var col = J.Mix(plate, ScK(sc, "fg"), 0.55);
            foreach (var c in cells)
                DrawIt(env, new JObject
                {
                    ["text"] = c.Ch,
                    ["font"] = JLayouts.S(Pm, "fontB"),
                    ["size"] = bf,
                    ["x"] = c.X,
                    ["y"] = c.Y,
                    ["rot"] = c.Rot,
                    ["color"] = col,
                    ["alpha"] = inA * @out,
                    ["ghost"] = false,
                });
            var mono = JLayouts.S(Pm, "fontB")!;
            var cs = bf * 0.9;
            DrawIt(env, new JObject
            {
                ["text"] = "YES", ["font"] = mono, ["size"] = cs,
                ["x"] = bx - bw * 0.36, ["y"] = by - bh * 0.36,
                ["color"] = col, ["alpha"] = inA * @out, ["ghost"] = false, ["track"] = 0.2,
            });
            DrawIt(env, new JObject
            {
                ["text"] = "NO", ["font"] = mono, ["size"] = cs,
                ["x"] = bx + bw * 0.36, ["y"] = by - bh * 0.36,
                ["color"] = col, ["alpha"] = inA * @out, ["ghost"] = false, ["track"] = 0.2,
            });
            DrawIt(env, new JObject
            {
                ["text"] = "GOOD BYE", ["font"] = mono, ["size"] = cs,
                ["x"] = bx, ["y"] = by + bh * 0.42,
                ["color"] = col, ["alpha"] = inA * @out, ["ghost"] = false, ["track"] = 0.3,
            });
            // 歌词行，以及每个字被拼出的时刻
            var text = Flat(cut.Text);
            var chars = JLayouts.CodePoints(text).Where(c => c != " ").ToList();
            var n2 = chars.Count;
            var rowY = port ? H * 0.74 : H * 0.8;
            var lines = n2 > (port ? 6 : 12) ? JLayouts.SplitLines(text, (int)Math.Ceiling(n2 / 2.0)) : text;
            var o2 = new JObject { ["track"] = 0.18, ["lead"] = 1.2 };
            var size = Math.Min(JLayouts.FitSize(lines, JLayouts.S(Pm, "font")!, W * 0.86, H * (port ? 0.26 : 0.2), o2), H * 0.13);
            var sl = Slots(new JObject
            {
                ["text"] = lines,
                ["font"] = JLayouts.S(Pm, "font"),
                ["size"] = size,
                ["x"] = W / 2.0,
                ["y"] = rowY,
                ["track"] = 0.18,
                ["lead"] = 1.2,
            });
            var span = cut.Dur * 0.5;
            var times = new List<double>();
            var acc = 0.0;
            var ws = new List<double>();
            for (var i = 0; i < n2; i++)
            {
                var w = 0.35 + JLayouts.D(Pm, "jit") * J.R(s, i, 51) * 2;
                ws.Add(w);
                acc += w;
            }

            var c0 = 0.25;
            for (var i = 0; i < n2; i++)
            {
                c0 += ws[i] / acc * (span - 0.25);
                times.Add(c0);
            }

            OuijaCell Target(int i)
            {
                var ch = i < chars.Count ? chars[i] : "";
                if (ch.Length == 1 && ch[0] is >= 'ァ' and <= 'ヶ') ch = ((char)(ch[0] - 0x60)).ToString();
                ch = ch.ToUpperInvariant();
                var cc = cells.FirstOrDefault(q => q.Ch == ch) ?? cells[(int)(J.H(s, i, 52) % (uint)cells.Count)];
                return cc;
            }

            // 指灵盘指针（planchette）的位置
            var px = bx;
            var py = by + bh * 0.1;
            var cur = -1;
            for (var i = 0; i < n2; i++)
            {
                var prevT = i > 0 ? times[i - 1] : 0;
                if (env.Ltb >= times[i] - Math.Min(0.4, (times[i] - prevT) * 0.75)) cur = i;
            }

            if (cur >= 0)
            {
                var tA = cur > 0 ? times[cur - 1] : 0;
                var tB = times[cur];
                var mv = Math.Min(0.4, (tB - tA) * 0.75);
                var pxA = px;
                var pyA = py;
                var A = cur > 0 ? Target(cur - 1) : new OuijaCell { X = pxA, Y = pyA };
                var B = Target(cur);
                var k = J.E.InOutCubic(J.Clamp((env.Ltb - (tB - mv)) / mv));
                px = J.Lerp(A.X, B.X, k) + Math.Sin(k * Math.PI) * bh * 0.06;
                py = J.Lerp(A.Y, B.Y, k) + bf * 1.2;
            }
            else
            {
                py += bf * 1.2;
            }

            // 没有目标时的游移
            px += Math.Sin(env.Ltb * 2.1 + s) * bf * 0.12;
            py += Math.Cos(env.Ltb * 1.7 + s) * bf * 0.1;
            var pr = bf * 1.2;
            var pc = ScK(sc, "fg");
            var pa = inA * @out;
            var shape = new[]
            {
                new[] { px, py - pr * 2.3 }, new[] { px + pr * 1.5, py + pr * 0.4 },
                new[] { px + pr * 0.9, py + pr * 1.3 }, new[] { px - pr * 0.9, py + pr * 1.3 },
                new[] { px - pr * 1.5, py + pr * 0.4 },
            };
            env.Poly(shape, J.Mix(plate, ScK(sc, "fg"), 0.12), pa * 0.85, false);
            env.Line(shape.Concat(new[] { shape[0] }).ToArray(), pc, lw * 1.4, pa, false);
            env.Circle(px, py - pr * 0.75, pr * 0.62, null, pc, lw * 1.4, pa, false);
            if (cur >= 0)
            {
                var B = Target(cur);
                if (Hypot(B.X - px, B.Y - (py - bf * 1.2)) < bf * 0.8)
                    DrawIt(env, new JObject
                    {
                        ["text"] = B.Ch,
                        ["font"] = JLayouts.S(Pm, "fontB"),
                        ["size"] = bf * 1.25,
                        ["x"] = B.X,
                        ["y"] = B.Y,
                        ["rot"] = B.Rot,
                        ["color"] = ScK(sc, "accent"),
                        ["alpha"] = pa,
                        ["ghost"] = false,
                    });
            }

            BBox? bb = null;
            foreach (var (g, i) in sl.Select((g, i) => (g, i)))
                bb = UB(bb, JLayouts.MainDraw(env, new JObject
                {
                    ["text"] = g.Ch,
                    ["font"] = JLayouts.S(Pm, "font"),
                    ["size"] = size,
                    ["x"] = g.X,
                    ["y"] = g.Y,
                    ["color"] = ScK(sc, "fg"),
                    ["mi"] = MiAt(env, i < times.Count ? times[i] : 0),
                }));
            env.Body = bb ?? Bb(W * 0.2, rowY - size, W * 0.8, rowY + size);
        },
    });

    // ---------------- hrMissing（JS L663-811） ----------------

    private static void RegisterHrMissing() => Reg0("hrMissing", new LayoutDef
    {
        Name = "尋ね人",
        Tags = new[] { "horror", "editorial" },
        W = 0.8,
        Treat = "safe",
        Fits = n => n >= 1 && n <= 16,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
            ["fontH"] = rng.Pick(FontsOf(st, "display")),
            ["ang"] = rng.Range(-3.5, 3.5),
            ["off"] = rng.Range(-0.06, 0.06),
            ["torn"] = (double)rng.Int(1, 3),
            ["stains"] = (double)rng.Int(1, 3),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var cut = env.Cut!;
            var Pm = env.P;
            var s = unchecked((int)cut.Seed);
            var port = IsPort(env);
            var u = U(env);
            var @out = Tout(env);
            var q = J.E.OutBack(J.Clamp(env.Lt / 0.3), 1.3);
            if (q <= 0)
            {
                env.Body = null;
                return;
            }

            var ph = H * 0.9;
            var pw = Math.Min(W * (port ? 0.86 : 0.5), ph * 0.72);
            var cx = W / 2.0 + JLayouts.D(Pm, "off") * W * (port ? 0.3 : 1);
            var cy = H / 2.0;
            var paper = IsDark(ScK(sc, "bg")) ? J.Mix(LightOf(sc), ScK(sc, "sub"), 0.12) : J.Mix(ScK(sc, "bg"), "#FFFFFF", 0.62);
            var ink = OnCol(sc, paper);
            var red = RedOn(sc, paper);
            var ang = JLayouts.D(Pm, "ang");
            var rad = ang * J.DEG;
            var cs = Math.Cos(rad);
            var sn = Math.Sin(rad);
            double[] P2(double lx, double ly) => new[] { cx + lx * cs - ly * sn, cy + lx * sn + ly * cs };
            var sq = 1.06 - 0.06 * q;
            var ctx = env.Ctx;
            ctx.Save();
            ctx.Translate((float)cx, (float)cy);
            ctx.RotateDegrees((float)(rad / J.DEG));
            ctx.Scale((float)sq, (float)sq);
            var x0 = -pw / 2;
            var y0 = -ph / 2;
            env.Rect(x0 + u * 0.012, y0 + u * 0.016, pw, ph, "#000000", 0.25 * @out, false);
            env.Rect(x0, y0, pw, ph, paper, @out, false);
            for (var k = 0; k < (int)JLayouts.D(Pm, "stains"); k++)
            {
                var sx = x0 + pw * J.Rr(0.22, 0.78, s, k, 1);
                var sy = y0 + ph * J.Rr(0.2, 0.8, s, k, 2);
                var R = pw * J.Rr(0.05, 0.12, s, k, 3);
                var pts = new List<double[]>();
                for (var i = 0; i < 12; i++)
                {
                    var a = i / 12.0 * J.TAU;
                    var r = R * (0.7 + 0.5 * J.R(s, k, i, 4));
                    pts.Add(new[] { sx + Math.Cos(a) * r, sy + Math.Sin(a) * r * 0.8 });
                }

                env.Blob(pts.ToArray(), J.Mix(paper, ScK(sc, "sub"), 0.35), 0.35 * @out, false);
            }

            var hs = pw * 0.15;
            DrawIt(env, new JObject
            {
                ["text"] = "MISSING",
                ["font"] = JLayouts.S(Pm, "fontH"),
                ["size"] = hs,
                ["x"] = 0.0,
                ["y"] = y0 + ph * 0.09,
                ["track"] = 0.08,
                ["color"] = red,
                ["alpha"] = @out,
                ["ghost"] = false,
                ["sx"] = 0.92,
            });
            // 照片里是无脸的头肩剪影
            var fw = pw * 0.52;
            var fh = fw * 1.18;
            var fx = -fw / 2;
            var fy = y0 + ph * 0.17;
            var photo = J.Mix(ink, paper, 0.25);
            env.Rect(fx, fy, fw, fh, photo, @out, false);
            var sil = J.Mix(ink, paper, 0.05);
            env.Circle(0, fy + fh * 0.42, fw * 0.2, sil, null, 0, @out, false);
            env.Blob(new[]
            {
                new[] { -fw * 0.42, fy + fh }, new[] { -fw * 0.36, fy + fh * 0.76 },
                new[] { -fw * 0.12, fy + fh * 0.66 }, new[] { fw * 0.12, fy + fh * 0.66 },
                new[] { fw * 0.36, fy + fh * 0.76 }, new[] { fw * 0.42, fy + fh },
            }, sil, @out, false);
            if (env.Pass == "main")
            {
                using var shader = SKShader.CreateLinearGradient(
                    new SKPoint(0, (float)fy), new SKPoint(0, (float)(fy + fh)),
                    new[] { Env.SkColor(paper, 0.28), Env.SkColor(paper, 0), Env.SkColor(paper, 0.18) },
                    new float[] { 0f, 0.5f, 1f }, SKShaderTileMode.Clamp);
                using var p = new SKPaint { Shader = shader, Color = Env.SkColor("#FFFFFF", @out) };
                ctx.DrawRect(new SKRect((float)fx, (float)fy, (float)(fx + fw), (float)(fy + fh)), p);
            }

            foreach (var sgn in new[] { -1, 1 })
            {
                ctx.Save();
                ctx.Translate((float)(sgn * pw * 0.4), (float)(y0 + ph * 0.005));
                ctx.RotateDegrees((float)(sgn * 28));
                env.Rect(-pw * 0.1, -pw * 0.03, pw * 0.2, pw * 0.06, J.Mix(paper, ScK(sc, "sub"), 0.25), 0.7 * @out, false);
                ctx.Restore();
            }

            var mono = MonoF(env);
            var fs = pw * 0.034;
            var ly = fy + fh + ph * 0.24;
            DrawIt(env, new JObject
            {
                ["text"] = "LAST SEEN " + FakeDate(s, ".") + "  " + FakeTime(s, 0)[..5],
                ["font"] = mono,
                ["size"] = fs,
                ["x"] = 0.0,
                ["y"] = ly,
                ["color"] = ink,
                ["alpha"] = @out * 0.85,
                ["ghost"] = false,
                ["track"] = 0.06,
            });
            var sub = Flat(!string.IsNullOrEmpty(cut.LineText) ? cut.LineText : cut.Text);
            DrawIt(env, new JObject
            {
                ["text"] = sub.Length > 22 ? sub[..21] + "…" : sub,
                ["font"] = BodyF(env),
                ["size"] = fs * 1.05,
                ["x"] = 0.0,
                ["y"] = ly + fs * 1.7,
                ["color"] = ink,
                ["alpha"] = @out * 0.7,
                ["ghost"] = false,
                ["track"] = 0.05,
            });
            // 可撕的电话联，torn 个已被撕走
            const int tabs = 7;
            var tw = pw / tabs;
            var ty = y0 + ph * 0.86;
            var th = ph * 0.14;
            for (var k = 0; k < tabs; k++)
            {
                if (J.H(s, k, 61) % 7 < (uint)JLayouts.D(Pm, "torn")) continue;
                var tx = x0 + k * tw;
                env.Line(new[] { new[] { tx, ty }, new[] { tx, ty + th } }, J.Mix(paper, ink, 0.4), Math.Max(1, u * 0.001), @out, false);
                ctx.Save();
                ctx.Translate((float)(tx + tw / 2), (float)(ty + th / 2));
                ctx.RotateDegrees(-90);
                DrawIt(env, new JObject
                {
                    ["text"] = "TEL 0" + (int)(J.H(s, 7) % 90 + 10) + "-" + (int)(J.H(s, 8) % 9000 + 1000),
                    ["font"] = mono,
                    ["size"] = Math.Min(tw * 0.42, th * 0.1),
                    ["x"] = 0.0,
                    ["y"] = 0.0,
                    ["color"] = ink,
                    ["alpha"] = @out * 0.8,
                    ["ghost"] = false,
                });
                ctx.Restore();
            }

            env.Line(new[] { new[] { x0, ty }, new[] { x0 + pw, ty } }, J.Mix(paper, ink, 0.4), Math.Max(1, u * 0.001), @out, false);
            ctx.Restore();
            // 歌词写在寻人启事的姓名位置
            var text = JLayouts.SplitLines(Flat(cut.Text), port ? 6 : 7);
            var lyY = fy + fh + ph * 0.1;
            var o = new JObject { ["track"] = 0.04, ["lead"] = 1.1 };
            var size = Math.Min(JLayouts.FitSize(text, JLayouts.S(Pm, "font")!, pw * 0.86, ph * 0.13, o), pw * 0.16);
            var lxy = P2(0, lyY * sq);
            var bb = JLayouts.MainDraw(env, new JObject
            {
                ["text"] = text,
                ["font"] = JLayouts.S(Pm, "font"),
                ["size"] = size * sq,
                ["x"] = lxy[0],
                ["y"] = lxy[1],
                ["rot"] = ang,
                ["track"] = 0.04,
                ["lead"] = 1.1,
                ["color"] = ink,
                ["plain"] = false,
            });
            env.Body = bb ?? Bb(cx - pw / 2, cy - ph / 2, cx + pw / 2, cy + ph / 2);
        },
    });

    // ---------------- hrWrongOne（JS L813-883） ----------------

    private static void RegisterHrWrongOne() => Reg0("hrWrongOne", new LayoutDef
    {
        Name = "一字だけ違う",
        Tags = new[] { "horror", "editorial", "calm" },
        W = 1,
        Fits = n => n >= 2 && n <= 16,
        Plan = (rng, cut, st) =>
        {
            var cs = JLayouts.CodePoints(Flat(cut.Text))
                .Select((c, i) => (C: c, I: i))
                .Where(t => t.C != " " && !J.IsPunct(t.C[0]))
                .ToList();
            var kan = cs.Where(t => J.IsKanji(t.C[0]) || J.IsKata(t.C[0]) || Regex.IsMatch(t.C, "[A-Za-z]")).ToList();
            var pool = kan.Count > 0 ? kan : cs;
            var pick = pool.Count > 0 ? pool[rng.Int(0, pool.Count - 1)].I : 0;
            return new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "serif", "display")),
                ["wrong"] = (double)pick,
                ["ang"] = rng.Pick(new[] { 28, 90, 180, -90, -35 }),
                ["red"] = rng.Chance(0.35),
                ["nums"] = rng.Chance(0.7),
                ["sink"] = rng.Range(0.06, 0.2),
            };
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var cut = env.Cut!;
            var Pm = env.P;
            var s = unchecked((int)cut.Seed);
            var lt = env.Lt;
            var port = IsPort(env);
            var text = JLayouts.SplitLines(Flat(cut.Text), port ? 5 : 9);
            var o = new JObject { ["track"] = 0.22, ["lead"] = 1.5 };
            var size = Math.Min(JLayouts.FitSize(text, JLayouts.S(Pm, "font")!, W * 0.82, H * 0.46, o), H * 0.2);
            var baseIt = new JObject
            {
                ["text"] = text,
                ["font"] = JLayouts.S(Pm, "font"),
                ["size"] = size,
                ["x"] = W / 2.0,
                ["y"] = H / 2.0,
                ["track"] = 0.22,
                ["lead"] = 1.5,
            };
            var all = Slots(baseIt);
            // Pm.wrong 是含空格的扁平下标，这里换算成槽位下标
            var flatArr = JLayouts.CodePoints(Flat(cut.Text));
            var wi = 0;
            for (int i = 0, k = 0; i < flatArr.Count; i++)
            {
                if (flatArr[i] == " ") continue;
                if (i == (int)JLayouts.D(Pm, "wrong"))
                {
                    wi = k;
                    break;
                }

                k++;
            }

            wi = Math.Min(wi, all.Count - 1);
            var d = cut.Dur;
            var turnAt = d * 0.35;
            BBox? bb = null;
            for (var i = 0; i < all.Count; i++)
            {
                var g = all[i];
                var it = new JObject
                {
                    ["text"] = g.Ch,
                    ["font"] = JLayouts.S(Pm, "font"),
                    ["size"] = size,
                    ["x"] = g.X,
                    ["y"] = g.Y,
                    ["color"] = ScK(sc, "fg"),
                    ["mi"] = (double)i,
                };
                if (i == wi)
                {
                    // 只有这个字缓慢转动，其余不动，偶尔猛地一抽
                    var qq = J.E.InOutSine(J.Clamp((lt - turnAt) / Math.Max(0.5, d * 0.45)));
                    var twitch = J.R(s, (int)env.Step >> 1, 71) < 0.07 && lt > turnAt ? J.Rs(s, (int)env.Step, 72) * 14 : 0;
                    it["rot"] = JLayouts.D(Pm, "ang") * qq + twitch;
                    it["y"] = g.Y + size * JLayouts.D(Pm, "sink") * qq;
                    it["x"] = g.X + J.Rs(s, (int)env.Step >> 2, 73) * size * 0.015 * qq;
                    if (JLayouts.B(Pm, "red")) it["color"] = J.Mix(ScK(sc, "fg"), ScK(sc, "accent"), qq);
                    it["noHold"] = true;
                }

                bb = UB(bb, JLayouts.MainDraw(env, it));
            }

            if (JLayouts.B(Pm, "nums") && all.Count > 0)
            {
                var e = Tin(env, cut.InDur * 0.8, 0.3) * Tout(env);
                var fs = J.Clamp(size * 0.14, 9, 22);
                var mono = MonoF(env);
                if (e > 0)
                    for (var i = 0; i < all.Count; i++)
                    {
                        var g = all[i];
                        var wrong = i == wi && lt > turnAt + 0.3;
                        DrawIt(env, new JObject
                        {
                            ["text"] = wrong ? "??" : Pad2(i + 1),
                            ["font"] = mono,
                            ["size"] = fs,
                            ["x"] = g.X,
                            ["y"] = g.Y + size * 0.72,
                            ["color"] = wrong ? ScK(sc, "accent") : ScK(sc, "sub"),
                            ["alpha"] = e * (wrong ? 1 : 0.7),
                            ["ghost"] = false,
                            ["track"] = 0.1,
                        });
                    }
            }

            env.Body = bb;
        },
    });

    // ---------------- hrRisingDark（JS L885-951） ----------------

    private static void RegisterHrRisingDark() => Reg0("hrRisingDark", new LayoutDef
    {
        Name = "闇から這い出る",
        Tags = new[] { "horror", "emotional" },
        W = 0.8,
        Fits = n => n >= 1 && n <= 14,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
            ["pulls"] = (double)rng.Int(3, 5),
            ["span"] = rng.Range(0.45, 0.6),
            ["rim"] = rng.Chance(0.8),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var cut = env.Cut!;
            var Pm = env.P;
            var s = unchecked((int)cut.Seed);
            var port = IsPort(env);
            var text = JLayouts.SplitLines(Flat(cut.Text), port ? 5 : 9);
            var o = new JObject { ["track"] = 0.06, ["lead"] = 1.2 };
            var size = Math.Min(JLayouts.FitSize(text, JLayouts.S(Pm, "font")!, W * 0.82, H * 0.34, o), H * 0.2);
            var cy = H * 0.44;
            var all = Slots(new JObject
            {
                ["text"] = text,
                ["font"] = JLayouts.S(Pm, "font"),
                ["size"] = size,
                ["x"] = W / 2.0,
                ["y"] = cy,
                ["track"] = 0.06,
                ["lead"] = 1.2,
            });
            var nl = text.Split('\n').Length;
            var bottom = cy + nl * size * 1.2 / 2;
            var surf = bottom + size * 0.3;
            var T = cut.Dur * JLayouts.D(Pm, "span");
            var pulls = (int)JLayouts.D(Pm, "pulls");
            BBox? bb = null;
            for (var i = 0; i < all.Count; i++)
            {
                var g = all[i];
                // 一把一把往上爬，几个突然的发力点
                var prog = 0.0;
                for (var k = 0; k < pulls; k++)
                {
                    var tk = J.Clamp(J.R(s, i, k, 81) * 0.8 + k * 0.2 / pulls * 1.0) * T * (k + 1) / pulls;
                    prog += J.E.OutCubic(J.Clamp((env.Ltb - tk) / 0.14)) / pulls;
                }

                var rest = surf - g.Y + size * 0.62;
                var dy = (1 - prog) * rest;
                var shake = prog < 1 ? J.Rs(s, i, (int)env.Step, 82) * size * 0.02 : 0;
                bb = UB(bb, JLayouts.MainDraw(env, new JObject
                {
                    ["text"] = g.Ch,
                    ["font"] = JLayouts.S(Pm, "font"),
                    ["size"] = size,
                    ["x"] = g.X + shake,
                    ["y"] = g.Y + dy,
                    ["rot"] = (1 - prog) * J.Rs(s, i, 83) * 16,
                    ["color"] = ScK(sc, "fg"),
                    ["mi"] = 0.0,
                }));
            }

            // 黑水池，盖在还没露出的字上面
            if (env.Pass == "main")
            {
                var pc = NightC(sc);
                var pts = new List<double[]>();
                const int n = 48;
                var a = Tout(env);
                var t = env.Ltb;
                for (var j = 0; j <= n; j++)
                {
                    var x = -W * 0.1 + W * 1.2 * j / (double)n;
                    pts.Add(new[]
                    {
                        x,
                        surf + (J.Noise1(j * 0.35 + t * 0.6, s) * 0.6 + Math.Sin(j * 0.9 + t * 1.4) * 0.15) * size * 0.12,
                    });
                }

                var ctx = env.Ctx;
                ctx.Save();
                using (var shader = SKShader.CreateLinearGradient(
                           new SKPoint(0, (float)(surf - size * 0.1)), new SKPoint(0, (float)H),
                           new[] { Env.SkColor(pc, 0.96), Env.SkColor(J.Mix(pc, "#000000", 0.5), 1) },
                           new float[] { 0f, 1f }, SKShaderTileMode.Clamp))
                using (var p = new SKPaint { IsAntialias = true, Shader = shader, Color = Env.SkColor("#FFFFFF", a) })
                using (var path = new SKPath())
                {
                    path.MoveTo((float)(-W * 0.2), (float)(H * 1.2));
                    foreach (var pt in pts) path.LineTo((float)pt[0], (float)pt[1]);
                    path.LineTo((float)(W * 1.2), (float)(H * 1.2));
                    path.Close();
                    ctx.DrawPath(path, p);
                }

                ctx.Restore();
                if (JLayouts.B(Pm, "rim"))
                    env.Line(pts.ToArray(), ScK(sc, "sub"), Math.Max(1, size * 0.012), 0.35 * a, false);
            }

            if (bb != null) bb.Y1 = Math.Min(bb.Y1, surf);
            env.Body = bb;
        },
    });

    // ---------------- hrRedacted（JS L953-1075） ----------------

    private static void RegisterHrRedacted() => Reg0("hrRedacted", new LayoutDef
    {
        Name = "黒塗り文書",
        Tags = new[] { "horror", "editorial" },
        W = 0.8,
        Busy = true,
        Fits = n => n >= 1 && n <= 20,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "serif", "body", "mono")),
            ["row"] = rng.Range(0.42, 0.58),
            ["stamp"] = rng.Chance(0.75),
            ["stampAng"] = rng.Range(-14, -6),
            ["leak"] = rng.Range(0.1, 0.25),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var cut = env.Cut!;
            var Pm = env.P;
            var s = unchecked((int)cut.Seed);
            var lt = env.Lt;
            var port = IsPort(env);
            var u = U(env);
            var @out = Tout(env);
            var inA = Tin(env, 0, 0.25);
            var mx = W * (port ? 0.08 : 0.12);
            var mw = W - mx * 2;
            var mono = MonoF(env);
            var fs = J.Clamp(u * 0.028, 12, 34);
            var rh = fs * 2.1;
            var barC = J.Mix(ScK(sc, "fg"), ScK(sc, "bg"), 0.08);
            var txtC = ScK(sc, "sub");
            DrawIt(env, new JObject
            {
                ["text"] = "FILE No. " + (int)(J.H(s, 1) % 9000 + 1000) + "   " + FakeDate(s, "."),
                ["font"] = mono,
                ["size"] = fs * 0.8,
                ["align"] = "left",
                ["x"] = mx,
                ["y"] = H * 0.08,
                ["color"] = txtC,
                ["alpha"] = inA * @out,
                ["ghost"] = false,
                ["track"] = 0.08,
            });
            env.Line(new[] { new[] { mx, H * 0.08 + fs }, new[] { mx + mw * inA, H * 0.08 + fs } }, txtC,
                Math.Max(1, u * 0.0012), 0.6 * @out, false);
            var text = JLayouts.SplitLines(Flat(cut.Text), port ? 7 : 16);
            var nl = text.Split('\n').Length;
            var o = new JObject { ["track"] = 0.06, ["lead"] = 1.3, ["align"] = "left" };
            var size = Math.Min(Math.Min(JLayouts.FitSize(text, JLayouts.S(Pm, "font")!, mw, H * 0.24, o), H * 0.1), fs * 3.4);
            var ly = H * JLayouts.D(Pm, "row");
            var lh = nl * size * 1.3;
            // 填充行：正文被涂黑
            var body = Flat(!string.IsNullOrEmpty(cut.LineText) ? cut.LineText : cut.Text) + "　";
            var body6 = string.Concat(Enumerable.Repeat(body, 6));
            var rowsN = (int)Math.Floor((H * 0.84 - H * 0.14) / rh);
            var ctx = env.Ctx;
            for (var r = 0; r < rowsN; r++)
            {
                var y = H * 0.15 + r * rh;
                if (Math.Abs(y - ly) < lh / 2 + rh * 0.7) continue;
                var e = J.Clamp((env.Ltb - r * 0.025) / 0.2) * @out;
                if (e <= 0) continue;
                var len = r % 5 == 4 ? J.Rr(0.3, 0.6, s, r, 2) : J.Rr(0.85, 1, s, r, 2);
                if (env.Pass == "main")
                {
                    ctx.Save();
                    ctx.ClipRect(new SKRect((float)mx, (float)(y - rh / 2), (float)(mx + mw * len), (float)(y + rh / 2)));
                    DrawIt(env, new JObject
                    {
                        ["text"] = body6,
                        ["font"] = BodyF(env),
                        ["size"] = fs,
                        ["align"] = "left",
                        ["x"] = mx,
                        ["y"] = y,
                        ["color"] = txtC,
                        ["alpha"] = 0.6 * e,
                        ["ghost"] = false,
                        ["track"] = 0.05,
                    });
                    ctx.Restore();
                }

                var x = mx;
                while (x < mx + mw * len)
                {
                    var rx = (int)Math.Round(x);
                    var w = mw * J.Rr(0.08, 0.35, s, r, rx, 3);
                    var gap = fs * J.Rr(0.4, 2.4, s, r, rx, 4);
                    if (J.R(s, r, rx, 5) > JLayouts.D(Pm, "leak"))
                        env.Rect(x, y - fs * 0.62, Math.Min(w, mx + mw * len - x) * e, fs * 1.24, barC, @out, false);
                    x += w + gap;
                }
            }

            var oo = new JObject { ["track"] = 0.06, ["lead"] = 1.3, ["align"] = "left" };
            var bb = JLayouts.MainDraw(env, new JObject
            {
                ["text"] = text,
                ["font"] = JLayouts.S(Pm, "font"),
                ["size"] = size,
                ["x"] = mx,
                ["y"] = ly,
                ["align"] = "left",
                ["track"] = 0.06,
                ["lead"] = 1.3,
                ["color"] = ScK(sc, "fg"),
            });
            // 盖着歌词的黑条：滑开、停住、再滑走
            if (env.Pass == "main")
            {
                var m = JLayouts.Measure(new JObject
                {
                    ["text"] = text,
                    ["font"] = JLayouts.S(Pm, "font"),
                    ["size"] = size,
                    ["align"] = "left",
                    ["track"] = 0.06,
                    ["lead"] = 1.3,
                });
                var d0 = cut.InDur * 0.3;
                var k1 = J.E.InOutCubic(J.Clamp((lt - d0) / 0.25)) * 0.35;
                var k2 = J.E.InOutCubic(J.Clamp((lt - d0 - 0.25 - cut.Dur * 0.08) / 0.3)) * 0.65;
                var k = k1 + k2;
                var bw = (m.W + size * 0.4) * (1 - k);
                if (bw > 0.5)
                    env.Rect(mx - size * 0.2 + (m.W + size * 0.4) * k, ly - m.H / 2 - size * 0.12, bw,
                        m.H + size * 0.24, barC, inA, false);
            }

            if (JLayouts.B(Pm, "stamp"))
            {
                var at = cut.Dur * 0.45;
                var q = J.Clamp((lt - at) / 0.12);
                if (q > 0)
                {
                    var sc2 = 1.6 - 0.6 * J.E.OutCubic(q);
                    var a = Math.Min(1, q * 2) * @out * 0.85;
                    var ss = fs * 1.6;
                    const string word = "CLASSIFIED";
                    var tw = JLayouts.Measure(new JObject { ["text"] = word, ["font"] = mono, ["size"] = ss, ["track"] = 0.2 }).W;
                    ctx.Save();
                    ctx.Translate((float)(W - mx - tw * 0.55), (float)(H * 0.8));
                    ctx.RotateDegrees((float)JLayouts.D(Pm, "stampAng"));
                    ctx.Scale((float)sc2, (float)sc2);
                    env.RRect(-tw / 2 - ss * 0.5, -ss * 0.9, tw + ss, ss * 1.8, ss * 0.2, null, a, false,
                        ScK(sc, "accent"), Math.Max(2, ss * 0.1));
                    DrawIt(env, new JObject
                    {
                        ["text"] = word,
                        ["font"] = mono,
                        ["size"] = ss,
                        ["x"] = 0.0,
                        ["y"] = 0.0,
                        ["color"] = ScK(sc, "accent"),
                        ["alpha"] = a,
                        ["ghost"] = false,
                        ["track"] = 0.2,
                    });
                    ctx.Restore();
                }
            }

            env.Body = bb;
        },
    });

    // ---------------- hrStaticTv（JS L1077-1161） ----------------

    private static void RegisterHrStaticTv() => Reg0("hrStaticTv", new LayoutDef
    {
        Name = "砂嵐のテレビ",
        Tags = new[] { "horror", "glitch" },
        W = 0.8,
        Treat = "safe",
        Fits = n => n >= 1 && n <= 14,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "display", "body")),
            ["off"] = rng.Range(-0.06, 0.06),
            ["tune"] = rng.Range(0.3, 0.5),
            ["ant"] = rng.Range(18, 34),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var cut = env.Cut!;
            var Pm = env.P;
            var s = unchecked((int)cut.Seed);
            var lt = env.Lt;
            var port = IsPort(env);
            var u = U(env);
            var @out = Tout(env);
            var inA = Tin(env, 0, 0.3);
            var tw = Math.Min(W * (port ? 0.86 : 0.62), H * (port ? 0.5 : 0.8) * 1.3);
            var th = tw / 1.3;
            var cx = W / 2.0 + JLayouts.D(Pm, "off") * W;
            var cy = H / 2.0 + th * 0.06;
            var body = J.Mix(ScK(sc, "bg"), ScK(sc, "fg"), IsDark(ScK(sc, "bg")) ? 0.13 : 0.22);
            var edge = J.Mix(body, ScK(sc, "fg"), 0.3);
            var lw = Math.Max(1, u * 0.0018);
            var ctx = env.Ctx;
            // 屏幕的光洒进房间
            if (env.Pass == "main")
            {
                using var shader = Radial2(cx, cy, th * 0.3, Math.Max(W, H) * 0.7,
                    new[] { (0.0, Cf(LightOf(sc), 0.1 * inA * @out)), (1.0, Cf(LightOf(sc), 0)) });
                using var p = new SKPaint { Shader = shader };
                ctx.DrawRect(new SKRect(0, 0, (float)W, (float)H), p);
            }

            var top = cy - th / 2;
            foreach (var sg in new[] { -1, 1 })
            {
                var a = (90 + sg * JLayouts.D(Pm, "ant") + Math.Sin(env.Ltb * 0.7 + sg) * 1.5) * J.DEG;
                env.Line(new[]
                {
                    new[] { cx, top },
                    new[] { cx - Math.Cos(a) * th * 0.45, top - Math.Sin(a) * th * 0.45 },
                }, edge, lw * 2, inA * @out, false);
            }

            env.RRect(cx - tw / 2, top, tw, th, th * 0.08, body, inA * @out, false, edge, lw * 1.5);
            var sw = tw * 0.72;
            var sh = th * 0.78;
            var sx0 = cx - tw / 2 + tw * 0.05;
            var sy0 = top + th * 0.11;
            var kx = sx0 + sw + (tw - sw - tw * 0.05) / 2;
            for (var k = 0; k < 2; k++)
                env.Circle(kx, top + th * (0.25 + k * 0.2), tw * 0.035, J.Mix(body, ScK(sc, "bg"), 0.4), edge, lw, inA * @out, false);
            for (var k = 0; k < 5; k++)
                env.Line(new[]
                {
                    new[] { kx - tw * 0.04, top + th * (0.62 + k * 0.05) },
                    new[] { kx + tw * 0.04, top + th * (0.62 + k * 0.05) },
                }, edge, lw, inA * @out * 0.8, false);
            env.Rect(cx - tw * 0.36, top + th, tw * 0.05, th * 0.06, body, inA * @out, false);
            env.Rect(cx + tw * 0.31, top + th, tw * 0.05, th * 0.06, body, inA * @out, false);
            // 屏幕：砂噪慢慢让位给文字
            var scr = J.Mix(ScK(sc, "bg"), "#000000", 0.6);
            env.RRect(sx0, sy0, sw, sh, sh * 0.1, scr, inA, false);
            var tune = J.E.InOutCubic(J.Clamp((lt - 0.15) / (cut.Dur * JLayouts.D(Pm, "tune"))));
            var burst = J.R(s, (int)env.Step, 91) < 0.06 ? 0.35 : 0;
            if (env.Pass == "main")
            {
                ctx.Save();
                ctx.ClipRect(new SKRect((float)(sx0 + sh * 0.03), (float)(sy0 + sh * 0.03),
                    (float)(sx0 + sh * 0.03 + sw - sh * 0.06), (float)(sy0 + sh * 0.03 + sh - sh * 0.06)));
                DrawNoise(env, sx0, sy0, sw, sh, (0.85 - 0.6 * tune + burst) * inA, (int)env.Step, s);
                ctx.Restore();
            }

            var tc = J.Lum(ScK(sc, "fg")) > 0.5 ? ScK(sc, "fg") : "#FFFFFF";
            var text = JLayouts.SplitLines(Flat(cut.Text), port ? 5 : 7);
            var o = new JObject { ["track"] = 0.04, ["lead"] = 1.15 };
            var size = Math.Min(JLayouts.FitSize(text, JLayouts.S(Pm, "font")!, sw * 0.82, sh * 0.6, o), sh * 0.36);
            var roll = (1 - tune) * sh * 0.08 * Math.Sin(env.Ltb * 23);
            ctx.Save();
            ctx.ClipRect(new SKRect((float)sx0, (float)sy0, (float)(sx0 + sw), (float)(sy0 + sh)));
            var bb = JLayouts.MainDraw(env, new JObject
            {
                ["text"] = text,
                ["font"] = JLayouts.S(Pm, "font"),
                ["size"] = size,
                ["x"] = sx0 + sw / 2,
                ["y"] = sy0 + sh / 2 + roll,
                ["track"] = 0.04,
                ["lead"] = 1.15,
                ["color"] = tc,
                ["alpha"] = 0.55 + 0.45 * tune,
            });
            ctx.Restore();
            if (env.Pass == "main")
            {
                // 玻璃反光 + 扫描线
                var sp = Math.Max(3, sh / 70);
                for (var y = sy0; y < sy0 + sh; y += sp * 2)
                    env.Rect(sx0, y, sw, sp * 0.5, "#000000", 0.18 * inA, false);
                env.RRect(sx0 + sw * 0.06, sy0 + sh * 0.05, sw * 0.4, sh * 0.12, sh * 0.06, "#FFFFFF", 0.05 * inA * @out, false);
            }

            env.Body = bb ?? Bb(sx0, sy0, sx0 + sw, sy0 + sh);
        },
    });

    // ---------------- hrSpiritPhoto（JS L1163-1275） ----------------

    private static void RegisterHrSpiritPhoto() => Reg0("hrSpiritPhoto", new LayoutDef
    {
        Name = "心霊写真",
        Tags = new[] { "horror", "emotional" },
        W = 0.8,
        Fits = n => n >= 1 && n <= 16,
        Plan = (rng, cut, st) => new JObject
        {
            ["hand"] = HandOf(rng, st),
            ["ang"] = rng.Range(-5, 5),
            ["spot"] = new JArray(rng.Range(0.25, 0.75), rng.Range(0.3, 0.6)),
            ["win"] = rng.Chance(0.6),
            ["side"] = rng.Pick(new[] { 1, -1 }),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var cut = env.Cut!;
            var Pm = env.P;
            var s = unchecked((int)cut.Seed);
            var lt = env.Lt;
            var port = IsPort(env);
            var u = U(env);
            var @out = Tout(env);
            var inA = Tin(env, 0, 0.3);
            var spot = (JArray?)Pm["spot"];
            // 照片放左边（横屏）或上方（竖屏），便签在另一侧
            var pw = port ? W * 0.78 : Math.Min(W * 0.46, H * 0.86 * 1.25);
            var ph = pw / 1.25;
            var side = JLayouts.D(Pm, "side");
            var pcx = port ? W / 2.0 : (side > 0 ? W * 0.3 : W * 0.7);
            var pcy = port ? H * 0.33 : H * 0.5;
            var rad = JLayouts.D(Pm, "ang") * J.DEG;
            var cs = Math.Cos(rad);
            var sn = Math.Sin(rad);
            double[] T(double lx, double ly) => new[] { pcx + lx * cs - ly * sn, pcy + lx * sn + ly * cs };
            var border = J.Mix(LightOf(sc), ScK(sc, "sub"), 0.1);
            var dark = J.Mix(DarkOf(sc), "#000000", 0.35);
            var develop = J.E.InOutSine(J.Clamp(lt / Math.Max(0.6, cut.Dur * 0.35)));
            var ctx = env.Ctx;
            ctx.Save();
            ctx.Translate((float)pcx, (float)pcy);
            ctx.RotateDegrees((float)(rad / J.DEG));
            var bw = u * 0.018;
            env.Rect(-pw / 2 - bw + u * 0.01, -ph / 2 - bw + u * 0.012, pw + bw * 2, ph + bw * 3, "#000000", 0.25 * inA * @out, false);
            env.Rect(-pw / 2 - bw, -ph / 2 - bw, pw + bw * 2, ph + bw * 3.2, border, inA * @out, false);
            env.Rect(-pw / 2, -ph / 2, pw, ph, dark, inA * @out, false);
            var mid = J.Mix(dark, border, 0.22 * develop);
            if (JLayouts.B(Pm, "win"))
            {
                var wx = -pw * 0.3;
                var wy = -ph * 0.32;
                var ww = pw * 0.3;
                var wh = ph * 0.45;
                var lw = Math.Max(1, u * 0.004);
                env.Rect(wx, wy, ww, wh, J.Mix(dark, border, 0.12 * develop), inA * @out, false);
                env.Line(new[] { new[] { wx + ww / 2, wy }, new[] { wx + ww / 2, wy + wh } }, dark, lw, inA * @out, false);
                env.Line(new[] { new[] { wx, wy + wh / 2 }, new[] { wx + ww, wy + wh / 2 } }, dark, lw, inA * @out, false);
            }

            env.Rect(-pw / 2, ph * 0.18, pw, ph * 0.32, mid, 0.6 * inA * @out, false);
            var spx = -pw / 2 + pw * (double)(spot?[0] ?? 0.5);
            var spy = -ph / 2 + ph * (double)(spot?[1] ?? 0.45);
            var sr = pw * 0.09;
            if (env.Pass == "main")
            {
                using var shader = Radial2(spx, spy, 0, sr * 1.6,
                    new[] { (0.0, Cf(border, 0.55 * develop)), (1.0, Cf(border, 0)) });
                using var p = new SKPaint { Shader = shader, Color = Env.SkColor("#FFFFFF", inA * @out) };
                ctx.Save();
                ctx.Translate((float)spx, (float)spy);
                ctx.Scale(0.75f, 1.25f);
                ctx.Translate((float)-spx, (float)-spy);
                ctx.DrawRect(new SKRect((float)(spx - sr * 2), (float)(spy - sr * 2), (float)(spx + sr * 2), (float)(spy + sr * 2)), p);
                ctx.Restore();
            }

            DrawIt(env, new JObject
            {
                ["text"] = "'" + FakeDate(s, " ")[2..],
                ["font"] = MonoF(env),
                ["size"] = ph * 0.055,
                ["align"] = "right",
                ["x"] = pw / 2 - ph * 0.05,
                ["y"] = ph / 2 - ph * 0.06,
                ["color"] = J.FitContrast(ScK(sc, "accent"), dark, 3),
                ["alpha"] = 0.9 * develop * @out,
                ["ghost"] = false,
                ["track"] = 0.1,
            });
            // 围着污点的红笔圈
            var ringE = J.E.OutCubic(J.Clamp((lt - cut.Dur * 0.3) / 0.5)) * @out;
            if (ringE > 0)
            {
                var pts = new List<double[]>();
                const int n = 40;
                var rr = sr * 1.5;
                for (var i = 0; i <= n * 1.15; i++)
                {
                    var a = i / (double)n * J.TAU - 1.2;
                    var w = 1 + 0.12 * J.Noise1(i * 0.3, s) + i / (double)n * 0.08;
                    pts.Add(new[] { spx + Math.Cos(a) * rr * w * 0.8, spy + Math.Sin(a) * rr * w });
                }

                env.PolyPartial(pts.ToArray(), ringE, ScK(sc, "accent"), Math.Max(2, u * 0.005), 0.95, false);
            }

            ctx.Restore();
            // 指向便签的箭头 + 手写便签（歌词）
            var text = JLayouts.SplitLines(Flat(cut.Text), port ? 7 : 6);
            var nx = port ? W / 2.0 : (side > 0 ? W * 0.76 : W * 0.24);
            var ny = port ? H * 0.75 : H * 0.5;
            var no = new JObject { ["track"] = 0.02, ["lead"] = 1.2 };
            var size = Math.Min(JLayouts.FitSize(text, JLayouts.S(Pm, "hand")!, port ? W * 0.84 : W * 0.38,
                H * (port ? 0.26 : 0.46), no), H * 0.14);
            var axy = T(spx + sr * 1.4 * (port ? 0.3 : side), spy + sr * (port ? 1.6 : 0.3));
            var nm = JLayouts.Measure(new JObject
            {
                ["text"] = text,
                ["font"] = JLayouts.S(Pm, "hand"),
                ["size"] = size,
                ["track"] = 0.02,
                ["lead"] = 1.2,
            });
            var tx = port ? nx : nx - side * (nm.W / 2 + size * 0.35);
            var ty = port ? ny - nm.H / 2 - size * 0.35 : ny;
            var ae = J.E.OutCubic(J.Clamp((lt - cut.Dur * 0.3 - 0.35) / 0.35)) * @out;
            if (ae > 0)
            {
                var ax = axy[0];
                var ay = axy[1];
                var mx = (ax + tx) / 2 + (port ? W * 0.1 : 0);
                var my = (ay + ty) / 2 - (port ? 0 : H * 0.1);
                var pts = new List<double[]>();
                for (var i = 0; i <= 16; i++)
                {
                    var k = i / 16.0;
                    pts.Add(new[]
                    {
                        (1 - k) * (1 - k) * ax + 2 * k * (1 - k) * mx + k * k * tx,
                        (1 - k) * (1 - k) * ay + 2 * k * (1 - k) * my + k * k * ty,
                    });
                }

                env.PolyPartial(pts.AsEnumerable().Reverse().ToArray(), ae, ScK(sc, "accent"), Math.Max(2, u * 0.004), 0.9, false);
                if (ae > 0.95)
                {
                    var x1 = pts[0][0];
                    var y1 = pts[0][1];
                    var x2 = pts[2][0];
                    var y2 = pts[2][1];
                    var an = Math.Atan2(y1 - y2, x1 - x2);
                    var L = u * 0.03;
                    env.Line(new[]
                    {
                        new[] { x1 + Math.Cos(an + 2.6) * L, y1 + Math.Sin(an + 2.6) * L },
                        new[] { x1, y1 },
                        new[] { x1 + Math.Cos(an - 2.6) * L, y1 + Math.Sin(an - 2.6) * L },
                    }, ScK(sc, "accent"), Math.Max(2, u * 0.004), 0.9, false);
                }
            }

            env.Body = JLayouts.MainDraw(env, new JObject
            {
                ["text"] = text,
                ["font"] = JLayouts.S(Pm, "hand"),
                ["size"] = size,
                ["x"] = nx,
                ["y"] = ny,
                ["track"] = 0.02,
                ["lead"] = 1.2,
                ["rot"] = -JLayouts.D(Pm, "ang") * 0.4,
                ["color"] = ScK(sc, "fg"),
            });
        },
    });

    // ---------------- hrWrongShadow（JS L1277-1348） ----------------

    private static void RegisterHrWrongShadow() => Reg0("hrWrongShadow", new LayoutDef
    {
        Name = "影が違う",
        Tags = new[] { "horror", "emotional", "graphic" },
        W = 0.9,
        Fits = n => n >= 1 && n <= 12,
        Plan = (rng, cut, st) => new JObject
        {
            ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
            ["k"] = rng.Range(1.5, 1.9),
            ["turn"] = rng.Range(0.45, 0.65),
            ["dir"] = rng.Pick(new[] { 1, -1 }),
            ["lean"] = rng.Range(4, 9),
        },
        Draw = env =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var cut = env.Cut!;
            var Pm = env.P;
            var s = unchecked((int)cut.Seed);
            var lt = env.Lt;
            var port = IsPort(env);
            var text = JLayouts.SplitLines(Flat(cut.Text), port ? 4 : 7);
            var o = new JObject { ["track"] = 0.05, ["lead"] = 1.1 };
            var size = Math.Min(JLayouts.FitSize(text, JLayouts.S(Pm, "font")!, W * 0.66, H * 0.26, o), H * 0.16);
            var mIt = new JObject
            {
                ["text"] = text,
                ["font"] = JLayouts.S(Pm, "font"),
                ["size"] = size,
                ["track"] = 0.05,
                ["lead"] = 1.1,
            };
            var m = JLayouts.Measure(mIt);
            var cy = H * 0.66 - m.H * 0.2;
            var @out = Tout(env);
            // 灯光抖着亮起
            var on = lt < 0.08 ? 0 : lt < 0.16 ? 1 : lt < 0.24 ? 0.25 : 1;
            var flick = J.R(s, (int)env.Step, 101) < 0.04 ? 0.5 : 1;
            var L = on * flick * @out;
            var shS = Math.Min(size * JLayouts.D(Pm, "k"), JLayouts.FitSize(text, JLayouts.S(Pm, "font")!, W * 0.92, H * 0.42, o));
            var shM = JLayouts.Measure(new JObject
            {
                ["text"] = text,
                ["font"] = JLayouts.S(Pm, "font"),
                ["size"] = shS,
                ["track"] = 0.05,
                ["lead"] = 1.1,
            });
            var sy = Math.Max(H * 0.06 + shM.H / 2, cy - m.H * 0.5 - shM.H * 0.42);
            if (env.Pass == "main" && L > 0)
            {
                var pool = IsDark(ScK(sc, "bg")) ? J.Mix(ScK(sc, "bg"), ScK(sc, "fg"), 0.16) : J.Mix(ScK(sc, "bg"), "#FFFFFF", 0.5);
                var R = Math.Max(shM.W, shS * 2.2) * 0.75;
                var ctx = env.Ctx;
                ctx.Save();
                ctx.Translate((float)(W / 2), (float)sy);
                ctx.Scale(1, 0.72f);
                using (var shader = Radial2(0, 0, 0, R,
                           new[] { (0.0, Cf(pool, L)), (1.0, Cf(pool, 0)) }))
                using (var p = new SKPaint { Shader = shader })
                {
                    ctx.DrawRect(new SKRect((float)-R, (float)-R, (float)R, (float)R), p);
                }

                ctx.Restore();
                // 影子更大更软，而且自己会动
                var t = env.Ltb;
                var d = cut.Dur;
                var turn = J.E.InOutCubic(J.Clamp((lt - d * JLayouts.D(Pm, "turn")) / 0.5));
                var sxk = 1 - 2 * turn;
                var twitch = J.R(s, (int)env.Step >> 1, 102) < 0.05 ? J.Rs(s, (int)env.Step, 103) * 8 : 0;
                var shC = IsDark(ScK(sc, "bg")) ? J.Mix(ScK(sc, "bg"), "#000000", 0.55) : J.Mix(ScK(sc, "bg"), ScK(sc, "fg"), 0.28);
                DrawIt(env, new JObject
                {
                    ["text"] = text,
                    ["font"] = JLayouts.S(Pm, "font"),
                    ["size"] = shS,
                    ["x"] = W / 2.0 + J.Noise1(t * 0.35, s) * size * 0.5,
                    ["y"] = sy + J.Noise1(t * 0.3, s + 2) * size * 0.1,
                    ["track"] = 0.05,
                    ["lead"] = 1.1,
                    ["sx"] = Math.Abs(sxk) < 0.04 ? 0.04 : sxk,
                    ["rot"] = JLayouts.D(Pm, "dir") * JLayouts.D(Pm, "lean") * J.Noise1(t * 0.25 + 3, s + 4) + twitch,
                    ["skew"] = J.Noise1(t * 0.2, s + 5) * 10,
                    ["color"] = shC,
                    ["alpha"] = 0.85 * L,
                    ["blur"] = env.AllowFilter ? shS * 0.03 : 0.0,
                    ["ghost"] = false,
                });
            }

            var bb = JLayouts.MainDraw(env, new JObject
            {
                ["text"] = text,
                ["font"] = JLayouts.S(Pm, "font"),
                ["size"] = size,
                ["x"] = W / 2.0,
                ["y"] = cy,
                ["color"] = ScK(sc, "fg"),
                ["track"] = 0.05,
                ["lead"] = 1.1,
            });
            var fe = Tin(env, 0.1, 0.5) * @out;
            if (fe > 0)
                env.Line(new[]
                {
                    new[] { W / 2 - m.W * 0.8 * fe, cy + m.H / 2 + size * 0.2 },
                    new[] { W / 2 + m.W * 0.8 * fe, cy + m.H / 2 + size * 0.2 },
                }, ScK(sc, "sub"), Math.Max(1, size * 0.012), 0.5, false);
            env.Body = bb;
        },
    });
}
