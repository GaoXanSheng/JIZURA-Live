// 移植自 frontend/src/engine/11p_horror3.js（horror 包 3/3：decor、背景、运镜、屏幕特效、
// 转场和三个风格）。
//
// 注册组：decor ×7 / bg ×4 / cam ×2 / fx ×3 / trans ×2 / style ×3（Reg.Styles，"不覆盖"语义）。
// RNG：plan 侧 Rng 顺序流逐行对位；Draw/Get 侧全走 J.R/J.Rs/J.Rr/J.H 确定性哈希。
//
// extra/set 标记：JS 条目上的 `set: 'horror'` 已落 IRegEntry.Set 真字段（RegD/B/C/F/X 统一赋值）；
// 风格上的 set 落在 StyleDef.Extra["set"]（Omakase 门禁读该键）；`ae: '…'` 行内注释。
//
// 桩/TODO：
// 1. fx ×3（hrSubliminal/hrSignalLoss/hrPassingShadow）的 draw(ctx, ev, k, I) 是整帧合成路径
//   （拿上一帧快照做 difference/multiply、黑屏出字、横穿暗影），Renderer 目前只有内置分支
//   （Renderer.cs 里 TODO(11p) 明说等 11p_fxB 定型签名）——本文件按元数据全量注册
//   （name/tags/w/dur/amp/glitchy/mid/pre），Draw 留空 + TODO。
// 2. trans 的 draw(ctx, A, B, p, I) 已定型为 TransDrawFn（Renderer + P11TreatTrans 同款），
//    hrStaticCut/hrBlink 全量移植；createPattern + DOMMatrix → SKShader.CreateBitmap + SKMatrix。
// 3. bg 的 bgT（同 bg 同 seed 连跑的起始时刻）用 ConditionalWeakTable 按 Cut 实例缓存
//   （对位 JS WeakMap bgRun）。
//
// 跨文件符号对位：J.centerBB→JLayouts.CenterBb、J.blob/poly/line/circle/arc/rect/rrect/
// polyPartial→env.*、J.mix/lum/rgba(本地)/fitContrast/h/r/rs/rr/noise1/clamp/lerp/smooth→J.*。

using System.Globalization;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace JizuraLive.Engine;

public static class P11Horror3
{
    private const string P = "horror3";
    private static int _installed;

    // JS reg 助手：Object.assign({set: 'horror'}, def) —— 各组注册前统一落 IRegEntry.Set
    private static void RegD(string key, DecorDef def) { def.Set = "horror"; Reg.Register("decor", key, def, P); }
    private static void RegB(string key, BgDef def) { def.Set = "horror"; Reg.Register("bg", key, def, P); }
    private static void RegC(string key, CamDef def) { def.Set = "horror"; Reg.Register("cam", key, def, P); }
    private static void RegF(string key, FxDef def) { def.Set = "horror"; Reg.Register("fx", key, def, P); }
    private static void RegX(string key, TransDef def) { def.Set = "horror"; Reg.Register("trans", key, def, P); }

    // ================= 本包 helper（JS L10-77 逐条对位） =================

    private static double U(Env env) => Math.Min(env.W, env.H);
    private static bool IsDark(string? c) => J.Lum(c ?? "#000000") < 0.45;

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

    private static string LightOf(Scheme sc) =>
        J.Lum(ScK(sc, "fg")) > J.Lum(ScK(sc, "bg")) ? ScK(sc, "fg") : ScK(sc, "bg");

    private static string NightC(Scheme sc) => J.Mix(ScK(sc, "bg"), "#000000", IsDark(ScK(sc, "bg")) ? 0.72 : 0.9);

    /// <summary>layC(sc, k)：配色底/字色的插值（背景结构线用）。</summary>
    private static string LayC(Scheme sc, double k) => J.Mix(ScK(sc, "bg"), ScK(sc, "fg"), k);

    /// <summary>配色读取兜底（JS 直接读 sc.xxx；hrMold 会读 accent2/sub，缺字段回黑）。</summary>
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

    private static double InOut(Env env, double d = 0.35) =>
        J.E.OutCubic(J.Clamp(env.Lt / d)) * (1 - J.E.InCubic(env.POut));

    private static double Wrap(double v, double m) => ((v % m) + m) % m;

    private static bool Overlaps(BBox bb, double x0, double y0, double x1, double y1, double pad = 0) =>
        !(x1 < bb.X0 - pad || x0 > bb.X1 + pad || y1 < bb.Y0 - pad || y0 > bb.Y1 + pad);

    // 噪点纹理块：只建一次，确定性（JS L27-43；4 张 canvas → SKBitmap 缓存）
    private static readonly SKBitmap?[] Noise = new SKBitmap?[4];

