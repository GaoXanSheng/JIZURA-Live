using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace JizuraLive.Server;
public sealed class KgController : WebApiController
{
    [Route(HttpVerbs.Get, "/api/kg/search")]
    public async Task<KgSearchResponse> Search(
        [QueryField("kw")] string? kw, [QueryField("page")] string? page)
    {
        int p = Math.Max(1, int.TryParse(page, out var v) ? v : 1);
        return await ServerContext.KuGou.SearchAsync(kw ?? "", p);
    }

    [Route(HttpVerbs.Get, "/api/kg/url")]
    public async Task<KgUrlResponse> Url(
        [QueryField("hash")] string? hash,
        [QueryField("album_id")] string? albumId,
        [QueryField("album_audio_id")] string? albumAudioId,
        [QueryField("quality")] string? quality)
    {
        if (string.IsNullOrEmpty(hash)) return new KgUrlResponse { Ok = false, Error = "缺少 hash" };
        int q = int.TryParse(quality, out var v) ? v : 128;
        return await ServerContext.KuGou.GetUrlAsync(hash!, albumId ?? "0", albumAudioId ?? "0", q);
    }

    [Route(HttpVerbs.Get, "/api/kg/lyric")]
    public Task<KgLyricResponse> Lyric(
        [QueryField("hash")] string? hash,
        [QueryField("id")] string? id,
        [QueryField("accesskey")] string? accesskey,
        [QueryField("album_id")] string? albumId) =>
        ServerContext.KuGou.GetLyricAsync(hash, id, accesskey, albumId ?? "");

    [Route(HttpVerbs.Get, "/api/kg/playlists")]
    public Task<PlaylistsResponse> Playlists() => ServerContext.KuGou.MyPlaylistsAsync();

    [Route(HttpVerbs.Get, "/api/kg/playlist")]
    public Task<KgSearchResponse> Playlist([QueryField("id")] string? id) =>
        ServerContext.KuGou.PlaylistSongsAsync(id);

    [Route(HttpVerbs.Get, "/api/kg/qr")]
    public Task<KgQrResponse> Qr() => ServerContext.KuGou.QrAsync();

    [Route(HttpVerbs.Get, "/api/kg/qr/check")]
    public Task<KgQrCheckResponse> QrCheck([QueryField("key")] string? key) =>
        ServerContext.KuGou.QrCheckAsync(key ?? "");

    [Route(HttpVerbs.Get, "/api/kg/login")]
    public KgLoginResponse Login() => ServerContext.KuGou.LoginState();

    [Route(HttpVerbs.Get, "/api/kg/logout")]
    public KgLoginResponse Logout() => ServerContext.KuGou.Logout();

    [Route(HttpVerbs.Get, "/api/kg/audio")]
    public Task Audio([QueryField("u")] string? target) =>
        AudioProxy.StreamAsync(HttpContext, ServerContext.KuGou.Http, target);
}
