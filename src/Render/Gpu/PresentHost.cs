using System.Runtime.InteropServices;
using System.Windows.Interop;
using JizuraLive.Logging;

namespace JizuraLive.Render.Gpu;

public sealed class PresentHost : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;

    private IntPtr _hwnd = IntPtr.Zero;
    private Thread? _thread;
    private volatile bool _running;

    public int InitialWidth { get; set; } = 1920;
    public int InitialHeight { get; set; } = 1080;

    public bool VSync { get; set; } = true;
    public bool AllowTearing { get; set; }

    public event Action<PresentHost>? Ready;

    public event Action<PresentHost>? Prepare;

    public event Action<PresentHost, double>? Frame;

    public event Action<PresentHost>? Resized;

    public event Action<PresentHost>? Shutdown;

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

    public bool StopRender(int joinMs = 3000)
    {
        _running = false;
        Clock?.RequestStop();
        var thread = _thread;
        if (thread is null) return true;

        try
        {
            if (thread.Join(joinMs)) return true;
            Log.Error($"[PresentHost] 渲染线程未在 {joinMs} ms 内退出，仍在原生调用里");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("[PresentHost] 等待渲染线程退出失败：" + ex.Message, ex);
            return false;
        }
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (!StopRender())
            Log.Error("[PresentHost] 停机握手未完成：仍按 WPF 要求销毁 HWND，GPU 资源留给渲染线程或进程退出收尾");

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
                var (cw, ch) = ClientSize();
                if (cw != target.Width || ch != target.Height)
                {
                    target.Resize(cw, ch);
                    Resized?.Invoke(this);
                }

                try
                {
                    Prepare?.Invoke(this);
                }
                catch (Exception ex)
                {
                    Log.Error("[PresentHost] 帧准备异常：" + ex.Message, ex);
                }

                clock.WaitNext();

                try
                {
                    Frame?.Invoke(this, clock.Dt);
                    target.Present();
                }
                catch (Exception ex)
                {
                    Log.Error("[PresentHost] 帧呈现异常：" + ex.Message, ex);
                }

                if (gpu.IsDeviceRemoved)
                {
                    Error = $"设备已移除（{gpu.RemovedReasonText()}），停止出帧";
                    Log.Error($"[PresentHost] {Error}");
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
            try
            {
                Shutdown?.Invoke(this);
            }
            catch (Exception ex)
            {
                Log.Error("[PresentHost] 收尾释放失败：" + ex.Message, ex);
            }

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
