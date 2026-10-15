using JizuraLive.Pv;

namespace JizuraLive.App.PvStage.Blueprint;

/// <summary>画布坐标或屏幕坐标上的一个点（由调用点决定口径），无 WPF 依赖。</summary>
public readonly record struct BpPoint(double X, double Y);

/// <summary>轴对齐矩形。</summary>
public readonly record struct BpRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public bool Contains(BpPoint p) => p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;
}

/// <summary>命中到的引脚。</summary>
public readonly record struct BpPin(string NodeId, string Port, bool IsInput);

/// <summary>
/// 连线预测的拒绝原因。与 <c>SignalGraph.TryAddEdge</c> / <c>SignalCompiler</c> 的**当场可判部分**同源：
/// 端口存在且方向为 Output→Input（= PortMismatch）、一个输入至多一条边（= InputOccupied）、不成环（= Cycle）。
/// 单位统一只在编译器里判（画布不复制那条规则），所以这里没有"单位不匹配"这一档。
/// </summary>
public enum BpConnectIssue
{
    None,
    PortMismatch,
    InputOccupied,
    Cycle,
}

/// <summary>蓝图画布的纯几何与纯预测（吸附、坐标变换、引脚命中、连线合法性、拓扑自动布局），无 WPF 依赖。</summary>
public static class BlueprintMath
{
    /// <summary>节点拖动吸附步长（px，画布坐标）。</summary>
    public const double GridSize = 8;

    public const double MinZoom = 0.25;
    public const double MaxZoom = 2.0;

    public const double NodeWidth = 168;
    public const double TitleHeight = 26;
    public const double PinRowHeight = 18;
    public const double ParamRowHeight = 20;
    public const double ValueRowHeight = 16;
    public const double RowGap = 4;

    /// <summary>未知节点（存档里手改出来的 Kind）占位卡片的高度。</summary>
    public const double UnknownNodeHeight = 34;

    public const double PinRadius = 4;
    public const double PinHitRadius = 9;
    public const double EdgeHitTolerance = 6;

    /// <summary>自动布局的列间距 / 行间距。</summary>
    public const double LayoutGapX = 64;
    public const double LayoutGapY = 28;

    /// <summary>ParameterOutput 的绑定目标行：不是 schema 参数（地址存在 Binding 上），但要在节点体里能编辑。</summary>
    public const string TargetRow = "#target";

    private const int EdgeSamples = 32;

    // ---- 吸附 ----

    public static double Snap(double value) => Math.Round(value / GridSize, MidpointRounding.AwayFromZero) * GridSize;

    public static BpPoint Snap(BpPoint p) => new(Snap(p.X), Snap(p.Y));

    // ---- 坐标 ↔ 画布 ----

    public static BpPoint ToScreen(BpPoint canvas, double zoom, BpPoint pan) =>
        new(canvas.X * zoom + pan.X, canvas.Y * zoom + pan.Y);

    public static BpPoint ToCanvas(BpPoint screen, double zoom, BpPoint pan) =>
        zoom > 0 ? new((screen.X - pan.X) / zoom, (screen.Y - pan.Y) / zoom) : pan;

    public static double ClampZoom(double zoom) => Math.Clamp(zoom, MinZoom, MaxZoom);

    /// <summary>以 anchor（屏幕点）为锚改缩放：锚点下的画布点保持不动。</summary>
    public static BpPoint ZoomPan(BpPoint pan, double oldZoom, double newZoom, BpPoint anchor)
    {
        var canvas = ToCanvas(anchor, oldZoom, pan);
        return new(anchor.X - canvas.X * newZoom, anchor.Y - canvas.Y * newZoom);
    }

    // ---- 节点体内布局（渲染与命中测试共用同一份行高/行序）----

    public static bool HasOutput(SignalNodeDef def)
    {
        foreach (var port in def.Ports)
            if (!port.IsInput)
                return true;
        return false;
    }

    /// <summary>参数行数 = schema 参数 + ParameterOutput 的绑定目标行。</summary>
    public static int ParamRowCount(SignalNodeDef def) =>
        def.Params.Length + (def.Kind == SignalNodeKind.ParameterOutput ? 1 : 0);

