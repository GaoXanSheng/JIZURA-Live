using System.Globalization;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace JizuraLive.Engine.Packs;

// 移植自 frontend/src/engine/11p_bgcamB.js：bgcamB 手法包——bg 背景 37 条（渐变、和柄/织物纹样、
// 场景、质感）+ cam 运镜 12 条，pack = "bgcamB"。
//
// Canvas2D → SkiaSharp 直译（README 约定 1）：
//   createPattern(repeat)        → SKShader.CreateBitmap(tile, Repeat, Repeat, T·S localMatrix)
//   离屏 canvas 缓存（CV/PL/LOWC）→ SKSurface.Snapshot 的 SKImage LRU（容量/预算同源）
//   createConicGradient          → SKShader.CreateSweepGradient（JS 的无 conic 回退分支不再需要）
//   globalAlpha                  → paint.Color alpha / DrawImage paint alpha
//   globalCompositeOperation     → SKBlendMode（fireworks 的 screen、青海波瓦片的 destination-out）
//   Path2D / fill('evenodd')     → SKPath（FillType=EvenOdd）
//   putImageData 斑点噪声        → SKBitmap 逐像素写入 + SKImage.FromPixels 借用（Owner 保活）
//
// 跨文件符号对位：J.rgba→本地 Rgba、J.smooth→J.Smooth、J.mix/lum/contrast/noise1/r/rs/rr/h→J.*、
// J.E.*→J.E.*；clamp/lerp→J.Clamp/J.Lerp、Math.hypot→本地 Hyp。
// env 对位缺口：env.beat 对象（since/len）C# Env 未携带 → BeatSince 走无 beat 的 ltb 取模回退
// （env.beat 已由渲染器挂 Env.BeatHas/BeatSince/BeatLen/BeatIndex，BeatSince 按真节拍走）；
// cam 返回的 sx/sy（vertigo/jelly）在
// CamState 无对应字段 → 暂不入状态（TODO(Registry)：CamState 增补 Sx/Sy 后回迁）。
public static class P11BgcamB
{
    private const string PKG = "bgcamB";
    private static int _installed;

    private static readonly SKSamplingOptions SmpLinear = new(SKFilterMode.Linear, SKMipmapMode.None);
    private static readonly SKSamplingOptions SmpNearest = new(SKFilterMode.Nearest);

    /// <summary>11p_bgcamB.js 的 IIFE 载入即注册；显式公开供装配按文件序调用。幂等。</summary>
    public static void Install()
    {
        if (_installed != 0) return;
        _installed = 1;

        // ================= bg 背景（源注册顺序即候选顺序，不能变） =================

        BgReg("auroraRibbons", "オーロラ", new[] { "emotional", "calm" }, 0.9, false,
            rng => Prm(
                ("seed", Bs(rng)), ("n", rng.Int(2, 3)), ("y", rng.Range(0.36, 0.48)),
                ("k", rng.Range(0.2, 0.28)), ("c0", rng.Int(0, 3))),
            DrawAuroraRibbons);

        BgReg("meshBlobs", "メッシュグラデ", new[] { "calm", "emotional", "pop" }, 1, true,
            rng => Prm(("seed", Bs(rng)), ("n", rng.Int(3, 4)), ("k", rng.Range(0.2, 0.3))),
            DrawMeshBlobs);

        BgReg("duotoneSweep", "二色スイープ", new[] { "calm", "pop", "graphic", "emotional" }, 0.9, true,
            rng => Prm(
                ("seed", Bs(rng)),
                ("pos", rng.Pick(new[] { new[] { 0.5, 1.2 }, new[] { -0.15, 1.1 }, new[] { 1.15, 1.1 }, new[] { 0.5, -0.2 }, new[] { -0.1, -0.1 } })),
                ("spd", rng.Range(0.08, 0.14) * rng.Pick(new[] { 1.0, -1.0 })),
                ("k", rng.Range(0.14, 0.2)),
                ("a0", rng.Range(0, 6.28))),
            DrawDuotoneSweep);

        BgReg("horizonGlow", "惑星の縁", new[] { "emotional", "calm", "editorial" }, 0.8, false,
            rng => Prm(
                ("seed", Bs(rng)), ("cx", rng.Range(0.3, 0.7)), ("R", rng.Range(1.3, 2.1)),
                ("top", rng.Range(0.7, 0.8)), ("k", rng.Range(0.28, 0.4))),
            DrawHorizonGlow);

        BgReg("seigaiha", "青海波", new[] { "calm", "editorial", "graphic" }, 0.8, false,
            rng => Prm(
                ("seed", Bs(rng)), ("R", rng.Range(0.07, 0.1)), ("rings", rng.Int(3, 4)),
                ("k", rng.Range(0.075, 0.1)), ("dir", rng.Pick(new[] { 1.0, -1.0 })), ("acc", rng.Chance(0.3))),
            DrawSeigaiha);

        BgReg("asanoha", "麻の葉", new[] { "calm", "editorial", "graphic", "emotional" }, 0.8, false,
            rng => Prm(
                ("seed", Bs(rng)), ("a", rng.Range(0.1, 0.14)), ("k", rng.Range(0.09, 0.12)),
                ("dx", rng.Pick(new[] { 1.0, -1.0 })), ("sweep", rng.Range(0.07, 0.12))),
            DrawAsanoha);

        BgReg("houndstooth", "千鳥格子", new[] { "graphic", "editorial", "pop" }, 0.6, false,
            rng => Prm(
                ("seed", Bs(rng)), ("c", rng.Range(0.04, 0.055)), ("k", rng.Range(0.055, 0.075)),
                ("dx", rng.Pick(new[] { 1.0, -1.0 })), ("acc", rng.Chance(0.25))),
            DrawHoundstooth);

        BgReg("herringbone", "ヘリンボーン", new[] { "editorial", "calm", "graphic" }, 0.6, false,
            rng => Prm(
                ("seed", Bs(rng)), ("u", rng.Range(0.032, 0.045)), ("k", rng.Range(0.07, 0.1)),
                ("dir", rng.Pick(new[] { 1.0, -1.0 })), ("rot", rng.Pick(new object[] { 45.0, 45.0, -45.0 }))),
            DrawHerringbone);

        BgReg("argyle", "アーガイル", new[] { "pop", "graphic", "editorial" }, 0.6, false,
            rng => Prm(
                ("seed", Bs(rng)), ("dw", rng.Range(0.16, 0.22)), ("asp", rng.Range(1.3, 1.5)),
                ("k", rng.Range(0.06, 0.085)), ("up", rng.Pick(new[] { 1.0, -1.0 }))),
            DrawArgyle);

        BgReg("tartan", "タータン", new[] { "pop", "calm", "editorial" }, 0.6, false,
            rng => Prm(
                ("seed", Bs(rng)), ("S", rng.Range(0.34, 0.5)), ("w1", rng.Range(0.18, 0.28)),
                ("w2", rng.Range(0.08, 0.13)), ("k", rng.Range(0.085, 0.115)), ("acc", rng.Chance(0.6))),
            DrawTartan);

        BgReg("chevron", "山形", new[] { "pop", "graphic" }, 0.6, false,
            rng => Prm(
                ("seed", Bs(rng)), ("p", rng.Range(0.13, 0.19)), ("amp", rng.Range(0.28, 0.42)),
                ("k", rng.Range(0.055, 0.075)), ("dir", rng.Pick(new[] { 1.0, -1.0 })), ("acc", rng.Chance(0.3))),
            DrawChevron);

        BgReg("isoCubes", "立方体", new[] { "graphic", "pop", "calm" }, 0.6, false,
            rng => Prm(
                ("seed", Bs(rng)), ("s", rng.Range(0.055, 0.075)), ("k", rng.Range(0.08, 0.11)),
                ("spd", rng.Range(0.2, 0.35) * rng.Pick(new[] { 1.0, -1.0 })), ("l0", rng.Range(0, 6.28))),
            DrawIsoCubes);

        BgReg("hexGrid", "六角格子", new[] { "graphic", "glitch", "calm" }, 0.8, false,
            rng => Prm(
                ("seed", Bs(rng)), ("s", rng.Range(0.05, 0.07)), ("k", rng.Range(0.09, 0.12)),
                ("mode", rng.Pick(new[] { "ring", "ring", "sparkle" }))),
            DrawHexGrid);

        BgReg("triTess", "三角モザイク", new[] { "graphic", "calm", "emotional" }, 0.7, false,
            rng => Prm(
                ("seed", Bs(rng)), ("a", rng.Range(0.1, 0.15)), ("k", rng.Range(0.07, 0.1)), ("acc", rng.Chance(0.35))),
            DrawTriTess);

        BgReg("moire", "モアレ", new[] { "glitch", "graphic", "calm" }, 0.6, false,
            rng => Prm(
                ("seed", Bs(rng)), ("gap", rng.Range(0.016, 0.022)), ("k", rng.Range(0.08, 0.11)),
                ("amp", rng.Range(0.05, 0.09))),
            DrawMoire);

        BgReg("squareTunnel", "四角トンネル", new[] { "glitch", "graphic", "pop" }, 0.6, false,
            rng => Prm(
                ("seed", Bs(rng)), ("r", rng.Range(1.22, 1.32)),
                ("twist", rng.Range(3, 7) * rng.Pick(new[] { 1.0, -1.0 })),
                ("spd", rng.Range(0.35, 0.6)), ("k", rng.Range(0.05, 0.07))),
            DrawSquareTunnel);

        BgReg("spiralArms", "渦巻き", new[] { "glitch", "pop", "graphic" }, 0.6, false,
            rng => Prm(
                ("seed", Bs(rng)), ("n", rng.Int(3, 6)), ("b", rng.Range(0.26, 0.38)),
                ("spd", rng.Range(0.14, 0.24) * rng.Pick(new[] { 1.0, -1.0 })),
                ("k", rng.Range(0.055, 0.075)), ("cy", rng.Pick(new[] { 0.5, 0.5, 0.56 }))),
            DrawSpiralArms);

        BgReg("topoLines", "等高線", new[] { "calm", "editorial", "graphic" }, 0.8, false,
            rng => Prm(
                ("seed", Bs(rng)), ("n", rng.Int(10, 14)), ("fs", rng.Range(1.2, 1.7)),
                ("k", rng.Range(0.11, 0.15)), ("acc", rng.Chance(0.3))),
            DrawTopoLines);

        BgReg("ridgePlot", "稜線グラフ", new[] { "editorial", "emotional", "calm" }, 0.7, false,
            rng => Prm(
                ("seed", Bs(rng)), ("n", rng.Int(18, 26)), ("k", rng.Range(0.15, 0.2)),
                ("amp", rng.Range(0.07, 0.1)), ("mode", rng.Pick(new[] { "center", "center", "wide" })),
                ("spd", rng.Range(0.18, 0.3))),
            DrawRidgePlot);

        BgReg("starfield", "星空", new[] { "emotional", "calm" }, 0.9, false,
            rng => Prm(
                ("seed", Bs(rng)), ("ang", rng.Range(-0.3, 0.3) + (rng.Chance(0.5) ? Math.PI : 0)),
                ("spd", rng.Range(0.8, 1.3)), ("shoot", rng.Chance(0.75))),
            DrawStarfield);

        BgReg("nightMoon", "月夜", new[] { "emotional", "calm", "editorial" }, 0.8, false,
            rng => Prm(
                ("seed", Bs(rng)), ("side", rng.Pick(new[] { 1.0, -1.0 })), ("R", rng.Range(0.11, 0.15)),
                ("phase", rng.Pick(new[] { "full", "crescent", "crescent" })), ("k", rng.Range(0.18, 0.26))),
            DrawNightMoon);

        BgReg("skyline", "街並み", new[] { "emotional", "editorial", "pop" }, 0.8, false,
            rng => Prm(
                ("seed", Bs(rng)), ("k", rng.Range(0.08, 0.11)), ("dir", rng.Pick(new[] { 1.0, -1.0 })),
                ("win", rng.Range(0.22, 0.34))),
            DrawSkyline);

        BgReg("sunsetSun", "夕日", new[] { "emotional", "calm", "pop" }, 0.8, false,
            rng => Prm(
                ("seed", Bs(rng)), ("hz", rng.Range(0.7, 0.76)), ("R", rng.Range(0.12, 0.16)),
                ("cx", rng.Pick(new object[] { rng.Range(0.22, 0.34), rng.Range(0.66, 0.78) })),
                ("k", rng.Range(0.28, 0.36))),
            DrawSunsetSun);

        BgReg("oceanWaves", "海の波", new[] { "calm", "emotional", "editorial" }, 0.8, false,
            rng => Prm(
                ("seed", Bs(rng)), ("n", rng.Int(9, 13)), ("hz", rng.Range(0.48, 0.58)),
                ("k", rng.Range(0.12, 0.17)), ("dir", rng.Pick(new[] { 1.0, -1.0 }))),
            DrawOceanWaves);

        BgReg("rainWindow", "雨の窓", new[] { "emotional", "calm", "editorial" }, 0.8, false,
            rng => Prm(
                ("seed", Bs(rng)), ("ang", rng.Range(4, 13) * rng.Pick(new[] { 1.0, -1.0 })),
                ("n", rng.Int(100, 140)), ("k", rng.Range(0.15, 0.2)), ("drops", rng.Int(16, 24))),
            DrawRainWindow);

        BgReg("snowLayers", "雪", new[] { "calm", "emotional" }, 0.9, false,
            rng => Prm(("seed", Bs(rng)), ("wind", rng.Range(-0.45, 0.45)), ("dens", rng.Range(0.85, 1.2))),
            DrawSnowLayers);

        BgReg("fireworks", "花火", new[] { "pop", "emotional" }, 0.7, false,
            rng => Prm(("seed", Bs(rng)), ("per", rng.Range(0.55, 0.8)), ("k", rng.Range(0.42, 0.55))),
            DrawFireworks);

        BgReg("cloudLayers", "雲", new[] { "calm", "emotional", "pop" }, 0.8, false,
            rng => Prm(("seed", Bs(rng)), ("dir", rng.Pick(new[] { 1.0, -1.0 })), ("k", rng.Range(0.06, 0.09))),
            DrawCloudLayers);

        BgReg("mountains", "山並み", new[] { "calm", "emotional", "editorial" }, 0.8, false,
            rng => Prm(
                ("seed", Bs(rng)), ("n", rng.Int(3, 4)), ("k", rng.Range(0.07, 0.1)),
                ("dir", rng.Pick(new[] { 1.0, -1.0 })), ("mist", rng.Chance(0.7))),
            DrawMountains);

        BgReg("filmStrip", "フィルム", new[] { "editorial", "emotional", "glitch" }, 0.7, false,
            rng => Prm(
                ("seed", Bs(rng)), ("dir", rng.Pick(new[] { 1.0, -1.0 })), ("spd", rng.Range(0.6, 1.2)),
                ("scratch", rng.Chance(0.7))),
            DrawFilmStrip);

        BgReg("vhsBand", "VHSノイズ", new[] { "glitch", "emotional" }, 0.6, false,
            rng => Prm(
                ("seed", Bs(rng)), ("h", rng.Range(0.07, 0.12)),
                ("spd", rng.Range(0.08, 0.16) * rng.Pick(new[] { 1.0, -1.0 })),
                ("k", rng.Range(0.22, 0.32))),
            DrawVhsBand);

        BgReg("tornPaper", "破れ紙", new[] { "editorial", "emotional", "pop" }, 0.7, false,
            rng => Prm(
                ("seed", Bs(rng)), ("v", rng.Pick(new[] { "tb", "tb", "diag", "side" })), ("k", rng.Range(0.05, 0.08))),
            DrawTornPaper);

        BgReg("godRays", "光芒", new[] { "emotional", "calm", "editorial" }, 0.8, false,
            rng => Prm(
                ("seed", Bs(rng)),
                ("x", rng.Pick(new object[] { rng.Range(0.12, 0.35), rng.Range(0.65, 0.88), 0.5 })),
                ("n", rng.Int(7, 11)), ("spread", rng.Range(45, 75)), ("k", rng.Range(0.11, 0.16)),
                ("dust", rng.Chance(0.75))),
            DrawGodRays);

        BgReg("vignettePulse", "色の周辺光", new[] { "emotional", "calm", "pop" }, 0.9, true,
            rng => Prm(
                ("seed", Bs(rng)), ("k", rng.Range(0.3, 0.42)), ("two", rng.Chance(0.6)),
                ("rate", rng.Range(0.35, 0.55))),
            DrawVignettePulse);

        BgReg("kaleidoscope", "万華鏡", new[] { "pop", "glitch", "emotional" }, 0.6, false,
            rng => Prm(
                ("seed", Bs(rng)), ("n", rng.Pick(new[] { 6.0, 8.0, 8.0, 10.0 })), ("m", rng.Int(6, 9)),
                ("k", rng.Range(0.075, 0.1)),
                ("spd", rng.Range(0.05, 0.1) * rng.Pick(new[] { 1.0, -1.0 }))),
            DrawKaleidoscope);

        BgReg("marble", "大理石", new[] { "calm", "editorial", "emotional" }, 0.7, false,
            rng => Prm(
                ("seed", Bs(rng)), ("ang", rng.Range(0, 3.14)), ("freq", rng.Range(1.4, 2.2)),
                ("turb", rng.Range(7, 10)), ("k", rng.Range(0.17, 0.22)), ("acc", rng.Chance(0.5)),
                ("dir", rng.Pick(new[] { 1.0, -1.0 }))),
            DrawMarble);

        BgReg("paperCut", "切り絵", new[] { "pop", "emotional", "calm" }, 0.7, false,
            rng => Prm(
                ("seed", Bs(rng)), ("L", rng.Int(3, 4)), ("lobes", rng.Int(5, 9)), ("k", rng.Range(0.06, 0.09)),
                ("acc", rng.Chance(0.5)), ("p", rng.Range(2.4, 3.2))),
            DrawPaperCut);

        // ================= cam 运镜 =================

        Reg.Register("cam", "orbitDrift", new CamDef
        {
            Name = "周回", Tags = new[] { "calm", "emotional", "graphic" }, W = 0.8,
            Plan = Plan(rng => Prm(("dir", rng.Pick(new[] { 1.0, -1.0 })), ("a0", rng.Range(0, 6.28)), ("sp", rng.Range(1.1, 1.6)))),
            Get = env =>
            {
                var p = CamP(env);
                var K = KM(env);
                var d = Num(p, "dir", 1);
                var th = Num(p, "a0", 0) + d * env.Lt * Num(p, "sp", 1.3);
                var r = J.E.OutCubic(J.Clamp(env.Lt / 0.7));
                return new CamState
                {
                    X = Math.Cos(th) * env.W * 0.016 * K * r,
                    Y = Math.Sin(th) * env.H * 0.02 * K * r,
                    Rot = Math.Sin(th) * 0.9 * K * r * d,
                    S = 1.02
                };
            }
        }, PKG);

        Reg.Register("cam", "barrelRoll", new CamDef
        {
            Name = "バレルロール", Tags = new[] { "pop", "glitch", "graphic" }, W = 0.5, Strong = true,
            Plan = Plan(rng => Prm(("dir", rng.Pick(new[] { 1.0, -1.0 })), ("a", rng.Range(70, 110)), ("d", rng.Range(0.42, 0.55)))),
            Get = env =>
            {
                var p = CamP(env);
                var K = Math.Min(1, KM(env));
                var q = J.Clamp(env.Lt / Num(p, "d", 0.5));
                if (q >= 1) return new CamState(); // JS return {}：全默认
                var r = 1 - J.E.OutBack(q, 1.3); // 快速翻转，稍微过冲再回正
                return new CamState
                {
                    Rot = Num(p, "dir", 1) * (Num(p, "a", 90)) * K * r,
                    S = 1 - 0.12 * K * Math.Sin(Math.PI * Math.Min(1, q * 1.25)),
                    Blur = 9 * K * J.Clamp(1 - q * 2.2)
                };
            }
        }, PKG);

        Reg.Register("cam", "pendulumSway", new CamDef
        {
            Name = "振り子", Tags = new[] { "emotional", "pop", "calm" }, W = 0.7,
            Plan = Plan(rng => Prm(("a", rng.Range(1.8, 2.6)), ("per", rng.Range(2, 3)), ("side", rng.Pick(new[] { 1.0, -1.0 })))),
            Get = env =>
            {
                // 画面挂在屏幕上方的支点上，旋转和横移是联动的
                var p = CamP(env);
                var K = KM(env);
                var damp = 0.75 + 0.25 * Math.Exp(-env.Lt * 0.6);
                var L = env.W * 0.62; // 这样横移在任意画幅下都约是宽度的 3%
                var phi = Num(p, "a", 2.2) * K * Num(p, "side", 1) * Math.Cos(env.Lt / Num(p, "per", 2.4) * J.TAU) * damp * J.DEG;
                return new CamState { X = -L * Math.Sin(phi), Y = -L * (1 - Math.Cos(phi)), Rot = phi / J.DEG, S = 1.02 };
            }
        }, PKG);

        Reg.Register("cam", "focusIn", new CamDef
        {
            Name = "ピント合わせ", Tags = new[] { "emotional", "calm", "editorial" }, W = 0.9,
            Plan = Plan(rng => Prm(("d", rng.Range(0.5, 0.8)), ("b", rng.Range(10, 15)))),
            Get = env =>
            {
                var p = CamP(env);
                var K = KM(env);
                var q = J.E.OutCubic(J.Clamp(env.Lt / Num(p, "d", 0.65)));
                return new CamState
                {
                    // 对焦时的镜头呼吸
                    Blur = (1 - q) * Num(p, "b", 12) * Math.Min(1, K),
                    S = 1 + 0.03 * (1 - q) + 0.012 * K * CuOf(env)
                };
            }
        }, PKG);

        Reg.Register("cam", "rackFocus", new CamDef
        {
            Name = "ピンぼけ", Tags = new[] { "emotional", "calm", "editorial" }, W = 0.6,
            Plan = Plan(rng => Prm(("b", rng.Range(5, 8)), ("at", rng.Range(0.6, 0.7)))),
            Get = env =>
            {
                var p = CamP(env);
                var K = KM(env);
                var dur = env.Cut?.Dur ?? 0;
                var st = Math.Max(dur * Num(p, "at", 0.65), dur - 0.9);
                var q = J.E.InOutSine(J.Clamp((env.Lt - st) / Math.Max(0.2, dur - st)));
                return new CamState
                {
                    Blur = q * Num(p, "b", 6.5) * Math.Min(1, K),
                    S = 1.01 - 0.02 * q * K,
                    Y = env.H * 0.004 * q
                };
            }
        }, PKG);

        Reg.Register("cam", "earthquake", new CamDef
        {
            Name = "地震", Tags = new[] { "glitch", "pop", "emotional" }, W = 0.5, Strong = true,
            Plan = Plan(rng => Prm(("per", rng.Range(0.55, 0.8)), ("a", rng.Range(0.85, 1.1)))),
            Get = env =>
            {
                // 一直有低频晃动，节拍上再叠一下猛的；纵向为主，跑在 ≤24Hz 的随机时钟上
                var p = CamP(env);
                var K = KM(env) * Num(p, "a", 1);
                var hit = Math.Exp(-BeatSince(env, Num(p, "per", 0.65)) * 7);
                var amp = K * (0.14 + hit);
                var sd = SeedOf(env);
                var st = unchecked((int)env.Step);
                return new CamState
                {
                    X = J.Rs(sd, st, 11) * env.W * 0.005 * amp,
                    Y = J.Rs(sd, st, 12) * env.H * 0.014 * amp,
                    Rot = J.Rs(sd, st, 13) * 0.45 * amp,
                    S = 1.02 + 0.012 * hit * K,
                    Blur = 1.5 * hit * K
                };
            }
        }, PKG);

        Reg.Register("cam", "floatNoise", new CamDef
        {
            Name = "浮遊", Tags = new[] { "calm", "emotional" }, W = 0.9,
            Plan = Plan(rng => Prm(("f", rng.Range(0.8, 1.2)))),
            Get = env =>
            {
                var p = CamP(env);
                var K = KM(env);
                var t = env.Lt * Num(p, "f", 1);
                var sd = SeedOf(env) + 7;
                return new CamState
                {
                    X = J.Noise1(t * 0.6, sd) * env.W * 0.02 * K,
                    Y = (J.Noise1(t * 0.5 + 5, sd + 1) * 0.7 + 0.3 * Math.Sin(t * 1.3)) * env.H * 0.024 * K,
                    Rot = J.Noise1(t * 0.3 + 9, sd + 2) * 1.3 * K,
                    S = 1.025 + 0.012 * Math.Sin(t * 0.8)
                };
            }
        }, PKG);

        Reg.Register("cam", "vertigo", new CamDef
        {
            Name = "めまい", Tags = new[] { "emotional", "glitch" }, W = 0.6,
            Plan = Plan(rng => Prm(("dir", rng.Pick(new[] { 1.0, -1.0 })), ("a", rng.Range(0.05, 0.07)))),
            Get = env =>
            {
                // 缓慢推进但透视一直在变形：拉伸/错切随缩放变大
                var p = CamP(env);
                var K = KM(env);
                var z = J.E.InOutSine(CuOf(env));
                var w = Math.Sin(env.Lt * 2.3);
                var d = Num(p, "dir", 1);
                // TODO(Registry)：CamState 无 Sx/Sy 字段——JS 的 sx/sy 变形暂不入状态（仅 Skx/Rot/S 生效）
                return new CamState
                {
                    S = 1 + Num(p, "a", 0.06) * K * z,
                    Skx = 2.2 * K * z * Math.Sin(env.Lt * 1.7) * d,
                    Rot = 0.8 * K * z * Math.Sin(env.Lt * 1.1 + 1) * d
                };
            }
        }, PKG);

        Reg.Register("cam", "tiltDown", new CamDef
        {
            Name = "ティルトダウン", Tags = new[] { "calm", "editorial", "emotional" }, W = 0.8,
            Plan = Plan(rng => Prm(("a", rng.Range(0.024, 0.032)))),
            Get = env =>
            {
                // 镜头向下摇：从画面下方升进来再停稳，顺带从轻微的 zoom 里缓出来
                var p = CamP(env);
                var K = KM(env);
                var q = J.E.OutCubic(CuOf(env));
                var a = env.H * Num(p, "a", 0.028) * K;
                return new CamState { Y = a * (1.2 - 1.6 * q), S = 1.035 - 0.02 * q };
            }
        }, PKG);

        Reg.Register("cam", "spiralIn", new CamDef
        {
            Name = "渦ズーム", Tags = new[] { "pop", "graphic", "emotional" }, W = 0.6,
            Plan = Plan(rng => Prm(("dir", rng.Pick(new[] { 1.0, -1.0 })), ("a0", rng.Range(0, 6.28)), ("d", rng.Range(0.9, 1.3)))),
            Get = env =>
            {
                var p = CamP(env);
                var K = KM(env);
                var d = Num(p, "dir", 1);
                var e = J.E.OutCubic(J.Clamp(env.Lt / Num(p, "d", 1.1)));
                var r = 1 - e;
                var th = Num(p, "a0", 0) + d * e * J.TAU * 0.8;
                return new CamState
                {
                    X = Math.Cos(th) * env.W * 0.03 * K * r,
                    Y = Math.Sin(th) * env.H * 0.035 * K * r,
                    Rot = -d * 7 * K * r,
                    S = 1 - 0.08 * K * r + 0.015 * K * CuOf(env)
                };
            }
        }, PKG);

        Reg.Register("cam", "snapPan", new CamDef
        {
            Name = "スナップパン", Tags = new[] { "pop", "glitch", "graphic" }, W = 0.7,
            Plan = Plan(rng => Prm(("dir", rng.Pick(new[] { 1.0, -1.0 })), ("a", rng.Range(0.024, 0.032)), ("at", rng.Range(0.45, 0.6)))),
            Get = env =>
            {
                // 先保持一个构图，中途猛甩到相反构图（有节拍就卡节拍），再保持住
                var p = CamP(env);
                var K = KM(env);
                var dur = env.Cut?.Dur ?? 0;
                var d = Num(p, "dir", 1);
                var A = env.W * Num(p, "a", 0.028) * K * d;
                var drift = env.W * 0.005 * K * d * (CuOf(env) - 0.5);
                if (dur < 1.1) return new CamState { X = A * 0.5 * (1 - 2 * CuOf(env)), S = 1.02 };
                var dt = env.Lt - SnapTime(env, Num(p, "at", 0.5));
                var q = J.E.InOutCubic(J.Clamp(dt / 0.16));
                var bell = dt > 0 && dt < 0.16 ? Math.Sin(Math.PI * dt / 0.16) : 0;
                return new CamState
                {
                    X = A * (1 - 2 * q) - drift,
                    S = 1.02 + 0.015 * bell,
                    Skx = -d * 5 * K * bell,
                    Blur = 14 * K * bell
                };
            }
        }, PKG);

        Reg.Register("cam", "jelly", new CamDef
        {
            Name = "ぷるん", Tags = new[] { "pop", "graphic" }, W = 0.7,
            Plan = Plan(rng => Prm(("a", rng.Range(0.045, 0.065)), ("f", rng.Range(18, 24)))),
            Get = env =>
            {
                // 挤压拉伸的晃动：切分开始时压扁落地，节拍上再晃一次
                var p = CamP(env);
                var K = KM(env);
                var t = env.Lt;
                var f = Num(p, "f", 21);
                var w = Math.Exp(-t * 5) * Math.Cos(t * f);
                // JS：beat 且 t>0.6 时叠一记 exp(-s*7)*cos(s*f)（sx/sy 仍不入 CamState，见文件头 TODO）
                if (env.BeatHas && env.BeatLen > 0.2 && t > 0.6)
                {
                    var bs = BeatSince(env, 0.6);
                    w += 0.45 * Math.Exp(-bs * 7) * Math.Cos(bs * f);
                }
                var A = Num(p, "a", 0.055) * K;
                return new CamState { Y = env.H * 0.008 * K * w, S = 1.01 }; // sx/sy 暂不入 CamState（见文件头 TODO）
            }
        }, PKG);
    }

