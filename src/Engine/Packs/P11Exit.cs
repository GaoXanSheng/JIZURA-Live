// 移植自 frontend/src/engine/11p_exit.js（exit/hold 表现包：exit 36 条 + hold 20 条）。
// JS 的 pack 名是 'exitHold'（exit+hold 两组合用包）；C# 侧按任务约定统一挂 pack="exit"。
//
// 契约与 JAnim.cs / P11Enter.cs 相同：MoveDef.Draw(env) 里 it = env.It、p = env.Ap（exit=pOut、
// hold=amt）、env.Cut 携带 dur/inDur/outDur/seed/text；逐字动画推 env.CharFns（管线 mainDraw 统一
// CombineChar）、逐片动画推 env.PieceFns；条目字段直接写 item（bands/vbands/clip/clipY/streak/echo/
// dash/fillAlpha/blur…JLayouts.DrawFx 全部消费）。随机数全部是 J.R/J.Rs/J.H/J.Noise1 确定性哈希
// （无顺序流），哈希入参逐字保持——同 seed 同动画轨迹。
//
// 管线缺口与降级（本文件内解决，不改共享文件）：
// 1. JS 的 merged/addC 自带合并（保 clip/skew/blur/outline，尾部有 a<=0.003/|s|<0.004 的 HIDE 折叠）：
//    C# JAnim.CombineChar 全字段合并；DrawItem 侧 a<=0.002 即跳过，语义等价，不需要。
// 2. it.post/it.pre（chainPost/chainPre）：管线钩子已接（Env.PreFn/PostFn → JLayouts.DrawFx，
//    pre=文字下方、post=文字上方）；本包 chainPost 维持「就地执行」约定（内容/几何一致，层级在
//    主文字下方；文字副本类 post 经 DrawCopy 就地画副本），层级敏感的迁出来不及逐条回归，
//    见 P11TreatTrans/P11Looks 的 env.PreFn/PostFn 先例。
// 3. it.clipFn（任意路径裁剪，irisClose/diagWipeOut/blindsClose/checkerOut 用）：已接管线钩子
//    （Env.ClipFn → DrawFx ClipPath，返回 SKPath；JS 的 ctx.beginPath 构造对位）。
// 4. `ae`（AE 导出模板名）与 dissolve 的 pieces:true / backspace 的 cursor:true：MoveDef 无对应
//    属性，未注册——对位见各条目行内注释。
// 5. env.beat 对象（len/since/index）由渲染器每帧挂 Env（BeatHas/BeatSince/BeatLen/BeatIndex，
//    09_render beatAt 对位）；BeatHas=false 回 JS 的 ltb 回退式。
//
// 跨文件符号对位：J.measure→JText.Measure、J.layoutText→JText.LayoutText、J.itemBox→JAnim.ItemBox、
// J.glyphCount→JLayouts.GlyphCount、J.itemBands→JAnim.ItemBands、J.noise1→J.Noise1、
// J.rgba(本地 Rgba)/r/rs/rr/h/mix/lum/smooth/clamp/lerp→J.*。

using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace JizuraLive.Engine;

public static class P11Exit
{
    private const string P = "exit";
    private static int _installed;

    // ================= 本包 helper（JS L9-223 逐条对位） =================

    private static bool IsHex(string? c) => c != null && c.Length > 0 && c[0] == '#' && (c.Length == 7 || c.Length == 4);

    private static string MixC(string? a, string? b, double t)
    {
        t = J.Clamp(t);
        if (t <= 0.002) return a ?? "#ffffff";
        if (t >= 0.998) return b ?? "#ffffff";
        return IsHex(a) && IsHex(b) ? J.Mix(a!, b!, t) : (t < 0.5 ? a! : b!);
    }

    /// <summary>JS colOf：it.color || '#ffffff'（本文件不做 isHex 校验，对齐源）。</summary>
    private static string ColOf(JObject it) => S(it, "color") ?? "#ffffff";

    private static string? ScK(Env env, string k) => env.Sc?.Str(k);

    private static double MotionK(Env env) => J.Clamp(env.Fx.Motion / 0.7, 0, 1.6);

    private static bool IsSp(string? ch) => ch is " " or "　";

    private static JText.TextLayout LayOf(JObject it) => JText.Measure(it).Lay;

    /// <summary>JS box：item 在设计空间的包围盒（每次重算，sx/sy/size 可能刚变过；Measure 按内容 memo）。</summary>
    private static ItemBounds Box(JObject it) => JAnim.ItemBox(it);

    private static int CutN(Env env) => Math.Max(1, JLayouts.GlyphCount(env.Cut?.Text ?? ""));
    private static bool CutBit(Env env, int k) => (J.H(unchecked((int)(env.Cut?.Seed ?? 0)), k, 991) & 1) == 1;

    private static bool IsSingle(Env env, JObject it) => LayOf(it).N == 1 && CutN(env) > 1;

    private static double Win(double p, double o, double spread) => J.Clamp((p - o * spread) / (1 - spread));

    private static double[] Rot2(double x, double y, double r) =>
        new[] { x * Math.Cos(r) - y * Math.Sin(r), x * Math.Sin(r) + y * Math.Cos(r) };

    private static double[] ToD(JObject it, double lx, double ly)
    {
        var xy = Rot2(lx, ly, N0(it, "rot") * J.DEG);
        return new[] { N0(it, "x") + xy[0], N0(it, "y") + xy[1] };
    }

    /// <summary>设计坐标的方向向量改用 item 空间表示，charFn 的偏移才会沿屏幕轴走。</summary>
    private static double[] DownI(JObject it)
    {
        var r = N0(it, "rot") * J.DEG;
        return new[] { Math.Sin(r), Math.Cos(r) };
    }

    /// <summary>字形 i 在 cut 内的先后次序 0..1；单字 item 按 mi 在 cut 里的位置排。</summary>
    private static Func<int, int, double> OrderOf(Env env, JObject it)
    {
        var nCut = CutN(env);
        var mi = Num(it, "mi");
        return (i, n) => n > 1 ? i / (double)(n - 1) : nCut > 1 ? J.Clamp(mi / (nCut - 1)) : 0;
    }

    // 剪裁窗固定在 item 空间，字形被 (dx, dy) 平移时窗子不动；kx/ky 是同一 charFn 附带的字形缩放
    private static CharXf WinShift(JObject g, JObject it, double dx, double dy, double kx = 1, double ky = 1)
    {
        var sx = N1(it, "sx");
        var sy = N1(it, "sy");
        var mx = dx != 0 ? 0.66 : 1.6;
        var my = dy != 0 ? 0.66 : 1.6;
        if (!B(g, "r90"))
        {
            var fx = dx / (D(g, "w") * sx);
            var fy = dy / (D(g, "h") * sy);
            return new CharXf
            {
                ClipX = new[] { (-mx - fx) / kx, (mx - fx) / kx },
                ClipY = new[] { (-my - fy) / ky, (my - fy) / ky },
            };
        }

        // 字形转了 90°（竖排）：局部 x 沿 item +y、按 sx 缩放，局部 y 沿 item -x、按 sy 缩放
        var mY = my * D(g, "h") * sx;
        var mX = mx * D(g, "w") * sy;
        return new CharXf
        {
            ClipX = new[] { (-mY - dy) / (sx * kx * D(g, "w")), (mY - dy) / (sx * kx * D(g, "w")) },
            ClipY = new[] { (-mX + dx) / (sy * ky * D(g, "h")), (mX + dx) / (sy * ky * D(g, "h")) },
        };
    }

    private static JAnim.CharFn Glyphs(Env env, JAnim.CharFn fn)
    {
        env.CharFns.Add(fn); // JS addC：管线侧 CombineChar 折叠合并
        return fn;
    }

    /// <summary>
    /// withCopies 的对位：主绘制走 mainFn（推 env.CharFns，管线在主文字上生效）；
    /// 每个 copyFn 在就地 post 阶段画一份 item 副本（管线未挂 post，层级在主文字下方）。
    /// </summary>
    private static void WithCopies(Env env, JObject it, JAnim.CharFn mainFn, IReadOnlyList<JAnim.CharFn> copyFns, JObject? extra = null)
    {
        var prev = env.CharFns.ToList();
        Glyphs(env, mainFn);
        foreach (var f in copyFns)
        {
            var cf = JAnim.CombineChar(prev.Concat(new[] { f }).ToList());
            DrawCopy(env, it, cf, c =>
            {
                c["streak"] = null;
                c["echo"] = null;
                if (extra != null)
                    foreach (var pp in extra.Properties())
                        c[pp.Name] = pp.Value;
            });
        }
    }

    /// <summary>JS J.drawItem 直调的副本：DeepClone + 置换 charFn/pieceFn（P11Enter.DrawIt 同型）。</summary>
    private static BBox? DrawCopy(Env env, JObject src, JAnim.CharFn? charFn, Action<JObject>? mutate = null)
    {
        var c = (JObject)src.DeepClone();
        mutate?.Invoke(c);
        var prevC = env.CharFn;
        var prevP = env.PieceFn;
        env.CharFn = charFn;
        env.PieceFn = null;
        try
        {
            return JText.DrawItem(env, c, new JText.TextDrawOpts
            {
                HideText = env.HideText,
                GlyphLog = env.GlyphLog,
                AllowFilter = env.AllowFilter,
                NoPieces = true,
            });
        }
        finally
        {
            env.CharFn = prevC;
            env.PieceFn = prevP;
        }
    }

    /// <summary>JS J.drawFx 直调的副本：走真管线 DrawFx（bands/vbands/clip 副本消费），外挂任意路径裁剪。</summary>
    private static BBox? DrawFxCopy(Env env, JObject src, JAnim.CharFn? charFn, SKPath? clip, Action<JObject>? mutate = null)
    {
        var c = (JObject)src.DeepClone();
        mutate?.Invoke(c);
        var prevC = env.CharFn;
        var prevP = env.PieceFn;
        env.CharFn = charFn;
        env.PieceFn = null;
        try
        {
            if (clip != null)
            {
                env.Ctx.Save();
                env.Ctx.ClipPath(clip, SKClipOperation.Intersect, true);
            }
            try
            {
                return JLayouts.DrawFx(env, c);
            }
            finally
            {
                if (clip != null) env.Ctx.Restore();
            }
        }
        finally
        {
            env.CharFn = prevC;
            env.PieceFn = prevP;
        }
    }

    /// <summary>当前 item 的合并 charFn（JS post 阶段 it2.charFn 的对位；管线在 Draw 返回后才写入 env.CharFn）。</summary>
    private static JAnim.CharFn? MergedNow(Env env) => env.CharFns.Count == 0 ? null : JAnim.CombineChar(env.CharFns);

    /// <summary>在 item 自己的坐标系里画，含旋转。</summary>
    private static void InItem(Env env, JObject it, Action fn)
    {
        env.Ctx.Save();
        env.Ctx.Translate((float)N0(it, "x"), (float)N0(it, "y"));
        var rot = N0(it, "rot");
        if (rot != 0) env.Ctx.RotateDegrees((float)rot);
        try
        {
            fn();
        }
        finally
        {
            env.Ctx.Restore();
        }
    }

    private sealed class LnExt
    {
        public int Li;
        public double X0, X1, Y0, Y1;
    }

    /// <summary>每行（竖排为每列）在 item 空间的范围。</summary>
    private static List<LnExt> LineExt(JObject it)
    {
        var lay = JText.LayoutText(it);
        var sx = N1(it, "sx");
        var sy = N1(it, "sy");
        var size = N0(it, "size");
        var vert = B(it, "vertical");
        var map = new Dictionary<int, LnExt>();
        foreach (var g in lay.Cells)
        {
            if (IsSp(g.Ch)) continue;
            if (!map.TryGetValue(g.Li, out var l)) l = map[g.Li] = new LnExt { Li = g.Li, X0 = 1e9, X1 = -1e9, Y0 = 1e9, Y1 = -1e9 };
            var hw = (vert ? size : g.W) / 2;
            var hh = (vert ? g.H : size) / 2;
            l.X0 = Math.Min(l.X0, (g.X - hw) * sx);
            l.X1 = Math.Max(l.X1, (g.X + hw) * sx);
            l.Y0 = Math.Min(l.Y0, (g.Y - hh) * sy);
            l.Y1 = Math.Max(l.Y1, (g.Y + hh) * sy);
        }
        return map.Values.OrderBy(l => l.Li).ToList();
    }

    private sealed class Dbx
    {
        public double X0, Y0, X1, Y1, Cx, Cy, W, H;
    }

