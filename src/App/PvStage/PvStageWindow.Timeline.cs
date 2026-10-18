using System.Globalization;
using System.IO;
using System.Text;
using JizuraLive.App.PvStage.Timeline;
using JizuraLive.Engine;
using JizuraLive.Logging;
using JizuraLive.Pv;
using JizuraLive.Render.Effects;
using JizuraLive.Vts;

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

        foreach (var clip in _lyricClips)
            snap.Lyrics.Add(new TClip
            {
                Start = clip.StartMs / 1000.0,
                End = clip.EndMs / 1000.0,
                Text = clip.Text,
            });

        ExportDesignTracks(snap);
        ExportVtsCueTracks(snap);

        _snap = snap;
        TheTimeline.SetSnapshot(snap);
    }

    private void ExportVtsCueTracks(TSnapshot snap)
    {
        var hotkeys = new TVtsCueTrack { Kind = VtsCueKind.Hotkey };
        var expressions = new TVtsCueTrack { Kind = VtsCueKind.Expression };
        foreach (var cue in _project.VtsCues?.Cues ?? new List<VtsCue>())
        {
            if (cue is null) continue;
            var target = cue.Target ?? "";
            var item = new TVtsCue
            {
                Time = cue.Time,
                Duration = cue.Duration > 0.01 ? cue.Duration : 2.0,
                Target = target,
                Mode = cue.Mode,
                Label = (cue.Kind == VtsCueKind.Hotkey
                    ? _vts.Hotkeys.FirstOrDefault(h => h.HotkeyId == target)?.Name
                    : Path.GetFileNameWithoutExtension(target)) ?? target,
            };
            if (cue.Kind == VtsCueKind.Hotkey) hotkeys.Cues.Add(item);
            else if (cue.Kind == VtsCueKind.Expression) expressions.Cues.Add(item);
        }

        snap.VtsCueTracks.Add(hotkeys);
        snap.VtsCueTracks.Add(expressions);
    }

    private void ExportDesignTracks(TSnapshot snap)
    {
        double duration = snap.DurationSeconds;

        var fx = new TDesignTrack { Kind = DesignTrackKind.Fx };
        foreach (var node in _effects.Nodes)
            foreach (var seg in DesignTracks.FromEffectNode(node, duration))
                fx.Segments.Add(ToTSegment(seg));

        var cam = new TDesignTrack { Kind = DesignTrackKind.Cam };
        var shots = _project.CameraTrack?.Shots ?? new List<CameraShot>();
        for (int i = 0; i < shots.Count; i++)
        {
            var seg = ToTSegment(DesignTracks.FromCameraShot(shots[i]));
            seg.RefId = i.ToString(CultureInfo.InvariantCulture);
            cam.Segments.Add(seg);
        }

        var treat = new TDesignTrack { Kind = DesignTrackKind.Treat };

        var layerCam = new TDesignTrack { Kind = DesignTrackKind.LayerCam };
        if (_project.LayerCameraTracks.TryGetValue(PvScene.CharacterLayerId, out var layerTrack))
        {
            for (int i = 0; i < layerTrack.Shots.Count; i++)
            {
                var seg = DesignTracks.FromCameraShot(layerTrack.Shots[i]);
                seg.Track = DesignTrackKind.LayerCam;
                var tseg = ToTSegment(seg);
                tseg.RefId = "layer:" + i.ToString(CultureInfo.InvariantCulture);
                layerCam.Segments.Add(tseg);
            }
        }

        snap.DesignTracks.Add(fx);
        snap.DesignTracks.Add(cam);
        snap.DesignTracks.Add(layerCam);
        snap.DesignTracks.Add(treat);
        foreach (DesignTrackKind kind in new[]
                 {
                     DesignTrackKind.Layout, DesignTrackKind.Enter, DesignTrackKind.Hold,
                     DesignTrackKind.Exit, DesignTrackKind.Decor, DesignTrackKind.Bg, DesignTrackKind.Trans,
                 })
        {
            var track = new TDesignTrack { Kind = kind };
            foreach (var seg in _project.DesignSegments
                         .Where(d => d.Track == kind)
                         .OrderBy(d => d.Start))
                track.Segments.Add(ToTSegment(seg));
            snap.DesignTracks.Add(track);
        }

        ExportLegacyPlanTracks(snap);
    }

    private void ExportLegacyPlanTracks(TSnapshot snap)
    {
        if (_legacyPlanInfo is not { Cuts.Count: > 0 } plan) return;

        var layout = snap.DesignTracks.FirstOrDefault(t => t.Kind == DesignTrackKind.Layout);
        var enter = snap.DesignTracks.FirstOrDefault(t => t.Kind == DesignTrackKind.Enter);
        var exit = snap.DesignTracks.FirstOrDefault(t => t.Kind == DesignTrackKind.Exit);
        var treat = snap.DesignTracks.FirstOrDefault(t => t.Kind == DesignTrackKind.Treat);
        if (layout is null || enter is null || exit is null || treat is null) return;

        string prevLayout = "", prevEnter = "", prevExit = "", prevTreat = "";
        for (int i = 0; i < plan.Cuts.Count; i++)
        {
            var cut = plan.Cuts[i];
            double end = cut.End > cut.Start ? cut.End : cut.Start + 0.1;
            AddIfChanged(layout, DesignTrackKind.Layout, cut.Layout, i, cut.Start, end, ref prevLayout);
            AddIfChanged(enter, DesignTrackKind.Enter, cut.Enter, i, cut.Start, end, ref prevEnter);
            AddIfChanged(exit, DesignTrackKind.Exit, cut.Exit, i, cut.Start, end, ref prevExit);
            if (!string.Equals(cut.Treat, "none", StringComparison.OrdinalIgnoreCase))
                AddIfChanged(treat, DesignTrackKind.Treat, cut.Treat, i, cut.Start, end, ref prevTreat);
        }

        void AddIfChanged(TDesignTrack track, DesignTrackKind kind, string? technique, int index,
            double start, double end, ref string prev)
        {
            var value = technique ?? "";
            if (value.Length == 0) return;
            if (string.Equals(prev, value, StringComparison.Ordinal)) return;
            prev = value;
            track.Segments.Add(new TDesignSegment
            {
                RefId = $"legacy_{kind}_{index}",
                Track = (int)kind,
                Start = start,
                End = end,
                Technique = value,
                Label = LegacyTechniqueLabel(kind, value),
                Source = "legacy",
            });
        }
    }

    private static TDesignSegment ToTSegment(DesignSegment seg) => new()
    {
        RefId = string.IsNullOrWhiteSpace(seg.RefId) ? seg.Id : seg.RefId!,
        Track = (int)seg.Track,
        Start = seg.Start,
        End = seg.End,
        FadeIn = seg.FadeIn,
        FadeOut = seg.FadeOut,
        Technique = seg.Technique,
        Label = TimelineTechniqueLabel(seg.Track, seg.Technique),
        Source = seg.Source,
        Locked = seg.Locked,
        Params = new Dictionary<string, double>(seg.Params, StringComparer.Ordinal),
    };

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
        var t = _audio.Time;
        var d = _audio.Duration;
        TheTimeline.SetPlayhead(t);
        TblTime.Text = $"{Fmt(t)} / {Fmt(d)}";
        if (!_draggingTime && d > 0)
        {
            SldTime.Maximum = Math.Max(0.01, d);
            SldTime.LargeChange = Math.Max(0.5, d / 20);
            SldTime.SmallChange = Math.Max(0.1, d / 200);
            SldTime.Value = Math.Clamp(t, 0, SldTime.Maximum);
        }
    }

    private void Timeline_VtsCueDeleteRequested(VtsCueKind kind, double time)
    {
        if (_project.VtsCues?.Cues is not { } cues) return;

        int removed = cues.RemoveAll(c =>
            c is not null && c.Kind == kind && Math.Abs(c.Time - time) < 1e-3);
        if (removed == 0) return;

        PushUndo();
        RefreshTimeline();
        _saveError = SaveProjectNow();
        TblStatus.Text = "已删除 VTS 关键帧";
    }

    private void Timeline_VtsCueEdited(VtsCueKind kind, string target,
        double oldStart, double newStart, double newDuration)
    {
        if (_project.VtsCues?.Cues is not { } cues) return;

        var cue = cues.FirstOrDefault(c =>
            c is not null && c.Kind == kind && c.Target == target && Math.Abs(c.Time - oldStart) < 1e-3);
        if (cue is null) return;

        cue.Time = newStart;
        cue.Duration = newDuration;
        cues.Sort((a, b) => a.Time.CompareTo(b.Time));

        PushUndo();
        RefreshTimeline();
        _saveError = SaveProjectNow();
        TblStatus.Text = "已调整 VTS 关键帧时间";
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

    private (EffectNode?, int) ResolveEffectRef(string refId)
    {
        int hash = refId.IndexOf('#');
        string instanceId = hash < 0 ? refId : refId[..hash];
        int window = hash < 0 ? -1
            : int.TryParse(refId[(hash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w) ? w : -1;
        var node = _effects.Nodes.FirstOrDefault(n => string.Equals(n.InstanceId, instanceId, StringComparison.Ordinal));
        return (node, window);
    }

    private CameraShot? FindCameraShot(string refId)
    {
        if (!int.TryParse(refId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)) return null;
        var shots = _project.CameraTrack?.Shots;
        return shots is not null && index >= 0 && index < shots.Count ? shots[index] : null;
    }

    private CameraShot? FindLayerCameraShot(string refId)
    {
        var parts = refId.Split(':');
        if (parts.Length != 2 || !string.Equals(parts[0], "layer", StringComparison.Ordinal)) return null;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)) return null;
        if (!_project.LayerCameraTracks.TryGetValue(PvScene.CharacterLayerId, out var track)) return null;
        return index >= 0 && index < track.Shots.Count ? track.Shots[index] : null;
    }

    private DesignSegment? FindDesignSegment(string refId)
    {
        if (string.IsNullOrEmpty(refId)) return null;
        return _project.DesignSegments.FirstOrDefault(d =>
            string.Equals(d.RefId, refId, StringComparison.Ordinal)
            || string.Equals(d.Id, refId, StringComparison.Ordinal));
    }

    private CameraShot? FindCameraShotAt(double start)
    {
        var shots = _project.CameraTrack?.Shots;
        return shots?.FirstOrDefault(s => Math.Abs(s.Start - start) < 0.05);
    }

    private CameraShot? FindLayerCameraShotAt(double start)
    {
        if (!_project.LayerCameraTracks.TryGetValue(PvScene.CharacterLayerId, out var track)) return null;
        return track.Shots.FirstOrDefault(s => Math.Abs(s.Start - start) < 0.05);
    }

    private DesignSegment? FindDesignSegmentAt(int track, double start)
    {
        var kind = (DesignTrackKind)track;
        return _project.DesignSegments.FirstOrDefault(d =>
            d.Track == kind && Math.Abs(d.Start - start) < 0.05);
    }

    private SegmentTarget? FindLegacyCut(string refId)
    {
        if (_legacyPlanInfo is not { } plan || string.IsNullOrEmpty(refId)) return null;
        var parts = refId.Split('_');
        if (parts.Length < 3 || !string.Equals(parts[0], "legacy", StringComparison.Ordinal)) return null;
        if (!Enum.TryParse<DesignTrackKind>(parts[1], true, out var kind)) return null;
        if (kind is not (DesignTrackKind.Layout or DesignTrackKind.Enter
            or DesignTrackKind.Exit or DesignTrackKind.Treat)) return null;
        if (!int.TryParse(parts[^1], out var index) || index < 0 || index >= plan.Cuts.Count) return null;
        return SegmentTarget.OfLegacyCut(index, kind);
    }

    private void Timeline_SegmentSelected(TDesignSegment seg)
    {
        if (seg.Track == (int)DesignTrackKind.Fx)
        {
            var (node, window) = ResolveEffectRef(seg.RefId);
            _segmentTarget = node is null
                ? null
                : window >= 0 ? SegmentTarget.OfEffectWindow(node, window) : SegmentTarget.Of(node);
        }
        else if (seg.Track == (int)DesignTrackKind.Cam)
            _segmentTarget = (FindCameraShot(seg.RefId) ?? FindCameraShotAt(seg.Start)) is { } shot
                ? SegmentTarget.Of(shot)
                : null;
        else if (seg.Track == (int)DesignTrackKind.LayerCam)
            _segmentTarget = (FindLayerCameraShot(seg.RefId) ?? FindLayerCameraShotAt(seg.Start)) is { } layerShot
                ? SegmentTarget.OfLayerShot(layerShot)
                : null;
        else if (FindDesignSegment(seg.RefId) is { } design)
            _segmentTarget = SegmentTarget.Of(design);
        else if (FindDesignSegmentAt(seg.Track, seg.Start) is { } designAt)
            _segmentTarget = SegmentTarget.Of(designAt);
        else if (FindLegacyCut(seg.RefId) is { } legacy)
            _segmentTarget = legacy;
        else _segmentTarget = null;

        BuildInspector();
    }

    private void Timeline_SegmentDeselected()
    {
        if (_segmentTarget is null) return;
        _segmentTarget = null;
        BuildInspector();
    }

    private void Timeline_SegmentTimeEdited(TDesignSegment seg, double start, double end)
    {
        Timeline_SegmentSelected(seg);

        if (_segmentTarget is null)
        {
            RefreshTimeline();
            return;
        }

        if (_segmentTarget is { LegacyCutIndex: >= 0 })
        {
            TblStatus.Text = "原版切分的开始/结束时间由歌词与 Plan 决定，不能直接拖动";
            RefreshTimeline();
            return;
        }

        WriteSegmentTime(start, end, seg.FadeIn, seg.FadeOut);
        RefreshTimeline();
    }

    private void Timeline_SegmentDeleteRequested(TDesignSegment seg)
    {
        Timeline_SegmentSelected(seg);
        if (_segmentTarget is null)
        {
            RefreshTimeline();
            return;
        }

        bool removed = false;
        bool effectsChanged = false;
        bool cameraChanged = false;

        switch (_segmentTarget)
        {
            case { Effect: { } node, WindowIndex: >= 0 } target:
                if (target.WindowIndex < node.Windows.Count)
                {
                    node.Windows.RemoveAt(target.WindowIndex);
                    if (node.Windows.Count == 0) _effects.Nodes.Remove(node);
                    removed = true;
                    effectsChanged = true;
                }
                break;
            case { Effect: { } node }:
                removed = _effects.Nodes.Remove(node);
                effectsChanged = removed;
                break;
            case { Shot: { } shot, LayerShot: true }:
                if (_project.LayerCameraTracks.TryGetValue(PvScene.CharacterLayerId, out var layerTrack))
                {
                    removed = layerTrack.Shots.Remove(shot);
                    if (removed) layerTrack.UserEdited = true;
                    cameraChanged = removed;
                }
                break;
            case { Shot: { } shot }:
                removed = _project.CameraTrack?.Shots.Remove(shot) == true;
                cameraChanged = removed;
                break;
            case { Design: { } design }:
                removed = _project.DesignSegments.Remove(design);
                break;
            case { LegacyCutIndex: >= 0 }:
                TblStatus.Text = "原版切分由歌词与 Plan 生成，不能删除";
                RefreshTimeline();
                return;
        }

        if (!removed)
        {
            TblStatus.Text = "段未删除：对应对象已不存在";
            RefreshTimeline();
            return;
        }

        _segmentTarget = null;
        if (effectsChanged) EffectsChanged();
        if (cameraChanged)
            _evaluator = DirectorEvaluator.FromSaves(_project.Curves, _project.CameraTrack, _project.LayerCameraTracks);
        BuildInspector();
        AfterSegmentEdit();
        TblStatus.Text = "已删除设计段";
    }

    private SegmentTarget? _segmentTarget;

    private sealed class SegmentTarget
    {
        public EffectNode? Effect;
        public CameraShot? Shot;
        public bool LayerShot;
        public DesignSegment? Design;
        public int LegacyCutIndex = -1;
        public DesignTrackKind LegacyKind;
        public int WindowIndex = -1;

        public static SegmentTarget Of(EffectNode node) => new() { Effect = node };
        public static SegmentTarget OfEffectWindow(EffectNode node, int windowIndex) => new() { Effect = node, WindowIndex = windowIndex };
        public static SegmentTarget Of(CameraShot shot) => new() { Shot = shot };
        public static SegmentTarget OfLayerShot(CameraShot shot) => new() { Shot = shot, LayerShot = true };
        public static SegmentTarget Of(DesignSegment design) => new() { Design = design };
        public static SegmentTarget OfLegacyCut(int index, DesignTrackKind kind) =>
            new() { LegacyCutIndex = index, LegacyKind = kind };
    }

    private void WriteSegmentTime(double start, double end, double fadeIn, double fadeOut)
    {
        if (double.IsNaN(start) || double.IsNaN(end) || double.IsNaN(fadeIn) || double.IsNaN(fadeOut)
            || double.IsInfinity(start) || double.IsInfinity(end) || double.IsInfinity(fadeIn) || double.IsInfinity(fadeOut))
        {
            TblStatus.Text = "段时间未生效：时间值必须是有限数";
            return;
        }

        if (end <= start)
        {
            TblStatus.Text = $"段时间未生效：结束 {end:F3}s 必须大于开始 {start:F3}s";
            return;
        }

        start = Math.Max(0, start);
        fadeIn = Math.Max(0, fadeIn);
        fadeOut = Math.Max(0, fadeOut);
        double length = end - start;
        if (fadeIn + fadeOut > length)
        {
            double overflow = fadeIn + fadeOut - length;
            if (fadeIn >= fadeOut) fadeIn = Math.Max(0, fadeIn - overflow);
            else fadeOut = Math.Max(0, fadeOut - overflow);
            TblStatus.Text = $"淡入淡出之和已收敛到段长内（{start:F3}s – {end:F3}s）";
        }

        switch (_segmentTarget)
        {
            case { Effect: { } node, WindowIndex: >= 0 } target:
                if (target.WindowIndex < node.Windows.Count && node.Windows[target.WindowIndex].IsValid)
                    node.Windows[target.WindowIndex] = new EffectWindow(start, end, fadeIn, fadeOut);
                else
                {
                    TblStatus.Text = "效果段未生效：对应窗口已不存在";
                    return;
                }
                EffectsChanged();
                break;
            case { Effect: { } node }:
                if (node.Windows.Count > 0)
                    node.Windows[0] = new EffectWindow(start, end, fadeIn, fadeOut);
                else if (new EffectWindow(start, end, fadeIn, fadeOut).IsValid)
                    node.Windows.Add(new EffectWindow(start, end, fadeIn, fadeOut));
                EffectsChanged();
                break;
            case { Shot: { } shot }:
                shot.Start = start;
                shot.End = end;
                shot.Source = "user";
                if (_segmentTarget is { LayerShot: true }
                    && _project.LayerCameraTracks.TryGetValue(PvScene.CharacterLayerId, out var layerTrack))
                {
                    layerTrack.UserEdited = true;
                    layerTrack.Shots.Sort((a, b) => a.Start.CompareTo(b.Start));
                }
                else
                    _project.CameraTrack?.Shots.Sort((a, b) => a.Start.CompareTo(b.Start));
                break;
            case { Design: { } design }:
                design.Start = start;
                design.End = end;
                design.FadeIn = fadeIn;
                design.FadeOut = fadeOut;
                design.Source = "user";
                break;
            default:
                return;
        }

        TblStatus.Text = $"段时间已应用：{start:F3}s – {end:F3}s";
        AfterSegmentEdit();
    }

    private void WriteSegmentParam(string key, double value)
    {
        switch (_segmentTarget)
        {
            case { Effect: { } node }:
                node.Values[key] = (float)value;
                EffectsChanged();
                break;
            case { Shot: { } shot }:
                shot.Params[key] = value;
                shot.Source = "user";
                if (_segmentTarget is { LayerShot: true }
                    && _project.LayerCameraTracks.TryGetValue(PvScene.CharacterLayerId, out var layerTrack))
                    layerTrack.UserEdited = true;
                break;
            case { Design: { } design }:
                design.Params[key] = value;
                design.Source = "user";
                break;
            default:
                return;
        }

        AfterSegmentEdit();
    }

    private void WriteSegmentLocked(bool locked)
    {
        switch (_segmentTarget)
        {
            case { Effect: { } node }:
                node.Locked = locked;
                break;
            case { Shot: { } shot }:
                shot.Locked = locked;
                if (_segmentTarget is { LayerShot: true }
                    && _project.LayerCameraTracks.TryGetValue(PvScene.CharacterLayerId, out var layerTrack))
                    layerTrack.UserEdited = true;
                break;
            case { Design: { } design }:
                design.Locked = locked;
                break;
            default:
                return;
        }

        AfterSegmentEdit();
    }

    private void AfterSegmentEdit()
    {
        PublishDirector();
        RefreshTimeline();
        PushUndo();
        _saveError = SaveProjectNow();
    }

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
        _beatMap = null;
        PublishAudioBands();
        RefreshTimeline();

        Task.Run(() =>
        {
            WaveformPeaks? peaks = null;
            try
            {
                peaks = WaveformLoader.Load(path, cts.Token);
            }
            catch (Exception ex)
            {
                if (!cts.IsCancellationRequested) Log.Warn("[工作台] 波形装载失败：" + ex.Message);
            }

            Dispatcher.BeginInvoke(() =>
            {
                if (!ReferenceEquals(_audioLoadCts, cts)) return;
                if (peaks is not null)
                {
                    _peaks = peaks;
                    RefreshTimeline();
                }
            });
        });
    }
}
