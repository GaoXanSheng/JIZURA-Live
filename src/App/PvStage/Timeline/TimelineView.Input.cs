using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using JizuraLive.App.I18n;
using JizuraLive.Pv;

namespace JizuraLive.App.PvStage.Timeline;

public sealed partial class TimelineView
{
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (_snap is null) return;
        Focus();

        var p = e.GetPosition(this);
        if (e.ChangedButton == MouseButton.Right)
        {
            OpenKeyMenu(p);
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;
        e.Handled = true;

        if (p.Y >= RenderSize.Height - ScrollbarHeight)
        {
            _drag = DragMode.Pan;
            _dragOrigin = p;
            CaptureMouse();
            PanTo(p.X);
            return;
        }

        if (RowAt(p.Y) is not { } row) return;
        double x = p.X - HeaderWidth;

        if (e.ClickCount == 2 && row.Kind == RowKind.Automation)
        {
            HandleAutomationDoubleClick(row, x);
            return;
        }

        switch (row.Kind)
        {
            case RowKind.Ruler:
            case RowKind.Waveform:
                StartScrub(x);
                break;
            case RowKind.Lyrics:
                StartClipGesture(x);
                break;
            case RowKind.Automation:
                StartAutomationDrag(row, p, x);
                break;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag == DragMode.None) return;
        var p = e.GetPosition(this);
        switch (_drag)
        {
            case DragMode.Scrub:
                RaiseSeek(TimeAt(p.X - HeaderWidth));
                break;
            case DragMode.Key:
                MoveSelectedKeys(p);
                break;
            case DragMode.Handle:
                MoveHandle(p);
                break;
            case DragMode.Pan:
                PanTo(p.X);
                break;
            case DragMode.Clip:
                MoveClip(p);
                break;
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton != MouseButton.Left || _drag == DragMode.None) return;

        if (_drag == DragMode.Key)
        {
            RebuildSelectionFromDrag(); // 必须在 CommitGesture 排序之前取时刻
            _dragKeys.Clear();
            _dragTip = null;
            _drag = DragMode.None;
            CommitGesture("move");
        }
        else if (_drag == DragMode.Handle)
        {
            _drag = DragMode.None;
            CommitGesture("interp");
        }
        else if (_drag == DragMode.Clip)
        {
            _drag = DragMode.None;
            CommitClip();
        }
        else _drag = DragMode.None;

        if (IsMouseCaptured) ReleaseMouseCapture();
        InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_drag == DragMode.None) return;
        RestoreGesture();
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_snap is null) return;
        e.Handled = true;

        double x = Math.Max(0, e.GetPosition(this).X - HeaderWidth);
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            double anchorTime = TimeAt(x);
            double factor = e.Delta > 0 ? 1.25 : 0.8;
            _pixelsPerSecond = TimelineMath.ClampPixelsPerSecond(_pixelsPerSecond * factor, ContentWidth, _snap.DurationSeconds);
            _viewStart = TimelineMath.ZoomViewStart(anchorTime, x, _pixelsPerSecond);
        }
        else _viewStart -= e.Delta / 120.0 * ContentWidth * 0.15 / _pixelsPerSecond;

        _viewStart = TimelineMath.ClampViewStart(_viewStart, _pixelsPerSecond, ContentWidth, _snap.DurationSeconds);
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_snap is null) return;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        switch (e.Key)
        {
            case Key.Escape:
                CancelGesture();
                e.Handled = true;
                break;
            case Key.Delete:
                DeleteSelected();
                e.Handled = true;
                break;
            case Key.C when ctrl:
                CopySelected();
                e.Handled = true;
                break;
            case Key.V when ctrl:
                PasteAtPlayhead();
                e.Handled = true;
                break;
        }
    }

    private void StartScrub(double x)
    {
        _selection.Clear();
        _drag = DragMode.Scrub;
        CaptureMouse();
        RaiseSeek(TimeAt(x));
    }

    /// <summary>
    /// 歌词轨上按下：点句首仍 seek 到 start（W1 行为），按住句首/句尾/条体则起拖；空档点击只 seek。
    /// 拖动中就地改那句的区间（渲染跟着走），松手才决定提交还是还原。
    /// </summary>
    private void StartClipGesture(double x)
    {
        _dragTip = null;
        double time = TimeAt(x);
        double tolerance = TimelineMath.ClipEdgeHitPx / Math.Max(1, _pixelsPerSecond);
        if (_snap is null || TimelineMath.HitClip(_snap.Lyrics, time, tolerance) is not { } hit)
        {
            RaiseSeek(time);
            return;
        }

        var clip = _snap.Lyrics[hit.Index];
        RaiseSeek(clip.Start);
        _clipIndex = hit.Index;
        _clipEdge = hit.Edge;
        _clipOrigin = new ClipRange(clip.Start, clip.End);
        _clipAnchorTime = time;
        _clipConflict = false;
        _drag = DragMode.Clip;
        CaptureMouse();
        InvalidateVisual();
    }

    /// <summary>落点冲突时置高亮与提示，但快照照旧跟着指针走（拖动中的样子就是提交后的样子）。</summary>
    private void MoveClip(Point p)
    {
        if (_snap is null || _clipIndex < 0 || _clipIndex >= _snap.Lyrics.Count) return;

        bool snap = (Keyboard.Modifiers & ModifierKeys.Alt) == 0;
        var range = TimelineMath.DragClip(_clipOrigin, _clipEdge,
            TimeAt(p.X - HeaderWidth) - _clipAnchorTime,
            TimelineMath.GridStep(_pixelsPerSecond, Fps), snap);

        var clip = _snap.Lyrics[_clipIndex];
        clip.Start = range.Start;
        clip.End = range.End;
        var fault = TimelineMath.ClipFault(_snap.Lyrics, _clipIndex, range);
        _clipConflict = fault is not null;
        _dragTip = fault ?? $"{range.Start:F3}s – {range.End:F3}s";
        InvalidateVisual();
    }

    /// <summary>冲突则还原并把原因留在提示条（不提交、不入撤销栈）；否则一次 EditCommitted（Reason = "clip"）。</summary>
    private void CommitClip()
    {
        if (_snap is null || _clipIndex < 0 || _clipIndex >= _snap.Lyrics.Count)
        {
            _clipIndex = -1;
            _clipConflict = false;
            return;
        }

        int index = _clipIndex;
        var clip = _snap.Lyrics[index];
        var range = new ClipRange(clip.Start, clip.End);
        _clipIndex = -1;
        _clipConflict = false;

        if (TimelineMath.ClipFault(_snap.Lyrics, index, range) is { } fault)
        {
            clip.Start = _clipOrigin.Start;
            clip.End = _clipOrigin.End;
            _dragTip = fault;
            return;
        }

        bool moved = Math.Abs(range.Start - _clipOrigin.Start) > TimelineMath.TimeEpsilon ||
                     Math.Abs(range.End - _clipOrigin.End) > TimelineMath.TimeEpsilon;
        _dragTip = null;
        if (moved) EditCommitted?.Invoke(new TEdit { Reason = "clip", Clip = new TClipEdit(index, range.Start, range.End) });
    }

    private void StartAutomationDrag(Row row, Point p, double x)
    {
        var curve = _snap!.Curves[row.Index];
        if (StartHandleDrag(row, curve, p)) return;
        int hit = TimelineMath.HitKey(curve.Keys, x, _viewStart, _pixelsPerSecond);
        if (hit < 0)
        {
            StartScrub(x);
            return;
        }

        var key = curve.Keys[hit];
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            int at = _selection.FindIndex(s => s.Address == curve.Address && Math.Abs(s.Time - key.TimeSeconds) <= TimelineMath.TimeEpsilon);
            if (at >= 0)
            {
                _selection.RemoveAt(at);
                InvalidateVisual();
                return;
            }
            _selection.Add(new SelKey(curve.Address, key.TimeSeconds));
        }
        else if (!IsSelected(curve.Address, key.TimeSeconds))
        {
            _selection.Clear();
            _selection.Add(new SelKey(curve.Address, key.TimeSeconds));
        }

        _dragKeys.Clear();
        foreach (var selected in _selection)
        {
            var owner = FindCurve(selected.Address);
            if (owner is null) continue;
            int index = TimelineMath.IndexAtTime(owner.Keys, selected.Time);
            if (index >= 0) _dragKeys.Add((selected.Address, index, owner.Keys[index]));
        }

        BeginGesture(_dragKeys.Select(d => d.Address));
        _dragValuePerPixel = (curve.Max - curve.Min) / Math.Max(1, row.Height - 2 * ValuePad);
        _dragOrigin = p;
        _drag = DragMode.Key;
        CaptureMouse();
        InvalidateVisual();
    }

    /// <summary>命中控制柄则起拖（先于 key 拖动判定：柄就画在 key 旁边）。</summary>
    private bool StartHandleDrag(Row row, TCurve curve, Point p)
    {
        foreach (var (index, outSide) in HandleTargets(curve))
        {
            var handle = HandlePixel(curve, row, index, outSide);
            if (Math.Abs(handle.X - p.X) > TimelineMath.KeyHitRadiusPx) continue;
            if (Math.Abs(handle.Y - p.Y) > TimelineMath.KeyHitRadiusPx) continue;

            BeginGesture([curve.Address]);
            _handleDrag = (curve.Address, index, outSide);
            _dragRow = row;
            _drag = DragMode.Handle;
            CaptureMouse();
            return true;
        }
        return false;
    }

    private void MoveHandle(Point p)
    {
        var curve = FindCurve(_handleDrag.Address);
        if (curve is null || _handleDrag.KeyIndex >= curve.Keys.Count) return;

        int index = _handleDrag.KeyIndex;
        double time = TimeAt(p.X - HeaderWidth);
        double value = TimelineMath.YToValue(p.Y, curve.Min, curve.Max,
            _dragRow.Top + ValuePad, _dragRow.Height - 2 * ValuePad);

        if (_handleDrag.Out)
        {
            if (index + 1 >= curve.Keys.Count) return;
            var key = curve.Keys[index];
            var (x, y) = TimelineMath.OutHandleAt(time, value, key, curve.Keys[index + 1], key.OutY);
            curve.Keys[index] = key with { OutX = x, OutY = y };
        }
        else
        {
            if (index == 0) return;
            var key = curve.Keys[index];
            var (x, y) = TimelineMath.InHandleAt(time, value, curve.Keys[index - 1], key, key.InY);
            curve.Keys[index] = key with { InX = x, InY = y };
        }
        InvalidateVisual();
    }

    private void MoveSelectedKeys(Point p)
    {
        bool snap = (Keyboard.Modifiers & ModifierKeys.Alt) == 0;
        double grid = TimelineMath.GridStep(_pixelsPerSecond, Fps);
        double dt = TimeAt(p.X - HeaderWidth) - TimeAt(_dragOrigin.X - HeaderWidth);
        double dv = (_dragOrigin.Y - p.Y) * _dragValuePerPixel;

        foreach (var (address, index, origin) in _dragKeys)
        {
            var curve = FindCurve(address);
            if (curve is null || index >= curve.Keys.Count) continue;
            double time = Math.Max(0, TimelineMath.Snap(origin.TimeSeconds + dt, grid, snap));
            // 值域非法（Max ≤ Min）时不钳制：Math.Clamp 的 min > max 会抛
            double value = curve.Max > curve.Min ? Math.Clamp(origin.Value + dv, curve.Min, curve.Max) : origin.Value + dv;
            curve.Keys[index] = origin with { TimeSeconds = time, Value = value };
        }

        if (_dragKeys.Count > 0)
        {
            var first = _dragKeys[0];
            if (FindCurve(first.Address) is { } owner && first.KeyIndex < owner.Keys.Count)
            {
                var key = owner.Keys[first.KeyIndex];
                _dragTip = $"{key.TimeSeconds:F3}s  {key.Value.ToString("0.###", CultureInfo.InvariantCulture)}";
            }
        }
        InvalidateVisual();
    }

    private void RebuildSelectionFromDrag()
    {
        _selection.Clear();
        foreach (var (address, index, _) in _dragKeys)
        {
            var curve = FindCurve(address);
            if (curve is not null && index < curve.Keys.Count) _selection.Add(new SelKey(address, curve.Keys[index].TimeSeconds));
        }
    }

    private void PanTo(double x)
    {
        if (_snap is null) return;
        double trackW = Math.Max(1, RenderSize.Width - HeaderWidth);
        double duration = Math.Max(_snap.DurationSeconds, TimelineMath.MinSecondsPerScreen);
        double visible = Math.Min(duration, trackW / _pixelsPerSecond);
        double thumbW = Math.Min(trackW, Math.Max(24, trackW * visible / duration));
        double usable = Math.Max(1, trackW - thumbW);
        double fraction = Math.Clamp((x - HeaderWidth - thumbW / 2) / usable, 0, 1);
        _viewStart = TimelineMath.ClampViewStart(fraction * (duration - visible), _pixelsPerSecond, trackW, _snap.DurationSeconds);
        InvalidateVisual();
    }

    private void HandleAutomationDoubleClick(Row row, double x)
    {
        var curve = _snap!.Curves[row.Index];
        int hit = TimelineMath.HitKey(curve.Keys, x, _viewStart, _pixelsPerSecond);
        if (hit >= 0)
        {
            EditKeyValue(curve, hit);
            return;
        }
        AddKeyAt(curve, x);
    }

    /// <summary>空白区双击打帧：取该时刻的显示值；同刻已有 key 则只是选中它（同刻 = 替换）。</summary>
    private void AddKeyAt(TCurve curve, double x)
    {
        bool snap = (Keyboard.Modifiers & ModifierKeys.Alt) == 0;
        double time = TimelineMath.Snap(TimeAt(x), TimelineMath.GridStep(_pixelsPerSecond, Fps), snap);
        int existing = TimelineMath.IndexAtTime(curve.Keys, time);
        if (existing >= 0)
        {
            _selection.Clear();
            _selection.Add(new SelKey(curve.Address, curve.Keys[existing].TimeSeconds));
            InvalidateVisual();
            return;
        }

        BeginGesture([curve.Address]);
        double value = TimelineMath.Evaluate(curve.Keys, time, (curve.Min + curve.Max) / 2);
        curve.Keys.Add(new TKey(time, value, "SmoothStep"));
        _selection.Clear();
        _selection.Add(new SelKey(curve.Address, time));
        CommitGesture("add");
        InvalidateVisual();
    }

    private void EditKeyValue(TCurve curve, int index)
    {
        if (!PromptValue(curve.Keys[index].Value, out double value)) return;
        BeginGesture([curve.Address]);
        curve.Keys[index] = curve.Keys[index] with { Value = value };
        CommitGesture("value");
        InvalidateVisual();
    }

    private void OpenKeyMenu(Point p)
    {
        if (_snap is null || RowAt(p.Y) is not { Kind: RowKind.Automation } row) return;
        var curve = _snap.Curves[row.Index];
        int hit = TimelineMath.HitKey(curve.Keys, p.X - HeaderWidth, _viewStart, _pixelsPerSecond);
        if (hit < 0) return;

        var key = curve.Keys[hit];
        if (!IsSelected(curve.Address, key.TimeSeconds)) SelectKey(curve.Address, key.TimeSeconds);

        var menu = new ContextMenu { PlacementTarget = this, Placement = PlacementMode.MousePoint };
        foreach (var interp in TimelineMath.Interps)
        {
            var item = new MenuItem { Header = interp, IsCheckable = true, IsChecked = key.Interp == interp };
            item.Click += (_, _) => SetInterp(interp);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var remove = new MenuItem { Header = "Delete" };
        remove.Click += (_, _) => DeleteSelected();
        menu.Items.Add(remove);
        menu.IsOpen = true;
    }

    /// <summary>把当前选区的插值改成 <paramref name="interp"/>（右键五档菜单的入口）。一次 EditCommitted。</summary>
    public void SetInterp(string interp)
    {
        if (_selection.Count == 0) return;
        BeginGesture(_selection.Select(s => s.Address));
        foreach (var selected in _selection)
        {
            var curve = FindCurve(selected.Address);
            if (curve is null) continue;
            int index = TimelineMath.IndexAtTime(curve.Keys, selected.Time);
            if (index >= 0) curve.Keys[index] = curve.Keys[index] with { Interp = interp };
        }
        CommitGesture("interp");
        InvalidateVisual();
    }

    private void DeleteSelected()
    {
        if (_selection.Count == 0) return;
        BeginGesture(_selection.Select(s => s.Address));
        foreach (var selected in _selection)
        {
            var curve = FindCurve(selected.Address);
            if (curve is null) continue;
            int index = TimelineMath.IndexAtTime(curve.Keys, selected.Time);
            if (index >= 0) curve.Keys.RemoveAt(index);
        }
        _selection.Clear();
        CommitGesture("delete");
        InvalidateVisual();
    }

    private void CopySelected()
    {
        _clipboard.Clear();
        if (_selection.Count == 0) return;

        _clipboardAddress = _selection[0].Address; // 同轨粘贴：只复制首个选中 key 所在轨
        foreach (var selected in _selection)
        {
            if (selected.Address != _clipboardAddress) continue;
            var curve = FindCurve(selected.Address);
            if (curve is null) continue;
            int index = TimelineMath.IndexAtTime(curve.Keys, selected.Time);
            if (index >= 0) _clipboard.Add(curve.Keys[index]);
        }
        _clipboard.Sort(static (a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
    }

    private void PasteAtPlayhead()
    {
        if (_clipboard.Count == 0 || _snap is null) return;
        if (FindCurve(_clipboardAddress) is not { } curve) return;

        double anchor = _clipboard[0].TimeSeconds;
        BeginGesture([curve.Address]);
        foreach (var key in _clipboard)
        {
            double time = _snap.PlayheadSeconds + (key.TimeSeconds - anchor);
            int conflict = TimelineMath.IndexAtTime(curve.Keys, time);
            if (conflict >= 0) curve.Keys[conflict] = key with { TimeSeconds = time };
            else curve.Keys.Add(key with { TimeSeconds = time });
        }
        _selection.Clear();
        CommitGesture("add");
        InvalidateVisual();
    }

    private bool PromptValue(double current, out double value)
    {
        value = current;
        var box = new TextBox
        {
            Text = current.ToString("0.###", CultureInfo.InvariantCulture),
            MinWidth = 140,
            Padding = new Thickness(4, 2, 4, 2),
        };
        box.SelectAll();

        var accept = new Button { Content = "OK", MinWidth = 64, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = L.T("Dialog.Cancel"), MinWidth = 64, IsCancel = true };
        var dialog = new Window
        {
            Owner = Window.GetWindow(this),
            Title = "Value",
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Content = new StackPanel
            {
                Margin = new Thickness(14),
                Children =
                {
                    box,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 12, 0, 0),
                        Children = { accept, cancel },
                    },
                },
            },
        };

        bool accepted = false;
        accept.Click += (_, _) =>
        {
            accepted = true;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        dialog.ShowDialog();

        return accepted && double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
