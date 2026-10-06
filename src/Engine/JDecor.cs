// 移植自 frontend/src/engine/07_decor.js
//
// 内置装饰图形 15 个（JS J.DECOR）+ HUD 绘制（J.drawHUD）。
// JS 的 draw(env, bb, P)：bb 是 layout render 返回的主体 bbox（后景装饰传 null）——
// C# 从 env.Body 读，空时用画面中央默认框（JLayouts.CenterBb，对位 JS 的 center(env,bb)）。
// 参数 P → DecorSpec（seed/n/corner/accent/right/low/big/mode/from/to）。
// 随机全走 J.r/J.rs/J.rr 的无状态哈希（与 JS 同参同值，消费顺序即调用顺序）；
// 配色 sc.* 用 Scheme 的松散键访问；`a || b` 的空串回退用 ColOr 对齐。

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace JizuraLive.Engine;

public static class JDecor
{
    static JDecor() => Install();

    /// <summary>幂等注册本文件全部 decor。注册顺序 = JS J.DECOR_ORDER（抽取候选顺序，不能变）。</summary>
    public static void Install()
    {
        if (Reg.Decor.ContainsKey("brackets")) return; // 幂等
        RegisterBrackets();
        RegisterRings();
        RegisterDots();
        RegisterArrows();
        RegisterSlash();
        RegisterSparks();
        RegisterLeaders();
        RegisterWaveform();
        RegisterBarcode();
        RegisterGrid();
        RegisterStripes();
        RegisterBlobs();
        RegisterBars();
        RegisterShapes();
        RegisterCounter();
    }

    // ---------------- 公共小助手 ----------------

    /// <summary>JS inOut：进场 outCubic × (1 - 出场 inCubic)。</summary>
    private static double InOut(Env env) => J.E.OutCubic(J.Clamp(env.Lt / 0.3)) * (1 - J.E.InCubic(env.POut));

    /// <summary>sc[k]（Scheme 松散键；缺失为 null → 绘制助手跳过，对位 JS 无配色时 fillStyle 抛错被 try/catch 吞掉）。</summary>
    private static string? Col(Env env, string k) => env.Sc?.Str(k);

    /// <summary>JS 的 `sc.a || sc.b`：a 缺省/空串时回退 b。</summary>
    private static string? ColOr(Env env, string a, string b)
    {
        var v = env.Sc?.Str(a);
        return !string.IsNullOrEmpty(v) ? v : env.Sc?.Str(b);
    }

    private static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    /// <summary>monoF(env) = (st.fonts.mono && st.fonts.mono[0]) || 'mono'。</summary>
    private static string MonoF(Env env) => JLayouts.Font0(env.St, "mono") ?? "mono";

    private static readonly string[] ShapeTypes = { "circle", "square", "tri", "halftone", "halftone", "ring", "ring" };

    // ---------------- 前景装饰（front） ----------------

    private static void RegisterBrackets()
    {
        Reg.Register("decor", "brackets", new DecorDef
        {
            Name = "枠マーク",
            Layer = "front",
            Draw = (env, d) =>
            {
                var bb = JLayouts.CenterBb(env, env.Body);
                var e = J.E.OutExpo(J.Clamp(env.Lt / 0.35)) * (1 - J.E.InCubic(env.POut));
                if (e <= 0) return;
                var pad = 18 + (bb.Y1 - bb.Y0) * 0.12;
                var x0 = bb.X0 - pad;
                var x1 = bb.X1 + pad;
                var y0 = bb.Y0 - pad;
                var y1 = bb.Y1 + pad;
                var cx = (x0 + x1) / 2;
                var cy = (y0 + y1) / 2;
                var X0 = J.Lerp(cx, x0, e);
                var X1 = J.Lerp(cx, x1, e);
                var Y0 = J.Lerp(cy, y0, e);
                var Y1 = J.Lerp(cy, y1, e);
                var L = Math.Min(x1 - x0, y1 - y0) * 0.16 + 8;
                var c = d.Accent ? Col(env, "accent") : Col(env, "fg");
                const double w = 2.2;
                env.Line(new[] { new[] { X0, Y0 + L }, new[] { X0, Y0 }, new[] { X0 + L, Y0 } }, c, w, 1);
                env.Line(new[] { new[] { X1 - L, Y0 }, new[] { X1, Y0 }, new[] { X1, Y0 + L } }, c, w, 1);
                env.Line(new[] { new[] { X0, Y1 - L }, new[] { X0, Y1 }, new[] { X0 + L, Y1 } }, c, w, 1);
                env.Line(new[] { new[] { X1 - L, Y1 }, new[] { X1, Y1 }, new[] { X1, Y1 - L } }, c, w, 1);
            },
        });
    }

