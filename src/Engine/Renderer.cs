// 移植自 frontend/src/engine/09_render.js
//
// 渲染器主流程：整帧缓存（koma 量化步内逐像素相同才复用）、paper 纸纹、色差 ghost 三 pass
// （B/A/main，lag 1.6/24 与 0.8/24）、每 cut 的 bg/layout/enter/hold/exit/decor/cam（经 Env.Make
// 装配）、cut 间转场（J.TRANS，core 注册表为空、代码路径保留）、事件后期 FX 内置分支
// （slice/block/invert/flash/zoom/mosaic；chroma/shake 走帧头的衰减注入）、HUD（JDecor.DrawHud）、
// モーフ（glyphLog 抓取 + 逐字滑移/化掉 + 位图缓存）。
//
// Canvas2D → SkiaSharp 对位（与 JText/Env 同约定）：
//   ctx.filter='blur(Npx)' → SKImageFilter.CreateBlur(σ=N/2)；
//   ctx.globalCompositeOperation → 每 paint 的 BlendMode（ghost pass 的整 pass multiply 经 Env.Blend）；
//   ctx.setTransform/save/restore → SKCanvas.SetMatrix/Save/Restore；
//   离屏画布（scratch/small/tiny/camLayer/transA·B/guardP·M/morph*）→ SKSurface 复用（尺寸变化即重建）。
//
// 未移植（明确不进纯 C# core，检测到即抛）：
//   st.kline → J.KLINE_RENDERER（11p 图表渲染路径）；st.ascii → J.asciiFrame（11p 字符画）。
//   11p_* 表现包一律不在本次范围。

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using JizuraLive.Engine.Packs;
using JizuraLive.Logging;
using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace JizuraLive.Engine;

/// <summary>frame(ctx, plan, t, opt) 的 opt（JS duck-typed 对象的定型）。</summary>
public sealed class RenderOpts
{
    /// <summary>输出缩放（画布像素 = 设计尺寸 * scale）。</summary>
    public double Scale = 1;

    /// <summary>fast：跳过 glow/filter 的性能路径。</summary>
    public bool Fast;

    /// <summary>关掉色差 ghost pass。</summary>
    public bool NoGhost;

    /// <summary>透過PNG（保留 alpha；配合 Layer 分层导出）。</summary>
    public bool Transparent;

    /// <summary>透過分层：'back' = 背景图+歌词后面的装饰，'front' = 其余。</summary>
    public string? Layer;

    /// <summary>K线背景已画好，不能再清底（kline 渲染路径专用）。</summary>
    public bool KlineBg;

    /// <summary>跳过 cut 间转场（转场 A 帧递归、morph 抓取时置真）。</summary>
    public bool NoTrans;

    /// <summary>跳过事件后期 + 纹理后期。</summary>
    public bool NoPost;

    /// <summary>跳过 HUD。</summary>
    public bool NoHud;

    /// <summary>舞台大字模式打点（JS 只透传给 PERF；C# 保留字段）。</summary>
    public bool Stage;

    /// <summary>ascii 帧内层防递归（asciiFrame 未移植，仅保字段）。</summary>
    public bool AsciiInner;

    /// <summary>モーフ字形抓取（非 null 时主 pass 记录每个字形落点，不进整帧缓存）。</summary>
    public List<JText.GlyphLogEntry>? GlyphLog;

    public RenderOpts Clone() => new()
    {
        Scale = Scale,
        Fast = Fast,
        NoGhost = NoGhost,
        Transparent = Transparent,
        Layer = Layer,
        KlineBg = KlineBg,
        NoTrans = NoTrans,
        NoPost = NoPost,
        NoHud = NoHud,
        Stage = Stage,
        AsciiInner = AsciiInner,
        GlyphLog = GlyphLog,
    };
}

/// <summary>J.TRANS[k].draw(ctx, A, B, p, info) 的 C# 形态（M2 定型；11p_treattrans 移植时按实际签名对齐）。</summary>
public delegate void TransDrawFn(SKCanvas ctx, SKImage prevA, SKImage curB, double p, TransCtx info);

/// <summary>trans draw 的 info 对位（JS 的 {cw,ch,sc,scPrev,st,P,step,t,scale,allowFilter,seed,tmp}）。</summary>
public sealed class TransCtx
{
    public int Cw;
    public int Ch;
    public Scheme Sc = new();
    public Scheme ScPrev = new();
    public StyleDef St = null!;
    public JObject P = new();
    public int Step;
    public double T;
    public double Scale;
    public bool AllowFilter;
    public int Seed;

    /// <summary>tmp(w, h)：转场临时画布（复用）。</summary>
    public Func<int, int, SKSurface> Tmp = null!;
}

/// <summary>渲染器：J.Renderer 对位。线程不安全（每画布/每导出管线各建一个）。</summary>
public sealed class Renderer : IDisposable
{
    /// <summary>
    /// 中间面工厂（GPU 光栅接线用）：非空时 scratch/small/tiny/camLayer/transA·B·C/morph/guard 等
    /// 中间面改由它创建（GPU 面，省掉每帧「CPU 面 → GPU 上传」）。null = CPU 面（默认：
    /// 导出/测试/校验路径保持位级一致）。主画布不在此列——由调用方给（Spout 给 GPU 面，预览/导出给 CPU 面）。
    /// </summary>
    public Func<int, int, SKSurface>? SurfaceFactory;

        // 内容 pass 描述符，实例级复用；Frame() 里只刷色差偏移
        private sealed class PassDesc
        {
            public readonly string Pass;
            public readonly double Lag;
            public double OffX;
            public double OffY;

            public PassDesc(string pass, double lag)
            {
                Pass = pass;
                Lag = lag;
            }
        }

        private readonly PassDesc[] _passes =
        {
            new("B", 1.6 / 24),
            new("A", 0.8 / 24),
            new("main", 0),
        };

        // 离屏画布（JS 的 ensure(w,h) 按需 resize 的 canvas 组）
        private SKSurface? _scratch, _small, _tiny, _camLayer, _transA, _transB, _transC, _morphCv, _morphShotCv, _guardP, _guardM;
        private readonly SKBitmap?[] _grain = new SKBitmap?[4];
        private SKBitmap _scan = null!;
        private readonly Dictionary<string, SKBitmap> _paperCache = new();

        // 整帧缓存（JS 的 this.fCache）：冷键不落盘，同键再现才拷贝
        private Plan? _fcPlan;
        private SKImage? _fcCv;
        private string _fcKey = "";
        private string _fcHot = "";

        // モーフ位图缓存 / glyphLog 缓存（按 plan 键控）
        private Plan? _msPlan;
        private Dictionary<string, SKImage>? _msMap;
        private Plan? _mlPlan;
        private Dictionary<string, (List<JText.GlyphLogEntry> A, List<JText.GlyphLogEntry> B)>? _mlMap;

        /// <summary>纸纹/颗粒用确定性伪随机（JS 是 Math.random，纯噪声无位级对齐价值，定 seed 保可复现）。</summary>
        private readonly Random _rng = new(0x20260922);

        /// <summary>性能剖析钩子（性能专项接线；null 时零开销）：(阶段名, 距上次报告 ms, 距上次报告的本线程分配字节)。
        /// 分配量用 GC.GetAllocatedBytesForCurrentThread——渲染都在本线程发生，即阶段归因。线程不安全。</summary>
        public static Action<string, double, long>? StageHook;

        public Renderer()
        {
            for (var k = 0; k < 4; k++)
            {
                var g = new SKBitmap(256, 256);
                FillNoise(g, v => (byte)v, v => (byte)v, v => (byte)v, _ => 255, () => _rng.NextDouble() * 255);
                _grain[k] = g;
            }
            // 1×4：上 3 行白、底 1 行黑（scanline pattern）
            var sl = new SKBitmap(1, 4);
            using (var x = new SKCanvas(sl))
            using (var w = new SKPaint { Color = SKColors.White })
            using (var b = new SKPaint { Color = SKColors.Black })
            {
                x.Clear(SKColors.Transparent);
                x.DrawRect(0, 0, 1, 3, w);
                x.DrawRect(0, 3, 1, 1, b);
            }
            _scan = sl;
        }

        // ---------------- 主入口 ----------------

