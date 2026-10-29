using JizuraLive.Render.Gpu;

namespace JizuraLive.Render.Effects;

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

        new("slice", "Fx.Slice", EffectStage.GlobalPost, EffectCategory.Geometry,
            ShaderSources.Slice,
            [
                EffectParam.Num("bands", "Fx.Slice.Bands", 0, 0, 2f, 60f, 14f, 1f),
                EffectParam.Num("amount", "Fx.Slice.Amount", 0, 1, 0f, 160f, 40f, 1f),
                EffectParam.Num("seed", "Fx.Slice.Seed", 0, 2, 0f, 999f, 3f, 1f),
            ],
            ["glitch", "impact", "cyber"]),

        new("shake", "Fx.Shake", EffectStage.GlobalPost, EffectCategory.Camera,
            ShaderSources.Shake,
            [
                EffectParam.Num("amount", "Fx.Shake.Amount", 0, 0, 0f, 80f, 14f, 0.5f),
                EffectParam.Num("rate", "Fx.Shake.Rate", 0, 1, 0.1f, 4f, 1f, 0.05f),
            ],
            ["impact", "beat", "horror"]),

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

        new("bloom", "Fx.Bloom", EffectStage.GlobalPost, EffectCategory.LightGlow,
            ShaderSources.BloomComposite,
            [
                EffectParam.Num("threshold", "Fx.Bloom.Threshold", 0, 0, 0f, 1f, 0.62f, unit: ParamUnit.Ratio),
                EffectParam.Num("intensity", "Fx.Bloom.Intensity", 0, 1, 0f, 3f, 1.1f, 0.05f, unit: ParamUnit.Dimensionless),
                EffectParam.Num("radius", "Fx.Bloom.Radius", 0, 2, 1f, 120f, 26f, 1f, unit: ParamUnit.Pixels),
            ],
            ["pop", "emotional", "neon", "cinematic"], CostHint: 3f)
        {
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

        new("audioPulse", "Fx.AudioPulse", EffectStage.GlobalPost, EffectCategory.LightGlow,
            ShaderSources.AudioPulse,
            [
                EffectParam.Num("amount", "Fx.AudioPulse.Amount", 0, 0, 0f, 1f, 0.7f),
                EffectParam.Num("zoom", "Fx.AudioPulse.Zoom", 0, 1, 0f, 1f, 0.4f),
                EffectParam.Num("glow", "Fx.AudioPulse.Glow", 0, 2, 0f, 2f, 0.8f),
                EffectParam.Num("low", "Fx.AudioPulse.Low", 0, 3, 0f, 2f, 1f),
                EffectParam.Num("high", "Fx.AudioPulse.High", 1, 0, 0f, 2f, 0.5f),
            ],
            ["audio", "impact", "anime"]),

        new("letterbox", "Fx.Letterbox", EffectStage.GlobalPost, EffectCategory.Camera,
            ShaderSources.Letterbox,
            [
                EffectParam.Num("amount", "Fx.Letterbox.Amount", 0, 0, 0f, 1f, 1f),
                EffectParam.Color3("color", "Fx.Letterbox.Color", 1, 0, "#000000"),
            ],
            ["cinematic", "anime", "calm"]),
    ];

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

    private static float[] One(float v)
    {
        var c = new float[Gpu.FullScreenPass.ParamSlots * 4];
        c[0] = v;
        return c;
    }

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
        ["flash"] = ("amount", 0f),
        ["zoomPunch"] = ("scale", 1f),
        ["slice"] = ("amount", 0f),
        ["shake"] = ("amount", 0f),
        ["lightRays"] = ("intensity", 0f),
        ["glassShatter"] = ("amount", 0f),
        ["bloom"] = ("intensity", 0f),
        ["motionBlur"] = ("strength", 0f),
        ["speedLines"] = ("amount", 0f),
        ["audioPulse"] = ("amount", 0f),
        ["wipe"] = ("progress", 0f),
        ["mangaPanels"] = ("amount", 0f),
        ["duotone"] = ("amount", 0f),
        ["letterbox"] = ("amount", 0f),
    };

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
}