    // ================= 本包 helper（JS L9-212 逐条对位） =================

    private static bool IsDark(string c) => J.Lum(c) < 0.45;
    private static string Bg(Scheme sc) => sc.Str("bg") ?? "#000000";
    private static string Fg(Scheme sc) => sc.Str("fg") ?? "#FFFFFF";
    private static string Ac(Scheme sc) => sc.Str("accent") ?? "#FFFFFF";

    /// <summary>layC：淡淡的底色层，k 大致在 0.04..0.15。</summary>
    private static string LayC(Scheme sc, double k) => J.Mix(Bg(sc), Fg(sc), k);

    /// <summary>tintC：淡淡的强调色。</summary>
    private static string TintC(Scheme sc, double k) => J.Mix(Bg(sc), Ac(sc), k);

    private static double Umin(Env env) => Math.Min(env.W, env.H);
    private static int Bs(Rng rng) => rng.Int(1, 1_000_000_000);
    private static double Wrap(double v, double m) => ((v % m) + m) % m;
    private static double Fract(double v) => v - Math.Floor(v);
    private static double Hyp(double x, double y) => Math.Sqrt(x * x + y * y);
    private static double Round(double v) => Math.Round(v, MidpointRounding.AwayFromZero); // JS Math.round

    private static string F2(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    private static string F3(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);

    private static SKColor Rgba(string? col, double a = 1)
    {
        var (r, g, b) = J.Hex(col ?? "#FFFFFF");
        return new SKColor((byte)r, (byte)g, (byte)b, (byte)Math.Round(J.Clamp(a) * 255));
    }

    /// <summary>hues：从配色里挑跟背景拉得开的颜色，accent 优先，去重。</summary>
    private static List<string> Hues(Scheme sc)
    {
        var bg = Bg(sc);
        var outs = new List<string>();
        foreach (var c in new[] { sc.Str("accent"), sc.Str("accent2"), sc.Str("ghostA"), sc.Str("ghostB"), sc.Str("fg") })
        {
            if (c == null || J.Contrast(c, bg) < 1.25) continue;
            var k = c.ToLowerInvariant();
            if (!outs.Any(o => o.ToLowerInvariant() == k)) outs.Add(c);
        }
        return outs.Count > 0 ? outs : new List<string> { Fg(sc) };
    }

    /// <summary>glowOf：深色背景上明显更亮的颜色，做光效用。</summary>
    private static string GlowOf(Scheme sc)
    {
        var l = J.Lum(Bg(sc));
        foreach (var c in new[] { sc.Str("accent"), sc.Str("accent2"), sc.Str("ghostA"), sc.Str("ghostB"), sc.Str("fg") })
            if (c != null && J.Lum(c) > l + 0.25) return c;
        return Fg(sc);
    }

    /// <summary>lightOn：浅背景上也看得清的光色：中浅底给纯白，接近纸白就改用 accent 混白。</summary>
    private static string LightOn(Scheme sc) => IsDark(Bg(sc))
        ? GlowOf(sc)
        : J.Lum(Bg(sc)) < 0.78 ? "#FFFFFF" : J.Mix(Ac(sc), "#FFFFFF", 0.2);

    // 当前背景播了多久；相邻切分同 bg 同 seed 算同一段连续播放（JS WeakMap → CWT）
    private static readonly ConditionalWeakTable<Cut, double[]> BgRun = new();

    private static double BgT(Env env)
    {
        var c = env.Cut;
        if (c == null) return env.T;
        if (BgRun.TryGetValue(c, out var box)) return env.T - box[0];
        var s = c.Start;
        var cs = env.Plan?.Cuts;
        if (cs != null)
            for (var i = c.Index - 1; i >= 0 && i < cs.Count; i--)
            {
                var p = cs[i];
                if (p.Bg == c.Bg && JToken.DeepEquals(p.BgP["seed"], c.BgP["seed"]) && Math.Abs(p.End - s) < 0.06) s = p.Start;
                else break;
            }
        BgRun.AddOrUpdate(c, new[] { s });
        return env.T - s;
    }

    private static double FadeIn(Env env, double d = 0.6) => J.E.OutCubic(J.Clamp(BgT(env) / d));

    /// <summary>JS plan(rng) 只吃 rng；注册表的 Plan 是 (rng, st) —— 适配器对位。</summary>
    private static Func<Rng, StyleDef, JObject> Plan(Func<Rng, JObject> f) => (rng, st) => f(rng);

    private static void BgReg(string key, string name, string[] tags, double w, bool subtle,
        Func<Rng, JObject> plan, Action<Env> draw)
        => Reg.Register("bg", key, new BgDef
        {
            Key = key,
            Name = name,
            Tags = tags,
            W = w,
            Subtle = subtle,
            Plan = Plan(plan),
            // bgReg 包装：save/restore 包住条目自己的绘制
            Draw = env =>
            {
                using var _ = new SKAutoCanvasRestore(env.Ctx);
                draw(env);
            }
        }, PKG);

    // 预渲染缓存（瓦片/精灵/纹理），小 LRU（≤40 条），Owner 持有像素源（putImageData 类）
    private sealed class ImgEntry
    {
        public SKImage Img = null!;
        public IDisposable? Owner;
    }

    private static readonly Dictionary<string, ImgEntry> Cv = new();

    private static SKImage Cached(string key, Func<ImgEntry> make)
    {
        if (Cv.TryGetValue(key, out var hit))
        {
            Cv.Remove(key);
            Cv[key] = hit;
            return hit.Img;
        }
        while (Cv.Count >= 40)
        {
            var k0 = Cv.Keys.First();
            Cv[k0].Img.Dispose();
            Cv[k0].Owner?.Dispose();
            Cv.Remove(k0);
        }
        var e = make();
        Cv[key] = e;
        return e.Img;
    }

    private static SKImage Cached(string key, Func<SKImage> make) => Cached(key, () => new ImgEntry { Img = make() });

    // 瓦片位图缓存（CreateBitmap 需要 SKBitmap，P11LayoutsB.DotTile 同款）
    private static readonly Dictionary<string, SKBitmap> BmpCv = new();

    private static SKBitmap CachedBmp(string key, Func<SKBitmap> make)
    {
        if (BmpCv.TryGetValue(key, out var hit))
        {
            BmpCv.Remove(key);
            BmpCv[key] = hit;
            return hit;
        }
        while (BmpCv.Count >= 40)
        {
            // 同 Plate：只摘引用不 Dispose（淘汰顺序不是 LRU，本帧仍可能有人持有）
            var k0 = BmpCv.Keys.First();
            BmpCv.Remove(k0);
        }
        var c = make();
        BmpCv[key] = c;
        return c;
    }

    private static double ResQ(Env env) => Math.Min(2, Math.Max(0.1, env.Scale == 0 ? 1 : env.Scale));

    /// <summary>tileCv：周期性瓦片，周期 pw × ph 设计像素，按输出分辨率渲染（SKBitmap 供 CreateBitmap）。</summary>
    private static SKBitmap TileCv(string key, Env env, double pw, double ph, Action<SKCanvas> paint)
    {
        var q = ResQ(env);
        var w = Math.Max(2, (int)Round(pw * q));
        var h = Math.Max(2, (int)Round(ph * q));
        return CachedBmp($"{key}|{w}x{h}", () =>
        {
            var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var c = new SKCanvas(bmp);
            c.Clear(SKColors.Transparent);
            c.Scale((float)(w / pw), (float)(h / ph));
            paint(c);
            return bmp;
        });
    }

    /// <summary>fillTile：用瓦片填当前空间的 (x0,y0,w,h) 矩形，pattern 原点偏移 (ox,oy)。
    /// JS 的「设备空间整数吸附」分支省略——统一走 shader 平铺（TODO(对拍)：接缝亚像素差）。</summary>
    private static void FillTile(SKCanvas ctx, SKBitmap tile, double pw, double ph,
        double x0, double y0, double w, double h, double ox = 0, double oy = 0, double a = 1)
    {
        using var shader = SKShader.CreateBitmap(tile, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat,
            SKMatrix.Concat(SKMatrix.CreateTranslation((float)ox, (float)oy),
                SKMatrix.CreateScale((float)(pw / tile.Width), (float)(ph / tile.Height))));
        using var p = new SKPaint
        {
            IsAntialias = true,
            Color = SKColors.White.WithAlpha((byte)Math.Round(J.Clamp(a) * 255)),
            Shader = shader
        };
        ctx.DrawRect(new SKRect((float)x0, (float)y0, (float)(x0 + w), (float)(y0 + h)), p);
    }

    // 整帧底板：贵的填充按输出分辨率只画一次，之后每帧整像素 blit
    private static readonly Dictionary<string, SKImage> Pl = new();

    private static SKImage Plate(string key, Env env, double mx, double my, Action<SKCanvas, double, double> paint)
    {
        var q = ResQ(env);
        var w = (int)Math.Ceiling((env.W + mx) * q) + 2;
        var h = (int)Math.Ceiling((env.H + my) * q) + 2;
        var k = $"{key}|{w}x{h}";
        if (Pl.TryGetValue(k, out var hit))
        {
            Pl.Remove(k);
            Pl[k] = hit;
            return hit;
        }
        long px = (long)w * h;
        foreach (var v in Pl.Values) px += (long)v.Width * v.Height;
        var budget = Math.Max(30e6, (double)w * h * 3.2); // 上限约 120 MB
        while (Pl.Count > 0 && (Pl.Count >= 12 || px > budget))
        {
            var k0 = Pl.Keys.First();
            var v0 = Pl[k0];
            px -= (long)v0.Width * v0.Height;
            // 只摘引用、不 Dispose：淘汰顺序（Keys.First）不是 LRU——Remove+Add 会复用空槽，刚取过的
            // 底板也可能被选中，而调用方的局部变量还持有它（同帧多次 Plate：底图 + 线纹，或背景切换的
            // 新旧两层）。当场 Dispose 会让随后的 Blit 读到已释放的 SKImage → 0xC0000005
            // （崩在 sk_image_get_width）。交 GC 回收：仍被局部变量引用的对象 GC 不会动，
            // 引用消失后终结器再释放，语义上不可能提前释放。
            Pl.Remove(k0);
        }
        using var surf = SKSurface.Create(new SKImageInfo(w, h));
        surf.Canvas.Scale((float)q, (float)q);
        paint(surf.Canvas, w / q, h / q);
        var img = surf.Snapshot();
        Pl[k] = img;
        return img;
    }

    /// <summary>把底板的 (ox,oy)（设计像素）对到画面左上角，并吸附到整数设备像素。</summary>
    private static void Blit(SKCanvas ctx, Env env, SKImage cv, double ox, double oy, double a = 1,
        SKBlendMode blend = SKBlendMode.SrcOver)
    {
        var m = ctx.TotalMatrix;
        var s = m.ScaleX / ResQ(env);
        ctx.Save();
        ctx.ResetMatrix();
        var dx = (float)-Round(ox * m.ScaleX);
        var dy = (float)-Round(oy * m.ScaleX); // JS 同源：y 也用 m.a
        using var p = new SKPaint
        {
            Color = SKColors.White.WithAlpha((byte)Math.Round(J.Clamp(a) * 255)),
            BlendMode = blend
        };
        if (Math.Abs(s - 1) < 1e-3)
            ctx.DrawImage(cv, new SKRect(dx, dy, dx + cv.Width, dy + cv.Height), SmpLinear, p);
        else
            ctx.DrawImage(cv, new SKRect(dx, dy, dx + (float)(cv.Width * s), dy + (float)(cv.Height * s)), SmpLinear, p);
        ctx.Restore();
    }

    // 大面积柔效果（渐变、光晕）画到复用的低分辨率画布上，一次性放大
    private static readonly Dictionary<string, SKSurface> LowC = new();

    private static void DrawLow(SKCanvas ctx, Env env, double div, Action<SKCanvas, double> fn)
    {
        var q = env.Scale == 0 ? 1 : env.Scale;
        var w = Math.Max(8, (int)Math.Ceiling(env.W * q / div));
        var h = Math.Max(8, (int)Math.Ceiling(env.H * q / div));
        var k = w + "x" + h;
        if (!LowC.TryGetValue(k, out var c))
        {
            if (LowC.Count > 3)
            {
                foreach (var s in LowC.Values) s.Dispose();
                LowC.Clear();
            }
            c = SKSurface.Create(new SKImageInfo(w, h));
            LowC[k] = c;
        }
        var x = c.Canvas;
        x.ResetMatrix();
        x.Clear(SKColors.Transparent);
        x.Scale((float)(w / env.W), (float)(h / env.H));
        fn(x, w / (double)env.W);
        x.ResetMatrix();
        var pix = c.PeekPixels()!;
        using var img = SKImage.FromPixels(pix.Info, pix.GetPixels(), pix.RowBytes);
        ctx.DrawImage(img, new SKRect(0, 0, env.W, env.H), SmpLinear, null); // imageSmoothingEnabled = true
    }

    // ---------------- 基础绘制助手 ----------------

    /// <summary>stop 列表：颜色收 string（hex）或 SKColor，统一转 SKColor。</summary>
    private static (double Stop, SKColor C)[] Stops(params (double Stop, object Col)[] stops)
        => stops.Select(s => (s.Stop, s.Col is string str ? Rgba(str) : (SKColor)s.Col)).ToArray();

    private static SKShader Lin(double x0, double y0, double x1, double y1, (double Stop, SKColor C)[] stops)
        => SKShader.CreateLinearGradient(new SKPoint((float)x0, (float)y0), new SKPoint((float)x1, (float)y1),
            stops.Select(s => s.C).ToArray(), stops.Select(s => (float)s.Stop).ToArray(), SKShaderTileMode.Clamp);

    private static SKShader Rad(double cx, double cy, double r, (double Stop, SKColor C)[] stops)
        => SKShader.CreateRadialGradient(new SKPoint((float)cx, (float)cy), (float)r,
            stops.Select(s => s.C).ToArray(), stops.Select(s => (float)s.Stop).ToArray(), SKShaderTileMode.Clamp);

    /// <summary>双半径（环带）径向渐变（horizonGlow/nightMoon/vignette/sunsetSun 用）——
    /// Canvas2D createRadialGradient(r0→r1) 底层就是两点锥形渐变，同圆心的 TwoPointConical 等价。</summary>
    private static SKShader Rad2(double cx, double cy, double r0, double r1, (double Stop, SKColor C)[] stops)
        => SKShader.CreateTwoPointConicalGradient(new SKPoint((float)cx, (float)cy), (float)r0,
            new SKPoint((float)cx, (float)cy), (float)r1,
            stops.Select(s => s.C).ToArray(), stops.Select(s => (float)s.Stop).ToArray(), SKShaderTileMode.Clamp);

    /// <summary>createConicGradient → CreateSweepGradient（start/end 角度差一整圈，度）。</summary>
    private static SKShader Conic(double a, double cx, double cy, (double Stop, SKColor C)[] stops)
    {
        var s0 = (float)(a * 180 / Math.PI);
        return SKShader.CreateSweepGradient(new SKPoint((float)cx, (float)cy),
            stops.Select(s => s.C).ToArray(), stops.Select(s => (float)s.Stop).ToArray(),
            SKShaderTileMode.Clamp, s0, s0 + 360);
    }

    private static void FillRect(SKCanvas ctx, double x, double y, double w, double h, object? fill,
        double a = 1, SKBlendMode blend = SKBlendMode.SrcOver)
    {
        if (fill == null || a <= 0) return;
        using var p = new SKPaint { IsAntialias = true, BlendMode = blend };
        if (fill is string col) p.Color = Rgba(col, a);
        else if (fill is SKColor colr) p.Color = colr.WithAlpha((byte)Math.Round(J.Clamp(a) * 255));
        else if (fill is SKShader sh)
        {
            p.Color = SKColors.White.WithAlpha((byte)Math.Round(J.Clamp(a) * 255));
            p.Shader = sh;
        }
        else return;
        ctx.DrawRect(new SKRect((float)x, (float)y, (float)(x + w), (float)(y + h)), p);
    }

    private static void FillPath(SKCanvas ctx, SKPath path, object? fill, double a = 1,
        SKBlendMode blend = SKBlendMode.SrcOver)
    {
        if (fill == null || a <= 0) return;
        using var p = new SKPaint { IsAntialias = true, BlendMode = blend };
        if (fill is string col) p.Color = Rgba(col, a);
        else if (fill is SKColor colr) p.Color = colr.WithAlpha((byte)Math.Round(J.Clamp(a) * 255));
        else if (fill is SKShader sh)
        {
            p.Color = SKColors.White.WithAlpha((byte)Math.Round(J.Clamp(a) * 255));
            p.Shader = sh;
        }
        else return;
        ctx.DrawPath(path, p);
    }

    private static void Stroke(SKCanvas ctx, SKPath path, string? col, double lw, double a = 1,
        SKBlendMode blend = SKBlendMode.SrcOver, SKStrokeCap cap = SKStrokeCap.Butt,
        SKStrokeJoin join = SKStrokeJoin.Miter, SKPathEffect? dash = null)
    {
        if (col == null || a <= 0) return;
        using var p = new SKPaint
        {
            IsAntialias = true,
            IsStroke = true,
            StrokeWidth = (float)lw,
            Color = Rgba(col, a),
            BlendMode = blend,
            StrokeCap = cap,
            StrokeJoin = join,
            PathEffect = dash
        };
        ctx.DrawPath(path, p);
    }

    private static void DrawImg(SKCanvas ctx, SKImage img, double dx, double dy, double dw, double dh,
        double a = 1, SKBlendMode blend = SKBlendMode.SrcOver, SKSamplingOptions? smp = null)
    {
        using var p = new SKPaint
        {
            Color = SKColors.White.WithAlpha((byte)Math.Round(J.Clamp(a) * 255)),
            BlendMode = blend
        };
        ctx.DrawImage(img, new SKRect((float)dx, (float)dy, (float)(dx + dw), (float)(dy + dh)), smp ?? SmpLinear, p);
    }

    private static void DrawImgSrc(SKCanvas ctx, SKImage img, SKRect src, SKRect dst, double a = 1,
        SKBlendMode blend = SKBlendMode.SrcOver, SKSamplingOptions? smp = null)
    {
        using var p = new SKPaint
        {
            Color = SKColors.White.WithAlpha((byte)Math.Round(J.Clamp(a) * 255)),
            BlendMode = blend
        };
        ctx.DrawImage(img, src, dst, smp ?? SmpLinear, p);
    }

    /// <summary>pathPoly：闭合多边形轮廓（追加到现有 path）。</summary>
    private static void Poly(SKPath p, double[][] pts)
    {
        p.MoveTo((float)pts[0][0], (float)pts[0][1]);
        for (var i = 1; i < pts.Length; i++) p.LineTo((float)pts[i][0], (float)pts[i][1]);
        p.Close();
    }

    private static void AddCircle(SKPath p, double cx, double cy, double r)
        => p.AddOval(new SKRect((float)(cx - r), (float)(cy - r), (float)(cx + r), (float)(cy + r)));

    // 快速 2D value noise，输出 0..1，预渲染和逐帧场都用
    private static double Hash2(int x, int y, int s)
    {
        unchecked
        {
            var h = x * 374761393 + y * 668265263 + s * 144665;
            h = (h ^ (int)((uint)h >> 13)) * 1274126177;
            h ^= (int)((uint)h >> 16);
            return (uint)h / 4294967296.0;
        }
    }

    private static double Noise2(double x, double y, int s)
    {
        var ix = (int)Math.Floor(x);
        var iy = (int)Math.Floor(y);
        var fx = x - ix;
        var fy = y - iy;
        var u = fx * fx * (3 - 2 * fx);
        var v = fy * fy * (3 - 2 * fy);
        var a = Hash2(ix, iy, s);
        var b = Hash2(ix + 1, iy, s);
        var c = Hash2(ix, iy + 1, s);
        var d = Hash2(ix + 1, iy + 1, s);
        return a + (b - a) * u + (c - a) * v + (a - b - c + d) * u * v;
    }

    private static double Fbm2(double x, double y, int s, int oct = 3)
    {
        double v = 0, a = 0.5, f = 1, n = 0;
        for (var i = 0; i < oct; i++)
        {
            v += Noise2(x * f, y * f, s + i * 101) * a;
            n += a;
            a *= 0.5;
            f *= 2.03;
        }
        return v / n;
    }

    private static double Fbm1(double x, int s, int oct = 3)
    {
        double v = 0, a = 0.5, f = 1, n = 0;
        for (var i = 0; i < oct; i++)
        {
            v += J.Noise1(x * f, s + i * 53) * a;
            n += a;
            a *= 0.5;
            f *= 2.1;
        }
        return v / n;
    } // 输出 -1..1

    // ---------------- bg 条目 Draw（源注册顺序） ----------------

    // 每种颜色一条竖直帘条：顶部透明，下沿最亮
    private static SKImage AuroraStrip(string col) => Cached("aurS|" + col, () =>
    {
        const int w = 4, h = 256;
        using var surf = SKSurface.Create(new SKImageInfo(w, h));
        var x = surf.Canvas;
        using (var p = new SKPaint
               {
                   IsAntialias = true,
                   Shader = Lin(0, 0, 0, 256, Stops(
                       (0, Rgba(col, 0)), (0.5, Rgba(col, 0.22)), (0.88, Rgba(col, 0.85)),
                       (0.94, Rgba(col, 1)), (1, Rgba(col, 0))))
               })
            x.DrawRect(0, 0, w, h, p);
        return surf.Snapshot();
    });

    private static void DrawAuroraRibbons(Env env)
    {
        var ctx = env.Ctx;
        var (W, H) = (env.W, env.H);
        var sc = env.Sc!;
        var P = POf(env);
        var s = (int)Num(P, "seed", 1);
        var t = env.T;
        var dk = IsDark(Bg(sc));
        var cols = Hues(sc);
        var e = FadeIn(env, 1.2);
        var N = J.Clamp((int)Round(W / 18.0), 50, 120);
        var cw = W / (double)N;
        var k = Num(P, "k", 0.24) * e * (dk ? 1.25 : 0.8);
        var lite = cols.Where(c => J.Lum(c) > J.Lum(Bg(sc)) + 0.12).ToList();
        var lc = dk
            ? lite.Count > 0 ? lite : new List<string> { LightOn(sc) }
            : new List<string> { LightOn(sc), Ac(sc) };
        DrawLow(ctx, env, 4, (x, q) =>
        {
            var n = (int)Num(P, "n", 2);
            for (var r = 0; r < n; r++)
            {
                var col = lc[((int)Num(P, "c0", 0) + r) % lc.Count];
                var strip = AuroraStrip(col);
                var @base = H * (Num(P, "y", 0.42) + r * 0.1 - 0.05);
                var dir = r % 2 != 0 ? -1 : 1;
                var ph = (s % 97) * 0.13 + r * 2.1;
                for (var i = 0; i <= N; i++)
                {
                    var u = i / (double)N;
                    var xx = i * cw - cw / 2;
                    var y = @base + H * 0.07 * Math.Sin(u * J.TAU * 0.75 + t * 0.3 * dir + ph)
                            + H * 0.045 * J.Noise1(u * 3.5 + t * 0.22 * dir, s + r * 7);
                    var h = Math.Max(W, H) * (0.24 + 0.12 * J.Noise1(u * 2.6 - t * 0.15, s + r * 13 + 5))
                            * (1 - 0.3 * r / 3.0) * (0.8 + 0.2 * e);
                    var ray = Math.Pow(0.5 + 0.5 * J.Noise1(u * 34 + t * 0.9 * dir, s + r * 31), 1.4)
                              * (0.7 + 0.3 * J.Noise1(u * 13 - t * 0.4, s + r * 3));
                    var a = k * (0.2 + 1.1 * ray) * J.Smooth(0, 0.18, u) * J.Smooth(1, 0.82, u)
                            * (0.75 + 0.25 * J.Noise1(u * 1.5 + t * 0.1, s + r));
                    if (a <= 0.004) continue;
                    // 列吸附到低分辨率像素上，相邻条带才接得上无缝
                    var x0 = Round(xx * q) / q;
                    var x1 = Round((xx + cw) * q) / q;
                    if (x1 <= x0) continue;
                    DrawImgSrc(x, strip, new SKRect(0, 0, 4, 256),
                        new SKRect((float)x0, (float)(y - h), (float)x1, (float)(y - h + h * 1.07)), a);
                }
            }
        });
    }

    private static void DrawMeshBlobs(Env env)
    {
        var ctx = env.Ctx;
        var (W, H) = (env.W, env.H);
        var sc = env.Sc!;
        var P = POf(env);
        var s = (int)Num(P, "seed", 1);
        var t = env.T;
        var cols = Hues(sc);
        var dk = IsDark(Bg(sc));
        var e = FadeIn(env, 1);
        var R0 = Math.Max(W, H) * 0.5;
        var n = (int)Num(P, "n", 3);
        DrawLow(ctx, env, 6, (x, _) =>
        {
            for (var i = 0; i < n; i++)
            {
                var w1 = J.Rr(0.16, 0.3, s, i, 1);
                var w2 = J.Rr(0.14, 0.26, s, i, 2);
                var cx = W * (0.5 + 0.42 * Math.Sin(t * w1 + J.R(s, i, 3) * J.TAU));
                var cy = H * (0.5 + 0.4 * Math.Cos(t * w2 + J.R(s, i, 4) * J.TAU));
                var R = R0 * J.Rr(0.75, 1.1, s, i, 5) * (1 + 0.08 * Math.Sin(t * 0.45 + i * 1.7));
                var c = J.Mix(Bg(sc), cols[i % cols.Count], Num(P, "k", 0.25) * (dk ? 1 : 0.75));
                using var g = Rad(cx, cy, R, Stops((0, Rgba(c, 0.95 * e)), (0.45, Rgba(c, 0.5 * e)), (1, Rgba(c, 0))));
                var x0 = Math.Max(0, cx - R);
                var y0 = Math.Max(0, cy - R);
                var x1 = Math.Min(W, cx + R);
                var y1 = Math.Min(H, cy + R);
                if (x1 > x0 && y1 > y0) FillRect(x, x0, y0, x1 - x0, y1 - y0, g);
            }
        });
    }

    private static void DrawDuotoneSweep(Env env)
    {
        var ctx = env.Ctx;
        var (W, H) = (env.W, env.H);
        var sc = env.Sc!;
        var P = POf(env);
        var cols = Hues(sc);
        var e = FadeIn(env, 0.8);
        var k = Num(P, "k", 0.16);
        var cA = J.Mix(Bg(sc), cols[0], k);
        var cB = J.Mix(Bg(sc), cols.Count > 1 ? cols[1] : Fg(sc), k);
        var pos = Pair(P, "pos", new[] { 0.5, 1.2 });
        var cx = W * pos[0];
        var cy = H * pos[1];
        var a = Num(P, "a0", 0) + env.T * Num(P, "spd", 0.1);
        DrawLow(ctx, env, 6, (x, _) =>
        {
            // Skia 原生有 sweep：conic 可用，无回退分支
            using (var g = Conic(a, cx, cy, Stops((0, cA), (0.25, cB), (0.5, cA), (0.75, cB), (1, cA))))
                FillRect(x, 0, 0, W, H, g, 0.9 * e);
            // 跟着扫掠一起转的柔高光
            using (var g2 = Conic(a * 1.6 + 1, cx, cy, Stops(
                       (0, Rgba("#000000", 0)), (0.06, Rgba(LightOn(sc), 0.07 * e)), (0.12, Rgba("#000000", 0)),
                       (0.56, Rgba("#000000", 0)), (0.62, Rgba(LightOn(sc), 0.07 * e)), (0.68, Rgba("#000000", 0)),
                       (1, Rgba("#000000", 0))))
                   )
                FillRect(x, 0, 0, W, H, g2, 1);
        });
    }

    private static void DrawHorizonGlow(Env env)
    {
        var ctx = env.Ctx;
        var (W, H) = (env.W, env.H);
        var sc = env.Sc!;
        var P = POf(env);
        var t = env.T;
        var dk = IsDark(Bg(sc));
        var e = FadeIn(env, 1.1);
        var U = Umin(env);
        var R = Math.Max(W, H) * Num(P, "R", 1.6);
        var cx = W * Num(P, "cx", 0.5) + W * 0.03 * Math.Sin(t * 0.08 + NumN(P, "seed", 1) % 9);
        var cy = H * Num(P, "top", 0.75) + R + (1 - e) * H * 0.25 + H * 0.008 * Math.Sin(t * 0.25);
        var gc = dk ? GlowOf(sc) : TintC(sc, 0.8);
        var k = Num(P, "k", 0.32) * (dk ? 1 : 0.55);
        using (var circle = new SKPath())
        {
            circle.AddCircle((float)cx, (float)cy, (float)R);
            FillPath(ctx, circle, dk ? J.Mix(Bg(sc), "#000000", 0.42) : J.Mix(Bg(sc), Fg(sc), 0.05), e);
        }
        var breathe = 0.85 + 0.15 * Math.Sin(t * 0.7);
        var top = Math.Max(0, cy - R * 1.22);
        if (top < H)
        {
            using var ring = new SKPath { FillType = SKPathFillType.EvenOdd };
            ring.AddRect(new SKRect(0, (float)top, W, H));
            ring.AddCircle((float)cx, (float)cy, (float)(R * 0.97));
            using var g = Rad2(cx, cy, R * 0.97, R * 1.22, Stops(
                (0, Rgba(gc, 0)), (0.1, Rgba(gc, k * 0.25 * breathe)), (0.123, Rgba(gc, k * breathe)),
                (0.2, Rgba(gc, k * 0.45 * breathe)), (0.5, Rgba(gc, k * 0.12)), (1, Rgba(gc, 0))));
            FillPath(ctx, ring, g);
        }
        using (var arc = new SKPath())
        {
            arc.AddArc(new SKRect((float)(cx - R), (float)(cy - R), (float)(cx + R), (float)(cy + R)), 180, 180);
            Stroke(ctx, arc, gc, Math.Max(1, U * 0.0022), Math.Min(0.8, k * 1.6));
        }
        // 沿行星边缘慢慢滑的亮斑
        var fa = -Math.PI / 2 + 0.22 * Math.Sin(t * 0.12 + NumN(P, "seed", 1) % 13);
        var fx = cx + Math.Cos(fa) * R;
        var fy = cy + Math.Sin(fa) * R;
        var fr = U * 0.3;
        using (var g2 = Rad2(fx, fy, 0, fr, Stops(
                   (0, Rgba(dk ? gc : LightOn(sc), k * 0.9 * breathe)), (0.3, Rgba(gc, k * 0.25)), (1, Rgba(gc, 0))))
               )
            FillRect(ctx, fx - fr, fy - fr, fr * 2, fr * 2, g2);
    }

    private static void DrawSeigaiha(Env env)
    {
        var ctx = env.Ctx;
        var (W, H) = (env.W, env.H);
        var sc = env.Sc!;
        var P = POf(env);
        var t = env.T;
        var R = Umin(env) * Num(P, "R", 0.08);
        var rings = (int)Num(P, "rings", 4);
        var e = FadeIn(env, 0.7);
        var col = Flag(P, "acc") ? TintC(sc, Num(P, "k", 0.09) * 1.6) : LayC(sc, Num(P, "k", 0.09));
        var fillCol = LayC(sc, Num(P, "k", 0.09) * 0.35);
        var key = $"sgh|{col}|{rings}|{F2(R)}";
        var pl = Plate(key, env, 2 * R, R, (x, w, h) =>
        {
            var tile = TileCv(key, env, 2 * R, R, y =>
            {
                var lw = R * 0.075;
                for (var j = -3; j <= 4; j++)
                for (var i = -1; i <= 2; i++)
                {
                    var cx = i * 2 * R + ((j & 1) != 0 ? R : 0);
                    var cy = j * R / 2;
                    // destination-out 打孔再 source-over 画纹样
                    using (var punch = new SKPath())
                    {
                        AddCircle(punch, cx, cy, R);
                        using var dp = new SKPaint { IsAntialias = true, Color = SKColors.Black, BlendMode = SKBlendMode.DstOut };
                        y.DrawPath(punch, dp);
                    }
                    using (var inner = new SKPath())
                    {
                        AddCircle(inner, cx, cy, R / rings * 0.62);
                        FillPath(y, inner, fillCol);
                    }
                    using var arcs = new SKPath();
                    for (var m = 0; m < rings; m++)
                    {
                        var r = R * (1 - m / (double)rings) - lw * 0.6;
                        if (r > lw) AddCircle(arcs, cx, cy, r);
                    }
                    Stroke(y, arcs, col, lw);
                }
            });
            FillTile(x, tile, 2 * R, R, 0, 0, w, h);
        });
        Blit(ctx, env, pl, Wrap(t * R * 0.22 * Num(P, "dir", 1), 2 * R),
            Wrap(R * 0.5 + Math.Sin(t * 0.5) * R * 0.08 - (1 - e) * R * 0.5, R), e);
    }

    private static void DrawAsanoha(Env env)
    {
        var ctx = env.Ctx;
        var (W, H) = (env.W, env.H);
        var sc = env.Sc!;
        var P = POf(env);
        var t = env.T;
        var a = Umin(env) * Num(P, "a", 0.12);
        var h = a * Math.Sqrt(3) / 2;
        var e = FadeIn(env, 0.8);
        var col = LayC(sc, Num(P, "k", 0.09));
        var key = $"asa|{col}|{F2(a)}";
        var pl = Plate(key, env, a, 2 * h, (x, w, hh) =>
        {
            var tile = TileCv(key, env, a, 2 * h, y =>
            {
                double[] Pt(int r, int i) => new[] { i * a + ((r & 1) != 0 ? a / 2 : 0), r * h };
                using var path = new SKPath();
                void Tri(double[] A, double[] B, double[] C)
                {
                    var gx = (A[0] + B[0] + C[0]) / 3;
                    var gy = (A[1] + B[1] + C[1]) / 3;
                    path.MoveTo((float)A[0], (float)A[1]);
                    path.LineTo((float)B[0], (float)B[1]);
                    path.LineTo((float)C[0], (float)C[1]);
                    path.Close();
                    foreach (var Q in new[] { A, B, C })
                    {
                        path.MoveTo((float)gx, (float)gy);
                        path.LineTo((float)Q[0], (float)Q[1]);
                    }
                }
                for (var r = -2; r <= 3; r++)
                for (var i = -2; i <= 2; i++)
                {
                    if ((r & 1) == 0)
                    {
                        Tri(Pt(r, i), Pt(r, i + 1), Pt(r + 1, i));
                        Tri(Pt(r + 1, i), Pt(r + 1, i + 1), Pt(r, i + 1));
                    }
                    else
                    {
                        Tri(Pt(r, i), Pt(r, i + 1), Pt(r + 1, i + 1));
                        Tri(Pt(r + 1, i), Pt(r + 1, i + 1), Pt(r, i));
                    }
                }
                Stroke(y, path, col, Math.Max(0.8, a * 0.018), 1, SKBlendMode.SrcOver, SKStrokeCap.Round);
            });
            FillTile(x, tile, a, 2 * h, 0, 0, w, hh);
        });
        var ox = Wrap(t * a * 0.08 * Num(P, "dx", 1), a);
        var oy = Wrap(t * h * 0.05 + (1 - e) * h, 2 * h);
        Blit(ctx, env, pl, ox, oy, e);
        // 慢速斜向光带把图案重画得更亮，三层嵌套出软边
        var D = W + H;
        var c = (Wrap(BgT(env) * Num(P, "sweep", 0.09) + 0.4, 1.5) - 0.25) * D;
        var bw = Umin(env) * 0.22;
        foreach (var (f, al) in new[] { (1.0, 0.4), (0.6, 0.4), (0.3, 0.45) })
        {
            ctx.Save();
            using (var clip = new SKPath())
            {
                clip.MoveTo((float)(c - bw * f), 0);
                clip.LineTo((float)(c + bw * f), 0);
                clip.LineTo((float)(c + bw * f - H), H);
                clip.LineTo((float)(c - bw * f - H), H);
                clip.Close();
                ctx.ClipPath(clip, SKClipOperation.Intersect, true);
            }
            Blit(ctx, env, pl, ox, oy, e * al);
            ctx.Restore();
        }
    }

    private static void DrawHoundstooth(Env env)
    {
        var ctx = env.Ctx;
        var (W, H) = (env.W, env.H);
        var sc = env.Sc!;
        var P = POf(env);
        var t = env.T;
        var c = Umin(env) * Num(P, "c", 0.045);
        var e = FadeIn(env, 0.6);
        var col = Flag(P, "acc") ? TintC(sc, Num(P, "k", 0.065) * 1.7) : LayC(sc, Num(P, "k", 0.065));
        var key = $"hnd|{col}|{F2(c)}";
        var pl = Plate(key, env, 2 * c, 2 * c, (x, w, h) =>
        {
            var tile = TileCv(key, env, 2 * c, 2 * c, y =>
            {
                foreach (var (ox, oy) in new[] { (0.0, 0.0), (2 * c, 0.0), (0.0, 2 * c), (2 * c, 2 * c), (-2 * c, 0.0), (0.0, -2 * c) })
                {
                    using var path = new SKPath();
                    path.AddRect(new SKRect((float)ox, (float)oy, (float)(ox + c), (float)(oy + c)));
                    foreach (var (bx, by) in new[] { (ox + c, oy), (ox, oy + c) })
                    {
                        Poly(path, new[] { new[] { bx, by }, new[] { bx + c / 2, by }, new[] { bx, by + c / 2 } });
                        Poly(path, new[] { new[] { bx + c, by }, new[] { bx + c, by + c / 2 }, new[] { bx + c / 2, by + c }, new[] { bx, by + c } });
                    }
                    FillPath(y, path, col);
                }
            });
            FillTile(x, tile, 2 * c, 2 * c, 0, 0, w, h);
        });
        var d = t * c * 0.35;
        Blit(ctx, env, pl, Wrap(d * Num(P, "dx", 1), 2 * c), Wrap(d * 0.6, 2 * c), e);
    }

    private static void DrawHerringbone(Env env)
    {
        var ctx = env.Ctx;
        var (W, H) = (env.W, env.H);
        var sc = env.Sc!;
        var P = POf(env);
        var t = env.T;
        var u = Umin(env) * Num(P, "u", 0.038);
        var e = FadeIn(env, 0.7);
        var k = Num(P, "k", 0.08);
        var rot = Num(P, "rot", 45);
        var cA = LayC(sc, k);
        var cB = LayC(sc, k * 0.45);
        var key = $"hrb|{cA}|{cB}|{F2(u)}";
        var Pd = 4 * u * Math.Sqrt(2); // 45° 编织在屏幕空间的周期
        var pl = Plate(key + "|" + rot.ToString(CultureInfo.InvariantCulture), env, Pd, Pd, (x, w, h) =>
        {
            var tile = TileCv(key, env, 4 * u, 4 * u, y =>
            {
                var g = u * 0.1;
                void Brick(double x0, double y0, double bw, double bh, string c)
                    => FillRect(y, x0 * u + g, y0 * u + g, bw * u - 2 * g, bh * u - 2 * g, c);
                for (var kk = -8; kk <= 8; kk++)
                for (var m = -4; m <= 4; m++)
                {
                    var bx = kk + 2 * m;
                    var by = kk - 2 * m;
                    if (bx > 6 || bx < -3 || by > 6 || by < -3) continue;
                    Brick(bx, by, 2, 1, cA);
                    Brick(bx + 2, by - 1, 1, 2, cB);
                }
            });
            var D = Hyp(w, h) / 2 + 4 * u;
            x.Translate((float)(w / 2), (float)(h / 2));
            x.RotateDegrees((float)rot);
            FillTile(x, tile, 4 * u, 4 * u, -D, -D, 2 * D, 2 * D);
        });
        var sp = t * u * 0.6 * Num(P, "dir", 1); // 沿之字列的行进量
        Blit(ctx, env, pl, rot > 0 ? Pd / 2 : Wrap(sp, Pd), rot > 0 ? Wrap(sp, Pd) : Pd / 2, e);
        // 缓缓扫过织物的高光带
        var L = W + H;
        var bx = (Wrap(t * 0.1, 1.6) - 0.3) * L;
        var bw = Umin(env) * 0.35;
        var hc = LightOn(sc);
        using (var gl = Lin(bx - bw, 0, bx + bw, 0,
                   Stops((0, Rgba(hc, 0)), (0.5, Rgba(hc, IsDark(Bg(sc)) ? 0.035 : 0.08)), (1, Rgba(hc, 0)))))
        {
            ctx.Save();
            ctx.Concat(new SKMatrix(1, -0.6f, 0, 0, 1, 0, 0, 0, 1)); // ctx.transform(1, 0, -0.6, 1, 0, 0)
            FillRect(ctx, bx - bw, 0, bw * 2, H, gl, e);
            ctx.Restore();
        }
    }

    private static void DrawArgyle(Env env)
    {
        var ctx = env.Ctx;
        var (W, H) = (env.W, env.H);
        var sc = env.Sc!;
        var P = POf(env);
        var t = env.T;
        var dw = Umin(env) * Num(P, "dw", 0.18);
        var dh = dw * Num(P, "asp", 1.4);
        var k = Num(P, "k", 0.07);
        var e = FadeIn(env, 0.6);
        var cA = LayC(sc, k);
        var cB = TintC(sc, k * 1.6);
        var cL = LayC(sc, k * 3);
        var kd = $"arg|{cA}|{cB}|{F2(dw)}|{F2(dh)}";
        var kl = $"argL|{cL}|{F2(dw)}|{F2(dh)}";
        var dia = Plate(kd, env, 2 * dw, 2 * dh, (x, w, h) =>
            FillTile(x, TileCv(kd, env, 2 * dw, 2 * dh, y =>
            {
                // (i+j)&1 交替色：按菱形逐个铺
                for (var j = -1; j <= 2; j++)
                for (var i = -1; i <= 2; i++)
                {
                    using var one = new SKPath();
                    var cx = i * dw;
                    var cy = j * dh;
                    Poly(one, new[] { new[] { cx, cy - dh / 2 }, new[] { cx + dw / 2, cy }, new[] { cx, cy + dh / 2 }, new[] { cx - dw / 2, cy } });
                    FillPath(y, one, ((i + j) & 1) != 0 ? cB : cA);
                }
            }), 2 * dw, 2 * dh, 0, 0, w, h));
        var lines = Plate(kl, env, dw, dh, (x, w, h) =>
            FillTile(x, TileCv(kl, env, dw, dh, y =>
            {
                var L = Hyp(dw, dh);
                using var path = new SKPath();
                foreach (var (ox, oy) in new[] { (0.0, 0.0), (dw, 0.0), (0.0, dh), (-dw, 0.0), (0.0, -dh) })
                {
                    path.MoveTo((float)(ox - dw / 2), (float)oy);
                    path.LineTo((float)(ox + dw / 2), (float)(oy + dh));
                    path.MoveTo((float)(ox + dw / 2), (float)oy);
                    path.LineTo((float)(ox - dw / 2), (float)(oy + dh));
                }
                using var dash = SKPathEffect.CreateDash(new float[] { (float)(L / 12), (float)(L / 12) }, 0);
                Stroke(y, path, cL, Math.Max(0.8, dw * 0.012), 1, SKBlendMode.SrcOver, SKStrokeCap.Butt,
                    SKStrokeJoin.Miter, dash);
            }), dw, dh, 0, 0, w, h));
        var d = t * dh * 0.1 * Num(P, "up", 1);
        Blit(ctx, env, dia, Wrap(dw - W / 2, 2 * dw), Wrap(d - (1 - e) * dh * 0.3, 2 * dh), e);
        Blit(ctx, env, lines, Wrap(dw / 2 - W / 2 - d * 0.6, dw), Wrap(-d, dh), e * 0.9);
    }

    private static void DrawTartan(Env env)
    {
        var ctx = env.Ctx;
        var (W, H) = (env.W, env.H);
        var sc = env.Sc!;
        var P = POf(env);
        var t = env.T;
        var S = Umin(env) * Num(P, "S", 0.42);
        var k = Num(P, "k", 0.1);
        var e = FadeIn(env, 0.8);
        var cA = LayC(sc, k);
        var cB = Flag(P, "acc") ? TintC(sc, k * 1.5) : LayC(sc, k * 0.6);
        var cL = LayC(sc, k * 1.6);
        var cT = TintC(sc, k * 2.4);
        var w1 = Num(P, "w1", 0.22);
        var w2 = Num(P, "w2", 0.1);
        var key = $"tart|{cA}|{cB}|{cL}|{cT}|{F3(w1)}|{F3(w2)}|{S.ToString("0.0", CultureInfo.InvariantCulture)}";
        // 一组镜像的 tartan 条纹：宽带/细线/中带/发丝线
        var sett = new[] { (0.0, w1, cA, 0.6), (w1 + 0.04, 0.012, cL, 0.9), (w1 + 0.1, w2, cB, 0.55), (0.72, 0.008, cT, 0.9), (0.84, 0.02, cL, 0.7) };
        void Stripe(SKCanvas x, double len, double span)
        {
            foreach (var (p, w, c, a) in sett)
            {
                for (var o = 0.0; o < len + S; o += S)
                {
                    FillRect(x, o + p * S, 0, w * S, span, c, a);
                    FillRect(x, o + S - (p + w) * S, 0, w * S, span, c, a);
                }
            }
        }
        var vert = Plate(key + "|v", env, S, 0, (x, w, h) => Stripe(x, w, h));
        var hor = Plate(key + "|h", env, 0, S, (x, w, h) =>
        {
            x.Concat(new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1)); // ctx.transform(0, 1, 1, 0, 0, 0)
            Stripe(x, h, w);
        });
        Blit(ctx, env, vert, Wrap(-t * S * 0.035, S), 0, e);
        Blit(ctx, env, hor, 0, Wrap(t * S * 0.025 + S * 0.37, S), e);
    }