    /// <summary>第 index 个引脚行的中心 y（相对节点左上角）。行序 = schema 端口声明序。</summary>
    public static double PinRowY(int index) => TitleHeight + RowGap + (index + 0.5) * PinRowHeight;

    public static double ParamRowTop(SignalNodeDef def) => TitleHeight + RowGap + def.Ports.Length * PinRowHeight + RowGap;

    public static double ParamRowY(SignalNodeDef def, int index) => ParamRowTop(def) + (index + 0.5) * ParamRowHeight;

    public static double ValueRowY(SignalNodeDef def) =>
        ParamRowTop(def) + ParamRowCount(def) * ParamRowHeight + ValueRowHeight / 2;

    public static double NodeHeight(SignalNodeDef def) =>
        ParamRowTop(def) + ParamRowCount(def) * ParamRowHeight + (HasOutput(def) ? ValueRowHeight : 0) + RowGap;

    public static BpRect NodeRect(BpNode node)
    {
        var def = SignalNodeCatalog.Get(node.Kind);
        return new(node.X, node.Y, NodeWidth, def is null ? UnknownNodeHeight : NodeHeight(def));
    }

    /// <summary>第 index 个参数行的名字：先 schema 参数，绑定目标行排最后。</summary>
    public static string ParamNameAt(SignalNodeDef def, int index) =>
        index < def.Params.Length ? def.Params[index].Name : TargetRow;

    // ---- 命中 ----

    public static BpNode? FindNode(BpSnapshot snap, string id)
    {
        foreach (var node in snap.Nodes)
            if (string.Equals(node.Id, id, StringComparison.Ordinal))
                return node;
        return null;
    }

    /// <summary>引脚中心（画布坐标）；节点或端口不存在返回 null。</summary>
    public static BpPoint? PinPoint(BpSnapshot snap, string nodeId, string port)
    {
        var node = FindNode(snap, nodeId);
        if (node is null) return null;
        var def = SignalNodeCatalog.Get(node.Kind);
        if (def is null) return null;

        for (var i = 0; i < def.Ports.Length; i++)
        {
            var p = def.Ports[i];
            if (!string.Equals(p.Name, port, StringComparison.Ordinal)) continue;
            return new(node.X + (p.IsInput ? 0 : NodeWidth), node.Y + PinRowY(i));
        }

        return null;
    }

    /// <summary>最近的引脚（半径内）；后画的节点在上层，所以倒序命中。</summary>
    public static BpPin? HitPin(BpSnapshot snap, BpPoint canvas)
    {
        for (var n = snap.Nodes.Count - 1; n >= 0; n--)
        {
            var node = snap.Nodes[n];
            var def = SignalNodeCatalog.Get(node.Kind);
            if (def is null) continue;

            for (var i = 0; i < def.Ports.Length; i++)
            {
                var port = def.Ports[i];
                double x = node.X + (port.IsInput ? 0 : NodeWidth);
                double y = node.Y + PinRowY(i);
                if (Math.Abs(canvas.X - x) > PinHitRadius || Math.Abs(canvas.Y - y) > PinHitRadius) continue;
                return new BpPin(node.Id, port.Name, port.IsInput);
            }
        }

        return null;
    }

    /// <summary>节点卡片命中（最上层优先）；未知节点也吃命中（它占位、可选中、可删）。</summary>
    public static string? HitNode(BpSnapshot snap, BpPoint canvas)
    {
        for (var n = snap.Nodes.Count - 1; n >= 0; n--)
        {
            var node = snap.Nodes[n];
            if (NodeRect(node).Contains(canvas)) return node.Id;
        }

        return null;
    }

    /// <summary>边命中（到贝塞尔折线的距离，容差内取最近的一条）。</summary>
    public static string? HitEdge(BpSnapshot snap, BpPoint canvas, double tolerance = EdgeHitTolerance)
    {
        string? best = null;
        double bestDistance = tolerance;

        foreach (var edge in snap.Edges)
        {
            if (!TryEdgeCurve(snap, edge, out var p0, out var c1, out var c2, out var p1)) continue;

            var previous = p0;
            for (var i = 1; i <= EdgeSamples; i++)
            {
                var current = Bezier(p0, c1, c2, p1, (double)i / EdgeSamples);
                double distance = DistanceToSegment(canvas, previous, current);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = edge.Id;
                }

                previous = current;
            }
        }

