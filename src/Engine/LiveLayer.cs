using Newtonsoft.Json;
using SkiaSharp;

namespace JizuraLive.Engine;

/// <summary>
/// 外部实时层参数（VTS 等 Spout 输入的合成方式）：UI 线程改、渲染线程每帧读，只有原始类型读写，
/// 不加锁——最坏读到「上一次拖动」的组合，不会撕裂出非法状态。
/// 直接序列化进 settings.json（PV 模式：<see cref="PvOptions.Sources"/> 的每一路）。
/// </summary>
public sealed class LiveLayerOptions
{
    /// <summary>是否启用本路源（关 = 完全不接收，零开销）。</summary>
    public bool Enabled { get; set; }

    /// <summary>要接收的 Spout 发送端名（VTS 的 Spout2 输出名，区分大小写）。</summary>
    public string SenderName { get; set; } = "VTubeStudioSpout";

    /// <summary>不透明度 0~1。</summary>
    public double Alpha { get; set; } = 1;

    /// <summary>相对「contain/cover 基准尺寸」的缩放 0.1~4。</summary>
    public double Scale { get; set; } = 1;

    /// <summary>水平偏移（画布宽的比例，0 = 居中；+1 = 右移一个画布宽）。</summary>
    public double OffsetX { get; set; }

    /// <summary>垂直偏移（画布高的比例，0 = 居中）。</summary>
    public double OffsetY { get; set; }

    /// <summary>旋转（度，绕目标矩形中心，正 = 顺时针）。</summary>
    public double Rotation { get; set; }

    /// <summary>水平镜像。</summary>
    public bool MirrorX { get; set; }

    /// <summary>true = 铺满（裁切）；false = 完整可见（留边）。</summary>
    public bool Cover { get; set; }

    /// <summary>源已预乘 alpha（源是 Skia/Unity 预乘输出时勾选；勾错的表现是边缘过亮或过暗）。</summary>
    public bool Premultiplied { get; set; }

    /// <summary>模糊半径（工程设计像素，0 = 关）。</summary>
    public double Blur { get; set; }

    /// <summary>自发光强度 0~1（层自身颜色的模糊泛光，0 = 关）。</summary>
    public double Glow { get; set; }

    /// <summary>色相偏移（度，-180~180，0 = 原色）。</summary>
    public double Hue { get; set; }

    /// <summary>
    /// 参数修订号：UI 改任何字段时 +1。暂停态下没有新画面时，预览泵靠它知道「这一帧得重出」
    /// （画面内容没变但合成参数变了）。泵侧不需要——有实时层时本来就逐帧出图。不落盘。
    /// </summary>
    [JsonIgnore]
    public int Revision { get; set; }
}

/// <summary>
/// PV 模式（多路 Spout 源实时合成 + 推流）参数：UI 线程改、渲染线程每拍读。
/// 与 <see cref="LiveLayerOptions"/> 同一套无锁纪律——原始类型与列表引用直接读，不加锁。
/// 列表增删（UI 点「添加/删除源」）在渲染侧用「每拍 ToArray 快照 + 逐项判空」容忍：
/// 最坏某一拍少画/多画一路，绝不让渲染线程撞上集合修改。
/// 直接序列化进 settings.json（<see cref="JizuraLive.App.Settings.Pv"/>）。
/// </summary>
public sealed class PvOptions
{
    /// <summary>PV 源列表上限（UI 层执行；模型不设限，老配置原样读进来）。</summary>
    public const int MaxSources = 4;

    /// <summary>是否启用多路源合成（关 = 全部不接收，零开销）。</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 整幅合成一起吃镜头（推荐）：背景定义 + 歌词 + 人物 + HUD 由当前切分的同一台摄影机拍下，
    /// cut 切换时整幅画面一起切镜。关掉 = 老路径：镜头只作用于歌词，人物是否跟随看 <see cref="FollowCamera"/>。
    /// </summary>
    public bool GlobalCamera { get; set; } = true;

    /// <summary>字幕层烤进主通道最上层（人物之上）：单路推流的画面里字幕永远压着人物。
    /// 关掉 = 只走独立的 -LYRICS 通道（OBS 自己叠）。</summary>
    public bool SubtitleOnTop { get; set; } = true;

    /// <summary>源跟随歌曲镜头动效（仅 <see cref="GlobalCamera"/> 关闭时生效）。</summary>
    public bool FollowCamera { get; set; } = true;

    /// <summary>换歌时自动触发的 VTS 热键 ID（空 = 不触发；配合 <see cref="AutoHotkeyOnSong"/>）。</summary>
    public string AutoHotkeyId { get; set; } = "";

    /// <summary>云歌单换歌时触发 <see cref="AutoHotkeyId"/>（角色换表情/动作跟着歌走）。</summary>
    public bool AutoHotkeyOnSong { get; set; }

    /// <summary>源槽（顺序 = 合成层次：先画的在下）。参数复用 <see cref="LiveLayerOptions"/>。</summary>
    public List<LiveLayerOptions> Sources { get; set; } = new();