        /// <summary>把 t 时刻的一帧画进 ctx，画布像素 = 设计尺寸 * scale。</summary>
        public void Frame(SKCanvas ctx, Plan plan, double t, RenderOpts? optIn = null)
        {
            var opt = optIn ?? new RenderOpts();
            var W = plan.W;
            var H = plan.H;
            var scale = opt.Scale != 0 ? opt.Scale : 1;
            var dm = ctx.DeviceClipBounds;
            var cw = Math.Max(1, dm.Width);
            var ch = Math.Max(1, dm.Height);
            // 性能剖析（性能专项临时接线，null 时零开销）：StageHook != null 才计时。
            // (阶段名, 距上次报告的毫秒)；粒度：setup/bgpaper/bgdef/cutpass/camblur/morph/trans/hud/post/fcsave
            var _h0 = StageHook != null ? Stopwatch.GetTimestamp() : 0L;
            var _a0 = StageHook != null ? GC.GetAllocatedBytesForCurrentThread() : 0L;
            void Stage(string name)
            {
                if (StageHook == null) return;
                var now = Stopwatch.GetTimestamp();
                var alloc = GC.GetAllocatedBytesForCurrentThread();
                StageHook(name, (now - _h0) * 1000.0 / Stopwatch.Frequency, alloc - _a0);
                _h0 = now;
                _a0 = alloc;
            }
            var fx = plan.Fx;
            var st = plan.Style;
            var fps = Math.Max(1, plan.Fps);
            // K线风格走完全独立的渲染路径（11p）：图表+中心滚动歌词画满整帧直接返回
            if (Truthy(JLayouts.StTok(st, "kline")))
            {
                P11Kline.Frame(ctx, plan, t, opt, (int)cw, (int)ch);
                return;
            }
            // ASCII 字符画风格（11p）：整帧先按正常管线渲进离屏，再整帧转字符网格；
            // AsciiInner 防 asciiFrame 递归内层再次进入本分支
            if (Truthy(JLayouts.StTok(st, "ascii")) && !opt.AsciiInner)
            {
                P11Ascii.Frame(ctx, plan, t, opt,
                    (inner, innerOpt) => Frame(inner, plan, t, innerOpt));
                return;
            }
            // 动作按 koma 量化（24fps 作画基准）；随机闪烁走 <=24Hz 时钟
            var stepDur = Planner.StepDur(fx, fps);
            var koma = Planner.KomaOf(fx);
            var clock = koma > 0 ? stepDur : 1.0 / 24;
            var tq = Math.Floor(t / stepDur + 1e-6) * stepDur;
            var mainCut = Planner.CutAt(plan, tq);
            var schemes = st.Schemes;
            Scheme Sc0() => schemes.Count > 0 ? schemes[0] : new Scheme();
            var sc = schemes.Count > 0
                ? schemes[mainCut != null ? ((mainCut.Scheme % schemes.Count) + schemes.Count) % schemes.Count : 0]
                : Sc0();
            var allowFilter = !opt.Fast; // Skia 恒有 filter（JS 的 filterOK 能力探测在 C# 恒真）
            JLang.SetLang(string.IsNullOrEmpty(plan.Lang) ? "ja" : plan.Lang); // 字体随歌词语言
            JText.SetTypeset(plan.Typeset); // 文字整列
            var u = H / 1080.0;
            var key = plan.KeyBg is "green" or "black" ? plan.KeyBg : null; // keyBg 合成用：黑底白字，最后 keyFinish() 收尾
            var events = plan.Events;
            // 事件衰减与节拍脉冲按真实 t 连续（不量化）：窗口内整帧缓存不命中，保持逐帧正确
            double spike = 0, shake = 0, beatPulse = 0;
            var evLive = false;
            for (var i = 0; i < events.Count; i++)
            {
                var ev = events[i];
                if (ev.T > t) break;
                if (t < ev.T + Math.Max(ev.Dur, 1.0 / fps)) evLive = true;
                var dt = (t - ev.T) * 24;
                if (dt > 14) continue;
                if (ev.Type == "chroma") spike += ev.Amp * Math.Pow(0.55, dt);
                else if (ev.Type == "shake") shake += ev.Amp * Math.Pow(0.62, dt);
            }
            if (plan.Beats is { Length: > 0 })
            {
                var b = PrevBeat(plan.Beats, t);
                if (b != null && t - b.Value < 0.25) beatPulse = 0.9 * Math.Exp(-(t - b.Value) * 16);
            }
            var step = (int)Math.Floor(tq / clock + 1e-6);
            // energy 取样对齐 koma 时钟：画面一切动作已按 koma 作画，柱状/频谱类跟随同一时钟
            float energy = 0;
            if (plan.Energy is { Length: > 0 } en)
            {
                var eIdx = Math.Clamp((int)Math.Floor(tq * plan.EnergyRate), 0, en.Length - 1);
                energy = en[eIdx];
            }
            // 转场窗口：合成结果依赖真实 t 与两帧内容，整帧缓存绕过（A 静帧由本缓存按自身键命中）
            Cut? tPrev = !opt.NoTrans && mainCut is { Index: > 0 } mc0 ? plan.Cuts[mc0.Index - 1] : null;
            var transDur0 = mainCut is { TransDur: > 0 } ? mainCut.TransDur : 0.35;
            var transOn = !opt.NoTrans && mainCut != null && !string.IsNullOrEmpty(mainCut.Trans)
                && Reg.Trans.ContainsKey(mainCut.Trans) && mainCut.Index > 0
                && tPrev != null && (tq - mainCut.Start) < transDur0
                && Math.Abs(tPrev.End - mainCut.Start) < 0.06;
            // 整帧缓存：画面内容由 (tq, step) 量化量完全决定，同一逐步内的帧逐像素相同——
            // koma=12 时 60fps 播放每秒只需真渲染 12 次，其余整帧贴图。冷键不落盘：
            // 多画布/多 plan 轮转时白付拷贝不值，同一键第二次出现才开始写缓存。
            string? fKey = null;
            if (opt.GlyphLog == null && !opt.KlineBg && opt.Layer == null && !evLive && spike == 0 && shake == 0
                && beatPulse <= 0.02 && !transOn)
            {
                if (!ReferenceEquals(plan, _fcPlan))
                {
                    _fcPlan = plan;
                    _fcKey = "";
                    _fcHot = "";
                }
                fKey = string.Create(CultureInfo.InvariantCulture,
                    $"{tq.ToString(CultureInfo.InvariantCulture)}:{step}:{cw}x{ch}:{scale.ToString("F4", CultureInfo.InvariantCulture)}:" +
                    $"{(opt.Fast ? 1 : 0)}{(opt.Transparent ? 1 : 0)}{(opt.NoGhost ? 1 : 0)}{(opt.NoHud ? 1 : 0)}{(opt.NoPost ? 1 : 0)}{(opt.NoTrans ? 1 : 0)}" +
                    $":{key}:{eIdxIdx(plan, tq)}");
            }
            if (fKey != null && fKey == _fcKey && _fcCv != null && _fcCv.Width == cw && _fcCv.Height == ch)
            {
                ctx.Save();
                ctx.SetMatrix(SKMatrix.Identity);
                using (var p = new SKPaint { BlendMode = SKBlendMode.Src }) // JS globalCompositeOperation='copy'
                    ctx.DrawImage(_fcCv, 0, 0, p);
                ctx.Restore();
                return;
            }
            Stage("setup");
            ctx.Save();
            ctx.SetMatrix(SKMatrix.Identity);
            ctx.Clear(); // !opt.klineBg 才清（K线背景已画好不能清掉；kline 路径上面已抛）
            ctx.SetMatrix(SKMatrix.CreateScale((float)scale, (float)scale));
            var bgCol = sc.Str("bg") ?? "#000000";
            if (key != null && !opt.Transparent)
            {
                RectFill(ctx, 0, 0, W, H, Env.SkColor("#000000"));
            }
            else if (!opt.Transparent && !opt.KlineBg)
            {
                // 底色 + 中央提亮渐变：两者合成结果只依赖 (bgCol, 渐变α, 尺寸, scale)，逐帧不变 →
                // 预渲染成一张不透明缓存图 1:1 贴回。不透明像素的 premul 存储是无损的（缓存里的合成
                // 取整与画布上「先填底色再叠渐变」逐位相同），不像半透明中转会引入 ±1 抖动。
                DrawGradImage(ctx, BgBakedImage(bgCol, cw, ch, W, H, scale,
                    J.Lum(bgCol) < 0.5 ? 0.045 : 0.10));
                var paperAmt = (Truthy(sc.Get("paper")) ? 1 : StTextureNum(st, "paper")) * fx.Texture;
                if (paperAmt > 0.02)
                {
                    var dark = J.Lum(bgCol) < 0.4;
                    // JS 用 ctx.filter='invert(1)' 现场反相；C# 预生成反相纸纹（暗底 screen 亮噪 ≈ 同效果）
                    // 性能：SKImage 缓存复用（每帧 FromBitmap 会白白产出一个 finalizable 对象）
                    var paperImg = PaperImage(W, H, dark);
                    // 每帧一次的低频绘制保留一次性 paint（性能专项实测：低频位共用共享 paint 会引入
                    // 难以溯源的状态残留；真正的高频热点是 Env 助手里的逐 decor/逐字形绘制）
                    using var pp = new SKPaint
                    {
                        BlendMode = dark ? SKBlendMode.Screen : SKBlendMode.Multiply,
                        Color = SKColors.White.WithAlpha((byte)Math.Round((dark ? paperAmt * 0.06 : paperAmt * 0.85) * 255)),
                    };
                    ctx.DrawImage(paperImg, new SKRect(0, 0, (float)W, (float)H), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), pp);
                }
            }
            Stage("bgpaper");
            var stGhost = StNum(st, "ghost", 1);
            var chroma = fx.Chroma * stGhost * (1 + spike + beatPulse);
            var beatInfo = plan.Beats is { Length: > 0 } ? BeatAt(plan.Beats, tq) : null;
            // 透過PNG 前后景分层（opt.layer）：'back' = 背景图+歌词后面的装饰，'front' = 其余
            var layer = opt.Transparent ? opt.Layer ?? null : null;
            if ((!opt.Transparent || layer == "back") && key == null && !opt.KlineBg && mainCut != null
                && !string.IsNullOrEmpty(mainCut.Bg) && mainCut.Bg != "none"
                && Reg.Bg.TryGetValue(mainCut.Bg, out var bgDef))
            {
                var env = MakeEnv(ctx, plan, mainCut, sc,
                    t: tq, lt: tq - mainCut.Start, ltb: tq - mainCut.Start, stepI: step, scale: scale,
                    allowFilter: allowFilter, energy: energy, beatInfo: beatInfo,
                    pass: "main", passColor: null, layer: layer, zone: null, hideText: false, glyphLog: null);
                ctx.Save();
                try
                {
                    using var _bg = new Prof.Scope("bg", mainCut.Bg); // 叶子归因（性能专项）
                    bgDef.Draw?.Invoke(env); // JS draw(env, cut.bgP || {})：C# 的 BgDef.Draw(env) 从 env.Cut.BgP 取
                }
                catch (Exception e)
                {
                    WarnThrottled("bg", mainCut.Bg, e);
                }
                ctx.Restore();
            }
            Stage("bgdef");
            var shx = J.Rs(step, 71) * shake * 16 * u;
            var shy = J.Rs(step, 72) * shake * 11 * u;
            _passes[0].OffX = -3.4 * chroma * u;
            _passes[0].OffY = -1.3 * chroma * u;
            _passes[1].OffX = 3.2 * chroma * u;
            _passes[1].OffY = 1.9 * chroma * u;
            var ghostOn = fx.Chroma > 0.02 && stGhost > 0.02 && !opt.NoGhost;
            // morph：cut 开头一小段时间，歌词改由 drawMorph 画（字形滑移/融化）
            var MC = !opt.NoTrans && opt.GlyphLog == null && mainCut is { Index: > 0 } mc1 && mc1.Morph != null ? mc1 : null;
            var mPrev = MC != null ? plan.Cuts[MC.Index - 1] : null;
            var mlt = MC != null ? tq - MC.Start : 0;
            var morphDur = MC?.Morph != null ? NumTok(MC.Morph, "dur") : 0;
            var morphOn = MC != null && mPrev != null && mlt < morphDur && Math.Abs(mPrev.End - MC.Start) < 0.06;
            // 镜头虚焦整层只做一次 blur：每个 draw call 都挂 blur filter，多行文字时慢得没法用
            double layerBlur = 0;
            SKCanvas? LX = null;
            if (allowFilter && mainCut != null && mainCut.Cam != "push" && Reg.Camera.TryGetValue(mainCut.Cam, out var camDef0))
            {
                try
                {
                    var e0 = MakeEnv(ctx, plan, mainCut, sc,
                        t: tq, lt: tq - mainCut.Start, ltb: tq - mainCut.Start, stepI: step, scale: scale,
                        allowFilter: allowFilter, energy: energy, beatInfo: beatInfo,
                        pass: "main", passColor: null, layer: layer, zone: null, hideText: false, glyphLog: null);
                    var c0 = camDef0.Get?.Invoke(e0);
                    if (c0 != null && c0.Blur > 0.4) layerBlur = c0.Blur;
                }
                catch
                {
                    // JS 空 catch 对位
                }
                if (layerBlur > 0)
                {
                    var L = Ensure(ref _camLayer, cw, ch);
                    LX = L.Canvas;
                    LX.SetMatrix(SKMatrix.Identity);
                    LX.Clear();
                    LX.SetMatrix(SKMatrix.CreateScale((float)scale, (float)scale));
                }
            }
            foreach (var P in _passes)
            {
                if (P.Pass != "main" && !ghostOn) continue;
                var tp = Math.Max(0, tq - P.Lag);
                var cut0 = P.Lag > 0 ? Planner.CutAt(plan, tp) : mainCut;
                if (cut0 == null) continue;
                if (morphOn && !ReferenceEquals(cut0, mainCut)) continue;
                // 中央を空ける：cut 画在自己一侧的 band，companion 画在另一侧
                var cutList = plan.CenterFree && cut0.Companion != null ? new[] { cut0, cut0.Companion! } : new[] { cut0 };
                foreach (var cut in cutList)
                {
                    var csc = schemes.Count > 0
                        ? schemes[((cut.Scheme % schemes.Count) + schemes.Count) % schemes.Count]
                        : sc;
                    var lt = tp - cut.Start;
                    var X = LX ?? ctx;
                    var Z = plan.CenterFree ? cut.Zone : null;
                    var env = MakeEnv(X, plan, cut, csc,
                        t: tp, lt: lt, ltb: lt + P.Lag, stepI: (int)Math.Floor(tp / clock + 1e-6), scale: scale,
                        allowFilter: allowFilter, energy: energy, beatInfo: beatInfo,
                        pass: P.Pass,
                        passColor: P.Pass == "A" ? csc.Str("ghostA") : P.Pass == "B" ? csc.Str("ghostB") : null,
                        layer: layer, zone: Z, hideText: morphOn,
                        glyphLog: P.Pass == "main" ? opt.GlyphLog : null);
                    X.Save();
                    // cut 的文字、装饰、镜头都裁剪在自己的 band 内
                    if (Z != null)
                    {
                        X.ClipRect(new SKRect(Z.X, Z.Y, Z.X + Z.W, Z.Y + Z.H));
                        X.Translate(Z.X, Z.Y);
                    }
                    var envW = env.W;
                    var envH = env.H;
                    // 该 cut 的镜头运动，默认缓推
                    if (!Reg.Camera.TryGetValue(cut.Cam, out var camDef) || camDef == null)
                        Reg.Camera.TryGetValue("push", out camDef);
                    CamState? cam = null;
                    try
                    {
                        cam = camDef?.Get?.Invoke(env);
                    }
                    catch
                    {
                        cam = null;
                    }
                    cam ??= new CamState();
                    X.Translate((float)(envW / 2.0 + shx + P.OffX + cam.X), (float)(envH / 2.0 + shy + P.OffY + cam.Y));
                    if (cam.Rot != 0) X.RotateDegrees((float)cam.Rot); // JS: rotate(cam.rot * DEG) 收弧度；RotateDegrees 直接收度
                    if (cam.Skx != 0) X.Concat(SKMatrix.CreateSkew((float)Math.Tan(cam.Skx * J.DEG), 0));
                    // TODO(11p)：JS 还有 cam.sx/cam.sy（11p 相机用）；CamState 暂无该字段，按 s 统一处理
                    X.Scale((float)cam.S, (float)cam.S);
                    X.Translate((float)-envW / 2, (float)-envH / 2);
                    // ghost pass 的整 pass 染色（JS 的 X.globalCompositeOperation）
                    env.Blend = P.Pass != "main" && J.Lum(csc.Str("bg") ?? "#000000") > 0.55
                        ? SKBlendMode.Multiply
                        : SKBlendMode.SrcOver;
                    DrawCut(env);
                    X.Restore();
                }
            }
            Stage("cutpass");
            if (LX != null)
            {
                ctx.Save();
                ctx.SetMatrix(SKMatrix.Identity);
                using (var bp = new SKPaint { ImageFilter = JText.BlurFilter(layerBlur * scale) })
                using (var snap = _camLayer!.Snapshot())
                    ctx.DrawImage(snap, 0, 0, bp);
                ctx.Restore();
            }
            Stage("camblur");
            if (morphOn && layer != "back" && MC != null && mPrev != null)
            {
                var L = MorphLogs(plan, mPrev, MC, cw, ch, scale, opt);
                var kk = J.Clamp(mlt / morphDur);
                var e = kk < 0.5 ? 4 * kk * kk * kk : 1 - Math.Pow(-2 * kk + 2, 3) / 2;
                // koma=0（逐输出帧，如 60fps 工程）时 morph 连续现绘；位图只有 10 档 + ≤960 宽，
                // 满帧率下会看到阶梯。koma>0（24 基准作画）维持位图路径
                if (koma == 0) DrawMorph(ctx, L, e, allowFilter);
                else MorphShot(ctx, plan, mPrev, MC, cw, ch, scale, e, allowFilter, L);
            }
            Stage("morph");
            // cut 间转场：用上一 cut 的收尾帧和当前帧合成（窗口判定已在开头算好：transOn）
            if (transOn && mainCut != null && mPrev2(plan, mainCut) is { } prev)
            {
                var lt = tq - mainCut.Start;
                var dur = mainCut.TransDur > 0 ? mainCut.TransDur : 0.35;
                if (lt < dur && Math.Abs(prev.End - mainCut.Start) < 0.06
                    && Reg.Trans.TryGetValue(mainCut.Trans!, out var tdef))
                {
                    var A = Ensure(ref _transA, cw, ch);
                    var B = Ensure(ref _transB, cw, ch);
                    var bx = B.Canvas;
                    bx.SetMatrix(SKMatrix.Identity);
                    using (var cp = new SKPaint { BlendMode = SKBlendMode.Src })
                    using (var cur = SnapshotOf(ctx))
                        bx.DrawImage(cur, 0, 0, cp);
                    var prevOpt = opt.Clone();
                    prevOpt.NoTrans = true;
                    prevOpt.NoPost = true;
                    prevOpt.NoHud = true;
                    Frame(A.Canvas, plan, Math.Max(prev.Start, prev.End - 1e-3), prevOpt);
                    var psc = schemes.Count > 0
                        ? schemes[((prev.Scheme % schemes.Count) + schemes.Count) % schemes.Count]
                        : sc;
                    ctx.Save();
                    ctx.SetMatrix(SKMatrix.Identity);
                    // 透過出力跟 post 一样套 alphaGuard：转场普遍从 sc.bg 全幅填起，不 fence 会把空背景涂实
                    var guard = opt.Transparent ? NewGuard(cw, ch) : null;
                    guard?.Begin(ctx);
                    try
                    {
                        if (tdef.Draw is TransDrawFn draw)
                        {
                            // 注：A/B 快照此处**不能** using 释放——Skia 对未变更表面复用同一张快照镜像，
                            // SkiaSharp 又按原生句柄去重托管包装，Dispose 可能连带释放整帧缓存 _fcCv 引用的
                            // 同一对象（实测 sk_image_get_width AV）。要治「转场帧快照进终结器」需另设
                            // 按 surface 的快照缓存，不能用局部 using。
                            draw(ctx, A.Snapshot(), B.Snapshot(), J.Clamp(lt / dur), new TransCtx
                            {
                                Cw = cw,
                                Ch = ch,
                                Sc = sc,
                                ScPrev = psc,
                                St = st,
                                P = mainCut.TransP ?? new JObject(),
                                Step = step,
                                T = t,
                                Scale = scale,
                                AllowFilter = allowFilter,
                                Seed = unchecked((int)mainCut.Seed),
                                Tmp = (w, h) => Ensure(ref _transC, w, h),
                            });
                        }
                        else
                        {
                            // TODO(11p)：TransDef.Draw 的 C# 签名未定型（trans 注册表 core 为空，走不到）
                            Log.Warn($"trans {mainCut.Trans}: draw 签名不是 TransDrawFn，跳过");
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Warn($"trans {mainCut.Trans}: {e.Message}");
                    }
                    guard?.End(ctx);
                    ctx.Restore();
                }
            }
            Stage("trans");
            if (plan.Hud && !opt.NoHud && layer != "back")
            {
                var env = MakeEnv(ctx, plan, mainCut, sc,
                    t: tq, lt: 0, ltb: 0, stepI: step, scale: scale,
                    allowFilter: allowFilter, energy: energy, beatInfo: beatInfo,
                    pass: "main", passColor: null, layer: layer, zone: null, hideText: false, glyphLog: null);
                JDecor.DrawHud(env, plan);
            }
            Stage("hud");
            ctx.Restore();
            if (!opt.NoPost) Post(ctx, plan, t, tq, step, sc, scale, opt, allowFilter);
            if (key != null && !opt.NoPost) KeyFinish(ctx, key, opt);
            Stage("post");
            // 整帧缓存落盘：冷键只记 hot，同键再现才真正拷贝（首帧 warm-up 后开始命中）
            if (fKey != null)
            {
                if (fKey == _fcHot)
                {
                    _fcCv?.Dispose();
                    _fcCv = SnapshotOf(ctx);
                    _fcKey = fKey;
                }
                else
                {
                    _fcHot = fKey;
                    _fcKey = "";
                }
            }
            Stage("fcsave");
        }

