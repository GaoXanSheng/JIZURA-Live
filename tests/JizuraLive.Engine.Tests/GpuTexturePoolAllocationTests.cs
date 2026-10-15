using JizuraLive.Render.Effects;
using JizuraLive.Render.Gpu;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 效果执行链的稳态托管分配闸（M7-卫生）。两处优化的收益都只是"每帧少分配多少字节"：
/// 空闲缓存从 LinkedList 换成 List（每个 AddLast 一个新节点）、执行器复用输出槽暂存数组
/// （过去每帧 new 一个 Entry?[]）。功能测试全绿也照样每帧在漏托管垃圾，只有分配计数能钉住。
///
/// 无 GPU 环境整类跳过（要真实设备建纹理）。
/// </summary>
public class GpuTexturePoolAllocationTests
{
    private const int W = 320;
    private const int H = 180;

    /// <summary>
    /// 池的 Rent/Return 稳态零分配。前提是**桶不被掏空**：桶空会 <c>_free.Remove(key)</c>，
    /// 下次 Return 重建 List 与字典项，那一笔分配不属于稳态（真实帧路径里池的常驻张数正是按这个前提留的余量）。
    /// </summary>
    [GpuFact]
    public void RentReturnSteadyStateDoesNotAllocate()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();

        using (gpu)
        using (var pool = new GpuTexturePool(gpu.Device,
            GpuTexturePool.DefaultFreeCacheBudgetBytes, GpuTexturePool.DefaultMaxFreePerSize))
        {
            // 预热：把默认每尺寸上限张数都租满再归还，桶里留下上限张
            var warm = new List<GpuTexturePool.Entry>();
            for (var i = 0; i < GpuTexturePool.DefaultMaxFreePerSize; i++) warm.Add(pool.Rent(256, 256));
            foreach (var e in warm) pool.Return(e);

            const int rounds = 200;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var r = 0; r < rounds; r++)
            {
                // 一轮 4 次租/还：桶始终非空（上限 8），不触发淘汰
                var a = pool.Rent(256, 256);
                var b = pool.Rent(256, 256);
                var c = pool.Rent(256, 256);
                var d = pool.Rent(256, 256);
                pool.Return(a);
                pool.Return(b);
                pool.Return(c);
                pool.Return(d);
            }

            var after = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(0L, after - before);
        }
    }

    /// <summary>
    /// 跑同一张渲染图（同尺寸、同 pass 数）的稳态零分配：执行器复用输出槽暂存数组、池复用面，
    /// 两轮之间不该有任何托管分配。渲染图本身的重建（<see cref="EffectGraph.BuildRenderGraph"/>）
    /// 在别处，不在这条断言的范围里——这里量的是 <see cref="GpuEffectRunner.RunGraph"/> 本身。
    /// </summary>
    [GpuFact]
    public void RunGraphSteadyStateDoesNotAllocate()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();

        using (gpu)
        using (var runner = new GpuEffectRunner(gpu))
        using (var input = runner.UploadBgra(NewBgra(W, H), W, H))
        {
            var preset = EffectPresets.All.First(p => p.Id == "vhs");
            var graph = preset.Build().BuildRenderGraph(runner.Shaders, 0f, W, H);
            Assert.True(graph.Count > 1, "本用例要一条多 pass 链，预设链长不足");

            // 池侧预热：把这条链会用到的每个尺寸都先填到每尺寸上限，于是 RunGraph 的 Rent 不会把桶掏空
            // （掏空会让下次 Return 重建 List/字典项——那属于池的"冷启动"，不是稳态）
            foreach (var size in graph.Passes
                         .Select(p => ((int)Math.Round(W * p.Scale), (int)Math.Round(H * p.Scale)))
                         .Distinct())
            {
                var keep = new List<GpuTexturePool.Entry>();
                for (var i = 0; i < GpuTexturePool.DefaultMaxFreePerSize; i++) keep.Add(runner.Pool.Rent(size.Item1, size.Item2));
                foreach (var e in keep) runner.Pool.Return(e);
            }

            // 再跑两轮把执行器的暂存数组涨到位
            for (var i = 0; i < 2; i++)
            {
                var warm = runner.RunGraph(graph, input.Srv, W, H);
                runner.Pool.Return(warm);
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 50; i++)
            {
                var output = runner.RunGraph(graph, input.Srv, W, H);
                runner.Pool.Return(output);
            }

            var after = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(0L, after - before);
        }
    }

    private static byte[] NewBgra(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 40;
            pixels[i + 1] = 80;
            pixels[i + 2] = 160;
            pixels[i + 3] = 255;
        }

        return pixels;
    }
}
