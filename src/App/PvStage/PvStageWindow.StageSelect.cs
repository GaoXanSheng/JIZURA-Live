using System.Numerics;
using System.Windows;
using System.Windows.Input;
using JizuraLive.App.I18n;
using JizuraLive.Pv;

namespace JizuraLive.App.PvStage;

public partial class PvStageWindow
{
    private enum StageDragKind
    {
        None,
        Move,
        Scale,
        Rotate,
    }

    private PvLayerConfig? _stageDragLayer;
    private StageDragKind _stageDragKind;
    private bool _stageDragVts;
    private Vector2 _stageDragStart;
    private Matrix3x2 _stageDragInverse;
    private Vector2 _stageDragCenter;
    private double _stageDragStartDistance;
    private double _stageDragStartAngle;
    private double _stageDragVtsScaleX;
    private double _stageDragVtsScaleY;
    private double _stageDragBaseX;
    private double _stageDragBaseY;
    private double _stageDragBaseScale;
    private double _stageDragBaseRotation;
    private double _stageDragValueX;
    private double _stageDragValueY;
    private double _stageDragValueScale;
    private double _stageDragValueRotation;
    private bool _stageDragKeyX;
    private bool _stageDragKeyY;
    private bool _stageDragKeyScale;
    private bool _stageDragKeyRotation;
    private bool _stageDragMoved;
    private long _stageDragRefreshAt;


    private void SyncStageSelection() => _pipe?.SelectedLayerId = _selectedLayer?.Id;

    private void Stage_PointerPressed(Point hostPoint)
    {
        if (_exporting || _selectedLayer is not { } layer || _pipe is not { } pipe) return;
        if (pipe.Selection is not { } sel) return;
        if (!TryDesignPoint(hostPoint, out var design)) return;
        if (!TryStageHandle(sel, design, out var kind)) return;

        if (_audio.IsPlaying)
        {
            _audio.Pause();
            UpdatePlayIcon();
        }

        var vts = _stageVtsDrive && _scene.IsCharacterLayer(layer);
        var t = _audio.Time;
        var xAddr = vts ? VtsXAddr : LayerAddr(layer, "offsetX");
        var yAddr = vts ? VtsYAddr : LayerAddr(layer, "offsetY");
        var scaleAddr = vts ? VtsSizeAddr : LayerAddr(layer, "scale");
        var rotateAddr = vts ? VtsRotationAddr : LayerAddr(layer, "rotation");

        _stageDragLayer = layer;
        _stageDragKind = kind;
        _stageDragVts = vts;
        _stageDragStart = design;
        _stageDragCenter = sel.Center;
        _stageDragStartDistance = Math.Max(1f, Vector2.Distance(design, sel.Center));
        _stageDragStartAngle = Angle(design - sel.Center);
        _stageDragVtsScaleX = 2.0 / Math.Max(1.0, sel.FitWidth);
        _stageDragVtsScaleY = 2.0 / Math.Max(1.0, sel.FitHeight);
        _stageDragMoved = false;

        _stageDragKeyX = Keyed(xAddr);
        _stageDragKeyY = Keyed(yAddr);
        _stageDragKeyScale = Keyed(scaleAddr);
        _stageDragKeyRotation = Keyed(rotateAddr);
        _stageDragBaseX = StageValue(xAddr, t, layer, vts);
        _stageDragBaseY = StageValue(yAddr, t, layer, vts);
        _stageDragBaseScale = StageValue(scaleAddr, t, layer, vts);
        _stageDragBaseRotation = StageValue(rotateAddr, t, layer, vts);
        _stageDragValueX = _stageDragBaseX;
        _stageDragValueY = _stageDragBaseY;
        _stageDragValueScale = _stageDragBaseScale;
        _stageDragValueRotation = _stageDragBaseRotation;

        var linear = new Matrix3x2(sel.Matrix.M11, sel.Matrix.M12, sel.Matrix.M21, sel.Matrix.M22, 0, 0);
        _stageDragInverse = Matrix3x2.Invert(linear, out var inverse) ? inverse : Matrix3x2.Identity;
    }

    private void Stage_PointerMoved(Point hostPoint)
    {
        if (_stageDragLayer is not { } layer) return;
        if (!TryDesignPoint(hostPoint, out var design)) return;

        if (!_stageDragMoved)
        {
            if (_stageDragKind == StageDragKind.Move
                && Vector2.DistanceSquared(design, _stageDragStart) < 0.25f) return;
            _stageDragMoved = true;
            PushUndo();
        }

        double t = _audio.Time;
        switch (_stageDragKind)
        {
            case StageDragKind.Move:
                MoveStageLayer(layer, design, t);
                break;
            case StageDragKind.Scale:
                ScaleStageLayer(layer, design, t);
                break;
            case StageDragKind.Rotate:
                RotateStageLayer(layer, design, t);
                break;
        }

        PublishDirector();

        long now = Environment.TickCount64;
        if (now - _stageDragRefreshAt >= 33)
        {
            _stageDragRefreshAt = now;
            RefreshParamRows();
            RefreshTimeline();
        }

        TblStatus.Text = StageStatus(layer);
    }

