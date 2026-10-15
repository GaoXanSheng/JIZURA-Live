using System.Windows;
using JizuraLive.App.PvStage.Blueprint;
using JizuraLive.Pv;
using JizuraLive.Render.Effects;

namespace JizuraLive.App.PvStage;

/// <summary>
/// 蓝图工作区接线：文档 ⇄ 画布投影、一次手势一个撤销事务、诊断快照。
/// 画布只认 <see cref="BpSnapshot"/> / <see cref="BpEdit"/>，文档、发布与撤销全在本文件里。
/// </summary>
public partial class PvStageWindow
{
    /// <summary>没有单位可标的节点（相加/相乘这类量纲由输入推导）：给空表，画布只显示端口名。</summary>
    private static readonly IReadOnlyDictionary<string, ParamUnit> NoPortUnits = new Dictionary<string, ParamUnit>();

    // ---------------- 工作区切换 ----------------

    private void BtnViewStage_Click(object sender, RoutedEventArgs e) => ShowWorkspace(blueprint: false);

    private void BtnViewBlueprint_Click(object sender, RoutedEventArgs e) => ShowWorkspace(blueprint: true);

    /// <summary>
    /// 舞台 / 蓝图切换。两块都留在树上，只切可见性——切回来时预览的交换链与画布的缩放、选中都还在。
    /// 诊断轮询只在蓝图可见时挂着：它是 250ms 一次的求值，藏在舞台后面跑没有意义。
    /// </summary>
    private void ShowWorkspace(bool blueprint)
    {
        BlueprintCanvas.DiagnosticsProvider = blueprint ? BlueprintDiagnostics : null;
        StageWorkspace.Visibility = blueprint ? Visibility.Collapsed : Visibility.Visible;
        BlueprintCanvas.Visibility = blueprint ? Visibility.Visible : Visibility.Collapsed;
        BtnViewStage.Tag = blueprint ? null : "on";
        BtnViewBlueprint.Tag = blueprint ? "on" : null;
        if (blueprint) RefreshBlueprint();
        else SaveBlueprintLayout(); // 切离蓝图时把视图态交给工程文档，随下一次存盘落盘
    }

    // ---------------- 编辑器布局（画布视图态） ----------------

    /// <summary>
    /// 画布视图态 → 工程文档。布局不是内容：不触发编译、不入撤销栈，只在切离蓝图与工程保存时各写一次。
    /// </summary>
    private void SaveBlueprintLayout()
    {
        var layout = EditorLayoutOf();
        layout.CanvasZoom = BlueprintCanvas.Zoom;
        layout.CanvasPanX = BlueprintCanvas.PanX;
        layout.CanvasPanY = BlueprintCanvas.PanY;
        layout.SelectedNodeIds = new List<string>(BlueprintCanvas.SelectedNodeIds);
    }

    /// <summary>工程文档 → 画布视图态（缩放由画布自己钳制到 0.25–2）。新建/打开工程时调一次。</summary>
    private void ApplyBlueprintLayout()
    {
        var layout = EditorLayoutOf();
        BlueprintCanvas.Zoom = layout.CanvasZoom;
        BlueprintCanvas.PanX = layout.CanvasPanX;
        BlueprintCanvas.PanY = layout.CanvasPanY;
        BlueprintCanvas.SetSelection(layout.SelectedNodeIds ?? []);
    }

    /// <summary>撤销快照走的是裸反序列化（不过 ProjectStore 的治理），布局字段可能是 null。</summary>
    private EditorLayout EditorLayoutOf() => _project.EditorLayout ??= new EditorLayout();

    // ---------------- 文档 → 画布 ----------------

