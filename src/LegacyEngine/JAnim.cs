// 移植自 frontend/src/engine/05_anim.js
//
// enter/hold/exit 三组核心动画 + 逐字/逐片合并器 + item 几何助手。
// JS 的 apply(env, it, p, ctx) 在 C# 定型为 MoveDef.Draw(env)：
//   it   → env.It（JObject item，与前端 duck-typed 对象一一对应）
//   p    → env.Ap（enter→pIn / exit→pOut / hold→amt，mainDraw 调用前写入）
//   ctx  → env.Cut（JS ctx={dur,inDur,outDur} 与 cut 同名字段一一对应，不再单独建对象）
// charFns/pieceFns 是函数值字段，JObject 装不下，挂 env.CharFns/env.PieceFns（见 Env）。
// 挂到 def 上的 flags（assemble.pieces / explode·fall·drift.shatter / type.cursor / wipe.bar）
// MoveDef 暂无对应属性：mainDraw/drawFx 回接方按 key 特判（各条目行内有注释）。
//
// 随机数：Draw 内全部走 J.R/J.Rs 确定性哈希（非顺序流），哈希入参逐字保持——
// 同 seed 同动画轨迹；不用 Rng 顺序流（planner 侧才用）。

using Newtonsoft.Json.Linq;

namespace JizuraLive.LegacyEngine;

// ---------------- 逐字/逐片变换记录（03_text.js 的 J.PT / J.PID 对位；drawItem 消费） ----------------

/// <summary>J.PT/J.PID：碎片（笔画/连通域）变换。Pid 是恒等哨兵，引用比较，不得改写其字段。</summary>
public sealed class PieceXf
{
    public double Dx, Dy, Rot;
    public double S = 1;

    /// <summary>拉伸量（>1 拉长），与 Sdir 配套。</summary>
    public double St = 1;

    /// <summary>拉伸方向（度）。</summary>
    public double Sdir;

    public double A = 1;
}

/// <summary>
/// charFn 返回值。字段全部可缺省，drawItem 侧按 JS 语义取默认：
/// dx/dy/rot 取 0，s/a/sx/sy/skew/blur 缺省即“未设置”（JS undefined，消费处 ?? 默认），
/// ch/color 缺省保持原字形。
/// </summary>
public sealed class CharXf
{
    public bool Hide;
    public double Dx, Dy, Rot;
    public double? S, A;
    public double? Sx, Sy;
    public string? Ch;
    public string? Color;
    public double? Skew, Blur;
    public bool Outline;
    public double[]? ClipX, ClipY;
}

public static class JAnim
{
    /// <summary>(i, g, n)：字 index / 字形排版对象（layoutText 产物，core 动画未用）/ 字数。</summary>
    public delegate CharXf? CharFn(int i, JObject? g, int n);

    /// <summary>(ci, pj, pc, ox, oy, g)：片 index / 片内 index / 片数 / 片中心偏移 / 字形。返回 Pid=恒等、null=隐藏。</summary>
    public delegate PieceXf? PieceFn(int ci, int pj, int pc, double ox, double oy, JObject? g);

    /// <summary>J.PID（恒定碎片哨兵；Object.freeze 的 C# 对位，引用比较）。</summary>
    public static readonly PieceXf Pid = new();

    /// <summary>J.PT。</summary>
    public static PieceXf Pt(double dx = 0, double dy = 0, double rot = 0, double s = 1, double st = 1, double sdir = 0, double a = 1) =>
        new() { Dx = dx, Dy = dy, Rot = rot, S = s, St = st, Sdir = sdir, A = a };

    // ---------------- J.itemBands / J.itemBox ----------------