    private static void DrawChevron(Env env)
    {
        var ctx = env.Ctx;
        var (W, H) = (env.W, env.H);
        var sc = env.Sc!;
        var P = POf(env);
        var t = env.T;
        var p = Umin(env) * Num(P, "p", 0.16);
        var D = p * 0.5;
        var A = p * Num(P, "amp", 0.35);
        var e = FadeIn(env, 0.6);
        var col = Flag(P, "acc") ? TintC(sc, Num(P, "k", 0.065) * 1.7) : LayC(sc, Num(P, "k", 0.065));
        var key = $"chev|{col}|{F2(p)}|{F2(A)}";
        var pl = Plate(key, env, p, D, (x, w, h) =>
            FillTile(x, TileCv(key, env, p, D, y =>
            {
                using var path = new SKPath();
                for (var j = -2; j <= 3; j++)
                {
                    var y0 = j * D + A / 2;
                    Poly(path, new[]
                    {
                        new[] { -1.0, y0 }, new[] { p / 2, y0 - A }, new[] { p + 1, y0 },
                        new[] { p + 1, y0 + D / 2 }, new[] { p / 2, y0 - A + D / 2 }, new[] { -1.0, y0 + D / 2 }
                    });
                }
                FillPath(y, path, col);
            }), p, D, 0, 0, w, h));
        Blit(ctx, env, pl, Wrap(p / 2 - W / 2 + Math.Sin(t * 0.3) * p * 0.1, p),
            Wrap(-t * D * 0.4 * Num(P, "dir", 1) + (1 - e) * D, D), e);
    }

