using System.IO;
using JizuraLive.Pv;
using JizuraLive.Render.Effects;
using JizuraLive.Vts;
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
            scene.Camera.Rotation = 7.5;   // 旋转常量同属镜头基础值（帧状态 Rotation 槽的来源）

            var colorGrade = EffectCatalog.Get("colorGrade")!;
            var glow = EffectCatalog.Get("glow")!;
            var effects = new EffectGraph();
            effects.Add("colorGrade", n =>
            {
                n.Set(colorGrade.Params[0], 1.5f);           // saturation
                n.SetColor(colorGrade.Params[4], "#8f6ac0"); // tint
            });
            effects.Add("glow", n => n.Set(glow.Params[1], 2.25f)); // intensity
            var glowInstanceId = effects.Nodes[1].InstanceId;       // 曲线按实例 Id 寻址，不用 DefId

            // 三条曲线：镜头 scale 三 key（含 SmoothStep）、镜头 rotation 两 key、glow 实例的 intensity 单 key
            var cameraScale = new FloatCurve();
            Assert.True(cameraScale.TryAdd(0.0, 1.0));
            Assert.True(cameraScale.TryAdd(2.5, 1.4, CurveInterp.SmoothStep));
            Assert.True(cameraScale.TryAdd(6.0, 1.0, CurveInterp.SmoothStep));
            var cameraRotation = new FloatCurve();
            Assert.True(cameraRotation.TryAdd(0.0, 0.0));
            Assert.True(cameraRotation.TryAdd(4.0, 15.0));
            var glowIntensity = new FloatCurve();
            Assert.True(glowIntensity.TryAdd(1.5, 2.25, CurveInterp.Step));

            var project = new DirectorProject
            {
                Name = "往返工程",
                Scene = scene,
                Effects = effects,
                AudioPath = "assets/audio/song.mp3",
                LrcText = "[00:01.00]第一行\n[00:03.00]第二行",
                Curves =
                {
                    new CurveSave { Address = "camera.scale", Curve = cameraScale },
                    new CurveSave { Address = "camera.rotation", Curve = cameraRotation },
                    new CurveSave { Address = $"effect:{glowInstanceId}.intensity", Curve = glowIntensity },
                },
            };

            Assert.Null(ProjectStore.Save(project, path));

            // 效果部分必须是嵌套的 JSON 对象，而不是一层转义字符串
            var root = JObject.Parse(File.ReadAllText(path));
            Assert.IsType<JObject>(root["Effects"]);
            Assert.Equal(3, ((JArray)root["Curves"]!).Count);

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
            Assert.Equal(7.5, loaded.Scene.Camera.Rotation, 3);
            Assert.Equal("assets/audio/song.mp3", loaded.AudioPath);
            Assert.Equal(project.LrcText, loaded.LrcText);

            Assert.Equal(2, loaded.Effects.Nodes.Count);
            Assert.Equal("colorGrade", loaded.Effects.Nodes[0].DefId);
            Assert.Equal(1.5f, loaded.Effects.Nodes[0].Value(colorGrade.Params[0]));
            Assert.Equal("#8f6ac0", loaded.Effects.Nodes[0].ColorValue(colorGrade.Params[4]));
            Assert.Equal("glow", loaded.Effects.Nodes[1].DefId);
            Assert.Equal(2.25f, loaded.Effects.Nodes[1].Value(glow.Params[1]));

            // 曲线随工程往返：地址集合、key 数、值、插值模式逐一相等
            Assert.Equal(3, loaded.Curves.Count);
            var loadedCamera = Assert.Single(loaded.Curves, c => c.Address == "camera.scale").Curve;
            Assert.Equal(3, loadedCamera.Count);
            Assert.Equal(CurveInterp.SmoothStep, loadedCamera.Keys[1].Mode);
            Assert.Equal(cameraScale.Evaluate(0.0), loadedCamera.Evaluate(0.0));
            Assert.Equal(cameraScale.Evaluate(2.5), loadedCamera.Evaluate(2.5));
            Assert.Equal(cameraScale.Evaluate(6.0), loadedCamera.Evaluate(6.0));

            // 镜头 rotation 曲线同样往返（帧状态 Rotation 槽的曲线来源）
            var loadedRotation = Assert.Single(loaded.Curves, c => c.Address == "camera.rotation").Curve;
            Assert.Equal(2, loadedRotation.Count);
            Assert.Equal(cameraRotation.Evaluate(4.0), loadedRotation.Evaluate(4.0));

            var loadedGlow = Assert.Single(loaded.Curves, c => c.Address == $"effect:{glowInstanceId}.intensity").Curve;
            Assert.Equal(1, loadedGlow.Count);
            Assert.Equal(CurveInterp.Step, loadedGlow.Keys[0].Mode);
            Assert.Equal(glowIntensity.Evaluate(1.5), loadedGlow.Evaluate(1.5));
            Assert.Equal(glowIntensity.Evaluate(9.0), loadedGlow.Evaluate(9.0)); // 单 key 恒值也照旧
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// 旧工程 JSON 的镜头段没有 <c>Rotation</c> 字段（W2 之前存的工程）：读回来必须是 0，
    /// 既不报错也不把镜头歪掉——新字段只能加，不能成为旧文件的打开门槛。
    /// </summary>
    [Fact]
    public void LegacyCameraWithoutRotationLoadsAsZero()
    {
        var path = NewProjectPath();
        try
        {
            const string json = """
            {
              "SchemaVersion": 1,
              "Name": "旧工程",
              "Scene": {
                "Width": 1280,
                "Height": 720,
                "Camera": { "X": -40.0, "Y": 12.0, "Scale": 1.2 }
              }
            }
            """;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);

            var result = ProjectStore.Load(path);

            Assert.Null(result.Fault);
            var loaded = Assert.IsType<DirectorProject>(result.Project);
            Assert.Equal(-40, loaded.Scene.Camera.X, 3);
            Assert.Equal(12, loaded.Scene.Camera.Y, 3);
            Assert.Equal(1.2, loaded.Scene.Camera.Scale, 3);
            Assert.Equal(0, loaded.Scene.Camera.Rotation, 3);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void UnknownCurveAddressIsDroppedOnLoadAndProjectStillLoads()
    {
        var path = NewProjectPath();
        try
        {
            // Mode：0=Step 1=Linear 2=SmoothStep。第一条地址不合法，第二条合法——
            // 手写 JSON 模拟用户改坏 / 旧版本写坏一条地址的存档
            const string json = """
            {
              "SchemaVersion": 1,
              "Name": "含坏地址的工程",
              "Curves": [
                { "Address": "bogus", "Curve": { "Keys": [ { "Time": 0.0, "Value": 1.0, "Mode": 1 } ] } },
                { "Address": "layer:lyrics_pv.opacity", "Curve": {
                    "Keys": [ { "Time": 0.0, "Value": 0.5, "Mode": 0 }, { "Time": 1.0, "Value": 1.0, "Mode": 0 } ] } }
              ]
            }
            """;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);

            var result = ProjectStore.Load(path);

            // 坏一条地址不炸整份工程：工程照常载入，Fault 保持 null（这不是"打不开"）
            Assert.Null(result.Fault);
            var loaded = Assert.IsType<DirectorProject>(result.Project);

            // 坏地址条目被丢弃，合法的原样保留
            var kept = Assert.Single(loaded.Curves);
            Assert.Equal("layer:lyrics_pv.opacity", kept.Address);
            Assert.Equal(2, kept.Curve.Count);
            Assert.Equal(0.5, kept.Curve.Evaluate(0.0));
            Assert.Equal(1.0, kept.Curve.Evaluate(1.0));
            Assert.Equal(CurveInterp.Step, kept.Curve.Keys[1].Mode);

            // 治理只发生在内存：磁盘文件一字未动，下次用户主动保存才落盘
            Assert.Equal(json, File.ReadAllText(path));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void UnsortedOrDuplicateCurveKeysAreNormalizedOnLoad()
    {
        var path = NewProjectPath();
        try
        {
            // 手写乱序 + 1µs 内重复的 key：二分求值的前提（升序、时刻唯一）被破坏。
            // 载入必须恢复不变量，否则乱序的 key 会让曲线算出错值时有时无的镜头。
            const string json = """
            {
              "SchemaVersion": 1,
              "Name": "乱序曲线",
              "Curves": [
                { "Address": "camera.scale", "Curve": { "Keys": [
                    { "Time": 2.0, "Value": 1.4, "Mode": 1 },
                    { "Time": 1.0, "Value": 1.0, "Mode": 1 },
                    { "Time": 1.0000002, "Value": 9.9, "Mode": 1 },
                    { "Time": 3.0, "Value": 1.8, "Mode": 1 } ] } }
              ]
            }
            """;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);

            var result = ProjectStore.Load(path);

            Assert.Null(result.Fault);
            var curve = Assert.Single(Assert.IsType<DirectorProject>(result.Project).Curves).Curve;

            // 升序 + 1µs 重复被合并（保留先出现的）：4 → 3 个 key
            Assert.Equal(3, curve.Count);
            Assert.Equal(new[] { 1.0, 2.0, 3.0 }, curve.Keys.Select(k => k.Time).ToArray());
            Assert.Equal(1.0, curve.Evaluate(1.0));
            Assert.Equal(1.4, curve.Evaluate(2.0));
            Assert.Equal(1.8, curve.Evaluate(3.0));

            // 治理只发生在内存：磁盘文件一字未动
            Assert.Equal(json, File.ReadAllText(path));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void CurveHandlesRoundTripAndOldKeysGetDefaultHandles()
    {
        var path = NewProjectPath();
        try
        {
            // 旧档（没有句柄字段）与新档（显式句柄、Bezier/Spring 档位）混在一份文件里：
            // Mode：0=Step 1=Linear 2=SmoothStep 3=Bezier 4=Spring
            const string json = """
            {
              "SchemaVersion": 1,
              "Name": "句柄",
              "Curves": [
                { "Address": "camera.scale", "Curve": { "Keys": [
                    { "Time": 0.0, "Value": 1.0, "Mode": 2 },
                    { "Time": 1.0, "Value": 1.4, "Mode": 3, "OutX": 0.5, "OutY": 0.25, "InX": 0.1, "InY": -0.4 } ] } },
                { "Address": "layer:lyrics_pv.opacity", "Curve": { "Keys": [
                    { "Time": 0.0, "Value": 0.0, "Mode": 4, "OutX": 0.0, "OutY": 0.0, "InX": 0.0, "InY": 0.0 } ] } }
              ]
            }
            """;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);

            var result = ProjectStore.Load(path);
            Assert.Null(result.Fault);
            var loaded = Assert.IsType<DirectorProject>(result.Project);

            var camera = Assert.Single(loaded.Curves, c => c.Address == "camera.scale").Curve;
            var old = camera.Keys[0];
            // 旧档缺句柄字段 ⇒ 补默认 (0.333,0,0.333,0)：不能读成 0（那是"两端都没有手柄"的退化形状）
            Assert.Equal(0.333, old.OutX);
            Assert.Equal(0, old.OutY);
            Assert.Equal(0.333, old.InX);
            Assert.Equal(0, old.InY);
            Assert.Equal(CurveInterp.SmoothStep, old.Mode);

            // 新档的句柄原样读回（含显式的 0）：任何一位都不许被"补默认"覆盖
            var young = camera.Keys[1];
            Assert.Equal(CurveInterp.Bezier, young.Mode);
            Assert.Equal(0.5, young.OutX);
            Assert.Equal(0.25, young.OutY);
            Assert.Equal(0.1, young.InX);
            Assert.Equal(-0.4, young.InY);
            Assert.Equal(1.4, camera.Evaluate(1.0));       // 端点值照旧精确
            Assert.Equal(1.0, camera.Evaluate(0.0));

            var spring = Assert.Single(loaded.Curves, c => c.Address == "layer:lyrics_pv.opacity").Curve;
            Assert.Equal(CurveInterp.Spring, spring.Keys[0].Mode);
            Assert.Equal(0, spring.Keys[0].OutX);          // 显式 0 被保留：默认值只补"字段缺失"

            // 存盘再读：句柄与档位都要原样往返（写出去少一个字段就是丢用户的编辑）
            Assert.Null(ProjectStore.Save(loaded, path));
            var reloaded = Assert.IsType<DirectorProject>(ProjectStore.Load(path).Project);
            var back = Assert.Single(reloaded.Curves, c => c.Address == "camera.scale").Curve;
            Assert.Equal(camera.Keys[1], back.Keys[1]);
            Assert.Equal(camera.Keys[0], back.Keys[0]);
            Assert.Equal(camera.Evaluate(0.5), back.Evaluate(0.5));
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

    /// <summary>
    /// 布局的读入治理：非有限数值（NaN/Infinity）会让画布变换整片失效，按"这个字段没有"回落；
    /// 缺布局段按默认视图。布局不是内容，绝不因此拒绝打开工程，也不顺手回写磁盘。
    /// </summary>
    [Fact]
    public void NonFiniteLayoutFallsBackToDefaults()
    {
        var path = NewProjectPath();
        try
        {
            var project = new DirectorProject
            {
                Name = "坏布局",
                EditorLayout =
                {
                    CanvasZoom = double.NaN,
                    CanvasPanX = double.NegativeInfinity,
                    CanvasPanY = 12,
                    SelectedNodeIds = null!,
                    TimelineZoom = double.NaN,
                },
            };

            // 存盘不许因坏值失败（真实来源是坏写盘/手改 JSON；这里直接构造同一形态）
            Assert.Null(ProjectStore.Save(project, path));

            var result = ProjectStore.Load(path);

            Assert.Null(result.Fault);
            var loaded = Assert.IsType<DirectorProject>(result.Project);

            // 非有限视图参数 ⇒ 默认视图；null 列表 ⇒ 空
            Assert.Equal(1.0, loaded.EditorLayout.CanvasZoom);
            Assert.Equal(0.0, loaded.EditorLayout.CanvasPanX);
            Assert.Equal(12.0, loaded.EditorLayout.CanvasPanY);
            Assert.Equal(1.0, loaded.EditorLayout.TimelineZoom);
            Assert.Empty(loaded.EditorLayout.SelectedNodeIds);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// 旧工程里残留的信号图段：信号图已从本程序删除，那一段加载时被整段忽略（Json.NET 忽略未知字段），
    /// 既不报错也不影响其余字段——旧工程照常打得开，其余内容一字不少。
    /// </summary>
    [Fact]
    public void LegacySignalGraphSectionIsIgnoredOnLoad()
    {
        var path = NewProjectPath();
        try
        {
            var project = new DirectorProject { Name = "带信号图的旧工程" };
            project.Scene.Layers.Add(new PvLayerConfig { Id = "bg" });
            project.Effects.Add("bloom");
            var curve = new FloatCurve();
            curve.TryAdd(0.0, 1.2);
            project.Curves.Add(new CurveSave { Address = "camera.scale", Curve = curve });
            Assert.Null(ProjectStore.Save(project, path));

            // 把信号图段塞进存档（旧版本写下的字段形态）
            var root = JObject.Parse(File.ReadAllText(path));
            root["SignalGraph"] = JObject.Parse(
                "{\"Nodes\":[{\"Id\":\"n_a\",\"Kind\":2,\"Params\":{\"value\":1},\"Texts\":{}}],"
                + "\"Edges\":[],"
                + "\"Bindings\":[{\"Id\":\"b_a\",\"NodeId\":\"n_a\",\"Address\":\"camera.scale\"}]}");
            File.WriteAllText(path, root.ToString());

            var result = ProjectStore.Load(path);

            Assert.Null(result.Fault);
            var loaded = Assert.IsType<DirectorProject>(result.Project);
            Assert.Equal("带信号图的旧工程", loaded.Name);
            Assert.Single(loaded.Scene.Layers);
            Assert.Single(loaded.Effects.Nodes);
            Assert.Equal("camera.scale", Assert.Single(loaded.Curves).Address);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// 存盘写的是当前架构版本。VtsPose 是新节点种类，旧读方会把未知种类连边一起丢掉——
    /// 与其"看起来能跑"，不如让旧读方按版本过新拒绝打开（契约 §15.3 的拒绝而非截断）。
    /// </summary>
    [Fact]
    public void SavedProjectCarriesCurrentSchemaVersion()
    {
        var path = NewProjectPath();
        try
        {
            Assert.Null(ProjectStore.Save(new DirectorProject { Name = "版本" }, path));

            var root = JObject.Parse(File.ReadAllText(path));
            Assert.Equal(DirectorProject.CurrentSchemaVersion, (int)root["SchemaVersion"]!);
            Assert.Equal(DirectorProject.CurrentSchemaVersion, Assert.IsType<DirectorProject>(ProjectStore.Load(path).Project).SchemaVersion);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// VTS 演出 cue 轨随工程往返：每个字段（含角色映射与提前量）都要一字不差地存回来——
    /// 丢一条 role 映射或一个 Duration，播出时就少一段动作、或者表情一直挂在那儿不关。
    /// </summary>
    [Fact]
    public void VtsCueTrackRoundTripsWithRolesAndLead()
    {
        var path = NewProjectPath();
        try
        {
            var project = new DirectorProject { Name = "带演出轨" };
            project.VtsCues.LeadMs = 120;
            project.VtsCues.Roles[VtsRoles.Chorus] = "chorus.exp3.json";
            project.VtsCues.Roles[VtsRoles.Hit] = "spin";
            project.VtsCues.Cues.Add(new VtsCue
            {
                Time = 12.5, Kind = VtsCueKind.Expression, Target = "role:chorus", Value = 1, Duration = 8,
            });
            project.VtsCues.Cues.Add(new VtsCue
            {
                Time = 20.25, Kind = VtsCueKind.Move, X = 4, Y = -2, Rotation = 6, Value = 0.3, Duration = 0.5,
            });
            project.VtsCues.Cues.Add(new VtsCue
            {
                Time = 21, Kind = VtsCueKind.Pulse, Target = "FaceAngleZ", Value = 12, Duration = 0.4,
            });
            project.VtsCues.Cues.Add(new VtsCue
            {
                Time = 30, Kind = VtsCueKind.Hotkey, Target = "role:hit",
            });

            Assert.Null(ProjectStore.Save(project, path));

            var result = ProjectStore.Load(path);
            Assert.Null(result.Fault);
            var loaded = Assert.IsType<DirectorProject>(result.Project);

            Assert.Equal(120.0, loaded.VtsCues.LeadMs);
            Assert.Equal("chorus.exp3.json", loaded.VtsCues.Roles[VtsRoles.Chorus]);
            Assert.Equal("spin", loaded.VtsCues.Roles[VtsRoles.Hit]);
            Assert.Equal(4, loaded.VtsCues.Cues.Count);

            var expression = Assert.Single(loaded.VtsCues.Cues, c => c.Kind == VtsCueKind.Expression);
            Assert.Equal(12.5, expression.Time, 6);
            Assert.Equal("role:chorus", expression.Target);
            Assert.Equal(1.0, expression.Value);
            Assert.Equal(8.0, expression.Duration);

            var move = Assert.Single(loaded.VtsCues.Cues, c => c.Kind == VtsCueKind.Move);
            Assert.Equal(20.25, move.Time, 6);
            Assert.Equal(4.0, move.X);
            Assert.Equal(-2.0, move.Y);
            Assert.Equal(6.0, move.Rotation);
            Assert.Equal(0.3, move.Value);
            Assert.Equal(0.5, move.Duration);

            var pulse = Assert.Single(loaded.VtsCues.Cues, c => c.Kind == VtsCueKind.Pulse);
            Assert.Equal("FaceAngleZ", pulse.Target);
            Assert.Equal(12.0, pulse.Value);
            Assert.Equal(0.4, pulse.Duration);

            Assert.Equal("role:hit", Assert.Single(loaded.VtsCues.Cues, c => c.Kind == VtsCueKind.Hotkey).Target);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// 这个字段之前存的工程里没有 VtsCues：加载后必须是**空轨**而不是 null——
    /// 播出侧直接读 track.Cues，null 会在第一次演出时炸掉。
    /// </summary>
    [Fact]
    public void LegacyProjectWithoutVtsCuesLoadsEmptyTrack()
    {
        var path = NewProjectPath();
        try
        {
            const string json = """
            {
              "SchemaVersion": 1,
              "Name": "没有演出轨的旧工程",
              "Scene": { "Width": 1280, "Height": 720 }
            }
            """;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);

            var result = ProjectStore.Load(path);

            Assert.Null(result.Fault);
            var loaded = Assert.IsType<DirectorProject>(result.Project);
            Assert.NotNull(loaded.VtsCues);
            Assert.Empty(loaded.VtsCues.Cues);
            Assert.Empty(loaded.VtsCues.Roles);
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
    public void CorruptLegacySceneFallsBackToBlankSceneAndStillCreatesProject()
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

            // 回退是**空白场景**（零图层），不是那套预置模板图层——没有可用旧数据时
            // 不该替用户决定画面结构。画布等基础设施仍在（有合理默认）。
            Assert.Empty(boot.Project.Scene.Layers);
            Assert.True(boot.Project.Scene.Width > 0 && boot.Project.Scene.Height > 0);

            // 坏掉的旧文件也不许被"顺手修好"
            Assert.Equal(corrupt, File.ReadAllText(legacyScenePath));

            // 导入路径照常写工程文件，内容 = 内存里的空白场景
            Assert.True(File.Exists(projectPath));
            var reloaded = ProjectStore.Load(projectPath);
            Assert.Null(reloaded.Fault);
            Assert.Empty(reloaded.Project!.Scene.Layers);
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
