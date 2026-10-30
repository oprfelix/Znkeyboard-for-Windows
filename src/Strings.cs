using System.Globalization;

namespace ZnKeyboard;

/// <summary>UI text in Chinese or English, following the language setting ("system" uses Windows').</summary>
public static class L
{
    public static bool IsChinese => Settings.Current.UiLanguage switch
    {
        "zh" => true,
        "en" => false,
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh",
    };

    private static string T(string zh, string en) => IsChinese ? zh : en;

    // Tray menu
    public static string Polish => T("润色", "Polish");
    public static string PolishSelection => T("润色所选文字", "Polish Selected Text");
    public static string TranslateTo(TranslateLanguage language) => T($"AI 翻译为 {language.DisplayName}", $"Translate to {language.DisplayName}");
    public static string TranslateMenu => T("翻译成", "Translate To");
    public static string SettingsMenu => T("设置…", "Settings…");
    public static string Quit => T("退出 ZnKeyboard", "Quit ZnKeyboard");

    // Review panel
    public static string Working => T("处理中…", "Working…");
    public static string Replace => T("替换", "Replace");
    public static string Copy => T("复制", "Copy");
    public static string Cancel => T("取消", "Cancel");
    public static string OpenSettings => T("打开设置", "Open Settings");

    // Errors
    public static string NeedKey => T("请先在设置里填写 API Key。", "Add your API key in Settings first.");
    public static string BadEndpoint => T("API 地址无效，请在设置里填写以 https:// 开头的地址。", "The API address is invalid. Enter one starting with https:// in Settings.");
    public static string NoSelection => T("请先选中要处理的文字。", "Select some text first.");
    public static string TooLong => T("文字太长，请分段处理（最多 5000 字）。", "The text is too long; do it in parts (5,000 characters max).");
    public static string NoChange => T("没有需要修改的地方。", "Nothing to change.");
    public static string KeyRejected => T("请求被拒绝，请检查 API Key 和模型。", "The request was rejected. Check your API key and model.");
    public static string BadResponse => T("AI 没有返回可用的结果，请再试一次。", "The AI returned nothing usable. Try again.");
    public static string RequestFailed(int code) => T($"AI 请求失败（{code}）。", $"The AI request failed ({code}).");
    public static string NetworkFailed => T("无法连接 AI 服务，请检查网络和 API 地址。", "Couldn't reach the AI service. Check your connection and API address.");

    // Settings
    public static string SettingsTitle => T("ZnKeyboard 设置", "ZnKeyboard Settings");
    public static string AiSection => T("AI 服务", "AI service");
    public static string Provider => T("服务商", "Provider");
    public static string OpenAICompatible => T("OpenAI 兼容", "OpenAI compatible");
    public static string ApiBaseUrl => T("API 地址", "API address");
    public static string ApiKey => T("API Key", "API key");
    public static string Save => T("保存", "Save");
    public static string KeyStored => T("✓ 已保存在 Windows 凭据管理器", "✓ Saved in Windows Credential Manager");
    public static string NoKeyYet => T("还没有 Key？在 Google AI Studio 免费获取 ↗", "No key yet? Get one free in Google AI Studio ↗");
    public static string Model => T("模型", "Model");
    public static string Recommended => T("推荐", "recommended");
    public static string GeminiNote => T("仅提供 Flash-Lite 模型，速度快、费用低。", "Flash-Lite models only: fast and low cost.");
    public static string OpenAINote => T("支持 OpenAI、DeepSeek、OpenRouter 等兼容接口，例如 DeepSeek 填 https://api.deepseek.com、模型 deepseek-chat。",
        "Works with OpenAI, DeepSeek, OpenRouter and other compatible APIs, e.g. https://api.deepseek.com with model deepseek-chat.");
    public static string PrivacyNote => T("润色和翻译时，选中的文字会发送给所选的服务商。每个服务商的 Key 分开保存。",
        "When you polish or translate, the selected text is sent to the selected provider. Each provider keeps its own key.");

    public static string UsageSection => T("使用", "Usage");
    public static string PolishShortcut => T("润色快捷键", "Polish shortcut");
    public static string TranslateShortcut => T("AI 翻译快捷键", "Translate shortcut");
    public static string ShortcutsNote => T("在任意程序里选中文字后按快捷键，确认后替换原文。点击快捷键可以改成你习惯的组合，Esc 取消。",
        "Select text in any app and press a shortcut, then confirm to replace it. Click a shortcut to change it; Esc cancels.");
    public static string PressShortcut => T("按下新快捷键…", "Press keys…");
    public static string ResetDefault => T("恢复默认", "Reset");
    public static string NeedModifier => T("需要包含 Ctrl、Alt 或 Win 中的至少一个。", "Include at least one of Ctrl, Alt or Win.");
    public static string ShortcutUsedByOther => T("另一个功能已在用这个快捷键。", "The other action already uses this shortcut.");
    public static string ShortcutTaken => T("这个快捷键已被其他程序占用，换一个试试。", "Another app already uses this shortcut. Try another.");

    public static string GeneralSection => T("通用", "General");
    public static string UiLanguage => T("界面语言", "Language");
    public static string FollowSystem => T("跟随系统", "System");
    public static string StartWithWindows => T("开机自动启动", "Start with Windows");
    public static string Version => T("版本", "Version");
    public static string TrayHint => T("ZnKeyboard：选中文字后按快捷键润色或翻译", "ZnKeyboard: select text and press a shortcut");
}
