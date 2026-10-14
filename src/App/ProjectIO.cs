using System.IO;
using JizuraLive.Engine;
using JizuraLive.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Timer = System.Timers.Timer;

namespace JizuraLive.App;

/// <summary>
/// 工程持久化：mergeProject（不信任外部输入的合并清洗，逐条对位旧前端 project.ts）
/// + autosave（%LOCALAPPDATA% 下的 autosave.json，含启动恢复与退出兜底）。
/// </summary>
public static class ProjectIO
{
    private static string AutosavePath => Path.Combine(AppDirs.ConfigDir, "autosave.json");

    private static Timer? _autosaveTimer;
    private static Project? _pendingAutosaveProject;
    private static readonly object _autosaveLock = new();

    /// <summary>
    /// 工程 JSON 统一 camelCase 输出（对齐 pkg.ts 的 JSON.stringify(S.project) 契约；
    /// MergeProject / 前端 mergeProject 都按 camelCase 键读取，Newtonsoft 的 JObject
    /// 键查找是大小写敏感的，写错口径 = 打开/恢复时字段全部静默丢失）。
    /// 只影响属性名；JObject 成员（overrides/cutTime 等数据键）与字典键（行号"0"等）原样保留。
    /// </summary>
    private static readonly JsonSerializer ProjectSerializer =
        new() { ContractResolver = new CamelCasePropertyNamesContractResolver() };
    private static readonly object _serializerLock = new(); // JsonSerializer 非线程安全：autosave 定时器线程与保存并发用

    /// <summary>工程 → 紧凑 camelCase JSON（= JSON.stringify）。</summary>
    public static string SerializeProject(Project p)
    {
        var sw = new StringWriter();
        lock (_serializerLock) ProjectSerializer.Serialize(sw, p);
        return sw.ToString();
    }

    // ---------------- mergeProject（project.ts 逐条对位） ----------------

