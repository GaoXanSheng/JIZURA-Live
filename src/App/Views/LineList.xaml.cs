using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using JizuraLive.Engine;
using JizuraLive.Logging;
using Newtonsoft.Json.Linq;

namespace JizuraLive.App.Views;

// 行与切分列表（对位 frontend/src/components/lyrics/LineList.vue + LineRow.vue）：
// 逐行显示行号 / 开始时刻（手动青框）/ 行文本（点击定位）/ 每段切分边界与布局 / 动作行。
// 窗口化渲染改由 ListBox 虚拟化承担；播放行跟随用 150ms 轮询当前行（对位 scrolltoline）。
// 视觉树（行内三列 + 切分格 + 动作行图标）在 LineList.xaml 的 RowTemplate 里；
// 注：导出范围按钮只有视觉（RangeBtn）—— exportRange 字段尚未进入 C# Project 模型（Vue 专属），
// 因此点击无行为、行也没有 inRange 的琥珀描边，接入 Project/导出层后补。
public partial class LineList : UserControl
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>手法下拉选项（自动 + 注册顺序全部版式；对位 techPoolOpts）。</summary>
    public static readonly List<string> LayoutNames = BuildLayoutNames();

    /// <summary>手法下拉选项对应的 layout key（LayoutNames[i] ↔ LayoutKeys[i]）。</summary>
    public static readonly List<string> LayoutKeys = BuildLayoutKeys();

    /// <summary>切分数下拉选项（自动 + 1..6）。</summary>
    public static readonly List<string> NcutNames =
        new() { "切分 自动", "切分 1", "切分 2", "切分 3", "切分 4", "切分 5", "切分 6" };

    private static List<string> BuildLayoutNames()
    {
        var list = new List<string> { "自动" };
        foreach (var k in Reg.LayoutOrder)
            list.Add(Reg.Layouts.TryGetValue(k, out var d) && d != null ? d.Name : k);
        return list;
    }

    private static List<string> BuildLayoutKeys()
    {
        var list = new List<string> { "" };
        list.AddRange(Reg.LayoutOrder);
        return list;
    }

    private bool _syncing;
    private bool _subscribed;
    private int _activeLine = -1;
    private readonly Dictionary<int, LineRowVm> _rowMap = new();

    // 行内编辑态（对位 LineRow.vue 的 editing；歌词重规划换行数据时草稿随 _editDraft 保留）
    private int _editingI = -1;
    private string _editingPre = "";
    private string _editingBody = "";
    private int _editingSrc = -1;

    private readonly DispatcherTimer _followTimer;
    // 状态变化合帧：一次重规划/打点会连发多次 StateChanged，逐次同步 Rebuild 会把 UI 线程压在
    // O(行×切分) 重建 + WPF 文本测量上（实测布局风暴里 Rebuild 累计 1.8s、字体流被反复装载）。
    private readonly DispatcherTimer _rebuildTimer;

    public LineList()
    {
        InitializeComponent();
        _followTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        _followTimer.Tick += (_, _) => FollowCurrentLine(scroll: true);
        _rebuildTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(60) };
        _rebuildTimer.Tick += (_, _) =>
        {
            _rebuildTimer.Stop();
            Rebuild();
        };
        Loaded += (_, _) =>
        {
            if (_subscribed) return;
            _subscribed = true;
            App.MainInstance!.State.StateChanged += () => Dispatcher.BeginInvoke(() =>
            {
                _rebuildTimer.Stop();
                _rebuildTimer.Start();
            });
            Rebuild();
            _followTimer.Start();
        };
    }

    private EditorState State => App.MainInstance!.State;

    // ---------------- 构建（对位 LineList.vue rebuild） ----------------

    private void Rebuild()
    {
        // 慢动作留痕（性能专项）：ItemsSource 换新后的 WPF 测量/字体装载不在此计时内（那部分
        // 要靠 trace 归因），这里只看托管重建本身是否超预算。
        var t0 = Stopwatch.GetTimestamp();
        RebuildCore();
        var ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        if (ms > 80)
            Log.Info($"[perf] LineList.Rebuild {ms:F0}ms（{State.Plan?.Lines.Count ?? 0} 行）");
    }

    private void RebuildCore()
    {
        var plan = State.Plan;
        var old = Lst.ItemsSource as List<LineRowVm>;
        var draft = _editingI >= 0 ? old?.FirstOrDefault(r => r.I == _editingI)?.EditValue : null;
        _rowMap.Clear();
        if (plan == null || plan.Lines.Count == 0)
        {
            _syncing = true;
            Lst.ItemsSource = null;
            _syncing = false;
            TblInfo.Text = "0 行 / 0 切分";
            _activeLine = -1;
            return;
        }

        var p = State.Project;
        var rows = new List<LineRowVm>(plan.Lines.Count);
        for (var i = 0; i < plan.Lines.Count; i++)
        {
            var ln = plan.Lines[i];
            var ov = p.Overrides.TryGetValue(i.ToString(), out var o) && o is JObject oo ? oo : null;
            var manual = p.Timing.LineTimes.ContainsKey(i.ToString());
            var label = ln.Interlude
                ? (ln.Secs is > 0 ? $"〔间奏 {TrimNum(ln.Secs.Value)}秒〕" : "〔间奏〕")
                : ln.Text;
            var row = new LineRowVm
            {
                I = i,
                No = (i + 1).ToString("00", Inv),
                StartText = ln.Start.ToString("0.00", Inv),
                Manual = manual,
                StartTip = $"行开始（秒）・{(manual ? "手动" : "自动")}",
                LineStart = ln.Start,
                Label = label,
                Full = label,
                Interlude = ln.Interlude,
                LayoutKey = ov?.Value<string>("layout") ?? "",
                NcutIndex = ov?["cuts"] is { Type: JTokenType.Integer } ? (int)ov["cuts"] : 0,
                Locked = ov?.Value<bool?>("lock") == true,
                HasCutsControls = !ln.Interlude,
                Cuts = new List<CutVm>(),
            };
            row.LayoutIndex = Math.Max(0, LayoutKeys.IndexOf(row.LayoutKey));

            // 每行的非特殊切分，序号即行内第 k 段（对位 rebuild 的 span/cuts 过滤）
            var k = 0;
            var cutLayouts = ov?["cutLayouts"] as JObject;
            var cutTime = ov?["cutTime"] as JObject;
            foreach (var c in plan.Cuts)
            {
                if (c.Line != i) continue;
                if (!Reg.Layouts.TryGetValue(c.Layout, out var ld) || ld == null || ld.Special) continue;
                var forced = cutLayouts?[k.ToString()]?.Value<string>();
                var ctTok = cutTime?[k.ToString()];
                var ct = ctTok != null && ctTok.Type is JTokenType.Float or JTokenType.Integer ? (double?)ctTok : null;
                var enterName = Reg.Enter.TryGetValue(c.Enter, out var en) && en != null ? en.Name : c.Enter;
                var exitName = Reg.Exit.TryGetValue(c.Exit, out var ex) && ex != null ? ex.Name : c.Exit;
                row.Cuts.Add(new CutVm
                {
                    Row = row,
                    K = k,
                    StartText = c.Start.ToString("0.00", Inv),
                    HasTime = k > 0,
                    CtManual = ct != null,
                    Start = c.Start,
                    Half = Math.Min(c.Dur * 0.5, c.InDur + 0.05),
                    Title = $"{c.Text}｜{enterName} → {exitName}",
                    BoundaryTip = $"切分 {k} 的结束 ＝ 切分 {k + 1} 的开始（秒・{(ct != null ? "手动" : "自动")}）。" +
                                  $"切分首尾相接，两段共用这条边界。{(ct != null ? "清空则恢复自动" : "输入数值则固定")}",
                    LayoutIndex = Math.Max(0, LayoutKeys.IndexOf(forced ?? c.Layout)),
                    Forced = forced != null,
                    Border = HueBorder(Reg.LayoutOrder.IndexOf(c.Layout)),
                });
                k++;
            }

            rows.Add(row);
            _rowMap[i] = row;
        }

        // 行内编辑中的行：新数据里恢复编辑态与草稿（对位「数据都在 rows 里，无损失」）
        if (_editingI >= 0)
        {
            var er = rows.FirstOrDefault(r => r.I == _editingI);
            if (er != null)
            {
                er.Editing = true;
                er.EditValue = draft ?? _editingBody;
            }
            else
            {
                _editingI = -1; // 行没了（歌词重排），编辑态自然失效
            }
        }

        _syncing = true;
        Lst.ItemsSource = rows;
        _syncing = false;
        TblInfo.Text = $"{plan.Lines.Count} 行 / {plan.Cuts.Count} 切分";
        FollowCurrentLine(scroll: false);
    }

    private static string TrimNum(double v) => v.ToString("0.#", Inv);

    // 对位 layoutHue：hsla((LAYOUT_ORDER.indexOf(k)*37+30)%360, 70%, 58%, 0.7)
    private static Brush HueBorder(int idx)
    {
        var h = (((idx * 37 + 30) % 360) + 360) % 360;
        var (r, g, b) = HslToRgb(h / 360.0, 0.70, 0.58);
        var brush = new SolidColorBrush(Color.FromArgb(179, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static (byte, byte, byte) HslToRgb(double h, double s, double l)
    {
        if (s == 0) { var v = (byte)Math.Round(l * 255); return (v, v, v); }
        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var pp = 2 * l - q;
        double F(double t)
        {
            t = (t % 1 + 1) % 1;
            if (t < 1.0 / 6) return pp + (q - pp) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return pp + (q - pp) * (2.0 / 3 - t) * 6;
            return pp;
        }
        return ((byte)Math.Round(F(h + 1.0 / 3) * 255), (byte)Math.Round(F(h) * 255), (byte)Math.Round(F(h - 1.0 / 3) * 255));
    }

    // ---------------- 播放行跟随（对位 scrolltoline + active） ----------------

    // 点击行文本：定位到该行开头一点（对位 seekLine）
    private void Label_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LineRowVm row) return;
        App.MainInstance!.Preview.Seek(row.LineStart + 0.001);
    }

    private void FollowCurrentLine(bool scroll)
    {
        var plan = App.MainInstance!.Preview.Plan;
        if (plan == null || plan.Lines.Count == 0) return;
        var t = App.MainInstance!.Preview.CurrentTime;
        var li = -1;
        for (var i = 0; i < plan.Lines.Count; i++)
        {
            if (plan.Lines[i].Start <= t + 1e-3) li = i;
            else break;
        }

        if (li == _activeLine) return;
        var old = _activeLine;
        _activeLine = li;
        if (old >= 0 && _rowMap.TryGetValue(old, out var o1)) o1.Active = false;
        if (li >= 0 && _rowMap.TryGetValue(li, out var o2))
        {
            o2.Active = true;
            if (scroll) Lst.ScrollIntoView(o2); // 行列表随播放滚动（对位 scrollToLine）
        }
    }

    // ---------------- 行开始时刻 ----------------

    private void TimeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var tb = (TextBox)sender;
        var hadFocus = tb.IsKeyboardFocused;
        tb.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        if (hadFocus) tb.Focus();
        e.Handled = true;
    }

    // 行开始（秒）：手动 > LRC > 估算；清空恢复自动（对位 LineRow.vue onTime，pushEdit → edit 栈）
    private void StartBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var tb = (TextBox)sender;
        if (tb.DataContext is not LineRowVm row) return;
        var t = State.Project.Timing;
        var has = t.LineTimes.TryGetValue(row.I.ToString(), out var cur);
        if (!double.TryParse(tb.Text, NumberStyles.Float, Inv, out var v) || v < 0 || !double.IsFinite(v))
        {
            if (has)
                State.Mutate(p => p.Timing.LineTimes.Remove(row.I.ToString()), "行时刻", replan: true);
            return;
        }

        v = Math.Max(0, v);
        if (has && Math.Abs(cur - v) < 1e-6) return;
        State.Mutate(p => p.Timing.LineTimes[row.I.ToString()] = v, "行时刻", replan: true);
    }

    // ---------------- 行内改歌词（对位 LineRow.vue startEdit/finishEdit） ----------------

    private void EditBtn_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LineRowVm row) return;
        var plan = State.Plan;
        if (plan == null || row.I >= plan.Lines.Count) return;
        var ln = plan.Lines[row.I];
        var lines = (State.Project.Lyrics ?? "").Replace("\r", "").Split('\n');
        if (ln.Src < 0 || ln.Src >= lines.Length) return;
        // 保留行首的 LRC 时间戳前缀，只改正文
        var pre = Regex.Match(lines[ln.Src], @"^\s*(?:\[\d+:\d+(?:\.\d+)?\])*").Value;
        var body = lines[ln.Src][pre.Length..].Trim();
        _editingI = row.I;
        _editingPre = pre;
        _editingBody = body;
        _editingSrc = ln.Src;
        row.Editing = true;
        row.EditValue = body;
    }

    private void EditBox_Loaded(object sender, RoutedEventArgs e)
    {
        var tb = (TextBox)sender;
        tb.Focus();
        tb.Select(tb.Text.Length, 0);
    }

    private void EditBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            FinishEdit((TextBox)sender, ok: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            FinishEdit((TextBox)sender, ok: false);
            e.Handled = true;
        }
    }

    private void EditBox_LostFocus(object sender, RoutedEventArgs e) => FinishEdit((TextBox)sender, ok: true);

    private void FinishEdit(TextBox tb, bool ok)
    {
        if (_editingI < 0) return; // 已提交（如 Mutate 触发的重建把焦点挤掉）
        var i = _editingI;
        var pre = _editingPre;
        var body = _editingBody;
        var src = _editingSrc;
        var value = tb.Text;
        _editingI = -1;
        if (_rowMap.TryGetValue(i, out var vm)) vm.Editing = false;
        if (!ok) return;
        var v = value.Trim();
        if (v.Length == 0 || v == body) return;
        // 歌词本体是内容修改：edit 栈（Vue finishEdit 的 pushEdit 语义）
        State.Mutate(p =>
        {
            var lines = (p.Lyrics ?? "").Replace("\r", "").Split('\n');
            if (src >= 0 && src < lines.Length)
            {
                lines[src] = pre + v;
                p.Lyrics = string.Join("\n", lines);
            }
        }, "改歌词", replan: true);
    }

    // ---------------- 行级指定（对位 LineRow.vue onNcut/onLayout/onLock） ----------------

    // 切分数：0 = 自动。Vue onNcut 走 remember/commit（外观栈）；overrides 亦属外观史 HKEYS
    private void NcutCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || !IsLoaded) return;
        if ((sender as FrameworkElement)?.DataContext is not LineRowVm row) return;
        var n = ((ComboBox)sender).SelectedIndex;
        if (n < 0) return;
        // 幂等闸：WPF 容器生成在下一个布局趟，届时 _syncing 已复位——绑定初始化会重复触发
        // SelectionChanged。值没变就绝不 Mutate，否则 Mutate→StateChanged→重建行→布局趟再触发
        // = 无限回环（栈溢出在最深的引擎叶子 CodePoints 爆出，表象像 Planner 分段递归，实为 UI 回环）
        {
            var curOv = State.Project.Overrides.TryGetValue(row.I.ToString(), out var o1) ? o1 : null;
            var curCuts = curOv != null && curOv["cuts"] != null ? (int?)curOv.Value<int>("cuts") : null;
            if ((n > 0 ? n : (int?)null) == curCuts) return;
        }
        State.Mutate(p =>
        {
            var ov = TapSession.TakeOverride(p, row.I);
            if (n > 0) ov["cuts"] = n;
            else ov.Remove("cuts");
            ov.Remove("single");
            TapSession.PutOverride(p, row.I, ov);
        }, "切分数", replan: true, look: true);
        if (State.Plan != null && row.I < State.Plan.Lines.Count)
            App.MainInstance!.Preview.Seek(State.Plan.Lines[row.I].Start + 0.001);
    }

    private void RowLayoutCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || !IsLoaded) return;
        if ((sender as FrameworkElement)?.DataContext is not LineRowVm row) return;
        var idx = ((ComboBox)sender).SelectedIndex;
        if (idx < 0) return;
        var key = idx > 0 ? LayoutKeys[idx] : "";
        // 幂等闸（同 NcutCombo：容器生成晚于 _syncing，绑定初始化重复触发必须靠值比较挡住）
        {
            var lOv = State.Project.Overrides.TryGetValue(row.I.ToString(), out var o2) ? o2 : null;
            var curLayout = lOv != null && lOv["layout"] != null ? (string?)lOv.Value<string>("layout") : null;
            if ((key.Length > 0 ? key : null) == curLayout) return;
        }
        State.Mutate(p =>
        {
            var ov = TapSession.TakeOverride(p, row.I);
            if (key.Length > 0) ov["layout"] = key;
            else ov.Remove("layout");
            TapSession.PutOverride(p, row.I, ov);
        }, "行布局", replan: true, look: true);
    }

    // 锁定这一行的构成：存 lockedSeed + 行快照（对位 onLock → J.lineSnapshot）
    private void LockBtn_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LineRowVm row) return;
        var plan = State.Plan;
        if (plan == null || row.I >= plan.Lines.Count) return;
        var line = plan.Lines[row.I];
        var toLock = !row.Locked;
        State.Mutate(p =>
        {
            var ov = TapSession.TakeOverride(p, row.I);
            if (toLock)
            {
                ov["lock"] = true;
                ov["lockedSeed"] = (long)line.Seed;
                var snap = Planner.LineSnapshot(plan, row.I);
                if (snap != null) ov["lockedCuts"] = snap;
            }
            else
            {
                ov.Remove("lock");
                ov.Remove("lockedSeed");
                ov.Remove("lockedCuts");
            }
            TapSession.PutOverride(p, row.I, ov);
        }, toLock ? "锁定行" : "解锁行", replan: true, look: true);
    }

    // ---------------- 切分格（对位 LineRow.vue onCutTime/onCutLayout） ----------------

    // 切分边界时刻（第 2 段起）：输入绝对时刻，存成行首起的偏移；清空恢复自动。
    // pushEdit 语义 → edit 栈；提交后定位到该边界（对位 seek）
    private void CutTimeBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var tb = (TextBox)sender;
        if (tb.DataContext is not CutVm cell) return;
        var raw = tb.Text;
        var hasV = double.TryParse(raw, NumberStyles.Float, Inv, out var v) && v >= 0 && double.IsFinite(v);
        var cur = State.Project.Overrides.TryGetValue(cell.Row.I.ToString(), out var o) && o is JObject oo
            ? ((oo["cutTime"] as JObject)?[cell.K.ToString()])
            : null;
        var had = cur != null && cur.Type is JTokenType.Float or JTokenType.Integer ? (double)cur : (double?)null;
        if (!hasV && had == null) return; // 仍是自动，无变更
        if (hasV && had != null && Math.Abs(had.Value - (Math.Max(0, Math.Round((v - cell.Row.LineStart) * 1000) / 1000.0))) < 1e-6)
            return;
        State.Mutate(p =>
        {
            var ov = TapSession.TakeOverride(p, cell.Row.I);
            var ct = ov["cutTime"] as JObject ?? new JObject();
            var key = cell.K.ToString();
            if (hasV) ct[key] = Math.Max(0, Math.Round((v - cell.Row.LineStart) * 1000) / 1000.0);
            else ct.Remove(key);
            if (ct.Count > 0) ov["cutTime"] = ct;
            else ov.Remove("cutTime");
            TapSession.PutOverride(p, cell.Row.I, ov);
        }, "切分边界", replan: true);
        App.MainInstance!.Preview.Seek(Math.Max(0, hasV ? v : cell.Start) + 0.001);
    }

    // 该段手法指定：写 cutLayouts + cutTech（setCutTech 的 layout 分支）；切分静默（cutQuiet）未移植
    private void CutCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || !IsLoaded) return;
        if ((sender as FrameworkElement)?.DataContext is not CutVm cell) return;
        var idx = ((ComboBox)sender).SelectedIndex;
        if (idx < 0) return;
        var key = idx > 0 ? LayoutKeys[idx] : "";
        // 幂等闸（同 NcutCombo/RowLayoutCombo：容器生成在 Measure 趟里触发绑定初始化，会重复触发
        // SelectionChanged）。值没变就绝不 Mutate——否则 Mutate→Replan→StateChanged→重建行→布局趟
        // 再触发 = 无限循环。dotnet-stack 实证：UI 线程整趟卡在 Replan 等锁（25s 调度饥饿、按钮全失灵）
        {
            var k0 = cell.K.ToString();
            var cOv = State.Project.Overrides.TryGetValue(cell.Row.I.ToString(), out var o3) ? o3 : null;
            var cTech = cOv?["cutTech"] as JObject;
            var cLay = cOv?["cutLayouts"] as JObject;
            var stored = (cTech?[k0] as JObject)?["layout"]?.Value<string>()
                         ?? cLay?[k0]?.Value<string>() ?? "";
            if (key == stored) return;
        }
        State.Mutate(p =>
        {
            var ov = TapSession.TakeOverride(p, cell.Row.I);
            var k = cell.K.ToString();
            if (key.Length > 0)
            {
                var cls = ov["cutLayouts"] as JObject ?? new JObject();
                cls[k] = key;
                ov["cutLayouts"] = cls;
                var tech = ov["cutTech"] as JObject ?? new JObject();
                var slot = tech[k] as JObject ?? new JObject();
                slot["layout"] = key;
                tech[k] = slot;
                ov["cutTech"] = tech;
            }
            else
            {
                if (ov["cutLayouts"] is JObject cls) { cls.Remove(k); if (cls.Count == 0) ov.Remove("cutLayouts"); }
                if (ov["cutTech"] is JObject tech)
                {
                    if (tech[k] is JObject slot) { slot.Remove("layout"); if (slot.Count == 0) tech.Remove(k); }
                    if (tech.Count == 0) ov.Remove("cutTech");
                }
            }
            TapSession.PutOverride(p, cell.Row.I, ov);
        }, "切分布局", replan: true, look: true);
        App.MainInstance!.Preview.Seek(cell.Start + cell.Half);
    }

    // 从这一行重新打点（对位 startTap(row.i)）
    private void TapFromBtn_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LineRowVm row)
            TapSession.Start(row.I);
    }
}