    /// <summary>
    /// 参数修订号：UI 改任何字段/增删源时 +1。暂停态下没有新画面时，预览泵靠它知道「这一帧得重出」。
    /// 泵侧不需要——有实时层时本来就逐帧出图。不落盘。
    /// </summary>
    [JsonIgnore]
    public int Revision { get; set; }

    /// <summary>
    /// 渲染侧的一份稳定快照（每拍取；UI 增删源时是安全的拷贝，逐项判空防读到未写入槽）。
    /// 关着的时候返回空表——调用方不用自己判 Enabled。
    /// </summary>
    public LiveLayerOptions[] Snapshot()
    {
        if (!Enabled) return [];
        var src = Sources;
        if (src == null || src.Count == 0) return [];
        var arr = src.ToArray(); // List.ToArray 不查版本号：并发增删最坏是这一拍少画一路，不抛
        var n = 0;
        foreach (var s in arr)
            if (s != null) arr[n++] = s;
        if (n == arr.Length) return arr;
        var trimmed = new LiveLayerOptions[n];
        Array.Copy(arr, trimmed, n);
        return trimmed;
    }
}

/// <summary>
/// 一帧的实时层绘制规格（渲染线程内即用即弃）。
/// <see cref="Frame"/> 是**借用**的位图（接收端复用缓冲），只在 Frame() 调用期间有效，禁止留存引用。
/// </summary>
public sealed class LiveLayer
{
    public SKBitmap Frame = null!;
    public double Alpha = 1;
    public double Scale = 1;
    public double OffsetX;
    public double OffsetY;
    public double Rotation;
    public double Blur;
    public double Glow;
    public double Hue;
    public bool MirrorX;
    public bool Cover;

    /// <summary>从可调参数 + 当前帧位图组装一帧规格（Frame 借用，不复制像素）。</summary>
    public static LiveLayer From(LiveLayerOptions o, SKBitmap frame) => new()
    {
        Frame = frame,
        Alpha = Math.Clamp(o.Alpha, 0, 1),
        Scale = Math.Clamp(o.Scale, 0.05, 8),
        OffsetX = o.OffsetX,
        OffsetY = o.OffsetY,
        Rotation = o.Rotation,
        Blur = Math.Max(0, o.Blur),
        Glow = Math.Clamp(o.Glow, 0, 1),
        Hue = o.Hue,
        MirrorX = o.MirrorX,
        Cover = o.Cover,
    };

    /// <summary>
    /// 目标矩形（设计坐标系 cw×ch）：先按 contain/cover 取基准缩放，再乘用户 Scale，居中后加偏移。
    /// 纯函数，可单测（tests 的 LiveLayerTests）。
    /// </summary>
    public static SKRect ComputeDest(int sw, int sh, float cw, float ch,
        double scale, double offX, double offY, bool cover)
    {
        if (sw <= 0 || sh <= 0 || cw <= 0 || ch <= 0) return SKRect.Empty;
        var fit = cover
            ? Math.Max(cw / sw, ch / sh)
            : Math.Min(cw / sw, ch / sh);
        var k = fit * Math.Max(0.01, scale);
        var w = (float)(sw * k);
        var h = (float)(sh * k);
        var left = (cw - w) / 2f + (float)(offX * cw);
        var top = (ch - h) / 2f + (float)(offY * ch);
        return new SKRect(left, top, left + w, top + h);
    }

    /// <summary>目标矩形（按本层参数）。</summary>
    public SKRect Dest(float cw, float ch) =>
        ComputeDest(Frame.Width, Frame.Height, cw, ch, Scale, OffsetX, OffsetY, Cover);

    /// <summary>
    /// 色相旋转矩阵（SVG feColorMatrix 同款公式：绕亮度轴转，保亮度）。
    /// 0 度返回 null（不挂 colorFilter，省一次逐像素变换）。
    /// </summary>
    public static SKColorFilter? HueFilter(double deg)
    {
        var d = deg % 360;
        if (Math.Abs(d) < 0.5) return null;
        var r = d * Math.PI / 180.0;
        var c = Math.Cos(r);
        var s = Math.Sin(r);
        // 亮度权重（Rec.709），与 GrayscaleFilter 同源
        const double lr = 0.2126, lg = 0.7152, lb = 0.0722;
        var m = new float[]
        {
            (float)(lr + c * (1 - lr) - s * lr),
            (float)(lg - c * lg - s * lg),
            (float)(lb - c * lb + s * (1 - lb)),
            0, 0,
            (float)(lr - c * lr + s * 0.143),
            (float)(lg + c * (1 - lg) + s * 0.140),
            (float)(lb - c * lb - s * 0.283),
            0, 0,
            (float)(lr - c * lr - s * (1 - lr)),
            (float)(lg - c * lg + s * lg),
            (float)(lb + c * (1 - lb) + s * lb),
            0, 0,
            0, 0, 0, 1, 0,
        };
        return SKColorFilter.CreateColorMatrix(m);
    }
}
