using System.Windows;
using System.Windows.Controls;

namespace JizuraLive.App.Controls;

/// <summary>
/// 套装徽标（对位旧 UI 的 <c>&lt;i class="badge-set"&gt;文&lt;/i&gt;</c>）：
/// 10px 字 + 1px 描边 + 3px 圆角。描边/字的颜色由样式（Badge / BadgeEx / …）决定。
/// </summary>
public class JzBadge : Control
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(JzBadge), new PropertyMetadata(""));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
}
