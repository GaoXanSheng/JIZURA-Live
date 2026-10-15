using JizuraLive.Render.Effects;

namespace JizuraLive.Pv;

/// <summary>编译/装载问题的严重度。</summary>
public enum SignalIssueSeverity
{
    /// <summary>不阻断编译（Plan 照常产出），但必须显示：合法文档里的"输出无效"配置。</summary>
    Warning,

    /// <summary>静态配置错误：整张图不被采用（Plan = null）。不允许"只采用其中一半"（契约 §13.6）。</summary>
    Error,
}

/// <summary>一条编译问题：稳定码 + 严重度 + 精确定位（节点/边/绑定 Id，UI 靠它跳到出错的那一格）。</summary>
public sealed record SignalCompileIssue(
    string Code,
    SignalIssueSeverity Severity,
    string? NodeId,
    string? EdgeId,
    string? BindingId,
    string Message);

/// <summary>编译结果：<see cref="Plan"/> 非空即编译成功；有 Error 时 Plan 恒为 null。</summary>
public sealed record SignalCompileResult(CompiledSignalGraph? Plan, IReadOnlyList<SignalCompileIssue> Issues);

/// <summary>编译/复用期用到的稳定错误码。码被面板与测试断言，改码等于改契约（文案可以改，码不行）。</summary>
internal static class SignalIssueCodes
{
    internal const string TooManyNodes = "too-many-nodes";
    internal const string TooManyEdges = "too-many-edges";
    internal const string EmptyId = "empty-id";
    internal const string DuplicateNodeId = "duplicate-node-id";
    internal const string DuplicateEdgeId = "duplicate-edge-id";
    internal const string DuplicateBindingId = "duplicate-binding-id";
    internal const string UnknownNodeKind = "unknown-node-kind";

    internal const string ParamNotFinite = "param-not-finite";
    internal const string ParamOutOfRange = "param-out-of-range";
    internal const string ParamInvalidEnum = "param-invalid-enum";
    internal const string RemapZeroSpan = "remap-zero-span";
    internal const string ClampInverted = "clamp-inverted";

    internal const string EdgeMissingNode = "edge-missing-node";
    internal const string EdgeMissingPort = "edge-missing-port";
    internal const string EdgeDirection = "edge-direction";
    internal const string EdgeSelfLoop = "edge-self-loop";
    internal const string EdgeMultipleIntoInput = "edge-multiple-into-input";

    /// <summary>残余节点既可能在环上，也可能被环阻塞——消息必须用「环或受环阻塞」，不得声称残余全在环内（§8.2.2）。</summary>
    internal const string CycleOrBlocked = "cycle-or-blocked";

    internal const string AddressInvalid = "address-invalid";
    internal const string UnitUnspecified = "unit-unspecified";
    internal const string UnitMismatch = "unit-mismatch";

    internal const string RequiredInputUnconnected = "required-input-unconnected";
    internal const string TargetDuplicateWriter = "target-duplicate-writer";
    internal const string BindingMissingNode = "binding-missing-node";
    internal const string BindingNodeNotOutput = "binding-node-not-output";
    internal const string BindingBadAddress = "binding-bad-address";

    /// <summary>结构版本变了 / 编译身份对不上：必须重编，不能拿旧计划继续跑（§8.2.3）。</summary>
    internal const string StructureChanged = "structure-changed";
}

/// <summary>
/// 编译期规则（校验 + 端口槽位映射）。集中放一处：编辑期与编译期必须用同一套判定，
/// 否则"能存进去的图"和"能编译的图"会分叉成两个真相。
/// </summary>
internal static class SignalRules
{
    /// <summary>
    /// 单个节点的参数语义：有限、在域内（离散参数报 <c>param-invalid-enum</c>）、
    /// 单位参数 ≠ Unspecified；再做两条跨参数检查（Remap 零区间、Clamp 上下界反了）。
    /// </summary>
    internal static bool ValidateNodeParams(SignalNode node, SignalNodeDef def, out string code, out string message)
    {
        foreach (var p in def.Params)
        {
            if (p.Kind != SignalParamKind.Number) continue;

            var v = SignalNodeCatalog.ParamValue(node, def, p.Name);
            if (!double.IsFinite(v))
            {
                code = SignalIssueCodes.ParamNotFinite;
                message = $"节点 {node.Id}（{node.Kind}）的参数 {p.Name} 不是有限值：{v}";
                return false;
            }

            if (SignalNodeCatalog.IsDiscreteParam(node.Kind, p.Name))
            {
                if (v != Math.Floor(v) || v < p.Min || v > p.Max)
                {
                    code = SignalIssueCodes.ParamInvalidEnum;
                    message = $"节点 {node.Id}（{node.Kind}）的参数 {p.Name} = {v} 不是有定义的枚举成员（合法取值 {p.Min}..{p.Max}）";
                    return false;
                }

                if (SignalNodeCatalog.IsUnitParam(node.Kind, p.Name) && (ParamUnit)v == ParamUnit.Unspecified)
                {
                    code = SignalIssueCodes.ParamInvalidEnum;
                    message = $"节点 {node.Id}（{node.Kind}）的单位参数 {p.Name} 是 Unspecified：未注册/未验证不是一种量纲，只能作为 Remap 的输入";
                    return false;
                }

                continue;
            }

            if (v < p.Min || v > p.Max)
            {
                code = SignalIssueCodes.ParamOutOfRange;
                message = $"节点 {node.Id}（{node.Kind}）的参数 {p.Name} = {v} 越界（允许 {p.Min}..{p.Max}）";
                return false;
            }
        }

        if (node.Kind == SignalNodeKind.Remap)
        {
            var inMin = SignalNodeCatalog.ParamValue(node, def, "inMin");
            var inMax = SignalNodeCatalog.ParamValue(node, def, "inMax");
            if (inMin == inMax)
            {
                code = SignalIssueCodes.RemapZeroSpan;
                message = $"节点 {node.Id} 的 Remap 输入区间为零长度（inMin = inMax = {inMin}）：比例无从求得，给一个非零区间";
                return false;
            }
        }

        if (node.Kind == SignalNodeKind.Clamp)
        {
            var min = SignalNodeCatalog.ParamValue(node, def, "min");
            var max = SignalNodeCatalog.ParamValue(node, def, "max");
            if (min > max)
            {
                code = SignalIssueCodes.ClampInverted;
                message = $"节点 {node.Id} 的 Clamp 下界大于上界（min = {min} > max = {max}）";
                return false;
            }
        }

        code = "";
        message = "";
        return true;
    }

