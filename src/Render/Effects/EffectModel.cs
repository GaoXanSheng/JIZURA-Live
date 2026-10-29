using System.Globalization;

namespace JizuraLive.Render.Effects;

public enum EffectStage
{
    Source,

    Layer,

    Composite,

    GlobalPost,

    Transition,

    Output,
}

public enum EffectCategory
{
    ColorGrade,

    LightGlow,

    DistortGlitch,

    Geometry,

    Texture,

    Camera,
}

public enum EffectParamKind
{
    Number,

    Bool,

    Color,
}

public sealed record EffectParam(
    string Key,
    string LabelKey,
    EffectParamKind Kind,
    int Slot,
    int Comp,
    float Min = 0f,
    float Max = 1f,
    float Default = 0f,
    float Step = 0.01f,
    string Group = "main")
{
    public string DefaultColor { get; init; } = "#ffffff";

    public ParamUnit Unit { get; init; } = ParamUnit.Unspecified;

    public static EffectParam Num(string key, string labelKey, int slot, int comp,
        float min, float max, float def, float step = 0.01f, string group = "main",
        ParamUnit unit = ParamUnit.Unspecified) =>
        new(key, labelKey, EffectParamKind.Number, slot, comp, min, max, def, step, group)
        {
            Unit = unit,
        };

    public static EffectParam Flag(string key, string labelKey, int slot, int comp,
        bool def, string group = "main") =>
        new(key, labelKey, EffectParamKind.Bool, slot, comp, 0f, 1f, def ? 1f : 0f, 1f, group);

    public static EffectParam Color3(string key, string labelKey, int slot, int comp,
        string def = "#ffffff", string group = "main") =>
        new(key, labelKey, EffectParamKind.Color, slot, comp, 0f, 1f, 0f, 0.01f, group)
        {
            DefaultColor = def,
        };
}

public delegate int EffectGraphBuild(EffectNode node, Gpu.GpuRenderGraph graph,
    Gpu.ShaderLibrary library, int input, int width, int height);

public sealed record EffectDef(
    string Id,
    string NameKey,
    EffectStage Stage,
    EffectCategory Category,
    string Hlsl,
    EffectParam[] Params,
    string[] Tags,
    float CostHint = 1f)
{
    public EffectGraphBuild? GraphBuild { get; init; }

}

public readonly record struct EffectWindow(double Start, double End, double FadeIn = 0, double FadeOut = 0)
{
    public double Gain { get; init; } = 1;

    [Newtonsoft.Json.JsonIgnore]
    public bool IsValid => double.IsFinite(Start) && double.IsFinite(End) && End > Start
                           && double.IsFinite(FadeIn) && double.IsFinite(FadeOut) && FadeIn >= 0 && FadeOut >= 0
                           && double.IsFinite(Gain) && Gain >= 0;

    public double WeightAt(double t)
    {
        if (!(t >= Start && t < End)) return 0;
        var len = End - Start;
        var fi = FadeIn;
        var fo = FadeOut;
        if (fi + fo > len)
        {
            var k = len / (fi + fo);
            fi *= k;
            fo *= k;
        }

        var w = 1.0;
        if (fi > 0) w = Math.Min(w, (t - Start) / fi);
        if (fo > 0) w = Math.Min(w, (End - t) / fo);
        return Math.Clamp(w, 0, 1) * Gain;
    }
}

public enum EffectBlend
{
    Normal,
    Add,
    Screen,
    Multiply,
    Overlay,
}

public sealed class EffectNode
{
    public string DefId { get; init; } = "";

    public EffectStage Stage { get; set; } = EffectStage.GlobalPost;

    public EffectBlend Blend { get; set; } = EffectBlend.Normal;

    public float Opacity { get; set; } = 1f;

    public string Target { get; set; } = "";

    public string InstanceId { get; init; } = "";

    public string Source { get; set; } = "user";

    public bool Locked { get; set; }

    public bool Enabled { get; set; } = true;

    public List<EffectWindow> Windows { get; } = new();

    public bool FrameActive { get; set; } = true;

    private Dictionary<string, float>? _rest;

    public float RestValue(EffectParam p)
    {
        _rest ??= new Dictionary<string, float>();
        if (!_rest.TryGetValue(p.Key, out var v)) _rest[p.Key] = v = Value(p);
        return v;
    }

    public double WindowWeight(double time)
    {
        if (Windows.Count == 0) return 1;
        double best = 0;
        foreach (var w in Windows)
        {
            var v = w.WeightAt(time);
            if (v > best) best = v;
        }

        return best;
    }

    public Dictionary<string, float> Values { get; } = new();

    public Dictionary<string, string> Colors { get; } = new();

    public float Value(EffectParam p) => Values.TryGetValue(p.Key, out var v) ? v : p.Default;

    public string ColorValue(EffectParam p) =>
        Colors.TryGetValue(p.Key, out var c) ? c : p.DefaultColor;

    public EffectNode Set(EffectParam p, float value)
    {
        Values[p.Key] = Math.Clamp(value, p.Min, p.Max);
        return this;
    }

    public EffectNode SetColor(EffectParam p, string hex)
    {
        Colors[p.Key] = hex;
        return this;
    }

    internal static (float R, float G, float B) ParseHex(string hex)
    {
        var s = (hex ?? "").TrimStart('#');
        if (s.Length == 3) s = $"{s[0]}{s[0]}{s[1]}{s[1]}{s[2]}{s[2]}";
        if (s.Length < 6) return (1f, 1f, 1f);
        return (Parse2(s, 0), Parse2(s, 2), Parse2(s, 4));

        static float Parse2(string s, int i) =>
            int.TryParse(s.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
                ? v / 255f
                : 1f;
    }
}