    public static Project MergeProject(JObject? raw)
    {
        var d = Project.NewDefault();
        var p = raw ?? new JObject();

        // 标量字段：raw 有就用 raw（Object.assign(d, p)）
        d.Title = p.Value<string>("title") ?? d.Title;
        d.Artist = p.Value<string>("artist") ?? d.Artist;
        d.Lyrics = p.Value<string>("lyrics") ?? d.Lyrics;
        d.Style = p.Value<string>("style") ?? d.Style;
        d.Mood = p.Value<string?>("mood") ?? d.Mood;
        d.ThemeId = p.Value<string?>("themeId"); // TODO(M5)：omakase 移植后按 J.THEMES 校验
        if (p["extra"]?.Type == JTokenType.Boolean) d.Extra = (bool)p["extra"];
        if (p["wa"]?.Type == JTokenType.Boolean) d.Wa = (bool)p["wa"];
        if (p["horror"]?.Type == JTokenType.Boolean) d.Horror = (bool)p["horror"];
        if (p["typo"]?.Type == JTokenType.Boolean) d.Typo = (bool)p["typo"];
        if (p["kinetic"]?.Type == JTokenType.Boolean) d.Kinetic = (bool)p["kinetic"];
        if (p["unify"]?.Type == JTokenType.Boolean) d.Unify = (bool)p["unify"];
        if (p["typeset"]?.Type == JTokenType.Boolean) d.Typeset = (bool)p["typeset"];
        if (p["centerFree"]?.Type == JTokenType.Boolean) d.CenterFree = (bool)p["centerFree"];
        d.Lang = p.Value<string>("lang") ?? d.Lang;
        if (p["seed"] != null) d.Seed = (uint)p["seed"];
        d.Aspect = p.Value<string>("aspect") ?? d.Aspect;
        if (p["res"]?.Type == JTokenType.Integer) d.Res = (int)p["res"];
        if (p["fps"]?.Type == JTokenType.Integer) d.Fps = (int)p["fps"];
        if (p["centerDir"] != null) d.CenterDir = (string)p["centerDir"];
        if (p["centerZone"] != null) d.CenterZone = (string)p["centerZone"];
        if (p["centerGap"]?.Type == JTokenType.Float || p["centerGap"]?.Type == JTokenType.Integer)
            d.CenterGap = (double)p["centerGap"];

        // fx / timing：Object.assign(默认, raw)
        if (p["fx"] is JObject fx) MergeFx(d.Fx, fx);
        if (p["timing"] is JObject tm) MergeTiming(d.Timing, tm);

        // enabled：默认全开，raw 只能关（老工程里新手法包的条目自动可用）
        if (p["enabled"] is JObject en)
            foreach (var g in Reg.GroupKeys)
                if (en[g] is JObject go)
                    foreach (var kv in go)
                        if (kv.Value?.Type == JTokenType.Boolean)
                            d.Enabled[g][kv.Key] = (bool)kv.Value;

        d.Overrides = new Dictionary<string, JObject>();
        if (p["overrides"] is JObject ov)
            foreach (var kv in ov)
                if (kv.Value is JObject o) d.Overrides[kv.Key] = o;

        // 音频文件名与路径
        d.AudioName = p.Value<string>("audioName") ?? p.Value<string>("audio_name");
        d.AudioPath = p.Value<string>("audioPath") ?? p.Value<string>("audio_path");

        // 音频特效（预览共用）：不信任外部输入，统一清洗
        d.AudioFx = AudioFx.SanitizeAudioFx(p["audioFx"] as JObject ?? new JObject());

        // keyBg 只认三态（plan 渲染读它做抠像底色）
        var keyBg = p.Value<string>("keyBg");
        d.KeyBg = keyBg is "green" or "black" ? keyBg : "off";

        // 选段范围：{from,to} 0 基切分索引（对位 project.ts 的 exportRange）；
        // 形状不合法（非数值 / from < 0）直接丢 → null = 整片。越界 clamp 在 Ranges.ExportRangeCuts 里做。
        if (p["exportRange"] is JObject er && Ranges.TryInt(er["from"], out var erFrom) && erFrom >= 0)
            d.ExportRange = new JObject
            {
                ["from"] = erFrom,
                ["to"] = Ranges.TryInt(er["to"], out var erTo) ? erTo : erFrom,
            };
        else
            d.ExportRange = null;

        // 锁：只允许 plain key 且只能开
        d.Locks = new LocksSettings();
        if (p["locks"] is JObject locks)
        {
            if (locks["tech"] is JObject tech)
                foreach (var kv in tech)
                    if (kv.Value?.Type == JTokenType.Boolean && (bool)kv.Value && PlainKey(kv.Key))
                        d.Locks.Tech[kv.Key] = true;
            if (locks["params"] is JObject pars)
                foreach (var kv in pars)
                    if (kv.Value?.Type == JTokenType.Boolean && (bool)kv.Value && PlainKey(kv.Key))
                        d.Locks.Params[kv.Key] = true;
        }

        // 颜色必须是颜色（#hex）或布尔
        d.Colors = new JObject { ["enabled"] = p["colors"]?["enabled"]?.Type == JTokenType.Boolean && (bool)p["colors"]["enabled"] };
        if (p["colors"] is JObject cols)
            foreach (var kv in cols)
            {
                if (kv.Key == "enabled") continue;
                if (kv.Value?.Type == JTokenType.Boolean) d.Colors[kv.Key] = (bool)kv.Value;
                else if (kv.Value?.Type == JTokenType.String && IsHexColor((string)kv.Value)) d.Colors[kv.Key] = (string)kv.Value;
            }

        // 用户字体：key 白名单 + 字段清洗 + 按 key 去重。去重是给历史版本擦屁股：撤销/重做曾用
        // JsonConvert.PopulateObject（集合追加语义），同一条字体被复制成百万条、autosave 撑到几百 MB，
        // 读回来必须收敛（否则快照序列化数秒、常驻内存 GB 级）。
        d.UserFonts = new List<UserFont>();
        var seenFontKeys = new HashSet<string>(StringComparer.Ordinal);
        if (p["userFonts"] is JArray ufs)
            foreach (var uf in ufs.OfType<JObject>())
            {
                var key = uf.Value<string>("key") ?? "";
                if (!JFonts.SafeFontKey.IsMatch(key) || !seenFontKeys.Add(key)) continue;
                var label = uf.Value<string>("label") ?? key;
                var family = uf.Value<string>("family") ?? (key.Length > 5 ? key[5..] : key);
                d.UserFonts.Add(new UserFont
                {
                    Key = key,
                    Label = label[..Math.Min(80, label.Length)],
                    Family = JFonts.SafeFamily(family),
                    Weight = Math.Clamp(uf.Value<int?>("weight") ?? 400, 100, 900)
                });
                // 字体本体不进工程包（体积大）：装载后由 EditorState.RestoreFonts 从用户字体库
                // %LOCALAPPDATA%\JizuraLive\fonts 读回（上传时已落盘），缺的走系统字体栈并在日志点名
            }

        // v0.10 行序迁移（只搬一次）
        MigrateOrder(d, p);

        // 字体三角色：role 是 plain key 且字体存在
        d.Fonts = new Dictionary<string, string>();
        if (p["fonts"] is JObject fonts)
            foreach (var kv in fonts)
                if (kv.Value?.Type == JTokenType.String && PlainKey(kv.Key) && JFonts.Has((string)kv.Value))
                    d.Fonts[kv.Key] = (string)kv.Value;

        return d;
    }

