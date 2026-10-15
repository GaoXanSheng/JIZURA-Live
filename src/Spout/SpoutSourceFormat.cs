using Vortice.DXGI;

namespace JizuraLive.Spout;

/// <summary>
/// Spout 接收边界的**源格式白名单**与不兼容格式的诊断文案。
///
/// 为什么必须有这道闸：源纹理的格式由**发送端**决定（native <c>SetSenderFormat</c>）。
/// 10/16 位、浮点、YUV 这类源的每像素字节数不是 4，按 <c>w*h*4</c> 解释就是**静默花屏**：
/// 合成器看不出来，导出的成片里也看不出是 bug。所以它们一律拒绝并给可读诊断（见 SpoutIn.Pump）。
///
/// **但 8 位 RGBA 家族的两种通道序都要收**：BGRA8 与 RGBA8 都是每像素 4 字节、D2D 都接受，
/// 差别只是 R/B 两个通道的位置。VTube Studio 等**真实发送端实际发的是 RGBA8**——
/// 早先白名单只放 BGRA8 曾把最常见的真实源整源拒掉（工作台里表现为「Spout 收不到画面」）。
/// 修法是**自有纹理跟随源格式**，让 D2D 按真实通道序解释，而不是拒绝或做通道交换。
///
/// 纯函数、不依赖 D3D 设备——单测可以直接钉（tests/JizuraLive.Engine.Tests/SpoutGpuInputTests.cs）。
/// </summary>
public static class SpoutSourceFormat
{
    /// <summary>
    /// 该源格式能否被安全消费：8 位 RGBA 家族的两种通道序（BGRA8 / RGBA8）。
    /// RGBA8 的实机取证（tools/SpoutProbe --live 的 L4 段，1872×1057 真实 VTS 源）：
    /// 自有纹理用 R8G8B8A8 时 native 直收返回 true、像素非黑，且 D2D 能直接把它包成位图。
    /// </summary>
    public static bool IsSupported(Format f) =>
        f is Format.B8G8R8A8_UNorm or Format.R8G8B8A8_UNorm;

    /// <summary>
    /// 不兼容格式的**可读**诊断：含格式名与数值，说明本引擎消费 8 位 RGBA 家族
    /// （BGRA8/RGBA8），并点明「每像素字节数不是 4」这一真正原因——
    /// 使用者（工作台状态条 / 日志）据此能判断该去改发送端格式，而不是去查渲染管线。
    /// </summary>
    public static string DescribeUnsupported(Format f) =>
        $"Spout 源格式 {(int)f}（{f}）不在接收白名单内：本引擎消费 8 位 RGBA 家族源纹理"
        + $"（白名单：B8G8R8A8_UNorm/{(int)Format.B8G8R8A8_UNorm}、"
        + $"R8G8B8A8_UNorm/{(int)Format.R8G8B8A8_UNorm}）。"
        + "该格式的每像素字节数不是 4（10/16 位、浮点或 YUV 等），按每像素 4 字节解释会得到"
        + "静默错色的花屏，因此本帧不出图。请把发送端格式改为 8 位 RGBA（B8G8R8A8 或 R8G8B8A8）。";
}