    private static void DrawIsoCubes(Env env)
    {
        var ctx = env.Ctx;
        var (W, H) = (env.W, env.H);
        var sc = env.Sc!;
        var P = POf(env);
        var t = env.T;
        var s = Umin(env) * Num(P, "s", 0.065);
        var r3 = Math.Sqrt(3);
        var pw = r3 * s;
        var ph = 3 * s;
        var e = FadeIn(env, 0.6);
        var col = LayC(sc, Num(P, "k", 0.09));
        var faces = new[] { "top", "left", "right" }.Select((f, fi) =>
        {
            var key = $"iso|{f}|{col}|{F2(s)}";
            return Plate(key, env, pw, ph, (x, w, h) =>
                FillTile(x, TileCv(key, env, pw, ph, y =>
                {
                    using var path = new SKPath();
                    for (var n2 = -2; n2 <= 3; n2++)
                    for (var n1 = -3; n1 <= 3; n1++)
                    {
                        var cx = n1 * pw + n2 * pw / 2;
                        var cy = n2 * 1.5 * s;
                        var T = new[] { cx, cy - s };
                        var UR = new[] { cx + pw / 2, cy - s / 2 };
                        var LR = new[] { cx + pw / 2, cy + s / 2 };
                        var B = new[] { cx, cy + s };
                        var LL = new[] { cx - pw / 2, cy + s / 2 };
                        var UL = new[] { cx - pw / 2, cy - s / 2 };
                        var C = new[] { cx, cy };
                        Poly(path, fi == 0 ? new[] { T, UR, C, UL } : fi == 1 ? new[] { UL, C, B, LL } : new[] { UR, LR, B, C });
                    }
                    FillPath(y, path, col);
                }), pw, ph, 0, 0, w, h));
        }).ToArray();
        // 光向慢慢转，三个面的亮度轮流当主
        var L = Num(P, "l0", 0) + t * Num(P, "spd", 0.25);
        var dirs = new[] { -Math.PI / 2, Math.PI * 5 / 6, Math.PI / 6 };
        var ox = Wrap(t * s * 0.25, pw);
        var oy = Wrap(t * s * 0.25 / r3 * 1.5, ph);
        for (var fi = 0; fi < faces.Length; fi++)
            Blit(ctx, env, faces[fi], ox, oy, e * (0.25 + 0.75 * (0.5 + 0.5 * Math.Cos(L - dirs[fi]))));
    }

    private static void DrawHexGrid(Env env)
    {
        var ctx = env.Ctx;
        var (W, H) = (env.W, env.H);
        var sc = env.Sc!;
        var P = POf(env);
        var t = env.T;
        var sd = (int)Num(P, "seed", 1);
        var U = Umin(env);
        var s = U * Num(P, "s", 0.06);
        var r3 = Math.Sqrt(3);
        var e = FadeIn(env, 0.7);
        var cols = (int)Math.Ceiling(W / (r3 * s)) + 2;
        var rows = (int)Math.Ceiling(H / (1.5 * s)) + 2;
        var rr = s * 0.9;
        var cx0 = W / 2.0;
        var cy0 = H / 2.0;
        var R = Hyp(W, H) / 2;
        using var lines = new SKPath();
        using var lit0 = new SKPath();
        using var lit1 = new SKPath();
        using var lit2 = new SKPath();
        var lit = new[] { lit0, lit1, lit2 };
        void Hex(SKPath p, double x, double y, double r)
        {
            for (var m = 0; m < 6; m++)
            {
                var a = (m * 60 - 30) * J.DEG;
                var px = x + Math.Cos(a) * r;
                var py = y + Math.Sin(a) * r;
                if (m != 0) p.LineTo((float)px, (float)py);
                else p.MoveTo((float)px, (float)py);
            }
            p.Close();
        }
        var tw = (int)Math.Floor(t * 3);
        var mode = Str(P, "mode", "ring");
        for (var j = -1; j < rows; j++)
        for (var i = -1; i < cols; i++)
        {
            var x = (i + ((j & 1) != 0 ? 0.5 : 0)) * r3 * s;
            var y = j * 1.5 * s;
            var d = Hyp(x - cx0, y - cy0) / R;
            if (d > e * 1.2) continue;
            Hex(lines, x, y, rr);
            double v;
            if (mode == "sparkle") v = J.R(sd, i, j, tw) < 0.05 ? 1 - Fract(t * 3) * 0.6 : 0;
            else v = Math.Pow(0.5 + 0.5 * Math.Cos((d * 3.2 - t * 0.55) * J.TAU), 6) * (0.6 + 0.4 * J.R(sd, i, j));
            if (v > 0.2) Hex(lit[v > 0.75 ? 2 : v > 0.45 ? 1 : 0], x, y, rr * 0.86);
        }
        var k = Num(P, "k", 0.1);
        Stroke(ctx, lines, LayC(sc, k), Math.Max(1, U * 0.0022));
        var ms = new[] { 0.5, 1.0, 1.6 };
        for (var l = 0; l < 3; l++) FillPath(ctx, lit[l], TintC(sc, k * ms[l]));
    }

