using JizuraLive.Render.Effects;

namespace JizuraLive.Pv;

/// <summary>
/// 信号图节点种类。数值即**存档契约**：种类重排等于让所有旧工程接到别的节点上，
/// 只能追加、不能改已有成员的数值（新增种类算结构变化，必须抬 DirectorProject.CurrentSchemaVersion）。
/// 首版只开放 Float 数值节点（契约 §8.1）：VtsPose 与有状态节点（Beat/Blink/Damper/Envelope）未过准入门槛。
/// </summary>
public enum SignalNodeKind
{
    /// <summary>显式单位常量（value + unit）。</summary>
    Constant = 0,

    /// <summary>读某地址的基础值（常量 + 曲线结果），只读、不自反馈。</summary>
    TimelineBaseValue = 1,

    /// <summary>时间源：显式选择 Transport（工程时间轴）或 Live（单调时钟）。</summary>
    Time = 2,

    /// <summary>音频频带快照（带有效位；失效不当作 0）。</summary>
    AudioBands = 3,

    /// <summary>算术相加（要求两输入同单位）。</summary>
    Add = 4,

    /// <summary>相乘（至多一个输入有量纲）。</summary>
    Multiply = 5,

    /// <summary>区间夹取。</summary>
    Clamp = 6,

    /// <summary>区间重映射（先把比例 clamp 到 0..1，再映射到声明的输出单位）。</summary>
    Remap = 7,

    /// <summary>低频振荡：offset + amplitude×sin(2π×frequency×time + phase)，无按帧累计状态。</summary>
    Lfo = 8,

    /// <summary>绑定出口：一个 ParameterOutput 通过 bindings 写目标（节点本身不存第二份 Target）。</summary>
    ParameterOutput = 9,
}

/// <summary>节点参数的种类（决定编辑控件、以及合法性检查走哪条规则）。</summary>
public enum SignalParamKind
{
    /// <summary>数值参数：编辑用滑杆/输入框，合法性检查用有限性 + 范围（或枚举域）。</summary>
    Number,

    /// <summary>文本参数：地址一类只供显示与编译期解析，不进帧内路径。</summary>
    Text,
}

/// <summary>
/// 端口定义：名字 / 方向 / 是否必需 / 未连接时的声明默认值。
/// 端口名与方向是稳定契约——存档里存的就是端口名，改名字等于改文件格式。
/// </summary>
public sealed record SignalPortDef(string Name, bool IsInput, bool Required, double Default);

/// <summary>
/// 参数定义。<see cref="Kind"/> = Number 时用 <see cref="Default"/>/<see cref="Min"/>/<see cref="Max"/>；
/// = Text 时用 <see cref="DefaultText"/>。
/// 离散选择参数（band/source/unit/outUnit）的 [Min,Max] 就是它的枚举域，越域报 <c>param-invalid-enum</c>。
/// </summary>
public sealed record SignalParamDef(string Name, SignalParamKind Kind, double Default, double Min, double Max, string DefaultText);

/// <summary>
/// 一种节点的 schema：端口表 + 参数表。
/// 首版节点种类、端口名/方向/默认值全部固定，**不接受** JSON 指定任意 C# 类型（契约 §8.1）。
/// </summary>
public sealed record SignalNodeDef(SignalNodeKind Kind, string NameKey, SignalPortDef[] Ports, SignalParamDef[] Params);

/// <summary>
/// 信号节点目录：节点 schema 的唯一真相（编辑入口的默认值、编译期的校验、求值器的端口语义都读这一份表）。
///
/// 为什么不是"每种节点一个类"：首版节点都是无状态纯函数，用数据表 + 一个求值 switch 表达，
/// 比每个节点一套类型更少代码也更容易一眼看全端口/单位约定；将来真有有状态节点（§8.3 准入门槛）时再单独建模。
/// </summary>
public static class SignalNodeCatalog
{
    /// <summary>
    /// "任意有限双精度"：Constant.value / Clamp 上下界 / Remap 各界按这个域声明。
    /// 这不是"不校验"——越出这个域必然是非有限值，照样被 param-not-finite 拒绝。
    /// </summary>
    private const double AnyFinite = double.MaxValue;

    // 端口是共享的不可变定义：同名同义就用同一个实例，避免 23 处各写一遍默认值写出分叉。
    private static readonly SignalPortDef Out = new("out", false, false, 0);
    private static readonly SignalPortDef InRequired = new("in", true, true, 0);
    private static readonly SignalPortDef ARequired = new("a", true, true, 0);
    private static readonly SignalPortDef BRequired = new("b", true, true, 0);

