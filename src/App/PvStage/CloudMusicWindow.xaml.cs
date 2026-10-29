using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using JizuraLive.App.Cloud;
using JizuraLive.Logging;
using JizuraLive.Server.Models;
using JizuraLive.App.I18n;

namespace JizuraLive.App.PvStage;

public partial class CloudMusicWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly List<SongRow> _rows = new();
    private int _page = 1;
    private string _lastKeyword = "";
    private string _source = CloudMusicService.Kugou;

    public CloudMusicWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => TxtKeyword.Focus();
    }

    public KgSong? SelectedSong { get; private set; }

    public string Quality =>
        (CmbQuality.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "320";

    public CloudLyricOption? SelectedLyricOption => CmbLyricVersion.SelectedItem as CloudLyricOption;

    private int _lyricOptionsVersion;

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

    private void BtnSource_Click(object sender, RoutedEventArgs e)
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
        TblStatus.Text = L.T("CloudMusic.StatusPrompt");
        ResetDetail();
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
        }

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
            LoadCovers(rows);
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

    private async void LoadCovers(IReadOnlyList<SongRow> rows)
    {
        foreach (var row in rows)
        {
            try
            {
                row.Cover = await CloudMusicService.LoadCoverAsync(row.Song.Cover);
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
        DialogResult = true;
    }

    private void LstSongs_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LstSongs.SelectedItem is SongRow row)
        {
            SelectedSong = row.Song;
            DialogResult = true;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) DialogResult = false;
    }
}
