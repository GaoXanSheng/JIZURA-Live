using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using JizuraLive.Engine;
using JizuraLive.Logging;
using Microsoft.Win32;

namespace JizuraLive.App.Views;

// 歌词编辑面板（对位 frontend/src/components/lyrics/LyricsPanel.vue）：
// 歌词正文（1 行 = 1 短语）/ 语言选择 + 自动判定提示 / 读取 LRC / 清空歌词 / 记法说明。
// 音译・翻译注音与歌词版本切换随云端歌词库（lyricStore）在后续里程碑接入。
public partial class LyricsPanel : UserControl
{
    private bool _syncing;
    private bool _subscribed;
    private readonly DispatcherTimer _debounce; // 对位 onInput 的 replanSoon(260)
    private string? _pendingLyrics;

    public LyricsPanel()
    {
        InitializeComponent();
        CmbLang.Items.Add("自动判定");
        foreach (var kv in JLang.LangLabel)
            if (kv.Key != "auto")
                CmbLang.Items.Add($"{kv.Value}");
        CmbLang.SelectedIndex = 0;
        _debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(260) };
        _debounce.Tick += (_, _) => ApplyLyrics();
        Loaded += (_, _) =>
        {
            if (_subscribed) return;
            _subscribed = true;
            App.MainInstance!.State.StateChanged += () => Dispatcher.Invoke(Pull);
            var s = App.SettingsOf();
            _syncing = true;
            CkRoma.IsChecked = s?.WantRoma ?? true;
            CkTrans.IsChecked = s?.WantTrans ?? true;
            _syncing = false;
            Pull();
        };
    }

    /// <summary>音译/翻译：装载云端歌词时是否写入注音（对位旧 lyricStore 的 roma/trans 选项）。</summary>
    private void LyricOpt_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || !IsLoaded) return;
        var s = App.SettingsOf();
        if (s == null) return;
        s.WantRoma = CkRoma.IsChecked == true;
        s.WantTrans = CkTrans.IsChecked == true;
        s.Save();
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
        State.Mutate(p => p.Lyrics = v, "歌词", replan: true);
    }

    private void CmbLang_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || !IsLoaded) return;
        var idx = CmbLang.SelectedIndex;
        var lang = idx <= 0 ? "auto" : JLang.LangLabel.FirstOrDefault(kv => kv.Value == (string)CmbLang.Items[idx]).Key;
        if (lang == null || lang == State.Project.Lang) return;
        // 语言影响字体气质，对位 Vue onLang 的 remember/commit：走观感栈，不混进歌词编辑史
        State.Mutate(p => p.Lang = lang, "歌词语言", replan: true, look: true);
    }

    // ---------------- 动作 ----------------

    private void BtnSyntax_Click(object sender, RoutedEventArgs e) =>
        SyntaxBox.Visibility = SyntaxBox.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    // 读取 LRC（对位 look.ts loadLrc）：整份替换歌词，清掉一切绑定行号的东西，可撤销
    private void BtnLoadLrc_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "读取 LRC", Filter = "LRC 歌词 (*.lrc)|*.lrc|文本 (*.txt)|*.txt|全部|*.*" };
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
        }, "读取 LRC", replan: true);
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
        }, "清空歌词", replan: true);
        main.Preview.Seek(0);
    }
}
