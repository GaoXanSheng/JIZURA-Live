using System.IO;
using System.Security.Cryptography;
using System.Text;
using JizuraLive.Logging;
using JizuraLive.Server.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JizuraLive.App.Cloud;

/// <summary>
/// 歌词落盘缓存（cloud\lyrics\{平台}_{歌曲身份}.json）：装载与版本切换取词前都先查它。
/// 歌词基本不会变，缓存不过期；「清理云缓存」递归删 cloud\ 时一并清掉。
/// 键＝平台 + 歌曲身份（酷狗 Hash / 网易云 Id）；版本切换的候选词用单独的候选键空间（cand_…）。
/// </summary>
public static class LyricCache
{
    private const int MaxStemLength = 80;

    public static string DirPath => Path.Combine(AppDirs.CloudDir, "lyrics");

    /// <summary>候选版本（酷狗切版本下拉）的键前缀：这些响应对应的是「候选 id」而不是歌曲身份。</summary>
    public const string CandidatePrefix = "cand_";

    /// <summary>缓存文件路径：文件名做非法字符清洗，被改动过就缀一段内容哈希防撞名。</summary>
    public static string FileOf(string source, string identity)
    {
        var raw = $"{source}_{identity}";
        var safe = SongLoader.SanitizeFileName(raw);
        if (safe.Length > MaxStemLength) safe = safe[..MaxStemLength];
        if (!string.Equals(safe, raw, StringComparison.Ordinal))
            safe += "_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..8].ToLowerInvariant();
        return Path.Combine(DirPath, safe + ".json");
    }

    /// <summary>命中缓存（含内容非空校验；损坏/残缺的缓存当作没有，下次照常重新请求）。</summary>
    public static KgLyricResponse? Get(string source, string identity)
    {
        if (identity.Length == 0) return null;
        var file = FileOf(source, identity);
        try
        {
            if (!File.Exists(file)) return null;
            var ly = JObject.Parse(File.ReadAllText(file)).ToObject<KgLyricResponse>();
            if (ly is not { Ok: true } || string.IsNullOrEmpty(ly.Content)) return null;
            Log.Info($"[Cloud] 命中歌词缓存：{Path.GetFileName(file)}");
            return ly;
        }
        catch (Exception ex)
        {
            Log.Debug("[Cloud] 歌词缓存读取失败（忽略）：" + ex.Message);
            return null;
        }
    }

    /// <summary>写入缓存（只记成功且有内容的响应；接口失败不记，下次仍然重试）。</summary>
    public static void Put(string source, string identity, KgLyricResponse ly, string? albumId)
    {
        if (identity.Length == 0 || ly is not { Ok: true } || string.IsNullOrEmpty(ly.Content)) return;
        try
        {
            var j = JObject.FromObject(ly);
            j["identity"] = identity;
            j["albumId"] = albumId ?? "";
            j["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Directory.CreateDirectory(DirPath);
            AtomicFile.Write(FileOf(source, identity), j.ToString(Formatting.None));
        }
        catch (Exception ex)
        {
            Log.Debug("[Cloud] 歌词缓存落盘失败：" + ex.Message);
        }
    }
}
