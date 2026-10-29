using System.Windows;
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

    private sealed class TechPlayback(BitmapSource[] frames)
    {
        public BitmapSource[] Frames { get; } = frames;
        public int Index { get; set; }
    }

    private readonly Dictionary<Image, TechPlayback> _techPlaybacks = new();
    private readonly HashSet<Border> _techAutoPlayHosts = new();
    private DispatcherTimer? _techPreviewTimer;
    private Border? _techPreviewHost;

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
                    if (image.Parent is Border host
                        && (_techAutoPlayHosts.Contains(host) || ReferenceEquals(_techPreviewHost, host)))
                        PlayTechniquePreview(image, frames);
                }));
            }, TaskScheduler.Default);
    }

    private void EnsureTechniquePreview(Border host, string? group, string key, int width, bool autoPlay = false)
    {
        if (group is null) return;
        if (autoPlay) _techAutoPlayHosts.Add(host);
        else _techAutoPlayHosts.Remove(host);

        if (host.Child is Image cached && cached.Tag is TechPreviewTag tag
            && tag.Version == TechniquePreview.Version)
        {
            if (tag.Frames is { Length: > 0 } frames
                && (autoPlay || ReferenceEquals(_techPreviewHost, host)))
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
            _techPlaybacks.Remove(image);
            image.Source = frames[0];
            return;
        }

        _techPreviewTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(125) };
        _techPreviewTimer.Tick -= TechPreviewTick;
        _techPreviewTimer.Tick += TechPreviewTick;
        if (_techPlaybacks.ContainsKey(image))
        {
            if (!_techPreviewTimer.IsEnabled) _techPreviewTimer.Start();
            return;
        }

        _techPlaybacks[image] = new TechPlayback(frames);
        image.Source = frames[0];
        if (!_techPreviewTimer.IsEnabled) _techPreviewTimer.Start();
    }

    private void StopTechniquePreview(Image? image)
    {
        if (image is null || !_techPlaybacks.Remove(image, out var playback)) return;
        if (playback.Frames.Length > 0) image.Source = playback.Frames[0];
        if (_techPlaybacks.Count == 0) _techPreviewTimer?.Stop();
    }

    private void UnloadTechniquePreview(Border host)
    {
        _techAutoPlayHosts.Remove(host);
        if (host.Child is not Image image) return;
        _techPlaybacks.Remove(image);
        if (_techPlaybacks.Count == 0) _techPreviewTimer?.Stop();
        image.Source = null;
        image.Tag = new TechPreviewTag(TechniquePreview.Version, null);
        host.Child = host.Tag as UIElement;
    }

    private void StopTechniquePreview()
    {
        _techPreviewTimer?.Stop();
        _techPlaybacks.Clear();
        _techAutoPlayHosts.Clear();
        _techPreviewHost = null;
    }

    private void TechPreviewTick(object? sender, EventArgs e)
    {
        if (_techPlaybacks.Count == 0)
        {
            _techPreviewTimer?.Stop();
            return;
        }

        foreach (var (image, playback) in _techPlaybacks.ToList())
        {
            if (image.Parent is null)
            {
                _techPlaybacks.Remove(image);
                continue;
            }

            playback.Index = (playback.Index + 1) % playback.Frames.Length;
            image.Source = playback.Frames[playback.Index];
        }

        if (_techPlaybacks.Count == 0) _techPreviewTimer?.Stop();
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
