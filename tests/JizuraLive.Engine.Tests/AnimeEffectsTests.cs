using JizuraLive.Render.Draw;
using JizuraLive.Render.Effects;
using JizuraLive.Render.Gpu;
using Vortice.Mathematics;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 漫画 / 二次元语汇后期效果的回归闸（M4）。四条：
/// ①恒等参数（amount=0 / progress=0 / progress=1）必须逐像素等于输入；
/// ②激活参数必须真的改变画面，且画面不塌（alpha 不丢、不出现 NaN 塌成的纯黑）；
/// ③色块转场 progress=0.5 整帧被色块覆盖；④电影黑边条内全黑、画面中线原样。
/// </summary>
public class AnimeEffectsTests
{
    private const int W = 320;
    private const int H = 180;

    private static EffectParam P(string defId, string key) =>
        EffectCatalog.Get(defId)!.Params.First(p => p.Key == key);

    /// <summary>
    /// 底图刻意不含纯黑（每通道 ≥ 0.2），后面「激活参数不产生纯黑像素」才成为可判定的断言。
    /// 全部像素不透明：效果里的预乘往返（/a 再 *a）在 a=1 时是精确的，恒等用例才卡得住 1/255。
    /// </summary>
    private static void DrawBase(Immediate2D d, int w, int h)
    {
        d.Begin();
        d.Clear(new Color4(0.20f, 0.24f, 0.34f, 1f));

        d.FillRect(new Rect(0, 0, w, h), new Paint2D
        {
            Brush = new LinearGradient2D(new System.Numerics.Vector2(0, 0),
                new System.Numerics.Vector2(w, h),
            [
                new GradientStop2D(0f, new Color4(0.30f, 0.36f, 0.52f, 1f)),
                new GradientStop2D(0.5f, new Color4(0.62f, 0.38f, 0.56f, 1f)),
                new GradientStop2D(1f, new Color4(0.92f, 0.70f, 0.34f, 1f)),
            ]),
        });

        for (var i = 0; i < 4; i++)
            d.FillCircle(46f + i * 74f, h * 0.4f, 22f,
                new Paint2D { Brush = new SolidBrush2D(new Color4(0.24f, 0.86f, 0.78f, 1f)) });

        d.FillRect(new Rect(w * 0.55f, h * 0.66f, 84f, 34f),
            new Paint2D { Brush = new SolidBrush2D(new Color4(0.95f, 0.93f, 0.88f, 1f)) });

        d.End();
    }

    private static ReadbackImage RenderBase(D2DHost host, OffscreenTarget target)
    {
        host.Draw.SetTarget(target.Bitmap, W, H);
        DrawBase(host.Draw, W, H);
        return target.Readback();
    }

    private static ReadbackImage Apply(D3DDevice gpu, GpuEffectRunner runner, ReadbackImage baseBmp,
        EffectGraph graph)
    {
        using var input = runner.Upload(baseBmp);
        var rg = graph.BuildRenderGraph(runner.Shaders, 0.7f, W, H);
        var output = runner.RunGraph(rg, input.Srv, W, H);
        try
        {
            return GpuReadback.ToImage(gpu, output.Texture, W, H);
        }
        finally
        {
            runner.Pool.Return(output);
        }
    }

    private static IEnumerable<(string Id, string Label, Action<EffectNode> Cfg)> IdentityCases()
    {
        foreach (var id in new[] { "speedLines", "mangaPanels", "duotone", "letterbox" })
            yield return (id, $"{id} amount=0", n => n.Set(P(id, "amount"), 0f));

        // 转场两端都必须严格回到原画面：起点没盖住、终点完全退场
        yield return ("wipe", "wipe progress=0", n => n.Set(P("wipe", "progress"), 0f));
        yield return ("wipe", "wipe progress=1", n => n.Set(P("wipe", "progress"), 1f));
    }

    private static IEnumerable<(string Id, string Label, Action<EffectNode> Cfg)> ActiveCases()
    {
        yield return ("speedLines", "speedLines 密集线", n =>
        {
            n.Set(P("speedLines", "amount"), 0.9f);
            n.Set(P("speedLines", "density"), 96f);
            n.Set(P("speedLines", "inner"), 0.28f);
            n.Set(P("speedLines", "thickness"), 1.6f);
            n.Set(P("speedLines", "speed"), 6f);
        });
        yield return ("wipe", "wipe 推进中", n =>
        {
            n.Set(P("wipe", "progress"), 0.35f);
            n.Set(P("wipe", "angle"), 25f);
        });
        yield return ("mangaPanels", "mangaPanels 三格", n => n.Set(P("mangaPanels", "amount"), 1f));
        yield return ("duotone", "duotone 强", n => n.Set(P("duotone", "amount"), 0.85f));
        yield return ("letterbox", "letterbox 满条", n => n.Set(P("letterbox", "amount"), 1f));
    }

