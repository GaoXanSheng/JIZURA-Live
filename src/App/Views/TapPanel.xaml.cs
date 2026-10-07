using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using JizuraLive.Engine;
using Newtonsoft.Json.Linq;

namespace JizuraLive.App.Views;

// 打点同步面板（对位 frontend/src/components/lyrics/TapPanel.vue）：
// 打点中显示浮层（琥珀底），按钮 TAP / 回退一步 / 结束 + 打点速度 + 倒数开关。
// 键位（对位 boot.ts bindKeys 打点分支）：Space/Enter = 打点、Tab = 接切分、Backspace = 回退、Esc = 结束。
public partial class TapPanel : UserControl
{
    private readonly DispatcherTimer _timer;
    private double _lastT;          // 循环回卷检测用
    private bool _wasActive;
    private bool _subscribed;

    public TapPanel()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) =>
        {
            if (!TapSession.Active) return;
            var pv = App.MainInstance!.Preview;
            var t = pv.CurrentTime;
            // 对位 bus 'tapend'（look.ts）：播到循环终点自动结束打点。只认回卷（t 跳回小值），
            // 手动 seek / 暂停不打断打点会话（与 Vue 一致）
            if (!TapSession.Counting && _lastT > 2.0 && t < 1.0)
                TapSession.Stop();
            _lastT = t;
        };
        Loaded += (_, _) =>
        {
            if (_subscribed) return;
            _subscribed = true;
            TapSession.Changed += Refresh;
            App.MainInstance!.State.StateChanged += OnStateChanged;
            // 键位挂在窗口级：打点时焦点可能在任意面板，隧道路由要经过公共祖先才收得到。
            // handledEventsToo=true：打点中按空格，MainWindow 的 OnPreviewKeyDown 会先把空格当播放/暂停
            // 处理掉（并标记 Handled），这里再收到时把误触的暂停恢复成播放再打点（同一事件内完成，无感知）
            var win = Window.GetWindow(this);
            // 方法组不能裸传 Delegate 参数（C# 会推断出匿名委托类型，与 RoutedEvent
            // 注册的 KeyEventHandler 不匹配，运行时抛“处理程序类型不匹配”）——显式包装
            win?.AddHandler(Keyboard.PreviewKeyDownEvent,
                new KeyEventHandler(OnWinPreviewKeyDown), handledEventsToo: true);
            Refresh();
            _timer.Start();
        };
    }

    private void OnStateChanged() => Dispatcher.Invoke(Refresh);

    // ---------------- 打点键位（boot.ts bindKeys 对位） ----------------

    private void OnWinPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!TapSession.Active) return;
        var pv = App.MainInstance!.Preview;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var typing = Keyboard.FocusedElement is TextBoxBase;

        // Esc 无论是否在输入框里都结束打点（对位 Vue）
        if (key == Key.Escape)
        {
            pv.Pause();
            TapSession.Stop();
            e.Handled = true;
            return;
        }

        if (typing) return; // 输入框里这些键照常编辑文字

        if (key is Key.Space or Key.Enter)
        {
            if (!pv.IsPlaying) pv.Play(); // 修掉窗口级空格=播放/暂停造成的误暂停
            TapSession.TapNow();
            e.Handled = true;
        }
        else if (key == Key.Tab)
        {
            TapSession.TapCut(); // 刚打点的行的下一个切分从现在开始
            e.Handled = true;    // 同时拦掉 Tab 的焦点移动
        }
        else if (key == Key.Back)
        {
            TapSession.TapBack();
            e.Handled = true;
        }
    }

    // ---------------- UI 刷新 ----------------

    private void Refresh()
    {
        if (!IsLoaded) return;
        var main = App.MainInstance!;
        var active = TapSession.Active;
        if (active && !_wasActive)
            _lastT = main.Preview.CurrentTime;
        _wasActive = active;
        Root.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (!active) return;

        var plan = main.State.Plan;
        var lineText = "—";
        if (TapSession.CountDisplay is int n) lineText = $"倒数 {n}…";
        else if (plan != null && TapSession.I >= 0 && TapSession.I < plan.Lines.Count)
        {
            var ln = plan.Lines[TapSession.I];
            lineText = $"{TapSession.I + 1}. {(ln.Interlude ? "〔间奏〕" : ln.Text)}";
        }

        TblLine.Text = lineText;
        BtnTap.IsEnabled = !TapSession.Counting;
        BtnTap.Content = TapSession.Counting ? "准备开始…" : "TAP";
        BtnBack.IsEnabled = TapSession.Done.Count > 0;
        HintBox.Visibility = TapSession.Done.Count > 2 ? Visibility.Collapsed : Visibility.Visible;
        if (!_rateTouched) CmbRate.SelectedIndex = TapSession.Rate switch { 0.75 => 1, 0.5 => 2, _ => 0 };
        if (!_countTouched) ChkCount.IsChecked = TapSession.CountOpt;
    }

    private bool _rateTouched; // 用户动过就不再被会话刷回
    private bool _countTouched;

    private void BtnTap_Click(object sender, RoutedEventArgs e) => TapSession.TapNow();

    private void BtnBack_Click(object sender, RoutedEventArgs e) => TapSession.TapBack();

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        // 对位 TapPanel.vue 的 onStop：pause() + stopTap()
        App.MainInstance!.Preview.Pause();
        TapSession.Stop();
    }

    private void CmbRate_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _rateTouched = true;
        TapSession.Rate = CmbRate.SelectedIndex switch { 1 => 0.75, 2 => 0.5, _ => 1 };
        // TODO(M4)：预览播放钟是墙钟外推，暂不支持变速播放；选择仅记档。
        // 打点存的是歌曲本来时刻，速率只影响听感，不影响写进去的数据
    }

    private void ChkCount_Changed(object sender, RoutedEventArgs e)
    {
        _countTouched = true;
        TapSession.CountOpt = ChkCount.IsChecked == true;
    }
}

