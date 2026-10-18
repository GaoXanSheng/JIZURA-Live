
using Newtonsoft.Json.Linq;

namespace JizuraLive.LegacyEngine;

public static class P11Typo2
{
    private const string P = "typo2";
    private static int _installed;

    private static void Reg0(string group, string key, MoveDef def)
    {
        def.Set = "typo";         Reg.Register(group, key, def, P);
    }

    private static bool IsSp(string c) => c is " " or "　";

    private static CharXf Hide() => new() { Hide = true }; 
        private static JText.TextLayout LayOf(JObject it) => JText.Measure(it).Lay;

        private static double MotionK(Env env) => J.Clamp(env.Fx.Motion / 0.7, 0, 1.6);

        private static int CutN(Env env) => Math.Max(1, JLayouts.GlyphCount(env.Cut?.Text ?? ""));

    private static string AccentOf(Env env)
    {
        var sc = env.Sc!;
        return J.Contrast(sc.Str("accent") ?? "", sc.Str("bg") ?? "") >= 1.8 ? sc.Str("accent")! : sc.Str("fg")!;
    }

    private static string SubOf(Env env)
    {
        var sc = env.Sc!;
        return J.Contrast(sc.Str("sub") ?? "", sc.Str("bg") ?? "") >= 1.5 ? sc.Str("sub")! : sc.Str("fg")!;
    }

        private static void AddPost(Env env, JObject it, Action<Env, JObject> fn)
    {
        var prev = env.PostFn;
        env.PostFn = prev != null
            ? (e, x, bb) => { prev(e, x, bb); fn(e, x); }
            : (Action<Env, JObject, BBox?>)((e, x, _) => fn(e, x));
    }

        private static double[] Orders(Env env, JObject it)
    {
        var lay = LayOf(it);
        var @out = new double[lay.N];
        var idx = new List<int>();
        foreach (var g in lay.Cells)
            if (!IsSp(g.Ch))
                idx.Add(g.I);
        var n = idx.Count;
        if (n <= 1)
        {
            var N = CutN(env);
            var o = N > 1 ? J.Clamp(JAnim.Num(it, "mi") / (N - 1)) : 0;
            foreach (var i in idx) @out[i] = o;
            return @out;
        }
        for (var k = 0; k < idx.Count; k++) @out[idx[k]] = k / (double)(n - 1);
        return @out;
    }

