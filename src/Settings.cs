using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ZnKeyboard;

public enum Provider { Gemini, OpenAICompatible }

/// <summary>Endpoints the OpenAI-compatible provider offers without typing an address.</summary>
public enum ApiPreset { DeepSeek, Custom }

public enum ShortcutAction { Polish, Translate, Screenshot }

/// <summary>A global shortcut: Win32 MOD_* flags, a virtual-key code, and the key's name ("R", "F5").</summary>
public sealed record Shortcut(int Modifiers, int Key, string KeyName)
{
    public const int Alt = 0x1, Control = 0x2, Shift = 0x4, Win = 0x8;

    public string Display
    {
        get
        {
            var text = new StringBuilder();
            if ((Modifiers & Control) != 0) text.Append("Ctrl+");
            if ((Modifiers & Alt) != 0) text.Append("Alt+");
            if ((Modifiers & Shift) != 0) text.Append("Shift+");
            if ((Modifiers & Win) != 0) text.Append("Win+");
            return text.Append(KeyName).ToString();
        }
    }

    public bool Collides(Shortcut other) => Modifiers == other.Modifiers && Key == other.Key;

    /// <summary>Ctrl+Alt+R / T / E: the same letters as ⇧⌘R / T / E on the Mac.</summary>
    public static Shortcut Default(ShortcutAction action) => action switch
    {
        ShortcutAction.Polish => new Shortcut(Control | Alt, 0x52, "R"),
        ShortcutAction.Translate => new Shortcut(Control | Alt, 0x54, "T"),
        _ => new Shortcut(Control | Alt, 0x45, "E"),
    };
}

/// <summary>
/// Everything but the API keys, as JSON in %APPDATA%\ZnKeyboard\settings.json. API keys go to
/// Windows Credential Manager, one entry per provider.
/// </summary>
public sealed class Settings
{
    public const string DefaultGeminiModel = "gemini-3.5-flash-lite";
    public const string DeepSeekBaseUrl = "https://api.deepseek.com";
    // The custom preset starts from DeepSeek's address too, so 0.3.x settings without one still work.
    public const string DefaultOpenAIBaseUrl = DeepSeekBaseUrl;
    public const string DefaultOpenAIModel = "deepseek-flash";

    public Provider Provider { get; set; } = Provider.Gemini;
    /// <summary>Unset before presets: then DeepSeek's address (the default) means DeepSeek, anything else custom.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<ApiPreset>))]
    public ApiPreset? Preset { get; set; }
    /// <summary>The typed address, used by the custom preset.</summary>
    public string OpenAIBaseUrl { get; set; } = DefaultOpenAIBaseUrl;
    public string GeminiModel { get; set; } = DefaultGeminiModel;
    public string OpenAIModel { get; set; } = DefaultOpenAIModel;
    public string TranslateTarget { get; set; } = "EN";
    /// <summary>The language screenshot translation reads into.</summary>
    public string ScreenshotTarget { get; set; } = "ZH";
    /// <summary>"system", "zh" or "en".</summary>
    public string UiLanguage { get; set; } = "system";
    /// <summary>The 润色 | AI 翻译为 pill on text selected in a text box. On by default.</summary>
    public bool SelectionPill { get; set; } = true;
    /// <summary>The 翻译为 pill on text selected while reading (web pages, documents). Off by default.</summary>
    public bool ReadingPill { get; set; }
    /// <summary>The language the reading pill translates into, on its own.</summary>
    public string ReadingTarget { get; set; } = "EN";
    /// <summary>Translation cards (reading, screenshot) stay open on clicks elsewhere. Remembered.</summary>
    public bool PinTranslationCard { get; set; }
    /// <summary>A daily look for a new release. On by default.</summary>
    public bool AutomaticUpdateChecks { get; set; } = true;
    public DateTime LastUpdateCheck { get; set; }
    /// <summary>The newest version already announced, so each is announced once.</summary>
    public string? NotifiedUpdate { get; set; }
    public Shortcut? PolishShortcut { get; set; }
    public Shortcut? TranslateShortcut { get; set; }
    public Shortcut? ScreenshotShortcut { get; set; }

    // Static fields initialise in the order they are written, so the paths must come before Current:
    // until 0.6.0 Load() ran while FilePath was still null, never read the file, and the next Save()
    // wrote the defaults over it.
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZnKeyboard");
    public static string FolderPath => Folder;
    private static readonly string FilePath = Path.Combine(Folder, "settings.json");
    // The previous good copy, kept by each save.
    private static readonly string BackupPath = FilePath + ".bak";
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    public static Settings Current { get; private set; } = Load();

