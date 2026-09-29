using System;
using System.IO;
using Newtonsoft.Json;

namespace JizuraLive;

public sealed class Settings
{
    public string SpoutName { get; set; } = "JIZURA-live";
    public bool Clean { get; set; }
    public bool SpoutEnabled { get; set; } = true;
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public int RenderFps { get; set; } = 60;

    public static string ConfigDir => Path.Combine(AppContext.BaseDirectory, "config");
    public static string FilePath => Path.Combine(ConfigDir, "settings.json");

    private static string LegacyFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JizuraLive", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string text = File.ReadAllText(FilePath);
                var settings = JsonConvert.DeserializeObject<Settings>(text);
                if (settings != null)
                {
                    Log.Info($"已加载配置文件：{FilePath}");
                    return settings;
                }
            }
            else if (File.Exists(LegacyFilePath))
            {
                string text = File.ReadAllText(LegacyFilePath);
                var settings = JsonConvert.DeserializeObject<Settings>(text);
                if (settings != null)
                {
                    Log.Info($"检测到旧版配置，自动迁移至：{FilePath}");
                    settings.Save();
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

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented));
            Log.Info($"配置已保存至：{FilePath}");
        }
        catch (Exception ex)
        {
            Log.Error($"保存配置文件失败 ({FilePath})：" + ex.Message);
        }
    }
}
