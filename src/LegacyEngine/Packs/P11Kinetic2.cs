
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace JizuraLive.LegacyEngine;

public static class P11Kinetic2
{
    private const string P = "kinetic2";
    private static int _installed;

        private static void Reg0(string g, string key, MoveDef def)
    {
        def.Set = "kinetic";         Reg.Register(g, key, def, P);
    }

    private static string Strip(string? t) => Regex.Replace(t ?? "", @"\s+", "");
    private static bool HasLatin(string? t) => Regex.IsMatch(t ?? "", "[A-Za-z]");
    private static bool IsBlank(string ch) => ch is " " or "　";

    private static double BellK(double k) => Math.Sin(Math.PI * J.Clamp(k));

    private static double[] RotV(double x, double y, double a)
    {
        var c = Math.Cos(a * J.DEG);
        var s = Math.Sin(a * J.DEG);
        return new[] { x * c - y * s, x * s + y * c };
    }

        private static double Kick(double t, double k = 9, double f = 26) =>
        t <= 0 ? 0 : Math.Exp(-k * t) * Math.Sin(f * t);

    private static double BounceE(double x)
    {
        const double n1 = 7.5625, d1 = 2.75;
        if (x < 1 / d1) return n1 * x * x;
        if (x < 2 / d1) return n1 * (x -= 1.5 / d1) * x + 0.75;
        if (x < 2.5 / d1) return n1 * (x -= 2.25 / d1) * x + 0.9375;
        return n1 * (x -= 2.625 / d1) * x + 0.984375;
    }

        private static double Stg(double p, double k, double n, double spread) =>
        J.Clamp((p - (n > 1 ? k / (n - 1) : 0) * spread) / (1 - spread));

        private static void Glyphs(Env env, JAnim.CharFn fn) => env.CharFns.Add(fn);

    private static double Mot(Env env) => env.Fx.Motion; 
    private static double BeatSince(Env env, double per)
    {
        var b = BeatOf(env);
        return b.Has && b.Len > 0.15 ? b.Since : ((env.Ltb % per) + per) % per;
    }

    private static int BeatIdx(Env env, double per)
    {
        var b = BeatOf(env);
        return b.Has && b.Len > 0.15 ? b.Index : (int)Math.Floor(env.Ltb / per);
    }

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

    private static CharXf Hide() => new() { Hide = true };

        private static double Sx1(JObject it) => JAnim.NumOr1(it, "sx");
    private static double Sy1(JObject it) => JAnim.NumOr1(it, "sy");

    private sealed class WBox
    {
        public double X0 = 1e9, X1 = -1e9, Y0 = 1e9, Y1 = -1e9;
        public int N, Li;
        public double Cx, Cy;
    }

    private sealed class Geo
    {
        public JText.TextLayout L1 = null!;
        public int[] Map = Array.Empty<int>();
        public double[] InW = Array.Empty<double>();
        public int NW;
        public WBox[] Wb = Array.Empty<WBox>();
        public double Bx0, Bx1, By0, By1, Bcx, Bcy;
        public bool Vert;
        public List<KeyValuePair<int, List<int>>>? Lw;     }

    private static readonly Dictionary<string, Geo> GeoCache = new();

    private static (List<string> Ws, int Off) WordsFor(Env env, string text)
    {
        var lat = HasLatin(text);
        var ws = (env.Cut?.Words ?? Array.Empty<string>())
            .Select(Strip).Where(s => s.Length > 0).ToList();
        var flatS = Strip(text);
        var off = ws.Count > 0 ? string.Join("", ws).IndexOf(flatS, StringComparison.Ordinal) : -1;
        if (off < 0)
        {
            ws = (lat
                    ? Regex.Split(text, @"\s+")
                    : Planner.ChunkText(text.Replace("\n", "")).ToArray())
                .Select(Strip).Where(s => s.Length > 0).ToList();
            off = 0;
        }
        return (ws, off);
    }

    private static Geo Wgeo(Env env, JObject it)
    {
        var text = it["text"]?.Value<string>() ?? "";
        var key = string.Join("\u0002",
            text,
            JLayouts.S(it, "font") ?? "",
            JAnim.Num(it, "track"),
            JAnim.Num(it, "lead"),
            JLayouts.Truthy(it["vertical"]) ? 1 : 0,
            JLayouts.S(it, "align") ?? "",
            Sx1(it),
            Sy1(it),
            JText.Typeset ? 1 : 0,
            string.Join("\u0001", env.Cut?.Words ?? Array.Empty<string>()));
        if (GeoCache.TryGetValue(key, out var hit)) return hit;

        var probe = (JObject)it.DeepClone();         probe["size"] = 1;
        probe.Remove("_lay");
        probe.Remove("_m");
        var l1 = JText.LayoutText(probe);
        var (ws, off) = WordsFor(env, text);
        var bnd = new List<int>();
        var acc = 0;
        foreach (var w in ws)
        {
            acc += JLayouts.CpLen(w);
            bnd.Add(acc);
        }
        var map = new int[l1.Cells.Count];
        var inW = new double[l1.Cells.Count];
        Array.Fill(map, -1);
        int k = 0, jmin = int.MaxValue, jmax = -1;
        foreach (var g in l1.Cells)
        {
            if (IsBlank(g.Ch)) continue;
            var pos = off + k;
            var j = bnd.FindIndex(b => pos < b);
            if (j < 0) j = Math.Max(0, bnd.Count - 1);
            map[g.I] = j;
            jmin = Math.Min(jmin, j);
            jmax = Math.Max(jmax, j);
            k++;
        }
        if (jmax < 0)
        {
            jmin = 0;
            jmax = 0;
        }
        var nW = jmax - jmin + 1;
        var sx = Sx1(it);
        var sy = Sy1(it);
        var wb = new WBox[nW];
        for (var j = 0; j < nW; j++) wb[j] = new WBox();
        foreach (var g in l1.Cells)
        {
            if (map[g.I] < 0) continue;
            var j = map[g.I] - jmin;
            map[g.I] = j;
            var b = wb[j];
            var x = (g.X + g.Vx) * sx;
            var y = (g.Y + g.Vy) * sy;
            if (b.N == 0) b.Li = g.Li;
            inW[g.I] = b.N++;
            b.X0 = Math.Min(b.X0, x - g.W * sx / 2);
            b.X1 = Math.Max(b.X1, x + g.W * sx / 2);
            b.Y0 = Math.Min(b.Y0, y - g.H * sy / 2);
            b.Y1 = Math.Max(b.Y1, y + g.H * sy / 2);
        }
        foreach (var b in wb)
        {
            if (b.N == 0) b.X0 = b.X1 = b.Y0 = b.Y1 = 0;
            b.Cx = (b.X0 + b.X1) / 2;
            b.Cy = (b.Y0 + b.Y1) / 2;
        }
        double x0 = 1e9, x1 = -1e9, y0 = 1e9, y1 = -1e9;
        foreach (var b in wb)
        {
            if (b.N == 0) continue;
            x0 = Math.Min(x0, b.X0);
            x1 = Math.Max(x1, b.X1);
            y0 = Math.Min(y0, b.Y0);
            y1 = Math.Max(y1, b.Y1);
        }
        if (x0 > x1) x0 = x1 = y0 = y1 = 0;
        var geo = new Geo
        {
            L1 = l1,
            Map = map,
            InW = inW,
            NW = nW,
            Wb = wb,
            Bx0 = x0,
            Bx1 = x1,
            By0 = y0,
            By1 = y1,
            Bcx = (x0 + x1) / 2,
            Bcy = (y0 + y1) / 2,
            Vert = JLayouts.Truthy(it["vertical"]),
        };
        if (GeoCache.Count > 300) GeoCache.Clear();
        GeoCache[key] = geo;
        return geo;
    }

