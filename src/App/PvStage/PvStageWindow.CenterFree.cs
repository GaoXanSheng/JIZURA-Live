using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using JizuraLive.Logging;
using JizuraLive.Pv;

namespace JizuraLive.App.PvStage;

public partial class PvStageWindow
{
    private DispatcherTimer? _avoidDebounce;
    private bool _avoidHullSeen;
    private bool _avoidImageSeen;

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

    internal void TglSmartAvoid_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingSettings || _project is null) return;

        _project.Preset.SmartAvoid = TglSmartAvoid.IsChecked == true;
        PresetChanged();
    }

    private void ScheduleAvoidRefresh()
    {
        if (_project?.Preset.SmartAvoid != true) return;

        _avoidDebounce ??= CreateAvoidDebounce();
        _avoidDebounce.Stop();
        _avoidDebounce.Start();
    }

    private void NoteAvoidHull(bool hasHull)
    {
        if (!hasHull)
        {
            _avoidHullSeen = false;
            return;
        }

        if (_avoidHullSeen) return;
        _avoidHullSeen = true;
        ScheduleAvoidRefresh();
    }

    private void NoteAvoidImage()
    {
        if (_project?.Preset.SmartAvoid != true) return;

        var character = _scene.CharacterLayer;
        var ready = character is not null && _pipe is not null
                    && _pipe.Renderer.Stage.LastImages.TryGetValue(character.Id, out var image)
                    && image.HasContent;
        if (!ready)
        {
            _avoidImageSeen = false;
            return;
        }

        if (_avoidImageSeen) return;
        _avoidImageSeen = true;
        ScheduleAvoidRefresh();
    }

    private DispatcherTimer CreateAvoidDebounce()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            RequestPipelineRebuild();
        };
        return timer;
    }

    private PvAvoidSnapshot? CaptureAvoidSnapshot()
    {
        if (_project?.Preset.SmartAvoid != true) return null;

        var character = _scene.CharacterLayer;
        var front = _scene.Layers.FirstOrDefault(l => l is not null && l.Id == LegacyPvProvider.FrontId);
        if (character is null || front is null) return null;

        var images = _pipe?.Renderer.Stage.LastImages;
        var background = PvAvatarAvoid.FindBackground(_scene, images);
        var characterImage = _pipe is null ? default : _pipe.CaptureLayerImage(character.Id);
        var backgroundImage = default(PvLayerImage);
        if (images is not null && background is not null
            && images.TryGetValue(background.Id, out var backgroundFound))
            backgroundImage = backgroundFound;

        var vts = _pipe?.VtsControl ?? new PvVtsControl(false, 0, 0, 0, 0);
        var contour = vts.HasHull
            ? characterImage.Alpha is { Length: > 0 } ? "VTS+Alpha" : "VTS"
            : characterImage.Alpha is { Length: > 0 } ? "Alpha" : "无";
        var hullInfo = vts.HasHull
            ? $" 范围=[{vts.HullMinX:F3},{vts.HullMaxX:F3}]x[{vts.HullMinY:F3},{vts.HullMaxY:F3}]"
              + $" 点数={(vts.HullPoints?.Length ?? 0) / 2}"
            : "";
        Log.Info($"[智能避让] 快照：角色={character.Id} 轮廓={contour} 文字层={front.Id}{hullInfo}");
        return PvAvatarAvoid.Snapshot(_scene, _evaluator.Snapshot(), character, front, background,
            characterImage, backgroundImage, vts, _project.LegacyStageCamera);
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
