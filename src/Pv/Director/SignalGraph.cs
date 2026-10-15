using Newtonsoft.Json;

namespace JizuraLive.Pv;

/// <summary>
/// 信号图上的一个节点：引用一个 <see cref="SignalNodeKind"/>，参数按**名字**存（不按下标/槽位）。
/// 名字由 <see cref="SignalNodeCatalog"/> 声明，所以改求值实现不会污染已保存的图；
/// <see cref="Params"/> 只有在 schema 声明过的键才合法（编辑入口与加载治理各拦一道）。
/// </summary>
public sealed class SignalNode
{
    public string Id { get; set; } = "";

    public SignalNodeKind Kind { get; set; }

    /// <summary>禁用 = 输出无效（显式状态），**不是**适用于纹理节点的通用 Bypass（契约 §8.2.5）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>数值参数（按 schema 的参数名索引）。</summary>
    public Dictionary<string, double> Params { get; set; } = new();

    /// <summary>文本参数（目前只有地址文本）。</summary>
    public Dictionary<string, string> Texts { get; set; } = new();

    /// <summary>
    /// 画布横坐标（网格吸附后的值，由画布写入）。
    /// null = 存档里没有坐标（旧工程，或新节点还没摆）：读入按 (0,0) 参与显示并**标记待自动布局**。
    /// 坐标是布局不是内容——改它不动 <see cref="SignalGraph.StructureVersion"/>，已编译的拓扑继续复用。
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public double? X { get; set; }

    /// <summary>画布纵坐标，语义同 <see cref="X"/>。</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public double? Y { get; set; }

    /// <summary>两个坐标都有 = 这个节点不用再自动布局。</summary>
    [JsonIgnore]
    public bool HasPosition => X.HasValue && Y.HasValue;
}

/// <summary>
/// 一条边：从 <see cref="FromNode"/> 的输出端口连到 <see cref="ToNode"/> 的输入端口。
/// 端口名落进存档而不是序号——将来给某类节点加端口不会把旧存档的连线接到别的引脚上。
/// </summary>
public sealed class SignalEdge
{
    public string Id { get; set; } = "";
    public string FromNode { get; set; } = "";
    public string FromPort { get; set; } = "";
    public string ToNode { get; set; } = "";
    public string ToPort { get; set; } = "";
}

/// <summary>
/// 一条绑定：某个 <see cref="SignalNodeKind.ParameterOutput"/> 节点写向一个参数地址。
///
/// <see cref="Address"/> 存文本（<c>effect:fx_glow_ab12.intensity</c>），只在编译期解析一次；
/// 目标身份不存进节点——一个 ParameterOutput 不保存"第二份 Target"（契约 §13.7）。
/// </summary>
public sealed class SignalBinding
{
    public string Id { get; set; } = "";
    public string NodeId { get; set; } = "";
    public string Address { get; set; } = "";
}

/// <summary>
/// 数值信号图（文档形态）：节点 + 边 + 绑定。
///
/// 两条稳定约定：
/// <list type="number">
/// <item>本类只做**局部校验**（Id/端口/范围/单输入单边这类当场能判的），
/// 全图校验（环、单位统一、目标可解析）留给 <see cref="SignalCompiler"/>：
/// 编辑入口不该为了"现在能不能编译"而拒绝一次中间态编辑。</item>
/// <item>只有**结构增删**才递增 <see cref="StructureVersion"/>；参数值变化不动它——
/// 契约 §13.6「变化分类」要求常量/Key 值变化只更新数据，编译期据此复用拓扑（不重排、不重建槽位）。</item>
/// </list>
/// </summary>
public sealed class SignalGraph
{
    /// <summary>节点数上限（契约 §15.3：超过限制在候选采用之前拒绝并说明数量，不截断保存）。</summary>
    public const int MaxNodes = 512;

    /// <summary>边数上限（同上）。</summary>
    public const int MaxEdges = 2048;

    public List<SignalNode> Nodes { get; set; } = new();

    public List<SignalEdge> Edges { get; set; } = new();

    public List<SignalBinding> Bindings { get; set; } = new();

    /// <summary>
    /// 结构版本：只在节点/边/绑定的增删上递增。参数与文本改动**不**动它
    /// （见类型注释第 2 条；<c>TryApplyParams</c> 靠它判断能否复用已编译拓扑）。
    /// 不进存档：它是会话内的"这一版拓扑"标记，不是文档内容。
    /// </summary>
    [JsonIgnore]
    public int StructureVersion { get; private set; }

