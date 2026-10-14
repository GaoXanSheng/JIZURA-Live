// VTS 插件 API 客户端回归：
//   · 真机联调（VTS 在本机跑着时）：连上 ws://127.0.0.1:8001，未授权会话应停在「待授权」并报到版本号——
//     这条把「协议/握手/状态机」钉在真 VTS 上（VTS 没开则直接跳过，不误报）。
//   · 纯逻辑：VtsFraming 的记录语义（取景数据是 MoveModel 的参数形状，客户端回读也用它承载）。
// 不碰授权弹窗（那一步必须由人点「允许」）：测试只走到 NeedAuth。

using System.Threading;
using JizuraLive.Vts;
using Xunit;
using Xunit.Abstractions;

namespace JizuraLive.Engine.Tests;

public class VtsApiTests
{
    private readonly ITestOutputHelper _out;

    public VtsApiTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void Connects_To_Running_Vts_And_Reports_Version()
    {
        using var api = new VtsApi();
        api.Start();
        try
        {
            // 连不上（VTS 没开）= 合法状态，直接跳过
            var deadline = Environment.TickCount64 + 8_000;
            while (Environment.TickCount64 < deadline && api.State == VtsState.Disconnected)
                Thread.Sleep(200);

            if (api.State == VtsState.Disconnected)
            {
                _out.WriteLine("VTS 没在运行，跳过真机联调");
                return;
            }

            _out.WriteLine($"状态={api.State} 版本={api.Version}");
            Assert.NotEqual("", api.Version);            // APIStateResponse 报的 VTS 版本
            Assert.True(api.State is VtsState.NeedAuth or VtsState.Ready or VtsState.AwaitApproval,
                $"连上后状态不该是 {api.State}");
            Assert.Equal("", api.Hotkeys.Count == 0 ? "" : "有热键"); // 未授权拿不到热键列表：应为空
        }
        finally
        {
            api.Stop();
        }
    }

    [Fact]
    public void Framing_Record_Is_Value_Semantics()
    {
        var a = new VtsFraming(0.6, 0.1, -0.2, 5);
        var b = new VtsFraming(0.6, 0.1, -0.2, 5);
        Assert.Equal(a, b); // record：值相等（测试里用来比对下发/回读的取景）

        // VTS 官方支持 -100 到 100，全身取景典型参数为 size=-45, y=-0.15
        var fullBody = new VtsFraming(-45, 0, -0.15, 0);
        Assert.Equal(-45, fullBody.Size);
        Assert.Equal(-0.15, fullBody.Y);
    }
}
