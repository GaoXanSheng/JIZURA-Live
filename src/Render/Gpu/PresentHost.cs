using System.Runtime.InteropServices;
using System.Windows.Interop;
using JizuraLive.Logging;

namespace JizuraLive.Render.Gpu;

/// <summary>
/// WPF 里承载 GPU 直呈的子窗口（HwndHost）：自建子 HWND + 自己的 D3D11 交换链 + 独立渲染线程。
///
/// 为什么不是 D3DImage/D3D11ImageSource：D3DImage 走 D3D9Ex 互操作，受 DWM 合成与额外拷贝约束，
/// 帧时机由 WPF 合成器决定，做不出「锁显示器刷新、1 帧延迟」的游戏引擎式呈现。HwndHost 把一块
/// 子窗口交给我们的渲染线程，交换链 Present 直接对屏——这正是 OBS/游戏/Resolume 这类工具的做法。
/// 代价是 airspace：子窗口不被 WPF 的变换/裁剪/圆角/透明影响，所以它只做纯预览面，叠加控件另开窗口。
///
/// 生命周期：BuildWindowCore 建子窗口并起渲染线程；渲染线程里建设备→交换链→帧节奏，然后
/// 触发 <see cref="Ready"/>（使用方在此建 D2D/DWrite 资源，必须在该线程上），随后每帧触发
/// <see cref="Frame"/>，由本类负责 Present。窗口尺寸变化由渲染线程轮询客户区自行 ResizeBuffers。
/// </summary>
public sealed class PresentHost : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;

    private IntPtr _hwnd = IntPtr.Zero;
    private Thread? _thread;
    private volatile bool _running;

    /// <summary>初始/回退目标尺寸（子窗口尚未布局出尺寸时用）。</summary>
    public int InitialWidth { get; set; } = 1920;
    public int InitialHeight { get; set; } = 1080;

    public bool VSync { get; set; } = true;
    public bool AllowTearing { get; set; }

    /// <summary>设备/交换链就绪（在渲染线程上触发）。使用方在此建 D2D/DWrite 等线程亲和的资源。</summary>
    public event Action<PresentHost>? Ready;

    /// <summary>
    /// 每帧**等待垂直同步之前**的回调（渲染线程）。把「与呈现无依赖的重活」（引擎栅格化等）
    /// 放这里，它就能和 vsync 等待重叠，而不是全压在信号之后把 Present 推晚——这是拿到满帧率的
    /// 关键：先干活、再等 vsync、最后只做合成与呈现。
    /// </summary>
    public event Action<PresentHost>? Prepare;

    /// <summary>每帧回调（渲染线程）：参数为距上一帧的秒数。回调内画完即可，Present 由本类负责。</summary>
    public event Action<PresentHost, double>? Frame;

    /// <summary>后缓冲尺寸变化（渲染线程）：使用方据此重建与尺寸绑定的位图/渲染目标。</summary>
    public event Action<PresentHost>? Resized;

    /// <summary>初始化失败的原因（非空即 GPU 路不可用）。</summary>
    public string? Error { get; private set; }

    public D3DDevice? Gpu { get; private set; }
    public SwapChainTarget? Target { get; private set; }
    public FrameClock? Clock { get; private set; }

    public double Fps => Clock?.Fps ?? 0;
    public long Frames => Clock?.Frame ?? 0;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _hwnd = CreateWindowExW(0, "STATIC", null, WsChild | WsVisible,
            0, 0, InitialWidth, InitialHeight, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            Error = $"子窗口创建失败 err={Marshal.GetLastWin32Error()}";
            Log.Error("[PresentHost] " + Error);
            return new HandleRef(this, IntPtr.Zero);
        }

        _running = true;
        _thread = new Thread(RenderLoop)
        {
            IsBackground = true,
            Name = "RenderCore.Present",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();

        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        _running = false;
        try
        {
            _thread?.Join(2000);
        }
        catch (Exception ex)
        {
            Log.Debug("[PresentHost] 渲染线程退出等待超时：" + ex.Message);
        }

        if (hwnd.Handle != IntPtr.Zero) DestroyWindow(hwnd.Handle);
        _hwnd = IntPtr.Zero;
    }

    private void RenderLoop()
    {
        try
        {
            var gpu = D3DDevice.TryCreate(out var err);
            if (gpu is null)
            {
                Error = err;
                Log.Error("[PresentHost] GPU 不可用：" + err);
                return;
            }

            Gpu = gpu;
            var (w, h) = ClientSize();
            var target = SwapChainTarget.Create(gpu, _hwnd, w, h, VSync, AllowTearing);
            Target = target;
            var clock = new FrameClock(target.FrameLatencyWaitable);
            Clock = clock;

            Ready?.Invoke(this);

            while (_running)
            {
                // 客户区变化（窗口缩放/拖动）→ 重建后缓冲，并让使用方重建尺寸相关资源
                var (cw, ch) = ClientSize();
                if (cw != target.Width || ch != target.Height)
                {
                    target.Resize(cw, ch);
                    Resized?.Invoke(this);
                }

                // 先做与呈现无依赖的准备（引擎栅格化等）——与随后的 vsync 等待重叠，
                // 避免把重活全压在 vsync 信号之后导致 Present 错过当帧
                Prepare?.Invoke(this);

                clock.WaitNext();
                Frame?.Invoke(this, clock.Dt);
                target.Present();

                if (gpu.IsDeviceRemoved)
                {
                    Log.Error($"[PresentHost] 设备已移除（{gpu.RemovedReasonText()}），停止出帧");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log.Error("[PresentHost] 渲染线程异常退出：" + ex.Message, ex);
        }
        finally
        {
            Target?.Dispose();
            Gpu?.Dispose();
            Target = null;
            Gpu = null;
        }
    }

    private (int W, int H) ClientSize()
    {
        if (_hwnd == IntPtr.Zero) return (InitialWidth, InitialHeight);
        if (GetClientRect(_hwnd, out var r) && r.Right > 0 && r.Bottom > 0)
            return (r.Right - r.Left, r.Bottom - r.Top);
        return (InitialWidth, InitialHeight);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string? windowName,
        int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
}
