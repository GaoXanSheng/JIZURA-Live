using System.IO;
using System.IO.Compression;
using System.Text;
using JizuraLive.Engine;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// KRC 解码（M5）：krc1 头 + 16 字节循环异或 → zlib → UTF-8（去 BOM）。
/// 测试自己按同一格式造密文，因此「解出来对不对」是往返验证，不是拿实现当标准。
/// </summary>
public class KrcDecoderTests
{
    private static readonly byte[] Key =
    [
        64, 71, 97, 119, 94, 50, 116, 71, 81, 54, 49, 45, 206, 210, 110, 105,
    ];

    private const string KrcText = """
        [ti:测试曲]
        [language:eyJsfQ==]
        [1000,5000]<0,400,0>あ<400,600,0>い
        """;

    /// <summary>按格式造密文：UTF-8（带 BOM）→ zlib → 逐字节异或 → 前面加 "krc1"。</summary>
    private static byte[] Encrypt(string text)
    {
        var plain = new List<byte> { 0xEF, 0xBB, 0xBF }; // BOM：解码时必须被吃掉
        plain.AddRange(Encoding.UTF8.GetBytes(text));

        using var buffer = new MemoryStream();
        using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(plain.ToArray());

        var compressed = buffer.ToArray();
        var cipher = new byte[4 + compressed.Length];
        "krc1"u8.CopyTo(cipher);
        for (var i = 0; i < compressed.Length; i++)
            cipher[4 + i] = (byte)(compressed[i] ^ Key[i % Key.Length]);

        return cipher;
    }

    [Fact]
    public void DecryptRoundTripsKrcTextWithoutBom()
    {
        var decrypted = KrcDecoder.Decrypt(Encrypt(KrcText));

        Assert.Equal(KrcText, decrypted);
        Assert.False(decrypted.StartsWith('\uFEFF'));

        // 解出来的文本就是 LrcParser 的输入：逐字时间一路到底
        var clip = Assert.Single(LrcParser.ParseClips(decrypted).Clips);
        Assert.Equal(1000, clip.StartMs);
        Assert.Equal(6000, clip.EndMs);
        Assert.Equal("あい", clip.Text);
        Assert.Equal(new long[] { 1000, 1400 }, clip.Words!.Select(w => w.StartMs).ToArray());
    }

    [Fact]
    public void IsKrcChecksTheFourByteMagic()
    {
        Assert.True(KrcDecoder.IsKrc(Encrypt(KrcText)));
        Assert.True(KrcDecoder.IsKrc("krc1"u8.ToArray()));
        Assert.False(KrcDecoder.IsKrc(Encoding.UTF8.GetBytes("[00:01.00]普通 LRC")));
        Assert.False(KrcDecoder.IsKrc("krc2"u8.ToArray()));
        Assert.False(KrcDecoder.IsKrc("krc"u8.ToArray()));
        Assert.False(KrcDecoder.IsKrc(null!));
    }

    [Fact]
    public void DecryptThrowsOnMissingMagic()
    {
        var ex = Assert.Throws<InvalidDataException>(
            () => KrcDecoder.Decrypt(Encoding.UTF8.GetBytes("[00:01.00]不是 KRC")));

        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void DecryptThrowsOnBrokenZlibPayload()
    {
        // 头对但异或出来的不是 zlib 流：必须报错，不能返回半截乱码
        var garbage = Encoding.UTF8.GetBytes("definitely not a zlib stream");
        var cipher = new byte[4 + garbage.Length];
        "krc1"u8.CopyTo(cipher);
        for (var i = 0; i < garbage.Length; i++)
            cipher[4 + i] = (byte)(garbage[i] ^ Key[i % Key.Length]);

        var ex = Assert.Throws<InvalidDataException>(() => KrcDecoder.Decrypt(cipher));

        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }
}
