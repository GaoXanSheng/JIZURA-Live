using JizuraLive.Pv;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Effects;
using JizuraLive.Render.Gpu;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 粒子图层提供者：精灵必须真的落到像素上。
/// 只查 <c>HasContent</c> 是假绿（目标面一直都在），所以这里一律回读数 alpha——
/// 有粒子就该有非透明像素，Density=0 且无爆发就该一片透明（上一帧不许留在面上）。
/// </summary>
public class ParticleLayerProviderTests
{
    private const int W = 640;
    private const int H = 360;

    [GpuFact]
    public void ParticlesRenderVisiblePixels()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();

        using (gpu)
        using (var host = D2DHost.Create(gpu))
        using (var provider = new ParticleLayerProvider("fx_sakura", gpu, host, new PvParticleConfig(), W, H))
        {
            var image = provider.Pump(new PvFrameContext(1.0, 0, 0, W, H));
            Assert.True(image.HasContent);

            using var pixels = ReadbackOrFail(provider, "t=1.0");
            Assert.True(CountVisible(pixels) > 0, "t=1.0 时应画出可见粒子");
        }
    }

    [GpuFact]
    public void ZeroDensityWithoutBurstsStaysTransparent()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();

        var cfg = new PvParticleConfig { Density = 0 };
        using (gpu)
        using (var host = D2DHost.Create(gpu))
        using (var provider = new ParticleLayerProvider("fx_none", gpu, host, cfg, W, H))
        {
            provider.Pump(new PvFrameContext(1.0, 0, 0, W, H));
            using var pixels = ReadbackOrFail(provider, "t=1.0 空场");
            Assert.Equal(0, CountVisible(pixels));
        }
    }

    /// <summary>
    /// 走完整管线：RequestAddParticles 建层 → Prepare 画好粒子 → Composite 上屏。
    /// 粒子层必须在合成之外先画好（它要切自己的离屏面）——进了合成器的绘制批次会把那一批打断，
    /// 整帧一个像素都贴不出去；只测 Pump 的用例看不见这条，所以这里从 Composite 的输出回读。
    /// </summary>
    [GpuFact]
    public void PipelineCompositesParticleLayer()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();

        using (gpu)
        {
            var scene = PvScene.CreateBlank();
            scene.Width = W;
            scene.Height = H;

            using var pipe = new PvPipeline(gpu, scene, new EffectGraph());
            pipe.RequestAddParticles("sparkle");
            pipe.Prepare(1.0);

            var layer = Assert.Single(pipe.Scene.Layers);
            Assert.Equal(PvLayerKind.ForegroundFx, layer.Kind);
            Assert.Equal("星光", layer.Name);
            Assert.Equal(1.25, layer.Parallax.Depth);
            Assert.Equal("sparkle", layer.Particles?.Kind);

            using var target = OffscreenTarget.Create(gpu, pipe.D2DContext, W, H);
            pipe.Composite(target.Bitmap, W, H);
            gpu.Context.Flush();
            using var pixels = target.Readback();

            // 空场景的合成面是不透明黑：有非黑像素才说明粒子真的上了屏
            var lit = 0;
            for (var i = 0; i + 3 < pixels.BgraPixels.Length; i += 4)
                if (pixels.BgraPixels[i] > 8 || pixels.BgraPixels[i + 1] > 8 || pixels.BgraPixels[i + 2] > 8) lit++;

            Assert.True(lit > 0, "粒子层应当有像素合成上屏");
        }
    }

    private static ReadbackImage ReadbackOrFail(ParticleLayerProvider provider, string when) =>
        provider.ReadbackTarget()
        ?? throw new InvalidOperationException($"没有可回读的离屏面（{when}）：Pump 之后才有目标面");

    /// <summary>数非透明像素（BGRA8888 预乘，alpha 在每像素第 4 字节）。</summary>
    private static int CountVisible(ReadbackImage image)
    {
        var pixels = image.BgraPixels;
        var count = 0;
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) count++;

        return count;
    }
}
