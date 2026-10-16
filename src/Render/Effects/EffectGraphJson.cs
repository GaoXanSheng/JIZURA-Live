using System.IO;
using JizuraLive.Logging;
using Newtonsoft.Json;

namespace JizuraLive.Render.Effects;

/// <summary>
/// 效果图的持久化（JSON）。
///
/// 为什么参数按 **key** 存而不是按槽位：槽位是 HLSL 的实现细节，改一次着色器布局就会让所有存档失效；
/// 按 key 存则「改实现不动数据」。反序列化时按 schema 夹范围、丢弃未知效果（带告警）——
/// 存档来自旧版本或别的机器时不该整条链报废，能用的部分照用。
/// </summary>
public static class EffectGraphJson
{
    /// <summary>
    /// 当前存档版本（结构变化时递增，供将来做迁移）。
    /// 加 <c>InstanceId</c> 属于纯增量扩展，所以**不**抬版本号：旧读方忽略未知字段照样能读；
    /// 抬了反而会让旧读方把整份存档判成不认识的版本而整段拒绝。
    /// </summary>
    public const int CurrentVersion = 1;

    private sealed class NodeDto
    {
        public string DefId { get; set; } = "";
        public bool Enabled { get; set; } = true;

        /// <summary>实例 Id；空则不写（旧读方视野里这份 JSON 和从前没有区别）。</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? InstanceId { get; set; }

        public Dictionary<string, float>? Values { get; set; }
        public Dictionary<string, string>? Colors { get; set; }

        /// <summary>出现区间；全程生效的节点不写（旧读方视野里不变）。</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public List<EffectWindow>? Windows { get; set; }
    }

    private sealed class GraphDto
    {
        public int Version { get; set; } = CurrentVersion;
        public List<NodeDto> Nodes { get; set; } = new();
    }

    public static string Serialize(EffectGraph graph, bool indented = true)
    {
        var dto = new GraphDto
        {
            Version = CurrentVersion,
            Nodes = graph.Nodes.Select(n => new NodeDto
            {
                DefId = n.DefId,
                Enabled = n.Enabled,
                InstanceId = string.IsNullOrEmpty(n.InstanceId) ? null : n.InstanceId,
                Values = n.Values.Count > 0 ? new Dictionary<string, float>(n.Values) : null,
                Colors = n.Colors.Count > 0 ? new Dictionary<string, string>(n.Colors) : null,
                Windows = n.Windows.Count > 0 ? new List<EffectWindow>(n.Windows) : null,
            }).ToList(),
        };
        return JsonConvert.SerializeObject(dto, indented ? Formatting.Indented : Formatting.None);
    }

    public static EffectGraph Deserialize(string? json)
    {
        var graph = new EffectGraph();
        if (string.IsNullOrWhiteSpace(json)) return graph;

        GraphDto? dto;
        try
        {
            dto = JsonConvert.DeserializeObject<GraphDto>(json);
        }
        catch (Exception ex)
        {
            Log.Warn("[EffectGraph] 效果图 JSON 解析失败，按空图处理：" + ex.Message);
            return graph;
        }

        if (dto?.Nodes is null) return graph;

        foreach (var nd in dto.Nodes)
        {
            var def = EffectCatalog.Get(nd.DefId);
            if (def is null)
            {
                // 未知效果（旧存档/别的分支）只跳过这一个节点，不影响整条链
                Log.Warn($"[EffectGraph] 存档里含未知效果「{nd.DefId}」，已跳过");
                continue;
            }

            // 有 Id 原样保留；旧存档没有 Id 的节点在这里统一补号——补出来的号会被下一次 Serialize
            // 写进 JSON，所以补号结果本身可往返。补号就地做，不另开 public EnsureInstanceIds：
            // 调用方不该关心「这个节点有没有号」。
            // （InstanceId 是 init 属性，只能在对象初始化器里赋值，所以补号落在构造处，而不是循环末尾。）
            var node = new EffectNode
            {
                DefId = nd.DefId,
                Enabled = nd.Enabled,
                InstanceId = string.IsNullOrEmpty(nd.InstanceId)
                    ? EffectGraph.NewInstanceId(nd.DefId)
                    : nd.InstanceId,
            };
            if (nd.Values is not null)
            {
                foreach (var (key, value) in nd.Values)
                {
                    // 非有限值在这里就丢掉，绝不进常量缓冲：EffectNode.Set 走 Math.Clamp，
                    // 而 NaN 与上下界比较恒为 false、不会被夹住，会一路写进 GPU 常量。
                    // 不入 Values ⇒ EffectNode.Value 回落 schema 默认——与信号求值器「失效回落声明默认」是同一条规则。
                    if (!float.IsFinite(value))
                    {
                        Log.Warn($"[EffectGraph] 效果「{nd.DefId}」的参数 {key} 值非有限（{value}），已丢弃并按 schema 默认处理");
                        continue;
                    }

                    // 只接受 schema 里声明过的 key；范围由 Set 夹住
                    var p = def.Params.FirstOrDefault(x => x.Key == key);
                    if (p is not null) node.Set(p, value);
                }
            }

            if (nd.Colors is not null)
            {
                foreach (var (key, hex) in nd.Colors)
                {
                    var p = def.Params.FirstOrDefault(x => x.Key == key && x.Kind == EffectParamKind.Color);
                    if (p is not null) node.SetColor(p, hex);
                }
            }

            if (nd.Windows is not null)
            {
                foreach (var w in nd.Windows)
                {
                    // 坏区间（非有限 / 长度非正）只丢这一段：留着会让节点在任何时刻都不出现，比"没有这段"更难查
                    if (w.IsValid) node.Windows.Add(w);
                    else Log.Warn($"[EffectGraph] 效果「{nd.DefId}」含非法出现区间 {w}，已丢弃");
                }

                node.Windows.Sort((a, b) => a.Start.CompareTo(b.Start));
            }

            graph.Nodes.Add(node);
        }

        return graph;
    }

    public static void Save(EffectGraph graph, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, Serialize(graph));
    }

    public static EffectGraph Load(string path) =>
        File.Exists(path) ? Deserialize(File.ReadAllText(path)) : new EffectGraph();
}
