using System.Globalization;

namespace JizuraLive.Render.Effects;

/// <summary>
/// 效果挂载阶段（**分类的第一根轴**）：效果「挂在哪」决定它能拿到什么输入、在哪一层生效。
/// </summary>
public enum EffectStage
{
    /// <summary>单个输入源（Spout/图片/视频）自身。</summary>
    Source,

    /// <summary>单个图层（背景 / 角色 / 歌词 / 字幕…）。</summary>
    Layer,

    /// <summary>图层之间的合成（两输入）。</summary>
    Composite,

    /// <summary>整帧后期（所有图层合完之后）。</summary>
    GlobalPost,

    /// <summary>转场（A→B，两输入）。</summary>
    Transition,

    /// <summary>输出前（letterbox / 透明 / 编码前）。</summary>
    Output,
}

/// <summary>
/// 效果类别（**分类的第二根轴**）：效果「是什么」决定它在 UI 里归到哪一组、怎么被检索。
/// </summary>
public enum EffectCategory
{
    /// <summary>调色：饱和度/对比/色相/LUT/色调染色。</summary>
    ColorGrade,

    /// <summary>光效：泛光/辉光/光条/星芒/光线。</summary>
    LightGlow,

    /// <summary>扭曲故障：色差/块位移/扫描线/液态/波浪/鱼眼。</summary>
    DistortGlitch,

    /// <summary>几何：抖动/缩放冲击/旋转/切片/马赛克。</summary>
    Geometry,

    /// <summary>质感：颗粒/纸纹/半调/抖动量化/暗角/VHS。</summary>
    Texture,

    /// <summary>粒子：尘/火花/音符/雪/彩带。</summary>
    Particle,

    /// <summary>文字：逐字/卡拉OK/描边/字形变形。</summary>
    Typography,

    /// <summary>运镜：推拉/手持/地震/视差。</summary>
    Camera,
}

/// <summary>参数种类（决定 UI 用什么控件、以及往常量缓冲里填几个分量）。</summary>
public enum EffectParamKind
{
    /// <summary>数值滑杆：1 个分量。</summary>
    Number,

    /// <summary>开关：1 个分量（0/1）。</summary>
    Bool,

    /// <summary>颜色：3 个连续分量（RGB），UI 用取色器。</summary>
    Color,
}

/// <summary>
/// 效果参数的定义（**schema**）。UI 完全由它生成控件——这是「参数不再是一堆散落的 if」的关键。
/// <paramref name="Slot"/>/<paramref name="Comp"/> 直接对应 HLSL 里的 <c>P[slot].comp</c>。
/// </summary>
public sealed record EffectParam(
    string Key,
    string LabelKey,
    EffectParamKind Kind,
    int Slot,
    int Comp,
    float Min = 0f,
    float Max = 1f,
    float Default = 0f,
    float Step = 0.01f,
    string Group = "main")
{
    /// <summary>颜色参数的默认值（<c>#rrggbb</c>）。</summary>
    public string DefaultColor { get; init; } = "#ffffff";

    /// <summary>
    /// 参数单位（量纲）。默认 <see cref="ParamUnit.Unspecified"/> = 尚未标注，
    /// 因而**不能**被信号绑定当作目标：绑定会被 fail-closed 拒绝，把"这个参数还没登记单位"的缺口暴露出来，
    /// 而不是静默按错单位接线（补齐属 S06 参数注册表的工作）。
    ///
    /// 刻意不进位置参数：全仓 60+ 处 <see cref="Num"/> 构造只在需要标注时才写它，
    /// 加一个 init 属性既不改既有调用，也不改任何既有行为（没有地方把 EffectParam 当字典键或做相等比较）。
    /// </summary>
    public ParamUnit Unit { get; init; } = ParamUnit.Unspecified;

    public static EffectParam Num(string key, string labelKey, int slot, int comp,
        float min, float max, float def, float step = 0.01f, string group = "main",
        ParamUnit unit = ParamUnit.Unspecified) =>
        new(key, labelKey, EffectParamKind.Number, slot, comp, min, max, def, step, group)
        {
            Unit = unit,
        };

    public static EffectParam Flag(string key, string labelKey, int slot, int comp,
        bool def, string group = "main") =>
        new(key, labelKey, EffectParamKind.Bool, slot, comp, 0f, 1f, def ? 1f : 0f, 1f, group);

    public static EffectParam Color3(string key, string labelKey, int slot, int comp,
        string def = "#ffffff", string group = "main") =>
        new(key, labelKey, EffectParamKind.Color, slot, comp, 0f, 1f, 0f, 0.01f, group)
        {
            DefaultColor = def,
        };
}

