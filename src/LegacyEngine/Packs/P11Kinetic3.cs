
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace JizuraLive.LegacyEngine;

public static class P11Kinetic3
{
    private const string P = "kinetic3";
    private static int _installed;

        private static void Reg0(string g, string key, object def)
    {
        switch (def)
        {
            case CamDef d: d.Set = "kinetic"; Reg.Register(g, key, d, P); break;
            case TransDef d: d.Set = "kinetic"; Reg.Register(g, key, d, P); break;
            case TreatDef d: d.Set = "kinetic"; Reg.Register(g, key, d, P); break;
            case DecorDef d: d.Set = "kinetic"; Reg.Register(g, key, d, P); break;
        }
    }

    private static string Strip(string? t) => System.Text.RegularExpressions.Regex.Replace(t ?? "", @"\s+", "");
    private static bool IsBlank(string ch) => ch is " " or "　";
    private static double BellK(double k) => Math.Sin(Math.PI * J.Clamp(k));

    private static double Km(Env env) => J.Clamp((env.Fx.Motion) * 1.25, 0, 1.25);

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

    private sealed class WordTimesEntry
    {
        public int NMax;
        public double[] T = System.Array.Empty<double>();
    }

    private static readonly ConditionalWeakTable<Cut, WordTimesEntry> ClockCache = new();

    private static double[] WordTimes(Env env, int nMax = 6)
    {
        var c = env.Cut!;
        if (ClockCache.TryGetValue(c, out var v) && v.NMax == nMax) return v.T;
        var n = Math.Max(1, Math.Min(nMax, c.Words.Length > 0 ? c.Words.Length : 1));
        if (n < 2 && c.Dur > 1.2) n = 2;
        var dur = c.Dur;
        var last = Math.Max(0, Math.Min(dur * 0.5, (n - 1) * 0.38));
        var t = new List<double>();
        for (var i = 0; i < n; i++) t.Add(n > 1 ? last * i / (n - 1) : 0);
        var beats = env.Plan.Beats;
        if (beats is { Length: > 0 } && n > 1)
        {
            var pick = new List<double>();
            double prev = 0;
            foreach (var b in beats)
            {
                var r = b - c.Start;
                if (r <= 0.12) continue;
                if (r > dur * 0.72) break;
                if (r - prev >= 0.2)
                {
                    pick.Add(r);
                    prev = r;
                }
                if (pick.Count >= n - 1) break;
            }
            if (pick.Count >= n - 1 && pick[n - 2] <= Math.Max(last * 1.35, dur * 0.55))
                t = new List<double> { 0 }.Concat(pick).ToList();
        }
        var entry = new WordTimesEntry { NMax = nMax, T = t.ToArray() };
        ClockCache.Remove(c);
        ClockCache.Add(c, entry);
        return entry.T;
    }

    private static int CurIdx(double[] ts, double t)
    {
        var k = -1;
        for (var i = 0; i < ts.Length; i++)
            if (t >= ts[i])
                k = i;
        return k;
    }

    private static double MinD(TransCtx i) => Math.Min(i.Cw, i.Ch);

    private static string TAcc(TransCtx i)
    {
        var acc = i.Sc.Str("accent");
        return acc != null && J.Contrast(acc, i.Sc.Str("bg") ?? "#000") >= 1.6
            ? acc
            : i.Sc.Str("fg") ?? "#FFFFFF";
    }

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

        private static bool Alive(JObject it) =>
        !(it["fill"] is JValue fv && fv.Type == JTokenType.Boolean && !(bool)fv.Value!)
        && JAnim.NumNull(it, "alpha", 1) >= 0.9
        && !string.IsNullOrEmpty(it["text"]?.Value<string>())
        && JAnim.Num(it, "size") > 1;

    private sealed class TGeo
    {
        public JText.TextLayout L1 = null!;
        public int[] Map = System.Array.Empty<int>();
        public int NW;
    }

    private static readonly Dictionary<string, TGeo> TmCache = new();

