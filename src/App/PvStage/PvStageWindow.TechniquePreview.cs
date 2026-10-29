using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using JizuraLive.LegacyEngine;
using JizuraLive.Pv;

namespace JizuraLive.App.PvStage;

public partial class PvStageWindow
{
    private sealed class TechPreviewTag(int version, BitmapSource[]? frames)
    {
        public int Version { get; } = version;

        public BitmapSource[]? Frames { get; } = frames;
    }

    private DispatcherTimer? _techPreviewTimer;
    private Border? _techPreviewHost;
    private Image? _techPreviewImage;
    private BitmapSource[]? _techPreviewFrames;
    private int _techPreviewIndex;

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

        Task.Run(() => TechniquePreview.RenderFrames(preset, style, seed, sceneWidth, sceneHeight, group, key, width))
            .ContinueWith(t =>
            {
                if (t.Status != TaskStatus.RanToCompletion || t.Result is not { Length: > 0 } frames) return;
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    if (TechniquePreview.Version != version)
                    {
                        if (retry) LoadTechniquePreview(image, group, key, width, retry: false);
                        return;
                    }

                    image.Tag = new TechPreviewTag(version, frames);
                    image.Source = frames[0];
                    if (ReferenceEquals(_techPreviewHost, image.Parent)) PlayTechniquePreview(image, frames);
                }));
            }, TaskScheduler.Default);
    }

    private void EnsureTechniquePreview(Border host, string? group, string key, int width)
    {
        if (group is null) return;
        if (host.Child is Image cached && cached.Tag is TechPreviewTag tag
            && tag.Version == TechniquePreview.Version)
        {
            if (ReferenceEquals(_techPreviewHost, host) && tag.Frames is { Length: > 0 } frames)
                PlayTechniquePreview(cached, frames);
            return;
        }

        var image = new Image
        {
            Stretch = Stretch.Uniform,
            Tag = new TechPreviewTag(TechniquePreview.Version, null),
        };
        host.Child = image;
        LoadTechniquePreview(image, group, key, width);
    }

    private void PlayTechniquePreview(Image image, BitmapSource[] frames)
    {
        if (frames.Length <= 1)
        {
            image.Source = frames[0];
            return;
        }

        _techPreviewTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(125) };
        _techPreviewTimer.Tick -= TechPreviewTick;
        _techPreviewTimer.Tick += TechPreviewTick;
        _techPreviewImage = image;
        _techPreviewFrames = frames;
        _techPreviewIndex = 0;
        image.Source = frames[0];
        if (!_techPreviewTimer.IsEnabled) _techPreviewTimer.Start();
    }

    private void StopTechniquePreview(Image? image)
    {
        if (image is null || !ReferenceEquals(_techPreviewImage, image)) return;

        _techPreviewTimer?.Stop();
        if (_techPreviewFrames is { Length: > 0 }) image.Source = _techPreviewFrames[0];
        _techPreviewImage = null;
        _techPreviewFrames = null;
        _techPreviewIndex = 0;
    }

    private void StopTechniquePreview()
    {
        _techPreviewTimer?.Stop();
        _techPreviewImage = null;
        _techPreviewFrames = null;
        _techPreviewIndex = 0;
        _techPreviewHost = null;
    }

    private void TechPreviewTick(object? sender, EventArgs e)
    {
        if (_techPreviewImage is null || _techPreviewFrames is not { Length: > 1 })
        {
            _techPreviewTimer?.Stop();
            return;
        }

        _techPreviewIndex = (_techPreviewIndex + 1) % _techPreviewFrames.Length;
        _techPreviewImage.Source = _techPreviewFrames[_techPreviewIndex];
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
