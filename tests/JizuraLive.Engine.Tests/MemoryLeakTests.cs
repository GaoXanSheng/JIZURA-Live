using System.IO;
using JizuraLive.App;
using JizuraLive.Engine;
using Newtonsoft.Json;
using SkiaSharp;
using Xunit;

namespace JizuraLive.Engine.Tests;

[Collection("EngineRegistry")]
public sealed class MemoryLeakTests : IClassFixture<AutosaveGuard>
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
        state.ReplaceProject(Project.NewDefault(), "测试工程");

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

    /// <summary>
    /// 撤销/重做不得让集合字段膨胀：历史回放曾用 JsonConvert.PopulateObject（对已有集合是「追加」
    /// 语义），撤销一次 UserFonts 就翻一倍——实测把工程撑到 6,765,201 条 / 664MB autosave
    ///（快照序列化 6s、常驻内存 9GB+）。改 ObjectCreationHandling.Replace 后集合换新实例。
    /// </summary>
    [Fact]
    public void Undo重做_集合字段不膨胀()
    {
        using var preview = new PreviewEngine();
        using var state = new EditorState(preview);
        state.ReplaceProject(Project.NewDefault(), "测试工程");

        // 工程带一条用户字体（复现触发条件）
        state.Mutate(p => p.UserFonts = new List<UserFont>
        {
            new() { Key = "user_UF_kniuheiti_light", Label = "kniuheiti-light", Family = "UF_kniuheiti_light", Weight = 400 }
        }, "seed font", replan: false);
        Assert.Single(state.Project.UserFonts);

        for (var i = 0; i < 8; i++)
            state.Mutate(p => p.Title = "t" + i, "edit" + i, replan: false);

        // 8 次撤销 + 8 次重做 + 再来一轮：追加语义下会翻到 2^16 条量级
        for (var i = 0; i < 8; i++) Assert.True(state.Undo());
        for (var i = 0; i < 8; i++) Assert.True(state.Redo());
        for (var i = 0; i < 8; i++) Assert.True(state.Undo());

        Assert.Single(state.Project.UserFonts);
        Assert.Equal("user_UF_kniuheiti_light", state.Project.UserFonts[0].Key);
    }

    /// <summary>Json.NET 的集合填充语义（钉住上一条测试的依据：Auto = 追加，Replace = 换新）。</summary>
    [Fact]
    public void Json填充_集合语义_Auto追加_Replace换新()
    {
        var auto = new Bag { Items = { "a" } };
        JsonConvert.PopulateObject("""{"Items":["a"]}""", auto);
        Assert.Equal(2, auto.Items.Count); // 默认语义：往已有集合里追加（就是泄漏根源）

        var replace = new Bag { Items = { "a" } };
        using (var reader = new JsonTextReader(new StringReader("""{"Items":["a"]}""")))
            new JsonSerializer { ObjectCreationHandling = ObjectCreationHandling.Replace }.Populate(reader, replace);
        Assert.Single(replace.Items); // Replace：集合换新实例，与「整份快照装回去」的语义一致
    }

    private sealed class Bag
    {
        public List<string> Items { get; set; } = new();
    }
}
