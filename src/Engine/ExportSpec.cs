using Newtonsoft.Json.Linq;

namespace JizuraLive.Engine;

// 导出勾选模型（移植自 frontend/src/jizura/export-spec.ts，逐行对齐，纯数据/纯函数、零依赖）：
// OutTab 的勾选组合 → 校验 → 可执行参数（ExportSpecDef）。导出编排（M4 的 export 层）只认 spec，
// 引擎与 Worker 只吃 spec 拆出来的原始选项——三层各改各的，互不牵连。
// MCP/agent-api 的旧 kind（勾选模型上线前的 9 种按钮）经 SpecFromLegacyKind 归一到同一份 spec。
//
// project.ExportCfg 落盘 JSON 的键名与 TS 完全一致：
//   format / visuals / subs / bg / bgStyle / audio / layered / toFile

// ---------------- 勾选状态（持久化在 project.exportCfg） ----------------

/// <summary>导出面板的勾选状态。字段名即 JSON 键（对齐 TS 的 ExportCfg interface）。</summary>
public sealed record ExportCfg(
    string Format,   // 'mp4' | 'webm' | 'png'
    bool Visuals,    // 渲染画面（歌词动画层）
    bool Subs,       // 云字幕烤入
    bool Bg,         // 背景（false = 透明底，仅 WebM/PNG）
    string BgStyle,  // 背景形态（bg=true 时生效；off=常规）：'off' | 'green' | 'black'
    bool Audio,      // 歌曲：MP4 内嵌 / 其它格式转另存 WAV
    bool Layered,    // PNG 分层输出（back/ front/ 双目录）
    bool ToFile)     // MP4 大视频直接写入文件
{
    public static ExportCfg Default { get; } =
        new("mp4", Visuals: true, Subs: false, Bg: true, BgStyle: "off",
            Audio: true, Layered: false, ToFile: false);
}

/// <summary>可执行导出参数：直接对应引擎/Worker 的原始选项（对齐 TS 的 ExportSpec interface）。</summary>
public sealed record ExportSpecDef(
    string Kind,             // 'mp4' | 'mp4file' | 'webm' | 'png'
    bool Transparent,
    string? Layer,           // 'front' | null（只画前景层）
    bool NoHud,
    bool Layers,             // png 分层（back/ front/）
    bool CustomDrawOnly,     // 纯字幕层（无渲染画面，只画字幕）
    bool BakeSubs,           // overlay 烤入字幕
    bool EmbedAudio,         // mp4 内嵌音轨
    string KeyBg)            // 'off' | 'green' | 'black'
{
    public JObject ToJObject() => new()
    {
        ["kind"] = Kind,
        ["transparent"] = Transparent,
        ["layer"] = Layer == null ? JValue.CreateNull() : (JToken)Layer,
        ["noHud"] = NoHud,
        ["layers"] = Layers,
        ["customDrawOnly"] = CustomDrawOnly,
        ["bakeSubs"] = BakeSubs,
        ["embedAudio"] = EmbedAudio,
        ["keyBg"] = KeyBg,
    };
}

/// <summary>BuildSpec 的环境事实（调用方从工程/状态取；纯模块不做副作用查询）。
/// SubOn = 「字幕」面板内容模式 ≠ 不显示，且已装载 KRC 逐字字幕数据。</summary>
public sealed record SpecCtx(bool SubOn, bool AudioLoaded);

/// <summary>BuildSpec 结果：ok=false 时 Error 是给 toast 的中文提示。</summary>
public sealed record SpecResult(bool Ok, ExportSpecDef? Spec, bool WavToo, string? Error)
{
    public static SpecResult Fail(string error) => new(false, null, false, error);
    public static SpecResult Good(ExportSpecDef spec, bool wavToo) => new(true, spec, wavToo, null);
}

public static class ExportSpec
{
    // ---------------- 表（对齐 TS 的 EXPORT_BG_STYLES / FORMATS 与 kind 说明） ----------------

    /// <summary>背景形态（EXPORT_BG_STYLES）：常规 / 绿幕 / 黑幕。</summary>
    public static readonly (string Value, string Label)[] BgStyles =
        { ("off", "常规"), ("green", "绿幕"), ("black", "黑幕") };

