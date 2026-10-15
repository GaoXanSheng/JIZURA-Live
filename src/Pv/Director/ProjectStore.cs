using System.IO;
using JizuraLive.App;
using JizuraLive.Logging;
using Newtonsoft.Json;

namespace JizuraLive.Pv;

/// <summary>
/// <see cref="ProjectStore.Load"/> 的结果：Project 非空即成功；
/// 失败时 Project 为 null，Fault 以稳定前缀区分原因（不存在 / 读取失败 / JSON 损坏 / 版本过新）。
/// </summary>
public sealed record ProjectLoadResult(DirectorProject? Project, string? Fault);

/// <summary>
/// 工程文件（.jzpv）的读写边界。
///
/// Pv 层复用 App 层原子写（<see cref="AtomicFile"/>，同一程序集内直接引用，不复制第二套临时文件逻辑）：
/// 写入先落 <c>.tmp</c> 再覆盖改名，进程被杀不会留下截断的工程文件。
///
/// 写盘约定与仓库现有 SaveSceneNow 一致：返回 null = 成功，非 null = 失败原因。
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

        return new ProjectLoadResult(project, null);
    }
}
