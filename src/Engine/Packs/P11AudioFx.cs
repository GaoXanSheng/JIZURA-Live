using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace JizuraLive.Engine.Packs;

/// <summary>
/// 追加音频表现包（移植自 frontend/src/engine/11p_audiofx.js）：山脊线（能量历史，bg）、
/// 随音高游移（hold）、心电图（decor）、换台（trans）、ASCII 化（fx，走渲染器 FxPlug 插件桥）。
/// 全部 extra 新增标记（IRegEntry.Extra = true；audioFx 不在 Omakase 的 BasePacks，双保险）。
/// </summary>
public static class P11AudioFx
{
    private const string Pk = "audioFx";
    private static double U(Env env) => Math.Min(env.W, env.H) / 1080.0;
    private static bool IsDark(string? c) => J.Lum(c ?? "#000") < 0.45;
    private static string LayC(Scheme sc, double k) => J.Mix(sc.Str("bg") ?? "#000", sc.Str("fg") ?? "#fff", k);
    private static int Bs(Rng rng) => rng.Int(1, 1_000_000_000);
    private static double MidiOf(double hz) => 69 + 12 * Math.Log2(hz / 440);

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    /// <summary>JS 的 o.k 数值读取（缺省回 d）。</summary>
    private static double Num(JObject? o, string k, double d)
    {
        var v = o?[k];
        return v != null && (v.Type == JTokenType.Float || v.Type == JTokenType.Integer) ? (double)v : d;
    }

    // ---------------- bg：音频山脊线 ----------------
    // 能量历史画成 Joy Division 式层叠山脊：前排=现在，后排=过去。无能量数据时退化为慢速噪声山脊。
    private static readonly BgDef RidgeAudio = new()
    {
        Key = "ridgeAudio",
        Name = "オーディオ稜線",
        Tags = new[] { "calm", "emotional", "editorial" },
        W = 1.1,
        Plan = (rng, _) => new JObject
        {
            ["seed"] = Bs(rng),
            ["n"] = rng.Int(9, 13),
            ["win"] = Math.Round(rng.Range(2.4, 3.4) * 1000) / 1000.0,
            ["amp"] = Math.Round(rng.Range(0.16, 0.22) * 1000) / 1000.0,
            ["mode"] = rng.Pick(new[] { "wide", "wide", "center" }),
        },
        Draw = env =>
        {
            var ctx = env.Ctx;
            var P = env.P;
            var t = env.T;
            var n = (int)Num(P, "n", 11);
            var sd = (int)Num(P, "seed", 1);
            var e = J.E.OutCubic(Clamp01(env.Lt / 0.9));
            if (e <= 0.003) return;
            var energy = env.Plan.Energy;
            var rate = env.Plan.EnergyRate;
            double? EAt(double ts)
            {
                if (energy == null || rate <= 0 || energy.Length == 0) return null;
                var f = (int)Math.Round(ts * rate);
                return f >= 0 && f < energy.Length ? energy[f] : null;
            }
            var hasAudio = EAt(0) != null || EAt(5) != null;
            double Val(double ts, int r)
            {
                var v = EAt(ts);
                if (v != null) return Clamp01(v.Value);
                return Math.Pow(0.5 + 0.5 * J.Noise1(ts * 0.7 + r * 0.8, sd + r * 13), 1.7) * 0.5;
            }

            var x0 = (float)(env.W * 0.05);
            var x1 = (float)(env.W * 0.95);
            var y0 = (float)(env.H * 0.14);
            var y1 = (float)(env.H * 0.9);
            var gap = (y1 - y0) / (n - 1);
            const int m = 96;
            var amp = env.H * Num(P, "amp", 0.19);
            var win = Num(P, "win", 2.8);
            var step = win / n; // 每行时间错后：前排=现在，后排=过去

            ctx.Save();
            try
            {
                // LineJoin 由 paint.StrokeJoin 设置
                for (var r = 0; r < n; r++)
                {
                    var bas = y0 + r * gap;
                    var tEnd = t - (n - 1 - r) * step;
                    var front = r == n - 1;
                    var grow = J.E.OutCubic(Clamp01(e * 1.5 - r / (double)n * 0.5));
                    if (grow <= 0.003) continue;
                    var lc = front ? env.Sc?.Str("accent") ?? "#fff" : LayC(env.Sc!, 0.5 + 0.25 * r / (double)n);
                    var beatPulse = env.BeatHas ? Math.Exp(-env.BeatSince * 5.5) : 0; // JS 的 beat ? exp(-beat.since*5.5) : 0
                    var pts = new List<SKPoint>();
                    for (var j = 0; j <= m; j++)
                    {
                        var uu = j / (double)m;
                        var x = x0 + (x1 - x0) * uu;
                        var envp = (P.Value<string>("mode") == "center")
                            ? 0.12 + 0.88 * Math.Exp(-Math.Pow((uu - 0.5) / 0.26, 2))
                            : 0.3 + 0.7 * Math.Pow(Math.Sin(Math.PI * uu), 1.4);
                        var ts = tEnd - (1 - uu) * win;
                        var v = hasAudio ? Math.Pow(Val(ts, r), 1.25) : Val(ts, r);
                        pts.Add(new SKPoint((float)x, (float)(bas - amp * envp * v * 2.1 * grow)));
                    }
                    using (var fill = new SKPaint { Color = Env.SkColor(env.Sc?.Str("bg") ?? "#000", 0.88f) })
                    {
                        using var path = new SKPath();
                        path.MoveTo((float)x0, (float)bas + 1);
                        foreach (var pt in pts) path.LineTo(pt);
                        path.LineTo(x1, bas + 1);
                        path.Close();
                        ctx.DrawPath(path, fill);
                    }
                    using (var stroke = new SKPaint
                    {
                        Color = SKColor.Parse(lc),
                        StrokeWidth = Math.Max(1, (float)(U(env) * (front ? 0.0038 + beatPulse * 0.003 : 0.0028))),
                        IsAntialias = true,
                        StrokeJoin = SKStrokeJoin.Round,
                    })
                    {
                        for (var q = 0; q < pts.Count; q++)
                        {
                            if (q == 0) ctx.DrawPoint(pts[q], stroke);
                            else ctx.DrawLine(pts[q - 1], pts[q], stroke);
                        }
                    }
                }
            }
            finally
            {
                ctx.Restore();
            }
        },
    };

