using System.IO;
using System.Text;
using EmbedIO;
using JizuraLive.Logging;
using Newtonsoft.Json;

namespace JizuraLive.Server.Smtc;

// /api/smtc/events：SSE 推送 SMTC 状态快照，跟随模式用它替代 300ms 轮询。
// 变化即时推（服务端 Changed 信号，信号量天然合并风暴），没变化每秒发一条心跳——
// 前端拿每条快照重锚定本地推算的进度（PositionLiveMs 已按服务器时钟算到「此刻」）。
// 客户端断开经取消令牌感知，写失败即退出清理。
internal sealed class SmtcSseModule : WebModuleBase
{
    public SmtcSseModule() : base("/api/smtc/events")
    {
    }

    public override bool IsFinalHandler => true;

    protected override async Task<bool> OnRequestAsync(IHttpContext context)
    {
        if (!string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            return false;

        var res = context.Response;
        res.ContentType = "text/event-stream; charset=utf-8";
        res.Headers["Cache-Control"] = "no-cache";
        // 关掉可能的代理缓冲（本机服务直连，保险起见）
        res.Headers["X-Accel-Buffering"] = "no";

        var service = ServerContext.Smtc;
        var signal = new SemaphoreSlim(0, 1);
        void OnChanged()
        {
            try
            {
                signal.Release();
            }
            catch
            {
                // 计数已满：合并即可
            }
        }

        service.Changed += OnChanged;
        try
        {
            var stream = res.OutputStream;
            // 连接建立立即给一条全量，前端无需等首个心跳
            await WriteSnapshotAsync(stream, service, context.CancellationToken);
            while (!context.CancellationToken.IsCancellationRequested)
            {
                // 等变化信号（最多 1 秒）——超时也照发：这就是心跳，重锚定用
                await signal.WaitAsync(TimeSpan.FromSeconds(1), context.CancellationToken);
                await WriteSnapshotAsync(stream, service, context.CancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 客户端断开，正常路径
        }
        catch (Exception ex)
        {
            Log.Debug("[Smtc] SSE 连接结束：" + ex.Message);
        }
        finally
        {
            service.Changed -= OnChanged;
            signal.Dispose();
        }

        return true;
    }

    private static async Task WriteSnapshotAsync(Stream stream, SmtcService service, CancellationToken ct)
    {
        var payload = "data: " + JsonConvert.SerializeObject(service.GetState()) + "\n\n";
        var bytes = Encoding.UTF8.GetBytes(payload);
        await stream.WriteAsync(bytes, 0, bytes.Length, ct);
        await stream.FlushAsync(ct);
    }
}