/// <summary>
/// 打点同步会话（对位 frontend/src/jizura/look.ts 的 S.tap + startTap/tapNow/tapCut/tapBack/stopTap）。
/// 放在 Views 静态类上：音频面板「打点同步」按钮、行列表 ◎ 按钮、本面板共享同一会话。
/// 数据全部经 EditorState.Mutate 写入 project.timing.lineTimes / project.overrides[*].cutTime，
/// 每次打点一条撤销记录（Ctrl+Z 可逐步回退；Vue 是整场一条，这里粒度更细）。
/// </summary>
internal static class TapSession
{
    /// <summary>一次打点（行或切分边界）的回退凭据：had = 改前的旧值（null = 原本无）。</summary>
    public sealed class DoneEntry
    {
        public int I;                 // 行号
        public double? Had;
        public bool Cut;              // true = 切分边界打点（Tab）
        public int Li;                // Cut 时：所属行
        public int K;                 // Cut 时：切分序号（第 K+1 段的边界）
    }

    public static bool Active { get; private set; }
    public static int I { get; private set; }           // 下一行
    public static int From { get; private set; }        // 起始行
    public static int? Cl { get; private set; }         // 刚打点的行（Tab 接切分用）
    public static int Ck { get; private set; } = 1;     // Cl 行的下一个切分序号
    public static bool Counting { get; private set; }   // 倒数进行中
    public static int? CountDisplay { get; private set; }
    public static double Rate { get; set; } = 1;        // 仅记档（预览钟尚不支持变速），见 TapPanel 注释
    public static bool CountOpt { get; set; }           // 倒数 3・2・1 开关
    public static readonly List<DoneEntry> Done = new();

    /// <summary>会话状态变化（面板订阅刷新；全部在 UI 线程触发）。</summary>
    public static event Action? Changed;

    public static void Notify() => Changed?.Invoke();

    private static PreviewEngine Preview => App.MainInstance!.Preview;
    private static Plan? Plan => App.MainInstance!.State.Plan;

    /// <summary>从 from 行开始打点：播放从该行稍前起（不早于上一行），更早的行保持原时间。</summary>
    public static void Start(int from)
    {
        var plan = Plan;
        if (plan == null || plan.Lines.Count == 0) return;
        from = Math.Clamp(from, 0, plan.Lines.Count - 1);
        var epoch = ++_epoch;
        Active = true;
        I = from;
        From = from;
        Done.Clear();
        Cl = null;
        Ck = 1;
        Counting = false;
        CountDisplay = null;
        // 比该行稍早，但不早于上一行（对位 look.ts startTap 的 t0）
        var prev = from > 0 ? plan.Lines[from - 1] : null;
        var cur = plan.Lines[from];
        var t0 = from == 0 ? 0 : Math.Max(0, Math.Max(prev!.Start + 0.01, cur.Start - 2.5));
        Preview.Seek(t0);
        Notify();
        if (CountOpt)
        {
            Counting = true;
            Notify();
            _ = CountdownAsync(epoch);
        }
        else
        {
            Preview.Play();
        }
    }

    // 倒数：3・2・1 之后歌曲才开始（每步 700ms，对位 look.ts）
    private static async Task CountdownAsync(uint epoch)
    {
        for (var n = 3; n >= 1; n--)
        {
            if (!Active || epoch != _epoch) return;
            CountDisplay = n;
            Notify();
            await Task.Delay(700);
        }

        if (!Active || epoch != _epoch) return;
        Counting = false;
        CountDisplay = null;
        Notify();
        Preview.Play();
    }

