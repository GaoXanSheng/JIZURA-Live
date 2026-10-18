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

    [JsonProperty("trans", NullValueHandling = NullValueHandling.Ignore)]
    public string? Trans { get; set; }

    [JsonProperty("roma", NullValueHandling = NullValueHandling.Ignore)]
    public string? Roma { get; set; }

    [JsonProperty("candidates", NullValueHandling = NullValueHandling.Ignore)]
    public List<KgLyricCandidate>? Candidates { get; set; }
}

