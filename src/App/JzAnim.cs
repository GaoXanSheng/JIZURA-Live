using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace JizuraLive.App;

/// <summary>
/// 界面动效的统一入口与 token（界面层唯一的动画代码；渲染引擎里的动画在 src/Engine）。
///
/// 纪律（与后台渲染泵 MainWindow.RenderPump / 抖动探针 _uiProbe 共存）：
///   1) 只动 Opacity 与 RenderTransform —— GPU 合成，不触发布局、不重绘、不重建 FormattedText；
///   2) 只做「进入」动画 + 短时长（110–150ms），不做需要延迟收起的退出动画
///      （面板隐藏仍即时 Collapsed，保住 TechTab 之类以 IsVisibleChanged 为信号的逻辑）；
///   3) 动画一律 FillBehavior.Stop —— 播完自动回到基值，不留 HoldEnd 时钟
///      （基值本身就是终值，所以既不会闪一下，也不会出现「清掉动画后停在中间态」）；
///   4) 弹窗与程序化构建的界面元素用它做入场；位移/弹窗类由 Enabled 统一跟随
///      系统「在 Windows 中显示动画」开关。
/// </summary>
public static class JzAnim
{
    // ---------------- token（界面层唯一一份时长/缓动） ----------------

    /// <summary>hover 进入 / 勾选弹入（110ms）。</summary>
    public static readonly Duration Fast = new(TimeSpan.FromMilliseconds(110));

    /// <summary>hover 退出（130ms；比进入略长，收起不抢眼）。</summary>
    public static readonly Duration Out = new(TimeSpan.FromMilliseconds(130));

    /// <summary>面板 / 浮层进入（140ms）。</summary>
    public static readonly Duration Panel = new(TimeSpan.FromMilliseconds(140));

    /// <summary>弹窗进入（150ms）。</summary>
    public static readonly Duration Dialog = new(TimeSpan.FromMilliseconds(150));

    /// <summary>共享缓动：进入用减速曲线（快起慢收）；多份 Storyboard 共用一份冻结实例是安全的。</summary>
    public static readonly CubicEase EaseOut = MakeEase(EasingMode.EaseOut);

    private static CubicEase MakeEase(EasingMode mode)
    {
        var e = new CubicEase { EasingMode = mode };
        e.Freeze();
        return e;
    }

    /// <summary>系统「在 Windows 中显示动画」关闭时，代码侧动效直接落到终值。</summary>
    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    // ---------------- 进入动画 ----------------

    /// <summary>淡入：Opacity 0→1（元素基值保持 1，播完不留时钟）。
    /// <paramref name="delay"/> &gt; 0 时元素先隐形等着（否则延迟期间会先亮着再淡入），播完把基值补回 1。</summary>
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

