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
        // 0.3.0–0.3.1 stored the OpenAI-compatible provider's number (1) for a fixed DeepSeek
        // provider with no address or model, so those load as DeepSeek through the defaults; only
        // the key needs to move back.
        Credentials.MoveDeepSeekKey();
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception)
        {
            // A damaged file falls back to defaults rather than keeping the app from starting.
        }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public TranslateLanguage Target => Languages.ByCode(TranslateTarget);

    /// <summary>The model for <paramref name="provider"/>. Gemini falls back to the default unless a Lite model is saved.</summary>
    public string ModelFor(Provider provider) => provider == Provider.Gemini
        ? (GeminiModels.IsLite(GeminiModel) ? GeminiModel : DefaultGeminiModel)
        : (string.IsNullOrWhiteSpace(OpenAIModel) ? DefaultOpenAIModel : OpenAIModel.Trim());

    public Shortcut ShortcutFor(ShortcutAction action) =>
        (action == ShortcutAction.Polish ? PolishShortcut : TranslateShortcut) ?? Shortcut.Default(action);

    public void SetShortcut(ShortcutAction action, Shortcut shortcut)
    {
        if (action == ShortcutAction.Polish) PolishShortcut = shortcut; else TranslateShortcut = shortcut;
        Save();
    }

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
    private const string LegacyDeepSeekTarget = "ZnKeyboard/deepseek-api-key";

    private static string Target(Provider provider) =>
        provider == Provider.Gemini ? "ZnKeyboard/gemini-api-key" : "ZnKeyboard/openai-api-key";

    public static string? Read(Provider provider) => ReadTarget(Target(provider));

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
