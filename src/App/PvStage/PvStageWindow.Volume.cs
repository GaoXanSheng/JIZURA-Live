using System.Windows;
using JizuraLive.App.I18n;

namespace JizuraLive.App.PvStage;

public partial class PvStageWindow
{
    private double _volume = 0.8;
    private bool _muted;
    private bool _syncingVolume;
    private bool _volumeReady;

    private void InitVolume()
    {
        var settings = App.SettingsOf();
        _volume = Math.Clamp(settings?.PreviewVolume ?? 0.8, 0, 1);
        _muted = settings?.PreviewMuted ?? false;
        _volumeReady = true;
        ApplyVolume();
    }

    private void ApplyVolume()
    {
        _audio.Volume = (float)_volume;
        _audio.Muted = _muted;
        UpdateVolumeUi();
    }

    private void UpdateVolumeUi()
    {
        if (SldVolume is null || TblVolume is null || VolumeIcon is null) return;

        _syncingVolume = true;
        try
        {
            SldVolume.Value = Math.Clamp(_volume * 100, 0, 100);
            TblVolume.Text = $"{Math.Round(_volume * 100):F0}%";
            VolumeIcon.Symbol = _muted
                ? Wpf.Ui.Controls.SymbolRegular.SpeakerMute24
                : Wpf.Ui.Controls.SymbolRegular.Speaker224;
        }
        finally
        {
            _syncingVolume = false;
        }
    }

    private void SldVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_volumeReady || _syncingVolume) return;

        _volume = Math.Clamp(e.NewValue / 100.0, 0, 1);
        _muted = false;
        ApplyVolume();
        SaveVolume();
    }

    private void BtnMute_Click(object sender, RoutedEventArgs e)
    {
        _muted = !_muted;
        if (!_muted && _volume <= 0.001) _volume = 0.8;
        ApplyVolume();
        SaveVolume();
        TblStatus.Text = _muted ? L.T("已静音") : L.F("音量 {0:F0}%", _volume * 100);
    }

    private void SaveVolume()
    {
        var settings = App.SettingsOf();
        if (settings is null) return;

        settings.PreviewVolume = _volume;
        settings.PreviewMuted = _muted;
        settings.SaveDebounced();
    }
}
