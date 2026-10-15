using System.IO;
using JizuraLive.Render.Text;
using Newtonsoft.Json;

namespace JizuraLive.Pv;

/// <summary>
/// 独立 PV 场景文档（与编辑器 settings.json 彻底解耦，可独立存档、独立演进）
/// </summary>
public sealed class PvScene
{
    public const string DefaultFileName = "config/pv_scene.json";

    /// <summary>
    /// 默认角色层的 Id。场景模板与管线找角色层必须认同一个名字——
    /// 曾经一边写 <c>character</c>、一边写 <c>actor</c>，角色摘要路径因此永远是空的。
    /// </summary>
    public const string CharacterLayerId = "character";

    /// <summary>场景版本号</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>场景名称</summary>
    public string Name { get; set; } = "Default PV Stage";

    /// <summary>输出设计画布宽度</summary>
    public int Width { get; set; } = 1920;

    /// <summary>输出设计画布高度</summary>
    public int Height { get; set; } = 1080;

    /// <summary>默认帧率</summary>
    public int Fps { get; set; } = 60;

    /// <summary>全局三维视差运镜总开关（开启时各层按自身 Depth 表现不同位移与缩放）</summary>
    public bool GlobalParallaxEnabled { get; set; } = true;

    /// <summary>镜头基础值（持久化到工程）：追踪失效/未连接时的静息取景，也是追踪位移的基准。</summary>
    public PvCameraConfig Camera { get; set; } = new();

    /// <summary>全局自适应环境光调色与光影融合开关（杜绝生硬贴片感）</summary>
    public bool AmbientBlendEnabled { get; set; } = true;

    /// <summary>歌词层样式预设名（Classic / Neon / Outline / Minimal），未知名字按 Classic 画。</summary>
    public string? LyricStyleName { get; set; } = JizuraLive.Render.Text.LyricStyle.ClassicName;

    /// <summary>自定义歌词样式（null = 用 <see cref="LyricStyleName"/> 指定的预设）。</summary>
    public LyricStyle? LyricStyle { get; set; }

    /// <summary>防露底自适应安全边缘保护开关（镜头晃动时自动延展边缘，杜绝黑边穿帮）</summary>
    public bool AntiBleedGuardEnabled { get; set; } = true;

    /// <summary>
    /// 场景环境主色（<c>#rrggbb</c>）：图层做环境光染色时向它靠
    /// （<c>PvVisualBlendConfig.AmbientTintIntensity</c> 为权重）。通常取当前歌词工程的配色主色，
    /// 这样角色/素材才会"吃"到画面氛围。
    ///
    /// 存字符串而不是 <c>Color4</c>：后者是公共**字段**的结构体，Newtonsoft 默认只序列化属性，
    /// 直接存会变成 <c>{}</c>、读回全 0（实测踩过）。字符串还顺带让 pv.json 可读可手改。
    /// </summary>
    public string AmbientTint { get; set; } = "#ffffff";

    /// <summary>环境主色的解析结果（不参与序列化）。</summary>
    [JsonIgnore]
    public Vortice.Mathematics.Color4 AmbientTintColor => ParseHex(AmbientTint);

    /// <summary>解析 <c>#rgb</c> / <c>#rrggbb</c>；非法输入回落白色。</summary>
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

    /// <summary>
    /// 图层栈（从下至上合成）：
    /// 典型工业级层序：
    /// [0] Background    (底衬/环境渐变/底纹，Depth ~ 0.35)
    /// [1] VisualAsset   (角色/立绘/Spout输入，Depth ~ 0.85)
    /// [2] LyricPv       (核心动效歌词，Depth ~ 1.00)
    /// [3] ForegroundFx  (前景粒子/HUD高光，Depth ~ 1.35)
    /// [4] Subtitle      (压顶精准字幕，永远清晰可读)
    /// </summary>
    public List<PvLayerConfig> Layers { get; set; } = new();

    /// <summary>
    /// 空白场景：仅画布/帧率等基础设施，**零图层**。新工程用这个——
    /// 预置那套模板图层（角色/歌词/前景/字幕）等于替用户决定好画面结构，那不叫新建。
    /// </summary>
    public static PvScene CreateBlank() => new();

    /// <summary>创建默认的工业级 PV 场景模板</summary>
    public static PvScene CreateDefault()
    {
        var scene = new PvScene();

        // 1. 底衬层
        scene.Layers.Add(new PvLayerConfig
        {
            Id = "bg",
            Name = "Background Layer",
            Kind = PvLayerKind.Background,
            Parallax = new PvParallaxConfig { Depth = 0.30, RotationFactor = 0.4 },
        });

        // 2. 视觉主体/角色层（VTS / 插画 / 3D）
        scene.Layers.Add(new PvLayerConfig
        {
            Id = CharacterLayerId,
            Name = "Character Asset",
            Kind = PvLayerKind.VisualAsset,
            SpoutSenderName = "VTubeStudioSpout", // 必须与真实发送端同名：VTS 发的是 "VTubeStudioSpout"（不是 "VTubeStudio"），对不上就永远收不到帧；SpoutIn.Normalize 的兜底名也是它
            Parallax = new PvParallaxConfig { Depth = 0.85, RotationFactor = 0.6 },
            VisualBlend = new PvVisualBlendConfig
            {
                AmbientTintIntensity = 0.20,
                DropShadow = 0.35,
                RimGlow = 0.15,
                AntiBleedMargin = 0.08,
            },
        });

        // 3. 核心动效歌词层
        scene.Layers.Add(new PvLayerConfig
        {
            Id = "lyrics_pv",
            Name = "Kinetic Typography PV",
            Kind = PvLayerKind.LyricPv,
            Parallax = new PvParallaxConfig { Depth = 1.00, RotationFactor = 1.0 },
        });

        // 4. 前景特效层
        scene.Layers.Add(new PvLayerConfig
        {
            Id = "foreground_fx",
            Name = "Foreground FX & Particles",
            Kind = PvLayerKind.ForegroundFx,
            Enabled = false, // 按需开启
            Parallax = new PvParallaxConfig { Depth = 1.35, RotationFactor = 1.2 },
        });

        // 5. 顶层字幕层
        scene.Layers.Add(new PvLayerConfig
        {
            Id = "subtitle_top",
            Name = "Top Subtitle Layer",
            Kind = PvLayerKind.Subtitle,
            Parallax = new PvParallaxConfig { Depth = 0.0, RotationFactor = 0.0 }, // 字幕不受镜头摇晃影响，保持绝对可读
        });

        return scene;
    }

    /// <summary>序列化为 JSON 字符串</summary>
    public string ToJson() => JsonConvert.SerializeObject(this, Formatting.Indented);

    /// <summary>从 JSON 字符串反序列化</summary>
    public static PvScene? FromJson(string json) => JsonConvert.DeserializeObject<PvScene>(json);

    /// <summary>保存为 JSON 文件</summary>
    public void SaveToFile(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonConvert.SerializeObject(this, Formatting.Indented);
        File.WriteAllText(filePath, json);
    }

    /// <summary>从 JSON 文件加载，文件不存在时返回默认模板</summary>
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

/// <summary>
/// 镜头基础值（持久化到工程）：追踪失效/未连接时的静息取景，也是追踪位移的基准。
/// 不能由 PvCamState 兼任——那是每帧运行态，把运行态当已保存常量会导致"重启后镜头随机位"。
/// </summary>
public sealed class PvCameraConfig
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Scale { get; set; } = 1.0;
}
