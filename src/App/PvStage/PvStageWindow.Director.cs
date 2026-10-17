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

    private async void BtnRegenerateAuto_Click(object sender, RoutedEventArgs e)
    {
        if (_autoGenerating) return;
        _autoSeed++;
        await GenerateAutoAsync();
    }

    private async void BtnGenerateAuto_Click(object sender, RoutedEventArgs e) => await GenerateAutoAsync();

    private async Task GenerateAutoAsync()
    {
        if (_autoGenerating) return;

        var audioPath = _audioPath;
        var cachedMap = _beatMap;
        var existingAnalysis = _audioBands;

        var what = _autoSeed > 1 ? $"换一版（第 {_autoSeed} 版）" : "一键成片";

        if (string.IsNullOrWhiteSpace(audioPath) && cachedMap is null && existingAnalysis is null)
        {
            ShowStatus($"{what}失败：请先导入音频");
            return;
        }

        _autoGenerating = true;
        BtnGenerateAuto.IsEnabled = false;
        BtnRegenerateAuto.IsEnabled = false;
        BtnGenerateAuto.Content = "生成中…";
        ShowStatus($"{what}：正在分析音频节拍与能量…");

        string style = "dynamic";
        if (CmbAutoStyle.SelectedItem is ComboBoxItem item && item.Tag is string s)
            style = s;
        double intensity = SldAutoIntensity.Value;
        var options = new AutoOptions { Style = style, Intensity = intensity, Seed = _autoSeed };

        try
        {
            var (beatMap, analysisResult, error) = await Task.Run<(BeatMap? BeatMap, AudioBandSeries? Bands, string? Error)>(() =>
            {
                try
                {
                    var map = cachedMap;
                    var bands = existingAnalysis;
                    if ((map is null || bands is null) && (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath)))
                        return (map, bands, "音频文件不存在，无法分析");

                    if (bands is null)
                    {
                        bands = AudioFileAnalyzer.Analyze(audioPath!, CancellationToken.None);
                        if (bands is null) return (map, null, "音频频带分析失败");
                    }

                    map ??= SongAnalyzer.AnalyzeFile(audioPath!, CancellationToken.None);
                    return (map, bands, null);
                }
                catch (Exception ex)
                {
                    return (null, null, ex.Message);
                }
            });

            if (beatMap is null)
            {
                ShowStatus($"{what}失败：{error ?? "未知错误"}");
                return;
            }

            _beatMap = beatMap;

            if (_audioBands is null && analysisResult is AudioBandSeries abs)
            {
                _audioBands = abs;
                PublishAudioBands();
                RefreshParamRows();
            }

            var report = await Task.Run(() => AutoDirector.Generate(_project, beatMap, options));

            _evaluator = DirectorEvaluator.FromSaves(_project.Curves, _project.CameraTrack);
            EffectsChanged();
            PublishDirector();

            if (_pipe is not null)
            {
                _rebuildPipeKeepTransport = true;
                _rebuildPipe = true;
            }

            PublishLyricClips(_lyricClips);

            BuildEffectRack();
            BuildLayerList();
            TheTimeline.Fps = _scene.Fps;
            RefreshTimeline();
            BuildInspector();
            SyncLyricStylePicker();

            PushUndo();

            int particleLayers = _project.Scene.Layers.Count(l => l.Particles is not null);
            int styleSpans = _project.LyricStyles.Count;

            TblAutoReport.Text =
                $"第 {_autoSeed} 版（种子 {_autoSeed}）\n"
                + $"BPM {beatMap.Bpm:F1} · 段落 {beatMap.Sections.Count} 段 · 节拍 {beatMap.Beats.Count} 个\n"
                + $"段落序列 {SectionSequence(beatMap)}\n"
                + $"分镜 {report.CameraShots} 个 · 曲线 {report.CurveKeys} 键 · 效果实例 {report.EffectInstances} 个 · 出场 {report.EffectWindows} 段\n"
                + $"粒子层 {particleLayers} 层 · 歌词样式 {styleSpans} 段";
            var note = $"{what}完成：第 {_autoSeed} 版 · 分镜 {report.CameraShots} 个 · 特效 {report.EffectInstances} 个（出场 {report.EffectWindows} 段）"
                       + $" · 粒子层 {particleLayers} · 歌词样式 {styleSpans} 段";
            ShowStatus(note);
            Log.Info($"[PV 工作台] {note}");
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
    }
}
