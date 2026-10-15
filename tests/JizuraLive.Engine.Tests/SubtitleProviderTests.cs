using System.Numerics;
using JizuraLive.Engine;
using JizuraLive.Pv;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Effects;
using JizuraLive.Render.Gpu;
using JizuraLive.Render.Text;
using Vortice.Mathematics;
using Xunit.Abstractions;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 字幕图层（P3）：纯排版规划 + 纯 GPU 绘字。
///
/// 分成两半：<see cref="SubtitleLayout"/> 的规划是纯函数（无设备也能跑），
/// 绘字则必须真跑 D2D 并回读像素——「只清透明底」也能让 HasContent 为真，
/// 所以凡是要钉住"真的画了字"的断言一律数非透明像素。
/// </summary>
public class SubtitleProviderTests
{
    private const int W = 640;
    private const int H = 360;

    /// <summary>纯排版测试的固定字体度量：Plan 只把它们当输入，不自己量字体。</summary>
    private const float Ascent = 40f;
    private const float LineHeight = 56f;

    /// <summary>换算断言用的设计高度：1080 是发布画布。</summary>
    private const int DesignH = 1080;

    private readonly ITestOutputHelper _out;

    public SubtitleProviderTests(ITestOutputHelper output) => _out = output;

    private sealed class FixedSource : IPvSubtitleSource
    {
        public IReadOnlyList<KrcLine>? Lines { get; init; }
        public SubtitleConfig? Config { get; init; }
        public long Version { get; init; } = 1;
    }

    private static List<KrcLine> SyntheticKrc()
    {
        var words = new List<KrcWord>
        {
            new(0, 400, "声"),
            new(400, 400, "が"),
            new(800, 500, "遠"),
            new(1300, 500, "く"),
            new(1800, 600, "で"),
        };
        return
        [
            new KrcLine(0, 2400, "声が遠くで", words),
            new KrcLine(3000, 2000, "まだ君を探してる",
                [new KrcWord(3000, 1000, "まだ"), new KrcWord(4000, 1000, "君を探してる")]),
        ];
    }

    // ---------------- 纯排版规划 ----------------

    private static KrcLine Line(long startMs, long durMs, string text) => new(startMs, durMs, text);

    /// <summary>开启绘字的配置：Content=Off 是 POCO 默认（整层不画），要测排版必须显式打开。</summary>
    private static SubtitleConfig On(SubPos pos = SubPos.Bottom, double size = 4.5, double stroke = 4.0,
        string? bg = null, bool hold = false, double margin = 4.0) => new()
    {
        Content = SubContent.Trans,
        Pos = pos,
        Size = size,
        Stroke = stroke,
        BgColor = bg ?? "rgba(0,0,0,0.6)",
        HoldLastLine = hold,
        MarginBottom = margin,
        MarginTop = margin,
    };

    private static SubtitlePlan? PlanAt(SubtitleConfig? cfg, IReadOnlyList<KrcLine> lines, double time,
        int height = DesignH) =>
        SubtitleLayout.Plan(cfg, LrcParser.ToClips(lines), time, W, height, Ascent, LineHeight);

    private static void AssertColor(float r, float g, float b, float a, Color4 actual)
    {
        Assert.Equal(r, actual.R, 3);
        Assert.Equal(g, actual.G, 3);
        Assert.Equal(b, actual.B, 3);
        Assert.Equal(a, actual.A, 3);
    }

    [Fact]
    public void SubtitlePlanOffMeansNoDraw()
    {
        var lines = new[] { Line(0, 2000, "声が遠くで") };

        // Content 的 POCO 默认就是 Off：整层不画
        Assert.Null(PlanAt(new SubtitleConfig(), lines, 1.0));
        Assert.Null(PlanAt(null, lines, 1.0));
    }

    [Fact]
    public void SubtitlePlanNullInGapAndAtClipEnd()
    {
        var lines = new[] { Line(0, 2000, "第一句"), Line(5000, 2000, "第二句") };

        Assert.NotNull(PlanAt(On(), lines, 1.0)); // 对照：句内必须出字，不是"永远 null"
        Assert.Null(PlanAt(On(), lines, 2.0));    // 半开区间 [0,2000)：t == EndMs 已离开
        Assert.Null(PlanAt(On(), lines, 3.5));    // 两句之间的空档
    }

