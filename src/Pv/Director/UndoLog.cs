namespace JizuraLive.Pv;

/// <summary>
/// 快照式撤销/重做栈。快照内容由调用方决定（JSON 字符串），本类不解析、不比较、不去重，纯容器。
/// 光标 <see cref="_index"/> 指向当前快照；Undo/Redo 只移动光标并返回落点快照。
/// </summary>
public sealed class UndoLog
{
    /// <summary>保留的最大快照数，超出丢最旧。首次 Push 之后光标就在最新快照上。</summary>
    public const int MaxDepth = 50;

    private readonly List<string> _stack = new();
    private int _index = -1;

    public bool CanUndo => _index > 0;

    public bool CanRedo => _index >= 0 && _index < _stack.Count - 1;

    public int Count => _stack.Count;

    /// <summary>压入新快照：先截断光标之后的重做分支，再追加；超出上限丢最旧。</summary>
    public void Push(string snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (_index < _stack.Count - 1)
            _stack.RemoveRange(_index + 1, _stack.Count - 1 - _index);

        _stack.Add(snapshot);

        if (_stack.Count > MaxDepth)
            _stack.RemoveAt(0);

        _index = _stack.Count - 1;
    }

    /// <summary>回退到上一个快照并返回它；无可回退时返回 null（不抛）。</summary>
    public string? Undo()
    {
        if (!CanUndo) return null;
        return _stack[--_index];
    }

    /// <summary>前进到下一个快照并返回它；无可前进时返回 null（不抛）。</summary>
    public string? Redo()
    {
        if (!CanRedo) return null;
        return _stack[++_index];
    }

    public void Clear()
    {
        _stack.Clear();
        _index = -1;
    }
}
