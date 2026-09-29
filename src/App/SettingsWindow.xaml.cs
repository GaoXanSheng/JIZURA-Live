using System.Windows;
using System.Windows.Input;

namespace JizuraLive;

public partial class SettingsWindow : Window
{
    private static SettingsWindow? _active;

    private SettingsWindow(
        Settings settings)
    {
        InitializeComponent();

        SettingsCtl.Initialize(
            settings);

        PreviewKeyDown +=
            (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    Close();
                }
            };

        Loaded +=
            (_, _) =>
            {
                SettingsCtl.MoveFocus(
                    new TraversalRequest(
                        FocusNavigationDirection.First));
            };

        Closed +=
            (_, _) =>
            {
                if (_active == this)
                {
                    _active = null;
                }
            };
    }

    // --------------------------------------------------------------------
    // 设置独立窗口（替代原来的 Popup）
    //
    // 主浏览器是 HwndHost 版 CefSharp，CEF 持有真实的原生子窗口；
    // Win32 键盘焦点在原生子窗口上时，WPF 侧的控件收不到按键。
    // 独立顶级窗口有自己的 HWND，打开后 Win32 焦点随之切换，
    // 输入时主界面 CEF 原生窗口收不到键盘消息，问题即消失。
    // --------------------------------------------------------------------

    public static void Toggle(
        Settings settings,
        Window owner,
        FrameworkElement anchor)
    {
        var existing = _active;

        if (existing != null)
        {
            existing.Close();
            return;
        }

        var win = new SettingsWindow(
            settings)
        {
            Owner = owner
        };

        PositionOver(win, owner, anchor);

        win.Show();

        _active = win;

        Log.Info(
            $"设置窗口已显示：" +
            $"Left={win.Left:F0}, Top={win.Top:F0}, " +
            $"Size={win.ActualWidth:F0}x{win.ActualHeight:F0}");
    }

    // 定位：右缘对齐锚点（齿轮按钮），顶部贴按钮下方。
    // PointToScreen 得到设备像素，再除以 DPI 变换转回 WPF 逻辑坐标，
    // 这样窗口最大化、跨 DPI 缩放显示器时位置都正确。
    private static void PositionOver(
        SettingsWindow win,
        Window owner,
        FrameworkElement anchor)
    {
        if (owner == null || anchor == null)
            return;

        var transform =
            PresentationSource
            .FromVisual(owner)
            ?.CompositionTarget
            ?.TransformToDevice;

        double scaleX =
            transform?.M11 ?? 1.0;

        double scaleY =
            transform?.M22 ?? 1.0;

        if (scaleX <= 0)
            scaleX = 1.0;

        if (scaleY <= 0)
            scaleY = 1.0;

        var screenPx =
            anchor.PointToScreen(
                new Point(
                    anchor.ActualWidth,
                    anchor.ActualHeight));

        double dipX =
            screenPx.X / scaleX;

        double dipY =
            screenPx.Y / scaleY;

        // SettingsPanel 固定 Width=450
        win.Left = dipX - 450;

        win.Top = dipY + 4;
    }
}
