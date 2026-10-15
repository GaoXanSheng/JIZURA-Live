using JizuraLive.Pv;

namespace JizuraLive.Engine.Tests;

/// <summary>快照式撤销栈（<see cref="UndoLog"/>）的纯模型测试，不需要 GPU / 窗口。</summary>
public class UndoLogTests
{
    [Fact]
    public void UndoRedoRoundTrips()
    {
        var log = new UndoLog();
        log.Push("a");
        log.Push("b");
        log.Push("c");

        Assert.True(log.CanUndo);    // 光标在最新快照上，下面还有更旧的 a/b 可回退
        Assert.False(log.CanRedo);   // 光标已在最新快照上，没有更新的可重做

        Assert.Equal("b", log.Undo());
        Assert.Equal("a", log.Undo());
        Assert.Null(log.Undo());
        Assert.False(log.CanUndo);

        Assert.Equal("b", log.Redo());
        Assert.Equal("c", log.Redo());
        Assert.Null(log.Redo());
        Assert.False(log.CanRedo);
    }

    [Fact]
    public void PushTruncatesRedoBranch()
    {
        var log = new UndoLog();
        log.Push("a");
        log.Push("b");
        log.Push("c");

        Assert.Equal("b", log.Undo());
        Assert.True(log.CanRedo);

        log.Push("d");
        Assert.False(log.CanRedo);
        Assert.Null(log.Redo());

        Assert.Equal("b", log.Undo());
        Assert.Equal("a", log.Undo());
        Assert.Null(log.Undo());
    }

    [Fact]
    public void DepthCapsAtMaxDepthDroppingOldest()
    {
        var log = new UndoLog();
        for (int i = 1; i <= 60; i++) log.Push(i.ToString());

        Assert.Equal(UndoLog.MaxDepth, log.Count);

        var seen = new List<string>();
        while (log.CanUndo) seen.Add(log.Undo()!);

        Assert.Equal(49, seen.Count);
        Assert.Equal("59", seen[0]);
        Assert.Equal("11", seen[^1]);   // 1..10 已被丢最旧丢弃
        Assert.Null(log.Undo());
    }

    [Fact]
    public void EmptyStackUndoRedoReturnNull()
    {
        var log = new UndoLog();

        Assert.False(log.CanUndo);
        Assert.False(log.CanRedo);
        Assert.Null(log.Undo());
        Assert.Null(log.Redo());
    }

    [Fact]
    public void ClearResetsBothDirections()
    {
        var log = new UndoLog();
        log.Push("a");
        log.Push("b");
        log.Undo();

        log.Clear();

        Assert.Equal(0, log.Count);
        Assert.False(log.CanUndo);
        Assert.False(log.CanRedo);
        Assert.Null(log.Undo());
        Assert.Null(log.Redo());
    }
}
