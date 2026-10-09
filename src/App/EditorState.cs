using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using JizuraLive.App.Cloud;
using JizuraLive.Engine;
using JizuraLive.Logging;
using JizuraLive.Server;
using Newtonsoft.Json;

namespace JizuraLive.App;

/// <summary>
/// 编辑器单一事实源（对位 frontend/src/jizura/state.ts + stores/editor）：
/// 工程/规划/音频 + 修改事务（快照撤销重做）+ revision。
/// 面板改状态一律走 Mutate / ReplaceProject / SetAudio，不许绕过事件直改。
/// </summary>
public sealed class EditorState : IDisposable
{
    public PreviewEngine Engine { get; }
    public Project Project { get; private set; }
    public Plan? Plan => Engine.Plan;
    public AudioData? Audio { get; private set; }
    public string? AudioPath { get; private set; }
    public string? AudioFileName { get; private set; }

    /// <summary>当前音频是否为重启或打开工程时恢复的上次音频。</summary>
    public bool IsAudioRestored { get; private set; }

    /// <summary>后台是否正在恢复上次的音频。</summary>
    public bool IsRestoringAudio { get; private set; }

    private int _restoreAudioSeq;

    /// <summary>工程版本号：MCP/外部协同的乐观锁基础。</summary>
    public int Revision { get; private set; }

    /// <summary>结构性变化（已自动重规划+防抖存盘），面板据此刷新。</summary>
    public event Action? StateChanged;

    /// <summary>音频投入/卸载。</summary>
    public event Action? AudioChanged;

    /// <summary>云字幕（KRC 逐字）：云音乐装载 / 跟随匹配时写入；导出烤字幕与独立 Spout 字幕通道消费。</summary>
    public List<KrcLine>? Subtitles => Engine.Subtitles;

    /// <summary>字幕层配置（字号/颜色/双语/位置等，UI 可调）。
    /// 模块默认 Content=Off（对位 TS）；编辑器侧默认双语——有字幕数据即出画面。
    /// 与引擎单一实例同源（导出与 Spout 字幕通道读同一份）。</summary>
    public SubtitleConfig SubtitleCfg => Engine.SubtitleCfg;

    public void SetSubtitles(List<KrcLine>? lines)
    {
        Engine.SetSubtitles(lines);
        NotifyAsync(StateChanged); // OutTab 的 SubOn 等面板读数随字幕状态刷新
    }

    /// <summary>当前装载的云端歌词来源（原文 + 候选版本）。会话级，不落工程；歌词面板的版本下拉读它。</summary>
    public LyricSource? LyricSrc { get; private set; }

    /// <summary>歌词来源/候选版本变化（装载新歌、切换版本）。</summary>
    public event Action? LyricSourceChanged;

    public void SetLyricSource(LyricSource? src)
    {
        LyricSrc = src;
        NotifyAsync(LyricSourceChanged);
    }

    /// <summary>撤销重做栈变化（按钮可用态）。</summary>
    public event Action? HistoryChanged;

    // 历史回放（撤销/重做）用的填充器：JsonConvert.PopulateObject 的默认语义是「已有集合继续追加、
    // 字典/对象合并」，与「把快照整个装回去」的历史语义不符——撤销一次 UserFonts 就翻一倍
    //（实测 22 次撤销把工程撑到 6.7M 条 / 664MB autosave：快照序列化 6s、常驻内存 9GB+）。
    // ObjectCreationHandling.Replace = 集合/字典换新实例再装，和 ReplaceProject 的语义一致。
    private static readonly JsonSerializer PopulateSerializer = new()
    {
        ObjectCreationHandling = ObjectCreationHandling.Replace
    };

    /// <summary>把历史快照灌回现有 Project 实例（实例引用不变：引擎与各面板都持有它）。</summary>
    private static void PopulateFrom(string json, Project project)
    {
        using var reader = new JsonTextReader(new StringReader(json));
        PopulateSerializer.Populate(reader, project);
    }

