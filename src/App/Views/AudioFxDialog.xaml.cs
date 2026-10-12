using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using JizuraLive.App.Controls;
using JizuraLive.App.I18n;
using JizuraLive.Engine;
using Newtonsoft.Json.Linq;

namespace JizuraLive.App.Views;

/// <summary>
/// 音频效果弹窗（重写版）：预设 chips（一键写整条链）+ 变速滑块（改歌曲时长，歌词/节拍按原时刻
/// 跟随）+ 效果链 rack（开关 / 展开参数 / ↑↓ 调序 / ✕ 移除；单位与参数表来自引擎 AudioFx.Units）+
/// EQ 七段曲线（EqCurve 控件，拖点 ±12dB）+ 添加效果 + 注记。
///
/// 结构：rack 走 ItemsControl + 模板 + 轻量 VM（UnitVm / ParamVm）——结构变化重绑 VM、值变化只
/// 回填（不再有「整段重建控件 + RefreshValues 闭包列表」的手工同步）；展开态与滚动位置在重建后保留。
/// 骨架外的一切动态文案仍走 code-behind（本仓房屋风格），本地化键沿用 AudioFx.json。
///
/// 提交节奏：拖动滑块只刷显示、松手 SubmitNow；EQ 拖动中只重绘、松手 SubmitNow；
/// 点击/键盘等非拖动改动走 120ms 防抖。SubmitNow 会先把挂起的防抖编辑合并进来再提交——
/// 旧实现直接丢弃挂起编辑，快速连改会静默丢最后一次参数改动。
///
/// 数据权威在 project.audioFx（EditorState.Mutate，replan:false）；预览链由 PreviewEngine 在
/// Tick/Play 时按配置差异重建，导出侧同一条 AudioFx.Build。
/// </summary>
public partial class AudioFxDialog : Window
{
    private EditorState S => App.MainInstance!.State;
    private PreviewEngine Preview => App.MainInstance!.Preview;

    private readonly DispatcherTimer _debounce; // 非拖动改动的提交防抖
    private bool _ready;                        // OnLoaded 之后才允许提交（构造期 XAML 赋值不提交）
    private bool _syncing;                      // 程序回填（回填路径）不提交
    private bool _dragging;                     // 任一滑块拖动中（拖动只刷显示，松手才提交）
    private string _pendingLabel = "";
    private Func<JObject, JObject>? _pendingEdit;

    private string _shape = "";    // 链形状签名（id 序列）：变了才重建 VM 列表
    private int _openUnit = -1;    // 展开的单元下标（对位 openUnit）
    private readonly ObservableCollection<UnitVm> _units = new();

