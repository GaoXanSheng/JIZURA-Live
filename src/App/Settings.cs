using System.IO;
using Newtonsoft.Json;

namespace JizuraLive;

public sealed class Settings
{
    public string SpoutName { get; set; } = "JIZURA-live";
    public bool Clean { get; set; }
    public bool SpoutEnabled { get; set; } = true;

    // Spout 输出透明底：镜像跳过背景填充，只出歌词+特效透明图层给 OBS 合成；主界面预览不受影响，重启镜像生效
    public bool SpoutTransparent { get; set; }

    // 独立歌词通道：歌词层单独输出到 <SpoutID>-LYRICS，透明底供分层合成
    public bool SpoutLyricsEnabled { get; set; }

    // 字幕通道帧率：历史上硬钳 30（与主通道共享 CEF gpu-process 单线程光栅，
    // 满帧会拖垮主通道）；EBF 后瓶颈缓解，交还用户配置。生效值仍钳到
    // min(LyricsFps, RenderFps)——字幕层画得比主通道快没有意义，纯烧 GPU
    public int LyricsFps { get; set; } = 30;
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public int RenderFps { get; set; } = 60;

    // 镜像跑独立宿主进程（同 exe --mirror-host）：独立 GPU 进程，编辑器不被镜像光栅拖累。
    // false 或 JIZURA_MIRROR_HOST=0 回退同进程模式
    public bool SplitMirrorHost { get; set; } = true;

    // 镜像 External BeginFrame：帧节奏由宿主按输出帧率主动 SendExternalBeginFrame 驱动，
    // 绕开 Chromium 合成器自调度（BeginFrame 断供、稳态帧间隔被量化到 ~25ms 的来源）。
    // 重启镜像生效；JIZURA_MIRROR_EBF=1/0 环境变量可覆盖
    public bool MirrorExternalBeginFrame { get; set; }

    // 镜像 desynchronized 画布（低延迟交换链）。默认关走合成器路径（dsync=0）：
    // 合成器调度下 present 会被消费端反压阻塞 100-270ms。EBF 开启后 present 节奏
    // 归宿主管，可尝试开启换更低延迟；JIZURA_MIRROR_DSYNC=1/0 环境变量可覆盖
    public bool MirrorDsync { get; set; }

    // CDP 调试端口，默认关；开启后 CEF 只监听本机回环。
    // RemoteDebuggingPort 必须在 Cef.Initialize 前配置，重启生效
    public bool CdpEnabled { get; set; }
    public int CdpPort { get; set; } = 9222;

    // 镜像独立宿主进程的 CDP 调试端口：页面跑在另一个进程里，主进程的调试端口够不着它。
    // 默认开在 9223 与主进程错开；端口在宿主进程拉起时定型，改动需重启应用
    // （或关开一次「镜像独立进程渲染」重建宿主进程）。别与主进程调试端口相同——后启动的一方绑不上
    public bool MirrorCdpEnabled { get; set; } = true;
    public int MirrorCdpPort { get; set; } = 9223;

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