        return best;
    }

    /// <summary>边的贝塞尔控制点：水平出柄，柄长随跨度伸缩（限制在 30..160）。</summary>
    public static bool TryEdgeCurve(BpSnapshot snap, BpEdge edge,
        out BpPoint p0, out BpPoint c1, out BpPoint c2, out BpPoint p1)
    {
        p0 = default;
        c1 = default;
        c2 = default;
        p1 = default;

        if (PinPoint(snap, edge.FromNode, edge.FromPort) is not { } from) return false;
        if (PinPoint(snap, edge.ToNode, edge.ToPort) is not { } to) return false;

        p0 = from;
        p1 = to;
        double handle = Math.Clamp(Math.Abs(to.X - from.X) * 0.5, 30, 160);
        c1 = new(from.X + handle, from.Y);
        c2 = new(to.X - handle, to.Y);
        return true;
    }

    public static BpPoint Bezier(BpPoint p0, BpPoint c1, BpPoint c2, BpPoint p1, double t)
    {
        double s = 1 - t;
        double a = s * s * s, b = 3 * s * s * t, c = 3 * s * t * t, d = t * t * t;
        return new(a * p0.X + b * c1.X + c * c2.X + d * p1.X, a * p0.Y + b * c1.Y + c * c2.Y + d * p1.Y);
    }

    // ---- 连线合法性预测 ----

    /// <summary>预测"从 fromNode.fromPort 连到 toNode.toPort"是否合法；拒绝原因见 <see cref="BpConnectIssue"/>。</summary>
    public static BpConnectIssue CheckConnect(BpSnapshot snap, string fromNode, string fromPort, string toNode, string toPort)
    {
        var from = FindNode(snap, fromNode);
        var to = FindNode(snap, toNode);
        if (from is null || to is null) return BpConnectIssue.PortMismatch;

        var fromDef = SignalNodeCatalog.Get(from.Kind);
        var toDef = SignalNodeCatalog.Get(to.Kind);
        if (fromDef is null || toDef is null) return BpConnectIssue.PortMismatch;

        var fromPortDef = SignalNodeCatalog.FindPort(fromDef, fromPort);
        var toPortDef = SignalNodeCatalog.FindPort(toDef, toPort);
        if (fromPortDef is null || toPortDef is null) return BpConnectIssue.PortMismatch;
        if (fromPortDef.IsInput || !toPortDef.IsInput) return BpConnectIssue.PortMismatch;

        if (string.Equals(fromNode, toNode, StringComparison.Ordinal)) return BpConnectIssue.Cycle;

        foreach (var edge in snap.Edges)
            if (string.Equals(edge.ToNode, toNode, StringComparison.Ordinal) &&
                string.Equals(edge.ToPort, toPort, StringComparison.Ordinal))
                return BpConnectIssue.InputOccupied;

        return CanReach(snap.Edges, toNode, fromNode) ? BpConnectIssue.Cycle : BpConnectIssue.None;
    }

    /// <summary>沿边方向能否从 start 走到 target（= 加一条 target→start 的边会成环）。</summary>
    private static bool CanReach(IReadOnlyList<BpEdge> edges, string start, string target)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(start);
        seen.Add(start);

        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (string.Equals(id, target, StringComparison.Ordinal)) return true;

            foreach (var edge in edges)
            {
                if (!string.Equals(edge.FromNode, id, StringComparison.Ordinal)) continue;
                if (seen.Add(edge.ToNode)) stack.Push(edge.ToNode);
            }
        }

        return false;
    }

    // ---- 旧工程无坐标时的拓扑自动布局 ----

    /// <summary>
    /// 按 Kahn 深度分列：输入在左（深度 0）、输出在右，同列按 Id 序纵向堆叠。
    /// 环上的节点排不出序（Kahn 残余），统一放最后一列而不是丢掉——不丢数据。
    /// </summary>
    public static IReadOnlyDictionary<string, BpPoint> AutoLayout(BpSnapshot snap)
    {
        var depth = new Dictionary<string, int>(StringComparer.Ordinal);
        var inDegree = new Dictionary<string, int>(StringComparer.Ordinal);
        var successors = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var node in snap.Nodes)
        {
            depth[node.Id] = 0;
            inDegree[node.Id] = 0;
        }

        foreach (var edge in snap.Edges)
        {
            if (!inDegree.ContainsKey(edge.FromNode) || !inDegree.ContainsKey(edge.ToNode)) continue;
            if (string.Equals(edge.FromNode, edge.ToNode, StringComparison.Ordinal)) continue;

            inDegree[edge.ToNode]++;
            if (!successors.TryGetValue(edge.FromNode, out var list)) successors[edge.FromNode] = list = new List<string>();
            list.Add(edge.ToNode);
        }

        var ready = new PriorityQueue<string, string>(StringComparer.Ordinal);
        foreach (var node in snap.Nodes)
            if (inDegree[node.Id] == 0)
                ready.Enqueue(node.Id, node.Id);

        var ordered = new HashSet<string>(StringComparer.Ordinal);
        int maxDepth = 0;
        while (ready.TryDequeue(out var id, out _))
        {
            ordered.Add(id);
            if (!successors.TryGetValue(id, out var outs)) continue;

            foreach (var next in outs)
            {
                int candidate = depth[id] + 1;
                if (candidate > depth[next]) depth[next] = candidate;
                if (depth[next] > maxDepth) maxDepth = depth[next];
                if (--inDegree[next] == 0) ready.Enqueue(next, next);
            }
        }

        foreach (var node in snap.Nodes)
            if (!ordered.Contains(node.Id))
                depth[node.Id] = maxDepth + 1;

        var columns = new SortedDictionary<int, List<BpNode>>();
        foreach (var node in snap.Nodes)
        {
            if (!columns.TryGetValue(depth[node.Id], out var column)) columns[depth[node.Id]] = column = new List<BpNode>();
            column.Add(node);
        }

        var result = new Dictionary<string, BpPoint>(StringComparer.Ordinal);
        foreach (var (column, nodes) in columns)
        {
            nodes.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));
            double y = 0;
            foreach (var node in nodes)
            {
                result[node.Id] = Snap(new BpPoint(column * (NodeWidth + LayoutGapX), y));
                y += NodeRect(node).Height + LayoutGapY;
            }
        }

        return result;
    }

    // ---- 小地图 ----

    /// <summary>节点包围盒；空图返回零矩形。</summary>
    public static BpRect ContentBounds(BpSnapshot snap)
    {
        if (snap.Nodes.Count == 0) return default;

        var first = NodeRect(snap.Nodes[0]);
        double left = first.X, top = first.Y, right = first.Right, bottom = first.Bottom;
        foreach (var node in snap.Nodes)
        {
            var rect = NodeRect(node);
            left = Math.Min(left, rect.X);
            top = Math.Min(top, rect.Y);
            right = Math.Max(right, rect.Right);
            bottom = Math.Max(bottom, rect.Bottom);
        }

        return new(left, top, right - left, bottom - top);
    }

    public static BpRect Union(BpRect a, BpRect b)
    {
        if (a.Width <= 0 || a.Height <= 0) return b;
        if (b.Width <= 0 || b.Height <= 0) return a;

        double left = Math.Min(a.X, b.X), top = Math.Min(a.Y, b.Y);
        return new(left, top, Math.Max(a.Right, b.Right) - left, Math.Max(a.Bottom, b.Bottom) - top);
    }

    /// <summary>画布点 → 小地图矩形内的点（等比、居中）。</summary>
    public static BpPoint ToMinimap(BpPoint canvas, in BpRect content, in BpRect target)
    {
        if (content.Width <= 0 || content.Height <= 0) return new(target.X, target.Y);

        double scale = Math.Min(target.Width / content.Width, target.Height / content.Height);
        double offsetX = target.X + (target.Width - content.Width * scale) / 2;
        double offsetY = target.Y + (target.Height - content.Height * scale) / 2;
        return new(offsetX + (canvas.X - content.X) * scale, offsetY + (canvas.Y - content.Y) * scale);
    }

    private static double DistanceToSegment(BpPoint p, BpPoint a, BpPoint b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= 0) return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));

        double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared, 0, 1);
        double cx = a.X + t * dx, cy = a.Y + t * dy;
        return Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
    }
}
