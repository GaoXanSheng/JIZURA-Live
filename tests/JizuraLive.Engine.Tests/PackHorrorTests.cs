// M5 horror 表现包（horror1/2/3）冒烟：注册契约 + planner/renderer 全链路。
// 1) Install 后三个包各自的组含其 key（pack = "horror1"/"horror2"/"horror3"，次序 = 源文件
//    J.register 出现序），幂等（二次 Install 不改注册表）；
// 2) 代表性条目（每包 ≥3 条）经 Planner.PreviewPlan + Renderer 一帧 320×180：不抛且非全空
//    （与 scheme bg 差异 > 40 的像素占比 > 0.4%）；Draw 另在直连 Env 下再跑一次——
//    Renderer.DrawCut 会吞掉 layout/decor 异常（只留日志），不能只靠整帧渲染判定；
// 3) Name 断言只查非空（全量引擎表面可能被 zh-labels 覆盖为简中）。
// 离屏面按宿主契约绝不 Dispose（Renderer 的帧缓存跨帧持有该画面的 SKImage 快照）。

using System.Globalization;
using System.Linq;
using JizuraLive.Engine;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace JizuraLive.Engine.Tests;

[Collection("EngineRegistry")]
public class PackHorrorTests
{
    private readonly ITestOutputHelper _out;

    public PackHorrorTests(ITestOutputHelper output) => _out = output;

    /// <summary>11p_horror1.js 的 J.register('layout', …) 出现序（12 条）。</summary>
    private static readonly string[] H1Layouts =
    {
        "hrFlashlight", "hrDoorGap", "hrWallScrawl", "hrCctv", "hrOuija", "hrMissing",
        "hrWrongOne", "hrRisingDark", "hrRedacted", "hrStaticTv", "hrSpiritPhoto", "hrWrongShadow",
    };

    /// <summary>11p_horror2.js 的 J.register 出现序（enter 7 / exit 7 / hold 4 / treat 4）。</summary>
    private static readonly string[] H2Enters =
    {
        "hrBlinkCreep", "hrJumpScare", "hrUneasy", "hrVhold", "hrMirrorSnap", "hrManifest", "hrClawReveal",
    };

    private static readonly string[] H2Exits =
    {
        "hrPulledDown", "hrLookBack", "hrTurnAway", "hrShiver", "hrSwallow", "hrFlickerDie", "hrDrain",
    };

    private static readonly string[] H2Holds = { "hrTwitch", "hrStare", "hrLagOne", "hrFlickerLight" };
    private static readonly string[] H2Treats = { "hrInkBleed", "hrEroded", "hrRedact", "hrDoubleExp" };

    /// <summary>11p_horror3.js 的注册出现序（decor 7 / bg 4 / cam 2 / fx 3 / trans 2 / style 3）。</summary>
    private static readonly string[] H3Decors =
    {
        "hrScratches", "hrSigil", "hrWatchEye", "hrStaticPatch", "hrDustBeam", "hrDrips", "hrCracks",
    };

    private static readonly string[] H3Bgs = { "hrFailingLamp", "hrCorridor", "hrMold", "hrDeadTrees" };
    private static readonly string[] H3Cams = { "hrNervous", "hrDutchSnap" };
    private static readonly string[] H3Fxs = { "hrSubliminal", "hrSignalLoss", "hrPassingShadow" };
    private static readonly string[] H3Trans = { "hrStaticCut", "hrBlink" };
    private static readonly string[] H3Styles = { "hrRuin", "hrNightRec", "hrCurse" };

    private static void InstallAll()
    {
        EngineBoot.Install();
        P11Horror1.Install();
        P11Horror2.Install();
        P11Horror3.Install();
    }

