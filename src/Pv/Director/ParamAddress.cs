namespace JizuraLive.Pv;

/// <summary>可寻址参数的形式，决定 <see cref="ParamAddress.Target"/> 的语义。</summary>
public enum ParamAddressKind
{
    /// <summary>镜头参数（无目标 Id）。</summary>
    Camera,

    /// <summary>图层参数，Target 是图层 Id（<c>character</c> / <c>lyrics_pv</c> / <c>src_1</c>）。</summary>
    Layer,

    /// <summary>
    /// 效果实例参数，Target 是**实例** Id（<c>fx_glow_ab12</c>）而不是定义 Id：
    /// 同一种效果可以有多个实例，各自独立寻址（两张 glow 的 intensity 是两条曲线）。
    /// </summary>
    Effect,
}

/// <summary>
/// 参数地址：动画曲线、信号绑定的键。三种文本形式：
/// <list type="bullet">
/// <item><c>camera.scale</c> → 镜头参数（Target 为空）</item>
/// <item><c>layer:lyrics_pv.parallax.depth</c> → 图层参数（Param 可含点）</item>
/// <item><c>effect:fx_glow_ab12.intensity</c> → 效果实例参数</item>
/// </list>
///
/// readonly record struct：值相等，可以直接当字典键——解析一次得到的地址与再解析一次的另一个实例是同一个键，
/// 不会出现"两个看起来一样的字符串各自占一个槽"。文本形式只用于持久化与 UI 显示。
/// </summary>
public readonly record struct ParamAddress(ParamAddressKind Kind, string Target, string Param)
{
    private const string LayerPrefix = "layer:";
    private const string EffectPrefix = "effect:";
    private const string CameraPrefix = "camera.";

    /// <summary>
    /// 解析地址文本。非法输入返回 false 且 <paramref name="address"/> 为 default。
    /// 不抛异常：调用方拿到的是存档 / UI 文本，坏输入要能优雅报告（整份工程不该为一条坏地址打不开）。
    /// </summary>
    public static bool TryParse(string? text, out ParamAddress address)
    {
        address = default;
        if (string.IsNullOrEmpty(text)) return false;

        if (text.StartsWith(LayerPrefix, StringComparison.Ordinal))
            return TrySplitTarget(text, LayerPrefix.Length, ParamAddressKind.Layer, out address);

        if (text.StartsWith(EffectPrefix, StringComparison.Ordinal))
            return TrySplitTarget(text, EffectPrefix.Length, ParamAddressKind.Effect, out address);

        // 无前缀的形式只认 camera：若把任意 "foo.bar" 都当镜头参数，ToString 就吐不回原文本（往返不闭合），
        // 更糟的是拼错的地址会被静默当成另一条合法曲线。未知前缀一律拒绝。
        if (!text.StartsWith(CameraPrefix, StringComparison.Ordinal)) return false;

        var param = text[CameraPrefix.Length..];
        if (param.Length == 0) return false;

        address = new ParamAddress(ParamAddressKind.Camera, "", param);
        return true;
    }

    /// <summary>把 <c>&lt;前缀&gt;&lt;目标 Id&gt;.&lt;参数名&gt;</c> 切开：Id 到第一个点为止，余下整段是参数名。</summary>
    private static bool TrySplitTarget(string text, int prefixLength, ParamAddressKind kind, out ParamAddress address)
    {
        address = default;

        var rest = text[prefixLength..];
        var dot = rest.IndexOf('.');
        // dot &lt; 0：没有点，整段只是一个 Id，缺参数名；dot == 0：":" 后直接是点，Id 为空
        if (dot <= 0) return false;
        var param = rest[(dot + 1)..];
        // 空参数名永远指不到真实参数，允许它只会在字典里多一个永远取不到值的键（而且 UI 上不可见）
        if (param.Length == 0) return false;

        address = new ParamAddress(kind, rest[..dot], param);
        return true;
    }

    /// <summary>回文本。<see cref="TryParse"/> 后接 <see cref="ToString"/> 必须回到原文本（往返稳定）。</summary>
    public override string ToString() => Kind switch
    {
        ParamAddressKind.Layer => LayerPrefix + Target + "." + Param,
        ParamAddressKind.Effect => EffectPrefix + Target + "." + Param,
        _ => CameraPrefix + Param,
    };
}