    /// <summary>横向条带，盖住 item 的纵向范围：返回 [y0, y1, dx] 列表。</summary>
    public static List<double[]> ItemBands(Env env, JObject it, int n, Func<int, int, double> dxFn)
    {
        var m = JLayouts.Measure(it);
        var h = Math.Max(m.H, Num(it, "size")) * 1.25 + 20;
        var y0 = Num(it, "y") - h / 2;
        var seed = SeedOf(it);
        var cuts = new List<double> { 0 };
        for (var i = 1; i < n; i++) cuts.Add(J.R(seed, n, i, 3));
        cuts.Add(1);
        cuts.Sort();
        var @out = new List<double[]>();
        for (var i = 0; i < cuts.Count - 1; i++)
            @out.Add(new[] { y0 + cuts[i] * h, y0 + cuts[i + 1] * h, dxFn(i, cuts.Count - 1) });
        return @out;
    }

    /// <summary>bands 数组 → JArray（[[y0,y1,dx],…]；drawFx 消费）。</summary>
    public static JArray JArr(IReadOnlyList<double[]> rows)
    {
        var a = new JArray();
        foreach (var r in rows) a.Add(new JArray(r.Select(v => new JValue(v))));
        return a;
    }

    /// <summary>J.itemBox：item 在设计空间的包围盒（未旋转）。</summary>
    public static ItemBounds ItemBox(JObject it)
    {
        var m = JLayouts.Measure(it);
        var align = it["align"]?.Value<string>();
        var vertical = it["vertical"]?.Value<bool>() == true;
        var x = Num(it, "x");
        var y = Num(it, "y");
        var x0 = vertical ? x - m.W / 2 : align == "left" ? x : align == "right" ? x - m.W : x - m.W / 2;
        var y0 = vertical && align == "left" ? y : y - m.H / 2;
        return new ItemBounds
        {
            X0 = x0,
            Y0 = y0,
            X1 = x0 + m.W,
            Y1 = y0 + m.H,
            W = m.W,
            H = m.H,
            Cx = x0 + m.W / 2,
            Cy = y0 + m.H / 2
        };
    }

    // ---------------- combineChar / combinePiece（JS 同名） ----------------

    /// <summary>合并 charFns：hide 一票否决；dx/dy/rot/skew/blur 累加，s/a/sx/sy 连乘，其余非空覆盖。</summary>
    public static CharFn? CombineChar(IReadOnlyList<CharFn> fns)
    {
        if (fns.Count == 0) return null;
        if (fns.Count == 1) return fns[0];
        return (i, g, n) =>
        {
            CharXf? o = null;
            foreach (var f in fns)
            {
                var r = f(i, g, n);
                if (r == null) continue;
                if (r.Hide) return r;
                o ??= new CharXf();
                o.Dx += r.Dx;
                o.Dy += r.Dy;
                o.Rot += r.Rot;
                if (r.S != null) o.S = (o.S ?? 1) * r.S.Value;
                if (r.A != null) o.A = (o.A ?? 1) * r.A.Value;
                if (r.Sx != null && r.Sx.Value != 0) o.Sx = (o.Sx ?? 1) * r.Sx.Value;
                if (r.Sy != null && r.Sy.Value != 0) o.Sy = (o.Sy ?? 1) * r.Sy.Value;
                if (!string.IsNullOrEmpty(r.Ch)) o.Ch = r.Ch;
                if (!string.IsNullOrEmpty(r.Color)) o.Color = r.Color;
                if (r.Skew != null && r.Skew.Value != 0) o.Skew = (o.Skew ?? 0) + r.Skew.Value;
                if (r.Blur != null && r.Blur.Value != 0) o.Blur = (o.Blur ?? 0) + r.Blur.Value;
                if (r.Outline) o.Outline = true;
                if (r.ClipX != null)
                    o.ClipX = o.ClipX != null
                        ? new[] { Math.Max(o.ClipX[0], r.ClipX[0]), Math.Min(o.ClipX[1], r.ClipX[1]) }
                        : r.ClipX;
                if (r.ClipY != null)
                    o.ClipY = o.ClipY != null
                        ? new[] { Math.Max(o.ClipY[0], r.ClipY[0]), Math.Min(o.ClipY[1], r.ClipY[1]) }
                        : r.ClipY;
            }
            return o;
        };
    }

