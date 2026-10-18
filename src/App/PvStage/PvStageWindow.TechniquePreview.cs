using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using JizuraLive.LegacyEngine;
using JizuraLive.Pv;

namespace JizuraLive.App.PvStage;

public partial class PvStageWindow
{
    private void LoadTechniquePreview(Image image, string? group, string key, int width, bool retry = true)
    {
        if (group is null || string.IsNullOrWhiteSpace(key)) return;

        EngineBoot.InstallPacks();
        var preset = Preset.Clone();
        var style = _project.LegacyStyle;
        var seed = _project.LegacySeed;
        var sceneWidth = _scene.Width;
        var sceneHeight = _scene.Height;
        var version = TechniquePreview.Version;

        Task.Run(() => TechniquePreview.Render(preset, style, seed, sceneWidth, sceneHeight, group, key, width))
            .ContinueWith(t =>
            {
                if (t.Status != TaskStatus.RanToCompletion || t.Result is not { } source) return;
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    if (TechniquePreview.Version != version)
                    {
                        if (retry) LoadTechniquePreview(image, group, key, width, retry: false);
                        return;
                    }

                    image.Source = source;
                }));
            }, TaskScheduler.Default);
    }

    private void EnsureTechniquePreview(Border host, string? group, string key, int width)
    {
        if (group is null) return;
        if (host.Child is Image cached && cached.Tag is int version && version == TechniquePreview.Version) return;

        var image = new Image { Stretch = Stretch.Uniform, Tag = TechniquePreview.Version };
        host.Child = image;
        LoadTechniquePreview(image, group, key, width);
    }

    private static string? PreviewGroup(DesignTrackKind kind) => kind switch
    {
        DesignTrackKind.Layout => "layout",
        DesignTrackKind.Enter => "enter",
        DesignTrackKind.Hold => "hold",
        DesignTrackKind.Exit => "exit",
        DesignTrackKind.Decor => "decor",
        DesignTrackKind.Treat => "treat",
        DesignTrackKind.Bg => "bg",
        DesignTrackKind.Cam => "cam",
        DesignTrackKind.Fx => "fx",
        DesignTrackKind.Trans => "trans",
        _ => null,
    };
}
