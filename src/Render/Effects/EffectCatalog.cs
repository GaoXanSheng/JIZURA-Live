using JizuraLive.Render.Gpu;

namespace JizuraLive.Render.Effects;

/// <summary>
/// 效果目录：所有效果的登记处（两轴分类 + 参数 schema + HLSL）。
///
/// 与旧引擎的「手法注册表」的关系：旧表是**每种视觉手法一个条目**（约 300 条，各画各的）；
/// 这里是**每种可复用的 GPU 效果一个条目**（颗粒、色差、扫描线…），视觉手法将改为
/// 「效果图 + 参数预设」的组合（P2 后续）。两者不是替代关系：手法回答「画什么」，
/// 效果图回答「怎么滤」。
/// </summary>
public static class EffectCatalog
{
    public static readonly IReadOnlyList<EffectDef> All =
    [
        new("chromatic", "Fx.Chromatic", EffectStage.GlobalPost, EffectCategory.DistortGlitch,
            ShaderSources.Chromatic,
            [
                EffectParam.Num("amount", "Fx.Chromatic.Amount", 0, 0, 0f, 40f, 6f, 0.5f),
                EffectParam.Num("cx", "Fx.Chromatic.Cx", 0, 1, 0f, 1f, 0.5f),
                EffectParam.Num("cy", "Fx.Chromatic.Cy", 0, 2, 0f, 1f, 0.5f),
                EffectParam.Flag("radial", "Fx.Chromatic.Radial", 0, 3, true),
            ],
            ["glitch", "pop", "cyber"]),

        new("glitch", "Fx.Glitch", EffectStage.GlobalPost, EffectCategory.DistortGlitch,
            ShaderSources.Glitch,
            [
                EffectParam.Num("amount", "Fx.Glitch.Amount", 0, 0, 0f, 120f, 24f, 1f),
                EffectParam.Num("blockSize", "Fx.Glitch.BlockSize", 0, 1, 4f, 160f, 48f, 1f),
                EffectParam.Num("seed", "Fx.Glitch.Seed", 0, 2, 0f, 999f, 7f, 1f),
            ],
            ["glitch", "cyber"]),

        new("scanline", "Fx.Scanline", EffectStage.GlobalPost, EffectCategory.Texture,
            ShaderSources.Scanline,
            [
                EffectParam.Num("density", "Fx.Scanline.Density", 0, 0, 40f, 1600f, 420f, 10f),
                EffectParam.Num("strength", "Fx.Scanline.Strength", 0, 1, 0f, 1f, 0.32f),
                EffectParam.Num("roll", "Fx.Scanline.Roll", 0, 2, 0f, 3f, 0.25f),
            ],
            ["retro", "vhs", "cyber"]),

        new("grain", "Fx.Grain", EffectStage.GlobalPost, EffectCategory.Texture,
            ShaderSources.Grain,
            [
                EffectParam.Num("amount", "Fx.Grain.Amount", 0, 0, 0f, 0.4f, 0.07f, 0.005f),
                EffectParam.Num("size", "Fx.Grain.Size", 0, 1, 0.5f, 8f, 1.5f, 0.1f),
                EffectParam.Flag("colored", "Fx.Grain.Colored", 0, 2, false),
            ],
            ["retro", "calm", "film"]),

        new("vignette", "Fx.Vignette", EffectStage.GlobalPost, EffectCategory.Texture,
            ShaderSources.Vignette,
            [
                EffectParam.Num("intensity", "Fx.Vignette.Intensity", 0, 0, 0f, 1f, 0.5f),
                EffectParam.Num("smoothness", "Fx.Vignette.Smoothness", 0, 1, 0.05f, 1f, 0.45f),
                EffectParam.Num("roundness", "Fx.Vignette.Roundness", 0, 2, 0f, 1f, 0.7f),
                EffectParam.Num("centerY", "Fx.Vignette.CenterY", 0, 3, -0.3f, 0.3f, 0f, 0.01f),
            ],
            ["calm", "film", "emotional"]),

        new("colorGrade", "Fx.ColorGrade", EffectStage.GlobalPost, EffectCategory.ColorGrade,
            ShaderSources.ColorGrade,
            [
                EffectParam.Num("saturation", "Fx.ColorGrade.Saturation", 0, 0, 0f, 2f, 1f, 0.01f),
                EffectParam.Num("contrast", "Fx.ColorGrade.Contrast", 0, 1, 0.5f, 2f, 1f, 0.01f),
                EffectParam.Num("brightness", "Fx.ColorGrade.Brightness", 0, 2, -0.5f, 0.5f, 0f, 0.005f),
                EffectParam.Num("strength", "Fx.ColorGrade.Strength", 0, 3, 0f, 1f, 1f),
                EffectParam.Color3("tint", "Fx.ColorGrade.Tint", 1, 0),
                EffectParam.Num("tintStrength", "Fx.ColorGrade.TintStrength", 1, 3, 0f, 1f, 0f),
            ],
            ["calm", "emotional", "grade"]),

        new("halftone", "Fx.Halftone", EffectStage.GlobalPost, EffectCategory.Texture,
            ShaderSources.Halftone,
            [
                EffectParam.Num("dotSize", "Fx.Halftone.DotSize", 0, 0, 2f, 24f, 6f, 0.5f),
                EffectParam.Num("angle", "Fx.Halftone.Angle", 0, 1, 0f, 90f, 45f, 1f),
                EffectParam.Num("strength", "Fx.Halftone.Strength", 0, 2, 0f, 1f, 1f),
                EffectParam.Flag("mono", "Fx.Halftone.Mono", 0, 3, false),
            ],
            ["graphic", "pop", "retro"]),

        new("liquid", "Fx.Liquid", EffectStage.GlobalPost, EffectCategory.DistortGlitch,
            ShaderSources.Liquid,
            [
                EffectParam.Num("amount", "Fx.Liquid.Amount", 0, 0, 0f, 160f, 26f, 1f),
                EffectParam.Num("scale", "Fx.Liquid.Scale", 0, 1, 0.5f, 12f, 3f, 0.1f),
                EffectParam.Num("speed", "Fx.Liquid.Speed", 0, 2, 0f, 3f, 0.5f, 0.05f),
            ],
            ["emotional", "calm", "dreamy"]),

        new("glow", "Fx.Glow", EffectStage.GlobalPost, EffectCategory.LightGlow,
            ShaderSources.Glow,
            [
                EffectParam.Num("threshold", "Fx.Glow.Threshold", 0, 0, 0f, 1f, 0.6f, unit: ParamUnit.Ratio),
                EffectParam.Num("intensity", "Fx.Glow.Intensity", 0, 1, 0f, 3f, 1.1f, 0.05f, unit: ParamUnit.Dimensionless),
                EffectParam.Num("radius", "Fx.Glow.Radius", 0, 2, 1f, 160f, 32f, 1f, unit: ParamUnit.Pixels),
            ],
            ["pop", "emotional", "neon"], CostHint: 2.5f),

        new("posterize", "Fx.Posterize", EffectStage.GlobalPost, EffectCategory.Texture,
            ShaderSources.Posterize,
            [
                EffectParam.Num("levels", "Fx.Posterize.Levels", 0, 0, 2f, 16f, 6f, 1f),
                EffectParam.Num("dither", "Fx.Posterize.Dither", 0, 1, 0f, 2f, 0.8f, 0.05f),
                EffectParam.Num("strength", "Fx.Posterize.Strength", 0, 2, 0f, 1f, 1f),
            ],
            ["graphic", "retro", "pop"]),

        // ---- 旧引擎 8 个内置 FX 的 GPU 对位（原来硬编码在 Renderer 的 switch 里）----

        new("invert", "Fx.Invert", EffectStage.GlobalPost, EffectCategory.ColorGrade,
            ShaderSources.Invert,
            [EffectParam.Num("amount", "Fx.Invert.Amount", 0, 0, 0f, 1f, 1f)],
            ["glitch", "pop", "impact"]),

        new("flash", "Fx.Flash", EffectStage.GlobalPost, EffectCategory.LightGlow,
            ShaderSources.Flash,
            [
                EffectParam.Num("amount", "Fx.Flash.Amount", 0, 0, 0f, 1f, 0.7f),
                EffectParam.Color3("color", "Fx.Flash.Color", 1, 0, "#ffffff"),
            ],
            ["impact", "pop", "beat"]),

        new("zoomPunch", "Fx.ZoomPunch", EffectStage.GlobalPost, EffectCategory.Geometry,
            ShaderSources.ZoomPunch,
            [
                EffectParam.Num("scale", "Fx.ZoomPunch.Scale", 0, 0, 0.5f, 2f, 1.12f, 0.005f),
                EffectParam.Num("cx", "Fx.ZoomPunch.Cx", 0, 1, 0f, 1f, 0.5f),
                EffectParam.Num("cy", "Fx.ZoomPunch.Cy", 0, 2, 0f, 1f, 0.5f),
            ],
            ["impact", "beat", "pop"]),

        new("mosaic", "Fx.Mosaic", EffectStage.GlobalPost, EffectCategory.Geometry,
            ShaderSources.Mosaic,
            [EffectParam.Num("block", "Fx.Mosaic.Block", 0, 0, 2f, 96f, 16f, 1f)],
            ["glitch", "cyber"]),

        new("slice", "Fx.Slice", EffectStage.GlobalPost, EffectCategory.Geometry,
            ShaderSources.Slice,
            [
                EffectParam.Num("bands", "Fx.Slice.Bands", 0, 0, 2f, 60f, 14f, 1f),
                EffectParam.Num("amount", "Fx.Slice.Amount", 0, 1, 0f, 160f, 40f, 1f),
                EffectParam.Num("seed", "Fx.Slice.Seed", 0, 2, 0f, 999f, 3f, 1f),
            ],
            ["glitch", "impact", "cyber"]),

        new("block", "Fx.Block", EffectStage.GlobalPost, EffectCategory.DistortGlitch,
            ShaderSources.Block,
            [
                EffectParam.Num("blockSize", "Fx.Block.BlockSize", 0, 0, 4f, 200f, 48f, 1f),
                EffectParam.Num("amount", "Fx.Block.Amount", 0, 1, 0f, 160f, 36f, 1f),
                EffectParam.Num("seed", "Fx.Block.Seed", 0, 2, 0f, 999f, 11f, 1f),
            ],
            ["glitch", "cyber"]),

        new("shake", "Fx.Shake", EffectStage.GlobalPost, EffectCategory.Camera,
            ShaderSources.Shake,
            [
                EffectParam.Num("amount", "Fx.Shake.Amount", 0, 0, 0f, 80f, 14f, 0.5f),
                EffectParam.Num("rate", "Fx.Shake.Rate", 0, 1, 0.1f, 4f, 1f, 0.05f),
            ],
            ["impact", "beat", "horror"]),

        // ---- 追加：研究里点名的热门 PV 风格 ----

        new("vhs", "Fx.Vhs", EffectStage.GlobalPost, EffectCategory.Texture,
            ShaderSources.Vhs,
            [
                EffectParam.Num("warble", "Fx.Vhs.Warble", 0, 0, 0f, 8f, 1.4f, 0.1f),
                EffectParam.Num("chroma", "Fx.Vhs.Chroma", 0, 1, 0f, 12f, 2.6f, 0.1f),
                EffectParam.Num("tracking", "Fx.Vhs.Tracking", 0, 2, 0f, 1f, 0.5f),
            ],
            ["retro", "vhs", "glitch"]),

        new("lightRays", "Fx.LightRays", EffectStage.GlobalPost, EffectCategory.LightGlow,
            ShaderSources.LightRays,
            [
                EffectParam.Num("length", "Fx.LightRays.Length", 0, 0, 0.02f, 0.5f, 0.16f, 0.005f),
                EffectParam.Num("intensity", "Fx.LightRays.Intensity", 0, 1, 0f, 3f, 0.9f, 0.05f),
                EffectParam.Num("cx", "Fx.LightRays.Cx", 0, 2, 0f, 1f, 0.5f),
                EffectParam.Num("cy", "Fx.LightRays.Cy", 0, 3, 0f, 1f, 0.35f),
            ],
            ["emotional", "neon", "cinematic"], CostHint: 2f),

        new("glassShatter", "Fx.GlassShatter", EffectStage.GlobalPost, EffectCategory.Geometry,
            ShaderSources.GlassShatter,
            [
                EffectParam.Num("cells", "Fx.GlassShatter.Cells", 0, 0, 2f, 40f, 12f, 0.5f),
                EffectParam.Num("amount", "Fx.GlassShatter.Amount", 0, 1, 0f, 60f, 12f, 0.5f),
                EffectParam.Num("crack", "Fx.GlassShatter.Crack", 0, 2, 0f, 1f, 0.75f),
                EffectParam.Num("seed", "Fx.GlassShatter.Seed", 0, 3, 0f, 999f, 5f, 1f),
            ],
            ["impact", "glitch", "dramatic"]),

        new("wave", "Fx.Wave", EffectStage.GlobalPost, EffectCategory.DistortGlitch,
            ShaderSources.Wave,
            [
                EffectParam.Num("amount", "Fx.Wave.Amount", 0, 0, 0f, 80f, 16f, 0.5f),
                EffectParam.Num("wavelength", "Fx.Wave.Wavelength", 0, 1, 10f, 400f, 120f, 5f),
                EffectParam.Num("speed", "Fx.Wave.Speed", 0, 2, 0f, 4f, 0.8f, 0.05f),
                EffectParam.Flag("vertical", "Fx.Wave.Vertical", 0, 3, false),
            ],
            ["calm", "water", "emotional"]),

        // ---- 多 pass 重型效果 ----

        new("bloom", "Fx.Bloom", EffectStage.GlobalPost, EffectCategory.LightGlow,
            ShaderSources.BloomComposite,   // 文档用：真正绘制由 GraphBuild 编排
            [
                EffectParam.Num("threshold", "Fx.Bloom.Threshold", 0, 0, 0f, 1f, 0.62f, unit: ParamUnit.Ratio),
                EffectParam.Num("intensity", "Fx.Bloom.Intensity", 0, 1, 0f, 3f, 1.1f, 0.05f, unit: ParamUnit.Dimensionless),
                EffectParam.Num("radius", "Fx.Bloom.Radius", 0, 2, 1f, 120f, 26f, 1f, unit: ParamUnit.Pixels),
            ],
            ["pop", "emotional", "neon", "cinematic"], CostHint: 3f)
        {
            // 真正的 mip 链 bloom：亮部提取(1/2) → 三级可分离模糊(1/2→1/4→1/8) → 与原图相加
            GraphBuild = BuildBloom,
        },

        new("motionBlur", "Fx.MotionBlur", EffectStage.GlobalPost, EffectCategory.Geometry,
            ShaderSources.MotionBlur,
            [
                EffectParam.Num("length", "Fx.MotionBlur.Length", 0, 0, 0f, 120f, 18f, 0.5f),
                EffectParam.Num("angle", "Fx.MotionBlur.Angle", 0, 1, 0f, 360f, 0f, 1f),
                EffectParam.Num("strength", "Fx.MotionBlur.Strength", 0, 2, 0f, 1f, 0.8f),
            ],
            ["cinematic", "impact", "speed"]),

        // ---- 漫画 / 二次元语汇 ----

        new("speedLines", "Fx.SpeedLines", EffectStage.GlobalPost, EffectCategory.Geometry,
            ShaderSources.SpeedLines,
            [
                EffectParam.Num("amount", "Fx.SpeedLines.Amount", 0, 0, 0f, 1f, 0.8f),
                EffectParam.Num("density", "Fx.SpeedLines.Density", 0, 1, 8f, 160f, 64f, 1f),
                EffectParam.Num("inner", "Fx.SpeedLines.Inner", 0, 2, 0f, 0.9f, 0.35f),
                EffectParam.Num("thickness", "Fx.SpeedLines.Thickness", 0, 3, 0.2f, 3f, 1f, 0.05f),
                EffectParam.Num("speed", "Fx.SpeedLines.Speed", 1, 0, 0f, 12f, 4f, 0.1f),
                EffectParam.Flag("horizontal", "Fx.SpeedLines.Horizontal", 1, 1, false),
                EffectParam.Color3("color", "Fx.SpeedLines.Color", 2, 0, "#ffffff"),
            ],
            ["anime", "impact", "speed"]),

        new("wipe", "Fx.Wipe", EffectStage.GlobalPost, EffectCategory.Geometry,
            ShaderSources.Wipe,
            [
                EffectParam.Num("progress", "Fx.Wipe.Progress", 0, 0, 0f, 1f, 0f),
                EffectParam.Num("angle", "Fx.Wipe.Angle", 0, 1, -180f, 180f, 30f, 1f),
                EffectParam.Num("softness", "Fx.Wipe.Softness", 0, 2, 0f, 0.2f, 0.01f, 0.001f),
                EffectParam.Flag("iris", "Fx.Wipe.Iris", 0, 3, false),
                EffectParam.Color3("color", "Fx.Wipe.Color", 1, 0, "#ffffff"),
                EffectParam.Num("band", "Fx.Wipe.Band", 1, 3, 0f, 0.25f, 0.06f, 0.005f),
                EffectParam.Color3("accent", "Fx.Wipe.Accent", 2, 0, "#ff5fa2"),
            ],
            ["anime", "transition", "impact"]),

        new("mangaPanels", "Fx.MangaPanels", EffectStage.GlobalPost, EffectCategory.Geometry,
            ShaderSources.MangaPanels,
            [
                EffectParam.Num("amount", "Fx.MangaPanels.Amount", 0, 0, 0f, 1f, 1f),
                EffectParam.Num("count", "Fx.MangaPanels.Count", 0, 1, 2f, 3f, 3f, 1f),
                EffectParam.Num("tilt", "Fx.MangaPanels.Tilt", 0, 2, -30f, 30f, 10f, 1f),
                EffectParam.Num("border", "Fx.MangaPanels.Border", 0, 3, 0f, 0.03f, 0.008f, 0.001f),
                EffectParam.Color3("borderColor", "Fx.MangaPanels.BorderColor", 1, 0, "#ffffff"),
                EffectParam.Num("zoom", "Fx.MangaPanels.Zoom", 1, 3, 1f, 3f, 1.8f, 0.05f),
                EffectParam.Num("focusY", "Fx.MangaPanels.FocusY", 2, 0, 0f, 1f, 0.32f),
            ],
            ["anime", "graphic", "impact"]),

        new("duotone", "Fx.Duotone", EffectStage.GlobalPost, EffectCategory.ColorGrade,
            ShaderSources.Duotone,
            [
                EffectParam.Num("amount", "Fx.Duotone.Amount", 0, 0, 0f, 1f, 0.6f),
                EffectParam.Color3("shadow", "Fx.Duotone.Shadow", 1, 0, "#2b1b5a"),
                EffectParam.Color3("highlight", "Fx.Duotone.Highlight", 2, 0, "#ffb3d9"),
            ],
            ["anime", "grade", "emotional"]),

        new("letterbox", "Fx.Letterbox", EffectStage.GlobalPost, EffectCategory.Camera,
            ShaderSources.Letterbox,
            [
                EffectParam.Num("amount", "Fx.Letterbox.Amount", 0, 0, 0f, 1f, 1f),
                EffectParam.Color3("color", "Fx.Letterbox.Color", 1, 0, "#000000"),
            ],
            ["cinematic", "anime", "calm"]),
    ];

