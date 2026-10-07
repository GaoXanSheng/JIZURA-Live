using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using JizuraLive.Engine;

namespace JizuraLive.App.Views;

// 播放条（对位 Transport.vue）：播放/暂停、时间滑条 scrub、循环；时间跟随引擎。
// M3 适配：滑条拖动走引擎 scrub（拖动中画面跟手、音频静默，松手恢复，对位 barDown/barUp）；
// 并负责把 State 的音频投进引擎（State.AudioChanged → Preview.SetAudioSource）——
// 本控件常驻中栏，是引擎音频源最稳的接线点；时长经引擎重规划自然变为音频时长。
public partial class Transport : UserControl
{
    private bool _syncSlider;
    private bool _scrubbing;
    private bool _wired;
    private readonly DispatcherTimer _timer;

    public Transport()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Sync();
        _timer.Start();
        Loaded += (_, _) =>
        {
            var main = App.MainInstance;
            if (main != null && !_wired)
            {
                _wired = true;
                // EditorState.SetAudio 里的那次 Replan 发生在 AudioChanged 之前（拿不到音频），
                // 这里接通后引擎会再补一次带音频的重规划（SetAudioSource 内部处理）
                main.State.AudioChanged += WireAudioSource;
                WireAudioSource();
            }
            Sync();
            App.MainInstance!.Preview.Play();
        };
    }

    private PreviewEngine Preview => App.MainInstance!.Preview;

    /// <summary>音频投入/卸载（含 ResetAll 清空）：交给引擎换链，失败自动降级墙钟。</summary>
    private void WireAudioSource()
    {
        var st = App.MainInstance!.State;
        Preview.SetAudioSource(st.Audio, st.AudioPath);
    }

    private void BtnPlay_Click(object sender, RoutedEventArgs e) => Preview.TogglePlay();

    private void SldTime_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncSlider) return;
        Preview.Seek(e.NewValue);
        UpdateTimeText();
    }

    private void SldTime_Down(object sender, MouseButtonEventArgs e)
    {
        _scrubbing = true;
        Preview.BeginScrub(); // 拖动中音频静默跟随，松手恢复（对位 beginScrub）
    }
    private void SldTime_Up(object sender, MouseButtonEventArgs e)
    {
        _scrubbing = false;
        Preview.EndScrub();
    }

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
