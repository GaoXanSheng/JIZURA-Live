using System.IO;
using System.Text;
using JizuraLive.Engine;
using JizuraLive.Pv;

namespace JizuraLive.App.PvStage;

public partial class PvStageWindow
{
    private void PublishLyricClips(IReadOnlyList<LyricClip> clips)
    {
        _lyricClips = [.. clips];
        System.Threading.Interlocked.Increment(ref _subtitleVersion);
    }

    private static string ReadLyricFileText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (KrcDecoder.IsKrc(bytes)) return KrcDecoder.Decrypt(bytes);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding("GB18030").GetString(bytes);
        }
    }

    private bool HasBuiltinWords() =>
        _lyrics is not null && _lyrics.Any(l => l.Words is { Count: > 0 });
}
