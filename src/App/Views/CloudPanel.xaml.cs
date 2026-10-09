using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using JizuraLive.App.Cloud;
using JizuraLive.App.I18n;
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

    /// <summary>歌单行 VM（PlaylistPane 的列表项）。</summary>
    public sealed class PlaylistRow
    {
        public PlaylistInfo Info { get; }
        public string Name => Info.Name;
        public string CountLine => L.F("Cloud.CountLine", Info.Count);

        public PlaylistRow(PlaylistInfo info) => Info = info;
    }

    private string _source = "kugou";
    private string _pane = "search";
    private bool _searching;
    private bool _loading;
    private bool _loadingPlaylists;
    private bool _loadingSongs;
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
        ApplyTexts();
        Loaded += (_, _) =>
        {
            FitToOwner();
            JzAnim.PlayWindowIn(Mask, Card); // 打开动效：遮罩淡入 + 卡片微缩放上浮
            ShowPane(_pane);
            SyncSourceButtons();
            RefreshLogin();
            _syncingFollow = true;
            ChkFollow.IsChecked = Follow.On;
            _syncingFollow = false;
            Follow.Changed += RefreshFollow;
            RefreshFollow();
            L.LanguageChanged += ApplyTexts; // 换语言：重建音质下拉、复位搜索按钮并重刷登录/跟随状态行
        };
        Unloaded += (_, _) => L.LanguageChanged -= ApplyTexts;
        Closed += (_, _) =>
        {
            _closed = true;
            StopQrPolling();
            if (App.MainInstance != null) Follow.Changed -= RefreshFollow;
            if (ReferenceEquals(_open, this)) _open = null;
        };
    }

    /// <summary>语言切换（含构造期首建）：重刷常驻静态赋值；动态状态行在下次状态更新时自然用新语言。</summary>
    private void ApplyTexts()
    {
        // 音质下拉重建（「无损」走词库，128k/320k 无中文原样）；_syncing 防误触发 SelectionChanged
        _syncingFollow = true;
        var idx = CmbQuality.SelectedIndex;
        CmbQuality.ItemsSource = new[] { "128k", "320k", L.T("Cloud.QualityLossless") };
        CmbQuality.SelectedIndex = idx < 0 ? 0 : idx;
        _syncingFollow = false;
        if (!_searching && !_loading) BtnSearch.Content = L.T("Cloud.Search");
        if (!IsLoaded) return; // 构造期：登录卡片与跟随状态由 Loaded 里的首刷负责
        RefreshLogin();
        RefreshFollow();
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
        JzAnim.ShowFade(PaneSearch, pane == "search");
        JzAnim.ShowFade(PanePlaylists, pane == "playlists");
        JzAnim.ShowFade(PaneFollow, pane == "follow");
        JzAnim.ShowFade(PaneAccount, pane == "account");
        NavSearch.Tag = pane == "search" ? "on" : null;
        NavPlaylists.Tag = pane == "playlists" ? "on" : null;
        NavFollow.Tag = pane == "follow" ? "on" : null;
        NavAccount.Tag = pane == "account" ? "on" : null;
        // 进歌单页就拉一次我的歌单（对位 PlaylistPane 的 onMounted → loadPlaylists）
        if (pane == "playlists") _ = LoadPlaylistsAsync();
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
        // 平台换了结果也跟着换（对位 store.setSource：清搜索结果与歌单输入，歌单页重拉）
        Lst.ItemsSource = null;
        TblSearchInfo.Text = "";
        TbPlaylist.Text = "";
        TblPlaylistTip.Text = "";
        LstPlaylists.ItemsSource = null;
        TblPlaylistStatus.Text = "";
        SetPlaylistStatus();
        if (_pane == "playlists") _ = LoadPlaylistsAsync();
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
            ? (string.IsNullOrEmpty(st.Nickname) ? L.T("Cloud.LoggedIn") : L.F("Cloud.LoggedInAs", st.Nickname))
            : L.T("Cloud.NotLoggedIn");
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
        BtnSearch.Content = L.T("Cloud.Searching");
        TblSearchInfo.Text = L.T("Cloud.Searching");
        try
        {
            var r = _source == "netease"
                ? await ServerContext.Netease.SearchAsync(kw, 1)
                : await ServerContext.KuGou.SearchAsync(kw, 1);
            if (_closed) return;
            if (!r.Ok)
                throw new SongLoader.CloudException(r.Error ?? L.T("Cloud.SearchFailed"));
            Lst.ItemsSource = r.List.Select(s => new CloudRow(s)).ToList();
            TblSearchInfo.Text = L.F("Cloud.SearchCount", r.Total) + (r.List.Count == 0 ? L.T("Cloud.NoResults") : "");
            if (r.List.Count == 0) Lst.ItemsSource = null;
        }
        catch (Exception ex)
        {
            if (_closed) return;
            Lst.ItemsSource = null;
            TblSearchInfo.Text = "";
            var msg = ex.Message;
            MessageBox.Show(this, L.F("Cloud.SearchFailedMsg", msg), L.T("Cloud.MsgTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            Log.Info("[Cloud] 搜索失败：" + msg);
        }
        finally
        {
            _searching = false;
            if (!_closed)
            {
                BtnSearch.IsEnabled = true;
                BtnSearch.Content = L.T("Cloud.Search");
            }
        }
    }

    // ---------------- 我的歌单（对位 PlaylistPane.vue + cloud store 的 loadPlaylists/loadPlaylistSongs） ----------------

    /// <summary>歌单 ID 解析（对位旧前端 api.parsePlaylistId）：global_collection_id= → id= →
    /// 整串纯 ID（≥5 位，可含 -）。比旧版多认一种「/playlist/&lt;id&gt;」路径式分享链接（网易云常见形态）。</summary>
    public static string ParsePlaylistId(string? s)
    {
        s = (s ?? "").Trim();
        var m = Regex.Match(s, @"[?&]global_collection_id=([\w-]+)");
        if (!m.Success) m = Regex.Match(s, @"[?&]id=([\w-]+)");
        if (!m.Success) m = Regex.Match(s, @"/playlist/([\w-]+)");
        if (!m.Success) m = Regex.Match(s, @"^([\w-]{5,})$");
        return m.Success ? m.Groups[1].Value : "";
    }

    /// <summary>歌单区的空态/加载态行（有列表就收起）。</summary>
    private void SetPlaylistStatus()
    {
        TblPlaylistStatus.Visibility = LstPlaylists.Items.Count > 0
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    /// <summary>拉「我的歌单」（对位 loadPlaylists）：未登录/读取失败都落在歌单区的提示行上，不弹窗。</summary>
    private async Task LoadPlaylistsAsync()
    {
        if (_loadingPlaylists) return;
        _loadingPlaylists = true;
        SetPlaylistMsg(L.T("Cloud.PlaylistLoading"), "Mut");
        try
        {
            var r = _source == "netease"
                ? await ServerContext.Netease.MyPlaylistsAsync()
                : await ServerContext.KuGou.MyPlaylistsAsync();
            if (_closed) return;
            if (!r.Ok)
            {
                LstPlaylists.ItemsSource = null; // 拉取失败不留上一轮的旧列表（对位 store 的 playlists = []）
                var msg = r.Error ?? L.T("Cloud.ReadFailed");
                SetPlaylistMsg(msg.StartsWith("请先扫码登录", StringComparison.Ordinal)
                    ? msg + L.T("Cloud.ScanHintSuffix")
                    : L.F("Cloud.LoadFailedMsg", msg), "Danger");
                TblPlaylistTip.Text = "";
                return;
            }

            LstPlaylists.ItemsSource = r.List.Select(p => new PlaylistRow(p)).ToList();
            TblPlaylistTip.Text = r.List.Count > 0
                ? L.T("Cloud.PlaylistTipHas")
                : L.T("Cloud.PlaylistTipEmpty");
            SetPlaylistMsg(TblPlaylistTip.Text, "Mut");
        }
        catch (Exception ex)
        {
            if (_closed) return;
            LstPlaylists.ItemsSource = null;
            SetPlaylistMsg(L.F("Cloud.LoadFailedMsg", ex.Message), "Danger");
            TblPlaylistTip.Text = "";
            Log.Info("[Cloud] 我的歌单加载失败：" + ex.Message);
        }
        finally
        {
            _loadingPlaylists = false;
        }
    }

    private void SetPlaylistMsg(string text, string colorKey)
    {
        TblPlaylistStatus.Text = text;
        TblPlaylistStatus.Foreground = (SolidColorBrush)FindResource(colorKey);
        SetPlaylistStatus();
    }

    /// <summary>歌单 → 歌曲列表（对位 loadPlaylistSongs）：结果落在搜索页的结果列表里，点歌名即可载入。</summary>
    private async Task LoadPlaylistSongsAsync(string id, string? label)
    {
        if (_loadingSongs) return;
        _loadingSongs = true;
        TblPlaylistTip.Text = L.T("Cloud.PlaylistReading");
        try
        {
            var r = _source == "netease"
                ? await ServerContext.Netease.PlaylistSongsAsync(id)
                : await ServerContext.KuGou.PlaylistSongsAsync(id);
            if (_closed) return;
            if (!r.Ok) throw new SongLoader.CloudException(r.Error ?? L.T("Cloud.ReadFailed"));
            ShowPane("search"); // 载入完切到搜索页看结果（对位 cloud.pane = 'search'）
            Lst.ItemsSource = r.List.Select(s => new CloudRow(s)).ToList();
            TbQuery.Text = label ?? "";
            var name = label is { Length: > 0 } ? label : L.T("Cloud.PlaylistWord");
            TblSearchInfo.Text = L.F("Cloud.PlaylistLoadedLine", name, r.List.Count);
            if (r.List.Count == 0) Lst.ItemsSource = null;
            TblPlaylistTip.Text = ""; // 歌单页已切走；下次进页会重拉并重写提示
            Log.Info($"[Cloud] 歌单已载入：{name}（{r.List.Count} 首，{_source}）");
        }
        catch (Exception ex)
        {
            if (_closed) return;
            TblPlaylistTip.Text = L.F("Cloud.PlaylistLoadFailedMsg", ex.Message);
            Log.Info("[Cloud] 歌单载入失败：" + ex.Message);
            MessageBox.Show(this, L.F("Cloud.PlaylistLoadFailedMsg", ex.Message), L.T("Cloud.MsgTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _loadingSongs = false;
        }
    }

    private void BtnPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PlaylistRow row }) return;
        _ = LoadPlaylistSongsAsync(row.Info.Id, row.Name);
    }

    /// <summary>粘贴框载入（对位 loadPasted）：认不出 ID 就只提示，不打网络。</summary>
    private void BtnPlaylistLoad_Click(object sender, RoutedEventArgs e) => _ = LoadPastedAsync();

    private void TbPlaylist_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _ = LoadPastedAsync();
        e.Handled = true;
    }

    private async Task LoadPastedAsync()
    {
        var id = ParsePlaylistId(TbPlaylist.Text);
        if (id.Length == 0)
        {
            TblPlaylistTip.Text = _source == "netease"
                ? L.T("Cloud.PlaylistIdHintNe")
                : L.T("Cloud.PlaylistIdHintKg");
            return;
        }

        await LoadPlaylistSongsAsync(id, null);
    }

    // ---------------- 装载 ----------------

    private async void BtnLoad_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is not Button { Tag: CloudRow row }) return;
        _loading = true;
        // 挡重复点击用 IsHitTestVisible 而不是 IsEnabled：禁用态会让整张列表进入 WPF 的 disabled 视觉，
        // 逐像素透明的窗口在装载期间补帧不及时，就是用户看到的「面板底色错乱」。悬停高亮本来就靠命中测试，
        // 挡掉命中测试一样能防连点，但不碰行的配色。
        Lst.IsHitTestVisible = false;
        BtnSearch.IsEnabled = false;
        var song = row.Song;
        row.Status = L.T("Cloud.StatusGetUrl");
        try
        {
            var res = await SongLoader.LoadAsync(State, _source, song, Quality, s => row.Status = s);
            row.Status = L.T("Cloud.StatusLoaded");
            Log.Info($"[Cloud] 云音乐装载完成：{song.Name}（{res.FileName}{res.LyricNote}）"
                     + (res.Trial ? "【仅试听片段：这首需要 VIP，可扫码登录后再试】" : ""));
            Close(); // 装载成功即关窗（对位旧版「点歌名载入」的闭环）
        }
        catch (Exception ex)
        {
            row.Status = L.T("Cloud.StatusFailed");
            Log.Error("[Cloud] 装载失败：" + ex.Message, ex);
            if (!_closed)
                MessageBox.Show(this, L.F("Cloud.LoadFailedBox", song.Name, ex.Message), L.T("Cloud.MsgTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _loading = false;
            if (!_closed)
            {
                Lst.IsHitTestVisible = true;
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
        JzAnim.ShowFade(QrPanel, true);
        QrImg.Source = null;
        TblQrTitle.Text = _source == "netease" ? L.T("Cloud.QrTitleNe") : L.T("Cloud.QrTitleKg");
        QrScanned.Visibility = Visibility.Collapsed;
        TblQrTip.Text = L.T("Cloud.QrFetching");
        try
        {
            var q = _source == "netease"
                ? await ServerContext.Netease.QrAsync()
                : await ServerContext.KuGou.QrAsync();
            if (!q.Ok || string.IsNullOrEmpty(q.Key))
                throw new SongLoader.CloudException(q.Error ?? L.T("Cloud.QrGenFailed"));
            _qrKey = q.Key;
            QrImg.Source = await DecodeQrImageAsync(q.Img);
            TblQrTip.Text = L.T("Cloud.QrWaiting");
            StartQrPolling();
        }
        catch (Exception ex)
        {
            TblQrTip.Text = L.F("Cloud.FailedMsg", ex.Message);
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
                    TblQrTip.Text = r.Error ?? L.T("Cloud.QueryFailed");
                    return;
                }

                var st = r.Status;
                // 酷狗状态码：1 等待 / 2 已扫码 / 0 过期 / 4 成功；网易云：801 / 802 / 800 / 803
                switch (st)
                {
                    case "1" or "801":
                        TblQrTip.Text = L.T("Cloud.QrWaiting");
                        QrScanned.Visibility = Visibility.Collapsed;
                        break;
                    case "2" or "802":
                        TblQrTip.Text = L.T("Cloud.QrScannedConfirm");
                        JzAnim.ShowFade(QrScanned, true); // 已扫码：浮层淡入（轮询反复刷，靠幂等）
                        break;
                    case "0" or "800":
                        TblQrTip.Text = L.T("Cloud.QrExpired");
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
                        TblQrTip.Text = L.T("Cloud.QrUnknown");
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
        var items = new List<SessionItem> { new("", L.T("Cloud.AutoSelect")) };
        if (has)
            foreach (var s in f.Sessions)
                items.Add(new SessionItem(s.AppId,
                    L.F("Cloud.SessionItem", s.AppName,
                        s.Title.Length > 0 ? s.Title : L.T("Cloud.NoSongInfo"))));

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
        TblOffset.Text = L.F("Cloud.OffsetText", $"{(f.Offset > 0 ? "+" : "")}{f.Offset:0.0}s");

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
            FollowController.StNoSession => (L.T("Cloud.StNoSession"), "Amber"),
            FollowController.StNoMeta => (L.T("Cloud.StNoMeta"), "Amber"),
            FollowController.StMatching => (L.T("Cloud.StMatching"), "Mut"),
            FollowController.StOk => (L.T("Cloud.StOk"), "Cyan"),
            FollowController.StNoMatch => (L.T("Cloud.StNoMatch"), "Amber"),
            FollowController.StError => (L.T("Cloud.StError"), "Danger"),
            _ => (L.T("Cloud.StOff"), "Mut"),
        };
        TblFollowStatus.Text = text + (f.Fmt.Length > 0
            ? " · " + (f.Fmt == "krc" ? L.T("Cloud.FmtKrc") : f.Fmt == "ne" ? L.T("Cloud.FmtNe") : "LRC")
            : "");
        TblFollowStatus.Foreground = (SolidColorBrush)FindResource(color);
        var now = (f.AppName.Length > 0 ? f.AppName + " ｜ " : "") + f.Title +
                  (f.Artist.Length > 0 ? " - " + f.Artist : "");
        TblFollowNow.Text = (now + (f.MatchedAs.Length > 0 ? L.F("Cloud.MatchedAs", f.MatchedAs) : "")).Trim('　');
        TblFollowDetail.Text = f.Detail +
                               (f.Estimated ? L.T("Cloud.EstimatedNote") : "");
    }
}
