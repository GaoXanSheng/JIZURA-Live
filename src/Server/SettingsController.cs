using System.IO;
using System.Windows;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using JizuraLive.Server;

namespace JizuraLive.Server;

/// <summary>
/// 前端「设置」选项卡的读写端点：GET/POST /api/app/settings。
/// POST 应用全部变更（原 WPF SettingsPanel 的逻辑移植到这里）——
/// 分辨率即时调整镜像尺寸、帧率/Spout/透明变化按需重启镜像、CDP 重启应用生效。
/// </summary>
internal sealed class SettingsController : WebApiController
{
    public sealed class Dto
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public int Fps { get; set; }
        public bool SpoutEnabled { get; set; }
        public string SpoutName { get; set; } = "";
        public bool SpoutTransparent { get; set; }
        public bool CdpEnabled { get; set; }
        public int CdpPort { get; set; }
    }

    private static object CurrentState(Settings s) => new
    {
        width = s.Width,
        height = s.Height,
        fps = s.RenderFps,
        spoutEnabled = s.SpoutEnabled,
        spoutName = s.SpoutName,
        spoutTransparent = s.SpoutTransparent,
        cdpEnabled = s.CdpEnabled,
        cdpPort = s.CdpPort,
    };

    [Route(HttpVerbs.Get, "/api/app/settings")]
    public void Get()
    {
        var settings = ((App?)Application.Current)?.Settings;
        if (settings == null)
        {
            Response.StatusCode = 409;
            return;
        }
        HttpContext.SendStringAsync(
            Newtonsoft.Json.JsonConvert.SerializeObject(new { ok = true, settings = CurrentState(settings) }),
            "application/json; charset=utf-8", System.Text.Encoding.UTF8);
    }

    [Route(HttpVerbs.Post, "/api/app/settings")]
    public async System.Threading.Tasks.Task Apply()
    {
        using var ms = new MemoryStream();
        await Request.InputStream.CopyToAsync(ms);
        Dto? d;
        try
        {
            d = Newtonsoft.Json.JsonConvert.DeserializeObject<Dto>(
                System.Text.Encoding.UTF8.GetString(ms.ToArray()));
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

        var changed = new System.Collections.Generic.List<string>();

        // 全部 UI/管线操作回 WPF 主线程
        Application.Current?.Dispatcher.Invoke(() =>
        {
            var main = Application.Current.MainWindow as MainWindow;

            // —— 渲染窗口分辨率：即时调整镜像尺寸 ——
            if (settings.Width != d.Width || settings.Height != d.Height)
            {
                settings.Width = d.Width;
                settings.Height = d.Height;
                settings.Save();
                if (SpoutPipeline.Instance.IsRunning && SpoutPipeline.Instance._browser != null)
                {
                    SpoutPipeline.Instance._browser.Size = new System.Drawing.Size(d.Width, d.Height);
                }
                changed.Add($"渲染窗口分辨率 {d.Width}×{d.Height}");
            }

            // —— 渲染帧率：变化才重启镜像 ——
            int fps = System.Math.Clamp(d.Fps, 1, 240);
            if (settings.RenderFps != fps)
            {
                settings.RenderFps = fps;
                settings.Save();
                if (SpoutPipeline.Instance.IsRunning) main?.RestartSpout();
                changed.Add($"渲染帧率 {fps}fps");
            }

            // —— Spout 输出 / ID / 透明背景 ——
            string name = string.IsNullOrWhiteSpace(d.SpoutName) ? "JIZURA-live" : d.SpoutName.Trim();
            bool nameChanged = !string.Equals(settings.SpoutName, name, System.StringComparison.Ordinal);
            bool spoutChanged = settings.SpoutEnabled != d.SpoutEnabled || nameChanged;
            bool transChanged = settings.SpoutTransparent != d.SpoutTransparent;

            settings.SpoutEnabled = d.SpoutEnabled;
            settings.SpoutName = name;
            settings.SpoutTransparent = d.SpoutTransparent;
            if (spoutChanged || transChanged) settings.Save();

            if (d.SpoutEnabled && spoutChanged)
            {
                if (SpoutPipeline.Instance.IsRunning && nameChanged)
                {
                    main?.RestartSpout();
                    changed.Add($"Spout ID → {name}（镜像已重启）");
                }
                else if (!SpoutPipeline.Instance.IsRunning)
                {
                    main?.StartSpout();
                    changed.Add("Spout 输出已开启");
                }
            }
            else if (!d.SpoutEnabled && spoutChanged)
            {
                main?.StopSpout();
                changed.Add("Spout 输出已关闭");
            }
            else if (nameChanged)
            {
                changed.Add($"Spout ID → {name}（开启输出时生效）");
            }

            if (transChanged && d.SpoutTransparent)
            {
                changed.Add("渲染透明背景（镜像已重启）");
            }
            else if (transChanged)
            {
                changed.Add("渲染透明背景已关闭（镜像已重启）");
            }

            // —— 调试端口：重启应用生效 ——
            int port = System.Math.Clamp(d.CdpPort, 1, 65535);
            if (settings.CdpEnabled != d.CdpEnabled || settings.CdpPort != port)
            {
                settings.CdpEnabled = d.CdpEnabled;
                settings.CdpPort = port;
                settings.Save();
                changed.Add($"调试端口{(d.CdpEnabled ? $" {port}" : " 关闭")}（重启应用后生效）");
            }
        });

        HttpContext.SendStringAsync(
            Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                ok = true,
                changed,
                settings = CurrentState(settings),
            }),
            "application/json; charset=utf-8", System.Text.Encoding.UTF8);
    }

    [Route(HttpVerbs.Post, "/api/app/render/restart")]
    public void RestartRender()
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (!SpoutPipeline.Instance.IsRunning)
                return;

            (Application.Current.MainWindow as MainWindow)?.RestartSpout();
        });
        HttpContext.SendStringAsync("{\"ok\":true}", "application/json; charset=utf-8", System.Text.Encoding.UTF8);
    }
}
