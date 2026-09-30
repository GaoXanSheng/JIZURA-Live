using System.Net.Http;
using System.Text;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace JizuraLive.Server;

/// <summary>
/// 渲染调试面板的服务端辅助端点：
///   GET /api/perf/cdp-targets?port= —— 服务端转发 CEF CDP 的 /json/list，
///   页面直连被 CORS 拦截时的兜底（CDP 需在设置里开启调试端口并重启应用）。
/// </summary>
internal sealed class PerfController : WebApiController
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    [Route(HttpVerbs.Get, "/api/perf/cdp-targets")]
    public async Task CdpTargets()
    {
        int port = 9222;
        if (int.TryParse(HttpContext.Request.QueryString["port"], out int p) && p is >= 1 and <= 65535)
            port = p;
        try
        {
            var body = await Http.GetStringAsync($"http://127.0.0.1:{port}/json/list");
            await HttpContext.SendStringAsync(body, "application/json; charset=utf-8", Encoding.UTF8);
        }
        catch
        {
            Response.StatusCode = 502;
            await HttpContext.SendStringAsync("{\"ok\":false,\"error\":\"CDP 端口不可达（设置里开启调试端口并重启应用）\"}",
                "application/json; charset=utf-8", Encoding.UTF8);
        }
    }
}