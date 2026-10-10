using System.IO;
using JizuraLive.Engine;
using SkiaSharp;
using Xunit;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 字体管线（JFonts 解析层）：目录扫描 → 用户字体 → 系统族 → 语言×气质系统栈 → 逐字兜底。
/// 断言的落点是「客户端离线可用的行为」：任何字体键都解析得出脸、CJK 不缺字、各键观感有区分、
/// 上传字体真的进渲染路径、持久化能读回。运行环境的系统字体（如微软雅黑）缺失时会跳过对应断言；
/// 上传/投放类用例的素材取自系统字体目录（引擎字体不随包）。
/// </summary>
[Collection("EngineRegistry")]
public class FontPipelineTests : IDisposable
{
    private readonly string _userDir;
    private readonly string? _oldUserDir;

    public FontPipelineTests()
    {
        EngineBoot.Install();
        _oldUserDir = JFonts.UserFontDir;
        _userDir = Path.Combine(Path.GetTempPath(), "jizura-font-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_userDir);
        JFonts.UserFontDir = _userDir;
    }

    public void Dispose()
    {
        JFonts.UserFontDir = _oldUserDir ?? JFonts.UserFontDir;
        JFonts.ScanFontDirs(force: true);
        JFonts.InvalidateResolveCache();
        try
        {
            Directory.Delete(_userDir, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    /// <summary>测试素材：系统字体文件（引擎字体不随包，测试不再依赖仓库内字体）。
    /// 候选都取不到（精简系统）时，用它的用例直接跳过。</summary>
    private static string? FontFixture()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
        foreach (var name in new[] { "consola.ttf", "segoeui.ttf", "msyh.ttc" })
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>素材文件真正的族名：断言不写死具体字体，换候选也能跑。</summary>
    private static string FixtureFamily(string file)
    {
        using var tf = SKTypeface.FromFile(file);
        Assert.NotNull(tf);
        return tf!.FamilyName;
    }

    /// <summary>是否跑在有微软雅黑（保底脸）的机器上：CI/精简系统上跳过依赖它的断言。</summary>
    private static bool HasYaHei => JFonts.IsFamilyInstalled("Microsoft YaHei") || JFonts.IsFamilyInstalled("Microsoft YaHei UI");

    [Fact]
    public void 每个字体键都解析得出脸且不是默认体()
    {
        JLang.SetLang("ja");
        foreach (var key in JFonts.Table.Keys)
        {
            var face = JLang.FaceOf(key);
            var tf = JFonts.ResolveChain(JFonts.FaceChain(face), face.Weight);
            Assert.NotNull(tf);
            Assert.False(string.IsNullOrEmpty(tf.FamilyName), $"{key}: 解析出的脸没有族名（落到默认体了）");
        }
    }

    [Fact]
    public void 日语歌词不缺字_逐字兜底覆盖汉字与假名()
    {
        JLang.SetLang("ja");
        foreach (var ch in new[] { "漢", "あ", "ア", "々", "…" })
        {
            var font = JText.FaceFontFor("gothic_bold", ch, 64);
            var cp = char.ConvertToUtf32(ch, 0);
            Assert.True(font.Typeface.ContainsGlyph(cp), $"gothic_bold 画「{ch}」缺字（会出 .notdef 方块）");
        }
    }

    [Fact]
    public void 等宽键画汉字时逐字回退到有字的脸()
    {
        JLang.SetLang("ja");
        var latin = JText.FaceFontFor("mono", "M", 64);
        Assert.True(latin.Typeface.ContainsGlyph('M'));
        var cjk = JText.FaceFontFor("mono", "漢", 64);
        Assert.True(cjk.Typeface.ContainsGlyph('漢'), "mono 键下的汉字没有回退脸（会出方块）");
    }

    [Fact]
    public void 各气质键的脸互有区分_不是全指同一张()
    {
        JLang.SetLang("ja");
        var families = new HashSet<string>();
        foreach (var key in new[] { "gothic_bold", "mincho", "round", "dot", "mono" })
        {
            var f = JLang.FaceOf(key);
            families.Add(JFonts.ResolveChain(JFonts.FaceChain(f), f.Weight).FamilyName);
        }
        // 离线机器至少应有黑体/明朝/等宽三种不同族（同一张脸说明字体键成了摆设）
        Assert.True(families.Count >= 2, "全部字体键都解析到同一张脸：" + string.Join("、", families));
    }

    [Fact]
    public void 保底脸是系统微软雅黑()
    {
        if (!HasYaHei) return; // 精简系统（无雅黑）跳过
        var tf = JFonts.ResolveChain(new[] { "绝不存在的字体名ZZZ", "sans-serif" }, 400);
        Assert.Contains("YaHei", tf.FamilyName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 上传字体进入渲染路径且能从字体库恢复()
    {
        var fixture = FontFixture();
        if (fixture == null) return; // 精简系统没有素材：跳过
        var family = FixtureFamily(fixture);

        // 1) 上传：登记 + 落盘进用户字体库
        var uf = JFonts.LoadFontFile(fixture);
        Assert.True(JFonts.IsLoaded(uf.Key));
        Assert.True(File.Exists(Path.Combine(_userDir, uf.Key + ".font")), "上传字体没有落进用户字体库");

        // 2) 渲染路径真的用它（此前 JText 不查 UserFaces，上传字体画不出来）。
        // 上传字体按合成族名（UF_*）登记，系统里必然没有，所以解析出素材族名就说明走的是 UserFaces。
        JLang.SetLang("ja");
        var face = JLang.FaceOf(uf.Key);
        var tf = JFonts.ResolveChain(JFonts.FaceChain(face), face.Weight);
        Assert.Equal(family, tf.FamilyName);

        // 3) 换进程式恢复：清掉目录项后从字体库读回
        JFonts.Table.Remove(uf.Key);
        JFonts.InvalidateResolveCache();
        var missing = JFonts.RestoreUserFonts(new[] { uf });
        Assert.Empty(missing);
        Assert.True(JFonts.IsLoaded(uf.Key), "字体库里明明有本体，恢复后却标记为未加载");
        Assert.Equal(family,
            JFonts.ResolveChain(JFonts.FaceChain(JLang.FaceOf(uf.Key)), 400).FamilyName);
    }

    [Fact]
    public void 字体目录扫描后目录内的族可直接解析()
    {
        var fixture = FontFixture();
        if (fixture == null) return; // 精简系统没有素材：跳过
        var family = FixtureFamily(fixture);
        File.Copy(fixture, Path.Combine(_userDir, "fixture-drop-in" + Path.GetExtension(fixture)), overwrite: true);
        JFonts.ScanFontDirs(force: true);

        Assert.True(JFonts.InFontDirs(family), "字体目录里的族没进索引");
        var tf = JFonts.ResolveChain(new[] { "\"" + family + "\"" }, 400);
        Assert.Equal(family, tf.FamilyName);

        // 同一个族换字重也能解析（单文件族按请求字重就近挑，回退不落空）
        var bold = JFonts.ResolveChain(new[] { "\"" + family + "\"" }, 700);
        Assert.Equal(family, bold.FamilyName);
    }

    [Fact]
    public void 缺本体的用户字体被标为缺失且渲染回退不崩()
    {
        var ghost = new JFonts.UserFontRef("user_UF_ghostfont", "幽灵字体", "UF_ghostfont", 400);
        var missing = JFonts.RestoreUserFonts(new[] { ghost });
        Assert.Contains("幽灵字体", missing);
        Assert.Equal(new[] { "幽灵字体" }, JFonts.MissingUserFonts(new[] { ghost.Key }));

        // 渲染侧：缺本体 → 回退到系统栈，不是 Default、也不是空
        var tf = JFonts.ResolveChain(JFonts.FaceChain(JFonts.Table[ghost.Key]), 400);
        Assert.NotNull(tf);
        Assert.False(string.IsNullOrEmpty(tf.FamilyName));
    }
}