    /// <summary>格式单选（OutTab.vue 的 FORMATS）。</summary>
    public static readonly (string Value, string Label)[] Formats =
        { ("mp4", "MP4 视频"), ("webm", "WebM 视频"), ("png", "PNG 序列") };

    /// <summary>可执行的 kind 枚举与含义（给 MCP/文档/占位 UI 的说明性元数据）。</summary>
    public static readonly (string Kind, string Desc)[] KindDocs =
    {
        ("mp4", "MP4 视频（内存合成，字幕烤入、歌曲内嵌音轨，不透明整帧）"),
        ("mp4file", "MP4 大视频直存文件（边导出边写盘，长曲不占内存）"),
        ("webm", "WebM 视频（支持透明 alpha 底或常规成片；音轨另存 WAV）"),
        ("png", "PNG 序列（整帧/透明前景/纯字幕/分层 back+front 均可）"),
    };

    // ---------------- 勾选清洗（TS 的 normalizeCfg） ----------------

    /// <summary>脏输入 → 干净勾选（未知 format/bgStyle 归 mp4/off，非布尔回默认值）。</summary>
    public static ExportCfg NormalizeCfg(JObject? raw)
    {
        var o = raw;
        bool Bool(string k, bool d) => o?[k] is JValue { Type: JTokenType.Boolean } b ? (bool)b.Value : d;
        var format = o?["format"]?.ToString() is { } f && (f == "webm" || f == "png") ? f : "mp4";
        var bgStyle = o?["bgStyle"]?.ToString() is { } s && (s == "green" || s == "black") ? s : "off";
        return new ExportCfg(
            format,
            Bool("visuals", true),
            Bool("subs", false),
            Bool("bg", true),
            bgStyle,
            Bool("audio", true),
            Bool("layered", false),
            Bool("toFile", false));
    }

    /// <summary>从 project.ExportCfg 读（缺省回默认勾选）。</summary>
    public static ExportCfg CfgOf(Project? project) => NormalizeCfg(project?.ExportCfg);

    /// <summary>勾选写回 project.ExportCfg（先清洗再落，保持 JSON 键与 TS 一致）。</summary>
    public static void SetCfg(Project project, ExportCfg cfg)
        => project.ExportCfg = ToJObject(NormalizeCfg(ToJObject(cfg)));

    /// <summary>勾选 → project.exportCfg 的落盘形态。</summary>
    public static JObject ToJObject(ExportCfg c) => new()
    {
        ["format"] = c.Format,
        ["visuals"] = c.Visuals,
        ["subs"] = c.Subs,
        ["bg"] = c.Bg,
        ["bgStyle"] = c.BgStyle,
        ["audio"] = c.Audio,
        ["layered"] = c.Layered,
        ["toFile"] = c.ToFile,
    };

    // ---------------- 勾选 → spec（TS 的 buildSpec） ----------------

    /// <summary>UI 侧就能判定的勾选硬伤（OutTab 的 invalidMsg；空串 = 无问题）。</summary>
    public static string UiInvalidMessage(ExportCfg c)
    {
        if (!c.Visuals && !c.Subs)
            return "至少勾选「渲染画面」或「字幕」，否则没有可导出的内容";
        if (c.Format == "mp4" && (!c.Visuals || !c.Bg))
            return "MP4 不支持透明/无背景导出：请勾选背景（可用绿幕/黑幕形态），或改用 WebM / PNG 序列";
        return "";
    }

