using JizuraLive.Render.Effects;

namespace JizuraLive.Pv;

/// <summary>
/// 一个可寻址参数的注册项（S06）：单位、范围、默认值与显示标签。
/// 单位是量纲标注（供界面与诊断读）；范围是**写入目标时**用来限幅的那一对，不是文档的合法性边界。
/// </summary>
public readonly record struct ParamSpec(ParamUnit Unit, double Min, double Max, double Default, string Label);

/// <summary>
/// 参数注册表（S06）：地址 → (单位, 范围, 默认值, 标签)。
///
/// 三个域各有来源，但对外只有这一份答案：
/// <list type="bullet">
/// <item>相机与图层是**静态表**——参数集合不随工程变化，只有相机的位移范围随画布尺寸（±1 个画布宽/高）。</item>
/// <item>效果按该**实例**的 <see cref="EffectParam"/> schema 解析，未标注单位的键保持 Unspecified
/// （缺口留在明处，而不是被当成某个默认量纲）。</item>
/// </list>
///
/// 这里不夹取值：越界由调用方按 <see cref="ParamSpec.Min"/>/<see cref="ParamSpec.Max"/> 夹并打 clamp 标
/// （契约 §3.3 步骤 4），所以同一份注册表既服务限幅、也服务"只显示不修改"的诊断。
/// </summary>
public static class ParamRegistry
{
    // 稳定错误码：面板与测试断言码，文案可以改、码不行。解析失败一律是 <code>: <消息>。
    internal const string CodeMissingInstance = "target-missing-instance";
    internal const string CodeMissingKey = "target-missing-key";
    internal const string CodeNotNumber = "target-not-number";

    private const double DegreeRange = 180;

    /// <summary>
    /// 解析一个地址到注册项。失败返回 false 并给出 <c>&lt;code&gt;: &lt;消息&gt;</c>。
    /// <paramref name="scene"/> 只用于相机的位移范围；传 null 按默认画布（1920×1080）解析。
    /// </summary>
    public static bool TryResolve(in ParamAddress addr, EffectGraph? effects, PvScene? scene,
        out ParamSpec spec, out string? error)
    {
        spec = default;
        switch (addr.Kind)
        {
            case ParamAddressKind.Camera:
                return TryResolveCamera(addr, scene, out spec, out error);

            case ParamAddressKind.Layer:
                return TryResolveLayer(addr, out spec, out error);

            default:
                if (!TryFindEffectParam(addr, effects, out _, out var param, out error)) return false;
                spec = new ParamSpec(param!.Unit, param.Min, param.Max, param.Default, param.LabelKey);
                return true;
        }
    }

    /// <summary>
    /// 效果域：按**实例** Id 找到实例与其 schema 参数。
    /// <see cref="TryResolve"/> 靠它取单位/范围/默认值。
    /// </summary>
    private static bool TryFindEffectParam(in ParamAddress addr, EffectGraph? effects,
        out EffectNode? node, out EffectParam? param, out string? error)
    {
        node = null;
        param = null;

        if (effects is null)
        {
            error = $"{CodeMissingInstance}: 效果图为空，无法解析 {addr}";
            return false;
        }

        node = FindByInstanceId(effects, addr.Target);
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
                error = $"{CodeNotNumber}: {addr} 是 {p.Kind} 参数；可动画通道只支持 Number";
                return false;
            }

            param = p;
            error = null;
            return true;
        }

        error = $"{CodeMissingKey}: 实例 {node.InstanceId}（{node.DefId}）没有参数「{addr.Param}」（地址应按 Key，不是 DefId/列表下标/槽位）";
        return false;
    }

    /// <summary>
    /// 镜头参数。唯一主镜头用保留目标 main，但地址文本的现有形态是 <c>camera.scale</c>（Target 为空），
    /// 所以 Target 非空一律拒绝——否则 ToString 吐不回原文本，地址往返就断了。
    /// </summary>
    private static bool TryResolveCamera(in ParamAddress addr, PvScene? scene, out ParamSpec spec, out string? error)
    {
        spec = default;
        if (addr.Target.Length != 0)
        {
            error = $"{CodeMissingKey}: 镜头的 Target 恒为空（地址形态 camera.scale），「{addr.Target}」不是镜头 Id";
            return false;
        }

        // 位移是"设计像素"，范围 ±1 个画布：没有场景时按默认画布解析（范围随画布变，单位不变）
        var canvas = scene ?? new PvScene();

        switch (addr.Param)
        {
            case "offsetX":
                spec = new ParamSpec(ParamUnit.Pixels, -canvas.Width, canvas.Width, 0, "Param.Camera.OffsetX");
                error = null;
                return true;

            case "offsetY":
                spec = new ParamSpec(ParamUnit.Pixels, -canvas.Height, canvas.Height, 0, "Param.Camera.OffsetY");
                error = null;
                return true;

            case "scale":
                spec = new ParamSpec(ParamUnit.Dimensionless, 0.5, 2, 1, "Param.Camera.Scale");
                error = null;
                return true;

            case "rotation":
                spec = new ParamSpec(ParamUnit.Degrees, -DegreeRange, DegreeRange, 0, "Param.Camera.Rotation");
                error = null;
                return true;

            default:
                error = $"{CodeMissingKey}: 镜头没有参数「{addr.Param}」（首版注册 offsetX/offsetY/scale/rotation）";
                return false;
        }
    }

    /// <summary>图层参数：静态表，任意图层 Id 共用同一组参数（几何/透明度是每层都有的那几项）。</summary>
    private static bool TryResolveLayer(in ParamAddress addr, out ParamSpec spec, out string? error)
    {
        spec = default;
        if (addr.Target.Length == 0)
        {
            error = $"{CodeMissingKey}: 图层地址缺少图层 Id（形态 layer:&lt;id&gt;.{addr.Param}）";
            return false;
        }

        switch (addr.Param)
        {
            case "offsetX":
                spec = new ParamSpec(ParamUnit.Ratio, -1, 1, 0, "Param.Layer.OffsetX");
                error = null;
                return true;

            case "offsetY":
                spec = new ParamSpec(ParamUnit.Ratio, -1, 1, 0, "Param.Layer.OffsetY");
                error = null;
                return true;

            case "scale":
                spec = new ParamSpec(ParamUnit.Dimensionless, 0.1, 5, 1, "Param.Layer.Scale");
                error = null;
                return true;

            case "opacity":
                spec = new ParamSpec(ParamUnit.Ratio, 0, 1, 1, "Param.Layer.Opacity");
                error = null;
                return true;

            case "rotation":
                spec = new ParamSpec(ParamUnit.Degrees, -DegreeRange, DegreeRange, 0, "Param.Layer.Rotation");
                error = null;
                return true;

            default:
                error = $"{CodeMissingKey}: 图层没有参数「{addr.Param}」（首版注册 offsetX/offsetY/scale/opacity/rotation）";
                return false;
        }
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
