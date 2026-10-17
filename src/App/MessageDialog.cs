using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using JizuraLive.App.I18n;

namespace JizuraLive.App;

public static class MessageDialog
{
    private const string TextPrimaryKey = "TextFillColorPrimaryBrush";
    private const string TextSecondaryKey = "TextFillColorSecondaryBrush";
    private const string DialogFillKey = "SolidBackgroundFillColorSecondaryBrush";
    private const string DialogStrokeKey = "CardStrokeColorDefaultBrush";
    private const string DividerKey = "DividerStrokeColorDefaultBrush";

    private static readonly FontFamily UiFont = new("Microsoft YaHei UI, Microsoft YaHei, Segoe UI");

    private static Brush Theme(string key) => (Brush)Application.Current.FindResource(key);

    public static void Info(Window owner, string title, string body, string? closeText = null) =>
        Show(owner, title, BuildBody(body), null, closeText ?? L.T("Dialog.Close"), primaryClose: true);

    public static void Info(Window owner, string title, UIElement body, string? closeText = null) =>
        Show(owner, title, body, null, closeText ?? L.T("Dialog.Close"), primaryClose: true);

    public static void Warn(Window owner, string title, string body) =>
        Show(owner, title, BuildBody(body), null, L.T("Dialog.Close"), primaryClose: true);

    public static void Confirm(Window owner, string title, string body, string okText, Action onOk,
        UIElement? extra = null) =>
        Show(owner, title, BuildBody(body), extra, okText, onOk: onOk);

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
        body.Children.Add(Text(L.T("Dialog.ResetKeep"), 12, Theme(TextSecondaryKey), wrap: true));
        Show(owner, L.T("Dialog.ResetTitle"), body, null, L.T("Dialog.ResetTitle"), onOk: onOk, dangerOk: true);
    }

    private static UIElement BuildBody(string text) => Text(text, 13, null, wrap: true);

    private static TextBlock Text(string s, double size, Brush? brush, bool wrap = false) => new()
    {
        Text = s,
        FontSize = size,
        Foreground = brush ?? Theme(TextPrimaryKey),
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
        Brush panel = Theme(DialogFillKey);
        Brush line2 = Theme(DialogStrokeKey);
        Brush line = Theme(DividerKey);

        var shadowLayer = new Border
        {
            Background = panel,
            BorderBrush = line2,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 60, ShadowDepth = 6, Opacity = 0.6 },
        };
        var card = new Border
        {
            Background = panel,
            BorderBrush = line2,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(24, 24, 24, 16),
            MaxWidth = 560,
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme(TextPrimaryKey),
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
            AllowsTransparency = true,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Background = new SolidColorBrush(Color.FromArgb(0x99, 6, 6, 8)),
            FontFamily = UiFont,
            Content = new Grid
            {
                Margin = new Thickness(24),
                Children =
                {
                    new Grid
                    {
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Children = { shadowLayer, card },
                    },
                },
            },
        };
        card.Child = stack;
        TextOptions.SetTextFormattingMode(dialog, TextFormattingMode.Display);

        void Close() => dialog.Close();

        if (!primaryClose)
        {
            var cancel = new Wpf.Ui.Controls.Button
            {
                Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
                Content = cancelText,
                Margin = new Thickness(8, 0, 0, 0),
            };
            cancel.Click += (_, _) => Close();
            btnRow.Children.Add(cancel);
        }

        var ok = new Wpf.Ui.Controls.Button
        {
            Appearance = dangerOk ? Wpf.Ui.Controls.ControlAppearance.Danger
                : primaryClose ? Wpf.Ui.Controls.ControlAppearance.Secondary
                : Wpf.Ui.Controls.ControlAppearance.Primary,
            Content = okText,
            MinWidth = 76,
        };
        ok.Click += (_, _) =>
        {
            Close();
            onOk?.Invoke();
        };
        btnRow.Children.Add(ok);
        footer.Child = btnRow;
        stack.Children.Add(footer);

        WindowFit.TryCover(dialog, owner);
        dialog.Loaded += (_, _) =>
        {
            WindowFit.TryCover(dialog, owner);
            JzAnim.FadeIn(card, JzAnim.Dialog);
            JzAnim.PopIn(card, 0.985, 6);
        };
        dialog.ShowDialog();
    }
}
