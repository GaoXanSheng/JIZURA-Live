using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JizuraLive.LegacyEngine;

public sealed class Project
{

    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Lyrics { get; set; } = "";

    public string Style { get; set; } = "noir";

        public bool Extra { get; set; }

        public bool Wa { get; set; } = true;

        public bool Horror { get; set; }

        public bool Typo { get; set; } = true;

        public bool Kinetic { get; set; } = true;

        public string Lang { get; set; } = "auto";

        public string KeyBg { get; set; } = "off";

        public bool Unify { get; set; }

        public bool Typeset { get; set; }

        public string CenterDir { get; set; } = "tb";

        public bool CenterFree { get; set; }

        public double? CenterGap { get; set; }

        public string CenterZone { get; set; } = "center";

        public bool SmartAvoid { get; set; }

        [JsonIgnore]
        public Func<double, double, int, int, IReadOnlyList<Zone>>? AvoidZones { get; set; }

    public uint Seed { get; set; } = 20260922;

    public string Aspect { get; set; } = "16:9";
    public int Res { get; set; } = 1080;
    public int Fps { get; set; } = 24;

    public FxSettings Fx { get; set; } = new();
    public TimingSettings Timing { get; set; } = new();

        public Dictionary<string, Dictionary<string, bool>> Enabled { get; set; } = new();

        public Dictionary<string, JObject> Overrides { get; set; } = new();

        public JObject Colors { get; set; } = new() { ["enabled"] = false };

        public Dictionary<string, string> Fonts { get; set; } = new();

        public string? AudioName { get; set; }

        public string? AudioPath { get; set; }

    [JsonIgnore]
    public static readonly string SampleLyrics =
        "夜明けの色を/覚えてる\nほどけた声が遠くで鳴った\nねえ、まだ間に合うかな\n*透明*なままじゃ終われない!";

    }

public sealed class FxSettings
{
    public double Motion { get; set; } = 0.7;
    public double Glitch { get; set; } = 0.55;
    public double Chroma { get; set; } = 0.7;
    public double Decor { get; set; } = 0.5;
    public double Density { get; set; } = 0.55;
    public double Texture { get; set; } = 0.6;
    public bool Flash { get; set; } = true;
        public int? Koma { get; set; }
        public bool OnTwos { get; set; }
    public string Hud { get; set; } = "auto";
    public double BgSwitch { get; set; } = 0.35;
    public bool HideNo { get; set; }
    public bool HideTime { get; set; }
}

public sealed class TimingSettings
{
    public double Bpm { get; set; }
    public double Offset { get; set; } = 0.4;
    public bool Snap { get; set; } = true;
    public double Tail { get; set; } = 0.9;

        public Dictionary<string, double> LineTimes { get; set; } = new();
    public double LineScale { get; set; } = 1;

        public bool? UseAudioLength { get; set; }
}

public sealed class Plan
{
    public int Version { get; set; } = 1;
    public string Generator { get; set; } = "JIZURA";
    public string? AppVersion { get; set; }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public int W { get; set; }
    public int H { get; set; }
    public int Fps { get; set; } = 24;
    public double Duration { get; set; }
    public string StyleKey { get; set; } = "";

    [JsonIgnore]
    public StyleDef Style { get; set; } = null!;

    public FxSettings Fx { get; set; } = new();
    public uint Seed { get; set; }

    public List<PlanLine> Lines { get; set; } = new();
    public List<Cut> Cuts { get; set; } = new();
    public List<PlanEvent> Events { get; set; } = new();
    public double[] Beats { get; set; } = Array.Empty<double>();

    public bool Hud { get; set; }

        public string? KeyBg { get; set; }

    public bool CenterFree { get; set; }
    public Zone[]? Zones { get; set; }
    public bool Typeset { get; set; }
    public bool Unify { get; set; }

        public string Lang { get; set; } = "ja";

    public float[]? Energy { get; set; }
    public double EnergyRate { get; set; }
    public float[]? Pitch { get; set; }
    public double PitchRate { get; set; }

        [JsonIgnore]
    public readonly Dictionary<PlanEvent, Cut> EvOwner = new();
}

public sealed class PlanLine
{
    public int Index { get; set; }

        public int Src { get; set; }

    public double? Lrc { get; set; }
    public string Text { get; set; } = "";
    public bool Interlude { get; set; }
    public double? Secs { get; set; }
    public double Start { get; set; }
    public double End { get; set; }

        public double VisEnd { get; set; }

    public string? Note { get; set; }
    public bool Impact { get; set; }
    public string[] Emph { get; set; } = Array.Empty<string>();
    public string[]? Chunks { get; set; }
    public uint Seed { get; set; }
}

public sealed class Cut
{
    public int Index { get; set; }
    public string Text { get; set; } = "";

        public string LineText { get; set; } = "";

    public string? Note { get; set; }

        public int Line { get; set; }
    public double Start { get; set; }
    public double End { get; set; }
    public double Dur { get; set; }
    public string Layout { get; set; } = "center";
    public string Enter { get; set; } = "blur";
    public string Exit { get; set; } = "blur";
    public string Hold { get; set; } = "still";
    public double InDur { get; set; } = 0.3;
    public double OutDur { get; set; } = 0.25;
    public double Stagger { get; set; } = 0.04;

    public JObject Params { get; set; } = new();
    public List<DecorSpec> Decor { get; set; } = new();
    public int Scheme { get; set; }
    public uint Seed { get; set; }
    public bool Emph { get; set; }
    public bool Recap { get; set; }
    public string[] Words { get; set; } = Array.Empty<string>();

    public string Treat { get; set; } = "none";
    public JObject TreatP { get; set; } = new();
    public string Bg { get; set; } = "none";
    public JObject BgP { get; set; } = new();
    public string Cam { get; set; } = "push";
    public JObject CamP { get; set; } = new();

    public string? Trans { get; set; }
    public JObject TransP { get; set; } = new();
    public double TransDur { get; set; }

        public Zone? Zone { get; set; }

        public string? Utext { get; set; }

        public JObject? Morph { get; set; }

    public bool Kime { get; set; }
    public bool WeightGrow { get; set; }

        [JsonIgnore]
    public Cut? Companion { get; set; }
}

public sealed class DecorSpec
{
    public string Id { get; set; } = "";
    public int Seed { get; set; }
    public int N { get; set; }
    public bool Right { get; set; }
    public bool Low { get; set; }
    public bool Accent { get; set; }
    public bool Corner { get; set; }
    public bool Big { get; set; }
    public string Mode { get; set; } = "count";
    public int From { get; set; }
    public int To { get; set; }
    public int V { get; set; }
    public double R { get; set; }
}

public sealed class PlanEvent
{
    public double T { get; set; }
    public string Type { get; set; } = "";
    public double Amp { get; set; }
    public double Dur { get; set; }
}

public sealed class Zone
{
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }

        public string Side { get; set; } = "";
}

public sealed class AudioData
{
    public double Duration { get; set; }
    public double Bpm { get; set; }
    public double[] Beats { get; set; } = Array.Empty<double>();
    public float[]? Energy { get; set; }
    public double EnergyRate { get; set; }
    public float[]? Peaks { get; set; }
    public float[]? Pitch { get; set; }
    public double PitchRate { get; set; }
}
