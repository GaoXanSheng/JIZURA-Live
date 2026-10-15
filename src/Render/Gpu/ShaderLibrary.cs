using JizuraLive.Logging;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace JizuraLive.Render.Gpu;

/// <summary>
/// HLSL 编译与着色器缓存。
///
/// 为什么是 D3DCompile 而不是离线编译：效果是**数据驱动**的（效果库里一条 EffectDef 对应一段 HLSL），
/// 运行时编译 + 缓存让「加一个效果」= 加一段 HLSL 字符串，不用改构建。编译失败带上 D3DCompiler 的
/// 错误文本抛出来——着色器语法错误必须一眼可见，不能静默退化成空效果。
///
/// 约定：所有像素着色器共享同一段前置（全屏三角形顶点着色器 + 参数常量缓冲 + 采样器），
/// 各自的入口都叫 <c>PSMain</c>。
/// </summary>
public sealed class ShaderLibrary : IDisposable
{
    /// <summary>像素着色器 profile。ps_4_0 足够（本机 feature level 11_1），且兼容面最广。</summary>
    private const string PixelProfile = "ps_4_0";

    private const string VertexProfile = "vs_4_0";

    /// <summary>
    /// 所有效果共用的前置：全屏三角形顶点着色器（靠 SV_VertexID 生成，无需顶点缓冲与输入布局）、
    /// 16 个 float4 的参数缓冲、线性夹取采样器。
    /// </summary>
    public const string Prelude = """
        cbuffer Params : register(b0) { float4 P[16]; };
        Texture2D  Tex  : register(t0);
        Texture2D  Tex1 : register(t1);   // second input: compositing / trails / bloom final
        SamplerState Smp : register(s0);

        struct VSOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

        // Fullscreen triangle: vertices (0,0) (2,0) (0,2), UV matches; no vertex buffer needed.
        VSOut VSMain(uint id : SV_VertexID)
        {
            VSOut o;
            float2 uv = float2((id << 1) & 2, id & 2);
            o.uv = uv;
            o.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
            return o;
        }

        // Convenience: read effect params by float4 slot.
        #define PARAM(slot, comp) P[slot].comp

        // Deterministic hashes (noise/dither): same time -> same frame, pixel for pixel.
        float Hash11(float p) { p = frac(p * 0.1031); p *= p + 33.33; p *= p + p; return frac(p); }
        float Hash21(float2 p) { float3 p3 = frac(float3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return frac((p3.x + p3.y) * p3.z); }
        float2 Hash22(float2 p) { float3 p3 = frac(float3(p.xyx) * float3(0.1031, 0.1030, 0.0973)); p3 += dot(p3, p3.yzx + 33.33); return frac((p3.xx + p3.yz) * p3.zy); }

        """;

    private readonly ID3D11Device _device;
    private readonly Dictionary<string, ID3D11PixelShader> _pixels = new();
    private ID3D11VertexShader? _fullscreenVs;

    public ShaderLibrary(ID3D11Device device) => _device = device;

    /// <summary>全屏三角形顶点着色器（全局唯一）。</summary>
    public ID3D11VertexShader FullscreenVertexShader =>
        _fullscreenVs ??= _device.CreateVertexShader(
            Compile(Prelude, "VSMain", VertexProfile, "fullscreen-vs"), null);

    /// <summary>按 key 取（并缓存）像素着色器；<paramref name="hlsl"/> 只需写 PSMain，前置自动加。</summary>
    public ID3D11PixelShader GetPixel(string key, string hlsl)
    {
        if (_pixels.TryGetValue(key, out var ps)) return ps;
        // 注意：C# 原始字符串不保留末尾换行，直接拼接会把前置的最后一行和着色器首行粘在一起
        ps = _device.CreatePixelShader(Compile(Prelude + "\n" + hlsl, "PSMain", PixelProfile, key), null);
        _pixels[key] = ps;
        return ps;
    }

    /// <summary>编译一段 HLSL；失败抛出带编译器错误文本的异常。</summary>
    public static byte[] Compile(string source, string entryPoint, string profile, string name)
    {
        try
        {
            // 注意参数顺序：Vortice 的友好重载是 (源码, 入口, 源名, profile, flags, effectFlags)
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