    private static void DrawTriTess(Env env)
    {
        var ctx = env.Ctx;
        var (W, H) = (env.W, env.H);
        var sc = env.Sc!;
        var P = POf(env);
        var t = env.T;
        var sd = (int)Num(P, "seed", 1);
        var U = Umin(env);
        var a = U * Num(P, "a", 0.12);
        var h = a * Math.Sqrt(3) / 2;
        var e = FadeIn(env, 0.8);
        using var p0 = new SKPath();
        using var p1 = new SKPath();
        using var p2 = new SKPath();
        using var p3 = new SKPath();
        var paths = new[] { p0, p1, p2, p3 };
        var cols = (int)Math.Ceiling(W / a) + 2;
        var rows = (int)Math.Ceiling(H / h) + 1;
        var sweep = (Wrap(t * 0.07, 1.6) - 0.3) * (W + H);
        for (var r = 0; r < rows; r++)
        for (var i = -1; i < cols; i++)
        {
            var y0 = r * h;
            var y1 = y0 + h;
            var off = (r & 1) != 0 ? a / 2 : 0;
            for (var up = 0; up < 2; up++)
            {
                var x0 = i * a + off + (up != 0 ? a / 2 : 0);
                var tri = up != 0
                    ? new[] { new[] { x0, y1 }, new[] { x0 + a, y1 }, new[] { x0 + a / 2, y0 } }
                    : new[] { new[] { x0, y0 }, new[] { x0 + a, y0 }, new[] { x0 + a / 2, y1 } };
                var gx = x0 + a / 2;
                var gy = up != 0 ? y0 + h * 0.66 : y0 + h * 0.33;
                var v = Fbm2(gx / U * 1.6 + t * 0.12, gy / U * 1.6 - t * 0.07, sd, 2);
                v += 0.35 * Math.Exp(-Math.Pow((gx + gy - sweep) / (U * 0.35), 2));
                v = v * e + (J.R(sd, r, i, up) - 0.5) * 0.12;
                var lv = v > 0.78 ? 3 : v > 0.62 ? 2 : v > 0.46 ? 1 : v > 0.3 ? 0 : -1;
                if (lv < 0) continue;
                Poly(paths[lv], tri);
            }
        }
        var k = Num(P, "k", 0.085);
        var acc = Flag(P, "acc"); // JS: cf = P.acc ? tintC : layC
        var ms = new[] { 0.35, 0.65, 1.0, 1.45 };
        for (var l = 0; l < 4; l++)
            FillPath(ctx, paths[l], l == 3 && acc ? TintC(sc, k * ms[l] * 1.4)
                : acc ? TintC(sc, k * ms[l]) : LayC(sc, k * ms[l]));
    }

    private static void DrawMoire(Env env)
    {
        var ctx = env.Ctx;
        var (W, H) = (env.W, env.H);
        var sc = env.Sc!;
        var P = POf(env);
        var t = env.T;
        var U = Umin(env);
        var gap = U * Num(P, "gap", 0.018);
        var amp = U * Num(P, "amp", 0.07);
        var e = FadeIn(env, 0.8);
        var s = (int)Num(P, "seed", 1) % 17;
        var col = LayC(sc, Num(P, "k", 0.1));
        var M = amp * 2.2;
        // 一组同心环只渲染一次并留边，两个干涉副本是同一块底板的两次 blit
        var pl = Plate($"moi|{col}|{F2(gap)}|{M.ToString("0.0", CultureInfo.InvariantCulture)}", env, M * 2, M * 2, (x, w, h) =>
        {
            var cx = w / 2;
            var cy = h / 2;
            var Rm = Hyp(w, h) / 2;
            using var rings = new SKPath();
            for (var r = gap; r < Rm; r += gap) AddCircle(rings, cx, cy, r);
            Stroke(x, rings, col, gap * 0.42);
        });
        var cs = new[]
        {
            (amp * Math.Sin(t * 0.33 + s), amp * 0.7 * Math.Cos(t * 0.27 + s)),
            (-amp * Math.Sin(t * 0.29 + s + 1), -amp * 0.7 * Math.Cos(t * 0.37 + s + 2))
        };
        foreach (var (dx, dy) in cs) Blit(ctx, env, pl, M - dx, M - dy, e);
    }

    private static void DrawSquareTunnel(Env env)
    {
        var ctx = env.Ctx;
        var (W, H) = (env.W, env.H);
        var sc = env.Sc!;
        var P = POf(env);
        var t = env.T;
        var U = Umin(env);
        var r = Num(P, "r", 1.26);
        var lr = Math.Log(r);
        var e = FadeIn(env, 0.5);
        var z = t * Num(P, "spd", 0.45) + (1 - e) * 1.5;
        var S0 = U * 0.08;
        var maxS = Hyp(W, H) * 1.05;
        // 第 n 个方块的半边长是 S0 * r^(z - n)，z 前进即向前飞
        var nLo = (int)Math.Floor(z - Math.Log(maxS / S0) / lr) - 2;
        nLo -= ((nLo % 2) + 2) % 2; // 固定最外层方块的奇偶，避免闪变
        var nHi = (int)Math.Ceiling(z - Math.Log(U * 0.035 / S0) / lr);
        ctx.Translate((float)(W / 2.0), (float)(H / 2.0));
        using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
        for (var n = nLo; n <= nHi && n - nLo < 60; n++)
        {
            var hs = S0 * Math.Pow(r, z - n);
            var a = ((z - n) * Num(P, "twist", 5) + t * 4) * J.DEG;
            var c = Math.Cos(a) * hs;
            var s2 = Math.Sin(a) * hs;
            path.MoveTo((float)(c - s2), (float)(s2 + c));
            path.LineTo((float)(-c - s2), (float)(-s2 + c));
            path.LineTo((float)(-c + s2), (float)(-s2 - c));
            path.LineTo((float)(c + s2), (float)(s2 - c));
            path.Close();
        }
        FillPath(ctx, path, LayC(sc, Num(P, "k", 0.06)));
    }

    private static void DrawSpiralArms(Env env)
    {
        var ctx = env.Ctx;
        var (W, H) = (env.W, env.H);
        var sc = env.Sc!;
        var P = POf(env);
        var t = env.T;
        var U = Umin(env);
        var n = (int)Num(P, "n", 4);
        var b = Num(P, "b", 0.32);
        var e = FadeIn(env, 0.7);
        var cx = W / 2.0;
        var cy = H * Num(P, "cy", 0.5);
        var r0 = U * 0.09;
        var Rm = Hyp(W, H) * 0.62;
        var thMax = Math.Log(Rm / r0) / b;
        var w = Math.PI / n;
        const int steps = 72;
        var rot = t * Num(P, "spd", 0.18) + (1 - e) * 1.2 * Math.Sign(Num(P, "spd", 1));
        using var path = new SKPath();
        for (var k = 0; k < n; k++)
        {
            var @base = rot + k * J.TAU / n;
            for (var i = 0; i <= steps; i++)
            {
                var th = thMax * i / (double)steps;
                var r = r0 * Math.Exp(b * th) * (0.4 + 0.6 * e);
                var a = @base + th;
                var px = cx + Math.Cos(a) * r;
                var py = cy + Math.Sin(a) * r;
                if (i != 0) path.LineTo((float)px, (float)py);
                else path.MoveTo((float)px, (float)py);
            }
            for (var i = steps; i >= 0; i--)
            {
                var th = thMax * i / (double)steps;
                var r = r0 * Math.Exp(b * th) * (0.4 + 0.6 * e);
                var a = @base + th + w;
                path.LineTo((float)(cx + Math.Cos(a) * r), (float)(cy + Math.Sin(a) * r));
            }
            path.Close();
        }
        FillPath(ctx, path, LayC(sc, Num(P, "k", 0.065)));
        using var g = Rad(cx, cy, U * 0.42, Stops((0, Rgba(Bg(sc), 0.8)), (1, Rgba(Bg(sc), 0))));
        FillRect(ctx, cx - U * 0.42, cy - U * 0.42, U * 0.84, U * 0.84, g);
    }

    // marching squares：16 种角点情形的线段端点（边号 0 上 1 右 2 下 3 左）
    private static readonly int[][][] MS =
    {
        Array.Empty<int[]>(),
        new[] { new[] { 3, 2 } },
        new[] { new[] { 2, 1 } },
        new[] { new[] { 3, 1 } },
        new[] { new[] { 0, 1 } },
        new[] { new[] { 0, 1 }, new[] { 3, 2 } },
        new[] { new[] { 0, 2 } },
        new[] { new[] { 3, 0 } },
        new[] { new[] { 3, 0 } },
        new[] { new[] { 0, 2 } },
        new[] { new[] { 3, 0 }, new[] { 2, 1 } },
        new[] { new[] { 0, 1 } },
        new[] { new[] { 3, 1 } },
        new[] { new[] { 2, 1 } },
        new[] { new[] { 3, 2 } },
        Array.Empty<int[]>(),
    };

    private static void DrawTopoLines(Env env)
    {
        var ctx = env.Ctx;
        var (W, H) = (env.W, env.H);
        var sc = env.Sc!;
        var P = POf(env);
        var t = env.T;
        var sd = (int)Num(P, "seed", 1);
        var U = Umin(env);
        var cell = U / 24.0;
        var e = FadeIn(env, 0.9);
        var nx = (int)Math.Ceiling(W / cell) + 1;
        var ny = (int)Math.Ceiling(H / cell) + 1;
        var fs = Num(P, "fs", 1.4) / U;
        var f = new float[nx * ny];
        for (var j = 0; j < ny; j++)
        for (var i = 0; i < nx; i++)
        {
            var X = i * cell * fs;
            var Y = j * cell * fs;
            var rx = X * 0.8 - Y * 0.6;
            var ry = X * 0.6 + Y * 0.8;
            var v = Fbm2(rx + t * 0.035 + 0.35 * Noise2(ry * 0.7, rx * 0.7, sd + 5), ry - t * 0.025, sd, 3);
            f[j * nx + i] = (float)J.Clamp((v - 0.5) * 2.1 + 0.5, -0.2, 1.2);
        }
        var n = (int)Num(P, "n", 12);
        var d = 1 / (double)n;
        var z = t * 0.06 + (1 - e) * 2;
        using var lines = new SKPath();
        using var major = new SKPath();
        for (var m = -1; m <= n + 1; m++)
        {
            var L = (m + Fract(z)) * d;
            var g = m - Math.Floor(z);
            var p = ((g % 4) + 4) % 4 == 0 ? major : lines;
            for (var j = 0; j < ny - 1; j++)
            for (var i = 0; i < nx - 1; i++)
            {
                var a = f[j * nx + i];
                var b = f[j * nx + i + 1];
                var c = f[(j + 1) * nx + i + 1];
                var dd = f[(j + 1) * nx + i];
                var idx = (a > L ? 8 : 0) | (b > L ? 4 : 0) | (c > L ? 2 : 0) | (dd > L ? 1 : 0);
                if (idx == 0 || idx == 15) continue;
                double[] Pt(int ed) => ed == 0
                    ? new[] { i + (L - a) / (b - a), (double)j }
                    : ed == 1
                        ? new[] { (double)(i + 1), j + (L - b) / (c - b) }
                        : ed == 2
                            ? new[] { i + (L - dd) / (c - dd), (double)(j + 1) }
                            : new[] { (double)i, j + (L - a) / (dd - a) };
                foreach (var seg in MS[idx])
                {
                    var q0 = Pt(seg[0]);
                    var q1 = Pt(seg[1]);
                    p.MoveTo((float)(q0[0] * cell), (float)(q0[1] * cell));
                    p.LineTo((float)(q1[0] * cell), (float)(q1[1] * cell));
                }
            }
        }
        var k = Num(P, "k", 0.13);
        Stroke(ctx, lines, LayC(sc, k), Math.Max(1, U * 0.0018), e, SKBlendMode.SrcOver, SKStrokeCap.Round, SKStrokeJoin.Round);
        Stroke(ctx, major, Flag(P, "acc") ? TintC(sc, k * 2.2) : LayC(sc, k * 1.6), Math.Max(1.5, U * 0.0036), e,
            SKBlendMode.SrcOver, SKStrokeCap.Round, SKStrokeJoin.Round);
    }

    private static void DrawRidgePlot(Env env)
    {
        var ctx = env.Ctx;
        var (W, H) = (env.W, env.H);
        var sc = env.Sc!;
        var P = POf(env);
        var t = env.T;
        var sd = (int)Num(P, "seed", 1);
        var U = Umin(env);
        var n = (int)Num(P, "n", 22);
        var e = FadeIn(env, 1);
        var x0 = W * 0.06;
        var x1 = W * 0.94;
        var y0 = H * 0.2;
        var y1 = H * 0.94;
        var gap = (y1 - y0) / (n - 1);
        var m = J.Clamp((int)Round((x1 - x0) / (U * 0.012)), 60, 170);
        var amp = H * Num(P, "amp", 0.085) * (W < H ? 0.6 : 1);
        var lc = LayC(sc, Num(P, "k", 0.17));
        var paper = sc.Get("paper") is JValue { Type: JTokenType.Boolean, Value: true }
                    || (env.St.Extra != null && env.St.Extra.TryGetValue("texture", out var tex)
                        && tex is JObject to && to["paper"] is JValue tv
                        && (tv.Type is JTokenType.Integer or JTokenType.Float) && Convert.ToDouble(tv.Value) > 0.5);
        var fillC = Rgba(Bg(sc), paper ? 0.72 : 0.9);
        var mode = Str(P, "mode", "center");
        for (var i = 0; i < n; i++)
        {
            var @base = y0 + i * gap;
            var grow = J.E.OutCubic(J.Clamp(e * 1.6 - i / (double)n * 0.6));
            var pts = new double[m + 1][];
            for (var j = 0; j <= m; j++)
            {
                var u = j / (double)m;
                var x = J.Lerp(x0, x1, u);
                var env1 = mode == "wide"
                    ? 0.3 + 0.7 * Math.Pow(Math.Sin(Math.PI * u), 2)
                    : 0.08 + 0.92 * Math.Exp(-Math.Pow((u - 0.5) / 0.16, 2));
                var v = Math.Pow(0.5 + 0.5 * Fbm1(u * 7 + t * Num(P, "spd", 0.24) + i * 0.41, sd + i * 7, 3), 2.4);
                pts[j] = new[] { x, @base - amp * env1 * v * 2.2 * grow - H * 0.002 * J.Noise1(u * 40 + i, sd) };
            }
            using (var fill = new SKPath())
            {
                fill.MoveTo((float)x0, (float)(@base + 1));
                foreach (var q in pts) fill.LineTo((float)q[0], (float)q[1]);
                fill.LineTo((float)x1, (float)(@base + 1));
                fill.Close();
                FillPath(ctx, fill, fillC);
            }
            using (var line = new SKPath())
            {
                line.MoveTo((float)pts[0][0], (float)pts[0][1]);
                foreach (var q in pts) line.LineTo((float)q[0], (float)q[1]);
                Stroke(ctx, line, lc, Math.Max(1, U * 0.0022), 1, SKBlendMode.SrcOver, SKStrokeCap.Butt, SKStrokeJoin.Round);
            }
        }
    }

    private static void DrawStarfield(Env env)
    {
        var ctx = env.Ctx;
        var (W, H) = (env.W, env.H);
        var sc = env.Sc!;
        var P = POf(env);
        var t = env.T;
        var sd = (int)Num(P, "seed", 1);
        var U = Umin(env);
        var dk = IsDark(Bg(sc));
        var e = FadeIn(env, 0.9);
        var col = dk ? J.Mix(Bg(sc), Fg(sc), 0.92) : J.Lum(Bg(sc)) < 0.78 ? "#FFFFFF" : LayC(sc, 0.5);
        var area = Math.Sqrt(W * (double)H / (1920 * 1080));
        var vx = Math.Cos(Num(P, "ang", 0));
        var vy = Math.Sin(Num(P, "ang", 0));
        var spd = Num(P, "spd", 1);
        var layers = new[] { (90, 0.0013, 0.006, 0.45), (46, 0.002, 0.018, 0.65), (18, 0.0032, 0.045, 0.85) };
        for (var l = 0; l < layers.Length; l++)
        {
            var (cnt, rad, sp, aB) = layers[l];
            var n = (int)Round(cnt * area);
            var v = U * sp * spd;
            for (var i = 0; i < n; i++)
            {
                var x = Wrap(J.R(sd, l, i, 1) * W * 1.1 + t * v * vx, W * 1.1) - W * 0.05;
                var y = Wrap(J.R(sd, l, i, 2) * H * 1.1 + t * v * vy, H * 1.1) - H * 0.05;
                var r = U * rad * (0.6 + 0.8 * J.R(sd, l, i, 3));
                var tw = 0.5 + 0.5 * Math.Sin(t * (1.1 + 2.6 * J.R(sd, l, i, 4)) + i * 1.7);
                var a = aB * (0.45 + 0.55 * tw) * e * (dk ? 1 : 0.65);
                if (l < 2) FillRect(ctx, x - r, y - r, r * 2, r * 2, col, a);
                else
                {
                    using var dot = new SKPath();
                    AddCircle(dot, x, y, r);
                    FillPath(ctx, dot, col, a);
                    if (J.R(sd, l, i, 5) < 0.5)
                    {
                        var L = r * (4 + 3 * tw);
                        var th = Math.Max(0.6, r * 0.25);
                        FillRect(ctx, x - L, y - th / 2, L * 2, th, col, a * 0.5);
                        FillRect(ctx, x - th / 2, y - L, th, L * 2, col, a * 0.5);
                    }
                }
            }
        }
        if (Flag(P, "shoot") || P["shoot"] == null)
        {
            var per = 3.4;
            var idx = (int)Math.Floor(t / per);
            var age = t - idx * per - J.R(sd, idx, 8) * 1.5;
            if (age > 0 && age < 0.75 && J.R(sd, idx, 9) < 0.85)
            {
                var q = age / 0.75;
                var sx = W * J.Rr(0.15, 0.7, sd, idx, 1);
                var sy = H * J.Rr(0.05, 0.35, sd, idx, 2);
                var ang = J.Rr(20, 38, sd, idx, 3) * J.DEG * (J.R(sd, idx, 4) < 0.5 ? 1 : -1);
                var dx = Math.Cos(ang) * (ang < 0 ? -1 : 1);
                var dy = Math.Abs(Math.Sin(ang));
                var L = U * 0.55;
                var hx = sx + dx * L * J.E.OutQuad(q);
                var hy = sy + dy * L * J.E.OutQuad(q);
                var tl = U * 0.16 * Math.Sin(Math.PI * q);
                using var g = Lin(hx, hy, hx - dx * tl, hy - dy * tl,
                    Stops((0, Rgba(col, 0.8 * e * (1 - q * 0.6))), (1, Rgba(col, 0))));
                using var ln = new SKPath();
                ln.MoveTo((float)hx, (float)hy);
                ln.LineTo((float)(hx - dx * tl), (float)(hy - dy * tl));
                using var p = new SKPaint
                {
                    IsAntialias = true,
                    IsStroke = true,
                    StrokeWidth = (float)Math.Max(1, U * 0.002),
                    StrokeCap = SKStrokeCap.Round,
                    Color = SKColors.White,
                    Shader = g
                };
                ctx.DrawPath(ln, p);
            }
        }
    }