    /// <summary>
    /// schema 端口名 → 求值器端口槽（-1 = 求值器不消费这个端口）。
    /// 求值器只认 0/1/2 三个输入槽：in/a/time、b/offset、amplitude；编译期按名字绑好，帧内只做数组索引。
    /// </summary>
    internal static int PortSlotOf(SignalNodeKind kind, string? name) => kind switch
    {
        SignalNodeKind.Add or SignalNodeKind.Multiply =>
            name == "a" ? CompiledSignalGraph.PortPrimary : name == "b" ? CompiledSignalGraph.PortSecondary : -1,
        SignalNodeKind.Clamp or SignalNodeKind.Remap or SignalNodeKind.ParameterOutput =>
            name == "in" ? CompiledSignalGraph.PortPrimary : -1,
        SignalNodeKind.Lfo =>
            name == "time" ? CompiledSignalGraph.PortPrimary
            : name == "offset" ? CompiledSignalGraph.PortSecondary
            : name == "amplitude" ? CompiledSignalGraph.PortAmplitude
            : -1,
        _ => -1,
    };
}

/// <summary>
/// 信号图编译器：把**文档形态**的图编译成帧内可走的定长槽位计划。
///
/// 契约要点：
/// <list type="number">
/// <item>任一 Error ⇒ Plan = null，绝不"采用一半"（§13.6）。</item>
/// <item>拓扑序用 Kahn + 按 Id 的 <see cref="PriorityQueue{TElement,TPriority}"/>（Ordinal），
/// 代价 O(E + V log V)；顺序稳定⇒同一份图两次编译逐项相同（§8.2.2、§13.7 验收）。</item>
/// <item>只在结构变化时重编；参数修改走 <see cref="CompiledSignalGraph.TryApplyParams"/>（§8.2.3）。</item>
/// </list>
/// </summary>
public static class SignalCompiler
{
    /// <summary>
    /// 编译一张信号图。<paramref name="scene"/> 只用于解析相机位移的注册范围（±1 个画布），
    /// 传 null 按默认画布解析；与信号图无关的调用方（持久化往返、纯图用例）不必给。
    /// </summary>
    public static SignalCompileResult Compile(SignalGraph graph, EffectGraph effects, PvScene? scene = null)
    {
        var issues = new List<SignalCompileIssue>();
        var plan = Build(graph, effects, scene, issues);
        return new SignalCompileResult(plan, issues);
    }