// ---------------- 行 / 切分格 VM（对位 LineRow.vue 的 Row/CutCell） ----------------

public sealed class LineRowVm : INotifyPropertyChanged
{
    /// <summary>行号（0 起）。</summary>
    public int I { get; set; }
    /// <summary>显示序号（01 起，等宽两位）。</summary>
    public string No { get; set; } = "";

    public string StartText { get; set; } = "";
    public string StartTip { get; set; } = "";
    public string Label { get; set; } = "";
    public string Full { get; set; } = "";
    public bool Interlude { get; set; }
    public bool Manual { get; set; }
    public double LineStart { get; set; }
    public int LayoutIndex { get; set; }
    public string LayoutKey { get; set; } = "";
    public int NcutIndex { get; set; }
    public bool Locked { get; set; }
    public bool HasCutsControls { get; set; } = true;
    public List<CutVm> Cuts { get; set; } = new();
    public string EditValue { get; set; } = "";

    private bool _active;

    /// <summary>播放中行（高亮 + 左侧琥珀条）。</summary>
    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Active)));
        }
    }

    private bool _editing;

    /// <summary>行内编辑中（文本换编辑框）。</summary>
    public bool Editing
    {
        get => _editing;
        set
        {
            if (_editing == value) return;
            _editing = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Editing)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class CutVm
{
    public LineRowVm Row = null!;
    public int K;
    public double Start;
    public double Half;

    /// <summary>边界时刻为手动指定（边框青色）。注意必须是属性：WPF 绑定不支持字段
    /// （旧实现是字段，DataTrigger 静默失败，青框从未生效）。</summary>
    public bool CtManual { get; set; }

    public string StartText { get; set; } = "";
    public bool HasTime { get; set; }
    public string Title { get; set; } = "";
    public string BoundaryTip { get; set; } = "";
    public int LayoutIndex { get; set; }
    public bool Forced { get; set; }
    public Brush Border { get; set; } = Brushes.Transparent;
}
