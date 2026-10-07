using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using JizuraLive.App.Cloud;
using JizuraLive.Logging;
using JizuraLive.Server;
using JizuraLive.Server.Models;

namespace JizuraLive.App.Views;

// 云音乐面板（对位 frontend/src/components/cloud/CloudMusicModal.vue + SearchPane/SongList/AccountPane）：
// 平台切换（酷狗/网易云）→ 搜索 → 歌曲列表 → 装载（下载音频+歌词+写工程+关窗）；
// 扫码登录（按需弹出）；SMTC 跟随开关。网络全部后台 Task，UI 经 Dispatcher 回组，不卡主窗口。
public partial class CloudPanel : Window
{
    /// <summary>列表行 VM。</summary>
    public sealed class CloudRow : INotifyPropertyChanged
    {
        public KgSong Song { get; }
        public string Name => Song.Name;
        public string SingerLine =>
            Song.Singer + (Song.Album.Length > 0 ? " · " + Song.Album : "") + " · " + FmtDur(Song.Duration);
        public Visibility VipVisibility => Song.Pay != 0 ? Visibility.Visible : Visibility.Collapsed;
        private string _status = "";

        public string Status
        {
            get => _status;
            set { _status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status))); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public CloudRow(KgSong song) => Song = song;

        internal static string FmtDur(long sec)
        {
            sec = Math.Max(0, sec);
            return $"{sec / 60}:{sec % 60:D2}";
        }
    }

    private string _source = "kugou";
    private bool _searching;
    private bool _loading;
    private bool _syncingFollow;
    private bool _closed;
    private DispatcherTimer? _qrTimer;
    private string _qrKey = "";
    private bool _qrBusy;

    private static CloudPanel? _open;

    private EditorState State => App.MainInstance!.State;
    private FollowController Follow => App.MainInstance!.Follow;

    public CloudPanel()
    {
        InitializeComponent();
        CmbQuality.ItemsSource = new[] { "128k", "320k", "无损" };
        CmbQuality.SelectedIndex = 0;
        Loaded += (_, _) =>
        {
            SyncSourceButtons();
            RefreshLogin();
            _syncingFollow = true;
            ChkFollow.IsChecked = Follow.On;
            _syncingFollow = false;
            Follow.Changed += RefreshFollow;
            RefreshFollow();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            StopQrPolling();
            if (App.MainInstance != null) Follow.Changed -= RefreshFollow;
            if (ReferenceEquals(_open, this)) _open = null;
        };
    }

    /// <summary>打开面板（单例：已开则激活）。</summary>
    public static void Open(Window owner)
    {
        if (_open != null)
        {
            _open.Activate();
            return;
        }

        var w = new CloudPanel { Owner = owner };
        _open = w;
        w.Show();
    }

    // ---------------- 平台 / 登录 ----------------

    private void SyncSourceButtons()
    {
        var on = (System.Windows.Media.SolidColorBrush)FindResource("Raised2");
        var fg = (System.Windows.Media.SolidColorBrush)FindResource("Fg");
        var mut = (System.Windows.Media.SolidColorBrush)FindResource("Mut");
        var off = System.Windows.Media.Brushes.Transparent;
        BtnKugou.Background = _source == "kugou" ? on : off;
        BtnKugou.Foreground = _source == "kugou" ? fg : mut;
        BtnNetease.Background = _source == "netease" ? on : off;
        BtnNetease.Foreground = _source == "netease" ? fg : mut;
    }

    private void BtnSource_Click(object sender, RoutedEventArgs e)
    {
        var next = ReferenceEquals(sender, BtnNetease) ? "netease" : "kugou";
        if (next == _source) return;
        _source = next;
        SyncSourceButtons();
        StopQrPolling();
        QrPanel.Visibility = Visibility.Collapsed;
        RefreshLogin();
    }

    private void RefreshLogin()
    {
        var st = _source == "netease" ? ServerContext.Netease.LoginState() : ServerContext.KuGou.LoginState();
        TblLogin.Text = st.Logged ? $"已登录：{st.Nickname}" : "未登录";
        BtnLogin.Visibility = st.Logged ? Visibility.Collapsed : Visibility.Visible;
        BtnLogout.Visibility = st.Logged ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnLogout_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_source == "netease") ServerContext.Netease.Logout();
            else ServerContext.KuGou.Logout();
        }
        catch (Exception ex)
        {
            Log.Warn("[Cloud] 退出登录失败：" + ex.Message);
        }

        RefreshLogin();
    }

    // ---------------- 搜索 ----------------

    private string Quality =>
        CmbQuality.SelectedIndex switch { 1 => "320", 2 => "flac", _ => "128" };

    private void TbQuery_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _ = DoSearchAsync();
            e.Handled = true;
        }
    }

    private void BtnSearch_Click(object sender, RoutedEventArgs e) => _ = DoSearchAsync();

    private async Task DoSearchAsync()
    {
        var kw = TbQuery.Text.Trim();
        if (kw.Length == 0) return;
        if (_searching) return;
        _searching = true;
        BtnSearch.IsEnabled = false;
        BtnSearch.Content = "搜索中…";
        TblSearchInfo.Text = "搜索中…";
        try
        {
            var r = _source == "netease"
                ? await ServerContext.Netease.SearchAsync(kw, 1)
                : await ServerContext.KuGou.SearchAsync(kw, 1);
            if (_closed) return;
            if (!r.Ok)
                throw new SongLoader.CloudException(r.Error ?? "搜索失败");
            Lst.ItemsSource = r.List.Select(s => new CloudRow(s)).ToList();
            TblSearchInfo.Text = $"共 {r.Total} 首" + (r.List.Count == 0 ? "（没有结果）" : "");
            if (r.List.Count == 0) Lst.ItemsSource = null;
        }
        catch (Exception ex)
        {
            if (_closed) return;
            Lst.ItemsSource = null;
            TblSearchInfo.Text = "";
            var msg = ex.Message;
            MessageBox.Show(this, "搜索失败：" + msg, "云音乐", MessageBoxButton.OK, MessageBoxImage.Warning);
            Log.Info("[Cloud] 搜索失败：" + msg);
        }
        finally
        {
            _searching = false;
            if (!_closed)
            {
                BtnSearch.IsEnabled = true;
                BtnSearch.Content = "搜索";
            }
        }
    }

    // ---------------- 装载 ----------------

    private async void BtnLoad_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is not Button { Tag: CloudRow row }) return;
        _loading = true;
        Lst.IsEnabled = false;
        BtnSearch.IsEnabled = false;
        var song = row.Song;
        row.Status = "获取链接…";
        try
        {
            var res = await SongLoader.LoadAsync(State, _source, song, Quality, s => row.Status = s);
            row.Status = "已装载";
            Log.Info($"[Cloud] 云音乐装载完成：{song.Name}（{res.FileName}{res.LyricNote}）"
                     + (res.Trial ? "【仅试听片段：这首需要 VIP，可扫码登录后再试】" : ""));
            Close(); // 装载成功即关窗（对位旧版「点歌名载入」的闭环）
        }
        catch (Exception ex)
        {
            row.Status = "失败";
            Log.Error("[Cloud] 装载失败：" + ex.Message, ex);
            if (!_closed)
                MessageBox.Show(this, $"装载失败：{song.Name}\n{ex.Message}", "云音乐",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _loading = false;
            if (!_closed)
            {
                Lst.IsEnabled = true;
                BtnSearch.IsEnabled = true;
            }
        }
    }

    // ---------------- 扫码登录 ----------------

    private void BtnLogin_Click(object sender, RoutedEventArgs e) => _ = OpenQrAsync();

    private void BtnQrRefresh_Click(object sender, RoutedEventArgs e) => _ = OpenQrAsync();

    private void BtnQrClose_Click(object sender, RoutedEventArgs e)
    {
        StopQrPolling();
        QrPanel.Visibility = Visibility.Collapsed;
    }

    private async Task OpenQrAsync()
    {
        StopQrPolling();
        QrPanel.Visibility = Visibility.Visible;
        QrImg.Source = null;
        TblQrTip.Text = "正在获取二维码…";
        try
        {
            var q = _source == "netease"
                ? await ServerContext.Netease.QrAsync()
                : await ServerContext.KuGou.QrAsync();
            if (!q.Ok || string.IsNullOrEmpty(q.Key))
                throw new SongLoader.CloudException(q.Error ?? "二维码生成失败");
            _qrKey = q.Key;
            QrImg.Source = await DecodeQrImageAsync(q.Img);
            TblQrTip.Text = "等待扫码…";
            StartQrPolling();
        }
        catch (Exception ex)
        {
            TblQrTip.Text = "失败：" + ex.Message;
        }
    }

    /// <summary>二维码图：网易云给 data:image/png;base64；酷狗给 base64 图（个别实现带 data: 前缀）。</summary>
    private static async Task<BitmapImage?> DecodeQrImageAsync(string? img)
    {
        if (string.IsNullOrEmpty(img)) return null;
        byte[] bytes;
        if (img.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            img.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            bytes = await http.GetByteArrayAsync(img);
        }
        else
        {
            var b64 = img.StartsWith("data:", StringComparison.Ordinal)
                ? img[(img.IndexOf(',', StringComparison.Ordinal) + 1)..]
                : img;
            bytes = Convert.FromBase64String(b64);
        }

        var bmp = new BitmapImage();
        using var ms = new MemoryStream(bytes);
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private void StartQrPolling()
    {
        StopQrPolling();
        _qrTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _qrTimer.Tick += async (_, _) =>
        {
            // 防重入：上一轮还没回来就跳过
            if (_qrBusy || _qrKey.Length == 0) return;
            _qrBusy = true;
            try
            {
                var r = _source == "netease"
                    ? await ServerContext.Netease.QrCheckAsync(_qrKey)
                    : await ServerContext.KuGou.QrCheckAsync(_qrKey);
                if (_closed) return;
                if (!r.Ok)
                {
                    TblQrTip.Text = r.Error ?? "查询失败";
                    return;
                }

                var st = r.Status;
                // 酷狗状态码：1 等待 / 2 已扫码 / 0 过期 / 4 成功；网易云：801 / 802 / 800 / 803
                switch (st)
                {
                    case "1" or "801":
                        TblQrTip.Text = "等待扫码…";
                        break;
                    case "2" or "802":
                        TblQrTip.Text = "已扫码，请在手机上确认";
                        break;
                    case "0" or "800":
                        TblQrTip.Text = "二维码已过期，请点「刷新二维码」";
                        StopQrPolling();
                        break;
                    case "4" or "803":
                        StopQrPolling();
                        QrPanel.Visibility = Visibility.Collapsed;
                        RefreshLogin();
                        Log.Info($"[Cloud] 扫码登录成功（{_source}）");
                        break;
                    default:
                        TblQrTip.Text = "状态未知，请刷新二维码";
                        break;
                }
            }
            catch
            {
                // 网络抖动，等下一轮
            }
            finally
            {
                _qrBusy = false;
            }
        };
        _qrTimer.Start();
    }

    private void StopQrPolling()
    {
        _qrTimer?.Stop();
        _qrTimer = null;
        _qrKey = "";
    }

    private void CmbQuality_Changed(object sender, SelectionChangedEventArgs e)
    {
    }

    // ---------------- SMTC 跟随 ----------------

    private void ChkFollow_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingFollow || !IsLoaded) return; // XAML 初始化期
        var want = ChkFollow.IsChecked == true;
        if (want != Follow.On) Follow.Toggle();
        RefreshFollow();
    }

    private void BtnNudgeM_Click(object sender, RoutedEventArgs e) => Follow.Nudge(-0.5);

    private void BtnNudgeP_Click(object sender, RoutedEventArgs e) => Follow.Nudge(0.5);

    private void BtnRematch_Click(object sender, RoutedEventArgs e) => Follow.Rematch();

    private void RefreshFollow()
    {
        if (_closed || App.MainInstance == null) return;
        var f = Follow;
        _syncingFollow = true;
        ChkFollow.IsChecked = f.On;
        _syncingFollow = false;
        TblOffset.Text = f.Offset == 0 ? "" : $"偏移 {f.Offset:+0.0;-0.0}s";

        var has = f.On && f.Status != FollowController.StOff;
        FollowDetail.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        TblFollowDetail.Visibility = f.On && f.Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!has)
        {
            TblFollowStatus.Text = "";
            TblFollowNow.Text = "";
            TblFollowDetail.Text = "";
            return;
        }

        // 文案/配色对位旧版 FollowPane 的 STATUS 映射
        var (text, color) = f.Status switch
        {
            FollowController.StNoSession => ("未检测到播放器", "Amber"),
            FollowController.StNoMeta => ("播放器未提供歌曲信息", "Amber"),
            FollowController.StMatching => ("正在匹配歌词…", "Mut"),
            FollowController.StOk => ("已同步", "Cyan"),
            FollowController.StNoMatch => ("没匹配到歌词", "Amber"),
            FollowController.StError => ("出错", "Danger"),
            _ => ("未开启", "Mut"),
        };
        TblFollowStatus.Text = text + (f.Fmt.Length > 0
            ? " · " + (f.Fmt == "krc" ? "KRC 逐字" : f.Fmt == "ne" ? "网易 LRC" : "LRC")
            : "");
        TblFollowStatus.Foreground = (System.Windows.Media.SolidColorBrush)FindResource(color);
        var now = (f.AppName.Length > 0 ? f.AppName + " ｜ " : "") + f.Title +
                  (f.Artist.Length > 0 ? " - " + f.Artist : "");
        TblFollowNow.Text = (now + (f.MatchedAs.Length > 0 ? "　→ 已匹配 " + f.MatchedAs : "")).Trim('　');
        TblFollowDetail.Text = f.Detail +
                               (f.Estimated ? "（此播放器不上报进度：按「切歌/恢复播放=从头」估算）" : "");
    }
}