    private static CompiledSignalGraph? Build(SignalGraph graph, EffectGraph effects, PvScene? scene, List<SignalCompileIssue> issues)
    {
        var failed = false;

        void Report(string code, SignalIssueSeverity severity, string? nodeId, string? edgeId, string? bindingId, string message)
        {
            issues.Add(new SignalCompileIssue(code, severity, nodeId, edgeId, bindingId, message));
            if (severity == SignalIssueSeverity.Error) failed = true;
        }

        void Error(string code, string? nodeId, string? edgeId, string? bindingId, string message) =>
            Report(code, SignalIssueSeverity.Error, nodeId, edgeId, bindingId, message);

        void Warn(string code, string? nodeId, string? edgeId, string? bindingId, string message) =>
            Report(code, SignalIssueSeverity.Warning, nodeId, edgeId, bindingId, message);

        // ---- 1. 数量上限（§15.3：超限在采用之前拒绝并说明数量，不截断保存）----
        if (graph.Nodes.Count > SignalGraph.MaxNodes)
            Error(SignalIssueCodes.TooManyNodes, null, null, null,
                $"信号图节点数 {graph.Nodes.Count} 超过上限 {SignalGraph.MaxNodes}");
        if (graph.Edges.Count > SignalGraph.MaxEdges)
            Error(SignalIssueCodes.TooManyEdges, null, null, null,
                $"信号图边数 {graph.Edges.Count} 超过上限 {SignalGraph.MaxEdges}");

        // ---- 2. 节点 Id / 种类 / 参数 ----
        var nodeById = new Dictionary<string, SignalNode>(StringComparer.Ordinal);
        foreach (var node in graph.Nodes)
        {
            if (node is null)
            {
                Error(SignalIssueCodes.EmptyId, null, null, null, "信号图里有一个空节点（Nodes 含 null）");
                continue;
            }

            if (string.IsNullOrEmpty(node.Id))
            {
                Error(SignalIssueCodes.EmptyId, null, null, null, "节点 Id 为空");
                continue;
            }

            if (!nodeById.TryAdd(node.Id, node))
            {
                Error(SignalIssueCodes.DuplicateNodeId, node.Id, null, null, $"节点 Id 重复：{node.Id}");
                continue;
            }

            var def = SignalNodeCatalog.Get(node.Kind);
            if (def is null)
            {
                Error(SignalIssueCodes.UnknownNodeKind, node.Id, null, null,
                    $"未知节点种类：{node.Id} 的 Kind = {(int)node.Kind}");
                continue;
            }

            if (!SignalRules.ValidateNodeParams(node, def, out var code, out var message))
                Error(code, node.Id, null, null, message);
        }

        // ---- 3. 边：端点/端口名/方向/自环/同一输入至多一条 ----
        var edgeIds = new HashSet<string>(StringComparer.Ordinal);
        var inputOwner = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var edge in graph.Edges)
        {
            if (edge is null)
            {
                Error(SignalIssueCodes.EmptyId, null, null, null, "信号图里有一条空边（Edges 含 null）");
                continue;
            }

            if (string.IsNullOrEmpty(edge.Id))
            {
                Error(SignalIssueCodes.EmptyId, null, null, null, "边 Id 为空");
                continue;
            }

            if (!edgeIds.Add(edge.Id))
            {
                Error(SignalIssueCodes.DuplicateEdgeId, null, edge.Id, null, $"边 Id 重复：{edge.Id}");
                continue;
            }

            if (string.IsNullOrEmpty(edge.FromNode) || !nodeById.TryGetValue(edge.FromNode, out var from))
            {
                Error(SignalIssueCodes.EdgeMissingNode, null, edge.Id, null, $"边的起点节点不存在：{edge.FromNode}");
                continue;
            }

            if (string.IsNullOrEmpty(edge.ToNode) || !nodeById.TryGetValue(edge.ToNode, out var to))
            {
                Error(SignalIssueCodes.EdgeMissingNode, from.Id, edge.Id, null, $"边的终点节点不存在：{edge.ToNode}");
                continue;
            }

            var fromPort = SignalNodeCatalog.FindPort(SignalNodeCatalog.Get(from.Kind)!, edge.FromPort);
            if (fromPort is null)
            {
                Error(SignalIssueCodes.EdgeMissingPort, from.Id, edge.Id, null,
                    $"节点 {from.Id}（{from.Kind}）未声明端口「{edge.FromPort}」");
                continue;
            }

            if (fromPort.IsInput)
            {
                Error(SignalIssueCodes.EdgeDirection, from.Id, edge.Id, null,
                    $"边的起点必须是输出端口，「{edge.FromPort}」是输入端");
                continue;
            }

            var toPort = SignalNodeCatalog.FindPort(SignalNodeCatalog.Get(to.Kind)!, edge.ToPort);
            if (toPort is null)
            {
                Error(SignalIssueCodes.EdgeMissingPort, to.Id, edge.Id, null,
                    $"节点 {to.Id}（{to.Kind}）未声明端口「{edge.ToPort}」");
                continue;
            }

            if (!toPort.IsInput)
            {
                Error(SignalIssueCodes.EdgeDirection, to.Id, edge.Id, null,
                    $"边的终点必须是输入端口，「{edge.ToPort}」是输出端");
                continue;
            }

            if (string.Equals(edge.FromNode, edge.ToNode, StringComparison.Ordinal))
            {
                Error(SignalIssueCodes.EdgeSelfLoop, to.Id, edge.Id, null, $"不允许自环：{edge.FromNode}");
                continue;
            }

            // 键用 "\0" 分隔，避免 (a, b.c) 与 (a.b, c) 这类拼接撞车
            if (!inputOwner.TryAdd(edge.ToNode + "\0" + edge.ToPort, edge.Id))
                Error(SignalIssueCodes.EdgeMultipleIntoInput, to.Id, edge.Id, null,
                    $"输入端口 {edge.ToNode}.{edge.ToPort} 已有一条连线（一个输入至多一条边）");
        }

        if (failed) return null;

