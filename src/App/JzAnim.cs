using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace JizuraLive.App;

public static class JzAnim
{

    public static readonly Duration Fast = new(TimeSpan.FromMilliseconds(110));

    public static readonly Duration Out = new(TimeSpan.FromMilliseconds(130));

    public static readonly Duration Panel = new(TimeSpan.FromMilliseconds(140));

    public static readonly Duration Dialog = new(TimeSpan.FromMilliseconds(150));

    public static readonly CubicEase EaseOut = MakeEase(EasingMode.EaseOut);

    private static CubicEase MakeEase(EasingMode mode)
    {
        var e = new CubicEase { EasingMode = mode };
        e.Freeze();
        return e;
    }

    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    public static void FadeIn(FrameworkElement? el, Duration? dur = null, TimeSpan delay = default)
    {
        if (el == null || !Enabled) return;
        var anim = new DoubleAnimation(0, 1, dur ?? Panel) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop };
        if (delay > TimeSpan.Zero)
        {
            el.Opacity = 0;
            anim.BeginTime = delay;
            anim.Completed += (_, _) => el.Opacity = 1;
        }
        el.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    public static void FadeSlideIn(FrameworkElement? el, double dy = 4, Duration? dur = null,
        TimeSpan delay = default)
    {
        if (el == null || !Enabled) return;
        var d = dur ?? Panel;
        FadeIn(el, d, delay);
        var tt = new TranslateTransform(0, 0);
        el.RenderTransform = tt;
        tt.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(dy, 0, d)
            {
                BeginTime = delay > TimeSpan.Zero ? delay : null,
                EasingFunction = EaseOut,
                FillBehavior = FillBehavior.Stop
            });
    }

    public static void PopIn(FrameworkElement? el, double from = 0.985, double dy = 6, Duration? dur = null)
    {
        if (el == null || !Enabled) return;
        var d = dur ?? Dialog;
        var sc = new ScaleTransform(1, 1);
        var tt = new TranslateTransform(0, 0);
        var tg = new TransformGroup();
        tg.Children.Add(sc);
        tg.Children.Add(tt);
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        el.RenderTransform = tg;
        sc.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(from, 1, d) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });
        sc.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(from, 1, d) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });
        tt.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(dy, 0, d) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });
    }

    public static void PlayWindowIn(FrameworkElement? mask, FrameworkElement? card)
    {
        FadeIn(mask, Dialog);
        PopIn(card, 0.985, 6, Dialog);
    }

    public static void FadeInBrush(Brush? b, Duration? dur = null)
    {
        if (b == null || !Enabled) return;
        b.BeginAnimation(Brush.OpacityProperty,
            new DoubleAnimation(0, 1, dur ?? Dialog) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });
    }

    public static void ShowFade(UIElement? el, bool on, double dy = 4)
    {
        if (el == null) return;
        if (!on)
        {
            el.Visibility = Visibility.Collapsed;
            return;
        }
        if (el.Visibility == Visibility.Visible) return;
        el.Visibility = Visibility.Visible;
        FadeSlideIn(el as FrameworkElement, dy);
    }

    private sealed class ScrollAnim
    {
        public ScrollViewer Sv = null!;
        public double From, To, Last;
        public long T0;
        public int Ms;
        public DispatcherTimer Timer = null!;
    }

    private static ScrollAnim? _scroll;

    public static bool AnimateScroll(ScrollViewer? sv, double target, int ms = 140)
    {
        if (sv == null || !Enabled) return false;
        var from = sv.VerticalOffset;
        StopScroll();
        if (Math.Abs(target - from) < 1) return true;
        var a = new ScrollAnim
        {
            Sv = sv, From = from, To = target, Last = from,
            T0 = Environment.TickCount64, Ms = Math.Max(1, ms),
        };
        a.Timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(16) };
        a.Timer.Tick += (_, _) =>
        {
            if (!ReferenceEquals(_scroll, a)) return;
            if (Math.Abs(a.Sv.VerticalOffset - a.Last) > 0.5) { StopScroll(); return; }
            var p = Math.Clamp((Environment.TickCount64 - a.T0) / (double)a.Ms, 0, 1);
            var v = a.From + (a.To - a.From) * (1 - Math.Pow(1 - p, 3));
            a.Sv.ScrollToVerticalOffset(v);
            a.Last = v;
            if (p >= 1) StopScroll();
        };
        _scroll = a;
        a.Timer.Start();
        return true;
    }

    public static void StopScroll()
    {
        if (_scroll == null) return;
        _scroll.Timer.Stop();
        _scroll = null;
    }
}
