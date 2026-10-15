using System.IO;
using JizuraLive.Pv;
using JizuraLive.Render.Effects;
using Newtonsoft.Json.Linq;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 工程文件（.jzpv）读写边界：往返、损坏文件不写盘、未来版本拒绝、文件不存在可区分、旧版双文件导入。
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
            scene.Camera.X = -40;  // 镜头基础值必须落盘：重启后静息取景不能变回 (0,0,1)
            scene.Camera.Scale = 1.2;

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
            Assert.Equal(-40, loaded.Scene.Camera.X, 3);
            Assert.Equal(1.2, loaded.Scene.Camera.Scale, 3);
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

    /// <summary>LoadOrImport 的完整布局：同一临时目录里的工程文件与两个旧版文件。</summary>
    private static (string Dir, string Project, string LegacyScene, string LegacyEffects) NewImportLayout()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jizura-import-" + Guid.NewGuid().ToString("N"));
        return (dir,
            Path.Combine(dir, "project.jzpv"),
            Path.Combine(dir, "pv_scene.json"),
            Path.Combine(dir, "pv_effects.json"));
    }

    [Fact]
    public void ExistingProjectWinsAndLegacyFilesAreIgnored()
    {
        var (dir, projectPath, legacyScenePath, legacyEffectsPath) = NewImportLayout();
        try
        {
            // 旧文件内容与工程刻意不同：结果必须来自工程，一个字段都不许被旧文件带偏
            var legacyScene = new PvScene { Name = "旧场景" };
            legacyScene.Layers.Add(new PvLayerConfig { Id = "legacy_bg" });
            var legacyEffects = new EffectGraph();
            legacyEffects.Add("glow");

            var project = new DirectorProject
            {
                Name = "新工程",
                Scene = new PvScene { Name = "工程场景" },
            };
            project.Scene.Layers.Add(new PvLayerConfig { Id = "bg" });
            project.Scene.Layers.Add(new PvLayerConfig { Id = "character" });
            project.Effects.Add("colorGrade");
            project.Effects.Add("bloom");
            project.Effects.Add("glitch");

            Directory.CreateDirectory(dir);
            File.WriteAllText(legacyScenePath, legacyScene.ToJson());
            File.WriteAllText(legacyEffectsPath, EffectGraphJson.Serialize(legacyEffects));
            Assert.Null(ProjectStore.Save(project, projectPath));

            var boot = ProjectStore.LoadOrImport(projectPath, legacyScenePath, legacyEffectsPath);

            Assert.False(boot.ImportedFromLegacy);
            Assert.Null(boot.Fault);
            Assert.Equal("工程场景", boot.Project.Scene.Name);
            Assert.Equal(2, boot.Project.Scene.Layers.Count);
            Assert.Equal(3, boot.Project.Effects.Nodes.Count);
        }
        finally
        {
            Cleanup(projectPath);
        }
    }

    [Fact]
    public void MissingProjectImportsLegacyAndLeavesLegacyBytesUntouched()
    {
        var (dir, projectPath, legacyScenePath, legacyEffectsPath) = NewImportLayout();
        try
        {
            var legacyScene = new PvScene { Name = "旧场景" };
            legacyScene.Layers.Add(new PvLayerConfig { Id = "legacy_bg" });
            legacyScene.Layers.Add(new PvLayerConfig { Id = "legacy_char" });
            var legacyEffects = new EffectGraph();
            legacyEffects.Add("glow");
            legacyEffects.Add("bloom");

            Directory.CreateDirectory(dir);
            File.WriteAllText(legacyScenePath, legacyScene.ToJson());
            File.WriteAllText(legacyEffectsPath, EffectGraphJson.Serialize(legacyEffects));
            var sceneBytes = File.ReadAllBytes(legacyScenePath);
            var effectsBytes = File.ReadAllBytes(legacyEffectsPath);

            var boot = ProjectStore.LoadOrImport(projectPath, legacyScenePath, legacyEffectsPath);

            Assert.True(boot.ImportedFromLegacy);
            Assert.Null(boot.Fault);
            Assert.Equal("旧场景", boot.Project.Scene.Name);
            Assert.Equal(2, boot.Project.Scene.Layers.Count);
            Assert.Equal(2, boot.Project.Effects.Nodes.Count);
            Assert.Equal("glow", boot.Project.Effects.Nodes[0].DefId);

            // 工程文件已落盘，内容就是导入结果：下次启动直接走 Load，导入只发生一次
            Assert.True(File.Exists(projectPath));
            var reloaded = ProjectStore.Load(projectPath);
            Assert.Null(reloaded.Fault);
            Assert.Equal(2, reloaded.Project!.Scene.Layers.Count);
            Assert.Equal(2, reloaded.Project.Effects.Nodes.Count);

            // 旧文件只读：导入不成就地改写，一个字节都不许动
            Assert.Equal(sceneBytes, File.ReadAllBytes(legacyScenePath));
            Assert.Equal(effectsBytes, File.ReadAllBytes(legacyEffectsPath));
        }
        finally
        {
            Cleanup(projectPath);
        }
    }

    [Fact]
    public void CorruptLegacySceneFallsBackToDefaultAndStillCreatesProject()
    {
        var (dir, projectPath, legacyScenePath, legacyEffectsPath) = NewImportLayout();
        try
        {
            const string corrupt = "{ \"Name\": \"坏场景\", oops";

            Directory.CreateDirectory(dir);
            File.WriteAllText(legacyScenePath, corrupt);
            File.WriteAllText(legacyEffectsPath, EffectGraphJson.Serialize(new EffectGraph()));

            var boot = ProjectStore.LoadOrImport(projectPath, legacyScenePath, legacyEffectsPath);

            Assert.True(boot.ImportedFromLegacy);
            Assert.NotNull(boot.Fault);
            Assert.Contains("旧场景文件", boot.Fault);

            var def = PvScene.CreateDefault();
            Assert.Equal(def.Name, boot.Project.Scene.Name);
            Assert.Equal(def.Layers.Count, boot.Project.Scene.Layers.Count);

            // 坏掉的旧文件也不许被"顺手修好"
            Assert.Equal(corrupt, File.ReadAllText(legacyScenePath));

            // 导入路径照常写工程文件，内容 = 内存里的默认场景
            Assert.True(File.Exists(projectPath));
            var reloaded = ProjectStore.Load(projectPath);
            Assert.Null(reloaded.Fault);
            Assert.Equal(def.Layers.Count, reloaded.Project!.Scene.Layers.Count);
        }
        finally
        {
            Cleanup(projectPath);
        }
    }

    [Fact]
    public void CorruptProjectIsReportedWithoutImportingOrWriting()
    {
        var (dir, projectPath, legacyScenePath, legacyEffectsPath) = NewImportLayout();
        try
        {
            const string corrupt = "{ \"SchemaVersion\": 1, \"Name\": \"坏工程\", oops";
            var legacyScene = new PvScene { Name = "旧场景" };
            legacyScene.Layers.Add(new PvLayerConfig { Id = "legacy_bg" });

            Directory.CreateDirectory(dir);
            File.WriteAllText(projectPath, corrupt);
            File.WriteAllText(legacyScenePath, legacyScene.ToJson());
            File.WriteAllText(legacyEffectsPath, EffectGraphJson.Serialize(new EffectGraph()));

            var boot = ProjectStore.LoadOrImport(projectPath, legacyScenePath, legacyEffectsPath);

            Assert.False(boot.ImportedFromLegacy);
            Assert.NotNull(boot.Fault);
            Assert.Contains("损坏", boot.Fault);

            // 内存回落默认工程，绝不采纳旧文件内容：工程文件存在就不走导入
            Assert.DoesNotContain(boot.Project.Scene.Layers, l => l.Id == "legacy_bg");

            // 损坏的工程文件一字未动，也没被"导入结果"覆盖
            Assert.Equal(corrupt, File.ReadAllText(projectPath));
        }
        finally
        {
            Cleanup(projectPath);
        }
    }

    private static void Cleanup(string projectPath)
    {
        var dir = Path.GetDirectoryName(projectPath);
        if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}
