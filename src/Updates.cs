using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;
using TextBlock = System.Windows.Controls.TextBlock;

namespace ZnKeyboard;

/// <summary>
/// Updates in place: checks the latest GitHub release (by hand, or once a day), downloads its exe
/// and runs it. The new exe installs itself over this copy (Installer), which it asks to quit, and
/// starts; settings and API keys stay. Also the About box.
/// </summary>
public static class Updates
{
    private const string ReleasesPage = "https://github.com/oprfelix/Znkeyboard-for-Windows/releases/latest";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    // A 30 MB download on a slow line needs longer than an API call.
    private static readonly HttpClient Download = new() { Timeout = TimeSpan.FromMinutes(10) };

    public sealed record Release(string Version, Uri Exe, long Size);

    /// <summary>A newer release found by the last check; the tray menu offers it.</summary>
    public static Release? Available { get; private set; }

    /// <summary>Shows a tray notification (title, text); clicking it installs <see cref="Available"/>.</summary>
    public static Action<string, string> Notify { get; set; } = (_, _) => { };

    public static string CurrentVersion => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private static string DownloadFolder => Path.Combine(Path.GetTempPath(), "ZnKeyboard-update");

    // ---- Checking ----

    /// <summary>"Check for Updates…" from the tray or Settings.</summary>
    public static async void Check()
    {
        var (reached, release) = await LatestReleaseAsync();
        var current = CurrentVersion;
        if (!reached)
        {
            if (await Ask(L.CheckForUpdates.TrimEnd('…'), L.UpdateCheckFailed, L.Open)) OpenReleases();
        }
        else if (release != null && IsNewer(release))
        {
            Available = release;
            if (await Ask(L.UpdateAvailable(release.Version), L.UpdateAvailableNote(current), L.UpdateNow)) Install(release);
        }
        else
        {
            Available = null;
            await Ask(L.CheckForUpdates.TrimEnd('…'), L.UpToDate(current), null);
        }
    }

    private static DispatcherTimer? timer;

    /// <summary>Checks shortly after start and then every few hours, at most once a day; tells about each new version once.</summary>
    public static void StartAutomaticChecks()
    {
        timer = new DispatcherTimer { Interval = TimeSpan.FromHours(3) };
        timer.Tick += (_, _) => AutomaticCheck();
        timer.Start();
        _ = Task.Delay(TimeSpan.FromSeconds(20)).ContinueWith(_ => AutomaticCheck(), TaskScheduler.FromCurrentSynchronizationContext());
        // What an update left behind; the exe in it has finished by now.
        _ = Task.Delay(TimeSpan.FromSeconds(15)).ContinueWith(_ =>
        {
            try { if (Directory.Exists(DownloadFolder)) Directory.Delete(DownloadFolder, recursive: true); } catch (Exception) { }
        });
    }

    private static async void AutomaticCheck()
    {
        var settings = Settings.Current;
        if (!settings.AutomaticUpdateChecks || DateTime.UtcNow - settings.LastUpdateCheck < TimeSpan.FromHours(20)) return;
        var (reached, release) = await LatestReleaseAsync();
        if (!reached) return;
        settings.LastUpdateCheck = DateTime.UtcNow;
        settings.Save();
        if (release == null || !IsNewer(release))
        {
            Available = null;
            return;
        }
        Available = release;
        if (settings.NotifiedUpdate == release.Version) return;
        settings.NotifiedUpdate = release.Version;
        settings.Save();
        DebugLog.Note($"update {release.Version} available");
        Notify(L.UpdateAvailable(release.Version), L.UpdateAvailableBalloon);
    }

    private static bool IsNewer(Release release) =>
        Version.TryParse(release.Version, out var next) && Version.TryParse(CurrentVersion, out var mine) && next > mine;

