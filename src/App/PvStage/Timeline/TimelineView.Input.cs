using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using JizuraLive.App.I18n;
using JizuraLive.Pv;
using JizuraLive.Vts;

namespace JizuraLive.App.PvStage.Timeline;

public sealed partial class TimelineView
{
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (_snap is null) return;
        Focus();

        var p = e.GetPosition(this);
        if (e.ChangedButton == MouseButton.Right)
        {
            if (TryOpenVtsCueMenu(p)) return;
            if (TryOpenDesignMenu(p)) return;
            OpenKeyMenu(p);
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;
        e.Handled = true;

        if (p.Y >= RenderSize.Height - ScrollbarHeight)
        {
            _drag = DragMode.Pan;
            _dragOrigin = p;
            _hScrollGrab = HorizontalScrollbar(out _, out _, out var hThumb, out var hThumbWidth)
                           && p.X >= hThumb && p.X <= hThumb + hThumbWidth
                ? p.X - hThumb
                : 0;
            CaptureMouse();
            PanTo(p.X);
            return;
        }

        if (p.X >= RenderSize.Width - ScrollbarWidth
            && VerticalScrollbar(out var vTop, out var vHeight, out var thumbTop, out var thumbHeight)
            && p.Y >= vTop && p.Y < vTop + vHeight)
        {
            _drag = DragMode.VPan;
            _vScrollGrab = p.Y >= thumbTop && p.Y <= thumbTop + thumbHeight
                ? p.Y - thumbTop
                : thumbHeight / 2;
            CaptureMouse();
            ScrollToThumb(p.Y, _vScrollGrab);
            return;
        }

        if (RowAt(p.Y) is not { } row) return;
        if (EyeAt(p, row))
        {
            e.Handled = true;
            if (row.Kind == RowKind.Ruler) TryOpenHiddenTracksMenu();
            else ToggleTrackHidden(row);
            return;
        }
        if (TrackResizeRowAt(p, out var resizeRow))
        {
            e.Handled = true;
            StartTrackResize(resizeRow, p);
            return;
        }
        double x = p.X - HeaderWidth;

        if (e.ClickCount == 2 && row.Kind == RowKind.Automation)
        {
            HandleAutomationDoubleClick(row, x);
            return;
        }

        switch (row.Kind)
        {
            case RowKind.Ruler:
            case RowKind.Waveform:
                StartScrub(x);
                break;
            case RowKind.Lyrics:
                StartClipGesture(x);
                break;
            case RowKind.Design:
                StartDesignGesture(row, p, x);
                break;
            case RowKind.Automation:
                StartAutomationDrag(row, p, x);
                break;
            case RowKind.VtsCue:
                StartVtsCueGesture(row, x);
                break;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag == DragMode.None)
        {
            var hover = e.GetPosition(this);
            if (hover.X >= RenderSize.Width - ScrollbarWidth
                && VerticalScrollbar(out var vTop, out var vHeight, out _, out _)
                && hover.Y >= vTop && hover.Y < vTop + vHeight)
            {
                Cursor = Cursors.SizeNS;
                return;
            }
            if (RowAt(hover.Y) is { } hoverRow && EyeAt(hover, hoverRow))
            {
                Cursor = Cursors.Hand;
                return;
            }
            if (TrackResizeRowAt(hover, out _))
            {
                Cursor = Cursors.SizeNS;
                return;
            }
            UpdateDesignCursor(hover);
            return;
        }
        var p = e.GetPosition(this);
        switch (_drag)
        {
            case DragMode.Scrub:
                RaiseSeek(TimeAt(p.X - HeaderWidth));
                break;
            case DragMode.Key:
                MoveSelectedKeys(p);
                break;
            case DragMode.Pan:
                PanTo(p.X);
                break;
            case DragMode.VPan:
                ScrollToThumb(p.Y, _vScrollGrab);
                break;
            case DragMode.Clip:
                MoveClip(p);
                break;
            case DragMode.Design:
                MoveDesignSegment(p);
                break;
            case DragMode.VtsCue:
                MoveVtsCue(p.X - HeaderWidth);
                break;
            case DragMode.TrackResize:
                MoveTrackResize(p);
                break;
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton != MouseButton.Left || _drag == DragMode.None) return;

        if (_drag == DragMode.Key)
        {
            RebuildSelectionFromDrag();
            _dragKeys.Clear();
            _dragTip = null;
            _drag = DragMode.None;
            CommitGesture("move");
        }
        else if (_drag == DragMode.Clip)
        {
            _drag = DragMode.None;
            CommitClip();
        }
        else if (_drag == DragMode.Design)
        {
            _drag = DragMode.None;
            CommitDesignGesture();
        }
        else if (_drag == DragMode.VtsCue)
        {
            CommitVtsCueDrag();
        }
        else if (_drag == DragMode.TrackResize)
        {
            _drag = DragMode.None;
            _dragTip = null;
            CommitTrackResize();
        }
        else _drag = DragMode.None;

        if (IsMouseCaptured) ReleaseMouseCapture();
        InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_drag == DragMode.None) return;
        if (_drag == DragMode.TrackResize)
        {
            _drag = DragMode.None;
            _dragTip = null;
            CommitTrackResize();
            InvalidateVisual();
            return;
        }
        if (_drag == DragMode.VtsCue)
        {
            RestoreVtsCueDrag();
            _drag = DragMode.None;
            InvalidateVisual();
            return;
        }

