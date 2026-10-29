using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using JizuraLive.Logging;
using JizuraLive.Server.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JizuraLive.Server.KuGou;

public sealed partial class KuGouClient
{
    public HttpClient Http => HttpClientFactorySetup.Factory.CreateClient("KuGou");

    private async Task<JObject> GetJsonAsync(HttpRequestMessage req, int timeoutMs = 20000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        var json = await resp.Content.ReadAsStringAsync(cts.Token);
        return JObject.Parse(json);
    }

    private Task<JObject> GetJsonAsync(string url, int timeoutMs = 20000)
    {
        return GetJsonAsync(new HttpRequestMessage(HttpMethod.Get, url), timeoutMs);
    }

    private async Task<JObject> V5UrlAsync(string hash, string albumId, string albumAudioId, int quality,
        int freePart, string token, long userid)
    {
        var st = EnsureIdentity();
        var now = ((int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000)).ToString();
        var dfid = S(st, "dfid").Length > 0 ? S(st, "dfid") : "-";
        var q = new Dictionary<string, string>
        {
            ["album_id"] = long.TryParse(albumId, out var aid) ? aid.ToString() : "0",
            ["area_code"] = "1",
            ["hash"] = hash.ToLowerInvariant(),
            ["ssa_flag"] = "is_fromtrack",
            ["version"] = "11430",
            ["page_id"] = "151369488",
            ["quality"] = quality.ToString(),
            ["album_audio_id"] = long.TryParse(albumAudioId, out var aaid) ? aaid.ToString() : "0",
            ["behavior"] = "play",
            ["pid"] = "2",
            ["cmd"] = "26",
            ["pidversion"] = "3001",
            ["IsFreePart"] = freePart != 0 ? "1" : "0",
            ["ppage_id"] = "463467626,350369493,788954147",
            ["cdnBackup"] = "1",
            ["module"] = "",
            ["clientver"] = "11430",
            ["dfid"] = dfid,
            ["mid"] = S(st, "mid"),
            ["uuid"] = "-",
            ["appid"] = AppId,
            ["clienttime"] = now
        };
        if (token != "")
        {
            q["token"] = token;
            q["userid"] = userid.ToString();
        }

        q["key"] = Md5($"{q["hash"]}{KeySalt}{AppId}{q["mid"]}{userid}");
        var plain = string.Concat(q.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => $"{k}={q[k]}"));
        q["signature"] = Md5($"{AndroidSalt}{plain}{AndroidSalt}");
        var qs = string.Join("&", q.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://gateway.kugou.com/v5/url?" + qs);
        req.Headers.TryAddWithoutValidation("x-router", "trackercdn.kugou.com");
        AddGatewayHeaders(req, dfid, now, q["mid"]);
        return await GetJsonAsync(req);
    }

    private static UrlHit? PickUrl(JObject d)
    {
        if (d.Value<int?>("status") != 1) return null;
        var url = d["url"] is JArray arr ? arr.FirstOrDefault()?.ToString() : d["url"]?.ToString();
        if (string.IsNullOrEmpty(url)) return null;
        var ext = d.Value<string>("extName")?.ToLowerInvariant() is { Length: > 0 } e ? e : "mp3";
        return new UrlHit(url!, ext, d.Value<long?>("timeLength") ?? 0);
    }

    public async Task<KgSearchResponse> SearchAsync(string kw, int page)
    {
        if (string.IsNullOrWhiteSpace(kw)) return new KgSearchResponse { Ok = false, Error = "缺少关键词" };
        var d = await GetJsonAsync(
            $"https://mobiles.kugou.com/api/v3/search/song?format=json&keyword={Uri.EscapeDataString(kw)}&page={page}&pagesize=20");
        var r = new KgSearchResponse { Ok = true };
        foreach (var it in d["data"]?["info"] as JArray ?? new JArray())
            r.List.Add(new KgSong
            {
                Hash = it.Value<string>("hash") ?? "",
                AlbumId = it["album_id"]?.ToString() ?? "",
                AlbumAudioId = it["album_audio_id"]?.ToString() ?? "",
                Name = it.Value<string>("songname") ?? "",
                Singer = it.Value<string>("singername") ?? "",
                Album = it.Value<string>("album_name") ?? "",
                Duration = it.Value<long?>("duration") ?? 0,
                Pay = it.Value<long?>("pay_type") ?? 0,
                Cover = it["trans_param"]?["union_cover"]?.ToString()?.Replace("{size}", "240") ?? ""
            });

        r.Total = d["data"]?.Value<long?>("total") ?? r.List.Count;
        return r;
    }