    /// <summary>
    /// 整份重投影（节点、边、绑定、坐标与地址候选都在这一份里）。
    /// 只在文档变了或切回蓝图时调：<c>SetSnapshot</c> 会收起参数编辑浮层并取消进行中的拖动。
    /// </summary>
    private void RefreshBlueprint()
    {
        var graph = _project.SignalGraph;
        var edges = new List<BpEdge>(graph.Edges.Count);
        foreach (var edge in graph.Edges)
            if (edge is not null)
                edges.Add(new BpEdge(edge.Id, edge.FromNode, edge.FromPort, edge.ToNode, edge.ToPort));

        var nodes = new List<BpNode>(graph.Nodes.Count);
        foreach (var node in graph.Nodes)
            if (node is not null)
                nodes.Add(Project(node));

        // 旧工程没有坐标：按拓扑分层自动布局，并当场写回文档（下一次存盘随之落盘，重开不再重排）
        if (graph.Nodes.Any(n => n is not null && !n.HasPosition))
        {
            var layout = BlueprintMath.AutoLayout(new BpSnapshot { Nodes = nodes, Edges = edges });
            nodes.Clear();
            foreach (var node in graph.Nodes)
            {
                if (node is null) continue;
                if (!node.HasPosition && layout.TryGetValue(node.Id, out var at))
                {
                    node.X = at.X;
                    node.Y = at.Y;
                }

                nodes.Add(Project(node));
            }
        }

        var bindings = new List<BpBinding>(graph.Bindings.Count);
        foreach (var binding in graph.Bindings)
            if (binding is not null)
                bindings.Add(new BpBinding(binding.Id, binding.NodeId, binding.Address));

        BlueprintCanvas.SetSnapshot(new BpSnapshot
        {
            Nodes = nodes,
            Edges = edges,
            Bindings = bindings,
            AddressOptions = SignalAddressOptions(),
        });
    }

    /// <summary>参数/文本表按引用交给画布：它是只读投影，没有写文档的接口。</summary>
    private BpNode Project(SignalNode node) => new(node.Id, node.Kind, node.X ?? 0, node.Y ?? 0)
    {
        Enabled = node.Enabled,
        Params = node.Params,
        Texts = node.Texts,
        PortUnits = PortUnitsOf(node, SignalNodeCatalog.Get(node.Kind)),
    };

    /// <summary>
    /// 引脚单位：只在 schema 直接声明得到时才标（常量/Remap 的单位参数、时间/频带/VTS 的固定量纲、
    /// 基础值取目标注册单位）。相加/相乘/夹取/振荡/输出的单位由输入推导——那条规则只住在 SignalCompiler，
    /// 画布不复制：标错一个单位比不标更坏。
    /// </summary>
    private IReadOnlyDictionary<string, ParamUnit> PortUnitsOf(SignalNode node, SignalNodeDef? def)
    {
        if (def is null) return NoPortUnits;

        ParamUnit? unit = node.Kind switch
        {
            SignalNodeKind.Constant => UnitOf(SignalNodeCatalog.ParamValue(node, def, "unit")),
            SignalNodeKind.Remap => UnitOf(SignalNodeCatalog.ParamValue(node, def, "outUnit")),
            SignalNodeKind.Time => ParamUnit.Seconds,
            SignalNodeKind.AudioBands => ParamUnit.Ratio,
            SignalNodeKind.VtsPose => SignalNodeCatalog.VtsChannelUnit(
                (VtsChannel)(int)SignalNodeCatalog.ParamValue(node, def, "channel")),
            SignalNodeKind.TimelineBaseValue => BaseValueUnit(node, def),
            _ => null,
        };
        if (unit is not { } resolved) return NoPortUnits;

        var map = new Dictionary<string, ParamUnit>();
        foreach (var port in def.Ports)
            if (!port.IsInput)
                map[port.Name] = resolved;
        return map;
    }

    /// <summary>单位参数存的是 ParamUnit 序号；未定义 / Unspecified 不是量纲，不标（显示「?」比不显示更坏）。</summary>
    private static ParamUnit? UnitOf(double value)
    {
        var unit = (ParamUnit)(int)value;
        return Enum.IsDefined(unit) && unit != ParamUnit.Unspecified ? unit : null;
    }

    private ParamUnit? BaseValueUnit(SignalNode node, SignalNodeDef def)
    {
        var text = SignalNodeCatalog.TextValue(node, def, "address");
        return ParamAddress.TryParse(text, out var addr) &&
               ParamRegistry.TryResolve(addr, _effects, _scene, out var spec, out _)
            ? spec.Unit
            : null;
    }