    /// <summary>TAP：把下一行的开始对齐到现在（存歌曲时刻，打点中播放速度不影响数值）。</summary>
    public static void TapNow()
    {
        if (!Active || Counting) return; // 倒数期间不打点
        var plan = Plan;
        if (plan == null || I >= plan.Lines.Count) return;
        var i = I;
        var t = Math.Round(Preview.CurrentTime * 1000) / 1000.0;
        DoneEntry? entry = null;
        App.MainInstance!.State.Mutate(p =>
        {
            var lt = p.Timing.LineTimes;
            entry = new DoneEntry
            {
                I = i,
                Had = lt.TryGetValue(i.ToString(), out var h) ? h : null,
            };
            lt[i.ToString()] = t;
            // 后面的行若在之前一轮被打过且早于本时间，清掉防止倒挂（对位 look.ts tapNow）
            foreach (var k in lt.Keys.Where(k => int.TryParse(k, out var ki) && ki > i && lt[k] <= t + 0.2).ToList())
                lt.Remove(k);
        }, "打点");
        if (entry != null) Done.Add(entry);
        I++;
        Cl = i;
        Ck = 1;
        if (Plan == null || I >= Plan.Lines.Count) Stop();
        else Notify();
    }

    /// <summary>打点中的 Tab：刚才打点的行的下一个切分从现在开始（多切分行）。</summary>
    public static void TapCut()
    {
        if (!Active || Counting || Cl == null) return;
        var plan = Plan;
        if (plan == null) return;
        var li = Cl.Value;
        if (li < 0 || li >= plan.Lines.Count) return;
        var k = Ck;
        var ln = plan.Lines[li];
        var n = plan.Cuts.Count(c =>
            c.Line == li && Reg.Layouts.TryGetValue(c.Layout, out var ld) && ld != null && !ld.Special);
        if (k < 1 || k >= n)
            return; // 这一行没有更多切分了
        var t = Math.Round(Preview.CurrentTime * 1000) / 1000.0;
        DoneEntry? entry = null;
        App.MainInstance!.State.Mutate(p =>
        {
            var cur = TakeOverride(p, li);
            var ct = cur["cutTime"] as JObject ?? new JObject();
            var key = k.ToString();
            var old = ct[key];
            entry = new DoneEntry
            {
                I = li,
                Had = old != null && old.Type is JTokenType.Float or JTokenType.Integer ? (double)old : null,
                Cut = true,
                Li = li,
                K = k,
            };
            // 对齐「现在」的绝对时刻，存成行首起的偏移（对位 look.ts tapCut）
            ct[key] = Math.Max(0, Math.Round((t - ln.Start) * 1000) / 1000.0);
            cur["cutTime"] = ct;
            p.Overrides[li.ToString()] = cur;
        }, "打点切分");
        if (entry != null) Done.Add(entry);
        Ck = k + 1;
        Notify();
    }

    /// <summary>回退一次打点并跳回一点（Backspace / 回退一步按钮）。</summary>
    public static void TapBack()
    {
        if (!Active || Done.Count == 0) return;
        var d = Done[^1];
        Done.RemoveAt(Done.Count - 1);
        App.MainInstance!.State.Mutate(p =>
        {
            if (d.Cut)
            {
                var cur = TakeOverride(p, d.Li);
                var ct = cur["cutTime"] as JObject ?? new JObject();
                var key = d.K.ToString();
                if (d.Had != null) ct[key] = d.Had.Value;
                else ct.Remove(key);
                if (ct.Count > 0) cur["cutTime"] = ct;
                else cur.Remove("cutTime");
                PutOverride(p, d.Li, cur);
                Cl = d.Li;
                Ck = d.K;
            }
            else
            {
                var lt = p.Timing.LineTimes;
                if (d.Had != null) lt[d.I.ToString()] = d.Had.Value;
                else lt.Remove(d.I.ToString());
                I = d.I;
                // 切分打点继续接在上一打点行后面（对位 look.ts tapBack）
                var prev = Done.LastOrDefault(x => !x.Cut);
                Cl = prev?.I;
                Ck = prev != null ? 1 + Done.Count(x => x.Cut && x.Li == prev.I) : 1;
            }
        }, "打点回退");
        Preview.Seek(Math.Max(0, Preview.CurrentTime - 3));
        if (!Preview.IsPlaying) Preview.Play();
        Notify();
    }

    /// <summary>结束打点（面板「结束」= 先 Pause 再 Stop；Esc 同）。</summary>
    public static void Stop()
    {
        if (!Active) return;
        Active = false;
        Counting = false;
        CountDisplay = null;
        _epoch++;
        // 速率 ≠ 1 时回正常速度的 seek 对位（look.ts stopTap）随 M4 变速播放接入
        Notify();
    }

    private static uint _epoch; // 会话代号：取消进行中的倒数

    // ---------------- overrides 的 JObject 小工具（setOv 语义：空对象删除） ----------------

    internal static JObject TakeOverride(Project p, int line)
    {
        return p.Overrides.TryGetValue(line.ToString(), out var o) && o is JObject jo
            ? new JObject(jo)
            : new JObject();
    }

    internal static void PutOverride(Project p, int line, JObject ov)
    {
        if (ov.Count > 0) p.Overrides[line.ToString()] = ov;
        else p.Overrides.Remove(line.ToString());
    }
}
