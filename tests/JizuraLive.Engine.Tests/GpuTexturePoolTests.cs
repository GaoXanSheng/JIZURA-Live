using JizuraLive.Render.Gpu;
using Vortice.Mathematics;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// <see cref="GpuTexturePool"/> 的空闲缓存上界与生命周期闸（M7-卫生）。
///
/// 池化不能是「只进不出」：效果图尺寸一多，空闲面会无限攒下去；但上界**只能约束空闲面**——
/// 租出中的面被淘汰就是「消费者手里的纹理突然被释放」，比多占显存严重得多。
/// 这组用例把这两条语义钉住：上界生效（预算 + 每尺寸上限）、淘汰=释放、租出面免疫、重复归还不重复入池。
/// </summary>
public class GpuTexturePoolTests
{
    private const long Budget4MiB = 4L << 20;

    private static D3DDevice RequireGpu()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();
        return gpu;
    }

    /// <summary>建一个指定上界的池跑一段用例（无 GPU 时整个用例由 [GpuFact] 判为跳过）。</summary>
    private static void WithPool(long budget, int cap, Action<D3DDevice, GpuTexturePool> body)
    {
        using var gpu = RequireGpu();
        using var pool = new GpuTexturePool(gpu.Device, budget, cap);
        body(gpu, pool);
    }

    /// <summary>池化语义：同尺寸 rent→return→rent 必须拿回同一个实例（上界改造不许破坏复用）。</summary>
    [GpuFact]
    public void Rent_Return_ReusesSameEntry()
    {
        WithPool(GpuTexturePool.DefaultFreeCacheBudgetBytes, GpuTexturePool.DefaultMaxFreePerSize, (_, pool) =>
        {
            var first = pool.Rent(128, 64);
            pool.Return(first);

            var again = pool.Rent(128, 64);
            Assert.Same(first, again);

            pool.Return(again);
        });
    }

    /// <summary>上界生效：每尺寸桶不超过上限、全局空闲字节不超过预算，且淘汰后池仍然可用。</summary>
    [GpuFact]
    public void FreeCache_IsBoundedByBudgetAndPerSizeCap()
    {
        const int cap = 2;
        WithPool(Budget4MiB, cap, (_, pool) =>
        {
            // ① 每尺寸上限：同尺寸同时租 5 张（都在租出中，必是 5 个不同实例），再全部归还。
            //    上限 2 → 只留最近归还的 2 张，其余 3 张被淘汰（= 释放）。“每个尺寸桶 ≤ cap”在这里
            //    用存活张数观察：留下的必须正好是 cap 张。
            var sameSize = new List<GpuTexturePool.Entry>();
            for (var i = 0; i < 5; i++) sameSize.Add(pool.Rent(64, 64));
            foreach (var e in sameSize) pool.Return(e);

            Assert.Equal(cap, sameSize.Count(e => e.Texture.NativePointer != IntPtr.Zero));
            Assert.Equal(cap, pool.GetStats().FreeCount);

            // ② 全局字节预算：40 个不同尺寸各 rent/return 一轮（256KiB × 40 = 10MiB ≫ 4MiB）
            for (var i = 0; i < 40; i++)
            {
                var e = pool.Rent(256 + i, 256);
                pool.Return(e);
            }

            var stats = pool.GetStats();
            Assert.True(stats.FreeBytes <= Budget4MiB,
                $"空闲缓存 {stats.FreeBytes} 字节超过预算 {Budget4MiB}");
            Assert.True(stats.Evictions > 0, "归还压力远超预算却没有发生淘汰");
            Assert.True(stats.FreeCount > 0, "上界不该把空闲面清空到 0——那样池化就退化成每帧新建");

            // 淘汰是释放资源，不是让池失效：之后仍能正常租到可用的面
            var again = pool.Rent(320, 320);
            Assert.NotEqual(IntPtr.Zero, again.Texture.NativePointer);
            pool.Return(again);
        });
    }

    /// <summary>租出中的面永不被淘汰：淘汰只动空闲面。</summary>
    [GpuFact]
    public void LeasedEntries_AreNeverEvicted()
    {
        WithPool(1L << 20, 2, (gpu, pool) =>
        {
            // 4 张不同尺寸，租出后一直不归还
            var leased = new List<GpuTexturePool.Entry>
            {
                pool.Rent(128, 128),
                pool.Rent(192, 128),
                pool.Rent(256, 128),
                pool.Rent(320, 128),
            };

            // 归还压力：40 个尺寸各一轮（256KiB × 40 ≫ 1MiB 预算），逼出大量淘汰
            for (var i = 0; i < 40; i++)
            {
                var e = pool.Rent(256 + i, 256);
                pool.Return(e);
            }

            var stats = pool.GetStats();
            Assert.True(stats.Evictions > 0, "压力不足、没有发生淘汰——本用例就失去意义了");
            Assert.Equal(leased.Count, stats.LeasedCount);

            foreach (var e in leased)
            {
                // 三个 COM 对象都还活着（Dispose 过的话 NativePointer 归零）
                Assert.NotEqual(IntPtr.Zero, e.Texture.NativePointer);
                Assert.NotEqual(IntPtr.Zero, e.Rtv.NativePointer);
                Assert.NotEqual(IntPtr.Zero, e.Srv.NativePointer);

                // 仍可被绑定使用：绑成渲染目标、清一次、再把 SRV 绑到像素阶段
                gpu.Context.OMSetRenderTargets(e.Rtv, null);
                gpu.Context.ClearRenderTargetView(e.Rtv, new Color4(0f, 0f, 0f, 1f));
                gpu.Context.PSSetShaderResource(0, e.Srv);
            }

            foreach (var e in leased) pool.Return(e);
        });
    }

    /// <summary>重复归还被拒绝：不会让同一张面同时挂在空闲缓存两处（进而发给两个消费者）。</summary>
    [GpuFact]
    public void DoubleReturn_IsRejected_NotDuplicated()
    {
        WithPool(GpuTexturePool.DefaultFreeCacheBudgetBytes, GpuTexturePool.DefaultMaxFreePerSize, (_, pool) =>
        {
            var e = pool.Rent(96, 96);
            pool.Return(e);
            pool.Return(e); // 第二次必须被忽略并计数

            var stats = pool.GetStats();
            Assert.Equal(1, stats.DoubleReturns);
            Assert.Equal(1, stats.FreeCount);

            // 连租两次必须拿到两个不同实例（同一张面不能同时给两个消费者）
            var a = pool.Rent(96, 96);
            var b = pool.Rent(96, 96);
            Assert.NotSame(a, b);
            Assert.NotEqual(IntPtr.Zero, a.Texture.NativePointer);
            Assert.NotEqual(IntPtr.Zero, b.Texture.NativePointer);

            pool.Return(a);
            pool.Return(b);
        });
    }

    /// <summary>淘汰 = 释放（不是只丢引用）：被淘汰的面三个 COM 对象归零，存活字节账目同步下降。</summary>
    [GpuFact]
    public void Eviction_DisposesEntry_AndAccountingShrinks()
    {
        WithPool(Budget4MiB, 2, (_, pool) =>
        {
            // 6 张同尺寸面（512×512×4 = 1MiB）：上限 2 → 最旧的 4 张必被淘汰，第一张必在其中
            var entries = new List<GpuTexturePool.Entry>();
            for (var i = 0; i < 6; i++) entries.Add(pool.Rent(512, 512));

            var victim = entries[0];
            pool.Return(victim);
            var before = pool.GetStats();

            for (var i = 1; i < entries.Count; i++) pool.Return(entries[i]);
            var after = pool.GetStats();

            Assert.Equal(IntPtr.Zero, victim.Texture.NativePointer);
            Assert.Equal(IntPtr.Zero, victim.Rtv.NativePointer);
            Assert.Equal(IntPtr.Zero, victim.Srv.NativePointer);
            Assert.True(after.Evictions > 0, "超出每尺寸上限却没有淘汰");
            Assert.True(after.AllocatedBytes < before.AllocatedBytes,
                $"淘汰后存活字节应下降：{before.AllocatedBytes} → {after.AllocatedBytes}");
        });
    }

    /// <summary>Dispose 释放一切（含租出中的面）、幂等，且之后 Rent/Return 显式抛，不留 use-after-dispose。</summary>
    [GpuFact]
    public void Dispose_ReleasesEverything_AndRentAfterDispose_Throws()
    {
        WithPool(GpuTexturePool.DefaultFreeCacheBudgetBytes, GpuTexturePool.DefaultMaxFreePerSize, (_, pool) =>
        {
            var freed = pool.Rent(64, 64);
            var leased = pool.Rent(128, 96);
            pool.Return(freed); // 一张空闲、一张租出：两种状态都必须被释放

            pool.Dispose();

            Assert.Equal(IntPtr.Zero, freed.Texture.NativePointer);
            Assert.Equal(IntPtr.Zero, leased.Texture.NativePointer);
            Assert.Equal(0, pool.GetStats().AllocatedBytes);

            pool.Dispose(); // 幂等：二次 Dispose 不抛

            Assert.Throws<ObjectDisposedException>(() => { pool.Rent(64, 64); });
            Assert.Throws<ObjectDisposedException>(() => pool.Return(leased));
        });
    }
}
