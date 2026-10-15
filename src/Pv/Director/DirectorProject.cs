using JizuraLive.Render.Effects;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JizuraLive.Pv;

/// <summary>
/// 单文件 PV 工程模型（.jzpv）：场景、效果图、音频引用与歌词原文放在一个文档里。
///
/// 画布宽高 / fps 的唯一真相在 <see cref="PvScene"/>（Scene.Width / Height / Fps），
/// 本类不再另存一份 canvas / fps 字段——同一事实存两处，迟早会在某次写入后分叉。
/// </summary>
public sealed class DirectorProject
{
    /// <summary>当前工程架构版本。读取时高于此值的文档直接拒绝打开（未来版本可能含当前代码不认识的语义）。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>工程架构版本（= <see cref="CurrentSchemaVersion"/>）。</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>工程名称（展示用，不参与渲染）。</summary>
    public string Name { get; set; } = "Untitled PV";

    /// <summary>
    /// 场景文档（画布尺寸 / fps / 图层栈的唯一真相）。默认**空白**（零图层）：工程是用户的画布，
    /// 不该预置一套模板图层替他决定画面结构。
    ///
    /// 必须 Replace：Json.NET 默认的 ObjectCreationHandling.Auto 会复用属性上已有的实例
    /// 再把存档条目追加进同一个 Layers，读回就会多出一份重复图层。
    /// </summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public PvScene Scene { get; set; } = PvScene.CreateBlank();

    /// <summary>效果图（工程文件里是嵌套 JSON，见 <see cref="EffectGraphJsonConverter"/>）。</summary>
    [JsonConverter(typeof(EffectGraphJsonConverter))]
    public EffectGraph Effects { get; set; } = new();

    /// <summary>音频文件引用（null = 未绑定音源）。相对路径由调用方按工程目录解释，本层只存原文。</summary>
    public string? AudioPath { get; set; }

    /// <summary>已导入的 LRC 原文（null = 未导入歌词）。</summary>
    public string? LrcText { get; set; }

    /// <summary>
    /// 关键帧曲线（按参数地址索引）。空列表 = 没有任何动画，所有参数取基础值。
    ///
    /// 加这个字段属于纯增量扩展，所以**不**抬 <see cref="SchemaVersion"/>：旧读方忽略未知字段照样能读
    /// （与 EffectGraphJson 加 InstanceId 同一个理由）。
    /// </summary>
    public List<CurveSave> Curves { get; set; } = new();

    /// <summary>
    /// 数值信号图（节点 / 边 / 绑定）。
    ///
    /// 两条规则写在这里，免得下次有人凭直觉改错其一：
    /// <list type="number">
    /// <item>加这个字段是**纯增量扩展**，不抬 <see cref="SchemaVersion"/>（同 <see cref="Curves"/> 的理由）：
    /// 旧读方不认识它，照样读得到场景、效果与曲线。</item>
    /// <item>但**新增节点种类**或**改属性名**属结构变化，必须抬 <see cref="CurrentSchemaVersion"/>：
    /// 旧版本对着一张语义不同的图"看起来能跑"（把新种类当未知、把改名的属性当默认）比整份拒绝打开危险得多。</item>
    /// </list>
    ///
    /// 必须 Replace：Json.NET 默认的 ObjectCreationHandling.Auto 会复用属性上已有的实例，
    /// 再把存档条目**追加**进同一个列表（同 <see cref="Scene"/> 的理由）。
    /// </summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public SignalGraph SignalGraph { get; set; } = new();
}

/// <summary>
/// 一条曲线的存档形态：地址文本 + 曲线本体。
///
/// 地址存字符串而不是结构体字段：JSON 里保持"一行看得懂"，也让加载时能对单条坏地址做治理
/// （解析失败只丢这一条，不炸整份工程——见 <see cref="ProjectStore"/>）。
/// </summary>
public sealed class CurveSave
{
    /// <summary>参数地址文本，形如 <c>camera.scale</c> / <c>effect:fx_glow_ab12.intensity</c>。</summary>
    public string Address { get; set; } = "";

    /// <summary>曲线本体。</summary>
    public FloatCurve Curve { get; set; } = new();
}

/// <summary>
/// 把 <see cref="EffectGraph"/> 的内嵌 JSON 读写交给 <see cref="EffectGraphJson"/>：
/// 工程文件里效果部分是嵌套的可读 JSON 对象，而不是一层转义字符串。
///
/// 转换器只搬运文本，不重复实现效果图的字段映射——效果存档的 schema 只有 EffectGraphJson 一份。
/// </summary>
public sealed class EffectGraphJsonConverter : JsonConverter<EffectGraph>
{
    public override EffectGraph? ReadJson(JsonReader reader, Type objectType, EffectGraph? existingValue,
        bool hasExistingValue, JsonSerializer serializer) =>
        reader.TokenType == JsonToken.Null
            // 存档里写 null 与 EffectGraphJson 的既有策略一致：按空图处理，不把 null 塞进非空属性
            ? EffectGraphJson.Deserialize(null)
            : EffectGraphJson.Deserialize(JToken.Load(reader).ToString());

    public override void WriteJson(JsonWriter writer, EffectGraph? value, JsonSerializer serializer)
    {
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteRawValue(EffectGraphJson.Serialize(value));
    }
}
