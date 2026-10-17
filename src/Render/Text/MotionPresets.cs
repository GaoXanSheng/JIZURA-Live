namespace JizuraLive.Render.Text;

public sealed record MotionPresetDef(
    MotionPhase Phase,
    string Key,
    string Name,
    MotionParam[] Params,
    GlyphPresetFn Draw);

public static class MotionPresets
{
    public const string LongTailNote =
        "长尾手法（P11 全量约 150 条）按需补注册，见 notDoing";

    public static MotionPresetDef? Get(MotionPhase phase, string? key) =>
        TryGet(phase, key, out var def) ? def : null;

    public static bool TryGet(MotionPhase phase, string? key, out MotionPresetDef def)
    {
        def = null!;
        if (string.IsNullOrWhiteSpace(key)) return false;
        var k = key.Trim();
        for (var i = 0; i < _all.Length; i++)
        {
            if (_all[i].Phase != phase || !string.Equals(_all[i].Key, k, StringComparison.OrdinalIgnoreCase))
                continue;
            def = _all[i];
            return true;
        }
        return false;
    }

    public static IReadOnlyList<MotionPresetDef> All => _all;

    private static readonly MotionPresetDef[] _all =
    [
        ..EnterPresets(),
        ..HoldPresets(),
        ..ExitPresets(),
    ];

