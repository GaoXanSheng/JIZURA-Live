using System.Text;
using EmbedIO;
using EmbedIO.WebApi;
using Newtonsoft.Json;

namespace JizuraLive.Server;

public sealed class EmbeddedServer : IDisposable
{
    private const int PortBase = 8642;
    private readonly CancellationTokenSource _cts = new();

    private readonly WebServer _server;
    private Task? _runTask;
    private bool _started;

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
            .WithModule(BuildApiModule());
    }

    public int Port { get; }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        try
        {
            _server.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug("Server Dispose failed: " + ex);
        }

        // 共享 HttpClient 是进程级单例：端口探测失败的实例绝不能碰它，
        // 否则重试下一个端口后首个 API 调用就会 ObjectDisposedException
        // (现在通过 IHttpClientFactory 管理，不再手动释放)
        if (_started)
        {
        }
    }

    public static EmbeddedServer? Start()
    {
        for (int port = PortBase; port < PortBase + 10; port++)
        {
            var server = new EmbeddedServer(port);
            if (server.TryStart()) return server;
            try
            {
                server.Dispose();
            }
            catch (Exception ex)
            {
                Log.Debug("Failed to dispose server on port probe: " + ex);
            }
        }

        return null;
    }

    private static WebApiModule BuildApiModule()
    {
        var api = new WebApiModule("/", SerializeWithNewtonsoft);
        api.WithController<KgController>();
        api.WithController<NeController>();
        api.WithController<AnalyzeController>();
        api.WithController<SettingsController>();
        api.WithController<PerfController>();
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
                if (_server.State == WebServerState.Listening)
                {
                    _started = true;
                    return true;
                }

                if (_runTask.IsCompleted) return false;
                Thread.Sleep(50);
            }

            return _started = _server.State == WebServerState.Listening;
        }
        catch (Exception ex)
        {
            Log.Debug("Server TryStart failed: " + ex);
            return false;
        }
    }
}