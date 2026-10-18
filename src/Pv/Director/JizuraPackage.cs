using System.IO;
using System.IO.Compression;
using System.Text;

namespace JizuraLive.Pv;

internal enum JzCodec : byte
{
    Raw = 0,
    Brotli = 1,
    Gzip = 2
}

internal sealed record JizuraPackageEntry(string Name, JzCodec Codec, long RawSize, byte[] Data);

internal static class JizuraPackage
{
    public const int LatestVersion = 1;

    public static bool LooksLikePackage(ReadOnlySpan<byte> head) =>
        head.Length >= 4 && head[0] == (byte)'J' && head[1] == (byte)'Z' && head[2] == (byte)'L' && head[3] == (byte)'V';

    public static List<JizuraPackageEntry> ReadFile(string path) => Read(File.ReadAllBytes(path));

    public static List<JizuraPackageEntry> Read(byte[] bytes)
    {
        if (bytes.Length < 16 || !LooksLikePackage(bytes))
            throw new InvalidDataException("not a JZLV package");
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

    public static byte[] Build(byte[] projectJson, byte[]? audio)
    {
        var entries = new List<(string Name, byte[] Data, bool Raw)> { ("project.json", projectJson, false) };
        if (audio is { Length: > 0 }) entries.Add(("audio.bin", audio, true));

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
        {
            w.Write((byte)'J');
            w.Write((byte)'Z');
            w.Write((byte)'L');
            w.Write((byte)'V');
            w.Write((ushort)LatestVersion);
            w.Write((ushort)entries.Count);
            w.Write(0u);
            w.Write(0u);

            var headerLen = 16 + entries.Sum(e => 2 + Encoding.UTF8.GetByteCount(e.Name) + 24);
            var payloads = new List<byte[]>(entries.Count);
            long off = headerLen;
            foreach (var (name, data, raw) in entries)
            {
                byte[] packed;
                byte codec;
                if (raw)
                {
                    codec = 0;
                    packed = data;
                }
                else
                {
                    var src = new MemoryStream();
                    using (var br = new BrotliStream(src, CompressionLevel.Optimal, true))
                        br.Write(data, 0, data.Length);
                    packed = src.ToArray();
                    codec = 1;
                }

                payloads.Add(packed);
                var nameBytes = Encoding.UTF8.GetBytes(name);
                w.Write((byte)nameBytes.Length);
                w.Write(nameBytes);
                w.Write(codec);
                w.Write((ulong)data.LongLength);
                w.Write((ulong)packed.LongLength);
                w.Write((ulong)off);
                off += packed.LongLength;
            }

            foreach (var pl in payloads) w.Write(pl);
        }

        var bytes = ms.ToArray();
        var back = Read(bytes);
        var backProject = back.FirstOrDefault(e => e.Name == "project.json")
                          ?? throw new InvalidDataException("package missing project.json");
        if (!backProject.Data.AsSpan().SequenceEqual(projectJson))
            throw new InvalidDataException("package project.json round-trip mismatch");
        if (audio is { Length: > 0 })
        {
            var backAudio = back.FirstOrDefault(e => e.Name == "audio.bin");
            if (backAudio is null || !backAudio.Data.AsSpan().SequenceEqual(audio))
                throw new InvalidDataException("package audio round-trip mismatch");
        }

        return bytes;
    }

    public static void WriteFile(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    private static bool IsSafeName(string name) =>
        name.Length > 0 && name.Length <= 255
                        && name.IndexOf('\0') < 0 && name.IndexOf('\\') < 0 && !name.Contains("..")
                        && name[0] != '/';

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
                    data = ReadAll(br, rawSize, name);
                break;
            case JzCodec.Gzip:
                using (var src = new MemoryStream(payload.ToArray(), false))
                using (var gz = new GZipStream(src, CompressionMode.Decompress))
                    data = ReadAll(gz, rawSize, name);
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
