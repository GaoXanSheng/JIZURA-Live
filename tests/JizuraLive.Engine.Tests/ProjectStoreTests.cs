using System.IO;
using JizuraLive.Pv;
using JizuraLive.Render.Effects;
using Newtonsoft.Json.Linq;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 工程文件（.jzpv）读写边界：往返、损坏文件不写盘、未来版本拒绝、文件不存在可区分。
/// 每个用例独立临时目录（Guid 隔离，finally 删除），不往测试进程工作目录留垃圾。
/// </summary>
public class ProjectStoreTests
{
    // Save 的失败路径（写入被拒）不做单测：Windows 上要靠占用文件 / 只读目录 / ACL 才能稳定触发，
    // 依赖杀软与目录权限、跨机器不稳；该路径本身只是 try/catch 把 ex.Message 交给调用方。

    [Fact]
    public void RoundTripPreservesSceneEffectsAudioAndLyrics()
    {
        var path = NewProjectPath();
        try
        {
            var scene = PvScene.CreateDefault();
            scene.Width = 1280;
            scene.Height = 720;

            var colorGrade = EffectCatalog.Get("colorGrade")!;
            var glow = EffectCatalog.Get("glow")!;
            var effects = new EffectGraph();
            effects.Add("colorGrade", n =>
            {
                n.Set(colorGrade.Params[0], 1.5f);           // saturation
                n.SetColor(colorGrade.Params[4], "#8f6ac0"); // tint
            });
            effects.Add("glow", n => n.Set(glow.Params[1], 2.25f)); // intensity

            var project = new DirectorProject
            {
                Name = "往返工程",
                Scene = scene,
                Effects = effects,
                AudioPath = "assets/audio/song.mp3",
                LrcText = "[00:01.00]第一行\n[00:03.00]第二行",
            };

            Assert.Null(ProjectStore.Save(project, path));

            // 效果部分必须是嵌套的 JSON 对象，而不是一层转义字符串
            var root = JObject.Parse(File.ReadAllText(path));
            Assert.IsType<JObject>(root["Effects"]);

            var result = ProjectStore.Load(path);
            Assert.Null(result.Fault);
            var loaded = Assert.IsType<DirectorProject>(result.Project);

            Assert.Equal("往返工程", loaded.Name);
            Assert.Equal(scene.Layers.Count, loaded.Scene.Layers.Count);
            Assert.Equal(1280, loaded.Scene.Width);
            Assert.Equal(720, loaded.Scene.Height);
            Assert.Equal(scene.Fps, loaded.Scene.Fps);
            Assert.Equal("assets/audio/song.mp3", loaded.AudioPath);
            Assert.Equal(project.LrcText, loaded.LrcText);

            Assert.Equal(2, loaded.Effects.Nodes.Count);
            Assert.Equal("colorGrade", loaded.Effects.Nodes[0].DefId);
            Assert.Equal(1.5f, loaded.Effects.Nodes[0].Value(colorGrade.Params[0]));
            Assert.Equal("#8f6ac0", loaded.Effects.Nodes[0].ColorValue(colorGrade.Params[4]));
            Assert.Equal("glow", loaded.Effects.Nodes[1].DefId);
            Assert.Equal(2.25f, loaded.Effects.Nodes[1].Value(glow.Params[1]));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void CorruptJsonIsRejectedAndDiskFileUntouched()
    {
        var path = NewProjectPath();
        try
        {
            const string corrupt = "{ \"SchemaVersion\": 1, \"Name\": \"坏文件\", oops";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, corrupt);

            var result = ProjectStore.Load(path);

            Assert.Null(result.Project);
            Assert.Contains("损坏", result.Fault);
            Assert.Equal(corrupt, File.ReadAllText(path));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void FutureSchemaVersionIsRejectedAndDiskFileUntouched()
    {
        var path = NewProjectPath();
        try
        {
            const string future = "{\n  \"SchemaVersion\": 999,\n  \"Name\": \"来自未来\"\n}";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, future);

            var result = ProjectStore.Load(path);

            Assert.Null(result.Project);
            Assert.Contains("版本", result.Fault);
            Assert.DoesNotContain("损坏", result.Fault); // 可区分：是"版本过新"，不是"文件损坏"
            Assert.Equal(future, File.ReadAllText(path));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void MissingFileReportsNotFoundAndCreatesNothing()
    {
        var path = NewProjectPath();
        try
        {
            var result = ProjectStore.Load(path);

            Assert.Null(result.Project);
            Assert.Contains("不存在", result.Fault);
            Assert.DoesNotContain("损坏", result.Fault); // 可区分：是"不存在"，不是"损坏"
            Assert.False(File.Exists(path));
            Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        }
        finally
        {
            Cleanup(path);
        }
    }

    private static string NewProjectPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jizura-project-" + Guid.NewGuid().ToString("N"));
        return Path.Combine(dir, "project.jzpv");
    }

    private static void Cleanup(string projectPath)
    {
        var dir = Path.GetDirectoryName(projectPath);
        if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}
