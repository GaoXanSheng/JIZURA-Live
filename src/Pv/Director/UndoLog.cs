namespace JizuraLive.Pv;

public sealed class UndoLog
{
    public const int MaxDepth = 50;

    private readonly List<string> _stack = new();
    private int _index = -1;

    public bool CanUndo => _index > 0;

    public bool CanRedo => _index >= 0 && _index < _stack.Count - 1;

    public int Count => _stack.Count;

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

    public string? Undo()
    {
        if (!CanUndo) return null;
        return _stack[--_index];
    }

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