        private static int KeyOf(JText.TextLayout lay)
    {
        var cells = lay.Cells;
        int best = -1, bl = 0;
        for (var i = 0; i < cells.Count; i++)
        {
            if (cells[i].Ch.Length == 0 || !J.IsKanji(cells[i].Ch[0])) continue;
            var j = i;
            while (j < cells.Count && cells[j].Ch.Length > 0 && J.IsKanji(cells[j].Ch[0])) j++;
            if (j - i > bl)
            {
                bl = j - i;
                best = i;
            }
            i = j;         }
        if (best >= 0) return cells[best].I;
        int wb = -1, wl = 0;
        bool Latin(string ch) => ch.Length > 0 && (ch[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' || ch is "'" or "’");
        for (var i = 0; i < cells.Count; i++)
        {
            var ch = cells[i].Ch;
            if (ch.Length == 0 || !(ch[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9')) continue;
            var j = i;
            while (j < cells.Count && Latin(cells[j].Ch)) j++;
            if (j - i > wl)
            {
                wl = j - i;
                wb = i;
            }
            i = j;
        }
        if (wb >= 0) return cells[wb].I;
        foreach (var g in cells)
            if (!IsSp(g.Ch) && !J.IsPunct(g.Ch[0]) && !J.IsSmallKana(g.Ch[0]))
                return g.I;
        return cells.Count > 0 ? cells[0].I : 0;
    }

        private sealed class LineSpan
    {
        public int Li;
        public double A0, A1, C;
    }

    private static List<LineSpan> LineInfo(JObject it)
    {
        var lay = LayOf(it);
        var sx = JAnim.NumOr1(it, "sx");
        var sy = JAnim.NumOr1(it, "sy");
        var vertical = JLayouts.Truthy(it["vertical"]);
        var map = new SortedDictionary<int, LineSpan>();
        foreach (var g in lay.Cells)
        {
            if (IsSp(g.Ch)) continue;
            var a0 = vertical ? (g.Y - g.H / 2) * sy : (g.X - g.W / 2) * sx;
            var a1 = vertical ? (g.Y + g.H / 2) * sy : (g.X + g.W / 2) * sx;
            if (!map.TryGetValue(g.Li, out var L))
                map[g.Li] = new LineSpan { Li = g.Li, A0 = a0, A1 = a1, C = vertical ? g.X * sx : g.Y * sy };
            else
            {
                L.A0 = Math.Min(L.A0, a0);
                L.A1 = Math.Max(L.A1, a1);
            }
        }
        return map.Values.ToList();
    }

        private static void InItem(Env env, JObject it, Action fn)
    {
        if (env.Pass != "main") return;
        var ctx = env.Ctx;
        ctx.Save();
        ctx.Translate((float)JAnim.Num(it, "x"), (float)JAnim.Num(it, "y"));
        var rot = JAnim.Num(it, "rot");
        if (rot != 0) ctx.RotateDegrees((float)rot);
        try
        {
            fn();
        }
        finally
        {
            ctx.Restore();
        }
    }

        private static ItemBounds BoxOf(JObject it) => JAnim.ItemBox(it);

        private readonly record struct BeatInfo(bool Has, double Since, double Len);

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
        return new BeatInfo(true, t - beats[i], Math.Max(0.2, len));
    }

        public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;
        RegisterTyKeyFirst();            RegisterTyLineWipe();            RegisterTyZoomOne();             RegisterTyUnderLift();           RegisterTyDotGrow();             RegisterTyBracketOpen();         RegisterTyRetype();              RegisterTyRubyDrop();            RegisterTyStrike();              RegisterTyToDot();               RegisterTyLineFeed();            RegisterTyBracketClose();        RegisterTyToIndex();             RegisterTyKeyLast();             RegisterTyUnderSink();           RegisterTyFoldVert();            RegisterTyKeyPulse();            RegisterTyReadCursor();          RegisterTyOutlineBlink();        RegisterTyTrackStep();       }

    private static void RegisterTyKeyFirst() => Reg0("enter", "tyKeyFirst", new MoveDef
    {
        Name = "キー字先行",
        Tags = new[] { "pop", "graphic", "editorial", "emotional" },
        W = 1,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var lay = LayOf(it);
            var sx = JAnim.NumOr1(it, "sx");
            var sy = JAnim.NumOr1(it, "sy");
            var plain = lay.Cells.Where(g => !IsSp(g.Ch)).ToList();
            if (plain.Count <= 1)
            {
                var q0 = J.Clamp(p / 0.7);
                env.CharFns.Add((_, _, _) => q0 <= 0
                    ? Hide()
                    : new CharXf { S = J.Lerp(1.9, 1, J.E.OutExpo(q0)), A = J.Clamp(q0 * 4) });
                return;
            }
            var ki = KeyOf(lay);
            var gk = lay.Cells.FirstOrDefault(g => g.I == ki) ?? plain[0];
            var md = 1.0;
            foreach (var g in plain) md = Math.Max(md, Math.Abs(g.I - ki));
            var kq = J.Clamp(p / 0.42);
            var byI = lay.Cells.ToDictionary(g => g.I);
            env.CharFns.Add((i, _, _) =>
            {
                if (i == ki)
                    return kq <= 0 ? Hide() : new CharXf { S = J.Lerp(2.1, 1, J.E.OutExpo(kq)), A = J.Clamp(kq * 4) };
                if (!byI.TryGetValue(i, out var g)) return null;
                var d = Math.Abs(i - ki) / md;
                var q = J.Clamp((p - 0.3 - d * 0.35) / 0.35);
                if (q <= 0) return Hide();
                var e = J.E.OutExpo(q);
                return new CharXf
                {
                    Dx = (gk.X - g.X) * sx * (1 - e),
                    Dy = (gk.Y - g.Y) * sy * (1 - e),
                    S = J.Lerp(0.4, 1, e),
                    A = J.Clamp(q * 2.5),
                };
            });
        },
    });

    private static void RegisterTyLineWipe() => Reg0("enter", "tyLineWipe", new MoveDef
    {
        Name = "行送りワイプ",
        Tags = new[] { "editorial", "graphic", "calm" },
        W = 1,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var lines = LineInfo(it);
            var nL = Math.Max(1, lines.Count);
            var st = Math.Min(0.28, 0.6 / nL);
            var qs = new Dictionary<int, double>();
            foreach (var L in lines)
            {
                var k = lines.FindIndex(q => q.Li == L.Li);
                qs[L.Li] = J.E.InOutCubic(J.Clamp((p - Math.Max(0, k) * st) / (1 - (nL - 1) * st)));
            }
            var sx = JAnim.NumOr1(it, "sx");
            var sy = JAnim.NumOr1(it, "sy");
            var V = JLayouts.Truthy(it["vertical"]);
            var byLi = lines.ToDictionary(L => L.Li);
            env.CharFns.Add((_, g, _) =>
            {
                if (g == null) return null;
                var li = (int)JAnim.Num(g, "li");
                var q = qs.GetValueOrDefault(li, 1);
                if (q >= 1) return null;
                if (!byLi.TryGetValue(li, out var L)) return null;
                var edge = J.Lerp(L.A0, L.A1, q);
                var start = V ? (JAnim.Num(g, "y") - JAnim.Num(g, "h") / 2) * sy
                              : (JAnim.Num(g, "x") - JAnim.Num(g, "w") / 2) * sx;
                var len = V ? JAnim.Num(g, "h") * sy : JAnim.Num(g, "w") * sx;
                var f = (edge - start) / Math.Max(1, len);
                if (f <= 0) return Hide();
                if (f >= 1) return null;
                var lim = -0.6 + 1.2 * f;
                return V ? new CharXf { ClipY = new[] { -0.7, lim } } : new CharXf { ClipX = new[] { -0.7, lim } };
            });
            var ac = AccentOf(env);
            var lw = Math.Max(2, JAnim.Num(it, "size") * 0.05);
            AddPost(env, it, (e, i) => InItem(e, i, () =>
            {
                foreach (var L in lines)
                {
                    var q = qs[L.Li];
                    if (q >= 1) continue;
                    var a = J.Lerp(L.A0, L.A1, J.E.InCubic(J.Clamp(q * 1.4 - 0.4)));
                    var b = J.Lerp(L.A0, L.A1, q);
                    if (b - a < 1) continue;
                    var off = L.C + JAnim.Num(i, "size") * (V ? sx : sy) * 0.64;
                    e.Line(V ? new[] { new[] { off, a }, new[] { off, b } } : new[] { new[] { a, off }, new[] { b, off } },
                        ac, lw, JAnim.NumNull(i, "alpha", 1), false);
                }
            }));
        },
    });

    private static void RegisterTyZoomOne() => Reg0("enter", "tyZoomOne", new MoveDef
    {
        Name = "一字ずつ拡大",
        Tags = new[] { "pop", "graphic", "emotional" },
        W = 0.8,
        MinDur = 1.0,
        InDur = (dur, n) => J.Clamp(0.14 * n + 0.2, 0.4, Math.Min(1.5, dur * 0.55)),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var lay = LayOf(it);
            var sx = JAnim.NumOr1(it, "sx");
            var sy = JAnim.NumOr1(it, "sy");
            var plain = lay.Cells.Where(g => !IsSp(g.Ch)).ToList();
            var nn = plain.Count;
            if (nn == 0) return;
            var x0 = 1e9;
            var x1 = -1e9;
            var y0 = 1e9;
            var y1 = -1e9;
            foreach (var g in plain)
            {
                x0 = Math.Min(x0, g.X - g.W / 2);
                x1 = Math.Max(x1, g.X + g.W / 2);
                y0 = Math.Min(y0, g.Y - g.H / 2);
                y1 = Math.Max(y1, g.Y + g.H / 2);
            }
            var mx = (x0 + x1) / 2;
            var my = (y0 + y1) / 2;
            var big = J.Clamp(Math.Min(env.W, env.H) * 0.5 / Math.Max(1, JAnim.Num(it, "size") * Math.Max(sx, sy)), 1.15, 2.6);
            var L = nn > 1 ? Math.Min(0.55, 2.2 / (nn + 1)) : 1;
            var rank = new Dictionary<int, int>();
            for (var k = 0; k < plain.Count; k++) rank[plain[k].I] = k;
            var single = nn == 1 && CutN(env) > 1;
            env.CharFns.Add((i, g, _) =>
            {
                if (!rank.TryGetValue(i, out var k)) return null;
                var s0 = nn > 1 ? k / (double)(nn - 1) * (1 - L) : 0;
                var q = J.Clamp((p - s0) / L);
                if (q <= 0) return Hide();
                if (single) return new CharXf { S = J.Lerp(big, 1, J.E.OutExpo(q)), A = J.Clamp(q * 5) };
                if (g == null) return null;
                var cx = (mx - JAnim.Num(g, "x")) * sx;
                var cy = (my - JAnim.Num(g, "y")) * sy;
                if (q < 0.38)
                {
                    var u = J.E.OutCubic(q / 0.38);
                    return new CharXf { Dx = cx, Dy = cy, S = J.Lerp(big * 1.25, big, u), A = J.Clamp(q / 0.1) };
                }
                var e = J.E.InOutCubic((q - 0.38) / 0.62);
                return new CharXf { Dx = cx * (1 - e), Dy = cy * (1 - e), S = J.Lerp(big, 1, e) };
            });
        },
    });

