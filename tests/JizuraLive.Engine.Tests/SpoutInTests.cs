// Spout 输入端回归：自产自销（本进程 SpoutSender → SpoutIn），把 SpoutDXWrapper 接收端的
// 调用语义钉在测试里。探针实测的语义（2026-10，见 SpoutIn.cs 头注）：
//   · SetReceiverName + OpenDirectX(设备指针) 即自动连接同名源，不需要 SelectSender；
//   · ReceiveTexturePtr() 每拍返回同一块纹理指针（未连接时为 0），是设备上可直接 CopyResource 的纹理；
//   · GetWidth/GetHeight = 源尺寸；IsConnected 在首帧到达后才为真；
//   · 纹理格式随源（本测试用 B8G8R8A8_UNorm，与 VTS/本程序发送端一致）。
//
// 图案刻意做成「上红下蓝」的非对称色块：一测通道序（BGRA 解错会把红蓝换位），
// 二测上下翻转（D3D 系的 y 轴约定弄反会上下颠倒）。

using System;
using System.Threading;
using JizuraLive.Spout;
using SharpDX.Direct3D11;
using SpoutDXWrapper;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;
using Device = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;

namespace JizuraLive.Engine.Tests;

// 必须归入 EngineRegistry（DisableParallelization）：Spout2 的发送端/接收端共享内存注册表是
// **进程级**状态，GpuRasterTests 会在另一线程启停 SpoutOut（建/销发送端）。两者并发时
// 一边遍历发送端列表、一边增删，实测能把测试宿主进程直接崩掉（原生 AV，非托管栈）。
[Collection("EngineRegistry")]
public class SpoutInTests
{
    private const string SenderName = "JIZURA-TEST-IN-SRC";

    private readonly ITestOutputHelper _out;

    public SpoutInTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void SpoutIn_Receives_Own_Sender_Frame()
    {
        using var device = new Device(SharpDX.Direct3D.DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        using var tex = MakeTexture(device, 256, 128);
        FillTopRedBottomBlue(tex);

        var sender = new SpoutSender();
        sender.SetSenderName(SenderName);
        Assert.True(sender.OpenDirectX(device.NativePointer), "Sender.OpenDirectX 失败");
        Assert.True(sender.SendTexture(tex.NativePointer), "SendTexture 失败");

        using var sink = new SpoutIn(device);
        var got = false;
        for (var i = 0; i < 60 && !got; i++)
        {
            got = sink.Pump(true, SenderName, premultiplied: false);
            if (!got) Thread.Sleep(10);
        }

        Assert.True(got, "SpoutIn 收不到自产自销的帧（接收端语义变了？）");
        Assert.True(sink.Connected);
        Assert.Equal(256, sink.SourceWidth);
        Assert.Equal(128, sink.SourceHeight);

        SKBitmap? copy = null;
        try
        {
            Assert.True(sink.TryCopyLatest(ref copy, out var seq));
            Assert.True(seq > 0);
            Assert.NotNull(copy);
            Assert.Equal(256, copy!.Width);
            Assert.Equal(128, copy.Height);

            var top = copy.GetPixel(128, 12);
            var bottom = copy.GetPixel(128, 116);
            _out.WriteLine($"上：{top}  下：{bottom}");
            Assert.True(top.Red > 200 && top.Blue < 60, $"顶部应为红（通道序/翻转？）：{top}");
            Assert.True(bottom.Blue > 200 && bottom.Red < 60, $"底部应为蓝（通道序/翻转？）：{bottom}");
        }
        finally
        {
            copy?.Dispose();
            sender.Release();
            sender.Dispose();
        }
    }

    [Fact]
    public void SpoutIn_Disabled_Or_Missing_Source_Is_Quiet()
    {
        using var device = new Device(SharpDX.Direct3D.DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        using var sink = new SpoutIn(device);

        // 关着：不建接收端、不产帧、不抛
        Assert.False(sink.Pump(false, "不存在的源", false));
        Assert.False(sink.Connected);
        Assert.Null(sink.FrontForRender);

        // 开着但源不在（VTS 没启动的常态）：返回 false 且不抛，留给下一次 Pump 重试
        Assert.False(sink.Pump(true, "JIZURA-TEST-IN-NO-SUCH-SENDER", false));
        Assert.False(sink.Connected);
        Assert.Null(sink.FrontForRender);
        Assert.Null(sink.LatestInfo);
    }

    private static Texture2D MakeTexture(Device device, int w, int h) => new(device, new Texture2DDescription
    {
        Width = w,
        Height = h,
        MipLevels = 1,
        ArraySize = 1,
        Format = SharpDX.DXGI.Format.B8G8R8A8_UNorm,
        SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0),
        Usage = ResourceUsage.Dynamic,
        BindFlags = BindFlags.ShaderResource,
        CpuAccessFlags = CpuAccessFlags.Write,
        OptionFlags = ResourceOptionFlags.None,
    });

    private static unsafe void FillTopRedBottomBlue(Texture2D tex)
    {
        var h = tex.Description.Height;
        var w = tex.Description.Width;
        var ctx = tex.Device.ImmediateContext;
        var m = ctx.MapSubresource(tex, 0, MapMode.WriteDiscard, MapFlags.None);
        try
        {
            var p = (byte*)m.DataPointer;
            for (var y = 0; y < h; y++)
            {
                var row = p + (long)y * m.RowPitch;
                var top = y < h / 2;
                for (var x = 0; x < w; x++)
                {
                    row[x * 4 + 0] = (byte)(top ? 0 : 255);   // B
                    row[x * 4 + 1] = 0;                       // G
                    row[x * 4 + 2] = (byte)(top ? 255 : 0);   // R
                    row[x * 4 + 3] = 255;                     // A
                }
            }
        }
        finally
        {
            ctx.UnmapSubresource(tex, 0);
        }
    }
}
