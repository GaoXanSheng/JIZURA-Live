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
        Window owner)
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

        PositionOver(win, owner);

        win.Show();

        _active = win;
    }

    // 定位：主窗口右上内侧（前端「导出」页唤起，无锚点按钮）。
    private static void PositionOver(
        SettingsWindow win,
        Window owner)
    {
        if (owner == null)
            return;

        // SettingsPanel 固定 Width=450；贴主窗口右上，向下留出标题栏
        win.Left = owner.Left + owner.ActualWidth - 450 - 24;
        win.Top = owner.Top + 48;
    }
}
