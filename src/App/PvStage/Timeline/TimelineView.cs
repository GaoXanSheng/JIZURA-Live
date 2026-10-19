using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using JizuraLive.Pv;
using JizuraLive.Vts;

namespace JizuraLive.App.PvStage.Timeline;

public sealed partial class TimelineView : Control
{
    public const double HeaderWidth = 180;

    public const double RulerHeight = 26;
    public const double WaveformHeight = 60;
    public const double LyricHeight = 30;
    public const double DesignMinHeight = 14;
    public const double DesignMaxHeight = 24;
    public const double AutomationHeight = 24;
    public const double ScrollbarHeight = 14;
    public const double ScrollbarWidth = 12;

    internal enum RowKind
    {
        Ruler,
        Waveform,
        Lyrics,
        Design,
        Automation,
        VtsCue,
    }

    internal readonly record struct Row(RowKind Kind, int Index, double Top, double Height);

    internal readonly record struct SelKey(ParamAddress Address, double Time);

    internal enum DragMode
    {
        None,
        Scrub,
        Key,
        Handle,
        Pan,
        VPan,
        Clip,
        Design,
        VtsCue,
    }

    internal readonly record struct SelSegment(int Track, string RefId);

    private readonly List<SelKey> _selection = new();
    private SelSegment? _selSegment;
    private readonly Dictionary<ParamAddress, List<TKey>> _gestureBefore = new();
    private readonly List<(ParamAddress Address, int KeyIndex, TKey Orig)> _dragKeys = new();
    private readonly List<TKey> _clipboard = new();

    private TSnapshot? _snap;
    private ParamAddress _clipboardAddress;
    private double _viewStart;
    private double _pixelsPerSecond = 120;
    private bool _fitted;
    private DragMode _drag;
    private Point _dragOrigin;
    private double _dragValuePerPixel;
    private string? _dragTip;
    private (ParamAddress Address, int KeyIndex, bool Out) _handleDrag;
    private Row _dragRow;
    private int _clipIndex = -1;
    private ClipEdge _clipEdge;
    private ClipRange _clipOrigin;
    private double _clipAnchorTime;
    private bool _clipConflict;

    private TDesignSegment? _dragSegment;
    private double _segOriginStart;
    private double _segOriginEnd;
    private double _segAnchorTime;
    private bool _segResizeStart;
    private bool _segResizeEnd;

