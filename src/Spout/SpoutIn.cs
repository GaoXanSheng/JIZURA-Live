using System.Runtime.InteropServices;
using JizuraLive.Logging;
using SpoutDXWrapper;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace JizuraLive.Spout;

public sealed class SpoutIn : IDisposable
{
    private const int WaitLogMs = 10_000;
    private const int RetryBackoffMs = 2_000;
    private const int MaxFails = 5;

    private readonly ID3D11Device _device;
    private SpoutReceiver? _rx;
    private string _rxName = "";

    private readonly ID3D11Texture2D?[] _bufs = new ID3D11Texture2D?[2];

    private int _front = -1;

    private int _back;

    private long _generation;

    private int _srcW = -1, _srcH = -1, _srcFmt = -1;

    private int _seenFmt = -1;

    private readonly object _pub = new();
    private long _seq;

    private long _lastWaitLog = -WaitLogMs;
    private long _retryAfterTs;
    private int _fails;

    private bool _gaveUp;

    public string LastError { get; private set; } = "";

    public SpoutIn(ID3D11Device device) => _device = device;

    public long Seq => Interlocked.Read(ref _seq);
    public bool Connected { get; private set; }
    public int SourceWidth => _srcW < 0 ? 0 : _srcW;
    public int SourceHeight => _srcH < 0 ? 0 : _srcH;

    public int SourceFormat => _seenFmt;

    public string? FormatDiagnostic { get; private set; }

    public long Generation => Interlocked.Read(ref _generation);

    public ID3D11Texture2D? FrontTexture
    {
        get
        {
            lock (_pub) return _front < 0 ? null : _bufs[_front];
        }
    }

    public int FrontIndex
    {
        get
        {
            lock (_pub) return _front;
        }
    }

    public bool Pump(bool enabled, string senderName)
    {
        if (_gaveUp) return false;
        if (!enabled)
        {
            if (_rx != null) Close();
            return false;
        }

        var now = Environment.TickCount64;
        if (now < _retryAfterTs) return false;

        var name = Normalize(senderName);
        if (_rx == null || !string.Equals(_rxName, name, StringComparison.Ordinal))
        {
            Close();
            if (!Open(name)) return false;
        }

        IntPtr ptr;
        try
        {
            ptr = _rx!.ReceiveTexturePtr();
        }
        catch (Exception ex)
        {
            Fail("ReceiveTexturePtr", ex);
            return false;
        }

        if (ptr == IntPtr.Zero)
        {
            Connected = false;
            if (now - _lastWaitLog >= WaitLogMs)
            {
                _lastWaitLog = now;
                Log.Info($"[SpoutIn] 等待 Spout 源 '{name}'……");
            }
            return false;
        }

        try
        {
            Marshal.AddRef(ptr);
            using var src = new ID3D11Texture2D(ptr);
            var d = src.Description;
            if (d.Width <= 0 || d.Height <= 0) return false;

            int w = (int)d.Width;
            int h = (int)d.Height;
            int fmt = (int)d.Format;

            if (!SpoutSourceFormat.IsSupported(d.Format))
            {
                var changed = _seenFmt != fmt;
                _seenFmt = fmt;
                FormatDiagnostic = SpoutSourceFormat.DescribeUnsupported(d.Format);
                Connected = false;
                ClearFront();
                if (changed) Log.Warn("[SpoutIn] " + FormatDiagnostic);
                return false;
            }

            _seenFmt = fmt;
            FormatDiagnostic = null;

            if (!EnsureTargets(w, h, fmt))
            {
                Connected = false;
                return false;
            }

            var dst = _bufs[_back]!;

            if (!_rx!.ReceiveToExistingTexture(dst.NativePointer))
            {
                Connected = false;
                return false;
            }

            lock (_pub)
            {
                _front = _back;
                _back ^= 1;
                _seq++;
            }

            Connected = true;
            _fails = 0;
            if (_lastWaitLog != 0) _lastWaitLog = 0;
            return true;
        }
        catch (Exception ex)
        {
            Fail("收帧/直收", ex);
            return false;
        }
    }

    private void ClearFront()
    {
        lock (_pub) _front = -1;
    }

    private static string Normalize(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "VTubeStudioSpout" : name.Trim();

    private bool Open(string name)
    {
        try
        {
            var rx = new SpoutReceiver();
            rx.SetReceiverName(name);
            if (!rx.OpenDirectX(_device.NativePointer))
            {
                rx.Dispose();
                Fail($"SpoutReceiver.OpenDirectX 失败（源 '{name}'）", null);
                return false;
            }

            _rx = rx;
            _rxName = name;
            _lastWaitLog = 0;
            Log.Info($"[SpoutIn] 接收端就绪，等待源 '{name}'");
            return true;
        }
        catch (Exception ex)
        {
            Fail($"SpoutReceiver 创建失败（源 '{name}'）", ex);
            return false;
        }
    }

    private void Close()
    {
        Connected = false;
        var rx = _rx;
        _rx = null;
        _rxName = "";
        if (rx != null)
        {
            try { rx.Dispose(); }
            catch (Exception ex) { Log.Debug("[SpoutIn] Receiver.Dispose 失败：" + ex.Message); }
        }

        DisposeBuffers();
        Interlocked.Increment(ref _generation);
        lock (_pub) _seq++;
    }

    private void DisposeBuffers()
    {
        lock (_pub) { _front = -1; _back = 0; }
        for (var i = 0; i < _bufs.Length; i++)
        {
            _bufs[i]?.Dispose();
            _bufs[i] = null;
        }

        _srcW = _srcH = -1;
        _srcFmt = -1;
    }

    private void Fail(string what, Exception? ex)
    {
        _fails++;
        LastError = ex == null ? what : $"{what}：{ex.Message}";
        _retryAfterTs = Environment.TickCount64 + RetryBackoffMs;
        Close();
        if (_fails >= MaxFails)
        {
            _gaveUp = true;
            var msg = $"[SpoutIn] {what} 连续失败 {_fails} 次，停止重试";
            if (ex != null) Log.Error(msg + "：" + ex.Message, ex);
            else Log.Error(msg);
            return;
        }

        Log.Warn($"[SpoutIn] {what}（第 {_fails}/{MaxFails} 次，{RetryBackoffMs}ms 后重试）"
                 + (ex != null ? "：" + ex.Message : ""));
    }

    private bool EnsureTargets(int w, int h, int dxgiFormat)
    {
        if (_bufs[0] != null && _srcW == w && _srcH == h && _srcFmt == dxgiFormat)
            return true;

        DisposeBuffers();

        var desc = new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = (Format)dxgiFormat,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };

        _bufs[0] = _device.CreateTexture2D(desc);
        _bufs[1] = _device.CreateTexture2D(desc);

        _srcW = w;
        _srcH = h;
        _srcFmt = dxgiFormat;
        Interlocked.Increment(ref _generation);

        Log.Info($"[SpoutIn] 源尺寸变更：{w}×{h} (Format {(Format)dxgiFormat})");
        return true;
    }

    public void Dispose()
    {
        Close();
    }
}
