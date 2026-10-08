using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using JizuraLive.Logging;
using Newtonsoft.Json;

namespace JizuraLive.App.I18n;

/// <summary>
/// UI 文案门面（i18n）。词库 = I18n/Locales/{lang}/{panel}.json（按面板一文件，嵌入资源，启动全量装载）。
/// XAML 静态文本走 {DynamicResource Loc.键}（换语言自动刷新）；code-behind 动态文本走 T/F，
/// 并在 LanguageChanged 时重刷（订阅范式对位 EditorState.StateChanged 的 Loaded/Unloaded）。
/// 缺键回退 zh-Hans，仍缺则原样返回键名并记 Warn（便于发现漏网文案）。
/// </summary>
public static class L
{
    /// <summary>资源键前缀：XAML 里 {DynamicResource Loc.TopBar.Open} ↔ 词库键 "TopBar.Open"。</summary>
    public const string Prefix = "Loc.";

    public const string DefaultLang = "zh-Hans";

    private static readonly Dictionary<string, Dictionary<string, string>> Packs = new();
    private static Dictionary<string, string> _current = new();
    private static Dictionary<string, string> _fallback = new();
    private static ResourceDictionary? _rd;

    /// <summary>当前语言（Settings.UiLang，词库目录名）。</summary>
    public static string CurrentLang { get; private set; } = DefaultLang;

    /// <summary>语言切换广播；面板在 Loaded/Unloaded 里订阅/退订，收到后重刷动态文本与下拉项。</summary>
    public static event Action? LanguageChanged;

    /// <summary>已装载的语言代码（由 Locales 子目录发现）。</summary>
    public static IReadOnlyCollection<string> AvailableLangs => Packs.Keys.ToArray();

    /// <summary>App 启动时调用一次（Settings.Load 之后、MainWindow 构造之前——窗口 XAML 的 DynamicResource 依赖它）。</summary>
    public static void Initialize(string? lang)
    {
        LoadAllPacks();
        SetLanguage(string.IsNullOrWhiteSpace(lang) ? DefaultLang : lang.Trim());
    }

    /// <summary>切换语言并广播；词库不存在的语言回退默认语言。</summary>
    public static void SetLanguage(string lang)
    {
        if (!Packs.ContainsKey(lang))
        {
            if (lang != DefaultLang)
                Log.Warn($"[i18n] 词库不存在：{lang}，回退 {DefaultLang}");
            lang = DefaultLang;
        }

        if (!Packs.TryGetValue(DefaultLang, out var fb))
            Packs[DefaultLang] = fb = new Dictionary<string, string>();
        _fallback = fb;
        _current = lang == DefaultLang ? fb : Packs[lang];
        CurrentLang = lang;

        ApplyToResources();
        LanguageChanged?.Invoke();
    }

    /// <summary>查文案；缺键回退默认语言，仍缺则返回键名本身并记 Warn。</summary>
    public static string T(string key)
    {
        if (_current.TryGetValue(key, out var v)) return v;
        if (_fallback.TryGetValue(key, out var f)) return f;
        Log.Warn("[i18n] 缺少文案键：" + key);
        return key;
    }

    /// <summary>查格式化文案（词库值里用 {0}/{1} 占位）。</summary>
    public static string F(string key, params object?[] args) => string.Format(T(key), args);

    /// <summary>词库只读视图（测试/工具用；未装载的语言返回空字典）。</summary>
    public static IReadOnlyDictionary<string, string> Pack(string lang) =>
        Packs.TryGetValue(lang, out var p) ? p : new Dictionary<string, string>();

    /// <summary>把当前语言灌进应用级资源字典。字典实例固定、换语言只换内容，DynamicResource 引用自动失效刷新；
    /// 默认语言的键一并灌入，缺译回退时 DynamicResource 也能解析到。</summary>
    private static void ApplyToResources()
    {
        if (Application.Current == null) return; // 单元测试无 WPF Application

        if (_rd == null)
        {
            _rd = new ResourceDictionary();
            Application.Current.Resources.MergedDictionaries.Insert(0, _rd);
        }

        _rd.BeginInit();
        try
        {
            _rd.Clear();
            foreach (var (k, v) in _current) _rd[Prefix + k] = v;
            if (!ReferenceEquals(_current, _fallback))
                foreach (var (k, v) in _fallback)
                    if (!_current.ContainsKey(k))
                        _rd[Prefix + k] = v;
        }
        finally
        {
            _rd.EndInit();
        }
    }

    private static void LoadAllPacks()
    {
        if (Packs.Count > 0) return;
        // 嵌入资源名：JizuraLive.src.App.I18n.Locales.{lang}.{panel}.json
        // 注意 MSBuild 会把目录名里的 '-' 转成 '_'（zh-Hans → zh_Hans），装载时换回连字符
        var asm = Assembly.GetExecutingAssembly();
        var rx = new Regex(@"\.I18n\.Locales\.([A-Za-z0-9_]+)\.[A-Za-z0-9_]+\.json$", RegexOptions.Compiled);
        foreach (var name in asm.GetManifestResourceNames())
        {
            var m = rx.Match(name);
            if (!m.Success) continue;
            using var stream = asm.GetManifestResourceStream(name);
            if (stream == null) continue;
            using var reader = new StreamReader(stream);
            var dict = JsonConvert.DeserializeObject<Dictionary<string, string>>(reader.ReadToEnd());
            if (dict == null) continue;
            var lang = m.Groups[1].Value.Replace('_', '-');
            if (!Packs.TryGetValue(lang, out var pack))
                Packs[lang] = pack = new Dictionary<string, string>();
            foreach (var (k, v) in dict) pack[k] = v;
        }

        if (!Packs.ContainsKey(DefaultLang))
            Log.Warn("[i18n] 未找到默认词库 zh-Hans（I18n/Locales 目录为空？）");
    }
}
