using System;
using System.Threading.Tasks;
using CefSharp;
using CefSharp.OffScreen;

namespace JizuraLive;

public sealed class D3DChromiumWebBrowser : ChromiumWebBrowser
{
    private readonly BrowserSettings _browserSettings;

    private bool _created;

    public D3DChromiumWebBrowser(
        string url,
        BrowserSettings browserSettings)
        : base(
            url,
            browserSettings,
            // null = 默认 RequestContext：必须与主窗口浏览器共享同一存储分区，
            // 否则 localStorage 互不相通，工程跟随（storage 事件）永远不会触发
            null,
            false)
    {
        _browserSettings =
            browserSettings;
    }

    public void CreateD3DBrowser()
    {
        if (_created)
            return;

        var windowInfo =
            new WindowInfo();

        windowInfo.SetAsWindowless(
            IntPtr.Zero);

        windowInfo.WindowlessRenderingEnabled =
            true;

        windowInfo.ExternalBeginFrameEnabled =
            false;

        windowInfo.SharedTextureEnabled =
            true;

        _created = true;

        CreateBrowser(
            windowInfo,
            _browserSettings);
    }

    public async Task WaitReadyAsync()
    {
        await WaitForInitialLoadAsync();
    }
}