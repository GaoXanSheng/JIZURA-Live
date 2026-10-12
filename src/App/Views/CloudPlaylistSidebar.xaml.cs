using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JizuraLive.App.Cloud;
using JizuraLive.App.I18n;
using JizuraLive.Logging;
using JizuraLive.Server;
using JizuraLive.Server.Models;

namespace JizuraLive.App.Views;

/// <summary>
/// 播放器模式左侧的歌单栏：我的歌单（封面 + 曲目数 + 归属徽标），点整单播、点箭头展开曲目、点曲目从这首播。
/// 歌单/曲目都走与云音乐面板同一套接口与缓存（PlaylistInfo / LyricCache 无关，只是队列来源）。
/// </summary>
public partial class CloudPlaylistSidebar : UserControl
{
    /// <summary>歌单行 VM：封面、展开态、懒加载出来的曲目。</summary>
    public sealed class Row : INotifyPropertyChanged
    {
        public PlaylistInfo Info { get; }
        public string Name => Info.Name;
        public string Initial => Info.Name.Length > 0 ? Info.Name[..1] : "?";
        public string MetaLine { get; }
        public string Badge { get; }
        public Visibility BadgeVisibility { get; }
        public Brush BadgeBrush { get; }
        public Brush Tile { get; private set; }
        public Visibility InitialVisibility { get; private set; } = Visibility.Visible;

        private bool _expanded;
        private bool _loading;
        public List<SongRow> Songs { get; } = [];

        public Row(PlaylistInfo info, Brush tile)
        {
            Info = info;
            Tile = tile;
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

        public string Chevron => _expanded ? "M2 6 L6 2 L10 6 Z" : "M2 2 L6 6 L10 2 Z"; // ▾ / ▸（16 视图框，填充三角）
        public Visibility SongsVisibility => _expanded ? Visibility.Visible : Visibility.Collapsed;
        public Visibility SongsLoadingVisibility => _loading ? Visibility.Visible : Visibility.Collapsed;

        public event PropertyChangedEventHandler? PropertyChanged;

        public bool Expanded
        {
            get => _expanded;
            set
            {
                _expanded = value;
                Raise(nameof(Chevron));
                Raise(nameof(SongsVisibility));
            }
        }

        public bool Loading
        {
            get => _loading;
            set
            {
                _loading = value;
                Raise(nameof(SongsLoadingVisibility));
            }
        }

        public void SetSongs(IEnumerable<KgSong> songs)
        {
            Songs.Clear();
            var i = 1;
            foreach (var s in songs) Songs.Add(new SongRow(s, i++));
            Raise(nameof(Songs));
        }

        public void SetCover(BitmapSource? bmp)
        {
            if (bmp == null) return;
            Tile = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
            InitialVisibility = Visibility.Collapsed;
            Raise(nameof(Tile));
            Raise(nameof(InitialVisibility));
        }

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
    }

    /// <summary>展开出来的曲目行。</summary>
    public sealed class SongRow(KgSong song, int index)
    {
        public KgSong Song { get; } = song;
        public string Index { get; } = index.ToString();
        public string Name { get; } = song.Name;
        public string Dur { get; } = FmtDur(song.Duration);

        private static string FmtDur(long sec)
        {
            sec = Math.Max(0, sec);
            return $"{sec / 60}:{sec % 60:D2}";
        }
    }

    private static readonly string[] TileKeys = ["Tile1", "Tile2", "Tile3", "Tile4", "Tile5", "Tile6"];

    private string _source = "kugou";
    private bool _loading;

    private CloudPlayService? Svc => App.MainInstance?.Cloud;

