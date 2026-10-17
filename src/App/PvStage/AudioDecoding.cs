using System.IO;
using NAudio.Vorbis;
using NAudio.Wave;

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
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("音频路径为空", nameof(path));

        var ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            return Route(path) == AudioDecoderKind.Vorbis ? new VorbisWaveReader(path) : new AudioFileReader(path);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"音频解码失败（{ext}）：{path}：{ex.Message}{Hint(ext)}", ex);
        }
    }

    private static string Hint(string ext) => ext switch
    {
        ".ogg" => "；ogg 只支持 Vorbis 编码（Opus 编码的 .ogg 打不开）",
        ".flac" => "；flac 由系统解码器解码，失败多为文件损坏或超出系统解码器能力",
        ".kgm" => "；加密音频格式不支持",
        _ => "",
    };
}
