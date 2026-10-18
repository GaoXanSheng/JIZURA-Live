using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace JizuraLive.Render.Gpu;

public sealed class FullScreenPass : IDisposable
{
    public const int ParamSlots = 16;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _ctx;
    private readonly ShaderLibrary _lib;
    private readonly ID3D11Buffer _constantBuffer;
    private readonly ID3D11SamplerState _sampler;
    private readonly float[] _scratch = new float[ParamSlots * 4];

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

    public void Run(ID3D11ShaderResourceView input0, ID3D11ShaderResourceView? input1,
        GpuTexturePool.Entry target, ID3D11PixelShader pixelShader, ReadOnlySpan<float> constants) =>
        Run(input0, input1, target.Rtv, target.Width, target.Height, pixelShader, constants);

    public void Run(ID3D11ShaderResourceView input0, ID3D11ShaderResourceView? input1,
        ID3D11RenderTargetView target, int width, int height, ID3D11PixelShader pixelShader,
        ReadOnlySpan<float> constants)
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

        _ctx.PSSetShaderResources(0, 2, NoViews);
        _ctx.OMSetRenderTargets(target, null);
        _ctx.RSSetViewport(0, 0, width, height, 0f, 1f);
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