    private static IEnumerable<MotionPresetDef> EnterPresets()
    {
        yield return new(MotionPhase.Enter, "cut", "カット", [], (_, _, _, _) => null);

        yield return new(MotionPhase.Enter, "fade", "フェード", [],
            (i, n, p, env) =>
            {
                var q = Gx.Stg(p, Gx.OrdLr(i, n), 0.25);
                if (q <= 0) return new GlyphCharXf { Hide = true };
                if (q >= 1) return null;
                var e = Gx.OutCubic(q);
                return new GlyphCharXf { A = e, Dy = (1 - e) * env.H * 0.02 };
            });

        yield return new(MotionPhase.Enter, "slide", "スライド", [new("dist", 0.26, 0.05, 0.6)],
            (i, n, p, env) =>
            {
                var q = Gx.Stg(p, Gx.OrdLr(i, n), 0.5);
                if (q <= 0) return new GlyphCharXf { Hide = true };
                if (q >= 1) return null;
                var e = Gx.OutQuint(q);
                var dist = Gx.P(env, "dist", 0.26) * env.W;
                return new GlyphCharXf { Dx = -(1 - e) * dist, A = Gx.Clamp(q * 1.6) };
            });

        yield return new(MotionPhase.Enter, "pop", "ポップ", [new("stagger", 0.45, 0, 0.8), new("back", 2.6, 0.5, 5)],
            (i, n, p, env) =>
            {
                var stagger = Gx.P(env, "stagger", 0.45);
                var d = n > 1 ? i / (double)(n - 1) * stagger : 0;
                var q = Gx.Clamp((p - d) / 0.55);
                if (q <= 0) return new GlyphCharXf { Hide = true };
                var s = Gx.OutBack(q, Gx.P(env, "back", 2.6));
                return new GlyphCharXf { S = s, Rot = (1 - Gx.OutCubic(q)) * Gx.Rs(env.Seed, i, 9) * 28 * Gx.Deg, A = Gx.Clamp(q * 3) };
            });

        yield return new(MotionPhase.Enter, "drop", "落下", [new("stagger", 0.5, 0, 0.8)],
            (i, n, p, env) =>
            {
                var d = n > 1 ? Gx.R(env.Seed, i, 4) * Gx.P(env, "stagger", 0.5) : 0;
                var q = Gx.Clamp((p - d) / 0.5);
                if (q <= 0) return new GlyphCharXf { Hide = true };
                var b = Gx.Bounce(q);
                return new GlyphCharXf
                {
                    Dy = -(1 - b) * env.Size * 2.4,
                    Sy = 1 + (1 - q) * 0.5,
                    Sx = 1 - (1 - q) * 0.2,
                    A = Gx.Clamp(q * 3),
                };
            });

        yield return new(MotionPhase.Enter, "zoom", "ズーム", [],
            (_, _, p, env) =>
            {
                if (p <= 0) return new GlyphCharXf { Hide = true };
                var e = Gx.OutExpo(p);
                return new GlyphCharXf
                {
                    S = Gx.Lerp(1.7, 1, e),
                    A = Gx.Clamp(p * 4),
                };
            });

        yield return new(MotionPhase.Enter, "spin", "回転", [new("turns", 200, 45, 540)],
            (i, n, p, env) =>
            {
                var d = n > 1 ? i / (double)(n - 1) * 0.4 : 0;
                var q = Gx.Clamp((p - d) / 0.6);
                if (q <= 0) return new GlyphCharXf { Hide = true };
                var e = Gx.OutExpo(q);
                return new GlyphCharXf
                {
                    Rot = (1 - e) * (Gx.R(env.Seed, i) > 0.5 ? 1 : -1) * Gx.P(env, "turns", 200) * Gx.Deg,
                    S = Gx.Lerp(0.15, 1, e),
                    A = Gx.Clamp(q * 3),
                };
            });

        yield return new(MotionPhase.Enter, "type", "タイプ", [],
            (i, n, p, _) =>
            {
                var k = n > 0 ? i / (double)n : 0;
                if (p <= 0 || p < k) return new GlyphCharXf { Hide = true };
                return new GlyphCharXf { A = Gx.Clamp((p - k) * n * 4 + 0.25) };
            });

        yield return new(MotionPhase.Enter, "assemble", "分解→集合", [new("spread", 3.2, 0.5, 8)],
            (i, n, p, env) =>
            {
                var d = Gx.R(env.Seed, i, 1) * 0.45;
                var x = Gx.Clamp((p - d) / 0.62);
                if (x <= 0) return new GlyphCharXf { Hide = true };
                var e = Gx.OutExpo(x);
                var k = 1 - e;
                if (k <= 0.0005) return null;
                var ang = Gx.R(env.Seed, i, 2) * Gx.Tau;
                var dist = env.Size * Gx.P(env, "spread", 3.2) * (0.35 + 0.65 * Gx.R(env.Seed, i, 3));
                return new GlyphCharXf
                {
                    Dx = Math.Cos(ang) * dist * k,
                    Dy = Math.Sin(ang) * dist * k,
                    Rot = Gx.Rs(env.Seed, i, 4) * 190 * k * Gx.Deg,
                    S = 1 + (Gx.Lerp(0.4, 2.1, Gx.R(env.Seed, i, 5)) - 1) * k,
                    A = Gx.Clamp(p * 6),
                };
            });

        yield return new(MotionPhase.Enter, "wipe", "ワイプ", [],
            (i, n, p, env) =>
            {
                var e = Gx.InOutExpo(p);
                var k = i / (double)Math.Max(1, n);
                if (k > e) return new GlyphCharXf { Hide = true };
                var edge = Gx.Clamp((e - k) * n);
                return new GlyphCharXf { A = edge };
            });

        yield return new(MotionPhase.Enter, "blur", "ブラー", [new("amount", 26, 4, 80)],
            (_, _, p, env) =>
            {
                if (p <= 0) return new GlyphCharXf { Hide = true };
                var e = Gx.OutCubic(p);
                return new GlyphCharXf
                {
                    A = Math.Pow(e, 0.7),
                    S = 1 + 0.18 * (1 - e),
                    Skew = (1 - e) * Gx.P(env, "amount", 26) * 0.01,
                };
            });

        yield return new(MotionPhase.Enter, "stretch", "伸縮", [new("from", 4.2, 1.5, 8)],
            (i, n, p, env) =>
            {
                var q = Gx.Stg(p, Gx.OrdLr(i, n), 0.3);
                if (q <= 0) return new GlyphCharXf { Hide = true };
                if (q >= 1) return null;
                var e = Gx.OutExpo(q);
                return new GlyphCharXf
                {
                    Sx = Gx.Lerp(Gx.P(env, "from", 4.2), 1, e),
                    Sy = Gx.Lerp(0.5, 1, e),
                    A = Gx.Clamp(q * 4),
                };
            });

        yield return new(MotionPhase.Enter, "flicker", "点滅", [],
            (i, _, p, env) =>
            {
                if (p >= 1) return null;
                return Gx.R(env.Seed, env.Step, i) < p * 1.25 ? null : new GlyphCharXf { Hide = true };
            });

        yield return new(MotionPhase.Enter, "scramble", "スクランブル", [],
            (i, n, p, env) =>
            {
                var settle = 0.25 + 0.75 * (n > 1 ? i / (double)(n - 1) : 1);
                if (p >= settle) return null;
                if (p < settle * 0.25 && Gx.R(env.Seed, i, env.Step, 2) < 0.5) return new GlyphCharXf { Hide = true };
                return new GlyphCharXf { A = 0.85, Sx = 1 + Gx.Rs(env.Seed, i, env.Step) * 0.08 };
            });

        yield return new(MotionPhase.Enter, "riseMask", "下から出現", [new("stagger", 0.45, 0, 0.8)],
            (i, n, p, env) =>
            {
                var q = Gx.Stg(p, Gx.OrdLr(i, n), Gx.P(env, "stagger", 0.45));
                if (q <= 0) return new GlyphCharXf { Hide = true };
                if (q >= 1) return null;
                var e = Gx.OutQuint(q);
                return new GlyphCharXf { Dy = 1.15 * (1 - e) * env.Size, A = Gx.Clamp(q * 5) };
            });

        yield return new(MotionPhase.Enter, "flipX", "縦軸フリップ", [new("stagger", 0.45, 0, 0.8)],
            (i, n, p, env) =>
            {
                var q = Gx.Stg(p, Gx.OrdLr(i, n), Gx.P(env, "stagger", 0.45));
                if (q <= 0) return new GlyphCharXf { Hide = true };
                if (q >= 1) return null;
                var th = (1 - Gx.OutBack(q, 1.25)) * 180 * Gx.Deg;
                return new GlyphCharXf
                {
                    Sx = Math.Max(0.04, Math.Cos(th)),
                    A = Gx.Clamp(q * 8),
                };
            });

        yield return new(MotionPhase.Enter, "domino", "ドミノ", [new("stagger", 0.55, 0, 0.85)],
            (i, n, p, env) =>
            {
                var q = Gx.Stg(p, Gx.OrdLr(i, n), Gx.P(env, "stagger", 0.55));
                if (q <= 0) return new GlyphCharXf { Hide = true };
                if (q >= 1) return null;
                var th = (1 - Gx.OutBack(q, 1.5)) * 90 * Gx.Deg;
                return new GlyphCharXf { Rot = th, A = Gx.Clamp(q * 4) };
            });

        yield return new(MotionPhase.Enter, "bounceBig", "大ジャンプ", [new("stagger", 0.35, 0, 0.7)],
            (i, n, p, env) =>
            {
                var q = Gx.Stg(p, Gx.OrdLr(i, n), Gx.P(env, "stagger", 0.35));
                if (q <= 0) return new GlyphCharXf { Hide = true };
                if (q >= 1) return null;
                double dy, rot = 0;
                if (q < 0.5)
                {
                    var s = q / 0.5;
                    dy = 7.666 * s * s - 9.266 * s + 1.6;
                    rot = Gx.Rs(env.Seed, i, 21) * 28 * Math.Sin(Math.PI * s) * Gx.Deg;
                }
                else if (q < 0.76)
                {
                    var s = (q - 0.5) / 0.26;
                    dy = -0.24 * 4 * s * (1 - s);
                }
                else if (q < 0.9)
                {
                    var s = (q - 0.76) / 0.14;
                    dy = -0.06 * 4 * s * (1 - s);
                }
                else dy = 0;
                var imp = Math.Exp(-Math.Pow((q - 0.5) / 0.035, 2)) + 0.6 * Math.Exp(-Math.Pow((q - 0.76) / 0.03, 2));
                return new GlyphCharXf
                {
                    Dy = dy * env.Size,
                    Sy = 1 - 0.3 * imp,
                    Sx = 1 + 0.24 * imp,
                    Rot = rot,
                    A = Gx.Clamp(q * 7),
                };
            });

        yield return new(MotionPhase.Enter, "squashDrop", "潰れて着地", [new("stagger", 0.4, 0, 0.8)],
            (i, n, p, env) =>
            {
                var q = Gx.Stg(p, Gx.OrdLr(i, n), Gx.P(env, "stagger", 0.4));
                if (q <= 0) return new GlyphCharXf { Hide = true };
                if (q >= 1) return null;
                const double tA = 0.22;
                double dy = 0, sy;
                if (q < tA)
                {
                    var t = q / tA;
                    dy = -(1 - t * t) * env.Size * 1.7;
                    sy = 1 + 0.4 * t;
                }
                else
                {
                    var t = (q - tA) / (1 - tA);
                    sy = 1 - 0.58 * Math.Exp(-3 * t) * Math.Cos(t * 6.2) * (1 - t);
                }
                return new GlyphCharXf
                {
                    Dy = dy,
                    Sy = sy,
                    Sx = 1 / Math.Pow(sy, 0.8),
                    A = Gx.Clamp(q * 10),
                };
            });

        yield return new(MotionPhase.Enter, "rubber", "ゴム伸び", [new("stagger", 0.4, 0, 0.8)],
            (i, n, p, _) =>
            {
                var q = Gx.Stg(p, Gx.OrdLr(i, n), 0.4);
                if (q <= 0) return new GlyphCharXf { Hide = true };
                if (q >= 1) return null;
                var f = Math.Pow(1 - q, 2) * Math.Cos(q * 3.5 * Math.PI);
                var sx = Math.Max(0.03, 1 - f);
                return new GlyphCharXf
                {
                    Sx = sx,
                    Sy = Gx.Clamp(1 + (1 - sx) * 0.45, 0.72, 1.45),
                    A = Gx.Clamp(q * 6),
                };
            });

        yield return new(MotionPhase.Enter, "trackIn", "字間収束", [new("spread", 1.6, 0.3, 4)],
            (i, n, p, env) =>
            {
                var e = Gx.OutQuint(p);
                var k = Gx.OrdLr(i, n);
                var dx = k * env.Size * Gx.P(env, "spread", 1.6) * (1 - e);
                return new GlyphCharXf { Dx = dx, A = Gx.OutCubic(Gx.Clamp(p * 1.7)) };
            });

        yield return new(MotionPhase.Enter, "spiralIn", "螺旋集合", [new("stagger", 0.15, 0, 0.6)],
            (i, n, p, env) =>
            {
                var q = Gx.Stg(p, Gx.OrdLr(i, n), Gx.P(env, "stagger", 0.15));
                if (q <= 0) return new GlyphCharXf { Hide = true };
                if (q >= 1) return null;
                var e = Gx.OutCubic(q);
                var k = 1 - e;
                var dir = Gx.Dir(env.Seed);
                var ang = dir * k * 200 * Gx.Deg;
                var th = Gx.R(env.Seed, i, 31) * Gx.Tau + ang;
                var orb = env.Size * k;
                return new GlyphCharXf
                {
                    Dx = Math.Cos(th) * orb,
                    Dy = Math.Sin(th) * orb,
                    Rot = dir * k * 320 * Gx.Deg,
                    S = Gx.Lerp(0.45, 1, e),
                    A = Gx.Clamp(q * 5),
                };
            });

        yield return new(MotionPhase.Enter, "glitchIn", "グリッチ出現", [],
            (i, _, p, env) =>
            {
                var settle = 0.38 + 0.52 * Gx.R(env.Seed, i, 71);
                if (p >= settle) return null;
                var u = p / settle;
                if (Gx.R(env.Seed, i, env.Step, 72) > 0.25 * Gx.Clamp(p * 12) + 0.75 * u)
                    return new GlyphCharXf { Hide = true };
                var k = 1 - u;
                return new GlyphCharXf
                {
                    Dx = Gx.Rs(env.Seed, i, env.Step, 73) * env.Size * 0.5 * k,
                    Dy = Gx.Rs(env.Seed, i, env.Step, 74) * env.Size * 0.14 * k,
                    Sx = Gx.R(env.Seed, i, env.Step, 77) < 0.35 ? Gx.Lerp(0.55, 1.9, Gx.R(env.Seed, i, env.Step, 78)) : null,
                    A = 0.9,
                };
            });

        yield return new(MotionPhase.Enter, "windBlown", "風に乗って", [new("stagger", 0.5, 0, 0.8)],
            (i, n, p, env) =>
            {
                var dir = Gx.Dir(env.Seed);
                var kOrd = dir > 0 ? 1 - Gx.OrdLr(i, n) : Gx.OrdLr(i, n);
                var q = Gx.Stg(p, kOrd, Gx.P(env, "stagger", 0.5));
                if (q <= 0) return new GlyphCharXf { Hide = true };
                if (q >= 1) return null;
                var k = 1 - Gx.OutCubic(q);
                var ph = Gx.R(env.Seed, i, 193) * Gx.Tau;
                var dv = env.W * 0.3 + env.Size * 2;
                return new GlyphCharXf
                {
                    Dx = -dir * dv * k,
                    Dy = Math.Sin(ph + k * 6) * 0.5 * env.Size * k,
                    Rot = Math.Sin(ph * 2 + k * 7) * 120 * k * Gx.Deg,
                    A = Gx.Clamp(q * 4),
                };
            });
    }

