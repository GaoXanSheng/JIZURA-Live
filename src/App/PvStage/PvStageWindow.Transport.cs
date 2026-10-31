using System.Diagnostics;

namespace JizuraLive.App.PvStage;

public partial class PvStageWindow
{
    private const double ShuttleIntervalMs = 33;

    private System.Windows.Threading.DispatcherTimer? _shuttleTimer;
    private bool _shuttleActive;
    private int _shuttleRate = 1;
    private long _shuttleLastTick;

    private void TransportPlayForward()
    {
        StopShuttle();
        if (_audio.IsPlaying || _audio.Duration <= 0) return;
        _audio.Play();
        TheTimeline.FollowPlayhead();
        UpdatePlayIcon();
    }

    private void TransportPause()
    {
        StopShuttle();
        if (_audio.IsPlaying) _audio.Pause();
        UpdatePlayIcon();
    }

    private void TransportPlayReverse()
    {
        if (_audio.Duration <= 0) return;
        if (_audio.IsPlaying) _audio.Pause();

        if (_shuttleActive)
        {
            _shuttleRate = _shuttleRate >= 8 ? 1 : _shuttleRate * 2;
        }
        else
        {
            _shuttleRate = 1;
            _shuttleTimer ??= CreateShuttleTimer();
            _shuttleLastTick = Stopwatch.GetTimestamp();
            _shuttleTimer.Start();
        }

        _shuttleActive = true;
        TheTimeline.FollowPlayhead();
        UpdatePlayIcon();
    }

    private System.Windows.Threading.DispatcherTimer CreateShuttleTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(ShuttleIntervalMs),
        };
        timer.Tick += (_, _) => ShuttleTick();
        return timer;
    }

    private void ShuttleTick()
    {
        if (!_shuttleActive)
        {
            _shuttleTimer?.Stop();
            return;
        }

        long now = Stopwatch.GetTimestamp();
        double dt = (now - _shuttleLastTick) / (double)Stopwatch.Frequency;
        _shuttleLastTick = now;
        if (dt <= 0) return;

        double target = _audio.Time - _shuttleRate * dt;
        if (target <= 0)
        {
            _audio.Seek(0);
            StopShuttle();
            UpdatePlayIcon();
            return;
        }

        _audio.Seek(target);
    }

    private void StopShuttle()
    {
        if (!_shuttleActive) return;
        _shuttleActive = false;
        _shuttleRate = 1;
        _shuttleTimer?.Stop();
        UpdatePlayIcon();
    }
}
