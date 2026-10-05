using System.IO;
using System.Windows;
using CefSharp;
using JizuraLive.Logging;
using Microsoft.Win32;

namespace JizuraLive.App;

// 页面「保存」按钮走 <a download> 触发浏览器下载，CefSharp 不设 DownloadHandler
// 时下载被静默取消，按钮点了毫无反应。这里弹保存对话框落盘

public sealed class AppDownloadHandler : IDownloadHandler
{
    public static readonly AppDownloadHandler Instance = new();

    // 免确认落盘目录：页面经 set_autosave_dir 消息设置（遗留通道，已无发送方——
    // MCP 导出改走 /api/export/save 直接落盘）。非 null 时直接写入不弹另存为。
    // CEF 线程读、UI 线程写，volatile 引用赋值本身原子
    public static volatile string? AutoSaveDir;

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

        var suggested =
            string.IsNullOrWhiteSpace(downloadItem.SuggestedFileName)
                ? "download"
                : downloadItem.SuggestedFileName;

        Log.Info($"浏览器下载请求：{suggested}");

        var ext = Path.GetExtension(suggested);

        // 指定了免确认目录：直接落盘，目录不可用再回退另存为
        var autoDir = AutoSaveDir;
        if (!string.IsNullOrWhiteSpace(autoDir))
            try
            {
                Directory.CreateDirectory(autoDir);
                var target = Path.Combine(autoDir, suggested);
                callback.Continue(target, false);
                callback.Dispose();
                Log.Info($"免确认下载：{target}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"免确认目录不可用（{autoDir}），回退另存为：{ex.Message}");
            }

        var downloads =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");

        // 回调来自 CEF 线程，Win32 对话框有线程亲和性，
        // 必须切到 UI 线程"创建并显示"，否则抛"无法显示在其他线程上创建的对话框"。
        // Dispatcher 不可用（无 UI 线程）时下载直接取消
        string? path = null;

        var app = Application.Current;
        if (app?.Dispatcher != null)
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
                    AddExtension = true
                };

                if (dlg.ShowDialog() == true)
                    path = dlg.FileName;
            });

        if (string.IsNullOrEmpty(path))
        {
            // false = 取消该下载
            callback.Dispose();
            return false;
        }

        // 对话框已由我们自己弹出，showDialog 传 false
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
        // 路径已定，交给 CEF 继续写文件；进度不上 UI
        callback.Dispose();
    }
}