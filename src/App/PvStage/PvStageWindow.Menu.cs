using System.Windows;
using System.Windows.Controls;
using JizuraLive.App.I18n;
using JizuraLive.Pv;

namespace JizuraLive.App.PvStage;

public partial class PvStageWindow
{
    private bool _syncingTlZoom;

    private void MenuNewProject_Click(object sender, RoutedEventArgs e) =>
        BtnNewProject_Click(this, new RoutedEventArgs());

    private void MenuOpenProject_Click(object sender, RoutedEventArgs e) =>
        BtnOpenProject_Click(this, new RoutedEventArgs());

    private void MenuSave_Click(object sender, RoutedEventArgs e) => SaveNow();

    private void MenuSaveAs_Click(object sender, RoutedEventArgs e) => SaveProjectAs();

    private void MenuImportAudio_Click(object sender, RoutedEventArgs e) =>
        BtnOpenAudio_Click(this, new RoutedEventArgs());

    private void MenuImportLyrics_Click(object sender, RoutedEventArgs e) =>
        BtnOpenLyrics_Click(this, new RoutedEventArgs());

    private void MenuImportImage_Click(object sender, RoutedEventArgs e) => ImportImageLayer();

    private void MenuCloudMusic_Click(object sender, RoutedEventArgs e) =>
        BtnCloudMusic_Click(this, new RoutedEventArgs());

    private void MenuExit_Click(object sender, RoutedEventArgs e) => Close();

    private void MenuUndo_Click(object sender, RoutedEventArgs e) => UndoRedo(true);

    private void MenuRedo_Click(object sender, RoutedEventArgs e) => UndoRedo(false);

    private void DeleteLayer(PvLayerConfig layer)
    {
        if (IsEngineLayer(layer.Id))
        {
            ShowStatus(L.T("StageUi.Stage.EngineLayerCannotDelete"), 3);
            return;
        }

        var idRef = layer.Id;
        _evaluator.RemoveCurvesForTarget(idRef);

        bool fxChanged = false;
        foreach (var node in _effects.Nodes)
        {
            if (node is null || !string.Equals(node.Target, idRef, StringComparison.Ordinal)) continue;
            node.Target = "";
            fxChanged = true;
        }

        if (fxChanged) EffectsChanged();
        PublishDirector();

        var pipe = _pipe;
        if (pipe is not null)
        {
            pipe.RequestRemove(idRef);
            return;
        }

        var index = _scene.Layers.FindIndex(l => l.Id == idRef);
        if (index < 0) return;
        _scene.Layers = PvPipeline.WithLayerRemovedAt(_scene.Layers, index);
        BuildLayerList();
    }

    private void MenuToggleInspector_Click(object sender, RoutedEventArgs e) => ToggleInspector();

    private void MenuToggleLeftPanel_Click(object sender, RoutedEventArgs e) => ToggleLeftPanel();

    private void MenuToggleTimeline_Click(object sender, RoutedEventArgs e) => ToggleTimeline();

    private void MenuExpandTimeline_Click(object sender, RoutedEventArgs e) =>
        TheTimeline.ExpandAllGroups();

    private void MenuCollapseTimeline_Click(object sender, RoutedEventArgs e) =>
        TheTimeline.CollapseAllGroups();

    private void MenuFitTimeline_Click(object sender, RoutedEventArgs e) =>
        TheTimeline.ZoomToFit();

    private void MenuResetLayout_Click(object sender, RoutedEventArgs e)
    {
        _inspectorCollapsed = false;
        _leftCollapsed = false;
        _timelineCollapsed = false;
        LeftColumn.MinWidth = 240;
        LeftColumn.Width = new GridLength(320);
        Left.Visibility = Visibility.Visible;
        LeftSplitter.Visibility = Visibility.Visible;
        LeftSplitterColumn.Width = new GridLength(4);
        InspectorColumn.MinWidth = 340;
        InspectorColumn.Width = new GridLength(InspectorWidth);
        InspectorSplitterColumn.Width = new GridLength(6);
        InspectorPane.Visibility = Visibility.Visible;
        InspectorSplitter.Visibility = Visibility.Visible;
        TimelineRow.MinHeight = 80;
        TimelineRow.Height = new GridLength(300);
        TheTimeline.Visibility = Visibility.Visible;
        TheTimeline.ZoomToFit();
        TheTimeline.CollapseAllGroups();
    }