    /// <summary>Whether GitHub answered, and the latest release with its exe (null when it has none).</summary>
    private static async Task<(bool reached, Release? release)> LatestReleaseAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/oprfelix/Znkeyboard-for-Windows/releases/latest");
            request.Headers.UserAgent.ParseAdd("ZnKeyboard-Windows");
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return (false, null);
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            var version = ((string?)json?["tag_name"])?.TrimStart('v');
            var asset = json?["assets"]?.AsArray().FirstOrDefault(a => ((string?)a?["name"])?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true);
            var link = (string?)asset?["browser_download_url"];
            if (version == null || link == null || !Uri.TryCreate(link, UriKind.Absolute, out var exe) || exe.Host != "github.com") return (true, null);
            return (true, new Release(version, exe, (long?)asset?["size"] ?? 0));
        }
        catch (Exception)
        {
            return (false, null);
        }
    }

    // ---- Installing ----

    private static Window? progressWindow;

    /// <summary>
    /// Downloads <paramref name="release"/>, checks it is the promised ZnKeyboard version and starts it;
    /// it then replaces this copy. On any failure nothing changes and the download page is offered.
    /// </summary>
    public static async void Install(Release release)
    {
        if (progressWindow != null) return;
        var cancel = new CancellationTokenSource();
        var (title, bar) = ShowProgress(release, cancel);
        try
        {
            var path = await DownloadAsync(release, fraction => bar.Value = fraction * 100, cancel.Token);
            Verify(path, release);
            title.Text = L.InstallingUpdate;
            bar.IsIndeterminate = true;
            DebugLog.Note($"update {release.Version} downloaded, starting it");
            // The new copy installs itself, asks this one to quit and starts in its place.
            Process.Start(new ProcessStartInfo(path, Installer.AutoUpdateArg) { UseShellExecute = false, WorkingDirectory = DownloadFolder });
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            CloseProgress();
        }
        catch (Exception e)
        {
            CloseProgress();
            DebugLog.Note($"update failed: {e.GetType().Name}");
            var reason = e is UpdateException u ? u.Message : L.UpdateDownloadFailed;
            if (await Ask(L.UpdateFailed, L.UpdateFailedNote(reason), L.Open)) OpenReleases();
        }
    }

    private sealed class UpdateException(string message) : Exception(message);

    private static async Task<string> DownloadAsync(Release release, Action<double> progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(DownloadFolder);
        var path = Path.Combine(DownloadFolder, $"ZnKeyboard-Windows-{release.Version}.exe");
        using var request = new HttpRequestMessage(HttpMethod.Get, release.Exe);
        request.Headers.UserAgent.ParseAdd("ZnKeyboard-Windows");
        using var response = await Download.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        if (!response.IsSuccessStatusCode) throw new UpdateException(L.UpdateDownloadFailed);
        var total = response.Content.Headers.ContentLength ?? release.Size;
        await using (var source = await response.Content.ReadAsStreamAsync(cancel))
        await using (var target = File.Create(path))
        {
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancel)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancel);
                done += read;
                if (total > 0) progress((double)done / total);
            }
        }
        return path;
    }

    /// <summary>
    /// The whole file, and ZnKeyboard at the version the release names. The exe isn't code signed;
    /// it comes over HTTPS from the project's own GitHub release.
    /// </summary>
    private static void Verify(string path, Release release)
    {
        var size = new FileInfo(path).Length;
        if (release.Size > 0 && size != release.Size) throw new UpdateException(L.UpdateDamaged);
        var info = FileVersionInfo.GetVersionInfo(path);
        var version = $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}";
        if (info.ProductName != "ZnKeyboard for Windows" || version != release.Version) throw new UpdateException(L.UpdateDamaged);
    }

    private static (TextBlock title, System.Windows.Controls.ProgressBar bar) ShowProgress(Release release, CancellationTokenSource cancel)
    {
        var title = new TextBlock { Text = L.DownloadingUpdate(release.Version), FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        var bar = new System.Windows.Controls.ProgressBar { Minimum = 0, Maximum = 100, Height = 4 };
        var stop = new Button { Content = L.Cancel, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        stop.Click += (_, _) => cancel.Cancel();
        var window = new Window
        {
            Title = "ZnKeyboard",
            Width = 380,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Icon = App.IconImage(),
            Content = new StackPanel { Margin = new Thickness(20), Children = { title, bar, stop } },
        };
        window.SetResourceReference(Control.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        window.Closed += (_, _) => { if (progressWindow == window) { progressWindow = null; cancel.Cancel(); } };
        progressWindow = window;
        window.Show();
        window.Activate();
        return (title, bar);
    }

    private static void CloseProgress()
    {
        var window = progressWindow;
        progressWindow = null;
        window?.Close();
    }

    // ---- Dialogs ----

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
}
