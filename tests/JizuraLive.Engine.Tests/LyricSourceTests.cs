using System.Text;
using JizuraLive.App.Cloud;
using JizuraLive.App.I18n;
using JizuraLive.Server.Models;
using Xunit;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 云端歌词来源（歌词面板「歌词版本」下拉 + 音译/翻译勾选即时重生成的数据源）：
/// BuildLrc 按格式分派，Label 是候选版本的下拉文案。
/// </summary>
public class LyricSourceTests
{
    [Fact]
    public void Krc来源_按勾选生成注音()
    {
        // language 块是 base64(JSON)；两块分别被脚本分类成音译（拉丁）/翻译（汉字）
        var blob = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            """{"content":[{"type":0,"lyricContent":[["yoake"],["sora"]]},{"type":1,"lyricContent":[["黎明"],["天空"]]}]}"""));
        var krc = "[0,1000]<0,500,0>夜<500,500,0>明\r\n"
                  + "[1000,1000]<0,1000,0>空\r\n"
                  + $"[language:{blob}]\r\n";
        var src = new LyricSource { Source = "kugou", Fmt = "krc", Text = krc };

        var both = src.BuildLrc(wantRoma: true, wantTrans: true);
        Assert.Contains("夜明", both);
        Assert.Contains("yoake", both);
        Assert.Contains("黎明", both);

        var none = src.BuildLrc(wantRoma: false, wantTrans: false);
        Assert.Contains("夜明", none);
        Assert.DoesNotContain("yoake", none);
        Assert.DoesNotContain("黎明", none);
    }

    [Fact]
    public void 普通Lrc来源_原样输出且标注无注音()
    {
        var src = new LyricSource { Source = "kugou", Fmt = "lrc", Text = "[00:01.00]hello\n" };
        Assert.Equal("[00:01.00]hello\n", src.BuildLrc(true, true));
        Assert.False(src.HasNotes);
    }

    [Fact]
    public void 网易云来源_主词与翻译按时间戳对齐()
    {
        var lrc = new List<Krc.NeRow> { new(1000, "夜明け"), new(3000, "空") };
        var trans = new List<Krc.NeRow> { new(1000, "黎明"), new(3000, "天空") };
        var src = new LyricSource
        {
            Source = "netease", Fmt = "ne", NeRaw = (lrc, trans, new List<Krc.NeRow>())
        };

        var text = src.BuildLrc(wantRoma: true, wantTrans: true);
        Assert.Contains("夜明け", text);
        Assert.Contains("黎明", text);
        Assert.Contains("天空", text);
    }

    [Fact]
    public void 候选版本文案_来源加上传者_匿名上传者不显示()
    {
        L.Initialize(null); // 测试进程无 WPF Application，L 内部已防护
        Assert.Equal("官方", LyricSource.Label(new KgLyricCandidate { From = "官方", Nick = "热心用户" }));
        Assert.Equal("官方 · 小明", LyricSource.Label(new KgLyricCandidate { From = "官方", Nick = "小明" }));
        Assert.Equal("其他版本", LyricSource.Label(new KgLyricCandidate { From = "", Nick = "" }));
    }
}
