using System.IO;
using System.Windows;
using System.Windows.Controls;
using JizuraLive.Pv;
using JizuraLive.Vts;

namespace JizuraLive.App.PvStage;

public partial class PvStageWindow
{
    private readonly VtsApi _vts = new();
    private double _vtsRestX;
    private double _vtsRestY;
    private double _vtsRestSize;
    private (double X, double Y, double Size) _vtsSent = (double.NaN, double.NaN, double.NaN);
    private bool _vtsWasReady;
    private double _cueTime;
    private double _vtsHotkeyDuration = 2.0;
    private double _vtsExpressionDuration = 2.0;
    private VtsCueMode _vtsHotkeyMode = VtsCueMode.Toggle;
    private VtsCueMode _vtsExpressionMode = VtsCueMode.Toggle;
    private readonly HashSet<string> _manualExprOn = new(StringComparer.Ordinal);
    private readonly List<(double Time, VtsCueKind Kind, string Target)> _pendingCueOff = new();

    private static readonly ParamAddress VtsXAddr = new(ParamAddressKind.Vts, "model", "x");
    private static readonly ParamAddress VtsYAddr = new(ParamAddressKind.Vts, "model", "y");
    private static readonly ParamAddress VtsSizeAddr = new(ParamAddressKind.Vts, "model", "size");

    private void InitVts()
    {
        _vts.TrackingEnabled = false;
        _vts.Changed += () => Dispatcher.BeginInvoke(UpdateVtsUi);
        _vts.Start();

        _vtsTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _vtsTimer.Tick += (_, _) =>
        {
            UpdateVtsMove();
            UpdateVtsCues();
        };
        _vtsTimer.Start();
    }

