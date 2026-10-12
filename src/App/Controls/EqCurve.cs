using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using JizuraLive.Engine;

namespace JizuraLive.App.Controls;

/// <summary>
/// EQ 七段曲线编辑器（原 AudioFxDialog code-behind 的 Canvas 手搓版重做为此控件）：
/// 折线 + 拉点，拖点改 ±12dB。dB 刻度与频点标签和曲线共用同一套坐标函数——旧实现里
/// 曲线按 452px 画布定位、频点标签走等分 Grid，两套坐标不严格对齐；现在宽度自适应、
/// 坐标只有一个来源。
///
/// 拖动中只重绘（显示层），松手才触发 <see cref="DragCommitted"/> 交外层提交——
/// 不在拖动过程中重建音频链（预览链重建是停声+重开设备的重量级操作）。
/// 字体/字号取元素上的 TextElement 属性（模板里设 MonoFont / 10px）。
/// </summary>
public sealed class EqCurve : FrameworkElement
{
    private const double CanvasH = 120;  // 曲线区高（对位旧画布 height=120）
    private const double Gutter = 30;    // 左侧 dB 刻度留白
    private const double RightPad = 12;  // 右端留白（对位旧公式 EqW-EqL-12）
    private const double LabelRow = 16;  // 底部频点标签行
    private const double DotR = 4;
    private const double VPad = 8;       // ±12dB 距画布上下边的距离（对位旧公式 EqH/2-8）

    public static readonly DependencyProperty BandsProperty = DependencyProperty.Register(
        nameof(Bands), typeof(double[]), typeof(EqCurve),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnBandsChanged));

    private static void OnBandsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // 外层回填了权威值：撤下拖动副本（拖动显示 → 提交回填之间的空窗由此保持连续，不闪回旧曲线）
        var c = (EqCurve)d;
        c._live = null;
        c.InvalidateVisual();
    }

    /// <summary>增益数组（dB，长度 = AudioFx.EqBands.Length）；外层回填新引用即重绘。</summary>
    public double[]? Bands
    {
        get => (double[])GetValue(BandsProperty);
        set => SetValue(BandsProperty, value);
    }

    /// <summary>松手落定：把新增益数组交给外层提交（拖动过程不触发）。</summary>
    public event Action<double[]>? DragCommitted;

    private int _drag = -1;
    private double[]? _live; // 拖动中的工作副本（覆盖 Gains 显示，提交后才由外层回填）

    // 主题画刷（懒解析；主题画刷是共享冻结实例，不释放）
    private Brush? _bg, _axis, _amber, _mut;

    private static int N => AudioFx.EqBands.Length;

    /// <summary>频点标签：60 / 150 / 400 / 1k / 2.4k / 6k / 15k（由引擎频点派生，不再单独维护一份）。</summary>
    private static string BandLabel(int i)
    {
        var f = AudioFx.EqBands[i];
        if (f < 1000) return f.ToString(CultureInfo.InvariantCulture);
        var k = f / 1000.0;
        return (k == Math.Floor(k) ? k.ToString("0", CultureInfo.InvariantCulture)
                                   : k.ToString("0.#", CultureInfo.InvariantCulture)) + "k";
    }

    private double X(int i, double w) => Gutter + i * Math.Max(0, w - Gutter - RightPad) / (N - 1);
    private static double Y(double db) => CanvasH / 2 - Math.Clamp(db, -12, 12) / 12 * (CanvasH / 2 - VPad);

    private int NearestBand(double x, double w)
    {
        var best = 0;
        var bd = double.MaxValue;
        for (var i = 0; i < N; i++)
        {
            var d = Math.Abs(X(i, w) - x);
            if (d >= bd) continue;
            bd = d;
            best = i;
        }
        return best;
    }

    private double[] Current() => _live ?? Bands ?? new double[N];

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, CanvasH + LabelRow);

    public EqCurve()
    {
        Cursor = Cursors.Cross;
        Focusable = false;
    }

    private Brush Res(string key, Color fallback)
    {
        var b = TryFindResource(key) as Brush;
        if (b != null) return b;
        var s = new SolidColorBrush(fallback);
        s.Freeze();
        return s;
    }

    private void Resolve()
    {
        _bg ??= Res("Ink", Color.FromRgb(0x11, 0x11, 0x14));
        _axis ??= Res("Line2", Color.FromArgb(46, 255, 255, 255));
        _amber ??= Res("Amber", Color.FromRgb(0xFF, 0xC1, 0x40));
        _mut ??= Res("Mut", Color.FromRgb(0x99, 0x99, 0x99));
    }

    protected override void OnRender(DrawingContext dc)
    {
        Resolve();
        var w = ActualWidth;
        var h = CanvasH + LabelRow;
        if (w < 40) return;
        dc.DrawRectangle(_bg, null, new Rect(0, 0, w, h));

        // 中轴线（1px 吸附半像素，避免 2px 灰边）
        var y0 = Math.Round(Y(0)) + 0.5;
        dc.DrawLine(new Pen(_axis, 1), new Point(0, y0), new Point(w, y0));

        var gains = Current();
        // 折线（控制点直连，对位旧 Polyline）
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(X(0, w), Y(gains[0])), false, false);
            for (var i = 1; i < N; i++) g.LineTo(new Point(X(i, w), Y(gains[i])), true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, new Pen(_amber, 2) { LineJoin = PenLineJoin.Round }, geo);
        // 拉点
        for (var i = 0; i < N; i++)
            dc.DrawEllipse(_amber, null, new Point(X(i, w), Y(gains[i])), DotR, DotR);

        // dB 刻度 + 频点标签（与曲线同一坐标）
        var family = TextElement.GetFontFamily(this);
        var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var size = TextElement.GetFontSize(this);
        if (double.IsNaN(size) || size < 1) size = 10;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        void Cap(string text, double x, double top)
            => dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, size, _mut, dpi), new Point(x, top));
        Cap("+12", 4, Y(12) - 7);
        Cap("0", 4, Y(0) - 7);
        Cap("-12", 4, Y(-12) - 4);
        for (var i = 0; i < N; i++)
        {
            var ft = new FormattedText(BandLabel(i), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, size, _mut, dpi);
            var x = Math.Clamp(X(i, w) - ft.Width / 2, 2, Math.Max(2, w - ft.Width - 2));
            dc.DrawText(ft, new Point(x, CanvasH + 2));
        }
    }

    private void ApplyY(double y)
    {
        if (_drag < 0 || _live == null) return;
        var db = Math.Clamp((CanvasH / 2 - y) / (CanvasH / 2 - VPad) * 12, -12, 12); // 对位旧 eqApply 映射
        _live[_drag] = Math.Round(db * 10) / 10;                                   // 对位 +db.toFixed(1)
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        CaptureMouse();
        _live = (double[])Current().Clone();
        _drag = NearestBand(e.GetPosition(this).X, ActualWidth);
        ApplyY(e.GetPosition(this).Y);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag >= 0) ApplyY(e.GetPosition(this).Y);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag < 0) return;
        _drag = -1;
        ReleaseMouseCapture();
        e.Handled = true;
        // 拖动副本继续显示，直到外层提交后回填 Gains（OnBandsChanged 撤下）——不闪回旧曲线
        if (DragCommitted != null) DragCommitted(_live!);
        else { _live = null; InvalidateVisual(); }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_drag < 0) return;
        // 未正常抬手（失焦/被抢捕获）：丢弃拖动副本，回到已提交值显示
        _drag = -1;
        _live = null;
        InvalidateVisual();
    }
}
