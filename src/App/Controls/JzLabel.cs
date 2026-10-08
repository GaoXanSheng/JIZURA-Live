using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace JizuraLive.App.Controls;

/// <summary>
/// 带字距的标签（对位旧 UI 的 <c>tracking-[0.18em]</c> 小标题）：WPF 的 TextBlock 没有 letter-spacing，
/// 这里按字拆成 TextBlock 逐个加右侧边距。字号/前景/字重仍走 TextElement 继承（调用方照常设 FontSize 等）。
/// </summary>
public class JzLabel : StackPanel
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(JzLabel),
            new PropertyMetadata("", OnRebuild));

    /// <summary>字距，单位 em（0.18 = CSS 的 tracking-[0.18em]）。</summary>
    public static readonly DependencyProperty TrackingProperty =
        DependencyProperty.Register(nameof(Tracking), typeof(double), typeof(JzLabel),
            new PropertyMetadata(0.0, OnRebuild));

    private DependencyPropertyDescriptor? _fontSizeHook;
    private EventHandler? _fontSizeHandler;
    public static readonly DependencyProperty ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner(
            typeof(JzLabel),
            new FrameworkPropertyMetadata(
                Brushes.Black,
                FrameworkPropertyMetadataOptions.Inherits,
                OnRebuild));

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }
    public JzLabel()
    {
        Orientation = Orientation.Horizontal;
        Initialized += (_, _) =>
        {
            // 字号是 TextElement 的附加属性（不是本类的 DP），只能挂描述符才能在变化时重排
            _fontSizeHook = DependencyPropertyDescriptor.FromProperty(TextElement.FontSizeProperty, typeof(JzLabel));
            _fontSizeHandler = (_, _) => Rebuild();
            _fontSizeHook.AddValueChanged(this, _fontSizeHandler);
        };
        Unloaded += (_, _) =>
        {
            if (_fontSizeHook != null && _fontSizeHandler != null)
                _fontSizeHook.RemoveValueChanged(this, _fontSizeHandler);
            _fontSizeHook = null;
            _fontSizeHandler = null;
        };
        Rebuild();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double Tracking
    {
        get => (double)GetValue(TrackingProperty);
        set => SetValue(TrackingProperty, value);
    }

    private static void OnRebuild(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((JzLabel)d).Rebuild();

    private void Rebuild()
    {
        Children.Clear();
        var s = Text;
        if (string.IsNullOrEmpty(s)) return;
        var size = TextElement.GetFontSize(this);
        if (double.IsNaN(size) || size <= 0) size = 13;
        // TextBlock 的隐式样式会压过 TextElement 继承：把继承来的值显式落到每个字上
        var weight = TextElement.GetFontWeight(this);
        var brush = TextElement.GetForeground(this);
        var family = TextElement.GetFontFamily(this);
        var gap = Tracking > 0 ? Tracking * size : 0;

        TextBlock Make(string text, double right) => new()
        {
            Text = text,
            FontSize = size,
            FontWeight = weight,
            Foreground = brush,
            FontFamily = family,
            LineHeight = size * 1.5,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Margin = new Thickness(0, 0, right, 0),
        };

        if (gap <= 0)
        {
            Children.Add(Make(s, 0));
            return;
        }

        for (var i = 0; i < s.Length; i++)
            Children.Add(Make(s[i].ToString(), i == s.Length - 1 ? 0 : gap));
    }
}
