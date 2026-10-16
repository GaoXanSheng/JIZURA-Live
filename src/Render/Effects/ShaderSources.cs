namespace JizuraLive.Render.Effects;

/// <summary>
/// 效果库的 HLSL 源码（只写 <c>PSMain</c>；全屏三角形 VS、参数常量缓冲、采样器与噪声函数
/// 由 <see cref="Gpu.ShaderLibrary.Prelude"/> 统一前置）。
///
/// **参数约定**（与 C# 侧 <see cref="EffectParam.Slot"/>/<see cref="EffectParam.Comp"/> 严格一一对应）：
///   <c>P[0..14]</c> 归效果自己用；<c>P[15]</c> 是**全局保留槽**，由效果图统一填：
///   <c>P[15].x = 时间(秒)</c>、<c>P[15].y = 宽</c>、<c>P[15].z = 高</c>、<c>P[15].w = 宽高比</c>。
///   像素单位的参数（半径、位移量）在着色器里一律除以 <c>P[15].y</c> 归一化。
///
/// 纹理是 **BGRA8 预乘**：做颜色运算前先 <c>/ max(a,1e-4)</c> 解预乘，输出时再乘回 alpha，
/// 否则半透明区域会出现「越透明越暗」的偏色。
/// </summary>
public static class ShaderSources
{
    /// <summary>色差 RGB split。P0 = amount(px), cx, cy, radial(0/1)</summary>
    public const string Chromatic = """
        float4 PSMain(VSOut i) : SV_Target
        {
            float2 uv = i.uv;
            float2 dir = PARAM(0, w) > 0.5
                ? normalize(uv - float2(PARAM(0, y), PARAM(0, z)) + 1e-6)
                : float2(1, 0);
            float2 off = dir * PARAM(0, x) / max(PARAM(15, y), 1.0);
            float4 c = Tex.Sample(Smp, uv);
            float r = Tex.Sample(Smp, uv + off).r;
            float b = Tex.Sample(Smp, uv - off).b;
            return float4(r, c.g, b, c.a);
        }
        """;

    /// <summary>故障：整行块位移 + 横向色差，时间门控。P0 = amount(px), blockSize, seed, 保留</summary>
    public const string Glitch = """
        float4 PSMain(VSOut i) : SV_Target
        {
            float2 uv = i.uv;
            float t = PARAM(15, x);
            float blocks = max(PARAM(0, y), 4.0);
            float row = floor(uv.y * blocks);
            float seed = PARAM(0, z);
            float tick = floor(t * 9.0) + seed;
            float gate = step(0.72, Hash21(float2(row, tick)));
            float shift = (Hash21(float2(row * 1.7, tick)) - 0.5) * 2.0;
            float px = PARAM(0, x) / max(PARAM(15, y), 1.0);
            uv.x += gate * shift * px;
            float4 c = Tex.Sample(Smp, uv);
            float2 off = float2(px * 0.5 * gate, 0);
            float r = Tex.Sample(Smp, uv + off).r;
            float b = Tex.Sample(Smp, uv - off).b;
            return float4(r, c.g, b, c.a);
        }
        """;

    /// <summary>扫描线 + 缓慢回扫亮带。P0 = 密度, 强度, 滚动速度, 保留</summary>
    public const string Scanline = """
        float4 PSMain(VSOut i) : SV_Target
        {
            float4 c = Tex.Sample(Smp, i.uv);
            float t = PARAM(15, x) * PARAM(0, z);
            float lines = max(PARAM(0, x), 1.0);
            float s = 0.5 + 0.5 * sin((i.uv.y * lines + t) * 6.2831853);
            c.rgb *= 1.0 - PARAM(0, y) * s;
            float band = smoothstep(0.0, 0.05, frac(i.uv.y - t * 0.1));
            c.rgb *= 0.92 + 0.08 * band;
            return c;
        }
        """;

