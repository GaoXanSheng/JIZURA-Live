using System.IO;
using System.Text;
using log4net;
using log4net.Appender;
using log4net.Config;
using log4net.Core;
using log4net.Layout;

namespace JizuraLive;

public static class Log
{
    private static readonly ILog L = LogManager.GetLogger("JizuraLive");

    public static string? LogDir { get; private set; }
    public static string? LogFile { get; private set; }


    public static void Init()
    {
        LogDir = AppDirs.LogsDir;
        Directory.CreateDirectory(LogDir);
        LogFile = Path.Combine(LogDir, "jizura.log");

        var fileLayout = new PatternLayout("%date [%2thread] %-5level %logger - %message%newline");
        fileLayout.ActivateOptions();
        var file = new RollingFileAppender
        {
            File = LogFile,
            AppendToFile = true,
            RollingStyle = RollingFileAppender.RollingMode.Composite,
            MaxSizeRollBackups = 10,
            MaximumFileSize = "10MB",
            StaticLogFileName = true,
            Layout = fileLayout,
            Encoding = Encoding.UTF8
        };
        file.ActivateOptions();

        var panelLayout = new PatternLayout("%date{HH:mm:ss} %-5level %message");
        panelLayout.ActivateOptions();
        var panel = new PanelAppender { Layout = panelLayout };
        panel.ActivateOptions();

        BasicConfigurator.Configure(file, panel);

        Info("================ JIZURA Live 启动 ================");
        Info("日志文件：" + LogFile);
    }

    public static void Debug(string message)
    {
        Console.WriteLine(message);
        L.Debug(message);
    }

    public static void Info(string message)
    {
        Console.WriteLine(message);
        L.Info(message);
    }

    public static void Warn(string message)
    {
        Console.WriteLine(message);
        L.Warn(message);
    }

    public static void Error(string message)
    {
        Console.WriteLine(message);
        L.Error(message);
    }

    public static void Error(string message, Exception ex)
    {
        Console.WriteLine(message);
        // 不带堆栈的异常日志等于没日志（排查单文件发布问题时吃过亏）
        L.Error(message + "\n" + ex);
    }

    public static void Fatal(string message)
    {
        Console.WriteLine(message);
        L.Fatal(message);
    }


    public static void Shutdown()
    {
        LogManager.Shutdown();
    }
}

public sealed class PanelAppender : AppenderSkeleton
{
    public static event Action<string>? Line;

    protected override void Append(LoggingEvent loggingEvent)
    {
        var line = RenderLoggingEvent(loggingEvent);
        Line?.Invoke(line);
    }
}