using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using JizuraLive.App.I18n;
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
        Pan,
        VPan,
        Clip,
        Design,
        VtsCue,
        TrackResize,
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

    public event Action<VtsCueKind, string, double>? VtsCueDeleteRequested;

    public event Action<VtsCueKind, string>? VtsTrackClearRequested;

    public event Action<ParamAddress>? CurveClearRequested;

    public event Action<DesignTrackKind>? DesignTrackClearRequested;

    public event Action<VtsCueKind, string, double, double, double>? VtsCueEdited;

    public event Action? TrackVisibilityChanged;

    public event Action? TrackHeightsChanged;

    public Func<double, Task<double?>>? ValuePrompt { get; set; }

    public IEnumerable<string> CollapsedTrackIds =>
        _snap?.CollapsedTracks ?? (IEnumerable<string>)Array.Empty<string>();

    public IReadOnlyDictionary<string, double> TrackHeights =>
        _snap?.TrackHeights ?? EmptyHeights;

    private static readonly Dictionary<string, double> EmptyHeights = new(StringComparer.Ordinal);

    private const string WaveformTrackId = "waveform";
    private const string LyricsTrackId = "lyrics";
    private const double EyeHitWidth = 24;
    private const double RulerEyeLeft = HeaderWidth - 52;
    private const double WaveformMinHeight = 30;
    private const double WaveformMaxHeight = 200;
    private const double LyricsMinHeight = 20;
    private const double LyricsMaxHeight = 120;
    private const double TrackMinHeight = 18;
    private const double TrackMaxHeight = 120;

    private string _resizeTrackId = "";
    private double _resizeStartY;
    private double _resizeStartHeight;
    private double _resizeMinHeight;
    private double _resizeMaxHeight;

    private static string DesignTrackId(DesignTrackKind kind) => "design:" + kind;

    private static string CurveTrackId(in ParamAddress address) => "curve:" + address;

    private static string VtsTrackId(TVtsCueTrack track) =>
        track.Id.Length > 0 ? track.Id : "vts:" + track.Kind;

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
        bool Hidden(string id) => _snap?.CollapsedTracks.Contains(id) == true;

        rows.Add(new Row(RowKind.Ruler, 0, top, RulerHeight));
        top += RulerHeight;

        if (!Hidden(WaveformTrackId))
        {
            double height = HeightOf(WaveformTrackId, WaveformHeight,
                WaveformMinHeight, WaveformMaxHeight);
            rows.Add(new Row(RowKind.Waveform, 0, top, height));
            top += height;
        }

        if (_snap is { Lyrics.Count: > 0 } && !Hidden(LyricsTrackId))
        {
            double height = HeightOf(LyricsTrackId, LyricHeight, LyricsMinHeight, LyricsMaxHeight);
            rows.Add(new Row(RowKind.Lyrics, 0, top, height));
            top += height;
        }

        double trackTop = top;
        _trackTop = trackTop;
        double viewH = Math.Max(0, contentHeight - trackTop);

        var populated = new List<int>();
        if (_snap is not null)
            for (int i = 0; i < _snap.DesignTracks.Count; i++)
                if (_snap.DesignTracks[i].Segments.Count > 0
                    && !Hidden(DesignTrackId(_snap.DesignTracks[i].Kind)))
                    populated.Add(i);

        double autoDesignH = DesignMaxHeight;
        if (populated.Count > 0)
            autoDesignH = Math.Clamp(viewH / populated.Count, DesignMinHeight, DesignMaxHeight);

        var designHeights = new List<double>(populated.Count);
        double designBlock = 0;
        foreach (var index in populated)
        {
            double height = HeightOf(DesignTrackId(_snap!.DesignTracks[index].Kind),
                autoDesignH, DesignMinHeight, TrackMaxHeight);
            designHeights.Add(height);
            designBlock += height;
        }

        double automationBlock = 0;
        if (_snap is not null)
            for (int i = 0; i < _snap.Curves.Count; i++)
            {
                var id = CurveTrackId(_snap.Curves[i].Address);
                if (Hidden(id)) continue;
                automationBlock += HeightOf(id, AutomationHeight, TrackMinHeight, TrackMaxHeight);
            }

        double cueBlock = 0;
        if (_snap is not null)
            for (int i = 0; i < _snap.VtsCueTracks.Count; i++)
            {
                var id = VtsTrackId(_snap.VtsCueTracks[i]);
                if (Hidden(id)) continue;
                cueBlock += HeightOf(id, AutomationHeight, TrackMinHeight, TrackMaxHeight);
            }

        _trackViewHeight = viewH;
        _trackTotalHeight = designBlock + automationBlock + cueBlock;
        ClampScrollOffset();

        double y = trackTop - _scrollOffset;

        for (int i = 0; i < populated.Count; i++)
        {
            double height = designHeights[i];
            rows.Add(new Row(RowKind.Design, populated[i], y, height));
            y += height;
        }

        if (_snap is not null)
            for (int i = 0; i < _snap.Curves.Count; i++)
            {
                var id = CurveTrackId(_snap.Curves[i].Address);
                if (Hidden(id)) continue;
                double height = HeightOf(id, AutomationHeight, TrackMinHeight, TrackMaxHeight);
                rows.Add(new Row(RowKind.Automation, i, y, height));
                y += height;
            }

        if (_snap is not null)
            for (int i = 0; i < _snap.VtsCueTracks.Count; i++)
            {
                var id = VtsTrackId(_snap.VtsCueTracks[i]);
                if (Hidden(id)) continue;
                double height = HeightOf(id, AutomationHeight, TrackMinHeight, TrackMaxHeight);
                rows.Add(new Row(RowKind.VtsCue, i, y, height));
                y += height;
            }

        return rows;
    }

    private double HeightOf(string id, double fallback, double min, double max) =>
        _snap is not null && _snap.TrackHeights.TryGetValue(id, out var height)
            ? Math.Clamp(height, min, max)
            : Math.Clamp(fallback, min, max);

    private string TrackIdOf(in Row row) => row.Kind switch
    {
        RowKind.Waveform => WaveformTrackId,
        RowKind.Lyrics => LyricsTrackId,
        RowKind.Design => DesignTrackId(_snap!.DesignTracks[row.Index].Kind),
        RowKind.Automation => CurveTrackId(_snap!.Curves[row.Index].Address),
        RowKind.VtsCue => VtsTrackId(_snap!.VtsCueTracks[row.Index]),
        _ => "",
    };

    private void ToggleTrackHidden(in Row row)
    {
        if (_snap is null) return;
        var id = TrackIdOf(row);
        if (id.Length == 0) return;

        bool hidden = !_snap.CollapsedTracks.Remove(id);
        if (hidden) _snap.CollapsedTracks.Add(id);

        BuildRows();
        ClampScrollOffset();
        InvalidateVisual();
        TrackVisibilityChanged?.Invoke();
    }

    private void ShowTrack(string id)
    {
        if (_snap is null || !_snap.CollapsedTracks.Remove(id)) return;
        BuildRows();
        ClampScrollOffset();
        InvalidateVisual();
        TrackVisibilityChanged?.Invoke();
    }

    private void ShowAllTracks()
    {
        if (_snap is null || _snap.CollapsedTracks.Count == 0) return;
        _snap.CollapsedTracks.Clear();
        BuildRows();
        ClampScrollOffset();
        InvalidateVisual();
        TrackVisibilityChanged?.Invoke();
    }

    private List<(string Id, string Label)> HiddenTrackEntries()
    {
        var list = new List<(string, string)>();
        if (_snap is null) return list;
        bool Hidden(string id) => _snap.CollapsedTracks.Contains(id);

        if (Hidden(WaveformTrackId)) list.Add((WaveformTrackId, L.T("音频")));
        if (Hidden(LyricsTrackId) && _snap.Lyrics.Count > 0) list.Add((LyricsTrackId, L.T("歌词")));
        foreach (var track in _snap.DesignTracks)
        {
            if (track.Segments.Count == 0) continue;
            var id = DesignTrackId(track.Kind);
            if (Hidden(id)) list.Add((id, DesignTrackNames.Label(track.Kind)));
        }
        foreach (var curve in _snap.Curves)
        {
            var id = CurveTrackId(curve.Address);
            if (Hidden(id)) list.Add((id, curve.Label));
        }
        foreach (var track in _snap.VtsCueTracks)
        {
            var id = VtsTrackId(track);
            if (Hidden(id)) list.Add((id, VtsCueTrackLabel(track)));
        }
        return list;
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
    private double _trackTop;
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

}
