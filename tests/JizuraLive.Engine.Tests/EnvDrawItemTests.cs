using System.Linq;
using JizuraLive.Engine;
using Newtonsoft.Json.Linq;
using SkiaSharp;
using Xunit;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 逐字动画只作用于 mainDraw 正在画的那个 item：布局自绘的附加文字（kicker / deck / 装饰标签）
/// 不该继承它。
/// 回归背景（实测日志）：env.CharFn 是帧级残留，布局在 MainDraw 之后 env.DrawItem(新 item) 会连带
/// 吃到上一个 item 的逐字回调；而手法回调是按「主文字的词几何」索引的（g0.Map[i]），字数一变就
/// IndexOutOfRange——knBeatLean + headlineDeck / windowChrome 每帧抛（5s 内 45 次），
/// 整段文字都画不出来（表象就是「布局有问题」）。JS 侧 it.charFns 挂在 item 上，天生不会串。
/// </summary>
public class EnvDrawItemTests
{
    [Fact]
    public void 布局自绘的附加文字不吃主文字的逐字动画()
    {
        EngineBoot.InstallPacks();
        P11LayoutsC.Install();
        P11Kinetic2.Install();
        var proj = Project.NewDefault();
        proj.Lyrics = "夜明けの色を覚えてる";
        var plan = Planner.PreviewPlan(proj, "layout", "headlineDeck");
        var cut = plan.Cuts[0];
        cut.Hold = "knBeatLean"; // 逐字回调按主文字的词几何索引：附加 item 字数不同就出界
        var sc = plan.Style.Schemes.FirstOrDefault() ?? new Scheme();
        var surface = SKSurface.Create(new SKImageInfo(640, 360)); // 面绝不 Dispose（宿主契约）
        var scale = 640.0 / plan.W;
        surface.Canvas.Save();
        surface.Canvas.Scale((float)scale);
        var lt = System.Math.Max(cut.InDur, 0.8);
        var env = Env.Make(surface.Canvas, plan, cut, sc, lt, 1.0 / 24, 0.5, 0, null, "main", null,
            scale, lt, lt);
        // 布局内部先 MainDraw 主文字（注册逐字回调），随后 env.DrawItem 画 kicker/deck——
        // 旧实现这里继承主文字的 CharFn，越界抛 IndexOutOfRange。
        var ex = Record.Exception(() => Reg.Layouts["headlineDeck"].Draw!(env));
        surface.Canvas.Restore();
        Assert.Null(ex);
    }

    [Fact]
    public void 主文字自己仍然吃到逐字动画()
    {
        // 反向红线：修「附加文字不串动画」不能把主文字自己的动画也修没了
        EngineBoot.InstallPacks();
        P11Kinetic2.Install();
        var proj = Project.NewDefault();
        proj.Lyrics = "夜明けの色を覚えてる";
        var plan = Planner.PreviewPlan(proj, "hold", "knBeatLean");
        var cut = plan.Cuts[0];
        var sc = plan.Style.Schemes.FirstOrDefault() ?? new Scheme();
        var surface = SKSurface.Create(new SKImageInfo(640, 360));
        var scale = 640.0 / plan.W;
        surface.Canvas.Save();
        surface.Canvas.Scale((float)scale);
        var lt = System.Math.Max(cut.InDur, 0.8);
        var env = Env.Make(surface.Canvas, plan, cut, sc, lt, 1.0 / 24, 0.5, 0, null, "main", null,
            scale, lt, lt);
        var it = new JObject
        {
            ["text"] = "夜明けの色を覚えてる",
            ["font"] = "gothic_black",
            ["size"] = System.Math.Max(48.0, plan.H * 0.09),
            ["x"] = plan.W / 2.0,
            ["y"] = plan.H / 2.0,
            ["track"] = 0.02,
            ["color"] = sc.Str("fg"),
            ["mi"] = 0.0,
            ["hold"] = "knBeatLean",
        };
        var ex = Record.Exception(() => JLayouts.MainDraw(env, it));
        var applied = env.CharFn != null; // mainDraw 合并后的逐字函数
        surface.Canvas.Restore();
        Assert.Null(ex);
        Assert.True(applied, "主文字没拿到 hold 的逐字函数（动画被误伤）");
    }
}
