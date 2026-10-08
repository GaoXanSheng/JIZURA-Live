using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using JizuraLive.App.Controls;
using JizuraLive.App.I18n;
using JizuraLive.Engine;
using Newtonsoft.Json.Linq;

namespace JizuraLive.App.Views;

// 当前切分信息行（对位 CutInfo.vue 的 chips 行）：10Hz 或切分变化时重读
// Planner.CutAt(plan, 引擎时钟)，展示 #序号 + 九个手法组 chip（组名 + 手法名）。
// is-forced（用户在替换面板钉过该组）→ 琥珀描边，语义对位 cutTechSlot/cutQuietSlot。
// 末尾「重抽 / 一键随机」= McpTools.RerollCurrentCut 的同款落点（Reg/Omakase/Mutate 直调，
// 不经过 MCP 协议层——工具实例可能没起，状态与引擎调用与 MCP 路径一致）。
// 与 Vue 的取舍：点 chip 弹「手法替换面板」属后续里程碑，chips 这一版只作静态展示（点击无操作）。
public partial class CutInfo : UserControl
{
    /// <summary>手法组表（对位 look.ts CHIP_GROUPS：组 → 组名的词库键（chip 前缀）→ 描边色键）。</summary>
    private static readonly (string G, string Key, string? Chip)[] Groups =
    {
        ("layout", "CutInfo.Group.Layout", "ChipL"), ("enter", "CutInfo.Group.Enter", "ChipE"),
        ("hold", "CutInfo.Group.Hold", "ChipH"), ("exit", "CutInfo.Group.Exit", "ChipX"),
        ("decor", "CutInfo.Group.Decor", null), ("treat", "CutInfo.Group.Treat", "ChipT"),
        ("bg", "CutInfo.Group.Bg", "ChipB"), ("cam", "CutInfo.Group.Cam", "ChipC"),
        ("trans", "CutInfo.Group.Trans", "ChipC"),
    };

    /// <summary>重抽（shuffle）动的组（对位 look.ts rerollCurrentCut）。</summary>
    private static readonly string[] ShuffleGroups = { "layout", "enter", "hold", "exit", "cam", "trans" };

    private int _lastIdx = -2; // -2 = 未初始化，首帧必刷
    private Brush _amber = null!;

    public CutInfo()
    {
        InitializeComponent();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => Sync();
        Loaded += (_, _) =>
        {
            _amber = (Brush)FindResource("Amber");
            var st = App.MainInstance?.State;
            if (st != null) st.StateChanged += () => _lastIdx = -2; // plan 更新后强制重算
            L.LanguageChanged -= ApplyTexts;
            L.LanguageChanged += ApplyTexts;
            Sync();
            timer.Start();
        };
        Unloaded += (_, _) => L.LanguageChanged -= ApplyTexts;
    }