    private static SKBitmap NoiseCv(int k)
    {
        k = ((k % 4) + 4) % 4;
        if (Noise[k] is { } hit) return hit;
        const int w = 128, h = 96;
        var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bytes = bmp.Bytes; // Bgra8888：B,G,R,A 逐像素
        for (var i = 0; i < w * h; i++)
        {
            var v = (byte)(Math.Pow(J.R(i, k, 5511), 1.3) * 255);
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
    private static readonly SKSamplingOptions SampLinear = new(SKFilterMode.Linear, SKMipmapMode.None);

    private static SKPaint AlphaPaint(double alpha) => new()
    {
        Color = SKColors.White.WithAlpha((byte)Math.Round(J.Clamp(alpha) * 255)),
    };

    // bgT：该背景从开始算起的时间；相邻同 bg 且同 seed 的 cut 视为同一次（JS L45-60）。
    private static readonly ConditionalWeakTable<Cut, StrongBox<double>> BgRun = new();

    private static double BgT(Env env)
    {
        var c = env.Cut;
        if (c == null) return env.T;
        var box = BgRun.GetValue(c, _ =>
        {
            var s = c.Start;
            var cuts = env.Plan?.Cuts;
            if (cuts != null)
            {
                var seed0 = c.BgP["seed"]?.Value<double>() ?? 0;
                for (var i = c.Index - 1; i >= 0 && i < cuts.Count; i--)
                {
                    var p = cuts[i];
                    var pSeed = p.BgP["seed"]?.Value<double>() ?? 0;
                    if (p.Bg == c.Bg && pSeed == seed0 && Math.Abs(p.End - s) < 0.06) s = p.Start;
                    else break;
                }
            }

            return new StrongBox<double>(s);
        });
        return env.T - box.Value;
    }

    /// <summary>bg 包装：draw(env, Pm, ctx) 外面套 ctx save/restore。JS bgReg。</summary>
    private static BgDef BgReg(string key, string name, string[] tags, double w, bool subtle,
        Func<Rng, StyleDef, JObject>? plan, Action<Env, JObject, SKCanvas> draw)
    {
        return new BgDef
        {
            Key = key,
            Name = name,
            Tags = tags,
            W = w,
            Subtle = subtle,
            Plan = plan,
            Draw = env =>
            {
                var ctx = env.Ctx;
                ctx.Save();
                try
                {
                    draw(env, env.Cut?.BgP ?? new JObject(), ctx);
                }
                finally
                {
                    ctx.Restore();
                }
            },
        };
    }

    /// <summary>灯光亮度 0..1，按输出帧率时钟采样：大部分时间亮着，偶尔闪断。JS lamp。</summary>
    private static double Lamp(double t, int seed, double rate = 1, int fps = 24)
    {
        var st = (int)Math.Floor(t * fps);
        var run = st >> 2;
        if (J.R(seed, run, 7) < 0.1 * rate) return J.R(seed, st, 8) < 0.5 ? 0.15 : 0.55;
        return 0.88 + 0.12 * J.R(seed, st, 9);
    }

    // ================= decor（JS L80-335 出现序） =================

    // ---------------- hrScratches（JS L80-112） ----------------

    private static void RegisterHrScratches() => RegD("hrScratches", new DecorDef
    {
        Name = "引っ掻き傷",
        Tags = new[] { "horror", "glitch" },
        W = 0.9,
        Layer = "front",
        Draw = (env, d) =>
        {
            if (env.Pass != "main") return;
            var bb = JLayouts.CenterBb(env, env.Body);
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var s = d.Seed;
            var u = U(env);
            var @out = 1 - J.E.InCubic(env.POut);
            var n = d.N != 0 ? d.N : 2;
            for (var g = 0; g < n; g++)
            {
                var e = J.E.OutExpo(J.Clamp((env.Lt - 0.08 - g * 0.18) / 0.16)) * @out;
                if (e <= 0) continue;
                var corner = (g + d.V) % 4;
                var right = corner % 2 == 1;
                var low = corner >= 2;
                var L = u * J.Rr(0.28, 0.42, s, g, 1);
                var ang = (right ? -1 : 1) * J.Rr(55, 75, s, g, 2) * J.DEG * (low ? -1 : 1);
                var cx = right ? J.Rr(W * 0.72, W * 0.9, s, g, 3) : J.Rr(W * 0.1, W * 0.28, s, g, 3);
                var cy = low ? J.Rr(H * 0.72, H * 0.88, s, g, 4) : J.Rr(H * 0.12, H * 0.28, s, g, 4);
                if (Overlaps(bb, cx - L / 2, cy - L / 2, cx + L / 2, cy + L / 2, u * 0.02))
                    cy = low ? Math.Max(cy, bb.Y1 + L * 0.6) : Math.Min(cy, bb.Y0 - L * 0.6);
                var dx = Math.Sin(ang);
                var dy = -Math.Cos(ang);
                var px = Math.Cos(ang);
                var py = Math.Sin(ang);
                var col = g % 2 != 0 && J.Contrast(ScK(sc, "accent"), ScK(sc, "bg")) > 1.8
                    ? ScK(sc, "accent")
                    : ScK(sc, "fg");
                for (var k = 0; k < 4; k++)
                {
                    var off = (k - 1.5) * u * 0.042;
                    var len = L * (0.75 + 0.3 * J.R(s, g, k, 5)) * e;
                    var st = J.R(s, g, k, 6) * L * 0.1;
                    var x0 = cx + px * off - dx * L / 2 + dx * st;
                    var y0 = cy + py * off - dy * L / 2 + dy * st;
                    const int m = 10;
                    var pts = new double[m + 1][];
                    for (var i = 0; i <= m; i++)
                    {
                        var f = i / (double)m;
                        var j = J.Rs(s, g, k, i) * u * 0.004;
                        pts[i] = new[] { x0 + dx * len * f + px * j, y0 + dy * len * f + py * j };
                    }

                    // 中间粗、两端细
                    var lw = u * 0.011;
                    for (var i = 0; i < m; i++)
                        env.Line(new[] { pts[i], pts[i + 1] }, col,
                            Math.Max(1, lw * Math.Sin(Math.PI * (i + 0.5) / m)), 0.85, false);
                }
            }
        },
    });

    // ---------------- hrSigil（JS L115-149） ----------------

    private static void RegisterHrSigil() => RegD("hrSigil", new DecorDef
    {
        Name = "魔法陣",
        Tags = new[] { "horror", "graphic" },
        W = 0.7,
        Layer = "back",
        Subtle = true,
        Draw = (env, d) =>
        {
            if (env.Pass != "main") return;
            var bb = JLayouts.CenterBb(env, env.Body);
            var sc = env.Sc!;
            var s = d.Seed;
            var u = U(env);
            var e = J.E.InOutSine(J.Clamp(env.Lt / 1.2));
            var @out = 1 - J.E.InCubic(env.POut);
            if (@out <= 0) return;
            var cx = (bb.X0 + bb.X1) / 2;
            var cy = (bb.Y0 + bb.Y1) / 2;
            var R = Math.Min(u * 0.46, Math.Max(bb.X1 - bb.X0, bb.Y1 - bb.Y0) * 0.62 + u * 0.08);
            var col = IsDark(ScK(sc, "bg")) ? LayC(sc, 0.3) : LayC(sc, 0.28);
            var lw = Math.Max(1.2, u * 0.0022);
            var rot = env.Ltb * 4 * (d.Right ? -1 : 1) * J.DEG;
            var a = @out;
            env.Arc(cx, cy, R, -90, -90 + 360 * e, col, lw * 1.4, a, false);
            env.Arc(cx, cy, R * 0.9, 90, 90 + 360 * e, col, lw, a, false);
            env.Arc(cx, cy, R * 0.62, -90, -90 + 360 * e, col, lw, a * 0.8, false);
            var pts = new List<double[]>();
            for (var i = 0; i <= 7; i++)
            {
                var an = rot - Math.PI / 2 + i * 3 % 7 / 7.0 * J.TAU;
                pts.Add(new[] { cx + Math.Cos(an) * R * 0.9, cy + Math.Sin(an) * R * 0.9 });
            }

            env.PolyPartial(pts.ToArray(), J.Clamp((env.Lt - 0.3) / 1.2), col, lw, a, false);
            // 两圈之间的刻痕
            const int m = 42;
            for (var i = 0; i < m * e; i++)
            {
                var an = rot * -0.6 + i / (double)m * J.TAU;
                var t = (int)(J.H(s, i, 3) % 4);
                var r0 = R * 0.915;
                var r1 = R * 0.985;
                var c = Math.Cos(an);
                var sn = Math.Sin(an);
                if (t == 0)
                    env.Line(new[] { new[] { cx + c * r0, cy + sn * r0 }, new[] { cx + c * r1, cy + sn * r1 } }, col, lw, a, false);
                else if (t == 1)
                    env.Circle(cx + c * (r0 + r1) / 2, cy + sn * (r0 + r1) / 2, R * 0.012, null, col, lw, a, false);
                else if (t == 2)
                {
                    var q = (r0 + r1) / 2;
                    var dd = R * 0.018;
                    env.Line(new[]
                    {
                        new[] { cx + c * q - sn * dd, cy + sn * q + c * dd },
                        new[] { cx + c * q + sn * dd, cy + sn * q - c * dd },
                    }, col, lw, a, false);
                }
            }
        },
    });

    // ---------------- hrWatchEye（JS L152-203） ----------------

    private static void RegisterHrWatchEye() => RegD("hrWatchEye", new DecorDef
    {
        Name = "見ている目",
        Tags = new[] { "horror", "graphic" },
        W = 0.8,
        Layer = "front",
        Draw = (env, d) =>
        {
            if (env.Pass != "main") return;
            var bb = JLayouts.CenterBb(env, env.Body);
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var s = d.Seed;
            var u = U(env);
            var ew = u * (d.Big ? 0.22 : 0.16);
            var eh = ew * 0.26;
            var x = d.Right ? W - u * 0.07 - ew / 2 : u * 0.07 + ew / 2;
            var y = d.Low ? H - u * 0.08 - eh : u * 0.08 + eh;
            if (Overlaps(bb, x - ew / 2, y - eh, x + ew / 2, y + eh, u * 0.02))
                y = d.Low
                    ? Math.Max(y, Math.Min(H - eh * 1.2, bb.Y1 + eh * 1.6))
                    : Math.Min(y, Math.Max(eh * 1.2, bb.Y0 - eh * 1.6));
            var open = J.E.OutCubic(J.Clamp((env.Lt - 0.2) / 0.6)) * (1 - J.E.InCubic(env.POut));
            // 不定期眨眼
            var bt = env.Ltb;
            for (var k = 0; k < 3; k++)
            {
                var at = 0.9 + J.R(s, k, 5) * 3 + k * 1.3;
                var dist = Math.Abs(bt - at);
                if (dist < 0.08) open *= dist / 0.08;
            }

            if (open <= 0.01)
            {
                env.Line(new[] { new[] { x - ew / 2, y }, new[] { x + ew / 2, y } }, ScK(sc, "fg"),
                    Math.Max(1.2, u * 0.003), 0.8 * (1 - J.E.InCubic(env.POut)) * J.Clamp(env.Lt / 0.2), false);
                return;
            }

            var col = ScK(sc, "fg");
            var lw = Math.Max(1.2, u * 0.003);
            var ctx = env.Ctx;

            double[][] Lid(double sgn)
            {
                var pts = new double[17][];
                for (var i = 0; i <= 16; i++)
                {
                    var f = i / 16.0;
                    var xx = x - ew / 2 + ew * f;
                    pts[i] = new[] { xx, y + sgn * Math.Sin(Math.PI * f) * eh * open };
                }

                return pts;
            }

            var top = Lid(-1);
            var bot = Lid(1);
            // 虹膜和瞳孔裁剪在眼眶里，望向文字
            var tx = (bb.X0 + bb.X1) / 2;
            var ty = (bb.Y0 + bb.Y1) / 2;
            var an = Math.Atan2(ty - y, tx - x);
            var look = Math.Min(1, Hypot(tx - x, ty - y) / u);
            var ix = x + Math.Cos(an) * ew * 0.18 * look + J.Noise1(bt * 0.8, s) * ew * 0.03;
            var iy = y + Math.Sin(an) * eh * 0.3 * look;
            ctx.Save();
            using (var clip = new SKPath())
            {
                clip.MoveTo((float)top[0][0], (float)top[0][1]);
                for (var i = 1; i < top.Length; i++) clip.LineTo((float)top[i][0], (float)top[i][1]);
                for (var i = bot.Length - 1; i >= 0; i--) clip.LineTo((float)bot[i][0], (float)bot[i][1]);
                clip.Close();
                ctx.ClipPath(clip);
            }

            env.Circle(ix, iy, eh * 0.78, null, col, lw, 1, false);
            env.Circle(ix, iy, eh * 0.34, IsDark(ScK(sc, "bg")) ? ScK(sc, "accent") : ScK(sc, "fg"), null, 0, 1, false);
            ctx.Restore();
            env.Line(top, col, lw * 1.3, 1, false);
            env.Line(bot, col, lw, 1, false);
            for (var k = 0; k < 5; k++)
            {
                var f = 0.2 + k * 0.15;
                var p = top[(int)Math.Round(f * 16)];
                env.Line(new[] { p, new[] { p[0] + (f - 0.5) * ew * 0.12, p[1] - eh * 0.35 * open } }, col, lw * 0.8, 0.8, false);
            }
        },
    });

    // ---------------- hrStaticPatch（JS L206-232） ----------------

    private static void RegisterHrStaticPatch() => RegD("hrStaticPatch", new DecorDef
    {
        Name = "砂嵐の欠片",
        Tags = new[] { "horror", "glitch" },
        W = 0.8,
        Layer = "front",
        Draw = (env, d) =>
        {
            if (env.Pass != "main") return;
            var bb = JLayouts.CenterBb(env, env.Body);
            double W = env.W, H = env.H;
            var s = d.Seed;
            var st = (int)env.Step;
            var u = U(env);
            var ctx = env.Ctx;
            var a0 = InOut(env, 0.2);
            if (a0 <= 0) return;
            var n = 3 + (d.N != 0 ? d.N : 2) * 2;
            ctx.Save();
            using var img = SKImage.FromBitmap(NoiseCv(st));
            for (var k = 0; k < n; k++)
            {
                if (J.R(s, k, st, 1) < 0.35) continue;
                var w = u * J.Rr(0.06, 0.2, s, k, 2);
                var h = u * J.Rr(0.02, 0.07, s, k, 3);
                var edge = (int)(J.H(s, k, 4) % 4);
                var x = J.R(s, k, 5) * (W - w);
                var y = J.R(s, k, 6) * (H - h);
                if (edge == 0) y = J.Rr(0.02, 0.18, s, k, 7) * H;
                else if (edge == 1) y = H - h - J.Rr(0.02, 0.18, s, k, 7) * H;
                else if (edge == 2) x = J.Rr(0.01, 0.1, s, k, 7) * W;
                else x = W - w - J.Rr(0.01, 0.1, s, k, 7) * W;
                x += J.Rs(s, k, st >> 1, 8) * u * 0.02;
                if (Overlaps(bb, x, y, x + w, y + h, u * 0.02)) continue;
                var sx = (int)Math.Floor(J.R(s, k, st, 9) * 64);
                var sy = (int)Math.Floor(J.R(s, k, st, 10) * 48);
                using var p = AlphaPaint(a0 * J.Rr(0.45, 0.85, s, k, st, 11));
                // ctx.drawImage(N, sx, sy, 48, 24, x, y, w, h)（imageSmoothing off）
                ctx.DrawImage(img,
                    new SKRect(sx, sy, sx + 48, sy + 24),
                    new SKRect((float)x, (float)y, (float)(x + w), (float)(y + h)), SampNearest, p);
            }

            ctx.Restore();
        },
    });

    // ---------------- hrDustBeam（JS L235-275） ----------------

    private static void RegisterHrDustBeam() => RegD("hrDustBeam", new DecorDef
    {
        Name = "光の筋と埃",
        Tags = new[] { "horror", "calm", "emotional" },
        W = 0.8,
        Layer = "back",
        Subtle = true,
        Draw = (env, d) =>
        {
            if (env.Pass != "main") return;
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var s = d.Seed;
            var u = U(env);
            var a = InOut(env, 0.8);
            if (a <= 0) return;
            var L = LightOf(sc);
            var right = d.Right;
            var x0 = right ? W * 0.78 : W * 0.22;
            var w0 = W * 0.12;
            var w1 = W * 0.34;
            var sl = (right ? -1 : 1) * W * 0.28;
            var poly = new[]
            {
                new[] { x0 - w0 / 2, -2.0 }, new[] { x0 + w0 / 2, -2.0 },
                new[] { x0 + sl + w1 / 2, H + 2.0 }, new[] { x0 + sl - w1 / 2, H + 2.0 },
            };
            var k = IsDark(ScK(sc, "bg")) ? 0.1 : 0.22;
            var ctx = env.Ctx;
            ctx.Save();
            using (var path = new SKPath())
            {
                path.MoveTo((float)poly[0][0], (float)poly[0][1]);
                for (var i = 1; i < poly.Length; i++) path.LineTo((float)poly[i][0], (float)poly[i][1]);
                path.Close();
                if (!IsDark(ScK(sc, "bg")))
                {
                    // 亮纸配色：改为压暗光柱之外的房间（evenodd：整屏 rect + 光柱 poly）
                    using var room = new SKPath { FillType = SKPathFillType.EvenOdd };
                    room.AddRect(new SKRect(-10, -10, (float)(W + 10), (float)(H + 10)));
                    room.AddPath(path);
                    using (var dp = new SKPaint
                           {
                               IsAntialias = true,
                               Color = Env.SkColor(NightC(sc), 0.12 * a),
                           })
                        ctx.DrawPath(room, dp);
                }

                using (var shader = SKShader.CreateLinearGradient(
                           new SKPoint(0, 0), new SKPoint(0, (float)H),
                           new[] { Env.SkColor(L, k * a), Env.SkColor(L, 0) },
                           new float[] { 0f, 1f }, SKShaderTileMode.Clamp))
                using (var fp = new SKPaint { IsAntialias = true, Shader = shader })
                    ctx.DrawPath(path, fp);
                ctx.ClipPath(path);
            }

            var dustC = IsDark(ScK(sc, "bg")) ? L : ScK(sc, "sub");
            var t = env.Ltb;
            for (var i = 0; i < 46; i++)
            {
                var fy = Wrap(J.R(s, i, 1) + t * J.Rr(0.004, 0.02, s, i, 2), 1);
                var y = fy * H;
                var f = J.R(s, i, 3);
                var cx = x0 + sl * fy;
                var ww = w0 + (w1 - w0) * fy;
                var x = cx + (f - 0.5) * ww + Math.Sin(t * 0.7 + i) * u * 0.01;
                env.Circle(x, y, u * J.Rr(0.0012, 0.0035, s, i, 4), dustC, null, 0,
                    a * (0.35 + 0.35 * Math.Sin(t * 1.5 + i)) * (1 - fy * 0.6), false);
            }

            ctx.Restore();
        },
    });

    // ---------------- hrDrips（JS L278-302） ----------------

    private static void RegisterHrDrips() => RegD("hrDrips", new DecorDef
    {
        Name = "垂れる墨",
        Tags = new[] { "horror", "emotional" },
        W = 0.8,
        Layer = "front",
        Draw = (env, d) =>
        {
            if (env.Pass != "main") return;
            var bb = JLayouts.CenterBb(env, env.Body);
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var s = d.Seed;
            var u = U(env);
            var @out = 1 - J.E.InCubic(env.POut);
            if (@out <= 0) return;
            var col = IsDark(ScK(sc, "bg")) ? J.Mix(ScK(sc, "accent"), ScK(sc, "bg"), 0.35) : NightC(sc);
            var band = u * 0.03 * J.E.OutCubic(J.Clamp(env.Lt / 0.4));
            var pts = new List<double[]> { new[] { -4.0, -4.0 }, new[] { W + 4, -4.0 } };
            for (var i = 40; i >= 0; i--)
                pts.Add(new[] { W * i / 40.0, band * (0.6 + 0.6 * J.R(s, i, 1)) });
            env.Poly(pts.ToArray(), col, 0.9 * @out, false);
            var n = 7 + d.V * 3;
            var lim = Math.Max(band * 2, bb.Y0 - u * 0.05);
            for (var k = 0; k < n; k++)
            {
                var x = W * (0.03 + 0.94 * J.R(s, k, 2));
                var g = J.E.OutCubic(J.Clamp((env.Lt - J.R(s, k, 3) * 0.8) / (2.5 + J.R(s, k, 4) * 3)));
                var L = u * J.Rr(0.06, 0.3, s, k, 5) * g;
                if (x > bb.X0 - u * 0.03 && x < bb.X1 + u * 0.03) L = Math.Min(L, lim - band);
                if (L <= 1) continue;
                var w = u * J.Rr(0.004, 0.012, s, k, 6);
                env.Poly(new[]
                {
                    new[] { x - w, band * 0.5 }, new[] { x + w, band * 0.5 },
                    new[] { x + w * 0.7, band + L }, new[] { x - w * 0.7, band + L },
                }, col, 0.9 * @out, false);
                env.Circle(x, band + L, w * 1.35, col, null, 0, 0.9 * @out, false);
            }
        },
    });

    // ---------------- hrCracks（JS L305-335） ----------------

    private static void RegisterHrCracks() => RegD("hrCracks", new DecorDef
    {
        Name = "ひび割れ",
        Tags = new[] { "horror", "graphic", "glitch" },
        W = 0.7,
        Layer = "front",
        Draw = (env, d) =>
        {
            if (env.Pass != "main") return;
            var bb = JLayouts.CenterBb(env, env.Body);
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var s = d.Seed;
            var u = U(env);
            var @out = 1 - J.E.InCubic(env.POut);
            if (@out <= 0) return;
            var e = J.E.OutCubic(J.Clamp(env.Lt / 1.6));
            var ox = d.Right ? W : 0;
            var oy = d.Low ? H : 0;
            var col = ScK(sc, "fg");
            var lw = Math.Max(1, u * 0.0018);
            var @base = Math.Atan2(H / 2 - oy, W / 2 - ox);

            void Branch(double x, double y, double an, double len, int depth, int id)
            {
                var pts = new List<double[]> { new[] { x, y } };
                var cx = x;
                var cy = y;
                const int m = 7;
                for (var i = 1; i <= m; i++)
                {
                    var a = an + J.Rs(s, id, i, 1) * 0.5;
                    cx += Math.Cos(a) * len / m;
                    cy += Math.Sin(a) * len / m;
                    if (Overlaps(bb, cx, cy, cx, cy, u * 0.03)) break;
                    pts.Add(new[] { cx, cy });
                }

                env.PolyPartial(pts.ToArray(), J.Clamp(e * (1 + depth * 0.2) - depth * 0.25), col,
                    lw * (1.4 - depth * 0.35), 0.75 * @out, false);
                if (depth >= 2) return;
                for (var k = 0; k < 2; k++)
                {
                    var j = 2 + (int)(J.H(s, id, k, 3) % (uint)Math.Max(1, pts.Count - 1));
                    var p = pts[Math.Min(j, pts.Count - 1)];
                    Branch(p[0], p[1], an + (k != 0 ? 0.6 : -0.6) * J.Rr(0.6, 1.2, s, id, k, 4),
                        len * 0.5, depth + 1, id * 3 + k + 1);
                }
            }

            for (var r = 0; r < 3; r++)
                Branch(ox, oy, @base + (r - 1) * 0.35 + J.Rs(s, r, 9) * 0.15, u * J.Rr(0.28, 0.42, s, r, 10), 0, r + 1);
        },
    });

    // ================= bg（JS L337-506 出现序） =================

    // ---------------- hrFailingLamp（JS L337-361） ----------------

    private static void RegisterHrFailingLamp() => RegB("hrFailingLamp", BgReg("hrFailingLamp",
        "切れかけの灯", new[] { "horror", "emotional" }, 0.9, true,
        (rng, _) => new JObject
        {
            ["seed"] = (double)rng.Int(1, 1000000000),
            ["x"] = rng.Range(0.35, 0.65),
            ["rate"] = rng.Range(0.7, 1.3),
        },
        (env, Pm, ctx) =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var t = env.T;
            var u = U(env);
            var lv = Lamp(t, (int)JLayouts.D(Pm, "seed"), NumOr(Pm, "rate", 1), env.Fps);
            var fi = J.E.OutCubic(J.Clamp(BgT(env) / 0.6));
            var L = LightOf(sc);
            var dk = NightC(sc);
            var cx = W * NumOr(Pm, "x", 0.5);
            var cy = -u * 0.1;
            using (var g = Radial2(cx, cy, 0, Hypot(W, H) * 0.9,
                       new[]
                       {
                           (0.0, Cf(L, (IsDark(ScK(sc, "bg")) ? 0.16 : 0.3) * lv * fi)),
                           (0.5, Cf(L, (IsDark(ScK(sc, "bg")) ? 0.04 : 0.08) * lv * fi)),
                           (1.0, Cf(L, 0)),
                       }))
            using (var p = new SKPaint { Shader = g })
                ctx.DrawRect(new SKRect(0, 0, (float)W, (float)H), p);
            using (var v = Radial2(W / 2.0, H / 2.0, Math.Min(W, H) * 0.3, Hypot(W, H) * 0.6,
                       new[] { (0.0, Cf(dk, 0)), (1.0, Cf(dk, (0.75 - 0.25 * lv) * fi)) }))
            using (var p = new SKPaint { Shader = v })
                ctx.DrawRect(new SKRect(0, 0, (float)W, (float)H), p);
            // 顶上的灯管本身
            using (var p = new SKPaint
                   {
                       Color = Env.SkColor(LayC(sc, IsDark(ScK(sc, "bg")) ? 0.35 : 0.15), (0.25 + 0.5 * lv) * fi),
                   })
                ctx.DrawRect(new SKRect((float)(cx - u * 0.12), (float)(u * 0.012),
                    (float)(cx + u * 0.12), (float)(u * 0.012 + Math.Max(2, u * 0.006))), p);
        }));

    // ---------------- hrCorridor（JS L363-429） ----------------

    private static void RegisterHrCorridor() => RegB("hrCorridor", BgReg("hrCorridor",
        "暗い廊下", new[] { "horror", "graphic" }, 0.8, false,
        (rng, _) => new JObject
        {
            ["seed"] = (double)rng.Int(1, 1000000000),
            ["vx"] = rng.Range(0.44, 0.56),
            ["vy"] = rng.Range(0.44, 0.54),
            ["spd"] = rng.Range(0.25, 0.45),
            ["doors"] = rng.Chance(0.75),
        },
        (env, Pm, ctx) =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var t = BgT(env);
            var u = U(env);
            var fi = J.E.OutCubic(J.Clamp(t / 0.6));
            var vx = W * NumOr(Pm, "vx", 0.5);
            var vy = H * NumOr(Pm, "vy", 0.5);
            var bw = W * 0.08;
            var bh = H * 0.1;
            var col = LayC(sc, IsDark(ScK(sc, "bg")) ? 0.28 : 0.22);
            var lw = Math.Max(1.2, u * 0.0026);
            // 尽头的灯（会闪）
            var lv = Lamp(env.T, (int)JLayouts.D(Pm, "seed") + 1, 1, env.Fps);
            using (var g = Radial2(vx, vy, 0, Math.Max(bw, bh) * 3,
                       new[]
                       {
                           (0.0, Cf(LightOf(sc), (IsDark(ScK(sc, "bg")) ? 0.22 : 0.3) * lv * fi)),
                           (1.0, Cf(LightOf(sc), 0)),
                       }))
            using (var p = new SKPaint { Shader = g })
                ctx.DrawRect(new SKRect((float)(vx - bw * 4), (float)(vy - bh * 4),
                    (float)(vx + bw * 4), (float)(vy + bh * 4)), p);
            var back = new[]
            {
                new[] { vx - bw, vy - bh }, new[] { vx + bw, vy - bh },
                new[] { vx + bw, vy + bh }, new[] { vx - bw, vy + bh },
            };
            var corners = new[] { new[] { 0.0, 0.0 }, new[] { W, 0.0 }, new[] { W, H }, new[] { 0.0, H } };
            using (var path = new SKPath())
            {
                for (var i = 0; i < 4; i++)
                {
                    path.MoveTo((float)corners[i][0], (float)corners[i][1]);
                    path.LineTo((float)back[i][0], (float)back[i][1]);
                }

                path.AddRect(new SKRect((float)(vx - bw), (float)(vy - bh), (float)(vx + bw), (float)(vy + bh)));
                // 朝镜头滑来的门框
                const int n = 7;
                var sp = NumOr(Pm, "spd", 0.35);
                for (var k = 0; k < n; k++)
                {
                    var f = Wrap(k / (double)n + t * sp * 0.12, 1);
                    var z = Math.Pow(f, 2.2);
                    var x0 = J.Lerp(vx - bw, 0, z);
                    var x1 = J.Lerp(vx + bw, W, z);
                    var y0 = J.Lerp(vy - bh, 0, z);
                    var y1 = J.Lerp(vy + bh, H, z);
                    path.MoveTo((float)x0, (float)y0);
                    path.LineTo((float)x1, (float)y0);
                    path.MoveTo((float)x0, (float)y1);
                    path.LineTo((float)x1, (float)y1);
                    if (JLayouts.B(Pm, "doors") && k % 2 == 0)
                    {
                        // 两侧墙上的门：天花线和地脚线之间的竖线
                        var z2 = Math.Pow(Wrap(f + 0.05, 1), 2.2);
                        if (z2 > z)
                            for (var side = 0; side < 2; side++)
                            {
                                var xa = side != 0 ? x1 : x0;
                                var xb = side != 0 ? J.Lerp(vx + bw, W, z2) : J.Lerp(vx - bw, 0, z2);
                                var ya = J.Lerp(y0, y1, 0.25);
                                var yb = J.Lerp(J.Lerp(vy - bh, 0, z2), J.Lerp(vy + bh, H, z2), 0.25);
                                path.MoveTo((float)xa, (float)y1);
                                path.LineTo((float)xa, (float)ya);
                                path.LineTo((float)xb, (float)yb);
                                path.LineTo((float)xb, (float)J.Lerp(vy + bh, H, z2));
                            }
                    }
                }

                using var p = new SKPaint
                {
                    IsAntialias = true,
                    IsStroke = true,
                    StrokeWidth = (float)lw,
                    Color = Env.SkColor(col, fi),
                };
                ctx.DrawPath(path, p);
            }

            using (var v = Radial2(vx, vy, Math.Min(W, H) * 0.2, Hypot(W, H) * 0.65,
                       new[] { (0.0, Cf(NightC(sc), 0)), (1.0, Cf(NightC(sc), 0.55 * fi)) }))
            using (var p = new SKPaint { Shader = v })
                ctx.DrawRect(new SKRect(0, 0, (float)W, (float)H), p);
        }));