    // ---------------- hold：随音高游移 ----------------
    // 整行随实时音高相对近期均值上下浮移。无音高数据时退化为慢速漂移。
    private static readonly MoveDef PitchDrift = new()
    {
        Key = "pitchDrift",
        Name = "音程で揺れる",
        Tags = new[] { "calm", "emotional" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It;
            if (it == null) return;
            var plan = env.Plan;
            var pitch = plan.Pitch;
            var rate = plan.PitchRate;
            var amt = env.Ap;
            var motion = env.Fx.Motion;
            var k = amt * Clamp01(motion / 0.7) * 1.0;
            if (k < 0.01) return;
            var sz = Num(it, "size", 0);
            var sy0 = Num(it, "sy", 1);
            double d = 0;
            if (pitch is { Length: > 0 } && rate > 0)
            {
                var tAbs = (env.Cut?.Start ?? 0) + env.Ltb;
                double? Med(double win)
                {
                    var vals = new List<double>();
                    var f0 = Math.Max(0, (int)Math.Floor((tAbs - win) * rate));
                    var f1 = Math.Min(pitch.Length, (int)Math.Ceiling((tAbs + win) * rate));
                    for (var f = f0; f < f1; f++)
                        if (pitch[f] > 0) vals.Add(MidiOf(pitch[f]));
                    if (vals.Count == 0) return null;
                    vals.Sort();
                    return vals[vals.Count >> 1];
                }
                var now = Med(0.12);
                var near = Med(1.6);
                if (now != null && near != null) d = Clamp01((now.Value - near.Value) / 9) is var dd && dd != 0 ? Math.Clamp((now.Value - near.Value) / 9, -1, 1) : 0;
                else if (now != null) d = Math.Clamp((now.Value - 64) / 14, -0.6, 0.6);
            }
            else
            {
                d = 0.45 * J.Noise1(env.Ltb * 0.6, (int)(env.Cut?.Seed ?? 0) | 1);
            }

            // addC 对位：往 env.CharFns 追加一个 charFn 并重combine（dy/rot 随音高）
            var driftK = k;
            var driftD = d;
            env.CharFns.Add((i, g, nn) => new CharXf
            {
                Dy = -driftD * sz * sy0 * 0.42 * driftK,
                Rot = driftD * 1.5 * driftK,
            });
            env.CharFn = JAnim.CombineChar(env.CharFns);
        },
    };