    /// <summary>恒等参数下逐像素等于输入（≤1/255）——效果在两端必须是「没插进来」而不是「几乎没插进来」。</summary>
    [GpuFact]
    public void IdentityParamsLeaveTheFrameUntouched()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();
        using (gpu)
        using (var host = D2DHost.Create(gpu))
        using (var target = OffscreenTarget.Create(gpu, host.Ctx, W, H))
        using (var runner = new GpuEffectRunner(gpu))
        {
            using var baseBmp = RenderBase(host, target);

            foreach (var (id, label, cfg) in IdentityCases())
            {
                using var frame = Apply(gpu, runner, baseBmp, new EffectGraph().Add(id, cfg));
                var diff = GoldenImage.Compare(baseBmp, frame);
                Assert.True(diff.SameSize, $"{label}：尺寸变了");
                Assert.True(diff.MaxAbs <= 1, $"{label} 改变了画面（最大差 {diff.MaxAbs}，平均 {diff.MeanAbs:F3}）");
            }
        }
    }

    /// <summary>
    /// 激活参数下画面必须真的变了，且没有塌掉：alpha 全保留、不出现纯黑像素。
    /// 关于 NaN：D3D11 往 UNORM 目标写 NaN 会变成 0，回读里看不到 NaN 本身，
    /// 所以这里用「纯黑像素」当它的可见签名（本用例底图刻意不含纯黑，这五个效果也不该造出纯黑）；
    /// 另一侧在跑到 GPU 之前先确认打包出来的常量全是有限值。
    /// </summary>
    [GpuFact]
    public void ActiveParamsChangeTheFrameWithoutCollapsing()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();
        using (gpu)
        using (var host = D2DHost.Create(gpu))
        using (var target = OffscreenTarget.Create(gpu, host.Ctx, W, H))
        using (var runner = new GpuEffectRunner(gpu))
        {
            using var baseBmp = RenderBase(host, target);

            foreach (var (id, label, cfg) in ActiveCases())
            {
                var graph = new EffectGraph().Add(id, cfg);
                var constants = EffectGraph.PackConstants(EffectCatalog.Get(id)!, graph.Nodes[0], 0.7f, W, H);
                Assert.All(constants, v => Assert.True(float.IsFinite(v), $"{label}：打包出的常量含非有限值"));

                using var frame = Apply(gpu, runner, baseBmp, graph);

                var diff = GoldenImage.Compare(baseBmp, frame);
                Assert.True(diff.MaxAbs >= 4, $"{label} 对画面几乎没有影响（最大差 {diff.MaxAbs}）");

                var black = 0;
                for (var y = 0; y < H; y++)
                for (var x = 0; x < W; x++)
                {
                    var c = frame.GetPixel(x, y);
                    Assert.True(c.Alpha == 255, $"{label}：({x},{y}) 的 alpha 掉到 {c.Alpha}");
                    // letterbox 的黑边是有意为之的纯黑，不参与这条签名判定
                    if (id != "letterbox" && c.Red == 0 && c.Green == 0 && c.Blue == 0) black++;
                }

                if (id != "letterbox")
                    Assert.True(black == 0, $"{label} 出现 {black} 个纯黑像素（NaN 在 UNORM 目标上的塌陷签名）");
            }
        }
    }

    /// <summary>转场中点（非光圈）整帧就是色块色——0→0.5 推入、0.5→1 退出的交界处必须盖满。</summary>
    [GpuFact]
    public void WipeMidpointCoversTheWholeFrameWithTheBlockColor()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();
        using (gpu)
        using (var host = D2DHost.Create(gpu))
        using (var target = OffscreenTarget.Create(gpu, host.Ctx, W, H))
        using (var runner = new GpuEffectRunner(gpu))
        {
            using var baseBmp = RenderBase(host, target);

            var graph = new EffectGraph().Add("wipe", n =>
            {
                n.Set(P("wipe", "progress"), 0.5f);
                n.Set(P("wipe", "iris"), 0f);
                n.SetColor(P("wipe", "color"), "#ff5fa2");
            });

            using var frame = Apply(gpu, runner, baseBmp, graph);

            for (var y = 0; y < H; y += 3)
            for (var x = 0; x < W; x += 3)
            {
                var c = frame.GetPixel(x, y);
                Assert.True(Math.Abs(c.Red - 255) <= 2 && Math.Abs(c.Green - 95) <= 2
                            && Math.Abs(c.Blue - 162) <= 2 && c.Alpha == 255,
                    $"wipe progress=0.5 时 ({x},{y}) 不是色块色：#{c.Red:x2}{c.Green:x2}{c.Blue:x2}{c.Alpha:x2}");
            }
        }
    }

    /// <summary>按 progress 跑一次光圈转场；cover 取 #ff5fa2，与线性中点用例同一套颜色约定。</summary>
    private static ReadbackImage WipeIris(D3DDevice gpu, GpuEffectRunner runner, ReadbackImage baseBmp, float progress)
    {
        var graph = new EffectGraph().Add("wipe", n =>
        {
            n.Set(P("wipe", "progress"), progress);
            n.Set(P("wipe", "iris"), 1f);
            n.SetColor(P("wipe", "color"), "#ff5fa2");
        });
        return Apply(gpu, runner, baseBmp, graph);
    }

    private static bool Near(ReadbackPixel a, ReadbackPixel b, int tol) =>
        Math.Abs(a.Red - b.Red) <= tol && Math.Abs(a.Green - b.Green) <= tol &&
        Math.Abs(a.Blue - b.Blue) <= tol && Math.Abs(a.Alpha - b.Alpha) <= tol;

    [GpuFact]
    public void WipeIrisIsRoundOnWideFrames()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();
        using (gpu)
        using (var host = D2DHost.Create(gpu))
        using (var target = OffscreenTarget.Create(gpu, host.Ctx, W, H))
        using (var runner = new GpuEffectRunner(gpu))
        {
            using var baseBmp = RenderBase(host, target);
            using var f = WipeIris(gpu, runner, baseBmp, 0.62f);

            int Reach(int dx, int dy)
            {
                int x = W / 2, y = H / 2, n = 0;
                while (x >= 0 && x < W && y >= 0 && y < H && Near(baseBmp.GetPixel(x, y), f.GetPixel(x, y), 1))
                {
                    x += dx;
                    y += dy;
                    n++;
                }
                return n;
            }

            var rx = Reach(1, 0);
            var ry = Reach(0, 1);
            Assert.True(ry > 10 && ry < H / 2 - 5, $"竖向半径 {ry}px 不在可量范围");
            Assert.True(Math.Abs(rx - ry) <= 3, $"光圈在 {W}x{H} 上不圆：横向半径 {rx}px、竖向半径 {ry}px");
        }
    }

    /// <summary>「没被色块盖住」的像素数：回读结果与原画面逐通道相同（≤1 灰阶）。</summary>
    private static int RevealedPixels(ReadbackImage baseBmp, ReadbackImage frame)
    {
        var n = 0;
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
            if (Near(baseBmp.GetPixel(x, y), frame.GetPixel(x, y), 1)) n++;
        return n;
    }

    /// <summary>
    /// 光圈转场必须与线性转场同构：0→0.5 圆从画面边缘收拢、中点整帧盖满；
    /// 0.5→1 半径掉头往回长、从中心张开露出画面，progress→1 严格回到原画面。
    ///
    /// 回归闸针对的缺陷（src/Render/Effects/ShaderSources.cs 的 Wipe iris 分支）：
    /// 后半程 r 的 lerp 方向没跟着 u 一起翻转，0.5→1 重复了一遍「收拢」，
    /// 导演按 progress 0→1 推这个转场会看到「收拢→闪开→再收拢→闪开」。
    /// </summary>
    [GpuFact]
    public void WipeIrisClosesInThenOpensFromTheCentre()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();
        using (gpu)
        using (var host = D2DHost.Create(gpu))
        using (var target = OffscreenTarget.Create(gpu, host.Ctx, W, H))
        using (var runner = new GpuEffectRunner(gpu))
        {
            using var baseBmp = RenderBase(host, target);

            // ① 中点：整帧就是色块色（与线性用例同一条闸，两半的交界处必须没有缝）
            using (var mid = WipeIris(gpu, runner, baseBmp, 0.5f))
            {
                for (var y = 0; y < H; y += 3)
                for (var x = 0; x < W; x += 3)
                {
                    var c = mid.GetPixel(x, y);
                    Assert.True(Math.Abs(c.Red - 255) <= 2 && Math.Abs(c.Green - 95) <= 2
                                && Math.Abs(c.Blue - 162) <= 2 && c.Alpha == 255,
                        $"wipe(iris) progress=0.5 时 ({x},{y}) 不是色块色：#{c.Red:x2}{c.Green:x2}{c.Blue:x2}{c.Alpha:x2}");
                }
            }

            // ② 后半程：露出的画面面积必须随 progress 严格增长（= 从中心向外张开）
            var prev = -1;
            foreach (var p in new[] { 0.55f, 0.65f, 0.75f, 0.85f, 0.95f })
            {
                using var f = WipeIris(gpu, runner, baseBmp, p);
                var area = RevealedPixels(baseBmp, f);
                Assert.True(area > prev,
                    $"wipe(iris) 后半程露出面积没有随 progress 增长（从中心张开）：progress={p} 露出 {area} 像素，前一个 {prev}");
                prev = area;
            }

            // ③ 前半程仍然是从边缘收拢：露出的面积随 progress 严格减少（修后半程不许把前半程带翻）
            var prevFirst = int.MaxValue;
            foreach (var p in new[] { 0.05f, 0.15f, 0.25f, 0.35f, 0.45f })
            {
                using var f = WipeIris(gpu, runner, baseBmp, p);
                var area = RevealedPixels(baseBmp, f);
                Assert.True(area < prevFirst,
                    $"wipe(iris) 前半程露出面积没有随 progress 减少（从边缘收拢）：progress={p} 露出 {area} 像素，前一个 {prevFirst}");
                prevFirst = area;
            }

            // ④ 第二半程的形态：圆盘已经越过画面四边中点（那里露出原画面），四角仍被盖住。
            // 光圈在屏幕空间是正圆，16:9 下左右边中点比上下远，前沿色带又不能扫到角上，所以取 0.92
            using (var f = WipeIris(gpu, runner, baseBmp, 0.92f))
            {
                (int X, int Y)[] revealed = [(W / 2, H / 2), (1, H / 2), (W - 2, H / 2), (W / 2, 1), (W / 2, H - 2)];
                foreach (var (x, y) in revealed)
                    Assert.True(Near(baseBmp.GetPixel(x, y), f.GetPixel(x, y), 1),
                        $"wipe(iris) progress=0.92 时 ({x},{y}) 该已从中心张开露出原画面，实际 {f.GetPixel(x, y)}（原图 {baseBmp.GetPixel(x, y)}）");

                (int X, int Y)[] covered = [(2, 2), (W - 3, 2), (2, H - 3), (W - 3, H - 3)];
                foreach (var (x, y) in covered)
                {
                    var c = f.GetPixel(x, y);
                    Assert.True(Math.Abs(c.Red - 255) <= 2 && Math.Abs(c.Green - 95) <= 2 && Math.Abs(c.Blue - 162) <= 2,
                        $"wipe(iris) progress=0.92 时四角 ({x},{y}) 该仍被色块盖住，实际 #{c.Red:x2}{c.Green:x2}{c.Blue:x2}");
                }
            }

            // ⑤ 两端严格回原画面：起点还没盖上来、终点必须完全退场（p→1 处不许靠 p>=1 的提前返回遮丑）
            foreach (var p in new[] { 0.001f, 0.99f, 0.999f })
            {
                using var f = WipeIris(gpu, runner, baseBmp, p);
                var diff = GoldenImage.Compare(baseBmp, f);
                Assert.True(diff.SameSize && diff.MaxAbs <= 1,
                    $"wipe(iris) progress={p} 没有回到原画面（最大差 {diff.MaxAbs}，平均 {diff.MeanAbs:F3}）");
            }
        }
    }

    /// <summary>黑边：条内完全不透明纯黑，画面中线逐像素原样（条不是「把整帧压暗」）。</summary>
    [GpuFact]
    public void LetterboxBarsAreOpaqueBlackAndKeepTheMiddleRowIntact()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();
        using (gpu)
        using (var host = D2DHost.Create(gpu))
        using (var target = OffscreenTarget.Create(gpu, host.Ctx, W, H))
        using (var runner = new GpuEffectRunner(gpu))
        {
            using var baseBmp = RenderBase(host, target);

            using var frame = Apply(gpu, runner, baseBmp,
                new EffectGraph().Add("letterbox", n => n.Set(P("letterbox", "amount"), 1f)));

            var top10 = H / 10;
            for (var y = 0; y < top10; y++)
            for (var x = 0; x < W; x += 5)
            {
                var c = frame.GetPixel(x, y);
                Assert.True(c is { Red: 0, Green: 0, Blue: 0, Alpha: 255 },
                    $"letterbox 顶部 10%（{x},{y}）不是不透明纯黑：#{c.Red:x2}{c.Green:x2}{c.Blue:x2}{c.Alpha:x2}");
            }

            var mid = H / 2;
            for (var x = 0; x < W; x++)
            {
                var a = baseBmp.GetPixel(x, mid);
                var b = frame.GetPixel(x, mid);
                Assert.True(a == b, $"letterbox 改变了画面中线 ({x},{mid})：{a} → {b}");
            }
        }
    }
}
