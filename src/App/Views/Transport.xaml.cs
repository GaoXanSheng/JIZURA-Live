using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using JizuraLive.Engine;

namespace JizuraLive.App.Views;

// 播放条（对位 Transport.vue）：播放/暂停、时间滑条 scrub、循环；时间跟随引擎
public partial class Transport : UserControl
{
    private bool _syncSlider;
    private bool _scrubbing;
    private readonly DispatcherTimer _timer;

    public Transport()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Sync();
        _timer.Start();
        Loaded += (_, _) => { Sync(); App.MainInstance!.Preview.Play(); };
    }

    private PreviewEngine Preview => App.MainInstance!.Preview;

    private void BtnPlay_Click(object sender, RoutedEventArgs e) => Preview.TogglePlay();

    private void SldTime_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncSlider) return;
        Preview.Seek(e.NewValue);
        UpdateTimeText();
    }

    private void SldTime_Down(object sender, MouseButtonEventArgs e) => _scrubbing = true;
    private void SldTime_Up(object sender, MouseButtonEventArgs e) => _scrubbing = false;

    private void ChkLoop_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        Preview.Loop = ChkLoop.IsChecked == true;
    }

    private void Sync()
    {
        var dur = Preview.Duration;
        _syncSlider = true;
        SldTime.Maximum = Math.Max(0.001, dur);
        if (!_scrubbing)
            SldTime.Value = Math.Min(Preview.CurrentTime, SldTime.Maximum);
        _syncSlider = false;
        UpdateTimeText();
        BtnPlay.Content = Preview.IsPlaying ? "\uE769" : "\uE768"; // ⏸ / ▶
    }

    private void UpdateTimeText() =>
        TblTime.Text = $"{J.FmtTime(Preview.CurrentTime)} / {J.FmtTime(Preview.Duration)}";
}