    public CloudPlaylistSidebar()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _source = CloudPanel.LastSource;
            SyncSourceButtons();
            L.LanguageChanged += ApplyTexts;
            _ = LoadPlaylistsAsync();
        };
        Unloaded += (_, _) => L.LanguageChanged -= ApplyTexts;
    }

    private void ApplyTexts()
    {
        // 行内文案（N 首 / 徽标）是构造时定格的：换语言后重建一遍
        if (Lst.ItemsSource is List<Row> rows && rows.Count > 0) Render(rows.Select(r => r.Info).ToList());
    }

    private void SyncSourceButtons()
    {
        BtnKg.Tag = _source == "kugou" ? "on" : null;
        BtnNe.Tag = _source == "netease" ? "on" : null;
    }

    private void BtnSource_Click(object sender, RoutedEventArgs e)
    {
        var next = ReferenceEquals(sender, BtnNe) ? "netease" : "kugou";
        if (next == _source) return;
        _source = next;
        SyncSourceButtons();
        _ = LoadPlaylistsAsync();
    }

    private async Task LoadPlaylistsAsync()
    {
        if (_loading) return;
        _loading = true;
        SetStatus(L.T("Cloud.PlaylistLoading"), "Mut");
        try
        {
            var r = _source == "netease"
                ? await ServerContext.Netease.MyPlaylistsAsync()
                : await ServerContext.KuGou.MyPlaylistsAsync();
            if (!IsLoaded) return;
            if (!r.Ok)
            {
                Render([]);
                var msg = r.Error ?? L.T("Cloud.ReadFailed");
                SetStatus(msg.StartsWith("请先扫码登录", StringComparison.Ordinal)
                    ? msg + L.T("Cloud.ScanHintSuffix")
                    : L.F("Cloud.LoadFailedMsg", msg), "Danger");
                return;
            }
            Render(r.List);
            SetStatus(r.List.Count > 0 ? "" : L.T("Cloud.PlaylistTipEmpty"), "Mut");
        }
        catch (Exception ex)
        {
            if (!IsLoaded) return;
            Render([]);
            SetStatus(L.F("Cloud.LoadFailedMsg", ex.Message), "Danger");
            Log.Info("[Player] 侧栏歌单加载失败：" + ex.Message);
        }
        finally
        {
            _loading = false;
        }
    }

    private void Render(List<PlaylistInfo> list)
    {
        var rows = list
            .Select(p => new Row(p, TileBrush(p.Name)))
            .ToList();
        Lst.ItemsSource = rows;
        foreach (var row in rows) LoadCover(row);
    }

    private static void LoadCover(Row row)
    {
        var url = row.Info.Cover;
        if (url.Length == 0) return;
        var bmp = CoverCache.Get(url, bmp => row.SetCover(bmp));
        if (bmp != null) row.SetCover(bmp);
    }

    /// <summary>色块：优先取主题里的 Tile1..6（找不到也不抛，退回深灰）。</summary>
    private Brush TileBrush(string name) =>
        TryFindResource(TileKeys[StableTileIndex(name)]) as Brush ?? Brushes.DimGray;

    private static int StableTileIndex(string name)
    {
        var h = 0;
        foreach (var ch in name) h = (h * 31 + ch) & 0x7FFFFFFF;
        return h % 6;
    }

    private void SetStatus(string text, string colorKey)
    {
        TblStatus.Text = text;
        TblStatus.Foreground = (Brush)FindResource(colorKey);
        TblStatus.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------- 交互 ----------------

    private async void BtnExpand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Row row }) return;
        e.Handled = true;
        row.Expanded = !row.Expanded;
        if (!row.Expanded || row.Songs.Count > 0 || row.Info.Id.Length == 0) return;

        row.Loading = true;
        try
        {
            var songs = await FetchSongsAsync(row.Info.Id);
            if (!IsLoaded) return;
            row.SetSongs(songs);
        }
        catch (Exception ex)
        {
            Log.Info("[Player] 侧栏曲目加载失败：" + ex.Message);
        }
        finally
        {
            row.Loading = false;
        }
    }

    /// <summary>点歌单：整单从第一首开始播。</summary>
    private async void BtnPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Row row }) return;
        e.Handled = true;
        try
        {
            var songs = row.Songs.Count > 0 ? row.Songs.Select(s => s.Song).ToList() : await FetchSongsAsync(row.Info.Id);
            if (!IsLoaded || songs.Count == 0) return;
            if (row.Songs.Count == 0) row.SetSongs(songs);
            Svc?.Play(songs, 0, CloudPanel.LastQuality);
        }
        catch (Exception ex)
        {
            Log.Info("[Player] 侧栏整单播放失败：" + ex.Message);
        }
    }

    /// <summary>点曲目：从这首开始播（队列＝这个歌单）。</summary>
    private void BtnSong_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SongRow song }) return;
        e.Handled = true;
        var row = (Lst.ItemsSource as List<Row>)?.FirstOrDefault(r => r.Songs.Contains(song));
        if (row == null) return;
        var queue = row.Songs.Select(s => s.Song).ToList();
        var index = row.Songs.IndexOf(song);
        if (index >= 0) Svc?.Play(queue, index, CloudPanel.LastQuality);
    }

    private Task<List<KgSong>> FetchSongsAsync(string id) =>
        _source == "netease"
            ? ServerContext.Netease.PlaylistSongsAsync(id).ContinueWith(t => t.Result.List)
            : ServerContext.KuGou.PlaylistSongsAsync(id).ContinueWith(t => t.Result.List);
}
