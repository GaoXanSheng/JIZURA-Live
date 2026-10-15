using System.IO;
using System.Runtime.InteropServices;

namespace JizuraLive.Pv;

/// <summary>
/// 视频文件解码器：Media Foundation SourceReader 的最小 P/Invoke 封装，解出 RGB32 内存帧。
/// COM 接口按 vtable 槽位手写（槽位序照 Windows SDK 的 mfreadwrite.h / mfobjects.h），
/// 与 src/Spout/SpoutIn.cs 的原生互作同一种风格，不引入第三方 MF 包装库。
///
/// 只做同步「取某一时刻的帧」：不接异步回调、不开音频轨、不走硬件解码（MF 默认软件解码）。
/// 非线程安全：调用方串行用（渲染线程），与 <see cref="FileVideoProvider"/> 的用法一致。
/// </summary>
public sealed class MfVideoDecoder : IDisposable
{
    // ---- vtable 槽位 ----
    // IUnknown: 0..2。IMFAttributes: 3 GetItem … 7 GetUINT32, 8 GetUINT64, 21 SetUINT32, 22 SetUINT64, 24 SetGUID。
    // IMFSourceReader: 5 GetNativeMediaType, 6 GetCurrentMediaType, 7 SetCurrentMediaType,
    //                  8 SetCurrentPosition, 9 ReadSample, 12 GetPresentationAttribute。
    // IMFMediaBuffer: 3 Lock, 4 Unlock。
    // IMFSample（继 IMFAttributes）: 35 GetSampleTime, 37 GetSampleDuration, 41 ConvertToContiguousBuffer。
    private const int SlotRelease = 2;
    private const int SlotGetUInt32 = 7;
    private const int SlotGetUInt64 = 8;
    private const int SlotSetUInt32 = 21;
    private const int SlotSetUInt64 = 22;
    private const int SlotSetGuid = 24;
    private const int SlotReaderGetNativeType = 5;
    private const int SlotReaderGetCurrentType = 6;
    private const int SlotReaderSetCurrentType = 7;
    private const int SlotReaderSetPosition = 8;
    private const int SlotReaderReadSample = 9;
    private const int SlotReaderGetPresentation = 12;
    private const int SlotBufferLock = 3;
    private const int SlotBufferUnlock = 4;
    private const int SlotSampleGetTime = 35;
    private const int SlotSampleGetDuration = 37;
    private const int SlotSampleContiguous = 41;

    private const int FirstVideoStream = -4;      // MF_SOURCE_READER_FIRST_VIDEO_STREAM
    private const int MediaSource = -1;           // MF_SOURCE_READER_MEDIASOURCE（时长挂在它身上）
    private const uint FlagError = 0x1;           // MF_SOURCE_READERF_ERROR
    private const uint MfVersion = 0x0002_0070;   // MF_VERSION = MF_SDK_VERSION << 16 | MF_API_VERSION
    private const ushort VtI8 = 20;
    private const ushort VtUi8 = 21;
    private const long HnsPerSecond = 10_000_000;
    private const uint CoInitMultithreaded = 0x0; // COINIT_MULTITHREADED

    /// <summary>
    /// 向前跳进超过这个秒数就重新定位，不走顺序解：顺序解的代价随跳进线性涨（暂停十分钟再恢复会把整段片子顺序解一遍）。
    /// 量级取自实测——x264 默认 keyint=250，60fps 下关键帧间隔约 4.2s，seek 的额外回退代价与之同量级。
    /// </summary>
    private const double ForwardSeekThreshold = 2.0;

    private static readonly Guid EnableVideoProcessing = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    private static readonly Guid MajorTypeKey = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid SubtypeKey = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid FrameSizeKey = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid FrameRateKey = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid DefaultStrideKey = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid DurationKey = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
    private static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid VideoFormatRgb32 = new("00000016-0000-0010-8000-00aa00389b71"); // D3DFMT_X8R8G8B8

    private static readonly object PlatformGate = new();
    private static int _platformRefs;

    private IntPtr _reader;
    private bool _platformRetained;
    private double _lastPts = double.NaN;
    private double _lastDuration;

