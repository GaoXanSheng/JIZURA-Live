using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace JizuraLive.App.Controls;

/// <summary>
/// 切分信息 chip 的构件（对位旧 UI 的 <c>&lt;button class="chip"&gt;&lt;b&gt;标签&lt;/b&gt;值&lt;/button&gt;</c>）：
/// <see cref="Lead"/> 是 mut 色的小前缀（布局/入场/…），<see cref="Value"/> 是正文值。
/// 继承 Button 以保留点击/命令语义；视觉在 Theme.xaml 的 Chip* 样式里。
/// </summary>
public class JzChip : Button
{
    /// <summary>前缀为空时把前缀 TextBlock 收起来（视觉上等同只有值的 chip）。</summary>
    public static readonly IValueConverter LeadVisibility = new EmptyToCollapsed();

    public static readonly DependencyProperty LeadProperty =
        DependencyProperty.Register(nameof(Lead), typeof(string), typeof(JzChip), new PropertyMetadata(""));

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(string), typeof(JzChip), new PropertyMetadata(""));

    public string Lead
    {
        get => (string)GetValue(LeadProperty);
        set => SetValue(LeadProperty, value);
    }

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    private sealed class EmptyToCollapsed : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
