using JizuraLive.Render.Effects;

namespace JizuraLive.Pv;

/// <summary>
/// 一个已解析的信号绑定目标：把地址文本翻译成"写哪个实例的哪个参数、什么单位、什么范围、默认值多少"。
///
/// <see cref="Node"/>/<see cref="Param"/> 是**引用**：编译期解析一次之后，求值路径只按引用读基础值，
/// 不再解析地址文本（契约 §3.2：字符串只供显示和序列化，不在每帧解析）。
/// 相机与图层域当前解析不出目标（没有写入方，见 <see cref="SignalTargets.TryResolveBinding"/>），
/// 所以经 <see cref="SignalTargets"/> 拿到的目标恒来自效果实例：<see cref="Node"/>/<see cref="Param"/>
/// 非 null，单位/范围/默认值取自该实例的 schema。
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
/// 目标键解析：注册表（S06）定单位/范围/默认值，本类补上效果实例的写入引用。
///
/// 错误文本形如 <c>&lt;code&gt;: &lt;消息&gt;</c>：码给编译器和 UI 做稳定分类（面板与测试都断言码，文案可以改），
/// 消息给人看。
/// </summary>
public static class SignalTargets
{
    /// <summary>
    /// 解析到**效果实例**参数。只认 effect 域：调用方（铺参数键、面板预检）要的是"哪个实例的哪个键"，
    /// 相机/图层没有效果参数可铺。相机与图层的地址走 <see cref="TryResolveBinding"/>——那里同样解析不到
    /// 写入方，当前一律以 <c>target-domain-unwired</c> fail-closed 拒绝。
    /// </summary>
    public static bool TryResolve(in ParamAddress addr, EffectGraph effects, out SignalTarget target, out string? error)
    {
        target = default;

        if (addr.Kind != ParamAddressKind.Effect)
        {
            error = $"{ParamRegistry.CodeMissingInstance}: {addr} 不是效果实例参数（本入口只解析 effect 域，相机/图层走 SignalTargets.TryResolveBinding）";
            return false;
        }

        return TryResolveEffect(addr, effects, out target, out error);
    }

    /// <summary>
    /// 解析**任意域**的绑定/基础值目标（契约 §8.1：绑定的 Address 三种形态都是合法地址）。
    /// <paramref name="scene"/> 只影响相机的位移范围，传 null 按默认画布解析。
    ///
    /// 相机/图层域 fail-closed 拒绝（<see cref="ParamRegistry.CodeDomainUnwired"/>）：地址合法，
    /// 但渲染侧没有写入方——<c>PvPipeline.ApplySignals</c> 只写效果实例参数（这些目标的
    /// <see cref="SignalTarget.Node"/> 是 null，解析值会被直接丢掉），而图层参数是文档常量、
    /// 帧内没有覆盖通道。编译通过而值被丢掉就是"显示连线、画面不动"，比报错更难查，所以宁可拒绝。
    /// 等渲染侧真接上（相机写本帧 <c>Stage.Camera</c>、图层写该层的合成输入）再放开这一条。
    /// </summary>
    public static bool TryResolveBinding(in ParamAddress addr, EffectGraph effects, PvScene? scene,
        out SignalTarget target, out string? error)
    {
        target = default;

        if (addr.Kind == ParamAddressKind.Effect)
            return TryResolveEffect(addr, effects, out target, out error);

        // 先走注册表：地址本身写错了（未知参数名 / 缺图层 Id / 镜头带了 Target）要报它自己的码，
        // 不能一律报成"没接线"——两种拒绝的原因必须分得开
        if (!ParamRegistry.TryResolve(addr, effects, scene, out _, out error)) return false;

        error = $"{ParamRegistry.CodeDomainUnwired}: {addr} 是相机/图层参数；注册表认得它，"
            + "但渲染侧还没有写入方（信号只写效果实例参数，图层参数是文档常量、帧内没有覆盖通道）。"
            + "接线完成前拒绝绑定：编译通过而值被丢掉等于静默无效";
        return false;
    }

    private static bool TryResolveEffect(in ParamAddress addr, EffectGraph effects, out SignalTarget target, out string? error)
    {
        target = default;

        if (!ParamRegistry.TryFindEffectParam(addr, effects, out var node, out var param, out error)) return false;

        target = new SignalTarget(addr, param!.Unit, param.Min, param.Max, param.Default, node!, param);
        error = null;
        return true;
    }

    /// <summary>
    /// 目标的基础值（契约 §3.3 步骤 2）：命中曲线取曲线值，否则取实例常量
    /// （<see cref="EffectNode.Value"/> 已回落 schema 默认）。
    ///
    /// 纯函数、只读：曲线 Key 与实例常量在求值前后一字不改——动画值绝不写回文档。
    /// 相机/图层的目标到不了这里（<see cref="TryResolveBinding"/> 在编译期就拒了它们），
    /// 所以这条路径上的 <see cref="SignalTarget.Node"/> 恒非 null：基础值就是实例常量。
    /// </summary>
    public static double BaseValue(in SignalTarget target, DirectorEvaluator? curves, double transportTime)
    {
        if (curves is not null && curves.TryEvaluate(target.Address, transportTime, out var animated))
            return animated;

        return target.Node is null ? target.Default : target.Node.Value(target.Param);
    }
}