    private SetPositionFn? _setPosition;
    private ReadSampleFn? _readSample;
    private GetSampleTimeFn? _sampleTime;
    private GetSampleDurationFn? _sampleDuration;
    private ContiguousBufferFn? _toContiguous;
    private LockFn? _bufferLock;
    private UnlockFn? _bufferUnlock;

    public int Width { get; }
    public int Height { get; }

    /// <summary>时长（秒）；取不到时为 0。</summary>
    public double Duration { get; }

    /// <summary>帧率（帧/秒）；取不到时为 0。</summary>
    public double FrameRate { get; }

    /// <summary>帧数估值（时长 × 帧率，四舍五入）；时长或帧率缺失时为 0。</summary>
    public int FrameCount { get; }

    /// <summary>输出帧的字节数（恒 w×4×h，上传纹理按它分配缓冲）。</summary>
    public int FrameBytes => Width * 4 * Height;

    /// <summary>
    /// 输出行的字节跨度：负值表示画面自下而上（MF 的 RGB32 输出常见如此，照 GDI DIB 的约定），拷帧时按它翻行。
    /// </summary>
    public int Stride { get; }

    /// <summary>最近一次失败的读数（null = 正常）。</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 最近一次成功取到的帧的时刻（秒）；还没取到过为 NaN。
    /// 调用方拿它判断"这一帧是不是上一帧" —— 同值时 buffer 里的内容与上次完全一样。
    /// </summary>
    public double LastFramePts => _lastPts;

    private MfVideoDecoder(IntPtr reader, int width, int height, double duration, double frameRate, int stride)
    {
        _reader = reader;
        _platformRetained = true;
        Width = width;
        Height = height;
        Duration = duration;
        FrameRate = frameRate;
        FrameCount = duration > 0 && frameRate > 0 ? (int)Math.Round(duration * frameRate) : 0;
        Stride = stride != 0 ? stride : width * 4;
    }

    /// <summary>
    /// 打开一个视频文件；失败返回 null 并给出可读原因（不抛）。
    /// 输出格式固定为 RGB32（字节序 BGRA，同 DXGI_FORMAT_B8G8R8A8_UNorm）。
    /// </summary>
    public static MfVideoDecoder? TryOpen(string path, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "视频路径为空";
            return null;
        }

        if (!File.Exists(path))
        {
            error = $"视频文件不存在：{path}";
            return null;
        }

