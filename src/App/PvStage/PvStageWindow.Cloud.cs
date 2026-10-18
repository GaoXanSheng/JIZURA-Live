using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using JizuraLive.App.Cloud;
using JizuraLive.Logging;

namespace JizuraLive.App.PvStage;

public partial class PvStageWindow
{
    private void BtnProjectMenu_Click(object sender, RoutedEventArgs e) =>
        ShowMenu((FrameworkElement)sender, new (string Label, Action Click)[]
        {
            ("新建项目", () => BtnNewProject_Click(this, new RoutedEventArgs())),
            ("打开项目…", () => BtnOpenProject_Click(this, new RoutedEventArgs())),
            ("保存", SaveNow),
            ("另存为…", SaveProjectAs),
        });

    private void BtnImportMenu_Click(object sender, RoutedEventArgs e) =>
        ShowMenu((FrameworkElement)sender, new (string Label, Action Click)[]
        {
            ("导入音频…", () => BtnOpenAudio_Click(this, new RoutedEventArgs())),
            ("导入歌词…", () => BtnOpenLyrics_Click(this, new RoutedEventArgs())),
        });

    private void ShowMenu(FrameworkElement anchor, IReadOnlyList<(string Label, Action Click)> items)
    {
        var panel = new StackPanel { MinWidth = 150 };
        Popup? popup = null;

        foreach (var (label, action) in items)
        {
            var button = new Wpf.Ui.Controls.Button
            {
                Content = label,
                Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 6, 12, 6),
            };
            button.Click += (_, _) =>
            {
                if (popup is not null) popup.IsOpen = false;
                action();
            };
            panel.Children.Add(button);
        }

        var root = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4),
            Child = panel,
            Background = TryFindResource("ApplicationBackgroundBrush") as Brush
                         ?? new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x24)),
        };
        root.SetResourceReference(Border.BorderBrushProperty, CardStrokeKey);

        popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 4,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = root,
            IsOpen = true,
        };
    }

    internal async void BtnCloudMusic_Click(object sender, RoutedEventArgs e) => await ImportCloudMusicAsync();

    private async Task ImportCloudMusicAsync()
    {
        var dlg = new CloudMusicWindow { Owner = this };
        if (dlg.ShowDialog() != true || dlg.SelectedSong is not { } song) return;

        TblStatus.Text = $"云端音乐：{song.Singer} - {song.Name} 正在获取…";
        try
        {
            var loaded = await CloudMusicService.LoadAsync(song, dlg.Quality,
                msg => TblStatus.Text = "云端音乐：" + msg);
            LoadAudioFile(loaded.AudioPath);

            if (!string.IsNullOrWhiteSpace(loaded.Lrc))
            {
                var lrc = loaded.Lrc;
                if (!lrc.Contains("[ti:", StringComparison.OrdinalIgnoreCase))
                    lrc = $"[ti:{song.Name}]\n[ar:{song.Singer}]\n" + lrc;
                LoadLyrics(lrc);
            }

            _saveError = SaveProjectToCurrent();
            RefreshMediaPanel();
            RefreshTimeline();
            TblStatus.Text = $"已导入云端音乐：{song.Singer} - {song.Name}{loaded.Note}";
            Log.Info($"[Cloud] 已导入云端音乐：{song.Singer} - {song.Name} → {loaded.AudioPath}");
        }
        catch (Exception ex)
        {
            TblStatus.Text = "云端音乐导入失败：" + ex.Message;
            Log.Warn("[Cloud] 云端音乐导入失败：" + ex);
        }
    }
}
