using System.Windows;

namespace JizuraLive.App.PvStage;

public partial class PvStageWindow
{
    private const double ExportAudioOffsetSec = 0;

    private bool HasExportAudio => !string.IsNullOrWhiteSpace(_audioPath);

    private void SyncExportAudioToggle()
    {
        ChkIncludeAudio.IsEnabled = HasExportAudio;
        if (!HasExportAudio) ChkIncludeAudio.IsChecked = false;
        else if (!_exportFormEdited) ChkIncludeAudio.IsChecked = true;
    }

    private void ChkIncludeAudio_Click(object sender, RoutedEventArgs e) => _exportFormEdited = true;
}
