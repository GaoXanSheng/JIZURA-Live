namespace JizuraLive.Engine;

/// <summary>
/// 一句歌词的冻结区间（LRC 导入的产物；之后可被歌词轨编辑拖动/拉伸后再冻结）。
///
/// 区间一律**半开** <c>[StartMs, EndMs)</c>：<c>EndMs</c> 那一刻即清屏，间奏不残留上一句
/// （DOC/PV-DIRECTOR-SYSTEM.md §13.5）。时间单位沿用 long 毫秒，与 <see cref="KrcLine"/> 同口径，
/// 不引入第二套时钟——渲染层自己 /1000。
/// </summary>
/// <param name="Id">稳定标识，供 UI 选中与撤销引用（解析器按顺序生成，导入是一次整体替换）。</param>
/// <param name="StartMs">本句起点（毫秒，含）。</param>
/// <param name="EndMs">本句终点（毫秒，不含）：这一刻即清屏。</param>
/// <param name="Text">整句文本；同刻多条已合并，用 '\n' 连接。</param>
/// <param name="OutOfRange">末句起点已在音频尾外（或没有音频时长）：区间是待校准的临时值。</param>
/// <param name="Words">逐字/逐词时间（KRC 或增强 LRC 的产物）；null = 没有逐字时间，由 <see cref="LyricTiming.EnsureWords"/> 按字合成。
/// 各词 <see cref="KrcWord.StartMs"/> 是**绝对**歌曲毫秒，按时间升序，依次拼接等于 <paramref name="Text"/> 去掉 '\n' 后的结果。</param>
/// <param name="Style">本句歌词样式预设名；null = 用全局样式（导演按段落设置）。</param>
public sealed record LyricClip(
    string Id, long StartMs, long EndMs, string Text, bool OutOfRange,
    IReadOnlyList<KrcWord>? Words = null, string? Style = null);

/// <summary>
/// 某一时刻命中的歌词样本：动效歌词与清晰字幕消费**同一个**样本，
/// 各自不再重算「当前是哪句」或另算一套进度（DOC/PV-DIRECTOR-SYSTEM.md §13.5）。
/// </summary>
public readonly record struct LyricSample(
    string ClipId, string Text, long StartMs, long EndMs, bool OutOfRange)
{
    /// <summary>相对本句起点的局部毫秒（早于起点为负，由调用方自行决定含义）。</summary>
    public long LocalMs(double timeSeconds) => (long)(timeSeconds * 1000.0) - StartMs;

    /// <summary>本句进度 0..1；零/负时长（不该出现，解析器保证 end&gt;start）显式取 1，不做除零。</summary>
    public double ProgressAt(double timeSeconds)
    {
        if (EndMs <= StartMs) return 1.0;
        return Math.Clamp((double)LocalMs(timeSeconds) / (EndMs - StartMs), 0.0, 1.0);
    }
}

/// <summary>导入期的一条问题记录：不猜测、不隐式择一，报出文件里的行号让人去改（契约 §13.5）。</summary>
public sealed record LrcImportIssue(int LineNumber, string Reason);

/// <summary>LRC 导入结果：Clip 序列 + 问题清单（问题不阻断导入，只是要能被看到）。</summary>
public sealed record LrcImportResult(
    IReadOnlyList<LyricClip> Clips, IReadOnlyList<LrcImportIssue> Issues);
