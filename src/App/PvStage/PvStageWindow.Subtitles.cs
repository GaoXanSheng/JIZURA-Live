using System.Threading;
using JizuraLive.App.I18n;
using JizuraLive.Engine;
using JizuraLive.Pv;

namespace JizuraLive.App.PvStage;

public partial class PvStageWindow
{
    private const string SubtitleLayerId = "subtitle_top";

    private WindowSubtitleSource? _subtitleSource;
    private long _subtitleVersion = 1;

    private bool SyncSubtitleLayer()
    {
        var index = _scene.Layers.FindIndex(l => l is not null
            && (string.Equals(l.Id, SubtitleLayerId, StringComparison.Ordinal)
                || l.Kind == PvLayerKind.Subtitle));
        if (App.SettingsOf()?.PreviewSubtitles != true)
        {
            if (index < 0) return false;
            var existing = _scene.Layers[index];
            _scene.Layers = PvPipeline.WithLayerRemovedAt(_scene.Layers, index);
            if (ReferenceEquals(_selectedLayer, existing)) _selectedLayer = null;
            return true;
        }

        if (index >= 0)
        {
            var existing = _scene.Layers[index];
            if (existing.Kind != PvLayerKind.Subtitle) return false;

            var changed = false;
            if (existing.Topmost is null)
            {
                existing.Topmost = true;
                changed = true;
            }

            if (!string.IsNullOrEmpty(existing.SpoutSenderName))
            {
                existing.SpoutSenderName = null;
                changed = true;
            }

            if (IsDefaultBlend(existing.VisualBlend))
            {
                existing.VisualBlend = NeutralBlend();
                changed = true;
            }

            return changed;
        }

        var next = new List<PvLayerConfig>(_scene.Layers)
        {
            new()
            {
                Id = SubtitleLayerId,
                Name = L.T("StageUi.Layer.SubtitleLayer"),
                Kind = PvLayerKind.Subtitle,
                Topmost = true,
                SpoutSenderName = null,
                Parallax = new PvParallaxConfig
                {
                    Depth = 0,
                    Follow = 0,
                    RotationFactor = 0,
                    ScaleFactor = 0,
                    Defocus = 0,
                },
                VisualBlend = NeutralBlend(),
            },
        };
        _scene.Layers = next;
        return true;
    }

    private static PvVisualBlendConfig NeutralBlend() => new()
    {
        AmbientTintIntensity = 0,
        RimGlow = 0,
        DropShadow = 0,
        ShadowOffsetY = 0,
        AntiBleedMargin = 0,
    };

    private static bool IsDefaultBlend(PvVisualBlendConfig blend) =>
        Math.Abs(blend.AmbientTintIntensity - 0.18) < 1e-9
        && Math.Abs(blend.RimGlow - 0.15) < 1e-9
        && Math.Abs(blend.DropShadow - 0.3) < 1e-9
        && Math.Abs(blend.ShadowOffsetY - 16) < 1e-9
        && Math.Abs(blend.AntiBleedMargin - 0.06) < 1e-9;

    private WindowSubtitleSource SubtitleSource => _subtitleSource ??= new WindowSubtitleSource(this);

    private sealed class WindowSubtitleSource(PvStageWindow owner) : IPvSubtitleSource
    {
        public IReadOnlyList<KrcLine>? Lines
        {
            get
            {
                var list = new List<KrcLine>(owner._lyricClips.Count);
                foreach (var clip in owner._lyricClips)
                    list.Add(new KrcLine(clip.StartMs, clip.EndMs - clip.StartMs, clip.Text,
                        clip.Words, clip.SourceIndex));
                return list;
            }
        }

        public SubtitleConfig? Config
        {
            get
            {
                var settings = App.SettingsOf();
                if (settings is null) return null;
                return settings.PreviewSubtitles
                    ? settings.Subtitle
                    : new SubtitleConfig { Content = SubContent.Off };
            }
        }

        public long Version => Interlocked.Read(ref owner._subtitleVersion);
    }
}