    private void MoveStageLayer(PvLayerConfig layer, Vector2 design, double t)
    {
        var delta = design - _stageDragStart;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            if (Math.Abs(delta.X) >= Math.Abs(delta.Y)) delta.Y = 0;
            else delta.X = 0;
        }

        var offset = Vector2.Transform(delta, _stageDragInverse);
        double x, y;
        if (_stageDragVts)
        {
            x = SnapVtsMove(Math.Clamp(_stageDragBaseX + offset.X * _stageDragVtsScaleX, -1000, 1000));
            y = SnapVtsMove(Math.Clamp(_stageDragBaseY - offset.Y * _stageDragVtsScaleY, -1000, 1000));
        }
        else
        {
            x = Math.Clamp(_stageDragBaseX + offset.X / _scene.Width, -1, 1);
            y = Math.Clamp(_stageDragBaseY + offset.Y / _scene.Height, -1, 1);
        }

        var xAddr = _stageDragVts ? VtsXAddr : LayerAddr(layer, "offsetX");
        var yAddr = _stageDragVts ? VtsYAddr : LayerAddr(layer, "offsetY");
        ApplyStageValue(layer, xAddr, _stageDragKeyX, _stageDragVts, x, t);
        ApplyStageValue(layer, yAddr, _stageDragKeyY, _stageDragVts, y, t);

