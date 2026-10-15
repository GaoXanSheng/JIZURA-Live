using Vortice.DXGI;

namespace JizuraLive.Spout;

/// <summary>
/// Spout 接收边界的**源格式白名单**与不兼容格式的诊断文案。
///
/// 为什么必须有这道闸：源纹理的格式由**发送端**决定（native <c>SetSenderFormat</c>），
/// 本引擎内部则统一 BGRA8（DOC/PV-DIRECTOR-SYSTEM.md §9：「支持的分辨率/格式在接收边界检查，
/// 不能把任意 DXGI 格式按 w*h*4 解释」）。旧实现把源纹理 CopyResource 到同格式的 staging，
/// 再按 <c>w*4</c> 逐行 <c>Marshal.Copy</c>——10/16 位源于是被按每像素 4 字节切成了错色/半行错位。
/// 那不是崩溃，是**静默花屏**：合成器看不出来，导出的成片里也看不出是 bug。
///
/// 所以白名单之外的格式一律：给一条可读诊断、**不出图**（见 SpoutIn.Pump）。
///
/// 纯函数、不依赖 D3D 设备——单测可以直接钉（tests/JizuraLive.Engine.Tests/SpoutGpuInputTests.cs）。
/// </summary>
public static class SpoutSourceFormat
{
    /// <summary>
    /// 该源格式能否按 BGRA8 语义安全消费。
    /// 白名单目前只有 <see cref="Format.B8G8R8A8_UNorm"/>——全引擎 BGRA8 约定。
    /// </summary>
    public static bool IsSupported(Format f) => f == Format.B8G8R8A8_UNorm;

    /// <summary>
    /// 不兼容格式的**可读**诊断：含格式名与数值，并说明「按每像素 4 字节读会得到错色」——
    /// 使用者（工作台状态条 / 日志）据此能判断该去改发送端格式，而不是去查渲染管线。
    /// </summary>
    public static string DescribeUnsupported(Format f) =>
        $"Spout 源格式 {(int)f}（{f}）不在接收白名单内：本引擎只按 BGRA8 消费源纹理"
        + $"（白名单：B8G8R8A8_UNorm/{(int)Format.B8G8R8A8_UNorm}）。"
        + "按每像素 4 字节读会得到错色（该格式的每像素字节数不是 4，或通道顺序不是 BGRA），"
        + "因此本帧不出图。请把发送端格式改为 B8G8R8A8_UNorm。";
}
