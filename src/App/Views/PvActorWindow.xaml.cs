using System.Windows;

namespace JizuraLive.App.Views;

public partial class PvActorWindow : Window
{
    private static PvActorWindow? _instance;

    public PvActorWindow()
    {
        InitializeComponent();
        Closed += (_, _) =>
        {
            _instance = null;
            PreviewView.Dispose();
        };
    }

    /// <summary>
    /// 打开或将已有独立窗口激活到最前
    /// </summary>
    public static void OpenOrFocus()
    {
        if (_instance != null)
        {
            if (_instance.WindowState == WindowState.Minimized)
                _instance.WindowState = WindowState.Normal;
            _instance.Activate();
            return;
        }

        _instance = new PvActorWindow();
        _instance.Show();
    }

    private void CkTopmost_Changed(object sender, RoutedEventArgs e)
    {
        Topmost = CkTopmost.IsChecked == true;
    }
}