        var reader = IntPtr.Zero;
        var attributes = IntPtr.Zero;
        var nativeType = IntPtr.Zero;
        var outputType = IntPtr.Zero;
        var currentType = IntPtr.Zero;
        MfVideoDecoder? decoder = null;
        try
        {
            if (!TryRetainPlatform(out error)) return null;

            Ensure(MFCreateAttributes(out attributes, 1), "MFCreateAttributes");
            // 这一条才是"能索要 RGB32"的前提：它启用的视频处理（软件色彩转换）负责 NV12/YUY2 → RGB32，
            // 不加则 SetCurrentMediaType 直接拒掉非 RGB32 的源。
            var enableKey = EnableVideoProcessing;
            Ensure(Slot<SetUInt32Fn>(attributes, SlotSetUInt32)(attributes, ref enableKey, 1),
                "设置 MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING");
            Ensure(MFCreateSourceReaderFromURL(path, attributes, out reader), "MFCreateSourceReaderFromURL");

            var width = 0;
            var height = 0;
            var frameRate = 0.0;
            if (Slot<GetNativeTypeFn>(reader, SlotReaderGetNativeType)(reader, FirstVideoStream, 0, out nativeType) >= 0
                && nativeType != IntPtr.Zero)
            {
                ReadFrameSize(nativeType, out width, out height);
                ReadFrameRate(nativeType, out frameRate);
            }

            if (width <= 0 || height <= 0) throw new InvalidOperationException($"读不到视频尺寸：{path}");

            // 索要 RGB32：只给主类型 + 子类型 + 尺寸，步长/交错交给 SourceReader 自己谈（谈完再从输出类型读回步长）。
            Ensure(MFCreateMediaType(out outputType), "MFCreateMediaType");
            var majorKey = MajorTypeKey;
            var subtypeKey = SubtypeKey;
            var videoType = MediaTypeVideo;
            var rgb32 = VideoFormatRgb32;
            var frameSizeKey = FrameSizeKey;
            var setGuid = Slot<SetGuidFn>(outputType, SlotSetGuid);
            Ensure(setGuid(outputType, ref majorKey, ref videoType), "设置 MF_MT_MAJOR_TYPE");
            Ensure(setGuid(outputType, ref subtypeKey, ref rgb32), "设置 MF_MT_SUBTYPE");
            Ensure(Slot<SetUInt64Fn>(outputType, SlotSetUInt64)(outputType, ref frameSizeKey, Pack(width, height)),
                "设置 MF_MT_FRAME_SIZE");
            Ensure(Slot<SetCurrentTypeFn>(reader, SlotReaderSetCurrentType)(reader, FirstVideoStream, IntPtr.Zero, outputType),
                "SetCurrentMediaType(RGB32)");

            var durationKey = DurationKey;
            var duration = 0.0;
            if (Slot<GetPresentationFn>(reader, SlotReaderGetPresentation)(reader, MediaSource, ref durationKey, out var pv) >= 0)
            {
                if (pv.Vt is VtI8 or VtUi8) duration = pv.Value / (double)HnsPerSecond;
                PropVariantClear(ref pv);
            }

            var stride = 0;
            if (Slot<GetCurrentTypeFn>(reader, SlotReaderGetCurrentType)(reader, FirstVideoStream, out currentType) >= 0
                && currentType != IntPtr.Zero)
            {
                var strideKey = DefaultStrideKey;
                // MF_MT_DEFAULT_STRIDE 里存的是 INT32（步长可为负），按 UINT32 读回来只是原样搬运
                if (Slot<GetUInt32Fn>(currentType, SlotGetUInt32)(currentType, ref strideKey, out var raw) >= 0)
                    stride = unchecked((int)raw);
            }

            decoder = new MfVideoDecoder(reader, width, height, duration, frameRate, stride);
            reader = IntPtr.Zero; // 所有权转给 decoder
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            Release(currentType);
            Release(outputType);
            Release(nativeType);
            Release(attributes);
            if (decoder is null)
            {
                Release(reader);
                ReleasePlatform();
            }
        }

