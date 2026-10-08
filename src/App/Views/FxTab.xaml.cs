using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using JizuraLive.App.I18n;
using JizuraLive.Engine;
using Newtonsoft.Json.Linq;

namespace JizuraLive.App.Views;

// 特效面板（对位 FxTab.vue + Dialogs.vue 音频特效弹窗的落panel版）：
// fx 参数滑块（双向绑定手写：拖动中只刷显示，DragCompleted / 键盘改动才 Mutate 防循环）+
// 闪光/抽帧/HUD/种子/隐藏装饰编号时刻开关 + 音频特效（预设下拉 + 效果单元开关，AudioFx 公开表）。
public partial class FxTab : UserControl
{
    private bool _built;
    private bool _syncing;

    private EditorState S => App.MainInstance!.State;

    /// <summary>fx 参数滑块表（对位 FX 常量：key → 词库键，L.T 在构建/换语言时解析）。</summary>
    private static readonly (string Key, string LKey)[] FxKeys =
    {
        ("motion", "Fx.Param.Motion"), ("glitch", "Fx.Param.Glitch"), ("chroma", "Fx.Param.Chroma"), ("decor", "Fx.Param.Decor"),
        ("density", "Fx.Param.Density"), ("texture", "Fx.Param.Texture"), ("bgSwitch", "Fx.Param.BgSwitch"),
    };

    /// <summary>抽帧档位（koma：0/8/12/24）。</summary>
    private static readonly (int V, string LKey)[] KomaOptions =
    {
        (0, "Fx.Koma.Full"), (8, "Fx.Koma.Threes"), (12, "Fx.Koma.Twos"), (24, "Fx.Koma.Ones"),
    };

    // 锁定按钮的悬浮说明（对位 Vue 的 LOCK_ON / LOCK_OFF）
    private static string LockOn => L.T("Fx.LockOn");
    private static string LockOff => L.T("Fx.LockOff");

    private sealed record FxRow(string Key, string Label, Slider Slider, TextBlock Value, Button Lock);

    private readonly List<FxRow> _rows = new();
    private readonly Dictionary<string, bool> _thumbDrag = new();