        private static List<KeyValuePair<int, List<int>>> LineWords(Geo g)
    {
        if (g.Lw != null) return g.Lw;
        var m = new SortedDictionary<int, List<int>>();
        for (var j = 0; j < g.NW; j++)
        {
            var li = g.Wb[j].Li;
            if (!m.TryGetValue(li, out var list)) m[li] = list = new List<int>();
            list.Add(j);
        }
        return g.Lw = m.Select(kv => kv).ToList();
    }

        private static double KOf(Geo g, JObject it, JObject? gl)
    {
        if (gl == null) return JAnim.Num(it, "size");
        var gi = (int)JLayouts.D(gl, "i");
        var r = gi >= 0 && gi < g.L1.Cells.Count ? g.L1.Cells[gi] : null;
        var gw = JLayouts.D(gl, "w");
        return r != null && r.W > 1e-6 ? gw / r.W : JAnim.Num(it, "size");
    }

        private static double[] Gpos(JObject? gl, JObject it) => gl is null
        ? [0.0, 0.0]
        : new[]
        {
            (JLayouts.D(gl, "x") + JLayouts.D(gl, "vx")) * Sx1(it),
            (JLayouts.D(gl, "y") + JLayouts.D(gl, "vy")) * Sy1(it),
        };

    private static double Win(double dur) => J.Clamp(dur * 0.45, 0.32, 1.0);
    private static double Wout(double dur) => J.Clamp(dur * 0.32, 0.3, 0.75);