    // ---------------- 诊断快照 ----------------

    /// <summary>
    /// 画布每 250ms 拉一次：每个节点的输出值与有效位。
    ///
    /// 与 <see cref="RefreshParamRows"/> / <c>PvPipeline.ApplySignals</c> 同一份输入（同 tick 同值），
    /// 计划也共用检查器那份按引用缓存的编译结果。
    /// </summary>
    private IReadOnlyDictionary<string, BpValue> BlueprintDiagnostics()
    {
        var values = new Dictionary<string, BpValue>(StringComparer.Ordinal);
        var plan = BadgePlan();
        var graph = _project.SignalGraph;
        if (plan is null || graph is null) return values;

        var t = _audio.Time;
        // 同 PvPipeline.ApplySignals：VTS 姿态取同一个探针、同一个 250ms 新鲜度，
        // 否则画布上的 VtsPose 节点会显示"实机恒失效、预览有值"的假有效位。
        var live = Monotonic.Elapsed.TotalSeconds;
        plan.Evaluate(new SignalFrameInputs(t, live,
            PvPipeline.SampleAudioBands(_audioBands, t), _evaluator, VtsPoseSnapshot.Read(_vts?.Tracking, live)));

        var invalid = InvalidNodes(graph, plan);
        for (var i = 0; i < plan.Nodes.Count; i++)
        {
            var id = plan.Nodes[i].Id;
            values[id] = new BpValue(plan.Values[i], !invalid.Contains(id));
        }

        return values;
    }

    /// <summary>
    /// 本帧失效的节点。计划只公开值与 issue，不公开有效位，所以：被禁用与有 issue 的节点是已知失效点，
    /// 再沿边向下游传播——求值器里"已连输入失效 ⇒ 本节点失效"对每一种带输入的节点都成立，
    /// 而失效的上游自己一定记了 issue（禁用除外，已在上面收进来），所以不会漏。
    /// </summary>
    private static HashSet<string> InvalidNodes(SignalGraph graph, CompiledSignalGraph plan)
    {
        var invalid = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in graph.Nodes)
            if (node is not null && !node.Enabled)
                invalid.Add(node.Id);

        foreach (var issue in plan.Issues)
            invalid.Add(issue.NodeId);

        var pending = new Queue<string>(invalid);
        while (pending.Count > 0)
        {
            var from = pending.Dequeue();
            foreach (var edge in graph.Edges)
            {
                if (edge is null || !string.Equals(edge.FromNode, from, StringComparison.Ordinal)) continue;
                if (invalid.Add(edge.ToNode)) pending.Enqueue(edge.ToNode);
            }
        }