    /// <summary>胶片颗粒。P0 = 强度, 尺寸(px), 彩色(0/1), 保留</summary>
    public const string Grain = """
        float4 PSMain(VSOut i) : SV_Target
        {
            float4 c = Tex.Sample(Smp, i.uv);
            float2 res = max(PARAM(15, yz), 1.0);
            float2 cell = floor(i.uv * res / max(PARAM(0, y), 0.5));
            float t = floor(PARAM(15, x) * 24.0);
            float n = Hash21(cell + t * 17.0);
            float3 g = PARAM(0, z) > 0.5
                ? float3(n, Hash21(cell + t * 17.0 + 3.7), Hash21(cell + t * 17.0 + 9.1)) - 0.5
                : (n - 0.5).xxx;
            float3 rgb = c.rgb / max(c.a, 1e-4);
            float lum = dot(rgb, float3(0.299, 0.587, 0.114));
            rgb += g * PARAM(0, x) * (0.35 + 0.65 * lum);
            return float4(saturate(rgb) * c.a, c.a);
        }
        """;

    /// <summary>暗角。P0 = 强度, 平滑度, 圆度, 中心y偏移</summary>
    public const string Vignette = """
        float4 PSMain(VSOut i) : SV_Target
        {
            float4 c = Tex.Sample(Smp, i.uv);
            float2 p = i.uv - float2(0.5, 0.5 + PARAM(0, w));
            p.x *= lerp(1.0, max(PARAM(15, w), 0.01), 1.0 - PARAM(0, z));
            float d = length(p) * 1.4142;
            float v = smoothstep(1.0, max(1.0 - PARAM(0, y), 0.0), d);
            c.rgb *= lerp(1.0, v, PARAM(0, x));
            return c;
        }
        """;

    /// <summary>调色。P0 = 饱和度, 对比度, 亮度, 强度；P1 = tint.rgb, tintStrength</summary>
    public const string ColorGrade = """
        float4 PSMain(VSOut i) : SV_Target
        {
            float4 c = Tex.Sample(Smp, i.uv);
            float3 src = c.rgb / max(c.a, 1e-4);
            float3 rgb = src;
            float lum = dot(rgb, float3(0.299, 0.587, 0.114));
            rgb = lerp(lum.xxx, rgb, PARAM(0, x));
            rgb = (rgb - 0.5) * PARAM(0, y) + 0.5 + PARAM(0, z);
            rgb = lerp(rgb, rgb * (PARAM(1, xyz) * 2.0), PARAM(1, w));
            rgb = lerp(src, rgb, PARAM(0, w));
            return float4(saturate(rgb) * c.a, c.a);
        }
        """;

    /// <summary>半调网点。P0 = 点径(px), 角度(度), 强度, 灰度(0/1)</summary>
    public const string Halftone = """
        float4 PSMain(VSOut i) : SV_Target
        {
            float4 c = Tex.Sample(Smp, i.uv);
            float3 rgb = c.rgb / max(c.a, 1e-4);
            float lum = dot(rgb, float3(0.299, 0.587, 0.114));
            float a = radians(PARAM(0, y));
            float2 px = i.uv * max(PARAM(15, yz), 1.0);
            float2 rp = float2(px.x * cos(a) - px.y * sin(a), px.x * sin(a) + px.y * cos(a));
            float2 cell = frac(rp / max(PARAM(0, x), 1.0)) - 0.5;
            float d = length(cell);
            float dotv = smoothstep(0.5, 0.42, d / max(1.0 - lum, 1e-3));
            float3 graded = PARAM(0, w) > 0.5 ? dotv.xxx : rgb * dotv;
            rgb = lerp(rgb, graded, PARAM(0, z));
            return float4(saturate(rgb) * c.a, c.a);
        }
        """;

    /// <summary>液态（域扭曲噪声位移）。P0 = 强度(px), 尺度, 速度, 保留</summary>
    public const string Liquid = """
        float2 Fbm2(float2 p)
        {
            float2 s = 0;
            float2 amp = 1;
            for (int k = 0; k < 3; k++) { s += amp * (Hash22(p).xy * 2.0 - 1.0); p *= 2.02; amp *= 0.5; }
            return s;
        }
        float4 PSMain(VSOut i) : SV_Target
        {
            float2 uv = i.uv;
            float2 q = uv * max(PARAM(0, y), 0.1);
            q += PARAM(15, x) * PARAM(0, z);
            float2 d = Fbm2(q);
            uv += d * PARAM(0, x) / max(PARAM(15, yz), 1.0);
            return Tex.Sample(Smp, uv);
        }
        """;