    private static void RegisterRings()
    {
        Reg.Register("decor", "rings", new DecorDef
        {
            Name = "座標の円",
            Layer = "front",
            Draw = (env, d) =>
            {
                var bb = JLayouts.CenterBb(env, env.Body);
                var H = env.H;
                var s = d.Seed;
                var e = J.E.OutExpo(J.Clamp(env.Lt / 0.5)) * (1 - J.E.InCubic(env.POut));
                if (e <= 0) return;
                var cx = (bb.X0 + bb.X1) / 2;
                var cy = (bb.Y0 + bb.Y1) / 2;
                var R0 = Math.Max(bb.X1 - bb.X0, bb.Y1 - bb.Y0) * 0.55 + H * 0.05;
                for (var k = 0; k < (d.N != 0 ? d.N : 2); k++)
                {
                    var R = R0 * (1 + k * 0.28 + J.R(s, k, 1) * 0.1);
                    var a0 = J.R(s, k, 2) * 360 + env.Ltb * (k % 2 != 0 ? -14 : 10);
                    env.Arc(cx, cy, R, a0, a0 + 360 * e * (0.55 + 0.45 * J.R(s, k, 3)), Col(env, "fg"), 1.2, 0.7, false);
                    var pa = (a0 + 40) * J.DEG;
                    var px = cx + Math.Cos(pa) * R;
                    var py = cy + Math.Sin(pa) * R;
                    env.Circle(px, py, 4, Col(env, "accent"), null, 0, 1, false);
                    env.DrawItem(new JObject
                    {
                        ["text"] = $"X{(long)Math.Round(px, MidpointRounding.AwayFromZero)} Y{(long)Math.Round(py, MidpointRounding.AwayFromZero)}",
                        ["font"] = MonoF(env),
                        ["size"] = J.Clamp(H * 0.015, 10, 18),
                        ["align"] = "left",
                        ["x"] = px + 10,
                        ["y"] = py - 12,
                        ["color"] = Col(env, "sub"),
                        ["alpha"] = e,
                        ["ghost"] = false,
                    });
                }
            },
        });
    }

    private static void RegisterDots()
    {
        Reg.Register("decor", "dots", new DecorDef
        {
            Name = "ドットの輪",
            Layer = "front",
            Draw = (env, d) =>
            {
                var bb = JLayouts.CenterBb(env, env.Body);
                var H = env.H;
                var e = InOut(env);
                if (e <= 0) return;
                var cx = (bb.X0 + bb.X1) / 2;
                var cy = (bb.Y0 + bb.Y1) / 2;
                var R = Math.Max(bb.X1 - bb.X0, bb.Y1 - bb.Y0) * 0.62 + H * 0.04;
                const int m = 36;
                for (var i = 0; i < m * e; i++)
                {
                    var a = (i / (double)m * 360 + env.Ltb * 20) * J.DEG;
                    env.Circle(cx + Math.Cos(a) * R, cy + Math.Sin(a) * R, i % 6 == 0 ? 4 : 2.2,
                        i % 6 == 0 ? Col(env, "accent") : Col(env, "fg"), null, 0, 0.85, false);
                }
            },
        });
    }

