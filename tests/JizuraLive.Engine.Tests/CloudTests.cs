using System.IO;
using System.IO.Compression;
using System.Text;
using JizuraLive.App.Cloud;
using JizuraLive.Server.Models;
using Xunit;
using Xunit.Abstractions;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// M6 云音乐纯逻辑测试（不依赖网络）：KRC 解密 roundtrip + 逐字时间轴解析 + JIZURA LRC 生成 +
/// 网易 LRC 展开/重建 + 文件名清洗 + 跟随匹配的标题清洗/歌手拆分/挑歌评分。
/// </summary>
public class CloudTests
{
    private readonly ITestOutputHelper _out;

    public CloudTests(ITestOutputHelper output) => _out = output;

    // krc.ts 同款 16 字节解密 key（测试构造加密样例用，逐字节对位）
    private static readonly byte[] KrcKey =
    [
        0x40, 0x47, 0x61, 0x77, 0x5E, 0x32, 0x74, 0x47,
        0x51, 0x36, 0x31, 0x2D, 0xCE, 0xD2, 0x6E, 0x69
    ];

    /// <summary>构造合法 .krc：krc1 魔数 + key 循环异或 + zlib（KRC 解密的逆过程）。</summary>
    private static byte[] MakeKrcFile(string plaintext)
    {
        var bodyMs = new MemoryStream();
        using (var zlib = new ZLibStream(bodyMs, CompressionMode.Compress, true))
            zlib.Write(Encoding.UTF8.GetBytes(plaintext));
        var body = bodyMs.ToArray();
        for (var i = 0; i < body.Length; i++) body[i] ^= KrcKey[i % 16];
        var data = new byte[body.Length + 4];
        Encoding.ASCII.GetBytes("krc1").CopyTo(data, 0);
        body.CopyTo(data, 4);
        return data;
    }

    private const string SampleKrc =
        "[ti:テスト曲]\r\n" +
        "[offset:200]\r\n" +
        "[2000,3000]<2000,1500,0>你好<3500,1500,0>世界\r\n" +
        "[6000,4000]\r\n" +
        "[10000,3000]<10000,3000,0>再/见\r\n";

    [Fact]
    public void Krc解密_按key循环异或与zlib还原()
    {
        var decrypted = Krc.Decrypt(MakeKrcFile(SampleKrc));
        Assert.Equal(SampleKrc, decrypted);
    }

    [Fact]
    public void Krc解密_缺魔数抛错()
    {
        Assert.ThrowsAny<Exception>(() => Krc.Decrypt("[00:01.00]x"u8.ToArray()));
        Assert.ThrowsAny<Exception>(() => Krc.Decrypt([0x6B, 0x72, 0x63, 0x32, 0x78, 0x78]));
    }

    [Fact]
    public void Krc逐字解析_行与字时间轴()
    {
        var text = Krc.Decrypt(MakeKrcFile(SampleKrc));
        var lines = Krc.Parse(text);
        Assert.Equal(3, lines.Count); // 间奏空行也是 timed 行

        var l0 = lines[0];
        Assert.Equal(2000, l0.StartMs);
        Assert.Equal(3000, l0.DurMs);
        Assert.Equal("你好世界", l0.Text);
        Assert.Equal(2, l0.Words.Count);
        Assert.Equal("你好", l0.Words[0].Text);
        Assert.Equal((2000L, 1500L), (l0.Words[0].StartMs, l0.Words[0].DurMs));
        Assert.Equal("世界", l0.Words[1].Text);
        Assert.Equal((3500L, 1500L), (l0.Words[1].StartMs, l0.Words[1].DurMs));

        Assert.Equal(6000, lines[1].StartMs);
        Assert.Equal("", lines[1].Text);
        Assert.Empty(lines[1].Words);

        Assert.Equal("再/见", lines[2].Text); // 半角 / 原样保留，LRC 生成阶段才换全角
        Assert.Single(lines[2].Words);
    }

