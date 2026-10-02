using System.IO;
using Newtonsoft.Json;

namespace JizuraLive;

public sealed class Settings
{
    public string SpoutName { get; set; } = "JIZURA-live";
    public bool Clean { get; set; }
    public bool SpoutEnabled { get; set; } = true;

    // Spout 输出透明背景：镜像页面跳过背景填充（只输出歌词+特效的透明图层），
    // 供 OBS 等下游合成；主界面预览不受影响。改动重启镜像后生效
    public bool SpoutTransparent { get; set; }

    // 独立 Spout 字幕通道：把歌词层单独输出到 <SpoutID>-LYRICS 通道（透明底，供分层合成）
    public bool SpoutLyricsEnabled { get; set; }
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public int RenderFps { get; set; } = 60;

    // CDP 调试端口：默认关闭；开启后 CEF 仅在本机回环地址监听该端口。
    // RemoteDebuggingPort 必须在 Cef.Initialize 前配置，改动重启后生效
    public bool CdpEnabled { get; set; }
    public int CdpPort { get; set; } = 9222;

    public static string ConfigDir => AppDirs.ConfigDir;
    public static string FilePath => Path.Combine(ConfigDir, "settings.json");

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
            AtomicFile.Write(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented));
            Log.Info($"配置已保存至：{FilePath}");
        }
        catch (Exception ex)
        {
            Log.Error($"保存配置文件失败 ({FilePath})：" + ex.Message);
        }
    }
}