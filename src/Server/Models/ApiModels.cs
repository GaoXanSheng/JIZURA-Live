using Newtonsoft.Json;

namespace JizuraLive.Server.Models;

public class ApiResponse
{
    [JsonProperty("ok")] public bool Ok { get; set; }

    [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
    public string? Error { get; set; }
}

public sealed class KgSong
{
    [JsonProperty("source")] public string Source { get; set; } = "kugou";
    [JsonProperty("hash")] public string Hash { get; set; } = "";
    [JsonProperty("id")] public string Id { get; set; } = "";
    [JsonProperty("albumId")] public string AlbumId { get; set; } = "";
    [JsonProperty("albumAudioId")] public string AlbumAudioId { get; set; } = "";
    [JsonProperty("name")] public string Name { get; set; } = "";
    [JsonProperty("singer")] public string Singer { get; set; } = "";
    [JsonProperty("album")] public string Album { get; set; } = "";
    [JsonProperty("duration")] public long Duration { get; set; }
    [JsonProperty("pay")] public long Pay { get; set; }
    [JsonProperty("cover")] public string Cover { get; set; } = "";
}

public sealed class KgSearchResponse : ApiResponse
{
    [JsonProperty("total")] public long Total { get; set; }
    [JsonProperty("list")] public List<KgSong> List { get; } = [];
}

public sealed class KgUrlResponse : ApiResponse
{
    [JsonProperty("url", NullValueHandling = NullValueHandling.Ignore)]
    public string? Url { get; set; }

    [JsonProperty("ext", NullValueHandling = NullValueHandling.Ignore)]
    public string? Ext { get; set; }

    [JsonProperty("quality", NullValueHandling = NullValueHandling.Ignore)]
    public string? Quality { get; set; }

    [JsonProperty("logged", NullValueHandling = NullValueHandling.Ignore)]
    public bool? Logged { get; set; }

    [JsonProperty("trial", NullValueHandling = NullValueHandling.Ignore)]
    public bool? Trial { get; set; }

    [JsonProperty("needVip", NullValueHandling = NullValueHandling.Ignore)]
    public bool? NeedVip { get; set; }

    [JsonProperty("stale", NullValueHandling = NullValueHandling.Ignore)]
    public bool? Stale { get; set; }
}

public sealed class KgLyricCandidate
{
    [JsonProperty("id")] public string Id { get; set; } = "";
    [JsonProperty("accesskey")] public string AccessKey { get; set; } = "";
    [JsonProperty("from")] public string From { get; set; } = "";
    [JsonProperty("nick")] public string Nick { get; set; } = "";
}

public sealed class KgLyricResponse : ApiResponse
{
    [JsonProperty("fmt", NullValueHandling = NullValueHandling.Ignore)]
    public string? Fmt { get; set; }

    [JsonProperty("content", NullValueHandling = NullValueHandling.Ignore)]
    public string? Content { get; set; }

    // 网易云翻译(tlyric)/音译(romalrc) LRC，base64，前端按时间戳对齐成注音
    [JsonProperty("trans", NullValueHandling = NullValueHandling.Ignore)]
    public string? Trans { get; set; }

    [JsonProperty("roma", NullValueHandling = NullValueHandling.Ignore)]
    public string? Roma { get; set; }

    [JsonProperty("candidates", NullValueHandling = NullValueHandling.Ignore)]
    public List<KgLyricCandidate>? Candidates { get; set; }
}

public sealed class KgQrResponse : ApiResponse
{
    [JsonProperty("key", NullValueHandling = NullValueHandling.Ignore)]
    public string? Key { get; set; }

    [JsonProperty("img", NullValueHandling = NullValueHandling.Ignore)]
    public string? Img { get; set; }
}

public sealed class KgQrCheckResponse : ApiResponse
{
    [JsonProperty("status")] public string Status { get; set; } = "";

    [JsonProperty("nickname", NullValueHandling = NullValueHandling.Ignore)]
    public string? Nickname { get; set; }
}

public sealed class KgLoginResponse : ApiResponse
{
    [JsonProperty("logged")] public bool Logged { get; set; }

    [JsonProperty("nickname", NullValueHandling = NullValueHandling.Ignore)]
    public string? Nickname { get; set; }

    [JsonProperty("userid", NullValueHandling = NullValueHandling.Ignore)]
    public string? UserId { get; set; }
}

/// <summary>歌单归属（两块都实测过：酷狗 /v7/get_all_list 的 is_def + list_create_userid，
/// 网易云 /api/user/playlist 的 specialType + subscribed）。空串＝平台没给，UI 不分组。</summary>
public static class PlaylistKind
{
    public const string Mine = "mine";       // 我创建的
    public const string Subscribed = "sub";  // 我收藏的
    public const string Liked = "liked";     // 平台默认收藏夹（网易云「我喜欢的音乐」/酷狗「默认收藏」「我喜欢」）
}

// 歌单条目，酷狗（global_collection_id）与网易云共用同一形态
public sealed class PlaylistInfo
{
    [JsonProperty("id")] public string Id { get; set; } = "";
    [JsonProperty("name")] public string Name { get; set; } = "";
    [JsonProperty("count")] public int Count { get; set; }

    [JsonProperty("kind", NullValueHandling = NullValueHandling.Ignore)]
    public string Kind { get; set; } = "";

    [JsonProperty("creator", NullValueHandling = NullValueHandling.Ignore)]
    public string Creator { get; set; } = "";

    /// <summary>歌单封面（酷狗 pic 的 {size} 已替换成实尺寸；两平台都可能为空）。</summary>
    [JsonProperty("cover", NullValueHandling = NullValueHandling.Ignore)]
    public string Cover { get; set; } = "";
}

public sealed class PlaylistsResponse : ApiResponse
{
    [JsonProperty("list")] public List<PlaylistInfo> List { get; } = [];
}