    /// <summary>柔光泛光（单 pass 近似）。P0 = 阈值, 强度, 半径(px), 保留</summary>
    public const string Glow = """
        float4 PSMain(VSOut i) : SV_Target
        {
            float4 base = Tex.Sample(Smp, i.uv);
            float3 rgb = base.rgb / max(base.a, 1e-4);
            float radius = max(PARAM(0, z), 0.5) / max(PARAM(15, y), 1.0);
            float3 acc = 0;
            for (int k = 0; k < 16; k++)
            {
                float ang = 6.2831853 * k / 16.0;
                float2 off = float2(cos(ang), sin(ang)) * radius * (0.4 + 0.6 * frac(k * 0.618));
                float4 s = Tex.Sample(Smp, i.uv + off);
                float3 srgb = s.rgb / max(s.a, 1e-4);
                float l = dot(srgb, float3(0.299, 0.587, 0.114));
                acc += srgb * max(l - PARAM(0, x), 0.0);
            }
            // Divide by the sample count (sum, not weighted average): when bright pixels are a
            // small share of the neighbourhood the result decays naturally. A weighted average
            // would return nearly the original brightness for "one bright pixel + dark ring",
            // which added back becomes a full white wash.
            acc /= 16.0;
            rgb += acc * PARAM(0, y);
            return float4(saturate(rgb) * base.a, base.a);
        }
        """;

    /// <summary>量化 + Bayer 抖动。P0 = 级数, 抖动强度, 强度, 保留</summary>
    public const string Posterize = """
        float Bayer4(float2 p)
        {
            int2 i = int2(fmod(p, 4.0));
            static const float m[16] = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };
            return m[i.y * 4 + i.x] / 16.0;
        }
        float4 PSMain(VSOut i) : SV_Target
        {
            float4 c = Tex.Sample(Smp, i.uv);
            float3 rgb = c.rgb / max(c.a, 1e-4);
            float lv = max(PARAM(0, x), 2.0);
            float d = (Bayer4(i.uv * max(PARAM(15, yz), 1.0)) - 0.5) * PARAM(0, y) / lv;
            float3 q = floor((rgb + d) * lv + 0.5) / lv;
            q = lerp(rgb, q, PARAM(0, z));
            return float4(saturate(q) * c.a, c.a);
        }
        """;

    // ------------------------------------------------------------------
    // 旧引擎 8 个内置 FX 的 GPU 对位（invert / flash / zoom / mosaic / slice / block / shake；
    // 第 8 个 chroma 由上面的 Chromatic 覆盖）。它们原来硬编码在 Renderer.FrameCore 的 switch 里，
    // 现在成为效果图上的普通节点，可分类、可调参、可叠加。
    // ------------------------------------------------------------------

    /// <summary>反相（对位内置 invert）。P0 = 强度</summary>
    public const string Invert = """
        float4 PSMain(VSOut i) : SV_Target
        {
            float4 c = Tex.Sample(Smp, i.uv);
            float3 src = c.rgb / max(c.a, 1e-4);
            float3 rgb = lerp(src, 1.0 - src, PARAM(0, x));
            return float4(saturate(rgb) * c.a, c.a);
        }
        """;

    /// <summary>闪白/闪色（对位内置 flash）。P0 = 强度；P1 = 颜色 rgb</summary>
    public const string Flash = """
        float4 PSMain(VSOut i) : SV_Target
        {
            float4 c = Tex.Sample(Smp, i.uv);
            float3 rgb = c.rgb / max(c.a, 1e-4);
            rgb = lerp(rgb, PARAM(1, xyz), PARAM(0, x));
            return float4(saturate(rgb) * c.a, c.a);
        }
        """;

