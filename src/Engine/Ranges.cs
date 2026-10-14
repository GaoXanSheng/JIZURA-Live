// 移植自 frontend/src/jizura/state.ts（cutAround / loopRange）+ jizura/look.ts（exportRangeCuts / exportRange / rangeSuffix）。
// 纯函数：只吃 plan 与 project.exportRange，供预览循环（PreviewEngine）的整首/行/切分/选段四档共用。
//
// 术语反挂（Vue 有意为之，见 Transport.vue 注释：「用户术语是反的」）：
//   内部 Line  = 当前行的完整切分 → 界面文案「切分循环」
//   内部 Cut   = 当前单个切分     → 界面文案「行循环」
//   Range      = 选段范围 → 界面文案「选段循环」（C# 扩展档，Vue 无）

using Newtonsoft.Json.Linq;

namespace JizuraLive.Engine;

/// <summary>预览循环档位（对位 state.ts 的 LoopMode：all/line/cut/false；Range 为 C# 扩展档）。</summary>
public enum LoopMode
{
    All,
    Line,
    Cut,
    Range,
    Off,
}

public static class Ranges
{
    // ---------------- 档位与字符串（MCP / 日志） ----------------

    /// <summary>MCP 档位串 → 内部值；未知返回 null（调用方报 invalid_input）。</summary>
    public static LoopMode? ParseMode(string? s) => s switch
    {
        "all" => LoopMode.All,
        "line" => LoopMode.Line,
        "cut" => LoopMode.Cut,
        "range" => LoopMode.Range,
        "off" => LoopMode.Off,
        _ => null,
    };

    /// <summary>内部值 → MCP 档位串（get_state / preview 工具回执）。</summary>
    public static string ModeKey(LoopMode m) => m switch
    {
        LoopMode.Line => "line",
        LoopMode.Cut => "cut",
        LoopMode.Range => "range",
        LoopMode.Off => "off",
        _ => "all",
    };

    /// <summary>按钮轮转序（对位 Transport.vue 的 LOOP_CYCLE，末尾追加 range 档）。</summary>
    public static readonly LoopMode[] Cycle = { LoopMode.All, LoopMode.Line, LoopMode.Cut, LoopMode.Range, LoopMode.Off };

    // ---------------- 循环范围（state.ts cutAround / loopRange） ----------------

    /// <summary>cutAround：t 落在切分内取那个切分；否则取 start ≤ t 的最后一个切分（可能 null）。</summary>
    public static Cut? CutAround(Plan? plan, double t)
    {
        if (plan == null) return null;
        var c = Planner.CutAt(plan, t);
        if (c != null) return c;
        Cut? ans = null;
        foreach (var cut in plan.Cuts)
        {
            if (cut.Start <= t) ans = cut;
            else break;
        }
        return ans;
    }

    /// <summary>
    /// loopRange（state.ts:199-214）：Line = 当前行的完整切分（首切分 start → 末切分 end，
    /// 跳过 interlude/title）；Cut = 当前单个切分；Range = 选段范围（未设/越界 → 整首）；
    /// 其余（All/Off）= 整首。
    /// </summary>
    public static (double Start, double End) LoopRange(Plan? plan, LoopMode mode, JObject? exportRange, double t)
    {
        var endAll = plan?.Duration ?? 0;
        if (plan == null) return (0, endAll);
        if (mode == LoopMode.Cut)
        {
            var c = CutAround(plan, t);
            return c != null ? (c.Start, c.End) : (0, endAll);
        }
        if (mode == LoopMode.Line)
        {
            var c = CutAround(plan, t);
            if (c == null) return (0, endAll);
            if (c.Line < 0) return (c.Start, c.End);
            var first = double.NaN;
            var last = double.NaN;
            foreach (var x in plan.Cuts)
            {
                if (x.Line != c.Line || x.Layout == "interlude" || x.Layout == "title") continue;
                if (double.IsNaN(first)) first = x.Start;
                last = x.End;
            }
            return double.IsNaN(first) ? (c.Start, c.End) : (first, last);
        }
        if (mode == LoopMode.Range)
            return ExportSpanSeconds(plan, exportRange) ?? (0, endAll);
        return (0, endAll);
    }

    // ---------------- 选段范围（look.ts exportRangeCuts / exportRange / rangeSuffix） ----------------

