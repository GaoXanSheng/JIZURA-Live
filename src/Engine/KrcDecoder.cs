using System.IO;
using System.IO.Compression;
using System.Text;

namespace JizuraLive.Engine;

/// <summary>
/// 酷狗 KRC 文件 → 歌词文本。格式：4 字节 ASCII 头 <c>krc1</c>，其后整段与 16 字节密钥循环异或，
/// 得到 zlib 流，解压后是 UTF-8（可能带 BOM）的文本，正文由 <see cref="LrcParser"/> 识别。
///
/// 坏数据一律抛 <see cref="InvalidDataException"/> 并带上原因：解析器不该猜「这算不算 KRC」。
/// </summary>
public static class KrcDecoder
{
    private static readonly byte[] Key =
    [
        64, 71, 97, 119, 94, 50, 116, 71, 81, 54, 49, 45, 206, 210, 110, 105,
    ];

    private static readonly byte[] Magic = "krc1"u8.ToArray();

    public static bool IsKrc(byte[] data)
    {
        if (data is null || data.Length < Magic.Length) return false;
        for (var i = 0; i < Magic.Length; i++)
            if (data[i] != Magic[i]) return false;

        return true;
    }

    public static string Decrypt(byte[] krcFile)
    {
        if (!IsKrc(krcFile))
            throw new InvalidDataException("不是 KRC 文件：开头不是 ASCII \"krc1\"");

        var payload = new byte[krcFile.Length - Magic.Length];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)(krcFile[i + Magic.Length] ^ Key[i % Key.Length]);

        try
        {
            using var input = new MemoryStream(payload, writable: false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            // detectEncodingFromByteOrderMarks 顺手吃掉 UTF-8 BOM
            using var reader = new StreamReader(zlib, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"KRC 解压失败：{ex.Message}", ex);
        }
    }
}
