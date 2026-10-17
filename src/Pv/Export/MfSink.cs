using System.Runtime.InteropServices;

namespace JizuraLive.Pv;

public sealed class MfSink : IDisposable
{
    private const uint MfVersion = 0x00020070;
    private const int H264ProfileMain = 77;
    private const long HnsPerSecond = 10_000_000;
    private const int Progressive = 2;
    private const int Bt709 = 1;
    private const int AudioBits = 16;
    private const int AudioAvgBytesPerSecond = 24_000;
    private const uint AacPayloadRaw = 0;

    private IMFSinkWriter? _writer;
    private readonly int _stream;
    private readonly int _audioStream;
    private readonly long _frameDuration;
    private readonly int _frameBytes;
    private long _nextTimestamp;
    private bool _comInitialized;

    private MfSink(IMFSinkWriter writer, int stream, int audioStream, int width, int height, int fps,
        bool comInitialized)
    {
        _writer = writer;
        _stream = stream;
        _audioStream = audioStream;
        _frameDuration = HnsPerSecond / fps;
        _frameBytes = Nv12Size(width, height);
        _comInitialized = comInitialized;
    }

    public static int Nv12Size(int width, int height) => width * height * 3 / 2;

    public static MfSink Open(string path, int width, int height, int fps, bool withAudio = false,
        int audioSampleRate = 48000, int audioChannels = 2)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("导出路径为空", nameof(path));
        if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(width), $"NV12 要求宽高为正偶数（当前 {width}×{height}）");
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps), $"帧率必须为正数（当前 {fps}）");
        if (withAudio && (audioSampleRate <= 0 || audioChannels is < 1 or > 2))
            throw new ArgumentOutOfRangeException(nameof(audioSampleRate),
                $"音轨只支持 1..2 声道的正整数采样率（当前 {audioSampleRate}Hz {audioChannels}ch）");

        var comInitialized = CoInitializeEx(IntPtr.Zero, 0x2) == 0;
        var mfStarted = false;
        IMFSinkWriter? pending = null;
        try
        {
            Check(MFStartup(MfVersion, 0), "初始化 Media Foundation");
            mfStarted = true;

            Check(MFCreateSinkWriterFromURL(path, IntPtr.Zero, IntPtr.Zero, out var writer), "打开输出文件");
            pending = writer;

            BuildStreams(writer, width, height, fps, withAudio, audioSampleRate, audioChannels,
                out var stream, out var audioStream);
            return new MfSink(writer, stream, audioStream, width, height, fps, comInitialized);
        }
        catch
        {
            Release(pending);
            if (mfStarted) MFShutdown();
            if (comInitialized) CoUninitialize();
            throw;
        }
    }

    public void WriteFrame(byte[] nv12)
    {
        if (_writer is null) throw new ObjectDisposedException(nameof(MfSink));
        if (nv12.Length != _frameBytes)
            throw new ArgumentException($"NV12 帧应为 {_frameBytes} 字节，实际 {nv12.Length}", nameof(nv12));

        WriteSample(_stream, nv12, _nextTimestamp, _frameDuration, "帧");
        _nextTimestamp += _frameDuration;
    }

    public void WriteAudio(ReadOnlySpan<short> interleaved, long sampleTime100ns, long duration100ns)
    {
        if (_writer is null) throw new ObjectDisposedException(nameof(MfSink));
        if (_audioStream < 0) throw new InvalidOperationException("本次导出没有音轨（Open 时 withAudio = false）");
        if (interleaved.Length == 0) return;

        WriteSample(_audioStream, MemoryMarshal.AsBytes(interleaved), sampleTime100ns, duration100ns, "音频");
    }

    private void WriteSample(int stream, ReadOnlySpan<byte> data, long time, long duration, string what)
    {
        Check(MFCreateMemoryBuffer((uint)data.Length, out var buffer), $"建立{what}缓冲");
        IMFSample? sample = null;
        try
        {
            Check(buffer.Lock(out var target, IntPtr.Zero, IntPtr.Zero), $"锁定{what}缓冲");
            try
            {
                unsafe
                {
                    data.CopyTo(new Span<byte>((void*)target, data.Length));
                }
            }
            finally
            {
                buffer.Unlock();
            }

            Check(buffer.SetCurrentLength(data.Length), $"设置{what}缓冲长度");
            Check(MFCreateSample(out sample), "建立媒体样本");
            Check(sample.AddBuffer(buffer), "装载缓冲");
            Check(sample.SetSampleTime(time), "设置时间戳");
            Check(sample.SetSampleDuration(duration), $"设置{what}时长");
            Check(_writer!.WriteSample(stream, sample), $"写入{what}");
        }
        finally
        {
            Release(sample);
            Release(buffer);
        }
    }

    public void Close()
    {
        var writer = _writer;
        if (writer is null) return;
        _writer = null;

        try
        {
            Check(writer.Finalize(), "收尾（写 MP4 索引）");
        }
        finally
        {
            Release(writer);
            MFShutdown();
            if (_comInitialized)
            {
                CoUninitialize();
                _comInitialized = false;
            }
        }
    }

    public void Dispose() => Close();

    private static void BuildStreams(IMFSinkWriter writer, int width, int height, int fps, bool withAudio,
        int audioSampleRate, int audioChannels, out int stream, out int audioStream)
    {
        Check(MFCreateMediaType(out var outType), "建立输出媒体类型");
        SetVideoType(outType, MfVideoFormatH264, width, height, fps);
        Check(outType.SetUINT32(MfMtAvgBitrate, (uint)BitrateFor(width, height, fps)), "设置码率");
        Check(outType.SetUINT32(MfMtMpeg2Profile, H264ProfileMain), "设置 H.264 档位");
        Check(writer.AddStream(outType, out stream), "添加视频流");

        Check(MFCreateMediaType(out var inType), "建立输入媒体类型");
        SetVideoType(inType, MfVideoFormatNv12, width, height, fps);
        Check(inType.SetUINT32(MfMtYuvMatrix, Bt709), "声明色度矩阵");
        Check(writer.SetInputMediaType(stream, inType, IntPtr.Zero), "设置输入格式（NV12）");

        audioStream = -1;
        if (withAudio)
        {
            Check(MFCreateMediaType(out var audioOut), "建立音轨输出媒体类型");
            SetAudioType(audioOut, MfAudioFormatAac, audioSampleRate, audioChannels);
            Check(audioOut.SetUINT32(MfMtAudioAvgBytesPerSecond, AudioAvgBytesPerSecond), "设置音轨码率");
            Check(audioOut.SetUINT32(MfMtAacPayloadType, AacPayloadRaw), "声明裸 AAC 载荷");
            Check(writer.AddStream(audioOut, out audioStream), "添加音轨");

            Check(MFCreateMediaType(out var audioIn), "建立音轨输入媒体类型");
            SetAudioType(audioIn, MfAudioFormatPcm, audioSampleRate, audioChannels);
            Check(audioIn.SetUINT32(MfMtAudioBlockAlignment, (uint)(audioChannels * AudioBits / 8)), "设置音轨块对齐");
            Check(audioIn.SetUINT32(MfMtAudioAvgBytesPerSecond,
                (uint)(audioSampleRate * audioChannels * AudioBits / 8)), "设置音轨字节率");
            Check(audioIn.SetUINT32(MfMtAllSamplesIndependent, 1), "声明音轨样本独立");
            Check(writer.SetInputMediaType(audioStream, audioIn, IntPtr.Zero), "设置输入格式（PCM）");
        }

        Check(writer.BeginWriting(), "开始写入");
    }

    private static void SetVideoType(IMFMediaType type, in Guid subtype, int width, int height, int fps)
    {
        Check(type.SetGUID(in MfMtMajorType, in MediaTypeVideo), "设置主类型");
        Check(type.SetGUID(in MfMtSubtype, in subtype), "设置子类型");
        Check(type.SetUINT32(in MfMtInterlaceMode, Progressive), "设置逐行扫描");
        Check(type.SetUINT64(in MfMtFrameSize, Pack(width, height)), "设置帧尺寸");
        Check(type.SetUINT64(in MfMtFrameRate, Pack(fps, 1)), "设置帧率");
        Check(type.SetUINT64(in MfMtPixelAspectRatio, Pack(1, 1)), "设置像素宽高比");
    }

    private static void SetAudioType(IMFMediaType type, in Guid subtype, int sampleRate, int channels)
    {
        Check(type.SetGUID(in MfMtMajorType, in MediaTypeAudio), "设置主类型");
        Check(type.SetGUID(in MfMtSubtype, in subtype), "设置子类型");
        Check(type.SetUINT32(in MfMtAudioSamplesPerSecond, (uint)sampleRate), "设置采样率");
        Check(type.SetUINT32(in MfMtAudioNumChannels, (uint)channels), "设置声道数");
        Check(type.SetUINT32(in MfMtAudioBitsPerSample, AudioBits), "设置位深");
    }

    private static int BitrateFor(int width, int height, int fps) =>
        (int)Math.Clamp((long)width * height * fps / 10, 2_000_000, 60_000_000);

    private static ulong Pack(long high, long low) => ((ulong)high << 32) | (uint)low;

    private static void Check(int hr, string step)
    {
        if (hr < 0) throw new InvalidOperationException($"{step}失败：{Describe(hr)}");
    }

    private static string Describe(int hr) => hr switch
    {
        unchecked((int)0xC00D36B4) => "输入格式不被接受（MF_E_INVALIDMEDIATYPE）",
        unchecked((int)0xC00D5212) => "系统里找不到 H.264 编码器（MF_E_TOPO_CODEC_NOT_FOUND）",
        _ => $"0x{hr:X8}",
    };

    private static void Release(object? com)
    {
        if (com is null) return;
        try
        {
            Marshal.ReleaseComObject(com);
        }
        catch (InvalidComObjectException)
        {
        }
    }

    private static readonly Guid MfMtMajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MfMtSubtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MfMtAvgBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid MfMtInterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid MfMtFrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MfMtFrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid MfMtPixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid MfMtYuvMatrix = new("3e23d450-2c75-4d25-a00e-b91670d12327");
    private static readonly Guid MfMtMpeg2Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    private static readonly Guid MfMtAudioNumChannels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid MfMtAudioSamplesPerSecond = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid MfMtAudioBlockAlignment = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    private static readonly Guid MfMtAudioAvgBytesPerSecond = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    private static readonly Guid MfMtAudioBitsPerSample = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    private static readonly Guid MfMtAllSamplesIndependent = new("c9173739-5e56-461c-b713-46fb995cb95f");
    private static readonly Guid MfMtAacPayloadType = new("bfbabe79-7434-4d1c-94f0-72a3b9e17188");

    private static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid MediaTypeAudio = new("73647561-0000-0010-8000-00aa00389b71");
    private static readonly Guid MfVideoFormatH264 = new("34363248-0000-0010-8000-00aa00389b71");
    private static readonly Guid MfVideoFormatNv12 = new("3231564e-0000-0010-8000-00aa00389b71");
    private static readonly Guid MfAudioFormatAac = new("00001610-0000-0010-8000-00aa00389b71");
    private static readonly Guid MfAudioFormatPcm = new("00000001-0000-0010-8000-00aa00389b71");

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMediaType(out IMFMediaType mediaType);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMemoryBuffer(uint maxLength, out IMFMediaBuffer buffer);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateSample(out IMFSample sample);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MFCreateSinkWriterFromURL(
        [MarshalAs(UnmanagedType.LPWStr)] string outputUrl, IntPtr byteStream, IntPtr attributes,
        out IMFSinkWriter sinkWriter);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaType : IMFAttributes
    {
    }

    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFAttributes
    {
        [PreserveSig] int GetItem(in Guid key, IntPtr value);
        [PreserveSig] int GetItemType(in Guid key, out int type);
        [PreserveSig] int CompareItem(in Guid key, IntPtr value, out int result);
        [PreserveSig] int Compare(IntPtr theirs, int matchType, out int result);
        [PreserveSig] int GetUINT32(in Guid key, out uint value);
        [PreserveSig] int GetUINT64(in Guid key, out ulong value);
        [PreserveSig] int GetDouble(in Guid key, out double value);
        [PreserveSig] int GetGUID(in Guid key, out Guid value);
        [PreserveSig] int GetStringLength(in Guid key, out uint length);
        [PreserveSig] int GetString(in Guid key, IntPtr value, uint size, IntPtr length);
        [PreserveSig] int GetAllocatedString(in Guid key, out IntPtr value, out uint length);
        [PreserveSig] int GetBlobSize(in Guid key, out uint size);
        [PreserveSig] int GetBlob(in Guid key, IntPtr buf, uint size, IntPtr blobSize);
        [PreserveSig] int GetAllocatedBlob(in Guid key, out IntPtr buf, out uint size);
        [PreserveSig] int GetUnknown(in Guid key, in Guid riid, out IntPtr value);
        [PreserveSig] int SetItem(in Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(in Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(in Guid key, uint value);
        [PreserveSig] int SetUINT64(in Guid key, ulong value);
        [PreserveSig] int SetDouble(in Guid key, double value);
        [PreserveSig] int SetGUID(in Guid key, in Guid value);
        [PreserveSig] int SetString(in Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(in Guid key, IntPtr buf, uint size);
        [PreserveSig] int SetUnknown(in Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IntPtr dest);
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSample
    {
        [PreserveSig] int GetItem(in Guid key, IntPtr value);
        [PreserveSig] int GetItemType(in Guid key, out int type);
        [PreserveSig] int CompareItem(in Guid key, IntPtr value, out int result);
        [PreserveSig] int Compare(IntPtr theirs, int matchType, out int result);
        [PreserveSig] int GetUINT32(in Guid key, out uint value);
        [PreserveSig] int GetUINT64(in Guid key, out ulong value);
        [PreserveSig] int GetDouble(in Guid key, out double value);
        [PreserveSig] int GetGUID(in Guid key, out Guid value);
        [PreserveSig] int GetStringLength(in Guid key, out uint length);
        [PreserveSig] int GetString(in Guid key, IntPtr value, uint size, IntPtr length);
        [PreserveSig] int GetAllocatedString(in Guid key, out IntPtr value, out uint length);
        [PreserveSig] int GetBlobSize(in Guid key, out uint size);
        [PreserveSig] int GetBlob(in Guid key, IntPtr buf, uint size, IntPtr blobSize);
        [PreserveSig] int GetAllocatedBlob(in Guid key, out IntPtr buf, out uint size);
        [PreserveSig] int GetUnknown(in Guid key, in Guid riid, out IntPtr value);
        [PreserveSig] int SetItem(in Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(in Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(in Guid key, uint value);
        [PreserveSig] int SetUINT64(in Guid key, ulong value);
        [PreserveSig] int SetDouble(in Guid key, double value);
        [PreserveSig] int SetGUID(in Guid key, in Guid value);
        [PreserveSig] int SetString(in Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(in Guid key, IntPtr buf, uint size);
        [PreserveSig] int SetUnknown(in Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IntPtr dest);

        [PreserveSig] int GetSampleFlags(out int flags);
        [PreserveSig] int SetSampleFlags(int flags);
        [PreserveSig] int GetSampleTime(out long time);
        [PreserveSig] int SetSampleTime(long time);
        [PreserveSig] int GetSampleDuration(out long duration);
        [PreserveSig] int SetSampleDuration(long duration);
        [PreserveSig] int GetBufferCount(out int count);
        [PreserveSig] int GetBufferByIndex(int index, out IMFMediaBuffer buffer);
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
        [PreserveSig] int RemoveBufferByIndex(int index);
        [PreserveSig] int RemoveAllBuffers();
        [PreserveSig] int GetTotalLength(out int length);
        [PreserveSig] int CopyToBuffer(IMFMediaBuffer buffer);
    }

    [ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer, IntPtr maxLength, IntPtr currentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out int length);
        [PreserveSig] int SetCurrentLength(int length);
        [PreserveSig] int GetMaxLength(out int maxLength);
    }

    [ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSinkWriter
    {
        [PreserveSig] int AddStream(IMFMediaType targetType, out int streamIndex);
        [PreserveSig] int SetInputMediaType(int streamIndex, IMFMediaType inputType, IntPtr encodingParameters);
        [PreserveSig] int BeginWriting();
        [PreserveSig] int WriteSample(int streamIndex, IMFSample sample);
        [PreserveSig] int SendStreamTick(int streamIndex, long timestamp);
        [PreserveSig] int PlaceMarker(int streamIndex, IntPtr context);
        [PreserveSig] int NotifyEndOfSegment(int streamIndex);
        [PreserveSig] int Flush(int streamIndex);
        [PreserveSig] int Finalize();
    }
}