    /// <summary>缩放冲击（对位内置 zoom）。P0 = 缩放, 中心x, 中心y, 保留</summary>
    public const string ZoomPunch = """
        float4 PSMain(VSOut i) : SV_Target
        {
            float2 ctr = float2(PARAM(0, y), PARAM(0, z));
            float2 uv = (i.uv - ctr) / max(PARAM(0, x), 1e-3) + ctr;
            return Tex.Sample(Smp, uv);
        }
        """;

    /// <summary>马赛克（对位内置 mosaic）。P0 = 块大小(px)</summary>
    public const string Mosaic = """
        float4 PSMain(VSOut i) : SV_Target
        {
            float2 res = max(PARAM(15, yz), 1.0);
            float block = max(PARAM(0, x), 1.0);
            float2 px = floor(i.uv * res / block) * block + block * 0.5;
            return Tex.Sample(Smp, px / res);
        }
        """;

    /// <summary>横向切片位移（对位内置 slice，确定性、非时间门控）。P0 = 条数, 位移(px), 种子</summary>
    public const string Slice = """
        float4 PSMain(VSOut i) : SV_Target
        {
            float bands = max(PARAM(0, x), 1.0);
            float row = floor(i.uv.y * bands);
            float sh = (Hash21(float2(row, PARAM(0, z))) - 0.5) * 2.0
                     * PARAM(0, y) / max(PARAM(15, y), 1.0);
            return Tex.Sample(Smp, i.uv + float2(sh, 0));
        }
        """;

    /// <summary>块位移（对位内置 block）。P0 = 块大小(px), 位移(px), 种子</summary>
    public const string Block = """
        float4 PSMain(VSOut i) : SV_Target
        {
            float2 res = max(PARAM(15, yz), 1.0);
            float2 cell = floor(i.uv * res / max(PARAM(0, x), 1.0));
            float2 r = Hash22(cell + PARAM(0, z));
            float2 sh = (r - 0.5) * 2.0 * PARAM(0, y) / res;
            return Tex.Sample(Smp, i.uv + sh);
        }
        """;

    /// <summary>整体抖动（对位内置 shake）。P0 = 幅度(px), 频率</summary>
    public const string Shake = """
        float4 PSMain(VSOut i) : SV_Target
        {
            float2 res = max(PARAM(15, yz), 1.0);
            float t = floor(PARAM(15, x) * max(PARAM(0, y), 0.1) * 8.0);
            float2 sh = float2(Hash11(t) - 0.5, Hash11(t + 17.0) - 0.5)
                      * 2.0 * PARAM(0, x) / res;
            return Tex.Sample(Smp, i.uv + sh);
        }
        """;

    // ------------------------------------------------------------------
    // 追加效果库（研究里点名的热门 PV 风格）
    // ------------------------------------------------------------------

    /// <summary>VHS 磁带：逐行抖动 + 色度偏移 + 跟踪带。P0 = 抖动(px), 色度偏移(px), 跟踪强度, 保留</summary>
    public const string Vhs = """
        float4 PSMain(VSOut i) : SV_Target
        {
            float t = PARAM(15, x);
            float2 res = max(PARAM(15, yz), 1.0);
            float2 uv = i.uv;
            float row = floor(uv.y * res.y);   // note: 'line' is a reserved word in HLSL
            float tick = floor(t * 12.0);
            // per-scanline warble
            uv.x += (Hash21(float2(row, tick)) - 0.5) * 2.0 * PARAM(0, x) / res.x;
            // tracking band: a slowly travelling strip of strong displacement
            float band = 1.0 - smoothstep(0.0, 0.05, abs(frac(uv.y - t * 0.07) - 0.5) * 2.0);
            uv.x += band * PARAM(0, z) * (Hash21(float2(row, 7.0)) - 0.5) * 2.0 * 20.0 / res.x;
            float2 cs = float2(PARAM(0, y) / res.x, 0);
            float4 c = Tex.Sample(Smp, uv);
            float r = Tex.Sample(Smp, uv + cs).r;
            float b = Tex.Sample(Smp, uv - cs).b;
            return float4(r, c.g, b, c.a);
        }
        """;

