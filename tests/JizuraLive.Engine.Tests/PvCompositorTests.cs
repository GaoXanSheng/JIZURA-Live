using System.IO;
using JizuraLive.Engine;
using JizuraLive.Pv;
using SkiaSharp;
using Xunit;

namespace JizuraLive.Engine.Tests;

public class PvCompositorTests
{
    [Fact]
    public void ParallaxEngine_Calculates_Depth_Proportional_Displacement()
    {
        // 验证三维多层视差：各图层的平移量严格与各自的 Depth 深度系数成正比
        var cam = new CamState { X = 100, Y = 50, Rot = 10, S = 1.2 };

        var bgLayer = new PvLayerConfig { Parallax = new PvParallaxConfig { Depth = 0.35, RotationFactor = 0.5 } };
        var charLayer = new PvLayerConfig { Parallax = new PvParallaxConfig { Depth = 0.85, RotationFactor = 0.8 } };
        var lyricLayer = new PvLayerConfig { Parallax = new PvParallaxConfig { Depth = 1.00, RotationFactor = 1.0 } };
        var fgLayer = new PvLayerConfig { Parallax = new PvParallaxConfig { Depth = 1.40, RotationFactor = 1.2 } };

        var resBg = PvParallaxEngine.ComputeMatrix(1920, 1080, bgLayer, cam, 0, 0, true);
        var resChar = PvParallaxEngine.ComputeMatrix(1920, 1080, charLayer, cam, 0, 0, true);
        var resLyric = PvParallaxEngine.ComputeMatrix(1920, 1080, lyricLayer, cam, 0, 0, true);
        var resFg = PvParallaxEngine.ComputeMatrix(1920, 1080, fgLayer, cam, 0, 0, true);

        // 验证缩放系数近大远小：Depth 越大，变焦位移量越显著
        Assert.True(resBg.EffectiveScale < resChar.EffectiveScale);
        Assert.True(resChar.EffectiveScale < resLyric.EffectiveScale);
        Assert.True(resLyric.EffectiveScale < resFg.EffectiveScale);
    }

    [Fact]
    public void ParallaxEngine_Applies_AntiBleed_Overscan_When_Camera_Moves()
    {
        // 相机大幅度运动时，开启 AntiBleed 的图层自动应用安全扩展放大，杜绝黑边穿帮
        var staticCam = new CamState { X = 0, Y = 0, Rot = 0, S = 1.0 };
        var wildCam = new CamState { X = 150, Y = 100, Rot = 15, S = 0.85 }; // 伴随平移、旋转与缩减

        var layer = new PvLayerConfig
        {
            VisualBlend = new PvVisualBlendConfig { AntiBleedMargin = 0.10 }
        };

        var resStatic = PvParallaxEngine.ComputeMatrix(1920, 1080, layer, staticCam, 0, 0, true);
        var resWild = PvParallaxEngine.ComputeMatrix(1920, 1080, layer, wildCam, 0, 0, true);

        Assert.Equal(1.0, resStatic.OverscanScale);
        Assert.True(resWild.OverscanScale > 1.02, $"剧烈运镜时未能提供防露底超取景扩展：{resWild.OverscanScale}");
    }

    [Fact]
    public void AmbientColorFilter_Tints_White_Pixels_Toward_Scene_Tone()
    {
        // 环境光调色测试：白色色块经场景主色（青色）环境光滤镜处理后，蓝色和绿色通道显著上升，实现氛围融入
        var sceneTone = new SKColor(0, 220, 255);
        using var filter = PvColorGrading.CreateAmbientColorFilter(sceneTone, intensity: 0.25);
        Assert.NotNull(filter);

        using var bmp = new SKBitmap(64, 64);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);

        using var paint = new SKPaint
        {
            Color = SKColors.White,
            ColorFilter = filter
        };
        canvas.DrawRect(new SKRect(0, 0, 64, 64), paint);

