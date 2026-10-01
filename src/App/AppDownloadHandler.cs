using System.IO;
using System.Windows;
using CefSharp;
using Microsoft.Win32;

namespace JizuraLive;

// --------------------------------------------------------------------
// 页面里的"保存"按钮（工程 .json / AE json / WAV 等）通过
// <a download> 触发浏览器下载。CefSharp 不设置 DownloadHandler 时
// 下载会被静默取消——按钮点了毫无反应。这里弹保存对话框落盘。
// --------------------------------------------------------------------

public sealed class AppDownloadHandler : IDownloadHandler
{
    public static readonly AppDownloadHandler Instance = new();

    public bool CanDownload(
        IWebBrowser chromiumWebBrowser,
        IBrowser browser,
        string url,
        string requestMethod)
    {
        return true;
    }

    public bool OnBeforeDownload(
        IWebBrowser chromiumWebBrowser,
        IBrowser browser,
        DownloadItem downloadItem,
        IBeforeDownloadCallback callback)
    {
        if (callback == null)
            return false;

        string suggested =
            string.IsNullOrWhiteSpace(downloadItem.SuggestedFileName)
                ? "download"
                : downloadItem.SuggestedFileName;

        Log.Info($"浏览器下载请求：{suggested}");

        string downloads =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");

        string ext = Path.GetExtension(suggested);

        // 回调来自 CEF 线程：Win32 对话框有线程亲和性，
        // 必须在 UI 线程上"创建并显示"，否则抛
        // "无法显示在其他线程上创建的对话框"。
        // Dispatcher 不可用（无 UI 线程）时直接取消下载
        string? path = null;

        var app = Application.Current;
        if (app?.Dispatcher != null)
        {
            app.Dispatcher.Invoke(() =>
            {
                var dlg = new SaveFileDialog
                {
                    FileName = suggested,
                    InitialDirectory = Directory.Exists(downloads)
                        ? downloads
                        : AppContext.BaseDirectory,
                    Filter = string.IsNullOrEmpty(ext)
                        ? "所有文件|*.*"
                        : $"文件|*{ext}|所有文件|*.*",
                    AddExtension = true,
                };

                if (dlg.ShowDialog() == true)
                    path = dlg.FileName;
            });
        }

        if (string.IsNullOrEmpty(path))
        {
            // 返回 false = 取消该下载
            callback.Dispose();
            return false;
        }

        // 保存对话框已由我们自己弹出，showDialog 传 false
        callback.Continue(path, false);

        callback.Dispose();

        return true;
    }

    public void OnDownloadUpdated(
        IWebBrowser chromiumWebBrowser,
        IBrowser browser,
        DownloadItem downloadItem,
        IDownloadItemCallback callback)
    {
        // 已选路径，交给 CEF 继续写文件；进度不需要 UI
        callback.Dispose();
    }
}