using JizuraLive.Engine;
using SkiaSharp;
using Xunit;

namespace JizuraLive.Engine.Tests;

/// <summary>enterB 表现包（11p_enterB.js 移植，第二批 47 条）冒烟：注册数量/元数据/每条目可规划可绘制。</summary>
[Collection("EngineRegistry")]
public class PackEnterBTests
{
    /// <summary>JS 源 DEFS 字面量序（注册顺序 = 抽取候选顺序，不能变）。</summary>
    private static readonly string[] Keys =
    {
        "springIn", "pendulum", "rollIn", "slingshot", "rockSettle", "bounceBall", "snapRail",
        "fanOpen", "cylinder", "shuffle", "stopMotion", "ripple", "zipper", "zoomAlt", "tiltUp",
        "stickerPeel", "crumple", "noteUnfold", "tornJoin", "splitFlap", "overexpose", "glint",
        "loupe", "filmFeed", "backlight", "lightLeak", "heatHaze", "crtOn", "interlace",
        "loadingBar", "dither", "odometer", "matrixRain", "hatchFill", "brushReveal", "inkDrop",
        "quarters", "invertBox", "printRegister", "echoCount", "liquidFill", "windBlown",
        "strokeOrder", "clockWipe", "shadowFirst", "bubbles", "tokoroten",
    };

    [Fact]
    public void Install_后EnterOrder含全部enterB条目且元数据非空()
    {
        EngineBoot.InstallPacks();
        P11EnterB.Install();
        P11EnterB.Install(); // 幂等：重复调用不得追加/替换

        Assert.Equal(Keys.Length, Reg.Enter.Count(kv => kv.Value.Pack == "enterB"));
        for (var i = 0; i < Keys.Length; i++)
        {
            var k = Keys[i];
            Assert.True(Reg.Enter.ContainsKey(k), $"enterB.{k} 未注册");
            Assert.True(Reg.EnterOrder.Contains(k), $"enterB.{k} 不在 EnterOrder");
            if (i > 0)
                // 顺序 = JS DEFS 字面量序（全局 order 里排在其他包之后，断言相对次序）
                Assert.True(Reg.EnterOrder.IndexOf(Keys[i - 1]) < Reg.EnterOrder.IndexOf(k),
                    $"enterB.{k} 的注册次序不对");
            var def = Reg.Enter[k];
            Assert.Equal("enterB", def.Pack);
            Assert.False(string.IsNullOrEmpty(def.Name), $"enterB.{k} 缺 name");
            Assert.NotNull(def.Draw);
            // 有 inDur 的条目：函数可调用（对位 JS minDur/maxChars 已逐条对上 MoveDef 字段）
            if (def.InDur != null) Assert.Equal(def.InDur(1.0, 5), def.InDur(1.0, 5));
        }
    }

    [Fact]
    public void 每个enterB条目PreviewPlan并Draw不抛()
    {
        EngineBoot.InstallPacks();
        P11EnterB.Install();
        var proj = Project.NewDefault();
        proj.Lyrics = "夜明けの色を覚えてる";
        var plan = Planner.Plan(proj, null);

        foreach (var kv in Reg.Enter.Where(kv => kv.Value.Pack == "enterB").ToList())
        {
            var p2 = Planner.PreviewPlan(proj, "enter", kv.Key);
            Assert.NotNull(p2);
            var cut = p2.Cuts.FirstOrDefault();
            Assert.NotNull(cut);
            using var surf = SKSurface.Create(new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul));
            var env = Env.Make(surf.Canvas, p2, cut, p2.Style.Schemes.FirstOrDefault(), 0.3, 1.0 / 24, 0.5, 0.5, null, "main", null, 1.0);
            // 真实管线里 Draw 由 item 流水线调用、env.It 是当前 item；直调时给最小 item（键缺省走默认值）
            env.It = new Newtonsoft.Json.Linq.JObject();
            var ex = Record.Exception(() => kv.Value.Draw?.Invoke(env));
            Assert.Null(ex);
        }
    }
}