    [Fact]
    public void SubtitlePlanHoldLastLineKeepsPreviousLine()
    {
        var lines = new[] { Line(0, 2000, "最后一句") };

        var inside = PlanAt(On(hold: true), lines, 1.0)!.Value;
        var held = PlanAt(On(hold: true), lines, 6.0)!.Value;

        Assert.Equal(inside.ClipId, held.ClipId);
        Assert.Equal("最后一句", held.Text);

        Assert.Null(PlanAt(On(hold: false), lines, 6.0));
    }

    [Fact]
    public void SubtitlePlanPositionsByConfig()
    {
        var lines = new[] { Line(0, 2000, "一句") };
        var clips = LrcParser.ToClips(lines);

        var bottom = SubtitleLayout.Plan(On(pos: SubPos.Bottom, margin: 4.0), clips, 1.0, W, DesignH,
            Ascent, LineHeight)!.Value;
        // 文本块底边 = 末行基线 + 基线以下部分（行高 - ascent）
        var blockBottom = bottom.FirstBaselineY + (bottom.Lines.Length - 1) * bottom.LineHeight
                          + (bottom.LineHeight - Ascent);
        Assert.Equal(1080 - 43.2, blockBottom, 1);
        Assert.Equal(W / 2f, bottom.CenterX);

        var top = SubtitleLayout.Plan(On(pos: SubPos.Top, margin: 4.0), clips, 1.0, W, DesignH,
            Ascent, LineHeight)!.Value;
        Assert.Equal(43.2, top.FirstBaselineY - Ascent, 1);

        // 位置翻转：Top 的基线必须比 Bottom 的靠上
        Assert.True(top.FirstBaselineY < bottom.FirstBaselineY,
            $"Pos 翻转后锚点应随之翻转：Top {top.FirstBaselineY} vs Bottom {bottom.FirstBaselineY}");
    }

    [Fact]
    public void SubtitlePlanSizesFromConfig()
    {
        var lines = new[] { Line(0, 2000, "一句") };
        var clips = LrcParser.ToClips(lines);

        var plan = SubtitleLayout.Plan(On(size: 4.5, stroke: 4.0), clips, 1.0, W, DesignH,
            Ascent, LineHeight)!.Value;
        Assert.Equal(48.6, plan.FontSizePx, 1); // Size 是设计高度百分比
        Assert.Equal(4f, plan.StrokeWidthPx);   // Stroke 是设计像素，不随高度缩放
        Assert.Equal(16, plan.StrokeOffsets.Count);
        Assert.All(plan.StrokeOffsets, o => Assert.Equal(2.0, (double)o.Length(), 3)); // 半径 = Stroke / 2
        Assert.DoesNotContain(plan.StrokeOffsets, o => o == Vector2.Zero);

        var noStroke = SubtitleLayout.Plan(On(stroke: 0), clips, 1.0, W, DesignH,
            Ascent, LineHeight)!.Value;
        Assert.Empty(noStroke.StrokeOffsets);
    }

    [Fact]
    public void SubtitleColorParsesHexAndRgba()
    {
        var fallback = new Color4(0.5f, 0.25f, 0.125f, 1f);

        AssertColor(1f, 1f, 1f, 1f, SubtitleLayout.ParseColor("#ffffff", fallback));
        AssertColor(0f, 0f, 0f, 1f, SubtitleLayout.ParseColor("#000000", fallback));
        AssertColor(1f, 1f, 1f, 1f, SubtitleLayout.ParseColor("#fff", fallback)); // #rgb 与 PvScene.ParseHex 同规则
        AssertColor(0f, 0f, 0f, 0.6f, SubtitleLayout.ParseColor("rgba(0,0,0,0.6)", fallback));
        AssertColor(1f, 128f / 255f, 0f, 1f, SubtitleLayout.ParseColor("rgba(255,128,0,1)", fallback));

        // 非法串一律回落 fallback，绝不抛：颜色串来自可手改的配置
        foreach (var bad in new string?[]
                 {
                     null, "", "   ", "nope", "#12", "#1234567", "rgba(0,0,0)", "rgba(0,0,0,0.6",
                 })
            AssertColor(fallback.R, fallback.G, fallback.B, fallback.A,
                SubtitleLayout.ParseColor(bad, fallback));
    }

