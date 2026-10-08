using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using JizuraLive.Engine;
using JizuraLive.Export;
using Microsoft.Win32;

namespace JizuraLive.App.Views;

// 导出面板（对位 OutTab.vue 的导出勾选块）：格式单选 + 内容/背景勾选组合 → project.ExportCfg
// （ExportSpec 勾选模型清洗落盘）。导出按钮为 M4 占位；画幅/分辨率/fps 与设置页同源。
public partial class OutTab : UserControl
{
    private bool _built;
    private bool _syncing;

    private EditorState S => App.MainInstance!.State;

    private static readonly string[] Aspects = { "16:9", "9:16", "4:3", "3:4", "1:1", "4:5", "21:9" };
    private static readonly (int V, string Label)[] ResOptions =
        { (720, "720p"), (1080, "1080p"), (1440, "1440p"), (2160, "4K") };
    private static readonly int[] FpsOptions = { 24, 30, 60 };

    public OutTab()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_built)
        {
            _built = true;
            foreach (var a in Aspects) CbAspect.Items.Add(new ComboBoxItem { Content = a, Tag = a });
            foreach (var (v, label) in ResOptions) CbRes.Items.Add(new ComboBoxItem { Content = label, Tag = v.ToString() });
            foreach (var f in FpsOptions) CbFps.Items.Add(new ComboBoxItem { Content = f.ToString(), Tag = f.ToString() });
            foreach (var (v, label) in ExportSpec.BgStyles)
                CbBgStyle.Items.Add(new ComboBoxItem { Content = label, Tag = v });
            foreach (var (v, label) in DirOptions()) CbCenterDir.Items.Add(new ComboBoxItem { Content = label, Tag = v });
        }
        S.StateChanged += OnState;
        Pull();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => S.StateChanged -= OnState;

    private void OnState() => Dispatcher.Invoke(Pull);

    private static (string V, string Label)[] DirOptions() =>
        new[] { ("tb", "上下分列"), ("lr", "左右分列") };

    // ---------------- 画幅 / 输出参数（与设置页同源字段） ----------------

    private void CbAspect_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        var v = SelectedTag(CbAspect);
        if (v == null || v == S.Project.Aspect) return;
        S.Mutate(p => p.Aspect = v, "画幅：" + v);
    }

    private void CbRes_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        var v = SelectedTag(CbRes);
        if (v == null || v == S.Project.Res.ToString()) return;
        S.Mutate(p => p.Res = int.Parse(v), "分辨率", replan: false);
    }

    private void CbFps_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        var v = SelectedTag(CbFps);
        if (v == null || v == S.Project.Fps.ToString()) return;
        S.Mutate(p => p.Fps = int.Parse(v), "fps");
    }

    private void Center_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var free = CkCenterFree.IsChecked == true;
        var dir = SelectedTag(CbCenterDir) ?? "tb";
        S.Mutate(p =>
        {
            p.CenterFree = free;
            p.CenterDir = dir;
        }, "留出画面中央");
    }

    // ---------------- 导出勾选（ExportSpec 勾选模型） ----------------

    private ExportCfg Cfg() => ExportSpec.CfgOf(S.Project);

    private void Format_Click(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        PatchCfg((string)((Button)sender).Tag!, null);
    }

    private void Cfg_Changed(object sender, RoutedEventArgs e) => PatchCfg(null, null);

    private void BgStyle_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        PatchCfg(null, SelectedTag(CbBgStyle));
    }

    /// <summary>勾选合并进 project.ExportCfg（normalize 后落盘，键名与 TS 契约一致）。</summary>
    private void PatchCfg(string? format, string? bgStyle)
    {
        var c = Cfg();
        var next = ReadChecks(c with { Format = format ?? c.Format, BgStyle = bgStyle ?? c.BgStyle });
        if (next == c) return;
        S.Mutate(p => ExportSpec.SetCfg(p, next), "导出设置", replan: false);
    }

    /// <summary>从控件读整套勾选（Vue 的 setCheck 逐项写，这里合并为一次事务）。</summary>
    private ExportCfg ReadChecks(ExportCfg c) => c with
    {
        Visuals = CkVisuals.IsChecked == true,
        Subs = CkSubs.IsChecked == true,
        Bg = CkBg.IsChecked == true,
        Audio = CkAudio.IsChecked == true,
        Layered = CkLayered.IsChecked == true,
        ToFile = CkToFile.IsChecked == true,
    };

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this)!;
        var state = S;
        if (state.Plan == null) return;

        var cfg = ExportSpec.CfgOf(state.Project);
        var invalid = ExportSpec.UiInvalidMessage(cfg);
        if (invalid.Length > 0)
        {
            MessageBox.Show(owner, invalid, "导出", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 字幕系统：云音乐装载/跟随匹配写入 KRC 逐字后可用（烤字幕走 ExportJob 的文字层）
        var res = ExportSpec.BuildSpec(cfg, new SpecCtx(
            SubOn: state.Subtitles is { Count: > 0 }, AudioLoaded: state.AudioPath != null));
        if (!res.Ok)
        {
            MessageBox.Show(owner, res.Error, "导出", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var spec = res.Spec!;

        var (filter, ext) = spec.Kind switch
        {
            "webm" => ("WebM 视频 (*.webm)|*.webm", ".webm"),
            "png" => ("PNG 序列 (*.zip)|*.zip", ".zip"),
            "wav" => ("WAV 音频 (*.wav)|*.wav", ".wav"),
            _ => ("MP4 视频 (*.mp4)|*.mp4", ".mp4"),
        };
        var name = string.IsNullOrWhiteSpace(state.Project.Title) ? "jizura" : state.Project.Title;
        var dlg = new SaveFileDialog { Filter = filter, FileName = name + ext, Title = "导出到" };
        if (dlg.ShowDialog(owner) != true) return;

        var req = new ExportRequest
        {
            Project = state.Project,
            Plan = state.Plan,
            Spec = spec,
            OutputPath = dlg.FileName,
            AudioPath = state.AudioPath,
            WavToo = res.WavToo,
            SubtitleLines = state.Subtitles,
            SubtitleCfg = state.SubtitleCfg,
        };

        var win = new ExportProgressWindow(req) { Owner = owner };
        var ok = win.ShowDialog() == true;
        if (ok)
            MessageBox.Show(owner, "导出完成：\n" + dlg.FileName, "字面 Live",
                MessageBoxButton.OK, MessageBoxImage.Information);
        else if (win.Error != null)
            MessageBox.Show(owner, "导出失败：\n" + win.Error.Message, "字面 Live",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        // 取消（无错误）静默返回
    }

    // ---------------- 回刷 ----------------

    private void Pull()
    {
        if (App.MainInstance == null) return;
        _syncing = true;
        try
        {
            var p = S.Project;
            SelectTag(CbAspect, Aspects.Contains(p.Aspect) ? p.Aspect : "16:9");
            SelectTag(CbRes, ResOptions.Any(o => o.V == p.Res) ? p.Res.ToString() : "1080");
            SelectTag(CbFps, FpsOptions.Contains(p.Fps) ? p.Fps.ToString() : "24");
            SetChecked(CkCenterFree, p.CenterFree);
            SelectTag(CbCenterDir, p.CenterDir == "lr" ? "lr" : "tb");
            CenterDirRow.Visibility = p.CenterFree ? Visibility.Visible : Visibility.Collapsed;

            var c = Cfg();
            SetChecked(CkVisuals, c.Visuals);
            SetChecked(CkSubs, c.Subs);
            SetChecked(CkBg, c.Bg);
            SetChecked(CkAudio, c.Audio);
            SetChecked(CkLayered, c.Layered);
            SetChecked(CkToFile, c.ToFile);
            SelectTag(CbBgStyle, c.BgStyle);

            // 格式单选态
            BtnFmtMp4.Background = c.Format == "mp4" ? (Brush)FindResource("Amber") : (Brush)FindResource("Raised");
            BtnFmtMp4.Foreground = c.Format == "mp4" ? (Brush)FindResource("Ink") : (Brush)FindResource("Fg");
            BtnFmtWebm.Background = c.Format == "webm" ? (Brush)FindResource("Amber") : (Brush)FindResource("Raised");
            BtnFmtWebm.Foreground = c.Format == "webm" ? (Brush)FindResource("Ink") : (Brush)FindResource("Fg");
            BtnFmtPng.Background = c.Format == "png" ? (Brush)FindResource("Amber") : (Brush)FindResource("Raised");
            BtnFmtPng.Foreground = c.Format == "png" ? (Brush)FindResource("Ink") : (Brush)FindResource("Fg");

            // 条件显隐（对位 showLayered / showToFile / cfg.bg 分支）
            var mp4 = c.Format == "mp4";
            CkBg.IsEnabled = !mp4;
            BgStyleRow.Visibility = c.Bg ? Visibility.Visible : Visibility.Collapsed;
            TblBgNote.Visibility = c.Bg && c.BgStyle != "off" ? Visibility.Visible : Visibility.Collapsed;
            TransparentRow.Visibility = c.Bg ? Visibility.Collapsed : Visibility.Visible;
            TblMp4TransWarn.Visibility = !c.Bg && mp4 ? Visibility.Visible : Visibility.Collapsed;
            CkLayered.Visibility = c.Format == "png" && c.Bg && c.Visuals ? Visibility.Visible : Visibility.Collapsed;
            CkToFile.Visibility = mp4 ? Visibility.Visible : Visibility.Collapsed;

            // 校验与提示
            var invalid = ExportSpec.UiInvalidMessage(c);
            TblInvalid.Text = invalid;
            TblInvalid.Visibility = invalid.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            BtnExport.IsEnabled = invalid.Length == 0;
            var fmtName = c.Format == "mp4" ? "MP4 视频" : c.Format == "webm" ? "WebM 视频" : "PNG 序列";
            var audioNote = mp4 || !c.Audio
                ? ""
                : $"「{fmtName}」无法内嵌音轨：导出时会询问是否另存一个 WAV 音频文件";
            TblAudioNote.Text = audioNote;
            TblAudioNote.Visibility = audioNote.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            TblSubsNote.Visibility = invalid.Length == 0 && audioNote.Length == 0 && c.Subs
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        finally
        {
            _syncing = false;
        }
    }

    // ---------------- 小工具 ----------------

    private static string? SelectedTag(ComboBox cb)
        => cb.SelectedItem is ComboBoxItem it ? (string?)it.Tag : null;

    private static void SelectTag(ComboBox cb, string? tag)
    {
        if (tag == null) return;
        foreach (var i in cb.Items.OfType<ComboBoxItem>())
            if ((string)i.Tag! == tag)
            {
                if (!ReferenceEquals(cb.SelectedItem, i)) cb.SelectedItem = i;
                return;
            }
        if (cb.Items.Count > 0) cb.SelectedIndex = 0;
    }

    private static void SetChecked(CheckBox cb, bool v)
    {
        if (cb.IsChecked != v) cb.IsChecked = v;
    }
}