    /// <summary>旋转后的 item box 求设计空间 AABB，含 pad 与 skew 让位。</summary>
    private static Dbx DBox(JObject it, double pad = 0)
    {
        var b = Box(it);
        double x0 = 1e9, y0 = 1e9, x1 = -1e9, y1 = -1e9;
        foreach (var (px, py) in new[] { (b.X0, b.Y0), (b.X1, b.Y0), (b.X0, b.Y1), (b.X1, b.Y1) })
        {
            var p = ToD(it, px - N0(it, "x"), py - N0(it, "y"));
            x0 = Math.Min(x0, p[0]);
            x1 = Math.Max(x1, p[0]);
            y0 = Math.Min(y0, p[1]);
            y1 = Math.Max(y1, p[1]);
        }
        var sk = Math.Abs(Math.Tan(N0(it, "skew") * J.DEG)) * b.H * 0.5;
        return new Dbx
        {
            X0 = x0 - pad - sk,
            Y0 = y0 - pad,
            X1 = x1 + pad + sk,
            Y1 = y1 + pad,
            Cx = (x0 + x1) / 2,
            Cy = (y0 + y1) / 2,
        };
    }

    private sealed class Lbx
    {
        public double X0, Y0, X1, Y1, Cx, Cy, W, H;
    }

    /// <summary>item 空间的 box，相对 it.x/it.y，不旋转。</summary>
    private static Lbx LBox(JObject it, double pad = 0)
    {
        var b = Box(it);
        return new Lbx
        {
            X0 = b.X0 - N0(it, "x") - pad,
            Y0 = b.Y0 - N0(it, "y") - pad,
            X1 = b.X1 - N0(it, "x") + pad,
            Y1 = b.Y1 - N0(it, "y") + pad,
            Cx = b.Cx - N0(it, "x"),
            Cy = b.Cy - N0(it, "y"),
            W = b.W + pad * 2,
            H = b.H + pad * 2,
        };
    }

    /// <summary>以 item 空间给定的锚点缩放 sx/sy，不给锚点就用 box 中心。</summary>
    private static void ScaleAbout(JObject it, double kx, double ky, double? ax = null, double? ay = null)
    {
        double axx, ayy;
        if (ax == null)
        {
            var b = Box(it);
            axx = b.Cx - N0(it, "x");
            ayy = b.Cy - N0(it, "y");
        }
        else
        {
            axx = ax.Value;
            ayy = ay!.Value;
        }
        var d = Rot2(axx * (1 - kx), ayy * (1 - ky), N0(it, "rot") * J.DEG);
        it["x"] = N0(it, "x") + d[0];
        it["y"] = N0(it, "y") + d[1];
        it["sx"] = N1(it, "sx") * kx;
        it["sy"] = N1(it, "sy") * ky;
    }

    /// <summary>以 box 中心缩放字号，位置、字距、字形都跟着。</summary>
    private static void SizeAbout(JObject it, double k)
    {
        var b = Box(it);
        var ax = b.Cx - N0(it, "x");
        var ay = b.Cy - N0(it, "y");
        var d = Rot2(ax * (1 - k), ay * (1 - k), N0(it, "rot") * J.DEG);
        it["x"] = N0(it, "x") + d[0];
        it["y"] = N0(it, "y") + d[1];
        it["size"] = N0(it, "size") * k;
    }

    /// <summary>绕 item 空间一点旋转 {x, y, rot}。</summary>
    private static void RotateAbout(JObject it, double deg, double ax, double ay)
    {
        var r0 = N0(it, "rot") * J.DEG;
        var r1 = r0 + deg * J.DEG;
        var p0 = Rot2(ax, ay, r0);
        var p1 = Rot2(ax, ay, r1);
        it["x"] = N0(it, "x") + p0[0] - p1[0];
        it["y"] = N0(it, "y") + p0[1] - p1[1];
        it["rot"] = N0(it, "rot") + deg;
    }

    /// <summary>tearOut 的半边状态（JS 的普通对象 {x, y, rot}）。</summary>
    private sealed class HState
    {
        public double X, Y, Rot;
    }

    private static void RotateAboutH(HState h, double deg, double ax, double ay)
    {
        var r0 = h.Rot * J.DEG;
        var r1 = r0 + deg * J.DEG;
        var p0 = Rot2(ax, ay, r0);
        var p1 = Rot2(ax, ay, r1);
        h.X += p0[0] - p1[0];
        h.Y += p0[1] - p1[1];
        h.Rot += deg;
    }

    // env.beat {since,len,index} 由渲染器每帧挂 Env（BeatHas/BeatSince/BeatLen/BeatIndex，09_render beatAt）；
    // BeatHas=false（plan 无节拍）回 JS 的 ltb 回退式（JS beatLen/beatSince/beatIdx 对位）
    private static double BeatLen(Env env, double fb) => env.BeatHas && env.BeatLen > 0 ? env.BeatLen : fb;
    private static double BeatSince(Env env, double fb) => env.BeatHas ? env.BeatSince : ((env.Ltb % fb) + fb) % fb;
    private static int BeatIdx(Env env, double fb) => env.BeatHas ? env.BeatIndex : (int)Math.Floor(env.Ltb / fb);

    /// <summary>JS J.rgba(h, a) → "rgba(r,g,b,a)"（shadow.color 吃这个格式）。</summary>
    private static string Rgba(string hex, double a)
    {
        var (r, g, b) = J.Hex(hex);
        return "rgba(" + r + "," + g + "," + b + "," + a + ")";
    }

    // 松散读取（对齐 JS 语义）：(x||0)/(x||1)/(x??d)
    private static string? S(JObject o, string k) => JLayouts.S(o, k);
    private static double D(JObject o, string k, double d = 0) => JLayouts.D(o, k, d);
    private static bool B(JObject o, string k) => JLayouts.B(o, k);
    private static double Num(JObject o, string k) => JAnim.Num(o, k);
    private static double N0(JObject o, string k) => JAnim.Num(o, k);
    private static double NN(JObject o, string k, double d) => JAnim.NumNull(o, k, d);
    private static double N1(JObject o, string k) => JAnim.NumOr1(o, k);
    private static int SeedOf(JObject o) => JAnim.SeedOf(o);

    // ================= 注册（JS X/H 字面量序，Object.keys 语义） =================

    /// <summary>exit 条目统一包一层 JS 的兜底：p≈1 时不管配方留下什么，一定完全消失。</summary>
    private static void Reg1(string key, MoveDef def)
    {
        var f = def.Draw;
        def.Draw = env =>
        {
            if (env.Ap >= 0.998)
            {
                env.It!["alpha"] = 0.0;
                return;
            }
            f?.Invoke(env);
        };
        Reg.Register("exit", key, def, P);
    }

    private static void RegH(string key, MoveDef def) => Reg.Register("hold", key, def, P);

    private const string POOL = "※◆◇■□▲△▼●○◎＃＊＋×÷＝≠∞§†01／＼＜＞";