        // ---- 4. 拓扑序：Kahn + 按 Id 的优先队列（Ordinal），残余 = 环或受环阻塞 ----
        var inDegree = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in nodeById.Values) inDegree[node.Id] = 0;

        var successors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var edge in graph.Edges)
        {
            inDegree[edge.ToNode] = inDegree[edge.ToNode] + 1;
            if (!successors.TryGetValue(edge.FromNode, out var list))
                successors[edge.FromNode] = list = new List<string>();
            list.Add(edge.ToNode);
        }

        var ready = new PriorityQueue<string, string>(StringComparer.Ordinal);
        foreach (var (id, degree) in inDegree)
            if (degree == 0) ready.Enqueue(id, id);

        var order = new List<string>(inDegree.Count);
        while (ready.TryDequeue(out var id, out _))
        {
            order.Add(id);
            if (!successors.TryGetValue(id, out var outs)) continue;

            foreach (var next in outs)
            {
                var degree = inDegree[next] - 1;
                inDegree[next] = degree;
                if (degree == 0) ready.Enqueue(next, next);
            }
        }

        if (order.Count < inDegree.Count)
        {
            var ordered = new HashSet<string>(order, StringComparer.Ordinal);
            var residual = new List<string>();
            foreach (var id in inDegree.Keys)
                if (!ordered.Contains(id)) residual.Add(id);
            residual.Sort(StringComparer.Ordinal);

            // 不得声称残余全在环内：环的下游节点同样排不出序，但它不在环上（§8.2.2）
            var listed = residual.Count <= 8
                ? string.Join("、", residual)
                : $"{residual.Count} 个（过多未逐一列出）";
            foreach (var id in residual)
                Error(SignalIssueCodes.CycleOrBlocked, id, null, null,
                    $"环或受环阻塞的残余节点：{id}（本节点可能不在环上，而是在环的下游）。残余共 {residual.Count} 个：{listed}");

            return null;
        }

        // ---- 编译产物：全部按槽位索引的定长数组（帧内只做数组行走 + 索引，不做字符串/枚举解析）----
        var count = order.Count;
        var nodes = new SignalNode[count];
        var kinds = new SignalNodeKind[count];
        var slotOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var node = nodeById[order[i]];
            nodes[i] = node;
            kinds[i] = node.Kind;
            slotOf[node.Id] = i;
        }

        var paramValues = new double[count * CompiledSignalGraph.NodeParamSlots];
        var inputSource = new int[count * CompiledSignalGraph.MaxInputs];
        var inputRequired = new bool[count * CompiledSignalGraph.MaxInputs];
        var inputDefault = new double[count * CompiledSignalGraph.MaxInputs];
        // 0 会被读成"来源是槽位 0"：未连必须是 -1
        Array.Fill(inputSource, -1);

        for (var i = 0; i < count; i++)
        {
            CompiledSignalGraph.FillParams(nodes[i], paramValues, i * CompiledSignalGraph.NodeParamSlots);

            var def = SignalNodeCatalog.Get(kinds[i])!;
            foreach (var p in def.Ports)
            {
                var port = SignalRules.PortSlotOf(kinds[i], p.Name);
                if (port < 0) continue;
                var at = i * CompiledSignalGraph.MaxInputs + port;
                inputRequired[at] = p.Required;
                inputDefault[at] = p.Default;
            }
        }

        foreach (var edge in graph.Edges)
        {
            var to = slotOf[edge.ToNode];
            var port = SignalRules.PortSlotOf(kinds[to], edge.ToPort);
            if (port < 0) continue;
            inputSource[to * CompiledSignalGraph.MaxInputs + port] = slotOf[edge.FromNode];
        }

        // ---- 5. 单位统一：按拓扑序一遍（Add 同单位、Multiply 至多一个量纲、Unspecified 只能进 Remap）----
        var units = new ParamUnit[count];
        var unitKnown = new bool[count];
        var targets = new List<SignalTarget>();
        var timelineTarget = new int[count];
        Array.Fill(timelineTarget, -1);

        ParamUnit InputUnit(int slot, int port, out bool known)
        {
            var src = inputSource[slot * CompiledSignalGraph.MaxInputs + port];
            if (src >= 0 && unitKnown[src])
            {
                known = true;
                return units[src];
            }

            known = false;
            return ParamUnit.Unspecified;
        }

        void RejectUnspecifiedInput(int slot, SignalNode node, string what)
        {
            for (var port = 0; port < CompiledSignalGraph.MaxInputs; port++)
            {
                var u = InputUnit(slot, port, out var known);
                if (!known || u != ParamUnit.Unspecified) continue;

                Error(SignalIssueCodes.UnitUnspecified, node.Id, null, null,
                    $"{what} 的输入单位未注册（Unspecified）：它不是一个量纲，只能作为 Remap 的输入做显式换算");
                return;
            }
        }

        foreach (var id in order)
        {
            var i = slotOf[id];
            var node = nodes[i];

            switch (kinds[i])
            {
                case SignalNodeKind.Constant:
                    units[i] = (ParamUnit)paramValues[i * CompiledSignalGraph.NodeParamSlots + CompiledSignalGraph.SlotConstantUnit];
                    unitKnown[i] = true;
                    break;

                case SignalNodeKind.TimelineBaseValue:
                {
                    var def = SignalNodeCatalog.Get(kinds[i])!;
                    var text = SignalNodeCatalog.TextValue(node, def, "address");
                    if (!ParamAddress.TryParse(text, out var addr))
                    {
                        Error(SignalIssueCodes.AddressInvalid, node.Id, null, null,
                            $"TimelineBaseValue 缺少可解析的地址文本：「{text}」");
                        break;
                    }

                    if (!SignalTargets.TryResolveBinding(addr, effects, scene, out var target, out var resolveError))
                    {
                        Error(ParamRegistry.CodeOf(resolveError), node.Id, null, null,
                            $"TimelineBaseValue 的地址 {addr} 解析失败：{ParamRegistry.MessageOf(resolveError)}");
                        break;
                    }

                    targets.Add(target);
                    timelineTarget[i] = targets.Count - 1;
                    units[i] = target.Unit;
                    unitKnown[i] = true;
                    break;
                }

                case SignalNodeKind.Time:
                    units[i] = ParamUnit.Seconds;
                    unitKnown[i] = true;
                    break;

                case SignalNodeKind.AudioBands:
                    units[i] = ParamUnit.Ratio;
                    unitKnown[i] = true;
                    break;

                case SignalNodeKind.Lfo:
                    // 输出无量纲（幅度×正弦）；输入只要求"不是未注册单位"
                    units[i] = ParamUnit.Dimensionless;
                    unitKnown[i] = true;
                    RejectUnspecifiedInput(i, node, "Lfo");
                    break;

                case SignalNodeKind.Add:
                {
                    var ua = InputUnit(i, CompiledSignalGraph.PortPrimary, out var ka);
                    var ub = InputUnit(i, CompiledSignalGraph.PortSecondary, out var kb);
                    if ((ka && ua == ParamUnit.Unspecified) || (kb && ub == ParamUnit.Unspecified))
                    {
                        Error(SignalIssueCodes.UnitUnspecified, node.Id, null, null,
                            "Add 的输入单位未注册（Unspecified）：Unspecified 只能进 Remap 做显式换算");
                        break;
                    }

                    if (ka && kb && ua != ub)
                    {
                        Error(SignalIssueCodes.UnitMismatch, node.Id, null, null,
                            $"Add 的两个输入单位不同（{ua} 与 {ub}）：先改成同单位，或经 Remap 显式换算");
                        break;
                    }

                    // 未连的输入不参与判定：缺的是连线，不是单位（那由 required-input-unconnected 警告点名）
                    if (ka || kb)
                    {
                        units[i] = ka ? ua : ub;
                        unitKnown[i] = true;
                    }

                    break;
                }

                case SignalNodeKind.Multiply:
                {
                    var ua = InputUnit(i, CompiledSignalGraph.PortPrimary, out var ka);
                    var ub = InputUnit(i, CompiledSignalGraph.PortSecondary, out var kb);
                    if ((ka && ua == ParamUnit.Unspecified) || (kb && ub == ParamUnit.Unspecified))
                    {
                        Error(SignalIssueCodes.UnitUnspecified, node.Id, null, null,
                            "Multiply 的输入单位未注册（Unspecified）：Unspecified 只能进 Remap 做显式换算");
                        break;
                    }

                    var dimA = ka && ua != ParamUnit.Dimensionless;
                    var dimB = kb && ub != ParamUnit.Dimensionless;
                    if (dimA && dimB)
                    {
                        Error(SignalIssueCodes.UnitMismatch, node.Id, null, null,
                            $"Multiply 至多一个输入有量纲（这里两个都是：{ua} 与 {ub}）：把其中一个换成无量纲的倍率");
                        break;
                    }

                    if (ka || kb)
                    {
                        units[i] = dimA ? ua : dimB ? ub : ParamUnit.Dimensionless;
                        unitKnown[i] = true;
                    }

                    break;
                }

                case SignalNodeKind.Clamp:
                {
                    var u = InputUnit(i, CompiledSignalGraph.PortPrimary, out var known);
                    if (known && u == ParamUnit.Unspecified)
                    {
                        Error(SignalIssueCodes.UnitUnspecified, node.Id, null, null,
                            "Clamp 的输入单位未注册（Unspecified）：Unspecified 只能进 Remap 做显式换算");
                        break;
                    }

                    if (known)
                    {
                        units[i] = u;
                        unitKnown[i] = true;
                    }

                    break;
                }

                case SignalNodeKind.Remap:
                {
                    // Remap 是 Unspecified 唯一的出路：输入不必注册单位，输出单位由 outUnit **显式**声明
                    var outUnit = (ParamUnit)paramValues[i * CompiledSignalGraph.NodeParamSlots + CompiledSignalGraph.SlotRemapOutUnit];
                    if (outUnit != ParamUnit.Unspecified)
                    {
                        units[i] = outUnit;
                        unitKnown[i] = true;
                    }

                    break;
                }

                case SignalNodeKind.ParameterOutput:
                {
                    // 出口节点的"单位"就是它**输入**的单位：绑定校验拿它和目标键比
                    RejectUnspecifiedInput(i, node, "ParameterOutput");
                    var u = InputUnit(i, CompiledSignalGraph.PortPrimary, out var known);
                    if (known)
                    {
                        units[i] = u;
                        unitKnown[i] = true;
                    }

                    break;
                }
            }
        }

        // ---- 6. 绑定校验（节点存在且是 ParameterOutput、地址可解析、目标可解析、单位一致、单写入者）----
        var bindingNode = new List<int>();
        var bindingAddress = new List<ParamAddress>();
        var bindingTarget = new List<int>();
        var bindingIds = new HashSet<string>(StringComparer.Ordinal);
        var writerByTarget = new Dictionary<ParamAddress, string>();

        foreach (var binding in graph.Bindings)
        {
            if (binding is null)
            {
                Error(SignalIssueCodes.EmptyId, null, null, null, "信号图里有一条空绑定（Bindings 含 null）");
                continue;
            }

            if (string.IsNullOrEmpty(binding.Id))
            {
                Error(SignalIssueCodes.EmptyId, null, null, null, "绑定 Id 为空");
                continue;
            }

            if (!bindingIds.Add(binding.Id))
            {
                Error(SignalIssueCodes.DuplicateBindingId, null, null, binding.Id, $"绑定 Id 重复：{binding.Id}");
                continue;
            }

            if (string.IsNullOrEmpty(binding.NodeId) || !nodeById.TryGetValue(binding.NodeId, out var node))
            {
                Error(SignalIssueCodes.BindingMissingNode, binding.NodeId, null, binding.Id,
                    $"绑定 {binding.Id} 指向的节点不存在：{binding.NodeId}");
                continue;
            }

            if (node.Kind != SignalNodeKind.ParameterOutput)
            {
                Error(SignalIssueCodes.BindingNodeNotOutput, node.Id, null, binding.Id,
                    $"只有 ParameterOutput 可以绑定目标，{node.Id} 是 {node.Kind}");
                continue;
            }

            if (!ParamAddress.TryParse(binding.Address, out var addr))
            {
                Error(SignalIssueCodes.BindingBadAddress, node.Id, null, binding.Id,
                    $"绑定地址不可解析：{binding.Address}");
                continue;
            }

            if (!SignalTargets.TryResolveBinding(addr, effects, scene, out var target, out var resolveError))
            {
                Error(ParamRegistry.CodeOf(resolveError), node.Id, null, binding.Id, ParamRegistry.MessageOf(resolveError));
                continue;
            }

            if (!writerByTarget.TryAdd(addr, binding.Id))
            {
                Error(SignalIssueCodes.TargetDuplicateWriter, node.Id, null, binding.Id,
                    $"目标 {addr} 已有绑定 {writerByTarget[addr]}：一个参数至多一个输出绑定（要叠加请用 Add/Multiply 节点）");
                continue;
            }

            if (target.Unit == ParamUnit.Unspecified)
            {
                // fail-closed：这个参数的语义量纲还没登记（S06），宁可拒绝，也不静默按错单位接线
                Error(SignalIssueCodes.UnitUnspecified, node.Id, null, binding.Id,
                    $"目标 {addr} 的参数尚未注册单位（S06 参数注册表），拒绝绑定");
                continue;
            }

            var slot = slotOf[node.Id];
            if (unitKnown[slot] && units[slot] != ParamUnit.Unspecified && units[slot] != target.Unit)
            {
                Error(SignalIssueCodes.UnitMismatch, node.Id, null, binding.Id,
                    $"绑定单位不一致：信号是 {units[slot]}，目标 {addr} 要求 {target.Unit}");
                continue;
            }

            targets.Add(target);
            bindingNode.Add(slot);
            bindingAddress.Add(addr);
            bindingTarget.Add(targets.Count - 1);
        }

        if (failed) return null;

        // ---- 7. 警告：必要输入未连 = "输出无效"，不是拒绝（§8.1 / §13.7 验收）----
        foreach (var node in nodes)
        {
            var def = SignalNodeCatalog.Get(node.Kind)!;
            var slot = slotOf[node.Id];
            foreach (var p in def.Ports)
            {
                if (!p.IsInput || !p.Required) continue;
                var port = SignalRules.PortSlotOf(node.Kind, p.Name);
                if (port < 0) continue;
                if (inputSource[slot * CompiledSignalGraph.MaxInputs + port] >= 0) continue;

                Warn(SignalIssueCodes.RequiredInputUnconnected, node.Id, null, null,
                    $"节点 {node.Id}（{node.Kind}）的必要输入「{p.Name}」未连线：该节点输出无效（输出无效 ≠ 拒绝编译）");
            }
        }

        return new CompiledSignalGraph(graph.StructureVersion, nodes, kinds, paramValues, inputSource, inputRequired,
            inputDefault, targets.ToArray(), timelineTarget, bindingAddress.ToArray(), bindingNode.ToArray(),
            bindingTarget.ToArray());
    }
}

