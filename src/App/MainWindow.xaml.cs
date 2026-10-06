using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using JizuraLive.Engine;
using JizuraLive.Logging;
using JizuraLive.Spout;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace JizuraLive.App;

public partial class MainWindow : Window
{
    private readonly Settings _settings;
    private readonly PreviewEngine _preview = new();
    private readonly SpoutOut _spout = new();

    // 预览帧率跟 RenderFps（钳到 60：预览是给人看的，引擎作画按 koma 量化，再快只是烧 CPU）
    private readonly DispatcherTimer _renderTimer;
    private readonly DispatcherTimer _transportTimer;
    private bool _syncSlider; // 程序性刷滑条时置真，ValueChanged 不回灌 Seek
    private bool _scrubbing;

    public MainWindow(Settings settings)
    {
        InitializeComponent();

        _settings = settings;

        // 最大化 ↔ 窗口化按钮的图标/提示跟随窗口状态
        StateChanged += (_, _) => UpdateMaxButton();

        ChkSpout.IsChecked = settings.SpoutEnabled;

        var fps = Math.Clamp(settings.RenderFps, 1, 60);
        _renderTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(1000 / (double)fps),
        };
        _renderTimer.Tick += (_, _) =>
        {
            _preview.Tick();
            Stage.InvalidateVisual();
        };
        _renderTimer.Start();

        // Transport 状态同步（滑条/时间文本）10Hz 够了，别跟渲染抢
        _transportTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _transportTimer.Tick += (_, _) => SyncTransport();
        _transportTimer.Start();

        SyncTransport();
        UpdateStatusText();
        _preview.Play(); // 预览默认自动播放（暂停态泵只出脏帧，起播才能看到真帧率）

