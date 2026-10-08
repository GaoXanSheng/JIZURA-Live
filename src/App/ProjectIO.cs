using System.IO;
using System.IO.Compression;
using System.Text;
using JizuraLive.App.I18n;
using JizuraLive.Engine;
using JizuraLive.Logging;
using JizuraLive.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Timer = System.Timers.Timer;

namespace JizuraLive.App;

/// <summary>
/// 工程持久化：mergeProject（不信任外部输入的合并清洗，逐条对位旧前端 project.ts）
/// + autosave（%LOCALAPPDATA% 下的 autosave.json）+ 打开/保存。
/// .jizuralive 容器写入（pkg.ts 移植）由 SavePackage 提供。
/// </summary>
public static class ProjectIO
{
    public const string FileFilter =
        "字面 Live 工程 (*.jizuralive)|*.jizuralive|工程 JSON (*.json)|*.json|全部|*.*";

    private static string AutosavePath => Path.Combine(AppDirs.ConfigDir, "autosave.json");

    private static Timer? _autosaveTimer;

    /// <summary>
    /// 工程 JSON 统一 camelCase 输出（对齐 pkg.ts 的 JSON.stringify(S.project) 契约；
    /// MergeProject / 前端 mergeProject 都按 camelCase 键读取，Newtonsoft 的 JObject
    /// 键查找是大小写敏感的，写错口径 = 打开/恢复时字段全部静默丢失）。
    /// 只影响属性名；JObject 成员（overrides/cutTime 等数据键）与字典键（行号"0"等）原样保留。
    /// </summary>
    private static readonly JsonSerializer ProjectSerializer =
        new() { ContractResolver = new CamelCasePropertyNamesContractResolver() };

