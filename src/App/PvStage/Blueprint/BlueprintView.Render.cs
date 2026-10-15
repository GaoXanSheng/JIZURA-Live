using System.Globalization;
using System.Windows;
using System.Windows.Media;
using JizuraLive.Pv;
using JizuraLive.Render.Effects;

namespace JizuraLive.App.PvStage.Blueprint;

public sealed partial class BlueprintView
{
    private static readonly Brush BgBrush = Solid(0x0A, 0x0A, 0x0D);
    private static readonly Brush NodeBrush = Solid(0x16, 0x16, 0x1E);
    private static readonly Brush NodeDisabledBrush = Solid(0x11, 0x11, 0x15);
    private static readonly Brush AccentBrush = Solid(0x5B, 0x8C, 0xFF);
    private static readonly Brush AccentSoftBrush = Solid(0x2A, 0x3A, 0x66);
    private static readonly Brush FgBrush = Solid(0xEC, 0xEC, 0xF2);
    private static readonly Brush DimBrush = Solid(0x9A, 0x9A, 0xA8);
    private static readonly Brush FaintBrush = Solid(0x5E, 0x5E, 0x6C);
    private static readonly Brush InvalidBrush = Solid(0xFF, 0x5F, 0x6E);
    private static readonly Brush ValidBrush = Solid(0x38, 0xE1, 0xC4);
    private static readonly Brush PinInBrush = Solid(0x7A, 0xD1, 0x9E);
    private static readonly Brush PinOutBrush = Solid(0xE1, 0xB3, 0x6B);
    private static readonly Brush PinIllegalBrush = Solid(0x44, 0x44, 0x4E);
    private static readonly Brush StatusBrush = Solid(0xE6, 0x17, 0x17, 0x1E);
    private static readonly Brush MinimapBrush = Solid(0xCC, 0x0E, 0x0E, 0x14);
    private static readonly Brush MarqueeFillBrush = Solid(0x22, 0x5B, 0x8C, 0xFF);
    private static readonly Pen GridPen = FrozenPen(Solid(0x16, 0x16, 0x1C), 1);
    private static readonly Pen SoftPen = FrozenPen(Solid(0x23, 0x23, 0x2C), 1);
    private static readonly Pen NodePen = FrozenPen(Solid(0x2E, 0x2E, 0x3A), 1);
    private static readonly Pen SelPen = FrozenPen(AccentBrush, 1.6);
    private static readonly Pen InvalidPen = FrozenPen(InvalidBrush, 1.6);
    private static readonly Pen EdgePen = FrozenPen(Solid(0x6E, 0x8A, 0xC8), 1.4);
    private static readonly Pen PinPen = FrozenPen(Solid(0x0A, 0x0A, 0x0D), 1);
    private static readonly Pen MinimapPen = FrozenPen(Solid(0x5B, 0x8C, 0xFF), 1);
    private static readonly Pen MarqueePen = FrozenPen(AccentBrush, 1);
    private static readonly Typeface UiFont = new("Microsoft YaHei UI");
    private static readonly Typeface MonoFont = new("Consolas");