    /// <summary>
    /// 勾选组合 → 可执行 spec。返回 Error 时给 toast（运行时校验，含字幕面板未开启）。
    /// </summary>
    public static SpecResult BuildSpec(ExportCfg cfg, SpecCtx ctx)
    {
        if (!cfg.Visuals && !cfg.Subs)
            return SpecResult.Fail("至少勾选「渲染画面」或「字幕」，否则没有可导出的内容");
        if (cfg.Subs && !ctx.SubOn)
            return SpecResult.Fail("字幕渲染未开启或没有云字幕数据：请先在「字幕」面板选择要导出的字幕内容（翻译/音译），并确保已装载酷狗 KRC 逐字歌词");
        if (cfg.Format == "mp4" && (!cfg.Visuals || !cfg.Bg))
            return SpecResult.Fail("MP4 不支持透明/无背景导出：请改用 WebM 视频，或把背景勾上（可用绿幕/黑幕形态）");

        // mp4 大视频直存只在浏览器支持文件句柄时有意义，capability 由调用方折进 toFile
        var spec = new ExportSpecDef(
            Kind: "mp4", Transparent: false, Layer: null, NoHud: false,
            Layers: false, CustomDrawOnly: false, BakeSubs: cfg.Subs,
            EmbedAudio: false, KeyBg: cfg.Bg ? cfg.BgStyle : "off");

        switch (cfg.Format)
        {
            case "mp4":
                return SpecResult.Good(
                    spec with { Kind = cfg.ToFile ? "mp4file" : "mp4", EmbedAudio = cfg.Audio },
                    wavToo: false);
            case "webm":
            {
                // 勾背景=不透明成片（带风格背景与 HUD）；不勾=透明 alpha，且只画前景层；
                // 纯字幕层没有「背景」可言，永远透明底
                var frontOnly = cfg.Visuals && !cfg.Bg;
                var customDrawOnly = !cfg.Visuals;
                return SpecResult.Good(spec with
                {
                    Kind = "webm",
                    Transparent = customDrawOnly || !cfg.Bg,
                    Layer = frontOnly ? "front" : null,
                    NoHud = frontOnly,
                    CustomDrawOnly = customDrawOnly,
                    BakeSubs = cfg.Subs && cfg.Visuals,
                }, wavToo: cfg.Audio);
            }
            default:
            {
                // png：勾背景=不透明整帧（可选分层 back/front）；不勾=透明前景（frontOnly，无 HUD）；
                // 只勾字幕=透明底纯字幕序列。分层必须是透明 PNG（back/ front/ 都是拿来叠的）
                var layers = cfg.Layered && cfg.Visuals && cfg.Bg;
                var frontOnly = cfg.Visuals && !cfg.Bg && !layers;
                return SpecResult.Good(spec with
                {
                    Kind = "png",
                    Transparent = layers || !(cfg.Visuals && cfg.Bg),
                    Layer = frontOnly ? "front" : null,
                    NoHud = frontOnly,
                    Layers = layers,
                    CustomDrawOnly = !cfg.Visuals,
                    BakeSubs = cfg.Subs && cfg.Visuals,
                }, wavToo: cfg.Audio);
            }
        }
    }

    // ---------------- 旧 kind 归一（TS 的 specFromLegacyKind） ----------------

    /// <summary>
    /// MCP/agent-api 的旧 kind（本勾选模型上线前的 9 种按钮）→ 等价 spec，行为与旧版逐一对齐：
    /// 旧版里只有 mp4/mp4file 烤字幕（v0.10 字幕回归修复），webm/png 家族一律不带字幕。
    /// 未知 kind 返回 null。
    /// </summary>
    public static ExportSpecDef? SpecFromLegacyKind(string kind, Project? project)
    {
        static string KeyBgOf(Project? p)
            => p != null && (p.KeyBg == "green" || p.KeyBg == "black") ? p.KeyBg : "off";

        // 旧字段 includeAudio（!!project && project.includeAudio !== false）：C# 的 typed Project
        // 没有这个字段，字段缺失即视为 true（!== false 恒成立）
        var embedAudio = project != null;

        switch (kind)
        {
            case "mp4":
                return new ExportSpecDef("mp4", false, null, false, false, false, true,
                    embedAudio, KeyBgOf(project));
            case "mp4file":
                return new ExportSpecDef("mp4file", false, null, false, false, false, true,
                    embedAudio, KeyBgOf(project));
            case "webm":
                return new ExportSpecDef("webm", true, null, false, false, false, false, false, "off");
            case "webmfront":
                return new ExportSpecDef("webm", true, "front", true, false, false, false, false, "off");
            case "subwebm":
                return new ExportSpecDef("webm", true, null, false, false, true, true, false, "off");
            case "png":
                return new ExportSpecDef("png", false, null, false, false, false, false, false, KeyBgOf(project));
            case "pnga":
                return new ExportSpecDef("png", true, null, false, false, false, false, false, "off");
            case "pngl":
                return new ExportSpecDef("png", true, null, false, true, false, false, false, "off");
            case "pngf":
                return new ExportSpecDef("png", true, "front", true, false, false, false, false, "off");
            default:
                return null;
        }
    }
}
