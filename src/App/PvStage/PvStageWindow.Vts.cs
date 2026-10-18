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
    private double _vtsExprDuration = 2.0;
    private readonly List<(double Time, string Target)> _pendingExprOff = new();

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

        AddInspector(AnimParamRow(_inspectorRows, VtsXAddr, "位移X", -2, 2, 0.01,
            () => _vtsRestX, v => _vtsRestX = v));
        AddInspector(AnimParamRow(_inspectorRows, VtsYAddr, "位移Y", -2, 2, 0.01,
            () => _vtsRestY, v => _vtsRestY = v));
        AddInspector(AnimParamRow(_inspectorRows, VtsSizeAddr, "缩放Z%", -80, 80, 1,
            () => _vtsRestSize, v => _vtsRestSize = v));

        AddInspector(ConstRow("自动强度", 0.1, 1.5, 0.05,
            () => _vtsAutoIntensity, v => _vtsAutoIntensity = v));

        var auto = new Wpf.Ui.Controls.Button
        {
            Content = "按音频生成移动曲线",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 6, 0, 0),
        };
        auto.Click += (_, _) => GenerateVtsMoveFromAudio();
        AddInspector(auto);

        AddInspector(ThemeText("动作关键帧", TextSecondaryKey, 12));
        var hotCombo = new ComboBox { FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var hotkey in _vts.Hotkeys)
            hotCombo.Items.Add(new ComboBoxItem { Content = hotkey.Name, Tag = hotkey.HotkeyId });
        if (hotCombo.Items.Count == 0)
            hotCombo.Items.Add(new ComboBoxItem { Content = "未读取到动作键", IsEnabled = false });
        hotCombo.SelectedIndex = 0;
        AddInspector(hotCombo);

        var hotAdd = new Wpf.Ui.Controls.Button
        {
            Content = "在播放头打动作键",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 0),
        };
        hotAdd.Click += (_, _) =>
        {
            if (hotCombo.SelectedItem is ComboBoxItem { Tag: string id }) AddVtsCue(VtsCueKind.Hotkey, id);
            else TblStatus.Text = "先连上 VTS 读取动作键";
        };
        AddInspector(hotAdd);

        AddInspector(ThemeText("表情关键帧", TextSecondaryKey, 12));
        AddInspector(ConstRow("表情持续", 0.3, 10, 0.1,
            () => _vtsExprDuration, v => _vtsExprDuration = v));
        var exprCombo = new ComboBox { FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var file in _vts.Expressions)
            exprCombo.Items.Add(new ComboBoxItem
            {
                Content = Path.GetFileNameWithoutExtension(file),
                Tag = file,
            });
        if (exprCombo.Items.Count == 0)
            exprCombo.Items.Add(new ComboBoxItem { Content = "未读取到表情", IsEnabled = false });
        exprCombo.SelectedIndex = 0;
        AddInspector(exprCombo);

        var exprAdd = new Wpf.Ui.Controls.Button
        {
            Content = "在播放头打表情",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 0),
        };
        exprAdd.Click += (_, _) =>
        {
            if (exprCombo.SelectedItem is ComboBoxItem { Tag: string file }) AddVtsCue(VtsCueKind.Expression, file);
            else TblStatus.Text = "先连上 VTS 读取表情";
        };
        AddInspector(exprAdd);

        AddInspector(ThemeText(
            "点每行的 ◆K 在播放头打关键帧；关键帧会出现在时间线自动化轨，可直接拖贝塞尔手柄。",
            TextTertiaryKey, 11, wrap: true));
    }

    private void AddVtsCue(VtsCueKind kind, string target)
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
            Target = target,
            Duration = kind == VtsCueKind.Expression ? Math.Max(0.2, _vtsExprDuration) : 0,
        });
        _project.VtsCues.Cues.Sort((a, b) => a.Time.CompareTo(b.Time));

        PushUndo();
        RefreshTimeline();
        _saveError = SaveProjectNow();
        TblStatus.Text = $"已在 {t:F2}s 打点：{(kind == VtsCueKind.Hotkey ? "动作键" : "表情")}";
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
            foreach (var off in _pendingExprOff) _vts.SetExpression(off.Target, false, 0.15);
            _pendingExprOff.Clear();
            _cueTime = t;
            return;
        }

        foreach (var cue in cues)
        {
            if (cue is null || cue.Time <= _cueTime || cue.Time > t) continue;
            if (cue.Kind == VtsCueKind.Hotkey)
            {
                if (!string.IsNullOrWhiteSpace(cue.Target)) _vts.TriggerHotkey(cue.Target);
            }
            else if (cue.Kind == VtsCueKind.Expression && !string.IsNullOrWhiteSpace(cue.Target))
            {
                _vts.SetExpression(cue.Target, true, 0.15);
                if (cue.Duration > 0.01) _pendingExprOff.Add((cue.Time + cue.Duration, cue.Target));
            }
        }
        _cueTime = t;

        for (int i = _pendingExprOff.Count - 1; i >= 0; i--)
        {
            if (_pendingExprOff[i].Time > t) continue;
            _vts.SetExpression(_pendingExprOff[i].Target, false, 0.15);
            _pendingExprOff.RemoveAt(i);
        }
    }

    private double _vtsAutoIntensity = 0.6;

    private void GenerateVtsMoveFromAudio()
    {
        var map = _beatMap;
        double duration = _audio.Duration;
        if (map is null || map.Beats.Count == 0 || duration <= 0.2)
        {
            TblStatus.Text = "自动生成角色移动：先导入音频";
            return;
        }

        double Energy(double t)
        {
            foreach (var section in map.Sections)
                if (t >= section.Start && t < section.End) return section.Energy;
            return 0.5;
        }

        double k = Math.Clamp(_vtsAutoIntensity, 0.1, 1.5);
        double scale = 0.6 + 0.7 * k;
        double beat = map.Bpm > 0 ? 60.0 / map.Bpm : 0.5;
        double phrase = Math.Max(1.0, beat * 8);
        var anchors = new List<double>();
        for (double t = phrase; t < duration - 0.1; t += phrase) anchors.Add(t);

        var rng = new Random(_project.LegacySeed);
        double Vary() => 0.85 + rng.NextDouble() * 0.3;

        var x = new FloatCurve();
        x.TryAdd(0, 0, CurveInterp.Bezier);
        int sign = 1;
        foreach (var t in anchors)
        {
            double amp = (0.08 + 0.12 * Math.Clamp(Energy(t), 0, 1)) * scale * Vary();
            x.TryAdd(t, sign * amp, CurveInterp.Bezier);
            sign = -sign;
        }
        x.TryAdd(duration, 0, CurveInterp.Bezier);

        var y = new FloatCurve();
        y.TryAdd(0, 0, CurveInterp.Bezier);
        int ySign = 1;
        foreach (var t in anchors)
        {
            double amp = (0.012 + 0.022 * Math.Clamp(Energy(t), 0, 1)) * scale * Vary();
            y.TryAdd(t, ySign * amp, CurveInterp.Bezier);
            ySign = -ySign;
        }
        y.TryAdd(duration, 0, CurveInterp.Bezier);

        var size = new FloatCurve();
        size.TryAdd(0, 0, CurveInterp.Bezier);
        foreach (var section in map.Sections)
        {
            if (section.Start <= 0.08 || section.Start >= duration - 0.08) continue;
            double v = Math.Clamp((section.Energy - 0.5) * 30, -12, 22) * scale;
            if (Math.Abs(v) < 0.6) continue;
            size.TryAdd(section.Start, v, CurveInterp.Bezier);
        }
        size.TryAdd(duration, 0, CurveInterp.Bezier);

        PushUndo();
        _evaluator.SetCurve(VtsXAddr, x, "auto");
        _evaluator.SetCurve(VtsYAddr, y, "auto");
        _evaluator.SetCurve(VtsSizeAddr, size, "auto");
        PublishDirector();
        RefreshTimeline();
        RefreshParamRows();
        _saveError = SaveProjectNow();
        TblStatus.Text = $"已生成角色移动：X {x.Count} 帧 · Y {y.Count} 帧 · Z {size.Count} 帧";
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
        var sizeDrive = baseSize * Math.Clamp(size, -80, 80) / 100.0;

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
