using System;
using System.Net.Http;
using System.Net.Http.Json;

namespace ZnKeyboard;

/// <summary>
/// The anonymous daily ping behind the numbers on znkeyboard.2amz.com: a random id made on this PC,
/// the platform, the version and how many times text was polished or translated since the last
/// ping. Never any text, key or setting. Off when the user turns it off in Settings.
/// </summary>
public static class UsageStats
{
    private const string Endpoint = "https://znkeyboard.2amz.com/api/ping";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static bool sending;

    /// <summary>One finished polish, translation or reading translation.</summary>
    public static void RecordUse()
    {
        var settings = Settings.Current;
        if (!settings.AnonymousStats) return;
        settings.PendingUses++;
        settings.Save();
    }

    /// <summary>Sends the ping once a day; a failed one is tried again later, uses kept.</summary>
    public static async void PingIfDue()
    {
        var settings = Settings.Current;
        if (!settings.AnonymousStats || sending || DateTime.UtcNow - settings.LastPing < TimeSpan.FromHours(20)) return;
        sending = true;
        try
        {
            settings.InstallId ??= Guid.NewGuid().ToString();
            var uses = settings.PendingUses;
            using var response = await Http.PostAsJsonAsync(Endpoint, new { id = settings.InstallId, platform = "windows", version = Updates.CurrentVersion, uses });
            if (!response.IsSuccessStatusCode) return;
            settings.LastPing = DateTime.UtcNow;
            settings.PendingUses = Math.Max(0, settings.PendingUses - uses);
            settings.Save();
        }
        catch (Exception)
        {
            // Offline or blocked: next time.
        }
        finally
        {
            sending = false;
        }
    }
}
