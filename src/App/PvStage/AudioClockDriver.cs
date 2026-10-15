using System.Diagnostics;
using JizuraLive.Logging;
using JizuraLive.Pv;
using NAudio.Wave;

namespace JizuraLive.App.PvStage;

/// <summary>
/// 纯净的 NAudio 音频与时钟驱动器：实现 IPvDriver。
/// 支持播放本地 MP3/WAV/FLAC，提供毫秒级对齐的播放时钟与实时音量能量计算。
/// </summary>
public sealed class AudioClockDriver : IPvDriver
{
    private WaveOut? _output;
    private AudioFileReader? _reader;
    private readonly Stopwatch _virtualClock = new();
    private double _virtualOffset;
    private double _virtualDuration = 180.0;
    private bool _isPlaying;

    public string Name => _reader is null ? "虚拟时钟驱动器" : "NAudio 音频驱动器";

    public double Time
    {
        get
        {
            if (_reader is not null)
            {
                return _reader.CurrentTime.TotalSeconds;
            }
            return _virtualOffset + (_isPlaying ? _virtualClock.Elapsed.TotalSeconds : 0);
        }
    }

    public double Duration
    {
        get
        {
            if (_reader is not null)
            {
                return _reader.TotalTime.TotalSeconds;
            }
            return _virtualDuration;
        }
    }

    public bool IsPlaying => _isPlaying;

    public event Action? Changed;

    /// <summary>加载本地音频文件。</summary>
    public bool LoadAudio(string filePath)
    {
        try
        {
            Pause();
            _reader?.Dispose();
            _reader = null;
            _output?.Dispose();
            _output = null;

            _reader = new AudioFileReader(filePath);
            _output = new WaveOut();
            _output.Init(_reader);
            _virtualClock.Reset();
            _virtualOffset = 0;

            Log.Info($"[音频驱动] 已加载：{filePath}（时长 {Duration:F1}s）");
            Changed?.Invoke();
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
        if (_isPlaying) return;
        _isPlaying = true;

        if (_output is not null && _reader is not null)
        {
            _output.Play();
        }
        else
        {
            _virtualClock.Restart();
        }

        Changed?.Invoke();
    }

    public void Pause()
    {
        if (!_isPlaying) return;
        _isPlaying = false;

        if (_output is not null)
        {
            _output.Pause();
        }
        else
        {
            _virtualOffset += _virtualClock.Elapsed.TotalSeconds;
            _virtualClock.Reset();
        }

        Changed?.Invoke();
    }

    public void Seek(double time)
    {
        time = Math.Clamp(time, 0, Duration);

        if (_reader is not null)
        {
            _reader.CurrentTime = TimeSpan.FromSeconds(time);
        }
        else
        {
            _virtualOffset = time;
            if (_isPlaying) _virtualClock.Restart();
        }

        Changed?.Invoke();
    }

    public void Dispose()
    {
        Pause();
        _reader?.Dispose();
        _reader = null;
        _output?.Dispose();
        _output = null;
    }
}