    private static void RegisterArrows()
    {
        Reg.Register("decor", "arrows", new DecorDef
        {
            Name = "矢印",
            Layer = "front",
            Draw = (env, d) =>
            {
                var bb = JLayouts.CenterBb(env, env.Body);
                var W = env.W;
                var H = env.H;
                var e = J.E.OutExpo(J.Clamp(env.Lt / 0.4)) * (1 - J.E.InCubic(env.POut));
                if (e <= 0) return;
                var cy = (bb.Y0 + bb.Y1) / 2;
                var s = J.Clamp(H * 0.03, 14, 40);
                var gap = s * 0.9;
                foreach (var side in new[] { -1, 1 })
                {
                    var xEdge = side < 0 ? bb.X0 - s * 1.2 : bb.X1 + s * 1.2;
                    for (var i = 0; i < 3; i++)
                    {
                        var on = (env.Step + i) % 3 != 0;
                        var x = xEdge + side * (i * gap + (1 - e) * W * 0.2);
                        var dd = -side; // 箭头指向文字
                        env.Line(
                            new[]
                            {
                                new[] { x - dd * s * 0.35, cy - s * 0.5 },
                                new[] { x + dd * s * 0.35, cy },
                                new[] { x - dd * s * 0.35, cy + s * 0.5 },
                            },
                            i == 0 ? Col(env, "accent") : Col(env, "fg"), Math.Max(2, s * 0.14), on ? 1 : 0.3, false);
                    }
                }

                if (!d.Big) return;
                var bx = d.Right ? W * 0.9 : W * 0.1;
                var by = H * (d.Low ? 0.82 : 0.2);
                var L = H * 0.1 * e;
                var dx = d.Right ? -1 : 1;
                var dy = d.Low ? -1 : 1;
                env.Line(new[] { new[] { bx, by }, new[] { bx + dx * L, by + dy * L } }, Col(env, "fg"), Math.Max(3, H * 0.008), 1, true);
                env.Line(
                    new[]
                    {
                        new[] { bx + dx * L * 0.45, by + dy * L },
                        new[] { bx + dx * L, by + dy * L },
                        new[] { bx + dx * L, by + dy * L * 0.55 },
                    }, Col(env, "fg"), Math.Max(3, H * 0.008), 1, true);
            },
        });
    }

    private static void RegisterSlash()
    {
        Reg.Register("decor", "slash", new DecorDef
        {
            Name = "スラッシュ",
            Layer = "front",
            Draw = (env, d) =>
            {
                var W = env.W;
                var H = env.H;
                var s = d.Seed;
                for (var k = 0; k < (d.N != 0 ? d.N : 1); k++)
                {
                    var e = J.E.OutExpo(J.Clamp((env.Lt - k * 0.06) / 0.35));
                    if (e <= 0) continue;
                    var ang = J.Rr(-70, -20, s, k, 1) * J.DEG;
                    var cx = J.Rr(W * 0.3, W * 0.7, s, k, 2);
                    var cy = J.Rr(H * 0.3, H * 0.7, s, k, 3);
                    var L = Math.Sqrt((double)W * W + (double)H * H);
                    var x0 = cx - Math.Cos(ang) * L / 2;
                    var y0 = cy - Math.Sin(ang) * L / 2;
                    var t0 = env.POut > 0 ? J.E.InCubic(env.POut) : 0;
                    env.Line(
                        new[]
                        {
                            new[] { x0 + Math.Cos(ang) * L * t0, y0 + Math.Sin(ang) * L * t0 },
                            new[] { x0 + Math.Cos(ang) * L * e, y0 + Math.Sin(ang) * L * e },
                        },
                        k > 0 ? Col(env, "accent") : Col(env, "fg"), k > 0 ? 2 : 1.4, 0.9, true);
                }
            },
        });
    }

