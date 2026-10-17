using System.Text;
using JizuraLive.App.PvStage.Timeline;
using JizuraLive.Engine;
using JizuraLive.Logging;
using JizuraLive.Pv;

namespace JizuraLive.App.PvStage;

public partial class PvStageWindow
{
    private double TimelineSpan() => _audio.Duration > 0.5 ? _audio.Duration : 30;

    private void RefreshTimeline()
    {
        var snap = new TSnapshot
        {
            DurationSeconds = TimelineSpan(),
            PlayheadSeconds = _audio.Time,
            Waveform = _peaks,
        };

        foreach (var addr in _evaluator.Curves.Keys.OrderBy(a => a.ToString(), StringComparer.Ordinal))
        {
            var curve = _evaluator.Curves[addr];
            if (curve.Count == 0) continue;
            snap.Curves.Add(TrackFor(addr, curve));
        }

        if (!_evaluator.Curves.ContainsKey(_camScaleAddr))
            snap.Curves.Add(TrackFor(_camScaleAddr, null));

        foreach (var clip in _lyricClips)
            snap.Lyrics.Add(new TClip
            {
                Start = clip.StartMs / 1000.0,
                End = clip.EndMs / 1000.0,
                Text = clip.Text,
            });

        _snap = snap;
        TheTimeline.SetSnapshot(snap);
    }

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

    private void TickTimeline()
    {
        if (_snap is null) return;
        TheTimeline.SetPlayhead(_audio.Time);
    }

    private void Timeline_SeekRequested(double seconds)
    {
        _audio.Seek(seconds);
        TickTimeline();
    }

    private void Timeline_EditCommitted(TEdit edit)
    {
        if (edit.Reason == "clip")
        {
            CommitLyricClip(edit);
            return;
        }

        if (!_evaluator.Curves.TryGetValue(edit.Address, out var curve))
        {
            curve = new FloatCurve();
            _evaluator.SetCurve(edit.Address, curve);
        }

        curve.Keys.Clear();
        foreach (var key in edit.After)
            curve.Keys.Add(new CurveKey(key.TimeSeconds, key.Value, ParseInterp(key.Interp),
                key.OutX, key.OutY, key.InX, key.InY));
        if (curve.Count == 0) _evaluator.RemoveCurve(edit.Address);

        PublishDirector();
        RefreshParamRows();
        PushUndo();
        _saveError = SaveProjectNow();
        TblStatus.Text = $"关键帧已{ReasonLabel(edit.Reason)} · {edit.After.Count} 个 key";
    }

    private void CommitLyricClip(TEdit edit)
    {
        if (edit.Clip is not { } moved) return;
        if (_snap is null || moved.Index < 0
            || moved.Index >= _snap.Lyrics.Count || moved.Index >= _lyricClips.Count)
        {
            TblStatus.Text = "歌词条未生效：这一句已经不在歌词轨上了";
            return;
        }

        var range = new ClipRange(moved.Start, moved.End);
        if (TimelineMath.ClipFault(_snap.Lyrics, moved.Index, range) is { } fault)
        {
            TblStatus.Text = "歌词条未生效：" + fault;
            return;
        }

        var clips = TimelineMath.WithClipRange(_lyricClips, moved.Index, ToMs(range.Start), ToMs(range.End));
        PublishLyricClips(clips);
        _lrcText = LrcTextOf(clips);

        PushUndo();
        _saveError = SaveProjectNow();
        RefreshTimeline();
        TblStatus.Text = $"歌词条已调整：{range.Start:F3}s – {range.End:F3}s";
    }

    private static long ToMs(double seconds) => (long)Math.Round(seconds * 1000.0);

    private static string LrcTextOf(IReadOnlyList<LyricClip> clips)
    {
        var sb = new StringBuilder();
        foreach (var clip in clips)
        {
            var start = Stamp(clip.StartMs);
            foreach (var line in clip.Text.Split('\n'))
                sb.Append(start).Append(line).Append('\n');
            sb.Append(Stamp(clip.EndMs)).Append('\n');
        }

        return sb.ToString();

        static string Stamp(long ms) => $"[{ms / 60000:D2}:{ms / 1000 % 60:D2}.{ms % 1000:D3}]";
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
                if (!ReferenceEquals(_audioLoadCts, cts)) return;
                if (peaks is not null)
                {
                    _peaks = peaks;
                    RefreshTimeline();
                }

                if (bands is not null)
                {
                    _audioBands = bands;
                    PublishAudioBands();
                    RefreshParamRows();
                    ShowStatus("音频分析完成，可一键成片");
                }
            });
        });
    }
}
