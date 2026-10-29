using System.Windows;
using System.Windows.Controls;

namespace JizuraLive.App.PvStage.Panels;

public partial class LeftPanel : UserControl
{
    public LeftPanel() => InitializeComponent();

    public PvStageWindow? Owner { get; set; }

    private void Nav_Click(object sender, RoutedEventArgs e) => Owner?.Nav_Click(sender, e);

    private void BtnCloudMusic_Click(object sender, RoutedEventArgs e) => Owner?.BtnCloudMusic_Click(sender, e);

    private void BtnAddLayer_Click(object sender, RoutedEventArgs e) => Owner?.BtnAddLayer_Click(sender, e);

    private void BtnFxAdd_Click(object sender, RoutedEventArgs e) => Owner?.BtnFxAdd_Click(sender, e);

    private void StageToggles_Changed(object sender, RoutedEventArgs e) => Owner?.StageToggles_Changed(sender, e);

    private void BtnGenerateAuto_Click(object sender, RoutedEventArgs e) => Owner?.BtnGenerateAuto_Click(sender, e);

    private void BtnRegenerateAuto_Click(object sender, RoutedEventArgs e) => Owner?.BtnRegenerateAuto_Click(sender, e);

    private void SldAutoIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        Owner?.SldAutoIntensity_ValueChanged(sender, e);

    private void CmbAutoStyle_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Owner?.CmbAutoStyle_SelectionChanged(sender, e);

    private void BtnExport_Click(object sender, RoutedEventArgs e) => Owner?.BtnExport_Click(sender, e);

    private void BtnExportBrowse_Click(object sender, RoutedEventArgs e) => Owner?.BtnExportBrowse_Click(sender, e);

    private void ChkIncludeAudio_Click(object sender, RoutedEventArgs e) => Owner?.ChkIncludeAudio_Click(sender, e);

    private void ExportField_TextChanged(object sender, TextChangedEventArgs e) => Owner?.ExportField_TextChanged(sender, e);

    private void CanvasField_TextChanged(object sender, TextChangedEventArgs e) => Owner?.CanvasField_TextChanged(sender, e);

    private void CmbCanvasPreset_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Owner?.CmbCanvasPreset_SelectionChanged(sender, e);

    private void CmbPvRender_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Owner?.CmbPvRender_SelectionChanged(sender, e);

    private void BtnApplyCanvas_Click(object sender, RoutedEventArgs e) => Owner?.BtnApplyCanvas_Click(sender, e);

    private void TglVSync_Changed(object sender, RoutedEventArgs e) => Owner?.TglVSync_Changed(sender, e);

    private void TglLegacyComposite_Changed(object sender, RoutedEventArgs e) =>
        Owner?.TglLegacyComposite_Changed(sender, e);

    private void TglPreviewSubtitles_Changed(object sender, RoutedEventArgs e) =>
        Owner?.TglPreviewSubtitles_Changed(sender, e);

    private void CmbSubContent_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Owner?.CmbSubContent_SelectionChanged(sender, e);

    private void CmbSubPos_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Owner?.CmbSubPos_SelectionChanged(sender, e);

    private void BtnVtsReconnect_Click(object sender, RoutedEventArgs e) => Owner?.BtnVtsReconnect_Click(sender, e);

    private void BtnVtsAuth_Click(object sender, RoutedEventArgs e) => Owner?.BtnVtsAuth_Click(sender, e);

    private void TglCenterFree_Changed(object sender, RoutedEventArgs e) => Owner?.TglCenterFree_Changed(sender, e);

    private void CmbCenterZone_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Owner?.CmbCenterZone_SelectionChanged(sender, e);

    private void SldCenterGap_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        Owner?.SldCenterGap_ValueChanged(sender, e);

    private void TglBroadcast_Changed(object sender, RoutedEventArgs e) => Owner?.TglBroadcast_Changed(sender, e);

    private void TglSpoutTransparent_Changed(object sender, RoutedEventArgs e) =>
        Owner?.TglSpoutTransparent_Changed(sender, e);

    private void BtnReseedPreset_Click(object sender, RoutedEventArgs e) => Owner?.BtnReseedPreset_Click(sender, e);

    private void CmbKoma_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Owner?.CmbKoma_SelectionChanged(sender, e);

    private void CmbHud_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Owner?.CmbHud_SelectionChanged(sender, e);

    private void CmbKeyBg_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Owner?.CmbKeyBg_SelectionChanged(sender, e);

    private void TxtTechFilter_TextChanged(object sender, TextChangedEventArgs e) =>
        Owner?.TxtTechFilter_TextChanged(sender, e);
}
