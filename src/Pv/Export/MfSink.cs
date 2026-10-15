using System.Runtime.InteropServices;

namespace JizuraLive.Pv;

/// <summary>
/// Media Foundation SinkWriter 的最小封装：H.264 编码写 MP4。
///
/// 输入帧是 NV12（Y 平面 + 交错 UV，逐行自上而下）：NV12 是 H.264 编码器明确接受的输入类型之一，
/// 而系统内存里的 RGB 按惯例是 bottom-up，送 RGB32 得靠媒体类型声明朝向才不会上下颠倒——所以不走那条路。
/// 时间戳由内部帧序号推出：第 n 帧 = n × (10^7 / fps) 个 100ns 单位；每帧字节数必须等于 <see cref="Nv12Size"/>。
/// 不调用 <see cref="Close"/> 就没有能播的文件：MP4 的索引在 IMFSinkWriter::Finalize 里才写。
/// </summary>
public sealed class MfSink : IDisposable
{
    private const uint MfVersion = 0x00020070;    // MF_VERSION = (SDK 2 << 16) | API 0x70
    private const int H264ProfileMain = 77;       // eAVEncH264VProfile_Main：Baseline 无 B 帧/CABAC，成片压不干净
    private const long HnsPerSecond = 10_000_000; // MF 的时间基是 100ns
    private const int Progressive = 2;            // MFVideoInterlace_Progressive
    private const int Bt709 = 1;                  // MFVideoTransferMatrix_BT709

    private IMFSinkWriter? _writer;
    private readonly int _stream;
    private readonly long _frameDuration;
    private readonly int _frameBytes;
    private long _nextTimestamp;
    private bool _comInitialized;

    private MfSink(IMFSinkWriter writer, int stream, int width, int height, int fps, bool comInitialized)
    {
        _writer = writer;
        _stream = stream;
        _frameDuration = HnsPerSecond / fps;
        _frameBytes = Nv12Size(width, height);
        _comInitialized = comInitialized;
    }

    /// <summary>NV12 帧的字节数：Y 全分辨率 + UV 各半分辨率交错。</summary>
    public static int Nv12Size(int width, int height) => width * height * 3 / 2;

