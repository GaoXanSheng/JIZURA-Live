using System.IO;

namespace JizuraLive;

// 配置/登录态文件的原子写：先写同目录临时文件，再 File.Replace 原子替换。
// 直接 WriteAllText 覆盖在进程被杀/断电时留下截断的 JSON——
// 丢的可能是扫码登录态（要重扫）或全部桌面设置
public static class AtomicFile
{
    public static void Write(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        if (File.Exists(path))
            File.Replace(tmp, path, null);
        else
            File.Move(tmp, path);
    }
}
