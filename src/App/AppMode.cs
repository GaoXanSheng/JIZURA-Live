namespace JizuraLive.App;

/// <summary>
/// 界面模式（标题栏三档切换，<see cref="MainWindow.SetMode"/>）：
/// 编辑器 = 全套编辑面板；播放器 = 跟着云歌单出 PV 的满画面布局；
/// PV = 播放器布局 + 右侧 PV 面板（多路 Spout 源实时合成 + 推流控制台）。
/// 后两者共用云播放会话（进模式快照工程、退出回编辑器才还原）。
/// </summary>
public enum AppMode
{
    Editor,
    Player,
    Pv,
}
