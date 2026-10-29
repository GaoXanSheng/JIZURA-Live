using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JizuraLive.Pv;

public sealed class LegacyPresetSettings
{
    public bool Unify { get; set; }

    public bool Typeset { get; set; }

    public bool Extra { get; set; }

    public bool Wa { get; set; } = true;

    public bool Typo { get; set; } = true;

    public bool Kinetic { get; set; } = true;

    public bool Horror { get; set; }

    public string Lang { get; set; } = "auto";

    public string KeyBg { get; set; } = "off";

    public double Motion { get; set; } = 0.7;

    public double Glitch { get; set; } = 0.55;

    public double Chroma { get; set; } = 0.7;

    public double Decor { get; set; } = 0.5;

    public double Density { get; set; } = 0.55;

    public double Texture { get; set; } = 0.6;

    public double BgSwitch { get; set; } = 0.35;

    public bool Flash { get; set; } = true;

    public string Hud { get; set; } = "auto";

    public bool HideNo { get; set; }

    public bool HideTime { get; set; }

    public bool CenterFree { get; set; }

    public string CenterDir { get; set; } = "tb";

    public string CenterZone { get; set; } = "center";

    public double? CenterGap { get; set; }

    public bool ColorsEnabled { get; set; }

    public bool AccentOn { get; set; }

    public string ColorBg { get; set; } = "";

    public string ColorFg { get; set; } = "";

    public string ColorSub { get; set; } = "";

    public string ColorAccent { get; set; } = "";

    public string ColorGhostA { get; set; } = "";

    public string ColorGhostB { get; set; } = "";

    public string FontDisplay { get; set; } = "";

    public string FontSerif { get; set; } = "";

    public string FontBody { get; set; } = "";

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public Dictionary<string, Dictionary<string, bool>> Enabled { get; set; } = new();

    public LegacyPresetSettings Clone()
    {
        var copy = (LegacyPresetSettings)MemberwiseClone();
        copy.Enabled = CloneEnabled(Enabled);
        return copy;
    }

    public void ApplyTo(LegacyEngine.Project project)
    {
        project.Unify = Unify;
        project.Typeset = Typeset;
        project.Extra = Extra;
        project.Wa = Wa;
        project.Typo = Typo;
        project.Kinetic = Kinetic;
        project.Horror = Horror;
        project.Lang = string.IsNullOrWhiteSpace(Lang) ? "auto" : Lang;
        project.KeyBg = string.IsNullOrWhiteSpace(KeyBg) ? "off" : KeyBg;
        project.CenterFree = CenterFree;
        project.CenterDir = string.IsNullOrWhiteSpace(CenterDir) ? "tb" : CenterDir;
        project.CenterZone = string.IsNullOrWhiteSpace(CenterZone) ? "center" : CenterZone;
        project.CenterGap = CenterGap;

        var fx = project.Fx;
        fx.Motion = Clamp01(Motion);
        fx.Glitch = Clamp01(Glitch);
        fx.Chroma = Clamp01(Chroma);
        fx.Decor = Clamp01(Decor);
        fx.Density = Clamp01(Density);
        fx.Texture = Clamp01(Texture);
        fx.BgSwitch = Clamp01(BgSwitch);
        fx.Flash = Flash;
        fx.Koma = null;
        fx.OnTwos = false;
        fx.Hud = string.IsNullOrWhiteSpace(Hud) ? "auto" : Hud;
        fx.HideNo = HideNo;
        fx.HideTime = HideTime;

        if (Enabled.Count > 0)
            project.Enabled = CloneEnabled(Enabled);

        var colors = new JObject();
        if (ColorsEnabled)
        {
            colors["enabled"] = true;
            PutHex(colors, "bg", ColorBg);
            PutHex(colors, "fg", ColorFg);
            PutHex(colors, "sub", ColorSub);
        }

        if (AccentOn)
        {
            colors["accentOn"] = true;
            PutHex(colors, "accent", ColorAccent);
            PutHex(colors, "ghostA", ColorGhostA);
            PutHex(colors, "ghostB", ColorGhostB);
        }

        if (colors.Count > 0) project.Colors = colors;

        var fonts = new Dictionary<string, string>(StringComparer.Ordinal);
        PutFont(fonts, "display", FontDisplay);
        PutFont(fonts, "serif", FontSerif);
        PutFont(fonts, "body", FontBody);
        if (fonts.Count > 0) project.Fonts = fonts;
    }

    private static void PutHex(JObject target, string key, string? value)
    {
        var hex = (value ?? "").Trim();
        if (hex.Length is 4 or 7 && hex[0] == '#')
            target[key] = hex;
    }

    private static void PutFont(Dictionary<string, string> target, string role, string? key)
    {
        if (!string.IsNullOrWhiteSpace(key)) target[role] = key;
    }

    public void EnsureEnabled()
    {
        if (Enabled.Count > 0) return;
        Enabled = CloneEnabled(LegacyEngine.Reg.DefaultEnabled());
    }

    public static Dictionary<string, Dictionary<string, bool>> CloneEnabled(
        Dictionary<string, Dictionary<string, bool>> source)
    {
        var clone = new Dictionary<string, Dictionary<string, bool>>(StringComparer.Ordinal);
        foreach (var (group, items) in source)
            clone[group] = new Dictionary<string, bool>(items, StringComparer.Ordinal);
        return clone;
    }

    private static double Clamp01(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
}
