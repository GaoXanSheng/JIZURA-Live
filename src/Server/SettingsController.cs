using System.IO;
using System.Text;
using System.Windows;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using JizuraLive.App;
using JizuraLive.Logging;
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
            lyricsFps = s.LyricsFps,
            spoutEnabled = s.SpoutEnabled,
            spoutName = s.SpoutName,
            spoutTransparent = s.SpoutTransparent,
            spoutLyricsEnabled = s.SpoutLyricsEnabled,
            splitMirrorHost = s.SplitMirrorHost,
            mirrorExternalBeginFrame = s.MirrorExternalBeginFrame,
            mirrorDsync = s.MirrorDsync,
            mirrorCdpEnabled = s.MirrorCdpEnabled,
            mirrorCdpPort = s.MirrorCdpPort,
            cdpEnabled = s.CdpEnabled,
            cdpPort = s.CdpPort
        };
    }

    [Route(HttpVerbs.Get, "/api/app/settings")]
    public async Task Get()
    {
        var settings = ((App.App?)Application.Current)?.Settings;
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

        var settings = ((App.App?)Application.Current)?.Settings;
        if (settings == null)
        {
            Response.StatusCode = 409;
            return;
        }

        var changed = new List<string>();

        // 这里只保存设置：渲染管线将在 M2（纯 C# Spout 直出）回归后重建应用逻辑。
        // 分辨率钳到 D3D11 合理纹理范围，POST 100000×100000 会一路传到渲染端试图建巨型纹理
        var width = Math.Clamp(d.Width, 64, 8192);
        var height = Math.Clamp(d.Height, 64, 8192);

        // 开关差值只用于「变更了什么」的回执文案：赋值前取，赋值后再比较永远为 false
        var lyricsToggled = settings.SpoutLyricsEnabled != d.SpoutLyricsEnabled;
        var splitToggled = settings.SplitMirrorHost != d.SplitMirrorHost;

        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (settings.Width != width || settings.Height != height)
                changed.Add($"渲染窗口分辨率 {width}×{height}");

            var fps = Math.Clamp(d.Fps, 1, 240);
            if (settings.RenderFps != fps)
                changed.Add($"渲染帧率 {fps}fps");

            // 字幕通道帧率随「重启渲染引擎」与主通道一起生效；生效值再钳主通道帧率（见 Settings.LyricsFps）
            var lyricsFps = Math.Clamp(d.LyricsFps, 1, 240);
            if (settings.LyricsFps != lyricsFps)
                changed.Add($"字幕通道帧率 {lyricsFps}fps");

            var name = string.IsNullOrWhiteSpace(d.SpoutName) ? "JIZURA-live" : d.SpoutName.Trim();
            if (!string.Equals(settings.SpoutName, name, StringComparison.Ordinal))
                changed.Add($"Spout ID → {name}");
            if (settings.SpoutEnabled != d.SpoutEnabled)
                changed.Add($"Spout 输出{(d.SpoutEnabled ? " 开启" : " 关闭")}");
            if (settings.SpoutTransparent != d.SpoutTransparent)
                changed.Add($"渲染透明背景{(d.SpoutTransparent ? " 开启" : " 关闭")}");
            if (lyricsToggled)
                changed.Add($"独立 Spout 字幕通道{(d.SpoutLyricsEnabled ? " 开启" : " 关闭")}");
            if (splitToggled)
                changed.Add($"镜像独立进程渲染{(d.SplitMirrorHost ? " 开启" : " 关闭")}");
            if (settings.MirrorExternalBeginFrame != d.MirrorExternalBeginFrame)
                changed.Add($"镜像 External BeginFrame{(d.MirrorExternalBeginFrame ? " 开启" : " 关闭")}");
            if (settings.MirrorDsync != d.MirrorDsync)
                changed.Add($"desynchronized 画布{(d.MirrorDsync ? " 开启" : " 关闭")}");

            // 宿主进程的 CDP 端口在 Cef.Initialize/进程拉起时定型：改了只能重启应用（或重建宿主进程）
            var mirrorCdpEnabled = d.MirrorCdpEnabled;
            var mirrorCdp = Math.Clamp(d.MirrorCdpPort, 0, 65535);
            if (settings.MirrorCdpEnabled != mirrorCdpEnabled ||
                (mirrorCdpEnabled && settings.MirrorCdpPort != mirrorCdp))
                changed.Add(mirrorCdpEnabled
                    ? $"镜像宿主调试端口 {mirrorCdp}（重启应用后生效）"
                    : "镜像宿主调试端口 关闭（重启应用后生效）");
            if (mirrorCdpEnabled && mirrorCdp > 0 && d.CdpEnabled && mirrorCdp == Math.Clamp(d.CdpPort, 1, 65535))
                changed.Add("⚠ 镜像宿主调试端口与主进程调试端口相同，后启动的一方连不上");

            settings.Width = width;
            settings.Height = height;
            settings.RenderFps = Math.Clamp(d.Fps, 1, 240);
            settings.LyricsFps = Math.Clamp(d.LyricsFps, 1, 240);
            settings.SpoutEnabled = d.SpoutEnabled;
            settings.SpoutName = string.IsNullOrWhiteSpace(d.SpoutName) ? "JIZURA-live" : d.SpoutName.Trim();
            settings.SpoutTransparent = d.SpoutTransparent;
            settings.SpoutLyricsEnabled = d.SpoutLyricsEnabled;
            settings.SplitMirrorHost = d.SplitMirrorHost;
            settings.MirrorExternalBeginFrame = d.MirrorExternalBeginFrame;
            settings.MirrorDsync = d.MirrorDsync;
            settings.MirrorCdpEnabled = d.MirrorCdpEnabled;
            settings.MirrorCdpPort = Math.Clamp(d.MirrorCdpPort, 0, 65535);

            var port = Math.Clamp(d.CdpPort, 1, 65535);
            if (settings.CdpEnabled != d.CdpEnabled || settings.CdpPort != port)
            {
                settings.CdpEnabled = d.CdpEnabled;
                settings.CdpPort = port;
                changed.Add($"调试端口{(d.CdpEnabled ? $" {port}" : " 关闭")}（重启应用后生效）");
            }
        });

        // 文件 IO 移出 UI 线程：Invoke 内只剩内存赋值，HTTP 线程等 UI 的时间不含写盘
        settings.Save();

        await HttpContext.SendStringAsync(
            JsonConvert.SerializeObject(new
            {
                ok = true,
                changed,
                settings = CurrentState(settings)
            }),
            "application/json; charset=utf-8", Encoding.UTF8);
    }

    // 「重启渲染引擎」：渲染管线 M2 回归后按此刻 settings 的值重建；
    // 纯 C# 重构期间保留端点（契约不变），先回 ok 空操作
    [Route(HttpVerbs.Post, "/api/app/render/restart")]
    public async Task RestartRender()
    {
        Log.Info("[Settings] render/restart：渲染引擎纯 C# 重构中，M2 回归后生效");
        await HttpContext.SendStringAsync("{\"ok\":true}", "application/json; charset=utf-8", Encoding.UTF8);
    }

    public sealed class Dto
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public int Fps { get; set; }
        public int LyricsFps { get; set; } = 30;
        public bool SpoutEnabled { get; set; }
        public string SpoutName { get; set; } = "";
        public bool SpoutTransparent { get; set; }
        public bool SpoutLyricsEnabled { get; set; }
        public bool SplitMirrorHost { get; set; } = true;
        public bool MirrorExternalBeginFrame { get; set; }
        public bool MirrorDsync { get; set; }
        public bool MirrorCdpEnabled { get; set; } = true;
        public int MirrorCdpPort { get; set; } = 9223;
        public bool CdpEnabled { get; set; }
        public int CdpPort { get; set; }
    }
}