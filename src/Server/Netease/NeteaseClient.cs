using System.Net.Http;
using System.Text;
using JizuraLive.Logging;
using JizuraLive.Server.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QRCoder;

namespace JizuraLive.Server.Netease;

public sealed partial class NeteaseClient
{
    public HttpClient Http => HttpClientFactorySetup.Factory.CreateClient("Netease");

    private async Task<JObject> PostAsync(string apiUrl, JObject data, bool eapi, List<string>? setCookies = null)
    {
        var header = new JObject
        {
            ["appver"] = AppVer,
            ["versioncode"] = "140",
            ["buildver"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            ["resolution"] = "1920x1080",
            ["os"] = "pc",
            ["requestId"] = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_0001"
        };
        var csrf = S(NeState(), "__csrf");
        if (csrf.Length > 0) header["__csrf"] = csrf;
        data["header"] = header;

        var cookie =
            $"os=pc; appver={AppVer}; versioncode=140; buildver={header["buildver"]}; resolution=1920x1080";
        var musicU = S(NeState(), "MUSIC_U");
        if (musicU.Length > 0)
        {
            cookie += $"; MUSIC_U={musicU}";
            if (eapi) header["MUSIC_U"] = musicU;         }

        if (csrf.Length > 0) cookie += $"; __csrf={csrf}";

        var postUrl = eapi
            ? "https://interface3.music.163.com/eapi" + apiUrl["/api".Length..]
            : "https://music.163.com" + apiUrl.Replace("/api/", "/weapi/");
        using var req = new HttpRequestMessage(HttpMethod.Post, postUrl);
        req.Headers.TryAddWithoutValidation("Cookie", cookie);
        req.Headers.TryAddWithoutValidation("User-Agent", PcUa);
        req.Headers.TryAddWithoutValidation("Referer", "https://music.163.com");

        if (eapi)
        {
            req.Content = new StringContent("params=" + EapiParams(apiUrl, data.ToString(Formatting.None)),
                Encoding.UTF8, "application/x-www-form-urlencoded");
            using var cts = new CancellationTokenSource(20000);
            using var resp = await Http.SendAsync(req, cts.Token);
            CollectCookies(resp, setCookies);
            var raw = await resp.Content.ReadAsByteArrayAsync(cts.Token);
            string json;
            try
            {
                json = EapiDecrypt(raw);
            }
            catch (Exception ex)
            {
                Log.Debug("EapiDecrypt failed, falling back to UTF8: " + ex);
                json = Encoding.UTF8.GetString(raw);
            }

            return JObject.Parse(json);
        }
        else
        {
            var (p, esk) = WeapiEncrypt(data.ToString(Formatting.None));
            req.Content = new StringContent($"params={Uri.EscapeDataString(p)}&encSecKey={esk}",
                Encoding.UTF8, "application/x-www-form-urlencoded");
            using var cts = new CancellationTokenSource(20000);
            using var resp = await Http.SendAsync(req, cts.Token);
            CollectCookies(resp, setCookies);
            return JObject.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
        }
    }

    private static void CollectCookies(HttpResponseMessage resp, List<string>? sink)
    {
        if (sink == null) return;
        if (resp.Headers.TryGetValues("Set-Cookie", out var lines)) sink.AddRange(lines);
    }

    public async Task<KgSearchResponse> SearchAsync(string kw, int page)
    {
        if (string.IsNullOrWhiteSpace(kw)) return new KgSearchResponse { Ok = false, Error = "缺少关键词" };
        var d = await PostAsync("/api/cloudsearch/pc", new JObject
        {
            ["s"] = kw,
            ["type"] = 1,
            ["limit"] = 30,
            ["offset"] = Math.Max(0, page - 1) * 30
        }, true);
        var r = new KgSearchResponse { Ok = true };
        foreach (var it in d["result"]?["songs"] as JArray ?? new JArray())
        {
            var fee = it.Value<long?>("fee") ?? 0;
            r.List.Add(new KgSong
            {
                Source = "netease",
                Id = it.Value<long?>("id")?.ToString() ?? "",
                Name = it.Value<string>("name") ?? "",
                Singer = string.Join(" / ", (it["ar"] as JArray ?? new JArray())
                    .Select(a => a.Value<string>("name")).Where(n => !string.IsNullOrEmpty(n))),
                Album = it["al"]?.Value<string>("name") ?? "",
                Duration = (it.Value<long?>("dt") ?? 0) / 1000,
                Pay = fee is 1 or 4 ? 1 : 0,
                Cover = it["al"]?.Value<string>("picUrl") ?? ""
            });
        }

        r.Total = d["result"]?.Value<long?>("songCount") ?? r.List.Count;
        return r;
    }

    public async Task<KgUrlResponse> GetUrlAsync(string id, string quality)
    {
        if (string.IsNullOrEmpty(id)) return new KgUrlResponse { Ok = false, Error = "缺少 id" };
        var level = quality switch
        {
            "320" => "exhigh",
            "flac" => "lossless",
            _ => "standard"
        };
        var encodeType = quality == "flac" ? "flac" : "mp3";
        var d = await PostAsync("/api/song/enhance/player/url/v1", new JObject
        {
            ["ids"] = $"[{id}]",
            ["level"] = level,
            ["encodeType"] = encodeType
        }, true);
        var hit = d["data"] is JArray arr && arr.Count > 0 ? arr[0] : null;
        var url = hit?.Value<string>("url");
        if (string.IsNullOrEmpty(url))
        {
            var musicU = S(NeState(), "MUSIC_U");
            return new KgUrlResponse
            {
                Ok = false,
                NeedVip = true,
                Stale = musicU.Length > 0,
                Error = musicU.Length > 0
                    ? "网易云登录可能已失效，请重新扫码登录"
                    : "这首需要网易云 VIP：请点「登录」扫码"
            };
        }

        return new KgUrlResponse
        {
            Ok = true,
            Url = url,
            Ext = (hit?.Value<string>("type") ?? "mp3").ToLowerInvariant(),
            Quality = hit?.Value<string>("level") ?? level,
            Trial = hit?["freeTrialInfo"] != null && hit["freeTrialInfo"]!.Type != JTokenType.Null,
            Logged = S(NeState(), "MUSIC_U").Length > 0
        };
    }

    public async Task<KgLyricResponse> GetLyricAsync(string? id)
    {
        if (string.IsNullOrEmpty(id)) return new KgLyricResponse { Ok = false, Error = "缺少 id" };
        var d = await PostAsync("/api/song/lyric/v1", new JObject
        {
            ["id"] = id,
            ["cv"] = 4796112,
            ["lv"] = 0, ["kv"] = 0, ["tv"] = 0, ["rv"] = 0, ["yv"] = 0, ["ytv"] = 0, ["yrv"] = 0
        }, true);
        var lrc = d["lrc"]?.Value<string>("lyric") ?? "";
        if (lrc.Length == 0) return new KgLyricResponse { Ok = false, Error = "没有找到歌词" };
        var trans = d["tlyric"]?.Value<string>("lyric") ?? "";
        var roma = d["romalrc"]?.Value<string>("lyric") ?? "";
        return new KgLyricResponse
        {
            Ok = true,
            Fmt = "lrc",
            Content = B64(lrc),
            Trans = trans.Length > 0 ? B64(trans) : null,
            Roma = roma.Length > 0 ? B64(roma) : null
        };
    }

}