using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using JizuraLive.Pv;

namespace JizuraLive.App.PvStage.Blueprint;

public sealed partial class BlueprintView
{
    // 参数行按压的候选编辑：拖出位移则转节点移动丢弃它，原地松手才真正打开
    private (string NodeId, string Param)? _pendingEdit;

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        var screen = new BpPoint(e.GetPosition(this).X, e.GetPosition(this).Y);
        if (screen.Y >= ContentHeight) return;

        CloseEditor();

        if (e.ChangedButton == MouseButton.Right)
        {
            OpenContextMenu(screen);
            return;
        }

        if (e.ChangedButton == MouseButton.Middle)
        {
            _drag = DragMode.Pan;
            _dragLastScreen = screen;
            CaptureMouse();
            return;
        }

        if (e.ChangedButton != MouseButton.Left) return;
        e.Handled = true;

        var canvas = ToCanvas(screen);
        if (e.ClickCount == 2 && BlueprintMath.HitNode(_snap, canvas) is null)
        {
            ResetView();
            return;
        }

        // 参数行：按下先当拖动候选（拖出位移=挪节点），原地松手才开编辑器——
        // 否则节点主体的每次按压都被编辑吃掉，拖动只剩标题栏一条缝
        if (BlueprintMath.HitNode(_snap, canvas) is { } nodeId &&
            BlueprintMath.HitParamRow(_snap, nodeId, canvas) is { } paramName)
        {
            _pendingEdit = (nodeId, paramName);
            StartNodeDrag(nodeId, screen, canvas);
            return;
        }

        if (BlueprintMath.HitPin(_snap, canvas) is { } pin)
        {
            // 只有输出引脚发起连线；输入引脚的改接走"右键删旧边 + 重连"
            if (!pin.IsInput) StartLink(pin, canvas);
            return;
        }

        if (BlueprintMath.HitNode(_snap, canvas) is { } hit)
        {
            StartNodeDrag(hit, screen, canvas);
            return;
        }

