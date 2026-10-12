using System.IO;
using JizuraLive.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JizuraLive.App;

/// <summary>
/// 工程音频的自动备份（cache\audio\autosave_song.ext，对位前端 J.saveSong）：
/// 原路径被移动或删除后重启仍能无缝恢复。
///
/// 去重靠一份小记录（cache\audio\.autosave.json 里的源路径+字节数+修改时间）：同一份源文件
/// 反复装载（云音乐命中缓存后重新分析、重启恢复同一首）不必再整份复制一遍；
/// 源文件变了（改过/换歌）就照常重拷，备份记录只在拷贝确实发生后更新。
/// </summary>
public static class AutosaveAudioCache
{
    private sealed record Meta(string Src, long Size, long MtimeUtc, string Dest, long DestSize);

    public static string MetaPath => Path.Combine(AppDirs.AudioCacheDir, ".autosave.json");

    /// <summary>把音频备份一份到缓存目录（已是最新则跳过拷贝）。返回备份路径；源文件不存在返回 null。</summary>
    public static string? Mirror(string path)
    {
        if (!File.Exists(path)) return null;
        Directory.CreateDirectory(AppDirs.AudioCacheDir);
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) ext = ".mp3";
        var dest = Path.Combine(AppDirs.AudioCacheDir, "autosave_song" + ext);
        if (SamePath(path, dest)) return dest; // 源就是备份本体：没有可拷的
        var src = new FileInfo(path);
        var meta = Read();
        if (meta != null && SamePath(meta.Src, path) && meta.Size == src.Length
            && meta.MtimeUtc == src.LastWriteTimeUtc.Ticks && SamePath(meta.Dest, dest)
            && BackupIntact(dest, meta.DestSize))
        {
            Log.Debug("[音频缓存] 备份已是最新，跳过拷贝：" + Path.GetFileName(dest));
            return dest;
        }

        File.Copy(path, dest, overwrite: true);
        Write(new Meta(path, src.Length, src.LastWriteTimeUtc.Ticks, dest, new FileInfo(dest).Length));
        return dest;
    }

    private static bool BackupIntact(string dest, long size)
    {
        try { return File.Exists(dest) && new FileInfo(dest).Length == size; }
        catch { return false; }
    }

    private static Meta? Read()
    {
        try
        {
            if (!File.Exists(MetaPath)) return null;
            var j = JObject.Parse(File.ReadAllText(MetaPath));
            var src = j.Value<string>("src");
            var dest = j.Value<string>("dest");
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(dest)) return null;
            return new Meta(src, j.Value<long?>("size") ?? 0, j.Value<long?>("mtimeUtc") ?? 0, dest,
                j.Value<long?>("destSize") ?? 0);
        }
        catch (Exception ex)
        {
            Log.Debug("[音频缓存] 备份记录读取失败（忽略）：" + ex.Message);
            return null;
        }
    }

    private static void Write(Meta m)
    {
        try
        {
            var j = new JObject
            {
                ["src"] = m.Src,
                ["size"] = m.Size,
                ["mtimeUtc"] = m.MtimeUtc,
                ["dest"] = m.Dest,
                ["destSize"] = m.DestSize,
            };
            AtomicFile.Write(MetaPath, j.ToString(Formatting.None));
        }
        catch (Exception ex)
        {
            Log.Debug("[音频缓存] 备份记录落盘失败（下次重拷）：" + ex.Message);
        }
    }

    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}
