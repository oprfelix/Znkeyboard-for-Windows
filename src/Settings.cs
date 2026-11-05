using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ZnKeyboard;

/// <summary>Saved as its number, so new providers go last; the menu's order is <see cref="Settings.ProviderMenu"/>.</summary>
public enum Provider { Gemini, OpenAICompatible, DeepSeek }

/// <summary>Through 0.7.4 DeepSeek was a preset of the OpenAI-compatible provider; only the upgrade reads this.</summary>
public enum ApiPreset { DeepSeek, Custom }

/// <summary>New actions go last: each one's hot key id is its number.</summary>
public enum ShortcutAction { Polish, Translate, Screenshot, Read }

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

    /// <summary>Ctrl+Shift+1 reading, 2 screenshot, E polish, R translate: the Mac's ⇧⌘ keys.</summary>
    public static Shortcut Default(ShortcutAction action) => action switch
    {
        ShortcutAction.Read => new Shortcut(Control | Shift, 0x31, "1"),
        ShortcutAction.Screenshot => new Shortcut(Control | Shift, 0x32, "2"),
        ShortcutAction.Polish => new Shortcut(Control | Shift, 0x45, "E"),
        _ => new Shortcut(Control | Shift, 0x52, "R"),
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
    // Where the OpenAI-compatible provider's address starts before the user types their own.
    public const string DefaultOpenAIBaseUrl = "https://api.openai.com/v1";
    public const string DefaultDeepSeekModel = "deepseek-flash";
    /// <summary>The providers in the order the settings menu lists them.</summary>
    public static readonly Provider[] ProviderMenu = [Provider.DeepSeek, Provider.Gemini, Provider.OpenAICompatible];

    public Provider Provider { get; set; } = Provider.Gemini;
    /// <summary>Unset before presets: then DeepSeek's address (or none) means DeepSeek, anything else custom.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<ApiPreset>))]
    public ApiPreset? Preset { get; set; }
    /// <summary>Whether the DeepSeek preset has moved to its own provider.</summary>
    public bool DeepSeekPresetSplit { get; set; }
    /// <summary>The OpenAI-compatible provider's address. Empty until Load, so files without one read as DeepSeek.</summary>
    public string OpenAIBaseUrl { get; set; } = "";
    public string GeminiModel { get; set; } = DefaultGeminiModel;
    public string DeepSeekModel { get; set; } = DefaultDeepSeekModel;
    /// <summary>No default: a model ID only means something on the address the user typed.</summary>
    public string OpenAIModel { get; set; } = "";
    public string TranslateTarget { get; set; } = "EN";
    /// <summary>The language translations to read go into: text selected while reading, and screenshots. Kept under its 0.4 name.</summary>
    public string ScreenshotTarget { get; set; } = "ZH";
    /// <summary>"system", "zh" or "en".</summary>
    public string UiLanguage { get; set; } = "system";
    /// <summary>The 润色 | AI 翻译为 pill on text selected in a text box. On by default.</summary>
    public bool SelectionPill { get; set; } = true;
    /// <summary>The 翻译为 pill on text selected while reading (web pages, documents). Off by default.</summary>
    public bool ReadingPill { get; set; }
    /// <summary>Translation cards (reading, screenshot) stay open on clicks elsewhere. Remembered.</summary>
    public bool PinTranslationCard { get; set; }
    /// <summary>The anonymous daily ping behind the numbers on znkeyboard.2amz.com. On by default.</summary>
    public bool AnonymousStats { get; set; } = true;
    /// <summary>A random id made on this PC for the ping, tied to nothing else.</summary>
    public string? InstallId { get; set; }
    /// <summary>Polish and translate runs not yet reported.</summary>
    public int PendingUses { get; set; }
    public DateTime LastPing { get; set; }
    /// <summary>A daily look for a new release. On by default.</summary>
    public bool AutomaticUpdateChecks { get; set; } = true;
    public DateTime LastUpdateCheck { get; set; }
    /// <summary>The newest version already announced, so each is announced once.</summary>
    public string? NotifiedUpdate { get; set; }
    public Shortcut? PolishShortcut { get; set; }
    public Shortcut? TranslateShortcut { get; set; }
    public Shortcut? ScreenshotShortcut { get; set; }
    public Shortcut? ReadShortcut { get; set; }

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
        settings.OpenAIBaseUrl ??= "";
        settings.OpenAIModel ??= "";
        settings.SplitDeepSeekPreset();
        if (string.IsNullOrWhiteSpace(settings.OpenAIBaseUrl)) settings.OpenAIBaseUrl = DefaultOpenAIBaseUrl;
        settings.GeminiModel ??= DefaultGeminiModel;
        settings.DeepSeekModel ??= DefaultDeepSeekModel;
        settings.TranslateTarget ??= "EN";
        settings.ScreenshotTarget ??= "ZH";
        settings.UiLanguage ??= "system";
        if (!Enum.IsDefined(settings.Provider)) settings.Provider = Provider.Gemini;
        return settings;
    }

    /// <summary>
    /// One-time move of the DeepSeek preset to its own provider, key and model included. The old
    /// key is removed only once the new one reads back, so a failed write retries next launch.
    /// </summary>
    private void SplitDeepSeekPreset()
    {
        if (DeepSeekPresetSplit) return;
        var onDeepSeek = Preset == ApiPreset.DeepSeek
            || (Preset == null && (string.IsNullOrWhiteSpace(OpenAIBaseUrl) || OpenAIBaseUrl.Contains("api.deepseek.com")));
        if (onDeepSeek)
        {
            var key = Credentials.Read(Provider.OpenAICompatible);
            if (!string.IsNullOrEmpty(key))
            {
                Credentials.Write(Provider.DeepSeek, key);
                if (Credentials.Read(Provider.DeepSeek) != key.Trim()) return;
                Credentials.Write(Provider.OpenAICompatible, null);
            }
            if (OpenAIModel.StartsWith("deepseek", StringComparison.OrdinalIgnoreCase)) DeepSeekModel = OpenAIModel.Trim();
            OpenAIModel = "";
            if (OpenAIBaseUrl.Contains("api.deepseek.com")) OpenAIBaseUrl = "";
            if (Provider == Provider.OpenAICompatible) Provider = Provider.DeepSeek;
        }
        Preset = null;
        DeepSeekPresetSplit = true;
        Save();
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
    public string ModelFor(Provider provider) => provider switch
    {
        Provider.Gemini => GeminiModels.IsLite(GeminiModel) ? GeminiModel : DefaultGeminiModel,
        Provider.DeepSeek => string.IsNullOrWhiteSpace(DeepSeekModel) ? DefaultDeepSeekModel : DeepSeekModel.Trim(),
        _ => OpenAIModel.Trim(),
    };

    public void SetModel(Provider provider, string model)
    {
        switch (provider)
        {
            case Provider.Gemini: GeminiModel = model; break;
            case Provider.DeepSeek: DeepSeekModel = model.Trim(); break;
            default: OpenAIModel = model.Trim(); break;
        }
    }

    public Shortcut ShortcutFor(ShortcutAction action) => action switch
    {
        ShortcutAction.Polish => PolishShortcut,
        ShortcutAction.Translate => TranslateShortcut,
        ShortcutAction.Screenshot => ScreenshotShortcut,
        _ => ReadShortcut,
    } ?? Shortcut.Default(action);

    public void SetShortcut(ShortcutAction action, Shortcut shortcut)
    {
        switch (action)
        {
            case ShortcutAction.Polish: PolishShortcut = shortcut; break;
            case ShortcutAction.Translate: TranslateShortcut = shortcut; break;
            case ShortcutAction.Screenshot: ScreenshotShortcut = shortcut; break;
            default: ReadShortcut = shortcut; break;
        }
        Save();
    }

    public TranslateLanguage ReadTarget => Languages.ByCode(ScreenshotTarget);

    /// <summary>The client for the selected provider and its key, model and endpoint.</summary>
    public AIClient CreateClient() => new(Provider, Provider == Provider.DeepSeek ? DeepSeekBaseUrl : OpenAIBaseUrl, Credentials.Read(Provider) ?? "", ModelFor(Provider));

    // Start with Windows, through the current user's Run key (no admin rights needed).
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // What Task Manager, Settings → Apps → Startup and security tools turned off: a value named like
    // the Run entry, odd first byte. Windows skips such an entry, however long it stays in the Run key.
    private const string StartupApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string StartupName = "ZnKeyboard";

    public static bool StartsWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            using var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedKey);
            var turnedOff = approved?.GetValue(StartupName) is byte[] { Length: > 0 } state && (state[0] & 1) != 0;
            return key?.GetValue(StartupName) is string && !turnedOff;
        }
        set => SetStartWithWindows(value ? Environment.ProcessPath : null);
    }

    /// <summary>Starts <paramref name="exe"/> with Windows, or nothing when null; either way an earlier "turned off" is gone.</summary>
    public static void SetStartWithWindows(string? exe)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (exe != null) key.SetValue(StartupName, StartupCommand(exe));
            else key.DeleteValue(StartupName, throwOnMissingValue: false);
        }
        using var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedKey, writable: true);
        approved?.DeleteValue(StartupName, throwOnMissingValue: false);
    }

    /// <summary>Points an existing entry at <paramref name="exe"/>, leaving alone whether it is turned on.</summary>
    public static void UpdateStartWithWindows(string exe)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(StartupName) is string) key.SetValue(StartupName, StartupCommand(exe));
    }

    private static string StartupCommand(string exe) => $"\"{exe}\" {Installer.StartupArg}";
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
    // Stays apart from the DeepSeek provider's target: MoveDeepSeekKey empties this one on every launch.
    private const string LegacyDeepSeekTarget = "ZnKeyboard/deepseek-api-key";

    private static string Target(Provider provider) => provider switch
    {
        Provider.Gemini => "ZnKeyboard/gemini-api-key",
        Provider.DeepSeek => "ZnKeyboard/deepseek-provider-key",
        _ => "ZnKeyboard/openai-api-key",
    };

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