        StartMarquee(screen);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag == DragMode.None) return;

        var screen = new BpPoint(e.GetPosition(this).X, e.GetPosition(this).Y);
        switch (_drag)
        {
            case DragMode.Pan:
                _pan = new BpPoint(_pan.X + screen.X - _dragLastScreen.X, _pan.Y + screen.Y - _dragLastScreen.Y);
                _dragLastScreen = screen;
                break;

            case DragMode.Node:
                var canvas = ToCanvas(screen);
                _dragDelta = BlueprintMath.DragDelta(_dragStartCanvas, canvas);
                break;

            case DragMode.Marquee:
                _marqueeEnd = screen;
                SelectInMarquee();
                break;

            case DragMode.Link:
                _linkEnd = ToCanvas(screen);
                _linkHover = BlueprintMath.HitPin(_snap, _linkEnd);
                break;
        }

        InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag == DragMode.None) return;
        if (e.ChangedButton != MouseButton.Left && e.ChangedButton != MouseButton.Middle) return;

        if (_drag == DragMode.Node && (_dragDelta.X != 0 || _dragDelta.Y != 0)) EmitMove();
        else if (_drag == DragMode.Node && _pendingEdit is { } pe)
        {
            var up = e.GetPosition(this);
            OpenParamEditor(pe.NodeId, pe.Param, new BpPoint(up.X, up.Y));
        }
        else if (_drag == DragMode.Link) FinishLink();
        _pendingEdit = null;

        _drag = DragMode.None;
        _dragOrigins.Clear();
        _dragDelta = default;
        _linkHover = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        CancelDrag();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        e.Handled = true;

        var screen = new BpPoint(e.GetPosition(this).X, e.GetPosition(this).Y);
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            double zoom = BlueprintMath.ClampZoom(_zoom * (e.Delta > 0 ? 1.25 : 0.8));
            _pan = BlueprintMath.ZoomPan(_pan, _zoom, zoom, screen);
            _zoom = zoom;
        }
        else
        {
            _pan = new BpPoint(_pan.X, _pan.Y + e.Delta / 120.0 * 48);
        }

        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Escape:
                CancelDrag();
                e.Handled = true;
                break;

            case Key.Delete:
                RemoveSelected();
                e.Handled = true;
                break;
        }
    }

    // ---- 拖动 ----

    private void StartNodeDrag(string nodeId, BpPoint screen, BpPoint canvas)
    {
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (shift)
        {
            if (!_selection.Add(nodeId)) _selection.Remove(nodeId);
        }
        else if (!_selection.Contains(nodeId))
        {
            _selection.Clear();
            _selection.Add(nodeId);
        }

        _dragOrigins.Clear();
        foreach (var id in _selection)
            if (BlueprintMath.FindNode(_snap, id) is { } node)
                _dragOrigins[id] = new BpPoint(node.X, node.Y);

        if (_dragOrigins.Count == 0)
        {
            InvalidateVisual();
            return;
        }

        _drag = DragMode.Node;
        _dragStartScreen = screen;
        _dragStartCanvas = canvas;
        _dragDelta = default;
        CaptureMouse();
        InvalidateVisual();
    }

    private void StartMarquee(BpPoint screen)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0) _selection.Clear();

        _drag = DragMode.Marquee;
        _dragStartScreen = screen;
        _marqueeEnd = screen;
        CaptureMouse();
        InvalidateVisual();
    }

    private void StartLink(BpPin pin, BpPoint canvas)
    {
        _drag = DragMode.Link;
        _linkFromNode = pin.NodeId;
        _linkFromPort = pin.Port;
        _linkEnd = canvas;
        _linkHover = null;
        CaptureMouse();
        InvalidateVisual();
    }

    private void SelectInMarquee()
    {
        _selection.Clear();
        foreach (var id in BlueprintMath.MarqueeSelection(_snap, _dragStartScreen, _marqueeEnd, _zoom, _pan))
            _selection.Add(id);
    }

    private void EmitMove()
    {
        var moves = BlueprintMath.DragMoves(_dragOrigins, _dragDelta);
        if (moves.Count > 0) Commit(new BpEdit { Kind = BpEditKind.MoveNodes, Moves = moves });
    }

    private void FinishLink()
    {
        var hover = _linkHover;
        _linkHover = null;

        if (hover is not { IsInput: true })
        {
            SetStatus(hover is null ? null : "端口不匹配：连线要落在输入引脚上");
            return;
        }

        var target = hover.Value;
        var issue = BlueprintMath.CheckConnect(_snap, _linkFromNode, _linkFromPort, target.NodeId, target.Port);
        if (issue != BpConnectIssue.None)
        {
            SetStatus(Describe(issue));
            return;
        }

        SetStatus(null);
        Commit(new BpEdit
        {
            Kind = BpEditKind.Connect,
            FromNode = _linkFromNode,
            FromPort = _linkFromPort,
            ToNode = target.NodeId,
            ToPort = target.Port,
        });
    }

    private static string Describe(BpConnectIssue issue) => issue switch
    {
        BpConnectIssue.PortMismatch => "端口不匹配：只能从输出引脚连到输入引脚",
        BpConnectIssue.InputOccupied => "输入已被占用：一个输入至多一条连线（先删掉旧线）",
        BpConnectIssue.Cycle => "成环：这条线会让信号绕回自己",
        _ => "",
    };

    private void RemoveSelected()
    {
        if (_selection.Count == 0) return;

        var ids = new List<string>(_selection);
        _selection.Clear();
        Commit(new BpEdit { Kind = BpEditKind.RemoveNodes, NodeIds = ids });
        InvalidateVisual();
    }

    // ---- 右键菜单 ----

    private void OpenContextMenu(BpPoint screen)
    {
        var canvas = ToCanvas(screen);

        if (BlueprintMath.HitNode(_snap, canvas) is { } nodeId && BlueprintMath.FindNode(_snap, nodeId) is { } node)
        {
            if (!_selection.Contains(nodeId))
            {
                _selection.Clear();
                _selection.Add(nodeId);
            }

            InvalidateVisual();
            var menu = new ContextMenu { PlacementTarget = this, Placement = PlacementMode.MousePoint };
            var toggle = new MenuItem { Header = node.Enabled ? "禁用" : "启用" };
            toggle.Click += (_, _) => Commit(new BpEdit
            {
                Kind = BpEditKind.SetEnabled,
                NodeIds = new List<string>(_selection),
                Enabled = !node.Enabled,
            });
            menu.Items.Add(toggle);

            var remove = new MenuItem { Header = "删除" };
            remove.Click += (_, _) => RemoveSelected();
            menu.Items.Add(remove);

            menu.IsOpen = true;
            return;
        }

        if (BlueprintMath.HitEdge(_snap, canvas) is { } edgeId)
        {
            Commit(new BpEdit { Kind = BpEditKind.Disconnect, EdgeId = edgeId });
            return;
        }

        OpenAddMenu(canvas);
    }

    private void OpenAddMenu(BpPoint canvas)
    {
        var at = BlueprintMath.Snap(canvas);
        var menu = new ContextMenu { PlacementTarget = this, Placement = PlacementMode.MousePoint };

        for (var category = 0; category < Categories.Length; category++)
        {
            var submenu = new MenuItem { Header = Categories[category] };
            foreach (var def in SignalNodeCatalog.All)
            {
                if (CategoryOf(def.Kind) != category) continue;

                var kind = def.Kind;
                var item = new MenuItem { Header = kind.ToString() };
                item.Click += (_, _) => Commit(new BpEdit
                {
                    Kind = BpEditKind.AddNode,
                    AddKind = kind,
                    X = at.X,
                    Y = at.Y,
                });
                submenu.Items.Add(item);
            }

            if (submenu.Items.Count > 0) menu.Items.Add(submenu);
        }

        menu.IsOpen = true;
    }

    // ---- 参数编辑 ----

    private void OpenParamEditor(string nodeId, string paramName, BpPoint screen)
    {
        var node = BlueprintMath.FindNode(_snap, nodeId);
        if (node is null) return;
        var def = SignalNodeCatalog.Get(node.Kind);
        if (def is null) return;

        var popup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.Relative,
            HorizontalOffset = screen.X + 8,
            VerticalOffset = screen.Y + 8,
            StaysOpen = true,
        };

        FrameworkElement? editor;
        if (paramName == BlueprintMath.TargetRow)
        {
            editor = AddressEditor(node, AddressOf(node.Id), binding: true, paramName: "");
        }
        else
        {
            var param = SignalNodeCatalog.FindParam(def, paramName);
            if (param is null) return;

            editor = param.Kind == SignalParamKind.Text
                ? AddressEditor(node, node.Texts.TryGetValue(paramName, out var text) ? text : param.DefaultText, binding: false, paramName)
                : SignalNodeCatalog.IsDiscreteParam(node.Kind, paramName)
                    ? DiscreteEditor(node, param)
                    : NumberEditor(node, param, popup);
        }

        popup.Child = new Border
        {
            Background = StatusBrush,
            BorderBrush = AccentSoftBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6),
            Child = editor,
        };

        _editor = popup;
        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_editor, popup)) _editor = null;
        };
        popup.IsOpen = true;
    }

    private FrameworkElement NumberEditor(BpNode node, SignalParamDef param, Popup popup)
    {
        double current = node.Params.TryGetValue(param.Name, out var stored) ? stored : param.Default;
        var input = new TextBox
        {
            Text = current.ToString("0.###", CultureInfo.InvariantCulture),
            MinWidth = 120,
            Foreground = FgBrush,
            Background = BgBrush,
            BorderBrush = AccentSoftBrush,
        };
        input.SelectAll();

        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                CloseEditor();
                e.Handled = true;
                return;
            }

            if (e.Key != Key.Enter) return;
            e.Handled = true;
            CloseEditor();

            // 编辑期就用编译期同一套合法性判定（有限 + 域内 + 离散取整），不另立规则
            if (!double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
                !SignalGraph.IsLegalNumber(node.Kind, param, value))
            {
                SetStatus($"参数不合法：{param.Name} = {input.Text}");
                return;
            }

            SetStatus(null);
            Commit(new BpEdit { Kind = BpEditKind.SetParam, NodeIds = [node.Id], ParamName = param.Name, Value = value });
        };

        popup.Opened += (_, _) => input.Focus();
        return input;
    }

    private FrameworkElement DiscreteEditor(BpNode node, SignalParamDef param)
    {
        double current = node.Params.TryGetValue(param.Name, out var stored) ? stored : param.Default;
        var combo = new ComboBox { MinWidth = 120 };
        for (double value = param.Min; value <= param.Max; value += 1)
            combo.Items.Add(DiscreteLabel(node.Kind, param.Name, value));

        combo.SelectedIndex = Math.Clamp((int)Math.Round(current - param.Min), 0, combo.Items.Count - 1);
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex < 0) return;
            CloseEditor();
            Commit(new BpEdit
            {
                Kind = BpEditKind.SetParam,
                NodeIds = [node.Id],
                ParamName = param.Name,
                Value = param.Min + combo.SelectedIndex,
            });
        };
        return combo;
    }

    private FrameworkElement AddressEditor(BpNode node, string current, bool binding, string paramName)
    {
        var combo = new ComboBox { MinWidth = 180 };
        combo.Items.Add("（无）");
        if (current.Length > 0) combo.Items.Add(current);
        foreach (var option in _snap.AddressOptions)
            if (!string.Equals(option, current, StringComparison.Ordinal))
                combo.Items.Add(option);

        combo.SelectedIndex = current.Length > 0 ? 1 : 0;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex < 0) return;
            CloseEditor();

            var address = combo.SelectedIndex == 0 ? "" : (string)combo.Items[combo.SelectedIndex]!;
            Commit(binding
                ? new BpEdit { Kind = BpEditKind.SetBinding, NodeIds = [node.Id], Text = address }
                : new BpEdit { Kind = BpEditKind.SetText, NodeIds = [node.Id], ParamName = paramName, Text = address });
        };
        return combo;
    }
}