        _stageDragValueX = x;
        _stageDragValueY = y;
    }

    private void ScaleStageLayer(PvLayerConfig layer, Vector2 design, double t)
    {
        double distance = Vector2.Distance(design, _stageDragCenter);
        double ratio = distance / _stageDragStartDistance;
        double value = _stageDragVts
            ? Math.Clamp(_stageDragBaseScale + (ratio - 1) * 100, -90, 300)
            : Math.Clamp(_stageDragBaseScale * ratio, 0.1, 5);

        var addr = _stageDragVts ? VtsSizeAddr : LayerAddr(layer, "scale");
        ApplyStageValue(layer, addr, _stageDragKeyScale, _stageDragVts, value, t);
        _stageDragValueScale = value;
    }

    private void RotateStageLayer(PvLayerConfig layer, Vector2 design, double t)
    {
        double angle = Angle(design - _stageDragCenter);
        double delta = NormalizeAngle(angle - _stageDragStartAngle);
        double value = Math.Clamp(_stageDragBaseRotation + delta, -180, 180);

        var addr = _stageDragVts ? VtsRotationAddr : LayerAddr(layer, "rotation");
        ApplyStageValue(layer, addr, _stageDragKeyRotation, _stageDragVts, value, t);
        _stageDragValueRotation = value;
    }

    private string StageStatus(PvLayerConfig layer)
    {
        string title = (_stageDragVts ? "VTS " : "") + LayerTitle(layer);
        return _stageDragKind switch
        {
            StageDragKind.Scale => L.F("图层 {0}：{1} {2:F2}", title, L.T("缩放"), _stageDragValueScale),
            StageDragKind.Rotate => L.F("图层 {0}：{1} {2:F1}", title, L.T("旋转"), _stageDragValueRotation),
            _ when _stageDragVts =>
                L.F("图层 {0}：X {1:F2} · Y {2:F2}", title, _stageDragValueX, _stageDragValueY),
            _ => L.F("图层 {0}：X {1:F3} · Y {2:F3}", title, _stageDragValueX, _stageDragValueY),
        };
    }

    private void Stage_PointerReleased()
    {
        if (_stageDragLayer is null) return;

        var layer = _stageDragLayer;
        var kind = _stageDragKind;
        bool moved = _stageDragMoved;
        bool vts = _stageDragVts;
        _stageDragLayer = null;
        _stageDragKind = StageDragKind.None;
        _stageDragMoved = false;
        if (!moved) return;

        RefreshParamRows();
        RefreshTimeline();
        _saveError = SaveProjectNow();

        string title = (vts ? "VTS " : "") + LayerTitle(layer);
        TblStatus.Text = kind switch
        {
            StageDragKind.Scale => L.F("已调整图层 {0}：{1} {2:F2}", title, L.T("缩放"), _stageDragValueScale),
            StageDragKind.Rotate => L.F("已调整图层 {0}：{1} {2:F1}", title, L.T("旋转"), _stageDragValueRotation),
            _ when vts =>
                L.F("已调整图层 {0}：X {1:F2} · Y {2:F2}", title, _stageDragValueX, _stageDragValueY),
            _ => L.F("已调整图层 {0}：X {1:F3} · Y {2:F3}", title, _stageDragValueX, _stageDragValueY),
        };
    }

    private void ApplyStageValue(PvLayerConfig layer, in ParamAddress addr, bool keyed,
        bool vts, double value, double t)
    {
        if (keyed)
        {
            var curve = EnsureCurve(addr, StageRest(layer, addr, vts), t);
            var mode = ModeOfKeyAt(curve, t) ?? DefaultCurveMode();
            curve.Remove(t);
            curve.TryAdd(t, value, mode);
            return;
        }

        if (vts)
        {
            switch (addr.Param)
            {
                case "x": _vtsRestX = value; break;
                case "y": _vtsRestY = value; break;
                case "size": _vtsRestSize = value; break;
                case "rotation": _vtsRestRot = value; break;
            }
            return;
        }

        switch (addr.Param)
        {
            case "offsetX": layer.OffsetX = value; break;
            case "offsetY": layer.OffsetY = value; break;
            case "scale": layer.Scale = value; break;
            case "rotation": layer.Rotation = value; break;
        }
    }

    private double StageValue(in ParamAddress addr, double t, PvLayerConfig layer, bool vts) =>
        _evaluator.TryEvaluate(addr, t, out var value) ? value : StageRest(layer, addr, vts);

    private double StageRest(PvLayerConfig layer, in ParamAddress addr, bool vts) =>
        addr.Param switch
        {
            "offsetX" => layer.OffsetX,
            "offsetY" => layer.OffsetY,
            "scale" => layer.Scale,
            "rotation" when !vts => layer.Rotation,
            "x" => _vtsRestX,
            "y" => _vtsRestY,
            "size" => _vtsRestSize,
            "rotation" => _vtsRestRot,
            _ => 0,
        };

    private bool Keyed(in ParamAddress addr) => _evaluator.Curves.TryGetValue(addr, out _);

    private static ParamAddress LayerAddr(PvLayerConfig layer, string param) =>
        new(ParamAddressKind.Layer, layer.Id, param);

    private static double Angle(Vector2 v) => Math.Atan2(v.Y, v.X) * 180.0 / Math.PI;

    private static double NormalizeAngle(double degrees)
    {
        degrees %= 360;
        if (degrees > 180) degrees -= 360;
        if (degrees < -180) degrees += 360;
        return degrees;
    }

    private bool TryDesignPoint(Point hostPoint, out Vector2 design)
    {
        design = default;

        double w = Stage.ActualWidth;
        double h = Stage.ActualHeight;
        int dw = _pipe?.DesignWidth ?? _scene.Width;
        int dh = _pipe?.DesignHeight ?? _scene.Height;
        if (w <= 1 || h <= 1 || dw <= 0 || dh <= 0) return false;

        double scale = Math.Min(w / dw, h / dh);
        if (scale <= 0) return false;

        double pw = dw * scale;
        double ph = dh * scale;
        double ox = (w - pw) / 2;
        double oy = (h - ph) / 2;

        design = new Vector2(
            (float)((hostPoint.X - ox) / scale),
            (float)((hostPoint.Y - oy) / scale));
        return true;
    }

    private static bool TryStageHandle(PvSelectionInfo sel, Vector2 p, out StageDragKind kind)
    {
        const float radius = 18f;
        if (Vector2.DistanceSquared(p, sel.RotationHandle) <= radius * radius)
        {
            kind = StageDragKind.Rotate;
            return true;
        }

        if (Near(p, sel.A, radius) || Near(p, sel.B, radius)
            || Near(p, sel.C, radius) || Near(p, sel.D, radius))
        {
            kind = StageDragKind.Scale;
            return true;
        }

        if (SelectionContains(sel, p))
        {
            kind = StageDragKind.Move;
            return true;
        }

        kind = StageDragKind.None;
        return false;
    }

    private static bool Near(Vector2 a, Vector2 b, float radius) =>
        Vector2.DistanceSquared(a, b) <= radius * radius;

    private static bool SelectionContains(PvSelectionInfo sel, Vector2 p)
    {
        float c1 = Cross(sel.A, sel.B, p);
        float c2 = Cross(sel.B, sel.C, p);
        float c3 = Cross(sel.C, sel.D, p);
        float c4 = Cross(sel.D, sel.A, p);

        return (c1 >= 0 && c2 >= 0 && c3 >= 0 && c4 >= 0)
               || (c1 <= 0 && c2 <= 0 && c3 <= 0 && c4 <= 0);
    }

    private static float Cross(Vector2 a, Vector2 b, Vector2 p) =>
        (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);

    private bool StageCursorHitTest(Point hostPoint)
    {
        if (_exporting || _selectedLayer is null || _pipe?.Selection is not { } sel) return false;
        return TryDesignPoint(hostPoint, out var design) && TryStageHandle(sel, design, out _);
    }
}