    /// <summary>径向光线（从亮点向外拉出）。P0 = 长度(0..0.5), 强度, 中心x, 中心y</summary>
    public const string LightRays = """
        float4 PSMain(VSOut i) : SV_Target
        {
            float4 base = Tex.Sample(Smp, i.uv);
            float2 ctr = float2(PARAM(0, z), PARAM(0, w));
            float2 step2 = (ctr - i.uv) * PARAM(0, x) / 16.0;
            float2 uv = i.uv;
            float3 acc = 0;
            for (int k = 0; k < 16; k++)
            {
                uv += step2;
                float4 s = Tex.Sample(Smp, uv);
                float3 srgb = s.rgb / max(s.a, 1e-4);
                float l = dot(srgb, float3(0.299, 0.587, 0.114));
                acc += srgb * max(l - 0.5, 0.0);
            }
            acc /= 16.0;
            float3 rgb = base.rgb / max(base.a, 1e-4) + acc * PARAM(0, y);
            return float4(saturate(rgb) * base.a, base.a);
        }
        """;

    /// <summary>玻璃破碎（Voronoi 分块位移 + 裂纹暗边）。P0 = 块数, 位移(px), 裂纹强度, 种子</summary>
    public const string GlassShatter = """
        float2 Voronoi(float2 p, out float edge)
        {
            float2 n = floor(p);
            float2 f = frac(p);
            float d1 = 8.0;
            float d2 = 8.0;
            for (int y = -1; y <= 1; y++)
            {
                for (int x = -1; x <= 1; x++)
                {
                    float2 g = float2(x, y);
                    float2 o = Hash22(n + g);
                    float d = length(g + o - f);
                    if (d < d1) { d2 = d1; d1 = d; }
                    else if (d < d2) { d2 = d; }
                }
            }
            edge = d2 - d1;
            return float2(d1, d2);
        }
        float4 PSMain(VSOut i) : SV_Target
        {
            float2 res = max(PARAM(15, yz), 1.0);
            float seed = PARAM(0, w);
            float2 p = i.uv * max(PARAM(0, x), 2.0) + seed;
            float edge;
            Voronoi(p, edge);
            float2 r = Hash22(floor(p) + seed) - 0.5;
            float2 sh = r * 2.0 * PARAM(0, y) / res;
            float4 c = Tex.Sample(Smp, i.uv + sh);
            float3 rgb = c.rgb / max(c.a, 1e-4);
            rgb *= lerp(1.0, smoothstep(0.0, 0.07, edge), PARAM(0, z));
            return float4(rgb * c.a, c.a);
        }
        """;

    /// <summary>波浪扭曲。P0 = 幅度(px), 波长(px), 速度, 方向(0=横波/1=纵波)</summary>
    public const string Wave = """
        float4 PSMain(VSOut i) : SV_Target
        {
            float2 res = max(PARAM(15, yz), 1.0);
            float t = PARAM(15, x) * PARAM(0, z);
            float amp = PARAM(0, x) / res.y;
            float wl = max(PARAM(0, y), 1.0) / res.y;
            float2 uv = i.uv;
            if (PARAM(0, w) > 0.5)
                uv.x += sin((uv.y / wl) * 6.2831853 + t) * amp;
            else
                uv.y += sin((uv.x * max(PARAM(15, w), 1.0) / wl) * 6.2831853 + t) * amp;
            return Tex.Sample(Smp, uv);
        }
        """;

    // ------------------------------------------------------------------
    // 多 pass 效果的构件（由 GpuRenderGraph 编排；不是独立可选的"效果"）
    // ------------------------------------------------------------------

    /// <summary>亮部提取（bloom 第一级）。P0 = 阈值</summary>
    public const string BloomBright = """
        float4 PSMain(VSOut i) : SV_Target
        {
            float4 c = Tex.Sample(Smp, i.uv);
            float3 rgb = c.rgb / max(c.a, 1e-4);
            float l = dot(rgb, float3(0.299, 0.587, 0.114));
            float k = smoothstep(PARAM(0, x), 1.0, l);
            return float4(rgb * k * c.a, c.a);
        }
        """;

