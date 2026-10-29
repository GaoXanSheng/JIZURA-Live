using System.Windows;
using JizuraLive.App.Cloud;
using JizuraLive.Logging;
using JizuraLive.App.I18n;

namespace JizuraLive.App.PvStage;

public partial class PvStageWindow
{
    internal async void BtnCloudMusic_Click(object sender, RoutedEventArgs e) => await ImportCloudMusicAsync();

    private async Task ImportCloudMusicAsync()
    {
        var dlg = new CloudMusicWindow { Owner = this };
        if (dlg.ShowDialog() != true || dlg.SelectedSong is not { } song) return;

        TblStatus.Text = L.F("StageUi.Stage.CloudMusicFetching", song.Singer, song.Name);
        try
        {
            var loaded = await CloudMusicService.LoadAsync(song, dlg.Quality, dlg.SelectedLyricOption,
                msg => TblStatus.Text = L.T("StageUi.Stage.CloudMusicPrefix") + msg);
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
            TblStatus.Text = L.F("StageUi.Stage.CloudMusicImported", song.Singer, song.Name, loaded.Note);
            Log.Info($"[Cloud] 已导入云端音乐：{song.Singer} - {song.Name} → {loaded.AudioPath}");
        }
        catch (Exception ex)
        {
            TblStatus.Text = L.T("StageUi.Stage.CloudMusicImportFailed") + ex.Message;
            Log.Warn("[Cloud] 云端音乐导入失败：" + ex);
        }
    }
}
