using JizuraLive.Logging;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace JizuraLive.Render.Gpu;

/// <summary>
/// 一块绑定到某个 HWND 的 DXGI 交换链 + 其后缓冲渲染目标视图。
///
/// 为什么用 flip 模型（FLIP_DISCARD）：flip 模型的后缓冲直接交给 DWM 扫描输出，省掉一份拷贝，
/// 也是 DirectFlip/Independent Flip（全屏免合成）的前提；旧的 BitBlt 模型在 WDDM 上多一次拷贝且
/// 无法进入独立翻转。BufferCount=2 + SetMaximumFrameLatency(1) 给出「1 帧延迟」的低延迟呈现。
///
/// 垂直同步：Present(1, 0) 锁显示器刷新（P0 的默认）；VRR/撕裂允许时（SwapChainFlags.AllowTearing
/// 且呈现用 Present(0, ALLOW_TEARING)）可跑无同步撕裂模式——由使用方按场景选。
///
/// 帧延迟等待对象：交换链带 FrameLatencyWaitableObject 时，渲染线程每帧先 Wait 该句柄再画，
/// 这就是「游戏引擎式」的帧节奏来源（见 FrameClock），取代 Stopwatch 定时器。
/// </summary>
public sealed class SwapChainTarget : IDisposable
{
    public IDXGISwapChain1 SwapChain { get; }
    public ID3D11RenderTargetView RenderTargetView { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>垂直同步呈现（Present(1,0)）。</summary>
    public bool VSync { get; }

    /// <summary>允许撕裂（仅 vsync 关时生效；需交换链带 AllowTearing 标志且驱动支持）。</summary>
    public bool AllowTearing { get; }

    /// <summary>帧延迟等待对象句柄（可能为 IntPtr.Zero：交换链不支持时退回 Stopwatch 节奏）。</summary>
    public IntPtr FrameLatencyWaitable { get; }

    /// <summary>后台缓冲的 DXGI 格式（D2D 目标位图必须与之对齐）。</summary>
    public Format BackBufferFormat { get; }

    private readonly ID3D11Device _device;

    private SwapChainTarget(ID3D11Device device, IDXGISwapChain1 swapChain, int w, int h,
        bool vsync, bool allowTearing, IntPtr waitable, Format format)
    {
        _device = device;
        SwapChain = swapChain;
        Width = w;
        Height = h;
        VSync = vsync;
        AllowTearing = allowTearing;
        FrameLatencyWaitable = waitable;
        BackBufferFormat = format;
        RenderTargetView = CreateRtv();
    }

    /// <summary>
    /// 在 hwnd 上建交换链。默认 BGRA8（D2D 目标位图常用格式，也是 WPF/D2D 互通最省心的格式）。
    /// </summary>
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
            waitable, desc.Format);
        Log.Info($"[SwapChain] {width}x{height} flip-discard，vsync={(vsync ? "开" : "关")}"
                 + $"，撕裂允许={(tearing ? "是" : "否")}，帧延迟等待对象={(waitable != IntPtr.Zero ? "有" : "无")}");
        return target;
    }

    /// <summary>取后台缓冲纹理（第 index 张；D2D 目标位图、D3D 渲染目标都从它来）。</summary>
    public ID3D11Texture2D GetBackBuffer(uint index = 0) => SwapChain.GetBuffer<ID3D11Texture2D>(index);

    /// <summary>把交换链设为当前渲染目标并设好视口。</summary>
    public void Bind(ID3D11DeviceContext ctx)
    {
        ctx.OMSetRenderTargets(RenderTargetView, null);
        ctx.RSSetViewport(0, 0, Width, Height);
    }

    /// <summary>按新尺寸重建后缓冲（先解绑 RTV，ResizeBuffers 后再建）。尺寸没变直接返回。</summary>
    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == Width && height == Height) return;

        RenderTargetView.Dispose();
        // ResizeBuffers 要求所有后缓冲引用已释放（GetBuffer 的临时引用由调用方 using 管好）
        SwapChain.ResizeBuffers(0, (uint)width, (uint)height, Format.Unknown, SwapChainFlags.None);
        Width = width;
        Height = height;
        RenderTargetView = CreateRtv();
    }

    /// <summary>呈现一帧：vsync 用 Present(1,0)；无同步且允许撕裂用 Present(0,ALLOW_TEARING)。</summary>
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

    /// <summary>呈现失败里属「正常状态」的：被遮挡（最小化/锁屏）与上一帧还在画。</summary>
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
