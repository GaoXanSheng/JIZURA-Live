using System.Linq;
using System.Windows;
using System.Windows.Controls;
using JizuraLive.Vts;

namespace JizuraLive.App.PvStage;

/// <summary>
/// 素材页 VTS 卡的演出 cue 部分：提前量、四个角色的映射下拉与试触发，
/// 以及播出时按音频时钟推进调度器的宿主。cue 本身由导演模块生成，这里只负责配映射与发令。
/// </summary>
public partial class PvStageWindow
{
    /// <summary>映射下拉的一项：不用 / 一个 VTS 热键 / 一个表情文件。</summary>
    private sealed record CueTargetItem(string Text, CueTargetKind Kind, string Value)
    {
        public override string ToString() => Text;
    }

    private enum CueTargetKind { None, Hotkey, Expression }

    private sealed class CueRoleRow
    {
        public required string Role { get; init; }
        public required ComboBox Combo { get; init; }
        public required Wpf.Ui.Controls.Button Trial { get; init; }
    }

    private readonly List<CueRoleRow> _cueRows = new();

    // 上次建下拉项时的热键/表情签名（连上 VTS、列表到手才重建）；换工程时作废重来
    private string? _cueListKey;
    private bool _syncingCueRows;   // 程序化回填下拉：那次 SelectionChanged 不是用户选择
    private bool _syncingCueLead;
    private bool _cueAligned;       // 这一段连着的时间基线建过没有
    private readonly HashSet<string> _trialExpressions = new(StringComparer.Ordinal);

    // sink 隔一层转接到会话上，因为 VTS 会话会被整体换掉（开关一次就 new 一个新实例）
    private readonly IVtsCueSink _cueSink;

    /// <summary>没开会话时全部静默丢弃（Tick 那一侧本来也不会调）。</summary>
    private sealed class VtsCueSinkRouter(PvStageWindow window) : IVtsCueSink
    {
        public void TriggerHotkey(string nameOrId) => window._vts?.TriggerHotkey(nameOrId);

        public void SetExpression(string expressionFile, bool active, double fadeSec)
            => window._vts?.SetExpression(expressionFile, active, fadeSec);

        public void MoveModelRelative(double x, double y, double rotation, double size, double seconds)
            => window._vts?.MoveModelRelative(x, y, rotation, size, seconds);

        public void InjectAdd(IReadOnlyDictionary<string, double> values) => window._vts?.InjectAdd(values);
    }

    // ---------------- 调度器宿主 ----------------

    /// <summary>装上工程里的 cue 轨（换工程 / 改映射 / 改提前量都走这里）。</summary>
    private void ReloadCueTrack()
    {
        _cues.Load(_project.VtsCues);
        _cueAligned = false;
        AlignCueBaseline();
    }

    /// <summary>
    /// 建立这一段的时间基线，只做一次。
    /// 没有它：连上 VTS（或重装轨）时播放头可能已在半途，而调度器第一拍默认从游标 0 开始发，
    /// 整首歌已过去的 cue 会一口气喷到直播画面上。先落一个早于播放头的时间，随后真正那一拍就被
    /// 跳转判定吃下（跳转不补发）。播放头还在开头时不预置——那时照常对齐，t≈0 的 cue 才不会丢。
    /// </summary>
    private void AlignCueBaseline()
    {
        if (_cueAligned) return;
        if (_vts is not { State: VtsState.Ready }) return; // 没连上：等连上那一次再对齐
        if (_audio.Time > 0.5) _cues.Tick(-1.0, true);
        _cueAligned = true;
    }

    /// <summary>连着并鉴权才推进 cue（33ms 播放头定时器）。拖进度条由调度器自己的跳转判定处理。</summary>
    private void TickVtsCues()
    {
        if (_vts is not { State: VtsState.Ready })
        {
            // 会话关掉或掉线：下次连着重新对齐，掉线期间那段不补发
            _cueAligned = false;
            return;
        }

        AlignCueBaseline();
        _cues.Tick(_audio.Time, _audio.IsPlaying);
    }