    private static Settings Load()
    {
        // 0.3.0–0.3.1 stored the OpenAI-compatible provider's number (1) for a fixed DeepSeek
        // provider with no address or model, so those load as DeepSeek through the defaults; only
        // the key needs to move back.
        Credentials.MoveDeepSeekKey();
        var settings = Read(FilePath) ?? Read(BackupPath);
        if (settings == null)
        {
            settings = new Settings();
            // Settings lost before 0.6.1 still have their key: pick the provider it belongs to.
            if (string.IsNullOrEmpty(Credentials.Read(Provider.Gemini)) && !string.IsNullOrEmpty(Credentials.Read(Provider.OpenAICompatible)))
                settings.Provider = Provider.OpenAICompatible;
        }
        // A file edited by hand, or written by another version, may leave these out or null.
        settings.OpenAIBaseUrl ??= DefaultOpenAIBaseUrl;
        settings.GeminiModel ??= DefaultGeminiModel;
        settings.OpenAIModel ??= DefaultOpenAIModel;
        settings.TranslateTarget ??= "EN";
        settings.ScreenshotTarget ??= "ZH";
        settings.ReadingTarget ??= "EN";
        settings.UiLanguage ??= "system";
        if (!Enum.IsDefined(settings.Provider)) settings.Provider = Provider.Gemini;
        return settings;
    }

    /// <summary>The settings in <paramref name="path"/>, or null when it is missing or damaged (then copied aside for the log).</summary>
    private static Settings? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path));
        }
        catch (Exception error)
        {
            DebugLog.Note($"settings unreadable ({Path.GetFileName(path)}): {error.GetType().Name}");
            try { File.Copy(path, path + ".damaged", overwrite: true); } catch (Exception) { }
            return null;
        }
    }

    /// <summary>
    /// Writes a temporary file through to disk, then swaps it in and keeps the previous copy as
    /// settings.json.bak, so a crash, power cut or update mid-save leaves whole settings, never half a file.
    /// </summary>
    public void Save()
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                var temp = FilePath + ".tmp";
                using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    JsonSerializer.Serialize(file, this, Format);
                    file.Flush(flushToDisk: true);
                }
                if (File.Exists(FilePath)) File.Replace(temp, FilePath, BackupPath, ignoreMetadataErrors: true);
                else File.Move(temp, FilePath);
            }
            catch (Exception error)
            {
                // Still in memory; the next save tries again. A file held by antivirus must not crash the tray app.
                DebugLog.Note($"settings not saved: {error.GetType().Name}: {error.Message}");
            }
        }
    }

    public TranslateLanguage Target => Languages.ByCode(TranslateTarget);

    /// <summary>The model for <paramref name="provider"/>. Gemini falls back to the default unless a Lite model is saved.</summary>
    public string ModelFor(Provider provider) => provider == Provider.Gemini
        ? (GeminiModels.IsLite(GeminiModel) ? GeminiModel : DefaultGeminiModel)
        : (string.IsNullOrWhiteSpace(OpenAIModel) ? DefaultOpenAIModel : OpenAIModel.Trim());

    public Shortcut ShortcutFor(ShortcutAction action) => action switch
    {
        ShortcutAction.Polish => PolishShortcut,
        ShortcutAction.Translate => TranslateShortcut,
        _ => ScreenshotShortcut,
    } ?? Shortcut.Default(action);

    public void SetShortcut(ShortcutAction action, Shortcut shortcut)
    {
        switch (action)
        {
            case ShortcutAction.Polish: PolishShortcut = shortcut; break;
            case ShortcutAction.Translate: TranslateShortcut = shortcut; break;
            default: ScreenshotShortcut = shortcut; break;
        }
        Save();
    }

    public TranslateLanguage ReadTarget => Languages.ByCode(ScreenshotTarget);

    public TranslateLanguage ReadingLanguage => Languages.ByCode(ReadingTarget);

    /// <summary>The client for the selected provider and its key, model and endpoint.</summary>
    public AIClient CreateClient() => new(Provider, CompatibleBaseUrl, Credentials.Read(Provider) ?? "", ModelFor(Provider));

    public ApiPreset EffectivePreset => Preset
        ?? (string.IsNullOrWhiteSpace(OpenAIBaseUrl) || OpenAIBaseUrl.Contains("api.deepseek.com") ? ApiPreset.DeepSeek : ApiPreset.Custom);

    /// <summary>The address the OpenAI-compatible provider calls: the preset's, or the typed one.</summary>
    public string CompatibleBaseUrl => EffectivePreset == ApiPreset.DeepSeek ? DeepSeekBaseUrl : OpenAIBaseUrl;

    // Start with Windows, through the current user's Run key (no admin rights needed).
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool StartsWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("ZnKeyboard") is string;
        }
        set => SetStartWithWindows(value ? Environment.ProcessPath : null);
    }

    /// <summary>Starts <paramref name="exe"/> with Windows, or nothing when null.</summary>
    public static void SetStartWithWindows(string? exe)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (exe != null) key.SetValue("ZnKeyboard", $"\"{exe}\"");
        else key.DeleteValue("ZnKeyboard", throwOnMissingValue: false);
    }
}

