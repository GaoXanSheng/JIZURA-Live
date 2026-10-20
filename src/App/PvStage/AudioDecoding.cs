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
        if (string.IsNullOrEmpty(path)) throw new ArgumentException(L.T("音频路径为空"), nameof(path));

        var ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            return Route(path) == AudioDecoderKind.Vorbis ? new VorbisWaveReader(path) : new AudioFileReader(path);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(L.F("音频解码失败（{0}）：{1}：{2}{3}", ext, path, ex.Message, Hint(ext)), ex);
        }
    }

    private static string Hint(string ext) => ext switch
    {
        ".ogg" => L.T("；ogg 只支持 Vorbis 编码（Opus 编码的 .ogg 打不开）"),
        ".flac" => L.T("；flac 由系统解码器解码，失败多为文件损坏或超出系统解码器能力"),
        ".kgm" => L.T("；加密音频格式不支持"),
        _ => "",
    };
}
