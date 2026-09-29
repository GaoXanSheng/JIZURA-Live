using System.IO;
using System.Text;
using EmbedIO;
using EmbedIO.WebApi;
using JizuraLive.Server;
using Newtonsoft.Json;

namespace JizuraLive.Server;


public sealed class EmbeddedServer : IDisposable
{
    private const int PortBase = 8642;

    public int Port { get; }

    private readonly WebServer _server;
    private readonly CancellationTokenSource _cts = new();
    private Task? _runTask;

    private EmbeddedServer(int port)
    {
        Port = port;
        _server = new WebServer(o => o
                .WithUrlPrefix($"http://127.0.0.1:{port}/")
                .WithMode(HttpListenerMode.EmbedIO))
            // 静态站必须在前：WebApiModule 未命中路由会直接 404 终结而不是传递，
            // 放前面会把 /render 等 SPA 路径吞掉。API 侧由 StaticSiteModule 的
            // 回退排除（api/ 前缀不回退）保证到达控制器。
            .WithModule(new StaticSiteModule())
            .WithModule(BuildApiModule());    }

    public static EmbeddedServer? Start()
    {
        for (int port = PortBase; port < PortBase + 10; port++)
        {
            var server = new EmbeddedServer(port);
            if (server.TryStart()) return server;
            try { server.Dispose(); } catch { }
        }
        return null;
    }

    private static WebApiModule BuildApiModule()
    {
        var api = new WebApiModule("/", SerializeWithNewtonsoft);
        api.WithController<KgController>();
        api.WithController<NeController>();
        api.WithController<AnalyzeController>();
        return api;
    }

    
    private static async Task SerializeWithNewtonsoft(IHttpContext context, object? data)
    {
        await context.SendStringAsync(
            JsonConvert.SerializeObject(data),
            "application/json; charset=utf-8",
            Encoding.UTF8);
    }

    private bool TryStart()
    {
        try
        {
            _runTask = _server.RunAsync(_cts.Token);
            
            _ = _runTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            for (int i = 0; i < 40; i++) 
            {
                if (_server.State == WebServerState.Listening) return true;
                if (_runTask.IsCompleted) return false; 
                Thread.Sleep(50);
            }
            return _server.State == WebServerState.Listening;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _server.Dispose(); } catch { }
        ServerContext.KuGou.Http.Dispose();
        ServerContext.Netease.Http.Dispose();
    }
}