    // ---------------- 面板 ----------------

    /// <summary>四个角色的映射行（控件建在代码里，容器是 xaml 的 VtsCueRoles）。</summary>
    private void BuildVtsCuePanel()
    {
        VtsCueRoles.Children.Clear();
        _cueRows.Clear();

        foreach (var (role, label) in VtsRoles.All)
        {
            var row = new Grid { Margin = new Thickness(0, 5, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var lab = ThemeText(label, TextSecondaryKey, 12);
            lab.Margin = new Thickness(0, 0, 6, 0);
            Grid.SetColumn(lab, 0);
            row.Children.Add(lab);

            var combo = new ComboBox { Margin = new Thickness(0, 0, 6, 0) };
            combo.Tag = role;
            combo.SelectionChanged += CueRole_SelectionChanged;
            Grid.SetColumn(combo, 1);
            row.Children.Add(combo);

            var trial = new Wpf.Ui.Controls.Button
            {
                Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent,
                Content = "试一下",
                FontSize = 11,
                Padding = new Thickness(6, 1, 6, 1),
                ToolTip = "立刻触发这个映射（不等播放）",
            };
            trial.Click += (_, _) => TrialCueRole(role);
            Grid.SetColumn(trial, 2);
            row.Children.Add(trial);

            VtsCueRoles.Children.Add(row);
            _cueRows.Add(new CueRoleRow { Role = role, Combo = combo, Trial = trial });
        }

        SyncVtsCuePanel();
    }

    /// <summary>把 cue 面板对齐到当前工程：提前量、角色映射下拉，并重装调度器上的轨。</summary>
    private void SyncVtsCuePanel()
    {
        SyncCueLeadSlider();
        RefreshVtsCuePickers(force: true);
        ReloadCueTrack();
    }

    /// <summary>热键/表情列表到手（连上 VTS）才重建下拉项；签名没变就什么都不做。</summary>
    private void RefreshVtsCuePickers(bool force = false)
    {
        var vts = _vts;
        var key = vts is null
            ? ""
            : string.Join('\n', vts.Hotkeys.Select(h => h.Name)) + "\u0001" + string.Join('\n', vts.Expressions);
        if (!force && string.Equals(key, _cueListKey, StringComparison.Ordinal)) return;
        _cueListKey = key;

        foreach (var row in _cueRows)
        {
            var selected = _project.VtsCues.Roles.TryGetValue(row.Role, out var v) ? v : "";
            FillCueCombo(row.Combo, selected ?? "");
            row.Trial.IsEnabled = vts is not null;
        }
    }

    /// <summary>填一个角色的下拉：不用 / 热键名 / 表情文件，并选中工程里存的那个映射。</summary>
    private void FillCueCombo(ComboBox combo, string selected)
    {
        var vts = _vts;
        var items = new List<CueTargetItem> { new("（不使用）", CueTargetKind.None, "") };

        if (vts is not null)
        {
            foreach (var hk in vts.Hotkeys) items.Add(new(hk.Name, CueTargetKind.Hotkey, hk.Name));
            foreach (var file in vts.Expressions) items.Add(new($"表情：{file}", CueTargetKind.Expression, file));
        }

        // 存的映射不在当前列表里（VTS 没连 / 换过模型）：原样留一项，别让人以为映射没了
        var stored = !string.IsNullOrEmpty(selected);
        if (stored && items.All(i => i.Value != selected))
            items.Add(new($"（{selected}）", GuessKind(selected), selected));

        _syncingCueRows = true;
        try
        {
            combo.Items.Clear();
            foreach (var item in items) combo.Items.Add(item);

            var index = 0;
            if (stored)
                for (var i = 1; i < items.Count; i++)
                    if (items[i].Value == selected)
                    {
                        index = i;
                        break;
                    }

            combo.SelectedIndex = index;
        }
        finally { _syncingCueRows = false; }
    }

    private void CueRole_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 程序化回填（建项、选中工程里存的映射）不是用户操作，不能当输入写回去
        if (_syncingCueRows) return;
        if (sender is not ComboBox { SelectedItem: CueTargetItem item, Tag: string role }) return;

        var roles = _project.VtsCues.Roles;
        roles.TryGetValue(role, out var prev);
        var next = item.Kind == CueTargetKind.None ? null : item.Value;

        if (next is null && prev is null) return;   // 本来就是"不使用"
        if (next is not null && prev == next) return; // 没变

        if (next is null) roles.Remove(role);
        else roles[role] = next;

        // role:xxx 的解析结果变了，必须重装轨才生效（游标复位由 AlignCueBaseline 兜住）
        ReloadCueTrack();
        PushUndo();
        _saveError = SaveProjectNow();
        ShowStatus($"{CueRoleLabel(role)}映射：{item.Text}");
    }

    /// <summary>「试一下」：立刻通过 sink 触发该角色当前映射，不等播放。</summary>
    private void TrialCueRole(string role)
    {
        var vts = _vts;
        if (vts is null)
        {
            ShowStatus("VTS 未连接，先把上面的连接打开");
            return;
        }

        if (!_project.VtsCues.Roles.TryGetValue(role, out var target) || string.IsNullOrEmpty(target))
        {
            ShowStatus($"{CueRoleLabel(role)}没有映射，先在下拉里选一个");
            return;
        }

        if (GuessKind(target) == CueTargetKind.Expression)
        {
            // 连按是开关：不会把表情留在模型上（直播画面看得见）
            var on = !_trialExpressions.Remove(target);
            if (on) _trialExpressions.Add(target);
            _cueSink.SetExpression(target, on, 0.4);
            ShowStatus($"试一下 {CueRoleLabel(role)}：表情 {target}（{(on ? "开" : "关")}）");
        }
        else
        {
            _cueSink.TriggerHotkey(target);
            ShowStatus($"试一下 {CueRoleLabel(role)}：热键 {target}");
        }
    }

    // ---------------- 提前量 ----------------

    private void SldCueLead_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var ms = Math.Round(Math.Clamp(e.NewValue, SldCueLead.Minimum, SldCueLead.Maximum));
        if (TblCueLead is not null) TblCueLead.Text = $"{ms:0} ms";

        // InitializeComponent 里就会先触发一次（那时工程还没装载）：那次不是用户输入
        if (_project is null || _syncingCueLead) return;

        _project.VtsCues.LeadMs = ms;
        ReloadCueTrack(); // 提前量是装载时读进调度器的：改了必须重装才生效

        // 拖动中只改内存：存盘是整份工程 JSON 的序列化 + 原子写，一次拖动能触发上百次
        if (SldCueLead.IsMouseCaptureWithin) return;
        PushUndo();
        _saveError = SaveProjectNow();
    }

    /// <summary>把工程里的提前量回填到滑杆与数值（换工程 / 撤销后调）。</summary>
    private void SyncCueLeadSlider()
    {
        var lead = _project.VtsCues.LeadMs;
        var ms = double.IsFinite(lead) ? Math.Clamp(lead, SldCueLead.Minimum, SldCueLead.Maximum) : 80;

        _syncingCueLead = true;
        try
        {
            SldCueLead.Value = ms;
            TblCueLead.Text = $"{ms:0} ms";
        }
        finally { _syncingCueLead = false; }
    }

    // ---------------- 小工具 ----------------

    private static CueTargetKind GuessKind(string target) =>
        target.EndsWith(".exp3.json", StringComparison.OrdinalIgnoreCase)
            ? CueTargetKind.Expression
            : CueTargetKind.Hotkey;

    private static string CueRoleLabel(string role)
    {
        foreach (var (r, label) in VtsRoles.All)
            if (r == role) return label;
        return role;
    }
}
