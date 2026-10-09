using System.Globalization;
using System.IO;
using System.Xml.Linq;
using Xunit;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 下拉框（ComboBox）模板的两条几何红线——对应用户报的「布局挤压：行内手法下拉末字被裁」。
/// 只做源码级检查，不建 WPF Application：本测试进程里 Application.Current 一旦非 null，
/// EditorState.NotifyAsync / ReplanAsync 会往没人泵的 Dispatcher 上排队，McpTests 全部超时
///（实测整套从 27s 涨到 18min），所以这里断言 XAML 结构而不是跑真实量测。
/// 1) 选中项文字必须由模板里的局部 TextBlock 样式接管：Theme 的隐式 TextBlock 样式（13px）会盖住
///    ContentPresenter 生成文本的继承字号，而 ComboBox.MeasureOverride 按自身 FontSize 量 ——
///    量 10px 画 13px，框比字窄，末字被压掉。
/// 2) 右内边距必须容得下右侧箭头（7.2 宽 + 10 右边距 ≈ 17.2px），否则箭头压在末字上。
/// </summary>
public class ComboTemplateTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XElement Theme()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var path = Path.Combine(dir.FullName, "src", "App", "Theme.xaml");
            if (File.Exists(path)) return XDocument.Load(path).Root!;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("未找到 src/App/Theme.xaml");
    }

    private static XElement ComboTemplate() => Theme()
        .Descendants()
        .Where(e => e.Name.LocalName == "ControlTemplate")
        .First(e => (string?)e.Attribute("TargetType") == "ComboBox"
                    && e.Descendants().Any(d => (string?)d.Attribute(X + "Name") == "cp"));

    [Fact]
    public void 选中项文字有局部样式接管隐式字号()
    {
        var cp = ComboTemplate().Descendants().First(e => (string?)e.Attribute(X + "Name") == "cp");
        var style = cp.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "ContentPresenter.Resources")?
            .Elements()
            .FirstOrDefault(e => e.Name.LocalName == "Style"
                                 && (string?)e.Attribute("TargetType") == "TextBlock");
        Assert.True(style != null, "ComboBox 模板的选中项 ContentPresenter 缺少局部 TextBlock 样式："
                                   + "隐式 TextBlock 样式（13px）会盖住控件字号，末字被裁");
    }

    [Theory]
    [InlineData("SelXs")]
    [InlineData("SelSm")]
    [InlineData("SelBase")]
    public void 右内边距容得下箭头(string styleKey)
    {
        var style = Theme().Elements()
            .FirstOrDefault(e => e.Name.LocalName == "Style" && (string?)e.Attribute(X + "Key") == styleKey);
        Assert.True(style != null, $"找不到样式 {styleKey}");
        var pad = style.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "Setter" && (string?)e.Attribute("Property") == "Padding")?
            .Attribute("Value")?.Value;
        if (pad == null) return; // 用基类默认值（SelBase 的 9,0,22,0）也算合格
        // Thickness 的书写顺序是 左,上,右,下 —— 右边距是第 3 个数
        var parts = pad.Split(',');
        var right = double.Parse(parts[2].Trim(), CultureInfo.InvariantCulture);
        Assert.True(right >= 18, $"{styleKey} 右内边距 {right} 太窄（箭头区约 17.2px），会压在末字上");
    }
}