    private void UpdateVtsUi()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateVtsUi);
            return;
        }

        if (_project is null) return;

        var state = _vts.State;
        var text = state switch
        {
            VtsState.Ready => "已连接" + (string.IsNullOrWhiteSpace(_vts.ModelName) ? "" : " · " + _vts.ModelName),
            VtsState.AwaitApproval => "等待 VTS 弹窗确认…",
            VtsState.NeedAuth => "已连接 · 需要授权",
            _ => string.IsNullOrWhiteSpace(_vts.LastError) ? "未连接" : "未连接 · " + _vts.LastError,
        };

        TblVtsState.Text = text;
        BtnVtsAuth.IsEnabled = state is VtsState.NeedAuth;

        if (state == VtsState.Ready && !_vtsWasReady) _vtsSent = (double.NaN, double.NaN, double.NaN);
        _vtsWasReady = state == VtsState.Ready;
    }

    internal void BtnVtsReconnect_Click(object sender, RoutedEventArgs e)
    {
        _vts.Reconnect();
        UpdateVtsUi();
    }

    internal void BtnVtsAuth_Click(object sender, RoutedEventArgs e)
    {
        _vts.RequestAuth();
        UpdateVtsUi();
    }

    private void BuildCharacterMoveSection()
    {
        AddSectionTitle("角色移动");
        AddInspector(ThemeText(
            "X/Y 为 VTS 场景位移，Z 为皮套缩放；关键帧用平滑贝塞尔插值，播放时实时写入 VTS。",
            TextSecondaryKey, 11, wrap: true));

        var framing = _vts.Framing;
        AddInspector(ThemeText(
            framing is null
                ? "滑杆是相对载入时 VTS 姿态的偏移，0 = 保持基准；连上 VTS 后读取基准。"
                : $"基准：X {framing.X:F2} · Y {framing.Y:F2} · 缩放 {framing.Size:F2}；滑杆 0 = 保持基准。",
            TextTertiaryKey, 11, wrap: true));

        AddInspector(AnimParamRow(_inspectorRows, VtsXAddr, "位移X", -5, 5, 0.01,
            () => _vtsRestX, v => _vtsRestX = v));
        AddInspector(AnimParamRow(_inspectorRows, VtsYAddr, "位移Y", -5, 5, 0.01,
            () => _vtsRestY, v => _vtsRestY = v));
        AddInspector(AnimParamRow(_inspectorRows, VtsSizeAddr, "缩放Z%", -800, 500, 1,
            () => _vtsRestSize, v => _vtsRestSize = v));

        var addKeys = new Wpf.Ui.Controls.Button
        {
            Content = "在播放头添加移动关键帧",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 6, 0, 0),
        };
        addKeys.Click += (_, _) => AddVtsMoveKeysAtPlayhead();
        AddInspector(addKeys);

        var clear = new Wpf.Ui.Controls.Button
        {
            Content = "清空移动关键帧",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 10, 0, 0),
        };
        clear.Click += (_, _) => ClearVtsMoves();
        AddInspector(clear);

        AddInspector(ThemeText(
            "点每行的 ◆K 在播放头打关键帧；关键帧会出现在时间线自动化轨，可直接拖贝塞尔手柄。",
            TextTertiaryKey, 11, wrap: true));
    }

    private void BuildCharacterExpressionSection()
    {
        AddSectionTitle("角色表情");
        AddInspector(ThemeText(
            "动作键与表情共用时间线；开关到点开启、结束关闭，一次性只触发开始，手动不自动触发。",
            TextSecondaryKey, 11, wrap: true));

        AddInspector(ThemeText("动作关键帧", TextSecondaryKey, 12));
        AddInspector(ConstRow("持续", 0.3, 20, 0.1,
            () => _vtsHotkeyDuration, v => _vtsHotkeyDuration = v));
        var hotMode = VtsModeCombo(() => _vtsHotkeyMode, v => _vtsHotkeyMode = v);
        AddInspector(hotMode);
        var hotCombo = new ComboBox { FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var hotkey in _vts.Hotkeys)
            hotCombo.Items.Add(new ComboBoxItem { Content = hotkey.Name, Tag = hotkey.HotkeyId });
        if (hotCombo.Items.Count == 0)
            hotCombo.Items.Add(new ComboBoxItem { Content = "未读取到动作键", IsEnabled = false });
        hotCombo.SelectionChanged += (_, _) =>
        {
            if (hotCombo.SelectedItem is not ComboBoxItem { Tag: string id }) return;
            var hotkey = _vts.Hotkeys.FirstOrDefault(h => h.HotkeyId == id);
            if (hotkey is null) return;
            bool toggle = hotkey.Type.Contains("toggle", StringComparison.OrdinalIgnoreCase);
            _vtsHotkeyMode = toggle ? VtsCueMode.Toggle : VtsCueMode.Once;
            hotMode.SelectedIndex = toggle ? 0 : 1;
        };
        hotCombo.SelectedIndex = 0;
        AddInspector(hotCombo);

        var hotAdd = new Wpf.Ui.Controls.Button
        {
            Content = _vtsHotkeyMode == VtsCueMode.Manual ? "手动触发动作" : "在播放头开启动作",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 0),
        };
        hotAdd.Click += (_, _) =>
        {
            if (hotCombo.SelectedItem is not ComboBoxItem { Tag: string id })
            {
                TblStatus.Text = "先连上 VTS 读取动作键";
                return;
            }

            if (_vtsHotkeyMode == VtsCueMode.Manual)
            {
                _vts.TriggerHotkey(id);
                TblStatus.Text = "已手动触发动作键";
            }
            else AddVtsCue(VtsCueKind.Hotkey, id, _vtsHotkeyMode);
        };
        hotMode.SelectionChanged += (_, _) =>
            hotAdd.Content = _vtsHotkeyMode == VtsCueMode.Manual ? "手动触发动作" : "在播放头开启动作";
        AddInspector(hotAdd);

        AddInspector(ThemeText("表情关键帧", TextSecondaryKey, 12));
        AddInspector(ConstRow("持续", 0.3, 20, 0.1,
            () => _vtsExpressionDuration, v => _vtsExpressionDuration = v));
        var exprMode = VtsModeCombo(() => _vtsExpressionMode, v => _vtsExpressionMode = v);
        AddInspector(exprMode);

        var combo = new ComboBox { FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var file in _vts.Expressions)
            combo.Items.Add(new ComboBoxItem
            {
                Content = Path.GetFileNameWithoutExtension(file),
                Tag = file,
            });
        if (combo.Items.Count == 0)
            combo.Items.Add(new ComboBoxItem { Content = "未读取到表情", IsEnabled = false });
        combo.SelectedIndex = 0;
        AddInspector(combo);

        var add = new Wpf.Ui.Controls.Button
        {
            Content = _vtsExpressionMode == VtsCueMode.Manual ? "手动开关表情" : "在播放头开启表情",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 0),
        };
        add.Click += (_, _) =>
        {
            if (combo.SelectedItem is not ComboBoxItem { Tag: string file })
            {
                TblStatus.Text = "先连上 VTS 读取表情";
                return;
            }

            if (_vtsExpressionMode == VtsCueMode.Manual)
            {
                if (_manualExprOn.Remove(file))
                {
                    _vts.SetExpression(file, false, 0.1);
                    TblStatus.Text = "已手动关闭表情";
                }
                else
                {
                    _vts.SetExpression(file, true, 0.1);
                    _manualExprOn.Add(file);
                    TblStatus.Text = "已手动开启表情";
                }
            }
            else AddVtsCue(VtsCueKind.Expression, file, _vtsExpressionMode);
        };
        exprMode.SelectionChanged += (_, _) =>
            add.Content = _vtsExpressionMode == VtsCueMode.Manual ? "手动开关表情" : "在播放头开启表情";
        AddInspector(add);

        AddInspector(ThemeText(
            "时间线上的开关条可拖动位置，拖左右边缘改开始/结束；一次性关键帧只可拖动时间。",
            TextTertiaryKey, 11, wrap: true));
    }

    private void ClearVtsMoves()
    {
        bool removed = _evaluator.RemoveCurve(VtsXAddr);
        removed |= _evaluator.RemoveCurve(VtsYAddr);
        removed |= _evaluator.RemoveCurve(VtsSizeAddr);
        if (!removed)
        {
            TblStatus.Text = "角色移动没有关键帧";
            return;
        }

        ResetVtsMove();
        PushUndo();
        PublishDirector();
        RefreshTimeline();
        RefreshParamRows();
        _saveError = SaveProjectNow();
        TblStatus.Text = "已清空角色移动关键帧";
    }

    private ComboBox VtsModeCombo(Func<VtsCueMode> read, Action<VtsCueMode> write)
    {
        var combo = new ComboBox { FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };
        combo.Items.Add(new ComboBoxItem { Content = "开关（开始开，结束关）", Tag = VtsCueMode.Toggle });
        combo.Items.Add(new ComboBoxItem { Content = "一次性（只触发开始）", Tag = VtsCueMode.Once });
        combo.Items.Add(new ComboBoxItem { Content = "手动（不自动触发）", Tag = VtsCueMode.Manual });
        combo.SelectedIndex = read() switch
        {
            VtsCueMode.Once => 1,
            VtsCueMode.Manual => 2,
            _ => 0,
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: VtsCueMode mode }) write(mode);
        };
        return combo;
    }

    private void AddVtsCue(VtsCueKind kind, string target, VtsCueMode mode)
    {
        if (string.IsNullOrWhiteSpace(target) || _project is null) return;
        _project.VtsCues ??= new VtsCueTrack();

        double t = _audio.Time;
        _project.VtsCues.Cues.RemoveAll(c =>
            c is not null && c.Kind == kind && c.Target == target && Math.Abs(c.Time - t) < 1e-3);
        _project.VtsCues.Cues.Add(new VtsCue
        {
            Time = t,
            Kind = kind,
            Mode = mode,
            Target = target,
            Duration = Math.Max(0.2, kind == VtsCueKind.Hotkey
                ? _vtsHotkeyDuration
                : _vtsExpressionDuration),
        });
        _project.VtsCues.Cues.Sort((a, b) => a.Time.CompareTo(b.Time));

        PushUndo();
        RefreshTimeline();
        TheTimeline.RevealCues();
        _saveError = SaveProjectNow();
        TblStatus.Text = $"已在 {t:F2}s 打点：{(kind == VtsCueKind.Hotkey ? "动作键" : "表情")}"
                         + $"（{(mode == VtsCueMode.Toggle ? "开关" : "一次性")}）";
    }

    private void UpdateVtsCues()
    {
        if (_project?.VtsCues?.Cues is not { } cues) return;

        double t = _audio.Time;
        if (!_audio.IsPlaying)
        {
            _cueTime = t;
            return;
        }

        if (t < _cueTime || t - _cueTime > 0.5)
        {
            foreach (var off in _pendingCueOff) EndCue(off.Kind, off.Target);
            _pendingCueOff.Clear();
            _cueTime = t;
            return;
        }

        foreach (var cue in cues)
        {
            if (cue is null || cue.Time <= _cueTime || cue.Time > t) continue;
            if (string.IsNullOrWhiteSpace(cue.Target)) continue;
            if (cue.Mode == VtsCueMode.Manual) continue;

            if (cue.Kind == VtsCueKind.Hotkey) _vts.TriggerHotkey(cue.Target);
            else if (cue.Kind == VtsCueKind.Expression) _vts.SetExpression(cue.Target, true, 0.15);

            double duration = cue.Duration > 0.01 ? cue.Duration : 2.0;
            if (cue.Mode == VtsCueMode.Toggle)
                _pendingCueOff.Add((cue.Time + duration, cue.Kind, cue.Target));
        }
        _cueTime = t;

        for (int i = _pendingCueOff.Count - 1; i >= 0; i--)
        {
            if (_pendingCueOff[i].Time > t) continue;
            EndCue(_pendingCueOff[i].Kind, _pendingCueOff[i].Target);
            _pendingCueOff.RemoveAt(i);
        }
    }

    private void EndCue(VtsCueKind kind, string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        if (kind == VtsCueKind.Expression) _vts.SetExpression(target, false, 0.15);
        else _vts.TriggerHotkey(target);
    }

    private void AddVtsMoveKeysAtPlayhead()
    {
        double t = _audio.Time;
        AddVtsMoveKey(VtsXAddr, _vtsRestX, t);
        AddVtsMoveKey(VtsYAddr, _vtsRestY, t);
        AddVtsMoveKey(VtsSizeAddr, _vtsRestSize, t);

        PublishDirector();
        RefreshTimeline();
        RefreshParamRows();
        PushUndo();
        _saveError = SaveProjectNow();
        TblStatus.Text = $"已在 {t:F2}s 添加角色移动关键帧（X / Y / Z）";
    }

    private void AddVtsMoveKey(in ParamAddress addr, double baseValue, double time)
    {
        double value = _evaluator.TryEvaluate(addr, time, out var eval) ? eval : baseValue;
        if (!_evaluator.Curves.TryGetValue(addr, out var curve))
        {
            curve = new FloatCurve();
            _evaluator.SetCurve(addr, curve);
        }

        var mode = ModeOfKeyAt(curve, time) ?? CurveModeFor(addr);
        curve.Remove(time);
        curve.TryAdd(time, value, mode);
        curve.SmoothBezierHandles();
    }

    private void UpdateVtsMove()
    {
        if (_vts.Framing is null) return;

        var t = _audio.Time;
        var x = _evaluator.TryEvaluate(VtsXAddr, t, out var vx) ? vx : _vtsRestX;
        var y = _evaluator.TryEvaluate(VtsYAddr, t, out var vy) ? vy : _vtsRestY;
        var size = _evaluator.TryEvaluate(VtsSizeAddr, t, out var vs) ? vs : _vtsRestSize;

        if (Math.Abs(x - _vtsSent.X) < 1e-4
            && Math.Abs(y - _vtsSent.Y) < 1e-4
            && Math.Abs(size - _vtsSent.Size) < 1e-4) return;

        var baseSize = _vts.Framing?.Size ?? 0;
        var sizeDrive = baseSize * Math.Clamp(size, -800, 500) / 100.0;

        _vtsSent = (x, y, size);
        _vts.SetDriveOffset(sizeDrive, Math.Clamp(x, -1000, 1000), Math.Clamp(y, -1000, 1000), 0);
    }

    private void ResetVtsMove()
    {
        _vtsSent = (double.NaN, double.NaN, double.NaN);
        _vts.SetDriveOffset(0, 0, 0, 0);
    }

    private static CurveInterp CurveModeFor(in ParamAddress addr) =>
        addr.Kind == ParamAddressKind.Vts ? CurveInterp.Bezier : CurveInterp.SmoothStep;
}