    /// <summary>
    /// mip 链 bloom：亮部提取 → 逐级降采样 + 可分离模糊 → 与原图相加。
    /// 尺寸靠每个 pass 的 Scale 递减（1/2 → 1/4 → 1/8），既是质量的来源也是省算力的手段。
    /// </summary>
    private static int BuildBloom(EffectNode node, GpuRenderGraph graph, ShaderLibrary lib,
        int input, int width, int height)
    {
        var ps = Get("bloom")!.Params;
        var threshold = node.Value(ps[0]);
        var intensity = node.Value(ps[1]);
        var radius = node.Value(ps[2]);

        var bright = lib.GetPixel("bloom.bright", ShaderSources.BloomBright);
        var blurH = lib.GetPixel("bloom.blurH", ShaderSources.BlurH);
        var blurV = lib.GetPixel("bloom.blurV", ShaderSources.BlurV);
        var composite = lib.GetPixel("bloom.composite", ShaderSources.BloomComposite);

        var a = graph.Add(bright, One(threshold), input, GpuRenderGraph.None, 0.5);
        var b = graph.Add(blurH, One(radius * 0.5f), a, GpuRenderGraph.None, 0.5);
        var c = graph.Add(blurV, One(radius * 0.5f), b, GpuRenderGraph.None, 0.5);
        var d = graph.Add(blurH, One(radius), c, GpuRenderGraph.None, 0.25);
        var e = graph.Add(blurV, One(radius), d, GpuRenderGraph.None, 0.25);
        var f = graph.Add(blurH, One(radius * 2), e, GpuRenderGraph.None, 0.125);
        var g2 = graph.Add(blurV, One(radius * 2), f, GpuRenderGraph.None, 0.125);
        return graph.Add(composite, One(intensity), input, g2, 1.0);
    }

