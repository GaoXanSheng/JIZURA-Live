using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using JizuraLive.App.Cloud;
using JizuraLive.App.I18n;
using JizuraLive.App.UI;
using JizuraLive.Logging;
using JizuraLive.Server.Models;

namespace JizuraLive.App.PvStage;

public partial class CloudMusicWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly List<SongRow> _rows = new();
    private List<PlaylistInfo> _playlists = new();
    private int _page = 1;
    private string _lastKeyword = "";
    private string _source = CloudMusicService.Kugou;
    private bool _loggedIn;

    public CloudMusicWindow()
    {
        InitializeComponent();
        ApplySavedBounds();
        Loaded += async (_, _) =>
        {
            TxtKeyword.Focus();
            await RefreshAccountAsync();
        };
        Closed += (_, _) =>
        {
            _coverLoadVersion++;
            SaveBounds();
        };
    }

    public event EventHandler? ImportRequested;

    public KgSong? SelectedSong { get; private set; }

    public string Quality =>
        (CmbQuality.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "128";

    public CloudLyricOption? SelectedLyricOption => CmbLyricVersion.SelectedItem as CloudLyricOption;

    private void ApplySavedBounds()
    {
        if (App.SettingsOf()?.Layout is not { } layout) return;
        if (double.IsFinite(layout.CloudWidth) && layout.CloudWidth > 0)
            Width = Math.Max(MinWidth, layout.CloudWidth);
        if (double.IsFinite(layout.CloudHeight) && layout.CloudHeight > 0)
            Height = Math.Max(MinHeight, layout.CloudHeight);
        if (layout.CloudLeft is not { } left || layout.CloudTop is not { } top) return;
        if (!double.IsFinite(left) || !double.IsFinite(top)) return;
        if (!IsOnScreen(left, top, Width, Height)) return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
    }

    private void SaveBounds()
    {
        if (App.SettingsOf()?.Layout is not { } layout) return;
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;
        if (!double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height)
            || bounds.Width <= 0 || bounds.Height <= 0) return;

        layout.CloudLeft = bounds.Left;
        layout.CloudTop = bounds.Top;
        layout.CloudWidth = bounds.Width;
        layout.CloudHeight = bounds.Height;
        App.SettingsOf()?.SaveDebounced();
    }

    private static bool IsOnScreen(double left, double top, double width, double height)
    {
        const double minVisible = 80;
        double overlapX = Math.Min(left + width, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth)
                          - Math.Max(left, SystemParameters.VirtualScreenLeft);
        double overlapY = Math.Min(top + height, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
                          - Math.Max(top, SystemParameters.VirtualScreenTop);
        return overlapX >= minVisible && overlapY >= minVisible;
    }

    private int _lyricOptionsVersion;
    private int _coverLoadVersion;

    private sealed class SongRow : INotifyPropertyChanged
    {
        private ImageSource? _cover;

        public SongRow(KgSong song, string length)
        {
            Song = song;
            Name = song.Name;
            Meta = string.IsNullOrWhiteSpace(song.Album) ? song.Singer : $"{song.Singer} · {song.Album}";
            Length = length;
        }

        public KgSong Song { get; }
        public string Name { get; }
        public string Meta { get; }
        public string Length { get; }

        public ImageSource? Cover
        {
            get => _cover;
            set
            {
                _cover = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Cover)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private async void BtnSource_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string source } || source == _source) return;
        _source = source;
        BtnKugou.Appearance = source == CloudMusicService.Kugou
            ? Wpf.Ui.Controls.ControlAppearance.Primary
            : Wpf.Ui.Controls.ControlAppearance.Transparent;
        BtnNetease.Appearance = source == CloudMusicService.Netease
            ? Wpf.Ui.Controls.ControlAppearance.Primary
            : Wpf.Ui.Controls.ControlAppearance.Transparent;
        _rows.Clear();
        LstSongs.ItemsSource = null;
        _coverLoadVersion++;
        TblStatus.Text = L.T("CloudMusic.StatusPrompt");
        ResetDetail();
        await RefreshAccountAsync();
    }

    private async void BtnSearch_Click(object sender, RoutedEventArgs e) => await SearchAsync(reset: true);

    private async void BtnMore_Click(object sender, RoutedEventArgs e)
    {
        if (TxtKeyword.Text.Trim().Length == 0) return;
        _page++;
        await SearchAsync(reset: false);
    }

    private async void TxtKeyword_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SearchAsync(reset: true);
    }

    private async Task SearchAsync(bool reset)
    {
        var keyword = TxtKeyword.Text.Trim();
        if (keyword.Length == 0)
        {
            TblStatus.Text = L.T("CloudMusic.EnterKeyword");
            return;
        }

        if (reset || !string.Equals(keyword, _lastKeyword, StringComparison.Ordinal))
        {
            _lastKeyword = keyword;
            _page = 1;
            _rows.Clear();
            _coverLoadVersion++;
        }
        var coverVersion = _coverLoadVersion;

        BtnSearch.IsEnabled = false;
        BtnMore.IsEnabled = false;
        TblStatus.Text = L.T("CloudMusic.StatusSearching");
        try
        {
            var songs = await CloudMusicService.SearchAsync(_source, keyword, _page);
            var rows = songs.Select(s => new SongRow(s, FormatLength(s.Duration))).ToList();
            _rows.AddRange(rows);
            LstSongs.ItemsSource = null;
            LstSongs.ItemsSource = _rows;
            if (rows.Count == 0) TblStatus.Text = _rows.Count == 0 ? L.T("CloudMusic.NoResults") : L.T("CloudMusic.NoMoreResults");
            else TblStatus.Text = L.F("CloudMusic.ResultPage", _rows.Count, _page);
            LoadCovers(rows, coverVersion);
        }
        catch (Exception ex)
        {
            Log.Warn("[Cloud] 搜索失败：" + ex.Message);
            TblStatus.Text = L.T("CloudMusic.SearchFailed") + ex.Message;
        }
        finally
        {
            BtnSearch.IsEnabled = true;
            BtnMore.IsEnabled = true;
        }
    }

    private const int MaxCoverLoads = 80;

    private async void LoadCovers(IReadOnlyList<SongRow> rows, int version)
    {
        var loaded = 0;
        foreach (var row in rows)
        {
            if (version != _coverLoadVersion) return;
            if (loaded++ >= MaxCoverLoads) return;
            try
            {
                var cover = await CloudMusicService.LoadCoverAsync(row.Song.Cover);
                if (version != _coverLoadVersion) return;
                row.Cover = cover;
            }
            catch
            {
            }
        }
    }

    private static string FormatLength(long seconds)
    {
        if (seconds <= 0) return "";
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalMinutes:00}:{ts.Seconds:00}";
    }

    private async Task RefreshAccountAsync()
    {
        _playlists.Clear();
        FavoriteHost.Children.Clear();
        PlaylistHost.Children.Clear();

        var state = CloudMusicService.LoginState(_source);
        _loggedIn = state.Logged;
        TblAccount.Text = _loggedIn
            ? string.IsNullOrWhiteSpace(state.Nickname) ? L.T("CloudMusic.LoggedIn") : state.Nickname
            : L.T("CloudMusic.NotLoggedIn");
        BtnLogin.Visibility = _loggedIn ? Visibility.Collapsed : Visibility.Visible;
        BtnLogout.Visibility = _loggedIn ? Visibility.Visible : Visibility.Collapsed;
        TblPlaylistHint.Text = "";
        TblPlaylistGroup.Visibility = Visibility.Collapsed;

        if (_loggedIn) await LoadPlaylistsAsync();
    }

    private async Task LoadPlaylistsAsync()
    {
        TblPlaylistHint.Text = L.T("CloudMusic.PlaylistsLoading");
        try
        {
            _playlists = await CloudMusicService.MyPlaylistsAsync(_source);
            TblPlaylistHint.Text = "";
            BuildPlaylistHost();
        }
        catch (Exception ex)
        {
            _playlists.Clear();
            FavoriteHost.Children.Clear();
            PlaylistHost.Children.Clear();
            TblPlaylistGroup.Visibility = Visibility.Collapsed;
            TblPlaylistHint.Text = L.T("CloudMusic.PlaylistsFailed") + ex.Message;
            BtnLogin.Visibility = Visibility.Visible;
            BtnLogout.Visibility = Visibility.Collapsed;
            Log.Warn("[Cloud] 歌单获取失败：" + ex);
        }
    }

    private void BuildPlaylistHost()
    {
        FavoriteHost.Children.Clear();
        PlaylistHost.Children.Clear();

        var favorites = _playlists.Where(p => p.Kind == PlaylistKind.Liked).ToList();
        var playlists = _playlists.Where(p => p.Kind != PlaylistKind.Liked).ToList();

        if (favorites.Count > 0)
        {
            FavoriteHost.Children.Add(GroupHeader(L.T("CloudMusic.Favorites")));
            foreach (var playlist in favorites) FavoriteHost.Children.Add(PlaylistRow(playlist));
        }

        TblPlaylistGroup.Visibility = playlists.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var playlist in playlists) PlaylistHost.Children.Add(PlaylistRow(playlist));

        if (favorites.Count == 0 && playlists.Count == 0)
            TblPlaylistHint.Text = L.T("CloudMusic.PlaylistsEmpty");
    }

    private static TextBlock GroupHeader(string text)
    {
        var header = new TextBlock
        {
            Text = text,
            FontSize = 11,
            Margin = new Thickness(4, 10, 0, 4),
        };
        header.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        return header;
    }

    private FrameworkElement PlaylistRow(PlaylistInfo playlist)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = playlist.Name,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        grid.Children.Add(name);

        var count = new TextBlock
        {
            Text = L.F("CloudMusic.PlaylistCount", playlist.Count),
            FontSize = 10,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        count.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        Grid.SetColumn(count, 1);
        grid.Children.Add(count);

        var row = new Border
        {
            Child = grid,
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(0, 1, 0, 1),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
        };
        row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += async (_, _) => await LoadPlaylistAsync(playlist);
        return row;
    }

    private async Task LoadPlaylistAsync(PlaylistInfo playlist)
    {
        TblStatus.Text = L.F("CloudMusic.PlaylistLoading", playlist.Name);
        try
        {
            var songs = await CloudMusicService.PlaylistSongsAsync(_source, playlist.Id);
            _lastKeyword = "";
            _page = 1;
            _coverLoadVersion++;
            var rows = songs.Select(s => new SongRow(s, FormatLength(s.Duration))).ToList();
            _rows.Clear();
            _rows.AddRange(rows);
            LstSongs.ItemsSource = null;
            LstSongs.ItemsSource = _rows;
            TblStatus.Text = rows.Count == 0
                ? L.T("CloudMusic.PlaylistEmpty")
                : L.F("CloudMusic.PlaylistLoaded", playlist.Name, rows.Count);
            LoadCovers(rows, _coverLoadVersion);
        }
        catch (Exception ex)
        {
            TblStatus.Text = L.T("CloudMusic.PlaylistLoadFailed") + ex.Message;
            Log.Warn("[Cloud] 歌单歌曲获取失败：" + ex);
        }
    }

    private async void BtnLogin_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CloudLoginDialog(_source) { Owner = this };
        if (dialog.ShowDialog() == true) await RefreshAccountAsync();
    }

    private async void BtnLogout_Click(object sender, RoutedEventArgs e)
    {
        CloudMusicService.Logout(_source);
        await RefreshAccountAsync();
    }

    private async void LstSongs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (LstSongs.SelectedItem is not SongRow row)
        {
            ResetDetail();
            return;
        }

        TblName.Text = row.Name;
        TblSinger.Text = row.Meta;
        TblAlbum.Text = row.Length;
        ImgCover.Source = null;
        if (row.Cover is not null) ImgCover.Source = row.Cover;
        TblDetailStatus.Text = L.T("CloudMusic.DoubleClickHint");
        await LoadLyricOptionsAsync(row.Song);
    }

    private async Task LoadLyricOptionsAsync(KgSong song)
    {
        var version = ++_lyricOptionsVersion;
        CmbLyricVersion.IsEnabled = false;
        CmbLyricVersion.Items.Clear();
        CmbLyricVersion.Items.Add(new CloudLyricOption("", "", L.T("CloudMusic.LyricVersionDefault")));
        CmbLyricVersion.SelectedIndex = 0;
        try
        {
            var options = await CloudMusicService.GetLyricOptionsAsync(song);
            if (version != _lyricOptionsVersion) return;
            CmbLyricVersion.Items.Clear();
            foreach (var option in options) CmbLyricVersion.Items.Add(option);
            CmbLyricVersion.SelectedIndex = 0;
        }
        catch
        {
        }
        finally
        {
            if (version == _lyricOptionsVersion) CmbLyricVersion.IsEnabled = true;
        }
    }

    private void ResetDetail()
    {
        _lyricOptionsVersion++;
        CmbLyricVersion.Items.Clear();
        CmbLyricVersion.Items.Add(new CloudLyricOption("", "", L.T("CloudMusic.LyricVersionDefault")));
        CmbLyricVersion.SelectedIndex = 0;
        CmbLyricVersion.IsEnabled = false;
        TblName.Text = L.T("CloudMusic.SelectSong");
        TblSinger.Text = "";
        TblAlbum.Text = "";
        ImgCover.Source = null;
    }

    private void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        if (LstSongs.SelectedItem is not SongRow row)
        {
            TblStatus.Text = L.T("CloudMusic.SelectFirst");
            return;
        }

        SelectedSong = row.Song;
        ImportRequested?.Invoke(this, EventArgs.Empty);
    }

    private void LstSongs_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LstSongs.SelectedItem is SongRow row)
        {
            SelectedSong = row.Song;
            ImportRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }
}