        return decoder;
    }

    /// <summary>
    /// 把 <paramref name="seconds"/> 处的帧解到 <paramref name="buffer"/>（长度须 ≥ <see cref="FrameBytes"/>）。
    /// 播放（时间前进一点）走顺序读，每次一帧；回退、回绕、跳远才重新定位。
    /// 定位是**关键帧粒度**的：实测 seek 到 5.0s 后第一帧是 4.2s，所以定位后要一直读到覆盖目标时刻的那一帧。
    /// 目标仍落在上一帧的展示区间内时直接返回 true、buffer 不动 —— 少了这一条，60fps 的取帧节奏
    /// 会把 30fps 的源每次读两帧，整段播成 2 倍速（调用方因此必须始终传同一个缓冲）。
    /// </summary>
    public bool CopyFrameAt(double seconds, byte[] buffer)
    {
        if (_reader == IntPtr.Zero) return false;
        if (buffer is null || buffer.Length < FrameBytes)
        {
            LastError = $"帧缓冲太小：需要 {FrameBytes} 字节，给了 {buffer?.Length ?? 0}";
            return false;
        }

        if (!double.IsFinite(seconds)) return false;
        seconds = Math.Max(0, seconds);

        var known = !double.IsNaN(_lastPts);
        if (known && seconds >= _lastPts && seconds < _lastPts + _lastDuration) return true;
        if (!known || seconds < _lastPts || seconds - _lastPts > ForwardSeekThreshold)
        {
            _lastPts = double.NaN;
            if (!Seek(seconds)) return false;
        }

        _readSample ??= Slot<ReadSampleFn>(_reader, SlotReaderReadSample);
        // 解到目标时刻为止：seek 可能落在目标之前（关键帧），得继续往前读；读到流尾自然结束。
        while (true)
        {
            var hr = _readSample(_reader, FirstVideoStream, 0, out _, out var flags, out _, out var sample);
            if (hr < 0)
            {
                LastError = $"ReadSample 失败：{Describe(hr)}";
                return false;
            }

            if (sample == IntPtr.Zero)
            {
                // 流尾 / 无样本：本帧没有内容（有错误标志时留一条可读原因）
                if ((flags & FlagError) != 0) LastError = "源在读帧时报错（流损坏或解码失败）";
                return false;
            }

            try
            {
                if (!TryCopySample(sample, buffer, out var pts, out var duration)) continue;
                _lastPts = pts;
                _lastDuration = duration;
                LastError = null;
                // pts + duration > seconds：这一帧就是 target 时刻该显示的那一帧
                if (pts >= seconds || pts + duration > seconds) return true;
            }
            finally
            {
                Release(sample);
            }
        }
    }

    private bool Seek(double seconds)
    {
        _setPosition ??= Slot<SetPositionFn>(_reader, SlotReaderSetPosition);
        var position = new PropVariant { Vt = VtI8, Value = (long)(seconds * HnsPerSecond) };
        var timeFormat = Guid.Empty; // GUID_NULL = 以 100ns 为单位、相对文件开头
        var hr = _setPosition(_reader, ref timeFormat, ref position);
        if (hr < 0)
        {
            LastError = $"定位到 {seconds:F3}s 失败：{Describe(hr)}";
            return false;
        }

        return true;
    }

    private bool TryCopySample(IntPtr sample, byte[] buffer, out double pts, out double duration)
    {
        _sampleTime ??= Slot<GetSampleTimeFn>(sample, SlotSampleGetTime);
        _sampleDuration ??= Slot<GetSampleDurationFn>(sample, SlotSampleGetDuration);
        _sampleTime(sample, out var ptsHns);
        _sampleDuration(sample, out var durationHns);
        pts = ptsHns / (double)HnsPerSecond;
        duration = durationHns > 0
            ? durationHns / (double)HnsPerSecond
            : FrameRate > 0 ? 1.0 / FrameRate : 0.0; // 样本不带时长就用帧率补，否则上面那个复用区间恒为空

        _toContiguous ??= Slot<ContiguousBufferFn>(sample, SlotSampleContiguous);
        if (_toContiguous(sample, out var mediaBuffer) < 0 || mediaBuffer == IntPtr.Zero) return false;

        try
        {
            _bufferLock ??= Slot<LockFn>(mediaBuffer, SlotBufferLock);
            _bufferUnlock ??= Slot<UnlockFn>(mediaBuffer, SlotBufferUnlock);
            if (_bufferLock(mediaBuffer, out var data, out _, out var length) < 0) return false;
            try
            {
                return CopyRows(data, (int)length, buffer);
            }
            finally
            {
                _bufferUnlock(mediaBuffer);
            }
        }
        finally
        {
            Release(mediaBuffer);
        }
    }

    /// <summary>
    /// 整行按步长拷贝，并把每像素第 4 字节钉成 255：MFVideoFormat_RGB32 的第 4 字节是 X（未定义，
    /// 常见为 0），而 D2D 位图按预乘解释 —— A=0 会让整层全透明（是黑屏，不是报错）。
    /// </summary>
    private bool CopyRows(IntPtr source, int length, byte[] destination)
    {
        var rowBytes = Width * 4;
        var pitch = Math.Abs(Stride);
        var bottomUp = Stride < 0;
        if (length < pitch * Height) return false; // 数据不完整，不按整帧读

        for (var y = 0; y < Height; y++)
        {
            var sourceRow = bottomUp ? Height - 1 - y : y;
            var offset = y * rowBytes;
            Marshal.Copy(source + sourceRow * pitch, destination, offset, rowBytes);
            for (var i = offset + 3; i < offset + rowBytes; i += 4) destination[i] = 255;
        }

        return true;
    }

    private static void ReadFrameSize(IntPtr mediaType, out int width, out int height)
    {
        width = 0;
        height = 0;
        var key = FrameSizeKey;
        if (Slot<GetUInt64Fn>(mediaType, SlotGetUInt64)(mediaType, ref key, out var packed) < 0) return;
        width = (int)(packed >> 32);                 // MF_MT_FRAME_SIZE = HI32(宽) : LO32(高)
        height = (int)(packed & 0xFFFF_FFFF);
    }

    private static void ReadFrameRate(IntPtr mediaType, out double frameRate)
    {
        frameRate = 0;
        var key = FrameRateKey;
        if (Slot<GetUInt64Fn>(mediaType, SlotGetUInt64)(mediaType, ref key, out var packed) < 0) return;
        var numerator = (uint)(packed >> 32);        // MF_MT_FRAME_RATE = HI32(分子) : LO32(分母)
        var denominator = (uint)(packed & 0xFFFF_FFFF);
        if (denominator != 0 && numerator != 0) frameRate = numerator / (double)denominator;
    }

    private static ulong Pack(int high, int low) => ((ulong)high << 32) | (uint)low;

    private static string Describe(int hr)
    {
        var text = Marshal.GetExceptionForHR(hr)?.Message;
        return string.IsNullOrWhiteSpace(text) ? $"hr=0x{hr:x8}" : $"hr=0x{hr:x8} {text}";
    }

    private static void Ensure(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what} 失败：{Describe(hr)}");
    }

    private static T Slot<T>(IntPtr instance, int index) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), index * IntPtr.Size));

    private static void Release(IntPtr instance)
    {
        if (instance != IntPtr.Zero) Slot<ReleaseFn>(instance, SlotRelease)(instance);
    }

    private static bool TryRetainPlatform(out string error)
    {
        error = "";
        lock (PlatformGate)
        {
            if (_platformRefs > 0)
            {
                _platformRefs++;
                return true;
            }

            // MF 的调用线程要进过 COM；没进过的线程在这里并入 MTA。已经是别的套间（UI 线程是 STA）就沿用，
            // 所以返回值一律不管——RPC_E_CHANGED_MODE 只说明"这线程已经是别的套间"，不是失败。
            CoInitializeEx(IntPtr.Zero, CoInitMultithreaded);

            var hr = MFStartup(MfVersion, 0); // MFSTARTUP_FULL
            if (hr < 0)
            {
                error = $"MFStartup 失败：{Describe(hr)}";
                return false;
            }

            _platformRefs = 1;
            return true;
        }
    }

    private static void ReleasePlatform()
    {
        lock (PlatformGate)
        {
            if (_platformRefs == 0) return;
            if (--_platformRefs == 0) MFShutdown();
        }
    }

    public void Dispose()
    {
        if (_reader == IntPtr.Zero) return;
        Release(_reader);
        _reader = IntPtr.Zero;
        _setPosition = null;
        _readSample = null;
        _sampleTime = null;
        _sampleDuration = null;
        _toContiguous = null;
        _bufferLock = null;
        _bufferUnlock = null;
        if (_platformRetained)
        {
            _platformRetained = false;
            ReleasePlatform();
        }
    }

    // ---- COM 委托 ----
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReleaseFn(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetUInt32Fn(IntPtr self, ref Guid key, uint value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetUInt64Fn(IntPtr self, ref Guid key, ulong value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetUInt32Fn(IntPtr self, ref Guid key, out uint value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetUInt64Fn(IntPtr self, ref Guid key, out ulong value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetGuidFn(IntPtr self, ref Guid key, ref Guid value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetNativeTypeFn(IntPtr self, int streamIndex, int typeIndex, out IntPtr mediaType);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetCurrentTypeFn(IntPtr self, int streamIndex, out IntPtr mediaType);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetCurrentTypeFn(IntPtr self, int streamIndex, IntPtr reserved, IntPtr mediaType);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetPositionFn(IntPtr self, ref Guid timeFormat, ref PropVariant position);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReadSampleFn(IntPtr self, int streamIndex, uint controlFlags,
        out int actualStreamIndex, out uint streamFlags, out long timestamp, out IntPtr sample);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPresentationFn(IntPtr self, int streamIndex, ref Guid key, out PropVariant value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSampleTimeFn(IntPtr self, out long time);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSampleDurationFn(IntPtr self, out long duration);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ContiguousBufferFn(IntPtr self, out IntPtr buffer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int LockFn(IntPtr self, out IntPtr data, out uint maxLength, out uint currentLength);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UnlockFn(IntPtr self);

    /// <summary>PROPVARIANT 的 x64 布局（8 字节头 + 16 字节联合）；本仓库只出 x64，够用。</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public long Value;
    }

    // ---- P/Invoke ----
    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateAttributes(out IntPtr attributes, uint initialSize);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMediaType(out IntPtr mediaType);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes, out IntPtr reader);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);
}
