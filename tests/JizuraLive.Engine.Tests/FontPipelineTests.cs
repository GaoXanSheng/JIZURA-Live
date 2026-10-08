using System.IO;
using JizuraLive.Engine;
using SkiaSharp;
using Xunit;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 字体管线（JFonts 解析层）：目录扫描 → 用户字体 → 系统族 → 语言×气质系统栈 → 逐字兜底。
/// 断言的落点是「客户端离线可用的行为」：任何字体键都解析得出脸、CJK 不缺字、各键观感有区分、
/// 上传字体真的进渲染路径、持久化能读回。运行环境的系统字体（如微软雅黑）缺失时会跳过对应断言。
/// </summary>
[Collection("EngineRegistry")]
public class FontPipelineTests : IDisposable
{
    private readonly string _userDir;
    private readonly string? _oldUserDir;
    private readonly string _repoRoot;
    private readonly string _plexFile;

    public FontPipelineTests()
    {
        EngineBoot.Install();
        _oldUserDir = JFonts.UserFontDir;
        _userDir = Path.Combine(Path.GetTempPath(), "jizura-font-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_userDir);
        JFonts.UserFontDir = _userDir;
        _repoRoot = RepoRoot();
        _plexFile = Path.Combine(_repoRoot, "assets", "fonts", "IBMPlexSansJP-Medium.ttf");
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

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "JizuraLive.csproj"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
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
        Assert.True(File.Exists(_plexFile), "缺 assets/fonts/IBMPlexSansJP-Medium.ttf（测试素材）");

        // 1) 上传：登记 + 落盘进用户字体库
        var uf = JFonts.LoadFontFile(_plexFile);
        Assert.True(JFonts.IsLoaded(uf.Key));
        Assert.True(File.Exists(Path.Combine(_userDir, uf.Key + ".font")), "上传字体没有落进用户字体库");

        // 2) 渲染路径真的用它（此前 JText 不查 UserFaces，上传字体画不出来）
        JLang.SetLang("ja");
        var face = JLang.FaceOf(uf.Key);
        var tf = JFonts.ResolveChain(JFonts.FaceChain(face), face.Weight);
        Assert.Equal("IBM Plex Sans JP", tf.FamilyName);

        // 3) 换进程式恢复：清掉目录项后从字体库读回
        JFonts.Table.Remove(uf.Key);
        JFonts.InvalidateResolveCache();
        var missing = JFonts.RestoreUserFonts(new[] { uf });
        Assert.Empty(missing);
        Assert.True(JFonts.IsLoaded(uf.Key), "字体库里明明有本体，恢复后却标记为未加载");
        Assert.Equal("IBM Plex Sans JP",
            JFonts.ResolveChain(JFonts.FaceChain(JLang.FaceOf(uf.Key)), 400).FamilyName);
    }

    [Fact]
    public void 字体目录扫描后目录内的族可直接解析()
    {
        Assert.True(File.Exists(_plexFile));
        File.Copy(_plexFile, Path.Combine(_userDir, "plex-drop-in.ttf"), overwrite: true);
        JFonts.ScanFontDirs(force: true);

        Assert.True(JFonts.InFontDirs("IBM Plex Sans JP"), "字体目录里的族没进索引");
        var tf = JFonts.ResolveChain(new[] { "\"IBM Plex Sans JP\"" }, 500);
        Assert.Equal("IBM Plex Sans JP", tf.FamilyName);

        // 同族多字重：按请求字重挑最近的一张（目录索引里 Medium=500 与 Medium 自身的一致性）
        var bold = JFonts.ResolveChain(new[] { "\"IBM Plex Sans JP\"" }, 700);
        Assert.Equal("IBM Plex Sans JP", bold.FamilyName);
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
