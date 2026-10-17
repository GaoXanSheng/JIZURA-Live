using System.Diagnostics.CodeAnalysis;
using JizuraLive.Logging;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace JizuraLive.Render.Gpu;

public sealed class GpuTexturePool : IDisposable
{
    public const long DefaultFreeCacheBudgetBytes = 128L << 20;

    public const int DefaultMaxFreePerSize = 8;

    public sealed class Entry : IDisposable
    {
        public ID3D11Texture2D Texture { get; }
        public ID3D11RenderTargetView Rtv { get; }
        public ID3D11ShaderResourceView Srv { get; }
        public int Width { get; }
        public int Height { get; }

        private IDisposable? _attachment;

        internal long ReturnSeq;

        internal Entry(ID3D11Texture2D texture, ID3D11RenderTargetView rtv, ID3D11ShaderResourceView srv, int w, int h)
        {
            Texture = texture;
            Rtv = rtv;
            Srv = srv;
            Width = w;
            Height = h;
        }

        public void Attach(IDisposable resource)
        {
            var previous = _attachment;
            _attachment = resource;
            previous?.Dispose();
        }

        public bool TryGetAttachment<T>([NotNullWhen(true)] out T? resource) where T : class, IDisposable
        {
            resource = _attachment as T;
            return resource is not null;
        }

        public void DisposeAttachment()
        {
            var attachment = _attachment;
            _attachment = null;
            attachment?.Dispose();
        }

        public void Dispose()
        {
            DisposeAttachment();
            Srv.Dispose();
            Rtv.Dispose();
            Texture.Dispose();
        }
    }

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

    private readonly Dictionary<(int W, int H), List<Entry>> _free = new();

    private readonly List<Entry> _all = new();

    private readonly HashSet<Entry> _leased = new();

    private readonly HashSet<(int W, int H)> _warnedDoubleReturn = new();

    private long _returnSeq;
    private int _freeCount;
    private long _freeBytes;
    private long _allocatedBytes;
    private long _evictions;
    private long _doubleReturns;
    private bool _disposed;

    public GpuTexturePool(ID3D11Device device,
        long freeCacheBudgetBytes = DefaultFreeCacheBudgetBytes,
        int maxFreePerSize = DefaultMaxFreePerSize)
    {
        _device = device;
        _freeCacheBudgetBytes = Math.Max(0, freeCacheBudgetBytes);
        _maxFreePerSize = Math.Max(0, maxFreePerSize);
    }

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

    public PoolStats GetStats() =>
        new(_freeCount, _freeBytes, _leased.Count, _allocatedBytes, _evictions, _doubleReturns);

    public IReadOnlyList<Entry> LiveEntries => _all;

    private void TrimFreeCache((int W, int H) key)
    {
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

        while (_freeBytes > _freeCacheBudgetBytes)
        {
            var oldestKey = OldestFreeBucket();
            if (oldestKey is null) break;
            var free = _free[oldestKey.Value];
            var oldest = free[0];
            free.RemoveAt(0);
            DisposeFree(oldest);
            if (free.Count == 0) _free.Remove(oldestKey.Value);
        }
    }

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

    private void DisposeFree(Entry entry)
    {
        _freeCount--;
        _freeBytes -= Bytes(entry);
        _all.Remove(entry);
        _allocatedBytes -= Bytes(entry);
        _evictions++;
        entry.Dispose();
    }

    private static long Bytes(Entry entry) => (long)entry.Width * entry.Height * 4;

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
