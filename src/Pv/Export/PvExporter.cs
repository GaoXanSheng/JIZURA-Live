using JizuraLive.App.PvStage;
using JizuraLive.Engine;
using JizuraLive.Logging;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Effects;
using JizuraLive.Render.Gpu;
using JizuraLive.Render.Text;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace JizuraLive.Pv;

public static class PvExporter
{
    public static string? Export(PvScene scene, EffectGraph effects, double durationSec, int fps, string outPath,
        IProgress<double>? progress, PvExportInputs? inputs = null)
    {
        if (string.IsNullOrWhiteSpace(outPath)) return "导出路径为空";
        if (Validate(scene, effects, durationSec, fps, out _) is { } reason) return reason;

        try
        {
            using var audio = AudioTrackOf(inputs);
            using var sink = MfSink.Open(outPath, scene.Width, scene.Height, fps, audio is not null);
            var frame = 0;
            var fault = RenderFrames(scene, effects, durationSec, fps, progress, nv12 =>
            {
                sink.WriteFrame(nv12);
                audio?.WriteThrough(++frame, fps, sink);
            }, inputs);
            sink.Close();
            return fault;
        }
        catch (Exception ex)
        {
            return "导出失败：" + ex.Message;
        }
    }

    private static AudioTrack? AudioTrackOf(PvExportInputs? inputs)
    {
        var path = inputs?.AudioPath;
        return string.IsNullOrWhiteSpace(path) ? null : AudioTrack.Open(path!, inputs!.AudioOffsetSec);
    }