    [Fact]
    public void Install_Registers_All_Keys_In_Source_Order()
    {
        InstallAll();
        P11Horror1.Install();
        P11Horror2.Install();
        P11Horror3.Install(); // 幂等：二次调用不改注册表

        void CheckOrder<T>(string group, Dictionary<string, T> dict, List<string> order, string[] keys, string pack)
            where T : class, IRegEntry
        {
            var idx = keys.Select(k => order.IndexOf(k)).ToList();
            Assert.DoesNotContain(-1, idx);
            for (var i = 1; i < idx.Count; i++)
                Assert.True(idx[i] > idx[i - 1], $"order 次序漂移：{group}.{keys[i - 1]} → {group}.{keys[i]}");
            Assert.Equal(order.Count, order.Distinct().Count()); // 幂等不会把 key 追加第二遍
            foreach (var k in keys)
            {
                Assert.True(dict.ContainsKey(k), $"{group}.{k} 未注册");
                Assert.Equal(pack, dict[k].Pack);
                Assert.False(string.IsNullOrEmpty(dict[k].Name), $"{group}.{k} 缺 name");
            }
        }

        CheckOrder("layout", Reg.Layouts, Reg.LayoutOrder, H1Layouts, "horror1");
        CheckOrder("enter", Reg.Enter, Reg.EnterOrder, H2Enters, "horror2");
        CheckOrder("exit", Reg.Exit, Reg.ExitOrder, H2Exits, "horror2");
        CheckOrder("hold", Reg.Hold, Reg.HoldOrder, H2Holds, "horror2");
        CheckOrder("treat", Reg.Treat, Reg.TreatOrder, H2Treats, "horror2");
        CheckOrder("decor", Reg.Decor, Reg.DecorOrder, H3Decors, "horror3");
        CheckOrder("bg", Reg.Bg, Reg.BgOrder, H3Bgs, "horror3");
        CheckOrder("cam", Reg.Camera, Reg.CameraOrder, H3Cams, "horror3");
        CheckOrder("fx", Reg.Fxe, Reg.FxeOrder, H3Fxs, "horror3");
        CheckOrder("trans", Reg.Trans, Reg.TransOrder, H3Trans, "horror3");

        // horror1 元数据抽查（源文件字面量）
        Assert.True(Reg.Layouts["hrFlashlight"].Busy);
        Assert.Equal("safe", Reg.Layouts["hrCctv"].Treat);
        Assert.Equal(0.8, Reg.Layouts["hrOuija"].Portrait);
        Assert.True(Reg.Layouts["hrRedacted"].Fits!(10));
        Assert.False(Reg.Layouts["hrWrongOne"].Fits!(1));
        // horror2：时长函数抽查
        Assert.Equal(0.65, Reg.Enter["hrBlinkCreep"].InDur!(1.3, 4));
        Assert.Equal(0.8, Reg.Exit["hrPulledDown"].OutDur!(2.0, 4));
        // horror3：decor layer/subtle、cam strong、风格表
        Assert.Equal("front", Reg.Decor["hrScratches"].Layer);
        Assert.Equal("back", Reg.Decor["hrSigil"].Layer);
        Assert.True(Reg.Decor["hrSigil"].Subtle);
        Assert.True(Reg.Camera["hrDutchSnap"].Strong);
        foreach (var k in H3Styles)
        {
            Assert.True(Reg.Styles.ContainsKey(k), $"style.{k} 未注册");
            Assert.False(string.IsNullOrEmpty(Reg.Styles[k].Name));
            Assert.Equal(3, Reg.Styles[k].Schemes.Count);
            Assert.Equal("horror", (string?)Reg.Styles[k].Extra?["set"]);
            Assert.Contains(k, JStyles.StyleOrder);
        }
    }

