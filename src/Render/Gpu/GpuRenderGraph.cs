using Vortice.Direct3D11;

namespace JizuraLive.Render.Gpu;

public sealed class GpuRenderGraph
{
    public const int External = -1;

    public const int None = -2;

    public readonly record struct Pass(
        ID3D11PixelShader Shader,
        float[] Constants,
        int Input0,
        int Input1,
        double Scale);

    private readonly List<Pass> _passes = new();

    public IReadOnlyList<Pass> Passes => _passes;

    public int Count => _passes.Count;

    public int Add(ID3D11PixelShader shader, float[] constants,
        int input0 = External, int input1 = None, double scale = 1.0)
    {
        _passes.Add(new Pass(shader, constants, input0, input1, scale));
        return _passes.Count - 1;
    }

    public void Clear() => _passes.Clear();

    public void AddChain(IEnumerable<(ID3D11PixelShader Shader, float[] Constants)> chain)
    {
        var prev = External;
        foreach (var (shader, constants) in chain)
            prev = Add(shader, constants, prev);
    }
}