    private static void RegisterSparks()
    {
        Reg.Register("decor", "sparks", new DecorDef
        {
            Name = "スパーク",
            Layer = "front",
            Draw = (env, d) =>
            {
                var W = env.W;
                var H = env.H;
                var s = d.Seed;
                for (var k = 0; k < (d.N != 0 ? d.N : 6); k++)
                {
                    var q = J.E.OutBack(J.Clamp((env.Lt - J.R(s, k, 1) * 0.4) / 0.2), 2) * (1 - J.E.InCubic(env.POut));
                    if (q <= 0) continue;
                    var x = J.Rr(W * 0.05, W * 0.95, s, k, 2);
                    var y = J.Rr(H * 0.08, H * 0.92, s, k, 3);
                    var r = J.Rr(H * 0.015, H * 0.04, s, k, 4) * q;
                    var rot = env.Ltb * J.Rs(s, k, 5) * 3 + J.R(s, k, 6) * 3;
                    var arms = J.R(s, k, 7) < 0.5 ? 3 : 4;
                    for (var a = 0; a < arms; a++)
                    {
                        var an = rot + a * Math.PI / arms;
                        env.Line(
                            new[]
                            {
                                new[] { x - Math.Cos(an) * r, y - Math.Sin(an) * r },
                                new[] { x + Math.Cos(an) * r, y + Math.Sin(an) * r },
                            },
                            k % 3 == 0 ? Col(env, "accent") : Col(env, "fg"), Math.Max(1.5, r * 0.14), 1, true);
                    }
                }
            },
        });
    }

    // JS 的 cut._leadLabels：romaji 全量扫描且每帧相同，按 cut 实例缓存。
    // C# 的 Cut（Model.cs）不动，用 ConditionalWeakTable 挂等价缓存。
    private static readonly ConditionalWeakTable<Cut, string[]> LeadLabels = new();

    private static void RegisterLeaders()
    {
        Reg.Register("decor", "leaders", new DecorDef
        {
            Name = "引き出し線",
            Layer = "front",
            Draw = (env, d) =>
            {
                var bb = JLayouts.CenterBb(env, env.Body);
                var W = env.W;
                var H = env.H;
                var s = d.Seed;
                var e = J.E.OutExpo(J.Clamp((env.Lt - 0.1) / 0.45)) * (1 - J.E.InCubic(env.POut));
                if (e <= 0) return;
                // 引线只画右上和左下两条；第三条曾计划挂在 note 上，实际从未绘制
                var cut = env.Cut;
                if (cut == null) return;
                var labels = LeadLabels.GetValue(cut, c => new[]
                {
                    NonEmpty(J.Romaji(Regex.Replace(c.Text, @"\s+", ""))) ?? c.LineText,
                    "No." + (c.Line + 1).ToString(CultureInfo.InvariantCulture).PadLeft(2, '0') + " / " + J.FmtTime(c.Start),
                });
                var fs = J.Clamp(H * 0.018, 11, 20);
                var anchors = new[] { new[] { bb.X1, bb.Y0 }, new[] { bb.X0, bb.Y1 }, new[] { bb.X1, bb.Y1 } };
                for (var k = 0; k < 2; k++)
                {
                    var ax = anchors[k][0];
                    var ay = anchors[k][1];
                    var tx = J.Clamp(ax + (k == 1 ? -1 : 1) * W * J.Rr(0.06, 0.14, s, k, 1), W * 0.06, W * 0.94);
                    var ty = J.Clamp(ay + (k == 0 ? -1 : 1) * H * J.Rr(0.08, 0.16, s, k, 2), H * 0.08, H * 0.92);
                    env.PolyPartial(
                        new[] { new[] { ax, ay }, new[] { tx, ty }, new[] { tx + (k == 1 ? -1 : 1) * W * 0.05, ty } },
                        e, Col(env, "sub"), 1.2, 1, false);
                    env.Circle(ax, ay, 3.5, Col(env, "accent"), null, 0, e, false);
                    env.DrawItem(new JObject
                    {
                        ["text"] = labels[k],
                        ["font"] = k == 0 ? JLayouts.Font0(env.St, "body") : MonoF(env),
                        ["size"] = fs,
                        ["align"] = k == 1 ? "right" : "left",
                        ["x"] = tx + (k == 1 ? -1 : 1) * W * 0.055,
                        ["y"] = ty - fs * 0.9,
                        ["track"] = 0.06,
                        ["color"] = Col(env, "fg"),
                        ["alpha"] = e,
                        ["ghost"] = false,
                    });
                }
            },
        });
    }