    /// <summary>可分离高斯·横向。P0 = 半径(px，按外部输入分辨率计)</summary>
    public const string BlurH = """
        float4 PSMain(VSOut i) : SV_Target
        {
            float r = PARAM(0, x) / max(PARAM(15, y), 1.0);
            float4 acc = 0;
            float wsum = 0;
            for (int k = -4; k <= 4; k++)
            {
                float w = exp(-0.5 * (k / 2.0) * (k / 2.0));
                acc += Tex.Sample(Smp, i.uv + float2(r * k * 0.25, 0)) * w;
                wsum += w;
            }
            return acc / wsum;
        }
        """;

    /// <summary>可分离高斯·纵向（按宽高比换算，保证各向同性）。P0 = 半径(px)</summary>
    public const string BlurV = """
        float4 PSMain(VSOut i) : SV_Target
        {
            float r = PARAM(0, x) / max(PARAM(15, y), 1.0) * max(PARAM(15, w), 0.01);
            float4 acc = 0;
            float wsum = 0;
            for (int k = -4; k <= 4; k++)
            {
                float w = exp(-0.5 * (k / 2.0) * (k / 2.0));
                acc += Tex.Sample(Smp, i.uv + float2(0, r * k * 0.25)) * w;
                wsum += w;
            }
            return acc / wsum;
        }
        """;

    /// <summary>bloom 收尾：原图 + 泛光。Tex = 原图，Tex1 = 泛光。P0 = 强度</summary>
    public const string BloomComposite = """
        float4 PSMain(VSOut i) : SV_Target
        {
            float4 base = Tex.Sample(Smp, i.uv);
            float4 bloom = Tex1.Sample(Smp, i.uv);
            float3 rgb = base.rgb / max(base.a, 1e-4)
                       + (bloom.rgb / max(bloom.a, 1e-4)) * PARAM(0, x);
            return float4(saturate(rgb) * base.a, base.a);
        }
        """;

    /// <summary>定向运动模糊（单 pass，沿给定方向多抽样）。P0 = 长度(px), 角度(度), 强度</summary>
    public const string MotionBlur = """
        float4 PSMain(VSOut i) : SV_Target
        {
            float a = radians(PARAM(0, y));
            float2 dir = float2(cos(a), sin(a)) * PARAM(0, x) / max(PARAM(15, y), 1.0);
            float4 acc = 0;
            for (int k = 0; k < 12; k++)
            {
                float t = k / 11.0 - 0.5;
                acc += Tex.Sample(Smp, i.uv + dir * t);
            }
            acc /= 12.0;
            return lerp(Tex.Sample(Smp, i.uv), acc, PARAM(0, z));
        }
        """;

    // ------------------------------------------------------------------
    // 漫画 / 二次元语汇（集中线、色块转场、分格、双色调、电影黑边）
    // ------------------------------------------------------------------

