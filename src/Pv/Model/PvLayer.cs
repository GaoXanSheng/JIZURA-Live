using Newtonsoft.Json;

namespace JizuraLive.Pv;

/// <summary>
/// PV 图层类别
/// </summary>
public enum PvLayerKind
{
    /// <summary>背景层（渐变、底纹、环境底色）</summary>
    Background,

    /// <summary>视觉素材/角色层（Spout 输入、立绘插画、手书分格）</summary>
    VisualAsset,

    /// <summary>核心动效歌词层（由 JizuraLive.Engine 核心离屏渲染提供）</summary>
    LyricPv,

    /// <summary>前景特效层（浮动粒子、HUD 装饰、前景遮罩）</summary>
    ForegroundFx,

    /// <summary>字幕层（KRC/LRC 逐字字幕，永远保证最上层可读性）</summary>
    Subtitle,
}

/// <summary>
/// 视差运镜配置（模拟三维焦平面摄像机位移）
/// </summary>
public sealed class PvParallaxConfig
{
    /// <summary>
    /// 视差深度系数：
    /// 0.0 = 无限远背景（不随镜头平移）；
    /// 0.35 = 远景背景；
    /// 0.85 = 中景主体（角色立绘/模型）；
    /// 1.0 = 标准焦点平面（核心歌词动效）；
    /// 1.35 = 前景浮动层（近大远快，近景掠过）。
    /// </summary>
    public double Depth { get; set; } = 1.0;

    /// <summary>是否跟随镜头旋转（角色层通常弱化旋转，以保持重心视觉平衡）</summary>
    public double RotationFactor { get; set; } = 1.0;

    /// <summary>是否跟随镜头缩放（近大远小）</summary>
    public double ScaleFactor { get; set; } = 1.0;
}

/// <summary>
/// 视觉环境融合配置（解决生硬贴片感与粗制滥造问题）
/// </summary>
public sealed class PvVisualBlendConfig
{
    /// <summary>环境光染色强度（0~1，0=原色直接贴，0.2=自然吃场景主色调氛围）</summary>
    public double AmbientTintIntensity { get; set; } = 0.18;

    /// <summary>背光泛光强度 0~1（深色背景下凸显角色轮廓）</summary>
    public double RimGlow { get; set; } = 0.15;

    /// <summary>接触阴影不透明度 0~1（浅色背景下提供立足感）</summary>
    public double DropShadow { get; set; } = 0.3;

    /// <summary>阴影偏移 Y（像素）</summary>
    public double ShadowOffsetY { get; set; } = 16.0;

    /// <summary>防露底安全扩展（0.0~0.2，默认 0.06 = 6% 画布超取景，防止相机震动出现黑边破绽）</summary>
    public double AntiBleedMargin { get; set; } = 0.06;
}

/// <summary>
/// 前景粒子层配置。非 null 即由粒子驱动本层（<see cref="PvLayerConfig.Particles"/>），
/// 参数是**逐帧读**的：改这里下一帧就生效，不需要重建提供者。
/// </summary>
public sealed class PvParticleConfig
{
    /// <summary>粒子种类：<c>sakura</c>（飘落花瓣）/ <c>sparkle</c>（原地闪烁星光）/ <c>bokeh</c>（大而淡的光斑）。</summary>
    public string Kind { get; set; } = "sakura";

    /// <summary>屏上同时存在的环境粒子数 0..400（上界见 <see cref="ParticleField.MaxAmbient"/>）。</summary>
    public double Density { get; set; } = 120;

    /// <summary>运动速度倍率 0.2..3。</summary>
    public double Speed { get; set; } = 1;

    /// <summary>粒子尺寸倍率 0.3..3。</summary>
    public double Size { get; set; } = 1;

    /// <summary>粒子颜色 <c>#rrggbb</c>。</summary>
    public string Color { get; set; } = "#ffc0d8";

    /// <summary>随机种子：换一个种子就是另一套完全不同的粒子分布。</summary>
    public int Seed { get; set; } = 1;

    /// <summary>爆发时刻（秒）：每次从画面中心向外喷一簇星光，寿命约 1.2s。</summary>
    public List<double> Bursts { get; set; } = new();
}

/// <summary>
/// 图层配置（独立场景文档中的每一层定义，可独立序列化与加载）
/// </summary>
public sealed class PvLayerConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "Layer";
    public PvLayerKind Kind { get; set; } = PvLayerKind.VisualAsset;
    public bool Enabled { get; set; } = true;

    /// <summary>图层不透明度 0~1</summary>
    public double Opacity { get; set; } = 1.0;

    /// <summary>基础几何变换：相对缩放 0.1~5</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>基础几何变换：水平偏移（画布宽度的比例，0=居中）</summary>
    public double OffsetX { get; set; }

    /// <summary>基础几何变换：垂直偏移（画布高度的比例，0=居中）</summary>
    public double OffsetY { get; set; }

    /// <summary>基础几何变换：旋转角度（度）</summary>
    public double Rotation { get; set; }

    /// <summary>水平镜像</summary>
    public bool MirrorX { get; set; }

    /// <summary>是否铺满裁切（true=Cover 铺满, false=Contain 留边完整可见）</summary>
    public bool Cover { get; set; }

    /// <summary>Spout 源名称（仅当 Kind == VisualAsset 时有效）</summary>
    public string? SpoutSenderName { get; set; } = "VTubeStudio";

    /// <summary>
    /// 磁盘图片路径（仅当 Kind == VisualAsset 时有效）。非空即由图片驱动本层，<see cref="SpoutSenderName"/> 随之失效：
    /// 一层只认一个源，两个源同时挂着只会让"这层到底画的是什么"无从判断（管线也按这条互斥建提供者）。
    /// </summary>
    public string? ImagePath { get; set; }

    /// <summary>
    /// 视频文件路径（仅当 Kind == VisualAsset 时有效，循环播放）。与 <see cref="ImagePath"/>/<see cref="SpoutSenderName"/>
    /// 三者互斥，同挂多个时优先级 Spout &gt; Video &gt; Image（判据见 PvPipeline.WantsSpout/WantsVideo/WantsImage）。
    /// </summary>
    public string? VideoPath { get; set; }

    /// <summary>
    /// 粒子配置（Kind 用 <see cref="PvLayerKind.ForegroundFx"/>）。非空即由粒子驱动本层：
    /// 管线按它建 <c>ParticleLayerProvider</c>，此时 <see cref="ImagePath"/>/<see cref="VideoPath"/>/
    /// <see cref="SpoutSenderName"/> 都不再参与判定。
    /// </summary>
    public PvParticleConfig? Particles { get; set; }

    /// <summary>
    /// 源像素是否已预乘 alpha。默认 true：VTS/Spout 发送端发的是预乘像素，
    /// 且 D2D 的 surface 位图只接受 Premultiplied（声明 Straight 建不出位图 → 该层永不出图）。
    /// </summary>
    public bool Premultiplied { get; set; } = true;

    /// <summary>视差运镜控制参数</summary>
    public PvParallaxConfig Parallax { get; set; } = new();

    /// <summary>环境光与高级融合参数</summary>
    public PvVisualBlendConfig VisualBlend { get; set; } = new();
}