        private static Cut? mPrev2(Plan plan, Cut mainCut) =>
            mainCut.Index > 0 ? plan.Cuts[mainCut.Index - 1] : null;

        // eIdx 已进 fKey（能量样本号）；占位保持键形一致
        private static int eIdxIdx(Plan plan, double tq) =>
            plan.Energy is { Length: > 0 } ? Math.Clamp((int)Math.Floor(tq * plan.EnergyRate), 0, plan.Energy.Length - 1) : 0;

        // ---------------- keyFinish（keyBg 合成收尾） ----------------

        /// <summary>
        /// 合成用收尾：整帧先做成黑底白字（黑=空）。key=green 时再 screen 上 #00FF00，
        /// 黑变绿、白保持白，灰阶（ghost/辉光/淡出）键控后即成半透明。
        /// </summary>
        private void KeyFinish(SKCanvas ctx, string key, RenderOpts opt)
        {
            var cw = Math.Max(1, ctx.DeviceClipBounds.Width);
            var ch = Math.Max(1, ctx.DeviceClipBounds.Height);
            ctx.Save();
            ctx.SetMatrix(SKMatrix.Identity);
            if (opt.Transparent)
            {
                // 保留 alpha：过一层拷贝再去色（ctx.filter='grayscale(1)' → 亮度加权色矩阵）
                var S = Ensure(ref _scratch, cw, ch);
                var sx = S.Canvas;
                sx.SetMatrix(SKMatrix.Identity);
                sx.Clear();
                using (var img = SnapshotOf(ctx))
                using (var p = new SKPaint { ColorFilter = GrayscaleFilter() })
                    sx.DrawImage(img, 0, 0, p);
                using (var sp = new SKPaint { BlendMode = SKBlendMode.Src })
                using (var img2 = S.Snapshot())
                    ctx.DrawImage(img2, 0, 0, sp);
            }
            else
            {
                // 不透明帧：和任意灰做 saturation 混合，保明度去色
                RectFill(ctx, 0, 0, cw, ch, new SKColor(128, 128, 128), SKBlendMode.Saturation);
                if (key == "green") RectFill(ctx, 0, 0, cw, ch, Env.SkColor("#00FF00"), SKBlendMode.Screen);
            }
            ctx.Restore();
        }