    /// <summary>合并 pieceFns：null 一票否决（隐藏），Pid 跳过，其余逐字段叠加；st 取最大并带上其 sdir。</summary>
    public static PieceFn? CombinePiece(IReadOnlyList<PieceFn> fns)
    {
        if (fns.Count == 0) return null;
        if (fns.Count == 1) return fns[0];
        return (ci, pj, pc, ox, oy, g) =>
        {
            PieceXf? o = null;
            foreach (var f in fns)
            {
                var r = f(ci, pj, pc, ox, oy, g);
                if (r == null) return null;
                if (ReferenceEquals(r, Pid)) continue;
                o ??= new PieceXf();
                o.Dx += r.Dx;
                o.Dy += r.Dy;
                o.Rot += r.Rot;
                o.S *= r.S;
                o.A *= r.A;
                if (r.St > o.St)
                {
                    o.St = r.St;
                    o.Sdir = r.Sdir;
                }
            }
            return o ?? Pid;
        };
    }

    // ---------------- 注册（顺序即 JS 的 ENTER_ORDER / HOLD_ORDER / EXIT_ORDER，06_layouts.js L1304-1306） ----------------

    private static int _installed;

    /// <summary>幂等注册本文件全部 enter/hold/exit 条目（pack="core"）。</summary>
    public static void Install()
    {
        if (System.Threading.Interlocked.Exchange(ref _installed, 1) != 0) return;
        RegisterEnter();
        RegisterHold();
        RegisterExit();
    }

