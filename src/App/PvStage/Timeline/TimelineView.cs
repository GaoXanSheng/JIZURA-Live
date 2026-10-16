using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using JizuraLive.Pv;

namespace JizuraLive.App.PvStage.Timeline;

/// <summary>
/// 多轨时间轴（自绘）：标尺 / 波形 / 歌词 / 自动化。
/// 只认 <see cref="TSnapshot"/> / <see cref="TEdit"/>，不引用 DirectorProject / UndoLog / 渲染管线。
/// </summary>
public sealed partial class TimelineView : Control
{
    /// <summary>轨道头宽度。</summary>
    public const double HeaderWidth = 180;

    public const double RulerHeight = 26;
    public const double WaveformHeight = 60;
    public const double LyricHeight = 30;
    public const double AutomationHeight = 24;
    public const double ScrollbarHeight = 10;

    internal enum RowKind
    {
        Ruler,
        Waveform,
        Lyrics,
        Automation,
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
        Clip,
    }

    private readonly List<SelKey> _selection = new();
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

    public TimelineView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
    }

    /// <summary>帧网格的帧率。画布 fps 的唯一真相在 PvScene，由宿主拷进来。</summary>
    public double Fps { get; set; } = 30;

    /// <summary>播放头轻量刷新：只挪线不动快照（SetSnapshot 全量重建，播放中用它会 2Hz 卡跳）。</summary>
    public void SetPlayhead(double seconds)
    {
        if (_snap is null) return;
        _snap.PlayheadSeconds = seconds;
        InvalidateVisual();
    }

    /// <summary>UI 线程全量刷新显示。</summary>
    public void SetSnapshot(TSnapshot snap)
    {
        _snap = snap ?? throw new ArgumentNullException(nameof(snap));
        _viewStart = TimelineMath.ClampViewStart(_viewStart, _pixelsPerSecond, ContentWidth, snap.DurationSeconds);
        _selection.RemoveAll(s =>
        {
            var curve = FindCurve(s.Address);
            return curve is null || TimelineMath.IndexAtTime(curve.Keys, s.Time) < 0;
        });
        InvalidateVisual();
    }

    /// <summary>秒。</summary>
    public event Action<double>? SeekRequested;

    /// <summary>手势结束发一次（对应一次撤销事务）；拖动过程不发。</summary>
    public event Action<TEdit>? EditCommitted;

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
        InvalidateVisual();
    }

    private double ContentWidth => Math.Max(1, RenderSize.Width - HeaderWidth);

    private double ContentHeight => Math.Max(0, RenderSize.Height - ScrollbarHeight);

    private double TimeAt(double x) => TimelineMath.XToTime(x, _viewStart, _pixelsPerSecond);

    private List<Row> BuildRows()
    {
        var rows = new List<Row>();
        double top = 0;
        rows.Add(new Row(RowKind.Ruler, 0, top, RulerHeight));
        top += RulerHeight;
        rows.Add(new Row(RowKind.Waveform, 0, top, WaveformHeight));
        top += WaveformHeight;
        if (_snap is { Lyrics.Count: > 0 })
        {
            rows.Add(new Row(RowKind.Lyrics, 0, top, LyricHeight));
            top += LyricHeight;
        }
        if (_snap is not null)
            for (int i = 0; i < _snap.Curves.Count; i++)
            {
                rows.Add(new Row(RowKind.Automation, i, top, AutomationHeight));
                top += AutomationHeight;
            }
        return rows;
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

    /// <summary>首次拿到快照且已知宽度时按整曲一屏。</summary>
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

    /// <summary>还原到手势起点（Esc / 拖动中丢捕获）。</summary>
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

    /// <summary>歌词条拖动改的是快照里那句（就地改），手势作废时按起点还原。</summary>
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

    /// <summary>Bezier 段的 Interp 名（段由左 key 的 Interp 决定）。</summary>
    private const string BezierInterp = "Bezier";

    /// <summary>选中某轨某刻的 key（同刻窗口 1µs）；该处没有 key 时清空选区。宿主与检查器的入口。</summary>
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

    /// <summary>拖动中 key 已离开原时刻，选区按拖动项判定。</summary>
    private bool IsKeySelected(ParamAddress address, int index, double time)
    {
        if (_drag == DragMode.Key)
            return _dragKeys.Exists(d => d.Address == address && d.KeyIndex == index);
        return IsSelected(address, time);
    }

    /// <summary>可拖的控制柄 = 两端至少一个被选中的 Bezier 段的两个柄（段左 key 决定是不是 Bezier 段）。</summary>
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

    /// <summary>控制柄的时间/值 → 像素（出柄取段的左 key，入柄取右 key）。</summary>
    private Point HandlePixel(TCurve curve, Row row, int index, bool outSide)
    {
        var a = outSide ? curve.Keys[index] : curve.Keys[index - 1];
        var b = outSide ? curve.Keys[index + 1] : curve.Keys[index];
        var (time, value) = outSide ? TimelineMath.OutHandlePoint(a, b) : TimelineMath.InHandlePoint(a, b);
        return new Point(HeaderWidth + TimelineMath.TimeToX(time, _viewStart, _pixelsPerSecond),
            TimelineMath.ValueToY(value, curve.Min, curve.Max, row.Top + ValuePad, row.Height - 2 * ValuePad));
    }
}
