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

        /// <summary>列表序号（只有歌单详情填；搜索页留空＝不显示，但保留左侧 26px 留白）。</summary>
        public string Index { get; init; } = "";

        private string _status = "";
        private bool _isCurrent;
        private bool _isPlaying;

        /// <summary>这一行是不是正在试听的那首（行内图标常显）。</summary>
        public bool IsCurrent
        {
            get => _isCurrent;
            set
            {
                if (_isCurrent == value) return;
                _isCurrent = value;
                Raise(nameof(IsCurrent));
                Raise(nameof(PlayGlyph));
            }
        }

        /// <summary>当前试听是否在播（决定行内图标 ▶ 还是 ⏸）。</summary>
        public bool IsPlaying
        {
            get => _isPlaying;
            set
            {
                if (_isPlaying == value) return;
                _isPlaying = value;
                Raise(nameof(PlayGlyph));
            }
        }

        /// <summary>行内按钮图标：不是当前曲＝▶；当前曲按播放状态给 ▶/⏸。</summary>
        public string PlayGlyph => _isCurrent && _isPlaying ? "⏸" : "▶";

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

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

    /// <summary>歌单卡片 VM：色块（无缩略图时用歌单名稳定哈希取一档渐变）+ 首字 + 名字 + 「N 首 · 创建者」+ 归属徽标。
    /// 封面拉到后把色块换成图（同一处绑定），首字隐去。</summary>
    public sealed class PlaylistRow : INotifyPropertyChanged
    {
        public PlaylistInfo Info { get; }
        public string Name => Info.Name;
        public string Initial => Info.Name.Length > 0 ? Info.Name[..1] : "?";
        public Brush Tile { get; private set; }
        public Visibility InitialVisibility { get; private set; } = Visibility.Visible;
        public string MetaLine { get; }
        public string Badge { get; }
        public Visibility BadgeVisibility { get; }
        public Brush BadgeBrush { get; }

        /// <summary>入场动画只播一次（虚拟化容器回收复用时不重播，否则滚动一路闪）。</summary>
        public bool Animated { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        public PlaylistRow(PlaylistInfo info, Brush tile)
        {
            Info = info;
            Tile = tile;
            // 曲目数 0 不显示（网易云「我喜欢的音乐」接口就是 0，占位反而像出错）
            MetaLine = string.Join(" · ", new[]
            {
                info.Count > 0 ? L.F("Cloud.CountLine", info.Count) : "",
                info.Creator
            }.Where(x => x.Length > 0));
            (Badge, BadgeVisibility, BadgeBrush) = info.Kind switch
            {
                PlaylistKind.Mine => (L.T("Cloud.BadgeMine"), Visibility.Visible, Res("Cyan")),
                PlaylistKind.Subscribed => (L.T("Cloud.BadgeSubscribed"), Visibility.Visible, Res("Mut")),
                PlaylistKind.Liked => (L.T("Cloud.BadgeLiked"), Visibility.Visible, Res("Amber")),
                _ => ("", Visibility.Collapsed, Brushes.Transparent),
            };
        }

        public void SetCover(BitmapImage? bmp)
        {
            if (bmp == null) return;
            Tile = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
            InitialVisibility = Visibility.Collapsed;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Tile)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InitialVisibility)));
        }

        private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
    }

    /// <summary>歌单名 → 色块档位：不用 string.GetHashCode（.NET 每进程随机加盐，色块会每次开都换）。</summary>
    private static int StableTileIndex(string name)
    {
        var h = 0;
        foreach (var ch in name) h = (h * 31 + ch) & 0x7FFFFFFF;
        return h % 6;
    }

    private string _source = "kugou";
    private string _pane = "search";
    private bool _searching;
    private bool _loading;
    private bool _loadingPlaylists;
    private List<PlaylistInfo> _playlists = []; // 最近一次拉到的「我的歌单」（切页/返回不重拉网络）
    private PlaylistInfo? _curPlaylist;          // 当前打开详情的歌单（竞态判定用）
    private List<PlaylistRow> _playlistRows = []; // 卡片墙全量行（筛选切换复用，封面不重下）
    private string _kindFilter = "all";           // 归属筛选：all / mine / sub / liked

    // 会话级记忆（静态＝跨面板实例）：面板关掉再开不该把搜索词/结果/歌单全忘光
    private static string _lastSource = "kugou";

    /// <summary>上次用的来源/音质（播放器模式侧栏起播时沿用同一档）。</summary>
    public static string LastSource => _lastSource;

    public static string LastQualityOf(int index) => index switch { 1 => "320", 2 => "flac", _ => "128" };

    private static string _lastQuery = "";
    private static List<KgSong> _lastResults = [];
    private static List<PlaylistInfo> _lastPlaylists = [];
    private static int _lastQuality;

    /// <summary>上次选的音质（"128"/"320"/"flac"）。</summary>
    public static string LastQuality => LastQualityOf(_lastQuality);
    private static readonly Dictionary<string, List<KgSong>> DetailCache = new(); // 歌单 id → 曲目（来回切不重拉）
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
            RestoreLastState();               // 上次的搜索词/结果/歌单列表：重开即恢复
            ShowPane(_pane);
            SyncSourceButtons();
            RefreshLogin();
            _syncingFollow = true;
            ChkFollow.IsChecked = Follow.On;
            _syncingFollow = false;
            Follow.Changed += RefreshFollow;
            RefreshFollow();
            Cloud.Changed += SyncPlayGlyphs; // 播放会话是唯一事实源：行内 glyph 与条显隐都跟着它
            SyncPlayGlyphs();
            L.LanguageChanged += ApplyTexts; // 换语言：重建音质下拉、复位搜索按钮并重刷登录/跟随状态行
        };
        Unloaded += (_, _) => L.LanguageChanged -= ApplyTexts;
        Closed += (_, _) =>
        {
            _closed = true;
            StopQrPolling();
            // 关面板不再停播：播放生命周期归 CloudPlayService（主界面底部那条继续控制）
            if (App.MainInstance != null)
            {
                App.MainInstance.Cloud.Changed -= SyncPlayGlyphs;
                Follow.Changed -= RefreshFollow;
            }
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
        if (_playlists.Count > 0) RenderPlaylists(); // 筛选条/徽标等文案重刷
        if (_pane == "detail" && _curPlaylist != null)
            TblDetailMeta.Text = MetaOf(_curPlaylist, LstDetail.Items.Count);
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

    /// <summary>Esc 关窗；扫码层开着时不关（同旧版 onKey 的 !cloud.qr.active 判断）。
    /// 空格＝试听播放/暂停（输入框里打字除外）。</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && App.MainInstance?.Cloud.HasTrack == true
            && Keyboard.FocusedElement is not TextBox and not ComboBox)
        {
            App.MainInstance!.Cloud.Toggle();
            e.Handled = true;
            return;
        }
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
        JzAnim.ShowFade(PanePlaylistDetail, pane == "detail");
        JzAnim.ShowFade(PaneFollow, pane == "follow");
        JzAnim.ShowFade(PaneAccount, pane == "account");
        NavSearch.Tag = pane == "search" ? "on" : null;
        NavPlaylists.Tag = pane is "playlists" or "detail" ? "on" : null; // 歌单详情仍属「我的歌单」
        NavFollow.Tag = pane == "follow" ? "on" : null;
        NavAccount.Tag = pane == "account" ? "on" : null;
        // 进歌单页就拉一次我的歌单（对位 PlaylistPane 的 onMounted → loadPlaylists）；
        // 已经拿到过就不重拉（从歌单详情返回时不必再打一次网络）
        if (pane == "playlists" && _playlists.Count == 0) _ = LoadPlaylistsAsync();
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
        _lastSource = next;
        _lastQuery = "";
        _lastResults = [];
        _lastPlaylists = [];
        SyncSourceButtons();
        StopQrPolling();
        QrPanel.Visibility = Visibility.Collapsed;
        RefreshLogin();
        // 平台换了结果也跟着换（对位 store.setSource：清搜索结果与歌单输入，歌单页重拉）
        Lst.ItemsSource = null;
        TblSearchInfo.Text = "";
        TbPlaylist.Text = "";
        TblPlaylistTip.Text = "";
        _playlists = [];
        RenderPlaylists();
        if (_pane == "detail")
        {
            _curPlaylist = null;
            LstDetail.ItemsSource = null;
            ShowPane("playlists");
        }
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
            _lastQuery = kw;
            _lastResults = r.List.ToList();
            Lst.ItemsSource = _lastResults.Select(s => new CloudRow(s)).ToList();
            SyncPlayGlyphs();
            TblSearchInfo.Text = L.F("Cloud.SearchCount", r.Total) + (r.List.Count == 0 ? L.T("Cloud.NoResults") : "");
            if (r.List.Count == 0) Lst.ItemsSource = null;
        }
        catch (Exception ex)
        {
            if (_closed) return;
            _lastResults = [];
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

    /// <summary>歌单区的空态/加载态行（有卡片就收起）。</summary>
    private void SetPlaylistStatus()
    {
        TblPlaylistStatus.Visibility = LstPlaylists.Items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
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
                _playlists = []; // 拉取失败不留上一轮的旧列表（对位 store 的 playlists = []）
                RenderPlaylists();
                var msg = r.Error ?? L.T("Cloud.ReadFailed");
                SetPlaylistMsg(msg.StartsWith("请先扫码登录", StringComparison.Ordinal)
                    ? msg + L.T("Cloud.ScanHintSuffix")
                    : L.F("Cloud.LoadFailedMsg", msg), "Danger");
                TblPlaylistTip.Text = "";
                return;
            }

            _playlists = r.List.ToList();
            _lastPlaylists = _playlists;
            RenderPlaylists();
            TblPlaylistTip.Text = _playlists.Count > 0
                ? L.T("Cloud.PlaylistTipHas")
                : L.T("Cloud.PlaylistTipEmpty");
            SetPlaylistMsg(TblPlaylistTip.Text, "Mut");
        }
        catch (Exception ex)
        {
            if (_closed) return;
            _playlists = [];
            RenderPlaylists();
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

    /// <summary>歌单分段结果；Grouped=false 表示平台没给归属信息，三段合成一段（全部落在 Mine）。</summary>
    public sealed record PlaylistSections(bool Grouped, List<PlaylistInfo> Liked, List<PlaylistInfo> Mine,
        List<PlaylistInfo> Sub);

    /// <summary>按归属分段（纯函数，便于测试）。</summary>
    public static PlaylistSections GroupPlaylists(IEnumerable<PlaylistInfo> all)
    {
        var list = all.ToList();
        if (list.Any(p => p.Kind.Length == 0))
            return new PlaylistSections(false, [], list, []);
        return new PlaylistSections(true,
            list.Where(p => p.Kind == PlaylistKind.Liked).ToList(),
            list.Where(p => p.Kind == PlaylistKind.Mine).ToList(),
            list.Where(p => p.Kind == PlaylistKind.Subscribed).ToList());
    }

    private static readonly string[] TileKeys = ["Tile1", "Tile2", "Tile3", "Tile4", "Tile5", "Tile6"];

    /// <summary>
    /// 卡片墙：一套虚拟化容器装所有歌单，归属靠**筛选条 + 卡片徽标**区分——原来那三段带标题的
    /// 分组在虚拟化的换行面板里塞不进"整行标题"，三段各一条滚动也不对。
    /// </summary>
    private void RenderPlaylists()
    {
        _playlistRows = _playlists
            .Select(p => new PlaylistRow(p, (Brush)FindResource(TileKeys[StableTileIndex(p.Name)])))
            .ToList();
        ApplyFilter();
        SyncFilterChips();
    }

    private void ApplyFilter()
    {
        LstPlaylists.ItemsSource = _kindFilter switch
        {
            PlaylistKind.Mine => _playlistRows.Where(r => r.Info.Kind == PlaylistKind.Mine).ToList(),
            PlaylistKind.Subscribed => _playlistRows.Where(r => r.Info.Kind == PlaylistKind.Subscribed).ToList(),
            PlaylistKind.Liked => _playlistRows.Where(r => r.Info.Kind == PlaylistKind.Liked).ToList(),
            _ => _playlistRows,
        };
        SetPlaylistStatus();
    }

    /// <summary>筛选条：带条数；平台没给归属信息时只留「全部」，某档为空就收起该档。</summary>
    private void SyncFilterChips()
    {
        var grouped = _playlistRows.Any(r => r.Info.Kind.Length > 0);
        var counts = new Dictionary<string, int>
        {
            ["all"] = _playlistRows.Count,
            [PlaylistKind.Mine] = _playlistRows.Count(r => r.Info.Kind == PlaylistKind.Mine),
            [PlaylistKind.Subscribed] = _playlistRows.Count(r => r.Info.Kind == PlaylistKind.Subscribed),
            [PlaylistKind.Liked] = _playlistRows.Count(r => r.Info.Kind == PlaylistKind.Liked),
        };
        SetChip(ChipAll, "Cloud.PlaylistAll", "all", counts["all"], true);
        SetChip(ChipMine, "Cloud.PlaylistMine", PlaylistKind.Mine, counts[PlaylistKind.Mine], grouped);
        SetChip(ChipSub, "Cloud.PlaylistSubscribed", PlaylistKind.Subscribed, counts[PlaylistKind.Subscribed], grouped);
        SetChip(ChipLiked, "Cloud.PlaylistLiked", PlaylistKind.Liked, counts[PlaylistKind.Liked], grouped);
        if (_kindFilter != "all" && counts.TryGetValue(_kindFilter, out var n) && n == 0)
        {
            _kindFilter = "all"; // 换平台/换账号后当前档没东西了：退回全部
            ApplyFilter();
            SetChip(ChipAll, "Cloud.PlaylistAll", "all", counts["all"], true);
        }
    }

    private void SetChip(Button chip, string labelKey, string kind, int count, bool enabled)
    {
        chip.Content = count > 0 ? $"{L.T(labelKey)} {count}" : L.T(labelKey);
        chip.Visibility = enabled && (kind == "all" || count > 0) ? Visibility.Visible : Visibility.Collapsed;
        var on = _kindFilter == kind;
        // 注意：FindResource 查不到键时给的是 UnsetValue（MS.Internal.NamedObject），强转 Brush 会崩；
        // 一律走 TryFindResource + 兜底
        chip.Foreground = TryFindResource(on ? "Fg" : "Mut") as Brush ?? Brushes.White;
        chip.Background = on ? TryFindResource("Raised2") as Brush ?? Brushes.Transparent : Brushes.Transparent;
    }

    private void ChipFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string kind } || kind == _kindFilter) return;
        _kindFilter = kind;
        ApplyFilter();
        SyncFilterChips();
    }

    /// <summary>首次实例化走 Loaded、容器回收复用走 DataContextChanged（元素"出生"就带着 DataContext，
    /// 首次不会再触发 DataContextChanged——只挂那个等于一张封面都拉不到）。</summary>
    private void Card_Loaded(object sender, RoutedEventArgs e) => CardRealized(sender);

    private void Card_DataChanged(object? sender, DependencyPropertyChangedEventArgs e) => CardRealized(sender);

    private void CardRealized(object? sender)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not PlaylistRow row) return;
        LoadCover(row);
        if (row.Animated) return;
        row.Animated = true;
        JzAnim.FadeSlideIn(fe, 6, JzAnim.Out);
    }

    /// <summary>卡片封面：内存 + 磁盘缓存，拉到即换（同一张图再进页面直接命中）。</summary>
    private static void LoadCover(PlaylistRow row)
    {
        var url = row.Info.Cover;
        if (url.Length == 0) return;
        var bmp = CoverCache.Get(url, bmp => row.SetCover(bmp)); // 命中则立即有值
        if (bmp != null) row.SetCover(bmp);
    }

    // ---------------- 歌单详情 ----------------

    private void BtnDetailBack_Click(object sender, RoutedEventArgs e) => ShowPane("playlists");

    /// <summary>点歌单卡片 → 进详情页看曲目（不再把歌曲灌进搜索页）。头信息直接用卡片上那份，不额外请求。</summary>
    private void BtnPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PlaylistRow row }) return;
        _ = OpenPlaylistAsync(row.Info);
    }

    /// <summary>粘贴框载入（认不出 ID 就只提示，不打网络）。</summary>
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

        // 粘贴进来的歌单没有名字与归属：名字先用占位，曲目数等读完详情补
        await OpenPlaylistAsync(new PlaylistInfo { Id = id, Name = L.T("Cloud.PlaylistWord") });
    }

    /// <summary>歌单详情：拉曲目列表落到详情页（行与「点歌名载入」的语义与搜索页完全一致）。</summary>
    private async Task OpenPlaylistAsync(PlaylistInfo info)
    {
        _curPlaylist = info;
        TblDetailName.Text = info.Name;
        TblDetailMeta.Text = MetaOf(info, info.Count);
        LstDetail.ItemsSource = null;
        ShowPane("detail");

        // 会话级详情缓存：来回切歌单不重拉（酷狗一个歌单最多要 4 次请求）
        if (info.Id.Length > 0 && DetailCache.TryGetValue(info.Id, out var cached))
        {
            FillDetail(info, cached);
            SetDetailStatus(cached.Count == 0 ? L.T("Cloud.DetailEmpty") : "", "Mut");
            return;
        }

        SetDetailStatus(L.T("Cloud.PlaylistReading"), "Mut");
        // 上一次还在读也不用管：下面按 _curPlaylist 引用判定，旧那次读完会自己作废（最后点的那个赢）
        try
        {
            var r = _source == "netease"
                ? await ServerContext.Netease.PlaylistSongsAsync(info.Id)
                : await ServerContext.KuGou.PlaylistSongsAsync(info.Id);
            if (_closed || !ReferenceEquals(_curPlaylist, info)) return; // 期间又点了别的歌单
            if (!r.Ok) throw new SongLoader.CloudException(r.Error ?? L.T("Cloud.ReadFailed"));
            if (info.Id.Length > 0)
            {
                DetailCache[info.Id] = r.List.ToList();
                if (DetailCache.Count > 40) DetailCache.Remove(DetailCache.Keys.First()); // 防长会话堆积
            }
            FillDetail(info, r.List);
            SetDetailStatus(r.List.Count == 0 ? L.T("Cloud.DetailEmpty") : "", "Mut");
            Log.Info($"[Cloud] 歌单已打开：{info.Name}（{r.List.Count} 首，{_source}）");
        }
        catch (Exception ex)
        {
            if (_closed || !ReferenceEquals(_curPlaylist, info)) return;
            SetDetailStatus(L.F("Cloud.PlaylistLoadFailedMsg", ex.Message), "Danger");
            Log.Info("[Cloud] 歌单载入失败：" + ex.Message);
        }
    }

    /// <summary>把曲目铺进详情列表（补真实曲目数 + 认领正在试听那行）。</summary>
    private void FillDetail(PlaylistInfo info, List<KgSong> songs)
    {
        LstDetail.ItemsSource = songs.Select((s, i) => new CloudRow(s) { Index = (i + 1).ToString() }).ToList();
        if (info.Count == 0) TblDetailMeta.Text = MetaOf(info, songs.Count); // 粘贴进来的歌单：补上真实曲目数
        SyncPlayGlyphs();
    }

    private static string MetaOf(PlaylistInfo info, int count) => string.Join(" · ", new[]
    {
        count > 0 ? L.F("Cloud.CountLine", count) : "",
        info.Creator
    }.Where(x => x.Length > 0));

    /// <summary>重开面板恢复上次的搜索词/结果/歌单列表（会话级记忆，进程退出即忘）。</summary>
    private void RestoreLastState()
    {
        _source = _lastSource;
        _syncingFollow = true;
        CmbQuality.SelectedIndex = _lastQuality is >= 0 and <= 2 ? _lastQuality : 0;
        _syncingFollow = false;
        TbQuery.Text = _lastQuery;
        if (_lastResults.Count > 0)
        {
            Lst.ItemsSource = _lastResults.Select(s => new CloudRow(s)).ToList();
            TblSearchInfo.Text = L.F("Cloud.SearchCount", _lastResults.Count);
        }
        if (_lastPlaylists.Count > 0)
        {
            _playlists = [.. _lastPlaylists];
            RenderPlaylists();
        }
        SyncPlayGlyphs();
    }

    /// <summary>详情页状态行（空串＝收起）。</summary>
    private void SetDetailStatus(string text, string colorKey)
    {
        TblDetailStatus.Text = text;
        TblDetailStatus.Foreground = (SolidColorBrush)FindResource(colorKey);
        TblDetailStatus.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------- 试听（交给全局播放会话） ----------------

    private CloudPlayService Cloud => App.MainInstance!.Cloud;

    /// <summary>行内那个键：点一下＝从这首开始顺序试听（队列＝它所在的那个列表）；再点当前曲＝暂停/继续。</summary>
    private void BtnRowPlay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CloudRow row }) return;
        e.Handled = true; // 别让外层整行按钮当成「载入工程」
        if (Cloud.HasTrack && Cloud.IsCurrent(row.Song))
        {
            Cloud.Toggle();
            return;
        }
        var src = LstDetail.Items.Contains(row) ? LstDetail.Items : Lst.Items;
        var rows = src.OfType<CloudRow>().ToList();
        var index = rows.FindIndex(r => CloudPlayService.SameSong(r.Song, row.Song));
        // 身份认不出来的条目（歌单里的空 Hash 空条目）退回列表位置：否则点击静默失效，
        // 看着就是"点了没反应"（这种条目点下去会取不到音频，由会话跳过并给出提示）
        if (index < 0) index = rows.FindIndex(r => ReferenceEquals(r, row));
        if (index >= 0) Cloud.Play(rows.Select(r => r.Song).ToList(), index, Quality);
    }

    /// <summary>歌单详情页的「播放全部」：整单顺序试听。</summary>
    private void BtnPlayAll_Click(object sender, RoutedEventArgs e)
    {
        var queue = LstDetail.Items.OfType<CloudRow>().Select(r => r.Song).ToList();
        if (queue.Count > 0) Cloud.Play(queue, 0, Quality);
    }

    /// <summary>把「正在播放/正在播」标记同步到列表行（播放会话是唯一事实源）；播放条显隐跟着走。</summary>
    private void SyncPlayGlyphs()
    {
        var svc = App.MainInstance?.Cloud;
        if (svc == null) return;
        foreach (var row in Lst.Items.OfType<CloudRow>().Concat(LstDetail.Items.OfType<CloudRow>()))
        {
            var cur = svc.HasTrack && svc.IsCurrent(row.Song);
            row.IsCurrent = cur;
            row.IsPlaying = cur && svc.IsPlaying;
        }
        JzAnim.ShowFade(BarCloud, svc.HasTrack, 6);
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
            Log.Error("[Cloud] 二维码获取失败：" + ex.Message, ex);
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
        // 只停旧计时器，绝不能走 StopQrPolling：它会把 _qrKey 清空，
        // 而 OpenQrAsync 是先设 key 再进这里，key 一清轮询就永远短路（扫码因此一直无效）
        _qrTimer?.Stop();
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
            catch (Exception ex)
            {
                Log.Debug("[Cloud] 扫码轮询异常：" + ex.Message); // 网络抖动，等下一轮
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

    /// <summary>音质选择记进会话（重开面板与试听/装载共用同一档；本次装载即刻生效）。</summary>
    private void CmbQuality_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingFollow) _lastQuality = CmbQuality.SelectedIndex;
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
