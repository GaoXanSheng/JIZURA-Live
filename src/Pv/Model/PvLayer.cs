using Newtonsoft.Json;

namespace JizuraLive.Pv;

public enum PvLayerKind
{
    Background = 0,

    VisualAsset = 1,

    ForegroundFx = 3,

    Subtitle = 5,
}

public sealed class PvParallaxConfig
{
    public double Depth { get; set; } = 1.0;

        public double? Follow { get; set; }

    public double RotationFactor { get; set; } = 1.0;

    public double ScaleFactor { get; set; } = 1.0;

    public double Defocus { get; set; } = 1.0;
}

public sealed class PvVisualBlendConfig
{
    public double AmbientTintIntensity { get; set; } = 0.18;

    public double RimGlow { get; set; } = 0.15;

    public double DropShadow { get; set; } = 0.3;

    public double ShadowOffsetY { get; set; } = 16.0;

    public double AntiBleedMargin { get; set; } = 0.06;
}

public sealed class PvLayerConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "Layer";
    public PvLayerKind Kind { get; set; } = PvLayerKind.VisualAsset;
    public bool Enabled { get; set; } = true;
    public bool Locked { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public bool? Topmost { get; set; }

    public double Opacity { get; set; } = 1.0;

    public double Scale { get; set; } = 1.0;

    public double OffsetX { get; set; }

    public double OffsetY { get; set; }

    public double Rotation { get; set; }

    public bool MirrorX { get; set; }

    public bool Cover { get; set; }

    public string? SpoutSenderName { get; set; } = "VTubeStudio";

    public string? AssetPath { get; set; }

    public bool Premultiplied { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public bool? VtsCharacter { get; set; }

    public PvParallaxConfig Parallax { get; set; } = new();

    public PvVisualBlendConfig VisualBlend { get; set; } = new();
}
