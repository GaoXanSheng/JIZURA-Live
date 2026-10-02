using System.IO;
using System.IO.Compression;
using System.Text;

namespace JizuraLive.Server;

internal enum JzCodec : byte
{
    Raw = 0,
    Brotli = 1,
    Gzip = 2
}

internal sealed record JizuraPackageEntry(string Name, JzCodec Codec, long RawSize, byte[] Data);

// .jizuralive（JZLV）容器解析。布局与前端 src/jizura/pkg.ts 逐字节对齐，改动必须两边同步：
//   "JZLV" | u16 version | u16 entryCount | u32 reserved
//   每条目: u8 nameLen | name(UTF-8) | u8 codec(0=raw,1=brotli,2=gzip) | u64 rawSize | u64 packSize | u64 offset
//   之后是各条目负载连续排列，全部小端。
// 音频/字体条目按设计裸存（本体已是压缩格式），project.json 为 brotli（旧写入端可能是 gzip）
internal static class JizuraPackage
{
    public const int LatestVersion = 1;

    public static bool LooksLikePackage(ReadOnlySpan<byte> head)
    {
        return head.Length >= 4 && head[0] == (byte)'J' && head[1] == (byte)'Z' && head[2] == (byte)'L' &&
               head[3] == (byte)'V';
    }

    public static List<JizuraPackageEntry> ReadFile(string path)
    {
        return Read(File.ReadAllBytes(path));
    }

    public static List<JizuraPackageEntry> Read(byte[] bytes)
    {
        if (bytes.Length < 16 || !LooksLikePackage(bytes))
            throw new InvalidDataException("not a jizuralive package");
        int version = BitConverter.ToUInt16(bytes, 4);
        if (version > LatestVersion)
            throw new InvalidDataException($"package version {version} is newer than supported {LatestVersion}");
        int count = BitConverter.ToUInt16(bytes, 6);

        var entries = new List<JizuraPackageEntry>(count);
        var p = 16;
        for (var i = 0; i < count; i++)
        {
            if (p + 3 > bytes.Length) throw new InvalidDataException("truncated header");
            int nameLen = bytes[p];
            p++;
            if (p + nameLen + 25 > bytes.Length) throw new InvalidDataException("truncated header");
            var name = Encoding.UTF8.GetString(bytes, p, nameLen);
            p += nameLen;
            if (!IsSafeName(name)) throw new InvalidDataException($"unsafe entry name: {name}");
            var codec = (JzCodec)bytes[p];
            p++;
            if (codec is not (JzCodec.Raw or JzCodec.Brotli or JzCodec.Gzip))
                throw new InvalidDataException($"unknown codec {codec} in {name}");
            var rawSize = (long)BitConverter.ToUInt64(bytes, p);
            p += 8;
            var packSize = (long)BitConverter.ToUInt64(bytes, p);
            p += 8;
            var offset = (long)BitConverter.ToUInt64(bytes, p);
            p += 8;
            if (rawSize < 0 || packSize < 0 || offset < 0 || offset > bytes.Length - packSize)
                throw new InvalidDataException($"entry out of bounds: {name}");
            var data = Decode(bytes.AsSpan((int)offset, (int)packSize), codec, rawSize, name);
            entries.Add(new JizuraPackageEntry(name, codec, rawSize, data));
        }

        return entries;
    }

    // 名字只当条目标签用，禁掉路径分隔与盘符语法，防止未来落盘提取时越界
    private static bool IsSafeName(string name)
    {
        return name.Length > 0 && name.Length <= 255
                               && name.IndexOf('\0') < 0 && name.IndexOf('\\') < 0 && !name.Contains("..") &&
                               name[0] != '/';
    }

    private static byte[] Decode(ReadOnlySpan<byte> payload, JzCodec codec, long rawSize, string name)
    {
        byte[] data;
        switch (codec)
        {
            case JzCodec.Raw:
                data = payload.ToArray();
                break;
            case JzCodec.Brotli:
                using (var src = new MemoryStream(payload.ToArray(), false))
                using (var br = new BrotliStream(src, CompressionMode.Decompress))
                {
                    data = ReadAll(br, rawSize, name);
                }

                break;
            case JzCodec.Gzip:
                using (var src = new MemoryStream(payload.ToArray(), false))
                using (var gz = new GZipStream(src, CompressionMode.Decompress))
                {
                    data = ReadAll(gz, rawSize, name);
                }

                break;
            default:
                throw new InvalidDataException($"unknown codec in {name}");
        }

        if (data.Length != rawSize)
            throw new InvalidDataException($"size mismatch in {name}: got {data.Length}, expected {rawSize}");
        return data;
    }

    private static byte[] ReadAll(Stream s, long rawSize, string name)
    {
        if (rawSize > int.MaxValue) throw new InvalidDataException($"entry too large: {name}");
        var buf = new byte[rawSize];
        var got = 0;
        while (got < buf.Length)
        {
            var n = s.Read(buf, got, buf.Length - got);
            if (n <= 0) throw new InvalidDataException($"truncated payload in {name}");
            got += n;
        }

        if (s.ReadByte() >= 0) throw new InvalidDataException($"trailing bytes in {name}");
        return buf;
    }
}