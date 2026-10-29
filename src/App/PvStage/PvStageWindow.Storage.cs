using System.IO;
using System.Windows;
using JizuraLive.App.I18n;
using JizuraLive.App.UI;
using JizuraLive.Logging;

namespace JizuraLive.App.PvStage;

public partial class PvStageWindow
{
    internal void RefreshCacheStats()
    {
        var cache = Storage.DirSize(AppDirs.CacheDir);
        var audio = Storage.DirSize(AppDirs.AudioCacheDir);
        var covers = Storage.DirSize(Path.Combine(AppDirs.CacheDir, "covers")).Bytes
                     + Storage.DirSize(Path.Combine(AppDirs.CacheDir, "cloud-covers")).Bytes;
        var other = Math.Max(0, cache.Bytes - audio.Bytes - covers);
        var cloud = Storage.DirSize(AppDirs.CloudDir);
        var logs = Storage.DirSize(AppDirs.LogsDir);

        TblCacheStats.Text = L.F("StageUi.Settings.CacheStats",
            Storage.FormatSize(cache.Bytes), Storage.FormatSize(audio.Bytes),
            Storage.FormatSize(covers), Storage.FormatSize(other),
            Storage.FormatSize(cloud.Bytes), Storage.FormatSize(logs.Bytes));
    }

    internal async void BtnClearCache_Click(object sender, RoutedEventArgs e)
    {
        var cache = Storage.DirSize(AppDirs.CacheDir);
        var logs = Storage.DirSize(AppDirs.LogsDir);
        var files = cache.Files + logs.Files;
        var bytes = cache.Bytes + logs.Bytes;
        if (files == 0)
        {
            TblCacheState.Text = L.T("StageUi.Settings.CacheEmpty");
            RefreshCacheStats();
            return;
        }

        var confirmed = FluentDialog.Confirm(this,
            L.T("StageUi.Settings.CacheClearTitle"),
            L.F("StageUi.Settings.CacheClearConfirm", files, Storage.FormatSize(bytes)),
            danger: true);
        if (!confirmed) return;

        var keep = _audioPath;
        BtnClearCache.IsEnabled = false;
        try
        {
            var (removed, freed) = await Task.Run(() => Storage.ClearCache(keep));
            TblCacheState.Text = L.F("StageUi.Settings.CacheCleared", removed, Storage.FormatSize(freed));
            Log.Info($"[存储] 已清空缓存与日志：{removed} 个文件，释放 {Storage.FormatSize(freed)}");
        }
        catch (Exception ex)
        {
            TblCacheState.Text = L.F("StageUi.Settings.CacheClearFailed", ex.Message);
            Log.Warn("[存储] 清空缓存与日志失败：" + ex.Message);
        }
        finally
        {
            BtnClearCache.IsEnabled = true;
            RefreshCacheStats();
        }
    }

    internal void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Storage.OpenFolder(AppDirs.CacheDir);
        }
        catch (Exception ex)
        {
            TblCacheState.Text = L.F("StageUi.Settings.OpenFolderFailed", ex.Message);
            Log.Warn("[存储] 打开缓存目录失败：" + ex.Message);
        }
    }
}
