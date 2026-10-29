using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using JizuraLive.App.I18n;
using JizuraLive.Pv;
using JizuraLive.Vts;

namespace JizuraLive.App.PvStage.Timeline;

public sealed partial class TimelineView : Control
{
    public const double HeaderWidth = 200;

    public const double RulerHeight = 28;
    public const double WaveformHeight = 60;
    public const double LyricHeight = 30;
    public const double DesignMinHeight = 14;
    public const double DesignMaxHeight = 24;
    public const double AutomationHeight = 24;
    public const double GroupHeight = 24;
    public const double ScrollbarHeight = 14;
    public const double ScrollbarWidth = 12;

    internal enum RowKind
    {
        Ruler,
        Waveform,
        Lyrics,
        Group,
        Design,
        Automation,
        VtsCue,
    }

    internal readonly record struct Row(RowKind Kind, int Index, double Top, double Height, string GroupKey = "");

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
    private bool _snapEnabled = true;
    private SelSegment? _selSegment;
    private readonly Dictionary<ParamAddress, List<TKey>> _gestureBefore = new();
    private readonly List<(ParamAddress Address, int KeyIndex, TKey Orig)> _dragKeys = new();
    private readonly List<TKey> _clipboard = new();

    private TSnapshot? _snap;
    private ParamAddress _clipboardAddress;
    private double _viewStart;
    private double _pixelsPerSecond = 120;
    private bool _fitted;
    private bool _followPlayhead = true;
    private bool _hoverActive;
    private double _hoverX;
    private double _hoverY;
    private double _hoverRowTop = double.NaN;
    private double _hoverRowBottom = double.NaN;
    private bool _hoverRowIsRuler;
    private DragMode _drag;
    private Point _dragOrigin;
    private Point _pressPoint;
    private bool _dragPastThreshold;
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
    private bool _segDirty;

