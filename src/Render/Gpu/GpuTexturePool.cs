using Vortice.Direct3D11;
using Vortice.DXGI;

namespace JizuraLive.Render.Gpu;

/// <summary>
/// 效果机的渲染目标池：按 (宽, 高) 复用「纹理 + RTV + SRV」三元组。
///
/// 为什么池化：效果链是 ping-pong 的，每个 pass 都要一张输出面；逐帧新建纹理会持续产生显存分配
/// 与 COM 对象，几百帧后就是可见的卡顿。池化后稳态下零分配。
///
/// 纹理同时带 RTV（当输出）与 SRV（当下一 pass 的输入），这是 ping-pong 的最小配置。
/// </summary>
public sealed class GpuTexturePool : IDisposable
{
    /// <summary>一张可作渲染目标也可作着色器输入的纹理。</summary>
    public sealed class Entry : IDisposable
    {
        public ID3D11Texture2D Texture { get; }
        public ID3D11RenderTargetView Rtv { get; }
        public ID3D11ShaderResourceView Srv { get; }
        public int Width { get; }
        public int Height { get; }

        internal Entry(ID3D11Texture2D texture, ID3D11RenderTargetView rtv, ID3D11ShaderResourceView srv, int w, int h)
        {
            Texture = texture;
            Rtv = rtv;
            Srv = srv;
            Width = w;
            Height = h;
        }

        public void Dispose()
        {
            Srv.Dispose();
            Rtv.Dispose();
            Texture.Dispose();
        }
    }

    private readonly ID3D11Device _device;
    private readonly Dictionary<(int W, int H), Stack<Entry>> _free = new();
    private readonly List<Entry> _all = new();

    public GpuTexturePool(ID3D11Device device) => _device = device;

    /// <summary>取一张 w×h 的渲染目标（同尺寸优先复用）。</summary>
    public Entry Rent(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (_free.TryGetValue((width, height), out var stack) && stack.Count > 0)
            return stack.Pop();

        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1u, 0u),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
        };
        var texture = _device.CreateTexture2D(desc);
        var entry = new Entry(texture, _device.CreateRenderTargetView(texture),
            _device.CreateShaderResourceView(texture), width, height);
        _all.Add(entry);
        return entry;
    }

    /// <summary>归还（同一张可反复租用；Dispose 时统一释放）。</summary>
    public void Return(Entry entry)
    {
        if (!_free.TryGetValue((entry.Width, entry.Height), out var stack))
        {
            stack = new Stack<Entry>();
            _free[(entry.Width, entry.Height)] = stack;
        }

        stack.Push(entry);
    }

    public void Dispose()
    {
        foreach (var e in _all) e.Dispose();
        _all.Clear();
        _free.Clear();
    }
}
