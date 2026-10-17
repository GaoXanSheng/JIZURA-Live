using System.IO;
using System.Runtime.InteropServices;

namespace JizuraLive.Pv;

public sealed class MfVideoDecoder : IDisposable
{
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

    private const int FirstVideoStream = -4;
    private const int MediaSource = -1;
    private const uint FlagError = 0x1;
    private const uint MfVersion = 0x0002_0070;
    private const ushort VtI8 = 20;
    private const ushort VtUi8 = 21;
    private const long HnsPerSecond = 10_000_000;
    private const uint CoInitMultithreaded = 0x0;

    private const double ForwardSeekThreshold = 2.0;

    private static readonly Guid EnableVideoProcessing = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    private static readonly Guid MajorTypeKey = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid SubtypeKey = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid FrameSizeKey = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid FrameRateKey = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid DefaultStrideKey = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid DurationKey = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
    private static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid VideoFormatRgb32 = new("00000016-0000-0010-8000-00aa00389b71");

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

    public double Duration { get; }

    public double FrameRate { get; }

    public int FrameCount { get; }

    public int FrameBytes => Width * 4 * Height;

    public int Stride { get; }

    public string? LastError { get; private set; }

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
                if (Slot<GetUInt32Fn>(currentType, SlotGetUInt32)(currentType, ref strideKey, out var raw) >= 0)
                    stride = unchecked((int)raw);
            }

            decoder = new MfVideoDecoder(reader, width, height, duration, frameRate, stride);
            reader = IntPtr.Zero;
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
                if ((flags & FlagError) != 0) LastError = "源在读帧时报错（流损坏或解码失败）";
                return false;
            }

            try
            {
                if (!TryCopySample(sample, buffer, out var pts, out var duration)) continue;
                _lastPts = pts;
                _lastDuration = duration;
                LastError = null;
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
        var timeFormat = Guid.Empty;
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
            : FrameRate > 0 ? 1.0 / FrameRate : 0.0;

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

    private bool CopyRows(IntPtr source, int length, byte[] destination)
    {
        var rowBytes = Width * 4;
        var pitch = Math.Abs(Stride);
        var bottomUp = Stride < 0;
        if (length < pitch * Height) return false;

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
        width = (int)(packed >> 32);
        height = (int)(packed & 0xFFFF_FFFF);
    }

    private static void ReadFrameRate(IntPtr mediaType, out double frameRate)
    {
        frameRate = 0;
        var key = FrameRateKey;
        if (Slot<GetUInt64Fn>(mediaType, SlotGetUInt64)(mediaType, ref key, out var packed) < 0) return;
        var numerator = (uint)(packed >> 32);
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

            CoInitializeEx(IntPtr.Zero, CoInitMultithreaded);

            var hr = MFStartup(MfVersion, 0);
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

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public long Value;
    }

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
