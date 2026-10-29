using JizuraLive.Render.Effects;

namespace JizuraLive.Pv;

public readonly record struct ParamSpec(ParamUnit Unit, double Min, double Max, double Default, string Label);

public static class ParamRegistry
{
    internal const string CodeMissingInstance = "target-missing-instance";
    internal const string CodeMissingKey = "target-missing-key";
    internal const string CodeNotNumber = "target-not-number";

    private const double DegreeRange = 180;

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

            case ParamAddressKind.Vts:
                return TryResolveVts(addr, out spec, out error);

            default:
                if (!TryFindEffectParam(addr, effects, out _, out var param, out error)) return false;
                spec = new ParamSpec(param!.Unit, param.Min, param.Max, param.Default, param.LabelKey);
                return true;
        }
    }

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

        var channel = addr.Param.Length > 2
                      && addr.Param[^2] == '.'
                      && addr.Param[^1] is 'r' or 'g' or 'b';
        var baseKey = channel ? addr.Param[..^2] : addr.Param;

        foreach (var p in def.Params)
        {
            if (!string.Equals(p.Key, baseKey, StringComparison.Ordinal)) continue;

            if (channel)
            {
                if (p.Kind != EffectParamKind.Color)
                {
                    error = $"{CodeMissingKey}: {addr} 只在颜色参数上支持 .r/.g/.b 通道";
                    return false;
                }

                param = p;
                error = null;
                return true;
            }

            if (p.Kind is not (EffectParamKind.Number or EffectParamKind.Bool))
            {
                error = p.Kind == EffectParamKind.Color
                    ? $"{CodeNotNumber}: {addr} 是颜色参数；请用 {p.Key}.r / {p.Key}.g / {p.Key}.b 三条通道"
                    : $"{CodeNotNumber}: {addr} 是 {p.Kind} 参数；可动画通道只支持 Number / Bool / Color 通道";
                return false;
            }

            param = p;
            error = null;
            return true;
        }

        error = $"{CodeMissingKey}: 实例 {node.InstanceId}（{node.DefId}）没有参数「{addr.Param}」（地址应按 Key，不是 DefId/列表下标/槽位）";
        return false;
    }

    private static bool TryResolveCamera(in ParamAddress addr, PvScene? scene, out ParamSpec spec, out string? error)
    {
        spec = default;
        if (addr.Target.Length != 0)
        {
            error = $"{CodeMissingKey}: 镜头的 Target 恒为空（地址形态 camera.scale），「{addr.Target}」不是镜头 Id";
            return false;
        }

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

            case "blur":
                spec = new ParamSpec(ParamUnit.Pixels, 0, 40, 0, "Param.Camera.Blur");
                error = null;
                return true;

            case "brighten":
                spec = new ParamSpec(ParamUnit.Ratio, 0, 1, 0, "Param.Camera.Brighten");
                error = null;
                return true;

            default:
                error = $"{CodeMissingKey}: 镜头没有参数「{addr.Param}」（首版注册 offsetX/offsetY/scale/rotation/blur/brighten）";
                return false;
        }
    }

    private static bool TryResolveVts(in ParamAddress addr, out ParamSpec spec, out string? error)
    {
        spec = default;
        switch (addr.Param)
        {
            case "x":
                spec = new ParamSpec(ParamUnit.Dimensionless, -1000, 1000, 0, "Param.Vts.X");
                error = null;
                return true;

            case "y":
                spec = new ParamSpec(ParamUnit.Dimensionless, -1000, 1000, 0, "Param.Vts.Y");
                error = null;
                return true;

            case "size":
                spec = new ParamSpec(ParamUnit.Dimensionless, -90, 300, 0, "Param.Vts.Size");
                error = null;
                return true;

            case "rotation":
                spec = new ParamSpec(ParamUnit.Degrees, -DegreeRange, DegreeRange, 0, "Param.Vts.Rotation");
                error = null;
                return true;

            default:
                error = $"{CodeMissingKey}: 角色移动只有 x/y/size/rotation 四条通道（地址 vts.{addr.Param}）";
                return false;
        }
    }

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
