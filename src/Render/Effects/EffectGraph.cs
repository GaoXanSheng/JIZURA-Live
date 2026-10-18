using JizuraLive.Render.Gpu;

namespace JizuraLive.Render.Effects;

public sealed class EffectGraph
{
    public const int GlobalSlot = 15;

    public List<EffectNode> Nodes { get; } = new();

    internal static string NewInstanceId(string defId) =>
        $"fx_{defId}_{Guid.NewGuid().ToString("N")[..6]}";

    public EffectGraph Clone()
    {
        var copy = new EffectGraph();
        foreach (var node in Nodes)
        {
            var clone = new EffectNode
            {
                DefId = node.DefId,
                InstanceId = node.InstanceId,
                Enabled = node.Enabled,
                Target = node.Target,
            };
            foreach (var (key, value) in node.Values) clone.Values[key] = value;
            foreach (var (key, color) in node.Colors) clone.Colors[key] = color;
            clone.Windows.AddRange(node.Windows);
            copy.Nodes.Add(clone);
        }

        return copy;
    }

    public GpuRenderGraph BuildRenderGraph(ShaderLibrary library, float time, int width, int height,
        string target = "")
    {
        var graph = new GpuRenderGraph();
        var prev = GpuRenderGraph.External;

        foreach (var node in Nodes)
        {
            if (!node.Enabled || !node.FrameActive) continue;
            if (!string.Equals(node.Target, target, StringComparison.Ordinal)) continue;
            var def = EffectCatalog.Get(node.DefId);
            if (def is null) continue;

            if (def.GraphBuild is not null)
            {
                prev = def.GraphBuild(node, graph, library, prev, width, height);
                continue;
            }

            graph.Add(library.GetPixel(def.Id, def.Hlsl),
                PackConstants(def, node, time, width, height), prev);
            prev = graph.Count - 1;
        }

        foreach (var pass in graph.Passes) FillGlobals(pass.Constants, time, width, height);
        return graph;
    }

    public bool HasEffectsFor(string target)
    {
        foreach (var node in Nodes)
            if (node.Enabled && node.FrameActive
                && string.Equals(node.Target, target, StringComparison.Ordinal)
                && EffectCatalog.Get(node.DefId) is not null)
                return true;
        return false;
    }

    public static float[] PackConstants(EffectDef def, EffectNode node, float time, int width, int height)
    {
        var c = new float[FullScreenPass.ParamSlots * 4];
        foreach (var p in def.Params)
        {
            var baseIndex = p.Slot * 4 + p.Comp;
            switch (p.Kind)
            {
                case EffectParamKind.Number:
                case EffectParamKind.Bool:
                    c[baseIndex] = node.Value(p);
                    break;
                case EffectParamKind.Color:
                    var (r, g, b) = EffectNode.ParseHex(node.ColorValue(p));
                    c[baseIndex] = r;
                    if (baseIndex + 1 < c.Length) c[baseIndex + 1] = g;
                    if (baseIndex + 2 < c.Length) c[baseIndex + 2] = b;
                    break;
            }
        }

        FillGlobals(c, time, width, height);
        return c;
    }

    public static void FillGlobals(float[] c, float time, int width, int height)
    {
        var gi = GlobalSlot * 4;
        if (gi + 3 >= c.Length) return;
        c[gi] = time;
        c[gi + 1] = width;
        c[gi + 2] = height;
        c[gi + 3] = height > 0 ? width / (float)height : 1f;
    }
}