    /// <summary>只填第 0 个分量的常量数组（全局槽由 EffectGraph 统一补）。</summary>
    private static float[] One(float v)
    {
        var c = new float[Gpu.FullScreenPass.ParamSlots * 4];
        c[0] = v;
        return c;
    }

    /// <summary>
    /// 各效果的"混合参数"与它的无效果值：出现区间淡入淡出时把该参数从无效果值插到当前值。
    /// 不在表里的效果（mosaic / vhs：没有能退化成原图的单个参数）在区间边界硬切。
    /// </summary>
    private static readonly Dictionary<string, (string Key, float Identity)> Mix = new(StringComparer.Ordinal)
    {
        ["chromatic"] = ("amount", 0f),
        ["glitch"] = ("amount", 0f),
        ["scanline"] = ("strength", 0f),
        ["grain"] = ("amount", 0f),
        ["vignette"] = ("intensity", 0f),
        ["colorGrade"] = ("strength", 0f),
        ["halftone"] = ("strength", 0f),
        ["liquid"] = ("amount", 0f),
        ["glow"] = ("intensity", 0f),
        ["posterize"] = ("strength", 0f),
        ["invert"] = ("amount", 0f),
        ["flash"] = ("amount", 0f),
        ["zoomPunch"] = ("scale", 1f),
        ["slice"] = ("amount", 0f),
        ["block"] = ("amount", 0f),
        ["shake"] = ("amount", 0f),
        ["lightRays"] = ("intensity", 0f),
        ["glassShatter"] = ("amount", 0f),
        ["wave"] = ("amount", 0f),
        ["bloom"] = ("intensity", 0f),
        ["motionBlur"] = ("strength", 0f),
    };

    /// <summary>取效果的混合参数 Key 与无效果值；没有则返回 false。</summary>
    public static bool TryGetMix(string defId, out string key, out float identity)
    {
        if (Mix.TryGetValue(defId, out var m))
        {
            (key, identity) = m;
            return true;
        }

        key = "";
        identity = 0;
        return false;
    }

    private static readonly Dictionary<string, EffectDef> ById =
        All.ToDictionary(d => d.Id, StringComparer.Ordinal);

    public static EffectDef? Get(string id) => ById.GetValueOrDefault(id);

    /// <summary>按类别取（UI 的分类树按这个分组）。</summary>
    public static IEnumerable<EffectDef> ByCategory(EffectCategory c) =>
        All.Where(d => d.Category == c);

    /// <summary>按挂载阶段取。</summary>
    public static IEnumerable<EffectDef> ByStage(EffectStage s) =>
        All.Where(d => d.Stage == s);

    /// <summary>按标签检索（"glitch"/"calm"/"cyber"…，用于按情绪自动选效果）。</summary>
    public static IEnumerable<EffectDef> TaggedWith(string tag) =>
        All.Where(d => d.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
}
