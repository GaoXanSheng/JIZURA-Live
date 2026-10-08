using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using JizuraLive.App.I18n;
using JizuraLive.Engine;
using JizuraLive.Logging;
using JizuraLive.Server;
using Microsoft.Win32;

namespace JizuraLive.App.Views;

// 音频投入面板（对位 frontend/src/components/lyrics/AudioPanel.vue）：
// 文件选择 → NAudio 解码 → AudioAnalyzer 节拍分析（后台任务，UI 显示进度/耗时）→ State.SetAudio，
// 以及 BPM / 开始(秒) / 行时长 / 对齐节拍 / 清除手动时间 / 打点同步入口。
// 云端音乐与 .krc 歌词导入随云端歌词库在后续里程碑接入。
public partial class AudioPanel : UserControl
{
    private bool _syncing;
    private bool _subscribed;
    private bool _loading;
    private string _loadNote = ""; // 最近一次分析的结果行（耗时等）
    private Stopwatch? _activeSw;
    private readonly DispatcherTimer _elapsedTimer;

    public AudioPanel()
    {
        InitializeComponent();
        _elapsedTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        Loaded += (_, _) =>
        {
            L.LanguageChanged -= ApplyTexts;
            L.LanguageChanged += ApplyTexts;
            if (_subscribed) return;
            _subscribed = true;
            var main = App.MainInstance!;
            main.State.StateChanged += () => Dispatcher.Invoke(SyncFields);
            main.State.AudioChanged += () => Dispatcher.Invoke(RefreshAudio);
            TapSession.Changed += RefreshTapBtn;
            SyncFields();
            RefreshAudio();
            RefreshTapBtn();
        };
        Unloaded += (_, _) => L.LanguageChanged -= ApplyTexts;
    }

    private EditorState State => App.MainInstance!.State;

    // ---------------- 状态 → UI ----------------

    private void SyncFields()
    {
        if (_loading) return;
        var t = State.Project.Timing;
        _syncing = true;
        if (!TbBpm.IsKeyboardFocused) TbBpm.Text = t.Bpm > 0 ? F(t.Bpm) : "";
        if (!TbOffset.IsKeyboardFocused) TbOffset.Text = F(t.Offset);
        if (!TbLineScale.IsKeyboardFocused) TbLineScale.Text = F(t.LineScale);
        ChkSnap.IsChecked = t.Snap;
        // BPM 占位：有音频时显示检测结果（对位 bpmPlaceholder）
        PhBpm.Text = State.Audio != null ? L.F("Audio.AutoBpm", State.Audio.Bpm) : L.T("Audio.Placeholder");
        _syncing = false;
    }

    private void RefreshAudio()
    {
        var name = State.AudioFileName;
        var note = _loadNote;
        if (string.IsNullOrEmpty(note) && State.IsAudioRestored)
            note = L.T("Audio.RestoredNote");

        TblAudioName.Text = name == null
            ? L.T("Audio.NoSong")
            : State.Audio != null
                ? L.F("Audio.SongInfo", name, J.FmtTime(State.Audio.Duration), State.Audio.Bpm, note)
                : State.IsRestoringAudio
                    ? L.F("Audio.Restoring", name)
                    : name;
        SyncFields();
    }

    /// <summary>云端音乐（对位 AudioPanel 的 openCloud → CloudMusicModal）。</summary>
    private void BtnCloud_Click(object sender, RoutedEventArgs e) =>
        CloudPanel.Open(Window.GetWindow(this)!);

    private void RefreshTapBtn() =>
        BtnTap.Content = TapSession.Active ? L.T("Audio.TapStop") : L.T("Audio.Tap");

