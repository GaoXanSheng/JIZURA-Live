using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using JizuraLive.Logging;

namespace JizuraLive.Render.Gpu;

public sealed class PresentHost : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int SsNotify = 0x00000100;
    private const int SwpNoMove = 0x0002;
    private const int SwpNoZOrder = 0x0004;
    private const int SwpNoActivate = 0x0010;

    private const uint WmSetCursor = 0x0020;
    private const uint WmNcHitTest = 0x0084;
    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmCaptureChanged = 0x0215;
    private const uint WmMouseLeave = 0x02A3;
    private const uint TmeLeave = 0x00000002;
    private const int HtClient = 1;
    private const int IdcArrow = 32512;
    private const int IdcSizeAll = 32644;

    private IntPtr _hwnd = IntPtr.Zero;
    private Thread? _thread;
    private volatile bool _running;
    private readonly DispatcherTimer _sizeTimer;
    private readonly SubclassProc _subclassProc;
    private bool _pointerDown;
    private bool _trackLeave;

    public PresentHost()
    {
        _subclassProc = WndSubclass;
        SizeChanged += (_, _) => ResizeChild();
        _sizeTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _sizeTimer.Tick += (_, _) => ResizeChild();
        Loaded += (_, _) => _sizeTimer.Start();
        Unloaded += (_, _) => _sizeTimer.Stop();
    }

    public int InitialWidth { get; set; } = 1920;
    public int InitialHeight { get; set; } = 1080;

    private volatile bool _vsync = true;

    public bool VSync
    {
        get => _vsync;
        set
        {
            _vsync = value;
            if (Target is not null) Target.VSync = value;
        }
    }

    public bool AllowTearing { get; set; }

    public event Action<PresentHost>? Ready;

    public event Action<PresentHost>? BeforeResize;

    public event Action<PresentHost>? Prepare;

    public event Action<PresentHost, double>? Frame;

    public event Action<PresentHost>? Resized;

    public event Action<PresentHost>? Shutdown;

    public event Action<Point>? PointerPressed;

    public event Action<Point>? PointerMoved;

    public event Action? PointerReleased;

    public Func<Point, bool>? MoveCursorHitTest;

    public string? Error { get; private set; }

    public D3DDevice? Gpu { get; private set; }
    public SwapChainTarget? Target { get; private set; }
    public FrameClock? Clock { get; private set; }

    public double Fps => Clock?.Fps ?? 0;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _hwnd = CreateWindowExW(0, "STATIC", null, WsChild | WsVisible | SsNotify,
            0, 0, InitialWidth, InitialHeight, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            Error = $"子窗口创建失败 err={Marshal.GetLastWin32Error()}";
            Log.Error("[PresentHost] " + Error);
            return new HandleRef(this, IntPtr.Zero);
        }

        if (!SetWindowSubclass(_hwnd, _subclassProc, (UIntPtr)1, IntPtr.Zero))
            Log.Warn($"[PresentHost] 子窗口输入挂接失败 err={Marshal.GetLastWin32Error()}");

        _running = true;
        _thread = new Thread(RenderLoop)
        {
            IsBackground = true,
            Name = "RenderCore.Present",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();

        Dispatcher.BeginInvoke(new Action(ResizeChild));

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

        if (_hwnd != IntPtr.Zero) RemoveWindowSubclass(_hwnd, _subclassProc, (UIntPtr)1);
        if (hwnd.Handle != IntPtr.Zero) DestroyWindow(hwnd.Handle);
        _hwnd = IntPtr.Zero;
    }

    protected override void OnWindowPositionChanged(System.Windows.Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        ResizeChild();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        ResizeChild();
    }

    private void ResizeChild()
    {
        if (_hwnd == IntPtr.Zero) return;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        int w = Math.Max(1, (int)Math.Round(ActualWidth * dpi.DpiScaleX));
        int h = Math.Max(1, (int)Math.Round(ActualHeight * dpi.DpiScaleY));
        if (w <= 1 || h <= 1) return;

        if (GetClientRect(_hwnd, out var current)
            && current.Right - current.Left == w
            && current.Bottom - current.Top == h)
            return;

        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, w, h, SwpNoMove | SwpNoZOrder | SwpNoActivate);
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
                    BeforeResize?.Invoke(this);
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

    private IntPtr WndSubclass(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam,
        UIntPtr uIdSubclass, IntPtr dwRefData)
    {
        switch (msg)
        {
            case WmNcHitTest:
                return (IntPtr)HtClient;

            case WmMouseMove:
                if (!_trackLeave)
                {
                    _trackLeave = true;
                    var track = new TrackMouseEventData
                    {
                        Size = (uint)Marshal.SizeOf<TrackMouseEventData>(),
                        Flags = TmeLeave,
                        Hwnd = hwnd,
                    };
                    TrackMouseEvent(ref track);
                }
                RaisePointer(PointerMoved, lParam);
                break;

            case WmLButtonDown:
                _pointerDown = true;
                SetCapture(hwnd);
                RaisePointer(PointerPressed, lParam);
                return IntPtr.Zero;

            case WmLButtonUp:
                if (_pointerDown)
                {
                    _pointerDown = false;
                    ReleaseCapture();
                    RaisePointerReleased();
                }
                return IntPtr.Zero;

            case WmCaptureChanged:
                if (_pointerDown)
                {
                    _pointerDown = false;
                    RaisePointerReleased();
                }
                break;

            case WmMouseLeave:
                _trackLeave = false;
                break;

            case WmSetCursor:
                if (((long)lParam & 0xFFFF) == HtClient)
                {
                    SetCursor(LoadCursorW(IntPtr.Zero,
                        (IntPtr)(_pointerDown || HitTestMove() ? IdcSizeAll : IdcArrow)));
                    return (IntPtr)1;
                }
                break;
        }

        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private bool HitTestMove()
    {
        var test = MoveCursorHitTest;
        if (test is null || !GetCursorPos(out var p)) return false;
        if (!ScreenToClient(_hwnd, ref p)) return false;

        try { return test(ToDip(p.X, p.Y)); }
        catch (Exception ex)
        {
            Log.Warn("[PresentHost] 光标命中测试异常：" + ex.Message);
            return false;
        }
    }

    private Point ToDip(int x, int y)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Point(x / dpi.DpiScaleX, y / dpi.DpiScaleY);
    }

    private void RaisePointer(Action<Point>? handler, IntPtr lParam)
    {
        if (handler is null) return;
        try { handler(ToDip(LoWord(lParam), HiWord(lParam))); }
        catch (Exception ex)
        {
            Log.Warn("[PresentHost] 鼠标事件处理异常：" + ex.Message);
        }
    }

    private void RaisePointerReleased()
    {
        try { PointerReleased?.Invoke(); }
        catch (Exception ex)
        {
            Log.Warn("[PresentHost] 鼠标事件处理异常：" + ex.Message);
        }
    }

    private static int LoWord(IntPtr value) => unchecked((short)(long)value);

    private static int HiWord(IntPtr value) => unchecked((short)((long)value >> 16));

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam,
        UIntPtr uIdSubclass, IntPtr dwRefData);

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouseEventData
    {
        public uint Size;
        public uint Flags;
        public IntPtr Hwnd;
        public uint HoverTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, IntPtr data);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursorW(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point32 p);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr hwnd, ref Point32 p);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TrackMouseEvent(ref TrackMouseEventData data);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string? windowName,
        int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
        int x, int y, int cx, int cy, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
}