    // ---------------- decor：心电图 ----------------
    // 底部一条扫过的心电折线：平线走带、节拍处爆双相尖峰，右侧 BPM 读数。
    private static readonly DecorDef Ecg = new()
    {
        Key = "ecg",
        Name = "心電図",
        Tags = new[] { "graphic", "glitch", "pop" },
        W = 0.8,
        Layer = "front",
        Draw = (env, spec) =>
        {
            if (env.Pass != "main") return;
            var W = env.W;
            var H = env.H;
            var sc = env.Sc!;
            var u = U(env);
            var o = 1 - J.E.InCubic(Clamp01(env.POut));
            var inA = J.E.OutExpo(Clamp01(env.Lt / 0.5));
            var a = o * inA;
            if (a <= 0.005 || env.Lt < 0) return;
            var ctx = env.Ctx;
            var beats = env.Plan.Beats is { Length: > 0 } bb ? bb : null;
            var energy = env.Plan.Energy;
            var erate = env.Plan.EnergyRate;
            var x0 = W * 0.08;
            var x1 = W * 0.92;
            var y = H * (spec.Low ? 0.88 : 0.84);
            const double win = 3.4;
            const int m = 150;
            var amp = (float) H * 0.055;
            var seed = (int)(env.Cut?.Seed ?? 0) | 1;

            // ts 时刻的脉冲高度：节拍双相尖峰（无节拍→能量包络）
            double Spike(double ts)
            {
                if (beats != null)
                {
                    int lo = 0, hi = beats.Length - 1, bi = -1;
                    while (lo <= hi)
                    {
                        var mid = (lo + hi) >> 1;
                        if (beats[mid] <= ts)
                        {
                            bi = mid;
                            lo = mid + 1;
                        }
                        else hi = mid - 1;
                    }
                    var s = 0.0;
                    for (var b = Math.Max(0, bi - 1); b <= Math.Min(beats.Length - 1, bi + 1); b++)
                    {
                        var dt = ts - beats[b];
                        if (dt >= -0.035 && dt < 0) s = Math.Max(s, dt / 0.035);
                        else if (dt >= 0 && dt < 0.055) s = Math.Max(s, 1 - dt / 0.055 * 1.55);
                    }
                    return s;
                }
                if (energy != null && erate > 0)
                {
                    var f = (int)Math.Round(ts * erate);
                    return f >= 0 && f < energy.Length ? J.Clamp(energy[f] * 1.4 - 0.35) * 0.8 : 0;
                }
                return 0.25 + 0.25 * J.Noise1(ts * 3, seed);
            }

            ctx.Save();
            try
            {
                // 底网格：基线 + 时间刻度
                using (var grid = new SKPaint { Color = Env.SkColor(sc.Str("fg") ?? "#fff", (float)(0.14 * a)), StrokeWidth = Math.Max(1, (float)u) })
                {
                    ctx.DrawLine((float)x0, (float)y, (float)x1, (float)y, grid);
                    for (var i = 0; i <= 8; i++)
                    {
                        var gx = x0 + (x1 - x0) * i / 8.0;
                        ctx.DrawLine((float)gx, (float)(y - 4 * u), (float)gx, (float)(y + 4 * u), grid);
                    }
                }

                // 心电折线：左=过去，右=现在
                var pts = new List<SKPoint>();
                for (var j = 0; j <= m; j++)
                {
                    var uu = j / (double)m;
                    var ts = env.T - (1 - uu) * win;
                    if (ts < 0) continue;
                    var noise = 0.06 * J.Noise1(ts * 26, seed);
                    pts.Add(new SKPoint((float)(x0 + (x1 - x0) * uu), (float)(y - amp * (Spike(ts) + noise))));
                }
                if (pts.Count < 2) return;
                var glowCol = Env.SkColor(IsDark(sc.Str("bg")) ? sc.Str("accent") ?? "#fff" : LayC(sc, 0.35), (float)(0.35 * a));
                foreach (var (col, lw) in new[] { (glowCol, 5f * (float)u), (SKColor.Parse(sc.Str("accent") ?? "#fff"), (float)Math.Max(1.2, 1.8 * u)) })
                {
                    using var sp = new SKPaint { Color = col, StrokeWidth = lw, IsAntialias = true, StrokeJoin = SKStrokeJoin.Round };
                    for (var q = 0; q < pts.Count; q++)
                    {
                        if (q == 0) ctx.DrawPoint(pts[q], sp);
                        else ctx.DrawLine(pts[q - 1], pts[q], sp);
                    }
                }
                var last = pts[^1];
                using (var dot = new SKPaint { Color = Env.SkColor(sc.Str("accent") ?? "#fff", (float)a), IsAntialias = true })
                    ctx.DrawCircle(last, 3 * (float)u, dot);

                // BPM 读数（C# Env 无 beat 对象：节拍数据存在时按首间间隔估）
                if (beats is { Length: > 2 })
                {
                    var len = beats[1] - beats[0];
                    if (len > 0.2)
                    {
                        using var font = new SKFont(SKTypeface.FromFamilyName("Consolas"), (int)Math.Round(15 * u));
                        using var fp = new SKPaint { Color = Env.SkColor(sc.Str("sub") ?? "#888", (float)(a * 0.9)), IsAntialias = true };
                        ctx.DrawText(Math.Round(60 / len) + " BPM", (float)x1, (float)(y - amp - 10 * u), SKTextAlign.Right, font, fp);
                    }
                }
            }
            finally
            {
                ctx.Restore();
            }
        },
    };