        public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;
        KnWordSlam();             KnTypeToSlam();           KnReplaceIn();            KnHingeDrop();            KnLoopIn();               KnPushIn();               KnInertia();              KnWordSpin();             KnDiveIn();               KnStretchOut();           KnWordKick();             KnPushOut();              KnDiveGlyph();            KnLaunch();               KnWordBlink();            KnCloseGap();             KnJumpCutOut();           KnStackAway();            KnWordPulse();            KnCounterRock();          KnWordRide();             KnTickShift();            KnBeatLean();             KnGapBreath();        }

    private static void KnWordSlam() => Reg0("enter", "knWordSlam", new MoveDef
    {
        Name = "語ごとスラム",
        Tags = new[] { "pop", "graphic", "glitch" },
        W = 1.1,
        MinDur = 0.5,
        InDur = (dur, _) => Win(dur),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var g0 = Wgeo(env, it);
            var n = g0.NW;
            var sp = n > 1 ? 0.62 : 0;
            var secs = env.Cut!.InDur * (1 - sp);
            double Land(int j) => Stg(p, j, n, sp);
            Glyphs(env, (i, gl, _) =>
            {
                var j = g0.Map[i];
                if (j < 0) return null;
                var q = Land(j);
                if (q <= 0) return Hide();
                var k = KOf(g0, it, gl);
                var b = g0.Wb[j];
                var gp = Gpos(gl!, it);
                var gx = gp[0];
                var gy = gp[1];
                var e = J.E.OutExpo(J.Clamp(q * 1.35));
                var s = 1 + 2.1 * (1 - e);
                double dy = 0;
                for (var j2 = n - 1; j2 > j; j2--)
                {
                    var q2 = Land(j2);
                    if (q2 >= 0.74)
                    {
                        dy = JAnim.Num(it, "size") * 0.09 * Kick((q2 - 0.74) * secs, 11, 30);
                        break;
                    }
                }
                dy *= J.Clamp((1 - p) * 10);
                return new CharXf
                {
                    S = s,
                    Dx = (gx - b.Cx * k) * (s - 1),
                    Dy = (gy - b.Cy * k) * (s - 1) + dy,
                    A = J.Clamp(q * 7),
                    Rot = (1 - e) * (j % 2 != 0 ? 7 : -7),
                };
            });
        },
    });

    private static void KnTypeToSlam() => Reg0("enter", "knTypeToSlam", new MoveDef
    {
        Name = "打鍵→拡大",
        Tags = new[] { "pop", "editorial", "graphic" },
        W = 0.9,
        MinDur = 0.55,
        InDur = (dur, _) => Win(dur),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var g0 = Wgeo(env, it);
            var n = g0.NW;
            var sp = n > 1 ? 0.55 : 0;
            Glyphs(env, (i, gl, _) =>
            {
                var j = g0.Map[i];
                if (j < 0) return null;
                var q = Stg(p, j, n, sp);
                if (q <= 0) return Hide();
                var b = g0.Wb[j];
                var cnt = b.N;
                var k = KOf(g0, it, gl);
                var gp = Gpos(gl!, it);
                var gx = gp[0];
                var gy = gp[1];
                const double tq = 0.55;
                if (q < tq && g0.InW[i] >= Math.Floor(q / tq * cnt + 1e-6) + 1) return Hide();
                var e = q < tq ? 0 : J.E.OutBack(J.Clamp((q - tq) / (1 - tq)), 2.2);
                var s = J.Lerp(0.42, 1, e);
                var ax = g0.Vert ? b.Cx * k : b.X0 * k;
                var ay = g0.Vert ? b.Y0 * k : b.Cy * k;
                return new CharXf { S = s, Dx = (gx - ax) * (s - 1), Dy = (gy - ay) * (s - 1), A = q < tq ? 0.85 : 1 };
            });
        },
    });

    private static void KnReplaceIn() => Reg0("enter", "knReplaceIn", new MoveDef
    {
        Name = "入れ替わり登場",
        Tags = new[] { "pop", "graphic", "glitch" },
        W = 0.9,
        MinDur = 0.7,
        InDur = (dur, _) => J.Clamp(dur * 0.5, 0.45, 1.2),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var g0 = Wgeo(env, it);
            var n = g0.NW;
            var t = n > 1 ? 0.7 : 0.2;
            var bw = Math.Max(1e-3, g0.Vert ? g0.By1 - g0.By0 : g0.Bx1 - g0.Bx0);
            var fly = J.E.OutExpo(J.Clamp((p - t) / (1 - t)));
            Glyphs(env, (i, gl, _) =>
            {
                var j = g0.Map[i];
                if (j < 0) return null;
                var b = g0.Wb[j];
                var k = KOf(g0, it, gl);
                var gp = Gpos(gl!, it);
                var gx = gp[0];
                var gy = gp[1];
                var ww = Math.Max(1e-3, g0.Vert ? b.Y1 - b.Y0 : b.X1 - b.X0);
                var big = Math.Min(2.4, Math.Max(1, bw / ww * 0.8));
                var w0 = j / (double)n * t;
                var w1 = (j + 1) / (double)n * t;
                double s, cx, cy, a = 1;
                if (p < t)
                {
                    if (p < w0 || p >= w1) return Hide();
                    var q = (p - w0) / (w1 - w0);
                    s = big * (1 + 0.35 * Math.Exp(-q * 9));
                    cx = g0.Bcx * k;
                    cy = g0.Bcy * k;
                    a = J.Clamp(q * 8);
                }
                else
                {
                    var last = j == n - 1;
                    var s0 = last ? big : 0.3;
                    s = J.Lerp(s0, 1, fly);
                    cx = J.Lerp(g0.Bcx, b.Cx, fly) * k;
                    cy = J.Lerp(g0.Bcy, b.Cy, fly) * k;
                    a = last ? 1 : J.Clamp(fly * 3);
                }
                return new CharXf { S = s, Dx = cx + (gx - b.Cx * k) * s - gx, Dy = cy + (gy - b.Cy * k) * s - gy, A = a };
            });
        },
    });

    private static void KnHingeDrop() => Reg0("enter", "knHingeDrop", new MoveDef
    {
        Name = "蝶番おろし",
        Tags = new[] { "pop", "graphic" },
        W = 0.9,
        MinDur = 0.5,
        InDur = (dur, _) => Win(dur),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var g0 = Wgeo(env, it);
            var n = g0.NW;
            var sp = n > 1 ? 0.5 : 0;
            var dir = (J.H(unchecked((int)env.Cut!.Seed), (int)JAnim.Num(it, "mi"), 61) & 1) != 0 ? 1 : -1;
            Glyphs(env, (i, gl, _) =>
            {
                var j = g0.Map[i];
                if (j < 0) return null;
                var q = Stg(p, j, n, sp);
                if (q <= 0) return Hide();
                var b = g0.Wb[j];
                var k = KOf(g0, it, gl);
                var gp = Gpos(gl!, it);
                var gx = gp[0];
                var gy = gp[1];
                var th = -88 * dir * (1 - BounceE(q));
                var px = (dir > 0 ? b.X0 : b.X1) * k;
                var py = b.Y1 * k;
                var rv = RotV(gx - px, gy - py, th);
                return new CharXf { Dx = px + rv[0] - gx, Dy = py + rv[1] - gy, Rot = th, A = J.Clamp(q * 6) };
            });
        },
    });

    private static void KnLoopIn() => Reg0("enter", "knLoopIn", new MoveDef
    {
        Name = "ループ入り",
        Tags = new[] { "pop", "graphic" },
        W = 0.8,
        MinDur = 0.5,
        InDur = (dur, _) => J.Clamp(dur * 0.5, 0.4, 1.1),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var vert = JLayouts.Truthy(it["vertical"]);
            var dir = (J.H(unchecked((int)env.Cut!.Seed), (int)JAnim.Num(it, "mi"), 63) & 1) != 0 ? 1 : -1;
            var d = Math.Min(env.W, env.H) * 0.55;
            var r = d / J.TAU * 1.5;
            Glyphs(env, (i, _, n) =>
            {
                var q = Stg(p, dir > 0 ? i : n - 1 - i, n, 0.5);
                if (q <= 0) return Hide();
                var v = 1 - J.E.OutCubic(q);
                var ph = J.TAU * v;
                var a0 = v * d - r * Math.Sin(ph);
                var b0 = -r * (1 - Math.Cos(ph));
                var ta = d - J.TAU * r * Math.Cos(ph);
                var tb = -J.TAU * r * Math.Sin(ph);
                var ang = Math.Atan2(tb, ta * dir) / J.DEG * Math.Min(1, v * 4);
                return vert
                    ? new CharXf { Dx = b0, Dy = -a0 * dir, Rot = ang, A = J.Clamp(q * 6) }
                    : new CharXf { Dx = a0 * dir, Dy = b0, Rot = ang, A = J.Clamp(q * 6) };
            });
        },
    });

    private static void KnPushIn() => Reg0("enter", "knPushIn", new MoveDef
    {
        Name = "押し込み",
        Tags = new[] { "pop", "editorial", "graphic" },
        W = 1,
        MinDur = 0.45,
        InDur = (dur, _) => Win(dur),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var g0 = Wgeo(env, it);
            var n = g0.NW;
            var vert = g0.Vert;
            var f = p * n;
            var k0 = Math.Min(n - 1, (int)Math.Floor(f));
            var e = J.E.OutBack(J.Clamp((f - k0) / 0.75), 1.5);
            var s = new Dictionary<int, double>();
            foreach (var kv in LineWords(g0))
            {
                var js = kv.Value;
                var end = vert ? g0.Wb[js[^1]].Y1 : g0.Wb[js[^1]].X1;
                double Far(int j) => end - (vert ? g0.Wb[j].Y1 : g0.Wb[j].X1);
                var vis = js.Where(j => j <= k0).ToList();
                if (vis.Count == 0) continue;
                var last = vis[^1];
                var prev = vis.Count > 1 ? vis[^2] : -1;
                var v = Far(last);
                if (last == k0)
                    v += prev >= 0
                        ? (Far(prev) - Far(last)) * (1 - e)
                        : (end - (vert ? g0.Wb[last].Y0 : g0.Wb[last].X0)) * 0.6 * (1 - e);
                s[kv.Key] = v;
            }
            Glyphs(env, (i, gl, _) =>
            {
                var j = g0.Map[i];
                if (j < 0) return null;
                if (j > k0) return Hide();
                var k = KOf(g0, it, gl);
                var a = j == k0 ? J.Clamp(e * 3) : 1;
                var v = g0.Wb[j].Li is var li && s.TryGetValue(li, out var vv) ? vv : 0;
                return vert ? new CharXf { Dy = v * k, A = a } : new CharXf { Dx = v * k, A = a };
            });
        },
    });

    private static void KnInertia() => Reg0("enter", "knInertia", new MoveDef
    {
        Name = "急ブレーキ",
        Tags = new[] { "pop", "graphic" },
        W = 0.9,
        MinDur = 0.45,
        InDur = (dur, _) => J.Clamp(dur * 0.4, 0.3, 0.8),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var vert = JLayouts.Truthy(it["vertical"]);
            var dir = (J.H(unchecked((int)env.Cut!.Seed), (int)JAnim.Num(it, "mi"), 67) & 1) != 0 ? 1 : -1;             var d = (vert ? env.H : env.W) * 0.6;
            Glyphs(env, (i, _, n) =>
            {
                var back = n > 1 ? (dir > 0 ? (n - 1 - i) / (double)(n - 1) : i / (double)(n - 1)) : 0;                 var q = J.Clamp((p - back * 0.3) / 0.7);
                var e = J.E.OutBack(q, 2.1);
                var v = (J.E.OutBack(Math.Min(1, q + 0.02), 2.1) - e) / 0.02;
                var off = -dir * d * (1 - e);
                var st = 1 + Math.Min(0.5, Math.Abs(v) * 0.08);
                var sq = q > 0.55 ? 1 - 0.18 * back * BellK((q - 0.55) / 0.45) : 1;
                return vert
                    ? new CharXf { Dy = off, Sy = st * sq, Sx = 1 / st }
                    : new CharXf { Dx = off, Sx = st * sq, Sy = 1 / st };
            });
        },
    });

    private static void KnWordSpin() => Reg0("enter", "knWordSpin", new MoveDef
    {
        Name = "語ごと回転",
        Tags = new[] { "pop", "graphic" },
        W = 0.9,
        InDur = (dur, _) => Win(dur),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var g0 = Wgeo(env, it);
            var n = g0.NW;
            var sp = n > 1 ? 0.5 : 0;
            Glyphs(env, (i, gl, _) =>
            {
                var j = g0.Map[i];
                if (j < 0) return null;
                var q = Stg(p, j, n, sp);
                if (q <= 0) return Hide();
                var b = g0.Wb[j];
                var k = KOf(g0, it, gl);
                var gp = Gpos(gl!, it);
                var gx = gp[0];
                var gy = gp[1];
                var e = J.E.OutBack(q, 1.6);
                var th = (1 - J.E.OutCubic(q)) * 200 * (j % 2 != 0 ? -1 : 1);
                var s = J.Lerp(0.15, 1, e);
                var rv = RotV((gx - b.Cx * k) * s, (gy - b.Cy * k) * s, th);
                return new CharXf
                { Dx = b.Cx * k + rv[0] - gx, Dy = b.Cy * k + rv[1] - gy, Rot = th, S = s, A = J.Clamp(q * 5) };
            });
        },
    });

    private static void KnDiveIn() => Reg0("enter", "knDiveIn", new MoveDef
    {
        Name = "手前から語",
        Tags = new[] { "pop", "emotional", "graphic" },
        W = 0.9,
        InDur = (dur, _) => Win(dur),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var g0 = Wgeo(env, it);
            var n = g0.NW;
            var sp = n > 1 ? 0.55 : 0;
            Glyphs(env, (i, gl, _) =>
            {
                var j = g0.Map[i];
                if (j < 0) return null;
                var q = Stg(p, j, n, sp);
                if (q <= 0) return Hide();
                var b = g0.Wb[j];
                var k = KOf(g0, it, gl);
                var gp = Gpos(gl!, it);
                var gx = gp[0];
                var gy = gp[1];
                var e = J.E.OutCubic(q);
                var s = J.Lerp(5.5, 1, e);
                return new CharXf
                {
                    S = s,
                    Dx = (gx - b.Cx * k) * (s - 1) + (b.Cx - g0.Bcx) * k * (s - 1) * 0.6,
                    Dy = (gy - b.Cy * k) * (s - 1),
                    A = J.Clamp(q * 2.2) * J.Lerp(0.35, 1, e),
                };
            });
        },
    });

    private static void KnStretchOut() => Reg0("enter", "knStretchOut", new MoveDef
    {
        Name = "伸び出し",
        Tags = new[] { "pop", "graphic" },
        W = 0.9,
        InDur = (dur, _) => Win(dur),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var g0 = Wgeo(env, it);
            var n = g0.NW;
            var sp = n > 1 ? 0.55 : 0;
            Glyphs(env, (i, gl, _) =>
            {
                var j = g0.Map[i];
                if (j < 0) return null;
                var q = Stg(p, j, n, sp);
                if (q <= 0) return Hide();
                var b = g0.Wb[j];
                var k = KOf(g0, it, gl);
                var gp = Gpos(gl!, it);
                var gx = gp[0];
                var gy = gp[1];
                var s = q >= 1 ? 1 : Math.Max(0.02, J.E.OutElastic(q));
                return g0.Vert
                    ? new CharXf { Dy = (gy - b.Y0 * k) * (s - 1), Sy = s, A = J.Clamp(q * 8) }
                    : new CharXf { Dx = (gx - b.X0 * k) * (s - 1), Sx = s, A = J.Clamp(q * 8) };
            });
        },
    });

    private static void KnWordKick() => Reg0("exit", "knWordKick", new MoveDef
    {
        Name = "語ごと蹴り出し",
        Tags = new[] { "pop", "graphic" },
        W = 1,
        OutDur = (dur, _) => Wout(dur),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var g0 = Wgeo(env, it);
            var n = g0.NW;
            var sp = n > 1 ? 0.4 : 0;
            var h = env.H;
            Glyphs(env, (i, gl, _) =>
            {
                var j = g0.Map[i];
                if (j < 0) return null;
                var q = Stg(p, j, n, sp);
                if (q <= 0) return null;
                if (q >= 1) return Hide();
                var b = g0.Wb[j];
                var k = KOf(g0, it, gl);
                var gp = Gpos(gl!, it);
                var gx = gp[0];
                var gy = gp[1];
                var up = j % 2 != 0 ? 1 : -1;
                var e = J.E.InCubic(q);
                var th = up * 70 * e;
                var rv = RotV(gx - b.Cx * k, gy - b.Cy * k, th);
                return new CharXf
                {
                    Dx = b.Cx * k + rv[0] - gx + up * JAnim.Num(it, "size") * 0.6 * e,
                    Dy = b.Cy * k + rv[1] - gy + up * h * 0.9 * e - up * JAnim.Num(it, "size") * 0.25 * BellK(q * 2),
                    Rot = th,
                    A = 1 - J.E.InQuad(J.Clamp((q - 0.45) / 0.55)),
                };
            });
        },
    });

    private static void KnPushOut() => Reg0("exit", "knPushOut", new MoveDef
    {
        Name = "押し出し退場",
        Tags = new[] { "editorial", "graphic", "pop" },
        W = 0.9,
        OutDur = (dur, _) => Wout(dur),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var g0 = Wgeo(env, it);
            var vert = g0.Vert;
            double St(int j) => vert ? g0.Wb[j].Y0 : g0.Wb[j].X0;
            double En(int j) => vert ? g0.Wb[j].Y1 : g0.Wb[j].X1;
            var s = new Dictionary<int, double>();
            var start = new Dictionary<int, double>();
            foreach (var kv in LineWords(g0))
            {
                var js = kv.Value;
                var m = js.Count;
                var s0 = St(js[0]);
                var f = p * m;
                var k0 = Math.Min(m - 1, (int)Math.Floor(f));
                var e = J.E.OutBack(J.Clamp((f - k0) / 0.8), 1.6);
                double To(int q) => q >= m ? En(js[m - 1]) - s0 + 0.2 : St(js[q]) - s0;
                s[kv.Key] = J.Lerp(To(k0), To(k0 + 1), e);
                start[kv.Key] = s0;
            }
            Glyphs(env, (i, gl, _) =>
            {
                var j = g0.Map[i];
                if (j < 0) return null;
                var li = g0.Wb[j].Li;
                var sh = s.TryGetValue(li, out var v) ? v : 0;
                var k = KOf(g0, it, gl);
                var gp = Gpos(gl!, it);
                var gx = gp[0];
                var gy = gp[1];
                var pos = (vert ? gy / k : gx / k) - sh;
                var over = (start.TryGetValue(li, out var sv) ? sv : 0) - pos;
                var a = (1 - J.Clamp(over / 0.45)) * (1 - J.Smooth(0.72, 1, p));
                if (a <= 0.01) return Hide();
                return vert ? new CharXf { Dy = -sh * k, A = a } : new CharXf { Dx = -sh * k, A = a };
            });
            if (p > 0.97)
                it["alpha"] = JAnim.NumNull(it, "alpha", 1) * (1 - J.Clamp((p - 0.97) / 0.03));
        },
    });

    private static void KnDiveGlyph() => Reg0("exit", "knDiveGlyph", new MoveDef
    {
        Name = "一字へ突入",
        Tags = new[] { "pop", "emotional", "graphic" },
        W = 0.9,
        OutDur = (dur, _) => J.Clamp(dur * 0.3, 0.3, 0.7),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var probe = (JObject)it.DeepClone();             probe["size"] = 1;
            probe.Remove("_lay");
            probe.Remove("_m");
            var lay = JText.LayoutText(probe).Cells.Where(g => !IsBlank(g.Ch)).ToList();
            if (lay.Count == 0) return;
            var f = lay.FirstOrDefault(g => J.IsKanji(g.Ch[0])) ?? lay[lay.Count / 2];
            var one = lay.Count <= 1;
            var fi = f.I;
            var big = Math.Exp(Math.Pow(p, 1.7) * Math.Log(one ? 3 : 34));
            if (one)
            {
                it["alpha"] = JAnim.NumNull(it, "alpha", 1) * (1 - J.Smooth(0.3, 1, p));
            }
            else
            {
                var e = J.E.InOutCubic(p);
                var fx0 = (f.X + f.Vx) * Sx1(it) * JAnim.Num(it, "size");
                var fy0 = (f.Y + f.Vy) * Sy1(it) * JAnim.Num(it, "size");
                if (JAnim.Num(it, "rot") == 0)
                {
                    it["x"] = JAnim.Num(it, "x") + (env.W / 2.0 - (JAnim.Num(it, "x") + fx0)) * e;
                    it["y"] = JAnim.Num(it, "y") + (env.H / 2.0 - (JAnim.Num(it, "y") + fy0)) * e;
                }
            }
            double? fxF = null, fyF = null;
            Glyphs(env, (i, gl, _) =>
            {
                if (gl == null) return null;                 if (fxF == null)
                {
                    var gi = (int)JLayouts.D(gl, "i");
                    var src = lay.FirstOrDefault(q => q.I == gi) ?? f;
                    var k = JLayouts.D(gl, "w") / Math.Max(1e-6, src.W);
                    fxF = (f.X + f.Vx) * Sx1(it) * k;
                    fyF = (f.Y + f.Vy) * Sy1(it) * k;
                }
                var gp = Gpos(gl, it);
                var gx = gp[0];
                var gy = gp[1];
                var a = i == fi ? 1 - J.Smooth(0.6, 1, p) : 1 - J.Smooth(0.25, 0.7, p);
                if (a <= 0.01) return Hide();
                return new CharXf { S = big, Dx = (gx - fxF.Value) * (big - 1), Dy = (gy - fyF.Value) * (big - 1), A = a };
            });
        },
    });

    private static void KnLaunch() => Reg0("exit", "knLaunch", new MoveDef
    {
        Name = "急発進",
        Tags = new[] { "pop", "graphic" },
        W = 0.9,
        OutDur = (dur, _) => J.Clamp(dur * 0.3, 0.3, 0.65),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var vert = JLayouts.Truthy(it["vertical"]);
            var dir = (J.H(unchecked((int)env.Cut!.Seed), (int)JAnim.Num(it, "mi"), 71) & 1) != 0 ? 1 : -1;
            var d = (vert ? env.H : env.W) * 1.25;
            Glyphs(env, (i, _, n) =>
            {
                var back = n > 1 ? (dir > 0 ? (n - 1 - i) / (double)(n - 1) : i / (double)(n - 1)) : 0;
                var q = J.Clamp((p - back * 0.35) / 0.65);
                var antic = -0.06 * BellK(J.Clamp(p / 0.18));                 var e = J.E.InCubic(q) + antic;
                var v = 3 * q * q;
                var st = 1 + Math.Min(1.2, v * 0.35);
                if (q >= 1) return Hide();
                return vert
                    ? new CharXf { Dy = dir * d * e, Sy = st, Sx = 1 / Math.Sqrt(st) }
                    : new CharXf { Dx = dir * d * e, Sx = st, Sy = 1 / Math.Sqrt(st) };
            });
        },
    });

    private static void KnWordBlink() => Reg0("exit", "knWordBlink", new MoveDef
    {
        Name = "一語ずつ消灯",
        Tags = new[] { "pop", "glitch", "graphic" },
        W = 0.9,
        OutDur = (dur, _) => J.Clamp(dur * 0.35, 0.3, 0.8),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var g0 = Wgeo(env, it);
            var n = g0.NW;
            var rev = (J.H(unchecked((int)env.Cut!.Seed), 73) & 1) == 1;
            var acc = env.Sc!.Str("accent");
            var seed = unchecked((int)env.Cut!.Seed);
            var mi = (int)JAnim.Num(it, "mi");
            Glyphs(env, (i, gl, _) =>
            {
                var j0 = (uint)i < (uint)g0.Map.Length ? g0.Map[i] : -1;
                if (j0 < 0 || (uint)j0 >= (uint)g0.Wb.Length) return null;
                var j = rev ? n - 1 - j0 : j0;
                var w0 = n > 1 ? j / (double)n : J.R(seed, mi, 74) * 0.6;
                var w1 = n > 1 ? (j + 1) / (double)n : w0 + 0.4;
                if (p >= w1 || p >= 0.999) return Hide();
                if (p < w0) return null;
                var q = (p - w0) / (w1 - w0);
                var b = g0.Wb[j0];
                var k = KOf(g0, it, gl);
                var gp = Gpos(gl!, it);
                var s = 1 + 0.16 * BellK(q * 1.4);
                return new CharXf
                {
                    S = s,
                    Dx = (gp[0] - b.Cx * k) * (s - 1),
                    Dy = (gp[1] - b.Cy * k) * (s - 1),
                    Color = q > 0.25 ? acc : null,
                    A = q > 0.7 ? 0.35 : 1,
                };
            });
        },
    });

    private static void KnCloseGap() => Reg0("exit", "knCloseGap", new MoveDef
    {
        Name = "詰めて消える",
        Tags = new[] { "editorial", "graphic", "pop" },
        W = 0.9,
        OutDur = (dur, _) => J.Clamp(dur * 0.35, 0.35, 0.8),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var g0 = Wgeo(env, it);
            var n = g0.NW;
            var vert = g0.Vert;
            double Lo(int j) => vert ? g0.Wb[j].Y0 : g0.Wb[j].X0;
            double Hi(int j) => vert ? g0.Wb[j].Y1 : g0.Wb[j].X1;
            var ord = new List<int>();
            for (int a = 0, b = n - 1; a <= b; a++, b--)
            {
                ord.Add(a);
                if (b != a) ord.Add(b);
            }
            var rank = new int[n];
            for (var r = 0; r < ord.Count; r++) rank[ord[r]] = r;
            var cArr = new double[n];
            for (var j = 0; j < n; j++) cArr[j] = J.E.InOutCubic(J.Clamp(p * n - rank[j]));
            var ctr = new double[n];
            var byLine = new SortedDictionary<int, List<int>>();
            for (var j = 0; j < n; j++)
            {
                var li = g0.Wb[j].Li;
                if (!byLine.TryGetValue(li, out var list)) byLine[li] = list = new List<int>();
                list.Add(j);
            }
            foreach (var js in byLine.Values)
            {
                var m = js.Count;
                var wid = js.Select(j => Hi(j) - Lo(j)).ToArray();
                var gaps = js.Select((j, q) => q < m - 1 ? Lo(js[q + 1]) - Hi(j) : 0).ToArray();
                double Cc(int q) => q < m - 1 ? gaps[q] * (1 - Math.Max(cArr[js[q]], cArr[js[q + 1]])) : 0;
                var tot = 0.0;
                for (var q = 0; q < m; q++) tot += wid[q] * (1 - cArr[js[q]]) + Cc(q);
                var x = (Lo(js[0]) + Hi(js[m - 1])) / 2 - tot / 2;
                for (var q = 0; q < m; q++)
                {
                    var w = wid[q] * (1 - cArr[js[q]]);
                    ctr[js[q]] = x + w / 2;
                    x += w + Cc(q);
                }
            }
            Glyphs(env, (i, gl, _) =>
            {
                var j = g0.Map[i];
                if (j < 0) return null;
                if (cArr[j] >= 0.999) return Hide();
                var k = KOf(g0, it, gl);
                var gp = Gpos(gl!, it);
                var s = 1 - cArr[j];
                var wc = (Lo(j) + Hi(j)) / 2;
                var along = vert ? gp[1] : gp[0];
                var cross = vert ? gp[0] : gp[1];
                var cc = vert ? g0.Wb[j].Cx : g0.Wb[j].Cy;
                var na = ctr[j] * k + (along - wc * k) * s;
                var nc = cc * k + (cross - cc * k) * s;
                return vert
                    ? new CharXf { Dx = nc - gp[0], Dy = na - gp[1], S = Math.Max(0.01, s) }
                    : new CharXf { Dx = na - gp[0], Dy = nc - gp[1], S = Math.Max(0.01, s) };
            });
        },
    });

    private static void KnJumpCutOut() => Reg0("exit", "knJumpCutOut", new MoveDef
    {
        Name = "ジャンプカット",
        Tags = new[] { "pop", "glitch", "graphic" },
        W = 0.8,
        OutDur = (dur, _) => J.Clamp(dur * 0.3, 0.3, 0.6),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var sd = J.H(unchecked((int)env.Cut!.Seed), (int)JAnim.Num(it, "mi"), 79);
            var side = (sd & 1) != 0 ? 1 : -1;
            var st = (int)Math.Floor(J.Clamp(p) * 4);
            if (st >= 3)
            {
                it["alpha"] = 0;
                return;
            }
            var sTab = new[]
            {
                new[] { 1.22, 0.05 * side, -0.02, 0.0 },
                new[] { 0.8, -0.07 * side, 0.03, 0.0 },
                new[] { 1.5, 0.02 * side, 0.0, 3.0 * side },
            };
            var s = sTab[st];
            it["size"] = JAnim.Num(it, "size") * s[0];
            it["x"] = JAnim.Num(it, "x") + s[1] * env.W;
            it["y"] = JAnim.Num(it, "y") + s[2] * env.H;
            it["rot"] = JAnim.Num(it, "rot") + s[3];
        },
    });

    private static void KnStackAway() => Reg0("exit", "knStackAway", new MoveDef
    {
        Name = "積んで落とす",
        Tags = new[] { "pop", "graphic" },
        W = 0.8,
        OutDur = (dur, _) => J.Clamp(dur * 0.4, 0.45, 0.9),
        MinDur = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var g0 = Wgeo(env, it);
            var n = g0.NW;
            const double t = 0.62;
            var h = env.H;
            var lh = g0.Vert ? (double?)null : g0.By1 - g0.By0;
            Glyphs(env, (i, gl, _) =>
            {
                var j = g0.Map[i];
                if (j < 0) return null;
                var b = g0.Wb[j];
                var k = KOf(g0, it, gl);
                var gp = Gpos(gl!, it);
                var q = J.E.InOutCubic(J.Clamp((p * (1 / t) - j / Math.Max(1, n) * 0.7) / 0.3));
                var hh = g0.Vert ? b.X1 - b.X0 : Math.Max(1e-3, b.Y1 - b.Y0);
                var tx = g0.Vert ? g0.Bcx - (j - (n - 1) / 2.0) * hh * 1.05 : g0.Bcx;
                var ty = g0.Vert ? g0.Bcy : g0.Bcy - (j - (n - 1) / 2.0) * hh * 1.05;
                var hop = -BellK(q) * (lh ?? hh) * 0.8;
                var dx = (tx - b.Cx) * k * q;
                var dy = (ty - b.Cy) * k * q + hop * k;
                var fall = J.Clamp((p - t) / (1 - t));
                dy += J.E.InQuad(fall) * h * 1.2;
                var rot = fall * (j % 2 != 0 ? 8 : -8);
                if (fall >= 1) return Hide();
                return new CharXf { Dx = dx, Dy = dy, Rot = rot, A = 1 - J.Smooth(0.8, 1, fall) };
            });
        },
    });

    private static void KnWordPulse() => Reg0("hold", "knWordPulse", new MoveDef
    {
        Name = "語ごとの拍",
        Tags = new[] { "pop", "graphic" },
        W = 1,
        Draw = env =>
        {
            var it = env.It!;
            var amt = env.Ap;
            var g0 = Wgeo(env, it);
            var n = g0.NW;
            if (n < 1) return;
            const double per = 0.52;
            var s0 = BeatSince(env, per);
            var cur = ((BeatIdx(env, per) % n) + n) % n;
            var pulse = Math.Exp(-s0 * 4) * 0.14 * amt * (0.4 + Mot(env));
            if (pulse < 0.002) return;
            Glyphs(env, (i, gl, _) =>
            {
                var j = g0.Map[i];
                if (j != cur) return null;
                var b = g0.Wb[j];
                var k = KOf(g0, it, gl);
                var gp = Gpos(gl!, it);
                var s = 1 + pulse;
                return new CharXf { S = s, Dx = (gp[0] - b.Cx * k) * (s - 1), Dy = (gp[1] - b.Cy * k) * (s - 1) };
            });
        },
    });

    private static void KnCounterRock() => Reg0("hold", "knCounterRock", new MoveDef
    {
        Name = "逆回転ゆれ",
        Tags = new[] { "pop", "calm", "graphic" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var amt = env.Ap;
            var g0 = Wgeo(env, it);
            var n = g0.NW;
            var t = env.Ltb;             var a = 4 * amt * (0.4 + Mot(env)) * Math.Sin(t * J.TAU * 0.55);
            if (Math.Abs(a) < 0.05) return;
            Glyphs(env, (i, gl, _) =>
            {
                var j = g0.Map[i];
                if (j < 0) return null;
                var th = a * (j % 2 != 0 ? -1 : 1) * (n == 1 ? 0.6 : 1);
                var b = g0.Wb[j];
                var k = KOf(g0, it, gl);
                var gp = Gpos(gl!, it);
                var rv = RotV(gp[0] - b.Cx * k, gp[1] - b.Cy * k, th);
                return new CharXf { Dx = b.Cx * k + rv[0] - gp[0], Dy = b.Cy * k + rv[1] - gp[1], Rot = th };
            });
        },
    });

    private static void KnWordRide() => Reg0("hold", "knWordRide", new MoveDef
    {
        Name = "語の波乗り",
        Tags = new[] { "calm", "emotional", "pop" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var amt = env.Ap;
            var g0 = Wgeo(env, it);
            var t = env.Ltb;             var a = JAnim.Num(it, "size") * 0.07 * amt * (0.4 + Mot(env));
            if (a < 0.2) return;
            Glyphs(env, (i, gl, _) =>
            {
                var j = g0.Map[i];
                if (j < 0) return null;
                var b = g0.Wb[j];
                var k = KOf(g0, it, gl);
                var gp = Gpos(gl!, it);
                var ph = t * 3.1 - j * 1.25;
                var off = a * Math.Sin(ph);
                var th = Math.Cos(ph) * 3.5 * amt;
                var rv = RotV(gp[0] - b.Cx * k, gp[1] - b.Cy * k, th);
                return g0.Vert
                    ? new CharXf { Dx = b.Cx * k + rv[0] - gp[0] + off, Dy = b.Cy * k + rv[1] - gp[1], Rot = th }
                    : new CharXf { Dx = b.Cx * k + rv[0] - gp[0], Dy = b.Cy * k + rv[1] - gp[1] + off, Rot = th };
            });
        },
    });

    private static void KnTickShift() => Reg0("hold", "knTickShift", new MoveDef
    {
        Name = "刻みシフト",
        Tags = new[] { "graphic", "pop", "editorial" },
        W = 0.8,
        Draw = env =>
        {
            var it = env.It!;
            var amt = env.Ap;
            const double per = 0.5;
            var s0 = BeatSince(env, per);
            var idx = BeatIdx(env, per);
            var d = JAnim.Num(it, "size") * 0.05 * amt * (0.4 + Mot(env));
            if (d < 0.2) return;
            var from = idx % 2 != 0 ? 1 : -1;
            var e = J.E.OutBack(J.Clamp(s0 / 0.09), 2.6);
            var x = J.Lerp(-from, from, e) * d;
            if (JLayouts.Truthy(it["vertical"])) it["y"] = JAnim.Num(it, "y") + x;
            else it["x"] = JAnim.Num(it, "x") + x;
        },
    });

    private static void KnBeatLean() => Reg0("hold", "knBeatLean", new MoveDef
    {
        Name = "拍で傾く",
        Tags = new[] { "pop", "glitch", "graphic" },
        W = 0.8,
        Draw = env =>
        {
            var it = env.It!;
            var amt = env.Ap;
            var g0 = Wgeo(env, it);
            const double per = 0.55;
            var s0 = BeatSince(env, per);
            var idx = BeatIdx(env, per);
            var a = 18 * amt * (0.4 + Mot(env)) * Math.Exp(-s0 * 4.5) * Math.Cos(s0 * 15);
            if (Math.Abs(a) < 0.1) return;
            Glyphs(env, (i, _, _) =>
            {
                var j = g0.Map[i];
                if (j < 0) return null;
                return new CharXf { Skew = a * ((j + idx) % 2 != 0 ? 1 : -1) };
            });
        },
    });

    private static void KnGapBreath() => Reg0("hold", "knGapBreath", new MoveDef
    {
        Name = "語間の呼吸",
        Tags = new[] { "calm", "editorial", "emotional" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var amt = env.Ap;
            var g0 = Wgeo(env, it);
            var n = g0.NW;
            var t = env.Ltb;             if (n < 2) return;
            var a = JAnim.Num(it, "size") * 0.16 * amt * (0.4 + Mot(env)) * (0.5 - 0.5 * Math.Cos(t * J.TAU * 0.4));
            if (a < 0.2) return;
            Glyphs(env, (i, _, _) =>
            {
                var j = g0.Map[i];
                if (j < 0) return null;
                var o = (j - (n - 1) / 2.0) * a;
                return g0.Vert ? new CharXf { Dy = o } : new CharXf { Dx = o };
            });
        },
    });
}
