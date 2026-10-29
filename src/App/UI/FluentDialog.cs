using System.Windows;
using System.Windows.Controls;
using JizuraLive.App.I18n;
using Wpf.Ui.Controls;

namespace JizuraLive.App.UI;

public static class FluentDialog
{
    public static bool Confirm(Window owner, string title, string message, bool danger = false) =>
        Show(owner, title, message, L.T("Dialog.Ok"), L.T("Dialog.Cancel"), danger) == true;

    public static void Info(Window owner, string title, string message) =>
        Show(owner, title, message, null, L.T("Dialog.Ok"), false);

    private static bool? Show(Window owner, string title, string message, string? primaryText, string closeText,
        bool danger)
    {
        var text = new System.Windows.Controls.TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
        };
        text.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");

        var scroll = new ScrollViewer
        {
            Content = text,
            MaxHeight = 440,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(scroll);
        panel.Children.Add(buttons);

        var dialog = new FluentWindow
        {
            Owner = owner,
            Title = title,
            Width = 480,
            MinWidth = 0,
            MinHeight = 0,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowCornerPreference = WindowCornerPreference.Round,
            Content = panel,
            FontFamily = owner.FontFamily,
        };

        if (primaryText is not null)
        {
            var primary = new Wpf.Ui.Controls.Button
            {
                Content = primaryText,
                Appearance = danger ? ControlAppearance.Danger : ControlAppearance.Primary,
                MinWidth = 88,
                IsDefault = true,
            };
            primary.Click += (_, _) => dialog.DialogResult = true;
            buttons.Children.Add(primary);
        }

        var close = new Wpf.Ui.Controls.Button
        {
            Content = closeText,
            MinWidth = 88,
            IsCancel = true,
            IsDefault = primaryText is null,
            Margin = new Thickness(8, 0, 0, 0),
        };
        close.Click += (_, _) => dialog.DialogResult = false;
        buttons.Children.Add(close);

        return dialog.ShowDialog();
    }
}