    private static TGeo TreatGeo(Env env, JObject it)
    {
        var text = it["text"]?.Value<string>() ?? "";
        var key = string.Join("\u0002",
            text,
            JLayouts.S(it, "font") ?? "",
            JAnim.Num(it, "track"),
            JAnim.Num(it, "lead"),
            JLayouts.Truthy(it["vertical"]) ? 1 : 0,
            JLayouts.S(it, "align") ?? "",
            JText.Typeset ? 1 : 0,
            string.Join("\u0001", env.Cut?.Words ?? System.Array.Empty<string>()));
        if (TmCache.TryGetValue(key, out var hit)) return hit;
        var probe = (JObject)it.DeepClone();         probe["size"] = 1;
        probe["sx"] = 1;
        probe["sy"] = 1;
        probe.Remove("_lay");
        probe.Remove("_m");
        var l1 = JText.LayoutText(probe);
        var ws = (env.Cut?.Words ?? System.Array.Empty<string>())
            .Select(Strip).Where(s => s.Length > 0).ToList();
        var off = ws.Count > 0 ? string.Join("", ws).IndexOf(Strip(text), StringComparison.Ordinal) : -1;
        if (off < 0)
        {
            ws = (System.Text.RegularExpressions.Regex.IsMatch(text, "[A-Za-z]")
                    ? System.Text.RegularExpressions.Regex.Split(text, @"\s+")
                    : Planner.ChunkText(text.Replace("\n", "")).ToArray())
                .Select(Strip).Where(s => s.Length > 0).ToList();
            off = 0;
        }
        var bnd = new List<int>();
        var acc = 0;
        foreach (var w in ws)
        {
            acc += JLayouts.CpLen(w);
            bnd.Add(acc);
        }
        var map = new int[l1.Cells.Count];
        Array.Fill(map, -1);
        int k = 0, jmin = int.MaxValue;
        foreach (var g in l1.Cells)
        {
            if (IsBlank(g.Ch)) continue;
            var j = bnd.FindIndex(b => off + k < b);
            if (j < 0) j = bnd.Count - 1;
            map[g.I] = j;
            jmin = Math.Min(jmin, j);
            k++;
        }
        for (var i = 0; i < map.Length; i++)
            if (map[i] >= 0)
                map[i] -= jmin;
        var geo = new TGeo { L1 = l1, Map = map, NW = Math.Max(0, map.Length > 0 ? map.Max() : 0) + 1 };
        if (TmCache.Count > 300) TmCache.Clear();
        TmCache[key] = geo;
        return geo;
    }

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
            if (b.X1 <= b.X0)
            {
                b.X0 = bb.X0;
                b.X1 = bb.X1;
            }
            if (b.Y1 <= b.Y0)
            {
                b.Y0 = bb.Y0;
                b.Y1 = bb.Y1;
            }
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

