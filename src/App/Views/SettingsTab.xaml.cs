using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using JizuraLive.App.I18n;
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
    // 带文案的下拉：数组存资源键，构建时经 L.T 解析（换语言时 RebuildLocalizedItems 重建）
    private static readonly (string V, string Key)[] KeyBgOptions =
        { ("off", "Settings.KeyBg.Off"), ("green", "Settings.KeyBg.Green"), ("black", "Settings.KeyBg.Black") };
    private static readonly (string V, string Key)[] ZoneOptions =
    {
        ("center", "Settings.Zone.Center"), ("left", "Settings.Zone.Left"),
        ("right", "Settings.Zone.Right"), ("top", "Settings.Zone.Top"), ("bottom", "Settings.Zone.Bottom"),
    };
    private static readonly (string V, string Key)[] DirOptions =
        { ("tb", "Settings.Dir.Tb"), ("lr", "Settings.Dir.Lr") };

    // UI 语言清单：词库目录（zh-Hans）→ 界面自称。新增语言时补一项即可
    private static readonly (string Code, string Name)[] UiLangs = { ("zh-Hans", "简体中文") };

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
            BuildUiLangCombo();
            CbCenterZone.SelectionChanged += (_, _) => CenterDirRow.Visibility =
                SelectedTag(CbCenterZone) == "center" ? Visibility.Visible : Visibility.Collapsed;
        }
        S.StateChanged += OnState;
        L.LanguageChanged += OnLanguageChanged;
        Pull();
        PullSettingsDraft();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        S.StateChanged -= OnState;
        L.LanguageChanged -= OnLanguageChanged;
    }

    private void OnState() => Dispatcher.Invoke(Pull);

    /// <summary>语言切换：重建带文案的下拉（留白位置/竖屏分列/背景）＋ 刷按钮双态文案与错误占位。</summary>
    private void OnLanguageChanged()
    {
        RebuildLocalizedItems();
        ValidateDraft();
        if (JFonts.FontDirs().FirstOrDefault(Directory.Exists) == null)
            TblFontDir.Text = L.T("Settings.Fonts.NoDir");
    }

    private void BuildComboItems()
    {
        if (CbAspect.Items.Count > 0) return;
        foreach (var a in Aspects) CbAspect.Items.Add(new ComboBoxItem { Content = a, Tag = a });
        foreach (var (v, label) in ResOptions) CbRes.Items.Add(new ComboBoxItem { Content = label, Tag = v.ToString() });
        foreach (var f in FpsOptions) CbFps.Items.Add(new ComboBoxItem { Content = f.ToString(), Tag = f.ToString() });
        RebuildLocalizedItems();
    }

    /// <summary>清空并按当前语言重建 Zone/Dir/KeyBg 三组下拉，保留选中值（_syncing 防误触发提交）。</summary>
    private void RebuildLocalizedItems()
    {
        _syncing = true;
        try
        {
            var zone = SelectedTag(CbCenterZone);
            var dir = SelectedTag(CbCenterDir);
            var keyBg = SelectedTag(CbKeyBg);
            CbCenterZone.Items.Clear();
            CbCenterDir.Items.Clear();
            CbKeyBg.Items.Clear();
            foreach (var (v, key) in ZoneOptions) CbCenterZone.Items.Add(new ComboBoxItem { Content = L.T(key), Tag = v });
            foreach (var (v, key) in DirOptions) CbCenterDir.Items.Add(new ComboBoxItem { Content = L.T(key), Tag = v });
            foreach (var (v, key) in KeyBgOptions) CbKeyBg.Items.Add(new ComboBoxItem { Content = L.T(key), Tag = v });
            if (zone != null) SelectTag(CbCenterZone, zone);
            if (dir != null) SelectTag(CbCenterDir, dir);
            if (keyBg != null) SelectTag(CbKeyBg, keyBg);
        }
        finally
        {
            _syncing = false;
        }
    }

    private void BuildUiLangCombo()
    {
        if (CbUiLang.Items.Count > 0) return;
        foreach (var (code, name) in UiLangs) CbUiLang.Items.Add(new ComboBoxItem { Content = name, Tag = code });
        SelectTag(CbUiLang, App.SettingsOf()?.UiLang ?? L.DefaultLang);
    }

    /// <summary>UI 语言：即时保存 + 热切换（L.SetLanguage 灌资源字典并广播 LanguageChanged，
    /// 各面板自刷动态文本；XAML 静态文本由 DynamicResource 自动刷新）。</summary>
    private void UiLang_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        var code = SelectedTag(CbUiLang);
        var st = App.SettingsOf();
        if (code == null || st == null || code == st.UiLang) return;
        st.UiLang = code;
        st.Save();
        L.SetLanguage(code);
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
        }, L.T("Settings.Mutate.Center"));
        if (TbCenterGap.Text != gap.ToString()) TbCenterGap.Text = gap.ToString();
    }

    private void CbAspect_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        var v = SelectedTag(CbAspect);
        if (v == null || v == S.Project.Aspect) return;
        S.Mutate(p => p.Aspect = v, L.F("Settings.Mutate.AspectFmt", v)); // setAspect：与导出页同源
    }

    private void CbRes_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        var v = SelectedTag(CbRes);
        if (v == null || v == S.Project.Res.ToString()) return;
        S.Mutate(p => p.Res = int.Parse(v), L.T("Settings.Res"), replan: false); // setRes 只 autosave
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
        }, L.F("Settings.Mutate.KeyBgFmt",
            v == "green" ? L.T("Settings.KeyBgShort.Green")
            : v == "black" ? L.T("Settings.KeyBgShort.Black")
            : L.T("Settings.KeyBgShort.Off")));
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
            MessageBox.Show(Window.GetWindow(this)!, L.T("Settings.Msg.WinRange"), L.T("Main.Title"),
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
        BtnRestart.Content = !_dirty ? L.T("Settings.Btn.Restart") : L.T("Settings.Btn.RestartSave");
        BtnRestart.ToolTip = _dirty ? L.T("Settings.Btn.RestartSave.Tip") : L.T("Settings.Btn.Restart.Tip");
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
            CkFetchFonts.IsChecked = st.FetchFonts;
            TblFontDir.Text = JFonts.FontDirs().FirstOrDefault(Directory.Exists) ?? L.T("Settings.Fonts.NoDir");
            _dirty = false;
        }
        finally
        {
            _syncing = false;
        }
        ValidateDraft();
    }

    /// <summary>缺字体自动联网补：即时生效（不需要重启渲染引擎）。开的时候顺带把当前 plan 用到的
    /// 字体请求一遍，拉到后 FontFetch 回调重规划换脸。</summary>
    private void FetchFonts_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var st = App.SettingsOf();
        if (st == null) return;
        st.FetchFonts = CkFetchFonts.IsChecked == true;
        FontFetch.Enabled = st.FetchFonts;
        st.Save();
        if (FontFetch.Enabled)
            _ = JFonts.EnsureFonts(S.Project.Lyrics, JFonts.FontsOfPlan(App.MainInstance?.Preview.Plan));
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
