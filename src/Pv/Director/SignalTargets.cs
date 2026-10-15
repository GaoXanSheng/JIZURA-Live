using JizuraLive.Render.Effects;

namespace JizuraLive.Pv;

/// <summary>
/// 帧状态里的写入槽（契约 §13.6 S06 消费表）：相机四槽 + 图层五参。
/// 编译期从地址解析一次（<see cref="SignalTargets.TryResolveBinding"/>），帧内只按槽 switch，不再比对参数字符串。
/// <see cref="None"/> = 效果域：它写实例参数，不经帧状态。
/// </summary>
public enum PvFrameSlot
{
    None,

    CameraOffsetX,
    CameraOffsetY,
    CameraScale,
    CameraRotation,

    LayerOffsetX,
    LayerOffsetY,
    LayerScale,
    LayerOpacity,
    LayerRotation,
}

/// <summary>
/// 一个已解析的信号绑定目标：把地址文本翻译成"写哪里、什么单位、什么范围、默认值多少"。
///
/// <see cref="Node"/>/<see cref="Param"/> 是**引用**：编译期解析一次之后，求值路径只按引用读基础值，
/// 不再解析地址文本（契约 §3.2：字符串只供显示和序列化，不在每帧解析）。
/// <see cref="Slot"/> 非 None 时（相机/图层域）没有效果实例可写，<see cref="Node"/>/<see cref="Param"/>
/// 无意义（恒为 null），写入方是所有者场景**本帧**的帧状态槽；效果域则 <see cref="Slot"/> 为 None、
/// <see cref="Scene"/> 为 null。
///
/// <see cref="Scene"/> 存的是**所有者**而不是帧状态实例：帧状态每帧整份换新（copy-on-write），
/// 编译期捕获实例的话下一帧就永远是陈旧的那一份。
/// </summary>
public readonly record struct SignalTarget(
    ParamAddress Address,
    ParamUnit Unit,
    double Min,
    double Max,
    double Default,
    EffectNode Node,
    EffectParam Param,
    PvFrameSlot Slot,
    PvScene? Scene);

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
    /// 相机/图层没有效果参数可铺。相机与图层的地址走 <see cref="TryResolveBinding"/>。
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
    /// <paramref name="scene"/> 给相机的位移范围与图层目标的存在性；相机/图层域**必须**有场景
    /// （它们的写入方是本帧帧状态），传 null 一律拒绝——效果域不需要场景。
    ///
    /// 三个域都解析到真正的写入方：效果域写实例参数（<see cref="SignalTarget.Node"/>），
    /// 相机/图层域写本帧 <see cref="PvFrameState"/> 的槽（<see cref="SignalTarget.Slot"/>，见 S06 消费表）。
    /// 解析不出槽的目标一律 fail-closed 拒绝——编译通过而值被丢掉就是"显示连线、画面不动"，比报错更难查。
    /// </summary>
    public static bool TryResolveBinding(in ParamAddress addr, EffectGraph effects, PvScene? scene,
        out SignalTarget target, out string? error)
    {
        target = default;

        if (addr.Kind == ParamAddressKind.Effect)
            return TryResolveEffect(addr, effects, out target, out error);

        // 先走注册表：地址本身写错了（未知参数名 / 缺图层 Id / 镜头带了 Target）要报它自己的码，
        // 不能一律报成"没接线"——两种拒绝的原因必须分得开
        if (!ParamRegistry.TryResolve(addr, effects, scene, out var spec, out error)) return false;

        // 相机/图层的写入方是本帧帧状态，而帧状态只挂在场景上：没有场景就没有写入方，
        // 编译通过也只会把值静默丢掉（"显示连线、画面不动"比报错更难查），所以 fail-closed。
        if (scene is null)
        {
            error = $"{ParamRegistry.CodeDomainUnwired}: {addr} 的写入方是本帧帧状态，解析需要场景（scene 为 null 时编译通过、值被静默丢掉）";
            return false;
        }

        // 图层目标必须真的在场景里：绑到一个不存在的图层 Id 上，帧状态槽写进去也没人看（静默无效），
        // 与效果域"实例必须存在"同一条 fail-closed
        if (addr.Kind == ParamAddressKind.Layer && !HasLayer(scene, addr.Target))
        {
            error = $"{ParamRegistry.CodeMissingInstance}: 场景里没有 Id 为「{addr.Target}」的图层（地址 {addr}）";
            return false;
        }

        var slot = SlotOf(addr);
        if (slot == PvFrameSlot.None)
        {
            // 注册表认得这个参数、消费表却不认：两张表漂了。宁可拒绝，也不要接一条写不到任何地方的值
            error = $"{ParamRegistry.CodeMissingKey}: {addr} 解析不出帧状态槽（参数注册表与 S06 消费表不一致）";
            return false;
        }

        target = new SignalTarget(addr, spec.Unit, spec.Min, spec.Max, spec.Default, null!, null!, slot, scene);
        error = null;
        return true;
    }

    /// <summary>地址 → 帧状态槽。注册表先过了一遍参数名，所以这里只可能拿到它认得的名字。</summary>
    private static PvFrameSlot SlotOf(in ParamAddress addr) => addr.Kind switch
    {
        ParamAddressKind.Camera => addr.Param switch
        {
            "offsetX" => PvFrameSlot.CameraOffsetX,
            "offsetY" => PvFrameSlot.CameraOffsetY,
            "scale" => PvFrameSlot.CameraScale,
            "rotation" => PvFrameSlot.CameraRotation,
            _ => PvFrameSlot.None,
        },
        ParamAddressKind.Layer => addr.Param switch
        {
            "offsetX" => PvFrameSlot.LayerOffsetX,
            "offsetY" => PvFrameSlot.LayerOffsetY,
            "scale" => PvFrameSlot.LayerScale,
            "opacity" => PvFrameSlot.LayerOpacity,
            "rotation" => PvFrameSlot.LayerRotation,
            _ => PvFrameSlot.None,
        },
        _ => PvFrameSlot.None,
    };

    private static bool HasLayer(PvScene scene, string id)
    {
        foreach (var layer in scene.Layers)
            if (layer is not null && string.Equals(layer.Id, id, StringComparison.Ordinal))
                return true;
        return false;
    }

    private static bool TryResolveEffect(in ParamAddress addr, EffectGraph effects, out SignalTarget target, out string? error)
    {
        target = default;

        if (!ParamRegistry.TryFindEffectParam(addr, effects, out var node, out var param, out error)) return false;

        target = new SignalTarget(addr, param!.Unit, param.Min, param.Max, param.Default, node!, param, PvFrameSlot.None, null);
        error = null;
        return true;
    }

    /// <summary>
    /// 目标的基础值（契约 §3.3 步骤 2）：相机/图层域取帧状态里的 base——那是"文档常量 → 曲线"的产物，
    /// 曲线只有渲染侧那一处采样；效果域命中曲线取曲线值，否则取实例常量
    /// （<see cref="EffectNode.Value"/> 已回落 schema 默认）。
    ///
    /// <paramref name="frame"/> 是调用方本帧的**草稿**帧状态（渲染线程）；传 null 时读所有者场景当前
    /// **已发布**的快照——UI 徽标求值走这条，拿到的永远是某一帧的完整快照，不会是写了一半的对象。
    ///
    /// 纯函数、只读：曲线 Key、实例常量与帧状态在求值前后一字不改——动画值绝不写回文档。
    /// </summary>
    public static double BaseValue(in SignalTarget target, DirectorEvaluator? curves, double transportTime,
        PvFrameState? frame = null)
    {
        if (target.Slot != PvFrameSlot.None)
        {
            var state = frame ?? target.Scene?.Frame;
            if (state is not null && state.TryGetBase(target.Slot, target.Address.Target, out var frameBase))
                return frameBase;
        }

        if (curves is not null && curves.TryEvaluate(target.Address, transportTime, out var animated))
            return animated;

        return target.Node is null ? target.Default : target.Node.Value(target.Param);
    }
}
