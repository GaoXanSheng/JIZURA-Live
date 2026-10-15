using JizuraLive.Pv;
using JizuraLive.Render.Effects;

namespace JizuraLive.App.PvStage.Blueprint;

/// <summary>
/// 画布节点（只读投影）。端口/参数 schema 一律读 <see cref="SignalNodeCatalog"/>，画布不另立一份；
/// <see cref="X"/>/<see cref="Y"/> 是网格吸附后的画布坐标（与 SignalNode.x/y 同口径）。
/// </summary>
public sealed record BpNode(string Id, SignalNodeKind Kind, double X, double Y)
{
    private static readonly IReadOnlyDictionary<string, double> NoNumbers = new Dictionary<string, double>();
    private static readonly IReadOnlyDictionary<string, string> NoTexts = new Dictionary<string, string>();
    private static readonly IReadOnlyDictionary<string, ParamUnit> NoUnits = new Dictionary<string, ParamUnit>();

    public bool Enabled { get; init; } = true;

    /// <summary>数值参数（schema 参数名索引）；缺键按 schema 声明的默认值。</summary>
    public IReadOnlyDictionary<string, double> Params { get; init; } = NoNumbers;

    /// <summary>文本参数（地址一类）。</summary>
    public IReadOnlyDictionary<string, string> Texts { get; init; } = NoTexts;

    /// <summary>引脚单位（端口名索引）。画布只显示不推导：单位规则只住在 SignalCompiler 一处。</summary>
    public IReadOnlyDictionary<string, ParamUnit> PortUnits { get; init; } = NoUnits;
}

/// <summary>一条边（只读投影）。端口名口径与 <see cref="SignalEdge"/> 相同。</summary>
public readonly record struct BpEdge(string Id, string FromNode, string FromPort, string ToNode, string ToPort);

/// <summary>一条绑定（只读投影）。画布只拿它显示 ParameterOutput 的目标地址。</summary>
public readonly record struct BpBinding(string Id, string NodeId, string Address);

/// <summary>一个节点本帧的输出与有效位（宿主从求值诊断喂进来）。</summary>
public readonly record struct BpValue(double Value, bool Valid);

/// <summary>画布的全量输入快照。</summary>
public sealed class BpSnapshot
{
    public IReadOnlyList<BpNode> Nodes { get; init; } = [];
    public IReadOnlyList<BpEdge> Edges { get; init; } = [];
    public IReadOnlyList<BpBinding> Bindings { get; init; } = [];

    /// <summary>已注册的地址文本（camera/layer/effect），供地址下拉；顺序即下拉顺序。</summary>
    public IReadOnlyList<string> AddressOptions { get; init; } = [];
}

/// <summary>画布手势的语义。一次手势一个 <see cref="BpEdit"/>，对应宿主的一个撤销事务。</summary>
public enum BpEditKind
{
    MoveNodes,
    Connect,
    Disconnect,
    RemoveNodes,
    SetEnabled,
    SetParam,
    SetText,
    SetBinding,
    AddNode,
}

/// <summary>一个节点拖动后的落点（画布坐标，已吸附）。</summary>
public readonly record struct BpNodeMove(string NodeId, double X, double Y);

/// <summary>
/// 画布回传给宿主的编辑意图。只描述"改了什么"，不描述"怎么改文档"：
/// 边的 Id、节点 Id 的生成、以及禁用/参数落库都由宿主决定。
/// </summary>
public sealed record BpEdit
{
    public BpEditKind Kind { get; init; }

    /// <summary>MoveNodes：落点表。</summary>
    public IReadOnlyList<BpNodeMove> Moves { get; init; } = [];

    /// <summary>RemoveNodes / SetEnabled / SetParam / SetText / SetBinding：作用的节点集。</summary>
    public IReadOnlyList<string> NodeIds { get; init; } = [];

    /// <summary>Connect：起点节点与端口。</summary>
    public string FromNode { get; init; } = "";

    public string FromPort { get; init; } = "";

    /// <summary>Connect：终点节点与端口。</summary>
    public string ToNode { get; init; } = "";

    public string ToPort { get; init; } = "";

    /// <summary>Disconnect：要删的边。</summary>
    public string EdgeId { get; init; } = "";

    /// <summary>SetEnabled：目标状态。</summary>
    public bool Enabled { get; init; }

    /// <summary>SetParam / SetText：schema 参数名。</summary>
    public string ParamName { get; init; } = "";

    /// <summary>SetParam：新值。</summary>
    public double Value { get; init; }

    /// <summary>SetText / SetBinding：新文本（地址）。</summary>
    public string Text { get; init; } = "";

    /// <summary>AddNode：要创建的种类与落点。</summary>
    public SignalNodeKind AddKind { get; init; }

    public double X { get; init; }

    public double Y { get; init; }
}