/// <summary>
/// 编译产物（编译/复用部分）：拓扑序 + 槽位数组 + 已解析目标引用。
///
/// 帧内路径（<c>Evaluate</c>）在 SignalEvaluator.cs 的另一半里：本类刻意拆成 partial，
/// 是为了让"结构/校验/拓扑"与"每帧数值"两块各自读完，而不是交织在一千行里。
/// </summary>
public sealed partial class CompiledSignalGraph
{
    /// <summary>求值器的输入端口槽上限（Lfo 的 time/offset/amplitude 是当前最多的）。</summary>
    internal const int MaxInputs = 3;

    /// <summary>每节点保留的参数槽数（Remap 的 5 个参数是当前最多的）。</summary>
    internal const int NodeParamSlots = 5;

    internal const int PortPrimary = 0;    // in / a / time
    internal const int PortSecondary = 1;  // b / offset
    internal const int PortAmplitude = 2;  // amplitude

    internal const int SlotConstantValue = 0, SlotConstantUnit = 1;
    internal const int SlotTimeSource = 0;
    internal const int SlotBand = 0;
    internal const int SlotClampMin = 0, SlotClampMax = 1;
    internal const int SlotRemapInMin = 0, SlotRemapInMax = 1, SlotRemapOutMin = 2, SlotRemapOutMax = 3, SlotRemapOutUnit = 4;
    internal const int SlotLfoFrequency = 0, SlotLfoPhase = 1;