    /// <summary>
    /// 建输出文件并接上 H.264 编码器。失败抛 <see cref="InvalidOperationException"/>（带步骤名与 HRESULT），
    /// 不返回半成品。参数校验归调用方（<see cref="PvExporter"/> 已经查过一遍）。
    /// </summary>
    public static MfSink Open(string path, int width, int height, int fps)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("导出路径为空", nameof(path));
        if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(width), $"NV12 要求宽高为正偶数（当前 {width}×{height}）");
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps), $"帧率必须为正数（当前 {fps}）");

        // 只有 S_OK 才是这次调用完成了初始化，也才有配对的 CoUninitialize；
        // S_FALSE（已初始化）/ RPC_E_CHANGED_MODE（别的套间模式）都不接手释放。
        var comInitialized = CoInitializeEx(IntPtr.Zero, 0x2) == 0;
        var mfStarted = false;
        IMFSinkWriter? pending = null;
        try
        {
            Check(MFStartup(MfVersion, 0), "初始化 Media Foundation");
            mfStarted = true;

            Check(MFCreateSinkWriterFromURL(path, IntPtr.Zero, IntPtr.Zero, out var writer), "打开输出文件");
            pending = writer;

            BuildStreams(writer, width, height, fps, out var stream);
            return new MfSink(writer, stream, width, height, fps, comInitialized);
        }
        catch
        {
            Release(pending);
            if (mfStarted) MFShutdown();
            if (comInitialized) CoUninitialize();
            throw;
        }
    }

    /// <summary>
    /// 送一帧 NV12。数据拷进 MF 缓冲，调用方可以复用同一个数组。
    /// 每帧新建缓冲与样本：SinkWriter 不承诺在本调用返回前消化掉样本，复用同一块会让编码器读到下一帧的数据。
    /// </summary>
    public void WriteFrame(byte[] nv12)
    {
        var writer = _writer ?? throw new ObjectDisposedException(nameof(MfSink));
        if (nv12.Length != _frameBytes)
            throw new ArgumentException($"NV12 帧应为 {_frameBytes} 字节，实际 {nv12.Length}", nameof(nv12));

        Check(MFCreateMemoryBuffer((uint)nv12.Length, out var buffer), "建立媒体缓冲");
        IMFSample? sample = null;
        try
        {
            Check(buffer.Lock(out var data, IntPtr.Zero, IntPtr.Zero), "锁定媒体缓冲");
            try
            {
                Marshal.Copy(nv12, 0, data, nv12.Length);
            }
            finally
            {
                buffer.Unlock();
            }

            Check(buffer.SetCurrentLength(nv12.Length), "设置缓冲长度");
            Check(MFCreateSample(out sample), "建立媒体样本");
            Check(sample.AddBuffer(buffer), "装载缓冲");
            Check(sample.SetSampleTime(_nextTimestamp), "设置时间戳");
            Check(sample.SetSampleDuration(_frameDuration), "设置帧时长");
            Check(writer.WriteSample(_stream, sample), "写入帧");
        }
        finally
        {
            Release(sample);
            Release(buffer);
        }

        _nextTimestamp += _frameDuration;
    }

    /// <summary>收尾：写 MP4 索引、关闭文件。幂等。</summary>
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

    private static void BuildStreams(IMFSinkWriter writer, int width, int height, int fps, out int stream)
    {
        Check(MFCreateMediaType(out var outType), "建立输出媒体类型");
        SetVideoType(outType, MfVideoFormatH264, width, height, fps);
        Check(outType.SetUINT32(MfMtAvgBitrate, (uint)BitrateFor(width, height, fps)), "设置码率");
        Check(outType.SetUINT32(MfMtMpeg2Profile, H264ProfileMain), "设置 H.264 档位");
        Check(writer.AddStream(outType, out stream), "添加视频流");

        Check(MFCreateMediaType(out var inType), "建立输入媒体类型");
        SetVideoType(inType, MfVideoFormatNv12, width, height, fps);
        // 我们的转换按 BT.709 有限范围算；不声明的话 VUI 里是 unspecified，各播放器自己猜
        Check(inType.SetUINT32(MfMtYuvMatrix, Bt709), "声明色度矩阵");
        Check(writer.SetInputMediaType(stream, inType, IntPtr.Zero), "设置输入格式（NV12）");

        Check(writer.BeginWriting(), "开始写入");
        // 两个媒体类型到此为止：SinkWriter 已经拷走它要的属性，剩下的引用交给 GC
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

    /// <summary>码率档：0.1 bit/像素/帧，夹在 2..60 Mbps（码率 UI 不在本次范围内）。</summary>
    private static int BitrateFor(int width, int height, int fps) =>
        (int)Math.Clamp((long)width * height * fps / 10, 2_000_000, 60_000_000);

    /// <summary>MF 的「两个 32 位打包进 64 位」属性：高位在前（尺寸=宽/高，比例=分子/分母）。</summary>
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
        // 对象已被断开时 ReleaseComObject 会抛：收尾路径上不值得让它盖住真正的失败原因
        try
        {
            Marshal.ReleaseComObject(com);
        }
        catch (InvalidComObjectException)
        {
        }
    }

    // ---------------- 互操作 ----------------
    // 只声明用到的槽位；IUnknown 派生的 ComImport 接口按声明顺序对 vtable，名称不参与解析。

    private static readonly Guid MfMtMajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MfMtSubtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MfMtAvgBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid MfMtInterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid MfMtFrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MfMtFrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid MfMtPixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid MfMtYuvMatrix = new("3e23d450-2c75-4d25-a00e-b91670d12327");
    private static readonly Guid MfMtMpeg2Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036");

    private static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid MfVideoFormatH264 = new("34363248-0000-0010-8000-00aa00389b71");
    private static readonly Guid MfVideoFormatNv12 = new("3231564e-0000-0010-8000-00aa00389b71");

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

    /// <summary>
    /// 媒体类型：本接口自己不声明方法，调用的一律是基类的属性槽位。少声明一个都不行——
    /// 派生接口的方法槽**不会**自动跳过基类的 30 个（见 <see cref="IMFSample"/>），所以这里必须保持空体。
    /// </summary>
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

    /// <summary>
    /// 样本。**必须把 IMFAttributes 的 30 个槽位一起铺开**：ComImport 接口按声明顺序对 vtable，
    /// 派生接口自己声明的方法不会自动加到基类槽位之后（实测：只声明下面 30 行以外的部分，
    /// AddBuffer 会落到 IMFAttributes::GetString 上，返回 MF_E_ATTRIBUTENOTFOUND，编码器一帧都收不到）。
    /// </summary>
    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSample
    {
        // IMFAttributes 的 30 个槽位（本类不使用，只为占位）
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