    private static IEnumerable<MotionPresetDef> HoldPresets()
    {
        yield return new(MotionPhase.Hold, "still", "静止", [], (_, _, _, _) => null);

        yield return new(MotionPhase.Hold, "jitter", "ジッター", [new("amp", 1, 0, 3)],
            (i, _, _, env) =>
            {
                var a = env.Size * 0.025 * Gx.P(env, "amp", 1);
                if (a < 0.2) return null;
                return new GlyphCharXf
                {
                    Dx = Gx.Rs(env.Seed, env.Step, i, 1) * a,
                    Dy = Gx.Rs(env.Seed, env.Step, i, 2) * a,
                    Rot = Gx.Rs(env.Seed, env.Step, i, 3) * 4 * Gx.Deg,
                };
            });

        yield return new(MotionPhase.Hold, "drift", "ドリフト", [new("amp", 1, 0, 3)],
            (i, _, _, env) =>
            {
                var k = Gx.P(env, "amp", 1);
                var u = env.Lt / Math.Max(0.3, env.Dur);
                return new GlyphCharXf
                {
                    Dx = Gx.Dir(env.Seed) * (u - 0.5) * env.W * 0.035 * k + Gx.NoiseLike(env.Seed + 7, env.T * 0.6 + i * 0.31) * env.Size * 0.02 * k,
                    Dy = Gx.NoiseLike(env.Seed, env.T * 0.9 + i * 0.43) * env.Size * 0.03 * k,
                };
            });

        yield return new(MotionPhase.Hold, "breathe", "呼吸", [new("amp", 1, 0, 3)],
            (_, _, _, env) =>
            {
                var f = 1 + 0.035 * Math.Sin(env.Lt * Gx.Tau * 0.9) * Gx.P(env, "amp", 1);
                return new GlyphCharXf { S = f };
            });

        yield return new(MotionPhase.Hold, "wave", "ウェーブ", [new("amp", 1, 0, 3)],
            (i, _, _, env) =>
            {
                var k = Gx.P(env, "amp", 1);
                return new GlyphCharXf
                {
                    Dy = Math.Sin(env.Lt * 7 + i * 0.75) * env.Size * 0.07 * k,
                    Rot = Math.Cos(env.Lt * 7 + i * 0.75) * 5 * k * Gx.Deg,
                };
            });

        yield return new(MotionPhase.Hold, "glitchtick", "グリッチ", [new("glitch", 1, 0, 3)],
            (i, _, _, env) =>
            {
                if (Gx.R(env.Seed, env.Step, 77) >= 0.22 * Gx.P(env, "glitch", 1) + 0.02) return null;
                var dx = Gx.R(env.Seed, env.Step, i, 5) < 0.6 ? Gx.Rs(env.Seed, env.Step, i, 6) * env.Size * 0.35 : 0;
                return new GlyphCharXf { Dx = dx };
            });

        yield return new(MotionPhase.Hold, "float", "ふわふわ", [new("amp", 1, 0, 3)],
            (i, _, _, env) =>
            {
                var k = Gx.P(env, "amp", 1);
                var a = env.Size * 0.075 * k;
                var ph = env.Seed % 97 * 0.13;
                return new GlyphCharXf
                {
                    Dy = (0.55 * Math.Sin(env.Lt * 2.1 + i * 1.3 + ph) + 0.6 * Gx.NoiseLike(env.Seed, env.Lt * 0.9 + i * 0.43)) * a,
                    Dx = Gx.NoiseLike(env.Seed + 7, env.Lt * 0.6 + i * 0.31) * a * 0.35,
                };
            });

        yield return new(MotionPhase.Hold, "sway", "ゆらぎ", [new("amp", 1, 0, 3)],
            (_, _, _, env) =>
            {
                var k = Gx.P(env, "amp", 1);
                return new GlyphCharXf
                {
                    Rot = Math.Sin(env.Lt * Gx.Tau / 3.2 + env.Seed % 7) * 2.6 * k * Gx.Deg,
                };
            });

        yield return new(MotionPhase.Hold, "pulse", "脈動", [new("amp", 1, 0, 3)],
            (_, _, _, env) =>
            {
                var k = Gx.P(env, "amp", 1);
                return new GlyphCharXf { S = 1 + 0.03 * Math.Exp(-Gx.Frac(env.Lt / 0.5) * 9) * k };
            });

        yield return new(MotionPhase.Hold, "shimmer", "きらめき", [new("amp", 1, 0, 3)],
            (i, _, _, env) =>
            {
                var k = Gx.P(env, "amp", 1);
                var tw = 0.5 + 0.5 * Gx.NoiseLike(env.Seed, env.Lt * 3.2 + i * 1.7);
                return new GlyphCharXf { A = 1 - 0.42 * Math.Min(1, k) * tw * tw };
            });

        yield return new(MotionPhase.Hold, "waveOut", "予兆の波", [new("amp", 1, 0, 3)],
            (i, _, _, env) =>
            {
                var k = Gx.P(env, "amp", 1);
                var sz = env.Size * 0.06 * k;
                return new GlyphCharXf
                {
                    Dy = Math.Sin(env.Lt * 6 + i * 0.9) * sz,
                    Rot = Math.Cos(env.Lt * 6 + i * 0.9) * 4 * k * Gx.Deg,
                };
            });
    }

