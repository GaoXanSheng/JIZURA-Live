using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using JizuraLive.Pv;
using JizuraLive.Render.Effects;

namespace JizuraLive.App.PvStage.Blueprint;

/// <summary>
/// 蓝图节点画布（自绘）。只认 <see cref="BpSnapshot"/> / <see cref="BpEdit"/>：
/// 不引用 SignalGraph 文档类型、UndoLog 或渲染管线，文档改动一律回传宿主做撤销事务。
/// </summary>
public sealed partial class BlueprintView : Control
{
    public const double MinimapWidth = 120;
    public const double MinimapHeight = 80;
    public const double StatusBarHeight = 22;

    private const int DiagnosticsIntervalMs = 250;
    private const double MinimapMargin = 12;
    private const int MaxLabelChars = 20;

    internal enum DragMode
    {
        None,
        Pan,
        Node,
        Marquee,
        Link,
    }

    private static readonly string[] Categories = ["输入", "数学", "输出"];

    private readonly HashSet<string> _selection = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BpValue> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BpPoint> _dragOrigins = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _diagnostics;

    private BpSnapshot _snap = new();
    private double _zoom = 1;
    private BpPoint _pan;
    private string? _status;
    private Func<IReadOnlyDictionary<string, BpValue>>? _diagnosticsProvider;
    private Popup? _editor;

    private DragMode _drag;
    private BpPoint _dragStartScreen;
    private BpPoint _dragStartCanvas;
    private BpPoint _dragLastScreen;
    private BpPoint _dragDelta;
    private BpPoint _marqueeEnd;
    private BpPoint _linkEnd;
    private BpPin? _linkHover;
    private string _linkFromNode = "";
    private string _linkFromPort = "";

    public BlueprintView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;