        private static SKColorFilter GrayscaleFilter() => SKColorFilter.CreateColorMatrix(new float[]
        {
            0.2126f, 0.7152f, 0.0722f, 0, 0,
            0.2126f, 0.7152f, 0.0722f, 0, 0,
            0.2126f, 0.7152f, 0.0722f, 0, 0,
            0, 0, 0, 1, 0,
        });

        // ---------------- モーフ ----------------

        /// <summary>上一 cut 每个字形的停驻位置 + 本 cut 字形的落点（glyphLog 抓取，带缓存）。</summary>
        private (List<JText.GlyphLogEntry> A, List<JText.GlyphLogEntry> B) MorphLogs(
            Plan plan, Cut A, Cut B, int cw, int ch, double scale, RenderOpts opt)
        {
            if (!ReferenceEquals(_mlPlan, plan))
            {
                _mlPlan = plan;
                _mlMap = new Dictionary<string, (List<JText.GlyphLogEntry>, List<JText.GlyphLogEntry>)>();
            }
            var map = _mlMap!;
            var key = A.Index + ":" + B.Index + ":" + cw + "x" + ch + ":" + scale.ToString("F4", CultureInfo.InvariantCulture);
            if (map.TryGetValue(key, out var hit)) return hit;
            var x = Ensure(ref _morphCv, cw, ch).Canvas;
            var o2 = new RenderOpts
            {
                Scale = scale,
                NoPost = true,
                NoHud = true,
                NoTrans = true,
                NoGhost = true,
                Transparent = opt.Transparent,
                Fast = true,
            };
            var la = new List<JText.GlyphLogEntry>();
            var lb = new List<JText.GlyphLogEntry>();
            o2.GlyphLog = la;
            Frame(x, plan, Math.Max(A.Start, A.End - 1e-3), o2);
            o2.GlyphLog = lb;
            Frame(x, plan, B.Start + (B.Morph != null ? NumTok(B.Morph, "dur") : 0) + 1e-3, o2);
            var r = (la, lb);
            map[key] = r;
            if (map.Count > 24) map.Remove(FirstKey(map));
            return r;
        }

