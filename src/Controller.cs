using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ZnKeyboard;

/// <summary>
/// One shortcut press: read the selection (or a screenshot's text), ask the AI, show the result,
/// replace or copy on Enter. The card can run the same text again or into another language.
/// </summary>
public sealed class Controller
{
    private readonly ReviewWindow panel = new();
    private CancellationTokenSource? running;
    /// <summary>The text being worked on, kept so ↻ and the language menu don't read it again.</summary>
    private (string text, RewriteMode mode, AIClient client)? last;

    public Action OpenSettings { get; set; } = () => { };

    public bool IsPanelVisible => panel.IsVisible;

    public Controller()
    {
        panel.OnOpenSettings = () => OpenSettings();
        panel.OnRetry = () => { if (last is { } l) Process(l.text, l.mode, l.client); };
    }

    public async void Run(RewriteMode mode)
    {
        running?.Cancel();
        var cancel = new CancellationTokenSource();
        running = cancel;
        last = null;

        // The window whose text is selected; the paste must land there.
        var source = Native.GetForegroundWindow();
        if (Native.IsOwnWindow(source)) source = IntPtr.Zero;

        var client = Settings.Current.CreateClient();
        if (client.ApiKey.Length == 0 || !client.HasValidEndpoint)
        {
            panel.ShowError(client.ApiKey.Length == 0 ? L.NeedKey : L.BadEndpoint, offerSettings: true);
            return;
        }

        // Where the selection ends, asked alongside the read, so the card can open beside it; an app
        // that hasn't answered by then just gets the pointer.
        var where = Task.Run(() => TextBridge.InspectFocusedSelection([]));
        // Read the selection before showing anything: once the panel takes focus, Ctrl+C would copy from it.
        var selection = await TextBridge.SelectedTextAsync();
        panel.Anchor = where.IsCompletedSuccessfully && where.Result.state == TextBridge.SelectionState.Selected ? where.Result.end : null;
        if (cancel.IsCancellationRequested) return;
        if (string.IsNullOrWhiteSpace(selection))
        {
            panel.ShowError(L.NoSelection, offerSettings: false);
            return;
        }

        panel.OnReplace = text => _ = TextBridge.ReplaceSelectionAsync(source, selection, text);
        // Picking another language in the card translates the same selection again and remembers it.
        panel.OnLanguageChange = language =>
        {
            Settings.Current.TranslateTarget = language.Code;
            Settings.Current.Save();
            Process(selection, RewriteMode.Translate(language), client);
        };
        Process(selection, mode, client);
    }

    /// <summary>
    /// Screenshot translation: drag over part of the screen, read its text on this PC, and translate
    /// it to read. Text already in the reading language (Chinese by default) goes to English instead.
    /// </summary>
    /// <summary>Set while the screen is dimmed for dragging; another Ctrl+Alt+E then does nothing.</summary>
    private bool capturing;

    public async void RunScreenshot()
    {
        if (capturing) return;
        running?.Cancel();
        var cancel = new CancellationTokenSource();
        running = cancel;
        last = null;
        panel.Close(clearResult: true);

        // Key and address problems are fixed in Settings, before any dragging.
        var client = Settings.Current.CreateClient();
        if (client.ApiKey.Length == 0 || !client.HasValidEndpoint)
        {
            panel.ShowError(client.ApiKey.Length == 0 ? L.NeedKey : L.BadEndpoint, offerSettings: true);
            return;
        }

        capturing = true;
        System.Drawing.Bitmap? captured;
        try { captured = await ScreenText.SelectRegionAsync(); }
        finally { capturing = false; }
        using var image = captured;
        if (image == null || cancel.IsCancellationRequested) return;
        var target = Settings.Current.ReadTarget;
        var mode = RewriteMode.Read(target);
        panel.ShowLoading(mode);
        string text;
        try
        {
            text = (await ScreenText.RecognizeAsync(image)).Trim();
        }
        catch (Exception e)
        {
            DebugLog.Note($"OCR failed: {e.GetType().Name} {e.Message}");
            if (!cancel.IsCancellationRequested) panel.ShowError(L.OcrFailed, offerSettings: false);
            return;
        }
        if (cancel.IsCancellationRequested) return;
        if (text.Length == 0)
        {
            panel.ShowError(L.NoTextFound, offerSettings: false);
            return;
        }
        if (target.Code is "ZH" or "TW" && ScreenText.IsMostlyChinese(text)) mode = RewriteMode.Read(Languages.ByCode("EN"));

        panel.OnLanguageChange = language =>
        {
            Settings.Current.ScreenshotTarget = language.Code;
            Settings.Current.Save();
            Process(text, RewriteMode.Read(language), client);
        };
        Process(text, mode, client);
    }

    private async void Process(string selection, RewriteMode mode, AIClient client)
    {
        running?.Cancel();
        var cancel = new CancellationTokenSource();
        running = cancel;
        last = (selection, mode, client);
        panel.ShowLoading(mode);
        var (leading, core, trailing) = SplitWhitespace(selection);
        try
        {
            var result = await client.RewriteAsync(core, mode, cancel.Token);
            if (cancel.IsCancellationRequested || !panel.IsVisible) return;
            if (mode.IsRead) panel.ShowResult(result, recognised: selection);
            // Keep the selection's surrounding spaces and line breaks so the paste fits back in.
            else panel.ShowResult(leading + result + trailing);
        }
        catch (RewriteException e)
        {
            if (!cancel.IsCancellationRequested && panel.IsVisible) panel.ShowError(e.Describe(), e.FixableInSettings, canRetry: !e.FixableInSettings);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            if (!cancel.IsCancellationRequested && panel.IsVisible) panel.ShowError(L.NetworkFailed, offerSettings: false, canRetry: true);
        }
    }

    /// <summary>Splits off leading and trailing whitespace, which the model would drop.</summary>
    private static (string leading, string core, string trailing) SplitWhitespace(string text)
    {
        var leading = new string(text.TakeWhile(char.IsWhiteSpace).ToArray());
        var trailing = new string(text.Reverse().TakeWhile(char.IsWhiteSpace).Reverse().ToArray());
        return (leading, text[leading.Length..^trailing.Length], trailing);
    }
}
