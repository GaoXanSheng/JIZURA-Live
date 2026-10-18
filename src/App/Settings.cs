using System.Diagnostics;
using System.IO;
using JizuraLive.Engine;
using JizuraLive.Logging;
using Newtonsoft.Json;

namespace JizuraLive.App;

public sealed class Settings
{
    public int SchemaVersion { get; set; } = 1;

    public string SpoutName { get; set; } = "JIZURA-live-PV";
    public bool Clean { get; set; }
    public bool SpoutEnabled { get; set; } = true;

    public bool SpoutTransparent { get; set; } = false;

    public PvOptions Pv { get; set; } = new();

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public LiveLayerOptions? Live { get; set; }

    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public int RenderFps { get; set; } = 60;
    public bool VSync { get; set; } = true;

    public bool WantRoma { get; set; } = true;
    public bool WantTrans { get; set; } = true;

    public SubtitleConfig Subtitle { get; set; } = new() { Content = SubContent.Both };

    public double PreviewVolume { get; set; } = 0.8;
    public bool PreviewMuted { get; set; }

    public double CloudVolume { get; set; } = 0.85;
    public bool CloudMuted { get; set; }

    public bool FetchFonts { get; set; } = true;

    public string UiLang { get; set; } = "zh-Hans";

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
                    settings.MigratePv();
                    Log.Info($"已加载配置文件：{FilePath}");
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"配置文件解析失败 ({FilePath})，将使用默认配置并重新生成：{ex.Message}");
        }

        var defaultSettings = new Settings();
        defaultSettings.Save();
        Log.Info($"已初始化默认配置文件：{FilePath}");
        return defaultSettings;
    }

    private void MigratePv()
    {
        if (Live == null) return;
        if (Pv.Sources.Count == 0)
        {
            Pv.Sources.Add(Live);
            Pv.Enabled |= Live.Enabled;
        }

        Live = null;
        Log.Info($"[Settings] 已把旧版单路外部输入配置迁移为 PV 源（共 {Pv.Sources.Count} 路，"
                 + $"合成{(Pv.Enabled ? "开" : "关")}）");
    }

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
