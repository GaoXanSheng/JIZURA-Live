using JizuraLive.Render.Gpu;

namespace JizuraLive.Render.Effects;

public sealed class EffectGraph
{
    public const int GlobalSlot = 15;

    public const int AudioSlot = 14;

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
                Stage = node.Stage,
                Blend = node.Blend,
                Opacity = node.Opacity,
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
        string target = "", float bass = 0, float mid = 0, float high = 0, float rms = 0,
        EffectStage stage = EffectStage.GlobalPost)
    {
        var graph = new GpuRenderGraph();
        var prev = GpuRenderGraph.External;

        foreach (var node in Nodes)
        {
            if (!node.Enabled || !node.FrameActive) continue;
            if (node.Stage != stage) continue;
            if (!string.Equals(node.Target, target, StringComparison.Ordinal)) continue;
            var def = EffectCatalog.Get(node.DefId);
            if (def is null) continue;

            var source = prev;
            var output = def.GraphBuild is not null
                ? def.GraphBuild(node, graph, library, prev, width, height)
                : graph.Add(library.GetPixel(def.Id, def.Hlsl),
                    PackConstants(def, node, time, width, height, bass, mid, high, rms), prev);

            if (node.Blend == EffectBlend.Normal && node.Opacity >= 0.999f)
            {
                prev = output;
                continue;
            }

            var blendConstants = new float[FullScreenPass.ParamSlots * 4];
            blendConstants[0] = (float)node.Blend;
            blendConstants[1] = Math.Clamp(node.Opacity, 0f, 1f);
            prev = graph.Add(library.GetPixel("fx.blend", ShaderSources.Blend),
                blendConstants, output, source);
        }

        foreach (var pass in graph.Passes)
            FillGlobals(pass.Constants, time, width, height, bass, mid, high, rms);
        return graph;
    }

    public bool HasEffectsFor(string target, EffectStage stage = EffectStage.GlobalPost)
    {
        foreach (var node in Nodes)
            if (node.Enabled && node.FrameActive
                && node.Stage == stage
                && string.Equals(node.Target, target, StringComparison.Ordinal)
                && EffectCatalog.Get(node.DefId) is not null)
                return true;
        return false;
    }

    public static float[] PackConstants(EffectDef def, EffectNode node, float time, int width, int height,
        float bass = 0, float mid = 0, float high = 0, float rms = 0)
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

        FillGlobals(c, time, width, height, bass, mid, high, rms);
        return c;
    }

    public static void FillGlobals(float[] c, float time, int width, int height,
        float bass = 0, float mid = 0, float high = 0, float rms = 0)
    {
        var gi = GlobalSlot * 4;
        if (gi + 3 >= c.Length) return;
        c[gi] = time;
        c[gi + 1] = width;
        c[gi + 2] = height;
        c[gi + 3] = height > 0 ? width / (float)height : 1f;

        var ai = AudioSlot * 4;
        if (ai + 3 >= c.Length) return;
        c[ai] = bass;
        c[ai + 1] = mid;
        c[ai + 2] = high;
        c[ai + 3] = rms;
    }
}
