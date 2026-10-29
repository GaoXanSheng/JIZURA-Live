using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using JizuraLive.App.Cloud;
using JizuraLive.Logging;
using JizuraLive.App.I18n;

namespace JizuraLive.App.PvStage;

public partial class PvStageWindow
{
    private void BtnProjectMenu_Click(object sender, RoutedEventArgs e) =>
        ShowMenu((FrameworkElement)sender, new (string Label, Action Click)[]
        {
            (L.T("新建项目"), () => BtnNewProject_Click(this, new RoutedEventArgs())),
            (L.T("打开项目…"), () => BtnOpenProject_Click(this, new RoutedEventArgs())),
            (L.T("保存"), SaveNow),
            (L.T("另存为…"), SaveProjectAs),
        });

    private void ShowMenu(FrameworkElement anchor, IReadOnlyList<(string Label, Action Click)> items)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            MinWidth = 150,
        };

        foreach (var (label, action) in items)
        {
            var item = new Wpf.Ui.Controls.MenuItem { Header = label };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    internal async void BtnCloudMusic_Click(object sender, RoutedEventArgs e) => await ImportCloudMusicAsync();

    private async Task ImportCloudMusicAsync()
    {
        var dlg = new CloudMusicWindow { Owner = this };
        if (dlg.ShowDialog() != true || dlg.SelectedSong is not { } song) return;

        TblStatus.Text = L.F("云端音乐：{0} - {1} 正在获取…", song.Singer, song.Name);
        try
        {
            var loaded = await CloudMusicService.LoadAsync(song, dlg.Quality,
                msg => TblStatus.Text = L.T("云端音乐：") + msg);
            LoadAudioFile(loaded.AudioPath);

            if (!string.IsNullOrWhiteSpace(loaded.Lrc))
            {
                var lrc = loaded.Lrc;
                if (!lrc.Contains("[ti:", StringComparison.OrdinalIgnoreCase))
                    lrc = $"[ti:{song.Name}]\n[ar:{song.Singer}]\n" + lrc;
                LoadLyrics(lrc);
            }

            _saveError = SaveProjectToCurrent();
            RefreshMediaPanel();
            RefreshTimeline();
            TblStatus.Text = L.F("已导入云端音乐：{0} - {1}{2}", song.Singer, song.Name, loaded.Note);
            Log.Info($"[Cloud] 已导入云端音乐：{song.Singer} - {song.Name} → {loaded.AudioPath}");
        }
        catch (Exception ex)
        {
            TblStatus.Text = L.T("云端音乐导入失败：") + ex.Message;
            Log.Warn("[Cloud] 云端音乐导入失败：" + ex);
        }
    }
}
