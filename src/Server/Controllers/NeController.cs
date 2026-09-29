using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace JizuraLive.Server;

// 网易云在线搜歌 / 取词 / 扫码登录（对应前端 11b_kggkrc.js 的「网易云」来源）
public sealed class NeController : WebApiController
{
    [Route(HttpVerbs.Get, "/api/ne/search")]
    public async Task<KgSearchResponse> Search(
        [QueryField("kw")] string? kw, [QueryField("page")] string? page)
    {
        int p = Math.Max(1, int.TryParse(page, out var v) ? v : 1);
        return await ServerContext.Netease.SearchAsync(kw ?? "", p);
    }

    [Route(HttpVerbs.Get, "/api/ne/url")]
    public Task<KgUrlResponse> Url(
        [QueryField("id")] string? id, [QueryField("quality")] string? quality)
    {
        if (string.IsNullOrEmpty(id)) return Task.FromResult(new KgUrlResponse { Ok = false, Error = "缺少 id" });
        return ServerContext.Netease.GetUrlAsync(id!, quality ?? "128");
    }

    [Route(HttpVerbs.Get, "/api/ne/lyric")]
    public Task<KgLyricResponse> Lyric([QueryField("id")] string? id) =>
        ServerContext.Netease.GetLyricAsync(id);

    [Route(HttpVerbs.Get, "/api/ne/qr")]
    public Task<KgQrResponse> Qr() => ServerContext.Netease.QrAsync();

    [Route(HttpVerbs.Get, "/api/ne/playlists")]
    public Task<PlaylistsResponse> Playlists() => ServerContext.Netease.MyPlaylistsAsync();

    [Route(HttpVerbs.Get, "/api/ne/playlist")]
    public Task<KgSearchResponse> Playlist([QueryField("id")] string? id) =>
        ServerContext.Netease.PlaylistSongsAsync(id);

    [Route(HttpVerbs.Get, "/api/ne/qr/check")]
    public Task<KgQrCheckResponse> QrCheck([QueryField("key")] string? key) =>
        ServerContext.Netease.QrCheckAsync(key ?? "");

    [Route(HttpVerbs.Get, "/api/ne/login")]
    public KgLoginResponse Login() => ServerContext.Netease.LoginState();

    [Route(HttpVerbs.Get, "/api/ne/logout")]
    public KgLoginResponse Logout() => ServerContext.Netease.Logout();

    [Route(HttpVerbs.Get, "/api/ne/audio")]
    public Task Audio([QueryField("u")] string? target) =>
        AudioProxy.StreamAsync(HttpContext, ServerContext.Netease.Http, target);
}
