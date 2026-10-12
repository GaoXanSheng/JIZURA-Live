using System.IO;
using JizuraLive.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JizuraLive.App.Cloud;

/// <summary>
/// 云音乐下载记录索引（cloud\downloads.json）：歌曲身份（平台+ID+音质）→ 已下载的本地文件。
///
/// 为什么要有记录：光靠「歌手 - 歌名.ext」这个文件名判断缓存，装载前还是得先请求一次 URL 接口
/// （离线/接口失效时本地文件就用不上），而且换音质（128k/320k 都落 mp3）会被同名文件静默命中，
/// 试听片段也会被当成完整音轨长期复用。记录里存下音质、字节数、是否试听，装载就能先查记录再决定
/// 是直接复用、还是重下覆盖。
/// </summary>
public static class AudioIndex
{
    private const string FileName = "downloads.json";

    /// <summary>条数上限（超出按时间淘汰最旧）；一首歌一个音质一条，正常用户远到不了。</summary>
    private const int MaxEntries = 2000;

    public sealed record Entry(string File, long Size, string Ext, string Quality, bool Trial, long Ts,
        string Source, string Identity, string Name, string Singer);

    private static readonly object Gate = new();
    private static Dictionary<string, Entry>? _map;

    public static string IndexPath => Path.Combine(AppDirs.CloudDir, FileName);

    private static string Key(string source, string identity, string quality) => $"{source}:{identity}:{quality}";

    private static Dictionary<string, Entry> Map()
    {
        lock (Gate)
        {
            if (_map != null) return _map;
            var map = new Dictionary<string, Entry>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(IndexPath))
                {
                    foreach (var p in JObject.Parse(File.ReadAllText(IndexPath)).Properties())
                    {
                        var e = p.Value;
                        var file = e.Value<string>("file");
                        if (string.IsNullOrEmpty(file)) continue;
                        map[p.Name] = new Entry(file, e.Value<long?>("size") ?? 0, e.Value<string>("ext") ?? "",
                            e.Value<string>("quality") ?? "", e.Value<bool?>("trial") ?? false,
                            e.Value<long?>("ts") ?? 0, e.Value<string>("source") ?? "",
                            e.Value<string>("identity") ?? "", e.Value<string>("name") ?? "",
                            e.Value<string>("singer") ?? "");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug("[Cloud] 下载记录读取失败（忽略）：" + ex.Message);
            }
            return _map = map;
        }
    }

    /// <summary>记录里的文件名对应的绝对路径；必须落在 cloud 目录内（手改的 JSON 不能指到别处）。</summary>
    public static string? FilePath(string file)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        try
        {
            var root = Path.GetFullPath(AppDirs.CloudDir);
            var full = Path.GetFullPath(Path.Combine(root, file));
            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>文件还在、且长度与下载时一致（长度变了＝被覆盖过/没下完，不能信）。</summary>
    private static bool Valid(Entry e)
    {
        if (e.Size <= 0 || FilePath(e.File) is not { } p || !File.Exists(p)) return false;
        try { return new FileInfo(p).Length == e.Size; }
        catch { return false; }
    }

    /// <summary>
    /// 可复用的记录：同一首歌 + 同一音质 + 文件完好 + 不是试听片段。
    /// 命中就不必请求 URL 接口、更不必下载（离线也能重载）。
    /// </summary>
    public static Entry? Get(string source, string identity, string quality)
    {
        if (identity.Length == 0) return null;
        var e = GetAny(source, identity);
        if (e == null || e.Quality != quality || e.Trial) return null;
        return Valid(e) ? e : null;
    }

    /// <summary>这首歌的最近一条记录（不限音质、不校验文件）：用来判断同名旧文件能否沿用。</summary>
    public static Entry? GetAny(string source, string identity)
    {
        if (identity.Length == 0) return null;
        lock (Gate)
        {
            Entry? best = null;
            var prefix = $"{source}:{identity}:";
            foreach (var (k, v) in Map())
                if (k.StartsWith(prefix, StringComparison.Ordinal) && (best == null || v.Ts > best.Ts))
                    best = v;
            return best;
        }
    }

    /// <summary>
    /// 缓存目录里那份同名文件能不能直接沿用：没有记录（本次改动前下载的老缓存）→ 沿用现有口径；
    /// 有记录则要求音质一致、不是「以前只下到试听片段」、且字节数对得上——否则重下覆盖。
    /// </summary>
    public static bool CanReuseExisting(Entry? prev, string quality, bool trial)
    {
        if (prev == null) return true;
        if (prev.Quality != quality) return false;
        if (prev.Trial && !trial) return false;
        return Valid(prev);
    }

    /// <summary>分配缓存文件名：基准「歌手 - 歌名.ext」；已被别的歌占用就加「 (2)」「 (3)」后缀。</summary>
    public static string ResolveFileName(string source, string identity, string singer, string name, string ext)
    {
        var baseName = SongLoader.AudioFileName(singer, name, ext);
        var stem = Path.GetFileNameWithoutExtension(baseName);
        var suffix = Path.GetExtension(baseName);
        lock (Gate)
        {
            var taken = Map().Values
                .Where(v => !(v.Source == source && v.Identity == identity))
                .Select(v => v.File)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!taken.Contains(baseName)) return baseName;
            for (var n = 2; n < 100; n++)
            {
                var candidate = $"{stem} ({n}){suffix}";
                if (!taken.Contains(candidate)) return candidate;
            }
            return baseName;
        }
    }

    /// <summary>下载成功后记账（试听也记，但打标记——它不参与命中）。</summary>
    public static void Put(string source, string identity, string quality, string file, long size, string ext,
        bool trial, string name, string singer)
    {
        if (identity.Length == 0 || string.IsNullOrEmpty(file)) return;
        lock (Gate)
        {
            var map = Map();
            map[Key(source, identity, quality)] = new Entry(file, size, ext, quality, trial,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), source, identity, name, singer);
            if (map.Count > MaxEntries)
                foreach (var k in map.OrderBy(kv => kv.Value.Ts).Take(map.Count - MaxEntries).Select(kv => kv.Key)
                             .ToList())
                    map.Remove(k);
            Persist(map);
        }
    }

    /// <summary>删掉指向已删文件/长度不符的记录（启动自愈清理调用）。返回清理条数。</summary>
    public static int Prune()
    {
        lock (Gate)
        {
            var map = Map();
            var dead = map.Where(kv => !Valid(kv.Value)).Select(kv => kv.Key).ToList();
            foreach (var k in dead) map.Remove(k);
            if (dead.Count > 0) Persist(map);
            return dead.Count;
        }
    }

    /// <summary>cloud 目录被整体清空后重置内存索引（下一次访问重新读盘）。</summary>
    public static void Reload()
    {
        lock (Gate) _map = null;
    }

    private static void Persist(Dictionary<string, Entry> map)
    {
        try
        {
            var j = new JObject();
            foreach (var (k, v) in map)
                j[k] = new JObject
                {
                    ["file"] = v.File,
                    ["size"] = v.Size,
                    ["ext"] = v.Ext,
                    ["quality"] = v.Quality,
                    ["trial"] = v.Trial,
                    ["ts"] = v.Ts,
                    ["source"] = v.Source,
                    ["identity"] = v.Identity,
                    ["name"] = v.Name,
                    ["singer"] = v.Singer,
                };
            Directory.CreateDirectory(AppDirs.CloudDir);
            AtomicFile.Write(IndexPath, j.ToString(Formatting.None));
        }
        catch (Exception ex)
        {
            Log.Debug("[Cloud] 下载记录落盘失败：" + ex.Message);
        }
    }
}
