using JizuraLive.Render.Draw;
using Vortice.Mathematics;

namespace JizuraLive.Pv;

/// <summary>
/// PV 舞台：把「场景文档 + 图层提供者 + 驱动器 + 合成器」串成一帧。
///
/// 这是 P3 的核心产物——PV 从「编辑器里的一个模式」变成「一个有自己场景文档、自己图层栈、
/// 自己时钟来源的子系统」：
///   * 它不知道图从哪来（Provider 负责）；
///   * 它不知道音乐从哪来（Driver 负责）；
///   * 它不知道画面怎么被消费（调用方给它一个 Immediate2D 目标）。
/// 三条边界都只有接口，所以换源、换歌单、换输出都不需要动这个类。
/// </summary>
public sealed class PvStage : IDisposable
{
    private readonly Dictionary<string, IPvLayerProvider> _providers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PvLayerImage> _images = new(StringComparer.Ordinal);

    public PvScene Scene { get; }

    /// <summary>本帧的运镜（PV 舞台自己的镜头，与引擎 cut 镜头解耦）。</summary>
    public PvCamState Camera { get; set; } = PvCamState.Identity;

    /// <summary>音乐来源（null = 手动/无时钟，时间为 0）。</summary>
    public IPvDriver? Driver { get; set; }

    /// <summary>本帧的音频能量与节拍相位（由宿主从分析结果填；舞台只消费）。</summary>
    public double Energy { get; set; }
    public double Beat { get; set; }

    /// <summary>最后一帧各层是否出图（诊断/状态条用）。</summary>
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

    /// <summary>合成一帧到给定绘制面。图层配置按 id 绑定提供者；没有提供者的层本帧缺席。</summary>
    public void RenderFrame(Immediate2D draw, int width, int height)
    {
        var ctx = new PvFrameContext(Driver?.Time ?? 0, Beat, Energy, width, height);
        var compositor = new PvStageCompositor(draw);

        _images.Clear();
        foreach (var layer in Scene.Layers)
        {
            if (!layer.Enabled) continue;
            if (!_providers.TryGetValue(layer.Id, out var provider)) continue;
            var image = provider.Pump(ctx);
            if (image.HasContent) _images[layer.Id] = image;
        }

        compositor.Composite(Scene, Camera, _images, width, height);
    }

    /// <summary>建一个默认场景：底色 → 素材/角色 → 歌词 → 前景 → 字幕，深度递增。</summary>
    public static PvStage CreateDefault()
    {
        var stage = new PvStage(PvScene.CreateDefault());
        return stage;
    }

    public void Dispose()
    {
        foreach (var p in _providers.Values) p.Dispose();
        _providers.Clear();
        Driver?.Dispose();
    }
}
