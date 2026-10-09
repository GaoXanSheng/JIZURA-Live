using System.IO;
using System.Xml.Linq;
using Xunit;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 界面动效的源码级红线：XAML 模板里 Storyboard.TargetName 写的名字，必须在同一模板内存在
/// 同名 x:Name。
///
/// 为什么必须静态查：WPF 的 TargetName 是按模板命名域在**运行时**解析的 —— 名字写错不会编译
/// 报错，而是等触发器第一次命中时才抛（本仓界面动效几乎全挂在 Trigger/DataTrigger 的
/// EnterActions/ExitActions 上，写错就是「鼠标一悬停就崩」）。名字还可能指向 Freezable
/// （TranslateTransform / ScaleTransform / SolidColorBrush），它们同样靠 x:Name 注册进模板
/// 命名域，肉眼更难发现写错。
///
/// 与 ComboTemplateTests 一样只做源码级检查、不建 WPF Application：本测试进程里
/// Application.Current 一旦非 null，EditorState.NotifyAsync / ReplanAsync 会往没人泵的
/// Dispatcher 上排队，McpTests 会集体超时。
/// </summary>
public class AnimTemplateTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] TemplateTags = ["ControlTemplate", "DataTemplate", "ItemsPanelTemplate"];

    [Fact]
    public void 模板里的动画目标名都能解析到同名元素()
    {
        var root = AppDir();
        var bad = new List<string>();
        var total = 0;
        var files = 0;

        foreach (var file in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories))
        {
            files++;
            var doc = XDocument.Load(file);
            foreach (var tmpl in doc.Descendants().Where(e => TemplateTags.Contains(e.Name.LocalName)))
            {
                var names = tmpl.DescendantsAndSelf()
                    .Attributes(X + "Name")
                    .Select(a => a.Value)
                    .ToHashSet(StringComparer.Ordinal);
                if (names.Count == 0) continue;

                foreach (var st in tmpl.Descendants().Where(e => e.Name.LocalName == "Storyboard"))
                foreach (var anim in st.Elements())
                {
                    var tn = anim.Attribute("Storyboard.TargetName")?.Value;
                    if (tn == null) continue;
                    total++;
                    if (!names.Contains(tn))
                        bad.Add($"{Path.GetFileName(file)}：TargetName=\"{tn}\" 在 {tmpl.Name.LocalName} 里找不到同名 x:Name"
                                + $"（{anim.Name.LocalName}）");
                }
            }
        }

        Assert.True(files > 0, $"没扫到任何 XAML（{root}）");
        Assert.True(total > 0, "一处 Storyboard.TargetName 都没扫到 —— 界面动效是不是被整体撤掉了？");
        Assert.True(bad.Count == 0, "动画目标名解析不到同名元素（触发器命中时会抛异常）：\n" + string.Join("\n", bad));
    }

    /// <summary>仓库根下的 src/App（测试从 bin 启动，逐级上溯找）。</summary>
    private static string AppDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var path = Path.Combine(dir.FullName, "src", "App");
            if (Directory.Exists(path)) return path;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("未找到 src/App");
    }
}
