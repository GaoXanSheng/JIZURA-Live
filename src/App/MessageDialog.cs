using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using JizuraLive.App.I18n;

namespace JizuraLive.App;

/// <summary>
/// 暗色模态弹窗（对位旧 UI Dialogs.vue：rgba(6,6,8,.6) 全窗遮罩 + bg-panel 圆角 14 卡片）。
/// 用代码搭视觉树：内容是程序化拼的（标题 + 正文 + 可选要点列表），XAML 反而更啰嗦。
/// 静态工具类（非控件）：按钮默认文案在方法内经 L.T 解析，不订阅 LanguageChanged。
/// </summary>
public static class MessageDialog
{
    /// <summary>说明型弹窗（一个「关闭」）。</summary>
    public static void Info(Window owner, string title, string body, string? closeText = null) =>
        Show(owner, title, BuildBody(body), null, closeText ?? L.T("Dialog.Close"), primaryClose: true);

    /// <summary>警示型弹窗（一个「关闭」）。</summary>
    public static void Warn(Window owner, string title, string body) =>
        Show(owner, title, BuildBody(body), null, L.T("Dialog.Close"), primaryClose: true);

    /// <summary>确认型弹窗（取消 + 确认）。</summary>
    public static void Confirm(Window owner, string title, string body, string okText, Action onOk,
        UIElement? extra = null) =>
        Show(owner, title, BuildBody(body), extra, okText, onOk: onOk);

    /// <summary>清空/初始化确认（带要点列表，对位 Dialogs.vue 的 reset 弹窗）。</summary>
    public static void ConfirmReset(Window owner, Action onOk)
    {
        var body = new StackPanel();
        body.Children.Add(Text(L.T("Dialog.ResetIntro"), 13, null, wrap: true));
        var list = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        foreach (var item in new[]
                 {
                     L.T("Dialog.ResetItem1"), L.T("Dialog.ResetItem2"), L.T("Dialog.ResetItem3"),
                     L.T("Dialog.ResetItem4"), L.T("Dialog.ResetItem5"),
                 })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
            row.Children.Add(Text("•", 13, null));
            row.Children.Add(Text("　" + item, 13, null));
            list.Children.Add(row);
        }
        body.Children.Add(list);
        body.Children.Add(Text(L.T("Dialog.ResetKeep"), 12,
            (Brush)Application.Current.FindResource("Mut"), wrap: true));
        Show(owner, L.T("Dialog.ResetTitle"), body, null, L.T("Dialog.ResetTitle"), onOk: onOk, dangerOk: true);
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
        Action? onOk = null, bool primaryClose = false, bool dangerOk = false, string? cancelText = null) =>
        ShowCore(owner, title, body, extra, okText, onOk, primaryClose, dangerOk, cancelText ?? L.T("Dialog.Cancel"));

    private static void ShowCore(Window owner, string title, UIElement body, UIElement? extra, string okText,
        Action? onOk, bool primaryClose, bool dangerOk, string cancelText)
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
            // 必须分层：本窗按 owner 尺寸铺满，底色是半透明遮罩。非分层窗口的客户区被 DWM
            // 当作不透明面，遮罩会叠在窗口自身的黑底上 —— 主界面被整块涂黑而不是透出来变暗。
            AllowsTransparency = true,
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
            // 打开动效：只动卡片 —— 淡入 + 微缩放上浮（遮罩是窗口自身的底色、铺满 owner，不做动效）
            JzAnim.FadeIn(card, JzAnim.Dialog);
            JzAnim.PopIn(card, 0.985, 6);
        };
        dialog.ShowDialog();
    }
}
