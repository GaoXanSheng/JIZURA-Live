namespace JizuraLive.Pv;

public enum ParamAddressKind
{
    Camera,

    Layer,

    Effect,

    Vts,
}

public readonly record struct ParamAddress(ParamAddressKind Kind, string Target, string Param)
{
    private const string LayerPrefix = "layer:";
    private const string EffectPrefix = "effect:";
    private const string CameraPrefix = "camera.";
    private const string VtsPrefix = "vts.";

    public static bool TryParse(string? text, out ParamAddress address)
    {
        address = default;
        if (string.IsNullOrEmpty(text)) return false;

        if (text.StartsWith(LayerPrefix, StringComparison.Ordinal))
            return TrySplitTarget(text, LayerPrefix.Length, ParamAddressKind.Layer, out address);

        if (text.StartsWith(EffectPrefix, StringComparison.Ordinal))
            return TrySplitTarget(text, EffectPrefix.Length, ParamAddressKind.Effect, out address);

        if (text.StartsWith(CameraPrefix, StringComparison.Ordinal))
        {
            var cameraParam = text[CameraPrefix.Length..];
            if (cameraParam.Length == 0) return false;
            address = new ParamAddress(ParamAddressKind.Camera, "", cameraParam);
            return true;
        }

        if (!text.StartsWith(VtsPrefix, StringComparison.Ordinal)) return false;

        var vtsParam = text[VtsPrefix.Length..];
        if (vtsParam.Length == 0) return false;

        address = new ParamAddress(ParamAddressKind.Vts, "model", vtsParam);
        return true;
    }

    private static bool TrySplitTarget(string text, int prefixLength, ParamAddressKind kind, out ParamAddress address)
    {
        address = default;

        var rest = text[prefixLength..];
        var dot = rest.IndexOf('.');
        if (dot <= 0) return false;
        var param = rest[(dot + 1)..];
        if (param.Length == 0) return false;

        address = new ParamAddress(kind, rest[..dot], param);
        return true;
    }

    public override string ToString() => Kind switch
    {
        ParamAddressKind.Layer => LayerPrefix + Target + "." + Param,
        ParamAddressKind.Effect => EffectPrefix + Target + "." + Param,
        ParamAddressKind.Vts => VtsPrefix + Param,
        _ => CameraPrefix + Param,
    };
}
