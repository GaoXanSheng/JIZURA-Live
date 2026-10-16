using System.Windows;
using System.Windows.Controls;
using JizuraLive.Logging;
using JizuraLive.Pv;

namespace JizuraLive.App.PvStage;

/// <summary>粒子图层入口：只投递种类名，图层配置与提供者由管线在渲染线程上建。</summary>
public partial class PvStageWindow
{
    private static readonly string[] ParticleKinds = ["sakura", "sparkle", "bokeh"];

    private void BtnAddParticles_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = BtnAddParticles };
        foreach (var kind in ParticleKinds)
        {
            var k = kind;
            var item = new MenuItem { Header = ParticleKindLabel(k) };
            item.Click += (_, _) => AddParticleLayer(k);
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    /// <summary>
    /// 加一层粒子：与「+ 源」同一条路——只投递种类名，图层配置由管线按同一条注册路径生成
    /// （Id 接着 src_&lt;n&gt; 编号）。入撤销栈与落盘由 LayersChanged 回调统一做，
    /// 这里再造一份配置就是第二个真相。
    /// </summary>
    private void AddParticleLayer(string kind)
    {
        var pipe = _pipe;
        if (pipe is null)
        {
            TblStatus.Text = "舞台未就绪：粒子图层未加入";
            return;
        }

        pipe.RequestAddParticles(kind);
        TblStatus.Text = $"添加粒子图层 {ParticleKindLabel(kind)}…";
        Log.Info($"[工作台] 添加粒子图层：{kind}");
    }

    private static string ParticleKindLabel(string kind) => kind switch
    {
        "sparkle" => "星光",
        "bokeh" => "光斑",
        "sakura" => "樱花",
        _ => kind,
    };

    /// <summary>图层卡片副标题：粒子层报种类（「粒子 · 樱花」），其余沿用类别名。</summary>
    private static string LayerMetaLabel(PvLayerConfig layer) =>
        layer.Particles is { } p ? $"粒子 · {ParticleKindLabel(p.Kind)}" : LayerKindLabel(layer.Kind);
}
