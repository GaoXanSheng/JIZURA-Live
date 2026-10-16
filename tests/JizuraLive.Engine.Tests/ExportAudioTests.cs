using System.IO;
using JizuraLive.Pv;
using JizuraLive.Render.Effects;
using NAudio.Wave;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 导出音轨：真渲染 + 真编码（MF 的 H.264 + AAC），所以整类挂 [GpuFact]。
/// 音轨内容一律用 NAudio 的 <see cref="MediaFoundationReader"/> 读回 MP4 校验（时长与 RMS），
/// 不直接看 SinkWriter 的内部状态——那只能证明"调用了"，证明不了文件里真有声音。
/// </summary>
public class ExportAudioTests
{
    private const int Fps = 30;

    private static string TempPath(string ext) =>
        Path.Combine(Path.GetTempPath(), "jizura-audio-" + Guid.NewGuid().ToString("N") + ext);

    private static PvScene SmallScene()
    {
        var scene = PvScene.CreateBlank();
        scene.Width = 320;
        scene.Height = 180;
        return scene;
    }

    /// <summary>
    /// 44.1kHz 单声道 16bit 正弦：刻意用非 48k 单声道源，导出侧必须重采样到 48k 并复制成双声道。
    /// <paramref name="toneFromSec"/> 之前是静音——用来证明 <see cref="PvExportInputs.AudioOffsetSec"/> 真的挪了时间轴。
    /// </summary>
    private static string WriteSineWav(double seconds, double toneFromSec)
    {
        var path = TempPath(".wav");
        using var writer = new WaveFileWriter(path, new WaveFormat(44100, 16, 1));
        var total = (int)(44100 * seconds);
        var from = (int)(44100 * toneFromSec);
        for (var n = 0; n < total; n++)
            writer.WriteSample(n < from ? 0f : (float)(0.5 * Math.Sin(2 * Math.PI * 440 * n / 44100.0)));

        return path;
    }

    /// <summary>读回 MP4 的音轨：解码后的格式、时长（秒）与整体 RMS。</summary>
    private static (WaveFormat Format, double Seconds, double Rms) ReadBack(string path)
    {
        using var reader = new MediaFoundationReader(path);
        var samples = reader.ToSampleProvider();
        var format = samples.WaveFormat;
        var buffer = new float[format.SampleRate * format.Channels];
        long count = 0;
        double sum = 0;

        while (true)
        {
            var read = samples.Read(buffer.AsSpan());
            if (read <= 0) break;
            for (var i = 0; i < read; i++) sum += (double)buffer[i] * buffer[i];
            count += read;
        }

        return (format, count / (double)(format.SampleRate * format.Channels),
            Math.Sqrt(sum / Math.Max(1, count)));
    }

    private static void Delete(params string[] paths)
    {
        foreach (var path in paths)
        {
            try { File.Delete(path); }
            catch (IOException) { }
        }
    }

    [GpuFact]
    public void ExportedFileCarriesAudibleAudio()
    {
        var wav = WriteSineWav(3, 0);
        var mp4 = TempPath(".mp4");
        try
        {
            var reason = PvExporter.Export(SmallScene(), new EffectGraph(), 1.5, Fps, mp4, null,
                new PvExportInputs(AudioPath: wav));

            Assert.Null(reason);
            var (format, seconds, rms) = ReadBack(mp4);
            Assert.Equal(48000, format.SampleRate);
            Assert.Equal(2, format.Channels);
            Assert.InRange(seconds, 1.35, 1.65);
            Assert.True(rms > 0.05, $"读回的音轨应当有 440Hz 正弦（RMS={rms:F4}）");
        }
        finally
        {
            Delete(wav, mp4);
        }
    }

    /// <summary>前 1 秒静音、后 2 秒正弦：偏移 0 该几乎无声，偏移 1.0 该落到正弦上——两头都钉住。</summary>
    [GpuFact]
    public void AudioOffsetShiftsTheSongIntoTheExport()
    {
        var wav = WriteSineWav(3, 1);
        var plain = TempPath(".mp4");
        var shifted = TempPath(".mp4");
        try
        {
            Assert.Null(PvExporter.Export(SmallScene(), new EffectGraph(), 1.0, Fps, plain, null,
                new PvExportInputs(AudioPath: wav)));
            Assert.Null(PvExporter.Export(SmallScene(), new EffectGraph(), 1.0, Fps, shifted, null,
                new PvExportInputs(AudioPath: wav, AudioOffsetSec: 1.0)));

            var (_, plainSeconds, plainRms) = ReadBack(plain);
            var (_, shiftedSeconds, shiftedRms) = ReadBack(shifted);
            Assert.InRange(plainSeconds, 0.85, 1.15);
            Assert.InRange(shiftedSeconds, 0.85, 1.15);
            Assert.True(plainRms < 0.01, $"偏移 0 落在源文件的静音段（RMS={plainRms:F4}）");
            Assert.True(shiftedRms > 0.05, $"偏移 1.0 应当落到正弦段（RMS={shiftedRms:F4}）");
        }
        finally
        {
            Delete(wav, plain, shifted);
        }
    }

    /// <summary>音频比视频短：音轨长度跟视频走，缺的部分补静音（不然成片的音轨会在中途断掉）。</summary>
    [GpuFact]
    public void ShortAudioIsPaddedWithSilenceToTheVideoLength()
    {
        var wav = WriteSineWav(0.5, 0);
        var mp4 = TempPath(".mp4");
        try
        {
            var reason = PvExporter.Export(SmallScene(), new EffectGraph(), 1.5, Fps, mp4, null,
                new PvExportInputs(AudioPath: wav));

            Assert.Null(reason);
            var (_, seconds, rms) = ReadBack(mp4);
            Assert.InRange(seconds, 1.35, 1.65);
            Assert.True(rms > 0.05, $"前 0.5 秒有声音（整段 RMS={rms:F4}）");
        }
        finally
        {
            Delete(wav, mp4);
        }
    }

    /// <summary>
    /// 不给 AudioPath 时与加音轨之前一样：只有视频流。
    /// "没有音轨"用按音轨读回会报错来观察——MediaFoundationReader 只认第一条音轨，只有视频流时它直接报流号无效。
    /// </summary>
    [GpuFact]
    public void ExportWithoutAudioPathStaysVideoOnly()
    {
        var mp4 = TempPath(".mp4");
        try
        {
            var reason = PvExporter.Export(SmallScene(), new EffectGraph(), 0.5, Fps, mp4, null);

            Assert.Null(reason);
            Assert.True(new FileInfo(mp4).Length > 0);
            Assert.Throws<NAudio.MediaFoundation.MediaFoundationException>(() => ReadBack(mp4));
        }
        finally
        {
            Delete(mp4);
        }
    }

    /// <summary>解码失败不该连累视频：导出照常成功，音轨退化为整段静音（读回不抛，时长仍跟视频走）。</summary>
    [GpuFact]
    public void MissingAudioFileStillExportsVideo()
    {
        var mp4 = TempPath(".mp4");
        try
        {
            var reason = PvExporter.Export(SmallScene(), new EffectGraph(), 0.5, Fps, mp4, null,
                new PvExportInputs(AudioPath: TempPath(".wav")));

            Assert.Null(reason);
            Assert.True(new FileInfo(mp4).Length > 0, "没有音频也要留下能播的 MP4");

            var (_, seconds, rms) = ReadBack(mp4);
            Assert.InRange(seconds, 0.35, 0.65);
            Assert.True(rms < 0.01, $"退化的音轨只能是静音（RMS={rms:F4}）");
        }
        finally
        {
            Delete(mp4);
        }
    }
}
