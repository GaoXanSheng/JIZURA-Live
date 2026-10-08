using System.Windows;
using System.Windows.Threading;
using JizuraLive.App.Cloud;
using JizuraLive.Engine;
using JizuraLive.Logging;
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

    /// <summary>撤销重做栈变化（按钮可用态）。</summary>
    public event Action? HistoryChanged;

    // 双历史：edit（工程内容）与 look（样式观感）分栈，对位前端双历史
    private readonly Stack<(string Label, string Json)> _undoEdit = new();
    private readonly Stack<(string Label, string Json)> _redoEdit = new();
    private readonly Stack<(string Label, string Json)> _undoLook = new();
    private readonly Stack<(string Label, string Json)> _redoLook = new();

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
        JsonConvert.PopulateObject(json, Project);
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
        JsonConvert.PopulateObject(json, Project);
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
        Engine.SetProject(Project);
        Engine.Replan();
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
        undoStack.Push((label, Snapshot()));
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
        JsonConvert.PopulateObject(json, Project);
        AfterChange(true);
        NotifyAsync(HistoryChanged);
        return true;
    }

    public bool Redo()
    {
        if (_redoEdit.Count == 0) return false;
        var (label, json) = _redoEdit.Pop();
        _undoEdit.Push((label, Snapshot()));
        JsonConvert.PopulateObject(json, Project);
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
        var snap = Snapshot();
        var audio = Audio; // 投入后不可变，直接传引用
        Task.Run(() =>
        {
            if (Volatile.Read(ref _replanSeq) != seq) return; // 已被更新请求取代，别开算
            Plan plan;
            try
            {
                var proj = JsonConvert.DeserializeObject<Project>(snap) ?? Project;
                lock (EngineRenderGate.Lock) // 与渲染/Spout 共享引擎静态缓存，规划必须串行
                    plan = Planner.Plan(proj, audio);
            }
            catch (Exception ex)
            {
                Log.Error("后台重规划失败（保留旧 plan）：" + ex.Message, ex);
                return;
            }
            if (Volatile.Read(ref _replanSeq) != seq) return; // 算完发现过期：丢弃
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (Volatile.Read(ref _replanSeq) == seq) Engine.SetPlan(plan);
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
        _undoEdit.Push((label, Snapshot()));
        _redoEdit.Clear();
        Project = project;
        Engine.SetProject(project);
        Revision++;
        StateChanged?.Invoke();
        NotifyAsync(HistoryChanged);
        ProjectIO.Autosave(project);
    }

    /// <summary>初始化（对位 resetAllProject）：清空歌词/音频/历史，回默认工程。</summary>
    public void ResetAll()
    {
        _undoEdit.Clear();
        _redoEdit.Clear();
        _undoLook.Clear();
        _redoLook.Clear();
        var p = Project.NewDefault();
        p.Lyrics = "";
        Project = p;
        SetAudioInternal(null, null, null);
        Engine.SetProject(p);
        Revision++;
        StateChanged?.Invoke();
        AudioChanged?.Invoke();
        NotifyAsync(HistoryChanged);
        ProjectIO.Autosave(p);
    }

    // ---------------- 音频 ----------------

    public void SetAudio(AudioData audio, string path, string? fileName)
    {
        SetAudioInternal(audio, path, fileName);
        Revision++;
        ReplanAsync(); // plan.energy/pitch/beats 来自音频（后台重规划）
        StateChanged?.Invoke();
        AudioChanged?.Invoke();
    }

    private void SetAudioInternal(AudioData? audio, string? path, string? fileName)
    {
        Audio = audio;
        AudioPath = path;
        AudioFileName = fileName;
    }

    public void Dispose()
    {
        ProjectIO.FlushAutosave(Project);
    }
}
