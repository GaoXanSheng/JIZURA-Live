using System.Windows;
using System.Windows.Controls;

namespace JizuraLive.App.PvStage;

public partial class PvStageWindow
{
    internal void TglCenterFree_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingSettings || _project is null) return;

        _project.Preset.CenterFree = TglCenterFree.IsChecked == true;
        PresetChanged();
    }

    internal void CmbCenterZone_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSettings || _project is null) return;
        if (CmbCenterZone.SelectedItem is not ComboBoxItem { Tag: string zone }) return;

        _project.Preset.CenterZone = zone;
        PresetChanged();
    }

    internal void SldCenterGap_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingSettings || _project is null) return;

        _project.Preset.CenterGap = Math.Round(e.NewValue);
        if (TblCenterGap is not null) TblCenterGap.Text = $"{Math.Round(e.NewValue):0}%";
        PresetChanged();
    }

    private void SelectCenterZone(string? zone)
    {
        var key = string.IsNullOrWhiteSpace(zone) ? "center" : zone;
        foreach (var item in CmbCenterZone.Items)
        {
            if (item is ComboBoxItem { Tag: string tag } && tag == key)
            {
                CmbCenterZone.SelectedItem = item;
                return;
            }
        }

        CmbCenterZone.SelectedIndex = 0;
    }
}