    /// <summary>集中线（径向/横向速度线）。P0 = 强度, 线数, 留空半径, 线宽；P1 = 每秒重随机次数, 横向；P2 = 线色 rgb</summary>
    public const string SpeedLines = """
        float4 PSMain(VSOut i) : SV_Target
        {
            float4 c = Tex.Sample(Smp, i.uv);
            float amount = saturate(PARAM(0, x));
            if (amount <= 0.0) return c;

            float lines = max(PARAM(0, y), 4.0);
            float inner = max(PARAM(0, z), 0.0);
            float thick = max(PARAM(0, w), 0.05);
            float aspect = max(PARAM(15, w), 0.01);

            // working space: y-units (short side half = 0.5), so 'inner' is a share of the short side
            float2 p = (i.uv - 0.5) * float2(aspect, 1.0);
            float2 he = float2(0.5 * aspect, 0.5);
            float dist = length(p);
            float innerR = inner * 0.5;
            // seed = line index + floor(time * speed): the lines re-roll their length in steps,
            // which is what makes the pattern read as hand-drawn on threes rather than as noise
            float tick = floor(PARAM(15, x) * max(PARAM(1, x), 0.0));

            float m;
            if (PARAM(1, y) > 0.5)
            {
                // horizontal: streaks run in from the left / right edge, one pair per band
                float bands = max(lines * 0.5, 2.0);
                float band = (p.y + he.y) / (2.0 * he.y) * bands;
                float bidx = floor(band);
                float rk = Hash21(float2(bidx, tick + (p.x < 0.0 ? 0.0 : 0.37)));
                float startX = he.x * (1.0 - lerp(0.45, 1.0, rk));
                float halfW = 0.22 * thick;
                m = smoothstep(startX, startX + 0.05, abs(p.x));
                m *= 1.0 - smoothstep(halfW * 0.5, halfW, abs(band - bidx - 0.5));
            }
            else
            {
                // radial: wedges from the frame edge pointing at the centre
                float ang = atan2(p.y, p.x);
                float2 dirv = float2(cos(ang), sin(ang));
                float edge = min(he.x / max(abs(dirv.x), 1e-4), he.y / max(abs(dirv.y), 1e-4));

                float sector = (ang + 3.14159265) / 6.2831853 * lines;
                float idx = floor(sector);
                float rk = Hash21(float2(idx, idx + tick));
                float startR = max(innerR, edge * (1.0 - lerp(0.45, 1.0, rk)));

                float halfA = 0.125 * thick;
                m = smoothstep(startR, startR + 0.05, dist);
                m *= 1.0 - smoothstep(halfA * 0.5, halfA, abs(sector - idx - 0.5));
            }

            m *= smoothstep(innerR, innerR + 0.08, dist);

            float3 src = c.rgb / max(c.a, 1e-4);
            float3 rgb = lerp(src, PARAM(2, xyz), saturate(m * amount));
            return float4(saturate(rgb) * c.a, c.a);
        }
        """;

    /// <summary>色块转场 / 圆形光圈。P0 = 进度, 角度(度), 柔边, 光圈(0/1)；P1 = 色块 rgb, 前沿带宽；P2 = 前沿色 rgb</summary>
    public const string Wipe = """
        float4 PSMain(VSOut i) : SV_Target
        {
            float p = saturate(PARAM(0, x));
            float4 c = Tex.Sample(Smp, i.uv);
            if (p <= 0.0 || p >= 1.0) return c;

            float soft = max(PARAM(0, z), 1e-4);
            float band = max(PARAM(1, w), 0.0);
            // u ramps 0 -> 1 inside each half; only the mask direction flips at the midpoint,
            // so the cover sweeps in from one side and back out to the other
            float u = p <= 0.5 ? p * 2.0 : (p - 0.5) * 2.0;

            float m;
            float edgeDist;
            if (PARAM(0, w) > 0.5)
            {
                float rMax = 0.7071068 + soft + band + 1e-3;
                // the disc radius is the mask axis here, so it has to turn round at the midpoint
                // exactly like the linear front below; otherwise the second half closes in again.
                // r: edge -> -2soft (the cover sweeps in), then -2soft -> edge (it opens from the centre)
                float r = p <= 0.5 ? lerp(rMax, -2.0 * soft, u) : lerp(-2.0 * soft, rMax, u);
                float d = length(i.uv - 0.5);
                m = smoothstep(r - soft, r + soft, d);
                edgeDist = abs(d - r);
            }
            else
            {
                float a = radians(PARAM(0, y));
                float2 dir = float2(cos(a), sin(a));
                float reach = 0.5 * (abs(dir.x) + abs(dir.y));
                float front = lerp(-(reach + soft + band + 1e-3), reach + soft + band + 1e-3, u);
                float axis = dot(i.uv - 0.5, dir);
                float s = smoothstep(front - soft, front + soft, axis);
                m = p <= 0.5 ? 1.0 - s : s;
                edgeDist = abs(axis - front);
            }

            float edge = 1.0 - smoothstep(0.0, max(band, 1e-4), edgeDist);
            float3 cover = lerp(PARAM(1, xyz), PARAM(2, xyz), edge);
            float3 src = c.rgb / max(c.a, 1e-4);
            float3 rgb = lerp(src, cover, m);
            return float4(saturate(rgb) * c.a, c.a);
        }
        """;