    [Fact]
    public void SubtitlePlanBackgroundOnlyWhenAlphaPositive()
    {
        var lines = new[] { Line(0, 2000, "带底的字幕") };
        var clips = LrcParser.ToClips(lines);

        var clear = SubtitleLayout.Plan(On(bg: "rgba(0,0,0,0)"), clips, 1.0, W, DesignH,
            Ascent, LineHeight)!.Value;
        Assert.Null(clear.Background);

        var shaded = SubtitleLayout.Plan(On(bg: "rgba(0,0,0,0.6)"), clips, 1.0, W, DesignH,
            Ascent, LineHeight)!.Value;
        var bg = shaded.Background!.Value;

        // 文本包围盒（基线以上 ascent、以下 lineHeight - ascent）必须落在底块里
        var textTop = shaded.FirstBaselineY - Ascent;
        var textBottom = shaded.FirstBaselineY + (shaded.Lines.Length - 1) * shaded.LineHeight
                         + (shaded.LineHeight - Ascent);
        Assert.True(bg.Y <= textTop && bg.Bottom >= textBottom,
            $"底块 {bg} 应包住文本 {textTop}..{textBottom}");
        Assert.True(bg.X < shaded.CenterX && bg.Right > shaded.CenterX, "底块应左右包住居中文本");

        // 是"字条"不是整屏：宽高都小于画布
        Assert.True(bg.Width < W && bg.Height < DesignH, $"底块不应铺满画布：{bg}");
    }

    [Fact]
    public void SubtitlePlanDrawsMergedMultiLineText()
    {
        var two = SubtitleLayout.Plan(On(), LrcParser.ToClips(new[] { Line(0, 4000, "第一行\n第二行") }),
            1.0, W, DesignH, Ascent, LineHeight)!.Value;
        var one = SubtitleLayout.Plan(On(), LrcParser.ToClips(new[] { Line(0, 4000, "第一行") }),
            1.0, W, DesignH, Ascent, LineHeight)!.Value;

        Assert.Equal(2, two.Lines.Length);
        Assert.Equal("第一行", two.Lines[0]);
        Assert.Equal("第二行", two.Lines[1]);

        // 第 i 行基线 = FirstBaselineY + i × LineHeight；底边锚点固定时，多一行把首行基线整体上移一个行高
        Assert.Equal(LineHeight, two.LineHeight, 3);
        Assert.Equal(one.FirstBaselineY - LineHeight, two.FirstBaselineY, 3);
    }

    // ---------------- 真绘字（GPU） ----------------

    /// <summary>非透明像素数：只清透明底会得到 0。</summary>
    private static int CountInk(ReadbackImage img)
    {
        var n = 0;
        for (var y = 0; y < img.Height; y++)
        for (var x = 0; x < img.Width; x++)
            if (img.GetPixel(x, y).Alpha > 0)
                n++;

        return n;
    }

