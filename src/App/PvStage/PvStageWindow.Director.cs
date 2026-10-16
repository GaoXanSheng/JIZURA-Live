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

/// <summary>
/// 导演页接线：一键生成 / 换一版（同一套生成路径，只差种子）＋ 生成物落地到各消费方的四步。
/// </summary>
public partial class PvStageWindow
{
    // 「换一版」的版本号：从 1 起，每次 +1 后走同一套生成路径。不落盘——种子不进工程，
    // 免得重开工程看到的「第 N 版」与磁盘上那份生成结果对不上。
    private int _autoSeed = 1;

    /// <summary>换一版：种子 +1 再走一次与「一键生成」完全相同的路径（同种子必得同一版）。</summary>
    private async void BtnRegenerateAuto_Click(object sender, RoutedEventArgs e)
    {
        if (_autoGenerating) return;
        _autoSeed++;
        await GenerateAutoAsync();
    }

    /// <summary>一键成片：后台节拍与能量分析 → 自动导演生成 → 单次可撤销事务 → 刷新时间轴与检查器。</summary>
    private async void BtnGenerateAuto_Click(object sender, RoutedEventArgs e) => await GenerateAutoAsync();

    private async Task GenerateAutoAsync()
    {
        if (_autoGenerating) return;

        var audioPath = _audioPath;
        var cachedMap = _beatMap;
        var existingAnalysis = _audioBands;

        // 报错文案跟按钮走：第 1 版是「一键成片」，之后都是「换一版」
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
            // 导入时已分析过的就用缓存，不再解一遍整曲；缺哪一份（图/频带）才补哪一份
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

            // UI 线程调 AutoDirector.Generate 写进工程
            var report = AutoDirector.Generate(_project, beatMap, options);

            // 同步工程曲线至 _evaluator 并发布给管线
            _evaluator = DirectorEvaluator.FromSaves(_project.Curves);
            // 生成整份重写了效果实例（AutoDirector 原地改的是 _project.Effects == _effects），
            // 所以效果图与曲线两样都要发布
            EffectsChanged();
            PublishDirector();

            // 导演在图层栈里增删了粒子层，而图层提供者是管线构造期按引用一次性注册的，
            // 运行期没有任何入口能补注册一棵带配置的层（RequestAddParticles 只收种类名）：
            // 只能走换工程那条整条重建的路，但音源与离线分析不动——换一版不该把播放头归零、
            // 把波形/频带/节拍图全部作废重算。置位必须在 EffectsChanged 之后：重建吃的是刚发布的克隆。
            if (_pipe is not null)
            {
                _rebuildPipeKeepTransport = true;
                _rebuildPipe = true;
            }

            // 逐段歌词样式与 VTS cue 是生成物里不在曲线/效果图上的两样，各自下发一次。
            // 歌词样式只存在工程的区间里（clip 不落盘），不重新套一遍，屏幕上还是上一版的样式。
            PublishLyricClips(_lyricClips);
            ReloadCueTrack();

            // 刷新效果架、图层、时间轴与检查器
            BuildEffectRack();
            BuildLayerList();
            TheTimeline.Fps = _scene.Fps;
            RefreshTimeline();
            BuildInspector();
            SyncLyricStylePicker();

            // 一次 PushUndo（整次生成一条撤销）
            PushUndo();

            int particleLayers = _project.Scene.Layers.Count(l => l.Particles is not null);
            int cueCount = _project.VtsCues.Cues.Count;
            int styleSpans = _project.LyricStyles.Count;

            // 分析结果与生成摘要留在「导演」面板上
            TblAutoReport.Text =
                $"第 {_autoSeed} 版（种子 {_autoSeed}）\n"
                + $"BPM {beatMap.Bpm:F1} · 段落 {beatMap.Sections.Count} 段 · 节拍 {beatMap.Beats.Count} 个\n"
                + $"段落序列 {SectionSequence(beatMap)}\n"
                + $"曲线 {report.CurveKeys} 键（相机 {report.CameraKeys}）· 效果实例 {report.EffectInstances} 个 · 出场 {report.EffectWindows} 段\n"
                + $"粒子层 {particleLayers} 层 · VTS cue {cueCount} 个 · 歌词样式 {styleSpans} 段";
            var note = $"{what}完成：第 {_autoSeed} 版 · 相机 {report.CameraKeys} 键 · 特效 {report.EffectInstances} 个（出场 {report.EffectWindows} 段）"
                       + $" · 粒子层 {particleLayers} · VTS cue {cueCount} · 歌词样式 {styleSpans} 段";
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
