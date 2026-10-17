using System.IO;
using JizuraLive.Logging;
using Newtonsoft.Json;

namespace JizuraLive.Render.Effects;

public static class EffectGraphJson
{
    public const int CurrentVersion = 1;

    private sealed class NodeDto
    {
        public string DefId { get; set; } = "";
        public bool Enabled { get; set; } = true;

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? InstanceId { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? Target { get; set; }

        public Dictionary<string, float>? Values { get; set; }
        public Dictionary<string, string>? Colors { get; set; }

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
                Target = string.IsNullOrEmpty(n.Target) ? null : n.Target,
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
                Log.Warn($"[EffectGraph] 存档里含未知效果「{nd.DefId}」，已跳过");
                continue;
            }

            var node = new EffectNode
            {
                DefId = nd.DefId,
                Enabled = nd.Enabled,
                InstanceId = string.IsNullOrEmpty(nd.InstanceId)
                    ? EffectGraph.NewInstanceId(nd.DefId)
                    : nd.InstanceId,
                Target = nd.Target ?? "",
            };
            if (nd.Values is not null)
            {
                foreach (var (key, value) in nd.Values)
                {
                    if (!float.IsFinite(value))
                    {
                        Log.Warn($"[EffectGraph] 效果「{nd.DefId}」的参数 {key} 值非有限（{value}），已丢弃并按 schema 默认处理");
                        continue;
                    }

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
