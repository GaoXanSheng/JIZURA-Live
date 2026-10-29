using System.Diagnostics;
using System.IO;
using JizuraLive.Logging;

namespace JizuraLive.App;

public static class Storage
{
    public static (long Bytes, int Files) DirSize(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return (0, 0);

        long bytes = 0;
        var files = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    bytes += new FileInfo(path).Length;
                    files++;
                }
                catch (Exception ex)
                {
                    Log.Warn("[存储] 统计文件失败：" + path + "：" + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("[存储] 统计目录失败：" + dir + "：" + ex.Message);
        }

        return (bytes, files);
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024L * 1024) return (bytes / 1024d).ToString("0.#") + " KB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / (1024d * 1024)).ToString("0.#") + " MB";
        return (bytes / (1024d * 1024 * 1024)).ToString("0.##") + " GB";
    }

    public static (int Files, long Bytes) ClearCache(string? keepPath)
    {
        var keep = string.IsNullOrWhiteSpace(keepPath) ? null : SafeFullPath(keepPath);
        var currentLog = SafeFullPath(Path.Combine(AppDirs.LogsDir, "jizura.log"));

        var (cacheFiles, cacheBytes) = DeleteFiles(AppDirs.CacheDir, keep);
        var (logFiles, logBytes) = DeleteFiles(AppDirs.LogsDir, currentLog);

        RemoveEmptyDirs(AppDirs.CacheDir);
        RemoveEmptyDirs(AppDirs.LogsDir);

        return (cacheFiles + logFiles, cacheBytes + logBytes);
    }

    public static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static (int Files, long Bytes) DeleteFiles(string dir, string? keep)
    {
        if (!Directory.Exists(dir)) return (0, 0);

        var files = 0;
        long bytes = 0;
        foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList())
        {
            if (keep is not null && string.Equals(SafeFullPath(path), keep, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var length = new FileInfo(path).Length;
                File.Delete(path);
                files++;
                bytes += length;
            }
            catch (Exception ex)
            {
                Log.Warn("[存储] 删除文件失败：" + path + "：" + ex.Message);
            }
        }

        return (files, bytes);
    }

    private static void RemoveEmptyDirs(string dir)
    {
        if (!Directory.Exists(dir)) return;

        foreach (var sub in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length).ToList())
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(sub).Any()) Directory.Delete(sub);
            }
            catch (Exception ex)
            {
                Log.Warn("[存储] 删除空目录失败：" + sub + "：" + ex.Message);
            }
        }
    }

    private static string SafeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }
}
