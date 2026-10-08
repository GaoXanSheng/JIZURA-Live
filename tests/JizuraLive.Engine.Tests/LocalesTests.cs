using System.IO;
using System.Text.RegularExpressions;
using JizuraLive.App.I18n;
using Xunit;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// i18n 词库与引用完整性（M5 守门测试）：
/// 1) zh-Hans 词库可装载、非空、值非空；
/// 2) 代码里 L.T/L.F 直呼键 + 键形字符串字面量（表格里存的键）全部存在于词库；
/// 3) XAML 的 {DynamicResource Loc.*} 引用全部存在；
/// 4) 缺键回退链：当前语言 → zh-Hans → 键名原样。
/// </summary>
public class LocalesTests
{
    private static readonly Lazy<string> RepoRoot = new(FindRepoRoot);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "JizuraLive.csproj"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("未找到仓库根（JizuraLive.csproj）");
    }

    /// <summary>扫描范围：src/App + src/Export（UI 与导出层的 L 使用方）。</summary>
    private static IEnumerable<string> ScanFiles()
    {
        foreach (var sub in new[] { "src/App", "src/Export" })
        {
            var root = Path.Combine(RepoRoot.Value, sub);
            if (!Directory.Exists(root)) continue;
            foreach (var ext in new[] { "*.cs", "*.xaml" })
                foreach (var f in Directory.EnumerateFiles(root, ext, SearchOption.AllDirectories))
                    if (!f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                        yield return f;
        }
    }

    private static IReadOnlyDictionary<string, string> LoadPack()
    {
        L.Initialize(null); // 测试进程无 WPF Application，L 内部已防护
        return L.Pack(L.DefaultLang);
    }

    [Fact]
    public void Pack_Loads_NonEmpty_With_NonBlank_Values()
    {
        var pack = LoadPack();
        Assert.NotEmpty(pack);
        foreach (var (key, value) in pack)
            Assert.True(!string.IsNullOrWhiteSpace(value), $"词库键值空白：{key}");
        Assert.Equal("界面语言", pack["Settings.UiLang"]); // 抽查一个已知键
    }

    [Fact]
    public void Referenced_Keys_All_Exist()
    {
        var pack = LoadPack();
        var missing = new SortedSet<string>();

        // 直接调用点：L.T("...") / L.F("...")
        var rxCall = new Regex("""(?:L\.T|L\.F)\(\s*"([^"]+)""", RegexOptions.Compiled);
        // 键形字符串字面量（表格/元组里存的键，如 { ("off", "Settings.KeyBg.Off") }）
        var rxKeyShape = new Regex("""([A-Z][A-Za-z0-9]*(?:\.[A-Za-z0-9_]+)+)""", RegexOptions.Compiled);
        // XAML：{DynamicResource Loc.X.Y}
        var rxDynamic = new Regex("""\{DynamicResource Loc\.([A-Za-z0-9_.]+)\}""", RegexOptions.Compiled);

        foreach (var file in ScanFiles())
        {
            var text = BlankOutNonKeyLines(File.ReadAllText(file));
            foreach (Match m in rxCall.Matches(text))
                Check(m.Groups[1].Value, file);
            foreach (Match m in rxDynamic.Matches(text))
                Check(m.Groups[1].Value, file);
            if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                foreach (var literal in CsStringLiterals(text))
                    foreach (Match m in rxKeyShape.Matches(literal))
                        Check(m.Groups[1].Value, file);
        }

        // 候选直接命中，或是词库键的动态前缀（如 "Mcp.Tool." + name + ".Desc"）都算通过
        void Check(string key, string file)
        {
            if (pack.ContainsKey(key)) return;
            if (pack.Keys.Any(k => k.StartsWith(key, StringComparison.Ordinal))) return;
            missing.Add(key + "  ← " + Path.GetFileName(file));
        }
    }

    /// <summary>日志行与不经 L 的 throw 行是豁免区（日志不走 i18n；内部异常消息非 UI 文案），
    /// 置空以免英文标识符（Log 标签、类型名）被误判成键。</summary>
    private static string BlankOutNonKeyLines(string text)
    {
        var rxLog = new Regex("""\bLog\.(Info|Warn|Error|Debug|Fatal)\s*\(""", RegexOptions.Compiled);
        var rxThrow = new Regex("""\bthrow\s+new\s+\w*Exception""", RegexOptions.Compiled);
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (rxLog.IsMatch(lines[i]) || (rxThrow.IsMatch(lines[i]) && !lines[i].Contains("L.T(")))
                lines[i] = new string(' ', lines[i].Length);
        }
        return string.Join("\n", lines);
    }

    /// <summary>轻量状态机：剥掉 // 与 /* */ 注释，收集普通、逐字（@"..."）与插值（$"..."）字符串的字面量；
    /// 插值串收集的是「洞里嵌套的普通字符串」（键所在处）而非整段模板。</summary>
    private static IEnumerable<string> CsStringLiterals(string text)
    {
        var literals = new List<string>();
        int i = 0, n = text.Length;

        void SkipNormalString(ref int i) // 进入时 i 指向开引号
        {
            i++;
            while (i < n && text[i] != '"')
            {
                if (text[i] == '\\' && i + 1 < n) i += 2;
                else if (text[i] == '\n') break;
                else i++;
            }
            i = Math.Min(i + 1, n);
        }

        void CollectNormalString(ref int i)
        {
            i++;
            var cur = new System.Text.StringBuilder();
            while (i < n && text[i] != '"')
            {
                if (text[i] == '\\' && i + 1 < n) { cur.Append(text[i]).Append(text[i + 1]); i += 2; }
                else if (text[i] == '\n') break;
                else cur.Append(text[i++]);
            }
            i = Math.Min(i + 1, n);
            literals.Add(cur.ToString());
        }

        void SkipInterpolatedString(ref int i, bool collect) // 进入时 i 指向 $" 的引号
        {
            i++;
            var cur = new System.Text.StringBuilder();
            while (i < n)
            {
                char c = text[i];
                if (c == '"') { i++; break; }
                if (c == '\\' && i + 1 < n) { if (collect) cur.Append(text[i]).Append(text[i + 1]); i += 2; continue; }
                if (c == '\n') break;
                if (c == '{')
                {
                    if (i + 1 < n && text[i + 1] == '{') { if (collect) cur.Append('{'); i += 2; continue; }
                    i++; // 进洞：洞里是代码，串/字符按普通规则跳过，嵌套字符串收集
                    int depth = 1;
                    while (i < n && depth > 0)
                    {
                        char d = text[i];
                        if (d == '{') depth++;
                        else if (d == '}') { depth--; i++; continue; }
                        else if (d == '"') { if (collect) CollectNormalString(ref i); else SkipNormalString(ref i); continue; }
                        else if (d == '\'')
                        {
                            i++;
                            while (i < n && text[i] != '\'') { if (text[i] == '\\' && i + 1 < n) i++; i++; }
                            i = Math.Min(i + 1, n);
                            continue;
                        }
                        i++;
                    }
                    if (collect) cur.Append('\u0000');
                    continue;
                }
                if (collect) cur.Append(c);
                i++;
            }
            if (collect) literals.Add(cur.ToString());
        }

        void CollectVerbatimString(ref int i) // 进入时 i 指向 @" 的引号
        {
            i++;
            var cur = new System.Text.StringBuilder();
            while (i < n)
            {
                if (text[i] == '"')
                {
                    if (i + 1 < n && text[i + 1] == '"') { cur.Append('"'); i += 2; }
                    else { i++; break; }
                }
                else cur.Append(text[i++]);
            }
            literals.Add(cur.ToString());
        }

        while (i < n)
        {
            char c = text[i];
            if (c == '/' && i + 1 < n && text[i + 1] == '/')
            {
                while (i < n && text[i] != '\n') i++;
            }
            else if (c == '/' && i + 1 < n && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < n && !(text[i] == '*' && text[i + 1] == '/')) i++;
                i = Math.Min(i + 2, n);
            }
            else if (c == '@' && i + 1 < n && text[i + 1] == '"')
            {
                CollectVerbatimString(ref i);
            }
            else if (c == '$' && i + 1 < n && text[i + 1] == '"')
            {
                SkipInterpolatedString(ref i, collect: true);
            }
            else if (c == '"')
            {
                CollectNormalString(ref i);
            }
            else i++;
        }
        return literals;
    }

    [Fact]
    public void Missing_Key_Falls_Back_To_Key_Name()
    {
        LoadPack();
        Assert.Equal("No.Such.Key.Anywhere", L.T("No.Such.Key.Anywhere"));
    }

    [Fact]
    public void Unknown_Language_Falls_Back_To_Default()
    {
        L.Initialize("xx-XX");
        Assert.Equal(L.DefaultLang, L.CurrentLang);
        Assert.Equal("界面语言", L.T("Settings.UiLang"));
        L.Initialize(null); // 还原默认，避免影响同进程其他测试
    }

    [Fact]
    public void LanguageChanged_Fires_On_SetLanguage()
    {
        L.Initialize(null);
        var fired = 0;
        L.LanguageChanged += () => fired++;
        L.SetLanguage(L.DefaultLang); // 同语言重设也广播（面板重刷语义）
        L.LanguageChanged -= () => fired++;
        Assert.Equal(1, fired);
    }
}
