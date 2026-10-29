using System.IO;
using System.Security.Cryptography;
using System.Text;
using JizuraLive.App;
using JizuraLive.App.I18n;
using JizuraLive.Logging;
using JizuraLive.Render.Effects;
using JizuraLive.Vts;
using Newtonsoft.Json;

namespace JizuraLive.Pv;

public sealed record ProjectLoadResult(DirectorProject? Project, string? Fault);

public sealed record ProjectBootstrap(DirectorProject Project, string? Fault);

public static class ProjectStore
{
    private static readonly object WriteGate = new();

    public static string? SaveJson(DirectorProject project, string path)
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

    public static string? Save(DirectorProject project, string path, byte[]? audio)
    {
        try
        {
            var json = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(project));
            var bytes = JizuraPackage.Build(json, audio);
            lock (WriteGate)
            {
                JizuraPackage.WriteFile(path, bytes);
            }

            Log.Info($"[工程存储] 工程包已保存：{path}（{bytes.Length} 字节，音频 {(audio is { Length: > 0 } ? audio.Length : 0)} 字节）");
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn("[工程存储] 保存失败：" + ex.Message);
            return ex.Message;
        }
    }

    private static string DecodeUtf8(byte[] data)
    {
        var text = Encoding.UTF8.GetString(data);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    public static ProjectLoadResult Load(string path)
    {
        if (!File.Exists(path))
            return new ProjectLoadResult(null, L.F("工程文件不存在：{0}", path));

        string json;
        byte[]? embeddedAudio = null;
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (JizuraPackage.LooksLikePackage(bytes))
            {
                var entries = JizuraPackage.Read(bytes);
                var projectEntry = entries.FirstOrDefault(e => e.Name == "project.json")
                                   ?? throw new InvalidDataException(L.T("工程包缺少 project.json"));
                json = DecodeUtf8(projectEntry.Data);
                embeddedAudio = entries.FirstOrDefault(e => e.Name == "audio.bin")?.Data;
            }
            else
            {
                json = DecodeUtf8(bytes);
            }
        }
        catch (Exception ex)
        {
            return new ProjectLoadResult(null, L.F("工程文件读取失败：{0}", ex.Message));
        }

        DirectorProject? project;
        try
        {
            project = JsonConvert.DeserializeObject<DirectorProject>(json);
        }
        catch (Exception ex)
        {
            return new ProjectLoadResult(null, L.F("工程文件 JSON 损坏：{0}", ex.Message));
        }

        if (project is null)
            return new ProjectLoadResult(null, L.T("工程文件 JSON 损坏：解析结果为空"));

        project.Scene ??= PvScene.CreateDefault();
        project.Preset ??= new LegacyPresetSettings();
        project.Effects ??= new EffectGraph();
        project.Curves ??= new List<CurveSave>();
        project.CameraTrack ??= new CameraTrack();
        project.LayerCameraTracks ??= new Dictionary<string, CameraTrack>(StringComparer.Ordinal);
        project.VtsCues ??= new VtsCueTrack();
        project.DesignSegments ??= new List<DesignSegment>();
        project.LegacyCutOverrides ??= new List<LegacyCutOverride>();
        project.EditorLayout ??= new EditorLayout();
        project.LyricEnds ??= new Dictionary<int, long>();

        if (embeddedAudio is { Length: > 0 })
            RestoreEmbeddedAudio(project, path, embeddedAudio);

        if (!string.IsNullOrWhiteSpace(project.AudioPath) && !Path.IsPathRooted(project.AudioPath))
        {
            try
            {
                project.AudioPath = Path.GetFullPath(
                    Path.Combine(Path.GetDirectoryName(path) ?? "", project.AudioPath));
            }
            catch (Exception ex)
            {
                Log.Warn("[工程存储] 相对音频路径解析失败：" + ex.Message);
            }
        }

        if (project.SchemaVersion > DirectorProject.CurrentSchemaVersion)
            return new ProjectLoadResult(null,
                L.F("工程版本过新（{0} > {1}），本程序不识别，请升级后再打开", project.SchemaVersion, DirectorProject.CurrentSchemaVersion));

        NormalizeScene(project, project.Scene);
        ResolveAssetPaths(project, path);

        SanitizeCurves(project);

        if (project.VtsBase is { } vtsBase)
        {
            vtsBase.X = Finite(vtsBase.X, 0);
            vtsBase.Y = Finite(vtsBase.Y, 0);
            vtsBase.Size = Finite(vtsBase.Size, 1);
            vtsBase.Rotation = Finite(vtsBase.Rotation, 0);
        }

        SanitizeCameraTrack(project);

        project.SyncCharacterCameraTrack();

        SanitizeDesignSegments(project);

        SanitizeLayout(project);

        return new ProjectLoadResult(project, null);
    }

    private static void SanitizeLayout(DirectorProject project)
    {
        var layout = project.EditorLayout ??= new EditorLayout();

        layout.CollapsedTrackIds ??= new List<string>();
        layout.TrackHeights ??= new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var id in layout.TrackHeights.Where(p => !double.IsFinite(p.Value)).Select(p => p.Key).ToList())
            layout.TrackHeights.Remove(id);

        layout.AutoStyle = (layout.AutoStyle ?? "").Trim().ToLowerInvariant();
        if (layout.AutoStyle is not ("soft" or "dynamic" or "epic")) layout.AutoStyle = "dynamic";
        layout.AutoIntensity = Math.Clamp(Finite(layout.AutoIntensity, 0.6), 0, 1);

        layout.DeletedAutoIds ??= new List<string>();
        layout.DeletedAutoIds = layout.DeletedAutoIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        project.LyricEnds ??= new Dictionary<int, long>();
        foreach (var key in project.LyricEnds.Keys.ToList())
            if (key < 0 || project.LyricEnds[key] <= 0) project.LyricEnds.Remove(key);

        project.LegacyCutOverrides ??= new List<LegacyCutOverride>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        project.LegacyCutOverrides = project.LegacyCutOverrides
            .Where(o => o is not null && double.IsFinite(o.Start)
                        && seen.Add($"{o.Line}:{Math.Round(o.Start, 3):F3}"))
            .ToList();
        foreach (var o in project.LegacyCutOverrides)
        {
            o.Layout ??= "";
            o.Enter ??= "";
            o.Exit ??= "";
            o.Treat ??= "";
            o.LayoutParams = SanitizeNumbers(o.LayoutParams);
            o.TreatParams = SanitizeNumbers(o.TreatParams);
            o.BgParams = SanitizeNumbers(o.BgParams);
            o.CamParams = SanitizeNumbers(o.CamParams);
            o.LayoutTexts = SanitizeTexts(o.LayoutTexts);
            o.TreatTexts = SanitizeTexts(o.TreatTexts);
            o.BgTexts = SanitizeTexts(o.BgTexts);
            o.CamTexts = SanitizeTexts(o.CamTexts);
        }
    }

    private static Dictionary<string, double> SanitizeNumbers(Dictionary<string, double>? source)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        if (source is null) return result;
        foreach (var (key, value) in source)
            if (!string.IsNullOrWhiteSpace(key) && double.IsFinite(value))
                result[key] = value;
        return result;
    }

    private static Dictionary<string, string> SanitizeTexts(Dictionary<string, string>? source)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (source is null) return result;
        foreach (var (key, value) in source)
            if (!string.IsNullOrWhiteSpace(key) && value is not null)
                result[key] = value;
        return result;
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
        track.Fps = Finite(track.Fps, 60);
        if (track.Fps <= 0) track.Fps = 60;

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
            if (save is null || save.Curve is null || !ParamAddress.TryParse(save.Address, out var addr))
            {
                Log.Warn("[工程存储] 丢弃不可解析的曲线条目：" + (save is null ? "(null)" : save.Address));
                continue;
            }

            if (addr.Kind == ParamAddressKind.Effect
                && !project.Effects.Nodes.Any(n => n is not null
                    && string.Equals(n.InstanceId, addr.Target, StringComparison.Ordinal)))
            {
                Log.Warn("[工程存储] 丢弃没有对应效果实例的曲线：" + save.Address);
                continue;
            }

            project.Curves[kept++] = save;
        }

        if (kept < project.Curves.Count) project.Curves.RemoveRange(kept, project.Curves.Count - kept);

        foreach (var save in project.Curves) save.Curve.NormalizeKeys();
    }

    private static void RestoreEmbeddedAudio(DirectorProject project, string projectPath, byte[] data)
    {
        try
        {
            Directory.CreateDirectory(AppDirs.AudioCacheDir);
            var source = string.IsNullOrWhiteSpace(project.AudioPath) ? projectPath : project.AudioPath;
            var ext = Path.GetExtension(source);
            if (string.IsNullOrWhiteSpace(ext)) ext = ".mp3";
            var hash = Convert.ToHexString(SHA256.HashData(data))[..12].ToLowerInvariant();
            var cached = Path.Combine(AppDirs.AudioCacheDir, $"pv_audio_{hash}{ext}");
            if (!File.Exists(cached) || new FileInfo(cached).Length != data.Length)
                File.WriteAllBytes(cached, data);
            project.AudioPath = cached;
            Log.Info($"[工程存储] 工程包音频已解包：{cached}");
        }
        catch (Exception ex)
        {
            Log.Warn("[工程存储] 工程包音频提取失败：" + ex.Message);
        }
    }

    private static void NormalizeScene(DirectorProject project, PvScene scene)
    {
        if (scene is null) return;
        scene.Layers ??= new List<PvLayerConfig>();
        scene.Layers.RemoveAll(l => l is null);

        var removed = scene.Layers
            .Where(l => l.Kind == PvLayerKind.VisualAsset
                        && string.IsNullOrWhiteSpace(l.SpoutSenderName)
                        && string.IsNullOrWhiteSpace(l.AssetPath))
            .Select(l => l.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (removed.Count == 0) return;

        scene.Layers = scene.Layers.Where(l => !removed.Contains(l.Id)).ToList();
        project.Curves.RemoveAll(c => c is not null
            && ParamAddress.TryParse(c.Address, out var addr)
            && addr.Kind == ParamAddressKind.Layer
            && removed.Contains(addr.Target));
        foreach (var node in project.Effects.Nodes)
            if (node is not null && removed.Contains(node.Target)) node.Target = "";
        Log.Info($"[工程存储] 已移除 {removed.Count} 个没有来源的素材图层");
    }

    private static void ResolveAssetPaths(DirectorProject project, string projectPath)
    {
        if (project.Scene is not { } scene) return;
        var dir = Path.GetDirectoryName(projectPath) ?? "";
        foreach (var layer in scene.Layers)
        {
            if (layer is null || string.IsNullOrWhiteSpace(layer.AssetPath)) continue;
            if (Path.IsPathRooted(layer.AssetPath)) continue;
            try
            {
                layer.AssetPath = Path.GetFullPath(Path.Combine(dir, layer.AssetPath));
            }
            catch (Exception ex)
            {
                Log.Warn("[工程存储] 图片路径解析失败：" + ex.Message);
            }
        }
    }

    public static ProjectBootstrap LoadOrCreate(string projectPath)
    {
        if (File.Exists(projectPath))
        {
            var loaded = Load(projectPath);
            if (loaded.Project is not null)
                return new ProjectBootstrap(loaded.Project, null);

            Log.Warn("[工程存储] 打开工程失败，本次会话使用默认工程并改用恢复路径，原文件不覆盖：" + loaded.Fault);
            var fallback = new DirectorProject();
            fallback.SyncCharacterCameraTrack();
            return new ProjectBootstrap(fallback, loaded.Fault);
        }

        var project = new DirectorProject();
        NormalizeScene(project, project.Scene);
        project.SyncCharacterCameraTrack();

        var saveFault = SaveJson(project, projectPath);
        return new ProjectBootstrap(project, saveFault is null ? null : L.F("工程文件写入失败：{0}", saveFault));
    }
}
