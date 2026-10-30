using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using JizuraLive.App.Cloud;
using JizuraLive.Logging;
using JizuraLive.Server.Models;
using JizuraLive.App.I18n;

namespace JizuraLive.App.PvStage;

public partial class PvStageWindow
{
    private KgSong? _cloudSong;
    private CloudLyricOption? _cloudLyricOption;
    private CloudMusicWindow? _cloudMusicWindow;
    private bool _cloudImporting;

    internal void BtnCloudMusic_Click(object sender, RoutedEventArgs e)
    {
        if (_cloudMusicWindow is { IsLoaded: true } open)
        {
            open.Activate();
            return;
        }

        var cloud = new CloudMusicWindow();
        cloud.ImportRequested += (_, _) => OnCloudMusicImportRequested(cloud);
        cloud.Closed += (_, _) =>
        {
            if (ReferenceEquals(_cloudMusicWindow, cloud)) _cloudMusicWindow = null;
        };
        _cloudMusicWindow = cloud;
        cloud.Show();
    }

    private async void OnCloudMusicImportRequested(CloudMusicWindow window)
    {
        if (window.SelectedSong is not { } song) return;
        await ImportCloudSongAsync(song, window.Quality, window.SelectedLyricOption);
    }

    private async Task ImportCloudSongAsync(KgSong song, string quality, CloudLyricOption? lyricOption)
    {
        if (_cloudImporting) return;
        _cloudImporting = true;
        TblStatus.Text = L.F("StageUi.Stage.CloudMusicFetching", song.Singer, song.Name);
        try
        {
            var loaded = await CloudMusicService.LoadAsync(song, quality, lyricOption,
                msg => TblStatus.Text = L.T("StageUi.Stage.CloudMusicPrefix") + msg);
            if (!LoadAudioFile(loaded.AudioPath)) return;

            SetCloudSong(song, lyricOption);
            ApplyCloudLyrics(song, loaded.Lrc);

            _saveError = SaveProjectToCurrent();
            RefreshMediaPanel();
            RefreshTimeline();
            TblStatus.Text = L.F("StageUi.Stage.CloudMusicImported", song.Singer, song.Name, loaded.Note)
                             + L.T("StageUi.Stage.CloudLyricSwapHint");
            Log.Info($"[Cloud] 已导入云端音乐：{song.Singer} - {song.Name} → {loaded.AudioPath}");
        }
        catch (Exception ex)
        {
            TblStatus.Text = L.T("StageUi.Stage.CloudMusicImportFailed") + ex.Message;
            Log.Warn("[Cloud] 云端音乐导入失败：" + ex);
        }
        finally
        {
            _cloudImporting = false;
        }
    }

    private void SetCloudSong(KgSong? song, CloudLyricOption? option = null)
    {
        _cloudSong = song;
        _cloudLyricOption = option;
        Left.BtnCloudLyricVersion.Visibility = song is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyCloudLyrics(KgSong song, string lrc)
    {
        if (string.IsNullOrWhiteSpace(lrc)) return;
        if (!lrc.Contains("[ti:", StringComparison.OrdinalIgnoreCase))
            lrc = $"[ti:{song.Name}]\n[ar:{song.Singer}]\n" + lrc;
        LoadLyrics(lrc);
    }

    internal async void BtnCloudLyricVersion_Click(object sender, RoutedEventArgs e)
    {
        if (_cloudSong is not { } song || sender is not FrameworkElement anchor) return;

        TblStatus.Text = L.F("StageUi.Stage.CloudLyricVersionsLoading", song.Singer, song.Name);
        List<CloudLyricOption> options;
        try
        {
            options = await CloudMusicService.GetLyricOptionsAsync(song);
        }
        catch (Exception ex)
        {
            TblStatus.Text = L.T("StageUi.Stage.CloudLyricVersionsFailed") + ex.Message;
            Log.Warn("[Cloud] 歌词版本获取失败：" + ex);
            return;
        }

        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (var option in options)
        {
            var item = new MenuItem
            {
                Header = option.Label,
                IsCheckable = true,
                IsChecked = option == _cloudLyricOption,
            };
            item.Click += async (_, _) => await ApplyCloudLyricVersionAsync(song, option);
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
        TblStatus.Text = L.F("StageUi.Stage.CloudLyricVersionsReady", song.Singer, song.Name);
    }

    private async Task ApplyCloudLyricVersionAsync(KgSong song, CloudLyricOption option)
    {
        TblStatus.Text = L.F("StageUi.Stage.CloudLyricApplying", option.Label);
        try
        {
            var lrc = await CloudMusicService.LoadLyricsAsync(song, option);
            if (string.IsNullOrWhiteSpace(lrc))
            {
                TblStatus.Text = L.F("StageUi.Stage.CloudLyricUnavailable", option.Label);
                return;
            }

            ApplyCloudLyrics(song, lrc);
            _cloudLyricOption = option;
            _saveError = SaveProjectNow();            RefreshMediaPanel();
            RefreshTimeline();
            TblStatus.Text = L.F("StageUi.Stage.CloudLyricApplied", option.Label, _lyrics?.Count ?? 0);
            Log.Info($"[Cloud] 已切换歌词版本：{song.Singer} - {song.Name} → {option.Label}");
        }
        catch (Exception ex)
        {
            TblStatus.Text = L.T("StageUi.Stage.CloudLyricFailed") + ex.Message;
            Log.Warn("[Cloud] 歌词版本切换失败：" + ex);
        }
    }
}
