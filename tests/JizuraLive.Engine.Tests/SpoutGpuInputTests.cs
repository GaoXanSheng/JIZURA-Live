using System.Reflection;
using JizuraLive.Pv;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Gpu;
using JizuraLive.Spout;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Xunit.Abstractions;
using DCommonAlphaMode = Vortice.DCommon.AlphaMode;
using DCommonPixelFormat = Vortice.DCommon.PixelFormat;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// M7「Spout 输入去 CPU 往返」的托管侧钉子。三条不同类型的证据：
///
///   1. <see cref="SpoutSourceFormat"/> 的白名单与诊断串——纯函数，无设备。
///      钉住「不兼容源必须可诊断」：源格式不在白名单时，使用者必须能拿到一条含格式名/数值
///      与「按每像素 4 字节读会得到错色」的可读串，而不是静默花屏。
///   2. <see cref="SpoutIn"/> 的**结构性反例钉子**（反射，无设备）：旧路径的
///      staging 纹理 + <c>Map</c> + <c>Marshal.Copy</c> + 托管 byte[] 像素缓冲一旦回归，
///      这条用例就红。它是结构检查、不是行为测试——见方法注释里的边界说明。
///   3. 设备级两条（<c>[GpuFact]</c>，WARP 可跑）：同设备拷贝是**独立副本**（借用期保证的 GPU 版）、
///      以及自有纹理能被包成 D2D surface 位图并画出来（含 alpha 模式能力边界的可复跑证据）。
///
/// 收帧耗时的真实量级、native 指针语义、源消失后的 ~67ms 有界代价都不在这里——
/// 那些只能靠 tools/SpoutProbe（G 段）实测，证据落在 DOC/PV-ENGINE-ARCHITECTURE.md 验证证据节。
/// </summary>
public class SpoutGpuInputTests
{
    private const int W = 640;
    private const int H = 360;

    private readonly ITestOutputHelper _out;

    public SpoutGpuInputTests(ITestOutputHelper output) => _out = output;

    // ============================================================ 1. 白名单

    /// <summary>
    /// 白名单只认 BGRA8，其余一律拒绝**且必须给得出可读诊断**。
    /// 这条用例存在的理由：拒绝而不给诊断等于把「静默错色」换成「静默黑屏」，用户还是不知道该去改发送端格式。
    /// </summary>
    [Fact]
    public void SpoutSourceFormat_RejectsUnsupportedFormats_WithReadableDiagnostic()
    {
        Assert.True(SpoutSourceFormat.IsSupported(Format.B8G8R8A8_UNorm),
            "B8G8R8A8_UNorm 是全引擎 BGRA8 约定，必须在白名单里");

        Format[] unsupported =
        [
            Format.R10G10B10A2_UNorm,
            Format.R16G16B16A16_Float,
            Format.R8G8B8A8_UNorm,        // 同为 4 字节/像素但通道顺序不同，按 BGRA 读一样错色
            Format.B8G8R8A8_UNorm_SRgb,   // 布局同 BGRA8 但带 sRGB 语义：采样会做 gamma 转换，同样按 BGRA8 消费会错色
            Format.Unknown,
        ];

        foreach (var f in unsupported)
        {
            Assert.False(SpoutSourceFormat.IsSupported(f), $"{f} 不该被当作 BGRA8 消费");

            var d = SpoutSourceFormat.DescribeUnsupported(f);
            Assert.False(string.IsNullOrWhiteSpace(d), $"{f} 必须给得出诊断串");
            Assert.True(d.Contains(f.ToString()) || d.Contains(((int)f).ToString()),
                $"诊断里要有格式名或数值，实际：{d}");
            Assert.Contains("4 字节", d);
        }
    }

    // ===================================================== 2. 结构性反例钉子