    public TimelineView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
    }

    public double Fps { get; set; } = 30;

    public void SetPlayhead(double seconds, bool follow = false)
    {
        if (_snap is null) return;

        if (follow)
        {
            double visible = Math.Max(0.1, ContentWidth / Math.Max(1, _pixelsPerSecond));
            double left = _viewStart + visible * 0.02;
            double right = _viewStart + visible * 0.8;
            bool outside = seconds < _viewStart || seconds > _viewStart + visible;
            if (!_followPlayhead && outside) _followPlayhead = true;

            if (_followPlayhead && seconds < left)
                _viewStart = TimelineMath.ClampViewStart(
                    seconds - visible * 0.2, _pixelsPerSecond, ContentWidth, _snap.DurationSeconds);
            else if (_followPlayhead && seconds > right)
                _viewStart = TimelineMath.ClampViewStart(
                    seconds - visible * 0.8, _pixelsPerSecond, ContentWidth, _snap.DurationSeconds);
        }

        _snap.PlayheadSeconds = seconds;
        InvalidateVisual();
    }

    public void FollowPlayhead()
    {
        if (_snap is null) return;
        _followPlayhead = true;
        double visible = Math.Max(0.1, ContentWidth / Math.Max(1, _pixelsPerSecond));
        double seconds = _snap.PlayheadSeconds;
        if (seconds < _viewStart || seconds > _viewStart + visible * 0.8)
            _viewStart = TimelineMath.ClampViewStart(
                seconds - visible * 0.2, _pixelsPerSecond, ContentWidth, _snap.DurationSeconds);
        InvalidateVisual();
    }

    public void SeekAdjacentKey(int direction)
    {
        if (_snap is null || direction == 0) return;

        double current = _snap.PlayheadSeconds;
        double best = double.NaN;
        foreach (var curve in _snap.Curves)
        {
            foreach (var key in curve.Keys)
            {
                double time = key.TimeSeconds;
                if (direction < 0)
                {
                    if (time >= current - TimelineMath.TimeEpsilon) continue;
                    if (double.IsNaN(best) || time > best) best = time;
                }
                else
                {
                    if (time <= current + TimelineMath.TimeEpsilon) continue;
                    if (double.IsNaN(best) || time < best) best = time;
                }
            }
        }

        if (double.IsNaN(best)) return;
        double max = Math.Max(0, _snap.DurationSeconds);
        SeekRequested?.Invoke(Math.Clamp(best, 0, max));
    }

    public bool SnapEnabled
    {
        get => _snapEnabled;
        set
        {
            if (_snapEnabled == value) return;
            _snapEnabled = value;
            InvalidateVisual();
        }
    }

    public bool ToggleSnap()
    {
        SnapEnabled = !_snapEnabled;
        return _snapEnabled;
    }

    public void SeekAdjacentCut(int direction)
    {
        if (_snap is null || direction == 0) return;

        double current = _snap.PlayheadSeconds;
        double best = double.NaN;

        void Take(double time)
        {
            if (direction < 0)
            {
                if (time >= current - TimelineMath.TimeEpsilon) return;
                if (double.IsNaN(best) || time > best) best = time;
            }
            else
            {
                if (time <= current + TimelineMath.TimeEpsilon) return;
                if (double.IsNaN(best) || time < best) best = time;
            }
        }

        foreach (var clip in _snap.Lyrics)
        {
            Take(clip.Start);
            Take(clip.End);
        }

        foreach (var track in _snap.DesignTracks)
        {
            foreach (var segment in track.Segments)
            {
                Take(segment.Start);
                Take(segment.End);
            }
        }

        foreach (var track in _snap.VtsCueTracks)
        {
            foreach (var cue in track.Cues) Take(cue.Time);
        }

        if (double.IsNaN(best)) return;
        SeekRequested?.Invoke(Math.Clamp(best, 0, Math.Max(0, _snap.DurationSeconds)));
    }

    public void RevealCues()
    {
        if (_snap is null) return;
        BuildRows();
        _scrollOffset = Math.Max(0, _trackTotalHeight - _trackViewHeight);
        InvalidateVisual();
    }

    public void ResetView()
    {
        _fitted = false;
        _viewStart = 0;
        _pixelsPerSecond = 120;
        _followPlayhead = true;
        InvalidateVisual();
    }

    private (double Min, double Max) ZoomRange()
    {
        double duration = Math.Max(_snap?.DurationSeconds ?? 0, TimelineMath.MinSecondsPerScreen);
        double min = ContentWidth / duration;
        double max = ContentWidth / TimelineMath.MinSecondsPerScreen;
        return (min, Math.Max(min, max));
    }

    public double ZoomFraction
    {
        get
        {
            var (min, max) = ZoomRange();
            if (max <= min || _pixelsPerSecond <= 0) return 0;
            return Math.Clamp(Math.Log(_pixelsPerSecond / min) / Math.Log(max / min), 0, 1);
        }
    }

    public void SetZoomFraction(double fraction)
    {
        if (_snap is null || ContentWidth <= 0) return;
        var (min, max) = ZoomRange();
        double target = min * Math.Pow(max / min, Math.Clamp(fraction, 0, 1));
        double anchor = TimeAt(ContentWidth / 2);
        _pixelsPerSecond = TimelineMath.ClampPixelsPerSecond(target, ContentWidth, _snap.DurationSeconds);
        _viewStart = TimelineMath.ClampViewStart(
            TimelineMath.ZoomViewStart(anchor, ContentWidth / 2, _pixelsPerSecond),
            _pixelsPerSecond, ContentWidth, _snap.DurationSeconds);
        _followPlayhead = false;
        InvalidateVisual();
        ZoomChanged?.Invoke(ZoomFraction);
    }

    public void ZoomIn() => SetZoomFraction(ZoomFraction + 0.08);

    public void ZoomOut() => SetZoomFraction(ZoomFraction - 0.08);

    public void ZoomToFit()
    {
        if (_snap is null) return;
        var (min, _) = ZoomRange();
        _pixelsPerSecond = Math.Max(1, min);
        _viewStart = 0;
        _followPlayhead = true;
        InvalidateVisual();
        ZoomChanged?.Invoke(ZoomFraction);
    }

    public void ScrollToTime(double seconds)
    {
        if (_snap is null || ContentWidth <= 0) return;
        double visible = ContentWidth / Math.Max(1, _pixelsPerSecond);
        _viewStart = TimelineMath.ClampViewStart(
            seconds - visible / 2, _pixelsPerSecond, ContentWidth, _snap.DurationSeconds);
        _followPlayhead = false;
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

    public event Action<int, double, double, string>? LyricClipMenuRequested;

    public event Action? ScrubStarted;

    public event Action? ScrubEnded;

    public event Action<TEdit>? EditCommitted;

    public event Action<TDesignSegment>? SegmentSelected;

    public event Action<TDesignSegment, double, double>? SegmentTimeEdited;

    public event Action<TDesignSegment>? SegmentDeleteRequested;

    public event Action<VtsCueKind, string, double>? VtsCueDeleteRequested;

    public event Action<VtsCueKind, string>? VtsTrackClearRequested;

    public event Action<ParamAddress>? CurveClearRequested;

    public event Action<DesignTrackKind, string>? DesignTrackClearRequested;

    public event Action<VtsCueKind, string, double, double, double>? VtsCueEdited;

    public event Action? TrackVisibilityChanged;

    public event Action? TrackHeightsChanged;

    public event Action<TOwner?>? OwnerSelected;

    public event Action<double>? ZoomChanged;

    public Func<double, Task<double?>>? ValuePrompt { get; set; }

    public IEnumerable<string> CollapsedTrackIds =>
        _snap?.CollapsedTracks ?? (IEnumerable<string>)Array.Empty<string>();

    public IEnumerable<string> ExpandedGroupIds =>
        _snap?.ExpandedGroups ?? (IEnumerable<string>)Array.Empty<string>();

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
    private string _selectedOwnerKey = "";
    private readonly Dictionary<string, int> _groupChildCounts = new(StringComparer.Ordinal);
    private double _resizeStartY;
    private double _resizeStartHeight;
    private double _resizeMinHeight;
    private double _resizeMaxHeight;

    private static string DesignTrackId(DesignTrackKind kind) => "design:" + kind;

    private static string DesignTrackId(TDesignTrack track) =>
        track.Id.Length > 0 ? track.Id : DesignTrackId(track.Kind);

    internal static string TrackLabel(TDesignTrack track) =>
        track.Label.Length > 0 ? track.Label : DesignTrackNames.Label(track.Kind);

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

        var blocks = new List<GroupBlock>();
        int designRows = 0;
        _groupChildCounts.Clear();
        if (_snap is not null)
        {
            foreach (var owner in _snap.Owners)
            {
                var children = ChildEntries(owner, Hidden);
                _groupChildCounts[owner.Key] = children.Count;
                bool show = owner.Kind == TOwnerKind.Layer || children.Count > 0;
                if (!show) continue;
                if (owner.Kind == TOwnerKind.Design && !IsGroupHidden(owner.Key))
                    designRows += children.Count;
                blocks.Add(new GroupBlock(owner, children)
                {
                    Expanded = IsGroupExpanded(owner.Key),
                    Hidden = IsGroupHidden(owner.Key),
                });
            }
        }

        double groupTotal = blocks.Count * GroupHeight;
        double designAutoH = designRows > 0
            ? Math.Clamp(Math.Max(0, viewH - groupTotal) / designRows, DesignMinHeight, DesignMaxHeight)
            : DesignMaxHeight;

        double total = 0;
        foreach (var block in blocks)
        {
            if (block.Hidden) continue;
            total += GroupHeight;
            if (!block.Expanded) continue;
            foreach (var child in block.Children)
                total += ChildHeight(child, designAutoH);
        }

        _trackViewHeight = viewH;
        _trackTotalHeight = total;
        ClampScrollOffset();

        double y = trackTop - _scrollOffset;
        foreach (var block in blocks)
        {
            if (block.Hidden) continue;
            rows.Add(new Row(RowKind.Group, 0, y, GroupHeight, block.Owner.Key));
            y += GroupHeight;
            if (!block.Expanded) continue;
            foreach (var child in block.Children)
            {
                double height = ChildHeight(child, designAutoH);
                rows.Add(new Row(child.Kind, child.Index, y, height, block.Owner.Key));
                y += height;
            }
        }

        return rows;
    }

    private sealed class GroupBlock
    {
        public GroupBlock(TOwner owner, List<ChildEntry> children)
        {
            Owner = owner;
            Children = children;
            Expanded = false;
            Hidden = false;
        }

        public TOwner Owner { get; }
        public List<ChildEntry> Children { get; }
        public bool Expanded { get; set; }
        public bool Hidden { get; set; }
    }

    private readonly record struct ChildEntry(RowKind Kind, int Index, string TrackId);

    private List<ChildEntry> ChildEntries(TOwner owner, Func<string, bool> hidden)
    {
        var list = new List<ChildEntry>();
        if (_snap is null) return list;

        switch (owner.Kind)
        {
            case TOwnerKind.Camera:
            case TOwnerKind.VtsMove:
            case TOwnerKind.Layer:
                for (int i = 0; i < _snap.Curves.Count; i++)
                {
                    var curve = _snap.Curves[i];
                    if (curve.Address.Kind != owner.AddressKind) continue;
                    if (!string.Equals(curve.Address.Target, owner.Target, StringComparison.Ordinal)) continue;
                    var id = CurveTrackId(curve.Address);
                    if (hidden(id)) continue;
                    list.Add(new ChildEntry(RowKind.Automation, i, id));
                }
                break;
            case TOwnerKind.Effect:
                for (int i = 0; i < _snap.Curves.Count; i++)
                {
                    var curve = _snap.Curves[i];
                    if (curve.Address.Kind != owner.AddressKind) continue;
                    if (!string.Equals(curve.Address.Target, owner.Target, StringComparison.Ordinal)) continue;
                    var id = CurveTrackId(curve.Address);
                    if (hidden(id)) continue;
                    list.Add(new ChildEntry(RowKind.Automation, i, id));
                }
                break;
            case TOwnerKind.Design:
            {
                bool effectGroup = string.Equals(owner.Key, "fx", StringComparison.Ordinal);
                for (int i = 0; i < _snap.DesignTracks.Count; i++)
                {
                    var track = _snap.DesignTracks[i];
                    bool effectTrack = track.Id.Length > 0;
                    if (effectTrack != effectGroup) continue;
                    if (track.Segments.Count == 0) continue;
                    var id = DesignTrackId(track);
                    if (hidden(id)) continue;
                    list.Add(new ChildEntry(RowKind.Design, i, id));
                }
                break;
            }
            case TOwnerKind.VtsCue:
                for (int i = 0; i < _snap.VtsCueTracks.Count; i++)
                {
                    var track = _snap.VtsCueTracks[i];
                    if (track.Cues.Count == 0) continue;
                    var id = VtsTrackId(track);
                    if (hidden(id)) continue;
                    list.Add(new ChildEntry(RowKind.VtsCue, i, id));
                }
                break;
        }

        return list;
    }

    private double ChildHeight(in ChildEntry child, double designAutoH) => child.Kind switch
    {
        RowKind.Design => HeightOf(child.TrackId, designAutoH, DesignMinHeight, TrackMaxHeight),
        _ => HeightOf(child.TrackId, AutomationHeight, TrackMinHeight, TrackMaxHeight),
    };

    private double HeightOf(string id, double fallback, double min, double max) =>
        _snap is not null && _snap.TrackHeights.TryGetValue(id, out var height)
            ? Math.Clamp(height, min, max)
            : Math.Clamp(fallback, min, max);

    private string TrackIdOf(in Row row) => row.Kind switch
    {
        RowKind.Waveform => WaveformTrackId,
        RowKind.Lyrics => LyricsTrackId,
        RowKind.Group => GroupTrackId(row.GroupKey),
        RowKind.Design => DesignTrackId(_snap!.DesignTracks[row.Index]),
        RowKind.Automation => CurveTrackId(_snap!.Curves[row.Index].Address),
        RowKind.VtsCue => VtsTrackId(_snap!.VtsCueTracks[row.Index]),
        _ => "",
    };

    private static string GroupTrackId(string key) => "group:" + key;

    internal TOwner? FindOwner(string key)
    {
        if (_snap is null) return null;
        foreach (var owner in _snap.Owners)
            if (string.Equals(owner.Key, key, StringComparison.Ordinal)) return owner;
        return null;
    }

    internal bool IsGroupExpanded(string key) =>
        _snap is not null && _snap.ExpandedGroups.Contains(key);

    internal bool IsGroupHidden(string key) =>
        _snap is not null && _snap.CollapsedTracks.Contains(GroupTrackId(key));

    internal bool HasGroupChildren(string key) => GroupChildCount(key) > 0;

    internal int GroupChildCount(string key)
    {
        if (_groupChildCounts.TryGetValue(key, out var count)) return count;
        if (_snap is null) return 0;
        var owner = FindOwner(key);
        return owner is null ? 0 : ChildEntries(owner, id => _snap.CollapsedTracks.Contains(id)).Count;
    }

    private void ToggleGroupExpanded(in Row row)
    {
        if (_snap is null || row.GroupKey.Length == 0) return;
        if (!_snap.ExpandedGroups.Remove(row.GroupKey))
            _snap.ExpandedGroups.Add(row.GroupKey);
        BuildRows();
        ClampScrollOffset();
        InvalidateVisual();
        TrackVisibilityChanged?.Invoke();
    }

    private void ToggleGroupHidden(in Row row)
    {
        if (_snap is null || row.GroupKey.Length == 0) return;
        var id = GroupTrackId(row.GroupKey);
        if (!_snap.CollapsedTracks.Remove(id)) _snap.CollapsedTracks.Add(id);
        BuildRows();
        ClampScrollOffset();
        InvalidateVisual();
        TrackVisibilityChanged?.Invoke();
    }

    public void ExpandAllGroups()
    {
        if (_snap is null) return;
        foreach (var owner in _snap.Owners) _snap.ExpandedGroups.Add(owner.Key);
        BuildRows();
        ClampScrollOffset();
        InvalidateVisual();
        TrackVisibilityChanged?.Invoke();
    }

    public void CollapseAllGroups()
    {
        if (_snap is null) return;
        _snap.ExpandedGroups.Clear();
        BuildRows();
        ClampScrollOffset();
        InvalidateVisual();
        TrackVisibilityChanged?.Invoke();
    }

    public void SetSelectedOwner(string? key)
    {
        _selectedOwnerKey = key ?? "";
        InvalidateVisual();
    }

    private void SelectOwner(in Row row)
    {
        var owner = FindOwner(row.GroupKey);
        if (owner is null) return;
        _selectedOwnerKey = owner.Key;
        _selection.Clear();
        ClearSegmentSelection();
        OwnerSelected?.Invoke(owner);
        InvalidateVisual();
    }

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
        foreach (var owner in _snap.Owners)
        {
            var id = GroupTrackId(owner.Key);
            if (Hidden(id)) list.Add((id, owner.Label));
        }
        foreach (var track in _snap.DesignTracks)
        {
            if (track.Segments.Count == 0) continue;
            var id = DesignTrackId(track);
            if (Hidden(id)) list.Add((id, TrackLabel(track)));
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
        _followPlayhead = true;
        double max = Math.Max(0, _snap?.DurationSeconds ?? 0);
        SeekRequested?.Invoke(Math.Clamp(seconds, 0, max));
    }

    private void FitOnce(double contentWidth)
    {
        if (_fitted || _snap is null || contentWidth <= 0) return;
        _fitted = true;
        _pixelsPerSecond = contentWidth / Math.Max(_snap.DurationSeconds, TimelineMath.MinSecondsPerScreen);
        _viewStart = 0;
        _followPlayhead = true;
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
        if (_drag == DragMode.Design && _dragSegment is { } segment)
        {
            segment.Start = _segOriginStart;
            segment.End = _segOriginEnd;
            _dragSegment = null;
            _segDirty = false;
        }
        if (_drag == DragMode.VtsCue) RestoreVtsCueDrag();
        _dragPastThreshold = false;
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
        _dragPastThreshold = false;
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
