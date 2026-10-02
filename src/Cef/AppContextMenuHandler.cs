using CefSharp;
using CefSharp.Handler;

namespace JizuraLive;

/// <summary>
///     主窗口自定义右键菜单：CEF 原生渲染（暗色、与系统一致）。
///     新版 CEF 的 RunContextMenu 返回 true 已无法阻止原生菜单（返回值被忽略，
///     原生 UI 自己接管渲染），唯一正路是 OnBeforeContextMenu 里改写菜单模型：
///     Clear 后塞入自定义条目，CEF 原生渲染；点击回调 OnContextMenuCommand 执行。
///     编辑项按 ContextMenuEditState 灰显。仅挂主窗口 Browser（镜像浏览器不可见）。
/// </summary>
public sealed class AppContextMenuHandler : ContextMenuHandler
{
    public static AppContextMenuHandler Instance { get; } = new();

    // CEF 约定 26500+ 为用户自定义命令区（MENU_ID_USER_FIRST）
    private static readonly CefMenuCommand CmdUndo = (CefMenuCommand)26501;
    private static readonly CefMenuCommand CmdRedo = (CefMenuCommand)26502;
    private static readonly CefMenuCommand CmdCut = (CefMenuCommand)26503;
    private static readonly CefMenuCommand CmdCopy = (CefMenuCommand)26504;
    private static readonly CefMenuCommand CmdPaste = (CefMenuCommand)26505;
    private static readonly CefMenuCommand CmdSelectAll = (CefMenuCommand)26506;

    private AppContextMenuHandler()
    {
    }

    protected override void OnBeforeContextMenu(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
        IContextMenuParams parameters, IMenuModel model)
    {
        var s = parameters.EditStateFlags;
        model.Clear();
        if (parameters.IsEditable)
        {
            Add(model, CmdUndo, "撤销", s.HasFlag(ContextMenuEditState.CanUndo));
            Add(model, CmdRedo, "重做", s.HasFlag(ContextMenuEditState.CanRedo));
            model.AddSeparator();
        }
        Add(model, CmdCut, "剪切", s.HasFlag(ContextMenuEditState.CanCut));
        Add(model, CmdCopy, "复制", s.HasFlag(ContextMenuEditState.CanCopy));
        Add(model, CmdPaste, "粘贴", s.HasFlag(ContextMenuEditState.CanPaste));
        Add(model, CmdSelectAll, "全选", s.HasFlag(ContextMenuEditState.CanSelectAll));
    }

    protected override bool OnContextMenuCommand(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
        IContextMenuParams parameters, CefMenuCommand commandId, CefEventFlags eventFlags)
    {
        switch (commandId)
        {
            case var _ when commandId == CmdUndo: frame.Undo(); return true;
            case var _ when commandId == CmdRedo: frame.Redo(); return true;
            case var _ when commandId == CmdCut: frame.Cut(); return true;
            case var _ when commandId == CmdCopy: frame.Copy(); return true;
            case var _ when commandId == CmdPaste: frame.Paste(); return true;
            case var _ when commandId == CmdSelectAll: frame.SelectAll(); return true;
            default:
                return false;
        }
    }

    private static void Add(IMenuModel model, CefMenuCommand id, string label, bool enabled)
    {
        model.AddItem(id, label);
        if (!enabled) model.SetEnabled(id, false);
    }
}