    private static void RegisterWaveform()
    {
        Reg.Register("decor", "waveform", new DecorDef
        {
            Name = "波形",
            Layer = "front",
            Draw = (env, d) =>
            {
                var W = env.W;
                var H = env.H;
                var e = InOut(env);
                if (e <= 0) return;
                var y = H * (d.Low ? 0.86 : 0.14);
                const int n = 120;
                var pts = new List<double[]>();
                var en = env.Energy; // JS: env.energy != null ? env.energy : 0.5（C# Env.Energy 由渲染器填充）
                for (var i = 0; i <= n; i++)
                {
                    var u = i / (double)n;
                    var x = J.Lerp(W * 0.18, W * 0.82, u);
                    var env2 = Math.Sin(u * Math.PI);
                    var amp = H * 0.035 * env2 * (0.35 + en) * (0.5 + 0.5 * J.Noise1(u * 18 + env.T * 9, d.Seed));
                    pts.Add(new[] { x, y + (i % 2 != 0 ? amp : -amp) });
                }

                env.PolyPartial(pts.ToArray(), e, Col(env, "fg"), 1.4, 0.9, false);
            },
        });
    }

    private static void RegisterBarcode()
    {
        Reg.Register("decor", "barcode", new DecorDef
        {
            Name = "バーコード",
            Layer = "front",
            Draw = (env, d) =>
            {
                var W = env.W;
                var H = env.H;
                var s = d.Seed;
                var e = InOut(env);
                if (e <= 0) return;
                var x0 = d.Right ? W * 0.84 : W * 0.06;
                var y0 = d.Low ? H * 0.84 : H * 0.07;
                var h = H * 0.05;
                var x = x0;
                for (var i = 0; i < 34; i++)
                {
                    var w = 1 + (int)Math.Floor(J.R(s, i, 1) * 3.2);
                    if (J.R(s, i, 2) < 0.62) env.Rect(x, y0, w * e, h, Col(env, "fg"), 0.9, false);
                    x += w + 1.5;
                }

                env.DrawItem(new JObject
                {
                    ["text"] = (J.H(s, 5) % 1000000000UL).ToString(CultureInfo.InvariantCulture).PadLeft(9, '0'),
                    ["font"] = MonoF(env),
                    ["size"] = J.Clamp(H * 0.014, 9, 16),
                    ["align"] = "left",
                    ["x"] = x0,
                    ["y"] = y0 + h + 12,
                    ["color"] = Col(env, "fg"),
                    ["alpha"] = e * 0.9,
                    ["ghost"] = false,
                    ["track"] = 0.2,
                });
            },
        });
    }

    // ---------------- 背景装饰（back） ----------------

    private static void RegisterGrid()
    {
        Reg.Register("decor", "grid", new DecorDef
        {
            Name = "グリッド",
            Layer = "back",
            Draw = (env, d) =>
            {
                var W = env.W;
                var H = env.H;
                var g = H / (double)(d.N != 0 ? d.N : 8);
                var a = 0.12 * InOut(env);
                if (a <= 0) return;
                for (var x = (W / 2.0) % g; x < W; x += g)
                    env.Line(new[] { new[] { x, 0.0 }, new[] { x, (double)H } }, Col(env, "sub"), 1, a, false);
                for (var y = (H / 2.0) % g; y < H; y += g)
                    env.Line(new[] { new[] { 0.0, y }, new[] { (double)W, y } }, Col(env, "sub"), 1, a, false);
            },
        });
    }

