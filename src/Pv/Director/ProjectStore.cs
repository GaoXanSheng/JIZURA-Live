using System.IO;
using JizuraLive.App;
using JizuraLive.Logging;
using JizuraLive.Render.Effects;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JizuraLive.Pv;

public sealed record ProjectLoadResult(DirectorProject? Project, string? Fault);

public sealed record ProjectBootstrap(DirectorProject Project, string? Fault, bool ImportedFromLegacy);

public static class ProjectStore
{
    private static readonly object WriteGate = new();

    public static string? Save(DirectorProject project, string path)
    {
        try
        {
            var json = JsonConvert.SerializeObject(project, Formatting.Indented);
            lock (WriteGate)
            {
                AtomicFile.Write(path, json);
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.Warn("[工程存储] 保存失败：" + ex.Message);
            return ex.Message;
        }
    }

    public static ProjectLoadResult Load(string path)
    {
        if (!File.Exists(path))
            return new ProjectLoadResult(null, $"工程文件不存在：{path}");

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            return new ProjectLoadResult(null, $"工程文件读取失败：{ex.Message}");
        }

        DirectorProject? project;
        try
        {
            project = JsonConvert.DeserializeObject<DirectorProject>(StripLegacyParticles(json));
        }
        catch (Exception ex)
        {
            return new ProjectLoadResult(null, $"工程文件 JSON 损坏：{ex.Message}");
        }

        if (project is null)
            return new ProjectLoadResult(null, "工程文件 JSON 损坏：解析结果为空");

        if (project.SchemaVersion > DirectorProject.CurrentSchemaVersion)
            return new ProjectLoadResult(null,
                $"工程版本过新（{project.SchemaVersion} > {DirectorProject.CurrentSchemaVersion}），本程序不识别，请升级后再打开");

        SanitizeCurves(project);

        SanitizeCameraTrack(project);

        SanitizeLayout(project);

        return new ProjectLoadResult(project, null);
    }

    private static void SanitizeLayout(DirectorProject project)
    {
        var layout = project.EditorLayout ??= new EditorLayout();

        layout.CanvasZoom = Finite(layout.CanvasZoom, 1);
        layout.CanvasPanX = Finite(layout.CanvasPanX, 0);
        layout.CanvasPanY = Finite(layout.CanvasPanY, 0);
        layout.TimelineZoom = Finite(layout.TimelineZoom, 1);

        layout.SelectedNodeIds ??= new List<string>();
        layout.CollapsedTrackIds ??= new List<string>();
    }

    private static double Finite(double value, double fallback) => double.IsFinite(value) ? value : fallback;

    private static void SanitizeCameraTrack(DirectorProject project)
    {
        var track = project.CameraTrack ??= new CameraTrack();
        track.Shots ??= new List<CameraShot>();
        track.Beats ??= new List<double>();

        track.Width = Finite(track.Width, 1280);
        track.Height = Finite(track.Height, 720);
        if (track.Width <= 0) track.Width = 1280;
        if (track.Height <= 0) track.Height = 720;
        track.BeatLen = Finite(track.BeatLen, 0.5);
        if (track.BeatLen <= 0.05) track.BeatLen = 0.5;

        track.Beats = track.Beats
            .Where(double.IsFinite).Where(b => b >= 0)
            .Distinct().OrderBy(b => b).ToList();

        int kept = 0;
        for (int i = 0; i < track.Shots.Count; i++)
        {
            var shot = track.Shots[i];
            if (shot is null || !double.IsFinite(shot.Start) || !double.IsFinite(shot.End) || shot.End <= shot.Start)
            {
                Log.Warn("[工程存储] 丢弃非法镜头：" + (shot is null ? "(null)" : $"{shot.Start:F3}–{shot.End:F3}"));
                continue;
            }

            shot.Params ??= new Dictionary<string, double>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(shot.Preset)) shot.Preset = "push";
            foreach (var key in shot.Params.Keys.Where(k => !double.IsFinite(shot.Params[k])).ToList())
                shot.Params.Remove(key);

            track.Shots[kept++] = shot;
        }

        if (kept < track.Shots.Count) track.Shots.RemoveRange(kept, track.Shots.Count - kept);
        track.Shots.Sort((a, b) => a.Start.CompareTo(b.Start));
    }

    private static void SanitizeCurves(DirectorProject project)
    {
        if (project.Curves.Count == 0) return;

        var kept = 0;
        for (var i = 0; i < project.Curves.Count; i++)
        {
            var save = project.Curves[i];
            if (save is null || save.Curve is null || !ParamAddress.TryParse(save.Address, out _))
            {
                Log.Warn("[工程存储] 丢弃不可解析的曲线条目：" + (save is null ? "(null)" : save.Address));
                continue;
            }

            project.Curves[kept++] = save;
        }

        if (kept < project.Curves.Count) project.Curves.RemoveRange(kept, project.Curves.Count - kept);

        foreach (var save in project.Curves) save.Curve.NormalizeKeys();
    }

    private static string StripLegacyParticles(string json)
    {
        if (!json.Contains("Particles", StringComparison.Ordinal)) return json;

        try
        {
            var root = JObject.Parse(json);
            var scene = root["Scene"];
            if (scene is null) return json;

            foreach (var layer in scene["Layers"] ?? Enumerable.Empty<JToken>())
                (layer as JObject)?.Remove("Particles");

            return root.ToString(Formatting.None);
        }
        catch (Exception ex)
        {
            Log.Warn("[工程存储] 剥离旧粒子配置失败，按原文解析：" + ex.Message);
            return json;
        }
    }

    public static ProjectBootstrap LoadOrImport(string projectPath, string legacyScenePath, string legacyEffectsPath)
    {
        if (File.Exists(projectPath))
        {
            var loaded = Load(projectPath);
            if (loaded.Project is not null)
                return new ProjectBootstrap(loaded.Project, null, false);

            Log.Warn("[工程存储] 打开工程失败，本次会话使用默认工程（磁盘文件保持原样）：" + loaded.Fault);
            return new ProjectBootstrap(new DirectorProject(), loaded.Fault, false);
        }

        var faults = new List<string>();
        var project = new DirectorProject
        {
            Scene = ReadLegacyScene(legacyScenePath, faults),
            Effects = ReadLegacyEffects(legacyEffectsPath, faults),
        };

        var saveFault = Save(project, projectPath);
        if (saveFault is not null) faults.Add($"工程文件写入失败：{saveFault}");

        return new ProjectBootstrap(project, faults.Count > 0 ? string.Join("；", faults) : null, true);
    }

    private static PvScene ReadLegacyScene(string path, List<string> faults)
    {
        if (!File.Exists(path)) return PvScene.CreateBlank();

        try
        {
            var scene = PvScene.FromJson(File.ReadAllText(path));
            if (scene is not null) return scene;
            faults.Add($"旧场景文件解析结果为空：{path}");
        }
        catch (Exception ex)
        {
            faults.Add($"旧场景文件解析失败：{ex.Message}");
        }

        return PvScene.CreateBlank();
    }

    private static EffectGraph ReadLegacyEffects(string path, List<string> faults)
    {
        if (!File.Exists(path)) return new EffectGraph();

        try
        {
            return EffectGraphJson.Deserialize(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            faults.Add($"旧效果文件读取失败：{ex.Message}");
            return new EffectGraph();
        }
    }
}
