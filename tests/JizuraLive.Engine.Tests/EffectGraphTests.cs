using JizuraLive.Render.Draw;
using JizuraLive.Render.Effects;
using JizuraLive.Render.Gpu;
using Vortice.Mathematics;
using Xunit.Abstractions;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// P2 效果图的回归闸：①目录里每个效果的 HLSL 都必须能编译（语法错误一个都不许漏）；
/// ②整条效果链要能在真实画面上跑通并产出可复现的图（golden）。
/// </summary>
public class EffectGraphTests
{
    private const int W = 640;
    private const int H = 360;
    private const string GoldenName = "render-core-fx-chain";

    private readonly ITestOutputHelper _out;

    public EffectGraphTests(ITestOutputHelper output) => _out = output;

    /// <summary>目录里每个效果的 HLSL 都要能编译——把语法错误挡在测试里，而不是运行期黑屏。</summary>
    [Fact]
    public void EveryCatalogShaderCompiles()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) return;
        using (gpu)
        using (var runner = new GpuEffectRunner(gpu))
        {
            var ok = 0;
            foreach (var def in EffectCatalog.All)
            {
                var ps = runner.Shaders.GetPixel(def.Id, def.Hlsl);
                Assert.NotNull(ps);
                ok++;
            }

            _out.WriteLine($"[fx] {ok}/{EffectCatalog.All.Count} 个效果着色器编译通过");
            Assert.Equal(EffectCatalog.All.Count, ok);
        }
    }

    /// <summary>参数 schema 自身要自洽：槽位不越界、不占用全局保留槽、颜色不跨槽。</summary>
    [Fact]
    public void CatalogSchemasAreWellFormed()
    {
        // HLSL 必须纯 ASCII：D3DCompile 在遇到非 ASCII（例如中文注释）时会报出与真实原因
        // 毫不相干的语法错误（实测「redefinition of 'P'」「invalid subscript 'S'」），
        // 排查代价极高。这条守门把问题挡在提交前。
        AssertAscii(ShaderLibrary.Prelude, "Prelude");

        foreach (var def in EffectCatalog.All)
        {
            AssertAscii(def.Hlsl, def.Id);
            Assert.False(string.IsNullOrWhiteSpace(def.Id));
            Assert.False(string.IsNullOrWhiteSpace(def.Hlsl));
            foreach (var p in def.Params)
            {
                Assert.InRange(p.Slot, 0, EffectGraph.GlobalSlot - 1);
                Assert.InRange(p.Comp, 0, 3);
                if (p.Kind == EffectParamKind.Color)
                    Assert.True(p.Comp + 2 <= 3, $"{def.Id}.{p.Key}：颜色参数跨槽（Comp={p.Comp}）");
                Assert.True(p.Min <= p.Default && p.Default <= p.Max,
                    $"{def.Id}.{p.Key}：默认值 {p.Default} 不在 [{p.Min},{p.Max}] 内");
            }

            // 同一 (Slot, Comp) 不允许被两个参数占用
            var seen = new HashSet<(int, int)>();
            foreach (var p in def.Params)
            {
                var span = p.Kind == EffectParamKind.Color ? 3 : 1;
                for (var i = 0; i < span; i++)
                    Assert.True(seen.Add((p.Slot, p.Comp + i)), $"{def.Id}：槽位 ({p.Slot},{p.Comp + i}) 被重复占用");
            }
        }
    }

    private static void AssertAscii(string hlsl, string what)
    {
        foreach (var line in hlsl.Split('\n'))
        {
            foreach (var ch in line)
            {
                if (ch > 127)
                {
                    Assert.Fail($"HLSL「{what}」含非 ASCII 字符 U+{(int)ch:X4}（行：{line.Trim()}）——"
                                + "D3DCompile 会报无关语法错误，请把着色器注释改成英文");
                }
            }
        }
    }

    /// <summary>画一张有颜色层次、有细节、有文字的底图，让各种效果都看得出差别。</summary>
    private static void DrawBaseScene(Immediate2D d, int w, int h)
    {
        d.Begin();
        d.Clear(new Color4(0.06f, 0.06f, 0.09f, 1f));

        d.FillRect(new Rect(0, 0, w, h), new Paint2D
        {
            Brush = new LinearGradient2D(new System.Numerics.Vector2(0, 0),
                new System.Numerics.Vector2(w, h),
            [
                new GradientStop2D(0f, new Color4(0.08f, 0.10f, 0.22f, 1f)),
                new GradientStop2D(0.5f, new Color4(0.42f, 0.14f, 0.44f, 1f)),
                new GradientStop2D(1f, new Color4(0.98f, 0.72f, 0.16f, 1f)),
            ]),
        });

        for (var i = 0; i < 6; i++)
        {
            d.FillCircle(90f + i * 92f, h * 0.42f, 30f - i * 2f, new Paint2D
            {
                Brush = new RadialGradient2D(new System.Numerics.Vector2(90f + i * 92f, h * 0.42f),
                    34f, 34f,
                [
                    new GradientStop2D(0f, new Color4(0.10f, 0.96f, 0.84f, 1f)),
                    new GradientStop2D(1f, new Color4(0.10f, 0.96f, 0.84f, 0f)),
                ]),
            });
        }

        d.DrawText("EFFECT GRAPH 效果图", w / 2f, h * 0.62f, TextAlign2D.Center,
            d.Fonts.Get("Microsoft YaHei UI", 34),
            new Paint2D { Brush = new SolidBrush2D(new Color4(0.97f, 0.94f, 0.90f, 1f)) });

        d.DrawText("chromatic · glitch · scanline · grain · vignette · glow", w / 2f, h * 0.72f,
            TextAlign2D.Center, d.Fonts.Get("Consolas", 14),
            new Paint2D { Brush = new SolidBrush2D(new Color4(1f, 1f, 1f, 0.6f)) });

        d.End();
    }

    private static ReadbackImage RenderBaseScene(D2DHost host, D3DDevice gpu, OffscreenTarget target)
    {
        host.Draw.SetTarget(target.Bitmap, W, H);
        DrawBaseScene(host.Draw, W, H);
        return target.Readback();
    }

    /// <summary>一条把目录里多数类别都覆盖到的链。</summary>
    private static EffectGraph BuildShowcaseGraph()
    {
        var g = new EffectGraph();
        g.Add("colorGrade", n =>
        {
            var ps = EffectCatalog.Get("colorGrade")!.Params;
            n.Set(ps[0], 1.25f);                    // 饱和度
            n.Set(ps[1], 1.10f);                    // 对比度
            n.SetColor(ps[4], "#8ad4ff");           // tint 冷色
            n.Set(ps[5], 0.18f);                    // tint 强度
        });
        g.Add("glow", n => n.Set(EffectCatalog.Get("glow")!.Params[1], 0.85f));
        g.Add("chromatic", n =>
        {
            var ps = EffectCatalog.Get("chromatic")!.Params;
            n.Set(ps[0], 1.6f);
            n.Set(ps[3], 1f);                       // 径向
        });
        g.Add("scanline", n =>
        {
            var ps = EffectCatalog.Get("scanline")!.Params;
            n.Set(ps[0], 26f);
            n.Set(ps[1], 0.10f);
            n.Set(ps[2], 0.4f);
        });
        g.Add("grain", n =>
        {
            var ps = EffectCatalog.Get("grain")!.Params;
            n.Set(ps[0], 0.018f);
        });
        g.Add("vignette", n =>
        {
            var ps = EffectCatalog.Get("vignette")!.Params;
            n.Set(ps[0], 0.38f);
            n.Set(ps[1], 0.5f);
        });
        return g;
    }

    private static ReadbackImage RunChain(D3DDevice gpu, D2DHost host, OffscreenTarget target,
        GpuEffectRunner runner, EffectGraph graph, float time)
    {
        using var baseBmp = RenderBaseScene(host, gpu, target);
        using var input = runner.Upload(baseBmp);
        var rg = graph.BuildRenderGraph(runner.Shaders, time, W, H);
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

    [Fact]
    public void EffectChainProducesVisibleDeterministicOutput()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) return;
        using (gpu)
        using (var host = D2DHost.Create(gpu))
        using (var target = OffscreenTarget.Create(gpu, host.Ctx, W, H))
        using (var runner = new GpuEffectRunner(gpu))
        {
            var graph = BuildShowcaseGraph();
            using var a = RunChain(gpu, host, target, runner, graph, 1.5f);
            using var b = RunChain(gpu, host, target, runner, graph, 1.5f);

            Assert.Equal(W, a.Width);
            Assert.Equal(H, a.Height);
            Assert.True(GoldenImage.BitIdentical(a, b), "同一时刻同一条效果链必须逐字节相同");

            var colors = new HashSet<uint>();
            for (var y = 0; y < H; y += 3)
            for (var x = 0; x < W; x += 3)
            {
                var c = a.GetPixel(x, y);
                colors.Add(((uint)c.Red << 16) | ((uint)c.Green << 8) | c.Blue);
            }

            Assert.True(colors.Count > 200, $"效果链输出颜色过少（{colors.Count}），链可能没生效");
            _out.WriteLine($"[fx] 效果链输出 {colors.Count} 种颜色（{W}x{H}）");

            if (GoldenImage.UpdateRequested)
            {
                GoldenImage.SaveOrUpdate(GoldenName, a);
                // 同时存一张「无效果」底图，便于肉眼区分「底图本身的问题」与「效果造成的差异」
                using var baseOnly = RenderBaseScene(host, gpu, target);
                GoldenImage.SaveOrUpdate(GoldenName + "-base", baseOnly);
            }
        }
    }

    [Fact]
    public void EffectChainMatchesGolden()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) return;
        using (gpu)
        using (var host = D2DHost.Create(gpu))
        using (var target = OffscreenTarget.Create(gpu, host.Ctx, W, H))
        using (var runner = new GpuEffectRunner(gpu))
        {
            using var frame = RunChain(gpu, host, target, runner, BuildShowcaseGraph(), 1.5f);

            if (GoldenImage.UpdateRequested)
            {
                GoldenImage.SaveOrUpdate(GoldenName, frame);
                return;
            }

            using var golden = GoldenImage.TryLoad(GoldenName);
            if (golden is null)
            {
                _out.WriteLine($"[golden] 缺基准 {GoldenImage.PathOf(GoldenName)}");
                return;
            }

            var diff = GoldenImage.Compare(golden.Value, frame);
            Assert.True(diff.SameSize, "尺寸与基准不一致：" + diff);
            Assert.True(diff.MeanAbs <= 2.0, $"平均绝对差超容差：{diff}");
            Assert.True(diff.PctOver8 <= 0.02, $"超阈值像素占比超容差：{diff}");
        }
    }

    /// <summary>每个效果单独跑一遍，确认都真的改变了画面（抓「编译过但什么都没做」的空效果）。</summary>
    [Fact]
    public void EveryEffectChangesTheFrame()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) return;
        using (gpu)
        using (var host = D2DHost.Create(gpu))
        using (var target = OffscreenTarget.Create(gpu, host.Ctx, W, H))
        using (var runner = new GpuEffectRunner(gpu))
        {
            using var baseBmp = RenderBaseScene(host, gpu, target);
            var empty = new EffectGraph(); // 无效果 = 参照
            using var reference = RunChainWithBase(gpu, runner, empty, baseBmp);

            foreach (var def in EffectCatalog.All)
            {
                // 两侧各试一次：极值也可能恰好落在「无操作」上（例如 vignette 强度取 0），
                // 所以只要有一侧改变了画面，就证明这个效果是活的。
                var best = 0.0;
                foreach (var bias in new[] { 0, 1, 2 })   // 低 / 中 / 高
                {
                    var g = new EffectGraph().Add(def.Id, n => ConfigureStrong(n, def, bias));
                    using var outBmp = RunChainWithBase(gpu, runner, g, baseBmp);
                    best = Math.Max(best, GoldenImage.Compare(reference, outBmp).MeanAbs);
                }

                _out.WriteLine($"[fx] {def.Id,-12} 最强改变 meanAbs={best:F3}");
                Assert.True(best > 0.05,
                    $"效果 {def.Id} 两侧极值都对画面几乎没影响（best meanAbs={best:F4}）——疑似空效果");
            }
        }
    }

    /// <summary>
    /// 把每个参数都推到「离默认值最远」的一侧——用来证明效果**有能力**改变画面。
    /// 必要性：有些效果的默认参数本身就是恒等变换（例如 colorGrade 默认饱和度/对比度都是 1、
    /// 强度 1、tint 强度 0），拿默认值测「有没有效果」会误判成空效果。
    /// </summary>
    /// <summary>
    /// 把参数整体推向 低 / 中 / 高 三档。为什么要三档：单看极值可能恰好退化——
    /// 例如 glow 的阈值取 1 时「没有像素够亮」，强度取 0 时又什么都不加，两侧都成了空操作，
    /// 但这不代表效果是死的。三档里只要有任意一档改变画面，就证明它是活的。
    /// </summary>
    private static void ConfigureStrong(EffectNode n, EffectDef def, int bias)
    {
        foreach (var p in def.Params)
        {
            switch (p.Kind)
            {
                case EffectParamKind.Bool:
                    n.Set(p, bias == 2 ? 1f : 0f);
                    break;
                case EffectParamKind.Color:
                    n.SetColor(p, bias switch { 0 => "#20ffc0", 2 => "#ff2080", _ => "#ffd24a" });
                    break;
                default:
                    n.Set(p, bias switch { 0 => p.Min, 2 => p.Max, _ => (p.Min + p.Max) / 2f });
                    break;
            }
        }
    }

    private static ReadbackImage RunChainWithBase(D3DDevice gpu, GpuEffectRunner runner, EffectGraph graph,
        ReadbackImage baseBmp)
    {
        if (graph.Nodes.Count == 0)
        {
            // 空图：直接回读底图，作为参照
            using var input = runner.Upload(baseBmp);
            return GpuReadback.ToImage(gpu, input.Texture, baseBmp.Width, baseBmp.Height);
        }

        using var uploaded = runner.Upload(baseBmp);
        var rg = graph.BuildRenderGraph(runner.Shaders, 1.5f, baseBmp.Width, baseBmp.Height);
        var output = runner.RunGraph(rg, uploaded.Srv, baseBmp.Width, baseBmp.Height);
        try
        {
            return GpuReadback.ToImage(gpu, output.Texture, baseBmp.Width, baseBmp.Height);
        }
        finally
        {
            runner.Pool.Return(output);
        }
    }
}
