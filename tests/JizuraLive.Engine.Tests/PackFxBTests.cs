// M5 表现包 fxB + looks 冒烟：注册表语义 + 插件 Draw 直调。
// 1) Install 后 Reg.FxeOrder 含 fxB 的全部非内置 key（次序 = 源文件 fx() 出现序）；looks 四组
//    （treat/bg/cam/fx）key 命中且次序对位；
// 2) fxB 每条目：Planner.PreviewPlan(proj, "fx", key) 的 cut + 最小 item，装上 FxPlug 桥
//    （S = 有内容的整帧快照、tmp/tmp2 = 复用面工厂）直调 FxDef.Draw(env, ev) 不抛；
// 3) looks 每组每条目同构直调（treat 走 TreatDef.Draw、bg 走 BgDef.Draw、cam 走 CamDef.Get）；
// 4) Name 只断言非空（zh 覆盖由 ZhLabels 重放负责，不在这里比字符串）。

using System.Globalization;
using JizuraLive.Engine;
using Newtonsoft.Json.Linq;
using SkiaSharp;
using Xunit;

namespace JizuraLive.Engine.Tests;

[Collection("EngineRegistry")]
public class PackFxBTests
{
    /// <summary>11p_fxB.js 的 fx(k, d) 出现序（34 条）。</summary>
    private static readonly string[] FxBKeys =
    {
        "radialChroma", "bloomFlash", "bulge", "pixelSort", "interlace", "macroBlock",
        "halftone", "duotone", "ditherBit", "rotateSnap", "echoFrames", "kaleido",
        "bandInvert", "lightRays", "anamorphic", "heartbeat", "tvStatic", "dustScratches",
        "filmAdvance", "perspectiveTilt", "ripple", "focusLines", "speedLines", "starGlint",
        "colorBars", "zoomStutter", "negativeRing", "edgeDetect", "shatter", "defocus",
        "snapshot", "squash", "scanBar", "loopScroll",
    };

    /// <summary>11p_looks.js 各组注册出现序。</summary>
    private static readonly string[] LooksTreatKeys =
    {
        "outline", "outlineFill", "doubleOutline", "extrude", "longShadow", "hardShadow",
        "softShadow", "glow", "marker", "underline", "strike", "boxed", "gradientV",
        "splitColor", "halftone", "stripes", "hatch", "dotted", "alternate", "italic",
        "wide", "tall", "echoOutline", "emphasisDots",
    };

    private static readonly string[] LooksBgKeys =
    {
        "sunburst", "concentric", "halftoneFade", "bigStripes", "splitV", "splitH",
        "splitDiag", "gradientSweep", "spotlight", "tvBars", "checker", "bigChar",
        "speedLines", "scanBars", "dotGrid", "retroGrid", "bokehBg", "particlesBg",
        "ripples", "polka", "eqBars", "borderFrame", "letterbox", "noiseField",
    };

    private static readonly string[] LooksCamKeys =
    {
        "pullOut", "panL", "panR", "tiltUp", "dutch", "handheld", "beatPunch", "whipIn",
        "crashZoom", "bounce", "roll", "driftDiag", "shakeHard", "dollyIn", "stepZoom",
    };

    private static readonly string[] LooksFxKeys =
    {
        "panelWipe", "irisTrans", "doors", "blindsTrans", "rgbSplit", "smear", "vhsRoll",
        "trackingNoise", "mirrorFlash", "strobe", "posterize", "hueShift", "tileShift",
        "filmBurn", "whipBlur", "blackFrame", "whiteFrame", "waveWarp",
        "pixelDrift", "zoomPunch", "lightSweep", "crtOff", "splitSlide",
    };

    public PackFxBTests()
    {
        EngineBoot.Install();
        P11FxB.Install();
        P11Looks.Install();
        P11FxB.Install(); // 幂等：二次调用不改注册表
        P11Looks.Install();
    }

    private static void AssertOrdered(string[] keys, List<string> order, string group, string pack,
        Func<string, IRegEntry?> fetch)
    {
        Assert.Equal(keys.Length, keys.Distinct().Count());
        var idx = keys.Select(k => order.IndexOf(k)).ToList();
        Assert.DoesNotContain(-1, idx);
        for (var i = 1; i < idx.Count; i++)
            Assert.True(idx[i] > idx[i - 1], $"order 次序漂移：{group}.{keys[i - 1]} → {group}.{keys[i]}");
        foreach (var k in keys)
        {
            var d = fetch(k);
            Assert.True(d != null, $"{group}.{k} 未注册");
            Assert.Equal(pack, d!.Pack);
            Assert.False(string.IsNullOrEmpty(d.Name), $"{group}.{k} 缺 name");
        }
    }

    [Fact]
    public void Install_FxeOrder_含全部非内置_fxB_Key()
    {
        AssertOrdered(FxBKeys, Reg.FxeOrder, "fx", "fxB", k => Reg.Fxe.GetValueOrDefault(k));
        foreach (var k in FxBKeys)
        {
            Assert.False(Reg.Fxe[k].Builtin, $"fx.{k} 应为非内置（插件路径）");
            Assert.NotNull(Reg.Fxe[k].Draw);
        }
    }

