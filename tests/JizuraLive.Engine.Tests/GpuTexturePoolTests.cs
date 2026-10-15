using JizuraLive.Render.Draw;
using JizuraLive.Render.Effects;
using JizuraLive.Render.Gpu;
using Vortice.Direct2D1;
using Vortice.DXGI;
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

    /// <summary>
    /// 附件（挂在 entry 上的派生资源，如包住纹理的 D2D 位图）随 entry 一起走：
    /// 淘汰即释放、池 Dispose 时连租出面上的也释放。这条是「位图钉住已淘汰纹理」那个漏法的机制闸。
    /// </summary>
    [GpuFact]
    public void Attachment_DiesWithEntry_OnEvictionAndOnPoolDispose()
    {
        WithPool(GpuTexturePool.DefaultFreeCacheBudgetBytes, 1, (_, pool) =>
        {
            // ① 淘汰：上限 1 → 先归还的那张被挤出去
            var victim = pool.Rent(64, 64);
            var evicted = new TrackedDisposable();
            victim.Attach(evicted);
            var keep = pool.Rent(64, 64);
            pool.Return(victim);
            pool.Return(keep);

            Assert.Equal(IntPtr.Zero, victim.Texture.NativePointer); // 前提：确实发生了淘汰
            Assert.True(evicted.Disposed, "entry 被淘汰时，挂在它上面的派生资源必须一起释放");
            Assert.False(victim.TryGetAttachment(out TrackedDisposable? _), "淘汰后不该还能取到附件");

            // ② 池 Dispose：租出中的面上的附件同样要释放（末帧冻结时最后一张 fx 面就一直是租出的）
            var leased = pool.Rent(96, 96);
            var onLeased = new TrackedDisposable();
            leased.Attach(onLeased);
            pool.Dispose();
            Assert.True(onLeased.Disposed, "池 Dispose 时租出面上的派生资源也必须释放");
        });
    }

    /// <summary>
    /// 附件可以单独摘掉（换代重建：D2D 上下文重建后旧位图必须作废，但 entry 本身继续可用），
    /// 且重新挂一份之后取到的必须是新的那份——旧附件不能被当成命中。
    /// </summary>
    [GpuFact]
    public void Attachment_CanBeDroppedPerGeneration_EntryStaysUsable()
    {
        // 上限 0：归还即淘汰，于是"换代清掉 + 归还释放"两条路径在同一个用例里都能观察到
        WithPool(GpuTexturePool.DefaultFreeCacheBudgetBytes, 0, (_, pool) =>
        {
            var e = pool.Rent(64, 64);
            var old = new TrackedDisposable();
            e.Attach(old);
            Assert.True(e.TryGetAttachment<TrackedDisposable>(out var got));
            Assert.Same(old, got);

            e.DisposeAttachment(); // 换代清理
            Assert.True(old.Disposed);
            Assert.False(e.TryGetAttachment(out TrackedDisposable? _));
            Assert.NotEqual(IntPtr.Zero, e.Texture.NativePointer); // entry 本身还活着、还能用

            var fresh = new TrackedDisposable();
            e.Attach(fresh);
            Assert.True(e.TryGetAttachment<TrackedDisposable>(out var got2));
            Assert.Same(fresh, got2); // 换代后取到的是新附件，不是被 Dispose 的旧附件

            pool.Return(e); // 上限 0 → 当场淘汰
            Assert.True(fresh.Disposed, "归还后当场淘汰时，附件同样要释放");
        });
    }

    /// <summary>
    /// 泄漏根因的机制闸：包住纹理的 D2D 位图挂在 entry 上，entry 被淘汰时它必须一起死。
    /// 位图持有底层 DXGI 面的引用——只要它活着，被淘汰的纹理就释放不掉（显存漏）。
    /// 用弱引用观察：旧写法（按 NativePointer 建字典缓存位图）在这里会留下活着的位图，用例失败。
    /// </summary>
    [GpuFact]
    public void AttachedD2DBitmap_DoesNotOutliveEvictedEntry()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();

        using (gpu)
        using (var host = D2DHost.Create(gpu))
        using (var pool = new GpuTexturePool(gpu.Device, GpuTexturePool.DefaultFreeCacheBudgetBytes, 1))
        {
            var victim = pool.Rent(256, 256);
            var weak = AttachBitmap(victim, host);
            var other = pool.Rent(256, 256); // 同时租出 → 必是另一张面
            pool.Return(victim);
            pool.Return(other);              // 上限 1：两张同尺寸归还 → 最旧的 victim 被淘汰

            Assert.Equal(IntPtr.Zero, victim.Texture.NativePointer); // 前提：确实发生了淘汰

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.False(weak.IsAlive,
                "被淘汰 entry 上的 D2D 位图仍然存活：它持有底层 DXGI 面的引用，会一直钉住这张纹理（显存只增不减）");
        }
    }

    /// <summary>
    /// 默认每尺寸上限必须覆盖**现目录里最长的一条同尺寸 pass 链**。
    /// 上限小于链长时，池在稳态下永远进不了固定复用集（每帧新分配 + 每帧淘汰，1080p 一张 7.91MiB）。
    /// 有人往预设/目录里加更长的链时这条闸会失败，提醒同步抬上限或让执行器提前归还。
    /// </summary>
    [GpuFact]
    public void DefaultFreeCapCoversLongestSameSizeChainOfShippedPresets()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();

        using (gpu)
        using (var runner = new GpuEffectRunner(gpu))
        {
            var longest = 0;
            var worst = "(无)";
            foreach (var preset in EffectPresets.All)
            {
                var graph = preset.Build().BuildRenderGraph(runner.Shaders, 0f, 1920, 1080);
                var perSize = graph.Passes
                    .GroupBy(p => (W: (int)Math.Round(1920 * p.Scale), H: (int)Math.Round(1080 * p.Scale)))
                    .Max(g => g.Count());
                if (perSize > longest)
                {
                    longest = perSize;
                    worst = preset.Id;
                }
            }

            Assert.True(GpuTexturePool.DefaultMaxFreePerSize >= longest,
                $"最长同尺寸链是预设「{worst}」的 {longest} 段，超过每尺寸空闲上限 "
                + $"{GpuTexturePool.DefaultMaxFreePerSize}——稳态会每帧新分配并淘汰，池化名存实亡");
        }
    }

    /// <summary>
    /// 稳态零分配：一帧里同尺寸 pass 数不超过每尺寸上限时，归还不得触发淘汰，
    /// 下一轮必须原样复用同一批实例（池化的目的就是稳态不分配）。
    /// </summary>
    [GpuFact]
    public void SteadyState_ChainWithinCap_ReusesWithoutEviction()
    {
        // 8 = 默认上限，也是「现目录最长同尺寸链（5）」之上留了余量的档位
        const int chain = 8;
        Assert.True(GpuTexturePool.DefaultMaxFreePerSize >= chain,
            "默认每尺寸上限已小于本用例的链长，用例前提不成立");

        WithPool(GpuTexturePool.DefaultFreeCacheBudgetBytes, GpuTexturePool.DefaultMaxFreePerSize, (_, pool) =>
        {
            var first = new List<GpuTexturePool.Entry>();
            for (var i = 0; i < chain; i++) first.Add(pool.Rent(512, 512));
            foreach (var e in first) pool.Return(e);

            var afterFirst = pool.GetStats();
            Assert.Equal(0, afterFirst.Evictions);
            Assert.Equal(chain, afterFirst.FreeCount);

            var second = new List<GpuTexturePool.Entry>();
            for (var i = 0; i < chain; i++) second.Add(pool.Rent(512, 512));

            foreach (var e in second) Assert.Contains(e, first); // 引用相等：全是复用来的
            var afterSecond = pool.GetStats();
            Assert.Equal(0, afterSecond.Evictions);
            Assert.Equal(afterFirst.AllocatedBytes, afterSecond.AllocatedBytes); // 一张都没新建

            foreach (var e in second) pool.Return(e);
        });
    }

    /// <summary>把一张 entry 的纹理包成 D2D 位图挂上去（模拟 PvStageRenderer.BitmapFor），返回它的弱引用。</summary>
    private static WeakReference AttachBitmap(GpuTexturePool.Entry entry, D2DHost host)
    {
        using var surface = entry.Texture.QueryInterface<IDXGISurface>();
        var bmp = host.Ctx.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            96f, 96f, BitmapOptions.None));
        entry.Attach(bmp);
        return new WeakReference(bmp);
    }

    private sealed class TrackedDisposable : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
