using System.IO;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using JizuraLive.Logging;

namespace JizuraLive.Server.Controllers;

// MCP 导出（start_export 带 dir）的落盘端点：页面把成片 blob 直接 POST 过来写进指定目录。
// 不走浏览器下载通道——下载与「免确认目录复位」消息存在竞态（复位会赶在下载请求前到达宿主），
// 下载落回另存为对话框，导出被模态框卡死等用户手点，MCP 求值也随之全部超时
public sealed class ExportSaveController : WebApiController
{
    [Route(HttpVerbs.Post, "/api/export/save")]
    public async Task<object> Save([QueryField("dir")] string? dir, [QueryField("name")] string? name)
    {
        if (string.IsNullOrWhiteSpace(dir))
            throw new ArgumentException("dir 不能为空");
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("name 不能为空");
        // 只收纯文件名；目录必须是绝对路径。Path.GetFullPath 归一后再比对，杜绝穿越
        if (name.Contains('/') || name.Contains('\\') || name.Contains(".."))
            throw new ArgumentException("name 只能是文件名");
        if (!Path.IsPathRooted(dir))
            throw new ArgumentException("dir 必须是绝对路径");
        var fullDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        var target = Path.GetFullPath(Path.Combine(fullDir, name));
        if (!target.StartsWith(fullDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("目标路径越界");

        Directory.CreateDirectory(fullDir);
        await using (var fs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await HttpContext.Request.InputStream.CopyToAsync(fs);
        }
        Log.Info($"[Api] 导出落盘：{target}");
        return new { ok = true, path = target };
    }
}
