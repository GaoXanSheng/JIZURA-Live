using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using JizuraLive.Engine;

namespace JizuraLive.App.Views;

// 设置面板（对位 SettingsTab.vue）：
// 中央を空ける（centerFree/zone/gap/dir）、画幅/分辨率/fps/背景（project，即时生效）、
// 渲染窗口分辨率/帧率 + Spout 组（App Settings 草稿，点「重启渲染引擎」统一生效）。
public partial class SettingsTab : UserControl
{
    private bool _built;
    private bool _syncing;
    private bool _dirty;

    private EditorState S => App.MainInstance!.State;

    // 对位 ASPECTS / OutTab 的分辨率与 fps 档位
    private static readonly string[] Aspects = { "16:9", "9:16", "4:3", "3:4", "1:1", "4:5", "21:9" };
    private static readonly (int V, string Label)[] ResOptions =
        { (720, "720p"), (1080, "1080p"), (1440, "1440p"), (2160, "4K") };
    private static readonly int[] FpsOptions = { 12, 15, 18, 24, 25, 30, 48, 50, 60, 90, 120 };
    private static readonly (string V, string Label)[] KeyBgOptions =
        { ("off", "常规（风格背景）"), ("green", "绿幕（用于合成）"), ("black", "黑幕（用于合成）") };
    private static readonly (string V, string Label)[] ZoneOptions =
    {
        ("center", "画面中央（文字分列两侧/上下）"), ("left", "左侧留白（文字排右侧）"),
        ("right", "右侧留白（文字排左侧）"), ("top", "顶部留白（文字排下方）"), ("bottom", "底部留白（文字排上方）"),
    };
    private static readonly (string V, string Label)[] DirOptions =
        { ("tb", "上下留白"), ("lr", "左右留白") };

    private const int ResMin = 64, ResMax = 4096;

