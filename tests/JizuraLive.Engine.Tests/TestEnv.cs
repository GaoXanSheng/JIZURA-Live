// 测试进程隔离：把 AppDirs 数据根重定向到临时目录，必须在任何测试碰 AppDirs 之前执行。
// 历史事故：测试直接读写 %LOCALAPPDATA%\JizuraLive\config\autosave.json——用户工程被覆盖成
// 「Test Title 19」，配合撤销回放的集合追加语义把 userFonts 灌成 6,765,201 条 / 664MB。
using System.IO;
using System.Runtime.CompilerServices;

namespace JizuraLive.Engine.Tests;

internal static class TestEnv
{
    [ModuleInitializer]
    internal static void Init()
    {
        Environment.SetEnvironmentVariable("JIZURA_DATA_DIR",
            Path.Combine(Path.GetTempPath(), "jizura-tests"));
    }
}
