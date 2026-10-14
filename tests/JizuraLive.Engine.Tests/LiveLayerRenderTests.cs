// 实时层合成接线回归：
//   · 基本接线：同一 plan、同一时刻渲染两帧——不带实时层 / 带一块纯品红实时层，断言两帧不同且
//     多出来的像素正是实时层的颜色（DrawLive 一旦被挪到歌词之后、或整帧缓存把实时层缓存住，立刻红）；
//   · 多路：两块不同色的实时层按槽位顺序合成，两层都要看得见；
//   · 跟随镜头（PV 模式）：注册一个确定性的假相机（S=1.05、无旋转/无偏移），断言开跟随之后
//     实时层的落位真的被镜头矩阵挪动了——这条钉住「源与歌词共享同一台摄影机」。
//
// 用假相机而不是内置相机：内置相机（push/earthquake 等）的幅度小且随机相位，断言只能写得很松；
// 常量相机让「挪了多少」可精确断言。注册进 Reg 的是全局状态，故归入 EngineRegistry 串行集合。

using JizuraLive.Engine;
using SkiaSharp;
using Xunit;

namespace JizuraLive.Engine.Tests;

[Collection("EngineRegistry")]
public class LiveLayerRenderTests
{
    [Fact]
    public void Frame_With_LiveLayer_Draws_Layer_Color()
    {
        var plan = MakePlan();
        var t = MidCutTime(plan);
        var info = new SKImageInfo(plan.W, plan.H);

        using var renderer = new Renderer();
        using var plain = SKSurface.Create(info);
        renderer.Frame(plain.Canvas, plan, t, new RenderOpts { Scale = 1 });

        using var magenta = SolidBitmap(new SKColor(255, 0, 255, 255));
        using var live = SKSurface.Create(info);
        renderer.Frame(live.Canvas, plan, t, new RenderOpts
        {
            Scale = 1,
            LiveFollowCamera = false,
            Live = [LiveLayer.From(new LiveLayerOptions { Alpha = 1, Scale = 1 }, magenta)],
        });

        using var a = plain.Snapshot();
        using var b = live.Snapshot();
        var (magentaPlain, magentaLive, diff) = Compare(a, b, plan.W, plan.H);

        // 品红像素占比：不带层时基本为 0（工程风格不用这个色），带层时铺满画幅的一大块
        Assert.True(magentaLive > 0.20, $"带实时层的帧里品红像素只有 {magentaLive:P1}（层没画上？）");
        Assert.True(magentaPlain < 0.02, $"不带实时层的帧里就有 {magentaPlain:P1} 品红（判据失效）");
        Assert.True(diff > 0.20, $"两帧差异只有 {diff:P1}（实时层没进画面）");
    }

    [Fact]
    public void Frame_With_Two_LiveLayers_Draws_Both()
    {
        var plan = MakePlan();
        var t = MidCutTime(plan);

        using var renderer = new Renderer();
        using var magenta = SolidBitmap(new SKColor(255, 0, 255, 255));
        using var cyan = SolidBitmap(new SKColor(0, 255, 255, 255));

        // 基线：不带实时层的同一帧（判据相对基线，免得被工程风格/后期吃掉的比例写死）
        var (m0, c0) = CountColors(renderer, plan, t, live: null);

        // 第二路刻意偏左：这一拍的歌词/装饰正压着画面中央，居中放的小层会被完全盖住（实测 0%）
        var (m1, c1) = CountColors(renderer, plan, t, live:
        [
            LiveLayer.From(new LiveLayerOptions { Alpha = 1, Scale = 1 }, magenta),                        // 下层：铺满
            LiveLayer.From(new LiveLayerOptions { Alpha = 1, Scale = 0.3, OffsetX = -0.3 }, cyan),        // 上层：左侧小块
        ]);

        Assert.True(m1 - m0 > 0.15, $"下层品红缺席（基线 {m0:P1} → {m1:P1}）");
        Assert.True(c1 - c0 > 0.008, $"上层青色缺席（基线 {c0:P1} → {c1:P1}，多路合成只画了第一路？）");
        // 下层铺满、上层只是小块：品红的可见增量必须明显更大（顺序错了就是反过来）
        Assert.True(m1 - m0 > (c1 - c0) * 3, $"层序可疑：品红增量 {m1 - m0:P1}、青增量 {c1 - c0:P1}");
    }

