using JizuraLive.Pv;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Effects;
using JizuraLive.Render.Gpu;
using Vortice.Mathematics;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// fx 位图缓存的生命周期闸（内存泄漏回归）。
///
/// 旧写法按纹理 NativePointer 建字典缓存位图，而最终输出的纹理每帧都是新的
/// （池的每尺寸空闲上限小于一帧里的同尺寸 pass 数 → 每帧新分配 + 每帧淘汰），
/// 于是字典每帧 +1 条且永不回收；更糟的是位图持有底层 DXGI 面的引用，
/// 被淘汰的纹理也释放不掉——字典与显存一起漏。
///
/// 现在位图挂在池 entry 上、随 entry 复用与淘汰：本组用例钉住
/// ①位图创建数由池的复用集封顶、不随帧数增长；②稳态无淘汰（上限覆盖链长）；
/// ③换代清理仍然有效（旧位图作废、下一帧按当前上下文重建）；④效果链输出真的被画出来了。
/// </summary>
public class FxBitmapCacheTests
{
    private const int W = 1920;
    private const int H = 1080;

    /// <summary>默认启动就是 cinematic 链（colorGrade + bloom(8 pass) + motionBlur + vignette）。</summary>
    private static EffectGraph Cinematic() =>
        EffectPresets.Create("cinematic") ?? throw new InvalidOperationException("预设 cinematic 不存在");

    private static PvScene SingleLayerScene()
    {
        var scene = new PvScene
        {
            Name = "fx-cache",
            Width = W,
            Height = H,
            AmbientBlendEnabled = false,
        };
        scene.Layers.Add(new PvLayerConfig
        {
            Id = "bg",
            Name = "Fill",
            Kind = PvLayerKind.Background,
            Parallax = new PvParallaxConfig { Depth = 0, RotationFactor = 0, ScaleFactor = 0 },
            VisualBlend = new PvVisualBlendConfig
            {
                AmbientTintIntensity = 0, RimGlow = 0, DropShadow = 0, AntiBleedMargin = 0,
            },
        });
        return scene;
    }

    /// <summary>
    /// 连跑多帧后位图创建数必须仍由池的复用集封顶。
    /// 每帧新分配纹理时（旧写法的触发条件）这个数会涨到帧数量级，用例即失败。
    /// </summary>
    [GpuFact]
    public void CinematicChain_RunManyFrames_BitmapCountStaysBounded()
    {
        const int frames = 40;
        const int warmup = 5;

        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();

        using (gpu)
        using (var host = D2DHost.Create(gpu))
        {
            var effects = Cinematic();
            using var renderer = new PvStageRenderer(gpu, host, SingleLayerScene(), effects, W, H);
            renderer.Stage.Register(new ColorProvider("bg", new Color4(0.25f, 0.50f, 0.75f, 1f)));

            using var target = OffscreenTarget.Create(gpu, host.Ctx, W, H);

            for (var i = 0; i < warmup; i++) renderer.Composite(target.Bitmap, W, H);
            var settled = renderer.FxPool.GetStats();

            for (var i = warmup; i < frames; i++) renderer.Composite(target.Bitmap, W, H);
            var final = renderer.FxPool.GetStats();
            gpu.Context.Flush();

            // ① 位图创建数：由池的复用集封顶，不随帧数增长
            Assert.True(renderer.FxBitmapsCreated <= GpuTexturePool.DefaultMaxFreePerSize,
                $"跑了 {frames} 帧却创建了 {renderer.FxBitmapsCreated} 张 fx 位图"
                + $"（上限应为每尺寸空闲上限 {GpuTexturePool.DefaultMaxFreePerSize}）——"
                + "说明最终输出的纹理每帧都是新的，位图缓存随帧数增长（内存泄漏）");

            // ② 稳态无淘汰、无新分配：每尺寸上限覆盖了这条链的同尺寸 pass 数
            Assert.Equal(0, final.Evictions);
            Assert.Equal(settled.AllocatedBytes, final.AllocatedBytes);
            Assert.True(settled.AllocatedBytes > 0, "池里一张面都没有——用例前提不成立");

            // ③ 画面确实画出来了（不是靠跳过位图/跳过合成"省"出来的）
            using var px = target.Readback();
            var c = px.GetPixel(W / 2, H / 2);
            Assert.True(c.Red + c.Green + c.Blue > 60,
                $"效果链跑完后画面接近全黑（R{c.Red} G{c.Green} B{c.Blue}）：合成路径可能没真的执行");
        }
    }

    /// <summary>
    /// 换代清理仍然有效：清掉后旧位图作废、下一帧按当前上下文重建，而池的复用集（entry）不受影响。
    /// </summary>
    [GpuFact]
    public void ClearFxBitmapCache_InvalidatesAndRebuilds_WithoutTouchingPool()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();

        using (gpu)
        using (var host = D2DHost.Create(gpu))
        {
            var effects = Cinematic();
            using var renderer = new PvStageRenderer(gpu, host, SingleLayerScene(), effects, W, H);
            renderer.Stage.Register(new ColorProvider("bg", new Color4(0.3f, 0.3f, 0.3f, 1f)));

            using var target = OffscreenTarget.Create(gpu, host.Ctx, W, H);

            renderer.Composite(target.Bitmap, W, H);
            var createdFirst = renderer.FxBitmapsCreated;
            Assert.True(createdFirst > 0, "第一帧没有创建任何 fx 位图——用例前提不成立");

            var allocated = renderer.FxPool.GetStats().AllocatedBytes;

            renderer.ClearFxBitmapCache();
            Assert.Equal(0, renderer.FxBitmapsCreated); // 计数按代际归零

            renderer.Composite(target.Bitmap, W, H);
            Assert.True(renderer.FxBitmapsCreated > 0, "换代清理后没有重建位图：画面上会是空/旧内容");
            Assert.Equal(allocated, renderer.FxPool.GetStats().AllocatedBytes); // 换代只作废派生资源，不动纹理
        }
    }
}