    private static void RegisterTyUnderLift() => Reg0("enter", "tyUnderLift", new MoveDef
    {
        Name = "下線から立つ",
        Tags = new[] { "editorial", "graphic", "pop" },
        W = 1,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var lines = LineInfo(it);
            var ord = Orders(env, it);
            var V = JLayouts.Truthy(it["vertical"]);
            var sx = JAnim.NumOr1(it, "sx");
            var sy = JAnim.NumOr1(it, "sy");
            var multi = LayOf(it).Cells.Count(g => !IsSp(g.Ch)) > 1;
            env.CharFns.Add((i, g, _) =>
            {
                var o = multi && i >= 0 && i < ord.Length ? ord[i] : 0;
                var q = J.Clamp((p - 0.22 - o * 0.38) / 0.4);
                if (q <= 0) return Hide();
                if (q >= 1) return null;
                var e = Math.Max(0.02, J.E.OutBack(q, 1.5));
                if (g == null) return new CharXf { Sx = V ? e : null, Sy = V ? null : e };
                return V
                    ? new CharXf { Sx = e, Dx = -(1 - e) * JAnim.Num(g, "w") * sx * 0.45 }
                    : new CharXf { Sy = e, Dy = (1 - e) * JAnim.Num(g, "h") * sy * 0.45 };
            });
            var qb = J.E.OutExpo(J.Clamp(p / 0.32));
            var qr = J.E.InCubic(J.Clamp((p - 0.72) / 0.28));
            var ac = AccentOf(env);
            AddPost(env, it, (e, i) => InItem(e, i, () =>
            {
                var lw = Math.Max(2.5, JAnim.Num(i, "size") * 0.055);
                foreach (var L in lines)
                {
                    var a = J.Lerp(L.A0, L.A1, qr);
                    var b = J.Lerp(L.A0, L.A1, qb);
                    if (b - a < 1) continue;
                    var off = V ? L.C - JAnim.Num(i, "size") * sx * 0.56 : L.C + JAnim.Num(i, "size") * sy * 0.56;
                    e.Line(V ? new[] { new[] { off, a }, new[] { off, b } } : new[] { new[] { a, off }, new[] { b, off } },
                        ac, lw, JAnim.NumNull(i, "alpha", 1), false);
                }
            }));
        },
    });

    private static void RegisterTyDotGrow() => Reg0("enter", "tyDotGrow", new MoveDef
    {
        Name = "点から字",
        Tags = new[] { "pop", "calm", "graphic" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var ord = Orders(env, it);
            var ac = AccentOf(env);
            env.CharFns.Add((i, _, _) =>
            {
                var t0 = i >= 0 && i < ord.Length ? ord[i] * 0.5 : 0;
                var q1 = J.Clamp((p - t0) / 0.18);
                var q2 = J.Clamp((p - t0 - 0.2) / 0.3);
                if (q1 <= 0) return Hide();
                if (q2 <= 0) return new CharXf { Ch = "・", S = 1.25 * J.E.OutBack(q1, 2.6), Color = ac };
                if (q2 >= 1) return null;
                return new CharXf { S = J.Lerp(0.3, 1, J.E.OutBack(q2, 1.8)), A = J.Clamp(q2 * 3) };
            });
        },
    });

        private static void DrawBrackets(Env e, bool v, double cx, double cy, double half, double w, double h,
        double size, string col, double a)
    {
        if (a <= 0.01) return;
        var g = size * 0.18;
        var L = Math.Max(size * 0.28, (v ? w : h) * 0.3);
        var lw = Math.Max(2, size * 0.055);
        if (!v)
        {
            var xl = cx - half - g;
            var xr = cx + half + g;
            var yt = cy - h / 2 - g * 0.5;
            var yb = cy + h / 2 + g * 0.5;
            e.Line(new[] { new[] { xl, yt + L }, new[] { xl, yt }, new[] { xl + L, yt } }, col, lw, a, false);
            e.Line(new[] { new[] { xr, yb - L }, new[] { xr, yb }, new[] { xr - L, yb } }, col, lw, a, false);
        }
        else
        {
            var yt = cy - half - g;
            var yb = cy + half + g;
            var xr = cx + w / 2 + g * 0.5;
            var xl = cx - w / 2 - g * 0.5;
            e.Line(new[] { new[] { xr - L, yt }, new[] { xr, yt }, new[] { xr, yt + L } }, col, lw, a, false);
            e.Line(new[] { new[] { xl + L, yb }, new[] { xl, yb }, new[] { xl, yb - L } }, col, lw, a, false);
        }
    }

    private static void RegisterTyBracketOpen() => Reg0("enter", "tyBracketOpen", new MoveDef
    {
        Name = "括弧が開く",
        Tags = new[] { "editorial", "graphic", "pop" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var b = BoxOf(it);
            var v = JLayouts.Truthy(it["vertical"]);
            var pad = JAnim.Num(it, "size") * 0.12;
            var e = J.E.InOutCubic(J.Clamp((p - 0.08) / 0.62));
            var half = ((v ? b.H : b.W) / 2 + pad) * e;
            if (v) it["clipY"] = new JArray(b.Cy - half, b.Cy + half);
            else it["clip"] = new JArray(b.Cx - half, b.Cx + half);
            var solo = LayOf(it).Cells.Count(g => !IsSp(g.Ch)) <= 1 && CutN(env) > 1;
            var a = solo ? 0 : J.Clamp(p / 0.08) * (1 - J.Clamp((p - 0.78) / 0.22));
            var col = AccentOf(env);
            var size = JAnim.Num(it, "size");
            AddPost(env, it, (en, _) =>
            {
                if (en.Pass == "main")
                    DrawBrackets(en, v, b.Cx, b.Cy, half, b.W, b.H, size, col, a);
            });
        },
    });

    private static void RegisterTyRetype() => Reg0("enter", "tyRetype", new MoveDef
    {
        Name = "打ち直し",         Tags = new[] { "editorial", "glitch", "emotional" },
        W = 0.8,
        MinDur = 1.0,
        InDur = (dur, n) => J.Clamp(0.1 * n + 0.4, 0.45, Math.Min(1.5, dur * 0.55)),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var lay = LayOf(it);
            var n = lay.N;
            var plain = lay.Cells.Where(g => !IsSp(g.Ch)).ToList();
            var seed = JAnim.SeedOf(it);
            if (plain.Count <= 1)
            {
                env.CharFns.Add((_, _, _) => p < 0.35 ? Hide() : null);
                return;
            }
            var wi = -1;
            if (plain.Count >= 3)
                wi = plain[1 + (int)(J.H(seed, 31) % (uint)(plain.Count - 1))].I;
            var s = n + (wi >= 0 ? 2 : 0);
            var k = (int)Math.Floor(J.Clamp(p) * (s + 0.999));
            int shown;
            var wrong = false;
            if (wi < 0 || k <= wi) shown = Math.Min(n, k);
            else if (k == wi + 1)
            {
                shown = wi + 1;
                wrong = true;
            }
            else if (k == wi + 2) shown = wi;
            else shown = Math.Min(n, k - 2);
            var pool = JLang.Pool("kana") ?? "あいうえお";
            var poolCp = JLayouts.CodePoints(pool);
            var wch = poolCp[(int)(J.H(seed, 32) % (uint)poolCp.Count)];
            var ac = AccentOf(env);
            var layCh = wi >= 0 && wi < lay.Cells.Count ? lay.Cells[wi].Ch : "";
            env.CharFns.Add((i, _, _) =>
            {
                if (i >= shown) return Hide();
                if (wrong && i == wi)
                    return new CharXf { Ch = wch == layCh ? "＊" : wch, Color = ac };
                return null;
            });
            it["cursorAt"] = p < 1 && !JLayouts.Truthy(it["vertical"]) && !JLayouts.Truthy(it["rot"]) ? shown : -1;
        },
    });

    private static void RegisterTyRubyDrop() => Reg0("enter", "tyRubyDrop", new MoveDef
    {
        Name = "ルビから",
        Tags = new[] { "calm", "editorial", "emotional" },
        W = 1,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var ord = Orders(env, it);
            var v = JLayouts.Truthy(it["vertical"]);
            var sx = JAnim.NumOr1(it, "sx");
            var sy = JAnim.NumOr1(it, "sy");
            var sub = SubOf(env);
            env.CharFns.Add((i, g, _) =>
            {
                var t0 = i >= 0 && i < ord.Length ? ord[i] : 0;
                var q = J.Clamp((p - t0 * 0.45) / 0.55);
                if (q <= 0) return Hide();
                if (q >= 1) return null;
                var off = g == null ? 0 : v ? JAnim.Num(g, "w") * sx * 0.8 : -JAnim.Num(g, "h") * sy * 0.8;
                if (q < 0.4)
                {
                    var a = J.Clamp(q / 0.4 * 1.6);
                    return v
                        ? new CharXf { Dx = off, S = 0.32, A = a, Color = sub }
                        : new CharXf { Dy = off, S = 0.32, A = a, Color = sub };
                }
                var e = J.E.OutCubic((q - 0.4) / 0.6);
                var o = new CharXf { S = J.Lerp(0.32, 1, e) };
                if (v) o.Dx = off * (1 - e);
                else o.Dy = off * (1 - e);
                if (e < 0.55) o.Color = sub;
                return o;
            });
        },
    });

    private static void RegisterTyStrike() => Reg0("exit", "tyStrike", new MoveDef
    {
        Name = "線で消す",
        Tags = new[] { "editorial", "graphic", "emotional" },
        W = 1,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var lines = LineInfo(it);
            var ord = Orders(env, it);
            var v = JLayouts.Truthy(it["vertical"]);
            env.CharFns.Add((i, _, _) =>
            {
                var t0 = i >= 0 && i < ord.Length ? ord[i] : 0;
                var q = J.Clamp((p - 0.3 - t0 * 0.3) / 0.4);
                if (q <= 0) return null;
                if (q >= 1) return Hide();
                var e = J.E.InCubic(q);
                return v
                    ? new CharXf { Sx = 1 - e * 0.95, A = 1 - e * e }
                    : new CharXf { Sy = 1 - e * 0.95, A = 1 - e * e };
            });
            var ql = J.E.OutExpo(J.Clamp(p / 0.38));
            var qr = J.E.InCubic(J.Clamp((p - 0.74) / 0.26));
            var ac = AccentOf(env);
            var sx = JAnim.NumOr1(it, "sx");
            var sy = JAnim.NumOr1(it, "sy");
            AddPost(env, it, (e, i) => InItem(e, i, () =>
            {
                var lw = Math.Max(2.5, JAnim.Num(i, "size") * 0.07);
                foreach (var L in lines)
                {
                    var pad = JAnim.Num(i, "size") * 0.1;
                    var a0 = L.A0 - pad;
                    var a1 = L.A1 + pad;
                    var a = J.Lerp(a0, a1, qr);
                    var b = J.Lerp(a0, a1, ql);
                    if (b - a < 1) continue;
                    var off = L.C + JAnim.Num(i, "size") * (v ? sx : sy) * 0.04;
                    e.Line(v ? new[] { new[] { off, a }, new[] { off, b } } : new[] { new[] { a, off }, new[] { b, off } },
                        ac, lw, 1, false);
                }
            }));
        },
    });

    private static void RegisterTyToDot() => Reg0("exit", "tyToDot", new MoveDef
    {
        Name = "点に戻る",
        Tags = new[] { "calm", "pop", "graphic" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var ord = Orders(env, it);
            var ac = AccentOf(env);
            env.CharFns.Add((i, _, _) =>
            {
                var t0 = i >= 0 && i < ord.Length ? ord[i] : 0;
                var q = J.Clamp((p - t0 * 0.4) / 0.6);
                if (q <= 0) return null;
                if (q >= 1) return Hide();
                if (q < 0.5) return new CharXf { S = J.Lerp(1, 0.3, J.E.InCubic(q / 0.5)) };
                var u = (q - 0.5) / 0.5;
                return new CharXf { Ch = "・", Color = ac, S = 1.5 * (1 - J.E.InCubic(u)), A = 1 - u * u };
            });
        },
    });

    private static void RegisterTyLineFeed() => Reg0("exit", "tyLineFeed", new MoveDef
    {
        Name = "改行送り",
        Tags = new[] { "editorial", "calm", "graphic" },
        W = 1,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var b = BoxOf(it);
            var v = JLayouts.Truthy(it["vertical"]);
            var pad = JAnim.Num(it, "size") * 0.12;
            var k = J.Clamp(p) * 3;
            var st = Math.Min(3, Math.Floor(k));
            var f = k - st;
            var step = Math.Min(1, (st + J.E.OutCubic(J.Clamp(f * 2.6))) / 3);
            var dist = (v ? b.W : b.H) + JAnim.Num(it, "size") * 0.3;
            if (v)
            {
                it["clip"] = new JArray(b.X0 - pad, b.X1 + pad);
                it["x"] = JAnim.Num(it, "x") + dist * step;
            }
            else
            {
                it["clipY"] = new JArray(b.Y0 - pad, b.Y1 + pad);
                it["y"] = JAnim.Num(it, "y") - dist * step;
            }
            if (p >= 0.999) it["alpha"] = 0;
        },
    });

    private static void RegisterTyBracketClose() => Reg0("exit", "tyBracketClose", new MoveDef
    {
        Name = "括弧閉じ",
        Tags = new[] { "editorial", "graphic", "pop" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var b = BoxOf(it);
            var v = JLayouts.Truthy(it["vertical"]);
            var pad = JAnim.Num(it, "size") * 0.12;
            var e = J.E.InOutCubic(J.Clamp((p - 0.12) / 0.7));
            var half = ((v ? b.H : b.W) / 2 + pad) * (1 - e);
            if (v) it["clipY"] = new JArray(b.Cy - half, b.Cy + half);
            else it["clip"] = new JArray(b.Cx - half, b.Cx + half);
            if (e >= 1) it["alpha"] = 0;
            var solo = LayOf(it).Cells.Count(g => !IsSp(g.Ch)) <= 1 && CutN(env) > 1;
            var a = solo ? 0 : J.Clamp(p / 0.12) * (1 - J.Clamp((p - 0.84) / 0.16));
            var col = AccentOf(env);
            var size = JAnim.Num(it, "size");
            AddPost(env, it, (en, _) =>
            {
                if (en.Pass == "main")
                    DrawBrackets(en, v, b.Cx, b.Cy, half, b.W, b.H, size, col, a);
            });
        },
    });

    private static void RegisterTyToIndex() => Reg0("exit", "tyToIndex", new MoveDef
    {
        Name = "番号に変わる",
        Tags = new[] { "editorial", "glitch", "graphic" },
        W = 0.8,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var ord = Orders(env, it);
            var sub = SubOf(env);
            var v = JLayouts.Truthy(it["vertical"]);
            var num = new Dictionary<int, string>();
            var k = 0;
            var plain = LayOf(it).Cells.Where(g => !IsSp(g.Ch)).ToList();
            var @base = plain.Count <= 1 ? (int)Math.Round(JAnim.Num(it, "mi"), MidpointRounding.AwayFromZero) : 0;
            foreach (var g in plain) num[g.I] = (@base + ++k).ToString().PadLeft(2, '0');
            env.CharFns.Add((i, g, _) =>
            {
                var t0 = i >= 0 && i < ord.Length ? ord[i] : 0;
                var q = J.Clamp((p - t0 * 0.4) / 0.6);
                if (q <= 0) return null;
                if (q >= 1) return Hide();
                if (q < 0.3) return new CharXf { S = J.Lerp(1, 0.55, J.E.InCubic(q / 0.3)) };
                var u = (q - 0.3) / 0.7;
                var o = new CharXf { Ch = num.GetValueOrDefault(i, "00"), S = 0.42, Color = sub, A = 1 - J.E.InCubic(u) };
                if (g != null)
                {
                    if (v) o.Dx = -JAnim.Num(g, "w") * 0.2 * u;
                    else o.Dy = -JAnim.Num(g, "h") * 0.2 * u;
                }
                return o;
            });
        },
    });

    private static void RegisterTyKeyLast() => Reg0("exit", "tyKeyLast", new MoveDef
    {
        Name = "一字残し",
        Tags = new[] { "emotional", "pop", "graphic" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var lay = LayOf(it);
            var sx = JAnim.NumOr1(it, "sx");
            var sy = JAnim.NumOr1(it, "sy");
            var plain = lay.Cells.Where(g => !IsSp(g.Ch)).ToList();
            var ki = plain.Count > 1 ? KeyOf(lay) : plain.Count > 0 ? plain[0].I : 0;
            var gk = lay.Cells.FirstOrDefault(g => g.I == ki) ?? (plain.Count > 0 ? plain[0] : null);
            var md = 1.0;
            foreach (var g in plain) md = Math.Max(md, Math.Abs(g.I - ki));
            if (plain.Count <= 1 && CutN(env) > 1)
            {
                var keepCh = plain.Count > 0 && plain[0].Ch.Length > 0 && J.IsKanji(plain[0].Ch[0]);
                var keep = keepCh &&
                           J.H(unchecked((int)(env.Cut?.Seed ?? 0)), (int)Math.Round(JAnim.Num(it, "mi"), MidpointRounding.AwayFromZero), 5) % 2 == 0;
                env.CharFns.Add((_, _, _) =>
                {
                    if (!keep)
                    {
                        var q = J.Clamp(p / 0.5);
                        return q >= 1 ? Hide() : new CharXf { S = 1 - 0.6 * J.E.InCubic(q), A = 1 - q };
                    }
                    var q2 = J.Clamp((p - 0.4) / 0.6);
                    return q2 >= 1 ? Hide() : new CharXf { S = 1 + J.E.InCubic(q2) * 1.4, A = 1 - J.E.InCubic(q2) };
                });
                return;
            }
            var byI = lay.Cells.ToDictionary(g => g.I);
            env.CharFns.Add((i, _, _) =>
            {
                if (i == ki)
                {
                    var q = J.Clamp((p - 0.45) / 0.55);
                    return q >= 1 ? Hide() : new CharXf { S = 1 + J.E.InCubic(q) * 1.8, A = 1 - J.E.InCubic(q) };
                }
                if (!byI.TryGetValue(i, out var g)) return null;
                var d = Math.Abs(i - ki) / md;
                var q2 = J.Clamp((p - (1 - d) * 0.2) / 0.45);
                if (q2 <= 0) return null;
                if (q2 >= 1) return Hide();
                var e = J.E.InCubic(q2);
                return new CharXf
                {
                    Dx = (gk!.X - g.X) * sx * e,
                    Dy = (gk.Y - g.Y) * sy * e,
                    S = 1 - 0.6 * e,
                    A = 1 - e,
                };
            });
        },
    });

    private static void RegisterTyUnderSink() => Reg0("exit", "tyUnderSink", new MoveDef
    {
        Name = "下線へ沈む",
        Tags = new[] { "editorial", "calm", "graphic" },
        W = 1,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var lines = LineInfo(it);
            var ord = Orders(env, it);
            var v = JLayouts.Truthy(it["vertical"]);
            var sx = JAnim.NumOr1(it, "sx");
            var sy = JAnim.NumOr1(it, "sy");
            env.CharFns.Add((i, g, _) =>
            {
                var t0 = i >= 0 && i < ord.Length ? ord[i] : 0;
                var q = J.Clamp((p - 0.2 - t0 * 0.4) / 0.35);
                if (q <= 0) return null;
                if (q >= 1) return Hide();
                var e = J.E.InCubic(q);
                var d = e * 1.25;
                if (g == null) return new CharXf { A = 1 - e };
                return v
                    ? new CharXf { Dx = -JAnim.Num(g, "w") * sx * d, ClipX = new[] { -0.55 + d, 0.7 } }
                    : new CharXf { Dy = JAnim.Num(g, "h") * sy * d, ClipY = new[] { -0.7, 0.55 - d } };
            });
            var qb = J.E.OutExpo(J.Clamp(p / 0.28));
            var qr = J.E.InCubic(J.Clamp((p - 0.78) / 0.22));
            var ac = AccentOf(env);
            AddPost(env, it, (e, i) => InItem(e, i, () =>
            {
                var lw = Math.Max(2.5, JAnim.Num(i, "size") * 0.05);
                foreach (var L in lines)
                {
                    var a = J.Lerp(L.A0, L.A1, qr);
                    var b = J.Lerp(L.A0, L.A1, qb);
                    if (b - a < 1) continue;
                    var off = v ? L.C - JAnim.Num(i, "size") * sx * 0.56 : L.C + JAnim.Num(i, "size") * sy * 0.56;
                    e.Line(v ? new[] { new[] { off, a }, new[] { off, b } } : new[] { new[] { a, off }, new[] { b, off } },
                        ac, lw, 1, false);
                }
            }));
        },
    });

    private static void RegisterTyFoldVert() => Reg0("exit", "tyFoldVert", new MoveDef
    {
        Name = "縦組に折れる",
        Tags = new[] { "graphic", "editorial", "pop" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var lay = LayOf(it);
            var v = JLayouts.Truthy(it["vertical"]);
            var sx = JAnim.NumOr1(it, "sx");
            var sy = JAnim.NumOr1(it, "sy");
            var ln = new SortedDictionary<int, (List<JText.GlyphCell> Gs, double C0, double C1)>();
            foreach (var g in lay.Cells)
            {
                if (IsSp(g.Ch)) continue;
                if (!ln.TryGetValue(g.Li, out var L)) L = (new List<JText.GlyphCell>(), 1e9, -1e9);
                L.Gs.Add(g);
                L.C0 = Math.Min(L.C0, v ? g.Y : g.X);
                L.C1 = Math.Max(L.C1, v ? g.Y : g.X);
                ln[g.Li] = L;
            }
            var e = J.E.InOutCubic(J.Clamp(p / 0.5));
            var f = J.Clamp((p - 0.36) / 0.5);
            env.CharFns.Add((i, g, _) =>
            {
                if (f >= 1) return Hide();
                if (g == null || !ln.TryGetValue((int)JAnim.Num(g, "li"), out var L)) return null;
                var k = Math.Max(0, L.Gs.FindIndex(q => q.I == i));
                var n = L.Gs.Count;
                var mid = (L.C0 + L.C1) / 2;
                var pitch = Math.Min(1.02, (v ? env.W : (double)env.H) * 0.82
                                          / Math.Max(1, n * (v ? JAnim.Num(g, "w") * sx : JAnim.Num(g, "h") * sy)));
                var off = (k - (n - 1) / 2.0) * pitch;
                double dx, dy;
                if (!v)
                {
                    dx = (mid - JAnim.Num(g, "x")) * sx;
                    dy = off * JAnim.Num(g, "h") * sy;
                }
                else
                {
                    dy = (mid - JAnim.Num(g, "y")) * sy;
                    dx = off * JAnim.Num(g, "w") * sx;
                }
                var drift = J.E.InQuad(f) * JAnim.Num(it, "size") * 0.25;
                return new CharXf
                {
                    Dx = dx * e - (v ? drift : 0),
                    Dy = dy * e + (v ? 0 : drift),
                    A = 1 - J.E.InQuad(f),
                };
            });
        },
    });

    private static void RegisterTyKeyPulse() => Reg0("hold", "tyKeyPulse", new MoveDef
    {
        Name = "一字の鼓動",
        Tags = new[] { "emotional", "pop", "calm" },
        W = 1,
        Draw = env =>
        {
            var it = env.It!;
            var amt = env.Ap;
            var lay = LayOf(it);
            var plain = lay.Cells.Where(g => !IsSp(g.Ch)).ToList();
            if (plain.Count == 0) return;
            var b = BeatOf(env);
            var pulse = b.Has
                ? Math.Exp(-b.Since / Math.Max(0.12, b.Len * 0.35))
                : Math.Pow(0.5 + 0.5 * Math.Sin(env.Ltb * J.TAU * 0.95), 4);
            var k = 0.1 * amt * MotionK(env) * pulse;
            if (k < 0.002) return;
            if (plain.Count == 1)
            {
                if (plain[0].Ch.Length == 0 || !J.IsKanji(plain[0].Ch[0])) return;
                env.CharFns.Add((_, _, _) => new CharXf { S = 1 + k * 0.7 });
                return;
            }
            var ki = KeyOf(lay);
            env.CharFns.Add((i, _, _) => i == ki ? new CharXf { S = 1 + k } : null);
        },
    });

    private static void RegisterTyReadCursor() => Reg0("hold", "tyReadCursor", new MoveDef
    {
        Name = "読み送り",
        Tags = new[] { "calm", "editorial", "pop" },
        W = 1,
        Draw = env =>
        {
            var it = env.It!;
            var amt = env.Ap;
            if (amt < 0.3) return;
            var lay = LayOf(it);
            var plain = lay.Cells.Where(g => !IsSp(g.Ch)).ToList();
            var n = plain.Count;
            if (n == 0) return;
            var ac = AccentOf(env);
            var v = JLayouts.Truthy(it["vertical"]);
            var lift = JAnim.Num(it, "size") * 0.07 * Math.Min(1, MotionK(env));
            var b = BeatOf(env);
            var rate = b.Has ? 1 / Math.Max(0.2, b.Len) : 4;
            var tot = n <= 1 ? CutN(env) : n;
            var pos = (int)Math.Floor(env.Ltb * rate) % (tot + 2);
            var frac = env.Ltb * rate % 1;
            var up = J.E.OutCubic(J.Clamp(frac * 4)) * (1 - J.E.InCubic(J.Clamp((frac - 0.6) / 0.4)));
            if (n <= 1)
            {
                if ((int)Math.Round(JAnim.Num(it, "mi"), MidpointRounding.AwayFromZero) % (tot + 2) != pos) return;
                env.CharFns.Add((_, _, _) =>
                    v ? new CharXf { Dx = lift * up, Color = ac } : new CharXf { Dy = -lift * up, Color = ac });
                return;
            }
            var gi = pos >= 0 && pos < n ? plain[pos].I : -1;
            env.CharFns.Add((i, _, _) =>
                i == gi ? (v ? new CharXf { Dx = lift * up, Color = ac } : new CharXf { Dy = -lift * up, Color = ac }) : null);
        },
    });

    private static void RegisterTyOutlineBlink() => Reg0("hold", "tyOutlineBlink", new MoveDef
    {
        Name = "白抜き明滅",
        Tags = new[] { "glitch", "graphic", "pop" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var amt = env.Ap;
            var lay = LayOf(it);
            var plain = lay.Cells.Where(g => !IsSp(g.Ch)).ToList();
            if (plain.Count == 0) return;
            var seed = JAnim.SeedOf(it);
            var st = (int)env.Step >> 1;
            var denom = plain.Count == 1 ? Math.Max(1, CutN(env) * 0.5) : 1;
            if (J.R(seed, st, 41) > 0.28 * amt * (0.5 + 0.5 * MotionK(env)) / denom) return;
            var pick = plain[(int)(J.H(seed, st, 42) % (uint)plain.Count)].I;
            var two = plain.Count > 3 && J.R(seed, st, 43) < 0.3
                ? plain[(int)(J.H(seed, st, 44) % (uint)plain.Count)].I
                : -1;
            env.CharFns.Add((i, _, _) => i == pick || i == two ? new CharXf { Outline = true } : null);
        },
    });

    private static void RegisterTyTrackStep() => Reg0("hold", "tyTrackStep", new MoveDef
    {
        Name = "字間ステップ",
        Tags = new[] { "graphic", "editorial", "pop" },
        W = 0.8,
        Draw = env =>
        {
            var it = env.It!;
            var amt = env.Ap;
            if (LayOf(it).Cells.Count(g => !IsSp(g.Ch)) < 2) return;
            var b = BeatOf(env);
            var len = b.Has ? Math.Max(0.25, b.Len) : 0.55;
            var t = env.Ltb / len;
            var k = (int)Math.Floor(t);
            var f = t - k;
            int[] lv = { 0, 1, 2, 1 };
            var a = lv[((k % 4) + 4) % 4];
            var b2 = lv[(((k + 1) % 4) + 4) % 4];
            var v = J.Lerp(a, b2, J.E.OutExpo(J.Clamp((f - 0.85) / 0.15)));
            it["track"] = JAnim.Num(it, "track") + v * 0.05 * amt * Math.Min(1.2, MotionK(env));
            it["_m"] = null;
            it["_lay"] = null;
        },
    });
}
