using JizuraLive.Render.Gpu;

namespace JizuraLive.Render.Effects;

/// <summary>
/// 效果图：一串有序的效果节点，按顺序求值成一条 GPU pass 链。
///
/// 求值是**纯数据到数据**的（节点 + 时间 + 尺寸 → <see cref="GpuPass"/> 列表），不碰任何 GPU 状态——
/// 所以它能被单测钉住（参数打包对不对、槽位有没有越界），而 <see cref="GpuEffectRunner"/> 只负责把
/// 这条链跑出来。
///
/// 参数打包规则：每个 <see cref="EffectParam"/> 声明自己的 (Slot, Comp)，值按 key 从节点取，
/// 颜色占 3 个连续分量。槽位 15 是全局保留槽（时间/宽/高/宽高比），效果不得占用。
/// </summary>
public sealed class EffectGraph
{
    /// <summary>全局保留槽（与 <c>ShaderSources</c> 的约定一致）。</summary>
    public const int GlobalSlot = 15;

    public List<EffectNode> Nodes { get; } = new();

    public EffectGraph Add(string defId, Action<EffectNode>? configure = null)
    {
        var node = new EffectNode { DefId = defId, InstanceId = NewInstanceId(defId) };
        configure?.Invoke(node);
        Nodes.Add(node);
        return this;
    }

    /// <summary>
    /// 发一个实例 Id：<c>fx_&lt;defId&gt;_&lt;6 位十六进制&gt;</c>。
    /// 为什么不用 GUID 全长：Id 要写进 JSON、还会显示在 UI 上，选短格式是为了能一眼看出
    /// 「这是哪个效果的第几个实例」；6 位十六进制约 1677 万种，单张图内的碰撞概率可忽略。
    /// </summary>
    internal static string NewInstanceId(string defId) =>
        $"fx_{defId}_{Guid.NewGuid().ToString("N")[..6]}";

    public bool Remove(string defId) => Nodes.RemoveAll(n => n.DefId == defId) > 0;

    public EffectNode? Find(string defId) => Nodes.FirstOrDefault(n => n.DefId == defId);

    /// <summary>
    /// 深拷贝：节点（连实例 Id）、启用状态、全部参数值与颜色都是新的，与原名下一份不共享。
    ///
    /// 这是跨线程发布的唯一入口：UI 线程改完文档图后克隆一份交给渲染线程（<c>PvPipeline.PublishEffects</c>），
    /// 渲染线程此后只碰这份克隆。两边不再同时摸同一个节点列表，界面里的增删/排序也就不会与渲染线程
    /// 每帧的 <c>foreach Nodes</c> 撞出「集合已修改」。
    /// </summary>
    public EffectGraph Clone()
    {
        var copy = new EffectGraph();
        foreach (var node in Nodes)
        {
            var clone = new EffectNode
            {
                DefId = node.DefId,
                InstanceId = node.InstanceId,
                Enabled = node.Enabled,
            };
            foreach (var (key, value) in node.Values) clone.Values[key] = value;
            foreach (var (key, color) in node.Colors) clone.Colors[key] = color;
            copy.Nodes.Add(clone);
        }

        return copy;
    }

    /// <summary>
    /// 把节点搬到 <paramref name="toIndex"/>（列表顺序 = 执行顺序，效果架拖拽重排的写入口）。
    /// 搬的是节点对象本身，实例 Id 与参数值都跟着走——曲线与信号绑定按实例 Id 寻址，重排不该动它们。
    /// <paramref name="toIndex"/> 越界夹进合法区间；返回 false = 位置没变（找不到节点 / 本来就在那儿）。
    /// </summary>
    public bool Move(EffectNode node, int toIndex)
    {
        var from = Nodes.IndexOf(node);
        if (from < 0) return false;

        toIndex = Math.Clamp(toIndex, 0, Nodes.Count - 1);
        if (from == toIndex) return false;

        Nodes.RemoveAt(from);
        Nodes.Insert(toIndex, node);
        return true;
    }

