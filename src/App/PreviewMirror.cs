using SkiaSharp;

namespace JizuraLive.App;

/// <summary>
/// 预览镜像（性能专项）：让「预览」直接消费 Spout 泵渲好的 GPU 帧，不再自己用 CPU 重渲同一内容。
///
/// 动机（实测，TEST-我把我唱给你听.jizuralive + 一键随机冷缓存工况）：
///   预览 CPU 光栅 ~15-25ms/帧、Spout GPU 光栅 ~4.7ms/帧，两者共用 EngineRenderGate 串行执行——
///   合计超过 16.7ms 帧预算，输出与预览同时掉到 22-45fps，UI 线程也被挤到 100-3400ms 抖动。
///   现在 Spout 泵在 FlushAndSync 后把出图面经 D3D11 staging 读回（1080p 实测 ~1.7ms；
///   ANGLE 自己的 glReadPixels 同尺寸要 13.6ms，故走 staging 拷贝），预览泵只做「缩放贴显示」
///   （~4ms，在预览线程、不占渲染闸）——两边都满帧，CPU 空出来给 UI。
///   读回正确性：与 Skia ReadPixels 逐字节一致（obj/mirror-dump 对拍）。
///
/// 线程模型（单锁；临界区都是毫秒级拷贝，无嵌套）：
///   预览泵    Request(w,h)      —— 按舞台算出的目标尺寸（≤1 = 关闭镜像，预览走原 CPU 路径）
///   Spout 泵  BeginWrite/EndWrite —— 把读回的帧写 back 缓冲，翻面 + seq++（无请求时零开销）
///   预览泵    WithLatest         —— 借最新整帧做缩放贴显示（持锁期间使用，禁止留存引用）
/// </summary>
public sealed class PreviewMirror
{
    private readonly object _lock = new();
    private int _reqW, _reqH;
    private SKBitmap? _front, _back;
    private long _seq;

    /// <summary>预览泵的镜像请求尺寸（宽或高 ≤1 = 关闭）。</summary>
    public void Request(int w, int h)
    {
        lock (_lock)
        {
            _reqW = w;
            _reqH = h;
        }
    }

    /// <summary>是否要镜像（Spout 泵每帧读一次；无请求时整条读回路径零开销）。</summary>
    public bool Wanted
    {
        get { lock (_lock) return _reqW > 1 && _reqH > 1; }
    }

    /// <summary>最新帧序号（预览泵据此判断有没有新帧）。</summary>
    public long Seq
    {
        get { lock (_lock) return _seq; }
    }

    /// <summary>Spout 泵（GPU 现场、持上下文）：取一块尺寸匹配的可写 back 位图；不需要镜像返回 null。</summary>
    public SKBitmap? BeginWrite(int w, int h)
    {
        lock (_lock)
        {
            if (_reqW <= 1 || _reqH <= 1 || w < 1 || h < 1) return null;
            if (_back == null || _back.Width != w || _back.Height != h)
            {
                _back?.Dispose();
                _back = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            }

            return _back;
        }
    }

    /// <summary>Spout 泵：读回完成，翻面发布。</summary>
    public void EndWrite()
    {
        lock (_lock)
        {
            if (_back == null) return;
            (_front, _back) = (_back, _front);
            _seq++;
        }
    }

    /// <summary>预览泵：借最新整帧（持锁期间使用；期间 Spout 泵的 BeginWrite 会等待，帧不会被写坏）。</summary>
    public void WithLatest(Action<SKBitmap> use)
    {
        lock (_lock)
        {
            if (_front != null) use(_front);
        }
    }
}