    /// <summary>
    /// **结构性反例钉子**（不是行为测试）：断言 <see cref="SpoutIn"/> 里不存在 CPU 往返的三件套——
    /// staging 纹理字段 <c>_stg</c>、任何 <c>byte[]</c> 字段、以及 <see cref="SpoutIn"/> 的
    /// <c>FrontPixels</c> 属性；同时断言新的对外面（纹理借用 + 代次 + 槽位 + 源格式 + 诊断）在。
    ///
    /// 为什么用反射而不是行为：CPU 往返的代价（每帧一次 GPU→CPU→GPU + 一个 w*h*4 托管数组）
    /// 在功能上「也能跑对」，靠像素断言抓不住——只有结构检查能在回归发生的那一次提交上报警。
    /// 收帧耗时的真实数字由 tools/SpoutProbe 的 G1 段实测（新路径 p50≈0.05ms、每帧托管分配≈49B）。
    /// </summary>
    [Fact]
    public void SpoutIn_StructuralPin_NoCpuRoundTrip()
    {
        var t = typeof(SpoutIn);
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static
                                 | BindingFlags.Public | BindingFlags.NonPublic;

        var fields = t.GetFields(all);
        Assert.DoesNotContain(fields, f => f.Name == "_stg");
        Assert.DoesNotContain(fields, f => f.Name.Contains("stg", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fields, f => f.FieldType == typeof(byte[]));

        Assert.Null(t.GetProperty("FrontPixels", all));

        Assert.NotNull(t.GetProperty("FrontTexture", all));
        Assert.NotNull(t.GetProperty("Generation", all));
        Assert.NotNull(t.GetProperty("FrontIndex", all));
        Assert.NotNull(t.GetProperty("SourceFormat", all));
        Assert.NotNull(t.GetProperty("FormatDiagnostic", all));

        _out.WriteLine("SpoutIn 字段：" + string.Join("、", fields.Select(f => $"{f.FieldType.Name} {f.Name}")));
    }

    // ================================================== 3. 设备级：独立副本

    /// <summary>
    /// 设备级钉住「我们的收帧是一份**独立副本**」：同设备两张 BGRA8 纹理，源用 D2D 清纯色，
    /// <c>Context.CopyResource(dst, src)</c> 后 dst 必须等于源；随后**只改源**（不再拷贝），
    /// dst 回读必须不变——这正是 S5/G3 借用期保证的 GPU 版：上游接着写共享纹理，
    /// 下游手里那口帧不会被改，合成器中途读帧不会被撕裂。
    /// </summary>
    [GpuFact]
    public void SameDeviceCopyIntoOwnedTexture_PreservesPixels_AndIsIndependentCopy()
    {
        var gpu = D3DDevice.TryCreate(out var err);
        if (gpu is null) GpuRequire.Fail(err);

        using (gpu)
        using (var host = D2DHost.Create(gpu!))
        using (var src = OffscreenTarget.Create(gpu!, host.Ctx, W, H, sampleable: true))
        using (var dst = OffscreenTarget.Create(gpu!, host.Ctx, W, H, sampleable: true))
        {
            var red = new Color4(1f, 0f, 0f, 1f);
            var blue = new Color4(0f, 0f, 1f, 1f);

            Clear(host, src, red);
            gpu!.Context.CopyResource(dst.Texture, src.Texture);
            gpu.Context.Flush();

            using (var copied = dst.Readback())
                AssertNear(copied, red, "同设备 CopyResource 之后 dst 必须等于源");

            // 只改源，不再拷贝：dst 是独立副本，不许跟着变
            Clear(host, src, blue);
            using (var srcNow = src.Readback())
                AssertNear(srcNow, blue, "源本身必须真的变了，否则下面的断言没有意义");
            using (var dstNow = dst.Readback())
                AssertNear(dstNow, red, "上游写入漏进了下游（dst 不是独立副本）");
        }
    }

    // ============================================ 4. 设备级：surface 位图能力边界

    /// <summary>
    /// 把「自有纹理 → D2D surface 位图」这条路连同 alpha 模式的能力边界固化成可复跑证据：
    ///   · Premultiplied + <c>BitmapOptions.None</c> 建得出位图，画进 D2D 目标后回读非透明、颜色一致；
    ///   · Straight 建得出就照实记（说明 D2D 接受 Straight，生产可沿用 <c>_premultiplied</c> 开关语义）；
    ///     建不出就断言异常可捕获，并**在同一张纹理上**再建一次 Premultiplied 位图证明资源没被弄脏。
    ///
    /// 实测（RTX 4060 Laptop，2026-10-15，tools/SpoutProbe G3 的 alpha 能力边界段）：
    /// surface 位图**只接受 Premultiplied**，声明 Straight 抛 UNSUPPORTEDPIXELFORMAT——
    /// 所以 SpoutSourceProvider 在 straight 声明下按「一条 Log.Warn + 不出图」交付，不静默变色。
    /// </summary>
    [GpuFact]
    public void OwnedTexture_WrapsIntoD2DBitmap_AndRenders()
    {
        var gpu = D3DDevice.TryCreate(out var err);
        if (gpu is null) GpuRequire.Fail(err);

        using (gpu)
        using (var host = D2DHost.Create(gpu!))
        using (var owned = CreateOwned(gpu!, W, H))
        using (var target = OffscreenTarget.Create(gpu!, host.Ctx, W, H))
        {
            var green = new Color4(0.2f, 0.8f, 0.3f, 1f);

            // 先把自有纹理填成已知色。用一张 Target 位图写穿回去，顺带证明「位图确实指着这张纹理」。
            using (var surface = owned.QueryInterface<IDXGISurface>())
            using (var writeBmp = host.Ctx.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
                       new DCommonPixelFormat(Format.B8G8R8A8_UNorm, DCommonAlphaMode.Premultiplied),
                       96f, 96f, BitmapOptions.Target)))
            {
                host.Draw.SetTarget(writeBmp, W, H);
                host.Draw.Begin();
                host.Draw.Clear(green);
                host.Ctx.Flush(out _, out _);
                host.Draw.End();
                host.Draw.SetTarget(null, 1, 1); // 摘掉：后面要把同一张纹理当源位图画
            }

            // ---- ① Premultiplied：建位图 → 画进 D2D 目标 → 回读 ----
            using (var bmp = Wrap(host, owned, DCommonAlphaMode.Premultiplied))
            {
                Assert.NotNull(bmp);
                host.Draw.SetTarget(target.Bitmap, W, H);
                host.Draw.Begin();
                host.Draw.Clear(new Color4(0f, 0f, 0f, 0f));
                host.Draw.DrawBitmap(bmp!, new Rect(0, 0, W, H));
                host.Ctx.Flush(out _, out _);
                host.Draw.End();

                using var img = target.Readback();
                var c = img.GetPixel(W / 2, H / 2);
                _out.WriteLine($"Premultiplied surface 位图绘制后中心像素 = R{c.Red}G{c.Green}B{c.Blue}A{c.Alpha}");
                Assert.True(c.Alpha > 200, "画进目标后不该是透明的（说明位图确实有内容）");
                AssertNear(c, green, "surface 位图绘制后的颜色必须与源一致");
            }

            // ---- ② Straight：能力边界照实记 ----
            Exception? straightError = null;
            try
            {
                using var straight = Wrap(host, owned, DCommonAlphaMode.Straight);
                _out.WriteLine("Straight surface 位图：**创建成功**（D2D 接受 Straight，生产可沿用 _premultiplied 开关语义）");
            }
            catch (Exception ex)
            {
                straightError = ex;
                _out.WriteLine($"Straight surface 位图：**被拒绝**（{ex.GetType().Name}）→ {ex.Message}");
            }

            // ---- ③ 资源没被弄脏：随后仍能建成 Premultiplied 位图 ----
            using (var again = Wrap(host, owned, DCommonAlphaMode.Premultiplied))
            {
                Assert.NotNull(again);
            }

            if (straightError is not null)
            {
                Assert.False(string.IsNullOrWhiteSpace(straightError.Message), "被拒时也要留下可读原因");
                _out.WriteLine("→ 结论：D2D 的 surface 位图不接受 Straight；SpoutSourceProvider 对声明 straight 的源"
                               + "给一条 Log.Warn 并返回 Empty（不静默变色），straight→premultiplied 转换 pass 不在本线。");
            }
        }
    }

    // ================================ 5. SpoutSourceProvider 的 alpha 接线

    /// <summary>
    /// **生产接线钉子**：<see cref="SpoutSourceProvider"/> 在**默认构造**下必须真的建得出位图，而且画得出来。
    ///
    /// 这条用例存在的理由：M7 换成 surface 位图后，构造参数默认值是 <c>premultiplied: false</c>
    /// → <c>EnsureBitmap</c> 声明 Straight → D2D 抛 UNSUPPORTEDPIXELFORMAT（就是上面第 4 节实测的能力边界）
    /// → 捕获后返回 null → <c>Pump</c> 交 Empty：默认接线（Character/VTS 层）**永不出图**，
    /// 症状是「一条 Log.Warn + 黑屏」而不是报错。上面那条只断言「异常可读」，抓不到接线错误，
    /// 所以这里断言的是**位图非 null 且真能画出源纹理的颜色**。
    ///
    /// 用反射调私有 <c>EnsureBitmap</c>：它只收一张纹理，不需要真发送端；而 <c>Pump</c> 要
    /// <c>SpoutIn</c> 真连上发送端，单测里给不出。所以这是接线检查，不是端到端取帧测试
    /// （端到端在 tools/SpoutProbe 的 G 段）。
    /// </summary>
    [GpuFact]
    public void SpoutSourceProvider_DefaultAlphaMode_ProducesDrawableBitmap()
    {
        var gpu = D3DDevice.TryCreate(out var err);
        if (gpu is null) GpuRequire.Fail(err);

        using (gpu)
        using (var host = D2DHost.Create(gpu!))
        using (var owned = CreateOwned(gpu!, W, H))
        {
            var want = new Color4(0.1f, 0.4f, 0.9f, 1f);

            // 先把自有纹理填成已知色：位图真指着这张纹理，才画得出这个颜色
            using (var surface = owned.QueryInterface<IDXGISurface>())
            using (var fill = host.Ctx.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
                       new DCommonPixelFormat(Format.B8G8R8A8_UNorm, DCommonAlphaMode.Premultiplied),
                       96f, 96f, BitmapOptions.Target)))
            {
                host.Draw.SetTarget(fill, W, H);
                host.Draw.Begin();
                host.Draw.Clear(want);
                host.Ctx.Flush(out _, out _);
                host.Draw.End();
                host.Draw.SetTarget(null, 1, 1);
            }

            // ① 默认构造 = 生产接线（PvPipeline 走的就是这个默认值）：必须建得出位图
            using (var provider = new SpoutSourceProvider("probe_default", gpu!, host.Ctx, NoSuchSender))
            {
                var bmp = EnsureBitmap(provider, owned);
                Assert.NotNull(bmp);
                AssertDraws(gpu!, host, bmp!, want, "默认构造（premultiplied 默认值）");
            }

            // ② 显式 premultiplied: true 与默认同一条路
            using (var provider = new SpoutSourceProvider("probe_premul", gpu!, host.Ctx, NoSuchSender,
                       premultiplied: true))
                Assert.NotNull(EnsureBitmap(provider, owned));

            // ③ 显式 straight：surface 位图不接受 → 按设计「一条告警 + 不出图」，不静默改用别的 alpha 模式
            using (var provider = new SpoutSourceProvider("probe_straight", gpu!, host.Ctx, NoSuchSender,
                       premultiplied: false))
                Assert.Null(EnsureBitmap(provider, owned));
        }
    }

    // ============================================================== 小工具

    /// <summary>本用例不取帧，只要一个名字：<c>EnsureBitmap</c> 只收纹理，不碰 <see cref="SpoutIn"/>。</summary>
    private const string NoSuchSender = "JizuraLiveNoSuchSender";

    /// <summary>反射调私有 <c>EnsureBitmap(tex, generation, frontIndex)</c>：代次 0 / 槽位 0。</summary>
    private static ID2D1Bitmap? EnsureBitmap(SpoutSourceProvider provider, ID3D11Texture2D tex)
    {
        var method = typeof(SpoutSourceProvider).GetMethod("EnsureBitmap",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (ID2D1Bitmap?)method!.Invoke(provider, [tex, 0L, 0]);
    }

    /// <summary>把位图原样画进离屏面并回读：中心与角点都必须是源纹理的颜色。</summary>
    private static void AssertDraws(D3DDevice gpu, D2DHost host, ID2D1Bitmap bmp, Color4 want, string why)
    {
        using var target = OffscreenTarget.Create(gpu, host.Ctx, W, H);
        host.Draw.SetTarget(target.Bitmap, W, H);
        host.Draw.Begin();
        host.Draw.Clear(new Color4(0f, 0f, 0f, 0f));
        host.Draw.DrawBitmap(bmp, new Rect(0, 0, W, H));
        host.Ctx.Flush(out _, out _);
        host.Draw.End();
        host.Draw.SetTarget(null, 1, 1); // 摘掉目标：后面 D3D 侧要动同一张纹理

        using var img = target.Readback();
        AssertNear(img, want, why);
    }

    /// <summary>与 <see cref="SpoutIn"/> 的自有帧纹理同规格（BGRA8 / Default / RT|SR / Mip1 / Array1 / CPU=None / Misc=None）。</summary>
    private static ID3D11Texture2D CreateOwned(D3DDevice gpu, int w, int h) =>
        gpu.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });

    /// <summary>把一张纹理包成 D2D surface 位图（与 SpoutSourceProvider.EnsureBitmap 同一手法）。</summary>
    private static ID2D1Bitmap1 Wrap(D2DHost host, ID3D11Texture2D tex, DCommonAlphaMode mode)
    {
        using var surface = tex.QueryInterface<IDXGISurface>();
        return host.Ctx.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
            new DCommonPixelFormat(Format.B8G8R8A8_UNorm, mode), 96f, 96f, BitmapOptions.None));
    }

    private static void Clear(D2DHost host, OffscreenTarget target, Color4 color)
    {
        host.Draw.SetTarget(target.Bitmap, target.Width, target.Height);
        host.Draw.Begin();
        host.Draw.Clear(color);
        host.Ctx.Flush(out _, out _); // Flush 只能在 BeginDraw/EndDraw 之间调
        host.Draw.End();
        host.Draw.SetTarget(null, 1, 1); // 摘掉目标：后面 D3D 侧要动同一张纹理，别让 D2D 还占着
    }

    /// <summary>中心 + 一个角点都在容差内（单点容易被 letterbox/边角骗）。</summary>
    private static void AssertNear(ReadbackImage img, Color4 want, string why)
    {
        foreach (var (x, y) in new[] { (img.Width / 2, img.Height / 2), (img.Width - 2, img.Height - 2) })
        {
            AssertNear(img.GetPixel(x, y), want, why);
        }
    }

    private static void AssertNear(ReadbackPixel c, Color4 want, string why)
    {
        const int tol = 8;
        static int E(float v) => (int)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f);
        var ok = Math.Abs(c.Red - E(want.R)) <= tol && Math.Abs(c.Green - E(want.G)) <= tol
                 && Math.Abs(c.Blue - E(want.B)) <= tol && Math.Abs(c.Alpha - E(want.A)) <= tol;
        Assert.True(ok, $"{why}：期望 R{E(want.R)}G{E(want.G)}B{E(want.B)}A{E(want.A)}，"
                        + $"实际 R{c.Red}G{c.Green}B{c.Blue}A{c.Alpha}");
    }
}
