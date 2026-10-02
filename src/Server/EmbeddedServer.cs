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

    private EmbeddedServer(int port)
    {
        Port = port;
        _server = new WebServer(o => o
                .WithUrlPrefix($"http://127.0.0.1:{port}/")
                .WithMode(HttpListenerMode.EmbedIO))
            // 防跨站守卫在最前（api/ 前缀命中即终结，其余放行）；
            // 静态站其次：WebApiModule 未命中路由会直接 404 终结而不是传递，
            // 放前面会把 /render 等 SPA 路径吞掉。API 侧由 StaticSiteModule 的
            // 回退排除（api/ 前缀不回退）保证到达控制器。
            .WithModule(new ApiGuardModule())
            .WithModule(new StaticSiteModule())
            .WithModule(BuildApiModule());
    }

    public int Port { get; }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
            _server.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug("Server Dispose failed: " + ex);
        }

        _cts.Dispose();
    }

    public static EmbeddedServer? Start()
    {
        for (var port = PortBase; port < PortBase + 10; port++)
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
        // 控制器异常不再透传成 EmbedIO 的 500 HTML（前端 response.json() 会挂）：
        // EmbedIO 已把响应重置为空 500，这里补 JSON 体，前端能拿到真实错误
        api.OnUnhandledException = (ctx, ex) =>
        {
            Log.Error("[Api] 控制器未处理异常：" + ctx.Request.Url, ex);
            return ctx.SendStringAsync(
                "{\"ok\":false,\"error\":\"服务处理请求失败，详见日志\"}",
                "application/json; charset=utf-8", Encoding.UTF8);
        };
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

            // 服务器异常退出不再静默丢弃；主动 Dispose 触发的取消不算故障
            _ = _runTask.ContinueWith(t =>
            {
                var ex = t.Exception?.GetBaseException();
                if (ex is not OperationCanceledException)
                    Log.Error("WebServer 异常退出", ex ?? new Exception("unknown error"));
            }, TaskContinuationOptions.OnlyOnFaulted);

            for (var i = 0; i < 40; i++)
            {
                if (_server.State == WebServerState.Listening)
                    return true;

                if (_runTask.IsCompleted) return false;
                Thread.Sleep(50);
            }

            return _server.State == WebServerState.Listening;
        }
        catch (Exception ex)
        {
            Log.Debug("Server TryStart failed: " + ex);
            return false;
        }
    }
}