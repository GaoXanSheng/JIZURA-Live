using System.IO;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;

namespace JizuraLive.Pv;

public sealed class MfVideoDecoder : IDisposable
{
    private const int SlotRelease = 2;
    private const int SlotGetUInt32 = 7;
    private const int SlotGetUInt64 = 8;
    private const int SlotSetUInt32 = 21;
    private const int SlotSetUInt64 = 22;
    private const int SlotSetGuid = 24;
    private const int SlotSetUnknown = 27;
    private const int SlotDeviceManagerResetDevice = 7;
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
    private const int SlotSampleGetBuffer = 40;
    private const int SlotSampleContiguous = 41;
    private const int SlotDxgiBufferGetResource = 3;
    private const int SlotDxgiBufferGetSubresource = 5;

    private const int FirstVideoStream = -4;
    private const int MediaSource = -1;
    private const uint FlagError = 0x1;
    private const uint MfVersion = 0x0002_0070;
    private const ushort VtI8 = 20;
    private const ushort VtUi8 = 21;
    private const long HnsPerSecond = 10_000_000;
    private const uint CoInitMultithreaded = 0x0;

    private const double ForwardSeekThreshold = 0.4;

    private const int MaxDecodeEdge = 1920;

    private static readonly Guid EnableVideoProcessing = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    private static readonly Guid EnableAdvancedVideoProcessing = new("a634a91c-822b-41b9-a494-4de4643612b0");
    private static readonly Guid SourceReaderD3DManager = new("ec822da2-e1e9-4b29-a0d8-563c719f5269");
    private static readonly Guid IidDxgiBuffer = new("e7174cfa-1c9e-48b1-8866-626226bfc258");
    private static readonly Guid IidTexture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid MajorTypeKey = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid SubtypeKey = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid FrameSizeKey = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid FrameRateKey = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid DefaultStrideKey = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid InterlaceModeKey = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid DurationKey = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
    private static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid VideoFormatRgb32 = new("00000016-0000-0010-8000-00aa00389b71");
    private static readonly Guid VideoFormatNv12 = new("3231564e-0000-0010-8000-00aa00389b71");

    private static readonly object PlatformGate = new();
    private static int _platformRefs;

    public static string LastOpenNote { get; private set; } = "";

    private IntPtr _reader;
    private IntPtr _deviceManager;
    private IntPtr _heldSample;
    private bool _gpuEnabled;
    private bool _platformRetained;
    private double _lastPts = double.NaN;
    private double _lastDuration;

    private SetPositionFn? _setPosition;
    private ReadSampleFn? _readSample;
    private GetSampleTimeFn? _sampleTime;
    private GetSampleDurationFn? _sampleDuration;
    private GetBufferFn? _getBuffer;
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

    private MfVideoDecoder(IntPtr reader, IntPtr deviceManager, bool gpuEnabled,
        int width, int height, double duration, double frameRate, int stride)
    {
        _reader = reader;
        _deviceManager = deviceManager;
        _gpuEnabled = gpuEnabled;
        _platformRetained = true;
        Width = width;
        Height = height;
        Duration = duration;
        FrameRate = frameRate;
        FrameCount = duration > 0 && frameRate > 0 ? (int)Math.Round(duration * frameRate) : 0;
        Stride = stride != 0 ? stride : width * 4;
    }

    public static MfVideoDecoder? TryOpen(string path, ID3D11Device? device, out string error)
    {
        error = "";
        LastOpenNote = "";
        if (device is null) return TryOpenCore(path, null, false, out error);

        var gpu = TryOpenCore(path, device, true, out var gpuError);
        if (gpu is not null)
        {
            LastOpenNote = LastOpenNote.Length > 0
                ? "D3D11 硬件解码；" + LastOpenNote
                : "D3D11 硬件解码";
            return gpu;
        }

        var software = TryOpenCore(path, null, false, out var softwareError);
        if (software is not null)
        {
            LastOpenNote = $"硬解不可用（{gpuError}），已回退软解";
            return software;
        }

        error = $"硬解失败：{gpuError}；软解失败：{softwareError}";
        return null;
    }