    public FxTab()
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
            BuildSliderRows();
            BuildComboItems();
            BuildAfxRows();
        }
        S.StateChanged += OnState;
        L.LanguageChanged += OnLanguageChanged;
        Pull();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        S.StateChanged -= OnState;
        L.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged() => Dispatcher.Invoke(ApplyTexts);

    /// <summary>语言切换后重刷 code-behind 动态文案：抽帧/HUD 下拉项与预设下拉占位（清空后由 BuildComboItems
    /// 与 PullAudioFx 重建；音频单元行来自引擎注册表，不在词库范围）。选择复位全程 _syncing 防误触发。</summary>
    private void ApplyTexts()
    {
        if (App.MainInstance == null) return;
        _syncing = true;
        try
        {
            CbKoma.Items.Clear();
            CbHud.Items.Clear();
            CbAfxPreset.Items.Clear();
            BuildComboItems();
        }
        finally
        {
            _syncing = false;
        }
        Pull(); // 重选 + 重建「— 选择预设 —」占位
    }

    /// <summary>滚到音频特效区（Transport 的「特效」按钮落点）。</summary>
    public void ScrollToAudio() => AfxHeader.BringIntoView();

    private void OnState() => Dispatcher.Invoke(Pull);

    // ---------------- 构建 ----------------

    private void BuildSliderRows()
    {
        // fx 参数滑块（7 行）。XAML 里的容器 Grid x:Name="SliderRows" 目前被摘掉（见 FxTab.xaml 的 TODO），
        // 这里跟着停用：Pull 里 _rows 为空自然跳过。把那个 Grid 放回 XAML 后，恢复本方法原实现即可。
        // TODO(fx 滑块区回迁)：原实现见 git 历史（96px 标签 | 滑块 | 36px 数值 | 锁定键，行距 8）。
    }

    private void BuildComboItems()
    {
        foreach (var (v, lkey) in KomaOptions)
            CbKoma.Items.Add(new ComboBoxItem { Content = L.T(lkey), Tag = v });
        foreach (var (v, lkey) in new[] { ("auto", "Fx.Hud.Auto"), ("on", "Fx.Hud.On"), ("off", "Fx.Hud.Off") })
            CbHud.Items.Add(new ComboBoxItem { Content = L.T(lkey), Tag = v });
    }

    private void BuildAfxRows()
    {
        foreach (var def in AudioFx.Units.Values)
        {
            var id = def.Id;
            var cb = new CheckBox
            {
                Content = def.Label,
                Tag = id,
                Margin = new Thickness(0, 0, 0, 6), // .flex flex-col gap-1.5
                ToolTip = def.Note,
            };
            cb.Checked += (_, _) => { if (!_syncing) ToggleUnit(id, true); };
            cb.Unchecked += (_, _) => { if (!_syncing) ToggleUnit(id, false); };
            AfxUnitRows.Children.Add(cb);
        }
    }

    // ---------------- 提交（Mutate） ----------------

    private void CommitFx(string key, double v)
    {
        if (Math.Abs(GetFx(S.Project.Fx, key) - v) < 1e-9) return;
        S.Mutate(p =>
        {
            SetFx(p.Fx, key, v);
            p.Mood = null; // onSlider：动了参数就丢掉一键随机的主题
        }, L.F("Fx.Mutate.Param", L.T(FxKeys.First(f => f.Key == key).LKey)));
    }

    private void ToggleLock(string key)
    {
        var on = !S.Project.Locks.Params.TryGetValue(key, out var v) || !v;
        S.Mutate(p =>
        {
            if (on) p.Locks.Params[key] = true;
            else p.Locks.Params.Remove(key);
        }, L.F("Fx.Mutate.LockParam", key), replan: false);
    }

    private void BtnKomaLock_Click(object sender, RoutedEventArgs e) => ToggleLock("koma");

    private void Unify_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        S.Mutate(p => p.Unify = CkUnify.IsChecked == true, L.T("Fx.Unify"), look: true);
    }

    private void Typeset_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        S.Mutate(p => p.Typeset = CkTypeset.IsChecked == true, L.T("Fx.Typeset"), look: true);
    }

    private void Flash_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        S.Mutate(p => p.Fx.Flash = CkFlash.IsChecked == true, L.T("Fx.Flash"));
    }

    private void CbKoma_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || CbKoma.SelectedItem is not ComboBoxItem it) return;
        var k = (int)it.Tag!;
        S.Mutate(p =>
        {
            p.Fx.Koma = k;
            p.Fx.OnTwos = k > 0; // onKoma 语义：有抽帧即二拍作画
            p.Mood = null;
        }, L.F("Fx.Mutate.Koma", k));
    }

    private void CbHud_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || CbHud.SelectedItem is not ComboBoxItem it) return;
        var v = (string)it.Tag!;
        if (v == (S.Project.Fx.Hud ?? "auto")) return;
        S.Mutate(p => p.Fx.Hud = v, L.T("Fx.Hud"));
    }

    private void HideDeco_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        S.Mutate(p =>
        {
            p.Fx.HideNo = CkHideNo.IsChecked == true;
            p.Fx.HideTime = CkHideTime.IsChecked == true;
        }, L.T("Fx.Mutate.HideDeco"));
    }

    private void TbSeed_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitSeed();
            e.Handled = true;
        }
    }

    private void TbSeed_Commit(object sender, RoutedEventArgs e) => CommitSeed();

    private void CommitSeed()
    {
        if (_syncing) return;
        if (!uint.TryParse(TbSeed.Text.Trim(), out var seed))
        {
            TbSeed.Text = S.Project.Seed.ToString(); // 非法回显
            return;
        }
        if (seed == S.Project.Seed) return;
        S.Mutate(p => p.Seed = seed, L.T("Fx.Seed"));
    }

    private void BtnNewSeed_Click(object sender, RoutedEventArgs e)
        => S.Mutate(p => p.Seed = (uint)(Random.Shared.NextDouble() * 1e9), L.T("Fx.NewSeed")); // (Math.random()*1e9)|0

    // ---------------- 音频特效 ----------------

    /// <summary>效果单元开关：勾上=入链（缺省参数），取消=移除（对位弹窗的 addUnit/rmUnit）。</summary>
    private void ToggleUnit(string id, bool on)
    {
        var cur = AudioFx.SanitizeAudioFx(S.Project.AudioFx);
        var units = ((JArray)cur["units"]!).Where(u => u is not JObject o || o["id"]?.ToString() != id).ToList();
        if (on)
            units.Add(new JObject { ["id"] = id, ["on"] = true, ["params"] = AudioFx.Units[id].Defaults.DeepClone() });
        cur["units"] = new JArray(units);
        var label = AudioFx.Units[id].Label;
        S.Mutate(p => p.AudioFx = cur, L.F(on ? "Fx.Mutate.UnitOn" : "Fx.Mutate.UnitOff", label), replan: false);
    }

    private void CbAfxPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || CbAfxPreset.SelectedItem is not ComboBoxItem { Tag: string id } || id.Length == 0) return;
        var preset = AudioFx.Presets.FirstOrDefault(x => x.Id == id);
        if (preset == null) return;
        S.Mutate(p => p.AudioFx = AudioFx.SanitizeAudioFx((JObject)preset.Cfg.DeepClone()),
            L.F("Fx.Mutate.Preset", preset.Label), replan: false);
        // StateChanged → Pull 会把下拉复位到占位（预设是动作不是状态）
    }

    // ---------------- 回刷 ----------------

    private void Pull()
    {
        if (App.MainInstance == null) return;
        _syncing = true;
        try
        {
            var p = S.Project;

            SetChecked(CkUnify, p.Unify);
            SetChecked(CkTypeset, p.Typeset);
            SetChecked(CkFlash, p.Fx.Flash);
            SetChecked(CkHideNo, p.Fx.HideNo);
            SetChecked(CkHideTime, p.Fx.HideTime);

            foreach (var row in _rows)
            {
                var v = GetFx(p.Fx, row.Key);
                if (Math.Abs(row.Slider.Value - v) > 1e-9) row.Slider.Value = v;
                row.Value.Text = Round100(v);
                var locked = p.Locks.Params.TryGetValue(row.Key, out var lv) && lv;
                row.Lock.Tag = locked ? "on" : "off";
                row.Lock.ToolTip = locked ? LockOff : LockOn;
            }

            var koma = KomaOptions.Where(o => o.V == p.Fx.Koma).Select(o => o.V).FirstOrDefault(12);
            SelectTag(CbKoma, koma.ToString());
            SelectTag(CbHud, string.IsNullOrEmpty(p.Fx.Hud) ? "auto" : p.Fx.Hud);
            if (TbSeed.Text != p.Seed.ToString()) TbSeed.Text = p.Seed.ToString();

            var komaLocked = p.Locks.Params.TryGetValue("koma", out var klv) && klv;
            BtnKomaLock.Tag = komaLocked ? "on" : "off";
            BtnKomaLock.ToolTip = komaLocked ? LockOff : LockOn;

            PullAudioFx();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void PullAudioFx()
    {
        if (CbAfxPreset.Items.Count == 0)
        {
            CbAfxPreset.Items.Add(new ComboBoxItem { Content = L.T("Fx.PresetPlaceholder"), Tag = "" });
            foreach (var pr in AudioFx.Presets)
                CbAfxPreset.Items.Add(new ComboBoxItem
                {
                    Content = pr.Label,
                    Tag = pr.Id,
                    ToolTip = L.F("Fx.ApplyPreset.Tip", pr.Label),
                });
        }
        CbAfxPreset.SelectedIndex = 0; // 预设下拉只作动作入口，不记状态（Vue 为按钮组）

        var fx = AudioFx.SanitizeAudioFx(S.Project.AudioFx);
        var onIds = new HashSet<string>();
        if (fx["units"] is JArray arr)
            foreach (var u in arr.OfType<JObject>())
                if (u["on"]?.Type != JTokenType.Boolean || (bool)u["on"]!)
                    onIds.Add(u["id"]?.ToString() ?? "");
        foreach (var cb in AfxUnitRows.Children.OfType<CheckBox>())
            SetChecked(cb, onIds.Contains((string)cb.Tag!));
    }

    // ---------------- 小工具 ----------------

    private static double GetFx(FxSettings f, string k) => k switch
    {
        "motion" => f.Motion,
        "glitch" => f.Glitch,
        "chroma" => f.Chroma,
        "decor" => f.Decor,
        "density" => f.Density,
        "texture" => f.Texture,
        "bgSwitch" => f.BgSwitch,
        _ => 0.5,
    };

    private static void SetFx(FxSettings f, string k, double v)
    {
        switch (k)
        {
            case "motion": f.Motion = v; break;
            case "glitch": f.Glitch = v; break;
            case "chroma": f.Chroma = v; break;
            case "decor": f.Decor = v; break;
            case "density": f.Density = v; break;
            case "texture": f.Texture = v; break;
            case "bgSwitch": f.BgSwitch = v; break;
        }
    }

    private static string Round100(double v) => Math.Round(v * 100).ToString();

    // 统一感/文字排列是 ToggleButton（.switch），其余开关仍是 CheckBox
    private static void SetChecked(ToggleButton tb, bool v)
    {
        if (tb.IsChecked != v) tb.IsChecked = v;
    }

    private static void SelectTag(ComboBox cb, string tag)
    {
        foreach (var i in cb.Items.OfType<ComboBoxItem>())
            if (string.Equals(i.Tag?.ToString(), tag, StringComparison.Ordinal))
            {
                if (!ReferenceEquals(cb.SelectedItem, i)) cb.SelectedItem = i;
                return;
            }
        if (cb.Items.Count > 0) cb.SelectedIndex = 0;
    }
}
