using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;

namespace JizuraLive.Server;

public static class HttpClientFactorySetup
{
    public static IServiceProvider ServiceProvider { get; private set; } = null!;
    public static IHttpClientFactory Factory => ServiceProvider.GetRequiredService<IHttpClientFactory>();

    public static void Initialize()
    {
        var services = new ServiceCollection();

        services.AddHttpClient("KuGou", client => { client.Timeout = TimeSpan.FromSeconds(100); });

        services.AddHttpClient("Netease", client => { client.Timeout = TimeSpan.FromSeconds(100); });

        ServiceProvider = services.BuildServiceProvider();
    }
}