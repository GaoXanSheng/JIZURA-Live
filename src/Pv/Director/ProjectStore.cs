using System.IO;
using JizuraLive.App;
using JizuraLive.Logging;
using JizuraLive.Render.Effects;
using Newtonsoft.Json;

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
            project = JsonConvert.DeserializeObject<DirectorProject>(json);
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

        Migrate(project);

        NormalizeScene(project, project.Scene);

        SanitizeCurves(project);

        SanitizeCameraTrack(project);

        project.SyncCharacterCameraTrack();

        SanitizeDesignSegments(project);

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
        SanitizeTrack(project.CameraTrack ??= new CameraTrack());
        project.LayerCameraTracks ??= new Dictionary<string, CameraTrack>(StringComparer.Ordinal);
        foreach (var track in project.LayerCameraTracks.Values)
            if (track is not null) SanitizeTrack(track);
    }

    private static void SanitizeTrack(CameraTrack track)
    {
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

    private static void SanitizeDesignSegments(DirectorProject project)
    {
        if (project.DesignSegments.Count == 0) return;

        var kept = 0;
        for (var i = 0; i < project.DesignSegments.Count; i++)
        {
            var seg = project.DesignSegments[i];
            if (seg is null || !double.IsFinite(seg.Start) || !double.IsFinite(seg.End) || seg.End <= seg.Start)
            {
                Log.Warn("[工程存储] 丢弃非法设计段：" + (seg is null ? "(null)" : $"{seg.Start:F3}–{seg.End:F3}"));
                continue;
            }

            if (!Enum.IsDefined(seg.Track))
            {
                Log.Warn($"[工程存储] 丢弃未知轨种的设计段：{seg.Track}（{seg.Start:F3}–{seg.End:F3}）");
                continue;
            }

            seg.Params ??= new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var key in seg.Params.Keys.Where(k => !double.IsFinite(seg.Params[k])).ToList())
                seg.Params.Remove(key);

            project.DesignSegments[kept++] = seg;
        }

        if (kept < project.DesignSegments.Count)
            project.DesignSegments.RemoveRange(kept, project.DesignSegments.Count - kept);
        project.DesignSegments.Sort((a, b) =>
        {
            var c = a.Start.CompareTo(b.Start);
            return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
        });
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

    private static void Migrate(DirectorProject project)
    {
        if (project.SchemaVersion < 3)
        {
            var layers = project.Scene?.Layers;
            if (layers is not null)
            {
                foreach (var layer in layers)
                {
                    if (layer is null || string.IsNullOrWhiteSpace(layer.SpoutSenderName)) continue;

                    // 直播输入默认不跟随机位，避免 VTS 画面自己漂移。
                    layer.Parallax ??= new PvParallaxConfig();
                    layer.Parallax.Depth = 0;
                    layer.Parallax.RotationFactor = 0;
                    layer.Parallax.ScaleFactor = 0;
                }
            }
        }

        project.SchemaVersion = DirectorProject.CurrentSchemaVersion;
    }

    private static void NormalizeScene(DirectorProject project, PvScene scene)
    {
        if (scene is null) return;

        var hollowLayerIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var layer in scene.Layers)
        {
            if (layer is null) continue;
            bool hollow = layer.Kind is PvLayerKind.VisualAsset or PvLayerKind.ForegroundFx
                          && string.IsNullOrWhiteSpace(layer.SpoutSenderName)
                          && string.IsNullOrWhiteSpace(layer.VideoPath)
                          && string.IsNullOrWhiteSpace(layer.ImagePath);
            if (hollow) hollowLayerIds.Add(layer.Id);
        }

        if (hollowLayerIds.Count > 0)
        {
            scene.Layers = scene.Layers.Where(l => l is null || !hollowLayerIds.Contains(l.Id)).ToList();
            int removed = project.Curves.RemoveAll(c =>
                c is not null && c.Address.StartsWith("layer:", StringComparison.Ordinal)
                && ParamAddress.TryParse(c.Address, out var addr)
                && addr.Kind == ParamAddressKind.Layer
                && hollowLayerIds.Contains(addr.Target));
            if (removed > 0)
                Log.Warn($"[工程存储] 规范化：移除 {hollowLayerIds.Count} 个空壳图层及 {removed} 条关联曲线");
        }

        var decorLayerIds = scene.Layers
            .Where(l => l is not null && (l.Kind == PvLayerKind.Decor || l.Decor is not null))
            .Select(l => l.Id)
            .ToHashSet(StringComparer.Ordinal);

        if (decorLayerIds.Count > 0)
        {
            scene.Layers = scene.Layers.Where(l => l is null || !decorLayerIds.Contains(l.Id)).ToList();
            project.Curves.RemoveAll(c =>
                c is not null
                && ParamAddress.TryParse(c.Address, out var addr)
                && addr.Kind == ParamAddressKind.Layer
                && decorLayerIds.Contains(addr.Target));
            Log.Warn($"[工程存储] 规范化：移除 {decorLayerIds.Count} 个已停用的装饰图层");
        }

        if (scene.Layers.RemoveAll(l => l is not null && string.Equals(l.Id, "bg", StringComparison.Ordinal)) > 0)
        {
            project.Curves.RemoveAll(c =>
                c is not null
                && ParamAddress.TryParse(c.Address, out var addr)
                && addr.Kind == ParamAddressKind.Layer
                && string.Equals(addr.Target, "bg", StringComparison.Ordinal));
            Log.Warn("[工程存储] 规范化：移除多余的 Background Layer（已有 PV 背景层）");
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
            var fallback = new DirectorProject();
            fallback.SyncCharacterCameraTrack();
            return new ProjectBootstrap(fallback, loaded.Fault, false);
        }

        var faults = new List<string>();
        var project = new DirectorProject
        {
            Scene = ReadLegacyScene(legacyScenePath, faults),
            Effects = ReadLegacyEffects(legacyEffectsPath, faults),
        };

        NormalizeScene(project, project.Scene);
        project.SyncCharacterCameraTrack();

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
