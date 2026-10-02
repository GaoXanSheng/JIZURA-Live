using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QRCoder;

namespace JizuraLive.Server;

// 网易云在线搜索/播放地址/歌词/扫码登录。
// 端点与参数对齐 NeteaseCloudMusicApi v4.2.0（搜索与播放地址走 eapi，扫码登录走 weapi），
// 匿名可用；ne_login.json 里有 MUSIC_U 时自动带上解锁 VIP 歌曲
public sealed partial class NeteaseClient
{
    public HttpClient Http => HttpClientFactorySetup.Factory.CreateClient("Netease");

    // 统一加密 POST，apiUrl 形如 /api/cloudsearch/pc（eapi 直用，weapi 时把 /api/ 换成 /weapi/）。
    // setCookies 非空时收集响应 Set-Cookie 头：扫码登录 803 的凭据在这里，不在 body
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
            if (eapi) header["MUSIC_U"] = musicU; // eapi 的加密 header 里也要求带上
        }

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

    // LRC 明文（base64），翻译/音译随行（同为 base64），前端按时间戳对齐成注音
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

    public async Task<KgQrResponse> QrAsync()
    {
        var d = await PostAsync("/api/login/qrcode/unikey", new JObject { ["type"] = 1 }, false);
        var key = d.Value<string>("unikey") ?? "";
        if (key.Length == 0) return new KgQrResponse { Ok = false, Error = "二维码生成失败" };
        var content = $"https://music.163.com/login?codekey={key}";
        string img;
        using (var gen = new QRCodeGenerator())
        using (var data = gen.CreateQrCode(content, QRCodeGenerator.ECCLevel.M))
        using (var png = new PngByteQRCode(data))
        {
            img = "data:image/png;base64," + Convert.ToBase64String(png.GetGraphic(6));
        }

        return new KgQrResponse { Ok = true, Key = key, Img = img };
    }

    public async Task<KgQrCheckResponse> QrCheckAsync(string key)
    {
        if (string.IsNullOrEmpty(key)) return new KgQrCheckResponse { Ok = false, Error = "缺少 key" };
        var cookies = new List<string>();
        var d = await PostAsync("/api/login/qrcode/client/login", new JObject
        {
            ["key"] = key,
            ["type"] = 1
        }, false, cookies);
        var code = d.Value<int?>("code") ?? 0;
        if (code == 803)
        {
            // 登录成功，凭据在 Set-Cookie 头里（MUSIC_U / __csrf）；个别封装会把 cookie 串塞进 body，一并解析
            var patch = new JObject();

            void eat(string raw)
            {
                foreach (var part in raw.Split(';',
                             StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = part.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = part[..eq].Trim(), v = part[(eq + 1)..].Trim();
                    if ((k == "MUSIC_U" || k == "__csrf") && v.Length > 0) patch[k] = v;
                }
            }

            eat(d.Value<string>("cookie") ?? "");
            foreach (var line in cookies) eat(line);
            if (!patch.ContainsKey("MUSIC_U"))
                return new KgQrCheckResponse { Ok = false, Error = "扫码成功但没拿到登录凭据，请点「刷新二维码」重试" };
            patch["nickname"] = d["profile"]?.Value<string>("nickname") ?? "网易云账号";
            NeSave(patch);
        }

        return new KgQrCheckResponse
            { Ok = true, Status = code.ToString(), Nickname = d["profile"]?.Value<string>("nickname") };
    }

    public KgLoginResponse LoginState()
    {
        var st = NeState();
        return new KgLoginResponse
        {
            Ok = true,
            Logged = S(st, "MUSIC_U").Length > 0,
            Nickname = S(st, "nickname")
        };
    }

    public KgLoginResponse Logout()
    {
        NeSave(new JObject { ["MUSIC_U"] = "", ["__csrf"] = "", ["nickname"] = "" });
        return new KgLoginResponse { Ok = true, Logged = false };
    }

    // 歌单歌曲映射成搜索结果同款 DTO，前端渲染零改动
    private static KgSong TrackToSong(JObject it)
    {
        var fee = it.Value<long?>("fee") ?? 0;
        return new KgSong
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
        };
    }

    public async Task<PlaylistsResponse> MyPlaylistsAsync()
    {
        if (S(NeState(), "MUSIC_U").Length == 0)
            return new PlaylistsResponse { Ok = false, Error = "请先扫码登录网易云" };
        var acc = await PostAsync("/api/nuser/account/get", new JObject(), false);
        var uid = acc["profile"]?.Value<long?>("userId")?.ToString()
                  ?? acc["account"]?.Value<long?>("id")?.ToString() ?? "";
        if (uid.Length == 0)
            return new PlaylistsResponse { Ok = false, Error = "登录状态已失效，请重新扫码" };
        var d = await PostAsync("/api/user/playlist", new JObject
        {
            ["uid"] = long.Parse(uid),
            ["limit"] = 60,
            ["offset"] = 0,
            ["includeVideoSong"] = true
        }, false);
        var r = new PlaylistsResponse { Ok = true };
        foreach (var p in d["playlist"] as JArray ?? new JArray())
            r.List.Add(new PlaylistInfo
            {
                Id = p.Value<long?>("id")?.ToString() ?? "",
                Name = p.Value<string>("name") ?? "",
                Count = p.Value<int?>("trackCount") ?? 0
            });

        return r;
    }

    public async Task<KgSearchResponse> PlaylistSongsAsync(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || !long.TryParse(id.Trim(), out _))
            return new KgSearchResponse { Ok = false, Error = "缺少歌单 ID（可粘贴 music.163.com 的歌单链接）" };
        var d = await PostAsync("/api/v6/playlist/detail", new JObject
        {
            ["id"] = long.Parse(id.Trim()),
            ["n"] = 1000
        }, false);
        var r = new KgSearchResponse { Ok = true };
        foreach (var t in d["playlist"]?["tracks"] as JArray ?? new JArray())
            r.List.Add(TrackToSong((JObject)t));
        r.Total = r.List.Count;
        return r;
    }
}