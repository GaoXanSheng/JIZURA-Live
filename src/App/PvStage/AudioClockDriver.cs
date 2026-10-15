using JizuraLive.Logging;
using NAudio.Wave;

namespace JizuraLive.App.PvStage;

/// <summary>
/// 本地音频播放器与传输时钟。
///
/// 时间口径是「设备已播放位置」，不是解码器位置：实测 WaveOut 会把解码器领先可听输出约
/// 150–200ms（2×100ms 缓冲），拿解码位置当音乐时钟，歌词与特效整体抢拍。
/// <see cref="WaveOut.GetPosition"/> 又是「本次 Play 起算」的毫米钟（Stop 后归零），
/// 所以每段播放记一个基点 <see cref="_playBase"/>，位置永远等于 基点 + 本段已播。
/// 暂停时不读设备钟——实测暂停中它仍会爬 0.02–0.03s/1.2s。
/// </summary>
public sealed class AudioClockDriver : IDisposable
{
    private WaveOut? _output;
    private AudioFileReader? _reader;

    private double _position;
    private double _playBase;
    private volatile bool _playing;
    private volatile bool _stopRequested;

    /// <summary>设备错误原因（null = 正常）。播放因故障停下时保留位置。</summary>
    public string? Error { get; private set; }

    public bool IsPlaying => _playing;

    public double Duration => _reader?.TotalTime.TotalSeconds ?? 0;

    /// <summary>本帧音乐时刻（秒）。无音源时就是用户定位到的位置，不随墙钟推进。</summary>
    public double Time
    {
        get
        {
            if (!_playing || _output is null || _reader is null) return _position;

            // GetPosition() 给的是字节数。必须用解码器自己的 WaveFormat 换算——
            // AudioFileReader 即使源是 16bit PCM，输出格式也是 IeeeFloat 32bit，按源格式换算会整整快一倍。
            var abps = _reader.WaveFormat.AverageBytesPerSecond;
            if (abps <= 0) return _position;

            return Math.Clamp(_playBase + _output.GetPosition() / (double)abps, 0, Duration);
        }
    }

    /// <summary>加载本地音频：成功则位置归零并停在未播放态。</summary>
    public bool LoadAudio(string filePath)
    {
        try
        {
            ReleaseOutput();

            var reader = new AudioFileReader(filePath);
            var output = new WaveOut();
            output.Init(reader);
            output.PlaybackStopped += (_, e) => OnStopped(e.Exception);

            _reader = reader;
            _output = output;
            _position = 0;
            _playBase = 0;
            Error = null;

            Log.Info($"[音频驱动] 已加载：{filePath}（时长 {Duration:F1}s）");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("[音频驱动] 加载音频失败：" + ex.Message, ex);
            return false;
        }
    }

    public void Play()
    {
        if (_playing || _reader is null || _output is null) return;

        // 播完后再按播放 = 从头开始
        var from = _position >= Duration - 0.02 ? 0 : _position;
        StartAt(from);
        Error = null;
    }

    public void Pause()
    {
        if (_output is null || _reader is null) return;
        var t = Time;
        _playing = false;
        _position = t;
        _output.Pause();
    }

    public void Seek(double time)
    {
        // 无音源时项目时长还没有归属（M2/M5 才由歌词扩展），只做下限保护
        _position = _reader is null ? Math.Max(0, time) : Math.Clamp(time, 0, Duration);
        if (_reader is null || _output is null) return;

        _reader.CurrentTime = TimeSpan.FromSeconds(_position);
        // 播放中定位：已排队缓冲里仍是定位前的样本（实测约 0.25s 还能听见），先停再起倒掉
        if (_playing) StartAt(_position);
        else _playBase = _position;
    }

    public void Dispose() => ReleaseOutput();

    /// <summary>停—起一次：设备钟归零，旧缓冲作废，基点回到项目位置。</summary>
    private void StartAt(double seconds)
    {
        if (_reader is null || _output is null) return;

        _reader.CurrentTime = TimeSpan.FromSeconds(seconds);
        _stopRequested = true;
        _output.Stop();
        _stopRequested = false;

        _playBase = seconds;
        _position = seconds;
        _output.Play();
        _playing = true;
    }

    private void OnStopped(Exception? fault)
    {
        _playing = false;

        if (fault is not null)
        {
            // 设备故障：既不是播完也不是用户暂停，位置保持不动
            Error = fault.Message;
            Log.Error("[音频驱动] 播放设备异常：" + fault.Message, fault);
            return;
        }

        if (_stopRequested) return;
        if (_reader is not null && _reader.Position >= _reader.Length) _position = Duration;
    }

    /// <summary>先停输出再释放解码器：实测反过来会让输出静默掉落，回调也不来。</summary>
    private void ReleaseOutput()
    {
        _stopRequested = true;
        _playing = false;

        if (_output is not null)
        {
            try { _output.Stop(); } catch (Exception ex) { Log.Warn("[音频驱动] 停止输出失败：" + ex.Message); }
            _output.Dispose();
            _output = null;
        }

        _reader?.Dispose();
        _reader = null;
    }
}
