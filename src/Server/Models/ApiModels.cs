using Newtonsoft.Json;

namespace JizuraLive.Server;




public class ApiResponse
{
    [JsonProperty("ok")]
    public bool Ok { get; set; }

    [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
    public string? Error { get; set; }
}

public sealed class UploadResponse : ApiResponse
{
    [JsonProperty("outputs", NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? Outputs { get; set; }
}


public sealed class ConvertedItem
{
    public ConvertedItem(string name, long size, long mtime)
    {
        Name = name;
        Size = size;
        Mtime = mtime;
    }

    [JsonProperty("name")] public string Name { get; }
    [JsonProperty("size")] public long Size { get; }
    [JsonProperty("mtime")] public long Mtime { get; }
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
    [JsonProperty("url", NullValueHandling = NullValueHandling.Ignore)] public string? Url { get; set; }
    [JsonProperty("ext", NullValueHandling = NullValueHandling.Ignore)] public string? Ext { get; set; }
    [JsonProperty("quality", NullValueHandling = NullValueHandling.Ignore)] public string? Quality { get; set; }
    [JsonProperty("logged", NullValueHandling = NullValueHandling.Ignore)] public bool? Logged { get; set; }
    [JsonProperty("trial", NullValueHandling = NullValueHandling.Ignore)] public bool? Trial { get; set; }
    [JsonProperty("needVip", NullValueHandling = NullValueHandling.Ignore)] public bool? NeedVip { get; set; }
    [JsonProperty("stale", NullValueHandling = NullValueHandling.Ignore)] public bool? Stale { get; set; }
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
    [JsonProperty("fmt", NullValueHandling = NullValueHandling.Ignore)] public string? Fmt { get; set; }
    [JsonProperty("content", NullValueHandling = NullValueHandling.Ignore)] public string? Content { get; set; }
    // 网易云：翻译(tlyric)/音译(romalrc) LRC，base64，前端按时间戳对齐成注音
    [JsonProperty("trans", NullValueHandling = NullValueHandling.Ignore)] public string? Trans { get; set; }
    [JsonProperty("roma", NullValueHandling = NullValueHandling.Ignore)] public string? Roma { get; set; }
    [JsonProperty("candidates", NullValueHandling = NullValueHandling.Ignore)] public List<KgLyricCandidate>? Candidates { get; set; }
}

public sealed class KgQrResponse : ApiResponse
{
    [JsonProperty("key", NullValueHandling = NullValueHandling.Ignore)] public string? Key { get; set; }
    [JsonProperty("img", NullValueHandling = NullValueHandling.Ignore)] public string? Img { get; set; }
}

public sealed class KgQrCheckResponse : ApiResponse
{
    [JsonProperty("status")] public string Status { get; set; } = "";
    [JsonProperty("nickname", NullValueHandling = NullValueHandling.Ignore)] public string? Nickname { get; set; }
}

public sealed class KgLoginResponse : ApiResponse
{
    [JsonProperty("logged")] public bool Logged { get; set; }
    [JsonProperty("nickname", NullValueHandling = NullValueHandling.Ignore)] public string? Nickname { get; set; }
    [JsonProperty("userid", NullValueHandling = NullValueHandling.Ignore)] public string? UserId { get; set; }
}

public sealed class NePlaylist
{
    [JsonProperty("id")] public string Id { get; set; } = "";
    [JsonProperty("name")] public string Name { get; set; } = "";
    [JsonProperty("count")] public int Count { get; set; }
}

public sealed class NePlaylistsResponse : ApiResponse
{
    [JsonProperty("list")] public List<NePlaylist> List { get; } = [];
}