    private readonly int _structureVersion;
    private readonly SignalNode[] _nodes;
    private readonly SignalNodeKind[] _kind;
    private readonly bool[] _enabled;
    private readonly double[] _params;
    private readonly int[] _inputSource;
    private readonly bool[] _inputRequired;
    private readonly double[] _inputDefault;
    private readonly SignalTarget[] _targets;
    private readonly int[] _timelineTarget;
    private readonly ParamAddress[] _bindingAddress;
    private readonly int[] _bindingNode;
    private readonly int[] _bindingTarget;
    private readonly double[] _values;
    private readonly bool[] _valid;
    private readonly List<ResolvedParam> _resolved = new();
    private readonly List<SignalEvalIssue> _issueList = new();

    internal CompiledSignalGraph(
        int structureVersion,
        SignalNode[] nodes,
        SignalNodeKind[] kind,
        double[] paramValues,
        int[] inputSource,
        bool[] inputRequired,
        double[] inputDefault,
        SignalTarget[] targets,
        int[] timelineTarget,
        ParamAddress[] bindingAddress,
        int[] bindingNode,
        int[] bindingTarget)
    {
        _structureVersion = structureVersion;
        _nodes = nodes;
        _kind = kind;
        _params = paramValues;
        _inputSource = inputSource;
        _inputRequired = inputRequired;
        _inputDefault = inputDefault;
        _targets = targets;
        _timelineTarget = timelineTarget;
        _bindingAddress = bindingAddress;
        _bindingNode = bindingNode;
        _bindingTarget = bindingTarget;
        _values = new double[nodes.Length];
        _valid = new bool[nodes.Length];
        _enabled = new bool[nodes.Length];
        for (var i = 0; i < nodes.Length; i++) _enabled[i] = nodes[i].Enabled;
    }

