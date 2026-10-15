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
    /// <summary>当前存档版本（结构变化时递增，供将来做迁移）。</summary>
    public const int CurrentVersion = 1;

    private sealed class NodeDto
    {
        public string DefId { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public Dictionary<string, float>? Values { get; set; }
        public Dictionary<string, string>? Colors { get; set; }
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
                Values = n.Values.Count > 0 ? new Dictionary<string, float>(n.Values) : null,
                Colors = n.Colors.Count > 0 ? new Dictionary<string, string>(n.Colors) : null,
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

            var node = new EffectNode { DefId = nd.DefId, Enabled = nd.Enabled };
            if (nd.Values is not null)
            {
                foreach (var (key, value) in nd.Values)
                {
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