    public static string? RenderFrames(PvScene scene, EffectGraph effects, double durationSec, int fps,
        IProgress<double>? progress, Action<byte[]> writeFrame, PvExportInputs? inputs = null)
    {
        if (Validate(scene, effects, durationSec, fps, out var frameCount) is { } reason) return reason;

        var width = scene.Width;
        var height = scene.Height;
        var nv12 = new byte[MfSink.Nv12Size(width, height)];

        try
        {
            var gpu = D3DDevice.TryCreate(out var gpuError);
            if (gpu is null) return $"建立离屏 D3D 设备失败：{gpuError}";

            var legacy = inputs?.Legacy;
            if (legacy is not null)
                legacy = new LegacyPvOptions
                {
                    Enabled = legacy.Enabled,
                    LrcText = legacy.LrcText,
                    AudioPath = legacy.AudioPath,
                    Style = legacy.Style,
                    Seed = legacy.Seed,
                    RenderScale = legacy.RenderScale,
                    NoCutTrans = legacy.NoCutTrans,
                    StageCamera = legacy.StageCamera,
                    StageTransitions = legacy.StageTransitions,
                    Synchronous = true,
                };

            using (gpu)
            using (var pipe = new PvPipeline(gpu, scene, effects, SubtitleSourceOf(inputs), legacy))
            using (var target = OffscreenTarget.Create(gpu, pipe.D2DContext, width, height))
            {
                pipe.SetDirector(inputs?.Curves);
                pipe.SetDesignSegments(inputs?.Segments);
                pipe.SetAudioAnalysis(inputs?.Bands);

                for (var i = 0; i < frameCount; i++)
                {
                    pipe.Prepare(i / (double)fps);
                    pipe.Composite(target.Bitmap, width, height);
                    gpu.Context.Flush();

                    using var frame = target.Readback();
                    BgraToNv12(frame.BgraPixels, width, height, nv12);
                    writeFrame(nv12);
                    progress?.Report((i + 1) / (double)frameCount);
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            return "导出失败：" + ex.Message;
        }
    }

    private static IPvSubtitleSource? SubtitleSourceOf(PvExportInputs? inputs) =>
        inputs?.Subtitles is { } config ? new ExportSubtitleSource(inputs.Clips, config) : null;

    private sealed class ExportSubtitleSource(IReadOnlyList<LyricClip>? clips, SubtitleConfig config)
        : IPvSubtitleSource
    {
        public IReadOnlyList<KrcLine>? Lines { get; } = LinesOf(clips);
        public SubtitleConfig? Config { get; } = config;
        public long Version => 1;

        private static IReadOnlyList<KrcLine>? LinesOf(IReadOnlyList<LyricClip>? clips)
        {
            if (clips is null) return null;

            var lines = new List<KrcLine>(clips.Count);
            foreach (var clip in clips)
                lines.Add(new KrcLine(clip.StartMs, clip.EndMs - clip.StartMs, clip.Text));
            return lines;
        }
    }

    private sealed class AudioTrack : IDisposable
    {
        private const int SampleRate = 48000;
        private const int Channels = 2;
        private const int ChunkFrames = 2048;
        private const long HnsPerSecond = 10_000_000;

        private readonly WaveStream? _reader;
        private readonly ISampleProvider? _samples;
        private readonly int _sourceChannels;
        private readonly float[] _readBuffer = new float[ChunkFrames * 2];
        private readonly short[] _interleaved = new short[ChunkFrames * Channels];
        private long _written;
        private bool _exhausted;

        private AudioTrack(WaveStream? reader, ISampleProvider? samples, int sourceChannels)
        {
            _reader = reader;
            _samples = samples;
            _sourceChannels = sourceChannels;
        }

        public static AudioTrack Open(string path, double offsetSec)
        {
            WaveStream? reader = null;
            try
            {
                reader = AudioDecoding.Open(path);
                if (double.IsFinite(offsetSec) && offsetSec > 0)
                    reader.CurrentTime = TimeSpan.FromSeconds(offsetSec);

                var samples = reader.ToSampleProvider();
                var channels = samples.WaveFormat.Channels;
                if (samples.WaveFormat.SampleRate != SampleRate)
                    samples = new WdlResamplingSampleProvider(samples, SampleRate);

                return new AudioTrack(reader, samples, channels);
            }
            catch (Exception ex)
            {
                reader?.Dispose();
                Log.Warn($"[导出] 音轨解码失败，本次导出的音轨为静音：{path}：{ex.Message}");
                return new AudioTrack(null, null, 1);
            }
        }

        public void WriteThrough(long frames, int fps, MfSink sink)
        {
            var target = (long)Math.Round(frames * SampleRate / (double)fps);
            while (_written < target)
            {
                var want = (int)Math.Min(target - _written, ChunkFrames);
                var got = _exhausted ? 0 : ReadFrames(want);
                if (got < want)
                {
                    _exhausted = true;
                    Array.Clear(_interleaved, got * Channels, (want - got) * Channels);
                }

                sink.WriteAudio(_interleaved.AsSpan(0, want * Channels), Timestamp(_written), Timestamp(want));
                _written += want;
            }
        }

        private int ReadFrames(int want)
        {
            if (_samples is null) return 0;

            var got = 0;
            while (got < want)
            {
                var read = _samples.Read(_readBuffer.AsSpan(0, Math.Min(_readBuffer.Length, (want - got) * _sourceChannels)));
                if (read <= 0) break;

                var frames = read / _sourceChannels;
                for (var i = 0; i < frames; i++)
                {
                    var left = _readBuffer[i * _sourceChannels];
                    _interleaved[(got + i) * Channels] = ToPcm16(left);
                    _interleaved[(got + i) * Channels + 1] =
                        ToPcm16(_sourceChannels == 1 ? left : _readBuffer[i * _sourceChannels + 1]);
                }

                got += frames;
            }

            return got;
        }

        private static long Timestamp(long sampleFrames) => sampleFrames * HnsPerSecond / SampleRate;

        private static short ToPcm16(float sample) =>
            (short)Math.Clamp((int)MathF.Round(sample * 32767f), short.MinValue, short.MaxValue);

        public void Dispose() => _reader?.Dispose();
    }

    public static void BgraToNv12(byte[] bgra, int width, int height, byte[] nv12)
    {
        for (var y = 0; y < height; y++)
        {
            var src = y * width * 4;
            var dst = y * width;
            for (var x = 0; x < width; x++, src += 4, dst++)
            {
                int b = bgra[src], g = bgra[src + 1], r = bgra[src + 2];
                nv12[dst] = (byte)(16 + ((47 * r + 157 * g + 16 * b) >> 8));
            }
        }

        var chromaBase = width * height;
        for (var y = 0; y < height; y += 2)
        {
            var top = y * width * 4;
            var bottom = top + width * 4;
            var dst = chromaBase + y / 2 * width;
            for (var x = 0; x < width; x += 2, dst += 2)
            {
                var i = top + x * 4;
                var j = bottom + x * 4;
                var b = (bgra[i] + bgra[i + 4] + bgra[j] + bgra[j + 4] + 2) >> 2;
                var g = (bgra[i + 1] + bgra[i + 5] + bgra[j + 1] + bgra[j + 5] + 2) >> 2;
                var r = (bgra[i + 2] + bgra[i + 6] + bgra[j + 2] + bgra[j + 6] + 2) >> 2;
                nv12[dst] = (byte)(128 + ((-26 * r - 87 * g + 112 * b) >> 8));
                nv12[dst + 1] = (byte)(128 + ((112 * r - 102 * g - 10 * b) >> 8));
            }
        }
    }

    private static string? Validate(PvScene? scene, EffectGraph? effects, double durationSec, int fps, out int frameCount)
    {
        frameCount = 0;
        if (scene is null) return "导出缺少场景文档";
        if (effects is null) return "导出缺少效果图";
        if (!double.IsFinite(durationSec) || durationSec <= 0) return $"导出时长必须是正数（当前 {durationSec}）";
        if (fps is < 1 or > 240) return $"帧率必须在 1..240 之间（当前 {fps}）";

        if (scene.Width <= 0 || scene.Height <= 0 || (scene.Width & 1) != 0 || (scene.Height & 1) != 0)
            return $"画布宽高必须是正偶数（当前 {scene.Width}×{scene.Height}）：NV12 色度按 2×2 采样";

        var frames = Math.Round(durationSec * fps);
        if (frames > int.MaxValue) return $"导出帧数超出上限（{durationSec} 秒 × {fps} fps）";
        frameCount = Math.Max(1, (int)frames);
        return null;
    }
}

public sealed record PvExportInputs(
    DirectorEvaluator? Curves = null,
    AudioBandSeries? Bands = null,
    IReadOnlyList<DesignSegment>? Segments = null,
    IReadOnlyList<LyricClip>? Clips = null,
    SubtitleConfig? Subtitles = null,
    string? AudioPath = null,
    double AudioOffsetSec = 0,
    LegacyPvOptions? Legacy = null);
