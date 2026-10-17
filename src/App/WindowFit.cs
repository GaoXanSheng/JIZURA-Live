using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace JizuraLive.App;

public static class WindowFit
{
    public static bool TryCover(Window overlay, Window? owner)
    {
        if (owner == null) return false;
        var hwnd = new WindowInteropHelper(owner).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return false;

        var w = r.R - r.L;
        var h = r.B - r.T;
        if (w < 200 || h < 200) return false;

        var scale = VisualTreeHelper.GetDpi(owner).PixelsPerDip;
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
