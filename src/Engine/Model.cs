using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JizuraLive.Engine;

// 引擎数据模型。字段名与 JS 保持一致（JSON 序列化直接对齐 .jizuralive / MCP / AE 导出契约）。
// 松散的 pack 专用参数（params/bgP/treatP/camP/transP）保持 JObject：
// 与前端 duck-typed 对象一一对应，锁定行快照（lockedCuts）也按原样 JSON 往返。

// ---------------- 工程（frontend/src/jizura/project.ts 的 mergeProject 输入输出） ----------------

public sealed class Project
{
    public int Version { get; set; } = 1;

    /// <summary>歌词行序版本：2 = 书写序围绕 LRC 戳排序（v0.10）。迁移标记。</summary>
    public int TimingOrder { get; set; } = 2;

    /// <summary>一键随机的主题（J.THEMES 的 key），只有一键随机读它。</summary>
    public string? ThemeId { get; set; }

    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Lyrics { get; set; } = "";

    public string Style { get; set; } = "noir";
    public string? Mood { get; set; }

    /// <summary>随机可以选用初版之后追加的手法（追加分）。</summary>
    public bool Extra { get; set; }

    /// <summary>和風纹样（提灯・障子・家紋…），在 extra 之后生效。</summary>
    public bool Wa { get; set; } = true;

    /// <summary>独立于 extra 的手法集：ホラー（同时会启用ホラー情绪）。</summary>
    public bool Horror { get; set; }

    /// <summary>文字PV系 typographic 手法。</summary>
    public bool Typo { get; set; } = true;

    /// <summary>キネティック 手法。</summary>
    public bool Kinetic { get; set; } = true;

    /// <summary>歌词语言: auto | ja | zh-Hant | zh-Hans | ko | en。</summary>
    public string Lang { get; set; } = "auto";

    /// <summary>抠像合成背景: off | green | black。</summary>
    public string KeyBg { get; set; } = "off";

    /// <summary>統一感：段落调色板、重复行同款、キメ、モーフ、太さ。</summary>
    public bool Unify { get; set; }

    /// <summary>文字整列：假名疏排、小粒子/首字放大、西文字号、提前 0.2s、整体收敛。</summary>
    public bool Typeset { get; set; }

    /// <summary>竖屏中央留白方向: tb = 上下, lr = 左右。</summary>
    public string CenterDir { get; set; } = "tb";

    /// <summary>中央を空ける：切分排进两侧条带，中间留给角色。</summary>
    public bool CenterFree { get; set; }

    /// <summary>留白占画面宽/高的百分比（10..70，设置页自定义）；缺省 null 用内置固定比例。</summary>
    public double? CenterGap { get; set; }

    /// <summary>留白位置: center / left / right / top / bottom。</summary>
    public string CenterZone { get; set; } = "center";

    public uint Seed { get; set; } = 20260922;

    public string Aspect { get; set; } = "16:9";
    public int Res { get; set; } = 1080;
    public int Fps { get; set; } = 24;

    public FxSettings Fx { get; set; } = new();
    public TimingSettings Timing { get; set; } = new();

    /// <summary>组 → 键 → 开关。没显式关掉的都算开（老工程里新手法包的条目自动可用）。</summary>
    public Dictionary<string, Dictionary<string, bool>> Enabled { get; set; } = new();

    /// <summary>逐行覆盖（line → 任意覆盖字段：layout/enter/exit/hold/bg/decor/treat/cam/trans/cuts/single/
    /// lock/lockedSeed/lockedCuts/cutTech/cutLayouts/cutQuiet/cutTime/seed）。结构松散，保持 JObject。</summary>
    public Dictionary<string, JObject> Overrides { get; set; } = new();

    /// <summary>随机/重抽不能动的组和值（只在 UI 侧用）。plain key → true。</summary>
    public LocksSettings Locks { get; set; } = new();

    /// <summary>用户配色覆盖：enabled + #rrggbb。松散保持 JObject（mergeProject 白名单清洗）。</summary>
    public JObject Colors { get; set; } = new() { ["enabled"] = false };

    /// <summary>字体三角色（main/sub/rubi 等）→ 字体 key。</summary>
    public Dictionary<string, string> Fonts { get; set; } = new();

    public List<UserFont> UserFonts { get; set; } = new();

    /// <summary>音频文件名（如 song.mp3），对位前端 project.audioName。</summary>
    public string? AudioName { get; set; }

