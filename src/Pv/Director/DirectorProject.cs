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

    public List<CurveSave> Curves { get; set; } = new();

    [Newtonsoft.Json.JsonProperty(ObjectCreationHandling = Newtonsoft.Json.ObjectCreationHandling.Replace)]
    public CameraTrack CameraTrack { get; set; } = new();

    [Newtonsoft.Json.JsonProperty(ObjectCreationHandling = Newtonsoft.Json.ObjectCreationHandling.Replace)]
    public Dictionary<string, CameraTrack> LayerCameraTracks { get; set; } = new(StringComparer.Ordinal);

    public VtsCueTrack VtsCues { get; set; } = new();

    public List<DesignSegment> DesignSegments { get; set; } = new();

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

public sealed class EditorLayout
{
    public double CanvasZoom { get; set; } = 1;

    public double CanvasPanX { get; set; }

    public double CanvasPanY { get; set; }

    public List<string> SelectedNodeIds { get; set; } = new();

    public double TimelineZoom { get; set; } = 1;

    public long TimelineScrollTicks { get; set; }

    public List<string> CollapsedTrackIds { get; set; } = new();

    public Dictionary<string, double> TrackHeights { get; set; } = new(StringComparer.Ordinal);

    public bool VtsStageDrive { get; set; } = true;
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