    private static void DrawNightMoon(Env env)
    {
        var ctx = env.Ctx;
        var (W, H) = (env.W, env.H);
        var sc = env.Sc!;
        var P = POf(env);
        var t = env.T;
        var sd = (int)Num(P, "seed", 1);
        var U = Umin(env);
        var dk = IsDark(Bg(sc));
        var e = FadeIn(env, 1.2);
        var side = Num(P, "side", 1);
        var port = H > W;
        var R = U * Num(P, "R", 0.13);
        var mx = W * (0.5 + side * (port ? 0.2 : 0.3));
        var my = H * (port ? 0.2 : 0.27) + (1 - e) * H * 0.06;
        var glow = GlowOf(sc);
        var moonC = dk
            ? J.Mix(Bg(sc), glow == Fg(sc) ? Fg(sc) : J.Mix(Fg(sc), glow, 0.3), Num(P, "k", 0.22))
            : J.Mix(Bg(sc), "#FFFFFF", 0.55);
        var haloC = dk ? moonC : J.Mix(Bg(sc), "#FFFFFF", 0.7);
        // 稀疏的星星，带闪烁
        var nS = (int)Round(28 * Math.Sqrt(W * (double)H / (1920 * 1080)));
        var starCol = dk ? LayC(sc, 0.55) : LayC(sc, 0.2);
        for (var i = 0; i < nS; i++)
        {
            var x = J.R(sd, i, 1) * W;
            var y = J.R(sd, i, 2) * H * 0.62;
            var r = U * J.Rr(0.001, 0.0024, sd, i, 3);
            if (Hyp(x - mx, y - my) < R * 2.2) continue;
            FillRect(ctx, x - r, y - r, r * 2, r * 2, starCol, e * (0.2 + 0.4 * (0.5 + 0.5 * Math.Sin(t * J.Rr(0.8, 2.4, sd, i, 4) + i))));
        }
        var br = 0.9 + 0.1 * Math.Sin(t * 0.7);
        using (var g = Rad2(mx, my, R * 0.9, R * 3.4, Stops(
                   (0, Rgba(haloC, (dk ? 0.35 : 0.5) * br)), (0.3, Rgba(haloC, (dk ? 0.1 : 0.18) * br)), (1, Rgba(haloC, 0))))
               )
            FillRect(ctx, mx - R * 3.4, my - R * 3.4, R * 6.8, R * 6.8, g, e);
        ctx.Save();
        if (Str(P, "phase", "full") == "crescent")
        {
            using var clip = new SKPath { FillType = SKPathFillType.EvenOdd };
            clip.AddRect(new SKRect((float)(mx - R * 2), (float)(my - R * 2), (float)(mx + R * 2), (float)(my + R * 2)));
            clip.AddCircle((float)(mx + side * R * 0.42), (float)(my - R * 0.22), (float)(R * 0.9));
            ctx.ClipPath(clip, SKClipOperation.Intersect, true);
        }
        using (var moon = new SKPath())
        {
            AddCircle(moon, mx, my, R);
            FillPath(ctx, moon, moonC, e);
        }
        if (Str(P, "phase", "full") != "crescent")
        {
            using var craters = new SKPath();
            for (var i = 0; i < 6; i++)
            {
                var a = J.R(sd, i, 11) * J.TAU;
                var d = R * Math.Sqrt(J.R(sd, i, 12)) * 0.7;
                var cr = R * J.Rr(0.08, 0.2, sd, i, 13);
                AddCircle(craters, mx + Math.Cos(a) * d, my + Math.Sin(a) * d, cr);
            }
            FillPath(ctx, craters, J.Mix(moonC, Bg(sc), 0.22), e);
        }
        ctx.Restore();
        // 横掠月亮的薄云条
        var cloudC = J.Mix(Bg(sc), Fg(sc), dk ? 0.07 : 0.05);
        void Cap(SKPath p, double x, double y, double w, double h)
        {
            p.MoveTo((float)(x + h / 2), (float)y);
            p.LineTo((float)(x + w - h / 2), (float)y);
            p.ArcTo(new SKRect((float)(x + w - h), (float)y, (float)(x + w), (float)(y + h)), -90, 180, false);
            p.LineTo((float)(x + h / 2), (float)(y + h));
            p.ArcTo(new SKRect((float)x, (float)y, (float)(x + h), (float)(y + h)), 90, 180, false);
            p.Close();
        }
        for (var i = 0; i < 3; i++)
        {
            var w = U * J.Rr(0.4, 0.7, sd, i, 21);
            var h = U * J.Rr(0.02, 0.03, sd, i, 22);
            var L = W + w * 2;
            var x = Wrap((i + J.R(sd, i, 23) * 0.5) / 3.0 * L + t * U * J.Rr(0.025, 0.045, sd, i, 24), L) - w;
            var y = my + R * (i - 1) * 0.8 + R * J.Rr(-0.2, 0.3, sd, i, 25);
            using var cloud = new SKPath();
            Cap(cloud, x, y, w, h);
            Cap(cloud, x + w * J.Rr(0.15, 0.4, sd, i, 26), y - h * 0.75, w * 0.45, h * 0.9);
            Cap(cloud, x + w * J.Rr(0.35, 0.6, sd, i, 27), y + h * 0.7, w * 0.5, h * 0.8);
            FillPath(ctx, cloud, cloudC, e * 0.7);
        }
    }

    private static void DrawSkyline(Env env)
    {
        var ctx = env.Ctx;
        var (W, H) = (env.W, env.H);
        var sc = env.Sc!;
        var P = POf(env);
        var t = env.T;
        var sd = (int)Num(P, "seed", 1);
        var U = Umin(env);
        var dk = IsDark(Bg(sc));
        var e = FadeIn(env, 0.9);
        var k = Num(P, "k", 0.09);
        var Lp = Math.Max(W, H) * 1.5;
        var rise = (1 - e) * H * 0.25;
        // 城市背后的地平线光晕
        var gc = dk ? GlowOf(sc) : Ac(sc);
        using (var g = Lin(0, H * 0.5, 0, H, Stops((0, Rgba(gc, 0)), (1, Rgba(gc, (dk ? 0.1 : 0.08) * e)))))
            FillRect(ctx, 0, H * 0.5, W, H * 0.5, g);
        var winLit = dk ? J.Mix(Bg(sc), GlowOf(sc), 0.4) : J.Mix(LayC(sc, k * 1.1), "#FFFFFF", 0.55);
        var layers = new[] { (0.6, 0.12, 0.34, 0.012), (1.1, 0.06, 0.2, 0.03) };
        for (var l = 0; l < layers.Length; l++)
        {
            var (km, h0, h1, v) = layers[l];
            var col = LayC(sc, k * km);
            var off = Wrap(t * U * v * Num(P, "dir", 1), Lp);
            using var body = new SKPath();
            using var wins = new SKPath();
            var x = 0.0;
            var b = 0;
            while (x < Lp && b < 80)
            {
                var w = U * J.Rr(0.05, 0.12, sd, l, b, 1);
                var gapW = U * J.Rr(0, 0.012, sd, l, b, 2);
                var h = H * J.Rr(h0, h1, sd, l, b, 3) * (W < H ? 0.75 : 1);
                var tier = J.R(sd, l, b, 4) < 0.35;
                var ant = J.R(sd, l, b, 5) < 0.25;
                foreach (var X0 in new[] { x - off, x - off + Lp })
                {
                    if (X0 > W || X0 + w < 0) continue;
                    var top = H - h + rise;
                    body.AddRect(new SKRect((float)X0, (float)top, (float)(X0 + w), (float)(top + h + 2)));
                    if (tier)
                        body.AddRect(new SKRect((float)(X0 + w * 0.2), (float)(top - h * 0.12), (float)(X0 + w * 0.2 + w * 0.6), (float)(top - h * 0.12 + h * 0.12 + 1)));
                    if (ant)
                        body.AddRect(new SKRect((float)(X0 + w * 0.5 - 1), (float)(top - h * (tier ? 0.32 : 0.2)), (float)(X0 + w * 0.5 - 1 + Math.Max(1.5, U * 0.002)), (float)(top - h * (tier ? 0.32 : 0.2) + h * 0.2)));
                    var cw = U * 0.022;
                    var ch = U * 0.03;
                    var nx = (int)Math.Floor((w - cw * 0.4) / cw);
                    var ny = (int)Math.Floor((h - ch) / ch);
                    for (var wy = 0; wy < ny && wy < 30; wy++)
                    for (var wx = 0; wx < nx; wx++)
                    {
                        var ph = (int)Math.Floor(t * 0.25 + J.R(sd, l, b, wx, wy) * 7);
                        if (J.R(sd + ph, l * 97 + b, wx, wy) > Num(P, "win", 0.28)) continue;
                        wins.AddRect(new SKRect(
                            (float)(X0 + (w - nx * cw) / 2 + wx * cw + cw * 0.3),
                            (float)(top + ch * 0.7 + wy * ch),
                            (float)(X0 + (w - nx * cw) / 2 + wx * cw + cw * 0.3 + cw * 0.4),
                            (float)(top + ch * 0.7 + wy * ch + ch * 0.45)));
                    }
                }
                x += w + gapW;
                b++;
            }
            FillPath(ctx, body, col, 1);
            FillPath(ctx, wins, winLit, l != 0 ? 0.8 : 0.5);
        }
    }

    private static void DrawSunsetSun(Env env)
    {
        var ctx = env.Ctx;
        var (W, H) = (env.W, env.H);
        var sc = env.Sc!;
        var P = POf(env);
        var t = env.T;
        var sd = (int)Num(P, "seed", 1);
        var U = Umin(env);
        var dk = IsDark(Bg(sc));
        var e = FadeIn(env, 1.2);
        var k = Num(P, "k", 0.35);
        var hz = H * Num(P, "hz", 0.72) * (W < H ? 0.97 : 1);
        var R = U * Num(P, "R", 0.14);
        var cx = W * (W < H ? 0.5 + (Num(P, "cx", 0.3) - 0.5) * 0.6 : Num(P, "cx", 0.3));
        var bt = BgT(env);
        var cy = hz - R * 0.5 + R * 0.35 * J.Clamp(bt / 14) + (1 - e) * R * 0.9;
        var sunBase = dk ? GlowOf(sc) : Ac(sc);
        var sunC = J.Mix(Bg(sc), sunBase, k * (dk ? 1 : 0.7));
        using (var sky = Lin(0, hz - H * 0.5, 0, hz, Stops((0, Rgba(sunBase, 0)), (1, Rgba(sunBase, (dk ? 0.12 : 0.1) * e)))))
            FillRect(ctx, 0, hz - H * 0.5, W, H * 0.5, sky);
        ctx.Save();
        ctx.ClipRect(SKRect.Create(0, 0, W, (float)hz));
        using (var halo = Rad2(cx, cy, R, R * 3, Stops((0, Rgba(sunC, 0.45 * e)), (1, Rgba(sunC, 0)))))
            FillRect(ctx, cx - R * 3, cy - R * 3, R * 6, R * 6, halo);
        using (var sun = new SKPath())
        {
            AddCircle(sun, cx, cy, R);
            FillPath(ctx, sun, sunC, e);
        }
        ctx.Restore();
        FillRect(ctx, 0, hz - 0.5, W, Math.Max(1, U * 0.0016), LayC(sc, 0.14), e);
        // 水面：太阳下的碎光倒影，加几道淡淡的涌浪线
        for (var j = 0; j < 18; j++)
        {
            var y = hz + U * 0.012 * Math.Pow(j + 1, 1.3);
            if (y > H) break;
            var q = j / 18.0;
            var hh = Math.Max(1.2, U * 0.0035 * (1 + j * 0.1));
            var wv = R * (1.25 - q * 0.6) * (0.55 + 0.45 * J.Noise1(t * 1.3 + j * 1.9, sd));
            var xo = R * 0.18 * J.Noise1(t * 0.9 + j * 2.7, sd + 3);
            var split = 0.2 + 0.15 * J.Noise1(t * 1.7 + j * 3.3, sd + 9);
            FillRect(ctx, cx + xo - wv, y, wv * (1 - split), hh, sunC, e * 0.75 * (1 - q) * (0.6 + 0.4 * J.Noise1(t * 2 + j, sd + 5)));
            FillRect(ctx, cx + xo - wv + wv * (1 + split), y, wv * (1 - split), hh, sunC, e * 0.75 * (1 - q) * (0.6 + 0.4 * J.Noise1(t * 2 + j, sd + 5)));
        }
        for (var j = 0; j < 7; j++)
        {
            var y = hz + (H - hz) * (0.12 + j * 0.13);
            var x = Wrap(t * U * 0.03 * (j % 2 != 0 ? 1 : -1) + J.R(sd, j, 31) * W, W * 1.4) - W * 0.2;
            FillRect(ctx, x, y, U * J.Rr(0.15, 0.4, sd, j, 32), Math.Max(1, U * 0.0018), LayC(sc, 0.08), e * 0.8);
        }
    }

    private static void DrawOceanWaves(Env env)
    {
        var ctx = env.Ctx;
        var (W, H) = (env.W, env.H);
        var sc = env.Sc!;
        var P = POf(env);
        var t = env.T;
        var sd = (int)Num(P, "seed", 1);
        var U = Umin(env);
        var n = (int)Num(P, "n", 11);
        var hz = Num(P, "hz", 0.52);
        var e = FadeIn(env, 0.9);
        var dir = Num(P, "dir", 1);
        var m = J.Clamp((int)Round(W / (U * 0.012)), 60, 200);
        for (var i = 0; i < n; i++)
        {
            var q = (i + 1) / (double)n;
            var y0 = H * (hz + (1 - hz) * Math.Pow(q, 1.55)) + (1 - e) * H * 0.15 * q;
            var A = U * (0.004 + 0.028 * Math.Pow(q, 1.4));
            var lam = W * (0.07 + 0.3 * q);
            var w = 0.9 + 0.5 * J.R(sd, i, 1);
            var ph = J.R(sd, i, 2) * J.TAU;
            using var path = new SKPath();
            for (var j = 0; j <= m; j++)
            {
                var x = W * j / (double)m;
                var u = x / lam * J.TAU;
                var y = y0 + A * (Math.Sin(u - t * w * dir + ph)
                                  + 0.35 * Math.Sin(u * 2.3 + t * w * 1.3 * dir + ph * 2)
                                  + 0.2 * J.Noise1(x / U * 3 + t * 0.4, sd + i));
                if (j != 0) path.LineTo((float)x, (float)y);
                else path.MoveTo((float)x, (float)y);
            }
            Stroke(ctx, path, LayC(sc, Num(P, "k", 0.14) * (0.45 + 0.75 * q)),
                Math.Max(1, U * (0.0014 + 0.0035 * q)), e, SKBlendMode.SrcOver, SKStrokeCap.Round, SKStrokeJoin.Round);
        }
    }

    private static void DrawRainWindow(Env env)
    {
        var ctx = env.Ctx;
        var (W, H) = (env.W, env.H);
        var sc = env.Sc!;
        var P = POf(env);
        var t = env.T;
        var sd = (int)Num(P, "seed", 1);
        var U = Umin(env);
        var e = FadeIn(env, 0.6);
        var k = Num(P, "k", 0.15);
        var dk = IsDark(Bg(sc));
        var area = Math.Sqrt(W * (double)H / (1920 * 1080));
        var n = (int)Round(Num(P, "n", 70) * area);
        var tn = Math.Tan(Num(P, "ang", 8) * J.DEG);
        var span = W + H * Math.Abs(tn);
        using (var rain = new SKPath())
        {
            for (var i = 0; i < n; i++)
            {
                var v = H * J.Rr(1.3, 2.1, sd, i, 1);
                var L = H * J.Rr(0.03, 0.09, sd, i, 2);
                var y = Wrap(J.R(sd, i, 4) * (H + L) + t * v, H + L) - L;
                var x = J.R(sd, i, 3) * span - (tn > 0 ? H * tn : 0) + y * tn;
                rain.MoveTo((float)x, (float)y);
                rain.LineTo((float)(x + L * tn), (float)(y + L));
            }
            Stroke(ctx, rain, LayC(sc, k), Math.Max(1, U * 0.0016), e * 0.85, SKBlendMode.SrcOver, SKStrokeCap.Round);
        }
        using (var fog = Lin(0, H * 0.62, 0, H, Stops((0, Rgba(Fg(sc), 0)), (1, Rgba(Fg(sc), 0.05 * e)))))
            FillRect(ctx, 0, H * 0.62, W, H * 0.38, fog);
        // 玻璃上的水滴：先停在原地长大，再往下滑并拖出尾迹
        var dc = LayC(sc, k * 1.5);
        var hc = dk ? LayC(sc, k * 3.5) : J.Mix(Bg(sc), "#FFFFFF", 0.7);
        var drops = (int)Num(P, "drops", 12);
        for (var i = 0; i < drops; i++)
        {
            var per = J.Rr(3.5, 6.5, sd, i, 11);
            var u = Wrap(t + J.R(sd, i, 12) * per, per) / per;
            const double u0 = 0.6;
            var cyc = (int)Math.Floor((t + J.R(sd, i, 12) * per) / per);
            var x0 = J.Rr(0.03, 0.97, sd, i, cyc, 13) * W;
            var y0 = J.Rr(0.04, 0.7, sd, i, cyc, 14) * H;
            var r = U * J.Rr(0.008, 0.019, sd, i, 15);
            var x = x0;
            var y = y0;
            var a = e * J.Clamp(u / 0.08);
            if (u < u0) r *= 0.65 + 0.35 * u / u0;
            else
            {
                var q = (u - u0) / (1 - u0);
                y = y0 + J.E.InQuad(q) * H * 1.15;
                x = x0 + U * 0.006 * Math.Sin(q * 18 + i);
                using var trail = new SKPath();
                trail.MoveTo((float)x0, (float)y0);
                trail.QuadTo((float)(x0 + U * 0.004 * Math.Sin(i)), (float)((y0 + y) / 2), (float)x, (float)(y - r * 0.8));
                Stroke(ctx, trail, dc, r * 0.45, e * 0.55 * (1 - q * 0.5));
            }
            if (y - r > H) continue;
            using (var drop = new SKPath())
            {
                drop.AddOval(new SKRect((float)(x - r * 0.9), (float)(y - r), (float)(x + r * 0.9), (float)(y + r)));
                FillPath(ctx, drop, dc, a * 0.9);
            }
            using (var hi = new SKPath())
            {
                AddCircle(hi, x - r * 0.3, y - r * 0.35, r * 0.28);
                FillPath(ctx, hi, hc, a * 0.7);
            }
        }
    }

    private static SKImage SoftDot(string col) => Cached("dot|" + col, () =>
    {
        using var surf = SKSurface.Create(new SKImageInfo(64, 64));
        using (var p = new SKPaint
               {
                   IsAntialias = true,
                   Shader = Rad(32, 32, 32, Stops((0, Rgba(col, 1)), (0.45, Rgba(col, 0.75)), (1, Rgba(col, 0))))
               })
            surf.Canvas.DrawRect(0, 0, 64, 64, p);
        return surf.Snapshot();
    });

    private static void DrawSnowLayers(Env env)
    {
        var ctx = env.Ctx;
        var (W, H) = (env.W, env.H);
        var sc = env.Sc!;
        var P = POf(env);
        var t = env.T;
        var sd = (int)Num(P, "seed", 1);
        var U = Umin(env);
        var e = FadeIn(env, 1);
        var col = J.Lum(Bg(sc)) < 0.88 ? J.Mix(Bg(sc), "#FFFFFF", 0.92) : LayC(sc, 0.35);
        var spr = SoftDot(col);
        var area = Math.Sqrt(W * (double)H / (1920 * 1080)) * Num(P, "dens", 1);
        var layers = new[] { (70, 0.0045, 0.05, 0.012, 0.5), (36, 0.009, 0.09, 0.02, 0.6), (11, 0.02, 0.16, 0.035, 0.32) };
        for (var l = 0; l < layers.Length; l++)
        {
            var (cnt, sz, v, sw, aB) = layers[l];
            var n = (int)Round(cnt * area);
            var vy = H * v;
            var vx = vy * Num(P, "wind", 0);
            for (var i = 0; i < n; i++)
            {
                var s = U * sz * (0.7 + 0.6 * J.R(sd, l, i, 1));
                var f = J.Rr(0.5, 1.3, sd, l, i, 2);
                var y = Wrap(J.R(sd, l, i, 3) * H * 1.2 + t * vy * (0.8 + 0.4 * J.R(sd, l, i, 4)), H * 1.2) - H * 0.1;
                var x = Wrap(J.R(sd, l, i, 5) * W * 1.1 + t * vx + Math.Sin(t * f + i) * U * sw, W * 1.1) - W * 0.05;
                DrawImg(ctx, spr, x - s, y - s, s * 2, s * 2, aB * e * (0.6 + 0.4 * J.R(sd, l, i, 6)));
            }
        }
    }

    private static void DrawFireworks(Env env)
    {
        var ctx = env.Ctx;
        var (W, H) = (env.W, env.H);
        var sc = env.Sc!;
        var P = POf(env);
        var t = env.T;
        var sd = (int)Num(P, "seed", 1);
        var U = Umin(env);
        var dk = IsDark(Bg(sc));
        var e = FadeIn(env, 0.3);
        var cols = Hues(sc);
        var blend = dk ? SKBlendMode.Screen : SKBlendMode.SrcOver;
        var per = Num(P, "per", 0.65);
        var idx = (int)Math.Floor(t / per);
        const double launch = 0.45;
        const double life = 2;
        var K = Num(P, "k", 0.48) * e * (dk ? 1 : 0.6);
        var g = U * 0.11;
        for (var b = idx - 5; b <= idx; b++)
        {
            var t0 = b * per + J.R(sd, b, 1) * per * 0.5;
            var age = t - t0;
            if (age < 0 || age > launch + life) continue;
            var ox = W * J.Rr(0.1, 0.9, sd, b, 2);
            var oy = H * J.Rr(0.1, 0.48, sd, b, 3);
            var @base = cols[(int)(J.H(sd, b, 4) % cols.Count)];
            var c = dk ? @base : J.Mix(@base, Bg(sc), 0.2);
            if (age < launch)
            {
                var q = J.E.OutQuad(age / launch);
                var y = J.Lerp(H * 1.02, oy, q);
                var y2 = J.Lerp(H * 1.02, oy, J.E.OutQuad(Math.Max(0, age - 0.12) / launch));
                using var up = new SKPath();
                up.MoveTo((float)(ox + Math.Sin(age * 30) * U * 0.002), (float)y);
                up.LineTo((float)ox, (float)y2);
                Stroke(ctx, up, c, Math.Max(1, U * 0.0022), K * 0.6, blend, SKStrokeCap.Round);
                continue;
            }
            var a = age - launch;
            var n0 = 28 + (int)(J.H(sd, b, 5) % 14);
            var n = n0 * 2;
            var V = U * J.Rr(0.7, 1.05, sd, b, 6);
            var fade = Math.Pow(1 - a / life, 1.6);
            (double X, double Y) Pos(int j, double tt, double sp, double th)
            {
                var d = sp * (1 - Math.Exp(-2.6 * tt)) / 2.6;
                return (ox + Math.Cos(th) * d, oy + Math.Sin(th) * d + 0.5 * g * tt * tt);
            }
            using var burst = new SKPath();
            var heads = new List<(double X, double Y)>();
            for (var j = 0; j < n; j++)
            {
                var ring = j >= n0;
                var th = j % n0 / (double)n0 * J.TAU + (ring ? Math.PI / n0 : 0) + J.Rs(sd, b, j) * 0.1;
                var sp = V * (ring ? 0.55 : 1) * (0.85 + 0.15 * J.R(sd, b, j, 7));
                var p1 = Pos(j, a, sp, th);
                var p0 = Pos(j, Math.Max(0, a - 0.16), sp, th);
                burst.MoveTo((float)p0.X, (float)p0.Y);
                burst.LineTo((float)p1.X, (float)p1.Y);
                heads.Add(p1);
            }
            Stroke(ctx, burst, c, Math.Max(1, U * 0.0024), K * fade, blend, SKStrokeCap.Round);
            var headCol = dk ? J.Mix(c, "#FFFFFF", 0.5) : c;
            var step = unchecked((int)env.Step);
            for (var j = 0; j < heads.Count; j++)
            {
                if (a > 0.9 && J.R(sd, b, j, step) < 0.35) continue; // 尾段噼啪闪烁
                FillRect(ctx, heads[j].X - U * 0.0028, heads[j].Y - U * 0.0028, U * 0.0056, U * 0.0056, headCol, K * fade, blend);
            }
            if (a < 0.25)
            {
                var fr = U * 0.12;
                using var gg = Rad(ox, oy, fr, Stops((0, Rgba(c, K * 0.6 * (1 - a / 0.25))), (1, Rgba(c, 0))));
                FillRect(ctx, ox - fr, oy - fr, fr * 2, fr * 2, gg, 1, blend);
            }
        }
    }