    /// <summary>漫画分格。P0 = 强度, 格数, 分隔线倾角(度), 边框宽；P1 = 边框色 rgb, 放大；P2 = 焦点 y</summary>
    public const string MangaPanels = """
        float4 PSMain(VSOut i) : SV_Target
        {
            float amount = saturate(PARAM(0, x));
            float4 orig = Tex.Sample(Smp, i.uv);
            if (amount <= 0.0) return orig;

            float2 res = max(PARAM(15, yz), 1.0);
            float n = clamp(floor(PARAM(0, y) + 0.5), 2.0, 3.0);
            float zoom = max(PARAM(1, w), 1.0);
            float focusY = saturate(PARAM(2, x));
            float borderPx = max(PARAM(0, w), 0.0) * min(res.x, res.y);

            float2 px = i.uv * res;
            float pw = res.x / n;
            // shear in pixel space: separators stay straight but tilt with the frame height
            float xs = px.x - (px.y - res.y * 0.5) * tan(radians(PARAM(0, z)));
            float slide = (1.0 - amount) * res.x;

            // panels arrive from the sides: odd ones from the right, the rest from the left.
            // Overlapping in-transit panels are painted in order, so a later panel wins.
            float4 outC = orig;
            for (int j = 0; j < 3; j++)
            {
                float k = float(j);
                if (k < n)
                {
                    float dx = ((j == 1) ? slide : -slide);
                    float lx = xs - (k * pw + dx);
                    if (lx >= 0.0 && lx < pw)
                    {
                        float z = k < 0.5 ? 1.05 : (k < 1.5 ? zoom : zoom * 1.6);
                        float2 ctr = k < 0.5
                            ? float2(0.5, 0.5)
                            : (k < 1.5 ? float2(0.5, focusY) : float2(0.58, focusY));
                        float2 sp = ctr * res + (float2(lx, px.y) - float2(pw, res.y) * 0.5) / z;
                        float4 panel = Tex.Sample(Smp, sp / res);

                        float inBorder = (lx < borderPx || lx > pw - borderPx
                                       || px.y < borderPx || px.y > res.y - borderPx) ? 1.0 : 0.0;
                        float3 rgb = lerp(panel.rgb / max(panel.a, 1e-4), PARAM(1, xyz), inBorder);
                        float alpha = lerp(panel.a, 1.0, inBorder);
                        outC = float4(saturate(rgb) * alpha, alpha);
                    }
                }
            }
            return outC;
        }
        """;

    /// <summary>双色调。P0 = 强度；P1 = 暗部色 rgb；P2 = 亮部色 rgb</summary>
    public const string Duotone = """
        float4 PSMain(VSOut i) : SV_Target
        {
            float4 c = Tex.Sample(Smp, i.uv);
            float3 src = c.rgb / max(c.a, 1e-4);
            float luma = dot(src, float3(0.2126, 0.7152, 0.0722));
            float3 duo = lerp(PARAM(1, xyz), PARAM(2, xyz), saturate(luma));
            float3 rgb = lerp(src, duo, saturate(PARAM(0, x)));
            return float4(saturate(rgb) * c.a, c.a);
        }
        """;

    /// <summary>电影黑边。P0 = 强度（条高 = 强度 x 0.12 x 高）；P1 = 条色 rgb</summary>
    public const string Letterbox = """
        float4 PSMain(VSOut i) : SV_Target
        {
            float4 c = Tex.Sample(Smp, i.uv);
            float bar = saturate(PARAM(0, x)) * 0.12;
            if (bar <= 0.0) return c;
            if (i.uv.y < bar || i.uv.y > 1.0 - bar) return float4(PARAM(1, xyz), 1.0);
            return c;
        }
        """;
}
