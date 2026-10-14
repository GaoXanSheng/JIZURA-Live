// Spout 发送端枚举：读 Spout2 的「发送端名字注册表」共享内存。
//
// 为什么不走 SpoutDXWrapper：包装层（native\SpoutDXWrapper.dll，C++/CLI，源码不在本仓库）只暴露
// SpoutSender/SpoutReceiver 两组接口，没有 GetSenderCount/GetSender；而 native\SpoutDX.dll 里
// 编入的 spoutSenderNames 用的是标准的进程间共享内存注册表（实测字符串：`SpoutSenderNames`、
// `%s_SpoutAccessMutex`、`spoutSenderNames::SetMaxSenders`），格式稳定且托管侧可直接读——
// 不动 native、不加新依赖。
//
// 布局（2026-10 由 SpoutNamesTests 真机探测钉死，dump 见该测试输出）：
//   · 映射名 "SpoutSenderNames"：65536 字节 = 256 个槽 × 256 字节，槽内是 NUL 结尾的 ASCII 名字；
//     无头部计数，no header——槽 0 的第一个字节就是名字本身，第 i 个名字在 buf[i*256]；
//     槽按名字字典序排列（std::set 遍历序），空槽全 0。
//   · 尾巴字节：写入方只写「名字 + NUL」，不清槽内剩余字节。实测槽里能看到上次注册的残留
//     （`Shoost\0udioSpout\0` = 旧名 ShoostudioSpout 被 Shoost 覆盖的结果）→ 解析必须**遇 NUL 即止**，
//     否则会把残留尾巴当名字读出来。
//   · 访问互斥 "SpoutSenderNames_SpoutAccessMutex"：写入者持锁，读取时短超时拿锁，避免读到半截。
//
// 读不到（本机没跑过任何 Spout 程序 / 拿不到锁 / 尺寸不认识）一律返回空表——调用方（PV 面板）
// 用手填名字兜底，枚举失败绝不影响合成链路。

using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using JizuraLive.Logging;

namespace JizuraLive.Spout;

/// <summary>活跃 Spout 发送端的枚举（只读共享内存注册表，失败即空表）。</summary>
public static class SpoutNames
{
    private const string MapName = "SpoutSenderNames";
    private const string MutexName = MapName + "_SpoutAccessMutex";

    /// <summary>槽大小与槽数（注册表布局常量，见文件头注）。</summary>
    private const int SlotBytes = 256;
    private const int Slots = 256;

    /// <summary>拿不到访问互斥就放弃本次读取（注册表正在被写，宁可这拍空手也别读半截）。</summary>
    private const int LockWaitMs = 100;

    /// <summary>枚举结果缓存（UI 每秒刷、可能被多处调用；注册表本身也是按秒级变化的慢量）。</summary>
    private const int CacheMs = 500;

    /// <summary>注册表缓冲区上限：实测 65536，异常值不猜（返回空表）。</summary>
    private const int MaxBufferBytes = 1 << 20;

    private static readonly object Gate = new();
    private static string[] _cache = [];
    private static long _cacheTs = long.MinValue / 2;
    private static bool _warned;

    /// <summary>
    /// 当前活跃的发送端名（可能为空表：没源在跑 / 注册表读不了）。
    /// <paramref name="refresh"/> = true 绕过缓存（「刷新源列表」按钮用）。
    /// </summary>
    public static IReadOnlyList<string> Enumerate(bool refresh = false)
    {
        lock (Gate)
        {
            var now = Environment.TickCount64;
            if (!refresh && now - _cacheTs < CacheMs) return _cache;
            _cacheTs = now;
            var buf = TryReadBuffer(out _);
            _cache = buf == null ? [] : ParseRegistry(buf).ToArray();
            return _cache;
        }
    }