    [Fact]
    public void KrcToLrc_时间轴停顿行与切镜头指令()
    {
        var lrc = Krc.KrcToLrc(Krc.Decrypt(MakeKrcFile(SampleKrc)));
        _out.WriteLine(lrc);
        var rows = lrc.Replace("\r", "").Split('\n');
        // offset:200 → 2000-200=1800ms
        Assert.Equal("[00:01.80]你好世界", rows[0]);
        Assert.Equal("", rows[1]); // 间奏 = JIZURA 的停顿
        Assert.Equal("[00:09.80]再／见", rows[2]); // 10000-200=9800；/ → ／（半角 / 是切镜头指令）
        Assert.EndsWith("\n", lrc);
    }

    [Fact]
    public void KrcToLrc_翻译按timed行对齐成注音()
    {
        var blob = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            """{"content":[{"type":1,"lyricContent":[["你好世界（翻）"],[""],["再见（翻）"]]}]}"""));
        var krc = SampleKrc.Replace("[offset:200]", $"[offset:200]\r\n[language:{blob}]");
        var lrc = Krc.KrcToLrc(Krc.Decrypt(MakeKrcFile(krc)));
        _out.WriteLine(lrc);
        var rows = lrc.Replace("\r", "").Split('\n');
        Assert.Equal("[00:01.80]你好世界|你好世界（翻）", rows[0]);
        Assert.Equal("", rows[1]); // 空行不吃注音（blob 里那行是空）
        Assert.Equal("[00:09.80]再／见|再见（翻）", rows[2]);
    }

    [Fact]
    public void KrcToLrc_可关掉注音()
    {
        var blob = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            """{"content":[{"type":1,"lyricContent":[["翻译一"]]}]}"""));
        var krc = $"[5000,1000]<5000,1000,0>词\r\n[language:{blob}]\r\n";
        var lrc = Krc.KrcToLrc(krc, wantRoma: false, wantTrans: false);
        Assert.Equal("[00:05.00]词\n", lrc);
    }

    [Fact]
    public void 语言块按脚本分类_拉丁算音译汉字算翻译()
    {
        Assert.Equal("roma", Krc.ClassifyLangRows(["ni hao shi jie"]));
        Assert.Equal("trans", Krc.ClassifyLangRows(["你好世界"]));
        Assert.Equal("none", Krc.ClassifyLangRows(["123"]));
    }

    // ---------------- 网易云 LRC ----------------

    [Fact]
    public void NeLrc展开_多时间戳与元数据()
    {
        var src = "[ti:Meta]\n" +
                  "{\"c\":[{\"tx\":\"作词：X\"}]}\n" +
                  "[00:10.50]single\n" +
                  "[00:20.00][00:40]multi\n" +
                  "no stamp line\n";
        var rows = Krc.NeParseLrc(src);
        Assert.Equal(
            new (long?, string)[]
            {
                (null, "作词：X"), (10500, "single"), (20000, "multi"), (40000, "multi"), (null, "no stamp line")
            },
            rows.Select(r => (r.Ms, r.Text)).ToArray());
    }

    [Fact]
    public void NeLrc重建_无时间戳行在前后句间均摊()
    {
        var lrc = new List<Krc.NeRow>
        {
            new(10000, "a"),
            new(null, "作词"),
            new(null, "作曲"),
            new(20000, "c"),
        };
        var text = Krc.NeBuildLrc(lrc, [], []);
        _out.WriteLine(text);
        var rows = text.Split('\n');
        Assert.Equal("[00:10.00]a", rows[0]);
        Assert.Equal("[00:13.33]作词", rows[1]);
        Assert.Equal("[00:16.66]作曲", rows[2]);
        Assert.Equal("[00:20.00]c", rows[3]);
    }

    [Fact]
    public void NeLrc重建_整曲无时间戳不补时()
    {
        var lrc = new List<Krc.NeRow> { new(null, "作词"), new(null, "作曲") };
        Assert.Equal("作词\n作曲\n", Krc.NeBuildLrc(lrc, [], []));
    }

    [Fact]
    public void NeLrc重建_翻译按时间戳对齐注音()
    {
        var lrc = new List<Krc.NeRow> { new(10000, "你好世界") };
        var trans = new List<Krc.NeRow> { new(10000, "hello world") };
        var text = Krc.NeBuildLrc(lrc, trans, [], wantRoma: false);
        Assert.Equal("[00:10.00]你好世界|hello world\n", text);
    }

    // ---------------- 文件名 ----------------

    [Fact]
    public void 文件名清洗_非法字符与保留名()
    {
        Assert.Equal("AC DC Back In Black", SongLoader.SanitizeFileName("AC/DC: Back In Black?"));
        Assert.Equal("_CON", SongLoader.SanitizeFileName("CON"));
        Assert.Equal("untitled", SongLoader.SanitizeFileName("  ???  "));
        Assert.Equal("a.b", SongLoader.SanitizeFileName("a.b..."));
    }

    [Fact]
    public void 音频缓存名_歌手缺省退化()
    {
        Assert.Equal("名前.mp3", SongLoader.AudioFileName("", "名前", "mp3"));
        Assert.Equal("A B - X.flac", SongLoader.AudioFileName("A/B", "X", "flac"));
        Assert.Equal("谁 - 歌.m4a", SongLoader.AudioFileName("谁", "歌", "M4A"));
        Assert.Equal("谁 - 歌.mp3", SongLoader.AudioFileName("谁", "歌", "")); // 空 ext 兜底 mp3
    }

    // ---------------- 跟随：标题清洗 / 歌手拆分 / 挑歌 ----------------

    [Fact]
    public void 跟随_标题剥垃圾括段但不伤正经括号()
    {
        Assert.Equal("Song Name", FollowController.CleanTitle("Song Name (Official Video)"));
        Assert.Equal("Song Name", FollowController.CleanTitle("【MV】Song Name【歌词】"));
        Assert.Equal("Song Name", FollowController.CleanTitle("Song Name (MV)"));
        Assert.Equal("Song Name (Deluxe)", FollowController.CleanTitle("Song Name (Deluxe)"));
        Assert.Equal("Lemon", FollowController.CleanTitle("Lemon"));
    }

    [Fact]
    public void 跟随_无歌手时标题两种顺序都试()
    {
        // 歌手非空 = 不拆标题，原样一对一
        Assert.Equal(new[] { ("Hotel California - Eagles", "Eagles") },
            FollowController.TitleArtistCandidates("Hotel California - Eagles", "Eagles"));
        // 歌手为空 = 「A - B」两边顺序各试一轮
        var cands = FollowController.TitleArtistCandidates("Hotel California - Eagles", "");
        Assert.Equal(new[] { ("Eagles", "Hotel California"), ("Hotel California", "Eagles") }, cands);
    }

    [Fact]
    public void 跟随_歌手拆分与匹配()
    {
        Assert.Equal(["a", "b"], FollowController.ArtistParts("A feat. B"));
        Assert.Equal(["a", "b", "c"], FollowController.ArtistParts("A / B、C"));
        Assert.True(FollowController.ArtistMatches("A / B", "B feat. C"));
        Assert.True(FollowController.ArtistMatches("周杰伦", "周杰伦 / 费玉清"));
        Assert.False(FollowController.ArtistMatches("A", ""));
        Assert.Equal("ablive", FollowController.Norm("A - B (Live)!"));
    }

    private static KgSong S(string name, string singer, long dur) =>
        new() { Name = name, Singer = singer, Duration = dur, Source = "kugou" };

    [Fact]
    public void 跟随_挑歌_标题歌手时长综合最优()
    {
        var list = new List<KgSong>
        {
            S("Song (Live)", "Someone", 200), // live 扣分
            S("Song", "Someone", 201),        // 全中
            S("Song", "其他歌手", 201),        // 歌手沾不上边重罚
        };
        var pick = FollowController.PickSong(list, "Song", "Someone", 201);
        Assert.Equal(201, pick!.Duration);
        Assert.Equal("Someone", pick.Singer);
    }

    [Fact]
    public void 跟随_挑歌_歌手是硬把关()
    {
        var list = new List<KgSong>
        {
            S("Song", "完全不同的歌手", 300),
            S("Song", "Someone feat. X", 201),
        };
        var pick = FollowController.PickSong(list, "Song", "Someone", 200);
        Assert.NotNull(pick);
        Assert.Contains("Someone", pick!.Singer);
    }

    [Fact]
    public void 跟随_挑歌_分太低宁缺毋滥()
    {
        var list = new List<KgSong> { S("Song", "完全不同的歌手", 300) };
        // 标题同 60 + 时长差>30 扣 10 + 歌手不沾边罚 45 = 5 < 30 → 不硬塞
        Assert.Null(FollowController.PickSong(list, "Song", "Someone", 200));
    }
}