    [Fact]
    public void LiveLayer_Sits_Above_The_Pv_Picture()
    {
        // 层序契约：PV 画面（底衬 + 背景定义 + 歌词/装饰）在人物**下面**，人物之上才是字幕（调用方烤）。
        // 判据：满幅实时层的可见占比要接近它的几何占比（contain 后 ~56% 画布）——被歌词/装饰压住的
        // 话会明显更低（改前实测 ~32%）。这条一红就说明 DrawLive 又被挪回 cut 内容之前了。
        var plan = MakePlan();
        var t = MidCutTime(plan);

        using var renderer = new Renderer();
        using var magenta = SolidBitmap(new SKColor(255, 0, 255, 255));
        var (m0, _) = CountColors(renderer, plan, t, live: null);
        var (m1, _) = CountColors(renderer, plan, t, live:
        [
            LiveLayer.From(new LiveLayerOptions { Alpha = 1, Scale = 1 }, magenta),
        ]);

        Assert.True(m1 - m0 > 0.45, $"实时层可见占比只有 {m1 - m0:P1}（应接近几何占比 ~56%：是不是被 PV 画面压住了？）");
    }

    [Fact]
    public void FrameCamera_Moves_Whole_Composite()
    {
        // 「整幅一起吃镜头」：FrameCamera 开着时，即使 LiveFollowCamera=false（不走逐层跟随），
        // 实时层也会因为整帧镜头而位移——这是图层顺序之外的另一条契约。
        Reg.Register("cam", PvCamKey, new CamDef { Key = PvCamKey, Name = "PV 测试相机", Get = _ => new CamState { X = 120 } });

        var plan = MakePlan();
        foreach (var cut in plan.Cuts) cut.Cam = PvCamKey;
        var t = MidCutTime(plan);

        using var renderer = new Renderer();
        using var magenta = SolidBitmap(new SKColor(255, 0, 255, 255));

        var off = MagentaCenterX(renderer, plan, t, magenta, follow: false, frameCam: false);
        var on = MagentaCenterX(renderer, plan, t, magenta, follow: false, frameCam: true);

        Assert.True(Math.Abs(off - plan.W / 2.0) < 12, $"不开整帧镜头时实时层没钉在画布中心：{off:F1}");
        Assert.True(on - off > 80, $"整帧镜头没生效（重心 {off:F1} → {on:F1}，应右移 ~120px）");
    }

    [Fact]
    public void Live_Follows_Camera_When_Enabled()
    {
        // 假的确定相机：只做水平平移 120 设计像素。为什么不用缩放当探针：Skia 的
        // translate→scale→translate 复合是「绕画布中心缩放」，缩放不移动重心，量不出来；
        // 平移则是干净的一比一（实测重心 958.5 → 1078.5，正好 +120）。
        Reg.Register("cam", PvCamKey, new CamDef { Key = PvCamKey, Name = "PV 测试相机", Get = _ => new CamState { X = 120 } });

        var plan = MakePlan();
        foreach (var cut in plan.Cuts) cut.Cam = PvCamKey; // 之后不会重规划，改 plan 即改相机
        var t = MidCutTime(plan);

        using var renderer = new Renderer();
        using var magenta = SolidBitmap(new SKColor(255, 0, 255, 255));

        var fixedX = MagentaCenterX(renderer, plan, t, magenta, follow: false);
        var followX = MagentaCenterX(renderer, plan, t, magenta, follow: true);

        Assert.True(Math.Abs(fixedX - plan.W / 2.0) < 12, $"不跟随时实时层没钉在画布中心：{fixedX:F1}");
        Assert.True(followX - fixedX > 80, $"跟随镜头没生效（重心 {fixedX:F1} → {followX:F1}，应右移 ~120px）");
    }

    private const string PvCamKey = "pvTestCam";

