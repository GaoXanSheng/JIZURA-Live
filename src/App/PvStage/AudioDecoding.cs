using System.IO;
using NAudio.Vorbis;
using NAudio.Wave;

namespace JizuraLive.App.PvStage;

/// <summary>解码器选择：<see cref="System"/> = AudioFileReader（.wav→WaveFileReader、.mp3→Mp3FileReader、其余交给 Media Foundation）。</summary>
public enum AudioDecoderKind
{
    System,
    Vorbis,
}

/// <summary>
/// 统一音频解码入口：播放（<see cref="AudioClockDriver"/>）、波形（WaveformLoader）、频带分析（AudioFileAnalyzer）
/// 三处共用这一条路，同一文件不会出现「播得了、波形空白」。
///
/// 输出一律 IeeeFloat 32bit 交错样本（两个分支都是），调用方按 ISampleProvider 读 float。
/// 路由只有这两条，因为：
///   ogg 只能走 NAudio.Vorbis——系统解码器不支持 ogg 容器，实测报 0xC00D36C4「byte stream type is not supported」；
///   flac 只能走系统解码器——NAudio.Flac 1.0.5702.29018 是 net45 包、依赖 NAudio 1.7.3（非强命名），
///   与 NAudio 3.1.0 的 NAudio.Core 不是同一个程序集身份，引用它的类型连编译都过不去（CS0012）。
/// </summary>
public static class AudioDecoding
{
    /// <summary>按扩展名选解码器：.ogg → VorbisWaveReader，其余 → AudioFileReader。</summary>
    public static AudioDecoderKind Route(string path) =>
        Path.GetExtension(path).Equals(".ogg", StringComparison.OrdinalIgnoreCase)
            ? AudioDecoderKind.Vorbis
            : AudioDecoderKind.System;

    /// <summary>
    /// 打开解码器。失败抛 <see cref="InvalidDataException"/>：带路径、扩展名、底层原因与针对性建议，
    /// 原异常挂在 InnerException 上（不吞）。
    /// </summary>
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

    /// <summary>失败建议只写这一格式特有的坑，通用原因交给底层异常消息。</summary>
    private static string Hint(string ext) => ext switch
    {
        // ogg 是容器不是编码：NVorbis 只解 Vorbis，Opus 编码的 .ogg 是常见的打不开来源
        ".ogg" => "；ogg 只支持 Vorbis 编码（Opus 编码的 .ogg 打不开）",
        ".flac" => "；flac 由系统解码器解码，失败多为文件损坏或超出系统解码器能力",
        ".kgm" => "；加密音频格式不支持",
        _ => "",
    };
}