    /// <summary>
    /// 造一个新节点：发一个 <c>n_&lt;kind&gt;_&lt;6hex&gt;</c> 号，并填好 schema 默认值。
    /// **不入图**——入图走 <see cref="TryAddNode"/>（那里才有唯一性与合法性校验）。
    /// Id 形态仿 <c>EffectGraph.NewInstanceId</c>：写进 JSON 也一眼看得出是哪种节点的第几个实例。
    /// </summary>
    public SignalNode CreateNode(SignalNodeKind kind)
    {
        var node = new SignalNode
        {
            Id = $"n_{kind.ToString().ToLowerInvariant()}_{Guid.NewGuid().ToString("N")[..6]}",
            Kind = kind,
        };

        var def = SignalNodeCatalog.Get(kind);
        if (def is not null)
        {
            foreach (var p in def.Params)
            {
                if (p.Kind == SignalParamKind.Text) node.Texts[p.Name] = p.DefaultText;
                else node.Params[p.Name] = p.Default;
            }
        }

        return node;
    }

    /// <summary>入图。失败时 <paramref name="error"/> 给原因，图内容一字不变。</summary>
    public bool TryAddNode(SignalNode node, out string? error)
    {
        if (node is null)
        {
            error = "节点为空";
            return false;
        }

        if (string.IsNullOrEmpty(node.Id))
        {
            error = "节点 Id 不能为空";
            return false;
        }

        if (FindNode(node.Id) is not null)
        {
            error = $"节点 Id 已存在：{node.Id}";
            return false;
        }

        var def = SignalNodeCatalog.Get(node.Kind);
        if (def is null)
        {
            error = $"未知节点种类：{(int)node.Kind}";
            return false;
        }

        if (Nodes.Count >= MaxNodes)
        {
            error = $"信号图节点数已达上限 {MaxNodes}";
            return false;
        }

        if (!ValidateParams(node, def, out error)) return false;

        Nodes.Add(node);
        StructureVersion++;
        error = null;
        return true;
    }

    /// <summary>改数值参数。名字未声明、非有限、越域（含离散参数的枚举域）一律拒绝并保持原值。</summary>
    public bool SetParam(SignalNode node, string name, double value)
    {
        if (node is null) return false;

        var def = SignalNodeCatalog.Get(node.Kind);
        if (def is null) return false;

        var p = SignalNodeCatalog.FindParam(def, name);
        if (p is null || p.Kind != SignalParamKind.Number) return false;
        if (!IsLegalNumber(node.Kind, p, value)) return false;

        node.Params[name] = value;
        return true;
    }

    /// <summary>改文本参数（地址一类）。名字未声明或种类不是 Text 一律拒绝。</summary>
    public bool SetText(SignalNode node, string name, string value)
    {
        if (node is null) return false;

        var def = SignalNodeCatalog.Get(node.Kind);
        if (def is null) return false;

        var p = SignalNodeCatalog.FindParam(def, name);
        if (p is null || p.Kind != SignalParamKind.Text) return false;

        node.Texts[name] = value;
        return true;
    }

    /// <summary>
    /// 连一条边。就地拒绝：Id 空/重复、端点不存在、端口名未声明、方向不是 Output→Input、
    /// 自环、同一输入已有连线（契约 §8.2.1 的"端口存在/多重输入/环路"当场能判的部分）。
    /// </summary>
    public bool TryAddEdge(SignalEdge edge, out string? error)
    {
        if (edge is null)
        {
            error = "边为空";
            return false;
        }

        if (string.IsNullOrEmpty(edge.Id))
        {
            error = "边 Id 不能为空";
            return false;
        }

        if (FindEdge(edge.Id) is not null)
        {
            error = $"边 Id 已存在：{edge.Id}";
            return false;
        }

        if (Edges.Count >= MaxEdges)
        {
            error = $"信号图边数已达上限 {MaxEdges}";
            return false;
        }

        var from = FindNode(edge.FromNode);
        if (from is null)
        {
            error = $"边的起点节点不存在：{edge.FromNode}";
            return false;
        }

        var to = FindNode(edge.ToNode);
        if (to is null)
        {
            error = $"边的终点节点不存在：{edge.ToNode}";
            return false;
        }

        if (string.Equals(edge.FromNode, edge.ToNode, StringComparison.Ordinal))
        {
            error = $"不允许自环：{edge.FromNode}";
            return false;
        }

        var fromDef = SignalNodeCatalog.Get(from.Kind);
        var fromPort = fromDef is null ? null : SignalNodeCatalog.FindPort(fromDef, edge.FromPort);
        if (fromPort is null)
        {
            error = $"节点 {from.Id}（{from.Kind}）未声明端口「{edge.FromPort}」";
            return false;
        }

        if (fromPort.IsInput)
        {
            error = $"边的起点必须是输出端口，「{edge.FromPort}」是输入端";
            return false;
        }

        var toDef = SignalNodeCatalog.Get(to.Kind);
        var toPort = toDef is null ? null : SignalNodeCatalog.FindPort(toDef, edge.ToPort);
        if (toPort is null)
        {
            error = $"节点 {to.Id}（{to.Kind}）未声明端口「{edge.ToPort}」";
            return false;
        }

        if (!toPort.IsInput)
        {
            error = $"边的终点必须是输入端口，「{edge.ToPort}」是输出端";
            return false;
        }

        if (FindEdgeIntoInput(edge.ToNode, edge.ToPort) is not null)
        {
            error = $"输入端口 {edge.ToNode}.{edge.ToPort} 已有一条连线（一个输入至多一条边）";
            return false;
        }

        Edges.Add(edge);
        StructureVersion++;
        error = null;
        return true;
    }

