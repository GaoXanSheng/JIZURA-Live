using System.Text.Json.Serialization;

namespace JizuraLive.Engine;

public sealed class LiveLayerOptions
{
    public bool Enabled { get; set; }
    public string SenderName { get; set; } = "VTubeStudioSpout";
    public double Alpha { get; set; } = 1;
    public double Scale { get; set; } = 1;
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public double Rotation { get; set; }
    public bool MirrorX { get; set; }
    public bool Cover { get; set; }
    public bool Premultiplied { get; set; }
    public double Blur { get; set; }
    public double Glow { get; set; }
    public double Hue { get; set; }

    [JsonIgnore]
    public int Revision { get; set; }
}

public sealed class PvOptions
{
    public const int MaxSources = 4;
    public bool Enabled { get; set; }
    public bool GlobalCamera { get; set; } = true;
    public bool SubtitleOnTop { get; set; } = true;
    public bool FollowCamera { get; set; } = true;
    public string AutoHotkeyId { get; set; } = "";
    public bool AutoHotkeyOnSong { get; set; }
    public List<LiveLayerOptions> Sources { get; set; } = new();
    public VtsDriveOptions VtsDrive { get; set; } = new();

    [JsonIgnore]
    public int Revision { get; set; }

}

public sealed class VtsDriveOptions
{
    public bool Enabled { get; set; } = true;
    public double Bounce { get; set; } = 0.05;
    public double Sway { get; set; } = 0.04;
    public double SwayRotation { get; set; } = 5;
    public double MouthWeight { get; set; }
    public double HeadWeight { get; set; }
    public double CameraFollow { get; set; }
    public string BeatHotkeyId { get; set; } = "";
    public int BeatHotkeyEvery { get; set; } = 4;

    [JsonIgnore]
    public int Revision { get; set; }
}
