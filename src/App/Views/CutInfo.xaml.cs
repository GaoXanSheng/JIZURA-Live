using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using JizuraLive.Engine;
using Newtonsoft.Json.Linq;

namespace JizuraLive.App.Views;

// 当前切分信息行（对位 CutInfo.vue 的 chips 行）：10Hz 或切分变化时重读
// Planner.CutAt(plan, 引擎时钟)，展示 #序号 + 九个手法组 chip（加粗组字母 + 手法名）。
// is-forced（用户在替换面板钉过该组）→ 琥珀描边，语义对位 cutTechSlot/cutQuietSlot。
// 与 Vue 的取舍：chips 只是静态展示（Vue 里点 chip 弹手法替换面板，属后续里程碑；
// 重抽/一键随机按钮依赖 rerollCurrentCut，同样后置）。
public partial class CutInfo : UserControl
{
    /// <summary>手法组表（对位 look.ts CHIP_GROUPS：组 → 加粗字母）。</summary>
    private static readonly (string G, string Label)[] Groups =
    {
        ("layout", "l"), ("enter", "e"), ("hold", "h"), ("exit", "x"),
        ("decor", ""), ("treat", "t"), ("bg", "b"), ("cam", "c"), ("trans", "c"),
    };

    private int _lastIdx = -2; // -2 = 未初始化，首帧必刷
    private Brush _fg = null!, _mut = null!, _amber = null!, _line = null!, _raised = null!;

    public CutInfo()
    {
        InitializeComponent();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => Sync();
        Loaded += (_, _) =>
        {
            var st = App.MainInstance?.State;
            if (st != null) st.StateChanged += () => _lastIdx = -2; // plan 更新后强制重算
            Sync();
            timer.Start();
        };
    }

    private void Sync()
    {
        var state = App.MainInstance?.State;
        var preview = App.MainInstance?.Preview;
        if (state?.Plan == null || preview == null) return;
        var cut = Planner.CutAt(state.Plan, preview.CurrentTime);
        var idx = cut?.Index ?? -1;
        if (idx == _lastIdx) return;
        _lastIdx = idx;
        Rebuild(state, cut);
    }

    private void Rebuild(EditorState state, Cut? cut)
    {
        if (_fg == null)
        {
            _fg = (Brush)FindResource("Fg");
            _mut = (Brush)FindResource("Mut");
            _amber = (Brush)FindResource("Amber");
            _line = (Brush)FindResource("Line2");
            _raised = (Brush)FindResource("Raised");
        }
        Chips.Children.Clear();
        if (cut == null)
        {
            TblNone.Visibility = Visibility.Visible;
            Chips.Visibility = Visibility.Collapsed;
            return;
        }
        TblNone.Visibility = Visibility.Collapsed;
        Chips.Visibility = Visibility.Visible;

        var plan = state.Plan!;
        var project = state.Project;
        var k = LyricCutK(plan, cut);

        Chips.Children.Add(Chip("", (cut.Index + 1).ToString("00"), mono: true, forced: false));
        foreach (var (g, label) in Groups)
        {
            var v = GroupVal(cut, g);
            Chips.Children.Add(Chip(label, GroupName(g, v), mono: false,
                forced: k >= 0 && IsForced(project, cut.Line, k, g)));
        }
    }

