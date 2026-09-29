using System.Windows;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace JizuraLive.Server;

/// <summary>
/// 应用级动作：前端经 /api/app/* 唤起 C# 侧 UI（如输出设置窗口）。
/// </summary>
internal sealed class AppController : WebApiController
{
    [Route(HttpVerbs.Post, "/api/app/settings")]
    public void ToggleSettings()
    {
        var app = (App?)Application.Current;
        var settings = app?.Settings;
        if (settings == null)
        {
            Response.StatusCode = 409;
            return;
        }

        // UI 必须回 WPF 主线程
        Application.Current?.Dispatcher.Invoke(() =>
        {
            (Application.Current?.MainWindow as MainWindow)?.ToggleSettings();
        });
    }
}