    protected override void OnRender(DrawingContext dc)
    {
        double w = RenderSize.Width, h = RenderSize.Height;
        if (w <= 0 || h <= 0) return;

        double contentH = ContentHeight;
        dc.DrawRectangle(BgBrush, null, new Rect(0, 0, w, h));

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, contentH)));
        RenderGrid(dc, w, contentH);

        dc.PushTransform(new TranslateTransform(_pan.X, _pan.Y));
        dc.PushTransform(new ScaleTransform(_zoom, _zoom));
        foreach (var edge in _snap.Edges) RenderEdge(dc, edge);
        if (_drag == DragMode.Link) RenderLinkPreview(dc);
        foreach (var node in _snap.Nodes) RenderNode(dc, node);
        dc.Pop();
        dc.Pop();

        if (_drag == DragMode.Marquee) dc.DrawRectangle(MarqueeFillBrush, MarqueePen, MarqueeRect());
        RenderMinimap(dc, w, contentH);
        dc.Pop();

        RenderStatusBar(dc, w, h);
    }

    private void RenderGrid(DrawingContext dc, double w, double h)
    {
        double step = BlueprintMath.GridSize;
        while (step * _zoom < 12) step *= 2;

        double left = (0 - _pan.X) / _zoom;
        for (double x = Math.Ceiling(left / step) * step; x * _zoom + _pan.X <= w; x += step)
        {
            double sx = x * _zoom + _pan.X;
            dc.DrawLine(GridPen, new Point(sx, 0), new Point(sx, h));
        }

        double top = (0 - _pan.Y) / _zoom;
        for (double y = Math.Ceiling(top / step) * step; y * _zoom + _pan.Y <= h; y += step)
        {
            double sy = y * _zoom + _pan.Y;
            dc.DrawLine(GridPen, new Point(0, sy), new Point(w, sy));
        }
    }

    private void RenderEdge(DrawingContext dc, BpEdge edge)
    {
        if (!BlueprintMath.TryEdgeCurve(_snap, edge, out var p0, out var c1, out var c2, out var p1)) return;

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(ToPoint(p0), false, false);
            g.BezierTo(ToPoint(c1), ToPoint(c2), ToPoint(p1), true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, EdgePen, geometry);
    }

    private void RenderLinkPreview(DrawingContext dc)
    {
        if (BlueprintMath.PinPoint(_snap, _linkFromNode, _linkFromPort) is not { } from) return;

        double handle = Math.Clamp(Math.Abs(_linkEnd.X - from.X) * 0.5, 30, 160);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(ToPoint(from), false, false);
            g.BezierTo(new Point(from.X + handle, from.Y), new Point(_linkEnd.X - handle, _linkEnd.Y), ToPoint(_linkEnd), true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, EdgePen, geometry);
    }

    private void RenderNode(DrawingContext dc, BpNode node)
    {
        var def = SignalNodeCatalog.Get(node.Kind);
        var origin = PositionOf(node);

        if (def is null)
        {
            var placeholder = new Rect(origin.X, origin.Y, BlueprintMath.NodeWidth, BlueprintMath.UnknownNodeHeight);
            dc.DrawRoundedRectangle(NodeDisabledBrush, InvalidPen, placeholder, 4, 4);
            dc.DrawText(Text("未知节点（已禁用）", 11, DimBrush), new Point(placeholder.X + 8, placeholder.Y + 9));
            return;
        }

        var card = new Rect(origin.X, origin.Y, BlueprintMath.NodeWidth, BlueprintMath.NodeHeight(def));
        bool selected = _selection.Contains(node.Id);
        bool invalid = !node.Enabled || (_values.TryGetValue(node.Id, out var value) && !value.Valid);

        dc.DrawRoundedRectangle(node.Enabled ? NodeBrush : NodeDisabledBrush,
            selected ? SelPen : invalid ? InvalidPen : NodePen, card, 4, 4);

        dc.DrawText(Text(node.Kind.ToString(), 11, node.Enabled ? FgBrush : FaintBrush), new Point(card.X + 8, card.Y + 6));
        dc.DrawLine(SoftPen, new Point(card.X + 1, card.Y + BlueprintMath.TitleHeight),
            new Point(card.Right - 1, card.Y + BlueprintMath.TitleHeight));

        RenderPins(dc, node, def, card);
        RenderParams(dc, node, def, card);

        if (BlueprintMath.HasOutput(def))
        {
            var (text, brush) = ValueText(node);
            dc.DrawText(Text(text, 10, brush, MonoFont), new Point(card.X + 8, card.Y + BlueprintMath.ValueRowY(def) - 7));
        }
    }

    private void RenderPins(DrawingContext dc, BpNode node, SignalNodeDef def, Rect card)
    {
        for (var i = 0; i < def.Ports.Length; i++)
        {
            var port = def.Ports[i];
            double y = card.Y + BlueprintMath.PinRowY(i);
            double x = port.IsInput ? card.X : card.Right;
            var (brush, radius) = PinStyle(node, port, x, y);
            dc.DrawEllipse(brush, PinPen, new Point(x, y), radius, radius);

            var label = Text(PortLabel(node, port), 10, port.Required || !port.IsInput ? DimBrush : FaintBrush);
            dc.DrawText(label, new Point(port.IsInput ? x + 8 : x - 8 - label.Width, y - 7));
        }
    }

    private void RenderParams(DrawingContext dc, BpNode node, SignalNodeDef def, Rect card)
    {
        int rows = BlueprintMath.ParamRowCount(def);
        for (var i = 0; i < rows; i++)
        {
            double y = card.Y + BlueprintMath.ParamRowY(def, i);
            dc.DrawLine(SoftPen, new Point(card.X + 1, y - BlueprintMath.ParamRowHeight / 2),
                new Point(card.Right - 1, y - BlueprintMath.ParamRowHeight / 2));

            string name = BlueprintMath.ParamNameAt(def, i);
            dc.DrawText(Text(name == BlueprintMath.TargetRow ? "target" : name, 10, FaintBrush), new Point(card.X + 8, y - 7));

            var value = Text(Short(ParamText(node, def, name)), 10, FgBrush, MonoFont);
            dc.DrawText(value, new Point(card.Right - 8 - value.Width, y - 7));
        }
    }

    private (Brush Brush, double Radius) PinStyle(BpNode node, SignalPortDef port, double x, double y)
    {
        if (_drag != DragMode.Link) return (port.IsInput ? PinInBrush : PinOutBrush, BlueprintMath.PinRadius);

        if (!port.IsInput) return (PinOutBrush, BlueprintMath.PinRadius);

        bool hovered = _linkHover is { } hit &&
            string.Equals(hit.NodeId, node.Id, StringComparison.Ordinal) &&
            string.Equals(hit.Port, port.Name, StringComparison.Ordinal);

        if (BlueprintMath.CheckConnect(_snap, _linkFromNode, _linkFromPort, node.Id, port.Name) != BpConnectIssue.None)
            return (PinIllegalBrush, BlueprintMath.PinRadius);

        return (AccentBrush, hovered ? BlueprintMath.PinRadius + 2 : BlueprintMath.PinRadius);
    }

    private static string PortLabel(BpNode node, SignalPortDef port)
    {
        if (!node.PortUnits.TryGetValue(port.Name, out var unit) || unit == ParamUnit.Unspecified) return port.Name;
        return $"{port.Name} [{UnitLabel(unit)}]";
    }

    private string ParamText(BpNode node, SignalNodeDef def, string name)
    {
        if (name == BlueprintMath.TargetRow)
        {
            var address = AddressOf(node.Id);
            return address.Length > 0 ? address : "（未绑定）";
        }

        var param = SignalNodeCatalog.FindParam(def, name);
        if (param is null) return "";

        if (param.Kind == SignalParamKind.Text)
        {
            var text = node.Texts.TryGetValue(name, out var stored) ? stored : param.DefaultText;
            return text.Length > 0 ? text : "（未设置）";
        }

        double value = node.Params.TryGetValue(name, out var storedValue) ? storedValue : param.Default;
        return SignalNodeCatalog.IsDiscreteParam(node.Kind, name)
            ? DiscreteLabel(node.Kind, name, value)
            : value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private (string Text, Brush Brush) ValueText(BpNode node)
    {
        if (!_values.TryGetValue(node.Id, out var value)) return ("—", FaintBrush);
        return value.Valid
            ? ($"✓ {value.Value:0.###}", ValidBrush)
            : ("✗ 无效", InvalidBrush);
    }

    private Rect MarqueeRect() => new(
        Math.Min(_dragStartScreen.X, _marqueeEnd.X),
        Math.Min(_dragStartScreen.Y, _marqueeEnd.Y),
        Math.Abs(_marqueeEnd.X - _dragStartScreen.X),
        Math.Abs(_marqueeEnd.Y - _dragStartScreen.Y));

    private void RenderMinimap(DrawingContext dc, double w, double contentH)
    {
        var box = new BpRect(w - MinimapWidth - MinimapMargin, contentH - MinimapHeight - MinimapMargin, MinimapWidth, MinimapHeight);
        if (box.X < 0 || box.Y < 0) return;

        dc.DrawRoundedRectangle(MinimapBrush, SoftPen, new Rect(box.X, box.Y, box.Width, box.Height), 3, 3);

        var viewport = ViewportCanvas;
        var content = BlueprintMath.Union(BlueprintMath.ContentBounds(_snap), viewport);
        if (content.Width <= 0 || content.Height <= 0) return;

        foreach (var node in _snap.Nodes)
        {
            var rect = BlueprintMath.NodeRect(node);
            var topLeft = BlueprintMath.ToMinimap(new BpPoint(rect.X, rect.Y), content, box);
            var bottomRight = BlueprintMath.ToMinimap(new BpPoint(rect.Right, rect.Bottom), content, box);
            dc.DrawRectangle(_selection.Contains(node.Id) ? AccentSoftBrush : DimBrush, null,
                new Rect(topLeft.X, topLeft.Y, Math.Max(2, bottomRight.X - topLeft.X), Math.Max(2, bottomRight.Y - topLeft.Y)));
        }

        var viewTopLeft = BlueprintMath.ToMinimap(new BpPoint(viewport.X, viewport.Y), content, box);
        var viewBottomRight = BlueprintMath.ToMinimap(new BpPoint(viewport.Right, viewport.Bottom), content, box);
        dc.DrawRectangle(null, MinimapPen, new Rect(viewTopLeft.X, viewTopLeft.Y,
            Math.Max(1, viewBottomRight.X - viewTopLeft.X), Math.Max(1, viewBottomRight.Y - viewTopLeft.Y)));
    }

    private void RenderStatusBar(DrawingContext dc, double w, double h)
    {
        double top = h - StatusBarHeight;
        dc.DrawRectangle(StatusBrush, null, new Rect(0, top, w, StatusBarHeight));
        dc.DrawLine(SoftPen, new Point(0, top), new Point(w, top));

        string message = _status ?? "中键平移 · Ctrl+滚轮缩放 · 双击空白复位 · 右键空白加节点";
        dc.DrawText(Text(message, 11, _status is null ? FaintBrush : InvalidBrush), new Point(8, top + 4));

        var zoom = Text($"{(int)Math.Round(_zoom * 100)}%", 11, DimBrush, MonoFont);
        dc.DrawText(zoom, new Point(w - 8 - zoom.Width, top + 4));
    }

    private static Point ToPoint(BpPoint p) => new(p.X, p.Y);

    private FormattedText Text(string text, double size, Brush brush, Typeface? typeface = null) => new(
        text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface ?? UiFont, size, brush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static SolidColorBrush Solid(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush Solid(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }
}
