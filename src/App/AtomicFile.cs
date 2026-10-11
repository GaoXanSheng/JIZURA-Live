using System.IO;

namespace JizuraLive.App;

// 原子写，防进程被杀/断电留下截断的 JSON，丢的可能是扫码登录态
public static class AtomicFile
{
    public static void Write(string path, string content)
    {
        UiStallProbe.Mark("file:" + Path.GetFileName(path));
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            // 不用 File.Replace：ReplaceFileW 要合并目标文件的元数据、过一遍文件系统过滤驱动，
            // 本机（火绒 HIPS）实测 4.5~9s/次，是「点音量按钮卡住」的元凶；MoveFileEx 覆盖同卷改名
            // 0~1ms，断电仍只丢本次新内容、旧文件完好
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            UiStallProbe.MarkIdle();
        }
    }
}
