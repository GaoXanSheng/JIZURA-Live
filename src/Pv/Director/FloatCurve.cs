using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JizuraLive.Pv;

public enum CurveInterp
{
    Step,

    Linear,

    SmoothStep,

    Bezier,

    Spring,
}

[JsonConverter(typeof(CurveKeyJsonConverter))]
public readonly record struct CurveKey(double Time, double Value, CurveInterp Mode,
    double OutX = 0.333, double OutY = 0, double InX = 0.333, double InY = 0);

public sealed class CurveKeyJsonConverter : JsonConverter<CurveKey>
{
    private const double DefaultHandleX = 0.333;

    public override CurveKey ReadJson(JsonReader reader, Type objectType, CurveKey existingValue,
        bool hasExistingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null) return default;

        var obj = JObject.Load(reader);
        return new CurveKey(
            (double?)obj["Time"] ?? 0,
            (double?)obj["Value"] ?? 0,
            (CurveInterp?)(int?)obj["Mode"] ?? CurveInterp.Step,
            (double?)obj["OutX"] ?? DefaultHandleX,
            (double?)obj["OutY"] ?? 0,
            (double?)obj["InX"] ?? DefaultHandleX,
            (double?)obj["InY"] ?? 0);
    }

    public override void WriteJson(JsonWriter writer, CurveKey value, JsonSerializer serializer)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("Time");
        writer.WriteValue(value.Time);
        writer.WritePropertyName("Value");
        writer.WriteValue(value.Value);
        writer.WritePropertyName("Mode");
        writer.WriteValue((int)value.Mode);
        writer.WritePropertyName("OutX");
        writer.WriteValue(value.OutX);
        writer.WritePropertyName("OutY");
        writer.WriteValue(value.OutY);
        writer.WritePropertyName("InX");
        writer.WriteValue(value.InX);
        writer.WritePropertyName("InY");
        writer.WriteValue(value.InY);
        writer.WriteEndObject();
    }
}

public sealed class FloatCurve
{
    private const double TimeEpsilon = 1e-6;

    private const double BezierParameterTolerance = 1e-6;

    public List<CurveKey> Keys { get; } = new();

    public int Count => Keys.Count;

    public bool TryAdd(double time, double value, CurveInterp mode = CurveInterp.Linear)
    {
        if (FindNear(time) >= 0) return false;

        Keys.Insert(InsertionIndex(time), new CurveKey(time, value, mode));
        return true;
    }

    public bool Remove(double time)
    {
        var index = FindNear(time);
        if (index < 0) return false;

        Keys.RemoveAt(index);
        return true;
    }

    public void NormalizeKeys()
    {
        if (Keys.Count < 2) return;

        Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
        for (var i = Keys.Count - 1; i > 0; i--)
        {
            if (Keys[i].Time - Keys[i - 1].Time < TimeEpsilon) Keys.RemoveAt(i);
        }
    }

    public double Evaluate(double time)
    {
        if (Keys.Count == 0)
            throw new InvalidOperationException(
                "空曲线没有可求的值：先 TryAdd 关键帧，或改走 DirectorEvaluator.TryEvaluate 这类容错入口。");

        var first = Keys[0];
        if (time <= first.Time) return first.Value;
        var last = Keys[^1];
        if (time >= last.Time) return last.Value;

        int lo = 0, hi = Keys.Count - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) >> 1;
            if (Keys[mid].Time <= time) lo = mid;
            else hi = mid;
        }

        var a = Keys[lo];
        var b = Keys[lo + 1];
        if (time == a.Time) return a.Value;

        var span = b.Time - a.Time;
        if (span <= 0) return a.Value;

        return Interpolate(a, b, (time - a.Time) / span);
    }

    private static double Interpolate(in CurveKey a, in CurveKey b, double u)
    {
        if (u <= 0) return a.Value;
        if (u >= 1) return b.Value;

        var delta = b.Value - a.Value;
        return a.Mode switch
        {
            CurveInterp.Step => a.Value,
            CurveInterp.SmoothStep => a.Value + delta * (u * u * (3 - 2 * u)),
            CurveInterp.Bezier => a.Value + delta * BezierRatio(u, a, b),
            CurveInterp.Spring => a.Value + delta * SpringRatio(u),
            _ => a.Value + delta * u,
        };
    }

    private static double BezierRatio(double u, in CurveKey a, in CurveKey b)
    {
        var x1 = Math.Clamp(a.OutX, 0, 1);
        var x2 = 1 - Math.Clamp(b.InX, 0, 1);

        double lo = 0, hi = 1;
        while (hi - lo > BezierParameterTolerance)
        {
            var mid = (lo + hi) * 0.5;
            if (CubicBezier(mid, x1, x2) < u) lo = mid;
            else hi = mid;
        }

        return CubicBezier((lo + hi) * 0.5, a.OutY, 1 - b.InY);
    }

    private static double CubicBezier(double w, double c1, double c2)
    {
        var inv = 1 - w;
        return 3 * inv * inv * w * c1 + 3 * inv * w * w * c2 + w * w * w;
    }

    private static double SpringRatio(double u)
    {
        static double Q(double x) => Math.Exp(-5 * x) * Math.Cos(12 * x);
        return (1 - Q(u)) / (1 - Q(1));
    }

    private int InsertionIndex(double time)
    {
        int lo = 0, hi = Keys.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (Keys[mid].Time < time) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    private int FindNear(double time)
    {
        var index = InsertionIndex(time);
        if (index < Keys.Count && Keys[index].Time - time < TimeEpsilon) return index;
        if (index > 0 && time - Keys[index - 1].Time < TimeEpsilon) return index - 1;
        return -1;
    }
}