/// <summary>Gemini is limited to Flash-Lite 2.5 and newer, as on Android and macOS.</summary>
public static class GeminiModels
{
    private static readonly Regex Lite = new(@"^gemini-(\d+(?:\.\d+)?)-flash-lite(?:-[a-z0-9-]+)?$", RegexOptions.Compiled);

    public static bool IsLite(string? id)
    {
        var match = Lite.Match((id ?? "").Trim().ToLowerInvariant());
        return match.Success && double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var version) && version >= 2.5;
    }

    /// <summary>Lite models from a /models listing, newest first, without previews whose stable release is listed.</summary>
    public static string[] LiteModels(string[] ids)
    {
        var lite = new System.Collections.Generic.HashSet<string>();
        foreach (var id in ids)
        {
            var clean = id.Replace("models/", "");
            if (IsLite(clean)) lite.Add(clean);
        }
        var result = new System.Collections.Generic.List<string>();
        foreach (var id in lite)
            if (!(id.EndsWith("-preview") && lite.Contains(id[..^"-preview".Length]))) result.Add(id);
        result.Sort((a, b) => Version(b).CompareTo(Version(a)) is var c && c != 0 ? c : string.CompareOrdinal(a, b));
        return result.ToArray();
    }

    private static double Version(string id)
    {
        var part = id["gemini-".Length..].Split('-')[0];
        return double.TryParse(part, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}

/// <summary>API keys in Windows Credential Manager (encrypted for the signed-in user).</summary>
public static class Credentials
{
    private const string LegacyDeepSeekTarget = "ZnKeyboard/deepseek-api-key";

    private static string Target(Provider provider) =>
        provider == Provider.Gemini ? "ZnKeyboard/gemini-api-key" : "ZnKeyboard/openai-api-key";

    public static string? Read(Provider provider) => ReadTarget(Target(provider));

    /// <summary>Removes every key ZnKeyboard saved (when uninstalling).</summary>
    public static void DeleteAll()
    {
        foreach (var provider in Enum.GetValues<Provider>()) CredDelete(Target(provider), CredTypeGeneric, 0);
        CredDelete(LegacyDeepSeekTarget, CredTypeGeneric, 0);
    }

    /// <summary>Moves a 0.3.0–0.3.1 DeepSeek key to the OpenAI-compatible provider, then removes it.</summary>
    public static void MoveDeepSeekKey()
    {
        var key = ReadTarget(LegacyDeepSeekTarget);
        if (key == null) return;
        if (string.IsNullOrEmpty(Read(Provider.OpenAICompatible))) Write(Provider.OpenAICompatible, key);
        CredDelete(LegacyDeepSeekTarget, CredTypeGeneric, 0);
    }

    private static string? ReadTarget(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.CredentialBlobSize == 0) return null;
            return Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public static void Write(Provider provider, string? value)
    {
        var key = (value ?? "").Trim();
        if (key.Length == 0)
        {
            CredDelete(Target(provider), CredTypeGeneric, 0);
            return;
        }
        var blob = Marshal.StringToCoTaskMemUni(key);
        try
        {
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = Target(provider),
                CredentialBlob = blob,
                CredentialBlobSize = (uint)(key.Length * 2),
                Persist = CredPersistLocalMachine,
                UserName = Environment.UserName,
            };
            if (!CredWrite(ref credential, 0)) DebugLog.Note($"key not saved: Win32 error {Marshal.GetLastWin32Error()}");
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUnicode(blob);
        }
    }

    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