    private static void RegisterStripes()
    {
        Reg.Register("decor", "stripes", new DecorDef
        {
            Name = "ストライプ",
            Layer = "back",
            Draw = (env, d) =>
            {
                var W = env.W;
                var H = env.H;
                var e = InOut(env);
                if (e <= 0) return;
                var w = H * 0.04;
                var cv = env.Ctx; // JS ctx.save/translate/rotate/restore → SKCanvas 同构
                cv.Save();
                cv.Translate((float)(d.Corner ? W * 0.85 : W * 0.15), (float)(d.Corner ? H * 0.15 : H * 0.85));
                cv.RotateDegrees(-35);
                for (var i = -6; i <= 6; i++)
                    env.Rect(i * w * 2 - w / 2 + env.Ltb * 40 % (w * 2), -H * 0.18 * e, w, H * 0.36 * e,
                        d.Accent ? Col(env, "accent") : Col(env, "dim"), 0.9, false);
                cv.Restore();
            },
        });
    }

    private static void RegisterBlobs()
    {
        Reg.Register("decor", "blobs", new DecorDef
        {
            Name = "インクの染み",
            Layer = "back",
            Draw = (env, d) =>
            {
                var W = env.W;
                var H = env.H;
                var s = d.Seed;
                var e = J.E.OutBack(J.Clamp(env.Lt / 0.35), 1.2) * (1 - J.E.InCubic(env.POut));
                if (e <= 0) return;
                for (var k = 0; k < (d.N != 0 ? d.N : 2); k++)
                {
                    var cx = J.Rr(W * 0.12, W * 0.88, s, k, 1);
                    var cy = J.Rr(H * 0.15, H * 0.85, s, k, 2);
                    var R = J.Rr(H * 0.06, H * 0.16, s, k, 3) * e;
                    const int m = 14;
                    var pts = new List<double[]>();
                    for (var i = 0; i < m; i++)
                    {
                        var a = i / (double)m * J.TAU;
                        var r = R * (0.72 + 0.5 * J.R(s, k, i, 4) + 0.08 * Math.Sin(env.Ltb * 3 + i));
                        pts.Add(new[] { cx + Math.Cos(a) * r, cy + Math.Sin(a) * r });
                    }

                    env.Blob(pts.ToArray(), k % 2 != 0 ? Col(env, "accent") : ColOr(env, "accent2", "accent"), 0.95, true);
                }
            },
        });
    }

    private static void RegisterBars()
    {
        Reg.Register("decor", "bars", new DecorDef
        {
            Name = "荒い帯",
            Layer = "back",
            Draw = (env, d) =>
            {
                var W = env.W;
                var H = env.H;
                var s = d.Seed;
                var n = d.N != 0 ? d.N : 3;
                for (var k = 0; k < n; k++)
                {
                    var e = J.E.OutExpo(J.Clamp((env.Lt - k * 0.05) / 0.3)) * (1 - J.E.InExpo(env.POut));
                    if (e <= 0) continue;
                    var y = H * (J.R(s, k, 11) < 0.5 ? J.Rr(0.1, 0.27, s, k, 1) : J.Rr(0.73, 0.9, s, k, 1));
                    var h = H * J.Rr(0.03, 0.08, s, k, 2);
                    var fromL = J.R(s, k, 3) < 0.5;
                    var w = W * J.Rr(0.35, 0.75, s, k, 4) * e;
                    var x0 = fromL ? -10 : W + 10 - w;
                    var pts = new List<double[]>();
                    const int m = 10;
                    for (var i = 0; i <= m; i++)
                        pts.Add(new[] { x0 + w * i / m, y - h / 2 + J.Rs(s, k, i, 5) * h * 0.08 });
                    pts.Add(new[] { x0 + w + J.Rs(s, k, 6) * h * 0.4, y + h * 0.1 });
                    for (var i = m; i >= 0; i--)
                        pts.Add(new[] { x0 + w * i / m, y + h / 2 + J.Rs(s, k, i, 7) * h * 0.08 });
                    env.Poly(pts.ToArray(), k == 0 ? Col(env, "accent") : Col(env, "ink"), 0.92, true);
                }
            },
        });
    }