    [GpuFact]
    public void SubtitleLayerDrawsThroughProvider()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();
        using (gpu)
        using (var host = D2DHost.Create(gpu))
        {
            var source = new FixedSource { Lines = SyntheticKrc() };
            using var provider = new SubtitleProvider("subtitle_top", gpu, host.Ctx, source);

            provider.Render(1.0, W, H);
            var img = provider.Pump(new PvFrameContext(1.0, 0, 0, W, H));
            Assert.True(img.HasContent, "提供者应当给出可绘制内容");
            Assert.Equal(W, img.Width);
            Assert.Equal(H, img.Height);
        }
    }

    [GpuFact]
    public void NoSubtitleDataYieldsNoContent()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();
        using (gpu)
        using (var host = D2DHost.Create(gpu))
        {
            using var provider = new SubtitleProvider("subtitle_top", gpu, host.Ctx,
                new FixedSource { Lines = null });
            provider.Render(1.0, W, H);
            var img = provider.Pump(new PvFrameContext(1.0, 0, 0, W, H));
            Assert.True(img.HasContent);
        }
    }

    /// <summary>
    /// 句内时刻必须真的有字：只清透明底也能让 HasContent 为真，所以这里数非透明像素。
    /// </summary>
    [GpuFact]
    public void SubtitleLayerDrawsNonEmptyPixels()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();
        using (gpu)
        using (var host = D2DHost.Create(gpu))
        using (var provider = new SubtitleProvider("subtitle_top", gpu, host.Ctx,
                   new FixedSource { Lines = [Line(0, 2000, "声が遠くで")], Config = On() }))
        {
            provider.Render(1.0, W, H);
            using var px = provider.ReadbackTarget()!.Value;

            var ink = CountInk(px);
            _out.WriteLine($"[字幕] 句内非透明像素 {ink}");
            Assert.True(ink > 0, "句内时刻必须画出字（只清透明底会得到 0 个非透明像素）");
        }
    }

    /// <summary>文字离开区间必须清屏：不能把上一帧位图继续当新句（契约 PV-DIRECTOR-SYSTEM.md:397）。</summary>
    [GpuFact]
    public void SubtitleLayerClearsAfterLineLeaves()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();
        using (gpu)
        using (var host = D2DHost.Create(gpu))
        using (var provider = new SubtitleProvider("subtitle_top", gpu, host.Ctx,
                   new FixedSource { Lines = [Line(0, 2000, "声が遠くで")], Config = On() }))
        {
            provider.Render(1.0, W, H);
            using (var lit = provider.ReadbackTarget()!.Value)
                Assert.True(CountInk(lit) > 0, "句内应先画出字，否则这条用例证明不了清屏");

            provider.Render(3.5, W, H); // 末句之后的空档
            using var dark = provider.ReadbackTarget()!.Value;
            Assert.Equal(0, CountInk(dark));
        }
    }

    /// <summary>
    /// 描边真的画了，不是只读了配置：Stroke=8 的墨迹必须严格多于 Stroke=0。
    ///
    /// 这条用例里**必须关掉底块**（bg 透明）：默认 BgColor 会画出字条，字条本身已让文本包围盒内
    /// 每个像素 alpha&gt;0，于是描边往外扩的那一圈像素「本来就不透明」——数非透明像素数的口径
    /// 对描边完全失灵（实测两者都是 2220）。关掉底块后量的才是字形覆盖：Stroke=0 → 643、
    /// Stroke=8 → 1579（RTX 4060 Laptop，2026-10-15）。
    /// </summary>
    [GpuFact]
    public void SubtitleStrokeWidensInkCoverage()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();
        using (gpu)
        using (var host = D2DHost.Create(gpu))
        {
            static int InkAt(D2DHost d2d, D3DDevice device, double strokeWidth)
            {
                using var provider = new SubtitleProvider("subtitle_top", device, d2d.Ctx,
                    new FixedSource
                    {
                        Lines = [Line(0, 2000, "描边测试")],
                        Config = On(stroke: strokeWidth, bg: "rgba(0,0,0,0)"),
                    });
                provider.Render(1.0, W, H);
                using var px = provider.ReadbackTarget()!.Value;
                return CountInk(px);
            }

            var thin = InkAt(host, gpu, 0);
            var thick = InkAt(host, gpu, 8);
            _out.WriteLine($"[字幕] Stroke=0 → {thin} 像素，Stroke=8 → {thick} 像素");
            Assert.True(thick > thin, $"描边应扩大墨迹覆盖：Stroke=8 → {thick}，Stroke=0 → {thin}");
        }
    }

    /// <summary>
    /// 清晰字幕画在全局后期**之后**：bloom 是"模糊 + 与原图相加"，画在它之前必然被改色。
    /// 所以叠加层覆盖的像素必须**精确等于**叠加色（不是"接近"）。
    /// </summary>
    [GpuFact]
    public void SubtitleOverlaySurvivesGlobalPostFx()
    {
        const byte baseR = 204, baseG = 127, baseB = 51;
        const byte overR = 229, overG = 199, overB = 61;

        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();
        using (gpu)
        using (var host = D2DHost.Create(gpu))
        {
            var scene = new PvScene
            {
                Width = W,
                Height = H,
                AmbientBlendEnabled = false,
                AntiBleedGuardEnabled = false,
            };
            scene.Layers.Add(new PvLayerConfig
            {
                Id = "bg",
                Kind = PvLayerKind.Background,
                Parallax = new PvParallaxConfig { Depth = 0, RotationFactor = 0, ScaleFactor = 0 },
            });

            var effects = new EffectGraph();
            effects.Add("bloom"); // 多级可分离模糊 + 相加：画在它之前的字幕一定会被糊/被提亮

            using var renderer = new PvStageRenderer(gpu, host, scene, effects, W, H);
            renderer.Stage.Register(new ColorProvider("bg",
                new Color4(baseR / 255f, baseG / 255f, baseB / 255f, 1f)));

            using var target = OffscreenTarget.Create(gpu, host.Ctx, W, H);

            static bool IsOverlayColor(ReadbackPixel px, byte r, byte g, byte b) =>
                px.Red == r && px.Green == g && px.Blue == b;

            renderer.Composite(target.Bitmap, W, H);
            gpu.Context.Flush();
            using (var before = target.Readback())
                Assert.False(IsOverlayColor(before.GetPixel(W / 2, H / 2), overR, overG, overB),
                    "对照：没有叠加层时中心不应等于叠加色");

            using var overlay = new ColorProvider("subtitle_overlay",
                new Color4(overR / 255f, overG / 255f, overB / 255f, 1f));
            renderer.Overlay = overlay;
            renderer.Composite(target.Bitmap, W, H);
            gpu.Context.Flush();
            using (var after = target.Readback())
            {
                var c = after.GetPixel(W / 2, H / 2);
                _out.WriteLine($"[字幕] 后期之后叠加层中心像素 R{c.Red} G{c.Green} B{c.Blue}");
                Assert.True(IsOverlayColor(c, overR, overG, overB),
                    $"叠加层像素应精确等于叠加色 {overR}/{overG}/{overB}，实际 {c.Red}/{c.Green}/{c.Blue}"
                    + "（画在后期之前会被 bloom 改色）");
            }

            // 摘掉叠加层：画面必须回到没有叠加层的样子，不能被冻在上一帧
            renderer.Overlay = null;
            renderer.Composite(target.Bitmap, W, H);
            gpu.Context.Flush();
            using var restored = target.Readback();
            Assert.False(IsOverlayColor(restored.GetPixel(W / 2, H / 2), overR, overG, overB),
                "摘掉叠加层后中心不应还是叠加色");
        }
    }

    /// <summary>
    /// 两种文字消费同一份 LyricSample（契约 L10）：同一份歌词下，句内都出字、空档都清屏。
    /// </summary>
    [GpuFact]
    public void SubtitleLayerUsesSharedLyricSample()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();
        using (gpu)
        using (var host = D2DHost.Create(gpu))
        {
            var krc = new[] { Line(0, 2000, "同一份样本") };

            var scene = new PvScene
            {
                Width = W,
                Height = H,
                AmbientBlendEnabled = false,
                AntiBleedGuardEnabled = false,
            };
            foreach (var id in new[] { "lyrics_pv", "subtitle_top" })
                scene.Layers.Add(new PvLayerConfig
                {
                    Id = id,
                    Kind = id == "lyrics_pv" ? PvLayerKind.LyricPv : PvLayerKind.Subtitle,
                    Parallax = new PvParallaxConfig { Depth = 0, RotationFactor = 0, ScaleFactor = 0 },
                });

            using var stage = new PvStage(scene);
            var lyric = new TypographyLayerProvider("lyrics_pv", gpu, host, W, H);
            var subtitle = new SubtitleProvider("subtitle_top", gpu, host.Ctx,
                new FixedSource { Lines = krc, Config = On() });
            stage.Register(lyric);
            stage.Register(subtitle); // 所有权归 Stage，由它的 Dispose 统一释放

            using var target = OffscreenTarget.Create(gpu, host.Ctx, W, H);

            int Lit(double time)
            {
                subtitle.Render(time, W, H);
                lyric.Render(time);
                host.Draw.SetTarget(target.Bitmap, W, H);
                host.Draw.Begin();
                host.Draw.Clear(new Color4(0f, 0f, 0f, 1f));
                stage.RenderFrame(host.Draw, new PvFrameContext(time, 0, 0, W, H));
                host.Draw.End();
                gpu.Context.Flush();

                using var px = target.Readback();
                var lit = 0;
                for (var y = 0; y < px.Height; y++)
                for (var x = 0; x < px.Width; x++)
                {
                    var c = px.GetPixel(x, y);
                    if (c.Red + c.Green + c.Blue > 30)
                        lit++;
                }

                return lit;
            }

            var inside = Lit(1.0);
            var gap = Lit(3.5);
            _out.WriteLine($"[字幕] 句内亮像素 {inside}，空档亮像素 {gap}");
            Assert.True(inside > 0, "句内：动效歌词与清晰字幕都该出字");
            Assert.Equal(0, gap); // 空档：两者都清屏
        }
    }
}