    private static void RegisterEnter()
    {
        Reg.Register("enter", "cut", new MoveDef
        {
            Name = "カット",
            Draw = _ => { }
        });

        Reg.Register("enter", "assemble", new MoveDef
        {
            Name = "分解→集合", // JS: pieces: true（MoveDef 暂无该 flag，mainDraw 按 key 特判）
            Draw = env =>
            {
                var it = env.It!;
                var dur = env.Cut!.InDur;
                var lt = env.Lt - Num(it, "delay");
                var spread = Num(it, "size") * 3.2 * (0.6 + env.Fx.Motion * 0.7);
                var seed = SeedOf(it);
                env.PieceFns.Add((ci, pj, _, _, _, _) =>
                {
                    var d = J.R(seed, ci, pj, 1) * dur * 0.45;
                    var x = (lt - d) / (dur * 0.62);
                    if (x < 0) return null;
                    var e = J.E.OutExpo(x);
                    var k = 1 - e;
                    if (k <= 0.0005) return Pid;
                    var ang = J.R(seed, ci, pj, 2) * J.TAU;
                    var dist = spread * (0.35 + 0.65 * J.R(seed, ci, pj, 3));
                    var sp = (e - J.E.OutExpo(x - 1 / (env.Fps * dur * 0.62))) * dist;
                    return Pt(Math.Cos(ang) * dist * k, Math.Sin(ang) * dist * k, J.Rs(seed, ci, pj, 4) * 190 * k,
                        1 + (J.Lerp(0.4, 2.1, J.R(seed, ci, pj, 5)) - 1) * k, 1 + Math.Min(2.0, sp * 0.02), ang / J.DEG, 1);
                });
            }
        });

        Reg.Register("enter", "slice", new MoveDef
        {
            Name = "スライス",
            Draw = env =>
            {
                var it = env.It!;
                var p = env.Ap;
                var w = (double)env.W;
                it["bands"] = JArr(ItemBands(env, it, 7, (i, _) => (1 - J.E.OutExpo(p * 1.2 - 0.05 * i)) * (i % 2 == 1 ? 1 : -1) * w * 0.9));
            }
        });

        Reg.Register("enter", "type", new MoveDef
        {
            Name = "タイプ", // JS: cursor: true（MoveDef 暂无该 flag，drawFx 按 key 特判光标）
            Draw = env =>
            {
                var it = env.It!;
                var p = env.Ap;
                var n = (int)(JLayouts.Measure(it).Lay?["N"]?.Value<double>() ?? 0);
                var k = (int)Math.Floor(p * (n + 0.999));
                env.CharFns.Add((i, _, _) => i >= k ? new CharXf { Hide = true } : null);
                it["cursorAt"] = p < 1 ? k : -1;
            }
        });

        Reg.Register("enter", "pop", new MoveDef
        {
            Name = "ポップ",
            Draw = env =>
            {
                var it = env.It!;
                var p = env.Ap;
                var seed = SeedOf(it);
                env.CharFns.Add((i, _, n) =>
                {
                    var d = n > 1 ? i / (double)(n - 1) * 0.45 : 0;
                    var q = J.Clamp((p - d) / 0.55);
                    if (q <= 0) return new CharXf { Hide = true };
                    return new CharXf { S = J.E.OutBack(q, 2.6), Rot = (1 - J.E.OutCubic(q)) * J.Rs(seed, i, 9) * 28 };
                });
            }
        });

        Reg.Register("enter", "drop", new MoveDef
        {
            Name = "落下",
            Draw = env =>
            {
                var it = env.It!;
                var p = env.Ap;
                var size = Num(it, "size");
                var seed = SeedOf(it);
                env.CharFns.Add((i, _, n) =>
                {
                    var d = n > 1 ? J.R(seed, i, 4) * 0.5 : 0;
                    var q = J.Clamp((p - d) / 0.5);
                    if (q <= 0) return new CharXf { Hide = true };
                    var b = Bounce(q);
                    return new CharXf { Dy = -(1 - b) * size * 2.4, Sy = 1 + (1 - q) * 0.5, Sx = 1 - (1 - q) * 0.2 };
                });
            }
        });

        Reg.Register("enter", "stretch", new MoveDef
        {
            Name = "伸縮",
            Draw = env =>
            {
                var it = env.It!;
                var e = J.E.OutExpo(env.Ap);
                it["sx"] = NumOr1(it, "sx") * J.Lerp(4.2, 1, e);
                it["streak"] = new JObject { ["n"] = 4, ["dx"] = Num(it, "size") * 0.55 * (1 - e), ["a"] = 0.3 * (1 - e) };
            }
        });

        Reg.Register("enter", "wipe", new MoveDef
        {
            Name = "ワイプ", // JS: bar: true（drawFx 画 wipeBar，数据在 item 上）
            Draw = env =>
            {
                var it = env.It!;
                var p = env.Ap;
                var e = J.E.InOutExpo(p);
                var m = JLayouts.Measure(it);
                var itx = Num(it, "x");
                var size = Num(it, "size");
                var x0 = itx - m.W / 2 - size * 0.2;
                var x1 = itx + m.W / 2 + size * 0.2;
                var dir = SeedOf(it) % 2 != 0 ? 1 : -1;
                var edge = dir > 0 ? J.Lerp(x0, x1, e) : J.Lerp(x1, x0, e);
                it["clip"] = dir > 0 ? new JArray(x0 - 4000, edge) : new JArray(edge, x1 + 4000);
                it["wipeBar"] = p < 1 ? new JObject { ["x"] = edge, ["h"] = m.H * 1.3 + size * 0.2 } : null;
            }
        });

        Reg.Register("enter", "blur", new MoveDef
        {
            Name = "ブラー",
            Draw = env =>
            {
                var it = env.It!;
                var e = J.E.OutCubic(env.Ap);
                it["blur"] = Num(it, "blur") + (1 - e) * 26;
                it["alpha"] = NumNull(it, "alpha", 1) * Math.Pow(e, 0.7);
                it["size"] = Num(it, "size") * (1 + 0.18 * (1 - e));
                it["spacing"] = 1 - e;
                it["track"] = Num(it, "track") + (1 - e) * 0.5;
            }
        });

        Reg.Register("enter", "spin", new MoveDef
        {
            Name = "回転",
            Draw = env =>
            {
                var it = env.It!;
                var p = env.Ap;
                var seed = SeedOf(it);
                env.CharFns.Add((i, _, n) =>
                {
                    var d = n > 1 ? i / (double)(n - 1) * 0.4 : 0;
                    var q = J.Clamp((p - d) / 0.6);
                    if (q <= 0) return new CharXf { Hide = true };
                    var e = J.E.OutExpo(q);
                    return new CharXf
                    {
                        Rot = (1 - e) * (J.R(seed, i) > 0.5 ? 1 : -1) * 200,
                        S = J.Lerp(0.15, 1, e),
                        A = Math.Min(1, q * 3)
                    };
                });
            }
        });

        Reg.Register("enter", "flicker", new MoveDef
        {
            Name = "点滅",
            Draw = env =>
            {
                var it = env.It!;
                var p = env.Ap;
                var seed = SeedOf(it);
                var step = (int)env.Step;
                env.CharFns.Add((i, _, _) =>
                {
                    if (p >= 1) return null;
                    return J.R(seed, step, i) < p * 1.25 ? null : new CharXf { Hide = true };
                });
            }
        });

        Reg.Register("enter", "scramble", new MoveDef
        {
            Name = "スクランブル",
            Draw = env =>
            {
                var it = env.It!;
                var p = env.Ap;
                var seed = SeedOf(it);
                var step = (int)env.Step;
                var pool = JLang.Pool("scramble") ?? "";
                env.CharFns.Add((i, _, n) =>
                {
                    var settle = 0.25 + 0.75 * (n > 1 ? i / (double)(n - 1) : 1);
                    if (p >= settle) return null;
                    if (p < settle * 0.25 && J.R(seed, i, step, 2) < 0.5) return new CharXf { Hide = true };
                    if (pool.Length == 0) return null;
                    var ch = pool[(int)Math.Floor(J.R(seed, i, step) * pool.Length)];
                    return new CharXf { Ch = ch.ToString(), A = 0.85 };
                });
            }
        });

        Reg.Register("enter", "zoom", new MoveDef
        {
            Name = "ズーム",
            Draw = env =>
            {
                var it = env.It!;
                var p = env.Ap;
                var e = J.E.OutExpo(p);
                it["size"] = Num(it, "size") * J.Lerp(1.7, 1, e);
                it["blur"] = Num(it, "blur") + (1 - e) * 14;
                it["alpha"] = NumNull(it, "alpha", 1) * Math.Min(1, p * 4);
            }
        });
    }

