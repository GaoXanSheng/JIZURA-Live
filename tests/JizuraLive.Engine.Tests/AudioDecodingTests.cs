using System.IO;
using JizuraLive.App.PvStage;
using NAudio.Wave;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 统一解码入口（<see cref="AudioDecoding"/>）的路由与失败消息。
///
/// 仓库里没有真实的 flac/ogg 样本，所以这里钉住的是可测部分：扩展名 → 解码器这张路由表，
/// 以及"失败不吞、消息带路径与格式"。真机上的实际解码验证留给编排者。
/// </summary>
public class AudioDecodingTests
{
    // ---------- 路由表 ----------

    [Theory]
    [InlineData("song.ogg", AudioDecoderKind.Vorbis)]
    [InlineData("song.OGG", AudioDecoderKind.Vorbis)]     // 扩展名大小写不该改变路由
    [InlineData(@"C:\music\带空格 的歌.ogg", AudioDecoderKind.Vorbis)]
    [InlineData("song.flac", AudioDecoderKind.System)]
    [InlineData("song.wav", AudioDecoderKind.System)]
    [InlineData("song.mp3", AudioDecoderKind.System)]
    [InlineData("song.m4a", AudioDecoderKind.System)]
    [InlineData("song.aac", AudioDecoderKind.System)]
    [InlineData("song.wma", AudioDecoderKind.System)]
    [InlineData("song", AudioDecoderKind.System)]          // 没有扩展名
    public void RoutePicksDecoderByExtension(string path, AudioDecoderKind expected) =>
        Assert.Equal(expected, AudioDecoding.Route(path));

    /// <summary>System 路由确实交到 AudioFileReader 手上：用真能解开的 wav 断言返回类型。</summary>
    [Fact]
    public void SystemRouteOpensAudioFileReader()
    {
        var path = TempPath(".wav");
        try
        {
            WriteSineWav(path);
            using var reader = AudioDecoding.Open(path);

            Assert.IsType<AudioFileReader>(reader);
            Assert.Equal(WaveFormatEncoding.IeeeFloat, reader.WaveFormat.Encoding);  // 三处消费统一口径
            Assert.Equal(44100, reader.WaveFormat.SampleRate);
            Assert.True(reader.TotalTime.TotalSeconds > 0.9);
        }
        finally { File.Delete(path); }
    }

    // ---------- 失败路径 ----------

    /// <summary>
    /// .ogg 必须走 Vorbis 而不是系统解码器：内容不是音频时，VorbisWaveReader 抛 ArgumentException，
    /// 而系统解码器给的是 MediaFoundationException——内层异常类型就是路由的证据。
    /// </summary>
    [Fact]
    public void OggRouteGoesToVorbisNotSystemDecoder()
    {
        var path = TempPath(".ogg");
        try
        {
            File.WriteAllText(path, "not audio at all");

            var ex = Assert.Throws<InvalidDataException>(() => AudioDecoding.Open(path));

            Assert.Contains(path, ex.Message);
            Assert.Contains(".ogg", ex.Message);
            Assert.Contains("Opus", ex.Message);                              // 该格式特有的建议
            Assert.IsType<ArgumentException>(ex.InnerException);              // NVorbis 的失败形态
        }
        finally { TryDelete(path); }
    }

    /// <summary>其余格式走系统解码器：坏文件给的是 MediaFoundationException，同样带路径与扩展名抛出。</summary>
    [Theory]
    [InlineData(".flac")]
    [InlineData(".m4a")]
    [InlineData(".xyz")]        // 未知扩展名同样不吞
    public void SystemRouteFailuresCarryPathAndFormat(string ext)
    {
        var path = TempPath(ext);
        try
        {
            File.WriteAllText(path, "not audio at all");

            var ex = Assert.Throws<InvalidDataException>(() => AudioDecoding.Open(path));

            Assert.Contains(path, ex.Message);
            Assert.Contains(ext, ex.Message);
            Assert.IsType<NAudio.MediaFoundation.MediaFoundationException>(ex.InnerException);
        }
        finally { File.Delete(path); }
    }

    /// <summary>文件不存在也抛统一异常（不是静默返回 null），原因留在 InnerException 里。</summary>
    [Fact]
    public void MissingFileThrowsInsteadOfReturningNull()
    {
        var path = TempPath(".ogg");

        var ex = Assert.Throws<InvalidDataException>(() => AudioDecoding.Open(path));

        Assert.Contains(path, ex.Message);
        Assert.IsType<FileNotFoundException>(ex.InnerException);
    }

    /// <summary>空路径当场拒绝：三处入口共用这一条检查，消息与原来一字不差。</summary>
    [Fact]
    public void EmptyPathIsRejectedBeforeAnyDecoderIsOpened()
    {
        var ex = Assert.Throws<ArgumentException>(() => AudioDecoding.Open(""));
        Assert.Contains("音频路径为空", ex.Message);
    }

    // ---------- 辅助 ----------

    private static string TempPath(string ext) =>
        Path.Combine(Path.GetTempPath(), "jz-decode-" + Guid.NewGuid().ToString("N") + ext);

    /// <summary>VorbisWaveReader 构造失败时不会关掉它已打开的文件句柄，临时文件删不掉——清理按尽力而为。</summary>
    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
    }

    private static void WriteSineWav(string path)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(44100, 16, 1));
        for (int n = 0; n < 44100; n++)
            writer.WriteSample((float)(0.5 * Math.Sin(2 * Math.PI * 1000 * n / 44100)));
    }
}
