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

    /// <summary>撤销重做栈变化（按钮可用态）。</summary>
    public event Action? HistoryChanged;

    // 双历史：edit（工程内容）与 look（样式观感）分栈，对位前端双历史
    private readonly Stack<(string Label, string Json)> _undoEdit = new();
    private readonly Stack<(string Label, string Json)> _redoEdit = new();
    private readonly Stack<(string Label, string Json)> _undoLook = new();
    private readonly Stack<(string Label, string Json)> _redoLook = new();

    public bool CanUndo => _undoEdit.Count > 0;
    public bool CanRedo => _redoEdit.Count > 0;

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
    public void Mutate(Action<Project> edit, string label, bool replan = true, bool look = false)
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
        HistoryChanged?.Invoke();
    }

    public bool Undo()
    {
        if (_undoEdit.Count == 0) return false;
        var (label, json) = _undoEdit.Pop();
        _redoEdit.Push((label, Snapshot()));
        JsonConvert.PopulateObject(json, Project);
        AfterChange(true);
        HistoryChanged?.Invoke();
        return true;
    }

    public bool Redo()
    {
        if (_redoEdit.Count == 0) return false;
        var (label, json) = _redoEdit.Pop();
        _undoEdit.Push((label, Snapshot()));
        JsonConvert.PopulateObject(json, Project);
        AfterChange(true);
        HistoryChanged?.Invoke();
        return true;
    }

    private void AfterChange(bool replan)
    {
        Revision++;
        if (replan) Engine.Replan();
        StateChanged?.Invoke();
        ProjectIO.Autosave(Project);
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
        HistoryChanged?.Invoke();
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
        HistoryChanged?.Invoke();
        ProjectIO.Autosave(p);
    }

    // ---------------- 音频 ----------------

    public void SetAudio(AudioData audio, string path, string? fileName)
    {
        SetAudioInternal(audio, path, fileName);
        Revision++;
        Engine.Replan(); // plan.energy/pitch/beats 来自音频
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
