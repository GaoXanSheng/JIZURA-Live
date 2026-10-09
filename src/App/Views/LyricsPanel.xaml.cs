using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using JizuraLive.App.Cloud;
using JizuraLive.App.I18n;
using JizuraLive.Engine;
using JizuraLive.Logging;
using Microsoft.Win32;

namespace JizuraLive.App.Views;

// 歌词编辑面板（对位 frontend/src/components/lyrics/LyricsPanel.vue）：
// 歌词正文（1 行 = 1 短语）/ 语言选择 + 自动判定提示 / 歌词版本切换 / 音译・翻译注音 /
// 读取 LRC / 清空歌词 / 记法说明。
// 云端歌词来源与候选版本由 EditorState.LyricSrc 持有（装载/跟随匹配时写入）。
public partial class LyricsPanel : UserControl
{
    private bool _syncing;
    private bool _subscribed;
    private readonly DispatcherTimer _debounce; // 对位 onInput 的 replanSoon(260)
    private string? _pendingLyrics;

    public LyricsPanel()
    {
        InitializeComponent();
        RebuildLangItems();
        CmbLang.SelectedIndex = 0;
        _debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(260) };
        _debounce.Tick += (_, _) => ApplyLyrics();
        Loaded += (_, _) =>
        {
            L.LanguageChanged -= ApplyTexts;
            L.LanguageChanged += ApplyTexts;
            if (_subscribed) return;
            _subscribed = true;
            App.MainInstance!.State.StateChanged += () => Dispatcher.Invoke(Pull);
            App.MainInstance!.State.LyricSourceChanged += () => Dispatcher.Invoke(RefreshVersions);
            var s = App.SettingsOf();
            _syncing = true;
            CkRoma.IsChecked = s?.WantRoma ?? true;
            CkTrans.IsChecked = s?.WantTrans ?? true;
            _syncing = false;
            Pull();
            RefreshVersions();
        };
        Unloaded += (_, _) => L.LanguageChanged -= ApplyTexts;
    }

    /// <summary>换语言：重建语言下拉项（日本語等语言名不翻译，仅「自动判定」走词库）并重读选中态。</summary>
    private void ApplyTexts()
    {
        RebuildLangItems();
        Pull();
        RefreshVersions();
    }

    /// <summary>语言选择器下拉项：首项「自动判定」走词库，其余为引擎语言名（原样）。</summary>
    private void RebuildLangItems()
    {
        _syncing = true;
        CmbLang.Items.Clear();
        CmbLang.Items.Add(L.T("Lyrics.AutoDetect"));
        foreach (var kv in JLang.LangLabel)
            if (kv.Key != "auto")
                CmbLang.Items.Add($"{kv.Value}");
        _syncing = false;
    }

    /// <summary>音译/翻译：装载云端歌词时是否写入注音（对位旧 lyricStore 的 roma/trans 选项）。
    /// 云端歌词已装载时立即重生成（对位 Vue applyLyricOptions），不必重新装载整首歌。</summary>
    private void LyricOpt_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || !IsLoaded) return;
        var s = App.SettingsOf();
        if (s == null) return;
        s.WantRoma = CkRoma.IsChecked == true;
        s.WantTrans = CkTrans.IsChecked == true;
        s.Save();
        if (State.LyricSrc != null) SongLoader.ReapplyLyricOptions(State);
    }

    // ---------------- 歌词版本（对位 Vue lyricStore 的 versions/switchVersion） ----------------

    /// <summary>候选版本 ≥ 2 才出下拉。首项是占位「选择版本」（对位 Vue 的 disabled option：
    /// 装载/换歌后不预选当前版本，用户主动选一次才切换）。</summary>
    private void RefreshVersions()
    {
        var src = State.LyricSrc;
        _syncing = true;
        try
        {
            CmbVersion.Items.Clear();
            if (src == null || src.Candidates.Count < 2)
            {
                VersionRow.Visibility = Visibility.Collapsed;
                TblVersionNote.Text = "";
                return;
            }

            CmbVersion.Items.Add(L.T("Lyrics.Version.Pick"));
            foreach (var c in src.Candidates) CmbVersion.Items.Add(LyricSource.Label(c));
            CmbVersion.SelectedIndex = 0;
            VersionRow.Visibility = Visibility.Visible;
        }
        finally
        {
            _syncing = false;
        }
    }

    private async void CmbVersion_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || !IsLoaded) return;
        var src = State.LyricSrc;
        var idx = CmbVersion.SelectedIndex - 1; // 0 = 占位项
        if (src == null || idx < 0 || idx >= src.Candidates.Count || idx == src.Selected) return;
        TblVersionNote.Text = L.T("Lyrics.Version.Switching");
        try
        {
            TblVersionNote.Text = await SongLoader.SwitchVersionAsync(State, idx);
        }
        catch (Exception ex)
        {
            Log.Warn("[LyricsPanel] 切换歌词版本失败：" + ex.Message);
            TblVersionNote.Text = L.F("Lyrics.Version.Fail", ex.Message);
            RefreshVersions(); // 失败回退到占位项
        }
    }

    private EditorState State => App.MainInstance!.State;

    // ---------------- 状态 → UI ----------------

    private void Pull()
    {
        var p = State.Project;
        _syncing = true;
        // 输入中不打断（对位 Vue sync 的 document.activeElement !== ta 判断）
        if (!TbLyrics.IsKeyboardFocused && _pendingLyrics == null)
            TbLyrics.Text = p.Lyrics ?? "";
        var lang = p.Lang ?? "auto";
        CmbLang.SelectedIndex = lang == "auto" ? 0 : Math.Max(0, CmbLang.Items.IndexOf(FindLangLabel(lang)));
        // auto 时显示判定结果（对位 noteLang）
        TblLangNote.Text = (lang == "auto")
            ? "→ " + JLang.LangLabel.GetValueOrDefault(JLang.ResolveLang(p), "?")
            : "";
        _syncing = false;
        _pendingLyrics = null;
    }

    private string? FindLangLabel(string key) =>
        JLang.LangLabel.TryGetValue(key, out var label) ? label : null;

    // ---------------- UI → 状态 ----------------

    private void TbLyrics_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        _pendingLyrics = TbLyrics.Text;
        _debounce.Stop();
        _debounce.Start();
    }

    private void ApplyLyrics()
    {
        _debounce.Stop();
        var v = _pendingLyrics;
        _pendingLyrics = null;
        if (v == null || v == State.Project.Lyrics) return;
        // 歌词是内容修改：edit 历史 + 重规划（replan:true）
        State.Mutate(p => p.Lyrics = v, L.T("Lyrics.Mutate.Lyrics"), replan: true);
    }

    private void CmbLang_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || !IsLoaded) return;
        var idx = CmbLang.SelectedIndex;
        var lang = idx <= 0 ? "auto" : JLang.LangLabel.FirstOrDefault(kv => kv.Value == (string)CmbLang.Items[idx]).Key;
        if (lang == null || lang == State.Project.Lang) return;
        // 语言影响字体气质，对位 Vue onLang 的 remember/commit：走观感栈，不混进歌词编辑史
        State.Mutate(p => p.Lang = lang, L.T("Lyrics.Lang"), replan: true, look: true);
    }

    // ---------------- 动作 ----------------

    private void BtnSyntax_Click(object sender, RoutedEventArgs e)
    {
        if (SyntaxBox.Visibility == Visibility.Visible)
        {
            SyntaxBox.Visibility = Visibility.Collapsed; // 收起即时，不拖动画
            return;
        }
        SyntaxBox.Visibility = Visibility.Visible;
        JzAnim.FadeSlideIn(SyntaxBox, 4);
    }

    // 读取 LRC（对位 look.ts loadLrc）：整份替换歌词，清掉一切绑定行号的东西，可撤销
    private void BtnLoadLrc_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = L.T("Lyrics.LoadLrc"), Filter = L.T("Lyrics.LrcDlg.Filter") };
        if (dlg.ShowDialog(Window.GetWindow(this)!) != true) return;
        string text;
        try
        {
            text = File.ReadAllText(dlg.FileName);
        }
        catch (Exception ex)
        {
            Log.Warn("[LyricsPanel] LRC 读取失败：" + ex.Message);
            return;
        }

        LoadLrc(text);
    }

    /// <summary>loadLrc 移植：去 BOM、统一换行、清行时间/覆盖，回到开头（可撤销）。</summary>
    public void LoadLrc(string text)
    {
        var main = App.MainInstance!;
        main.Preview.Pause();
        var normalized = (text ?? "").Replace("\uFEFF", "").Replace("\r\n", "\n").Replace("\r", "\n").Trim();
        State.Mutate(p =>
        {
            p.Lyrics = normalized;
            p.Timing.LineTimes.Clear();
            p.Overrides.Clear();
        }, L.T("Lyrics.LoadLrc"), replan: true);
        main.Preview.Seek(0);
    }

    // 清空歌词（对位 look.ts clearLyrics）：歌词 + 一切绑定行号的东西，可撤销
    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        var p = State.Project;
        if (string.IsNullOrWhiteSpace(p.Lyrics) && p.Timing.LineTimes.Count == 0)
        {
            TbLyrics.Focus(); // 本来就空：只把焦点交回输入框（对位 Vue focus-lyrics）
            return;
        }

        var main = App.MainInstance!;
        main.Preview.Pause();
        State.Mutate(x =>
        {
            x.Lyrics = "";
            x.Timing.LineTimes.Clear();
            x.Overrides.Clear();
        }, L.T("Lyrics.Clear"), replan: true);
        main.Preview.Seek(0);
    }
}
