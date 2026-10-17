using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using JizuraLive.Logging;

namespace JizuraLive.Spout;

public static class SpoutNames
{
    private const string MapName = "SpoutSenderNames";
    private const string MutexName = MapName + "_SpoutAccessMutex";

    private const int SlotBytes = 256;
    private const int Slots = 256;

    private const int LockWaitMs = 100;

    private const int CacheMs = 500;

    private const int MaxBufferBytes = 1 << 20;

    private static readonly object Gate = new();
    private static string[] _cache = [];
    private static long _cacheTs = long.MinValue / 2;
    private static bool _warned;

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

    public static string DumpRegistry()
    {
        var buf = TryReadBuffer(out var note);
        if (buf == null) return note;
        var sb = new StringBuilder();
        sb.Append($"len={buf.Length}\n");
        for (var i = 0; i < buf.Length; i += SlotBytes)
        {
            var end = i;
            var limit = Math.Min(i + SlotBytes, buf.Length);
            while (end < limit && buf[end] != 0) end++;
            if (end == i) continue;
            var raw = Encoding.ASCII.GetString(buf, i, limit - i).Replace('\0', '.');
            sb.Append($"slot{i / SlotBytes,3} @{i:x4}  {raw.TrimEnd('.')}\n");
        }

        return sb.ToString();
    }

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
