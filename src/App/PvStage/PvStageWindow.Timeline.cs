using JizuraLive.App.PvStage.Timeline;
using JizuraLive.Engine;
using JizuraLive.Logging;
using JizuraLive.Pv;

namespace JizuraLive.App.PvStage;

/// <summary>时间轴接线：快照组装、音频装载、seek 与编辑事务回写。</summary>
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

        // 每条已有曲线一条轨（不只 camera.scale）。按地址文本排序：字典的枚举顺序会在删键之后变，
        // 不排序的话轨会自己换位置。
        foreach (var addr in _evaluator.Curves.Keys.OrderBy(a => a.ToString(), StringComparer.Ordinal))
        {
            var curve = _evaluator.Curves[addr];
            if (curve.Count == 0) continue; // 空壳曲线没有轨道可画（与"空曲线 = 没有曲线"同一约定）
            snap.Curves.Add(TrackFor(addr, curve));
        }

        // camera.scale 恒定留一条轨：还没有任何曲线时，它是唯一能靠双击打第一个 key 的地方
        if (!_evaluator.Curves.ContainsKey(_camScaleAddr))
            snap.Curves.Add(TrackFor(_camScaleAddr, null));

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

    /// <summary>一条自动化轨（<paramref name="curve"/> 为 null = 空白轨，只给双击打第一个 key 用）。</summary>
    private TCurve TrackFor(in ParamAddress addr, FloatCurve? curve)
    {
        var (min, max) = CurveRange(addr, curve);
        var track = new TCurve { Address = addr, Label = TrackLabel(addr), Min = min, Max = max };
        if (curve is null) return track;

        foreach (var key in curve.Keys)
            track.Keys.Add(new TKey(key.Time, key.Value, key.Mode.ToString(),
                key.OutX, key.OutY, key.InX, key.InY));
        return track;
    }

    /// <summary>
    /// 轨道的显示域取注册表声明范围（与限幅徽标同一份数字）。注册表不认的地址（旧档里的孤儿曲线）
    /// 退回 key 自身的取值范围：显示域同时是拖动时的钳制域，域错了会静默改掉用户的 key。
    /// </summary>
    private (double Min, double Max) CurveRange(in ParamAddress addr, FloatCurve? curve)
    {
        if (ParamRegistry.TryResolve(addr, _effects, _scene, out var spec, out _)) return (spec.Min, spec.Max);
        if (curve is null || curve.Count == 0) return (-1, 1);

        var min = double.MaxValue;
        var max = double.MinValue;
        foreach (var key in curve.Keys)
        {
            min = Math.Min(min, key.Value);
            max = Math.Max(max, key.Value);
        }

        if (max - min < 1e-9) return (min - 0.5, max + 0.5);
        var pad = (max - min) * 0.1;
        return (min - pad, max + pad);
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
            curve.Keys.Add(new CurveKey(key.TimeSeconds, key.Value, ParseInterp(key.Interp),
                key.OutX, key.OutY, key.InX, key.InY));
        // 空曲线 = 没有曲线：求值上等价，存档里留个空壳只会误导（与打 key 那条路径同一约定）
        if (curve.Count == 0) _evaluator.RemoveCurve(edit.Address);

        PublishDirector();
        RefreshParamRows();
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
    /// 后台解码整段音频：波形（<see cref="WaveformLoader.Load"/>）+ 频带序列（<see cref="AudioFileAnalyzer.Analyze"/>）。
    /// 两者都只解码、不碰播放设备；换音频 / 换工程时取消上一次——两份结果同时回来会让波形轨与信号频带显示错的那一段。
    /// </summary>
    private void StartAudioLoad(string path)
    {
        _audioLoadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _audioLoadCts = cts;
        _peaks = null;
        _audioBands = null;
        PublishAudioBands();
        RefreshTimeline();

        Task.Run(() =>
        {
            WaveformPeaks? peaks = null;
            AudioBandSeries? bands = null;
            // 两条分析各管各的 try：频带解不出来不该把波形也一起丢掉
            try
            {
                peaks = WaveformLoader.Load(path, cts.Token);
            }
            catch (Exception ex)
            {
                if (!cts.IsCancellationRequested) Log.Warn("[工作台] 波形装载失败：" + ex.Message);
            }

            try
            {
                bands = AudioFileAnalyzer.Analyze(path, cts.Token);
            }
            catch (Exception ex)
            {
                if (!cts.IsCancellationRequested) Log.Warn("[工作台] 频带分析失败：" + ex.Message);
            }

            Dispatcher.BeginInvoke(() =>
            {
                if (!ReferenceEquals(_audioLoadCts, cts)) return; // 期间又换过音频：这一份作废
                if (peaks is not null)
                {
                    _peaks = peaks;
                    RefreshTimeline();
                }

                if (bands is not null)
                {
                    _audioBands = bands;
                    PublishAudioBands();
                    RefreshParamRows(); // 频带到位后「失效回落」的那些行当场变成「连线」
                }
            });
        });
    }
}