    // ---------------- trans：换台 ----------------
    // 电视换台：垂直同步滚动 + 撕裂条 + 中段雪花爆窗。
    private static readonly TransDef ChannelSwitch = new()
    {
        Key = "channelSwitch",
        Name = "チャンネル切替",
        Tags = new[] { "glitch" },
        W = 0.9,
        Dur = 0.34,
        Plan = (rng, _) => new JObject { ["seed"] = Bs(rng) },
        Draw = new TransDrawFn((ctx, a, b, p, info) =>
        {
            var cw = info.Cw;
            var ch = info.Ch;
            var sd = (int)info.Seed ^ (int)Num(info.P as Newtonsoft.Json.Linq.JObject, "seed", 1);
            var step = (int)info.Step;
            SKImage src = p < 0.5 ? a : b;
            var kp = p < 0.5 ? p * 2 : (p - 0.5) * 2; // 前半滚走 A，后半滚入 B
            // 垂直同步滚动（模 wrap 两段贴回）
            var roll = (int)Math.Floor(kp * 1.3 % 1 * ch);
            ctx.DrawImage(src, 0, roll - ch);
            ctx.DrawImage(src, 0, roll);
            // 撕裂横条：随机几条横向错位
            for (var i = 0; i < 4; i++)
            {
                var yy = ch * J.R(sd, step, i, 1);
                var hh = ch * J.Rr(0.02, 0.08, sd, i, 2);
                var dx = J.Rs(sd, step, i, 3) * cw * 0.08 * (0.4 + kp);
                if (Math.Abs(dx) > 1)
                    ctx.DrawImage(src, new SKRect(0, (float)yy, cw, (float)(yy + hh)),
                        new SKRect((float)dx, (float)yy, (float)(dx + cw), (float)(yy + hh)));
            }
            // 中段雪花爆窗
            var burst = p > 0.3 && p < 0.7 ? 1 - Math.Abs(p - 0.5) / 0.2 : 0;
            if (burst > 0.03)
            {
                var w = cw * 0.62 * burst;
                var h = ch * 0.5 * burst;
                var bx = (cw - w) / 2;
                var by = (ch - h) / 2;
                ctx.Save();
                ctx.ClipRect(SKRect.Create((float)bx, (float)by, (float)w, (float)h));
                using (var dark = new SKPaint { Color = new SKColor(8, 8, 8) })
                    ctx.DrawRect((float)bx, (float)by, (float)w, (float)h, dark);
                const int nS = 420;
                for (var i = 0; i < nS; i++)
                {
                    var v = (int)(40 + J.R(sd, step, i, 4) * 215);
                    using var sp = new SKPaint { Color = new SKColor((byte)v, (byte)v, (byte)v) };
                    var sz = 3 + J.R(sd, step, i, 5) * 6;
                    ctx.DrawRect((float)(bx + J.R(sd, step, i, 6) * (w - sz)), (float)(by + J.R(sd, step, i, 7) * (h - sz)), (float)sz, (float)sz, sp);
                }
                ctx.Restore();
            }
            // 滚动亮带（垂直同步前沿）+ 轻噪横纹
            var bandY = roll;
            using (var band = new SKPaint { Color = new SKColor(255, 255, 255, (byte)((0.14 + burst * 0.2) * 255)) })
                ctx.DrawRect(0, bandY - ch * 0.012f, cw, ch * 0.024f, band);
            using (var noise = new SKPaint { Color = new SKColor(0, 0, 0, (byte)(0.06 * kp * 255)) })
                for (var yy = step * 7 % 6; yy < ch; yy += 6)
                    ctx.DrawRect(0, yy, cw, 1, noise);
        }),
    };