        public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;
        KnReadPan();              KnTiltKick();             KnCardFlip();             KnShearKick();            KnJumpCut();              KnRushIn();               KnCornerSwing();          KnStutterCut();           KnStripSlam();            KnWordScale();            KnWordPlate();            KnSpeedTrail();           KnWordTicks();        }

    private static void KnReadPan() => Reg0("cam", "knReadPan", new CamDef
    {
        Name = "読み追い",
        Tags = new[] { "pop", "graphic", "editorial" },
        W = 0.8,
        Plan = (rng, _) => new JObject { ["a"] = rng.Range(0.026, 0.036) },
        Get = env =>
        {
            var ts = WordTimes(env);
            var n = ts.Length;
            var k = Km(env);
            var a = JLayouts.D(env.Cut!.CamP, "a", 0.03);
            var amp = env.W * a * k;
            var lt = env.Lt;
            if (n < 2)
                return new CamState
                {
                    S = 1.03,
                    X = amp * (1 - 2 * J.E.InOutSine(J.Clamp(lt / Math.Max(0.4, env.Cut.Dur)))) * 0.5,
                };
            double Pos(int kk) => amp * (1 - 2 * kk / (double)(n - 1));
            var idx = Math.Max(0, CurIdx(ts, lt));
            var e = idx > 0 ? J.E.OutBack(J.Clamp((lt - ts[idx]) / 0.2), 1.8) : 1;
            var x = idx > 0 ? J.Lerp(Pos(idx - 1), Pos(idx), e) : Pos(0);
            var tb = ts[n - 1] + 0.55;
            var rb = J.E.InOutCubic(J.Clamp((lt - tb) / 0.5));
            x *= 1 - rb;
            var whip = idx > 0 ? BellK(J.Clamp((lt - ts[idx]) / 0.14)) : 0;
            var sgn = amp > 0 ? 1 : amp < 0 ? -1 : 0;
            return new CamState
            {
                X = x,
                S = 1.035,
                Skx = -sgn * 2.5 * whip * k,
                Blur = 3 * whip * k,
            };
        }
    });

    private static void KnTiltKick() => Reg0("cam", "knTiltKick", new CamDef
    {
        Name = "語で傾く",
        Tags = new[] { "pop", "graphic", "emotional" },
        W = 0.8,
        Plan = (rng, _) => new JObject
        {
            ["dir"] = rng.Pick(new[] { 1, -1 }),
            ["a"] = rng.Range(2.4, 3.4),
        },
        Get = env =>
        {
            var ts = WordTimes(env);
            var n = ts.Length;
            var k = Math.Min(1, Km(env));
            var lt = env.Lt;
            var pc = env.Cut!.CamP;
            var a = JLayouts.D(pc, "a", 3) * k * (JLayouts.D(pc, "dir") != 0 ? JLayouts.D(pc, "dir") : 1);
            double Ang(int kk) => kk >= n - 1 ? 0 : (kk % 2 != 0 ? -a : a);
            var idx = Math.Max(0, CurIdx(ts, lt));
            var d = lt - ts[idx];
            var from = idx > 0 ? Ang(idx - 1) : 0;
            var to = Ang(idx);
            var sp = Math.Exp(-d * 7) * Math.Cos(d * 17);
            var r = to + (from - to) * sp;
            return new CamState
            {
                Rot = J.Clamp(r, -5, 5),
                S = 1.03 + 0.012 * Math.Abs(r) / 3,
            };
        }
    });

    private static void KnCardFlip() => Reg0("cam", "knCardFlip", new CamDef
    {
        Name = "カード返し",
        Tags = new[] { "pop", "graphic" },
        W = 0.7,
        Strong = true,
        Plan = (rng, _) => new JObject
        {
            ["vert"] = rng.Chance(0.3),
            ["dir"] = rng.Pick(new[] { 1, -1 }),
        },
        Get = env =>
        {
            var ts = WordTimes(env);
            var lt = env.Lt;
            var k = Math.Min(1, Km(env));
            var pc = env.Cut!.CamP;
            var d = JLayouts.D(pc, "dir") != 0 ? JLayouts.D(pc, "dir") : 1;
            var q = J.Clamp(lt / 0.42);
            var th = 90 * (1 - J.E.OutBack(q, 1.7));
            var f = Math.Max(0.04, Math.Abs(Math.Cos(th * J.DEG)));
            for (var i = 1; i < ts.Length; i++)
            {
                var dd = lt - ts[i];
                if (dd > 0 && dd < 0.24) f *= 1 - 0.1 * k * BellK(dd / 0.24);
            }
            var sk = 7 * Math.Sin(th * J.DEG) * d * k;
            if (JLayouts.Truthy(pc["vert"]))
                return new CamState
                {
                    S = 1.01 * f,
                    Y = -env.H * 0.02 * Math.Sin(th * J.DEG),
                };
            return new CamState
            {
                S = 1.01 * f,
                Skx = sk * 0.4,
                X = env.W * 0.02 * Math.Sin(th * J.DEG) * d,
            };
        }
    });

    private static void KnShearKick() => Reg0("cam", "knShearKick", new CamDef
    {
        Name = "シアーキック",
        Tags = new[] { "pop", "glitch", "graphic" },
        W = 0.8,
        Plan = (rng, _) => new JObject { ["a"] = rng.Range(5, 8) },
        Get = env =>
        {
            var ts = WordTimes(env);
            var lt = env.Lt;
            var k = Math.Min(1.1, Km(env));
            double since;
            int idx;
            var beat = BeatOf(env);
            if (beat.Has && beat.Len > 0.2 && lt > ts[^1] + 0.3)
            {
                since = beat.Since;
                idx = beat.Index;
            }
            else
            {
                idx = Math.Max(0, CurIdx(ts, lt));
                since = lt - ts[idx];
            }
            var w = Math.Exp(-since * 8) * Math.Cos(since * 22);
            var sg = idx % 2 != 0 ? 1 : -1;
            return new CamState
            {
                Skx = JLayouts.D(env.Cut!.CamP, "a", 6.5) * k * w * sg,
                X = env.W * 0.006 * k * w * sg,
                S = 1.02,
            };
        }
    });

    private static void KnJumpCut() => Reg0("cam", "knJumpCut", new CamDef
    {
        Name = "ジャンプカット",
        Tags = new[] { "pop", "glitch", "editorial" },
        W = 0.7,
        Plan = (rng, _) => new JObject { ["s0"] = rng.Int(0, 99) },
        Get = env =>
        {
            var ts = WordTimes(env);
            var n = ts.Length;
            var lt = env.Lt;
            var k = Math.Min(1, Km(env));
            var idx = Math.Max(0, CurIdx(ts, lt));
            if (lt > ts[n - 1] + 0.5 || n < 2) return new CamState { S = 1.02 };
            var sd = unchecked((int)env.Cut!.Seed) + (int)JLayouts.D(env.Cut.CamP, "s0");
            var tab = new[] { 1.0, 1.12, 1.05, 1.14, 1.08, 1.13 };
            var s = tab[((idx + sd % 3) % 6 + 6) % 6];
            var sg = idx % 2 != 0 ? 1 : -1;
            return new CamState
            {
                S = 1 + (s - 1) * k,
                X = sg * (0.5 + 0.5 * J.R(sd, idx, 1)) * env.W * 0.04 * k * (idx != 0 ? 1 : 0),
                Y = J.Rs(sd, idx, 2) * env.H * 0.035 * k,
            };
        }
    });

    private static void KnRushIn() => Reg0("cam", "knRushIn", new CamDef
    {
        Name = "奥から突進",
        Tags = new[] { "pop", "graphic", "emotional" },
        W = 0.7,
        Strong = true,
        Plan = (rng, _) => new JObject
        {
            ["z"] = rng.Range(0.66, 0.76),
            ["r"] = rng.Range(-4, 4),
        },
        Get = env =>
        {
            var k = Math.Min(1, Km(env));
            var q = J.Clamp(env.Lt / 0.34);
            if (q >= 1)
                return new CamState
                {
                    S = 1 + 0.01 * J.Clamp((env.Lt - 0.34) / Math.Max(0.3, env.Cut!.Dur)),
                };
            var pc = env.Cut!.CamP;
            var e = J.E.OutBack(q, 1.9);
            var s = J.Lerp(1 - (1 - JLayouts.D(pc, "z", 0.7)) * k, 1, e);
            return new CamState
            {
                S = s,
                Rot = JLayouts.D(pc, "r") * (1 - J.E.OutCubic(q)) * k,
                Blur = 9 * k * (1 - J.E.OutCubic(q)),
            };
        }
    });

    private static void KnCornerSwing() => Reg0("trans", "knCornerSwing", new TransDef
    {
        Name = "コーナースイング",
        Tags = new[] { "pop", "graphic" },
        W = 0.8,
        Dur = 0.42,
        Plan = (rng, _) => new JObject { ["c"] = rng.Int(0, 3) },
        Draw = Wrap((ctx, a, b, p, i, pp) =>
        {
            var cw = i.Cw;
            var ch = i.Ch;
            var c = (int)JLayouts.D(pp, "c");
            var px = c == 1 || c == 2 ? cw : 0;
            var py = c >= 2 ? 0 : ch;
            var sg = c == 0 || c == 2 ? 1 : -1;
            var e = J.E.InOutCubic(p);
            using (var fill = new SKPaint { Color = Env.SkColor(i.Sc.Str("bg") ?? "#000000") })
                ctx.DrawRect(new SKRect(0, 0, cw, ch), fill);

            void Put(SKImage img, double ang)
            {
                ctx.Save();
                ctx.Translate(px, py);
                ctx.RotateDegrees((float)ang);                 ctx.Translate(-px, -py);
                ctx.DrawImage(img, 0, 0);
                ctx.Restore();
            }

            Put(b, -90 * sg * (1 - e));
            Put(a, 90 * sg * e);
            var lw = Math.Max(2, MinD(i) * 0.006);
            ctx.Save();
            ctx.Translate(px, py);
            ctx.RotateDegrees((float)(90 * sg * e));             using (var fill = new SKPaint { Color = Env.SkColor(TAcc(i), BellK(p)) })
            {
                var y0 = -(py != 0 ? ch : 0);
                var fy0 = (float)y0;
                var flw = (float)lw;
                var fh2 = (float)(ch * 2);
                if (c == 0 || c == 3) ctx.DrawRect(new SKRect(-flw, fy0, 0, fy0 + fh2), fill);
                else ctx.DrawRect(new SKRect(0, fy0, flw, fy0 + fh2), fill);
            }
            ctx.Restore();
        }),
    });

    private static void KnStutterCut() => Reg0("trans", "knStutterCut", new TransDef
    {
        Name = "刻みカット",
        Tags = new[] { "pop", "glitch", "graphic" },
        W = 0.7,
        Dur = 0.36,
        Plan = (rng, _) => new JObject
        {
            ["z"] = rng.Range(1.05, 1.1),
            ["o"] = rng.Range(0.02, 0.035) * rng.Pick(new[] { 1, -1 }),
        },
        Draw = Wrap((ctx, a, b, p, i, pp) =>
        {
            var cw = i.Cw;
            var ch = i.Ch;
            var z = JLayouts.D(pp, "z", 1.07);
            var o = JLayouts.D(pp, "o", 0.03) * cw;
            var seq = new (SKImage Img, double S, double Dx)[]
            {
                (a, 1.0, 0), (b, z, o), (a, 1 / z, -o * 0.6), (b, z * 1.03, -o), (b, 1.0, 0),
            };
            var cuts = new[] { 0.18, 0.36, 0.52, 0.7 };
            var k = 0;
            while (k < cuts.Length && p >= cuts[k]) k++;
            var (img, s, dx) = seq[k];
            var sc = ReferenceEquals(img, a) ? i.ScPrev : i.Sc;
            using (var fill = new SKPaint { Color = Env.SkColor(sc.Str("bg") ?? i.Sc.Str("bg") ?? "#000000") })
                ctx.DrawRect(new SKRect(0, 0, cw, ch), fill);
            var dx0 = (float)(cw / 2.0 - cw * s / 2 + dx);
            var dy0 = (float)(ch / 2.0 - ch * s / 2);
            ctx.DrawImage(img, new SKRect(dx0, dy0, dx0 + (float)(cw * s), dy0 + (float)(ch * s)));
            var since = k > 0 ? p - cuts[k - 1] : 1;
            if (since < 0.06)
            {
                var h = Math.Max(3, ch * 0.012);
                using (var fill = new SKPaint { Color = Env.SkColor(TAcc(i), 0.9) })
                    ctx.DrawRect(new SKRect(0, (float)((k % 2 != 0 ? 0.3 : 0.68) * ch), cw,
                        (float)((k % 2 != 0 ? 0.3 : 0.68) * ch + h)), fill);
            }
        }),
    });

    private static void KnStripSlam() => Reg0("trans", "knStripSlam", new TransDef
    {
        Name = "短冊スラム",
        Tags = new[] { "pop", "graphic" },
        W = 0.8,
        Dur = 0.45,
        Plan = (rng, _) => new JObject
        {
            ["n"] = rng.Int(3, 5),
            ["rev"] = rng.Chance(0.5),
            ["up"] = rng.Chance(0.25),
        },
        Draw = Wrap((ctx, a, b, p, i, pp) =>
        {
            var cw = i.Cw;
            var ch = i.Ch;
            var n = (int)JLayouts.D(pp, "n", 4);
            ctx.DrawImage(a, 0, 0);
            using (var fill = new SKPaint { Color = SKColors.Black.WithAlpha((byte)Math.Round(J.Clamp(0.3 * p) * 255)) })
                ctx.DrawRect(new SKRect(0, 0, cw, ch), fill);
            var rev = JLayouts.Truthy(pp["rev"]);
            var up = JLayouts.Truthy(pp["up"]);
            for (var idx = 0; idx < n; idx++)
            {
                var kk = rev ? n - 1 - idx : idx;
                var d = kk / (double)n * 0.45;
                var q = J.Clamp((p - d) / 0.55);
                if (q <= 0) continue;
                const double tl = 0.62;
                double y;
                if (q < tl)
                {
                    var f = q / tl;
                    y = -ch * (1 - f * f);
                }
                else
                {
                    var t = (q - tl) / (1 - tl);
                    y = -ch * 0.06 * Math.Abs(Math.Sin(t * Math.PI * 2)) * (1 - t);
                }
                if (up) y = -y;
                var x0 = (int)Math.Round(idx * cw / (double)n);
                var x1 = (int)Math.Round((idx + 1) * cw / (double)n);
                ctx.DrawImage(b, new SKRect(x0, 0, x1, ch),
                    new SKRect(x0, (float)Math.Round(y), x1, (float)(Math.Round(y) + ch)));
                if (q < 1)
                {
                    var ry = (float)Math.Round(y);
                    var by = up ? ry - 3 : ry + ch - 3;
                    using var fill = new SKPaint { Color = Env.SkColor(TAcc(i), 0.8 * (1 - q)) };
                    ctx.DrawRect(new SKRect(x0, by, x1, by + (float)Math.Max(3, ch * 0.008)), fill);
                }
            }
        }),
    });

    private static void KnWordScale() => Reg0("treat", "knWordScale", new TreatDef
    {
        Name = "大小語",
        Tags = new[] { "pop", "graphic", "editorial" },
        W = 0.9,
        Safe = true,
        Plan = (rng, _) => new JObject
        {
            ["big"] = rng.Range(1.28, 1.42),
            ["small"] = rng.Range(0.78, 0.86),
            ["pick"] = rng.Pick(new[] { "long", "long", "last", "first" }),
        },
        Draw = env =>
        {
            var it = env.It!;
            var pc = env.Cut!.TreatP;
            if (!Alive(it)) return;
            var g0 = TreatGeo(env, it);
            if (g0.NW < 2) return;
            var big = JLayouts.D(pc, "big", 1.35);
            var small = JLayouts.D(pc, "small", 0.82);
            var pick = JLayouts.S(pc, "pick");
            var cnt = new int[g0.NW];
            var kan = new int[g0.NW];
            foreach (var g in g0.L1.Cells)
            {
                var j = g0.Map[g.I];
                if (j >= 0)
                {
                    cnt[j]++;
                    if (J.IsKanji(g.Ch[0])) kan[j]++;
                }
            }
            var kw = pick == "last" ? g0.NW - 1
                : pick == "first" ? 0
                : CntArgMax(cnt, kan);
            double tb = 0, ts2 = 0;
            foreach (var g in g0.L1.Cells)
            {
                var j = g0.Map[g.I];
                if (j < 0) continue;
                var aAdv = JLayouts.Truthy(it["vertical"]) ? g.H : g.W;
                if (j == kw) tb += aAdv;
                else ts2 += aAdv;
            }
            var norm = Math.Min(1, (tb + ts2) / Math.Max(1e-6, tb * big + ts2 * small));
            double F(int j) => (j == kw ? big : small) * norm;
            var vert = JLayouts.Truthy(it["vertical"]);
            var lines = new SortedDictionary<int, List<JText.GlyphCell>>();
            foreach (var g in g0.L1.Cells)
            {
                if (!lines.TryGetValue(g.Li, out var l)) lines[g.Li] = l = new List<JText.GlyphCell>();
                l.Add(g);
            }
            var @out = new (double Dx, double Dy, double S)?[g0.L1.Cells.Count];
            foreach (var l in lines.Values)
            {
                var first = l[0];
                var last = l[^1];
                var a0 = vert ? first.Y - first.H / 2 : first.X - first.W / 2;
                var a1 = vert ? last.Y + last.H / 2 : last.X + last.W / 2;
                double pos = 0;
                var np = new List<double>();
                foreach (var g in l)
                {
                    var j = g0.Map[g.I];
                    var kk = j >= 0 ? F(j) : small;
                    var adv = (vert ? g.H : g.W) * kk;
                    np.Add(pos + adv / 2);
                    pos += adv + JAnim.Num(it, "track");
                }
                var len = pos - JAnim.Num(it, "track");
                var old = a1 - a0;
                var align = JLayouts.S(it, "align");
                var start = align == "left" ? a0 : align == "right" ? a1 - len : a0 + (old - len) / 2;
                for (var q = 0; q < l.Count; q++)
                {
                    var g = l[q];
                    var j = g0.Map[g.I];
                    var kk = j >= 0 ? F(j) : small;
                    var na = start + np[q];
                    var oa = vert ? g.Y : g.X;
                    @out[g.I] = vert ? (0, na - oa, kk) : (na - oa, (1 - kk) * 0.36, kk);
                }
            }
            var sx = JAnim.NumOr1(it, "sx");
            var sy = JAnim.NumOr1(it, "sy");
            var l1 = g0.L1;
            env.CharFns.Add((i, gl, _) =>
            {
                var o = i < @out.Length ? @out[i] : null;
                if (o == null) return null;
                var r = i < l1.Cells.Count ? l1.Cells[i] : null;
                var k = r != null && r.W > 1e-6 && gl != null ? JLayouts.D(gl, "w") / r.W : JAnim.Num(it, "size");
                return new CharXf { Dx = o.Value.Dx * k * sx, Dy = o.Value.Dy * k * sy, S = o.Value.S };
            });
        },
    });

        private static int CntArgMax(int[] cnt, int[] kan)
    {
        var b = 0;
        for (var j = 1; j < cnt.Length; j++)
            if (cnt[j] + kan[j] * 0.5 > cnt[b] + kan[b] * 0.5)
                b = j;
        return b;
    }

    private static void KnWordPlate() => Reg0("treat", "knWordPlate", new TreatDef
    {
        Name = "語ごと反転",
        Tags = new[] { "pop", "graphic", "glitch" },
        W = 0.8,
        Plan = (rng, _) => new JObject
        {
            ["first"] = rng.Chance(0.5),
            ["pad"] = rng.Range(0.08, 0.14),
            ["tilt"] = rng.Chance(0.4) ? rng.Range(1.5, 3) : 0,
        },
        Draw = env =>
        {
            var it = env.It!;
            var pc = env.Cut!.TreatP;
            if (!Alive(it)) return;
            var g0 = TreatGeo(env, it);
            if (g0.NW < 2) return;
            var sc = env.Sc!;
            var plate = J.Contrast(sc.Str("ink") ?? "#000", sc.Str("bg") ?? "#000") >= 2
                ? sc.Str("ink")!
                : sc.Str("fg") ?? "#FFFFFF";
            string? tc = null;
            var bv = 0.0;
            foreach (var c in new[] { sc.Str("bg"), sc.Str("fg"), sc.Str("ink"), sc.Str("accent") })
            {
                if (c == null || c == plate) continue;
                var k = J.Contrast(c, plate);
                if (k > bv)
                {
                    bv = k;
                    tc = c;
                }
            }
            if (bv < 2.6) tc = J.Lum(plate) > 0.5 ? "#111111" : "#FFFFFF";
            var first = JLayouts.Truthy(pc["first"]);
            bool On(int j) => (j % 2 == 0) == first;
            var pad = JLayouts.D(pc, "pad", 0.1);
            var tilt = JLayouts.D(pc, "tilt");
            env.PreFn = (e, x) =>
            {
                var lay = JText.LayoutText(x);
                var sx = JAnim.NumOr1(x, "sx");
                var sy = JAnim.NumOr1(x, "sy");
                var boxes = new SortedDictionary<int, (double X0, double Y0, double X1, double Y1, double A)>();
                var charFn = e.CharFn;
                foreach (var g in lay.Cells)
                {
                    var j = g0.Map[g.I];
                    if (j < 0 || !On(j)) continue;
                    var cf = charFn?.Invoke(g.I, JText.CellJson(g), lay.N);
                    if (cf != null && cf.Hide) continue;
                    var s = cf != null && cf.S != null ? cf.S.Value : 1;
                    var a = cf != null && cf.A != null ? cf.A.Value : 1;
                    var gx = (g.X + g.Vx) * sx + (cf != null ? cf.Dx : 0);
                    var gy = (g.Y + g.Vy) * sy + (cf != null ? cf.Dy : 0);
                    var hw = g.W * sx * s / 2;
                    var hh = g.H * sy * s / 2;
                    var key = j * 100 + g.Li;
                    var (bx0, by0, bx1, by1, ba) = boxes.TryGetValue(key, out var bbx)
                        ? bbx : (1e9, 1e9, -1e9, -1e9, 1.0);
                    boxes[key] = (Math.Min(bx0, gx - hw), Math.Min(by0, gy - hh),
                        Math.Max(bx1, gx + hw), Math.Max(by1, gy + hh), Math.Min(ba, a));
                }
                if (boxes.Count == 0) return;
                var ctx = e.Ctx;
                var pd = JAnim.Num(x, "size") * pad;
                var alpha = JAnim.NumNull(x, "alpha", 1);
                ctx.Save();
                ctx.Translate((float)JAnim.Num(x, "x"), (float)JAnim.Num(x, "y"));
                var rot = JAnim.Num(x, "rot");
                if (rot != 0) ctx.RotateDegrees((float)rot);
                var skew = JAnim.Num(x, "skew");
                if (skew != 0) ctx.Skew((float)Math.Tan(skew * J.DEG), 0);
                var q = 0;
                foreach (var b in boxes.Values)
                {
                    var t = tilt != 0 ? (q++ % 2 != 0 ? tilt : -tilt) : 0;
                    ctx.Save();
                    ctx.Translate((float)((b.X0 + b.X1) / 2), (float)((b.Y0 + b.Y1) / 2));
                    if (t != 0) ctx.RotateDegrees((float)t);
                    var w = b.X1 - b.X0 + pd * 2;
                    var h = b.Y1 - b.Y0 + pd * 1.4;
                    e.Rect(-w / 2, -h / 2, w, h, plate, alpha * b.A, true);
                    ctx.Restore();
                }
                ctx.Restore();
            };
            env.CharFns.Add((i, _, _) =>
            {
                var j = g0.Map[i];
                return j >= 0 && On(j) ? new CharXf { Color = tc } : null;
            });
        },
    });

    private static void KnSpeedTrail() => Reg0("decor", "knSpeedTrail", new DecorDef
    {
        Name = "追従スピード線",
        Tags = new[] { "pop", "graphic", "glitch" },
        W = 0.8,
        Layer = "front",
        Draw = (env, sp) =>
        {
            if (env.Pass != "main") return;
            var bb = GetBB(env, env.Body);
            var w = env.W;
            var h = env.H;
            var sc = env.Sc!;
            var u = Math.Min(w, h);
            var o = J.E.OutCubic(J.Clamp(env.Lt / 0.3)) * (1 - J.E.InCubic(env.POut));
            if (o <= 0.01) return;
            var vert = bb.Y1 - bb.Y0 > (bb.X1 - bb.X0) * 1.3;
            var roomA = vert ? bb.Y0 : bb.X0;
            var roomB = vert ? h - bb.Y1 : w - bb.X1;
            var side = Math.Abs(roomA - roomB) < u * 0.05
                ? (sp.Right ? 1 : -1)
                : roomB > roomA ? 1 : -1;
            var n = 8 + sp.N * 3;
            var t = env.Ltb;
            var sub = sc.Str("sub") ?? "#888888";
            var col = J.Contrast(sub, sc.Str("bg") ?? "#000") >= 1.4 ? sub : sc.Str("fg") ?? "#FFFFFF";
            var span = vert ? bb.X1 - bb.X0 : bb.Y1 - bb.Y0;
            var len = vert ? bb.Y1 - bb.Y0 : bb.X1 - bb.X0;
            var seed = sp.Seed;
            for (var i = 0; i < n; i++)
            {
                double R(int k) => J.R(seed, i, k);
                var f = (i + 0.5) / n;
                var lw = Math.Max(1.5, u * (0.003 + R(1) * 0.006));
                var l = u * (0.18 + R(2) * 0.3) * o;
                var tight = Math.Max(roomA, roomB) < u * 0.14;
                var st = tight ? len * (0.2 + R(3) * 0.3) : -u * (0.015 + 0.02 * R(3));
                var ph = (t * (1.3 + R(4) * 1.5) + R(5)) % 1.0;                 var s0 = st * -1 + ph * l * 0.6;
                var s1 = s0 + l * (0.35 + 0.65 * (1 - ph));
                var a = (0.35 + 0.35 * R(6)) * o * (1 - ph * 0.5);
                var c = tight
                    ? (i % 2 != 0
                        ? (vert ? bb.X1 : bb.Y1) + span * (0.05 + 0.3 * f)
                        : (vert ? bb.X0 : bb.Y0) - span * (0.05 + 0.3 * f))
                    : J.Lerp(vert ? bb.X0 : bb.Y0, vert ? bb.X1 : bb.Y1, 0.08 + 0.84 * f) + (R(7) - 0.5) * span * 0.04;
                var e0 = side < 0 ? (vert ? bb.Y0 : bb.X0) : (vert ? bb.Y1 : bb.X1);
                var p0 = e0 + side * s0;
                var p1 = e0 + side * s1;
                env.Line(vert
                        ? new[] { new[] { c, p0 }, new[] { c, p1 } }
                        : new[] { new[] { p0, c }, new[] { p1, c } },
                    col, lw, a, false);
            }
        },
    });

    private static void KnWordTicks() => Reg0("decor", "knWordTicks", new DecorDef
    {
        Name = "語カウンター",
        Tags = new[] { "graphic", "editorial", "pop" },
        W = 0.8,
        Layer = "front",
        Subtle = true,
        Draw = (env, _) =>
        {
            var bb = GetBB(env, env.Body);
            var w = env.W;
            var h = env.H;
            var sc = env.Sc!;
            var u = Math.Min(w, h);
            var o = J.E.OutCubic(J.Clamp(env.Lt / 0.35)) * (1 - J.E.InCubic(env.POut));
            if (o <= 0.01) return;
            var ts = WordTimes(env);
            var n = ts.Length;
            var k = CurIdx(ts, env.Lt);
            var segW = u * 0.06;
            var segH = Math.Max(4, u * 0.011);
            var g = u * 0.014;
            var tot = n * segW + (n - 1) * g;
            var below = bb.Y1 + u * 0.08 < h * 0.93;
            var y = below ? bb.Y1 + u * 0.06 : bb.Y0 - u * 0.06;
            var cx = J.Clamp((bb.X0 + bb.X1) / 2, tot / 2 + w * 0.06, w * 0.94 - tot / 2);
            var x0 = cx - tot / 2;
            var accent = sc.Str("accent");
            var acc = accent != null && J.Contrast(accent, sc.Str("bg") ?? "#000") >= 1.6
                ? accent : sc.Str("fg") ?? "#FFFFFF";
            for (var i = 0; i < n; i++)
            {
                var x = x0 + i * (segW + g);
                var on = i <= k;
                var f = on ? J.E.OutExpo(J.Clamp((env.Lt - ts[i]) / 0.18)) : 0;
                env.Rect(x, y - segH / 2, segW, segH, sc.Str("sub"), 0.35 * o, false);
                if (f > 0)
                    env.Rect(x, y - segH / 2 - (i == k ? segH * 0.6 * (1 - f) : 0), segW * f,
                        segH * (i == k ? 1 + 1.2 * (1 - f) : 1), acc, o, false);
            }
            var fs = J.Clamp(u * 0.028, 14, 32);
            var mono = JLayouts.Font0(env.St, "mono") ?? "mono";
            env.DrawItem(new JObject
            {
                ["text"] = Math.Max(0, k + 1).ToString("00") + " / " + n.ToString("00"),
                ["font"] = mono,
                ["size"] = fs,
                ["align"] = "left",
                ["x"] = x0 + tot + u * 0.02,
                ["y"] = y,
                ["color"] = sc.Str("sub"),
                ["alpha"] = o,
                ["ghost"] = false,
                ["plain"] = true,
            });
        },
    });
}