    [Fact]
    public void Horror1_Layouts_PreviewPlan_And_320x180_Frame_Is_NonEmpty()
    {
        InstallAll();
        var project = Project.NewDefault();
        project.Lyrics = "夜明けの色を/覚えてる\nほどけた声が遠くで鳴った";
        using var renderer = new Renderer();

        // 面绝不 Dispose：宿主契约（引擎整帧缓存持有该画面的快照跨帧复用）
        var surface = SKSurface.Create(new SKImageInfo(320, 180));
        var canvas = surface.Canvas;
        var scale = 320.0 / Planner.DesignSize(project.Aspect).W;

        var fails = new List<string>();
        foreach (var key in H1Layouts)
        {
            Plan plan;
            try
            {
                plan = Planner.PreviewPlan(project, "layout", key);
            }
            catch (Exception e)
            {
                fails.Add($"{key}: PreviewPlan 抛 {e.GetType().Name}: {e.Message}");
                continue;
            }

            var cut = plan.Cuts[0];
            Assert.Equal(key, cut.Layout);
            var t = cut.Start + Math.Min(cut.Dur * 0.5, 0.9);
            var schemes = plan.Style.Schemes;
            var sc = schemes.Count > 0 ? schemes[0] : new Scheme();
            var bg = Env.SkColor(sc.Str("bg") ?? "#000000");

            try
            {
                canvas.Clear(SKColors.Transparent);
                renderer.Frame(canvas, plan, t, new RenderOpts { Scale = scale });
                canvas.Flush();
            }
            catch (Exception e)
            {
                fails.Add($"{key}: Frame 抛 {e.GetType().Name}: {e.Message}");
                continue;
            }

            // 非全空（在直调 Draw 之前量：hrFlashlight/hrDoorGap 自带整屏暗幕，直调会二次叠暗幕）
            if (!FrameHasContent(surface, bg, Env.SkColor(NightRef(sc)), out var ratio))
            {
                fails.Add($"{key}: 画面近乎空白（contentRatio={ratio.ToString("P2", CultureInfo.InvariantCulture)}）");
                continue;
            }

            _out.WriteLine($"horror1 {key}: contentRatio={ratio.ToString("P2", CultureInfo.InvariantCulture)}");

            // 直连 Env 再跑一次布局：Renderer.DrawCut 吞异常，只有这里能确认 Draw 本体不抛
            try
            {
                var env = Env.Make(canvas, plan, cut, sc, t - cut.Start, 0,
                    0.5, 0, null, "main", null, scale, t, t - cut.Start);
                Reg.Layouts[key].Draw!(env);
                canvas.Flush();
            }
            catch (Exception e)
            {
                fails.Add($"{key}: layout.Draw 抛 {e.GetType().Name}: {e.Message}");
            }
        }

        Assert.True(fails.Count == 0, "horror1 冒烟失败：\n" + string.Join("\n", fails));
    }

    /// <summary>nightC(sc)（horror 布局的整屏暗幕色）：bg 混向纯黑。JS J.mix(sc.bg, '#000000', …)。</summary>
    private static string NightRef(Scheme sc)
    {
        var bg = sc.Str("bg") ?? "#000000";
        return J.Mix(bg, "#000000", J.Lum(bg) < 0.45 ? 0.72 : 0.9);
    }

