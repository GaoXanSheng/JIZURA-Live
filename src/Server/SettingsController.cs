using System.IO;
using System.Text;
using System.Windows;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using Newtonsoft.Json;

namespace JizuraLive.Server;

// 前端「设置」选项卡的读写端点：GET/POST /api/app/settings。
// POST 应用全部变更：分辨率即时调整镜像尺寸、帧率/Spout/透明变化按需重启镜像、CDP 重启应用生效
internal sealed class SettingsController : WebApiController
{
    private static object CurrentState(Settings s)
    {
        return new
        {
            width = s.Width,
            height = s.Height,
            fps = s.RenderFps,
            spoutEnabled = s.SpoutEnabled,
            spoutName = s.SpoutName,
            spoutTransparent = s.SpoutTransparent,
            spoutLyricsEnabled = s.SpoutLyricsEnabled,
            cdpEnabled = s.CdpEnabled,
            cdpPort = s.CdpPort
        };
    }

    [Route(HttpVerbs.Get, "/api/app/settings")]
    public async Task Get()
    {
        var settings = ((App?)Application.Current)?.Settings;
        if (settings == null)
        {
            Response.StatusCode = 409;
            return;
        }

        await HttpContext.SendStringAsync(
            JsonConvert.SerializeObject(new { ok = true, settings = CurrentState(settings) }),
            "application/json; charset=utf-8", Encoding.UTF8);
    }

    [Route(HttpVerbs.Post, "/api/app/settings")]
    public async Task Apply()
    {
        using var ms = new MemoryStream();
        await Request.InputStream.CopyToAsync(ms);
        Dto? d;
        try
        {
            d = JsonConvert.DeserializeObject<Dto>(
                Encoding.UTF8.GetString(ms.ToArray()));
        }
        catch
        {
            Response.StatusCode = 400;
            return;
        }

        if (d == null)
        {
            Response.StatusCode = 400;
            return;
        }

        var settings = ((App?)Application.Current)?.Settings;
        if (settings == null)
        {
            Response.StatusCode = 409;
            return;
        }

        var changed = new List<string>();

        // 这里只保存不立即应用管线：渲染窗口分辨率/帧率/Spout ID/透明背景
        // 统一在「重启渲染引擎」时按 settings 当前值重建镜像，避免每次改动实时打断输出。
        // 分辨率钳到 D3D11 合理纹理范围，POST 100000×100000 会一路传到 CEF 试图建巨型纹理
        var width = Math.Clamp(d.Width, 64, 8192);
        var height = Math.Clamp(d.Height, 64, 8192);

        // 开关差值必须在赋值前取：赋值后再比较永远为 false
        var lyricsToggled = settings.SpoutLyricsEnabled != d.SpoutLyricsEnabled;

        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (settings.Width != width || settings.Height != height)
                changed.Add($"渲染窗口分辨率 {width}×{height}");

            var fps = Math.Clamp(d.Fps, 1, 240);
            if (settings.RenderFps != fps)
                changed.Add($"渲染帧率 {fps}fps");

            var name = string.IsNullOrWhiteSpace(d.SpoutName) ? "JIZURA-live" : d.SpoutName.Trim();
            if (!string.Equals(settings.SpoutName, name, StringComparison.Ordinal))
                changed.Add($"Spout ID → {name}");
            if (settings.SpoutEnabled != d.SpoutEnabled)
                changed.Add($"Spout 输出{(d.SpoutEnabled ? " 开启" : " 关闭")}");
            if (settings.SpoutTransparent != d.SpoutTransparent)
                changed.Add($"渲染透明背景{(d.SpoutTransparent ? " 开启" : " 关闭")}");
            if (lyricsToggled)
                changed.Add($"独立 Spout 字幕通道{(d.SpoutLyricsEnabled ? " 开启" : " 关闭")}");

            settings.Width = width;
            settings.Height = height;
            settings.RenderFps = Math.Clamp(d.Fps, 1, 240);
            settings.SpoutEnabled = d.SpoutEnabled;
            settings.SpoutName = string.IsNullOrWhiteSpace(d.SpoutName) ? "JIZURA-live" : d.SpoutName.Trim();
            settings.SpoutTransparent = d.SpoutTransparent;
            settings.SpoutLyricsEnabled = d.SpoutLyricsEnabled;

            var port = Math.Clamp(d.CdpPort, 1, 65535);
            if (settings.CdpEnabled != d.CdpEnabled || settings.CdpPort != port)
            {
                settings.CdpEnabled = d.CdpEnabled;
                settings.CdpPort = port;
                changed.Add($"调试端口{(d.CdpEnabled ? $" {port}" : " 关闭")}（重启应用后生效）");
            }
        });

        // 文件 IO 移出 UI 线程：Invoke 内只剩内存赋值与管线开关，HTTP 线程等 UI 的时间不含写盘。
        // 保持「先保存后重启」的顺序
        settings.Save();

        // 字幕通道开关变了：主通道要重注入 __jl_no_sub（字幕分离标志），歌词通道随 RestartSpout 启停，
        // 整体重启一次到位。CEF 浏览器创建必须在 Cef.Initialize 的线程（主线程）上
        if (lyricsToggled)
            Application.Current?.Dispatcher.Invoke(
                () => (Application.Current.MainWindow as MainWindow)?.RestartSpout());

        await HttpContext.SendStringAsync(
            JsonConvert.SerializeObject(new
            {
                ok = true,
                changed,
                settings = CurrentState(settings)
            }),
            "application/json; charset=utf-8", Encoding.UTF8);
    }

    // 「重启渲染引擎」：按此刻 settings 的值重建镜像，
    // 分辨率/帧率/ID/透明背景全部经 SpoutPipeline.Start 的参数一次性生效
    [Route(HttpVerbs.Post, "/api/app/render/restart")]
    public async Task RestartRender()
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            var settings = ((App?)Application.Current)?.Settings;
            if (settings == null)
                return;

            var main = Application.Current.MainWindow as MainWindow;
            if (settings.SpoutEnabled)
            {
                if (SpoutPipeline.Instance.IsRunning)
                    main?.RestartSpout();
                else
                    main?.StartSpout();
            }
            else
            {
                main?.StopSpout();
            }
        });
        await HttpContext.SendStringAsync("{\"ok\":true}", "application/json; charset=utf-8", Encoding.UTF8);
    }

    public sealed class Dto
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public int Fps { get; set; }
        public bool SpoutEnabled { get; set; }
        public string SpoutName { get; set; } = "";
        public bool SpoutTransparent { get; set; }
        public bool SpoutLyricsEnabled { get; set; }
        public bool CdpEnabled { get; set; }
        public int CdpPort { get; set; }
    }
}