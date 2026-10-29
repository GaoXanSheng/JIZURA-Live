namespace JizuraLive.Render.Effects;

public static class ShaderSources
{
    public const string Chromatic = """
        float4 PSMain(VSOut i) : SV_Target
        {
            float2 uv = i.uv;
            float2 dir = PARAM(0, w) > 0.5
                ? normalize(uv - float2(PARAM(0, y), PARAM(0, z)) + 1e-6)
                : float2(1, 0);
            float2 off = dir * PARAM(0, x) / max(PARAM(15, y), 1.0);
            off.y *= max(PARAM(15, w), 0.01);
            float4 c = Tex.Sample(Smp, uv);
            float r = Tex.Sample(Smp, uv + off).r;
            float b = Tex.Sample(Smp, uv - off).b;
            return float4(r, c.g, b, c.a);
        }
        """;

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
                float2 off = float2(cos(ang), sin(ang) * max(PARAM(15, w), 0.01))
                             * radius * (0.4 + 0.6 * frac(k * 0.618));
                float4 s = Tex.Sample(Smp, i.uv + off);
                float3 srgb = s.rgb / max(s.a, 1e-4);
                float l = dot(srgb, float3(0.299, 0.587, 0.114));
                acc += srgb * max(l - PARAM(0, x), 0.0);
            }
            acc /= 16.0;
            rgb += acc * PARAM(0, y);
            return float4(saturate(rgb) * base.a, base.a);
        }
        """;

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

    public const string Flash = """
        float4 PSMain(VSOut i) : SV_Target
        {
            float4 c = Tex.Sample(Smp, i.uv);
            float3 rgb = c.rgb / max(c.a, 1e-4);
            rgb = lerp(rgb, PARAM(1, xyz), PARAM(0, x));
            return float4(saturate(rgb) * c.a, c.a);
        }
        """;

    public const string ZoomPunch = """
        float4 PSMain(VSOut i) : SV_Target
        {
            float2 ctr = float2(PARAM(0, y), PARAM(0, z));
            float2 uv = (i.uv - ctr) / max(PARAM(0, x), 1e-3) + ctr;
            return Tex.Sample(Smp, uv);
        }
        """;

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

    public const string Blend = """
        float4 PSMain(VSOut i) : SV_Target
        {
            float4 a = Tex.Sample(Smp, i.uv);
            float4 b = Tex1.Sample(Smp, i.uv);
            float mode = PARAM(0, x);
            float opacity = saturate(PARAM(0, y));
            float3 ar = a.rgb / max(a.a, 1e-4);
            float3 br = b.rgb / max(b.a, 1e-4);
            float3 r;
            if (mode < 0.5) r = ar;
            else if (mode < 1.5) r = br + ar;
            else if (mode < 2.5) r = 1.0 - (1.0 - br) * (1.0 - ar);
            else if (mode < 3.5) r = br * ar;
            else
            {
                float3 lo = 2.0 * br * ar;
                float3 hi = 1.0 - 2.0 * (1.0 - br) * (1.0 - ar);
                r = lerp(lo, hi, step(0.5, br));
            }
            float alpha = lerp(b.a, max(a.a, b.a), opacity);
            float3 rgb = lerp(br, r, opacity);
            return float4(saturate(rgb) * alpha, alpha);
        }
        """;

    public const string AudioPulse = """
        float4 PSMain(VSOut i) : SV_Target
        {
            float amount = saturate(PARAM(0, x));
            float zoom = PARAM(0, y);
            float glow = PARAM(0, z);
            float lowGain = PARAM(0, w);
            float highGain = PARAM(1, x);
            float bass = saturate(PARAM(14, x));
            float mid = saturate(PARAM(14, y));
            float high = saturate(PARAM(14, z));
            float rms = saturate(PARAM(14, w));
            float e = saturate(bass * lowGain + mid * 0.6 + high * highGain + rms * 0.5);
            float2 uv = (i.uv - 0.5) / max(1.0 + e * zoom * 0.12, 0.01) + 0.5;
            float4 c = Tex.Sample(Smp, uv);
            float3 baseRgb = c.rgb / max(c.a, 1e-4);
            float split = e * high * 0.006;
            float4 sr = Tex.Sample(Smp, uv + float2(split, 0));
            float4 sb = Tex.Sample(Smp, uv - float2(split, 0));
            float3 rgb = float3(sr.r / max(sr.a, 1e-4), baseRgb.g, sb.b / max(sb.a, 1e-4))
                       * (1.0 + e * glow * 0.8);
            rgb = lerp(baseRgb, rgb, amount);
            return float4(saturate(rgb) * c.a, c.a);
        }
        """;

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

    public const string MotionBlur = """
        float4 PSMain(VSOut i) : SV_Target
        {
            float a = radians(PARAM(0, y));
            float2 dir = float2(cos(a), sin(a) * max(PARAM(15, w), 0.01))
                         * PARAM(0, x) / max(PARAM(15, y), 1.0);
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

            float2 p = (i.uv - 0.5) * float2(aspect, 1.0);
            float2 he = float2(0.5 * aspect, 0.5);
            float dist = length(p);
            float innerR = inner * 0.5;
            float tick = floor(PARAM(15, x) * max(PARAM(1, x), 0.0));

            float m;
            if (PARAM(1, y) > 0.5)
            {
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

    public const string Wipe = """
        float4 PSMain(VSOut i) : SV_Target
        {
            float p = saturate(PARAM(0, x));
            float4 c = Tex.Sample(Smp, i.uv);
            if (p <= 0.0 || p >= 1.0) return c;

            float soft = max(PARAM(0, z), 1e-4);
            float band = max(PARAM(1, w), 0.0);
            float u = p <= 0.5 ? p * 2.0 : (p - 0.5) * 2.0;
            float aspect = max(PARAM(15, w), 0.01);
            float2 q = (i.uv - 0.5) * float2(aspect, 1.0);

            float m;
            float edgeDist;
            if (PARAM(0, w) > 0.5)
            {
                float rMax = 0.5 * sqrt(aspect * aspect + 1.0) + soft + band + 1e-3;
                float r = p <= 0.5 ? lerp(rMax, -2.0 * soft, u) : lerp(-2.0 * soft, rMax, u);
                float d = length(q);
                m = smoothstep(r - soft, r + soft, d);
                edgeDist = abs(d - r);
            }
            else
            {
                float a = radians(PARAM(0, y));
                float2 dir = float2(cos(a), sin(a));
                float reach = 0.5 * (abs(dir.x) * aspect + abs(dir.y));
                float front = lerp(-(reach + soft + band + 1e-3), reach + soft + band + 1e-3, u);
                float axis = dot(q, dir);
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
            float xs = px.x - (px.y - res.y * 0.5) * tan(radians(PARAM(0, z)));
            float slide = (1.0 - amount) * res.x;

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
