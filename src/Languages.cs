using System.Collections.Generic;
using System.Linq;

namespace ZnKeyboard;

/// <summary>A translation target. <see cref="Name"/> goes into the prompt; <see cref="Code"/> is stored.</summary>
public sealed record TranslateLanguage(string Code, string Name, string ChineseName)
{
    /// <summary>"英语" or "English", following the UI language.</summary>
    public string DisplayName => L.IsChinese ? ChineseName : Name;

    public string Label => $"{Code} · {DisplayName}";
}

/// <summary>Same list and order as ZnKeyboard for Android and macOS: by international use.</summary>
public static class Languages
{
    public static readonly IReadOnlyList<TranslateLanguage> All = new[]
    {
        new TranslateLanguage("EN", "English", "英语"),
        new TranslateLanguage("ZH", "Simplified Chinese", "简体中文"),
        new TranslateLanguage("TW", "Traditional Chinese", "繁体中文"),
        new TranslateLanguage("ES", "Spanish", "西班牙语"),
        new TranslateLanguage("FR", "French", "法语"),
        new TranslateLanguage("AR", "Arabic", "阿拉伯语"),
        new TranslateLanguage("RU", "Russian", "俄语"),
        new TranslateLanguage("PT", "Portuguese", "葡萄牙语"),
        new TranslateLanguage("DE", "German", "德语"),
        new TranslateLanguage("JA", "Japanese", "日语"),
        new TranslateLanguage("KO", "Korean", "韩语"),
        new TranslateLanguage("ID", "Indonesian", "印尼语"),
        new TranslateLanguage("VI", "Vietnamese", "越南语"),
        new TranslateLanguage("TH", "Thai", "泰语"),
    };

    public static TranslateLanguage ByCode(string? code) => All.FirstOrDefault(l => l.Code == code) ?? All[0];
}