    /// <summary>换语言：chips 整行重建（-2 强制 Sync 重算）。</summary>
    private void ApplyTexts()
    {
        _lastIdx = -2;
        Sync();
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
        Row.Children.Clear();
        if (cut == null)
        {
            Row.Children.Add(new TextBlock
            {
                Text = L.T("CutInfo.NoCut"),
                Style = (Style)FindResource("Hint"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            return;
        }

        var plan = state.Plan!;
        var k = LyricCutK(plan, cut);

        // 第一个 chip：mono 的两位数序号（对位 Vue 的 label='#' 被 v-if 抑制，只留值）
        Row.Children.Add(MakeChip("", (cut.Index + 1).ToString("00"), null, forced: false, enabled: true, mono: true));
        foreach (var (g, key, chip) in Groups)
        {
            var v = GroupVal(cut, g);
            var forced = k >= 0 && IsForced(state.Project, cut.Line, k, g);
            Row.Children.Add(MakeChip(L.T(key), GroupName(g, v), chip, forced, enabled: k >= 0, mono: false));
        }

        var on = k >= 0; // 非歌词切分（片头/间奏）不可重抽（对位 !items.some(it => it.enabled)）
        Row.Children.Add(MakeBtn(L.T("CutInfo.Reroll"), L.T("CutInfo.Reroll.Tip"), "BtnGhostSm", on, () => RerollCut("shuffle")));
        Row.Children.Add(MakeBtn(L.T("CutInfo.Omakase"), L.T("CutInfo.Omakase.Tip"), "BtnAccentSm", on, () => RerollCut("omakase")));
    }

    /// <summary>一颗 chip（ctl:JzChip：mut 色组名前缀 + 手法值；is-forced 琥珀描边）。</summary>
    private JzChip MakeChip(string lead, string value, string? chip, bool forced, bool enabled, bool mono)
    {
        var el = new JzChip
        {
            Lead = lead,
            Value = value,
            IsEnabled = enabled,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (chip != null) el.Style = (Style)FindResource(chip);
        if (mono) el.FontFamily = (FontFamily)FindResource("MonoFont");
        if (forced) el.BorderBrush = _amber;
        return el;
    }

    private Button MakeBtn(string text, string tip, string styleKey, bool enabled, Action act)
    {
        var b = new Button
        {
            Content = text,
            ToolTip = tip,
            Style = (Style)FindResource(styleKey),
            IsEnabled = enabled,
            VerticalAlignment = VerticalAlignment.Center,
        };
        b.Click += (_, _) => act();
        return b;
    }

    // ---------------- 重抽 / 一键随机（对位 look.ts rerollCurrentCut） ----------------

    /// <summary>重抽当前切分构成：shuffle = 六个构成组换一版；omakase = 全部组随机（避开同款）。</summary>
    private void RerollCut(string kind)
    {
        var state = App.MainInstance?.State;
        var preview = App.MainInstance?.Preview;
        if (state?.Plan == null || preview == null) return;
        var plan = state.Plan;
        var t0 = preview.CurrentTime;
        var cut = Planner.CutAt(plan, t0);
        if (cut == null) return;
        var k = LyricCutK(plan, cut);
        if (k < 0) return;
        var n = CodePointCount(Regex.Replace(cut.Text ?? "", @"\s+", ""));
        var groups = kind == "omakase" ? Groups.Select(x => x.G).ToArray() : ShuffleGroups;
        state.Mutate(p =>
        {
            foreach (var g in groups)
            {
                var allowNone = g is "decor" or "trans";
                var avoid = kind == "omakase" ? GroupVal(cut, g) : null;
                var key = PickEnabledTech(p, g, n, allowNone, avoid);
                if (key.Length > 0) SetCutTech(p, cut.Line, k, g, key);
            }
            MarkCutQuiet(p, cut.Line, k, groups, true);
        }, kind == "omakase" ? L.T("CutInfo.Mutate.Omakase") : L.T("CutInfo.Mutate.Reroll"), replan: false, look: true);
        state.ReplanNow();
        preview.Seek(t0); // 画面留在原处（对位 seek(t0)）
        _lastIdx = -2;
        Sync();
    }

    /// <summary>pickEnabledTech：启用中、随机允许的手法里抽一枚（对位 look.ts/McpTools）。</summary>
    private static string PickEnabledTech(Project p, string g, int n, bool allowNone, string? avoid)
    {
        p.Enabled.TryGetValue(g, out var en);
        var order = Reg.OrderOf(g);
        bool On(string key) => en == null || !en.TryGetValue(key, out var v) || v;
        var keys = order.Where(key => RegDef(g, key) is { Special: false } && On(key) && Omakase.RandomOk(p, g, key)).ToList();
        if (keys.Count == 0) keys = order.Where(key => RegDef(g, key) is { Special: false } && On(key)).ToList();
        if (g == "layout")
        {
            var fit = keys.Where(key => Reg.Layouts.TryGetValue(key, out var ld) && (ld.Fits == null || ld.Fits(n))).ToList();
            if (fit.Count > 0) keys = fit;
        }
        if (allowNone && !keys.Contains("none")) keys.Insert(0, "none");
        if (!string.IsNullOrEmpty(avoid) && keys.Count > 1) keys = keys.Where(key => key != avoid).ToList();
        if (keys.Count == 0) return "";
        return keys[Random.Shared.Next(keys.Count)];
    }

    private static int CodePointCount(string s)
    {
        var n = 0;
        for (var i = 0; i < s.Length; i++, n++)
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
        return n;
    }

    private static IRegEntry? RegDef(string g, string k) => g switch
    {
        "layout" => Reg.Layouts.GetValueOrDefault(k),
        "enter" => Reg.Enter.GetValueOrDefault(k),
        "hold" => Reg.Hold.GetValueOrDefault(k),
        "exit" => Reg.Exit.GetValueOrDefault(k),
        "decor" => Reg.Decor.GetValueOrDefault(k),
        "treat" => Reg.Treat.GetValueOrDefault(k),
        "bg" => Reg.Bg.GetValueOrDefault(k),
        "cam" => Reg.Camera.GetValueOrDefault(k),
        "trans" => Reg.Trans.GetValueOrDefault(k),
        _ => null,
    };

    // ---- overrides 写入（setCutTech / markCutQuiet 的逐行对位，同 McpTools） ----

    private static JObject TakeOverride(Project p, int line) =>
        p.Overrides.TryGetValue(line.ToString(), out var o) && o != null ? new JObject(o) : new JObject();

    private static void PutOverride(Project p, int line, JObject ov)
    {
        if (ov.Count > 0) p.Overrides[line.ToString()] = ov;
        else p.Overrides.Remove(line.ToString());
    }

    private static void SetCutTech(Project p, int line, int k, string group, string key)
    {
        var cur = TakeOverride(p, line);
        var cutTech = cur["cutTech"] as JObject ?? new JObject();
        var slot = cutTech[k.ToString()] as JObject ?? new JObject();
        cutTech.Remove(k.ToString());
        if (key.Length == 0) slot.Remove(group);
        else slot[group] = key;
        if (slot.Count > 0) cutTech[k.ToString()] = slot;
        else cutTech.Remove(k.ToString());
        if (cutTech.Count > 0) cur["cutTech"] = cutTech;
        else cur.Remove("cutTech");

        var cutQuiet = cur["cutQuiet"] as JObject ?? new JObject();
        var q = cutQuiet[k.ToString()] as JObject ?? new JObject();
        cutQuiet.Remove(k.ToString());
        q.Remove(group);
        if (q.Count > 0) cutQuiet[k.ToString()] = q;
        else cutQuiet.Remove(k.ToString());
        if (cutQuiet.Count > 0) cur["cutQuiet"] = cutQuiet;
        else cur.Remove("cutQuiet");

        if (group == "layout")
        {
            var cutLayouts = cur["cutLayouts"] as JObject ?? new JObject();
            if (key.Length == 0) cutLayouts.Remove(k.ToString());
            else cutLayouts[k.ToString()] = key;
            if (cutLayouts.Count > 0) cur["cutLayouts"] = cutLayouts;
            else cur.Remove("cutLayouts");
        }
        PutOverride(p, line, cur);
    }

    private static void MarkCutQuiet(Project p, int line, int k, string[] groups, bool on)
    {
        var cur = TakeOverride(p, line);
        var cutQuiet = cur["cutQuiet"] as JObject ?? new JObject();
        var q = cutQuiet[k.ToString()] as JObject ?? new JObject();
        cutQuiet.Remove(k.ToString());
        foreach (var g in groups)
        {
            if (on) q[g] = true;
            else q.Remove(g);
        }
        if (q.Count > 0) cutQuiet[k.ToString()] = q;
        else cutQuiet.Remove(k.ToString());
        if (cutQuiet.Count > 0) cur["cutQuiet"] = cutQuiet;
        else cur.Remove("cutQuiet");
        PutOverride(p, line, cur);
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
        if (string.IsNullOrEmpty(k)) return L.T("CutInfo.None");
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
