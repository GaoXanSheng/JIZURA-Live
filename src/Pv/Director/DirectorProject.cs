using JizuraLive.Render.Effects;
using JizuraLive.Vts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.ComponentModel;

namespace JizuraLive.Pv;

public sealed class DirectorProject
{
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string Name { get; set; } = "Untitled PV";

    public bool LegacyStageCamera { get; set; } = true;

    public bool LegacyStageTransitions { get; set; } = true;

    public string LegacyStyle { get; set; } = "noir";

    public int LegacySeed { get; set; } = 20260922;

    public double LegacyRenderScale { get; set; } = 0.6667;

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public LegacyPresetSettings Preset { get; set; } = new();

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public PvScene Scene { get; set; } = PvScene.CreateDefault();

    [JsonConverter(typeof(EffectGraphJsonConverter))]
    public EffectGraph Effects { get; set; } = new();

    public string? AudioPath { get; set; }

    public string? LrcText { get; set; }

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public Dictionary<int, long> LyricEnds { get; set; } = new();

    public List<CurveSave> Curves { get; set; } = new();

    [Newtonsoft.Json.JsonProperty(ObjectCreationHandling = Newtonsoft.Json.ObjectCreationHandling.Replace)]
    public CameraTrack CameraTrack { get; set; } = new();

    [Newtonsoft.Json.JsonProperty(ObjectCreationHandling = Newtonsoft.Json.ObjectCreationHandling.Replace)]
    public Dictionary<string, CameraTrack> LayerCameraTracks { get; set; } = new(StringComparer.Ordinal);

    public VtsCueTrack VtsCues { get; set; } = new();

    public VtsBaseSave? VtsBase { get; set; }

    public List<DesignSegment> DesignSegments { get; set; } = new();

    public List<LegacyCutOverride> LegacyCutOverrides { get; set; } = new();

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public EditorLayout EditorLayout { get; set; } = new();

    public void SyncCharacterCameraTrack()
    {
        Scene ??= PvScene.CreateDefault();
        LayerCameraTracks ??= new Dictionary<string, CameraTrack>(StringComparer.Ordinal);

        var character = Scene.CharacterLayer;
        if (character is not null)
            character.Parallax ??= new PvParallaxConfig();

        if (!LayerCameraTracks.TryGetValue(PvScene.CharacterLayerId, out var layerTrack)
            || layerTrack is null)
            return;

        if (!layerTrack.UserEdited
            && layerTrack.Shots.All(shot => !shot.Locked
                && !string.Equals(shot.Source, "user", StringComparison.OrdinalIgnoreCase)))
        {
            LayerCameraTracks.Remove(PvScene.CharacterLayerId);
        }
    }
}

public sealed class VtsBaseSave
{
    public double X { get; set; }

    public double Y { get; set; }

    public double Size { get; set; } = 1;

    public double Rotation { get; set; }
}

public sealed class LegacyCutOverride
{
    public int Line { get; set; } = -1;

    public double Start { get; set; }

    public string Layout { get; set; } = "";

    public string Enter { get; set; } = "";

    public string Exit { get; set; } = "";

    public string Treat { get; set; } = "";

    public Dictionary<string, double> LayoutParams { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, double> TreatParams { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, double> BgParams { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, double> CamParams { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> LayoutTexts { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> TreatTexts { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> BgTexts { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> CamTexts { get; set; } = new(StringComparer.Ordinal);
}

public sealed class EditorLayout
{
    public List<string> CollapsedTrackIds { get; set; } = new();

    public Dictionary<string, double> TrackHeights { get; set; } = new(StringComparer.Ordinal);

    public bool VtsStageDrive { get; set; } = true;

    public bool VtsTintOnTransitions { get; set; }

    public string AutoStyle { get; set; } = "dynamic";

    public double AutoIntensity { get; set; } = 0.6;

    public bool LegacyComposite { get; set; } = true;

    public List<string> DeletedAutoIds { get; set; } = new();
}

public sealed class CurveSave
{
    public string Address { get; set; } = "";

    public FloatCurve Curve { get; set; } = new();

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Layer { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
    [DefaultValue("auto")]
    public string Source { get; set; } = "user";

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
    [DefaultValue(false)]
    public bool Locked { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
    [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public CurveBlend Blend { get; set; } = CurveBlend.Add;
}

public sealed class EffectGraphJsonConverter : JsonConverter<EffectGraph>
{
    public override EffectGraph? ReadJson(JsonReader reader, Type objectType, EffectGraph? existingValue,
        bool hasExistingValue, JsonSerializer serializer) =>
        reader.TokenType == JsonToken.Null
            ? EffectGraphJson.Deserialize(null)
            : EffectGraphJson.Deserialize(JToken.Load(reader).ToString());

    public override void WriteJson(JsonWriter writer, EffectGraph? value, JsonSerializer serializer)
    {
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteRawValue(EffectGraphJson.Serialize(value));
    }
}
