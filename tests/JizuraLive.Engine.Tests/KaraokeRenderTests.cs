using JizuraLive.Engine;
using JizuraLive.Pv;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Gpu;
using JizuraLive.Render.Text;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 逐字动画的像素级验收（照 TypographyTests 的回读写法）：只查「画没画」是假绿——
/// pop 的词没到点就绝不能有字形像素，karaoke 的高亮色与底色必须同时出现在一帧里。
/// </summary>
public class KaraokeRenderTests
{
    private const int W = 640;
    private const int H = 360;

    /// <summary>首词 1.1s 才开始（句 1.0s 起）：句内、词前这一刻必须什么都不画。</summary>
    [GpuFact]
    public void PopShowsNoGlyphBeforeTheFirstWordStarts()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();

        using (gpu)
        using (var host = D2DHost.Create(gpu))
        using (var provider = new TypographyLayerProvider("pop_test", gpu, host, W, H))
        {
            provider.SetClips([AnimatedClip("pop_1", 1000, 4000, "Pop")]);

            provider.Render(1.05); // 首词开始前 50ms：句已命中，但没有任何词到达自己的起点
            using (var before = ReadbackOrFail(provider, "首词开始前"))
                Assert.Equal(0, CountVisiblePixels(before));

            provider.Render(1.40); // 首词已开始 300ms：回弹走完，字形应当可见
            using (var after = ReadbackOrFail(provider, "首词开始后"))
                Assert.True(CountVisiblePixels(after) > 0, "首词开始后 300ms 应当画出字形");
        }
    }

    /// <summary>1.6s：首词已唱完（高亮色）、后两词还没唱到（底色），两色必须在同一帧里同时存在。</summary>
    [GpuFact]
    public void KaraokeDrawsHighlightOverBaseColor()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();

        using (gpu)
        using (var host = D2DHost.Create(gpu))
        using (var provider = new TypographyLayerProvider("karaoke_test", gpu, host, W, H))
        {
            provider.SetClips([AnimatedClip("karaoke_1", 1000, 4000, "Karaoke")]);

            provider.Render(1.6);
            using var image = ReadbackOrFail(provider, "句中");

            Assert.True(HasOpaquePixelNear(image, 0xff, 0x6f, 0xae), "已唱完的词应当是高亮色 #ff6fae");
            Assert.True(HasOpaquePixelNear(image, 0xff, 0xff, 0xff), "还没唱到的词应当仍是底色 #ffffff");
        }
    }

    /// <summary>typewriter 与 pop 走同一套「词开始前不可见」，只是淡入时长不同。</summary>
    [GpuFact]
    public void TypewriterShowsNoGlyphBeforeTheFirstWordStarts()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();

        using (gpu)
        using (var host = D2DHost.Create(gpu))
        using (var provider = new TypographyLayerProvider("typewriter_test", gpu, host, W, H))
        {
            provider.SetClips([AnimatedClip("tw_1", 1000, 4000, "Typewriter")]);

            provider.Render(1.05);
            using (var before = ReadbackOrFail(provider, "首词开始前"))
                Assert.Equal(0, CountVisiblePixels(before));

            provider.Render(1.40);
            using (var after = ReadbackOrFail(provider, "首词开始后"))
                Assert.True(CountVisiblePixels(after) > 0, "首词开始后应当画出字形");
        }
    }

    /// <summary>
    /// 三种逐字动画都必须**横向居中**：逐字路径用 <c>cx + X[t]</c> 左对齐画字，
    /// 所以缓存里的 X 必须是「相对行中心的左缘偏移」（BuildLayout 每行算完行宽后整体左移行宽/2）。
    /// 只断言「有/无像素」测不出这里——X 从 0 起累加会让整句从画面中心向右排（质心偏右），
    /// 而整句静态路径用 Center 对齐本来就是居中的，两者横向对不上。
    /// </summary>
    [GpuFact]
    public void AnimatedLyricsAreHorizontallyCentered()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();

        using (gpu)
        using (var host = D2DHost.Create(gpu))
        using (var provider = new TypographyLayerProvider("center_test", gpu, host, W, H))
        {
            // 末词 1.0s+1.7s = 2.7s 开始、400ms 后（3.1s）唱完；3.2s 这一刻三种动画的整句都完整可见
            // （句尾 4.0s 之前、进度 0.73 仍在进出场曲线之间 → alpha=1），质心才有「整句居中」的意义。
            foreach (var style in new[] { "Pop", "Typewriter", "Karaoke" })
            {
                provider.SetClips([AnimatedClip("center_" + style, 1000, 4000, style)]);

                provider.Render(3.2);
                using var image = ReadbackOrFail(provider, style);

                var centroid = CentroidX(image);
                Assert.True(Math.Abs(centroid - W / 2f) <= 24f,
                    $"{style} 的歌词应当横向居中：可见像素质心 X={centroid:F1}，画面中心 {W / 2f}");
            }
        }
    }

    /// <summary>
    /// 逐字时间与整句文本对不上（脏数据）：该句退回整句绘制，不能变成「整句消失」——
    /// 那份脏词表说 2.1s 才开始，照它排版这一刻是一个字都不该有的。
    /// </summary>
    [GpuFact]
    public void MismatchedWordsFallBackToWholeLine()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();

        using (gpu)
        using (var host = D2DHost.Create(gpu))
        using (var provider = new TypographyLayerProvider("dirty_test", gpu, host, W, H))
        {
            provider.SetClips(
            [
                new LyricClip("dirty_1", 1000, 4000, "あいう", false,
                    [new KrcWord(2100, 300, "×××")], "Karaoke"),
            ]);

            provider.Render(1.5);
            using var image = ReadbackOrFail(provider, "脏逐字时间");

            Assert.True(CountVisiblePixels(image) > 0, "逐字时间对不上时应退回整句绘制，而不是整句消失");
        }
    }

    /// <summary>五个词各 400ms、依次落在句内（StartMs 是绝对歌曲毫秒）；拼接等于整句文本。</summary>
    private static LyricClip AnimatedClip(string id, long startMs, long endMs, string style) =>
        new(id, startMs, endMs, "あいうえお", false,
            [
                new KrcWord(startMs + 100, 400, "あ"),
                new KrcWord(startMs + 500, 400, "い"),
                new KrcWord(startMs + 900, 400, "う"),
                new KrcWord(startMs + 1300, 400, "え"),
                new KrcWord(startMs + 1700, 400, "お"),
            ],
            style);

    /// <summary>回读离屏面；还没 Render 过就没有目标面，要显式失败而不是静默跳过。</summary>
    private static ReadbackImage ReadbackOrFail(TypographyLayerProvider provider, string when) =>
        provider.ReadbackTarget()
        ?? throw new InvalidOperationException($"没有可回读的离屏面（{when}）：Render 之后才有目标面");

    /// <summary>数非透明像素（BGRA8888 预乘，alpha 在每像素第 4 字节）。</summary>
    private static int CountVisiblePixels(ReadbackImage image)
    {
        var pixels = image.BgraPixels;
        var count = 0;
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) count++;

        return count;
    }

    /// <summary>可见像素（alpha≠0）的横向质心；没有可见像素时返回画面中心，交给调用方按容差判。</summary>
    private static float CentroidX(ReadbackImage image)
    {
        var pixels = image.BgraPixels;
        var w = image.Width;
        double sum = 0;
        long count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] == 0) continue;
            sum += (i / 4) % w;
            count++;
        }

        return count == 0 ? w / 2f : (float)(sum / count);
    }

    /// <summary>找接近某色的**全不透明**像素：回读是预乘 BGRA，只有 alpha=255 时通道才是原色。</summary>
    private static bool HasOpaquePixelNear(ReadbackImage image, byte r, byte g, byte b, int tolerance = 8)
    {
        var pixels = image.BgraPixels;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] != 255) continue;
            if (Math.Abs(pixels[i + 2] - r) <= tolerance
                && Math.Abs(pixels[i + 1] - g) <= tolerance
                && Math.Abs(pixels[i] - b) <= tolerance)
                return true;
        }

        return false;
    }
}
