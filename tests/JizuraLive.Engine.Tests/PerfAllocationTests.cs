// 性能专项回归测试（内存 / 卡顿）：
//   1) 字形分解（JText.Decompose）的暂存区复用——原来每个新字形要开 ~5-60MB 的 S×S 级缓冲，
//      全是 LOH 分配，播放时每遇到新字就灌一轮 gen2（实测停顿 0.8-4.3s = 界面秒级冻结）。
//   2) 舞台呈现（WriteableBitmap 在 UI 线程建、渲染泵线程写）——这条契约一旦破了，
//      泵线程会抛 InvalidOperationException（预览直接黑屏），所以钉在测试里。

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using JizuraLive.Engine;
using Xunit;
using Xunit.Abstractions;

namespace JizuraLive.Engine.Tests;

[Collection("EngineRegistry")]
public class PerfAllocationTests
{
    private readonly ITestOutputHelper _out;

    public PerfAllocationTests(ITestOutputHelper output) => _out = output;

    /// <summary>用一批不重复的汉字把字形缓存逐个打冷（每个字都是一次 decompose）。</summary>
    private const string Glyphs = "永和九年至癸丑暮春会于会稽山阴之兰亭修禊事也群贤毕至少长咸集";

    [Fact]
    public void 字形分解复用暂存区不灌LOH()
    {
        EngineBoot.Install();
        JFonts.ScanFontDirs();

        // 首字把暂存区撑到目标尺寸（res=256 → S=372，各块一次成型），之后逐字测分配
        _ = JText.Glyphs.Get("", Glyphs[0].ToString(), 200);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var gen2Before = GC.CollectionCount(2);
        var n = 0;
        foreach (var ch in Glyphs.Skip(1))
        {
            _ = JText.Glyphs.Get("", ch.ToString(), 200);
            n++;
        }

        var perGlyph = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)n;
        var gen2 = GC.CollectionCount(2) - gen2Before;
        _out.WriteLine($"字形分解：{n} 字，平均 {perGlyph / 1024:F0} KB/字，gen2 次数 {gen2}");

        // 修复前实测 ~5MB/字（res=256 档），gen2 每几个字就触发一次
        Assert.True(perGlyph < 1024 * 1024,
            $"每字分配 {perGlyph / 1024:F0} KB，暂存区复用失效（LOH 又回来了）");
    }

    [Fact]
    public void 舞台位图只能在UI线程写()
    {
        WriteableBitmap? bmp = null;
        Dispatcher? disp = null;
        Exception? uiEx = null;
        using var ready = new ManualResetEventSlim(false);
        var ui = new Thread(() =>
        {
            try
            {
                disp = Dispatcher.CurrentDispatcher;
                // 位图在 UI 线程建（WriteableBitmap 是 DispatcherObject，构造也要在线程上）
                disp.BeginInvoke(() =>
                {
                    bmp = new WriteableBitmap(64, 36, 96, 96, PixelFormats.Pbgra32, null);
                    ready.Set();
                });
                Dispatcher.Run(); // 必须真跑消息泵，否则主线程的 Invoke 会被取消
            }
            catch (Exception ex)
            {
                uiEx = ex;
                ready.Set();
            }
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.IsBackground = true;
        ui.Start();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(10)), "UI 线程没能建好位图");
        Assert.Null(uiEx);
        Assert.NotNull(bmp);
        Assert.NotNull(disp);

        // 契约一：非 UI 线程 Lock 会 VerifyAccess 直接抛——泵线程不能直接写舞台位图。
        // 这正是「泵只翻显示缓冲、UI 线程做一次 memcpy」这个分工存在的原因；
        // 哪天有人想省掉那次 memcpy 直接让泵写，这条测试会先红。
        var offThread = Record.Exception(() => bmp!.Lock());
        Assert.IsType<InvalidOperationException>(offThread);

        // 契约二：UI 线程自己写正常（UI 侧那次 memcpy 的落点）
        Exception? onUi = null;
        disp.Invoke(() =>
        {
            try
            {
                bmp!.Lock();
                try
                {
                    unsafe
                    {
                        var p = (byte*)bmp.BackBuffer;
                        for (var i = 0; i < 64 * 36; i++)
                        {
                            p[i * 4] = 0x10;
                            p[i * 4 + 1] = 0x20;
                            p[i * 4 + 2] = 0x30;
                            p[i * 4 + 3] = 0xFF;
                        }
                    }

                    bmp.AddDirtyRect(new Int32Rect(0, 0, 64, 36));
                }
                finally
                {
                    bmp.Unlock();
                }
            }
            catch (Exception ex)
            {
                onUi = ex;
            }
        });
        Assert.Null(onUi);

        disp.InvokeShutdown();
        ui.Join(3000);
    }
}
