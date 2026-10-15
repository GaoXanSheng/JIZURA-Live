using System.IO;
using JizuraLive.App;
using JizuraLive.Logging;
using JizuraLive.Render.Effects;
using Newtonsoft.Json;

namespace JizuraLive.Pv;

/// <summary>
/// <see cref="ProjectStore.Load"/> 的结果：Project 非空即成功；
/// 失败时 Project 为 null，Fault 以稳定前缀区分原因（不存在 / 读取失败 / JSON 损坏 / 版本过新）。
/// </summary>
public sealed record ProjectLoadResult(DirectorProject? Project, string? Fault);

/// <summary>
/// 工作台启动装载的结果：Project 恒非空（载入失败时是内存里的默认工程，磁盘文件保持原样）；
/// Fault 非空说明有需要告诉用户的原因；ImportedFromLegacy 标记这次是否走了旧版双文件导入。
/// </summary>
public sealed record ProjectBootstrap(DirectorProject Project, string? Fault, bool ImportedFromLegacy);

/// <summary>
/// 工程文件（.jzpv）的读写边界。
///
/// Pv 层复用 App 层原子写（<see cref="AtomicFile"/>，同一程序集内直接引用，不复制第二套临时文件逻辑）：
/// 写入先落 <c>.tmp</c> 再覆盖改名，进程被杀不会留下截断的工程文件。
///
/// 写盘约定沿用旧存盘路径：返回 null = 成功，非 null = 失败原因（调用方负责显示）。
/// <see cref="Load"/> 只读：文件不存在 / 损坏 / 版本过新一律只报告 Fault，绝不写盘——
/// 尤其不沿用「读失败就写回默认」的旧策略，那是覆盖用户数据的入口。
/// </summary>
public static class ProjectStore
{
    /// <summary>写入串行闸：Save 可能被 UI / 恢复任务等多处调用，先到先写，避免竞争同一个 .tmp。</summary>
    private static readonly object WriteGate = new();

    /// <summary>保存工程。成功返回 null，失败返回原因（不抛异常）。</summary>
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

    /// <summary>读取工程。任何失败都不写盘：磁盘上的原文件保持一字未动。</summary>
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
            // 存在但读不出来（占用 / 权限）：同样是"这份内容这次拿不到"，不写盘
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

        // 宽松治理：坏曲线只丢它自己，不改 Load 的既有语义（失败分类、绝不写盘）
        SanitizeCurves(project);

        // 信号图的治理分两档：能定位到"这一条读不出来"的就地丢弃；整图超容量则拒绝打开整份工程
        var signalFault = SanitizeSignalGraph(project);
        if (signalFault is not null) return new ProjectLoadResult(null, signalFault);

