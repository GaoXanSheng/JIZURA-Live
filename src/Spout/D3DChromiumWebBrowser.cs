using CefSharp;
using CefSharp.OffScreen;

namespace JizuraLive.Spout;

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
            // RequestContext 传 null = 与主窗口浏览器共享同一存储分区，
            // 否则 localStorage 互不相通，工程跟随（storage 事件）永远不会触发
            null,
            false)
    {
        _browserSettings =
            browserSettings;
    }

    public void CreateD3DBrowser(bool externalBeginFrame = false)
    {
        if (_created) return;

        var windowInfo = new WindowInfo();

        windowInfo.SetAsWindowless(IntPtr.Zero);

        windowInfo.WindowlessRenderingEnabled = true;

        // ExternalBeginFrame=true 时帧节奏改由宿主泵（SpoutPipeline）逐帧
        // SendExternalBeginFrame 驱动，绕开合成器自调度（BeginFrame 断供、
        // 稳态帧间隔被量化到 ~25ms 的来源）；false 保留合成器路径作回退
        windowInfo.ExternalBeginFrameEnabled = externalBeginFrame;

        windowInfo.SharedTextureEnabled = true;

        _created = true;

        CreateBrowser(windowInfo, _browserSettings);
    }
}