    // 双历史：edit（工程内容）与 look（样式观感）分栈，对位前端双历史；设 50 步容量上限防止内存无限增长
    private const int MaxHistory = 50;
    private readonly Stack<(string Label, string Json)> _undoEdit = new();
    private readonly Stack<(string Label, string Json)> _redoEdit = new();
    private readonly Stack<(string Label, string Json)> _undoLook = new();
    private readonly Stack<(string Label, string Json)> _redoLook = new();

    private static void PushHistory(Stack<(string Label, string Json)> stack, string label, string json)
    {
        if (stack.Count >= MaxHistory)
        {
            var arr = stack.ToArray(); // arr[0] is top, arr[^1] is bottom
            stack.Clear();
            for (var i = Math.Min(arr.Length, MaxHistory - 1) - 1; i >= 0; i--)
                stack.Push(arr[i]);
        }
        stack.Push((label, json));
    }

    public bool CanUndo => _undoEdit.Count > 0;
    public bool CanRedo => _redoEdit.Count > 0;

    // ---------------- MCP 直调的最小补充（M6） ----------------
    // 外观历史（_undoLook/_redoLook）此前只有内部压栈，没有消费方；MCP 的 history(kind=look)
    // 与 get_state.history 需要读数与回退入口，故补三个成员。除此之外 EditorState 未改。

    /// <summary>外观历史能否回退（对位 look.ts histInfo().canBack）。</summary>
    public bool CanUndoLook => _undoLook.Count > 0;

    /// <summary>外观历史能否前进（对位 histInfo().canFwd）。</summary>
    public bool CanRedoLook => _redoLook.Count > 0;

    /// <summary>外观历史读数（i = 已回退步数，len = i + 可前进步数 + 当前态）。</summary>
    public (int I, int Len, bool CanBack, bool CanFwd) LookHistoryInfo() =>
        (_undoLook.Count, _undoLook.Count + _redoLook.Count + 1, CanUndoLook, CanRedoLook);

    /// <summary>外观历史回退一步（MCP history kind=look）。</summary>
    public bool UndoLook()
    {
        if (_undoLook.Count == 0) return false;
        var (label, json) = _undoLook.Pop();
        _redoLook.Push((label, Snapshot()));
        PopulateFrom(json, Project);
        AfterChange(true);
        NotifyAsync(HistoryChanged);
        return true;
    }

    /// <summary>外观历史前进一步（MCP history kind=look）。</summary>
    public bool RedoLook()
    {
        if (_redoLook.Count == 0) return false;
        var (label, json) = _redoLook.Pop();
        _undoLook.Push((label, Snapshot()));
        PopulateFrom(json, Project);
        AfterChange(true);
        NotifyAsync(HistoryChanged);
        return true;
    }

    /// <summary>
    /// 同步重规划（MCP 写工具返回前必须有与 Revision 对应的新 plan：get_state 的行/切分读数、
    /// edit_line 的锁定快照都直接读 plan）。挂起的后台规划结果由 seq 丢弃（last-wins）。
    /// </summary>
    public void ReplanNow()
    {
        Interlocked.Increment(ref _replanSeq);
        Engine.Replan();
    }

    public EditorState(PreviewEngine engine)
    {
        Engine = engine;
        Project = ProjectIO.LoadAutosave() ?? Project.NewDefault();
        RestoreFonts(Project);
        AudioFileName = Project.AudioName;
        AudioPath = Project.AudioPath;
        // 首次规划走后台：Plan + 首帧预热压在 UI 线程上要 ~2-3s（实测 UI 探针 2919ms 抖动），
        // 打开应用即卡死就出在这。空 plan 期间预览泵跳过渲染，旧 plan（默认工程那一份）继续显示。
        Engine.SetProject(Project, replan: false);
        ReplanAsync();
        RestoreAudioAsync(seekToZero: false);
    }

