using System.Diagnostics.CodeAnalysis;
using JizuraLive.Logging;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace JizuraLive.Render.Gpu;

/// <summary>
/// 效果机的渲染目标池：按 (宽, 高) 复用「纹理 + RTV + SRV」三元组。
///
/// 为什么池化：效果链是 ping-pong 的，每个 pass 都要一张输出面；逐帧新建纹理会持续产生显存分配
/// 与 COM 对象，几百帧后就是可见的卡顿。池化后稳态下零分配。
///
/// 纹理同时带 RTV（当输出）与 SRV（当下一 pass 的输入），这是 ping-pong 的最小配置。
///
/// 空闲缓存**有上界**（每尺寸 key 的 free 上限 + 全局空闲字节预算）：池化本身是「只进不出」的，
/// 效果图里尺寸一多（多尺度 bloom / 缩放 pass / 图层各自尺寸），长期运行就会攒下整条链各尺寸的面——
/// 再也命中不到，却一直占着显存。上界只作用于**空闲**面；租出中的面永不计入淘汰（见 <see cref="Return"/>）。
/// 这是**空闲缓存**的上界，不是峰值上界：同时租出多少张由调用方（效果链长度 × 尺寸）决定。
/// </summary>
public sealed class GpuTexturePool : IDisposable
{
    /// <summary>
    /// 空闲缓存字节预算的初值：128MiB。
    /// 与 <see cref="DefaultMaxFreePerSize"/> 一样是**待 M7-基准与验收实测调整的初值**——
    /// 具体显存阈值按 DOC/PV-DIRECTOR-SYSTEM.md 第 499 行「M7再据测量定具体显存阈值」由实测数据定。
    /// </summary>
    public const long DefaultFreeCacheBudgetBytes = 128L << 20;

    /// <summary>
    /// 同一 (宽, 高) 允许留在空闲缓存里的张数初值。
    ///
    /// 为什么是 8 而不是 2：这个数是**稳态零分配**的门槛——小于「一帧里同尺寸 pass 数」，
    /// 每帧归还时都会淘汰掉下一帧还要用的面，稳态下每帧新分配、每帧淘汰，池化就名存实亡
    /// （1080p 一张 7.91 MiB，每帧两进两出就是持续 16 MiB 的分配器抖动）。
    /// 现目录里最长的一条同尺寸链是 vhs 预设的 5 段（colorGrade/chromatic/scanline/grain/vignette），
    /// 留到 8 是给它留出用户再叠几段的余量。上界只约束**空闲**面，且全局字节预算仍然兜底，
    /// 所以调大它的代价是常驻复用面（最坏 8×(7.91+1.98+0.49+0.12) ≈ 84 MiB，仍在 128MiB 预算内），
    /// 不是峰值显存。与 <see cref="DefaultFreeCacheBudgetBytes"/> 一样是待 M7 实测调整的初值。
    /// </summary>
    public const int DefaultMaxFreePerSize = 8;

    /// <summary>一张可作渲染目标也可作着色器输入的纹理。</summary>
    public sealed class Entry : IDisposable
    {
        public ID3D11Texture2D Texture { get; }
        public ID3D11RenderTargetView Rtv { get; }
        public ID3D11ShaderResourceView Srv { get; }
        public int Width { get; }
        public int Height { get; }

        /// <summary>派生资源（目前只有包住 <see cref="Texture"/> 的 D2D 位图）：生命周期与本 entry 绑定。</summary>
        private IDisposable? _attachment;

        /// <summary>归还序号（LRU 用）：池内单调递增，只在空闲缓存里有效，用于跨尺寸桶比较「谁更久没用」。</summary>
        internal long ReturnSeq;

        internal Entry(ID3D11Texture2D texture, ID3D11RenderTargetView rtv, ID3D11ShaderResourceView srv, int w, int h)
        {
            Texture = texture;
            Rtv = rtv;
            Srv = srv;
            Width = w;
            Height = h;
        }

        /// <summary>
        /// 挂一份派生资源，生命周期与本 entry 绑定：淘汰或池 <see cref="Dispose"/> 时随 entry 一起释放
        /// （重复挂会先释放上一份，不叠加）。
        ///
        /// 为什么必须挂在 entry 上，而不是让调用方按纹理指针另建一张缓存表：
        /// <list type="number">
        /// <item>派生资源（D2D 位图）持有底层 DXGI 面的引用，它活着这张纹理就释放不掉——
        /// 「按指针缓存」会同时漏掉缓存表本身与它钉住的显存。</item>
        /// <item>纹理被淘汰后指针会被 COM 分配器复用，按指针查表会把旧派生资源当成新纹理的命中。</item>
        /// </list>
        /// 绑到 entry 身份上，这两条一起消失（契约见 DOC/PV-DIRECTOR-SYSTEM.md「不能只凭 NativePointer 跨生命周期命中旧 Bitmap」）。
        /// </summary>
        public void Attach(IDisposable resource)
        {
            var previous = _attachment;
            _attachment = resource;
            previous?.Dispose();
        }

