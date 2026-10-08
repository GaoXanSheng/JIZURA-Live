using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

    /// <summary>跟随页「跟随的播放器」下拉项（AccountPane/FollowPane 的 session 选择）。</summary>
    private sealed record SessionItem(string AppId, string Label);

    private string _source = "kugou";
    private string _pane = "search";
    private bool _searching;
    private bool _loading;
    private bool _syncingFollow;
    private bool _closed;
    private DispatcherTimer? _qrTimer;
    private string _qrKey = "";
    private bool _qrBusy;
    private readonly List<string> _sessionSig = [];

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
            FitToOwner();
            ShowPane(_pane);
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

    // ---------------- 弹窗外壳（遮罩铺满 owner + 居中卡片，对位 MessageDialog / CloudMusicModal） ----------------

    /// <summary>遮罩窗口铺满 owner；卡片按旧版 w-[min(860px,96vw)] h-[min(600px,88vh)] 定尺寸。</summary>
    private void FitToOwner()
    {
        if (Owner == null || Owner.ActualWidth < 200 || Owner.ActualHeight < 200)
        {
            Width = 860;
            Height = 600;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Card.Width = 860;
            Card.Height = 600;
            return;
        }

        Left = Owner.Left;
        Top = Owner.Top;
        Width = Owner.ActualWidth;
        Height = Owner.ActualHeight;
        Card.Width = Math.Min(860, Width * 0.96);
        Card.Height = Math.Min(600, Height * 0.88);
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>点遮罩空白处关窗（对位 @click.self="cloud.open = false"）。</summary>
    private void Mask_Click(object sender, MouseButtonEventArgs e) => Close();

    /// <summary>扫码层的空白处关闭二维码（对位 @click.self="cloud.closeQr()"）。</summary>
    private void QrMask_Click(object sender, MouseButtonEventArgs e) => CloseQr();

    /// <summary>Esc 关窗；扫码层开着时不关（同旧版 onKey 的 !cloud.qr.active 判断）。</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || QrPanel.Visibility == Visibility.Visible) return;
        Close();
        e.Handled = true;
    }

    // ---------------- 页签（搜索 / 我的歌单 / 跟随播放器 / 账号） ----------------

    private void ShowPane(string pane)
    {
        _pane = pane;
        PaneSearch.Visibility = pane == "search" ? Visibility.Visible : Visibility.Collapsed;
        PanePlaylists.Visibility = pane == "playlists" ? Visibility.Visible : Visibility.Collapsed;
        PaneFollow.Visibility = pane == "follow" ? Visibility.Visible : Visibility.Collapsed;
        PaneAccount.Visibility = pane == "account" ? Visibility.Visible : Visibility.Collapsed;
        NavSearch.Tag = pane == "search" ? "on" : null;
        NavPlaylists.Tag = pane == "playlists" ? "on" : null;
        NavFollow.Tag = pane == "follow" ? "on" : null;
        NavAccount.Tag = pane == "account" ? "on" : null;
    }

    private void Nav_Click(object sender, RoutedEventArgs e) =>
        ShowPane(ReferenceEquals(sender, NavPlaylists) ? "playlists"
            : ReferenceEquals(sender, NavFollow) ? "follow"
            : ReferenceEquals(sender, NavAccount) ? "account"
            : "search");

    private void FollowGoSearch_Click(object sender, RoutedEventArgs e) => ShowPane("search");

    // ---------------- 平台 / 登录 ----------------

    private void SyncSourceButtons()
    {
        var on = (SolidColorBrush)FindResource("Raised2");
        var fg = (SolidColorBrush)FindResource("Fg");
        var mut = (SolidColorBrush)FindResource("Mut");
        var off = Brushes.Transparent;
        BtnKugou.Background = _source == "kugou" ? on : off;
        BtnKugou.Foreground = _source == "kugou" ? fg : mut;
        BtnNetease.Background = _source == "netease" ? on : off;
        BtnNetease.Foreground = _source == "netease" ? fg : mut;
    }

    private void BtnSource_Click(object sender, RoutedEventArgs e) =>
        SetSource(ReferenceEquals(sender, BtnNetease) ? "netease" : "kugou");

    /// <summary>切换搜索来源（头部 酷狗/网易云 分段按钮与账号卡片的「扫码登录」共用）。</summary>
    private void SetSource(string next)
    {
        if (next == _source) return;
        _source = next;
        SyncSourceButtons();
        StopQrPolling();
        QrPanel.Visibility = Visibility.Collapsed;
        RefreshLogin();
    }

    private void RefreshLogin()
    {
        ApplyLoginCard("kugou", TblLoginKg, BtnLoginKg, BtnLogoutKg);
        ApplyLoginCard("netease", TblLoginNe, BtnLoginNe, BtnLogoutNe);
    }

    /// <summary>账号页两张平台卡片（对位 AccountPane.vue：状态小字 + 扫码登录/退出登录）。</summary>
    private static void ApplyLoginCard(string source, TextBlock status, Button login, Button logout)
    {
        var st = source == "netease" ? ServerContext.Netease.LoginState() : ServerContext.KuGou.LoginState();
        status.Text = st.Logged
            ? "已登录" + (string.IsNullOrEmpty(st.Nickname) ? "" : "：" + st.Nickname)
            : "未登录 — VIP 歌曲与完整音质需要扫码登录";
        login.Visibility = st.Logged ? Visibility.Collapsed : Visibility.Visible;
        logout.Visibility = st.Logged ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnLogout_Click(object sender, RoutedEventArgs e)
    {
        // 卡片按钮带 Tag（kugou/netease），否则沿用当前来源
        var src = (sender as Button)?.Tag as string ?? _source;
        try
        {
            if (src == "netease") ServerContext.Netease.Logout();
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

    private void BtnLogin_Click(object sender, RoutedEventArgs e)
    {
        // 账号卡片的按钮带 Tag（kugou/netease）：先切到该平台，二维码/轮询跟着 _source 走
        if ((sender as Button)?.Tag is string src) SetSource(src);
        _ = OpenQrAsync();
    }

    private void BtnQrRefresh_Click(object sender, RoutedEventArgs e) => _ = OpenQrAsync();

    private void BtnQrClose_Click(object sender, RoutedEventArgs e) => CloseQr();

    private void CloseQr()
    {
        StopQrPolling();
        QrPanel.Visibility = Visibility.Collapsed;
    }

    private async Task OpenQrAsync()
    {
        StopQrPolling();
        QrPanel.Visibility = Visibility.Visible;
        QrImg.Source = null;
        TblQrTitle.Text = _source == "netease" ? "网易云扫码登录" : "酷狗扫码登录";
        QrScanned.Visibility = Visibility.Collapsed;
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
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
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
                        QrScanned.Visibility = Visibility.Collapsed;
                        break;
                    case "2" or "802":
                        TblQrTip.Text = "已扫码，请在手机上确认";
                        QrScanned.Visibility = Visibility.Visible;
                        break;
                    case "0" or "800":
                        TblQrTip.Text = "二维码已过期，请点「刷新二维码」";
                        QrScanned.Visibility = Visibility.Collapsed;
                        StopQrPolling();
                        break;
                    case "4" or "803":
                        StopQrPolling();
                        QrScanned.Visibility = Visibility.Collapsed;
                        QrPanel.Visibility = Visibility.Collapsed;
                        RefreshLogin();
                        Log.Info($"[Cloud] 扫码登录成功（{_source}）");
                        break;
                    default:
                        TblQrTip.Text = "状态未知，请刷新二维码";
                        QrScanned.Visibility = Visibility.Collapsed;
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

    // ±5s / 重置只在「估算进度」的播放器上有意义（对位 FollowPane 的 estimated 分支）
    private void BtnNudgeM5_Click(object sender, RoutedEventArgs e) => Follow.Nudge(-5);

    private void BtnNudgeP5_Click(object sender, RoutedEventArgs e) => Follow.Nudge(5);

    private void BtnOffsetReset_Click(object sender, RoutedEventArgs e) => Follow.Nudge(-Follow.Offset);

    private void BtnRematch_Click(object sender, RoutedEventArgs e) => Follow.Rematch();

    /// <summary>跟随的播放器下拉（FollowPane：自动选择 + 各会话；>1 个会话才显示）。</summary>
    private void CmbFollowSession_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingFollow || !IsLoaded) return;
        Follow.SelectSession(CmbFollowSession.SelectedValue as string);
    }

    private void RefreshFollowSessions(FollowController f, bool has)
    {
        var items = new List<SessionItem> { new("", "自动选择") };
        if (has)
            foreach (var s in f.Sessions)
                items.Add(new SessionItem(s.AppId,
                    s.AppName + "（" + (s.Title.Length > 0 ? s.Title : "无歌曲信息") + "）"));

        SessionsRow.Visibility = has && f.Sessions.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        // 会话清单没变就不重设 ItemsSource（否则下拉展开时会被打断）
        var sig = items.Select(i => i.AppId + "|" + i.Label).ToList();
        if (!sig.SequenceEqual(_sessionSig))
        {
            _sessionSig.Clear();
            _sessionSig.AddRange(sig);
            CmbFollowSession.ItemsSource = items;
        }

        var want = f.AutoSelected ? "" : f.SelectedAppId;
        if (!Equals(CmbFollowSession.SelectedValue as string, want))
        {
            _syncingFollow = true;
            CmbFollowSession.SelectedValue = want;
            _syncingFollow = false;
        }
    }

    private void RefreshFollow()
    {
        if (_closed || App.MainInstance == null) return;
        var f = Follow;
        _syncingFollow = true;
        ChkFollow.IsChecked = f.On;
        _syncingFollow = false;
        TblOffset.Text = $"{(f.Offset > 0 ? "+" : "")}{f.Offset:0.0}s（正数=歌词提前）";

        var has = f.On && f.Status != FollowController.StOff;
        FollowDetail.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        TblFollowDetail.Visibility = f.On && f.Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnRematch.Visibility = has && f.Status is FollowController.StNoMatch or FollowController.StError
            ? Visibility.Visible
            : Visibility.Collapsed;
        var est = has && f.Estimated;
        BtnNudgeM5.Visibility = est ? Visibility.Visible : Visibility.Collapsed;
        BtnNudgeP5.Visibility = est ? Visibility.Visible : Visibility.Collapsed;
        BtnOffsetReset.IsEnabled = has && f.Offset != 0;
        RefreshFollowSessions(f, has);
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
        TblFollowStatus.Foreground = (SolidColorBrush)FindResource(color);
        var now = (f.AppName.Length > 0 ? f.AppName + " ｜ " : "") + f.Title +
                  (f.Artist.Length > 0 ? " - " + f.Artist : "");
        TblFollowNow.Text = (now + (f.MatchedAs.Length > 0 ? "　→ 已匹配 " + f.MatchedAs : "")).Trim('　');
        TblFollowDetail.Text = f.Detail +
                               (f.Estimated ? "（此播放器不上报进度：按「切歌/恢复播放=从头」估算）" : "");
    }
}