    [Fact]
    public void Install_Looks_四组_Key_命中且次序对位()
    {
        AssertOrdered(LooksTreatKeys, Reg.TreatOrder, "treat", "looks", k => Reg.Treat.GetValueOrDefault(k));
        AssertOrdered(LooksBgKeys, Reg.BgOrder, "bg", "looks", k => Reg.Bg.GetValueOrDefault(k));
        AssertOrdered(LooksCamKeys, Reg.CameraOrder, "cam", "looks", k => Reg.Camera.GetValueOrDefault(k));
        AssertOrdered(LooksFxKeys, Reg.FxeOrder, "fx", "looks", k => Reg.Fxe.GetValueOrDefault(k));
    }

    // ---------------- 插件 Draw 直调（fxB + looks fx） ----------------

    private sealed class TmpPool : IDisposable
    {
        public readonly List<SKSurface> All = new();
        private readonly Dictionary<(int W, int H), SKSurface> _map = new();

        public SKSurface Get(int w, int h)
        {
            w = Math.Max(1, w);
            h = Math.Max(1, h);
            if (!_map.TryGetValue((w, h), out var s))
            {
                s = SKSurface.Create(new SKImageInfo(w, h));
                _map[(w, h)] = s;
                All.Add(s);
            }
            return s;
        }

        public void Dispose()
        {
            foreach (var s in All) s.Dispose();
        }
    }

    /// <summary>PreviewPlan 的 fx 事件 + 最小 env，直调 FxDef.Draw(env, ev)（插件路径全链在桥上）。</summary>
    private static void DrawFxEntry(string key, string? packFilter = null)
    {
        var def = Reg.Fxe.GetValueOrDefault(key);
        Assert.NotNull(def);
        if (packFilter != null) Assert.Equal(packFilter, def!.Pack);
        var proj = Project.NewDefault();
        proj.Lyrics = "夜明けの色を覚えてる";
        var plan = Planner.PreviewPlan(proj, "fx", key);
        Assert.NotNull(plan);
        var ev = plan.Events.FirstOrDefault(e => e.Type == key);
        Assert.NotNull(ev);
        var cut = plan.Cuts.FirstOrDefault();
        Assert.NotNull(cut);
        using var surf = SKSurface.Create(new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var scratch = SKSurface.Create(new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul));
        var sx = scratch.Canvas;
        sx.Clear(SKColors.Black);
        using (var p = new SKPaint { Color = SKColors.White })
            sx.DrawRect(20, 20, 280, 140, p); // S 要有内容（帧差/边缘检测类 fx 读它）
        using var pool = new TmpPool();
        P11FxB.FxBegin(320, 180, 0.4, scratch, pool.Get, pool.Get, transparent: false);
        var env = Env.Make(surf.Canvas, plan, cut, plan.Style.Schemes.FirstOrDefault(),
            0.3, 1.0 / 24, 0.5, 0.5, null, "main", null, 1.0, 0.5, 0.5);
        env.It = new JObject();
        var ex = Record.Exception(() => def!.Draw!.Invoke(env, ev!));
        P11FxB.FxEnd();
        Assert.Null(ex);
    }

