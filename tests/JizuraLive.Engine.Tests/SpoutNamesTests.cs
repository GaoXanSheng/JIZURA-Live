// Spout 发送端枚举回归 / 格式探测：
//
// 1) 布局解析（纯函数）：按 2026-10 真机探测出的布局喂合成缓冲区——256 字节一槽、NUL 结尾、
//    槽内可能带上次注册的残留尾巴（ShoostudioSpout 被 Shoost 覆盖后剩 `udioSpout`）必须被切掉。
// 2) 真机枚举：自建一个已知名字的 SpoutSender（OpenDirectX 时写入注册表），断言 Enumerate() 能找到它；
//    同时把注册表原始内容 dump 到测试输出，作为布局的第一手证据。
//
// 同 SpoutInTests：Spout2 的发送端注册表是**进程级**共享内存，必须归入 EngineRegistry 串行集合
// （GpuRasterTests 会在另一线程启停 SpoutOut 的发送端，并发遍历会崩测试宿主）。

using System.IO;
using System.Text;
using System.Threading;
using JizuraLive.Spout;
using SharpDX.Direct3D11;
using SpoutDXWrapper;
using Xunit;
using Xunit.Abstractions;
using Device = SharpDX.Direct3D11.Device;

namespace JizuraLive.Engine.Tests;

[Collection("EngineRegistry")]
public class SpoutNamesTests
{
    private const string SenderName = "JIZURA-TEST-NAMES-SRC";

    private readonly ITestOutputHelper _out;

    public SpoutNamesTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void ParseRegistry_Reads_Slots_And_Cuts_Stale_Tail()
    {
        var buf = new byte[256 * 256];
        WriteSlot(buf, 0, "Shoost\0udioSpout\0");       // 真实 dump 里见过的残留尾巴
        WriteSlot(buf, 1, "VTubeStudioSpout\0");
        WriteSlot(buf, 2, "");                          // 空槽
        WriteSlot(buf, 4, "   ");                       // 空白：不是名字
        WriteSlot(buf, 5, "\x01\x02garbage\0");         // 不可打印：跳过

        var names = SpoutNames.ParseRegistry(buf);

        Assert.Equal(new[] { "Shoost", "VTubeStudioSpout" }, names);
    }

    [Fact]
    public void ParseRegistry_Handles_Short_Buffer()
    {
        Assert.Empty(SpoutNames.ParseRegistry([]));

        var buf = new byte[100]; // 连一个槽都不够：不越界、不产名字
        WriteSlot(buf, 0, "X\0");
        Assert.Empty(SpoutNames.ParseRegistry(buf));
    }

    [Fact]
    public void Enumerate_Finds_Registered_Sender()
    {
        using var device = new Device(SharpDX.Direct3D.DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        using var tex = new Texture2D(device, new Texture2DDescription
        {
            Width = 64,
            Height = 64,
            MipLevels = 1,
            ArraySize = 1,
            Format = SharpDX.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.Write,
            OptionFlags = ResourceOptionFlags.None,
        });

        var sender = new SpoutSender();
        try
        {
            sender.SetSenderName(SenderName);
            Assert.True(sender.OpenDirectX(device.NativePointer), "Sender.OpenDirectX 失败（还没注册前）");
            Assert.True(sender.SendTexture(tex.NativePointer), "SendTexture 失败（还没注册前）");

            // 注册表写入是发送端创建路径上的同步动作，通常立刻可见；给 2s 余量防慢盘
            var names = Array.Empty<string>();
            for (var i = 0; i < 20; i++)
            {
                names = SpoutNames.Enumerate(refresh: true).ToArray();
                if (names.Contains(SenderName)) break;
                Thread.Sleep(100);
            }

            _out.WriteLine("注册表 dump：\n" + SpoutNames.DumpRegistry());
            _out.WriteLine("枚举结果：" + string.Join(", ", names));

            Assert.Contains(SenderName, names);
        }
        finally
        {
            sender.Release();
            sender.Dispose();
        }
    }

    /// <summary>把一段文本（可含 NUL 尾巴）写进第 <paramref name="slot"/> 个槽。</summary>
    private static void WriteSlot(byte[] buf, int slot, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        Array.Copy(bytes, 0, buf, slot * 256, Math.Min(bytes.Length, 256));
    }
}
