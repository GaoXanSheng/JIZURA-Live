using System.IO;
using JizuraLive.Engine;
using JizuraLive.Pv;
using JizuraLive.Render.Text;

namespace JizuraLive.App.PvStage;

public partial class PvStageWindow
{
    private void PublishLyricClips(IReadOnlyList<LyricClip> clips)
    {
        _lyricClips = [.. LyricStyleSpans.Apply(clips, _project.LyricStyles)];
        _pipe?.Renderer.Lyric?.SetClips(_lyricClips);
    }

    private static readonly string[] LyricStyleNames = [.. LyricStyle.PresetNames, .. LyricStyle.PackNames];
    private static readonly string[] LyricStyleLabels =
        [.. LyricStyle.PresetNames.Select(LyricStyleDisplayName), .. LyricStyle.PackNames.Select(LyricStyleDisplayName)];

    private static string LyricStyleDisplayName(string presetName) => presetName switch
    {
        "Classic" => "经典",
        "Neon" => "霓虹",
        "Outline" => "描边",
        "Minimal" => "极简",
        "Karaoke" => "卡拉OK",
        "Pop" => "弹跳",
        "Typewriter" => "打字机",
        "Noir" => "黑色电影",
        "Crimson" => "绯红信号",
        "Caution" => "警示",
        "Magenta" => "品红流行",
        "Paper" => "纸墨",
        "Hud" => "暗色HUD",
        "Mint" => "薄荷终端",
        "Specimen" => "标本",
        "Transit" => "转运",
        "Blueprint" => "蓝图",
        "Rouge" => "胭脂",
        "Mono" => "单色RGB",
        _ => presetName,
    };

    private static string ReadLyricFileText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return KrcDecoder.IsKrc(bytes) ? KrcDecoder.Decrypt(bytes) : File.ReadAllText(path);
    }

    private bool HasBuiltinWords() =>
        _lyrics is not null && _lyrics.Any(l => l.Words is { Count: > 0 });
}