    private static Plan MakePlan()
    {
        EngineBoot.Install();
        var project = Project.NewDefault();
        project.Lyrics = "夜明けの色を/覚えてる\nほどけた声が遠くで鳴った";
        var plan = Planner.Plan(project, null);
        Assert.True(plan.Cuts.Count > 0);
        return plan;
    }

    /// <summary>第一个 cut 的中点（t 落在 cut 里才有歌词/镜头；默认工程的开头有留白，取 1.2s 会掉到 cut 外）。</summary>
    private static double MidCutTime(Plan plan)
    {
        var c0 = plan.Cuts[0];
        var t = (c0.Start + c0.End) / 2;
        Assert.NotNull(Planner.CutAt(plan, t));
        return t;
    }

    /// <summary>统计一帧里品红/青色的占比（<paramref name="live"/> 为空 = 基线帧）。</summary>
    private static (double Magenta, double Cyan) CountColors(Renderer renderer, Plan plan, double t,
        List<LiveLayer>? live)
    {
        using var surf = SKSurface.Create(new SKImageInfo(plan.W, plan.H));
        renderer.Frame(surf.Canvas, plan, t, new RenderOpts
        {
            Scale = 1,
            LiveFollowCamera = false,
            Live = live,
        });

        using var img = surf.Snapshot();
        using var px = img.PeekPixels();
        long m = 0, c = 0, total = 0;
        for (var y = 0; y < plan.H; y += 3)
            for (var x = 0; x < plan.W; x += 3)
            {
                var col = px.GetPixelColor(x, y);
                total++;
                if (IsMagenta(col)) m++;
                if (IsCyan(col)) c++;
            }

        return (m / (double)total, c / (double)total);
    }

    /// <summary>渲染一帧带单路实时层的画面，返回品红区域的横向重心（无品红返回 NaN）。</summary>
    private static double MagentaCenterX(Renderer renderer, Plan plan, double t, SKBitmap layer, bool follow,
        bool frameCam = false)
    {
        using var surf = SKSurface.Create(new SKImageInfo(plan.W, plan.H));
        renderer.Frame(surf.Canvas, plan, t, new RenderOpts
        {
            Scale = 1,
            LiveFollowCamera = follow,
            FrameCamera = frameCam,
            Live = [LiveLayer.From(new LiveLayerOptions { Alpha = 1, Scale = 1 }, layer)],
        });

        using var img = surf.Snapshot();
        using var px = img.PeekPixels();
        long sum = 0, n = 0;
        for (var y = 0; y < plan.H; y += 3)
            for (var x = 0; x < plan.W; x += 3)
                if (IsMagenta(px.GetPixelColor(x, y)))
                {
                    sum += x;
                    n++;
                }

        return n == 0 ? double.NaN : sum / (double)n;
    }

    private static SKBitmap SolidBitmap(SKColor color)
    {
        var bmp = new SKBitmap(new SKImageInfo(64, 64, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        using var c = new SKCanvas(bmp);
        c.Clear(color);
        return bmp;
    }

    /// <summary>逐像素比较：返回 (无层帧的品红占比, 有层帧的品红占比, 两帧差异占比)。</summary>
    private static (double Plain, double Live, double Diff) Compare(SKImage a, SKImage b, int w, int h)
    {
        using var pa = a.PeekPixels();
        using var pb = b.PeekPixels();
        long mp = 0, ml = 0, diff = 0, total = 0;
        for (var y = 0; y < h; y += 3)
            for (var x = 0; x < w; x += 3)
            {
                var ca = pa.GetPixelColor(x, y);
                var cb = pb.GetPixelColor(x, y);
                total++;
                if (IsMagenta(ca)) mp++;
                if (IsMagenta(cb)) ml++;
                if (Math.Abs(ca.Red - cb.Red) + Math.Abs(ca.Green - cb.Green) + Math.Abs(ca.Blue - cb.Blue) > 40)
                    diff++;
            }

        return (mp / (double)total, ml / (double)total, diff / (double)total);
    }

    private static bool IsMagenta(SKColor c) => c.Red > 170 && c.Blue > 170 && c.Green < 90;
    private static bool IsCyan(SKColor c) => c.Green > 170 && c.Blue > 170 && c.Red < 90;
}
