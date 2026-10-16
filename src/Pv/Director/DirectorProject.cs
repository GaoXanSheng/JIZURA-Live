using JizuraLive.Render.Effects;
using JizuraLive.Vts;
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
    /// <summary>
    /// 当前工程架构版本。读取时高于此值的文档直接拒绝打开（未来版本可能含当前代码不认识的语义）。
    ///
    /// 2 是信号图时代的版本号（曾为 VtsPose 节点种类抬过一次）。信号图已从本程序里删除：
    /// 旧文档里残留的 <c>signalGraph</c> 字段加载时被忽略（Json.NET 忽略未知字段），照常打开。
    /// 版本号**不回退到 1**——回退会让所有已存的 v2 工程被判成"版本过新"而打不开。
    /// </summary>
    public const int CurrentSchemaVersion = 2;

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
    /// VTS 演出 cue 轨（导演模块生成、播出时按歌曲时间触发）。加这个字段属于纯增量扩展，
    /// 旧工程没有它就加载为空轨（属性初始化器兜住缺字段），所以**不**抬 <see cref="SchemaVersion"/>。
    /// </summary>
    public VtsCueTrack VtsCues { get; set; } = new();

    /// <summary>
    /// 编辑器布局（画布视图与选择、时间轴视图）。
    ///
    /// 布局不是内容：改它不触发编译、不计 dirty，只在保存时附带落盘（契约 §3.1）。
    /// 它不引用 Scene/Effects 里的任何对象，所以读入缺字段、或字段坏了，都只回落默认视图。
    ///
    /// 必须 Replace：同 <see cref="Scene"/> 的理由（Auto 会把存档条目追加进已有实例）。
    /// </summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public EditorLayout EditorLayout { get; set; } = new();
}

/// <summary>
/// 编辑器布局：这一台机器上"上次看到哪儿"，不是文档内容——改它不计 dirty、不触发编译、不参与撤销语义（契约 §3.1）。
/// 时间轴三个字段先存不消费：多轨时间轴接入后再读，先存下来省得以后再加一次格式。
/// </summary>
public sealed class EditorLayout
{
    /// <summary>画布缩放（1 = 100%）。</summary>
    public double CanvasZoom { get; set; } = 1;

    /// <summary>画布横向平移（画布像素）。</summary>
    public double CanvasPanX { get; set; }

    /// <summary>画布纵向平移（画布像素）。</summary>
    public double CanvasPanY { get; set; }

    /// <summary>画布选中的节点 Id（顺序 = 选中顺序；已删节点的 Id 由画布自行忽略）。</summary>
    public List<string> SelectedNodeIds { get; set; } = new();

    /// <summary>时间轴横向缩放（1 = 默认密度）。</summary>
    public double TimelineZoom { get; set; } = 1;

    /// <summary>时间轴滚动位置（ticks，一秒 10,000,000 tick）。</summary>
    public long TimelineScrollTicks { get; set; }

    /// <summary>折叠的轨 Id（时间轴折叠态）。</summary>
    public List<string> CollapsedTrackIds { get; set; } = new();
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

    /// <summary>
    /// 调制层名。null / 空 = 基础曲线（同一地址至多一条）；非空 = 叠在基础值之上的调制层，
    /// 同一地址可以有多层（段落运镜是基础，节拍冲击是调制层，两者互不覆盖）。
    /// 空值不写进 JSON：基础曲线的存档形态与加字段之前完全一样。
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Layer { get; set; }

    /// <summary>调制层与下层的合成方式（基础曲线忽略此字段）。默认 Add 不写进 JSON。</summary>
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
    [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public CurveBlend Blend { get; set; } = CurveBlend.Add;
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
