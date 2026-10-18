using System.IO;
using JizuraLive.Engine;
using JizuraLive.Pv;

namespace JizuraLive.App.PvStage;

public partial class PvStageWindow
{
    private void PublishLyricClips(IReadOnlyList<LyricClip> clips)
    {
        _lyricClips = [.. clips];
    }

    private static string ReadLyricFileText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return KrcDecoder.IsKrc(bytes) ? KrcDecoder.Decrypt(bytes) : File.ReadAllText(path);
    }

    private bool HasBuiltinWords() =>
        _lyrics is not null && _lyrics.Any(l => l.Words is { Count: > 0 });
}
