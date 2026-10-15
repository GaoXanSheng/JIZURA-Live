using System.Runtime.InteropServices;
using JizuraLive.Logging;
using SpoutDXWrapper;
using Vortice.Direct3D11;
using Vortice.DXGI;
using D3DMapFlags = Vortice.Direct3D11.MapFlags;

namespace JizuraLive.Spout;

/// <summary>
/// 外部 Spout 源的接收端（纯 Vortice D3D11 实现，零旧依赖）。
/// 把外部发送端（如 VTS 角色画面）读回内存或直接共享给渲染管线。
/// </summary>
public sealed class SpoutIn : IDisposable
{
    private const int WaitLogMs = 10_000;
    private const int RetryBackoffMs = 2_000;
    private const int MaxFails = 5;

    private readonly ID3D11Device _device;
    private SpoutReceiver? _rx;
    private string _rxName = "";

    private ID3D11Texture2D? _stg;
    private int _srcW = -1, _srcH = -1, _srcFmt = -1;

    private readonly object _pub = new();
    private byte[]? _front;
    private byte[]? _back;
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

    /// <summary>最新帧的 BGRA 原始像素（借用，在下一次 Pump 之前有效）。</summary>
    public byte[]? FrontPixels
    {
        get
        {
            lock (_pub) return _front;
        }
    }

    public bool Pump(bool enabled, string senderName, bool premultiplied = false)
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
            using var src = new ID3D11Texture2D(ptr);
            var d = src.Description;
            if (d.Width <= 0 || d.Height <= 0) return false;

            int w = (int)d.Width;
            int h = (int)d.Height;
            int fmt = (int)d.Format;

            if (!EnsureTargets(w, h, fmt))
            {
                Connected = false;
                return false;
            }

            _device.ImmediateContext.CopyResource(_stg!, src);
            var mapped = _device.ImmediateContext.Map(_stg!, 0, MapMode.Read, D3DMapFlags.None);
            try
            {
                var dst = _back!;
                var rowBytes = w * 4;
                for (var row = 0; row < h; row++)
                {
                    Marshal.Copy(IntPtr.Add(mapped.DataPointer, (int)(row * mapped.RowPitch)), dst, row * rowBytes, rowBytes);
                }
            }
            finally
            {
                _device.ImmediateContext.Unmap(_stg!, 0);
            }

            lock (_pub)
            {
                (_front, _back) = (_back, _front);
                _seq++;
            }

            Connected = true;
            _fails = 0;
            if (_lastWaitLog != 0) _lastWaitLog = 0;
            return true;
        }
        catch (Exception ex)
        {
            Fail("收帧/读回", ex);
            return false;
        }
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

        _stg?.Dispose();
        _stg = null;
        _srcW = _srcH = -1;
        _srcFmt = -1;
        lock (_pub)
        {
            _front = _back = null;
            _seq++;
        }
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
        if (_stg != null && _srcW == w && _srcH == h && _srcFmt == dxgiFormat && _front != null)
            return true;

        _stg?.Dispose();
        _stg = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = (Format)dxgiFormat,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        });

        _srcW = w;
        _srcH = h;
        _srcFmt = dxgiFormat;

        lock (_pub)
        {
            var len = w * h * 4;
            _front = new byte[len];
            _back = new byte[len];
        }

        Log.Info($"[SpoutIn] 源尺寸变更：{w}×{h} (Format {(Format)dxgiFormat})");
        return true;
    }

    public void Dispose()
    {
        Close();
    }
}