    private void MenuShortcuts_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "播放\n"
            + "空格          播放 / 暂停\n"
            + "J / K / L     倒放 / 暂停 / 正放（连按 J 加快速率）\n"
            + "Home / End    跳到开头 / 结尾\n\n"
            + "定位\n"
            + "← / →         上一帧 / 下一帧\n"
            + "Shift + ←/→   十帧\n"
            + "Ctrl + ←/→    一秒\n"
            + "↑ / ↓         上一个 / 下一个剪辑点\n"
            + "PageUp/Down   上一个 / 下一个关键帧\n\n"
            + "时间轴\n"
            + "+ / -         放大 / 缩小\n"
            + "Shift + Z     适配窗口\n"
            + "Ctrl + 0      适配窗口\n"
            + "S             吸附开关\n"
            + "Alt           拖动时临时关闭吸附\n"
            + "滚轮          上下滚动轨道（无溢出时左右平移）\n"
            + "Shift / Alt + 滚轮  左右平移\n"
            + "Ctrl + 滚轮   以鼠标位置缩放\n"
            + "中键拖动      平移时间轴\n"
            + "Delete        删除选中的关键帧 / 片段\n"
            + "Ctrl + C/V    复制 / 粘贴关键帧\n\n"
            + "编辑\n"
            + "Ctrl + Z      撤销\n"
            + "Ctrl + Shift + Z / Ctrl + Y  重做\n\n"
            + "文件\n"
            + "Ctrl + N      新建项目\n"
            + "Ctrl + O      打开项目\n"
            + "Ctrl + S      保存\n"
            + "Ctrl + Shift + S  另存为\n"
            + "Ctrl + I      导入音频\n"
            + "Ctrl + L      导入歌词\n\n"
            + "帮助\n"
            + "F1            快捷键一览",
            L.T("StageUi.Stage.MenuShortcuts"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void MenuAbout_Click(object sender, RoutedEventArgs e)
    {
        var version = typeof(PvStageWindow).Assembly.GetName().Version?.ToString() ?? "1.0";
        MessageBox.Show(this,
            $"JIZURA Live PV {version}\n\n歌词 PV 与 VTS 虚拟角色特效联动工作台。",
            L.T("StageUi.Stage.MenuAbout"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnPrevFrame_Click(object sender, RoutedEventArgs e) => SeekFrames(-1);

    private void BtnNextFrame_Click(object sender, RoutedEventArgs e) => SeekFrames(1);

    private void BtnTlSnap_Click(object sender, RoutedEventArgs e) => ToggleSnap();

    private void ToggleSnap()
    {
        bool on = TheTimeline.ToggleSnap();
        if (BtnTlSnap is not null)
        {
            BtnTlSnap.Appearance = on
                ? Wpf.Ui.Controls.ControlAppearance.Primary
                : Wpf.Ui.Controls.ControlAppearance.Secondary;
        }
        ShowStatus(on ? L.T("StageUi.Stage.SnapOn") : L.T("StageUi.Stage.SnapOff"), 2);
    }

    private void SeekFrames(int frames)
    {
        StopShuttle();
        double step = 1.0 / Math.Max(1, _scene.Fps);
        double max = Math.Max(0, _audio.Duration);
        _audio.Seek(Math.Clamp(_audio.Time + frames * step, 0, max));
    }

    private void BtnTlExpand_Click(object sender, RoutedEventArgs e) => TheTimeline.ExpandAllGroups();

    private void BtnTlCollapse_Click(object sender, RoutedEventArgs e) => TheTimeline.CollapseAllGroups();

    private void BtnTlZoomIn_Click(object sender, RoutedEventArgs e) => TheTimeline.ZoomIn();

    private void BtnTlZoomOut_Click(object sender, RoutedEventArgs e) => TheTimeline.ZoomOut();

    private void BtnTlFit_Click(object sender, RoutedEventArgs e) => TheTimeline.ZoomToFit();

    private void SldTlZoom_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingTlZoom) return;
        TheTimeline.SetZoomFraction(e.NewValue / 100.0);
    }

    private void SyncTimelineZoom(double fraction)
    {
        if (SldTlZoom is null) return;
        _syncingTlZoom = true;
        try
        {
            SldTlZoom.Value = Math.Clamp(fraction * 100, SldTlZoom.Minimum, SldTlZoom.Maximum);
        }
        finally
        {
            _syncingTlZoom = false;
        }
    }
}
