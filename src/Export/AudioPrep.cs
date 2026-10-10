using System.IO;
using JizuraLive.Engine;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Newtonsoft.Json.Linq;

namespace JizuraLive.Export;

/// <summary>
/// 音频预处理（导出用）：文件 → NAudio 解码 → 从选段起点定位 → 声道归一为立体声 → 重采样 48k →
/// AudioFx 效果链（与预览同一条 Build，逐样本一致）→ 变速（SpeedSampleProvider）→
/// 时长钳到输出时长（不足补零）。
/// 对位 JS 的 resample()：真实渲染窗口 = [startT, startT + durationSec×speed]，
/// 输出 = durationSec（选段 + 变速后的实际视频长度；变速语义与预览的 playbackRate 一致）。
/// </summary>
public static class AudioPrep
{
    public const int SampleRate = 48000;

    /// <summary>
    /// 返回 48k 立体声 f32 provider；音频缺失/解码失败返回 null（导出走无音轨）。
    /// startT = 选段起点（秒）；speed = project.audioFx.speed（1 = 原速）。
    /// </summary>
    public static ISampleProvider? Prepare(string audioPath, JObject? audioFx,
        double durationSec, double startT = 0, double speed = 1)
    {
        if (!File.Exists(audioPath)) return null;

        var reader = new MediaFoundationReader(audioPath);
        ISampleProvider src = reader.ToSampleProvider();

        // 先定位再重采样（对位预览 BuildChain 的规矩：解码器重定位以文件时间为准）
        if (startT > 0)
        {
            try
            {
                reader.Position = (long)(Math.Clamp(startT, 0, reader.TotalTime.TotalSeconds)
                                         * reader.WaveFormat.AverageBytesPerSecond);
            }
            catch
            {
                // 定位失败：从头导出（比整段静音更有用）
            }
        }

        // 声道归一：mono → 双声道复制；>2 声道取前两轨（对位预览链路）
        if (src.WaveFormat.Channels == 1)
            src = new MonoToStereoSampleProvider(src);
        else if (src.WaveFormat.Channels > 2)
            src = new FirstTwoChannelsProvider(src);

        if (src.WaveFormat.SampleRate != SampleRate)
            src = new WdlResamplingSampleProvider(src, SampleRate);

        // 音频特效链（units 全关时 Build 返回 null → 直通）；anchor 用选段起点，
        // 与预览在同一歌曲时刻的 LFO/相位对齐
        var graph = AudioFx.Build(audioFx,
            new FxBuildOpts { SampleRate = SampleRate, AnchorT = startT });
        if (graph != null)
        {
            graph.Input.Source = src;
            src = graph.Output;
        }

        // 变速（对位 resample() 里 src.playbackRate = speed）：时长 ÷speed、音调同升
        if (Math.Abs(speed - 1) > 1e-6)
            src = new SpeedSampleProvider(src, speed);

        // 时长钳制：写满输出时长（超出截断、不足补静音）
        return new DurationPadProvider(src, durationSec);
    }

    /// <summary>多声道取前两轨（对位预览的「取前 2」语义）。</summary>
    private sealed class FirstTwoChannelsProvider : ISampleProvider
    {
        private readonly ISampleProvider _src;
        private readonly int _srcCh;
        private float[] _buf = Array.Empty<float>();

        public FirstTwoChannelsProvider(ISampleProvider src)
        {
            _src = src;
            _srcCh = src.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, 2);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(Span<float> buffer)
        {
            var frames = buffer.Length / 2;
            var need = frames * _srcCh;
            if (_buf.Length < need) _buf = new float[need];
            var got = _src.Read(_buf.AsSpan(0, need));
            var gotFrames = got / _srcCh;
            for (var i = 0; i < gotFrames; i++)
            {
                buffer[i * 2] = _buf[i * _srcCh];
                buffer[i * 2 + 1] = _srcCh > 1 ? _buf[i * _srcCh + 1] : _buf[i * _srcCh];
            }
            return gotFrames * 2;
        }
    }

    /// <summary>截断/补零到指定时长的 provider。</summary>
    private sealed class DurationPadProvider : ISampleProvider
    {
        private readonly ISampleProvider _src;
        private readonly long _totalFrames;
        private long _written;

        public DurationPadProvider(ISampleProvider src, double seconds)
        {
            _src = src;
            WaveFormat = src.WaveFormat;
            _totalFrames = (long)Math.Round(seconds * WaveFormat.SampleRate);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(Span<float> buffer)
        {
            if (_written >= _totalFrames) return 0;
            var ch = WaveFormat.Channels;
            var maxFrames = (int)Math.Min(buffer.Length / ch, _totalFrames - _written);
            var got = _src.Read(buffer[..(maxFrames * ch)]);
            if (got < maxFrames * ch)
            {
                // 源结束：补零到目标时长
                buffer[got..(maxFrames * ch)].Clear();
                got = maxFrames * ch;
            }
            _written += got / ch;
            return got;
        }
    }
}

/// <summary>32-bit float WAV 写出（IEEE float，对位 10_audio.js 的 audioWav；导出侧与 MP4 音轨共用同一份采样）。</summary>
public static class WavWriter
{
    public static void Write(ISampleProvider src, string path, double durationSec)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, src.WaveFormat.Channels);
        using var w = new WaveFileWriter(path, format);
        var buf = new float[SampleRateChunk(format)];
        var frames = (long)Math.Round(durationSec * format.SampleRate);
        var done = 0L;
        while (done < frames)
        {
            var want = (int)Math.Min(buf.Length, (frames - done) * format.Channels);
            var got = src.Read(buf.AsSpan(0, want));
            if (got <= 0) break;
            w.WriteSamples(buf, 0, got);
            done += got / format.Channels;
        }
        var chunk = new float[SampleRateChunk(format)]; // 剩余帧补零到目标时长
        while (done < frames)
        {
            var want = (int)Math.Min(chunk.Length, (frames - done) * format.Channels);
            w.WriteSamples(chunk, 0, want);
            done += want / format.Channels;
        }
    }

    private static int SampleRateChunk(WaveFormat f) => Math.Max(1, f.SampleRate / 10) * f.Channels;
}
