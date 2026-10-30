using System.Diagnostics;
using System.IO;
using JizuraLive.Engine;
using JizuraLive.Logging;
using Newtonsoft.Json;

namespace JizuraLive.App;

public sealed class Settings
{
    public string SpoutName { get; set; } = "JIZURA-live-PV";
    public bool Clean { get; set; }
    public bool SpoutEnabled { get; set; } = true;

    public bool SpoutTransparent { get; set; } = false;

    public bool VSync { get; set; } = true;

    public SubtitleConfig Subtitle { get; set; } = new() { Content = SubContent.Both };

    public bool PreviewSubtitles { get; set; }

    public bool VtsEnabled { get; set; } = true;

    public double PreviewVolume { get; set; } = 0.8;
    public bool PreviewMuted { get; set; }

    public string UiLang { get; set; } = "zh-Hans";

    public WindowLayout Layout { get; set; } = new();

    public static string ConfigDir => AppDirs.ConfigDir;
    public static string FilePath => Path.Combine(ConfigDir, "pv_config.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var text = File.ReadAllText(FilePath);
                var settings = JsonConvert.DeserializeObject<Settings>(text);
                if (settings != null)
                {
                    Log.Info($"已加载配置文件：{FilePath}");
                    return Normalize(settings);
                }
            }
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".corrupt.bak", overwrite: true);
            }
            catch (Exception backupEx)
            {
                Log.Warn($"配置文件损坏，备份失败：{backupEx.Message}");
            }

            Log.Error($"配置文件解析失败 ({FilePath})，将使用默认配置并重新生成：{ex.Message}");
        }

        var defaultSettings = Normalize(new Settings());
        defaultSettings.Save();
        Log.Info($"已初始化默认配置文件：{FilePath}");
        return defaultSettings;
    }

    private static Settings Normalize(Settings settings)
    {
        settings.SpoutName = string.IsNullOrWhiteSpace(settings.SpoutName)
            ? "JIZURA-live-PV"
            : settings.SpoutName.Trim();
        settings.UiLang = string.IsNullOrWhiteSpace(settings.UiLang) ? "zh-Hans" : settings.UiLang;
        settings.Subtitle ??= new SubtitleConfig { Content = SubContent.Both };
        settings.PreviewVolume = double.IsFinite(settings.PreviewVolume)
            ? Math.Clamp(settings.PreviewVolume, 0, 1)
            : 0.8;
        settings.Layout ??= new WindowLayout();
        settings.Layout.LeftWidth = Clamp(settings.Layout.LeftWidth, 240, 1200, 320);
        settings.Layout.InspectorWidth = Clamp(settings.Layout.InspectorWidth, 340, 1200, 380);
        settings.Layout.TimelineHeight = Clamp(settings.Layout.TimelineHeight, 80, 1600, 300);
        settings.Layout.TimelineHeaderWidth = Clamp(settings.Layout.TimelineHeaderWidth, 120, 800, 200);
        settings.Layout.LeftTab = Math.Clamp(settings.Layout.LeftTab, 0, 4);
        return settings;
    }

    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    public void Save()
    {        lock (SaveGate)
        {
            try
            {
                var t0 = Stopwatch.GetTimestamp();
                Directory.CreateDirectory(ConfigDir);
                AtomicFile.Write(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented));
                Log.Info($"配置已保存至：{FilePath}");
            }
            catch (Exception ex)
            {
                Log.Error($"保存配置文件失败 ({FilePath})：" + ex.Message);
            }
        }
    }

    private static readonly object SaveGate = new();
    private static readonly object DebounceGate = new();
    private static System.Threading.Timer? _saveDebounce;
    private static Settings? _savePending;

    public void SaveDebounced()
    {
        lock (DebounceGate)
        {
            _savePending = this;
            _saveDebounce ??= new System.Threading.Timer(_ => FlushPending(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            _saveDebounce.Change(400, System.Threading.Timeout.Infinite);
        }
    }

    public static void FlushPending()
    {
        Settings? s;
        lock (DebounceGate)
        {
            s = _savePending;
            _savePending = null;
        }
        s?.Save();
    }
}

public sealed class WindowLayout
{
    public double LeftWidth { get; set; } = 320;

    public double InspectorWidth { get; set; } = 380;

    public double TimelineHeight { get; set; } = 300;

    public bool LeftCollapsed { get; set; }

    public bool InspectorCollapsed { get; set; }

    public bool TimelineCollapsed { get; set; }

    public int LeftTab { get; set; }

    public double TimelineHeaderWidth { get; set; } = 200;
}