    /// <summary>
    /// 求值成渲染图（多输入 / 多尺寸 / 任意拓扑）。
    /// <paramref name="time"/> 秒级、<paramref name="width"/>/<paramref name="height"/> 是外部输入的像素尺寸
    /// （像素单位的参数在着色器里靠它归一化）。
    /// </summary>
    public GpuRenderGraph BuildRenderGraph(ShaderLibrary library, float time, int width, int height)
    {
        var graph = new GpuRenderGraph();
        var prev = GpuRenderGraph.External;

        foreach (var node in Nodes)
        {
            if (!node.Enabled) continue;
            var def = EffectCatalog.Get(node.DefId);
            if (def is null) continue;

            if (def.GraphBuild is not null)
            {
                // 多 pass：由效果自己编排，返回它在本图里的输出槽位
                prev = def.GraphBuild(node, graph, library, prev, width, height);
                continue;
            }

            graph.Add(library.GetPixel(def.Id, def.Hlsl),
                PackConstants(def, node, time, width, height), prev);
            prev = graph.Count - 1;
        }

        // 统一补全局槽：单 pass 与多 pass 的常量数组都在这里拿到时间/尺寸
        foreach (var pass in graph.Passes) FillGlobals(pass.Constants, time, width, height);
        return graph;
    }

    /// <summary>线性链的便捷视图（老调用方/测试用；等价于渲染图，只是丢掉图信息）。</summary>
    public List<GpuPass> BuildPasses(ShaderLibrary library, float time, int width, int height)
    {
        var graph = BuildRenderGraph(library, time, width, height);
        return graph.Passes.Select(p => new GpuPass(p.Shader, p.Constants)).ToList();
    }

    /// <summary>把节点的参数按 schema 打包进常量数组（不含全局槽）。</summary>
    public static float[] PackConstants(EffectDef def, EffectNode node, float time, int width, int height)
    {
        var c = new float[FullScreenPass.ParamSlots * 4];
        foreach (var p in def.Params)
        {
            var baseIndex = p.Slot * 4 + p.Comp;
            switch (p.Kind)
            {
                case EffectParamKind.Number:
                case EffectParamKind.Bool:
                    c[baseIndex] = node.Value(p);
                    break;
                case EffectParamKind.Color:
                    var (r, g, b) = EffectNode.ParseHex(node.ColorValue(p));
                    c[baseIndex] = r;
                    if (baseIndex + 1 < c.Length) c[baseIndex + 1] = g;
                    if (baseIndex + 2 < c.Length) c[baseIndex + 2] = b;
                    break;
            }
        }

        FillGlobals(c, time, width, height);
        return c;
    }

    /// <summary>填全局保留槽：时间 / 宽 / 高 / 宽高比。</summary>
    public static void FillGlobals(float[] c, float time, int width, int height)
    {
        var gi = GlobalSlot * 4;
        if (gi + 3 >= c.Length) return;
        c[gi] = time;
        c[gi + 1] = width;
        c[gi + 2] = height;
        c[gi + 3] = height > 0 ? width / (float)height : 1f;
    }

    /// <summary>一个默认的整帧后期链（演示/默认预设用）：调色 → 泛光 → 颗粒 → 暗角。</summary>
    public static EffectGraph CreateDefaultPost() => new EffectGraph()
        .Add("colorGrade", n =>
        {
            n.Set(EffectCatalog.Get("colorGrade")!.Params[0], 1.1f);
            n.Set(EffectCatalog.Get("colorGrade")!.Params[1], 1.05f);
        })
        .Add("glow", n => n.Set(EffectCatalog.Get("glow")!.Params[1], 0.9f))
        .Add("grain", n => n.Set(EffectCatalog.Get("grain")!.Params[0], 0.05f))
        .Add("vignette", n => n.Set(EffectCatalog.Get("vignette")!.Params[0], 0.45f));
}
