using JizuraLive.Logging;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace JizuraLive.Render.Gpu;

public sealed class ShaderLibrary : IDisposable
{
    private const string PixelProfile = "ps_4_0";

    private const string VertexProfile = "vs_4_0";

    public const string Prelude = """
        cbuffer Params : register(b0) { float4 P[16]; };
        Texture2D  Tex  : register(t0);
        Texture2D  Tex1 : register(t1);
        SamplerState Smp : register(s0);

        struct VSOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

        VSOut VSMain(uint id : SV_VertexID)
        {
            VSOut o;
            float2 uv = float2((id << 1) & 2, id & 2);
            o.uv = uv;
            o.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
            return o;
        }

        #define PARAM(slot, comp) P[slot].comp

        float Hash11(float p) { p = frac(p * 0.1031); p *= p + 33.33; p *= p + p; return frac(p); }
        float Hash21(float2 p) { float3 p3 = frac(float3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return frac((p3.x + p3.y) * p3.z); }
        float2 Hash22(float2 p) { float3 p3 = frac(float3(p.xyx) * float3(0.1031, 0.1030, 0.0973)); p3 += dot(p3, p3.yzx + 33.33); return frac((p3.xx + p3.yz) * p3.zy); }

        """;

    private readonly ID3D11Device _device;
    private readonly Dictionary<string, ID3D11PixelShader> _pixels = new();
    private ID3D11VertexShader? _fullscreenVs;

    public ShaderLibrary(ID3D11Device device) => _device = device;

    public ID3D11VertexShader FullscreenVertexShader =>
        _fullscreenVs ??= _device.CreateVertexShader(
            Compile(Prelude, "VSMain", VertexProfile, "fullscreen-vs"), null);

    public ID3D11PixelShader GetPixel(string key, string hlsl)
    {
        if (_pixels.TryGetValue(key, out var ps)) return ps;
        ps = _device.CreatePixelShader(Compile(Prelude + "\n" + hlsl, "PSMain", PixelProfile, key), null);
        _pixels[key] = ps;
        return ps;
    }

    public static byte[] Compile(string source, string entryPoint, string profile, string name)
    {
        try
        {
            return Compiler.Compile(source, entryPoint, name, profile,
                ShaderFlags.OptimizationLevel3, EffectFlags.None).ToArray();
        }
        catch (Exception ex)
        {
            Log.Error($"[ShaderLibrary] 编译 {name}（{entryPoint}/{profile}）失败：{ex.Message}");
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var ps in _pixels.Values) ps.Dispose();
        _pixels.Clear();
        _fullscreenVs?.Dispose();
        _fullscreenVs = null;
    }
}
