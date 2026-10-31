using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Wpf.Ui.Controls;

namespace ZnKeyboard;

/// <summary>
/// "Check for Updates…" from the tray or Settings: compares this version with the latest GitHub
/// release and offers the download page. Also the About box.
/// </summary>
public static class Updates
{
    private const string ReleasesPage = "https://github.com/oprfelix/Znkeyboard-for-Windows/releases/latest";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static string CurrentVersion => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static async void Check()
    {
        var latest = await LatestVersionAsync();
        var current = CurrentVersion;
        if (latest == null)
        {
            if (await Ask(L.CheckForUpdates.TrimEnd('…'), L.UpdateCheckFailed, L.Open)) OpenReleases();
        }
        else if (Version.TryParse(latest, out var next) && Version.TryParse(current, out var mine) && next > mine)
        {
            if (await Ask(L.UpdateAvailable(latest), L.UpdateAvailableNote(current), L.Open)) OpenReleases();
        }
        else
        {
            await Ask(L.CheckForUpdates.TrimEnd('…'), L.UpToDate(current), null);
        }
    }

    public static async void ShowAbout() => await Ask(L.About, L.AboutText(CurrentVersion), null);

    /// <summary>A Fluent message box; true when the primary button (if any) was chosen.</summary>
    private static async Task<bool> Ask(string title, string message, string? primary)
    {
        var box = new MessageBox
        {
            Title = title,
            Content = message,
            CloseButtonText = primary == null ? "OK" : L.Cancel,
        };
        if (primary != null)
        {
            box.PrimaryButtonText = primary;
            box.IsPrimaryButtonEnabled = true;
        }
        return await box.ShowDialogAsync() == MessageBoxResult.Primary;
    }

    private static void OpenReleases() => Process.Start(new ProcessStartInfo(ReleasesPage) { UseShellExecute = true });

    /// <summary>"0.4.0" from the latest release's tag, or null when GitHub can't be reached.</summary>
    private static async Task<string?> LatestVersionAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/oprfelix/Znkeyboard-for-Windows/releases/latest");
            request.Headers.UserAgent.ParseAdd("ZnKeyboard-Windows");
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;
            var tag = (string?)JsonNode.Parse(await response.Content.ReadAsStringAsync())?["tag_name"];
            return tag?.TrimStart('v');
        }
        catch (Exception)
        {
            return null;
        }
    }
}