        /// <summary>モーフ帧位图缓存：按 e 量化 10 档存位图。位图降到 ≤960 宽存储。</summary>
        private void MorphShot(SKCanvas ctx, Plan plan, Cut A, Cut B, int cw, int ch, double scale, double e,
            bool allowFilter, (List<JText.GlyphLogEntry> A, List<JText.GlyphLogEntry> B) L)
        {
            var shot = MorphShotBuild(plan, A, B, cw, ch, scale, e, allowFilter, L);
            ctx.Save();
            ctx.SetMatrix(SKMatrix.Identity);
            using (var p = new SKPaint())
                ctx.DrawImage(shot, new SKRect(0, 0, cw, ch), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), p);
            ctx.Restore();
        }

        /// <summary>生成/取回某一档位的 morph 位图，预热可直接调它填缓存。</summary>
        public SKImage MorphShotBuild(Plan plan, Cut A, Cut B, int cw, int ch, double scale, double e,
            bool allowFilter, (List<JText.GlyphLogEntry> A, List<JText.GlyphLogEntry> B) L)
        {
            if (!ReferenceEquals(_msPlan, plan))
            {
                _msPlan = plan;
                _msMap = new Dictionary<string, SKImage>();
            }
            var map = _msMap!;
            var q = Math.Clamp((int)Math.Round(e * 9), 0, 9);
            var eq = q / 9.0;
            var key = A.Index + ":" + B.Index + ":" + cw + "x" + ch + ":" + scale.ToString("F4", CultureInfo.InvariantCulture) +
                      ":" + q + (allowFilter ? "f" : "n");
            if (map.TryGetValue(key, out var got)) return got;
            var full = Ensure(ref _morphShotCv, cw, ch);
            var fxc = full.Canvas;
            fxc.SetMatrix(SKMatrix.Identity);
            fxc.Clear();
            DrawMorph(fxc, L, eq, allowFilter);
            var sw = Math.Min(960, cw);
            var sh = Math.Max(1, (int)Math.Round(ch * sw / (double)cw));
            SKImage shot;
            using (var shotS = SKSurface.Create(new SKImageInfo(sw, sh)))
            {
                using (var fullImg = full.Snapshot())
                using (var p = new SKPaint())
                    shotS.Canvas.DrawImage(fullImg, new SKRect(0, 0, cw, ch), new SKRect(0, 0, sw, sh),
                        new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), p);
                shot = shotS.Snapshot();
            }
            map[key] = shot;
            if (map.Count > 12)
            {
                var old = FirstKey(map);
                map[old].Dispose();
                map.Remove(old);
            }
            return shot;
        }

        /// <summary>drawMorph：旧字化掉（下坠/膨胀/模糊）、共有字滑到新位置、新字从模糊里凝出。</summary>
        private static void DrawMorph(SKCanvas ctx,
            (List<JText.GlyphLogEntry> A, List<JText.GlyphLogEntry> B) L, double e, bool allowFilter)
        {
            var A = L.A;
            var B = L.B;
            var used = new bool[A.Count];
            var pairA = new List<JText.GlyphLogEntry?>(B.Count);
            var pairB = new List<JText.GlyphLogEntry>(B.Count);
            foreach (var g in B)
            {
                int j = -1;
                for (var q = 0; q < A.Count; q++)
                    if (!used[q] && A[q].Ch == g.Ch)
                    {
                        j = q;
                        break;
                    }
                if (j >= 0) used[j] = true;
                pairA.Add(j >= 0 ? A[j] : null);
                pairB.Add(g);
            }
            static double Det(float[] m)
            {
                var d = Math.Sqrt(Math.Abs((double)m[0] * m[3] - (double)m[1] * m[2]));
                return d > 0 ? d : 1;
            }
            void Put(JText.GlyphLogEntry g, string col, double alpha, double blur)
            {
                if (alpha <= 0.01) return;
                var font = JText.FaceFont(g.FontKey ?? "", g.Px); // 缓存实例，禁 Dispose
                font.GetFontMetrics(out var fm);
                var yMid = (float)((-fm.Ascent - fm.Descent) / 2); // textBaseline='middle'
                using var p = new SKPaint { IsAntialias = true };
                // 滤镜走 JText 共享记忆工厂：不 Dispose（性能专项）
                var f = allowFilter && blur > 0.4 ? JText.BlurFilter(blur) : null;
                p.ImageFilter = f;
                if (g.Fill)
                {
                    p.Color = Env.SkColor(col, Math.Min(1, alpha));
                    ctx.DrawText(g.Ch, 0, yMid, SKTextAlign.Center, font, p);
                }
                if (g.Stroke > 0)
                {
                    p.IsStroke = true;
                    p.StrokeJoin = SKStrokeJoin.Round;
                    p.StrokeWidth = (float)g.Stroke;
                    p.Color = Env.SkColor(g.StrokeColor ?? col, alpha);
                    ctx.DrawText(g.Ch, 0, yMid, SKTextAlign.Center, font, p);
                }
            }
            ctx.Save();
            // 旧句剩下的字化掉：下坠、膨胀、模糊
            for (var q = 0; q < A.Count; q++)
            {
                if (used[q]) continue;
                var a = A[q];
                ctx.SetMatrix(MOf(a.M));
                ctx.Translate(0, (float)(e * a.Px * 0.45));
                ctx.Scale((float)(1 + e * 0.12), (float)(1 + e * 0.6));
                Put(a, a.Color, a.A * Math.Pow(1 - e, 1.4), e * a.Px * 0.14 * Det(a.M));
            }
            // 共有字滑到新位置，新字从模糊里凝出
            for (var pi = 0; pi < pairB.Count; pi++)
            {
                var a = pairA[pi];
                var b = pairB[pi];
                if (a != null)
                {
                    var m = new float[6];
                    for (var i = 0; i < 6; i++) m[i] = (float)(a.M[i] + (b.M[i] - a.M[i]) * e);
                    ctx.SetMatrix(MOf(m));
                    var kpx = a.Px / b.Px;
                    var k = kpx + (1 - kpx) * e;
                    ctx.Scale((float)k, (float)k);
                    var col = IsHex(a.Color) && IsHex(b.Color) ? J.Mix(a.Color, b.Color, e) : e < 0.5 ? a.Color : b.Color;
                    Put(b, col, a.A + (b.A - a.A) * e, 0);
                }
                else
                {
                    var k = 1 - e;
                    ctx.SetMatrix(MOf(b.M));
                    ctx.Translate(0, (float)(-k * b.Px * 0.3));
                    ctx.Scale((float)(1 + k * 0.1), (float)(1 + k * 0.4));
                    Put(b, b.Color, b.A * e, k * b.Px * 0.14 * Det(b.M));
                }
            }
            ctx.Restore();
        }

        private static SKMatrix MOf(float[] m)
        {
            // JS setTransform(a,b,c,d,e,f)：x' = a·x + c·y + e → Skia 列 [scaleX skewX transX / skewY scaleY transY]
            var r = SKMatrix.Identity;
            r.ScaleX = m[0];
            r.SkewX = m[2];
            r.TransX = m[4];
            r.SkewY = m[1];
            r.ScaleY = m[3];
            r.TransY = m[5];
            return r;
        }

        private static readonly Regex HexRe = new("^#[0-9a-fA-F]{3,8}$", RegexOptions.Compiled);

        private static bool IsHex(string s) => HexRe.IsMatch(s);

        // ---------------- drawCut ----------------

        /// <summary>
        /// 包异常日志的按 (类, 名) 去重 + 5s 节流：坏包是**逐帧**抛（每帧数次），原样每帧写一条
        /// 日志 = 渲染线程被日志 I/O 拖住（实测 knZoomDive 每秒 60+ 条）。首条立即记，窗口内
        /// 只计 suppressed 数，窗口后带计数汇总一条。
        /// </summary>
        private readonly Dictionary<string, (long First, int Count, long LastLog)> _warnThrottle = new();

        private void WarnThrottled(string kind, string name, Exception e)
        {
            var key = kind + " " + name;
            var now = Environment.TickCount64;
            if (_warnThrottle.TryGetValue(key, out var st))
            {
                if (now - st.LastLog < 5000)
                {
                    _warnThrottle[key] = (st.First, st.Count + 1, st.LastLog);
                    return;
                }

                var more = st.Count > 0 ? $"（此前 5s 内另有 {st.Count} 次同类）" : "";
                _warnThrottle[key] = (now, 0, now);
                Log.Warn($"{kind} {name}: {e.Message}{more}");
                return;
            }

            _warnThrottle[key] = (now, 0, now);
            Log.Warn($"{kind} {name}: {e.Message}");
        }

        private void DrawCut(Env env)
        {
            var cut = env.Cut!;
            if (!Reg.Layouts.TryGetValue(cut.Layout, out var layout) || layout == null)
                Reg.Layouts.TryGetValue("center", out layout);
            var decor = cut.Decor;
            if (env.Layer != "front")
                foreach (var d in decor)
                    if (Reg.Decor.TryGetValue(d.Id, out var D) && D.Layer == "back")
                        try
                        {
                            using var _pb = new Prof.Scope("decorback", d.Id); // 叶子归因（性能专项）
                            D.Draw?.Invoke(env, d); // JS draw(env, null, d)：后景装饰 bb 由 env.Body 携带（可为空）
                        }
                        catch (Exception e)
                        {
                            WarnThrottled("decor", d.Id, e);
                        }
            if (env.Layer == "back")
            {
                env.Body = null; // 後景だけ：歌词和前景装饰归另一层
                return;
            }
            env.Body = null; // 清掉上一 cut/上一 decor 的残留（JS render 返回值语义）
            try
            {
                using var _pl = new Prof.Scope("layout", cut.Layout); // 叶子归因（性能专项）
                layout?.Draw?.Invoke(env); // JS render(env) 返回主体 bbox → C# 写 env.Body
            }
            catch (Exception e)
            {
                WarnThrottled("layout", cut.Layout, e);
            }
            var bb = env.Body;
            foreach (var d in decor)
                if (Reg.Decor.TryGetValue(d.Id, out var D) && D.Layer == "front")
                {
                    env.Body = bb;
                    try
                    {
                        using var _pf = new Prof.Scope("decorfront", d.Id); // 叶子归因（性能专项）
                        D.Draw?.Invoke(env, d);
                    }
                    catch (Exception e)
                    {
                        WarnThrottled("decor", d.Id, e);
                    }
                }
            env.Body = bb;
        }

        // ---------------- 透過PNG 的特效围栏 ----------------

        /// <summary>
        /// alphaGuard 的 C# 对位：JS 靠 monkey-patch fillRect/drawImage 拦「整幅不透明填充/内容副本」；
        /// C# 画布拦不了——内置 FX 都是我们自己的移植代码，直接调 ContentDraw（S 副本镜像进 M）
        /// 与 Cleared（整幅清底，TODO(11p)：转场类的整幅 sc.bg 填充走这里）。
        /// end()：cleared 时只留 FX 期间画进 M 的内容，否则 P（特效前内容）∪ M，最后 destination-in。
        /// </summary>
        private sealed class AlphaGuard
        {
            private readonly SKSurface _p, _m;
            public bool Cleared;

            public AlphaGuard(SKSurface p, SKSurface m)
            {
                _p = p;
                _m = m;
            }

            public void Begin(SKCanvas ctx)
            {
                Cleared = false;
                var pc = _p.Canvas;
                pc.SetMatrix(SKMatrix.Identity);
                pc.Clear();
                using (var img = SnapshotOf(ctx))
                using (var p = new SKPaint())
                    pc.DrawImage(img, 0, 0, p);
                _m.Canvas.SetMatrix(SKMatrix.Identity);
                _m.Canvas.Clear();
            }

            /// <summary>内容副本（S 的偏移/缩放/镜像块）：既画到 ctx，也镜像进 M（这部分是要留下的内容）。</summary>
            public void ContentDraw(SKCanvas ctx, SKImage img, SKRect dst, SKPaint paint) =>
                ContentDraw(ctx, img, new SKRect(0, 0, img.Width, img.Height), dst, paint);

            public void ContentDraw(SKCanvas ctx, SKImage img, SKRect src, SKRect dst, SKPaint paint)
            {
                ctx.DrawImage(img, src, dst, paint);
                var mc = _m.Canvas;
                mc.SetMatrix(SKMatrix.Identity);
                using var mp = new SKPaint { Color = SKColors.White.WithAlpha(paint.Color.Alpha) };
                mc.DrawImage(img, src, dst, mp);
            }

            public void End(SKCanvas ctx)
            {
                var mc = _m.Canvas;
                mc.SetMatrix(SKMatrix.Identity);
                if (!Cleared)
                using (var pimg = _p.Snapshot())
                using (var p = new SKPaint())
                    mc.DrawImage(pimg, 0, 0, p);
                ctx.Save();
                ctx.SetMatrix(SKMatrix.Identity);
                using (var mimg = _m.Snapshot())
                using (var dp = new SKPaint { BlendMode = SKBlendMode.DstIn })
                    ctx.DrawImage(mimg, 0, 0, dp);
                ctx.Restore();
            }
        }

        private AlphaGuard NewGuard(int cw, int ch) =>
            new(Ensure(ref _guardP, cw, ch), Ensure(ref _guardM, cw, ch));

        // ---------------- post（事件后期 + 纹理） ----------------

        private void Post(SKCanvas ctx, Plan plan, double t, double tq, int step, Scheme sc, double scale,
            RenderOpts opt, bool allowFilter)
        {
            var cw = Math.Max(1, ctx.DeviceClipBounds.Width);
            var ch = Math.Max(1, ctx.DeviceClipBounds.Height);
            // 性能剖析（与 Frame.Stage 同款；fx: 前缀的子阶段）
            var _h0 = StageHook != null ? Stopwatch.GetTimestamp() : 0L;
            var _a0 = StageHook != null ? GC.GetAllocatedBytesForCurrentThread() : 0L;
            void Stage(string name)
            {
                if (StageHook == null) return;
                var now = Stopwatch.GetTimestamp();
                var alloc = GC.GetAllocatedBytesForCurrentThread();
                StageHook(name, (now - _h0) * 1000.0 / Stopwatch.Frequency, alloc - _a0);
                _h0 = now;
                _a0 = alloc;
            }
            var fx = plan.Fx;
            var st = plan.Style;
            var active = new List<PlanEvent>();
            var evWin = 1.0 / plan.Fps;
            foreach (var ev in plan.Events)
                if (t >= ev.T && t < ev.T + Math.Max(ev.Dur, evWin))
                    active.Add(ev);
            var glow = StNum(st, "glow", 0.6) * 0.5 * fx.Texture;
            // 11p：非内置 FX（fxB/looks 插件）也要 scratch 帧（JS 的 needScratch 含 D.scratch——
            // C# 对全部插件恒拷贝，是否读 S 由各 Draw 决定，行为一致）
            var needScratch = active.Any(ev => ev.Type is "slice" or "block" or "zoom" or "mosaic")
                              || active.Any(ev => Reg.Fxe.TryGetValue(ev.Type, out var pd) && !pd.Builtin && pd.Draw != null)
                              || (!opt.Fast && glow > 0);
            SKSurface? S = null;
            if (needScratch) S = Ensure(ref _scratch, cw, ch);
            ctx.Save();
            ctx.SetMatrix(SKMatrix.Identity);
            var clock24 = (int)Math.Floor(t * 24); // glitch 随机数在任何输出帧率下至多 24 次/秒
            var guard = opt.Transparent ? NewGuard(cw, ch) : null;
            foreach (var ev in active)
            {
                // 进度钳到 0..1：比一输出帧还短的事件也要显示那一帧，否则 k 会冲过 1
                var k0 = (t - ev.T) / Math.Max(ev.Dur, 1e-3);
                var k = double.IsFinite(k0) ? J.Clamp(k0, 0, 1) : 0;
                var st2 = clock24;
                guard?.Begin(ctx);
                // —— 11p 非内置 FX 插件路径（fxB/looks）——
                // JS 的 D.draw(ctx, ev, k, I)：FxDef.Draw 签名是 (Env, PlanEvent)（Registry 冻结装不下
                // (k, I)），k/cw/ch/S/tmp/tmp2/transparent 经 P11FxB.FxPlug 桥传递；I.sc/st/step/
                // allowFilter 由 env.Sc/env.St/env.Step（= clock24，JS 同源）/env.AllowFilter 承载。
                // 透過 + alphaGuard：插件内部绘制无法镜像进 M（JS 靠 monkey-patch ctx），End() 退化为
                // P ∪ 空间 alpha 裁剪——插件新增的不透明区域会被裁掉（保守降级，TODO(11p)）。
                var fxDef = Reg.Fxe.GetValueOrDefault(ev.Type);
                if (fxDef is { Builtin: false, Draw: not null })
                {
                    if (S != null) CopyScratch(ctx, S);
                    P11FxB.FxBegin(cw, ch, k, S,
                        (w, h) => Ensure(ref _tiny, w, h),
                        (w, h) => Ensure(ref _small, w, h),
                        opt.Transparent);
                    var fxEnv = MakeEnv(ctx, plan, Planner.CutAt(plan, tq), sc,
                        t: t, lt: 0, ltb: 0, stepI: st2, scale: scale,
                        allowFilter: allowFilter, energy: 0, beatInfo: null,
                        pass: "main", passColor: null, layer: null, zone: null, hideText: false, glyphLog: null);
                    try
                    {
                        fxDef.Draw(fxEnv, ev);
                    }
                    catch (Exception e)
                    {
                        WarnThrottled("fx", ev.Type, e);
                    }
                    finally
                    {
                        P11FxB.FxEnd();
                    }
                    // JS 在插件 D.draw 后复位 ctx 状态；C# 的 paint 都作用域即弃，画布矩阵由
                    // fxEnv/包装的 Save/Restore 恢复
                    guard?.End(ctx);
                    continue;
                }
                switch (ev.Type)
                {
                    case "slice":
                    {
                        CopyScratch(ctx, S!);
                        using var simg = S!.Snapshot();
                        using var dp = new SKPaint();
                        var n = 6 + (int)(J.H(st2, 3) % 7);
                        double y = 0;
                        for (var i = 0; i < n && y < ch; i++)
                        {
                            var h = Math.Max(2, ch * J.Rr(0.01, 0.12, st2, i, 1));
                            var dx = J.R(st2, i, 2) < 0.55 ? J.Rs(st2, i, 3) * cw * 0.06 * ev.Amp : 0;
                            if (dx != 0)
                            {
                                var src = new SKRect(0, (float)y, cw, (float)(y + h));
                                var dst = new SKRect((float)dx, (float)y, (float)(dx + cw), (float)(y + h));
                                if (guard != null) guard.ContentDraw(ctx, simg, src, dst, dp);
                                else ctx.DrawImage(simg, src, dst, dp);
                            }
                            y += h + ch * J.Rr(0, 0.08, st2, i, 4);
                        }
                        break;
                    }
                    case "block":
                    {
                        CopyScratch(ctx, S!);
                        using var simg = S!.Snapshot();
                        using var dp = new SKPaint();
                        for (var i = 0; i < 9; i++)
                        {
                            var w = cw * J.Rr(0.05, 0.3, st2, i, 5);
                            var h = ch * J.Rr(0.01, 0.07, st2, i, 6);
                            var x = J.R(st2, i, 7) * (cw - w);
                            var y = J.R(st2, i, 8) * (ch - h);
                            var sx2 = J.Clamp(x + J.Rs(st2, i, 9) * cw * 0.08, 0, cw - w);
                            var sy2 = J.Clamp(y + J.Rs(st2, i, 10) * ch * 0.04, 0, ch - h);
                            var src = new SKRect((float)sx2, (float)sy2, (float)(sx2 + w), (float)(sy2 + h));
                            var dst = new SKRect((float)x, (float)y, (float)(x + w), (float)(y + h));
                            if (guard != null) guard.ContentDraw(ctx, simg, src, dst, dp);
                            else ctx.DrawImage(simg, src, dst, dp);
                            if (J.R(st2, i, 11) < 0.35)
                            {
                                using var bp = new SKPaint
                                {
                                    BlendMode = SKBlendMode.Difference,
                                    Color = Env.SkColor(
                                        J.R(st2, i, 12) < 0.5 ? sc.Str("ghostA") ?? "#ffffff" : sc.Str("ghostB") ?? "#ffffff"),
                                };
                                ctx.DrawRect(dst, bp);
                            }
                        }
                        break;
                    }
                    case "invert":
                    {
                        using var ip = new SKPaint { BlendMode = SKBlendMode.Difference, Color = SKColors.White };
                        ctx.DrawRect(new SKRect(0, 0, cw, ch), ip);
                        break;
                    }
                    case "flash":
                    {
                        var col = J.Lum(sc.Str("bg") ?? "#000000") < 0.5 ? sc.Str("fg") ?? "#ffffff" : "#ffffff";
                        RectFill(ctx, 0, 0, cw, ch, Env.SkColor(col, Math.Pow(1 - k, 1.5) * 0.92));
                        break;
                    }
                    case "zoom":
                    {
                        CopyScratch(ctx, S!);
                        using var simg = S!.Snapshot();
                        var a = ev.Amp * (1 - k);
                        for (var i = 1; i <= 6; i++)
                        {
                            var s2 = 1 + i * 0.022 * a;
                            using var zp = new SKPaint
                            {
                                Color = SKColors.White.WithAlpha((byte)Math.Round(0.2 * (1 - i / 7.0) * Math.Min(1, a * 1.3) * 255)),
                            };
                            var dst = new SKRect((float)(cw / 2.0 - cw * s2 / 2), (float)(ch / 2.0 - ch * s2 / 2),
                                (float)(cw / 2.0 + cw * s2 / 2), (float)(ch / 2.0 + ch * s2 / 2));
                            if (guard != null) guard.ContentDraw(ctx, simg, dst, zp);
                            else ctx.DrawImage(simg, dst, zp);
                        }
                        break;
                    }
                    case "mosaic":
                    {
                        CopyScratch(ctx, S!);
                        using var simg = S!.Snapshot();
                        var tw = Math.Max(8, (int)Math.Round(cw / 42.0));
                        var th = Math.Max(8, (int)Math.Round(ch / 42.0));
                        var T = Ensure(ref _tiny, tw, th);
                        var tx = T.Canvas;
                        tx.SetMatrix(SKMatrix.Identity);
                        tx.Clear();
                        using (var mp = new SKPaint()) // 缩小：线性采样
                            tx.DrawImage(simg, new SKRect(0, 0, tw, th), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), mp);
                        using (var timg = T.Snapshot())
                        using (var up = new SKPaint
                               {
                                   Color = SKColors.White.WithAlpha((byte)Math.Round(0.85 * (1 - k) * 255)),
                               })
                        {
                            var dst = new SKRect(0, 0, cw, ch);
                            // 放大：最近邻（JS imageSmoothingEnabled=false 的马赛克硬边）
                            if (guard != null) guard.ContentDraw(ctx, timg, dst, up);
                            else ctx.DrawImage(timg, dst, default(SKSamplingOptions), up);
                        }
                        break;
                    }
                }
                // JS 每个特效后复位 ctx 状态；C# 的 paint 都是作用域内即弃，无残留
                guard?.End(ctx);
            }
            Stage("fx:events");
            if (!opt.Fast && allowFilter && glow > 0.05 && !opt.Transparent)
            {
                var sw2 = Math.Max(1, (int)Math.Round(cw / 4.0));
                var sh2 = Math.Max(1, (int)Math.Round(ch / 4.0));
                var Sm = Ensure(ref _small, sw2, sh2);
                var sx = Sm.Canvas;
                sx.SetMatrix(SKMatrix.Identity);
                sx.Clear();
                // 辉光模糊只依赖小画布宽（同尺寸恒同 σ）：filter 实例缓存复用
                using (var snap = SnapshotOf(ctx))
                using (var bp = new SKPaint { ImageFilter = GlowFilter(sw2) })
                    sx.DrawImage(snap, new SKRect(0, 0, sw2, sh2),
                        new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), bp);
                Stage("fx:glowdown");
                using (var simg = Sm.Snapshot())
                using (var sp = new SKPaint { BlendMode = SKBlendMode.Screen,
                       Color = SKColors.White.WithAlpha((byte)Math.Round(glow * 0.55 * 255)) })
                    ctx.DrawImage(simg, new SKRect(0, 0, cw, ch),
                        new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), sp);
            }
            Stage("fx:glow");
            if (!opt.Transparent && plan.KeyBg is not ("green" or "black"))
            {
                var scan = StTextureNum(st, "scan") * fx.Texture;
                if (scan > 0.03)
                {
                    var k = Math.Max(1, (int)Math.Round(ch / 540.0));
                    ctx.Save();
                    ctx.Scale(k, k);
                    RectFill(ctx, 0, 0, cw / (double)k, ch / (double)k,
                        SKColors.White.WithAlpha((byte)Math.Round(scan * 0.28 * 255)), SKBlendMode.Multiply, shader: ScanShader());
                    ctx.Restore();
                }
                Stage("fx:scan");
                var gr = StTextureNum(st, "grain") * fx.Texture;
                if (gr > 0.02)
                {
                    var gi = ((step % 4) + 4) % 4;
                    var img = _grain[gi]!;
                    var k = Math.Max(1, ch / 1080.0);
                    var ox = J.R(step, 1) * 256;
                    var oy = J.R(step, 2) * 256;
                    var shader = GrainShader(gi);
                    ctx.Save();
                    ctx.Scale((float)k, (float)k);
                    ctx.Translate((float)-ox, (float)-oy);
                    // 注：曾试把覆盖矩形钳到「正好画布」以求省面积——实测无收益（Skia 绘制先按设备
                    // 边界裁剪，多铺的 256px 边距本就不花着色成本），故保留原式（像素与行为都不动）。
                    RectFill(ctx, 0, 0, cw / k + 256, ch / k + 256,
                        SKColors.White.WithAlpha((byte)Math.Round(gr * 0.2 * 255)), SKBlendMode.Overlay, shader: shader);
                    RectFill(ctx, 0, 0, cw / k + 256, ch / k + 256,
                        SKColors.White.WithAlpha((byte)Math.Round(gr * 0.035 * 255)), SKBlendMode.SrcOver, shader: shader);
                    ctx.Restore();
                }
                Stage("fx:grain");
                // 注：暗角渐变叠加在不定内容上（不可连底烘焙），premul 中转又有 ±1 抖动 → 维持 shader 直绘。
                using (var vg = VigGrad(cw, ch, 0.28 * fx.Texture))
                    RectFill(ctx, 0, 0, cw, ch, SKColors.White, shader: vg);
                Stage("fx:vig");
            }
            ctx.Restore();
        }

        // ---- 后期纹理的只读资源缓存（颗粒/扫描线 shader、辉光 blur；Dispose 统一收） ----
        // 注：常量全幅渐变（bgGrad/vigGrad）预渲染缓存试验后回退——premul 中转引入 ±1 级
        // 全帧像素抖动（97% 像素非位级一致），违反「逐像素不变」铁律，见 Frame/Post 内注释。

        private readonly SKShader?[] _grainShader = new SKShader?[4];
        private SKShader? _scanShader;
        private readonly Dictionary<(int, int, int), SKImageFilter> _glowFilters = new();

        private SKShader GrainShader(int gi) =>
            _grainShader[gi] ??= SKShader.CreateBitmap(_grain[gi]!, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat);

        private SKShader ScanShader() =>
            _scanShader ??= SKShader.CreateBitmap(_scan, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat);

        private SKImageFilter GlowFilter(int smallW)
        {
            var sigma = Math.Max(2, Math.Round(smallW / 160.0));
            var key = (smallW, (int)sigma, 0);
            if (!_glowFilters.TryGetValue(key, out var f))
            {
                f = JText.BlurFilter(sigma)!;
                _glowFilters[key] = f;
            }
            return f;
        }

        private void CopyScratch(SKCanvas ctx, SKSurface s)
        {
            var sx = s.Canvas;
            sx.SetMatrix(SKMatrix.Identity);
            sx.Clear();
            using (var img = SnapshotOf(ctx))
            using (var p = new SKPaint())
                sx.DrawImage(img, 0, 0, p);
        }

        // ---------------- makeEnv ----------------

        private static Env MakeEnv(SKCanvas ctx, Plan plan, Cut? cut, Scheme? sc,
            double t, double lt, double ltb, int stepI, double scale, bool allowFilter,
            double energy, BeatInfo? beatInfo, string pass, string? passColor,
            string? layer, Zone? zone, bool hideText, List<JText.GlyphLogEntry>? glyphLog)
        {
            var env = Env.Make(ctx, plan, cut, sc, lt, stepI, energy, BeatNorm(beatInfo), zone, pass, passColor, scale, t, ltb);
            if (beatInfo is { } bi)
            {
                env.BeatHas = true;
                env.BeatSince = bi.Since;
                env.BeatLen = bi.Len;
                env.BeatIndex = bi.Index;
            }
            env.Layer = layer;
            env.HideText = hideText;
            env.AllowFilter = allowFilter;
            env.GlyphLog = glyphLog;
            return env;
        }

        // ---------------- 纸纹 / 渐变 / 离屏画布帮手 ----------------

        /// <summary>paper(W, H)：半分辨率纸纹（低频底噪 + 逐像素噪声 + 900 条纤维 + 1400 颗粒），带缓存。
        /// inverted=true：暗底用（JS 的 ctx.filter='invert(1)' 预先内建）。</summary>
        private readonly Dictionary<string, SKImage> _paperImgCache = new();

        /// <summary>纸纹的 SKImage 视图（FromBitmap 共享位图像素）：缓存复用，调用方禁 Dispose。</summary>
        private SKImage PaperImage(int W, int H, bool inverted)
        {
            var bmp = Paper(W, H, inverted);
            var key = (inverted ? "i" : "n") + W + "x" + H;
            if (!_paperImgCache.TryGetValue(key, out var img))
            {
                img = SKImage.FromBitmap(bmp);
                _paperImgCache[key] = img;
            }
            return img;
        }

        // ---- 底色+中央提亮渐变的预渲染缓存（性能专项）----
        // bg 填充与 BgGrad 渐变的合成结果只依赖 (bgCol, 渐变α, 设计尺寸, scale)；scheme 轮换/明暗底
        // 切换会换键，字典按全键缓存、超量整清。缓存面按与画布相同的矩阵/着色器做同两步合成
        // （透明底 ← SrcOver 不透明底色 ← SrcOver 渐变），结果不透明 → premul 存储无损、贴回即原像素。
        // （注：单把「半透明渐变」单独烘焙成图会在 premul 中转里引入 ±1 抖动，不可行；必须连底色一起烘焙。）

        private readonly Dictionary<(int CW, int CH, int W, int H, double Scale, int Bg, int LiftA8), SKImage> _bgBaked = new();

        private SKImage BgBakedImage(string bgCol, int cw, int ch, int W, int H, double scale, double liftA)
        {
            var (r, g, b) = J.Hex(bgCol);
            var liftA8 = (int)Math.Round(liftA * 255);
            var key = (cw, ch, W, H, scale, r << 16 | g << 8 | b, liftA8);
            if (_bgBaked.TryGetValue(key, out var hit)) return hit;
            using var surf = SKSurface.Create(new SKImageInfo(cw, ch));
            var c = surf.Canvas;
            c.SetMatrix(SKMatrix.Identity);
            c.Clear(SKColors.Transparent);
            c.SetMatrix(SKMatrix.CreateScale((float)scale, (float)scale));
            using (var bp = new SKPaint { Color = new SKColor((byte)r, (byte)g, (byte)b) })
                c.DrawRect(new SKRect(0, 0, W, H), bp);
            using (var gp = new SKPaint { Color = SKColors.White, Shader = BgGrad(W, H, new SKColor(255, 255, 255, (byte)liftA8)) })
                c.DrawRect(new SKRect(0, 0, W, H), gp);
            var img = surf.Snapshot();
            if (_bgBaked.Count > 8)
            {
                foreach (var old in _bgBaked.Values) old.Dispose();
                _bgBaked.Clear();
            }
            _bgBaked[key] = img;
            return img;
        }

        /// <summary>预渲染整幅图的 1:1 贴回（identity 矩阵 + 最近邻采样，逐像素等于原直绘结果）。</summary>
        private void DrawGradImage(SKCanvas ctx, SKImage img)
        {
            ctx.Save();
            ctx.SetMatrix(SKMatrix.Identity);
            var p = Env.SharedPaint();
            p.IsAntialias = false;
            p.IsStroke = false;
            p.Shader = null;
            p.ImageFilter = null;
            p.Color = SKColors.White;
            p.BlendMode = SKBlendMode.SrcOver;
            ctx.DrawImage(img, 0, 0, default(SKSamplingOptions), p);
            ctx.Restore();
        }

        private SKBitmap Paper(int W, int H, bool inverted)
        {
            var key = (inverted ? "i" : "n") + W + "x" + H;
            if (_paperCache.TryGetValue(key, out var p)) return p;
            var w = Math.Max(1, (int)Math.Round(W / 2.0));
            var h = Math.Max(1, (int)Math.Round(H / 2.0));
            var bmp = new SKBitmap(w, h);
            using (var x = new SKCanvas(bmp))
            {
                x.Clear(SKColors.White);
                // 低频纸底噪（225..255 暖灰），放大铺满
                var loW = Math.Max(1, (int)Math.Ceiling(w / 24.0));
                var loH = Math.Max(1, (int)Math.Ceiling(h / 24.0));
                using (var lo = new SKBitmap(loW, loH))
                {
                    FillNoise(lo,
                        v => (byte)ClampByte(v),
                        v => (byte)ClampByte(v - 2),
                        v => (byte)ClampByte(v - 6),
                        _ => 255,
                        () => 225 + _rng.NextDouble() * 30);
                    using (var lp = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(0.9 * 255)) })
                        x.DrawBitmap(lo, new SKRect(0, 0, loW, loH), new SKRect(0, 0, w, h), lp);
                }
                // 逐像素噪声
                AddPerPxNoise(bmp, 22);
                // 900 条纤维（二次贝塞尔短弧）
                using (var sp = new SKPaint
                       {
                           IsAntialias = true,
                           IsStroke = true,
                           StrokeWidth = 0.7f,
                           Color = new SKColor(120, 110, 100, (byte)Math.Round(0.18 * 255)),
                       })
                {
                    for (var i = 0; i < 900; i++)
                    {
                        var X = _rng.NextDouble() * w;
                        var Y = _rng.NextDouble() * h;
                        var a = _rng.NextDouble() * J.TAU;
                        var L = 4 + _rng.NextDouble() * 14;
                        using var path = new SKPath();
                        path.MoveTo((float)X, (float)Y);
                        path.QuadTo((float)(X + Math.Cos(a + 0.5) * L / 2), (float)(Y + Math.Sin(a + 0.5) * L / 2),
                            (float)(X + Math.Cos(a) * L), (float)(Y + Math.Sin(a) * L));
                        x.DrawPath(path, sp);
                    }
                }
                // 1400 颗粒
                using (var fp = new SKPaint { Color = new SKColor(60, 50, 40, (byte)Math.Round(0.25 * 255)) })
                {
                    for (var i = 0; i < 1400; i++)
                        x.DrawRect((float)(_rng.NextDouble() * w), (float)(_rng.NextDouble() * h),
                            (float)(_rng.NextDouble() * 1.6), (float)(_rng.NextDouble() * 1.6), fp);
                }
            }
            if (inverted) InvertPixels(bmp);
            _paperCache[key] = bmp;
            return bmp;
        }

        private static double ClampByte(double v) => v < 0 ? 0 : v > 255 ? 255 : v;

        private static void FillNoise(SKBitmap bmp, Func<double, byte> r, Func<double, byte> g, Func<double, byte> b,
            Func<double, byte> a, Func<double> src)
        {
            var px = bmp.PeekPixels();
            var ptr = px.GetPixels();
            var len = px.Info.BytesSize;
            var buf = new byte[len];
            Marshal.Copy(ptr, buf, 0, len);
            var order = px.ColorType == SKColorType.Bgra8888;
            for (var i = 0; i + 3 < len; i += 4)
            {
                var v = src();
                if (order) // BGRA
                {
                    buf[i] = b(v);
                    buf[i + 1] = g(v);
                    buf[i + 2] = r(v);
                    buf[i + 3] = a(v);
                }
                else // RGBA
                {
                    buf[i] = r(v);
                    buf[i + 1] = g(v);
                    buf[i + 2] = b(v);
                    buf[i + 3] = a(v);
                }
            }
            Marshal.Copy(buf, 0, ptr, len);
        }

        private static void AddPerPxNoise(SKBitmap bmp, double amp)
        {
            var px = bmp.PeekPixels();
            var ptr = px.GetPixels();
            var len = px.Info.BytesSize;
            var buf = new byte[len];
            Marshal.Copy(ptr, buf, 0, len);
            var rnd = new Random(0x504E303);
            for (var i = 0; i + 3 < len; i += 4)
            {
                var n = (rnd.NextDouble() - 0.5) * amp;
                buf[i] = (byte)ClampByte(buf[i] + n);
                buf[i + 1] = (byte)ClampByte(buf[i + 1] + n);
                buf[i + 2] = (byte)ClampByte(buf[i + 2] + n);
            }
            Marshal.Copy(buf, 0, ptr, len);
        }

        private static void InvertPixels(SKBitmap bmp)
        {
            var px = bmp.PeekPixels();
            var ptr = px.GetPixels();
            var len = px.Info.BytesSize;
            var buf = new byte[len];
            Marshal.Copy(ptr, buf, 0, len);
            for (var i = 0; i + 3 < len; i += 4)
            {
                buf[i] = (byte)(255 - buf[i]);
                buf[i + 1] = (byte)(255 - buf[i + 1]);
                buf[i + 2] = (byte)(255 - buf[i + 2]);
            }
            Marshal.Copy(buf, 0, ptr, len);
        }

        /// <summary>bgGrad：中央提亮的径向渐变（JS createRadialGradient → Skia 两点圆锥）。</summary>
        private static SKShader BgGrad(int W, int H, SKColor lift) =>
            SKShader.CreateTwoPointConicalGradient(
                new SKPoint(W / 2f, H * 0.45f), 0,
                new SKPoint(W / 2f, H / 2f), (float)(Math.Sqrt((double)W * W + (double)H * H) * 0.6),
                new[] { lift, SKColors.Transparent }, null, SKShaderTileMode.Clamp);

        /// <summary>vigGrad：四角压暗。</summary>
        private static SKShader VigGrad(int cw, int ch, double a) =>
            SKShader.CreateTwoPointConicalGradient(
                new SKPoint(cw / 2f, ch / 2f), (float)(Math.Min(cw, ch) * 0.35),
                new SKPoint(cw / 2f, ch / 2f), (float)(Math.Sqrt((double)cw * cw + (double)ch * ch) * 0.62),
                new[] { SKColors.Transparent, new SKColor(0, 0, 0).WithAlpha((byte)Math.Round(a * 255)) },
                null, SKShaderTileMode.Clamp);

        private SKSurface Ensure(ref SKSurface? s, int w, int h)
        {
            w = Math.Max(1, w);
            h = Math.Max(1, h);
            if (s == null || s.Canvas.DeviceClipBounds.Width != w || s.Canvas.DeviceClipBounds.Height != h)
            {
                s?.Dispose();
                s = SurfaceFactory != null ? SurfaceFactory(w, h) : SKSurface.Create(new SKImageInfo(w, h));
            }
            return s;
        }

        private static SKImage SnapshotOf(SKCanvas ctx)
        {
            // SkiaSharp 3.x 的 SKCanvas 没有 ReadPixels：整帧缓存/特效 scratch 依赖 snapshot，
            // 主画布必须是 SKSurface 的 canvas（导出/冒烟管线均满足；SKCanvas(bitmap) 场景由上层包 surface）
            var s = ctx.Surface ?? throw new NotSupportedException(
                "Renderer 需要 SKSurface 的 canvas（可 Snapshot）；请用 SKSurface 包裹目标位图后再画");
            return s.Snapshot();
        }

        private static void RectFill(SKCanvas ctx, double x, double y, double w, double h, SKColor color,
            SKBlendMode blend = SKBlendMode.SrcOver, SKShader? shader = null)
        {
            var p = Env.SharedPaint();
            p.IsAntialias = false;
            p.IsStroke = false;
            p.ImageFilter = null;
            p.Color = color;
            p.BlendMode = blend;
            p.Shader = shader;
            ctx.DrawRect(new SKRect((float)x, (float)y, (float)(x + w), (float)(y + h)), p);
            p.Shader = null;
        }

        private static string FirstKey<TV>(Dictionary<string, TV> map)
        {
            foreach (var k in map.Keys) return k;
            throw new InvalidOperationException("empty map");
        }

        // ---------------- 松散 JSON 读取（item/params 的 duck-typed 对位） ----------------

        private static double NumTok(JObject? o, string k, double d = 0)
        {
            if (o == null) return d;
            var v = o[k];
            return v is JValue vv && vv.Type is JTokenType.Float or JTokenType.Integer
                ? Convert.ToDouble(vv.Value, CultureInfo.InvariantCulture)
                : d;
        }

        private static bool Truthy(JToken? t) => JLayouts.Truthy(t);

        private static double StNum(StyleDef st, string k, double d)
        {
            var v = JLayouts.StTok(st, k);
            return v is JValue vv && vv.Type is JTokenType.Float or JTokenType.Integer
                ? Convert.ToDouble(vv.Value, CultureInfo.InvariantCulture)
                : d;
        }

        private static double StTextureNum(StyleDef st, string k) =>
            JLayouts.StTok(st, "texture") is JObject t ? NumTok(t, k) : 0;

        // ---------------- beat（JS beatAt / prevBeat） ----------------

        private readonly record struct BeatInfo(double Since, double Len, int Index);

        private static BeatInfo? BeatAt(double[] beats, double t)
        {
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
            if (i < 0) return null;
            var len = i + 1 < beats.Length ? beats[i + 1] - beats[i] : i > 0 ? beats[i] - beats[i - 1] : 0.5;
            return new BeatInfo(t - beats[i], Math.Max(0.2, len), i);
        }

        private static double? PrevBeat(double[] beats, double t)
        {
            int lo = 0, hi = beats.Length - 1;
            double? ans = null;
            while (lo <= hi)
            {
                var m = (lo + hi) >> 1;
                if (beats[m] <= t)
                {
                    ans = beats[m];
                    lo = m + 1;
                }
                else hi = m - 1;
            }
            return ans;
        }

        /// <summary>beatInfo → Env.Beat（归一相位 since/len）；完整 {since,len,index} 由 MakeEnv 挂 Env.BeatHas 等字段。</summary>
        private static double BeatNorm(BeatInfo? b) => b == null ? 0 : b.Value.Since / b.Value.Len;

        public void Dispose()
        {
            _scratch?.Dispose();
            _small?.Dispose();
            _tiny?.Dispose();
            _camLayer?.Dispose();
            _transA?.Dispose();
            _transB?.Dispose();
            _transC?.Dispose();
            _morphCv?.Dispose();
            _morphShotCv?.Dispose();
            _guardP?.Dispose();
            _guardM?.Dispose();
            foreach (var g in _grain) g?.Dispose();
            foreach (var s in _grainShader) s?.Dispose();
            _scanShader?.Dispose();
            _glowFilters.Clear(); // 值来自 JText 共享记忆工厂，不随 Renderer 释放（性能专项）
            foreach (var img in _bgBaked.Values) img.Dispose();
            _bgBaked.Clear();
            foreach (var img in _paperImgCache.Values) img.Dispose();
            _paperImgCache.Clear();
            _scan.Dispose();
            foreach (var p in _paperCache.Values) p.Dispose();
            _paperCache.Clear();
            _fcCv?.Dispose();
            if (_msMap != null)
                foreach (var img in _msMap.Values)
                    img.Dispose();
        }
}