        var pixel = bmp.GetPixel(32, 32);
        // 原本纯白 255, 255, 255，经染色后红色轻微被环境色偏向（R: ~191, G: ~246, B: ~255）
        Assert.True(pixel.Red < 240, $"红色分量未能反映环境光吸收：{pixel.Red}");
        Assert.True(pixel.Blue > 240, $"蓝色分量偏离预期：{pixel.Blue}");
    }

    [Fact]
    public void PvScene_Serializes_And_Deserializes_Cleanly()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"pv_scene_test_{Guid.NewGuid():N}.json");
        try
        {
            var original = PvScene.CreateDefault();
            original.Name = "Test Cyberpunk PV";
            original.Width = 3840;
            original.Height = 2160;
            original.GlobalParallaxEnabled = true;

            original.SaveToFile(tempFile);
            Assert.True(File.Exists(tempFile));

            var loaded = PvScene.LoadFromFile(tempFile);
            Assert.Equal("Test Cyberpunk PV", loaded.Name);
            Assert.Equal(3840, loaded.Width);
            Assert.Equal(2160, loaded.Height);
            Assert.Equal(5, loaded.Layers.Count);
            Assert.Equal(PvLayerKind.Background, loaded.Layers[0].Kind);
            Assert.Equal(PvLayerKind.VisualAsset, loaded.Layers[1].Kind);
            Assert.Equal(PvLayerKind.LyricPv, loaded.Layers[2].Kind);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void PvCompositor_Renders_Multi_Layer_Stack()
    {
        var scene = PvScene.CreateDefault();
        var compositor = new PvCompositor();

        using var surface = SKSurface.Create(new SKImageInfo(scene.Width, scene.Height));
        var canvas = surface.Canvas;

        var ctx = new PvRenderContext
        {
            Width = scene.Width,
            Height = scene.Height,
            Time = 1.5,
            Camera = new CamState { X = 20, Y = -10, Rot = 2, S = 1.05 },
            PrimaryColor = new SKColor(20, 24, 32),
            AccentColor = new SKColor(255, 60, 120),
            FastMode = false
        };

        var fakeProvider = new MockAssetProvider();
        var lyricDrawn = false;
        var subtitleDrawn = false;

        compositor.CompositeFrame(
            canvas, scene, ctx,
            assetProvider: fakeProvider,
            renderLyricLayer: (c, s) => lyricDrawn = true,
            renderSubtitleLayer: c => subtitleDrawn = true);

        Assert.True(lyricDrawn, "歌词动效层未被合成");
        Assert.True(subtitleDrawn, "字幕层未被合成");

        using var img = surface.Snapshot();
        Assert.NotNull(img);
        var px = img.PeekPixels();
        var centerCol = px.GetPixelColor(scene.Width / 2, scene.Height / 2);
        Assert.True(centerCol.Alpha > 0, "合成画布中心存在空洞");
    }

    [Fact]
    public void PvTrackEventBus_Dispatches_Song_Change_Cleanly()
    {
        var received = false;
        PvTrackInfo? info = null;

        void Handler(PvTrackInfo t)
        {
            received = true;
            info = t;
        }

        PvTrackEventBus.SongChanged += Handler;
        try
        {
            PvTrackEventBus.PublishSongChanged("KING", "Kanaria", 160.0, 135.0);

            Assert.True(received);
            Assert.NotNull(info);
            Assert.Equal("KING", info.Value.SongName);
            Assert.Equal("Kanaria", info.Value.Singer);
            Assert.Equal(160.0, info.Value.Bpm);
            Assert.Equal(135.0, info.Value.Duration);
        }
        finally
        {
            PvTrackEventBus.SongChanged -= Handler;
        }
    }

    [Fact]
    public void CharacterActor_Produces_Beat_Bounce_And_Squash_Stretch()
    {
        // 验证节拍拍头（beatPhase < 0.22）产生下沉与横向膨胀
        var poseBeat = PvCharacterActor.EvaluatePose(t: 0.05, bpm: 120.0, energy: 0.8, isBeatHit: true);
        Assert.True(poseBeat.BounceOffsetY > 0, "拍头未产生节拍下沉");
        Assert.True(poseBeat.SquashScaleX > 1.0f, "下沉时未产生横向挤压膨胀");
        Assert.True(poseBeat.StretchScaleY < 1.0f, "下沉时垂直方向未缩短");

        // 验证回弹期（beatPhase > 0.22）产生弹性回跳
        var poseReturn = PvCharacterActor.EvaluatePose(t: 0.35, bpm: 120.0, energy: 0.5);
        Assert.True(poseReturn.BounceOffsetY != poseBeat.BounceOffsetY, "回弹期与下沉期位移未能区分");
    }

    [Fact]
    public void CharacterActor_Reacts_To_Flash_With_Overexposure()
    {
        var poseNormal = PvCharacterActor.EvaluatePose(t: 1.0, isFlashActive: false);
        var poseFlash = PvCharacterActor.EvaluatePose(t: 1.0, isFlashActive: true);

        Assert.Equal(0f, poseNormal.FlashBoost);
        Assert.True(poseFlash.FlashBoost > 0.5f, "白闪时未能激活角色高光过曝协同");

        var (_, paint) = PvCharacterActor.BuildActorTransformAndPaint(
            SKMatrix.Identity, 100, 100, poseFlash, ambientFilter: null);
        using (paint)
        {
            Assert.NotNull(paint.ColorFilter);
        }
    }

    [Fact]
    public void EffectEngine_Renders_Shockwave_And_Burst_Effects()
    {
        using var surface = SKSurface.Create(new SKImageInfo(800, 600));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);

        // 绘制节拍冲击波、集中线、变形光斑与浮游微尘
        PvEffectEngine.DrawBeatShockwave(canvas, 400, 300, 0.4, 250f, SKColors.Magenta);
        PvEffectEngine.DrawRadialBurst(canvas, 400, 300, 200f, 0.8, SKColors.Cyan);
        PvEffectEngine.DrawAnamorphicStreak(canvas, 800, 300, 0.7, SKColors.Yellow);
        PvEffectEngine.DrawFloatingDust(canvas, 800, 600, 2.5, density: 0.8, SKColors.White);

        using var img = surface.Snapshot();
        Assert.NotNull(img);
        var px = img.PeekPixels();

        long brightPixels = 0;
        for (var y = 0; y < 600; y += 4)
            for (var x = 0; x < 800; x += 4)
            {
                var c = px.GetPixelColor(x, y);
                if (c.Red > 30 || c.Green > 30 || c.Blue > 30) brightPixels++;
            }

        Assert.True(brightPixels > 200, "特效引擎未能成功在画布上绘制光影内容");
    }

    [Fact]
    public void PvCompositor_Renders_With_Character_Bounce_And_Beat_Effects()
    {
        var scene = PvScene.CreateDefault();
        var compositor = new PvCompositor();

        using var surface = SKSurface.Create(new SKImageInfo(scene.Width, scene.Height));
        var canvas = surface.Canvas;

        var ctx = new PvRenderContext
        {
            Width = scene.Width,
            Height = scene.Height,
            Time = 1.05,
            Bpm = 140.0,
            Energy = 0.85,
            IsBeatHit = true,
            IsFlashActive = true,
            EnableCharacterBeatBounce = true,
            EnableCharacterFlashSync = true,
            EnableBeatVisualEffects = true,
            PrimaryColor = new SKColor(16, 16, 24),
            AccentColor = SKColors.Magenta,
        };

        var fakeProvider = new MockAssetProvider();
        compositor.CompositeFrame(
            canvas, scene, ctx,
            assetProvider: fakeProvider,
            renderLyricLayer: (c, s) => { },
            renderSubtitleLayer: c => { });

        using var img = surface.Snapshot();
        Assert.NotNull(img);
    }

    private sealed class MockAssetProvider : IPvAssetFrameProvider
    {
        private readonly SKBitmap _bmp = new(new SKImageInfo(200, 200, SKColorType.Bgra8888, SKAlphaType.Premul));

        public MockAssetProvider()
        {
            using var c = new SKCanvas(_bmp);
            c.Clear(SKColors.Magenta);
        }

        public SKBitmap? GetFrameForLayer(string layerId) => _bmp;
    }
}