    /// <summary>
    /// 工程装载时恢复用户字体：上传字体的本体从用户字体库（%LOCALAPPDATA%\JizuraLive\fonts）读回登记，
    /// 本机字体名重新挂上目录；拿不到的在日志里点名（渲染侧回退到系统字体栈，不崩也不空）。
    /// 对位旧前端的 restoreUserFonts：刷新页面后工程里的上传字体还在。
    /// </summary>
    private static void RestoreFonts(Project project)
    {
        try
        {
            var missing = JFonts.RestoreUserFonts(
                project.UserFonts.Select(u => new JFonts.UserFontRef(u.Key, u.Label, u.Family, u.Weight)));
            if (missing.Count > 0)
                Log.Warn("[字体] 工程用到的用户字体缺本体（改用回退字体）：" + string.Join("、", missing));
        }
        catch (Exception ex)
        {
            Log.Warn("[字体] 用户字体恢复失败：" + ex.Message);
        }
    }

    // ---------------- 修改事务 ----------------

    /// <summary>当前工程快照（JSON）。</summary>
    public string Snapshot() => JsonConvert.SerializeObject(Project);

    /// <summary>
    /// 修改工程的一站式事务：压栈 → edit → revision++ → 重规划 → 通知 → 防抖存盘。
    /// look=true 走观感历史栈（风格/字体/配色类，时间轴语义与内容修改分开）。
    /// </summary>
    // 同线程重入闸：StateChanged 处理器里再调 Mutate = UI 回环（栈溢出的根源形态——
    // 溢出在最深的引擎叶子爆出来，表象像 Planner 分段递归，实为处理器回环）。
    // 处理器侧应有幂等闸（见 LineList），这里是最后一道兜底：拒绝嵌套并记日志，不崩
    private static bool _mutating;

    public void Mutate(Action<Project> edit, string label, bool replan = true, bool look = false)
    {
        if (_mutating)
        {
            Log.Error($"Mutate 重入被拒绝：{label}（StateChanged 处理器内再次 Mutate 属 UI 回环）");
            return;
        }
        _mutating = true;
        try
        {
            MutateCore(edit, label, replan, look);
        }
        finally
        {
            _mutating = false;
        }
    }

    private void MutateCore(Action<Project> edit, string label, bool replan, bool look)
    {
        var (undoStack, redoStack) = look ? (_undoLook, _redoLook) : (_undoEdit, _redoEdit);
        PushHistory(undoStack, label, Snapshot());
        redoStack.Clear();
        try
        {
            edit(Project);
        }
        catch (Exception ex)
        {
            Log.Error("Mutate 失败（已回滚通知）：" + label, ex);
        }
        AfterChange(replan);
        NotifyAsync(HistoryChanged);
    }

    public bool Undo()
    {
        if (_undoEdit.Count == 0) return false;
        var (label, json) = _undoEdit.Pop();
        _redoEdit.Push((label, Snapshot()));
        PopulateFrom(json, Project);
        AfterChange(true);
        NotifyAsync(HistoryChanged);
        return true;
    }

    public bool Redo()
    {
        if (_redoEdit.Count == 0) return false;
        var (label, json) = _redoEdit.Pop();
        _undoEdit.Push((label, Snapshot()));
        PopulateFrom(json, Project);
        AfterChange(true);
        NotifyAsync(HistoryChanged);
        return true;
    }

    private void AfterChange(bool replan)
    {
        Revision++;
        if (replan) ReplanAsync(); // 后台重规划：大工程一次 ~700ms，绝不在 UI 线程同步跑（冻结元凶）
        // 异步通知：Measure 布局趟里触发的 Mutate（容器生成→SelectionChanged→Mutate）若同步
        // Invoke 面板处理器，面板会在 Measure 期间改视觉树 → VisualCollection 索引越界
        //（"index must be less than Count"）。排队到布局趟结束后再刷面板
        NotifyAsync(StateChanged);
        ProjectIO.Autosave(Project);
    }