    /// <summary>一颗 chip（对位 .chip：胶囊、raised 底、11px；is-forced 琥珀边）。</summary>
    private Border Chip(string label, string value, bool mono, bool forced)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (!string.IsNullOrEmpty(label))
            row.Children.Add(new TextBlock
            {
                Text = label,
                FontWeight = FontWeights.Medium,
                Foreground = _mut,
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        row.Children.Add(new TextBlock
        {
            Text = value,
            Foreground = _fg,
            FontFamily = mono ? new FontFamily("Consolas") : FontFamily,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        return new Border
        {
            Child = row,
            Background = _raised,
            BorderBrush = forced ? _amber : _line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(8, 2, 8, 2),
            SnapsToDevicePixels = true,
        };
    }

    // ---------------- look.ts 对位（只读部分） ----------------

    /// <summary>cutGroupVal：切分在各手法组上的当前值（缺省值对齐 JS）。</summary>
    private static string GroupVal(Cut c, string g) => g switch
    {
        "layout" => c.Layout ?? "",
        "enter" => c.Enter ?? "",
        "hold" => c.Hold ?? "",
        "exit" => c.Exit ?? "",
        "treat" => string.IsNullOrEmpty(c.Treat) ? "none" : c.Treat,
        "bg" => string.IsNullOrEmpty(c.Bg) ? "none" : c.Bg,
        "cam" => string.IsNullOrEmpty(c.Cam) ? "push" : c.Cam,
        "trans" => c.Trans ?? "",
        "decor" => c.Decor is { Count: > 0 } ? c.Decor[0].Id ?? "" : "",
        _ => "",
    };

    /// <summary>groupName：registry 里的手法显示名，查不到原样回 key，空值 = 无。</summary>
    private static string GroupName(string g, string k)
    {
        if (string.IsNullOrEmpty(k)) return "无";
        return TechName(g, k) ?? k;
    }

    private static string? TechName(string g, string k) => g switch
    {
        "layout" => Reg.Layouts.GetValueOrDefault(k)?.Name,
        "enter" => Reg.Enter.GetValueOrDefault(k)?.Name,
        "hold" => Reg.Hold.GetValueOrDefault(k)?.Name,
        "exit" => Reg.Exit.GetValueOrDefault(k)?.Name,
        "decor" => Reg.Decor.GetValueOrDefault(k)?.Name,
        "treat" => Reg.Treat.GetValueOrDefault(k)?.Name,
        "bg" => Reg.Bg.GetValueOrDefault(k)?.Name,
        "cam" => Reg.Camera.GetValueOrDefault(k)?.Name,
        "trans" => Reg.Trans.GetValueOrDefault(k)?.Name,
        _ => null,
    };

    /// <summary>lyricCutK：本行第几份歌词切分（special 的 title/interlude 不计）。</summary>
    private static int LyricCutK(Plan plan, Cut cut)
    {
        if (cut.Line < 0) return -1;
        var k = 0;
        foreach (var c in plan.Cuts)
        {
            if (c.Line != cut.Line) continue;
            if (Reg.Layouts.GetValueOrDefault(c.Layout)?.Special == true) continue;
            if (c.Index == cut.Index) return k;
            k++;
        }
        return -1;
    }

    /// <summary>cutTechSlot：行覆盖里钉到这份切分的组值（cutLayouts 并进 layout 槽，对齐 JS 合并顺序）。</summary>
    private static JObject TechSlot(Project p, int line, int k)
    {
        var ks = k.ToString();
        if (!p.Overrides.TryGetValue(line.ToString(), out var o)) return new JObject();
        var t = (o["cutTech"] as JObject)?[ks] as JObject ?? new JObject();
        var lay = (o["cutLayouts"] as JObject)?[ks]?.ToString();
        if (lay != null && t["layout"] == null)
        {
            var m = new JObject { ["layout"] = lay };
            foreach (var kv in t) m[kv.Key] = kv.Value;
            t = m;
        }
        return t;
    }

    /// <summary>cutQuietSlot：被静默（随机不许再动）的组。</summary>
    private static JObject QuietSlot(Project p, int line, int k)
    {
        if (!p.Overrides.TryGetValue(line.ToString(), out var o)) return new JObject();
        return (o["cutQuiet"] as JObject)?[k.ToString()] as JObject ?? new JObject();
    }

    /// <summary>is-forced：该组被用户钉过且没被静默（对位 Vue 的 forced 判定）。</summary>
    private static bool IsForced(Project p, int line, int k, string g)
    {
        var s = TechSlot(p, line, k)[g];
        if (s == null || s.Type == JTokenType.Null) return false;
        if (s.Type == JTokenType.String && string.IsNullOrEmpty(((string?)s))) return false;
        return QuietSlot(p, line, k)[g] == null;
    }
}
