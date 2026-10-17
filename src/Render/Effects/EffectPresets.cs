namespace JizuraLive.Render.Effects;

public sealed record EffectPreset(string Id, string LabelKey, string DescriptionKey, Func<EffectGraph> Build);

public static class EffectPresets
{
    public static readonly IReadOnlyList<EffectPreset> All =
    [
        new("clean", "Preset.Clean", "Preset.Clean.Desc", () =>
        {
            var g = new EffectGraph();
            g.Add("colorGrade", n =>
            {
                Set(n, "saturation", 1.08f);
                Set(n, "contrast", 1.04f);
            });
            g.Add("vignette", n => Set(n, "intensity", 0.28f));
            return g;
        }),

        new("film", "Preset.Film", "Preset.Film.Desc", () =>
        {
            var g = new EffectGraph();
            g.Add("colorGrade", n =>
            {
                Set(n, "saturation", 0.92f);
                Set(n, "contrast", 1.10f);
                Set(n, "brightness", 0.015f);
                n.SetColor(EffectCatalog.Get("colorGrade")!.Params[4], "#ffd9a0");
                Set(n, "tintStrength", 0.14f);
            });
            g.Add("grain", n =>
            {
                Set(n, "amount", 0.055f);
                Set(n, "size", 1.6f);
            });
            g.Add("vignette", n =>
            {
                Set(n, "intensity", 0.45f);
                Set(n, "smoothness", 0.42f);
            });
            return g;
        }),

        new("vhs", "Preset.Vhs", "Preset.Vhs.Desc", () =>
        {
            var g = new EffectGraph();
            g.Add("colorGrade", n =>
            {
                Set(n, "saturation", 1.18f);
                Set(n, "contrast", 1.06f);
            });
            g.Add("chromatic", n =>
            {
                Set(n, "amount", 4f);
                Set(n, "radial", 0f);
            });
            g.Add("scanline", n =>
            {
                Set(n, "density", 220f);
                Set(n, "strength", 0.20f);
                Set(n, "roll", 0.35f);
            });
            g.Add("grain", n =>
            {
                Set(n, "amount", 0.07f);
                Set(n, "colored", 1f);
            });
            g.Add("vignette", n => Set(n, "intensity", 0.40f));
            return g;
        }),

        new("glitch", "Preset.Glitch", "Preset.Glitch.Desc", () =>
        {
            var g = new EffectGraph();
            g.Add("glitch", n =>
            {
                Set(n, "amount", 28f);
                Set(n, "blockSize", 56f);
            });
            g.Add("chromatic", n => Set(n, "amount", 8f));
            g.Add("slice", n =>
            {
                Set(n, "bands", 18f);
                Set(n, "amount", 46f);
            });
            return g;
        }),

        new("neon", "Preset.Neon", "Preset.Neon.Desc", () =>
        {
            var g = new EffectGraph();
            g.Add("colorGrade", n =>
            {
                Set(n, "saturation", 1.35f);
                Set(n, "contrast", 1.12f);
                n.SetColor(EffectCatalog.Get("colorGrade")!.Params[4], "#8fd4ff");
                Set(n, "tintStrength", 0.22f);
            });
            g.Add("glow", n =>
            {
                Set(n, "threshold", 0.5f);
                Set(n, "intensity", 1.6f);
                Set(n, "radius", 46f);
            });
            g.Add("vignette", n => Set(n, "intensity", 0.35f));
            return g;
        }),

        new("impact", "Preset.Impact", "Preset.Impact.Desc", () =>
        {
            var g = new EffectGraph();
            g.Add("zoomPunch", n => Set(n, "scale", 1.14f));
            g.Add("flash", n => Set(n, "amount", 0.35f));
            g.Add("chromatic", n => Set(n, "amount", 6f));
            return g;
        }),

        new("dreamy", "Preset.Dreamy", "Preset.Dreamy.Desc", () =>
        {
            var g = new EffectGraph();
            g.Add("colorGrade", n =>
            {
                Set(n, "saturation", 1.10f);
                Set(n, "brightness", 0.02f);
                n.SetColor(EffectCatalog.Get("colorGrade")!.Params[4], "#ffd0ea");
                Set(n, "tintStrength", 0.16f);
            });
            g.Add("liquid", n =>
            {
                Set(n, "amount", 14f);
                Set(n, "scale", 2.2f);
                Set(n, "speed", 0.35f);
            });
            g.Add("glow", n =>
            {
                Set(n, "threshold", 0.45f);
                Set(n, "intensity", 1.2f);
                Set(n, "radius", 40f);
            });
            return g;
        }),

        new("cinematic", "Preset.Cinematic", "Preset.Cinematic.Desc", () =>
        {
            var g = new EffectGraph();
            g.Add("colorGrade", n =>
            {
                Set(n, "saturation", 1.12f);
                Set(n, "contrast", 1.12f);
                Set(n, "brightness", -0.01f);
            });
            g.Add("bloom", n =>
            {
                Set(n, "threshold", 0.58f);
                Set(n, "intensity", 1.35f);
                Set(n, "radius", 34f);
            });
            g.Add("motionBlur", n =>
            {
                Set(n, "length", 9f);
                Set(n, "angle", 8f);
                Set(n, "strength", 0.45f);
            });
            g.Add("vignette", n => Set(n, "intensity", 0.42f));
            return g;
        }),

        new("graphic", "Preset.Graphic", "Preset.Graphic.Desc", () =>
        {
            var g = new EffectGraph();
            g.Add("colorGrade", n =>
            {
                Set(n, "saturation", 1.25f);
                Set(n, "contrast", 1.15f);
            });
            g.Add("posterize", n =>
            {
                Set(n, "levels", 5f);
                Set(n, "dither", 0.9f);
            });
            g.Add("halftone", n =>
            {
                Set(n, "dotSize", 7f);
                Set(n, "angle", 30f);
                Set(n, "strength", 0.45f);
            });
            return g;
        }),

        new("anime", "Preset.Anime", "Preset.Anime.Desc", () =>
        {
            var g = new EffectGraph();
            g.Add("bloom", n =>
            {
                Set(n, "threshold", 0.7f);
                Set(n, "intensity", 0.75f);
                Set(n, "radius", 24f);
            });
            g.Add("duotone", n => Set(n, "amount", 0.35f));
            g.Add("vignette", n => Set(n, "intensity", 0.22f));
            return g;
        }),
    ];

    public static EffectGraph? Create(string id) =>
        All.FirstOrDefault(p => p.Id == id)?.Build();

    public const string PickerPlaceholder = "（当前效果图）";

    public const int PickerInitialIndex = 0;

    public static IEnumerable<string> PickerLabels() =>
        All.Select(p => p.Id).Prepend(PickerPlaceholder);

    public static EffectPreset? PickerItemAt(int index) =>
        index <= 0 || index > All.Count ? null : All[index - 1];

    private static void Set(EffectNode n, string key, float value)
    {
        var def = EffectCatalog.Get(n.DefId)
                  ?? throw new InvalidOperationException($"预设引用了不存在的效果「{n.DefId}」");
        var p = def.Params.FirstOrDefault(x => x.Key == key)
                ?? throw new InvalidOperationException($"效果「{n.DefId}」没有参数「{key}」");
        n.Set(p, value);
    }
}