    private static void RegisterShapes()
    {
        Reg.Register("decor", "shapes", new DecorDef
        {
            Name = "図形",
            Layer = "back",
            Draw = (env, d) =>
            {
                var W = env.W;
                var H = env.H;
                var s = d.Seed;
                var n = d.N != 0 ? d.N : 5;
                var cv = env.Ctx; // JS ctx.save/translate/rotate/restore → SKCanvas 同构
                var cols = new[] { Col(env, "accent"), ColOr(env, "accent2", "fg"), Col(env, "ink"), Col(env, "fg") };
                for (var k = 0; k < n; k++)
                {
                    var q = J.E.OutBack(J.Clamp((env.Lt - J.R(s, k, 9) * 0.3) / 0.25), 1.8) * (1 - J.E.InCubic(env.POut));
                    if (q <= 0) continue;
                    var type = ShapeTypes[(int)Math.Floor(J.R(s, k, 1) * 7)];
                    var top = J.R(s, k, 4) < 0.5;
                    var x = J.Rr(W * 0.05, W * 0.95, s, k, 2) + env.Ltb * J.Rs(s, k, 3) * 30;
                    var y = (top ? J.Rr(H * 0.06, H * 0.24, s, k, 10) : J.Rr(H * 0.76, H * 0.94, s, k, 10)) + env.Ltb * J.Rs(s, k, 5) * 20;
                    var r = J.Rr(H * 0.018, H * 0.06, s, k, 6) * q;
                    var rotDeg = J.R(s, k, 7) * 360 + env.Ltb * J.Rs(s, k, 8) * 60; // JS 乘 J.DEG 转 rad 后 ctx.rotate；RotateDegrees 直接收度
                    var col = cols[k % 4];
                    cv.Save();
                    cv.Translate((float)x, (float)y);
                    cv.RotateDegrees((float)rotDeg);
                    switch (type)
                    {
                        case "circle":
                            env.Circle(0, 0, r, col, null, 0, 1, true);
                            break;
                        case "ring":
                            env.Circle(0, 0, r, null, col, Math.Max(2, r * 0.12), 1, true);
                            break;
                        case "square":
                            env.Rect(-r, -r, r * 2, r * 2, col, 1, true);
                            break;
                        case "tri":
                            env.Poly(new[] { new[] { 0.0, -r }, new[] { r * 0.9, r * 0.6 }, new[] { -r * 0.9, r * 0.6 } }, col, 1, true);
                            break;
                        default: // halftone
                            var dd = r / 3.2;
                            for (var i = -3; i <= 3; i++)
                                for (var j = -3; j <= 3; j++)
                                {
                                    var rr = dd * 0.45 * (1 - (Math.Abs(i) + Math.Abs(j)) / 8.0);
                                    if (rr > 0.5) env.Circle(i * dd, j * dd, rr, col, null, 0, 1, true);
                                }

                            break;
                    }

                    cv.Restore();
                }
            },
        });
    }

    private static void RegisterCounter()
    {
        Reg.Register("decor", "counter", new DecorDef
        {
            Name = "大きな数字",
            Layer = "back",
            Draw = (env, d) =>
            {
                var W = env.W;
                var H = env.H;
                var a = InOut(env);
                if (a <= 0) return;
                string num;
                if (d.Mode == "count")
                {
                    var dur = env.Cut?.Dur ?? 0;
                    num = ((long)Math.Floor(J.Lerp(d.From, d.To,
                        J.E.OutCubic(J.Clamp(env.Lt / (dur * 0.8)))))).ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    num = ((env.Cut?.Index ?? 0) + 1).ToString(CultureInfo.InvariantCulture).PadLeft(2, '0');
                }

                env.DrawItem(new JObject
                {
                    ["text"] = num,
                    ["font"] = JLayouts.Font0(env.St, "display"),
                    ["size"] = H * 0.5,
                    ["x"] = d.Right ? W * 0.86 : W * 0.14,
                    ["y"] = H * (d.Low ? 0.72 : 0.3),
                    ["color"] = d.Accent ? Col(env, "accent") : Col(env, "dim"),
                    ["alpha"] = a * (d.Accent ? 0.9 : 1),
                    ["ghost"] = false,
                });
            },
        });
    }