    [Fact]
    public void Horror2_Moves_PreviewPlan_DirectDraw_NonEmpty()
    {
        InstallAll();
        var project = Project.NewDefault();
        project.Lyrics = "夜明けの色を/覚えてる\nほどけた声が遠くで鳴った";
        using var renderer = new Renderer();
        var surface = SKSurface.Create(new SKImageInfo(320, 180));
        var canvas = surface.Canvas;
        var scale = 320.0 / Planner.DesignSize(project.Aspect).W;

        // 代表性条目：enter / exit / hold / treat 各至少一条
        var cases = new (string Group, string Key)[]
        {
            ("enter", "hrVhold"),
            ("enter", "hrManifest"),
            ("exit", "hrPulledDown"),
            ("exit", "hrSwallow"),
            ("hold", "hrTwitch"),
            ("treat", "hrInkBleed"),
            ("treat", "hrRedact"),
        };

        var fails = new List<string>();
        foreach (var (group, key) in cases)
        {
            Plan plan;
            try
            {
                plan = Planner.PreviewPlan(project, group, key);
            }
            catch (Exception e)
            {
                fails.Add($"{group}.{key}: PreviewPlan 抛 {e.GetType().Name}: {e.Message}");
                continue;
            }

            var cut = plan.Cuts[0];
            if (group == "enter") Assert.Equal(key, cut.Enter);
            if (group == "exit") Assert.Equal(key, cut.Exit);
            if (group == "hold") Assert.Equal(key, cut.Hold);
            if (group == "treat") Assert.Equal(key, cut.Treat);
            var t = cut.Start + Math.Min(cut.Dur * 0.5, 0.9);
            var schemes = plan.Style.Schemes;
            var sc = schemes.Count > 0 ? schemes[0] : new Scheme();
            var bg = Env.SkColor(sc.Str("bg") ?? "#000000");

            try
            {
                canvas.Clear(SKColors.Transparent);
                renderer.Frame(canvas, plan, t, new RenderOpts { Scale = scale });
                canvas.Flush();
            }
            catch (Exception e)
            {
                fails.Add($"{group}.{key}: Frame 抛 {e.GetType().Name}: {e.Message}");
                continue;
            }

            // 非全空（在直调 Draw 之前量，直调只在验不抛）
            if (!FrameHasContent(surface, bg, Env.SkColor(NightRef(sc)), out var ratio))
            {
                fails.Add($"{group}.{key}: 画面近乎空白（contentRatio={ratio.ToString("P2", CultureInfo.InvariantCulture)}）");
                continue;
            }

            _out.WriteLine($"horror2 {group}.{key}: contentRatio={ratio.ToString("P2", CultureInfo.InvariantCulture)}");

            // 直调 Draw：MoveDef/TreatDef 用一个最小 item（mainDraw 流水线外的直连验证）
            try
            {
                var env = Env.Make(canvas, plan, cut, sc, t - cut.Start, 0,
                    0.5, 0, null, "main", null, scale, t, t - cut.Start);
                env.It = new Newtonsoft.Json.Linq.JObject
                {
                    ["text"] = "字面",
                    ["font"] = "gothic_med",
                    ["size"] = 64.0,
                    ["x"] = 160.0,
                    ["y"] = 90.0,
                    ["color"] = sc.Str("fg") ?? "#FFFFFF",
                    ["seed"] = 123.0,
                    ["mi"] = 0.0,
                };
                env.Ap = 0.5;
                if (group == "enter") Reg.Enter[key].Draw!(env);
                else if (group == "exit") Reg.Exit[key].Draw!(env);
                else if (group == "hold") Reg.Hold[key].Draw!(env);
                else Reg.Treat[key].Draw!(env);
                canvas.Flush();
            }
            catch (Exception e)
            {
                fails.Add($"{group}.{key}: Draw 抛 {e.GetType().Name}: {e.Message}");
            }
        }

        Assert.True(fails.Count == 0, "horror2 冒烟失败：\n" + string.Join("\n", fails));
    }

