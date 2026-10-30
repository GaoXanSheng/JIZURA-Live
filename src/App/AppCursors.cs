using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using JizuraLive.Logging;

namespace JizuraLive.App;

public static class AppCursors
{
    private static readonly Dictionary<string, Cursor> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, MemoryStream> Streams = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    public static Cursor Arrow => Load("arrow.cur", Cursors.Arrow);
    public static Cursor Hand => Load("hand.cur", Cursors.Hand);
    public static Cursor IBeam => Load("ibeam.cur", Cursors.IBeam);
    public static Cursor Cross => Load("crosshair.cur", Cursors.Cross);
    public static Cursor SizeAll => Load("sizeall.cur", Cursors.SizeAll);
    public static Cursor SizeWE => Load("sizewe.cur", Cursors.SizeWE);
    public static Cursor SizeNS => Load("sizens.cur", Cursors.SizeNS);
    public static Cursor SizeNWSE => Load("sizenwse.cur", Cursors.SizeNWSE);
    public static Cursor SizeNESW => Load("sizenesw.cur", Cursors.SizeNESW);
    public static Cursor No => Load("no.cur", Cursors.No);
    public static Cursor Help => Load("help.cur", Cursors.Help);
    public static Cursor Pen => Load("nwpen.cur", Cursors.Pen);
    public static Cursor Wait => Load("wait.ani", Cursors.Wait);

    public static void Apply()
    {
        _ = Arrow;

        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => ((Window)s).Cursor = Arrow));
        EventManager.RegisterClassHandler(typeof(TextBoxBase), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => ((TextBoxBase)s).Cursor = IBeam));
        EventManager.RegisterClassHandler(typeof(PasswordBox), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => ((PasswordBox)s).Cursor = IBeam));
        EventManager.RegisterClassHandler(typeof(ContextMenu), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => ((ContextMenu)s).Cursor = Arrow));
        EventManager.RegisterClassHandler(typeof(ToolTip), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => ((ToolTip)s).Cursor = Arrow));
    }

    private static Cursor Load(string file, Cursor fallback)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(file, out var cached)) return cached;

            var path = Path.Combine(AppContext.BaseDirectory, "assets", "mono", file);
            try
            {
                if (!File.Exists(path))
                {
                    Log.Warn("[Cursor] 缺少光标文件：" + path);
                    Cache[file] = fallback;
                    return fallback;
                }

                if (file.EndsWith(".ani", StringComparison.OrdinalIgnoreCase))
                {
                    var animated = new Cursor(path);
                    Cache[file] = animated;
                    return animated;
                }

                var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
                var cursor = new Cursor(stream);
                Streams[file] = stream;
                Cache[file] = cursor;
                return cursor;
            }
            catch (Exception ex)
            {
                Log.Warn("[Cursor] 光标加载失败：" + file + "：" + ex.Message);
                Cache[file] = fallback;
                return fallback;
            }
        }
    }
}
