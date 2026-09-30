using System.Runtime.InteropServices;

namespace JizuraLive.Server;

/// <summary>
/// Windows「GPU Engine」性能计数器（pdh.dll）按 pid 读取 GPU 占用率，无需显卡厂商 SDK。
/// 计数器实例随进程的 GPU 活动动态出现/消失：pid 集合变化、采样失败或长期缺实例时整树重建。
/// Utilization Percentage 是速率型计数器，需要连续两次采集才有有效值（首帧返回 -1 = 未知）。
/// </summary>
internal sealed class GpuCounter : IDisposable
{
    private const int PdhMoreData = unchecked((int)0x800007D2);
    private const uint PdhFmtDouble = 0x00000200;

    // PDH_FMT_COUNTERVALUE_ITEM_DOUBLE（x64）：szName 指针(8) + CStatus(4)+pad(4) + double(8) = 24 字节
    private const int ItemSize = 24;
    private readonly HashSet<int> _pids = new();
    private long _builtAt;
    private int _collects;
    private IntPtr _counter;
    private bool _missingAny = true;

    private IntPtr _query;

    public void Dispose() => DisposeQuery();

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
    private static extern int PdhAddEnglishCounter(IntPtr query, string counterPath, IntPtr userData,
        out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhGetFormattedCounterArrayDouble(
        IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, byte[]? itemBuffer);

    /// <summary>按 pid 采样 GPU 占用（%）。无实例/样本不足的 pid 不出现在结果里，调用方按未知处理。</summary>
    public Dictionary<int, double> Sample(IReadOnlyCollection<int> pids)
    {
        var result = new Dictionary<int, double>();
        if (pids.Count == 0)
            return result;

        var now = Environment.TickCount64;
        if (_query == IntPtr.Zero || !_pids.SetEquals(pids) || (_missingAny && now - _builtAt > 5000))
            Rebuild(pids);
        if (_query == IntPtr.Zero)
            return result;

        if (PdhCollectQueryData(_query) != 0)
        {
            DisposeQuery();
            return result;
        }

        _collects++;
        if (_collects < 2)
            return result;

        _missingAny = ReadAll(pids, result);
        return result;
    }

    private void Rebuild(IReadOnlyCollection<int> pids)
    {
        DisposeQuery();
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0)
        {
            _query = IntPtr.Zero;
            return;
        }

        foreach (var pid in pids)
        {
            // 该 pid 暂无 GPU Engine 实例（尚未创建 D3D 设备或已空闲回收）时添加失败，跳过
            if (PdhAddEnglishCounter(_query, $@"\GPU Engine(pid_{pid}_*)\Utilization Percentage", IntPtr.Zero,
                    out _counter) == 0)
                _pids.Add(pid);
        }

        if (_pids.Count == 0)
        {
            PdhCloseQuery(_query);
            _query = IntPtr.Zero;
            return;
        }

        _builtAt = Environment.TickCount64;
        _collects = 0;
        _missingAny = true;
    }

    private bool ReadAll(IReadOnlyCollection<int> pids, Dictionary<int, double> result)
    {
        uint size = 0, count = 0;
        var st = PdhGetFormattedCounterArrayDouble(_counter, PdhFmtDouble, ref size, ref count, null);
        if (st != PdhMoreData || size == 0)
            return false;
        var buffer = new byte[size];
        if (PdhGetFormattedCounterArrayDouble(_counter, PdhFmtDouble, ref size, ref count, buffer) != 0)
            return false;

        var span = buffer.AsSpan(0, (int)Math.Min(size, buffer.Length));
        var sums = new Dictionary<int, double>();
        for (int i = 0; i < count; i++)
        {
            int off = i * ItemSize;
            var name = Marshal.PtrToStringUni(new IntPtr(MemoryMarshal.Read<long>(span.Slice(off, 8))));
            if (name == null || MemoryMarshal.Read<int>(span.Slice(off + 8, 4)) != 0)
                continue;
            // 实例名形如 pid_1234_luid_0x.._eng_0_engtype_3D
            var us = name.IndexOf('_', 4);
            if (!name.StartsWith("pid_", StringComparison.Ordinal) || us <= 4 ||
                !int.TryParse(name.AsSpan(4, us - 4), out int pid))
                continue;
            double v = MemoryMarshal.Read<double>(span.Slice(off + 16, 8));
            sums[pid] = sums.TryGetValue(pid, out var acc) ? acc + v : v;
        }

        bool all = true;
        foreach (var pid in pids)
        {
            if (sums.TryGetValue(pid, out var g))
                result[pid] = Math.Clamp(g, 0, 100);
            else
                all = false;
        }

        return all;
    }

    private void DisposeQuery()
    {
        if (_query != IntPtr.Zero)
        {
            PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }

        _counter = IntPtr.Zero;
        _pids.Clear();
        _collects = 0;
    }
}