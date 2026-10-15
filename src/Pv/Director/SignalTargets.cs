using JizuraLive.Render.Effects;

namespace JizuraLive.Pv;

/// <summary>
/// 一个已解析的信号绑定目标：把地址文本翻译成"写哪个实例的哪个参数、什么单位、什么范围、默认值多少"。
///
/// <see cref="Node"/>/<see cref="Param"/> 是**引用**：编译期解析一次之后，求值路径只按引用读基础值，
/// 不再解析地址文本（契约 §3.2：字符串只供显示和序列化，不在每帧解析）。
/// </summary>
public readonly record struct SignalTarget(
    ParamAddress Address,
    ParamUnit Unit,
    double Min,
    double Max,
    double Default,
    EffectNode Node,
    EffectParam Param);

/// <summary>
/// 目标键解析（首版只注册 effect 域，按域分派）。
///
/// 错误文本形如 <c>&lt;code&gt;: &lt;消息&gt;</c>：码给编译器和 UI 做稳定分类（面板与测试都断言码，文案可以改），
/// 消息给人看。相机/图层域的参数注册表（S06）在代码里还不存在，所以这里明确拒绝该域——
/// 在信号线里另建一份相机参数表就是"同一事实两处真相"。
/// </summary>
public static class SignalTargets
{
    internal const string CodeMissingInstance = "target-missing-instance";
    internal const string CodeMissingKey = "target-missing-key";
    internal const string CodeNotNumber = "target-not-number";
    internal const string CodeDomainUnregistered = "target-domain-unregistered";

    private const char CodeSeparator = ':';

    /// <summary>
    /// 解析地址到一个真实目标。
    /// 只认 effect 域、只认 <see cref="EffectParamKind.Number"/> 参数（Bool/Color 首版仍走静态控件）。
    /// </summary>
    public static bool TryResolve(in ParamAddress addr, EffectGraph effects, out SignalTarget target, out string? error)
    {
        target = default;

        if (addr.Kind != ParamAddressKind.Effect)
        {
            error = $"{CodeDomainUnregistered}: 该域参数表尚未注册（S06），本线只注册 effect 域：{addr}";
            return false;
        }

        var node = FindByInstanceId(effects, addr.Target);
        if (node is null)
        {
            // 用 DefId 当地址是最常见的手滑：同一种效果可以有多个实例，DefId 指不到"哪一个"。
            // 这条路径必须点名，否则用户看到的是"找不到实例"却明明有那个效果。
            var defIdHit = FindByDefId(effects, addr.Target) is not null;
            error = defIdHit
                ? $"{CodeMissingInstance}: 地址里的「{addr.Target}」是效果**定义** Id；DefId 不能代替实例 Id（同一种效果可以有多个实例，各自独立寻址）"
                : $"{CodeMissingInstance}: 效果图里没有实例 Id 为「{addr.Target}」的节点（地址 {addr}）";
            return false;
        }

        var def = EffectCatalog.Get(node.DefId);
        if (def is null)
        {
            error = $"{CodeMissingKey}: 实例 {node.InstanceId} 的效果定义「{node.DefId}」不在目录里，无法确认参数表";
            return false;
        }

        foreach (var p in def.Params)
        {
            if (!string.Equals(p.Key, addr.Param, StringComparison.Ordinal)) continue;

            if (p.Kind != EffectParamKind.Number)
            {
                error = $"{CodeNotNumber}: {addr} 是 {p.Kind} 参数；信号绑定首版只开放 Number";
                return false;
            }

            target = new SignalTarget(addr, p.Unit, p.Min, p.Max, p.Default, node, p);
            error = null;
            return true;
        }

        error = $"{CodeMissingKey}: 实例 {node.InstanceId}（{node.DefId}）没有参数「{addr.Param}」（地址应按 Key，不是 DefId/列表下标/槽位）";
        return false;
    }

    /// <summary>
    /// 目标的基础值（契约 §3.3 步骤 2）：命中曲线取曲线值，否则取实例常量
    /// （<see cref="EffectNode.Value"/> 已回落 schema 默认）。
    ///
    /// 纯函数、只读：曲线 Key 与实例常量在求值前后一字不改——动画值绝不写回文档。
    /// </summary>
    public static double BaseValue(in SignalTarget target, DirectorEvaluator? curves, double transportTime)
    {
        if (curves is not null && curves.TryEvaluate(target.Address, transportTime, out var animated))
            return animated;

        return target.Node.Value(target.Param);
    }

    /// <summary>从 <c>&lt;code&gt;: &lt;消息&gt;</c> 里取码（编译期把它转成 <c>SignalCompileIssue.Code</c>）。</summary>
    internal static string CodeOf(string? error)
    {
        if (string.IsNullOrEmpty(error)) return "";
        var i = error.IndexOf(CodeSeparator);
        return i > 0 ? error[..i] : error;
    }

    /// <summary>从 <c>&lt;code&gt;: &lt;消息&gt;</c> 里取消息部分。</summary>
    internal static string MessageOf(string? error)
    {
        if (string.IsNullOrEmpty(error)) return "";
        var i = error.IndexOf(CodeSeparator);
        return i >= 0 && i + 1 < error.Length ? error[(i + 1)..].Trim() : error;
    }

    /// <summary>按**实例** Id 找（绝不用 DefId 匹配：那是"哪种效果"而不是"哪一个"）。</summary>
    private static EffectNode? FindByInstanceId(EffectGraph effects, string instanceId)
    {
        foreach (var n in effects.Nodes)
            if (n is not null && string.Equals(n.InstanceId, instanceId, StringComparison.Ordinal))
                return n;
        return null;
    }

    private static EffectNode? FindByDefId(EffectGraph effects, string defId)
    {
        foreach (var n in effects.Nodes)
            if (n is not null && string.Equals(n.DefId, defId, StringComparison.Ordinal))
                return n;
        return null;
    }
}
