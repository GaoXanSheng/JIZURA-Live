using JizuraLive.App;
using JizuraLive.Engine;
using SkiaSharp;
using Xunit;

namespace JizuraLive.Engine.Tests;

public sealed class MemoryLeakTests
{
    [Fact]
    public void Renderer_SurfaceCanBeDisposedAfterClearCache()
    {
        EngineBoot.InstallPacks();
        var proj = Project.NewDefault();
        proj.Lyrics = "Memory leak test line 1\nMemory leak test line 2";
        var plan = Planner.Plan(proj, null);

        using var renderer = new Renderer();

        // 第一次渲染：使用 640x360 表面
        using (var surf1 = SKSurface.Create(new SKImageInfo(640, 360)))
        {
            renderer.Frame(surf1.Canvas, plan, 0, new RenderOpts { Stage = true });
        } // surf1 在此释放

        // 清理缓存（断开快照引用）
        renderer.ClearFrameCache();

        // 第二次渲染：使用 800x450 表面，不发生 AV，正常完成渲染
        using (var surf2 = SKSurface.Create(new SKImageInfo(800, 450)))
        {
            var exception = Record.Exception(() =>
                renderer.Frame(surf2.Canvas, plan, 0.5, new RenderOpts { Stage = true }));
            Assert.Null(exception);
        }
    }

    [Fact]
    public void JText_FaceFont_CachesWeightGrow()
    {
        EngineBoot.InstallPacks();
        // 验证 wg > 0 时返回缓存实例而非逐次新建
        var f1 = JText.FaceFont("display", 48, 0.35);
        var f2 = JText.FaceFont("display", 48, 0.35);
        Assert.Same(f1, f2);

        // 0.35 与 0.36 量化到同一档（20 档 = 0.05 步长），返回同一实例
        var f3 = JText.FaceFont("display", 48, 0.36);
        Assert.Same(f1, f3);
    }

    [Fact]
    public void EditorState_UndoStack_CappedAtMaxHistory()
    {
        using var preview = new PreviewEngine();
        using var state = new EditorState(preview);

        for (var i = 0; i < 120; i++)
        {
            state.Mutate(p => p.Title = $"Test Title {i}", $"Edit {i}", replan: false);
        }

        Assert.True(state.CanUndo);
        // 回退 50 次应耗尽撤销栈（上限 50）
        var count = 0;
        while (state.Undo())
        {
            count++;
            if (count > 60) break;
        }
        Assert.Equal(50, count);
        Assert.False(state.CanUndo);
    }
}