        /// <summary>取当前附件（没挂过或类型不符返回 false）。</summary>
        public bool TryGetAttachment<T>([NotNullWhen(true)] out T? resource) where T : class, IDisposable
        {
            resource = _attachment as T;
            return resource is not null;
        }

        /// <summary>摘掉并释放附件，entry 本身继续存活：按代际重建派生资源时用（见 PvStageRenderer.ClearFxBitmapCache）。</summary>
        public void DisposeAttachment()
        {
            var attachment = _attachment;
            _attachment = null;
            attachment?.Dispose();
        }

        public void Dispose()
        {
            // 先释放附件再释放纹理：D2D 位图持有 Texture 的 DXGI 面引用，反过来顺序等于让纹理多活一轮
            DisposeAttachment();
            Srv.Dispose();
            Rtv.Dispose();
            Texture.Dispose();
        }
    }

    /// <summary>池的计数快照（诊断与验收用；淘汰只影响空闲缓存，不影响租出中的面）。</summary>
    public readonly record struct PoolStats(
        int FreeCount,
        long FreeBytes,
        int LeasedCount,
        long AllocatedBytes,
        long Evictions,
        long DoubleReturns);

    private readonly ID3D11Device _device;
    private readonly long _freeCacheBudgetBytes;
    private readonly int _maxFreePerSize;

    /// <summary>
    /// 空闲缓存：每尺寸一个列表，尾 = 最近归还（优先被租出），头 = 最久未用（优先被淘汰）。
    ///
    /// 为什么是 <see cref="List{T}"/> 而不是 <see cref="LinkedList{T}"/>：后者每次 AddLast 都要
    /// 新分配一个节点对象——稳态下每帧 Rent/Return 十几个面就是十几个节点（实测每帧约 440 字节托管垃圾，
    /// 见 tests 的 GpuTexturePoolAllocationTests）。List 的尾部增删不分配，桶内张数有上限（8），
    /// 从头部淘汰的 O(n) 位移可以忽略。
    /// </summary>
    private readonly Dictionary<(int W, int H), List<Entry>> _free = new();

    /// <summary>池仍然持有（含租出中）的全部 entry——Dispose 时逐个释放，淘汰时摘除。</summary>
    private readonly List<Entry> _all = new();

    /// <summary>租出中的 entry（引用相等）：既是租出计数，也是「重复归还」的判据。</summary>
    private readonly HashSet<Entry> _leased = new();

    /// <summary>已就重复归还告警过的尺寸（同一尺寸只提示一次，避免刷日志）。</summary>
    private readonly HashSet<(int W, int H)> _warnedDoubleReturn = new();

    private long _returnSeq;
    private int _freeCount;
    private long _freeBytes;
    private long _allocatedBytes;
    private long _evictions;
    private long _doubleReturns;
    private bool _disposed;

    /// <param name="freeCacheBudgetBytes">
    /// 空闲缓存字节上界；记账口径固定 <c>width * height * 4</c>（池内纹理恒为 BGRA8），
    /// 不是精确显存占用（不含对齐/驱动开销）。默认 <see cref="DefaultFreeCacheBudgetBytes"/>。
    /// </param>
    /// <param name="maxFreePerSize">同一 (宽, 高) 允许留在空闲缓存里的张数。默认 <see cref="DefaultMaxFreePerSize"/>。</param>
    public GpuTexturePool(ID3D11Device device,
        long freeCacheBudgetBytes = DefaultFreeCacheBudgetBytes,
        int maxFreePerSize = DefaultMaxFreePerSize)
    {
        _device = device;
        // 负值没有意义，按 0 处理（0 = 不缓存任何空闲面，仍然可用）
        _freeCacheBudgetBytes = Math.Max(0, freeCacheBudgetBytes);
        _maxFreePerSize = Math.Max(0, maxFreePerSize);
    }

