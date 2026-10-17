using Vortice.DXGI;

namespace JizuraLive.Spout;

public static class SpoutSourceFormat
{
    public static bool IsSupported(Format f) =>
        f is Format.B8G8R8A8_UNorm or Format.R8G8B8A8_UNorm;

    public static string DescribeUnsupported(Format f) =>
        $"Spout 源格式 {(int)f}（{f}）不在接收白名单内：本引擎消费 8 位 RGBA 家族源纹理"
        + $"（白名单：B8G8R8A8_UNorm/{(int)Format.B8G8R8A8_UNorm}、"
        + $"R8G8B8A8_UNorm/{(int)Format.R8G8B8A8_UNorm}）。"
        + "该格式的每像素字节数不是 4（10/16 位、浮点或 YUV 等），按每像素 4 字节解释会得到"
        + "静默错色的花屏，因此本帧不出图。请把发送端格式改为 8 位 RGBA（B8G8R8A8 或 R8G8B8A8）。";
}