        Loaded += (_, _) =>
        {
            // Spout 启动放 Loaded：管线建 D3D 设备要几百毫秒，别挡窗口先画出来
            if (_settings.SpoutEnabled)
                StartSpout();
        };
    }

    // ---------------- 引擎预览舞台 ----------------

    private void Stage_PaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        var w = e.Info.Width;
        var h = e.Info.Height;
        var plan = _preview.Plan;
        if (plan == null) return;

        // contain 适配居中（预览画布比例 ≠ 工程画幅时出暗边）；预览不超采（fit 钳 1.0）
        var fit = Math.Min(w / (double)plan.W, h / (double)plan.H);
        var scale = Math.Min(fit, 1.0);
        var rw = Math.Max(1, (int)Math.Round(plan.W * scale));
        var rh = Math.Max(1, (int)Math.Round(plan.H * scale));
        var t = _preview.CurrentTime;

        canvas.Clear(new SKColor(15, 15, 18));

        // SkiaSharp 3.119 坑（headless 复现：obj/repro，AV 于 Frame 的 _fcCv.Width）：
        // 引擎整帧缓存会把 SnapshotOf(ctx) 存起来跨帧用——交给 Frame 的面必须保留（绝不 Dispose），
        // 否则面一释放缓存里的快照就悬垂，下一次缓存命中即 AV。同理 blit 不用 Snapshot()
        //（dispose 快照会弄脏面的 cached-image 记账），改 PeekPixels + FromPixels 借用像素。
        // 旧尺寸面孤儿化进 graveyard，退出时随渲染器一起释放
        if (_stageSurface == null || _stageW != rw || _stageH != rh)
        {
            if (_stageSurface != null) _stageGraveyard.Add(_stageSurface);
            _stageSurface = SKSurface.Create(new SKImageInfo(rw, rh, SKColorType.Bgra8888, SKAlphaType.Premul));
            _stageW = rw;
            _stageH = rh;
        }

        lock (EngineRenderGate.Lock)
            _preview.Renderer.Frame(_stageSurface.Canvas, plan, t, new RenderOpts { Scale = scale, Stage = true });

        var px = _stageSurface.PeekPixels();
        if (px != null)
        {
            using var borrow = SKImage.FromPixels(px.Info, px.GetPixels(), px.RowBytes);
            canvas.DrawImage(borrow, (w - rw) / 2f, (h - rh) / 2f);
        }
    }

    // 引擎渲染面（保留，尺寸变化才换新）+ 换下的旧面（退出时统一释放）
    private SKSurface? _stageSurface;
    private int _stageW = -1, _stageH = -1;
    private readonly List<SKSurface> _stageGraveyard = new();

    // ---------------- Transport ----------------

    private void BtnPlay_Click(object sender, RoutedEventArgs e) => _preview.TogglePlay();

    private void SldTime_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncSlider || _preview == null) return;
        _preview.Seek(e.NewValue);
        UpdateTimeText();
    }

    private void SldTime_Down(object sender, MouseButtonEventArgs e) => _scrubbing = true;

    private void SldTime_Up(object sender, MouseButtonEventArgs e) => _scrubbing = false;

    private void ChkLoop_Changed(object sender, RoutedEventArgs e)
    {
        if (_preview == null) return;
        _preview.Loop = ChkLoop.IsChecked == true;
    }

    /// <summary>10Hz 刷 Transport：滑条跟随播放（scrub 中不回灌），时间文本、播放键图标。</summary>
    private void SyncTransport()
    {
        var dur = _preview.Duration;
        _syncSlider = true;
        SldTime.Maximum = Math.Max(0.001, dur);
        if (!_scrubbing)
            SldTime.Value = Math.Min(_preview.CurrentTime, SldTime.Maximum);
        _syncSlider = false;
        UpdateTimeText();
        BtnPlay.Content = _preview.IsPlaying ? "\uE769" : "\uE768"; // ⏸ / ▶（Segoe MDL2）
    }

    private void UpdateTimeText()
    {
        TblTime.Text = $"{J.FmtTime(_preview.CurrentTime)} / {J.FmtTime(_preview.Duration)}";
    }

    private void UpdateStatusText()
    {
        var plan = _preview.Plan;
        TblStatus.Text =
            $"{plan.W}×{plan.H} · {plan.Cuts.Count} 切分 · seed {plan.Seed}"
            + (_spout.IsRunning ? $" · Spout→{_spout.SenderName}" : "");
    }

    // ---------------- 顶栏：歌词编辑 / 重新规划 / Spout 开关 ----------------

    private void BtnLyrics_Click(object sender, RoutedEventArgs e)
    {
        var box = new TextBox
        {
            Text = _preview.Project.Lyrics,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 13,
            Padding = new Thickness(8),
        };
        var apply = new Button { Content = "应用", Padding = new Thickness(16, 3, 16, 3) };
        var cancel = new Button { Content = "取消", Padding = new Thickness(16, 3, 16, 3) };
        var panel = new DockPanel { Margin = new Thickness(10) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        buttons.Children.Add(apply);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        panel.Children.Add(box);

        var win = new Window
        {
            Title = "歌词",
            Owner = this,
            Width = 560,
            Height = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF17171B")),
            Content = panel,
        };

        apply.Click += (_, _) =>
        {
            if (box.Text != _preview.Project.Lyrics)
            {
                _preview.Project.Lyrics = box.Text;
                _preview.Replan();
                _preview.Seek(0);
                UpdateStatusText();
                Log.Info("[MainWindow] 歌词已更新并重新规划");
            }

            win.Close();
        };
        cancel.Click += (_, _) => win.Close();
        win.Show();
        box.Focus();
    }

    private void BtnReplan_Click(object sender, RoutedEventArgs e)
    {
        _preview.Replan();
        UpdateStatusText();
    }

    private void ChkSpout_Changed(object sender, RoutedEventArgs e)
    {
        if (_settings == null || _spout == null) return; // XAML 初始化期的空触发
        var on = ChkSpout.IsChecked == true;
        if (on == _settings.SpoutEnabled && (on == _spout.IsRunning || !IsLoaded)) return;
        _settings.SpoutEnabled = on;
        _settings.Save();
        if (on) StartSpout();
        else StopSpout();
        UpdateStatusText();
    }

    // ---------------- Spout 直出（SpoutOut 管线） ----------------

    private SpoutOutOptions SpoutOpts() => new()
    {
        SenderName = _settings.SpoutName,
        MainTransparent = _settings.SpoutTransparent,
        Width = _settings.Width,
        Height = _settings.Height,
        Fps = _settings.RenderFps,
        LyricsEnabled = _settings.SpoutLyricsEnabled,
        // 字幕层画得比主通道快没有意义（旧管线同钳法）
        LyricsFps = Math.Min(_settings.LyricsFps, _settings.RenderFps),
    };

    public void StartSpout()
    {
        if (_spout.IsRunning)
        {
            // 名称变了只有重建发送端才生效
            if (!string.Equals(_spout.SenderName, _settings.SpoutName, StringComparison.Ordinal))
                RestartSpout();
            return;
        }

        _spout.Start(_preview, SpoutOpts());
        UpdateStatusText();
    }

    public void StopSpout()
    {
        _spout.Stop();
        UpdateStatusText();
    }

    /// <summary>按当前 settings 重建管线（分辨率/帧率/名称/透明底改动由此生效）。</summary>
    public void RestartSpout()
    {
        if (!_spout.IsRunning)
        {
            if (_settings.SpoutEnabled) StartSpout();
            return;
        }

        Log.Info($"[MainWindow] 重启 Spout 管线：{_settings.SpoutName} {_settings.Width}×{_settings.Height}@{_settings.RenderFps}"
                 + ($", 字幕通道{(_settings.SpoutLyricsEnabled ? "开" : "关")}"));
        _spout.Restart(_preview, SpoutOpts());
        UpdateStatusText();
    }

    /// <summary>设置页「重启渲染引擎」（/api/app/render/restart）的落点。</summary>
    public void RestartRender()
    {
        if (_settings.SpoutEnabled) RestartSpout();
        else StopSpout();
    }

    // ---------------- 键盘 ----------------

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        // 空格 = 播放/暂停；歌词编辑框里打空格要照常输入
        if (e.Key == Key.Space && Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase)
        {
            _preview.TogglePlay();
            e.Handled = true;
        }
    }

    // ---------------- 窗口 chrome ----------------

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

    protected override void OnClosed(EventArgs e)
    {
        _renderTimer.Stop();
        _transportTimer.Stop();
        // 停泵释放：发送端、D3D 纹理/设备；渲染器先于 Skia 面释放（缓存快照引用着面）
        _spout.Dispose();
        _preview.Dispose();
        foreach (var s in _stageGraveyard) s.Dispose();
        _stageSurface?.Dispose();
        _stageSurface = null;
        base.OnClosed(e);
    }
}
