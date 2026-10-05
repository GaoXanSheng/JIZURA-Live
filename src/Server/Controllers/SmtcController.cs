using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using JizuraLive.Server.Smtc;

namespace JizuraLive.Server.Controllers;

// 外部播放器进度（SMTC）查询，前端 cloud/follow.ts 轮询这里驱动跟随模式
public sealed class SmtcController : WebApiController
{
    [Route(HttpVerbs.Get, "/api/smtc/state")]
    public SmtcService.StateSnapshot State()
    {
        return ServerContext.Smtc.GetState();
    }

    // 手动指定跟随的会话；appId 为空恢复自动挑选
    [Route(HttpVerbs.Post, "/api/smtc/select")]
    public SmtcService.StateSnapshot Select([QueryField("appId")] string? appId)
    {
        ServerContext.Smtc.Select(appId);
        return ServerContext.Smtc.GetState();
    }
}
