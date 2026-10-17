namespace JizuraLive.Pv;

public enum ParamAddressKind
{
    Camera,

    Layer,

    Effect,
}

public readonly record struct ParamAddress(ParamAddressKind Kind, string Target, string Param)
{
    private const string LayerPrefix = "layer:";
    private const string EffectPrefix = "effect:";
    private const string CameraPrefix = "camera.";

    public static bool TryParse(string? text, out ParamAddress address)
    {
        address = default;
        if (string.IsNullOrEmpty(text)) return false;

        if (text.StartsWith(LayerPrefix, StringComparison.Ordinal))
            return TrySplitTarget(text, LayerPrefix.Length, ParamAddressKind.Layer, out address);

        if (text.StartsWith(EffectPrefix, StringComparison.Ordinal))
            return TrySplitTarget(text, EffectPrefix.Length, ParamAddressKind.Effect, out address);

        if (!text.StartsWith(CameraPrefix, StringComparison.Ordinal)) return false;

        var param = text[CameraPrefix.Length..];
        if (param.Length == 0) return false;

        address = new ParamAddress(ParamAddressKind.Camera, "", param);
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
        _ => CameraPrefix + Param,
    };
}
