using JizuraLive.Render.Gpu;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 渲染核心的 GPU 冒烟测试。<see cref="D3DDevice.TryCreate"/> 在无 GPU/无 D3D 的环境
/// （CI 容器、WARP 也被禁的机器）允许失败——但**失败必须给出原因**，且成功时设备必须可用。
/// </summary>
public class D3DDeviceTests
{
    [Fact]
    public void TryCreate_ReturnsUsableDeviceOrActionableError()
    {
        var gpu = D3DDevice.TryCreate(out var error);

        if (gpu is null)
        {
            Assert.False(string.IsNullOrWhiteSpace(error),
                "设备创建失败时必须给出可诊断的原因（不能是空字符串）");
            return;
        }

        using (gpu)
        {
            Assert.NotNull(gpu.Device);
            Assert.NotNull(gpu.Context);
            Assert.NotNull(gpu.Factory);
            Assert.False(string.IsNullOrWhiteSpace(gpu.AdapterName));
            Assert.False(gpu.IsDeviceRemoved, "刚建的设备不应处于已移除状态：" + gpu.RemovedReasonText());
        }
    }

    [Fact]
    public void DebugRequested_OnlyByEnvFlag()
    {
        // 默认不开调试层（调试层在部分机器上会显著拖慢，且需要装 SDK 层）
        Assert.False(D3DDevice.DebugRequested);
    }
}
