using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
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

    /// <summary>fx 参数滑块表（对位 FX 常量：key → 日文标签照搬）。</summary>
    private static readonly (string Key, string Label)[] FxKeys =
    {
        ("motion", "动态强度"), ("glitch", "故障效果"), ("chroma", "色彩错位"), ("decor", "装饰量"),
        ("density", "镜头密度"), ("texture", "质感"), ("bgSwitch", "背景切换"),
    };

    /// <summary>抽帧档位（koma：0/8/12/24）。</summary>
    private static readonly (int V, string Label)[] KomaOptions =
    {
        (0, "全帧（同输出 fps）"), (8, "一拍三（8 张/秒）"), (12, "一拍二（12 张/秒）"), (24, "一拍一（24 张/秒）"),
    };

    // 锁定按钮的悬浮说明（对位 Vue 的 LOCK_ON / LOCK_OFF）
    private const string LockOn = "锁定：一键随机/重抽不改动此项";
    private const string LockOff = "锁定中，点击解除";

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
        Pull();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => S.StateChanged -= OnState;

    private void OnState() => Dispatcher.Invoke(Pull);

    // ---------------- 构建 ----------------

    private void BuildSliderRows()
    {
        foreach (var (key, label) in FxKeys)
        {
            // 参数名（.text-xs）
            var lab = new TextBlock
            {
                Text = label,
                FontSize = 12,
                LineHeight = 18,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };

            var slider = new Slider { Style = (Style)Resources["FxSlider"], Margin = new Thickness(0, 0, 8, 0) };

            // 数值（font-mono text-[11px] text-mut text-right；36px 列，右侧留 8 给锁定键）
            var value = new TextBlock
            {
                FontSize = 11,
                FontFamily = (FontFamily)FindResource("MonoFont"),
                LineHeight = 16.5,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Foreground = (Brush)FindResource("Mut"),
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };

            // 锁定键（.btn.btn-icon.btn-ghost，锁形 Path 随前景色）
            var lockBtn = new Button
            {
                Style = (Style)Resources["LockBtn"],
                Content = FindResource("FxLock"),
                ToolTip = LockOn,
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(lab, 0);
            Grid.SetColumn(slider, 1);
            Grid.SetColumn(value, 2);
            Grid.SetColumn(lockBtn, 3);
            grid.Children.Add(lab);
            grid.Children.Add(slider);
            grid.Children.Add(value);
            grid.Children.Add(lockBtn);
            grid.Margin = new Thickness(0, 0, 0, 8);

            SliderRows.Children.Add(grid);

            var row = new FxRow(key, label, slider, value, lockBtn);
            _rows.Add(row);

            slider.ValueChanged += (_, ev) =>
            {
                value.Text = Round100(ev.NewValue);
                if (_syncing) return;
                if (!_thumbDrag.TryGetValue(key, out var dragging) || !dragging)
                    CommitFx(key, ev.NewValue); // 键盘/滚轮/单击轨道：立即落
            };
            // 拖动中只刷显示，DragCompleted 落一次（Vue replanSoon 的防抖等价物）
            slider.Loaded += (_, _) =>
            {
                if (slider.Template?.FindName("thumb", slider) is not Thumb thumb) return;
                thumb.DragStarted += (_, _) => _thumbDrag[key] = true;
                thumb.DragCompleted += (_, _) =>
                {
                    _thumbDrag[key] = false;
                    CommitFx(key, slider.Value);
                };
            };

            lockBtn.Click += (_, _) => ToggleLock(key);
        }
    }

    private void BuildComboItems()
    {
        foreach (var (v, label) in KomaOptions)
            CbKoma.Items.Add(new ComboBoxItem { Content = label, Tag = v });
        foreach (var (v, label) in new[] { ("auto", "随风格"), ("on", "始终显示"), ("off", "不显示") })
            CbHud.Items.Add(new ComboBoxItem { Content = label, Tag = v });
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
        }, "特效：" + FxKeys.First(f => f.Key == key).Label);
    }

    private void ToggleLock(string key)
    {
        var on = !S.Project.Locks.Params.TryGetValue(key, out var v) || !v;
        S.Mutate(p =>
        {
            if (on) p.Locks.Params[key] = true;
            else p.Locks.Params.Remove(key);
        }, "锁定参数：" + key, replan: false);
    }

    private void BtnKomaLock_Click(object sender, RoutedEventArgs e) => ToggleLock("koma");

    private void Unify_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        S.Mutate(p => p.Unify = CkUnify.IsChecked == true, "统一感", look: true);
    }

    private void Typeset_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        S.Mutate(p => p.Typeset = CkTypeset.IsChecked == true, "文字排列", look: true);
    }

    private void Flash_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        S.Mutate(p => p.Fx.Flash = CkFlash.IsChecked == true, "闪光");
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
        }, "抽帧：" + k);
    }

    private void CbHud_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || CbHud.SelectedItem is not ComboBoxItem it) return;
        var v = (string)it.Tag!;
        if (v == (S.Project.Fx.Hud ?? "auto")) return;
        S.Mutate(p => p.Fx.Hud = v, "HUD");
    }

    private void HideDeco_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        S.Mutate(p =>
        {
            p.Fx.HideNo = CkHideNo.IsChecked == true;
            p.Fx.HideTime = CkHideTime.IsChecked == true;
        }, "装饰编号/时刻显示");
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
        S.Mutate(p => p.Seed = seed, "种子");
    }

    private void BtnNewSeed_Click(object sender, RoutedEventArgs e)
        => S.Mutate(p => p.Seed = (uint)(Random.Shared.NextDouble() * 1e9), "新种子"); // (Math.random()*1e9)|0

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
        S.Mutate(p => p.AudioFx = cur, "音频特效：" + label + (on ? " 开" : " 关"), replan: false);
    }

    private void CbAfxPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || CbAfxPreset.SelectedItem is not ComboBoxItem { Tag: string id } || id.Length == 0) return;
        var preset = AudioFx.Presets.FirstOrDefault(x => x.Id == id);
        if (preset == null) return;
        S.Mutate(p => p.AudioFx = AudioFx.SanitizeAudioFx((JObject)preset.Cfg.DeepClone()),
            "音频特效预设：" + preset.Label, replan: false);
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
            CbAfxPreset.Items.Add(new ComboBoxItem { Content = "— 选择预设 —", Tag = "" });
            foreach (var pr in AudioFx.Presets)
                CbAfxPreset.Items.Add(new ComboBoxItem
                {
                    Content = pr.Label,
                    Tag = pr.Id,
                    ToolTip = "套用预设：" + pr.Label,
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