    public SettingsTab()
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
            BuildComboItems();
            CbCenterZone.SelectionChanged += (_, _) => CenterDirRow.Visibility =
                SelectedTag(CbCenterZone) == "center" ? Visibility.Visible : Visibility.Collapsed;
        }
        S.StateChanged += OnState;
        Pull();
        PullSettingsDraft();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => S.StateChanged -= OnState;

    private void OnState() => Dispatcher.Invoke(Pull);

    private void BuildComboItems()
    {
        if (CbCenterZone.Items.Count > 0) return;
        foreach (var (v, label) in ZoneOptions) CbCenterZone.Items.Add(new ComboBoxItem { Content = label, Tag = v });
        foreach (var (v, label) in DirOptions) CbCenterDir.Items.Add(new ComboBoxItem { Content = label, Tag = v });
        foreach (var a in Aspects) CbAspect.Items.Add(new ComboBoxItem { Content = a, Tag = a });
        foreach (var (v, label) in ResOptions) CbRes.Items.Add(new ComboBoxItem { Content = label, Tag = v.ToString() });
        foreach (var f in FpsOptions) CbFps.Items.Add(new ComboBoxItem { Content = f.ToString(), Tag = f.ToString() });
        foreach (var (v, label) in KeyBgOptions) CbKeyBg.Items.Add(new ComboBoxItem { Content = label, Tag = v });
    }

    // ---------------- 提交（project 字段，即时生效） ----------------

    private void Center_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        CommitCenter();
    }

    private void CenterGap_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitCenter();
            e.Handled = true;
        }
    }

    private void CenterGap_Commit(object sender, RoutedEventArgs e) => CommitCenter();

    /// <summary>saveCenter：留白宽度钳 10..70、缺省 30（对位 Vue 的 Math.min/max 链）。</summary>
    private void CommitCenter()
    {
        var p0 = S.Project;
        var free = CkCenterFree.IsChecked == true;
        var gap = double.TryParse(TbCenterGap.Text.Trim(), out var g)
            ? Math.Clamp(Math.Round(g), 10, 70)
            : Math.Clamp(p0.CenterGap ?? 30, 10, 70);
        var zone = SelectedTag(CbCenterZone) ?? "center";
        var dir = SelectedTag(CbCenterDir) ?? "tb";
        if (p0.CenterFree == free && p0.CenterGap == gap && p0.CenterZone == zone && p0.CenterDir == dir) return;
        S.Mutate(p =>
        {
            p.CenterFree = free;
            p.CenterGap = gap;
            p.CenterZone = zone;
            p.CenterDir = dir;
        }, "留出画面中央");
        if (TbCenterGap.Text != gap.ToString()) TbCenterGap.Text = gap.ToString();
    }

    private void CbAspect_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        var v = SelectedTag(CbAspect);
        if (v == null || v == S.Project.Aspect) return;
        S.Mutate(p => p.Aspect = v, "画幅：" + v); // setAspect：与导出页同源
    }

    private void CbRes_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        var v = SelectedTag(CbRes);
        if (v == null || v == S.Project.Res.ToString()) return;
        S.Mutate(p => p.Res = int.Parse(v), "分辨率", replan: false); // setRes 只 autosave
    }

    private void CbFps_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        var v = SelectedTag(CbFps);
        if (v == null || v == S.Project.Fps.ToString()) return;
        S.Mutate(p => p.Fps = int.Parse(v), "fps");
    }

    /// <summary>setKeyBg：三态清洗 + 镜像进 exportCfg.bgStyle（两处下拉同源）。</summary>
    private void CbKeyBg_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        var v = SelectedTag(CbKeyBg) ?? "off";
        if (v == S.Project.KeyBg) return;
        S.Mutate(p =>
        {
            p.KeyBg = v;
            ExportSpec.SetCfg(p, ExportSpec.CfgOf(p) with { BgStyle = v });
        }, "背景：" + (v == "green" ? "绿幕" : v == "black" ? "黑幕" : "常规"));
    }

    // ---------------- 渲染窗口 / Spout 草稿组（点重启才生效） ----------------

    private void Draft_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        _dirty = true;
        ValidateDraft();
    }

    private void BtnRestart_Click(object sender, RoutedEventArgs e)
    {
        var st = App.SettingsOf();
        var main = App.MainInstance;
        if (st == null || main == null) return;
        if (!ValidateDraft())
        {
            MessageBox.Show(Window.GetWindow(this)!, "渲染窗口分辨率需 64~4096，帧率需 1~240", "字面 Live",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ApplyDraft(st);
        st.Save();
        main.RestartRender();
        _dirty = false;
        ValidateDraft();
    }

    private void ApplyDraft(Settings st)
    {
        st.Width = int.Parse(TbWinW.Text.Trim());
        st.Height = int.Parse(TbWinH.Text.Trim());
        st.RenderFps = int.Parse(TbWinFps.Text.Trim());
        st.SpoutEnabled = CkSpoutEnabled.IsChecked == true;
        st.SpoutName = string.IsNullOrWhiteSpace(TbSpoutName.Text) ? "JIZURA-live" : TbSpoutName.Text.Trim();
        st.SpoutTransparent = CkSpoutTransparent.IsChecked == true;
        st.SpoutLyricsEnabled = CkSpoutLyrics.IsChecked == true;
        st.LyricsFps = int.Parse(TbLyricsFps.Text.Trim());
    }

    private bool ValidateDraft()
    {
        var wOk = int.TryParse(TbWinW.Text.Trim(), out var w) && w >= ResMin && w <= ResMax;
        var hOk = int.TryParse(TbWinH.Text.Trim(), out var h) && h >= ResMin && h <= ResMax;
        var fOk = int.TryParse(TbWinFps.Text.Trim(), out var f) && f >= 1 && f <= 240;
        var lOk = int.TryParse(TbLyricsFps.Text.Trim(), out var l) && l >= 1 && l <= 240;
        TbResErr.Visibility = wOk && hOk ? Visibility.Collapsed : Visibility.Visible;
        TbFpsErr.Visibility = fOk ? Visibility.Collapsed : Visibility.Visible;
        TbLyricsFpsErr.Visibility = lOk ? Visibility.Collapsed : Visibility.Visible;
        var ok = wOk && hOk && fOk && lOk;
        BtnRestart.Content = !_dirty ? "重启渲染引擎" : "保存并重启渲染引擎";
        BtnRestart.ToolTip = _dirty ? "保存并重启渲染引擎使设置生效" : "以当前设置重启渲染引擎";
        BtnRestart.IsEnabled = ok; // 对位 Vue 的 :disabled="applying || !canApply"
        // 字幕通道名与帧率行随「独立 Spout 字幕通道」勾选显隐（Vue 的 v-if）
        SyncLyricsChan();
        LyricsFpsRow.Visibility = CkSpoutLyrics.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        return ok;
    }

    /// <summary>「{SpoutName}-LYRICS」后缀随 Spout ID 输入实时刷新（Vue 模板里的 b 元素）。</summary>
    private void SyncLyricsChan()
    {
        var name = string.IsNullOrEmpty(TbSpoutName.Text) ? "JIZURA-live" : TbSpoutName.Text;
        if (TblLyricsChan.Text != name + "-LYRICS") TblLyricsChan.Text = name + "-LYRICS";
    }

    private void PullSettingsDraft()
    {
        var st = App.SettingsOf();
        if (st == null) return;
        _syncing = true;
        try
        {
            TbWinW.Text = st.Width.ToString();
            TbWinH.Text = st.Height.ToString();
            TbWinFps.Text = st.RenderFps.ToString();
            CkSpoutEnabled.IsChecked = st.SpoutEnabled;
            TbSpoutName.Text = st.SpoutName;
            CkSpoutTransparent.IsChecked = st.SpoutTransparent;
            CkSpoutLyrics.IsChecked = st.SpoutLyricsEnabled;
            TbLyricsFps.Text = st.LyricsFps.ToString();
            _dirty = false;
        }
        finally
        {
            _syncing = false;
        }
        ValidateDraft();
    }

    // ---------------- 回刷 ----------------

    private void Pull()
    {
        if (App.MainInstance == null) return;
        _syncing = true;
        try
        {
            var p = S.Project;
            SetChecked(CkCenterFree, p.CenterFree);
            CenterOpts.Visibility = p.CenterFree ? Visibility.Visible : Visibility.Collapsed;
            SelectTag(CbCenterZone, p.CenterZone is { Length: > 0 } z ? z : "center");
            TbCenterGap.Text = (p.CenterGap ?? 30).ToString();
            SelectTag(CbCenterDir, p.CenterDir == "lr" ? "lr" : "tb");
            CenterDirRow.Visibility = (p.CenterZone ?? "center") == "center" ? Visibility.Visible : Visibility.Collapsed;

            SelectTag(CbAspect, Aspects.Contains(p.Aspect) ? p.Aspect : "16:9");
            SelectTag(CbRes, ResOptions.Any(o => o.V == p.Res) ? p.Res.ToString() : "1080");
            SelectTag(CbFps, FpsOptions.Contains(p.Fps) ? p.Fps.ToString() : "24");
            SelectTag(CbKeyBg, p.KeyBg is "green" or "black" ? p.KeyBg : "off");
        }
        finally
        {
            _syncing = false;
        }
    }

    // ---------------- 小工具 ----------------

    private static string? SelectedTag(ComboBox cb)
        => cb.SelectedItem is ComboBoxItem it ? (string?)it.Tag : null;

    private static void SelectTag(ComboBox cb, string tag)
    {
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
