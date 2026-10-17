using JizuraLive.Logging;
using NAudio.Wave;

namespace JizuraLive.App.PvStage;

public sealed class AudioClockDriver : IDisposable
{
    private readonly object _gate = new();
    private WaveOut? _output;
    private WaveStream? _reader;

    private double _position;
    private double _playBase;
    private volatile bool _playing;
    private volatile bool _stopRequested;

    public string? Error { get; private set; }

    public bool IsPlaying => _playing;

    public double Duration
    {
        get
        {
            lock (_gate)
            {
                return _reader?.TotalTime.TotalSeconds ?? 0;
            }
        }
    }

    public double Time
    {
        get
        {
            lock (_gate)
            {
                if (!_playing || _output is null || _reader is null) return _position;

                try
                {
                    var abps = _reader.WaveFormat.AverageBytesPerSecond;
                    if (abps <= 0) return _position;

                    return Math.Clamp(_playBase + _output.GetPosition() / (double)abps, 0, Duration);
                }
                catch (Exception ex)
                {
                    Log.Warn("[音频驱动] 读时钟位置异常：" + ex.Message);
                    return _position;
                }
            }
        }
    }

    public bool LoadAudio(string filePath)
    {
        lock (_gate)
        {
            try
            {
                ReleaseOutput();

                var reader = AudioDecoding.Open(filePath);
                var output = new WaveOut();
                output.Init(reader);
                output.PlaybackStopped += OnPlaybackStopped;

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
                Log.Error($"[音频驱动] 加载音频失败：{filePath}：{ex.Message}", ex);
                return false;
            }
        }
    }

    public void Play()
    {
        lock (_gate)
        {
            if (_playing || _reader is null || _output is null) return;

            var from = _position >= Duration - 0.02 ? 0 : _position;
            StartAt(from);
            Error = null;
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_output is null || _reader is null) return;
            var t = Time;
            _playing = false;
            _position = t;
            try { _output.Pause(); } catch { }
        }
    }

    public void Seek(double time)
    {
        lock (_gate)
        {
            var target = _reader is null ? Math.Max(0, time) : Math.Clamp(time, 0, Duration);
            if (_reader is null || _output is null)
            {
                _position = target;
                return;
            }

            try
            {
                _reader.CurrentTime = TimeSpan.FromSeconds(target);
            }
            catch (Exception ex)
            {
                Error = "定位失败：" + ex.Message;
                return;
            }

            _position = target;
            Error = null;
            if (_playing) StartAt(_position);
            else _playBase = _position;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            ReleaseOutput();
        }
    }

    private void StartAt(double seconds)
    {
        if (_reader is null || _output is null) return;

        try { _reader.CurrentTime = TimeSpan.FromSeconds(seconds); } catch { }
        _stopRequested = true;
        try { _output.Stop(); } catch { }
        _output.PlaybackStopped -= OnPlaybackStopped;
        try { _output.Dispose(); } catch { }
        _output = CreateOutput();
        _stopRequested = false;

        _playBase = seconds;
        _position = seconds;
        try { _output.Play(); } catch { }
        _playing = true;
    }

    private WaveOut CreateOutput()
    {
        var output = new WaveOut();
        output.Init(_reader!);
        output.PlaybackStopped += OnPlaybackStopped;
        return output;
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _output)) return;
            OnStopped(e.Exception);
        }
    }

    private void OnStopped(Exception? fault)
    {
        _playing = false;

        if (fault is not null)
        {
            Error = fault.Message;
            Log.Error("[音频驱动] 播放设备异常：" + fault.Message, fault);
            return;
        }

        if (_stopRequested) return;
        if (_reader is not null && _reader.Position >= _reader.Length) _position = Duration;
    }

    private void ReleaseOutput()
    {
        _stopRequested = true;
        _playing = false;

        if (_output is not null)
        {
            try { _output.Stop(); } catch (Exception ex) { Log.Warn("[音频驱动] 停止输出失败：" + ex.Message); }
            try { _output.Dispose(); } catch { }
            _output = null;
        }

        _reader?.Dispose();
        _reader = null;
    }
}
