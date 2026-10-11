using System.Diagnostics;
using System.IO;
using JizuraLive.Engine;
using JizuraLive.Logging;
using Newtonsoft.Json;

namespace JizuraLive.App;

public sealed class Settings
{
    public string SpoutName { get; set; } = "JIZURA-live";
    public bool Clean { get; set; }
    public bool SpoutEnabled { get; set; } = true;

    // Spout 输出透明底：主通道跳过背景填充，只出歌词+特效透明图层给 OBS 合成；主界面预览不受影响，重启镜像生效
    public bool SpoutTransparent { get; set; } = false;

    // 独立字幕通道：KRC 字幕层单独输出到 <SpoutID>-LYRICS，透明底供分层合成
    public bool SpoutLyricsEnabled { get; set; } = true;

    // 字幕通道帧率：生效值钳到 min(LyricsFps, RenderFps)——字幕层画得比主通道快没有意义，纯烧 GPU
    public int LyricsFps { get; set; } = 10;
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public int RenderFps { get; set; } = 60;

    // 歌词注音：装载云端歌词（酷狗 KRC / 网易云 LRC）时是否写入音译与翻译小字
    // （对位旧前端 lyricStore 的 roma/trans 选项，默认都开）
    public bool WantRoma { get; set; } = true;
    public bool WantTrans { get; set; } = true;

    // 云字幕层（KRC 逐字）样式与开关：对位旧前端 localStorage 的 jizura.kgsub（应用级配置，
    // 不进工程文件、不进撤销历史、改即生效）。引擎模块默认 Content=Off（对位 TS），
    // 编辑器默认双语——装载 KRC 字幕数据即出画面（与 PreviewEngine 的默认一致）。
    public SubtitleConfig Subtitle { get; set; } = new() { Content = SubContent.Both };

    // 预览音量/消音（对位旧前端 localStorage 的 jizura.previewVolume：v/m，重启恢复；
    // 只影响预览出声，导出始终原电平）
    public double PreviewVolume { get; set; } = 0.8;
    public bool PreviewMuted { get; set; }

    // 字体按需拉取（老前端 fetch-fonts 的联网对应物）：目录/系统都没有的字体家族后台从
    // Google Fonts 仓库镜像拉进用户字体库；关掉 = 纯本地（exe 旁 fonts\ + 系统字体栈）
    public bool FetchFonts { get; set; } = true;

    // UI 语言（i18n 词库目录名，src/App/I18n/Locales/{UiLang}/）：首期仅 zh-Hans，
    // 后续加语言 = 加词库目录 + 设置页下拉清单里补一项
    public string UiLang { get; set; } = "zh-Hans";

    private static string ConfigDir => AppDirs.ConfigDir;
    private static string FilePath => Path.Combine(ConfigDir, "settings.json");

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
        lock (SaveGate)
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

    // ---------------- 防抖后台落盘（点击/拖动路径专用） ----------------
    //
    // 写盘不便宜：AtomicFile 走文件系统，杀软过滤驱动活跃时（本机火绒 HIPS 实测单次 4.5~9s）能到秒级。
    // 点「声音」、拖音量条这种高频轻操作不该在 UI 线程碰磁盘，也不该等后台写盘：
    // SaveDebounced 只记脏 + 重置 400ms 计时（只碰 DebounceGate，纳秒级），真正的写盘在线程池；
    // 退出由 App.OnExit 的 FlushPending 兜底（防抖窗口内退出也不丢）。

    private static readonly object SaveGate = new();     // 写盘串行（只在 Save 内部持有，可能慢）
    private static readonly object DebounceGate = new(); // 防抖槽 + 计时器（UI 线程只碰这把）
    private static System.Threading.Timer? _saveDebounce;
    private static Settings? _savePending;

    /// <summary>请求持久化（防抖 400ms）：立即返回，UI 线程零磁盘接触。关键路径/退出用 <see cref="Save"/>。</summary>
    public void SaveDebounced()
    {
        lock (DebounceGate)
        {
            _savePending = this;
            _saveDebounce ??= new System.Threading.Timer(_ => FlushPending(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            _saveDebounce.Change(400, System.Threading.Timeout.Infinite);
        }
    }

    /// <summary>把防抖窗口内攒下的设置写掉（线程池回调/退出兜底）。写盘与同步 Save 互斥（SaveGate）。</summary>
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