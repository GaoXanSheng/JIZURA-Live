using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using JizuraLive.Engine;
using JizuraLive.Engine.Music;
using JizuraLive.Logging;
using JizuraLive.Pv;

namespace JizuraLive.App.PvStage;

public partial class PvStageWindow
{
    private int _autoSeed = 1;
    private bool _syncingStageToggles;
    private LegacyPvPlanInfo? _legacyPlanInfo;

    private void StageToggles_Changed(object sender, RoutedEventArgs e)
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

        _legacyPlanInfo = info;
        if (_beatMap is null && info.Duration > 0.01) _beatMap = info.ToBeatMap();
        var report = AutoDirector.GenerateLegacyStage(_project, info, _beatMap, new AutoOptions
        {
            Style = SelectedAutoStyle(),
            Intensity = SldAutoIntensity.Value,
            Seed = _project.LegacySeed,
        });

        _evaluator = DirectorEvaluator.FromSaves(_project.Curves, _project.CameraTrack);
        PublishDirector();

        _pipe.StageCameraEnabled = _project.LegacyStageCamera;
        _pipe.StageTransitionsEnabled = _project.LegacyStageTransitions;
        if (IsLoaded)
        {
            BuildLayerList();
            RefreshTimeline();
            BuildInspector();
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

    private async void BtnRegenerateAuto_Click(object sender, RoutedEventArgs e)
    {
        if (_autoGenerating) return;
        _autoSeed++;
        await GenerateAutoAsync();
    }

    private async void BtnGenerateAuto_Click(object sender, RoutedEventArgs e) => await GenerateAutoAsync();

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

        string style = SelectedAutoStyle();
        _autoSeed = Math.Max(1, _autoSeed);

        try
        {
            _project.LegacySeed = unchecked(20260922 + _autoSeed * 104729);
            _project.LegacyStyle = LegacyStyleFor(style);
            _scene.LyricStyleName = LegacyStyleToLyric(_project.LegacyStyle);
            SyncLyricStylePicker();
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
                + $"风格 {_project.LegacyStyle} · 正在按原版 Plan 切点缝入角色运动 / 整体运镜 / 整体转场…";
            var legacyNote = $"{what}：已重排原版引擎 · 第 {_autoSeed} 版 · 种子 {_project.LegacySeed} · 风格 {_project.LegacyStyle}";
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
