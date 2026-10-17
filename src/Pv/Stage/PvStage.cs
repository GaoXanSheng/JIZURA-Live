using JizuraLive.Render.Draw;
using Vortice.Mathematics;

namespace JizuraLive.Pv;

public sealed class PvStage : IDisposable
{
    private readonly Dictionary<string, IPvLayerProvider> _providers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PvLayerImage> _images = new(StringComparer.Ordinal);

    public PvScene Scene { get; }

    public PvCamState Camera { get; set; } = PvCamState.Identity;

    public double Energy { get; set; }
    public double Beat { get; set; }

    public IReadOnlyDictionary<string, PvLayerImage> LastImages => _images;

    public PvStage(PvScene scene) => Scene = scene;

    public void Register(IPvLayerProvider provider) => _providers[provider.Id] = provider;

    public IPvLayerProvider? Get(string id) => _providers.GetValueOrDefault(id);

    public bool Remove(string id)
    {
        if (!_providers.Remove(id, out var p)) return false;
        p.Dispose();
        return true;
    }

    public void RenderFrame(Immediate2D draw, in PvFrameContext ctx)
    {
        PumpImages(ctx);
        new PvStageCompositor(draw).Composite(Scene, Camera, _images, ctx.Width, ctx.Height);
    }

    public void PumpImages(in PvFrameContext ctx)
    {
        _images.Clear();
        var layers = Scene.Layers;
        for (var i = 0; i < layers.Count; i++)
        {
            var layer = layers[i];
            if (!layer.Enabled) continue;
            if (!_providers.TryGetValue(layer.Id, out var provider)) continue;
            var image = provider.Pump(ctx);
            if (image.HasContent) _images[layer.Id] = image;
        }

        foreach (var id in PvStageCompositor.FixedZones)
        {
            if (_images.ContainsKey(id)) continue;
            if (!_providers.TryGetValue(id, out var zone)) continue;
            var zi = zone.Pump(ctx);
            if (zi.HasContent) _images[id] = zi;
        }
    }

    public static PvStage CreateDefault()
    {
        var stage = new PvStage(PvScene.CreateDefault());
        return stage;
    }

    public void Dispose()
    {
        foreach (var p in _providers.Values) p.Dispose();
        _providers.Clear();
    }
}