    [Fact]
    public void Horror3_DecorBgCamTrans_PreviewPlan_DirectDraw_NonEmpty()
    {
        InstallAll();
        var project = Project.NewDefault();
        project.Lyrics = "夜明けの色を/覚えてる\nほどけた声が遠くで鳴った";
        using var renderer = new Renderer();
        var surface = SKSurface.Create(new SKImageInfo(320, 180));
        var canvas = surface.Canvas;
        var scale = 320.0 / Planner.DesignSize(project.Aspect).W;

        // 代表性条目：decor / bg / cam / trans 各至少一条（fx 的整帧合成路径见包头 TODO，注册即验）
        var frameCases = new (string Group, string Key)[]
        {
            ("decor", "hrScratches"),
            ("decor", "hrDrips"),
            ("bg", "hrCorridor"),
            ("bg", "hrFailingLamp"),
            ("cam", "hrNervous"),
            ("trans", "hrStaticCut"),
            ("trans", "hrBlink"),
        };

        var fails = new List<string>();
        foreach (var (group, key) in frameCases)
        {
            Plan plan;
            try
            {
                plan = Planner.PreviewPlan(project, group, key);
            }
            catch (Exception e)
            {
                fails.Add($"{group}.{key}: PreviewPlan 抛 {e.GetType().Name}: {e.Message}");
                continue;
            }

            var t = group == "trans" ? plan.Cuts[^1].Start + 0.6 : plan.Cuts[0].Start + 0.9;
            var cut = group == "trans" ? plan.Cuts[^1] : plan.Cuts[0];
            var schemes = plan.Style.Schemes;
            var sc = schemes.Count > 0 ? schemes[0] : new Scheme();
            var bg = Env.SkColor(sc.Str("bg") ?? "#000000");

            try
            {
                canvas.Clear(SKColors.Transparent);
                renderer.Frame(canvas, plan, t, new RenderOpts { Scale = scale });
                canvas.Flush();
            }
            catch (Exception e)
            {
                fails.Add($"{group}.{key}: Frame 抛 {e.GetType().Name}: {e.Message}");
                continue;
            }

            // 非全空（在直调 Draw 之前量，直调只在验不抛）
            if (!FrameHasContent(surface, bg, Env.SkColor(NightRef(sc)), out var ratio))
            {
                fails.Add($"{group}.{key}: 画面近乎空白（contentRatio={ratio.ToString("P2", CultureInfo.InvariantCulture)}）");
                continue;
            }

            _out.WriteLine($"horror3 {group}.{key}: contentRatio={ratio.ToString("P2", CultureInfo.InvariantCulture)}");

            // 直调 Draw/Get：Renderer 对 decor/背景吞异常，直连才能确认本体不抛
            try
            {
                var env = Env.Make(canvas, plan, cut, sc, t - cut.Start, 0,
                    0.5, 0, null, "main", null, scale, t, t - cut.Start);
                if (group == "decor")
                    Reg.Decor[key].Draw!(env, new DecorSpec { Id = key, Seed = 7, N = 2, V = 1, Big = true, Right = true, Low = true });
                else if (group == "bg") Reg.Bg[key].Draw!(env);
                else if (group == "cam") Assert.NotNull(Reg.Camera[key].Get!(env));
                else if (group == "trans")
                {
                    // 转场直调：两块实心 SKImage 走 p=0.5
                    using var ia = MakeSolidImage(64, 48, new SKColor(200, 60, 60));
                    using var ib = MakeSolidImage(64, 48, new SKColor(60, 60, 200));
                    (Reg.Trans[key].Draw as TransDrawFn)!.Invoke(canvas, ia, ib, 0.5,
                        new TransCtx { Cw = 64, Ch = 48, Sc = sc, St = plan.Style, Step = 3, T = t });
                }

                canvas.Flush();
            }
            catch (Exception e)
            {
                fails.Add($"{group}.{key}: Draw/Get 抛 {e.GetType().Name}: {e.Message}");
            }
        }

        // fx 三条：元数据契约（draw 的整帧合成路径 Renderer 未定型，见 P11Horror3 头注 TODO）
        foreach (var k in H3Fxs)
        {
            Assert.True(Reg.Fxe.ContainsKey(k));
            Assert.False(string.IsNullOrEmpty(Reg.Fxe[k].Name));
        }
        Assert.Equal(2, Reg.Fxe["hrSubliminal"].Dur);
        Assert.Equal(3, Reg.Fxe["hrSignalLoss"].Pre);
        Assert.True(Reg.Fxe["hrSignalLoss"].Glitchy);
        Assert.True(Reg.Fxe["hrPassingShadow"].Mid);

        Assert.True(fails.Count == 0, "horror3 冒烟失败：\n" + string.Join("\n", fails));
    }

    private static SKImage MakeSolidImage(int w, int h, SKColor color)
    {
        // Snapshot 独立持有像素，surface 释放后仍可用
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(color);
        return surface.Snapshot();
    }

    private static bool FrameHasContent(SKSurface surface, SKColor bg, SKColor? bg2, out double ratio)
    {
        double content = 0, total = 0;
        using var img = surface.Snapshot();
        using var px = img.PeekPixels();
        for (var y = 0; y < px.Height; y += 2)
            for (var x = 0; x < px.Width; x += 2)
            {
                var c = px.GetPixelColor(x, y);
                total++;
                var d = Math.Max(Math.Max(Math.Abs(c.Red - bg.Red), Math.Abs(c.Green - bg.Green)),
                    Math.Abs(c.Blue - bg.Blue));
                if (bg2 is { } b2)
                {
                    var d2 = Math.Max(Math.Max(Math.Abs(c.Red - b2.Red), Math.Abs(c.Green - b2.Green)),
                        Math.Abs(c.Blue - b2.Blue));
                    d = Math.Max(d, d2);
                }

                if (d > 40) content++;
            }

        ratio = total > 0 ? content / total : 0;
        return ratio > 0.004;
    }

}
