using JizuraLive.Render.Draw;
using Vortice.Mathematics;

namespace JizuraLive.Pv;

/// <summary>
/// PV 舞台：把「场景文档 + 图层提供者 + 帧快照 + 合成器」串成一帧。
///
/// 这是 P3 的核心产物——PV 从「编辑器里的一个模式」变成「一个有自己场景文档、自己图层栈、
/// 自己帧快照的子系统」：
///   * 它不知道图从哪来（Provider 负责）；
///   * 它不知道音乐从哪来（宿主每帧给一份 <see cref="PvFrameContext"/>，舞台不回读时钟）；
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

    /// <summary>
    /// 合成一帧到给定绘制面。图层配置按 id 绑定提供者；没有提供者的层本帧缺席。
    /// 时间、能量、节拍全部来自 <paramref name="ctx"/>——舞台内不许再自己读时钟，
    /// 否则同一帧的歌词贴图与着色器相位会各拿一个时刻。
    /// </summary>
    public void RenderFrame(Immediate2D draw, in PvFrameContext ctx)
    {
        var compositor = new PvStageCompositor(draw);

        _images.Clear();
        foreach (var layer in Scene.Layers)
        {
            if (!layer.Enabled) continue;
            if (!_providers.TryGetValue(layer.Id, out var provider)) continue;
            var image = provider.Pump(ctx);
            if (image.HasContent) _images[layer.Id] = image;
        }

        // 固定分区（动效歌词/清晰字幕）无条件泵出：它们不在用户图层列表里，
        // 只认 Scene.Layers 的话 provider 注册了也永远不上屏（歌词一次都没显示过的根因）
        foreach (var id in PvStageCompositor.FixedZones)
        {
            if (_images.ContainsKey(id)) continue;
            if (!_providers.TryGetValue(id, out var zone)) continue;
            var zi = zone.Pump(ctx);
            if (zi.HasContent) _images[id] = zi;
        }

        compositor.Composite(Scene, Camera, _images, ctx.Width, ctx.Height);
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
    }
}
