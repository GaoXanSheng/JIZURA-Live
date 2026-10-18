using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using JizuraLive.Engine;
using JizuraLive.Logging;
using JizuraLive.Pv;

namespace JizuraLive.App.PvStage;

public partial class PvStageWindow
{
    private int _autoSeed = 1;
    private bool _syncingStageToggles;
    private LegacyPvPlanInfo? _legacyPlanInfo;

    internal void StageToggles_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingStageToggles) return;

        _project.LegacyStageCamera = TglStageCamera.IsChecked == true;
        _project.LegacyStageTransitions = TglStageTransitions.IsChecked == true;

        if (_pipe is not null)
        {
            _pipe.StageCameraEnabled = _project.LegacyStageCamera;
            _pipe.StageTransitionsEnabled = _project.LegacyStageTransitions;
        }

        PublishDirector();
        SaveProjectNow();
        ShowStatus($"原版引擎叠加：整体运镜 {OnOff(_project.LegacyStageCamera)}"
                   + $" · 整体转场 {OnOff(_project.LegacyStageTransitions)}");
    }

    private static string OnOff(bool on) => on ? "开" : "关";

    private void SyncStageToggles()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(SyncStageToggles);
            return;
        }

        _syncingStageToggles = true;
        try
        {
            TglStageCamera.IsChecked = _project.LegacyStageCamera;
            TglStageTransitions.IsChecked = _project.LegacyStageTransitions;
        }
        finally
        {
            _syncingStageToggles = false;
        }
    }

    private string SelectedAutoStyle()
    {
        if (CmbAutoStyle.SelectedItem is ComboBoxItem { Tag: string s }) return s;
        return "dynamic";
    }

    private void ApplyLegacyPlan(LegacyPvPlanInfo info, bool force = false)
    {
        if (_pipe is null || info.Cuts.Count == 0) return;
        if (!force && ReferenceEquals(_legacyPlanInfo, info)) return;

        var preserved = CaptureUserCutOverrides(_legacyPlanInfo);
        _legacyPlanInfo = info;
        if (_beatMap is null && info.Duration > 0.01) _beatMap = info.ToBeatMap();
        var report = AutoDirector.GenerateLegacyStage(_project, info, _beatMap, new AutoOptions
        {
            Style = SelectedAutoStyle(),
            Intensity = SldAutoIntensity.Value,
            Seed = _project.LegacySeed,
        });

        ApplyPreservedCutOverrides(info, preserved);

        _evaluator = DirectorEvaluator.FromSaves(_project.Curves, _project.CameraTrack, _project.LayerCameraTracks);
        PublishDirector();

        _pipe.StageCameraEnabled = _project.LegacyStageCamera;
        _pipe.StageTransitionsEnabled = _project.LegacyStageTransitions;
        if (IsLoaded)
        {
            BuildLayerList();
            RefreshTimeline();
            BuildInspector();
            SyncPresetControls();
            _saveError = SaveProjectNow();
        }

        string cameraLine = _project.LegacyStageCamera ? $"运镜 {report.CameraShots} 个" : "运镜：关";
        string transLine = _project.LegacyStageTransitions ? $"转场 {report.TransSegments} 段" : "转场：关";

        TblAutoReport.Text =
            (_beatMap is { } bm ? $"BPM {bm.Bpm:F1} · 段落 {bm.Sections.Count} 段 · " : "")
            + $"原版 PV 计划 · {info.Cuts.Count} 个切分 · 时长 {info.Duration:F1}s\n"
            + $"舞台叠加：{cameraLine} · {transLine}（角色不单独位移，随镜头整体移动）\n"
            + $"布局 / 入退场 / 文字处理 / 引擎转场由原版引擎生成";

        ShowStatus($"原版引擎就绪：{info.Cuts.Count} 切分 · {cameraLine} · {transLine}");
    }

    private static Dictionary<double, LegacyPvCutInfo> CaptureUserCutOverrides(LegacyPvPlanInfo? info)
    {
        var map = new Dictionary<double, LegacyPvCutInfo>();
        if (info?.Cuts is null) return map;

        foreach (var cut in info.Cuts)
        {
            if (cut is null || !string.Equals(cut.Source, "user", StringComparison.OrdinalIgnoreCase)) continue;
            map[Math.Round(cut.Start, 3)] = new LegacyPvCutInfo
            {
                Layout = cut.Layout,
                Enter = cut.Enter,
                Exit = cut.Exit,
                Treat = cut.Treat,
            };
        }

        return map;
    }

    private void ApplyPreservedCutOverrides(LegacyPvPlanInfo info, Dictionary<double, LegacyPvCutInfo> preserved)
    {
        if (preserved.Count == 0) return;

        var applied = false;
        foreach (var cut in info.Cuts)
        {
            if (cut is null || !preserved.TryGetValue(Math.Round(cut.Start, 3), out var src)) continue;

            if (!string.IsNullOrWhiteSpace(src.Layout)) cut.Layout = src.Layout;
            if (!string.IsNullOrWhiteSpace(src.Enter)) cut.Enter = src.Enter;
            if (!string.IsNullOrWhiteSpace(src.Exit)) cut.Exit = src.Exit;
            cut.Treat = string.IsNullOrWhiteSpace(src.Treat) ? "none" : src.Treat;
            cut.Source = "user";
            applied = true;
        }

        if (applied) _pipe?.ApplyLegacyCutOverrides(info);
    }

    internal async void BtnRegenerateAuto_Click(object sender, RoutedEventArgs e)
    {
        if (_autoGenerating) return;
        _autoSeed++;
        await GenerateAutoAsync();
    }

    internal async void BtnGenerateAuto_Click(object sender, RoutedEventArgs e) => await GenerateAutoAsync();

    private Task GenerateAutoAsync()
    {
        if (_autoGenerating) return Task.CompletedTask;

        var what = _autoSeed > 1 ? $"换一版（第 {_autoSeed} 版）" : "一键成片";

        if (string.IsNullOrWhiteSpace(_audioPath) || !File.Exists(_audioPath))
        {
            ShowStatus($"{what}失败：请先导入音频");
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(_lrcText))
        {
            ShowStatus($"{what}失败：请先导入歌词");
            return Task.CompletedTask;
        }

        _autoGenerating = true;
        BtnGenerateAuto.IsEnabled = false;
        BtnRegenerateAuto.IsEnabled = false;
        BtnGenerateAuto.Content = "生成中…";
        ShowStatus($"{what}：正在重建原版引擎 Plan…");

        _autoSeed = Math.Max(1, _autoSeed);
        try
        {
            _project.LegacySeed = unchecked(20260922 + _autoSeed * 104729);
            SyncLyricStylePicker();
            SyncPresetControls();

            _effects.Nodes.Clear();
            _project.CameraTrack ??= new CameraTrack();
            _project.CameraTrack.Shots.Clear();
            _project.DesignSegments.RemoveAll(seg => seg.Track == DesignTrackKind.Trans);
            _evaluator = DirectorEvaluator.FromSaves(null, _project.CameraTrack, _project.LayerCameraTracks);
            EffectsChanged();

            SaveProjectNow();

            if (_pipe is not null)
            {
                _rebuildPipeKeepTransport = true;
                _rebuildPipe = true;
            }

            SyncStageToggles();
            PushUndo();

            string mapLine = _beatMap is { } bm
                ? $"BPM {bm.Bpm:F1} · 段落 {bm.Sections.Count} 段 · 节拍 {bm.Beats.Count} 个\n"
                : "";
            TblAutoReport.Text =
                $"原版引擎 · 第 {_autoSeed} 版（种子 {_project.LegacySeed}）\n"
                + mapLine
                + $"主题 {ThemeLabel(_project.LegacyStyle)} · 正在按原版 Plan 切点缝入角色运动 / 整体运镜 / 整体转场…";
            var legacyNote = $"{what}：已重排原版引擎 · 第 {_autoSeed} 版 · 种子 {_project.LegacySeed} · 主题 {ThemeLabel(_project.LegacyStyle)}";
            ShowStatus(legacyNote);
            Log.Info($"[PV 工作台] {legacyNote}");
        }
        catch (Exception ex)
        {
            TblAutoReport.Text = "生成失败：" + ex.Message;
            ShowStatus($"{what}失败：{ex.Message}");
            Log.Warn("[PV 工作台] 一键成片异常：" + ex);
        }
        finally
        {
            _autoGenerating = false;
            BtnGenerateAuto.Content = "一键生成";
            UpdateAutoButton();
        }

        return Task.CompletedTask;
    }
}