    private static void RegisterHold()
    {
        Reg.Register("hold", "still", new MoveDef
        {
            Name = "静止",
            Draw = _ => { }
        });

        Reg.Register("hold", "jitter", new MoveDef
        {
            Name = "ジッター",
            Draw = env =>
            {
                var it = env.It!;
                var amt = env.Ap;
                var seed = SeedOf(it);
                var step = (int)env.Step;
                var a = Num(it, "size") * 0.025 * amt * env.Fx.Motion;
                if (a < 0.2) return;
                env.CharFns.Add((i, _, _) => new CharXf
                {
                    Dx = J.Rs(seed, step, i, 1) * a,
                    Dy = J.Rs(seed, step, i, 2) * a,
                    Rot = J.Rs(seed, step, i, 3) * 4 * amt
                });
            }
        });

        Reg.Register("hold", "drift", new MoveDef
        {
            Name = "ドリフト",
            Draw = env =>
            {
                var it = env.It!;
                var amt = env.Ap;
                var u = env.Lt / Math.Max(0.3, env.Cut!.Dur);
                var dir = SeedOf(it) % 2 != 0 ? 1 : -1;
                it["x"] = Num(it, "x") + dir * (u - 0.5) * env.W * 0.035 * env.Fx.Motion;
                it["size"] = Num(it, "size") * (1 + 0.05 * u * env.Fx.Motion);
            }
        });

        Reg.Register("hold", "breathe", new MoveDef
        {
            Name = "呼吸",
            Draw = env =>
            {
                var it = env.It!;
                var amt = env.Ap;
                it["size"] = Num(it, "size") * (1 + 0.035 * Math.Sin(env.Lt * J.TAU * 0.9) * amt);
                it["track"] = Num(it, "track") + 0.03 * Math.Sin(env.Lt * J.TAU * 0.6) * amt;
            }
        });

        Reg.Register("hold", "wave", new MoveDef
        {
            Name = "ウェーブ",
            Draw = env =>
            {
                var it = env.It!;
                var amt = env.Ap;
                var size = Num(it, "size");
                var t = env.Lt;
                env.CharFns.Add((i, _, _) => new CharXf
                {
                    Dy = Math.Sin(t * 7 + i * 0.75) * size * 0.07 * amt,
                    Rot = Math.Cos(t * 7 + i * 0.75) * 5 * amt
                });
            }
        });

        Reg.Register("hold", "glitchtick", new MoveDef
        {
            Name = "グリッチ",
            Draw = env =>
            {
                var it = env.It!;
                var amt = env.Ap;
                var seed = SeedOf(it);
                var step = (int)env.Step;
                if (J.R(seed, step, 77) < 0.22 * env.Fx.Glitch * amt + 0.02)
                    it["bands"] = JArr(ItemBands(env, it, 6, (i, _) => J.R(seed, step, i, 5) < 0.6 ? J.Rs(seed, step, i, 6) * Num(it, "size") * 0.35 : 0));
            }
        });
    }

