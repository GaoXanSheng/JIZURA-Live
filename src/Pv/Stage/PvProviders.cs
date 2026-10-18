using JizuraLive.Render.Draw;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace JizuraLive.Pv;

public readonly record struct PvFrameContext(
    double Time,
    double Beat,
    double Energy,
    int Width,
    int Height);

public interface IPvLayerProvider : IDisposable
{
    string Id { get; }

    long Version { get; }

    PvLayerImage Pump(in PvFrameContext ctx);
}