    private static IEnumerable<MotionPresetDef> ExitPresets()
    {
        yield return new(MotionPhase.Exit, "cut", "カット", [], (_, _, _, _) => null);

        yield return new(MotionPhase.Exit, "fade", "フェード", [],
            (_, _, p, _) => p >= 1 ? new GlyphCharXf { Hide = true } : new GlyphCharXf { A = 1 - p });

        yield return new(MotionPhase.Exit, "shrink", "収縮", [],
            (_, _, p, env) =>
            {
                var e = Gx.InCubic(p);
                return new GlyphCharXf
                {
                    S = 1 - e * 0.96,
                    A = 1 - e * e,
                    Dy = e * env.H * 0.01,
                };
            });

        yield return new(MotionPhase.Exit, "fall", "崩落", [new("spin", 200, 0, 540)],
            (i, _, p, env) =>
            {
                var q = Gx.Win(p, Gx.R(env.Seed, i, 21) * 0.4, 0.6);
                if (q <= 0) return null;
                if (q >= 1) return new GlyphCharXf { Hide = true };
                return new GlyphCharXf
                {
                    Dy = Gx.InQuad(q) * env.H * 0.35,
                    Dx = Gx.Rs(env.Seed, i, 22) * env.W * 0.03 * q,
                    Rot = Gx.Rs(env.Seed, i, 23) * Gx.P(env, "spin", 200) * q * Gx.Deg,
                    A = 1 - Gx.InQuad(q) * 0.6,
                };
            });

        yield return new(MotionPhase.Exit, "drift", "霧散", [],
            (i, _, p, env) =>
            {
                var x = Gx.Clamp((p - Gx.R(env.Seed, i, 31) * 0.3) / 0.7);
                if (x <= 0) return null;
                if (x >= 1) return new GlyphCharXf { Hide = true };
                var e = Gx.InQuad(x);
                var ang = Gx.R(env.Seed, i, 32) * Gx.Tau;
                var dd = env.Size * 1.6 * (0.3 + 0.7 * Gx.R(env.Seed, i, 33));
                return new GlyphCharXf
                {
                    Dx = Math.Cos(ang) * dd * e,
                    Dy = Math.Sin(ang) * dd * e - env.Size * 0.3 * e,
                    Rot = Gx.Rs(env.Seed, i, 34) * 80 * e * Gx.Deg,
                    S = 1 - 0.35 * e,
                    A = 1 - e * e,
                };
            });

        yield return new(MotionPhase.Exit, "scatter", "飛散", [new("stagger", 0.35, 0, 0.7)],
            (i, _, p, env) =>
            {
                var d = Gx.R(env.Seed, i, 51) * Gx.P(env, "stagger", 0.35);
                var q = Gx.Clamp((p - d) / 0.65);
                if (q <= 0) return null;
                var e = Gx.InCubic(q);
                var ang = Gx.R(env.Seed, i, 52) * Gx.Tau;
                return new GlyphCharXf
                {
                    Dx = Math.Cos(ang) * env.W * 0.7 * e,
                    Dy = Math.Sin(ang) * env.W * 0.45 * e,
                    Rot = Gx.Rs(env.Seed, i, 53) * 540 * e * Gx.Deg,
                    S = 1 + e * 0.8,
                    A = 1 - q * q,
                };
            });

        yield return new(MotionPhase.Exit, "explode", "爆散", [new("spin", 260, 0, 720)],
            (i, _, p, env) =>
            {
                var x = Gx.Clamp(p / 0.7);
                if (x <= 0) return null;
                if (x >= 1) return new GlyphCharXf { Hide = true };
                var e = Gx.InCubic(x);
                var ang = Math.Atan2(Gx.Rs(env.Seed, i, 12) + 0.01, Gx.Rs(env.Seed, i, 13) + 0.01);
                var dist = Math.Max(env.W, env.H) * 0.9 * (0.35 + 0.65 * Gx.R(env.Seed, i, 13));
                return new GlyphCharXf
                {
                    Dx = Math.Cos(ang) * dist * e,
                    Dy = Math.Sin(ang) * dist * e,
                    Rot = Gx.Rs(env.Seed, i, 14) * Gx.P(env, "spin", 260) * e * Gx.Deg,
                    S = 1 + Gx.Rs(env.Seed, i, 15) * 0.6 * e,
                    A = 1 - x * x * x,
                };
            });

        yield return new(MotionPhase.Exit, "slide", "スライド退場", [new("stagger", 0.5, 0, 0.8)],
            (i, n, p, env) =>
            {
                var q = Gx.Stg(p, Gx.OrdLr(i, n), Gx.P(env, "stagger", 0.5));
                if (q <= 0) return null;
                if (q >= 1) return new GlyphCharXf { Hide = true };
                var e = Gx.InQuint(q);
                return new GlyphCharXf { Dx = e * env.W * 0.3, A = 1 - q };
            });

        yield return new(MotionPhase.Exit, "wipe", "ワイプ退場", [],
            (i, n, p, _) =>
            {
                var e = Gx.InOutExpo(p);
                var k = i / (double)Math.Max(1, n);
                if (k < e - 1.0 / Math.Max(1, n)) return new GlyphCharXf { Hide = true };
                return new GlyphCharXf { A = 1 };
            });

        yield return new(MotionPhase.Exit, "blur", "ブラー退場", [],
            (_, _, p, _) =>
            {
                var e = Gx.InQuad(p);
                return new GlyphCharXf { A = 1 - e, S = 1 + e * 0.2, Skew = e * 0.25 };
            });

        yield return new(MotionPhase.Exit, "stretch", "伸縮退場", [],
            (i, n, p, _) =>
            {
                var q = Gx.Stg(p, Gx.OrdLr(i, n), 0.3);
                if (q >= 1) return new GlyphCharXf { Hide = true };
                var e = Gx.InExpo(q);
                return new GlyphCharXf
                {
                    Sx = Gx.Lerp(1, 6, e),
                    Sy = Gx.Lerp(1, 0.6, e),
                    A = 1 - Gx.Smooth(0.7, 1, q),
                };
            });

        yield return new(MotionPhase.Exit, "glitch", "グリッチ退場", [],
            (i, _, p, env) =>
            {
                var amp = env.Size * (0.3 + p * 2.2);
                if (Gx.R(env.Seed, env.Step, i, 61) >= 0.75) return null;
                var a = 1.0;
                if (p > 0.55) a = Gx.R(env.Seed, env.Step, 63) < 0.5 ? 0.15 : 1;
                a *= 1 - Gx.Smooth(0.8, 1, p);
                return new GlyphCharXf { Dx = Gx.Rs(env.Seed, env.Step, i, 62) * amp, A = a };
            });

        yield return new(MotionPhase.Exit, "dissolve", "ほろほろ", [],
            (i, _, p, env) =>
            {
                var t0 = Gx.R(env.Seed, i, 72) * 0.7;
                var x = (p - t0) / 0.25;
                if (x <= 0) return null;
                if (x >= 1) return new GlyphCharXf { Hide = true };
                return new GlyphCharXf
                {
                    Dy = -x * env.Size * 0.05,
                    S = 1 + 0.3 * x,
                    A = 1 - x,
                };
            });

        yield return new(MotionPhase.Exit, "gravity", "重力落下", [],
            (i, _, p, env) =>
            {
                var d0 = Gx.R(env.Seed, i, 901) * 0.4;
                var q = Gx.Clamp((p - d0) / 0.6);
                if (q <= 0) return null;
                if (q >= 1) return new GlyphCharXf { Hide = true };
                var dFall = Math.Max(env.Size * 2, env.H);
                var h = env.Size * (0.12 + 0.22 * Gx.R(env.Seed, i, 902));
                var v0 = 2 * h + Math.Sqrt(4 * h * h + 4 * h * dFall);
                var down = -v0 * q + (dFall + v0) * q * q;
                return new GlyphCharXf
                {
                    Dy = down,
                    Dx = Gx.Rs(env.Seed, i, 903) * env.Size * 0.9 * q,
                    Rot = Gx.Rs(env.Seed, i, 904) * 240 * q * q * Gx.Deg,
                    A = 1 - Gx.Smooth(0.92, 1, q),
                };
            });

        yield return new(MotionPhase.Exit, "waveOut", "波で崩れる", [new("stagger", 0.55, 0, 0.85)],
            (i, n, p, env) =>
            {
                var q = Gx.Win(p, Gx.OrdLr(i, n) * Gx.P(env, "stagger", 0.55), 0.45);
                if (q <= 0) return null;
                if (q >= 1) return new GlyphCharXf { Hide = true };
                var a = env.Size * 0.42;
                return new GlyphCharXf
                {
                    Dy = -Math.Sin(q * Math.PI) * a + q * q * a * 2.6,
                    Dx = q * a * 0.6,
                    Rot = Math.Sin(q * Math.PI * 1.5) * 28 * Gx.Deg + q * 30 * Gx.Deg,
                    A = 1 - Gx.Smooth(0.45, 1, q),
                };
            });

        yield return new(MotionPhase.Exit, "riseOut", "上へ抜ける", [],
            (i, _, p, env) =>
            {
                var q = Gx.Win(p, Gx.R(env.Seed, i, 201) * 0.5, 0.5);
                if (q <= 0) return null;
                if (q >= 1) return new GlyphCharXf { Hide = true };
                var st = 1 + 0.5 * Math.Sin(Math.PI * q) * q;
                return new GlyphCharXf
                {
                    Dy = -Math.Pow(q, 2.2) * env.Size * 1.5,
                    Sy = st,
                    Sx = 1 - 0.1 * (st - 1),
                    A = 1 - Gx.Smooth(0.85, 1, q),
                };
            });

        yield return new(MotionPhase.Exit, "shatterLite", "軽碎", [],
            (i, _, p, env) =>
            {
                var q = Gx.Win(p, Gx.R(env.Seed, i, 81) * 0.35, 0.65);
                if (q <= 0) return null;
                if (q >= 1) return new GlyphCharXf { Hide = true };
                var e = Gx.InCubic(q);
                var ang = Gx.R(env.Seed, i, 82) * Gx.Tau;
                return new GlyphCharXf
                {
                    Dx = Math.Cos(ang) * env.Size * 1.4 * e,
                    Dy = Math.Sin(ang) * env.Size * 1.0 * e + env.Size * 0.8 * q * q,
                    Rot = Gx.Rs(env.Seed, i, 83) * 180 * e * Gx.Deg,
                    A = 1 - q,
                };
            });
    }

    public static class Registry
    {
        public static IReadOnlyList<MotionPresetDef> Enter => Filter(MotionPhase.Enter);

        public static IReadOnlyList<MotionPresetDef> Hold => Filter(MotionPhase.Hold);

        public static IReadOnlyList<MotionPresetDef> Exit => Filter(MotionPhase.Exit);

        private static IReadOnlyList<MotionPresetDef> Filter(MotionPhase phase)
        {
            var list = new List<MotionPresetDef>();
            foreach (var d in _all)
                if (d.Phase == phase)
                    list.Add(d);
            return list;
        }
    }
}