    private static void DrawCloudLayers(Env env)
    {
        var ctx = env.Ctx;
        var (W, H) = (env.W, env.H);
        var sc = env.Sc!;
        var P = POf(env);
        var t = env.T;
        var sd = (int)Num(P, "seed", 1);
        var U = Umin(env);
        var e = FadeIn(env, 1);
        var k = Num(P, "k", 0.075);
        var dir = Num(P, "dir", 1);
        var layers = new[] { (0.1, 0.3, 0.55, 0.012, 0.55), (0.3, 0.55, 0.8, 0.026, 0.8), (0.72, 0.98, 1.15, 0.05, 1.1) };
        for (var l = 0; l < layers.Length; l++)
        {
            var (ya, yb, scl, v, km) = layers[l];
            var cw0 = U * 0.5 * scl;
            var Lp = W + cw0 * 2.4;
            using var path = new SKPath();
            for (var c = 0; c < 4; c++)
            {
                var cw = cw0 * J.Rr(0.75, 1.2, sd, l, c, 1);
                var x = Wrap((c + J.R(sd, l, c, 2) * 0.6) / 4.0 * Lp + t * U * v * dir, Lp) - cw * 1.2;
                var y = H * J.Rr(ya, yb, sd, l, c, 3) + (1 - e) * H * 0.05 * (l + 1);
                var np = 5 + (int)(J.H(sd, l, c) % 3);
                var rmin = double.MaxValue;
                for (var p = 0; p < np; p++)
                {
                    var f = (p + 0.5) / np;
                    var pr = cw / np * (0.75 + 1.05 * Math.Sin(Math.PI * f)) * (0.85 + 0.3 * J.R(sd, l, c, p, 4))
                             * (1 + 0.04 * Math.Sin(t * 0.6 + p + c));
                    var px = x + cw * f + J.Rs(sd, l, c, p, 5) * cw * 0.03;
                    AddCircle(path, px, y - pr, pr);
                    rmin = Math.Min(rmin, pr);
                }
                path.AddRect(new SKRect((float)(x + cw * 0.5 / np), (float)(y - rmin), (float)(x + cw * 0.5 / np + cw * (1 - 1.0 / np)), (float)y));
            }
            FillPath(ctx, path, LayC(sc, k * km), 1);
        }
    }

    private static void DrawMountains(Env env)
    {
        var ctx = env.Ctx;
        var (W, H) = (env.W, env.H);
        var sc = env.Sc!;
        var P = POf(env);
        var t = env.T;
        var sd = (int)Num(P, "seed", 1);
        var U = Umin(env);
        var e = FadeIn(env, 1.1);
        var n = (int)Num(P, "n", 3);
        var k = Num(P, "k", 0.085);
        var m = J.Clamp((int)Round(W / (U * 0.01)), 60, 220);
        var port = H > W;
        for (var l = 0; l < n; l++)
        {
            var q = n > 1 ? l / (double)(n - 1) : 1;
            var @base = H * ((port ? 0.68 : 0.64) + 0.14 * q) + (1 - e) * H * 0.3 * (1 - q * 0.4);
            var A = H * (port ? 0.17 : 0.3) * (1 - 0.35 * q);
            var off = t * 0.012 * (l + 1) * Num(P, "dir", 1) + l * 7.3;
            var sc2 = U * (0.42 + 0.2 * (1 - q));
            using var path = new SKPath();
            path.MoveTo(-2, H + 2);
            for (var j = 0; j <= m; j++)
            {
                var x = W * j / (double)m;
                var X = x / sc2 + off;
                double v = 0, amp = 1, f = 1, nrm = 0;
                for (var o = 0; o < 4; o++)
                {
                    v += (1 - Math.Abs(J.Noise1(X * f, sd + l * 31 + o * 7))) * amp;
                    nrm += amp;
                    amp *= 0.48;
                    f *= 2.2;
                }
                v = J.Clamp((v / nrm - 0.4) * 1.8);
                path.LineTo((float)x, (float)(@base - A * Math.Pow(v, 2)));
            }
            path.LineTo(W + 2, H + 2);
            path.Close();
            FillPath(ctx, path, LayC(sc, k * (0.45 + 0.75 * q)), 1);
            if (!Flag(P, "mist") && P["mist"] != null) continue; // JS mist !== false
            if (l < n - 1)
            {
                using var g = Lin(0, @base - A * 0.25, 0, @base + H * 0.06,
                    Stops((0, Rgba(Bg(sc), 0)), (1, Rgba(Bg(sc), 0.55))));
                FillRect(ctx, 0, @base - A * 0.25, W, A * 0.25 + H * 0.06, g);
                FillRect(ctx, 0, @base + H * 0.06, W, H, Rgba(Bg(sc), 0.55));
            }
        }
    }

    private static void DrawFilmStrip(Env env)
    {
        var ctx = env.Ctx;
        var (W, H) = (env.W, env.H);
        var sc = env.Sc!;
        var P = POf(env);
        var t = env.T;
        var sd = (int)Num(P, "seed", 1);
        var U = Umin(env);
        var dk = IsDark(Bg(sc));
        var e = J.E.OutExpo(J.Clamp(BgT(env) / 0.55));
        var vert = H > W;
        var b = U * 0.085;
        var L = vert ? H : (double)W;
        var slide = (1 - e) * b * 1.3;
        var band = dk ? LayC(sc, 0.05) : LayC(sc, 0.13);
        var hole = dk ? LayC(sc, 0.17) : J.Mix(Bg(sc), "#FFFFFF", 0.55);
        var edge = dk ? LayC(sc, 0.12) : LayC(sc, 0.22);
        var p = b * 0.62;
        var hw = b * 0.3;
        var hh = b * 0.38;
        var off = Wrap(t * p * Num(P, "spd", 0.9) * Num(P, "dir", 1), p);
        void Ra(double a, double c, double w, double h, string col2, double al) // a 沿条带方向，c 横向
        {
            if (vert) FillRect(ctx, c, a, h, w, col2, al);
            else FillRect(ctx, a, c, w, h, col2, al);
        }
        var eh = Math.Max(1, U * 0.002);
        for (var side = 0; side < 2; side++)
        {
            var c0 = side != 0 ? (vert ? W : H) - b + slide : -slide;
            Ra(0, c0, L, b, band, 1);
            Ra(0, side != 0 ? c0 : c0 + b - eh, L, eh, edge, 1);
            using (var holes = new SKPath())
            {
                for (var a = off - p; a < L + p; a += p)
                {
                    var x = a + (p - hw) / 2;
                    var y = c0 + (b - hh) / 2 + (side != 0 ? b * 0.12 : -b * 0.12);
                    var r = b * 0.06;
                    if (vert) holes.AddRoundRect(new SKRect((float)y, (float)x, (float)(y + hh), (float)(x + hw)), (float)r, (float)r);
                    else holes.AddRoundRect(new SKRect((float)x, (float)y, (float)(x + hw), (float)(y + hh)), (float)r, (float)r);
                }
                FillPath(ctx, holes, hole);
            }
            // 每 4 个齿孔一道分帧线
            using (var marks = new SKPath())
            {
                for (var a = Wrap(off, p * 4) - p * 4; a < L + p; a += p * 4)
                {
                    if (vert) marks.AddRect(new SKRect((float)(c0 + b * (side != 0 ? 0.72 : 0.08)), (float)a,
                        (float)(c0 + b * (side != 0 ? 0.72 : 0.08) + b * 0.2), (float)(a + eh)));
                    else marks.AddRect(new SKRect((float)a, (float)(c0 + b * (side != 0 ? 0.72 : 0.08)),
                        (float)(a + eh), (float)(c0 + b * (side != 0 ? 0.72 : 0.08) + b * 0.2)));
                }
                FillPath(ctx, marks, edge);
            }
        }
        if (!Flag(P, "scratch") && P["scratch"] != null) return; // JS scratch !== false
        // 闪烁的划痕和灰尘，跟着绘制帧的时钟换
        var st = unchecked((int)env.Step);
        var scrCol = dk ? LayC(sc, 0.35) : LayC(sc, 0.3);
        for (var i = 0; i < 3; i++)
        {
            if (J.R(sd, st, i, 1) > 0.55) continue;
            var x = J.R(sd, st >> 2, i, 2) * (vert ? H : W) + J.Rs(sd, st, i, 3) * U * 0.004;
            var w = Math.Max(1, U * J.Rr(0.0008, 0.002, sd, st, i, 4));
            if (vert) FillRect(ctx, 0, x, W, w, scrCol, e * J.Rr(0.1, 0.22, sd, st, i, 5));
            else FillRect(ctx, x, 0, w, H, scrCol, e * J.Rr(0.1, 0.22, sd, st, i, 5));
        }
        for (var i = 0; i < 6; i++)
        {
            if (J.R(sd, st, i, 6) > 0.5) continue;
            var r = U * J.Rr(0.001, 0.003, sd, st, i, 7);
            FillRect(ctx, J.R(sd, st, i, 8) * W, J.R(sd, st, i, 9) * H, r * 2, r * 1.4, scrCol, e * 0.25);
        }
    }

