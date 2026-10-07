using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace JizuraLive.Engine.Packs;

/// <summary>
/// 追加音频表现包（移植自 frontend/src/engine/11p_audiofx.js）：山脊线（能量历史）、
/// 随音高游移（hold）、心电图（decor）、换台（trans）。全部 extra 新增标记。
/// 注：源里的 fx「ASCII化」依赖渲染器把非内置 FXE 的插件 Draw 路径接上（当前 TODO），
/// 本轮暂缓注册——路径接通后在此补一条 Reg.Register("fx","ascii",...)。
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
                        StrokeWidth = Math.Max(1, (float)(U(env) * ((front ? 0.0038 : 0.0028)))),
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

    /// <summary>注册全部条目（ASCII化 fx 待渲染器插件路径后补）。</summary>
    public static void Install()
    {
        Reg.Register("bg", "ridgeAudio", RidgeAudio, Pk);
        Reg.Register("hold", "pitchDrift", PitchDrift, Pk);
        Reg.Register("decor", "ecg", Ecg, Pk);
        Reg.Register("trans", "channelSwitch", ChannelSwitch, Pk);
    }
}