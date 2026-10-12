using System.IO;
using System.IO.Compression;
using System.Text;
using JizuraLive.App;
using JizuraLive.App.Cloud;
using JizuraLive.App.Views;
using JizuraLive.Server.KuGou;
using JizuraLive.Server.Models;
using JizuraLive.Server.Netease;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// M6 云音乐纯逻辑测试（不依赖网络）：KRC 解密 roundtrip + 逐字时间轴解析 + JIZURA LRC 生成 +
/// 网易 LRC 展开/重建 + 文件名清洗 + 跟随匹配的标题清洗/歌手拆分/挑歌评分 +
/// 下载记录索引与歌词缓存的命中判定（都在临时数据根里落盘往返）。
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
    public void 酷狗歌词响应_逐字字幕_先解码再解密()
    {
        // 真实响应形态：fmt=krc + content 是 base64（解码后才是 krc1 文件）。
        // 旧写法把 base64 直接丢给 Krc.Parse → 一行都解析不出 → 字幕层永远为空
        //（预览叠加/导出烤入/Spout 字幕通道全都不出画面），这里把回归钉住。
        var b64 = Convert.ToBase64String(MakeKrcFile(SampleKrc));
        var lines = Krc.ParseKgLyric("krc", b64);
        Assert.NotNull(lines);
        Assert.Equal(3, lines!.Count); // 间奏空行也是 timed 行
        Assert.Equal("你好世界", lines[0].Text);
        Assert.Equal(2, lines[0].Words.Count);
        Assert.Equal("再/见", lines[2].Text);

        Assert.Empty(Krc.Parse(b64)); // 旧写法的错误结果（base64 不是 KRC 文本）

        // 非 krc 格式（酷狗普通 LRC / 网易云）没有逐字 → null（调用方据此清空字幕层）
        Assert.Null(Krc.ParseKgLyric("lrc", b64));
        Assert.Null(Krc.ParseKgLyric("krc", ""));
        Assert.Null(Krc.ParseKgLyric(null, null));
    }

    [Fact]
    public void Krc逐字解析_行与字时间轴()    {
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

    [Fact]
    public void ParseLrc_标准时间标签_成功解析字幕行与时长()
    {
        var src = "[ti:Test]\n" +
                  "[00:02.50]第一句歌词\n" +
                  "[00:06.00]第二句歌词|注音忽略\n" +
                  "[间奏 4]\n" +
                  "[00:12.00]第三句歌词\n";
        var lines = Krc.ParseLrc(src);
        Assert.NotNull(lines);
        Assert.Equal(3, lines!.Count);
        Assert.Equal(2500, lines[0].StartMs);
        Assert.Equal("第一句歌词", lines[0].Text);
        Assert.Equal(3500, lines[0].DurMs); // 6000 - 2500

        Assert.Equal(6000, lines[1].StartMs);
        Assert.Equal("第二句歌词", lines[1].Text);
        Assert.Equal(6000, lines[1].DurMs); // 12000 - 6000

        Assert.Equal(12000, lines[2].StartMs);
        Assert.Equal("第三句歌词", lines[2].Text);
        Assert.Equal(4000, lines[2].DurMs); // 末行兜底 4000ms
    }

    [Fact]
    public void ParseLrc_纯文本无时间戳_返回null()
    {
        Assert.Null(Krc.ParseLrc("我还记得黎明的颜色\n远处响起渐渐散去的声音"));
        Assert.Null(Krc.ParseLrc(""));
        Assert.Null(Krc.ParseLrc(null));
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

    // ---------------- 歌单 ID 解析（PlaylistPane 的粘贴框） ----------------

    [Fact]
    public void 歌单ID解析_链接与纯ID()
    {
        // 酷狗：global_collection_id 优先
        Assert.Equal("collection_3_654116618_1_0", CloudPanel.ParsePlaylistId(
            "https://www.kugou.com/yy/special/single?global_collection_id=collection_3_654116618_1_0"));
        Assert.Equal("collection_3_1_2_3", CloudPanel.ParsePlaylistId("  collection_3_1_2_3  ")); // 纯 ID 可含下划线/连字符
        // 网易云：id= 与 /playlist/ 路径式
        Assert.Equal("24381616", CloudPanel.ParsePlaylistId("https://music.163.com/playlist?id=24381616"));
        Assert.Equal("24381616", CloudPanel.ParsePlaylistId("https://music.163.com/playlist/24381616"));
        Assert.Equal("24381616", CloudPanel.ParsePlaylistId("24381616"));
        // 认不出的输入返回空（不当作 ID 去打网络）
        Assert.Equal("", CloudPanel.ParsePlaylistId(""));
        Assert.Equal("", CloudPanel.ParsePlaylistId("abc"));
        Assert.Equal("", CloudPanel.ParsePlaylistId("https://music.163.com/"));
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

    // ---------------- 下载记录索引（cloud\downloads.json） ----------------

    private static string CloudPath(string name) => Path.Combine(AppDirs.CloudDir, name);

    private static void WriteCloudBytes(string name, int size)
    {
        Directory.CreateDirectory(AppDirs.CloudDir);
        File.WriteAllBytes(CloudPath(name), new byte[size]);
    }

    [Fact]
    public void 下载记录_命中判定_换音质换平台不算命中()
    {
        AudioIndex.Reload();
        var id = "t" + Guid.NewGuid().ToString("N");
        const string file = "索引测试 - 命中.mp3";
        try
        {
            WriteCloudBytes(file, 1024);
            AudioIndex.Put("kugou", id, "320", file, 1024, "mp3", false, "命中", "索引测试");
            AudioIndex.Reload(); // 走一遍落盘 → 读盘

            Assert.NotNull(AudioIndex.Get("kugou", id, "320"));
            Assert.Null(AudioIndex.Get("kugou", id, "128"));   // 音质不同的记录：不能拿 320 的文件顶 128
            Assert.Null(AudioIndex.Get("netease", id, "320")); // 别的平台
            Assert.Null(AudioIndex.Get("kugou", id + "x", "320"));

            // 文件长度变成别的值（被覆盖过 / 没下完）：记录作废，不能拿坏文件当命中
            WriteCloudBytes(file, 2048);
            AudioIndex.Reload();
            Assert.Null(AudioIndex.Get("kugou", id, "320"));

            // 文件没了：记录作废，Prune 顺手清掉
            File.Delete(CloudPath(file));
            AudioIndex.Reload();
            Assert.Null(AudioIndex.Get("kugou", id, "320"));
            AudioIndex.Prune();
            Assert.Null(AudioIndex.GetAny("kugou", id));
        }
        finally
        {
            try { File.Delete(CloudPath(file)); } catch { }
        }
    }

    [Fact]
    public void 下载记录_试听片段不当完整音轨命中()
    {
        AudioIndex.Reload();
        var id = "t" + Guid.NewGuid().ToString("N");
        const string file = "索引测试 - 试听.mp3";
        try
        {
            WriteCloudBytes(file, 512);
            AudioIndex.Put("kugou", id, "128", file, 512, "mp3", true, "试听", "索引测试");
            AudioIndex.Reload();

            var rec = AudioIndex.GetAny("kugou", id);
            Assert.NotNull(rec);                             // 记录在
            Assert.Null(AudioIndex.Get("kugou", id, "128")); // 但不命中：登录后可能已有完整音轨，要重新问接口
            Assert.False(AudioIndex.CanReuseExisting(rec, "128", false)); // 现在是完整音轨 → 重下覆盖
            Assert.True(AudioIndex.CanReuseExisting(rec, "128", true));   // 还是试听 → 沿用片段
            Assert.False(AudioIndex.CanReuseExisting(rec, "320", true));  // 换音质 → 重下
            Assert.True(AudioIndex.CanReuseExisting(null, "320", false)); // 无记录的老缓存：沿用现有口径
        }
        finally
        {
            try { File.Delete(CloudPath(file)); } catch { }
        }
    }

    [Fact]
    public void 下载记录_同名不同歌分配独立文件名()
    {
        AudioIndex.Reload();
        var idA = "a" + Guid.NewGuid().ToString("N");
        var idB = "b" + Guid.NewGuid().ToString("N");
        var first = AudioIndex.ResolveFileName("kugou", idA, "同一个歌手", "同一首歌", "mp3");
        Assert.Equal("同一个歌手 - 同一首歌.mp3", first);
        try
        {
            WriteCloudBytes(first, 256);
            AudioIndex.Put("kugou", idA, "128", first, 256, "mp3", false, "同一首歌", "同一个歌手");
            // 同名文件已被别的歌占用 → 换后缀，别互相覆盖
            Assert.Equal("同一个歌手 - 同一首歌 (2).mp3",
                AudioIndex.ResolveFileName("kugou", idB, "同一个歌手", "同一首歌", "mp3"));
            // 自己的歌反复解析拿到同一个名字（重复装载不会越排越多）
            Assert.Equal(first, AudioIndex.ResolveFileName("kugou", idA, "同一个歌手", "同一首歌", "mp3"));
        }
        finally
        {
            try { File.Delete(CloudPath(first)); } catch { }
        }
    }

    // ---------------- 歌词缓存（cloud\lyrics） ----------------

    [Fact]
    public void 歌词缓存_落盘往返_含翻译音译与候选版本()
    {
        var key = "t" + Guid.NewGuid().ToString("N");
        var ly = new KgLyricResponse
        {
            Ok = true,
            Fmt = "krc",
            Content = Convert.ToBase64String(MakeKrcFile(SampleKrc)),
            Trans = "dHJhbnM=",
            Roma = "cm9tYQ==",
            Candidates = [new KgLyricCandidate { Id = "c1", AccessKey = "ak", From = "kg", Nick = "nick" }],
        };
        try
        {
            Assert.Null(LyricCache.Get("kugou", key)); // 还没缓存
            LyricCache.Put("kugou", key, ly, "album-1");

            var got = LyricCache.Get("kugou", key);
            Assert.NotNull(got);
            Assert.Equal("krc", got!.Fmt);
            Assert.Equal(ly.Content, got.Content);
            Assert.Equal("dHJhbnM=", got.Trans);
            Assert.Equal("cm9tYQ==", got.Roma);
            Assert.Equal("c1", got.Candidates![0].Id);
            Assert.Equal("ak", got.Candidates[0].AccessKey);
            // 取回来的响应与接口返回同形：直接能进歌词装载（base64 → krc 解密 → 逐字解析）
            Assert.Equal(3, Krc.Parse(Krc.Decrypt(Convert.FromBase64String(got.Content!))).Count);

            // 失败响应 / 空内容不落缓存，下次仍然重新请求
            var bad = "b" + Guid.NewGuid().ToString("N");
            LyricCache.Put("kugou", bad, new KgLyricResponse { Ok = false }, null);
            LyricCache.Put("kugou", bad, new KgLyricResponse { Ok = true, Content = "" }, null);
            Assert.Null(LyricCache.Get("kugou", bad));
        }
        finally
        {
            try { File.Delete(LyricCache.FileOf("kugou", key)); } catch { }
        }
    }

    // ---------------- 歌单：归属解析与分组（字段取自真实响应，值做了替换） ----------------

    [Fact]
    public void 歌单解析_酷狗_默认收藏夹与自建收藏三分()
    {
        // /v7/get_all_list 实测片段：is_def 1=默认收藏、2=我喜欢；is_mine 恒为 0 不可用，
        // 自建靠 list_create_userid 是否等于自己
        var page = JObject.Parse("""
        {"data":{"info":[
          {"global_collection_id":"collection_3_111_1_0","name":"默认收藏","count":39,"is_def":1,"is_mine":0,"list_create_userid":111,"list_create_username":"我"},
          {"global_collection_id":"collection_3_111_2_0","name":"我喜欢","count":95,"is_def":2,"is_mine":0,"list_create_userid":111,"list_create_username":"我"},
          {"global_collection_id":"collection_3_111_3_0","name":"我的自建","count":12,"is_def":0,"is_mine":0,"list_create_userid":111,"list_create_username":"我"},
          {"global_collection_id":"collection_3_222_4_0","name":"别人的歌单","count":7,"is_def":0,"is_mine":0,"list_create_userid":222,"list_create_username":"别人"},
          {"global_collection_id":"","name":"没有 ID","count":1}
        ]}}
        """);
        var list = KuGouClient.ParsePlaylistsPage((JArray)page["data"]!["info"]!, 111);

        Assert.Equal(4, list.Count); // 缺 ID 的那条丢掉
        Assert.Equal([PlaylistKind.Liked, PlaylistKind.Liked, PlaylistKind.Mine, PlaylistKind.Subscribed],
            list.Select(p => p.Kind));
        Assert.Equal("我", list[2].Creator);
        Assert.Equal("别人", list[3].Creator);
        Assert.Equal(39, list[0].Count);
    }

    [Fact]
    public void 歌单解析_网易云_喜欢的音乐与订阅区开()
    {
        // /api/user/playlist 实测片段：specialType=5 是我喜欢的音乐（trackCount 常见为 0）
        var d = JObject.Parse("""
        {"code":200,"playlist":[
          {"id":4970371340,"name":"我喜欢的音乐","trackCount":0,"subscribed":false,"specialType":5,
           "creator":{"userId":333,"nickname":"我"}},
          {"id":6660664727,"name":"steam","trackCount":18,"subscribed":false,"specialType":0,
           "creator":{"userId":333,"nickname":"我"}},
          {"id":8888888888,"name":"收藏别人的","trackCount":40,"subscribed":true,"specialType":0,
           "creator":{"userId":444,"nickname":"别人"}}
        ]}
        """);
        var list = NeteaseClient.ParsePlaylists(d, 333);

        Assert.Equal(3, list.Count);
        Assert.Equal([PlaylistKind.Liked, PlaylistKind.Mine, PlaylistKind.Subscribed],
            list.Select(p => p.Kind));
        Assert.Equal("我喜欢的音乐", list[0].Name);
        Assert.Equal("别人", list[2].Creator);
        Assert.Equal(18, list[1].Count);
    }

    [Fact]
    public void 歌单分组_按归属三段_缺归属信息时合成一段()
    {
        var all = new List<PlaylistInfo>
        {
            new() { Name = "喜欢", Kind = PlaylistKind.Liked },
            new() { Name = "自建一", Kind = PlaylistKind.Mine },
            new() { Name = "自建二", Kind = PlaylistKind.Mine },
            new() { Name = "收藏的", Kind = PlaylistKind.Subscribed },
        };
        var g = CloudPanel.GroupPlaylists(all);
        Assert.True(g.Grouped);
        Assert.Single(g.Liked);
        Assert.Equal(2, g.Mine.Count);
        Assert.Single(g.Sub);

        // 平台没给归属信息：不假装分组，全部合成一段（否则歌单会凭空消失）
        var u = CloudPanel.GroupPlaylists(new List<PlaylistInfo> { new() { Name = "一" }, new() { Name = "二" } });
        Assert.False(u.Grouped);
        Assert.Equal(2, u.Mine.Count);
        Assert.Empty(u.Liked);
        Assert.Empty(u.Sub);
    }

    // ---------------- 工程音频自动备份去重（cache\audio\autosave_song.*） ----------------

    // ---------------- 云试听会话（纯逻辑，不碰音频设备） ----------------

    [Fact]
    public void 试听队列_上一首下一首与放完收场()
    {
        Assert.Equal(1, CloudPlayService.StepIndex(0, 1, 3));
        Assert.Equal(0, CloudPlayService.StepIndex(0, -1, 3));  // 开头按上一首：停在第一首
        Assert.Equal(0, CloudPlayService.StepIndex(-1, 1, 3));  // 还没起播时按下一位：从第一首（下标 0）开始
        Assert.Equal(-1, CloudPlayService.StepIndex(2, 1, 3));  // 最后一首放完：收场（不循环）
        Assert.Equal(-1, CloudPlayService.StepIndex(0, 1, 0));  // 空队列
    }

    [Fact]
    public void 试听同歌判定_平台加平台内身份()
    {
        var a = new KgSong { Source = "kugou", Hash = "H1", Name = "A" };
        var b = new KgSong { Source = "kugou", Hash = "H1", Name = "A（面板重建后的另一份对象）" };
        var c = new KgSong { Source = "kugou", Hash = "H2", Name = "A" };
        var d = new KgSong { Source = "netease", Id = "H1", Name = "A" };
        Assert.True(CloudPlayService.SameSong(a, b));   // 队列与列表是两批对象，按身份认领
        Assert.False(CloudPlayService.SameSong(a, c));  // 同平台不同歌
        Assert.False(CloudPlayService.SameSong(a, d));  // 平台不同（酷狗 Hash 与网易云 Id 撞值也不算同一首）
    }

    [Fact]
    public void 音频备份_同一份源文件只拷一次()
    {
        var src = Path.Combine(Path.GetTempPath(), $"jz-autosave-{Guid.NewGuid():N}.mp3");
        File.WriteAllBytes(src, new byte[4096]);
        string? dest = null;
        try
        {
            dest = AutosaveAudioCache.Mirror(src);
            Assert.NotNull(dest);
            Assert.Equal(4096, new FileInfo(dest!).Length);

            // 往备份里写标记字节再镜像一次：源没变 → 不重拷（标记还在）
            File.WriteAllBytes(dest!, Enumerable.Repeat((byte)0xAB, 4096).ToArray());
            Assert.Equal(dest, AutosaveAudioCache.Mirror(src));
            Assert.Equal(0xAB, File.ReadAllBytes(dest!)[0]);

            // 源文件变了（长度不同）→ 重拷
            File.WriteAllBytes(src, new byte[8192]);
            Assert.Equal(dest, AutosaveAudioCache.Mirror(src));
            Assert.Equal(8192, new FileInfo(dest!).Length);
        }
        finally
        {
            try { File.Delete(src); } catch { }
            if (dest != null)
            {
                try { File.Delete(dest); } catch { }
            }
        }
    }
}
