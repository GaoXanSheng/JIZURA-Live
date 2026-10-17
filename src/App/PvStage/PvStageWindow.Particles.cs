using System.Windows;
using System.Windows.Controls;
using JizuraLive.Logging;
using JizuraLive.Pv;

namespace JizuraLive.App.PvStage;

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

    private static string LayerMetaLabel(PvLayerConfig layer) =>
        layer.Particles is { } p ? $"粒子 · {ParticleKindLabel(p.Kind)}" : LayerKindLabel(layer.Kind);
}
