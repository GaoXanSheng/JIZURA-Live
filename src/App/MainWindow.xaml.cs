using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using JizuraLive.Logging;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace JizuraLive.App;

public partial class MainWindow : Window
{
    private readonly Settings _settings;

    // M0 占位验证画布的动画钟：证明 SkiaSharp 在 net10 + WPF 上端到端可用。
    // 编辑器舞台（Renderer.Frame 驱动）将在 M2 取代它
    private readonly DispatcherTimer _animTimer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Queue<double> _frameMs = new();
    private long _frameCount;
    private double _lastPaintMs;

    public MainWindow(Settings settings)
    {
        InitializeComponent();

        _settings = settings;

        // 最大化 ↔ 窗口化按钮的图标/提示跟随窗口状态
        StateChanged += (_, _) => UpdateMaxButton();

        _animTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(1000 / 60.0)
        };
        _animTimer.Tick += (_, _) => Stage.InvalidateVisual();
        _animTimer.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ((HwndSource)PresentationSource.FromVisual(this))?.AddHook(WndProc);
        UpdateMaxButton();
    }

    // WindowStyle=None 的窗口最大化默认盖住任务栏、边缘还悬出屏幕一截：
    // 挂 WM_GETMINMAXINFO 把最大化尺寸/位置钉到最近显示器的 work area（任务栏以内）解决
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO)
            return IntPtr.Zero;

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        var hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (hMonitor == IntPtr.Zero || !GetMonitorInfo(hMonitor, ref mi))
            return IntPtr.Zero;

        mmi.ptMaxPosition.x = mi.rcWork.left - mi.rcMonitor.left;
        mmi.ptMaxPosition.y = mi.rcWork.top - mi.rcMonitor.top;
        mmi.ptMaxSize.x = mi.rcWork.right - mi.rcWork.left;
        mmi.ptMaxSize.y = mi.rcWork.bottom - mi.rcWork.top;
        Marshal.StructureToPtr(mmi, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    // ---------------- M0 SkiaSharp 验证画布 ----------------

    private void Stage_PaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var paintStart = Stopwatch.GetTimestamp();
        var canvas = e.Surface.Canvas;
        var w = e.Info.Width;
        var h = e.Info.Height;
        var t = (float)_clock.Elapsed.TotalSeconds;

        canvas.Clear(new SKColor(15, 15, 18));

        // 动态渐变背景：验证着色与混合路径
        using var bg = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(w, h),
                new[] { new SKColor(38, 34, 66), new SKColor(18, 24, 40), new SKColor(46, 26, 40) },
                new[] { 0f, 0.55f, 1f },
                SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(0, 0, w, h, bg);

        // 轨道圆点：验证连续动画与时钟
        const int orbs = 7;
        for (var i = 0; i < orbs; i++)
        {
            var phase = t * (0.35f + i * 0.11f) + i * ((float)Math.PI * 2 / orbs);
            var x = w / 2f + MathF.Cos(phase) * w * 0.32f;
            var y = h / 2f + MathF.Sin(phase * 1.3f) * h * 0.3f;
            var r = 14 + 10 * MathF.Sin(t * 1.7f + i);

            using var orb = new SKPaint
            {
                IsAntialias = true,
                Color = SKColor.FromHsl((t * 24 + i * 34) % 360, 70, 62, 220)
            };
            canvas.DrawCircle(x, y, r, orb);
        }

        // 文字渲染：验证字体管线（中文 + 日文假名 + 等宽数字）；SkiaSharp 3.x 走 SKFont
        using var typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI", SKFontStyle.Normal);
        using var font = new SKFont(typeface);

        float TextLine(string s, float size, SKColor color, float y)
        {
            font.Size = size;
            using var p = new SKPaint { IsAntialias = true, Color = color };
            canvas.DrawText(s, 28, y, SKTextAlign.Left, font, p);
            return y + size * 1.5f;
        }

        var y0 = 44f;
        y0 = TextLine("JIZURA Live — 纯 C# 重构（pure-csharp 分支）", 22, new SKColor(230, 230, 236), y0);
        y0 = TextLine("SkiaSharp 渲染検証 / レンダリング検証 — Skia on net10.0-windows", 15, new SKColor(160, 200, 255), y0);

        var fps = _frameMs.Count > 0 ? 1000.0 / (_frameMs.Average()) : 0;
        var info =
            $"frame {_frameCount,7}  |  paint {_lastPaintMs,5:0.00} ms  |  {fps,5:0} fps  |  canvas {w}×{h}  |  t {t,7:0.00}s";
        TextLine(info, 14, new SKColor(140, 230, 160), y0 + 6);
        TextLine($"Spout={_settings.SpoutEnabled} {_settings.Width}×{_settings.Height}@{_settings.RenderFps} → M2 回归    引擎骨架：src/Engine（见 README 移植对照表）", 13,
            new SKColor(200, 200, 210, 160), y0 + 6);

        // 描边矩形边框：验证路径/描边
        using var border = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2,
            Color = new SKColor(90, 90, 110)
        };
        canvas.DrawRect(6, 6, w - 12, h - 12, border);

        // paint 本身耗时（不含 WPF 呈现）
        _lastPaintMs = (Stopwatch.GetTimestamp() - paintStart) * 1000.0 / Stopwatch.Frequency;
        _frameCount++;
        _frameMs.Enqueue(_lastPaintMs + 0.001);
        while (_frameMs.Count > 60)
            _frameMs.Dequeue();
    }

    protected override void OnClosed(EventArgs e)
    {
        _animTimer.Stop();
        base.OnClosed(e);
    }

    // ---------------- 窗口 chrome ----------------

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 双击标题栏在最大化/窗口化之间切换（标准窗口行为）
        if (e.ClickCount == 2)
        {
            BtnMax_Click(sender, e);
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
            try
            {
                DragMove();
            }
            catch (Exception ex)
            {
                Log.Debug("DragMove failed: " + ex);
            }
    }

    private void BtnMin_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void BtnMax_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    // 最大化态显示「窗口化」图标（还原），普通态显示「最大化」
    private void UpdateMaxButton()
    {
        var max = WindowState == WindowState.Maximized;
        BtnMax.Content = max ? "\uE923" : "\uE739";
        BtnMax.ToolTip = max ? "窗口化" : "最大化";
    }

    private const int WM_GETMINMAXINFO = 0x0024;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
