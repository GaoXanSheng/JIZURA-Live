// 变速（对位 WebAudio 的 playbackRate，以及 JS 导出里的 `spd`）：
// 预览与导出共用一个样本级实现——JS 版靠 AudioBufferSourceNode.playbackRate / OfflineAudioContext
// 的 src.playbackRate 实现「时长 ÷speed、音调同升」，C# 版此前用「重采样到 48000/speed 再交给
// WaveOut」的把戏（依赖设备/文件头按降下来的采样率播放，导出侧更是完全没接）。这里补成显式实现：
// 输出恒为上游采样率（两条链都已统一到 48000），按 speed 倍率向上游索取样本、线性插值，
// 于是同一段内容在 48000 下占用的时间变成 1/speed，设备/编码器都不需要配合。

using NAudio.Wave;

namespace JizuraLive.Engine;

/// <summary>
/// playbackRate 等价物：speed &gt; 1 = 变快（时长 ÷speed、音调随之升高）。
/// 只读上游、输出帧数 = 上游帧数 / speed（读到上游枯竭为止）。
/// </summary>
public sealed class SpeedSampleProvider : ISampleProvider
{
    /// <summary>缓冲下限（帧）：一次向上游要这么多，避免逐帧调用。</summary>
    private const int ChunkFrames = 4096;

    private readonly ISampleProvider _src;
    private readonly double _speed;
    private readonly int _ch;

    private float[] _buf = new float[ChunkFrames * 2]; // 交错 float；随需要扩
    private int _have;                                 // 缓冲内有效帧数（_buf 起点算起）
    private double _pos;                               // 当前读取位置（缓冲内帧坐标，浮点）
    private bool _eof;

    public SpeedSampleProvider(ISampleProvider src, double speed)
    {
        _src = src;
        _speed = Math.Clamp(speed, 0.5, 2);
        _ch = Math.Max(1, src.WaveFormat.Channels);
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, _ch);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(Span<float> buffer)
    {
        var ch = _ch;
        var outFrames = buffer.Length / ch;
        var produced = 0;
        while (produced < outFrames)
        {
            var need = (int)Math.Floor(_pos) + 2; // 插值要 i0 与 i0+1 两帧
            EnsureBuffered(need);
            if (_have == 0) break;                // 上游枯竭
            var i0 = Math.Min((int)Math.Floor(_pos), _have - 1);
            var i1 = Math.Min(i0 + 1, _have - 1); // 末帧处夹住（等价于重复最后一帧）
            var t = (float)Math.Clamp(_pos - i0, 0, 1);
            var o = produced * ch;
            var a = i0 * ch;
            var b = i1 * ch;
            for (var c = 0; c < ch; c++)
                buffer[o + c] = _buf[a + c] * (1 - t) + _buf[b + c] * t;
            _pos += _speed;
            produced++;
            var drop = (int)Math.Floor(_pos);
            if (drop > ChunkFrames / 2) Shift(drop);
        }
        return produced * ch;
    }

    /// <summary>保证缓冲内至少有 need 帧（读到上游枯竭为止）；不足也不抛，调用方按 _have 判。</summary>
    private void EnsureBuffered(int need)
    {
        if (need <= _have || _eof) return;
        var want = Math.Max(need, ChunkFrames);
        if (_buf.Length < want * _ch) Array.Resize(ref _buf, want * _ch);
        while (_have < want && !_eof)
        {
            var got = _src.Read(_buf.AsSpan(_have * _ch, (want - _have) * _ch));
            if (got <= 0)
            {
                _eof = true;
                break;
            }
            _have += got / _ch;
        }
    }

    /// <summary>丢掉已消费的 drop 帧，把缓冲起点前移（O(剩余帧) 拷贝，每块至多一次）。</summary>
    private void Shift(int drop)
    {
        drop = Math.Clamp(drop, 0, _have);
        if (drop == 0) return;
        var rest = (_have - drop) * _ch;
        if (rest > 0) Array.Copy(_buf, drop * _ch, _buf, 0, rest);
        _have -= drop;
        _pos -= drop;
    }
}
