using System.Text;
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

        // 歌词轨的条来自冻结的 Clip 序列（动效歌词层吃同一份）：编辑过的区间就在这里，不再是导入时的原文。
        // 下标即 _lyricClips 的下标——控件按下标报编辑，两边必须同序。
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
        if (_snap is null) return;
        // SetPlayhead 只挪线；SetSnapshot 全量重建留给模型变化，播放中走全量就是"时间条卡顿"
        TheTimeline.SetPlayhead(_audio.Time);
    }

    private void Timeline_SeekRequested(double seconds)
    {
        _audio.Seek(seconds);
        TickTimeline();
    }

    /// <summary>一次手势（After = 该轨完整 key 列表）写回求值器，再压一份撤销快照。</summary>
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
        // 空曲线 = 没有曲线：求值上等价，存档里留个空壳只会误导（与打 key 那条路径同一约定）
        if (curve.Count == 0) _evaluator.RemoveCurve(edit.Address);

        PublishDirector();
        RefreshParamRows();
        PushUndo();
        _saveError = SaveProjectNow();
        TblStatus.Text = $"关键帧已{ReasonLabel(edit.Reason)} · {edit.After.Count} 个 key";
    }

    /// <summary>
    /// 歌词条的一次拖动。区间规则用控件同一份判定（<see cref="TimelineMath.ClipFault"/>）：宿主才是文档的主人，
    /// 控件快照若已过期，这里必须挡住——一条重叠或倒置的区间进了工程，动效歌词与字幕会一起错。
    /// 拒绝 = 一字不改、不入撤销栈，原因写状态条；接受 = 一次 PushUndo。
    /// </summary>
    private void CommitLyricClip(TEdit edit)
    {
        if (edit.Clip is not { } moved) return;
        // 控件按下标报编辑；下标对的是时间轴快照里的条，宿主这份 Clip 序列与它同序同长
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

        // 克隆 → 改 → 归一化顺序 → 换引用：发布出去的那份此后不再改（动效歌词层按引用持有它）。
        // 归一化那一步不是洁癖：拖动可以把一句整条挪到邻句之后（ClipFault 只挡重叠、不挡跨位），
        // 而查找侧（LrcParser.FindSample）是二分「最后一个 StartMs ≤ t」——非升序会让挪过的那段
        // 连同被跨过的句子一起查不到，屏幕上就是歌词整段消失。
        var clips = TimelineMath.WithClipRange(_lyricClips, moved.Index, ToMs(range.Start), ToMs(range.End));
        _lyricClips = clips;
        _pipe?.Renderer.Lyric?.SetClips(clips);
        // 工程里歌词的落盘形态就是 LrcText（LyricClip[] 进文档属 DirectorProject 那条线，还没到）：
        // 不回写它，这次编辑在保存重开后就没了
        _lrcText = LrcTextOf(clips);

        PushUndo();
        _saveError = SaveProjectNow();
        RefreshTimeline();
        TblStatus.Text = $"歌词条已调整：{range.Start:F3}s – {range.End:F3}s";
    }

    private static long ToMs(double seconds) => (long)Math.Round(seconds * 1000.0);

    /// <summary>
    /// Clip 序列 → LRC 原文。每句写一个时间戳；句尾用**空标签**单独写一行——导入侧的规则是
    /// 「相邻边界 = 下一个时间戳」，不写这条边界，拖过的句尾会被下一句的起点顶掉。
    /// 多行文本按同刻多标签逐行写出，重解析时又合并回同一条（往返不丢结构）。
    /// 已知缺口：末句的终点由解析器按音频尾取（LrcParser.TailEnd），写出去的边界压不住它。
    /// </summary>
    private static string LrcTextOf(IReadOnlyList<LyricClip> clips)
    {
        var sb = new StringBuilder();
        foreach (var clip in clips)
        {
            var start = Stamp(clip.StartMs);
            foreach (var line in clip.Text.Split('\n'))
                sb.Append(start).Append(line).Append('\n');
            sb.Append(Stamp(clip.EndMs)).Append('\n');   // 清屏边界：这句的终点
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
                    ShowStatus("音频分析完成，可一键成片");
                }
            });
        });
    }
}