    /// <summary>工程 → 紧凑 camelCase JSON（= JSON.stringify）。</summary>
    public static string SerializeProject(Project p)
    {
        var sw = new StringWriter();
        ProjectSerializer.Serialize(sw, p);
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

        // 音频特效（预览+导出共用）：不信任外部输入，统一清洗
        d.AudioFx = AudioFx.SanitizeAudioFx(p["audioFx"] as JObject ?? new JObject());

        // keyBg 只认三态（plan 渲染读它做抠像底色）
        var keyBg = p.Value<string>("keyBg");
        d.KeyBg = keyBg is "green" or "black" ? keyBg : "off";

        // 导出勾选：TODO(M4/OutTab) normalizeCfg 移植后接入；bgStyle 镜像 keyBg
        d.ExportCfg = p["exportCfg"] is JObject ec ? DeepClone(ec) : null;
        if (d.ExportCfg != null) d.ExportCfg["bgStyle"] = d.KeyBg;
        // 老字段迁移：includeAudio=false → exportCfg.audio=false
        if (d.ExportCfg != null && d.ExportCfg["audio"] == null && p["includeAudio"]?.Type == JTokenType.Boolean)
            d.ExportCfg["audio"] = (bool)p["includeAudio"];

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

        // 用户字体：key 白名单 + 字段清洗
        d.UserFonts = new List<UserFont>();
        if (p["userFonts"] is JArray ufs)
            foreach (var uf in ufs.OfType<JObject>())
            {
                var key = uf.Value<string>("key") ?? "";
                if (!JFonts.SafeFontKey.IsMatch(key)) continue;
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
                // %LOCALAPPDATA%\JizuraLiveonts 读回（上传时已落盘），缺的走系统字体栈并在日志点名
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
        if (fx["motion"]?.Type == JTokenType.Float) f.Motion = (double)fx["motion"];
        if (fx["glitch"]?.Type == JTokenType.Float) f.Glitch = (double)fx["glitch"];
        if (fx["chroma"]?.Type == JTokenType.Float) f.Chroma = (double)fx["chroma"];
        if (fx["decor"]?.Type == JTokenType.Float) f.Decor = (double)fx["decor"];
        if (fx["density"]?.Type == JTokenType.Float) f.Density = (double)fx["density"];
        if (fx["texture"]?.Type == JTokenType.Float) f.Texture = (double)fx["texture"];
        if (fx["flash"]?.Type == JTokenType.Boolean) f.Flash = (bool)fx["flash"];
        if (fx["onTwos"]?.Type == JTokenType.Boolean) f.OnTwos = (bool)fx["onTwos"];
        if (fx["koma"]?.Type == JTokenType.Integer) f.Koma = (int)fx["koma"];
        if (fx["hud"] != null) f.Hud = (string)fx["hud"];
        if (fx["bgSwitch"]?.Type == JTokenType.Float) f.BgSwitch = (double)fx["bgSwitch"];
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

    private static JObject DeepClone(JObject o) => (JObject)o.DeepClone();

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
        _autosaveTimer ??= new Timer(700) { AutoReset = false };
        _autosaveTimer.Stop();
        _autosaveTimer.Elapsed += (_, _) => FlushAutosave(p);
        _autosaveTimer.Start();
    }

    public static void FlushAutosave(Project p)
    {
        _autosaveTimer?.Stop();
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

    // ---------------- 打开 / 保存 ----------------

    /// <summary>打开 .jizuralive（全量恢复）或 .json（样式配置导入）。</summary>
    public static Project Open(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (JizuraPackage.LooksLikePackage(bytes.AsSpan()))
        {
            var entries = JizuraPackage.Read(bytes);
            var pj = entries.FirstOrDefault(e => e.Name == "project.json")
                     ?? throw new InvalidDataException(L.T("ProjectIO.Err.MissingProjectJson"));
            var raw = JObject.Parse(Encoding.UTF8.GetString(pj.Data));
            var project = MergeProject(raw);

            // 包内音频裸存提取落盘
            var audioEntry = entries.FirstOrDefault(e => e.Name == "audio.bin");
            if (audioEntry != null && audioEntry.Data.Length > 0)
            {
                try
                {
                    Directory.CreateDirectory(AppDirs.AudioCacheDir);
                    var rawName = !string.IsNullOrEmpty(project.AudioName) ? project.AudioName : "audio.mp3";
                    var ext = Path.GetExtension(rawName);
                    if (string.IsNullOrEmpty(ext)) ext = ".mp3";
                    var safeBase = Path.GetFileNameWithoutExtension(rawName);
                    var cachedPath = Path.Combine(AppDirs.AudioCacheDir, $"{safeBase}_{Guid.NewGuid():N}{ext}");
                    File.WriteAllBytes(cachedPath, audioEntry.Data);
                    project.AudioPath = cachedPath;
                    if (string.IsNullOrEmpty(project.AudioName)) project.AudioName = rawName;
                    Log.Info($"[ProjectIO] 工程包音频已解包落盘：{cachedPath}");
                }
                catch (Exception ex)
                {
                    Log.Warn("[ProjectIO] 工程包音频提取失败：" + ex.Message);
                }
            }

            return project;
        }
        return MergeProject(JObject.Parse(Encoding.UTF8.GetString(bytes)));
    }

    /// <summary>
    /// 保存 .jizuralive：pkg.ts 的容器写入移植（M3-A 代理落地：JZLV 头 + project.json(brotli) + 音频/字体裸存）。
    /// 保存 .json：仅工程 JSON。
    /// </summary>
    public static void Save(Project p, string path, byte[]? audioFile, string? audioName)
    {
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            // 样式配置导入用 JSON（camelCase，与 .jizuralive 内的 project.json 同口径）
            var sw = new StringWriter();
            ProjectSerializer.Serialize(sw, p);
            AtomicFile.Write(path, sw.ToString());
            return;
        }
        SavePackage(p, path, audioFile, audioName);
    }

    /// <summary>
    /// .jizuralive 写入（pkg.ts 的 buildPackage/buildProjectPackage 移植）：
    ///   "JZLV" | u16 version=1 | u16 entryCount | u32 reserved(0)   —— 头 16 字节
    ///   每条目: u8 nameLen | name(UTF-8) | u8 codec(0=raw,1=brotli) | u64 rawSize | u64 packSize | u64 offset
    ///   之后各条目负载连续排列，全部小端；offset 是文件内绝对偏移（首条目 = 头长）。
    /// 条目：project.json（brotli；末尾附 app/appVersion 字段，对齐 pkg.ts 的 Object.assign）+ audio.bin（裸存，
    /// 音频本体已是压缩格式不白费 CPU）。用户字体（fonts/&lt;key&gt;）随 M2 字体管线落盘后接入。
    /// 写完先自校验：用读取端 JizuraPackage.Read 读回，断言 project.json/音频逐字节一致才落盘。
    /// </summary>
    public static void SavePackage(Project p, string path, byte[]? audioFile, string? audioName)
    {
        // project.json：全字段 + {app:'jizura-live', appVersion}（pkg.ts buildProjectPackage 同款，camelCase）
        var proj = p == null
            ? new JObject()
            : JObject.FromObject(p, ProjectSerializer);
        proj["app"] = "jizura-live";
        proj["appVersion"] = typeof(ProjectIO).Assembly.GetName().Version?.ToString(3) ?? "";
        var projBytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(proj)); // 紧凑输出 = JSON.stringify

        var entries = new List<(string Name, byte[] Data, bool Raw)> { ("project.json", projBytes, false) };
        if (audioFile is { Length: > 0 }) entries.Add(("audio.bin", audioFile, true)); // audioName 仅作元数据，条目名固定（pkg.ts AUDIO_ENTRY_PREFIX + 'bin'）

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
        {
            w.Write((byte)'J'); w.Write((byte)'Z'); w.Write((byte)'L'); w.Write((byte)'V');
            w.Write((ushort)JizuraPackage.LatestVersion);
            w.Write((ushort)entries.Count);
            w.Write(0u); // reserved（u32）
            w.Write(0u); // 头部补齐到 16 字节（pkg.ts 的 head = Uint8Array(16)，12..16 四个字节恒 0；
                         // 读取端 p=16 起解析条目，这里少 4 字节就会整体错位）

            // 条目头 = nameLen(1) + name + codec(1) + rawSize(8) + packSize(8) + offset(8) = nameLen + 26
            var headerLen = 16 + entries.Sum(e => 2 + Encoding.UTF8.GetByteCount(e.Name) + 24);
            var payloads = new List<byte[]>(entries.Count);
            long off = headerLen;
            foreach (var (name, data, raw) in entries)
            {
                byte[] packed;
                byte codec;
                if (raw)
                {
                    codec = 0;
                    packed = data;
                }
                else
                {
                    var src = new MemoryStream();
                    using (var br = new BrotliStream(src, CompressionLevel.Optimal, true))
                        br.Write(data, 0, data.Length);
                    packed = src.ToArray(); // BrotliStream Dispose 已把收尾字节刷进 src
                    codec = 1;
                }

                payloads.Add(packed);
                var nameBytes = Encoding.UTF8.GetBytes(name);
                w.Write((byte)nameBytes.Length);
                w.Write(nameBytes);
                w.Write(codec);
                w.Write((ulong)data.LongLength);
                w.Write((ulong)packed.LongLength);
                w.Write((ulong)off);
                off += packed.LongLength;
            }

            foreach (var pl in payloads) w.Write(pl);
        }

        var bytes = ms.ToArray();

        // 自校验：读取端逐字节断言（project.json 原文一致 + JSON 可解析 + 音频一致），不合格不落盘
        var back = JizuraPackage.Read(bytes);
        var backPj = back.FirstOrDefault(e => e.Name == "project.json")
                     ?? throw new InvalidDataException(L.T("ProjectIO.Err.Verify.MissingProjectJson"));
        if (!backPj.Data.AsSpan().SequenceEqual(projBytes))
            throw new InvalidDataException(L.T("ProjectIO.Err.Verify.ProjectJsonMismatch"));
        JObject.Parse(Encoding.UTF8.GetString(backPj.Data));
        if (audioFile is { Length: > 0 })
        {
            var backAudio = back.FirstOrDefault(e => e.Name == "audio.bin");
            if (backAudio == null || !backAudio.Data.AsSpan().SequenceEqual(audioFile))
                throw new InvalidDataException(L.T("ProjectIO.Err.Verify.AudioMismatch"));
        }

        // 原子落盘（AtomicFile 只有文本重载，字节版就地内联：tmp → replace/move）
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
        Log.Info($"[ProjectIO] 工程包已保存：{path}（{entries.Count} 条目，{bytes.Length} 字节，自校验通过）");
    }
}