    /// <summary>加一条绑定。节点必须存在且是 ParameterOutput，地址必须能解析，同目标地址至多一条绑定。</summary>
    public bool TryAddBinding(SignalBinding binding, out string? error)
    {
        if (binding is null)
        {
            error = "绑定为空";
            return false;
        }

        if (string.IsNullOrEmpty(binding.Id))
        {
            error = "绑定 Id 不能为空";
            return false;
        }

        if (FindBinding(binding.Id) is not null)
        {
            error = $"绑定 Id 已存在：{binding.Id}";
            return false;
        }

        var node = FindNode(binding.NodeId);
        if (node is null)
        {
            error = $"绑定节点不存在：{binding.NodeId}";
            return false;
        }

        if (node.Kind != SignalNodeKind.ParameterOutput)
        {
            error = $"只有 ParameterOutput 节点可以绑定目标，{node.Id} 是 {node.Kind}";
            return false;
        }

        if (!ParamAddress.TryParse(binding.Address, out var addr))
        {
            error = $"绑定地址不可解析：{binding.Address}";
            return false;
        }

        foreach (var existing in Bindings)
        {
            if (existing is not null && ParamAddress.TryParse(existing.Address, out var other) && other == addr)
            {
                error = $"目标 {addr} 已有一条绑定（一个参数至多一个输出绑定）";
                return false;
            }
        }

        Bindings.Add(binding);
        StructureVersion++;
        error = null;
        return true;
    }

    /// <summary>删节点：连带删掉指向它的边与挂在它身上的绑定，绝不留下悬空引用（契约 §13.1）。</summary>
    public bool RemoveNode(string id)
    {
        var index = FindNodeIndex(id);
        if (index < 0) return false;

        Nodes.RemoveAt(index);
        Edges.RemoveAll(e => e is null || IsEndpoint(e, id));
        Bindings.RemoveAll(b => b is null || string.Equals(b.NodeId, id, StringComparison.Ordinal));
        StructureVersion++;
        return true;
    }

    public bool RemoveEdge(string id)
    {
        var removed = Edges.RemoveAll(e => e is not null && string.Equals(e.Id, id, StringComparison.Ordinal));
        if (removed == 0) return false;

        StructureVersion++;
        return true;
    }

    public bool RemoveBinding(string id)
    {
        var removed = Bindings.RemoveAll(b => b is not null && string.Equals(b.Id, id, StringComparison.Ordinal));
        if (removed == 0) return false;

        StructureVersion++;
        return true;
    }

    /// <summary>删掉指向某实例的全部绑定（返回条数）。空 targetId 什么都不做，避免误删一整类目标。</summary>
    public int RemoveBindingsForTarget(string targetId)
    {
        if (string.IsNullOrEmpty(targetId)) return 0;

        var removed = Bindings.RemoveAll(b => b is not null && AddressTargets(b.Address, targetId));
        if (removed > 0) StructureVersion++;
        return removed;
    }