    private static void MergeFx(FxSettings f, JObject fx)
    {
        if (fx["motion"]?.Type is JTokenType.Float or JTokenType.Integer) f.Motion = (double)fx["motion"];
        if (fx["glitch"]?.Type is JTokenType.Float or JTokenType.Integer) f.Glitch = (double)fx["glitch"];
        if (fx["chroma"]?.Type is JTokenType.Float or JTokenType.Integer) f.Chroma = (double)fx["chroma"];
        if (fx["decor"]?.Type is JTokenType.Float or JTokenType.Integer) f.Decor = (double)fx["decor"];
        if (fx["density"]?.Type is JTokenType.Float or JTokenType.Integer) f.Density = (double)fx["density"];
        if (fx["texture"]?.Type is JTokenType.Float or JTokenType.Integer) f.Texture = (double)fx["texture"];
        if (fx["flash"]?.Type == JTokenType.Boolean) f.Flash = (bool)fx["flash"];
        if (fx["onTwos"]?.Type == JTokenType.Boolean) f.OnTwos = (bool)fx["onTwos"];
        if (fx["koma"]?.Type == JTokenType.Integer) f.Koma = (int)fx["koma"];
        if (fx["hud"] != null) f.Hud = (string)fx["hud"];
        if (fx["bgSwitch"]?.Type is JTokenType.Float or JTokenType.Integer) f.BgSwitch = (double)fx["bgSwitch"];
        if (fx["hideNo"]?.Type == JTokenType.Boolean) f.HideNo = (bool)fx["hideNo"];
        if (fx["hideTime"]?.Type == JTokenType.Boolean) f.HideTime = (bool)fx["hideTime"];
    }

    private static void MergeTiming(TimingSettings t, JObject tm)
    {
        if (tm["bpm"] != null && tm["bpm"].Type is JTokenType.Float or JTokenType.Integer) t.Bpm = (double)tm["bpm"];
        if (tm["offset"] != null && tm["offset"].Type is JTokenType.Float or JTokenType.Integer) t.Offset = (double)tm["offset"];
        if (tm["snap"]?.Type == JTokenType.Boolean) t.Snap = (bool)tm["snap"];
        if (tm["tail"] != null && tm["tail"].Type is JTokenType.Float or JTokenType.Integer) t.Tail = (double)tm["tail"];
        if (tm["lineScale"] != null && tm["lineScale"].Type is JTokenType.Float or JTokenType.Integer) t.LineScale = (double)tm["lineScale"];
        if (tm["lineTimes"] is JObject lt)
        {
            t.LineTimes = new Dictionary<string, double>();
            foreach (var kv in lt)
                if (kv.Value != null && (kv.Value.Type == JTokenType.Float || kv.Value.Type == JTokenType.Integer))
                    t.LineTimes[kv.Key] = (double)kv.Value;
        }
    }