    private static MfVideoDecoder? TryOpenCore(string path, ID3D11Device? device, bool useGpu,
        out string error)
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
        var deviceManager = IntPtr.Zero;
        MfVideoDecoder? decoder = null;
        try
        {
            if (!TryRetainPlatform(out error)) return null;

            Ensure(MFCreateAttributes(out attributes, 4), "MFCreateAttributes");
            var setUInt32 = Slot<SetUInt32Fn>(attributes, SlotSetUInt32);
            var advancedKey = EnableAdvancedVideoProcessing;
            setUInt32(attributes, ref advancedKey, 1);

            if (useGpu)
            {
                if (device is null) throw new InvalidOperationException("硬件解码需要 D3D11 设备");
                if (!TryCreateDeviceManager(device, out deviceManager, out var managerError))
                    throw new InvalidOperationException(managerError);
                var managerKey = SourceReaderD3DManager;
                var setUnknown = Slot<SetUnknownFn>(attributes, SlotSetUnknown);
                Ensure(setUnknown(attributes, ref managerKey, deviceManager),
                    "设置 MF_SOURCE_READER_D3D_MANAGER");
            }
            else
            {
                var enableKey = EnableVideoProcessing;
                Ensure(setUInt32(attributes, ref enableKey, 1),
                    "设置 MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING");
            }

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

            var outW = width;
            var outH = height;
            if (width > MaxDecodeEdge || height > MaxDecodeEdge)
            {
                var scale = MaxDecodeEdge / (double)Math.Max(width, height);
                outW = Math.Max(2, ((int)(width * scale)) & ~1);
                outH = Math.Max(2, ((int)(height * scale)) & ~1);
            }

            Ensure(MFCreateMediaType(out outputType), "MFCreateMediaType");
            var majorKey = MajorTypeKey;
            var subtypeKey = SubtypeKey;
            var videoType = MediaTypeVideo;
            var subtype = useGpu ? VideoFormatNv12 : VideoFormatRgb32;
            var frameSizeKey = FrameSizeKey;
            var setGuid = Slot<SetGuidFn>(outputType, SlotSetGuid);
            Ensure(setGuid(outputType, ref majorKey, ref videoType), "设置 MF_MT_MAJOR_TYPE");
            Ensure(setGuid(outputType, ref subtypeKey, ref subtype), "设置 MF_MT_SUBTYPE");
            var interlaceKey = InterlaceModeKey;
            var setUInt32Type = Slot<SetUInt32Fn>(outputType, SlotSetUInt32);
            setUInt32Type(outputType, ref interlaceKey, 2);
            var setFrameSize = Slot<SetUInt64Fn>(outputType, SlotSetUInt64);
            var setCurrentType = Slot<SetCurrentTypeFn>(reader, SlotReaderSetCurrentType);
            Ensure(setFrameSize(outputType, ref frameSizeKey, Pack(outW, outH)), "设置 MF_MT_FRAME_SIZE");
            var scaledHr = setCurrentType(reader, FirstVideoStream, IntPtr.Zero, outputType);
            if (scaledHr < 0)
            {
                LastOpenNote = $"缩放至 {outW}×{outH} 被拒（{Describe(scaledHr)}），回退原生尺寸";
                outW = width;
                outH = height;
                Ensure(setFrameSize(outputType, ref frameSizeKey, Pack(outW, outH)),
                    "设置 MF_MT_FRAME_SIZE(原生)");
                Ensure(setCurrentType(reader, FirstVideoStream, IntPtr.Zero, outputType),
                    "设置输出媒体类型");
            }

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

            decoder = new MfVideoDecoder(reader, deviceManager, useGpu, outW, outH, duration, frameRate, stride);
            reader = IntPtr.Zero;
            deviceManager = IntPtr.Zero;
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
            if (deviceManager != IntPtr.Zero) Release(deviceManager);
            if (decoder is null)
            {
                Release(reader);
                ReleasePlatform();
            }
        }

