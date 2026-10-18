using System.Windows;
using System.Windows.Controls;

namespace JizuraLive.App.PvStage;

public partial class PvStageWindow
{
    internal void TglCenterFree_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingSettings || _project is null) return;

        _lyricsAvoid = TglCenterFree.IsChecked == true;
        if (_lyricsAvoid && Preset.CenterFree)
        {
            Preset.CenterFree = false;
            PresetChanged();
        }

        _pipe?.SetLyricsAvoid(_lyricsAvoid, _lyricsAvoidMinScale);
        SaveLyricsAvoidSettings();
    }

    internal void SldCenterGap_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingSettings || _project is null) return;

        _lyricsAvoidMinScale = Math.Clamp(e.NewValue / 100.0, 0.3, 1.0);
        if (TblCenterGap is not null) TblCenterGap.Text = $"{Math.Round(e.NewValue):0}%";
        _pipe?.SetLyricsAvoid(_lyricsAvoid, _lyricsAvoidMinScale);

        if (!SldCenterGap.IsMouseCaptureWithin) SaveLyricsAvoidSettings();
    }

    private void SaveLyricsAvoidSettings()
    {
        var s = App.SettingsOf();
        if (s is null) return;
        s.LyricsAvoid = _lyricsAvoid;
        s.LyricsAvoidMinScale = _lyricsAvoidMinScale;
        s.Save();
    }
}
