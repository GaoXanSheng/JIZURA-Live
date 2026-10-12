using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using JizuraLive.App.Cloud;
using JizuraLive.App.I18n;
using JizuraLive.Server.Models;

namespace JizuraLive.App.Views;

/// <summary>
/// 云试听常驻条（两个宿主共用：主窗口底部、云音乐面板内）。自己不含播放状态——
/// 状态全在 <see cref="CloudPlayService"/> 里，本控件只是它的视图（Changed 整体刷新 / Tick 只刷进度）。
/// Compact = 面板内那侧：去掉音量块（面板本身窄，且音量在主界面条上已有）。
/// </summary>
public partial class CloudBar : UserControl
{
    private const double ThumbSize = 12;

    private CloudPlayService? _svc;
    private double _frac;
    private bool _scrubbing;
    private bool _volDragging;

    /// <summary>紧凑模式（面板宿主）：隐藏音量块。构造/加载前由宿主设置。</summary>
    public bool Compact { get; set; }

    public CloudBar()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (Compact) VolBox.Visibility = Visibility.Collapsed;
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

    // ---------------- 刷新 ----------------

    /// <summary>整体刷新（换曲/播放态/状态文案/队列位置/音量）。</summary>
    private void Refresh()
    {
        if (_svc == null) return;
        var song = _svc.Current;
        var has = song != null;

        BtnPrev.IsEnabled = has;
        BtnNext.IsEnabled = has;
        BtnPlay.IsEnabled = has;
        IcPlay.Data = (Geometry)FindResource(_svc.IsPlaying ? "IconPause" : "IconPlay");

        TblTitle.Text = has ? song!.Name : L.T("Cloud.BarIdle");
        TblStatus.Text = has ? StatusLine(song!, _svc.Status) : "";
        TblQueue.Text = has ? $"{_svc.QueueIndex + 1}/{_svc.QueueCount}" : "";
        SetCover(has ? song!.Cover : "", has ? song!.Name : "");

        if (!_volDragging) VolSlider.Value = _svc.Muted ? 0 : _svc.Volume;
        IcVol.Data = (Geometry)FindResource(_svc.Muted ? "IconVolumeMute" : "IconVolume");
        if (!_scrubbing) RefreshProgress();
    }

    private static string StatusLine(KgSong song, string status) =>
        status.Length > 0 ? status : song.Singer;

    /// <summary>只刷进度与时间（200ms 一次调用，别在这里动列表）。</summary>
    private void RefreshProgress()
    {
        if (_svc == null || _scrubbing) return;
        var dur = _svc.Duration;
        var pos = _svc.Time;
        SetBar(dur > 0 ? pos / dur : 0);
        TblTime.Text = $"{FmtDur((long)pos)} / {FmtDur((long)dur)}";
    }

    private static string FmtDur(long sec)
    {
        sec = Math.Max(0, sec);
        return $"{sec / 60}:{sec % 60:D2}";
    }

    private void SetCover(string url, string name)
    {
        TblCoverInitial.Text = name.Length > 0 ? name[..1] : "";
        TblCover.Background = (Brush)FindResource("Raised");
        if (url.Length == 0) return;
        var brush = new ImageBrush();
        var bmp = CoverCache.Get(url, bmp =>
        {
            if (bmp == null) return;
            brush.ImageSource = bmp;
            TblCover.Background = brush;
            JzAnim.FadeIn(TblCover); // 淡入即可（FadeIn 自带 0→1，别手动设 Opacity=0：播完会回落成 0）
        });
        if (bmp != null)
        {
            brush.ImageSource = bmp;
            TblCover.Background = brush;
        }
    }

    // ---------------- 传输键 ----------------

    private void BtnPlay_Click(object sender, RoutedEventArgs e) => _svc?.Toggle();

    private void BtnPrev_Click(object sender, RoutedEventArgs e) => _svc?.Prev();

    private void BtnNext_Click(object sender, RoutedEventArgs e) => _svc?.Next();

    // ---------------- 进度条（自绘，与 Transport 同款） ----------------

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
        _svc?.SeekTo(_frac); // 松手才真正定位（拖动中只跟手）
    }

    private void SeekTo(double x)
    {
        var w = BarProg.ActualWidth;
        if (w <= 0) return;
        SetBar(Math.Clamp(x / w, 0, 1));
        var dur = _svc?.Duration ?? 0;
        TblTime.Text = $"{FmtDur((long)(_frac * dur))} / {FmtDur((long)dur)}";
    }

    private void Prog_SizeChanged(object sender, SizeChangedEventArgs e) => SetBar(_frac);

    private void SetBar(double f)
    {
        _frac = Math.Clamp(f, 0, 1);
        var w = BarProg.ActualWidth;
        ProgFill.Width = w; // 只在这一处写宽度，同值写入不触发失效
        ProgFillShift.X = -w * (1 - _frac);
        ProgThumbShift.X = Math.Max(0, (w - ThumbSize) * _frac);
    }

    // ---------------- 音量 ----------------

    private void Vol_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_svc == null) return;
        _volDragging = true;
        _svc.SetVolume(e.NewValue, false, persist: false); // 拖动中只改播放链
        _volDragging = false;
    }

    private void Vol_Commit(object sender, MouseButtonEventArgs e) =>
        _svc?.SetVolume(VolSlider.Value, false, persist: true);

    private void Vol_LostCapture(object sender, MouseEventArgs e) =>
        _svc?.SetVolume(VolSlider.Value, false, persist: true);

    private void BtnMute_Click(object sender, RoutedEventArgs e) =>
        _svc?.SetVolume(null, !(_svc?.Muted ?? false));
}
