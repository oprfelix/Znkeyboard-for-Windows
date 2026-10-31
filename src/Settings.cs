using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ZnKeyboard;

/// <summary>Stored as a number: 1 was "OpenAI compatible" before 0.3.0 (see Settings.Migrate).</summary>
public enum Provider { Gemini, DeepSeek }

public enum ShortcutAction { Polish, Translate }

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

    public static Shortcut Default(ShortcutAction action) => action == ShortcutAction.Polish
        ? new Shortcut(Control | Alt, 0x52, "R")
        : new Shortcut(Control | Alt, 0x54, "T");
}

/// <summary>
/// Everything but the API keys, as JSON in %APPDATA%\ZnKeyboard\settings.json. API keys go to
/// Windows Credential Manager, one entry per provider.
/// </summary>
public sealed class Settings
{
    public const string DefaultGeminiModel = "gemini-3.5-flash-lite";
    public const string DeepSeekModel = "deepseek-chat";

    public Provider Provider { get; set; } = Provider.Gemini;
    public string GeminiModel { get; set; } = DefaultGeminiModel;
    /// <summary>Read only to migrate a 0.2 "OpenAI compatible" setup; never written back.</summary>
    [JsonPropertyName("OpenAIBaseUrl"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyOpenAIBaseUrl { get; set; }
    public string TranslateTarget { get; set; } = "EN";
    /// <summary>"system", "zh" or "en".</summary>
    public string UiLanguage { get; set; } = "system";
    /// <summary>The 润色 | AI 翻译为 pill on selected text. On by default.</summary>
    public bool SelectionPill { get; set; } = true;
    public Shortcut? PolishShortcut { get; set; }
    public Shortcut? TranslateShortcut { get; set; }

    public static Settings Current { get; private set; } = Load();

    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZnKeyboard");
    private static readonly string FilePath = Path.Combine(Folder, "settings.json");

    private static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return Migrate(JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings());
        }
        catch (Exception)
        {
            // A damaged file falls back to defaults rather than keeping the app from starting.
        }
        return new Settings();
    }

    /// <summary>
    /// 0.2 offered a free-form OpenAI-compatible provider (stored as 1, now DeepSeek). One pointed at
    /// DeepSeek keeps its key; any other falls back to Gemini.
    /// </summary>
    private static Settings Migrate(Settings settings)
    {
        if (settings.LegacyOpenAIBaseUrl == null) return settings;
        if (settings.Provider == Provider.DeepSeek && !settings.LegacyOpenAIBaseUrl.Contains("api.deepseek.com"))
            settings.Provider = Provider.Gemini;
        Credentials.MoveLegacyKey(settings.Provider == Provider.DeepSeek);
        settings.LegacyOpenAIBaseUrl = null;
        settings.Save();
        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public TranslateLanguage Target => Languages.ByCode(TranslateTarget);

    /// <summary>The model for <paramref name="provider"/>. Gemini falls back to the default unless a Lite model is saved; DeepSeek is fixed.</summary>
    public string ModelFor(Provider provider) => provider == Provider.Gemini
        ? (GeminiModels.IsLite(GeminiModel) ? GeminiModel : DefaultGeminiModel)
        : DeepSeekModel;

    public Shortcut ShortcutFor(ShortcutAction action) =>
        (action == ShortcutAction.Polish ? PolishShortcut : TranslateShortcut) ?? Shortcut.Default(action);

    public void SetShortcut(ShortcutAction action, Shortcut shortcut)
    {
        if (action == ShortcutAction.Polish) PolishShortcut = shortcut; else TranslateShortcut = shortcut;
        Save();
    }

    /// <summary>The client for the selected provider and its key and model.</summary>
    public AIClient CreateClient() => new(Provider, Credentials.Read(Provider) ?? "", ModelFor(Provider));

    // Start with Windows, through the current user's Run key (no admin rights needed).
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool StartsWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("ZnKeyboard") is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue("ZnKeyboard", $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue("ZnKeyboard", throwOnMissingValue: false);
        }
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
    private const string LegacyOpenAITarget = "ZnKeyboard/openai-api-key";

    private static string Target(Provider provider) =>
        provider == Provider.Gemini ? "ZnKeyboard/gemini-api-key" : "ZnKeyboard/deepseek-api-key";

    public static string? Read(Provider provider) => ReadTarget(Target(provider));

    /// <summary>Moves a 0.2 OpenAI-compatible key to DeepSeek (when <paramref name="toDeepSeek"/>), then removes it.</summary>
    public static void MoveLegacyKey(bool toDeepSeek)
    {
        var key = ReadTarget(LegacyOpenAITarget);
        if (key == null) return;
        if (toDeepSeek && string.IsNullOrEmpty(Read(Provider.DeepSeek))) Write(Provider.DeepSeek, key);
        CredDelete(LegacyOpenAITarget, CredTypeGeneric, 0);
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
            CredWrite(ref credential, 0);
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
