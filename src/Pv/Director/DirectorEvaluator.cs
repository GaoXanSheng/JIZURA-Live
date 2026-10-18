namespace JizuraLive.Pv;

public enum CurveBlend
{
    Add,

    Multiply,
}

public sealed record CurveLayer(string Name, CurveBlend Blend, FloatCurve Curve,
    string Source = "user", bool Locked = false);

public sealed class DirectorEvaluator
{
    private readonly Dictionary<ParamAddress, FloatCurve> _curves = new();
    private readonly Dictionary<ParamAddress, List<CurveLayer>> _layers = new();
    private readonly Dictionary<ParamAddress, (string Source, bool Locked)> _curveProvenance = new();
    private CameraTrack? _cameraTrack;
    private Dictionary<string, CameraTrack> _layerCameraTracks = new(StringComparer.Ordinal);

    public bool TryCameraAt(double time, out PvCamState camera)
    {
        if (_cameraTrack is null)
        {
            camera = PvCamState.Identity;
            return false;
        }

        return CameraDirection.Evaluate(_cameraTrack, time, out camera);
    }

    public bool TryLayerCameraAt(string layerId, double time, out PvCamState camera)
    {
        if (_layerCameraTracks.TryGetValue(layerId, out var track)
            && CameraDirection.Evaluate(track, time, out camera))
        {
            return true;
        }

        camera = PvCamState.Identity;
        return false;
    }

    public IReadOnlyDictionary<ParamAddress, FloatCurve> Curves => _curves;

    public void SetCurve(in ParamAddress addr, FloatCurve curve, string source = "user", bool locked = false)
    {
        _curves[addr] = curve;
        _curveProvenance[addr] = (source, locked);
    }

    public bool RemoveCurve(in ParamAddress addr)
    {
        _curveProvenance.Remove(addr);
        return _curves.Remove(addr);
    }

    public void SetLayer(in ParamAddress addr, string name, CurveBlend blend, FloatCurve curve,
        string source = "user", bool locked = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (!_layers.TryGetValue(addr, out var list)) _layers[addr] = list = new List<CurveLayer>();
        list.RemoveAll(l => string.Equals(l.Name, name, StringComparison.Ordinal));
        list.Add(new CurveLayer(name, blend, curve, source, locked));
    }

    public bool Affects(in ParamAddress addr)
    {
        if (_curves.TryGetValue(addr, out var c) && c.Count > 0) return true;
        if (!_layers.TryGetValue(addr, out var list)) return false;
        foreach (var l in list)
            if (l.Curve.Count > 0) return true;
        return false;
    }

    public bool TryEvaluate(in ParamAddress addr, double time, out double value)
    {
        if (!_curves.TryGetValue(addr, out var curve) || curve.Count == 0)
        {
            value = 0;
            return false;
        }

        value = curve.Evaluate(time);
        return true;
    }

    public double Resolve(in ParamAddress addr, double time, double rest)
    {
        var value = TryEvaluate(addr, time, out var b) ? b : rest;
        if (!_layers.TryGetValue(addr, out var list)) return value;

        foreach (var l in list)
            if (l.Blend == CurveBlend.Add && l.Curve.Count > 0) value += l.Curve.Evaluate(time);
        foreach (var l in list)
            if (l.Blend == CurveBlend.Multiply && l.Curve.Count > 0) value *= l.Curve.Evaluate(time);
        return value;
    }

    public DirectorEvaluator Snapshot()
    {
        var copy = new DirectorEvaluator();
        foreach (var (addr, curve) in _curves) copy._curves[addr] = CloneCurve(curve);
        foreach (var (addr, prov) in _curveProvenance) copy._curveProvenance[addr] = prov;
        foreach (var (addr, list) in _layers)
            copy._layers[addr] = list.Select(l => l with { Curve = CloneCurve(l.Curve) }).ToList();
        copy._cameraTrack = _cameraTrack?.Clone();
        copy._layerCameraTracks = _layerCameraTracks.ToDictionary(
            p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal);

        return copy;
    }

    private static FloatCurve CloneCurve(FloatCurve curve)
    {
        var cloned = new FloatCurve();
        foreach (var key in curve.Keys) cloned.Keys.Add(key);
        return cloned;
    }

    public void RemoveCurvesForTarget(string targetId)
    {
        if (string.IsNullOrEmpty(targetId)) return;

        foreach (var addr in _curves.Keys.Where(a => string.Equals(a.Target, targetId, StringComparison.Ordinal)).ToList())
        {
            _curves.Remove(addr);
            _curveProvenance.Remove(addr);
        }
        foreach (var addr in _layers.Keys.Where(a => string.Equals(a.Target, targetId, StringComparison.Ordinal)).ToList())
            _layers.Remove(addr);
    }

    public static DirectorEvaluator FromSaves(IEnumerable<CurveSave>? saves, CameraTrack? cameraTrack = null,
        Dictionary<string, CameraTrack>? layerCameraTracks = null)
    {
        var eval = new DirectorEvaluator
        {
            _cameraTrack = cameraTrack,
            _layerCameraTracks = layerCameraTracks is null
                ? new Dictionary<string, CameraTrack>(StringComparer.Ordinal)
                : new Dictionary<string, CameraTrack>(layerCameraTracks, StringComparer.Ordinal),
        };
        if (saves is null) return eval;

        foreach (var save in saves)
        {
            if (save?.Curve is null) continue;
            if (!ParamAddress.TryParse(save.Address, out var addr)) continue;
            if (string.IsNullOrEmpty(save.Layer)) eval.SetCurve(addr, save.Curve, save.Source, save.Locked);
            else eval.SetLayer(addr, save.Layer, save.Blend, save.Curve, save.Source, save.Locked);
        }

        return eval;
    }

    public List<CurveSave> ToSaves()
    {
        var saves = new List<CurveSave>();
        foreach (var (addr, curve) in _curves.OrderBy(p => p.Key.ToString(), StringComparer.Ordinal))
        {
            var (source, locked) = _curveProvenance.TryGetValue(addr, out var prov) ? prov : ("user", false);
            saves.Add(new CurveSave { Address = addr.ToString(), Curve = curve, Source = source, Locked = locked });
        }

        foreach (var (addr, list) in _layers.OrderBy(p => p.Key.ToString(), StringComparer.Ordinal))
        foreach (var l in list.OrderBy(l => l.Name, StringComparer.Ordinal))
            saves.Add(new CurveSave
            {
                Address = addr.ToString(),
                Curve = l.Curve,
                Layer = l.Name,
                Blend = l.Blend,
                Source = l.Source,
                Locked = l.Locked,
            });

        return saves;
    }
}