    // ---------------- hrMold（JS L431-467） ----------------

    private static void RegisterHrMold() => RegB("hrMold", BgReg("hrMold",
        "広がる染み", new[] { "horror", "emotional" }, 0.7, true,
        (rng, _) => new JObject
        {
            ["seed"] = (double)rng.Int(1, 1000000000),
            ["n"] = (double)rng.Int(3, 5),
            ["k"] = rng.Range(0.08, 0.14),
        },
        (env, Pm, ctx) =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var t = BgT(env);
            var u = U(env);
            var s = (int)JLayouts.D(Pm, "seed");
            var k = NumOr(Pm, "k", 0.1);
            var stain = IsDark(ScK(sc, "bg"))
                ? J.Mix(ScK(sc, "bg"), J.Mix(ScK(sc, "fg"), ScK(sc, "accent2"), 0.5), k)
                : J.Mix(ScK(sc, "bg"), J.Mix(ScK(sc, "sub"), "#000000", 0.3), k * 1.4);
            var rim = IsDark(ScK(sc, "bg"))
                ? J.Mix(ScK(sc, "bg"), ScK(sc, "fg"), k * 1.6)
                : J.Mix(ScK(sc, "bg"), "#000000", k * 1.3);
            for (var i = 0; i < (int)NumOr(Pm, "n", 4); i++)
            {
                var edge = (int)(J.H(s, i, 1) % 4);
                var f = J.R(s, i, 2);
                var cx = edge == 0 ? f * W : edge == 1 ? W : edge == 2 ? f * W : 0;
                var cy = edge == 0 ? 0 : edge == 1 ? f * H : edge == 2 ? H : f * H;
                var R = u * J.Rr(0.25, 0.5, s, i, 3)
                        * (0.35 + 0.65 * J.E.OutCubic(J.Clamp(t / J.Rr(8, 14, s, i, 4))))
                        * J.E.OutCubic(J.Clamp(t / 0.8));
                const int m = 36;
                var pts = new List<double[]>();
                for (var j = 0; j < m; j++)
                {
                    var a = j / (double)m * J.TAU;
                    var r = R * (0.7 + 0.45 * J.Noise1(j * 0.45, s + i) + 0.04 * Math.Sin(t * 0.3 + j));
                    pts.Add(new[] { cx + Math.Cos(a) * r, cy + Math.Sin(a) * r });
                }

                env.Blob(pts.ToArray(), stain, 0.9, false);
                using (var path = new SKPath())
                {
                    path.MoveTo((float)pts[0][0], (float)pts[0][1]);
                    for (var j = 1; j < pts.Count; j++) path.LineTo((float)pts[j][0], (float)pts[j][1]);
                    path.Close();
                    using var p = new SKPaint
                    {
                        IsAntialias = true,
                        IsStroke = true,
                        StrokeWidth = (float)Math.Max(1, u * 0.003),
                        Color = Env.SkColor(rim, 0.6),
                    };
                    ctx.DrawPath(path, p);
                }

                // 污渍边缘的斑点
                for (var j = 0; j < 14; j++)
                {
                    var a = J.R(s, i, j, 5) * J.TAU;
                    var r = R * J.Rr(1.02, 1.25, s, i, j, 6);
                    env.Circle(cx + Math.Cos(a) * r, cy + Math.Sin(a) * r, u * J.Rr(0.002, 0.008, s, i, j, 7),
                        stain, null, 0, 0.9, false);
                }
            }
        }));

    // ---------------- hrDeadTrees（JS L469-506） ----------------

    private static void RegisterHrDeadTrees() => RegB("hrDeadTrees", BgReg("hrDeadTrees",
        "枯れ木の森", new[] { "horror", "calm", "emotional" }, 0.7, false,
        (rng, _) => new JObject
        {
            ["seed"] = (double)rng.Int(1, 1000000000),
            ["n"] = (double)rng.Int(5, 8),
            ["fog"] = rng.Range(0.1, 0.2),
        },
        (env, Pm, ctx) =>
        {
            double W = env.W, H = env.H;
            var sc = env.Sc!;
            var t = env.T;
            var u = U(env);
            var s = (int)JLayouts.D(Pm, "seed");
            var fi = J.E.OutCubic(J.Clamp(BgT(env) / 0.8));
            double[][] layers = { new[] { 0.07, 0.75, 0.8 }, new[] { 0.13, 1.0, 1.15 } };
            for (var l = 0; l < layers.Length; l++)
            {
                var k = layers[l][0];
                var hk = layers[l][1];
                var sk = layers[l][2];
                var col = IsDark(ScK(sc, "bg")) ? LayC(sc, k * 0.9) : J.Mix(ScK(sc, "bg"), "#000000", k * 1.6);
                var n = (int)NumOr(Pm, "n", 6) + l * 2;
                for (var i = 0; i < n; i++)
                {
                    var x = W * (i + 0.5 + J.Rs(s, l, i, 1) * 0.35) / n;
                    var h = H * J.Rr(0.7, 1.05, s, l, i, 2) * hk;
                    var sw = Math.Sin(t * 0.4 + i + l) * 0.012;

                    void Seg(double x0, double y0, double an, double len, int d, int id)
                    {
                        var x1 = x0 + Math.Cos(an) * len;
                        var y1 = y0 + Math.Sin(an) * len;
                        using (var p = new SKPaint
                               {
                                   IsAntialias = true,
                                   IsStroke = true,
                                   StrokeCap = SKStrokeCap.Round, // JS ctx.lineCap = 'round'
                                   StrokeWidth = (float)Math.Max(1, u * 0.02 * sk * Math.Pow(0.55, d)),
                                   Color = Env.SkColor(col, fi),
                               })
                        using (var path = new SKPath())
                        {
                            path.MoveTo((float)x0, (float)y0);
                            path.LineTo((float)x1, (float)y1);
                            ctx.DrawPath(path, p);
                        }

                        if (d >= 4) return;
                        const int nb = 2;
                        for (var b = 0; b < nb; b++)
                            Seg(x1, y1, an + (b != 0 ? 1 : -1) * J.Rr(0.25, 0.7, s, id, b, 3) + sw * (d + 1),
                                len * J.Rr(0.55, 0.78, s, id, b, 4), d + 1, id * 2 + b + 1);
                    }

                    Seg(x, H + 2, -Math.PI / 2 + J.Rs(s, l, i, 5) * 0.08, h * 0.42, 0, (l * 50 + i) * 64 + 1);
                }
            }

            using (var f = SKShader.CreateLinearGradient(
                       new SKPoint(0, (float)(H * 0.45)), new SKPoint(0, (float)H),
                       new[]
                       {
                           Env.SkColor(LightOf(sc), 0),
                           Env.SkColor(IsDark(ScK(sc, "bg")) ? LayC(sc, 0.5) : ScK(sc, "bg"),
                               NumOr(Pm, "fog", 0.15) * fi),
                       },
                       new float[] { 0f, 1f }, SKShaderTileMode.Clamp))
            using (var p = new SKPaint { Shader = f })
                ctx.DrawRect(new SKRect(0, (float)(H * 0.45), (float)W, (float)H), p);
        }));

    // ================= cam（JS L508-544） =================

    /// <summary>KM(env)：JS 的 clamp((env.fx.motion ?? 0.7) * 1.25, 0, 1.25)。</summary>
    private static double KM(Env env) => J.Clamp(env.Fx.Motion * 1.25, 0, 1.25);

    private static double NumOr(JObject p, string k, double d) =>
        p[k] is JValue v && (v.Type == JTokenType.Float || v.Type == JTokenType.Integer) ? (double)v : d;

    private static void RegisterHrNervous() => RegC("hrNervous", new CamDef
    {
        Name = "怯えた手持ち",
        Tags = new[] { "horror", "glitch", "emotional" },
        W = 0.9,
        Plan = (rng, _) => new JObject { ["f"] = rng.Range(0.9, 1.3), ["jerk"] = rng.Range(0.6, 1) },
        Get = env =>
        {
            var K = KM(env);
            var Pm = env.Cut?.CamP ?? new JObject();
            var f = NumOr(Pm, "f", 1);
            var t = env.Lt;
            var sd = unchecked((int)(env.Cut?.Seed ?? 0));
            var x = (J.Noise1(t * f * 1.7, sd) * 0.6 + J.Noise1(t * f * 5.3, sd + 1) * 0.4) * env.W * 0.009 * K;
            var y = (J.Noise1(t * f * 1.4, sd + 2) * 0.6 + J.Noise1(t * f * 4.7, sd + 3) * 0.4) * env.H * 0.011 * K
                    + Math.Sin(t * 2.2) * env.H * 0.004 * K;
            var rot = J.Noise1(t * f * 1.1, sd + 4) * 1.2 * K;
            // 偶尔猛地一缩，很快稳住
            const double per = 1.6;
            var cyc = (int)Math.Floor(t / per);
            var since = t - cyc * per - J.R(sd, cyc, 5) * per * 0.6;
            if (since > 0 && J.R(sd, cyc, 6) < 0.65 * NumOr(Pm, "jerk", 0.8))
            {
                var d = Math.Exp(-since * 9) * K;
                x += J.Rs(sd, cyc, 7) * env.W * 0.025 * d;
                y += J.Rs(sd, cyc, 8) * env.H * 0.03 * d;
                rot += J.Rs(sd, cyc, 9) * 3 * d;
            }

            return new CamState { X = x, Y = y, Rot = rot, S = 1.03, Blur = 0 };
        },
    });

    private static void RegisterHrDutchSnap() => RegC("hrDutchSnap", new CamDef
    {
        Name = "不意の傾き",
        Tags = new[] { "horror", "emotional", "graphic" },
        W = 0.7,
        Strong = true,
        Plan = (rng, _) => new JObject
        {
            ["at"] = rng.Range(0.45, 0.65),
            ["a"] = rng.Range(3, 4.8) * rng.Pick(new[] { 1, -1 }),
        },
        Get = env =>
        {
            var K = KM(env);
            var Pm = env.Cut?.CamP ?? new JObject();
            var d = env.Cut?.Dur ?? 0;
            var u = J.Clamp(env.Lt / Math.Max(0.3, d));
            var q = J.Clamp((env.Lt - d * NumOr(Pm, "at", 0.55)) / 0.1);
            var e = J.E.OutBack(q, 2.5);
            return new CamState
            {
                S = 1 + 0.045 * K * J.E.InOutSine(u) + 0.02 * K * e,
                Rot = NumOr(Pm, "a", 4) * Math.Min(1, K) * e,
                Y = -env.H * 0.006 * K * e,
            };
        },
    });

    // ================= fx（JS L546-664；draw 整帧合成路径见头注 TODO 1） =================

    /// <summary>evS(ev)：事件确定种子。JS 的 J.h(Math.round(ev.t * 1000), 9127)。</summary>
    private static int EvS(PlanEvent ev) => unchecked((int)J.H((int)Math.Round(ev.T * 1000), 9127));

    private static void RegisterHrSubliminal() => RegF("hrSubliminal", new FxDef
    {
        Name = "サブリミナル",
        Tags = new[] { "horror", "glitch" },
        W = 0.6,
        Dur = 2,
        Amp = 1,
        Glitchy = true,
        Mid = true,
        // JS: scratch: true / ae: 'invert'（IRegEntry 无对应字段）
        // TODO(11p)：draw(ctx, ev, k, I) 整帧合成（上一帧快照放大 + difference/multiply 红染 +
        // 压暗）等 Renderer 定型非内置 fx 签名后按 JS L569-586 移植。
        Draw = null,
    });

    private static void RegisterHrSignalLoss() => RegF("hrSignalLoss", new FxDef
    {
        Name = "映像の途切れ",
        Tags = new[] { "horror", "glitch", "editorial" },
        W = 0.7,
        Dur = 9,
        Pre = 3,
        Amp = 1,
        Glitchy = true,
        // JS: scratch: true / ae: 'blackFrame'
        // TODO(11p)：draw：撕裂 → 黑屏 NO SIGNAL 字样 → 画面滚回（JS L600-636），等 fx 签名定型。
        Draw = null,
    });

    private static void RegisterHrPassingShadow() => RegF("hrPassingShadow", new FxDef
    {
        Name = "横切る影",
        Tags = new[] { "horror", "emotional" },
        W = 0.5,
        Dur = 5,
        Amp = 1,
        Mid = true,
        // JS: ae: 'flash'
        // TODO(11p)：draw：高大的暗影在几帧内横穿画面（JS L641-664），等 fx 签名定型。
        Draw = null,
    });

    // ================= trans（JS L666-737；TransDrawFn 已定型，全量移植） =================

    /// <summary>trReg 包装：p<=0 精确输出 A、p>=1 精确输出 B（同 P11TreatTrans.Wrap）。</summary>
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

    // ---------------- hrStaticCut（JS L678-701） ----------------

    private static void RegisterHrStaticCut() => RegX("hrStaticCut", new TransDef
    {
        Name = "砂嵐カット",
        Tags = new[] { "horror", "glitch" },
        W = 0.9,
        Dur = 0.4,
        Plan = (rng, _) => new JObject { ["roll"] = rng.Chance(0.6) },
        Draw = Wrap((ctx, a, b, p, i, Pm) =>
        {
            int cw = i.Cw, ch = i.Ch;
            var st = i.Step;
            var src = p < 0.5 ? a : b;
            var nz = p < 0.5 ? J.E.InQuad(p / 0.5) : 1 - J.E.OutQuad((p - 0.5) / 0.5);
            var oy = JLayouts.B(Pm, "roll")
                ? (int)Math.Round(Math.Sin(p * Math.PI) * ch * 0.08 * (p < 0.5 ? 1 : -1))
                : 0;
            using (var sp = AlphaPaint(1))
            {
                ctx.DrawImage(src, 0f, oy, SampLinear, sp);
                if (oy != 0) ctx.DrawImage(src, 0f, oy > 0 ? oy - ch : oy + ch, SampLinear, sp);
            }

            // createPattern(N, 'repeat') + DOMMatrix 平移缩放 → CreateBitmap + CreateScaleTranslation
            var sz = Math.Max(1, (int)Math.Round(ch / 400.0));
            var tx = -(int)Math.Floor(J.R(st, 1) * 128) * sz * 2;
            var ty = -(int)Math.Floor(J.R(st, 2) * 96) * sz;
            using (var shader = SKShader.CreateBitmap(NoiseCv(st), SKShaderTileMode.Repeat, SKShaderTileMode.Repeat,
                       SKMatrix.CreateScaleTranslation(sz * 2f, sz, tx, ty)))
            using (var p2 = new SKPaint
                   {
                       Shader = shader,
                       Color = SKColors.White.WithAlpha((byte)Math.Round(Math.Min(1, nz * 1.15) * 255)),
                   })
                ctx.DrawRect(new SKRect(0, 0, cw, ch), p2);
            using (var p3 = new SKPaint { Color = SKColors.Black.WithAlpha((byte)Math.Round(0.5 * nz * 255)) })
                ctx.DrawRect(new SKRect(0, (float)(J.R(st, 3) * ch), cw, (float)(J.R(st, 3) * ch + ch * 0.12)), p3);
        }),
    });

    // ---------------- hrBlink（JS L704-737） ----------------

    private static void RegisterHrBlink() => RegX("hrBlink", new TransDef
    {
        Name = "まばたき",
        Tags = new[] { "horror", "emotional" },
        W = 0.8,
        Dur = 0.45,
        Plan = (rng, _) => new JObject { ["half"] = rng.Chance(0.3) },
        Draw = Wrap((ctx, a, b, p, i, _) =>
        {
            int cw = i.Cw, ch = i.Ch;
            var src = p < 0.5 ? a : b;
            var c = p < 0.5 ? J.E.InCubic(p / 0.5) : 1 - J.E.OutCubic((p - 0.5) / 0.5);
            using (var sp = AlphaPaint(1))
                ctx.DrawImage(src, 0f, 0f, SampLinear, sp);
            if (c <= 0) return;
            var h = ch / 2.0 * c * 1.02;
            var bow = ch * 0.18 * (1 - c * 0.6);
            using (var black = new SKPaint { IsAntialias = true, Color = SKColors.Black })
            {
                using (var path = new SKPath())
                {
                    path.MoveTo(0, 0);
                    path.LineTo(cw, 0);
                    path.LineTo(cw, (float)(h - bow));
                    path.QuadTo(cw / 2f, (float)(h + bow), 0, (float)(h - bow));
                    path.Close();
                    ctx.DrawPath(path, black);
                }

                using (var path = new SKPath())
                {
                    path.MoveTo(0, ch);
                    path.LineTo(cw, ch);
                    path.LineTo(cw, (float)(ch - h + bow));
                    path.QuadTo(cw / 2f, (float)(ch - h - bow), 0, (float)(ch - h + bow));
                    path.Close();
                    ctx.DrawPath(path, black);
                }
            }

            // 眼睑周围的软边
            using (var shader = SKShader.CreateLinearGradient(
                       new SKPoint(0, 0), new SKPoint(0, ch),
                       new[] { new SKColor(0, 0, 0, 153), new SKColor(0, 0, 0, 0), new SKColor(0, 0, 0, 153) },
                       new float[] { 0f, 0.5f, 1f }, SKShaderTileMode.Clamp))
            using (var p2 = new SKPaint { Shader = shader, Color = SKColors.White.WithAlpha((byte)Math.Round(c * 255)) })
                ctx.DrawRect(new SKRect(0, 0, cw, ch), p2);
        }),
    });

    // ================= style ×3（JS L739-994） =================
    // JS 的 J.STYLES[k] 赋值是"不覆盖"语义（core 风格存在即跳过）；C# 对位写 Reg.Styles +
    // JStyles.StyleOrder 追加。desc/set/fonts/texture/ghost/glow 进 StyleDef.Extra。

    private static JObject W(params (string K, object V)[] kv)
    {
        var o = new JObject();
        foreach (var (k, v) in kv) o[k] = ToTok(v);
        return o;
    }

    private static Scheme Sch(params (string K, object V)[] kv)
    {
        var s = new Scheme { Extra = new Dictionary<string, JToken>() };
        foreach (var (k, v) in kv) s.Extra[k] = ToTok(v);
        return s;
    }

    // JS 字面量是整数处保持整数（JSON 往返对齐 JSON.stringify：1 而非 1.0）
    private static JToken ToTok(object v) => v switch
    {
        JToken t => t,
        string s => new JValue(s),
        bool b => new JValue(b),
        int i => new JValue(i),
        long l => new JValue(l),
        double d => Math.Floor(d) == d && !double.IsInfinity(d) ? new JValue((long)d) : new JValue(d),
        _ => JToken.FromObject(v),
    };

    private static void RegisterStyles()
    {
        // LAY / ENT / EXI：horror 布局/入场/退场的抽取偏置表（JS L739-781）
        var LAY = W(
            ("hrFlashlight", 1.6), ("hrDoorGap", 1.3), ("hrWallScrawl", 1.2), ("hrCctv", 1.2),
            ("hrOuija", 0.9), ("hrMissing", 1), ("hrWrongOne", 1.5), ("hrRisingDark", 1.3),
            ("hrRedacted", 1), ("hrStaticTv", 1), ("hrSpiritPhoto", 1), ("hrWrongShadow", 1.3),
            ("center", 1.1), ("vcols", 1.2), ("pop", 0.3));
        var ENT = W(
            ("hrBlinkCreep", 1.3), ("hrJumpScare", 0.9), ("hrUneasy", 1.6), ("hrVhold", 1.1),
            ("hrMirrorSnap", 1), ("hrManifest", 1.6), ("hrClawReveal", 1), ("blur", 1.2),
            ("flicker", 1.2), ("pop", 0.2), ("bounceBig", 0.1), ("bubbles", 0.1));
        var EXI = W(
            ("hrPulledDown", 1.4), ("hrLookBack", 1.3), ("hrTurnAway", 1.2), ("hrShiver", 1.2),
            ("hrSwallow", 1.3), ("hrFlickerDie", 1.3), ("hrDrain", 1.2), ("blur", 1.1),
            ("popOut", 0.1), ("balloonOff", 0.1));

        JObject Merged(JObject @base, params (string K, object V)[] over)
        {
            var o = (JObject)@base.DeepClone();
            foreach (var (k, v) in over) o[k] = ToTok(v);
            return o;
        }

        if (Reg.Styles.ContainsKey("hrRuin")) return; // 幂等 + 不覆盖（JS: if (J.STYLES[k]) continue）

        Reg.Styles["hrRuin"] = new StyleDef
        {
            Key = "hrRuin",
            Name = "廃墟",
            Tags = new[] { "horror" },
            Hud = false,
            GlitchBoost = 1,
            Bias = new JObject
            {
                ["layout"] = LAY,
                ["enter"] = ENT,
                ["exit"] = EXI,
                ["treat"] = W(
                    ("hrEroded", 1.6), ("hrInkBleed", 1.2), ("hrDoubleExp", 1.2), ("hrRedact", 0.6),
                    ("rainbow", 0.1), ("sticker", 0.1)),
                ["bg"] = W(
                    ("hrFailingLamp", 1.6), ("hrCorridor", 1.4), ("hrMold", 1.4), ("hrDeadTrees", 1.2),
                    ("polka", 0.1), ("candy", 0.1)),
                ["cam"] = W(("hrNervous", 1.4), ("hrDutchSnap", 1.1), ("handheld", 1.2), ("bounce", 0.1), ("jelly", 0.1)),
                ["fx"] = W(
                    ("hrPassingShadow", 1.2), ("hrSubliminal", 1), ("hrSignalLoss", 0.8),
                    ("dustScratches", 1.2), ("starGlint", 0.1)),
            },
            Decor = W(
                ("hrDustBeam", 1.6), ("hrCracks", 1.2), ("hrSigil", 0.8), ("hrScratches", 1),
                ("hrWatchEye", 0.6), ("hrDrips", 0.8), ("confetti", 0.05), ("heartsStars", 0.05)),
            Schemes = new List<Scheme>
            {
                Sch(
                    ("bg", "#161B18"), ("fg", "#D3DACF"), ("sub", "#7D887E"), ("accent", "#B0473A"),
                    ("accent2", "#6E8B6B"), ("ink", "#D3DACF"), ("dim", "#1F2621"),
                    ("ghostA", "#5E7A62"), ("ghostB", "#8A3A30")),
                Sch(
                    ("bg", "#8C958A"), ("fg", "#141814"), ("sub", "#2E362F"), ("accent", "#6A1A14"),
                    ("accent2", "#E4E8DF"), ("ink", "#141814"), ("dim", "#848D82"),
                    ("ghostA", "#3F4D41"), ("ghostB", "#6A1A14")),
                Sch(
                    ("bg", "#0C0F0D"), ("fg", "#A9B8A6"), ("sub", "#5D6B5E"), ("accent", "#C24A3A"),
                    ("accent2", "#A9B8A6"), ("ink", "#A9B8A6"), ("dim", "#161B17"),
                    ("ghostA", "#2F4A36"), ("ghostB", "#5E1E18")),
            },
            Extra = new Dictionary<string, JToken>
            {
                ["desc"] = "褪せた緑灰色・錆の赤・明朝の残響",
                ["set"] = "horror",
                ["fonts"] = new JObject
                {
                    ["display"] = new JArray("mincho_black", "shippori"),
                    ["serif"] = new JArray("mincho", "mincho_light"),
                    ["body"] = new JArray("mincho_light"),
                    ["mono"] = new JArray("mono"),
                },
                ["texture"] = new JObject { ["grain"] = 1, ["paper"] = 0.15, ["scan"] = 0.1 },
                ["ghost"] = 0.55,
                ["glow"] = 0.4,
            },
        };

        Reg.Styles["hrNightRec"] = new StyleDef
        {
            Key = "hrNightRec",
            Name = "深夜の録画",
            Tags = new[] { "horror" },
            Hud = true,
            GlitchBoost = 1.2,
            Bias = new JObject
            {
                ["layout"] = Merged(LAY, ("hrCctv", 2.2), ("hrStaticTv", 1.6), ("hrRedacted", 1.3), ("type", 1.2)),
                ["enter"] = Merged(ENT, ("hrVhold", 1.8), ("glitchIn", 1)),
                ["exit"] = Merged(EXI, ("hrFlickerDie", 1.8), ("glitch", 1)),
                ["treat"] = W(("hrDoubleExp", 1.4), ("hrRedact", 1.2), ("glitchSplit", 1), ("rainbow", 0.1)),
                ["bg"] = W(("hrFailingLamp", 1.4), ("hrCorridor", 1.6), ("vhsBand", 1.4), ("noiseField", 1), ("polka", 0.1)),
                ["cam"] = W(("hrNervous", 1.8), ("hrDutchSnap", 1), ("handheld", 1.2), ("jelly", 0.1)),
                ["fx"] = W(
                    ("hrSignalLoss", 1.6), ("hrSubliminal", 1.3), ("hrPassingShadow", 1),
                    ("tvStatic", 1.4), ("trackingNoise", 1.2), ("vhsRoll", 1.2), ("starGlint", 0.1)),
            },
            Decor = W(
                ("hrStaticPatch", 1.6), ("hrWatchEye", 1), ("hrCracks", 0.8), ("hrScratches", 0.8),
                ("timecodeBar", 1), ("hud", 1), ("confetti", 0.05)),
            Schemes = new List<Scheme>
            {
                Sch(
                    ("bg", "#050505"), ("fg", "#EDEDED"), ("sub", "#8A8A8A"), ("accent", "#E3261E"),
                    ("accent2", "#FFFFFF"), ("ink", "#EDEDED"), ("dim", "#121212"),
                    ("ghostA", "#6E6E6E"), ("ghostB", "#E3261E")),
                Sch(
                    ("bg", "#0B0E0B"), ("fg", "#D8F0D8"), ("sub", "#6F866F"), ("accent", "#FF3A2A"),
                    ("accent2", "#D8F0D8"), ("ink", "#D8F0D8"), ("dim", "#141A14"),
                    ("ghostA", "#3E6B3E"), ("ghostB", "#FF3A2A")),
                Sch(
                    ("bg", "#DADADA"), ("fg", "#0A0A0A"), ("sub", "#4A4A4A"), ("accent", "#C8140E"),
                    ("accent2", "#0A0A0A"), ("ink", "#0A0A0A"), ("dim", "#CCCCCC"),
                    ("ghostA", "#8A8A8A"), ("ghostB", "#C8140E")),
            },
            Extra = new Dictionary<string, JToken>
            {
                ["desc"] = "真っ黒な画面・監視映像の白と赤・砂嵐",
                ["set"] = "horror",
                ["fonts"] = new JObject
                {
                    ["display"] = new JArray("gothic_bold", "gothic_black"),
                    ["serif"] = new JArray("mincho"),
                    ["body"] = new JArray("gothic_med"),
                    ["mono"] = new JArray("mono", "dot"),
                },
                ["texture"] = new JObject { ["grain"] = 1.2, ["paper"] = 0, ["scan"] = 0.7 },
                ["ghost"] = 0.8,
                ["glow"] = 0.5,
            },
        };

        Reg.Styles["hrCurse"] = new StyleDef
        {
            Key = "hrCurse",
            Name = "呪いの手紙",
            Tags = new[] { "horror" },
            Hud = false,
            GlitchBoost = 1,
            Bias = new JObject
            {
                ["layout"] = Merged(LAY,
                    ("hrWallScrawl", 2), ("hrMissing", 1.6), ("hrSpiritPhoto", 1.5), ("hrOuija", 1.3),
                    ("hrCctv", 0.5), ("letterPaper", 1.2)),
                ["enter"] = Merged(ENT, ("hrClawReveal", 1.4), ("inkBleed", 1.4)),
                ["exit"] = Merged(EXI, ("hrDrain", 1.8), ("burn", 1)),
                ["treat"] = W(
                    ("hrInkBleed", 1.8), ("hrEroded", 1.3), ("hrRedact", 0.8), ("sticker", 0.1), ("chrome", 0.1)),
                ["bg"] = W(("hrMold", 1.8), ("hrFailingLamp", 1), ("tornPaper", 1), ("polka", 0.1)),
                ["cam"] = W(("hrNervous", 1), ("hrDutchSnap", 1.2), ("driftDiag", 1)),
                ["fx"] = W(
                    ("hrSubliminal", 1.2), ("hrPassingShadow", 1), ("filmBurn", 1),
                    ("dustScratches", 1.4), ("starGlint", 0.1)),
            },
            Decor = W(
                ("hrDrips", 1.6), ("hrScratches", 1.2), ("hrSigil", 1.2), ("hrCracks", 0.8),
                ("crossOut", 1), ("scribbleCircle", 0.8), ("confetti", 0.05)),
            Schemes = new List<Scheme>
            {
                Sch(
                    ("bg", "#D8CBA4"), ("fg", "#2A2017"), ("sub", "#6B5B45"), ("accent", "#7E1410"),
                    ("accent2", "#3F3326"), ("ink", "#2A2017"), ("dim", "#CDBF97"),
                    ("ghostA", "#9A2A20"), ("ghostB", "#8A7A5E"), ("paper", true)),
                Sch(
                    ("bg", "#1C140E"), ("fg", "#E3D5B0"), ("sub", "#9A8866"), ("accent", "#B8261C"),
                    ("accent2", "#E3D5B0"), ("ink", "#E3D5B0"), ("dim", "#271D15"),
                    ("ghostA", "#6E1510"), ("ghostB", "#5A4A34"), ("paper", true)),
                Sch(
                    ("bg", "#C4B28A"), ("fg", "#3A0D0A"), ("sub", "#6E4A3A"), ("accent", "#1E1812"),
                    ("accent2", "#7E1410"), ("ink", "#3A0D0A"), ("dim", "#B9A67D"),
                    ("ghostA", "#7E1410"), ("ghostB", "#6E5E44"), ("paper", true)),
            },
            Extra = new Dictionary<string, JToken>
            {
                ["desc"] = "黄ばんだ便箋・褪せた墨・暗い赤の書き込み",
                ["set"] = "horror",
                ["fonts"] = new JObject
                {
                    ["display"] = new JArray("klee", "brush", "mincho_black"),
                    ["serif"] = new JArray("klee", "mincho"),
                    ["body"] = new JArray("klee", "mincho"),
                    ["mono"] = new JArray("mono"),
                },
                ["texture"] = new JObject { ["grain"] = 0.7, ["paper"] = 1, ["scan"] = 0 },
                ["ghost"] = 0.4,
                ["glow"] = 0.3,
            },
        };

        if (!JStyles.StyleOrder.Contains("hrRuin")) JStyles.StyleOrder.Add("hrRuin");
        if (!JStyles.StyleOrder.Contains("hrNightRec")) JStyles.StyleOrder.Add("hrNightRec");
        if (!JStyles.StyleOrder.Contains("hrCurse")) JStyles.StyleOrder.Add("hrCurse");
    }

    // ================= 注册（JS 源 reg/bgReg/fx/trReg/STYLES 出现序） =================

    /// <summary>幂等注册本文件全部条目（顺序 = JS 源调用序；不挂静态构造函数）。</summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;
        RegisterHrScratches();    // decor, JS L80
        RegisterHrSigil();        // decor, JS L115
        RegisterHrWatchEye();     // decor, JS L152
        RegisterHrStaticPatch();  // decor, JS L206
        RegisterHrDustBeam();     // decor, JS L235
        RegisterHrDrips();        // decor, JS L278
        RegisterHrCracks();       // decor, JS L305
        RegisterHrFailingLamp();  // bg,    JS L337
        RegisterHrCorridor();     // bg,    JS L363
        RegisterHrMold();         // bg,    JS L431
        RegisterHrDeadTrees();    // bg,    JS L469
        RegisterHrNervous();      // cam,   JS L510
        RegisterHrDutchSnap();    // cam,   JS L531
        RegisterHrSubliminal();   // fx,    JS L559
        RegisterHrSignalLoss();   // fx,    JS L590
        RegisterHrPassingShadow();// fx,    JS L639
        RegisterHrStaticCut();    // trans, JS L678
        RegisterHrBlink();        // trans, JS L704
        RegisterStyles();         // style, JS L782-994
    }
}
