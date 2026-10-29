using System.IO;
using NAudio.Vorbis;
using NAudio.Wave;
using JizuraLive.App.I18n;

namespace JizuraLive.App.PvStage;

public enum AudioDecoderKind
{
    System,
    Vorbis,
}

public static class AudioDecoding
{
    public static AudioDecoderKind Route(string path) =>
        Path.GetExtension(path).Equals(".ogg", StringComparison.OrdinalIgnoreCase)
            ? AudioDecoderKind.Vorbis
            : AudioDecoderKind.System;

    public static WaveStream Open(string path)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException(L.T("StageUi.Media.AudioPathEmpty"), nameof(path));

        var ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            return Route(path) == AudioDecoderKind.Vorbis ? new VorbisWaveReader(path) : new AudioFileReader(path);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(L.F("StageUi.Media.AudioDecodeFailed", ext, path, ex.Message, Hint(ext)), ex);
        }
    }

    private static string Hint(string ext) => ext switch
    {
        ".ogg" => L.T("StageUi.Media.OggOnlyVorbis"),
        ".flac" => L.T("StageUi.Media.FlacDecoderHint"),
        ".kgm" => L.T("StageUi.Media.EncryptedAudioUnsupported"),
        _ => "",
    };
}
