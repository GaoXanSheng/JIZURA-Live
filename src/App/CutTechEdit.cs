using JizuraLive.Engine;
using Newtonsoft.Json.Linq;

namespace JizuraLive.App;

/// <summary>
/// 行覆盖（project.overrides[line]）里「切分手法」相关键的写入：cutTech / cutQuiet / cutLayouts
/// （对位 look.ts 的 setCutTech / markCutQuiet）。
/// 原先 CutInfo 与 McpTools 各存一份逐行对位的私有实现，切分手法替换面板又要开第三处——统一到这里，
/// 三处（切分信息行 / MCP 工具 / 替换面板）共用同一份语义：
///   · setCutTech 写 cutTech[k][group]，并顺手清掉该组的 cutQuiet（钉过就不静默了）；
///   · layout 组额外镜像到 cutLayouts[k]（planner 读它，对位 JS 的合并顺序）；
///   · 空对象一律删键（工程 JSON 里不留空壳）。
/// </summary>
internal static class CutTechEdit
{
    public static JObject TakeOverride(Project p, int line)
        => p.Overrides.TryGetValue(line.ToString(), out var o) && o != null ? new JObject(o) : new JObject();

    public static void PutOverride(Project p, int line, JObject ov)
    {
        if (ov.Count > 0) p.Overrides[line.ToString()] = ov;
        else p.Overrides.Remove(line.ToString());
    }

    /// <summary>把某切分的某手法组钉成 key；key 为空 = 恢复自动（删该组槽位）。</summary>
    public static void SetCutTech(Project p, int line, int k, string group, string key)
    {
        var cur = TakeOverride(p, line);
        var cutTech = cur["cutTech"] as JObject ?? new JObject();
        var slot = cutTech[k.ToString()] as JObject ?? new JObject();
        cutTech.Remove(k.ToString());
        if (key.Length == 0) slot.Remove(group);
        else slot[group] = key;
        if (slot.Count > 0) cutTech[k.ToString()] = slot;
        else cutTech.Remove(k.ToString());
        if (cutTech.Count > 0) cur["cutTech"] = cutTech;
        else cur.Remove("cutTech");

        var cutQuiet = cur["cutQuiet"] as JObject ?? new JObject();
        var q = cutQuiet[k.ToString()] as JObject ?? new JObject();
        cutQuiet.Remove(k.ToString());
        q.Remove(group);
        if (q.Count > 0) cutQuiet[k.ToString()] = q;
        else cutQuiet.Remove(k.ToString());
        if (cutQuiet.Count > 0) cur["cutQuiet"] = cutQuiet;
        else cur.Remove("cutQuiet");

        if (group == "layout")
        {
            var cutLayouts = cur["cutLayouts"] as JObject ?? new JObject();
            if (key.Length == 0) cutLayouts.Remove(k.ToString());
            else cutLayouts[k.ToString()] = key;
            if (cutLayouts.Count > 0) cur["cutLayouts"] = cutLayouts;
            else cur.Remove("cutLayouts");
        }
        PutOverride(p, line, cur);
    }

    /// <summary>静默标记（随机不许再动这些组）：on = 打标，off = 清掉。</summary>
    public static void MarkCutQuiet(Project p, int line, int k, string[] groups, bool on)
    {
        var cur = TakeOverride(p, line);
        var cutQuiet = cur["cutQuiet"] as JObject ?? new JObject();
        var q = cutQuiet[k.ToString()] as JObject ?? new JObject();
        cutQuiet.Remove(k.ToString());
        foreach (var g in groups)
        {
            if (on) q[g] = true;
            else q.Remove(g);
        }
        if (q.Count > 0) cutQuiet[k.ToString()] = q;
        else cutQuiet.Remove(k.ToString());
        if (cutQuiet.Count > 0) cur["cutQuiet"] = cutQuiet;
        else cur.Remove("cutQuiet");
        PutOverride(p, line, cur);
    }
}