        return decoder;
    }

    private static bool TryCreateDeviceManager(ID3D11Device device, out IntPtr manager, out string error)
    {
        manager = IntPtr.Zero;
        error = "";
        var hr = MFCreateDXGIDeviceManager(out var resetToken, out manager);
        if (hr < 0)
        {
            error = "MFCreateDXGIDeviceManager 失败：" + Describe(hr);
            return false;
        }

        try
        {
            var reset = Slot<ResetDeviceFn>(manager, SlotDeviceManagerResetDevice);
            hr = reset(manager, device.NativePointer, resetToken);
            if (hr >= 0) return true;
            error = "IMFDXGIDeviceManager::ResetDevice 失败：" + Describe(hr);
        }
        catch (Exception ex)
        {
            error = "IMFDXGIDeviceManager 初始化异常：" + ex.Message;
        }

        Release(manager);
        manager = IntPtr.Zero;
        return false;
    }

    public bool TryReadFrameAt(double seconds, byte[]? buffer,
        out ID3D11Texture2D? texture, out uint subresource, out double framePts, out bool newFrame)
    {
        texture = null;
        subresource = 0;
        framePts = _lastPts;
        newFrame = false;
        if (_reader == IntPtr.Zero) return false;
        if (!double.IsFinite(seconds)) return false;
        seconds = Math.Max(0, seconds);

        var known = !double.IsNaN(_lastPts);
        if (known && seconds >= _lastPts && seconds < _lastPts + _lastDuration) return true;

        ReleaseHeldSample();
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
                if (_gpuEnabled && TryGetSampleTexture(sample, out var texturePtr, out var textureIndex))
                {
                    ID3D11Texture2D gpuTexture;
                    try
                    {
                        gpuTexture = new ID3D11Texture2D(texturePtr);
                    }
                    catch
                    {
                        Marshal.Release(texturePtr);
                        throw;
                    }

                    ReadSampleTimes(sample, out var gpuPts, out var gpuDuration);
                    if (gpuPts < seconds && gpuPts + gpuDuration <= seconds)
                    {
                        gpuTexture.Dispose();
                        continue;
                    }

                    _heldSample = sample;
                    sample = IntPtr.Zero;
                    _lastPts = gpuPts;
                    _lastDuration = gpuDuration;
                    framePts = gpuPts;
                    newFrame = true;
                    LastError = null;
                    texture = gpuTexture;
                    subresource = textureIndex;
                    return true;
                }

                if (buffer is null || buffer.Length < FrameBytes)
                {
                    LastError = $"帧缓冲太小：需要 {FrameBytes} 字节，给了 {buffer?.Length ?? 0}";
                    return false;
                }

                if (!TryCopySample(sample, buffer, out var pts, out var duration)) continue;
                _lastPts = pts;
                _lastDuration = duration;
                framePts = pts;
                newFrame = true;
                LastError = null;
                if (pts >= seconds || pts + duration > seconds) return true;
            }
            finally
            {
                if (sample != IntPtr.Zero) Release(sample);
            }
        }
    }

    private bool TryGetSampleTexture(IntPtr sample, out IntPtr texturePtr, out uint subresource)
    {
        texturePtr = IntPtr.Zero;
        subresource = 0;
        _getBuffer ??= Slot<GetBufferFn>(sample, SlotSampleGetBuffer);
        if (_getBuffer(sample, 0, out var buffer) < 0 || buffer == IntPtr.Zero) return false;

        try
        {
            var dxgiIid = IidDxgiBuffer;
            if (Marshal.QueryInterface(buffer, in dxgiIid, out var dxgiBuffer) < 0) return false;
            try
            {
                var getResource = Slot<GetResourceFn>(dxgiBuffer, SlotDxgiBufferGetResource);
                var getIndex = Slot<GetSubresourceFn>(dxgiBuffer, SlotDxgiBufferGetSubresource);
                getIndex(dxgiBuffer, out subresource);
                var textureIid = IidTexture2D;
                return getResource(dxgiBuffer, ref textureIid, out texturePtr) >= 0
                       && texturePtr != IntPtr.Zero;
            }
            finally
            {
                Marshal.Release(dxgiBuffer);
            }
        }
        finally
        {
            Release(buffer);
        }
    }

    private void ReadSampleTimes(IntPtr sample, out double pts, out double duration)
    {
        _sampleTime ??= Slot<GetSampleTimeFn>(sample, SlotSampleGetTime);
        _sampleDuration ??= Slot<GetSampleDurationFn>(sample, SlotSampleGetDuration);
        _sampleTime(sample, out var ptsHns);
        _sampleDuration(sample, out var durationHns);
        pts = ptsHns / (double)HnsPerSecond;
        duration = durationHns > 0
            ? durationHns / (double)HnsPerSecond
            : FrameRate > 0 ? 1.0 / FrameRate : 0.0;
        if (duration <= 0 || duration > 1.0)
            duration = FrameRate > 0 ? 1.0 / FrameRate : 1.0 / 30.0;
    }

    private void ReleaseHeldSample()
    {
        if (_heldSample == IntPtr.Zero) return;
        Release(_heldSample);
        _heldSample = IntPtr.Zero;
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
        ReadSampleTimes(sample, out pts, out duration);

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

        if (!bottomUp && pitch == rowBytes)
        {
            Marshal.Copy(source, destination, 0, rowBytes * Height);
            return true;
        }

        for (var y = 0; y < Height; y++)
        {
            var sourceRow = bottomUp ? Height - 1 - y : y;
            Marshal.Copy(source + sourceRow * pitch, destination, y * rowBytes, rowBytes);
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
        ReleaseHeldSample();
        Release(_reader);
        _reader = IntPtr.Zero;
        if (_deviceManager != IntPtr.Zero)
        {
            Release(_deviceManager);
            _deviceManager = IntPtr.Zero;
        }

        _setPosition = null;
        _readSample = null;
        _sampleTime = null;
        _sampleDuration = null;
        _getBuffer = null;
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
    private delegate int SetUnknownFn(IntPtr self, ref Guid key, IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ResetDeviceFn(IntPtr self, IntPtr device, uint resetToken);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBufferFn(IntPtr self, int index, out IntPtr buffer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetResourceFn(IntPtr self, ref Guid iid, out IntPtr resource);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSubresourceFn(IntPtr self, out uint subresource);

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
    private static extern int MFCreateDXGIDeviceManager(out uint resetToken, out IntPtr deviceManager);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMediaType(out IntPtr mediaType);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes, out IntPtr reader);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);
}