    public TimelineView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
    }

    public double Fps { get; set; } = 30;

    public void SetPlayhead(double seconds)
    {
        if (_snap is null) return;
        _snap.PlayheadSeconds = seconds;
        InvalidateVisual();
    }

    public void RevealCues()
    {
        if (_snap is null) return;
        BuildRows();
        _scrollOffset = Math.Max(0, _trackTotalHeight - _trackViewHeight);
        InvalidateVisual();
    }

    public void SetSnapshot(TSnapshot snap)
    {
        _snap = snap ?? throw new ArgumentNullException(nameof(snap));
        ClampScrollOffset();
        _viewStart = TimelineMath.ClampViewStart(_viewStart, _pixelsPerSecond, ContentWidth, snap.DurationSeconds);
        _selection.RemoveAll(s =>
        {
            var curve = FindCurve(s.Address);
            return curve is null || TimelineMath.IndexAtTime(curve.Keys, s.Time) < 0;
        });
        if (_selSegment is { } sel && FindSegment(sel.Track, sel.RefId) is null)
        {
            _selSegment = null;
            SegmentDeselected?.Invoke();
        }
        InvalidateVisual();
    }

    public event Action<double>? SeekRequested;

    public event Action<TEdit>? EditCommitted;

    public event Action<TDesignSegment>? SegmentSelected;

    public event Action<TDesignSegment, double, double>? SegmentTimeEdited;

    public event Action<TDesignSegment>? SegmentDeleteRequested;

    public event Action<VtsCueKind, double>? VtsCueDeleteRequested;

    public event Action<VtsCueKind, string, double, double, double>? VtsCueEdited;

    public Func<double, Task<double?>>? ValuePrompt { get; set; }

    private TDesignSegment? FindSegment(int track, string refId)
    {
        if (_snap is null || refId.Length == 0) return null;
        if (track < 0 || track >= _snap.DesignTracks.Count) return null;
        foreach (var seg in _snap.DesignTracks[track].Segments)
            if (string.Equals(seg.RefId, refId, StringComparison.Ordinal)) return seg;
        return null;
    }

    private void ClearSegmentSelection()
    {
        if (_selSegment is null) return;
        _selSegment = null;
        SegmentDeselected?.Invoke();
    }

    public void ClearDesignSelection()
    {
        ClearSegmentSelection();
        InvalidateVisual();
    }

    public event Action? SegmentDeselected;

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? 640 : availableSize.Width;
        double h = double.IsInfinity(availableSize.Height)
            ? RulerHeight + WaveformHeight + LyricHeight + AutomationHeight * 3
            : availableSize.Height;
        return new Size(w, h);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        ClampScrollOffset();
        InvalidateVisual();
    }

    private double ContentWidth => Math.Max(1, RenderSize.Width - HeaderWidth);

    private double ContentHeight => Math.Max(0, RenderSize.Height - ScrollbarHeight);

    private double TimeAt(double x) => TimelineMath.XToTime(x, _viewStart, _pixelsPerSecond);

    private List<Row> BuildRows()
    {
        var rows = new List<Row>();
        double top = 0;
        double contentHeight = ContentHeight;
        rows.Add(new Row(RowKind.Ruler, 0, top, RulerHeight));
        top += RulerHeight;
        rows.Add(new Row(RowKind.Waveform, 0, top, WaveformHeight));
        top += WaveformHeight;
        if (_snap is { Lyrics.Count: > 0 })
        {
            rows.Add(new Row(RowKind.Lyrics, 0, top, LyricHeight));
            top += LyricHeight;
        }

        double trackTop = top;
        double viewH = Math.Max(0, contentHeight - trackTop);

        int automationCount = _snap?.Curves.Count ?? 0;
        int cueCount = _snap?.VtsCueTracks.Count ?? 0;

        var populated = new List<int>();
        if (_snap is not null)
        {
            for (int i = 0; i < _snap.DesignTracks.Count; i++)
                if (_snap.DesignTracks[i].Segments.Count > 0)
                    populated.Add(i);
        }

        double designH = DesignMaxHeight;
        if (populated.Count > 0) designH = Math.Clamp(viewH / populated.Count, DesignMinHeight, DesignMaxHeight);
        double designBlock = populated.Count * designH;

        _trackViewHeight = viewH;
        _trackTotalHeight = designBlock + automationCount * AutomationHeight + cueCount * AutomationHeight;
        ClampScrollOffset();

        double y = trackTop - _scrollOffset;

        for (int i = 0; i < populated.Count; i++)
        {
            rows.Add(new Row(RowKind.Design, populated[i], y, designH));
            y += designH;
        }

        if (_snap is not null)
            for (int i = 0; i < _snap.Curves.Count; i++)
            {
                rows.Add(new Row(RowKind.Automation, i, y, AutomationHeight));
                y += AutomationHeight;
            }

        if (_snap is not null)
            for (int i = 0; i < _snap.VtsCueTracks.Count; i++)
            {
                rows.Add(new Row(RowKind.VtsCue, i, y, AutomationHeight));
                y += AutomationHeight;
            }

        return rows;
    }

    private TVtsCue? _dragCue;
    private VtsCueKind _dragCueKind;
    private string _dragCueTarget = "";
    private double _dragCueStart;
    private double _dragCueDuration;
    private double _dragCueAnchor;
    private bool _dragCueResizeStart;
    private bool _dragCueResizeEnd;

    private double _scrollOffset;
    private double _trackViewHeight;
    private double _trackTotalHeight;
    private double _vScrollGrab;
    private double _hScrollGrab;

    private bool VerticalScrollbar(out double trackTop, out double trackHeight,
        out double thumbTop, out double thumbHeight)
    {
        trackTop = trackHeight = thumbTop = thumbHeight = 0;
        if (_trackTotalHeight <= _trackViewHeight + 1e-9) return false;

        trackTop = 0;
        trackHeight = ContentHeight;
        thumbHeight = Math.Min(trackHeight, Math.Max(28, _trackViewHeight * _trackViewHeight / _trackTotalHeight));
        double usable = Math.Max(1, trackHeight - thumbHeight);
        thumbTop = usable * _scrollOffset / Math.Max(1, _trackTotalHeight - _trackViewHeight);
        return true;
    }

    private bool HorizontalScrollbar(out double trackLeft, out double trackWidth,
        out double thumbLeft, out double thumbWidth)
    {
        trackLeft = HeaderWidth;
        trackWidth = Math.Max(1, RenderSize.Width - HeaderWidth);
        thumbLeft = thumbWidth = 0;
        if (_snap is null) return false;

        double duration = Math.Max(_snap.DurationSeconds, TimelineMath.MinSecondsPerScreen);
        double visible = Math.Min(duration, trackWidth / _pixelsPerSecond);
        thumbWidth = Math.Min(trackWidth, Math.Max(24, trackWidth * visible / duration));
        thumbLeft = trackLeft + Math.Min(trackWidth - thumbWidth, trackWidth * _viewStart / duration);
        return true;
    }

    private void ScrollToThumb(double y, double grab)
    {
        if (!VerticalScrollbar(out var trackTop, out var trackHeight, out _, out var thumbHeight)) return;
        double usable = Math.Max(1, trackHeight - thumbHeight);
        double fraction = Math.Clamp((y - grab - trackTop) / usable, 0, 1);
        _scrollOffset = fraction * Math.Max(0, _trackTotalHeight - _trackViewHeight);
        InvalidateVisual();
    }

    private void ClampScrollOffset()
    {
        double max = Math.Max(0, _trackTotalHeight - _trackViewHeight);
        if (_scrollOffset > max) _scrollOffset = max;
        if (_scrollOffset < 0) _scrollOffset = 0;
    }

    private void ScrollTracks(double delta)
    {
        ClampScrollOffset();
        double max = Math.Max(0, _trackTotalHeight - _trackViewHeight);
        _scrollOffset = Math.Clamp(_scrollOffset + delta, 0, max);
        InvalidateVisual();
    }

    private Row? RowAt(double y)
    {
        foreach (var row in BuildRows())
            if (y >= row.Top && y < row.Top + row.Height) return row;
        return null;
    }

    private TCurve? FindCurve(ParamAddress address)
    {
        if (_snap is null) return null;
        foreach (var curve in _snap.Curves)
            if (curve.Address == address) return curve;
        return null;
    }

    private void RaiseSeek(double seconds)
    {
        double max = Math.Max(0, _snap?.DurationSeconds ?? 0);
        SeekRequested?.Invoke(Math.Clamp(seconds, 0, max));
    }

    private void FitOnce(double contentWidth)
    {
        if (_fitted || _snap is null || contentWidth <= 0) return;
        _fitted = true;
        _pixelsPerSecond = contentWidth / Math.Max(_snap.DurationSeconds, TimelineMath.MinSecondsPerScreen);
        _viewStart = 0;
    }

    private void BeginGesture(IEnumerable<ParamAddress> addresses)
    {
        foreach (var address in addresses)
        {
            if (_gestureBefore.ContainsKey(address)) continue;
            if (FindCurve(address) is { } curve) _gestureBefore[address] = new List<TKey>(curve.Keys);
        }
    }

    private void CommitGesture(string reason)
    {
        if (_gestureBefore.Count == 0) return;
        var edits = new List<TEdit>();
        foreach (var (address, before) in _gestureBefore)
        {
            if (FindCurve(address) is not { } curve) continue;
            TimelineMath.Sort(curve.Keys);
            if (SameKeys(before, curve.Keys)) continue;
            edits.Add(new TEdit
            {
                Address = address,
                Before = before,
                After = [.. curve.Keys],
                Reason = reason,
            });
        }
        _gestureBefore.Clear();
        foreach (var edit in edits) EditCommitted?.Invoke(edit);
    }

    private void CancelGesture()
    {
        RestoreGesture();
        if (IsMouseCaptured) ReleaseMouseCapture();
        InvalidateVisual();
    }

    private void RestoreGesture()
    {
        RestoreClip();
        foreach (var (address, before) in _gestureBefore)
        {
            if (FindCurve(address) is not { } curve) continue;
            curve.Keys.Clear();
            curve.Keys.AddRange(before);
        }
        _gestureBefore.Clear();
        _dragKeys.Clear();
        _dragTip = null;
        _drag = DragMode.None;
    }

    private void RestoreClip()
    {
        _clipConflict = false;
        if (_snap is null || _clipIndex < 0 || _clipIndex >= _snap.Lyrics.Count)
        {
            _clipIndex = -1;
            return;
        }

        var clip = _snap.Lyrics[_clipIndex];
        clip.Start = _clipOrigin.Start;
        clip.End = _clipOrigin.End;
        _clipIndex = -1;
    }

    private static bool SameKeys(List<TKey> a, List<TKey> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    private const string BezierInterp = "Bezier";

    public void SelectKey(ParamAddress address, double time)
    {
        _selection.Clear();
        if (FindCurve(address) is { } curve)
        {
            int index = TimelineMath.IndexAtTime(curve.Keys, time);
            if (index >= 0) _selection.Add(new SelKey(address, curve.Keys[index].TimeSeconds));
        }
        InvalidateVisual();
    }

    private bool IsSelected(ParamAddress address, double time) =>
        _selection.Exists(s => s.Address == address && Math.Abs(s.Time - time) <= TimelineMath.TimeEpsilon);

    private bool IsKeySelected(ParamAddress address, int index, double time)
    {
        if (_drag == DragMode.Key)
            return _dragKeys.Exists(d => d.Address == address && d.KeyIndex == index);
        return IsSelected(address, time);
    }

    private IEnumerable<(int KeyIndex, bool Out)> HandleTargets(TCurve curve)
    {
        for (int i = 0; i + 1 < curve.Keys.Count; i++)
        {
            if (curve.Keys[i].Interp != BezierInterp) continue;
            if (!IsKeySelected(curve.Address, i, curve.Keys[i].TimeSeconds) &&
                !IsKeySelected(curve.Address, i + 1, curve.Keys[i + 1].TimeSeconds)) continue;
            yield return (i, true);
            yield return (i + 1, false);
        }
    }

    private Point HandlePixel(TCurve curve, Row row, int index, bool outSide)
    {
        var a = outSide ? curve.Keys[index] : curve.Keys[index - 1];
        var b = outSide ? curve.Keys[index + 1] : curve.Keys[index];
        var (time, value) = outSide ? TimelineMath.OutHandlePoint(a, b) : TimelineMath.InHandlePoint(a, b);
        return new Point(HeaderWidth + TimelineMath.TimeToX(time, _viewStart, _pixelsPerSecond),
            TimelineMath.ValueToY(value, curve.Min, curve.Max, row.Top + ValuePad, row.Height - 2 * ValuePad));
    }
}