    /// <summary>
    /// 从注册表原始缓冲区解析名字（探测/测试用，公开以便单测喂合成缓冲区）。
    /// 规则：每 <see cref="SlotBytes"/> 字节一个槽，取首个 NUL 之前的内容；过滤空/不可打印/超长项。
    /// </summary>
    public static List<string> ParseRegistry(byte[] buf)
    {
        var names = new List<string>();
        var max = Math.Min(buf.Length / SlotBytes, Slots);
        for (var i = 0; i < max; i++)
        {
            var start = i * SlotBytes;
            var end = start;
            var limit = start + SlotBytes;
            while (end < limit && buf[end] != 0) end++;
            if (end == start) continue;

            var name = Encoding.ASCII.GetString(buf, start, end - start).Trim();
            if (name.Length == 0) continue;
            if (!IsPrintable(name))
            {
                // 非 ASCII 残留：不是名字。第一次遇到记一条，方便排查布局变化
                if (!_warned)
                {
                    _warned = true;
                    Log.Warn($"[SpoutNames] 注册表槽 {i} 不是可打印名字，跳过：{Convert.ToHexString(buf, start, Math.Min(16, end - start))}");
                }

                continue;
            }

            names.Add(name);
        }

        return names;
    }

    /// <summary>
    /// 诊断用：注册表原始内容（十六进制 + 可见字符），写日志/测试 dump 用。
    /// 读不到返回说明文本，不抛。
    /// </summary>
    public static string DumpRegistry()
    {
        var buf = TryReadBuffer(out var note);
        if (buf == null) return note;
        var sb = new StringBuilder();
        sb.Append($"len={buf.Length}\n");
        for (var i = 0; i < buf.Length; i += SlotBytes)
        {
            // 只印有内容的槽（空槽占绝大多数，全印没意义）
            var end = i;
            var limit = Math.Min(i + SlotBytes, buf.Length);
            while (end < limit && buf[end] != 0) end++;
            if (end == i) continue;
            var raw = Encoding.ASCII.GetString(buf, i, limit - i).Replace('\0', '.');
            sb.Append($"slot{i / SlotBytes,3} @{i:x4}  {raw.TrimEnd('.')}\n");
        }

        return sb.ToString();
    }

    // ---------------- 内部 ----------------

    private static bool IsPrintable(string s)
    {
        foreach (var c in s)
            if (c is < (char)0x20 or > (char)0x7e) return false;
        return true;
    }

    private static byte[]? TryReadBuffer(out string note)
    {
        Mutex? mutex = null;
        var locked = false;
        try
        {
            try
            {
                if (Mutex.TryOpenExisting(MutexName, out var m)) mutex = m;
            }
            catch (Exception ex)
            {
                Log.Debug("[SpoutNames] 打开注册表互斥失败：" + ex.Message);
            }

            if (mutex != null)
            {
                locked = mutex.WaitOne(LockWaitMs);
                if (!locked)
                {
                    note = $"拿不到 {MutexName}（{LockWaitMs}ms），本次不读";
                    return null;
                }
            }

            using var map = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
            using var view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            var cap = view.Capacity;
            if (cap <= 0 || cap > MaxBufferBytes)
            {
                note = $"注册表尺寸异常（{cap}），不读";
                return null;
            }

            var buf = new byte[cap];
            view.ReadArray(0, buf, 0, buf.Length);
            note = "";
            return buf;
        }
        catch (FileNotFoundException)
        {
            // 本机还没跑过任何 Spout 程序：注册表不存在，空表是正确答案，不是错误
            note = "注册表不存在（本机尚无 Spout 程序注册过发送端）";
            return null;
        }
        catch (Exception ex)
        {
            if (!_warned)
            {
                _warned = true;
                Log.Warn("[SpoutNames] 读 Spout 发送端注册表失败（本次运行只记一次）：" + ex.Message);
            }

            note = "读取失败：" + ex.Message;
            return null;
        }
        finally
        {
            if (locked) mutex!.ReleaseMutex();
            mutex?.Dispose();
        }
    }
}