    public async Task<KgUrlResponse> GetUrlAsync(string hash, string albumId, string albumAudioId, int quality)
    {
        var st = EnsureIdentity();
        var token = S(st, "token");
        var userid = long.TryParse(S(st, "userid"), out var uid) ? uid : 0;

        if (token.Length > 0)
        {
            var hit = PickUrl(await V5UrlAsync(hash, albumId, albumAudioId, quality, 0, token, userid));
            if (hit == null)
            {
                try
                {
                    await RegisterDevAsync(token, userid);
                }
                catch (Exception ex)
                {
                    Log.Debug("KuGou RegisterDev failed (url path): " + ex);
                }

                hit = PickUrl(await V5UrlAsync(hash, albumId, albumAudioId, quality, 0, token, userid));
            }

            if (hit != null)
                return new KgUrlResponse
                    { Ok = true, Url = hit.Url, Ext = hit.Ext, Quality = quality.ToString(), Logged = true };
        }

        var free = PickUrl(await V5UrlAsync(hash, albumId, albumAudioId, quality, 0, "", 0));
        if (free != null)
            return new KgUrlResponse
                { Ok = true, Url = free.Url, Ext = free.Ext, Trial = false, Logged = false, Quality = quality.ToString() };

        try
        {
            var d = await GetJsonAsync(
                $"https://m.kugou.com/app/i/getSongInfo.php?cmd=playInfo&hash={Uri.EscapeDataString(hash)}");
            var u = d.Value<string>("url") ?? "";
            if (u.Length > 0)
                return new KgUrlResponse
                {
                    Ok = true,
                    Url = u,
                    Ext = d.Value<string>("extName")?.ToLowerInvariant() is { Length: > 0 } e ? e : "mp3",
                    Trial = false,
                    Logged = false,
                    Quality = "128"
                };
        }
        catch (Exception ex)
        {
            Log.Debug("Failed to get KuGou song info: " + ex);
        }

        var trial = PickUrl(await V5UrlAsync(hash, albumId, albumAudioId, 128, 1, "", 0));
        if (trial != null)
            return new KgUrlResponse
                { Ok = true, Url = trial.Url, Ext = trial.Ext, Trial = true, Logged = false, Quality = "128" };

        return new KgUrlResponse
        {
            Ok = false,
            NeedVip = true,
            Stale = token.Length > 0,
            Error = token.Length > 0 ? "酷狗登录可能已失效，请重新扫码登录" : "这首需要酷狗 VIP：请点「登录」扫码"
        };
    }

    public async Task<KgLyricResponse> GetLyricAsync(string? hash, string? id, string? accesskey, string albumId)
    {
        async Task<(string Fmt, string Content)?> grab(string sid, string akey)
        {
            var d = await GetJsonAsync(
                $"https://lyrics.kugou.com/download?ver=1&client=pc&id={Uri.EscapeDataString(sid)}&accesskey={Uri.EscapeDataString(akey)}&fmt=krc&charset=utf8");
            var fmt = "krc";
            if (d.Value<int?>("status") != 200 || (d.Value<string>("content") ?? "").Length == 0)
            {
                d = await GetJsonAsync(
                    $"https://lyrics.kugou.com/download?ver=1&client=pc&id={Uri.EscapeDataString(sid)}&accesskey={Uri.EscapeDataString(akey)}&fmt=lrc&charset=utf8");
                fmt = "lrc";
            }

            var content = d.Value<string>("content") ?? "";
            return d.Value<int?>("status") == 200 && content.Length > 0 ? (fmt, content) : null;
        }

        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(accesskey))
        {
            var hit = await grab(id!, accesskey!);
            if (hit == null) return new KgLyricResponse { Ok = false, Error = "该版本歌词下载失败" };
            return new KgLyricResponse { Ok = true, Fmt = hit.Value.Fmt, Content = hit.Value.Content };
        }

        if (string.IsNullOrEmpty(hash)) return new KgLyricResponse { Ok = false, Error = "缺少 hash" };
        var c = await GetJsonAsync(
            $"https://krcs.kugou.com/search?ver=1&man=yes&client=mobi&keyword=&duration=&hash={Uri.EscapeDataString(hash!)}&album_id={Uri.EscapeDataString(albumId)}");
        var cands = c["candidates"] as JArray ?? new JArray();
        if (cands.Count == 0) return new KgLyricResponse { Ok = false, Error = "没有找到歌词" };
        var first = cands[0];
        var hit2 = await grab(first.Value<string>("id") ?? "", first.Value<string>("accesskey") ?? "");
        if (hit2 == null) return new KgLyricResponse { Ok = false, Error = "歌词下载失败" };

        var candidates = new List<KgLyricCandidate>();
        foreach (var x in cands)
            candidates.Add(new KgLyricCandidate
            {
                Id = x.Value<string>("id") ?? "",
                AccessKey = x.Value<string>("accesskey") ?? "",
                From = x.Value<string>("product_from") ?? "",
                Nick = x.Value<string>("nickname") ?? ""
            });
        return new KgLyricResponse
            { Ok = true, Fmt = hit2.Value.Fmt, Content = hit2.Value.Content, Candidates = candidates };
    }

    public async Task<List<KgLyricCandidate>> GetLyricCandidatesAsync(string? hash, string albumId)
    {
        var list = new List<KgLyricCandidate>();
        if (string.IsNullOrEmpty(hash)) return list;
        var c = await GetJsonAsync(
            $"https://krcs.kugou.com/search?ver=1&man=yes&client=mobi&keyword=&duration=&hash={Uri.EscapeDataString(hash!)}&album_id={Uri.EscapeDataString(albumId)}");
        foreach (var x in c["candidates"] as JArray ?? new JArray())
            list.Add(new KgLyricCandidate
            {
                Id = x.Value<string>("id") ?? "",
                AccessKey = x.Value<string>("accesskey") ?? "",
                From = x.Value<string>("product_from") ?? "",
                Nick = x.Value<string>("nickname") ?? ""
            });
        return list;
    }

    private sealed record UrlHit(string Url, string Ext, long TimeLength);
}