using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace JizuraLive.App;

/// <summary>
/// 暗色模态弹窗（对位旧 UI Dialogs.vue：rgba(6,6,8,.6) 全窗遮罩 + bg-panel 圆角 14 卡片）。
/// 用代码搭视觉树：内容是程序化拼的（标题 + 正文 + 可选要点列表），XAML 反而更啰嗦。
/// </summary>
public static class MessageDialog
{
    /// <summary>说明型弹窗（一个「关闭」）。</summary>
    public static void Info(Window owner, string title, string body, string closeText = "关闭") =>
        Show(owner, title, BuildBody(body), null, closeText, primaryClose: true);

    /// <summary>警示型弹窗（一个「关闭」）。</summary>
    public static void Warn(Window owner, string title, string body) =>
        Show(owner, title, BuildBody(body), null, "关闭", primaryClose: true);

    /// <summary>确认型弹窗（取消 + 确认）。</summary>
    public static void Confirm(Window owner, string title, string body, string okText, Action onOk,
        UIElement? extra = null) =>
        Show(owner, title, BuildBody(body), extra, okText, onOk: onOk);

    /// <summary>清空/初始化确认（带要点列表，对位 Dialogs.vue 的 reset 弹窗）。</summary>
    public static void ConfirmReset(Window owner, Action onOk)
    {
        var body = new StackPanel();
        body.Children.Add(Text("将以下内容全部清空，回到最初状态。此操作无法撤销。", 13, null, wrap: true));
        var list = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        foreach (var item in new[]
                 {
                     "歌词・曲名・歌手", "载入的歌曲", "时间・逐行指定・导出范围",
                     "风格・演出・配色・字体等设置", "方案历史（◀ ▶）与撤销历史",
                 })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
            row.Children.Add(Text("•", 13, null));
            row.Children.Add(Text("　" + item, 13, null));
            list.Children.Add(row);
        }
        body.Children.Add(list);
        body.Children.Add(Text("预览音量会保留。已保存的项目文件不受影响。", 12,
            (Brush)Application.Current.FindResource("Mut"), wrap: true));
        Show(owner, "初始化", body, null, "初始化", onOk: onOk, dangerOk: true);
    }

    private static UIElement BuildBody(string text) => Text(text, 13, null, wrap: true);

    private static TextBlock Text(string s, double size, Brush? brush, bool wrap = false) => new()
    {
        Text = s,
        FontSize = size,
        Foreground = brush ?? (Brush)Application.Current.FindResource("Fg"),
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        LineHeight = size * 1.5,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
    };

    private static void Show(Window owner, string title, UIElement body, UIElement? extra, string okText,
        Action? onOk = null, bool primaryClose = false, bool dangerOk = false, string cancelText = "取消")
    {
        var res = Application.Current;
        Brush panel = (Brush)res.FindResource("Panel");
        Brush line2 = (Brush)res.FindResource("Line2");
        Brush line = (Brush)res.FindResource("LineBrush");
        Brush mut = (Brush)res.FindResource("Mut");

        var card = new Border
        {
            Background = panel,
            BorderBrush = line2,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(24, 24, 24, 16),
            MaxWidth = 560,
            Margin = new Thickness(24),
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 60, ShadowDepth = 6, Opacity = 0.6 },
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)res.FindResource("Fg"),
            Margin = new Thickness(0, 0, 0, 14),
        });
        stack.Children.Add(body);
        if (extra != null)
        {
            if (extra is FrameworkElement fe) fe.Margin = new Thickness(0, 12, 0, 0);
            stack.Children.Add(extra);
        }

        var footer = new Border
        {
            BorderBrush = line,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 12, 0, 0),
            Margin = new Thickness(0, 14, 0, 0),
        };
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var dialog = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Background = new SolidColorBrush(Color.FromArgb(0x99, 6, 6, 8)),
            FontFamily = (FontFamily)res.FindResource("UiFont"),
            Content = new Grid { Children = { card } },
        };
        card.Child = stack;

        void Close() => dialog.Close();

        if (!primaryClose)
        {
            var cancel = new Button { Content = cancelText, Margin = new Thickness(8, 0, 0, 0) };
            cancel.Style = (Style)res.FindResource("BtnGhost");
            cancel.Click += (_, _) => Close();
            btnRow.Children.Add(cancel);
        }

        var ok = new Button { Content = okText, Margin = new Thickness(0, 0, 0, 0) };
        ok.Style = (Style)res.FindResource(primaryClose ? "BtnPrimary" : dangerOk ? "BtnDanger" : "BtnPrimary");
        ok.MinWidth = 76;
        ok.Click += (_, _) =>
        {
            Close();
            onOk?.Invoke();
        };
        btnRow.Children.Add(ok);
        footer.Child = btnRow;
        stack.Children.Add(footer);

        // 遮罩铺满 owner（对位 fixed inset-0）
        dialog.Loaded += (_, _) =>
        {
            dialog.Left = owner.Left;
            dialog.Top = owner.Top;
            dialog.Width = owner.ActualWidth;
            dialog.Height = owner.ActualHeight;
        };
        dialog.ShowDialog();
    }
}