    public AudioFxDialog()
    {
        InitializeComponent();
        RackHost.ItemsSource = _units;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Flush();
        };
        BuildPresets();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 遮罩铺满 owner（对位 fixed inset-0）
        if (Owner != null)
        {
            Left = Owner.Left;
            Top = Owner.Top;
            Width = Owner.ActualWidth;
            Height = Owner.ActualHeight;
            Card.MaxHeight = Math.Max(200, Owner.ActualHeight - 48);
        }
        SlSpeed.TickFrequency = 0.05;
        SlSpeed.IsSnapToTickEnabled = true; // 档位 0.05（对位 step=0.05）
        S.StateChanged += OnState;
        L.LanguageChanged += OnLang;
        _ready = true;
        Refresh();
        // 打开动效：遮罩淡入 + 卡片微缩放上浮（只做进入，关闭即时）
        JzAnim.PlayWindowIn(Overlay, Card);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Flush(); // 关窗把挂起的提交落掉，别丢最后一次拖动
        _ready = false;
        S.StateChanged -= OnState;
        L.LanguageChanged -= OnLang;
    }

    private void OnState() => Dispatcher.Invoke(Refresh);

    /// <summary>语言切换：预设 chips、添加下拉重读文案，rack 整体重绑（摘要/EQ 文案在 VM 上）。</summary>
    private void OnLang() => Dispatcher.Invoke(() =>
    {
        BuildPresets();
        CbAdd.Tag = null; // 强制重建添加下拉项（占位文案）
        _shape = "";      // 强制重建 VM（摘要等动态文案）
        Refresh();
    });

    // ---------------- 关闭 ----------------

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>点遮罩空白处关闭（点卡片内部由 Card_MouseDown 吃掉，不会冒到这里）。</summary>
    private void Overlay_MouseDown(object sender, MouseButtonEventArgs e) => Close();

    private void Card_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Close();
        e.Handled = true;
    }

    // ---------------- VM ----------------

    /// <summary>VM 基类：回填抑制 + 属性通知（Suppress 期间 setter 只更新显示、不提交）。</summary>
    private abstract class VmBase : INotifyPropertyChanged
    {
        public bool Suppress;
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void Raise([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>效果链单元行：开关 / 名称（▸▾）/ 参数摘要 / 上移下移移除（CanUp/CanDown）。</summary>
    private sealed class UnitVm : VmBase
    {
        private readonly AudioFxDialog _d;
        public UnitVm(AudioFxDialog d) => _d = d;

        public int Index;
        public string Id = "";

        /// <summary>以下成员是绑定源：WPF 绑定不认字段，必须是属性（Params/Note 曾是字段，
        /// 展开区说明与参数行整块绑不上、渲染成空白）。</summary>
        public string Label { get; set; } = "";

        public string Note { get; set; } = "";
        public List<ParamVm> Params { get; } = new();

        private bool _on = true, _open, _canUp, _canDown;
        private string _summary = "";

        public bool IsOn
        {
            get => _on;
            set
            {
                if (_on == value) return;
                _on = value;
                Raise();
                if (!Suppress) _d.UserToggleUnit(this, value);
            }
        }

        public bool IsOpen
        {
            get => _open;
            set
            {
                if (_open == value) return;
                _open = value;
                Raise();
                Raise(nameof(HeadText));
                Raise(nameof(DetailVis));
            }
        }

        public bool CanUp { get => _canUp; set { if (_canUp == value) return; _canUp = value; Raise(); } }
        public bool CanDown { get => _canDown; set { if (_canDown == value) return; _canDown = value; Raise(); } }

        public string Summary { get => _summary; set { if (_summary == value) return; _summary = value; Raise(); } }
        public string HeadText => Label + (_open ? " ▾" : " ▸");
        public Visibility DetailVis => _open ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>参数行（数值滑块或 EQ 曲线，按 Def.Type 切模板内显隐）。</summary>
    private sealed class ParamVm : VmBase
    {
        private readonly AudioFxDialog _d;
        public readonly UnitVm Unit;
        public readonly FxParamDef Def;

        public ParamVm(AudioFxDialog d, UnitVm unit, FxParamDef def)
        {
            _d = d;
            Unit = unit;
            Def = def;
            Label = def.Label ?? def.Key;
        }

        public string Label { get; }
        public bool IsEq => Def.Type == "eq";
        public Visibility NumVis => IsEq ? Visibility.Collapsed : Visibility.Visible;
        public Visibility EqVis => IsEq ? Visibility.Visible : Visibility.Collapsed;

        public double Min => Def.Min;
        public double Max => Def.Max;
        public double Step => Def.Step;
        public double TickFreq => Def.Type == "int" ? Math.Max(1, Def.Step) : 0;
        public bool SnapTicks => Def.Type == "int";

        private double _value;
        public double Value
        {
            get => _value;
            set
            {
                if (Math.Abs(_value - value) < 1e-9) return;
                _value = value;
                Raise();
                Raise(nameof(Display));
                if (!Suppress && !Unit.Suppress) _d.UserEditParam(Unit, this);
            }
        }

        public string Display => Round2(_value) + (Def.Unit ?? "");

        private double[] _gains = new double[AudioFx.EqBands.Length];
        public double[] Gains
        {
            get => _gains;
            set
            {
                _gains = value;
                Raise();
            }
        }
    }

    // ---------------- 回填 / 重建 ----------------

    private void Refresh()
    {
        if (App.MainInstance == null) return;
        _syncing = true;
        try
        {
            var cfg = AudioFx.SanitizeAudioFx(S.Project.AudioFx);
            var units = (JArray)cfg["units"]!;
            var shape = string.Join(",", units.OfType<JObject>().Select(u => u["id"]?.ToString() ?? ""));
            if (shape != _shape)
            {
                _shape = shape;
                RebuildRack(units);
            }
            else
            {
                PushRackValues(units);
            }
            TblEmpty.Visibility = units.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RefreshAddCombo(units);
            RefreshSpeed(cfg);
            RefreshKey(cfg);
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>结构变化（增删/调序/换预设）：重绑 VM 列表；展开态与滚动位置保留。</summary>
    private void RebuildRack(JArray units)
    {
        var keepScroll = Scroller.VerticalOffset;
        _units.Clear();
        var i = 0;
        foreach (var t in units.OfType<JObject>())
        {
            var id = t["id"]?.ToString() ?? "";
            if (!AudioFx.Units.TryGetValue(id, out var def)) continue; // sanitize 已保证，不防御
            var vm = new UnitVm(this) { Index = i, Id = id, Label = def.Label, Note = def.Note, Suppress = true };
            foreach (var ps in def.Params) vm.Params.Add(new ParamVm(this, vm, ps) { Suppress = true });
            _units.Add(vm);
            i++;
        }
        if (_openUnit >= _units.Count) _openUnit = -1;
        PushRackValues(units);
        if (keepScroll > 0) // 重建后恢复滚动位置（ItemsControl 清空会把 offset 归零）
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Scroller.ScrollToVerticalOffset(keepScroll));
    }

    /// <summary>值回填（不回建）：开关态、箭头、摘要、滑块值 / EQ 增益。Suppress 期间 setter 不提交。</summary>
    private void PushRackValues(JArray units)
    {
        for (var i = 0; i < _units.Count && i < units.Count; i++)
        {
            var vm = _units[i];
            var u = (JObject)units[i]!;
            vm.Suppress = true;
            foreach (var p in vm.Params) p.Suppress = true;

            vm.Index = i;
            vm.IsOn = u["on"]?.Type != JTokenType.Boolean || (bool)u["on"]!;
            vm.CanUp = i > 0;
            vm.CanDown = i < _units.Count - 1;
            vm.IsOpen = i == _openUnit;

            var pr = (JObject)(u["params"] ?? new JObject())!;
            foreach (var p in vm.Params)
            {
                if (p.IsEq)
                {
                    var src = pr["gains"] as JArray;
                    var arr = new double[AudioFx.EqBands.Length];
                    for (var k = 0; k < arr.Length; k++)
                        if (src != null && k < src.Count) arr[k] = JNum(src[k]);
                    p.Gains = arr; // 新引用：EqCurve 的 AffectsRender 生效
                }
                else
                {
                    var v = ParamValueOrNull(pr, p.Def);
                    if (v != null) p.Value = v.Value;
                }
                p.Suppress = false;
            }
            if (AudioFx.Units.TryGetValue(vm.Id, out var def)) vm.Summary = Summary(def, u);
            vm.Suppress = false;
        }
    }

    /// <summary>参数摘要（对位 paramSummary）：`标签 值单位` 用「・」连；EQ 写 平直 / 自定义曲线。</summary>
    private static string Summary(FxUnitDef def, JObject unit)
    {
        var pr = (JObject)(unit["params"] ?? new JObject())!;
        var parts = new List<string>();
        foreach (var ps in def.Params)
        {
            if (ps.Type == "eq")
            {
                var any = (pr["gains"] as JArray)?.Any(v => Math.Abs(JNum(v)) > 0.05) == true;
                parts.Add(L.T(any ? "AudioFx.Eq.Custom" : "AudioFx.Eq.Flat"));
            }
            else
            {
                parts.Add($"{ps.Label} {Round2(ParamValue(pr, ps))}{ps.Unit}");
            }
        }
        return string.Join("・", parts);
    }

    // ---------------- rack 交互（模板按钮的 Click 都在这里落到 VM.Index） ----------------

    private void OnUnitHead(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not UnitVm vm) return;
        _openUnit = vm.IsOpen ? -1 : vm.Index; // 对位 openUnit：同一下标再点收起
        foreach (var u in _units) u.IsOpen = u.Index == _openUnit;
    }

    private void OnUnitUp(object sender, RoutedEventArgs e) => MoveUnit(((FrameworkElement)sender).DataContext as UnitVm, -1);

    private void OnUnitDown(object sender, RoutedEventArgs e) => MoveUnit(((FrameworkElement)sender).DataContext as UnitVm, 1);

    private void OnUnitRemove(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not UnitVm vm) return;
        var label = LabelOf(vm);
        var i = vm.Index;
        EditUnits(L.F("AudioFx.Mutate.Remove", label), units =>
        {
            if (i >= 0 && i < units.Count) units.RemoveAt(i);
            return units;
        });
        _openUnit = -1;
    }

    private void MoveUnit(UnitVm? vm, int delta)
    {
        if (vm == null) return;
        var i = vm.Index;
        var j = i + delta;
        if (i < 0 || j < 0 || j >= _units.Count) return;
        EditUnits(L.T("AudioFx.Mutate.Move"), units =>
        {
            if (i >= units.Count || j >= units.Count) return units;
            (units[i], units[j]) = (units[j], units[i]);
            return units;
        });
        _openUnit = -1;
    }

    private string LabelOf(UnitVm vm) => AudioFx.Units.TryGetValue(vm.Id, out var d) ? d.Label : "";

    /// <summary>参数滑块拖动落定（Loaded 时挂 Thumb 事件；WPF 的 Slider 不公开 IsDragging）。</summary>
    private void OnParamSliderLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Slider sl) return;
        AttachDrag(sl, () =>
        {
            if (!_ready || sl.DataContext is not ParamVm p) return;
            SubmitNow(L.F("AudioFx.Mutate.Param", p.Label),
                cur => SetParamOn(cur, p.Unit.Index, p.Def.Key, ParamToken(p)));
        });
    }

    /// <summary>EQ 曲线拖动落定：提交整组增益（拖动中控件自己只重绘，不提交）。
    /// Gains←Bands 的绑定在代码里挂：XAML 编译器对数组型 DP 的 {Binding} 直接报 MC4102
    /// （PropertyArrayStart），模板里写不了；Loaded 时 DataContext 已是 ParamVm。</summary>
    private void OnEqLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not EqCurve eq) return;
        if (BindingOperations.GetBinding(eq, EqCurve.BandsProperty) == null)
            eq.SetBinding(EqCurve.BandsProperty, new Binding(nameof(ParamVm.Gains)));
        eq.DragCommitted += gains =>
        {
            if (!_ready || eq.DataContext is not ParamVm p) return;
            SubmitNow(L.T("AudioFx.Mutate.Eq"), cur => SetParamOn(cur, p.Unit.Index, "gains", new JArray(gains)));
        };
    }

    private void AttachDrag(Slider sl, Action onRelease)
    {
        if (sl.Tag as string == "drag") return; // Loaded 可能多次触发（模板重应用），只挂一次
        if (sl.Template.FindName("PART_Track", sl) is not Track track) return;
        sl.Tag = "drag";
        track.Thumb.DragStarted += (_, _) => _dragging = true;
        track.Thumb.DragCompleted += (_, _) =>
        {
            _dragging = false;
            onRelease();
        };
    }

    // ---------------- 参数编辑（VM 写回 → 提交管线） ----------------

    private void UserToggleUnit(UnitVm vm, bool on)
    {
        if (_syncing || !_ready || App.MainInstance == null) return;
        var label = LabelOf(vm);
        EditUnits(L.F(on ? "AudioFx.Mutate.UnitOn" : "AudioFx.Mutate.UnitOff", label), units =>
        {
            if (vm.Index >= 0 && vm.Index < units.Count && units[vm.Index] is JObject u) u["on"] = on;
            return units;
        });
    }

    private void UserEditParam(UnitVm unit, ParamVm p)
    {
        if (_syncing || !_ready || App.MainInstance == null) return;
        if (_dragging) return; // 拖动中只刷显示，松手由 AttachDrag 提交
        SubmitDebounced(L.F("AudioFx.Mutate.Param", p.Label),
            cur => SetParamOn(cur, unit.Index, p.Def.Key, ParamToken(p)));
    }

    private static JToken ParamToken(ParamVm p)
        => p.Def.Type == "int" ? new JValue((int)Math.Round(p.Value)) : new JValue(p.Value);

    // ---------------- 变速（对位 applyFx({speed})） ----------------

    private void OnSpeedSliderLoaded(object sender, RoutedEventArgs e)
        => AttachDrag(SlSpeed, () =>
        {
            if (_ready) SubmitNow(SpeedLabel(SlSpeed.Value), SpeedEdit(SlSpeed.Value));
        });

    private void SlSpeed_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || !_ready || App.MainInstance == null) return; // 构造期与回填早退
        var speed = Math.Round(e.NewValue / 0.05) * 0.05;            // 档位对齐 0.05
        TblSpeedVal.Text = SpeedLabel(speed);
        var audio = Preview.Audio;
        if (audio != null) TblSpeedDur.Text = DurationText(audio.Duration / Math.Max(0.01, speed));
        if (_dragging) return;                                       // 拖动中只刷显示（松手由 AttachDrag 提交）
        SubmitDebounced(SpeedLabel(speed), SpeedEdit(speed));
    }

    private void RefreshSpeed(JObject cfg)
    {
        var audio = Preview.Audio;
        SpeedRow.Visibility = audio != null ? Visibility.Visible : Visibility.Collapsed;
        if (audio == null) return;
        var speed = AudioFx.FxSpeedOf(cfg);
        if (Math.Abs(SlSpeed.Value - speed) > 1e-6) SlSpeed.Value = speed;
        TblSpeedVal.Text = SpeedLabel(speed);
        TblSpeedDur.Text = DurationText(audio.Duration / Math.Max(0.01, speed));
    }

    private static string SpeedLabel(double speed) => L.F("AudioFx.Speed.ValueFmt", speed.ToString("F2"));

    private static Func<JObject, JObject> SpeedEdit(double speed) => cur =>
    {
        cur["speed"] = Math.Round(speed / 0.05) * 0.05;
        return cur;
    };

    /// <summary>变速后的歌曲时长文案（对位 fmtDur：≥60 秒写「x分y秒」）。</summary>
    private static string DurationText(double secs) => secs >= 60
        ? L.F("AudioFx.Dur.MinSecFmt", (int)(secs / 60), Math.Round(secs % 60))
        : L.F("AudioFx.Dur.SecFmt", secs.ToString("F1"));

    // ---------------- 升降调（写链里「音调」单元的 semitones；0 = 移除单元；与 rack 里同一份数据） ----------------

    private void RefreshKey(JObject cfg)
    {
        // 先刷值与滑块（无音频只是不显示这行，数据仍保持一致），再管显隐
        var semi = PitchSemitonesOf(cfg);
        if (Math.Abs(SlKey.Value - semi) > 1e-6) SlKey.Value = semi;
        TblKeyVal.Text = KeyLabel(semi);
        var audio = Preview.Audio;
        KeyRow.Visibility = audio != null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>链里「音调」单元的 semitones（无单元 = 0 = 直通）。</summary>
    private static double PitchSemitonesOf(JObject cfg)
    {
        if (cfg["units"] is JArray units)
            foreach (var t in units)
                if (t is JObject u && u["id"]?.ToString() == "pitch")
                    return Math.Round(JNum(u["params"]?["semitones"]));
        return 0;
    }

    private static string KeyLabel(double semi)
        => L.F("AudioFx.Key.ValueFmt", ((int)Math.Round(semi)).ToString("+0;-0;0"));

    /// <summary>写 pitch 单元：n=0 移除（回直通、链保持干净）；存在则改 semitones、不存在则追加默认单元。</summary>
    private static JObject SetPitch(JObject cfg, int semitones)
    {
        if (cfg["units"] is not JArray units) return cfg;
        JObject? hit = null;
        foreach (var t in units)
            if (t is JObject u && u["id"]?.ToString() == "pitch")
            {
                hit = u;
                break;
            }
        if (semitones == 0)
        {
            if (hit != null) units.Remove(hit);
            return cfg;
        }
        if (hit == null)
        {
            units.Add(new JObject
            {
                ["id"] = "pitch", ["on"] = true, ["params"] = new JObject { ["semitones"] = semitones }
            });
        }
        else
        {
            if (hit["params"] is not JObject pr)
            {
                pr = new JObject();
                hit["params"] = pr;
            }
            pr["semitones"] = semitones;
        }
        return cfg;
    }

    private void SlKey_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || !_ready || App.MainInstance == null) return; // 构造期与回填早退
        var n = (int)Math.Round(e.NewValue);
        TblKeyVal.Text = KeyLabel(n);
        if (_dragging) return; // 拖动中只刷显示（松手由 AttachDrag 提交）
        SubmitDebounced(L.F("AudioFx.Mutate.Key", KeyLabel(n)), cur => SetPitch(cur, n));
    }

    private void OnKeySliderLoaded(object sender, RoutedEventArgs e)
        => AttachDrag(SlKey, () =>
        {
            if (_ready)
                SubmitNow(L.F("AudioFx.Mutate.Key", KeyLabel(SlKey.Value)),
                    cur => SetPitch(cur, (int)Math.Round(SlKey.Value)));
        });

    // ---------------- 预设（对位 usePreset） ----------------

    private void BuildPresets()
    {
        PresetHost.Children.Clear();
        foreach (var preset in AudioFx.Presets)
        {
            var b = new Button
            {
                Style = (Style)FindResource("BtnGhostSm"),
                Content = preset.Label,
                Margin = new Thickness(0, 0, 4, 4),
            };
            var p = preset;
            b.Click += (_, _) =>
            {
                _openUnit = -1;
                var cfg = AudioFx.SanitizeAudioFx((JObject)p.Cfg.DeepClone());
                S.Mutate(pp => pp.AudioFx = cfg, L.F("AudioFx.Mutate.Preset", p.Label), replan: false);
            };
            ToolTipService.SetToolTip(b, L.F("AudioFx.Preset.TipFmt", p.Label));
            PresetHost.Children.Add(b);
        }
    }

    // ---------------- 添加效果（对位 addable / addUnit） ----------------

    private void RefreshAddCombo(JArray units)
    {
        var inChain = units.OfType<JObject>().Select(u => u["id"]?.ToString() ?? "").ToHashSet();
        var addable = AudioFx.Units.Values.Where(d => !inChain.Contains(d.Id)).ToList();
        var sig = string.Join(",", addable.Select(d => d.Id));
        if (CbAdd.Tag as string != sig)
        {
            CbAdd.Tag = sig;
            CbAdd.Items.Clear();
            CbAdd.Items.Add(new ComboBoxItem { Content = L.T("AudioFx.AddPlaceholder"), Tag = "" });
            foreach (var d in addable) CbAdd.Items.Add(new ComboBoxItem { Content = d.Label, Tag = d.Id });
            CbAdd.SelectedIndex = 0;
        }
        BtnAdd.IsEnabled = SelectedAddId().Length > 0;
    }

    private string SelectedAddId() => (CbAdd.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private void CbAdd_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        BtnAdd.IsEnabled = SelectedAddId().Length > 0;
    }

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        var id = SelectedAddId();
        if (id.Length == 0 || !AudioFx.Units.TryGetValue(id, out var def)) return;
        var label = def.Label;
        var defaults = def.Defaults.DeepClone();
        EditUnits(L.F("AudioFx.Mutate.Add", label), units =>
        {
            units.Add(new JObject { ["id"] = id, ["on"] = true, ["params"] = defaults });
            return units;
        });
        _openUnit = _units.Count; // 新单元落在末尾并自动展开（对位 addUnit 的 openUnit = length-1）
    }

    // ---------------- 提交（防抖 / 立即） ----------------

    private void EditUnits(string label, Func<JArray, JArray> edit)
        => SubmitNow(label, cur =>
        {
            cur["units"] = edit((JArray)cur["units"]!);
            return cur;
        });

    /// <summary>立即提交（结构操作与松手落定走这条）。挂起的防抖编辑先合并进来再提交——
    /// 旧实现直接丢弃挂起编辑，快速连改（改参数后立刻调序/移除）会静默丢最后一次参数改动。</summary>
    private void SubmitNow(string label, Func<JObject, JObject> edit)
    {
        _debounce.Stop();
        var pending = _pendingEdit;
        _pendingEdit = null;
        var cur = AudioFx.SanitizeAudioFx(S.Project.AudioFx);
        if (pending != null) cur = pending(cur); // 挂起的是更早的编辑：先落它，再落新编辑
        var next = AudioFx.SanitizeAudioFx(edit(cur));
        S.Mutate(p => p.AudioFx = next, label, replan: false);
    }

    /// <summary>防抖提交（点击/键盘/EQ 之外的连续改动合成一次写）。</summary>
    private void SubmitDebounced(string label, Func<JObject, JObject> edit)
    {
        _pendingLabel = label;
        _pendingEdit = edit;
        _debounce.Stop();
        _debounce.Start();
    }

    private void Flush()
    {
        var edit = _pendingEdit;
        _pendingEdit = null;
        if (edit == null) return;
        var next = AudioFx.SanitizeAudioFx(edit(AudioFx.SanitizeAudioFx(S.Project.AudioFx)));
        S.Mutate(p => p.AudioFx = next, _pendingLabel, replan: false);
    }

    private static JObject SetParamOn(JObject cfg, int index, string key, JToken value)
    {
        if (cfg["units"] is not JArray units || index < 0 || index >= units.Count) return cfg;
        if (units[index] is not JObject u) return cfg;
        var pr = (JObject)(u["params"] ?? new JObject())!;
        pr[key] = value;
        u["params"] = pr;
        return cfg;
    }

    // ---------------- 小助手 ----------------

    private static double ParamValue(JObject pr, FxParamDef ps) => ParamValueOrNull(pr, ps) ?? ps.Def;

    private static double? ParamValueOrNull(JObject pr, FxParamDef ps)
        => pr[ps.Key] is JValue { Type: JTokenType.Float or JTokenType.Integer } v ? Convert.ToDouble(v.Value) : null;

    /// <summary>JSON 数值取 double（非数值回 0）——JToken.Value 是泛型方法，不能直接喂给 Convert。</summary>
    private static double JNum(JToken? tok)
        => tok is JValue { Type: JTokenType.Float or JTokenType.Integer } v ? Convert.ToDouble(v.Value) : 0;

    private static string Round2(double v)
        => Math.Abs(v) >= 100 ? Math.Round(v).ToString("0") : Math.Round(v, 2).ToString("0.##");
}