    /// <summary>语言切换：重刷全部动态文本（XAML 静态文本由 DynamicResource 自刷）。</summary>
    private void ApplyTexts()
    {
        SyncFields();
        RefreshAudio();
        RefreshTapBtn();
    }

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static double P(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    // ---------------- 音频投入 ----------------

    private void BtnLoad_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = L.T("Audio.LoadDlg.Title"),
            Filter = L.T("Audio.LoadDlg.Filter"),
        };
        if (dlg.ShowDialog(Window.GetWindow(this)!) != true) return;
        _ = LoadAudioAsync(dlg.FileName);
    }

    /// <summary>
    /// 载入歌曲：后台解码（NAudio/MediaFoundation → 单声道 11025Hz）+ 节拍分析（AudioAnalyzer，
    /// 与前端 10_audio.js 同一算法），完成后 State.SetAudio（内部会 Replan 并广播 AudioChanged）。
    /// </summary>
    private async Task LoadAudioAsync(string path)
    {
        if (_loading) return;
        _loading = true;
        BtnLoad.IsEnabled = false;
        ProgTrack.Visibility = Visibility.Visible;
        TblProg.Visibility = Visibility.Visible;
        var sw = Stopwatch.StartNew();
        _activeSw = sw;
        _elapsedTimer.Tick += OnElapsedTick;
        _elapsedTimer.Start();
        try
        {
            var audio = await Task.Run(() =>
            {
                var mono = AudioAnalyzer.DecodeMonoAnalyzeRate(path, out var duration);
                var res = AudioAnalyzer.Analyze(mono, AudioAnalyzer.AnalyzeRate, duration);
                return new AudioData
                {
                    Duration = duration,
                    Bpm = res.Bpm,
                    Beats = res.Beats,
                    Energy = res.Energy,
                    EnergyRate = res.EnergyRate,
                    Peaks = res.Peaks,
                    Pitch = res.Pitch,
                    PitchRate = res.PitchRate,
                };
            });
            sw.Stop();
            _loadNote = L.F("Audio.AnalyzeNote", sw.ElapsedMilliseconds);
            State.SetAudio(audio, path, Path.GetFileName(path));
            Log.Info($"[AudioPanel] 歌曲已载入：{path}（{J.FmtTime(audio.Duration)}，{audio.Bpm:0.0} BPM，分析 {_loadNote}）");
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log.Error("[AudioPanel] 歌曲载入失败：" + path, ex);
            MessageBox.Show(Window.GetWindow(this)!, L.F("Audio.LoadFailed", ex.Message), L.T("Audio.LoadFailed.Title"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _elapsedTimer.Stop();
            _elapsedTimer.Tick -= OnElapsedTick;
            ProgTrack.Visibility = Visibility.Collapsed;
            TblProg.Visibility = Visibility.Collapsed;
            BtnLoad.IsEnabled = true;
            _loading = false;
            RefreshAudio();
            RefreshTapBtn();
        }
    }

    private void OnElapsedTick(object? sender, EventArgs e)
    {
        TblProg.Text = _activeSw == null
            ? L.T("Audio.Analyzing")
            : L.F("Audio.Analyzing.Elapsed", _activeSw.Elapsed.TotalSeconds);
        // 槽内来回扫的填充（对位不确定态进度条：解码时长不可知，只表示在跑）
        var w = ProgTrack.ActualWidth;
        if (w > 4 && _activeSw != null)
        {
            var frac = _activeSw.Elapsed.TotalSeconds % 1.6 / 1.6;
            var breath = 1 - Math.Abs(frac * 2 - 1);
            ProgFill.Width = Math.Max(24, w * (0.15 + 0.85 * breath));
        }
    }

    // ---------------- 时序字段 ----------------

    private void NumBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            // 让焦点走开触发 LostFocus 提交；提交后焦点还给输入框方便连续调
            var tb = (TextBox)sender;
            var hadFocus = tb.IsKeyboardFocused;
            tb.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            if (hadFocus) tb.Focus();
            e.Handled = true;
        }
    }

    private void TbBpm_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var v = Math.Max(0, P(TbBpm.Text));
        if (Math.Abs(State.Project.Timing.Bpm - v) < 1e-9) { SyncFields(); return; }
        // BPM/offset/行时长：Vue 里不进任何历史（onBpm 只有 replan）；这里走观感栈，
        // 避免把数值微调混进歌词/打点的 Ctrl+Z 内容史
        State.Mutate(p => p.Timing.Bpm = v, L.T("Audio.Mutate.Bpm"), replan: true, look: true);
    }

    private void TbOffset_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var v = Math.Max(0, P(TbOffset.Text));
        if (Math.Abs(State.Project.Timing.Offset - v) < 1e-9) { SyncFields(); return; }
        State.Mutate(p => p.Timing.Offset = v, L.T("Audio.Offset"), replan: true, look: true);
    }

    private void TbLineScale_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var v = Math.Clamp(P(TbLineScale.Text) == 0 ? 1 : P(TbLineScale.Text), 0.3, 4);
        if (Math.Abs(State.Project.Timing.LineScale - v) < 1e-9) { SyncFields(); return; }
        State.Mutate(p => p.Timing.LineScale = v, L.T("Audio.LineScale"), replan: true, look: true);
    }

    private void ChkSnap_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || !IsLoaded) return;
        var v = ChkSnap.IsChecked == true;
        if (State.Project.Timing.Snap == v) return;
        State.Mutate(p => p.Timing.Snap = v, L.T("Audio.Snap"), replan: true, look: true);
    }

    private void BtnResetTimes_Click(object sender, RoutedEventArgs e)
    {
        if (State.Project.Timing.LineTimes.Count == 0) return;
        // 手动行时刻属于内容编辑（Vue 的 edSnap 含 lineTimes）：edit 栈，可 Ctrl+Z
        State.Mutate(p => p.Timing.LineTimes.Clear(), L.T("Audio.ResetTimes"), replan: true);
    }

    private void BtnTap_Click(object sender, RoutedEventArgs e)
    {
        if (TapSession.Active) TapSession.Stop(); // 对位 onTapBtn：stopTap()（不暂停播放）
        else TapSession.Start(0);
    }
}