    private static void RegisterExit()
    {
        Reg.Register("exit", "cut", new MoveDef
        {
            Name = "カット",
            Draw = _ => { }
        });

        Reg.Register("exit", "explode", new MoveDef
        {
            Name = "爆散", // JS: pieces: true, shatter: true（mainDraw 按 key 特判）
            Draw = env =>
            {
                var it = env.It!;
                var dur = env.Cut!.OutDur;
                var lt = env.Lt - (env.Cut.Dur - env.Cut.OutDur);
                var spread = Math.Max(env.W, env.H) * 0.9 * (0.5 + env.Fx.Motion * 0.6);
                var seed = SeedOf(it);
                it["shatter"] = true;
                env.PieceFns.Add((ci, pj, _, ox, oy, _) =>
                {
                    var d = J.R(seed, ci, pj, 11) * dur * 0.3;
                    var x = (lt - d) / (dur * 0.7);
                    if (x <= 0) return Pid;
                    if (x >= 1) return null;
                    var e = J.E.InCubic(x);
                    var ang = Math.Atan2(oy + 0.01, ox + 0.01) + J.Rs(seed, ci, pj, 12) * 1.1;
                    var dist = spread * (0.35 + 0.65 * J.R(seed, ci, pj, 13));
                    var sp = (e - J.E.InCubic(x - 1 / (env.Fps * dur * 0.7))) * dist;
                    return Pt(Math.Cos(ang) * dist * e, Math.Sin(ang) * dist * e, J.Rs(seed, ci, pj, 14) * 260 * e,
                        1 + J.Rs(seed, ci, pj, 15) * 0.6 * e, 1 + Math.Min(2.4, sp * 0.012), ang / J.DEG, 1 - x * x * x);
                });
            }
        });

        Reg.Register("exit", "fall", new MoveDef
        {
            Name = "崩落", // JS: pieces: true, shatter: true
            Draw = env =>
            {
                var it = env.It!;
                var lt = env.Lt - (env.Cut!.Dur - env.Cut.OutDur);
                var outDur = env.Cut.OutDur;
                var g = env.H * 5.5;
                var seed = SeedOf(it);
                var step = (int)env.Step;
                it["shatter"] = true;
                env.CharFns.Add((_, _, _) => null);
                env.PieceFns.Add((ci, pj, _, _, _, _) =>
                {
                    var x = lt - J.R(seed, ci, pj, 21) * outDur * 0.4;
                    if (x <= 0)
                    {
                        var tr = lt > -0.25 ? 1 : 0; // 塌落前先抖一下
                        return tr != 0 ? Pt(J.Rs(seed, step, ci, pj) * Num(it, "size") * 0.012, 0) : Pid;
                    }
                    var v = g * x;
                    return Pt(J.Rs(seed, ci, pj, 22) * env.W * 0.03 * x, 0.5 * g * x * x, J.Rs(seed, ci, pj, 23) * 200 * x,
                        1, 1 + Math.Min(2.2, v * 0.0012), 90, 1);
                });
            }
        });

        Reg.Register("exit", "drift", new MoveDef
        {
            Name = "霧散", // JS: pieces: true, shatter: true
            Draw = env =>
            {
                var it = env.It!;
                var dur = env.Cut!.OutDur;
                var lt = env.Lt - (env.Cut.Dur - env.Cut.OutDur);
                var dist0 = Num(it, "size") * 1.6;
                var seed = SeedOf(it);
                it["shatter"] = true;
                env.PieceFns.Add((ci, pj, _, _, _, _) =>
                {
                    var x = (lt - J.R(seed, ci, pj, 31) * dur * 0.3) / (dur * 0.7);
                    if (x <= 0) return Pid;
                    if (x >= 1) return null;
                    var e = J.E.InQuad(x);
                    var ang = J.R(seed, ci, pj, 32) * J.TAU;
                    var dd = dist0 * (0.3 + 0.7 * J.R(seed, ci, pj, 33));
                    return Pt(Math.Cos(ang) * dd * e, Math.Sin(ang) * dd * e - Num(it, "size") * 0.3 * e,
                        J.Rs(seed, ci, pj, 34) * 80 * e, 1 - 0.35 * e, 1 + e * 0.8, ang / J.DEG, 1 - e * e);
                });
            }
        });

        Reg.Register("exit", "slice", new MoveDef
        {
            Name = "スライス退場",
            Draw = env =>
            {
                var it = env.It!;
                var p = env.Ap;
                var e = J.E.InExpo(p);
                var w = (double)env.W;
                var seed = SeedOf(it);
                it["bands"] = JArr(ItemBands(env, it, 7, (i, _) => e * (i % 2 == 1 ? -1 : 1) * w * 1.1 * (0.6 + 0.4 * J.R(seed, i, 41))));
            }
        });

        Reg.Register("exit", "wipe", new MoveDef
        {
            Name = "ワイプ退場", // JS: bar: true
            Draw = env =>
            {
                var it = env.It!;
                var p = env.Ap;
                var e = J.E.InOutExpo(p);
                var m = JLayouts.Measure(it);
                var itx = Num(it, "x");
                var size = Num(it, "size");
                var x0 = itx - m.W / 2 - size * 0.2;
                var x1 = itx + m.W / 2 + size * 0.2;
                var edge = J.Lerp(x0, x1, e);
                it["clip"] = new JArray(edge, x1 + 4000);
                it["wipeBar"] = p > 0 && p < 1 ? new JObject { ["x"] = edge, ["h"] = m.H * 1.3 + size * 0.2 } : null;
            }
        });

        Reg.Register("exit", "shrink", new MoveDef
        {
            Name = "収縮",
            Draw = env =>
            {
                var it = env.It!;
                var e = J.E.InCubic(env.Ap);
                it["size"] = Num(it, "size") * (1 - e * 0.96);
                it["alpha"] = NumNull(it, "alpha", 1) * (1 - e * e);
                it["track"] = Num(it, "track") - e * 0.2;
            }
        });

        Reg.Register("exit", "blur", new MoveDef
        {
            Name = "ブラー退場",
            Draw = env =>
            {
                var it = env.It!;
                var e = J.E.InQuad(env.Ap);
                it["blur"] = Num(it, "blur") + e * 30;
                it["alpha"] = NumNull(it, "alpha", 1) * (1 - e);
                it["size"] = Num(it, "size") * (1 + e * 0.2);
            }
        });

        Reg.Register("exit", "stretch", new MoveDef
        {
            Name = "伸縮退場",
            Draw = env =>
            {
                var it = env.It!;
                var p = env.Ap;
                var e = J.E.InExpo(p);
                it["sx"] = NumOr1(it, "sx") * J.Lerp(1, 6, e);
                it["sy"] = NumOr1(it, "sy") * J.Lerp(1, 0.6, e);
                it["alpha"] = NumNull(it, "alpha", 1) * (1 - J.Smooth(0.7, 1, p));
                it["streak"] = new JObject { ["n"] = 3, ["dx"] = -Num(it, "size") * 0.8 * e, ["a"] = 0.25 * e };
            }
        });

        Reg.Register("exit", "scatter", new MoveDef
        {
            Name = "飛散",
            Draw = env =>
            {
                var it = env.It!;
                var p = env.Ap;
                var seed = SeedOf(it);
                var w = (double)env.W;
                env.CharFns.Add((i, _, _) =>
                {
                    var d = J.R(seed, i, 51) * 0.35;
                    var q = J.Clamp((p - d) / 0.65);
                    if (q <= 0) return null;
                    var e = J.E.InCubic(q);
                    var ang = J.R(seed, i, 52) * J.TAU;
                    return new CharXf
                    {
                        Dx = Math.Cos(ang) * w * 0.7 * e,
                        Dy = Math.Sin(ang) * w * 0.45 * e,
                        Rot = J.Rs(seed, i, 53) * 540 * e,
                        S = 1 + e * 0.8,
                        A = 1 - q * q
                    };
                });
            }
        });

        Reg.Register("exit", "glitch", new MoveDef
        {
            Name = "グリッチ退場",
            Draw = env =>
            {
                var it = env.It!;
                var p = env.Ap;
                var seed = SeedOf(it);
                var step = (int)env.Step;
                var amp = Num(it, "size") * (0.3 + p * 2.2);
                it["bands"] = JArr(ItemBands(env, it, 9, (i, _) => J.R(seed, step, i, 61) < 0.75 ? J.Rs(seed, step, i, 62) * amp : 0));
                if (p > 0.55)
                    it["alpha"] = NumNull(it, "alpha", 1) * (J.R(seed, step, 63) < 0.5 ? 0.15 : 1) * (1 - J.Smooth(0.8, 1, p));
            }
        });
    }

