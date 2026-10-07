using JizuraLive.Engine;
using SkiaSharp;
using Xunit;

namespace JizuraLive.Engine.Tests;

/// <summary>exit/hold 表现包（11p_exit.js / 11p_exitB.js 移植）冒烟：注册数量/元数据/每条目可规划可绘制。</summary>
[Collection("EngineRegistry")]
public class PackExitTests
{
    /// <summary>JS 源里定义了 outDur 的 exit 条目（10p_exit 两文件逐条核对）。</summary>
    private static readonly string[] ExitOutDurKeys =
    {
        // 11p_exit.js
        "foldOut", "undraw", "vSliceDrop", "melt", "backspace", "gravity", "burn", "sweepCover",
        // 11p_exitB.js
        "peelOff", "crumpleOut", "tearOut", "scorchOut", "eraserOut", "sandOut", "shredOut",
        "dominoOut", "hingeOut", "rocketOff", "bounceOff", "balloonOff", "deflateOut", "glassBreak",
        "slotOut", "matrixOut", "tornadoOut", "rollUpOut", "snakeOut", "flutterOut", "rollOff",
        "fanClose", "floodOut", "slashOut", "scribbleOut", "candleOut",
    };

    /// <summary>JS 源里定义了 minDur 的 exit 条目。</summary>
    private static readonly string[] ExitMinDurKeys =
    {
        "melt", "backspace",
        "crumpleOut", "eraserOut", "sandOut", "shredOut", "dominoOut", "hingeOut", "rocketOff",
        "bounceOff", "balloonOff", "tornadoOut", "snakeOut", "flutterOut", "rollOff", "fanClose",
    };

    [Fact]
    public void Install_后ExitOrder含全部条目且元数据非空()
    {
        EngineBoot.InstallPacks();
        P11Exit.Install();
        P11ExitB.Install();

        Assert.True(Reg.Exit.Count >= 36 + 39, $"exit 条目数 {Reg.Exit.Count} < 75");
        Assert.True(Reg.Hold.Count >= 20 + 12, $"hold 条目数 {Reg.Hold.Count} < 32");

        foreach (var pack in new[] { "exit", "exitB" })
        {
            var exits = Reg.Exit.Where(kv => kv.Value.Pack == pack).ToList();
            Assert.Equal(pack == "exit" ? 36 : 39, exits.Count);
            foreach (var kv in exits)
            {
                Assert.Contains(kv.Key, Reg.ExitOrder);
                Assert.False(string.IsNullOrEmpty(kv.Value.Name), $"exit.{kv.Key} 缺 name");
                Assert.NotNull(kv.Value.Draw);
                Assert.Equal(ExitOutDurKeys.Contains(kv.Key), kv.Value.OutDur != null);
                Assert.Equal(ExitMinDurKeys.Contains(kv.Key), kv.Value.MinDur != null);
            }

            var holds = Reg.Hold.Where(kv => kv.Value.Pack == pack).ToList();
            Assert.Equal(pack == "exit" ? 20 : 12, holds.Count);
            foreach (var kv in holds)
            {
                Assert.Contains(kv.Key, Reg.HoldOrder);
                Assert.False(string.IsNullOrEmpty(kv.Value.Name), $"hold.{kv.Key} 缺 name");
                Assert.NotNull(kv.Value.Draw);
            }
        }
    }

    [Fact]
    public void 每个exit条目PreviewPlan并Draw不抛()
    {
        EngineBoot.InstallPacks();
        P11Exit.Install();
        P11ExitB.Install();
        var proj = Project.NewDefault();
        proj.Lyrics = "夜明けの色を覚えてる";

        foreach (var pack in new[] { "exit", "exitB" })
        {
            foreach (var kv in Reg.Exit.Where(kv => kv.Value.Pack == pack).ToList())
            {
                var p2 = Planner.PreviewPlan(proj, "exit", kv.Key);
                Assert.NotNull(p2);
                var cut = p2.Cuts.FirstOrDefault();
                Assert.NotNull(cut);
                using var surf = SKSurface.Create(new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul));
                var env = Env.Make(surf.Canvas, p2, cut, p2.Style.Schemes.FirstOrDefault(), 0.3, 1.0 / 24, 0.5, 0.5, null, "main", null, 1.0);
                // 真实管线里 Draw 由 item 流水线调用、env.It 是当前 item；直调时给最小 item（键缺省走默认值）
                env.It = new Newtonsoft.Json.Linq.JObject();
                env.Ap = 0.5; // 出场动画中段
                var ex = Record.Exception(() => kv.Value.Draw?.Invoke(env));
                Assert.Null(ex);
            }
        }
    }

    [Fact]
    public void 每个hold条目PreviewPlan并Draw不抛()
    {
        EngineBoot.InstallPacks();
        P11Exit.Install();
        P11ExitB.Install();
        var proj = Project.NewDefault();
        proj.Lyrics = "夜明けの色を覚えてる";

        foreach (var pack in new[] { "exit", "exitB" })
        {
            foreach (var kv in Reg.Hold.Where(kv => kv.Value.Pack == pack).ToList())
            {
                var p2 = Planner.PreviewPlan(proj, "hold", kv.Key);
                Assert.NotNull(p2);
                var cut = p2.Cuts.FirstOrDefault();
                Assert.NotNull(cut);
                using var surf = SKSurface.Create(new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul));
                var env = Env.Make(surf.Canvas, p2, cut, p2.Style.Schemes.FirstOrDefault(), 0.9, 1.0 / 24, 0.5, 0.5, null, "main", null, 1.0);
                env.It = new Newtonsoft.Json.Linq.JObject();
                env.Ap = 0.6; // hold 的 amt
                var ex = Record.Exception(() => kv.Value.Draw?.Invoke(env));
                Assert.Null(ex);
            }
        }
    }
}
