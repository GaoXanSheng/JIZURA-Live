using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using JizuraLive.Pv;

namespace JizuraLive.App.PvStage.Panels;

public partial class LeftPanel : UserControl
{
    public LeftPanel()
    {
        InitializeComponent();
        PreviewMouseLeftButtonDown += OnRootPreviewMouseLeftButtonDown;
    }

    public PvStageWindow? Owner { get; set; }

    private void OnRootPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (source is null) return;
        if (FindAncestor<Border>(source, b => b.Tag is PvLayerConfig || b.Tag is "fx") is not null) return;
        if (FindAncestor<ButtonBase>(source) is not null) return;
        if (FindAncestor<RangeBase>(source) is not null) return;
        if (FindAncestor<TextBoxBase>(source) is not null) return;
        if (FindAncestor<Selector>(source) is not null) return;
        if (FindAncestor<ScrollBar>(source) is not null) return;
        if (FindAncestor<Thumb>(source) is not null) return;
        if (FindAncestor<Expander>(source) is not null) return;
        Owner?.ClearLayerSelection();
    }

    private static T? FindAncestor<T>(DependencyObject? current, Func<T, bool>? match = null)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T typed && (match is null || match(typed))) return typed;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void Nav_Click(object sender, RoutedEventArgs e) => Owner?.Nav_Click(sender, e);

    private void BtnImportAudio_Click(object sender, RoutedEventArgs e) => Owner?.BtnOpenAudio_Click(sender, e);

    private void BtnImportLyrics_Click(object sender, RoutedEventArgs e) => Owner?.BtnOpenLyrics_Click(sender, e);

    private void BtnCloudMusic_Click(object sender, RoutedEventArgs e) => Owner?.BtnCloudMusic_Click(sender, e);

    private void BtnAddLayer_Click(object sender, RoutedEventArgs e) => Owner?.BtnAddLayer_Click(sender, e);

    private void BtnFxAdd_Click(object sender, RoutedEventArgs e) => Owner?.BtnFxAdd_Click(sender, e);

    private void StageToggles_Changed(object sender, RoutedEventArgs e) => Owner?.StageToggles_Changed(sender, e);

    private void BtnGenerateAuto_Click(object sender, RoutedEventArgs e) => Owner?.BtnGenerateAuto_Click(sender, e);

    private void SldAutoIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        Owner?.SldAutoIntensity_ValueChanged(sender, e);

    private void CmbCamSwitch_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Owner?.CmbCamSwitch_SelectionChanged(sender, e);

    private void SldCamBlend_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        Owner?.SldCamBlend_ValueChanged(sender, e);

    private void CmbAutoStyle_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Owner?.CmbAutoStyle_SelectionChanged(sender, e);

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

    private void BtnClearCache_Click(object sender, RoutedEventArgs e) => Owner?.BtnClearCache_Click(sender, e);

    private void BtnOpenFolder_Click(object sender, RoutedEventArgs e) => Owner?.BtnOpenFolder_Click(sender, e);

    private void BtnReseedPreset_Click(object sender, RoutedEventArgs e) => Owner?.BtnReseedPreset_Click(sender, e);

    private void CmbHud_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Owner?.CmbHud_SelectionChanged(sender, e);

    private void CmbKeyBg_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Owner?.CmbKeyBg_SelectionChanged(sender, e);

    private void TxtTechFilter_TextChanged(object sender, TextChangedEventArgs e) =>
        Owner?.TxtTechFilter_TextChanged(sender, e);
}
