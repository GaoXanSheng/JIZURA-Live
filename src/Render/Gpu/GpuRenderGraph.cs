using Vortice.Direct3D11;

namespace JizuraLive.Render.Gpu;

/// <summary>
/// 效果渲染图：一串带**多输入**与**多尺寸**的绘制。
///
/// 为什么需要它：线性单输入链表达不了两类关键效果——
///   * **mip 链 bloom**：亮部提取 → 逐级降采样 → 各级模糊 → 升采样相加 → 与原图合成。
///     这是有向无环图，不是链，而且每一级尺寸不同。
///   * **两输入合成 / 拖尾反馈**：一个 pass 同时读「原图」与「效果结果」（或上一帧）。
///
/// 输入编码：<see cref="External"/> 表示外部输入（引擎帧），<c>&gt;=0</c> 表示第 i 个 pass 的输出，
/// <see cref="None"/> 表示该槽位不绑。
/// </summary>
public sealed class GpuRenderGraph
{
    /// <summary>输入槽位：外部输入（引擎帧）。</summary>
    public const int External = -1;

    /// <summary>输入槽位：不绑。</summary>
    public const int None = -2;

    /// <summary>一次绘制：着色器 + 参数 + 两个输入槽 + 相对外部输入的尺寸比例。</summary>
    public readonly record struct Pass(
        ID3D11PixelShader Shader,
        float[] Constants,
        int Input0,
        int Input1,
        double Scale);

    private readonly List<Pass> _passes = new();

    public IReadOnlyList<Pass> Passes => _passes;

    public int Count => _passes.Count;

    /// <summary>加一个 pass，返回它的索引（可被后续 pass 当输入引用）。</summary>
    public int Add(ID3D11PixelShader shader, float[] constants,
        int input0 = External, int input1 = None, double scale = 1.0)
    {
        _passes.Add(new Pass(shader, constants, input0, input1, scale));
        return _passes.Count - 1;
    }

    public void Clear() => _passes.Clear();

    /// <summary>线性链的便捷写法：把一串单输入 pass 依次接起来（最后一个用外部输入之外的都自动串）。</summary>
    public void AddChain(IEnumerable<(ID3D11PixelShader Shader, float[] Constants)> chain)
    {
        var prev = External;
        foreach (var (shader, constants) in chain)
            prev = Add(shader, constants, prev);
    }
}
