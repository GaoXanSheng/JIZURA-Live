using System.Windows;
using System.Windows.Controls;
using JizuraLive.App.I18n;
using JizuraLive.Engine;
using JizuraLive.Export;
using Microsoft.Win32;

namespace JizuraLive.App.Views;

/// <summary>格式按钮选中态（Tag 留给格式值本身：Format_Click 读 Tag 判格式）。</summary>
public static class FmtState
{
    public static readonly DependencyProperty OnProperty =
        DependencyProperty.RegisterAttached("On", typeof(bool), typeof(FmtState), new PropertyMetadata(false));

    public static void SetOn(DependencyObject d, bool value) => d.SetValue(OnProperty, value);

    public static bool GetOn(DependencyObject d) => (bool)d.GetValue(OnProperty);
}

// 导出面板（对位 OutTab.vue 的导出勾选块）：格式单选 + 内容/背景勾选组合 → project.ExportCfg
// （ExportSpec 勾选模型清洗落盘）。导出走 SaveFileDialog → ExportJob；画幅/分辨率/fps 与设置页同源。
public partial class OutTab : UserControl
{
    private bool _built;
    private bool _syncing;

    /// <summary>导出进度窗（中止按钮的落点；模态期间面板不可点，属兜底）。</summary>
    private ExportProgressWindow? _active;

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
            foreach (var (v, key) in DirOptions()) CbCenterDir.Items.Add(new ComboBoxItem { Content = L.T(key), Tag = v });
        }
        S.StateChanged += OnState;
        L.LanguageChanged += OnLanguageChanged;
        App.MainInstance!.StatusChanged += RefreshStatus;
        Pull();
        RefreshStatus();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        S.StateChanged -= OnState;
        L.LanguageChanged -= OnLanguageChanged;
        if (App.MainInstance != null) App.MainInstance.StatusChanged -= RefreshStatus;
    }

    private void OnState() => Dispatcher.Invoke(Pull);

    /// <summary>语言切换：重建竖屏分列下拉（唯一带本地文案的下拉）＋ 刷状态小字与条件提示。</summary>
    private void OnLanguageChanged()
    {
        _syncing = true;
        try
        {
            var dir = SelectedTag(CbCenterDir);
            CbCenterDir.Items.Clear();
            foreach (var (v, key) in DirOptions()) CbCenterDir.Items.Add(new ComboBoxItem { Content = L.T(key), Tag = v });
            if (dir != null) SelectTag(CbCenterDir, dir);
        }
        finally
        {
            _syncing = false;
        }
        RefreshStatus();
        Pull();
    }

    /// <summary>状态小字（移植自 TopBar 的 RefreshStatus）：{W}×{H} · N 切分 · seed S · Spout→…/未开。</summary>
    private void RefreshStatus()
    {
        var main = App.MainInstance;
        if (main == null) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(RefreshStatus);
            return;
        }
        var plan = main.Preview.Plan;
        var head = plan != null ? L.F("Out.Status.PlanFmt", plan.W, plan.H, plan.Cuts.Count, plan.Seed) : "";
        var spout = main.Spout.IsRunning && !string.IsNullOrEmpty(main.Spout.SenderName)
            ? L.F("Out.Status.SpoutToFmt", main.Spout.SenderName)
            : L.T("Out.Status.SpoutOff");
        TblStatus.Text = head.Length > 0 ? head + " · " + spout : spout;
    }

    private static (string V, string Key)[] DirOptions() =>
        new[] { ("tb", "Out.Dir.Tb"), ("lr", "Out.Dir.Lr") };

    // ---------------- 画幅 / 输出参数（与设置页同源字段） ----------------

    private void CbAspect_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        var v = SelectedTag(CbAspect);
        if (v == null || v == S.Project.Aspect) return;
        S.Mutate(p => p.Aspect = v, L.F("Out.Mutate.AspectFmt", v));
    }

    private void CbRes_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        var v = SelectedTag(CbRes);
        if (v == null || v == S.Project.Res.ToString()) return;
        S.Mutate(p => p.Res = int.Parse(v), L.T("Out.Res"), replan: false);
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
        }, L.T("Out.Mutate.Center"));
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
        S.Mutate(p => ExportSpec.SetCfg(p, next), L.T("Out.Mutate.Cfg"), replan: false);
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
            MessageBox.Show(owner, invalid, L.T("Out.Msg.Title"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 字幕系统：云音乐装载/跟随匹配写入 KRC 逐字后可用（烤字幕走 ExportJob 的文字层）
        var res = ExportSpec.BuildSpec(cfg, new SpecCtx(
            SubOn: state.Subtitles is { Count: > 0 }, AudioLoaded: state.AudioPath != null));
        if (!res.Ok)
        {
            MessageBox.Show(owner, res.Error, L.T("Out.Msg.Title"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var spec = res.Spec!;

        var (filter, ext) = spec.Kind switch
        {
            "webm" => (L.T("Out.SaveDlg.Filter.Webm"), ".webm"),
            "png" => (L.T("Out.SaveDlg.Filter.Png"), ".zip"),
            "wav" => (L.T("Out.SaveDlg.Filter.Wav"), ".wav"),
            _ => (L.T("Out.SaveDlg.Filter.Mp4"), ".mp4"),
        };
        var name = string.IsNullOrWhiteSpace(state.Project.Title) ? "jizura" : state.Project.Title;
        var dlg = new SaveFileDialog { Filter = filter, FileName = name + ext, Title = L.T("Out.SaveDlg.Title") };
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

        // 进度块：准备中… → 模态窗（ExportProgressWindow 内部自绘进度）→ 收尾文案
        SetBusy(true, L.T("Out.Progress.Preparing"), 0);
        var ok = false;
        Exception? error = null;
        try
        {
            var win = new ExportProgressWindow(req) { Owner = owner };
            _active = win;
            ok = win.ShowDialog() == true;
            error = win.Error;
        }
        finally
        {
            _active = null;
            SetBusy(false, ok ? L.T("Out.Progress.Done") : L.T("Out.Progress.Canceled"), ok ? 1 : 0);
        }
        if (ok)
            MessageBox.Show(owner, L.F("Out.ExportDoneFmt", dlg.FileName), L.T("Main.Title"),
                MessageBoxButton.OK, MessageBoxImage.Information);
        else if (error != null)
            MessageBox.Show(owner, L.F("Out.ExportFailedFmt", error.Message), L.T("Main.Title"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        // 取消（无错误）静默返回
    }

    /// <summary>进度块显隐与填充（对位 Vue 的 v-if="busy || progressText !== '—'" + width: pct%）。</summary>
    private void SetBusy(bool busy, string text, double pct)
    {
        var p = Math.Clamp(pct, 0, 1);
        ProgFillCol.Width = new GridLength(p, GridUnitType.Star);
        ProgRestCol.Width = new GridLength(1 - p, GridUnitType.Star);
        TblProg.Text = text;
        BtnAbort.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ProgWrap.Visibility = busy || text != "—" ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>中止：关掉导出进度窗（Closed 里 CTS.Cancel + ExportBusy 归位）。</summary>
    private void BtnAbort_Click(object sender, RoutedEventArgs e) => _active?.Close();

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

            // 格式单选态（btn-primary / btn-ghost 由 FmtState.On 的模板触发器接管）
            FmtState.SetOn(BtnFmtMp4, c.Format == "mp4");
            FmtState.SetOn(BtnFmtWebm, c.Format == "webm");
            FmtState.SetOn(BtnFmtPng, c.Format == "png");

            // 条件显隐（对位 showLayered / showToFile / cfg.bg 分支）
            var mp4 = c.Format == "mp4";
            CkBg.IsEnabled = !mp4;
            BgStyleRow.Visibility = c.Bg ? Visibility.Visible : Visibility.Collapsed;
            TblBgNote.Visibility = c.Bg && c.BgStyle != "off" ? Visibility.Visible : Visibility.Collapsed;
            TransparentRow.Visibility = c.Bg ? Visibility.Collapsed : Visibility.Visible;
            TblMp4TransWarn.Visibility = !c.Bg && mp4 ? Visibility.Visible : Visibility.Collapsed;
            CkLayered.Visibility = c.Format == "png" && c.Bg && c.Visuals ? Visibility.Visible : Visibility.Collapsed;
            CkToFile.Visibility = mp4 ? Visibility.Visible : Visibility.Collapsed;

            // 校验与提示（.note 左竖线块随文案显隐）
            var invalid = ExportSpec.UiInvalidMessage(c);
            TblInvalid.Text = invalid;
            NoteInvalid.Visibility = invalid.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            BtnExport.IsEnabled = invalid.Length == 0;
            var fmtName = c.Format == "mp4" ? L.T("Out.Fmt.Mp4") : c.Format == "webm" ? L.T("Out.Fmt.Webm") : L.T("Out.Fmt.Png");
            var audioNote = mp4 || !c.Audio
                ? ""
                : L.F("Out.AudioNote.NoEmbedFmt", fmtName);
            TblAudioNote.Text = audioNote;
            NoteAudio.Visibility = audioNote.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            NoteSubs.Visibility = invalid.Length == 0 && audioNote.Length == 0 && c.Subs
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
