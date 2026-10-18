using Newtonsoft.Json;

namespace JizuraLive.Pv;

public enum PvLayerKind
{
    Background,

    VisualAsset,

    LyricPv,

    ForegroundFx,

    Decor,

    Subtitle,
}

public sealed class PvParallaxConfig
{
    public double Depth { get; set; } = 1.0;

    /// <summary>全局运镜跟随倍率；null = 沿用 Depth（旧工程兼容）。</summary>
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

public sealed class DecorConfig
{
    public List<string> Kinds { get; set; } = new();

    public double Density { get; set; } = 90;

    public double Speed { get; set; } = 1.0;

    public double Size { get; set; } = 1.0;

    public string Color { get; set; } = "#ffc0d8";

    public string AccentColor { get; set; } = "#8ad4ff";

    public int Seed { get; set; } = 1;

    public List<double> Bursts { get; set; } = new();
}

public sealed class PvLayerConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "Layer";
    public PvLayerKind Kind { get; set; } = PvLayerKind.VisualAsset;
    public bool Enabled { get; set; } = true;

    public double Opacity { get; set; } = 1.0;

    public double Scale { get; set; } = 1.0;

    public double OffsetX { get; set; }

    public double OffsetY { get; set; }

    public double Rotation { get; set; }

    public bool MirrorX { get; set; }

    public bool Cover { get; set; }

    public string? SpoutSenderName { get; set; } = "VTubeStudio";

    public string? ImagePath { get; set; }

    public string? VideoPath { get; set; }

    public DecorConfig? Decor { get; set; }

    public bool Premultiplied { get; set; }

    public PvParallaxConfig Parallax { get; set; } = new();

    public PvVisualBlendConfig VisualBlend { get; set; } = new();
}