    /// <summary>幂等注册本文件全部 exit/hold 条目（顺序 = JS 源 X/H 字面量序；不挂静态构造函数）。</summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;
        SinkMask();        // X.sinkMask
        RiseOut();         // X.riseOut
        SlideOutL();       // X.slideOutL
        SlideOutR();       // X.slideOutR
        FlipOutX();        // X.flipOutX
        FlipOutY();        // X.flipOutY
        FoldOut();         // X.foldOut
        Squash();          // X.squash
        TrackOutWide();    // X.trackOutWide
        Collapse();        // X.collapse
        ZoomThrough();     // X.zoomThrough
        ZoomFar();         // X.zoomFar
        SpinOut();         // X.spinOut
        Twist();           // X.twist
        WaveOut();         // X.waveOut
        BlurOutStagger();  // X.blurOutStagger
        Undraw();          // X.undraw
        OutlineOut();      // X.outlineOut
        IrisClose();       // X.irisClose
        DiagWipeOut();     // X.diagWipeOut
        BlindsClose();     // X.blindsClose
        CheckerOut();      // X.checkerOut
        SplitApart();      // X.splitApart
        VSliceDrop();      // X.vSliceDrop
        Melt();            // X.melt
        Dissolve();        // X.dissolve
        Backspace();       // X.backspace
        ScrambleOut();     // X.scrambleOut
        GlitchDissolve();  // X.glitchDissolve
        EchoOut();         // X.echoOut
        WhipOut();         // X.whipOut
        Gravity();         // X.gravity
        PopOut();          // X.popOut
        Burn();            // X.burn
        SweepCover();      // X.sweepCover
        ShatterLite();     // X.shatterLite
        Float();           // H.float
        Sway();            // H.sway
        Pulse();           // H.pulse
        Shimmer();         // H.shimmer
        ColorRun();        // H.colorRun
        RotateSlow();      // H.rotateSlow
        TrackBreathe();    // H.trackBreathe
        SkewWobble();      // H.skewWobble
        BeatHop();         // H.beatHop
        HWave();           // H.hWave
        Heartbeat();       // H.heartbeat
        OrbitSmall();      // H.orbitSmall
        Jelly();           // H.jelly
        ScanBand();        // H.scanBand
        NoiseDrift();      // H.noiseDrift
        Tilt();            // H.tilt
        ZoomSlow();        // H.zoomSlow
        StretchPulse();    // H.stretchPulse
        GlitchJump();      // H.glitchJump
        EchoTrail();       // H.echoTrail
    }

    // ---------------- sinkMask（X.sinkMask） ----------------

    private static void SinkMask() => Reg1("sinkMask", new MoveDef
    {
        Name = "沈む",
        Tags = new[] { "calm", "editorial", "graphic" },
        W = 1,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var ord = OrderOf(env, it);
            var sy = N1(it, "sy");
            Glyphs(env, (i, g, n) =>
            {
                if (g == null) return null;
                var q = Win(p, ord(i, n), 0.5);
                if (q <= 0) return null;
                if (q >= 1) return new CharXf { Hide = true };
                // 先微微上抬，再沉到自身基线以下
                var d = (J.E.InOutCubic(q) - 0.06 * Math.Sin(Math.PI * J.Clamp(q / 0.3))) * D(g, "h") * sy * 1.4;
                var o = WinShift(g, it, 0, d);
                o.Dy = d;
                return o;
            });
        },
    });

    // ---------------- riseOut（X.riseOut） ----------------

    private static void RiseOut() => Reg1("riseOut", new MoveDef
    {
        Name = "上へ抜ける",
        Tags = new[] { "calm", "emotional", "editorial" },
        W = 1,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var seed = SeedOf(it);
            var sy = N1(it, "sy");
            Glyphs(env, (i, g, _) =>
            {
                if (g == null) return null;
                var q = Win(p, J.R(seed, i, 201), 0.5);
                if (q <= 0) return null;
                if (q >= 1) return new CharXf { Hide = true };
                var d = -Math.Pow(q, 2.2) * D(g, "h") * sy * 1.5;
                var st = 1 + 0.5 * Math.Sin(Math.PI * q) * q; // 离场时的速度拉伸
                var o = WinShift(g, it, 0, d, 1, st);
                o.Dy = d;
                o.Sy = st;
                o.Sx = 1 - 0.1 * (st - 1);
                return o;
            });
        },
    });

    // ---------------- slideOutL / slideOutR（X.slideOut*） ----------------

    private static MoveDef SlideOutDef(int dir, string name) => new()
    {
        Name = name,
        Tags = new[] { "pop", "graphic" },
        W = 0.8,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var ord = OrderOf(env, it);
            var b = Box(it);
            var dist = env.W + b.W + N0(it, "size") * 1.5;
            var sz = N0(it, "size");
            Glyphs(env, (i, _, n) =>
            {
                var o = dir < 0 ? ord(i, n) : 1 - ord(i, n); // 前缘先走
                var q = Win(p, o, 0.5);
                if (q <= 0) return null;
                var wind = q < 0.24 ? Math.Sin(q / 0.24 * Math.PI) * sz * 0.07 : 0;
                var u = J.Clamp((q - 0.1) / 0.9);
                var v = 3 * u * u;
                return new CharXf
                {
                    Dx = dir * (dist * u * u * u - wind),
                    Sx = 1 + Math.Min(1.3, v * 0.45),
                    Sy = 1 - Math.Min(0.28, v * 0.09),
                    Skew = -dir * Math.Min(22, v * 8),
                    A = 1 - J.Smooth(0.82, 1, q),
                };
            });
        },
    };

    private static void SlideOutL() => Reg1("slideOutL", SlideOutDef(-1, "左へ流れる"));
    private static void SlideOutR() => Reg1("slideOutR", SlideOutDef(1, "右へ流れる"));

    // ---------------- flipOutX（X.flipOutX） ----------------

    private static void FlipOutX() => Reg1("flipOutX", new MoveDef
    {
        Name = "扉が閉まる",
        Tags = new[] { "graphic", "pop", "editorial" },
        W = 1,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var ord = OrderOf(env, it);
            var sx0 = N1(it, "sx");
            var c0 = ColOf(it);
            var bg = ScK(env, "bg") ?? "#ffffff";
            var dir = CutBit(env, 1) ? 1 : -1; // 铰链在左还是右
            Glyphs(env, (i, g, n) =>
            {
                if (g == null) return null;
                var q = Win(p, ord(i, n), 0.5);
                if (q <= 0) return null;
                if (q >= 1) return new CharXf { Hide = true };
                var th = J.E.InQuad(q) * Math.PI / 2;
                var c = Math.Cos(th);
                var s = Math.Sin(th);
                return new CharXf
                {
                    Sx = c,
                    Sy = 1 + 0.14 * s,
                    Dx = dir * (1 - c) * D(g, "w") * sx0 * 0.5,
                    Color = MixC(c0, bg, s * 0.55),
                    A = 1 - J.Smooth(0.8, 1, q),
                };
            });
        },
    });

    // ---------------- flipOutY（X.flipOutY） ----------------

    private static void FlipOutY() => Reg1("flipOutY", new MoveDef
    {
        Name = "パタン倒れ",
        Tags = new[] { "pop", "graphic" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var seed = SeedOf(it);
            var sy0 = N1(it, "sy");
            var c0 = ColOf(it);
            var bg = ScK(env, "bg") ?? "#ffffff";
            Glyphs(env, (i, g, _) =>
            {
                if (g == null) return null;
                var q = Win(p, J.R(seed, i, 211), 0.5);
                if (q <= 0) return null;
                if (q >= 1) return new CharXf { Hide = true };
                var th = J.E.InQuad(q) * Math.PI / 2;
                var c = Math.Cos(th);
                var s = Math.Sin(th);
                // 以底边为铰链，向后倒平贴回基线
                return new CharXf
                {
                    Sy = c,
                    Sx = 1 - 0.12 * s,
                    Dy = (1 - c) * D(g, "h") * sy0 * 0.5,
                    Color = MixC(c0, bg, s * 0.6),
                };
            });
        },
    });

    // ---------------- foldOut（X.foldOut） ----------------

    private static void FoldOut() => Reg1("foldOut", new MoveDef
    {
        Name = "折り畳み",
        Tags = new[] { "graphic", "editorial" },
        W = 0.9,
        OutDur = (dur, _) => J.Clamp(dur * 0.34, 0.28, 0.65),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var ord = OrderOf(env, it);
            var back = MixC(ColOf(it), ScK(env, "accent") ?? "#ffffff", 0.75);

            double Q(int i, int n) => Win(p, ord(i, n), 0.3);
            CharXf? Bottom(int i, JObject? g, int n)
            {
                var q = Q(i, n);
                if (q >= 1) return new CharXf { Hide = true };
                var b = J.Clamp((q - 0.5) / 0.5);
                return new CharXf
                {
                    ClipY = new[] { 0.0, 1.2 },
                    Sy = Math.Cos(J.E.InQuad(b) * Math.PI / 2),
                    A = 1 - J.Smooth(0.85, 1, q),
                };
            }
            CharXf? Top(int i, JObject? g, int n)
            {
                var q = Q(i, n);
                if (q >= 1) return new CharXf { Hide = true };
                var a = J.E.InOutSine(J.Clamp(q / 0.5));
                var b = J.Clamp((q - 0.5) / 0.5);
                var sy = Math.Cos(a * Math.PI) * Math.Cos(J.E.InQuad(b) * Math.PI / 2); // 从 1 折过 -1 再收到 0
                return new CharXf
                {
                    ClipY = new[] { -1.2, 0.012 },
                    Sy = sy,
                    Color = sy < 0 ? back : null,
                    A = 1 - J.Smooth(0.85, 1, q),
                };
            }
            WithCopies(env, it, Bottom, new[] { (JAnim.CharFn)Top });
        },
    });

    // ---------------- squash（X.squash） ----------------

    private static void Squash() => Reg1("squash", new MoveDef
    {
        Name = "潰れる",
        Tags = new[] { "glitch", "pop", "graphic" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var b0 = Box(it);
            var sz = N0(it, "size");
            var col = ColOf(it);
            var nCut = CutN(env);
            var single = LayOf(it).N == 1 && nCut > 1;
            var a1 = J.Clamp(p / 0.42);
            var a2 = J.Clamp((p - 0.4) / 0.45);
            var ky = 1 - 0.97 * J.E.InCubic(a1);
            var kx = (1 + 0.08 * J.E.OutCubic(a1)) * (1 - J.E.InOutCubic(a2));
            if (single)
            {
                // 单字 item：整条向屏幕中心塌缩
                it["x"] = J.Lerp(N0(it, "x"), env.W / 2.0, J.E.InOutCubic(a2));
                ScaleAbout(it, 1 + 0.08 * J.E.OutCubic(a1), ky);
                it["alpha"] = NN(it, "alpha", 1) * (1 - J.Smooth(0.7, 0.9, p));
            }
            else if (kx > 0.01) ScaleAbout(it, kx, ky);
            else it["alpha"] = 0.0;
            // CRT 关机：塌缩完留一个渐隐的亮点
            var c = single ? new[] { env.W / 2.0, N0(it, "y") } : ToD(it, b0.Cx - N0(it, "x"), b0.Cy - N0(it, "y"));
            var dotA = J.Smooth(0.6, 0.8, p) * (1 - J.Smooth(0.88, 1, p));
            if (dotA > 0.01 && (!single || Math.Round(Num(it, "mi")) == Math.Floor((nCut - 1) / 2.0)))
            {
                var r = sz * 0.08;
                // JS chainPost——就地执行（层级在主文字下方）
                env.Circle(c[0], c[1], r, col, null, 0, dotA, true);
                env.Rect(c[0] - r * 5 * (1 - a2 * 0.5), c[1] - r * 0.12, r * 10 * (1 - a2 * 0.5), r * 0.24, col, dotA * 0.8, false);
            }
        },
    });

    // ---------------- trackOutWide（X.trackOutWide） ----------------

    private static void TrackOutWide() => Reg1("trackOutWide", new MoveDef
    {
        Name = "字間が開く",
        Tags = new[] { "calm", "emotional", "editorial" },
        W = 1,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var b = Box(it);
            var ox = b.Cx - N0(it, "x");
            var oy = b.Cy - N0(it, "y");
            var sx = N1(it, "sx");
            var sy = N1(it, "sy");
            var vert = B(it, "vertical");
            var k = J.E.InQuad(p) * 1.6 + p * 0.3;
            var thin = 1 - 0.6 * J.E.InQuad(p);
            Glyphs(env, (i, g, _) =>
            {
                if (g == null) return null;
                return vert
                    ? new CharXf { Dy = (D(g, "y") * sy - oy) * k, Sy = thin }
                    : new CharXf { Dx = (D(g, "x") * sx - ox) * k, Sx = thin };
            });
            if (LayOf(it).N == 1 && CutN(env) > 1)
            {
                // 单字 item 从屏幕中心向外散开
                it["x"] = N0(it, "x") + (N0(it, "x") - env.W / 2.0) * k * 0.7;
                it["y"] = N0(it, "y") + (N0(it, "y") - env.H / 2.0) * k * 0.4;
            }
            it["alpha"] = NN(it, "alpha", 1) * (1 - J.Smooth(0.2, 1, p));
            if (env.Pass == "main")
                it["blur"] = N0(it, "blur") + Math.Min(J.E.InQuad(p) * N0(it, "size") * 0.035, Math.Min(env.W, env.H) * 0.012);
        },
    });

    // ---------------- collapse（X.collapse） ----------------

    private static void Collapse() => Reg1("collapse", new MoveDef
    {
        Name = "吸い込み",
        Tags = new[] { "pop", "graphic" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var dir = CutBit(env, 2) ? 1 : -1;
            var r = 1 - J.E.InQuad(p);
            var turn = dir * J.E.InQuad(p) * J.DEG;
            var sc = Math.Max(0.02, 1 - J.E.InQuad(p) * 0.94);
            // 差速旋转，靠里的部分转得快，拧出螺旋吸向中心
            double[] Spiral(double px, double py, double rr)
            {
                var phi = turn * (140 + 220 * (1 - J.Clamp(Math.Sqrt(px * px + py * py) / rr)));
                var c = Math.Cos(phi);
                var s = Math.Sin(phi);
                return new[] { (px * c - py * s) * r, (px * s + py * c) * r, phi };
            }
            if (LayOf(it).N == 1 && CutN(env) > 1)
            {
                // 单字 item 整体旋进屏幕中心
                var px = N0(it, "x") - env.W / 2.0;
                var py = N0(it, "y") - env.H / 2.0;
                var res = Spiral(px, py, Math.Max(env.W, env.H) * 0.5);
                it["x"] = env.W / 2.0 + res[0];
                it["y"] = env.H / 2.0 + res[1];
                it["rot"] = N0(it, "rot") + res[2] / J.DEG;
                it["size"] = N0(it, "size") * sc;
            }
            else
            {
                var b = Box(it);
                var ox = b.Cx - N0(it, "x");
                var oy = b.Cy - N0(it, "y");
                var sx = N1(it, "sx");
                var sy = N1(it, "sy");
                var rr = Math.Max(1, Math.Sqrt(b.W * b.W + b.H * b.H) * 0.5);
                Glyphs(env, (i, g, _) =>
                {
                    if (g == null) return null;
                    var px = D(g, "x") * sx - ox;
                    var py = D(g, "y") * sy - oy;
                    var res = Spiral(px, py, rr);
                    return new CharXf { Dx = res[0] - px, Dy = res[1] - py, Rot = res[2] / J.DEG, S = sc };
                });
            }
            it["alpha"] = NN(it, "alpha", 1) * (1 - J.Smooth(0.8, 1, p));
        },
    });

    // ---------------- zoomThrough（X.zoomThrough） ----------------

    private static void ZoomThrough() => Reg1("zoomThrough", new MoveDef
    {
        Name = "手前へ抜ける",
        Tags = new[] { "emotional", "pop" },
        W = 1,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var sz0 = N0(it, "size");
            var single = LayOf(it).N == 1 && CutN(env) > 1;
            var maxK = Math.Max(1.3, Math.Max(env.W, env.H) * (single ? 0.8 : 1.6) / Math.Max(1, sz0));
            var k = Math.Min(maxK, 1 + 4.5 * J.E.InCubic(p) + 0.25 * p);
            if (single)
            {
                var kk = 1 + 4.5 * J.E.InCubic(p) + 0.25 * p; // JS 原样：single 分支用未夹 maxK 的 kk
                it["x"] = env.W / 2.0 + (N0(it, "x") - env.W / 2.0) * kk;
                it["y"] = env.H / 2.0 + (N0(it, "y") - env.H / 2.0) * kk;
            }
            SizeAbout(it, k);
            var a = 1 - J.Smooth(0.3, 0.88, p);
            it["alpha"] = NN(it, "alpha", 1) * a;
            if (a > 0.01)
                it["echo"] = new JObject { ["n"] = 3, ["dx"] = 0, ["dy"] = 0, ["scale"] = 0.84, ["a"] = 0.45 * J.Smooth(0, 0.25, p), ["decay"] = 0.62 };
        },
    });

    // ---------------- zoomFar（X.zoomFar） ----------------

    private static void ZoomFar() => Reg1("zoomFar", new MoveDef
    {
        Name = "奥へ遠ざかる",
        Tags = new[] { "emotional", "calm" },
        W = 1,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var sz0 = N0(it, "size");
            var e = 1 - Math.Pow(1 - p, 2.6); // 先快退，后慢停
            SizeAbout(it, 1 - 0.9 * e);
            if (LayOf(it).N == 1 && CutN(env) > 1)
            {
                it["x"] = J.Lerp(N0(it, "x"), env.W / 2.0, e * 0.85);
                it["y"] = J.Lerp(N0(it, "y"), env.H / 2.0, e * 0.85);
            }
            it["y"] = N0(it, "y") - sz0 * 0.35 * e; // 朝上方的消失点退
            it["alpha"] = NN(it, "alpha", 1) * (1 - J.Smooth(0.5, 1, p));
            it["echo"] = new JObject
            {
                ["n"] = 3,
                ["dx"] = 0,
                ["dy"] = sz0 * 0.06 * e,
                ["scale"] = 1.2,
                ["a"] = 0.6 * J.Smooth(0, 0.2, p),
                ["decay"] = 0.62,
                ["outline"] = true,
            };
        },
    });

    // ---------------- spinOut（X.spinOut） ----------------

    private static void SpinOut() => Reg1("spinOut", new MoveDef
    {
        Name = "回って消える",
        Tags = new[] { "pop" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var ord = OrderOf(env, it);
            var dir = CutBit(env, 3) ? 1 : -1;
            var sy = N1(it, "sy");
            Glyphs(env, (i, g, n) =>
            {
                if (g == null) return null;
                var q = Win(p, ord(i, n), 0.45);
                if (q <= 0) return null;
                if (q >= 1) return new CharXf { Hide = true };
                var e = J.E.InCubic(q);
                return new CharXf
                {
                    Rot = dir * (i % 2 != 0 ? 1 : 0.8) * 250 * J.E.InQuad(q),
                    S = Math.Max(0.01, 1 - e),
                    Dy = -Math.Sin(Math.PI * q) * D(g, "h") * sy * 0.18,
                    A = 1 - J.Smooth(0.8, 1, q),
                };
            });
        },
    });

    // ---------------- twist（X.twist） ----------------

    private static void Twist() => Reg1("twist", new MoveDef
    {
        Name = "ねじれ",
        Tags = new[] { "pop", "graphic" },
        W = 0.8,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var ord = OrderOf(env, it);
            var vert = B(it, "vertical");
            var back = MixC(ColOf(it), ScK(env, "bg") ?? "#ffffff", 0.5);
            var flat = 1 - J.E.InQuad(J.Clamp((p - 0.55) / 0.45)); // 丝带最后转到侧对视线
            var e = Math.Pow(p, 1.3);
            Glyphs(env, (i, g, n) =>
            {
                var th = e * (0.5 + 2 * ord(i, n)) * Math.PI; // 相位沿阅读轴渐开，形成扭转
                var c = Math.Cos(th) * flat;
                var o = new CharXf
                {
                    Color = c < 0 ? back : null,
                    Skew = vert ? 0 : Math.Sin(th) * 12 * flat,
                };
                if (vert) o.Sx = c;
                else o.Sy = c;
                return o;
            });
            it["alpha"] = NN(it, "alpha", 1) * (1 - J.Smooth(0.9, 1, p));
        },
    });

    // ---------------- waveOut（X.waveOut） ----------------

    private static void WaveOut() => Reg1("waveOut", new MoveDef
    {
        Name = "波で崩れる",
        Tags = new[] { "pop", "emotional" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var ord = OrderOf(env, it);
            var sz = N0(it, "size");
            Glyphs(env, (i, _, n) =>
            {
                var q = Win(p, ord(i, n), 0.55);
                if (q <= 0) return null;
                if (q >= 1) return new CharXf { Hide = true };
                var a = sz * 0.42;
                return new CharXf
                {
                    Dy = -Math.Sin(q * Math.PI) * a + q * q * a * 2.6,
                    Dx = q * a * 0.6,
                    Rot = Math.Sin(q * Math.PI * 1.5) * 28 + q * 30,
                    A = 1 - J.Smooth(0.45, 1, q),
                };
            });
        },
    });

    // ---------------- blurOutStagger（X.blurOutStagger） ----------------

    private static void BlurOutStagger() => Reg1("blurOutStagger", new MoveDef
    {
        Name = "字ごとボケ",
        Tags = new[] { "calm", "emotional" },
        W = 1,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var ord = OrderOf(env, it);
            var sz = N0(it, "size");
            // 只在 main pass 加模糊，ghost 副本直接淡出
            var bmax = env.Pass == "main" ? Math.Min(sz * 0.09, Math.Min(env.W, env.H) * 0.02) : 0;
            Glyphs(env, (i, _, n) =>
            {
                var q = Win(p, ord(i, n), 0.55);
                if (q <= 0) return null;
                if (q >= 1) return new CharXf { Hide = true };
                var e = J.E.InOutSine(q);
                return new CharXf { Blur = e * bmax, A = 1 - q, Dy = -e * sz * 0.12, S = 1 + e * 0.22 };
            });
        },
    });

    // ---------------- undraw（X.undraw） ----------------

    private static void Undraw() => Reg1("undraw", new MoveDef
    {
        Name = "線に戻る",
        Tags = new[] { "editorial", "calm", "graphic" },
        W = 1,
        OutDur = (dur, _) => J.Clamp(dur * 0.36, 0.25, 0.7),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var a = J.Clamp(p / 0.3);
            var b = J.Clamp((p - 0.24) / 0.76);
            it["stroke"] = Math.Max(N0(it, "stroke"), N0(it, "size") * 0.022);
            if (S(it, "strokeColor") == null) it["strokeColor"] = ColOf(it);
            it["fillAlpha"] = NN(it, "fillAlpha", 1) * (1 - J.E.InOutSine(a));
            if (b > 0) it["dash"] = Math.Max(0.002, 1 - J.E.InOutSine(b));
            it["alpha"] = NN(it, "alpha", 1) * (1 - J.Smooth(0.9, 1, p));
        },
    });

    // ---------------- outlineOut（X.outlineOut） ----------------

    private static void OutlineOut() => Reg1("outlineOut", new MoveDef
    {
        Name = "塗りが抜ける",
        Tags = new[] { "graphic", "emotional" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var ord = OrderOf(env, it);
            var lw = Math.Max(1, N0(it, "size") * 0.024);
            var acc = ScK(env, "accent") ?? "#ffffff";

            double Q(int i, int n) => Win(p, ord(i, n), 0.35);
            CharXf? Fill(int i, JObject? g, int n)
            {
                var q = Q(i, n);
                if (q <= 0) return null;
                var k = J.E.InOutSine(J.Clamp(q / 0.7));
                if (k >= 0.999) return new CharXf { Hide = true };
                // 填充像排空一样流走，液面从顶部往下掉
                return new CharXf { ClipY = new[] { -0.72 + 1.44 * k, 1.2 } };
            }
            CharXf? Line(int i, JObject? g, int n)
            {
                var q = Q(i, n);
                if (q <= 0 || q >= 1) return new CharXf { Hide = true };
                var u = J.Clamp((q - 0.68) / 0.32);
                return new CharXf { Outline = true, S = 1 + 0.3 * J.E.OutCubic(u), A = Math.Min(1, q * 8) * (1 - u) };
            }
            WithCopies(env, it, Fill, new[] { (JAnim.CharFn)Line }, new JObject
            {
                ["stroke"] = lw,
                ["strokeColor"] = acc,
                ["fill"] = true,
            });
        },
    });

    // ---------------- irisClose（X.irisClose） ----------------

    private static void IrisClose() => Reg1("irisClose", new MoveDef
    {
        Name = "アイリス",
        Tags = new[] { "graphic", "pop", "editorial" },
        W = 1,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var b = Box(it);
            var c = ToD(it, b.Cx - N0(it, "x"), b.Cy - N0(it, "y"));
            var r0 = Math.Sqrt(b.W * b.W + b.H * b.H) * 0.5 + N0(it, "size") * 0.12;
            var r = r0 * (1 - J.E.InOutCubic(p));
            if (r < 0.5)
            {
                it["alpha"] = 0.0;
                return;
            }
            // JS clipFn：moveTo + arc 的整圆路径 → Env.ClipFn 桥（DrawFx ClipPath）
            env.ClipFn = (_, _) =>
            {
                var path = new SKPath();
                path.AddCircle((float)c[0], (float)c[1], (float)r);
                return path;
            };
            var lw = Math.Max(1.5, N0(it, "size") * 0.035);
            var acc = ScK(env, "accent") ?? "#ffffff";
            var a = Math.Min(1, p * 6) * (1 - J.Smooth(0.9, 1, p));
            // JS chainPost——就地执行
            env.Circle(c[0], c[1], r + lw * 0.5, null, acc, lw, a, false);
        },
    });

    // ---------------- diagWipeOut（X.diagWipeOut） ----------------

    private static void DiagWipeOut() => Reg1("diagWipeOut", new MoveDef
    {
        Name = "斜めワイプ",
        Tags = new[] { "graphic", "editorial" },
        W = 1,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            // 每个 cut 只有一条贯穿屏幕的斜边，方向来自 cut seed，同一 cut 的 item 被同一条线擦
            var bb = DBox(it, N0(it, "size") * 0.15);
            var seed = unchecked((int)(env.Cut?.Seed ?? 0));
            var v = (int)((uint)J.H(seed, 77) >> 3) & 3;
            var w = (double)env.W;
            var h = (double)env.H;
            var ang = new[] { 22.0, -22.0, 158.0, 202.0 }[v] * J.DEG;
            var d = new[] { Math.Cos(ang), Math.Sin(ang) };
            var t = new[] { -d[1], d[0] };
            double[,] cs = { { bb.X0, bb.Y0 }, { bb.X1, bb.Y0 }, { bb.X0, bb.Y1 }, { bb.X1, bb.Y1 }, { 0, 0 }, { w, 0 }, { 0, h }, { w, h } };
            var us = new double[8];
            var ws = new double[4];
            for (var i = 0; i < 8; i++)
            {
                us[i] = cs[i, 0] * d[0] + cs[i, 1] * d[1];
                if (i >= 4) ws[i - 4] = cs[i, 0] * t[0] + cs[i, 1] * t[1];
            }
            var u0 = us.Min();
            var u1 = us.Max();
            var w0 = ws.Min();
            var w1 = ws.Max();
            var th = Math.Max(2, Math.Min(w, h) * 0.011);
            var s = J.Lerp(u0 - th, u1 + th, J.E.InOutCubic(p));
            if (p >= 0.999)
            {
                it["alpha"] = 0.0;
                return;
            }
            // JS clipFn：保留侧的半平面四边形（pt(u,w) 四角，L=(W+H)*3 的超长边）→ Env.ClipFn 桥
            var l = (w + h) * 3;
            double[] Pt(double u, double w2) => new[] { d[0] * u + t[0] * w2, d[1] * u + t[1] * w2 };
            env.ClipFn = (_, _) =>
            {
                var a = Pt(s, w0 - l);
                var b2 = Pt(s, w1 + l);
                var c2 = Pt(s + l, w1 + l);
                var e = Pt(s + l, w0 - l);
                var path = new SKPath();
                path.MoveTo((float)a[0], (float)a[1]);
                path.LineTo((float)b2[0], (float)b2[1]);
                path.LineTo((float)c2[0], (float)c2[1]);
                path.LineTo((float)e[0], (float)e[1]);
                path.Close();
                return path;
            };
            if (Math.Abs(Num(it, "mi")) < 1e-6)
            {
                // 色条只画一次，由第一个 item 画（JS chainPost——就地执行）
                var acc = ScK(env, "accent") ?? "#ffffff";
                var pad = Math.Max(w, h) * 0.1;
                env.Poly(new[]
                {
                    new[] { d[0] * (s - th) + t[0] * (w0 - pad), d[1] * (s - th) + t[1] * (w0 - pad) },
                    new[] { d[0] * s + t[0] * (w0 - pad), d[1] * s + t[1] * (w0 - pad) },
                    new[] { d[0] * s + t[0] * (w1 + pad), d[1] * s + t[1] * (w1 + pad) },
                    new[] { d[0] * (s - th) + t[0] * (w1 + pad), d[1] * (s - th) + t[1] * (w1 + pad) },
                }, acc, 1, false);
            }
        },
    });

    // ---------------- blindsClose（X.blindsClose） ----------------

    private static void BlindsClose() => Reg1("blindsClose", new MoveDef
    {
        Name = "ブラインド",
        Tags = new[] { "graphic", "editorial" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var bb = DBox(it, N0(it, "size") * 0.2);
            var vert = B(it, "vertical");
            var span = vert ? bb.X1 - bb.X0 : bb.Y1 - bb.Y0;
            var cnt = J.Clamp((int)Math.Ceiling(span / Math.Max(4, N0(it, "size") * 0.26)), 2, 90);
            var pitch = span / cnt;
            var hs = new double[cnt];
            var any = false;
            for (var j = 0; j < cnt; j++)
            {
                var h = pitch * (1 - Math.Pow(Win(p, cnt > 1 ? j / (double)(cnt - 1) : 0, 0.4), 0.85));
                hs[j] = h;
                if (h > 0.05) any = true;
            }
            if (!any)
            {
                it["alpha"] = 0.0;
                return;
            }
            // JS clipFn：百叶条纹并集（逐条 ctx.rect）→ Env.ClipFn 桥
            var bbY0 = bb.Y0;
            var bbX0 = bb.X0;
            env.ClipFn = (_, _) =>
            {
                var path = new SKPath();
                for (var j = 0; j < cnt; j++)
                {
                    var h2 = hs[j];
                    if (h2 <= 0.05) continue;
                    if (vert)
                    {
                        var laneX0 = bbX0 + j * pitch + (pitch - h2) / 2;
                        path.AddRect(new SKRect((float)laneX0, (float)bbY0, (float)(laneX0 + h2), (float)bb.Y1));
                    }
                    else
                    {
                        var laneY0 = bbY0 + j * pitch + (pitch - h2) / 2;
                        path.AddRect(new SKRect((float)bb.X0, (float)laneY0, (float)bb.X1, (float)(laneY0 + h2)));
                    }
                }
                return path;
            };
        },
    });

    // ---------------- checkerOut（X.checkerOut） ----------------

    private static void CheckerOut() => Reg1("checkerOut", new MoveDef
    {
        Name = "市松",
        Tags = new[] { "graphic", "glitch" },
        W = 0.8,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var bb = DBox(it, N0(it, "size") * 0.15);
            var w = bb.X1 - bb.X0;
            var h = bb.Y1 - bb.Y0;
            var cell = Math.Max(4, N0(it, "size") * 0.26);
            while (w / cell * (h / cell) > 420) cell *= 1.25;
            var nx = (int)Math.Ceiling(w / cell);
            var ny = (int)Math.Ceiling(h / cell);
            var dg = Math.Max(1, nx + ny - 2);
            var rs = new List<double[]>();
            var any = false;
            for (var y = 0; y < ny; y++)
                for (var x = 0; x < nx; x++)
                {
                    var stt = ((x + y) & 1) * 0.42 + (x + y) / (double)dg * 0.3;
                    var k = 1 - J.E.InQuad(J.Clamp((p - stt) / 0.28));
                    if (k > 0.02)
                    {
                        any = true;
                        rs.Add(new[] { bb.X0 + (x + 0.5) * cell, bb.Y0 + (y + 0.5) * cell, cell * k + (k > 0.98 ? 0.8 : 0) });
                    }
                }
            if (!any)
            {
                it["alpha"] = 0.0;
                return;
            }
            // JS clipFn：格子并集（逐格 ctx.rect）→ Env.ClipFn 桥
            env.ClipFn = (_, _) =>
            {
                var path = new SKPath();
                foreach (var r in rs)
                    path.AddRect(new SKRect((float)(r[0] - r[2] / 2), (float)(r[1] - r[2] / 2),
                        (float)(r[0] + r[2] / 2), (float)(r[1] + r[2] / 2)));
                return path;
            };
        },
    });

    // ---------------- splitApart（X.splitApart） ----------------

    private static void SplitApart() => Reg1("splitApart", new MoveDef
    {
        Name = "上下に割れる",
        Tags = new[] { "graphic", "pop" },
        W = 1,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var vert = B(it, "vertical");
            var sz = N0(it, "size");
            var crack = J.Clamp(p / 0.2);
            var u = J.Clamp((p - 0.16) / 0.84);
            var e = J.E.OutCubic(u);
            var off = crack * sz * 0.03 + sz * 0.75 * e; // 先裂出发丝缝，两半再弹开
            var a = 1 - J.Smooth(0.35, 1, u);
            var sq = 1 - 0.35 * e;

            JAnim.CharFn Half(int sgn) => (i, g, n) => vert
                ? new CharXf
                {
                    ClipX = sgn < 0 ? new[] { -1.6, 0.012 } : new[] { 0.0, 1.6 },
                    Dx = sgn * off,
                    Dy = sgn * off * 0.3,
                    Sx = sq,
                    A = a,
                }
                : new CharXf
                {
                    ClipY = sgn < 0 ? new[] { -1.6, 0.012 } : new[] { 0.0, 1.6 },
                    Dy = sgn * off,
                    Dx = sgn * off * 0.3,
                    Sy = sq,
                    A = a,
                };
            WithCopies(env, it, Half(-1), new[] { Half(1) });
            var fl = J.Clamp(p / 0.2) * (1 - J.Smooth(0.55, 1, p));
            if (fl > 0.02)
            {
                var lines = LineExt(it);
                var acc = ScK(env, "accent") ?? "#ffffff";
                var lw = Math.Max(1.5, sz * (0.02 + 0.05 * e));
                var pad = sz * (0.15 + 0.5 * e);
                // JS chainPost（inItem 裂缝亮条）——就地执行
                InItem(env, it, () =>
                {
                    foreach (var l in lines)
                    {
                        if (vert)
                        {
                            var x = (l.X0 + l.X1) / 2;
                            env.Rect(x - lw / 2, l.Y0 - pad, lw, l.Y1 - l.Y0 + pad * 2, acc, fl, false);
                        }
                        else
                        {
                            var y = (l.Y0 + l.Y1) / 2;
                            env.Rect(l.X0 - pad, y - lw / 2, l.X1 - l.X0 + pad * 2, lw, acc, fl, false);
                        }
                    }
                });
            }
        },
    });

    // ---------------- vSliceDrop（X.vSliceDrop） ----------------

    private static void VSliceDrop() => Reg1("vSliceDrop", new MoveDef
    {
        Name = "縦スライス落下",
        Tags = new[] { "graphic", "glitch" },
        W = 0.9,
        OutDur = (dur, _) => J.Clamp(dur * 0.34, 0.25, 0.62),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var bb = DBox(it, N0(it, "size") * 0.35);
            var seed = SeedOf(it);
            var w = bb.X1 - bb.X0;
            var n = J.Clamp((int)Math.Round(w / (N0(it, "size") * 0.24)), 4, 14);
            var dd = env.H - bb.Y0 + N0(it, "size");
            var @out = new JArray();
            for (var k = 0; k < n; k++)
            {
                var d0 = J.R(seed, k, 301) * 0.45;
                var q = J.Clamp((p - d0) / 0.55);
                var dy = (q * q * 1.05 - Math.Sin(Math.PI * J.Clamp(q / 0.25)) * 0.012) * dd;
                @out.Add(new JArray(bb.X0 + k * w / n, bb.X0 + (k + 1) * w / n + 0.6, dy));
            }
            it["vbands"] = @out;
            it["alpha"] = NN(it, "alpha", 1) * (1 - J.Smooth(0.95, 1, p));
        },
    });

    // ---------------- melt（X.melt） ----------------

    private static void Melt() => Reg1("melt", new MoveDef
    {
        Name = "溶ける",
        Tags = new[] { "emotional", "glitch" },
        W = 0.8,
        OutDur = (dur, _) => J.Clamp(dur * 0.4, 0.3, 0.8),
        MinDur = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var b0 = Box(it);
            var seed = SeedOf(it);
            var sz = N0(it, "size");
            ScaleAbout(it, 1 - 0.05 * p, 1 + 0.8 * J.E.InQuad(p), b0.Cx - N0(it, "x"), b0.Y0 - N0(it, "y")); // 向下拉伸，锚在顶部
            var bb = DBox(it, sz * 0.35);
            var w = bb.X1 - bb.X0;
            var n = J.Clamp((int)Math.Round(w / (sz * (LayOf(it).N == 1 ? 0.3 : 0.15))), 4, 16);
            var e = J.E.InQuad(p);
            var @out = new JArray();
            for (var k = 0; k < n; k++)
            {
                var drip = 0.5 + 0.5 * J.Noise1(k * 0.6, seed);
                var spike = J.R(seed, k, 311) < 0.3 ? J.R(seed, k, 312) : 0;
                @out.Add(new JArray(bb.X0 + k * w / n, bb.X0 + (k + 1) * w / n + 0.6, e * sz * (0.1 + 1.3 * drip + 1.1 * spike)));
            }
            it["vbands"] = @out;
            it["color"] = MixC(ColOf(it), ScK(env, "accent") ?? "#ffffff", J.Smooth(0.3, 0.9, p) * 0.45);
            it["alpha"] = NN(it, "alpha", 1) * (1 - J.Smooth(0.55, 1, p));
        },
    });

    // ---------------- dissolve（X.dissolve；JS pieces: true） ----------------

    private static void Dissolve() => Reg1("dissolve", new MoveDef
    {
        Name = "ほろほろ",
        Tags = new[] { "calm", "emotional" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var seed = SeedOf(it);
            var sz = N0(it, "size");
            var b = DBox(it, 0);
            var bw = Math.Max(1, b.X1 - b.X0);
            it["shatter"] = true;
            env.PieceFns.Add((ci, pj, _, ox, _, _) =>
            {
                // 随机起点，再叠一层从左到右的柔和扫过
                var t0 = J.R(seed, ci, pj, 71) * 0.5 + J.Clamp((ox + N0(it, "x") - b.X0) / bw) * 0.25;
                var x = (p - t0) / 0.25;
                if (x <= 0) return JAnim.Pid;
                if (x >= 1) return null;
                return JAnim.Pt(0, -x * sz * 0.05, 0, 1 + 0.3 * x, 1, 0, 1 - x);
            });
            if (it["gradient"] != null || IsFalse(it["fill"]) || it["dash"] != null || it["pattern"] != null)
            {
                // 带这些属性做不了 piece，退回字形级近似
                var step = (int)env.Step;
                Glyphs(env, (i, _, _) =>
                {
                    var t0 = J.R(seed, i, 72) * 0.7;
                    return p > t0 + 0.3 ? new CharXf { Hide = true }
                        : p > t0 && J.R(seed, step, i, 73) < (p - t0) / 0.3 ? new CharXf { Hide = true }
                        : null;
                });
            }
        },
    });

    private static bool IsFalse(JToken? t) => t is JValue v && v.Type == JTokenType.Boolean && !(bool)v.Value!;

    // ---------------- backspace（X.backspace；JS cursor: true） ----------------

    private static void Backspace() => Reg1("backspace", new MoveDef
    {
        Name = "バックスペース",
        Tags = new[] { "editorial", "glitch" },
        W = 0.8, // JS: cursor: true（MoveDef 暂无该 flag；光标经 post 就地画矩形）
        MinDur = 0.9,
        OutDur = (dur, n) => J.Clamp(0.2 + n * 0.035, 0.3, Math.Max(0.3, Math.Min(0.9, dur * 0.45))),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var lay = LayOf(it);
            var n = lay.N;
            if (n == 0) return;
            var nCut = CutN(env);
            var single = n == 1 && nCut > 1;
            var seedCut = unchecked((int)(env.Cut?.Seed ?? 0));
            var sel = !single && (J.H(seedCut, 601) & 1) == 1;
            var acc = ScK(env, "accent") ?? "#ffffff";
            var bg = ScK(env, "bg") ?? "#ffffff";
            var sz = N0(it, "size");
            var sx = N1(it, "sx");
            var sy = N1(it, "sy");
            var vert = B(it, "vertical");
            var blink = Math.Floor(env.Ltb * 5) % 2 == 0;
            var cw = Math.Max(2, sz * 0.07);

            double[] CursorAfter(JText.GlyphCell g) => vert
                ? new[] { g.X * sx - sz * sx * 0.48, (g.Y + g.H / 2) * sy + sz * 0.05, sz * sx * 0.96, cw }
                : new[] { (g.X + g.W / 2) * sx + sz * 0.05, g.Y * sy - sz * sy * 0.5, cw, sz * sy };
            double[] CursorBefore(JText.GlyphCell g) => vert
                ? new[] { g.X * sx - sz * sx * 0.48, (g.Y - g.H / 2) * sy - sz * 0.05 - cw, sz * sx * 0.96, cw }
                : new[] { (g.X - g.W / 2) * sx - sz * 0.05 - cw, g.Y * sy - sz * sy * 0.5, cw, sz * sy };
            void DrawCur(double[] r) => InItem(env, it, () => env.Rect(r[0], r[1], r[2], r[3], acc, 1, false));

            if (single)
            {
                var j = J.Clamp((int)Math.Round(Num(it, "mi")), 0, nCut - 1);
                var del = Math.Pow(J.Clamp((p - 0.1) / 0.8), 1.3);
                var tj = (nCut - 1 - j) / (double)nCut;
                var g = lay.Cells[0];
                if (del > tj) Glyphs(env, (_, _, _) => new CharXf { Hide = true });
                if (p < 0.94)
                {
                    if (j == nCut - 1 && del <= 0)
                    {
                        if (blink) DrawCur(CursorAfter(g));
                    }
                    else if (del > tj && (j == 0 || del <= tj + 1 / (double)nCut))
                    {
                        if (!(j == 0 && del >= 1 && !blink)) DrawCur(CursorBefore(g));
                    }
                }
                return;
            }
            var vis = lay.Cells.Where(g => !IsSp(g.Ch)).ToList();
            if (vis.Count == 0) return;
            if (sel)
            {
                // 从尾部全选，相当于 shift+←，然后一次删掉
                var s = (int)Math.Ceiling(vis.Count * J.E.OutQuad(J.Clamp(p / 0.42)));
                var from = vis.Count - s;
                if (p >= 0.55) Glyphs(env, (_, _, _) => new CharXf { Hide = true });
                else if (s > 0)
                {
                    var selIdx = new HashSet<int>(vis.Skip(from).Select(g => g.I));
                    var fromLocal = from;
                    // JS chainPre（选中底色块，画在文字前）——就地执行，层级同 JS（文字下方）
                    InItem(env, it, () =>
                    {
                        foreach (var g in vis.Skip(fromLocal))
                        {
                            if (vert)
                                env.Rect(g.X * sx - sz * sx * 0.6, (g.Y - g.H / 2) * sy - 0.5, sz * sx * 1.2,
                                    g.H * sy + sz * N0(it, "track") * sy + 1, acc, 1, true);
                            else
                                env.Rect((g.X - g.W / 2) * sx - 0.5, g.Y * sy - sz * sy * 0.6,
                                    g.W * sx + sz * N0(it, "track") * sx + 1, sz * sy * 1.2, acc, 1, true);
                        }
                    });
                    Glyphs(env, (i, _, _) => selIdx.Contains(i) ? new CharXf { Color = bg } : null);
                }
                if (p < 0.94)
                {
                    var g0 = vis[Math.Min(vis.Count - 1, from)];
                    if (p >= 0.55)
                    {
                        if (blink) DrawCur(CursorBefore(vis[0]));
                    }
                    else if (s == 0)
                    {
                        if (blink) DrawCur(CursorAfter(vis[vis.Count - 1]));
                    }
                    else DrawCur(CursorBefore(g0));
                }
                return;
            }
            // 逐字删除，越删越快，像按键连发
            var del2 = Math.Pow(J.Clamp((p - 0.1) / 0.8), 1.3);
            var k = vis.Count - Math.Min(vis.Count, (int)Math.Floor(del2 * (vis.Count + 0.999)));
            var keep = k > 0 ? vis[k - 1].I : -1;
            if (k < vis.Count) Glyphs(env, (i, _, _) => i > keep ? new CharXf { Hide = true } : null);
            if (p < 0.94)
            {
                var idle = p < 0.1 || k == 0;
                if (!idle || blink) DrawCur(k > 0 ? CursorAfter(vis[k - 1]) : CursorBefore(vis[0]));
            }
        },
    });

    // ---------------- scrambleOut（X.scrambleOut） ----------------

    private static void ScrambleOut() => Reg1("scrambleOut", new MoveDef
    {
        Name = "記号化",
        Tags = new[] { "glitch" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var ord = OrderOf(env, it);
            var seed = SeedOf(it);
            var step = (int)env.Step;
            var acc = ScK(env, "accent") ?? "#ffffff";
            Glyphs(env, (i, _, n) =>
            {
                var q = Win(p, 0.65 * ord(i, n) + 0.35 * J.R(seed, i, 701), 0.55);
                if (q <= 0) return null;
                if (q >= 1) return new CharXf { Hide = true };
                if (q > 0.55 && J.R(seed, i, step, 703) < (q - 0.55) * 2.4) return new CharXf { Hide = true };
                return new CharXf
                {
                    Ch = POOL[(int)(J.H(seed, i, step, 702) % (uint)POOL.Length)].ToString(),
                    Color = J.R(seed, i, step, 704) < 0.3 ? acc : null,
                    S = 1 - 0.35 * J.Smooth(0.4, 1, q),
                };
            });
        },
    });

    // ---------------- glitchDissolve（X.glitchDissolve） ----------------

    private static void GlitchDissolve() => Reg1("glitchDissolve", new MoveDef
    {
        Name = "ブロック化",
        Tags = new[] { "glitch" },
        W = 0.8,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var seed = SeedOf(it);
            var step = (int)env.Step;
            var sz = N0(it, "size");
            var lay = LayOf(it);
            var sx = N1(it, "sx");
            var sy = N1(it, "sy");
            var acc = ScK(env, "accent") ?? "#ffffff";
            var c0 = ColOf(it);
            var cols = new[] { c0, acc, ScK(env, "ghostA") ?? acc, ScK(env, "ghostB") ?? c0 };

            double T(int i) => J.R(seed, i, 801) * 0.55;
            Glyphs(env, (i, _, _) =>
            {
                var q = (p - T(i)) / 0.45;
                if (q <= 0) return null;
                if (q >= 0.3) return new CharXf { Hide = true };
                var r = J.R(seed, i, step, 802);
                var cut = J.Rs(seed, i, step, 804) * 0.35;
                return new CharXf
                {
                    Dx = J.Rs(seed, i, step, 803) * sz * 0.12,
                    ClipY = r < 0.5 ? new[] { -1.2, cut } : new[] { cut, 1.2 },
                    Color = r < 0.25 ? acc : null,
                };
            });
            // JS chainPost（噪点色块）——就地执行
            InItem(env, it, () =>
            {
                foreach (var g in lay.Cells)
                {
                    if (IsSp(g.Ch)) continue;
                    var q = (p - T(g.I)) / 0.45;
                    if (q <= 0 || q >= 1) continue;
                    var cnt = (int)Math.Ceiling(5 * (1 - q * 0.8));
                    for (var b = 0; b < cnt; b++)
                    {
                        var hk = g.I * 8 + b;
                        if (J.R(seed, hk, step, 805) < q * 0.55) continue;
                        var bw = sz * J.Rr(0.12, 0.6, seed, hk, step, 806) * (1 - q * 0.5);
                        var bh = sz * J.Rr(0.06, 0.3, seed, hk, step, 807);
                        var x = g.X * sx + J.Rs(seed, hk, step, 808) * sz * 0.34 - bw / 2;
                        var y = g.Y * sy + J.Rs(seed, hk, step, 809) * sz * 0.34 - bh / 2;
                        env.Rect(x, y, bw, bh, cols[(int)(J.H(seed, hk, step, 810) % (uint)cols.Length)], 1, false);
                    }
                }
            });
        },
    });

    // ---------------- echoOut（X.echoOut） ----------------

    private static void EchoOut() => Reg1("echoOut", new MoveDef
    {
        Name = "残響",
        Tags = new[] { "emotional", "calm" },
        W = 0.9,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var b = Box(it);
            var ax = b.Cx - N0(it, "x");
            var ay = b.Cy - N0(it, "y");
            var a0 = NN(it, "alpha", 1);
            var sz = N0(it, "size");
            var lw = Math.Max(1, sz * 0.016);
            var col = ColOf(it);
            var acc = ScK(env, "accent") ?? "#ffffff";
            it["alpha"] = a0 * (1 - J.E.InOutSine(J.Clamp(p / 0.45)));
            // JS chainPost（3 圈描边残影）——就地执行；副本走当前合并 charFn
            var cf = MergedNow(env);
            for (var k = 0; k < 3; k++)
            {
                var q = J.Clamp((p - k * 0.15) / 0.55);
                if (q <= 0 || q >= 1) continue;
                var s = 1 + 0.5 * J.E.OutCubic(q) * (1 + k * 0.2);
                var d = Rot2(ax * (1 - s), ay * (1 - s), N0(it, "rot") * J.DEG);
                DrawCopy(env, it, cf, c =>
                {
                    c["x"] = N0(it, "x") + d[0];
                    c["y"] = N0(it, "y") + d[1];
                    c["size"] = N0(it, "size") * s;
                    c["alpha"] = a0 * 0.8 * (1 - q);
                    c["fill"] = false;
                    c["stroke"] = lw / s;
                    c["strokeColor"] = k == 1 ? acc : col;
                    c["pieceFn"] = null;
                    c["pre"] = null;
                    c["post"] = null;
                    c["echo"] = null;
                    c["streak"] = null;
                    c["extrude"] = null;
                    c["shadow"] = null;
                    c["gradient"] = null;
                    c["pattern"] = null;
                    c["dash"] = null;
                    c["fillAlpha"] = 1;
                    c["_lay"] = null;
                    c["_m"] = null;
                });
            }
        },
    });

    // ---------------- whipOut（X.whipOut） ----------------

    private static void WhipOut() => Reg1("whipOut", new MoveDef
    {
        Name = "ホイップ",
        Tags = new[] { "pop", "graphic" },
        W = 1,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var b = Box(it);
            var vert = B(it, "vertical");
            var sz = N0(it, "size");
            var dir = CutBit(env, 4) ? 1 : -1;
            var dist = (vert ? env.H + b.H : env.W + b.W) + sz * 2;
            var pb = sz * 0.14 * J.E.OutCubic(J.Clamp(p / 0.28));
            var u = J.Clamp((p - 0.26) / 0.74);
            var e = u * u;
            var v = Math.Min(1.6, 2 * u);
            var off = dir * (dist * e - pb * (1 - u));
            var r = N0(it, "rot") * J.DEG;
            if (vert)
            {
                it["x"] = N0(it, "x") + -off * Math.Sin(r);
                it["y"] = N0(it, "y") + off * Math.Cos(r);
                ScaleAbout(it, 1, 1 + v * 0.45);
            }
            else
            {
                it["x"] = N0(it, "x") + off * Math.Cos(r);
                it["y"] = N0(it, "y") + off * Math.Sin(r);
                ScaleAbout(it, 1 + v * 0.55, 1 - v * 0.08);
                it["skew"] = N0(it, "skew") - dir * Math.Min(28, v * 16);
            }
            if (v > 0.05)
            {
                var m = Math.Min(1, v);
                it["streak"] = vert
                    ? new JObject { ["n"] = 4, ["dx"] = 0, ["dy"] = -dir * sz * 0.45 * m, ["a"] = 0.4 * m }
                    : new JObject { ["n"] = 4, ["dx"] = -dir * sz * 0.45 * m, ["dy"] = 0, ["a"] = 0.4 * m };
            }
            it["alpha"] = NN(it, "alpha", 1) * (1 - J.Smooth(0.9, 1, p));
        },
    });

    // ---------------- gravity（X.gravity） ----------------

    private static void Gravity() => Reg1("gravity", new MoveDef
    {
        Name = "重力落下",
        Tags = new[] { "pop", "emotional" },
        W = 1,
        OutDur = (dur, _) => J.Clamp(dur * 0.38, 0.3, 0.75),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var seed = SeedOf(it);
            var sz = N0(it, "size");
            var bb = DBox(it, 0);
            var di = DownI(it);
            var dsx = di[0];
            var dsy = di[1];
            var dFall = Math.Max(sz * 2, env.H - bb.Y0 + sz * 1.2);
            Glyphs(env, (i, _, _) =>
            {
                var d0 = J.R(seed, i, 901) * 0.4;
                var q = J.Clamp((p - d0) / 0.6);
                if (q <= 0) return null;
                if (q >= 1) return new CharXf { Hide = true };
                var h = sz * (0.12 + 0.22 * J.R(seed, i, 902));
                var v0 = 2 * h + Math.Sqrt(4 * h * h + 4 * h * dFall);
                var gg = dFall + v0;
                var down = -v0 * q + gg * q * q;
                var side = J.Rs(seed, i, 903) * sz * 0.9 * q;
                return new CharXf
                {
                    Dx = down * dsx + side * dsy,
                    Dy = down * dsy - side * dsx,
                    Rot = J.Rs(seed, i, 904) * 240 * q * q,
                    A = 1 - J.Smooth(0.92, 1, q),
                };
            });
        },
    });

    // ---------------- popOut（X.popOut） ----------------

    private static void PopOut() => Reg1("popOut", new MoveDef
    {
        Name = "弾ける",
        Tags = new[] { "pop" },
        W = 1,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var ord = OrderOf(env, it);
            var seed = SeedOf(it);
            var acc = ScK(env, "accent") ?? "#ffffff";
            var c0 = ColOf(it);
            var lay = LayOf(it);
            var sz = N0(it, "size");
            var sx = N1(it, "sx");
            var sy = N1(it, "sy");

            double Q(int i, int n) => Win(p, 0.6 * ord(i, n) + 0.4 * J.R(seed, i, 1001), 0.5);
            Glyphs(env, (i, _, n) =>
            {
                var q = Q(i, n);
                if (q <= 0) return null;
                if (q >= 0.55) return new CharXf { Hide = true };
                var u = q / 0.55;
                return new CharXf
                {
                    S = 1 + 0.5 * u * u,
                    Sx = 1 + 0.07 * Math.Sin(u * 26) * u,
                    Sy = 1 - 0.07 * Math.Sin(u * 26) * u,
                    Color = MixC(c0, acc, u * 0.7),
                };
            });
            // JS chainPost（逐字冲击环 + 放射线）——就地执行
            InItem(env, it, () =>
            {
                foreach (var g in lay.Cells)
                {
                    if (IsSp(g.Ch)) continue;
                    var q = Q(g.I, lay.N);
                    if (q < 0.55 || q >= 1) continue;
                    var u = (q - 0.55) / 0.45;
                    var e = J.E.OutCubic(u);
                    var cx = g.X * sx;
                    var cy = g.Y * sy;
                    var lw = Math.Max(1, sz * 0.08 * (1 - u));
                    env.Circle(cx, cy, sz * (0.35 + 0.45 * e), null, acc, lw, 1 - u, false);
                    var a0 = J.R(seed, g.I, 1002) * J.TAU;
                    for (var k = 0; k < 8; k++)
                    {
                        var a = a0 + k / 8.0 * J.TAU;
                        var r0 = sz * (0.5 + 0.55 * e);
                        var r1 = r0 + sz * 0.16 * (1 - u);
                        env.Line(new[]
                        {
                            new[] { cx + Math.Cos(a) * r0, cy + Math.Sin(a) * r0 },
                            new[] { cx + Math.Cos(a) * r1, cy + Math.Sin(a) * r1 },
                        }, acc, lw, 1 - u, false);
                    }
                }
            });
        },
    });

    // ---------------- burn（X.burn） ----------------

    private static void Burn() => Reg1("burn", new MoveDef
    {
        Name = "焼失",
        Tags = new[] { "emotional", "glitch" },
        W = 0.8,
        OutDur = (dur, _) => J.Clamp(dur * 0.36, 0.28, 0.7),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var ord0 = OrderOf(env, it);
            var seed = SeedOf(it);
            var rev = CutBit(env, 5);
            double Ord(int i, int n) => rev ? 1 - ord0(i, n) : ord0(i, n);
            var c0 = ColOf(it);
            var acc = ScK(env, "accent") ?? "#ffffff";
            var bg = ScK(env, "bg") ?? "#ffffff";
            var sz = N0(it, "size");
            var lay = LayOf(it);
            var sx = N1(it, "sx");
            var sy = N1(it, "sy");
            var step = (int)env.Step;

            double Q(int i, int n) => Win(p, 0.6 * Ord(i, n) + 0.4 * J.R(seed, i, 1101), 0.45);
            double K(double q) => Math.Pow(J.Clamp((q - 0.12) / 0.88), 1.15); // 烧掉的比例，从下往上
            Glyphs(env, (i, _, n) =>
            {
                var q = Q(i, n);
                if (q <= 0) return null;
                if (q >= 1) return new CharXf { Hide = true };
                var heat = J.Smooth(0, 0.2, q);
                var ch = J.Smooth(0.35, 0.95, q);
                var k = K(q);
                return new CharXf
                {
                    Color = ch > 0 ? MixC(acc, bg, ch * 0.75) : MixC(c0, acc, heat),
                    ClipY = new[] { -1.2, 0.72 - 1.44 * k },
                    Dy = -k * sz * 0.1,
                    Dx = J.Rs(seed, i, step, 1102) * sz * 0.012 * heat,
                };
            });
            // JS chainPost（上升火星）——就地执行
            InItem(env, it, () =>
            {
                foreach (var g in lay.Cells)
                {
                    if (IsSp(g.Ch)) continue;
                    var q = Q(g.I, lay.N);
                    if (q <= 0.15 || q >= 1) continue;
                    for (var j = 0; j < 5; j++)
                    {
                        var b0 = 0.15 + J.R(seed, g.I, j, 1103) * 0.6;
                        var age = (q - b0) / 0.35;
                        if (age <= 0 || age >= 1) continue;
                        var ey = g.Y * sy + (0.72 - 1.44 * K(b0)) * g.H * sy - K(b0) * sz * 0.1 - age * sz * 0.55;
                        var ex = g.X * sx + J.Rs(seed, g.I, j, 1104) * g.W * sx * 0.4 + Math.Sin(age * 6 + j) * sz * 0.05;
                        env.Circle(ex, ey, Math.Max(1, sz * 0.04 * (1 - age)), acc, null, 0, 1 - age, false);
                    }
                }
            });
        },
    });

    // ---------------- sweepCover（X.sweepCover） ----------------

    private static void SweepCover() => Reg1("sweepCover", new MoveDef
    {
        Name = "バーで隠す",
        Tags = new[] { "graphic", "editorial", "pop" },
        W = 1,
        OutDur = (dur, _) => J.Clamp(dur * 0.34, 0.26, 0.6),
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var lines = LineExt(it);
            var vert = B(it, "vertical");
            var sz = N0(it, "size");
            var acc = ScK(env, "accent") ?? "#ffffff";
            var count = lines.Count;
            if (count == 0) return;
            var st = count > 1 ? Math.Min(0.12, 0.3 / (count - 1)) : 0;
            var span = 1 - (count - 1) * st;
            var nCut = CutN(env);
            var single = LayOf(it).N == 1 && nCut > 1;
            var uu = new double[count];
            for (var k = 0; k < count; k++)
                uu[k] = single ? Win(p, OrderOf(env, it)(0, 1), 0.45) : J.Clamp((p - k * st) / span);
            var hidden = new HashSet<int>();
            for (var k = 0; k < count; k++)
                if (uu[k] >= 0.5)
                    hidden.Add(lines[k].Li);
            if (hidden.Count > 0)
                Glyphs(env, (_, g, _) => g != null && hidden.Contains((int)D(g, "li")) ? new CharXf { Hide = true } : null);
            var pa = sz * 0.14;
            var pc = sz * 0.1;
            // JS chainPost（accent 遮盖条）——就地执行
            InItem(env, it, () =>
            {
                for (var k = 0; k < count; k++)
                {
                    var ln = lines[k];
                    var u2 = uu[k];
                    if (u2 <= 0 || u2 >= 1) continue;
                    var c1 = J.E.InOutCubic(J.Clamp(u2 / 0.5));
                    var c2 = J.E.InOutCubic(J.Clamp((u2 - 0.5) / 0.5));
                    if (vert)
                    {
                        var a0 = ln.Y0 - pa;
                        var a1 = ln.Y1 + pa;
                        var y0 = J.Lerp(a0, a1, c2);
                        var y1 = J.Lerp(a0, a1, c1);
                        if (y1 - y0 > 0.3) env.Rect(ln.X0 - pc, y0, ln.X1 - ln.X0 + pc * 2, y1 - y0, acc, 1, false);
                    }
                    else
                    {
                        var a0 = ln.X0 - pa;
                        var a1 = ln.X1 + pa;
                        var x0 = J.Lerp(a0, a1, c2);
                        var x1 = J.Lerp(a0, a1, c1);
                        if (x1 - x0 > 0.3) env.Rect(x0, ln.Y0 - pc, x1 - x0, ln.Y1 - ln.Y0 + pc * 2, acc, 1, false);
                    }
                }
            });
        },
    });

    // ---------------- shatterLite（X.shatterLite） ----------------

    private static void ShatterLite() => Reg1("shatterLite", new MoveDef
    {
        Name = "四分割飛散",
        Tags = new[] { "pop", "glitch" },
        W = 0.8,
        Draw = env =>
        {
            var it = env.It!;
            var p = env.Ap;
            var seed = SeedOf(it);
            var sz = N0(it, "size");
            var dist = sz * 1.7;

            JAnim.CharFn Quad(int qx, int qy, int id) => (i, _, _) =>
            {
                var clipX = qx < 0 ? new[] { -1.6, 0.012 } : new[] { 0.0, 1.6 };
                var clipY = qy < 0 ? new[] { -1.6, 0.012 } : new[] { 0.0, 1.6 };
                var d0 = J.R(seed, i, 1201) * 0.35;
                var q = J.Clamp((p - d0) / 0.65);
                if (q <= 0) return new CharXf { ClipX = clipX, ClipY = clipY };
                if (q >= 1) return new CharXf { Hide = true };
                var e = J.E.OutCubic(q);
                var dd = dist * (0.7 + 0.6 * J.R(seed, i, id, 1203));
                return new CharXf
                {
                    ClipX = clipX,
                    ClipY = clipY,
                    Dx = (qx + J.Rs(seed, i, id, 1202) * 0.5) * dd * e,
                    Dy = (qy + J.Rs(seed, i, id, 1205) * 0.5) * dd * e + sz * 0.9 * q * q,
                    Rot = J.Rs(seed, i, id, 1204) * 110 * e,
                    S = 1 - 0.45 * q,
                    A = 1 - J.Smooth(0.45, 1, q),
                };
            };
            WithCopies(env, it, Quad(-1, -1, 0), new[] { Quad(1, -1, 1), Quad(-1, 1, 2), Quad(1, 1, 3) });
        },
    });

    // ================= hold（JS H 字面量序） =================

    // ---------------- float（H.float） ----------------

    private static void Float() => RegH("float", new MoveDef
    {
        Name = "ふわふわ",
        Tags = new[] { "calm", "emotional" },
        W = 0.8,
        Draw = env =>
        {
            var it = env.It!;
            var k = env.Ap * MotionK(env);
            if (k < 0.01) return;
            var t = env.Ltb;
            var seed = SeedOf(it);
            var a = N0(it, "size") * 0.075 * k;
            var ph = seed % 97 * 0.13;
            Glyphs(env, (i, _, _) => new CharXf
            {
                Dy = (0.55 * Math.Sin(t * 2.1 + i * 1.3 + ph) + 0.6 * J.Noise1(t * 0.9 + i * 0.43, seed)) * a,
                Dx = J.Noise1(t * 0.6 + i * 0.31, seed + 7) * a * 0.35,
            });
        },
    });

    // ---------------- sway（H.sway） ----------------

    private static void Sway() => RegH("sway", new MoveDef
    {
        Name = "ゆらぎ",
        Tags = new[] { "calm", "emotional" },
        W = 0.6,
        Draw = env =>
        {
            var it = env.It!;
            var k = env.Ap * MotionK(env);
            if (k < 0.01) return;
            var b = Box(it);
            var ang = Math.Sin(env.Ltb * J.TAU / 3.2 + SeedOf(it) % 7) * 2.6 * k;
            RotateAbout(it, ang, b.Cx - N0(it, "x"), b.Y0 - N0(it, "y") - N0(it, "size") * 0.6); // 摆锤悬在文字上方一点
        },
    });

    // ---------------- pulse（H.pulse） ----------------

    private static void Pulse() => RegH("pulse", new MoveDef
    {
        Name = "脈動",
        Tags = new[] { "pop", "graphic" },
        W = 0.6,
        Draw = env =>
        {
            var k = env.Ap * MotionK(env);
            if (k < 0.01) return;
            var f = 1 + 0.055 * Math.Exp(-BeatSince(env, 0.5) * 9) * k;
            ScaleAbout(env.It!, f, f);
        },
    });

    // ---------------- shimmer（H.shimmer） ----------------

    private static void Shimmer() => RegH("shimmer", new MoveDef
    {
        Name = "きらめき",
        Tags = new[] { "emotional", "calm" },
        W = 0.5,
        Draw = env =>
        {
            var it = env.It!;
            var k = env.Ap * MotionK(env);
            if (k < 0.01) return;
            var t = env.Ltb;
            var seed = SeedOf(it);
            var step = (int)env.Step;
            var acc = ScK(env, "accent") ?? "#ffffff";
            var c0 = ColOf(it);
            Glyphs(env, (i, _, _) =>
            {
                var tw = 0.5 + 0.5 * J.Noise1(t * 3.2 + i * 1.7, seed);
                var sp = J.R(seed, step, i, 401) < 0.03 * k;
                return new CharXf
                {
                    A = 1 - 0.42 * Math.Min(1, k) * tw * tw,
                    Color = sp ? MixC(c0, acc, 0.85) : null,
                    S = sp ? 1.05 : 1,
                };
            });
        },
    });

    // ---------------- colorRun（H.colorRun） ----------------

    private static void ColorRun() => RegH("colorRun", new MoveDef
    {
        Name = "色が走る",
        Tags = new[] { "pop", "graphic" },
        W = 0.5,
        Draw = env =>
        {
            var it = env.It!;
            var k = env.Ap * Math.Min(1, MotionK(env));
            if (k < 0.01) return;
            var c0 = ColOf(it);
            var acc = ScK(env, "accent") ?? "#ffffff";
            if (!IsHex(c0) || !IsHex(acc)) return;
            var nCut = CutN(env);
            var mi = Num(it, "mi");
            var sz = N0(it, "size");
            var per = nCut + 5;
            var pos = ((env.Ltb * 7) % per + per) % per - 2.5;
            Glyphs(env, (i, _, n) =>
            {
                var d = (n > 1 ? i : mi) - pos;
                var w = Math.Exp(-d * d / 1.6);
                if (w < 0.02) return null;
                return new CharXf { Color = MixC(c0, acc, w * 0.9 * k), Dy = -w * sz * 0.035 * k };
            });
        },
    });

    // ---------------- rotateSlow（H.rotateSlow） ----------------

    private static void RotateSlow() => RegH("rotateSlow", new MoveDef
    {
        Name = "ゆっくり回転",
        Tags = new[] { "calm", "editorial" },
        W = 0.4,
        Draw = env =>
        {
            var it = env.It!;
            var k = env.Ap * MotionK(env);
            if (k < 0.01) return;
            var dir = ((uint)SeedOf(it) >> 1) & 1;
            var b = Box(it);
            RotateAbout(it, (dir != 0 ? 1 : -1) * Math.Min(10, env.Ltb * 2.6) * k, b.Cx - N0(it, "x"), b.Cy - N0(it, "y"));
        },
    });

    // ---------------- trackBreathe（H.trackBreathe） ----------------

    private static void TrackBreathe() => RegH("trackBreathe", new MoveDef
    {
        Name = "字間の呼吸",
        Tags = new[] { "calm", "editorial" },
        W = 0.5,
        Draw = env =>
        {
            var it = env.It!;
            var k = env.Ap * MotionK(env);
            if (k < 0.01) return;
            var b = Box(it);
            var ox = b.Cx - N0(it, "x");
            var oy = b.Cy - N0(it, "y");
            var sx = N1(it, "sx");
            var sy = N1(it, "sy");
            var vert = B(it, "vertical");
            var f = Math.Sin(env.Ltb * J.TAU / 2.8) * 0.075 * k;
            Glyphs(env, (_, g, _) =>
            {
                if (g == null) return null;
                return vert
                    ? new CharXf { Dy = (D(g, "y") * sy - oy) * f }
                    : new CharXf { Dx = (D(g, "x") * sx - ox) * f };
            });
        },
    });

    // ---------------- skewWobble（H.skewWobble） ----------------

    private static void SkewWobble() => RegH("skewWobble", new MoveDef
    {
        Name = "斜め揺れ",
        Tags = new[] { "pop", "glitch" },
        W = 0.4,
        Draw = env =>
        {
            var it = env.It!;
            var k = env.Ap * MotionK(env);
            if (k < 0.01) return;
            var b = Box(it);
            var oy = b.Cy - N0(it, "y");
            var s0 = N0(it, "skew");
            var s1 = s0 + Math.Sin(env.Ltb * J.TAU / 1.9) * 12 * k;
            // 补一个平移，box 中心保持不动
            var d = Rot2(-(Math.Tan(s1 * J.DEG) - Math.Tan(s0 * J.DEG)) * oy, 0, N0(it, "rot") * J.DEG);
            it["skew"] = s1;
            it["x"] = N0(it, "x") + d[0];
            it["y"] = N0(it, "y") + d[1];
        },
    });

    // ---------------- beatHop（H.beatHop） ----------------

    private static void BeatHop() => RegH("beatHop", new MoveDef
    {
        Name = "拍で跳ねる",
        Tags = new[] { "pop" },
        W = 0.5,
        Draw = env =>
        {
            var it = env.It!;
            var k = env.Ap * MotionK(env);
            if (k < 0.01) return;
            var len = BeatLen(env, 0.45);
            var u = BeatSince(env, 0.45) / (len * 0.85);
            if (u >= 1) return;
            var nCut = CutN(env);
            var idx = BeatIdx(env, 0.45);
            var cur = ((idx % nCut) + nCut) % nCut;
            var mi = Num(it, "mi");
            var sz = N0(it, "size");
            var h = Math.Pow(Math.Sin(Math.PI * u), 0.7);
            Glyphs(env, (i, _, n) =>
            {
                var d = Math.Abs((n > 1 ? i : mi) - cur);
                var w = d == 0 ? 1 : d == 1 ? 0.3 : 0; // 相邻的字带一点涟漪
                return w != 0
                    ? new CharXf { Dy = -h * w * sz * 0.2 * k, Sy = 1 + 0.08 * h * w * k, Sx = 1 - 0.05 * h * w * k }
                    : null;
            });
        },
    });

    // ---------------- hWave（H.hWave） ----------------

    private static void HWave() => RegH("hWave", new MoveDef
    {
        Name = "横波",
        Tags = new[] { "pop", "emotional" },
        W = 0.4,
        Draw = env =>
        {
            var it = env.It!;
            var k = env.Ap * MotionK(env);
            if (k < 0.01) return;
            var t = env.Ltb;
            var sz = N0(it, "size");
            Glyphs(env, (i, _, _) =>
            {
                var a = t * J.TAU * 0.55 - i * 0.9;
                return new CharXf { Dx = Math.Sin(a) * sz * 0.085 * k, Skew = -Math.Cos(a) * 9 * k };
            });
        },
    });

    // ---------------- heartbeat（H.heartbeat） ----------------

    private static void Heartbeat() => RegH("heartbeat", new MoveDef
    {
        Name = "鼓動",
        Tags = new[] { "emotional", "pop" },
        W = 0.4,
        Draw = env =>
        {
            var it = env.It!;
            var k = env.Ap * MotionK(env);
            if (k < 0.01) return;
            var u = env.BeatHas
                ? (env.BeatIndex % 2) * env.BeatLen + env.BeatSince
                : ((env.Ltb % 1.05) + 1.05) % 1.05; // JS heartbeat 的 env.beat 分支 + ltb 回退式
            var v = Math.Exp(-u * 9) + (u > 0.2 ? 0.75 * Math.Exp(-(u - 0.2) * 9) : 0);
            var f = 1 + 0.055 * v * k;
            ScaleAbout(it, f, f);
            it["color"] = MixC(ColOf(it), ScK(env, "accent") ?? "#ffffff", Math.Min(0.85, 0.6 * v * Math.Min(1, k)));
        },
    });

    // ---------------- orbitSmall（H.orbitSmall） ----------------

    private static void OrbitSmall() => RegH("orbitSmall", new MoveDef
    {
        Name = "小さな円運動",
        Tags = new[] { "calm", "pop" },
        W = 0.5,
        Draw = env =>
        {
            var it = env.It!;
            var k = env.Ap * MotionK(env);
            if (k < 0.01) return;
            var t = env.Ltb * J.TAU / 1.7;
            var r = N0(it, "size") * 0.05 * k;
            var dir = ((uint)SeedOf(it) >> 2) & 1;
            Glyphs(env, (i, _, _) => new CharXf
            {
                Dx = Math.Cos(t * (dir != 0 ? 1 : -1) + i * 0.9) * r,
                Dy = Math.Sin(t * (dir != 0 ? 1 : -1) + i * 0.9) * r,
            });
        },
    });

    // ---------------- jelly（H.jelly） ----------------

    private static void Jelly() => RegH("jelly", new MoveDef
    {
        Name = "ゼリー",
        Tags = new[] { "pop" },
        W = 0.4,
        Draw = env =>
        {
            var it = env.It!;
            var k = env.Ap * MotionK(env);
            if (k < 0.01) return;
            var sy0 = N1(it, "sy");
            var t = env.Ltb;
            var bs = env.BeatHas ? env.BeatSince : (double?)null; // JS jelly：有 beat 用衰减振荡，无 beat 用连续 sin
            Glyphs(env, (i, g, _) =>
            {
                if (g == null) return null;
                var w = bs != null
                    ? Math.Exp(-(bs.Value - i * 0.03) * 4.5) * Math.Sin(Math.Max(0, bs.Value - i * 0.03) * 21)
                    : Math.Sin(t * J.TAU * 1.1 - i * 0.55);
                var a = 0.1 * w * k;
                var syk = 1 - a;
                return new CharXf { Sx = 1 + a, Sy = syk, Dy = (1 - syk) * D(g, "h") * sy0 * 0.5 }; // 以底部为锚的挤压拉伸
            });
        },
    });

    // ---------------- scanBand（H.scanBand） ----------------

    private static void ScanBand() => RegH("scanBand", new MoveDef
    {
        Name = "走査帯",
        Tags = new[] { "glitch", "graphic" },
        W = 0.4,
        Draw = env =>
        {
            var it = env.It!;
            var k = env.Ap * MotionK(env);
            if (k < 0.01) return;
            if (it["bands"] != null || it["vbands"] != null) return; // 入场效果正在切这个 item，别再切
            const double per = 2.2;
            var cyc = (int)Math.Floor(env.Ltb / per);
            var v = ((env.Ltb % per) + per) % per / (per * 0.7);
            if (v >= 1) return;
            var bb = DBox(it, N0(it, "size") * 0.1);
            var h = bb.Y1 - bb.Y0;
            var bh = Math.Max(N0(it, "size") * 0.24, h * 0.12);
            var y = bb.Y0 - bh + (h + bh) * v;
            var dx = N0(it, "size") * 0.075 * k * (J.R(SeedOf(it), cyc, 451) < 0.5 ? 1 : -1);
            // JS it.bands = [[y, y + bh, dx]]：new JArray(集合) 会把集合摊平，必须用 JArr 建行
            it["bands"] = JAnim.JArr(new[] { new[] { y, y + bh, dx } });
            var acc = ScK(env, "accent") ?? "#ffffff";
            var lw = Math.Max(1, N0(it, "size") * 0.012);
            // JS chainPost——就地执行
            env.Rect(bb.X0, y + bh - lw, bb.X1 - bb.X0, lw, acc, 0.55 * Math.Min(1, k), false);
        },
    });

    // ---------------- noiseDrift（H.noiseDrift） ----------------

    private static void NoiseDrift() => RegH("noiseDrift", new MoveDef
    {
        Name = "ノイズ漂流",
        Tags = new[] { "calm", "emotional" },
        W = 0.6,
        Draw = env =>
        {
            var it = env.It!;
            var k = env.Ap * MotionK(env);
            if (k < 0.01) return;
            var t = env.Ltb * 0.35;
            var seed = SeedOf(it);
            var a = N0(it, "size") * 0.075 * k;
            Glyphs(env, (i, _, _) => new CharXf
            {
                Dx = J.Noise1(t + i * 1.7, seed + 11) * a,
                Dy = J.Noise1(t + i * 2.3, seed + 23) * a,
                Rot = J.Noise1(t * 1.3 + i * 0.9, seed + 37) * 7 * k,
            });
        },
    });

    // ---------------- tilt（H.tilt） ----------------

    private static void Tilt() => RegH("tilt", new MoveDef
    {
        Name = "シーソー",
        Tags = new[] { "calm", "editorial" },
        W = 0.4,
        Draw = env =>
        {
            var it = env.It!;
            var k = env.Ap * MotionK(env);
            if (k < 0.01) return;
            var b = Box(it);
            var ox = b.Cx - N0(it, "x");
            var oy = b.Cy - N0(it, "y");
            var sx = N1(it, "sx");
            var sy = N1(it, "sy");
            var vert = B(it, "vertical");
            // 基线倾斜，字形保持直立
            var tn = Math.Tan(Math.Sin(env.Ltb * J.TAU / 4.2 + SeedOf(it) % 5) * 4 * k * J.DEG);
            Glyphs(env, (_, g, _) =>
            {
                if (g == null) return null;
                return vert
                    ? new CharXf { Dx = -(D(g, "y") * sy - oy) * tn }
                    : new CharXf { Dy = (D(g, "x") * sx - ox) * tn };
            });
        },
    });

    // ---------------- zoomSlow（H.zoomSlow） ----------------

    private static void ZoomSlow() => RegH("zoomSlow", new MoveDef
    {
        Name = "じわ寄り",
        Tags = new[] { "calm", "emotional", "editorial" },
        W = 0.8,
        Draw = env =>
        {
            var it = env.It!;
            var k = env.Ap * MotionK(env);
            if (k < 0.01) return;
            var u = J.Clamp(env.Ltb / Math.Max(0.3, env.Cut?.Dur ?? 1));
            var f = 1 + 0.07 * J.E.OutQuad(u) * k;
            ScaleAbout(it, f, f);
        },
    });

    // ---------------- stretchPulse（H.stretchPulse） ----------------

    private static void StretchPulse() => RegH("stretchPulse", new MoveDef
    {
        Name = "横伸び拍",
        Tags = new[] { "pop", "graphic" },
        W = 0.4,
        Draw = env =>
        {
            var k = env.Ap * MotionK(env);
            if (k < 0.01) return;
            var kick = Math.Exp(-BeatSince(env, 0.5) * 8) * k;
            ScaleAbout(env.It!, 1 + 0.12 * kick, 1 - 0.045 * kick);
        },
    });

    // ---------------- glitchJump（H.glitchJump） ----------------

    private static void GlitchJump() => RegH("glitchJump", new MoveDef
    {
        Name = "時々ずれる",
        Tags = new[] { "glitch" },
        W = 0.4,
        Draw = env =>
        {
            var it = env.It!;
            var k = env.Ap * MotionK(env);
            if (k < 0.01) return;
            var seed = SeedOf(it);
            var step = (int)env.Step;
            var slot = (int)Math.Floor(step / 8.0);
            var ph = step - slot * 8;
            var gl = env.Fx.Glitch;
            if (ph > 2 || J.R(seed, slot, 501) > 0.4 + 0.3 * gl) return;
            var sz = N0(it, "size");
            it["x"] = N0(it, "x") + J.Rs(seed, slot, 502) * sz * 0.2 * k;
            it["y"] = N0(it, "y") + J.Rs(seed, slot, 503) * sz * 0.04 * k;
            if (ph == 0 && it["bands"] == null && it["vbands"] == null)
            {
                it["bands"] = JAnim.JArr(JAnim.ItemBands(env, it, 5, (b, _) =>
                    J.R(seed, slot, b, 504) < 0.6 ? J.Rs(seed, slot, b, 505) * sz * 0.12 * k : 0));
                if (J.R(seed, slot, 506) < 0.5) it["color"] = ScK(env, "ghostA") ?? ScK(env, "accent") ?? "#ffffff";
            }
        },
    });

    // ---------------- echoTrail（H.echoTrail） ----------------

    private static void EchoTrail() => RegH("echoTrail", new MoveDef
    {
        Name = "残像を引く",
        Tags = new[] { "emotional", "glitch" },
        W = 0.4,
        Draw = env =>
        {
            var it = env.It!;
            var k = env.Ap * MotionK(env);
            if (k < 0.01) return;
            var t = env.Ltb;
            var sz = N0(it, "size");
            var w = J.TAU / 3.4;
            var ph = SeedOf(it) % 100 * 0.1;
            var ax = sz * 0.09 * k;
            var ay = sz * 0.05 * k;
            it["x"] = N0(it, "x") + Math.Sin(w * t + ph) * ax;
            it["y"] = N0(it, "y") + Math.Sin(w * 1.37 * t + ph * 1.3) * ay;
            var vx = Math.Cos(w * t + ph) * w * ax;
            var vy = Math.Cos(w * 1.37 * t + ph * 1.3) * w * 1.37 * ay;
            if (it["echo"] == null)
                it["echo"] = new JObject
                {
                    ["n"] = 3,
                    ["dx"] = -vx * 0.3,
                    ["dy"] = -vy * 0.3,
                    ["a"] = 0.34 * Math.Min(1, k),
                    ["decay"] = 0.62,
                    ["color"] = ScK(env, "sub") ?? ColOf(it),
                };
        },
    });
}