    // v0.10 起混合 LRC 的无戳行不再排到最后，老工程里按行号存的逐行设置要搬到新行序上
    private static void MigrateOrder(Project d, JObject p)
    {
        var was = p.Value<int?>("timingOrder");
        d.TimingOrder = 2;
        if (was == null || was == 2) return;
        var map = Planner.ParseOrderV1(d.Lyrics);
        if (map == null) return;
        d.Timing.LineTimes = MoveKeys(d.Timing.LineTimes, map);
        var moved = new Dictionary<string, JObject>();
        foreach (var kv in d.Overrides)
        {
            if (int.TryParse(kv.Key, out var i) && i >= 0 && i < map.Length)
                moved[map[i].ToString()] = kv.Value;
            else moved[kv.Key] = kv.Value;
        }
        d.Overrides = moved;
        // exportRange 不迁移：上游按行存，本 fork 按切分存，语义不同无法按行号映射
    }

    private static Dictionary<string, double> MoveKeys(Dictionary<string, double> obj, int[] map)
    {
        var r = new Dictionary<string, double>();
        foreach (var kv in obj)
        {
            if (int.TryParse(kv.Key, out var i) && i >= 0 && i < map.Length)
                r[map[i].ToString()] = kv.Value;
            else r[kv.Key] = kv.Value;
        }
        return r;
    }

    private static bool PlainKey(string k)
    {
        foreach (var c in k)
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-'))
                return false;
        return k.Length > 0;
    }

    private static bool IsHexColor(string s) =>
        s.StartsWith('#') && s.Length is 4 or 7 or 9 &&
        s[1..].All(c => char.IsAsciiHexDigit(c));


    // ---------------- autosave（project.ts 的 localStorage → %LOCALAPPDATA%） ----------------

    public static Project? LoadAutosave()
    {
        try
        {
            if (!File.Exists(AutosavePath)) return null;
            var raw = JObject.Parse(File.ReadAllText(AutosavePath));
            Log.Info("已恢复自动保存的工程：" + AutosavePath);
            return MergeProject(raw);
        }
        catch (Exception ex)
        {
            Log.Warn("自动保存工程恢复失败（忽略）：" + ex.Message);
            return null;
        }
    }

    /// <summary>防抖 700ms（对位 project.ts autosave）。</summary>
    public static void Autosave(Project p)
    {
        lock (_autosaveLock)
        {
            _pendingAutosaveProject = p;
            if (_autosaveTimer == null)
            {
                _autosaveTimer = new Timer(700) { AutoReset = false };
                _autosaveTimer.Elapsed += (_, _) =>
                {
                    Project? proj;
                    lock (_autosaveLock)
                    {
                        proj = _pendingAutosaveProject;
                        _pendingAutosaveProject = null;
                    }
                    if (proj != null) FlushAutosave(proj);
                };
            }
            _autosaveTimer.Stop();
            _autosaveTimer.Start();
        }
    }

    public static void FlushAutosave(Project p)
    {
        lock (_autosaveLock)
        {
            _autosaveTimer?.Stop();
            _pendingAutosaveProject = null;
        }
        try
        {
            Directory.CreateDirectory(AppDirs.ConfigDir);
            AtomicFile.Write(AutosavePath, SerializeProject(p));
        }
        catch (Exception ex)
        {
            Log.Debug("autosave 失败：" + ex.Message);
        }
    }
}
