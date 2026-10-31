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
    public static string TranslateShort => T("AI 翻译为", "Translate to");
    public static string SettingsMenu => T("设置…", "Settings…");
    public static string Quit => T("退出 ZnKeyboard", "Quit ZnKeyboard");
    public static string OpenLog => T("打开诊断日志", "Open Diagnostic Log");
    public static string ScreenshotTranslate => T("截图翻译", "Screenshot Translate");
    public static string CheckForUpdates => T("检查更新…", "Check for Updates…");
    public static string About => T("关于 ZnKeyboard", "About ZnKeyboard");
    public static string AboutText(string version) => T($"ZnKeyboard for Windows {version}\n选中文字一键润色、翻译，截图翻译。\n\noprfelix.github.io/znkeyboard\nGPL-3.0 开源",
        $"ZnKeyboard for Windows {version}\nPolish or translate selected text, and translate screenshots.\n\noprfelix.github.io/znkeyboard\nOpen source under GPL-3.0");
    public static string UpdateAvailable(string version) => T($"有新版本 {version}", $"ZnKeyboard {version} is available");
    public static string UpdateAvailableNote(string current) => T($"你正在使用 {current}。下载后右键托盘图标「退出」，用新的 ZnKeyboard.exe 替换旧文件即可，设置和 API Key 都会保留。\n\n现在打开下载页吗？",
        $"You have {current}. Download it, quit ZnKeyboard from the tray, and replace ZnKeyboard.exe; your settings and API key stay.\n\nOpen the download page now?");
    public static string UpToDate(string current) => T($"ZnKeyboard {current} 已是最新版本。", $"ZnKeyboard {current} is up to date.");
    public static string UpdateCheckFailed => T("无法检查更新，请检查网络。\n\n要打开 GitHub 发布页看看吗？", "Couldn't check for updates. Check your connection.\n\nOpen the releases page on GitHub?");

    // Review panel
    public static string Working => T("处理中…", "Working…");
    public static string Replace => T("替换", "Replace");
    public static string Copy => T("复制", "Copy");
    public static string Cancel => T("取消", "Cancel");
    public static string OpenSettings => T("打开设置", "Open Settings");
    public static string Retry => T("重试", "Try Again");
    public static string Regenerate => T("重新生成", "Regenerate");
    public static string CloseHint => T("关闭（Esc）", "Close (Esc)");
    public static string CopyHint => T("复制结果（Ctrl+C）", "Copy the result (Ctrl+C)");
    public static string ScreenshotTranslateTo => T("截图翻译为", "Screenshot to");
    public static string OriginalText => T("原文", "Original");
    public static string CopyOriginal => T("复制原文", "Copy Original");
    public static string CopyTranslation => T("复制译文", "Copy Translation");
    public static string NoTextFound => T("截图里没有识别到文字。", "No text found in the screenshot.");
    public static string OcrFailed => T("识别文字失败。请确认 Windows 设置 → 时间和语言 → 语言和区域 里装有中文或英文语言包，再试一次。",
        "Couldn't read the text. Make sure a Chinese or English language pack is installed (Settings → Time & language → Language & region), then try again.");

    // Errors
    public static string NeedKey => T("请先在设置里填写 API Key。", "Add your API key in Settings first.");
    public static string BadEndpoint => T("API 地址无效，请在设置里填写以 https:// 开头的地址。", "The API address is invalid. Enter one starting with https:// in Settings.");
    public static string NoSelection => T("请先选中要处理的文字。", "Select some text first.");
    public static string TooLong => T("文字太长，请分段处理（最多 5000 字）。", "The text is too long; do it in parts (5,000 characters max).");
    public static string NoChange => T("没有需要修改的地方。", "Nothing to change.");
    public static string KeyRejected => T("API Key 无效或没有权限，请在设置里检查。", "The API key was rejected. Check it in Settings.");
    public static string RegionUnsupported => T("Gemini 在你所在的地区不可用。请在设置里改用 OpenAI 兼容（DeepSeek），或通过代理网络使用 Gemini。",
        "Gemini isn't available in your region. Switch to OpenAI compatible (DeepSeek) in Settings, or reach Gemini through a proxy.");
    public static string RequestRejected(int code, string? reason) => reason == null
        ? T($"服务商拒绝了请求（{code}）。", $"The provider rejected the request ({code}).")
        : T($"服务商拒绝了请求（{code}）：{reason}", $"The provider rejected the request ({code}): {reason}");
    public static string BadResponse => T("AI 没有返回可用的结果，请再试一次。", "The AI returned nothing usable. Try again.");
    public static string NetworkFailed => T("无法连接 AI 服务，请检查网络和 API 地址。", "Couldn't reach the AI service. Check your connection and API address.");

    // Settings
    public static string SettingsTitle => T("ZnKeyboard 设置", "ZnKeyboard Settings");
    public static string AiSection => T("AI 服务", "AI service");
    public static string Provider => T("服务商", "Provider");
    public static string OpenAICompatible => T("OpenAI 兼容", "OpenAI compatible");
    public static string ApiBaseUrl => T("API 地址", "API address");
    public static string ApiKey => T("API Key", "API key");
    public static string NoKeyYet => T("还没有 Key？在 Google AI Studio 免费获取 ↗", "No key yet? Get one free in Google AI Studio ↗");
    public static string NoDeepSeekKeyYet => T("还没有 Key？在 DeepSeek 开放平台获取 ↗", "No key yet? Get one on the DeepSeek platform ↗");
    public static string KeyChecking => T("正在验证 API Key…", "Checking the API key…");
    public static string KeyWorks => T("API Key 可用", "API key works");
    public static string KeyStoredNote => T("Key 保存在 Windows 凭据管理器，粘贴后自动保存并验证。", "Saved in Windows Credential Manager; pasted keys save and check themselves.");
    public static string KeyInvalid => T("API Key 无效或没有权限，请检查是否复制完整、是否已过期。", "This API key was rejected. Check it was copied in full and is still active.");
    public static string KeyUnverified(string reason) => T($"暂时无法验证 API Key：{reason}", $"Couldn't check the API key: {reason}");
    public static string Model => T("模型", "Model");
    public static string Recommended => T("推荐", "recommended");
    public static string GeminiNote => T("只提供速度快、费用低的 Flash-Lite 模型。润色或翻译时，选中的文字会发给 Google Gemini。",
        "Only the fast, low-cost Flash-Lite models. When you polish or translate, the selected text is sent to Google Gemini.");
    public static string ProviderNote => T("润色、翻译用哪家 AI 服务", "Which AI service polishes and translates");
    public static string ApiPresetNote => T("DeepSeek 地址已内置；自定义可接其他兼容服务", "DeepSeek's address is built in; Custom takes any compatible service");
    public static string ApiBaseUrlNote => T("以 https:// 开头", "Starts with https://");
    public static string ModelNote => T("润色和翻译使用的模型", "The model that polishes and translates");
    public static string ApiPreset => T("接口", "Endpoint");
    public static string CustomPreset => T("自定义", "Custom");
    public static string DeepSeekNote => T("地址已内置，填好 API Key 就能用，国内可直连。润色或翻译时，选中的文字会发给 DeepSeek。",
        "The address is built in, so a key is all you need; fast in mainland China. When you polish or translate, the selected text is sent to DeepSeek.");
    public static string OpenAINote => T("可接 OpenAI、OpenRouter 等兼容服务，填写对方的 API 地址和模型。润色或翻译时，选中的文字会发给该服务。",
        "Works with OpenAI, OpenRouter and other compatible services: enter their API address and model. When you polish or translate, the selected text is sent to that service.");

    public static string UsageSection => T("使用", "Usage");
    public static string SelectionPill => T("选中文字后显示按钮", "Show buttons on selected text");
    public static string SelectionPillNote => T("用鼠标拖选、双击或按 Ctrl+A 全选后，旁边会出现「润色」和「AI 翻译为」按钮",
        "Drag over text, double-click or press Ctrl+A, and Polish and Translate buttons appear beside it");
    public static string TranslateTargetNote => T("「AI 翻译为」默认翻译成的语言", "What Translate turns your text into");
    public static string ScreenshotTarget => T("截图翻译成", "Screenshot translates to");
    public static string ScreenshotTargetNote => T("截到的如果本身是中文，会翻译成英文。图片只在本机识别，不会上传",
        "Chinese text goes to English instead. The image is read on this PC and never uploaded");
    public static string ShortcutsSection => T("快捷键", "Shortcuts");
    public static string PolishShortcut => T("润色", "Polish");
    public static string PolishShortcutNote => T("选中文字后按下，润色后可替换原文", "Polish the selected text, then replace it");
    public static string TranslateShortcut => T("AI 翻译", "Translate");
    public static string TranslateShortcutNote => T("选中文字后按下，翻译成目标语言", "Translate the selected text into the target language");
    public static string ScreenshotShortcut => T("截图翻译", "Screenshot translate");
    public static string ScreenshotShortcutNote => T("按下后拖出屏幕上要翻译的区域", "Drag over the part of the screen to translate");
    public static string ShortcutsNote => T("按钮不出现的程序里也能用快捷键。点击快捷键后按下新的组合即可修改，Esc 取消。",
        "Shortcuts work even where the buttons don't appear. Click one, then press the new combination; Esc cancels.");
    public static string PressShortcut => T("按下新快捷键…", "Press keys…");
    public static string ResetDefault => T("恢复默认", "Reset");
    public static string NeedModifier => T("需要包含 Ctrl、Alt 或 Win 中的至少一个。", "Include at least one of Ctrl, Alt or Win.");
    public static string ShortcutUsedByOther => T("另一个功能已在用这个快捷键。", "The other action already uses this shortcut.");
    public static string ShortcutTaken => T("这个快捷键已被其他程序占用，换一个试试。", "Another app already uses this shortcut. Try another.");

    public static string GeneralSection => T("通用", "General");
    public static string UiLanguage => T("界面语言", "Language");
    public static string FollowSystem => T("跟随系统", "System");
    public static string StartWithWindows => T("开机自动启动", "Start with Windows");
    public static string StartWithWindowsNote => T("登录 Windows 后在托盘里待命", "Wait in the tray after you sign in");
    public static string UiLanguageNote => T("设置、托盘菜单和结果卡片的语言", "For Settings, the tray menu and the result card");
    public static string Version => T("版本", "Version");
    public static string DiagnosticLog => T("诊断日志", "Diagnostic log");
    public static string DiagnosticLogNote => T("遇到问题时发给我们，不包含任何文字内容", "Send it when something goes wrong; it never contains your text");
    public static string Open => T("打开", "Open");
    public static string TrayHint => T("ZnKeyboard：选中文字后按快捷键润色或翻译", "ZnKeyboard: select text and press a shortcut");
}
