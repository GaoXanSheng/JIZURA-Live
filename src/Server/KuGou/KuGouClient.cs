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

    // gateway 通用调用，android 签名：query 按 key 排序拼 k=v，前后夹 AndroidSalt、中间夹 body 原文取 MD5
    private async Task<JObject> GatewayAsync(string path, string? router,
        Dictionary<string, string>? extraParams, JObject? body)
    {
        var st = EnsureIdentity();
        var now = ((int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000)).ToString();
        var token = S(st, "token");
        var userid = long.TryParse(S(st, "userid"), out var uid) ? uid : 0;
        var q = new Dictionary<string, string>
        {
            ["dfid"] = S(st, "dfid").Length > 0 ? S(st, "dfid") : "-",
            ["mid"] = S(st, "mid"),
            ["uuid"] = "-",
            ["appid"] = AppId,
            ["clientver"] = ClientVer,
            ["clienttime"] = now
        };
        if (token.Length > 0)
        {
            q["token"] = token;
            q["userid"] = userid.ToString();
        }

        if (extraParams != null)
            foreach (var (k, v) in extraParams)
                q[k] = v;

        var data = body == null ? "" : JsonConvert.SerializeObject(body);
        var plain = string.Concat(q.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => $"{k}={q[k]}"));
        q["signature"] = Md5($"{AndroidSalt}{plain}{data}{AndroidSalt}");
        var qs = string.Join("&", q.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));

        using var req = new HttpRequestMessage(
            body == null ? HttpMethod.Get : HttpMethod.Post,
            $"https://gateway.kugou.com{path}?{qs}");
        if (body != null)
            req.Content = new StringContent(data, new MediaTypeHeaderValue("application/json"));
        if (router != null)
            req.Headers.TryAddWithoutValidation("x-router", router);
        AddGatewayHeaders(req, q["dfid"], now, q["mid"]);
        return await GetJsonAsync(req);
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
        // 不用 mobilecdn.kugou.com：2026-10 起其 DNS 解析到未配置该域名的 CDN 节点，
        // 下发 *.cdn.myqcloud.com 证书导致 TLS 校验失败（RemoteCertificateNameMismatch）；
        // mobiles.kugou.com 是同款 v3 接口，响应结构一致
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
                // 设备注册失败不能炸掉整个回退链，QR 路径对此同样捕获
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

    public async Task<PlaylistsResponse> MyPlaylistsAsync()
    {
        var st = KgState();
        var token = S(st, "token");
        if (token.Length == 0)
            return new PlaylistsResponse { Ok = false, Error = "请先扫码登录酷狗" };
        var myUid = long.TryParse(S(st, "userid"), out var uid) ? uid : 0;
        var r = new PlaylistsResponse { Ok = true };
        // 分页拉全（原来固定 page=1&pagesize=100，歌单超过 100 个会被悄悄截断）；20 页封顶防呆
        for (var page = 1; page <= 20; page++)
        {
            var d = await GatewayAsync("/v7/get_all_list", "cloudlist.service.kugou.com",
                new Dictionary<string, string> { ["plat"] = "1" },
                new JObject
                {
                    ["userid"] = myUid,
                    ["token"] = token,
                    ["total_ver"] = 979,
                    ["type"] = 2,
                    ["page"] = page,
                    ["pagesize"] = 100
                });
            // 成功响应实测 status=1 / error_code=0；第一页就非 1 = 登录态问题（原来会静默变成「没有歌单」）
            if (d.Value<int?>("status") is { } status && status != 1)
            {
                if (page == 1) return new PlaylistsResponse { Ok = false, Error = "酷狗登录状态已失效，请重新扫码" };
                break; // 后续页失败：已读到的照常返回
            }
            if (d["data"]?["info"] is not JArray info) break;
            r.List.AddRange(ParsePlaylistsPage(info, myUid));
            var total = d["data"]?.Value<int?>("list_count") ?? r.List.Count;
            if (info.Count == 0 || r.List.Count >= total) break;
        }

        return r;
    }

    /// <summary>
    /// 解析 /v7/get_all_list 的一页（抽出来是为了能拿真实响应做测试）。
    /// 归属判据实测：is_def 是平台自带的收藏夹（1=默认收藏、2=我喜欢）；is_mine 在这个接口上恒为 0
    /// 不能用，改用 list_create_userid 是否是自己。
    /// </summary>
    public static List<PlaylistInfo> ParsePlaylistsPage(JArray info, long myUid)
    {
        var list = new List<PlaylistInfo>();
        foreach (var p in info)
        {
            var id = p.Value<string>("global_collection_id") ?? "";
            var name = p.Value<string>("name") ?? "";
            if (id.Length == 0 || name.Length == 0) continue;
            var creatorId = p.Value<long?>("list_create_userid") ?? 0;
            list.Add(new PlaylistInfo
            {
                Id = id,
                Name = name,
                Count = p.Value<int?>("count") ?? 0,
                Creator = p.Value<string>("list_create_username") ?? "",
                // 歌单封面与歌曲封面同形：pic 里带 {size} 占位
                Cover = p.Value<string>("pic")?.Replace("{size}", "240") ?? "",
                Kind = (p.Value<int?>("is_def") ?? 0) > 0 ? PlaylistKind.Liked
                    : creatorId > 0 && creatorId == myUid ? PlaylistKind.Mine
                    : PlaylistKind.Subscribed,
            });
        }
        return list;
    }

    // 歌单歌曲映射成搜索结果同款 DTO，前端渲染零改动
    private static KgSong PlSongToSong(JObject it)
    {
        var singers = (it["singerinfo"] as JArray ?? new JArray())
            .Select(x => x.Value<string>("name")).Where(n => !string.IsNullOrEmpty(n));
        return new KgSong
        {
            Hash = it.Value<string>("hash") ?? "",
            AlbumId = it.Value<string>("album_id") ?? "",
            AlbumAudioId = it.Value<long?>("mixsongid")?.ToString() ?? "",
            Name = it.Value<string>("name") ?? "",
            Singer = string.Join(" / ", singers),
            Album = it["albuminfo"]?.Value<string>("name") ?? "",
            Duration = (it.Value<long?>("timelen") ?? 0) / 1000,
            Pay = (it.Value<long?>("feetype") ?? 0) != 0 ? 1 : 0,
            Cover = it.Value<string>("cover")?.Replace("{size}", "240") ?? ""
        };
    }

    public async Task<KgSearchResponse> PlaylistSongsAsync(string? id)
    {
        var gcid = (id ?? "").Trim();
        var m = Regex.Match(gcid, @"[?&](?:global_collection_id|id)=([\w-]+)");
        if (m.Success) gcid = m.Groups[1].Value;
        if (gcid.Length == 0)
            return new KgSearchResponse { Ok = false, Error = "缺少歌单 ID（可粘贴酷狗歌单链接或 global_collection_id）" };
        var token = S(KgState(), "token");
        if (token.Length == 0)
            return new KgSearchResponse { Ok = false, Error = "请先扫码登录酷狗" };

        var r = new KgSearchResponse { Ok = true };
        const int pageSize = 300;
        for (var begin = 0; begin < 1000; begin += pageSize)
        {
            var d = await GatewayAsync("/pubsongs/v2/get_other_list_file_nofilt", null,
                new Dictionary<string, string>
                {
                    ["area_code"] = "1",
                    ["begin_idx"] = begin.ToString(),
                    ["plat"] = "1",
                    ["type"] = "1",
                    ["mode"] = "1",
                    ["personal_switch"] = "1",
                    ["extend_fields"] = "abtags,hot_cmt,popularization",
                    ["pagesize"] = pageSize.ToString(),
                    ["global_collection_id"] = gcid
                }, null);
            if (d.Value<int?>("status") != 1)
            {
                if (r.List.Count > 0) break;
                var msg = d.Value<string>("errmsg") ?? "";
                return new KgSearchResponse
                {
                    Ok = false,
                    Error = "酷狗歌单读取失败" + (msg.Length > 0 ? "：" + msg : "，请确认歌单 ID 或重新扫码登录")
                };
            }

            foreach (var it in d["data"]?["songs"] as JArray ?? new JArray())
                r.List.Add(PlSongToSong((JObject)it));
            var count = d["data"]?.Value<int?>("count") ?? r.List.Count;
            if (r.List.Count >= count) break;
        }

        r.Total = r.List.Count;
        return r;
    }

    public async Task<KgQrResponse> QrAsync()
    {
        var st = EnsureIdentity();
        var (url, _) = SignedUrl("https://login-user.kugou.com", "/v2/qrcode", new Dictionary<string, string>
        {
            ["dfid"] = "-",
            ["mid"] = S(st, "mid"),
            ["appid"] = "1001",
            ["type"] = "1",
            ["plat"] = "4",
            ["qrcode_txt"] = $"https://h5.kugou.com/apps/loginQRCode/html/index.html?appid={AppId}&",
            ["srcappid"] = SrcAppId
        }, false);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        var d = await GetJsonAsync(req);
        var q = d["data"];
        var key = q?.Value<string>("qrcode") ?? "";
        if (key.Length == 0) return new KgQrResponse { Ok = false, Error = "二维码生成失败" };
        return new KgQrResponse { Ok = true, Key = key, Img = q?.Value<string>("qrcode_img") ?? "" };
    }

    public async Task<KgQrCheckResponse> QrCheckAsync(string key)
    {
        if (string.IsNullOrEmpty(key)) return new KgQrCheckResponse { Ok = false, Error = "缺少 key" };
        var st = EnsureIdentity();
        var (url, _) = SignedUrl("https://login-user.kugou.com", "/v2/get_userinfo_qrcode",
            new Dictionary<string, string>
            {
                ["dfid"] = "-",
                ["mid"] = S(st, "mid"),
                ["plat"] = "4",
                ["appid"] = AppId,
                ["srcappid"] = SrcAppId,
                ["qrcode"] = key,
                ["dev"] = S(st, "dev")
            }, false);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        var d = await GetJsonAsync(req);
        var q = d["data"];
        var status = q?.Value<int?>("status");
        var nickname = q?.Value<string>("nickname") ?? "";
        var token = q?.Value<string>("token") ?? "";
        if (status == 4 && token.Length > 0)
        {
            var userId = q?.Value<long?>("userid")?.ToString() is { Length: > 0 } u ? u : "0";
            KgSave(new JObject { ["token"] = token, ["userid"] = userId, ["nickname"] = nickname });
            try
            {
                await RegisterDevAsync(token, long.TryParse(userId, out var uid2) ? uid2 : 0);
            }
            catch (Exception ex)
            {
                Log.Debug("Failed to register KuGou dev: " + ex);
            }
        }

        return new KgQrCheckResponse { Ok = true, Status = status?.ToString() ?? "", Nickname = nickname };
    }

    public KgLoginResponse LoginState()
    {
        var st = KgState();
        return new KgLoginResponse
        {
            Ok = true,
            Logged = S(st, "token").Length > 0,
            Nickname = S(st, "nickname"),
            UserId = S(st, "userid")
        };
    }

    public KgLoginResponse Logout()
    {
        KgSave(new JObject { ["token"] = "", ["userid"] = "", ["nickname"] = "" });
        return new KgLoginResponse { Ok = true, Logged = false };
    }

    private sealed record UrlHit(string Url, string Ext, long TimeLength);
}