        _diagnostics = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(DiagnosticsIntervalMs),
        };
        _diagnostics.Tick += (_, _) => PollDiagnostics();
        Loaded += (_, _) =>
        {
            if (_diagnosticsProvider is not null) _diagnostics.Start();
        };
        Unloaded += (_, _) => _diagnostics.Stop();
    }

    /// <summary>诊断快照的来源（宿主提供）。画布每 250ms 拉一次，不订阅每帧。</summary>
    public Func<IReadOnlyDictionary<string, BpValue>>? DiagnosticsProvider
    {
        get => _diagnosticsProvider;
        set
        {
            _diagnosticsProvider = value;
            if (value is null) _diagnostics.Stop();
            else if (IsLoaded) _diagnostics.Start();
        }
    }

    /// <summary>一次编辑意图；宿主负责采用、落库与撤销（画布自己不写文档）。</summary>
    public event Action<BpEdit>? EditCommitted;

    /// <summary>UI 线程全量刷新显示。</summary>
    public void SetSnapshot(BpSnapshot snapshot)
    {
        _snap = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        CancelDrag();
        CloseEditor();
        _selection.RemoveWhere(id => BlueprintMath.FindNode(_snap, id) is null);
        InvalidateVisual();
    }

    /// <summary>缩放与平移复位 1× / 原点（双击空白）。</summary>
    public void ResetView()
    {
        _zoom = 1;
        _pan = default;
        InvalidateVisual();
    }

    // ---- 视图态（供宿主写进/恢复工程存档的 EditorLayout；画布内部手势照旧用私有字段） ----

    /// <summary>画布缩放（1 = 100%）。恢复存档时按画布自身的范围钳制（0.25–2）。</summary>
    public double Zoom
    {
        get => _zoom;
        set
        {
            if (!double.IsFinite(value)) return;
            _zoom = BlueprintMath.ClampZoom(value);
            InvalidateVisual();
        }
    }

    /// <summary>画布横向平移（画布像素）。</summary>
    public double PanX
    {
        get => _pan.X;
        set
        {
            if (!double.IsFinite(value)) return;
            _pan = new BpPoint(value, _pan.Y);
            InvalidateVisual();
        }
    }

    /// <summary>画布纵向平移（画布像素）。</summary>
    public double PanY
    {
        get => _pan.Y;
        set
        {
            if (!double.IsFinite(value)) return;
            _pan = new BpPoint(_pan.X, value);
            InvalidateVisual();
        }
    }

    /// <summary>当前选中集（副本：调用方拿不到画布内部那份集合）。</summary>
    public IReadOnlyCollection<string> SelectedNodeIds => _selection.ToArray();

    /// <summary>整份设置选中集；图里没有的 Id 忽略（存档里的选中集可能指向已删的节点）。</summary>
    public void SetSelection(IEnumerable<string> nodeIds)
    {
        _selection.Clear();
        foreach (var id in nodeIds)
            if (BlueprintMath.FindNode(_snap, id) is not null)
                _selection.Add(id);

        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width;
        double h = double.IsInfinity(availableSize.Height) ? 600 : availableSize.Height;
        return new Size(w, h);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        InvalidateVisual();
    }

    private double ContentHeight => Math.Max(0, RenderSize.Height - StatusBarHeight);

    private BpPoint ToCanvas(BpPoint screen) => BlueprintMath.ToCanvas(screen, _zoom, _pan);

    private BpPoint ToScreen(BpPoint canvas) => BlueprintMath.ToScreen(canvas, _zoom, _pan);

    private BpRect ViewportCanvas
    {
        get
        {
            var topLeft = ToCanvas(new BpPoint(0, 0));
            var bottomRight = ToCanvas(new BpPoint(RenderSize.Width, ContentHeight));
            return new(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
        }
    }

    /// <summary>拖动中的节点用落点画，快照本身不动（快照是只读投影）。</summary>
    private BpPoint PositionOf(BpNode node) =>
        _dragOrigins.ContainsKey(node.Id) ? new BpPoint(node.X + _dragDelta.X, node.Y + _dragDelta.Y) : new BpPoint(node.X, node.Y);

    private string AddressOf(string nodeId)
    {
        foreach (var binding in _snap.Bindings)
            if (string.Equals(binding.NodeId, nodeId, StringComparison.Ordinal))
                return binding.Address;
        return "";
    }

    private void PollDiagnostics()
    {
        if (DiagnosticsProvider is null) return;

        var values = DiagnosticsProvider();
        _values.Clear();
        foreach (var (id, value) in values) _values[id] = value;
        InvalidateVisual();
    }

    private void SetStatus(string? message)
    {
        _status = message;
        InvalidateVisual();
    }

    private void Commit(BpEdit edit) => EditCommitted?.Invoke(edit);

    /// <summary>参数编辑浮层：StaysOpen=true 下必须显式关（ComboBox 的下拉是另一个 Popup，
    /// StaysOpen=false 会在点下拉项时先把父浮层关掉）。</summary>
    private void CloseEditor()
    {
        if (_editor is null) return;
        _editor.IsOpen = false;
        _editor = null;
    }

    private void CancelDrag()
    {
        if (_drag == DragMode.None) return;
        _drag = DragMode.None;
        _dragOrigins.Clear();
        _dragDelta = default;
        _linkHover = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        InvalidateVisual();
    }

    /// <summary>节点库分类（顺序 = 右键菜单顺序）：默认输入类，数学/输出按种类点名。</summary>
    private static int CategoryOf(SignalNodeKind kind) => kind switch
    {
        SignalNodeKind.Add or SignalNodeKind.Multiply or SignalNodeKind.Clamp or SignalNodeKind.Remap or SignalNodeKind.Lfo => 1,
        SignalNodeKind.ParameterOutput => 2,
        _ => 0,
    };

    /// <summary>离散选择参数的显示名（取值域就是 schema 声明的 [Min,Max]）。</summary>
    private static string DiscreteLabel(SignalNodeKind kind, string param, double value) => (kind, param) switch
    {
        (SignalNodeKind.Constant or SignalNodeKind.Remap, "unit") => ((ParamUnit)(int)value).ToString(),
        (SignalNodeKind.Time, "source") => value == 1 ? "Live" : "Transport",
        (SignalNodeKind.AudioBands, "band") => value switch { 0 => "Rms", 1 => "Bass", 2 => "Mid", _ => "High" },
        _ => value.ToString("0.###", CultureInfo.InvariantCulture),
    };

    /// <summary>引脚单位标签口径（与 ParamUnit 同名口径；Unspecified 不是量纲，不显示）。</summary>
    private static string UnitLabel(ParamUnit unit) => unit switch
    {
        ParamUnit.Dimensionless => "1",
        ParamUnit.Ratio => "ratio",
        ParamUnit.Pixels => "px",
        ParamUnit.Degrees => "°",
        ParamUnit.Seconds => "s",
        _ => "?",
    };

    private static string Short(string text) => text.Length <= MaxLabelChars ? text : text[..(MaxLabelChars - 1)] + "…";
}
