using System.Windows.Controls;
using JizuraLive.App.PvStage.Panels;

namespace JizuraLive.App.PvStage;

public partial class PvStageWindow
{
    private ScrollViewer PanelMedia => Left.PanelMedia;
    private ScrollViewer PanelStyle => Left.PanelStyle;
    private ScrollViewer PanelTech => Left.PanelTech;
    private ScrollViewer PanelDirector => Left.PanelDirector;
    private ScrollViewer PanelOutput => Left.PanelOutput;
    private ScrollViewer PanelSettings => Left.PanelSettings;

    private RadioButton NavMedia => Left.NavMedia;
    private RadioButton NavStyle => Left.NavStyle;
    private RadioButton NavTech => Left.NavTech;
    private RadioButton NavDirector => Left.NavDirector;
    private RadioButton NavOutput => Left.NavOutput;
    private RadioButton NavSettings => Left.NavSettings;

    private Wpf.Ui.Controls.Button BtnAddLayer => Left.BtnAddLayer;
    private Wpf.Ui.Controls.Button BtnGenerateAuto => Left.BtnGenerateAuto;
    private Wpf.Ui.Controls.Button BtnRegenerateAuto => Left.BtnRegenerateAuto;
    private Wpf.Ui.Controls.Button BtnExport => Left.BtnExport;
    private Wpf.Ui.Controls.Button BtnExportBrowse => Left.BtnExportBrowse;
    private Wpf.Ui.Controls.Button BtnApplyCanvas => Left.BtnApplyCanvas;
    private Wpf.Ui.Controls.Button BtnReseedPreset => Left.BtnReseedPreset;

    private TextBlock TblAudioName => Left.TblAudioName;
    private TextBlock TblAudioMeta => Left.TblAudioMeta;
    private TextBlock TblLyricInfo => Left.TblLyricInfo;
    private TextBlock TblLayerCount => Left.TblLayerCount;
    private TextBlock TblThemeName => Left.TblThemeName;
    private TextBlock TblAutoReport => Left.TblAutoReport;
    private TextBlock TblAutoHint => Left.TblAutoHint;
    private TextBlock TblAutoIntensity => Left.TblAutoIntensity;
    private TextBlock TblTechCount => Left.TblTechCount;
    private TextBlock TblExportState => Left.TblExportState;
    private TextBlock TblCanvasState => Left.TblCanvasState;
    private TextBlock TblBroadcastState => Left.TblBroadcastState;
    private TextBlock TblGpuName => Left.TblGpuName;
    private TextBlock TblPvRender => Left.TblPvRender;
    private TextBlock TblLegacyComposite => Left.TblLegacyComposite;
    private TextBlock TblVtsState => Left.TblVtsState;
    private Wpf.Ui.Controls.Button BtnVtsReconnect => Left.BtnVtsReconnect;
    private Wpf.Ui.Controls.Button BtnVtsAuth => Left.BtnVtsAuth;

    private ComboBox CmbKoma => Left.CmbKoma;
    private ComboBox CmbHud => Left.CmbHud;
    private ComboBox CmbKeyBg => Left.CmbKeyBg;
    private ComboBox CmbFxAdd => Left.CmbFxAdd;
    private StackPanel FxStackHost => Left.FxStackHost;
    private ComboBox CmbAutoStyle => Left.CmbAutoStyle;
    private ComboBox CmbCanvasPreset => Left.CmbCanvasPreset;
    private ComboBox CmbPvRender => Left.CmbPvRender;
    private ComboBox CmbCenterZone => Left.CmbCenterZone;
    private ComboBox CmbSubContent => Left.CmbSubContent;
    private ComboBox CmbSubPos => Left.CmbSubPos;

    private Wpf.Ui.Controls.ToggleSwitch TglStageCamera => Left.TglStageCamera;
    private Wpf.Ui.Controls.ToggleSwitch TglStageTransitions => Left.TglStageTransitions;
    private Wpf.Ui.Controls.ToggleSwitch TglBroadcast => Left.TglBroadcast;
    private Wpf.Ui.Controls.ToggleSwitch TglSpoutTransparent => Left.TglSpoutTransparent;
    private Wpf.Ui.Controls.ToggleSwitch TglVSync => Left.TglVSync;
    private Wpf.Ui.Controls.ToggleSwitch TglLegacyComposite => Left.TglLegacyComposite;
    private Wpf.Ui.Controls.ToggleSwitch TglPreviewSubtitles => Left.TglPreviewSubtitles;
    private Wpf.Ui.Controls.ToggleSwitch TglCenterFree => Left.TglCenterFree;

    private Slider SldAutoIntensity => Left.SldAutoIntensity;
    private Slider SldCenterGap => Left.SldCenterGap;

    private TextBlock TblCenterGap => Left.TblCenterGap;

    private TextBox TxtPresetSeed => Left.TxtPresetSeed;
    private TextBox TxtTechFilter => Left.TxtTechFilter;
    private Wpf.Ui.Controls.TextBox TxtExportPath => Left.TxtExportPath;
    private Wpf.Ui.Controls.TextBox TxtExportSeconds => Left.TxtExportSeconds;
    private Wpf.Ui.Controls.TextBox TxtExportFps => Left.TxtExportFps;
    private Wpf.Ui.Controls.TextBox TxtCanvasW => Left.TxtCanvasW;
    private Wpf.Ui.Controls.TextBox TxtCanvasH => Left.TxtCanvasH;
    private Wpf.Ui.Controls.TextBox TxtCanvasFps => Left.TxtCanvasFps;

    private CheckBox ChkIncludeAudio => Left.ChkIncludeAudio;
    private CheckBox ChkBurnSubtitles => Left.ChkBurnSubtitles;
    private CheckBox ChkVtsDrive => Left.ChkVtsDrive;

    private Wpf.Ui.Controls.ProgressRing PrgExport => Left.PrgExport;

    private StackPanel LayersHost => Left.LayersHost;
    private WrapPanel ThemeGrid => Left.ThemeGrid;
    private StackPanel FxSliderHost => Left.FxSliderHost;
    private StackPanel FxToggleHost => Left.FxToggleHost;
    private StackPanel StyleExtraHost => Left.StyleExtraHost;
    private StackPanel TechPackHost => Left.TechPackHost;
    private StackPanel TechHost => Left.TechHost;
}
