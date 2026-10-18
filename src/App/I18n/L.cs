using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using JizuraLive.Logging;
using Newtonsoft.Json;

namespace JizuraLive.App.I18n;

public static class L
{
    public const string Prefix = "Loc.";

    public const string DefaultLang = "zh-Hans";

    private static readonly object Gate = new();
    private static Dictionary<string, Dictionary<string, string>> Packs = new();
    private static volatile bool _loaded;
    private static Dictionary<string, string> _current = new();
    private static Dictionary<string, string> _fallback = new();
    private static ResourceDictionary? _rd;

    public static string CurrentLang { get; private set; } = DefaultLang;

    public static event Action? LanguageChanged;

    public static void Initialize(string? lang)
    {
        EnsureLoaded();
        SetLanguage(string.IsNullOrWhiteSpace(lang) ? DefaultLang : lang.Trim());
    }

    public static void SetLanguage(string lang)
    {
        EnsureLoaded();
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

    public static string T(string key)
    {
        EnsureLoaded();
        if (_current.TryGetValue(key, out var v)) return v;
        if (_fallback.TryGetValue(key, out var f)) return f;
        Log.Warn("[i18n] 缺少文案键：" + key);
        return key;
    }

    private static void ApplyToResources()
    {
        if (Application.Current == null) return;

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

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (Gate)
        {
            if (_loaded) return;
            Packs = LoadAllPacks();
            _loaded = true;
        }
    }

    private static Dictionary<string, Dictionary<string, string>> LoadAllPacks()
    {
        var packs = new Dictionary<string, Dictionary<string, string>>();
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
            if (!packs.TryGetValue(lang, out var pack))
                packs[lang] = pack = new Dictionary<string, string>();
            foreach (var (k, v) in dict) pack[k] = v;
        }

        if (!packs.ContainsKey(DefaultLang))
            Log.Warn("[i18n] 未找到默认词库 zh-Hans（I18n/Locales 目录为空？）");
        return packs;
    }
}
