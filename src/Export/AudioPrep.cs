using System.IO;
using NAudio.Wave;

namespace JizuraLive.Export;

/// <summary>
/// 音频预处理（导出用）：文件 → NAudio 解码 → 声道归一为立体声 → 重采样 48k →
/// AudioFx 效果链（与预览同一条 Build，逐样本一致）→ 时长钳到视频时长（不足补零）。
/// </summary>
public static class AudioPrep
{
    public const int SampleRate = 48000;

    /// <summary>返回 48k 立体声 f32 provider；音频缺失/解码失败返回 null（导出走无音轨）。</summary>
    public static ISampleProvider? Prepare(string audioPath, Newtonsoft.Json.Linq.JObject? audioFx,
        double durationSec)
    {
        if (!File.Exists(audioPath)) return null;

        var reader = new MediaFoundationReader(audioPath);
        ISampleProvider src = reader.ToSampleProvider();

        // 声道归一：mono → 双声道复制；>2 声道取前两轨（对位预览链路）
        if (src.WaveFormat.Channels == 1)
            src = new NAudio.Wave.SampleProviders.MonoToStereoSampleProvider(src);
        else if (src.WaveFormat.Channels > 2)
            src = new FirstTwoChannelsProvider(src);

        if (src.WaveFormat.SampleRate != SampleRate)
            src = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(src, SampleRate);

        // 音频特效链（units 全关时 Build 返回 null → 直通）
        var graph = JizuraLive.Engine.AudioFx.Build(audioFx,
            new JizuraLive.Engine.FxBuildOpts { SampleRate = SampleRate, AnchorT = 0 });
        if (graph != null)
        {
            graph.Input.Source = src;
            src = graph.Output;
        }

        // 时长钳制：写满视频时长（超出截断、不足补静音）
        return new DurationPadProvider(src, durationSec);
    }

    /// <summary>多声道取前两轨（对位预览的「取前 2」语义）。</summary>
    private sealed class FirstTwoChannelsProvider : ISampleProvider
    {
        private readonly ISampleProvider _src;
        private readonly int _srcCh;
        private float[] _buf = System.Array.Empty<float>();

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

/// <summary>16-bit PCM WAV 写出（对位 10_audio.js 的 audioWav；导出侧与 MP4 音轨共用同一份采样）。</summary>
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
        // 补零到目标时长
        var chunk = new float[SampleRateChunk(format)];
        while (done < frames)
        {
            var want = (int)Math.Min(chunk.Length, (frames - done) * format.Channels);
            w.WriteSamples(chunk, 0, want);
            done += want / format.Channels;
        }
    }

    private static int SampleRateChunk(WaveFormat f) => Math.Max(1, f.SampleRate / 10) * f.Channels;
}