    // ---------------- 后台重规划（编辑动作立即返回） ----------------

    private int _replanSeq;

    /// <summary>
    /// 工程取 JSON 快照交后台线程规划（避免与 UI 线程后续编辑竞争共享状态），
    /// 完成后回 UI 线程换 plan。last-wins：更新的请求会使过期结果在计算前后两处被丢弃，
    /// 拖拽类高频调用不会在渲染闸上堆积过期重算。
    /// </summary>
    public void ReplanAsync()
    {
        var seq = Interlocked.Increment(ref _replanSeq);
        var tSnap = Stopwatch.GetTimestamp();
        var snap = Snapshot();
        var snapMs = (Stopwatch.GetTimestamp() - tSnap) * 1000.0 / Stopwatch.Frequency;
        var audio = Audio; // 投入后不可变，直接传引用
        Engine.ReplanPending = true; // 预览泵据此在重规划期间抑制 CPU 兜底（见 PreviewEngine.ReplanPending）
        Task.Run(() =>
        {
            if (Volatile.Read(ref _replanSeq) != seq) return; // 已被更新请求取代，别开算（标志由新请求管）
            Plan plan;
            var tPlan = Stopwatch.GetTimestamp();
            try
            {
                var proj = JsonConvert.DeserializeObject<Project>(snap) ?? Project;
                lock (EngineRenderGate.Lock) // 与渲染/Spout 共享引擎静态缓存，规划必须串行
                {
                    plan = Planner.Plan(proj, audio);
                    // 预热新 plan 首帧（低分辨率一帧，见 PreviewEngine.WarmFirstFrame）：把冷缓存的
                    // 0.7–1.8s 一次性开销从「换 plan 后的第一帧」挪到这里（后台、plan 还没生效）。
                    Engine.WarmFirstFrame(plan);
                }
            }
            catch (Exception ex)
            {
                Log.Error("后台重规划失败（保留旧 plan）：" + ex.Message, ex);
                if (Volatile.Read(ref _replanSeq) == seq) Engine.ReplanPending = false;
                return;
            }
            var planMs = (Stopwatch.GetTimestamp() - tPlan) * 1000.0 / Stopwatch.Frequency;
            if (Volatile.Read(ref _replanSeq) != seq) return; // 算完发现过期：丢弃
            var tDispatch = Stopwatch.GetTimestamp();
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (Volatile.Read(ref _replanSeq) != seq) return;
                // 分段计时（性能专项）：秒级冻结时一眼看出是「计算慢」还是「UI 线程被占住（派发等待）」。
                // 注意「计算」里含等 EngineRenderGate 的时间——闸被预览兜底/泵的渲染占着时会显著变大。
                var waitMs = (Stopwatch.GetTimestamp() - tDispatch) * 1000.0 / Stopwatch.Frequency;
                var tApply = Stopwatch.GetTimestamp();
                try
                {
                    Engine.SetPlan(plan);
                }
                finally
                {
                    Engine.ReplanPending = false;
                }
                var applyMs = (Stopwatch.GetTimestamp() - tApply) * 1000.0 / Stopwatch.Frequency;
                if (snapMs + planMs + waitMs + applyMs > 250)
                    Log.Info($"[perf] 重规划 seq={seq}：快照 {snapMs:F0}ms / 计算 {planMs:F0}ms（含等引擎闸）/ 派发等待 {waitMs:F0}ms / 换plan {applyMs:F0}ms");
            });
        });
    }

    /// <summary>面板通知一律异步排队：与 Measure/布局趟解耦（无 Dispatcher 的环境同步直调）。</summary>
    private static void NotifyAsync(Action? handlers)
    {
        if (handlers == null) return;
        var d = Application.Current?.Dispatcher;
        if (d == null || d.HasShutdownStarted)
        {
            handlers();
            return;
        }
        d.BeginInvoke(handlers, DispatcherPriority.Background);
    }

    // ---------------- 工程整体替换（打开 / 初始化 / 撤销到装载点） ----------------

    public void ReplaceProject(Project project, string label)
    {
        Interlocked.Increment(ref _restoreAudioSeq);
        PushHistory(_undoEdit, label, Snapshot());
        _redoEdit.Clear();
        RestoreFonts(project); // 打开工程：把它的用户字体从字体库读回（换机器/换工程都能恢复）
        Project = project;
        Engine.SetProject(project, replan: false);
        ReplanAsync(); // 打开大工程同样走后台：同步 Plan + 预热会冻住界面 2-3s
        Revision++;
        AudioFileName = project.AudioName;
        AudioPath = project.AudioPath;
        SetAudioInternal(null, project.AudioPath, project.AudioName);
        StateChanged?.Invoke();
        AudioChanged?.Invoke();
        NotifyAsync(HistoryChanged);
        ProjectIO.Autosave(project);
        RestoreAudioAsync(seekToZero: true);
    }

    /// <summary>初始化（对位 resetAllProject）：清空歌词/音频/历史，回默认工程。</summary>
    public void ResetAll()
    {
        Interlocked.Increment(ref _restoreAudioSeq);
        IsRestoringAudio = false;
        IsAudioRestored = false;
        _undoEdit.Clear();
        _redoEdit.Clear();
        _undoLook.Clear();
        _redoLook.Clear();
        var p = Project.NewDefault();
        p.Lyrics = "";
        Project = p;
        SetAudioInternal(null, null, null);
        Engine.SetProject(p, replan: false);
        ReplanAsync(); // 初始化回默认工程：同样别在 UI 线程上同步规划
        Revision++;
        StateChanged?.Invoke();
        AudioChanged?.Invoke();
        NotifyAsync(HistoryChanged);
        ProjectIO.Autosave(p);
        try
        {
            if (Directory.Exists(AppDirs.AudioCacheDir))
            {
                foreach (var f in Directory.GetFiles(AppDirs.AudioCacheDir, "autosave_song.*"))
                    File.Delete(f);
            }
        }
        catch { }
    }

    // ---------------- 音频 ----------------

    public void SetAudio(AudioData audio, string path, string? fileName, bool restored = false)
    {
        Interlocked.Increment(ref _restoreAudioSeq);
        IsRestoringAudio = false;
        IsAudioRestored = restored;
        SetAudioInternal(audio, path, fileName);
        Project.AudioPath = path;
        Project.AudioName = fileName;
        Task.Run(() => TryCacheAutosaveAudio(path, fileName));
        Revision++;
        ReplanAsync(); // plan.energy/pitch/beats 来自音频（后台重规划）
        StateChanged?.Invoke();
        AudioChanged?.Invoke();
        ProjectIO.Autosave(Project);
    }

    private void SetAudioInternal(AudioData? audio, string? path, string? fileName)
    {
        Audio = audio;
        AudioPath = path;
        AudioFileName = fileName;
    }

    /// <summary>
    /// 异步恢复上次或工程绑定的音频（对位前端 boot.ts 的 loadSong 恢复）。
    /// 解码与分析在后台线程跑，完成后回 UI 线程 SetAudio，无缝接上真音频。
    /// </summary>
    public void RestoreAudioAsync(bool seekToZero = false)
    {
        var targetName = Project.AudioName;
        var targetPath = Project.AudioPath;
        if (string.IsNullOrEmpty(targetName) && string.IsNullOrEmpty(targetPath)) return;

        var resolvedPath = ResolveAudioPath(targetPath, targetName);
        if (string.IsNullOrEmpty(resolvedPath) || !File.Exists(resolvedPath))
        {
            if (!string.IsNullOrEmpty(targetName))
                Log.Warn($"[音频恢复] 未找到上次的音频文件：{targetName}（记录路径：{targetPath}）");
            return;
        }

        var seq = Interlocked.Increment(ref _restoreAudioSeq);
        IsRestoringAudio = true;
        AudioFileName = targetName ?? Path.GetFileName(resolvedPath);
        AudioPath = resolvedPath;
        StateChanged?.Invoke();

        Task.Run(() =>
        {
            try
            {
                var mono = AudioAnalyzer.DecodeMonoAnalyzeRate(resolvedPath, out var duration);
                var res = AudioAnalyzer.Analyze(mono, AudioAnalyzer.AnalyzeRate, duration);
                var audio = new AudioData
                {
                    Duration = duration,
                    Bpm = res.Bpm,
                    Beats = res.Beats,
                    Energy = res.Energy,
                    EnergyRate = res.EnergyRate,
                    Peaks = res.Peaks,
                    Pitch = res.Pitch,
                    PitchRate = res.PitchRate,
                };

                if (Volatile.Read(ref _restoreAudioSeq) != seq) return;

                void Apply()
                {
                    if (Volatile.Read(ref _restoreAudioSeq) != seq) return;
                    IsRestoringAudio = false;
                    SetAudio(audio, resolvedPath, targetName ?? Path.GetFileName(resolvedPath), restored: !seekToZero);
                    Log.Info($"[音频恢复] 已成功恢复音频：{AudioFileName}（{J.FmtTime(audio.Duration)}，{audio.Bpm:0.0} BPM）");
                }

                var d = Application.Current?.Dispatcher;
                if (d != null && !d.HasShutdownStarted)
                {
                    d.BeginInvoke(Apply);
                }
                else
                {
                    Apply();
                }
            }
            catch (Exception ex)
            {
                if (Volatile.Read(ref _restoreAudioSeq) == seq)
                {
                    IsRestoringAudio = false;
                    Log.Warn($"[音频恢复] 音频解码分析失败：{resolvedPath}，{ex.Message}");
                }
            }
        });
    }

    /// <summary>
    /// 把当前工程的音频文件备份一份到本地缓存（对位前端 J.saveSong(f)），
    /// 即使原路径被移动或删除，重启后仍可无缝恢复。
    /// </summary>
    private static void TryCacheAutosaveAudio(string path, string? fileName)
    {
        try
        {
            if (!File.Exists(path)) return;
            Directory.CreateDirectory(AppDirs.AudioCacheDir);
            var ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) ext = ".mp3";
            var autosavePath = Path.Combine(AppDirs.AudioCacheDir, "autosave_song" + ext);
            if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(autosavePath), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(path, autosavePath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Log.Debug("[音频缓存] 自动保存音频备份失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 解析音频文件的物理路径：原路径优先；若不存在则按缓存副本与云音乐目录查找。
    /// </summary>
    public static string? ResolveAudioPath(string? path, string? fileName)
    {
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
            return path;

        // 尝试云端音乐缓存目录 (%LOCALAPPDATA%\JizuraLive\cloud\fileName)
        if (!string.IsNullOrEmpty(fileName))
        {
            var cloudPath = Path.Combine(AppDirs.Root, "cloud", fileName);
            if (File.Exists(cloudPath)) return cloudPath;
        }

        // 尝试 AudioCacheDir 中的备份
        if (Directory.Exists(AppDirs.AudioCacheDir))
        {
            var autosaveFiles = Directory.GetFiles(AppDirs.AudioCacheDir, "autosave_song.*");
            if (autosaveFiles.Length > 0 && File.Exists(autosaveFiles[0]))
                return autosaveFiles[0];

            if (!string.IsNullOrEmpty(fileName))
            {
                var candidate = Path.Combine(AppDirs.AudioCacheDir, fileName);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    public void Dispose()
    {
        ProjectIO.FlushAutosave(Project);
    }
}
