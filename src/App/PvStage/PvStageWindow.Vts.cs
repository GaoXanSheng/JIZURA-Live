using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using JizuraLive.Logging;
using JizuraLive.Pv;
using JizuraLive.Vts;
using JizuraLive.App.I18n;

namespace JizuraLive.App.PvStage;

public partial class PvStageWindow
{
    private readonly VtsApi _vts = new();
    private double _vtsRestX;
    private double _vtsRestY;
    private double _vtsRestSize;
    private double _vtsRestRot;
    private (double X, double Y, double Size, double Rotation) _vtsSent =
        (double.NaN, double.NaN, double.NaN, double.NaN);
    private bool _vtsWasReady;
    private double _cueTime;
    private double _vtsHotkeyDuration = 2.0;
    private double _vtsExpressionDuration = 2.0;
    private VtsCueMode _vtsHotkeyMode = VtsCueMode.Toggle;
    private VtsCueMode _vtsExpressionMode = VtsCueMode.Toggle;
    private double _vtsTintDuration = 2.0;
    private VtsCueMode _vtsTintMode = VtsCueMode.Toggle;
    private string _vtsTintScope = "all";
    private string _vtsTintTarget = "all";
    private string _vtsTintColor = "#FF6B6B";
    private ComboBox? _vtsTintTargetCombo;
    private ComboBox? _vtsHotkeyCombo;
    private ComboBox? _vtsExprCombo;
    private TextBlock? _vtsTintHint;
    private string _vtsPickSignature = "";
    private int _vtsTintTargetCount = -1;
    private bool _stageVtsDrive = true;
    private bool _vtsTintOnTransitions;
    private Wpf.Ui.Controls.ToggleSwitch? _vtsTintTransitionToggle;
    private TextBlock? _vtsFramingText;
    private bool _vtsBaseSynced;
    private bool _vtsSaveBasePending;
    private readonly HashSet<string> _manualExprOn = new(StringComparer.Ordinal);
    private readonly List<(double Time, VtsCueKind Kind, string Target)> _pendingCueOff = new();

    private static readonly ParamAddress VtsXAddr = new(ParamAddressKind.Vts, "model", "x");
    private static readonly ParamAddress VtsYAddr = new(ParamAddressKind.Vts, "model", "y");
    private static readonly ParamAddress VtsSizeAddr = new(ParamAddressKind.Vts, "model", "size");
    private static readonly ParamAddress VtsRotationAddr = new(ParamAddressKind.Vts, "model", "rotation");

    private void InitVts()
    {
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
            VtsState.Ready => L.T("StageUi.Vts.Connected") + (string.IsNullOrWhiteSpace(_vts.ModelName) ? "" : " · " + _vts.ModelName),
            VtsState.AwaitApproval => L.T("StageUi.Vts.AwaitingApproval"),
            VtsState.NeedAuth => L.T("StageUi.Vts.ConnectedNeedsAuth"),
            _ => string.IsNullOrWhiteSpace(_vts.LastError) ? L.T("StageUi.Vts.Disconnected") : L.T("StageUi.Vts.DisconnectedDetail") + _vts.LastError,
        };

        TblVtsState.Text = text;
        BtnVtsAuth.IsEnabled = state is VtsState.NeedAuth;
        if (_vtsFramingText is not null) _vtsFramingText.Text = VtsFramingLabel();

        if (state == VtsState.Ready && !_vtsWasReady)
        {
            _vtsSent = (double.NaN, double.NaN, double.NaN, double.NaN);
            _vtsBaseSynced = false;
        }

        if (_vtsSaveBasePending && state == VtsState.Ready && _vts.Framing is { } calibrated)
        {
            SaveVtsBase(calibrated);
            _vtsSaveBasePending = false;
            _vtsBaseSynced = true;
        }

        if (state == VtsState.Ready && !_vtsBaseSynced) SyncVtsBaseFromProject();
        else if (state != VtsState.Ready) _vtsBaseSynced = false;

        RefreshTintTargetsIfNeeded();
        RefreshVtsPickListsIfNeeded();

