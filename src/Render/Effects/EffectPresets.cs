namespace JizuraLive.Render.Effects;

/// <summary>一个具名的效果图预设（分类体系里的「可复用组合」，给用户一个起点而不是空白页）。</summary>
public sealed record EffectPreset(string Id, string LabelKey, string DescriptionKey, Func<EffectGraph> Build);

/// <summary>
/// 效果图预设库。预设回答的是「想要某种风格该怎么搭」——用户抱怨的「不知道怎么去分类调整」，
/// 一半靠分类树（EffectCategory），另一半就靠这些能直接套用的组合。
/// </summary>
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
            // 多 pass mip 链 bloom：亮部逐级降采样 + 模糊后与原图相加
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

    /// <summary>按 id 建一份预设图（不存在返回 null）。</summary>
    public static EffectGraph? Create(string id) =>
        All.FirstOrDefault(p => p.Id == id)?.Build();

    /// <summary>预设选择器的占位项：表示「当前效果图不是任何预设」。选中它不改动效果图。</summary>
    public const string PickerPlaceholder = "（当前效果图）";

    /// <summary>
    /// 预设选择器的初始选中项：恒为占位项。
    ///
    /// 启动时**绝不能**程序化选中某个预设：<c>SelectionChanged</c> 会把工程里已存的效果图整份换掉，
    /// 还会被随后的自动存盘写回磁盘——用户上次存的效果图就这么没了，而界面上只是「默认选中了某个风格」。
    /// </summary>
    public const int PickerInitialIndex = 0;

    /// <summary>选择器要显示的项（下标与 <see cref="PickerItemAt"/> 对应）：占位项 + 全部预设 Id。</summary>
    public static IEnumerable<string> PickerLabels() =>
        All.Select(p => p.Id).Prepend(PickerPlaceholder);

    /// <summary>
    /// 选择器第 <paramref name="index"/> 项对应的预设。占位项（<see cref="PickerInitialIndex"/>）
    /// 与越界返回 null = **不改动效果图**。
    ///
    /// 映射只此一处：选择器的下标与 <see cref="All"/> 差一个占位项，写两遍迟早有一天对不上。
    /// </summary>
    public static EffectPreset? PickerItemAt(int index) =>
        index <= 0 || index > All.Count ? null : All[index - 1];

    /// <summary>
    /// 按 key 设参数（预设里用 key 而不是下标，读起来才像配置而不是魔法数字）。
    /// key 写错时**直接抛**：预设是代码，拼错的参数名如果被静默忽略，会变成「预设看起来生效了、
    /// 其实某个参数一直是默认值」这种最难查的问题。
    /// </summary>
    private static void Set(EffectNode n, string key, float value)
    {
        var def = EffectCatalog.Get(n.DefId)
                  ?? throw new InvalidOperationException($"预设引用了不存在的效果「{n.DefId}」");
        var p = def.Params.FirstOrDefault(x => x.Key == key)
                ?? throw new InvalidOperationException($"效果「{n.DefId}」没有参数「{key}」");
        n.Set(p, value);
    }
}
