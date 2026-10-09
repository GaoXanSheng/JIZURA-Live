using JizuraLive.App.I18n;
using JizuraLive.Server.Models;

namespace JizuraLive.App.Cloud;

/// <summary>
/// 当前装载的云端歌词来源（对位 Vue lyricStore 的 <c>last</c>）。
/// 保留原始歌词文本与候选版本，供两件事：
/// 1) 「歌词版本」下拉——酷狗同一首歌常有官方/用户上传的多个版本，切换后重新生成歌词与字幕；
/// 2) 音译/翻译勾选变化后立即重生成（否则要重新装载才生效）。
/// 会话级（不落工程）：重启后没有原始文本，下拉自然消失，与 Vue 的 localStorage 版本同语义。
/// </summary>
public sealed class LyricSource
{
    /// <summary>"kugou" | "netease"。</summary>
    public required string Source { get; init; }

    /// <summary>"krc"（酷狗逐字）| "lrc"（酷狗普通 LRC）| "ne"（网易云 LRC + 翻译/音译）。</summary>
    public required string Fmt { get; init; }

    /// <summary>krc：解密后的 KRC 原文；lrc：明文 LRC。ne 走 NeRaw。</summary>
    public string Text { get; init; } = "";

    /// <summary>网易云的原始三份 LRC（主 + 翻译 + 音译），按时间戳对齐重建。</summary>
    public (List<Krc.NeRow> Lrc, List<Krc.NeRow> Trans, List<Krc.NeRow> Roma)? NeRaw { get; init; }

    /// <summary>酷狗歌词候选版本（网易云无候选，恒空）。</summary>
    public List<KgLyricCandidate> Candidates { get; init; } = new();

    /// <summary>当前选中的候选下标（无候选时 -1）。</summary>
    public int Selected { get; init; } = -1;

    /// <summary>酷狗专辑 id：按候选 id 取词时仍要带上。</summary>
    public string AlbumId { get; init; } = "";

    /// <summary>按当前音译/翻译勾选生成 JIZURA 语法的 LRC。</summary>
    public string BuildLrc(bool wantRoma, bool wantTrans) => Fmt switch
    {
        "krc" => Krc.KrcToLrc(Text, wantRoma, wantTrans),
        "ne" => NeRaw is { } raw ? Krc.NeBuildLrc(raw.Lrc, raw.Trans, raw.Roma, wantRoma, wantTrans) : Text,
        _ => Text
    };

    /// <summary>是否带注音（翻译/音译）能力——普通 LRC 没有，切换版本时给用户提示。</summary>
    public bool HasNotes => Fmt != "lrc";

    /// <summary>候选版本下拉文案（对位 Vue versionLabel）：来源 + 上传者，匿名上传者不显示。</summary>
    public static string Label(KgLyricCandidate c) =>
        (string.IsNullOrEmpty(c.From) ? L.T("Lyrics.Version.Other") : c.From)
        + (c.Nick.Length > 0 && c.Nick != "热心用户" ? " · " + c.Nick : "");
}