        _vtsWasReady = state == VtsState.Ready;
    }

    private void SyncVtsBaseFromProject()
    {
        if (_project is null || _vts.State != VtsState.Ready) return;

        if (_project.VtsBase is { } saved)
        {
            _vts.ApplyBase(new VtsFraming(saved.Size, saved.X, saved.Y, saved.Rotation));
            _vtsSent = (double.NaN, double.NaN, double.NaN, double.NaN);
            _vtsBaseSynced = true;
            return;
        }

        if (_vts.Framing is { } framing)
        {
            SaveVtsBase(framing);
            _vtsBaseSynced = true;
        }
    }

    private void SaveVtsBase(VtsFraming framing)
    {
        if (_project is null) return;

        _project.VtsBase = new VtsBaseSave
        {
            X = framing.X,
            Y = framing.Y,
            Size = framing.Size,
            Rotation = framing.Rotation,
        };
        _saveError = SaveProjectNow();
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
        AddSectionTitle(L.T("StageUi.Vts.CharacterMove"));
        AddInspector(ThemeText(
            L.T("StageUi.Vts.CharacterMoveHelp"),
            TextSecondaryKey, 11, wrap: true));

        _vtsFramingText = ThemeText(VtsFramingLabel(), TextTertiaryKey, 11, wrap: true);
        AddInspector(_vtsFramingText);

        var calibrate = new Wpf.Ui.Controls.Button
        {
            Content = L.T("StageUi.Vts.CalibrateInitialCoordinates"),
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 6, 0, 0),
        };
        calibrate.Click += (_, _) => CalibrateVtsBase();
        AddInspector(calibrate);

        var zeroOffset = new Wpf.Ui.Controls.Button
        {
            Content = L.T("StageUi.Vts.ZeroOffsets"),
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 6, 0, 0),
        };
        zeroOffset.Click += (_, _) => ZeroVtsOffsets();
        AddInspector(zeroOffset);

        _stageVtsDrive = _project.EditorLayout?.VtsStageDrive ?? _stageVtsDrive;
        AddInspector(BoolRow(L.T("StageUi.Vts.StageHandlesDriveVts"), _stageVtsDrive,
            v =>
            {
                _stageVtsDrive = v;
                if (_project.EditorLayout is { } layout) layout.VtsStageDrive = v;
                _saveError = SaveProjectNow();
            }));
        AddInspector(ThemeText(
            L.T("StageUi.Vts.VtsModeHelp"),
            TextTertiaryKey, 11, wrap: true));

        AddInspector(AnimParamRow(_inspectorRows, VtsXAddr, L.T("StageUi.Vts.OffsetX"), -1000, 1000, 0.01,
            () => _vtsRestX, v => _vtsRestX = v, sliderWindow: 0.2));
        AddInspector(AnimParamRow(_inspectorRows, VtsYAddr, L.T("StageUi.Vts.OffsetY"), -1000, 1000, 0.01,
            () => _vtsRestY, v => _vtsRestY = v, sliderWindow: 0.2));
        AddInspector(AnimParamRow(_inspectorRows, VtsSizeAddr, L.T("StageUi.Vts.ScaleZPercent"), -90, 300, 1,
            () => _vtsRestSize, v => _vtsRestSize = v));
        AddInspector(AnimParamRow(_inspectorRows, VtsRotationAddr, L.T("StageUi.Vts.Rotation"), -180, 180, 1,
            () => _vtsRestRot, v => _vtsRestRot = v));

        var addKeys = new Wpf.Ui.Controls.Button
        {
            Content = L.T("StageUi.Vts.AddMoveKeysAtPlayhead"),
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 6, 0, 0),
        };
        addKeys.Click += (_, _) => AddVtsMoveKeysAtPlayhead();
        AddInspector(addKeys);

        var clear = new Wpf.Ui.Controls.Button
        {
            Content = L.T("StageUi.Vts.ClearMoveKeys"),
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 10, 0, 0),
        };
        clear.Click += (_, _) => ClearVtsMoves();
        AddInspector(clear);

        AddInspector(ThemeText(
            L.T("StageUi.Vts.MoveKeyframeHelp"),
            TextTertiaryKey, 11, wrap: true));
    }

    private void BuildCharacterExpressionSection()
    {
        AddSectionTitle(L.T("StageUi.Vts.CharacterExpression"));
        AddInspector(ThemeText(
            L.T("StageUi.Vts.ExpressionTimelineHelp"),
            TextSecondaryKey, 11, wrap: true));

        AddInspector(ThemeText(L.T("StageUi.Vts.HotkeyKeyframes"), TextSecondaryKey, 12));
        AddInspector(ConstRow(L.T("StageUi.Vts.Duration"), 0.3, 20, 0.1,
            () => _vtsHotkeyDuration, v => _vtsHotkeyDuration = v));
        var hotMode = VtsModeCombo(() => _vtsHotkeyMode, v => _vtsHotkeyMode = v);
        AddInspector(hotMode);
        var hotCombo = new ComboBox { FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };
        _vtsHotkeyCombo = hotCombo;
        hotCombo.SelectionChanged += (_, _) =>
        {
            if (hotCombo.SelectedItem is not ComboBoxItem { Tag: string id }) return;
            var hotkey = _vts.Hotkeys.FirstOrDefault(h => h.HotkeyId == id);
            if (hotkey is null) return;
            bool toggle = hotkey.Type.Contains("toggle", StringComparison.OrdinalIgnoreCase);
            _vtsHotkeyMode = toggle ? VtsCueMode.Toggle : VtsCueMode.Once;
            hotMode.SelectedIndex = toggle ? 0 : 1;
        };
        FillVtsHotkeyCombo(hotCombo);
        AddInspector(hotCombo);

        var hotAdd = new Wpf.Ui.Controls.Button
        {
            Content = _vtsHotkeyMode == VtsCueMode.Manual ? L.T("StageUi.Vts.TriggerHotkeyManually") : L.T("StageUi.Vts.AddHotkeyAtPlayhead"),
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 0),
        };
        hotAdd.Click += (_, _) =>
        {
            if (hotCombo.SelectedItem is not ComboBoxItem { Tag: string id })
            {
                TblStatus.Text = L.T("StageUi.Vts.ConnectToReadHotkeys");
                return;
            }

            if (_vtsHotkeyMode == VtsCueMode.Manual)
            {
                _vts.TriggerHotkey(id);
                TblStatus.Text = L.T("StageUi.Vts.HotkeyTriggeredManually");
            }
            else AddVtsCue(VtsCueKind.Hotkey, id, _vtsHotkeyMode);
        };
        hotMode.SelectionChanged += (_, _) =>
            hotAdd.Content = _vtsHotkeyMode == VtsCueMode.Manual ? L.T("StageUi.Vts.TriggerHotkeyManually") : L.T("StageUi.Vts.AddHotkeyAtPlayhead");
        AddInspector(hotAdd);

        AddInspector(ThemeText(L.T("StageUi.Vts.ExpressionKeyframes"), TextSecondaryKey, 12));
        AddInspector(ConstRow(L.T("StageUi.Vts.Duration"), 0.3, 20, 0.1,
            () => _vtsExpressionDuration, v => _vtsExpressionDuration = v));
        var exprMode = VtsModeCombo(() => _vtsExpressionMode, v => _vtsExpressionMode = v);
        AddInspector(exprMode);

        var combo = new ComboBox { FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };
        _vtsExprCombo = combo;
        FillVtsExprCombo(combo);
        AddInspector(combo);

        var add = new Wpf.Ui.Controls.Button
        {
            Content = _vtsExpressionMode == VtsCueMode.Manual ? L.T("StageUi.Vts.ToggleExpressionManually") : L.T("StageUi.Vts.AddExpressionAtPlayhead"),
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 0),
        };
        add.Click += (_, _) =>
        {
            if (combo.SelectedItem is not ComboBoxItem { Tag: string file })
            {
                TblStatus.Text = L.T("StageUi.Vts.ConnectToReadExpressions");
                return;
            }

            if (_vtsExpressionMode == VtsCueMode.Manual)
            {
                if (_manualExprOn.Remove(file))
                {
                    _vts.SetExpression(file, false, 0.1);
                    TblStatus.Text = L.T("StageUi.Vts.ExpressionTurnedOffManually");
                }
                else
                {
                    _vts.SetExpression(file, true, 0.1);
                    _manualExprOn.Add(file);
                    TblStatus.Text = L.T("StageUi.Vts.ExpressionTurnedOnManually");
                }
            }
            else AddVtsCue(VtsCueKind.Expression, file, _vtsExpressionMode);
        };
        exprMode.SelectionChanged += (_, _) =>
            add.Content = _vtsExpressionMode == VtsCueMode.Manual ? L.T("StageUi.Vts.ToggleExpressionManually") : L.T("StageUi.Vts.AddExpressionAtPlayhead");
        AddInspector(add);

        AddInspector(ThemeText(
            L.T("StageUi.Vts.CueBarHelp"),
            TextTertiaryKey, 11, wrap: true));
        _vtsPickSignature = VtsPickSignature();
    }

    private string VtsPickSignature() =>
        string.Join("\u0001", _vts.Hotkeys.Select(h => h.HotkeyId + "\u0003" + h.Name + "\u0003" + h.Type))
        + "\u0002"
        + string.Join("\u0001", _vts.Expressions);

    private void RefreshVtsPickListsIfNeeded()
    {
        if (_vtsHotkeyCombo is null && _vtsExprCombo is null) return;

        var signature = VtsPickSignature();
        if (signature == _vtsPickSignature) return;
        _vtsPickSignature = signature;

        if (_vtsHotkeyCombo is { } hotkeys) FillVtsHotkeyCombo(hotkeys);
        if (_vtsExprCombo is { } expressions) FillVtsExprCombo(expressions);
    }

    private void FillVtsHotkeyCombo(ComboBox combo)
    {
        var selected = (combo.SelectedItem as ComboBoxItem)?.Tag as string;
        combo.Items.Clear();
        foreach (var hotkey in _vts.Hotkeys)
            combo.Items.Add(new ComboBoxItem { Content = hotkey.Name, Tag = hotkey.HotkeyId });
        if (combo.Items.Count == 0)
            combo.Items.Add(new ComboBoxItem { Content = L.T("StageUi.Vts.NoHotkeysLoaded"), IsEnabled = false });

        for (int i = 0; i < combo.Items.Count; i++)
            if (combo.Items[i] is ComboBoxItem { Tag: string id } && id == selected)
            {
                combo.SelectedIndex = i;
                return;
            }

        combo.SelectedIndex = 0;
    }

    private void FillVtsExprCombo(ComboBox combo)
    {
        var selected = (combo.SelectedItem as ComboBoxItem)?.Tag as string;
        combo.Items.Clear();
        foreach (var file in _vts.Expressions)
            combo.Items.Add(new ComboBoxItem
            {
                Content = Path.GetFileNameWithoutExtension(file),
                Tag = file,
            });
        if (combo.Items.Count == 0)
            combo.Items.Add(new ComboBoxItem { Content = L.T("StageUi.Vts.NoExpressionsLoaded"), IsEnabled = false });

        for (int i = 0; i < combo.Items.Count; i++)
            if (combo.Items[i] is ComboBoxItem { Tag: string file } && file == selected)
            {
                combo.SelectedIndex = i;
                return;
            }

        combo.SelectedIndex = 0;
    }

    private void BuildCharacterTintSection()
    {
        AddSectionTitle(L.T("StageUi.Vts.CharacterTint"));
        AddInspector(ThemeText(
            L.T("StageUi.Vts.TintScopeHelp"),
            TextSecondaryKey, 11, wrap: true));

        AddInspector(ThemeText(L.T("StageUi.Vts.Scope"), TextSecondaryKey, 12));
        var scope = new ComboBox { FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };
        scope.Items.Add(new ComboBoxItem { Content = L.T("StageUi.Vts.WholeModel"), Tag = "all" });
        scope.Items.Add(new ComboBoxItem { Content = L.T("StageUi.Vts.LayerGroup"), Tag = "group" });
        scope.Items.Add(new ComboBoxItem { Content = L.T("StageUi.Vts.SingleArtMesh"), Tag = "name" });
        scope.Items.Add(new ComboBoxItem { Content = L.T("StageUi.Vts.Tag"), Tag = "tag" });
        scope.SelectedIndex = _vtsTintScope switch
        {
            "group" => 1,
            "name" => 2,
            "tag" => 3,
            _ => 0,
        };
        AddInspector(scope);

        AddInspector(ThemeText(L.T("StageUi.Vts.Target"), TextSecondaryKey, 12));
        var target = new ComboBox { FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };
        _vtsTintTargetCombo = target;
        FillTintTargets(target);
        target.DropDownOpened += (_, _) =>
        {
            _vts.RefreshArtMeshes();
            FillTintTargets(target);
        };
        target.SelectionChanged += (_, _) =>
        {
            if (target.SelectedItem is not ComboBoxItem { Tag: string value }) return;
            if (value == _vtsTintTarget) return;
            _vtsTintTarget = value;
            RefreshTransitionTintOnTargetChange();
        };
        AddInspector(target);
        _vtsTintHint = ThemeText("", TextTertiaryKey, 11, wrap: true);
        AddInspector(_vtsTintHint);

        scope.SelectionChanged += (_, _) =>
        {
            if (scope.SelectedItem is not ComboBoxItem { Tag: string value }) return;
            _vtsTintScope = value;
            _vtsTintTarget = value == "all" ? "all" : "";
            _vtsTintTargetCount = -1;
            if (value != "all") _vts.RefreshArtMeshes();
            FillTintTargets(target);
            RefreshTransitionTintOnTargetChange();
        };

        var colorRow = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        colorRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        colorRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        colorRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });

        var colorLabel = ThemeText(L.T("StageUi.Vts.Color"), TextSecondaryKey, 12);
        colorLabel.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(colorLabel, 0);
        colorRow.Children.Add(colorLabel);

        var colorBox = new TextBox
        {
            Text = _vtsTintColor,
            FontSize = 12,
            Height = 28,
            FontFamily = MonoFontFamily,
            Padding = new Thickness(6, 0, 6, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        var swatch = new Border
        {
            Width = 22,
            Height = 20,
            CornerRadius = new CornerRadius(3),
            Background = BrushFromHex(_vtsTintColor),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Cursor = Cursors.Hand,
            ToolTip = L.T("StageUi.Vts.OpenColorPicker"),
        };
        swatch.SetResourceReference(Border.BorderBrushProperty, CardStrokeKey);
        swatch.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var current = IsHexColor(colorBox.Text.Trim()) ? colorBox.Text.Trim() : _vtsTintColor;
            Dispatcher.BeginInvoke(new Action(() =>
                ColorPickerPopup.Show(swatch, current,
                    hex => colorBox.Text = hex,
                    hex => swatch.Background = BrushFromHex(hex))));
        };
        colorBox.TextChanged += (_, _) =>
        {
            var hex = colorBox.Text.Trim();
            if (!IsHexColor(hex)) return;
            _vtsTintColor = hex;
            swatch.Background = BrushFromHex(hex);
        };
        Grid.SetColumn(colorBox, 1);
        colorRow.Children.Add(colorBox);
        Grid.SetColumn(swatch, 2);
        colorRow.Children.Add(swatch);
        AddInspector(colorRow);

        AddInspector(ConstRow(L.T("StageUi.Vts.Duration"), 0.3, 20, 0.1,
            () => _vtsTintDuration, v => _vtsTintDuration = v));
        var mode = VtsModeCombo(() => _vtsTintMode, v => _vtsTintMode = v);
        AddInspector(mode);

        var add = new Wpf.Ui.Controls.Button
        {
            Content = _vtsTintMode == VtsCueMode.Manual ? L.T("StageUi.Vts.ApplyTintNow") : L.T("StageUi.Vts.AddTintAtPlayhead"),
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 0),
        };
        add.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_vtsTintTarget))
            {
                TblStatus.Text = L.T("StageUi.Vts.SelectTintTargetFirst");
                return;
            }

            var (r, g, b) = ParseTintColor(_vtsTintColor);
            if (_vtsTintMode == VtsCueMode.Manual)
            {
                _vts.SetArtMeshTint(_vtsTintTarget, r, g, b);
                TblStatus.Text = L.T("StageUi.Vts.TintApplied");
                return;
            }
            AddVtsTintCue(r, g, b);
        };
        mode.SelectionChanged += (_, _) =>
            add.Content = _vtsTintMode == VtsCueMode.Manual
                ? L.T("StageUi.Vts.ApplyTintNow")
                : L.T("StageUi.Vts.AddTintAtPlayhead");
        AddInspector(add);

        var reset = new Wpf.Ui.Controls.Button
        {
            Content = L.T("StageUi.Vts.ResetTint"),
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 6, 0, 0),
        };
        reset.Click += (_, _) => ResetVtsTint();
        AddInspector(reset);

        AddInspector(ThemeText(L.T("StageUi.Vts.AutoTint"), TextSecondaryKey, 12));
        _vtsTintOnTransitions = _project.EditorLayout?.VtsTintOnTransitions ?? _vtsTintOnTransitions;
        AddInspector(BoolRow(L.T("StageUi.Vts.TransitionTint"), _vtsTintOnTransitions,
            SetTransitionTint, toggle => _vtsTintTransitionToggle = toggle));

        var autoTint = new Wpf.Ui.Controls.Button
        {
            Content = L.T("StageUi.Vts.AutoTintFromAudio"),
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 6, 0, 0),
        };
        autoTint.Click += (_, _) => AutoTintFromAudio();
        AddInspector(autoTint);

        AddInspector(ThemeText(
            L.T("StageUi.Vts.AutoTintHelp"),
            TextTertiaryKey, 11, wrap: true));

        AddInspector(ThemeText(
            L.T("StageUi.Vts.TintKeyframeHelp"),
            TextTertiaryKey, 11, wrap: true));
    }

    private const string AutoBeatSource = "auto-beat";
    private const string AutoTransitionSource = "auto-trans";

    private void AutoTintFromAudio()
    {
        if (_project is null) return;
        if (string.IsNullOrWhiteSpace(_vtsTintTarget))
        {
            TblStatus.Text = L.T("StageUi.Vts.SelectTintTargetFirst");
            return;
        }
        if (_beatMap is null || _beatMap.Beats.Count == 0)
        {
            TblStatus.Text = L.T("StageUi.Vts.NoBeatData");
            return;
        }

        var beats = _beatMap.Downbeats.Count > 0
            ? _beatMap.Downbeats
            : _beatMap.Beats.Where((_, index) => index % 4 == 0).ToList();

        var palette = BuildTintPalette(_vtsTintColor);
        var cueMode = _vtsTintMode == VtsCueMode.Manual ? VtsCueMode.Once : _vtsTintMode;
        double audioEnd = _audio.Duration > 0.5 ? _audio.Duration : _beatMap.Beats[^1] + 1;

        _project.VtsCues ??= new VtsCueTrack();
        var cues = _project.VtsCues.Cues;

        cues.RemoveAll(cue => cue is not null
            && cue.Kind == VtsCueKind.Tint
            && cue.Target == _vtsTintTarget
            && cue.Source == AutoBeatSource);

        int added = 0;
        for (int i = 0; i < beats.Count; i++)
        {
            double time = beats[i];
            if (time < 0 || time > audioEnd) continue;

            double next = i + 1 < beats.Count ? beats[i + 1] : time + 0.5;
            double pulse = Math.Clamp((next - time) * 0.45, 0.08, 0.5);
            var (r, g, b) = palette[TintColorIndex(time, i) % palette.Count];
            cues.Add(new VtsCue
            {
                Time = time,
                Kind = VtsCueKind.Tint,
                Mode = cueMode,
                Target = _vtsTintTarget,
                Duration = pulse,
                TintR = r,
                TintG = g,
                TintB = b,
                Source = AutoBeatSource,
            });
            added++;
        }

        cues.Sort((left, right) => left.Time.CompareTo(right.Time));
        PushUndo();
        RefreshTimeline();
        _saveError = SaveProjectNow();
        TblStatus.Text = L.F("StageUi.Vts.AutoTintedAtBeats", added);
    }

    private void RefreshTransitionTintOnTargetChange()
    {
        if (!_vtsTintOnTransitions) return;

        GenerateTransitionTints();
        PushUndo();
        RefreshTimeline();
        _saveError = SaveProjectNow();
    }

    private void SetTransitionTint(bool on)
    {
        if (on == _vtsTintOnTransitions) return;

        if (on)
        {
            if (!GenerateTransitionTints())
            {
                if (_vtsTintTransitionToggle is { } toggle) toggle.IsChecked = false;
                return;
            }
        }
        else RemoveTransitionTints();

        _vtsTintOnTransitions = on;
        if (_project.EditorLayout is { } layout) layout.VtsTintOnTransitions = on;

        PushUndo();
        RefreshTimeline();
        _saveError = SaveProjectNow();
    }

    private bool GenerateTransitionTints()
    {
        if (string.IsNullOrWhiteSpace(_vtsTintTarget))
        {
            TblStatus.Text = L.T("StageUi.Vts.SelectTintTargetFirst");
            return false;
        }

        var segments = _project.DesignSegments
            .Where(segment => segment is not null
                              && segment.Track == DesignTrackKind.Trans
                              && segment.End > segment.Start + 0.01)
            .ToList();
        if (segments.Count == 0)
        {
            TblStatus.Text = L.T("StageUi.Vts.NoTransitionSegments");
            return false;
        }

        var palette = BuildTintPalette(_vtsTintColor);
        var cueMode = _vtsTintMode == VtsCueMode.Manual ? VtsCueMode.Once : _vtsTintMode;
        _project.VtsCues ??= new VtsCueTrack();
        var cues = _project.VtsCues.Cues;

        cues.RemoveAll(cue => cue is not null
            && cue.Source == AutoTransitionSource
            && cue.Target == _vtsTintTarget);

        for (int i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var (r, g, b) = palette[TintColorIndex(segment.Start, i) % palette.Count];
            cues.Add(new VtsCue
            {
                Time = segment.Start,
                Kind = VtsCueKind.Tint,
                Mode = cueMode,
                Target = _vtsTintTarget,
                Duration = Math.Max(0.1, segment.End - segment.Start),
                TintR = r,
                TintG = g,
                TintB = b,
                Source = AutoTransitionSource,
            });
        }

        cues.Sort((left, right) => left.Time.CompareTo(right.Time));
        TblStatus.Text = L.F("StageUi.Vts.TransitionTintAdded", segments.Count);
        return true;
    }

    private void RemoveTransitionTints()
    {
        var cues = _project.VtsCues?.Cues;
        if (cues is null) return;

        cues.RemoveAll(cue => cue is not null && cue.Source == AutoTransitionSource);
        TblStatus.Text = L.T("StageUi.Vts.TransitionTintDisabled");
    }

    private static double SnapVtsMove(double value)
    {
        double step = (Keyboard.Modifiers & ModifierKeys.Control) != 0 ? 1.0
            : (Keyboard.Modifiers & ModifierKeys.Alt) != 0 ? 0.1
            : 0.01;
        return Math.Round(Math.Round(value / step, MidpointRounding.AwayFromZero) * step, 6);
    }

    private string VtsFramingLabel()
    {
        var framing = _vts.Framing;
        return framing is null
            ? L.T("StageUi.Vts.SliderOffsetHelp")
            : L.F("StageUi.Vts.BaseFramingLabel",
                framing.X, framing.Y, framing.Size);
    }

    private void ZeroVtsOffsets()
    {
        foreach (var addr in new[] { VtsXAddr, VtsYAddr, VtsSizeAddr, VtsRotationAddr })
            if (_evaluator.Curves.ContainsKey(addr))
                WriteCurveKeyAtPlayhead(addr, 0);

        _vtsRestX = 0;
        _vtsRestY = 0;
        _vtsRestSize = 0;
        _vtsRestRot = 0;
        _vtsSent = (double.NaN, double.NaN, double.NaN, double.NaN);
        PublishDirector();
        RefreshParamRows();
        RefreshTimeline();
        PushUndo();
        _saveError = SaveProjectNow();
        TblStatus.Text = L.T("StageUi.Vts.OffsetsZeroed");
    }

    private void CalibrateVtsBase()
    {
        if (_vts.State != VtsState.Ready)
        {
            TblStatus.Text = L.T("StageUi.Vts.ConnectBeforeCalibration");
            return;
        }
        if (string.IsNullOrWhiteSpace(_vts.ModelName))
        {
            TblStatus.Text = L.T("StageUi.Vts.NoModelLoaded");
            return;
        }

        _vts.CalibrateBase();
        _vtsSaveBasePending = true;
        _vtsRestX = 0;
        _vtsRestY = 0;
        _vtsRestSize = 0;
        _vtsRestRot = 0;
        _vtsSent = (double.NaN, double.NaN, double.NaN, double.NaN);
        RefreshParamRows();
        TblStatus.Text = L.T("StageUi.Vts.BaseCalibrated");
    }

    private void RefreshTintTargetsIfNeeded()
    {
        if (_vtsTintTargetCombo is null || _vtsTintScope == "all") return;

        int count = _vtsTintScope switch
        {
            "group" => _vts.ArtMeshGroups.Count,
            "name" => _vts.ArtMeshes.Count,
            "tag" => _vts.ArtMeshTags.Count,
            _ => 0,
        };
        if (count == _vtsTintTargetCount) return;

        _vtsTintTargetCount = count;
        FillTintTargets(_vtsTintTargetCombo);
    }

    private void FillTintTargets(ComboBox combo)
    {
        combo.Items.Clear();
        _vtsTintTargetCount = _vtsTintScope switch
        {
            "group" => _vts.ArtMeshGroups.Count,
            "name" => _vts.ArtMeshes.Count,
            "tag" => _vts.ArtMeshTags.Count,
            _ => 0,
        };

        switch (_vtsTintScope)
        {
            case "group":
                foreach (var group in _vts.ArtMeshGroups)
                    combo.Items.Add(new ComboBoxItem
                    {
                        Content = L.F("StageUi.Vts.GroupWithCount", group.GroupName, group.Count),
                        Tag = "group:" + group.GroupId,
                    });
                break;
            case "name":
            {
                var known = new HashSet<string>(_vts.ArtMeshes, StringComparer.Ordinal);
                var assigned = new HashSet<string>(StringComparer.Ordinal);
                foreach (var group in _vts.ArtMeshGroups)
                {
                    var entries = group.Entries.Where(known.Contains).ToArray();
                    if (entries.Length == 0) continue;

                    combo.Items.Add(new ComboBoxItem
                    {
                        Content = L.F("StageUi.Vts.GroupNameFormat", group.GroupName),
                        IsEnabled = false,
                    });
                    foreach (var name in entries)
                    {
                        combo.Items.Add(new ComboBoxItem { Content = "  " + name, Tag = "name:" + name });
                        assigned.Add(name);
                    }
                }

                foreach (var name in _vts.ArtMeshes)
                    if (!assigned.Contains(name))
                        combo.Items.Add(new ComboBoxItem { Content = name, Tag = "name:" + name });
                break;
            }
            case "tag":
                foreach (var tag in _vts.ArtMeshTags)
                    combo.Items.Add(new ComboBoxItem { Content = L.F("StageUi.Vts.TagNameFormat", tag), Tag = "tag:" + tag });
                break;
            default:
                combo.Items.Add(new ComboBoxItem { Content = L.T("StageUi.Vts.WholeModel"), Tag = "all" });
                break;
        }

        if (combo.Items.Count == 0)
            combo.Items.Add(new ComboBoxItem
            {
                Content = _vtsTintScope switch
                {
                    "group" => L.T("StageUi.Vts.NoLayerGroups"),
                    "name" => L.T("StageUi.Vts.NoArtMeshes"),
                    "tag" => L.T("StageUi.Vts.NoTags"),
                    _ => L.T("StageUi.Vts.NoTargets"),
                },
                IsEnabled = false,
            });

        for (int i = 0; i < combo.Items.Count; i++)
            if (combo.Items[i] is ComboBoxItem { Tag: string value } && value == _vtsTintTarget)
            {
                combo.SelectedIndex = i;
                return;
            }

        for (int i = 0; i < combo.Items.Count; i++)
            if (combo.Items[i] is ComboBoxItem { Tag: string first })
            {
                _vtsTintTarget = first;
                combo.SelectedIndex = i;
                UpdateTintHint();
                return;
            }

        _vtsTintTarget = "";
        combo.SelectedIndex = 0;
        UpdateTintHint();
    }

    private void UpdateTintHint()
    {
        if (_vtsTintHint is null) return;

        if (_vtsTintScope != "group" || _vts.ArtMeshGroups.Count > 0)
        {
            _vtsTintHint.Text = "";
            return;
        }

        if (!string.IsNullOrWhiteSpace(_vts.LastError))
        {
            _vtsTintHint.Text = L.F("StageUi.Vts.ReadLayerGroupsFailed", _vts.LastError);
            return;
        }

        var keys = _vts.ArtMeshResponseKeys;
        if (keys.Length == 0)
        {
            _vtsTintHint.Text = L.T("StageUi.Vts.ArtMeshListPending");
            return;
        }

        _vtsTintHint.Text = keys.Contains("artMeshGroups", StringComparison.Ordinal)
            ? L.T("StageUi.Vts.ArtMeshGroupsEmpty")
            : L.F("StageUi.Vts.ArtMeshGroupsMissing", keys);
    }

    private void AddVtsTintCue(int r, int g, int b)
    {
        if (_project is null) return;
        _project.VtsCues ??= new VtsCueTrack();

        double t = _audio.Time;
        var cues = _project.VtsCues.Cues;
        cues.RemoveAll(c => c is not null && c.Kind == VtsCueKind.Tint
            && c.Target == _vtsTintTarget
            && c.TintR == r && c.TintG == g && c.TintB == b
            && Math.Abs(c.Time - t) < 1e-3);
        cues.Add(new VtsCue
        {
            Time = t,
            Kind = VtsCueKind.Tint,
            Mode = _vtsTintMode,
            Target = _vtsTintTarget,
            Duration = Math.Max(0.2, _vtsTintDuration),
            TintR = r,
            TintG = g,
            TintB = b,
        });
        cues.Sort((x, y) => x.Time.CompareTo(y.Time));

        PushUndo();
        RefreshTimeline();
        TheTimeline.RevealCues();
        _saveError = SaveProjectNow();
        TblStatus.Text = L.F("StageUi.Vts.TintKeyframeAdded", t);
    }

    private void ResetVtsTint()
    {
        _vts.ClearArtMeshTint("all");
        TblStatus.Text = L.T("StageUi.Vts.TintReset");
    }

    private static (int R, int G, int B) ParseTintColor(string hex)
    {
        var text = hex.Trim().TrimStart('#');
        if (text.Length == 3) text = string.Concat(text.Select(c => new string(c, 2)));
        if (text.Length != 6
            || !int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
            return (255, 255, 255);
        return ((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
    }

    private int TintColorIndex(double time, int fallback)
    {
        var sections = _beatMap?.Sections;
        if (sections is { Count: > 0 })
            for (int i = 0; i < sections.Count; i++)
                if (time >= sections[i].Start && time < sections[i].End)
                    return i;

        return fallback / 8;
    }

    private static List<(int R, int G, int B)> BuildTintPalette(string hex)
    {
        var (r, g, b) = ParseTintColor(hex);
        var (h, s, v) = RgbToHsv(r, g, b);
        s = Math.Clamp(s, 0.35, 1);
        v = Math.Clamp(v, 0.45, 1);

        var palette = new List<(int, int, int)>(6);
        for (int i = 0; i < 6; i++)
            palette.Add(HsvToRgb(h + i * 60, s, v));
        return palette;
    }

    private static (double H, double S, double V) RgbToHsv(int r, int g, int b)
    {
        double rr = r / 255.0;
        double gg = g / 255.0;
        double bb = b / 255.0;
        double max = Math.Max(rr, Math.Max(gg, bb));
        double min = Math.Min(rr, Math.Min(gg, bb));
        double delta = max - min;

        double h = 0;
        if (delta > 1e-9)
        {
            if (max == rr) h = 60 * (((gg - bb) / delta) % 6);
            else if (max == gg) h = 60 * (((bb - rr) / delta) + 2);
            else h = 60 * (((rr - gg) / delta) + 4);
        }
        if (h < 0) h += 360;

        double s = max <= 0 ? 0 : delta / max;
        return (h, s, max);
    }

    private static (int R, int G, int B) HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = v - c;

        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return ((int)Math.Round((r + m) * 255),
            (int)Math.Round((g + m) * 255),
            (int)Math.Round((b + m) * 255));
    }

    private string VtsTintTargetName(string target)
    {
        if (string.IsNullOrEmpty(target) || target == "all") return L.T("StageUi.Vts.WholeModel");
        if (target.StartsWith("group:", StringComparison.Ordinal))
            return L.F("StageUi.Vts.GroupNameFormat", _vts.ArtMeshGroups
                .FirstOrDefault(g => g.GroupId == target[6..])?.GroupName ?? target[6..]);
        if (target.StartsWith("tag:", StringComparison.Ordinal)) return L.F("StageUi.Vts.TagNameFormat", target[4..]);
        if (target.StartsWith("name:", StringComparison.Ordinal))
        {
            var name = target[5..];
            var group = _vts.ArtMeshGroups.FirstOrDefault(g => g.Entries.Contains(name));
            return group is null ? name : L.F("StageUi.Vts.NameWithGroup", name, group.GroupName);
        }
        return target;
    }

    private string VtsTintTargetLabel(VtsCue cue)
    {
        var target = VtsTintTargetName(cue.Target);
        return $"#{Math.Clamp(cue.TintR, 0, 255):X2}"
               + $"{Math.Clamp(cue.TintG, 0, 255):X2}"
               + $"{Math.Clamp(cue.TintB, 0, 255):X2} {target}";
    }

    private void ClearVtsMoves()
    {
        bool removed = _evaluator.RemoveCurve(VtsXAddr);
        removed |= _evaluator.RemoveCurve(VtsYAddr);
        removed |= _evaluator.RemoveCurve(VtsSizeAddr);
        removed |= _evaluator.RemoveCurve(VtsRotationAddr);
        if (!removed)
        {
            TblStatus.Text = L.T("StageUi.Vts.NoMoveKeyframes");
            return;
        }

        ResetVtsMove();
        PushUndo();
        PublishDirector();
        RefreshTimeline();
        RefreshParamRows();
        _saveError = SaveProjectNow();
        TblStatus.Text = L.T("StageUi.Vts.MoveKeyframesCleared");
    }

    private ComboBox VtsModeCombo(Func<VtsCueMode> read, Action<VtsCueMode> write)
    {
        var combo = new ComboBox { FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };
        combo.Items.Add(new ComboBoxItem { Content = L.T("StageUi.Vts.CueModeToggle"), Tag = VtsCueMode.Toggle });
        combo.Items.Add(new ComboBoxItem { Content = L.T("StageUi.Vts.CueModeOnce"), Tag = VtsCueMode.Once });
        combo.Items.Add(new ComboBoxItem { Content = L.T("StageUi.Vts.CueModeManual"), Tag = VtsCueMode.Manual });
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
        TblStatus.Text = L.F("StageUi.Vts.CueAdded", t, (kind == VtsCueKind.Hotkey ? L.T("StageUi.Vts.Hotkey") : L.T("StageUi.Vts.Expression")))
                         + L.F("StageUi.Vts.CueModeSuffix", mode == VtsCueMode.Toggle ? L.T("StageUi.Vts.SwitchMode") : L.T("StageUi.Vts.OnceMode"));
    }

    private void UpdateVtsCues()
    {
        if (_project?.VtsCues?.Cues is not { } cues) return;

        double t = _audio.Time;
        if (!_audio.IsPlaying)
        {
            _cueTime = t - 0.001;
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
            else if (cue.Kind == VtsCueKind.Tint)
                _vts.SetArtMeshTint(cue.Target, cue.TintR, cue.TintG, cue.TintB);

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
        else if (kind == VtsCueKind.Tint) _vts.ClearArtMeshTint(target);
        else if (kind == VtsCueKind.Hotkey) _vts.TriggerHotkey(target);
    }

    private void AddVtsMoveKeysAtPlayhead()
    {
        double t = _audio.Time;
        AddVtsMoveKey(VtsXAddr, _vtsRestX, t);
        AddVtsMoveKey(VtsYAddr, _vtsRestY, t);
        AddVtsMoveKey(VtsSizeAddr, _vtsRestSize, t);
        AddVtsMoveKey(VtsRotationAddr, _vtsRestRot, t);

        PublishDirector();
        RefreshTimeline();
        RefreshParamRows();
        PushUndo();
        _saveError = SaveProjectNow();
        TblStatus.Text = L.F("StageUi.Vts.MoveKeyframesAdded", t);
    }

    private void AddVtsMoveKey(in ParamAddress addr, double baseValue, double time)
    {
        double value = _evaluator.TryEvaluate(addr, time, out var eval) ? eval : baseValue;
        var curve = EnsureCurve(addr, baseValue, time);

        var mode = ModeOfKeyAt(curve, time) ?? DefaultCurveMode();
        curve.Remove(time);
        curve.TryAdd(time, value, mode);
    }

    private void UpdateVtsMove()
    {
        if (_vts.Framing is null) return;

        var t = _audio.Time;
        var x = _evaluator.TryEvaluate(VtsXAddr, t, out var vx) ? vx : _vtsRestX;
        var y = _evaluator.TryEvaluate(VtsYAddr, t, out var vy) ? vy : _vtsRestY;
        var size = _evaluator.TryEvaluate(VtsSizeAddr, t, out var vs) ? vs : _vtsRestSize;
        var rotation = _evaluator.TryEvaluate(VtsRotationAddr, t, out var vr) ? vr : _vtsRestRot;

        bool controlActive = _stageVtsDrive
            && _selectedLayer is not null
            && _scene.IsCharacterLayer(_selectedLayer);
        var hull = _vts.Hull;
        _pipe?.VtsControl = new PvVtsControl(controlActive, x, y, size, rotation,
            hull?.MinX ?? 0, hull?.MaxX ?? 0, hull?.MinY ?? 0, hull?.MaxY ?? 0, hull is not null);

        if (Math.Abs(x - _vtsSent.X) < 1e-4
            && Math.Abs(y - _vtsSent.Y) < 1e-4
            && Math.Abs(size - _vtsSent.Size) < 1e-4
            && Math.Abs(rotation - _vtsSent.Rotation) < 1e-4) return;

        var baseSize = _vts.Framing?.Size ?? 0;
        var sizeDrive = baseSize * Math.Clamp(size, -90, 300) / 100.0;

        _vtsSent = (x, y, size, rotation);
        _vts.SetDriveOffset(sizeDrive, Math.Clamp(x, -1000, 1000), Math.Clamp(y, -1000, 1000),
            Math.Clamp(rotation, -360, 360));
    }

    private void ResetVtsMove()
    {
        _vtsSent = (double.NaN, double.NaN, double.NaN, double.NaN);
        _vts.SetDriveOffset(0, 0, 0, 0);
    }

    private static CurveInterp DefaultCurveMode() => CurveInterp.SmoothStep;
}