    // ---------------- 工具 ----------------

    /// <summary>bounce 缓动（JS 同名局部函数）。</summary>
    private static double Bounce(double x)
    {
        const double n1 = 7.5625, d1 = 2.75;
        if (x < 1 / d1) return n1 * x * x;
        if (x < 2 / d1) return n1 * (x -= 1.5 / d1) * x + 0.75;
        if (x < 2.5 / d1) return n1 * (x -= 2.25 / d1) * x + 0.9375;
        return n1 * (x -= 2.625 / d1) * x + 0.984375;
    }

    /// <summary>item 数值字段，缺省 0（JS undefined 参与运算处 item 上恒有值）。</summary>
    internal static double Num(JObject it, string k)
    {
        var v = it[k];
        return v == null || v.Type != JTokenType.Float && v.Type != JTokenType.Integer ? 0 : (double)v;
    }

    /// <summary>JS (x || 1)：缺省或 0 都取 1。</summary>
    internal static double NumOr1(JObject it, string k)
    {
        var v = Num(it, k);
        return v == 0 ? 1 : v;
    }

    /// <summary>JS (x ?? d)：0 保持 0，只有缺省取默认。</summary>
    internal static double NumNull(JObject it, string k, double d)
    {
        var v = it[k];
        return v == null || v.Type == JTokenType.Null ? d : (double)v;
    }

    /// <summary>JS (it.seed | 0)：uint 域的 seed 取低 32 位成 int32（哈希入参）。</summary>
    internal static int SeedOf(JObject it)
    {
        var v = it["seed"];
        var d = v == null || v.Type == JTokenType.Null ? 0 : (double)v;
        return unchecked((int)(long)d);
    }

    static JAnim() => Install();
}

/// <summary>J.itemBox 返回 {x0,y0,x1,y1,w,h,cx,cy}。</summary>
public sealed class ItemBounds
{
    public double X0, Y0, X1, Y1, W, H, Cx, Cy;
}