    // ---------------- fx：ASCII 化 ----------------
    // 全屏后期：把已渲好的整帧转成 ASCII 字符网格，事件两端自淡入淡出（JS L179-237）。
    // 复用渲染器 scratch 预拷帧（FxPlug.S），tmp 面降采样读像素、mono 字体逐格画字符。
    private const string AsciiRamp = " .'`^\",:;Il!i><~+_-?][}{1)(|\\/tfjrxnuvczXYUJCLQ0OZmwqpdbkhao*#MW&8%B@$";
    private const double AsciiBoost = 1.4;
    private const double AsciiLumSkip = 8;

    private static readonly FxDef AsciiFx = new()
    {
        Key = "ascii",
        Name = "ASCII化",
        Tags = new[] { "glitch", "graphic" },
        W = 0.7,
        Extra = true, // JS extra:true（追加分开关门禁）
        Dur = 3.2,
        Amp = 1,
        // scratch: true / ae 无 FxDef 字段：渲染器插件段恒拷 scratch（S），ae 无消费者
        Draw = (env, ev) =>
        {
            env.Ctx.Save();
            try
            {
                DrawAscii(env, ev, J.Clamp(FxPlug.K));
            }
            finally
            {
                env.Ctx.Restore();
            }
        },
    };

    private static void DrawAscii(Env env, PlanEvent ev, double k)
    {
        var S = FxPlug.S;
        var cw = FxPlug.Cw;
        var ch = FxPlug.Ch;
        if (S == null) return;
        var ctx = env.Ctx;
        var e = Math.Min(Math.Min(1, k / 0.12), (1 - k) / 0.12);
        if (e <= 0.01) return;
        var bg = env.Sc?.Str("bg") ?? "#000000";
        var mono = JLayouts.Font0(env.St, "mono") ?? "mono";
        var fontPx = Math.Max(8, (int)Math.Round(ch * 0.021));
        var cellH = (int)Math.Round(fontPx * 1.08);
        var font = JText.FaceFont(mono, fontPx); // 缓存实例，禁 Dispose
        using var meas = new SKPaint();
        var cellW = Math.Max(4f, font.MeasureText("M", meas));
        var cols = Math.Max(8, (int)Math.Floor(cw / cellW));
        var rows = Math.Max(6, (int)Math.Floor(ch / (double)cellH));
        // 整帧降采样到 cols×rows（JS 的 I.tmp 面 + imageSmoothingEnabled high）
        SKSurface? small = null;
        try
        {
            small = FxPlug.Tmp?.Invoke(cols, rows);
        }
        catch
        {
            small = null;
        }
        if (small == null) return;
        var scv = small.Canvas;
        scv.RestoreToCount(0);
        scv.Clear(SKColors.Transparent);
        scv.SetMatrix(SKMatrix.Identity);
        using (var simg = S.Snapshot())
        using (var dp = new SKPaint { IsAntialias = false })
            scv.DrawImage(simg, new SKRect(0, 0, cw, ch), new SKRect(0, 0, cols, rows),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), dp);
        var px = small.PeekPixels();
        if (px == null) return;
        // 整幅清底（JS alphaGuard 的 clearRect→fillRect 对位：不透明模式填 sc.bg；透過模式真清，
        // 插件清底不镜像进 M——渲染器既有保守降级，End 后新增区域被裁掉）
        ctx.SetMatrix(SKMatrix.Identity);
        if (FxPlug.Transparent)
        {
            ctx.Clear();
        }
        else
        {
            var cp = Env.SharedPaint();
            cp.IsAntialias = false;
            cp.IsStroke = false;
            cp.Shader = null;
            cp.ImageFilter = null;
            cp.Color = Env.SkColor(bg, 1);
            cp.BlendMode = SKBlendMode.SrcOver;
            ctx.DrawRect(new SKRect(0, 0, cw, ch), cp);
        }
        // 逐格：亮度→字符档位，颜色→像素色提亮（按面色彩类型解字节序，JS getImageData 的 RGBA 对位）
        var span = px.GetPixelSpan();
        var bpp = px.Info.BytesPerPixel;
        var rgbaOrder = px.Info.ColorType == SKColorType.Rgba8888;
        font.GetFontMetrics(out var fm);
        var yMid = (float)((-fm.Ascent - fm.Descent) / 2); // canvas textBaseline='middle'
        var ramp = AsciiRamp;
        var n = ramp.Length;
        var tp = Env.SharedPaint();
        tp.IsAntialias = true;
        tp.IsStroke = false;
        tp.Shader = null;
        tp.ImageFilter = null;
        tp.BlendMode = SKBlendMode.SrcOver;
        for (var r = 0; r < rows; r++)
        {
            var y = (r + 0.5f) * cellH;
            for (var c = 0; c < cols; c++)
            {
                var i = (r * cols + c) * bpp;
                byte red = span[i + 2], green = span[i + 1], blue = span[i];
                if (rgbaOrder) { red = span[i]; green = span[i + 1]; blue = span[i + 2]; }
                var lum = 0.2126 * red + 0.7152 * green + 0.0722 * blue;
                var idx = (int)Math.Round(lum / 255 * (n - 1));
                if (idx <= 0 || lum < AsciiLumSkip) continue;
                tp.Color = new SKColor(
                    (byte)Math.Min(255, (int)Math.Round(red * AsciiBoost)),
                    (byte)Math.Min(255, (int)Math.Round(green * AsciiBoost)),
                    (byte)Math.Min(255, (int)Math.Round(blue * AsciiBoost)));
                ctx.DrawText(ramp[idx].ToString(), (c + 0.5f) * cellW, y + yMid, SKTextAlign.Center, font, tp);
            }
        }
        if (e < 0.995) // 边缘与原画面交叉淡化（JS globalAlpha = 1-e; drawImage(S, 0, 0)）
        {
            using var simg2 = S.Snapshot();
            using var bp = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round((1 - e) * 255)) };
            ctx.DrawImage(simg2, 0, 0, bp);
        }
    }

    /// <summary>注册全部条目（JS 源出现序：bg/hold/decor/trans/fx）。</summary>
    public static void Install()
    {
        Reg.Register("bg", "ridgeAudio", RidgeAudio, Pk);
        Reg.Register("hold", "pitchDrift", PitchDrift, Pk);
        Reg.Register("decor", "ecg", Ecg, Pk);
        Reg.Register("trans", "channelSwitch", ChannelSwitch, Pk);
        Reg.Register("fx", "ascii", AsciiFx, Pk); // 渲染器 FxPlug 插件路径已接通（P11FxB 先例）
    }
}