    // VHS 斑点噪声，每个颜色/尺寸预渲染一次，3 个变体按绘制帧轮换
    private static SKImage VhsNoise(string col, int w, int h, int v) => Cached($"vhs|{col}|{w}x{h}|{v}", () =>
    {
        var (r0, g0, b0) = J.Hex(col);
        var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var px = new SKPixmap(bmp.Info, bmp.GetPixels(), bmp.RowBytes))
        {
            var span = px.GetPixelSpan();
            for (var y = 0; y < h; y++)
            {
                var row = 0.35 + 0.65 * Hash2(3, y, v * 13 + 1);
                for (var i = 0; i < w; i++)
                {
                    var n = Hash2(i >> 1, y, v * 7 + 2);
                    n = n * n * n;
                    if (Hash2(i >> 4, y, v * 5 + 3) > 0.94) n = Math.Max(n, 0.55 + 0.45 * Hash2(i, y, v + 9));
                    var o = (y * w + i) * 4;
                    span[o] = (byte)b0;     // BGRA8888 字节序
                    span[o + 1] = (byte)g0;
                    span[o + 2] = (byte)r0;
                    span[o + 3] = (byte)Math.Round(255 * J.Clamp(n * row));
                }
            }
        }
        var img = SKImage.FromPixels(bmp.Info, bmp.GetPixels(), bmp.RowBytes);
        return new ImgEntry { Img = img, Owner = bmp };
    });

    private static void DrawVhsBand(Env env)
    {
        var ctx = env.Ctx;
        var (W, H) = (env.W, env.H);
        var sc = env.Sc!;
        var P = POf(env);
        var t = env.T;
        var sd = (int)Num(P, "seed", 1);
        var U = Umin(env);
        var dk = IsDark(Bg(sc));
        var e = FadeIn(env, 0.3);
        var st = unchecked((int)env.Step);
        var q = Math.Min(1, env.Scale == 0 ? 1 : env.Scale) * 0.5;
        var nw = Math.Max(64, (int)Math.Ceiling(W * q));
        var nh = Math.Max(32, (int)Math.Ceiling(H * 0.2 * q));
        var col = dk ? Fg(sc) : J.Mix(Fg(sc), Bg(sc), 0.2);
        var k = Num(P, "k", 0.26) * e * (dk ? 1 : 0.75);
        var nz = VhsNoise(col, nw, nh, ((st % 3) + 3) % 3);
        var bh = H * Num(P, "h", 0.09);
        var y = Wrap(t * H * Num(P, "spd", 0.12) + J.R(sd, 1) * H, H * 1.3) - H * 0.15;
        // imageSmoothingEnabled = false → 最近邻
        var sh = Math.Min(nh, (int)Math.Ceiling(bh * q));
        var sy = (int)Math.Floor(J.R(sd, st, 2) * Math.Max(1, nh - sh));
        DrawImgSrc(ctx, nz, new SKRect(0, sy, nw, sy + sh),
            new SKRect((float)(J.Rs(sd, st, 3) * U * 0.01), (float)y, (float)(J.Rs(sd, st, 3) * U * 0.01 + W), (float)(y + bh)), k, SKBlendMode.SrcOver, SmpNearest);
        using (var g = Lin(0, y - bh * 0.6, 0, y + bh * 1.4,
                   Stops((0, Rgba(LightOn(sc), 0)), (0.4, Rgba(LightOn(sc), dk ? 0.05 : 0.1)), (1, Rgba(LightOn(sc), 0)))))
            FillRect(ctx, 0, y - bh * 0.6, W, bh * 2, g, e);
        var lc = dk ? LayC(sc, 0.3) : LayC(sc, 0.2);
        for (var i = 0; i < 3; i++)
        {
            var ly = y + bh * J.R(sd, st >> 1, i, 4);
            var lx = J.R(sd, st, i, 5) * W * 0.6;
            FillRect(ctx, lx, ly, W * J.Rr(0.2, 0.6, sd, st, i, 6), Math.Max(1, U * 0.0016), lc, e * 0.5);
        }
        // 底部 head-switching 条：横向错位的撕裂噪声
        var hb = H * 0.028;
        const int rows = 4;
        for (var r = 0; r < rows; r++)
        {
            var ry = H - hb + hb * r / (double)rows;
            var dx = J.Rs(sd, st, r, 7) * U * 0.03 + U * 0.02 * (rows - r) / (double)rows;
            var sry = (sy + r * 3) % Math.Max(1, nh - 2);
            DrawImgSrc(ctx, nz, new SKRect(0, sry, nw, sry + 2),
                new SKRect((float)dx, (float)ry, (float)(dx + W), (float)(ry + hb / rows + 0.5)), k * 0.9, SKBlendMode.SrcOver, SmpNearest);
        }
    }

    private static void DrawTornPaper(Env env)
    {
        var ctx = env.Ctx;
        var (W, H) = (env.W, env.H);
        var sc = env.Sc!;
        var P = POf(env);
        var t = env.T;
        var sd = (int)Num(P, "seed", 1);
        var U = Umin(env);
        var dk = IsDark(Bg(sc));
        var bt = BgT(env);
        var k = Num(P, "k", 0.065);
        var v = Str(P, "v", "tb");
        if (v == "side" && H > W) v = "tb";
        // [A, B, n]：撕边从 A 到 B，纸盖在法线 n 指的那一侧
        double[][][] S;
        if (v == "diag")
            S = new[]
            {
                new[] { new[] { -W * 0.05, H * 0.58 }, new[] { W * 0.5, H * 1.05 }, new[] { -0.7, 0.7 } },
                new[] { new[] { W * 0.55, -H * 0.05 }, new[] { W * 1.05, H * 0.48 }, new[] { 0.7, -0.7 } }
            };
        else if (v == "side")
            S = new[]
            {
                new[] { new[] { W * 0.13, -H * 0.05 }, new[] { W * 0.1, H * 1.05 }, new[] { -1.0, 0.0 } },
                new[] { new[] { W * 0.9, -H * 0.05 }, new[] { W * 0.87, H * 1.05 }, new[] { 1.0, 0.0 } }
            };
        else
            S = new[]
            {
                new[] { new[] { -W * 0.05, H * 0.83 }, new[] { W * 1.05, H * 0.79 }, new[] { 0.0, 1.0 } },
                new[] { new[] { -W * 0.05, H * 0.13 }, new[] { W * 1.05, H * 0.17 }, new[] { 0.0, -1.0 } }
            };
        var tones = dk
            ? new[] { LayC(sc, k), J.Mix(Bg(sc), Ac(sc), k * 1.6) }
            : new[] { J.Mix(Bg(sc), "#FFFFFF", 0.5), LayC(sc, k) };
        for (var i = 0; i < S.Length; i++)
        {
            var A = S[i][0];
            var B = S[i][1];
            var nrm = S[i][2];
            var inP = J.E.OutCubic(J.Clamp((bt - i * 0.12) / 0.6));
            var push = (1 - inP) * U * 0.35 + Math.Sin(t * 0.5 + i * 2) * U * 0.004;
            var ox = nrm[0] * push;
            var oy = nrm[1] * push;
            var len = Hyp(B[0] - A[0], B[1] - A[1]);
            var N = Math.Min(260, (int)Math.Ceiling(len / (U * 0.009)));
            var tx = (B[0] - A[0]) / len;
            var ty = (B[1] - A[1]) / len;
            var pts = new List<double[]>();
            for (var j = 0; j <= N; j++)
            {
                var u = j / (double)N;
                var o = U * (0.018 * J.Noise1(u * 9, sd + i * 17) + 0.007 * J.Noise1(u * 45, sd + i * 5) + 0.0035 * J.Rs(sd, i, j));
                pts.Add(new[] { A[0] + (B[0] - A[0]) * u + nrm[0] * o + ox, A[1] + (B[1] - A[1]) * u + nrm[1] * o + oy });
            }
            var far = Hyp(W, H);
            using var poly = new SKPath();
            poly.MoveTo((float)pts[0][0], (float)pts[0][1]);
            for (var j = 1; j < pts.Count; j++) poly.LineTo((float)pts[j][0], (float)pts[j][1]);
            poly.LineTo((float)(B[0] + nrm[0] * far + ox + tx * far * 0.2), (float)(B[1] + nrm[1] * far + oy + ty * far * 0.2));
            poly.LineTo((float)(A[0] + nrm[0] * far + ox - tx * far * 0.2), (float)(A[1] + nrm[1] * far + oy - ty * far * 0.2));
            poly.Close();
            var sd2 = U * 0.008;
            ctx.Save();
            ctx.Translate((float)(-nrm[0] * sd2 * 0.5), (float)(-nrm[1] * sd2 * 0.5 + sd2 * 0.6));
            FillPath(ctx, poly, Rgba("#000000", dk ? 0.35 : 0.1));
            ctx.Restore();
            FillPath(ctx, poly, tones[i % 2]);
            // 撕口的纤维毛边，撕痕处纸芯更亮
            using var fiber = new SKPath();
            for (var j = 0; j < pts.Count; j++)
            {
                var q = pts[j];
                if (j != 0) fiber.LineTo((float)(q[0] - nrm[0] * U * 0.002), (float)(q[1] - nrm[1] * U * 0.002));
                else fiber.MoveTo((float)q[0], (float)q[1]);
            }
            Stroke(ctx, fiber, dk ? LayC(sc, k * 2.4) : J.Mix(tones[i % 2], "#FFFFFF", 0.75), Math.Max(1, U * 0.004),
                1, SKBlendMode.SrcOver, SKStrokeCap.Butt, SKStrokeJoin.Round);
        }
    }

    private static void DrawGodRays(Env env)
    {
        var ctx = env.Ctx;
        var (W, H) = (env.W, env.H);
        var sc = env.Sc!;
        var P = POf(env);
        var t = env.T;
        var sd = (int)Num(P, "seed", 1);
        var U = Umin(env);
        var dk = IsDark(Bg(sc));
        var e = FadeIn(env, 1);
        var rc = dk ? LightOn(sc) : J.Lum(Bg(sc)) < 0.8 ? "#FFFFFF" : J.Mix(Ac(sc), Bg(sc), 0.3);
        var k = Num(P, "k", 0.13) * e * (dk ? 1.35 : 1.4);
        var sx = W * NumN(P, "x", 0.5) + W * 0.04 * Math.Sin(t * 0.1 + sd % 7);
        var sy = -H * 0.12;
        var L = Hyp(W, H) * 1.25;
        var n = (int)Num(P, "n", 9);
        var @base = Math.Atan2(H * 0.55 - sy, W * 0.5 - sx);
        DrawLow(ctx, env, 4, (x, _) =>
        {
            using var q0 = new SKPath();
            using var q1 = new SKPath();
            using var q2 = new SKPath();
            var paths = new[] { q0, q1, q2 };
            for (var i = 0; i < n; i++)
            {
                var a = @base + (Num(P, "spread", 60) * (i / (double)(n - 1) - 0.5) + J.Rs(sd, i, 1) * 4
                                 + 3 * J.Noise1(t * 0.15 + i * 1.3, sd)) * J.DEG;
                var w = (1.2 + 3.6 * J.R(sd, i, 2)) * J.DEG * (0.75 + 0.25 * J.Noise1(t * 0.4 + i, sd + 7));
                var lv = 0.5 + 0.5 * J.Noise1(t * 0.35 + i * 2.1, sd + 3);
                var dim = lv > 0.66 ? 0 : lv > 0.33 ? 1 : 2;
                // 三层嵌套楔形，光束的衰减才柔和
                var fs = new[] { 0.45, 1.0, 1.8 };
                for (var qi = 0; qi < 3; qi++)
                {
                    var p = paths[Math.Min(2, qi + dim)];
                    var ww = w * fs[qi];
                    p.MoveTo((float)sx, (float)sy);
                    p.LineTo((float)(sx + Math.Cos(a - ww / 2) * L), (float)(sy + Math.Sin(a - ww / 2) * L));
                    p.LineTo((float)(sx + Math.Cos(a + ww / 2) * L), (float)(sy + Math.Sin(a + ww / 2) * L));
                    p.Close();
                }
            }
            using var g = Rad(sx, sy, L, Stops(
                (0, Rgba(rc, Math.Min(1, k * 1.3))), (0.3, Rgba(rc, k * 0.6)),
                (0.7, Rgba(rc, k * 0.12)), (1, Rgba(rc, 0))));
            var ms = new[] { 0.55, 0.3, 0.16 };
            for (var l = 0; l < 3; l++) FillPath(x, paths[l], g, ms[l]);
        });
        if (!Flag(P, "dust") && P["dust"] != null) return; // JS dust !== false
        // 光柱里飘的尘埃
        for (var i = 0; i < 26; i++)
        {
            var x = Wrap(J.R(sd, i, 11) * W + t * U * J.Rs(sd, i, 12) * 0.02 + Math.Sin(t * 0.5 + i) * U * 0.01, W);
            var y = Wrap(J.R(sd, i, 13) * H + t * U * J.Rr(-0.02, 0.01, sd, i, 14), H);
            var r = U * J.Rr(0.0012, 0.003, sd, i, 15);
            var near = J.Clamp(1 - Hyp(x - sx, y - sy) / L);
            FillRect(ctx, x - r, y - r, r * 2, r * 2, rc, e * (0.15 + 0.35 * near) * (0.5 + 0.5 * Math.Sin(t * J.Rr(0.8, 2, sd, i, 16) + i)));
        }
    }

    private static void DrawVignettePulse(Env env)
    {
        var ctx = env.Ctx;
        var (W, H) = (env.W, env.H);
        var sc = env.Sc!;
        var P = POf(env);
        var t = env.T;
        var U = Umin(env);
        var dk = IsDark(Bg(sc));
        var e = FadeIn(env, 0.8);
        var cols = Hues(sc);
        // JS：beat 时 0.62+0.38*exp(-since*4)，无节拍回固定周期脉动
        var pulse = env.BeatHas
            ? 0.62 + 0.38 * Math.Exp(-env.BeatSince * 4)
            : 0.75 + 0.25 * Math.Sin(t * J.TAU * Num(P, "rate", 0.45));
        var k = Num(P, "k", 0.35) * e * pulse * (dk ? 1 : 0.8);
        var cA = J.Mix(Bg(sc), cols[0], 0.7);
        var cB = J.Mix(Bg(sc), cols.Count > 1 ? cols[1] : cols[0], 0.7);
        var R = Hyp(W, H) / 2;
        DrawLow(ctx, env, 6, (x, _) =>
        {
            if (Flag(P, "two") || P["two"] == null)
            {
                foreach (var (cx, cy, c, ph) in new[] { (0.0, 0.0, cA, 0.0), (W * 1.0, H * 1.0, cB, 1.7) })
                {
                    var rr = R * (1.25 + 0.08 * Math.Sin(t * 0.6 + ph));
                    using var g = Rad(cx, cy, rr, Stops((0, Rgba(c, k)), (0.45, Rgba(c, k * 0.35)), (1, Rgba(c, 0))));
                    FillRect(x, 0, 0, W, H, g);
                }
            }
            else
            {
                using var g = Rad2(W / 2.0, H / 2.0, U * 0.32 * (1.08 - 0.12 * pulse), R * 1.05,
                    Stops((0, Rgba(cA, 0)), (0.6, Rgba(cA, k * 0.45)), (1, Rgba(cA, k * 1.1))));
                FillRect(x, 0, 0, W, H, g);
            }
        });
    }

    private static void DrawKaleidoscope(Env env)
    {
        var ctx = env.Ctx;
        var (W, H) = (env.W, env.H);
        var sc = env.Sc!;
        var P = POf(env);
        var t = env.T;
        var sd = (int)Num(P, "seed", 1);
        var U = Umin(env);
        var e = FadeIn(env, 0.8);
        var n = (int)Num(P, "n", 8);
        var m = (int)Num(P, "m", 7);
        var k = Num(P, "k", 0.085);
        var cx = W / 2.0;
        var cy = H / 2.0;
        var R = Hyp(W, H) * 0.58;
        var seg = Math.PI / n;
        var r0 = U * 0.13;
        var rot = t * Num(P, "spd", 0.07);
        using var fillP = new SKPath();
        using var lineP = new SKPath();
        void Put(SKPath p, int type, double x, double y, double s, double a, double flip)
        {
            var c = Math.Cos(a);
            var si = Math.Sin(a);
            double[] T(double px, double py) => new[] { x + px * c - py * flip * si, y + px * si + py * flip * c };
            if (type == 2)
            {
                AddCircle(p, x, y, s * 0.55);
                return;
            }
            var pts = type == 0
                ? new[] { new[] { s, 0.0 }, new[] { -s / 2, s * 0.8 }, new[] { -s / 2, -s * 0.3 } }
                : new[] { new[] { s, 0.0 }, new[] { 0.0, s * 0.42 }, new[] { -s * 0.7, 0.0 }, new[] { 0.0, -s * 0.42 } };
            var q = pts.Select(pt => T(pt[0], pt[1])).ToArray();
            Poly(p, q);
        }
        for (var j = 0; j < m; j++)
        {
            var type = (int)(J.H(sd, j) % 3);
            var v = 0.6 + 0.8 * J.R(sd, j, 2);
            var r = r0 + Wrap(J.R(sd, j, 1) * (R - r0) + t * U * 0.05 * v, R - r0);
            var fade = J.Clamp((r - r0) / (U * 0.15)) * J.Clamp((R - r) / (U * 0.2)) * e;
            if (fade <= 0.02) continue;
            var phi = seg * (0.15 + 0.7 * (0.5 + 0.5 * Math.Sin(t * 0.35 * v + j * 1.9)));
            var s = r * (0.09 + 0.1 * J.R(sd, j, 3)) * fade;
            var own = t * J.Rs(sd, j, 4) * 1.2 + j;
            var p = J.R(sd, j, 5) < 0.35 ? lineP : fillP;
            for (var q = 0; q < n; q++)
            {
                var b = rot + q * 2 * seg;
                Put(p, type, cx + Math.Cos(b + phi) * r, cy + Math.Sin(b + phi) * r, s, b + phi + own, 1);
                Put(p, type, cx + Math.Cos(b - phi) * r, cy + Math.Sin(b - phi) * r, s, b - phi - own, -1);
            }
        }
        FillPath(ctx, fillP, LayC(sc, k));
        Stroke(ctx, lineP, TintC(sc, k * 2.4), Math.Max(1, U * 0.0028));
        // 淡淡的多边形环，撑住对称感
        using var rings = new SKPath();
        foreach (var (rr, dir) in new[] { (U * 0.46, -1.0), (U * 0.82, 1.0) })
        for (var q = 0; q <= 2 * n; q++)
        {
            var a = -rot * dir * 1.5 + q * seg;
            var x = cx + Math.Cos(a) * rr;
            var y = cy + Math.Sin(a) * rr;
            if (q != 0) rings.LineTo((float)x, (float)y);
            else rings.MoveTo((float)x, (float)y);
        }
        Stroke(ctx, rings, LayC(sc, k * 0.9), Math.Max(1, U * 0.002), e);
    }

    // 大理石纹：每个 seed/尺寸/颜色低分辨率算一次，之后慢慢转
    private static SKImage MarbleCv(JObject P, int px, string c1, string c2)
        => Cached($"marb|{Num(P, "seed", 1)}|{px}|{c1}|{c2}|{(Flag(P, "acc") ? 1 : 0)}", () =>
        {
            var bmp = new SKBitmap(px, px, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var pix = new SKPixmap(bmp.Info, bmp.GetPixels(), bmp.RowBytes))
            {
                var span = pix.GetPixelSpan();
                var sd = (int)Num(P, "seed", 1);
                var a = Num(P, "ang", 0.7);
                var ca = Math.Cos(a);
                var sa = Math.Sin(a);
                var cb = Math.Cos(a + 1.1);
                var sb = Math.Sin(a + 1.1);
                var fq = Num(P, "freq", 3) * J.TAU;
                var tb = Num(P, "turb", 5);
                var A = J.Hex(c1);
                var B = J.Hex(c2);
                var acc = Flag(P, "acc");
                for (var j = 0; j < px; j++)
                for (var i = 0; i < px; i++)
                {
                    var u = i / (double)px;
                    var v = j / (double)px;
                    var X = u * 3;
                    var Y = v * 3;
                    var s1 = Math.Sin((u * ca + v * sa) * fq + Fbm2(X, Y, sd, 4) * tb);
                    var w1 = 1 - Math.Abs(s1);
                    var cloud = J.Clamp((Fbm2(X * 0.9 + 11, Y * 0.9, sd + 21, 2) - 0.42) * 1.6);
                    var a1 = Math.Pow(w1, 14) * (0.45 + 0.55 * Noise2(X * 2.2 + 3, Y * 2.2, sd + 3))
                             + Math.Pow(w1, 4) * 0.08 + cloud * 0.22;
                    double a2 = 0;
                    if (acc)
                    {
                        var s2 = Math.Sin((u * cb + v * sb) * fq * 1.6 + Fbm2(X * 1.4 + 5, Y * 1.4, sd + 9, 3) * tb * 1.3);
                        a2 = Math.Pow(1 - Math.Abs(s2), 12) * 0.5;
                    }
                    var al = a1 + a2 * (1 - a1);
                    var o = (j * px + i) * 4;
                    if (al <= 0.003)
                    {
                        span[o + 3] = 0;
                        continue;
                    }
                    var f = a2 * (1 - a1) / al;
                    span[o] = (byte)Math.Round(A.Item3 + (B.Item3 - A.Item3) * f);     // BGRA8888 字节序
                    span[o + 1] = (byte)Math.Round(A.Item2 + (B.Item2 - A.Item2) * f);
                    span[o + 2] = (byte)Math.Round(A.Item1 + (B.Item1 - A.Item1) * f);
                    span[o + 3] = (byte)Math.Round(255 * J.Clamp(al));
                }
            }
            var img = SKImage.FromPixels(bmp.Info, bmp.GetPixels(), bmp.RowBytes);
            return new ImgEntry { Img = img, Owner = bmp };
        });

    private static void DrawMarble(Env env)
    {
        var ctx = env.Ctx;
        var (W, H) = (env.W, env.H);
        var sc = env.Sc!;
        var P = POf(env);
        var t = env.T;
        var e = FadeIn(env, 1);
        var U = Umin(env);
        var M = U * 0.12;
        var s = (int)Num(P, "seed", 1) % 13;
        var c2 = J.Contrast(Ac(sc), Bg(sc)) > 1.3 ? Ac(sc) : (sc.Str("accent2") ?? Fg(sc));
        // 低清纹理一次性放大进带漂移余量的整帧底板，每帧再做整像素 blit
        var key = $"marbP|{Num(P, "seed", 1)}|{Num(P, "ang", 0).ToString("0.000", CultureInfo.InvariantCulture)}|{Num(P, "freq", 0).ToString("0.000", CultureInfo.InvariantCulture)}|{Num(P, "turb", 0).ToString("0.000", CultureInfo.InvariantCulture)}|{(Flag(P, "acc") ? 1 : 0)}|{Fg(sc)}|{c2}";
        var pl = Plate(key, env, M * 2, M * 2, (x, w, h) =>
        {
            var D = Math.Max(w, h);
            var px = J.Clamp((int)Round(D * Math.Min(0.2, ResQ(env) * 0.28)), 96, 440);
            var cv = MarbleCv(P, px, Fg(sc), c2);
            DrawImg(x, cv, (w - D) / 2, (h - D) / 2, D, D);
        });
        Blit(ctx, env, pl, M + M * 0.9 * Math.Sin(t * 0.06 * Num(P, "dir", 1) + s),
            M + M * 0.9 * Math.Cos(t * 0.045 + s * 2), Num(P, "k", 0.23) * e * (IsDark(Bg(sc)) ? 1 : 0.8));
    }

    private static void DrawPaperCut(Env env)
    {
        var ctx = env.Ctx;
        var (W, H) = (env.W, env.H);
        var sc = env.Sc!;
        var P = POf(env);
        var t = env.T;
        var sd = (int)Num(P, "seed", 1);
        var U = Umin(env);
        var dk = IsDark(Bg(sc));
        var bt = BgT(env);
        var L = (int)Num(P, "L", 3);
        var k = Num(P, "k", 0.075);
        var pe = 2 / Num(P, "p", 2.8);
        var cx = W / 2.0;
        var cy = H / 2.0;
        const int N = 144;
        var lobes = (int)Num(P, "lobes", 6);
        for (var i = 0; i < L; i++)
        {
            var d = L > 1 ? (L - 1 - i) / (double)(L - 1) : 0; // 1 是最里层，开口最小
            var inP = J.E.OutCubic(J.Clamp((bt - i * 0.1) / 0.8));
            var grow = 1 + (1 - inP) * 0.7;
            var rx = W * (0.41 + 0.17 * (1 - d)) * grow;
            var ry = H * (0.38 + 0.17 * (1 - d)) * grow;
            var ph = J.R(sd, i, 1) * J.TAU;
            var sw = t * 0.25 * (i % 2 != 0 ? 1 : -1);
            var ox = Math.Sin(t * 0.35 + i) * U * 0.006 * (i + 1);
            var oy = Math.Cos(t * 0.3 + i) * U * 0.004 * (i + 1);
            using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
            path.AddRect(new SKRect(-W, -H, W * 3f, H * 3f));
            for (var j = 0; j < N; j++)
            {
                var th = j / (double)N * J.TAU;
                var c = Math.Cos(th);
                var s = Math.Sin(th);
                var wv = 1 + 0.035 * Math.Sin((lobes + i) * th + ph + sw) + 0.014 * Math.Sin((lobes * 2 + 3) * th - ph * 1.3 - sw * 0.7);
                var x = cx + ox + rx * Math.Sign(c) * Math.Pow(Math.Abs(c), pe) * wv;
                var y = cy + oy + ry * Math.Sign(s) * Math.Pow(Math.Abs(s), pe) * wv;
                if (j != 0) path.LineTo((float)x, (float)y);
                else path.MoveTo((float)x, (float)y);
            }
            path.Close();
            var sh = U * 0.009;
            ctx.Save();
            ctx.Translate((float)(sh * 0.4), (float)sh);
            FillPath(ctx, path, Rgba("#000000", dk ? 0.32 : 0.12));
            ctx.Restore();
            var hue = Flag(P, "acc") && i % 2 != 0 ? Ac(sc) : Fg(sc);
            var fill = dk
                ? J.Mix(Bg(sc), hue, k * (0.5 + 0.7 * (i + 1) / (double)L))
                : J.Mix(Bg(sc), i % 2 != 0 ? hue : "#FFFFFF", (i % 2 != 0 ? k : 0.35) * (0.6 + 0.5 * (i + 1) / (double)L));
            FillPath(ctx, path, fill);
        }
    }

    // ================= cam helper（JS L1961-2121） =================

    private static double KM(Env env) => J.Clamp(env.Fx.Motion * 1.25, 0, 1.25);
    private static double CuOf(Env env) => J.Clamp(env.Lt / Math.Max(0.3, env.Cut?.Dur ?? 0));
    private static int SeedOf(Env env) => (int)(env.Cut?.Seed ?? 0);

    private static double LagOf(Env env) => Math.Max(0, env.Ltb - env.Lt);

    /// <summary>带时间滞后的通路距上个节拍的时间；无节拍回 JS 的固定周期取模（wrap(env.lt, per)）。</summary>
    private static double BeatSince(Env env, double per)
    {
        if (env.BeatHas && env.BeatLen > 0.15)
        {
            var s = env.BeatSince - LagOf(env);
            if (s < 0) s += env.BeatLen;
            return s;
        }
        return Wrap(env.Lt, per);
    }

    private static JObject CamP(Env env) => env.Cut?.CamP ?? new JObject();

    private static readonly ConditionalWeakTable<Cut, double[]> SnapCache = new();

    private static double SnapTime(Env env, double at)
    {
        var c = env.Cut;
        if (c == null) return 0;
        if (SnapCache.TryGetValue(c, out var box)) return box[0];
        var v = c.Dur * at;
        var beats = env.Plan?.Beats;
        if (beats != null)
            foreach (var b in beats)
            {
                var r = b - c.Start;
                if (r >= c.Dur * 0.38 && r <= c.Dur * 0.72)
                {
                    v = r;
                    break;
                }
            }
        SnapCache.AddOrUpdate(c, new[] { v });
        return v;
    }

    // ---------------- 参数读取助手（JS 的 P.x || d / P.x ?? d / truthy 语义） ----------------

    private static readonly JObject EmptyP = new();

    private static JObject POf(Env env) => env.Cut?.BgP ?? EmptyP;

    /// <summary>JS (P.k || d)：缺键或 0 都取 d。</summary>
    private static double Num(JObject? p, string k, double d)
    {
        if (p?[k] is JValue jv && (jv.Type == JTokenType.Integer || jv.Type == JTokenType.Float))
        {
            var x = Convert.ToDouble(jv.Value, CultureInfo.InvariantCulture);
            if (x != 0) return x;
        }
        return d;
    }

    /// <summary>JS (P.x ?? d)：0 是有效值，只有缺键才取 d。</summary>
    private static double NumN(JObject? p, string k, double d)
    {
        if (p == null || p[k] == null) return d;
        if (p[k] is JValue jv && (jv.Type == JTokenType.Integer || jv.Type == JTokenType.Float))
            return Convert.ToDouble(jv.Value, CultureInfo.InvariantCulture);
        return d;
    }

    private static bool Flag(JObject? p, string k)
        => p?[k] is JValue { Type: JTokenType.Boolean } jv && (bool)jv.Value!;

    private static string Str(JObject? p, string k, string d)
        => p?[k] is JValue { Type: JTokenType.String } jv ? (string)jv.Value! : d;

    /// <summary>JS (P.pos || [a, b])：二元数组参数。</summary>
    private static double[] Pair(JObject? p, string k, double[] d)
    {
        if (p?[k] is JArray arr && arr.Count >= 2)
        {
            var a = new double[2];
            for (var i = 0; i < 2; i++)
            {
                a[i] = arr[i] is JValue jv && (jv.Type == JTokenType.Integer || jv.Type == JTokenType.Float)
                    ? Convert.ToDouble(jv.Value, CultureInfo.InvariantCulture)
                    : 0;
                if (a[i] == 0) a[i] = d[i];
            }
            return a;
        }
        return d;
    }

    private static JObject Prm(params (string K, object V)[] kv)
    {
        var o = new JObject();
        foreach (var (k, v) in kv)
            o[k] = v switch
            {
                JToken t => t,
                string s => new JValue(s),
                bool b => new JValue(b),
                int i => new JValue(i),
                long l => new JValue(l),
                double d => Math.Floor(d) == d && !double.IsInfinity(d) ? new JValue((long)d) : new JValue(d),
                double[] arr => new JArray(arr.Select(x => Math.Floor(x) == x ? new JValue((long)x) : new JValue(x))),
                _ => JToken.FromObject(v)
            };
        return o;
    }
}
