using System.IO;
using JizuraLive.Engine;
using JizuraLive.Pv;
using JizuraLive.Render.Text;

namespace JizuraLive.App.PvStage;

/// <summary>
/// 歌词接入（M5）：KRC 解密读入、逐字来源标注、样式预设下拉的中文名。
/// 只放本线新增逻辑；既有解析/装载路径见主文件（LoadLyrics / ParseLyrics）。
/// </summary>
public partial class PvStageWindow
{
    /// <summary>
    /// 歌词 clip 的唯一下发口：先按工程的逐段样式区间（<see cref="LyricStyleSpans"/>）给每句定样式，
    /// 再把结果作为 clip 序列发布给歌词层。**每一处**建出/替换 <c>_lyricClips</c> 的地方都要走这里——
    /// 样式只存在工程的区间里（clip 落盘的是 LrcText，装不下样式），漏一处就是「存盘重开后样式没了」。
    /// </summary>
    private void PublishLyricClips(IReadOnlyList<LyricClip> clips)
    {
        _lyricClips = [.. LyricStyleSpans.Apply(clips, _project.LyricStyles)];
        _pipe?.Renderer.Lyric?.SetClips(_lyricClips);
    }

    // 歌词样式下拉的固定项：取值域就是 LyricStyle.PresetNames；显示中文名，工程里存的仍是英文预设名。
    private static readonly string[] LyricStyleNames = [.. LyricStyle.PresetNames];
    private static readonly string[] LyricStyleLabels = [.. LyricStyle.PresetNames.Select(LyricStyleDisplayName)];

    private static string LyricStyleDisplayName(string presetName) => presetName switch
    {
        "Classic" => "经典",
        "Neon" => "霓虹",
        "Outline" => "描边",
        "Minimal" => "极简",
        "Karaoke" => "卡拉OK",
        "Pop" => "弹跳",
        "Typewriter" => "打字机",
        _ => presetName,
    };

    /// <summary>
    /// 读歌词文件：字节头是 "krc1" 的（酷狗 KRC）解密成文本再走 LRC 解析，其余按普通文本读。
    /// 坏 KRC 抛 <see cref="InvalidDataException"/>，由调用方的 catch 显示、不崩溃。
    /// </summary>
    private static string ReadLyricFileText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return KrcDecoder.IsKrc(bytes) ? KrcDecoder.Decrypt(bytes) : File.ReadAllText(path);
    }

    /// <summary>任一句自带逐字时间即算「文件自带」（KRC / 增强 LRC 导入的产物）。</summary>
    private bool HasBuiltinWords() =>
        _lyrics is not null && _lyrics.Any(l => l.Words is { Count: > 0 });
}
