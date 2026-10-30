using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using JizuraLive.App.Cloud;
using JizuraLive.App.I18n;
using JizuraLive.Logging;

namespace JizuraLive.App.UI;

public sealed class CloudLoginDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly string _source;
    private readonly Image _qr;
    private readonly TextBlock _status;
    private readonly Wpf.Ui.Controls.Button _refresh;
    private readonly DispatcherTimer _poll;
    private string _key = "";
    private bool _checking;
    private bool _closed;

    private string ScanHint => _source == CloudMusicService.Netease
        ? L.T("CloudMusic.LoginScanHint")
        : L.T("CloudMusic.LoginScanHintKugou");

    public CloudLoginDialog(string source)
    {
        _source = source;
        Title = L.T("CloudMusic.LoginTitle");
        Width = 340;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowCornerPreference = Wpf.Ui.Controls.WindowCornerPreference.Round;
        FontFamily = new FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI");
        UseLayoutRounding = true;

        _qr = new Image
        {
            Width = 220,
            Height = 220,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 14, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        _status = new TextBlock
        {
            Text = L.T("CloudMusic.LoginLoading"),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0),
            FontSize = 12,
        };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        _refresh = new Wpf.Ui.Controls.Button
        {
            Content = L.T("CloudMusic.LoginRefresh"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 14, 0, 0),
        };
        _refresh.Click += async (_, _) => await RefreshAsync();

        var close = new Wpf.Ui.Controls.Button
        {
            Content = L.T("Dialog.Cancel"),
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 8, 0, 0),
        };
        close.Click += (_, _) => Close();

        var hint = new TextBlock
        {
            Text = L.T("CloudMusic.LoginHint"),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            FontSize = 12,
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(hint);
        panel.Children.Add(_qr);
        panel.Children.Add(_status);
        panel.Children.Add(_refresh);
        panel.Children.Add(close);
        Content = panel;

        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _poll.Tick += async (_, _) => await CheckAsync();
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) =>
        {
            _closed = true;
            _poll.Stop();
        };
    }

    private async Task RefreshAsync()
    {
        _poll.Stop();
        _key = "";
        _qr.Source = null;
        _status.Text = L.T("CloudMusic.LoginLoading");
        _refresh.IsEnabled = false;
        try
        {
            var qr = await CloudMusicService.QrAsync(_source);
            if (_closed) return;
            if (!qr.Ok)
            {
                _status.Text = L.T("CloudMusic.LoginFailed") + qr.Error;
                return;
            }

            _key = qr.Key;
            _qr.Source = DecodeImage(qr.Img);
            _status.Text = ScanHint;
            _poll.Start();
        }
        catch (Exception ex)
        {
            _status.Text = L.T("CloudMusic.LoginFailed") + ex.Message;
            Log.Warn("[Cloud] 二维码获取失败：" + ex);
        }
        finally
        {
            _refresh.IsEnabled = true;
        }
    }

    private async Task CheckAsync()
    {
        if (_checking || _key.Length == 0) return;
        _checking = true;
        try
        {
            var result = await CloudMusicService.QrCheckAsync(_source, _key);
            if (_closed) return;
            if (!result.Ok)
            {
                _poll.Stop();
                _status.Text = L.T("CloudMusic.LoginFailed") + result.Error;
                return;
            }

            if (_source == CloudMusicService.Netease)
            {
                switch (result.Status)
                {
                    case "800":
                        _poll.Stop();
                        _status.Text = L.T("CloudMusic.LoginExpired");
                        break;
                    case "802":
                        _status.Text = L.T("CloudMusic.LoginConfirm");
                        break;
                    case "803":
                        _poll.Stop();
                        DialogResult = true;
                        break;
                    default:
                        _status.Text = ScanHint;
                        break;
                }
            }
            else
            {
                switch (result.Status)
                {
                    case "3":
                        _poll.Stop();
                        _status.Text = L.T("CloudMusic.LoginExpired");
                        break;
                    case "2":
                        _status.Text = L.T("CloudMusic.LoginConfirm");
                        break;
                    case "4":
                        _poll.Stop();
                        DialogResult = true;
                        break;
                    default:
                        _status.Text = ScanHint;
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("[Cloud] 扫码状态检查失败：" + ex);
            _status.Text = L.T("CloudMusic.LoginFailed") + ex.Message;
        }
        finally
        {
            _checking = false;
        }
    }

    private static BitmapImage? DecodeImage(string data)
    {
        try
        {
            if (data.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return new BitmapImage(new Uri(data));

            int comma = data.IndexOf(',');
            var payload = data.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0
                ? data[(comma + 1)..]
                : data;
            if (payload.Length == 0) return null;

            var bytes = Convert.FromBase64String(payload);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = new MemoryStream(bytes);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            Log.Warn("[Cloud] 二维码解析失败：" + ex.Message);
            return null;
        }
    }
}