    /// <summary>编译时的结构版本（<see cref="SignalGraph.StructureVersion"/> 的快照）。</summary>
    public int StructureVersion => _structureVersion;

    /// <summary>编译后的拓扑序节点表（Kahn + 按 Id 的 Ordinal 优先队列，两次编译逐项相同）。</summary>
    public IReadOnlyList<SignalNode> Nodes => _nodes;

    /// <summary>每个节点的当前输出值（按 <see cref="Nodes"/> 索引）；有效位为 false 时该值无意义。UI 的"数值查看"读这里。</summary>
    public IReadOnlyList<double> Values => _values;

    /// <summary>
    /// 参数复用（契约 §8.2.3）：拓扑只在结构版本变化时重编，改数值参数复用已编译拓扑。
    ///
    /// 成功 = 编译身份与参数配置都核验过，且**只**把参数值与启用位拷进本计划自己的数组：
    /// 不重排、不重建槽位、不重新校验单位。失败返回 false 且本计划一字不改（求值仍按旧值跑）。
    /// </summary>
    public bool TryApplyParams(SignalGraph graph, EffectGraph effects, out IReadOnlyList<SignalCompileIssue> issues)
    {
        var list = new List<SignalCompileIssue>();
        issues = list;

        void Fail(string code, string? nodeId, string? bindingId, string message) =>
            list.Add(new SignalCompileIssue(code, SignalIssueSeverity.Error, nodeId, null, bindingId, message));

        if (graph is null || effects is null)
        {
            Fail(SignalIssueCodes.StructureChanged, null, null, "编译输入为空（信号图或效果图缺失）");
            return false;
        }

        if (graph.StructureVersion != _structureVersion)
        {
            Fail(SignalIssueCodes.StructureChanged, null, null,
                $"信号图结构已变化（结构版本 {graph.StructureVersion} ≠ 编译时 {_structureVersion}）：必须重新编译");
            return false;
        }

        // 1) 节点身份：计划里的节点必须仍是文档里那**同一个**对象（被换掉 = 文档换了身份）
        var docNodes = new Dictionary<string, SignalNode>(StringComparer.Ordinal);
        foreach (var n in graph.Nodes)
            if (n is not null && !string.IsNullOrEmpty(n.Id))
                docNodes[n.Id] = n;

        foreach (var node in _nodes)
        {
            if (!docNodes.TryGetValue(node.Id, out var current) || !ReferenceEquals(current, node))
            {
                Fail(SignalIssueCodes.StructureChanged, node.Id, null,
                    $"节点 {node.Id} 已不在文档里或已被替换：计划已过期，必须重新编译");
                return false;
            }
        }

        // 2) 目标身份：绑定与 TimelineBaseValue 的地址仍解析到同一个 EffectNode / 同一个 EffectParam
        // （只比引用身份，不比注册项：范围随画布/单位随 schema 变不算身份变化，所以这里不必传场景）
        for (var b = 0; b < _bindingAddress.Length; b++)
        {
            var address = _bindingAddress[b];
            var compiled = _targets[_bindingTarget[b]];
            if (!SignalTargets.TryResolveBinding(address, effects, null, out var fresh, out var error))
            {
                Fail(ParamRegistry.CodeOf(error), _nodes[_bindingNode[b]].Id, null,
                    $"{ParamRegistry.MessageOf(error)}（绑定 {address} 的目标已失效）");
                return false;
            }

            if (!ReferenceEquals(fresh.Node, compiled.Node) || !ReferenceEquals(fresh.Param, compiled.Param))
            {
                Fail(ParamRegistry.CodeMissingInstance, _nodes[_bindingNode[b]].Id, null,
                    $"绑定 {address} 的目标已换成另一个实例或参数：计划不得沿用旧引用");
                return false;
            }
        }

        for (var i = 0; i < _nodes.Length; i++)
        {
            if (_kind[i] != SignalNodeKind.TimelineBaseValue || _timelineTarget[i] < 0) continue;

            var compiled = _targets[_timelineTarget[i]];
            var def = SignalNodeCatalog.Get(_kind[i])!;
            var text = SignalNodeCatalog.TextValue(_nodes[i], def, "address");

            // 地址文本改过（SetText 不算结构变化）⇒ 编译身份已经变了，必须重编
            if (!ParamAddress.TryParse(text, out var addr) || addr != compiled.Address)
            {
                Fail(SignalIssueCodes.AddressInvalid, _nodes[i].Id, null,
                    $"TimelineBaseValue 的地址文本已改为「{text}」：地址决定读哪个目标，改了必须重编");
                return false;
            }

            if (!SignalTargets.TryResolveBinding(addr, effects, null, out var fresh, out var error))
            {
                Fail(ParamRegistry.CodeOf(error), _nodes[i].Id, null, ParamRegistry.MessageOf(error));
                return false;
            }

            if (!ReferenceEquals(fresh.Node, compiled.Node) || !ReferenceEquals(fresh.Param, compiled.Param))
            {
                Fail(ParamRegistry.CodeMissingInstance, _nodes[i].Id, null,
                    $"TimelineBaseValue 的目标 {addr} 已换成另一个实例或参数");
                return false;
            }
        }

        // 3) 参数语义仍要合法（有限/域内/枚举，Remap 零区间、Clamp 上下界）——但不重校验单位
        foreach (var node in _nodes)
        {
            var def = SignalNodeCatalog.Get(node.Kind);
            if (def is null) continue;
            if (!SignalRules.ValidateNodeParams(node, def, out var code, out var message))
            {
                Fail(code, node.Id, null, message);
                return false;
            }
        }

        // 4) 全部核验通过才提交：只拷参数值与启用位（不重排、不重建槽位）
        for (var i = 0; i < _nodes.Length; i++)
        {
            FillParams(_nodes[i], _params, i * NodeParamSlots);
            _enabled[i] = _nodes[i].Enabled;
        }

        return true;
    }

