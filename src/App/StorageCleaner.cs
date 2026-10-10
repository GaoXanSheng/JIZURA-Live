using System.IO;
using JizuraLive.Logging;

namespace JizuraLive.App;

/// <summary>
/// 存储与缓存清理（cloud / cache / logs）：
/// 1) 提供各目录大小统计；
/// 2) 启动时后台自动清理孤立临时文件、超期日志与重复解包副本；
/// 3) 设置页提供用户主动一键清理入口，保留当前正在播放/编辑的音频。
/// </summary>
public static class StorageCleaner
{
    public sealed record StorageSummary(long CacheBytes, long CloudBytes, long LogsBytes)
    {
        public double CacheMb => CacheBytes / 1048576.0;
        public double CloudMb => CloudBytes / 1048576.0;
        public double LogsMb => LogsBytes / 1048576.0;
    }

    public static StorageSummary GetSummary()
    {
        return new StorageSummary(
            DirSize(AppDirs.CacheDir),
            DirSize(AppDirs.CloudDir),
            DirSize(AppDirs.LogsDir));
    }

    private static long DirSize(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; }
                catch { }
            }
        }
        catch { }
        return total;
    }

    /// <summary>
    /// 后台自愈清理（进程启动后低优先级跑一次）：
    /// - 清理 *.tmp / *.part 临时文件与 0 字节悬空文件；
    /// - 清理 cache\audio 下历史版本遗留的重复副本（仅保留最新一份与正在引用的文件）；
    /// - 清理超过 7 天的历史日志（jizura.log 除外），总量超 15MB 时从最旧删起。
    /// </summary>
    public static void AutoCleanupAsync(string? activeAudioPath = null)
    {
        Task.Run(() =>
        {
            try
            {
                var tempCleaned = CleanTempFiles(AppDirs.CacheDir) + CleanTempFiles(AppDirs.CloudDir);
                var dupCleaned = DeduplicateAudioCache(activeAudioPath);
                var logsCleaned = TrimLogs(keepDays: 7, maxBytesTotal: 15 * 1024 * 1024);
                if (tempCleaned > 0 || dupCleaned > 0 || logsCleaned > 0)
                    Log.Info($"[StorageCleaner] 自动清理完成：临时文件={tempCleaned}，重复音频={dupCleaned}，旧日志={logsCleaned}");
            }
            catch (Exception ex)
            {
                Log.Warn("[StorageCleaner] 自动清理异常：" + ex.Message);
            }
        });
    }

    /// <summary>一键清理缓存（cache\ 目录）：排除当前激活的音频文件。</summary>
    public static int ClearCache(string? activeAudioPath = null)
    {
        var count = 0;
        if (!Directory.Exists(AppDirs.CacheDir)) return 0;
        foreach (var file in Directory.EnumerateFiles(AppDirs.CacheDir, "*", SearchOption.AllDirectories))
        {
            if (IsSamePath(file, activeAudioPath)) continue;
            try
            {
                File.Delete(file);
                count++;
            }
            catch { }
        }
        return count;
    }

    /// <summary>清理云音乐下载（cloud\ 目录）：排除当前激活的音频文件。</summary>
    public static int ClearCloud(string? activeAudioPath = null)
    {
        var count = 0;
        if (!Directory.Exists(AppDirs.CloudDir)) return 0;
        foreach (var file in Directory.EnumerateFiles(AppDirs.CloudDir, "*", SearchOption.AllDirectories))
        {
            if (IsSamePath(file, activeAudioPath)) continue;
            try
            {
                File.Delete(file);
                count++;
            }
            catch { }
        }
        return count;
    }

    /// <summary>清理日志（logs\ 目录）：保留当前正在写的 jizura.log，删除旧日志和滚动备份。</summary>
    public static int ClearLogs()
    {
        var count = 0;
        if (!Directory.Exists(AppDirs.LogsDir)) return 0;
        foreach (var file in Directory.EnumerateFiles(AppDirs.LogsDir, "*"))
        {
            var name = Path.GetFileName(file);
            if (string.Equals(name, "jizura.log", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                File.Delete(file);
                count++;
            }
            catch { }
        }
        return count;
    }

    private static int CleanTempFiles(string dir)
    {
        var count = 0;
        if (!Directory.Exists(dir)) return 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try
            {
                var fi = new FileInfo(file);
                if (fi.Length == 0 && (fi.Name.StartsWith("embedded_") || fi.Extension is ".tmp" or ".part"))
                {
                    fi.Delete();
                    count++;
                }
                else if (fi.Extension is ".tmp" or ".part" && (DateTime.UtcNow - fi.LastWriteTimeUtc).TotalHours > 1)
                {
                    fi.Delete();
                    count++;
                }
            }
            catch { }
        }
        return count;
    }

    /// <summary>
    /// 去除 cache\audio\ 下按内容或前缀重复的解包文件（如老狼、王婧 - 想把我唱给你听_*.mp3）：
    /// 同名同大小仅保留最后修改的一份，其余安全删除。
    /// </summary>
    private static int DeduplicateAudioCache(string? activeAudioPath)
    {
        var count = 0;
        var dir = AppDirs.AudioCacheDir;
        if (!Directory.Exists(dir)) return 0;
        var files = Directory.GetFiles(dir, "*.*");
        var groups = files.GroupBy(f =>
        {
            var name = Path.GetFileNameWithoutExtension(f);
            var ext = Path.GetExtension(f);
            // 剥掉尾部的 _guid 或 _hash
            var cut = name.LastIndexOf('_');
            var baseName = cut > 0 ? name[..cut] : name;
            return (baseName, ext);
        });

        foreach (var g in groups)
        {
            var list = g.OrderByDescending(f => File.GetLastWriteTimeUtc(f)).ToList();
            if (list.Count <= 1) continue;

            // 保留第一个（最新的），其余如果与当前激活音频不同且大小一致则删除
            var keeper = list[0];
            var keeperLen = new FileInfo(keeper).Length;
            for (int i = 1; i < list.Count; i++)
            {
                var dup = list[i];
                if (IsSamePath(dup, activeAudioPath)) continue;
                try
                {
                    if (new FileInfo(dup).Length == keeperLen)
                    {
                        File.Delete(dup);
                        count++;
                        Log.Info($"[StorageCleaner] 已清理重复缓存文件：{Path.GetFileName(dup)}");
                    }
                }
                catch { }
            }
        }
        return count;
    }

    private static int TrimLogs(int keepDays, long maxBytesTotal)
    {
        var count = 0;
        var dir = AppDirs.LogsDir;
        if (!Directory.Exists(dir)) return 0;
        var files = Directory.GetFiles(dir, "*.log*")
            .Select(f => new FileInfo(f))
            .OrderByDescending(fi => fi.LastWriteTimeUtc)
            .ToList();

        long accumulated = 0;
        var cutoff = DateTime.UtcNow.AddDays(-keepDays);
        foreach (var fi in files)
        {
            if (string.Equals(fi.Name, "jizura.log", StringComparison.OrdinalIgnoreCase)) continue;
            accumulated += fi.Length;
            if (fi.LastWriteTimeUtc < cutoff || accumulated > maxBytesTotal)
            {
                try
                {
                    fi.Delete();
                    count++;
                }
                catch { }
            }
        }
        return count;
    }

    private static bool IsSamePath(string a, string? b)
    {
        if (string.IsNullOrEmpty(b)) return false;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}
