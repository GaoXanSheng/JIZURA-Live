using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using JizuraLive.App.Cloud;
using JizuraLive.App.I18n;
using JizuraLive.Server.Models;

namespace JizuraLive.App.Views;

/// <summary>
/// 播放器模式的「正在播放」控制块：进度条 + 上/播/下 + 时间 + 队列位置 + 操作 + 大字歌名 + 歌手/meta。
/// 状态全在 <see cref="CloudPlayService"/>（与底部常驻条同一个会话），本控件只是它的视图。
/// </summary>
public partial class CloudNowPlaying : UserControl
{
    private const double ThumbSize = 11;

    private CloudPlayService? _svc;
    private double _frac;
    private bool _scrubbing;
    private bool _volDragging;

    public CloudNowPlaying()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _svc = App.MainInstance?.Cloud;
            if (_svc == null) return;
            _svc.Changed += Refresh;
            _svc.Tick += RefreshProgress;
            L.LanguageChanged += Refresh;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            if (_svc != null)
            {
                _svc.Changed -= Refresh;
                _svc.Tick -= RefreshProgress;
            }
            L.LanguageChanged -= Refresh;
        };
    }

    private void Refresh()
    {
        if (_svc == null) return;
        var song = _svc.Current;
        var has = song != null;

        BtnPrev.IsEnabled = has;
        BtnNext.IsEnabled = has;
        BtnPlay.IsEnabled = has;
        BtnLoadToEditor.IsEnabled = has;
        IcPlay.Data = (Geometry)FindResource(_svc.IsPlaying ? "IconPause" : "IconPlay");

        TblTitle.Text = has ? song!.Name : L.T("Cloud.BarIdle");
        TblMeta.Text = has
            ? string.Join(" · ", new[] { song!.Singer, song.Album }.Where(x => !string.IsNullOrEmpty(x)))
            : L.T("Cloud.PlayerEmpty");
        TblQueue.Text = has ? $"{_svc.QueueIndex + 1} / {_svc.QueueCount}" : "";
        TblStatus.Text = _svc.Status;
        if (!_volDragging) VolSlider.Value = _svc.Muted ? 0 : _svc.Volume;
        IcVol.Data = (Geometry)FindResource(_svc.Muted ? "IconVolumeMute" : "IconVolume");
        if (!_scrubbing) RefreshProgress();
    }

    private void RefreshProgress()
    {
        if (_svc == null || _scrubbing) return;
        var dur = _svc.Duration;
        SetBar(dur > 0 ? _svc.Time / dur : 0);
        TblTime.Text = $"{Fmt((long)_svc.Time)} / {Fmt((long)dur)}";
    }

    private static string Fmt(long sec)
    {
        sec = Math.Max(0, sec);
        return $"{sec / 60}:{sec % 60:D2}";
    }

    // ---------------- 控制 ----------------

    private void BtnPlay_Click(object sender, RoutedEventArgs e) => _svc?.Toggle();

    private void BtnPrev_Click(object sender, RoutedEventArgs e) => _svc?.Prev();

    private void BtnNext_Click(object sender, RoutedEventArgs e) => _svc?.Next();

    private void BtnPick_Click(object sender, RoutedEventArgs e) => CloudPanel.Open(Window.GetWindow(this)!);

    /// <summary>把当前这首云歌载入编辑器（下载+分析+写工程），载入后退出播放器模式去编辑。</summary>
    private async void BtnLoadToEditor_Click(object sender, RoutedEventArgs e)
    {
        if (_svc?.Current is not { } song) return;
        BtnLoadToEditor.IsEnabled = false;
        try
        {
            var quality = _svc.Quality;
            _svc.SetStatus(L.T("Cloud.PlayerLoading"));
            await SongLoader.LoadAsync(App.MainInstance!.State, song.Source, song, quality,
                s => _svc.SetStatus(s));
            App.MainInstance.SetPlayerMode(false, restoreSnapshot: false); // 回编辑器看刚载入的这支 PV（别拿旧快照盖掉）
        }
        catch (Exception ex)
        {
            _svc.SetStatus(L.F("Cloud.PreviewFailed", ex.Message));
        }
        finally
        {
            BtnLoadToEditor.IsEnabled = true;
        }
    }

    // ---------------- 音量（与底部条同一套：拖动中只改链，松手落盘） ----------------

    private void Vol_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_svc == null) return;
        _volDragging = true;
        _svc.SetVolume(e.NewValue, false, persist: false);
        _volDragging = false;
    }

    private void Vol_Commit(object sender, MouseButtonEventArgs e) =>
        _svc?.SetVolume(VolSlider.Value, false, persist: true);

    private void Vol_LostCapture(object sender, MouseEventArgs e) =>
        _svc?.SetVolume(VolSlider.Value, false, persist: true);

    private void BtnMute_Click(object sender, RoutedEventArgs e) =>
        _svc?.SetVolume(null, !(_svc?.Muted ?? false));

    // ---------------- 进度条（自绘，与底部条同款手法） ----------------

    private void Prog_Down(object sender, MouseButtonEventArgs e)
    {
        if (_svc?.HasTrack != true) return;
        _scrubbing = true;
        BarProg.CaptureMouse();
        SeekTo(e.GetPosition(BarProg).X);
        e.Handled = true;
    }

    private void Prog_Move(object sender, MouseEventArgs e)
    {
        if (_scrubbing) SeekTo(e.GetPosition(BarProg).X);
    }

    private void Prog_Up(object sender, MouseButtonEventArgs e) => EndScrub();

    private void Prog_LostCapture(object sender, MouseEventArgs e) => EndScrub();

    private void EndScrub()
    {
        if (!_scrubbing) return;
        _scrubbing = false;
        BarProg.ReleaseMouseCapture();
        _svc?.SeekTo(_frac);
    }

    private void SeekTo(double x)
    {
        var w = BarProg.ActualWidth;
        if (w <= 0) return;
        SetBar(Math.Clamp(x / w, 0, 1));
        var dur = _svc?.Duration ?? 0;
        TblTime.Text = $"{Fmt((long)(_frac * dur))} / {Fmt((long)dur)}";
    }

    private void Prog_SizeChanged(object sender, SizeChangedEventArgs e) => SetBar(_frac);

    private void SetBar(double f)
    {
        _frac = Math.Clamp(f, 0, 1);
        var w = BarProg.ActualWidth;
        ProgFill.Width = w;
        ProgFillShift.X = -w * (1 - _frac);
        ProgThumbShift.X = Math.Max(0, (w - ThumbSize) * _frac);
    }
}