    // ---------------- HUD（JS J.drawHUD，渲染器按 fx.hud 调用） ----------------

    public static void DrawHud(Env env, Plan plan)
    {
        var W = env.W;
        var H = env.H;
        var m = Math.Round(H * 0.045, MidpointRounding.AwayFromZero);
        var L = H * 0.035;
        var c = Col(env, "sub");
        var fs = J.Clamp(H * 0.016, 10, 18);
        var mono = MonoF(env);
        const double lw = 1.4;
        env.Line(new[] { new[] { m, m + L }, new[] { m, m }, new[] { m + L, m } }, c, lw, 0.9, false);
        env.Line(new[] { new[] { W - m - L, m }, new[] { W - m, m }, new[] { W - m, m + L } }, c, lw, 0.9, false);
        env.Line(new[] { new[] { m, H - m - L }, new[] { m, H - m }, new[] { m + L, H - m } }, c, lw, 0.9, false);
        env.Line(new[] { new[] { W - m - L, H - m }, new[] { W - m, H - m }, new[] { W - m, H - m - L } }, c, lw, 0.9, false);
        var title = (string.IsNullOrEmpty(plan.Title) ? "UNTITLED" : plan.Title)
                    + (!string.IsNullOrEmpty(plan.Artist) ? " / " + plan.Artist : "");
        env.DrawItem(new JObject
        {
            ["text"] = title,
            ["font"] = JLayouts.Font0(env.St, "body"),
            ["size"] = fs,
            ["align"] = "left",
            ["x"] = m + L * 0.6,
            ["y"] = m + L * 0.9,
            ["color"] = c,
            ["track"] = 0.12,
            ["ghost"] = false,
        });
        var rec = env.Step % 4 < 2;
        if (rec) env.Circle(W - m - L * 2.6, m + L * 0.9, fs * 0.32, Col(env, "accent"), null, 0, 1, false);
        env.DrawItem(new JObject
        {
            ["text"] = "REC",
            ["font"] = mono,
            ["size"] = fs,
            ["align"] = "left",
            ["x"] = W - m - L * 2.2,
            ["y"] = m + L * 0.9,
            ["color"] = c,
            ["ghost"] = false,
            ["track"] = 0.1,
        });
        env.DrawItem(new JObject
        {
            ["text"] = J.FmtTime(env.T, plan.Fps),
            ["font"] = mono,
            ["size"] = fs,
            ["align"] = "left",
            ["x"] = m + L * 0.6,
            ["y"] = H - m - L * 0.9,
            ["color"] = c,
            ["ghost"] = false,
            ["track"] = 0.1,
        });
        var li = env.Cut != null ? env.Cut.Line + 1 : 0;
        env.DrawItem(new JObject
        {
            ["text"] = $"LYRIC {li.ToString(CultureInfo.InvariantCulture).PadLeft(2, '0')}/{plan.Lines.Count.ToString(CultureInfo.InvariantCulture).PadLeft(2, '0')}",
            ["font"] = mono,
            ["size"] = fs,
            ["align"] = "right",
            ["x"] = W - m - L * 0.6,
            ["y"] = H - m - L * 0.9,
            ["color"] = c,
            ["ghost"] = false,
            ["track"] = 0.1,
        });
        var u = plan.Duration > 0 ? J.Clamp(env.T / plan.Duration) : 0;
        env.Line(new[] { new[] { W * 0.3, H - m - L * 0.9 }, new[] { W * 0.7, H - m - L * 0.9 } }, c, 1, 0.35, false);
        env.Line(new[] { new[] { W * 0.3, H - m - L * 0.9 }, new[] { J.Lerp(W * 0.3, W * 0.7, u), H - m - L * 0.9 } }, Col(env, "accent"), 2, 0.9, false);
    }
}
