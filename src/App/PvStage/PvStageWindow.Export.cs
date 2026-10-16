using System.Windows;

namespace JizuraLive.App.PvStage;

/// <summary>
/// 导出音轨开关。导出面板只有"包不包含音轨"这一项，起点固定为 0 秒（面板没有起点设置），
/// 编码参数（48kHz 2ch AAC 192kbps）由 <see cref="JizuraLive.Pv.MfSink"/> 定死。
/// </summary>
public partial class PvStageWindow
{
    /// <summary>导出时间轴 0 对应歌曲的第 0 秒。</summary>
    private const double ExportAudioOffsetSec = 0;

    /// <summary>工程里有没有音源：导入成功才会写 _audioPath。</summary>
    private bool HasExportAudio => !string.IsNullOrWhiteSpace(_audioPath);

    /// <summary>
    /// 没有音源就禁用且不勾（勾了也没东西可编）；有音源时按默认勾选，用户动过导出表单就不再回填。
    /// 状态刷新每 500ms 会走到这里，所以整段保持幂等。
    /// </summary>
    private void SyncExportAudioToggle()
    {
        ChkIncludeAudio.IsEnabled = HasExportAudio;
        if (!HasExportAudio) ChkIncludeAudio.IsChecked = false;
        else if (!_exportFormEdited) ChkIncludeAudio.IsChecked = true;
    }

    /// <summary>用户点过这个开关就不再算默认值（与导出表单的文本框同一条约定）。</summary>
    private void ChkIncludeAudio_Click(object sender, RoutedEventArgs e) => _exportFormEdited = true;
}
