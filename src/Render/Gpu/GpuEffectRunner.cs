using Vortice.Direct3D11;
using Vortice.DXGI;

namespace JizuraLive.Render.Gpu;

public sealed class GpuEffectRunner : IDisposable
{
    private readonly ID3D11Device _device;

    private GpuTexturePool.Entry?[] _outputs = Array.Empty<GpuTexturePool.Entry?>();

    public ShaderLibrary Shaders { get; }
    public GpuTexturePool Pool { get; }
    public FullScreenPass Pass { get; }

    public GpuEffectRunner(D3DDevice gpu)
    {
        _device = gpu.Device;
        Shaders = new ShaderLibrary(gpu.Device);
        Pool = new GpuTexturePool(gpu.Device);
        Pass = new FullScreenPass(gpu.Device, gpu.Context, Shaders);
    }

    public GpuTexturePool.Entry RunGraph(GpuRenderGraph graph, ID3D11ShaderResourceView external,
        int width, int height)
    {
        if (graph.Count == 0)
            throw new ArgumentException("渲染图不能为空（至少一个 pass）", nameof(graph));

        var outputs = _outputs;
        if (outputs.Length < graph.Count) outputs = _outputs = new GpuTexturePool.Entry?[graph.Count];
        Array.Clear(outputs, 0, graph.Count);

        for (var i = 0; i < graph.Count; i++)
        {
            var p = graph.Passes[i];
            var tw = Math.Max(1, (int)Math.Round(width * p.Scale));
            var th = Math.Max(1, (int)Math.Round(height * p.Scale));
            var target = Pool.Rent(tw, th);

            var srv0 = Resolve(p.Input0, external, outputs, graph.Count);
            var srv1 = Resolve(p.Input1, external, outputs, graph.Count);
            if (srv0 is null)
                throw new InvalidOperationException($"pass {i} 的输入 0 为空（槽位 {p.Input0}）");

            Pass.Run(srv0, srv1, target, p.Shader, p.Constants);
            outputs[i] = target;
        }

        var last = outputs[graph.Count - 1]!;
        for (var i = 0; i < graph.Count - 1; i++)
            if (outputs[i] is not null) Pool.Return(outputs[i]!);
        return last;
    }

    private static ID3D11ShaderResourceView? Resolve(int slot, ID3D11ShaderResourceView external,
        GpuTexturePool.Entry?[] outputs, int count)
    {
        if (slot == GpuRenderGraph.None) return null;
        if (slot == GpuRenderGraph.External) return external;
        if (slot < 0 || slot >= count)
            throw new InvalidOperationException($"渲染图引用了非法输入槽位 {slot}");
        return outputs[slot]?.Srv
               ?? throw new InvalidOperationException($"渲染图引用了尚未产出的 pass {slot}");
    }

    public void Dispose()
    {
        Pass.Dispose();
        Pool.Dispose();
        Shaders.Dispose();
    }
}