    /// <summary>
    /// 淡入 + 轻微上浮（面板 / 浮层进入，也用于列表卡片错峰入场）。
    /// 契约：会整体接管 <paramref name="el"/> 的 RenderTransform —— 只可用于本身没有
    /// RenderTransform 的元素（各面板、浮层根、卡片）；Transport 的拖柄之类自带变换的元素别传。
    /// </summary>
    public static void FadeSlideIn(FrameworkElement? el, double dy = 4, Duration? dur = null,
        TimeSpan delay = default)
    {
        if (el == null || !Enabled) return;
        var d = dur ?? Panel;
        FadeIn(el, d, delay);
        // 基值必须是终值（0）：FillBehavior.Stop 播完回落基值，基值若设成起点 dy 元素会永久下移
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

    /// <summary>弹窗卡片微缩放 + 上浮（不做透明度 —— 遮罩那层已经在淡入，卡片再淡一层会发灰）。</summary>
    public static void PopIn(FrameworkElement? el, double from = 0.985, double dy = 6, Duration? dur = null)
    {
        if (el == null || !Enabled) return;
        var d = dur ?? Dialog;
        // 基值 = 终值（1 / 0）：播完回落终值。此前基值设成起点（from/dy），卡片动画结束后
        // 永久停在 0.985 缩放 —— RenderTransform 缩放是重采样，全篇文字跟着发虚（且末尾跳 6px）
        var sc = new ScaleTransform(1, 1);
        var tt = new TranslateTransform(0, 0);
        var tg = new TransformGroup();
        tg.Children.Add(sc);
        tg.Children.Add(tt);
        el.RenderTransformOrigin = new Point(0.5, 0.5); // 以卡片中心缩放（只影响缩放原点，不参与布局）
        el.RenderTransform = tg;
        sc.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(from, 1, d) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });
        sc.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(from, 1, d) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });
        tt.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(dy, 0, d) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });
    }

    /// <summary>
    /// 弹窗打开：遮罩淡入 + 卡片微缩放上浮。只做进入，关闭保持即时
    /// （退出要延迟 Close()，会引入按钮连点 / 重入问题，收益不抵）。
    /// <paramref name="mask"/> 传 null 表示遮罩是窗口自身的底色（那种走 <see cref="FadeInBrush"/>）。
    /// </summary>
    public static void PlayWindowIn(FrameworkElement? mask, FrameworkElement? card)
    {
        FadeIn(mask, Dialog);
        PopIn(card, 0.985, 6, Dialog);
    }

    /// <summary>
    /// 把「作为窗口底色的遮罩画刷」淡入（画刷级 Opacity 0→1）。
    /// 只允许传调用方自建的画刷（MessageDialog 那种 new 出来的），绝不传 Theme 里的共享画刷。
    /// </summary>
    public static void FadeInBrush(Brush? b, Duration? dur = null)
    {
        if (b == null || !Enabled) return;
        b.BeginAnimation(Brush.OpacityProperty,
            new DoubleAnimation(0, 1, dur ?? Dialog) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });
    }

    /// <summary>
    /// 显隐 + 进入淡入：只在真正从隐藏转显示时跑一次（给「会被频繁调用的刷新路径」用，
    /// 免得同一次状态刷新把动画重播一遍）。隐藏保持即时 Collapsed，不做退出动画。
    /// </summary>
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

    // ---------------- 滚动缓动 ----------------

    private sealed class ScrollAnim
    {
        public ScrollViewer Sv = null!;
        public double From, To, Last;
        public long T0;
        public int Ms;
        public DispatcherTimer Timer = null!;
    }

    private static ScrollAnim? _scroll;

    /// <summary>
    /// 把 <paramref name="sv"/> 的纵向偏移在 <paramref name="ms"/> 内缓动到 <paramref name="target"/>：
    /// 16ms 定时器只写 ScrollToVerticalOffset（Background 优先级，不触发布局外的额外工作）。
    /// 返回 false 表示「不适合动画，调用方直跳」：未启用动效 / 已到位之外的情况由调用方判断。
    /// 用户接管滚动（偏移偏离我们写入的值）或再次调用时，前一次动画立即中止。
    /// </summary>
    public static bool AnimateScroll(ScrollViewer? sv, double target, int ms = 140)
    {
        if (sv == null || !Enabled) return false;
        var from = sv.VerticalOffset;
        StopScroll();
        if (Math.Abs(target - from) < 1) return true; // 已在位：无需动画
        var a = new ScrollAnim
        {
            Sv = sv, From = from, To = target, Last = from,
            T0 = Environment.TickCount64, Ms = Math.Max(1, ms),
        };
        a.Timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(16) };
        a.Timer.Tick += (_, _) =>
        {
            if (!ReferenceEquals(_scroll, a)) return;
            // 用户接管（滚轮 / 拖条）：偏移不再等于我们上次写入的值，立刻让位
            if (Math.Abs(a.Sv.VerticalOffset - a.Last) > 0.5) { StopScroll(); return; }
            var p = Math.Clamp((Environment.TickCount64 - a.T0) / (double)a.Ms, 0, 1);
            var v = a.From + (a.To - a.From) * (1 - Math.Pow(1 - p, 3)); // ease-out cubic
            a.Sv.ScrollToVerticalOffset(v);
            a.Last = v;
            if (p >= 1) StopScroll();
        };
        _scroll = a;
        a.Timer.Start();
        return true;
    }

    /// <summary>中止进行中的滚动缓动（用户接管 / 视图重建时调）。</summary>
    public static void StopScroll()
    {
        if (_scroll == null) return;
        _scroll.Timer.Stop();
        _scroll = null;
    }
}
