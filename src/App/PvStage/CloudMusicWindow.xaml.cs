using System.Globalization;
using System.Windows;
using System.Windows.Input;
using JizuraLive.App.Cloud;
using JizuraLive.Logging;
using JizuraLive.Server.Models;

namespace JizuraLive.App.PvStage;

public partial class CloudMusicWindow : Wpf.Ui.Controls.FluentWindow
{
    private int _page = 1;
    private string _lastKeyword = "";

    public CloudMusicWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => TxtKeyword.Focus();
    }

    public KgSong? SelectedSong { get; private set; }

    private sealed record SongRow(KgSong Song, string Name, string Singer, string Album, string Length);

    private string Source =>
        (CmbSource.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? CloudMusicService.Kugou;

    private async void BtnSearch_Click(object sender, RoutedEventArgs e) => await SearchAsync(reset: true);

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
            TblStatus.Text = "请输入歌名或歌手";
            return;
        }

        if (reset || !string.Equals(keyword, _lastKeyword, StringComparison.Ordinal))
        {
            _lastKeyword = keyword;
            _page = 1;
            LstSongs.ItemsSource = null;
        }

        BtnSearch.IsEnabled = false;
        TblStatus.Text = "正在搜索…";
        try
        {
            var songs = await CloudMusicService.SearchAsync(Source, keyword, _page);
            var rows = songs.Select(s => new SongRow(s, s.Name, s.Singer, s.Album, FormatLength(s.Duration)))
                .ToList();
            LstSongs.ItemsSource = rows;
            if (rows.Count == 0) TblStatus.Text = "没有找到歌曲";
            else TblStatus.Text = $"共 {rows.Count} 首（第 {_page} 页）";
        }
        catch (Exception ex)
        {
            Log.Warn("[Cloud] 搜索失败：" + ex.Message);
            TblStatus.Text = "搜索失败：" + ex.Message;
        }
        finally
        {
            BtnSearch.IsEnabled = true;
        }
    }

    private static string FormatLength(long seconds)
    {
        if (seconds <= 0) return "";
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalMinutes:00}:{ts.Seconds:00}";
    }

    private void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        if (LstSongs.SelectedItem is not SongRow row)
        {
            TblStatus.Text = "先在列表里选中一首歌";
            return;
        }

        SelectedSong = row.Song;
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void LstSongs_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LstSongs.SelectedItem is SongRow row)
        {
            SelectedSong = row.Song;
            DialogResult = true;
        }
    }
}