        return invalid;
    }

    // ---------------- 画布 → 文档 ----------------

    /// <summary>画布的一次手势 = 一次可撤销事务；被拒绝时图一字不改（也不入撤销栈），原因写状态条。</summary>
    private void Blueprint_EditCommitted(BpEdit edit)
    {
        if (edit.Kind == BpEditKind.MoveNodes)
        {
            // 坐标是布局不是内容：就地写，不换引用——渲染线程不读 X/Y，已编译的拓扑继续复用
            foreach (var move in edit.Moves)
                if (FindSignalNode(move.NodeId) is { } moved)
                {
                    moved.X = move.X;
                    moved.Y = move.Y;
                }

            PushUndo();
            _saveError = SaveProjectNow();
            return;
        }

        // 克隆 → 改 → 换引用：发布出去的那份此后谁也不许再改（渲染线程每帧都在读它）
        var graph = CloneSignalGraph(_project.SignalGraph);
        if (ApplyBlueprintEdit(graph, edit) is { } fault)
        {
            TblStatus.Text = "蓝图编辑未生效：" + fault;
            return;
        }

        _project.SignalGraph = graph;
        PublishSignalGraph();
        PushUndo();
        _saveError = SaveProjectNow();
        RefreshBlueprint();
        RefreshParamRows(); // 采用后每行的值来源徽标当场跟着变，不等下一次 500ms 刷新
        TblStatus.Text = $"信号图已更新：{graph.Nodes.Count} 节点 · {graph.Edges.Count} 边 · {graph.Bindings.Count} 绑定";
    }

    /// <summary>把一次画布手势落到图上。返回非空 = 拒绝原因（此时图必须一字不改）。</summary>
    private string? ApplyBlueprintEdit(SignalGraph graph, BpEdit edit)
    {
        switch (edit.Kind)
        {
            case BpEditKind.Connect:
            {
                var edge = new SignalEdge
                {
                    Id = NewSignalId("e"),
                    FromNode = edit.FromNode,
                    FromPort = edit.FromPort,
                    ToNode = edit.ToNode,
                    ToPort = edit.ToPort,
                };
                return graph.TryAddEdge(edge, out var error) ? null : error;
            }

            case BpEditKind.Disconnect:
                return graph.RemoveEdge(edit.EdgeId) ? null : "这条连线已经不在图里了";

            case BpEditKind.RemoveNodes:
                foreach (var id in edit.NodeIds)
                    graph.RemoveNode(id);
                return null;

            case BpEditKind.SetEnabled:
                foreach (var id in edit.NodeIds)
                    if (FindSignalNode(graph, id) is { } toggled)
                        toggled.Enabled = edit.Enabled;
                return null;

            case BpEditKind.SetParam:
                if (FindSignalNode(graph, FirstNodeId(edit)) is not { } paramNode) return "节点已不在图里";
                return graph.SetParam(paramNode, edit.ParamName, edit.Value)
                    ? null
                    : $"节点 {paramNode.Id} 的 {edit.ParamName} = {edit.Value} 不合法";

            case BpEditKind.SetText:
                if (FindSignalNode(graph, FirstNodeId(edit)) is not { } textNode) return "节点已不在图里";
                return graph.SetText(textNode, edit.ParamName, edit.Text)
                    ? null
                    : $"节点 {textNode.Id} 没有文本参数 {edit.ParamName}";

            case BpEditKind.SetBinding:
                return SetBlueprintBinding(graph, FirstNodeId(edit), edit.Text);

            case BpEditKind.AddNode:
            {
                var node = graph.CreateNode(edit.AddKind);
                node.X = edit.X;
                node.Y = edit.Y;
                return graph.TryAddNode(node, out var error) ? null : error;
            }

            default:
                return "未知的画布手势";
        }
    }

    /// <summary>改参数/改文本/改绑定的手势都只带一个节点（画布那侧就是这么发的）。</summary>
    private static string FirstNodeId(BpEdit edit) => edit.NodeIds.Count > 0 ? edit.NodeIds[0] : "";

    /// <summary>绑定目标：空地址 = 解绑；一个输出节点至多一条绑定（先摘旧的再按新地址加一条）。</summary>
    private static string? SetBlueprintBinding(SignalGraph graph, string nodeId, string address)
    {
        if (FindSignalNode(graph, nodeId) is null) return "节点已不在图里";

        foreach (var old in graph.Bindings
                     .Where(b => b is not null && string.Equals(b.NodeId, nodeId, StringComparison.Ordinal))
                     .ToList())
            graph.RemoveBinding(old.Id);

        if (string.IsNullOrEmpty(address)) return null;

        var binding = new SignalBinding { Id = NewSignalId("b"), NodeId = nodeId, Address = address };
        return graph.TryAddBinding(binding, out var error) ? null : error;
    }

    /// <summary><see cref="SignalGraph"/> 的节点查找是私有的，宿主侧的编辑需要一份同口径的查找。</summary>
    private SignalNode? FindSignalNode(string id) => FindSignalNode(_project.SignalGraph, id);

    private static SignalNode? FindSignalNode(SignalGraph graph, string id)
    {
        foreach (var node in graph.Nodes)
            if (node is not null && string.Equals(node.Id, id, StringComparison.Ordinal))
                return node;
        return null;
    }
}