    /// <summary>
    /// 把本帧 Resolved 写进帧状态（契约 §3.3 步骤 3/4）：相机四槽写帧相机、图层五参写该层帧副本；
    /// 效果域不在这里写（调用方写实例参数）。只改帧状态，不回写文档、曲线与实例常量。
    /// 必须在 <see cref="Evaluate"/> 之后调用：Evaluate 读的正是同一份帧状态的 base 值。
    /// </summary>
    public void ApplyFrameState()
    {
        var frame = FrameState;
        if (frame is null) return;

        var camera = frame.Camera;
        for (var b = 0; b < _bindingAddress.Length; b++)
        {
            var target = _targets[_bindingTarget[b]];
            if (target.Slot == PvFrameSlot.None) continue;

            // _resolved 与绑定同序（Evaluate 里每个绑定恰好产出一项），所以下标直接对齐
            var value = _resolved[b].Value;
            switch (target.Slot)
            {
                case PvFrameSlot.CameraOffsetX: camera = camera with { X = value }; break;
                case PvFrameSlot.CameraOffsetY: camera = camera with { Y = value }; break;
                case PvFrameSlot.CameraScale: camera = camera with { Scale = value }; break;
                case PvFrameSlot.CameraRotation: camera = camera with { Rotation = value }; break;
                default:
                    if (frame.TryGetLayer(target.Address.Target, out var layer))
                        frame.SetLayer(target.Address.Target, layer.With(target.Slot, value));
                    break;
            }
        }

        frame.Camera = camera;
    }

    /// <summary>本计划的帧状态（编译时从场景捕获；纯效果域的计划为 null）。</summary>
    private PvFrameState? FrameState
    {
        get
        {
            foreach (var target in _targets)
                if (target.Frame is not null)
                    return target.Frame;

            return null;
        }
    }

    /// <summary>
    /// 把 schema 声明的参数按名字抄进求值器认的固定下标：帧内不再按名字查表，也不依赖 schema 的书写顺序。
    /// </summary>
    internal static void FillParams(SignalNode node, double[] dest, int offset)
    {
        var def = SignalNodeCatalog.Get(node.Kind);
        if (def is null) return;

        switch (node.Kind)
        {
            case SignalNodeKind.Constant:
                dest[offset + SlotConstantValue] = SignalNodeCatalog.ParamValue(node, def, "value");
                dest[offset + SlotConstantUnit] = SignalNodeCatalog.ParamValue(node, def, "unit");
                break;

            case SignalNodeKind.Time:
                dest[offset + SlotTimeSource] = SignalNodeCatalog.ParamValue(node, def, "source");
                break;

            case SignalNodeKind.AudioBands:
                dest[offset + SlotBand] = SignalNodeCatalog.ParamValue(node, def, "band");
                break;

            case SignalNodeKind.Clamp:
                dest[offset + SlotClampMin] = SignalNodeCatalog.ParamValue(node, def, "min");
                dest[offset + SlotClampMax] = SignalNodeCatalog.ParamValue(node, def, "max");
                break;

            case SignalNodeKind.Remap:
                dest[offset + SlotRemapInMin] = SignalNodeCatalog.ParamValue(node, def, "inMin");
                dest[offset + SlotRemapInMax] = SignalNodeCatalog.ParamValue(node, def, "inMax");
                dest[offset + SlotRemapOutMin] = SignalNodeCatalog.ParamValue(node, def, "outMin");
                dest[offset + SlotRemapOutMax] = SignalNodeCatalog.ParamValue(node, def, "outMax");
                dest[offset + SlotRemapOutUnit] = SignalNodeCatalog.ParamValue(node, def, "outUnit");
                break;

            case SignalNodeKind.Lfo:
                dest[offset + SlotLfoFrequency] = SignalNodeCatalog.ParamValue(node, def, "frequency");
                dest[offset + SlotLfoPhase] = SignalNodeCatalog.ParamValue(node, def, "phase");
                break;
        }
    }
}