    public static IEnumerable<object[]> FxBKeyData() => FxBKeys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(FxBKeyData))]
    public void FxB_每条目_直调_Draw_不抛(string key) => DrawFxEntry(key, "fxB");

    public static IEnumerable<object[]> LooksFxKeyData() => LooksFxKeys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(LooksFxKeyData))]
    public void Looks_fx_每条目_直调_Draw_不抛(string key) => DrawFxEntry(key, "looks");

    // ---------------- looks：treat / bg / cam ----------------

    private static Env MakeEnv(Plan plan, SKCanvas ctx, double lt = 0.5)
    {
        var env = Env.Make(ctx, plan, plan.Cuts.FirstOrDefault(), plan.Style.Schemes.FirstOrDefault(),
            lt, 1.0 / 24, 0.5, 0.5, null, "main", null, 1.0, lt, lt);
        return env;
    }

    private static JObject MinimalItem() => new()
    {
        ["text"] = "字面テスト",
        ["size"] = 80.0,
        ["x"] = 160.0,
        ["y"] = 90.0,
        ["color"] = "#FFFFFF",
    };

    [Theory]
    [MemberData(nameof(LooksTreatKeyData))]
    public void Looks_treat_每条目_Draw_不抛(string key)
    {
        var def = Reg.Treat.GetValueOrDefault(key);
        Assert.NotNull(def);
        Assert.Equal("looks", def!.Pack);
        var proj = Project.NewDefault();
        proj.Lyrics = "夜明けの色を覚えてる";
        var plan = Planner.PreviewPlan(proj, "treat", key);
        Assert.Equal(key, plan.Cuts.FirstOrDefault()?.Treat);
        using var surf = SKSurface.Create(new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul));
        var env = MakeEnv(plan, surf.Canvas);
        env.It = MinimalItem();
        var ex = Record.Exception(() => def.Draw!.Invoke(env));
        Assert.Null(ex);
    }

    public static IEnumerable<object[]> LooksTreatKeyData() => LooksTreatKeys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(LooksBgKeyData))]
    public void Looks_bg_每条目_Draw_不抛(string key)
    {
        var def = Reg.Bg.GetValueOrDefault(key);
        Assert.NotNull(def);
        Assert.Equal("looks", def!.Pack);
        var proj = Project.NewDefault();
        proj.Lyrics = "夜明けの色を覚えてる";
        var plan = Planner.PreviewPlan(proj, "bg", key);
        Assert.Equal(key, plan.Cuts.FirstOrDefault()?.Bg);
        using var surf = SKSurface.Create(new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul));
        var env = MakeEnv(plan, surf.Canvas);
        var ex = Record.Exception(() => def.Draw!.Invoke(env));
        Assert.Null(ex);
    }

    public static IEnumerable<object[]> LooksBgKeyData() => LooksBgKeys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(LooksCamKeyData))]
    public void Looks_cam_每条目_Get_不抛且返回状态(string key)
    {
        var def = Reg.Camera.GetValueOrDefault(key);
        Assert.NotNull(def);
        Assert.Equal("looks", def!.Pack);
        var proj = Project.NewDefault();
        proj.Lyrics = "夜明けの色を覚えてる";
        var plan = Planner.PreviewPlan(proj, "cam", key);
        Assert.Equal(key, plan.Cuts.FirstOrDefault()?.Cam);
        using var surf = SKSurface.Create(new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul));
        var env = MakeEnv(plan, surf.Canvas, 0.3);
        CamState? cam = null;
        var ex = Record.Exception(() => cam = def.Get?.Invoke(env));
        Assert.Null(ex);
        Assert.NotNull(cam);
        Assert.True(cam!.S > 0, $"cam.{key} 返回非法缩放 {cam.S}");
    }

    public static IEnumerable<object[]> LooksCamKeyData() => LooksCamKeys.Select(k => new object[] { k });

    // ---------------- 全帧渲染冒烟（fxB 插件路径经 Renderer.Post 接通） ----------------

    [Fact]
    public void Renderer_帧内_非内置fx插件路径_不抛且有内容()
    {
        var proj = Project.NewDefault();
        proj.Lyrics = "夜明けの色を覚えてる";
        var plan = Planner.Plan(proj, null);
        // 注入非内置 fx 事件（走 Renderer.Post 的 FXE 插件段）
        plan.Events.Add(new PlanEvent { T = 1.0, Type = "radialChroma", Amp = 1, Dur = 0.5 });
        plan.Events.Add(new PlanEvent { T = 1.0, Type = "halftone", Amp = 1, Dur = 0.5 });
        using var renderer = new Renderer();
        using var surface = SKSurface.Create(new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul));
        var ex = Record.Exception(() => renderer.Frame(surface.Canvas, plan, 1.1));
        Assert.Null(ex);
        // 帧非空（与纯黑底差异像素 > 1%）
        using var img = surface.Snapshot();
        using var bmp = new SKBitmap(320, 180);
        Assert.True(img.ReadPixels(bmp.Info, bmp.GetPixels(), bmp.RowBytes, 0, 0));
        var buf = new byte[bmp.ByteCount];
        System.Runtime.InteropServices.Marshal.Copy(bmp.GetPixels(), buf, 0, buf.Length);
        var diff = 0;
        for (var i = 0; i + 3 < buf.Length; i += 4)
            if (buf[i] > 40 || buf[i + 1] > 40 || buf[i + 2] > 40)
                diff++;
        Assert.True(diff > 320 * 180 * 0.01, $"帧内容异常：差异像素 {diff}");
    }

    [Fact]
    public void Planner_用_reg键做plan_元数据齐全()
    {
        foreach (var k in FxBKeys)
        {
            var d = Reg.Fxe[k];
            Assert.True(d.W > 0, $"fx.{k} 缺权重");
            Assert.NotEqual(0, d.Amp);
            Assert.True(d.Dur > 0, $"fx.{k} 缺时长");
        }
        foreach (var k in LooksBgKeys)
        {
            var d = Reg.Bg[k];
            Assert.NotNull(d.Plan);
            Assert.NotNull(d.Draw);
        }
        foreach (var k in LooksCamKeys)
        {
            var d = Reg.Camera[k];
            // shakeHard 无 plan（JS 同）——其余都有
            if (k != "shakeHard") Assert.NotNull(d.Plan);
            Assert.NotNull(d.Get);
        }
        foreach (var k in LooksTreatKeys)
        {
            var d = Reg.Treat[k];
            // italic/wide/tall 无 plan（JS 同）——其余都有
            if (k is not ("italic" or "wide" or "tall")) Assert.NotNull(d.Plan);
            Assert.NotNull(d.Draw);
        }
    }
}
