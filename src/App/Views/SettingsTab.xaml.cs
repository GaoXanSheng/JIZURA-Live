using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using JizuraLive.App.I18n;
using JizuraLive.Engine;
using JizuraLive.Logging;

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

    // 字幕面板下拉（对位 SubtitlePanel.vue 的 option 顺序与文本；空串 Tag = 字体「跟随画面」）
    private static readonly (string V, string Key)[] SubContentOptions =
    {
        ("off", "Settings.Subtitle.Content.Off"), ("trans", "Settings.Subtitle.Content.Trans"),
        ("roma", "Settings.Subtitle.Content.Roma"), ("both", "Settings.Subtitle.Content.Both"),
    };
    private static readonly (string V, string Key)[] SubPosOptions =
        { ("bottom", "Settings.Subtitle.Pos.Bottom"), ("top", "Settings.Subtitle.Pos.Top") };
    private static readonly (string V, string Key)[] SubFontOptions =
    {
        ("", "Settings.Subtitle.Font.Follow"), ("sans", "Settings.Subtitle.Font.Sans"),
        ("serif", "Settings.Subtitle.Font.Serif"),
    };

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

    /// <summary>语言切换：重建带文案的下拉（留白位置/竖屏分列/背景/字幕）＋ 刷按钮双态文案与错误占位。</summary>
    private void OnLanguageChanged()
    {
        RebuildLocalizedItems();
        PullSubtitle();
        ValidateDraft();
        RefreshStorageInfo();
        TblFontDir.Text = AppDirs.FontsDir;
    }

    private void BuildComboItems()
    {
        if (CbAspect.Items.Count > 0) return;
        foreach (var a in Aspects) CbAspect.Items.Add(new ComboBoxItem { Content = a, Tag = a });
        foreach (var (v, label) in ResOptions) CbRes.Items.Add(new ComboBoxItem { Content = label, Tag = v.ToString() });
        foreach (var f in FpsOptions) CbFps.Items.Add(new ComboBoxItem { Content = f.ToString(), Tag = f.ToString() });
        RebuildLocalizedItems();
    }

    /// <summary>清空并按当前语言重建 Zone/Dir/KeyBg/字幕三组下拉，保留选中值（_syncing 防误触发提交）。</summary>
    private void RebuildLocalizedItems()
    {
        _syncing = true;
        try
        {
            var zone = SelectedTag(CbCenterZone);
            var dir = SelectedTag(CbCenterDir);
            var keyBg = SelectedTag(CbKeyBg);
            var subContent = SelectedTag(CbSubContent);
            var subPos = SelectedTag(CbSubPos);
            var subFont = SelectedTag(CbSubFont);
            FillCombo(CbCenterZone, ZoneOptions);
            FillCombo(CbCenterDir, DirOptions);
            FillCombo(CbKeyBg, KeyBgOptions);
            FillCombo(CbSubContent, SubContentOptions);
            FillCombo(CbSubPos, SubPosOptions);
            FillCombo(CbSubFont, SubFontOptions);
            if (zone != null) SelectTag(CbCenterZone, zone);
            if (dir != null) SelectTag(CbCenterDir, dir);
            if (keyBg != null) SelectTag(CbKeyBg, keyBg);
            if (subContent != null) SelectTag(CbSubContent, subContent);
            if (subPos != null) SelectTag(CbSubPos, subPos);
            if (subFont != null) SelectTag(CbSubFont, subFont);
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>清空并按资源键重建一组带文案下拉（语言热切换共用）。</summary>
    private static void FillCombo(ComboBox cb, (string V, string Key)[] options)
    {
        cb.Items.Clear();
        foreach (var (v, key) in options) cb.Items.Add(new ComboBoxItem { Content = L.T(key), Tag = v });
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
        S.Mutate(p => p.Fps = int.Parse(v), L.T("Settings.Fps"));
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

    // ---------------- 字幕面板（App Settings，即时生效，不需要重启渲染引擎） ----------------
    // 对位 SubtitlePanel.vue：改即写共享的 SubtitleCfg + 存 settings.json + 通知引擎版本号
    // （预览泵与 Spout 字幕通道据此在暂停态也重绘一帧）；不进撤销历史（Vue 同款）。

    private void Sub_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var cfg = S.SubtitleCfg;
        var content = SelectedTag(CbSubContent) switch
        {
            "off" => SubContent.Off,
            "trans" => SubContent.Trans,
            "roma" => SubContent.Roma,
            _ => SubContent.Both
        };
        var pos = SelectedTag(CbSubPos) == "top" ? SubPos.Top : SubPos.Bottom;
        var font = SelectedTag(CbSubFont) ?? "";
        var bar = CkSubBar.IsChecked == true;
        var karaoke = CkSubKaraoke.IsChecked == true;
        var hold = CkSubHold.IsChecked == true;
        if (cfg.Content == content && cfg.Pos == pos && cfg.Font == font && cfg.Bar == bar
            && cfg.Karaoke == karaoke && cfg.HoldLastLine == hold) return;
        cfg.Content = content;
        cfg.Pos = pos;
        cfg.Font = font;
        cfg.Bar = bar;
        cfg.Karaoke = karaoke;
        cfg.HoldLastLine = hold;
        SaveSubCfg();
    }

    private void SubNum_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        SubNum_Commit(sender, e);
        e.Handled = true;
    }

    /// <summary>数字框提交（失焦/回车）：字号 2~15、描边 0~12、偏移 ±50，越界钳位、非法回显旧值。</summary>
    private void SubNum_Commit(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var tb = (TextBox)sender;
        var cfg = S.SubtitleCfg;
        if (ReferenceEquals(tb, TbSubSize)) CommitNum(tb, cfg.Size, 2, 15, v => cfg.Size = v);
        else if (ReferenceEquals(tb, TbSubStroke)) CommitNum(tb, cfg.Stroke, 0, 12, v => cfg.Stroke = v);
        else if (ReferenceEquals(tb, TbSubOffX)) CommitNum(tb, cfg.OffX, -50, 50, v => cfg.OffX = v);
        else if (ReferenceEquals(tb, TbSubOffY)) CommitNum(tb, cfg.OffY, -50, 50, v => cfg.OffY = v);
    }

    private void CommitNum(TextBox tb, double cur, double min, double max, Action<double> set)
    {
        var v = double.TryParse(tb.Text.Trim(), out var parsed) ? Math.Clamp(parsed, min, max) : cur;
        if (Math.Abs(v - cur) > 1e-9)
        {
            set(v);
            SaveSubCfg();
        }
        var text = v.ToString("0.####");
        if (tb.Text != text) tb.Text = text;
    }

    private void SubColor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        SubColor_Commit(sender, e);
        e.Handled = true;
    }

    /// <summary>色块提交：合法 hex 生效，非法回显旧值（同 StyleTab 的 CommitHex）。</summary>
    private void SubColor_Commit(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var tb = (TextBox)sender;
        var cfg = S.SubtitleCfg;
        var cur = ReferenceEquals(tb, TbSubColor) ? cfg.Color
            : ReferenceEquals(tb, TbSubStrokeColor) ? cfg.StrokeColor
            : cfg.UnsungColor;
        if (!TryNormHex(tb.Text, out var hex))
        {
            FillSubColorBox(tb, cur); // 非法输入回显旧值
            return;
        }
        if (!string.Equals(cur, hex, StringComparison.OrdinalIgnoreCase))
        {
            if (ReferenceEquals(tb, TbSubColor)) cfg.Color = hex;
            else if (ReferenceEquals(tb, TbSubStrokeColor)) cfg.StrokeColor = hex;
            else cfg.UnsungColor = hex;
            SaveSubCfg();
        }
        FillSubColorBox(tb, hex);
    }

    /// <summary>色块 = 彩色底 + 十六进制字（亮底换深字保证可读），与 StyleTab 的配色编辑同一套路。</summary>
    private static void FillSubColorBox(TextBox tb, string hex)
    {
        if (tb.Text != hex) tb.Text = hex;
        tb.Background = Brush(hex, "#808080");
        tb.Foreground = ReadableOn(hex);
        tb.ToolTip = hex;
    }

    /// <summary>字幕样式改动落盘：快照进 settings.json（应用级配置）+ 通知引擎版本号（预览泵/Spout 字幕通道重绘）。</summary>
    private void SaveSubCfg()
    {
        var st = App.SettingsOf();
        if (st != null)
        {
            var snap = new SubtitleConfig();
            snap.CopyFrom(S.SubtitleCfg);
            st.Subtitle = snap;
            st.Save();
        }
        S.Engine.NotifySubtitleCfgChanged();
    }

    /// <summary>字幕控件回显（Pull / 语言切换调用；只在 _syncing 或值无变化时执行，不会触发提交）。</summary>
    private void PullSubtitle()
    {
        var cfg = S.SubtitleCfg;
        SelectTag(CbSubContent, cfg.Content switch
        {
            SubContent.Off => "off",
            SubContent.Trans => "trans",
            SubContent.Roma => "roma",
            _ => "both"
        });
        SelectTag(CbSubPos, cfg.Pos == SubPos.Top ? "top" : "bottom");
        SelectTag(CbSubFont, cfg.Font is "sans" or "serif" ? cfg.Font : "");
        SetChecked(CkSubBar, cfg.Bar);
        SetChecked(CkSubKaraoke, cfg.Karaoke);
        SetChecked(CkSubHold, cfg.HoldLastLine);
        TbSubSize.Text = cfg.Size.ToString("0.####");
        TbSubStroke.Text = cfg.Stroke.ToString("0.####");
        TbSubOffX.Text = cfg.OffX.ToString("0.####");
        TbSubOffY.Text = cfg.OffY.ToString("0.####");
        FillSubColorBox(TbSubColor, cfg.Color);
        FillSubColorBox(TbSubStrokeColor, cfg.StrokeColor);
        FillSubColorBox(TbSubUnsung, cfg.UnsungColor);
    }

    /// <summary>#abc / #aabbcc → #aabbcc（小写）；其余 false（与 StyleTab.TryNormHex 同款）。</summary>
    private static bool TryNormHex(string? s, out string hex)
    {
        hex = "";
        var t = (s ?? "").Trim();
        if (!t.StartsWith('#')) return false;
        var body = t[1..];
        if (body.Length == 3 && body.All(Uri.IsHexDigit))
            body = string.Concat(body.Select(ch => new string(ch, 2)));
        if (body.Length != 6 || !body.All(Uri.IsHexDigit)) return false;
        hex = "#" + body.ToLowerInvariant();
        return true;
    }

    private static SolidColorBrush Brush(string? hex, string fallback)
    {
        try
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex ?? fallback));
            b.Freeze();
            return b;
        }
        catch
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));
            b.Freeze();
            return b;
        }
    }

    /// <summary>色块上的十六进制字：按亮度选前景（亮底深字 / 暗底亮字）。</summary>
    private static SolidColorBrush ReadableOn(string hex)
    {
        var c = Brush(hex, "#808080").Color;
        var lum = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
        var b = new SolidColorBrush(lum > 0.55 ? Color.FromRgb(0x12, 0x0C, 0x00) : Color.FromRgb(0xEC, 0xE7, 0xE1));
        b.Freeze();
        return b;
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
	            Directory.CreateDirectory(AppDirs.FontsDir);
	            TblFontDir.Text = AppDirs.FontsDir;
	            RefreshStorageInfo();
	            _dirty = false;
	        }
	        finally
	        {
	            _syncing = false;
	        }
	        ValidateDraft();
	    }

	    private void BtnOpenFontDir_Click(object sender, RoutedEventArgs e)
	    {
	        try
	        {
	            Directory.CreateDirectory(AppDirs.FontsDir);
	            Process.Start(new ProcessStartInfo("explorer.exe", AppDirs.FontsDir) { UseShellExecute = true });
	        }
	        catch (Exception ex)
	        {
	            Log.Warn("打开用户字体目录失败：" + ex.Message);
	        }
	    }

	    private void BtnOpenDataDir_Click(object sender, RoutedEventArgs e)
	    {
	        try
	        {
	            Directory.CreateDirectory(AppDirs.Root);
	            Process.Start(new ProcessStartInfo("explorer.exe", AppDirs.Root) { UseShellExecute = true });
	        }
	        catch (Exception ex)
	        {
	            Log.Warn("打开数据根目录失败：" + ex.Message);
	        }
	    }

	    private void BtnCleanCache_Click(object sender, RoutedEventArgs e)
	    {
	        StorageCleaner.ClearCache(S.AudioPath);
	        RefreshStorageInfo();
	    }

	    private void BtnCleanCloud_Click(object sender, RoutedEventArgs e)
	    {
	        StorageCleaner.ClearCloud(S.AudioPath);
	        RefreshStorageInfo();
	    }

	    private void BtnCleanLogs_Click(object sender, RoutedEventArgs e)
	    {
	        StorageCleaner.ClearLogs();
	        RefreshStorageInfo();
	    }

	    private void RefreshStorageInfo()
	    {
	        try
	        {
	            var summary = StorageCleaner.GetSummary();
	            TblStorageInfo.Text = L.F("Settings.Storage.SummaryFmt", summary.CacheMb, summary.CloudMb, summary.LogsMb);
	        }
	        catch
	        {
	            TblStorageInfo.Text = "";
	        }
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
            PullSubtitle();
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
