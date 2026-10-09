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
    private long _consumedSeq;

    // 新帧信号（EndWrite 发布 → 预览泵 WaitNewFrame 消费）。不设上限：预览泵落后时许可累积，
    // Wait 立即返回（退回「自己按节拍器跑」），不会把发布方堵住。
    private readonly SemaphoreSlim _frameReady = new(0);

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

    /// <summary>
    /// Spout 泵（GPU 现场、持上下文）：取一块尺寸匹配的可写 back 位图；不需要镜像返回 null。
    /// **消费闸**：上一帧还没被预览取走时返回 null——读回是 GPU 同步点（1080p 的
    /// CopyResource+MapSubresource 实测 ~3ms 且会等 GPU 落盘），预览泵跟不上的时候逐帧读回纯属
    /// 白烧 GPU/PCIe。预览是唯一的消费方，读回速率按它对齐即可。
    /// </summary>
    public SKBitmap? BeginWrite(int w, int h)
    {
        lock (_lock)
        {
            if (_reqW <= 1 || _reqH <= 1 || w < 1 || h < 1) return null;
            if (_seq != _consumedSeq) return null; // 上一帧还压在预览侧，跳过本次读回
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
        var published = false;
        lock (_lock)
        {
            if (_back != null)
            {
                (_front, _back) = (_back, _front);
                _seq++;
                published = true;
            }
        }
        // 唤醒预览泵：新帧就绪（许可数 = 已发布 - 已消费；预览泵慢了会累积，此时 Wait 立即返回）
        if (published) _frameReady.Release();
    }

    /// <summary>
    /// 预览泵：等一拍新帧（有界等待，ms 为上限；返回是否等到）。
    /// 预览泵有自己的节拍器、Spout 泵也有，两套节拍器错拍时同一帧会被贴两次、下一帧被跳过——
    /// 画面看着「一顿一顿」（Spout 输出本身是满帧的，所以 OBS 里反而顺）。
    /// 贴之前等一小拍新帧，就能与发布同拍：每次发布恰好消费一次。
    /// </summary>
    public bool WaitNewFrame(int ms) => _frameReady.Wait(ms);

    private IntPtr _gpuShareHandle = IntPtr.Zero;
    private int _gpuW, _gpuH;

    /// <summary>GPU 纯显存模式：Spout 泵渲染完成，发布最新 D3D11 共享句柄（零 CPU 拷贝）。</summary>
    public void PublishGpu(IntPtr shareHandle, int w, int h)
    {
        lock (_lock)
        {
            _gpuShareHandle = shareHandle;
            _gpuW = w;
            _gpuH = h;
            _seq++;
        }
        _frameReady.Release();
    }

    /// <summary>获取当前最新 GPU 共享帧信息（有可用句柄返回 true）。</summary>
    public bool TryGetGpuFrame(out IntPtr shareHandle, out int w, out int h, out long seq)
    {
        lock (_lock)
        {
            shareHandle = _gpuShareHandle;
            w = _gpuW;
            h = _gpuH;
            seq = _seq;
            return shareHandle != IntPtr.Zero;
        }
    }

    /// <summary>预览泵：借最新整帧（持锁期间使用；期间 Spout 泵的 BeginWrite 会等待，帧不会被写坏）。</summary>
    public void WithLatest(Action<SKBitmap> use)
    {
        lock (_lock)
        {
            _consumedSeq = _seq; // 消费闸：取走这一帧后，Spout 泵才允许读回下一帧
            if (_front != null) use(_front);
        }
    }
}