    /// <summary>音频本地绝对路径或缓存标识。</summary>
    public string? AudioPath { get; set; }

    /// <summary>音频特效配置（预览+导出共用）：{ units: [...], preset? }，J.sanitizeAudioFx 清洗。</summary>
    public JObject? AudioFx { get; set; }

    /// <summary>导出勾选（export-spec 勾选模型）。M4 接入 typed 模型前保持 JObject。</summary>
    public JObject? ExportCfg { get; set; }

    /// <summary>导出范围（选段）：{from,to} = 0 基切分索引（本 fork 的存储单位，上游按行），
    /// null = 整片。预览的「选段循环」档与导出裁剪都读它（对位 project.exportRange）。</summary>
    public JObject? ExportRange { get; set; }

    [JsonIgnore]
    public static readonly string SampleLyrics =
        "夜明けの色を/覚えてる\nほどけた声が遠くで鳴った\nねえ、まだ間に合うかな\n*透明*なままじゃ終われない!";

    /// <summary>J.defaultProject()：Enabled 映射依赖注册表已填充，调用时机在引擎装配完成后。</summary>
    public static Project NewDefault()
    {
        var p = new Project
        {
            Lyrics = SampleLyrics,
            Enabled = Reg.DefaultEnabled()
        };
        return p;
    }
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
    public bool OnTwos { get; set; } = true;
    public int Koma { get; set; } = 12;
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

    /// <summary>行 index（字符串键）→ 手工设定时刻（打点/拖动优先于 LRC）。</summary>
    public Dictionary<string, double> LineTimes { get; set; } = new();
    public double LineScale { get; set; } = 1;

    /// <summary>时长是否取音频长度（JS 里 useAudioLength !== false，缺省开）。</summary>
    public bool? UseAudioLength { get; set; }
}

public sealed class LocksSettings
{
    public Dictionary<string, bool> Tech { get; set; } = new();
    public Dictionary<string, bool> Params { get; set; } = new();
}

public sealed class UserFont
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Family { get; set; } = "";
    public int Weight { get; set; } = 400;
}

// ---------------- plan（08_planner.js 的 J.plan 输出） ----------------

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

    /// <summary>'green' | 'black' | null，抠像合成背景。</summary>
    public string? KeyBg { get; set; }

    public bool CenterFree { get; set; }
    public Zone[]? Zones { get; set; }
    public bool Typeset { get; set; }
    public bool Unify { get; set; }

    /// <summary>歌词语言（auto → 检测结果）。</summary>
    public string Lang { get; set; } = "ja";

    public float[]? Energy { get; set; }
    public double EnergyRate { get; set; }
    public float[]? Pitch { get; set; }
    public double PitchRate { get; set; }

    /// <summary>每个打点来自哪个切分（JS 的 WeakMap evOwner；锁定快照用）。</summary>
    [JsonIgnore]
    public readonly Dictionary<PlanEvent, Cut> EvOwner = new();
}

public sealed class PlanLine
{
    public int Index { get; set; }

    /// <summary>源文本行号（歌词原文里的物理行）。</summary>
    public int Src { get; set; }

    public double? Lrc { get; set; }
    public string Text { get; set; } = "";
    public bool Interlude { get; set; }
    public double? Secs { get; set; }
    public double Start { get; set; }
    public double End { get; set; }

    /// <summary>可见截止（比 End 早，超长行的切分只铺到 visEnd）。</summary>
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

    /// <summary>整行原文（切分只是行的一部分时仍保留全行，UI 显示用）。</summary>
    public string LineText { get; set; } = "";

    public string? Note { get; set; }

    /// <summary>-1 = 片头标题卡。</summary>
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

    /// <summary>中央を空ける：这个切分居住的侧边条带。</summary>
    public Zone? Zone { get; set; }

    /// <summary>该切分对应的原始词组文本（锁定快照用）。</summary>
    public string? Utext { get; set; }

    /// <summary>モーフ：同一行的下一部分从这一部分里长出来。</summary>
    public JObject? Morph { get; set; }

    public bool Kime { get; set; }
    public bool WeightGrow { get; set; }

    /// <summary>中央を空ける的孪生切分（另一半），渲染时一起画。</summary>
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

/// <summary>侧边条带（中央を空ける），设计像素。</summary>
public sealed class Zone
{
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }

    /// <summary>left / right / top / bottom。</summary>
    public string Side { get; set; } = "";
}

// ---------------- 音频（plan 的输入；与 C# AudioAnalyzer 输出对齐） ----------------

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
