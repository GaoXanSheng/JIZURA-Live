using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace JizuraLive.Render.Gpu;

/// <summary>
/// 一次全屏像素着色器 pass：输入一张纹理，输出到一张渲染目标，参数走常量缓冲。
///
/// 为什么不用顶点缓冲：全屏三角形靠 <c>SV_VertexID</c> 在顶点着色器里算出来（(0,0)(2,0)(0,2)），
/// 于是不需要顶点缓冲、不需要输入布局、不需要索引缓冲——一次 <c>Draw(3,0)</c> 就是整屏。
/// 这是后处理链的标准做法，也是开销最小的。
///
/// 参数约定：与 <see cref="ShaderLibrary.Prelude"/> 的 <c>float4 P[16]</c> 对应，
/// 调用方按 float 平铺填 16 个槽位（不足补 0），着色器里用 <c>PARAM(slot, comp)</c> 取。
/// </summary>
public sealed class FullScreenPass : IDisposable
{
    /// <summary>参数槽位数（与 Prelude 的 P[16] 一致）。</summary>
    public const int ParamSlots = 16;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _ctx;
    private readonly ShaderLibrary _lib;
    private readonly ID3D11Buffer _constantBuffer;
    private readonly ID3D11SamplerState _sampler;
    private readonly float[] _scratch = new float[ParamSlots * 4];

    /// <summary>
    /// 「把槽 0/1 解绑」用的空 view 数组（数组元素默认即 null，语义 = 这两个槽位不绑任何 view）。
    ///
    /// 为什么不用两次 <c>PSSetShaderResource(0, null)</c>：Vortice 3.8.3 的单数重载
    /// <c>PSSetShaderResource(uint slot, ID3D11ShaderResourceView shaderResourceView)</c> 第二参数是
    /// **非空**引用类型，传 null 字面量会得到 CS8625（DOC/PV-ENGINE-ARCHITECTURE.md §5 记录的
    /// 两处现存 nullable 警告正是这里）；复数重载的形参是 <c>ID3D11ShaderResourceView[]</c>、
    /// 元素允许为 null，一次调用表达「0 号起连续 2 个槽位都清空」，与两次单数解绑等价。
    /// </summary>
    private static readonly ID3D11ShaderResourceView[] NoViews = new ID3D11ShaderResourceView[2];

    public FullScreenPass(ID3D11Device device, ID3D11DeviceContext ctx, ShaderLibrary lib)
    {
        _device = device;
        _ctx = ctx;
        _lib = lib;

        var size = (uint)(ParamSlots * 16);
        _constantBuffer = device.CreateBuffer(new BufferDescription(
            size, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write,
            ResourceOptionFlags.None, 0), (SubresourceData?)null);

        _sampler = device.CreateSamplerState(SamplerDescription.LinearClamp);
    }

    /// <summary>
    /// 跑一个 pass。<paramref name="constants"/> 按 float 平铺（最长 64 个），不足补 0。
    /// <paramref name="input1"/> 为 null 时只绑槽 0（线性链）；两输入效果（合成/拖尾/bloom 收尾）用它。
    /// </summary>
    public void Run(ID3D11ShaderResourceView input, GpuTexturePool.Entry target,
        ID3D11PixelShader pixelShader, ReadOnlySpan<float> constants) =>
        Run(input, null, target, pixelShader, constants);

    /// <summary>两输入版本。</summary>
    public void Run(ID3D11ShaderResourceView input0, ID3D11ShaderResourceView? input1,
        GpuTexturePool.Entry target, ID3D11PixelShader pixelShader, ReadOnlySpan<float> constants)
    {
        Array.Clear(_scratch);
        var n = Math.Min(constants.Length, _scratch.Length);
        for (var i = 0; i < n; i++) _scratch[i] = constants[i];

        var mapped = _ctx.Map(_constantBuffer, MapMode.WriteDiscard, MapFlags.None);
        Marshal.Copy(_scratch, 0, mapped.DataPointer, _scratch.Length);
        _ctx.Unmap(_constantBuffer);

        _ctx.IASetInputLayout(null);
        _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _ctx.VSSetShader(_lib.FullscreenVertexShader);
        _ctx.PSSetShader(pixelShader);
        _ctx.VSSetConstantBuffer(0, _constantBuffer);
        _ctx.PSSetConstantBuffer(0, _constantBuffer);
        _ctx.PSSetSampler(0, _sampler);

        // 绑定顺序很重要（踩过的坑）：上一段 pass 的输出面此刻还挂在 RTV 上；若先把**同一张纹理**
        // 绑成 SRV，D3D11 会判定「同一资源既是输入又是输出」而把 SRV 解绑——第二段起采样全 0，
        // 表现为「任何多段效果链都输出全透明」。所以：先解绑 SRV → 再换 RTV → 最后绑新输入。
        // 解绑本身不能省，只换表达方式（见 NoViews 的说明）。
        _ctx.PSSetShaderResources(0, 2, NoViews);
        _ctx.OMSetRenderTargets(target.Rtv, null);
        _ctx.RSSetViewport(0, 0, target.Width, target.Height, 0f, 1f);
        _ctx.PSSetShaderResource(0, input0);
        if (input1 is not null) _ctx.PSSetShaderResource(1, input1);

        _ctx.Draw(3, 0);
    }

    public void Dispose()
    {
        _sampler.Dispose();
        _constantBuffer.Dispose();
    }
}
