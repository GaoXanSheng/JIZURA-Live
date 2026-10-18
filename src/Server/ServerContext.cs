using JizuraLive.Server.KuGou;
using JizuraLive.Server.Netease;

namespace JizuraLive.Server;

internal static class ServerContext
{
    public static KuGouClient KuGou { get; } = new();

    public static NeteaseClient Netease { get; } = new();
}
