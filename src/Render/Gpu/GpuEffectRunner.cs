using Vortice.Direct3D11;
using Vortice.DXGI;

namespace JizuraLive.Render.Gpu;

/// <summary>一张上传进来的只读纹理（效果机的输入）。</summary>
public sealed class GpuInputTexture : IDisposable
{
    public ID3D11Texture2D Texture { get; }
    public ID3D11ShaderResourceView Srv { get; }
    public int Width { get; }
    public int Height { get; }

    internal GpuInputTexture(ID3D11Texture2D texture, ID3D11ShaderResourceView srv, int w, int h)
    {
        Texture = texture;
        Srv = srv;
        Width = w;
        Height = h;
    }

    public void Dispose()
    {
        Srv.Dispose();
        Texture.Dispose();
    }
}

/// <summary>一次效果 pass：像素着色器 + 参数（float 平铺，见 <see cref="FullScreenPass"/>）。</summary>
public readonly record struct GpuPass(ID3D11PixelShader Shader, float[] Constants);

/// <summary>
/// 在**本设备**上打开的一块共享纹理 + 它的 SRV：效果链的输入入口。
/// </summary>
public sealed class GpuSharedSource : IDisposable
{
    public ID3D11Texture2D Texture { get; }
    public ID3D11ShaderResourceView Srv { get; }
    public int Width { get; }
    public int Height { get; }

    internal GpuSharedSource(ID3D11Texture2D texture, ID3D11ShaderResourceView srv, int w, int h)
    {
        Texture = texture;
        Srv = srv;
        Width = w;
        Height = h;
    }

    public void Dispose()
    {
        Srv.Dispose();
        Texture.Dispose();
    }
}

/// <summary>
/// 效果链执行器：把一串 <see cref="GpuPass"/> 依次跑在一张输入纹理上（ping-pong），返回最终面。
/// </summary>
public sealed class GpuEffectRunner : IDisposable
{
    private readonly ID3D11Device _device;

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

    /// <summary>
    /// 跑完一整条链。返回的 <see cref="GpuTexturePool.Entry"/> 归调用方所有，用完要
    /// <see cref="GpuTexturePool.Return"/>。中间面自动归还。
    /// </summary>
    public GpuTexturePool.Entry Run(ID3D11ShaderResourceView input, int width, int height,
        IReadOnlyList<GpuPass> passes)
    {
        if (passes.Count == 0)
            throw new ArgumentException("效果链不能为空（至少一个 pass）", nameof(passes));

        var prevSrv = input;
        GpuTexturePool.Entry? held = null;
        for (var i = 0; i < passes.Count; i++)
        {
            var target = Pool.Rent(width, height);
            Pass.Run(prevSrv, target, passes[i].Shader, passes[i].Constants);
            if (held is not null) Pool.Return(held);
            held = target;
            prevSrv = target.Srv;
        }

        return held!;
    }

    /// <summary>
    /// 跑一张渲染图（多输入 / 多尺寸 / 任意拓扑）。
    /// </summary>
    public GpuTexturePool.Entry RunGraph(GpuRenderGraph graph, ID3D11ShaderResourceView external,
        int width, int height)
    {
        if (graph.Count == 0)
            throw new ArgumentException("渲染图不能为空（至少一个 pass）", nameof(graph));

        var outputs = new GpuTexturePool.Entry?[graph.Count];
        for (var i = 0; i < graph.Count; i++)
        {
            var p = graph.Passes[i];
            var tw = Math.Max(1, (int)Math.Round(width * p.Scale));
            var th = Math.Max(1, (int)Math.Round(height * p.Scale));
            var target = Pool.Rent(tw, th);

            var srv0 = Resolve(p.Input0, external, outputs);
            var srv1 = Resolve(p.Input1, external, outputs);
            if (srv0 is null)
                throw new InvalidOperationException($"pass {i} 的输入 0 为空（槽位 {p.Input0}）");

            Pass.Run(srv0, srv1, target, p.Shader, p.Constants);
            outputs[i] = target;
        }

        var last = outputs[^1]!;
        for (var i = 0; i < outputs.Length - 1; i++)
            if (outputs[i] is not null) Pool.Return(outputs[i]!);
        return last;
    }

    private static ID3D11ShaderResourceView? Resolve(int slot, ID3D11ShaderResourceView external,
        GpuTexturePool.Entry?[] outputs)
    {
        if (slot == GpuRenderGraph.None) return null;
        if (slot == GpuRenderGraph.External) return external;
        if (slot < 0 || slot >= outputs.Length)
            throw new InvalidOperationException($"渲染图引用了非法输入槽位 {slot}");
        return outputs[slot]?.Srv
               ?? throw new InvalidOperationException($"渲染图引用了尚未产出的 pass {slot}");
    }

    /// <summary>
    /// 在共享纹理句柄上开一块 SRV 输入。用完 Dispose。
    /// </summary>
    public GpuSharedSource OpenShared(IntPtr shareHandle, int width, int height)
    {
        var texture = _device.OpenSharedResource<ID3D11Texture2D>(shareHandle);
        var srv = _device.CreateShaderResourceView(texture);
        return new GpuSharedSource(texture, srv, width, height);
    }

    /// <summary>
    /// 把 BGRA 图像数据上传成 GPU 纹理输入面。
    /// </summary>
    public GpuInputTexture Upload(ReadbackImage image) =>
        UploadBgra(image.BgraPixels, image.Width, image.Height);

    /// <summary>
    /// 把原始 BGRA8888 像素上传成只读纹理。
    /// </summary>
    public GpuInputTexture UploadBgra(byte[] bgraPixels, int width, int height)
    {
        unsafe
        {
            fixed (byte* ptr = bgraPixels)
            {
                var desc = new Texture2DDescription
                {
                    Width = (uint)width,
                    Height = (uint)height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1u, 0u),
                    Usage = ResourceUsage.Immutable,
                    BindFlags = BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.None,
                };
                var data = new SubresourceData((IntPtr)ptr, (uint)(width * 4));
                var texture = _device.CreateTexture2D(desc, data);
                var srv = _device.CreateShaderResourceView(texture);
                return new GpuInputTexture(texture, srv, width, height);
            }
        }
    }

    public void Dispose()
    {
        Pass.Dispose();
        Pool.Dispose();
        Shaders.Dispose();
    }
}