        return new ProjectLoadResult(project, null);
    }

    /// <summary>
    /// 信号图的装载治理：**只处理"这条内容读不出来"，不处理"这张图语义非法"**。
    ///
    /// 丢弃型（改内存 + 告警）：未知节点种类连它的边与绑定一起丢、schema 未声明的参数键、不可解析的绑定地址。
    /// 拒绝型（返回原因 ⇒ 整份工程打不开）：节点/边数超上限——只丢几个节点就悄悄换成"另一张语义不同的图"，
    /// 比打不开更糟（契约 §15.3「超过限制在候选采用之前拒绝并说明数量，不截断保存」）。
    ///
    /// 重复 Id / 悬空端口 / 环 / 单位不一致 / 目标不存在**不在这里丢**：它们留给编译期报错，
    /// 因为编译器能一次报全并保留用户修错所需的上下文（契约 §8.1「静态配置错误在编辑/编译时拒绝」）。
    /// 返回 null = 可以继续打开。
    /// </summary>
    private static string? SanitizeSignalGraph(DirectorProject project)
    {
        // "SignalGraph": null 按空图处理，不算打不开（与 EffectGraphJson 收到 null 的策略一致）
        if (project.SignalGraph is null)
        {
            project.SignalGraph = new SignalGraph();
            return null;
        }

        var graph = project.SignalGraph;

        // 手写 JSON 可能把三个列表写成 null：这里的约定是"没有就是空"，而不是抛异常
        if (graph.Nodes is null) graph.Nodes = new List<SignalNode>();
        if (graph.Edges is null) graph.Edges = new List<SignalEdge>();
        if (graph.Bindings is null) graph.Bindings = new List<SignalBinding>();

        if (graph.Nodes.Count > SignalGraph.MaxNodes || graph.Edges.Count > SignalGraph.MaxEdges)
            return $"信号图超出容量上限（节点 {graph.Nodes.Count}/{SignalGraph.MaxNodes}，边 {graph.Edges.Count}/{SignalGraph.MaxEdges}），本程序不打开这份工程";

        // 未知种类的节点无法求值，留着只会让编译报错；它挂着的边与绑定一起走（引用完整性）
        HashSet<string>? doomed = null;
        for (var i = graph.Nodes.Count - 1; i >= 0; i--)
        {
            var node = graph.Nodes[i];
            if (node is not null && SignalNodeCatalog.Get(node.Kind) is not null) continue;

            Log.Warn("[工程存储] 丢弃未知种类的信号节点：" +
                     (node is null ? "(null)" : $"{(int)node.Kind}（{node.Id}）"));

            if (node is not null && !string.IsNullOrEmpty(node.Id))
                (doomed ??= new HashSet<string>(StringComparer.Ordinal)).Add(node.Id);

            graph.Nodes.RemoveAt(i);
        }

        if (doomed is not null)
        {
            // 局部非空副本：lambda 里不能依赖外层变量的可空性分析
            var dropped = doomed;
            graph.Edges.RemoveAll(e => e is null ||
                dropped.Contains(e.FromNode ?? "") || dropped.Contains(e.ToNode ?? ""));
            graph.Bindings.RemoveAll(b => b is null || dropped.Contains(b.NodeId ?? ""));
        }

        foreach (var node in graph.Nodes)
        {
            if (node is null) continue;
            var def = SignalNodeCatalog.Get(node.Kind);
            if (def is null) continue;

            // 同上：手写 JSON 可能给出 null 字典，按"没有参数"处理
            if (node.Params is null) node.Params = new Dictionary<string, double>();
            if (node.Texts is null) node.Texts = new Dictionary<string, string>();

            // 只接受 schema 里声明过的键（同 EffectGraphJson 对未知 key 的处理：丢这一个，不炸整份工程）
            DropUnknownKeys(node.Id, node.Params, def, SignalParamKind.Number);
            DropUnknownTextKeys(node.Id, node.Texts, def);
        }

        // 坏地址只说明"这条绑定读不出来"，不代表整个工程不可用（同曲线对坏地址的处理）
        for (var i = graph.Bindings.Count - 1; i >= 0; i--)
        {
            var binding = graph.Bindings[i];
            if (binding is not null && ParamAddress.TryParse(binding.Address, out _)) continue;

            Log.Warn("[工程存储] 丢弃不可解析的信号绑定：" + (binding is null ? "(null)" : binding.Address));
            graph.Bindings.RemoveAt(i);
        }

        return null;
    }

    /// <summary>丢掉节点上 schema 未声明的参数键。先收集再删，避免边遍历边改集合。</summary>
    private static void DropUnknownKeys(string nodeId, Dictionary<string, double> values, SignalNodeDef def, SignalParamKind kind)
    {
        if (values.Count == 0) return;

        List<string>? doomed = null;
        foreach (var key in values.Keys)
        {
            var p = SignalNodeCatalog.FindParam(def, key);
            if (p is not null && p.Kind == kind) continue;
            (doomed ??= new List<string>()).Add(key);
        }

        if (doomed is null) return;

        foreach (var key in doomed)
        {
            Log.Warn($"[工程存储] 丢弃节点 {nodeId} 上未声明的参数键：{key}");
            values.Remove(key);
        }
    }

    /// <summary>文本参数同理（目前只有地址文本，但规则一样：未声明就丢）。</summary>
    private static void DropUnknownTextKeys(string nodeId, Dictionary<string, string> texts, SignalNodeDef def)
    {
        if (texts.Count == 0) return;

        List<string>? doomed = null;
        foreach (var key in texts.Keys)
        {
            var p = SignalNodeCatalog.FindParam(def, key);
            if (p is not null && p.Kind == SignalParamKind.Text) continue;
            (doomed ??= new List<string>()).Add(key);
        }

        if (doomed is null) return;

        foreach (var key in doomed)
        {
            Log.Warn($"[工程存储] 丢弃节点 {nodeId} 上未声明的文本参数键：{key}");
            texts.Remove(key);
        }
    }

    /// <summary>
    /// 曲线的宽松治理：地址解析失败（或曲线为 null）的条目从**内存**里丢弃并告警。
    ///
    /// 一条坏地址只说明"这条曲线读不出来"，不代表整个工程不可用——场景、效果、歌词都还在，
    /// 为此把整份工程判成损坏（或抛异常）是不成比例的。
    /// 只改内存、不写盘：磁盘文件保持原样，用户下次主动保存才真正落盘（与 Load 不写盘的约定一致）。
    /// </summary>
    private static void SanitizeCurves(DirectorProject project)
    {
        if (project.Curves.Count == 0) return;

        // 就地压缩，保序；先记数再统一 RemoveRange，避免边遍历边删
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

        // 手改文件可能塞进乱序/重复 key，破坏二分求值的前提（Keys 升序且时刻唯一）；
        // 恢复不变量，只改内存
        foreach (var save in project.Curves) save.Curve.NormalizeKeys();
    }

    /// <summary>
    /// 工作台启动入口：工程文件存在就只走 <see cref="Load"/>；不存在才把旧版
    /// <c>pv_scene.json</c> / <c>pv_effects.json</c> 导入成新工程。
    ///
    /// 只有「工程文件不存在」这一条路径允许写盘。工程存在但坏了/被占用时，只把默认工程放进内存，
    /// 绝不借导入之名把用户的工程文件用旧文件内容覆盖掉——那是覆盖数据的入口，比打不开更糟。
    /// </summary>
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

        // 工程文件不存在 = 首次升级到工程格式：只读解析两个旧文件，一个字节都不改
        // （用户可能还要用旧版程序打开它们）。解析问题只记进 Fault，继续用能拿到的部分。
        var faults = new List<string>();
        var project = new DirectorProject
        {
            Scene = ReadLegacyScene(legacyScenePath, faults),
            Effects = ReadLegacyEffects(legacyEffectsPath, faults),
        };

        // 导入结果立即落成工程文件：下次启动就走 Load，导入只发生一次。
        // 写失败不致命——本次会话照常用内存里的导入结果，只把原因交给调用方显示。
        var saveFault = Save(project, projectPath);
        if (saveFault is not null) faults.Add($"工程文件写入失败：{saveFault}");

        return new ProjectBootstrap(project, faults.Count > 0 ? string.Join("；", faults) : null, true);
    }

    /// <summary>只读导入旧版场景：文件不存在/解析失败都回落**空白场景**并在 Fault 里记原因
    /// （没有旧数据不等于该送用户一套模板图层）。</summary>
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

    /// <summary>
    /// 只读导入旧版效果图：文件不存在用空图；<see cref="EffectGraphJson.Deserialize"/> 自带
    /// 「损坏按空图处理」的回落与告警，这里只需兜住读取失败（占用 / 权限）。
    /// </summary>
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