/// <summary>
/// 多 pass 效果的渲染图构建器：往 <paramref name="graph"/> 里加若干 pass，返回最终输出的槽位。
/// <paramref name="input"/> 是上一阶段在本图里的槽位（<see cref="Gpu.GpuRenderGraph.External"/> = 外部输入）。
/// </summary>
public delegate int EffectGraphBuild(EffectNode node, Gpu.GpuRenderGraph graph,
    Gpu.ShaderLibrary library, int input, int width, int height);

/// <summary>一个效果的完整定义：身份 + 两轴分类 + HLSL + 参数 schema + 检索标签。</summary>
public sealed record EffectDef(
    string Id,
    string NameKey,
    EffectStage Stage,
    EffectCategory Category,
    string Hlsl,
    EffectParam[] Params,
    string[] Tags,
    float CostHint = 1f)
{
    /// <summary>
    /// 多 pass 效果的渲染图构建器。非空时 <see cref="Hlsl"/> 只作为文档，
    /// 实际绘制由构建器编排（mip 链 bloom 这类有向无环图表达不了单输入链）。
    /// </summary>
    public EffectGraphBuild? GraphBuild { get; init; }

    public bool IsMultiPass => GraphBuild is not null;
}

/// <summary>
/// 效果图上的一个节点：引用某个 <see cref="EffectDef"/>，带开关与参数值。
/// 参数值按 key 存（不按槽位），所以改 HLSL 的槽位不会污染已保存的参数。
/// </summary>
public sealed class EffectNode
{
    public string DefId { get; init; } = "";

    /// <summary>
    /// 实例身份：同一种 <see cref="EffectDef"/> 在图里可以出现多次，参数寻址
    /// （<c>effect:&lt;instance-id&gt;.param</c>）靠它区分「是哪一个」。
    /// 空 = 未分配（旧存档、以及测试里直接 new 的节点），由 <see cref="EffectGraph.Add"/> 发号、
    /// <see cref="EffectGraphJson.Deserialize"/> 给旧数据补号。
    /// </summary>
    public string InstanceId { get; init; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>数值/开关参数值。</summary>
    public Dictionary<string, float> Values { get; } = new();

    /// <summary>颜色参数值（<c>#rrggbb</c>）。</summary>
    public Dictionary<string, string> Colors { get; } = new();

    public float Value(EffectParam p) => Values.TryGetValue(p.Key, out var v) ? v : p.Default;

    public string ColorValue(EffectParam p) =>
        Colors.TryGetValue(p.Key, out var c) ? c : p.DefaultColor;

    /// <summary>设一个数值参数（顺手夹到 schema 的范围里）。</summary>
    public EffectNode Set(EffectParam p, float value)
    {
        Values[p.Key] = Math.Clamp(value, p.Min, p.Max);
        return this;
    }

    public EffectNode SetColor(EffectParam p, string hex)
    {
        Colors[p.Key] = hex;
        return this;
    }

    /// <summary>把参数解析成 <c>#rrggbb</c> 的 0..1 三元组。</summary>
    internal static (float R, float G, float B) ParseHex(string hex)
    {
        var s = (hex ?? "").TrimStart('#');
        if (s.Length == 3) s = $"{s[0]}{s[0]}{s[1]}{s[1]}{s[2]}{s[2]}";
        if (s.Length < 6) return (1f, 1f, 1f);
        return (Parse2(s, 0), Parse2(s, 2), Parse2(s, 4));

        static float Parse2(string s, int i) =>
            int.TryParse(s.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
                ? v / 255f
                : 1f;
    }
}
