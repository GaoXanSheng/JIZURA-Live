using System.Threading;
using JizuraLive.Engine;
using JizuraLive.Pv;

namespace JizuraLive.App.PvStage;

public partial class PvStageWindow
{
    private WindowSubtitleSource? _subtitleSource;
    private long _subtitleVersion = 1;

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