    /// <summary>
    /// exportRangeCuts：{from,to} 为 0 基切分索引（fork 的存储单位，上游按行）。clamp 到
    /// [0, cuts-1]、from ≤ to；未设（null / from 缺失 / from &lt; 0）/ 空 plan → null。
    /// to 缺失按 0 处理（对齐 JS 的 `r.to|0`），即折叠成单切分。
    /// </summary>
    public static (int From, int To)? ExportRangeCuts(JObject? range, int cutCount)
    {
        if (range == null || cutCount <= 0) return null;
        if (range["from"] == null || !TryInt(range["from"]!, out var from) || from < 0) return null;
        var to = TryInt(range["to"], out var tv) ? tv : 0;
        from = Math.Min(cutCount - 1, from);
        to = Math.Min(cutCount - 1, Math.Max(from, to));
        return (from, to);
    }

    /// <summary>
    /// exportRange：选段 → 秒区间。t0 = 首切分 start − 0.25s（不小于 0）；
    /// t1 = 末切分后一切分的 start，且不超过 b.end + 0.35s；末切分就是整首最后一枚时 t1 = plan.duration。
    /// </summary>
    public static (double T0, double T1)? ExportSpanSeconds(Plan? plan, JObject? exportRange)
    {
        if (plan == null || plan.Cuts.Count == 0) return null;
        var r = ExportRangeCuts(exportRange, plan.Cuts.Count);
        if (r == null) return null;
        var (from, to) = r.Value;
        var a = plan.Cuts[from];
        var b = plan.Cuts[to];
        var last = to == plan.Cuts.Count - 1;
        var t0 = Math.Max(0, a.Start - 0.25);
        var t1 = last ? plan.Duration : Math.Min(plan.Cuts[to + 1].Start, b.End + 0.35);
        return (t0, Math.Max(t0, t1));
    }

    /// <summary>文件名后缀（look.ts rangeSuffix）：未设范围回空串，否则 _C01 / _C01-03。</summary>
    public static string RangeSuffix(JObject? exportRange, int cutCount)
    {
        var r = ExportRangeCuts(exportRange, cutCount);
        if (r == null) return "";
        var (from, to) = r.Value;
        string F(int n) => (n + 1).ToString("00");
        return "_C" + F(from) + (to > from ? "-" + F(to) : "");
    }

    /// <summary>行 → 该行的切分跨度（look.ts setExportRange 的行按钮语义）：行无切分回 null。</summary>
    public static (int From, int To)? LineCutSpan(Plan? plan, int line)
    {
        if (plan == null) return null;
        var from = -1;
        var to = -1;
        for (var i = 0; i < plan.Cuts.Count; i++)
        {
            if (plan.Cuts[i].Line != line) continue;
            if (from < 0) from = i;
            to = i;
        }
        return from < 0 ? null : (from, to);
    }

    /// <summary>
    /// 行按钮语义（look.ts setExportRange）：把该行首→末切分设为选段范围；extend = 与现有范围取并集；
    /// 点在与当前范围完全相同的一段上 = 取消（null）。行没有切分回 false（调用方提示）。
    /// </summary>
    public static bool ApplyLineRange(Project project, Plan? plan, int line, bool extend)
    {
        var span = LineCutSpan(plan, line);
        if (span == null || plan == null) return false;
        var (from, to) = span.Value;
        var cur = ExportRangeCuts(project.ExportRange, plan.Cuts.Count);
        if (extend && cur != null)
            project.ExportRange = new JObject
            {
                ["from"] = Math.Min(cur.Value.From, from),
                ["to"] = Math.Max(cur.Value.To, to),
            };
        else if (cur != null && cur.Value.From == from && cur.Value.To == to)
            project.ExportRange = null;
        else
            project.ExportRange = new JObject { ["from"] = from, ["to"] = to };
        return true;
    }

    /// <summary>JSON 数值取整（JS 的 `|0` 语义）：非数值 / NaN / Inf 回 false。</summary>
    internal static bool TryInt(JToken? tok, out int v)
    {
        v = 0;
        if (tok is not JValue jv) return false;
        if (jv.Type is not (JTokenType.Integer or JTokenType.Float)) return false;
        var d = Convert.ToDouble(jv.Value);
        if (double.IsNaN(d) || double.IsInfinity(d)) return false;
        v = (int)Math.Truncate(d); // JS 的 `|0`：向零截断
        return true;
    }
}
