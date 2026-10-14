// PV 多路源的端到端接线（真 Spout 收发，覆盖泵线程那一半）：
//   · 本进程起两路 Spout 发送端（不同名字），SpoutOut 的 PV 合成把两路都连上（LiveIns 数量与连接态）；
//   · 有一路名字不存在时不拖累其他路：连上的那路照常推进世代号（暂停态也出帧的依据）。
// 单测（LiveLayerRenderTests）管「画出图」，这条管「收得进、连得上、世代号在走」——两头合起来
// 才是完整的「源进画面」链路。
//
// 与 SpoutInTests 同纪律：归入 EngineRegistry 串行集合（Spout 注册表与发送端是进程级状态）。

using System.Threading;
using JizuraLive.App;
using JizuraLive.Engine;
using JizuraLive.Spout;
using SharpDX.Direct3D11;
using SpoutDXWrapper;
using Xunit;
using Device = SharpDX.Direct3D11.Device;

namespace JizuraLive.Engine.Tests;

[Collection("EngineRegistry")]
public class PvPipelineTests
{
    [Fact]
    public void Pv_Composites_Two_Self_Senders()
    {
        using var device = MakeDevice();
        using var srcA = new FakeSender(device, "JIZURA-TEST-PV-A", new byte[] { 0, 0, 255, 255 });  // 红（BGRA）
        using var srcB = new FakeSender(device, "JIZURA-TEST-PV-B", new byte[] { 255, 0, 0, 255 });  // 蓝

        var pv = new PvOptions { Enabled = true };
        pv.Sources.Add(new LiveLayerOptions { Enabled = true, SenderName = "JIZURA-TEST-PV-A" });
        pv.Sources.Add(new LiveLayerOptions { Enabled = true, SenderName = "JIZURA-TEST-PV-B" });

        using var spout = StartSpout(pv);
        var (a, b) = WaitSources(spout, 2);

        Assert.True(a, "第一路源没连上（多路接收的槽位建错？）");
        Assert.True(b, "第二路源没连上（只建了一路接收端？）");
        Assert.True(spout.LiveGen > 0, "世代号没推进（暂停态泵不会继续出帧）");
        // 两路各自报自己的源尺寸（槽位对齐的证据：假源都是 256×128）
        Assert.Equal(256, spout.LiveIns[0].SourceWidth);
        Assert.Equal(128, spout.LiveIns[0].SourceHeight);
        Assert.Equal(256, spout.LiveIns[1].SourceWidth);
    }

    [Fact]
    public void Pv_With_One_Missing_Source_Keeps_The_Other()
    {
        using var device = MakeDevice();
        using var srcA = new FakeSender(device, "JIZURA-TEST-PV-C", new byte[] { 0, 255, 0, 255 });

        var pv = new PvOptions { Enabled = true };
        pv.Sources.Add(new LiveLayerOptions { Enabled = true, SenderName = "JIZURA-TEST-PV-C" });
        pv.Sources.Add(new LiveLayerOptions { Enabled = true, SenderName = "JIZURA-TEST-PV-NO-SUCH" });

        using var spout = StartSpout(pv);
        var (a, b) = WaitSources(spout, 2);

        Assert.True(a, "存在的源没连上");
        Assert.False(b, "不存在的源不该连上");
        Assert.True(spout.LiveGen > 0, "有源在动，世代号该推进");
    }

    // ---------------- 脚手架 ----------------

    private static Device MakeDevice() =>
        new(SharpDX.Direct3D.DriverType.Hardware, DeviceCreationFlags.BgraSupport);

    private static SpoutOut StartSpout(PvOptions pv)
    {
        EngineBoot.InstallPacks();
        var project = Project.NewDefault();
        project.Lyrics = "夜明けの色を/覚えてる";
        var pe = new PreviewEngine();
        pe.SetProject(project);
        Assert.NotNull(pe.Plan);

        var spout = new SpoutOut();
        var ok = spout.Start(pe, new SpoutOutOptions
        {
            SenderName = "JIZURA-TEST-PV-OUT",
            Width = 640,
            Height = 360,
            Fps = 30,
            LyricsEnabled = false,
            Pv = pv,
        });
        Assert.True(ok, "SpoutOut 启动失败");
        return spout;
    }

    /// <summary>等两路接收端各自的连接态（最多 5s）；返回 (第 1 路, 第 2 路)。</summary>
    private static (bool A, bool B) WaitSources(SpoutOut spout, int count)
    {
        for (var i = 0; i < 50; i++)
        {
            var ins = spout.LiveIns;
            if (ins.Count >= count)
            {
                var a = ins[0].Connected;
                var b = ins[1].Connected;
                if (a && b) return (true, true);
                // 不存在的源永远连不上：等够时间就按现状返回
                if (i >= 20 && a) return (a, b);
            }

            Thread.Sleep(100);
        }

        var final = spout.LiveIns;
        return (final.Count > 0 && final[0].Connected, final.Count > 1 && final[1].Connected);
    }

    /// <summary>本进程的一路假 Spout 源（固定色块的动态纹理，持续发布）。</summary>
    private sealed class FakeSender : IDisposable
    {
        private readonly Texture2D _tex;
        private readonly SpoutSender _sender = new();

        public FakeSender(Device device, string name, byte[] bgra)
        {
            _tex = new Texture2D(device, new Texture2DDescription
            {
                Width = 256,
                Height = 128,
                MipLevels = 1,
                ArraySize = 1,
                Format = SharpDX.DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0),
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.ShaderResource,
                CpuAccessFlags = CpuAccessFlags.Write,
                OptionFlags = ResourceOptionFlags.None,
            });
            Fill(device, _tex, bgra);
            _sender.SetSenderName(name);
            Assert.True(_sender.OpenDirectX(device.NativePointer), $"Sender.OpenDirectX 失败（{name}）");
            Assert.True(_sender.SendTexture(_tex.NativePointer), $"SendTexture 失败（{name}）");
        }

        public void Dispose()
        {
            try
            {
                _sender.Release();
                _sender.Dispose();
            }
            catch
            {
                // 释放期失败不该影响测试结论
            }

            _tex.Dispose();
        }

        private static unsafe void Fill(Device device, Texture2D tex, byte[] bgra)
        {
            var ctx = device.ImmediateContext;
            var m = ctx.MapSubresource(tex, 0, MapMode.WriteDiscard, MapFlags.None);
            try
            {
                var p = (byte*)m.DataPointer;
                var w = tex.Description.Width;
                var h = tex.Description.Height;
                for (var y = 0; y < h; y++)
                {
                    var row = p + (long)y * m.RowPitch;
                    for (var x = 0; x < w; x++)
                    {
                        row[x * 4 + 0] = bgra[0];
                        row[x * 4 + 1] = bgra[1];
                        row[x * 4 + 2] = bgra[2];
                        row[x * 4 + 3] = bgra[3];
                    }
                }
            }
            finally
            {
                ctx.UnmapSubresource(tex, 0);
            }
        }
    }
}
