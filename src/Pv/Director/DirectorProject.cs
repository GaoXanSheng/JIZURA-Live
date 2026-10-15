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
    /// 场景文档（画布尺寸 / fps / 图层栈的唯一真相）。
    ///
    /// 必须 Replace：Json.NET 默认的 ObjectCreationHandling.Auto 会复用属性上已有的实例
    /// （初始值里已有 5 个默认图层），再把存档条目追加进同一个 Layers，读回就成了 10 层。
    /// </summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public PvScene Scene { get; set; } = PvScene.CreateDefault();

    /// <summary>效果图（工程文件里是嵌套 JSON，见 <see cref="EffectGraphJsonConverter"/>）。</summary>
    [JsonConverter(typeof(EffectGraphJsonConverter))]
    public EffectGraph Effects { get; set; } = new();

    /// <summary>音频文件引用（null = 未绑定音源）。相对路径由调用方按工程目录解释，本层只存原文。</summary>
    public string? AudioPath { get; set; }

    /// <summary>已导入的 LRC 原文（null = 未导入歌词）。</summary>
    public string? LrcText { get; set; }
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
