using JizuraLive.Logging;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace JizuraLive.Render.Gpu;

public sealed class SwapChainTarget : IDisposable
{
    public IDXGISwapChain1 SwapChain { get; }
    public ID3D11RenderTargetView RenderTargetView { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    public bool VSync { get; set; }

    public bool AllowTearing { get; }

    public IntPtr FrameLatencyWaitable { get; }

    public Format BackBufferFormat { get; }

    private readonly ID3D11Device _device;
    private readonly SwapChainFlags _createFlags;

    private SwapChainTarget(ID3D11Device device, IDXGISwapChain1 swapChain, int w, int h,
        bool vsync, bool allowTearing, IntPtr waitable, Format format, SwapChainFlags createFlags)
    {
        _device = device;
        SwapChain = swapChain;
        Width = w;
        Height = h;
        VSync = vsync;
        AllowTearing = allowTearing;
        FrameLatencyWaitable = waitable;
        BackBufferFormat = format;
        _createFlags = createFlags;
        RenderTargetView = CreateRtv();
    }

    public static SwapChainTarget Create(D3DDevice gpu, IntPtr hwnd, int width, int height,
        bool vsync = true, bool allowTearing = false)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        var tearing = allowTearing && SupportsTearing(gpu.Factory);
        var flags = SwapChainFlags.FrameLatencyWaitableObject;
        if (tearing) flags |= SwapChainFlags.AllowTearing;

        var desc = new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1u, 0u),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
            Flags = flags,
        };

        var swapChain = gpu.Factory.CreateSwapChainForHwnd(gpu.Device, hwnd, desc, null, null);
        var swapChain2 = swapChain.QueryInterface<IDXGISwapChain2>();
        swapChain2.MaximumFrameLatency = 1;
        var waitable = swapChain2.FrameLatencyWaitableObject;
        swapChain2.Dispose();

        var target = new SwapChainTarget(gpu.Device, swapChain, width, height, vsync, tearing,
            waitable, desc.Format, flags);
        Log.Info($"[SwapChain] {width}x{height} flip-discard，vsync={(vsync ? "开" : "关")}"
                 + $"，撕裂允许={(tearing ? "是" : "否")}，帧延迟等待对象={(waitable != IntPtr.Zero ? "有" : "无")}");
        return target;
    }

    public ID3D11Texture2D GetBackBuffer(uint index = 0) => SwapChain.GetBuffer<ID3D11Texture2D>(index);

    public void Bind(ID3D11DeviceContext ctx)
    {
        ctx.OMSetRenderTargets(RenderTargetView, null);
        ctx.RSSetViewport(0, 0, Width, Height);
    }

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == Width && height == Height) return;

        RenderTargetView.Dispose();
        var hr = SwapChain.ResizeBuffers(0, (uint)width, (uint)height, Format.Unknown, _createFlags);
        if (hr.Failure)
        {
            Log.Warn($"[SwapChain] ResizeBuffers 失败 hr=0x{(int)hr.Code:x}（{hr.Description}），继续保持 {Width}x{Height}");
            RenderTargetView = CreateRtv();
            return;
        }

        Width = width;
        Height = height;
        RenderTargetView = CreateRtv();
    }

    public void Present()
    {
        var interval = VSync ? 1u : 0u;
        var flags = (!VSync && AllowTearing) ? PresentFlags.AllowTearing : PresentFlags.None;
        var hr = SwapChain.Present(interval, flags);
        if (hr.Failure && !IsTransient(hr))
            Log.Warn($"[SwapChain] Present 失败 hr=0x{(int)hr.Code:x}（{hr.Description}）");
    }

    private ID3D11RenderTargetView CreateRtv()
    {
        using var back = GetBackBuffer();
        return _device.CreateRenderTargetView(back);
    }

    private const int StatusOccluded = 0x087A0001;
    private const int StatusWasStillDrawing = 0x087A0002;

    private static bool IsTransient(Result hr) =>
        hr.Code == StatusOccluded || hr.Code == StatusWasStillDrawing;

    private static bool SupportsTearing(IDXGIFactory2 factory)
    {
        try
        {
            using var f5 = factory.QueryInterfaceOrNull<IDXGIFactory5>();
            if (f5 is null) return false;
            return f5.PresentAllowTearing;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        RenderTargetView.Dispose();
        SwapChain.Dispose();
    }
}
