using System;
using System.IO;
using System.Threading;
using JizuraLive.App;

namespace JizuraLive.Engine.Tests;

public sealed class AutosaveGuard : IDisposable
{
    private readonly string _path = Path.Combine(AppDirs.ConfigDir, "autosave.json");
    private readonly byte[]? _original;

    public AutosaveGuard()
    {
        try
        {
            _original = File.Exists(_path) ? File.ReadAllBytes(_path) : null;
        }
        catch
        {
            _original = null;
        }
    }

    public void Dispose()
    {
        // EditorState 的存盘是 700ms 防抖：等挂起的定时器落地后再还原，否则会被它覆盖
        Thread.Sleep(900);
        try
        {
            if (_original != null) File.WriteAllBytes(_path, _original);
        }
        catch
        {
            // 还原失败（文件被占用等）不阻塞测试
        }
    }
}
