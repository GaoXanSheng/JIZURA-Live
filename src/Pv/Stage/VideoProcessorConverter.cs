using JizuraLive.Render.Gpu;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace JizuraLive.Pv;

internal sealed class VideoProcessorConverter : IDisposable
{
    private const int OutputCount = 3;

    private const string Nv12Shader = """
        float4 PSMain(VSOut i) : SV_Target
        {
            float y = Tex.Sample(Smp, i.uv).r;
            float2 c = Tex1.Sample(Smp, i.uv).rg;
            y = (y - 16.0 / 255.0) * (255.0 / 219.0);
            c -= 0.5;
            float r = y + 1.5748 * c.y;
            float g = y - 0.1873 * c.x - 0.4681 * c.y;
            float b = y + 1.8556 * c.x;
            return float4(saturate(float3(r, g, b)), 1.0);
        }
        """;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11Device3 _device3;
    private readonly ShaderLibrary _shaders;
    private readonly FullScreenPass _pass;
    private readonly ID3D11PixelShader _shader;
    private readonly int _outputWidth;
    private readonly int _outputHeight;
    private readonly ID3D11Texture2D[] _outputs;
    private readonly ID3D11RenderTargetView[] _outputViews;
    private readonly ID3D11Query[] _queries;
    private readonly bool[] _pending = new bool[OutputCount];

    public IntPtr[] SharedHandles { get; }
    public int OutputWidth => _outputWidth;
    public int OutputHeight => _outputHeight;
    public int WrittenIndex { get; private set; }

    private ID3D11Texture2D? _copy;
    private ID3D11ShaderResourceView? _yView;
    private ID3D11ShaderResourceView? _uvView;
    private int _next;
    private int _inputWidth;
    private int _inputHeight;

    private VideoProcessorConverter(ID3D11Device device, ID3D11DeviceContext context,
        ID3D11Texture2D[] outputs, ID3D11RenderTargetView[] outputViews, ID3D11Query[] queries,
        IntPtr[] sharedHandles, int outputWidth, int outputHeight)
    {
        _device = device;
        _context = context;
        _device3 = device.QueryInterface<ID3D11Device3>();
        _shaders = new ShaderLibrary(device);
        _pass = new FullScreenPass(device, context, _shaders);
        _shader = _shaders.GetPixel("nv12-to-bgra", Nv12Shader);
        _outputs = outputs;
        _outputViews = outputViews;
        _queries = queries;
        SharedHandles = sharedHandles;
        _outputWidth = outputWidth;
        _outputHeight = outputHeight;
    }

    public static VideoProcessorConverter? TryCreate(ID3D11Device device, ID3D11DeviceContext context,
        int inputWidth, int inputHeight, int outputWidth, int outputHeight, out string error)
    {
        error = "";
        var outputs = new ID3D11Texture2D?[OutputCount];
        var views = new ID3D11RenderTargetView?[OutputCount];
        var queries = new ID3D11Query?[OutputCount];
        var handles = new IntPtr[OutputCount];
        try
        {
            for (var i = 0; i < OutputCount; i++)
            {
                var output = device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)outputWidth,
                    Height = (uint)outputHeight,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.Shared,
                });
                outputs[i] = output;
                views[i] = device.CreateRenderTargetView(output);
                queries[i] = device.CreateQuery(new QueryDescription(QueryType.Event));
                using var resource = output.QueryInterface<IDXGIResource>();
                handles[i] = resource.SharedHandle;
            }

            var converter = new VideoProcessorConverter(device, context,
                outputs.Select(o => o!).ToArray(), views.Select(v => v!).ToArray(),
                queries.Select(q => q!).ToArray(), handles, outputWidth, outputHeight);
            converter.EnsureInput(inputWidth, inputHeight);
            return converter;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            foreach (var query in queries) query?.Dispose();
            foreach (var view in views) view?.Dispose();
            foreach (var output in outputs) output?.Dispose();
            return null;
        }
    }

    public ID3D11Texture2D? Convert(ID3D11Texture2D source)
    {
        var desc = source.Description;
        EnsureInput((int)desc.Width, (int)desc.Height);
        if (_copy is null || _yView is null || _uvView is null) return null;

        var index = AcquireOutput();
        if (index < 0) return null;

        _context.CopyResource(_copy, source);
        _pass.Run(_yView, _uvView, _outputViews[index], _outputWidth, _outputHeight, _shader,
            ReadOnlySpan<float>.Empty);
        _context.End(_queries[index]);
        _pending[index] = true;
        WrittenIndex = index;
        return _outputs[index];
    }

    private int AcquireOutput()
    {
        for (var attempt = 0; attempt < OutputCount; attempt++)
        {
            var index = (_next + attempt) % OutputCount;
            if (_pending[index])
            {
                var result = _context.GetData(_queries[index], IntPtr.Zero, 0,
                    AsyncGetDataFlags.DoNotFlush);
                if (result.Code != 0) continue;
                _pending[index] = false;
            }

            _next = (index + 1) % OutputCount;
            return index;
        }

        return -1;
    }

    private void EnsureInput(int width, int height)
    {
        if (_copy is not null && _inputWidth == width && _inputHeight == height) return;

        _yView?.Dispose();
        _yView = null;
        _uvView?.Dispose();
        _uvView = null;
        _copy?.Dispose();
        _copy = null;

        _copy = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });

        _yView = _device3.CreateShaderResourceView1(_copy,
            new ShaderResourceViewDescription1(_copy, ShaderResourceViewDimension.Texture2D,
                Format.R8_UNorm, 0, 1, 0, 1, 0));
        _uvView = _device3.CreateShaderResourceView1(_copy,
            new ShaderResourceViewDescription1(_copy, ShaderResourceViewDimension.Texture2D,
                Format.R8G8_UNorm, 0, 1, 0, 1, 1));
        _inputWidth = width;
        _inputHeight = height;
    }

    public void Dispose()
    {
        foreach (var query in _queries) query.Dispose();
        _uvView?.Dispose();
        _yView?.Dispose();
        _copy?.Dispose();
        foreach (var view in _outputViews) view.Dispose();
        foreach (var output in _outputs) output.Dispose();
        _pass.Dispose();
        _shaders.Dispose();
        _device3.Dispose();
    }
}
