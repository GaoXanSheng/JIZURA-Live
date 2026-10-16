namespace JizuraLive.Vts;

/// <summary>演出 cue 的种类。</summary>
public enum VtsCueKind
{
    /// <summary>触发热键（模型动作/表情开关的 VTS 侧做法）。</summary>
    Hotkey,

    /// <summary>开关一个表情（按段落驱动情绪）。</summary>
    Expression,

    /// <summary>模型相对移动（叠加在基准取景之上的位移/缩放/旋转）。</summary>
    Move,

    /// <summary>参数脉冲（add 模式注入一段包络）。</summary>
    Pulse,
}

/// <summary>
/// 一条演出 cue。由导演模块生成、<see cref="VtsCueScheduler"/> 按歌曲秒触发，本类型只承载数据。
/// </summary>
public sealed class VtsCue
{
    /// <summary>触发时刻（歌曲秒）。</summary>
    public double Time { get; set; }

    public VtsCueKind Kind { get; set; }

    /// <summary>
    /// Hotkey = 热键名或 <c>role:xxx</c>；Expression = 表情文件名或 <c>role:xxx</c>；
    /// Pulse = VTS 输入参数 id（FaceAngleY / FaceAngleZ / MouthSmile …）。
    /// </summary>
    public string Target { get; set; } = "";

    /// <summary>Expression：1 = 开 0 = 关；Pulse：add 模式叠加峰值；Move：size 增量。</summary>
    public double Value { get; set; }

    /// <summary>Move：动画秒 0..2；Pulse：包络总长秒；Expression：&gt;0 时到期自动关。</summary>
    public double Duration { get; set; }

    /// <summary>Move 的相对位移（X / Y / 旋转）。</summary>
    public double X { get; set; }

    public double Y { get; set; }

    public double Rotation { get; set; }
}

/// <summary>一首歌的演出 cue 轨。</summary>
public sealed class VtsCueTrack
{
    /// <summary>cue 列表（顺序无所谓，调度器加载时按 <see cref="VtsCue.Time"/> 稳定排序）。</summary>
    public List<VtsCue> Cues { get; set; } = new();

    /// <summary>提前量（毫秒）：离散 cue 在 Time - LeadMs/1000 触发（让 VTS 侧的动作贴在拍上）。</summary>
    public double LeadMs { get; set; } = 80;

    /// <summary>角色 → 热键名或表情文件名。cue 的 Target 写 <c>role:xxx</c> 时按它解析。</summary>
    public Dictionary<string, string> Roles { get; set; } = new();
}

/// <summary>导演模块使用的固定角色名（映射在 <see cref="VtsCueTrack.Roles"/> 里配）。</summary>
public static class VtsRoles
{
    public const string Chorus = "chorus";
    public const string Hit = "hit";
    public const string Bridge = "bridge";
    public const string Outro = "outro";

    /// <summary>角色清单（配置面板按它列项）。</summary>
    public static readonly IReadOnlyList<(string Role, string Label)> All =
    [
        (Chorus, "副歌表情"),
        (Hit, "高潮动作"),
        (Bridge, "间奏表情"),
        (Outro, "结尾动作"),
    ];
}

/// <summary>
/// 演出 cue 的输出口：<see cref="VtsApi"/> 实现它（真发 VTS），测试用替身。
/// 与 <see cref="IVtsDriveTarget"/> 分开：那条线是持续量（每拍给最新值），这条线是离散事件 + 包络。
/// </summary>
public interface IVtsCueSink
{
    /// <summary>触发热键（名字或 hotkeyID 都行，VTS 两种都认）。</summary>
    void TriggerHotkey(string nameOrId);

    /// <summary>开关表情（<paramref name="expressionFile"/> 是表情文件名，不是显示名）。</summary>
    void SetExpression(string expressionFile, bool active, double fadeSec);

    /// <summary>相对模型当前位置移动（<paramref name="size"/> 是大小增量，<paramref name="seconds"/> 是动画时长）。</summary>
    void MoveModelRelative(double x, double y, double rotation, double size, double seconds);

    /// <summary>叠加注入（mode = add）：值是参数自身量纲的增量；全 0 表示释放。</summary>
    void InjectAdd(IReadOnlyDictionary<string, double> values);
}
