using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace ImmurokClient.Localization;

/// <summary>
/// 运行时多语言管理器（对齐 macOS LocalizationManager 的「点分 key + 每语言 JSON + 英语兜底」模型）。
///
/// - 支持语言与 macOS 一致：en / zh-Hans / zh-Hant / ja / fr / es / pt / ru / de，默认英语。
/// - 字符串以内嵌资源 JSON 提供：Localization/Strings/{code}.json（扁平 key→文案）。
/// - 缺失的 key 回退到英语，再回退到 key 本身，保证永不为空。
/// - 实现 INotifyPropertyChanged，切换语言时通过 "Item[]" 通知，XAML 绑定即时刷新（无需重启）。
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    public static readonly (string Code, string Name)[] SupportedLanguages =
    {
        ("en",      "English"),
        ("zh-Hans", "简体中文"),
        ("zh-Hant", "繁體中文"),
        ("ja",      "日本語"),
        ("fr",      "Français"),
        ("es",      "Español"),
        ("pt",      "Português"),
        ("ru",      "Русский"),
        ("de",      "Deutsch"),
    };

    public const string DefaultLanguage = "en";

    private Dictionary<string, string> _current = new();
    private readonly Dictionary<string, string> _fallback; // 英语
    private string _language = DefaultLanguage;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>语言切换后触发。用于让「代码里赋值的动态文本」重新渲染（XAML 的 {loc:Tr} 绑定会自动刷新，不依赖此事件）。</summary>
    public event Action? LanguageChanged;

    private Loc()
    {
        _fallback = Load(DefaultLanguage);
        _current = _fallback;
    }

    public string CurrentLanguage => _language;

    /// <summary>切换语言；无效码回退英语。触发全量绑定刷新。</summary>
    public void SetLanguage(string? code)
    {
        code = Normalize(code);
        if (code == _language && _current.Count > 0) return;
        _language = code;
        _current = code == DefaultLanguage ? _fallback : Load(code);
        // 通知索引器 "Item[]" 变化 → 所有 {loc:Tr} 绑定重新取值。
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentLanguage)));
        // 通知页面重跑动态渲染。
        LanguageChanged?.Invoke();
    }

    /// <summary>XAML 绑定用索引器：{Binding [key], Source={x:Static loc:Loc.Instance}}。</summary>
    public string this[string key] => Translate(key);

    /// <summary>取翻译；支持 string.Format 占位符（{0}{1}…）。</summary>
    public string T(string key, params object[] args)
    {
        string s = Translate(key);
        return args is { Length: > 0 } ? SafeFormat(s, args) : s;
    }

    private string Translate(string key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;
        if (_current.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v)) return v;
        if (_fallback.TryGetValue(key, out var f) && !string.IsNullOrEmpty(f)) return f;
        return key; // 兜底：显示 key，方便发现漏翻
    }

    private static string SafeFormat(string fmt, object[] args)
    {
        try { return string.Format(fmt, args); }
        catch { return fmt; }
    }

    private static string Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return DefaultLanguage;
        foreach (var (c, _) in SupportedLanguages)
            if (string.Equals(c, code, StringComparison.OrdinalIgnoreCase)) return c;
        return DefaultLanguage;
    }

    private static Dictionary<string, string> Load(string code)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            string res = $"ImmurokClient.Localization.Strings.{code}.json";
            using Stream? s = asm.GetManifestResourceStream(res);
            if (s is null) return new();
            using var r = new StreamReader(s);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(r.ReadToEnd());
            return dict ?? new();
        }
        catch { return new(); }
    }
}
