using JizuraLive.Engine;
using SkiaSharp;
using Xunit;

namespace JizuraLive.Engine.Tests;

/// <summary>enter 表现包（11p_enter.js 移植）冒烟：注册数量/元数据/每条目可规划可绘制。</summary>
[Collection("EngineRegistry")]
public class PackEnterTests
{
    [Fact]
    public void Install_后EnterOrder含全部条目且元数据非空()
    {
        EngineBoot.InstallPacks();
        P11Enter.Install();

        Assert.True(Reg.Enter.Count >= 40, $"enter 条目数 {Reg.Enter.Count} < 40");
        foreach (var kv in Reg.Enter.Where(kv => kv.Value.Pack == "enter"))
        {
            Assert.False(string.IsNullOrEmpty(kv.Value.Name), $"enter.{kv.Key} 缺 name");
            Assert.NotNull(kv.Value.Draw);
        }
    }

    [Fact]
    public void 每个enter条目PreviewPlan并Draw不抛()
    {
        EngineBoot.InstallPacks();
        P11Enter.Install();
        var proj = Project.NewDefault();
        proj.Lyrics = "夜明けの色を覚えてる";
        var plan = Planner.Plan(proj, null);

        foreach (var kv in Reg.Enter.Where(kv => kv.Value.Pack == "enter").ToList())
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