        if (_drag == DragMode.Design && _dragSegment is { } seg)
        {
            seg.Start = _segOriginStart;
            seg.End = _segOriginEnd;
            _dragSegment = null;
            _dragTip = null;
        }
        RestoreGesture();
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_snap is null) return;
        e.Handled = true;

        double x = Math.Max(0, e.GetPosition(this).X - HeaderWidth);
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0
            || e.GetPosition(this).X >= RenderSize.Width - ScrollbarWidth)
        {
            ScrollTracks(-e.Delta / 120.0 * AutomationHeight * 3);
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            double anchorTime = TimeAt(x);
            double factor = e.Delta > 0 ? 1.25 : 0.8;
            _pixelsPerSecond = TimelineMath.ClampPixelsPerSecond(_pixelsPerSecond * factor, ContentWidth, _snap.DurationSeconds);
            _viewStart = TimelineMath.ZoomViewStart(anchorTime, x, _pixelsPerSecond);
        }
        else _viewStart -= e.Delta / 120.0 * ContentWidth * 0.15 / _pixelsPerSecond;

        _viewStart = TimelineMath.ClampViewStart(_viewStart, _pixelsPerSecond, ContentWidth, _snap.DurationSeconds);
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_snap is null) return;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        switch (e.Key)
        {
            case Key.Escape:
                CancelGesture();
                e.Handled = true;
                break;
            case Key.Delete:
                if (_selSegment is { } sel && FindSegment(sel.Track, sel.RefId) is { } segment)
                    SegmentDeleteRequested?.Invoke(segment);
                else
                    DeleteSelected();
                e.Handled = true;
                break;
            case Key.C when ctrl:
                CopySelected();
                e.Handled = true;
                break;
            case Key.V when ctrl:
                PasteAtPlayhead();
                e.Handled = true;
                break;
        }
    }

    private TDesignSegment? DesignSegmentAt(Row row, double time)
    {
        if (_snap is null || row.Index < 0 || row.Index >= _snap.DesignTracks.Count) return null;
        var track = _snap.DesignTracks[row.Index];
        for (int i = track.Segments.Count - 1; i >= 0; i--)
        {
            var seg = track.Segments[i];
            if (time >= seg.Start && time <= seg.End) return seg;
        }
        return null;
    }

    private void StartDesignGesture(Row row, Point p, double x)
    {
        if (_snap is null) return;

        double time = TimeAt(x);
        var hit = DesignSegmentAt(row, time);
        if (hit is null)
        {
            ClearSegmentSelection();
            RaiseSeek(time);
            InvalidateVisual();
            return;
        }

        _selection.Clear();
        _selSegment = new SelSegment(row.Index, hit.RefId);
        SegmentSelected?.Invoke(hit);

        _dragSegment = hit;
        _segOriginStart = hit.Start;
        _segOriginEnd = hit.End;
        _segAnchorTime = time;

        double x0 = HeaderWidth + TimelineMath.TimeToX(hit.Start, _viewStart, _pixelsPerSecond);
        double x1 = HeaderWidth + TimelineMath.TimeToX(hit.End, _viewStart, _pixelsPerSecond);
        const double edgePx = 6;
        _segResizeStart = Math.Abs(p.X - x0) <= edgePx;
        _segResizeEnd = !_segResizeStart && Math.Abs(p.X - x1) <= edgePx;

        _drag = DragMode.Design;
        CaptureMouse();
        InvalidateVisual();
    }

    private void MoveDesignSegment(Point p)
    {
        if (_dragSegment is not { } seg || _snap is null) return;

        double delta = TimeAt(p.X - HeaderWidth) - _segAnchorTime;
        bool snap = (Keyboard.Modifiers & ModifierKeys.Alt) == 0;
        double step = TimelineMath.GridStep(_pixelsPerSecond, Fps);
        if (snap) delta = Math.Round(delta / step) * step;

        double duration = Math.Max(0, _snap.DurationSeconds);
        double minDur = Math.Max(0.05, step);

        if (_segResizeStart)
        {
            seg.Start = Math.Clamp(_segOriginStart + delta, 0, _segOriginEnd - minDur);
        }
        else if (_segResizeEnd)
        {
            seg.End = Math.Clamp(_segOriginEnd + delta, _segOriginStart + minDur, duration);
        }
        else
        {
            double length = _segOriginEnd - _segOriginStart;
            double start = Math.Clamp(_segOriginStart + delta, 0, Math.Max(0, duration - length));
            seg.Start = start;
            seg.End = start + length;
        }

        _dragTip = $"{FormatTime(seg.Start)} – {FormatTime(seg.End)}";
        InvalidateVisual();
    }

    private void CommitDesignGesture()
    {
        var seg = _dragSegment;
        _dragSegment = null;
        _dragTip = null;
        if (seg is null) return;
        SegmentTimeEdited?.Invoke(seg, seg.Start, seg.End);
        InvalidateVisual();
    }

    private bool TryOpenDesignMenu(Point p)
    {
        if (_snap is null || RowAt(p.Y) is not { Kind: RowKind.Design } row) return false;
        var seg = DesignSegmentAt(row, TimeAt(p.X - HeaderWidth));
        if (seg is null) return false;

        _selection.Clear();
        _selSegment = new SelSegment(row.Index, seg.RefId);
        SegmentSelected?.Invoke(seg);
        InvalidateVisual();

        var menu = new ContextMenu { PlacementTarget = this, Placement = PlacementMode.MousePoint };
        if (IsLegacy(seg))
        {
            menu.Items.Add(new MenuItem { Header = L.T("原版切分由歌词与 Plan 生成，只读"), IsEnabled = false });
            menu.IsOpen = true;
            return true;
        }

        var start = new MenuItem { Header = L.F("开始时间：{0}…", FormatTime(seg.Start)) };
        start.Click += async (_, _) =>
        {
            var value = await PromptValueAsync(seg.Start);
            if (value is null) return;
            seg.Start = Math.Clamp(value.Value, 0, Math.Max(0, seg.End - 0.01));
            SegmentTimeEdited?.Invoke(seg, seg.Start, seg.End);
            InvalidateVisual();
        };
        menu.Items.Add(start);

        var end = new MenuItem { Header = L.F("结束时间：{0}…", FormatTime(seg.End)) };
        end.Click += async (_, _) =>
        {
            var value = await PromptValueAsync(seg.End);
            if (value is null) return;
            seg.End = Math.Max(seg.Start + 0.01, value.Value);
            SegmentTimeEdited?.Invoke(seg, seg.Start, seg.End);
            InvalidateVisual();
        };
        menu.Items.Add(end);

        menu.Items.Add(new Separator());
        var fit = new MenuItem { Header = L.T("从播放头开始") };
        fit.Click += (_, _) =>
        {
            double length = seg.End - seg.Start;
            seg.Start = Math.Max(0, _snap.PlayheadSeconds);
            seg.End = seg.Start + length;
            SegmentTimeEdited?.Invoke(seg, seg.Start, seg.End);
            InvalidateVisual();
        };
        menu.Items.Add(fit);

        menu.Items.Add(new Separator());
        var remove = new MenuItem { Header = L.T("删除段") };
        remove.Click += (_, _) => SegmentDeleteRequested?.Invoke(seg);
        menu.Items.Add(remove);

        menu.IsOpen = true;
        return true;
    }

    private static bool IsLegacy(TDesignSegment seg) =>
        string.Equals(seg.Source, "legacy", StringComparison.Ordinal)
        && seg.RefId.StartsWith("legacy_", StringComparison.Ordinal)
        && seg.Track is (int)DesignTrackKind.Layout or (int)DesignTrackKind.Enter
            or (int)DesignTrackKind.Exit or (int)DesignTrackKind.Treat;

    private bool TrackResizeRowAt(Point p, out Row row)
    {
        row = default;
        if (p.X > HeaderWidth) return false;

        double contentHeight = ContentHeight;
        foreach (var candidate in BuildRows())
        {
            if (candidate.Kind == RowKind.Ruler) continue;

            double edge = candidate.Top + candidate.Height;
            bool fixedRow = candidate.Kind is RowKind.Waveform or RowKind.Lyrics;
            if (!fixedRow && edge < _trackTop) continue;
            if (edge > contentHeight) continue;
            if (Math.Abs(p.Y - edge) > 4) continue;

            row = candidate;
            return true;
        }
        return false;
    }

    private void StartTrackResize(in Row row, Point p)
    {
        if (_snap is null) return;

        var id = TrackIdOf(row);
        if (id.Length == 0) return;

        _resizeTrackId = id;
        _resizeStartY = p.Y;
        _resizeStartHeight = row.Height;
        (_resizeMinHeight, _resizeMaxHeight) = ResizeBounds(row.Kind);
        _drag = DragMode.TrackResize;
        _dragTip = $"{row.Height:0}px";
        CaptureMouse();
        InvalidateVisual();
    }

    private static (double Min, double Max) ResizeBounds(RowKind kind) => kind switch
    {
        RowKind.Waveform => (WaveformMinHeight, WaveformMaxHeight),
        RowKind.Lyrics => (LyricsMinHeight, LyricsMaxHeight),
        RowKind.Design => (DesignMinHeight, TrackMaxHeight),
        _ => (TrackMinHeight, TrackMaxHeight),
    };

    private void MoveTrackResize(Point p)
    {
        if (_snap is null || _resizeTrackId.Length == 0) return;

        double height = Math.Clamp(_resizeStartHeight + (p.Y - _resizeStartY),
            _resizeMinHeight, _resizeMaxHeight);
        height = Math.Round(height);
        _snap.TrackHeights[_resizeTrackId] = height;
        _dragTip = $"{height:0}px";
        BuildRows();
        ClampScrollOffset();
        InvalidateVisual();
    }

    private void CommitTrackResize()
    {
        if (_resizeTrackId.Length == 0) return;
        _resizeTrackId = "";
        TrackHeightsChanged?.Invoke();
    }

    private bool EyeAt(Point p, in Row row)
    {
        if (row.Kind == RowKind.Ruler)
            return p.X >= RulerEyeLeft && p.X < HeaderWidth && HiddenTrackEntries().Count > 0;
        return p.X >= 0 && p.X <= EyeHitWidth;
    }

    private bool TryOpenHiddenTracksMenu()
    {
        var entries = HiddenTrackEntries();
        if (entries.Count == 0) return false;

        var menu = new ContextMenu { PlacementTarget = this, Placement = PlacementMode.MousePoint };
        foreach (var (id, label) in entries)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => ShowTrack(id);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var all = new MenuItem { Header = L.T("全部显示") };
        all.Click += (_, _) => ShowAllTracks();
        menu.Items.Add(all);
        menu.IsOpen = true;
        return true;
    }

    private void UpdateDesignCursor(Point p)
    {
        if (_snap is null)
        {
            Cursor = Cursors.Arrow;
            return;
        }

        if (RowAt(p.Y) is { Kind: RowKind.VtsCue } cueRow)
        {
            var track = _snap!.VtsCueTracks[cueRow.Index];
            double x = p.X - HeaderWidth;
            var cue = CueAt(track, x);
            if (cue is null)
            {
                Cursor = Cursors.Arrow;
                return;
            }

            if (cue.Mode != VtsCueMode.Toggle)
            {
                Cursor = Cursors.SizeAll;
                return;
            }

            double duration = cue.Duration > 0.01 ? cue.Duration : 2.0;
            double sx = TimelineMath.TimeToX(cue.Time, _viewStart, _pixelsPerSecond);
            double ex = TimelineMath.TimeToX(cue.Time + duration, _viewStart, _pixelsPerSecond);
            Cursor = Math.Abs(x - sx) <= 6 || Math.Abs(x - ex) <= 6
                ? Cursors.SizeWE
                : Cursors.SizeAll;
            return;
        }

        if (RowAt(p.Y) is not { Kind: RowKind.Design } row)
        {
            Cursor = Cursors.Arrow;
            return;
        }

        var seg = DesignSegmentAt(row, TimeAt(p.X - HeaderWidth));
        if (seg is null)
        {
            Cursor = Cursors.Arrow;
            return;
        }

        double x0 = HeaderWidth + TimelineMath.TimeToX(seg.Start, _viewStart, _pixelsPerSecond);
        double x1 = HeaderWidth + TimelineMath.TimeToX(seg.End, _viewStart, _pixelsPerSecond);
        const double edgePx = 6;
        Cursor = Math.Abs(p.X - x0) <= edgePx || Math.Abs(p.X - x1) <= edgePx
            ? Cursors.SizeWE
            : Cursors.SizeAll;
    }

    private void StartScrub(double x)
    {
        ClearSegmentSelection();
        _selection.Clear();
        _drag = DragMode.Scrub;
        CaptureMouse();
        RaiseSeek(TimeAt(x));
    }

    private TVtsCue? CueAt(TVtsCueTrack track, double x)
    {
        foreach (var cue in track.Cues)
        {
            double start = TimelineMath.TimeToX(cue.Time, _viewStart, _pixelsPerSecond);
            if (cue.Mode != VtsCueMode.Toggle)
            {
                if (Math.Abs(x - start) <= 7) return cue;
                continue;
            }

            double duration = cue.Duration > 0.01 ? cue.Duration : 2.0;
            double end = TimelineMath.TimeToX(cue.Time + duration, _viewStart, _pixelsPerSecond);
            if (x >= start - 4 && x <= Math.Max(start + 4, end)) return cue;
        }
        return null;
    }

    private void StartVtsCueGesture(Row row, double x)
    {
        if (_snap is null || row.Index < 0 || row.Index >= _snap.VtsCueTracks.Count) return;
        var track = _snap.VtsCueTracks[row.Index];
        var hit = CueAt(track, x);
        if (hit is null)
        {
            StartScrub(x);
            return;
        }

        _dragCue = hit;
        _dragCueKind = track.Kind;
        _dragCueTarget = hit.Target;
        _dragCueStart = hit.Time;
        _dragCueDuration = hit.Duration > 0.01 ? hit.Duration : 2.0;
        _dragCueAnchor = TimeAt(x);

        double startPx = TimelineMath.TimeToX(hit.Time, _viewStart, _pixelsPerSecond);
        double endPx = TimelineMath.TimeToX(hit.Time + _dragCueDuration, _viewStart, _pixelsPerSecond);
        const double edge = 6;
        _dragCueResizeStart = hit.Mode == VtsCueMode.Toggle && Math.Abs(x - startPx) <= edge;
        _dragCueResizeEnd = hit.Mode == VtsCueMode.Toggle && !_dragCueResizeStart
                            && Math.Abs(x - endPx) <= edge;

        _drag = DragMode.VtsCue;
        CaptureMouse();
        InvalidateVisual();
    }

    private void MoveVtsCue(double x)
    {
        if (_snap is null || _dragCue is null) return;

        bool snap = (Keyboard.Modifiers & ModifierKeys.Alt) == 0;
        double grid = TimelineMath.GridStep(_pixelsPerSecond, Fps);
        double duration = Math.Max(0, _snap.DurationSeconds);
        double minDur = Math.Max(0.1, grid);
        double delta = TimeAt(x) - _dragCueAnchor;

        if (_dragCue.Mode == VtsCueMode.Once)
        {
            double t = Math.Clamp(_dragCueStart + delta, 0, Math.Max(0, duration - 0.05));
            if (snap) t = Math.Clamp(TimelineMath.Snap(t, grid, true), 0, Math.Max(0, duration - 0.05));
            _dragCue.Time = t;
            _dragTip = $"{t:F2}s";
        }
        else if (_dragCueResizeStart)
        {
            double t = Math.Clamp(_dragCueStart + delta, 0, _dragCueStart + _dragCueDuration - minDur);
            if (snap) t = Math.Clamp(TimelineMath.Snap(t, grid, true), 0, _dragCueStart + _dragCueDuration - minDur);
            _dragCue.Time = t;
            _dragCue.Duration = _dragCueStart + _dragCueDuration - t;
            _dragTip = $"{t:F2}s – {t + _dragCue.Duration:F2}s";
        }
        else if (_dragCueResizeEnd)
        {
            double end = Math.Clamp(_dragCueStart + _dragCueDuration + delta, _dragCueStart + minDur, duration);
            if (snap) end = Math.Clamp(TimelineMath.Snap(end, grid, true), _dragCueStart + minDur, duration);
            _dragCue.Duration = end - _dragCueStart;
            _dragTip = $"{_dragCueStart:F2}s – {end:F2}s";
        }
        else
        {
            double t = Math.Clamp(_dragCueStart + delta, 0, Math.Max(0, duration - _dragCueDuration));
            if (snap) t = Math.Clamp(TimelineMath.Snap(t, grid, true), 0, Math.Max(0, duration - _dragCueDuration));
            _dragCue.Time = t;
            _dragTip = $"{t:F2}s – {t + _dragCueDuration:F2}s";
        }

        InvalidateVisual();
    }

    private void CommitVtsCueDrag()
    {
        var cue = _dragCue;
        _dragCue = null;
        _drag = DragMode.None;
        _dragTip = null;

        if (cue is null) return;
        if (Math.Abs(cue.Time - _dragCueStart) < 1e-3 && Math.Abs(cue.Duration - _dragCueDuration) < 1e-3)
        {
            RaiseSeek(cue.Time);
            InvalidateVisual();
            return;
        }

        VtsCueEdited?.Invoke(_dragCueKind, _dragCueTarget, _dragCueStart, cue.Time, cue.Duration);
        InvalidateVisual();
    }

    private void RestoreVtsCueDrag()
    {
        if (_dragCue is not null)
        {
            _dragCue.Time = _dragCueStart;
            _dragCue.Duration = _dragCueDuration;
        }
        _dragCue = null;
        _dragTip = null;
    }

    private bool TryOpenVtsCueMenu(Point p)
    {
        if (_snap is null || RowAt(p.Y) is not { Kind: RowKind.VtsCue } row
            || row.Index < 0 || row.Index >= _snap.VtsCueTracks.Count) return false;

        var track = _snap.VtsCueTracks[row.Index];
        var hit = CueAt(track, p.X - HeaderWidth);
        if (hit is null) return false;

        var menu = new ContextMenu { PlacementTarget = this, Placement = PlacementMode.MousePoint };
        var remove = new MenuItem { Header = L.T("删除") };
        remove.Click += (_, _) => VtsCueDeleteRequested?.Invoke(track.Kind, hit.Time);
        menu.Items.Add(remove);
        menu.IsOpen = true;
        return true;
    }

    private void StartClipGesture(double x)
    {
        ClearSegmentSelection();
        _dragTip = null;
        double time = TimeAt(x);
        double tolerance = TimelineMath.ClipEdgeHitPx / Math.Max(1, _pixelsPerSecond);
        if (_snap is null || TimelineMath.HitClip(_snap.Lyrics, time, tolerance) is not { } hit)
        {
            RaiseSeek(time);
            return;
        }

        var clip = _snap.Lyrics[hit.Index];
        RaiseSeek(clip.Start);
        _clipIndex = hit.Index;
        _clipEdge = hit.Edge;
        _clipOrigin = new ClipRange(clip.Start, clip.End);
        _clipAnchorTime = time;
        _clipConflict = false;
        _drag = DragMode.Clip;
        CaptureMouse();
        InvalidateVisual();
    }

    private void MoveClip(Point p)
    {
        if (_snap is null || _clipIndex < 0 || _clipIndex >= _snap.Lyrics.Count) return;

        bool snap = (Keyboard.Modifiers & ModifierKeys.Alt) == 0;
        var range = TimelineMath.DragClip(_clipOrigin, _clipEdge,
            TimeAt(p.X - HeaderWidth) - _clipAnchorTime,
            TimelineMath.GridStep(_pixelsPerSecond, Fps), snap);

        var clip = _snap.Lyrics[_clipIndex];
        clip.Start = range.Start;
        clip.End = range.End;
        var fault = TimelineMath.ClipFault(_snap.Lyrics, _clipIndex, range);
        _clipConflict = fault is not null;
        _dragTip = fault ?? $"{range.Start:F3}s – {range.End:F3}s";
        InvalidateVisual();
    }

    private void CommitClip()
    {
        if (_snap is null || _clipIndex < 0 || _clipIndex >= _snap.Lyrics.Count)
        {
            _clipIndex = -1;
            _clipConflict = false;
            return;
        }

        int index = _clipIndex;
        var clip = _snap.Lyrics[index];
        var range = new ClipRange(clip.Start, clip.End);
        _clipIndex = -1;
        _clipConflict = false;

        if (TimelineMath.ClipFault(_snap.Lyrics, index, range) is { } fault)
        {
            clip.Start = _clipOrigin.Start;
            clip.End = _clipOrigin.End;
            _dragTip = fault;
            return;
        }

        bool moved = Math.Abs(range.Start - _clipOrigin.Start) > TimelineMath.TimeEpsilon ||
                     Math.Abs(range.End - _clipOrigin.End) > TimelineMath.TimeEpsilon;
        _dragTip = null;
        if (moved) EditCommitted?.Invoke(new TEdit { Reason = "clip", Clip = new TClipEdit(index, range.Start, range.End) });
    }

    private void StartAutomationDrag(Row row, Point p, double x)
    {
        var curve = _snap!.Curves[row.Index];
        int hit = TimelineMath.HitKey(curve.Keys, x, _viewStart, _pixelsPerSecond);
        if (hit < 0)
        {
            StartScrub(x);
            return;
        }

        var key = curve.Keys[hit];
        ClearSegmentSelection();
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            int at = _selection.FindIndex(s => s.Address == curve.Address && Math.Abs(s.Time - key.TimeSeconds) <= TimelineMath.TimeEpsilon);
            if (at >= 0)
            {
                _selection.RemoveAt(at);
                InvalidateVisual();
                return;
            }
            _selection.Add(new SelKey(curve.Address, key.TimeSeconds));
        }
        else if (!IsSelected(curve.Address, key.TimeSeconds))
        {
            _selection.Clear();
            _selection.Add(new SelKey(curve.Address, key.TimeSeconds));
        }

        _dragKeys.Clear();
        foreach (var selected in _selection)
        {
            var owner = FindCurve(selected.Address);
            if (owner is null) continue;
            int index = TimelineMath.IndexAtTime(owner.Keys, selected.Time);
            if (index >= 0) _dragKeys.Add((selected.Address, index, owner.Keys[index]));
        }

        BeginGesture(_dragKeys.Select(d => d.Address));
        _dragValuePerPixel = (curve.Max - curve.Min) / Math.Max(1, row.Height - 2 * ValuePad);
        _dragOrigin = p;
        _drag = DragMode.Key;
        CaptureMouse();
        InvalidateVisual();
    }

    private void MoveSelectedKeys(Point p)
    {
        bool snap = (Keyboard.Modifiers & ModifierKeys.Alt) == 0;
        double grid = TimelineMath.KeyframeStep;
        double dt = TimeAt(p.X - HeaderWidth) - TimeAt(_dragOrigin.X - HeaderWidth);
        double dv = (_dragOrigin.Y - p.Y) * _dragValuePerPixel;

        foreach (var (address, index, origin) in _dragKeys)
        {
            var curve = FindCurve(address);
            if (curve is null || index >= curve.Keys.Count) continue;
            double time = Math.Max(0, TimelineMath.Snap(origin.TimeSeconds + dt, grid, snap));
            double value;
            if (curve.Address.Kind == ParamAddressKind.Vts && curve.Address.Param is "x" or "y")
            {
                double step = (Keyboard.Modifiers & ModifierKeys.Control) != 0 ? 1.0
                    : (Keyboard.Modifiers & ModifierKeys.Alt) != 0 ? 0.1
                    : 0.01;
                value = origin.Value + (_dragOrigin.Y - p.Y) * step * 0.02;
                value = Math.Round(Math.Round(value / step, MidpointRounding.AwayFromZero) * step, 6);
            }
            else value = origin.Value + dv;
            if (!double.IsFinite(value)) value = origin.Value;
            curve.Keys[index] = origin with { TimeSeconds = time, Value = value };
        }

        if (_dragKeys.Count > 0)
        {
            var first = _dragKeys[0];
            if (FindCurve(first.Address) is { } owner && first.KeyIndex < owner.Keys.Count)
            {
                var key = owner.Keys[first.KeyIndex];
                _dragTip = $"{key.TimeSeconds:F3}s  {key.Value.ToString("0.###", CultureInfo.InvariantCulture)}";
            }
        }
        InvalidateVisual();
    }

    private void RebuildSelectionFromDrag()
    {
        _selection.Clear();
        foreach (var (address, index, _) in _dragKeys)
        {
            var curve = FindCurve(address);
            if (curve is not null && index < curve.Keys.Count) _selection.Add(new SelKey(address, curve.Keys[index].TimeSeconds));
        }
    }

    private void PanTo(double x)
    {
        if (_snap is null || !HorizontalScrollbar(out var trackX, out var trackW, out _, out var thumbW)) return;
        double usable = Math.Max(1, trackW - thumbW);
        double fraction = Math.Clamp((x - trackX - _hScrollGrab) / usable, 0, 1);
        double duration = Math.Max(_snap.DurationSeconds, TimelineMath.MinSecondsPerScreen);
        double visible = Math.Min(duration, trackW / _pixelsPerSecond);
        _viewStart = TimelineMath.ClampViewStart(fraction * (duration - visible), _pixelsPerSecond, trackW, _snap.DurationSeconds);
        InvalidateVisual();
    }

    private void HandleAutomationDoubleClick(Row row, double x)
    {
        var curve = _snap!.Curves[row.Index];
        int hit = TimelineMath.HitKey(curve.Keys, x, _viewStart, _pixelsPerSecond);
        if (hit >= 0)
        {
            EditKeyValue(curve, hit);
            return;
        }
        AddKeyAt(curve, x);
    }

    private void AddKeyAt(TCurve curve, double x)
    {
        bool snap = (Keyboard.Modifiers & ModifierKeys.Alt) == 0;
        double time = TimelineMath.Snap(TimeAt(x), TimelineMath.KeyframeStep, snap);
        int existing = TimelineMath.IndexAtTime(curve.Keys, time);
        if (existing >= 0)
        {
            _selection.Clear();
            _selection.Add(new SelKey(curve.Address, curve.Keys[existing].TimeSeconds));
            InvalidateVisual();
            return;
        }

        BeginGesture([curve.Address]);
        double value = TimelineMath.Evaluate(curve.Keys, time, (curve.Min + curve.Max) / 2);
        curve.Keys.Add(new TKey(time, value, "SmoothStep"));
        _selection.Clear();
        _selection.Add(new SelKey(curve.Address, time));
        CommitGesture("add");
        InvalidateVisual();
    }

    private async void EditKeyValue(TCurve curve, int index)
    {
        var value = await PromptValueAsync(curve.Keys[index].Value);
        if (value is null || !double.IsFinite(value.Value)) return;
        BeginGesture([curve.Address]);
        curve.Keys[index] = curve.Keys[index] with { Value = value.Value };
        CommitGesture("value");
        InvalidateVisual();
    }

    private void OpenKeyMenu(Point p)
    {
        if (_snap is null || RowAt(p.Y) is not { Kind: RowKind.Automation } row) return;
        var curve = _snap.Curves[row.Index];
        int hit = TimelineMath.HitKey(curve.Keys, p.X - HeaderWidth, _viewStart, _pixelsPerSecond);
        if (hit < 0) return;

        var key = curve.Keys[hit];
        if (!IsSelected(curve.Address, key.TimeSeconds)) SelectKey(curve.Address, key.TimeSeconds);

        var menu = new ContextMenu { PlacementTarget = this, Placement = PlacementMode.MousePoint };
        foreach (var interp in TimelineMath.Interps)
        {
            var item = new MenuItem
            {
                Header = InterpLabel(interp),
                IsCheckable = true,
                IsChecked = key.Interp == interp,
            };
            item.Click += (_, _) => SetInterp(interp);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var remove = new MenuItem { Header = L.T("删除") };
        remove.Click += (_, _) => DeleteSelected();
        menu.Items.Add(remove);
        menu.IsOpen = true;
    }

    private static string InterpLabel(string interp) => interp switch
    {
        "Step" => L.T("阶梯"),
        "Linear" => L.T("线性"),
        "SmoothStep" => L.T("平滑"),
        "Bezier" => L.T("贝塞尔"),
        "Spring" => L.T("弹簧"),
        _ => interp,
    };

    public void SetInterp(string interp)
    {
        if (_selection.Count == 0) return;
        BeginGesture(_selection.Select(s => s.Address));
        foreach (var selected in _selection)
        {
            var curve = FindCurve(selected.Address);
            if (curve is null) continue;
            int index = TimelineMath.IndexAtTime(curve.Keys, selected.Time);
            if (index >= 0) curve.Keys[index] = curve.Keys[index] with { Interp = interp };
        }
        CommitGesture("interp");
        InvalidateVisual();
    }

    private void DeleteSelected()
    {
        if (_selection.Count == 0) return;
        BeginGesture(_selection.Select(s => s.Address));
        foreach (var selected in _selection)
        {
            var curve = FindCurve(selected.Address);
            if (curve is null) continue;
            int index = TimelineMath.IndexAtTime(curve.Keys, selected.Time);
            if (index >= 0) curve.Keys.RemoveAt(index);
        }
        _selection.Clear();
        CommitGesture("delete");
        InvalidateVisual();
    }

    private void CopySelected()
    {
        _clipboard.Clear();
        if (_selection.Count == 0) return;

        _clipboardAddress = _selection[0].Address;
        foreach (var selected in _selection)
        {
            if (selected.Address != _clipboardAddress) continue;
            var curve = FindCurve(selected.Address);
            if (curve is null) continue;
            int index = TimelineMath.IndexAtTime(curve.Keys, selected.Time);
            if (index >= 0) _clipboard.Add(curve.Keys[index]);
        }
        _clipboard.Sort(static (a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
    }

    private void PasteAtPlayhead()
    {
        if (_clipboard.Count == 0 || _snap is null) return;
        if (FindCurve(_clipboardAddress) is not { } curve) return;

        double anchor = _clipboard[0].TimeSeconds;
        BeginGesture([curve.Address]);
        foreach (var key in _clipboard)
        {
            double time = _snap.PlayheadSeconds + (key.TimeSeconds - anchor);
            int conflict = TimelineMath.IndexAtTime(curve.Keys, time);
            if (conflict >= 0) curve.Keys[conflict] = key with { TimeSeconds = time };
            else curve.Keys.Add(key with { TimeSeconds = time });
        }
        _selection.Clear();
        CommitGesture("add");
        InvalidateVisual();
    }

    private async Task<double?> PromptValueAsync(double current)
    {
        if (ValuePrompt is null) return null;
        return await ValuePrompt(current);
    }
}
