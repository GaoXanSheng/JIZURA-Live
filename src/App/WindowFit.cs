using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace JizuraLive.App;

/// <summary>
/// 「铺满宿主窗口」的浮层窗口（遮罩弹窗）的定位。
///
/// 位置**不能**抄宿主的 <see cref="Window.Left"/>/<see cref="Window.Top"/>：宿主最大化时 WPF 报的是
/// **还原位置**（而 <see cref="Window.ActualWidth"/> 报的是当前尺寸），照抄会把遮罩摆到还原点——
/// 随用户上次还原的位置漂移，看着像随机跑偏；尺寸对、位置错正是这个错配的症状。
/// 也不能用 <see cref="UIElement.PointToScreen"/> 的客户区原点：WindowStyle=None 的最大化窗口
/// 客户区比窗口矩形内缩一圈隐形边框（96 DPI 下 7px），会盖不住宿主的窗口边缘。
/// 所以取宿主 HWND 的窗口矩形（物理像素）再按宿主 DPI 折算回 DIP。
/// </summary>
public static class WindowFit
{
    /// <summary>
    /// 把 <paramref name="overlay"/> 摆成与 <paramref name="owner"/> 当前窗口矩形等大同位；
    /// 宿主没铺开（或没有宿主）时返回 false，由调用方决定兜底位置。
    /// Show()/ShowDialog() 之前调用同样有效（值随窗口创建一起生效，避免先出现在系统默认位置再跳）。
    /// </summary>
    public static bool TryCover(Window overlay, Window? owner)
    {
        if (owner == null) return false;
        var hwnd = new WindowInteropHelper(owner).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return false;

        var w = r.R - r.L;
        var h = r.B - r.T;
        if (w < 200 || h < 200) return false; // 宿主还没铺开：交给调用方兜底

        var scale = VisualTreeHelper.GetDpi(owner).PixelsPerDip; // 宿主所在显示器的缩放
        overlay.Left = r.L / scale;
        overlay.Top = r.T / scale;
        overlay.Width = w / scale;
        overlay.Height = h / scale;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int L, T, R, B;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
}