    /// <summary>取一张 w×h 的渲染目标（同尺寸优先复用；优先给最近归还的那张）。</summary>
    public Entry Rent(int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        width = Math.Max(1, width);
        height = Math.Max(1, height);

        if (_free.TryGetValue((width, height), out var free) && free.Count > 0)
        {
            var reused = free[^1];
            free.RemoveAt(free.Count - 1);
            _freeCount--;
            _freeBytes -= Bytes(reused);
            if (free.Count == 0) _free.Remove((width, height));
            _leased.Add(reused);
            return reused;
        }

        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1u, 0u),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
        };
        var texture = _device.CreateTexture2D(desc);
        var entry = new Entry(texture, _device.CreateRenderTargetView(texture),
            _device.CreateShaderResourceView(texture), width, height);
        _all.Add(entry);
        _allocatedBytes += Bytes(entry);
        _leased.Add(entry);
        return entry;
    }

    /// <summary>
    /// 归还。同一张只接受一次：重复归还（或把别的池/已淘汰的面还进来）一律忽略并计入
    /// <see cref="PoolStats.DoubleReturns"/>——否则同一张面会同时挂在空闲缓存的两处，被租给两个消费者。
    ///
    /// 归栈后按「每尺寸上限 + 全局空闲字节预算」从**最久未用**（LRU：归还序号最小）开始淘汰，
    /// 淘汰即 Dispose 并从存活账目里摘除。租出中的面绝不在淘汰范围内——上界约束的是空闲缓存。
    /// </summary>
    public void Return(Entry entry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_leased.Remove(entry))
        {
            _doubleReturns++;
            if (_warnedDoubleReturn.Add((entry.Width, entry.Height)))
                Log.Warn($"[GpuTexturePool] 重复归还 {entry.Width}×{entry.Height} 的面，已忽略"
                         + "（同一尺寸只提示一次；重复归还可能是同一张面被两个消费者同时持有的前兆）");
            return;
        }

        entry.ReturnSeq = ++_returnSeq;
        if (!_free.TryGetValue((entry.Width, entry.Height), out var free))
        {
            free = new List<Entry>();
            _free[(entry.Width, entry.Height)] = free;
        }

        free.Add(entry);
        _freeCount++;
        _freeBytes += Bytes(entry);

        TrimFreeCache((entry.Width, entry.Height));
    }

    /// <summary>池的运行计数快照。Dispose 之后各项归零，不抛异常。</summary>
    public PoolStats GetStats() =>
        new(_freeCount, _freeBytes, _leased.Count, _allocatedBytes, _evictions, _doubleReturns);

    /// <summary>
    /// 池当前持有的全部 entry（含租出中）。给调用方清理挂在 entry 上的派生资源用
    /// （如按代际重建的 D2D 位图，见 <see cref="Entry.DisposeAttachment"/>）。
    /// 只读遍历：不要在遍历过程中 Rent/Return/Dispose 池。
    /// </summary>
    public IReadOnlyList<Entry> LiveEntries => _all;

    /// <summary>
    /// 把空闲缓存收回上界内：先每尺寸上限，再全局字节预算；两段都从最久未用的一端开始。
    /// <paramref name="key"/> 是刚归还的那一桶——只有它会涨，所以每尺寸上限只需检查它（稳态零分配）。
    /// </summary>
    private void TrimFreeCache((int W, int H) key)
    {
        // ① 每尺寸 key 的 free 上限
        if (_free.TryGetValue(key, out var bucket))
        {
            while (bucket.Count > _maxFreePerSize)
            {
                var oldest = bucket[0];
                bucket.RemoveAt(0);
                DisposeFree(oldest);
            }

            if (bucket.Count == 0) _free.Remove(key);
        }

        // ② 全局空闲字节预算。每次淘汰「各桶最旧」里最旧的那张——全局最旧者必然在其桶头。
        while (_freeBytes > _freeCacheBudgetBytes)
        {
            var oldestKey = OldestFreeBucket();
            if (oldestKey is null) break; // 没有可淘汰的空闲面（预算为负不可能出现，防御性收口）
            var free = _free[oldestKey.Value];
            var oldest = free[0];
            free.RemoveAt(0);
            DisposeFree(oldest);
            if (free.Count == 0) _free.Remove(oldestKey.Value);
        }
    }

    /// <summary>各尺寸桶里「最久未用」的那张中，全局最旧者所属的桶。</summary>
    private (int W, int H)? OldestFreeBucket()
    {
        (int W, int H)? best = null;
        var bestSeq = long.MaxValue;
        foreach (var (key, free) in _free)
        {
            var head = free[0];
            if (head.ReturnSeq < bestSeq)
            {
                bestSeq = head.ReturnSeq;
                best = key;
            }
        }

        return best;
    }

    /// <summary>淘汰一张空闲面：摘出存活账目并真正释放（淘汰 = 释放，不是只丢引用）。</summary>
    private void DisposeFree(Entry entry)
    {
        _freeCount--;
        _freeBytes -= Bytes(entry);
        _all.Remove(entry);
        _allocatedBytes -= Bytes(entry);
        _evictions++;
        entry.Dispose();
    }

    /// <summary>记账口径：池内纹理恒为 BGRA8，所以一张面按 width * height * 4 字节计。</summary>
    private static long Bytes(Entry entry) => (long)entry.Width * entry.Height * 4;

    /// <summary>释放全部 entry（含租出中的）并清空账目；幂等。之后 <see cref="Rent"/>/<see cref="Return"/> 会抛。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var e in _all) e.Dispose();
        _all.Clear();
        _free.Clear();
        _leased.Clear();
        _freeCount = 0;
        _freeBytes = 0;
        _allocatedBytes = 0;
    }
}
