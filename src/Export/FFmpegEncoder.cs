using System.Diagnostics;
using System.IO;
using System.Text;
using JizuraLive.App.I18n;
using JizuraLive.Logging;

namespace JizuraLive.Export;

/// <summary>
/// ffmpeg 进程管道编码器：渲染循环把 rawvideo 帧写 stdin，进度 = 已写帧数/总帧数
/// （stdin 阻塞即天然背压）。stderr 异步收集，失败时带尾部输出抛错。
/// ffmpeg 查找：程序目录 → native/ → PATH（开发机 WinGet 安装也可直接用）。
/// </summary>
public sealed class FFmpegEncoder : IDisposable
{
    private readonly Process _proc;
    private readonly Stream _stdin;
    private readonly StringBuilder _stderr = new();
    private readonly int _totalFrames;
    private int _framesWritten;
    private bool _finished;

    public int FramesWritten => _framesWritten;
    public double Progress => _totalFrames <= 0 ? 0 : Math.Min(1.0, _framesWritten / (double)_totalFrames);

    public FFmpegEncoder(IReadOnlyList<string> args, int totalFrames)
    {
        _totalFrames = totalFrames;
        var psi = new ProcessStartInfo(FindFFmpeg())
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        _proc = Process.Start(psi) ?? throw new InvalidOperationException(L.T("Export.Err.FFmpegStart"));
        _stdin = _proc.StandardInput.BaseStream;
        _proc.StandardOutput.ReadToEndAsync(); // 不吃会堵管道
        _proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (_stderr)
            {
                _stderr.AppendLine(e.Data);
                if (_stderr.Length > 64_000) _stderr.Remove(0, _stderr.Length - 32_000); // 只留尾部
            }
        };
        _proc.BeginErrorReadLine();
    }

    /// <summary>写一帧（宽×高×4 字节，BGRA 或 RGBA，顺序与 ffmpeg 参数一致）。</summary>
    public void WriteFrame(ReadOnlySpan<byte> pixels)
    {
        try
        {
            _stdin.Write(pixels);
        }
        catch (IOException)
        {
            // ffmpeg 提前退出（参数错/编码器起不来）：真实原因在 stderr 异步收集里，等它落地再抛，
            // 否则只报裸「管道已关闭」，排障全靠猜
            Thread.Sleep(300);
            throw new InvalidOperationException(L.F("Export.Err.FFmpegExitFmt", -1, StderrTail()));
        }
        _framesWritten++;
    }

    /// <summary>收尾：关 stdin → 等退出 → 非零码抛错（带 stderr 尾部）。</summary>
    public void Finish()
    {
        if (_finished) return;
        _finished = true;
        try
        {
            _stdin.Flush();
            _stdin.Close();
        }
        catch (IOException)
        {
            // ffmpeg 提前退出（下面统一报错）
        }

        if (!_proc.WaitForExit(120_000))
        {
            try { _proc.Kill(true); } catch { /* 已退出 */ }
            throw new InvalidOperationException(L.F("Export.Err.FFmpegTimeoutFmt", StderrTail()));
        }

        if (_proc.ExitCode != 0)
            throw new InvalidOperationException(L.F("Export.Err.FFmpegExitFmt", _proc.ExitCode, StderrTail()));
    }

    private string StderrTail()
    {
        lock (_stderr)
        {
            var s = _stderr.ToString();
            return s.Length > 1200 ? s[^1200..] : s;
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_proc.HasExited) _proc.Kill(true);
        }
        catch (Exception ex)
        {
            Log.Debug("ffmpeg Dispose: " + ex.Message);
        }
        _proc.Dispose();
    }

    /// <summary>程序目录 → native/ → PATH。</summary>
    public static string FindFFmpeg()
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var c in new[]
                 {
                     Path.Combine(baseDir, "ffmpeg.exe"),
                     Path.Combine(baseDir, "native", "ffmpeg.exe"),
                 })
            if (File.Exists(c)) return c;
        return "ffmpeg.exe"; // 交给 PATH 解析
    }

    public static bool Available()
    {
        try
        {
            var psi = new ProcessStartInfo(FindFFmpeg())
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-version");
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