    /// <summary>全部节点定义。枚举值即存档契约，不得重排。</summary>
    public static readonly IReadOnlyList<SignalNodeDef> All =
    [
        new(SignalNodeKind.Constant, "Signal.Node.Constant",
            [Out],
            [
                // value：显式单位常量本身（必须有限）
                new SignalParamDef("value", SignalParamKind.Number, 0, -AnyFinite, AnyFinite, ""),
                // unit：ParamUnit，默认 1 = Dimensionless；必须是已定义的枚举成员且 ≠ Unspecified
                new SignalParamDef("unit", SignalParamKind.Number, (double)ParamUnit.Dimensionless,
                    (double)ParamUnit.Unspecified, (double)ParamUnit.Seconds, ""),
            ]),

        new(SignalNodeKind.TimelineBaseValue, "Signal.Node.TimelineBaseValue",
            [Out],
            [
                // address：参数地址文本，只在编译期解析一次（契约 §3.2）
                new SignalParamDef("address", SignalParamKind.Text, 0, 0, 0, ""),
            ]),

        new(SignalNodeKind.Time, "Signal.Node.Time",
            [Out],
            [
                // source：0 = Transport（工程时间轴），1 = Live（单调时钟，暂停也走）
                new SignalParamDef("source", SignalParamKind.Number, 0, 0, 1, ""),
            ]),

        new(SignalNodeKind.AudioBands, "Signal.Node.AudioBands",
            [Out],
            [
                // band：0 = Rms，1 = Bass（默认），2 = Mid，3 = High
                new SignalParamDef("band", SignalParamKind.Number, 1, 0, 3, ""),
            ]),

        new(SignalNodeKind.Add, "Signal.Node.Add",
            [ARequired, BRequired, Out],
            []),

        new(SignalNodeKind.Multiply, "Signal.Node.Multiply",
            [ARequired, BRequired, Out],
            []),

        new(SignalNodeKind.Clamp, "Signal.Node.Clamp",
            [InRequired, Out],
            [
                new SignalParamDef("min", SignalParamKind.Number, 0, -AnyFinite, AnyFinite, ""),
                new SignalParamDef("max", SignalParamKind.Number, 1, -AnyFinite, AnyFinite, ""),
            ]),

        new(SignalNodeKind.Remap, "Signal.Node.Remap",
            [InRequired, Out],
            [
                new SignalParamDef("inMin", SignalParamKind.Number, 0, -AnyFinite, AnyFinite, ""),
                new SignalParamDef("inMax", SignalParamKind.Number, 1, -AnyFinite, AnyFinite, ""),
                new SignalParamDef("outMin", SignalParamKind.Number, 0, -AnyFinite, AnyFinite, ""),
                new SignalParamDef("outMax", SignalParamKind.Number, 1, -AnyFinite, AnyFinite, ""),
                // outUnit：输出单位由这里**显式**声明——Unspecified 的输入只能从这条路走出量纲
                new SignalParamDef("outUnit", SignalParamKind.Number, (double)ParamUnit.Dimensionless,
                    (double)ParamUnit.Unspecified, (double)ParamUnit.Seconds, ""),
            ]),

        new(SignalNodeKind.Lfo, "Signal.Node.Lfo",
            [
                new SignalPortDef("time", true, true, 0),      // 时间输入：由 Time 节点给（单位 Seconds）
                new SignalPortDef("offset", true, false, 0),   // 可选：未连则 0
                new SignalPortDef("amplitude", true, false, 1),// 可选：未连则 1
                Out,
            ],
            [
                new SignalParamDef("frequency", SignalParamKind.Number, 1, 0, 1000, ""), // Hz
                new SignalParamDef("phase", SignalParamKind.Number, 0, -AnyFinite, AnyFinite, ""), // 弧度
            ]),

        new(SignalNodeKind.ParameterOutput, "Signal.Node.ParameterOutput",
            [InRequired],
            []),
    ];

    private static readonly Dictionary<SignalNodeKind, SignalNodeDef> ByKind =
        All.ToDictionary(d => d.Kind);

    /// <summary>取节点定义；未定义的枚举值返回 null（存档里手改出来的 Kind=99 走这条路）。</summary>
    public static SignalNodeDef? Get(SignalNodeKind kind) =>
        Enum.IsDefined(kind) && ByKind.TryGetValue(kind, out var def) ? def : null;

    /// <summary>取参数定义；名字未声明返回 null。</summary>
    internal static SignalParamDef? FindParam(SignalNodeDef def, string? name)
    {
        if (name is null) return null;
        foreach (var p in def.Params)
            if (string.Equals(p.Name, name, StringComparison.Ordinal))
                return p;
        return null;
    }

    /// <summary>取端口定义；名字未声明返回 null。</summary>
    internal static SignalPortDef? FindPort(SignalNodeDef def, string? name)
    {
        if (name is null) return null;
        foreach (var p in def.Ports)
            if (string.Equals(p.Name, name, StringComparison.Ordinal))
                return p;
        return null;
    }

    /// <summary>参数取值：节点上没有这个键就用 schema 声明的默认值（手写 JSON 缺键不该等价于 0）。</summary>
    internal static double ParamValue(SignalNode node, SignalNodeDef def, string name) =>
        node.Params.TryGetValue(name, out var v) ? v : (FindParam(def, name)?.Default ?? 0);

    /// <summary>文本取值：同上，缺键回落声明的默认文本。</summary>
    internal static string TextValue(SignalNode node, SignalNodeDef def, string name) =>
        node.Texts.TryGetValue(name, out var v) ? v : (FindParam(def, name)?.DefaultText ?? "");

    /// <summary>
    /// 离散选择参数（取值域是枚举而不是连续区间）：越域报 <c>param-invalid-enum</c> 而不是 param-out-of-range，
    /// 因为对用户来说"这里只能选这几个之一"和"这个数太大了"是完全不同的两句话。
    /// </summary>
    internal static bool IsDiscreteParam(SignalNodeKind kind, string name) => kind switch
    {
        SignalNodeKind.Constant => name == "unit",
        SignalNodeKind.Time => name == "source",
        SignalNodeKind.AudioBands => name == "band",
        SignalNodeKind.Remap => name == "outUnit",
        _ => false,
    };

    /// <summary>单位声明参数：除了"是已定义枚举成员"，还必须 ≠ Unspecified（它是"没登记"，不是一种量纲）。</summary>
    internal static bool IsUnitParam(SignalNodeKind kind, string name) =>
        (kind == SignalNodeKind.Constant && name == "unit") ||
        (kind == SignalNodeKind.Remap && name == "outUnit");
}
