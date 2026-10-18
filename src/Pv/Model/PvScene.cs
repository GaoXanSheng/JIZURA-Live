using System.IO;
using JizuraLive.Render.Text;
using Newtonsoft.Json;

namespace JizuraLive.Pv;

public sealed class PvScene
{
    public const string DefaultFileName = "config/pv_scene.json";

    public const string CharacterLayerId = "character";

    public int SchemaVersion { get; set; } = 1;

    public string Name { get; set; } = "Default PV Stage";

    public int Width { get; set; } = 1920;

    public int Height { get; set; } = 1080;

    public int Fps { get; set; } = 60;

    public bool GlobalParallaxEnabled { get; set; } = true;

    public PvCameraConfig Camera { get; set; } = new();

    private PvFrameState _frame = new();

    [JsonIgnore]
    public PvFrameState Frame => Volatile.Read(ref _frame);

    public void PublishFrame(PvFrameState frame) => Volatile.Write(ref _frame, frame);

    public bool AmbientBlendEnabled { get; set; } = true;

    public double Atmosphere { get; set; } = 0.5;

    public string? LyricStyleName { get; set; } = JizuraLive.Render.Text.LyricStyle.ClassicName;

    public bool AntiBleedGuardEnabled { get; set; } = true;

    public string AmbientTint { get; set; } = "#ffffff";

    [JsonIgnore]
    public Vortice.Mathematics.Color4 AmbientTintColor => ParseHex(AmbientTint);

    public static Vortice.Mathematics.Color4 ParseHex(string? hex)
    {
        var t = (hex ?? "").Trim().TrimStart('#');
        if (t.Length == 3) t = $"{t[0]}{t[0]}{t[1]}{t[1]}{t[2]}{t[2]}";
        if (t.Length < 6) return new Vortice.Mathematics.Color4(1f, 1f, 1f, 1f);

        static float C(string s, int i) =>
            int.TryParse(s.AsSpan(i, 2), System.Globalization.NumberStyles.HexNumber, null, out var v)
                ? v / 255f : 1f;

        return new Vortice.Mathematics.Color4(C(t, 0), C(t, 2), C(t, 4), 1f);
    }

    public List<PvLayerConfig> Layers { get; set; } = new();

    [JsonIgnore]
    public PvLayerConfig? CharacterLayer
    {
        get
        {
            PvLayerConfig? fallback = null;
            foreach (var layer in Layers)
            {
                if (layer is null) continue;
                if (string.Equals(layer.Id, CharacterLayerId, StringComparison.Ordinal)) return layer;
                if (fallback is null && layer.Kind == PvLayerKind.VisualAsset
                    && !string.IsNullOrWhiteSpace(layer.SpoutSenderName))
                    fallback = layer;
            }

            return fallback;
        }
    }

    public static PvScene CreateBlank() => new();

    public static PvScene CreateDefault()
    {
        var scene = new PvScene();

        scene.Layers.Add(new PvLayerConfig
        {
            Id = CharacterLayerId,
            Name = "Character Asset",
            Kind = PvLayerKind.VisualAsset,
            SpoutSenderName = "VTubeStudioSpout",
            Premultiplied = false,
            Parallax = new PvParallaxConfig { Depth = 0.0, RotationFactor = 0.0, ScaleFactor = 0.0 },
            VisualBlend = new PvVisualBlendConfig
            {
                AmbientTintIntensity = 0.20,
                DropShadow = 0,
                RimGlow = 0,
                AntiBleedMargin = 0.08,
            },
        });

        return scene;
    }

    public string ToJson() => JsonConvert.SerializeObject(this, Formatting.Indented);

    public static PvScene? FromJson(string json) => JsonConvert.DeserializeObject<PvScene>(json);

    public void SaveToFile(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonConvert.SerializeObject(this, Formatting.Indented);
        File.WriteAllText(filePath, json);
    }

    public static PvScene LoadFromFile(string filePath)
    {
        if (!File.Exists(filePath))
            return CreateDefault();

        try
        {
            var json = File.ReadAllText(filePath);
            var scene = JsonConvert.DeserializeObject<PvScene>(json);
            return scene ?? CreateDefault();
        }
        catch
        {
            return CreateDefault();
        }
    }
}

public sealed class PvCameraConfig
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Scale { get; set; } = 1.0;

    public double Rotation { get; set; }
}
