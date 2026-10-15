using JizuraLive.App.PvStage.Blueprint;
using JizuraLive.Pv;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 蓝图节点画布的纯逻辑：网格吸附、坐标↔画布变换、引脚命中、连线合法性预测、旧工程拓扑自动布局。
/// 只测 <see cref="BlueprintMath"/>——控件本体（WPF）不在这里测。
/// </summary>
public class BlueprintTests
{
    private static BpNode Node(string id, SignalNodeKind kind, double x = 0, double y = 0) => new(id, kind, x, y);

    private static BpSnapshot Graph(params BpNode[] nodes) => new() { Nodes = nodes };

    // ---- 网格吸附 ----

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 0)]
    [InlineData(5, 8)]
    [InlineData(8, 8)]
    [InlineData(12, 16)]
    [InlineData(-5, -8)]
    [InlineData(-3, 0)]
    public void SnapRoundsToGrid(double input, double expected) =>
        Assert.Equal(expected, BlueprintMath.Snap(input));

    [Fact]
    public void SnapKeepsGridAlignedPoints()
    {
        var p = new BpPoint(16, -24);
        Assert.Equal(p, BlueprintMath.Snap(p));
    }

    // ---- 坐标 ↔ 画布 ----

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.25)]
    [InlineData(2.0)]
    public void TransformRoundTrips(double zoom)
    {
        var pan = new BpPoint(37, -14);
        var canvas = new BpPoint(123.5, -48.25);

        var back = BlueprintMath.ToCanvas(BlueprintMath.ToScreen(canvas, zoom, pan), zoom, pan);

        Assert.Equal(canvas.X, back.X, 6);
        Assert.Equal(canvas.Y, back.Y, 6);
    }

    [Fact]
    public void ZoomKeepsAnchorUnderPointer()
    {
        var pan = new BpPoint(10, 20);
        var anchor = new BpPoint(400, 300);
        double zoom = BlueprintMath.ClampZoom(2.0);

        var next = BlueprintMath.ZoomPan(pan, 1.0, zoom, anchor);
        var screen = BlueprintMath.ToScreen(BlueprintMath.ToCanvas(anchor, 1.0, pan), zoom, next);

        Assert.Equal(anchor.X, screen.X, 6);
        Assert.Equal(anchor.Y, screen.Y, 6);
    }

    [Theory]
    [InlineData(0.1, 0.25)]
    [InlineData(5.0, 2.0)]
    [InlineData(1.0, 1.0)]
    public void ZoomClampsToRange(double input, double expected) =>
        Assert.Equal(expected, BlueprintMath.ClampZoom(input));

    // ---- 命中 ----

    [Fact]
    public void HitPinFindsInputsAndOutput()
    {
        var snap = Graph(Node("n1", SignalNodeKind.Add, 100, 50));

        var a = BlueprintMath.HitPin(snap, new BpPoint(100, 50 + BlueprintMath.PinRowY(0)));
        Assert.True(a.HasValue);
        Assert.Equal("n1", a.Value.NodeId);
        Assert.Equal("a", a.Value.Port);
        Assert.True(a.Value.IsInput);

        var b = BlueprintMath.HitPin(snap, new BpPoint(100, 50 + BlueprintMath.PinRowY(1)));
        Assert.True(b.HasValue);
        Assert.Equal("b", b.Value.Port);

        var output = BlueprintMath.HitPin(snap, new BpPoint(100 + BlueprintMath.NodeWidth, 50 + BlueprintMath.PinRowY(2)));
        Assert.True(output.HasValue);
        Assert.Equal("out", output.Value.Port);
        Assert.False(output.Value.IsInput);
    }

    [Fact]
    public void HitPinMissesNodeInteriorAndUnknownKinds()
    {
        var snap = Graph(Node("n1", SignalNodeKind.Add, 100, 50));
        Assert.Null(BlueprintMath.HitPin(snap, new BpPoint(100 + BlueprintMath.NodeWidth / 2, 50 + BlueprintMath.PinRowY(0))));

        var unknown = Graph(new BpNode("u1", (SignalNodeKind)99, 0, 0));
        Assert.Null(BlueprintMath.HitPin(unknown, new BpPoint(0, 0)));
    }

    [Fact]
    public void HitNodeReturnsTopmostCard()
    {
        var snap = Graph(Node("n1", SignalNodeKind.Constant, 0, 0), Node("n2", SignalNodeKind.Add, 0, 0));

        Assert.Equal("n2", BlueprintMath.HitNode(snap, new BpPoint(20, 20)));
        Assert.Null(BlueprintMath.HitNode(snap, new BpPoint(-50, -50)));
    }

    [Fact]
    public void HitEdgeFollowsTheBezier()
    {
        var snap = new BpSnapshot
        {
            Nodes = [Node("n1", SignalNodeKind.Constant, 0, 0), Node("n2", SignalNodeKind.Clamp, 400, 200)],
            Edges = [new BpEdge("e1", "n1", "out", "n2", "in")],
        };

        Assert.True(BlueprintMath.TryEdgeCurve(snap, snap.Edges[0], out var p0, out var c1, out var c2, out var p1));
        var mid = BlueprintMath.Bezier(p0, c1, c2, p1, 0.5);

        Assert.Equal("e1", BlueprintMath.HitEdge(snap, mid));
        Assert.Null(BlueprintMath.HitEdge(snap, new BpPoint(mid.X, mid.Y - 200)));
    }

    // ---- 连线合法性预测（与 SignalGraph.TryAddEdge 的当场可判部分同源）----

    [Fact]
    public void ConnectRejectsPortDirectionAndUnknownPorts()
    {
        var snap = Graph(Node("n1", SignalNodeKind.Constant), Node("n2", SignalNodeKind.Clamp));

        Assert.Equal(BpConnectIssue.PortMismatch, BlueprintMath.CheckConnect(snap, "n1", "out", "n2", "out"));
        Assert.Equal(BpConnectIssue.PortMismatch, BlueprintMath.CheckConnect(snap, "n1", "in", "n2", "in"));
        Assert.Equal(BpConnectIssue.PortMismatch, BlueprintMath.CheckConnect(snap, "n1", "out", "n2", "nope"));
        Assert.Equal(BpConnectIssue.PortMismatch, BlueprintMath.CheckConnect(snap, "missing", "out", "n2", "in"));
    }

    [Fact]
    public void ConnectRejectsOccupiedInput()
    {
        var snap = new BpSnapshot
        {
            Nodes = [Node("n1", SignalNodeKind.Constant), Node("n2", SignalNodeKind.Constant), Node("n3", SignalNodeKind.Clamp)],
            Edges = [new BpEdge("e1", "n1", "out", "n3", "in")],
        };

        Assert.Equal(BpConnectIssue.InputOccupied, BlueprintMath.CheckConnect(snap, "n2", "out", "n3", "in"));
    }

    [Fact]
    public void ConnectRejectsCyclesAndSelfLoops()
    {
        var snap = new BpSnapshot
        {
            Nodes = [Node("n1", SignalNodeKind.Add), Node("n2", SignalNodeKind.Add), Node("n3", SignalNodeKind.Add)],
            Edges = [new BpEdge("e1", "n1", "out", "n2", "a"), new BpEdge("e2", "n2", "out", "n3", "a")],
        };

        Assert.Equal(BpConnectIssue.Cycle, BlueprintMath.CheckConnect(snap, "n3", "out", "n1", "a"));
        Assert.Equal(BpConnectIssue.Cycle, BlueprintMath.CheckConnect(snap, "n1", "out", "n1", "a"));
    }

    [Fact]
    public void ConnectAcceptsLegalEdgesIncludingDiamonds()
    {
        var snap = Graph(Node("n1", SignalNodeKind.Constant), Node("n2", SignalNodeKind.Clamp));
        Assert.Equal(BpConnectIssue.None, BlueprintMath.CheckConnect(snap, "n1", "out", "n2", "in"));

        var diamond = new BpSnapshot
        {
            Nodes = [Node("a1", SignalNodeKind.Constant), Node("a2", SignalNodeKind.Add), Node("a3", SignalNodeKind.Add)],
            Edges = [new BpEdge("e1", "a1", "out", "a2", "a"), new BpEdge("e2", "a1", "out", "a3", "a")],
        };
        Assert.Equal(BpConnectIssue.None, BlueprintMath.CheckConnect(diamond, "a2", "out", "a3", "b"));
    }

    // ---- 拓扑自动布局 ----

    [Fact]
    public void AutoLayoutColumnsByKahnDepth()
    {
        var snap = new BpSnapshot
        {
            Nodes = [Node("n1", SignalNodeKind.Constant), Node("n2", SignalNodeKind.Add), Node("n3", SignalNodeKind.ParameterOutput)],
            Edges = [new BpEdge("e1", "n1", "out", "n2", "a"), new BpEdge("e2", "n2", "out", "n3", "in")],
        };

        var layout = BlueprintMath.AutoLayout(snap);

        Assert.True(layout["n1"].X < layout["n2"].X, "输入应在左");
        Assert.True(layout["n2"].X < layout["n3"].X, "输出应在右");
        Assert.Equal(layout["n1"].X, BlueprintMath.Snap(layout["n1"].X));
        Assert.Equal(layout["n1"].Y, BlueprintMath.Snap(layout["n1"].Y));
    }

    [Fact]
    public void AutoLayoutStacksSourcesInOneColumnAndParksCyclesLast()
    {
        var snap = new BpSnapshot
        {
            Nodes =
            [
                Node("n1", SignalNodeKind.Add), Node("n2", SignalNodeKind.Add),
                Node("n3", SignalNodeKind.Constant), Node("n4", SignalNodeKind.Constant),
            ],
            Edges = [new BpEdge("e1", "n1", "out", "n2", "a"), new BpEdge("e2", "n2", "out", "n1", "a")],
        };

        var layout = BlueprintMath.AutoLayout(snap);

        Assert.Equal(layout["n3"].X, layout["n4"].X);
        Assert.NotEqual(layout["n3"].Y, layout["n4"].Y);
        Assert.True(layout["n1"].X > layout["n3"].X, "环上的节点排不出序，放最后一列而不是丢掉");
        Assert.Equal(layout["n1"].X, layout["n2"].X);
        Assert.NotEqual(layout["n1"].Y, layout["n2"].Y);
    }

    // ---- 小地图 ----

    [Fact]
    public void ContentBoundsCoversEveryCard()
    {
        var snap = Graph(Node("n1", SignalNodeKind.Constant, 0, 0), Node("n2", SignalNodeKind.Constant, 400, 200));
        var content = BlueprintMath.ContentBounds(snap);

        Assert.Equal(0, content.X);
        Assert.Equal(0, content.Y);
        Assert.Equal(400 + BlueprintMath.NodeWidth, content.Width);
        Assert.True(content.Height > 200);
    }

    [Fact]
    public void MinimapMapsContentIntoTheBox()
    {
        var snap = Graph(Node("n1", SignalNodeKind.Constant, 0, 0), Node("n2", SignalNodeKind.Constant, 400, 200));
        var content = BlueprintMath.ContentBounds(snap);
        var box = new BpRect(0, 0, 120, 80);

        var topLeft = BlueprintMath.ToMinimap(new BpPoint(content.X, content.Y), content, box);
        var bottomRight = BlueprintMath.ToMinimap(new BpPoint(content.Right, content.Bottom), content, box);

        Assert.True(topLeft.X >= 0 && topLeft.Y >= 0);
        Assert.True(bottomRight.X <= 120.001 && bottomRight.Y <= 80.001);
        Assert.True(bottomRight.X > topLeft.X);
    }

    // ---- 节点体内行布局 ----

    [Fact]
    public void ParameterOutputHasOneBindingRowAndNoValueRow()
    {
        var def = SignalNodeCatalog.Get(SignalNodeKind.ParameterOutput)!;

        Assert.Equal(1, BlueprintMath.ParamRowCount(def));
        Assert.Equal(BlueprintMath.TargetRow, BlueprintMath.ParamNameAt(def, 0));
        Assert.False(BlueprintMath.HasOutput(def));
    }

    [Fact]
    public void NodeHeightGrowsWithSchemaRows()
    {
        var add = SignalNodeCatalog.Get(SignalNodeKind.Add)!;
        var remap = SignalNodeCatalog.Get(SignalNodeKind.Remap)!;

        Assert.True(BlueprintMath.NodeHeight(remap) > BlueprintMath.NodeHeight(add));
        Assert.Equal(3, add.Ports.Length);
        Assert.Equal("inMin", BlueprintMath.ParamNameAt(remap, 0));
    }
}
