namespace JizuraLive.Pv;

/// <summary>
/// 播放音轨信息
/// </summary>
public readonly record struct PvTrackInfo(
    string SongName,
    string Singer,
    double Bpm,
    double Duration
);

/// <summary>
/// PV 音轨与节拍事件总线（彻底解耦播放器、外设与渲染）
/// 播放器只发布歌曲与时间信息，外设（如 VTS 热键联动、灯光同步）自行订阅，严禁反向直接引用
/// </summary>
public static class PvTrackEventBus
{
    public static event Action<PvTrackInfo>? SongChanged;
    public static event Action<int>? BeatHit; // 节拍击打事件 (拍号 0, 1, 2, 3...)

    /// <summary>发布换歌事件</summary>
    public static void PublishSongChanged(string songName, string singer, double bpm, double duration)
    {
        SongChanged?.Invoke(new PvTrackInfo(songName, singer, bpm, duration));
    }

    /// <summary>发布节拍打击事件</summary>
    public static void PublishBeatHit(int beatIndex)
    {
        BeatHit?.Invoke(beatIndex);
    }
}