    /// <summary>
    /// 删除某个目标（效果实例）时的级联，对齐 <see cref="DirectorEvaluator.RemoveCurvesForTarget"/>：
    /// 指向该实例的绑定、读取该实例地址的 TimelineBaseValue 节点及其关联边一起消失；
    /// 其它节点保留（下游按"缺输入"失效，而不是被悄悄接到下一个实例）。返回删掉的条目总数。
    /// </summary>
    public int RemoveTargetCascade(string targetId)
    {
        if (string.IsNullOrEmpty(targetId)) return 0;

        var removed = RemoveBindingsForTarget(targetId);

        List<string>? doomed = null;
        foreach (var node in Nodes)
        {
            if (node is null || node.Kind != SignalNodeKind.TimelineBaseValue) continue;
            var def = SignalNodeCatalog.Get(node.Kind);
            if (def is null) continue;
            if (AddressTargets(SignalNodeCatalog.TextValue(node, def, "address"), targetId))
                (doomed ??= new List<string>()).Add(node.Id);
        }

        if (doomed is null) return removed;

        foreach (var id in doomed)
            if (RemoveNode(id)) removed++;

        return removed;
    }

    private static bool IsEndpoint(SignalEdge edge, string nodeId) =>
        string.Equals(edge.FromNode, nodeId, StringComparison.Ordinal) ||
        string.Equals(edge.ToNode, nodeId, StringComparison.Ordinal);

    private static bool AddressTargets(string? address, string targetId) =>
        ParamAddress.TryParse(address, out var addr) && string.Equals(addr.Target, targetId, StringComparison.Ordinal);

    private SignalNode? FindNode(string id)
    {
        var index = FindNodeIndex(id);
        return index < 0 ? null : Nodes[index];
    }

    private int FindNodeIndex(string id)
    {
        for (var i = 0; i < Nodes.Count; i++)
            if (Nodes[i] is not null && string.Equals(Nodes[i].Id, id, StringComparison.Ordinal))
                return i;
        return -1;
    }

    private SignalEdge? FindEdge(string id)
    {
        foreach (var e in Edges)
            if (e is not null && string.Equals(e.Id, id, StringComparison.Ordinal))
                return e;
        return null;
    }

    private SignalEdge? FindEdgeIntoInput(string nodeId, string port)
    {
        foreach (var e in Edges)
        {
            if (e is null) continue;
            if (string.Equals(e.ToNode, nodeId, StringComparison.Ordinal) &&
                string.Equals(e.ToPort, port, StringComparison.Ordinal))
                return e;
        }

        return null;
    }

    private SignalBinding? FindBinding(string id)
    {
        foreach (var b in Bindings)
            if (b is not null && string.Equals(b.Id, id, StringComparison.Ordinal))
                return b;
        return null;
    }

    /// <summary>
    /// 节点参数的局部校验：只认 schema 声明过的键，值必须有限、在域内。
    /// 跨参数语义（Remap 零区间、Clamp 上下界反了）不在编辑期拦——它们是"整张图的配置"，由编译器一次报全。
    /// </summary>
    private static bool ValidateParams(SignalNode node, SignalNodeDef def, out string? error)
    {
        foreach (var (key, value) in node.Params)
        {
            var p = SignalNodeCatalog.FindParam(def, key);
            if (p is null || p.Kind != SignalParamKind.Number)
            {
                error = $"节点 {node.Id}（{node.Kind}）未声明参数「{key}」";
                return false;
            }

            if (!IsLegalNumber(node.Kind, p, value))
            {
                error = $"节点 {node.Id}（{node.Kind}）的参数「{key}」取值非法：{value}";
                return false;
            }
        }

        foreach (var key in node.Texts.Keys)
        {
            var p = SignalNodeCatalog.FindParam(def, key);
            if (p is null || p.Kind != SignalParamKind.Text)
            {
                error = $"节点 {node.Id}（{node.Kind}）未声明文本参数「{key}」";
                return false;
            }
        }

        error = null;
        return true;
    }

    /// <summary>
    /// 单元（单个参数）合法性：有限，且在域内。离散选择参数要求整数且在枚举域内
    /// （Unspecified 是"已定义但不是一种量纲"，留给编译器按 <c>param-invalid-enum</c> 报，让编辑能表达这个中间态）。
    /// </summary>
    internal static bool IsLegalNumber(SignalNodeKind kind, SignalParamDef p, double value)
    {
        if (!double.IsFinite(value)) return false;

        if (SignalNodeCatalog.IsDiscreteParam(kind, p.Name))
            return value == Math.Floor(value) && value >= p.Min && value <= p.Max;

        return value >= p.Min && value <= p.Max;
    }
}
