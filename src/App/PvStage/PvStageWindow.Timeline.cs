using JizuraLive.App.PvStage.Timeline;
using JizuraLive.Engine;
using JizuraLive.Logging;
using JizuraLive.Pv;

namespace JizuraLive.App.PvStage;

/// <summary>时间轴接线：快照组装、波形装载、seek 与编辑事务回写。</summary>
public partial class PvStageWindow
{
    /// <summary>时间轴的时间跨度（秒）：跟音频总长走；没音源时 30 秒占位，好过拿 0 当除数。</summary>
    private double TimelineSpan() => _audio.Duration > 0.5 ? _audio.Duration : 30;

    /// <summary>整份重建快照。只在模型变了时调（音频 / 歌词 / 曲线 / 波形 / 换工程）。</summary>
    private void RefreshTimeline()
    {
        var snap = new TSnapshot
        {
            DurationSeconds = TimelineSpan(),
            PlayheadSeconds = _audio.Time,
            Waveform = _peaks,
        };

        var camera = new TCurve { Address = _camScaleAddr, Label = "镜头 · 缩放", Min = ScaleMin, Max = ScaleMax };
        if (_evaluator.Curves.TryGetValue(_camScaleAddr, out var curve))
            foreach (var key in curve.Keys)
                camera.Keys.Add(new TKey(key.Time, key.Value, key.Mode.ToString()));
        snap.Curves.Add(camera);

        if (_lyrics is { } lyrics)
            foreach (var line in lyrics)
                snap.Lyrics.Add(new TClip
                {
                    Start = line.StartMs / 1000.0,
                    End = (line.StartMs + line.DurMs) / 1000.0,
                    Text = line.Text,
                });

        _snap = snap;
        TheTimeline.SetSnapshot(snap);
    }

    /// <summary>播放头原位刷（不重建快照）：拖动中的 key 归时间轴自己改，重建会把它顶掉。</summary>
    private void TickTimeline()
    {
        var snap = _snap;
        if (snap is null) return;
        snap.PlayheadSeconds = _audio.Time;
        TheTimeline.SetSnapshot(snap);
    }

    private void Timeline_SeekRequested(double seconds)
    {
        _audio.Seek(seconds);
        TickTimeline();
    }

    /// <summary>一次手势（After = 该轨完整 key 列表）写回求值器，再压一份撤销快照。</summary>
    private void Timeline_EditCommitted(TEdit edit)
    {
        if (!_evaluator.Curves.TryGetValue(edit.Address, out var curve))
        {
            curve = new FloatCurve();
            _evaluator.SetCurve(edit.Address, curve);
        }

        curve.Keys.Clear();
        foreach (var key in edit.After)
            curve.Keys.Add(new CurveKey(key.TimeSeconds, key.Value, ParseInterp(key.Interp)));
        // 空曲线 = 没有曲线：求值上等价，存档里留个空壳只会误导（与打 key 那条路径同一约定）
        if (curve.Count == 0) _evaluator.RemoveCurve(edit.Address);

        PublishDirector();
        RefreshCamBadge();
        PushUndo();
        _saveError = SaveProjectNow();
        TblStatus.Text = $"关键帧已{ReasonLabel(edit.Reason)} · {edit.After.Count} 个 key";
    }

    private static CurveInterp ParseInterp(string interp) =>
        Enum.TryParse<CurveInterp>(interp, out var mode) ? mode : CurveInterp.SmoothStep;

    private static string ReasonLabel(string reason) => reason switch
    {
        "add" => "新增",
        "move" => "移动",
        "delete" => "删除",
        "interp" => "改插值",
        "value" => "改值",
        _ => "更新",
    };

    /// <summary>
    /// 后台解码整段波形（<see cref="WaveformLoader.Load"/> 不碰播放设备）。
    /// 换音频 / 换工程时取消上一次：两份解码同时回来会让波形轨显示错的那一段。
    /// </summary>
    private void StartWaveformLoad(string path)
    {
        _waveformCts?.Cancel();
        var cts = new CancellationTokenSource();
        _waveformCts = cts;
        _peaks = null;
        RefreshTimeline();

        Task.Run(() =>
        {
            WaveformPeaks peaks;
            try
            {
                peaks = WaveformLoader.Load(path, cts.Token);
            }
            catch (Exception ex)
            {
                if (!cts.IsCancellationRequested) Log.Warn("[工作台] 波形装载失败：" + ex.Message);
                return;
            }

            Dispatcher.BeginInvoke(() =>
            {
                if (!ReferenceEquals(_waveformCts, cts)) return; // 期间又换过音频：这一份作废
                _peaks = peaks;
                RefreshTimeline();
            });
        });
    }
}
